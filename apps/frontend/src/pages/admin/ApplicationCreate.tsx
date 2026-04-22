/**
 * ApplicationCreate — Application Creation Page
 *
 * React page replacing `WebVella.Erp.Plugins.SDK/Pages/application/create.cshtml[.cs]`.
 * Renders the application creation form at route `/admin/applications/create`.
 *
 * Source mapping:
 *   - create.cshtml.cs  CreateModel      → controlled form state + mutation
 *   - create.cshtml     wv-form          → DynamicForm with stacked labels
 *   - create.cshtml.cs  InitPage()       → useRoles() hook for role options
 *   - create.cshtml.cs  OnPost()         → useCreateApp().mutateAsync
 *   - create.cshtml     wv-validation    → DynamicForm validation prop
 *   - create.cshtml     wv-page-header   → Tailwind page header section
 *
 * Field mapping (preserving all 8 from source):
 *   - Name       → text input (required)
 *   - Label      → text input (required)
 *   - IconClass  → text input (CSS class)
 *   - Color      → color picker + hex text (default #2196F3)
 *   - Author     → text input
 *   - Weight     → integer number input (default 10)
 *   - Description → textarea
 *   - Access     → multiselect checkboxes for role IDs (default [AdministratorRoleId])
 *
 * @module pages/admin/ApplicationCreate
 */

import { useState, useCallback } from 'react';
import { useNavigate, Link } from 'react-router-dom';

import { useCreateApp } from '../../hooks/useApps';
import { useRoles } from '../../hooks/useUsers';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import type { App } from '../../types/app';
import type { ErpRole } from '../../types/user';

// ---------------------------------------------------------------------------
// Constants — System IDs (defined locally per dependency whitelist rules)
// ---------------------------------------------------------------------------

/**
 * Administrator role GUID from Definitions.cs SystemIds.AdministratorRoleId.
 * Defined locally to avoid importing from utils/constants.ts which is outside
 * the depends_on_files whitelist for this component.
 */
const ADMINISTRATOR_ROLE_ID = 'bdc56420-caf0-4030-8a0e-d264938e0cda';

/**
 * Default application colour matching the monolith's C# default:
 * `public string Color { get; set; } = "#2196F3";` (create.cshtml.cs line 34).
 */
const DEFAULT_COLOR = '#2196F3';

/**
 * Default application sort weight matching the monolith's C# default:
 * `public int Weight { get; set; } = 10;` (create.cshtml.cs line 37).
 */
const DEFAULT_WEIGHT = 10;

// ---------------------------------------------------------------------------
// Helper — Build FormValidation from API / mutation errors
// ---------------------------------------------------------------------------

/**
 * Converts a mutation error (either a plain `Error` or an API error envelope
 * with structured `errors` array) into a `FormValidation` object suitable for
 * the DynamicForm validation prop.
 *
 * Replaces the monolith's `catch (ValidationException ex)` pattern from
 * create.cshtml.cs OnPost() where `ex.Message` and `ex.Errors` were mapped
 * to the Validation model.
 *
 * @param err - The caught error from useCreateApp().mutateAsync
 * @returns FormValidation with message and per-field errors
 */
function buildValidationFromError(err: unknown): FormValidation {
  const result: FormValidation = {
    message: 'An error occurred while creating the application.',
    errors: [],
  };

  if (err instanceof Error) {
    result.message = err.message;
  }

  /* Attempt to extract structured errors from the API error envelope.
   * The api/client.ts interceptor may produce an ApiError shape with an
   * `errors` array of `{ key, value, message }` items. We map these to
   * ValidationError's { propertyName, message } shape. */
  if (typeof err === 'object' && err !== null && 'errors' in err) {
    const apiErr = err as {
      errors?: Array<{ key?: string; value?: string; message?: string }>;
    };
    if (Array.isArray(apiErr.errors)) {
      result.errors = apiErr.errors.map(
        (e): ValidationError => ({
          propertyName: e.key ?? '',
          message: e.message ?? '',
        }),
      );
    }
  }

  return result;
}

// ---------------------------------------------------------------------------
// ApplicationCreate Component
// ---------------------------------------------------------------------------

/**
 * Application creation page component.
 *
 * Renders a form with all 8 fields from the source create.cshtml page,
 * populated with default values matching the monolith's OnGet() behaviour:
 *   - Color: #2196F3
 *   - Weight: 10
 *   - Access: [AdministratorRoleId]
 *
 * The Access multiselect is populated from the Identity service's role list
 * via the useRoles() hook, replacing the monolith's
 * `SecurityManager().GetAllRoles().OrderBy(x => x.Name)` call from
 * create.cshtml.cs InitPage().
 *
 * On successful creation, navigates to the new application's detail page
 * at `/admin/applications/{id}`, replacing the monolith's
 * `Response.Redirect($"/sdk/objects/application/r/{appId}/")`.
 *
 * @returns JSX element rendering the application creation page
 */
function ApplicationCreate(): React.JSX.Element {
  const navigate = useNavigate();

  // ── TanStack Query — create mutation ────────────────────────
  // Replaces: AppService.CreateApplication() in create.cshtml.cs OnPost()
  const {
    mutateAsync,
    isPending,
    isError,
    error: mutationError,
    isSuccess,
    data: mutationData,
  } = useCreateApp();

  // ── TanStack Query — role list for Access multiselect ───────
  // Replaces: SecurityManager().GetAllRoles() in create.cshtml.cs InitPage()
  const { data: rolesResponse, isLoading: rolesLoading } = useRoles();

  // ── Form state (matches create.cshtml.cs BindProperty fields) ─

  /** Application machine-readable name (required). */
  const [name, setName] = useState('');

  /** Application human-readable label (required). */
  const [label, setLabel] = useState('');

  /** Application description text. */
  const [description, setDescription] = useState('');

  /** CSS icon class for the application (e.g. "fa fa-cog"). */
  const [iconClass, setIconClass] = useState('');

  /** Application author / owner name. */
  const [author, setAuthor] = useState('');

  /** Application accent colour — default #2196F3 (create.cshtml.cs line 34). */
  const [color, setColor] = useState(DEFAULT_COLOR);

  /** Display sort weight — default 10 (create.cshtml.cs line 37). */
  const [weight, setWeight] = useState(DEFAULT_WEIGHT);

  /**
   * Role IDs with access to the application.
   * Default: [AdministratorRoleId] — matches create.cshtml.cs OnGet() line 79:
   * `Access.Add(SystemIds.AdministratorRoleId.ToString());`
   */
  const [access, setAccess] = useState<string[]>([ADMINISTRATOR_ROLE_ID]);

  // ── Validation state ──────────────────────────────────────────
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  // ── Derived data ──────────────────────────────────────────────

  /** Sorted role options for the Access multiselect. */
  const roles: ErpRole[] = (rolesResponse?.object ?? []).slice().sort(
    (a: ErpRole, b: ErpRole) => a.name.localeCompare(b.name),
  );

  /**
   * Effective validation state for DynamicForm.
   * Prefers explicit validation state set by handleSubmit. Falls back to
   * deriving validation from the mutation's reactive isError/error state.
   */
  const displayValidation: FormValidation | undefined =
    validation ??
    (isError && mutationError
      ? buildValidationFromError(mutationError)
      : undefined);

  /**
   * Created application ID from the successful mutation.
   * Used as a safety fallback: if the imperative `navigate()` in
   * handleSubmit didn't fire, this value enables a "View Application"
   * link in the success banner.
   */
  const createdAppId: string | undefined =
    isSuccess && mutationData?.object?.id
      ? mutationData.object.id
      : undefined;

  // ── Access multiselect toggle handler ─────────────────────────
  const handleAccessChange = useCallback((roleId: string) => {
    setAccess((prev) =>
      prev.includes(roleId)
        ? prev.filter((id) => id !== roleId)
        : [...prev, roleId],
    );
  }, []);

  // ── Form submit handler ───────────────────────────────────────
  const handleSubmit = useCallback(async () => {
    // Client-side required field validation (matches source required=true attributes)
    const errors: ValidationError[] = [];

    if (!name.trim()) {
      errors.push({
        propertyName: 'Name',
        message: 'Name is required.',
      });
    }

    if (!label.trim()) {
      errors.push({
        propertyName: 'Label',
        message: 'Label is required.',
      });
    }

    if (errors.length > 0) {
      setValidation({
        message: 'Please correct the errors below.',
        errors,
      });
      return;
    }

    // Clear previous validation
    setValidation(undefined);

    try {
      // Build the create payload matching CreateAppVariables.app shape.
      // The server assigns a new GUID (replaces `var appId = Guid.NewGuid()`
      // from create.cshtml.cs line 98).
      const appPayload: Partial<App> & Pick<App, 'name' | 'label'> = {
        name: name.trim(),
        label: label.trim(),
        description: description.trim(),
        iconClass: iconClass.trim(),
        author: author.trim(),
        color,
        weight,
        access,
      };

      const result = await mutateAsync({ app: appPayload });

      // Navigate to the newly created application's detail page.
      // Replaces: return Redirect($"/sdk/objects/application/r/{appId}/");
      if (result?.object?.id) {
        navigate(`/admin/applications/${result.object.id}`);
      }
    } catch (err: unknown) {
      // Map server-side errors to FormValidation state.
      // Replaces: catch (ValidationException ex) { Validation.Message = ex.Message; ... }
      setValidation(buildValidationFromError(err));
    }
  }, [
    name,
    label,
    description,
    iconClass,
    author,
    color,
    weight,
    access,
    mutateAsync,
    navigate,
  ]);

  // ── Render ────────────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-5xl px-4 py-6">
      {/* ── Page Header ─────────────────────────────────────────
       * Replaces wv-page-header from create.cshtml lines 11-18.
       * color="#dc3545" → red accent, icon="fa fa-plus", title="Create New App"
       */}
      <header className="mb-6 flex flex-col gap-4 border-b border-gray-200 pb-4 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-3">
          <span
            className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg text-white"
            style={{ backgroundColor: '#dc3545' }}
            aria-hidden="true"
          >
            <i className="fa fa-plus" />
          </span>
          <div>
            <nav aria-label="Breadcrumb">
              <Link
                to="/admin/applications"
                className="text-sm text-gray-500 hover:text-gray-700 hover:underline"
              >
                Applications
              </Link>
            </nav>
            <h1 className="text-xl font-semibold text-gray-900">
              Create New App
            </h1>
          </div>
        </div>

        {/* Header actions — replaces wv-page-header-actions from create.cshtml lines 13-17 */}
        <div className="flex items-center gap-2">
          {/* Create App submit button — linked to form via form="CreateRecord" */}
          <button
            type="submit"
            form="CreateRecord"
            disabled={isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? (
              <span
                className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                role="status"
                aria-label="Creating application"
              />
            ) : (
              <i className="fa fa-save" aria-hidden="true" />
            )}
            <span>{isPending ? 'Creating…' : 'Create App'}</span>
          </button>

          {/* Cancel link — replaces <a href='{ReturnUrl}' class='btn btn-white btn-sm'>Cancel</a> */}
          <Link
            to="/admin/applications"
            className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
          >
            Cancel
          </Link>
        </div>
      </header>

      {/* ── Success Fallback Banner ─────────────────────────────
       * Displays if the mutation succeeded but imperative navigate didn't fire.
       * Uses isSuccess + data (mutationData) from useCreateApp.
       */}
      {createdAppId && (
        <div
          role="status"
          className="mb-4 rounded-lg border border-green-200 bg-green-50 p-4 text-sm text-green-800"
        >
          Application created successfully.{' '}
          <Link
            to={`/admin/applications/${createdAppId}`}
            className="font-medium underline hover:text-green-900"
          >
            View Application
          </Link>
        </div>
      )}

      {/* ── DynamicForm ─────────────────────────────────────────
       * Replaces <wv-form id="CreateRecord" name="CreateRecord"
       *   label-mode="Stacked" mode="Form"> from create.cshtml line 23.
       * The validation prop passes FormValidation to the built-in
       * ValidationSummary (replaces wv-validation from line 21).
       */}
      <DynamicForm
        id="CreateRecord"
        name="CreateRecord"
        labelMode="stacked"
        fieldMode="form"
        validation={displayValidation}
        onSubmit={() => {
          void handleSubmit();
        }}
      >
        {/* Section — replaces wv-section class="mt-4" from create.cshtml line 24 */}
        <section className="mt-4 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          {/* ── Row 1: Name | Label ──────────────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Name field — required text input
             * Replaces: <wv-field-text label-text="Name" name="Name" required="true">
             * create.cshtml line 27 */}
            <div>
              <label
                htmlFor="field-name"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Name{' '}
                <span className="text-red-500" aria-label="required">
                  *
                </span>
              </label>
              <input
                id="field-name"
                name="Name"
                type="text"
                required
                value={name}
                onChange={(e) => setName(e.target.value)}
                autoComplete="off"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="Enter application name"
              />
            </div>

            {/* Label field — required text input
             * Replaces: <wv-field-text label-text="Label" name="Label" required="true">
             * create.cshtml line 30 */}
            <div>
              <label
                htmlFor="field-label"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Label{' '}
                <span className="text-red-500" aria-label="required">
                  *
                </span>
              </label>
              <input
                id="field-label"
                name="Label"
                type="text"
                required
                value={label}
                onChange={(e) => setLabel(e.target.value)}
                autoComplete="off"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="Enter application label"
              />
            </div>
          </div>

          {/* ── Row 2: IconClass | Color ─────────────────────── */}
          <div className="mt-6 grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* IconClass field — text input
             * Replaces: <wv-field-text label-text="Icon CSS Class" name="IconClass">
             * create.cshtml line 35 */}
            <div>
              <label
                htmlFor="field-icon-class"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Icon CSS Class
              </label>
              <input
                id="field-icon-class"
                name="IconClass"
                type="text"
                value={iconClass}
                onChange={(e) => setIconClass(e.target.value)}
                autoComplete="off"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="e.g. fa fa-cog"
              />
            </div>

            {/* Color field — color picker + hex text input
             * Replaces: <wv-field-color label-text="Color" name="Color">
             * create.cshtml line 38 */}
            <div>
              <label
                htmlFor="field-color"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Color
              </label>
              <div className="flex items-center gap-3">
                <input
                  id="field-color"
                  name="Color"
                  type="color"
                  value={color}
                  onChange={(e) => setColor(e.target.value)}
                  className="h-10 w-14 shrink-0 cursor-pointer rounded-md border border-gray-300 p-1"
                />
                <input
                  type="text"
                  value={color}
                  onChange={(e) => setColor(e.target.value)}
                  aria-label="Color hex value"
                  maxLength={7}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  placeholder="#2196F3"
                />
              </div>
            </div>
          </div>

          {/* ── Row 3: Author | Weight ───────────────────────── */}
          <div className="mt-6 grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Author field — text input
             * Replaces: <wv-field-text label-text="Author" name="Author">
             * create.cshtml line 43 */}
            <div>
              <label
                htmlFor="field-author"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Author
              </label>
              <input
                id="field-author"
                name="Author"
                type="text"
                value={author}
                onChange={(e) => setAuthor(e.target.value)}
                autoComplete="off"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="Enter author name"
              />
            </div>

            {/* Weight field — integer number input (decimal-digits=0)
             * Replaces: <wv-field-number label-text="Weight" decimal-digits="0" name="Weight">
             * create.cshtml line 46 */}
            <div>
              <label
                htmlFor="field-weight"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Weight
              </label>
              <input
                id="field-weight"
                name="Weight"
                type="number"
                step="1"
                min="0"
                value={weight}
                onChange={(e) => {
                  const parsed = parseInt(e.target.value, 10);
                  setWeight(Number.isNaN(parsed) ? 0 : parsed);
                }}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
          </div>

          {/* ── Row 4: Description (full width) ──────────────── */}
          <div className="mt-6">
            {/* Description field — textarea
             * Replaces: <wv-field-textarea label-text="Description" name="Description">
             * create.cshtml line 51 */}
            <label
              htmlFor="field-description"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <textarea
              id="field-description"
              name="Description"
              rows={4}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="Enter application description"
            />
          </div>

          {/* ── Row 5: Access multiselect (full width) ───────── */}
          <fieldset className="mt-6">
            {/* Access multiselect — checkbox list of roles
             * Replaces: <wv-field-multiselect label-text="Access" name="Access"
             *   options="Model.RoleOptions.ToWvSelectOption()">
             * create.cshtml line 56
             *
             * Roles are loaded via useRoles() → GET /v1/identity/roles,
             * replacing SecurityManager().GetAllRoles().OrderBy(x => x.Name)
             * from create.cshtml.cs InitPage(). */}
            <legend className="mb-1 block text-sm font-medium text-gray-700">
              Access
            </legend>

            {rolesLoading ? (
              <div className="flex items-center gap-2 rounded-md border border-gray-300 p-3">
                <span
                  className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-gray-400 border-t-transparent"
                  role="status"
                  aria-label="Loading roles"
                />
                <span className="text-sm text-gray-500">Loading roles…</span>
              </div>
            ) : (
              <div className="rounded-md border border-gray-300 p-3">
                {roles.length === 0 ? (
                  <p className="text-sm text-gray-500">No roles available.</p>
                ) : (
                  <div className="flex flex-wrap gap-3">
                    {roles.map((role: ErpRole) => (
                      <label
                        key={role.id}
                        className="inline-flex cursor-pointer items-center gap-2 rounded-md border border-gray-200 px-3 py-2 text-sm transition-colors hover:bg-gray-50"
                      >
                        <input
                          type="checkbox"
                          name="Access"
                          value={role.id}
                          checked={access.includes(role.id)}
                          onChange={() => handleAccessChange(role.id)}
                          className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                        />
                        <span className="text-gray-700">{role.name}</span>
                      </label>
                    ))}
                  </div>
                )}
              </div>
            )}
          </fieldset>
        </section>
      </DynamicForm>
    </div>
  );
}

export default ApplicationCreate;
