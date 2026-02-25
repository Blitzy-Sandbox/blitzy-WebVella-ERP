/**
 * AdminEntityList — Entity Admin List Page
 *
 * Route:    /admin/entities
 * Replaces: WebVella.Erp.Plugins.SDK/Pages/entity/list.cshtml[.cs]
 *
 * Renders a paginated, sortable data-table of all entity metadata fetched
 * from the Entity Management service (GET /v1/entities). Provides a search
 * drawer for case-insensitive name-based filtering, a "Create Entity" header
 * action, and per-row navigation to entity detail views.
 *
 * Data flow:
 *  1. useEntities() TanStack Query hook fetches all entities
 *  2. Client-side filter → sort → paginate (matching monolith pattern)
 *  3. DataTable renders the current-page slice (manualPagination + manualSorting)
 *  4. URL search-params drive sort column/direction and page number
 *
 * Monolith parity:
 *  - PagerSize = 15 (list.cshtml.cs line 24)
 *  - Default sort: Name ascending (list.cshtml.cs line 80)
 *  - Search: case-insensitive name CONTAINS (list.cshtml.cs lines 60-65)
 *  - Grid columns: action, icon, name, label, system, fields count
 */

import { useState, useMemo, useCallback } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useEntities } from '../../hooks/useEntities';
import { DataTable, type DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import type { Entity } from '../../types/entity';

/* ════════════════════════════════════════════════════════════════
 * Local types
 * ════════════════════════════════════════════════════════════════ */

/**
 * Intersection type that satisfies DataTable's generic constraint
 * (`T extends Record<string, unknown>`) while retaining full Entity
 * property typing in column cell renderers.
 */
type EntityRow = Entity & Record<string, unknown>;

/* ════════════════════════════════════════════════════════════════
 * Constants
 * ════════════════════════════════════════════════════════════════ */

/** Matches PagerSize = 15 from list.cshtml.cs (line 24). */
const PAGE_SIZE = 15;

/** Page header accent colour — matches monolith's wv-page-header color="#dc3545". */
const HEADER_COLOR = '#dc3545';

/* ════════════════════════════════════════════════════════════════
 * Component
 * ════════════════════════════════════════════════════════════════ */

/**
 * AdminEntityList renders the entity administration list page.
 *
 * Replaces the monolith SDK entity list:
 * - Route `/sdk/objects/entity/l/list` → `/admin/entities`
 * - `EntityManager.ReadEntities()` → `useEntities()` TanStack Query hook
 * - `wv-grid` → DataTable with TanStack Table
 * - `wv-drawer` → Drawer with name search form
 * - `ReturnUrlEncoded` navigation → React Router Link components
 */
export default function AdminEntityList() {
  /* ── URL-based state (sort & page) ──────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();

  const sortBy = searchParams.get('sortBy') ?? '';
  const sortOrder = (searchParams.get('sortOrder') ?? 'asc') as
    | 'asc'
    | 'desc';
  const currentPage = Math.max(
    1,
    parseInt(searchParams.get('page') ?? '1', 10) || 1,
  );

  /* ── Local component state ──────────────────────────────── */
  const [isDrawerVisible, setIsDrawerVisible] = useState(false);
  const [searchTerm, setSearchTerm] = useState('');

  /* ── Data fetching via TanStack Query ───────────────────── */
  const { data: entities, isLoading, isError } = useEntities();

  /* ── Memoised event handlers ────────────────────────────── */

  /** Open the search/filter drawer panel. */
  const handleOpenDrawer = useCallback(() => {
    setIsDrawerVisible(true);
  }, []);

  /** Close the search/filter drawer panel. */
  const handleCloseDrawer = useCallback(() => {
    setIsDrawerVisible(false);
  }, []);

  /**
   * Handle search input changes. Resets pagination to page 1 so the
   * user always sees results from the first page when the filter changes
   * (mirrors monolith re-applying Skip from page 1 on new search).
   */
  const handleSearchChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      setSearchTerm(event.target.value);
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('page', '1');
        return next;
      });
    },
    [setSearchParams],
  );

  /** Clear the active search filter and reset to page 1. */
  const handleClearSearch = useCallback(() => {
    setSearchTerm('');
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.set('page', '1');
      return next;
    });
  }, [setSearchParams]);

  /** DataTable page-change callback — updates the URL page param. */
  const handlePageChange = useCallback(
    (page: number) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('page', String(page));
        return next;
      });
    },
    [setSearchParams],
  );

  /**
   * DataTable sort-change callback. The DataTable component already
   * persists sortBy/sortOrder to URL params internally; this callback
   * additionally resets the page to 1 on sort change (matching the
   * monolith's behaviour of re-querying from the first page).
   */
  const handleSortChange = useCallback(
    (_newSortBy: string, _newSortOrder: 'asc' | 'desc') => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('page', '1');
        return next;
      });
    },
    [setSearchParams],
  );

  /* ── Derived data: filter → sort → paginate ─────────────── */

  /**
   * Filtered entities — case-insensitive name CONTAINS matching.
   * Mirrors list.cshtml.cs lines 60-65 (SearchString / CONTAINS filter
   * applied to entity.Name).
   */
  const filteredEntities = useMemo<EntityRow[]>(() => {
    if (!entities) return [];
    const term = searchTerm.trim().toLowerCase();
    if (!term) return entities as EntityRow[];
    return (entities as EntityRow[]).filter((entity) =>
      entity.name.toLowerCase().includes(term),
    );
  }, [entities, searchTerm]);

  /**
   * Sorted entities — default is Name ascending (monolith line 80:
   * `allEntities = allEntities.OrderBy(x => x.Name)`).
   * Supports sorting by 'name' and 'label' columns in both directions.
   */
  const sortedEntities = useMemo<EntityRow[]>(() => {
    const list = [...filteredEntities];

    return list.sort((a, b) => {
      let comparison = 0;

      switch (sortBy) {
        case 'name':
          comparison = a.name.localeCompare(b.name);
          break;
        case 'label':
          comparison = a.label.localeCompare(b.label);
          break;
        default:
          /* Default sort by name ascending — matches monolith
             list.cshtml.cs line 80 */
          comparison = a.name.localeCompare(b.name);
          break;
      }

      return sortOrder === 'desc' ? -comparison : comparison;
    });
  }, [filteredEntities, sortBy, sortOrder]);

  /**
   * Paginated slice — Skip/Take pattern with PAGE_SIZE = 15.
   * Mirrors list.cshtml.cs lines 95-96 (skip / take calculation).
   * DataTable uses manualPagination=true, so only the current-page
   * slice is rendered.
   */
  const paginatedEntities = useMemo<EntityRow[]>(() => {
    const startIndex = (currentPage - 1) * PAGE_SIZE;
    return sortedEntities.slice(startIndex, startIndex + PAGE_SIZE);
  }, [sortedEntities, currentPage]);

  /* ── Column definitions ─────────────────────────────────── */

  /**
   * DataTable columns matching the monolith's grid:
   * 1. action  (1%)  — eye icon link to entity detail page
   * 2. icon    (1%)  — colour/icon badge for the entity
   * 3. name          — sortable, main identifier
   * 4. label         — sortable, display name
   * 5. system  (80px) — System / User boolean badge
   * 6. fields  (80px) — field count
   */
  const columns = useMemo<DataTableColumn<EntityRow>[]>(
    () => [
      /* Action column — 1% width, link to entity detail page.
         Replaces monolith's ReturnUrlEncoded-based navigation. */
      {
        id: 'action',
        name: 'action',
        label: '',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: EntityRow) => (
          <Link
            to={`/admin/entities/${record.id}`}
            className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-sm text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
            title="View entity details"
          >
            {/* Eye icon — replaces monolith's fa fa-eye */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
              className="h-4 w-4"
            >
              <path d="M10 12a2 2 0 100-4 2 2 0 000 4z" />
              <path
                fillRule="evenodd"
                d="M.458 10C1.732 5.943 5.522 3 10 3s8.268 2.943 9.542 7c-1.274 4.057-5.064 7-9.542 7S1.732 14.057.458 10zM14 10a4 4 0 11-8 0 4 4 0 018 0z"
                clipRule="evenodd"
              />
            </svg>
          </Link>
        ),
      },

      /* Icon column — 1% width, renders entity colour/icon badge.
         Matches monolith's <div class='badge badge-pill'> pattern. */
      {
        id: 'icon',
        name: 'icon',
        label: '',
        width: '1%',
        sortable: false,
        cell: (_value: unknown, record: EntityRow) => (
          <span
            className="inline-flex items-center justify-center rounded-full bg-gray-100 p-1.5 text-base"
            style={{ color: record.color || '#999999' }}
            aria-hidden="true"
          >
            <span className={record.iconName || 'fa fa-database'} />
          </span>
        ),
      },

      /* Name column — sortable, main identifier.
         Matches monolith's Name column with Sortable = true. */
      {
        id: 'name',
        name: 'name',
        label: 'Name',
        sortable: true,
        accessorKey: 'name' as keyof EntityRow,
      },

      /* Label column — sortable, human-friendly display name. */
      {
        id: 'label',
        name: 'label',
        label: 'Label',
        sortable: true,
        accessorKey: 'label' as keyof EntityRow,
      },

      /* System column — boolean badge (System vs User entity). */
      {
        id: 'system',
        name: 'system',
        label: 'System',
        width: '80px',
        sortable: false,
        cell: (_value: unknown, record: EntityRow) =>
          record.system ? (
            <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
              System
            </span>
          ) : (
            <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
              User
            </span>
          ),
      },

      /* Fields count column — 80px, entity.fields.length.
         Matches monolith's "# Fields" column (Width = "80px"). */
      {
        id: 'fields',
        name: 'fields',
        label: '# Fields',
        width: '80px',
        sortable: false,
        cell: (_value: unknown, record: EntityRow) => (
          <span className="tabular-nums text-gray-700">
            {record.fields?.length ?? 0}
          </span>
        ),
      },
    ],
    [],
  );

  /* ── Loading state ──────────────────────────────────────── */
  if (isLoading) {
    return (
      <div
        className="flex items-center justify-center py-12"
        role="status"
        aria-live="polite"
      >
        <svg
          className="h-8 w-8 animate-spin text-gray-400"
          xmlns="http://www.w3.org/2000/svg"
          fill="none"
          viewBox="0 0 24 24"
          aria-hidden="true"
        >
          <circle
            className="opacity-25"
            cx="12"
            cy="12"
            r="10"
            stroke="currentColor"
            strokeWidth="4"
          />
          <path
            className="opacity-75"
            fill="currentColor"
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
          />
        </svg>
        <span className="sr-only">Loading entities…</span>
      </div>
    );
  }

  /* ── Error state ────────────────────────────────────────── */
  if (isError) {
    return (
      <div className="rounded-md bg-red-50 p-4" role="alert">
        <div className="flex">
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
            className="h-5 w-5 flex-shrink-0 text-red-400"
          >
            <path
              fillRule="evenodd"
              d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z"
              clipRule="evenodd"
            />
          </svg>
          <div className="ms-3">
            <h3 className="text-sm font-medium text-red-800">
              Failed to load entities
            </h3>
            <p className="mt-1 text-sm text-red-700">
              An error occurred while fetching the entity list. Please try
              again later.
            </p>
          </div>
        </div>
      </div>
    );
  }

  /* ── Main render ────────────────────────────────────────── */
  return (
    <>
      {/* ── Page header ──────────────────────────────────────
       * Replaces wv-page-header from list.cshtml with matching
       * #dc3545 accent colour, database icon, title and subtitle.
       * ──────────────────────────────────────────────────── */}
      <header className="mb-6">
        <div className="flex items-center gap-3">
          {/* Icon badge — matches monolith header colour #dc3545 */}
          <span
            className="inline-flex h-10 w-10 flex-shrink-0 items-center justify-center rounded-lg text-white"
            style={{ backgroundColor: HEADER_COLOR }}
            aria-hidden="true"
          >
            {/* Database icon — replaces monolith's fa fa-database */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
            >
              <path
                fillRule="evenodd"
                d="M3 12v3c0 1.657 3.134 3 7 3s7-1.343 7-3v-3c0 1.657-3.134 3-7 3s-7-1.343-7-3z"
                clipRule="evenodd"
              />
              <path
                fillRule="evenodd"
                d="M3 7v3c0 1.657 3.134 3 7 3s7-1.343 7-3V7c0 1.657-3.134 3-7 3S3 8.657 3 7z"
                clipRule="evenodd"
              />
              <path
                fillRule="evenodd"
                d="M17 5c0 1.657-3.134 3-7 3S3 6.657 3 5s3.134-3 7-3 7 1.343 7 3z"
                clipRule="evenodd"
              />
            </svg>
          </span>

          {/* Title area */}
          <div className="min-w-0 flex-1">
            <div className="flex items-baseline gap-2">
              <h1 className="truncate text-xl font-bold text-gray-900">
                All entities list
              </h1>
              <span className="whitespace-nowrap text-sm text-gray-500">
                {filteredEntities.length}{' '}
                {filteredEntities.length === 1 ? 'record' : 'records'}
              </span>
            </div>
            <p className="text-sm text-gray-500">Entities</p>
          </div>

          {/* Header actions */}
          <div className="flex flex-shrink-0 items-center gap-2">
            {/* Search/filter button — toggles the search drawer */}
            <button
              type="button"
              onClick={handleOpenDrawer}
              className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
              aria-label="Open entity search filter"
            >
              {/* Search icon */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
                className="h-4 w-4"
              >
                <path
                  fillRule="evenodd"
                  d="M8 4a4 4 0 100 8 4 4 0 000-8zM2 8a6 6 0 1110.89 3.476l4.817 4.817a1 1 0 01-1.414 1.414l-4.816-4.816A6 6 0 012 8z"
                  clipRule="evenodd"
                />
              </svg>
              Search
            </button>

            {/* Create Entity link — replaces monolith's header action button */}
            <Link
              to="/admin/entities/create"
              className="inline-flex items-center gap-1.5 rounded border border-green-600 bg-green-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-green-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 motion-safe:transition-colors motion-safe:duration-150"
            >
              {/* Plus icon */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
                className="h-4 w-4"
              >
                <path
                  fillRule="evenodd"
                  d="M10 3a1 1 0 011 1v5h5a1 1 0 110 2h-5v5a1 1 0 11-2 0v-5H4a1 1 0 110-2h5V4a1 1 0 011-1z"
                  clipRule="evenodd"
                />
              </svg>
              Create Entity
            </Link>
          </div>
        </div>

        {/* Active search indicator chip — shown when a filter is active */}
        {searchTerm.trim() && (
          <div className="mt-3 flex items-center gap-2">
            <span className="text-sm text-gray-500">Filtered by name:</span>
            <span className="inline-flex items-center gap-1 rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
              {searchTerm}
              <button
                type="button"
                onClick={handleClearSearch}
                className="ms-0.5 inline-flex h-4 w-4 items-center justify-center rounded-full text-blue-400 hover:bg-blue-200 hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600"
                aria-label="Clear search filter"
              >
                {/* Close / X icon */}
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  aria-hidden="true"
                  className="h-3 w-3"
                >
                  <path
                    fillRule="evenodd"
                    d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                    clipRule="evenodd"
                  />
                </svg>
              </button>
            </span>
          </div>
        )}
      </header>

      {/* ── Data table ───────────────────────────────────────
       * DataTable with manualPagination + manualSorting expects
       * pre-sorted, pre-paginated data along with totalCount for
       * pagination UI rendering.
       * ──────────────────────────────────────────────────── */}
      <DataTable<EntityRow>
        data={paginatedEntities}
        columns={columns}
        totalCount={filteredEntities.length}
        pageSize={PAGE_SIZE}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        onSortChange={handleSortChange}
        bordered
        hover
        emptyText="No entities found"
      />

      {/* ── Search drawer ────────────────────────────────────
       * Replaces wv-drawer from list.cshtml with a name search
       * form using case-insensitive CONTAINS matching.
       * ──────────────────────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerVisible}
        onClose={handleCloseDrawer}
        title="Search Entities"
        width="350px"
      >
        <form onSubmit={(e) => e.preventDefault()} noValidate>
          <div>
            <label
              htmlFor="entity-search-name"
              className="block text-sm font-medium text-gray-700"
            >
              Name
            </label>
            <input
              id="entity-search-name"
              type="text"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Search by entity name"
              value={searchTerm}
              onChange={handleSearchChange}
              autoComplete="off"
            />
            <p className="mt-1 text-xs text-gray-500">
              Case-insensitive name filter (contains match)
            </p>
          </div>

          {/* Clear button shown when a search is active */}
          {searchTerm.trim() && (
            <button
              type="button"
              onClick={handleClearSearch}
              className="mt-3 inline-flex items-center gap-1 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
            >
              Clear filter
            </button>
          )}
        </form>
      </Drawer>
    </>
  );
}
