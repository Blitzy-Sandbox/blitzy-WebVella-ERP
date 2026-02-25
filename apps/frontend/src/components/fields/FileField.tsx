/**
 * FileField — File Upload/Download component.
 *
 * React replacement for PcFieldFile ViewComponent. Provides file upload
 * with drag-and-drop support, S3 presigned URL integration, upload progress
 * tracking, and file download display with type-based icons.
 *
 * In the target architecture, files are managed via the File Management
 * microservice which uses S3 presigned URLs. Upload flow:
 *   GET presigned URL → PUT file to S3 → store S3 key as value.
 * Fallback: POST FormData to fileUploadApi endpoint.
 */

import React, { useState, useCallback, useRef, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import apiClient from '../../api/client';

/* ------------------------------------------------------------------ */
/*  Types                                                              */
/* ------------------------------------------------------------------ */

/**
 * Props for the FileField component.
 * Extends BaseFieldProps (minus value/onChange which are file-specific).
 */
export interface FileFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** File path or URL stored as the field value */
  value: string | null;
  /** Callback when the file value changes (upload complete or file removed) */
  onChange?: (value: string | null) => void;
  /** File type filter applied to the file input, e.g. "image/*,.pdf" */
  accept?: string;
  /** Upload endpoint URL — defaults to File Management service presigned URL flow */
  fileUploadApi?: string;
  /** URL prefix for constructing file download/access URLs */
  srcPrefix?: string;
}

/** Upload lifecycle state */
type UploadStatus = 'idle' | 'uploading' | 'error';

/** Metadata about the current file being uploaded or displayed */
interface FileInfo {
  name: string;
  size: number;
  type: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Default upload endpoint when fileUploadApi prop is not provided */
const DEFAULT_UPLOAD_API = '/file-management/upload';

/** Default URL prefix for file access when srcPrefix prop is not provided */
const DEFAULT_SRC_PREFIX = '/file-management/files';

/** Maximum file size: 100 MB */
const MAX_FILE_SIZE = 100 * 1024 * 1024;

/* ------------------------------------------------------------------ */
/*  Utility helpers                                                    */
/* ------------------------------------------------------------------ */

/**
 * Extract file extension from a path or filename string.
 * Returns lowercase extension without the dot, or empty string.
 */
function getFileExtension(filePath: string): string {
  if (!filePath) return '';
  const lastDot = filePath.lastIndexOf('.');
  if (lastDot === -1 || lastDot === filePath.length - 1) return '';
  return filePath.slice(lastDot + 1).toLowerCase();
}

/**
 * Extract a display-friendly filename from a path or URL.
 * Handles both forward-slash paths and backslash paths.
 */
function getFileName(filePath: string): string {
  if (!filePath) return '';
  const segments = filePath.replace(/\\/g, '/').split('/');
  return segments[segments.length - 1] || filePath;
}

/**
 * Format a byte count into a human-readable string.
 */
function formatFileSize(bytes: number): string {
  if (bytes === 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, exponent);
  return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}

/**
 * Map a file extension to an SVG icon path data.
 * Returns a tuple of [pathData, colorClass] for the icon.
 */
function getFileIconProps(ext: string): { pathData: string; colorClass: string } {
  switch (ext) {
    case 'pdf':
      return {
        pathData:
          'M7 21h10a2 2 0 002-2V9l-5-5H7a2 2 0 00-2 2v13a2 2 0 002 2zm5-16v4a1 1 0 001 1h4',
        colorClass: 'text-red-500',
      };
    case 'doc':
    case 'docx':
      return {
        pathData:
          'M7 21h10a2 2 0 002-2V9l-5-5H7a2 2 0 00-2 2v13a2 2 0 002 2zm5-16v4a1 1 0 001 1h4M9 13h6M9 17h6',
        colorClass: 'text-blue-600',
      };
    case 'xls':
    case 'xlsx':
    case 'csv':
      return {
        pathData:
          'M7 21h10a2 2 0 002-2V9l-5-5H7a2 2 0 00-2 2v13a2 2 0 002 2zm5-16v4a1 1 0 001 1h4M9 13l3 4m0-4l-3 4',
        colorClass: 'text-green-600',
      };
    case 'ppt':
    case 'pptx':
      return {
        pathData:
          'M7 21h10a2 2 0 002-2V9l-5-5H7a2 2 0 00-2 2v13a2 2 0 002 2zm5-16v4a1 1 0 001 1h4',
        colorClass: 'text-orange-500',
      };
    case 'jpg':
    case 'jpeg':
    case 'png':
    case 'gif':
    case 'svg':
    case 'webp':
      return {
        pathData:
          'M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z',
        colorClass: 'text-purple-500',
      };
    case 'zip':
    case 'rar':
    case 'gz':
    case '7z':
    case 'tar':
      return {
        pathData:
          'M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4',
        colorClass: 'text-yellow-600',
      };
    case 'mp3':
    case 'wav':
    case 'ogg':
    case 'flac':
      return {
        pathData:
          'M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2z',
        colorClass: 'text-pink-500',
      };
    case 'mp4':
    case 'avi':
    case 'mov':
    case 'mkv':
    case 'webm':
      return {
        pathData:
          'M15 10l4.553-2.276A1 1 0 0121 8.618v6.764a1 1 0 01-1.447.894L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z',
        colorClass: 'text-indigo-500',
      };
    default:
      return {
        pathData:
          'M7 21h10a2 2 0 002-2V9l-5-5H7a2 2 0 00-2 2v13a2 2 0 002 2zm5-16v4a1 1 0 001 1h4',
        colorClass: 'text-gray-500',
      };
  }
}

/* ------------------------------------------------------------------ */
/*  Sub-components                                                     */
/* ------------------------------------------------------------------ */

/** SVG icon for a file type based on extension */
function FileTypeIcon({ extension, className }: { extension: string; className?: string }) {
  const { pathData, colorClass } = getFileIconProps(extension);
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={`${colorClass} ${className ?? 'h-5 w-5'}`}
      aria-hidden="true"
    >
      <path d={pathData} />
    </svg>
  );
}

/** Upload cloud icon used in the drag-and-drop zone */
function UploadIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      className="mx-auto h-10 w-10 text-gray-400"
      aria-hidden="true"
    >
      <path d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
    </svg>
  );
}

/** Small remove/close icon */
function RemoveIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      className="h-4 w-4"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Download arrow icon */
function DownloadIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      className="h-4 w-4"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M3 17a1 1 0 011-1h12a1 1 0 110 2H4a1 1 0 01-1-1zm3.293-7.707a1 1 0 011.414 0L9 10.586V3a1 1 0 112 0v7.586l1.293-1.293a1 1 0 111.414 1.414l-3 3a1 1 0 01-1.414 0l-3-3a1 1 0 010-1.414z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/* ------------------------------------------------------------------ */
/*  Main Component                                                     */
/* ------------------------------------------------------------------ */

/**
 * FileField component — handles file upload (edit mode) and
 * file download display (display mode).
 *
 * The function accepts BaseFieldProps for compatibility with the
 * FieldRenderer FIELD_COMPONENT_MAP (which types all field components as
 * ComponentType<BaseFieldProps>). File-specific props (accept, fileUploadApi,
 * srcPrefix) are passed through by FieldRenderer and extracted via cast.
 * Consumers can import the FileFieldProps interface for type-safe direct usage.
 */
function FileField(props: BaseFieldProps): React.JSX.Element {
  // Cast to FileFieldProps to access file-specific properties.
  // FieldRenderer passes all extra props from the page/form config.
  const fileProps = props as unknown as FileFieldProps;

  const {
    accept,
    fileUploadApi,
    srcPrefix,
    name,
    mode = 'display',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder,
    description,
    isVisible,
    entityName,
    recordId,
    apiUrl,
    accessDeniedMessage = 'Access denied',
    emptyValueMessage = 'no data',
    label,
    labelMode,
    locale,
    fieldId,
  } = fileProps;

  // Safely narrow value and onChange from the generic BaseFieldProps.value (unknown)
  const value: string | null =
    typeof fileProps.value === 'string' ? fileProps.value : null;
  const onChange: ((v: string | null) => void) | undefined =
    typeof fileProps.onChange === 'function'
      ? (fileProps.onChange as (v: string | null) => void)
      : undefined;

  /* ---- State ---- */
  const [uploadStatus, setUploadStatus] = useState<UploadStatus>('idle');
  const [uploadProgress, setUploadProgress] = useState<number>(0);
  const [uploadError, setUploadError] = useState<string>('');
  const [isDragOver, setIsDragOver] = useState<boolean>(false);
  const [fileInfo, setFileInfo] = useState<FileInfo | null>(null);

  /* ---- Refs ---- */
  const fileInputRef = useRef<HTMLInputElement>(null);

  /* ---- Memoized values ---- */
  const fileDownloadUrl = useMemo<string>(() => {
    if (!value) return '';
    // If value is already a full URL, use it directly
    if (value.startsWith('http://') || value.startsWith('https://')) {
      return value;
    }
    const prefix = srcPrefix ?? DEFAULT_SRC_PREFIX;
    // Ensure no double slashes when joining
    const separator = prefix.endsWith('/') || value.startsWith('/') ? '' : '/';
    return `${prefix}${separator}${value}`;
  }, [value, srcPrefix]);

  const displayFileName = useMemo<string>(() => {
    if (!value) return '';
    return getFileName(value);
  }, [value]);

  const fileExtension = useMemo<string>(() => {
    if (!value) return '';
    return getFileExtension(value);
  }, [value]);

  const inputId = fieldId ?? `field-${name}`;

  /* ---- Upload logic ---- */

  /**
   * Attempt presigned URL upload flow:
   *  1. GET presigned URL from File Management service
   *  2. PUT file data to S3 presigned URL
   *  3. Return S3 object key as the stored value
   *
   * Falls back to FormData POST if presigned URL flow fails or
   * if a custom fileUploadApi is provided.
   */
  const uploadFile = useCallback(
    async (file: File): Promise<void> => {
      if (!onChange) return;

      // Validate file size
      if (file.size > MAX_FILE_SIZE) {
        setUploadStatus('error');
        setUploadError(`File exceeds maximum size of ${formatFileSize(MAX_FILE_SIZE)}`);
        return;
      }

      setUploadStatus('uploading');
      setUploadProgress(0);
      setUploadError('');
      setFileInfo({ name: file.name, size: file.size, type: file.type });

      try {
        // If a custom fileUploadApi is provided, use FormData POST directly
        if (fileUploadApi) {
          const formData = new FormData();
          formData.append('file', file);
          if (entityName) formData.append('entityName', entityName);
          if (recordId) formData.append('recordId', recordId);

          const response = await apiClient.post(fileUploadApi, formData, {
            headers: { 'Content-Type': 'multipart/form-data' },
            onUploadProgress: (progressEvent) => {
              if (progressEvent.total) {
                const percent = Math.round((progressEvent.loaded * 100) / progressEvent.total);
                setUploadProgress(percent);
              }
            },
          });

          const responseData = response.data as {
            success?: boolean;
            object?: string;
            url?: string;
            path?: string;
          };

          const filePath =
            responseData?.object ?? responseData?.url ?? responseData?.path ?? null;

          if (filePath && typeof filePath === 'string') {
            onChange(filePath);
            setUploadStatus('idle');
          } else {
            throw new Error('Upload response did not contain a valid file path');
          }
          return;
        }

        // Default: presigned URL flow via File Management service
        const uploadApiBase = apiUrl ?? DEFAULT_UPLOAD_API;

        // Step 1: Request a presigned upload URL
        const presignedResponse = await apiClient.get(uploadApiBase, {
          params: {
            fileName: file.name,
            contentType: file.type,
            entityName: entityName ?? undefined,
            recordId: recordId ?? undefined,
          },
        });

        const presignedData = presignedResponse.data as {
          success?: boolean;
          object?: {
            uploadUrl?: string;
            objectKey?: string;
          };
          uploadUrl?: string;
          objectKey?: string;
        };

        const uploadUrl =
          presignedData?.object?.uploadUrl ?? presignedData?.uploadUrl ?? null;
        const objectKey =
          presignedData?.object?.objectKey ?? presignedData?.objectKey ?? null;

        if (!uploadUrl || !objectKey) {
          throw new Error('Failed to obtain presigned upload URL');
        }

        // Step 2: PUT file directly to S3 presigned URL
        await apiClient.put(uploadUrl, file, {
          headers: {
            'Content-Type': file.type || 'application/octet-stream',
          },
          onUploadProgress: (progressEvent) => {
            if (progressEvent.total) {
              const percent = Math.round((progressEvent.loaded * 100) / progressEvent.total);
              setUploadProgress(percent);
            }
          },
        });

        // Step 3: Store the S3 object key as the field value
        onChange(objectKey);
        setUploadStatus('idle');
        setUploadProgress(100);
      } catch (err) {
        const errorMessage =
          err instanceof Error ? err.message : 'File upload failed. Please try again.';
        setUploadStatus('error');
        setUploadError(errorMessage);
        setUploadProgress(0);
      }
    },
    [onChange, fileUploadApi, entityName, recordId, apiUrl],
  );

  /* ---- Event handlers ---- */

  /** Trigger the hidden file input programmatically */
  const handleBrowseClick = useCallback(() => {
    fileInputRef.current?.click();
  }, []);

  /** Handle files selected via the native file picker */
  const handleFileChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const files = event.target.files;
      if (files && files.length > 0) {
        void uploadFile(files[0]);
      }
      // Reset input so the same file can be selected again if needed
      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }
    },
    [uploadFile],
  );

  /** Drag-and-drop: dragOver handler — show visual indicator */
  const handleDragOver = useCallback((event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.stopPropagation();
    setIsDragOver(true);
  }, []);

  /** Drag-and-drop: dragLeave handler — hide visual indicator */
  const handleDragLeave = useCallback((event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.stopPropagation();
    setIsDragOver(false);
  }, []);

  /** Drag-and-drop: drop handler — start upload */
  const handleDrop = useCallback(
    (event: React.DragEvent<HTMLDivElement>) => {
      event.preventDefault();
      event.stopPropagation();
      setIsDragOver(false);

      if (disabled || access !== 'full') return;

      const files = event.dataTransfer.files;
      if (files && files.length > 0) {
        // Validate the accept filter manually for drag-and-drop
        const file = files[0];
        if (accept && !isFileAccepted(file, accept)) {
          setUploadStatus('error');
          setUploadError(`File type not accepted. Allowed: ${accept}`);
          return;
        }
        void uploadFile(file);
      }
    },
    [disabled, access, accept, uploadFile],
  );

  /** Remove the current file value */
  const handleRemoveFile = useCallback(() => {
    if (onChange) {
      onChange(null);
      setFileInfo(null);
      setUploadStatus('idle');
      setUploadProgress(0);
      setUploadError('');
    }
  }, [onChange]);

  /* ---- Rendering ---- */

  // Hidden via isVisible flag
  if (isVisible === false) {
    return <></>;
  }

  // Access denied state
  if (access === 'forbidden') {
    return (
      <div className={`text-sm text-gray-500 italic ${className ?? ''}`}>
        {accessDeniedMessage}
      </div>
    );
  }

  // Display mode
  if (mode === 'display') {
    if (!value) {
      return (
        <span className={`text-sm text-gray-400 italic ${className ?? ''}`}>
          {emptyValueMessage}
        </span>
      );
    }

    return (
      <div className={`flex items-center gap-2 ${className ?? ''}`}>
        <FileTypeIcon extension={fileExtension} className="h-5 w-5 flex-shrink-0" />
        <a
          href={fileDownloadUrl}
          target="_blank"
          rel="noopener noreferrer"
          className="text-sm text-blue-600 underline-offset-2 hover:text-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          download={displayFileName}
          aria-label={`Download ${displayFileName}`}
        >
          {displayFileName}
        </a>
        <DownloadIcon />
      </div>
    );
  }

  // Edit mode
  const isReadonly = access === 'readonly' || disabled;

  return (
    <div className={`flex flex-col gap-2 ${className ?? ''}`}>
      {/* Hidden file input */}
      <input
        ref={fileInputRef}
        type="file"
        id={inputId}
        name={name}
        accept={accept}
        disabled={isReadonly}
        onChange={handleFileChange}
        className="sr-only"
        aria-label={label ?? placeholder ?? 'Choose file'}
        aria-required={required}
        aria-invalid={Boolean(error)}
        aria-describedby={
          [
            error ? `${name}-error` : undefined,
            description ? `${name}-description` : undefined,
          ]
            .filter(Boolean)
            .join(' ') || undefined
        }
        tabIndex={-1}
      />

      {/* Current file display (when a file is already selected/uploaded) */}
      {value && uploadStatus !== 'uploading' && (
        <div
          className="flex items-center gap-3 rounded-md border border-gray-200 bg-gray-50 px-3 py-2"
          role="status"
          aria-label={`Current file: ${displayFileName}`}
        >
          <FileTypeIcon extension={fileExtension} className="h-6 w-6 flex-shrink-0" />
          <div className="min-w-0 flex-1">
            <p className="truncate text-sm font-medium text-gray-700">{displayFileName}</p>
            {fileInfo && (
              <p className="text-xs text-gray-500">{formatFileSize(fileInfo.size)}</p>
            )}
          </div>
          {!isReadonly && (
            <button
              type="button"
              onClick={handleRemoveFile}
              className="flex-shrink-0 rounded p-1 text-gray-400 hover:bg-gray-200 hover:text-gray-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              aria-label={`Remove file ${displayFileName}`}
            >
              <RemoveIcon />
            </button>
          )}
        </div>
      )}

      {/* Upload progress bar */}
      {uploadStatus === 'uploading' && (
        <div className="space-y-1" role="status" aria-label="Uploading file">
          <div className="flex items-center justify-between text-xs text-gray-600">
            <span className="truncate">
              {fileInfo ? fileInfo.name : 'Uploading...'}
            </span>
            <span className="flex-shrink-0 font-medium">{uploadProgress}%</span>
          </div>
          <div
            className="h-2 w-full overflow-hidden rounded-full bg-gray-200"
            role="progressbar"
            aria-valuenow={uploadProgress}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-label={`Upload progress: ${uploadProgress}%`}
          >
            <div
              className="h-full rounded-full bg-blue-500 transition-all duration-200 ease-out"
              style={{ width: `${uploadProgress}%` }}
            />
          </div>
        </div>
      )}

      {/* Drag-and-drop zone (shown when no file or after removing) */}
      {(!value || uploadStatus === 'error') && uploadStatus !== 'uploading' && (
        <div
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
          className={[
            'flex flex-col items-center justify-center rounded-lg border-2 border-dashed px-6 py-8 text-center transition-colors',
            isDragOver
              ? 'border-blue-400 bg-blue-50'
              : 'border-gray-300 bg-white hover:border-gray-400',
            isReadonly ? 'cursor-not-allowed opacity-60' : 'cursor-pointer',
            error ? 'border-red-300' : '',
          ]
            .filter(Boolean)
            .join(' ')}
          role="button"
          tabIndex={isReadonly ? -1 : 0}
          onClick={isReadonly ? undefined : handleBrowseClick}
          onKeyDown={
            isReadonly
              ? undefined
              : (e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    handleBrowseClick();
                  }
                }
          }
          aria-label={placeholder ?? 'Drag and drop a file here, or click to browse'}
          aria-disabled={isReadonly}
        >
          <UploadIcon />
          <p className="mt-2 text-sm text-gray-600">
            <span className="font-semibold text-blue-600">Click to upload</span>
            {' or drag and drop'}
          </p>
          {accept && (
            <p className="mt-1 text-xs text-gray-500">
              Accepted types: {accept}
            </p>
          )}
        </div>
      )}

      {/* Error message from upload failure */}
      {uploadStatus === 'error' && uploadError && (
        <div
          className="flex items-start gap-2 rounded-md bg-red-50 px-3 py-2 text-sm text-red-700"
          role="alert"
        >
          <svg
            viewBox="0 0 20 20"
            fill="currentColor"
            className="mt-0.5 h-4 w-4 flex-shrink-0 text-red-500"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z"
              clipRule="evenodd"
            />
          </svg>
          <span>{uploadError}</span>
        </div>
      )}

      {/* Field-level error from parent form validation */}
      {error && (
        <p id={`${name}-error`} className="text-sm text-red-600" role="alert">
          {error}
        </p>
      )}

      {/* Description text */}
      {description && (
        <p id={`${name}-description`} className="text-xs text-gray-500">
          {description}
        </p>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Helpers (private)                                                   */
/* ------------------------------------------------------------------ */

/**
 * Validate whether a File matches an accept string.
 * Supports patterns like "image/*", ".pdf", "application/json".
 */
function isFileAccepted(file: File, acceptString: string): boolean {
  if (!acceptString) return true;

  const acceptedTypes = acceptString
    .split(',')
    .map((t) => t.trim().toLowerCase());

  return acceptedTypes.some((accepted) => {
    // Extension check: ".pdf", ".docx"
    if (accepted.startsWith('.')) {
      return file.name.toLowerCase().endsWith(accepted);
    }
    // Wildcard MIME type: "image/*"
    if (accepted.endsWith('/*')) {
      const mimePrefix = accepted.slice(0, -2);
      return file.type.toLowerCase().startsWith(mimePrefix);
    }
    // Exact MIME type: "application/pdf"
    return file.type.toLowerCase() === accepted;
  });
}

export default FileField;
