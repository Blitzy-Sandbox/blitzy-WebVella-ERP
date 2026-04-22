/**
 * EntityList — Entity Listing Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/list.cshtml[.cs]`.
 * Route: `/entities`
 *
 * Displays all entities in a sortable, paginated data table with a search
 * drawer for case-insensitive CONTAINS name filtering.  URL search params
 * drive page, sort, and filter state so views are shareable and support
 * browser back/forward navigation.
 *
 * Monolith parity:
 *  - PagerSize=15, default sort by Name ascending
 *  - wv-page-header color="#dc3545", icon "fa fa-database", title "All entities list"
 *  - wv-grid with action / icon / name / label / system columns
 *  - wv-drawer search form with q_name CONTAINS text filter
 *  - "Create Entity" header action linking to entity creation page
 *
 * @module pages/entities/EntityList
 */

import { useState, useMemo, useCallback, type ChangeEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useEntities } from '../../hooks/useEntities';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import type { Entity } from '../../types/entity';

/**
 * Entity augmented with an index signature to satisfy the
 * `DataTable<T extends Record<string, unknown>>` generic constraint.
 * TypeScript interfaces lack implicit index signatures, so this
 * intersection type is needed.  The cast is structurally safe because
 * all Entity properties are string-keyed.
 */
type EntityRow = Entity & Record<string, unknown>;

/* ════════════════════════════════════════════════════════════════
 * Constants
 * ════════════════════════════════════════════════════════════════ */

/**
 * Default number of entities displayed per page.
 * Matches monolith's `PagerSize = 15` from list.cshtml.cs.
 */
const PAGE_SIZE = 15;

/** Debounce delay (ms) for the name-filter input. */
const FILTER_DEBOUNCE_MS = 300;

/**
 * Module-level timer handle for filter debounce.
 * Safe because only one EntityList route instance is rendered at a time.
 */
let filterDebounceTimer: ReturnType<typeof setTimeout> | undefined;

/* ════════════════════════════════════════════════════════════════
 * Component
 * ════════════════════════════════════════════════════════════════ */

/**
 * Entity listing page.
 *
 * Fetches all entities via TanStack Query (`useEntities`), then applies
 * client-side name filtering, sorting (Name / Label), and pagination to
 * match the monolith's `EntityManager.ReadEntities()` + Skip/Take pattern.
 */
export default function EntityList() {
  /* ── URL search-param state ──────────────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();

  /* ── Drawer visibility ───────────────────────────────────── */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /* ── Filter state: immediate input value + debounced value ─ */
  const [nameFilter, setNameFilter] = useState(
    () => searchParams.get('q_name') || '',
  );
  const [debouncedFilter, setDebouncedFilter] = useState(
    () => searchParams.get('q_name') || '',
  );

  /* ── Data fetching via TanStack Query ────────────────────── */
  const { data: entities, isLoading, isError, error } = useEntities();

  /* ── Read current sort / page from URL ───────────────────── */
  const currentPage = Number(searchParams.get('page') || '1');
  const sortBy = searchParams.get('sortBy') || 'name';
  const sortOrder = (searchParams.get('sortOrder') || 'asc') as
    | 'asc'
    | 'desc';

  /* ── Client-side filtering + sorting ─────────────────────── */
  const filteredAndSorted = useMemo(() => {
    if (!entities) return [];
    let result = [...entities];

    /*
     * Case-insensitive CONTAINS filter on entity name.
     * Matches monolith's:
     *   allEntities.FindAll(x =>
     *     x.Name.ToLowerInvariant().Contains(filter.Value.ToLowerInvariant()))
     */
    if (debouncedFilter.trim()) {
      const lower = debouncedFilter.trim().toLowerCase();
      result = result.filter((entity) =>
        entity.name.toLowerCase().includes(lower),
      );
    }

    /*
     * Sort by Name (default) or Label, ascending (default) or descending.
     * Matches monolith's: allEntities.OrderBy(x => x.Name)
     */
    result.sort((a, b) => {
      const aVal = (sortBy === 'label' ? a.label : a.name) || '';
      const bVal = (sortBy === 'label' ? b.label : b.name) || '';
      const comparison = aVal.localeCompare(bVal, undefined, {
        sensitivity: 'base',
      });
      return sortOrder === 'desc' ? -comparison : comparison;
    });

    return result;
  }, [entities, debouncedFilter, sortBy, sortOrder]);

  /** Total filtered count — drives pagination display. */
  const totalCount = filteredAndSorted.length;

  /* ── Client-side pagination (Skip / Take) ────────────────── */
  const paginatedEntities = useMemo(() => {
    const start = (currentPage - 1) * PAGE_SIZE;
    return filteredAndSorted.slice(start, start + PAGE_SIZE);
  }, [filteredAndSorted, currentPage]);

  /* ── Sort change handler ─────────────────────────────────── */
  const handleSortChange = useCallback(
    (newSortBy: string, newSortOrder: 'asc' | 'desc') => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('sortBy', newSortBy);
        next.set('sortOrder', newSortOrder);
        next.set('page', '1'); // reset to page 1 on sort change
        return next;
      });
    },
    [setSearchParams],
  );

  /* ── Page change handler ─────────────────────────────────── */
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

  /* ── Drawer toggle handlers ──────────────────────────────── */
  const toggleDrawer = useCallback(() => {
    setIsDrawerOpen((prev) => !prev);
  }, []);

  const handleCloseDrawer = useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  /* ── Filter input change with debounce ───────────────────── */
  const handleFilterChange = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value;
      setNameFilter(value);

      if (filterDebounceTimer !== undefined) {
        clearTimeout(filterDebounceTimer);
      }

      filterDebounceTimer = setTimeout(() => {
        setDebouncedFilter(value);
        setSearchParams((prev) => {
          const next = new URLSearchParams(prev);
          if (value.trim()) {
            next.set('q_name', value.trim());
          } else {
            next.delete('q_name');
          }
          next.set('page', '1');
          return next;
        });
      }, FILTER_DEBOUNCE_MS);
    },
    [setSearchParams],
  );

  /* ── Clear filter handler ────────────────────────────────── */
  const handleClearFilter = useCallback(() => {
    if (filterDebounceTimer !== undefined) {
      clearTimeout(filterDebounceTimer);
    }
    setNameFilter('');
    setDebouncedFilter('');
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.delete('q_name');
      next.set('page', '1');
      return next;
    });
  }, [setSearchParams]);

  /* ════════════════════════════════════════════════════════════
   * Column definitions
   * ════════════════════════════════════════════════════════════ */
  const columns: DataTableColumn<EntityRow>[] = useMemo(
    () => [
      /* ── Action: eye-icon link to entity detail ─────────── */
      {
        id: 'action',
        label: '',
        width: '40px',
        cell: (_value: unknown, record: EntityRow) => (
          <Link
            to={`/entities/${record.id}`}
            className="inline-flex items-center justify-center rounded p-1 text-blue-600 motion-safe:transition-colors motion-safe:duration-150 hover:bg-blue-50 hover:text-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            title={`View ${record.name}`}
          >
            {/* Heroicons outline: eye */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-5 w-5"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M2.036 12.322a1.012 1.012 0 0 1 0-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178Z"
              />
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"
              />
            </svg>
            <span className="sr-only">View {record.name}</span>
          </Link>
        ),
      },

      /* ── Icon badge: coloured circle with entity color ──── */
      {
        id: 'icon',
        label: '',
        width: '48px',
        cell: (_value: unknown, record: EntityRow) => (
          <span
            className="inline-flex h-7 w-7 items-center justify-center rounded text-white"
            style={{ backgroundColor: record.color || '#6b7280' }}
            title={record.iconName || 'entity'}
            aria-hidden="true"
          >
            {/* Compact database icon (matches monolith's fa-database default) */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={2}
              stroke="currentColor"
              className="h-4 w-4"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M20.25 6.375c0 2.278-3.694 4.125-8.25 4.125S3.75 8.653 3.75 6.375m16.5 0c0-2.278-3.694-4.125-8.25-4.125S3.75 4.097 3.75 6.375m16.5 0v11.25c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125V6.375m16.5 0v3.75m-16.5-3.75v3.75m16.5 0v3.75C20.25 16.153 16.556 18 12 18s-8.25-1.847-8.25-4.125v-3.75m16.5 0c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125"
              />
            </svg>
          </span>
        ),
      },

      /* ── Name: sortable text accessor ───────────────────── */
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        sortable: true,
      },

      /* ── Label: sortable text accessor ──────────────────── */
      {
        id: 'label',
        label: 'Label',
        accessorKey: 'label',
        sortable: true,
      },

      /* ── System: boolean badge indicator ─────────────────── */
      {
        id: 'system',
        label: 'System',
        width: '100px',
        cell: (_value: unknown, record: EntityRow) =>
          record.system ? (
            <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
              Yes
            </span>
          ) : (
            <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
              No
            </span>
          ),
      },
    ],
    [],
  );

  /* ════════════════════════════════════════════════════════════
   * ERROR STATE
   * ════════════════════════════════════════════════════════════ */
  if (isError) {
    return (
      <div className="p-6">
        <div
          className="rounded-lg border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <div className="flex items-start gap-3">
            {/* Heroicons outline: exclamation-triangle */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="mt-0.5 h-5 w-5 shrink-0 text-red-500"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z"
              />
            </svg>
            <div>
              <h3 className="text-sm font-semibold text-red-800">
                Error loading entities
              </h3>
              <p className="mt-1 text-sm text-red-700">
                {error?.message ||
                  'An unexpected error occurred while loading entities.'}
              </p>
            </div>
          </div>
        </div>
      </div>
    );
  }

  /* ════════════════════════════════════════════════════════════
   * MAIN RENDER
   * ════════════════════════════════════════════════════════════ */
  return (
    <div className="flex flex-col gap-6 p-6">
      {/* ── Page header ────────────────────────────────────── */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          {/* Red accent icon — matches monolith wv-page-header color="#dc3545" */}
          <div
            className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-white"
            style={{ backgroundColor: '#dc3545' }}
            aria-hidden="true"
          >
            {/* Heroicons outline: circle-stack (database) */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-5 w-5"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M20.25 6.375c0 2.278-3.694 4.125-8.25 4.125S3.75 8.653 3.75 6.375m16.5 0c0-2.278-3.694-4.125-8.25-4.125S3.75 4.097 3.75 6.375m16.5 0v11.25c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125V6.375m16.5 0v3.75m-16.5-3.75v3.75m16.5 0v3.75C20.25 16.153 16.556 18 12 18s-8.25-1.847-8.25-4.125v-3.75m16.5 0c0 2.278-3.694 4.125-8.25 4.125s-8.25-1.847-8.25-4.125"
              />
            </svg>
          </div>
          <div>
            <h1 className="text-xl font-semibold text-gray-900">Entities</h1>
            <p className="text-sm text-gray-500">All entities list</p>
          </div>
        </div>

        {/* Header actions */}
        <div className="flex items-center gap-2">
          {/* Search drawer toggle */}
          <button
            type="button"
            onClick={toggleDrawer}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm motion-safe:transition-colors motion-safe:duration-150 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            aria-label="Toggle search drawer"
          >
            {/* Heroicons outline: magnifying-glass */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="m21 21-5.197-5.197m0 0A7.5 7.5 0 1 0 5.196 5.196a7.5 7.5 0 0 0 10.607 10.607Z"
              />
            </svg>
            Search
          </button>

          {/* Create Entity link button */}
          <Link
            to="/entities/create"
            className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white shadow-sm motion-safe:transition-colors motion-safe:duration-150 hover:bg-green-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-green-500"
          >
            {/* Heroicons outline: plus */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M12 4.5v15m7.5-7.5h-15"
              />
            </svg>
            Create Entity
          </Link>
        </div>
      </div>

      {/* ── Active filter indicator ────────────────────────── */}
      {debouncedFilter && (
        <div className="flex items-center gap-2 text-sm text-gray-600">
          <span>Filtered by name:</span>
          <span className="inline-flex items-center gap-1 rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
            {debouncedFilter}
            <button
              type="button"
              onClick={handleClearFilter}
              className="ms-0.5 inline-flex h-4 w-4 items-center justify-center rounded-full text-blue-600 hover:bg-blue-200 hover:text-blue-900 focus-visible:outline-none"
              aria-label="Clear name filter"
            >
              {/* Heroicons mini: x-mark */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={2}
                stroke="currentColor"
                className="h-3 w-3"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M6 18 18 6M6 6l12 12"
                />
              </svg>
            </button>
          </span>
          <span className="text-gray-400">
            ({totalCount} {totalCount === 1 ? 'result' : 'results'})
          </span>
        </div>
      )}

      {/* ── Data table ─────────────────────────────────────── */}
      <DataTable<EntityRow>
        data={paginatedEntities as EntityRow[]}
        columns={columns}
        totalCount={totalCount}
        pageSize={PAGE_SIZE}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        onSortChange={handleSortChange}
        bordered
        hover
        emptyText="No entities found"
        loading={isLoading}
      />

      {/* ── Search drawer ──────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        onClose={handleCloseDrawer}
        title="Search Entities"
        titleAction={
          debouncedFilter ? (
            <button
              type="button"
              onClick={handleClearFilter}
              className="rounded text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            >
              Clear all
            </button>
          ) : undefined
        }
      >
        <div className="space-y-4">
          <div>
            <label
              htmlFor="entity-name-filter"
              className="block text-sm font-medium text-gray-700"
            >
              Name
            </label>
            <div className="relative mt-1">
              <input
                type="text"
                id="entity-name-filter"
                value={nameFilter}
                onChange={handleFilterChange}
                placeholder="Filter by name…"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 pe-9 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                autoComplete="off"
              />
              {nameFilter && (
                <button
                  type="button"
                  onClick={handleClearFilter}
                  className="absolute inset-y-0 end-0 flex items-center pe-3 text-gray-400 hover:text-gray-600"
                  aria-label="Clear filter"
                >
                  <svg
                    xmlns="http://www.w3.org/2000/svg"
                    fill="none"
                    viewBox="0 0 24 24"
                    strokeWidth={2}
                    stroke="currentColor"
                    className="h-4 w-4"
                    aria-hidden="true"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M6 18 18 6M6 6l12 12"
                    />
                  </svg>
                </button>
              )}
            </div>
            <p className="mt-1 text-xs text-gray-500">
              Case-insensitive search on entity name
            </p>
          </div>
        </div>
      </Drawer>
    </div>
  );
}
