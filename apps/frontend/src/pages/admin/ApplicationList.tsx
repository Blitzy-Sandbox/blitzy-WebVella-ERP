/**
 * ApplicationList — Admin Application List Page
 *
 * Replaces WebVella.Erp.Plugins.SDK/Pages/application/list.cshtml[.cs].
 * Renders the "All Applications" listing at route `/admin/applications`.
 *
 * Source mapping:
 *   list.cshtml.cs ListModel  → ApplicationList React component
 *   list.cshtml wv-grid       → DataTable component
 *   list.cshtml wv-drawer     → Drawer component
 *   AppService.GetAllApplications() → useApps() TanStack Query hook
 *   PageUtils.GetListQueryParams()  → useSearchParams() URL state
 *   PagerSize=15                     → PAGE_SIZE constant
 *
 * @module pages/admin/ApplicationList
 */

import { useState, useMemo, useCallback } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useApps } from '../../hooks/useApps';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import type { App } from '../../types/app';

/**
 * Intersection type that satisfies DataTable's `Record<string, unknown>`
 * generic constraint while preserving App's typed members for column
 * accessor type-safety.
 */
type AppRow = App & Record<string, unknown>;

/**
 * Default page size matching source ListModel.PagerSize = 15.
 * @see list.cshtml.cs line 22: `public int PagerSize { get; set; } = 15;`
 */
const PAGE_SIZE = 15;

/**
 * Admin page component for listing all registered ERP applications.
 *
 * Provides:
 * - Sortable data grid with action, icon, name, label, description columns
 * - Icon badge per application with app-specific color
 * - Detail navigation links per row
 * - Drawer-based name search/filter (CONTAINS, case-insensitive)
 * - URL-persistent page, sort, and filter state
 * - "Create App" action button
 *
 * @returns React element for the application list page
 */
export default function ApplicationList(): React.JSX.Element {
  /* ═══════════════════════════════════════════════════════════
   * URL SEARCH-PARAM STATE
   * ═══════════════════════════════════════════════════════════ */
  const [searchParams, setSearchParams] = useSearchParams();

  /* ═══════════════════════════════════════════════════════════
   * LOCAL UI STATE
   * ═══════════════════════════════════════════════════════════ */

  /** Whether the search drawer panel is visible. */
  const [isDrawerOpen, setIsDrawerOpen] = useState(false);

  /**
   * Local text value for the name filter input inside the drawer.
   * Initialised from the current URL `name` param so the drawer
   * reflects any previously-applied filter on mount.
   */
  const [nameFilterInput, setNameFilterInput] = useState<string>(
    () => searchParams.get('name') ?? '',
  );

  /* ═══════════════════════════════════════════════════════════
   * DATA FETCHING — TanStack Query
   * ═══════════════════════════════════════════════════════════ */
  const { data, isLoading, isError, error } = useApps();

  /** Extract the App[] from the API response envelope. */
  const allApps = useMemo<App[]>(
    () => data?.object ?? [],
    [data],
  );

  /* ═══════════════════════════════════════════════════════════
   * READ URL PARAMETERS
   *
   * DataTable manages its own URL sort/page state internally via
   * the same param keys (sortBy, sortOrder, page). We read them
   * here only to drive the client-side filtering → sorting →
   * pagination pipeline, keeping both in sync via a single source
   * of truth (the URL).
   * ═══════════════════════════════════════════════════════════ */
  const appliedNameFilter = searchParams.get('name') ?? '';
  const sortBy = searchParams.get('sortBy') ?? '';
  const sortOrder = searchParams.get('sortOrder') ?? 'asc';
  const currentPage = parseInt(searchParams.get('page') ?? '1', 10) || 1;

  /* ═══════════════════════════════════════════════════════════
   * CLIENT-SIDE FILTERING
   *
   * Case-insensitive CONTAINS on `name`, matching the source:
   *   list.cshtml.cs line 52-60:
   *     filteredList = filteredList.Where(
   *       x => x.Name.ToLowerInvariant()
   *               .Contains(nameFilter.ToLowerInvariant())
   *     ).ToList();
   * ═══════════════════════════════════════════════════════════ */
  const filteredApps = useMemo<App[]>(() => {
    if (!appliedNameFilter) return allApps;
    const lowerFilter = appliedNameFilter.toLowerCase();
    return allApps.filter((app) =>
      (app.name ?? '').toLowerCase().includes(lowerFilter),
    );
  }, [allApps, appliedNameFilter]);

  /** Total record count after filtering (drives pagination). */
  const totalCount = filteredApps.length;

  /* ═══════════════════════════════════════════════════════════
   * CLIENT-SIDE SORTING
   *
   * Default sort: name ascending.
   * @see list.cshtml.cs line 55: `.OrderBy(x => x.Name)`
   * ═══════════════════════════════════════════════════════════ */
  const sortedApps = useMemo<App[]>(() => {
    const sorted = [...filteredApps];

    const compareField = (a: App, b: App): number => {
      switch (sortBy) {
        case 'name':
          return (a.name ?? '').localeCompare(b.name ?? '');
        case 'label':
          return (a.label ?? '').localeCompare(b.label ?? '');
        default:
          /* Default: sort by name ascending */
          return (a.name ?? '').localeCompare(b.name ?? '');
      }
    };

    sorted.sort((a, b) =>
      sortOrder === 'desc' ? -compareField(a, b) : compareField(a, b),
    );

    return sorted;
  }, [filteredApps, sortBy, sortOrder]);

  /* ═══════════════════════════════════════════════════════════
   * CLIENT-SIDE PAGINATION (Skip / Take)
   *
   * @see list.cshtml.cs line 92:
   *   `filteredList = filteredList.Skip(pageSize * (pager - 1))
   *                               .Take(pageSize).ToList();`
   * ═══════════════════════════════════════════════════════════ */
  const paginatedApps = useMemo<App[]>(() => {
    const skip = (currentPage - 1) * PAGE_SIZE;
    return sortedApps.slice(skip, skip + PAGE_SIZE);
  }, [sortedApps, currentPage]);

  /* ═══════════════════════════════════════════════════════════
   * EVENT HANDLERS
   * ═══════════════════════════════════════════════════════════ */

  /** Opens the search drawer. */
  const handleOpenDrawer = useCallback(() => {
    setIsDrawerOpen(true);
  }, []);

  /** Closes the search drawer. */
  const handleCloseDrawer = useCallback(() => {
    setIsDrawerOpen(false);
  }, []);

  /** Updates the local name filter input value. */
  const handleNameFilterChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      setNameFilterInput(e.target.value);
    },
    [],
  );

  /**
   * Applies the name filter to the URL and resets the page to 1.
   * Closes the drawer after submission to match the monolith's
   * search-then-close behaviour.
   */
  const handleSearchSubmit = useCallback(
    (e: React.FormEvent) => {
      e.preventDefault();
      setSearchParams((prev) => {
        const params = new URLSearchParams(prev);
        const trimmed = nameFilterInput.trim();
        if (trimmed) {
          params.set('name', trimmed);
        } else {
          params.delete('name');
        }
        /* Reset to page 1 so the user does not land on an empty page. */
        params.set('page', '1');
        return params;
      });
      setIsDrawerOpen(false);
    },
    [nameFilterInput, setSearchParams],
  );

  /**
   * Clears all active name filters and resets to page 1.
   * Replaces the source "clear all" `titleAction` link in the drawer.
   */
  const handleClearFilters = useCallback(() => {
    setNameFilterInput('');
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      params.delete('name');
      params.set('page', '1');
      return params;
    });
  }, [setSearchParams]);

  /* ═══════════════════════════════════════════════════════════
   * COLUMN DEFINITIONS
   *
   * Matches the monolith's 5-column grid:
   *   action (1%), icon (1%), name (sortable), label (sortable),
   *   description
   *
   * @see list.cshtml.cs lines 97-137 WvGridColumnMeta setup
   * @see list.cshtml lines 17-38 wv-grid-row / wv-grid-column
   * ═══════════════════════════════════════════════════════════ */
  const columns = useMemo<DataTableColumn<AppRow>[]>(
    () => [
      /* ── Action column ────────────────────────────────── */
      {
        id: 'action',
        label: '',
        name: 'action',
        width: '1%',
        noWrap: true,
        cell: (_value: unknown, record: AppRow) => (
          <Link
            to={`/admin/applications/${record.id}`}
            className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-sm text-gray-600 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
            title="App details"
          >
            {/* Eye icon (replaces fa fa-eye) */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path d="M10 3C5 3 1.73 7.11 1 10c.73 2.89 4 7 9 7s8.27-4.11 9-7c-.73-2.89-4-7-9-7Zm0 12a5 5 0 1 1 0-10 5 5 0 0 1 0 10Zm0-8a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z" />
            </svg>
          </Link>
        ),
      },

      /* ── Icon badge column ────────────────────────────── */
      {
        id: 'icon',
        label: '',
        name: 'icon',
        width: '1%',
        noWrap: true,
        cell: (_value: unknown, record: AppRow) => (
          <span
            className="inline-flex items-center justify-center rounded-full text-lg"
            style={{ color: record.color || '#2196F3' }}
            aria-hidden="true"
          >
            {/*
              Dynamic icon class from data (e.g. "fa fa-cube").
              Requires FontAwesome CSS loaded at app level.
            */}
            <i className={record.iconClass || 'fa fa-cube'} />
          </span>
        ),
      },

      /* ── Name column (sortable + searchable) ──────────── */
      {
        id: 'name',
        label: 'Name',
        name: 'name',
        sortable: true,
        searchable: true,
        accessorKey: 'name',
      },

      /* ── Label column (sortable) ──────────────────────── */
      {
        id: 'label',
        label: 'Label',
        name: 'label',
        sortable: true,
        searchable: true,
        accessorKey: 'label',
      },

      /* ── Description column ───────────────────────────── */
      {
        id: 'description',
        label: 'Description',
        name: 'description',
        accessorKey: 'description',
      },
    ],
    [],
  );

  /* ═══════════════════════════════════════════════════════════
   * RENDER
   * ═══════════════════════════════════════════════════════════ */
  return (
    <div className="flex flex-col gap-6">
      {/* ─── Page Header ──────────────────────────────────── */}
      <header className="flex flex-wrap items-start justify-between gap-4">
        {/* Title section */}
        <div className="flex items-center gap-3">
          {/* Page icon — replaces fa fa-th with color #dc3545 */}
          <span
            className="flex h-10 w-10 items-center justify-center rounded text-xl"
            style={{ color: '#dc3545' }}
            aria-hidden="true"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-6 w-6"
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path
                fillRule="evenodd"
                d="M3 4a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V4Zm5 0a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V4Zm5 0a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1h-3a1 1 0 0 1-1-1V4ZM3 9a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9Zm5 0a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V9Zm5 0a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1h-3a1 1 0 0 1-1-1V9ZM3 14a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-3Zm5 0a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1v-3Zm5 0a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v3a1 1 0 0 1-1 1h-3a1 1 0 0 1-1-1v-3Z"
                clipRule="evenodd"
              />
            </svg>
          </span>
          <div>
            <h1 className="text-xl font-semibold text-gray-900">
              All applications list
            </h1>
            {totalCount > 0 && (
              <p className="mt-0.5 text-sm text-gray-500">
                {`${totalCount} application${totalCount === 1 ? '' : 's'} found`}
              </p>
            )}
          </div>
        </div>

        {/* Header actions — replaces source header action buttons */}
        <div className="flex items-center gap-2">
          {/* Create App button */}
          <Link
            to="/admin/applications/create"
            className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path d="M10 5a1 1 0 0 1 1 1v3h3a1 1 0 1 1 0 2h-3v3a1 1 0 1 1-2 0v-3H6a1 1 0 1 1 0-2h3V6a1 1 0 0 1 1-1Z" />
            </svg>
            Create App
          </Link>

          {/* Search button — opens the drawer */}
          <button
            type="button"
            onClick={handleOpenDrawer}
            className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              className="h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M8 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM2 8a6 6 0 1 1 10.89 3.476l4.817 4.817a1 1 0 0 1-1.414 1.414l-4.816-4.816A6 6 0 0 1 2 8Z"
                clipRule="evenodd"
              />
            </svg>
            Search
          </button>
        </div>
      </header>

      {/* ─── Active filter indicator ──────────────────────── */}
      {appliedNameFilter && (
        <div className="flex items-center gap-2 text-sm text-gray-600">
          <span>Filtered by name:</span>
          <span className="inline-flex items-center gap-1 rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
            {appliedNameFilter}
            <button
              type="button"
              onClick={handleClearFilters}
              className="ml-0.5 inline-flex h-4 w-4 items-center justify-center rounded-full hover:bg-blue-200 focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600"
              aria-label={`Remove name filter: ${appliedNameFilter}`}
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-3 w-3"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path d="M6.28 5.22a.75.75 0 0 0-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 1 0 1.06 1.06L10 11.06l3.72 3.72a.75.75 0 1 0 1.06-1.06L11.06 10l3.72-3.72a.75.75 0 0 0-1.06-1.06L10 8.94 6.28 5.22Z" />
              </svg>
            </button>
          </span>
        </div>
      )}

      {/* ─── Error state ──────────────────────────────────── */}
      {isError && (
        <div
          role="alert"
          className="rounded border border-red-200 bg-red-50 p-4 text-sm text-red-700"
        >
          <p className="font-medium">Failed to load applications</p>
          <p className="mt-1">
            {error?.message ?? 'An unexpected error occurred.'}
          </p>
        </div>
      )}

      {/* ─── Data Table ───────────────────────────────────── */}
      <DataTable<AppRow>
        data={paginatedApps as AppRow[]}
        columns={columns}
        totalCount={totalCount}
        pageSize={PAGE_SIZE}
        bordered
        hover
        emptyText="No pages found"
        loading={isLoading}
      />

      {/* ─── Search Drawer ────────────────────────────────── */}
      <Drawer
        isVisible={isDrawerOpen}
        onClose={handleCloseDrawer}
        width="550px"
        title="Search Applications"
        titleAction={
          <button
            type="button"
            onClick={handleClearFilters}
            className="text-sm text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            clear all
          </button>
        }
      >
        <form
          onSubmit={handleSearchSubmit}
          name="SearchApplications"
          className="flex flex-col gap-4"
        >
          {/* Name filter — replaces wv-filter-text name="name" query-type="CONTAINS" */}
          <div className="flex flex-col gap-1.5">
            <label
              htmlFor="app-name-filter"
              className="text-sm font-medium text-gray-700"
            >
              Name
            </label>
            <input
              id="app-name-filter"
              type="text"
              value={nameFilterInput}
              onChange={handleNameFilterChange}
              placeholder="Contains…"
              autoComplete="off"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none"
            />
            <p className="text-xs text-gray-500">
              Case-insensitive search by application name
            </p>
          </div>

          <hr className="border-gray-200" />

          {/* Submit button */}
          <button
            type="submit"
            className="inline-flex w-full items-center justify-center rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
          >
            Search Applications
          </button>
        </form>
      </Drawer>
    </div>
  );
}
