// @vitest-environment jsdom

/**
 * @file useReports.test.ts
 * @description Comprehensive Vitest unit tests for the 10 report/data-source
 * TanStack Query hooks exported from src/hooks/useReports.ts.
 *
 * These hooks replace the monolith's DataSourceManager.cs — the central
 * runtime manager for data source discovery, caching (DATASOURCES 1-hour
 * IMemoryCache TTL), validation, execution (EQL + code), and SQL generation.
 *
 * Hook-to-endpoint mapping:
 *   - useReports           → GET  /v1/reports           (DataSourceManager.GetAll filtered)
 *   - useReport            → GET  /v1/reports/{id}      (DataSourceManager.Get)
 *   - useReportExecution   → POST /v1/reports/{id}/execute (DataSourceManager.Execute(id, params))
 *   - useDataSources       → GET  /v1/datasources       (DataSourceManager.GetAll)
 *   - useDataSource        → GET  /v1/datasources/{id}  (DataSourceManager.Get)
 *   - useCreateReport      → POST /v1/reports            (DataSourceManager.Create)
 *   - useUpdateReport      → PUT  /v1/reports/{id}       (DataSourceManager.Update)
 *   - useDeleteReport      → DEL  /v1/reports/{id}       (DataSourceManager.Delete)
 *   - useExecuteAdHocQuery → POST /v1/reports/query      (DataSourceManager.Execute(eql, params, returnTotal))
 *   - useGenerateSql       → POST /v1/datasources/generate-sql (DataSourceManager.GenerateSql)
 *
 * Key design decisions tested:
 *   - useReportExecution uses POST but is modeled as useQuery (cacheable)
 *   - useExecuteAdHocQuery is a useMutation (not cacheable — ad-hoc, unique)
 *   - Report execution staleTime: 0 (always fresh)
 *   - Data sources staleTime: 5 minutes (admin-managed, rarely change)
 *   - Mutations invalidate the minimal set of query keys for consistency
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
 * The hooks under test import `* as client` and call client.get, client.post,
 * client.put, client.del. The mock factory exports these as vi.fn() stubs.
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

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useReports,
  useReport,
  useReportExecution,
  useDataSources,
  useDataSource,
  useCreateReport,
  useUpdateReport,
  useDeleteReport,
  useExecuteAdHocQuery,
  useGenerateSql,
} from '../../../src/hooks/useReports';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';
import type { ApiResponse } from '../../../src/api/client';
import type {
  DataSourceBase,
  DatabaseDataSource,
  DataSourceParameter,
  EqlParameter,
} from '../../../src/types/datasource';
import type { BaseResponseModel } from '../../../src/types/common';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Mock report definition (DataSourceBase with Database type).
 * Represents a monthly sales report as returned by the Reporting service.
 * Replaces the monolith's DatabaseDataSource entity persisted in the
 * data_source PostgreSQL table.
 */
const mockReport = {
  id: 'report-a1b2c3d4-e5f6-7890-abcd-ef1234567890',
  type: 0, // DataSourceType.Database (const enum inlined)
  name: 'monthly-sales',
  description: 'Monthly Sales Report',
  weight: 10,
  returnTotal: true,
  entityName: 'invoice',
  fields: [
    { name: 'month', children: [] },
    { name: 'total', children: [] },
  ],
  parameters: [
    { name: 'year', type: 'number', value: '2024', ignoreParseErrors: false },
  ],
  resultModel: 'object',
};

/**
 * Second report fixture for list tests — Quarterly Revenue Breakdown.
 */
const mockReport2 = {
  id: 'report-b2c3d4e5-f6a7-8901-bcde-f12345678901',
  type: 0, // DataSourceType.Database
  name: 'quarterly-revenue',
  description: 'Quarterly Revenue Breakdown',
  weight: 20,
  returnTotal: true,
  entityName: 'invoice',
  fields: [
    { name: 'quarter', children: [] },
    { name: 'revenue', children: [] },
  ],
  parameters: [],
  resultModel: 'object',
};

/**
 * Database data source fixture (DatabaseDataSource) — includes eqlText/sqlText.
 * Replaces a row from the monolith's data_source table for a customer
 * account listing query. Includes both code and database type properties
 * for verifying data source operations.
 */
const mockDataSource = {
  id: 'ds-a1b2c3d4-e5f6-7890-abcd-ef1234567890',
  type: 0, // DataSourceType.Database
  name: 'account_list',
  description: 'List of customer accounts',
  weight: 10,
  returnTotal: true,
  entityName: 'account',
  fields: [
    { name: 'id', children: [] },
    { name: 'name', children: [] },
    { name: 'type', children: [] },
  ],
  parameters: [
    { name: 'type', type: 'text', value: 'customer', ignoreParseErrors: false },
  ] as DataSourceParameter[],
  resultModel: 'EntityRecordList',
  eqlText: 'SELECT * FROM account WHERE type = @type',
  sqlText: 'SELECT row_to_json(t) FROM (SELECT * FROM rec_account WHERE type = $1) t',
};

/**
 * Code data source fixture (DataSourceBase with Code type).
 * Mirrors the monolith's CodeDataSource instances discovered via
 * reflection by DataSourceManager.InitCodeDataSources().
 */
const mockCodeDataSource = {
  id: 'code-ds-b2c3d4e5-f6a7-8901-bcde-f12345678901',
  type: 1, // DataSourceType.Code
  name: 'dashboard_summary',
  description: 'Dashboard summary computed by server-side code',
  weight: 5,
  returnTotal: false,
  entityName: '',
  fields: [
    { name: 'metric', children: [] },
    { name: 'value', children: [] },
  ],
  parameters: [],
  resultModel: 'object',
};

/**
 * Report execution result fixture matching ReportExecutionResult interface.
 * Mirrors the JSON-shaped result produced by the monolith's
 * EqlCommand.Execute() → row_to_json() pipeline.
 */
const mockReportResult = {
  records: [
    { month: 'Jan', total: 15000 },
    { month: 'Feb', total: 18500 },
    { month: 'Mar', total: 22000 },
  ],
  totalCount: 12,
  fields: [
    { name: 'month' },
    { name: 'total' },
  ],
};

/**
 * SQL generation result fixture matching GenerateSqlResult interface.
 * Mirrors the output of DataSourceManager.GenerateSql() which compiled
 * EQL through the Irony grammar → AST → EqlBuilder.Sql.cs pipeline.
 */
const mockGenerateSqlResult = {
  sql: 'SELECT row_to_json(t) FROM (SELECT * FROM rec_account) t',
  parameters: [],
  fields: [
    { name: 'id' },
    { name: 'name' },
  ],
};

/**
 * Created/updated report fixture (DatabaseDataSource) — returned by
 * POST /v1/reports and PUT /v1/reports/{id} after server-side EQL
 * compilation, parameter validation, and name uniqueness check.
 */
const mockCreatedReport = {
  ...mockDataSource,
  id: 'new-report-c3d4e5f6-a789-0123-cdef-234567890123',
  name: 'active-tasks',
  description: 'Active tasks by status',
  entityName: 'task',
  eqlText: 'SELECT * FROM task WHERE $status = @status',
  sqlText: 'SELECT row_to_json(t) FROM (SELECT * FROM rec_task WHERE status = $1) t',
};

// ──────────────────────────────────────────────────────────────────────────────
// Helper functions
// ──────────────────────────────────────────────────────────────────────────────

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
 * and structured errors are included (mirrors EqlException / ValidationException).
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
 * All 10 report/datasource hooks require QueryClientProvider to access the
 * shared QueryClient.
 */
function createWrapper(queryClient?: QueryClient) {
  const client = queryClient ?? createTestQueryClient();
  return function TestQueryClientWrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client }, children);
  };
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
// Suite 1: useReports(params?) — Report listing
// ══════════════════════════════════════════════════════════════════════════════

describe('useReports', () => {
  /**
   * Tests basic report listing fetch.
   * Replaces DataSourceManager.GetAll() filtered for report-type data sources
   * which merged code + database sources from IMemoryCache (1-hour TTL).
   */
  it('should fetch reports', async () => {
    const reportsList = [mockReport, mockReport2];
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(reportsList) as never,
    );

    const { result } = renderHook(() => useReports(), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/reports', undefined);
    expect(result.current.data).toHaveLength(2);
    expect(result.current.data![0].name).toBe('monthly-sales');
    expect(result.current.data![1].name).toBe('quarterly-revenue');
  });

  /**
   * Tests empty report list response.
   * Verifies the hook returns an empty array when no reports exist,
   * handling the null/undefined object case gracefully.
   */
  it('should handle empty reports', async () => {
    // Return a response with null object (no reports exist yet)
    mockGet.mockResolvedValueOnce({
      success: true,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: 'Success',
      object: null,
      hash: undefined,
    } as never);

    const { result } = renderHook(() => useReports(), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data).toEqual([]);
  });

  /**
   * Tests pagination and filter parameter pass-through.
   * Mirrors the monolith's RecordList.cshtml.cs page model which
   * passed entity name, page, and page size to DataSourceManager.
   */
  it('should support pagination', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse([mockReport]) as never,
    );

    const params = { page: 2, pageSize: 10, entityName: 'invoice' };
    const { result } = renderHook(() => useReports(params), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/reports', params);
    expect(result.current.data).toHaveLength(1);
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 2: useReport(id) — Single report fetch
// ══════════════════════════════════════════════════════════════════════════════

describe('useReport', () => {
  /**
   * Tests fetching a single report by its GUID.
   * Replaces DataSourceManager.Get(Guid id) which filtered the
   * in-memory cache of all data sources by ID.
   */
  it('should fetch report by ID', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockReport) as never,
    );

    const { result } = renderHook(
      () => useReport(mockReport.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      `/reports/${encodeURIComponent(mockReport.id)}`,
    );
    expect(result.current.data?.name).toBe('monthly-sales');
    expect(result.current.data?.id).toBe(mockReport.id);
  });

  /**
   * Tests that parameter definitions are included in the response.
   * DataSourceBase.parameters contains name/type/value/ignoreParseErrors
   * entries matching the monolith's DataSourceParameter model.
   */
  it('should include parameter definitions', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockReport) as never,
    );

    const { result } = renderHook(
      () => useReport(mockReport.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.parameters).toHaveLength(1);
    expect(result.current.data?.parameters[0]).toEqual(
      expect.objectContaining({
        name: 'year',
        type: 'number',
        value: '2024',
      }),
    );
  });

  /**
   * Tests that the query is disabled when no id is provided.
   * Mirrors conditional fetching for detail views that resolve the
   * report ID from a route parameter (may be undefined initially).
   */
  it('should not fetch when id is falsy', () => {
    const { result } = renderHook(() => useReport(undefined), { wrapper });

    // Query should be disabled — not loading, not fetching
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 3: useReportExecution(id, params?) — Report execution
// ══════════════════════════════════════════════════════════════════════════════

describe('useReportExecution', () => {
  /**
   * Tests report execution with parameters.
   * Replaces DataSourceManager.Execute(Guid id, List<EqlParameter> parameters)
   * which compiled EQL → SQL, bound parameters, ran NpgsqlDataAdapter.Fill(),
   * and returned row_to_json() results with a 600-second command timeout.
   */
  it('should execute report with parameters', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const execParams = {
      parameters: [
        { name: 'year', type: 'number', value: '2024', ignoreParseErrors: false },
      ] as DataSourceParameter[],
      page: 1,
      pageSize: 50,
    };

    const { result } = renderHook(
      () => useReportExecution(mockReport.id, execParams),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith(
      `/reports/${encodeURIComponent(mockReport.id)}/execute`,
      execParams,
    );
  });

  /**
   * Tests that result data and field metadata are correctly returned.
   * Verifies the hook exposes records, totalCount, and fields from
   * the ReportExecutionResult interface.
   */
  it('should return result data and field metadata', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const { result } = renderHook(
      () => useReportExecution(mockReport.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.records).toHaveLength(3);
    expect(result.current.data?.totalCount).toBe(12);
    expect(result.current.data?.fields).toHaveLength(2);
    expect(result.current.data?.fields![0].name).toBe('month');
    expect(result.current.data?.fields![1].name).toBe('total');
  });

  /**
   * Tests that staleTime is 0 — report data must always be fresh.
   * The monolith re-executed the data source on every page load;
   * staleTime: 0 preserves this behaviour by immediately marking
   * fetched data as stale so TanStack Query refetches on remount.
   */
  it('should use staleTime of 0', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const { result } = renderHook(
      () => useReportExecution(mockReport.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // With staleTime: 0, data is immediately stale after fetch
    expect(result.current.isStale).toBe(true);
  });

  /**
   * Tests that the query is disabled when id is missing.
   * Prevents premature execution before the user has selected a report.
   */
  it('should not execute when id is missing', () => {
    const { result } = renderHook(
      () => useReportExecution(undefined),
      { wrapper },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockPost).not.toHaveBeenCalled();
  });

  /**
   * Tests that report execution is modeled as a query (useQuery) despite
   * using POST, providing caching, deduplication, and background refetch
   * semantics. A mutation would not offer these cache benefits.
   */
  it('should be modeled as query despite POST', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const { result } = renderHook(
      () => useReportExecution(mockReport.id),
      { wrapper },
    );

    // useQuery returns refetch (query semantic) and NOT mutate (mutation semantic)
    expect(typeof result.current.refetch).toBe('function');
    expect(result.current).not.toHaveProperty('mutate');
    expect(result.current).not.toHaveProperty('mutateAsync');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 4: useDataSources — Data source listing
// ══════════════════════════════════════════════════════════════════════════════

describe('useDataSources', () => {
  /**
   * Tests fetching all data sources.
   * Replaces DataSourceManager.GetAll() which merged code-based data sources
   * (discovered via reflection from ICodeDataSource implementations) with
   * database-stored data sources (from the data_source PostgreSQL table).
   */
  it('should fetch all data sources', async () => {
    const allDataSources = [mockDataSource, mockCodeDataSource];
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(allDataSources) as never,
    );

    const { result } = renderHook(() => useDataSources(), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/datasources');
    expect(result.current.data).toHaveLength(2);
  });

  /**
   * Tests that staleTime is 5 minutes.
   * The monolith used a 1-hour IMemoryCache expiration for the DATASOURCES
   * cache key (DataSourceManager static constructor). The serverless
   * architecture shortens this to 5 minutes for faster propagation of
   * admin-managed data source changes.
   *
   * After a successful fetch, data should NOT be stale (isStale=false)
   * because 5 minutes haven't elapsed yet.
   */
  it('should use staleTime of 5 minutes', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse([mockDataSource]) as never,
    );

    const { result } = renderHook(() => useDataSources(), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // With staleTime: 5 * 60 * 1000, data should be fresh right after fetch
    expect(result.current.isStale).toBe(false);
  });

  /**
   * Tests that the response includes both code and database data sources.
   * In the target architecture, the Entity Management Lambda returns both
   * types from its DynamoDB table (code sources seeded as read-only entries
   * during bootstrap, database sources created via admin UI).
   */
  it('should include both code and database data sources', async () => {
    const mixedSources = [mockDataSource, mockCodeDataSource];
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mixedSources) as never,
    );

    const { result } = renderHook(() => useDataSources(), { wrapper });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify both data source types are present
    const dbSource = result.current.data!.find((ds) => ds.type === 0);
    const codeSource = result.current.data!.find((ds) => ds.type === 1);
    expect(dbSource).toBeDefined();
    expect(dbSource!.name).toBe('account_list');
    expect(codeSource).toBeDefined();
    expect(codeSource!.name).toBe('dashboard_summary');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 5: useDataSource(id) — Single data source fetch
// ══════════════════════════════════════════════════════════════════════════════

describe('useDataSource', () => {
  /**
   * Tests fetching a single data source by its GUID.
   * Replaces DataSourceManager.Get(Guid id) which filtered the cached
   * GetAll() result set by ID using LINQ SingleOrDefault.
   */
  it('should fetch data source by ID', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockDataSource) as never,
    );

    const { result } = renderHook(
      () => useDataSource(mockDataSource.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      `/datasources/${encodeURIComponent(mockDataSource.id)}`,
    );
    expect(result.current.data?.name).toBe('account_list');
    expect(result.current.data?.id).toBe(mockDataSource.id);
  });

  /**
   * Tests that DatabaseDataSource properties (eqlText, parameters) are
   * included in the response. The monolith's DatabaseDataSource extends
   * DataSourceBase with eqlText and sqlText (generated by EqlBuilder.Sql.cs).
   * Verify these EQL-specific fields pass through correctly.
   */
  it('should include EQL text and parameters', async () => {
    mockGet.mockResolvedValueOnce(
      createSuccessResponse(mockDataSource) as never,
    );

    const { result } = renderHook(
      () => useDataSource(mockDataSource.id),
      { wrapper },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // DatabaseDataSource extends DataSourceBase with eqlText field;
    // verify the raw data includes these properties
    expect(result.current.data).toHaveProperty(
      'eqlText',
      'SELECT * FROM account WHERE type = @type',
    );
    expect(result.current.data?.parameters).toHaveLength(1);
    expect(result.current.data?.parameters[0]).toEqual(
      expect.objectContaining({
        name: 'type',
        type: 'text',
        value: 'customer',
      }),
    );
  });

  /**
   * Tests that the query is disabled when id is falsy.
   */
  it('should not fetch when id is falsy', () => {
    const { result } = renderHook(() => useDataSource(''), { wrapper });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 6: useCreateReport — Create report mutation
// ══════════════════════════════════════════════════════════════════════════════

describe('useCreateReport', () => {
  /**
   * Tests creating a new report (database data source) definition.
   * Replaces DataSourceManager.Create(DatabaseDataSource ds) which:
   *   1. Built EQL → verified compilation + generated SQL
   *   2. Validated parameter alignment
   *   3. Ensured name uniqueness across all data sources
   *   4. Persisted via DbDataSourceRepository.Create()
   *   5. Cleared the IMemoryCache DATASOURCES entry
   */
  it('should create report', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockCreatedReport) as never,
    );

    const { result } = renderHook(() => useCreateReport(), { wrapper });

    const payload = {
      name: 'active-tasks',
      description: 'Active tasks by status',
      eqlText: 'SELECT * FROM task WHERE $status = @status',
      entityName: 'task',
      parametersText: 'status,text,active',
      returnTotal: true,
    };

    await act(async () => {
      await result.current.mutateAsync(payload);
    });

    expect(mockPost).toHaveBeenCalledWith('/reports', payload);
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
    expect(result.current.data?.name).toBe('active-tasks');
  });

  /**
   * Tests that the reports query cache is invalidated on successful creation.
   * Ensures the new report appears in subsequent listings immediately,
   * matching the monolith's DataSourceManager.RemoveFromCache() behaviour.
   */
  it('should invalidate reports query', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockCreatedReport) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateReport(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        name: 'active-tasks',
        eqlText: 'SELECT * FROM task WHERE $status = @status',
      });
    });

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['reports'],
      }),
    );

    invalidateSpy.mockRestore();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 7: useUpdateReport — Update report mutation
// ══════════════════════════════════════════════════════════════════════════════

describe('useUpdateReport', () => {
  /**
   * Tests updating an existing report definition.
   * Replaces DataSourceManager.Update(DatabaseDataSource ds) which
   * performed the same EQL compilation / parameter validation pipeline
   * as Create, then called DbDataSourceRepository.Update().
   */
  it('should update report', async () => {
    const updatedReport = {
      ...mockCreatedReport,
      name: 'active-tasks-v2',
      eqlText: 'SELECT * FROM task WHERE $status = @status ORDER BY $priority DESC',
    };
    mockPut.mockResolvedValueOnce(
      createSuccessResponse(updatedReport) as never,
    );

    const { result } = renderHook(() => useUpdateReport(), { wrapper });

    const payload = {
      id: mockCreatedReport.id,
      name: 'active-tasks-v2',
      eqlText: 'SELECT * FROM task WHERE $status = @status ORDER BY $priority DESC',
      entityName: 'task',
    };

    await act(async () => {
      await result.current.mutateAsync(payload);
    });

    // id is extracted and used in the URL; remaining fields sent as body
    expect(mockPut).toHaveBeenCalledWith(
      `/reports/${encodeURIComponent(mockCreatedReport.id)}`,
      expect.objectContaining({
        name: 'active-tasks-v2',
        eqlText: 'SELECT * FROM task WHERE $status = @status ORDER BY $priority DESC',
        entityName: 'task',
      }),
    );
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
    expect(result.current.data?.name).toBe('active-tasks-v2');
  });

  /**
   * Tests that both the reports list and specific report detail caches
   * are invalidated on successful update, ensuring stale data never lingers.
   * Matches DataSourceManager.RemoveFromCache() + the per-id invalidation.
   */
  it('should invalidate reports list and specific report', async () => {
    mockPut.mockResolvedValueOnce(
      createSuccessResponse(mockCreatedReport) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateReport(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        id: mockCreatedReport.id,
        name: 'active-tasks',
        eqlText: 'SELECT * FROM task WHERE $status = @status',
      });
    });

    // Should invalidate the full reports list
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['reports'],
      }),
    );

    // Should also invalidate the specific report detail cache
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['reports', mockCreatedReport.id],
      }),
    );

    invalidateSpy.mockRestore();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 8: useDeleteReport — Delete report mutation
// ══════════════════════════════════════════════════════════════════════════════

describe('useDeleteReport', () => {
  /**
   * Tests deleting a report by its GUID.
   * Replaces DataSourceManager.Delete(Guid id) which removed the data source
   * from the data_source PostgreSQL table and cleared the IMemoryCache.
   */
  it('should delete report', async () => {
    const deleteResponse: ApiResponse<unknown> = {
      success: true,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: 'Report deleted successfully',
      object: undefined,
      hash: undefined,
    };
    mockDel.mockResolvedValueOnce(deleteResponse as never);

    const { result } = renderHook(() => useDeleteReport(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync(mockReport.id);
    });

    expect(mockDel).toHaveBeenCalledWith(
      `/reports/${encodeURIComponent(mockReport.id)}`,
    );
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
    expect(result.current.data?.success).toBe(true);
  });

  /**
   * Tests that the reports query cache is invalidated on successful deletion.
   * The deleted entry must disappear from listings immediately.
   */
  it('should invalidate reports query', async () => {
    const deleteResponse: ApiResponse<unknown> = {
      success: true,
      errors: [],
      statusCode: 200,
      timestamp: new Date().toISOString(),
      message: 'Report deleted',
      object: undefined,
      hash: undefined,
    };
    mockDel.mockResolvedValueOnce(deleteResponse as never);

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteReport(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync(mockReport.id);
    });

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['reports'],
      }),
    );

    invalidateSpy.mockRestore();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 9: useExecuteAdHocQuery — Ad-hoc EQL execution
// ══════════════════════════════════════════════════════════════════════════════

describe('useExecuteAdHocQuery', () => {
  /**
   * Tests executing an ad-hoc EQL query.
   * Replaces DataSourceManager.Execute(string eql, string parameters, bool returnTotal)
   * which compiled raw EQL on the fly, bound parameters (adding @ prefix via
   * ConvertDataSourceParameterToEqlParameter()), and ran the resulting SQL.
   */
  it('should execute ad-hoc query', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const { result } = renderHook(() => useExecuteAdHocQuery(), { wrapper });

    const queryPayload = {
      eqlText: 'SELECT * FROM account',
      parameters: [{ name: 'type', value: 'customer' }] as EqlParameter[],
      returnTotal: true,
    };

    await act(async () => {
      await result.current.mutateAsync(queryPayload);
    });

    expect(mockPost).toHaveBeenCalledWith('/reports/query', queryPayload);
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
  });

  /**
   * Tests that query results include records and totalCount.
   */
  it('should return query results', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const { result } = renderHook(() => useExecuteAdHocQuery(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        eqlText: 'SELECT * FROM account',
        returnTotal: true,
      });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
    expect(result.current.data?.records).toHaveLength(3);
    expect(result.current.data?.totalCount).toBe(12);
    expect(result.current.data?.fields).toHaveLength(2);
  });

  /**
   * Tests that no cache invalidation occurs after ad-hoc query execution.
   * Ad-hoc queries are pure read operations via mutation (no cache key
   * can meaningfully represent arbitrary EQL text) and should NOT
   * invalidate any existing query caches.
   */
  it('should not invalidate cache', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockReportResult) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useExecuteAdHocQuery(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        eqlText: 'SELECT * FROM account',
        returnTotal: true,
      });
    });

    expect(invalidateSpy).not.toHaveBeenCalled();

    invalidateSpy.mockRestore();
  });

  /**
   * Tests EQL syntax error handling.
   * Mirrors the monolith's EqlBuilder which threw EqlException with
   * line/column context from Irony's ParseTree.ParserMessages on
   * invalid EQL syntax. The server returns a 400 with structured errors.
   */
  it('should handle EQL syntax errors', async () => {
    const eqlError: ApiResponse<never> = {
      success: false,
      errors: [
        {
          key: 'eql',
          value: 'line 1, col 15',
          message: 'Syntax error: unexpected token "FORM" at line 1, column 15. Did you mean "FROM"?',
        },
      ],
      statusCode: 400,
      timestamp: new Date().toISOString(),
      message: 'EQL compilation failed',
      object: undefined as never,
      hash: undefined,
    };

    mockPost.mockRejectedValueOnce(eqlError);

    const { result } = renderHook(() => useExecuteAdHocQuery(), { wrapper });

    await act(async () => {
      try {
        await result.current.mutateAsync({
          eqlText: 'SELECT * FORM account',
          returnTotal: true,
        });
      } catch {
        // Expected to throw — mutation error handling
      }
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });
    expect(result.current.error).toBeDefined();
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// Suite 10: useGenerateSql — SQL preview from EQL
// ══════════════════════════════════════════════════════════════════════════════

describe('useGenerateSql', () => {
  /**
   * Tests generating a SQL preview from EQL.
   * Replaces DataSourceManager.GenerateSql(…) which compiled EQL through
   * the Irony grammar → AST → EqlBuilder.Sql.cs pipeline without executing.
   * Used in the SDK admin console data-source editor for live SQL preview.
   */
  it('should generate SQL from EQL', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockGenerateSqlResult) as never,
    );

    const { result } = renderHook(() => useGenerateSql(), { wrapper });

    const payload = {
      eqlText: 'SELECT * FROM account',
      entityName: 'account',
    };

    await act(async () => {
      await result.current.mutateAsync(payload);
    });

    expect(mockPost).toHaveBeenCalledWith(
      '/datasources/generate-sql',
      payload,
    );
    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
  });

  /**
   * Tests that the generated SQL string is returned correctly.
   * The response contains the compiled SQL (PostgreSQL dialect) plus
   * parameter and field metadata derived from the EQL SELECT clause.
   */
  it('should return generated SQL string', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockGenerateSqlResult) as never,
    );

    const { result } = renderHook(() => useGenerateSql(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        eqlText: 'SELECT * FROM account',
      });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });
    expect(result.current.data?.sql).toBe(
      'SELECT row_to_json(t) FROM (SELECT * FROM rec_account) t',
    );
    expect(result.current.data?.parameters).toEqual([]);
    expect(result.current.data?.fields).toHaveLength(2);
    expect(result.current.data?.fields![0].name).toBe('id');
  });

  /**
   * Tests validation error handling for invalid EQL.
   * Mirrors the monolith's EqlBuilder validation which threw
   * ValidationException when the EQL referenced non-existent entities
   * or fields (distinct from syntax errors which came from Irony parsing).
   */
  it('should handle validation errors', async () => {
    const validationError: ApiResponse<never> = {
      success: false,
      errors: [
        {
          key: 'eqlText',
          value: 'nonexistent_entity',
          message: 'Entity "nonexistent_entity" does not exist',
        },
      ],
      statusCode: 400,
      timestamp: new Date().toISOString(),
      message: 'EQL validation failed',
      object: undefined as never,
      hash: undefined,
    };

    mockPost.mockRejectedValueOnce(validationError);

    const { result } = renderHook(() => useGenerateSql(), { wrapper });

    await act(async () => {
      try {
        await result.current.mutateAsync({
          eqlText: 'SELECT * FROM nonexistent_entity',
        });
      } catch {
        // Expected to throw — mutation error handling
      }
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });
    expect(result.current.error).toBeDefined();
  });

  /**
   * Tests that no cache invalidation occurs for SQL generation.
   * SQL generation is a stateless, read-only operation that does not
   * affect any persisted state — no cache should be invalidated.
   */
  it('should not invalidate cache', async () => {
    mockPost.mockResolvedValueOnce(
      createSuccessResponse(mockGenerateSqlResult) as never,
    );

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useGenerateSql(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        eqlText: 'SELECT * FROM account',
      });
    });

    expect(invalidateSpy).not.toHaveBeenCalled();

    invalidateSpy.mockRestore();
  });
});
