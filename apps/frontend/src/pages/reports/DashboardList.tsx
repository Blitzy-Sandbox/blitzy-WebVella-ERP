/**
 * DashboardList — Report Dashboard Listing Page
 *
 * Replaces the monolith's SDK plugin data-source listing page:
 *   - Route: `/sdk/objects/data_source/l/{PageName?}` → `/reports`
 *   - Source: `list.cshtml.cs` (OnGet → filter → sort → paginate)
 *   - Source: `list.cshtml` (`<wv-grid>`, `<wv-drawer>` filter form)
 *   - Data:  `DataSourceManager.GetAll()` → `GET /v1/reporting/dashboards`
 *
 * Behavioral parity preserved (AAP §0.8.1):
 *   - All 7 columns: action, icon, Name (sortable), Type (sortable),
 *     Target (sortable), Returned Model, Params
 *   - Filters: name (CONTAINS), target (CONTAINS), model (CONTAINS),
 *     type (EQ — Database / Code)
 *   - Pagination: pageSize=15 default, URL-driven page state
 *   - Sorting: name, type, target — ascending/descending toggle
 *
 * Design patterns:
 *   - TanStack Query v5 for server state (caching, stale-while-revalidate)
 *   - React Router v7 useSearchParams for URL-driven filter/sort/page state
 *   - DataTable component replaces `<wv-grid>` tag helper
 *   - Tailwind CSS 4.x replaces Bootstrap 4 styling
 *
 * @module pages/reports/DashboardList
 */

import React, { useState, useMemo, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { get } from '../../api/client';
import {
  DataTable,
  type DataTableColumn,
} from '../../components/data-table/DataTable';
import { type DataSourceBase, DataSourceType } from '../../types/datasource';

// ---------------------------------------------------------------------------
// Local record type — satisfies DataTable's Record<string, unknown> constraint
// ---------------------------------------------------------------------------

/**
 * Row type for the DataTable. Extends DataSourceBase with a string index
 * signature so it satisfies the `Record<string, unknown>` generic constraint
 * required by the DataTable component.
 */
type DashboardRecord = DataSourceBase & Record<string, unknown>;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default number of rows per page — matches monolith's PageSize = 15. */
const DEFAULT_PAGE_SIZE = 15;

/** Query key namespace for the reporting dashboards listing. */
const QUERY_KEY_PREFIX = 'reporting';
const QUERY_KEY_ENTITY = 'dashboards';

/** Reporting API endpoint for dashboard listing. */
const DASHBOARDS_ENDPOINT = '/reporting/dashboards';

/**
 * Type filter options matching the monolith's `TypeOptions` list:
 *   new SelectOption() { Value = "0", Label = "DATABASE" }
 *   new SelectOption() { Value = "1", Label = "CODE" }
 */
const TYPE_FILTER_OPTIONS: ReadonlyArray<{
  value: string;
  label: string;
}> = [
  { value: '', label: 'All Types' },
  { value: String(DataSourceType.Database), label: 'Database' },
  { value: String(DataSourceType.Code), label: 'Code' },
] as const;

// ---------------------------------------------------------------------------
// Response shape
// ---------------------------------------------------------------------------

/**
 * Expected response payload from `GET /v1/reporting/dashboards`.
 * The API wraps this in an `ApiResponse<DashboardListPayload>` envelope.
 */
interface DashboardListPayload {
  /** Array of data-source records for the current page. */
  items: DataSourceBase[];
  /** Total record count (after filters) for pagination. */
  totalCount: number;
}

// ---------------------------------------------------------------------------
// Helper: human-readable label for DataSourceType
// ---------------------------------------------------------------------------

/**
 * Returns a display label for the DataSourceType enum.
 * Mirrors `DataSourceType.GetLabel()` from the monolith.
 */
function getTypeLabel(type: DataSourceType): string {
  switch (type) {
    case DataSourceType.Database:
      return 'Database';
    case DataSourceType.Code:
      return 'Code';
    default:
      return 'Unknown';
  }
}

// ---------------------------------------------------------------------------
// Helper: generate page description text
// ---------------------------------------------------------------------------

/**
 * Produces a description string for the page header.
 * Mirrors `PageUtils.GenerateListPageDescription` from the monolith.
 */
function generatePageDescription(
  totalCount: number,
  activeFilterCount: number,
): string {
  const recordWord = totalCount === 1 ? 'record' : 'records';
  if (activeFilterCount > 0) {
    const filterWord = activeFilterCount === 1 ? 'filter' : 'filters';
    return `${totalCount} ${recordWord} found with ${activeFilterCount} active ${filterWord}`;
  }
  return `${totalCount} ${recordWord} total`;
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * DashboardList — lists all report dashboards in a sortable, filterable,
 * paginated data table. This is the default route-level component for
 * `/reports`.
 */
export default function DashboardList(): React.ReactElement {
  // ── React Router state ────────────────────────────────────────────────
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  // ── Local UI state: filter panel open/close toggle ────────────────────
  const [filterPanelOpen, setFilterPanelOpen] = useState<boolean>(false);

  // ── Extract URL-driven pagination/sort/filter state ───────────────────
  const page = Math.max(1, parseInt(searchParams.get('page') || '1', 10) || 1);
  const pageSize =
    parseInt(searchParams.get('pageSize') || '', 10) || DEFAULT_PAGE_SIZE;
  const sortBy = searchParams.get('sortBy') || '';
  const sortOrder = (searchParams.get('sortOrder') || 'asc') as
    | 'asc'
    | 'desc';

  // Filters from URL search params (matches monolith's PageUtils.GetPageFiltersFromQuery)
  const nameFilter = searchParams.get('name') || '';
  const targetFilter = searchParams.get('target') || '';
  const modelFilter = searchParams.get('model') || '';
  const typeFilter = searchParams.get('type') || '';

  // ── Count active filters ──────────────────────────────────────────────
  const activeFilterCount = useMemo(() => {
    let count = 0;
    if (nameFilter) count += 1;
    if (targetFilter) count += 1;
    if (modelFilter) count += 1;
    if (typeFilter) count += 1;
    return count;
  }, [nameFilter, targetFilter, modelFilter, typeFilter]);

  // ── TanStack Query: fetch dashboards ──────────────────────────────────
  const {
    data: queryResult,
    isLoading,
    isError,
    error,
  } = useQuery({
    queryKey: [
      QUERY_KEY_PREFIX,
      QUERY_KEY_ENTITY,
      {
        page,
        pageSize,
        sortBy,
        sortOrder,
        filters: {
          name: nameFilter,
          target: targetFilter,
          model: modelFilter,
          type: typeFilter,
        },
      },
    ],
    queryFn: async () => {
      const params: Record<string, unknown> = {
        page,
        pageSize,
      };
      if (sortBy) {
        params.sortBy = sortBy;
        params.sortOrder = sortOrder;
      }
      if (nameFilter) params.name = nameFilter;
      if (targetFilter) params.target = targetFilter;
      if (modelFilter) params.model = modelFilter;
      if (typeFilter) params.type = typeFilter;

      const response = await get<DashboardListPayload>(
        DASHBOARDS_ENDPOINT,
        params,
      );
      return response;
    },
    placeholderData: (previousData) => previousData,
  });

  // ── Resolve data from query result envelope ───────────────────────────
  const dashboards: DashboardRecord[] =
    (queryResult?.object?.items as DashboardRecord[] | undefined) ?? [];
  const totalCount: number = queryResult?.object?.totalCount ?? 0;

  // ── Filter change handler ─────────────────────────────────────────────
  const handleFilterChange = useCallback(
    (filterName: string, filterValue: string) => {
      setSearchParams((prev) => {
        const params = new URLSearchParams(prev);
        if (filterValue) {
          params.set(filterName, filterValue);
        } else {
          params.delete(filterName);
        }
        // Reset to page 1 on any filter change
        params.set('page', '1');
        return params;
      });
    },
    [setSearchParams],
  );

  // ── Clear all filters ─────────────────────────────────────────────────
  const handleClearAllFilters = useCallback(() => {
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      params.delete('name');
      params.delete('target');
      params.delete('model');
      params.delete('type');
      params.set('page', '1');
      return params;
    });
  }, [setSearchParams]);

  // ── Navigate to create page ───────────────────────────────────────────
  const handleCreateDashboard = useCallback(() => {
    navigate('/reports/create');
  }, [navigate]);

  // ── Toggle filter panel ───────────────────────────────────────────────
  const toggleFilterPanel = useCallback(() => {
    setFilterPanelOpen((prev) => !prev);
  }, []);

  // ── Submit filter form on Enter key ───────────────────────────────────
  const handleFilterKeyDown = useCallback(
    (event: React.KeyboardEvent<HTMLInputElement>) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        // Filters are applied on change, Enter closes panel
        setFilterPanelOpen(false);
      }
    },
    [],
  );

  // ── 7-column definitions matching the monolith ────────────────────────
  const columns = useMemo(
    (): DataTableColumn<DashboardRecord>[] => [
      // Column 1: Action — 1% width, non-sortable, eye icon link
      {
        id: 'action',
        label: '',
        width: '1%',
        noWrap: true,
        cell: (_value: unknown, record: DashboardRecord) => (
          <Link
            to={`/reports/view/${record.id}`}
            className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-sm text-gray-600 no-underline hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
            title="Dashboard details"
            aria-label={`View details for ${record.name}`}
          >
            <svg
              className="h-4 w-4"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
              />
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z"
              />
            </svg>
          </Link>
        ),
      },

      // Column 2: Icon — 1% width, type badge
      {
        id: 'icon',
        label: '',
        width: '1%',
        noWrap: true,
        accessorKey: 'type',
        cell: (_value: unknown, record: DashboardRecord) => {
          const isDatabase = record.type === DataSourceType.Database;
          return (
            <span
              className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${
                isDatabase
                  ? 'bg-blue-100 text-blue-800'
                  : 'bg-purple-100 text-purple-800'
              }`}
              aria-label={isDatabase ? 'Database type' : 'Code type'}
            >
              {isDatabase ? 'DB' : 'Code'}
            </span>
          );
        },
      },

      // Column 3: Name — 220px, sortable
      {
        id: 'name',
        name: 'name',
        label: 'Name',
        width: '220px',
        sortable: true,
        accessorKey: 'name',
        cell: (value: unknown) => (
          <span className="font-medium text-gray-900">
            {String(value ?? '')}
          </span>
        ),
      },

      // Column 4: Type — 120px, sortable
      {
        id: 'type',
        name: 'type',
        label: 'Type',
        width: '120px',
        sortable: true,
        accessorKey: 'type',
        cell: (_value: unknown, record: DashboardRecord) =>
          getTypeLabel(record.type),
      },

      // Column 5: Target — sortable, shows "Entity: X" or "Class: X"
      {
        id: 'target',
        name: 'target',
        label: 'Target',
        sortable: true,
        accessorKey: 'entityName',
        cell: (_value: unknown, record: DashboardRecord) => {
          if (record.type === DataSourceType.Database) {
            return (
              <div>
                <span className="text-gray-400">Entity: </span>
                <span className="text-gray-900">
                  {record.entityName || '—'}
                </span>
              </div>
            );
          }
          // Code data sources display the class name (entityName field
          // carries the fully qualified class name for Code sources in
          // the API response, matching the monolith's ds.GetType().FullName)
          return (
            <div>
              <span className="text-gray-400">Class: </span>
              <span className="text-gray-900">
                {record.entityName || '—'}
              </span>
            </div>
          );
        },
      },

      // Column 6: Returned Model — 220px, not sortable, code-styled
      {
        id: 'model',
        label: 'Returned Model',
        width: '220px',
        accessorKey: 'resultModel',
        cell: (value: unknown) => (
          <code className="rounded bg-gray-100 px-1.5 py-0.5 text-xs text-gray-800">
            {String(value ?? '')}
          </code>
        ),
      },

      // Column 7: Params — 40px, not sortable, count display
      {
        id: 'param_count',
        label: 'Params',
        width: '40px',
        horizontalAlign: 'center' as const,
        accessorFn: (record: DashboardRecord) =>
          record.parameters?.length ?? 0,
        cell: (value: unknown) => (
          <span className="text-gray-600">{String(value ?? '0')}</span>
        ),
      },
    ],
    [],
  );

  // ── Page description ──────────────────────────────────────────────────
  const pageDescription = useMemo(
    () => generatePageDescription(totalCount, activeFilterCount),
    [totalCount, activeFilterCount],
  );

  // ── Error message extraction ──────────────────────────────────────────
  const errorMessage = useMemo(() => {
    if (!isError || !error) return '';
    if (typeof error === 'object' && 'message' in error) {
      return String((error as { message: string }).message);
    }
    return 'An unexpected error occurred while loading dashboards.';
  }, [isError, error]);

  // ══════════════════════════════════════════════════════════════════════
  // RENDER
  // ══════════════════════════════════════════════════════════════════════
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* ─── Page Header ─────────────────────────────────────────── */}
      <header className="border-b border-gray-200 bg-white px-6 py-4">
        <div className="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h1 className="text-xl font-semibold text-gray-900">
              All Dashboards
            </h1>
            <p className="mt-0.5 text-sm text-gray-500">{pageDescription}</p>
          </div>

          {/* Header actions — mirrors monolith's HeaderActions */}
          <div className="mt-2 flex items-center gap-2 sm:mt-0">
            <button
              type="button"
              onClick={handleCreateDashboard}
              className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
            >
              <svg
                className="h-4 w-4 text-green-600"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M12 4v16m8-8H4"
                />
              </svg>
              Create Dashboard
            </button>

            <button
              type="button"
              onClick={toggleFilterPanel}
              className={`inline-flex items-center gap-1.5 rounded border px-3 py-1.5 text-sm font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150 ${
                filterPanelOpen || activeFilterCount > 0
                  ? 'border-blue-300 bg-blue-50 text-blue-700 hover:bg-blue-100'
                  : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
              }`}
              aria-expanded={filterPanelOpen}
              aria-controls="dashboard-filter-panel"
            >
              <svg
                className="h-4 w-4"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                />
              </svg>
              Search
              {activeFilterCount > 0 && (
                <span className="inline-flex h-5 w-5 items-center justify-center rounded-full bg-blue-600 text-xs font-bold text-white">
                  {activeFilterCount}
                </span>
              )}
            </button>
          </div>
        </div>
      </header>

      {/* ─── Filter Panel (collapsible) ──────────────────────────── */}
      {filterPanelOpen && (
        <aside
          id="dashboard-filter-panel"
          className="border-b border-gray-200 bg-gray-50 px-6 py-4"
          role="search"
          aria-label="Filter dashboards"
        >
          <div className="flex flex-col gap-4 sm:flex-row sm:flex-wrap sm:items-end">
            {/* Name filter — CONTAINS */}
            <div className="flex min-w-[12rem] flex-col gap-1">
              <label
                htmlFor="filter-name"
                className="text-xs font-medium uppercase tracking-wide text-gray-600"
              >
                Name
              </label>
              <input
                id="filter-name"
                type="text"
                value={nameFilter}
                onChange={(e) => handleFilterChange('name', e.target.value)}
                onKeyDown={handleFilterKeyDown}
                placeholder="Search by name…"
                className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            {/* Target filter — CONTAINS */}
            <div className="flex min-w-[12rem] flex-col gap-1">
              <label
                htmlFor="filter-target"
                className="text-xs font-medium uppercase tracking-wide text-gray-600"
              >
                Target
              </label>
              <input
                id="filter-target"
                type="text"
                value={targetFilter}
                onChange={(e) => handleFilterChange('target', e.target.value)}
                onKeyDown={handleFilterKeyDown}
                placeholder="Search by target…"
                className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            {/* Model filter — CONTAINS */}
            <div className="flex min-w-[12rem] flex-col gap-1">
              <label
                htmlFor="filter-model"
                className="text-xs font-medium uppercase tracking-wide text-gray-600"
              >
                Returned Model
              </label>
              <input
                id="filter-model"
                type="text"
                value={modelFilter}
                onChange={(e) => handleFilterChange('model', e.target.value)}
                onKeyDown={handleFilterKeyDown}
                placeholder="Search by model…"
                className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>

            {/* Type filter — EQ (select) */}
            <div className="flex min-w-[10rem] flex-col gap-1">
              <label
                htmlFor="filter-type"
                className="text-xs font-medium uppercase tracking-wide text-gray-600"
              >
                Type
              </label>
              <select
                id="filter-type"
                value={typeFilter}
                onChange={(e) => handleFilterChange('type', e.target.value)}
                className="rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              >
                {TYPE_FILTER_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>

            {/* Clear all filters */}
            {activeFilterCount > 0 && (
              <button
                type="button"
                onClick={handleClearAllFilters}
                className="self-end text-sm text-blue-600 underline hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Clear all
              </button>
            )}
          </div>
        </aside>
      )}

      {/* ─── Error State ─────────────────────────────────────────── */}
      {isError && (
        <div
          className="mx-6 mt-4 rounded border border-red-200 bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          <strong className="font-semibold">Error: </strong>
          {errorMessage}
        </div>
      )}

      {/* ─── Data Table ──────────────────────────────────────────── */}
      <section className="flex-1 overflow-auto px-6 py-4" aria-label="Dashboard list">
        <DataTable<DashboardRecord>
          data={dashboards}
          columns={columns}
          totalCount={totalCount}
          pageSize={pageSize}
          bordered
          hover
          loading={isLoading}
          emptyText="No dashboards found"
        />
      </section>
    </div>
  );
}
