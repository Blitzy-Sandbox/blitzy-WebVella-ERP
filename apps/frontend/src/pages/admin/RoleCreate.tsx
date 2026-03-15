/**
 * RoleCreate — Admin Role Creation Page
 *
 * React page replacing the monolith's Razor Page at:
 *   - `WebVella.Erp.Plugins.SDK/Pages/role/create.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/role/create.cshtml.cs`
 *
 * Route: `/admin/roles/create`
 *
 * Source behaviour preserved:
 *   - Two form fields: Name (text, required) and Description (textarea, required)
 *   - Creates a new role via POST /v1/identity/roles (replaces SecurityManager.SaveRole)
 *   - Navigates to role list on success (replaces Response.Redirect(ReturnUrl))
 *   - Displays validation errors on failure (replaces ValidationException catch block)
 *   - Red themed page header with plus icon (matches create.cshtml line 11)
 *   - "Create Role" submit button + "Cancel" link (matches create.cshtml lines 14-15)
 *
 * AAP compliance:
 *   - §0.4.3  — Full role CRUD via Identity service
 *   - §0.5.1  — SecurityManager.SaveRole → useCreateRole mutation
 *   - §0.8.1  — Self-contained SPA page with no server-side rendering
 *   - §0.8.6  — API calls via /v1/ prefixed endpoints
 *
 * @module pages/admin/RoleCreate
 */

import { useState, useCallback, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router-dom';

import { useCreateRole } from '../../hooks/useUsers';
import type { ErpRole } from '../../types/user';
import DynamicForm, { type FormValidation } from '../../components/forms/DynamicForm';

/**
 * Return path for cancel and post-creation navigation.
 * Replaces the monolith's `ReturnUrl` default of `/sdk/access/role/l/list`
 * (create.cshtml.cs line 27) with the new SPA route.
 */
const ROLE_LIST_PATH = '/admin/roles';

/**
 * RoleCreate — Page component for creating a new system role.
 *
 * Renders a form with Name and Description fields, a submit button that
 * triggers a TanStack Query mutation to POST /v1/identity/roles, and a
 * cancel link that navigates back to the role list.
 *
 * Field mapping from source:
 *   - `Name`        → controlled text input (create.cshtml line 25)
 *   - `Description` → controlled textarea   (create.cshtml line 30)
 *
 * Mutation mapping from source:
 *   - `SecurityManager.SaveRole(newRole)` → `useCreateRole().mutate(payload)`
 *   - `ValidationException` catch         → `FormValidation` error display
 *   - `Response.Redirect(ReturnUrl)`      → `navigate(ROLE_LIST_PATH)`
 */
function RoleCreate(): React.ReactNode {
  /* ── Controlled form field state (create.cshtml.cs lines 15-19) ── */
  const [name, setName] = useState<string>('');
  const [description, setDescription] = useState<string>('');
  const [showSuccess, setShowSuccess] = useState(false);

  /* ── Validation state (create.cshtml.cs lines 53-58) ─────────── */
  const [validation, setValidation] = useState<FormValidation>({
    errors: [],
  });

  /* ── Routing ─────────────────────────────────────────────────── */
  const navigate = useNavigate();

  /* ── TanStack Query mutation (replaces SecurityManager.SaveRole) */
  const createRole = useCreateRole();

  /**
   * Form submission handler.
   *
   * Replicates the OnPost method from create.cshtml.cs lines 33-60:
   *   1. Collects Name and Description from form state
   *   2. Calls the Identity service to create the role
   *   3. Navigates to role list on success
   *   4. Maps API errors to FormValidation state on failure
   */
  const handleSubmit = useCallback(
    (event: FormEvent<HTMLFormElement>) => {
      event.preventDefault();

      /* Client-side required validation (belt-and-suspenders with server) */
      const errors: FormValidation['errors'] = [];

      if (!name.trim()) {
        errors.push({ propertyName: 'Name', message: 'Name is required.' });
      }

      if (!description.trim()) {
        errors.push({
          propertyName: 'Description',
          message: 'Description is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({ errors });
        return;
      }

      /* Clear previous validation state before submitting */
      setValidation({ errors: [] });

      /* Build payload matching CreateRolePayload from useUsers.ts */
      const payload: Omit<ErpRole, 'id'> = {
        name: name.trim(),
        description: description.trim(),
      };

      createRole.mutate(payload, {
        onSuccess: () => {
          /* Show success briefly then redirect to role list (create.cshtml.cs line 51) */
          setShowSuccess(true);
          setTimeout(() => navigate(ROLE_LIST_PATH), 1500);
        },
        onError: (apiError) => {
          /*
           * Map API error to FormValidation (create.cshtml.cs lines 53-58).
           * The ApiError from the client wrapper contains `message` and
           * `errors` (ApiErrorItem[]) which map to ValidationException's
           * Message and Errors properties.
           */
          setValidation({
            message: apiError.message || 'An error occurred while creating the role.',
            errors: (apiError.errors ?? []).map((err) => ({
              propertyName: err.key || '',
              message: err.message || err.value || '',
            })),
          });
        },
      });
    },
    [name, description, createRole, navigate],
  );

  return (
    <div className="mx-auto w-full max-w-4xl">
      {showSuccess && (
        <div className="mb-4 rounded-md bg-green-50 p-4" role="status" aria-live="polite">
          <p className="text-sm font-medium text-green-800" data-testid="success-notification">Role created successfully. Redirecting…</p>
        </div>
      )}
      {/* ── Page Header (create.cshtml lines 11-17) ────────────────
       *  Red themed header with plus icon, matching the source's
       *  color="#dc3545" and icon-class="fa fa-plus".
       * ──────────────────────────────────────────────────────────── */}
      <header className="mb-6 flex flex-wrap items-center justify-between gap-4 rounded-lg border-s-4 border-red-600 bg-white p-4 shadow-sm">
        <div className="flex items-center gap-3">
          {/* Plus icon replacing fa fa-plus (create.cshtml line 12) */}
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
            {/* area-label="Role" → breadcrumb-style label */}
            <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
              Role
            </p>
            {/* title="New Role" (create.cshtml line 12) */}
            <h1 className="text-lg font-semibold text-gray-900">New Role</h1>
          </div>
        </div>

        {/* Action buttons (create.cshtml lines 13-16) */}
        <div className="flex items-center gap-2">
          {/* Submit button — "Create Role" (create.cshtml line 14) */}
          <button
            type="submit"
            form="CreateRecord"
            disabled={createRole.isPending}
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
            {createRole.isPending ? 'Creating…' : 'Create Role'}
          </button>

          {/* Cancel link (create.cshtml line 15) */}
          <Link
            to={ROLE_LIST_PATH}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-200 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
        </div>
      </header>

      {/* ── Form (create.cshtml lines 21-34) ───────────────────────
       *  DynamicForm replaces <wv-form id="CreateRecord" name="CreateRecord"
       *  label-mode="Stacked" mode="Form" autocomplete="false">
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
        {/* Section wrapper (create.cshtml line 22 — <wv-section class="mt-4">) */}
        <section className="rounded-lg bg-white p-6 shadow-sm">
          {/* ── Name field (create.cshtml line 25) ─────────────────
           *  <wv-field-text label-text="Name" value="@Model.Name"
           *   name="Name" required="true">
           * ────────────────────────────────────────────────────────── */}
          <div className="mb-4">
            <label
              htmlFor="role-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name
              <span className="text-red-600" aria-hidden="true"> *</span>
            </label>
            <input
              id="role-name"
                type="text"
              name="name"
              data-testid="role-name-input"
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
              autoComplete="off"
              aria-required="true"
              aria-invalid={
                validation.errors.some(
                  (err) => err.propertyName.toLowerCase() === 'name',
                )
                  ? 'true'
                  : undefined
              }
              aria-describedby={
                validation.errors.some(
                  (err) => err.propertyName.toLowerCase() === 'name',
                )
                  ? 'role-name-error'
                  : undefined
              }
              className="block w-full max-w-md rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus-visible:border-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-500"
              placeholder="Enter role name"
            />
            {/* Per-field inline error */}
            {validation.errors
              .filter((err) => err.propertyName.toLowerCase() === 'name')
              .map((err, idx) => (
                <p
                  key={`name-err-${idx}`}
                  id="role-name-error"
                  role="alert"
                  className="mt-1 text-sm text-red-600"
                >
                  {err.message}
                </p>
              ))}
          </div>

          {/* ── Description field (create.cshtml line 30) ──────────
           *  <wv-field-textarea label-text="Description"
           *   value="@Model.Description" name="Description" required="true">
           * ────────────────────────────────────────────────────────── */}
          <div>
            <label
              htmlFor="role-description"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Description
              <span className="text-red-600" aria-hidden="true"> *</span>
            </label>
            <textarea
              id="role-description"
              name="description"
              data-testid="role-description-input"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              required
              autoComplete="off"
              rows={4}
              aria-required="true"
              aria-invalid={
                validation.errors.some(
                  (err) => err.propertyName.toLowerCase() === 'description',
                )
                  ? 'true'
                  : undefined
              }
              aria-describedby={
                validation.errors.some(
                  (err) => err.propertyName.toLowerCase() === 'description',
                )
                  ? 'role-description-error'
                  : undefined
              }
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus-visible:border-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-500"
              placeholder="Enter role description"
            />
            {/* Per-field inline error */}
            {validation.errors
              .filter(
                (err) => err.propertyName.toLowerCase() === 'description',
              )
              .map((err, idx) => (
                <p
                  key={`desc-err-${idx}`}
                  id="role-description-error"
                  role="alert"
                  className="mt-1 text-sm text-red-600"
                >
                  {err.message}
                </p>
              ))}
          </div>
        </section>
      </DynamicForm>
    </div>
  );
}

export default RoleCreate;
