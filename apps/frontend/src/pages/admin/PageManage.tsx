import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { usePage, useUpdatePage } from '../../hooks/usePages';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation } from '../../components/forms/DynamicForm';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import type { ErpPage } from '../../types/page';
import { PageType } from '../../types/page';

/**
 * Helper to get a human-readable label for PageType enum values.
 * Maps from C# PageType.GetLabel() used in manage.cshtml.cs.
 */
function getPageTypeLabel(type: PageType | undefined): string {
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
      return 'Unknown';
  }
}

/**
 * PageManage — Admin page for editing page metadata and sitemap configuration.
 *
 * Route: /admin/pages/:pageId/manage
 *
 * Replaces the monolith's WebVella.Erp.Plugins.SDK/Pages/page/manage.cshtml[.cs].
 * Loads page metadata via usePage(id), presents an editable form for
 * Name, Label, IconClass, Weight, Layout, Description, and a read-only
 * sitemap configuration section. On submit, calls useUpdatePage() mutation
 * preserving read-only flags (System, IsRazorBody, RazorBody).
 */
function PageManage(): React.JSX.Element {
  const { pageId } = useParams<{ pageId: string }>();
  const navigate = useNavigate();

  // --- Data fetching ---
  const { data: pageResponse, isLoading, isError, error } = usePage(pageId);
  const page: ErpPage | undefined = pageResponse?.object;

  // --- Mutation ---
  const updatePageMutation = useUpdatePage();

  // --- Form state (editable fields matching manage.cshtml.cs BindProperties) ---
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
  const [iconClass, setIconClass] = useState('');
  const [weight, setWeight] = useState<number>(10);
  const [layout, setLayout] = useState('');
  const [description, setDescription] = useState('');

  // --- Validation state ---
  const [validation, setValidation] = useState<FormValidation>({ errors: [] });

  // --- Sync form state when page data loads (InitPage equivalent) ---
  useEffect(() => {
    if (page) {
      setName(page.name ?? '');
      setLabel(page.label ?? '');
      setIconClass(page.iconClass ?? '');
      setWeight(page.weight ?? 10);
      setLayout(page.layout ?? '');
      // The Description field from the monolith cshtml maps to an
      // optional metadata property. If the API response carries it
      // as an extra attribute we pick it up, otherwise default empty.
      const anyPage = page as unknown as Record<string, unknown>;
      setDescription(typeof anyPage['description'] === 'string' ? anyPage['description'] : '');
    }
  }, [page]);

  // --- Sub-nav tabs (AdminPageUtils.GetPageAdminSubNav equivalent) ---
  const tabs: TabConfig[] = pageId
    ? [
        { id: 'details', label: 'Details' },
        { id: 'manage', label: 'Manage' },
        { id: 'body', label: 'Body' },
        { id: 'data-sources', label: 'Data Sources' },
      ]
    : [];

  /**
   * handleTabChange navigates to the appropriate admin page sub-route
   * when a tab is clicked. The "manage" tab stays on the current page.
   */
  const handleTabChange = useCallback(
    (tabId: string) => {
      if (!pageId) return;
      switch (tabId) {
        case 'details':
          navigate(`/admin/pages/${pageId}`);
          break;
        case 'manage':
          // Already on this page — no-op
          break;
        case 'body':
          navigate(`/admin/pages/${pageId}/body`);
          break;
        case 'data-sources':
          navigate(`/admin/pages/${pageId}/data-sources`);
          break;
        default:
          break;
      }
    },
    [pageId, navigate],
  );

  /**
   * handleSubmit — Form submission handler.
   * Calls useUpdatePage mutation preserving read-only system flags
   * (System, IsRazorBody, RazorBody) during update, matching
   * manage.cshtml.cs OnPost() behaviour.
   */
  const handleSubmit = useCallback(
    async (e?: React.FormEvent) => {
      if (e) {
        e.preventDefault();
      }

      if (!pageId || !page) return;

      // Client-side required validation
      const errors: FormValidation['errors'] = [];
      if (!name.trim()) {
        errors.push({ propertyName: 'name', message: 'Name is required.' });
      }
      if (!label.trim()) {
        errors.push({ propertyName: 'label', message: 'Label is required.' });
      }
      if (errors.length > 0) {
        setValidation({ message: 'Please correct the errors below.', errors });
        return;
      }

      setValidation({ errors: [] });

      try {
        await updatePageMutation.mutateAsync({
          id: pageId,
          name: name.trim(),
          label: label.trim(),
          iconClass: iconClass.trim() || undefined,
          weight,
          layout: layout.trim() || undefined,
          // Preserve read-only flags from the existing page data
          system: page.system,
          isRazorBody: page.isRazorBody,
          razorBody: page.razorBody,
          // Preserve sitemap binding (convert null → undefined for payload type)
          appId: page.appId ?? undefined,
          areaId: page.areaId ?? undefined,
          nodeId: page.nodeId ?? undefined,
          entityId: page.entityId ?? undefined,
          type: page.type,
        });

        // On success, navigate to page details view
        // Matches monolith Redirect($"/sdk/objects/page/r/{ErpPage.Id}/")
        navigate(`/admin/pages/${pageId}`);
      } catch (err: unknown) {
        // Handle server-side validation errors
        const apiError = err as {
          message?: string;
          errors?: Array<{ key?: string; propertyName?: string; message?: string }>;
        };
        const serverErrors =
          apiError?.errors?.map((e) => ({
            propertyName: e.propertyName ?? e.key ?? '',
            message: e.message ?? 'Validation error',
          })) ?? [];
        setValidation({
          message: apiError?.message ?? 'An error occurred while saving.',
          errors: serverErrors,
        });
      }
    },
    [pageId, page, name, label, iconClass, weight, layout, navigate, updatePageMutation],
  );

  // --- Loading state ---
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[12rem]">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
        <span className="sr-only">Loading page…</span>
      </div>
    );
  }

  // --- Error state ---
  if (isError) {
    return (
      <div
        className="rounded-md bg-red-50 p-4 text-red-800 border border-red-200"
        role="alert"
      >
        <h2 className="text-lg font-semibold">Error loading page</h2>
        <p className="mt-1 text-sm">
          {(error as Error)?.message ?? 'Could not load the requested page. Please try again.'}
        </p>
        <Link
          to="/admin/pages"
          className="mt-3 inline-block text-sm font-medium text-red-600 underline hover:text-red-800"
        >
          Back to pages
        </Link>
      </div>
    );
  }

  // --- Not found state ---
  if (!page) {
    return (
      <div
        className="rounded-md bg-yellow-50 p-4 text-yellow-800 border border-yellow-200"
        role="alert"
      >
        <h2 className="text-lg font-semibold">Page Not Found</h2>
        <p className="mt-1 text-sm">The page you are looking for does not exist.</p>
        <Link
          to="/admin/pages"
          className="mt-3 inline-block text-sm font-medium text-yellow-700 underline hover:text-yellow-900"
        >
          Back to pages
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* ─── Page header ─── */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          {page.iconClass && (
            <span
              className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-indigo-100 text-indigo-700"
              aria-hidden="true"
            >
              <i className={page.iconClass} />
            </span>
          )}
          <div>
            <h1 className="text-xl font-bold text-gray-900">
              Manage Page: {page.label || page.name}
            </h1>
            <p className="text-sm text-gray-500">
              Edit page metadata and sitemap configuration
            </p>
          </div>
        </div>

        {/* Header actions */}
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => void handleSubmit()}
            disabled={updatePageMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:opacity-60 disabled:cursor-not-allowed"
          >
            {updatePageMutation.isPending ? (
              <>
                <span className="animate-spin h-4 w-4 border-2 border-white border-t-transparent rounded-full" />
                Saving…
              </>
            ) : (
              <>
                <i className="fa fa-save" aria-hidden="true" />
                Save Page
              </>
            )}
          </button>

          <Link
            to={`/admin/pages/${pageId}`}
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            <i className="fa fa-times" aria-hidden="true" />
            Cancel
          </Link>
        </div>
      </div>

      {/* ─── Sub-nav tabs ─── */}
      <TabNav
        tabs={tabs}
        activeTabId="manage"
        onTabChange={handleTabChange}
      />

      {/* ─── Edit form ─── */}
      <DynamicForm
        name="ManageRecord"
        labelMode="stacked"
        fieldMode="form"
        validation={validation}
        showValidation={validation.errors.length > 0 || !!validation.message}
        onSubmit={handleSubmit}
      >
        {/* ────────── General Section ────────── */}
        <fieldset className="rounded-lg border border-gray-200 bg-white p-6">
          <legend className="px-2 text-base font-semibold text-gray-900">
            General
          </legend>

          <div className="grid grid-cols-1 gap-x-6 gap-y-5 sm:grid-cols-12">
            {/* Name — span 6 */}
            <div className="sm:col-span-6">
              <label
                htmlFor="page-name"
                className="block text-sm font-medium text-gray-700"
              >
                Name <span className="text-red-500">*</span>
              </label>
              <input
                id="page-name"
                type="text"
                required
                value={name}
                onChange={(e) => setName(e.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
                placeholder="Page name"
              />
            </div>

            {/* Label — span 6 */}
            <div className="sm:col-span-6">
              <label
                htmlFor="page-label"
                className="block text-sm font-medium text-gray-700"
              >
                Label <span className="text-red-500">*</span>
              </label>
              <input
                id="page-label"
                type="text"
                required
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
                placeholder="Page label"
              />
            </div>

            {/* IconClass — span 6 */}
            <div className="sm:col-span-6">
              <label
                htmlFor="page-icon"
                className="block text-sm font-medium text-gray-700"
              >
                Icon Class
              </label>
              <div className="relative mt-1">
                <input
                  id="page-icon"
                  type="text"
                  value={iconClass}
                  onChange={(e) => setIconClass(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
                  placeholder="e.g. fa fa-file"
                />
                {iconClass && (
                  <span className="absolute inset-y-0 end-0 flex items-center pe-3 text-gray-500">
                    <i className={iconClass} aria-hidden="true" />
                  </span>
                )}
              </div>
            </div>

            {/* Weight — span 6, integer only */}
            <div className="sm:col-span-6">
              <label
                htmlFor="page-weight"
                className="block text-sm font-medium text-gray-700"
              >
                Weight
              </label>
              <input
                id="page-weight"
                type="number"
                step="1"
                value={weight}
                onChange={(e) => setWeight(parseInt(e.target.value, 10) || 0)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
              />
              <p className="mt-1 text-xs text-gray-500">
                If greater than 1000, the page will not appear in navigation.
              </p>
            </div>

            {/* Layout — span 6 */}
            <div className="sm:col-span-6">
              <label
                htmlFor="page-layout"
                className="block text-sm font-medium text-gray-700"
              >
                Layout
              </label>
              <input
                id="page-layout"
                type="text"
                value={layout}
                onChange={(e) => setLayout(e.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
                placeholder="Layout identifier"
              />
            </div>

            {/* Description — full width */}
            <div className="sm:col-span-12">
              <label
                htmlFor="page-description"
                className="block text-sm font-medium text-gray-700"
              >
                Description
              </label>
              <textarea
                id="page-description"
                rows={3}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500"
                placeholder="Optional page description"
              />
            </div>
          </div>
        </fieldset>

        {/* ────────── Sitemap Section (edit mode display) ────────── */}
        <fieldset className="rounded-lg border border-gray-200 bg-white p-6">
          <legend className="px-2 text-base font-semibold text-gray-900">
            Sitemap
          </legend>

          <div className="grid grid-cols-1 gap-x-6 gap-y-5 sm:grid-cols-12">
            {/* Page Type — read-only */}
            <div className="sm:col-span-6">
              <span className="block text-sm font-medium text-gray-700">Type</span>
              <p className="mt-1 text-sm text-gray-900">
                {getPageTypeLabel(page.type)}
              </p>
            </div>

            {/* App ID */}
            <div className="sm:col-span-6">
              <span className="block text-sm font-medium text-gray-700">App ID</span>
              <p className="mt-1 truncate text-sm text-gray-900 font-mono">
                {page.appId || <span className="text-gray-400 italic">Not assigned</span>}
              </p>
            </div>

            {/* Area ID */}
            <div className="sm:col-span-6">
              <span className="block text-sm font-medium text-gray-700">Area ID</span>
              <p className="mt-1 truncate text-sm text-gray-900 font-mono">
                {page.areaId || <span className="text-gray-400 italic">Not assigned</span>}
              </p>
            </div>

            {/* Node ID */}
            <div className="sm:col-span-6">
              <span className="block text-sm font-medium text-gray-700">Node ID</span>
              <p className="mt-1 truncate text-sm text-gray-900 font-mono">
                {page.nodeId || <span className="text-gray-400 italic">Not assigned</span>}
              </p>
            </div>

            {/* Entity ID */}
            <div className="sm:col-span-6">
              <span className="block text-sm font-medium text-gray-700">Entity ID</span>
              <p className="mt-1 truncate text-sm text-gray-900 font-mono">
                {page.entityId || <span className="text-gray-400 italic">Not assigned</span>}
              </p>
            </div>

            {/* System flag */}
            <div className="sm:col-span-6">
              <span className="block text-sm font-medium text-gray-700">System</span>
              <p className="mt-1 text-sm">
                {page.system ? (
                  <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                    Yes
                  </span>
                ) : (
                  <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                    No
                  </span>
                )}
              </p>
            </div>
          </div>
        </fieldset>

        {/* ────────── Form Footer ────────── */}
        <div className="flex items-center justify-end gap-3 pt-2">
          <Link
            to={`/admin/pages/${pageId}`}
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          >
            Cancel
          </Link>
          <button
            type="submit"
            disabled={updatePageMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:opacity-60 disabled:cursor-not-allowed"
          >
            {updatePageMutation.isPending ? 'Saving…' : 'Save Page'}
          </button>
        </div>
      </DynamicForm>
    </div>
  );
}

export default PageManage;
