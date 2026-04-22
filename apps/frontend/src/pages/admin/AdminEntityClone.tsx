/**
 * AdminEntityClone — Entity Clone Page
 *
 * Route:    /admin/entities/:entityId/clone
 * Replaces: WebVella.Erp.Plugins.SDK/Pages/entity/clone.cshtml[.cs]
 *
 * Clones an existing entity by loading the source entity metadata via
 * useEntity(), pre-populating a form with its values, and submitting a
 * clone request to POST /v1/entities/{sourceEntityId}/clone via an inline
 * useMutation backed by the shared `post()` API helper.
 *
 * Data flow:
 *  1. useEntity(entityId)  — loads source entity for pre-population
 *  2. useRoles()           — loads all system roles for the CRUD permission grid
 *  3. User edits name/label/colour/icon/permissions on the pre-filled form
 *  4. useMutation + post() — submits clone request
 *  5. On success:          — invalidates ['entities'] cache, navigates to new entity
 *
 * Monolith parity (clone.cshtml.cs):
 *  - OnGet():  EntityManager.ReadEntity(RecordId) → useEntity()
 *  - OnPost(): EntityManager.CloneEntity(RecordId.Value, inputEntity) → POST clone
 *  - Permission grid: rows = CRUD operations, columns = roles (checkbox matrix)
 *  - Optional custom Id (GUID) for the new entity
 *  - Redirect to cloned entity detail page on success
 *
 * AAP compliance:
 *  - §0.4.3  — Full entity CRUD via Entity Management service
 *  - §0.5.1  — EntityManager.CloneEntity → POST /v1/entities/:id/clone
 *  - §0.8.1  — Self-contained SPA page with no SSR
 *  - §0.8.6  — API calls via /v1/ prefixed endpoints
 *
 * @module pages/admin/AdminEntityClone
 */

import { useState, useEffect, useCallback, useMemo } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import { useEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import { post } from '../../api/client';
import type { Entity, InputEntity, RecordPermissions } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';

/* ═══════════════════════════════════════════════════════════════════
 * Local Types
 * ═══════════════════════════════════════════════════════════════════ */

/** Entity admin sub-navigation tab definition. */
interface SubNavTab {
  id: string;
  label: string;
  href: string;
}

/** CRUD operation descriptor for the permission grid rows. */
interface CrudOperation {
  /** Key matching RecordPermissions property name. */
  key: keyof RecordPermissions;
  /** Human-readable label for the row header. */
  label: string;
}

/** Response shape from the clone endpoint. */
interface CloneResponse {
  /** The entity ID of the newly cloned entity (may be at root level). */
  id?: string;
  /** The newly cloned entity (may be nested under object). */
  object?: Entity;
  /** Whether the operation succeeded. */
  success?: boolean;
  /** Optional status message. */
  message?: string;
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
 * AdminEntityClone renders the entity clone page.
 *
 * Replaces the monolith SDK entity clone flow:
 * - Route `/sdk/objects/entity/c/clone?recordId={id}` → `/admin/entities/:entityId/clone`
 * - `EntityManager.CloneEntity()` → inline useMutation via `post()`
 * - `SecurityManager.GetAllRoles()` → `useRoles()` TanStack Query hook
 * - Razor form fields → controlled React inputs with Tailwind CSS styling
 * - RecordPermissions checkbox grid → role×CRUD checkbox matrix
 */
export default function AdminEntityClone(): React.ReactNode {
  /* ── Route params & navigation ───────────────────────────────── */
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ── Data fetching ───────────────────────────────────────────── */
  const {
    data: sourceEntity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityErrorObj,
  } = useEntity(entityId ?? '');

  const { data: rolesResponse, isLoading: rolesLoading } = useRoles();

  /**
   * Extract the roles array from the wrapped ApiResponse.
   * useRoles() returns ApiResponse<ErpRole[]>, so we access .object.
   */
  const roles: ErpRole[] = useMemo(
    () => (rolesResponse as { object?: ErpRole[] } | undefined)?.object ?? [],
    [rolesResponse],
  );

  /* ── Form state ──────────────────────────────────────────────── */
  const [name, setName] = useState('');
  const [newId, setNewId] = useState('');
  const [label, setLabel] = useState('');
  const [labelPlural, setLabelPlural] = useState('');
  const [color, setColor] = useState(DEFAULT_HEADER_COLOR);
  const [iconName, setIconName] = useState('fa fa-database');
  const [isSystem, setIsSystem] = useState(false);
  const [recordPermissions, setRecordPermissions] =
    useState<RecordPermissions>(EMPTY_PERMISSIONS);

  /** Validation state matching DynamicForm expectations. */
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* ── Pre-populate form when source entity loads ──────────────── */
  useEffect(() => {
    if (sourceEntity) {
      /* Name is left blank for the user to provide a new name (clone behaviour). */
      setName('');
      setNewId('');
      setLabel(sourceEntity.label ?? '');
      setLabelPlural(sourceEntity.labelPlural ?? '');
      setColor(sourceEntity.color ?? DEFAULT_HEADER_COLOR);
      setIconName(sourceEntity.iconName ?? 'fa fa-database');
      setIsSystem(sourceEntity.system ?? false);
      setRecordPermissions(
        sourceEntity.recordPermissions
          ? {
              canRead: [...sourceEntity.recordPermissions.canRead],
              canCreate: [...sourceEntity.recordPermissions.canCreate],
              canUpdate: [...sourceEntity.recordPermissions.canUpdate],
              canDelete: [...sourceEntity.recordPermissions.canDelete],
            }
          : EMPTY_PERMISSIONS,
      );
      setValidation(undefined);
    }
  }, [sourceEntity]);

  /* ── Clone mutation (inline useMutation with post()) ─────────── */
  const cloneMutation = useMutation<CloneResponse, Error, InputEntity>({
    mutationFn: async (payload: InputEntity) => {
      const response = await post<CloneResponse>(
        `/v1/entities/${entityId}/clone`,
        payload,
      );
      /*
       * post() returns ApiResponse<CloneResponse>. The actual data
       * is in the response.object property when the generic wrapper
       * is used. We handle both wrapped and unwrapped shapes.
       */
      const result = (response as unknown as { object?: CloneResponse })
        ?.object ?? (response as unknown as CloneResponse);
      return result;
    },
    onSuccess: (data) => {
      /* Invalidate the entities list cache so the new clone appears. */
      queryClient.invalidateQueries({ queryKey: ['entities'] });

      /* Navigate to the newly cloned entity detail page. */
      const clonedId = data?.object?.id ?? data?.id;
      if (clonedId) {
        navigate(`/admin/entities/${clonedId}`);
      } else {
        navigate('/admin/entities');
      }
    },
  });

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

      /* Validate optional GUID format if provided. */
      if (newId.trim()) {
        const guidRegex =
          /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
        if (!guidRegex.test(newId.trim())) {
          errors.push({
            propertyName: 'id',
            message: 'Id must be a valid GUID (e.g. 00000000-0000-0000-0000-000000000000).',
          });
        }
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
        name: name.trim(),
        label: label.trim(),
        labelPlural: labelPlural.trim(),
        color: color.trim() || DEFAULT_HEADER_COLOR,
        iconName: iconName.trim() || 'fa fa-database',
        system: isSystem,
        recordPermissions: {
          canRead: [...recordPermissions.canRead],
          canCreate: [...recordPermissions.canCreate],
          canUpdate: [...recordPermissions.canUpdate],
          canDelete: [...recordPermissions.canDelete],
        },
      };

      /* Include custom Id only when the user provided one. */
      if (newId.trim()) {
        payload.id = newId.trim();
      }

      /* ---- Execute clone mutation ---- */
      try {
        await cloneMutation.mutateAsync(payload);
      } catch (err: unknown) {
        const errorMessage =
          err instanceof Error
            ? err.message
            : 'An unexpected error occurred while cloning the entity.';
        setValidation({
          message: errorMessage,
          errors: [{ propertyName: '', message: errorMessage }],
        });
      }
    },
    [
      name,
      newId,
      label,
      labelPlural,
      color,
      iconName,
      isSystem,
      recordPermissions,
      cloneMutation,
    ],
  );

  /* ── Derived state ───────────────────────────────────────────── */
  const isDataLoading = entityLoading || rolesLoading;

  /** Entity admin sub-navigation tabs — no tab is "active" on clone. */
  const entitySubNavTabs = useMemo<SubNavTab[]>(
    () => [
      { id: 'details', label: 'Details', href: `/admin/entities/${entityId}` },
      {
        id: 'fields',
        label: 'Fields',
        href: `/admin/entities/${entityId}/fields`,
      },
      {
        id: 'relations',
        label: 'Relations',
        href: `/admin/entities/${entityId}/relations`,
      },
      {
        id: 'data',
        label: 'Data',
        href: `/admin/entities/${entityId}/data`,
      },
      {
        id: 'pages',
        label: 'Pages',
        href: `/admin/entities/${entityId}/pages`,
      },
      {
        id: 'web-api',
        label: 'Web API',
        href: `/admin/entities/${entityId}/web-api`,
      },
    ],
    [entityId],
  );

  /**
   * Check if a given role has a specific CRUD permission.
   * Used by the checkbox grid to determine checked state.
   */
  const isPermissionChecked = useCallback(
    (operation: keyof RecordPermissions, roleId: string): boolean =>
      recordPermissions[operation].includes(roleId),
    [recordPermissions],
  );

  /* ── Error state ─────────────────────────────────────────────── */
  if (entityError) {
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
       * ════════════════════════════════════════════════════════════ */}
      <header
        className="flex items-center justify-between px-6 py-4"
        style={{
          backgroundColor: sourceEntity?.color ?? DEFAULT_HEADER_COLOR,
        }}
      >
        <div className="flex items-center gap-3">
          {sourceEntity?.iconName && (
            <span
              className="inline-flex items-center justify-center h-8 w-8 rounded text-white"
              aria-hidden="true"
            >
              <i className={`fa ${sourceEntity.iconName}`} />
            </span>
          )}
          <div>
            <h1 className="text-lg font-semibold text-white leading-tight">
              {sourceEntity?.label ?? sourceEntity?.name ?? 'Entity'}
            </h1>
            <span className="text-sm text-white/80">Clone Entity</span>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Link
            to={`/admin/entities/${entityId}`}
            className="inline-flex items-center gap-1.5 rounded px-3 py-1.5 text-sm font-medium text-white bg-white/20 hover:bg-white/30 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white transition-colors"
          >
            Cancel
          </Link>
        </div>
      </header>

      {/* ════════════════════════════════════════════════════════════
       * Entity Admin Sub-Navigation
       * ════════════════════════════════════════════════════════════ */}
      <nav
        className="border-b border-gray-200 bg-white px-6"
        aria-label="Entity admin navigation"
      >
        <ul className="flex gap-0 -mb-px" role="tablist">
          {entitySubNavTabs.map((tab) => (
            <li key={tab.id} role="presentation">
              <Link
                to={tab.href}
                role="tab"
                aria-selected={false}
                className="inline-block px-4 py-2.5 text-sm font-medium border-b-2 border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300 transition-colors"
              >
                {tab.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>

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
          /* ---- Clone Form ---- */
          <div className="max-w-4xl">
            <DynamicForm
              id="CloneEntity"
              name="CloneEntity"
              labelMode="stacked"
              onSubmit={handleSubmit}
              validation={validation}
              showValidation={!!validation}
            >
              {/* ──────────────────────────────────────────────────
               * Row 1: Name + Id (two-column grid)
               * Matches clone.cshtml lines 20-33
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

                {/* Id (optional GUID override) */}
                <div>
                  <label
                    htmlFor="entityId"
                    className="block text-sm font-medium text-gray-700 mb-1"
                  >
                    Id
                  </label>
                  <input
                    type="text"
                    id="entityId"
                    name="id"
                    value={newId}
                    onChange={(e) => setNewId(e.target.value)}
                    className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                    aria-invalid={
                      validation?.errors.some(
                        (err) => err.propertyName === 'id',
                      )
                        ? 'true'
                        : undefined
                    }
                    placeholder="leave empty to autogenerate"
                    autoComplete="off"
                  />
                  {validation?.errors
                    .filter((err) => err.propertyName === 'id')
                    .map((err, i) => (
                      <p
                        key={`id-err-${i}`}
                        className="mt-1 text-xs text-red-600"
                        role="alert"
                      >
                        {err.message}
                      </p>
                    ))}
                </div>
              </div>

              {/* ──────────────────────────────────────────────────
               * Row 2: System checkbox
               * Matches clone.cshtml lines 34-38
               * ────────────────────────────────────────────────── */}
              <div className="mb-4">
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
                    {isSystem ? 'System entity' : 'Not a system entity'}
                  </span>
                </label>
              </div>

              {/* ──────────────────────────────────────────────────
               * Row 3: Label + LabelPlural (two-column grid)
               * Matches clone.cshtml lines 39-51
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
               * Row 4: Color + IconName (two-column grid)
               * Matches clone.cshtml lines 52-58
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
               * Record Permissions Grid (CRUD × Roles)
               *
               * Replaces the monolith permission grid from
               * clone.cshtml lines 59+ / clone.cshtml.cs lines 90-150.
               * Rows = CRUD operations, Columns = system roles.
               * Each cell is a checkbox toggling the role id in/out
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
                                      handlePermissionToggle(op.key, role.id)
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
               * Matches clone.cshtml lines 14-15 (Clone + Cancel)
               * ────────────────────────────────────────────────── */}
              <div className="flex items-center gap-3 pt-4 border-t border-gray-200">
                <button
                  type="submit"
                  disabled={cloneMutation.isPending}
                  className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {cloneMutation.isPending ? (
                    <>
                      <span
                        className="animate-spin inline-block h-4 w-4 border-2 border-white/30 border-t-white rounded-full"
                        aria-hidden="true"
                      />
                      Cloning…
                    </>
                  ) : (
                    <>
                      <i className="fa fa-copy" aria-hidden="true" />
                      Clone Entity
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
