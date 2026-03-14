// @vitest-environment jsdom

/**
 * @file useFiles.test.ts
 * @description Comprehensive Vitest unit tests for the 9 file management TanStack Query hooks
 * exported from src/hooks/useFiles.ts. These hooks replace the monolith's:
 *
 *   - **UserFileService.cs** — User file CRUD with MIME classification, GetFilesList
 *     (type/search/sort/page/pageSize filters), and CreateUserFile (transactional
 *     Fs.Move + RecordManager.CreateRecord in a single PostgreSQL transaction).
 *   - **DbFileRepository.cs** — LO/filesystem/blob file storage backends for
 *     create, read, update, delete operations on file binary content.
 *   - **DbFile.cs** — File metadata model (Id, FilePath, CreatedOn, ObjectId,
 *     CreatedBy, LastModifiedBy, LastModificationDate).
 *
 * The target architecture replaces the monolith's server-side file processing with
 * a 3-step S3 presigned URL upload flow:
 *   1. useRequestUploadUrl() → POST /v1/files/upload-url → presigned S3 URL + fileId
 *   2. useUploadFile()       → Direct axios PUT to presigned URL (NOT through API client)
 *   3. useConfirmUpload()    → POST /v1/files/confirm → metadata record creation
 *
 * Test suites cover:
 *   - useFiles           — paginated file list with type/search/sort/page filters
 *   - useFile            — single file metadata fetch by ID
 *   - useFileDownloadUrl — presigned download URL with 5-minute staleTime
 *   - useRequestUploadUrl — presigned upload URL generation (step 1)
 *   - useUploadFile       — direct S3 upload with progress tracking (step 2)
 *   - useConfirmUpload    — upload confirmation + metadata creation (step 3)
 *   - useUpdateFileMetadata — alt/caption metadata update
 *   - useDeleteFile        — file deletion (S3 + DynamoDB metadata)
 *   - Complete upload flow — end-to-end 3-step integration
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';

// ──────────────────────────────────────────────────────────────────────────────
// Module mocks — vi.mock calls are hoisted by Vitest before all imports
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Mock the centralized API client module.
 * All hooks except useUploadFile use these methods for HTTP calls:
 *   - get()  → useFiles (GET /v1/files), useFile (GET /v1/files/{id}),
 *              useFileDownloadUrl (GET /v1/files/{id}/download-url)
 *   - post() → useRequestUploadUrl (POST /v1/files/upload-url),
 *              useConfirmUpload (POST /v1/files/confirm)
 *   - put()  → useUpdateFileMetadata (PUT /v1/files/{id})
 *   - del()  → useDeleteFile (DELETE /v1/files/{id})
 */
vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
  default: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
    interceptors: {
      request: { use: vi.fn() },
      response: { use: vi.fn() },
    },
  },
}));

/**
 * Mock axios directly for the useUploadFile hook.
 * useUploadFile performs a direct axios.put() to the presigned S3 URL,
 * bypassing the centralized API client (which would add JWT headers and
 * correlation-IDs that interfere with S3 presigned URL validation).
 */
vi.mock('axios', () => ({
  default: {
    put: vi.fn(),
    create: vi.fn(() => ({
      interceptors: {
        request: { use: vi.fn() },
        response: { use: vi.fn() },
      },
    })),
  },
  __esModule: true,
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useFiles,
  useFile,
  useFileDownloadUrl,
  useRequestUploadUrl,
  useUploadFile,
  useConfirmUpload,
  useUpdateFileMetadata,
  useDeleteFile,
  useFileUpload,
} from '../../../src/hooks/useFiles';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';
import type { ApiResponse } from '../../../src/api/client';
import type { BaseResponseModel } from '../../../src/types/common';
import axios from 'axios';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);
const mockAxiosPut = vi.mocked(axios.put);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Mock file metadata object matching the FileMetadata interface from useFiles.ts.
 * Represents a PDF document file as returned by the File Management service.
 * Replaces DbFile.cs (Id, FilePath, CreatedOn, ObjectId, CreatedBy) with
 * S3-aware metadata including MIME type classification and alt/caption fields.
 */
const mockFileMetadata = {
  id: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
  filename: 'report.pdf',
  contentType: 'application/pdf',
  size: 1024000,
  type: 'document' as const,
  alt: 'Annual report',
  caption: 'Q4 2024 financial report',
  path: '/file/a1b2c3d4-e5f6-7890-abcd-ef1234567890/report.pdf',
  url: '/file/a1b2c3d4-e5f6-7890-abcd-ef1234567890/report.pdf',
  createdBy: 'eabd66fd-8de1-4d79-9674-447ee89921c2',
  createdOn: '2024-01-01T00:00:00Z',
  lastModifiedBy: 'eabd66fd-8de1-4d79-9674-447ee89921c2',
  lastModifiedOn: '2024-06-15T10:30:00Z',
};

/**
 * Mock image file metadata — includes width/height dimensions.
 * Replaces the monolith's `Helpers.GetImageDimension(bytes)` computation
 * (now computed client-side before the confirm step).
 */
const mockImageFileMetadata = {
  ...mockFileMetadata,
  id: 'img-1234-5678-abcd-ef1234567890',
  filename: 'photo.jpg',
  contentType: 'image/jpeg',
  size: 500000,
  type: 'image' as const,
  alt: 'Company photo',
  caption: 'Team photo 2024',
  path: '/file/img-1234-5678-abcd-ef1234567890/photo.jpg',
  url: '/file/img-1234-5678-abcd-ef1234567890/photo.jpg',
  width: 1920,
  height: 1080,
};

/**
 * Mock presigned upload URL response from POST /v1/files/upload-url.
 * Replaces the monolith's server-side file placement which was implicit
 * (no presigned URL needed since files were uploaded to the same server).
 */
const mockUploadUrlResponse = {
  uploadUrl: 'https://s3.localhost.localstack.cloud:4566/erp-files/uploads/new-file-guid?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=test',
  fileId: 'new-file-a1b2c3d4-e5f6-7890-abcd-ef1234567890',
  fields: {},
};

/**
 * Mock presigned download URL from GET /v1/files/{id}/download-url.
 * S3 presigned URLs typically expire in 15-60 minutes; we cache for 5 minutes.
 */
const mockDownloadUrlResponse = {
  downloadUrl: 'https://s3.localhost.localstack.cloud:4566/erp-files/file/a1b2c3d4/report.pdf?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Expires=900',
  expiresAt: new Date(Date.now() + 15 * 60 * 1000).toISOString(),
};

/**
 * Mock file list response matching FileListResponse interface.
 * Replaces UserFileService.GetFilesList which queried the user_file entity
 * via RecordManager.Find with type/search/sort filters.
 */
const mockFileListResponse = {
  files: [mockFileMetadata, mockImageFileMetadata],
  totalCount: 2,
  page: 1,
  pageSize: 20,
};

/**
 * Helper: Creates a success API response envelope matching ApiResponse<T>.
 * Mirrors the monolith's BaseResponseModel (success, errors, message,
 * timestamp, hash, accessWarnings) envelope pattern.
 */
function createSuccessResponse<T>(data: T): ApiResponse<T> {
  return {
    success: true,
    errors: [],
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: 'Success',
    object: data,
    hash: undefined,
  };
}

/**
 * Helper: Creates an error API response envelope.
 * Matches the monolith's DoBadRequestResponse pattern where success=false
 * and structured errors are included.
 */
function createErrorResponse(message: string, key?: string): ApiResponse<never> {
  return {
    success: false,
    errors: [
      {
        key: key ?? 'general',
        value: '',
        message,
      },
    ],
    statusCode: 400,
    timestamp: new Date().toISOString(),
    message,
    object: undefined as never,
    hash: undefined,
  };
}

// ──────────────────────────────────────────────────────────────────────────────
// Helper utilities
// ──────────────────────────────────────────────────────────────────────────────

/** Creates a fresh QueryClient with retries disabled for deterministic tests. */
function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

/**
 * Creates a React wrapper component that provides QueryClientProvider context.
 * Uses React.createElement instead of JSX since this is a .ts (not .tsx) file.
 * All 9 file hooks require QueryClientProvider to access the shared QueryClient.
 */
function createWrapper(queryClient?: QueryClient) {
  const client = queryClient ?? createTestQueryClient();
  return function TestQueryClientWrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client }, children);
  };
}

/**
 * Creates a mock File object for upload testing.
 * Replaces the monolith's server-side file handling where files were uploaded
 * to a temp directory and then moved via Fs.Move.
 */
function createMockFile(
  name: string,
  type: string,
  size: number,
): File {
  const content = new Uint8Array(Math.min(size, 64));
  const blob = new Blob([content], { type });
  return new File([blob], name, { type, lastModified: Date.now() });
}

// ──────────────────────────────────────────────────────────────────────────────
// Test lifecycle
// ──────────────────────────────────────────────────────────────────────────────

let queryClient: QueryClient;
let wrapper: ({ children }: { children: ReactNode }) => ReturnType<typeof createElement>;

beforeEach(() => {
  vi.clearAllMocks();
  queryClient = createTestQueryClient();
  wrapper = createWrapper(queryClient);
});

afterEach(() => {
  queryClient.clear();
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 1: useFiles(params?) — Paginated file listing with filters
// ══════════════════════════════════════════════════════════════════════════════

describe('useFiles', () => {
  /**
   * Tests basic file listing fetch (replaces UserFileService.GetFilesList
   * default call with no filters).
   */
  it('should fetch files list', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockFileListResponse) as never,
    );

    const { result } = renderHook(() => useFiles(), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/files/list', undefined);
    expect(result.current.data?.object?.files).toHaveLength(2);
    expect(result.current.data?.object?.totalCount).toBe(2);
    expect(result.current.data?.object?.page).toBe(1);
    expect(result.current.data?.object?.pageSize).toBe(20);
  });

  /**
   * Tests filtering by file type classification (image, document, video, etc.).
   * Mirrors the monolith's UserFileService.GetFilesList type parameter which
   * filtered via EntityQuery.QueryContains("type", type).
   */
  it('should filter by file type', async () => {
    const imageOnlyResponse = {
      files: [mockImageFileMetadata],
      totalCount: 1,
      page: 1,
      pageSize: 20,
    };
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(imageOnlyResponse) as never,
    );

    const { result } = renderHook(
      () => useFiles({ type: 'image' }),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/files/list', { type: 'image' });
    expect(result.current.data?.object?.files).toHaveLength(1);
    expect(result.current.data?.object?.files[0].type).toBe('image');
  });

  /**
   * Tests search text filter (OR across name, alt, caption fields).
   * Replaces the monolith's EntityQuery.QueryOR(
   *   QueryContains("name", search),
   *   QueryContains("alt", search),
   *   QueryContains("caption", search)
   * ) pattern from UserFileService.GetFilesList.
   */
  it('should filter by search text', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse({
        files: [mockFileMetadata],
        totalCount: 1,
        page: 1,
        pageSize: 20,
      }) as never,
    );

    const { result } = renderHook(
      () => useFiles({ search: 'report' }),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/files/list', { search: 'report' });
  });

  /**
   * Tests sorting options matching the monolith's sort parameter:
   *   1 = created_on DESC (QuerySortType.Descending — newest first)
   *   2 = name ASC (QuerySortType.Ascending — alphabetical)
   */
  it('should support sorting', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockFileListResponse) as never,
    );

    const { result } = renderHook(
      () => useFiles({ sort: 2 }),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/files/list', { sort: 2 });
  });

  /**
   * Tests pagination parameters (page and pageSize).
   * Replaces the monolith's skipCount = (page-1)*pageSize calculation
   * in UserFileService.GetFilesList.
   */
  it('should handle pagination', async () => {
    const page2Response = {
      files: [mockFileMetadata],
      totalCount: 25,
      page: 2,
      pageSize: 10,
    };
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(page2Response) as never,
    );

    const { result } = renderHook(
      () => useFiles({ page: 2, pageSize: 10 }),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/files/list', { page: 2, pageSize: 10 });
    expect(result.current.data?.object?.page).toBe(2);
    expect(result.current.data?.object?.totalCount).toBe(25);
  });

  /**
   * Tests combined filter parameters (type + search + sort + pagination).
   */
  it('should support combined filters', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockFileListResponse) as never,
    );

    const params = {
      type: 'document' as const,
      search: 'report',
      sort: 1 as const,
      page: 1,
      pageSize: 20,
    };

    const { result } = renderHook(
      () => useFiles(params),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/files/list', params);
  });

  /**
   * Tests error handling when the API returns success: false.
   * Mirrors the monolith's response.Success check and throw pattern.
   */
  it('should handle API errors', async () => {
    mockGet.mockResolvedValueOnce(
      createErrorResponse('Failed to load files') as never,
    );

    const { result } = renderHook(() => useFiles(), { wrapper });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error).toBeInstanceOf(Error);
    expect((result.current.error as Error).message).toBe('Failed to load files');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 2: useFile(id) — Single file metadata fetch
// ══════════════════════════════════════════════════════════════════════════════

describe('useFile', () => {
  /**
   * Tests fetching single file metadata by ID.
   * Replaces the monolith's RecordManager.Find for a specific file record.
   */
  it('should fetch file metadata by ID', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockFileMetadata) as never,
    );

    const { result } = renderHook(
      () => useFile(mockFileMetadata.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(`/files/${mockFileMetadata.id}`);
    expect(result.current.data?.object?.id).toBe(mockFileMetadata.id);
    expect(result.current.data?.object?.filename).toBe('report.pdf');
    expect(result.current.data?.object?.contentType).toBe('application/pdf');
    expect(result.current.data?.object?.size).toBe(1024000);
  });

  /**
   * Tests that image files include width/height dimensions.
   * Replaces the monolith's Helpers.GetImageDimension(tempFile.GetBytes())
   * which computed dimensions server-side during CreateUserFile.
   */
  it('should include file dimensions for images', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockImageFileMetadata) as never,
    );

    const { result } = renderHook(
      () => useFile(mockImageFileMetadata.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const file = result.current.data?.object;
    expect(file?.width).toBe(1920);
    expect(file?.height).toBe(1080);
    expect(file?.type).toBe('image');
  });

  /**
   * Tests that the query is disabled when id is undefined.
   * Prevents unnecessary API calls when the file ID is not yet known.
   */
  it('should not fetch when id is undefined', async () => {
    const { result } = renderHook(
      () => useFile(undefined),
      { wrapper },
    );

    // Query should remain in pending state (not fetching)
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });

  /**
   * Tests error handling for single file fetch.
   */
  it('should handle file not found error', async () => {
    mockGet.mockResolvedValueOnce(
      createErrorResponse('File not found', 'file_id') as never,
    );

    const { result } = renderHook(
      () => useFile('non-existent-id'),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect((result.current.error as Error).message).toBe('File not found');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 3: useFileDownloadUrl(id) — Presigned download URL
// ══════════════════════════════════════════════════════════════════════════════

describe('useFileDownloadUrl', () => {
  /**
   * Tests presigned download URL generation.
   * This replaces the monolith's direct file serving through DbFile.GetContentStream()
   * which read from LO/filesystem/blob backends. The target uses S3 presigned URLs
   * for direct browser-to-S3 downloads.
   */
  it('should get presigned download URL', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockDownloadUrlResponse) as never,
    );

    const { result } = renderHook(
      () => useFileDownloadUrl(mockFileMetadata.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      `/files/${mockFileMetadata.id}/download-url`,
    );
    expect(result.current.data?.object?.downloadUrl).toContain(
      's3.localhost.localstack.cloud',
    );
    expect(result.current.data?.object?.expiresAt).toBeDefined();
  });

  /**
   * Tests that the download URL query uses a staleTime of 5 minutes (300,000ms).
   * Presigned S3 URLs typically expire in 15-60 minutes; caching for 5 minutes
   * prevents excessive URL regeneration while ensuring freshness.
   *
   * Verifies by checking that the query state is still fresh immediately after
   * the first successful fetch (within the 5-minute window).
   */
  it('should use staleTime of 5 minutes', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockDownloadUrlResponse) as never,
    );

    const { result } = renderHook(
      () => useFileDownloadUrl(mockFileMetadata.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Data should not be stale immediately after fetching (within 5-min window)
    expect(result.current.isStale).toBe(false);

    // GET should only have been called once (not refetched)
    expect(mockGet).toHaveBeenCalledTimes(1);
  });

  /**
   * Tests that the query is disabled when id is undefined.
   */
  it('should not fetch when id is undefined', async () => {
    const { result } = renderHook(
      () => useFileDownloadUrl(undefined),
      { wrapper },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 4: useRequestUploadUrl — Presigned upload URL (Step 1)
// ══════════════════════════════════════════════════════════════════════════════

describe('useRequestUploadUrl', () => {
  /**
   * Tests requesting a presigned upload URL (step 1 of the 3-step upload flow).
   * This is the first step that replaces the monolith's implicit server-side
   * file placement (files were uploaded to the same server process).
   */
  it('should request presigned upload URL', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockUploadUrlResponse) as never,
    );

    const { result } = renderHook(() => useRequestUploadUrl(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        filename: 'photo.jpg',
        contentType: 'image/jpeg',
        size: 500000,
      });
    });

    expect(mockPost).toHaveBeenCalledWith('/files/upload-url', {
      filename: 'photo.jpg',
      contentType: 'image/jpeg',
      size: 500000,
    });
  });

  /**
   * Tests that the response contains both fileId and uploadUrl.
   * These are required for step 2 (upload to S3) and step 3 (confirm).
   */
  it('should return fileId and uploadUrl', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockUploadUrlResponse) as never,
    );

    const { result } = renderHook(() => useRequestUploadUrl(), { wrapper });

    let response: Awaited<ReturnType<typeof result.current.mutateAsync>>;
    await act(async () => {
      response = await result.current.mutateAsync({
        filename: 'photo.jpg',
        contentType: 'image/jpeg',
        size: 500000,
      });
    });

    expect(response!.object?.fileId).toBe(mockUploadUrlResponse.fileId);
    expect(response!.object?.uploadUrl).toContain('s3.localhost.localstack.cloud');
    expect(response!.object?.fields).toBeDefined();
  });

  /**
   * Tests error handling when the server rejects the upload URL request.
   * For example, file size exceeding maximum or invalid content type.
   */
  it('should handle upload URL request errors', async () => {
    mockPost.mockResolvedValueOnce(
      createErrorResponse('File size exceeds maximum allowed (50MB)', 'size') as never,
    );

    const { result } = renderHook(() => useRequestUploadUrl(), { wrapper });

    await expect(
      act(async () => {
        await result.current.mutateAsync({
          filename: 'huge-file.bin',
          contentType: 'application/octet-stream',
          size: 100_000_000,
        });
      }),
    ).rejects.toThrow('File size exceeds maximum allowed (50MB)');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 5: useUploadFile — Direct S3 upload (Step 2)
// ══════════════════════════════════════════════════════════════════════════════

describe('useUploadFile', () => {
  /**
   * Tests direct file upload to S3 presigned URL.
   * This is step 2 of the 3-step flow. Critically, this uses axios.put()
   * directly — NOT through the centralized API client — because S3
   * presigned URL uploads must NOT include JWT Bearer tokens or
   * X-Correlation-ID headers from the client interceptors.
   * Replaces the monolith's server-side Fs.Move from temp to permanent path.
   */
  it('should upload file to presigned URL', async () => {
    mockAxiosPut.mockResolvedValueOnce({ status: 200, data: '' });

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const presignedUrl = mockUploadUrlResponse.uploadUrl;

    const { result } = renderHook(() => useUploadFile(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        uploadUrl: presignedUrl,
        file: mockFile,
        contentType: 'image/jpeg',
      });
    });

    expect(mockAxiosPut).toHaveBeenCalledTimes(1);
    expect(mockAxiosPut).toHaveBeenCalledWith(
      presignedUrl,
      mockFile,
      expect.objectContaining({
        headers: { 'Content-Type': 'image/jpeg' },
      }),
    );

    // Verify the API client was NOT used (direct S3 upload bypasses it)
    expect(mockPut).not.toHaveBeenCalled();
    expect(mockPost).not.toHaveBeenCalled();
  });

  /**
   * Tests upload progress tracking via onUploadProgress callback.
   * This replaces the monolith's lack of progress reporting during
   * server-side file move (Fs.Move was synchronous and opaque to the client).
   */
  it('should support progress tracking', async () => {
    // Simulate axios calling onUploadProgress during upload
    mockAxiosPut.mockImplementationOnce(
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      async (_url: string, _data: unknown, config?: any) => {
        if (config?.onUploadProgress) {
          // Simulate progress events: 25%, 50%, 75%, 100%
          config.onUploadProgress({ loaded: 125000, total: 500000 });
          config.onUploadProgress({ loaded: 250000, total: 500000 });
          config.onUploadProgress({ loaded: 375000, total: 500000 });
          config.onUploadProgress({ loaded: 500000, total: 500000 });
        }
        return { status: 200, data: '' };
      },
    );

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const progressEvents: Array<{ loaded: number; total: number; percentage: number }> = [];

    const { result } = renderHook(() => useUploadFile(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        uploadUrl: mockUploadUrlResponse.uploadUrl,
        file: mockFile,
        contentType: 'image/jpeg',
        onProgress: (progress) => {
          progressEvents.push({ ...progress });
        },
      });
    });

    // Verify progress events were received
    expect(progressEvents.length).toBeGreaterThan(0);

    // Check that the last event has correct percentage
    const lastEvent = progressEvents[progressEvents.length - 1];
    expect(lastEvent.percentage).toBe(100);
    expect(lastEvent.loaded).toBe(500000);
    expect(lastEvent.total).toBe(500000);
  });

  /**
   * Tests error handling during S3 upload (network error, timeout, etc.).
   * This replaces the monolith's server-side Fs.Move exception handling
   * where IOException or "File move from temp folder failed" was thrown.
   */
  it('should handle upload failure', async () => {
    mockAxiosPut.mockRejectedValueOnce(new Error('Network Error'));

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useUploadFile(), { wrapper });

    try {
      await act(async () => {
        await result.current.mutateAsync({
          uploadUrl: mockUploadUrlResponse.uploadUrl,
          file: mockFile,
          contentType: 'image/jpeg',
        });
      });
    } catch {
      // Expected — mutateAsync re-throws the rejection
    }

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });
  });

  /**
   * Tests that Content-Type header matches the file's MIME type.
   * Critical for S3 to correctly serve the file with the right MIME type.
   */
  it('should set correct Content-Type header', async () => {
    mockAxiosPut.mockResolvedValueOnce({ status: 200, data: '' });

    const mockPdf = createMockFile('document.pdf', 'application/pdf', 1024000);
    const { result } = renderHook(() => useUploadFile(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        uploadUrl: mockUploadUrlResponse.uploadUrl,
        file: mockPdf,
        contentType: 'application/pdf',
      });
    });

    expect(mockAxiosPut).toHaveBeenCalledWith(
      expect.any(String),
      mockPdf,
      expect.objectContaining({
        headers: { 'Content-Type': 'application/pdf' },
      }),
    );
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 6: useConfirmUpload — Upload confirmation (Step 3)
// ══════════════════════════════════════════════════════════════════════════════

describe('useConfirmUpload', () => {
  /**
   * Tests confirming an upload with full metadata.
   * This replaces the monolith's transactional CreateUserFile which
   * performed Fs.Move + RecordManager.CreateRecord in a single
   * PostgreSQL transaction (with rollback on failure).
   */
  it('should confirm upload with metadata', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockImageFileMetadata) as never,
    );

    const { result } = renderHook(() => useConfirmUpload(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        fileId: 'new-file-a1b2c3d4-e5f6-7890-abcd-ef1234567890',
        filename: 'photo.jpg',
        alt: 'Company photo',
        caption: 'Team photo 2024',
        contentType: 'image/jpeg',
        size: 500000,
        width: 1920,
        height: 1080,
      });
    });

    expect(mockPost).toHaveBeenCalledWith('/files/confirm', {
      fileId: 'new-file-a1b2c3d4-e5f6-7890-abcd-ef1234567890',
      filename: 'photo.jpg',
      alt: 'Company photo',
      caption: 'Team photo 2024',
      contentType: 'image/jpeg',
      size: 500000,
      width: 1920,
      height: 1080,
    });
  });

  /**
   * Tests that the files query cache is invalidated on successful confirm.
   * This ensures the newly uploaded file appears in file lists immediately
   * without waiting for stale cache expiration.
   */
  it('should invalidate files query on success', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockImageFileMetadata) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useConfirmUpload(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        fileId: 'new-file-guid',
        filename: 'photo.jpg',
        contentType: 'image/jpeg',
        size: 500000,
      });
    });

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['files'],
      }),
    );

    invalidateSpy.mockRestore();
  });

  /**
   * Tests that contentType is sent to the server for MIME validation.
   * The monolith's MimeMapping.MimeUtility.GetMimeMapping + document
   * extension allowlist (.doc, .docx, .odt, .rtf, .txt, .pdf, .html,
   * .htm, .ppt, .pptx, .xls, .xlsx, .ods, .odp) is now handled
   * server-side during the confirm step.
   */
  it('should handle MIME classification server-side', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse({
        ...mockFileMetadata,
        contentType: 'application/pdf',
        type: 'document',
      }) as never,
    );

    const { result } = renderHook(() => useConfirmUpload(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        fileId: 'new-file-guid',
        filename: 'report.pdf',
        contentType: 'application/pdf',
        size: 1024000,
      });
    });

    // Verify contentType was sent to server (server performs MIME classification)
    expect(mockPost).toHaveBeenCalledWith(
      '/files/confirm',
      expect.objectContaining({
        contentType: 'application/pdf',
      }),
    );
  });

  /**
   * Tests error handling during confirm step.
   */
  it('should handle confirm errors', async () => {
    mockPost.mockResolvedValueOnce(
      createErrorResponse('Failed to confirm upload') as never,
    );

    const { result } = renderHook(() => useConfirmUpload(), { wrapper });

    await expect(
      act(async () => {
        await result.current.mutateAsync({
          fileId: 'invalid-file-guid',
          filename: 'photo.jpg',
          contentType: 'image/jpeg',
          size: 500000,
        });
      }),
    ).rejects.toThrow('Failed to confirm upload');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 7: useUpdateFileMetadata — Alt/caption metadata update
// ══════════════════════════════════════════════════════════════════════════════

describe('useUpdateFileMetadata', () => {
  /**
   * Tests updating file alt text and caption.
   * These fields have no direct monolith equivalent in DbFile.cs but
   * were stored as user_file entity record fields managed by RecordManager.
   */
  it('should update file alt/caption', async () => {
    const updatedFile = {
      ...mockFileMetadata,
      alt: 'Updated alt text',
      caption: 'Updated caption',
    };
    mockPut.mockResolvedValueOnce(
      createSuccessResponse(updatedFile) as never,
    );

    const { result } = renderHook(() => useUpdateFileMetadata(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        id: mockFileMetadata.id,
        data: { alt: 'Updated alt text', caption: 'Updated caption' },
      });
    });

    expect(mockPut).toHaveBeenCalledWith(
      `/files/${mockFileMetadata.id}`,
      { alt: 'Updated alt text', caption: 'Updated caption' },
    );
  });

  /**
   * Tests that both the files list cache and the specific file detail cache
   * are invalidated on successful update. This ensures consistency across
   * list views and detail views.
   */
  it('should invalidate files list and specific file', async () => {
    const updatedFile = {
      ...mockFileMetadata,
      alt: 'New alt',
      caption: 'New caption',
    };
    mockPut.mockResolvedValueOnce(
      createSuccessResponse(updatedFile) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateFileMetadata(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        id: mockFileMetadata.id,
        data: { alt: 'New alt', caption: 'New caption' },
      });
    });

    // Should invalidate the file list cache (all files queries)
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['files'],
      }),
    );

    // Should invalidate the specific file detail cache
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['files', 'detail', mockFileMetadata.id],
      }),
    );

    invalidateSpy.mockRestore();
  });

  /**
   * Tests error handling for metadata update.
   */
  it('should handle update errors', async () => {
    mockPut.mockResolvedValueOnce(
      createErrorResponse('Failed to update file metadata') as never,
    );

    const { result } = renderHook(() => useUpdateFileMetadata(), { wrapper });

    await expect(
      act(async () => {
        await result.current.mutateAsync({
          id: 'non-existent-id',
          data: { alt: 'New alt' },
        });
      }),
    ).rejects.toThrow('Failed to update file metadata');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 8: useDeleteFile — File deletion (S3 + DynamoDB metadata)
// ══════════════════════════════════════════════════════════════════════════════

describe('useDeleteFile', () => {
  /**
   * Tests file deletion by ID.
   * Replaces the monolith's DbFileRepository deletion which handled
   * cleanup across LO (pg_largeobject), filesystem, and blob storage
   * backends. The target deletes from S3 + DynamoDB metadata record.
   */
  it('should delete file', async () => {
    const deleteResponse: ApiResponse<BaseResponseModel> = {
      success: true,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: 'File deleted successfully',
      object: {
        success: true,
        errors: [],
        message: 'File deleted successfully',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      },
    };
    mockDel.mockResolvedValueOnce(deleteResponse as never);

    const { result } = renderHook(() => useDeleteFile(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync(mockFileMetadata.id);
    });

    expect(mockDel).toHaveBeenCalledWith(`/files/${mockFileMetadata.id}`);
  });

  /**
   * Tests that the files query cache is invalidated on successful deletion.
   * This ensures the deleted file disappears from list views immediately.
   */
  it('should invalidate files query', async () => {
    const deleteResponse: ApiResponse<BaseResponseModel> = {
      success: true,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: 'Deleted',
      object: {
        success: true,
        errors: [],
        message: 'Deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      },
    };
    mockDel.mockResolvedValueOnce(deleteResponse as never);

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteFile(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync(mockFileMetadata.id);
    });

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['files'],
      }),
    );

    invalidateSpy.mockRestore();
  });

  /**
   * Tests error handling for file deletion.
   */
  it('should handle delete errors', async () => {
    mockDel.mockResolvedValueOnce(
      createErrorResponse('Failed to delete file') as never,
    );

    const { result } = renderHook(() => useDeleteFile(), { wrapper });

    await expect(
      act(async () => {
        await result.current.mutateAsync('non-existent-id');
      }),
    ).rejects.toThrow('Failed to delete file');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 9: useFileUpload — Complete 3-step composite upload hook
// ══════════════════════════════════════════════════════════════════════════════

describe('useFileUpload', () => {
  /**
   * Tests the composite upload hook which orchestrates all 3 steps.
   * This replaces the monolith's atomic UserFileService.CreateUserFile()
   * which was a single transaction: strip /fs prefix → verify exists →
   * classify MIME → compute image dimensions → generate permanent path →
   * transactional Fs.Move + RecMan.CreateRecord.
   */
  it('should provide upload function and state', () => {
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    expect(result.current.upload).toBeTypeOf('function');
    expect(result.current.progress).toEqual({ loaded: 0, total: 0, percentage: 0 });
    expect(result.current.isUploading).toBe(false);
    expect(result.current.isError).toBe(false);
    expect(result.current.error).toBeNull();
    expect(result.current.reset).toBeTypeOf('function');
  });

  /**
   * Tests the complete 3-step upload flow end-to-end:
   * 1. Request presigned URL (POST /v1/files/upload-url)
   * 2. Upload file to S3 (direct axios PUT)
   * 3. Confirm upload (POST /v1/files/confirm)
   */
  it('should execute complete 3-step upload flow', async () => {
    // Step 1 mock: Request presigned upload URL
    mockPost
      .mockResolvedValueOnce(
        createSuccessResponse(mockUploadUrlResponse) as never,
      )
      // Step 3 mock: Confirm upload with metadata
      .mockResolvedValueOnce(
        createSuccessResponse(mockImageFileMetadata) as never,
      );

    // Step 2 mock: Direct S3 upload
    mockAxiosPut.mockResolvedValueOnce({ status: 200, data: '' });

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    let uploadResult: Awaited<ReturnType<typeof result.current.upload>>;
    await act(async () => {
      uploadResult = await result.current.upload({
        file: mockFile,
        alt: 'Company photo',
        caption: 'Team photo 2024',
        width: 1920,
        height: 1080,
      });
    });

    // Verify step 1: presigned URL request
    expect(mockPost).toHaveBeenCalledWith('/files/upload-url', {
      filename: 'photo.jpg',
      contentType: 'image/jpeg',
      size: expect.any(Number),
    });

    // Verify step 2: direct S3 upload
    expect(mockAxiosPut).toHaveBeenCalledWith(
      mockUploadUrlResponse.uploadUrl,
      mockFile,
      expect.objectContaining({
        headers: { 'Content-Type': 'image/jpeg' },
      }),
    );

    // Verify step 3: confirm upload with metadata
    expect(mockPost).toHaveBeenCalledWith('/files/confirm', expect.objectContaining({
      fileId: mockUploadUrlResponse.fileId,
      filename: 'photo.jpg',
      alt: 'Company photo',
      caption: 'Team photo 2024',
      contentType: 'image/jpeg',
      width: 1920,
      height: 1080,
    }));

    // Verify final state
    expect(result.current.isUploading).toBe(false);
    expect(result.current.isError).toBe(false);
    expect(uploadResult!.object).toBeDefined();
  });

  /**
   * Tests progress tracking during the composite upload flow.
   */
  it('should track upload progress', async () => {
    // Step 1 mock
    mockPost
      .mockResolvedValueOnce(
        createSuccessResponse(mockUploadUrlResponse) as never,
      )
      // Step 3 mock
      .mockResolvedValueOnce(
        createSuccessResponse(mockImageFileMetadata) as never,
      );

    // Step 2 mock with progress simulation
    mockAxiosPut.mockImplementationOnce(
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      async (_url: string, _data: unknown, config?: any) => {
        if (config?.onUploadProgress) {
          config.onUploadProgress({ loaded: 250000, total: 500000 });
          config.onUploadProgress({ loaded: 500000, total: 500000 });
        }
        return { status: 200, data: '' };
      },
    );

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    await act(async () => {
      await result.current.upload({
        file: mockFile,
      });
    });

    // After successful upload, progress should be at 100%
    expect(result.current.progress.percentage).toBe(100);
    expect(result.current.isUploading).toBe(false);
  });

  /**
   * Tests error handling when step 1 (request upload URL) fails.
   */
  it('should handle step 1 (request URL) failure', async () => {
    mockPost.mockResolvedValueOnce(
      createErrorResponse('Service unavailable') as never,
    );

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    try {
      await act(async () => {
        await result.current.upload({ file: mockFile });
      });
    } catch {
      // Expected to throw — composite hook re-throws after setting state
    }

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.isUploading).toBe(false);
    expect(result.current.error).toBeInstanceOf(Error);
  });

  /**
   * Tests error handling when step 2 (S3 upload) fails.
   */
  it('should handle step 2 (S3 upload) failure', async () => {
    // Step 1 succeeds
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockUploadUrlResponse) as never,
    );

    // Step 2 fails (network error)
    mockAxiosPut.mockRejectedValueOnce(new Error('Upload timeout'));

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    try {
      await act(async () => {
        await result.current.upload({ file: mockFile });
      });
    } catch {
      // Expected — composite hook re-throws after setting error state
    }

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.isUploading).toBe(false);
  });

  /**
   * Tests error handling when step 3 (confirm upload) fails.
   */
  it('should handle step 3 (confirm) failure', async () => {
    // Step 1 succeeds
    mockPost
      .mockResolvedValueOnce(
        createSuccessResponse(mockUploadUrlResponse) as never,
      )
      // Step 3 fails
      .mockResolvedValueOnce(
        createErrorResponse('Failed to create metadata record') as never,
      );

    // Step 2 succeeds
    mockAxiosPut.mockResolvedValueOnce({ status: 200, data: '' });

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    try {
      await act(async () => {
        await result.current.upload({ file: mockFile });
      });
    } catch {
      // Expected — composite hook re-throws after setting error state
    }

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.isUploading).toBe(false);
  });

  /**
   * Tests that reset() clears all upload state.
   */
  it('should reset state on reset()', async () => {
    // Trigger an error state first
    mockPost.mockResolvedValueOnce(
      createErrorResponse('Error') as never,
    );

    const mockFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    const { result } = renderHook(() => useFileUpload(), { wrapper });

    try {
      await act(async () => {
        await result.current.upload({ file: mockFile });
      });
    } catch {
      // Expected to throw
    }

    // Wait for async React state updates from the catch block
    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    // Reset should clear all state
    act(() => {
      result.current.reset();
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(false);
    });

    expect(result.current.progress).toEqual({ loaded: 0, total: 0, percentage: 0 });
    expect(result.current.isUploading).toBe(false);
    expect(result.current.error).toBeNull();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 10: Complete Upload Flow — End-to-end integration test
// ══════════════════════════════════════════════════════════════════════════════

describe('Complete upload flow', () => {
  /**
   * Tests the full 3-step upload workflow end-to-end using individual hooks:
   *  1. Call useRequestUploadUrl().mutate → GET presigned upload URL + fileId
   *  2. Call useUploadFile().mutate → PUT file to presigned URL on S3
   *  3. Call useConfirmUpload().mutate → POST confirm with metadata
   *  4. Verify files query cache invalidated
   *
   * This verifies the same workflow that useFileUpload orchestrates, but
   * using individual hooks to ensure they compose correctly.
   */
  it('should execute full upload flow with individual hooks', async () => {
    // Step 1 response: presigned URL and fileId
    const uploadUrlResp = createSuccessResponse({
      uploadUrl: 'https://s3.localhost.localstack.cloud:4566/erp-files/uploads/flow-test-id?presigned',
      fileId: 'flow-test-file-id',
      fields: {},
    });
    mockPost.mockResolvedValueOnce(uploadUrlResp as never);

    // Step 2 response: successful S3 upload
    mockAxiosPut.mockResolvedValueOnce({ status: 200, data: '' });

    // Step 3 response: confirmed metadata
    const confirmedFile = {
      ...mockImageFileMetadata,
      id: 'flow-test-file-id',
      filename: 'photo.jpg',
    };
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(confirmedFile) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    // Render all three hooks
    const { result: requestUrlResult } = renderHook(
      () => useRequestUploadUrl(),
      { wrapper },
    );
    const { result: uploadResult } = renderHook(
      () => useUploadFile(),
      { wrapper },
    );
    const { result: confirmResult } = renderHook(
      () => useConfirmUpload(),
      { wrapper },
    );

    // Step 1: Request presigned upload URL
    let urlResponse: Awaited<ReturnType<typeof requestUrlResult.current.mutateAsync>>;
    await act(async () => {
      urlResponse = await requestUrlResult.current.mutateAsync({
        filename: 'photo.jpg',
        contentType: 'image/jpeg',
        size: 500000,
      });
    });

    const uploadUrl = urlResponse!.object!.uploadUrl;
    const fileId = urlResponse!.object!.fileId;

    expect(uploadUrl).toContain('s3.localhost.localstack.cloud');
    expect(fileId).toBe('flow-test-file-id');

    // Step 2: Upload file directly to S3 using presigned URL
    const testFile = createMockFile('photo.jpg', 'image/jpeg', 500000);
    await act(async () => {
      await uploadResult.current.mutateAsync({
        uploadUrl,
        file: testFile,
        contentType: 'image/jpeg',
      });
    });

    expect(mockAxiosPut).toHaveBeenCalledWith(
      uploadUrl,
      testFile,
      expect.objectContaining({
        headers: { 'Content-Type': 'image/jpeg' },
      }),
    );

    // Step 3: Confirm upload with metadata (including image dimensions)
    await act(async () => {
      await confirmResult.current.mutateAsync({
        fileId,
        filename: 'photo.jpg',
        alt: 'Photo',
        caption: 'Team photo',
        contentType: 'image/jpeg',
        size: 500000,
        width: 1920,
        height: 1080,
      });
    });

    expect(mockPost).toHaveBeenCalledWith('/files/confirm', expect.objectContaining({
      fileId: 'flow-test-file-id',
      filename: 'photo.jpg',
      alt: 'Photo',
      caption: 'Team photo',
      contentType: 'image/jpeg',
      size: 500000,
      width: 1920,
      height: 1080,
    }));

    // Verify files query cache was invalidated after confirm
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['files'],
      }),
    );

    invalidateSpy.mockRestore();
  });

  /**
   * Tests the flow with a document file (non-image) to verify that
   * width/height are optional in the confirm step. Mirrors the
   * monolith's MIME classification where non-image files skip the
   * Helpers.GetImageDimension() call.
   */
  it('should handle non-image file upload flow', async () => {
    // Step 1
    mockPost.mockResolvedValueOnce(
      createSuccessResponse({
        uploadUrl: 'https://s3.localhost.localstack.cloud:4566/erp-files/uploads/doc-id?presigned',
        fileId: 'doc-file-id',
        fields: {},
      }) as never,
    );

    // Step 2
    mockAxiosPut.mockResolvedValueOnce({ status: 200, data: '' });

    // Step 3 — document file has no width/height
    const confirmedDoc = {
      ...mockFileMetadata,
      id: 'doc-file-id',
      filename: 'report.pdf',
      type: 'document' as const,
    };
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(confirmedDoc) as never,
    );

    const { result } = renderHook(() => useFileUpload(), { wrapper });

    const pdfFile = createMockFile('report.pdf', 'application/pdf', 1024000);
    let uploadResponse: Awaited<ReturnType<typeof result.current.upload>>;
    await act(async () => {
      uploadResponse = await result.current.upload({
        file: pdfFile,
      });
    });

    // Confirm was called without width/height
    const confirmCall = mockPost.mock.calls.find(
      (call) => call[0] === '/files/confirm',
    );
    expect(confirmCall).toBeDefined();
    expect((confirmCall![1] as Record<string, unknown>).width).toBeUndefined();
    expect((confirmCall![1] as Record<string, unknown>).height).toBeUndefined();

    expect(uploadResponse!.object).toBeDefined();
    expect(result.current.isUploading).toBe(false);
  });
});
