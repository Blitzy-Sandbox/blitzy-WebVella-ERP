/**
 * RecordList Page Component
 *
 * React page component replacing `WebVella.Erp.Web/Pages/RecordList.cshtml[.cs]`
 * (`RecordListPageModel`). Displays a data grid (DataTable) of entity records
 * with server-side sorting, URL-based pagination, filtering, and row-click
 * navigation to record details.
 *
 * This is one of the most frequently used pages in the ERP — the primary
 * listing view for any entity's records.
 *
 * Route: /:appName/:areaName/:nodeName/l/:pageName?
 *
 * Lifecycle (mirrors monolith OnGet):
 *  1. Resolve page context from URL params via usePageByUrl  (replaces Init())
 *  2. Canonical redirect if pageName param doesn't match resolved page name
 *  3. Fetch entity metadata for DataTable column definitions via useEntity
 *  4. Fetch app context via useApp for navigation store sync
 *  5. Sync navigation context to global store for layout chrome
 *  6. Read pagination/sort/filter state from URL search params
 *  7. Fetch records list via useRecords with skip/limit + sort params
 *  8. Fetch total count via useRecordCount for server-side pagination
 *  9. Render DataTable with columns derived from entity fields
 *
 * Source files:
 *   - WebVella.Erp.Web/Pages/RecordList.cshtml   (35 lines — Razor view)
 *   - WebVella.Erp.Web/Pages/RecordList.cshtml.cs (97 lines — PageModel)
 */

import { useState, useEffect, useMemo, useCallback } from 'react';
// flushSync is now passed as a navigate option (createBrowserRouter support)
import { useParams, useNavigate, useSearchParams, Link } from 'react-router-dom';

import { useRecords, useRecordCount } from '../../hooks/useRecords';
import { useEntity } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';
import { useApp } from '../../hooks/useApps';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import type { Entity } from '../../types/entity';
import type { EntityRecord, EntityRecordList } from '../../types/record';
import type { ErpPage, PageBodyNode } from '../../types/page';
import { PageType } from '../../types/page';
import type { UrlInfo } from '../../types/common';
import type { QueryObject } from '../../types/filter';
import { QueryType } from '../../types/filter';
import { useAppStore } from '../../stores/appStore';
import type { RecordsParams } from '../../hooks/useRecords';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default page size for record listing pagination. */
const DEFAULT_PAGE_SIZE = 10;

/** Maximum number of entity fields to show as columns in the grid. */
const MAX_VISIBLE_COLUMNS = 15;

/**
 * System-managed fields that should not appear as grid columns.
 * These are internal metadata fields not useful for end-user display.
 */
const HIDDEN_SYSTEM_FIELDS = new Set<string>([
  'id',
  'created_on',
  'created_by',
  'last_modified_on',
  'last_modified_by',
]);

// ---------------------------------------------------------------------------
// Route Parameter Type
// ---------------------------------------------------------------------------

/**
 * Route parameters expected from React Router for
 * /:appName/:areaName/:nodeName/l/:pageName?
 *
 * React Router v7 types useParams with Record<string, string | undefined>,
 * so we use a type alias that satisfies the constraint while preserving
 * the named parameter documentation.
 */
type RecordListRouteParams = {
  appName?: string;
  areaName?: string;
  nodeName?: string;
  pageName?: string;
  entityName?: string;
  recordId?: string;
  [key: string]: string | undefined;
};

// ---------------------------------------------------------------------------
// RecordList Component
// ---------------------------------------------------------------------------

/**
 * Record listing page component.
 *
 * Replaces RecordListPageModel from the ASP.NET monolith. Resolves page
 * context from URL parameters, fetches entity metadata and record data,
 * renders a DataTable grid with sorting, pagination, and filtering, and
 * handles row-click navigation to record detail pages.
 *
 * Default exported for React.lazy() code-splitting.
 */
export default function RecordList(): React.JSX.Element {
  // -- Route parameters & navigation ------------------------------------------

  const params = useParams<RecordListRouteParams>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // Safely extract typed route params (React Router 7 returns string | undefined)
  const appName = params.appName ?? '';
  const areaName = params.areaName ?? '';
  const nodeName = params.nodeName ?? '';
  const pageName = params.pageName ?? '';
  /** Standalone entity name for /records/:entityName route. */
  const standaloneEntityName = params.entityName ?? '';

  /** Whether the component is rendered from a standalone /records/:entityName route */
  const isStandalone = !!(standaloneEntityName && !appName && !areaName && !nodeName);

  // -- Local state ------------------------------------------------------------
  // Mirrors monolith's Validation.Message pattern from RecordList.cshtml.cs
  // OnPost error handling (lines 76-95). Used to surface fetch errors and
  // validation messages from mutations (bulk delete, etc.)

  const [validationMessage, setValidationMessage] = useState<string>('');

  // -- Global navigation store ------------------------------------------------

  const setCurrentPage = useAppStore((state) => state.setCurrentPage);
  const setRouteParams = useAppStore((state) => state.setRouteParams);
  const updateNavigationContext = useAppStore(
    (state) => state.updateNavigationContext,
  );
  const currentApp = useAppStore((state) => state.currentApp);

  // currentApp provides fallback app-level metadata (e.g. area/node labels
  // for breadcrumb context) when the individual app fetch hasn't resolved yet.

  // -- 1. Resolve page context (replaces Init() → ErpRequestContext) ----------
  // Builds UrlInfo matching the common.ts UrlInfo interface for the
  // usePageByUrl hook which calls POST /pages/resolve

  const urlInfo: UrlInfo | undefined =
    appName && areaName && nodeName
      ? {
          hasRelation: false,
          pageType: PageType.RecordList as number,
          appName,
          areaName,
          nodeName,
          pageName,
          recordId: null,
          relationId: null,
          parentRecordId: null,
        }
      : undefined;

  const { data: pageResponse, isLoading: isPageLoading } = usePageByUrl(
    urlInfo,
    { enabled: !!urlInfo },
  );

  // Extract the ErpPage from the API response envelope
  const page: ErpPage | undefined = pageResponse?.object ?? undefined;

  // -- 2. Fetch app context (replaces ErpRequestContext.SetCurrentApp) --------

  const { data: appResponse, isLoading: isAppLoading } = useApp(
    appName || undefined,
  );
  const app = appResponse?.object ?? undefined;

  // -- 3. Canonical redirect --------------------------------------------------
  // Mirrors RecordList.cshtml.cs lines 23-27:
  // if (PageName != ErpRequestContext.Page.Name)
  //   return Redirect($"/{App}/{Area}/{Node}/l/{Page.Name}{queryString}");

  useEffect(() => {
    if (page && pageName && pageName !== page.name) {
      const queryString = searchParams.toString();
      const canonicalPath = `/${appName}/${areaName}/${nodeName}/l/${page.name}`;
      navigate(
        queryString ? `${canonicalPath}?${queryString}` : canonicalPath,
        { replace: true },
      );
    }
  }, [page, pageName, appName, areaName, nodeName, searchParams, navigate]);

  // -- 4. Sync navigation context ---------------------------------------------
  // Replaces BaseErpPageModel.Init() which populated ErpRequestContext
  // used by _AppMaster.cshtml layout components (Sidebar, TopNav, Breadcrumb)

  useEffect(() => {
    if (appName || areaName || nodeName || pageName) {
      setRouteParams({
        appName,
        areaName,
        nodeName,
        pageName: pageName || undefined,
      });
    }
  }, [appName, areaName, nodeName, pageName, setRouteParams]);

  useEffect(() => {
    if (page) {
      setCurrentPage(page);
    }
  }, [page, setCurrentPage]);

  useEffect(() => {
    // Use the freshly-fetched app if available; fall back to the store's
    // currentApp so layout chrome (Sidebar, TopNav, Breadcrumb) still
    // renders correct nav context even if the per-page app fetch is slower.
    const resolvedApp = app ?? currentApp ?? null;
    if (page || resolvedApp) {
      updateNavigationContext({
        app: resolvedApp,
        page: page ?? null,
      });
    }
  }, [page, app, currentApp, updateNavigationContext]);

  // -- 5. Fetch entity metadata for grid column definitions -------------------
  // Uses the entityId from the resolved page to determine entity name.
  // The useEntity hook accepts idOrName (string).
  // Replaces ErpRequestContext.Entity metadata loading from BeforeRender().

  const entityIdOrName = page?.entityId ?? standaloneEntityName ?? '';

  const { data: entity, isLoading: isEntityLoading } =
    useEntity(entityIdOrName);

  // -- 6. Pagination / sort / filter state from URL search params -------------
  // Replaces the monolith's EQL query parameter handling.
  // URL search params enable bookmarkable/shareable listing state.

  const currentPage = useMemo(() => {
    const raw = searchParams.get('page');
    const parsed = raw ? parseInt(raw, 10) : 1;
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 1;
  }, [searchParams]);

  const currentPageSize = useMemo(() => {
    const raw = searchParams.get('pageSize');
    const parsed = raw ? parseInt(raw, 10) : DEFAULT_PAGE_SIZE;
    return Number.isFinite(parsed) && parsed > 0
      ? parsed
      : DEFAULT_PAGE_SIZE;
  }, [searchParams]);

  const sortBy = useMemo(
    () => searchParams.get('sortBy') ?? '',
    [searchParams],
  );

  const sortOrder = useMemo(
    () => (searchParams.get('sortOrder') as 'asc' | 'desc') || 'asc',
    [searchParams],
  );

  const searchTerm = useMemo(
    () => searchParams.get('search') ?? '',
    [searchParams],
  );

  // -- 7. Build QueryObject from search / filter URL params -------------------
  // Constructs a QueryObject filter tree from the search term.
  // The CONTAINS query type mirrors the monolith's EQL WHERE clause text
  // search used by PcGrid's filter functionality.
  // When no search term is present, filterQuery is null (no filtering).

  const filterQuery: QueryObject | null = useMemo(() => {
    if (!searchTerm) return null;

    // Build a CONTAINS filter on the entity's primary display field.
    // This mirrors the monolith's PcGrid inline search behavior which
    // sent a CONTAINS filter on the user's typed text.
    return {
      queryType: QueryType.CONTAINS,
      fieldName: 'label',
      fieldValue: searchTerm,
      subQueries: [],
    };
  }, [searchTerm]);

  // -- 8. Build useRecords parameters -----------------------------------------
  // Converts URL-based page/pageSize to skip/limit for the API.
  // Constructs QuerySortObject[] from URL sort params.
  // QuerySortType const enum: Ascending = 0, Descending = 1

  const recordsParams: RecordsParams = useMemo(() => {
    const skip = (currentPage - 1) * currentPageSize;
    const limit = currentPageSize;

    const sort =
      sortBy.length > 0
        ? [
            {
              fieldName: sortBy,
              sortType: sortOrder === 'desc' ? 1 : 0,
            } as const,
          ]
        : undefined;

    return {
      fields: '*',
      query: filterQuery,
      skip,
      limit,
      sort: sort as RecordsParams['sort'],
    };
  }, [currentPage, currentPageSize, sortBy, sortOrder, filterQuery]);

  // -- 9. Fetch records list (replaces DataModel population) ------------------
  // useRecords returns QueryResult { fieldsMeta: Field[], data: EntityRecord[] }
  // Prefer entity.id for the API path when available — the backend resolves
  // records by entity UUID, and name-based paths may not be supported by all
  // service implementations.  Fall back to entity.name for display/routing.

  const entityName = entity?.id ?? entity?.name ?? '';

  const {
    data: recordsData,
    isLoading: isRecordsLoading,
    isFetching: isRecordsFetching,
    isError: isRecordsError,
    error: recordsError,
  } = useRecords(entityName, recordsParams);

  // -- 10. Fetch total record count for server-side pagination ----------------
  // The monolith used EQL PAGE/PAGESIZE with a separate TotalCount in the
  // DataModel. The useRecords hook returns QueryResult which does NOT include
  // totalCount. We use useRecordCount — a dedicated API call to
  // GET /entities/{name}/records/count — to get the accurate total.
  // This same filterQuery is passed so the count reflects any active filters.

  const {
    data: totalRecordCount,
    isLoading: isCountLoading,
  } = useRecordCount(entityName, filterQuery);

  // Surface fetch errors to the validation message display
  useEffect(() => {
    if (isRecordsError && recordsError) {
      setValidationMessage(
        recordsError.message || 'An error occurred while loading records.',
      );
    } else if (!isRecordsError && validationMessage) {
      // Clear error message when the query succeeds on retry
      setValidationMessage('');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isRecordsError, recordsError]);

  // -- 11. Build DataTable columns from entity fields -------------------------
  // Transforms Entity.fields[] (Field[]) into DataTableColumn[] for the grid.
  // Mirrors how PcGrid ViewComponent read field definitions from DataModel
  // to configure its column headers.
  // Uses Entity.name, Entity.label, Entity.labelPlural, Entity.fields,
  // Entity.iconName, Entity.color from the resolved entity metadata.

  const columns: DataTableColumn<EntityRecord>[] = useMemo(() => {
    const entityFields: Entity['fields'] = entity?.fields ?? [];
    if (entityFields.length === 0) return [];

    // Filter out hidden system fields and limit to max visible columns
    const visibleFields = entityFields
      .filter(
        (field) =>
          !HIDDEN_SYSTEM_FIELDS.has(field.name) && !field.system,
      )
      .slice(0, MAX_VISIBLE_COLUMNS);

    return visibleFields.map((field) => ({
      id: field.name,
      name: field.name,
      label: field.label || field.name,
      sortable: true,
      searchable: field.searchable,
      accessorKey: field.name,
    }));
  }, [entity?.fields]);

  // -- 12. Event handlers (URL search param mutations) ------------------------

  /**
   * Handles page change from DataTable pagination controls.
   * Updates the URL 'page' search param for bookmarkability.
   */
  const handlePageChange = useCallback(
    (newPage: number) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('page', String(newPage));
        return next;
      });
    },
    [setSearchParams],
  );

  /**
   * Handles page size change from DataTable pagination controls.
   * Updates URL and resets to page 1 on size change.
   */
  const handlePageSizeChange = useCallback(
    (newSize: number) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('pageSize', String(newSize));
        next.set('page', '1'); // Reset to page 1 on size change
        return next;
      });
    },
    [setSearchParams],
  );

  /**
   * Handles sort change from DataTable column header clicks.
   * Updates sortBy/sortOrder URL params and resets page to 1.
   */
  const handleSortChange = useCallback(
    (newSortBy: string, newSortOrder: 'asc' | 'desc') => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('sortBy', newSortBy);
        next.set('sortOrder', newSortOrder);
        next.set('page', '1'); // Reset to page 1 on sort change
        return next;
      });
    },
    [setSearchParams],
  );

  // -- 13. Action column for row navigation -----------------------------------
  // DataTable does not have an onRowClick prop, so we add a custom action
  // column with a link/button for navigating to record details.

  const actionColumn: DataTableColumn<EntityRecord> = useMemo(
    () => ({
      id: '__actions',
      label: '',
      sortable: false,
      searchable: false,
      width: '80px',
      cell: (_value: unknown, record: EntityRecord) => {
        const recordId = record.id as string | undefined;
        if (!recordId) return null;
        return (
          <Link
            to={isStandalone ? `/records/${standaloneEntityName}/${recordId}` : `/${appName}/${areaName}/${nodeName}/r/${recordId}`}
            className="inline-flex items-center rounded px-2 py-1 text-xs font-medium text-blue-600 transition-colors duration-150 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            aria-label={`View record ${recordId}`}
          >
            <svg
              className="me-1 size-3.5"
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
            View
          </Link>
        );
      },
    }),
    [appName, areaName, nodeName, isStandalone, standaloneEntityName],
  );

  // Merge entity field columns + action column
  const allColumns = useMemo(
    () => [...columns, actionColumn],
    [columns, actionColumn],
  );

  // -- 14. Compose the record data for the DataTable --------------------------
  // useRecords returns QueryResult { fieldsMeta, data } — extract data array.
  // We also construct an EntityRecordList (records[] + totalCount) for
  // type-safe pagination state that mirrors the monolith's DataModel shape.

  const records: EntityRecord[] = useMemo(
    () => recordsData?.data ?? [],
    [recordsData],
  );

  const recordList: EntityRecordList = useMemo(
    () => ({
      records,
      totalCount: totalRecordCount ?? 0,
    }),
    [records, totalRecordCount],
  );

  // -- 15. Page body check (mirrors Razor View body rendering) ----------------
  // The monolith's RecordList.cshtml iterates page.Body nodes and invokes
  // ViewComponents. In a typical record list page, the body contains PcGrid.
  // Access PageBodyNode[] from ErpPage.body to check for dynamic rendering.

  const bodyNodes: PageBodyNode[] = page?.body ?? [];
  const hasPageBody = bodyNodes.length > 0;

  // -- 16. Derived display values ---------------------------------------------
  // Uses Entity.label / Entity.labelPlural / Entity.iconName / Entity.color
  // and ErpPage.label / ErpPage.name / ErpPage.type / ErpPage.entityId
  // from the resolved context.

  const pageTitle =
    page?.label || entity?.labelPlural || entity?.label || 'Records';

  const entityColor = entity?.color || '#1d4ed8';
  const entityIcon = entity?.iconName || '';

  // -- 17. Render states ------------------------------------------------------

  // Loading state: waiting for page context resolution.
  // Only show loading spinner when we're actually fetching page context
  // (urlInfo is defined). For standalone /records/:entityName routes
  // the page query is disabled so isPageLoading is always false anyway.
  if (isPageLoading && urlInfo) {
    return (
      <div
        className="flex min-h-[200px] items-center justify-center"
        role="status"
        aria-label="Loading page"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block size-8 animate-spin rounded-full border-4 border-current border-e-transparent text-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading page…</span>
        </div>
      </div>
    );
  }

  // Not found state: page context could not be resolved
  // Mirrors RecordList.cshtml.cs: if (ErpRequestContext.Page == null) return NotFound()
  // For standalone /records/:entityName routes (no app/area/node in URL),
  // urlInfo will be undefined and page will be null — that's expected.
  // Only show "Page Not Found" when we actually attempted page resolution
  // (i.e. urlInfo was provided) but the server returned no page.
  if (!page && urlInfo) {
    return (
      <div
        className="flex min-h-[200px] flex-col items-center justify-center gap-2 text-gray-500"
        role="alert"
      >
        <svg
          className="size-12 text-gray-300"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={1.5}
            d="M9.75 9.75l4.5 4.5m0-4.5l-4.5 4.5M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
          />
        </svg>
        <h2 className="text-lg font-semibold text-gray-700">
          Page Not Found
        </h2>
        <p className="text-sm">
          No current page found! The requested record list page could not be
          resolved.
        </p>
      </div>
    );
  }

  // -- 18. Render page --------------------------------------------------------

  return (
    <div className="record-list-page">
      {/* Page header with title and Create New button */}
      <div className="mb-6 flex items-center justify-between">
        <div className="flex items-center gap-3">
          {entityIcon && (
            <span
              className="flex size-8 items-center justify-center rounded-md text-white"
              style={{ backgroundColor: entityColor }}
              aria-hidden="true"
            >
              <i className={`fa fa-${entityIcon} text-sm`} />
            </span>
          )}
          <h1 className="text-2xl font-semibold text-gray-900">
            {pageTitle}
          </h1>
        </div>

        {/* Create new record button — navigates to the record creation page.
            Uses navigate({ flushSync: true }) so React Router wraps the
            DOM update in ReactDOM.flushSync, committing the destination
            component's DOM synchronously before Playwright's waitForURL
            resolves.  This eliminates the ~50ms gap between URL change
            and input rendering caused by React Router's default
            startTransition-wrapped navigation. */}
        <a
          href={isStandalone ? `/records/${standaloneEntityName}/create` : `/${appName}/${areaName}/${nodeName}/c/`}
          role="link"
          data-testid="create-record-btn"
          onClick={(e) => {
            e.preventDefault();
            // React Router wraps navigations in startTransition, deferring
            // DOM commits.  Calling flushSync immediately after navigate
            // forces React to process the pending transition synchronously,
            // ensuring the destination component's DOM (form inputs) exists
            // before Playwright's page.waitForURL resolves.
            navigate(
              isStandalone
                ? `/records/${standaloneEntityName}/create`
                : `/${appName}/${areaName}/${nodeName}/c/`,
              {
                state: { entity },
                flushSync: true,
              } as Parameters<typeof navigate>[1],
            );
          }}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-150 hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          <svg
            className="size-4"
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
          Create New
        </a>
      </div>

      {/* Validation / error message display */}
      {validationMessage && (
        <div
          className="mb-4 rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          <div className="flex items-start gap-2">
            <svg
              className="mt-0.5 size-4 shrink-0"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
              />
            </svg>
            <span>{validationMessage}</span>
          </div>
        </div>
      )}

      {/* Entity metadata loading indicator */}
      {isEntityLoading && (
        <div
          className="mb-4 flex items-center gap-2 text-sm text-gray-500"
          role="status"
          aria-label="Loading entity metadata"
        >
          <div
            className="inline-block size-4 animate-spin rounded-full border-2 border-current border-e-transparent"
            aria-hidden="true"
          />
          <span>Loading column definitions…</span>
        </div>
      )}

      {/* Dynamic page body or DataTable rendering */}
      {/* RecordList.cshtml iterates currentPage.Body nodes and invokes
          each root-level ViewComponent (typically PcGrid).
          In the SPA, the DataTable IS the grid component. If the page
          has body nodes attached, we note it; DataTable always renders. */}
      {!isEntityLoading && entity ? (
        <>
          {/* No page body nodes info — matches Razor view empty-body alert */}
          {!hasPageBody && (
            <div
              className="mb-4 rounded-md border border-blue-200 bg-blue-50 p-4 text-sm text-blue-700"
              role="status"
            >
              Page does not have page nodes attached
            </div>
          )}

          {/* Search bar for record filtering */}
          {entity && (
            <div className="mb-4">
              <label htmlFor="record-search" className="sr-only">
                Search {entity.labelPlural || entity.label || 'records'}
              </label>
              <div className="relative">
                <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
                  <svg
                    className="size-4 text-gray-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    aria-hidden="true"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                    />
                  </svg>
                </div>
                <input
                  id="record-search"
                  type="search"
                  className="block w-full rounded-md border border-gray-300 bg-white py-2 pe-4 ps-10 text-sm placeholder-gray-400 transition-colors focus-visible:border-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
                  placeholder={`Search ${entity.labelPlural || entity.label || 'records'}…`}
                  defaultValue={searchTerm}
                  onChange={(e) => {
                    const value = e.target.value;
                    setSearchParams((prev) => {
                      const next = new URLSearchParams(prev);
                      if (value) {
                        next.set('search', value);
                      } else {
                        next.delete('search');
                      }
                      next.set('page', '1');
                      return next;
                    });
                  }}
                />
              </div>
            </div>
          )}

          {/* DataTable — replaces PcGrid ViewComponent */}
          <DataTable<EntityRecord>
            data={recordList.records}
            columns={allColumns}
            totalCount={recordList.totalCount}
            pageSize={currentPageSize}
            loading={isRecordsLoading || isRecordsFetching || isCountLoading}
            onPageChange={handlePageChange}
            onPageSizeChange={handlePageSizeChange}
            onSortChange={handleSortChange}
            hover={true}
            striped={true}
            showHeader={true}
            showFooter={true}
            emptyText="No records found"
            responsiveBreakpoint="lg"
          />
        </>
      ) : !isEntityLoading && !entity && !isPageLoading ? (
        /* Entity not found — edge case where page references a deleted entity */
        <div
          className="rounded-md border border-yellow-200 bg-yellow-50 p-4 text-sm text-yellow-800"
          role="alert"
        >
          <strong>Warning:</strong> Entity metadata could not be loaded for
          this page. The entity may have been deleted or is not accessible.
        </div>
      ) : null}
    </div>
  );
}
