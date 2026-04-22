/**
 * EntityManage.tsx — Edit Entity Page
 *
 * React page replacing WebVella.Erp.Plugins.SDK/Pages/entity/manage.cshtml[.cs].
 * Loads entity by ID, pre-populates an editable form with all entity properties
 * including a CRUD × Roles permission checkbox grid, and submits updates via
 * the Entity Management service PUT endpoint.
 *
 * Route: /entities/:entityId/manage
 */

import { useState, useEffect, useCallback, type FormEvent } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { useEntity, useUpdateEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type { Entity, InputEntity, RecordPermissions } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import type { ApiError } from '../../api/client';

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Sub-navigation tabs matching monolith AdminPageUtils.GetEntityAdminSubNav */
const SUB_NAV_TABS = [
  { key: 'details', label: 'Details', pathSuffix: '' },
  { key: 'fields', label: 'Fields', pathSuffix: '/fields' },
  { key: 'relations', label: 'Relations', pathSuffix: '/relations' },
  { key: 'data', label: 'Data', pathSuffix: '/data' },
  { key: 'pages', label: 'Pages', pathSuffix: '/pages' },
  { key: 'webapi', label: 'Web API', pathSuffix: '/web-api' },
] as const;

/** CRUD permission rows for the permission checkbox grid */
const PERMISSION_TYPES: { key: keyof RecordPermissions; label: string }[] = [
  { key: 'canCreate', label: 'Create' },
  { key: 'canRead', label: 'Read' },
  { key: 'canUpdate', label: 'Update' },
  { key: 'canDelete', label: 'Delete' },
];

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * EntityManage — editable entity detail form.
 *
 * Pre-populates all entity fields from the fetched entity data and provides
 * inline editing with a CRUD × Roles permission checkbox grid.
 * Default export enables React.lazy() code-splitting in router.tsx.
 */
function EntityManage(): React.JSX.Element {
  /* ---- Route params & navigation --------------------------------- */
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  /* ---- Data fetching --------------------------------------------- */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
  } = useEntity(entityId ?? '');

  const {
    data: rolesResponse,
    isLoading: rolesLoading,
  } = useRoles();

  const roles: ErpRole[] = rolesResponse?.object ?? [];

  const updateMutation = useUpdateEntity();

  /* ---- Form state ------------------------------------------------ */
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
  const [labelPlural, setLabelPlural] = useState('');
  const [system, setSystem] = useState(false);
  const [color, setColor] = useState('#f44336');
  const [iconName, setIconName] = useState('fa fa-database');
  const [recordScreenIdField, setRecordScreenIdField] = useState<string | null>(null);
  const [permissions, setPermissions] = useState<RecordPermissions>({
    canCreate: [],
    canRead: [],
    canUpdate: [],
    canDelete: [],
  });

  /* ---- Validation / submission state ----------------------------- */
  const [validationMessage, setValidationMessage] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  /* ---- Sync form state when entity data arrives ------------------ */
  useEffect(() => {
    if (!entity) return;

    setName(entity.name ?? '');
    setLabel(entity.label ?? '');
    setLabelPlural(entity.labelPlural ?? '');
    setSystem(entity.system ?? false);
    setColor(entity.color ?? '#f44336');
    setIconName(entity.iconName ?? 'fa fa-database');
    setRecordScreenIdField(entity.recordScreenIdField ?? null);
    setPermissions({
      canCreate: entity.recordPermissions?.canCreate ?? [],
      canRead: entity.recordPermissions?.canRead ?? [],
      canUpdate: entity.recordPermissions?.canUpdate ?? [],
      canDelete: entity.recordPermissions?.canDelete ?? [],
    });
  }, [entity]);

  /* ---- Permission grid toggle handler ---------------------------- */
  const handlePermissionToggle = useCallback(
    (permKey: keyof RecordPermissions, roleId: string) => {
      setPermissions((prev) => {
        const current = prev[permKey];
        const exists = current.includes(roleId);
        return {
          ...prev,
          [permKey]: exists
            ? current.filter((id) => id !== roleId)
            : [...current, roleId],
        };
      });
    },
    [],
  );

  /* ---- Form submission ------------------------------------------- */
  const handleSubmit = useCallback(
    async (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setValidationMessage(null);
      setFieldErrors({});

      if (!entityId) return;

      const payload: InputEntity = {
        name,
        label,
        labelPlural,
        system,
        iconName,
        color,
        recordPermissions: permissions,
        recordScreenIdField: recordScreenIdField ?? undefined,
      };

      try {
        await updateMutation.mutateAsync({ id: entityId, entity: payload });
        navigate(`/entities/${entityId}`);
      } catch (err: unknown) {
        const apiError = err as ApiError | undefined;
        if (apiError?.message) {
          setValidationMessage(apiError.message);
        }
        if (apiError?.errors && Array.isArray(apiError.errors)) {
          const mapped: Record<string, string> = {};
          for (const fieldErr of apiError.errors) {
            if (fieldErr.key) {
              mapped[fieldErr.key.toLowerCase()] = fieldErr.message;
            }
          }
          setFieldErrors(mapped);
        }
      }
    },
    [
      entityId,
      name,
      label,
      labelPlural,
      system,
      iconName,
      color,
      recordScreenIdField,
      permissions,
      updateMutation,
      navigate,
    ],
  );

  /* ---- Loading state --------------------------------------------- */
  if (entityLoading || rolesLoading) {
    return (
      <div className="flex items-center justify-center min-h-[50vh]">
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-r-transparent"
            role="status"
          >
            <span className="sr-only">Loading…</span>
          </div>
          <p className="text-sm text-gray-500">Loading entity…</p>
        </div>
      </div>
    );
  }

  /* ---- Error state ------------------------------------------------ */
  if (entityError || !entity) {
    return (
      <div className="rounded-lg border border-red-300 bg-red-50 p-6 text-center">
        <h2 className="text-lg font-semibold text-red-800">
          Entity not found
        </h2>
        <p className="mt-2 text-sm text-red-600">
          The entity you are trying to edit does not exist or could not be
          loaded.
        </p>
        <Link
          to="/entities"
          className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
        >
          Back to Entities
        </Link>
      </div>
    );
  }

  /* ---- Render ----------------------------------------------------- */
  return (
    <div className="space-y-6">
      {/* ====== Page header ====== */}
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          {entity.color && (
            <span
              className="inline-flex h-10 w-10 items-center justify-center rounded-md text-white"
              style={{ backgroundColor: entity.color }}
              aria-hidden="true"
            >
              <i className={entity.iconName ?? 'fa fa-database'} />
            </span>
          )}
          <div>
            <h1 className="text-xl font-bold text-gray-900">
              Manage Entity
            </h1>
            <p className="text-sm text-gray-500">{entity.name}</p>
          </div>
        </div>

        {/* Header actions */}
        <div className="flex items-center gap-2">
          <button
            type="submit"
            form="ManageRecord"
            disabled={updateMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {updateMutation.isPending ? (
              <>
                <span
                  className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-solid border-white border-r-transparent"
                  aria-hidden="true"
                />
                Saving…
              </>
            ) : (
              <>
                <i className="fa fa-save" aria-hidden="true" />
                Save Entity
              </>
            )}
          </button>
          <Link
            to={`/entities/${entityId}`}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
        </div>
      </div>

      {/* ====== Sub-navigation tabs ====== */}
      <nav
        aria-label="Entity admin navigation"
        className="border-b border-gray-200"
      >
        <ul className="-mb-px flex gap-4 overflow-x-auto" role="tablist">
          {SUB_NAV_TABS.map((tab) => {
            const isActive = tab.key === 'details';
            return (
              <li key={tab.key} role="presentation">
                <Link
                  to={`/entities/${entityId}${tab.pathSuffix}`}
                  role="tab"
                  aria-selected={isActive}
                  className={`inline-block whitespace-nowrap border-b-2 px-1 pb-3 text-sm font-medium transition-colors ${
                    isActive
                      ? 'border-blue-600 text-blue-600'
                      : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700'
                  }`}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* ====== Validation alert ====== */}
      {validationMessage && (
        <div
          className="rounded-md border border-red-300 bg-red-50 p-4"
          role="alert"
        >
          <div className="flex items-start gap-3">
            <i
              className="fa fa-exclamation-circle mt-0.5 text-red-500"
              aria-hidden="true"
            />
            <div>
              <h3 className="text-sm font-semibold text-red-800">
                Validation Error
              </h3>
              <p className="mt-1 text-sm text-red-700">{validationMessage}</p>
            </div>
          </div>
        </div>
      )}

      {/* ====== Edit form ====== */}
      <form
        id="ManageRecord"
        onSubmit={handleSubmit}
        className="space-y-6"
        noValidate
      >
        {/* -- Row 1: Name + System -- */}
        <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
          {/* Name */}
          <div>
            <label
              htmlFor="entity-name"
              className="block text-sm font-medium text-gray-700"
            >
              Name <span className="text-red-500">*</span>
            </label>
            <input
              id="entity-name"
              type="text"
              required
              value={name}
              onChange={(e) => setName(e.target.value)}
              readOnly={entity.system}
              className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                fieldErrors['name']
                  ? 'border-red-500 bg-red-50'
                  : 'border-gray-300 bg-white'
              } ${entity.system ? 'cursor-not-allowed bg-gray-100 text-gray-500' : ''}`}
            />
            {fieldErrors['name'] && (
              <p className="mt-1 text-xs text-red-600">{fieldErrors['name']}</p>
            )}
          </div>

          {/* System */}
          <div className="flex items-end pb-2">
            <label className="inline-flex items-center gap-2 text-sm text-gray-700">
              <input
                type="checkbox"
                checked={system}
                onChange={(e) => setSystem(e.target.checked)}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              System
            </label>
          </div>
        </div>

        {/* -- Row 2: Label + LabelPlural -- */}
        <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
          {/* Label */}
          <div>
            <label
              htmlFor="entity-label"
              className="block text-sm font-medium text-gray-700"
            >
              Label <span className="text-red-500">*</span>
            </label>
            <input
              id="entity-label"
              type="text"
              required
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                fieldErrors['label']
                  ? 'border-red-500 bg-red-50'
                  : 'border-gray-300 bg-white'
              }`}
            />
            {fieldErrors['label'] && (
              <p className="mt-1 text-xs text-red-600">
                {fieldErrors['label']}
              </p>
            )}
          </div>

          {/* Label Plural */}
          <div>
            <label
              htmlFor="entity-label-plural"
              className="block text-sm font-medium text-gray-700"
            >
              Label Plural <span className="text-red-500">*</span>
            </label>
            <input
              id="entity-label-plural"
              type="text"
              required
              value={labelPlural}
              onChange={(e) => setLabelPlural(e.target.value)}
              className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                fieldErrors['labelplural']
                  ? 'border-red-500 bg-red-50'
                  : 'border-gray-300 bg-white'
              }`}
            />
            {fieldErrors['labelplural'] && (
              <p className="mt-1 text-xs text-red-600">
                {fieldErrors['labelplural']}
              </p>
            )}
          </div>
        </div>

        {/* -- Row 3: Color + IconName -- */}
        <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
          {/* Color */}
          <div>
            <label
              htmlFor="entity-color"
              className="block text-sm font-medium text-gray-700"
            >
              Color
            </label>
            <div className="mt-1 flex items-center gap-2">
              <input
                id="entity-color"
                type="color"
                value={color}
                onChange={(e) => setColor(e.target.value)}
                className="h-10 w-14 cursor-pointer rounded border border-gray-300 p-0.5"
              />
              <input
                type="text"
                value={color}
                onChange={(e) => setColor(e.target.value)}
                className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                aria-label="Color hex value"
              />
            </div>
          </div>

          {/* Icon Name */}
          <div>
            <label
              htmlFor="entity-icon"
              className="block text-sm font-medium text-gray-700"
            >
              Icon Name
            </label>
            <div className="mt-1 flex items-center gap-2">
              <span
                className="inline-flex h-10 w-10 flex-shrink-0 items-center justify-center rounded-md border border-gray-300 bg-gray-50 text-gray-500"
                aria-hidden="true"
              >
                <i className={iconName || 'fa fa-database'} />
              </span>
              <input
                id="entity-icon"
                type="text"
                value={iconName}
                onChange={(e) => setIconName(e.target.value)}
                placeholder="fa fa-database"
                className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              />
            </div>
          </div>
        </div>

        {/* -- Row 4: RecordScreenIdField + Entity Id (read-only) -- */}
        <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
          {/* RecordScreenIdField */}
          <div>
            <label
              htmlFor="entity-screen-id-field"
              className="block text-sm font-medium text-gray-700"
            >
              Record Screen Id Field
            </label>
            <select
              id="entity-screen-id-field"
              value={recordScreenIdField ?? ''}
              onChange={(e) =>
                setRecordScreenIdField(e.target.value || null)
              }
              className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <option value="">id (default)</option>
              {(entity.fields ?? []).map((field) => (
                <option key={field.id} value={field.id}>
                  {field.name}
                </option>
              ))}
            </select>
            <p className="mt-1 text-xs text-gray-500">
              The field used to generate the record screen URL path. Defaults to
              the &quot;id&quot; GUID field.
            </p>
          </div>

          {/* Entity Id (read-only) */}
          <div>
            <label
              htmlFor="entity-id-display"
              className="block text-sm font-medium text-gray-700"
            >
              Entity Id
            </label>
            <input
              id="entity-id-display"
              type="text"
              readOnly
              value={entity.id}
              className="mt-1 block w-full cursor-not-allowed rounded-md border border-gray-300 bg-gray-100 px-3 py-2 text-sm text-gray-500 shadow-sm"
            />
          </div>
        </div>

        {/* ====== Record Permissions ====== */}
        <fieldset className="space-y-3">
          <legend className="text-sm font-semibold text-gray-900">
            Record Permissions
          </legend>

          {roles.length === 0 && !rolesLoading && (
            <p className="text-sm text-gray-500">No roles available.</p>
          )}

          {roles.length > 0 && (
            <div className="overflow-x-auto rounded-lg border border-gray-200">
              <table className="min-w-full divide-y divide-gray-200 text-sm">
                <thead className="bg-gray-50">
                  <tr>
                    <th
                      scope="col"
                      className="px-4 py-3 text-start font-medium text-gray-600"
                    >
                      Permission
                    </th>
                    {roles.map((role) => (
                      <th
                        key={role.id}
                        scope="col"
                        className="px-4 py-3 text-center font-medium text-gray-600"
                      >
                        {role.name}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {PERMISSION_TYPES.map((perm) => (
                    <tr key={perm.key}>
                      <td className="whitespace-nowrap px-4 py-3 font-medium text-gray-700">
                        {perm.label}
                      </td>
                      {roles.map((role) => {
                        const isChecked = permissions[perm.key].includes(
                          role.id,
                        );
                        const checkboxId = `perm-${perm.key}-${role.id}`;
                        return (
                          <td
                            key={role.id}
                            className="px-4 py-3 text-center"
                          >
                            <input
                              id={checkboxId}
                              type="checkbox"
                              checked={isChecked}
                              onChange={() =>
                                handlePermissionToggle(perm.key, role.id)
                              }
                              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                              aria-label={`${perm.label} permission for ${role.name}`}
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

        {/* ====== Form actions (bottom) ====== */}
        <div className="flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
          <Link
            to={`/entities/${entityId}`}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
          <button
            type="submit"
            disabled={updateMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {updateMutation.isPending ? 'Saving…' : 'Save Entity'}
          </button>
        </div>
      </form>
    </div>
  );
}

export default EntityManage;
