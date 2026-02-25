/**
 * RoleList — Role Listing Page
 *
 * Replaces the monolith's `WebVella.Erp.Plugins.SDK/Pages/role/list.cshtml[.cs]`.
 * Route: `/admin/roles`
 *
 * Source mapping:
 *  - `list.cshtml.cs` ListModel.OnGet()  → `useRoles()` TanStack Query hook
 *  - `list.cshtml` <wv-grid>             → `<DataTable>` component
 *  - EQL `SELECT * FROM role`            → GET `/v1/identity/roles`
 *  - `SecurityManager.GetAllRoles()`     → Identity microservice API
 *
 * Key behaviors preserved from monolith:
 *  - Grid displays action, name, description columns
 *  - Built-in roles (guest, regular, administrator) show a lock icon
 *    instead of the edit pencil link (list.cshtml line 20–27)
 *  - Custom roles show an edit link navigating to the manage page
 *  - Page header: red theme (#dc3545), key icon, "Roles" title
 *  - Empty state: "No roles found" alert (list.cshtml line 33–38)
 *  - "New role" header action button linking to create page
 *
 * AAP compliance:
 *  - §0.4.3  — React admin page for SDK role management
 *  - §0.5.1  — SecurityManager.GetAllRoles → Identity service API
 *  - §0.7.7  — PcGrid ViewComponent → DataTable React component
 *  - §0.8.1  — Full behavioral parity with source role listing
 *  - Pure static SPA, Tailwind CSS only, no server-side rendering
 *
 * @module pages/admin/RoleList
 */

import { useMemo } from 'react';
import { Link } from 'react-router-dom';

import { useRoles } from '../../hooks/useUsers';
import type { ErpRole } from '../../types/user';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';

/**
 * Type alias adding an index signature so that `RoleRecord` satisfies the
 * `Record<string, unknown>` constraint required by `DataTable<T>`.
 */
type RoleRecord = ErpRole & { [key: string]: unknown };

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Set of built-in role names that cannot be edited or deleted.
 *
 * Mirrors the monolith's hardcoded check in list.cshtml line 20:
 *   `@if ((string)record["name"] == "guest" ||
 *         (string)record["name"] == "regular" ||
 *         (string)record["name"] == "administrator")`
 */
const BUILT_IN_ROLE_NAMES = new Set<string>([
  'guest',
  'regular',
  'administrator',
]);

/**
 * Determines whether a role is a built-in system role that should be
 * protected from editing. Comparison is case-insensitive to guard
 * against casing variations in API responses.
 *
 * @param roleName - The name of the role to check
 * @returns `true` if the role is a built-in system role
 */
function isBuiltInRole(roleName: string): boolean {
  return BUILT_IN_ROLE_NAMES.has(roleName.toLowerCase());
}

// ---------------------------------------------------------------------------
// Lock Icon SVG — replaces `fa fa-lock fa-fw` from monolith
// ---------------------------------------------------------------------------

/**
 * Inline lock icon rendered for built-in roles where editing is disabled.
 * Replaces the Font Awesome `fa-lock` icon used in the source Razor template
 * (list.cshtml line 22). Uses `currentColor` fill so it inherits the text
 * color from its container.
 */
function LockIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 448 512"
      fill="currentColor"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <path d="M144 144v48H304V144c0-44.2-35.8-80-80-80s-80 35.8-80 80zM80 192V144C80 64.5 144.5 0 224 0s144 64.5 144 144v48h16c44.2 0 80 35.8 80 80v192c0 44.2-35.8 80-80 80H64c-44.2 0-80-35.8-80-80V272c0-44.2 35.8-80 80-80h16z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Pencil Icon SVG — replaces `fa fa-pencil-alt fa-fw` from monolith
// ---------------------------------------------------------------------------

/**
 * Inline pencil/edit icon rendered for custom (non-built-in) roles.
 * Replaces the Font Awesome `fa-pencil-alt` icon used in the source
 * Razor template (list.cshtml line 26).
 */
function PencilIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 512 512"
      fill="currentColor"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <path d="M362.7 19.3L314.3 67.7 444.3 197.7l48.4-48.4c25-25 25-65.5 0-90.5L453.3 19.3c-25-25-65.5-25-90.5 0zm-71 71L58.6 323.5c-10.4 10.4-18 23.3-22.2 37.4L1 481.2C-1.5 489.7 .8 498.8 7 505s15.3 8.5 23.7 6.1l120.3-35.4c14.1-4.2 27-11.8 37.4-22.2L421.7 220.3 291.7 90.3z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Plus Icon SVG — replaces `fa fa-plus fa-fw` from monolith
// ---------------------------------------------------------------------------

/**
 * Inline plus icon for the "New role" button.
 * Replaces the Font Awesome `fa-plus` icon from list.cshtml line 11.
 */
function PlusIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 448 512"
      fill="currentColor"
      className="inline-block h-4 w-4"
      aria-hidden="true"
    >
      <path d="M256 80c0-17.7-14.3-32-32-32s-32 14.3-32 32V224H48c-17.7 0-32 14.3-32 32s14.3 32 32 32H192V432c0 17.7 14.3 32 32 32s32-14.3 32-32V288H400c17.7 0 32-14.3 32-32s-14.3-32-32-32H256V80z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Key Icon SVG — replaces `fa fa-key` from monolith header
// ---------------------------------------------------------------------------

/**
 * Inline key icon for the page header.
 * Replaces the Font Awesome `fa-key` icon from list.cshtml line 9.
 */
function KeyIcon(): React.ReactElement {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 512 512"
      fill="currentColor"
      className="inline-block h-5 w-5"
      aria-hidden="true"
    >
      <path d="M336 352c97.2 0 176-78.8 176-176S433.2 0 336 0S160 78.8 160 176c0 18.7 2.9 36.8 8.3 53.7L7 391c-4.5 4.5-7 10.6-7 17v80c0 13.3 10.7 24 24 24h80c13.3 0 24-10.7 24-24V448h40c13.3 0 24-10.7 24-24V384h40c6.4 0 12.5-2.5 17-7l33.3-33.3c16.9 5.4 35 8.3 53.7 8.3zM376 176a40 40 0 1 0 0-80 40 40 0 1 0 0 80z" />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// RoleList Page Component
// ---------------------------------------------------------------------------

/**
 * Role listing page component.
 *
 * Fetches all roles from the Identity service and renders them in a
 * bordered, hoverable DataTable with action/name/description columns.
 *
 * Built-in roles (guest, regular, administrator) display a disabled
 * lock button in the action column. Custom roles display an edit
 * pencil link that navigates to `/admin/roles/{id}/manage`.
 *
 * The page header matches the monolith's red-themed (#dc3545) header
 * with a key icon and "New role" action button.
 *
 * @returns The rendered RoleList page
 */
function RoleList(): React.ReactElement {
  // Fetch all roles from the Identity service
  // Replaces: EQL `SELECT * FROM role` (list.cshtml.cs line 79)
  const { data, isLoading, isError } = useRoles();

  // Extract the role array from the API response envelope
  const roles: RoleRecord[] = (data?.object ?? []) as RoleRecord[];

  // Total count for DataTable pagination
  // Mirrors: Model.TotalCount = Records.TotalCount (list.cshtml.cs line 82)
  const totalCount = roles.length;

  /**
   * Memoized column definitions for the DataTable.
   *
   * Column configuration mirrors the monolith's WvGridColumnMeta list
   * from list.cshtml.cs lines 60–74:
   *   1. action — 1% width, no label, renders lock/edit button
   *   2. name  — 200px width, "name" label
   *   3. description — auto width, "description" label
   */
  const columns = useMemo<DataTableColumn<RoleRecord>[]>(
    () => [
      {
        id: 'action',
        label: '',
        width: '1%',
        cell: (_value: unknown, record: RoleRecord) => {
          if (isBuiltInRole(record.name)) {
            // Built-in role — show disabled lock button
            // Mirrors: list.cshtml lines 21–23
            return (
              <button
                type="button"
                disabled
                className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-gray-400 opacity-60"
                aria-label={`Role "${record.name}" is a built-in system role and cannot be edited`}
              >
                <LockIcon />
              </button>
            );
          }

          // Custom role — show edit link button
          // Mirrors: list.cshtml lines 25–27
          // Route: /sdk/access/role/m/{id} → /admin/roles/{id}/manage
          return (
            <Link
              to={`/admin/roles/${record.id}/manage`}
              className="inline-flex items-center justify-center rounded border border-gray-300 bg-white px-2 py-1 text-gray-600 transition-colors duration-200 hover:bg-gray-50 hover:text-gray-800"
              aria-label={`Edit role "${record.name}"`}
            >
              <PencilIcon />
            </Link>
          );
        },
      },
      {
        id: 'name',
        label: 'Name',
        accessorKey: 'name',
        width: '200px',
      },
      {
        id: 'description',
        label: 'Description',
        accessorKey: 'description',
      },
    ],
    [],
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      {/* ─── Page Header ────────────────────────────────────────────
       * Replaces <wv-page-header> from list.cshtml lines 8–12.
       * Red themed (#dc3545), key icon, "Roles" title, "New role" action.
       * ──────────────────────────────────────────────────────────── */}
      <header
        className="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-lg px-4 py-3 text-white shadow-sm"
        style={{ backgroundColor: '#dc3545' }}
      >
        <div className="flex items-center gap-2">
          <KeyIcon />
          <div>
            <p className="text-sm font-medium uppercase tracking-wide opacity-80">
              Roles
            </p>
            <h1 className="text-lg font-semibold leading-tight">All roles</h1>
          </div>
        </div>

        {/* "New role" action button
         * Replaces: <wv-button type="LinkAsButton" href="/sdk/access/role/c"
         *   color="White" size="Small" icon-class="fa fa-plus fa-fw go-green"
         *   text="New role"> from list.cshtml line 11 */}
        <Link
          to="/admin/roles/create"
          className="inline-flex items-center gap-1.5 rounded border border-white/30 bg-white px-3 py-1.5 text-sm font-medium text-gray-800 shadow-sm transition-colors duration-200 hover:bg-gray-100"
        >
          <span className="text-green-600">
            <PlusIcon />
          </span>
          New role
        </Link>
      </header>

      {/* ─── Error State ────────────────────────────────────────── */}
      {isError && (
        <div
          role="alert"
          className="mb-4 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800"
        >
          An error occurred while loading roles. Please try again.
        </div>
      )}

      {/* ─── Data Table ─────────────────────────────────────────────
       * Replaces <wv-grid> from list.cshtml lines 15–40.
       * bordered=true, hover=true match monolith flags.
       * emptyText="No roles found" matches the monolith's empty alert.
       * ──────────────────────────────────────────────────────────── */}
      <DataTable<RoleRecord>
        data={roles}
        columns={columns}
        bordered={true}
        hover={true}
        loading={isLoading}
        totalCount={totalCount}
        pageSize={15}
        emptyText="No roles found"
      />
    </div>
  );
}

export default RoleList;
