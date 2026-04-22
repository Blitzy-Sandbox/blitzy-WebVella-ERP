import { useState, useMemo, useCallback } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useDataSources } from '../../hooks/useReports';
import { DataTable, type DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import type { DataSourceBase, DatabaseDataSource } from '../../types/datasource';
import { DataSourceType } from '../../types/datasource';

/**
 * Determines whether a DataSourceBase object is a DatabaseDataSource.
 * DatabaseDataSource extends DataSourceBase with eqlText and sqlText properties,
 * distinguishing DATABASE from CODE data sources for target column display logic.
 */
function isDatabaseDataSource(ds: DataSourceBase): ds is DatabaseDataSource {
  return ds.type === DataSourceType.Database;
}

/**
 * Returns a human-readable label for a DataSourceType enum value.
 * Matches the monolith's TypeOptions: DATABASE (0) and CODE (1).
 */
function getTypeLabel(type: DataSourceType): string {
  switch (type) {
    case DataSourceType.Database:
      return 'DATABASE';
    case DataSourceType.Code:
      return 'CODE';
    default:
      return 'UNKNOWN';
  }
}

/**
 * Computes the target column value for a data source.
 * - DATABASE sources display "Entity: {entityName}" (matching list.cshtml.cs line 72).
 * - CODE sources display "Class: {name}" (matching list.cshtml.cs line 84).
 */
function getTargetDisplay(ds: DataSourceBase): string {
  if (isDatabaseDataSource(ds)) {
    return ds.entityName ? `Entity: ${ds.entityName}` : '';
  }
  return ds.name ? `Class: ${ds.name}` : '';
}

/**
 * Returns the parameter count for a data source.
 * - DATABASE sources count their parameters array length.
 * - CODE sources always show 0 (matching list.cshtml.cs line 88).
 */
function getParamCount(ds: DataSourceBase): number {
  if (isDatabaseDataSource(ds)) {
    return ds.parameters?.length ?? 0;
  }
  return 0;
}

/** Default page size matching the monolith's PagerSize = 15 */
const DEFAULT_PAGE_SIZE = 15;

/**
 * DataSourceList — Admin page listing all data sources.
 *
 * Replaces the monolith's `WebVella.Erp.Plugins.SDK/Pages/data_source/list.cshtml[.cs]`.
 * Route: `/admin/data-sources`
 *
 * Features:
 * - Fetches all data sources via TanStack Query (`useDataSources`)
 * - Client-side CONTAINS filtering on name, target, model fields
 * - Client-side EQ filtering on type field (DATABASE / CODE)
 * - Client-side sorting on name, type, target columns
 * - URL-driven pagination (pageSize=15)
 * - Search drawer with 4 filter inputs
 * - "Create Data Source" navigation link
 * - 7-column DataTable matching the monolith grid
 */
function DataSourceList() {
  const [searchParams, setSearchParams] = useSearchParams();

  /* ---------- Drawer visibility state ---------- */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /* ---------- Filter values from URL search params ---------- */
  const filterName = searchParams.get('name') ?? '';
  const filterTarget = searchParams.get('target') ?? '';
  const filterModel = searchParams.get('model') ?? '';
  const filterType = searchParams.get('type') ?? '';

  /* ---------- Sort state from URL search params ---------- */
  const sortBy = searchParams.get('sortBy') ?? '';
  const sortOrder = searchParams.get('sortOrder') ?? 'asc';

  /* ---------- Pagination from URL ---------- */
  const currentPage = parseInt(searchParams.get('page') ?? '1', 10) || 1;

  /* ---------- Data fetching ---------- */
  const { data: dataSources, isLoading, isError, error } = useDataSources();

  /* ---------- Local filter input state (controlled inputs inside the drawer) ---------- */
  const [localName, setLocalName] = useState(filterName);
  const [localTarget, setLocalTarget] = useState(filterTarget);
  const [localModel, setLocalModel] = useState(filterModel);
  const [localType, setLocalType] = useState(filterType);

  /* ---------- Drawer handlers ---------- */
  const handleToggleDrawer = useCallback(() => {
    setIsDrawerOpen((prev) => !prev);
  }, []);

  const handleCloseDrawer = useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  /**
   * Apply filters — writes filter values into URL search params so that
   * DataTable pagination and filters are URL-driven (shareable, bookmarkable).
   */
  const handleApplyFilters = useCallback(() => {
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      /* Reset page to 1 when filters change */
      next.set('page', '1');

      if (localName) next.set('name', localName);
      else next.delete('name');

      if (localTarget) next.set('target', localTarget);
      else next.delete('target');

      if (localModel) next.set('model', localModel);
      else next.delete('model');

      if (localType) next.set('type', localType);
      else next.delete('type');

      return next;
    });
    setIsDrawerOpen(false);
  }, [localName, localTarget, localModel, localType, setSearchParams]);

  /**
   * Clear all filters — removes filter query params and resets local state.
   */
  const handleClearFilters = useCallback(() => {
    setLocalName('');
    setLocalTarget('');
    setLocalModel('');
    setLocalType('');
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.delete('name');
      next.delete('target');
      next.delete('model');
      next.delete('type');
      next.set('page', '1');
      return next;
    });
  }, [setSearchParams]);

  /**
   * Filtered, sorted, and paginated data — all computed client-side
   * matching the monolith's LINQ operations in list.cshtml.cs lines 99-153.
   */
  const { paginatedData, totalCount } = useMemo(() => {
    const allData = dataSources ?? [];

    /* ---- Filtering (CONTAINS for text, EQ for type) ---- */
    let filtered = allData;

    if (filterName) {
      const lower = filterName.toLowerCase();
      filtered = filtered.filter((ds) =>
        (ds.name ?? '').toLowerCase().includes(lower),
      );
    }

    if (filterTarget) {
      const lower = filterTarget.toLowerCase();
      filtered = filtered.filter((ds) =>
        getTargetDisplay(ds).toLowerCase().includes(lower),
      );
    }

    if (filterModel) {
      const lower = filterModel.toLowerCase();
      filtered = filtered.filter((ds) =>
        (ds.resultModel ?? '').toLowerCase().includes(lower),
      );
    }

    if (filterType) {
      const typeVal = parseInt(filterType, 10);
      if (!Number.isNaN(typeVal)) {
        filtered = filtered.filter((ds) => ds.type === typeVal);
      }
    }

    /* ---- Sorting ---- */
    if (sortBy) {
      const direction = sortOrder === 'desc' ? -1 : 1;
      filtered = [...filtered].sort((a, b) => {
        let aVal = '';
        let bVal = '';

        switch (sortBy) {
          case 'name':
            aVal = (a.name ?? '').toLowerCase();
            bVal = (b.name ?? '').toLowerCase();
            break;
          case 'type':
            aVal = getTypeLabel(a.type).toLowerCase();
            bVal = getTypeLabel(b.type).toLowerCase();
            break;
          case 'target':
            aVal = getTargetDisplay(a).toLowerCase();
            bVal = getTargetDisplay(b).toLowerCase();
            break;
          default:
            return 0;
        }

        if (aVal < bVal) return -1 * direction;
        if (aVal > bVal) return 1 * direction;
        return 0;
      });
    }

    const total = filtered.length;

    /* ---- Pagination (Skip / Take) ---- */
    const skip = (currentPage - 1) * DEFAULT_PAGE_SIZE;
    const paginated = filtered.slice(skip, skip + DEFAULT_PAGE_SIZE);

    return { paginatedData: paginated, totalCount: total };
  }, [dataSources, filterName, filterTarget, filterModel, filterType, sortBy, sortOrder, currentPage]);

  /**
   * Cast helper — extracts a DataSourceBase from the generic record type
   * used by DataTable's `Record<string, unknown>` constraint.
   */
  const asDs = useCallback(
    (record: Record<string, unknown>): DataSourceBase =>
      record as unknown as DataSourceBase,
    [],
  );

  /**
   * Column definitions matching the monolith's 7 grid columns from
   * list.cshtml.cs lines 158-196.
   *
   * DataTable requires `T extends Record<string, unknown>`, so we use the
   * default Record type and cast within each cell renderer.
   */
  const columns: DataTableColumn<Record<string, unknown>>[] = useMemo(
    () => [
      /* 1. Action column — eye icon linking to details */
      {
        id: 'action',
        label: '',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: Record<string, unknown>) => {
          const row = asDs(record);
          return (
            <Link
              to={`/admin/data-sources/${row.id}`}
              className="inline-flex items-center justify-center text-slate-500 hover:text-blue-600"
              title="View details"
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
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
          );
        },
      },
      /* 2. Icon badge column — type icon */
      {
        id: 'icon',
        label: '',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: Record<string, unknown>) => {
          const row = asDs(record);
          return (
            <span
              className={`inline-flex items-center justify-center rounded px-1.5 py-0.5 text-xs font-semibold leading-none ${
                row.type === DataSourceType.Database
                  ? 'bg-sky-100 text-sky-700'
                  : 'bg-amber-100 text-amber-700'
              }`}
            >
              {row.type === DataSourceType.Database ? 'DB' : 'C#'}
            </span>
          );
        },
      },
      /* 3. Name column — sortable, 220px width */
      {
        id: 'name',
        label: 'Name',
        width: '220px',
        sortable: true,
        accessorKey: 'name',
        cell: (_value: unknown, record: Record<string, unknown>) => {
          const row = asDs(record);
          return (
            <Link
              to={`/admin/data-sources/${row.id}`}
              className="text-blue-600 hover:text-blue-800 hover:underline"
            >
              {row.name ?? ''}
            </Link>
          );
        },
      },
      /* 4. Type column — sortable, 120px width */
      {
        id: 'type',
        label: 'Type',
        width: '120px',
        sortable: true,
        cell: (_value: unknown, record: Record<string, unknown>) => (
          <span>{getTypeLabel(asDs(record).type)}</span>
        ),
      },
      /* 5. Target column — sortable, auto width */
      {
        id: 'target',
        label: 'Target',
        sortable: true,
        cell: (_value: unknown, record: Record<string, unknown>) => (
          <span className="text-slate-700">{getTargetDisplay(asDs(record))}</span>
        ),
      },
      /* 6. Model column — 220px, code-formatted */
      {
        id: 'model',
        label: 'Model',
        width: '220px',
        sortable: false,
        cell: (_value: unknown, record: Record<string, unknown>) => (
          <code className="text-xs text-slate-600 break-all">
            {asDs(record).resultModel ?? ''}
          </code>
        ),
      },
      /* 7. Param count column — 40px */
      {
        id: 'param_count',
        label: '#P',
        width: '40px',
        sortable: false,
        horizontalAlign: 'center' as const,
        cell: (_value: unknown, record: Record<string, unknown>) => (
          <span>{getParamCount(asDs(record))}</span>
        ),
      },
    ],
    [asDs],
  );

  /* ============================================================
   * RENDER
   * ============================================================ */

  return (
    <div className="flex flex-col gap-4">
      {/* ---- Page Header ---- */}
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold text-slate-800">
          All data sources
        </h1>

        <div className="flex items-center gap-2">
          {/* Create Data Source action */}
          <Link
            to="/admin/data-sources/create"
            className="inline-flex items-center gap-1.5 rounded border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M12 4v16m8-8H4"
              />
            </svg>
            Add Data Source
          </Link>

          {/* Search button */}
          <button
            type="button"
            onClick={handleToggleDrawer}
            className="inline-flex items-center gap-1.5 rounded border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M21 21l-4.35-4.35M11 19a8 8 0 100-16 8 8 0 000 16z"
              />
            </svg>
            Search
          </button>
        </div>
      </div>

      {/* ---- Active filter indicators ---- */}
      {(filterName || filterTarget || filterModel || filterType) && (
        <div className="flex flex-wrap items-center gap-2 text-sm text-slate-600">
          <span className="font-medium">Active filters:</span>
          {filterName && (
            <span className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
              Name: {filterName}
            </span>
          )}
          {filterTarget && (
            <span className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
              Target: {filterTarget}
            </span>
          )}
          {filterModel && (
            <span className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
              Model: {filterModel}
            </span>
          )}
          {filterType && (
            <span className="inline-flex items-center rounded-full bg-blue-50 px-2 py-0.5 text-xs font-medium text-blue-700">
              Type: {filterType === '0' ? 'DATABASE' : 'CODE'}
            </span>
          )}
          <button
            type="button"
            onClick={handleClearFilters}
            className="text-xs text-red-500 hover:text-red-700 underline"
          >
            Clear all
          </button>
        </div>
      )}

      {/* ---- Error state ---- */}
      {isError && (
        <div
          role="alert"
          className="rounded border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700"
        >
          {error instanceof Error
            ? error.message
            : 'Failed to load data sources. Please try again.'}
        </div>
      )}

      {/* ---- Data Table ---- */}
      <DataTable
        data={paginatedData as unknown as Record<string, unknown>[]}
        columns={columns}
        totalCount={totalCount}
        pageSize={DEFAULT_PAGE_SIZE}
        bordered
        hover
        loading={isLoading}
        emptyText="No data sources found"
        name="datasource-list"
      />

      {/* ---- Search Drawer ---- */}
      <Drawer
        isVisible={isDrawerOpen}
        onClose={handleCloseDrawer}
        title="Search Data Sources"
        width="550px"
        titleAction={
          <button
            type="button"
            onClick={handleClearFilters}
            className="text-sm text-blue-600 hover:text-blue-800 underline"
          >
            clear all
          </button>
        }
      >
        <form
          onSubmit={(e) => {
            e.preventDefault();
            handleApplyFilters();
          }}
          className="flex flex-col gap-4 p-4"
        >
          {/* Name filter (CONTAINS) */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-name"
              className="text-sm font-medium text-slate-700"
            >
              Name
            </label>
            <input
              id="filter-name"
              type="text"
              value={localName}
              onChange={(e) => setLocalName(e.target.value)}
              placeholder="Contains..."
              className="rounded border border-slate-300 px-3 py-2 text-sm text-slate-800 placeholder-slate-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Target filter (CONTAINS) */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-target"
              className="text-sm font-medium text-slate-700"
            >
              Target
            </label>
            <input
              id="filter-target"
              type="text"
              value={localTarget}
              onChange={(e) => setLocalTarget(e.target.value)}
              placeholder="Contains..."
              className="rounded border border-slate-300 px-3 py-2 text-sm text-slate-800 placeholder-slate-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Model filter (CONTAINS) */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-model"
              className="text-sm font-medium text-slate-700"
            >
              Model
            </label>
            <input
              id="filter-model"
              type="text"
              value={localModel}
              onChange={(e) => setLocalModel(e.target.value)}
              placeholder="Contains..."
              className="rounded border border-slate-300 px-3 py-2 text-sm text-slate-800 placeholder-slate-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Type filter (EQ select) — DATABASE / CODE */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-type"
              className="text-sm font-medium text-slate-700"
            >
              Type
            </label>
            <select
              id="filter-type"
              value={localType}
              onChange={(e) => setLocalType(e.target.value)}
              className="rounded border border-slate-300 px-3 py-2 text-sm text-slate-800 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              <option value="">All</option>
              <option value={String(DataSourceType.Database)}>DATABASE</option>
              <option value={String(DataSourceType.Code)}>CODE</option>
            </select>
          </div>

          {/* Submit */}
          <button
            type="submit"
            className="mt-2 rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          >
            Apply Filters
          </button>
        </form>
      </Drawer>
    </div>
  );
}

export default DataSourceList;
