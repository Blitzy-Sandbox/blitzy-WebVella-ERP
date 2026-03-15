import { useState, useEffect, useCallback } from 'react';
import type { FormEvent } from 'react';
import { useParams, useNavigate, Link } from 'react-router';
import { useRole, useUpdateRole } from '../../hooks/useUsers';
import type { ErpRole } from '../../types/user';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation } from '../../components/forms/DynamicForm';

/**
 * RoleManage — Edit Role Page
 *
 * Route: /admin/roles/:roleId/manage
 *
 * Replaces the monolith's WebVella.Erp.Plugins.SDK/Pages/role/manage.cshtml[.cs].
 * Loads a role by ID from the Identity service, pre-populates the Name and
 * Description fields, and saves changes via PUT /v1/identity/roles/:id.
 *
 * Source behaviour preserved:
 *  - EQL `select * from role where id = '{RecordId}'` → useRole(roleId)
 *  - SecurityManager.SaveRole(role) → useUpdateRole() mutation
 *  - NotFound when role is missing → inline error with back-link
 *  - ValidationException → FormValidation error display
 */
function RoleManage(): React.JSX.Element {
  const { roleId } = useParams<{ roleId: string }>();
  const navigate = useNavigate();

  /* ─── Server State ────────────────────────────────────────────── */
  const {
    data: roleData,
    isLoading: isRoleLoading,
    isError: isRoleFetchError,
    error: roleFetchError,
  } = useRole(roleId);

  const updateRoleMutation = useUpdateRole();

  /* ─── Local Form State ────────────────────────────────────────── */
  const [name, setName] = useState<string>('');
  const [description, setDescription] = useState<string>('');
  const [showSuccess, setShowSuccess] = useState(false);
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* ─── Pre-populate form when role data arrives ────────────────── */
  useEffect(() => {
    if (!roleData) return;

    /*
     * useRole returns ApiResponse<ErpRole>; the role entity lives in `.object`.
     */
    const role: ErpRole | undefined = roleData.object;

    if (role) {
      setName(role.name ?? '');
      setDescription(role.description ?? '');
    }
  }, [roleData]);

  /* ─── Form Submission Handler ─────────────────────────────────── */
  const handleSubmit = useCallback(
    async (e: FormEvent) => {
      e.preventDefault();
      setValidation(undefined);

      /* Client-side required-field validation */
      const errors: Array<{ propertyName: string; message: string }> = [];

      if (!name.trim()) {
        errors.push({ propertyName: 'name', message: 'Name is required.' });
      }
      if (!description.trim()) {
        errors.push({
          propertyName: 'description',
          message: 'Description is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({
          message: 'Please correct the errors below.',
          errors,
        });
        return;
      }

      if (!roleId) return;

      try {
        await updateRoleMutation.mutateAsync({
          id: roleId,
          name: name.trim(),
          description: description.trim(),
        });

        /* Show success briefly then navigate to role details */
        setShowSuccess(true);
        setTimeout(() => navigate(`/admin/roles/${roleId}`, { replace: true }), 1500);
      } catch (mutationError: unknown) {
        /*
         * Map the API error envelope to FormValidation so DynamicForm
         * can render the validation summary and per-field messages.
         */
        const apiErr = mutationError as {
          message?: string;
          errors?: Array<{
            key?: string;
            message?: string;
            propertyName?: string;
          }>;
        };

        const mappedErrors: Array<{
          propertyName: string;
          message: string;
        }> = [];

        if (apiErr?.errors && Array.isArray(apiErr.errors)) {
          for (const item of apiErr.errors) {
            mappedErrors.push({
              propertyName: item.propertyName ?? item.key ?? '',
              message: item.message ?? 'Validation error.',
            });
          }
        }

        setValidation({
          message:
            apiErr?.message ?? 'An error occurred while saving the role.',
          errors: mappedErrors,
        });
      }
    },
    [roleId, name, description, updateRoleMutation, navigate],
  );

  /* ═══════════════════════════════════════════════════════════════
   * Render — Loading
   * ═══════════════════════════════════════════════════════════════ */
  if (isRoleLoading) {
    return (
      <div
        className="flex min-h-[400px] items-center justify-center"
        role="status"
        aria-label="Loading role"
      >
        <div className="flex items-center gap-2 text-gray-500">
          <svg
            className="h-5 w-5 animate-spin"
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
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
            />
          </svg>
          <span>Loading role…</span>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
   * Render — Not Found / Fetch Error
   * ═══════════════════════════════════════════════════════════════ */
  const resolvedRole: ErpRole | undefined = roleData?.object;

  if (isRoleFetchError || !resolvedRole) {
    const errorMessage =
      roleFetchError != null &&
      typeof roleFetchError === 'object' &&
      'message' in roleFetchError
        ? String((roleFetchError as { message: unknown }).message)
        : 'The requested role could not be found or an error occurred.';

    return (
      <div className="p-6">
        <div
          className="rounded-md border border-red-200 bg-red-50 p-4 text-red-700"
          role="alert"
        >
          <p className="font-semibold">Role not found</p>
          <p className="mt-1 text-sm">{errorMessage}</p>
          <Link
            to="/admin/roles"
            className="mt-3 inline-block text-sm font-medium text-red-700 underline hover:text-red-900"
          >
            ← Back to Roles
          </Link>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
   * Render — Main Content
   * ═══════════════════════════════════════════════════════════════ */
  return (
    <div className="p-6">
      {showSuccess && (
        <div className="mb-4 rounded-md bg-green-50 p-4" role="status" aria-live="polite">
          <p className="text-sm font-medium text-green-800" data-testid="success-notification">Role saved successfully. Redirecting…</p>
        </div>
      )}
      {/* ── Page Header ──────────────────────────────────────────── */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          {/* Red-themed icon badge (matches source #dc3545 / fa-key) */}
          <div
            className="flex h-10 w-10 shrink-0 items-center justify-center rounded"
            style={{ backgroundColor: '#dc3545' }}
            aria-hidden="true"
          >
            <svg
              className="h-5 w-5 text-white"
              fill="currentColor"
              viewBox="0 0 16 16"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path d="M0 8a4 4 0 0 1 7.465-2H14a.5.5 0 0 1 .354.146l1.5 1.5a.5.5 0 0 1 0 .708l-1.5 1.5a.5.5 0 0 1-.708 0L13 9.207l-.646.647a.5.5 0 0 1-.708 0L11 9.207l-.646.647a.5.5 0 0 1-.708 0L9 9.207l-.646.647A.5.5 0 0 1 8 10h-.535A4 4 0 0 1 0 8zm4-3a3 3 0 1 0 2.712 4.285A.5.5 0 0 1 7.163 9h.63l.853-.854a.5.5 0 0 1 .708 0l.646.647.646-.647a.5.5 0 0 1 .708 0l.646.647.646-.647a.5.5 0 0 1 .708 0l.646.647.793-.793-1-1H7.163a.5.5 0 0 1-.45-.285A3 3 0 0 0 4 5zm1.5 3a1.5 1.5 0 1 0-3 0 1.5 1.5 0 0 0 3 0zM3 8a1 1 0 1 1 2 0 1 1 0 0 1-2 0z" />
            </svg>
          </div>
          <h1 className="text-xl font-semibold text-gray-900">Manage Role</h1>
        </div>

        {/* Header action buttons */}
        <div className="flex items-center gap-2">
          <button
            type="submit"
            form="ManageRole"
            disabled={updateRoleMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {updateRoleMutation.isPending && (
              <svg
                className="h-4 w-4 animate-spin"
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
                  d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                />
              </svg>
            )}
            Save Role
          </button>

          <Link
            to={`/admin/roles/${roleId}`}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
        </div>
      </div>

      {/* ── Role Edit Form ───────────────────────────────────────── */}
      <DynamicForm
        id="ManageRole"
        name="ManageRole"
        validation={validation}
        onSubmit={handleSubmit}
        labelMode="stacked"
      >
        <div className="grid grid-cols-12 gap-x-4 gap-y-5">
          {/* Name — required, half-width (span-6 matching source) */}
          <div className="col-span-12 sm:col-span-6">
            <label
              htmlFor="role-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="role-name"
              name="name"
              type="text"
              required
              autoComplete="off"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              aria-required="true"
            />
          </div>

          {/* Description — required, full-width textarea (span-12 matching source) */}
          <div className="col-span-12">
            <label
              htmlFor="role-description"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Description{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <textarea
              id="role-description"
              name="description"
              required
              rows={4}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              aria-required="true"
            />
          </div>
        </div>
      </DynamicForm>
    </div>
  );
}

export default RoleManage;
