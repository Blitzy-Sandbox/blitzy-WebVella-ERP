import { useMemo, useEffect } from 'react';
import { useParams, useSearchParams, Navigate } from 'react-router-dom';
import { usePageByUrl } from '../../hooks/usePages';
import { useApp } from '../../hooks/useApps';
import { ComponentMode, type ErpPage, type PageBodyNode } from '../../types/page';
import PageBodyNodeRenderer from '../../components/common/PageBodyNodeRenderer';
import LoadingSpinner from '../../components/common/LoadingSpinner';

/**
 * Extracts the root component name from a fully qualified dotted component name.
 *
 * Replicates the Razor pattern from ApplicationNode.cshtml (line 17):
 *   var nameArray = bodyNode.ComponentName.Split('.');
 *   var rootComponentName = nameArray[nameArray.Length - 1];
 *
 * @example
 *   "WebVella.Erp.Web.Components.PcRow"       → "PcRow"
 *   "WebVella.Erp.Web.Components.PcFieldText"  → "PcFieldText"
 *   "PcSection"                                → "PcSection" (already short)
 *   ""                                         → "" (empty guard)
 */
function extractComponentName(fullName: string): string {
  if (!fullName || fullName.trim() === '') {
    return '';
  }
  const parts = fullName.split('.');
  return parts[parts.length - 1];
}

/**
 * Safely parses a JSON options string into an object.
 *
 * Replicates: PageUtils.ConvertStringToJObject(bodyNode.Options.ToString())
 * from ApplicationNode.cshtml line 22.
 *
 * Handles edge cases: null, undefined, empty string, already-parsed objects,
 * and malformed JSON (returns empty object on failure).
 */
function parseOptions(
  options: string | Record<string, unknown> | null | undefined,
): Record<string, unknown> {
  if (options === null || options === undefined) {
    return {};
  }
  if (typeof options === 'object') {
    return options;
  }
  if (typeof options === 'string' && options.trim() === '') {
    return {};
  }
  try {
    const parsed: unknown = JSON.parse(options);
    return typeof parsed === 'object' && parsed !== null
      ? (parsed as Record<string, unknown>)
      : {};
  } catch {
    return {};
  }
}

/**
 * AppNode — Application Node Page Component
 *
 * Replaces WebVella.Erp.Web/Pages/ApplicationNode.cshtml[.cs]
 * (ApplicationNodePageModel). This is the most granular page in the ERP's
 * navigation hierarchy: it renders content for a specific **node** within
 * an **area** within an **application**.
 *
 * Route: /:appName/:areaName/:nodeName/a/:pageName?
 *
 * KEY DIFFERENTIATOR: Supports the `hookKey` query parameter
 * (?hookKey=xxx) for URL-based override of page variant selection.
 * This is UNIQUE to AppNode among the four home-level pages. The hookKey
 * is forwarded to the page resolution API so the server can apply the
 * correct set of hooks / page configuration.
 *
 * Behavioral parity with monolith:
 * - Init()        → useApp(appName) + usePageByUrl({ …, hookKey })
 * - hookKey       → useSearchParams().get('hookKey')
 * - BeforeRender  → useEffect for document.title
 * - Body loop     → processedBodyNodes.map → PageBodyNodeRenderer
 * - NotFound()    → <Navigate> / error alert
 */
export default function AppNode(): React.JSX.Element {
  /* ── Route parameters ─────────────────────────────────────────────── */
  // Replaces: @page "/{AppName}/{AreaName}/{NodeName}/a/{PageName?}"
  const { appName, areaName, nodeName, pageName } = useParams<{
    appName: string;
    areaName: string;
    nodeName: string;
    pageName: string;
  }>();

  /* ── hookKey query parameter (UNIQUE to AppNode) ──────────────────── */
  // Replaces: HttpContext.Request.Query["hookKey"]
  // ApplicationNode.cshtml.cs lines 28-30:
  //   var hookKey = "";
  //   if (HttpContext.Request.Query.ContainsKey("hookKey"))
  //     hookKey = HttpContext.Request.Query["hookKey"];
  const [searchParams] = useSearchParams();
  const hookKey = searchParams.get('hookKey') ?? '';

  /* ── 1. Resolve application by name ─────────────────────────────── */
  // Replaces: ErpRequestContext.SetCurrentApp(appName, areaName, nodeName)
  const {
    data: appData,
    isLoading: appLoading,
    error: appError,
  } = useApp(appName);

  /* ── 2. Construct URL info for page resolution ──────────────────── */
  // hookKey is included in the resolve payload so the server applies
  // the correct page hooks / variant selection.
  const urlInfo = useMemo(() => {
    if (!appName) {
      return undefined;
    }
    const info = {
      hasRelation: false,
      pageType: 0,
      appName,
      areaName: areaName ?? '',
      nodeName: nodeName ?? '',
      pageName: pageName ?? '',
    };
    // Include hookKey for API-driven page variant selection when present.
    // UrlInfo is structurally typed; the extra field is forwarded in the
    // POST body to /pages/resolve where the server applies it.
    if (hookKey) {
      return { ...info, hookKey };
    }
    return info;
  }, [appName, areaName, nodeName, pageName, hookKey]);

  /* ── 3. Resolve current page via API ────────────────────────────── */
  // Replaces: Init() → BaseErpPageModel.SetCurrentPage()
  // hookKey-driven variant selection happens server-side.
  const {
    data: pageData,
    isLoading: pageLoading,
    error: pageError,
  } = usePageByUrl(urlInfo);

  // Unwrap the ApiResponse envelope to obtain the actual models.
  const app = appData?.object;
  const page: ErpPage | undefined = pageData?.object;

  /* ── 4. Set document title ──────────────────────────────────────── */
  // Replaces: ViewData["Title"] = Model.ErpRequestContext.Page.Label
  useEffect(() => {
    if (page?.label) {
      document.title = page.label;
    }
  }, [page?.label]);

  /* ── 5. Memoize processed body nodes ────────────────────────────── */
  // Pre-processes each PageBodyNode's componentName and options once
  // per page change instead of on every render. Mirrors the cshtml
  // loop logic at ApplicationNode.cshtml lines 15-24.
  const processedBodyNodes = useMemo(() => {
    if (!page?.body || page.body.length === 0) {
      return [];
    }
    return page.body
      .map((bodyNode: PageBodyNode) => ({
        bodyNode,
        componentName: extractComponentName(bodyNode.componentName),
        options: parseOptions(bodyNode.options),
      }))
      .filter((item) => item.componentName !== '');
  }, [page?.body]);

  /* ── 6. Loading state ───────────────────────────────────────────── */
  if (appLoading || pageLoading) {
    return (
      <div className="flex items-center justify-center min-h-[25rem]">
        <LoadingSpinner size="lg" label="Loading page…" />
      </div>
    );
  }

  /* ── 7. Application not found → redirect to error page ──────────── */
  // Replaces: NotFound() when app resolution fails in Init()
  if (appError || !app) {
    return (
      <Navigate
        to="/error?code=404&message=Application+not+found"
        replace
      />
    );
  }

  /* ── 8. Page not found → inline error state ─────────────────────── */
  // Replaces: ErpRequestContext.Page == null → return NotFound()
  // (ApplicationNode.cshtml.cs line 25)
  if (pageError || !page) {
    return (
      <div className="m-4 rounded-md bg-red-50 p-4" role="alert">
        <div className="flex">
          <div className="flex-shrink-0">
            <svg
              className="h-5 w-5 text-red-400"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                clipRule="evenodd"
              />
            </svg>
          </div>
          <div className="ms-3">
            <h3 className="text-sm font-medium text-red-800">
              No current page found!
            </h3>
            <p className="mt-1 text-sm text-red-700">
              The requested page could not be located for this application node.
            </p>
          </div>
        </div>
      </div>
    );
  }

  /* ── 9. Render page body nodes dynamically ──────────────────────── */
  // Replaces: ApplicationNode.cshtml body rendering loop (lines 10-31).
  // Each body node is dispatched to the correct React component via
  // PageBodyNodeRenderer (dynamic component lookup by short name).
  return (
    <div className="app-node-page">
      {processedBodyNodes.length > 0 ? (
        processedBodyNodes.map(({ bodyNode, componentName, options }) => (
          <PageBodyNodeRenderer
            key={bodyNode.id}
            componentName={componentName}
            options={options}
            mode={ComponentMode.Display}
            bodyNode={bodyNode}
          />
        ))
      ) : (
        <div className="m-4 rounded-md bg-blue-50 p-4" role="status">
          <div className="flex">
            <div className="flex-shrink-0">
              <svg
                className="h-5 w-5 text-blue-400"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a.75.75 0 000 1.5h.253a.25.25 0 01.244.304l-.459 2.066A1.75 1.75 0 0010.747 15H11a.75.75 0 000-1.5h-.253a.25.25 0 01-.244-.304l.459-2.066A1.75 1.75 0 009.253 9H9z"
                  clipRule="evenodd"
                />
              </svg>
            </div>
            <div className="ms-3">
              <p className="text-sm text-blue-700">
                Page does not have page nodes attached
              </p>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
