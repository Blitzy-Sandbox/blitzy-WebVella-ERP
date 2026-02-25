/**
 * AdminEntityManage — Entity Edit / Manage Page
 *
 * Route:    /admin/entities/:entityId/manage
 * Replaces: WebVella.Erp.Plugins.SDK/Pages/entity/manage.cshtml[.cs]
 *
 * Edits entity metadata and permissions. Pre-populates a form with
 * the current entity values via useEntity(), allows editing, and
 * submits updates via useUpdateEntity().
 *
 * Data flow:
 *  1. useEntity(entityId)    — loads entity metadata for pre-population
 *  2. useRoles()             — loads all system roles for the CRUD permission grid
 *  3. User edits name / label / colour / icon / permissions / recordScreenIdField
 *  4. useUpdateEntity()      — submits update mutation (PUT /v1/entities/:id)
 *  5. On success             — navigates to entity detail page
 *
 * Monolith parity (manage.cshtml.cs):
 *  - PageInit(): EntityManager.ReadEntity(RecordId) → useEntity()
 *  - OnPost():   EntityManager.UpdateEntity(input)  → useUpdateEntity().mutateAsync()
 *  - Permission grid: rows = CRUD operations, columns = roles (checkbox matrix)
 *  - RecordScreenIdField: dropdown populated from entity's fields array
 *  - Entity admin sub-nav via TabNav (replaces AdminPageUtils.GetEntityAdminSubNav)
 *
 * AAP compliance:
 *  - §0.4.3  — Full entity CRUD via Entity Management service
 *  - §0.5.1  — EntityManager.UpdateEntity → PUT /v1/entities/:id
 *  - §0.8.1  — Self-contained SPA page with no SSR
 *  - §0.8.6  — API calls via /v1/ prefixed endpoints
 *
 * @module pages/admin/AdminEntityManage
 */

import { useState, useEffect, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { useEntity, useUpdateEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type {
  Entity,
  InputEntity,
  RecordPermissions,
  Field,
} from '../../types/entity';
import type { ErpRole } from '../../types/user';
import DynamicForm from '../../components/forms/DynamicForm';
import type {
  FormValidation,
  ValidationError,
} from '../../components/forms/DynamicForm';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

/* ═══════════════════════════════════════════════════════════════════
 * Local Types
 * ═══════════════════════════════════════════════════════════════════ */

/** CRUD operation descriptor for the permission grid rows. */
interface CrudOperation {
  /** Key matching a RecordPermissions property name. */
  key: keyof RecordPermissions;
  /** Human-readable label for the row header. */
  label: string;
}

/* ═══════════════════════════════════════════════════════════════════
 * Constants
 * ═══════════════════════════════════════════════════════════════════ */

/** CRUD permission operations rendered as rows in the permission grid. */
const CRUD_OPERATIONS: CrudOperation[] = [
  { key: 'canCreate', label: 'Create' },
  { key: 'canRead', label: 'Read' },
  { key: 'canUpdate', label: 'Update' },
  { key: 'canDelete', label: 'Delete' },
];

/** Default empty permission set for initial form state. */
const EMPTY_PERMISSIONS: RecordPermissions = {
  canRead: [],
  canCreate: [],
  canUpdate: [],
  canDelete: [],
};

/** Default page header colour — matches monolith's wv-page-header color="#f44336". */
const DEFAULT_HEADER_COLOR = '#f44336';

/* ═══════════════════════════════════════════════════════════════════
 * Component
 * ═══════════════════════════════════════════════════════════════════ */

/**
 * AdminEntityManage renders the entity edit / manage page.
 *
 * Replaces the monolith SDK entity manage flow:
 * - Route `/sdk/objects/entity/c/manage?recordId={id}` → `/admin/entities/:entityId/manage`
 * - `EntityManager.ReadEntity()` → `useEntity()` TanStack Query hook
 * - `EntityManager.UpdateEntity()` → `useUpdateEntity().mutateAsync()`
 * - `SecurityManager.GetAllRoles()` → `useRoles()` TanStack Query hook
 * - Razor form fields → controlled React inputs with Tailwind CSS styling
 * - RecordPermissions checkbox grid → role × CRUD checkbox matrix
 * - FieldOptions dropdown → fieldOptions derived from entity.fields
 */
export default function AdminEntityManage(): React.ReactNode {
  /* ── Route params & navigation ───────────────────────────────── */
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  /* ── Data fetching ───────────────────────────────────────────── */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityIsError,
    error: entityErrorObj,
  } = useEntity(entityId ?? '');

  const { data: rolesResponse, isLoading: rolesLoading } = useRoles();

  /**
   * Extract the roles array from the wrapped ApiResponse.
   * useRoles() returns ApiResponse<ErpRole[]>, so access via .object.
   */
  const roles: ErpRole[] = useMemo(
    () =>
      (rolesResponse as { object?: ErpRole[] } | undefined)?.object ?? [],
    [rolesResponse],
  );

  /* ── Update mutation ─────────────────────────────────────────── */
  const {
    mutateAsync: updateMutateAsync,
    isPending: updatePending,
    isError: updateIsError,
    error: updateError,
    isSuccess: updateSuccess,
  } = useUpdateEntity();

  /* ── Form state ──────────────────────────────────────────────── */
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
  const [labelPlural, setLabelPlural] = useState('');
  const [color, setColor] = useState(DEFAULT_HEADER_COLOR);
  const [iconName, setIconName] = useState('fa fa-database');
  const [isSystem, setIsSystem] = useState(false);
  const [recordScreenIdField, setRecordScreenIdField] = useState<string>('');
  const [recordPermissions, setRecordPermissions] =
    useState<RecordPermissions>(EMPTY_PERMISSIONS);

  /** Validation state matching DynamicForm expectations. */
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* ── Pre-populate form when entity data loads ────────────────── */
  useEffect(() => {
    if (entity) {
      setName(entity.name ?? '');
      setLabel(entity.label ?? '');
      setLabelPlural(entity.labelPlural ?? '');
      setColor(entity.color ?? DEFAULT_HEADER_COLOR);
      setIconName(entity.iconName ?? 'fa fa-database');
      setIsSystem(entity.system ?? false);
      setRecordScreenIdField(entity.recordScreenIdField ?? '');
      setRecordPermissions(
        entity.recordPermissions
          ? {
              canRead: [...entity.recordPermissions.canRead],
              canCreate: [...entity.recordPermissions.canCreate],
              canUpdate: [...entity.recordPermissions.canUpdate],
              canDelete: [...entity.recordPermissions.canDelete],
            }
          : EMPTY_PERMISSIONS,
      );
      setValidation(undefined);
    }
  }, [entity]);

  /* ── RecordScreenIdField dropdown options ─────────────────────── */
  const fieldOptions = useMemo<Array<{ value: string; label: string }>>(() => {
    if (!entity?.fields) return [];
    return entity.fields.map((field: Field) => ({
      value: field.id,
      label: field.name,
    }));
  }, [entity?.fields]);

  /* ── Entity admin sub-navigation tabs ─────────────────────────── */
  const entitySubNavTabs = useMemo<TabConfig[]>(
    () => [
      { id: 'details', label: 'Details', content: undefined },
      { id: 'manage', label: 'Manage', content: undefined },
      { id: 'fields', label: 'Fields', content: undefined },
      { id: 'relations', label: 'Relations', content: undefined },
      { id: 'data', label: 'Data', content: undefined },
      { id: 'pages', label: 'Pages', content: undefined },
      { id: 'web-api', label: 'Web API', content: undefined },
    ],
    [],
  );

  /** Map tab ids to routes for navigation via TabNav. */
  const handleTabChange = useCallback(
    (tabId: string) => {
      const routeMap: Record<string, string> = {
        details: `/admin/entities/${entityId}`,
        manage: `/admin/entities/${entityId}/manage`,
        fields: `/admin/entities/${entityId}/fields`,
        relations: `/admin/entities/${entityId}/relations`,
        data: `/admin/entities/${entityId}/data`,
        pages: `/admin/entities/${entityId}/pages`,
        'web-api': `/admin/entities/${entityId}/web-api`,
      };
      const target = routeMap[tabId];
      if (target) {
        navigate(target);
      }
    },
    [entityId, navigate],
  );

  /* ── Permission grid toggle handler ──────────────────────────── */
  const handlePermissionToggle = useCallback(
    (operation: keyof RecordPermissions, roleId: string) => {
      setRecordPermissions((prev) => {
        const currentList = prev[operation];
        const isChecked = currentList.includes(roleId);
        return {
          ...prev,
          [operation]: isChecked
            ? currentList.filter((id) => id !== roleId)
            : [...currentList, roleId],
        };
      });
    },
    [],
  );

  /** Check if a given role has a specific CRUD permission. */
  const isPermissionChecked = useCallback(
    (operation: keyof RecordPermissions, roleId: string): boolean =>
      recordPermissions[operation].includes(roleId),
    [recordPermissions],
  );

  /* ── Form submission handler ─────────────────────────────────── */
  const handleSubmit = useCallback(
    async (e: React.FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setValidation(undefined);

      /* ---- Client-side validation ---- */
      const errors: ValidationError[] = [];

      if (!name.trim()) {
        errors.push({
          propertyName: 'name',
          message: 'Name is required.',
        });
      }
      if (!label.trim()) {
        errors.push({
          propertyName: 'label',
          message: 'Label is required.',
        });
      }
      if (!labelPlural.trim()) {
        errors.push({
          propertyName: 'labelPlural',
          message: 'Label Plural is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({
          message: 'Please correct the errors below.',
          errors,
        });
        return;
      }

      /* ---- Build InputEntity payload ---- */
      const payload: InputEntity = {
        id: entityId,
        name: name.trim(),
        label: label.trim(),
        labelPlural: labelPlural.trim(),
        color: color.trim() || DEFAULT_HEADER_COLOR,
        iconName: iconName.trim() || 'fa fa-database',
        system: isSystem,
        recordScreenIdField: recordScreenIdField || null,
        recordPermissions: {
          canRead: [...recordPermissions.canRead],
          canCreate: [...recordPermissions.canCreate],
          canUpdate: [...recordPermissions.canUpdate],
          canDelete: [...recordPermissions.canDelete],
        },
      };

      /* ---- Execute update mutation ---- */
      try {
        await updateMutateAsync({ id: entityId!, entity: payload });
        /* Navigate to entity detail page on success. */
        navigate(`/admin/entities/${entityId}`);
      } catch (err: unknown) {
        /*
         * Build a human-readable validation from the caught error.
         * The API may throw an Error with a descriptive message, or
         * the TanStack mutation wrapper may surface an ApiError.
         */
        const errorMessage =
          err instanceof Error
            ? err.message
            : 'An unexpected error occurred while updating the entity.';
        setValidation({
          message: errorMessage,
          errors: [{ propertyName: '', message: errorMessage }],
        });
      }
    },
    [
      entityId,
      name,
      label,
      labelPlural,
      color,
      iconName,
      isSystem,
      recordScreenIdField,
      recordPermissions,
      updateMutateAsync,
      navigate,
    ],
  );

  /* ── Derived state ───────────────────────────────────────────── */
  const isDataLoading = entityLoading || rolesLoading;

  /* ── Error state — entity failed to load ─────────────────────── */
  if (entityIsError) {
    return (
      <div className="p-6">
        <div className="rounded-md bg-red-50 p-4" role="alert">
          <p className="text-sm text-red-700">
            {entityErrorObj instanceof Error
              ? entityErrorObj.message
              : 'Failed to load entity data. Please try again later.'}
          </p>
          <Link
            to="/admin/entities"
            className="mt-2 inline-block text-sm font-medium text-red-800 underline hover:text-red-900"
          >
            Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  /* ── Render ──────────────────────────────────────────────────── */
  return (
    <div className="flex flex-col min-h-full">
      {/* ════════════════════════════════════════════════════════════
       * Page Header
       * Matches manage.cshtml lines 1-14:
       *   wv-page-header color, icon, entity label, "Manage" subtitle.
       * ════════════════════════════════════════════════════════════ */}
      <header
        className="flex items-center justify-between px-6 py-4"
        style={{
          backgroundColor: entity?.color ?? DEFAULT_HEADER_COLOR,
        }}
      >
        <div className="flex items-center gap-3">
          {entity?.iconName && (
            <span
              className="inline-flex items-center justify-center h-8 w-8 rounded text-white"
              aria-hidden="true"
            >
              <i className={`fa ${entity.iconName}`} />
            </span>
          )}
          <div>
            <h1 className="text-lg font-semibold text-white leading-tight">
              {entity?.label ?? entity?.name ?? 'Entity'}
            </h1>
            <span className="text-sm text-white/80">Manage</span>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Link
            to={`/admin/entities/${entityId}`}
            className="inline-flex items-center gap-1.5 rounded px-3 py-1.5 text-sm font-medium text-white bg-white/20 hover:bg-white/30 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white transition-colors"
          >
            <i className="fa fa-arrow-left" aria-hidden="true" />
            Back
          </Link>
        </div>
      </header>

      {/* ════════════════════════════════════════════════════════════
       * Entity Admin Sub-Navigation (TabNav)
       * Replaces AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "details")
       * ════════════════════════════════════════════════════════════ */}
      <div className="border-b border-gray-200 bg-white px-6">
        <TabNav
          tabs={entitySubNavTabs}
          visibleTabs={7}
          activeTabId="manage"
          onTabChange={handleTabChange}
        />
      </div>

      {/* ════════════════════════════════════════════════════════════
       * Main Content
       * ════════════════════════════════════════════════════════════ */}
      <main className="flex-1 p-6">
        {isDataLoading ? (
          /* ---- Loading Spinner ---- */
          <div
            className="flex items-center justify-center py-12"
            aria-live="polite"
            aria-busy="true"
          >
            <div
              className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"
              role="status"
            >
              <span className="sr-only">Loading entity data…</span>
            </div>
          </div>
        ) : (
          /* ---- Edit Form ---- */
          <div className="max-w-4xl">
            {/* Mutation-level success feedback (visible briefly before navigate) */}
            {updateSuccess && (
              <div
                className="mb-4 rounded-md bg-green-50 p-3"
                role="status"
              >
                <p className="text-sm text-green-700">
                  Entity updated successfully. Redirecting…
                </p>
              </div>
            )}

            {/* Mutation-level error fallback when validation state is not set */}
            {updateIsError && !validation && (
              <div
                className="mb-4 rounded-md bg-red-50 p-3"
                role="alert"
              >
                <p className="text-sm text-red-700">
                  {updateError instanceof Error
                    ? updateError.message
                    : 'An unexpected error occurred while updating the entity.'}
                </p>
              </div>
            )}

            <DynamicForm
              id="ManageEntity"
              name="ManageEntity"
              labelMode="stacked"
              fieldMode="form"
              onSubmit={handleSubmit}
              validation={validation}
              showValidation={!!validation}
            >
              {/* ──────────────────────────────────────────────────
               * Row 1: Name + System checkbox (two-column grid)
               * Matches manage.cshtml lines 16-24
               * ────────────────────────────────────────────────── */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
                {/* Name */}
                <div>
                  <label
                    htmlFor="entityName"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Name{' '}
                    <span className="text-red-500" aria-hidden="true">
                      *
                    </span>
                  </label>
                  <input
                    type="text"
                    id="entityName"
                    name="name"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    required
                    aria-required="true"
                    aria-invalid={
                      validation?.errors.some(
                        (err) => err.propertyName === 'name',
                      )
                        ? 'true'
                        : undefined
                    }
                    placeholder="e.g. product"
                    autoComplete="off"
                  />
                  {validation?.errors
                    .filter((err) => err.propertyName === 'name')
                    .map((err, i) => (
                      <p
                        key={`name-err-${i}`}
                        className="mt-1 text-xs text-red-600"
                        role="alert"
                      >
                        {err.message}
                      </p>
                    ))}
                </div>

                {/* System checkbox — aligned to bottom of row */}
                <div className="flex items-end pb-2">
                  <label className="inline-flex items-center gap-2 cursor-pointer">
                    <input
                      type="checkbox"
                      id="entitySystem"
                      name="system"
                      checked={isSystem}
                      onChange={(e) => setIsSystem(e.target.checked)}
                      className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                    />
                    <span className="text-sm font-medium text-gray-700">
                      System entity
                    </span>
                  </label>
                </div>
              </div>

              {/* ──────────────────────────────────────────────────
               * Row 2: Label + Label Plural (two-column grid)
               * Matches manage.cshtml lines 25-38
               * ────────────────────────────────────────────────── */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
                {/* Label */}
                <div>
                  <label
                    htmlFor="entityLabel"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Label{' '}
                    <span className="text-red-500" aria-hidden="true">
                      *
                    </span>
                  </label>
                  <input
                    type="text"
                    id="entityLabel"
                    name="label"
                    value={label}
                    onChange={(e) => setLabel(e.target.value)}
                    className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    required
                    aria-required="true"
                    aria-invalid={
                      validation?.errors.some(
                        (err) => err.propertyName === 'label',
                      )
                        ? 'true'
                        : undefined
                    }
                    autoComplete="off"
                  />
                  {validation?.errors
                    .filter((err) => err.propertyName === 'label')
                    .map((err, i) => (
                      <p
                        key={`label-err-${i}`}
                        className="mt-1 text-xs text-red-600"
                        role="alert"
                      >
                        {err.message}
                      </p>
                    ))}
                </div>

                {/* Label Plural */}
                <div>
                  <label
                    htmlFor="entityLabelPlural"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Label Plural{' '}
                    <span className="text-red-500" aria-hidden="true">
                      *
                    </span>
                  </label>
                  <input
                    type="text"
                    id="entityLabelPlural"
                    name="labelPlural"
                    value={labelPlural}
                    onChange={(e) => setLabelPlural(e.target.value)}
                    className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    required
                    aria-required="true"
                    aria-invalid={
                      validation?.errors.some(
                        (err) => err.propertyName === 'labelPlural',
                      )
                        ? 'true'
                        : undefined
                    }
                    autoComplete="off"
                  />
                  {validation?.errors
                    .filter((err) => err.propertyName === 'labelPlural')
                    .map((err, i) => (
                      <p
                        key={`labelPlural-err-${i}`}
                        className="mt-1 text-xs text-red-600"
                        role="alert"
                      >
                        {err.message}
                      </p>
                    ))}
                </div>
              </div>

              {/* ──────────────────────────────────────────────────
               * Row 3: Color + Icon Name (two-column grid)
               * Matches manage.cshtml lines 39-50
               * ────────────────────────────────────────────────── */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
                {/* Color */}
                <div>
                  <label
                    htmlFor="entityColor"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Color
                  </label>
                  <div className="flex items-center gap-2">
                    <input
                      type="color"
                      id="entityColorPicker"
                      value={color}
                      onChange={(e) => setColor(e.target.value)}
                      className="h-9 w-9 rounded border border-gray-300 cursor-pointer p-0"
                      aria-label="Pick entity colour"
                    />
                    <input
                      type="text"
                      id="entityColor"
                      name="color"
                      value={color}
                      onChange={(e) => setColor(e.target.value)}
                      className="block flex-1 rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      placeholder="#f44336"
                      autoComplete="off"
                    />
                  </div>
                </div>

                {/* Icon Name */}
                <div>
                  <label
                    htmlFor="entityIconName"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Icon Name
                  </label>
                  <div className="flex items-center gap-2">
                    <span
                      className="inline-flex items-center justify-center h-9 w-9 rounded border border-gray-300 bg-gray-50 text-gray-600"
                      aria-hidden="true"
                    >
                      <i className={iconName || 'fa fa-database'} />
                    </span>
                    <input
                      type="text"
                      id="entityIconName"
                      name="iconName"
                      value={iconName}
                      onChange={(e) => setIconName(e.target.value)}
                      className="block flex-1 rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      placeholder="fa fa-database"
                      autoComplete="off"
                    />
                  </div>
                </div>
              </div>

              {/* ──────────────────────────────────────────────────
               * Row 4: RecordScreenIdField + Entity Id (two-column)
               * Matches manage.cshtml lines 51-64
               * RecordScreenIdField — dropdown from entity.fields
               * Entity Id — read-only display of entity.id
               * ────────────────────────────────────────────────── */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
                {/* RecordScreenIdField */}
                <div>
                  <label
                    htmlFor="recordScreenIdField"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Record Screen Id Field
                  </label>
                  <select
                    id="recordScreenIdField"
                    name="recordScreenIdField"
                    value={recordScreenIdField}
                    onChange={(e) => setRecordScreenIdField(e.target.value)}
                    className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 bg-white"
                  >
                    <option value="">— none —</option>
                    {fieldOptions.map((opt) => (
                      <option key={opt.value} value={opt.value}>
                        {opt.label}
                      </option>
                    ))}
                  </select>
                </div>

                {/* Entity Id (read-only display) */}
                <div>
                  <label
                    htmlFor="entityIdDisplay"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Entity Id
                  </label>
                  <input
                    type="text"
                    id="entityIdDisplay"
                    value={entity?.id ?? entityId ?? ''}
                    readOnly
                    className="block w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm cursor-not-allowed"
                    tabIndex={-1}
                    aria-readonly="true"
                  />
                </div>
              </div>

              {/* ──────────────────────────────────────────────────
               * Row 5: Record Permissions Grid (CRUD × Roles)
               *
               * Replaces manage.cshtml lines 65+ / manage.cshtml.cs
               * RecordPermissions checkbox grid.
               * Rows = CRUD operations, Columns = system roles.
               * Each cell is a checkbox toggling the role id in / out
               * of the corresponding RecordPermissions array.
               * ────────────────────────────────────────────────── */}
              {roles.length > 0 && (
                <fieldset className="mb-6">
                  <legend className="text-sm font-medium text-gray-700 mb-2">
                    Record Permissions
                  </legend>
                  <div className="overflow-x-auto rounded-md border border-gray-200">
                    <table className="min-w-full text-sm">
                      <thead>
                        <tr className="bg-gray-50">
                          <th
                            scope="col"
                            className="px-4 py-2 text-start font-medium text-gray-600"
                          >
                            Operation
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
                      <tbody>
                        {CRUD_OPERATIONS.map((op) => (
                          <tr
                            key={op.key}
                            className="border-t border-gray-100 hover:bg-gray-50 transition-colors"
                          >
                            <td className="px-4 py-2 font-medium text-gray-700">
                              {op.label}
                            </td>
                            {roles.map((role) => {
                              const checked = isPermissionChecked(
                                op.key,
                                role.id,
                              );
                              const checkboxId = `perm-${op.key}-${role.id}`;
                              return (
                                <td
                                  key={role.id}
                                  className="px-4 py-2 text-center"
                                >
                                  <input
                                    type="checkbox"
                                    id={checkboxId}
                                    checked={checked}
                                    onChange={() =>
                                      handlePermissionToggle(
                                        op.key,
                                        role.id,
                                      )
                                    }
                                    className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                                    aria-label={`${op.label} permission for ${role.name}`}
                                  />
                                </td>
                              );
                            })}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </fieldset>
              )}

              {/* ──────────────────────────────────────────────────
               * Action Buttons
               * Matches manage.cshtml lines 9-14 (Save + Cancel)
               * ────────────────────────────────────────────────── */}
              <div className="flex items-center gap-3 pt-4 border-t border-gray-200">
                <button
                  type="submit"
                  disabled={updatePending}
                  className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {updatePending ? (
                    <>
                      <span
                        className="animate-spin inline-block h-4 w-4 border-2 border-white/30 border-t-white rounded-full"
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
                  to={`/admin/entities/${entityId}`}
                  className="inline-flex items-center rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 transition-colors"
                >
                  Cancel
                </Link>
              </div>
            </DynamicForm>
          </div>
        )}
      </main>
    </div>
  );
}
