/**
 * AppHome — Application Home Page Component
 *
 * React page component replacing the monolith's
 * WebVella.Erp.Web/Pages/ApplicationHome.cshtml[.cs] (ApplicationHomePageModel).
 * Renders the **home/landing page** for a specific ERP application
 * (e.g., CRM app home, Project app home).
 *
 * Route: /:appName/a/:pageName? (defined in router.tsx)
 *
 * Two route parameters:
 *   - :appName  (required) — Application slug (e.g. "crm", "project")
 *   - :pageName (optional) — Named page override; when absent, the
 *                             default application home page is returned
 *
 * Key behavioral mappings from the monolith:
 * - @page "/{AppName}/a/{PageName?}" directive → useParams for route extraction
 * - Init() → SetCurrentApp(appName, null, null)  → useApp(appName) hook
 * - Init() → SetCurrentPage(...)                 → usePageByUrl hook
 * - ViewData["Title"] = Page.Label               → useEffect setting document.title
 * - foreach bodyNode in Page.Body                → page.body.map + PageBodyNodeRenderer
 * - Component.InvokeAsync(rootComponentName, ...) → dynamic component dispatch
 * - IPageHook / IApplicationHomePageHook          → handled server-side by the API
 * - return NotFound() when app missing            → Navigate component redirect
 * - Bootstrap alert classes                       → Tailwind CSS utility equivalents
 *
 * Differences from AppNode.tsx:
 * - Does NOT support ?hookKey= query parameter (no useSearchParams)
 * - Only two route params (no :areaName or :nodeName)
 * - No area/node context — this is the app-level home
 *
 * @module AppHome
 */

import { useEffect } from 'react';
import { useParams, Navigate } from 'react-router-dom';
import { usePageByUrl } from '../../hooks/usePages';
import { useApp } from '../../hooks/useApps';
import PageBodyNodeRenderer from '../../components/common/PageBodyNodeRenderer';
import LoadingSpinner from '../../components/common/LoadingSpinner';
import {
  ComponentMode,
  PageType,
  type ErpPage,
  type PageBodyNode,
} from '../../types/page';

/**
 * Application Home page component.
 *
 * Resolves an ERP application by name, fetches its home page definition
 * via the page resolution API, and dynamically renders the page body
 * nodes using the PageBodyNodeRenderer component dispatch pattern.
 *
 * Lifecycle mirrors ApplicationHomePageModel.OnGet():
 *   1. Validate the application exists  (useApp)
 *   2. Resolve the app home page        (usePageByUrl)
 *   3. Set document.title from label    (useEffect)
 *   4. Render body nodes dynamically    (PageBodyNodeRenderer loop)
 */
export default function AppHome() {
  // ── Route Parameter Extraction ──────────────────────────────────────────
  // Replaces the Razor route directive @page "/{AppName}/a/{PageName?}"
  // and the ApplicationHomePageModel constructor injection of ErpRequestContext.
  const { appName, pageName } = useParams<'appName' | 'pageName'>();

  // ── Application Resolution ──────────────────────────────────────────────
  // Replaces ErpRequestContext.SetCurrentApp(appName) from
  // ApplicationHome.cshtml.cs Init(). Fetches the full App definition
  // to validate the application exists. Automatically disabled when
  // appName is falsy (e.g. during route transition).
  const {
    data: appData,
    isLoading: appLoading,
    error: appError,
  } = useApp(appName);

  // Extract the resolved App from the API response envelope.
  const app = appData?.object;

  // ── Page Resolution ─────────────────────────────────────────────────────
  // Construct UrlInfo for app-home page resolution.
  // Replaces BaseErpPageModel.Init() which called
  // SetCurrentApp(appName, null, null) — no area or node for app home.
  // PageType.Application maps to the C# enum value 2.
  const urlInfo = {
    hasRelation: false,
    pageType: PageType.Application as number,
    appName: appName ?? '',
    areaName: '',
    nodeName: '',
    pageName: pageName ?? '',
  };

  // Fetch the app home page via TanStack Query POST /v1/pages/resolve.
  // The default `enabled` check in usePageByUrl is `!!urlInfo.appName`,
  // which is truthy when appName is present — exactly what we need.
  const {
    data: pageData,
    isLoading: pageLoading,
    error: pageError,
  } = usePageByUrl(urlInfo);

  // Extract the resolved ErpPage from the API response envelope.
  const page: ErpPage | undefined = pageData?.object;

  // ── Document Title ──────────────────────────────────────────────────────
  // Replaces ViewData["Title"] = Model.ErpRequestContext.Page.Label
  // from ApplicationHome.cshtml line 6.
  useEffect(() => {
    if (page?.label) {
      document.title = page.label;
    } else if (app) {
      // Fallback to application label when page label is unavailable.
      // `app` is typed as `App` (inferred from useApp return type) which
      // has `label: string` — no explicit import or cast required.
      document.title = app.label || 'Application Home';
    }
  }, [page?.label, app]);

  // ── Loading State ───────────────────────────────────────────────────────
  // Displayed while either the app or page data is being fetched.
  // Replaces the implicit full-page server render of the monolith
  // (which had no loading state since pages were server-rendered).
  if (appLoading || pageLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <LoadingSpinner />
      </div>
    );
  }

  // ── Application Not Found ───────────────────────────────────────────────
  // Replaces the monolith's Init() → return NotFound() when the app does
  // not exist (ApplicationHome.cshtml.cs line 22).
  // Navigate to a generic error page when the application cannot be resolved.
  if (appError || !app) {
    return <Navigate to="/error?code=404" replace />;
  }

  // ── Page Error State ────────────────────────────────────────────────────
  // Replaces the catch block in OnGet that logged errors to the database
  // and set Validation.Message. In the SPA, display a clear error message.
  if (pageError) {
    return (
      <div className="rounded-md bg-red-50 p-4 m-4" role="alert">
        <p className="text-sm text-red-700">
          An error occurred loading the page. Please try again.
        </p>
      </div>
    );
  }

  // ── Page Not Found ──────────────────────────────────────────────────────
  // Replaces: if (ErpRequestContext.Page == null) return NotFound()
  // from ApplicationHome.cshtml.cs line 22, and the "No current page
  // found!" alert from the cshtml fallback rendering.
  if (!page) {
    return (
      <div className="rounded-md bg-red-50 p-4 m-4" role="alert">
        <p className="text-sm text-red-700">No current page found!</p>
      </div>
    );
  }

  // ── Dynamic Body Rendering ──────────────────────────────────────────────
  // THE CORE PATTERN — identical across all 4 home pages.
  // Replicates the monolith's foreach loop in ApplicationHome.cshtml (lines 11-28):
  //
  //   foreach (var bodyNode in currentPage.Body)
  //     var nameArray = bodyNode.ComponentName.Split('.');
  //     var rootComponentName = nameArray[nameArray.Length - 1];
  //     var options = PageUtils.ConvertStringToJObject(bodyNode.Options.ToString());
  //     var pcContext = new PageComponentContext(bodyNode, Model.DataModel,
  //                                             ComponentMode.Display, options);
  //     @await Component.InvokeAsync(rootComponentName, new { context = pcContext })
  return (
    <div className="p-4">
      {page.body && page.body.length > 0 ? (
        page.body.map((bodyNode: PageBodyNode) => {
          // Extract short component name from fully qualified dotted name.
          // e.g., "WebVella.Erp.Web.Components.PcRow" → "PcRow"
          const componentName = extractComponentName(bodyNode.componentName);

          // Skip nodes with empty or whitespace-only component names
          // (replicates: if (!String.IsNullOrWhiteSpace(rootComponentName)))
          if (!componentName) {
            return null;
          }

          // Parse the options JSON string into an object.
          // Replicates: PageUtils.ConvertStringToJObject(bodyNode.Options.ToString())
          const options = parseNodeOptions(bodyNode.options);

          return (
            <PageBodyNodeRenderer
              key={bodyNode.id}
              componentName={componentName}
              options={options}
              mode={ComponentMode.Display}
              bodyNode={bodyNode}
            />
          );
        })
      ) : (
        // Empty body fallback — replaces Bootstrap "alert alert-info"
        // from ApplicationHome.cshtml: "Page does not have page nodes attached"
        <div className="rounded-md bg-blue-50 p-4" role="status">
          <p className="text-sm text-blue-700">
            Page does not have page nodes attached
          </p>
        </div>
      )}
    </div>
  );
}

/**
 * Extracts the root component name from a fully qualified dotted component name.
 *
 * Replicates the Razor pattern from ApplicationHome.cshtml:
 *   var nameArray = bodyNode.ComponentName.Split('.');
 *   var rootComponentName = nameArray[nameArray.Length - 1];
 *
 * @param fullName - Fully qualified component name
 * @returns The last segment of the dotted name, or empty string for invalid input
 *
 * @example
 * extractComponentName("WebVella.Erp.Web.Components.PcRow")       // → "PcRow"
 * extractComponentName("WebVella.Erp.Web.Components.PcFieldText")  // → "PcFieldText"
 * extractComponentName("PcSection")                                // → "PcSection"
 * extractComponentName("")                                         // → ""
 * extractComponentName("  ")                                       // → ""
 */
function extractComponentName(fullName: string): string {
  if (!fullName || fullName.trim() === '') {
    return '';
  }
  const parts = fullName.split('.');
  return parts[parts.length - 1];
}

/**
 * Safely parses page body node options from a JSON string to an options object.
 *
 * Replicates the Razor pattern from ApplicationHome.cshtml:
 *   var options = PageUtils.ConvertStringToJObject(bodyNode.Options.ToString())
 *
 * Handles edge cases where options may be:
 * - A valid JSON string (normal case → parse and return)
 * - An empty or whitespace string (→ return empty object)
 * - Already an object at runtime (→ return as-is)
 * - Malformed JSON (→ return empty object, fail gracefully)
 *
 * @param options - Raw options value from PageBodyNode.options (typed as string)
 * @returns Parsed options object, or empty object on parse failure
 */
function parseNodeOptions(options: string): Record<string, unknown> {
  if (!options || (typeof options === 'string' && options.trim() === '')) {
    return {};
  }

  // Runtime safety: if options is already an object (e.g., API returned
  // pre-parsed JSON), return it directly without attempting to parse.
  if (typeof options === 'object') {
    return options as unknown as Record<string, unknown>;
  }

  try {
    const parsed: unknown = JSON.parse(options);
    if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
    return {};
  } catch {
    // Gracefully handle malformed JSON — return empty options
    // rather than crashing the entire page rendering
    return {};
  }
}
