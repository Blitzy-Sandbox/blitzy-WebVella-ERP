/**
 * @fileoverview File detail view page component with download capability.
 *
 * Replaces the monolith's DbFile.cs metadata model (Id, ObjectId, FilePath,
 * CreatedBy, CreatedOn, LastModifiedBy, LastModificationDate) and the
 * DbFile.GetBytes() / DbFile.GetContentStream() content retrieval logic
 * (which supported 3 storage backends: cloud blob, filesystem, PostgreSQL
 * Large Objects) with a File Management microservice API-backed detail view
 * using S3 presigned download URLs.
 *
 * Features:
 *  - File metadata display (filename, type, size, path, contentType,
 *    created/modified timestamps, creator/modifier, dimensions)
 *  - Image preview for image-type files using presigned download URL
 *  - File type icon for non-image files
 *  - Inline editing for alt text and caption via useUpdateFileMetadata()
 *  - Download via presigned S3 URL (opened in new tab)
 *  - File deletion with confirmation dialog
 *  - Breadcrumb navigation back to /files listing
 *  - Loading skeleton, error, and not-found states
 *
 * @module FileDetails
 */

import { useState } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  useFile,
  useFileDownloadUrl,
  useDeleteFile,
  useUpdateFileMetadata,
} from '../../hooks/useFiles';

// ---------------------------------------------------------------------------
// Helper Components
// ---------------------------------------------------------------------------

/**
 * Renders a labeled metadata key-value pair in a definition list format.
 * Used in the file metadata grid to display properties like filename,
 * type, size, etc.
 */
function MetadataRow({
  label,
  value,
}: {
  /** The metadata field label (e.g. "Filename", "Size"). */
  label: string;
  /** The metadata field value to display. */
  value: string;
}) {
  return (
    <div className="overflow-hidden">
      <dt className="text-sm text-gray-500">{label}</dt>
      <dd className="mt-0.5 text-sm font-medium text-gray-900 overflow-hidden text-ellipsis whitespace-nowrap">
        {value}
      </dd>
    </div>
  );
}

/**
 * Inline-editable text field with save/cancel controls.
 * Manages its own editing state via useState. Used for alt text
 * and caption metadata fields that can be updated in place.
 */
function EditableField({
  label,
  value,
  onSave,
  isSaving,
}: {
  /** Display label for the field. */
  label: string;
  /** Current persisted value. */
  value: string;
  /** Callback invoked with the new value when the user saves. */
  onSave: (newValue: string) => void;
  /** Whether a save operation is currently in progress. */
  isSaving: boolean;
}) {
  const [isEditing, setIsEditing] = useState(false);
  const [editValue, setEditValue] = useState(value);

  const handleStartEdit = () => {
    setEditValue(value);
    setIsEditing(true);
  };

  const handleCancel = () => {
    setEditValue(value);
    setIsEditing(false);
  };

  const handleSave = () => {
    onSave(editValue);
    setIsEditing(false);
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') {
      handleSave();
    } else if (e.key === 'Escape') {
      handleCancel();
    }
  };

  if (isEditing) {
    return (
      <div>
        <label className="block text-sm font-medium text-gray-700 mb-1">
          {label}
        </label>
        <div className="flex gap-2 items-center">
          <input
            type="text"
            value={editValue}
            onChange={(e) => setEditValue(e.target.value)}
            onKeyDown={handleKeyDown}
            className="flex-1 rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            disabled={isSaving}
            autoFocus
          />
          <button
            type="button"
            onClick={handleSave}
            disabled={isSaving}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isSaving ? 'Saving...' : 'Save'}
          </button>
          <button
            type="button"
            onClick={handleCancel}
            disabled={isSaving}
            className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Cancel
          </button>
        </div>
      </div>
    );
  }

  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">
        {label}
      </label>
      <div className="flex items-center gap-2">
        <span className="text-sm text-gray-900">
          {value || <span className="italic text-gray-400">Not set</span>}
        </span>
        <button
          type="button"
          onClick={handleStartEdit}
          className="rounded px-2 py-1 text-xs font-medium text-blue-600 hover:bg-blue-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
          aria-label={`Edit ${label}`}
        >
          Edit
        </button>
      </div>
    </div>
  );
}

/**
 * Maps a FileType string to an appropriate visual icon representation.
 * Replaces the monolith's RenderService.cs file-type-to-icon mapping logic.
 * Uses inline SVG icons with currentColor for theme compatibility.
 */
function FileTypeIcon({
  type,
  className,
}: {
  /** The classified file type (image, document, audio, video, application, other). */
  type: string;
  /** Optional additional CSS class names for sizing. */
  className?: string;
}) {
  const iconMap: Record<string, { path: string; label: string }> = {
    image: {
      path: 'M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z',
      label: 'Image file',
    },
    document: {
      path: 'M7 21h10a2 2 0 002-2V9.414a1 1 0 00-.293-.707l-5.414-5.414A1 1 0 0012.586 3H7a2 2 0 00-2 2v14a2 2 0 002 2z',
      label: 'Document file',
    },
    audio: {
      path: 'M9 19V6l12-3v13M9 19c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2zm12-3c0 1.105-1.343 2-3 2s-3-.895-3-2 1.343-2 3-2 3 .895 3 2z',
      label: 'Audio file',
    },
    video: {
      path: 'M15 10l4.553-2.276A1 1 0 0121 8.618v6.764a1 1 0 01-1.447.894L15 14M5 18h8a2 2 0 002-2V8a2 2 0 00-2-2H5a2 2 0 00-2 2v8a2 2 0 002 2z',
      label: 'Video file',
    },
    application: {
      path: 'M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4',
      label: 'Application file',
    },
    other: {
      path: 'M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8',
      label: 'File',
    },
  };

  const icon = iconMap[type] || iconMap.other;

  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-label={icon.label}
      role="img"
    >
      <path d={icon.path} />
    </svg>
  );
}

/**
 * Animated loading skeleton displayed while file metadata is being fetched.
 * Provides visual feedback that content is loading.
 */
function LoadingSkeleton() {
  return (
    <div className="mx-auto max-w-4xl p-6" role="status" aria-label="Loading file details">
      {/* Breadcrumb skeleton */}
      <div className="mb-4 h-4 w-48 animate-pulse rounded bg-gray-200" />

      {/* Card skeleton */}
      <div className="rounded-lg bg-white p-6 shadow">
        {/* Preview area skeleton */}
        <div className="mb-6 flex justify-center">
          <div className="h-48 w-48 animate-pulse rounded bg-gray-200" />
        </div>

        {/* Metadata grid skeleton */}
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          {Array.from({ length: 8 }).map((_, index) => (
            <div key={index}>
              <div className="mb-1 h-3 w-20 animate-pulse rounded bg-gray-200" />
              <div className="h-4 w-40 animate-pulse rounded bg-gray-200" />
            </div>
          ))}
        </div>

        {/* Editable fields skeleton */}
        <div className="mt-6 space-y-4">
          {Array.from({ length: 2 }).map((_, index) => (
            <div key={index}>
              <div className="mb-1 h-3 w-16 animate-pulse rounded bg-gray-200" />
              <div className="h-5 w-56 animate-pulse rounded bg-gray-200" />
            </div>
          ))}
        </div>
      </div>

      {/* Action buttons skeleton */}
      <div className="mt-6 flex gap-3">
        <div className="h-10 w-28 animate-pulse rounded bg-gray-200" />
        <div className="h-10 w-24 animate-pulse rounded bg-gray-200" />
        <div className="h-10 w-28 animate-pulse rounded bg-gray-200" />
      </div>

      <span className="sr-only">Loading file details…</span>
    </div>
  );
}

/**
 * Error state display when the file metadata fetch fails.
 * Shows the error message with a retry option.
 */
function ErrorDisplay({
  error,
  onRetry,
}: {
  /** The error object from the failed query. */
  error: Error | null;
  /** Callback to retry the failed request. */
  onRetry: () => void;
}) {
  return (
    <div className="mx-auto max-w-4xl p-6">
      <nav className="mb-4 text-sm text-gray-500" aria-label="Breadcrumb">
        <Link
          to="/files"
          className="text-blue-600 hover:text-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 rounded"
        >
          Files
        </Link>
        <span className="mx-2" aria-hidden="true">/</span>
        <span className="text-gray-700">Error</span>
      </nav>

      <div
        className="rounded-lg border border-red-200 bg-red-50 p-6 text-center"
        role="alert"
      >
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth={1.5}
          className="mx-auto mb-3 h-12 w-12 text-red-400"
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M12 9v3.75m9-.75a9 9 0 11-18 0 9 9 0 0118 0zm-9 3.75h.008v.008H12v-.008z"
          />
        </svg>
        <h2 className="mb-2 text-lg font-semibold text-red-800">
          Failed to Load File
        </h2>
        <p className="mb-4 text-sm text-red-600">
          {error?.message || 'An unexpected error occurred while loading the file details.'}
        </p>
        <div className="flex justify-center gap-3">
          <button
            type="button"
            onClick={onRetry}
            className="rounded bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2"
          >
            Try Again
          </button>
          <Link
            to="/files"
            className="inline-flex items-center rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
          >
            Back to Files
          </Link>
        </div>
      </div>
    </div>
  );
}

/**
 * Not-found state displayed when no file matches the requested ID.
 */
function NotFound() {
  return (
    <div className="mx-auto max-w-4xl p-6">
      <nav className="mb-4 text-sm text-gray-500" aria-label="Breadcrumb">
        <Link
          to="/files"
          className="text-blue-600 hover:text-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 rounded"
        >
          Files
        </Link>
        <span className="mx-2" aria-hidden="true">/</span>
        <span className="text-gray-700">Not Found</span>
      </nav>

      <div className="rounded-lg border border-gray-200 bg-white p-12 text-center shadow">
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth={1.5}
          className="mx-auto mb-4 h-16 w-16 text-gray-300"
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z"
          />
        </svg>
        <h2 className="mb-2 text-lg font-semibold text-gray-800">
          File Not Found
        </h2>
        <p className="mb-6 text-sm text-gray-500">
          The requested file could not be found. It may have been deleted or the
          URL is incorrect.
        </p>
        <Link
          to="/files"
          className="inline-flex items-center rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
        >
          Back to Files
        </Link>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------

/**
 * Formats a file size in bytes into a human-readable string.
 * Uses KB for files < 1 MB, MB for files < 1 GB, and GB otherwise.
 * Matches the monolith's UserFileService KB calculation:
 * `Math.Round((decimal)tempFile.GetBytes().Length / 1024, 2)`
 */
function formatFileSize(bytes: number): string {
  if (bytes === 0) return '0 KB';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(2)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

/**
 * Formats an ISO 8601 date string into a localized display string.
 * Returns 'N/A' for null or undefined values.
 */
function formatDate(isoString: string | undefined | null): string {
  if (!isoString) return 'N/A';
  try {
    return new Date(isoString).toLocaleString();
  } catch {
    return isoString;
  }
}

/**
 * Capitalizes the first letter of a file type string for display.
 */
function formatFileType(type: string): string {
  if (!type) return 'Unknown';
  return type.charAt(0).toUpperCase() + type.slice(1);
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * File detail view page component.
 *
 * Displays file metadata, image preview (for image-type files),
 * inline-editable alt text and caption fields, and action buttons
 * for download and deletion.
 *
 * Mounted at route `/files/:id` and loaded via `React.lazy()` for
 * code splitting. Requires authentication (protected route).
 *
 * Data fetching is handled entirely through TanStack Query 5 hooks:
 *  - useFile(id) for metadata
 *  - useFileDownloadUrl(id) for presigned S3 download URL
 *  - useDeleteFile() for deletion
 *  - useUpdateFileMetadata() for alt/caption editing
 */
export default function FileDetails() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  // TanStack Query hooks — sole data fetching mechanism (AAP §0.8.1)
  const {
    data: fileResponse,
    isLoading,
    isError,
    error,
    refetch,
  } = useFile(id);
  const { data: downloadUrlResponse } = useFileDownloadUrl(id);
  const deleteFileMutation = useDeleteFile();
  const updateMetadataMutation = useUpdateFileMetadata();

  // Extract typed data from ApiResponse wrappers
  const file = fileResponse?.object;
  const downloadUrl = downloadUrlResponse?.object?.downloadUrl;

  /**
   * Opens the presigned S3 download URL in a new browser tab,
   * triggering the browser's native download behavior.
   * Replaces DbFile.GetBytes() / DbFile.GetContentStream() which
   * resolved content from 3 storage backends.
   */
  const handleDownload = () => {
    if (downloadUrl) {
      window.open(downloadUrl, '_blank', 'noopener,noreferrer');
    }
  };

  /**
   * Deletes the file after user confirmation.
   * Replaces DbFileRepository.Delete() which removed both the storage
   * content and the metadata record. Navigates back to /files on success.
   */
  const handleDelete = async () => {
    if (!id) return;

    const confirmed = window.confirm(
      'Are you sure you want to delete this file? This action cannot be undone.',
    );
    if (!confirmed) return;

    try {
      await deleteFileMutation.mutateAsync(id);
      navigate('/files', { replace: true });
    } catch {
      // Error state is managed by the mutation's isError / error properties.
      // The user sees the error in the UI via the deletion error display below.
    }
  };

  /**
   * Updates the file's alt text metadata.
   * Uses useUpdateFileMetadata() with the nested { id, data } signature.
   */
  const handleUpdateAlt = (newAlt: string) => {
    if (!id) return;
    updateMetadataMutation.mutate({
      id,
      data: { alt: newAlt },
    });
  };

  /**
   * Updates the file's caption metadata.
   * Uses useUpdateFileMetadata() with the nested { id, data } signature.
   */
  const handleUpdateCaption = (newCaption: string) => {
    if (!id) return;
    updateMetadataMutation.mutate({
      id,
      data: { caption: newCaption },
    });
  };

  // --- Render: Loading state ---
  if (isLoading) {
    return <LoadingSkeleton />;
  }

  // --- Render: Error state ---
  if (isError) {
    return <ErrorDisplay error={error as Error | null} onRetry={refetch} />;
  }

  // --- Render: Not found state ---
  if (!file) {
    return <NotFound />;
  }

  // Determine if the file is an image for preview rendering
  const isImage = file.type === 'image';

  return (
    <div className="mx-auto max-w-4xl p-6">
      {/* Breadcrumb navigation */}
      <nav className="mb-4 text-sm text-gray-500" aria-label="Breadcrumb">
        <Link
          to="/files"
          className="text-blue-600 hover:text-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 rounded"
        >
          Files
        </Link>
        <span className="mx-2" aria-hidden="true">/</span>
        <span className="text-gray-700">{file.filename}</span>
      </nav>

      {/* File preview and metadata card */}
      <div className="rounded-lg bg-white p-6 shadow">
        {/* Image preview for image-type files using presigned S3 URL */}
        {isImage && downloadUrl && (
          <div className="mb-6">
            <img
              src={downloadUrl}
              alt={file.alt || file.filename}
              className="mx-auto max-w-full rounded"
              width={file.width}
              height={file.height}
              loading="lazy"
              decoding="async"
              style={{
                backgroundColor: '#f3f4f6',
                maxHeight: '24rem',
                objectFit: 'contain',
              }}
            />
          </div>
        )}

        {/* File type icon for non-image files */}
        {!isImage && (
          <div className="mb-6 flex justify-center">
            <FileTypeIcon type={file.type} className="h-24 w-24 text-gray-400" />
          </div>
        )}

        {/* File metadata grid */}
        <dl className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <MetadataRow label="Filename" value={file.filename} />
          <MetadataRow label="Type" value={formatFileType(file.type)} />
          <MetadataRow label="Size" value={formatFileSize(file.size)} />
          <MetadataRow label="Path" value={file.path} />
          <MetadataRow label="Content Type" value={file.contentType} />
          <MetadataRow label="Created" value={formatDate(file.createdOn)} />
          <MetadataRow
            label="Created By"
            value={file.createdBy || 'Unknown'}
          />
          <MetadataRow
            label="Last Modified"
            value={formatDate(file.lastModifiedOn)}
          />
          <MetadataRow
            label="Last Modified By"
            value={file.lastModifiedBy || 'Unknown'}
          />
          {/* Image dimensions (only shown for image-type files with dimensions) */}
          {file.width != null && file.height != null && (
            <MetadataRow
              label="Dimensions"
              value={`${file.width} × ${file.height} px`}
            />
          )}
        </dl>

        {/* Editable metadata: alt text and caption */}
        <div className="mt-6 space-y-4 border-t border-gray-200 pt-6">
          <EditableField
            label="Alt Text"
            value={file.alt || ''}
            onSave={handleUpdateAlt}
            isSaving={updateMetadataMutation.isPending}
          />
          <EditableField
            label="Caption"
            value={file.caption || ''}
            onSave={handleUpdateCaption}
            isSaving={updateMetadataMutation.isPending}
          />
        </div>

        {/* Metadata update error display */}
        {updateMetadataMutation.isError && (
          <div
            className="mt-4 rounded border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
            role="alert"
          >
            Failed to update metadata:{' '}
            {(updateMetadataMutation.error as Error)?.message ||
              'An unexpected error occurred.'}
          </div>
        )}
      </div>

      {/* Action buttons */}
      <div className="mt-6 flex flex-wrap gap-3">
        <button
          type="button"
          onClick={handleDownload}
          disabled={!downloadUrl}
          className="inline-flex items-center rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth={2}
            className="mr-2 h-4 w-4"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M3 16.5v2.25A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75V16.5M16.5 12L12 16.5m0 0L7.5 12m4.5 4.5V3"
            />
          </svg>
          Download
        </button>

        <button
          type="button"
          onClick={handleDelete}
          disabled={deleteFileMutation.isPending}
          className="inline-flex items-center rounded bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth={2}
            className="mr-2 h-4 w-4"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0"
            />
          </svg>
          {deleteFileMutation.isPending ? 'Deleting…' : 'Delete'}
        </button>

        <Link
          to="/files"
          className="inline-flex items-center rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
        >
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth={2}
            className="mr-2 h-4 w-4"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M10.5 19.5L3 12m0 0l7.5-7.5M3 12h18"
            />
          </svg>
          Back to Files
        </Link>
      </div>

      {/* Deletion error display */}
      {deleteFileMutation.isError && (
        <div
          className="mt-4 rounded border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
          role="alert"
        >
          Failed to delete file:{' '}
          {(deleteFileMutation.error as Error)?.message ||
            'An unexpected error occurred.'}
        </div>
      )}
    </div>
  );
}
