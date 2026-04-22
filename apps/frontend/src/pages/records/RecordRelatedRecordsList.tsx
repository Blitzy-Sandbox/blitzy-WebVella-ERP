/**
 * RecordRelatedRecordsList — Related Records List Page
 *
 * React page component replacing
 * `WebVella.Erp.Web/Pages/RecordRelatedRecordsList.cshtml` /
 * `RecordRelatedRecordsList.cshtml.cs`
 * (`RecordRelatedRecordsListPageModel`).
 *
 * Displays a data grid (DataTable) of records related to a parent record
 * via a specific entity relation, with server-side sorting, URL-based
 * pagination, and row-click navigation to related record details.
 *
 * Route: /:appName/:areaName/:nodeName/r/:recordId/rl/:relationId/l/:pageName?
 *
 * Lifecycle (mirrors monolith OnGet):
 *   1. Resolve page context via usePageByUrl  (replaces Init())
 *   2. Canonical redirect if pageName ≠ resolved page.name
 *   3. Fetch app context via useApp for navigation store sync
 *   4. Fetch relation metadata via useRelation
 *   5. Determine related entity from relation direction
 *   6. Fetch entity metadata for DataTable column definitions via useEntity
 *   7. Fetch parent record via useRecord for breadcrumb/context display
 *   8. Read pagination/sort state from URL search params
 *   9. Fetch related records via useRelatedRecords with pagination params
 *  10. Render DataTable with columns derived from entity fields
 *
 * Monolith behaviour preserved:
 *   - Init()              → usePageByUrl (page context resolution)
 *   - Canonical redirect    when PageName ≠ ErpRequestContext.Page.Name
 *   - IPageHook global      → no client equivalent (server-side hooks)
 *   - BeforeRender()        → entity metadata + related records data fetching
 *   - Page body rendering   → PageBodyNodeList + DataTable
 *   - Relation-scoped listing → records filtered by parent record ID + relation
 *
 * Key differences from the monolith:
 *   - No antiforgery tokens — JWT Bearer auth
 *   - IRecordRelatedRecordsListPageHook.OnGet/OnPost → API-level operations
 *   - Pre/post hooks → API-level validation + SNS domain events
 *   - Page body ViewComponent loop → PageBodyNodeList + DataTable
 *   - OnPost validation error handling → client-side error state
 *
 * @module pages/records/RecordRelatedRecordsList
 */

import { useState, useEffect, useMemo, useCallback } from 'react';
import { useParams, useNavigate, useSearchParams, Link } from 'react-router-dom';

// Internal hooks
import { useRelatedRecords, useRecord } from '../../hooks/useRecords';
import { useEntity, useRelation } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';
import { useApp } from '../../hooks/useApps';

// Components
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';

// Domain type imports
import type { Entity, EntityRelation } from '../../types/entity';
import type { EntityRecord, EntityRecordList } from '../../types/record';
import type { ErpPage, PageBodyNode } from '../../types/page';
import type { ErrorModel, UrlInfo } from '../../types/common';

// State store
import { useAppStore } from '../../stores/appStore';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default page size for related records list pagination. */
const DEFAULT_PAGE_SIZE = 10;

/** Maximum number of entity fields to show as grid columns. */
const MAX_VISIBLE_COLUMNS = 15;

/**
 * System-managed fields that should not appear as grid columns.
 * Internal metadata fields not useful for end-user display.
 */
const HIDDEN_SYSTEM_FIELDS = new Set<string>([
  'id',
  'created_on',
  'created_by',
  'last_modified_on',
  'last_modified_by',
]);

// ---------------------------------------------------------------------------
// Route Parameter Types
// ---------------------------------------------------------------------------

/**
 * Route parameters expected from React Router for
 * /:appName/:areaName/:nodeName/r/:recordId/rl/:relationId/l/:pageName?
 *
 * Mirrors the Razor route:
 *   {AppName}/{AreaName}/{NodeName}/r/{ParentRecordId}/rl/{RelationId}/l/{PageName?}
 *
 * React Router v7 types useParams with Record<string, string | undefined>,
 * so we use a type alias that satisfies the constraint while preserving
 * the named parameter documentation.
 */
type RecordRelatedRecordsListRouteParams = {
  /** Application slug (e.g. "crm"). */
  appName?: string;
  /** Sitemap area slug. */
  areaName?: string;
  /** Sitemap node slug. */
  nodeName?: string;
  /** Parent record GUID — the owning side of the relation. */
  recordId?: string;
  /** Relation GUID linking parent and related records. */
  relationId?: string;
  /** Optional page name slug for canonical URL matching. */
  pageName?: string;
  /** Index signature required by React Router v7 useParams generic. */
  [key: string]: string | undefined;
};

// ---------------------------------------------------------------------------
// Utility Functions
// ---------------------------------------------------------------------------

/**
 * Converts a raw record field value to a human-readable display string.
 * Handles null, undefined, booleans, dates, arrays, and plain objects
 * gracefully — never renders "null" or "undefined" as visible text.
 */
function formatFieldValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (value instanceof Date) return value.toLocaleString();
  if (Array.isArray(value)) return value.map(formatFieldValue).join(', ');
  if (typeof value === 'object') {
    try {
      return JSON.stringify(value);
    } catch {
      return '[Object]';
    }
  }
  return String(value);
}

/**
 * Extracts structured validation errors from a mutation/query error.
 * Supports both standard Error instances and ApiError objects with
 * `.errors[]` arrays produced by the API response interceptor.
 */
function extractApiErrors(
  error: unknown,
): { message: string; errors: ErrorModel[] } {
  if (!error) return { message: 'An unexpected error occurred.', errors: [] };
  const errObj = error as Record<string, unknown>;
  const fallback =
    typeof errObj.message === 'string'
      ? errObj.message
      : 'An unexpected error occurred.';
  const apiErr = error as { message?: string; errors?: ErrorModel[] };
  if (apiErr.errors && Array.isArray(apiErr.errors)) {
    return {
      message: apiErr.message ?? fallback,
      errors: apiErr.errors.map((e) => ({
        key: e.key ?? '',
        value: e.value ?? '',
        message: e.message ?? '',
      })),
    };
  }
  return { message: fallback, errors: [] };
}

/**
 * Determines which entity's records to display based on relation metadata
 * and the parent record's entity context.
 *
 * In the monolith, the relation direction was resolved in BeforeRender():
 *   - If the parent entity is the relation's origin → show target entity records
 *   - If the parent entity is the relation's target → show origin entity records
 *
 * The `pageEntityId` is the entity associated with the page (from ErpPage.entityId),
 * which corresponds to the parent record's entity. We compare this against the
 * relation's origin/target entity IDs to determine direction.
 *
 * @param relation  - Relation metadata
 * @param pageEntityId - Entity ID from the resolved page context
 * @returns The entity name of the related entity whose records should be fetched
 */
function resolveRelatedEntityName(
  relation: EntityRelation | undefined,
  pageEntityId: string | null | undefined,
): string {
  if (!relation || !pageEntityId) return '';

  // If the page entity matches the origin entity, display target entity records
  if (relation.originEntityId === pageEntityId) {
    return relation.targetEntityName;
  }
  // If the page entity matches the target entity, display origin entity records
  if (relation.targetEntityId === pageEntityId) {
    return relation.originEntityName;
  }
  // Fallback: use the target entity name
  return relation.targetEntityName;
}

// ---------------------------------------------------------------------------
// Local Components
// ---------------------------------------------------------------------------

/**
 * Recursively renders page body nodes in a minimal structural layout.
 *
 * Each body node carries a `componentName` (e.g. "PcRow", "PcSection")
 * and nested `nodes` forming an arbitrary-depth layout tree. This renderer
 * preserves the tree structure so that a full DynamicPageRenderer can
 * progressively enhance individual component types without breaking
 * the structural contract.
 *
 * Matches the monolith's RecordRelatedRecordsList.cshtml body rendering
 * loop (lines 14-27) which iterates over `currentPage.Body` root nodes
 * and invokes the associated ViewComponent for each.
 */
function PageBodyNodeList({
  nodes,
}: {
  nodes: PageBodyNode[];
}): React.JSX.Element | null {
  if (!nodes || nodes.length === 0) return null;
  return (
    <div className="space-y-4">
      {nodes.map((node) => (
        <div
          key={node.id}
          className="page-body-node"
          data-component={node.componentName}
          data-node-id={node.id}
          data-container-id={node.containerId}
        >
          {node.nodes && node.nodes.length > 0 && (
            <PageBodyNodeList nodes={node.nodes} />
          )}
        </div>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// RecordRelatedRecordsList Component
// ---------------------------------------------------------------------------

/**
 * Related records list page component.
 *
 * Replaces RecordRelatedRecordsListPageModel from the ASP.NET monolith.
 * Resolves page context from URL parameters, fetches relation/entity
 * metadata and related records data, renders a DataTable grid with sorting
 * and pagination, and handles row-click navigation to related record
 * details.
 *
 * Default exported for React.lazy() code-splitting.
 */
export default function RecordRelatedRecordsList(): React.JSX.Element {
  // -- Route parameters & navigation ------------------------------------------

  const params = useParams<RecordRelatedRecordsListRouteParams>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // Safely extract typed route params (React Router 7 returns string | undefined)
  const appName = params.appName ?? '';
  const areaName = params.areaName ?? '';
  const nodeName = params.nodeName ?? '';
  const recordId = params.recordId ?? '';
  const relationId = params.relationId ?? '';
  const pageName = params.pageName ?? '';

  // -- Local state ------------------------------------------------------------
  // Mirrors monolith's Validation.Message and Validation.Errors patterns
  // from RecordRelatedRecordsList.cshtml.cs OnPost error handling (lines 80-93).

  const [validationMessage, setValidationMessage] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<ErrorModel[]>([]);

  // -- Global navigation store ------------------------------------------------

  const setCurrentPage = useAppStore((state) => state.setCurrentPage);
  const setRouteParams = useAppStore((state) => state.setRouteParams);
  const updateNavigationContext = useAppStore(
    (state) => state.updateNavigationContext,
  );
  const currentApp = useAppStore((state) => state.currentApp);

  // -- 1. Resolve page context (replaces Init() → ErpRequestContext) ----------
  // Builds UrlInfo for usePageByUrl which calls POST /pages/resolve.
  // pageType for related-record list pages isn't a distinct PageType enum value
  // in the monolith; it resolves from the URL pattern. We pass RecordList (3)
  // since related record list pages share the entity list page type and the
  // server distinguishes by the hasRelation flag + relation context.

  const urlInfo: UrlInfo | undefined = useMemo(
    () =>
      appName && areaName && nodeName
        ? {
            hasRelation: true,
            pageType: 3 as number, // PageType.RecordList — related list uses the same page type
            appName,
            areaName,
            nodeName,
            pageName,
            recordId: recordId || null,
            relationId: relationId || null,
            parentRecordId: recordId || null,
          }
        : undefined,
    [appName, areaName, nodeName, pageName, recordId, relationId],
  );

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
  // Mirrors RecordRelatedRecordsList.cshtml.cs lines 23-29:
  // if (PageName != ErpRequestContext.Page.Name)
  //   return Redirect($"/{App}/{Area}/{Node}/r/{ParentRecordId}/rl/{RelationId}/l/{Page.Name}{queryString}");

  useEffect(() => {
    if (page && pageName && pageName !== page.name) {
      const queryString = searchParams.toString();
      const canonicalPath = `/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/l/${page.name}`;
      navigate(
        queryString ? `${canonicalPath}?${queryString}` : canonicalPath,
        { replace: true },
      );
    }
  }, [
    page,
    pageName,
    appName,
    areaName,
    nodeName,
    recordId,
    relationId,
    searchParams,
    navigate,
  ]);

  // -- 4. Sync navigation context ---------------------------------------------
  // Replaces BaseErpPageModel.Init() which populated ErpRequestContext
  // used by _AppMaster.cshtml layout components (Sidebar, TopNav, Breadcrumb)

  useEffect(() => {
    if (appName || areaName || nodeName || pageName || recordId || relationId) {
      setRouteParams({
        appName,
        areaName,
        nodeName,
        pageName: pageName || undefined,
        recordId: recordId || null,
        relationId: relationId || null,
        parentRecordId: recordId || null,
      });
    }
  }, [appName, areaName, nodeName, pageName, recordId, relationId, setRouteParams]);

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

  // -- 5. Fetch relation metadata ---------------------------------------------
  // The relation ID from the URL determines the linked entities.
  // useRelation returns EntityRelation directly (hook unwraps ApiResponse).

  const { data: relation } = useRelation(relationId || '');

  // -- 6. Determine the related entity ----------------------------------------
  // From the relation's origin/target entity names, determine which entity's
  // records to display. The page's entityId identifies the parent record's
  // entity; the "other" side of the relation is the related entity.

  const relatedEntityName = useMemo(
    () => resolveRelatedEntityName(relation, page?.entityId),
    [relation, page?.entityId],
  );

  // -- 7. Fetch entity metadata for grid column definitions -------------------
  // Uses relatedEntityName to load the related entity's field definitions,
  // which drive the DataTable column configuration.

  const { data: relatedEntity, isLoading: isEntityLoading } =
    useEntity(relatedEntityName);

  // Also fetch the parent entity metadata for breadcrumb/context display
  const parentEntityIdOrName = page?.entityId ?? '';
  const { data: parentEntity } = useEntity(parentEntityIdOrName);

  // -- 8. Fetch parent record for breadcrumb/context display ------------------
  // useRecord needs (entityName, id). Parent record provides navigation
  // context (e.g. "Contact: John Doe → Related Accounts").

  const parentEntityName = parentEntity?.name ?? '';
  const { data: parentRecord } = useRecord(
    parentEntityName,
    recordId,
  );

  // -- 9. Pagination / sort state from URL search params ----------------------
  // URL search params enable bookmarkable/shareable listing state.
  // Mirrors monolith's EQL PAGE/PAGESIZE query parameter handling.

  const currentPageNum = useMemo(() => {
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

  // -- 10. Build useRelatedRecords parameters ---------------------------------
  // useRelatedRecords(entityName, recordId, relationName, params?)
  // Converts URL-based page/pageSize to skip/limit for the API.

  const relatedRecordsParams = useMemo(
    () => ({
      fields: '*',
      skip: (currentPageNum - 1) * currentPageSize,
      limit: currentPageSize,
    }),
    [currentPageNum, currentPageSize],
  );

  const relationName = relation?.name ?? '';

  // -- 11. Fetch related records (replaces DataModel population) --------------
  // useRelatedRecords returns EntityRecordList { records[], totalCount }.

  const {
    data: relatedRecordsResponse,
    isLoading: isRecordsLoading,
    isFetching: isRecordsFetching,
    isError: isRecordsError,
    error: recordsError,
  } = useRelatedRecords(
    parentEntityName,
    recordId,
    relationName,
    relatedRecordsParams,
  );

  // useRelatedRecords returns EntityRecordList directly (hook unwraps ApiResponse)
  const relatedRecordList: EntityRecordList | undefined =
    relatedRecordsResponse ?? undefined;

  // Surface fetch errors to the validation message display
  useEffect(() => {
    if (isRecordsError && recordsError) {
      const { message, errors } = extractApiErrors(recordsError);
      setValidationMessage(message);
      setValidationErrors(errors);
    } else if (!isRecordsError && validationMessage) {
      // Clear error message when the query succeeds on retry
      setValidationMessage('');
      setValidationErrors([]);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isRecordsError, recordsError]);

  // -- 12. Build DataTable columns from entity fields -------------------------
  // Transforms Entity.fields[] (Field[]) into DataTableColumn[] for the grid.
  // Mirrors how PcGrid ViewComponent read field definitions from DataModel
  // to configure its column headers.
  // Uses Entity.name, Entity.label, Entity.labelPlural, Entity.fields,
  // Entity.iconName from the resolved related entity metadata.

  const columns: DataTableColumn<EntityRecord>[] = useMemo(() => {
    const entityFields = relatedEntity?.fields ?? [];
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
      cell: (value: unknown) => {
        const displayValue = formatFieldValue(value);
        return (
          <span
            title={displayValue}
            style={{ overflowWrap: 'break-word' }}
          >
            {displayValue || '\u2014'}
          </span>
        );
      },
    }));
  }, [relatedEntity?.fields]);

  // -- 13. Action column for row navigation -----------------------------------
  // Adds a "View" link column that navigates to the related record details page.
  // Route: /:appName/:areaName/:nodeName/r/:recordId/rl/:relationId/r/:relatedRecordId

  const actionColumn: DataTableColumn<EntityRecord> = useMemo(
    () => ({
      id: '__actions',
      label: '',
      sortable: false,
      searchable: false,
      width: '80px',
      cell: (_value: unknown, record: EntityRecord) => {
        const relatedRecordId = record.id as string | undefined;
        if (!relatedRecordId) return null;
        return (
          <Link
            to={`/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/r/${relatedRecordId}`}
            className="inline-flex items-center rounded px-2 py-1 text-xs font-medium text-blue-600 transition-colors duration-150 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            aria-label={`View related record ${relatedRecordId}`}
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
    [appName, areaName, nodeName, recordId, relationId],
  );

  // Merge entity field columns + action column
  const allColumns = useMemo(
    () => [...columns, actionColumn],
    [columns, actionColumn],
  );

  // -- 14. Compose record data for the DataTable ------------------------------
  // useRelatedRecords returns EntityRecordList (records[] + totalCount).

  const records: EntityRecord[] = useMemo(
    () => relatedRecordList?.records ?? [],
    [relatedRecordList],
  );

  const totalCount = relatedRecordList?.totalCount ?? 0;

  // -- 15. Page body check (mirrors Razor View body rendering) ----------------
  // The monolith's RecordRelatedRecordsList.cshtml iterates page.Body nodes
  // and invokes ViewComponents. Access PageBodyNode[] from ErpPage.body.

  const bodyNodes: PageBodyNode[] = page?.body ?? [];
  const hasPageBody = bodyNodes.length > 0;

  // -- 16. Derived display values ---------------------------------------------
  // Uses Entity.label / Entity.labelPlural / Entity.iconName / Entity.color,
  // EntityRelation.name / EntityRelation.relationType / originEntityName /
  // targetEntityName, and ErpPage.label / ErpPage.name / ErpPage.body /
  // ErpPage.entityId from the resolved contexts.

  const pageTitle =
    page?.label ||
    (relatedEntity
      ? `Related ${relatedEntity.labelPlural || relatedEntity.label}`
      : 'Related Records');

  const entityColor = relatedEntity?.color || '#1d4ed8';
  const entityIcon = relatedEntity?.iconName || '';
  const parentRecordLabel = parentRecord
    ? formatFieldValue(
        parentRecord['label'] ??
          parentRecord['name'] ??
          parentRecord['id'],
      )
    : recordId;

  // -- 17. Event handlers (URL search param mutations) ------------------------

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

  // -- 18. Render states ------------------------------------------------------

  // Loading state: waiting for page context resolution
  if (isPageLoading) {
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
  // Mirrors RecordRelatedRecordsList.cshtml: "No current page found!" danger alert
  if (!page) {
    return (
      <div
        className="flex min-h-[200px] flex-col items-center justify-center gap-2 text-gray-500"
        role="alert"
      >
        <svg
          className="size-12 text-red-300"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={1.5}
            d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126zM12 15.75h.007v.008H12v-.008z"
          />
        </svg>
        <h2 className="text-lg font-semibold text-red-700">
          No current page found!
        </h2>
        <p className="text-sm text-gray-600">
          The requested related records list page could not be resolved.
        </p>
        {recordId && (
          <Link
            to={`/${appName}/${areaName}/${nodeName}/r/${recordId}`}
            className="mt-2 inline-flex items-center gap-1 rounded-md bg-gray-100 px-3 py-1.5 text-sm font-medium text-gray-700 transition-colors hover:bg-gray-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
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
                d="M10 19l-7-7m0 0l7-7m-7 7h18"
              />
            </svg>
            Back to record
          </Link>
        )}
      </div>
    );
  }

  // -- 19. Render page --------------------------------------------------------

  return (
    <div className="record-related-records-list-page">
      {/* Breadcrumb-style context: Back to Parent Record + Relation Name */}
      <nav aria-label="Record navigation" className="mb-4">
        <div className="flex items-center gap-2 text-sm text-gray-500">
          <Link
            to={`/${appName}/${areaName}/${nodeName}/r/${recordId}`}
            className="inline-flex items-center gap-1 text-blue-600 transition-colors hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
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
                d="M10 19l-7-7m0 0l7-7m-7 7h18"
              />
            </svg>
            {parentEntity?.label || 'Record'}
            {parentRecordLabel ? `: ${parentRecordLabel}` : ''}
          </Link>
          <span aria-hidden="true">/</span>
          <span className="font-medium text-gray-700">
            {relation?.label || relation?.name || 'Related Records'}
          </span>
        </div>
      </nav>

      {/* Page header with title and Create Related button */}
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

        {/* Create related record button — links to related record creation page */}
        <Link
          to={`/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/c/`}
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
        </Link>
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
            <div>
              <span>{validationMessage}</span>
              {/* Per-field validation errors — matches ErrorModel.key + ErrorModel.message */}
              {validationErrors.length > 0 && (
                <ul className="mt-2 list-disc ps-5">
                  {validationErrors.map((err, idx) => (
                    <li key={err.key || idx}>
                      {err.key && (
                        <strong className="font-medium">{err.key}: </strong>
                      )}
                      {err.message}
                    </li>
                  ))}
                </ul>
              )}
            </div>
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
      {!isEntityLoading && relatedEntity ? (
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

          {/* If page has body nodes, render the structural tree above DataTable */}
          {hasPageBody && (
            <PageBodyNodeList nodes={bodyNodes} />
          )}

          {/* DataTable — replaces PcGrid ViewComponent for related records */}
          <DataTable<EntityRecord>
            data={records}
            columns={allColumns}
            totalCount={totalCount}
            pageSize={currentPageSize}
            currentPage={currentPageNum}
            loading={isRecordsLoading || isRecordsFetching}
            onPageChange={handlePageChange}
            onPageSizeChange={handlePageSizeChange}
            onSortChange={handleSortChange}
            hover
            striped
            showHeader
            showFooter
            emptyText={`No related ${relatedEntity.labelPlural || relatedEntity.label || 'records'} found`}
            responsiveBreakpoint="lg"
          />
        </>
      ) : !isEntityLoading && !relatedEntity && !isPageLoading ? (
        /* Entity not found — edge case where relation references a deleted entity */
        <div
          className="rounded-md border border-yellow-200 bg-yellow-50 p-4 text-sm text-yellow-800"
          role="alert"
        >
          <strong>Warning:</strong> Related entity metadata could not be loaded
          for this page. The entity may have been deleted or is not accessible.
        </div>
      ) : null}
    </div>
  );
}
