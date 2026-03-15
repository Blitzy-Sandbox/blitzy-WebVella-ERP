/**
 * @fileoverview TanStack Query 5 hooks for file management operations.
 *
 * Replaces the monolith's UserFileService.cs (user file CRUD with type/search
 * /sort filters, MIME classification, image dimension computation),
 * DbFileRepository.cs (LO/filesystem/blob storage backends), and DbFile.cs
 * (file metadata model) with API calls to the File Management microservice
 * using S3 presigned URLs.
 *
 * Upload flow (3-step pattern replacing the monolith's transactional approach):
 *  1. useRequestUploadUrl() → Gets presigned S3 URL + fileId
 *  2. useUploadFile()       → Direct upload to S3 (with progress tracking)
 *  3. useConfirmUpload()    → Create metadata record + server-side MIME classification
 *
 * This 3-step flow replaces the monolith's atomic CreateUserFile which moved
 * files within the server filesystem and created the DB record in a single
 * PostgreSQL transaction.
 *
 * @module useFiles
 */

import { useCallback, useState } from 'react';
import {
  useQuery,
  useMutation,
  useQueryClient,
  keepPreviousData,
} from '@tanstack/react-query';
import axios from 'axios';
import { get, post, put, del } from '../api/client';
import type { ApiResponse } from '../api/client';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Key Factory
// ---------------------------------------------------------------------------

/**
 * Centralized query key factory for file-related queries.
 * Ensures consistent key structure across all file hooks for cache management.
 */
const fileKeys = {
  /** Root key for all file queries. */
  all: ['files'] as const,
  /** Key for file list queries (with varying filter params). */
  lists: () => [...fileKeys.all, 'list'] as const,
  /** Key for a specific file list query filtered by params. */
  list: (params: FileListParams | undefined) =>
    [...fileKeys.lists(), params] as const,
  /** Key prefix for single-file detail queries. */
  details: () => [...fileKeys.all, 'detail'] as const,
  /** Key for a specific file detail by ID. */
  detail: (id: string) => [...fileKeys.details(), id] as const,
  /** Key for a presigned download URL query for a specific file. */
  downloadUrl: (id: string) =>
    [...fileKeys.all, id, 'download-url'] as const,
};

// ---------------------------------------------------------------------------
// Local Type Definitions
// ---------------------------------------------------------------------------

/**
 * File type classification enum matching the monolith's
 * UserFileService.CreateUserFile MIME classification logic plus
 * the document extension allowlist (.doc, .docx, .odt, .rtf, .txt,
 * .pdf, .html, .htm, .ppt, .pptx, .xls, .xlsx, .ods, .odp).
 */
export type FileType =
  | 'image'
  | 'document'
  | 'audio'
  | 'video'
  | 'application'
  | 'other';

/**
 * Sort options for file listing.
 * Mirrors UserFileService.GetFilesList sort parameter:
 *  - 1 = created_on DESC (newest first, default)
 *  - 2 = name ASC (alphabetical)
 */
export type FileSortOption = 1 | 2;

/**
 * Parameters for the file listing query.
 * Matches UserFileService.GetFilesList(type, search, sort, page, pageSize).
 */
export interface FileListParams {
  /** Filter by file type classification (image, document, video, etc.). */
  type?: FileType;
  /** Search text applied as OR condition over name, alt, and caption fields. */
  search?: string;
  /** Sort option: 1 = created_on DESC, 2 = name ASC. */
  sort?: FileSortOption;
  /** Page number (1-based, default: 1). */
  page?: number;
  /** Number of items per page (default: 20). */
  pageSize?: number;
}

/**
 * File metadata model returned by the File Management service.
 * Replaces the monolith's DbFile.cs (Id, FilePath, CreatedOn, ObjectId,
 * CreatedBy, LastModifiedBy, LastModificationDate) with S3-aware metadata
 * including dimensions and classified type.
 */
export interface FileMetadata {
  /** Unique file identifier (GUID as string). */
  id: string;
  /** Original filename including extension. */
  filename: string;
  /** MIME content type (e.g. "image/png", "application/pdf"). */
  contentType: string;
  /** File size in bytes. */
  size: number;
  /** Classified file type (image, document, audio, video, etc.). */
  type: FileType;
  /** Alternative text for accessibility. */
  alt: string;
  /** Caption or description. */
  caption: string;
  /** S3 object key / file path (replaces DbFile.FilePath). */
  path: string;
  /** Image width in pixels (only for image type files). */
  width?: number;
  /** Image height in pixels (only for image type files). */
  height?: number;
  /** Permanent URL for the file (may require authentication). */
  url?: string;
  /** User ID of the creator (replaces DbFile.CreatedBy). */
  createdBy: string;
  /** ISO 8601 timestamp when the file was created (replaces DbFile.CreatedOn). */
  createdOn: string;
  /** User ID of the last modifier (replaces DbFile.LastModifiedBy). */
  lastModifiedBy?: string;
  /** ISO 8601 timestamp of last modification (replaces DbFile.LastModificationDate). */
  lastModifiedOn?: string;
}

/**
 * Paginated file list response from GET /v1/files.
 */
export interface FileListResponse {
  /** Array of file metadata records. */
  files: FileMetadata[];
  /** Total number of files matching the filters. */
  totalCount: number;
  /** Current page number. */
  page: number;
  /** Page size used for this request. */
  pageSize: number;
}

/**
 * Request body for POST /v1/files/upload-url.
 * Initiates presigned upload URL generation (step 1 of upload flow).
 */
export interface UploadUrlRequest {
  /** Original filename including extension. */
  filename: string;
  /** MIME content type of the file to be uploaded. */
  contentType: string;
  /** File size in bytes. */
  size: number;
}

/**
 * Response from POST /v1/files/upload-url.
 * Contains the presigned S3 URL and server-assigned file tracking ID.
 */
export interface UploadUrlResponse {
  /** Presigned S3 URL for PUT upload. */
  uploadUrl: string;
  /** Server-assigned file ID for tracking through the upload flow. */
  fileId: string;
  /** Optional form fields for multipart (POST-based) uploads. */
  fields?: Record<string, string>;
}

/**
 * Request body for POST /v1/files/confirm.
 * Confirms the S3 upload and creates the file metadata record.
 * Replaces UserFileService.CreateUserFile(path, alt, caption) where
 * the server performed MIME classification and transactional persistence.
 */
export interface ConfirmUploadRequest {
  /** File ID returned from the upload URL request (step 1). */
  fileId: string;
  /** Original filename including extension. */
  filename: string;
  /** Alternative text for accessibility. */
  alt?: string;
  /** Caption or description. */
  caption?: string;
  /** MIME content type. */
  contentType: string;
  /** File size in bytes. */
  size: number;
  /** Image width in pixels (for image files, computed client-side). */
  width?: number;
  /** Image height in pixels (for image files, computed client-side). */
  height?: number;
}

/**
 * Request body for PUT /v1/files/{id} metadata update.
 */
export interface UpdateFileMetadataRequest {
  /** Updated alternative text. */
  alt?: string;
  /** Updated caption or description. */
  caption?: string;
}

/**
 * Response from GET /v1/files/{id}/download-url.
 */
export interface FileDownloadUrlResponse {
  /** Presigned S3 download URL. */
  downloadUrl: string;
  /** ISO 8601 expiration timestamp for the presigned URL. */
  expiresAt: string;
}

/**
 * Parameters for the useUploadFile mutation.
 * Contains the presigned URL, file data, and optional progress callback.
 */
export interface UploadFileParams {
  /** Presigned S3 URL for PUT upload. */
  uploadUrl: string;
  /** File or Blob to upload. */
  file: File | Blob;
  /** MIME content type for the Content-Type header. */
  contentType: string;
  /**
   * Optional progress callback for upload tracking.
   * Called repeatedly during upload with current progress information.
   */
  onProgress?: (progress: UploadProgress) => void;
}

/**
 * Upload progress information for UX progress bar rendering.
 */
export interface UploadProgress {
  /** Bytes uploaded so far. */
  loaded: number;
  /** Total bytes to upload. */
  total: number;
  /** Upload progress as a percentage (0-100). */
  percentage: number;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Stale time for presigned download URLs: 5 minutes (300,000ms).
 * Presigned URLs typically expire in 15-60 minutes; 5 minutes ensures
 * we refresh well before expiration while still caching for UX.
 */
const DOWNLOAD_URL_STALE_TIME_MS = 5 * 60 * 1000;

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of files with optional filters.
 *
 * Replaces `UserFileService.GetFilesList(type, search, sort, page, pageSize)`.
 * Supports type filter (image/document/audio/video), search across
 * name/alt/caption fields (OR condition), and sort by creation date
 * (descending) or filename (ascending).
 *
 * @param params - Optional filter, sort, and pagination parameters.
 * @returns TanStack Query result with file list data, loading state, and refetch.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isFetching, isError, error, isSuccess, refetch } = useFiles({
 *   type: 'image',
 *   search: 'logo',
 *   sort: 1,
 *   page: 1,
 *   pageSize: 20,
 * });
 * const files = data?.object?.files ?? [];
 * const totalCount = data?.object?.totalCount ?? 0;
 * ```
 */
export function useFiles(params?: FileListParams) {
  return useQuery({
    queryKey: fileKeys.list(params),
    queryFn: async (): Promise<ApiResponse<FileListResponse>> => {
      const response = await get<FileListResponse>('/files/list', params as Record<string, unknown> | undefined);
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to fetch files';
        throw new Error(errorMessage);
      }
      return response;
    },
    /* Keep fetched data fresh for 5 seconds — short enough that
       mutations (upload / delete) followed by invalidateQueries()
       trigger a visible refetch almost immediately, while still
       avoiding unnecessary re-fetches on rapid component re-renders. */
    staleTime: 5_000,
    /* Disable automatic refetch on window focus — file lists change
       infrequently and explicit invalidation after mutations handles
       the common case. */
    refetchOnWindowFocus: false,
    /* Preserve previous data while refetching — prevents flash of
       "No files found" while TanStack Query re-validates after
       mutations (upload, delete). This mirrors the monolith's
       synchronous page model where data was always available. */
    placeholderData: keepPreviousData,
  });
}

/**
 * Fetches metadata for a single file by ID.
 *
 * Returns file metadata including filename, content type, size, dimensions,
 * path, alt text, and caption. Query is enabled only when a valid ID is
 * provided to prevent unnecessary API calls.
 *
 * @param id - The file GUID identifier (undefined to disable the query).
 * @returns TanStack Query result with single file metadata.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } = useFile(fileId);
 * const file = data?.object;
 * ```
 */
export function useFile(id: string | undefined) {
  return useQuery({
    queryKey: fileKeys.detail(id ?? ''),
    queryFn: async (): Promise<ApiResponse<FileMetadata>> => {
      const response = await get<FileMetadata>(`/files/${id}`);
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to fetch file metadata';
        throw new Error(errorMessage);
      }
      return response;
    },
    enabled: Boolean(id),
  });
}

/**
 * Fetches a presigned download URL for a specific file.
 *
 * Uses a 5-minute staleTime since presigned S3 URLs typically expire in
 * 15-60 minutes. This balances freshness with caching for UX. The query
 * is enabled only when a valid file ID is provided.
 *
 * @param id - The file GUID identifier (undefined to disable the query).
 * @returns TanStack Query result with presigned download URL and expiry.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } = useFileDownloadUrl(fileId);
 * const downloadUrl = data?.object?.downloadUrl;
 * ```
 */
export function useFileDownloadUrl(id: string | undefined) {
  return useQuery({
    queryKey: fileKeys.downloadUrl(id ?? ''),
    queryFn: async (): Promise<ApiResponse<FileDownloadUrlResponse>> => {
      const response = await get<FileDownloadUrlResponse>(
        `/files/${id}/download-url`,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to get download URL';
        throw new Error(errorMessage);
      }
      return response;
    },
    enabled: Boolean(id),
    staleTime: DOWNLOAD_URL_STALE_TIME_MS,
  });
}

// ---------------------------------------------------------------------------
// Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Requests a presigned S3 upload URL from the File Management service.
 *
 * This is **step 1** of the 3-step upload flow:
 *  1. **useRequestUploadUrl()** → Gets presigned S3 URL + fileId
 *  2. useUploadFile()           → Direct upload to S3
 *  3. useConfirmUpload()        → Create metadata record
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useRequestUploadUrl();
 * const result = await mutateAsync({
 *   filename: 'photo.jpg',
 *   contentType: 'image/jpeg',
 *   size: 1024000,
 * });
 * const { uploadUrl, fileId } = result.object!;
 * ```
 */
export function useRequestUploadUrl() {
  return useMutation({
    mutationFn: async (
      request: UploadUrlRequest,
    ): Promise<ApiResponse<UploadUrlResponse>> => {
      const response = await post<UploadUrlResponse>(
        '/files/upload-url',
        request,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to get upload URL';
        throw new Error(errorMessage);
      }
      return response;
    },
  });
}

/**
 * Uploads a file directly to S3 using a presigned URL.
 *
 * This is **step 2** of the 3-step upload flow. It uses a **direct
 * axios.put()** call to the presigned S3 URL — NOT through
 * `../api/client.ts` — because S3 presigned URL uploads bypass the
 * API Gateway and must NOT include JWT Bearer tokens or correlation-ID
 * headers from the centralized client interceptors.
 *
 * Provides XMLHttpRequest-based upload progress events (loaded/total bytes)
 * for UX progress bar rendering via the `onProgress` callback in
 * {@link UploadFileParams}.
 *
 * Replaces the monolith's server-side `Fs.Move` file transfer from
 * `UserFileService.CreateUserFile` with client-side S3 upload.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useUploadFile();
 * await mutateAsync({
 *   uploadUrl: presignedUrl,
 *   file: selectedFile,
 *   contentType: 'image/jpeg',
 *   onProgress: ({ percentage }) => setProgress(percentage),
 * });
 * ```
 */
export function useUploadFile() {
  return useMutation({
    mutationFn: async (params: UploadFileParams) => {
      const { uploadUrl, file, contentType, onProgress } = params;

      const response = await axios.put(uploadUrl, file, {
        headers: {
          'Content-Type': contentType,
        },
        onUploadProgress: (progressEvent) => {
          if (onProgress && progressEvent.total != null && progressEvent.total > 0) {
            const loaded = progressEvent.loaded;
            const total = progressEvent.total;
            const percentage = Math.round((loaded / total) * 100);
            onProgress({ loaded, total, percentage });
          }
        },
      });

      return response;
    },
  });
}

/**
 * Confirms a completed S3 upload and creates the file metadata record.
 *
 * This is **step 3** of the 3-step upload flow. The File Management
 * service performs server-side MIME type classification (replacing
 * `UserFileService.CreateUserFile`'s `MimeMapping.MimeUtility.GetMimeMapping`
 * logic and the document extension allowlist) and stores the file metadata
 * in DynamoDB.
 *
 * On success, invalidates all files list caches to reflect the newly
 * uploaded file.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useConfirmUpload();
 * await mutateAsync({
 *   fileId: 'abc-123',
 *   filename: 'photo.jpg',
 *   alt: 'Company logo',
 *   caption: 'Our official logo',
 *   contentType: 'image/jpeg',
 *   size: 1024000,
 *   width: 800,
 *   height: 600,
 * });
 * ```
 */
export function useConfirmUpload() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      request: ConfirmUploadRequest,
    ): Promise<ApiResponse<FileMetadata>> => {
      const response = await post<FileMetadata>('/files/confirm', request);
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to confirm upload';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: fileKeys.all });
    },
  });
}

/**
 * Updates file metadata (alt text and/or caption) for an existing file.
 *
 * On success, invalidates both the files list caches and the specific
 * file's detail cache to reflect the updated metadata everywhere.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useUpdateFileMetadata();
 * await mutateAsync({
 *   id: fileId,
 *   data: { alt: 'Updated alt text', caption: 'New caption' },
 * });
 * ```
 */
export function useUpdateFileMetadata() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (params: {
      /** The file GUID identifier. */
      id: string;
      /** The metadata fields to update. */
      data: UpdateFileMetadataRequest;
    }): Promise<ApiResponse<FileMetadata>> => {
      const response = await put<FileMetadata>(
        `/files/${params.id}`,
        params.data,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to update file metadata';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: fileKeys.all });
      queryClient.invalidateQueries({
        queryKey: fileKeys.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes a file from S3 storage and removes its metadata record from
 * DynamoDB. Replaces `DbFileRepository` file deletion logic which handled
 * cleanup across LO/filesystem/blob storage backends.
 *
 * On success, invalidates all files list caches so the deleted file
 * no longer appears.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, reset } =
 *   useDeleteFile();
 * await mutateAsync(fileId);
 * ```
 */
export function useDeleteFile() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      id: string,
    ): Promise<ApiResponse<BaseResponseModel>> => {
      const response = await del<BaseResponseModel>(`/files/${id}`);
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to delete file';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: fileKeys.all });
    },
  });
}

// ---------------------------------------------------------------------------
// Composite Upload Hook
// ---------------------------------------------------------------------------

/**
 * Combined hook that orchestrates the complete 3-step file upload flow:
 *  1. Request a presigned S3 upload URL (via File Management service)
 *  2. Upload the file directly to S3 with progress tracking (via axios)
 *  3. Confirm the upload and create the file metadata record
 *
 * This replaces the monolith's atomic `UserFileService.CreateUserFile()`
 * which performed: strip /fs prefix → verify exists → classify MIME →
 * compute image dimensions → generate permanent path → transactional
 * Fs.Move + RecMan.CreateRecord.
 *
 * Provides a single `upload` function and combined state (progress,
 * isUploading, isError, error) for simplified consumer usage without
 * needing to coordinate three separate mutations.
 *
 * @returns Upload function and combined state object.
 *
 * @example
 * ```tsx
 * const { upload, progress, isUploading, isError, error, reset } = useFileUpload();
 *
 * const handleUpload = async (file: File) => {
 *   try {
 *     const result = await upload({
 *       file,
 *       alt: 'My image',
 *       caption: 'Uploaded photo',
 *       width: 800,
 *       height: 600,
 *     });
 *     console.log('Uploaded file:', result.object);
 *   } catch (err) {
 *     console.error('Upload failed:', err);
 *   }
 * };
 *
 * // Render progress bar:
 * // <ProgressBar value={progress.percentage} />
 * ```
 */
export function useFileUpload() {
  const [progress, setProgress] = useState<UploadProgress>({
    loaded: 0,
    total: 0,
    percentage: 0,
  });
  const [isUploading, setIsUploading] = useState(false);
  const [isError, setIsError] = useState(false);
  const [error, setError] = useState<Error | null>(null);

  const requestUrlMutation = useRequestUploadUrl();
  const uploadFileMutation = useUploadFile();
  const confirmUploadMutation = useConfirmUpload();

  /**
   * Executes the complete 3-step upload flow for a single file.
   *
   * @param params - Upload parameters including the File object and
   *                 optional metadata (alt, caption, dimensions).
   * @returns The confirmed file metadata API response.
   * @throws Error if any step of the upload flow fails.
   */
  const upload = useCallback(
    async (params: {
      /** The File object to upload. */
      file: File;
      /** Alternative text for accessibility. */
      alt?: string;
      /** Caption or description. */
      caption?: string;
      /** Image width in pixels (for image files, computed client-side). */
      width?: number;
      /** Image height in pixels (for image files, computed client-side). */
      height?: number;
    }): Promise<ApiResponse<FileMetadata>> => {
      const { file, alt, caption, width, height } = params;

      setIsUploading(true);
      setIsError(false);
      setError(null);
      setProgress({ loaded: 0, total: 0, percentage: 0 });

      try {
        // Step 1: Request presigned upload URL from File Management service
        const urlResponse = await requestUrlMutation.mutateAsync({
          filename: file.name,
          contentType: file.type || 'application/octet-stream',
          size: file.size,
        });

        const uploadUrlData = urlResponse.object;
        if (!uploadUrlData) {
          throw new Error(
            'Upload URL response missing required data (uploadUrl, fileId)',
          );
        }

        // Step 2: Upload file directly to S3 using presigned URL
        // Uses axios.put() directly — NOT through ../api/client — to bypass
        // JWT and correlation-ID interceptors for S3 direct upload.
        await uploadFileMutation.mutateAsync({
          uploadUrl: uploadUrlData.uploadUrl,
          file,
          contentType: file.type || 'application/octet-stream',
          onProgress: (uploadProgress: UploadProgress) => {
            setProgress(uploadProgress);
          },
        });

        // Step 3: Confirm upload and create metadata record in DynamoDB
        // Server performs MIME classification (replacing UserFileService's
        // MimeMapping.MimeUtility.GetMimeMapping + document extension allowlist)
        const confirmResponse = await confirmUploadMutation.mutateAsync({
          fileId: uploadUrlData.fileId,
          filename: file.name,
          alt: alt ?? '',
          caption: caption ?? '',
          contentType: file.type || 'application/octet-stream',
          size: file.size,
          width,
          height,
        });

        setIsUploading(false);
        setProgress({
          loaded: file.size,
          total: file.size,
          percentage: 100,
        });

        return confirmResponse;
      } catch (err) {
        const uploadError =
          err instanceof Error ? err : new Error('Upload failed');
        setIsError(true);
        setError(uploadError);
        setIsUploading(false);
        throw uploadError;
      }
    },
    [requestUrlMutation, uploadFileMutation, confirmUploadMutation],
  );

  /**
   * Resets all upload state (progress, error, loading) and underlying
   * mutation states. Call this before starting a new upload or to clear
   * error state after a failed upload.
   */
  const reset = useCallback(() => {
    setProgress({ loaded: 0, total: 0, percentage: 0 });
    setIsUploading(false);
    setIsError(false);
    setError(null);
    requestUrlMutation.reset();
    uploadFileMutation.reset();
    confirmUploadMutation.reset();
  }, [requestUrlMutation, uploadFileMutation, confirmUploadMutation]);

  return {
    /** Executes the complete 3-step upload flow. */
    upload,
    /** Current upload progress (loaded, total, percentage). */
    progress,
    /** Whether an upload is currently in progress. */
    isUploading,
    /** Whether the last upload attempt resulted in an error. */
    isError,
    /** The error from the last failed upload attempt, or null. */
    error,
    /** Resets all upload state and underlying mutations. */
    reset,
  };
}
