/**
 * AdminEntityPages — Entity Pages List Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/pages.cshtml[.cs]`.
 * Route: `/admin/entities/:entityId/pages`
 *
 * Displays a paginated, filterable grid of ERP pages associated with the
 * current entity.  Supports:
 *  - 8-column DataTable (action, label, name, app, entity, type, system, customized)
 *  - Client-side search drawer with 7 filter fields
 *  - Entity admin sub-navigation tabs (Details, Fields, Relations, Pages, Views, Lists)
 *  - "Create Entity Page" action link
 *  - Sorting by label (default), name, type, system, customized
 *  - Pagination with page size 15
 *
 * Source mapping:
 *  - pages.cshtml                  → JSX layout and grid columns
 *  - pages.cshtml.cs OnGet()       → usePages, useEntity, useEntities, useApps hooks
 *  - AdminPageUtils.GetEntityAdminSubNav → TabNav with entity admin tabs
 *  - PageService.GetAll()          → usePages({ entityId })
 *  - AppService.GetAllApplications → useApps()
 *  - EntityManager.ReadEntities()  → useEntities()
 *
 * @module pages/admin/AdminEntityPages
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, Link, useSearchParams, useNavigate } from 'react-router-dom';

/* ── Internal hooks ─────────────────────────────────────────── */
import { usePages } from '../../hooks/usePages';
import { useEntity, useEntities } from '../../hooks/useEntities';
import { useApps } from '../../hooks/useApps';

/* ── Internal components ────────────────────────────────────── */
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import Drawer from '../../components/common/Drawer';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

/* ── Internal types ─────────────────────────────────────────── */
import type { ErpPage } from '../../types/page';
import { PageType } from '../../types/page';
import type { Entity } from '../../types/entity';
import type { App } from '../../types/app';

/* ═══════════════════════════════════════════════════════════════
 * DataTable-Compatible Page Record Type
 *
 * DataTable<T> constrains T to Record<string, unknown>.  ErpPage is a
 * typed interface without an explicit index signature, so we create an
 * intersection type that satisfies the constraint while preserving all
 * ErpPage property types for column cell renderers.
 * ═══════════════════════════════════════════════════════════════ */

type PageRecord = ErpPage & Record<string, unknown>;

/* ═══════════════════════════════════════════════════════════════
 * Constants
 * ═══════════════════════════════════════════════════════════════ */

/** Matches the monolith's PagerSize = 15 from pages.cshtml wv-grid. */
const PAGE_SIZE = 15;

/** Default sort column — monolith orders by label ascending. */
const DEFAULT_SORT_BY = 'label';

/** Default sort direction. */
const DEFAULT_SORT_ORDER: 'asc' | 'desc' = 'asc';

/* ═══════════════════════════════════════════════════════════════
 * Filter State
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Shape of the search drawer filter form state.
 *
 * Maps 1:1 to the 7 filter fields in pages.cshtml search drawer:
 *  - label (CONTAINS), name (CONTAINS), app (EQ), entity (EQ),
 *    type (CONTAINS), system (checkbox / EQ), customized (checkbox / EQ)
 */
interface FilterState {
  label: string;
  name: string;
  app: string;
  entity: string;
  type: string;
  /** Boolean selector: '' (any) | 'true' | 'false'. */
  system: string;
  /** Boolean selector: '' (any) | 'true' | 'false'. */
  customized: string;
}

/** Empty filter state — no active filters. */
const INITIAL_FILTERS: FilterState = {
  label: '',
  name: '',
  app: '',
  entity: '',
  type: '',
  system: '',
  customized: '',
};

/* ═══════════════════════════════════════════════════════════════
 * Helper — PageType → Human-Readable Label
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Converts a `PageType` enum value to a human-readable string.
 * Replaces `page.Type.GetLabel()` from the monolith's C# enum extension.
 */
function getPageTypeLabel(type: PageType | undefined | null): string {
  switch (type) {
    case PageType.Home:
      return 'Home';
    case PageType.Site:
      return 'Site';
    case PageType.Application:
      return 'Application';
    case PageType.RecordList:
      return 'Record List';
    case PageType.RecordCreate:
      return 'Record Create';
    case PageType.RecordDetails:
      return 'Record Details';
    case PageType.RecordManage:
      return 'Record Manage';
    default:
      return '';
  }
}

/* ═══════════════════════════════════════════════════════════════
 * Helper — Entity Admin Sub-Navigation Tabs
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Builds the entity admin sub-navigation tabs array.
 *
 * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "pages")` from
 * the monolith's SDK plugin.  Each tab represents a section of the entity
 * admin: Details, Fields, Relations, Pages (active), Views, Lists.
 *
 * Tab content is intentionally empty because the TabNav is used as a
 * navigation bar — each section is a separate route.  The `onTabChange`
 * handler navigates when a different tab is clicked.
 */
function buildEntityAdminSubNavTabs(): TabConfig[] {
  return [
    { id: 'details', label: 'Details' },
    { id: 'fields', label: 'Fields' },
    { id: 'relations', label: 'Relations' },
    { id: 'pages', label: 'Pages' },
    { id: 'views', label: 'Views' },
    { id: 'lists', label: 'Lists' },
  ];
}

/**
 * Maps an entity admin sub-nav tab ID to the corresponding route path.
 */
function getTabRoutePath(entityId: string, tabId: string): string {
  const base = `/admin/entities/${entityId}`;
  switch (tabId) {
    case 'details':
      return base;
    case 'fields':
      return `${base}/fields`;
    case 'relations':
      return `${base}/relations`;
    case 'pages':
      return `${base}/pages`;
    case 'views':
      return `${base}/views`;
    case 'lists':
      return `${base}/lists`;
    default:
      return base;
  }
}

/* ═══════════════════════════════════════════════════════════════
 * Helper — Client-Side Filtering
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Applies the 7 search filters to a pages array.
 *
 * Reproduces the monolith's `pages.cshtml.cs` filter logic:
 *  - label / name / type → case-insensitive CONTAINS
 *  - app / entity        → exact (EQ) match on resolved name
 *  - system / customized → boolean EQ match
 */
function applyFilters(
  pages: readonly ErpPage[],
  filters: FilterState,
  appNameMap: ReadonlyMap<string, string>,
  entityNameMap: ReadonlyMap<string, string>,
): ErpPage[] {
  return pages.filter((page) => {
    /* label — CONTAINS */
    if (
      filters.label &&
      !(page.label ?? '').toLowerCase().includes(filters.label.toLowerCase())
    ) {
      return false;
    }

    /* name — CONTAINS */
    if (
      filters.name &&
      !(page.name ?? '').toLowerCase().includes(filters.name.toLowerCase())
    ) {
      return false;
    }

    /* app — EQ on resolved app name */
    if (filters.app) {
      const resolvedApp = appNameMap.get(page.appId ?? '') ?? '';
      if (resolvedApp.toLowerCase() !== filters.app.toLowerCase()) {
        return false;
      }
    }

    /* entity — EQ on resolved entity name */
    if (filters.entity) {
      const resolvedEntity = entityNameMap.get(page.entityId ?? '') ?? '';
      if (resolvedEntity.toLowerCase() !== filters.entity.toLowerCase()) {
        return false;
      }
    }

    /* type — CONTAINS on human-readable label */
    if (filters.type) {
      const typeLabel = getPageTypeLabel(page.type);
      if (!typeLabel.toLowerCase().includes(filters.type.toLowerCase())) {
        return false;
      }
    }

    /* system — EQ boolean */
    if (filters.system === 'true' && !page.system) {
      return false;
    }
    if (filters.system === 'false' && page.system) {
      return false;
    }

    /* customized (isRazorBody) — EQ boolean */
    if (filters.customized === 'true' && !page.isRazorBody) {
      return false;
    }
    if (filters.customized === 'false' && page.isRazorBody) {
      return false;
    }

    return true;
  });
}

/* ═══════════════════════════════════════════════════════════════
 * Helper — Client-Side Sorting
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Sorts pages by the specified column.
 *
 * Reproduces the monolith's sort logic from `pages.cshtml.cs`:
 * label (default), name, type, system, customized.
 */
function sortPages(
  pages: ErpPage[],
  sortBy: string,
  sortOrder: 'asc' | 'desc',
): ErpPage[] {
  const sorted = [...pages];
  const direction = sortOrder === 'desc' ? -1 : 1;

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
        comparison =
          Number(a.system ?? false) - Number(b.system ?? false);
        break;
      case 'customized':
        comparison =
          Number(a.isRazorBody ?? false) - Number(b.isRazorBody ?? false);
        break;
      default:
        comparison = (a.label ?? '').localeCompare(b.label ?? '');
        break;
    }

    return comparison * direction;
  });

  return sorted;
}

/* ═══════════════════════════════════════════════════════════════
 * Inline SVG Icons
 *
 * Using inline SVGs with fill="currentColor" per UI7 guidelines.
 * No hardcoded width/height — CSS controls sizing via className.
 * ═══════════════════════════════════════════════════════════════ */

/** Eye icon for the action column — matches the monolith's ti-eye icon class. */
function EyeIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block w-4 h-4"
      aria-hidden="true"
    >
      <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

/** Search icon for the search toggle button. */
function SearchIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block w-4 h-4"
      aria-hidden="true"
    >
      <circle cx="11" cy="11" r="8" />
      <line x1="21" y1="21" x2="16.65" y2="16.65" />
    </svg>
  );
}

/** Plus icon for the create button. */
function PlusIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="inline-block w-4 h-4 me-1"
      aria-hidden="true"
    >
      <line x1="12" y1="5" x2="12" y2="19" />
      <line x1="5" y1="12" x2="19" y2="12" />
    </svg>
  );
}

/* ═══════════════════════════════════════════════════════════════
 * Shared Tailwind class-name constants
 *
 * Centralised for single-source-of-truth; avoids repeated long strings
 * and ensures consistency across all form inputs in the drawer.
 * ═══════════════════════════════════════════════════════════════ */

const INPUT_CLASSES = [
  'rounded border border-gray-300 px-3 py-2 text-sm',
  'focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600',
].join(' ');

const LABEL_CLASSES = 'text-sm font-medium text-gray-700';

/* ═══════════════════════════════════════════════════════════════
 * Main Component
 * ═══════════════════════════════════════════════════════════════ */

/**
 * Entity pages list page component.
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/pages.cshtml[.cs]`.
 *
 * Workflow:
 *  1. Reads `entityId` from route parameters
 *  2. Fetches pages (filtered by entity), current entity, all entities, all apps
 *  3. Builds lookup maps for app/entity name resolution in grid columns
 *  4. Applies client-side search filters from the drawer form
 *  5. Sorts and paginates the result via DataTable (page size 15)
 *  6. Renders entity admin sub-navigation tabs with "pages" active
 */
export default function AdminEntityPages() {
  /* ─── Route Parameters ──────────────────────────────────── */
  const { entityId = '' } = useParams<{ entityId: string }>();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  /* ─── Data Fetching ─────────────────────────────────────── */

  /**
   * Fetch pages for the current entity.
   * Replaces: `PageService().GetAll()` → filter by EntityId
   * Returns ApiResponse<ErpPage[]> — access via data?.object
   */
  const {
    data: pagesResponse,
    isLoading: pagesLoading,
    isError: pagesError,
  } = usePages(entityId ? { entityId } : undefined);

  /**
   * Fetch current entity metadata for page header and sub-nav context.
   * Replaces: `EntityManager().ReadEntity(ParentRecordId)` from pages.cshtml.cs
   * Returns Entity directly (unwrapped by assertApiSuccess).
   */
  const { data: currentEntity, isLoading: entityLoading } =
    useEntity(entityId);

  /**
   * Fetch all entities for name resolution in grid 'entity' column.
   * Replaces: `EntityManager().ReadEntities().Object` from pages.cshtml.cs
   * Returns Entity[] directly (unwrapped by assertApiSuccess).
   */
  const { data: allEntities } = useEntities();

  /**
   * Fetch all applications for name resolution in grid 'app' column.
   * Replaces: `AppService.GetAllApplications()` from pages.cshtml.cs
   * Returns ApiResponse<App[]> — access via data?.object
   */
  const { data: appsResponse } = useApps();

  /* ─── Local State ───────────────────────────────────────── */
  const [drawerVisible, setDrawerVisible] = useState(false);
  const [filters, setFilters] = useState<FilterState>(INITIAL_FILTERS);

  /* ─── URL-Based Sort State ──────────────────────────────── */
  const sortBy = searchParams.get('sortBy') ?? DEFAULT_SORT_BY;
  const sortOrder = (searchParams.get('sortOrder') ?? DEFAULT_SORT_ORDER) as
    | 'asc'
    | 'desc';

  /* ─── Lookup Maps ───────────────────────────────────────── */

  /**
   * Entity ID → entity name lookup map.
   * Replaces monolith pattern:
   *   `entities.First(x => x.Id == page.EntityId)?.Name`
   */
  const entityNameMap = useMemo<Map<string, string>>(() => {
    const map = new Map<string, string>();
    if (allEntities) {
      for (const ent of allEntities) {
        if (ent.id && ent.name) {
          map.set(ent.id, ent.name);
        }
      }
    }
    return map;
  }, [allEntities]);

  /**
   * App ID → app name lookup map.
   * Replaces monolith pattern:
   *   `allApps.FirstOrDefault(x => x.Id == record.AppId)?.Name`
   */
  const appNameMap = useMemo<Map<string, string>>(() => {
    const map = new Map<string, string>();
    const apps = appsResponse?.object;
    if (apps) {
      for (const application of apps) {
        if (application.id && application.name) {
          map.set(application.id, application.name);
        }
      }
    }
    return map;
  }, [appsResponse]);

  /* ─── Filtered & Sorted Pages ───────────────────────────── */
  const processedPages = useMemo<ErpPage[]>(() => {
    const rawPages: ErpPage[] = pagesResponse?.object ?? [];

    // Apply client-side search filters (matches monolith filter-then-sort order)
    const filtered = applyFilters(rawPages, filters, appNameMap, entityNameMap);

    // Sort by the active sort column
    return sortPages(filtered, sortBy, sortOrder);
  }, [pagesResponse, filters, appNameMap, entityNameMap, sortBy, sortOrder]);

  /* ─── Column Definitions ────────────────────────────────── */

  /**
   * 8 columns matching the monolith's wv-grid from pages.cshtml:
   * action | label | name | app | entity | type | system | customized
   *
   * Uses PageRecord (ErpPage & Record<string, unknown>) to satisfy the
   * DataTable generic constraint while preserving typed property access.
   */
  const columns = useMemo<DataTableColumn<PageRecord>[]>(
    () => [
      /* 1. Action — eye icon link to page detail */
      {
        id: 'action',
        label: '',
        width: '1%',
        sortable: false,
        noWrap: true,
        cell: (_value: unknown, record: PageRecord) => (
          <Link
            to={`/admin/entities/${entityId}/pages/${record.id}`}
            className="text-blue-600 hover:text-blue-800 transition-colors duration-200"
            title="View page details"
          >
            <EyeIcon />
          </Link>
        ),
      },
      /* 2. Label — page label, sortable */
      {
        id: 'label',
        label: 'Label',
        accessorKey: 'label',
        sortable: true,
      },
      /* 3. Name — page name, sortable */
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        sortable: true,
      },
      /* 4. App — resolved application name */
      {
        id: 'app',
        label: 'App',
        sortable: false,
        cell: (_value: unknown, record: PageRecord) =>
          appNameMap.get(record.appId ?? '') ?? '',
      },
      /* 5. Entity — resolved entity name */
      {
        id: 'entity',
        label: 'Entity',
        sortable: false,
        cell: (_value: unknown, record: PageRecord) =>
          entityNameMap.get(record.entityId ?? '') ?? '',
      },
      /* 6. Type — page type label, sortable */
      {
        id: 'type',
        label: 'Type',
        sortable: true,
        cell: (_value: unknown, record: PageRecord) =>
          getPageTypeLabel(record.type),
      },
      /* 7. System — read-only checkbox, sortable */
      {
        id: 'system',
        label: 'System',
        sortable: true,
        horizontalAlign: 'center' as const,
        width: '80px',
        cell: (_value: unknown, record: PageRecord) => (
          <input
            type="checkbox"
            checked={record.system ?? false}
            readOnly
            disabled
            aria-label={`System page: ${record.system ? 'Yes' : 'No'}`}
            className="pointer-events-none"
          />
        ),
      },
      /* 8. Customized — maps to isRazorBody, sortable */
      {
        id: 'customized',
        label: 'Customized',
        sortable: true,
        horizontalAlign: 'center' as const,
        width: '100px',
        cell: (_value: unknown, record: PageRecord) => (
          <input
            type="checkbox"
            checked={record.isRazorBody ?? false}
            readOnly
            disabled
            aria-label={`Customized page: ${record.isRazorBody ? 'Yes' : 'No'}`}
            className="pointer-events-none"
          />
        ),
      },
    ],
    [entityId, appNameMap, entityNameMap],
  );

  /* ─── Sub-Navigation Tabs ───────────────────────────────── */
  const subNavTabs = useMemo(() => buildEntityAdminSubNavTabs(), []);

  /* ─── Event Handlers ────────────────────────────────────── */

  /** Toggle search drawer visibility. */
  const toggleDrawer = useCallback(() => {
    setDrawerVisible((prev) => !prev);
  }, []);

  /** Close the search drawer. */
  const closeDrawer = useCallback(() => {
    setDrawerVisible(false);
  }, []);

  /**
   * Handle entity admin sub-nav tab changes.
   * Navigates to the corresponding admin section route when a different
   * tab is clicked.  The "pages" tab is a no-op since we are already here.
   */
  const handleTabChange = useCallback(
    (tabId: string) => {
      if (tabId !== 'pages') {
        navigate(getTabRoutePath(entityId, tabId));
      }
    },
    [entityId, navigate],
  );

  /** Update a single filter field value. */
  const handleFilterFieldChange = useCallback(
    (field: keyof FilterState, value: string) => {
      setFilters((prev) => ({ ...prev, [field]: value }));
    },
    [],
  );

  /**
   * Apply current filters and close the drawer.
   * Resets pagination to page 1 to show results from the start.
   */
  const handleFilterApply = useCallback(() => {
    setDrawerVisible(false);
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.set('page', '1');
      return next;
    });
  }, [setSearchParams]);

  /** Clear all filter fields and reset pagination. */
  const handleFilterClear = useCallback(() => {
    setFilters(INITIAL_FILTERS);
    setSearchParams((prev) => {
      const next = new URLSearchParams(prev);
      next.set('page', '1');
      return next;
    });
  }, [setSearchParams]);

  /* ─── Loading State ─────────────────────────────────────── */
  if (entityLoading || pagesLoading) {
    return (
      <div
        className="flex items-center justify-center min-h-[200px]"
        role="status"
        aria-live="polite"
      >
        <span className="sr-only">Loading entity pages…</span>
        <div className="animate-pulse flex flex-col gap-4 w-full max-w-4xl px-4">
          <div className="h-8 bg-gray-200 rounded w-1/3" />
          <div className="h-4 bg-gray-200 rounded w-full" />
          <div className="h-4 bg-gray-200 rounded w-full" />
          <div className="h-4 bg-gray-200 rounded w-3/4" />
        </div>
      </div>
    );
  }

  /* ─── Error State ───────────────────────────────────────── */
  if (pagesError || !currentEntity) {
    return (
      <div
        className="bg-red-50 border border-red-200 rounded-md p-4 m-4 text-red-800"
        role="alert"
      >
        <h3 className="text-lg font-semibold mb-2">Error Loading Pages</h3>
        <p>
          {pagesError
            ? 'Failed to load pages for this entity. Please try again later.'
            : 'The requested entity could not be found.'}
        </p>
        <Link
          to="/admin/entities"
          className="mt-3 inline-block text-blue-600 hover:text-blue-800 underline"
        >
          ← Back to Entities
        </Link>
      </div>
    );
  }

  /* ─── Derived Values ────────────────────────────────────── */
  const hasActiveFilters = Object.values(filters).some((v) => v !== '');

  /* ─── Render ────────────────────────────────────────────── */
  return (
    <div className="flex flex-col gap-4">
      {/* ═══ Entity Admin Sub-Navigation ═══ */}
      <TabNav
        tabs={subNavTabs}
        activeTabId="pages"
        onTabChange={handleTabChange}
        visibleTabs={subNavTabs.length}
      />

      {/* ═══ Page Header ═══ */}
      <div className="flex items-center justify-between flex-wrap gap-2">
        <h4 className="text-lg font-semibold text-gray-800">
          <span className="text-gray-500 me-1">Entity:</span>
          {currentEntity.label ?? currentEntity.name}
          <span className="text-gray-400 mx-2" aria-hidden="true">
            ›
          </span>
          Pages
        </h4>

        <div className="flex items-center gap-2">
          {/* Create Entity Page action link */}
          <Link
            to={`/admin/entities/${entityId}/pages/create?PresetEntityId=${entityId}`}
            className={[
              'inline-flex items-center px-3 py-1.5 rounded',
              'bg-green-600 text-white text-sm font-medium',
              'hover:bg-green-700',
              'focus-visible:outline focus-visible:outline-2',
              'focus-visible:outline-offset-2 focus-visible:outline-green-600',
            ].join(' ')}
          >
            <PlusIcon />
            Create Entity Page
          </Link>

          {/* Search toggle button */}
          <button
            type="button"
            onClick={toggleDrawer}
            className={[
              'inline-flex items-center px-3 py-1.5 rounded border text-sm font-medium',
              hasActiveFilters
                ? 'bg-blue-50 border-blue-300 text-blue-700'
                : 'bg-white border-gray-300 text-gray-700',
              'hover:bg-gray-50',
              'focus-visible:outline focus-visible:outline-2',
              'focus-visible:outline-offset-2 focus-visible:outline-blue-600',
            ].join(' ')}
            aria-label="Toggle search filters"
            aria-expanded={drawerVisible}
          >
            <SearchIcon />
            <span className="ms-1">Search</span>
            {hasActiveFilters && (
              <span
                className="ms-1.5 inline-flex items-center justify-center w-5 h-5 rounded-full bg-blue-600 text-white text-xs"
                aria-label="Filters active"
              >
                !
              </span>
            )}
          </button>
        </div>
      </div>

      {/* ═══ Data Table ═══ */}
      <DataTable<PageRecord>
        data={processedPages as PageRecord[]}
        columns={columns}
        totalCount={processedPages.length}
        pageSize={PAGE_SIZE}
        loading={pagesLoading}
        hover
        striped
      />

      {/* ═══ Search Drawer ═══ */}
      <Drawer
        isVisible={drawerVisible}
        onClose={closeDrawer}
        title="Search Pages"
        titleAction={
          hasActiveFilters ? (
            <button
              type="button"
              onClick={handleFilterClear}
              className="text-sm text-red-600 hover:text-red-800 underline"
            >
              Clear Filters
            </button>
          ) : undefined
        }
      >
        <form
          onSubmit={(e) => {
            e.preventDefault();
            handleFilterApply();
          }}
          className="flex flex-col gap-4 p-4"
        >
          {/* Label filter — CONTAINS */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-label" className={LABEL_CLASSES}>
              Label
            </label>
            <input
              id="filter-label"
              type="text"
              value={filters.label}
              onChange={(e) =>
                handleFilterFieldChange('label', e.target.value)
              }
              placeholder="Contains…"
              className={INPUT_CLASSES}
            />
          </div>

          {/* Name filter — CONTAINS */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-name" className={LABEL_CLASSES}>
              Name
            </label>
            <input
              id="filter-name"
              type="text"
              value={filters.name}
              onChange={(e) =>
                handleFilterFieldChange('name', e.target.value)
              }
              placeholder="Contains…"
              className={INPUT_CLASSES}
            />
          </div>

          {/* App filter — EQ */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-app" className={LABEL_CLASSES}>
              App
            </label>
            <input
              id="filter-app"
              type="text"
              value={filters.app}
              onChange={(e) =>
                handleFilterFieldChange('app', e.target.value)
              }
              placeholder="Exact match…"
              className={INPUT_CLASSES}
            />
          </div>

          {/* Entity filter — EQ */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-entity" className={LABEL_CLASSES}>
              Entity
            </label>
            <input
              id="filter-entity"
              type="text"
              value={filters.entity}
              onChange={(e) =>
                handleFilterFieldChange('entity', e.target.value)
              }
              placeholder="Exact match…"
              className={INPUT_CLASSES}
            />
          </div>

          {/* Type filter — CONTAINS */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-type" className={LABEL_CLASSES}>
              Type
            </label>
            <input
              id="filter-type"
              type="text"
              value={filters.type}
              onChange={(e) =>
                handleFilterFieldChange('type', e.target.value)
              }
              placeholder="Contains…"
              className={INPUT_CLASSES}
            />
          </div>

          {/* System filter — Boolean EQ */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-system" className={LABEL_CLASSES}>
              System
            </label>
            <select
              id="filter-system"
              value={filters.system}
              onChange={(e) =>
                handleFilterFieldChange('system', e.target.value)
              }
              className={INPUT_CLASSES}
            >
              <option value="">Any</option>
              <option value="true">Yes</option>
              <option value="false">No</option>
            </select>
          </div>

          {/* Customized filter — Boolean EQ */}
          <div className="flex flex-col gap-1">
            <label htmlFor="filter-customized" className={LABEL_CLASSES}>
              Customized
            </label>
            <select
              id="filter-customized"
              value={filters.customized}
              onChange={(e) =>
                handleFilterFieldChange('customized', e.target.value)
              }
              className={INPUT_CLASSES}
            >
              <option value="">Any</option>
              <option value="true">Yes</option>
              <option value="false">No</option>
            </select>
          </div>

          {/* Action buttons */}
          <div className="flex items-center gap-2 pt-2 border-t border-gray-200">
            <button
              type="submit"
              className={[
                'inline-flex items-center px-4 py-2 rounded text-sm font-medium',
                'bg-blue-600 text-white hover:bg-blue-700',
                'focus-visible:outline focus-visible:outline-2',
                'focus-visible:outline-offset-2 focus-visible:outline-blue-600',
              ].join(' ')}
            >
              Apply Filters
            </button>
            <button
              type="button"
              onClick={handleFilterClear}
              className={[
                'inline-flex items-center px-4 py-2 rounded text-sm font-medium',
                'bg-white border border-gray-300 text-gray-700',
                'hover:bg-gray-50',
                'focus-visible:outline focus-visible:outline-2',
                'focus-visible:outline-offset-2 focus-visible:outline-blue-600',
              ].join(' ')}
            >
              Clear
            </button>
          </div>
        </form>
      </Drawer>
    </div>
  );
}
