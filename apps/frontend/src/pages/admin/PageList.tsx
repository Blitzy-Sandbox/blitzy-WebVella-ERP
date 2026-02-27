/**
 * PageList — Admin page definition listing.
 *
 * Route: `/admin/pages`
 *
 * Replaces the monolith's `WebVella.Erp.Plugins.SDK/Pages/page/list.cshtml[.cs]`.
 * Lists every ERP page definition with 8-column DataTable (action, label, name,
 * app, entity, type, system, weight), a 7-filter search Drawer, URL-driven
 * pagination (pageSize 15) and sorting, and a "Create Page" action button.
 *
 * Data flow:
 *  - usePages()    → Plugin System API GET /v1/pages  → ErpPage[]
 *  - useApps()     → Plugin System API GET /v1/apps   → App[]  (for ID→name)
 *  - useEntities() → Entity Mgmt  API GET /v1/entities → Entity[] (for ID→name)
 *  - useClonePage()→ POST /v1/pages/:id/clone           (action column)
 *
 * Filtering, sorting, and pagination are performed client-side to replicate
 * the monolith's behaviour where all pages were fetched once, then filtered
 * in the PageModel.
 */

import { useState, useMemo, useCallback } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { usePages, useClonePage } from '../../hooks/usePages';
import { useApps } from '../../hooks/useApps';
import { useEntities } from '../../hooks/useEntities';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import { PageType } from '../../types/page';
import type { ErpPage } from '../../types/page';

/* ═══════════════════════════════════════════════════════════════
   Local row type  (satisfies DataTable<T extends Record<string, unknown>>)
   ═══════════════════════════════════════════════════════════════ */

/**
 * Extends ErpPage with an index signature so that it satisfies the
 * DataTable generic constraint `T extends Record<string, unknown>`.
 * Every other DataTable consumer in the codebase applies the same pattern.
 */
interface PageRecord extends ErpPage {
  /** Index signature required by DataTable generic constraint */
  [key: string]: unknown;
}

/* ═══════════════════════════════════════════════════════════════
   Constants
   ═══════════════════════════════════════════════════════════════ */

/** Matches the monolith's PagerSize = 15 from list.cshtml.cs line 37. */
const PAGE_SIZE = 15;

/** Filter URL-parameter keys (kept in sync with Drawer inputs). */
const FILTER_KEYS = [
  'label',
  'name',
  'app',
  'entity',
  'type',
  'system',
  'customized',
] as const;

/* ═══════════════════════════════════════════════════════════════
   Helper – PageType label resolution
   ═══════════════════════════════════════════════════════════════ */

/** Maps PageType enum values to human-readable labels.
 *  Replicates C# `PageType.GetLabel()` from the monolith. */
const PAGE_TYPE_LABELS: Record<PageType, string> = {
  [PageType.Home]: 'Home',
  [PageType.Site]: 'Site',
  [PageType.Application]: 'Application',
  [PageType.RecordList]: 'Record List',
  [PageType.RecordCreate]: 'Record Create',
  [PageType.RecordDetails]: 'Record Details',
  [PageType.RecordManage]: 'Record Manage',
};

/**
 * Returns the display label for a given PageType value.
 * Falls back to "Unknown" for unexpected values.
 */
function getPageTypeLabel(type: PageType): string {
  return PAGE_TYPE_LABELS[type] ?? 'Unknown';
}

/* ═══════════════════════════════════════════════════════════════
   Filter state shape
   ═══════════════════════════════════════════════════════════════ */

interface PageFilters {
  label: string;
  name: string;
  app: string;
  entity: string;
  type: string;
  /** '' = no filter, 'true', 'false' */
  system: string;
  /** '' = no filter, 'true', 'false' – maps to ErpPage.isRazorBody */
  customized: string;
}

const EMPTY_FILTERS: PageFilters = {
  label: '',
  name: '',
  app: '',
  entity: '',
  type: '',
  system: '',
  customized: '',
};

/* ═══════════════════════════════════════════════════════════════
   PageList Component
   ═══════════════════════════════════════════════════════════════ */

/**
 * Admin page listing – default export for lazy-loading via React Router.
 */
export default function PageList() {
  /* ── URL search-param state ────────────────────────────────── */
  const [searchParams, setSearchParams] = useSearchParams();

  /* ── Drawer open/close state ───────────────────────────────── */
  const [drawerOpen, setDrawerOpen] = useState(false);

  /* ── Local form state for Drawer filter inputs ─────────────── */
  const [filterForm, setFilterForm] = useState<PageFilters>(EMPTY_FILTERS);

  /* ── Data fetching ─────────────────────────────────────────── */
  const {
    data: pagesData,
    isLoading: pagesLoading,
    isError: pagesError,
    isFetching: pagesFetching,
  } = usePages();

  const { data: appsData, isLoading: appsLoading } = useApps();
  const { data: entitiesData, isLoading: entitiesLoading } = useEntities();

  /* ── Clone mutation ────────────────────────────────────────── */
  const { mutate: clonePage, isPending: clonePending } = useClonePage();

  /* ══════════════════════════════════════════════════════════════
     Memoised lookup maps  (ID → display name)
     ══════════════════════════════════════════════════════════════ */

  /** Map<appId, appName> for resolving the App column. */
  const appMap = useMemo<Map<string, string>>(() => {
    const apps = appsData?.object ?? [];
    return new Map(apps.map((a) => [a.id, a.name]));
  }, [appsData]);

  /** Map<entityId, entityName> for resolving the Entity column. */
  const entityMap = useMemo<Map<string, string>>(() => {
    const entities = entitiesData ?? [];
    return new Map(entities.map((e) => [e.id, e.name]));
  }, [entitiesData]);

  /** Full list of apps for the filter form entity-name lookup. */
  const appsList = useMemo(() => appsData?.object ?? [], [appsData]);

  /** Full list of entities for the filter form entity-name lookup. */
  const entitiesList = useMemo(() => entitiesData ?? [], [entitiesData]);

  /* ══════════════════════════════════════════════════════════════
     Active filters (read from URL – source of truth)
     ══════════════════════════════════════════════════════════════ */

  const activeFilters = useMemo<PageFilters>(
    () => ({
      label: searchParams.get('label') ?? '',
      name: searchParams.get('name') ?? '',
      app: searchParams.get('app') ?? '',
      entity: searchParams.get('entity') ?? '',
      type: searchParams.get('type') ?? '',
      system: searchParams.get('system') ?? '',
      customized: searchParams.get('customized') ?? '',
    }),
    [searchParams],
  );

  /** Whether any filter is currently active (for badge / indicator). */
  const hasActiveFilters = useMemo(
    () => FILTER_KEYS.some((k) => activeFilters[k] !== ''),
    [activeFilters],
  );

  /* ══════════════════════════════════════════════════════════════
     All pages (unwrapped from API response)
     ══════════════════════════════════════════════════════════════ */

  const allPages = useMemo<PageRecord[]>(
    () => (pagesData?.object ?? []) as PageRecord[],
    [pagesData],
  );

  /* ══════════════════════════════════════════════════════════════
     Filtered pages
     ══════════════════════════════════════════════════════════════ */

  const filteredPages = useMemo<PageRecord[]>(() => {
    let pages = allPages;

    /* label: CONTAINS case-insensitive (monolith line 152) */
    if (activeFilters.label) {
      const needle = activeFilters.label.toLowerCase();
      pages = pages.filter((p) =>
        (p.label ?? '').toLowerCase().includes(needle),
      );
    }

    /* name: CONTAINS case-insensitive (monolith line 159) */
    if (activeFilters.name) {
      const needle = activeFilters.name.toLowerCase();
      pages = pages.filter((p) =>
        (p.name ?? '').toLowerCase().includes(needle),
      );
    }

    /* app: EQ by app name → resolve matching appIds (monolith line 166-176) */
    if (activeFilters.app) {
      const needle = activeFilters.app.toLowerCase();
      const matchingAppIds = new Set(
        appsList
          .filter((a) => a.name.toLowerCase() === needle)
          .map((a) => a.id),
      );
      pages = pages.filter((p) => p.appId != null && matchingAppIds.has(p.appId));
    }

    /* entity: EQ by entity name (monolith line 178-188) */
    if (activeFilters.entity) {
      const needle = activeFilters.entity.toLowerCase();
      const matchingEntityIds = new Set(
        entitiesList
          .filter((e) => e.name.toLowerCase() === needle)
          .map((e) => e.id),
      );
      pages = pages.filter(
        (p) => p.entityId != null && matchingEntityIds.has(p.entityId),
      );
    }

    /* type: CONTAINS matching PageType label strings (monolith line 190-200) */
    if (activeFilters.type) {
      const needle = activeFilters.type.toLowerCase();
      pages = pages.filter((p) =>
        getPageTypeLabel(p.type).toLowerCase().includes(needle),
      );
    }

    /* system: EQ boolean (monolith line 202-206) */
    if (activeFilters.system === 'true') {
      pages = pages.filter((p) => p.system === true);
    } else if (activeFilters.system === 'false') {
      pages = pages.filter((p) => p.system === false);
    }

    /* customized: EQ boolean → maps to isRazorBody (monolith line 208-212) */
    if (activeFilters.customized === 'true') {
      pages = pages.filter((p) => p.isRazorBody === true);
    } else if (activeFilters.customized === 'false') {
      pages = pages.filter((p) => p.isRazorBody === false);
    }

    return pages;
  }, [allPages, activeFilters, appsList, entitiesList]);

  /* ══════════════════════════════════════════════════════════════
     Sorted pages
     ══════════════════════════════════════════════════════════════ */

  const sortBy = searchParams.get('sortBy') ?? '';
  const sortOrder = (searchParams.get('sortOrder') ?? 'asc') as
    | 'asc'
    | 'desc';

  const sortedPages = useMemo<PageRecord[]>(() => {
    if (!sortBy) return filteredPages;

    const sorted = [...filteredPages];
    sorted.sort((a, b) => {
      let comparison = 0;

      switch (sortBy) {
        case 'label':
          comparison = (a.label ?? '').localeCompare(b.label ?? '');
          break;
        case 'name':
          comparison = (a.name ?? '').localeCompare(b.name ?? '');
          break;
        case 'type':
          comparison = getPageTypeLabel(a.type).localeCompare(
            getPageTypeLabel(b.type),
          );
          break;
        case 'system':
          comparison = (a.system ? 1 : 0) - (b.system ? 1 : 0);
          break;
        case 'weight':
          comparison = (a.weight ?? 0) - (b.weight ?? 0);
          break;
        default:
          return 0;
      }

      return sortOrder === 'desc' ? -comparison : comparison;
    });

    return sorted;
  }, [filteredPages, sortBy, sortOrder]);

  /* ══════════════════════════════════════════════════════════════
     Paginated pages  (client-side, DataTable uses manualPagination)
     ══════════════════════════════════════════════════════════════ */

  const currentPage =
    parseInt(searchParams.get('page') ?? '1', 10) || 1;
  const totalCount = sortedPages.length;

  const paginatedPages = useMemo<PageRecord[]>(() => {
    const start = (currentPage - 1) * PAGE_SIZE;
    return sortedPages.slice(start, start + PAGE_SIZE);
  }, [sortedPages, currentPage]);

  /* ══════════════════════════════════════════════════════════════
     Event handlers
     ══════════════════════════════════════════════════════════════ */

  /** Open the filter drawer and synchronise form inputs with URL state. */
  const handleOpenDrawer = useCallback(() => {
    setFilterForm({
      label: searchParams.get('label') ?? '',
      name: searchParams.get('name') ?? '',
      app: searchParams.get('app') ?? '',
      entity: searchParams.get('entity') ?? '',
      type: searchParams.get('type') ?? '',
      system: searchParams.get('system') ?? '',
      customized: searchParams.get('customized') ?? '',
    });
    setDrawerOpen(true);
  }, [searchParams]);

  /** Close the filter drawer without applying changes. */
  const handleCloseDrawer = useCallback(() => {
    setDrawerOpen(false);
  }, []);

  /** Write local form state to URL params, reset to page 1, and close. */
  const handleApplyFilters = useCallback(() => {
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);

      /* Set / delete each filter param */
      (Object.keys(filterForm) as Array<keyof PageFilters>).forEach((key) => {
        const value = filterForm[key];
        if (value) {
          params.set(key, value);
        } else {
          params.delete(key);
        }
      });

      /* Reset to first page when filters change */
      params.set('page', '1');
      return params;
    });
    setDrawerOpen(false);
  }, [filterForm, setSearchParams]);

  /** Clear all filter URL params AND form state. */
  const handleClearFilters = useCallback(() => {
    setFilterForm({ ...EMPTY_FILTERS });
    setSearchParams((prev) => {
      const params = new URLSearchParams(prev);
      FILTER_KEYS.forEach((key) => params.delete(key));
      params.set('page', '1');
      return params;
    });
  }, [setSearchParams]);

  /** Clone a page by its ID (replicates monolith ClonePage POST handler). */
  const handleClone = useCallback(
    (pageId: string) => {
      clonePage({ id: pageId });
    },
    [clonePage],
  );

  /** Update a single filter-form field value. */
  const handleFilterFieldChange = useCallback(
    (field: keyof PageFilters, value: string) => {
      setFilterForm((prev) => ({ ...prev, [field]: value }));
    },
    [],
  );

  /* ══════════════════════════════════════════════════════════════
     Column definitions
     ══════════════════════════════════════════════════════════════ */

  const columns = useMemo<DataTableColumn<PageRecord>[]>(
    () => [
      /* ── 1. Action (view + clone) ────────────────────────── */
      {
        id: 'action',
        label: '',
        width: '1%',
        cell: (_value: unknown, row: PageRecord) => (
          <div className="flex items-center gap-1">
            <Link
              to={`/admin/pages/${row.id}`}
              className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-xs font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              title="View page"
            >
              {/* Eye icon (heroicons mini eye) */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-3.5 w-3.5"
                aria-hidden="true"
              >
                <path d="M10 12.5a2.5 2.5 0 100-5 2.5 2.5 0 000 5z" />
                <path
                  fillRule="evenodd"
                  d="M.664 10.59a1.651 1.651 0 010-1.186A10.004 10.004 0 0110 3c4.257 0 7.893 2.66 9.336 6.41.147.381.146.804 0 1.186A10.004 10.004 0 0110 17c-4.257 0-7.893-2.66-9.336-6.41zM14 10a4 4 0 11-8 0 4 4 0 018 0z"
                  clipRule="evenodd"
                />
              </svg>
            </Link>
            <button
              type="button"
              onClick={() => handleClone(row.id)}
              disabled={clonePending}
              className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-xs font-medium text-gray-700 shadow-sm hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              title={`Clone page "${row.label}"`}
              aria-label={`Clone page ${row.label}`}
            >
              {/* Copy/clone icon (heroicons mini document-duplicate) */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-3.5 w-3.5"
                aria-hidden="true"
              >
                <path d="M7 3.5A1.5 1.5 0 018.5 2h3.879a1.5 1.5 0 011.06.44l3.122 3.12A1.5 1.5 0 0117 6.622V12.5a1.5 1.5 0 01-1.5 1.5h-1v-3.379a3 3 0 00-.879-2.121L10.5 5.379A3 3 0 008.379 4.5H7v-1z" />
                <path d="M4.5 6A1.5 1.5 0 003 7.5v9A1.5 1.5 0 004.5 18h7a1.5 1.5 0 001.5-1.5v-5.879a1.5 1.5 0 00-.44-1.06L9.44 6.439A1.5 1.5 0 008.378 6H4.5z" />
              </svg>
            </button>
          </div>
        ),
      },

      /* ── 2. Label ────────────────────────────────────────── */
      {
        id: 'label',
        label: 'Label',
        accessorKey: 'label' as keyof PageRecord & string,
        sortable: true,
      },

      /* ── 3. Name ─────────────────────────────────────────── */
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name' as keyof PageRecord & string,
        sortable: true,
      },

      /* ── 4. App (resolved appId → name) ──────────────────── */
      {
        id: 'app',
        label: 'App',
        width: '140px',
        accessorFn: (row: PageRecord) =>
          row.appId ? (appMap.get(row.appId) ?? '') : '',
      },

      /* ── 5. Entity (resolved entityId → name, fallback ID) ─ */
      {
        id: 'entity',
        label: 'Entity',
        width: '140px',
        accessorFn: (row: PageRecord) => {
          if (!row.entityId) return '';
          return entityMap.get(row.entityId) ?? row.entityId;
        },
      },

      /* ── 6. Type (PageType label) ────────────────────────── */
      {
        id: 'type',
        label: 'Type',
        width: '120px',
        sortable: true,
        accessorFn: (row: PageRecord) => getPageTypeLabel(row.type),
      },

      /* ── 7. System (boolean indicator) ───────────────────── */
      {
        id: 'system',
        label: 'System',
        width: '80px',
        sortable: true,
        accessorFn: (row: PageRecord) => row.system,
        cell: (value: unknown) => (
          <span
            className={
              value === true ? 'text-green-600' : 'text-gray-400'
            }
            aria-label={value === true ? 'Yes' : 'No'}
          >
            {value === true ? '✓' : '✗'}
          </span>
        ),
      },

      /* ── 8. Weight (numeric sort order) ──────────────────── */
      {
        id: 'weight',
        label: 'Weight',
        width: '80px',
        sortable: true,
        accessorKey: 'weight' as keyof PageRecord & string,
      },
    ],
    [appMap, entityMap, handleClone, clonePending],
  );

  /* ══════════════════════════════════════════════════════════════
     Loading & error states
     ══════════════════════════════════════════════════════════════ */

  const isLoading = pagesLoading || appsLoading || entitiesLoading;

  if (pagesError) {
    return (
      <div className="p-6">
        <div
          role="alert"
          className="rounded border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-700"
        >
          <strong className="font-semibold">Error:</strong> Failed to load
          pages. Please try again later.
        </div>
      </div>
    );
  }

  /* ══════════════════════════════════════════════════════════════
     Render
     ══════════════════════════════════════════════════════════════ */

  return (
    <section className="flex flex-col gap-4 p-6" aria-labelledby="page-list-heading">
      {/* ── Page header ──────────────────────────────────────── */}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1
          id="page-list-heading"
          className="text-xl font-semibold text-gray-900"
        >
          Pages
        </h1>

        <div className="flex items-center gap-2">
          {/* Search / filter toggle */}
          <button
            type="button"
            onClick={handleOpenDrawer}
            className="relative inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            aria-label="Open search filters"
          >
            {/* Magnifying glass icon */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            Search
            {hasActiveFilters && (
              <span className="absolute -end-1 -top-1 flex h-4 w-4 items-center justify-center rounded-full bg-blue-600 text-[10px] font-bold text-white">
                !
              </span>
            )}
          </button>

          {/* Create Page button */}
          <Link
            to="/admin/pages/create"
            className="inline-flex items-center gap-1.5 rounded bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            {/* Plus icon */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            Create Page
          </Link>
        </div>
      </div>

      {/* ── Active filter badges (quick visibility) ──────────── */}
      {hasActiveFilters && (
        <div className="flex flex-wrap items-center gap-2" aria-label="Active filters">
          {FILTER_KEYS.map((key) => {
            const value = activeFilters[key];
            if (!value) return null;
            return (
              <span
                key={key}
                className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-2.5 py-0.5 text-xs font-medium text-blue-700"
              >
                <span className="font-semibold capitalize">{key}:</span>{' '}
                {key === 'system' || key === 'customized'
                  ? value === 'true'
                    ? 'Yes'
                    : 'No'
                  : value}
              </span>
            );
          })}
          <button
            type="button"
            onClick={handleClearFilters}
            className="text-xs font-medium text-blue-600 underline hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Clear all
          </button>
        </div>
      )}

      {/* ── Data table ───────────────────────────────────────── */}
      <DataTable<PageRecord>
        data={paginatedPages}
        columns={columns}
        totalCount={totalCount}
        pageSize={PAGE_SIZE}
        currentPage={currentPage}
        bordered
        hover
        loading={isLoading || pagesFetching}
        emptyText="No pages found."
      />

      {/* ── Filter drawer ────────────────────────────────────── */}
      <Drawer
        isVisible={drawerOpen}
        width="550px"
        title="Search Pages"
        titleAction={
          <button
            type="button"
            onClick={handleClearFilters}
            className="text-sm font-medium text-blue-600 underline hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            clear all
          </button>
        }
        onClose={handleCloseDrawer}
      >
        <form
          onSubmit={(e) => {
            e.preventDefault();
            handleApplyFilters();
          }}
          className="flex flex-col gap-4"
          aria-label="Page search filters"
        >
          {/* ── Label (text CONTAINS) ─────────────────────── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-label"
              className="text-sm font-medium text-gray-700"
            >
              Label
            </label>
            <input
              id="filter-label"
              type="text"
              value={filterForm.label}
              onChange={(e) =>
                handleFilterFieldChange('label', e.target.value)
              }
              placeholder="Contains…"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* ── Name (text CONTAINS) ──────────────────────── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-name"
              className="text-sm font-medium text-gray-700"
            >
              Name
            </label>
            <input
              id="filter-name"
              type="text"
              value={filterForm.name}
              onChange={(e) =>
                handleFilterFieldChange('name', e.target.value)
              }
              placeholder="Contains…"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* ── App (text EQ) ─────────────────────────────── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-app"
              className="text-sm font-medium text-gray-700"
            >
              App
            </label>
            <input
              id="filter-app"
              type="text"
              value={filterForm.app}
              onChange={(e) =>
                handleFilterFieldChange('app', e.target.value)
              }
              placeholder="Exact match…"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* ── Entity (text EQ) ──────────────────────────── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-entity"
              className="text-sm font-medium text-gray-700"
            >
              Entity
            </label>
            <input
              id="filter-entity"
              type="text"
              value={filterForm.entity}
              onChange={(e) =>
                handleFilterFieldChange('entity', e.target.value)
              }
              placeholder="Exact match…"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* ── Type (text CONTAINS) ──────────────────────── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-type"
              className="text-sm font-medium text-gray-700"
            >
              Type
            </label>
            <input
              id="filter-type"
              type="text"
              value={filterForm.type}
              onChange={(e) =>
                handleFilterFieldChange('type', e.target.value)
              }
              placeholder="Contains…"
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* ── System (checkbox ↔ 3-state select) ────────── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-system"
              className="text-sm font-medium text-gray-700"
            >
              System
            </label>
            <select
              id="filter-system"
              value={filterForm.system}
              onChange={(e) =>
                handleFilterFieldChange('system', e.target.value)
              }
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            >
              <option value="">All</option>
              <option value="true">Yes</option>
              <option value="false">No</option>
            </select>
          </div>

          {/* ── Customized (isRazorBody, 3-state select) ──── */}
          <div className="flex flex-col gap-1">
            <label
              htmlFor="filter-customized"
              className="text-sm font-medium text-gray-700"
            >
              Customized
            </label>
            <select
              id="filter-customized"
              value={filterForm.customized}
              onChange={(e) =>
                handleFilterFieldChange('customized', e.target.value)
              }
              className="block w-full rounded border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            >
              <option value="">All</option>
              <option value="true">Yes</option>
              <option value="false">No</option>
            </select>
          </div>

          {/* ── Apply button ──────────────────────────────── */}
          <div className="flex items-center justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={handleCloseDrawer}
              className="rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Cancel
            </button>
            <button
              type="submit"
              className="rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              Apply Filters
            </button>
          </div>
        </form>
      </Drawer>
    </section>
  );
}
