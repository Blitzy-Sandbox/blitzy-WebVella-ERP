/**
 * Report Generation / DataSource API Module
 *
 * Typed API functions for the Reporting & Analytics bounded-context service.
 * Replaces datasource execution and testing endpoints from WebApiController.cs
 * lines 494–602 and the datasource listing from AdminController.cs.
 * The Reporting service uses RDS PostgreSQL for read-model projections.
 *
 * Route prefixes:
 * - DataSource admin: /reporting/datasources/*
 * - Reports: /reporting/reports/*
 * - Dashboards: /reporting/dashboards/*
 */

import { get, post } from '../client';
import type { ApiResponse } from '../client';
import type { DataSourceBase, DataSourceParameter } from '../../types/datasource';

// ─── Interface Definitions ───────────────────────────────────────────────────

/**
 * Parameters for testing a datasource by providing EQL directly.
 * Maps to the DataSourceTestModel from WebApiController.cs line 511.
 *
 * @property action - 'sql' returns generated SQL query; 'data' executes and returns actual data
 * @property eql - Entity Query Language string to parse and execute
 * @property parameters - JSON-encoded parameter string for the EQL query
 * @property returnTotal - Whether to include total record count in response
 */
export interface DataSourceTestParams {
  /** When 'sql', returns the generated SQL without executing. When 'data', executes the query. */
  action: 'sql' | 'data';
  /** Entity Query Language string to parse and translate into a SQL query */
  eql: string;
  /** JSON-encoded parameter string for parameterized EQL queries */
  parameters: string;
  /** Whether to include total record count alongside the result set */
  returnTotal: boolean;
}

/**
 * Parameters for testing a datasource resolved by its registered ID.
 * Maps to the DataSourceTestModel variant at WebApiController.cs line 542.
 * The service resolves the datasource definition, extracts EQL from the
 * DatabaseDataSource, then merges page parameters with datasource-defined
 * parameters (WebApiController.cs lines 568-580: iterates dataSource.Parameters,
 * uses paramList match by name to override default values), and executes.
 *
 * @property action - 'sql' returns generated SQL query; 'data' executes and returns actual data
 * @property paramList - Parameter overrides that merge with datasource-defined parameters by name
 */
export interface DataSourceTestByIdParams {
  /** When 'sql', returns the generated SQL without executing. When 'data', executes the query. */
  action: 'sql' | 'data';
  /**
   * Parameter overrides that merge with datasource-defined parameters.
   * Each entry is matched by `name` against the datasource's own parameters;
   * matched entries override the default value, unmatched entries use the
   * datasource default. (Replicates WebApiController.cs lines 568-580)
   */
  paramList: DataSourceParameter[];
}

/**
 * Result of a datasource test execution.
 * When action='sql', the `sql` property contains the generated query string.
 * When action='data', the `data` property contains the serialized result set.
 * The `errors` array contains any EQL parsing or query execution errors.
 */
export interface DataSourceTestResult {
  /** Generated SQL query string (populated when action='sql') */
  sql: string;
  /** Serialized result set as a JSON string (populated when action='data') */
  data: string;
  /** Array of error objects from EQL parsing or query execution */
  errors: Array<{ message: string }>;
}

/**
 * Parameters for generating a report from the event-sourced read model.
 * The Reporting service consumes domain events from all bounded contexts
 * and builds read-optimized projections in RDS PostgreSQL.
 *
 * @property type - Report type identifier (e.g. 'sales', 'tasks', 'activity')
 * @property dateRange - Optional date range filter with ISO 8601 date strings
 * @property filters - Optional key-value filter criteria for narrowing results
 * @property groupBy - Optional field name to group report results by
 */
export interface ReportParams {
  /** Report type identifier used to select the appropriate read-model projection */
  type: string;
  /** Optional date range filter boundaries as ISO 8601 date strings */
  dateRange?: { from: string; to: string };
  /** Optional key-value filter criteria applied to the report query */
  filters?: Record<string, unknown>;
  /** Optional field name to group aggregated results by */
  groupBy?: string;
}

/**
 * Parameters for listing available reports with pagination and type filtering.
 *
 * @property page - Page number (1-based pagination)
 * @property pageSize - Number of report records per page
 * @property type - Optional report type filter to narrow results
 */
export interface ReportListParams {
  /** Page number for pagination (1-based) */
  page?: number;
  /** Number of report records to return per page */
  pageSize?: number;
  /** Optional filter by report type */
  type?: string;
}

// ─── DataSource API Functions ────────────────────────────────────────────────

/**
 * Validates C# datasource code compilation on the server.
 * Replaces WebApiController.cs DataSourceAction (line 494) which calls
 * CodeEvalService.Compile(model.CsCode) to validate code-based datasource
 * implementations without executing them.
 *
 * @param csCode - C# source code string to compile and validate server-side
 * @returns Compilation result with success status and diagnostic message
 *
 * @example
 * ```ts
 * const result = await compileDataSourceCode('public class MyDs { }');
 * if (result.object.success) {
 *   console.log('Compilation succeeded');
 * } else {
 *   console.error(result.object.message);
 * }
 * ```
 */
export async function compileDataSourceCode(
  csCode: string
): Promise<ApiResponse<{ success: boolean; message: string }>> {
  return post<{ success: boolean; message: string }>(
    '/reporting/datasources/code-compile',
    { csCode }
  );
}

/**
 * Tests a datasource by executing an EQL query directly.
 * Replaces WebApiController.cs DataSourceAction (line 511).
 *
 * When action='sql', the EQL is parsed and translated to SQL but not executed,
 * returning the generated SQL string for inspection.
 * When action='data', the EQL is parsed, translated, and executed against the
 * datastore, returning the serialized result set.
 *
 * @param params - Test parameters including action type, EQL string, parameters, and returnTotal flag
 * @returns Test result containing generated SQL, data, and any parsing/execution errors
 */
export async function testDataSource(
  params: DataSourceTestParams
): Promise<ApiResponse<DataSourceTestResult>> {
  return post<DataSourceTestResult>(
    '/reporting/datasources/test',
    params
  );
}

/**
 * Tests a datasource resolved by its registered ID.
 * Replaces WebApiController.cs DataSourceAction (line 542).
 *
 * The service resolves the datasource definition by ID, extracts the EQL from
 * the DatabaseDataSource, then merges page parameters with datasource-defined
 * parameters. The merge logic (WebApiController.cs lines 568-580) iterates
 * each datasource parameter and checks paramList for a matching entry by name;
 * if found, the paramList value is used, otherwise the datasource's default
 * value is preserved. After merging, execution proceeds as with testDataSource.
 *
 * @param dataSourceId - GUID identifier of the registered datasource to test
 * @param params - Test parameters with action type and parameter overrides
 * @returns Test result containing generated SQL, data, and any errors
 */
export async function testDataSourceById(
  dataSourceId: string,
  params: DataSourceTestByIdParams
): Promise<ApiResponse<DataSourceTestResult>> {
  return post<DataSourceTestResult>(
    `/reporting/datasources/${encodeURIComponent(dataSourceId)}/test`,
    params
  );
}

/**
 * Lists all registered datasources sorted by name.
 * Replaces AdminController.cs DataSourceAction (GET api/v3.0/p/sdk/datasource/list)
 * which calls DataSourceManager.GetAll() combining code-based and database-based
 * datasources, then orders the combined list by Name ascending.
 *
 * Each returned DataSourceBase includes metadata such as id, name, type,
 * description, parameters, fields, and resultModel.
 *
 * @returns Ordered array of all registered datasource metadata objects
 */
export async function listDataSources(): Promise<ApiResponse<DataSourceBase[]>> {
  return get<DataSourceBase[]>('/reporting/datasources');
}

// ─── Reporting API Functions ─────────────────────────────────────────────────

/**
 * Generates a report from the event-sourced read model.
 * The Reporting service consumes domain events from all bounded contexts
 * (via SQS subscriptions to SNS topics) and builds read-optimized projections
 * in RDS PostgreSQL. This endpoint queries those projections based on the
 * specified report type, date range, filters, and grouping.
 *
 * @param params - Report generation parameters including type, date range, filters, and grouping
 * @returns Generated report data as a key-value record
 */
export async function generateReport(
  params: ReportParams
): Promise<ApiResponse<Record<string, unknown>>> {
  return post<Record<string, unknown>>(
    '/reporting/reports',
    params
  );
}

/**
 * Retrieves a specific report by its unique identifier.
 *
 * @param reportId - Unique identifier of the report to retrieve
 * @returns Report data and metadata as a key-value record
 */
export async function getReportById(
  reportId: string
): Promise<ApiResponse<Record<string, unknown>>> {
  return get<Record<string, unknown>>(
    `/reporting/reports/${encodeURIComponent(reportId)}`
  );
}

/**
 * Lists available reports with optional pagination and type filtering.
 * Constructs query parameters from the optional ReportListParams and
 * appends them to the request URL.
 *
 * @param params - Optional parameters for pagination (page, pageSize) and type filtering
 * @returns Paginated array of available report metadata records
 */
export async function listReports(
  params?: ReportListParams
): Promise<ApiResponse<Record<string, unknown>[]>> {
  const queryParts: string[] = [];

  if (params?.page !== undefined) {
    queryParts.push(`page=${encodeURIComponent(String(params.page))}`);
  }
  if (params?.pageSize !== undefined) {
    queryParts.push(`pageSize=${encodeURIComponent(String(params.pageSize))}`);
  }
  if (params?.type !== undefined && params.type !== '') {
    queryParts.push(`type=${encodeURIComponent(params.type)}`);
  }

  const queryString = queryParts.length > 0 ? `?${queryParts.join('&')}` : '';
  return get<Record<string, unknown>[]>(`/reporting/reports${queryString}`);
}

/**
 * Retrieves dashboard analytics data from the Reporting service.
 * The dashboard aggregates data from multiple event-sourced read models
 * to present a consolidated overview of system metrics and KPIs.
 *
 * @param dashboardId - Optional dashboard identifier; when omitted, returns the primary/default dashboard
 * @returns Dashboard analytics data as a key-value record
 */
export async function getDashboardData(
  dashboardId?: string
): Promise<ApiResponse<Record<string, unknown>>> {
  const queryParts: string[] = [];

  if (dashboardId !== undefined && dashboardId !== '') {
    queryParts.push(`dashboardId=${encodeURIComponent(dashboardId)}`);
  }

  const queryString = queryParts.length > 0 ? `?${queryParts.join('&')}` : '';
  return get<Record<string, unknown>>(`/reporting/dashboards${queryString}`);
}
