/**
 * DashboardView.tsx — Report Dashboard Rendering Page
 *
 * Replaces the monolith's details.cshtml / details.cshtml.cs data source
 * details page (route /sdk/objects/data_source/r/{RecordId}/{PageName?}).
 * Fetches the dashboard definition from the Reporting service API and
 * renders metadata in read-only display mode, chart visualizations, and
 * tabular report output.
 *
 * Route: /reports/view/:id
 * Sources:
 *   - WebVella.Erp.Plugins.SDK/Pages/data_source/details.cshtml.cs
 *   - WebVella.Erp.Plugins.SDK/Pages/data_source/details.cshtml
 *   - WebVella.Erp/Api/DataSourceManager.cs (Get, Delete)
 *   - WebVella.Erp/Database/DbDataSourceRepository.cs
 */

import { useState, useCallback } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { get, post, del } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import Chart, { ChartType } from '../../components/common/Chart';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Modal, { ModalSize } from '../../components/common/Modal';
import {
  DataSourceType,
} from '../../types/datasource';
import type {
  DataSourceBase,
  DatabaseDataSource,
  DataSourceParameter,
  DataSourceTestModel,
} from '../../types/datasource';

/* ================================================================
 * LOCAL TYPES
 * ================================================================ */

/**
 * Extended response from the GET dashboard endpoint.
 * Includes the datasource definition together with a reference flag
 * that mirrors the monolith's `PageService().GetPageDataSourcesByDataSourceId()`
 * lock check.
 */
interface DashboardDetailResponse {
  dashboard: DatabaseDataSource;
  hasReferences: boolean;
}

/**
 * Result shape from the dashboard execution endpoint.
 * Provides chart-compatible data and tabular row data for the
 * DataTable component.
 */
interface DashboardExecutionResult {
  chartData?: {
    labels: string[];
    datasets: Array<{ label: string; data: number[] }>;
    chartType?: string;
  };
  tableData?: {
    columns: string[];
    rows: Array<Record<string, unknown>>;
    totalCount: number;
  };
}

/* ================================================================
 * QUERY KEY CONSTANTS
 * ================================================================ */

const QUERY_KEY_PREFIX = 'reporting';
const DASHBOARDS_KEY = 'dashboards';

/* ================================================================
 * HELPER FUNCTIONS
 * ================================================================ */

/**
 * Returns a human-readable display name for the dashboard.
 * Falls back to a generic label when the name is empty.
 */
function extractDisplayName(ds: DataSourceBase | undefined): string {
  return ds?.name || 'Unnamed Dashboard';
}

/**
 * Maps a DataSourceType to a human-readable label.
 *
 * Source: details.cshtml lines 44-68 — type-specific icon cards
 * with "Database" or "Code" labels.
 */
function getTypeLabel(type: DataSourceType | undefined): string {
  switch (type) {
    case DataSourceType.Database:
      return 'Database';
    case DataSourceType.Code:
      return 'Code';
    default:
      return 'Unknown';
  }
}

/**
 * Returns the type description shown on the indicator card.
 *
 * Source: details.cshtml — "SQL Select via EQL syntax" for
 * Database type, "Code-based data source" for Code type.
 */
function getTypeDescription(type: DataSourceType | undefined): string {
  switch (type) {
    case DataSourceType.Database:
      return 'SQL Select via EQL syntax';
    case DataSourceType.Code:
      return 'Code-based data source';
    default:
      return '';
  }
}

/**
 * Serializes parameters for the test execution payload.
 * Mirrors the monolith's parameter serialization format used by the
 * jQuery test POST handler in details.cshtml.
 */
function serializeParametersForTest(
  params: DataSourceParameter[] | undefined,
): string {
  if (!params || params.length === 0) {
    return '';
  }
  return params
    .map((p) => {
      const parts = [p.name ?? '', p.type ?? '', p.value ?? ''];
      if (p.ignoreParseErrors) {
        parts.push('true');
      }
      return parts.join(',');
    })
    .join('\n');
}

/**
 * Maps a chart type string from the API to the ChartType enum.
 * Falls back to Line chart when the type is unrecognised.
 */
function resolveChartType(apiType: string | undefined): ChartType {
  if (!apiType) {
    return ChartType.Bar;
  }
  const normalised = apiType.toLowerCase();
  const mapping: Record<string, ChartType> = {
    line: ChartType.Line,
    bar: ChartType.Bar,
    pie: ChartType.Pie,
    doughnut: ChartType.Doughnut,
    area: ChartType.Area,
    radar: ChartType.Radar,
    polararea: ChartType.PolarArea,
    horizontalbar: ChartType.HorizontalBar,
  };
  return mapping[normalised] ?? ChartType.Bar;
}

/**
 * Builds DataTableColumn definitions dynamically from column-name
 * strings returned by the dashboard execution endpoint.
 */
function buildTableColumns(
  columnNames: string[],
): DataTableColumn<Record<string, unknown>>[] {
  return columnNames.map((colName) => ({
    id: colName,
    name: colName,
    label: colName.charAt(0).toUpperCase() + colName.slice(1).replace(/_/g, ' '),
    sortable: true,
    accessorKey: colName,
  }));
}

/* ================================================================
 * COMPONENT
 * ================================================================ */

/**
 * DashboardView — Report Dashboard Detail Page
 *
 * Full behavioral parity with the monolith's data source details page:
 * - Fetches dashboard definition (GET /v1/reporting/dashboards/:id)
 * - Displays read-only metadata (name, description, entity, model,
 *   weight, returnTotal, parameters)
 * - Type indicator card with icon (Database = purple, Code = pink)
 * - Manage button → /reports/manage/:id (locked for Code type)
 * - Delete button with confirmation modal (locked if has references
 *   or Code type)
 * - Test execution: Preview Query (SQL) and Sample Data (JSON)
 *   via POST /v1/reporting/dashboards/test
 * - Visualization section: Chart + DataTable from execution data
 * - 404 handling when dashboard not found
 * - Loading and error states
 */
export default function DashboardView(): React.ReactNode {
  /* ── Route params & navigation ────────────────────────────── */
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ── Local state (replaces jQuery modal toggles) ──────────── */
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [showTestModal, setShowTestModal] = useState(false);
  const [testAction, setTestAction] = useState<'sql' | 'data'>('sql');
  const [testResult, setTestResult] = useState<string>('');
  const [isTestLoading, setIsTestLoading] = useState(false);
  const [showQuerySection, setShowQuerySection] = useState(false);

  /* ── Dashboard definition query ───────────────────────────── */
  const {
    data: detailResponse,
    isLoading,
    isError,
    error: fetchError,
  } = useQuery<ApiResponse<DashboardDetailResponse>>({
    queryKey: [QUERY_KEY_PREFIX, DASHBOARDS_KEY, id],
    queryFn: () =>
      get<DashboardDetailResponse>(`/v1/reporting/dashboards/${id}`),
    enabled: !!id,
    retry: (failureCount, error) => {
      const apiErr = error as unknown as ApiError;
      if (apiErr?.status === 404) {
        return false;
      }
      return failureCount < 3;
    },
  });

  /* ── Dashboard execution query (chart + table data) ───────── */
  const {
    data: executionResponse,
    isLoading: isExecutionLoading,
  } = useQuery<ApiResponse<DashboardExecutionResult>>({
    queryKey: [QUERY_KEY_PREFIX, DASHBOARDS_KEY, id, 'data'],
    queryFn: () =>
      post<DashboardExecutionResult>(
        `/v1/reporting/dashboards/${id}/execute`,
        {},
      ),
    enabled:
      !!id &&
      !!detailResponse?.success &&
      !!detailResponse?.object?.dashboard,
  });

  /* ── Delete mutation ──────────────────────────────────────── */
  const deleteMutation = useMutation<
    ApiResponse<void>,
    ApiError,
    void
  >({
    mutationFn: () => del<void>(`/v1/reporting/dashboards/${id}`),
    onSuccess: (response) => {
      if (response.success) {
        queryClient.invalidateQueries({
          queryKey: [QUERY_KEY_PREFIX, DASHBOARDS_KEY],
        });
        queryClient.invalidateQueries({
          queryKey: [QUERY_KEY_PREFIX],
        });
        navigate('/reports');
      }
    },
  });

  /* ── Delete confirmation handler ──────────────────────────── */
  const handleDeleteConfirm = useCallback(() => {
    deleteMutation.mutate();
    setShowDeleteModal(false);
  }, [deleteMutation]);

  const handleDeleteCancel = useCallback(() => {
    setShowDeleteModal(false);
  }, []);

  const handleDeleteClick = useCallback(() => {
    setShowDeleteModal(true);
  }, []);

  /* ── Test execution handler ───────────────────────────────── */
  const handleTestExecution = useCallback(
    async (action: 'sql' | 'data') => {
      if (!detailResponse?.object?.dashboard) {
        return;
      }
      const dashboard = detailResponse.object.dashboard;

      setIsTestLoading(true);
      setTestAction(action);
      setShowTestModal(true);
      setTestResult('');

      try {
        const dbDashboard = dashboard as DatabaseDataSource;
        const payload: DataSourceTestModel = {
          action,
          eql: dbDashboard.eqlText ?? '',
          parameters: serializeParametersForTest(dashboard.parameters),
          paramList: dashboard.parameters ?? [],
          returnTotal: dashboard.returnTotal ?? true,
        };

        const response = await post<{ result: string }>(
          '/v1/reporting/dashboards/test',
          payload,
        );

        if (response.success && response.object) {
          setTestResult(
            typeof response.object.result === 'string'
              ? response.object.result
              : JSON.stringify(response.object, null, 2),
          );
        } else {
          setTestResult(
            response.message ?? 'Test execution returned no results.',
          );
        }
      } catch (err: unknown) {
        const apiErr = err as ApiError;
        setTestResult(apiErr.message ?? 'Test execution failed.');
      } finally {
        setIsTestLoading(false);
      }
    },
    [detailResponse],
  );

  const handleTestModalClose = useCallback(() => {
    setShowTestModal(false);
    setTestResult('');
  }, []);

  const handleToggleQuerySection = useCallback(() => {
    setShowQuerySection((prev) => !prev);
  }, []);

  /* ── Derived state ────────────────────────────────────────── */
  const dashboardDetail = detailResponse?.object;
  const dashboard = dashboardDetail?.dashboard;
  const hasReferences = dashboardDetail?.hasReferences ?? false;
  const isDatabaseType = dashboard?.type === DataSourceType.Database;
  const isCodeType = dashboard?.type === DataSourceType.Code;
  const isDeleteLocked = hasReferences || isCodeType;
  const isManageLocked = isCodeType;
  const dashboardName = extractDisplayName(dashboard);
  const isNotFound =
    isError && (fetchError as unknown as ApiError)?.status === 404;
  const isDeleting = deleteMutation.isPending;

  /* Execution data */
  const executionData = executionResponse?.object;
  const chartData = executionData?.chartData;
  const tableData = executionData?.tableData;

  /* ================================================================
   * RENDER — Loading State
   * ================================================================ */
  if (isLoading) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent"
            role="status"
            aria-label="Loading dashboard"
          />
          <p className="text-sm text-gray-500">Loading dashboard…</p>
        </div>
      </div>
    );
  }

  /* ================================================================
   * RENDER — Not Found State (404)
   * ================================================================ */
  if (isNotFound) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-12 text-center">
        <div className="mb-4 text-6xl" aria-hidden="true">
          🔍
        </div>
        <h1 className="mb-2 text-2xl font-semibold text-gray-900">
          Dashboard Not Found
        </h1>
        <p className="mb-6 text-gray-500">
          The dashboard you are looking for does not exist or has been removed.
        </p>
        <Link
          to="/reports"
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to Dashboard List
        </Link>
      </div>
    );
  }

  /* ================================================================
   * RENDER — Generic Error State
   * ================================================================ */
  if (isError || !dashboard) {
    const apiErr = fetchError as unknown as ApiError;
    return (
      <div className="mx-auto max-w-3xl px-4 py-12 text-center">
        <div className="mb-4 text-6xl" aria-hidden="true">
          ⚠️
        </div>
        <h1 className="mb-2 text-2xl font-semibold text-gray-900">
          Error Loading Dashboard
        </h1>
        <p className="mb-6 text-gray-500">
          {apiErr?.message ?? 'An unexpected error occurred while loading the dashboard.'}
        </p>
        <Link
          to="/reports"
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to Dashboard List
        </Link>
      </div>
    );
  }

  /* Cast for database-type fields */
  const dbDashboard = dashboard as DatabaseDataSource;

  /* ================================================================
   * RENDER — Main Dashboard View
   * ================================================================ */
  return (
    <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page Header ──────────────────────────────────────── */}
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0 flex-1">
          <nav className="mb-1" aria-label="Breadcrumb">
            <Link
              to="/reports"
              className="text-sm text-blue-600 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              ← Back to Dashboards
            </Link>
          </nav>
          <h1 className="truncate text-2xl font-bold text-gray-900">
            {dashboardName}
          </h1>
          <p className="mt-1 text-sm text-gray-500">Details</p>
        </div>

        {/* ── Header Actions ──────────────────────────────── */}
        <div className="flex flex-shrink-0 items-center gap-3">
          {/* Manage button: enabled for DatabaseDataSource, locked for CodeDataSource */}
          {isManageLocked ? (
            <button
              type="button"
              disabled
              className="inline-flex items-center rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-400 cursor-not-allowed"
              title="Code-based data sources cannot be edited"
              aria-disabled="true"
            >
              <svg
                className="mr-1.5 h-4 w-4"
                aria-hidden="true"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M16.5 10.5V6.75a4.5 4.5 0 1 0-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 0 0 2.25-2.25v-6.75a2.25 2.25 0 0 0-2.25-2.25H6.75a2.25 2.25 0 0 0-2.25 2.25v6.75a2.25 2.25 0 0 0 2.25 2.25Z"
                />
              </svg>
              Manage Locked
            </button>
          ) : (
            <Link
              to={`/reports/manage/${id}`}
              className="inline-flex items-center rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <svg
                className="mr-1.5 h-4 w-4"
                aria-hidden="true"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="m16.862 4.487 1.687-1.688a1.875 1.875 0 1 1 2.652 2.652L10.582 16.07a4.5 4.5 0 0 1-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 0 1 1.13-1.897l8.932-8.931Zm0 0L19.5 7.125M18 14v4.75A2.25 2.25 0 0 1 15.75 21H5.25A2.25 2.25 0 0 1 3 18.75V8.25A2.25 2.25 0 0 1 5.25 6H10"
                />
              </svg>
              Manage
            </Link>
          )}

          {/* Delete button: disabled when locked */}
          {isDeleteLocked ? (
            <button
              type="button"
              disabled
              className="inline-flex items-center rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-400 cursor-not-allowed"
              title={
                isCodeType
                  ? 'Code-based data sources cannot be deleted'
                  : 'This dashboard is referenced by page data sources and cannot be deleted'
              }
              aria-disabled="true"
            >
              <svg
                className="mr-1.5 h-4 w-4"
                aria-hidden="true"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M16.5 10.5V6.75a4.5 4.5 0 1 0-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 0 0 2.25-2.25v-6.75a2.25 2.25 0 0 0-2.25-2.25H6.75a2.25 2.25 0 0 0-2.25 2.25v6.75a2.25 2.25 0 0 0 2.25 2.25Z"
                />
              </svg>
              Delete Locked
            </button>
          ) : (
            <button
              type="button"
              onClick={handleDeleteClick}
              disabled={isDeleting}
              className="inline-flex items-center rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <svg
                className="mr-1.5 h-4 w-4"
                aria-hidden="true"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="m14.74 9-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 0 1-2.244 2.077H8.084a2.25 2.25 0 0 1-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 0 0-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 0 1 3.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 0 0-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 0 0-7.5 0"
                />
              </svg>
              {isDeleting ? 'Deleting…' : 'Delete'}
            </button>
          )}
        </div>
      </div>

      {/* ── Type Indicator Card ───────────────────────────────── */}
      <div className="mb-6">
        {isDatabaseType ? (
          <div className="flex items-center gap-4 rounded-lg border border-purple-200 bg-purple-50 p-4">
            <div className="flex h-12 w-12 flex-shrink-0 items-center justify-center rounded-lg bg-purple-100">
              <svg
                className="h-6 w-6 text-purple-600"
                aria-hidden="true"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M20.25 6.375c0 2.278-3.694 4.125-8.25 4.125S3.75 8.653 3.75 6.375m16.5 0c0-2.278-3.694-4.125-8.25-4.125S3.75 4.097 3.75 6.375m16.5 0v11.25c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125V6.375m16.5 0v3.75m-16.5-3.75v3.75m16.5 0v3.75C20.25 16.153 16.556 18 12 18s-8.25-1.847-8.25-4.125v-3.75m16.5 0c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125"
                />
              </svg>
            </div>
            <div>
              <p className="text-sm font-semibold text-purple-800">
                {getTypeLabel(dashboard.type)}
              </p>
              <p className="text-sm text-purple-600">
                {getTypeDescription(dashboard.type)}
              </p>
            </div>
          </div>
        ) : (
          <div className="flex items-center gap-4 rounded-lg border border-pink-200 bg-pink-50 p-4">
            <div className="flex h-12 w-12 flex-shrink-0 items-center justify-center rounded-lg bg-pink-100">
              <svg
                className="h-6 w-6 text-pink-600"
                aria-hidden="true"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M17.25 6.75 22.5 12l-5.25 5.25m-10.5 0L1.5 12l5.25-5.25m7.5-3-4.5 16.5"
                />
              </svg>
            </div>
            <div>
              <p className="text-sm font-semibold text-pink-800">
                {getTypeLabel(dashboard.type)}
              </p>
              <p className="text-sm text-pink-600">
                {getTypeDescription(dashboard.type)}
              </p>
            </div>
          </div>
        )}
      </div>

      {/* ── Metadata Section ──────────────────────────────────── */}
      <section className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-200 px-6 py-4">
          <h2 className="text-lg font-semibold text-gray-900">
            Dashboard Properties
          </h2>
        </div>
        <div className="grid grid-cols-1 gap-6 px-6 py-5 sm:grid-cols-2">
          {/* Name */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Name</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {dashboard.name || '—'}
            </dd>
          </div>

          {/* Description */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Description</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {dashboard.description || '—'}
            </dd>
          </div>

          {/* Entity Name */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Entity Name</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {dashboard.entityName || '—'}
            </dd>
          </div>

          {/* Result Model */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Result Model</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {dashboard.resultModel || '—'}
            </dd>
          </div>

          {/* Weight */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Weight</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {dashboard.weight ?? 0}
            </dd>
          </div>

          {/* Return Total */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Return Total</dt>
            <dd className="mt-1 flex items-center text-sm text-gray-900">
              {dashboard.returnTotal ? (
                <span className="inline-flex items-center gap-1.5 text-green-700">
                  <svg
                    className="h-4 w-4"
                    aria-hidden="true"
                    fill="none"
                    viewBox="0 0 24 24"
                    strokeWidth={2}
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="m4.5 12.75 6 6 9-13.5"
                    />
                  </svg>
                  Yes
                </span>
              ) : (
                <span className="inline-flex items-center gap-1.5 text-gray-500">
                  <svg
                    className="h-4 w-4"
                    aria-hidden="true"
                    fill="none"
                    viewBox="0 0 24 24"
                    strokeWidth={2}
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M6 18 18 6M6 6l12 12"
                    />
                  </svg>
                  No
                </span>
              )}
            </dd>
          </div>
        </div>
      </section>

      {/* ── Parameters Section ────────────────────────────────── */}
      {dashboard.parameters && dashboard.parameters.length > 0 && (
        <section className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">
              Parameters
            </h2>
          </div>
          <div className="px-6 py-4">
            <div className="flex flex-wrap gap-2">
              {dashboard.parameters.map((param: DataSourceParameter, index: number) => (
                <span
                  key={`${param.name}-${index}`}
                  className="inline-flex items-center rounded-full bg-blue-50 px-3 py-1.5 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-600/20"
                >
                  {param.name}
                  {param.value ? (
                    <>
                      <span className="mx-1 text-blue-400">??</span>
                      {param.value}
                    </>
                  ) : null}
                  {param.type ? (
                    <span className="ml-1 text-blue-400">
                      ({param.type})
                    </span>
                  ) : null}
                </span>
              ))}
            </div>
          </div>
        </section>
      )}

      {/* ── Visualization Section — Chart ─────────────────────── */}
      {chartData && chartData.labels && chartData.datasets && (
        <section className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">
              Visualization
            </h2>
          </div>
          <div className="px-6 py-5">
            <Chart
              type={resolveChartType(chartData.chartType)}
              labels={chartData.labels}
              datasets={chartData.datasets.map((ds) => ({
                label: ds.label,
                data: ds.data,
              }))}
              height="320px"
              showLegend
            />
          </div>
        </section>
      )}

      {/* ── Visualization Section — Data Table ────────────────── */}
      {tableData && tableData.rows && tableData.rows.length > 0 && (
        <section className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 px-6 py-4">
            <h2 className="text-lg font-semibold text-gray-900">
              Report Data
            </h2>
          </div>
          <div className="px-2 py-2">
            <DataTable<Record<string, unknown>>
              data={tableData.rows}
              columns={buildTableColumns(tableData.columns)}
              totalCount={tableData.totalCount}
              pageSize={10}
              striped
              hover
              bordered
              showHeader
              showFooter
              emptyText="No report data available"
            />
          </div>
        </section>
      )}

      {/* ── Execution Loading Indicator ───────────────────────── */}
      {isExecutionLoading && (
        <div className="mb-6 flex items-center justify-center rounded-lg border border-gray-200 bg-white p-8 shadow-sm">
          <div className="text-center">
            <div
              className="mx-auto mb-3 h-6 w-6 animate-spin rounded-full border-2 border-blue-600 border-t-transparent"
              role="status"
              aria-label="Loading dashboard data"
            />
            <p className="text-sm text-gray-500">
              Executing dashboard query…
            </p>
          </div>
        </div>
      )}

      {/* ── Query / Configuration Section (collapsible) ──────── */}
      {isDatabaseType && (
        <section className="mb-6 rounded-lg border border-gray-200 bg-white shadow-sm">
          <button
            type="button"
            onClick={handleToggleQuerySection}
            className="flex w-full items-center justify-between px-6 py-4 text-left focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            aria-expanded={showQuerySection}
          >
            <h2 className="text-lg font-semibold text-gray-900">
              Query Configuration
            </h2>
            <svg
              className={`h-5 w-5 text-gray-400 transition-transform duration-200 ${
                showQuerySection ? 'rotate-180' : ''
              }`}
              aria-hidden="true"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="m19.5 8.25-7.5 7.5-7.5-7.5"
              />
            </svg>
          </button>

          {showQuerySection && (
            <div className="border-t border-gray-200 px-6 py-5">
              {/* EQL Text */}
              <div className="mb-5">
                <h3 className="mb-2 text-sm font-medium text-gray-700">
                  EQL Query
                </h3>
                <pre className="overflow-x-auto rounded-md bg-gray-900 p-4 text-sm text-green-400">
                  <code>{dbDashboard.eqlText || '(No EQL configured)'}</code>
                </pre>
              </div>

              {/* SQL Text (generated) */}
              {dbDashboard.sqlText && (
                <div className="mb-5">
                  <h3 className="mb-2 text-sm font-medium text-gray-700">
                    Generated SQL
                  </h3>
                  <pre className="overflow-x-auto rounded-md bg-gray-900 p-4 text-sm text-blue-300">
                    <code>{dbDashboard.sqlText}</code>
                  </pre>
                </div>
              )}

              {/* Test execution buttons */}
              <div className="flex flex-wrap gap-3">
                <button
                  type="button"
                  onClick={() => handleTestExecution('sql')}
                  disabled={isTestLoading}
                  className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <svg
                    className="mr-1.5 h-4 w-4"
                    aria-hidden="true"
                    fill="none"
                    viewBox="0 0 24 24"
                    strokeWidth={1.5}
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M5.25 5.653c0-.856.917-1.398 1.667-.986l11.54 6.347a1.125 1.125 0 0 1 0 1.972l-11.54 6.347a1.125 1.125 0 0 1-1.667-.986V5.653Z"
                    />
                  </svg>
                  {isTestLoading && testAction === 'sql'
                    ? 'Running…'
                    : 'Preview Query (SQL)'}
                </button>

                <button
                  type="button"
                  onClick={() => handleTestExecution('data')}
                  disabled={isTestLoading}
                  className="inline-flex items-center rounded-md bg-teal-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-teal-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-teal-600 disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <svg
                    className="mr-1.5 h-4 w-4"
                    aria-hidden="true"
                    fill="none"
                    viewBox="0 0 24 24"
                    strokeWidth={1.5}
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M19.5 14.25v-2.625a3.375 3.375 0 0 0-3.375-3.375h-1.5A1.125 1.125 0 0 1 13.5 7.125v-1.5a3.375 3.375 0 0 0-3.375-3.375H8.25m0 12.75h7.5m-7.5 3H12M10.5 2.25H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 0 0-9-9Z"
                    />
                  </svg>
                  {isTestLoading && testAction === 'data'
                    ? 'Running…'
                    : 'Sample Data (JSON)'}
                </button>
              </div>
            </div>
          )}
        </section>
      )}

      {/* ── Delete Confirmation Modal ─────────────────────────── */}
      <Modal
        isVisible={showDeleteModal}
        title="Delete Dashboard"
        onClose={handleDeleteCancel}
      >
        <div className="py-4">
          <p className="text-sm text-gray-600">
            Are you sure you want to delete the dashboard{' '}
            <strong className="font-semibold text-gray-900">
              {dashboardName}
            </strong>
            ? This action cannot be undone.
          </p>
        </div>
        <div className="flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
          <button
            type="button"
            onClick={handleDeleteCancel}
            className="rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleDeleteConfirm}
            disabled={isDeleting}
            className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isDeleting ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </Modal>

      {/* ── Test Execution Result Modal ────────────────────────── */}
      <Modal
        isVisible={showTestModal}
        title={
          testAction === 'sql'
            ? 'SQL Result'
            : 'Sample Data Result'
        }
        size={ModalSize.Large}
        onClose={handleTestModalClose}
      >
        <div className="py-4">
          {testAction === 'data' && (
            <div
              className="mb-4 rounded-md border border-yellow-200 bg-yellow-50 p-3"
              role="alert"
            >
              <div className="flex items-start">
                <svg
                  className="mr-2 mt-0.5 h-4 w-4 flex-shrink-0 text-yellow-600"
                  aria-hidden="true"
                  fill="none"
                  viewBox="0 0 24 24"
                  strokeWidth={1.5}
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z"
                  />
                </svg>
                <p className="text-xs text-yellow-800">
                  The data below is a sample representation. Production
                  results may vary depending on the current database state.
                </p>
              </div>
            </div>
          )}

          {isTestLoading ? (
            <div className="flex items-center justify-center py-8">
              <div
                className="h-6 w-6 animate-spin rounded-full border-2 border-blue-600 border-t-transparent"
                role="status"
                aria-label="Running test execution"
              />
              <span className="ml-3 text-sm text-gray-500">
                Executing…
              </span>
            </div>
          ) : (
            <pre
              className={`max-h-[60vh] overflow-auto rounded-md p-4 text-sm ${
                testAction === 'sql'
                  ? 'bg-gray-900 text-blue-300'
                  : 'bg-gray-900 text-green-400'
              }`}
            >
              <code>{testResult || '(No results)'}</code>
            </pre>
          )}
        </div>
        <div className="flex items-center justify-end border-t border-gray-200 pt-4">
          <button
            type="button"
            onClick={handleTestModalClose}
            className="rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Close
          </button>
        </div>
      </Modal>

      {/* ── Delete Mutation Error Display ──────────────────────── */}
      {deleteMutation.isError && (
        <div
          className="fixed inset-x-0 bottom-0 z-50 mx-auto mb-6 max-w-xl rounded-lg border border-red-200 bg-red-50 p-4 shadow-lg"
          role="alert"
        >
          <div className="flex items-start">
            <svg
              className="mr-2 mt-0.5 h-5 w-5 flex-shrink-0 text-red-600"
              aria-hidden="true"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M12 9v3.75m9-.75a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-9 3.75h.008v.008H12v-.008Z"
              />
            </svg>
            <div>
              <h3 className="text-sm font-semibold text-red-800">
                Delete Failed
              </h3>
              <p className="mt-1 text-sm text-red-700">
                {(deleteMutation.error as ApiError)?.message ??
                  'An unexpected error occurred while deleting the dashboard.'}
              </p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
