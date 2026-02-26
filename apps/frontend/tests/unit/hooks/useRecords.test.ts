/**
 * @file useRecords.test.ts
 *
 * Comprehensive Vitest tests for the 11 record CRUD TanStack Query hooks
 * exported from `src/hooks/useRecords.ts`.
 *
 * These hooks replace the monolith's:
 *  - `RecordManager.cs`  — record CRUD with hooks, permission checks,
 *    relation-aware processing, and field normalisation
 *  - `ImportExportManager.cs` — CSV import/export pipeline
 *
 * Test suites cover:
 *  1. useRecords         — paginated record list
 *  2. useRecord          — single record fetch
 *  3. useRecordCount     — record count
 *  4. useRelatedRecords  — relation-based record list
 *  5. useCreateRecord    — create + cache invalidation
 *  6. useUpdateRecord    — update + optimistic update + rollback
 *  7. useDeleteRecord    — delete + cache invalidation
 *  8. useCreateManyToManyRelation — M2M bridge creation
 *  9. useRemoveManyToManyRelation — M2M bridge deletion
 * 10. useImportRecords   — CSV import via FormData
 * 11. useExportRecords   — CSV export with download URL
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React, { type ReactNode } from 'react';

// =====================================================================
// Mock API client — intercepts all HTTP calls from the hooks under test
// =====================================================================

vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
}));

import { get, post, put, del } from '../../../src/api/client';

const mockedGet = vi.mocked(get);
const mockedPost = vi.mocked(post);
const mockedPut = vi.mocked(put);
const mockedDel = vi.mocked(del);

// =====================================================================
// Hooks under test (11 named exports)
// =====================================================================

import {
  useRecords,
  useRecord,
  useRecordCount,
  useRelatedRecords,
  useCreateRecord,
  useUpdateRecord,
  useDeleteRecord,
  useCreateManyToManyRelation,
  useRemoveManyToManyRelation,
  useImportRecords,
  useExportRecords,
} from '../../../src/hooks/useRecords';

// =====================================================================
// Type-only imports for test fixtures
// =====================================================================

import type {
  EntityRecord,
  EntityRecordList,
  QueryResult,
  QueryResponse,
  QueryCountResponse,
  RecordResponse,
  RecordListResponse,
} from '../../../src/types/record';

import type {
  QueryObject,
  QuerySortObject,
} from '../../../src/types/filter';

import type { BaseResponseModel } from '../../../src/types/common';

// =====================================================================
// Constants — numeric values matching the source enums
// =====================================================================

/** QueryType.EQ = 0 */
const QUERY_TYPE_EQ = 0;
/** QueryType.AND = 6 */
const QUERY_TYPE_AND = 6;
/** QuerySortType.Ascending = 0 */
const SORT_TYPE_ASCENDING = 0;
/** QuerySortType.Descending = 1 */
const SORT_TYPE_DESCENDING = 1;

// =====================================================================
// Mock Fixtures
// =====================================================================

const mockRecord: EntityRecord = {
  id: 'a1000000-0000-0000-0000-000000000001',
  name: 'Acme Corp',
  email: 'info@acme.com',
  created_on: '2024-01-01T00:00:00Z',
};

const mockRecord2: EntityRecord = {
  id: 'a1000000-0000-0000-0000-000000000002',
  name: 'Globex Inc',
  email: 'hello@globex.com',
  created_on: '2024-01-15T00:00:00Z',
};

const mockFieldsMeta = [
  { name: 'id', label: 'ID', fieldType: 16 },
  { name: 'name', label: 'Name', fieldType: 18 },
  { name: 'email', label: 'Email', fieldType: 5 },
  { name: 'created_on', label: 'Created On', fieldType: 2 },
];

const mockQueryResult: QueryResult = {
  fieldsMeta: mockFieldsMeta as any,
  data: [mockRecord, mockRecord2],
};

const mockRecordList: EntityRecordList = {
  records: [mockRecord, mockRecord2],
  totalCount: 2,
};

const mockQuerySort: QuerySortObject[] = [
  { fieldName: 'name', sortType: SORT_TYPE_ASCENDING as any },
];

const mockQueryFilter: QueryObject = {
  queryType: QUERY_TYPE_EQ as any,
  fieldName: 'status',
  fieldValue: 'active',
  subQueries: [],
};

const mockRelationId = 'r1000000-0000-0000-0000-000000000001';

// =====================================================================
// Response Helpers
// =====================================================================

/**
 * Build a successful API response envelope matching `ApiResponse<T>`.
 */
function createSuccessResponse<T>(object: T): {
  success: BaseResponseModel['success'];
  errors: BaseResponseModel['errors'];
  message: BaseResponseModel['message'];
  timestamp: BaseResponseModel['timestamp'];
  hash: string | undefined;
  object: T;
  statusCode: number;
} {
  return {
    success: true,
    object,
    errors: [],
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: '',
    hash: 'response-hash',
  };
}

/**
 * Build an error response envelope for 400/403/404/500 scenarios.
 * The `assertApiSuccess` helper inside each hook checks `response.success`
 * and throws with the concatenated `errors[].message` values.
 */
function createErrorResponse(
  statusCode: number,
  errors: Array<{ key: string; value: string; message: string }>,
  message?: string,
) {
  return {
    success: false as const,
    object: undefined,
    errors,
    statusCode,
    timestamp: new Date().toISOString(),
    message: message || errors[0]?.message || 'Error',
    hash: undefined,
  };
}

// =====================================================================
// QueryClient Wrapper
// =====================================================================

let queryClient: QueryClient;

/**
 * Creates a React wrapper providing QueryClientProvider context.
 * Uses React.createElement (not JSX) because this is a .ts file.
 */
function createWrapper() {
  return function Wrapper({ children }: { children: ReactNode }) {
    return React.createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

// =====================================================================
// Setup / Teardown
// =====================================================================

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  vi.clearAllMocks();
});

afterEach(() => {
  queryClient.clear();
});

// #####################################################################
//  1. useRecords — Paginated record listing
//     Replaces RecordManager.Find(entityName, query) + hooks + permissions
// #####################################################################

describe('useRecords', () => {
  it('should fetch records for entity', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockQueryResult));

    const { result } = renderHook(() => useRecords('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/entities/account/records',
      undefined,
    );
    expect(result.current.data).toEqual(mockQueryResult);
    expect(result.current.data!.data).toHaveLength(2);
    expect(result.current.data!.fieldsMeta).toEqual(mockFieldsMeta);
  });

  it('should pass query parameters correctly', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockQueryResult));

    const params = {
      fields: 'id,name,email',
      sort: mockQuerySort,
      skip: 0,
      limit: 10,
      query: mockQueryFilter,
    };

    const { result } = renderHook(() => useRecords('account', params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/entities/account/records',
      {
        fields: 'id,name,email',
        query: JSON.stringify(mockQueryFilter),
        sort: JSON.stringify(mockQuerySort),
        skip: 0,
        limit: 10,
      },
    );
  });

  it('should handle empty results', async () => {
    const emptyResult: QueryResult = {
      fieldsMeta: mockFieldsMeta as any,
      data: [],
    };
    mockedGet.mockResolvedValueOnce(createSuccessResponse(emptyResult));

    const { result } = renderHook(() => useRecords('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data!.data).toEqual([]);
  });

  it('should include query params in query key for caching', async () => {
    const params1 = { fields: 'id,name', skip: 0, limit: 10 };
    const params2 = { fields: 'id,email', skip: 10, limit: 10 };

    mockedGet.mockResolvedValue(createSuccessResponse(mockQueryResult));

    const { result: result1 } = renderHook(() => useRecords('account', params1), {
      wrapper: createWrapper(),
    });
    const { result: result2 } = renderHook(() => useRecords('account', params2), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result1.current.isSuccess).toBe(true);
      expect(result2.current.isSuccess).toBe(true);
    });

    // Two distinct param sets → two separate API calls
    expect(mockedGet).toHaveBeenCalledTimes(2);
  });

  it('should use 30-second staleTime', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockQueryResult));

    const { result } = renderHook(() => useRecords('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Re-render with same params within staleTime — no new fetch
    renderHook(() => useRecords('account'), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should not fetch when entityName is empty', async () => {
    const { result } = renderHook(() => useRecords(''), {
      wrapper: createWrapper(),
    });

    // The query should remain in idle/disabled state
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should propagate API success=false as error', async () => {
    mockedGet.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'entity', value: 'bad_entity', message: 'Entity not found' },
      ]),
    );

    const { result } = renderHook(() => useRecords('bad_entity'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Entity not found');
  });

  it('should propagate server error (500)', async () => {
    mockedGet.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useRecords('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });
});

// #####################################################################
//  2. useRecord — Single record fetch
//     Replaces RecordManager.Find with id filter
// #####################################################################

describe('useRecord', () => {
  const recordId = 'a1000000-0000-0000-0000-000000000001';

  it('should fetch record by ID', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRecord));

    const { result } = renderHook(() => useRecord('account', recordId), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      `/entities/account/records/${recordId}`,
    );
    expect(result.current.data).toEqual(mockRecord);
    expect(result.current.data!.name).toBe('Acme Corp');
  });

  it('should not fetch when entityName is empty', async () => {
    const { result } = renderHook(() => useRecord('', recordId), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch when id is empty', async () => {
    const { result } = renderHook(() => useRecord('account', ''), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should propagate 404 not found error', async () => {
    mockedGet.mockResolvedValueOnce(
      createErrorResponse(404, [
        { key: 'record', value: 'missing-id', message: 'Record not found' },
      ]),
    );

    const { result } = renderHook(() => useRecord('account', 'missing-id'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Record not found');
  });

  it('should handle permission denied (403)', async () => {
    mockedGet.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: 'permission', value: 'CanRead', message: 'Access denied: CanRead permission required' },
      ]),
    );

    const { result } = renderHook(() => useRecord('account', recordId), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('Access denied');
  });

  it('should use staleTime of 30 seconds', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRecord));

    const { result } = renderHook(() => useRecord('account', recordId), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    renderHook(() => useRecord('account', recordId), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should URL-encode entityName and id in path', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRecord));

    const { result } = renderHook(
      () => useRecord('my entity', 'id with spaces'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/entities/my%20entity/records/id%20with%20spaces',
    );
  });
});

// #####################################################################
//  3. useRecordCount — Record count
//     Replaces RecordManager.Count(entityName, query)
// #####################################################################

describe('useRecordCount', () => {
  it('should return record count', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(42));

    const { result } = renderHook(() => useRecordCount('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/entities/account/records/count',
      undefined,
    );
    expect(result.current.data).toBe(42);
  });

  it('should pass query filter as JSON string', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(15));

    const { result } = renderHook(
      () => useRecordCount('account', mockQueryFilter),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/entities/account/records/count',
      { query: JSON.stringify(mockQueryFilter) },
    );
    expect(result.current.data).toBe(15);
  });

  it('should handle zero count', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(0));

    const { result } = renderHook(() => useRecordCount('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data).toBe(0);
  });

  it('should not fetch when entityName is empty', async () => {
    const { result } = renderHook(() => useRecordCount(''), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should use different cache keys for different queries', async () => {
    mockedGet.mockResolvedValue(createSuccessResponse(10));

    const query2: QueryObject = {
      queryType: QUERY_TYPE_AND as any,
      fieldName: '',
      fieldValue: '',
      subQueries: [mockQueryFilter],
    };

    const { result: r1 } = renderHook(
      () => useRecordCount('account', mockQueryFilter),
      { wrapper: createWrapper() },
    );
    const { result: r2 } = renderHook(
      () => useRecordCount('account', query2),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(r1.current.isSuccess).toBe(true);
      expect(r2.current.isSuccess).toBe(true);
    });

    expect(mockedGet).toHaveBeenCalledTimes(2);
  });

  it('should propagate server error', async () => {
    mockedGet.mockRejectedValueOnce(new Error('Server error'));

    const { result } = renderHook(() => useRecordCount('account'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Server error');
  });
});

// #####################################################################
//  4. useRelatedRecords — Related records via relation
//     Replaces $relation_name query pattern in RecordManager
// #####################################################################

describe('useRelatedRecords', () => {
  const entityName = 'account';
  const recordId = 'a1000000-0000-0000-0000-000000000001';
  const relationName = 'contacts';

  it('should fetch related records', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRecordList));

    const { result } = renderHook(
      () => useRelatedRecords(entityName, recordId, relationName),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      `/entities/account/records/${recordId}/relations/contacts`,
      undefined,
    );
    expect(result.current.data).toEqual(mockRecordList);
    expect(result.current.data!.records).toHaveLength(2);
    expect(result.current.data!.totalCount).toBe(2);
  });

  it('should pass pagination params', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockRecordList));

    const relatedParams = { fields: 'id,name', skip: 0, limit: 25 };

    const { result } = renderHook(
      () => useRelatedRecords(entityName, recordId, relationName, relatedParams),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      `/entities/account/records/${recordId}/relations/contacts`,
      { fields: 'id,name', skip: 0, limit: 25 },
    );
  });

  it('should include params in query key for caching', async () => {
    mockedGet.mockResolvedValue(createSuccessResponse(mockRecordList));

    const params1 = { skip: 0, limit: 10 };
    const params2 = { skip: 10, limit: 10 };

    const { result: r1 } = renderHook(
      () => useRelatedRecords(entityName, recordId, relationName, params1),
      { wrapper: createWrapper() },
    );
    const { result: r2 } = renderHook(
      () => useRelatedRecords(entityName, recordId, relationName, params2),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(r1.current.isSuccess).toBe(true);
      expect(r2.current.isSuccess).toBe(true);
    });

    expect(mockedGet).toHaveBeenCalledTimes(2);
  });

  it('should not fetch when entityName is empty', async () => {
    const { result } = renderHook(
      () => useRelatedRecords('', recordId, relationName),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch when recordId is empty', async () => {
    const { result } = renderHook(
      () => useRelatedRecords(entityName, '', relationName),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch when relationName is empty', async () => {
    const { result } = renderHook(
      () => useRelatedRecords(entityName, recordId, ''),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should propagate 403 permission denied', async () => {
    mockedGet.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: 'permission', value: 'CanRead', message: 'Access denied' },
      ]),
    );

    const { result } = renderHook(
      () => useRelatedRecords(entityName, recordId, relationName),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Access denied');
  });
});

// #####################################################################
//  5. useCreateRecord — Record creation + cache invalidation
//     Replaces RecordManager.CreateRecord(entityName, record) + hooks
// #####################################################################

describe('useCreateRecord', () => {
  it('should create record', async () => {
    const newRecord: EntityRecord = {
      id: 'new-record-id',
      name: 'New Contact',
      email: 'new@example.com',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(newRecord));

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', data: newRecord });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/contact/records',
      newRecord,
    );
    expect(result.current.data).toEqual(newRecord);
  });

  it('should invalidate entity records on success', async () => {
    const newRecord: EntityRecord = { id: 'new-id', name: 'Test' };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(newRecord));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', data: newRecord });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records', 'account'],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should handle relation-prefixed properties', async () => {
    const recordWithRelations: EntityRecord = {
      name: 'Acme Corp',
      '$contact.email': 'linked@acme.com',
      '$contact.first_name': 'John',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse({ ...recordWithRelations, id: 'new-id' }));

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', data: recordWithRelations });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The body sent to POST must include the relation-prefixed keys
    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/account/records',
      expect.objectContaining({
        '$contact.email': 'linked@acme.com',
        '$contact.first_name': 'John',
      }),
    );
  });

  it('should handle validation errors (400)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'name', value: '', message: 'Name is required' },
        { key: 'email', value: 'bad', message: 'Invalid email format' },
      ]),
    );

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', data: { email: 'bad' } });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Name is required; Invalid email format');
  });

  it('should handle permission denied (403)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: 'permission', value: 'CanCreate', message: 'Access denied: CanCreate permission required' },
      ]),
    );

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', data: { name: 'Test' } });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('CanCreate');
  });

  it('should handle server error (500)', async () => {
    mockedPost.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', data: { name: 'Test' } });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });

  it('should work with any entity name (generic)', async () => {
    const taskRecord: EntityRecord = { id: 'task-1', subject: 'Fix bug', priority: 'high' };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(taskRecord));

    const { result } = renderHook(() => useCreateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'task', data: taskRecord });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/entities/task/records', taskRecord);
  });
});

// #####################################################################
//  6. useUpdateRecord — Update with optimistic update + rollback
//     Replaces RecordManager.UpdateRecord(entityName, record)
// #####################################################################

describe('useUpdateRecord', () => {
  const recordId = 'a1000000-0000-0000-0000-000000000001';
  const entityName = 'account';

  it('should update record', async () => {
    const updatedRecord: EntityRecord = { ...mockRecord, name: 'Updated Acme' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedRecord));

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName,
        id: recordId,
        data: { name: 'Updated Acme' },
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/entities/account/records/${recordId}`,
      { name: 'Updated Acme' },
    );
    expect(result.current.data).toEqual(updatedRecord);
  });

  it('should implement optimistic update', async () => {
    // Seed the cache with the original record so the optimistic
    // update has something to merge into
    queryClient.setQueryData(['records', entityName, recordId], mockRecord);

    const updatedFromServer: EntityRecord = {
      ...mockRecord,
      name: 'Optimistic Result',
      updated_on: '2024-06-01T00:00:00Z',
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedFromServer));

    const cancelQueriesSpy = vi.spyOn(queryClient, 'cancelQueries');
    const setQueryDataSpy = vi.spyOn(queryClient, 'setQueryData');

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName,
        id: recordId,
        data: { name: 'Optimistic Result' },
      });
    });

    // 1. In-flight queries for the detail key should be cancelled
    expect(cancelQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records', entityName, recordId],
    });

    // 2. Cache should have been optimistically updated with merged data
    expect(setQueryDataSpy).toHaveBeenCalledWith(
      ['records', entityName, recordId],
      expect.objectContaining({
        name: 'Optimistic Result',
        email: 'info@acme.com', // Original field preserved (merge semantics)
      }),
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    cancelQueriesSpy.mockRestore();
    setQueryDataSpy.mockRestore();
  });

  it('should rollback optimistic update on failure', async () => {
    // Seed the cache with the original record
    queryClient.setQueryData(['records', entityName, recordId], mockRecord);

    mockedPut.mockRejectedValueOnce(new Error('Update failed'));

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName,
        id: recordId,
        data: { name: 'Should Rollback' },
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    // Cache should be restored to original data (rollback)
    const cachedData = queryClient.getQueryData<EntityRecord>(
      ['records', entityName, recordId],
    );
    expect(cachedData).toEqual(mockRecord);
    expect(cachedData!.name).toBe('Acme Corp');
  });

  it('should invalidate entity records list and specific record on settled', async () => {
    const updated: EntityRecord = { ...mockRecord, name: 'Updated' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName,
        id: recordId,
        data: { name: 'Updated' },
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // onSettled invalidates the entity-scoped queries ['records', 'account']
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records', entityName],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should invalidate on settled even after failure', async () => {
    queryClient.setQueryData(['records', entityName, recordId], mockRecord);
    mockedPut.mockRejectedValueOnce(new Error('Fail'));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName,
        id: recordId,
        data: { name: 'Fail' },
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    // onSettled fires regardless of success/error
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records', entityName],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should handle API success=false as error with optimistic rollback', async () => {
    queryClient.setQueryData(['records', entityName, recordId], mockRecord);

    mockedPut.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'name', value: '', message: 'Name cannot be empty' },
      ]),
    );

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName,
        id: recordId,
        data: { name: '' },
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Name cannot be empty');

    // Rollback should have restored the original data
    const cachedData = queryClient.getQueryData<EntityRecord>(
      ['records', entityName, recordId],
    );
    expect(cachedData).toEqual(mockRecord);
  });

  it('should URL-encode entityName and id in path', async () => {
    const updated: EntityRecord = { id: 'special-id', name: 'Updated' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const { result } = renderHook(() => useUpdateRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'my entity',
        id: 'id with spaces',
        data: { name: 'Updated' },
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      '/entities/my%20entity/records/id%20with%20spaces',
      { name: 'Updated' },
    );
  });
});

// #####################################################################
//  7. useDeleteRecord — Record deletion + cache invalidation
//     Replaces RecordManager.DeleteRecord(entityName, recordId) + hooks
// #####################################################################

describe('useDeleteRecord', () => {
  const recordId = 'a1000000-0000-0000-0000-000000000001';

  it('should delete record', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useDeleteRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', id: recordId });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/entities/account/records/${recordId}`,
    );
  });

  it('should invalidate entity records on success', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', id: recordId });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records', 'account'],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should handle permission denied (403)', async () => {
    mockedDel.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: 'permission', value: 'CanDelete', message: 'Access denied: CanDelete permission required' },
      ]),
    );

    const { result } = renderHook(() => useDeleteRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', id: recordId });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('CanDelete');
  });

  it('should handle server error (500)', async () => {
    mockedDel.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useDeleteRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', id: recordId });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });

  it('should work with any entity name (generic)', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useDeleteRecord(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'task', id: 'task-id-123' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockedDel).toHaveBeenCalledWith('/entities/task/records/task-id-123');
  });
});

// #####################################################################
//  8. useCreateManyToManyRelation — M2M bridge creation
//     Replaces RecordManager.CreateRelationManyToManyRecord
// #####################################################################

describe('useCreateManyToManyRelation', () => {
  const originId = 'origin-0000-0000-0000-000000000001';
  const targetId = 'target-0000-0000-0000-000000000002';

  it('should create M2M relation', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useCreateManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/relations/${mockRelationId}/records`,
      { originId, targetId },
    );
  });

  it('should invalidate all record queries on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // M2M relations can affect multiple entities → broad invalidation
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records'],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should return BaseResponseModel shape on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useCreateManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(
      expect.objectContaining({
        success: true,
        errors: [],
        message: '',
      }),
    );
  });

  it('should handle validation errors (400)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'relation', value: mockRelationId, message: 'Relation bridge record already exists' },
      ]),
    );

    const { result } = renderHook(() => useCreateManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Relation bridge record already exists');
  });

  it('should handle server error (500)', async () => {
    mockedPost.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useCreateManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });
});

// #####################################################################
//  9. useRemoveManyToManyRelation — M2M bridge deletion
//     Replaces RecordManager.RemoveRelationManyToManyRecord
// #####################################################################

describe('useRemoveManyToManyRelation', () => {
  const originId = 'origin-0000-0000-0000-000000000001';
  const targetId = 'target-0000-0000-0000-000000000002';

  it('should remove M2M relation', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useRemoveManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/relations/${mockRelationId}/records/${originId}/${targetId}`,
    );
  });

  it('should invalidate all record queries on success', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useRemoveManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records'],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should handle 404 when relation bridge record does not exist', async () => {
    mockedDel.mockResolvedValueOnce(
      createErrorResponse(404, [
        { key: 'relation', value: mockRelationId, message: 'Relation bridge record not found' },
      ]),
    );

    const { result } = renderHook(() => useRemoveManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Relation bridge record not found');
  });

  it('should URL-encode all path parameters', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useRemoveManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: 'rel with space',
        originId: 'origin/special',
        targetId: 'target&special',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      '/relations/rel%20with%20space/records/origin%2Fspecial/target%26special',
    );
  });

  it('should handle server error (500)', async () => {
    mockedDel.mockRejectedValueOnce(new Error('Internal server error'));

    const { result } = renderHook(() => useRemoveManyToManyRelation(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        relationId: mockRelationId,
        originId,
        targetId,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });
});

// #####################################################################
// 10. useImportRecords — CSV import via FormData
//     Replaces ImportExportManager.ImportEntityRecordsFromCsv
// #####################################################################

describe('useImportRecords', () => {
  it('should import records from CSV', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const csvFile = new File(['id,name\nnull,Test'], 'contacts.csv', {
      type: 'text/csv',
    });

    const { result } = renderHook(() => useImportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', file: csvFile });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/contact/records/import',
      expect.any(FormData),
    );

    // Verify FormData contents
    const formData = mockedPost.mock.calls[0][1] as FormData;
    expect(formData.get('file')).toBe(csvFile);
  });

  it('should include fieldMapping when provided', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const csvFile = new File(['First Name,Last Name\nJohn,Doe'], 'contacts.csv', {
      type: 'text/csv',
    });
    const mapping = { 'First Name': 'first_name', 'Last Name': 'last_name' };

    const { result } = renderHook(() => useImportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'contact',
        file: csvFile,
        fieldMapping: mapping,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const formData = mockedPost.mock.calls[0][1] as FormData;
    expect(formData.get('file')).toBe(csvFile);
    expect(formData.get('fieldMapping')).toBe(JSON.stringify(mapping));
  });

  it('should invalidate entity records on import success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const csvFile = new File(['id,name\nnull,Test'], 'test.csv', {
      type: 'text/csv',
    });

    const { result } = renderHook(() => useImportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'account', file: csvFile });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ['records', 'account'],
    });

    invalidateQueriesSpy.mockRestore();
  });

  it('should return BaseResponseModel shape on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const csvFile = new File(['data'], 'test.csv', { type: 'text/csv' });

    const { result } = renderHook(() => useImportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', file: csvFile });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(
      expect.objectContaining({
        success: true,
        errors: [],
      }),
    );
  });

  it('should handle validation errors (400)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(400, [
        { key: 'csv', value: 'contacts.csv', message: 'Invalid CSV format: missing required columns' },
      ]),
    );

    const csvFile = new File(['bad data'], 'contacts.csv', { type: 'text/csv' });

    const { result } = renderHook(() => useImportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', file: csvFile });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('Invalid CSV format');
  });

  it('should handle server error (500)', async () => {
    mockedPost.mockRejectedValueOnce(new Error('Internal server error'));

    const csvFile = new File(['data'], 'test.csv', { type: 'text/csv' });

    const { result } = renderHook(() => useImportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ entityName: 'contact', file: csvFile });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Internal server error');
  });
});

// #####################################################################
// 11. useExportRecords — CSV export with download URL
//     Replaces ImportExportManager CSV export pipeline
// #####################################################################

describe('useExportRecords', () => {
  it('should export records', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'contact',
        fields: ['id', 'first_name', 'last_name', 'email'],
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/contact/records/export',
      {
        fields: ['id', 'first_name', 'last_name', 'email'],
        format: 'csv',
      },
    );
  });

  it('should include query filter when provided', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'account',
        query: mockQueryFilter,
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/account/records/export',
      expect.objectContaining({
        query: mockQueryFilter,
        format: 'csv',
      }),
    );
  });

  it('should not invalidate cache on export (read-only)', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const invalidateQueriesSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'contact',
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Export is read-only — no cache invalidation
    expect(invalidateQueriesSpy).not.toHaveBeenCalled();

    invalidateQueriesSpy.mockRestore();
  });

  it('should omit empty/undefined fields from body', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'account',
        // No fields, no query, no format → body should be empty object
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/account/records/export',
      {},
    );
  });

  it('should return BaseResponseModel shape on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'contact',
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data).toEqual(
      expect.objectContaining({
        success: true,
        errors: [],
      }),
    );
  });

  it('should handle server error (500)', async () => {
    mockedPost.mockRejectedValueOnce(new Error('Export service unavailable'));

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'contact',
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toBe('Export service unavailable');
  });

  it('should handle permission denied (403)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(403, [
        { key: 'permission', value: 'CanRead', message: 'Access denied: CanRead permission required for export' },
      ]),
    );

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'account',
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('CanRead');
  });

  it('should work with any entity name (generic)', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useExportRecords(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        entityName: 'task',
        fields: ['id', 'subject'],
        format: 'csv',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockedPost).toHaveBeenCalledWith(
      '/entities/task/records/export',
      { fields: ['id', 'subject'], format: 'csv' },
    );
  });
});
