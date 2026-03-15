/**
 * File Upload/Download/Management API Module
 *
 * Typed API functions for file management operations in the WebVella ERP
 * microservices architecture. Routes to the File Management bounded-context
 * service which uses S3 for storage.
 *
 * Replaces WebApiController.cs file endpoints (lines 3252–4232):
 *   - File download (presigned S3 URL generation)
 *   - File upload (single & multi, temp & permanent)
 *   - File move (S3 copy-then-delete)
 *   - File delete
 *   - User file management (list, create, multi-upload)
 *
 * Route prefixes:
 *   - File operations:      /file-management/files/*
 *   - User file operations:  /file-management/user-files/*
 *
 * @module api/endpoints/files
 */

import { get, post, del } from '../client';
import type { ApiResponse } from '../client';
import apiClient from '../client';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Route prefix constants
// ---------------------------------------------------------------------------

/** Base path for general file operations (upload, download, move, delete). */
const FILE_BASE = '/file-management/files';

/** Base path for user-file-specific operations (list, create, multi-upload). */
const USER_FILE_BASE = '/file-management/user-files';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Query parameters for listing user files.
 *
 * Mirrors the query signature of the monolith's `GetUserFileList` endpoint
 * (WebApiController.cs line 3886) which delegates to
 * `UserFileService.GetFilesList(type, search, sort, page, pageSize)`.
 */
export interface UserFileListParams {
  /** File type filter (e.g. 'image', 'video', 'audio', 'document', 'other'). */
  type?: string;
  /** Free-text search across file names. */
  search?: string;
  /** Sort option identifier (1 = default sort). */
  sort?: number;
  /** 1-based page index for pagination. */
  page?: number;
  /** Number of items per page. Server default is 30. */
  pageSize?: number;
}

/**
 * Response payload returned by single-file and bulk-file upload operations.
 *
 * Maps to the FSResponse object returned by the monolith's `UploadFile`
 * endpoint (WebApiController.cs line 3327) which contains the file URL and
 * original file name after the file has been persisted to the temp folder.
 * In the target S3-backed architecture the `url` is the S3 object key.
 */
export interface FileUploadResponse {
  /** URL or S3 object key of the uploaded file. */
  url: string;
  /** Original file name as submitted by the client. */
  filename: string;
}

/**
 * Strongly-typed representation of a user-file entity record.
 *
 * Mirrors the `user_file` entity fields created by the monolith's
 * `UserFileService.CreateUserFile()` method which populates MIME type,
 * type classification, optional image dimensions, and metadata.
 */
export interface UserFile {
  /** Unique record identifier (GUID). */
  id: string;
  /** File display name (derived from original upload filename). */
  name: string;
  /** Permanent storage path (S3 key or relative path). */
  path: string;
  /** Classified file type ('image' | 'video' | 'audio' | 'document' | 'other'). */
  type: string;
  /** File size in bytes. */
  size: number;
  /** Alt-text for accessibility (optional, user-supplied). */
  alt?: string;
  /** Caption/description (optional, user-supplied). */
  caption?: string;
  /** Image pixel width (populated only for image files). */
  width?: number;
  /** Image pixel height (populated only for image files). */
  height?: number;
  /** ISO-8601 creation timestamp. */
  createdOn: string;
}

// ---------------------------------------------------------------------------
// File Operations — General
// ---------------------------------------------------------------------------

/**
 * Retrieve a download URL (presigned S3 URL) for the file at the given path.
 *
 * Replaces the monolith's `Download` endpoint (WebApiController.cs line 3252)
 * which served file bytes directly with MIME detection, 30-day cache headers,
 * and If-Modified-Since/304 support. In the target architecture the backend
 * generates a time-limited presigned S3 URL that the frontend can open or
 * redirect to.
 *
 * @param filePath - Relative storage path of the file (may contain nested
 *                   segments, e.g. "images/products/photo.jpg").
 * @returns API response containing the presigned download URL.
 */
export async function getFileDownloadUrl(
  filePath: string,
): Promise<ApiResponse<{ url: string }>> {
  return get<{ url: string }>(
    `${FILE_BASE}/${encodeURIComponent(filePath)}/download-url`,
  );
}

/**
 * Shared multipart/form-data upload helper.
 *
 * Extracts the common FormData construction, header setting, and response
 * unwrapping that is repeated across uploadFile(), uploadUserFilesMultiple(),
 * and uploadFilesMultiple().
 *
 * @param url       - The API endpoint to POST the form data to.
 * @param fieldName - The FormData field name for the file(s).
 * @param files     - One or more File objects to include in the upload.
 * @returns The unwrapped API response from the server.
 */
async function postMultipart<T>(
  url: string,
  fieldName: string,
  files: File | File[],
): Promise<ApiResponse<T>> {
  const formData = new FormData();
  const fileArray = Array.isArray(files) ? files : [files];
  for (const file of fileArray) {
    formData.append(fieldName, file);
  }

  const response = await apiClient.post<ApiResponse<T>>(
    url,
    formData,
    { headers: { 'Content-Type': 'multipart/form-data' } },
  );

  return response.data;
}

/**
 * Upload a single file to temporary storage.
 *
 * Replaces the monolith's `UploadFile` endpoint (WebApiController.cs line 3327)
 * which accepted an IFormFile, persisted it via `DbFileRepository.CreateTempFile()`
 * and returned `{ url, filename }` as an FSResponse.
 *
 * In the target architecture the file is uploaded to an S3 temp prefix. The
 * returned `url` is the S3 object key that can later be moved to a permanent
 * location via {@link moveFile} or committed via {@link createUserFile}.
 *
 * Uses the raw `apiClient` for `multipart/form-data` uploads rather than the
 * typed `post()` helper which sends JSON.
 *
 * @param file - The File object to upload.
 * @returns API response containing the upload URL and original filename.
 */
export async function uploadFile(
  file: File,
): Promise<ApiResponse<FileUploadResponse>> {
  return postMultipart<FileUploadResponse>(`${FILE_BASE}/upload`, 'file', file);
}

/**
 * Move (rename) a file from one storage path to another.
 *
 * Replaces the monolith's `MoveFile` endpoint (WebApiController.cs line 3347)
 * which accepted a JSON body `{ source, target, overwrite }` and performed a
 * filesystem move via `DbFileRepository`. In the target architecture this
 * translates to an S3 copy-then-delete operation.
 *
 * @param source    - Current storage path of the file.
 * @param target    - Desired new storage path.
 * @param overwrite - When `true`, overwrite any existing file at `target`.
 *                    Defaults to `false`.
 * @returns API response indicating success or failure.
 */
export async function moveFile(
  source: string,
  target: string,
  overwrite?: boolean,
): Promise<ApiResponse<void>> {
  return post<void>(`${FILE_BASE}/move`, {
    source,
    target,
    overwrite: overwrite ?? false,
  });
}

/**
 * Delete a file at the specified storage path.
 *
 * Replaces the monolith's `DeleteFile` endpoint (WebApiController.cs line 3370)
 * which accepted a wildcard path and removed the file from the filesystem.
 * In the target architecture the file is deleted from S3.
 *
 * @param filePath - Storage path of the file to delete.
 * @returns API response indicating success or failure.
 */
export async function deleteFile(
  filePath: string,
): Promise<ApiResponse<void>> {
  return del<void>(`${FILE_BASE}/${encodeURIComponent(filePath)}`);
}

// ---------------------------------------------------------------------------
// User-File Operations
// ---------------------------------------------------------------------------

/**
 * List user files with optional filtering and pagination.
 *
 * Replaces the monolith's `GetUserFileList` endpoint
 * (WebApiController.cs line 3886) which delegated to
 * `UserFileService.GetFilesList()` with type/search/sort/page/pageSize
 * parameters. The service filters `user_file` entity records, supports text
 * search across file names, and returns paginated results (default page
 * size: 30).
 *
 * @param params - Optional filter and pagination parameters.
 * @returns API response containing an array of user-file entity records.
 */
export async function getUserFileList(
  params?: UserFileListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(
    USER_FILE_BASE,
    params as Record<string, unknown> | undefined,
  );
}

/**
 * Create a user-file entity record from a previously uploaded temporary file.
 *
 * Replaces the monolith's `UploadUserFile` endpoint
 * (WebApiController.cs line 3906) which accepted `{ path, alt, caption }` and
 * delegated to `UserFileService.CreateUserFile()`. That service method:
 *   1. Moves the file from the temp folder to a permanent location.
 *   2. Detects MIME type and classifies the file type
 *      (image / video / audio / document / other).
 *   3. Extracts image dimensions when applicable.
 *   4. Creates a `user_file` entity record with all metadata.
 *
 * @param path    - Storage path of the previously uploaded temp file
 *                  (as returned by {@link uploadFile}).
 * @param alt     - Optional alt-text for accessibility.
 * @param caption - Optional caption / description.
 * @returns API response containing the newly created user-file entity record.
 */
export async function createUserFile(
  path: string,
  alt?: string,
  caption?: string,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(USER_FILE_BASE, { path, alt, caption });
}

/**
 * Upload multiple files and create user-file entity records for each.
 *
 * Replaces the monolith's `UploadUserFileMultiple` endpoint
 * (WebApiController.cs line 4041) which handled a list of IFormFile objects
 * in a single transaction:
 *   1. For each file: detect MIME type, classify type, extract image
 *      dimensions (width/height).
 *   2. Create a `user_file` entity record per file.
 *   3. Commit all records atomically within a single transaction.
 *
 * Uses the raw `apiClient` for `multipart/form-data` uploads.
 *
 * @param files - Array of File objects to upload.
 * @returns API response containing an array of created user-file entity
 *          records (one per uploaded file).
 */
export async function uploadUserFilesMultiple(
  files: File[],
): Promise<ApiResponse<EntityRecord[]>> {
  return postMultipart<EntityRecord[]>(`${USER_FILE_BASE}/upload-multiple`, 'files', files);
}

/**
 * Upload multiple files to temporary storage in bulk.
 *
 * Replaces the monolith's `UploadFileMultiple` endpoint
 * (WebApiController.cs line ~4134) which handled a list of IFormFile objects
 * in a single transaction, creating temp files for each and returning
 * metadata records containing the URL (S3 key) and original filename.
 *
 * Unlike {@link uploadUserFilesMultiple}, this does **not** create permanent
 * user-file entity records. The returned temp file URLs should be committed
 * later via {@link createUserFile} or moved via {@link moveFile}.
 *
 * Uses the raw `apiClient` for `multipart/form-data` uploads.
 *
 * @param files - Array of File objects to upload to temporary storage.
 * @returns API response containing an array of upload metadata records
 *          (url + filename per file).
 */
export async function uploadFilesMultiple(
  files: File[],
): Promise<ApiResponse<FileUploadResponse[]>> {
  return postMultipart<FileUploadResponse[]>(`${FILE_BASE}/upload-multiple`, 'files', files);
}
