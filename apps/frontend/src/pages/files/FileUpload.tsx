import React, { useState, useCallback, useRef } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  useRequestUploadUrl,
  useUploadFile,
  useConfirmUpload,
} from '../../hooks/useFiles';

/**
 * File type classification matching the monolith's UserFileService MIME logic.
 * Categories: image, video, audio, document, other — exactly replicating
 * UserFileService.CreateUserFile() lines 70-89.
 */
function classifyFileType(file: File): string {
  const mimeType = file.type;
  const extensionMatch = file.name.split('.').pop()?.toLowerCase();
  const extension = extensionMatch ? `.${extensionMatch}` : '';

  if (mimeType.startsWith('image/')) return 'image';
  if (mimeType.startsWith('video/')) return 'video';
  if (mimeType.startsWith('audio/')) return 'audio';

  /* Document extension allowlist from UserFileService (lines 82-84) */
  const docExtensions = [
    '.doc',
    '.docx',
    '.odt',
    '.rtf',
    '.txt',
    '.pdf',
    '.html',
    '.htm',
    '.ppt',
    '.pptx',
    '.xls',
    '.xlsx',
    '.ods',
    '.odp',
  ];
  if (docExtensions.includes(extension)) return 'document';

  return 'other';
}

/**
 * Get image dimensions client-side via HTMLImageElement.
 * Replaces Helpers.GetImageDimension(tempFile.GetBytes()) from the monolith's
 * UserFileService (line 71). Uses Object URL for efficient in-memory loading.
 */
function getImageDimensions(
  file: File,
): Promise<{ width: number; height: number }> {
  return new Promise((resolve, reject) => {
    const objectUrl = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      const width = img.naturalWidth;
      const height = img.naturalHeight;
      URL.revokeObjectURL(objectUrl);
      resolve({ width, height });
    };
    img.onerror = () => {
      URL.revokeObjectURL(objectUrl);
      reject(new Error('Failed to load image for dimension detection'));
    };
    img.src = objectUrl;
  });
}

/**
 * Format file size in KB, matching UserFileService calculation:
 * Math.Round((decimal)tempFile.GetBytes().Length / 1024, 2)
 */
function formatFileSize(bytes: number): string {
  return (bytes / 1024).toFixed(2);
}

/** Status badge color mappings for each upload state */
const STATUS_STYLES: Record<
  string,
  { bg: string; text: string; label: string }
> = {
  pending: {
    bg: 'bg-gray-100',
    text: 'text-gray-700',
    label: 'Pending',
  },
  uploading: {
    bg: 'bg-blue-100',
    text: 'text-blue-700',
    label: 'Uploading',
  },
  confirming: {
    bg: 'bg-yellow-100',
    text: 'text-yellow-700',
    label: 'Confirming',
  },
  completed: {
    bg: 'bg-green-100',
    text: 'text-green-700',
    label: 'Completed',
  },
  error: {
    bg: 'bg-red-100',
    text: 'text-red-700',
    label: 'Error',
  },
};

/** File type icon mappings for visual classification display */
const FILE_TYPE_ICONS: Record<string, { icon: string; color: string }> = {
  image: { icon: '🖼️', color: 'text-purple-600' },
  video: { icon: '🎬', color: 'text-blue-600' },
  audio: { icon: '🎵', color: 'text-green-600' },
  document: { icon: '📄', color: 'text-orange-600' },
  other: { icon: '📁', color: 'text-gray-600' },
};

/** Represents a file in the upload queue with all tracked metadata */
interface UploadingFile {
  /** The browser File object from input or drag-and-drop */
  file: File;
  /** Server-assigned file ID from Step 1 (presigned URL request) */
  id: string;
  /** Upload progress percentage 0-100 during S3 upload (Step 2) */
  progress: number;
  /** Current status in the 3-step upload pipeline */
  status: 'pending' | 'uploading' | 'confirming' | 'completed' | 'error';
  /** Error message if upload failed at any step */
  error?: string;
  /** Alt text metadata — matches UserFileService.CreateUserFile(path, alt, caption) */
  alt: string;
  /** Caption metadata — matches UserFileService.CreateUserFile(path, alt, caption) */
  caption: string;
  /** Client-side pre-classified file type for UI display */
  fileType: string;
}

/**
 * FileUpload page component — React replacement for the monolith's
 * UserFileService.CreateUserFile() and DbFileRepository.Create().
 *
 * Implements the 3-step S3 presigned URL upload pattern:
 *   Step 1: POST /v1/files/upload-url → { uploadUrl, fileId }
 *   Step 2: PUT <uploadUrl> with file binary (direct S3, NOT through Lambda)
 *   Step 3: POST /v1/files/confirm → metadata record creation
 *
 * Supports multiple file upload, drag-and-drop, per-file progress tracking,
 * alt/caption metadata, client-side image dimension detection, and MIME
 * type pre-classification — all matching the original monolith behavior.
 */
export default function FileUpload() {
  const navigate = useNavigate();
  const fileInputRef = useRef<HTMLInputElement>(null);

  const [files, setFiles] = useState<UploadingFile[]>([]);
  const filesRef = useRef<UploadingFile[]>(files);
  filesRef.current = files; // Keep ref in sync every render
  const [isDragging, setIsDragging] = useState(false);
  const [isUploadingAll, setIsUploadingAll] = useState(false);

  /* TanStack Query mutation hooks — 3-step upload flow */
  const requestUrl = useRequestUploadUrl();
  const uploadFile = useUploadFile();
  const confirmUpload = useConfirmUpload();

  /* ───────── State update helpers ───────── */

  const updateFile = useCallback(
    (index: number, updates: Partial<UploadingFile>) => {
      setFiles((prev) =>
        prev.map((f, i) => (i === index ? { ...f, ...updates } : f)),
      );
    },
    [],
  );

  const removeFile = useCallback((index: number) => {
    setFiles((prev) => prev.filter((_, i) => i !== index));
  }, []);

  /* ───────── File selection handler ───────── */

  const handleFilesSelected = useCallback((selectedFiles: FileList) => {
    const newFiles: UploadingFile[] = Array.from(selectedFiles).map(
      (file) => ({
        file,
        id: '',
        progress: 0,
        status: 'pending' as const,
        alt: '',
        caption: '',
        fileType: classifyFileType(file),
      }),
    );
    setFiles((prev) => [...prev, ...newFiles]);
  }, []);

  /* ───────── Drag-and-drop handlers ───────── */

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(true);
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDragging(false);
  }, []);

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setIsDragging(false);
      if (e.dataTransfer.files.length > 0) {
        handleFilesSelected(e.dataTransfer.files);
      }
    },
    [handleFilesSelected],
  );

  /* ───────── 3-step upload flow for a single file ───────── */

  const uploadSingleFile = useCallback(
    async (index: number) => {
      /**
       * Read the current file state from the ref. Using a ref ensures
       * synchronous access to the latest state regardless of React 18
       * automatic batching, which may defer setState updater callbacks
       * when other state updates are pending in the same batch.
       */
      const currentFile = filesRef.current[index];

      if (!currentFile || currentFile.status !== 'pending') return;

      try {
        /* Step 1: Request presigned S3 upload URL */
        updateFile(index, { status: 'uploading', progress: 0, error: undefined });

        const urlResult = await requestUrl.mutateAsync({
          filename: currentFile.file.name,
          contentType: currentFile.file.type || 'application/octet-stream',
          size: currentFile.file.size,
        });

        const uploadUrlData = urlResult.object;
        if (!uploadUrlData) {
          throw new Error(
            'Upload URL response missing required data (uploadUrl, fileId)',
          );
        }

        const fileId = uploadUrlData.fileId;
        const uploadUrl = uploadUrlData.uploadUrl;

        updateFile(index, { id: fileId });

        /* Step 2: Direct upload to S3 presigned URL with progress tracking */
        await uploadFile.mutateAsync({
          uploadUrl,
          file: currentFile.file,
          contentType: currentFile.file.type || 'application/octet-stream',
          onProgress: (progress) => {
            updateFile(index, { progress: progress.percentage });
          },
        });

        /* Step 3: Confirm upload — create metadata record on server */
        updateFile(index, { status: 'confirming', progress: 100 });

        /**
         * Client-side image dimension detection — replaces
         * Helpers.GetImageDimension(tempFile.GetBytes()) from
         * UserFileService (line 71).
         */
        let width: number | undefined;
        let height: number | undefined;
        if (currentFile.file.type.startsWith('image/')) {
          try {
            const dimensions = await getImageDimensions(currentFile.file);
            width = dimensions.width;
            height = dimensions.height;
          } catch {
            /* Non-fatal: dimensions are optional metadata */
          }
        }

        /**
         * Re-read alt/caption from the ref since user may have
         * edited them while upload was in progress.
         */
        const latestState = filesRef.current[index];
        const latestAlt = latestState?.alt ?? currentFile.alt;
        const latestCaption = latestState?.caption ?? currentFile.caption;

        await confirmUpload.mutateAsync({
          fileId,
          filename: currentFile.file.name,
          alt: latestAlt || undefined,
          caption: latestCaption || undefined,
          contentType: currentFile.file.type || 'application/octet-stream',
          size: currentFile.file.size,
          width,
          height,
        });

        updateFile(index, { status: 'completed', progress: 100 });
      } catch (err: unknown) {
        const message =
          err instanceof Error ? err.message : 'Upload failed. Please try again.';
        updateFile(index, { status: 'error', error: message });
      }
    },
    [requestUrl, uploadFile, confirmUpload, updateFile],
  );

  /* ───────── Batch upload: all pending files ───────── */

  const uploadAll = useCallback(async () => {
    setIsUploadingAll(true);
    try {
      /**
       * Read from filesRef.current so we always see the latest state,
       * avoiding stale closure issues under React 18 automatic batching.
       */
      const currentFiles = filesRef.current;
      for (let i = 0; i < currentFiles.length; i++) {
        if (currentFiles[i].status === 'pending') {
          await uploadSingleFile(i);
        }
      }
    } finally {
      setIsUploadingAll(false);
    }
  }, [uploadSingleFile]);

  /* ───────── Retry a single failed upload ───────── */

  const retryUpload = useCallback(
    async (index: number) => {
      const resetData: Partial<UploadingFile> = {
        status: 'pending',
        progress: 0,
        error: undefined,
        id: '',
      };
      updateFile(index, resetData);
      /**
       * Also update the ref immediately so uploadSingleFile sees the
       * 'pending' status synchronously (React 18 batching may defer the
       * state-setter callback).
       */
      const current = filesRef.current[index];
      if (current) {
        filesRef.current = filesRef.current.map((f, i) =>
          i === index ? { ...f, ...resetData } as UploadingFile : f,
        );
      }
      await uploadSingleFile(index);
    },
    [uploadSingleFile, updateFile],
  );

  /* ───────── Clear completed files from queue ───────── */

  const clearCompleted = useCallback(() => {
    setFiles((prev) => prev.filter((f) => f.status !== 'completed'));
  }, []);

  /* ───────── Derived state ───────── */

  const hasPendingFiles = files.some((f) => f.status === 'pending');
  const hasCompletedFiles = files.some((f) => f.status === 'completed');
  const allCompleted =
    files.length > 0 && files.every((f) => f.status === 'completed');

  /* ───────── Render ───────── */

  return (
    <div className="mx-auto max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-6 flex items-center justify-between">
        <div>
          <nav aria-label="Breadcrumb" className="mb-1">
            <ol className="flex items-center gap-1.5 text-sm text-gray-500">
              <li>
                <Link
                  to="/files"
                  className="hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 rounded"
                >
                  Files
                </Link>
              </li>
              <li aria-hidden="true">
                <svg
                  className="h-4 w-4 text-gray-400"
                  viewBox="0 0 16 16"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path
                    fillRule="evenodd"
                    d="M6.22 4.22a.75.75 0 0 1 1.06 0l3.25 3.25a.75.75 0 0 1 0 1.06l-3.25 3.25a.75.75 0 0 1-1.06-1.06L8.94 8 6.22 5.28a.75.75 0 0 1 0-1.06Z"
                    clipRule="evenodd"
                  />
                </svg>
              </li>
              <li>
                <span className="font-medium text-gray-900">Upload</span>
              </li>
            </ol>
          </nav>
          <h1 className="text-2xl font-bold text-gray-900">Upload Files</h1>
        </div>
        <Link
          to="/files"
          className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
        >
          <svg
            className="h-4 w-4"
            viewBox="0 0 16 16"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M9.78 4.22a.75.75 0 0 1 0 1.06L7.06 8l2.72 2.72a.75.75 0 1 1-1.06 1.06L5.47 8.53a.75.75 0 0 1 0-1.06l3.25-3.25a.75.75 0 0 1 1.06 0Z"
              clipRule="evenodd"
            />
          </svg>
          Back to Files
        </Link>
      </div>

      {/* Drag-and-drop zone */}
      <div
        role="button"
        tabIndex={0}
        aria-label="Drop files here or click to browse"
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        onClick={() => fileInputRef.current?.click()}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            fileInputRef.current?.click();
          }
        }}
        className={`relative cursor-pointer rounded-lg border-2 border-dashed p-12 text-center transition-colors duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 ${
          isDragging
            ? 'border-blue-500 bg-blue-50'
            : 'border-gray-300 bg-gray-50 hover:border-gray-400 hover:bg-gray-100'
        }`}
      >
        {/* Upload icon */}
        <svg
          className="mx-auto h-12 w-12 text-gray-400"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth={1.5}
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M3 16.5v2.25A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V16.5m-13.5-9L12 3m0 0 4.5 4.5M12 3v13.5"
          />
        </svg>
        <p className="mt-3 text-sm font-medium text-gray-700">
          {isDragging ? 'Drop files here' : 'Drag and drop files here, or'}
        </p>
        {!isDragging && (
          <p className="mt-1 text-sm text-blue-600">click to browse</p>
        )}
        <p className="mt-2 text-xs text-gray-500">
          Supports all file types — images, documents, audio, video, and more
        </p>
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="sr-only"
          aria-label="Select files to upload"
          onChange={(e) => {
            if (e.target.files && e.target.files.length > 0) {
              handleFilesSelected(e.target.files);
            }
            /* Reset input value so re-selecting the same file works */
            e.target.value = '';
          }}
        />
      </div>

      {/* Upload queue */}
      {files.length > 0 && (
        <div className="mt-6">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-lg font-semibold text-gray-900">
              Upload Queue
              <span className="ml-2 text-sm font-normal text-gray-500">
                ({files.length} {files.length === 1 ? 'file' : 'files'})
              </span>
            </h2>
            {hasCompletedFiles && (
              <button
                type="button"
                onClick={clearCompleted}
                className="text-sm text-gray-500 hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 rounded"
              >
                Clear completed
              </button>
            )}
          </div>

          <ul className="space-y-3" role="list">
            {files.map((uploadingFile, index) => {
              const statusStyle = STATUS_STYLES[uploadingFile.status];
              const typeInfo =
                FILE_TYPE_ICONS[uploadingFile.fileType] ||
                FILE_TYPE_ICONS.other;
              const isPending = uploadingFile.status === 'pending';
              const isActive =
                uploadingFile.status === 'uploading' ||
                uploadingFile.status === 'confirming';
              const isError = uploadingFile.status === 'error';
              const isCompleted = uploadingFile.status === 'completed';

              return (
                <li
                  key={`${uploadingFile.file.name}-${uploadingFile.file.size}-${uploadingFile.file.lastModified}-${index}`}
                  className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
                >
                  {/* File info row */}
                  <div className="flex items-start gap-3">
                    {/* File type icon */}
                    <span
                      className={`flex-shrink-0 text-2xl ${typeInfo.color}`}
                      aria-hidden="true"
                    >
                      {typeInfo.icon}
                    </span>

                    {/* File details */}
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2">
                        <p className="truncate text-sm font-medium text-gray-900">
                          {uploadingFile.file.name}
                        </p>
                        <span
                          className={`inline-flex flex-shrink-0 items-center rounded-full px-2 py-0.5 text-xs font-medium ${statusStyle.bg} ${statusStyle.text}`}
                        >
                          {statusStyle.label}
                        </span>
                      </div>
                      <div className="mt-0.5 flex items-center gap-3 text-xs text-gray-500">
                        <span>{formatFileSize(uploadingFile.file.size)} KB</span>
                        <span className="capitalize">
                          {uploadingFile.fileType}
                        </span>
                        {uploadingFile.file.type && (
                          <span>{uploadingFile.file.type}</span>
                        )}
                      </div>

                      {/* Alt and caption inputs — editable only when pending */}
                      {isPending && (
                        <div className="mt-3 grid grid-cols-1 gap-2 sm:grid-cols-2">
                          <div>
                            <label
                              htmlFor={`alt-${index}`}
                              className="block text-xs font-medium text-gray-600"
                            >
                              Alt text
                            </label>
                            <input
                              id={`alt-${index}`}
                              type="text"
                              placeholder="Describe this file"
                              value={uploadingFile.alt}
                              onChange={(e) =>
                                updateFile(index, { alt: e.target.value })
                              }
                              className="mt-0.5 block w-full rounded border border-gray-300 px-2 py-1 text-sm text-gray-900 placeholder-gray-400 focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
                            />
                          </div>
                          <div>
                            <label
                              htmlFor={`caption-${index}`}
                              className="block text-xs font-medium text-gray-600"
                            >
                              Caption
                            </label>
                            <input
                              id={`caption-${index}`}
                              type="text"
                              placeholder="Optional caption"
                              value={uploadingFile.caption}
                              onChange={(e) =>
                                updateFile(index, { caption: e.target.value })
                              }
                              className="mt-0.5 block w-full rounded border border-gray-300 px-2 py-1 text-sm text-gray-900 placeholder-gray-400 focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-blue-500"
                            />
                          </div>
                        </div>
                      )}

                      {/* Display alt/caption values after upload */}
                      {!isPending &&
                        (uploadingFile.alt || uploadingFile.caption) && (
                          <div className="mt-1 flex gap-3 text-xs text-gray-500">
                            {uploadingFile.alt && (
                              <span>
                                Alt: {uploadingFile.alt}
                              </span>
                            )}
                            {uploadingFile.caption && (
                              <span>
                                Caption: {uploadingFile.caption}
                              </span>
                            )}
                          </div>
                        )}

                      {/* Progress bar — shown during uploading/confirming */}
                      {(isActive || isCompleted) && (
                        <div className="mt-2">
                          <div
                            className="h-2 w-full overflow-hidden rounded-full bg-gray-200"
                            role="progressbar"
                            aria-valuenow={uploadingFile.progress}
                            aria-valuemin={0}
                            aria-valuemax={100}
                            aria-label={`Upload progress for ${uploadingFile.file.name}`}
                          >
                            <div
                              className={`h-full rounded-full transition-all duration-300 ${
                                isCompleted ? 'bg-green-500' : 'bg-blue-500'
                              }`}
                              style={{ width: `${uploadingFile.progress}%` }}
                            />
                          </div>
                          <p className="mt-0.5 text-right text-xs text-gray-500">
                            {uploadingFile.progress}%
                          </p>
                        </div>
                      )}

                      {/* Error message */}
                      {isError && uploadingFile.error && (
                        <p
                          className="mt-2 text-sm text-red-600"
                          role="alert"
                        >
                          {uploadingFile.error}
                        </p>
                      )}
                    </div>

                    {/* Action buttons */}
                    <div className="flex flex-shrink-0 items-center gap-1">
                      {isError && (
                        <button
                          type="button"
                          onClick={() => retryUpload(index)}
                          className="rounded p-1 text-blue-600 hover:bg-blue-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
                          aria-label={`Retry upload for ${uploadingFile.file.name}`}
                        >
                          <svg
                            className="h-5 w-5"
                            viewBox="0 0 20 20"
                            fill="currentColor"
                            aria-hidden="true"
                          >
                            <path
                              fillRule="evenodd"
                              d="M15.312 11.424a5.5 5.5 0 0 1-9.201 2.466l-.312-.311h2.433a.75.75 0 0 0 0-1.5H4.598a.75.75 0 0 0-.75.75v3.634a.75.75 0 0 0 1.5 0v-2.033l.28.28a7 7 0 0 0 11.712-3.138.75.75 0 0 0-1.028-.148Zm-10.624-2.85a5.5 5.5 0 0 1 9.201-2.466l.312.312H11.77a.75.75 0 0 0 0 1.5h3.634a.75.75 0 0 0 .75-.75V3.536a.75.75 0 0 0-1.5 0v2.033l-.28-.28A7 7 0 0 0 3.66 8.427a.75.75 0 0 0 1.028.148Z"
                              clipRule="evenodd"
                            />
                          </svg>
                        </button>
                      )}
                      {(isPending || isError) && (
                        <button
                          type="button"
                          onClick={() => removeFile(index)}
                          className="rounded p-1 text-gray-400 hover:bg-gray-100 hover:text-gray-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
                          aria-label={`Remove ${uploadingFile.file.name} from queue`}
                        >
                          <svg
                            className="h-5 w-5"
                            viewBox="0 0 20 20"
                            fill="currentColor"
                            aria-hidden="true"
                          >
                            <path d="M6.28 5.22a.75.75 0 0 0-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 1 0 1.06 1.06L10 11.06l3.72 3.72a.75.75 0 1 0 1.06-1.06L11.06 10l3.72-3.72a.75.75 0 0 0-1.06-1.06L10 8.94 6.28 5.22Z" />
                          </svg>
                        </button>
                      )}
                      {isCompleted && (
                        <svg
                          className="h-5 w-5 text-green-500"
                          viewBox="0 0 20 20"
                          fill="currentColor"
                          aria-label="Upload complete"
                        >
                          <path
                            fillRule="evenodd"
                            d="M10 18a8 8 0 1 0 0-16 8 8 0 0 0 0 16Zm3.857-9.809a.75.75 0 0 0-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 1 0-1.06 1.061l2.5 2.5a.75.75 0 0 0 1.137-.089l4-5.5Z"
                            clipRule="evenodd"
                          />
                        </svg>
                      )}
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
        </div>
      )}

      {/* Action buttons */}
      {files.length > 0 && (
        <div className="mt-6 flex flex-wrap items-center gap-3">
          {hasPendingFiles && (
            <button
              type="button"
              onClick={uploadAll}
              disabled={isUploadingAll}
              className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isUploadingAll ? (
                <>
                  <svg
                    className="h-4 w-4 animate-spin"
                    viewBox="0 0 24 24"
                    fill="none"
                    aria-hidden="true"
                  >
                    <circle
                      className="opacity-25"
                      cx="12"
                      cy="12"
                      r="10"
                      stroke="currentColor"
                      strokeWidth="4"
                    />
                    <path
                      className="opacity-75"
                      fill="currentColor"
                      d="M4 12a8 8 0 0 1 8-8V0C5.373 0 0 5.373 0 12h4Z"
                    />
                  </svg>
                  Uploading…
                </>
              ) : (
                <>
                  <svg
                    className="h-4 w-4"
                    viewBox="0 0 16 16"
                    fill="currentColor"
                    aria-hidden="true"
                  >
                    <path d="M7.25 10.25a.75.75 0 0 0 1.5 0V4.56l2.22 2.22a.75.75 0 1 0 1.06-1.06l-3.5-3.5a.75.75 0 0 0-1.06 0l-3.5 3.5a.75.75 0 0 0 1.06 1.06l2.22-2.22v5.69Z" />
                    <path d="M3.5 9.75a.75.75 0 0 0-1.5 0v1.5A2.75 2.75 0 0 0 4.75 14h6.5A2.75 2.75 0 0 0 14 11.25v-1.5a.75.75 0 0 0-1.5 0v1.5c0 .69-.56 1.25-1.25 1.25h-6.5c-.69 0-1.25-.56-1.25-1.25v-1.5Z" />
                  </svg>
                  Upload All
                </>
              )}
            </button>
          )}

          {allCompleted && (
            <button
              type="button"
              onClick={() => navigate('/files')}
              className="inline-flex items-center gap-2 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-green-500 focus-visible:ring-offset-2"
            >
              <svg
                className="h-4 w-4"
                viewBox="0 0 16 16"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M12.416 3.376a.75.75 0 0 1 .208 1.04l-5 7.5a.75.75 0 0 1-1.154.114l-3-3a.75.75 0 0 1 1.06-1.06l2.353 2.353 4.493-6.74a.75.75 0 0 1 1.04-.207Z"
                  clipRule="evenodd"
                />
              </svg>
              Done — View Files
            </button>
          )}

          {!hasPendingFiles && !allCompleted && (
            <button
              type="button"
              onClick={() => navigate('/files')}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
            >
              Back to Files
            </button>
          )}
        </div>
      )}

      {/* Empty state */}
      {files.length === 0 && (
        <p className="mt-6 text-center text-sm text-gray-500">
          No files selected. Drag and drop files above or click to browse.
        </p>
      )}
    </div>
  );
}
