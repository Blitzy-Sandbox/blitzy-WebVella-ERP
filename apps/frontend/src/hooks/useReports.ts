/**
 * Report Generation & Data Source TanStack Query Hooks
 *
 * Provides 10 hooks for the Reporting & Analytics microservice and the Entity
 * Management service's data-source endpoints, replacing the monolith's
 * `DataSourceManager.cs` report/data-source execution pipeline with HTTP API
 * calls routed through API Gateway to per-domain Lambda handlers.
 *
 * Architecture mapping (monolith → microservice):
 *   - DataSourceManager.GetAll()                → useDataSources       (GET  /v1/datasources)
 *   - DataSourceManager.Get(id)                 → useDataSource        (GET  /v1/datasources/{id})
 *   - DataSourceManager.Get(id)                 → useReport            (GET  /v1/reports/{id})
 *   - DataSourceManager.GetAll() [filtered]     → useReports           (GET  /v1/reports)
 *   - DataSourceManager.Execute(id, params)     → useReportExecution   (POST /v1/reports/{id}/execute)
 *   - DataSourceManager.Create(ds)              → useCreateReport      (POST /v1/reports)
 *   - DataSourceManager.Update(ds)              → useUpdateReport      (PUT  /v1/reports/{id})
 *   - DataSourceManager.Delete(id)              → useDeleteReport      (DEL  /v1/reports/{id})
 *   - DataSourceManager.Execute(eql, p, retTot) → useExecuteAdHocQuery (POST /v1/reports/query)
 *   - DataSourceManager.GenerateSql(…)          → useGenerateSql       (POST /v1/datasources/generate-sql)
 *
 * Design decisions:
 *   - Reports are CQRS read-models built from domain events (AAP §0.4.2).
 *   - Report listing and retrieval route to the Reporting service
 *     (`/reports`), while data-source management routes to the Entity
 *     Management service (`/datasources`).
 *   - `useReportExecution` issues a POST (complex parameter payload) but is
 *     modelled as `useQuery` so that TanStack Query caches the result set.
 *     `staleTime: 0` ensures always-fresh data.
 *   - `useExecuteAdHocQuery` is a `useMutation` because ad-hoc queries are
 *     unique, user-driven, and not suitable for key-based caching.
 *   - `useDataSources` uses a 5-minute `staleTime` — more aggressive than the
 *     monolith's 1-hour `IMemoryCache` duration, reflecting the serverless
 *     deployment where data-source metadata changes are rarer but need faster
 *     propagation.
 *   - Query keys follow a factory pattern for consistent, fine-grained cache
 *     invalidation — mutations invalidate the minimal set of affected keys.
 *   - All API calls go through the centralised `client` module, which handles
 *     Bearer-token injection, correlation-ID propagation, envelope unwrapping,
 *     and automatic 401 token refresh.
 *
 * @module hooks/useReports
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import * as client from '../api/client';
import type {
  DataSourceBase,
  DatabaseDataSource,
  DataSourceParameter,
  EqlParameter,
} from '../types/datasource';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Key Factories
// ---------------------------------------------------------------------------

/**
 * Centralised query-key factory for all report-related TanStack Query caches.
 *
 * Hierarchy:
 *   reportKeys.all                        → invalidates every report cache
 *   reportKeys.list(params)               → per-filter report listing
 *   reportKeys.detail(id)                 → single report by ID
 *   reportKeys.execution(id, params)      → cached execution results
 */
const reportKeys = {
  /** Root key — used to invalidate all report caches at once. */
  all: ['reports'] as const,

  /**
   * Key for a filtered list of reports.
   * Matches AAP: query key `['reports', params]`.
   */
  list: (params?: ReportListParams) => ['reports', params] as const,

  /**
   * Key for a single report identified by its GUID.
   * Matches AAP: query key `['reports', id]`.
   */
  detail: (id: string) => ['reports', id] as const,

  /**
   * Key for the cached result set of a report execution.
   * Matches AAP: query key `['reports', id, 'execute', params]`.
   */
  execution: (id: string, params?: ReportExecutionParams) =>
    ['reports', id, 'execute', params] as const,
};

/**
 * Centralised query-key factory for data-source–related caches.
 *
 * Hierarchy:
 *   datasourceKeys.all            → invalidates every datasource cache
 *   datasourceKeys.list()         → full datasource listing
 *   datasourceKeys.detail(id)     → single datasource by ID
 */
const datasourceKeys = {
  /** Root key — used to invalidate all datasource caches at once. */
  all: ['datasources'] as const,

  /** Key for the full list of data sources. */
  list: () => ['datasources'] as const,

  /**
   * Key for a single data source identified by its GUID.
   * Matches AAP: query key `['datasources', id]`.
   */
  detail: (id: string) => ['datasources', id] as const,
};

// ---------------------------------------------------------------------------
// Local Types (not exported — consumers infer via function signatures)
// ---------------------------------------------------------------------------

/**
 * Filter / pagination parameters for the report listing endpoint.
 *
 * Mirrors the query-string parameters accepted by `GET /v1/reports`.
 */
interface ReportListParams {
  /** Filter reports by entity name (e.g. "task", "account"). */
  entityName?: string;
  /** Filter by data-source type string (e.g. "database", "code"). */
  type?: string;
  /** 1-based page number for server-side pagination. */
  page?: number;
  /** Number of items per page (default defined by Reporting service). */
  pageSize?: number;
}

/**
 * Parameters for executing a stored report.
 *
 * Mirrors the POST body of `/reports/{id}/execute`.
 * Replaces `DataSourceManager.Execute(Guid id, List<EqlParameter> parameters)`.
 */
interface ReportExecutionParams {
  /**
   * Named parameters to bind into the report's EQL/SQL.
   * Each entry maps to one `DataSourceParameter` from the report definition.
   */
  parameters?: DataSourceParameter[];
  /** 1-based page number for result-set pagination. */
  page?: number;
  /** Result-set page size. */
  pageSize?: number;
}

/**
 * Result of executing a report or ad-hoc query.
 *
 * Mirrors the JSON-shaped result produced by the monolith's
 * `EqlCommand.Execute()` → `row_to_json()` pipeline. In the target
 * architecture, the Reporting Lambda serialises DynamoDB items or RDS
 * PostgreSQL rows into this shape.
 */
interface ReportExecutionResult {
  /** Array of record objects (dynamic keys per entity field definitions). */
  records: Record<string, unknown>[];
  /** Total number of matching records (when `returnTotal` is true). */
  totalCount: number;
  /**
   * Field metadata describing the shape of each record.
   * Derived from `DataSourceModelFieldMeta` (name + optional children for
   * relation-navigated fields with `$` prefix in the monolith's EQL).
   */
  fields?: Array<{ name: string; children?: Array<{ name: string }> }>;
}

/**
 * Payload for creating or updating a report definition.
 *
 * Maps to the monolith's `DataSourceManager.Create(DatabaseDataSource)` /
 * `DataSourceManager.Update(DatabaseDataSource)` parameter set. The server
 * handles EQL compilation, parameter parsing, SQL generation, and name
 * uniqueness validation (mirroring the original Create/Update logic in
 * DataSourceManager.cs lines 100–230).
 */
interface ReportMutationPayload {
  /** Unique display name for the data source (validated for uniqueness). */
  name: string;
  /** Human-readable description. */
  description?: string;
  /**
   * Sorting weight (default: 10). Lower weight → appears first in listings.
   * Mirrors `DataSourceBase.Weight`.
   */
  weight?: number;
  /** The primary entity this data source queries against. */
  entityName?: string;
  /**
   * EQL query text — the core query definition.
   * Server-side: compiled via the EQL engine, validated for syntax errors,
   * and used to derive SQL and field metadata.
   * Mirrors `DatabaseDataSource.EqlText`.
   */
  eqlText: string;
  /**
   * Newline-separated parameter definitions in "name,type,value[,ignoreParseErrors]"
   * format. Parsed by the server's `ProcessParametersText()` logic.
   * Mirrors the monolith's `DataSourceManager.ProcessParametersText()`.
   */
  parametersText?: string;
  /**
   * Whether the server should compute total record count alongside the
   * paginated result set (default: true).
   * Mirrors `DataSourceBase.ReturnTotal`.
   */
  returnTotal?: boolean;
}

/**
 * Payload for executing an ad-hoc EQL query.
 *
 * Replaces `DataSourceManager.Execute(string eql, string parameters, bool returnTotal)`.
 * Unlike stored report execution, ad-hoc queries are not persisted — the
 * server compiles and runs the EQL on the fly.
 */
interface AdHocQueryPayload {
  /** Raw EQL query text to compile and execute. */
  eqlText: string;
  /**
   * Named parameters to bind into the EQL query.
   * Each parameter's name is prefixed with `@` by the server (matching the
   * monolith's `ConvertDataSourceParameterToEqlParameter()` logic).
   */
  parameters?: EqlParameter[];
  /**
   * Whether to compute total record count alongside the result set.
   * Mirrors the `returnTotal` flag in `DataSourceManager.Execute()`.
   */
  returnTotal?: boolean;
}

/**
 * Payload for generating a SQL preview from EQL.
 *
 * Replaces `DataSourceManager.GenerateSql(…)` which compiled EQL through
 * the Irony grammar → AST → SQL pipeline without executing it.
 */
interface GenerateSqlPayload {
  /** Raw EQL text to translate to SQL. */
  eqlText: string;
  /** Parameters that affect query compilation (e.g. type resolution). */
  parameters?: EqlParameter[];
  /** Entity context for field resolution during compilation. */
  entityName?: string;
}

/**
 * Result of the SQL generation endpoint.
 *
 * Contains the compiled SQL and derived metadata without executing the query.
 */
interface GenerateSqlResult {
  /** The compiled SQL string (PostgreSQL dialect for RDS-backed services). */
  sql: string;
  /**
   * Parameters extracted from the EQL, converted to `DataSourceParameter`
   * format with type information.
   */
  parameters?: DataSourceParameter[];
  /** Field metadata derived from the EQL SELECT clause. */
  fields?: Array<{ name: string; children?: Array<{ name: string }> }>;
}

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Lists available reports / dashboards from the Reporting service.
 *
 * Replaces filtered calls to `DataSourceManager.GetAll()` that were used by
 * the monolith's Razor Pages (`RecordList.cshtml.cs`, `Index.cshtml.cs`) to
 * populate report selectors and dashboard listings.
 *
 * The Reporting service owns report metadata as CQRS projections built from
 * `entity-management.datasource.created/updated/deleted` domain events.
 *
 * @param params - Optional filter / pagination parameters
 * @returns TanStack Query result wrapping `DataSourceBase[]`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } = useReports({
 *   entityName: 'task',
 *   page: 1,
 *   pageSize: 20,
 * });
 * ```
 */
export function useReports(params?: ReportListParams) {
  return useQuery({
    queryKey: reportKeys.list(params),
    queryFn: async (): Promise<DataSourceBase[]> => {
      const response = await client.get<DataSourceBase[]>(
        '/reports',
        params as Record<string, unknown> | undefined,
      );

      // Unwrap the API envelope — return empty array when the server
      // responds with success but no payload (e.g. no reports exist yet)
      if (!response.object) {
        return [];
      }
      return response.object;
    },
  });
}

/**
 * Retrieves a single report definition by its GUID.
 *
 * Replaces `DataSourceManager.Get(Guid id)` which filtered the in-memory
 * cache of all data sources. In the target architecture, this is a direct
 * DynamoDB `GetItem` on the Reporting service's table.
 *
 * The query is disabled when `id` is falsy, allowing conditional fetching
 * (e.g. detail views that resolve the ID from a route parameter).
 *
 * @param id - Report GUID; undefined / empty string disables the query
 * @returns TanStack Query result wrapping `DataSourceBase | null`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } = useReport(
 *   reportId,
 * );
 * ```
 */
export function useReport(id?: string) {
  return useQuery({
    queryKey: reportKeys.detail(id ?? ''),
    queryFn: async (): Promise<DataSourceBase | null> => {
      const response = await client.get<DataSourceBase>(
        `/reports/${encodeURIComponent(id!)}`,
      );
      return response.object ?? null;
    },
    enabled: Boolean(id),
  });
}

/**
 * Executes a stored report and caches the result set.
 *
 * Replaces `DataSourceManager.Execute(Guid id, List<EqlParameter> parameters)`
 * which compiled EQL → SQL, bound parameters, ran `NpgsqlDataAdapter.Fill()`,
 * and returned `row_to_json()` results with a 600-second command timeout.
 *
 * Modelled as `useQuery` (not `useMutation`) because report execution is a
 * read operation — the results benefit from TanStack Query's caching,
 * deduplication, and background refetch semantics. A POST verb is used
 * because the parameter payload can be large / complex.
 *
 * `staleTime: 0` ensures report data is always refetched on component mount,
 * matching the monolith's behaviour where each page load re-executed the
 * data source.
 *
 * The query is disabled until both `id` is truthy and `params` are ready,
 * preventing premature execution before the user has configured parameters.
 *
 * @param id     - Report GUID
 * @param params - Execution parameters (bindings, pagination)
 * @returns TanStack Query result wrapping `ReportExecutionResult`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch, isFetching } =
 *   useReportExecution(reportId, {
 *     parameters: [{ name: 'status', type: 'text', value: 'open' }],
 *     page: 1,
 *     pageSize: 50,
 *   });
 * ```
 */
export function useReportExecution(
  id?: string,
  params?: ReportExecutionParams,
) {
  return useQuery({
    queryKey: reportKeys.execution(id ?? '', params),
    queryFn: async (): Promise<ReportExecutionResult> => {
      const response = await client.post<ReportExecutionResult>(
        `/reports/${encodeURIComponent(id!)}/execute`,
        params,
      );

      // Return a safe default when the server sends a success envelope
      // with no result object (edge case: report returns zero rows)
      if (!response.object) {
        return { records: [], totalCount: 0 };
      }
      return response.object;
    },
    // Always refetch — report data must be fresh on every mount
    staleTime: 0,
    // Only fire when a valid report ID is provided
    enabled: Boolean(id),
  });
}

/**
 * Lists all registered data sources (code + database) from the Entity
 * Management service.
 *
 * Replaces `DataSourceManager.GetAll()` which merged code-based data sources
 * (discovered via reflection from `ICodeDataSource` implementations) with
 * database-stored data sources (from the `data_source` PostgreSQL table),
 * cached in `IMemoryCache` with a 1-hour sliding expiration.
 *
 * In the target architecture, the Entity Management Lambda handler returns
 * both types from its DynamoDB table (code data sources are seeded as
 * read-only entries during service bootstrap).
 *
 * `staleTime: 5 minutes` balances freshness against API call volume — data
 * source definitions change infrequently (admin operations only), so a
 * moderate cache duration is appropriate.
 *
 * @returns TanStack Query result wrapping `DataSourceBase[]`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } =
 *   useDataSources();
 * ```
 */
export function useDataSources() {
  return useQuery({
    queryKey: datasourceKeys.list(),
    queryFn: async (): Promise<DataSourceBase[]> => {
      const response = await client.get<DataSourceBase[]>('/datasources');

      if (!response.object) {
        return [];
      }
      return response.object;
    },
    // 5 minutes — data source definitions are admin-managed and rarely change.
    // The monolith used 1-hour IMemoryCache; we shorten this for serverless
    // environments where stale metadata could mask a recently created source.
    staleTime: 5 * 60 * 1_000,
  });
}

/**
 * Retrieves a single data source by its GUID from the Entity Management
 * service.
 *
 * Replaces `DataSourceManager.Get(Guid id)` which filtered the cached
 * `GetAll()` result set by ID.
 *
 * The query is disabled when `id` is falsy.
 *
 * @param id - Data source GUID; undefined / empty string disables the query
 * @returns TanStack Query result wrapping `DataSourceBase | null`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } =
 *   useDataSource(datasourceId);
 * ```
 */
export function useDataSource(id?: string) {
  return useQuery({
    queryKey: datasourceKeys.detail(id ?? ''),
    queryFn: async (): Promise<DataSourceBase | null> => {
      const response = await client.get<DataSourceBase>(
        `/datasources/${encodeURIComponent(id!)}`,
      );
      return response.object ?? null;
    },
    enabled: Boolean(id),
  });
}

// ---------------------------------------------------------------------------
// Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new report (database data source) definition.
 *
 * Replaces `DataSourceManager.Create(DatabaseDataSource ds)` which:
 *   1. Built EQL → verified compilation + generated SQL
 *   2. Validated parameter alignment between EQL and DataSourceParameter list
 *   3. Ensured name uniqueness across all data sources
 *   4. Persisted via `DbDataSourceRepository.Create()`
 *   5. Cleared the `IMemoryCache` entry
 *
 * In the target architecture the Reporting Lambda handler performs the same
 * validation pipeline server-side. On success, all `['reports']`-prefixed
 * query caches are invalidated so that subsequent listings reflect the new
 * report immediately.
 *
 * @returns TanStack Query mutation exposing `mutate(payload)` / `mutateAsync(payload)`
 *
 * @example
 * ```tsx
 * const createReport = useCreateReport();
 * createReport.mutate({
 *   name: 'Active Tasks',
 *   eqlText: 'SELECT * FROM task WHERE $status = @status',
 *   entityName: 'task',
 *   parametersText: 'status,text,active',
 * });
 * ```
 */
export function useCreateReport() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      payload: ReportMutationPayload,
    ): Promise<DatabaseDataSource> => {
      const response = await client.post<DatabaseDataSource>(
        '/reports',
        payload,
      );

      if (!response.object) {
        // Construct an informative error from the envelope's fields.
        // BaseResponseModel.message carries the server's human-readable reason;
        // BaseResponseModel.errors contains structured validation details.
        const errorMessage =
          response.message || 'Failed to create report definition';
        throw new Error(errorMessage);
      }
      return response.object;
    },
    onSuccess: () => {
      // Invalidate the full report listing so the new entry appears
      queryClient.invalidateQueries({ queryKey: reportKeys.all });
    },
  });
}

/**
 * Updates an existing report (database data source) definition.
 *
 * Replaces `DataSourceManager.Update(DatabaseDataSource ds)` which performed
 * the same EQL compilation / parameter validation pipeline as Create, then
 * called `DbDataSourceRepository.Update()` and `RemoveFromCache()`.
 *
 * On success, both the report listing and the specific report detail caches
 * are invalidated, ensuring stale data never lingers.
 *
 * @returns TanStack Query mutation exposing `mutate(payload)` / `mutateAsync(payload)`
 *
 * @example
 * ```tsx
 * const updateReport = useUpdateReport();
 * updateReport.mutate({
 *   id: 'abc-123',
 *   name: 'Active Tasks (v2)',
 *   eqlText: 'SELECT * FROM task WHERE $status = @status ORDER BY $priority DESC',
 *   entityName: 'task',
 * });
 * ```
 */
export function useUpdateReport() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      payload: ReportMutationPayload & { id: string },
    ): Promise<DatabaseDataSource> => {
      const { id, ...data } = payload;
      const response = await client.put<DatabaseDataSource>(
        `/reports/${encodeURIComponent(id)}`,
        data,
      );

      if (!response.object) {
        const errorMessage =
          response.message || 'Failed to update report definition';
        throw new Error(errorMessage);
      }
      return response.object;
    },
    onSuccess: (_data, variables) => {
      // Invalidate the entire reports list
      queryClient.invalidateQueries({ queryKey: reportKeys.all });
      // Also invalidate the specific detail cache for this report
      queryClient.invalidateQueries({
        queryKey: reportKeys.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes a report by its GUID.
 *
 * Replaces `DataSourceManager.Delete(Guid id)` which removed the data source
 * from the `data_source` PostgreSQL table and cleared the `IMemoryCache`.
 *
 * The mutation returns the API response envelope (typed as `BaseResponseModel`)
 * whose `success`, `errors`, and `message` properties indicate the outcome.
 * On success, all `['reports']`-prefixed query caches are invalidated.
 *
 * @returns TanStack Query mutation exposing `mutate(id)` / `mutateAsync(id)`
 *
 * @example
 * ```tsx
 * const deleteReport = useDeleteReport();
 * deleteReport.mutate('abc-123');
 * ```
 */
export function useDeleteReport() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (id: string) => {
      const response = await client.del<BaseResponseModel>(
        `/reports/${encodeURIComponent(id)}`,
      );

      // Surface server-side errors as thrown exceptions so that TanStack
      // Query's `isError` / `error` state activates correctly.
      if (!response.success && response.errors.length > 0) {
        const detail = response.errors
          .map((e) => e.message ?? e.key)
          .join('; ');
        throw new Error(response.message || detail || 'Failed to delete report');
      }

      return response;
    },
    onSuccess: () => {
      // Invalidate all report caches — the deleted entry must disappear
      queryClient.invalidateQueries({ queryKey: reportKeys.all });
    },
  });
}

/**
 * Executes an ad-hoc EQL query without a stored report definition.
 *
 * Replaces `DataSourceManager.Execute(string eql, string parameters, bool returnTotal)`
 * which compiled the raw EQL on the fly, bound parameters (adding `@` prefix
 * via `ConvertDataSourceParameterToEqlParameter()`), and ran the resulting
 * SQL against PostgreSQL.
 *
 * Modelled as a `useMutation` (not `useQuery`) because:
 *   - Ad-hoc queries are unique, user-driven, and ephemeral
 *   - There is no meaningful cache key to associate with arbitrary EQL text
 *   - The caller controls exactly when execution happens (imperative `mutate()`)
 *
 * No cache invalidation is performed — this is a pure read operation that
 * does not affect any persisted state.
 *
 * @returns TanStack Query mutation exposing `mutate(payload)` / `mutateAsync(payload)`
 *
 * @example
 * ```tsx
 * const adHocQuery = useExecuteAdHocQuery();
 * adHocQuery.mutate({
 *   eqlText: 'SELECT * FROM contact WHERE $company CONTAINS @search',
 *   parameters: [{ name: 'search', value: 'Acme' }],
 *   returnTotal: true,
 * });
 * ```
 */
export function useExecuteAdHocQuery() {
  return useMutation({
    mutationFn: async (
      payload: AdHocQueryPayload,
    ): Promise<ReportExecutionResult> => {
      const response = await client.post<ReportExecutionResult>(
        '/reports/query',
        payload,
      );

      // Return a safe default when the server returns no result object
      if (!response.object) {
        return { records: [], totalCount: 0 };
      }
      return response.object;
    },
    // No onSuccess cache invalidation — ad-hoc queries do not mutate state
  });
}

/**
 * Generates a SQL preview from an EQL query without executing it.
 *
 * Replaces `DataSourceManager.GenerateSql(…)` which compiled EQL through
 * the Irony grammar → AST → `EqlBuilder.Sql.cs` pipeline, returning the
 * generated PostgreSQL SQL along with parameter and field metadata.
 *
 * This is used in the SDK admin console's data-source editor to show a live
 * SQL preview as the administrator types EQL, enabling them to verify the
 * query before saving.
 *
 * No cache invalidation is performed — SQL generation is a stateless,
 * read-only operation.
 *
 * @returns TanStack Query mutation exposing `mutate(payload)` / `mutateAsync(payload)`
 *
 * @example
 * ```tsx
 * const generateSql = useGenerateSql();
 * generateSql.mutate({
 *   eqlText: 'SELECT * FROM account ORDER BY $name ASC',
 *   entityName: 'account',
 * });
 * // generateSql.data?.sql contains the compiled SQL string
 * ```
 */
export function useGenerateSql() {
  return useMutation({
    mutationFn: async (
      payload: GenerateSqlPayload,
    ): Promise<GenerateSqlResult> => {
      const response = await client.post<GenerateSqlResult>(
        '/datasources/generate-sql',
        payload,
      );

      if (!response.object) {
        const errorMessage =
          response.message || 'Failed to generate SQL from EQL';
        throw new Error(errorMessage);
      }
      return response.object;
    },
    // No onSuccess cache invalidation — SQL generation is stateless
  });
}
