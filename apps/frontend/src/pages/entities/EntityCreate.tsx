import { useState, useCallback, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { useCreateEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type { InputEntity, RecordPermissions } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import type { ApiError } from '../../api/client';
import type { FormValidation } from '../../components/forms/DynamicForm';

/* ─── Constants ──────────────────────────────────────────────── */

/** CRUD permission types displayed as rows in the permission grid. */
const PERMISSION_TYPES: ReadonlyArray<{
  key: keyof RecordPermissions;
  label: string;
}> = [
  { key: 'canCreate', label: 'Create' },
  { key: 'canRead', label: 'Read' },
  { key: 'canUpdate', label: 'Update' },
  { key: 'canDelete', label: 'Delete' },
];

/* ─── Helpers ────────────────────────────────────────────────── */

/**
 * Converts a mutation Error (which may carry ApiError properties
 * attached by the assertApiSuccess helper) into a FormValidation
 * structure for inline field-level and top-level error display.
 */
function toFormValidation(error: Error): FormValidation {
  const apiErr = error as unknown as Partial<ApiError>;
  return {
    message:
      apiErr.message ||
      error.message ||
      'An error occurred while creating the entity.',
    errors: Array.isArray(apiErr.errors)
      ? apiErr.errors.map((item) => ({
          propertyName: (item as { key?: string }).key ?? '',
          message:
            (item as { message?: string }).message ??
            (item as { value?: string }).value ??
            '',
        }))
      : [],
  };
}

/**
 * Returns the first matching field-level error message for a given
 * property name, or undefined if no error exists for that field.
 */
function getFieldError(
  validation: FormValidation | null,
  fieldName: string,
): string | undefined {
  if (!validation?.errors?.length) return undefined;
  const match = validation.errors.find(
    (e) => e.propertyName.toLowerCase() === fieldName.toLowerCase(),
  );
  return match?.message;
}

/**
 * Returns the appropriate Tailwind border/ring classes for an input
 * based on whether a validation error exists for the given field.
 */
function inputClasses(validation: FormValidation | null, fieldName: string): string {
  const hasError = !!getFieldError(validation, fieldName);
  return hasError
    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
    : 'border-gray-300 focus:border-indigo-500 focus:ring-indigo-500';
}

/* ─── Component ──────────────────────────────────────────────── */

/**
 * EntityCreate page — creates a new entity definition.
 *
 * Replaces the monolith's create.cshtml / create.cshtml.cs page.
 * Provides a form with entity metadata fields (name, label, color,
 * icon) and a CRUD × Roles permission checkbox grid.
 *
 * Route: /entities/create
 */
function EntityCreate(): React.JSX.Element {
  const navigate = useNavigate();

  /* ── Form field state ─────────────────────────────────────── */
  const [name, setName] = useState('');
  const [id, setId] = useState('');
  const [label, setLabel] = useState('');
  const [labelPlural, setLabelPlural] = useState('');
  const [system, setSystem] = useState(false);
  const [color, setColor] = useState('');
  const [iconName, setIconName] = useState('');

  /* ── Permission grid state ────────────────────────────────── */
  const [permissions, setPermissions] = useState<RecordPermissions>({
    canCreate: [],
    canRead: [],
    canUpdate: [],
    canDelete: [],
  });

  /* ── Queries & mutations ──────────────────────────────────── */
  const { data: rolesData, isLoading: rolesLoading } = useRoles();
  const createMutation = useCreateEntity();

  /** Roles derived from the Identity service response. */
  const roles: ErpRole[] = rolesData?.object ?? [];

  /** Validation state derived from the mutation error. */
  const validation: FormValidation | null = createMutation.isError
    ? toFormValidation(createMutation.error)
    : null;

  /* ── Handlers ─────────────────────────────────────────────── */

  /** Toggles a role's inclusion in a specific permission array. */
  const handlePermissionToggle = useCallback(
    (permissionKey: keyof RecordPermissions, roleId: string) => {
      setPermissions((prev) => {
        const current = prev[permissionKey];
        const exists = current.includes(roleId);
        return {
          ...prev,
          [permissionKey]: exists
            ? current.filter((rid) => rid !== roleId)
            : [...current, roleId],
        };
      });
    },
    [],
  );

  /** Generates a UUID v4 and sets it as the entity ID value. */
  const handleGenerateId = useCallback(() => {
    setId(crypto.randomUUID());
  }, []);

  /** Builds the InputEntity payload and submits the creation mutation. */
  const handleSubmit = useCallback(
    async (e: FormEvent) => {
      e.preventDefault();
      createMutation.reset();

      const input: InputEntity = {
        id: id.trim() || null,
        name: name.trim(),
        label: label.trim(),
        labelPlural: labelPlural.trim(),
        system,
        iconName: iconName.trim(),
        color: color.trim(),
        recordPermissions: {
          canCreate: [...permissions.canCreate],
          canRead: [...permissions.canRead],
          canUpdate: [...permissions.canUpdate],
          canDelete: [...permissions.canDelete],
        },
        recordScreenIdField: null,
      };

      try {
        const entity = await createMutation.mutateAsync(input);
        navigate(`/entities/${entity.id}`);
      } catch {
        /* Error is captured by the mutation hook and rendered via
           the validation derived state. No additional handling needed. */
      }
    },
    [
      name,
      id,
      label,
      labelPlural,
      system,
      color,
      iconName,
      permissions,
      createMutation,
      navigate,
    ],
  );

  /** Whether the submit button should be disabled. */
  const isSubmitDisabled = createMutation.isPending || createMutation.isSuccess;

  /* ── Render ───────────────────────────────────────────────── */
  return (
    <div className="min-h-full bg-gray-50">
      {/* ─── Page Header ─── */}
      <header className="border-b border-solid border-gray-200 bg-white">
        <div className="mx-auto max-w-7xl px-4 py-4 sm:px-6 lg:px-8">
          <div className="flex flex-wrap items-center justify-between gap-4">
            {/* Title group */}
            <div className="flex items-center gap-3">
              <span
                className="inline-flex h-10 w-10 shrink-0 items-center justify-center rounded text-white"
                style={{ backgroundColor: '#dc3545' }}
                aria-hidden="true"
              >
                <i className="fa fa-plus" />
              </span>
              <h1 className="text-xl font-semibold text-gray-900">
                Create Entity
              </h1>
            </div>

            {/* Action buttons */}
            <nav aria-label="Page actions" className="flex items-center gap-2">
              <button
                type="submit"
                form="CreateRecord"
                disabled={isSubmitDisabled}
                className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-60"
              >
                <i className="fa fa-save" aria-hidden="true" />
                {createMutation.isPending ? 'Creating\u2026' : 'Create Entity'}
              </button>
              <Link
                to="/entities"
                className="inline-flex items-center gap-1.5 rounded border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              >
                <i className="fa fa-times" aria-hidden="true" />
                Cancel
              </Link>
            </nav>
          </div>
        </div>
      </header>

      {/* ─── Main Content ─── */}
      <main className="mx-auto max-w-7xl px-4 py-6 sm:px-6 lg:px-8">
        {/* Success flash (shown briefly before navigation completes) */}
        {createMutation.isSuccess && createMutation.data && (
          <div
            className="mb-4 rounded border border-green-300 bg-green-50 p-3 text-sm text-green-800"
            role="status"
          >
            Entity &ldquo;{createMutation.data.name}&rdquo; created
            successfully. Redirecting&hellip;
          </div>
        )}

        {/* Top-level validation errors */}
        {validation && (
          <div
            className="mb-4 rounded border border-red-300 bg-red-50 p-4"
            role="alert"
          >
            {validation.message && (
              <p className="mb-2 font-medium text-red-800">
                {validation.message}
              </p>
            )}
            {validation.errors.length > 0 && (
              <ul className="list-inside list-disc space-y-1 text-sm text-red-700">
                {validation.errors.map((err, idx) => (
                  <li key={`${err.propertyName}-${idx}`}>
                    {err.propertyName ? (
                      <>
                        <span className="font-medium">
                          {err.propertyName}
                        </span>
                        :{' '}
                      </>
                    ) : null}
                    {err.message}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        {/* ─── Entity Creation Form ─── */}
        <form
          id="CreateRecord"
          onSubmit={handleSubmit}
          noValidate
          className="space-y-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
        >
          {/* ── Name (required) ── */}
          <div>
            <label
              htmlFor="entity-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name <span className="text-red-600">*</span>
            </label>
            <input
              type="text"
              id="entity-name"
              name="Name"
              required
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. my_entity"
              autoComplete="off"
              className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${inputClasses(validation, 'name')}`}
            />
            {getFieldError(validation, 'name') && (
              <p className="mt-1 text-sm text-red-600">
                {getFieldError(validation, 'name')}
              </p>
            )}
          </div>

          {/* ── Id (optional GUID) ── */}
          <div>
            <label
              htmlFor="entity-id"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Id
            </label>
            <div className="flex gap-2">
              <input
                type="text"
                id="entity-id"
                name="Id"
                value={id}
                onChange={(e) => setId(e.target.value)}
                placeholder="Auto-generated if empty"
                autoComplete="off"
                className={`block flex-1 rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${inputClasses(validation, 'id')}`}
              />
              <button
                type="button"
                onClick={handleGenerateId}
                className="shrink-0 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              >
                Generate
              </button>
            </div>
            {getFieldError(validation, 'id') && (
              <p className="mt-1 text-sm text-red-600">
                {getFieldError(validation, 'id')}
              </p>
            )}
          </div>

          {/* ── System ── */}
          <div className="flex items-center gap-2">
            <input
              type="checkbox"
              id="entity-system"
              name="System"
              checked={system}
              onChange={(e) => setSystem(e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
            />
            <label
              htmlFor="entity-system"
              className="text-sm font-medium text-gray-700"
            >
              System
            </label>
          </div>

          {/* ── Label ── */}
          <div>
            <label
              htmlFor="entity-label"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Label
            </label>
            <input
              type="text"
              id="entity-label"
              name="Label"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              autoComplete="off"
              className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${inputClasses(validation, 'label')}`}
            />
            {getFieldError(validation, 'label') && (
              <p className="mt-1 text-sm text-red-600">
                {getFieldError(validation, 'label')}
              </p>
            )}
          </div>

          {/* ── Label Plural ── */}
          <div>
            <label
              htmlFor="entity-label-plural"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Label Plural
            </label>
            <input
              type="text"
              id="entity-label-plural"
              name="LabelPlural"
              value={labelPlural}
              onChange={(e) => setLabelPlural(e.target.value)}
              autoComplete="off"
              className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${inputClasses(validation, 'labelPlural')}`}
            />
            {getFieldError(validation, 'labelPlural') && (
              <p className="mt-1 text-sm text-red-600">
                {getFieldError(validation, 'labelPlural')}
              </p>
            )}
          </div>

          {/* ── Color ── */}
          <div>
            <label
              htmlFor="entity-color"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Color
            </label>
            <div className="flex items-center gap-2">
              <input
                type="color"
                id="entity-color"
                name="Color"
                value={color || '#000000'}
                onChange={(e) => setColor(e.target.value)}
                className="h-10 w-14 cursor-pointer rounded border border-gray-300 p-0.5"
                aria-label="Entity color picker"
              />
              <input
                type="text"
                aria-label="Color hex value"
                value={color}
                onChange={(e) => setColor(e.target.value)}
                placeholder="#000000"
                autoComplete="off"
                className={`block flex-1 rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${inputClasses(validation, 'color')}`}
              />
            </div>
            {getFieldError(validation, 'color') && (
              <p className="mt-1 text-sm text-red-600">
                {getFieldError(validation, 'color')}
              </p>
            )}
          </div>

          {/* ── Icon Name ── */}
          <div>
            <label
              htmlFor="entity-icon-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Icon Name
            </label>
            <div className="flex items-center gap-2">
              {iconName && (
                <span
                  className="inline-flex h-8 w-8 items-center justify-center rounded bg-gray-100 text-gray-600"
                  aria-hidden="true"
                >
                  <i className={iconName} />
                </span>
              )}
              <input
                type="text"
                id="entity-icon-name"
                name="IconName"
                value={iconName}
                onChange={(e) => setIconName(e.target.value)}
                placeholder="e.g. fa fa-database"
                autoComplete="off"
                className={`block flex-1 rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${inputClasses(validation, 'iconName')}`}
              />
            </div>
            {getFieldError(validation, 'iconName') && (
              <p className="mt-1 text-sm text-red-600">
                {getFieldError(validation, 'iconName')}
              </p>
            )}
          </div>

          {/* ── Record Permissions Grid (CRUD × Roles) ── */}
          <fieldset>
            <legend className="mb-2 block text-sm font-medium text-gray-700">
              Record Permissions
            </legend>

            {rolesLoading ? (
              <p className="text-sm italic text-gray-500">Loading roles&hellip;</p>
            ) : roles.length === 0 ? (
              <p className="text-sm text-gray-500">
                No roles available. Permissions cannot be configured.
              </p>
            ) : (
              <div className="overflow-x-auto rounded-md border border-gray-200">
                <table className="min-w-full divide-y divide-gray-200 text-sm">
                  <thead className="bg-gray-50">
                    <tr>
                      <th
                        scope="col"
                        className="px-4 py-2 text-start font-medium text-gray-600"
                      >
                        Permission
                      </th>
                      {roles.map((role) => (
                        <th
                          key={role.id}
                          scope="col"
                          className="px-4 py-2 text-center font-medium text-gray-600"
                        >
                          {role.name}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100 bg-white">
                    {PERMISSION_TYPES.map((perm) => (
                      <tr key={perm.key}>
                        <td className="whitespace-nowrap px-4 py-2 font-medium text-gray-700">
                          {perm.label}
                        </td>
                        {roles.map((role) => {
                          const isChecked = permissions[perm.key].includes(
                            role.id,
                          );
                          const cbId = `perm-${perm.key}-${role.id}`;
                          return (
                            <td
                              key={role.id}
                              className="px-4 py-2 text-center"
                            >
                              <input
                                type="checkbox"
                                id={cbId}
                                checked={isChecked}
                                onChange={() =>
                                  handlePermissionToggle(perm.key, role.id)
                                }
                                aria-label={`${perm.label} permission for role ${role.name}`}
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
          </fieldset>
        </form>
      </main>
    </div>
  );
}

export default EntityCreate;
