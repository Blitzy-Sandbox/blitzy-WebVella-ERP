/**
 * AdminEntityCreate — React page component for creating new entity definitions.
 *
 * Route: /admin/entities/create
 *
 * Replaces:
 * - WebVella.Erp.Plugins.SDK/Pages/entity/create.cshtml  (Razor view)
 * - WebVella.Erp.Plugins.SDK/Pages/entity/create.cshtml.cs (PageModel)
 *
 * The page provides:
 * 1. Entity metadata fields: Name (required), Id (optional GUID), Label,
 *    LabelPlural, System (boolean), Color (color picker), IconName
 * 2. CRUD × Roles permission grid: a checkbox matrix where rows are CRUD
 *    operations (Create, Read, Update, Delete) and columns are system roles
 * 3. Form validation with error summary via DynamicForm
 * 4. Create mutation via Entity Management API (POST /v1/entities)
 * 5. Navigation: success → entity details page, cancel → entity list
 *
 * @module pages/admin/AdminEntityCreate
 */

import { useState, useCallback, useMemo } from 'react';
import { useNavigate, Link } from 'react-router';
import { useCreateEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type { InputEntity, RecordPermissions } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import type { ApiError } from '../../api/client';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * CRUD operation definitions for the entity permission grid.
 * Each entry maps to a key in RecordPermissions and is rendered as a row
 * in the permissions matrix with one column per system role.
 *
 * Replaces PermissionOptions from create.cshtml.cs InitPage():
 *   PermissionOptions = new List<string>() { "create", "read", "update", "delete" };
 */
const PERMISSION_OPERATIONS: ReadonlyArray<{
  readonly key: keyof RecordPermissions;
  readonly label: string;
}> = [
  { key: 'canCreate', label: 'Create' },
  { key: 'canRead', label: 'Read' },
  { key: 'canUpdate', label: 'Update' },
  { key: 'canDelete', label: 'Delete' },
] as const;

/**
 * Default empty record permissions.
 * All CRUD arrays start empty — no roles granted any permission by default,
 * matching the monolith's initial empty PermissionGrid state in InitPage().
 */
const EMPTY_PERMISSIONS: Readonly<RecordPermissions> = Object.freeze({
  canCreate: [],
  canRead: [],
  canUpdate: [],
  canDelete: [],
});

/** Default colour value for the entity colour picker. */
const DEFAULT_COLOR = '#ffffff';

// ---------------------------------------------------------------------------
// Helper Functions
// ---------------------------------------------------------------------------

/**
 * Type guard that determines whether an unknown caught error is an ApiError
 * produced by the Axios response interceptor in api/client.ts.
 *
 * The interceptor always constructs objects with `message`, `errors`, and
 * `status` properties — both for envelope-level failures (HTTP 200 with
 * `success: false`) and HTTP-level errors (4xx / 5xx).
 */
function isApiError(err: unknown): err is ApiError {
  if (err == null || typeof err !== 'object') return false;
  const candidate = err as Record<string, unknown>;
  return (
    typeof candidate.message === 'string' &&
    Array.isArray(candidate.errors) &&
    typeof candidate.status === 'number'
  );
}

/**
 * Maps a caught mutation error to the FormValidation shape consumed by
 * DynamicForm's validation summary.
 *
 * Replaces the monolith's `catch (ValidationException ex)` pattern from
 * create.cshtml.cs OnPost(), where validation errors were unpacked and
 * rendered via the `wv-validation` tag helper.
 *
 * @param err - The caught error (ApiError from interceptor, or plain Error)
 * @returns A FormValidation object for DynamicForm display
 */
function mapErrorToValidation(err: unknown): FormValidation {
  if (isApiError(err)) {
    const errors: ValidationError[] = err.errors.map((apiItem) => ({
      propertyName: apiItem.key ?? '',
      message: apiItem.message ?? '',
    }));
    return {
      message: err.message || 'Validation failed',
      errors,
    };
  }

  // Fallback for plain Error objects (from assertApiSuccess or network errors)
  return {
    message: (err instanceof Error ? err.message : String(err)) || 'An unexpected error occurred',
    errors: [],
  };
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Entity creation page rendered at `/admin/entities/create`.
 *
 * Lifecycle mirrors the monolith's create.cshtml.cs:
 * - InitPage() → useRoles() fetches roles for the permission grid on mount
 * - OnPost()  → handleSubmit() validates, calls useCreateEntity mutation,
 *                navigates to entity details on success or shows errors
 */
function AdminEntityCreate(): React.JSX.Element {
  const navigate = useNavigate();

  // ------------------------------------------------------------------
  // TanStack Query hooks
  // ------------------------------------------------------------------

  const {
    mutateAsync,
    isPending,
    isError,
    error: mutationError,
    isSuccess,
    data: createdEntity,
    reset,
  } = useCreateEntity();

  const { data: rolesData, isLoading: rolesLoading } = useRoles();

  // ------------------------------------------------------------------
  // Derived data
  // ------------------------------------------------------------------

  /**
   * Flattened roles array from the API response envelope.
   * Replaces AdminPageUtils.GetUserRoles() → SecurityManager.GetAllRoles()
   */
  const roles = useMemo<ErpRole[]>(
    () => rolesData?.object ?? [],
    [rolesData],
  );

  // ------------------------------------------------------------------
  // Form field state
  // ------------------------------------------------------------------

  const [name, setName] = useState('');
  const [entityId, setEntityId] = useState('');
  const [label, setLabel] = useState('');
  const [labelPlural, setLabelPlural] = useState('');
  const [system, setSystem] = useState(false);
  const [color, setColor] = useState(DEFAULT_COLOR);
  const [iconName, setIconName] = useState('');

  /** Permission grid state — CRUD × Roles checkbox matrix. */
  const [permissions, setPermissions] = useState<RecordPermissions>(
    () => ({
      canCreate: [...EMPTY_PERMISSIONS.canCreate],
      canRead: [...EMPTY_PERMISSIONS.canRead],
      canUpdate: [...EMPTY_PERMISSIONS.canUpdate],
      canDelete: [...EMPTY_PERMISSIONS.canDelete],
    }),
  );

  /** Client-side validation state for inline error display (e.g. empty name). */
  const [clientValidation, setClientValidation] = useState<{
    message: string;
    errors: { propertyName: string; message: string }[];
  } | null>(null);

  // ------------------------------------------------------------------
  // Validation state
  // ------------------------------------------------------------------

  /**
   * Derives FormValidation from the mutation error for DynamicForm display.
   * When isError is true and mutationError exists, the error is mapped to
   * FormValidation. Clears automatically when the mutation is reset or
   * succeeds.
   */
  const validation: FormValidation | undefined = useMemo(() => {
    if (isError && mutationError) {
      return mapErrorToValidation(mutationError);
    }
    // Clear validation on success or when no error exists
    if (isSuccess && createdEntity) {
      return undefined;
    }
    return undefined;
  }, [isError, mutationError, isSuccess, createdEntity]);

  // ------------------------------------------------------------------
  // Handlers
  // ------------------------------------------------------------------

  /**
   * Toggles a single permission checkbox in the CRUD × Roles grid.
   *
   * Adds the role ID to the operation's array if currently unchecked,
   * or removes it if currently checked. This mirrors the monolith's
   * JavaScript toggle logic in the permission grid checkboxes.
   *
   * @param operation - The CRUD operation key (canCreate, canRead, etc.)
   * @param roleId   - The role identifier to toggle
   */
  const handlePermissionToggle = useCallback(
    (operation: keyof RecordPermissions, roleId: string) => {
      setPermissions((prev) => {
        const current = prev[operation];
        const isChecked = current.includes(roleId);
        return {
          ...prev,
          [operation]: isChecked
            ? current.filter((rid) => rid !== roleId)
            : [...current, roleId],
        };
      });
    },
    [],
  );

  /**
   * Handles the entity creation form submission.
   *
   * Replaces create.cshtml.cs OnPost():
   * 1. Resets previous mutation state to clear stale errors
   * 2. Constructs InputEntity payload from form state
   * 3. Calls Entity Management API via useCreateEntity.mutateAsync
   * 4. On success, navigates to /admin/entities/{entityId}
   * 5. On failure, errors surface via the validation useMemo → DynamicForm
   */
  const handleSubmit = useCallback(async () => {
    // Clear any previous mutation state before re-submitting
    reset();

    // Client-side validation: name is required, must be lowercase without spaces
    // Matches EntityManager.CreateEntity() validation from the monolith.
    const trimmedName = name.trim();
    if (!trimmedName) {
      setClientValidation({
        message: 'Name is required',
        errors: [{ propertyName: 'Name', message: 'Name is required.' }],
      });
      return;
    }
    if (trimmedName !== trimmedName.toLowerCase() || trimmedName.includes(' ')) {
      setClientValidation({
        message: 'Entity name must be lowercase and contain no spaces',
        errors: [{ propertyName: 'Name', message: 'Entity name must be lowercase and contain no spaces.' }],
      });
      return;
    }
    setClientValidation(null);

    const input: InputEntity = {
      name: name.trim(),
      label: label.trim(),
      labelPlural: labelPlural.trim(),
      system,
      iconName: iconName.trim(),
      color: color.trim(),
      recordPermissions: permissions,
    };

    // Only include id when the user explicitly provided one (optional GUID).
    // Matches create.cshtml.cs: if (new Guid(Id) != Guid.Empty) input.Id = new Guid(Id);
    const trimmedId = entityId.trim();
    if (trimmedId.length > 0) {
      input.id = trimmedId;
    }

    try {
      const result = await mutateAsync(input);
      // Navigate to the newly created entity's detail page after a brief delay
      // to allow the success message to render visibly for users and E2E tests.
      // Replaces: return Redirect($"/sdk/objects/entity/r/{createdEntity.Id}/");
      if (result?.id) {
        setTimeout(() => navigate(`/admin/entities/${result.id}`), 1500);
      }
    } catch {
      // Error is captured by the mutation hook and surfaced via the
      // `validation` useMemo → DynamicForm's validation summary.
      // No explicit handling needed here.
    }
  }, [
    name, entityId, label, labelPlural, system, iconName, color,
    permissions, mutateAsync, navigate, reset,
  ]);

  /**
   * Synchronous wrapper for the async submit handler.
   * Passed to DynamicForm's onSubmit prop and the header button onClick.
   */
  const handleFormSubmit = useCallback(() => {
    void handleSubmit();
  }, [handleSubmit]);

  // ------------------------------------------------------------------
  // Render
  // ------------------------------------------------------------------

  return (
    <div className="mx-auto max-w-screen-xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page Header ─────────────────────────────────────────── */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          {/* Red accent icon matching the monolith's #dc3545 + fa-plus */}
          <span
            className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-red-600 text-white"
            aria-hidden="true"
          >
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-5 w-5"
            >
              <path d="M10.75 4.75a.75.75 0 0 0-1.5 0v4.5h-4.5a.75.75 0 0 0 0 1.5h4.5v4.5a.75.75 0 0 0 1.5 0v-4.5h4.5a.75.75 0 0 0 0-1.5h-4.5v-4.5Z" />
            </svg>
          </span>
          <h1 className="text-2xl font-semibold text-gray-900">
            Create New Entity
          </h1>
        </div>

        {/* Header action buttons */}
        <div className="flex items-center gap-3">
          <Link
            to="/admin/entities"
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            Cancel
          </Link>
          <button
            type="button"
            disabled={isPending}
            onClick={handleFormSubmit}
            className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? (
              <>
                <svg
                  className="-ml-0.5 mr-1.5 h-4 w-4 animate-spin"
                  xmlns="http://www.w3.org/2000/svg"
                  fill="none"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <circle
                    className="opacity-25"
                    cx="12"
                    cy="12"
                    r="10"
                    stroke="currentColor"
                    strokeWidth="4"
                  />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                  />
                </svg>
                Creating&hellip;
              </>
            ) : (
              'Create Entity'
            )}
          </button>
        </div>
      </div>

      {/* ── Success Indicator ───────────────────────────────────── */}
      {isSuccess && createdEntity && (
        <div
          className="mb-4 rounded-md bg-green-50 p-4"
          role="status"
          aria-live="polite"
        >
          <p className="text-sm font-medium text-green-800">
            Entity created successfully. Redirecting&hellip;
          </p>
        </div>
      )}

      {/* ── Client-side Validation Errors ────────────────────────── */}
      {clientValidation && (
        <p
          className="mb-4 rounded-md bg-red-50 p-4 text-sm font-medium text-red-800"
          role="alert"
          data-testid="validation-error"
        >
          {clientValidation.message}
        </p>
      )}

      {/* ── Entity Creation Form ────────────────────────────────── */}
      <DynamicForm
        id="CreateEntity"
        name="CreateEntity"
        labelMode="stacked"
        fieldMode="form"
        showValidation={!!validation}
        validation={validation}
        onSubmit={handleFormSubmit}
        className="rounded-lg border border-gray-200 bg-white shadow-sm"
      >
        <div className="space-y-6 p-6">
          {/* ── Row 1: Name + Id ──────────────────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Entity Name — required, unique system name */}
            <div>
              <label
                htmlFor="entity-name"
                className="block text-sm font-medium text-gray-700"
              >
                Name{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                id="entity-name"
                name="name"
                type="text"
                required
                value={name}
                onChange={(e) => { setName(e.target.value); }}
                placeholder="e.g. customer_order"
                autoComplete="off"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
              <p className="mt-1 text-xs text-gray-500">
                Unique system identifier using only a-z characters and
                underscores. Cannot be changed after creation.
              </p>
            </div>

            {/* Entity Id — optional GUID override */}
            <div>
              <label
                htmlFor="entity-id"
                className="block text-sm font-medium text-gray-700"
              >
                Id
              </label>
              <input
                id="entity-id"
                name="entityId"
                type="text"
                value={entityId}
                onChange={(e) => { setEntityId(e.target.value); }}
                placeholder="Auto-generated if empty"
                autoComplete="off"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
              <p className="mt-1 text-xs text-gray-500">
                Optional GUID. Leave empty to auto-generate.
              </p>
            </div>
          </div>

          {/* ── Row 2: System checkbox ────────────────────────── */}
          <div>
            <div className="flex items-center gap-2">
              <input
                id="entity-system"
                type="checkbox"
                checked={system}
                onChange={(e) => { setSystem(e.target.checked); }}
                className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
              />
              <label
                htmlFor="entity-system"
                className="text-sm font-medium text-gray-700"
              >
                System Entity
              </label>
            </div>
            <p className="mt-1 text-xs text-gray-500">
              System entities cannot be deleted by non-admin users.
            </p>
          </div>

          {/* ── Row 3: Label + Label Plural ───────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            <div>
              <label
                htmlFor="entity-label"
                className="block text-sm font-medium text-gray-700"
              >
                Label
              </label>
              <input
                id="entity-label"
                name="label"
                type="text"
                value={label}
                onChange={(e) => { setLabel(e.target.value); }}
                placeholder="e.g. Customer Order"
                autoComplete="off"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
            </div>

            <div>
              <label
                htmlFor="entity-label-plural"
                className="block text-sm font-medium text-gray-700"
              >
                Label Plural
              </label>
              <input
                id="entity-label-plural"
                name="labelPlural"
                type="text"
                value={labelPlural}
                onChange={(e) => { setLabelPlural(e.target.value); }}
                placeholder="e.g. Customer Orders"
                autoComplete="off"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
            </div>
          </div>

          {/* ── Row 4: Color + Icon Name ──────────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            {/* Colour picker — replaces wv-field-color from create.cshtml */}
            <div>
              <label
                htmlFor="entity-color"
                className="block text-sm font-medium text-gray-700"
              >
                Color
              </label>
              <div className="mt-1 flex items-center gap-3">
                <input
                  id="entity-color"
                name="color"
                  type="color"
                  value={color}
                  onChange={(e) => { setColor(e.target.value); }}
                  className="h-10 w-14 shrink-0 cursor-pointer rounded-md border border-gray-300 p-1"
                />
                <input
                  type="text"
                  value={color}
                  onChange={(e) => { setColor(e.target.value); }}
                  aria-label="Color hex value"
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                />
              </div>
            </div>

            {/* Icon name — replaces wv-field-icon from create.cshtml */}
            <div>
              <label
                htmlFor="entity-icon"
                className="block text-sm font-medium text-gray-700"
              >
                Icon Name
              </label>
              <input
                id="entity-icon"
                name="iconName"
                type="text"
                value={iconName}
                onChange={(e) => { setIconName(e.target.value); }}
                placeholder='e.g. fa fa-database'
                autoComplete="off"
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
              <p className="mt-1 text-xs text-gray-500">
                Font Awesome icon class (e.g.&nbsp;
                <code className="rounded bg-gray-100 px-1 py-0.5 text-xs">
                  fa fa-database
                </code>
                ).
              </p>
            </div>
          </div>

          {/* Divider between fields and permission grid */}
          <hr className="border-gray-200" />

          {/* ── Row 5: Record Permissions Grid (CRUD × Roles) ── */}
          <div>
            <h2 className="mb-3 text-lg font-medium text-gray-900">
              Record Permissions
            </h2>
            <p className="mb-4 text-sm text-gray-500">
              Configure which roles can perform each CRUD operation on records
              of this entity.
            </p>

            {rolesLoading ? (
              /* Loading spinner while roles are being fetched */
              <div
                className="flex items-center justify-center py-8"
                role="status"
                aria-label="Loading roles"
              >
                <svg
                  className="h-6 w-6 animate-spin text-indigo-600"
                  xmlns="http://www.w3.org/2000/svg"
                  fill="none"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <circle
                    className="opacity-25"
                    cx="12"
                    cy="12"
                    r="10"
                    stroke="currentColor"
                    strokeWidth="4"
                  />
                  <path
                    className="opacity-75"
                    fill="currentColor"
                    d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                  />
                </svg>
                <span className="sr-only">Loading roles&hellip;</span>
              </div>
            ) : roles.length === 0 ? (
              /* Empty state — no roles in the system */
              <p className="py-4 text-sm text-gray-500">
                No roles found. Permissions cannot be configured without system
                roles.
              </p>
            ) : (
              /* Permission matrix table */
              <div className="overflow-x-auto rounded-md border border-gray-200">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th
                        scope="col"
                        className="px-4 py-3 text-start text-xs font-medium uppercase tracking-wider text-gray-500"
                      >
                        Operation
                      </th>
                      {roles.map((role: ErpRole) => (
                        <th
                          key={role.id}
                          scope="col"
                          className="px-4 py-3 text-center text-xs font-medium uppercase tracking-wider text-gray-500"
                        >
                          {role.name}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-200 bg-white">
                    {PERMISSION_OPERATIONS.map((op) => (
                      <tr key={op.key}>
                        <td className="whitespace-nowrap px-4 py-3 text-sm font-medium text-gray-900">
                          {op.label}
                        </td>
                        {roles.map((role: ErpRole) => {
                          const isChecked = permissions[op.key].includes(
                            role.id,
                          );
                          const checkboxId = `perm-${op.key}-${role.id}`;
                          return (
                            <td
                              key={role.id}
                              className="px-4 py-3 text-center"
                            >
                              <input
                                id={checkboxId}
                                type="checkbox"
                                checked={isChecked}
                                onChange={() => {
                                  handlePermissionToggle(op.key, role.id);
                                }}
                                aria-label={`${op.label} permission for ${role.name}`}
                                className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
                              />
                            </td>
                          );
                        })}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </div>

        {/* ── Form Footer ─────────────────────────────────────── */}
        <div className="flex items-center justify-end gap-3 rounded-b-lg border-t border-gray-200 bg-gray-50 px-6 py-4">
          <Link
            to="/admin/entities"
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
          >
            Cancel
          </Link>
          <button
            type="submit"
            disabled={isPending}
            className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isPending ? 'Creating\u2026' : 'Create Entity'}
          </button>
        </div>
      </DynamicForm>
    </div>
  );
}

export default AdminEntityCreate;
