/**
 * EntityDetails — Entity Detail View (read-only)
 *
 * Displays a single entity's metadata in read-only mode, a CRUD × Roles
 * permission grid, entity admin sub-navigation tabs, and header action buttons
 * (Clone, Manage, Delete).
 *
 * Route: `/entities/:entityId`
 *
 * Replaces:
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/details.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/details.cshtml.cs`
 *
 * Source mapping:
 *   - `DetailsModel.PageInit()`  → `useEntity(entityId)` TanStack Query hook
 *   - `DetailsModel.OnPost()`    → `useDeleteEntity()` mutation + confirmation modal
 *   - `RecordScreenIdFieldName`  → `resolveRecordScreenIdFieldName()` helper
 *   - `PermissionOptions × RoleOptions grid` → inline CRUD × Roles table
 *   - `AdminPageUtils.GetEntityAdminSubNav()` → NavLink-based sub-nav tabs
 *   - Header actions (Clone/Delete/Manage) → Link / button / disabled span
 *
 * AAP compliance:
 *   - §0.4.3 — React admin module with entity detail view
 *   - §0.5.1 — `EntityManager.ReadEntity` → `useEntity`, `EntityManager.DeleteEntity` → `useDeleteEntity`
 *   - §0.7.7 — Razor ViewComponent → React page component mapping
 *   - §0.8.1 — Pure static SPA, Tailwind CSS only, default export for lazy loading
 *
 * @module pages/entities/EntityDetails
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, useNavigate, Link, NavLink } from 'react-router';

import { useEntity, useDeleteEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type { Entity, RecordPermissions, Field } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import Modal from '../../components/common/Modal';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Sub-navigation tabs shared across all entity admin pages.
 * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "details")` from
 * the monolith's SDK plugin.
 *
 * Each entry specifies a `label` for the visible text, a `path` suffix
 * appended to `/entities/:entityId`, and an optional `end` flag for NavLink
 * exact matching (only the root "Details" tab needs exact match).
 */
const SUB_NAV_TABS: ReadonlyArray<{
  label: string;
  path: string;
  end?: boolean;
}> = [
  { label: 'Details', path: '', end: true },
  { label: 'Fields', path: '/fields' },
  { label: 'Relations', path: '/relations' },
  { label: 'Data', path: '/data' },
  { label: 'Pages', path: '/pages' },
  { label: 'Web API', path: '/web-api' },
] as const;

/**
 * Human-readable labels for the four CRUD permission operations.
 * Maps to the `RecordPermissions` interface keys so the permission grid can
 * be rendered declaratively.
 */
const PERMISSION_OPERATIONS: ReadonlyArray<{
  key: keyof RecordPermissions;
  label: string;
}> = [
  { key: 'canCreate', label: 'Create' },
  { key: 'canRead', label: 'Read' },
  { key: 'canUpdate', label: 'Update' },
  { key: 'canDelete', label: 'Delete' },
] as const;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Resolves the human-readable field name for the entity's
 * `recordScreenIdField` GUID. Falls back to `"id"` when no override is set
 * or the GUID cannot be matched to a field in the entity's field list.
 *
 * Mirrors `DetailsModel` logic:
 * ```csharp
 * if (ErpEntity.RecordScreenIdField != null) {
 *     var screenIdField = ErpEntity.Fields.FirstOrDefault(f => f.Id == ErpEntity.RecordScreenIdField);
 *     if (screenIdField != null) RecordScreenIdFieldName = screenIdField.Name;
 * }
 * ```
 */
function resolveRecordScreenIdFieldName(entity: Entity | undefined): string {
  if (!entity?.recordScreenIdField) {
    return 'id';
  }

  const matched: Field | undefined = entity.fields.find(
    (f: Field) => f.id === entity.recordScreenIdField,
  );

  return matched?.name ?? 'id';
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Entity details page.
 *
 * Renders entity metadata in read-only mode with a permission grid, sub-nav
 * tabs, and header action buttons. Supports delete confirmation via a modal
 * dialog (disabled for system entities).
 */
function EntityDetails(): React.JSX.Element {
  /* -------------------------------------------------------------------- */
  /*  Route params & navigation                                           */
  /* -------------------------------------------------------------------- */
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  /* -------------------------------------------------------------------- */
  /*  Server state                                                        */
  /* -------------------------------------------------------------------- */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityErrorObj,
  } = useEntity(entityId ?? '');

  const { data: rolesData, isLoading: rolesLoading } = useRoles();

  /* -------------------------------------------------------------------- */
  /*  Mutations                                                           */
  /* -------------------------------------------------------------------- */
  const deleteEntityMutation = useDeleteEntity();

  /* -------------------------------------------------------------------- */
  /*  Local UI state                                                      */
  /* -------------------------------------------------------------------- */
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);

  /* -------------------------------------------------------------------- */
  /*  Derived data                                                        */
  /* -------------------------------------------------------------------- */

  /** Extract the roles array from the ApiResponse wrapper. */
  const roles = useMemo<ErpRole[]>(
    () => rolesData?.object ?? [],
    [rolesData],
  );

  /** Resolved field name for the entity's screen-ID field. */
  const recordScreenIdFieldName = useMemo(
    () => resolveRecordScreenIdFieldName(entity),
    [entity],
  );

  /* -------------------------------------------------------------------- */
  /*  Handlers                                                            */
  /* -------------------------------------------------------------------- */

  /** Opens the delete confirmation modal. */
  const handleDeleteModalOpen = useCallback(() => {
    setIsDeleteModalOpen(true);
  }, []);

  /** Closes the delete confirmation modal and resets mutation state. */
  const handleDeleteModalClose = useCallback(() => {
    setIsDeleteModalOpen(false);
    deleteEntityMutation.reset();
  }, [deleteEntityMutation]);

  /**
   * Executes the delete mutation and navigates to the entity list on
   * success. Keeps the modal open on failure so the user can see the error.
   */
  const handleDeleteConfirm = useCallback(async () => {
    if (!entityId) return;
    try {
      await deleteEntityMutation.mutateAsync(entityId);
      navigate('/entities');
    } catch {
      // Mutation error is surfaced via deleteEntityMutation.error — modal
      // stays open so the user can read the error and dismiss.
    }
  }, [deleteEntityMutation, entityId, navigate]);

  /* -------------------------------------------------------------------- */
  /*  Early-return states                                                 */
  /* -------------------------------------------------------------------- */

  /** Loading skeleton while data is being fetched. */
  if (entityLoading || rolesLoading) {
    return (
      <div className="flex items-center justify-center min-h-[32rem]">
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-indigo-600"
            role="status"
            aria-label="Loading entity details"
          />
          <span className="text-sm text-gray-500">Loading entity…</span>
        </div>
      </div>
    );
  }

  /** Error state — API call failed. */
  if (entityError) {
    return (
      <div className="flex items-center justify-center min-h-[32rem]">
        <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center max-w-md">
          <h2 className="text-lg font-semibold text-red-800">
            Failed to load entity
          </h2>
          <p className="mt-2 text-sm text-red-600">
            {entityErrorObj?.message ?? 'An unexpected error occurred.'}
          </p>
          <Link
            to="/entities"
            className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  /** Not-found state — entity ID resolved to nothing. */
  if (!entity) {
    return (
      <div className="flex items-center justify-center min-h-[32rem]">
        <div className="rounded-lg border border-amber-200 bg-amber-50 p-6 text-center max-w-md">
          <h2 className="text-lg font-semibold text-amber-800">
            Entity not found
          </h2>
          <p className="mt-2 text-sm text-amber-600">
            The entity you requested does not exist or has been deleted.
          </p>
          <Link
            to="/entities"
            className="mt-4 inline-block rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-amber-600"
          >
            Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  /* ================================================================== */
  /*  MAIN RENDER                                                       */
  /* ================================================================== */

  const isSystem: boolean = entity.system;

  return (
    <div className="space-y-6">
      {/* ---------------------------------------------------------------- */}
      {/*  PAGE HEADER                                                     */}
      {/* ---------------------------------------------------------------- */}
      <header className="rounded-lg border border-gray-200 bg-white shadow-sm">
        {/* Title row */}
        <div className="flex flex-wrap items-center gap-4 px-6 py-4">
          {/* Entity icon with accent colour */}
          <span
            className="flex h-10 w-10 shrink-0 items-center justify-center rounded-md text-white"
            style={{ backgroundColor: entity.color || '#dc3545' }}
            aria-hidden="true"
          >
            <i className={`fa ${entity.iconName || 'fa-database'}`} />
          </span>

          {/* Title block */}
          <div className="min-w-0 flex-1">
            <p className="text-xs font-medium uppercase tracking-wide text-gray-500">
              <Link
                to="/entities"
                className="hover:text-indigo-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              >
                Entities
              </Link>
            </p>
            <h1 className="truncate text-xl font-bold text-gray-900">
              {entity.name}
            </h1>
            <p className="text-sm text-gray-500">Details</p>
          </div>

          {/* Action buttons */}
          <div className="flex flex-wrap items-center gap-2">
            {/* Clone — hidden entirely for system entities */}
            {!isSystem && (
              <Link
                to={`/entities/${entityId}/clone`}
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
              >
                <i className="fa fa-clone" aria-hidden="true" />
                Clone
              </Link>
            )}

            {/* Delete — disabled (locked) for system entities */}
            {isSystem ? (
              <span
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-200 bg-gray-100 px-3 py-2 text-sm font-medium text-gray-400 cursor-not-allowed select-none"
                title="System entities cannot be deleted"
                aria-disabled="true"
              >
                <i className="fa fa-lock" aria-hidden="true" />
                Delete locked
              </span>
            ) : (
              <button
                type="button"
                onClick={handleDeleteModalOpen}
                className="inline-flex items-center gap-1.5 rounded-md border border-red-300 bg-white px-3 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              >
                <i className="fa fa-trash-alt" aria-hidden="true" />
                Delete
              </button>
            )}

            {/* Manage */}
            <Link
              to={`/entities/${entityId}/manage`}
              className="inline-flex items-center gap-1.5 rounded-md bg-indigo-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
            >
              <i className="fa fa-cog" aria-hidden="true" />
              Manage
            </Link>
          </div>
        </div>

        {/* Sub-navigation tabs */}
        <nav
          className="flex gap-0 overflow-x-auto border-t border-gray-200 px-6"
          aria-label="Entity administration"
        >
          {SUB_NAV_TABS.map((tab) => (
            <NavLink
              key={tab.label}
              to={`/entities/${entityId}${tab.path}`}
              end={tab.end}
              className={({ isActive }) =>
                [
                  'inline-block whitespace-nowrap border-b-2 px-4 py-3 text-sm font-medium transition-colors',
                  isActive
                    ? 'border-indigo-600 text-indigo-600'
                    : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700',
                ].join(' ')
              }
            >
              {tab.label}
            </NavLink>
          ))}
        </nav>
      </header>

      {/* ---------------------------------------------------------------- */}
      {/*  METADATA SECTION                                                */}
      {/* ---------------------------------------------------------------- */}
      <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <h2 className="sr-only">Entity Metadata</h2>

        <dl className="grid grid-cols-1 gap-x-8 gap-y-6 sm:grid-cols-2">
          {/* Name */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Name</dt>
            <dd className="mt-1 text-sm text-gray-900">{entity.name}</dd>
          </div>

          {/* Label */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Label</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {entity.label || '—'}
            </dd>
          </div>

          {/* Label Plural */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Label Plural</dt>
            <dd className="mt-1 text-sm text-gray-900">
              {entity.labelPlural || '—'}
            </dd>
          </div>

          {/* System */}
          <div>
            <dt className="text-sm font-medium text-gray-500">System</dt>
            <dd className="mt-1">
              {isSystem ? (
                <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
                  Yes
                </span>
              ) : (
                <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                  No
                </span>
              )}
            </dd>
          </div>

          {/* Color */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Color</dt>
            <dd className="mt-1 flex items-center gap-2 text-sm text-gray-900">
              <span
                className="inline-block h-5 w-5 rounded border border-gray-200"
                style={{ backgroundColor: entity.color || '#dc3545' }}
                aria-label={`Entity colour: ${entity.color || '#dc3545'}`}
              />
              {entity.color || '#dc3545'}
            </dd>
          </div>

          {/* Icon Name */}
          <div>
            <dt className="text-sm font-medium text-gray-500">Icon Name</dt>
            <dd className="mt-1 flex items-center gap-2 text-sm text-gray-900">
              <i
                className={`fa ${entity.iconName || 'fa-database'}`}
                aria-hidden="true"
              />
              {entity.iconName || '—'}
            </dd>
          </div>

          {/* Record Screen Id Field */}
          <div className="sm:col-span-2">
            <dt className="text-sm font-medium text-gray-500">
              Record Screen Id Field
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {recordScreenIdFieldName}
            </dd>
          </div>
        </dl>
      </section>

      {/* ---------------------------------------------------------------- */}
      {/*  PERMISSION GRID (read-only)                                     */}
      {/* ---------------------------------------------------------------- */}
      <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <h2 className="mb-4 text-base font-semibold text-gray-900">
          Record Permissions
        </h2>

        {roles.length === 0 ? (
          <p className="text-sm text-gray-500">No roles available.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200">
              <thead>
                <tr>
                  <th
                    scope="col"
                    className="sticky inset-inline-start-0 z-10 bg-white px-3 py-2 text-start text-xs font-medium uppercase tracking-wide text-gray-500"
                  >
                    Permission
                  </th>
                  {roles.map((role: ErpRole) => (
                    <th
                      key={role.id}
                      scope="col"
                      className="px-3 py-2 text-center text-xs font-medium uppercase tracking-wide text-gray-500"
                    >
                      {role.name}
                    </th>
                  ))}
                </tr>
              </thead>

              <tbody className="divide-y divide-gray-100">
                {PERMISSION_OPERATIONS.map((op) => (
                  <tr key={op.key}>
                    <td className="sticky inset-inline-start-0 z-10 bg-white px-3 py-2 text-sm font-medium text-gray-700">
                      {op.label}
                    </td>

                    {roles.map((role: ErpRole) => {
                      const permissionArray: string[] =
                        entity.recordPermissions?.[op.key] ?? [];
                      const isGranted: boolean = permissionArray.includes(
                        role.id,
                      );

                      return (
                        <td
                          key={role.id}
                          className="px-3 py-2 text-center"
                        >
                          <input
                            type="checkbox"
                            checked={isGranted}
                            disabled
                            readOnly
                            aria-label={`${op.label} permission for role ${role.name}`}
                            className="h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
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
      </section>

      {/* ---------------------------------------------------------------- */}
      {/*  DELETE CONFIRMATION MODAL                                       */}
      {/* ---------------------------------------------------------------- */}
      <Modal
        isVisible={isDeleteModalOpen}
        title="Delete Entity"
        onClose={handleDeleteModalClose}
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={handleDeleteModalClose}
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleDeleteConfirm}
              disabled={deleteEntityMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {deleteEntityMutation.isPending ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete the entity{' '}
          <strong className="font-semibold text-gray-900">
            {entity.name}
          </strong>
          ? This action cannot be undone. All fields, relations, and records
          associated with this entity will be permanently removed.
        </p>

        {deleteEntityMutation.isError && (
          <div className="mt-3 rounded-md border border-red-200 bg-red-50 p-3">
            <p className="text-sm text-red-600">
              {deleteEntityMutation.error?.message ??
                'Failed to delete entity. Please try again.'}
            </p>
          </div>
        )}
      </Modal>
    </div>
  );
}

export default EntityDetails;
