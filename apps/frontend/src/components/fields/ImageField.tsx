/**
 * ImageField — Image display and upload component.
 *
 * React replacement for the monolith's PcFieldImage ViewComponent.
 * Supports image preview, file selection, drag-and-drop upload,
 * presigned-URL upload flow via the File Management service,
 * and direct FormData POST as a fallback.
 */

import React, { useState, useCallback, useRef, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';
import apiClient from '../../api/client';

/* ------------------------------------------------------------------ */
/*  Types                                                              */
/* ------------------------------------------------------------------ */

/**
 * Props for the ImageField component.
 * Extends BaseFieldProps (minus `value` and `onChange`) with
 * image-specific typing and configuration.
 */
export interface ImageFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Image URL, path, or S3 key. `null` when no image is set. */
  value: string | null;
  /** Called when the image value changes (after upload success or removal). */
  onChange?: (value: string | null) => void;
  /** Accepted file MIME types for the file input. Defaults to `"image/*"`. */
  accept?: string;
  /** URL prefix prepended to `value` when constructing the image `src`. */
  srcPrefix?: string;
}

/** Internal upload lifecycle state machine. */
type UploadState = 'idle' | 'uploading' | 'success' | 'error';

/* ------------------------------------------------------------------ */
/*  Inline SVG Icon Components                                         */
/* ------------------------------------------------------------------ */

/** Placeholder icon rendered inside the empty drop-zone. */
function ImagePlaceholderIcon({ className }: { className?: string }): React.JSX.Element {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M2.25 15.75l5.159-5.159a2.25 2.25 0 013.182 0l5.159 5.159m-1.5-1.5l1.409-1.409a2.25 2.25 0 013.182 0l2.909 2.909M3.75 21h16.5A2.25 2.25 0 0022.5 18.75V5.25A2.25 2.25 0 0020.25 3H3.75A2.25 2.25 0 001.5 5.25v13.5A2.25 2.25 0 003.75 21zm3-10.5a1.5 1.5 0 110-3 1.5 1.5 0 010 3z"
      />
    </svg>
  );
}

/** Warning-circle icon rendered when the image fails to load. */
function BrokenImageIcon({ className }: { className?: string }): React.JSX.Element {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M12 9v3.75m9-.75a9 9 0 11-18 0 9 9 0 0118 0zm-9 3.75h.008v.008H12v-.008z"
      />
    </svg>
  );
}

/* ------------------------------------------------------------------ */
/*  ImageField Component                                               */
/* ------------------------------------------------------------------ */

/**
 * ImageField renders an image display or upload control.
 *
 * **Display mode** shows the image with responsive sizing, a broken-image
 * fallback, and an empty-value placeholder when no image is set.
 *
 * **Edit mode** provides a drag-and-drop zone, a hidden file input
 * triggered by a button click, a local preview via `URL.createObjectURL()`,
 * and a two-step upload (presigned URL → S3 PUT, with direct FormData
 * POST as a fallback via apiClient).
 */
function ImageField(props: ImageFieldProps): React.JSX.Element {
  /* ---- Destructure all accessed BaseFieldProps members ---- */
  const {
    name,
    fieldId,
    value,
    onChange,
    accept = 'image/*',
    srcPrefix = '',
    label,
    labelMode: _labelMode,
    mode = 'display',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder,
    description: _description,
    isVisible = true,
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    locale: _locale,
  } = props;

  /* ---- State ---- */
  const [uploadState, setUploadState] = useState<UploadState>('idle');
  const [uploadProgress, setUploadProgress] = useState<number>(0);
  const [isDragOver, setIsDragOver] = useState<boolean>(false);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [imageBroken, setImageBroken] = useState<boolean>(false);
  const [uploadError, setUploadError] = useState<string | null>(null);

  /* ---- Refs ---- */
  const fileInputRef = useRef<HTMLInputElement>(null);

  /* ---- Derived values ---- */
  const resolvedFieldId = fieldId ?? `field-${name}`;

  /**
   * Full image `src` URL, computed from the current value / preview state.
   * Prioritises the local object-URL preview (shown immediately after file
   * selection, before the upload completes).
   */
  const imageSrc = useMemo((): string | null => {
    if (previewUrl) return previewUrl;
    if (!value) return null;
    if (
      value.startsWith('http://') ||
      value.startsWith('https://') ||
      value.startsWith('data:') ||
      value.startsWith('blob:')
    ) {
      return value;
    }
    return `${srcPrefix}${value}`;
  }, [value, srcPrefix, previewUrl]);

  /* ================================================================ */
  /*  Upload logic                                                     */
  /* ================================================================ */

  /**
   * Validates a selected/dropped file, creates a local preview, then
   * attempts upload via presigned-URL flow (GET key → PUT to S3).
   * Falls back to a direct FormData POST when presigned-URL fails.
   */
  const uploadFile = useCallback(
    async (file: File): Promise<void> => {
      if (!file.type.startsWith('image/')) {
        setUploadError('Selected file is not a valid image.');
        return;
      }

      const localPreview = URL.createObjectURL(file);
      setPreviewUrl(localPreview);
      setImageBroken(false);
      setUploadError(null);
      setUploadState('uploading');
      setUploadProgress(0);

      try {
        // Step 1 — request presigned upload URL from the File Management service
        const presignedRes = await apiClient.get('/files/presigned-upload', {
          params: { fileName: file.name, contentType: file.type },
        });

        const { url: presignedUrl, key: fileKey } = presignedRes.data as {
          url: string;
          key: string;
        };

        // Step 2 — PUT the raw file bytes to S3 via the presigned URL
        await apiClient.put(presignedUrl, file, {
          headers: { 'Content-Type': file.type },
          onUploadProgress(progressEvent) {
            if (progressEvent.total) {
              setUploadProgress(
                Math.round((progressEvent.loaded * 100) / progressEvent.total),
              );
            }
          },
        });

        // Upload succeeded
        setUploadState('success');
        URL.revokeObjectURL(localPreview);
        setPreviewUrl(null);
        onChange?.(fileKey);
      } catch {
        // Fallback — direct FormData POST
        try {
          const formData = new FormData();
          formData.append('file', file);

          const uploadRes = await apiClient.post('/files/upload', formData, {
            headers: { 'Content-Type': 'multipart/form-data' },
            onUploadProgress(progressEvent) {
              if (progressEvent.total) {
                setUploadProgress(
                  Math.round((progressEvent.loaded * 100) / progressEvent.total),
                );
              }
            },
          });

          const resultPath = (uploadRes.data as { path: string }).path;
          setUploadState('success');
          URL.revokeObjectURL(localPreview);
          setPreviewUrl(null);
          onChange?.(resultPath);
        } catch (fallbackErr: unknown) {
          setUploadState('error');
          setUploadError(
            fallbackErr instanceof Error
              ? fallbackErr.message
              : 'Upload failed. Please try again.',
          );
          // Keep the local preview so the user can see what they attempted
        }
      }
    },
    [onChange],
  );

  /* ================================================================ */
  /*  Event handlers                                                   */
  /* ================================================================ */

  /** Handles the hidden <input type="file"> change event. */
  const handleInputChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>): void => {
      const file = event.target.files?.[0];
      if (file) {
        void uploadFile(file);
      }
      // Reset so the same file can be re-selected
      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }
    },
    [uploadFile],
  );

  /** Programmatically clicks the hidden file input. */
  const handleSelectClick = useCallback((): void => {
    fileInputRef.current?.click();
  }, []);

  /** Removes the current image and resets all upload state. */
  const handleRemove = useCallback((): void => {
    if (previewUrl) {
      URL.revokeObjectURL(previewUrl);
    }
    setPreviewUrl(null);
    setImageBroken(false);
    setUploadState('idle');
    setUploadProgress(0);
    setUploadError(null);
    onChange?.(null);
  }, [onChange, previewUrl]);

  /* ---- Drag-and-drop handlers ---- */

  const handleDragOver = useCallback(
    (event: React.DragEvent): void => {
      event.preventDefault();
      event.stopPropagation();
      if (!disabled) {
        setIsDragOver(true);
      }
    },
    [disabled],
  );

  const handleDragLeave = useCallback((event: React.DragEvent): void => {
    event.preventDefault();
    event.stopPropagation();
    setIsDragOver(false);
  }, []);

  const handleDrop = useCallback(
    (event: React.DragEvent): void => {
      event.preventDefault();
      event.stopPropagation();
      setIsDragOver(false);
      if (disabled) return;
      const file = event.dataTransfer.files?.[0];
      if (file) {
        void uploadFile(file);
      }
    },
    [disabled, uploadFile],
  );

  /* ---- Image load / error callbacks ---- */

  const handleImageError = useCallback((): void => {
    setImageBroken(true);
  }, []);

  const handleImageLoad = useCallback((): void => {
    setImageBroken(false);
  }, []);

  /* ================================================================ */
  /*  Visibility and Access Control Guards                              */
  /* ================================================================ */

  // Phase 1: Visibility check — render nothing when hidden
  if (!isVisible) {
    return <></>;
  }

  // Phase 2: Access control — forbidden renders lock message
  if (access === 'forbidden') {
    return (
      <div className={className}>
        <div
          className="flex items-center gap-2 rounded border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-400"
          role="status"
          aria-label="Access denied"
        >
          <svg
            className="h-4 w-4 shrink-0"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M10 1a4.5 4.5 0 00-4.5 4.5V9H5a2 2 0 00-2 2v6a2 2 0 002 2h10a2 2 0 002-2v-6a2 2 0 00-2-2h-.5V5.5A4.5 4.5 0 0010 1zm3 8V5.5a3 3 0 10-6 0V9h6z"
              clipRule="evenodd"
            />
          </svg>
          <span>{accessDeniedMessage}</span>
        </div>
      </div>
    );
  }

  // Phase 3: Compute effective disabled and mode from access level
  const effectiveDisabled = disabled || access === 'readonly';
  const effectiveMode = access === 'readonly' ? 'display' : mode;

  /* ================================================================ */
  /*  Render — DISPLAY MODE                                            */
  /* ================================================================ */

  if (effectiveMode === 'display') {
    // No image value
    if (!value && !previewUrl) {
      return (
        <span
          className={`text-sm italic text-gray-500${className ? ` ${className}` : ''}`}
          data-field-name={name}
        >
          {emptyValueMessage}
        </span>
      );
    }

    // Image failed to load — broken-image fallback
    if (imageBroken) {
      return (
        <div
          className={`inline-flex items-center gap-2 rounded-md border border-gray-200 bg-gray-50 px-3 py-2${className ? ` ${className}` : ''}`}
          data-field-name={name}
          role="img"
          aria-label={label ?? name}
        >
          <BrokenImageIcon className="h-5 w-5 shrink-0 text-gray-400" />
          <span className="text-sm text-gray-500">Image unavailable</span>
        </div>
      );
    }

    // Render the image
    return (
      <div
        className={`inline-block${className ? ` ${className}` : ''}`}
        data-field-name={name}
      >
        <img
          src={imageSrc ?? ''}
          alt={label ?? name}
          onError={handleImageError}
          onLoad={handleImageLoad}
          className="max-w-full rounded-md"
          loading="lazy"
          decoding="async"
          width={300}
          height={300}
          style={{ maxWidth: '100%', height: 'auto' }}
        />
      </div>
    );
  }

  /* ================================================================ */
  /*  Render — EDIT MODE                                               */
  /* ================================================================ */

  const hasImage = Boolean(imageSrc) && !imageBroken;
  const isUploading = uploadState === 'uploading';
  const editDisabled = effectiveDisabled || isUploading;

  return (
    <div className={className ?? ''} data-field-name={name}>
      {/* Hidden native file input */}
      <input
        ref={fileInputRef}
        type="file"
        id={resolvedFieldId}
        name={name}
        accept={accept}
        onChange={handleInputChange}
        disabled={editDisabled}
        className="sr-only"
        aria-label={label ?? placeholder ?? `Select image for ${name}`}
        required={required && !value}
        tabIndex={-1}
      />

      {hasImage ? (
        /* --- Image preview with change / remove buttons --- */
        <div className="space-y-2">
          <div className="relative inline-block overflow-hidden rounded-md border border-gray-200">
            <img
              src={imageSrc!}
              alt={label ?? name}
              onError={handleImageError}
              onLoad={handleImageLoad}
              className="block max-w-full"
              style={{ maxWidth: '20rem', height: 'auto' }}
              loading="lazy"
              decoding="async"
            />
          </div>

          {!editDisabled && (
            <div className="flex gap-2">
              <button
                type="button"
                onClick={handleSelectClick}
                disabled={editDisabled}
                className="inline-flex items-center rounded-md bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
              >
                Change
              </button>
              <button
                type="button"
                onClick={handleRemove}
                disabled={editDisabled}
                className="inline-flex items-center rounded-md bg-white px-3 py-1.5 text-sm font-medium text-red-600 shadow-sm ring-1 ring-inset ring-red-300 hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:cursor-not-allowed disabled:opacity-50"
              >
                Remove
              </button>
            </div>
          )}
        </div>
      ) : (
        /* --- Drag-and-drop zone (no image yet) --- */
        <div
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
          className={[
            'flex flex-col items-center justify-center rounded-md border-2 border-dashed px-6 py-8 text-center',
            isDragOver
              ? 'border-blue-400 bg-blue-50'
              : error
                ? 'border-red-300 bg-red-50'
                : 'border-gray-300 bg-gray-50',
            editDisabled
              ? 'cursor-not-allowed opacity-60'
              : 'cursor-pointer hover:border-gray-400',
          ]
            .filter(Boolean)
            .join(' ')}
          role="button"
          tabIndex={editDisabled ? -1 : 0}
          onClick={editDisabled ? undefined : handleSelectClick}
          onKeyDown={(e: React.KeyboardEvent) => {
            if (!editDisabled && (e.key === 'Enter' || e.key === ' ')) {
              e.preventDefault();
              handleSelectClick();
            }
          }}
          aria-label={placeholder ?? `Select or drop an image for ${name}`}
          aria-disabled={editDisabled || undefined}
          aria-invalid={Boolean(error)}
          aria-describedby={error ? `${name}-error` : undefined}
        >
          <ImagePlaceholderIcon className="mx-auto h-10 w-10 text-gray-400" />
          <p className="mt-2 text-sm text-gray-600">
            {isDragOver
              ? 'Drop image here'
              : (placeholder ?? 'Click to select or drag and drop')}
          </p>
          <p className="mt-1 text-xs text-gray-500">
            {accept === 'image/*' ? 'PNG, JPG, GIF, SVG, WebP' : accept}
          </p>
        </div>
      )}

      {/* Upload progress bar */}
      {isUploading && (
        <div className="mt-2" aria-live="polite">
          <div className="flex items-center justify-between text-xs text-gray-600">
            <span>Uploading…</span>
            <span>{uploadProgress}%</span>
          </div>
          <div className="mt-1 h-1.5 w-full overflow-hidden rounded-full bg-gray-200">
            <div
              className="h-full rounded-full bg-blue-500 transition-all duration-300"
              style={{ width: `${uploadProgress}%` }}
              role="progressbar"
              aria-valuenow={uploadProgress}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-label="Upload progress"
            />
          </div>
        </div>
      )}

      {/* Upload error from upload process */}
      {uploadError && (
        <p className="mt-1.5 text-sm text-red-600" role="alert">
          {uploadError}
        </p>
      )}

      {/* Validation error from parent form */}
      {error && (
        <p className="text-sm text-red-600 mt-1" id={`${name}-error`} role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

export default ImageField;
