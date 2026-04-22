/**
 * ApplicationPages — Application Pages Tab
 *
 * React page component replacing the monolith's SDK plugin page:
 *   - `WebVella.Erp.Plugins.SDK/Pages/application/pages.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/application/pages.cshtml.cs`
 *
 * Route: `/admin/applications/:appId/pages`
 *
 * Displays a bordered, hoverable data table listing all home pages
 * associated with a specific application, ordered by weight. Each row
 * shows an action link (opens page details in a new tab), the page
 * weight, and the page name — mirroring the 3-column `wv-grid` from
 * the Razor source.
 *
 * Sub-navigation tabs (Details, Manage, Pages, Sitemap) replicate the
 * monolith's `AdminPageUtils.GetAppAdminSubNav(App, "pages")` toolbar.
 *
 * The "Create Page" header action links to the page-creation form with
 * application context (AppId + Type query parameters), preserving the
 * `returnUrl` pattern from the original `pages.cshtml.cs` PageInit().
 *
 * @module pages/admin/ApplicationPages
 */

import { useMemo, useState, useCallback } from 'react';
import { useParams, Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';

import { get, type ApiResponse } from '../../api/client';
import {
  DataTable,
  type DataTableColumn,
} from '../../components/data-table/DataTable';
import TabNav, { type TabConfig } from '../../components/common/TabNav';
import type { App } from '../../types/app';
import type { ErpPage } from '../../types/page';

// ---------------------------------------------------------------------------
// Local types
// ---------------------------------------------------------------------------

/**
 * Intersection type that satisfies the DataTable generic constraint
 * (`T extends Record<string, unknown>`) while preserving typed access
 * to all ErpPage properties in cell renderers and column accessors.
 *
 * The DataTable requires an index-signature-compatible type; ErpPage
 * as a plain interface does not carry one. This intersection bridges
 * the two without losing type safety on `id`, `weight`, or `name`.
 */
type PageRecord = ErpPage & Record<string, unknown>;

// ---------------------------------------------------------------------------
// Inline SVG icons (monochrome, fill="currentColor" per UI7)
// ---------------------------------------------------------------------------

/**
 * Eye icon rendered in the action column — replaces
 * `<span class="fa fa-eye"></span>` from pages.cshtml line 30.
 */
function EyeIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 576 512"
      fill="currentColor"
      className="h-4 w-4"
      aria-hidden="true"
    >
      <path d="M288 80c-65.2 0-118.8 29.6-159.9 67.7C89.6 183.5 63 226 49.4 256c13.6 30 40.2 72.5 78.6 108.3C169.2 402.4 222.8 432 288 432s118.8-29.6 159.9-67.7c38.4-35.8 65-78.3 78.6-108.3-13.6-30-40.2-72.5-78.6-108.3C406.8 109.6 353.2 80 288 80zM95.4 112.6C142.5 68.8 207.2 32 288 32s145.5 36.8 192.6 80.6c46.8 43.5 78.1 95.4 93 131.1 3.3 7.9 3.3 16.7 0 24.6-14.9 35.7-46.2 87.7-93 131.1C433.5 443.2 368.8 480 288 480s-145.5-36.8-192.6-80.6C48.6 355.9 17.3 303.9 2.4 268.3c-3.3-7.9-3.3-16.7 0-24.6 14.9-35.7 46.2-87.7 93-131.1zM288 192a64 64 0 1 1 0 128 64 64 0 1 1 0-128zm0 176a112 112 0 1 0 0-224 112 112 0 1 0 0 224z" />
    </svg>
  );
}

/**
 * Plus icon rendered in the "Create Page" action button — replaces
 * `<i class='fa fa-plus go-green'></i>` from pages.cshtml.cs line 47.
 */
function PlusIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 448 512"
      fill="currentColor"
      className="h-3.5 w-3.5 text-green-600"
      aria-hidden="true"
    >
      <path d="M256 80c0-17.7-14.3-32-32-32s-32 14.3-32 32v144H48c-17.7 0-32 14.3-32 32s14.3 32 32 32h144v144c0 17.7 14.3 32 32 32s32-14.3 32-32V288h144c17.7 0 32-14.3 32-32s-14.3-32-32-32H256V80z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * **ApplicationPages** — Lists pages associated with an application.
 *
 * Replaces the `PagesModel` Razor page model from pages.cshtml.cs:
 *
 * - `OnGet()` lifecycle          → `useQuery` with `get<App>()`
 * - `AppService.GetApplication()`→ `GET /v1/apps/{appId}` API call
 * - `.OrderBy(x => x.Weight)`    → `useMemo` sort on `homePages`
 * - `WvGridColumnMeta` list      → `DataTableColumn<PageRecord>[]` via `useMemo`
 * - `AdminPageUtils.GetAppAdminSubNav(App, "pages")` → `TabNav` with `TabConfig[]`
 * - `HttpUtility.UrlEncode(CurrentUrl)` → `useState` with `encodeURIComponent`
 *
 * Default export enables lazy loading via `React.lazy()` for route-level
 * code splitting.
 */
export default function ApplicationPages(): React.ReactNode {
  // ── Route parameter extraction ──────────────────────────────────────
  // Replaces Razor @page "{RecordId}" route parameter.
  const { appId } = useParams<{ appId: string }>();

  // ── Return URL state ────────────────────────────────────────────────
  // Computed once on mount. Replaces C# HttpUtility.UrlEncode(CurrentUrl)
  // used in the "Create Page" header action link (pages.cshtml.cs line 47).
  const [returnUrl] = useState<string>(() =>
    encodeURIComponent(window.location.pathname + window.location.search),
  );

  // ── Application data fetch ──────────────────────────────────────────
  // Replaces the synchronous OnGet() → AppService.GetApplication(RecordId)
  // call from pages.cshtml.cs lines 58–69. TanStack Query provides
  // automatic caching, refetching, and loading/error states.
  const {
    data: response,
    isLoading,
    isError,
    error,
  } = useQuery<ApiResponse<App>>({
    queryKey: ['app', appId],
    queryFn: () => get<App>(`/apps/${appId}`),
    enabled: Boolean(appId),
  });

  const app = response?.object;

  // ── Sorted pages ────────────────────────────────────────────────────
  // Replaces C# `.HomePages.OrderBy(x => x.Weight).ToList()` from
  // pages.cshtml.cs line 69. Stable sort preserves insertion order for
  // equal weights.
  const sortedPages = useMemo<PageRecord[]>(() => {
    if (!app?.homePages) {
      return [];
    }
    // Cast is safe — ErpPage objects are plain JSON from the API and
    // always carry string-keyed properties at runtime.
    return [...app.homePages].sort(
      (a, b) => a.weight - b.weight,
    ) as PageRecord[];
  }, [app?.homePages]);

  // ── Tab change handler ──────────────────────────────────────────────
  // Navigates to the selected admin sub-section. Replaces the monolith's
  // `<a>` tag navigation from AdminPageUtils.GetAppAdminSubNav().
  const handleTabChange = useCallback(
    (tabId: string): void => {
      if (tabId !== 'pages' && appId) {
        window.location.href = `/admin/applications/${appId}/${tabId}`;
      }
    },
    [appId],
  );

  // ── Sub-navigation tab configuration ────────────────────────────────
  // Replaces C# AdminPageUtils.GetAppAdminSubNav(App, "pages") from
  // pages.cshtml.cs line 52 which generated 4 nav-link HTML strings
  // for Details, Manage, Pages (active), and Sitemap.
  const tabs = useMemo<TabConfig[]>(
    () => [
      { id: 'details', label: 'Details' },
      { id: 'manage', label: 'Manage' },
      { id: 'pages', label: 'Pages' },
      { id: 'sitemap', label: 'Sitemap' },
    ],
    [],
  );

  // ── DataTable column definitions ────────────────────────────────────
  // Replaces the C# WvGridColumnMeta list from pages.cshtml.cs lines
  // 71–87:
  //   action  → "" label, 50px width, link to page details (new tab)
  //   weight  → "weight" label, 80px width
  //   name    → "name" label, auto width
  const columns = useMemo<DataTableColumn<PageRecord>[]>(
    () => [
      {
        id: 'action',
        label: '',
        width: '50px',
        cell: (_value: unknown, record: PageRecord): React.ReactNode => (
          <Link
            to={`/admin/pages/${record.id}`}
            target="_blank"
            rel="noopener noreferrer"
            className={[
              'inline-flex items-center justify-center',
              'rounded border border-gray-300',
              'px-2 py-1 text-sm text-gray-600',
              'hover:bg-gray-50',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
            ].join(' ')}
            aria-label={`View page ${record.name}`}
          >
            <EyeIcon />
          </Link>
        ),
      },
      {
        id: 'weight',
        label: 'Weight',
        name: 'weight',
        width: '80px',
        accessorKey: 'weight',
      },
      {
        id: 'name',
        label: 'Name',
        name: 'name',
        accessorKey: 'name',
      },
    ],
    [],
  );

  // ── Loading state ───────────────────────────────────────────────────
  if (isLoading) {
    return (
      <div
        className="flex items-center justify-center gap-3 p-12"
        role="status"
      >
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent" />
        <span className="sr-only">Loading application pages…</span>
      </div>
    );
  }

  // ── Error state ─────────────────────────────────────────────────────
  if (isError) {
    const errorMessage =
      error instanceof Error
        ? error.message
        : 'An unexpected error occurred while loading the application.';

    return (
      <div
        className="rounded border border-red-300 bg-red-50 p-4 text-red-800"
        role="alert"
      >
        <p className="font-medium">Error loading application</p>
        <p className="mt-1 text-sm">{errorMessage}</p>
        <Link
          to="/admin/applications"
          className="mt-3 inline-flex items-center text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to applications
        </Link>
      </div>
    );
  }

  // ── Not found state ─────────────────────────────────────────────────
  // Replaces C# `if (App == null) return NotFound();` from
  // pages.cshtml.cs line 64.
  if (!app) {
    return (
      <div
        className="rounded border border-yellow-300 bg-yellow-50 p-4 text-yellow-800"
        role="alert"
      >
        <p className="font-medium">Application not found</p>
        <p className="mt-1 text-sm">
          The requested application could not be found.
        </p>
        <Link
          to="/admin/applications"
          className="mt-3 inline-flex items-center text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to applications
        </Link>
      </div>
    );
  }

  // ── Main render ─────────────────────────────────────────────────────
  return (
    <div className="space-y-6">
      {/* ── Page header ───────────────────────────────────────────────
       * Replaces <wv-page-header> from pages.cshtml lines 8–22.
       * color       → inline style on icon wrapper
       * area-label  → "Applications" breadcrumb text
       * title       → App.Label heading
       * subtitle    → "Pages"
       * icon-class  → App.IconClass rendered via <i> element
       * return-url  → Link to /admin/applications
       */}
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          {/* Application icon */}
          {app.iconClass && (
            <span
              className={`${app.iconClass} text-2xl`}
              style={app.color ? { color: app.color } : undefined}
              aria-hidden="true"
            />
          )}

          <div className="min-w-0">
            <p className="text-sm text-gray-500">
              <Link
                to="/admin/applications"
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Applications
              </Link>
            </p>
            <h1 className="truncate text-xl font-semibold text-gray-900">
              {app.label}
            </h1>
            <p className="text-sm text-gray-500">Pages</p>
          </div>
        </div>

        {/* ── Header actions ────────────────────────────────────────
         * Replaces C# HeaderActions from pages.cshtml.cs line 47:
         *   <a href='/sdk/objects/page/c/create?returnUrl=...
         *      &Type=Application&AppId=...'
         *      class='btn btn-white btn-sm'>
         *     <i class='fa fa-plus go-green'></i> CreatePage
         *   </a>
         */}
        <div className="flex shrink-0 items-center gap-2">
          <Link
            to={`/admin/pages/create?returnUrl=${returnUrl}&Type=Application&AppId=${app.id}`}
            className={[
              'inline-flex items-center gap-1.5',
              'rounded border border-gray-300 bg-white',
              'px-3 py-1.5 text-sm font-medium text-gray-700',
              'shadow-sm hover:bg-gray-50',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
            ].join(' ')}
          >
            <PlusIcon />
            Create Page
          </Link>
        </div>
      </header>

      {/* ── Sub-navigation tabs ─────────────────────────────────────
       * Replaces C# AdminPageUtils.GetAppAdminSubNav(App, "pages")
       * rendered in <wv-page-header-toolbar> from pages.cshtml
       * lines 17–22. The "pages" tab is pre-selected as the active
       * tab since this IS the Pages page.
       */}
      <nav aria-label="Application admin sections">
        <TabNav
          tabs={tabs}
          activeTabId="pages"
          onTabChange={handleTabChange}
          visibleTabs={4}
        />
      </nav>

      {/* ── Pages data table ────────────────────────────────────────
       * Replaces <wv-grid bordered="true" hover="true" ...> from
       * pages.cshtml lines 25–48. Three columns: action link (opens
       * page in new tab), weight, and name. pageSize=0 disables
       * pagination (monolith used PagerSize = 0 // All).
       * emptyText matches the "No pages found" alert-info message.
       */}
      <DataTable<PageRecord>
        data={sortedPages}
        columns={columns}
        bordered
        hover
        pageSize={0}
        emptyText="No pages found"
      />
    </div>
  );
}
