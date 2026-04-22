/**
 * PageCreate — Admin Page Creation Page
 *
 * React page replacing the monolith's Razor Page at:
 *   - `WebVella.Erp.Plugins.SDK/Pages/page/create.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/page/create.cshtml.cs`
 *
 * Route: `/admin/pages/create`
 *
 * Source behaviour preserved:
 *   - Form fields: Name (required), Label (required), Weight (default 10),
 *     IconClass, System (checkbox), Type (dropdown, default Site)
 *   - Sitemap binding section (app/area/node/entity cascading dropdowns)
 *     replacing the WvSdkPageSitemap ViewComponent
 *   - Creates via POST /v1/pages (replaces PageService.CreatePage)
 *   - Navigates to page details on success (replaces Redirect to page detail)
 *   - Displays validation errors on failure (replaces ValidationException catch)
 *   - Green themed Create Page button + Cancel link (matches create.cshtml)
 *
 * AAP compliance:
 *   - §0.4.3  — Full page CRUD via Entity Management service
 *   - §0.5.1  — PageService.CreatePage() → useCreatePage mutation
 *   - §0.7.7  — WvSdkPageSitemap ViewComponent → inline cascading dropdowns
 *   - §0.8.1  — Self-contained SPA page with no server-side rendering
 *   - §0.8.6  — API calls via /v1/ prefixed endpoints
 *
 * @module pages/admin/PageCreate
 */

import { useState, useCallback, useMemo } from 'react';
import { useNavigate, Link } from 'react-router-dom';

import { useCreatePage } from '../../hooks/usePages';
import { useApps } from '../../hooks/useApps';
import { useEntities } from '../../hooks/useEntities';
import { PageType } from '../../types/page';
import type { App } from '../../types/app';
import type { Entity } from '../../types/entity';
import DynamicForm, { type FormValidation } from '../../components/forms/DynamicForm';

/* ────────────────────────────────────────────────────────────────
 * Constants
 * ──────────────────────────────────────────────────────────────── */

/**
 * Return path for cancel and post-creation redirect base.
 * Replaces the monolith's ReturnUrl default of `/sdk/objects/page/l/list`
 * (create.cshtml cancel link) with the new SPA route.
 */
const PAGES_LIST_PATH = '/admin/pages';

/**
 * Default form weight value.
 * Matches create.cshtml.cs line: `public int Weight { get; set; } = 10;`
 */
const DEFAULT_WEIGHT = 10;

/**
 * PageType enum labels for the type dropdown.
 * Maps each PageType value to a human-readable display label.
 * Derived from C# PageType enum values referenced in create.cshtml.cs.
 */
const PAGE_TYPE_OPTIONS: ReadonlyArray<{ value: PageType; label: string }> = [
  { value: PageType.Home, label: 'Home' },
  { value: PageType.Site, label: 'Site' },
  { value: PageType.Application, label: 'Application' },
  { value: PageType.RecordList, label: 'Record List' },
  { value: PageType.RecordCreate, label: 'Record Create' },
  { value: PageType.RecordDetails, label: 'Record Details' },
  { value: PageType.RecordManage, label: 'Record Manage' },
];

/**
 * Page types that are entity-bound and require an entity selection.
 * When the user picks one of these types, the Entity dropdown becomes relevant.
 */
const ENTITY_BOUND_PAGE_TYPES: ReadonlySet<PageType> = new Set([
  PageType.RecordList,
  PageType.RecordCreate,
  PageType.RecordDetails,
  PageType.RecordManage,
]);

/* ────────────────────────────────────────────────────────────────
 * Helper: per-field validation error extraction
 * ──────────────────────────────────────────────────────────────── */

/**
 * Returns true if the validation state contains an error for the given
 * field name (case-insensitive comparison to match server-side
 * property name casing variations).
 */
function hasFieldError(
  validation: FormValidation,
  fieldName: string,
): boolean {
  const lower = fieldName.toLowerCase();
  return validation.errors.some(
    (err) => err.propertyName.toLowerCase() === lower,
  );
}

/**
 * Extracts all error messages for a specific field name from validation state.
 */
function getFieldErrors(
  validation: FormValidation,
  fieldName: string,
): Array<{ propertyName: string; message: string }> {
  const lower = fieldName.toLowerCase();
  return validation.errors.filter(
    (err) => err.propertyName.toLowerCase() === lower,
  );
}

/* ────────────────────────────────────────────────────────────────
 * Shared Tailwind class strings
 * ──────────────────────────────────────────────────────────────── */

const INPUT_CLASSES =
  'block w-full max-w-md rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus-visible:border-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-500';

const SELECT_CLASSES =
  'block w-full max-w-md rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:border-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-500';

const LABEL_CLASSES = 'mb-1 block text-sm font-medium text-gray-700';

const ERROR_CLASSES = 'mt-1 text-sm text-red-600';

/* ────────────────────────────────────────────────────────────────
 * PageCreate Component
 * ──────────────────────────────────────────────────────────────── */

/**
 * PageCreate — Page component for creating a new ERP page definition.
 *
 * Renders a form with basic page fields (Name, Label, Weight, IconClass,
 * System, Type) and a sitemap binding section (App, Area, Node, Entity
 * cascading dropdowns that replace the WvSdkPageSitemap ViewComponent).
 *
 * Mutation mapping from source:
 *   - `PageService.CreatePage(pageId, Name, Label, ...)` → `useCreatePage().mutateAsync(payload)`
 *   - `ValidationException` catch → `FormValidation` error display
 *   - `Redirect($"~/sdk/objects/page/r/{pageId}/")` → `navigate(\`/admin/pages/${id}\`)`
 *
 * Field mapping from create.cshtml.cs BindProperty:
 *   - Name (string, required) → controlled text input
 *   - Label (string, required) → controlled text input
 *   - Weight (int, default 10) → controlled number input
 *   - IconClass (string) → controlled text input
 *   - System (bool, default false) → controlled checkbox
 *   - Type (PageType, default Site) → controlled select
 *   - AppId (Guid?) → app select dropdown
 *   - AreaId (Guid?) → area select dropdown (cascaded from app)
 *   - NodeId (Guid?) → node select dropdown (cascaded from area)
 *   - EntityId (Guid?) → entity select dropdown
 */
function PageCreate(): React.ReactNode {
  /* ── Controlled form field state (create.cshtml.cs BindProperty) ──── */
  const [name, setName] = useState<string>('');
  const [label, setLabel] = useState<string>('');
  const [weight, setWeight] = useState<number>(DEFAULT_WEIGHT);
  const [iconClass, setIconClass] = useState<string>('');
  const [system, setSystem] = useState<boolean>(false);
  const [pageType, setPageType] = useState<PageType>(PageType.Site);

  /* ── Sitemap binding state (WvSdkPageSitemap ViewComponent) ──────── */
  const [appId, setAppId] = useState<string>('');
  const [areaId, setAreaId] = useState<string>('');
  const [nodeId, setNodeId] = useState<string>('');
  const [entityId, setEntityId] = useState<string>('');

  /* ── Validation state (create.cshtml.cs ValidationException) ──────── */
  const [validation, setValidation] = useState<FormValidation>({
    errors: [],
  });

  /* ── Routing ──────────────────────────────────────────────────────── */
  const navigate = useNavigate();

  /* ── Data hooks ───────────────────────────────────────────────────── */
  const createPageMutation = useCreatePage();
  const {
    mutateAsync,
    isPending,
    isError: isMutationError,
    error: mutationError,
    isSuccess: isMutationSuccess,
    data: mutationData,
    reset: resetMutation,
  } = createPageMutation;
  const { data: appsResponse, isLoading: appsLoading } = useApps();
  const { data: entities, isLoading: entitiesLoading } = useEntities();

  /* ── Derived data: apps list from ApiResponse envelope ─────────── */
  const apps: App[] = useMemo(
    () => appsResponse?.object ?? [],
    [appsResponse],
  );

  /* ── Derived data: entities list (already unwrapped by hook) ───── */
  const entityList: Entity[] = useMemo(
    () => entities ?? [],
    [entities],
  );

  /* ── Derived data: sitemap areas for selected app ─────────────── */
  const selectedApp = useMemo(
    () => apps.find((a) => a.id === appId) ?? null,
    [apps, appId],
  );

  const areas = useMemo(
    () => selectedApp?.sitemap?.areas ?? [],
    [selectedApp],
  );

  /* ── Derived data: sitemap nodes for selected area ────────────── */
  const selectedArea = useMemo(
    () => areas.find((a) => a.id === areaId) ?? null,
    [areas, areaId],
  );

  const nodes = useMemo(
    () => selectedArea?.nodes ?? [],
    [selectedArea],
  );

  /* ── Derived: whether current page type requires an entity ─────── */
  const isEntityBound = useMemo(
    () => ENTITY_BOUND_PAGE_TYPES.has(pageType),
    [pageType],
  );

  /* ── Cascade reset handlers ──────────────────────────────────── */

  /**
   * When the selected app changes, reset area and node selections
   * since the available areas change with the app.
   */
  const handleAppChange = useCallback((newAppId: string) => {
    setAppId(newAppId);
    setAreaId('');
    setNodeId('');
  }, []);

  /**
   * When the selected area changes, reset node selection
   * since the available nodes change with the area.
   */
  const handleAreaChange = useCallback((newAreaId: string) => {
    setAreaId(newAreaId);
    setNodeId('');
  }, []);

  /**
   * Form submission handler.
   *
   * Replicates the OnPost method from create.cshtml.cs:
   *   1. Client-side required field validation (Name, Label)
   *   2. Builds CreatePagePayload from form state
   *   3. Calls useCreatePage mutation via mutateAsync
   *   4. Navigates to page details on success
   *   5. Maps API errors to FormValidation state on failure
   */
  const handleSubmit = useCallback(
    async (event: React.FormEvent<HTMLFormElement>) => {
      event.preventDefault();

      /* Client-side required validation (belt-and-suspenders with server) */
      const errors: FormValidation['errors'] = [];

      if (!name.trim()) {
        errors.push({ propertyName: 'Name', message: 'Name is required.' });
      }

      if (!label.trim()) {
        errors.push({ propertyName: 'Label', message: 'Label is required.' });
      }

      if (errors.length > 0) {
        setValidation({ errors });
        return;
      }

      /* Clear previous validation and mutation state before submitting */
      setValidation({ errors: [] });
      resetMutation();

      /* Build payload matching CreatePagePayload from usePages.ts */
      const payload = {
        name: name.trim(),
        label: label.trim(),
        weight,
        type: pageType,
        iconClass: iconClass.trim() || undefined,
        system: system || undefined,
        appId: appId || undefined,
        entityId: entityId || undefined,
        areaId: areaId || undefined,
        nodeId: nodeId || undefined,
      };

      try {
        /*
         * mutateAsync returns ApiResponse<ErpPage>.
         * On success the server returns the created page with its new GUID id.
         * Replaces: var pageId = Guid.NewGuid(); PageService.CreatePage(pageId, ...);
         *           return Redirect($"~/sdk/objects/page/r/{pageId}/");
         */
        const result = await mutateAsync(payload);
        const newPageId = result?.object?.id;

        if (newPageId) {
          navigate(`${PAGES_LIST_PATH}/${newPageId}`);
        } else {
          /* Fallback: navigate to list if ID not returned */
          navigate(PAGES_LIST_PATH);
        }
      } catch (apiError: unknown) {
        /*
         * Map API error to FormValidation (create.cshtml.cs ValidationException catch).
         * The ApiError from the client wrapper contains `message` and
         * `errors` (ApiErrorItem[]) which map to ValidationException's
         * Message and Errors properties.
         */
        const err = apiError as {
          message?: string;
          errors?: Array<{ key?: string; message?: string; value?: string }>;
        };

        setValidation({
          message:
            err.message || 'An error occurred while creating the page.',
          errors: (err.errors ?? []).map((e) => ({
            propertyName: e.key || '',
            message: e.message || e.value || '',
          })),
        });
      }
    },
    [
      name,
      label,
      weight,
      pageType,
      iconClass,
      system,
      appId,
      entityId,
      areaId,
      nodeId,
      mutateAsync,
      resetMutation,
      navigate,
    ],
  );

  return (
    <div className="mx-auto w-full max-w-4xl">
      {/* ── Page Header (create.cshtml lines 10-17) ──────────────────
       *  Red themed header with plus icon, matching the source's
       *  color="#f44336" and icon-class="fa fa-plus".
       * ──────────────────────────────────────────────────────────── */}
      <header className="mb-6 flex flex-wrap items-center justify-between gap-4 rounded-lg border-s-4 border-red-600 bg-white p-4 shadow-sm">
        <div className="flex items-center gap-3">
          {/* Plus icon replacing fa fa-plus (create.cshtml line 11) */}
          <span
            className="inline-flex h-8 w-8 items-center justify-center rounded bg-red-600 text-white"
            aria-hidden="true"
          >
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
          </span>

          <div>
            {/* area-label="Page" → breadcrumb-style label */}
            <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
              Page
            </p>
            {/* title="Create Page" (create.cshtml line 11) */}
            <h1 className="text-lg font-semibold text-gray-900">
              Create Page
            </h1>
          </div>
        </div>

        {/* Action buttons (create.cshtml lines 13-16) */}
        <div className="flex items-center gap-2">
          {/* Submit button — "Create Page" (create.cshtml line 14) */}
          <button
            type="submit"
            form="CreateRecord"
            disabled={isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-3 py-1.5 text-sm font-medium text-white shadow-sm transition-colors duration-200 hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {/* Plus icon for submit button (icon-class="fa fa-plus go-white") */}
            <svg
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4"
              aria-hidden="true"
            >
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            {isPending ? 'Creating…' : 'Create Page'}
          </button>

          {/* Cancel link (create.cshtml line 15) */}
          <Link
            to={PAGES_LIST_PATH}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-200 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
        </div>
      </header>

      {/* ── Mutation Status Banners ─────────────────────────────────
       *  Show server-level error when mutation fails but there are no
       *  per-field validation errors, and a brief success message while
       *  navigating after successful creation.
       * ──────────────────────────────────────────────────────────── */}
      {isMutationError && validation.errors.length === 0 && (
        <div
          role="alert"
          className="mb-4 rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-800"
        >
          <strong className="font-semibold">Error: </strong>
          {mutationError?.message || 'An unexpected error occurred while creating the page.'}
        </div>
      )}

      {isMutationSuccess && mutationData && (
        <div
          role="status"
          className="mb-4 rounded-md border border-green-200 bg-green-50 p-4 text-sm text-green-800"
        >
          Page created successfully. Redirecting…
        </div>
      )}

      {/* ── Form (create.cshtml lines 20-65) ─────────────────────────
       *  DynamicForm replaces <wv-form id="CreateRecord"
       *  name="CreateRecord" label-mode="Stacked" mode="Form"
       *  autocomplete="false">
       * ──────────────────────────────────────────────────────────── */}
      <DynamicForm
        id="CreateRecord"
        name="CreateRecord"
        labelMode="stacked"
        fieldMode="form"
        validation={validation}
        showValidation
        onSubmit={handleSubmit}
        className="space-y-6"
      >
        {/* ── Basic Page Fields Section ────────────────────────────── */}
        <section className="rounded-lg bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-base font-semibold text-gray-800">
            Page Details
          </h2>

          {/* Row 1: Name, Label, Weight — 3-column grid (create.cshtml line 24-28) */}
          <div className="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-3">
            {/* ── Name field (create.cshtml line 25) ─────────────────
             *  <wv-field-text label-text="Name" value="@Model.Name"
             *   name="Name" required="true">
             * ──────────────────────────────────────────────────────── */}
            <div>
              <label htmlFor="page-name" className={LABEL_CLASSES}>
                Name
                <span className="text-red-600" aria-hidden="true">
                  {' '}
                  *
                </span>
              </label>
              <input
                id="page-name"
                type="text"
                name="Name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                required
                autoComplete="off"
                aria-required="true"
                aria-invalid={
                  hasFieldError(validation, 'name') ? 'true' : undefined
                }
                aria-describedby={
                  hasFieldError(validation, 'name')
                    ? 'page-name-error'
                    : undefined
                }
                className={INPUT_CLASSES}
                placeholder="Enter page name"
              />
              {getFieldErrors(validation, 'name').map((err, idx) => (
                <p
                  key={`name-err-${idx}`}
                  id="page-name-error"
                  role="alert"
                  className={ERROR_CLASSES}
                >
                  {err.message}
                </p>
              ))}
            </div>

            {/* ── Label field (create.cshtml line 26) ────────────────
             *  <wv-field-text label-text="Label" value="@Model.Label"
             *   name="Label" required="true">
             * ──────────────────────────────────────────────────────── */}
            <div>
              <label htmlFor="page-label" className={LABEL_CLASSES}>
                Label
                <span className="text-red-600" aria-hidden="true">
                  {' '}
                  *
                </span>
              </label>
              <input
                id="page-label"
                type="text"
                name="Label"
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                required
                autoComplete="off"
                aria-required="true"
                aria-invalid={
                  hasFieldError(validation, 'label') ? 'true' : undefined
                }
                aria-describedby={
                  hasFieldError(validation, 'label')
                    ? 'page-label-error'
                    : undefined
                }
                className={INPUT_CLASSES}
                placeholder="Enter page label"
              />
              {getFieldErrors(validation, 'label').map((err, idx) => (
                <p
                  key={`label-err-${idx}`}
                  id="page-label-error"
                  role="alert"
                  className={ERROR_CLASSES}
                >
                  {err.message}
                </p>
              ))}
            </div>

            {/* ── Weight field (create.cshtml line 27) ───────────────
             *  <wv-field-number label-text="Weight" value="@Model.Weight"
             *   name="Weight">
             * ──────────────────────────────────────────────────────── */}
            <div>
              <label htmlFor="page-weight" className={LABEL_CLASSES}>
                Weight
              </label>
              <input
                id="page-weight"
                type="number"
                name="Weight"
                value={weight}
                onChange={(e) =>
                  setWeight(
                    e.target.value === '' ? 0 : parseInt(e.target.value, 10),
                  )
                }
                autoComplete="off"
                className={INPUT_CLASSES}
                placeholder="10"
              />
            </div>
          </div>

          {/* Row 2: IconClass, Type — 2-column grid */}
          <div className="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* ── IconClass field ─────────────────────────────────────
             *  Matches create.cshtml.cs BindProperty IconClass (string).
             *  CSS icon class for the page icon (e.g. "fas fa-file").
             * ──────────────────────────────────────────────────────── */}
            <div>
              <label htmlFor="page-icon-class" className={LABEL_CLASSES}>
                Icon Class
              </label>
              <input
                id="page-icon-class"
                type="text"
                name="IconClass"
                value={iconClass}
                onChange={(e) => setIconClass(e.target.value)}
                autoComplete="off"
                className={INPUT_CLASSES}
                placeholder="e.g. fas fa-file"
              />
            </div>

            {/* ── Type field ──────────────────────────────────────────
             *  Matches create.cshtml.cs: public PageType Type { get; set; } = PageType.Site;
             *  Dropdown with all PageType enum values.
             * ──────────────────────────────────────────────────────── */}
            <div>
              <label htmlFor="page-type" className={LABEL_CLASSES}>
                Type
              </label>
              <select
                id="page-type"
                name="Type"
                value={pageType}
                onChange={(e) => setPageType(Number(e.target.value) as PageType)}
                className={SELECT_CLASSES}
              >
                {PAGE_TYPE_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {/* ── System checkbox ───────────────────────────────────────
           *  Matches create.cshtml.cs: public bool System { get; set; } = false;
           * ──────────────────────────────────────────────────────────── */}
          <div className="flex items-center gap-2">
            <input
              id="page-system"
              type="checkbox"
              name="System"
              checked={system}
              onChange={(e) => setSystem(e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
            />
            <label htmlFor="page-system" className="text-sm text-gray-700">
              System Page
            </label>
          </div>
        </section>

        {/* ── Sitemap Binding Section ─────────────────────────────────
         *  Replaces the WvSdkPageSitemap ViewComponent from create.cshtml.
         *  Provides cascading dropdowns for binding the page to a specific
         *  position in the application sitemap hierarchy:
         *    App → Area → Node → Entity
         * ──────────────────────────────────────────────────────────── */}
        <section className="rounded-lg bg-white p-6 shadow-sm">
          <h2 className="mb-4 text-base font-semibold text-gray-800">
            Sitemap Configuration
          </h2>
          <p className="mb-4 text-sm text-gray-500">
            Optionally bind this page to an application sitemap location.
          </p>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* ── Application dropdown ──────────────────────────────── */}
            <div>
              <label htmlFor="page-app-id" className={LABEL_CLASSES}>
                Application
              </label>
              <select
                id="page-app-id"
                name="AppId"
                value={appId}
                onChange={(e) => handleAppChange(e.target.value)}
                disabled={appsLoading}
                className={SELECT_CLASSES}
              >
                <option value="">
                  {appsLoading ? 'Loading applications…' : '— None —'}
                </option>
                {apps.map((app) => (
                  <option key={app.id} value={app.id}>
                    {app.label || app.name}
                  </option>
                ))}
              </select>
              {hasFieldError(validation, 'appId') &&
                getFieldErrors(validation, 'appId').map((err, idx) => (
                  <p
                    key={`appId-err-${idx}`}
                    role="alert"
                    className={ERROR_CLASSES}
                  >
                    {err.message}
                  </p>
                ))}
            </div>

            {/* ── Area dropdown (cascaded from selected app) ──────── */}
            <div>
              <label htmlFor="page-area-id" className={LABEL_CLASSES}>
                Area
              </label>
              <select
                id="page-area-id"
                name="AreaId"
                value={areaId}
                onChange={(e) => handleAreaChange(e.target.value)}
                disabled={!appId || areas.length === 0}
                className={SELECT_CLASSES}
              >
                <option value="">
                  {!appId
                    ? 'Select an application first'
                    : areas.length === 0
                      ? 'No areas available'
                      : '— None —'}
                </option>
                {areas.map((area) => (
                  <option key={area.id} value={area.id}>
                    {area.label || area.name}
                  </option>
                ))}
              </select>
              {hasFieldError(validation, 'areaId') &&
                getFieldErrors(validation, 'areaId').map((err, idx) => (
                  <p
                    key={`areaId-err-${idx}`}
                    role="alert"
                    className={ERROR_CLASSES}
                  >
                    {err.message}
                  </p>
                ))}
            </div>

            {/* ── Node dropdown (cascaded from selected area) ─────── */}
            <div>
              <label htmlFor="page-node-id" className={LABEL_CLASSES}>
                Node
              </label>
              <select
                id="page-node-id"
                name="NodeId"
                value={nodeId}
                onChange={(e) => setNodeId(e.target.value)}
                disabled={!areaId || nodes.length === 0}
                className={SELECT_CLASSES}
              >
                <option value="">
                  {!areaId
                    ? 'Select an area first'
                    : nodes.length === 0
                      ? 'No nodes available'
                      : '— None —'}
                </option>
                {nodes.map((node) => (
                  <option key={node.id} value={node.id}>
                    {node.label || node.name}
                  </option>
                ))}
              </select>
              {hasFieldError(validation, 'nodeId') &&
                getFieldErrors(validation, 'nodeId').map((err, idx) => (
                  <p
                    key={`nodeId-err-${idx}`}
                    role="alert"
                    className={ERROR_CLASSES}
                  >
                    {err.message}
                  </p>
                ))}
            </div>

            {/* ── Entity dropdown ─────────────────────────────────────
             *  Populated from useEntities() hook. Relevant for entity-bound
             *  page types (RecordList, RecordCreate, RecordDetails, RecordManage).
             *  Replaces the entity dropdown in WvSdkPageSitemap ViewComponent.
             * ──────────────────────────────────────────────────────── */}
            <div>
              <label htmlFor="page-entity-id" className={LABEL_CLASSES}>
                Entity
                {isEntityBound && (
                  <span className="ms-1 text-xs font-normal text-gray-400">
                    (recommended for {PAGE_TYPE_OPTIONS.find((o) => o.value === pageType)?.label ?? ''} pages)
                  </span>
                )}
              </label>
              <select
                id="page-entity-id"
                name="EntityId"
                value={entityId}
                onChange={(e) => setEntityId(e.target.value)}
                disabled={entitiesLoading}
                className={SELECT_CLASSES}
              >
                <option value="">
                  {entitiesLoading ? 'Loading entities…' : '— None —'}
                </option>
                {entityList.map((entity) => (
                  <option key={entity.id} value={entity.id}>
                    {entity.label || entity.name}
                  </option>
                ))}
              </select>
              {hasFieldError(validation, 'entityId') &&
                getFieldErrors(validation, 'entityId').map((err, idx) => (
                  <p
                    key={`entityId-err-${idx}`}
                    role="alert"
                    className={ERROR_CLASSES}
                  >
                    {err.message}
                  </p>
                ))}
            </div>
          </div>
        </section>
      </DynamicForm>
    </div>
  );
}

export default PageCreate;
