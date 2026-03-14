/**
 * AdminEntityDetails — Entity details page (read-only)
 *
 * Route: `/admin/entities/:entityId`
 *
 * Replaces:
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/details.cshtml`
 *   - `WebVella.Erp.Plugins.SDK/Pages/entity/details.cshtml.cs`
 *
 * Displays entity metadata in read-only mode with:
 *   - Page header (entity name, icon, color accent, area breadcrumb)
 *   - Action buttons: Clone, Delete (non-system only), Manage
 *   - Sub-navigation tabs: Details, Fields, Relations, Data, Pages, Web API
 *   - Metadata fields: Name, Id, Label, LabelPlural, Color, IconName,
 *     RecordScreenIdField, System flag
 *   - Record permissions CRUD grid (roles × create/read/update/delete)
 *   - Delete confirmation modal
 *
 * Data sources:
 *   - `useEntity(entityId)` — entity metadata (replaces EntityManager.ReadEntity)
 *   - `useRoles()` — role list for permission grid (replaces AdminPageUtils.GetUserRoles)
 *   - `useDeleteEntity()` — entity deletion mutation (replaces EntityManager.DeleteEntity)
 *
 * @module pages/admin/AdminEntityDetails
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router';

import { useEntity, useDeleteEntity } from '../../hooks/useEntities';
import { useRoles } from '../../hooks/useUsers';
import type { Entity, RecordPermissions } from '../../types/entity';
import type { ErpRole } from '../../types/user';
import Modal from '../../components/common/Modal';
import TabNav, { type TabConfig } from '../../components/common/TabNav';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Single row in the permission grid.
 * One row per role, with boolean CRUD permission flags computed from the
 * entity's RecordPermissions arrays.
 */
interface PermissionGridRow {
  /** Role GUID */
  roleId: string;
  /** Role display name */
  roleName: string;
  /** Whether this role has create permission */
  canCreate: boolean;
  /** Whether this role has read permission */
  canRead: boolean;
  /** Whether this role has update permission */
  canUpdate: boolean;
  /** Whether this role has delete permission */
  canDelete: boolean;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Entity admin sub-navigation tab definitions.
 *
 * Replaces `AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "details")`
 * from the monolith which generated HTML link elements for the header
 * toolbar. Each tab maps to a distinct entity admin section route.
 */
const ENTITY_ADMIN_TABS: TabConfig[] = [
  { id: 'details', label: 'Details' },
  { id: 'fields', label: 'Fields' },
  { id: 'relations', label: 'Relations' },
  { id: 'data', label: 'Data' },
  { id: 'pages', label: 'Pages' },
  { id: 'web-api', label: 'Web API' },
];

/** Number of visible tabs in the sub-navigation */
const VISIBLE_TABS_COUNT = ENTITY_ADMIN_TABS.length;

// ---------------------------------------------------------------------------
// Sub-components — Display-only field renderers
// ---------------------------------------------------------------------------

/** Props for the read-only text field display */
interface FieldDisplayProps {
  /** Label text displayed above the value */
  label: string;
  /** Value text displayed below the label */
  value: string;
  /** Whether to render in monospace font (e.g. for GUIDs) */
  mono?: boolean;
}

/**
 * Read-only text field display.
 * Replaces `<wv-field-text>` and `<wv-field-guid>` in Display mode.
 */
function FieldDisplay({ label, value, mono = false }: FieldDisplayProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        {label}
      </div>
      <div
        className={`mt-1 text-sm text-gray-900 ${mono ? 'font-mono' : ''}`}
      >
        {value || '\u2014'}
      </div>
    </div>
  );
}

/** Props for the color field display */
interface ColorFieldDisplayProps {
  /** Label text */
  label: string;
  /** CSS colour value (hex string) */
  value: string;
}

/**
 * Read-only color field display with swatch.
 * Replaces `<wv-field-color>` in Display mode.
 */
function ColorFieldDisplay({ label, value }: ColorFieldDisplayProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        {label}
      </div>
      <div className="mt-1 flex items-center gap-2">
        <span
          className="inline-block h-5 w-5 rounded border border-gray-300 flex-shrink-0"
          style={{ backgroundColor: value || 'transparent' }}
          aria-hidden="true"
        />
        <span className="text-sm text-gray-900 font-mono">
          {value || '\u2014'}
        </span>
      </div>
    </div>
  );
}

/** Props for the system checkbox display */
interface SystemFieldDisplayProps {
  /** Label text */
  label: string;
  /** Whether the entity is a system entity */
  value: boolean;
}

/**
 * Read-only boolean/checkbox display.
 * Replaces `<wv-field-checkbox>` in Display mode with
 * text-true="system entity" / text-false="not a system entity".
 */
function SystemFieldDisplay({ label, value }: SystemFieldDisplayProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide">
        {label}
      </div>
      <div className="mt-1 text-sm text-gray-900">
        {value ? 'system entity' : 'not a system entity'}
      </div>
    </div>
  );
}

/** Props for a single read-only permission checkbox */
interface PermissionCheckboxProps {
  /** Whether the permission is granted */
  checked: boolean;
  /** Accessible label for the checkbox (e.g. "Administrator can create") */
  label: string;
}

/**
 * Single read-only permission checkbox.
 * Replaces checkbox cells within the `<wv-field-checkbox-grid>` in Display mode
 * (text-true="granted", text-false="not granted").
 */
function PermissionCheckbox({ checked, label }: PermissionCheckboxProps): React.JSX.Element {
  return (
    <input
      type="checkbox"
      checked={checked}
      disabled
      aria-label={label}
      className="h-4 w-4 rounded border-gray-300 text-blue-600 disabled:opacity-60"
      readOnly
    />
  );
}

/** Props for the permission grid */
interface PermissionGridComponentProps {
  /** Section label */
  label: string;
  /** Grid rows — one per role */
  rows: PermissionGridRow[];
}

/**
 * Read-only CRUD permission grid.
 *
 * Replaces `<wv-field-checkbox-grid>` in Display mode:
 *   - Columns: Create, Read, Update, Delete  (from PermissionOptions)
 *   - Rows: one per role                      (from RoleOptions)
 *   - Cells: granted / not granted checkboxes  (from RecordPermissions)
 */
function PermissionGridDisplay({ label, rows }: PermissionGridComponentProps): React.JSX.Element {
  return (
    <div>
      <div className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">
        {label}
      </div>
      {rows.length === 0 ? (
        <p className="text-sm text-gray-400 italic">No roles available.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border border-gray-200">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th
                  scope="col"
                  className="px-4 py-2 text-start text-xs font-medium text-gray-500 uppercase"
                >
                  Role
                </th>
                <th
                  scope="col"
                  className="px-4 py-2 text-center text-xs font-medium text-gray-500 uppercase"
                >
                  Create
                </th>
                <th
                  scope="col"
                  className="px-4 py-2 text-center text-xs font-medium text-gray-500 uppercase"
                >
                  Read
                </th>
                <th
                  scope="col"
                  className="px-4 py-2 text-center text-xs font-medium text-gray-500 uppercase"
                >
                  Update
                </th>
                <th
                  scope="col"
                  className="px-4 py-2 text-center text-xs font-medium text-gray-500 uppercase"
                >
                  Delete
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white">
              {rows.map((row) => (
                <tr key={row.roleId}>
                  <td className="whitespace-nowrap px-4 py-2 text-sm font-medium text-gray-900">
                    {row.roleName}
                  </td>
                  <td className="px-4 py-2 text-center">
                    <PermissionCheckbox
                      checked={row.canCreate}
                      label={`${row.roleName} create: ${row.canCreate ? 'granted' : 'not granted'}`}
                    />
                  </td>
                  <td className="px-4 py-2 text-center">
                    <PermissionCheckbox
                      checked={row.canRead}
                      label={`${row.roleName} read: ${row.canRead ? 'granted' : 'not granted'}`}
                    />
                  </td>
                  <td className="px-4 py-2 text-center">
                    <PermissionCheckbox
                      checked={row.canUpdate}
                      label={`${row.roleName} update: ${row.canUpdate ? 'granted' : 'not granted'}`}
                    />
                  </td>
                  <td className="px-4 py-2 text-center">
                    <PermissionCheckbox
                      checked={row.canDelete}
                      label={`${row.roleName} delete: ${row.canDelete ? 'granted' : 'not granted'}`}
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * Entity details page showing read-only metadata and permissions.
 *
 * Fetches the entity by ID from URL params, displays all metadata fields
 * in display mode, renders a CRUD permission grid mapping roles to
 * canCreate / canRead / canUpdate / canDelete, and provides Clone / Manage /
 * Delete action buttons (Delete is disabled for system entities).
 *
 * Navigation sub-tabs link to sibling entity admin sections:
 *   /admin/entities/:entityId           — this page (Details)
 *   /admin/entities/:entityId/fields    — field management
 *   /admin/entities/:entityId/relations — relation management
 *   /admin/entities/:entityId/data      — record data browser
 *   /admin/entities/:entityId/pages     — page configuration
 *   /admin/entities/:entityId/web-api   — API reference
 */
export default function AdminEntityDetails(): React.JSX.Element {
  // ── URL params & navigation ───────────────────────────────────────────
  const { entityId = '' } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  // ── Local state ───────────────────────────────────────────────────────
  const [showDeleteModal, setShowDeleteModal] = useState(false);
  const [showDeleteSuccess, setShowDeleteSuccess] = useState(false);

  // ── Data queries ──────────────────────────────────────────────────────
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityErrorObj,
  } = useEntity(entityId);

  const { data: rolesData, isLoading: rolesLoading } = useRoles();

  const deleteEntityMutation = useDeleteEntity();

  // ── Derived data ──────────────────────────────────────────────────────

  /**
   * Extract roles array from the API response envelope.
   * `useRoles()` returns `ApiResponse<ErpRole[]>` — the typed array
   * lives inside `data.object`.
   */
  const roles: ErpRole[] = useMemo(
    () => rolesData?.object ?? [],
    [rolesData],
  );

  /**
   * Resolve the record screen ID field name from the entity's fields
   * array. Mirrors `details.cshtml.cs` PageInit() field lookup logic
   * (lines 47-53): look up the Field whose id matches
   * `Entity.RecordScreenIdField` and return its name; default to "id".
   */
  const recordScreenIdFieldName: string = useMemo((): string => {
    if (!entity?.recordScreenIdField) return 'id';
    const screenField = entity.fields?.find(
      (f) => f.id === entity.recordScreenIdField,
    );
    return screenField?.name ?? 'id';
  }, [entity]);

  /**
   * Build the permission grid by mapping each role against the entity's
   * RecordPermissions arrays.
   *
   * Mirrors `details.cshtml.cs` lines 58-94 where `valueGrid` is
   * constructed by iterating roles and checking
   * `CanCreate` / `CanRead` / `CanUpdate` / `CanDelete`.
   */
  const permissionGrid: PermissionGridRow[] = useMemo((): PermissionGridRow[] => {
    if (!entity?.recordPermissions || roles.length === 0) return [];

    const permissions: RecordPermissions = entity.recordPermissions;

    return roles.map((role: ErpRole): PermissionGridRow => ({
      roleId: role.id,
      roleName: role.name,
      canCreate: permissions.canCreate.includes(role.id),
      canRead: permissions.canRead.includes(role.id),
      canUpdate: permissions.canUpdate.includes(role.id),
      canDelete: permissions.canDelete.includes(role.id),
    }));
  }, [entity?.recordPermissions, roles]);

  // ── Callbacks ─────────────────────────────────────────────────────────

  /** Open the delete confirmation modal */
  const handleOpenDeleteModal = useCallback((): void => {
    setShowDeleteModal(true);
  }, []);

  /** Close the delete confirmation modal */
  const handleCloseDeleteModal = useCallback((): void => {
    setShowDeleteModal(false);
  }, []);

  /**
   * Confirm entity deletion.
   *
   * Invokes the delete mutation and navigates to `/admin/entities` on
   * success. Mirrors `details.cshtml.cs` OnPost() which calls
   * `EntityManager.DeleteEntity(ErpEntity.Id)` and redirects to
   * `/sdk/objects/entity/l/list`.
   */
  const handleConfirmDelete = useCallback((): void => {
    if (!entity) return;

    deleteEntityMutation.mutate(entity.id, {
      onSuccess: () => {
        setShowDeleteModal(false);
        setShowDeleteSuccess(true);
        // Delay navigation to let the success notification render for E2E visibility.
        // 2 seconds gives Playwright ample time to detect the notification.
        setTimeout(() => {
          navigate('/admin/entities');
        }, 2000);
      },
      onError: () => {
        /* Keep modal open so user sees the error rendered below the body text */
      },
    });
  }, [entity, deleteEntityMutation, navigate]);

  /**
   * Handle sub-navigation tab changes by navigating to the corresponding
   * entity admin section route.
   *
   * Mirrors `AdminPageUtils.GetEntityAdminSubNav()` from the monolith
   * which generated link elements for the header toolbar.
   */
  const handleTabChange = useCallback(
    (tabId: string): void => {
      if (tabId === 'details') return; // Already on the details page
      navigate(`/admin/entities/${entityId}/${tabId}`);
    },
    [entityId, navigate],
  );

  // ── Loading state ─────────────────────────────────────────────────────

    // ── Delete-success state (MUST be checked BEFORE loading / error states) ──
  // After a successful delete the useEntity query re-fires and returns a 404 /
  // error — if we checked entityError first, we'd show "Entity not found"
  // instead of the success notification.
  if (showDeleteSuccess) {
    return (
      <div className="p-6">
        <div
          className="m-4 rounded-md bg-green-50 p-4"
          role="status"
          aria-live="polite"
        >
          <p
            className="text-sm font-medium text-green-800"
            data-testid="success-notification"
          >
            Entity deleted successfully. Redirecting…
          </p>
        </div>
      </div>
    );
  }

  if (entityLoading || rolesLoading) {
    return (
      <div
        className="flex items-center justify-center p-12"
        role="status"
        aria-label="Loading entity details"
      >
        <div className="text-gray-500 text-sm">Loading entity details\u2026</div>
      </div>
    );
  }

  // ── Error / not-found state ───────────────────────────────────────────

  if (entityError || !entity) {
    return (
      <div className="p-6">
        <div
          className="rounded-md bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          {entityErrorObj?.message ?? 'Entity not found.'}
        </div>
      </div>
    );
  }

  // ── Render ────────────────────────────────────────────────────────────

  const isSystemEntity: boolean = entity.system;

  return (
    <div className="min-h-0 flex-1">
      {/* ────────── Page Header ────────── */}
      <header className="border-b border-gray-200 bg-white px-6 pb-0 pt-5">
        {/* Breadcrumb / back link */}
        <div className="mb-3">
          <Link
            to="/admin/entities"
            className="text-xs text-gray-500 hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            &larr; Entities
          </Link>
        </div>

        {/* Title row: icon + entity name + subtitle + action buttons */}
        <div className="flex flex-wrap items-start justify-between gap-4 mb-4">
          {/* Left: entity icon, name, subtitle */}
          <div className="flex items-center gap-3">
            {/* Entity icon badge */}
            <span
              className="flex h-10 w-10 items-center justify-center rounded-lg text-lg flex-shrink-0"
              style={{
                backgroundColor: entity.color || '#6b7280',
                color: '#fff',
              }}
              aria-hidden="true"
            >
              {entity.iconName ? (
                <i className={entity.iconName} />
              ) : (
                <span className="text-sm font-bold">
                  {entity.name.charAt(0).toUpperCase()}
                </span>
              )}
            </span>

            <div>
              <h1 className="text-xl font-semibold text-gray-900">
                {entity.name}
              </h1>
              <p className="text-sm text-gray-500">Details</p>
            </div>
          </div>

          {/* Right: action buttons */}
          <nav aria-label="Entity actions" className="flex items-center gap-2">
            {/* Clone action — always available */}
            <Link
              to={`/admin/entities/${entityId}/clone`}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <svg
                className="h-4 w-4 text-gray-400"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M15.75 17.25v3.375c0 .621-.504 1.125-1.125 1.125h-9.75a1.125 1.125 0 01-1.125-1.125V7.875c0-.621.504-1.125 1.125-1.125H6.75a9.06 9.06 0 011.5.124m7.5 10.376h3.375c.621 0 1.125-.504 1.125-1.125V11.25c0-4.46-3.243-8.161-7.5-8.876a9.06 9.06 0 00-1.5-.124H9.375c-.621 0-1.125.504-1.125 1.125v3.5m7.5 10.375H9.375a1.125 1.125 0 01-1.125-1.125v-9.25m12 6.625v-1.875a3.375 3.375 0 00-3.375-3.375h-1.5a1.125 1.125 0 01-1.125-1.125v-1.5a3.375 3.375 0 00-3.375-3.375H9.75"
                />
              </svg>
              Clone
            </Link>

            {/* Delete action — disabled for system entities */}
            {isSystemEntity ? (
              <button
                type="button"
                disabled
                title="System objects cannot be deleted"
                aria-disabled="true"
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-200 bg-gray-50 px-3 py-1.5 text-sm font-medium text-gray-400 cursor-not-allowed"
              >
                <svg
                  className="h-4 w-4"
                  fill="none"
                  viewBox="0 0 24 24"
                  strokeWidth={1.5}
                  stroke="currentColor"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M16.5 10.5V6.75a4.5 4.5 0 10-9 0v3.75m-.75 11.25h10.5a2.25 2.25 0 002.25-2.25v-6.75a2.25 2.25 0 00-2.25-2.25H6.75a2.25 2.25 0 00-2.25 2.25v6.75a2.25 2.25 0 002.25 2.25z"
                  />
                </svg>
                Delete locked
              </button>
            ) : (
              <button
                type="button"
                onClick={handleOpenDeleteModal}
                data-testid="delete-entity-btn"
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-red-600 shadow-sm hover:bg-red-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              >
                <svg
                  className="h-4 w-4"
                  fill="none"
                  viewBox="0 0 24 24"
                  strokeWidth={1.5}
                  stroke="currentColor"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0"
                  />
                </svg>
                Delete Entity
              </button>
            )}

            {/* Manage action — always available */}
            <Link
              to={`/admin/entities/${entityId}/manage?returnUrl=${encodeURIComponent(`/admin/entities/${entityId}`)}`}
              data-testid="edit-entity-btn"
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <svg
                className="h-4 w-4 text-orange-500"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.324.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 011.37.49l1.296 2.247a1.125 1.125 0 01-.26 1.431l-1.003.827c-.293.24-.438.613-.431.992a6.759 6.759 0 010 .255c-.007.378.138.75.43.99l1.005.828c.424.35.534.954.26 1.43l-1.298 2.247a1.125 1.125 0 01-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.57 6.57 0 01-.22.128c-.331.183-.581.495-.644.869l-.213 1.28c-.09.543-.56.941-1.11.941h-2.594c-.55 0-1.02-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 01-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 01-1.369-.49l-1.297-2.247a1.125 1.125 0 01.26-1.431l1.004-.827c.292-.24.437-.613.43-.992a6.932 6.932 0 010-.255c.007-.378-.138-.75-.43-.99l-1.004-.828a1.125 1.125 0 01-.26-1.43l1.297-2.247a1.125 1.125 0 011.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.087.22-.128.332-.183.582-.495.644-.869l.214-1.281z"
                />
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"
                />
              </svg>
              Manage
            </Link>
          </nav>
        </div>

        {/* Sub-navigation tabs */}
        <TabNav
          tabs={ENTITY_ADMIN_TABS}
          activeTabId="details"
          onTabChange={handleTabChange}
          visibleTabs={VISIBLE_TABS_COUNT}
        />
      </header>

      {/* ────────── Entity Metadata (Display Mode) ────────── */}
      <main className="px-6 py-6">
        <section
          className="rounded-lg border border-gray-200 bg-white p-6"
          aria-label="Entity metadata"
        >
          <div className="grid grid-cols-12 gap-x-6 gap-y-5">
            {/* Row 1: Name | Id */}
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Name" value={entity.name} />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Id" value={entity.id} mono />
            </div>

            {/* Row 2: Label | Label Plural */}
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Label" value={entity.label} />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="Label Plural" value={entity.labelPlural} />
            </div>

            {/* Row 3: Color | IconName */}
            <div className="col-span-12 sm:col-span-6">
              <ColorFieldDisplay label="Color" value={entity.color} />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay label="IconName" value={entity.iconName} />
            </div>

            {/* Row 4: Record Screen Id Field | System */}
            <div className="col-span-12 sm:col-span-6">
              <FieldDisplay
                label="Record Screen Id Field"
                value={recordScreenIdFieldName}
              />
            </div>
            <div className="col-span-12 sm:col-span-6">
              <SystemFieldDisplay label="System" value={isSystemEntity} />
            </div>

            {/* Row 5: Record Permissions (full width) */}
            <div className="col-span-12">
              <PermissionGridDisplay
                label="Record Permissions"
                rows={permissionGrid}
              />
            </div>
          </div>
        </section>
      </main>

      {/* ────────── Delete Confirmation Modal ────────── */}
      {!isSystemEntity && (
        <Modal
          isVisible={showDeleteModal}
          title="Delete Entity"
          onClose={handleCloseDeleteModal}
          footer={
            <div className="flex justify-end gap-3">
              <button
                type="button"
                onClick={handleCloseDeleteModal}
                className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleConfirmDelete}
                disabled={deleteEntityMutation.isPending}
                data-testid="confirm-delete-btn"
                className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {deleteEntityMutation.isPending ? 'Deleting\u2026' : 'Delete'}
              </button>
            </div>
          }
        >
          <p className="text-sm text-gray-600">
            Are you sure you want to delete this entity? This action is
            permanent and will remove all associated fields, relations, and
            records.
          </p>
          {deleteEntityMutation.isError && (
            <div
              className="mt-3 rounded-md bg-red-50 p-3 text-sm text-red-700"
              role="alert"
            >
              {deleteEntityMutation.error?.message ?? 'Failed to delete entity.'}
            </div>
          )}
        </Modal>
      )}
    </div>
  );
}
