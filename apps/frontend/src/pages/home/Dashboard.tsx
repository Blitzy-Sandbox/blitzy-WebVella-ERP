import { useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { usePageByUrl } from '../../hooks/usePages';
import PageBodyNodeRenderer from '../../components/common/PageBodyNodeRenderer';
import LoadingSpinner from '../../components/common/LoadingSpinner';
import {
  ComponentMode,
  PageType,
  type ErpPage,
  type PageBodyNode,
} from '../../types/page';

/**
 * Dashboard (Home Page) Component
 *
 * Main landing page of the ERP application — the first page users see after login.
 * Replaces the monolith's WebVella.Erp.Web/Pages/Index.cshtml[.cs] (HomePageModel).
 *
 * Route: "/" (index route) or "/:pageName" for named home pages.
 *
 * Resolves the home page via the page resolution API (POST /v1/pages/resolve)
 * and dynamically renders the page body nodes using the PageBodyNodeRenderer
 * component dispatch pattern — the React equivalent of the monolith's
 * Component.InvokeAsync(rootComponentName, ...) ViewComponent invocation.
 *
 * Key behavioral mappings from the monolith:
 * - @page "/{PageName?}" route directive → useParams for optional :pageName
 * - BaseErpPageModel.Init() → usePageByUrl hook with PageType.Home
 * - ViewData["Title"] = Page.Label → useEffect setting document.title
 * - foreach bodyNode in Page.Body → page.body.map with PageBodyNodeRenderer
 * - Component.InvokeAsync(rootComponentName, context) → dynamic component dispatch
 * - IPageHook / IHomePageHook execution → handled server-side by the API
 * - Bootstrap alert classes → Tailwind CSS utility equivalents
 */
export default function Dashboard() {
  // Extract optional :pageName from route (replaces @page "/{PageName?}" directive).
  // When pageName is undefined, the API returns the default home page.
  // When pageName is provided, the API returns the named home page.
  const { pageName } = useParams<'pageName'>();

  // Construct URL info for home page resolution.
  // Replaces BaseErpPageModel.Init() which resolved PageType.Home pages
  // via PageService.GetCurrentPage(). Home pages have no application,
  // area, or node context — only an optional pageName.
  const urlInfo = {
    hasRelation: false,
    pageType: PageType.Home as number,
    appName: '',
    areaName: '',
    nodeName: '',
    pageName: pageName ?? '',
  };

  // Fetch the home page via TanStack Query.
  // Must pass { enabled: true } to override the default enabled check
  // which requires appName (empty for home pages, so would default to disabled).
  const { data, isLoading, error } = usePageByUrl(urlInfo, { enabled: true });

  // Extract the resolved ErpPage from the API response envelope.
  // The API returns ApiResponse<ErpPage> where the page is at response.object.
  const page: ErpPage | undefined = data?.object;

  // Set document title from page label.
  // Replaces ViewData["Title"] = Model.ErpRequestContext.Page.Label (Index.cshtml line 6).
  useEffect(() => {
    if (page?.label) {
      document.title = page.label;
    } else {
      document.title = 'Dashboard';
    }
  }, [page?.label]);

  // Loading state — display centered spinner while page data is fetching.
  // Replaces the implicit full-page server render loading of the monolith
  // (which had no loading state since pages were server-rendered).
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <LoadingSpinner />
      </div>
    );
  }

  // Error state — replaces the catch block in OnGet that swallowed errors.
  // The monolith logged the error to the database and rendered the page
  // with whatever state it had; in the SPA, we show a clear error message.
  if (error) {
    return (
      <div className="rounded-md bg-red-50 p-4 m-4" role="alert">
        <p className="text-sm text-red-700">
          An error occurred loading the page. Please try again.
        </p>
      </div>
    );
  }

  // No page found — replaces return NotFound() from Index.cshtml.cs
  // and the "No current page found!" alert from Index.cshtml line 33.
  if (!page) {
    return (
      <div className="rounded-md bg-red-50 p-4 m-4" role="alert">
        <p className="text-sm text-red-700">No current page found!</p>
      </div>
    );
  }

  // Dynamic body rendering — THE CORE PATTERN.
  // Replicates the monolith's foreach loop in Index.cshtml (lines 14-27):
  //   foreach (var bodyNode in currentPage.Body)
  //     var nameArray = bodyNode.ComponentName.Split('.');
  //     var rootComponentName = nameArray[nameArray.Length - 1];
  //     var options = PageUtils.ConvertStringToJObject(bodyNode.Options.ToString());
  //     var pcContext = new PageComponentContext(bodyNode, Model.DataModel, ComponentMode.Display, options);
  //     @await Component.InvokeAsync(rootComponentName, new { context = pcContext })
  return (
    <div className="p-4">
      {page.body && page.body.length > 0 ? (
        page.body.map((bodyNode: PageBodyNode) => {
          // Extract short component name from fully qualified dotted name
          // e.g., "WebVella.Erp.Web.Components.PcRow" → "PcRow"
          const componentName = extractComponentName(bodyNode.componentName);

          // Skip nodes with empty or whitespace-only component names
          // (replicates: if (!String.IsNullOrWhiteSpace(rootComponentName)))
          if (!componentName) {
            return null;
          }

          // Parse the options JSON string into an object
          // (replicates: PageUtils.ConvertStringToJObject(bodyNode.Options.ToString()))
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
        // from Index.cshtml line 29: "Page does not have page nodes attached"
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
 * Replicates the Razor pattern from Index.cshtml lines 16-17:
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
 * Replicates the Razor pattern from Index.cshtml line 21:
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
