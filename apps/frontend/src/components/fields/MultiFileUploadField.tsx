/**
 * MultiFileUploadField — Multi-file upload component with drag-and-drop,
 * per-file progress tracking, configurable metadata property name mapping,
 * and file list management.
 *
 * Replaces the monolith's PcFieldMultiFileUpload ViewComponent and
 * WvFieldUserFileMultiple TagHelper. Uploads files via FormData POST
 * to a configurable API endpoint using the centralized apiClient.
 */
import React, { useState, useCallback, useRef, useMemo } from 'react';

import type { BaseFieldProps } from './FieldRenderer';
import apiClient from '../../api/client';

/* ------------------------------------------------------------------ */
/*  Exported Interfaces                                               */
/* ------------------------------------------------------------------ */

/**
 * Metadata describing a single uploaded file.
 * Property names are canonical — the component maps configurable
 * prop-name overrides onto these keys when parsing the value prop.
 */
export interface FileMetadata {
  /** Relative or absolute path/key identifying the file */
  path: string;
  /** Human-readable file name (e.g. "report.pdf") */
  name: string;
  /** File size in bytes */
  size: number;
  /** Optional CSS icon class (e.g. "fas fa-file-pdf") */
  icon?: string;
  /** Optional ISO-8601 timestamp of when the file was uploaded */
  timestamp?: string;
  /** Optional author/uploader identifier */
  author?: string;
}

/**
 * Props for MultiFileUploadField.
 * Extends BaseFieldProps (minus value/onChange which are overridden
 * with file-metadata-specific types).
 */
export interface MultiFileUploadFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Array of file metadata, a JSON-encoded string, or null */
  value: FileMetadata[] | string | null;
  /** Called whenever the canonical file list changes */
  onChange?: (value: FileMetadata[]) => void;
  /** MIME-type filter for the file picker (e.g. "image/*,.pdf") */
  accept?: string;
  /** Upload endpoint (default "/fs/upload-file-multiple") */
  fileUploadApi?: string;
  /** URL prefix prepended to file paths for download links */
  srcPrefix?: string;
  /** Property key used for `path` in raw metadata objects (default "path") */
  pathPropName?: string;
  /** Property key used for `size` in raw metadata objects (default "size") */
  sizePropName?: string;
  /** Property key used for `name` in raw metadata objects (default "name") */
  namePropName?: string;
  /** Property key used for `icon` in raw metadata objects (default "icon") */
  iconPropName?: string;
  /** Property key used for `timestamp` (default "timestamp") */
  timestampPropName?: string;
  /** Property key used for `author` (default "author") */
  authorPropName?: string;
}

/* ------------------------------------------------------------------ */
/*  Internal Types                                                    */
/* ------------------------------------------------------------------ */

/** Tracks per-file upload progress */
interface UploadState {
  /** 0-100 progress percentage */
  progress: number;
  /** Error message if upload failed */
  error?: string;
  /** Whether the upload is actively in flight */
  uploading: boolean;
}

/* ------------------------------------------------------------------ */
/*  Helper Utilities                                                  */
/* ------------------------------------------------------------------ */

/**
 * Format a byte count into a human-readable string.
 * Matches the monolith's KB → MB → GB formatting thresholds.
 */
function formatFileSize(bytes: number): string {
  if (bytes <= 0) return '0 B';
  const kb = bytes / 1024;
  if (kb < 1) return `${bytes} B`;
  if (kb < 1024) return `${kb.toFixed(1)} KB`;
  const mb = kb / 1024;
  if (mb < 1024) return `${mb.toFixed(1)} MB`;
  const gb = mb / 1024;
  return `${gb.toFixed(2)} GB`;
}

/** Return a CSS icon class derived from file extension */
function getFileIconClass(fileName: string): string {
  const ext = fileName.split('.').pop()?.toLowerCase() ?? '';
  const iconMap: Record<string, string> = {
    pdf: 'fas fa-file-pdf',
    doc: 'fas fa-file-word',
    docx: 'fas fa-file-word',
    xls: 'fas fa-file-excel',
    xlsx: 'fas fa-file-excel',
    csv: 'fas fa-file-csv',
    ppt: 'fas fa-file-powerpoint',
    pptx: 'fas fa-file-powerpoint',
    zip: 'fas fa-file-archive',
    rar: 'fas fa-file-archive',
    '7z': 'fas fa-file-archive',
    tar: 'fas fa-file-archive',
    gz: 'fas fa-file-archive',
    png: 'fas fa-file-image',
    jpg: 'fas fa-file-image',
    jpeg: 'fas fa-file-image',
    gif: 'fas fa-file-image',
    svg: 'fas fa-file-image',
    webp: 'fas fa-file-image',
    mp3: 'fas fa-file-audio',
    wav: 'fas fa-file-audio',
    mp4: 'fas fa-file-video',
    avi: 'fas fa-file-video',
    mov: 'fas fa-file-video',
    txt: 'fas fa-file-alt',
    json: 'fas fa-file-code',
    xml: 'fas fa-file-code',
    html: 'fas fa-file-code',
    css: 'fas fa-file-code',
    js: 'fas fa-file-code',
    ts: 'fas fa-file-code',
  };
  return iconMap[ext] ?? 'fas fa-file';
}

/** Generate a short random ID for upload tracking */
function generateUploadId(): string {
  return `upload_${Date.now()}_${Math.random().toString(36).slice(2, 9)}`;
}

/* ------------------------------------------------------------------ */
/*  Inline SVG Icons                                                  */
/* ------------------------------------------------------------------ */

/** Upload / cloud-upload icon used in the drop-zone */
function UploadIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block h-8 w-8"
      aria-hidden="true"
    >
      <path d="M12 16V4m0 0l-4 4m4-4l4 4" />
      <path d="M2 17l.621 2.485A2 2 0 004.561 21h14.878a2 2 0 001.94-1.515L22 17" />
    </svg>
  );
}

/** Trash / remove icon used on the per-file remove button */
function RemoveIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.52.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/** Download icon for display-mode links */
function DownloadIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <path d="M10.75 2.75a.75.75 0 00-1.5 0v8.614L6.295 8.235a.75.75 0 10-1.09 1.03l4.25 4.5a.75.75 0 001.09 0l4.25-4.5a.75.75 0 00-1.09-1.03l-2.955 3.129V2.75z" />
      <path d="M3.5 12.75a.75.75 0 00-1.5 0v2.5A2.75 2.75 0 004.75 18h10.5A2.75 2.75 0 0018 15.25v-2.5a.75.75 0 00-1.5 0v2.5c0 .69-.56 1.25-1.25 1.25H4.75c-.69 0-1.25-.56-1.25-1.25v-2.5z" />
    </svg>
  );
}

/* ------------------------------------------------------------------ */
/*  Component Implementation                                          */
/* ------------------------------------------------------------------ */

const MultiFileUploadField: React.FC<MultiFileUploadFieldProps> = ({
  /* BaseFieldProps members_accessed */
  name,
  label,
  labelMode = 'stacked',
  mode = 'edit',
  access = 'full',
  required = false,
  disabled = false,
  error,
  className,
  placeholder,
  description,
  isVisible = true,
  emptyValueMessage = 'No files',
  accessDeniedMessage = 'Access denied',
  locale,

  /* MultiFileUploadField-specific props */
  value,
  onChange,
  accept,
  fileUploadApi = '/fs/upload-file-multiple',
  srcPrefix = '/fs',
  pathPropName = 'path',
  sizePropName = 'size',
  namePropName = 'name',
  iconPropName = 'icon',
  timestampPropName = 'timestamp',
  authorPropName = 'author',
}) => {
  /* -------------------------------------------------------------- */
  /*  Visibility & Access Guards                                    */
  /* -------------------------------------------------------------- */
  if (!isVisible) return null;
  if (access === 'forbidden') {
    return (
      <div className="text-sm text-gray-500 italic" role="alert">
        {accessDeniedMessage}
      </div>
    );
  }

  /* -------------------------------------------------------------- */
  /*  Parse value → FileMetadata[]                                  */
  /* -------------------------------------------------------------- */
  const parsedFiles: FileMetadata[] = useMemo(() => {
    if (value == null) return [];

    let raw: unknown[];
    if (typeof value === 'string') {
      if (value.trim() === '') return [];
      try {
        const parsed = JSON.parse(value);
        raw = Array.isArray(parsed) ? parsed : [parsed];
      } catch {
        return [];
      }
    } else if (Array.isArray(value)) {
      raw = value;
    } else {
      return [];
    }

    return raw
      .filter((item): item is Record<string, unknown> =>
        item != null && typeof item === 'object',
      )
      .map((item) => ({
        path: String(item[pathPropName] ?? ''),
        name:
          String(item[namePropName] ?? '') ||
          String(item[pathPropName] ?? '')
            .split('/')
            .pop() ||
          'unknown',
        size: Number(item[sizePropName]) || 0,
        icon: item[iconPropName] != null
          ? String(item[iconPropName])
          : undefined,
        timestamp: item[timestampPropName] != null
          ? String(item[timestampPropName])
          : undefined,
        author: item[authorPropName] != null
          ? String(item[authorPropName])
          : undefined,
      }));
  }, [
    value,
    pathPropName,
    sizePropName,
    namePropName,
    iconPropName,
    timestampPropName,
    authorPropName,
  ]);

  /* -------------------------------------------------------------- */
  /*  State                                                         */
  /* -------------------------------------------------------------- */
  const [uploadStates, setUploadStates] = useState<
    Record<string, UploadState>
  >({});
  const [isDragOver, setIsDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  /** Whether the field is effectively read-only */
  const isReadOnly = access === 'readonly' || disabled || mode === 'display';

  /* -------------------------------------------------------------- */
  /*  Upload Logic                                                  */
  /* -------------------------------------------------------------- */

  /**
   * Upload a single File object and return the resulting FileMetadata.
   * Tracks upload progress via the uploadStates map.
   */
  const uploadSingleFile = useCallback(
    async (file: File, trackingId: string): Promise<FileMetadata | null> => {
      const formData = new FormData();
      formData.append('file', file);

      setUploadStates((prev) => ({
        ...prev,
        [trackingId]: { progress: 0, uploading: true },
      }));

      try {
        const response = await apiClient.post(fileUploadApi, formData, {
          headers: { 'Content-Type': 'multipart/form-data' },
          onUploadProgress: (progressEvent) => {
            const total = progressEvent.total ?? file.size;
            const percent =
              total > 0
                ? Math.round((progressEvent.loaded * 100) / total)
                : 0;
            setUploadStates((prev) => ({
              ...prev,
              [trackingId]: { progress: percent, uploading: true },
            }));
          },
        });

        setUploadStates((prev) => ({
          ...prev,
          [trackingId]: { progress: 100, uploading: false },
        }));

        /* Extract metadata from response – the API may return the file
           metadata at the top level or nested under `object` / `data`. */
        const data =
          response?.data?.object ?? response?.data?.data ?? response?.data ?? {};

        return {
          path: String(data[pathPropName] ?? data.path ?? ''),
          name:
            String(data[namePropName] ?? data.name ?? '') || file.name,
          size:
            Number(data[sizePropName] ?? data.size) || file.size,
          icon:
            data[iconPropName] != null
              ? String(data[iconPropName])
              : getFileIconClass(file.name),
          timestamp:
            data[timestampPropName] != null
              ? String(data[timestampPropName])
              : new Date().toISOString(),
          author:
            data[authorPropName] != null
              ? String(data[authorPropName])
              : undefined,
        };
      } catch (err: unknown) {
        const message =
          err instanceof Error ? err.message : 'Upload failed';
        setUploadStates((prev) => ({
          ...prev,
          [trackingId]: { progress: 0, uploading: false, error: message },
        }));
        return null;
      }
    },
    [
      fileUploadApi,
      pathPropName,
      sizePropName,
      namePropName,
      iconPropName,
      timestampPropName,
      authorPropName,
    ],
  );

  /**
   * Process an array of selected File objects:
   * upload each one, accumulate results, and call onChange.
   */
  const processFiles = useCallback(
    async (fileList: FileList | File[]) => {
      const filesToUpload = Array.from(fileList);
      if (filesToUpload.length === 0) return;

      const newFiles: FileMetadata[] = [];

      /* Upload all files concurrently */
      const uploadPromises = filesToUpload.map(async (file) => {
        const trackingId = generateUploadId();
        const result = await uploadSingleFile(file, trackingId);

        /* Clean up tracking state after a short delay */
        setTimeout(() => {
          setUploadStates((prev) => {
            const next = { ...prev };
            delete next[trackingId];
            return next;
          });
        }, 2000);

        if (result) newFiles.push(result);
      });

      await Promise.all(uploadPromises);

      if (newFiles.length > 0 && onChange) {
        onChange([...parsedFiles, ...newFiles]);
      }
    },
    [parsedFiles, onChange, uploadSingleFile],
  );

  /* -------------------------------------------------------------- */
  /*  Event Handlers                                                */
  /* -------------------------------------------------------------- */

  /** Open the native file picker */
  const handleBrowseClick = useCallback(() => {
    fileInputRef.current?.click();
  }, []);

  /** Handle files selected via the native file picker */
  const handleFileInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files;
      if (files && files.length > 0) {
        processFiles(files);
      }
      /* Reset input so the same file can be re-selected */
      if (fileInputRef.current) {
        fileInputRef.current.value = '';
      }
    },
    [processFiles],
  );

  /** Remove a file by index and call onChange */
  const handleRemoveFile = useCallback(
    (index: number) => {
      const updated = parsedFiles.filter((_, i) => i !== index);
      onChange?.(updated);
    },
    [parsedFiles, onChange],
  );

  /* Drag-and-drop handlers */
  const handleDragOver = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      e.stopPropagation();
      if (!isReadOnly) setIsDragOver(true);
    },
    [isReadOnly],
  );

  const handleDragLeave = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      e.stopPropagation();
      setIsDragOver(false);
    },
    [],
  );

  const handleDrop = useCallback(
    (e: React.DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      e.stopPropagation();
      setIsDragOver(false);
      if (isReadOnly) return;

      const files = e.dataTransfer.files;
      if (files && files.length > 0) {
        processFiles(files);
      }
    },
    [isReadOnly, processFiles],
  );

  /* -------------------------------------------------------------- */
  /*  Active upload list (for progress indicators)                  */
  /* -------------------------------------------------------------- */
  const activeUploads = useMemo(
    () =>
      Object.entries(uploadStates).filter(
        ([, state]) => state.uploading || state.error,
      ),
    [uploadStates],
  );

  /* -------------------------------------------------------------- */
  /*  Build file download URL                                       */
  /* -------------------------------------------------------------- */
  const buildFileUrl = useCallback(
    (filePath: string): string => {
      if (filePath.startsWith('http://') || filePath.startsWith('https://')) {
        return filePath;
      }
      const prefix = srcPrefix.endsWith('/') ? srcPrefix.slice(0, -1) : srcPrefix;
      const cleanPath = filePath.startsWith('/') ? filePath : `/${filePath}`;
      return `${prefix}${cleanPath}`;
    },
    [srcPrefix],
  );

  /* -------------------------------------------------------------- */
  /*  Render: File List Table                                       */
  /* -------------------------------------------------------------- */
  const renderFileTable = (isEditMode: boolean) => {
    if (parsedFiles.length === 0 && activeUploads.length === 0) {
      return (
        <div className="py-4 text-center text-sm text-gray-500 italic">
          {emptyValueMessage}
        </div>
      );
    }

    return (
      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200 text-sm">
          <thead className="bg-gray-50">
            <tr>
              <th
                scope="col"
                className="px-3 py-2 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
              >
                File
              </th>
              <th
                scope="col"
                className="px-3 py-2 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
              >
                Size
              </th>
              <th
                scope="col"
                className="px-3 py-2 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
              >
                Date
              </th>
              {isEditMode && (
                <th
                  scope="col"
                  className="px-3 py-2 text-end text-xs font-medium uppercase tracking-wider text-gray-500"
                >
                  Actions
                </th>
              )}
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100 bg-white">
            {parsedFiles.map((file, index) => {
              const iconClass = file.icon || getFileIconClass(file.name);
              const fileUrl = buildFileUrl(file.path);
              const formattedDate = file.timestamp
                ? new Intl.DateTimeFormat(locale ?? undefined, {
                    dateStyle: 'medium',
                    timeStyle: 'short',
                  }).format(new Date(file.timestamp))
                : '';

              return (
                <tr key={`${file.path}-${index}`} className="hover:bg-gray-50">
                  <td className="whitespace-nowrap px-3 py-2">
                    <div className="flex items-center gap-2">
                      <i
                        className={`${iconClass} text-gray-400`}
                        aria-hidden="true"
                      />
                      {isEditMode ? (
                        <span
                          className="max-w-xs truncate text-gray-900"
                          title={file.name}
                        >
                          {file.name}
                        </span>
                      ) : (
                        <a
                          href={fileUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex max-w-xs items-center gap-1 truncate text-blue-600 hover:text-blue-800 hover:underline"
                          title={file.name}
                        >
                          {file.name}
                          <DownloadIcon />
                        </a>
                      )}
                    </div>
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-gray-500">
                    {formatFileSize(file.size)}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-gray-500">
                    {formattedDate}
                  </td>
                  {isEditMode && (
                    <td className="whitespace-nowrap px-3 py-2 text-end">
                      <button
                        type="button"
                        onClick={() => handleRemoveFile(index)}
                        disabled={disabled}
                        className="inline-flex items-center rounded p-1 text-red-500 hover:bg-red-50 hover:text-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:pointer-events-none disabled:opacity-50"
                        aria-label={`Remove ${file.name}`}
                      >
                        <RemoveIcon />
                      </button>
                    </td>
                  )}
                </tr>
              );
            })}

            {/* In-progress uploads */}
            {activeUploads.map(([id, state]) => (
              <tr key={id} className="bg-blue-50">
                <td colSpan={isEditMode ? 4 : 3} className="px-3 py-2">
                  <div className="flex flex-col gap-1">
                    <span className="text-xs text-gray-600">
                      {state.error
                        ? `Upload failed: ${state.error}`
                        : `Uploading… ${state.progress}%`}
                    </span>
                    <div className="h-1.5 w-full overflow-hidden rounded-full bg-gray-200">
                      <div
                        className={`h-full rounded-full transition-all duration-200 ${
                          state.error ? 'bg-red-500' : 'bg-blue-500'
                        }`}
                        style={{ width: `${state.progress}%` }}
                        role="progressbar"
                        aria-valuenow={state.progress}
                        aria-valuemin={0}
                        aria-valuemax={100}
                      />
                    </div>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  };

  /* -------------------------------------------------------------- */
  /*  Render: Drag-and-Drop Zone (edit mode only)                   */
  /* -------------------------------------------------------------- */
  const renderDropZone = () => (
    <div
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
      className={[
        'relative flex flex-col items-center justify-center rounded-lg border-2 border-dashed p-6 text-center transition-colors',
        isDragOver
          ? 'border-blue-400 bg-blue-50'
          : 'border-gray-300 bg-gray-50 hover:border-gray-400',
        disabled ? 'pointer-events-none opacity-50' : 'cursor-pointer',
      ].join(' ')}
      role="button"
      tabIndex={disabled ? -1 : 0}
      aria-label="Drag and drop files here or click to browse"
      onClick={handleBrowseClick}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          handleBrowseClick();
        }
      }}
    >
      <div className="text-gray-400">
        <UploadIcon />
      </div>
      <p className="mt-2 text-sm text-gray-600">
        {placeholder || 'Drag and drop files here, or click to browse'}
      </p>
      {accept && (
        <p className="mt-1 text-xs text-gray-400">
          Accepted formats: {accept}
        </p>
      )}
    </div>
  );

  /* -------------------------------------------------------------- */
  /*  Render: Hidden File Input                                     */
  /* -------------------------------------------------------------- */
  const renderHiddenInput = () => (
    <input
      ref={fileInputRef}
      type="file"
      multiple
      accept={accept}
      onChange={handleFileInputChange}
      className="sr-only"
      tabIndex={-1}
      aria-hidden="true"
      name={name}
    />
  );

  /* -------------------------------------------------------------- */
  /*  Render: Label                                                 */
  /* -------------------------------------------------------------- */
  const renderLabel = () => {
    if (!label || labelMode === 'hidden') return null;
    return (
      <label className="block text-sm font-medium text-gray-700">
        {label}
        {required && (
          <span className="ml-1 text-red-500" aria-hidden="true">
            *
          </span>
        )}
      </label>
    );
  };

  /* -------------------------------------------------------------- */
  /*  Layout Computation                                            */
  /* -------------------------------------------------------------- */
  const isEditMode = mode === 'edit' && access === 'full' && !disabled;

  const wrapperClasses = useMemo(() => {
    const base = 'w-full';
    if (labelMode === 'horizontal') {
      return `${base} grid grid-cols-12 gap-2 items-start`;
    }
    if (labelMode === 'inline') {
      return `${base} flex items-start gap-2`;
    }
    return `${base} flex flex-col gap-1`;
  }, [labelMode]);

  const labelClasses = useMemo(() => {
    if (labelMode === 'horizontal') return 'col-span-3';
    return '';
  }, [labelMode]);

  const fieldClasses = useMemo(() => {
    if (labelMode === 'horizontal') return 'col-span-9';
    return '';
  }, [labelMode]);

  /* -------------------------------------------------------------- */
  /*  Main Render                                                   */
  /* -------------------------------------------------------------- */
  return (
    <div className={`${wrapperClasses} ${className ?? ''}`.trim()}>
      {label && labelMode !== 'hidden' && (
        <div className={labelClasses}>{renderLabel()}</div>
      )}

      <div className={fieldClasses}>
        {/* File list table */}
        <div
          className={`overflow-hidden rounded-lg border ${
            error ? 'border-red-300' : 'border-gray-200'
          }`}
        >
          {renderFileTable(isEditMode)}
        </div>

        {/* Drop zone and browse button — edit mode only */}
        {isEditMode && (
          <div className="mt-3">
            {renderDropZone()}
            {renderHiddenInput()}
          </div>
        )}

        {/* Description */}
        {description && (
          <p className="mt-1 text-sm text-gray-500">{description}</p>
        )}

        {/* Error message */}
        {error && (
          <p className="mt-1 text-sm text-red-600" role="alert">
            {error}
          </p>
        )}
      </div>
    </div>
  );
};

export default MultiFileUploadField;
