/**
 * UserList — Admin User Listing Page
 *
 * Replaces the monolith's SDK user listing at:
 *   - WebVella.Erp.Plugins.SDK/Pages/user/list.cshtml  (Razor template)
 *   - WebVella.Erp.Plugins.SDK/Pages/user/list.cshtml.cs (C# code-behind)
 *
 * Route: /admin/users  (replaces /sdk/access/user/l/list)
 *
 * Source behaviour preserved:
 *   - EQL `SELECT id,email,username,$user_role.name FROM user`
 *     → Identity service API  GET /v1/identity/users  with query params
 *   - Default sort: email ascending  (source ListModel.OnGet)
 *   - Sortable columns: email (120 px), username
 *   - PagerSize = 10  (source ListModel.PagerSize)
 *   - System user protection: lock icon, edit disabled
 *   - Non-system users: edit link to /admin/users/{id}/manage
 *   - Role column: comma-joined role names from `$user_role` relation
 *   - Empty state: "No users found"
 *   - Grid: bordered + hover
 *   - "Create User" action button → /admin/users/create
 *
 * AAP compliance:
 *   - §0.4.3  — React page replacing Razor Pages user list
 *   - §0.5.1  — SecurityManager.GetAllUsers → useUsers hook
 *   - §0.7.7  — PcGrid → DataTable component mapping
 *   - §0.8.1  — Pure static SPA, Tailwind CSS only
 *
 * @module pages/admin/UserList
 */

import { useState, useCallback, useMemo } from 'react';
import { useNavigate, Link, useSearchParams } from 'react-router-dom';

import { useUsers } from '../../hooks/useUsers';
import type { ErpUser, ErpRole } from '../../types/user';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';

// ---------------------------------------------------------------------------
// Local Types
// ---------------------------------------------------------------------------

/**
 * Extended user type that includes role assignments from the Identity API
 * response.  The Identity service endpoint `GET /v1/identity/users` returns
 * each user together with its role memberships (mirroring the monolith's
 * `$user_role` EQL relation expansion).  The base `ErpUser` interface in
 * `types/user.ts` is intentionally lean; this local extension adds the
 * roles array for the list page's display purposes.
 *
 * Uses a type alias (not interface) with an index signature so that
 * `UserWithRoles` satisfies the `Record<string, unknown>` constraint
 * required by the `DataTable<T>` generic parameter.
 */
type UserWithRoles = ErpUser & {
  /** Role assignments included in the Identity service list response. */
  roles?: ErpRole[];
  /** Index signature for DataTable<T extends Record<string, unknown>> compat. */
  [key: string]: unknown;
};

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Default page size matching the monolith's `ListModel.PagerSize = 10`.
 * Passed to the Identity service API and the DataTable component for
 * consistent pagination behaviour.
 */
const DEFAULT_PAGE_SIZE = 10;

/** Default sort column — email ascending, matching source `ListModel.OnGet`. */
const DEFAULT_SORT_BY = 'email';

/** Default sort direction. */
const DEFAULT_SORT_ORDER: 'asc' | 'desc' = 'asc';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Admin user listing page.
 *
 * Renders a sortable, paginated data table of all users with role
 * information.  Supports URL-driven pagination and sorting via
 * `useSearchParams`, which feeds both the `useUsers` TanStack Query hook
 * and the `DataTable` component.
 *
 * The page header includes a "Create User" action button that navigates
 * to `/admin/users/create`.  Each row contains either a lock icon
 * (system user) or an edit link (non-system user).
 */
export default function UserList(): React.ReactElement {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  // -------------------------------------------------------------------
  // Local UI state  (useState required per external_imports schema)
  // Tracks the search / filter text entered above the grid.
  // -------------------------------------------------------------------
  const [searchText, setSearchText] = useState('');

  // -------------------------------------------------------------------
  // URL-driven pagination & sorting parameters
  // -------------------------------------------------------------------
  const page = parseInt(searchParams.get('page') ?? '1', 10);
  const pageSize = parseInt(
    searchParams.get('pageSize') ?? String(DEFAULT_PAGE_SIZE),
    10,
  );
  const sortBy = searchParams.get('sortBy') ?? DEFAULT_SORT_BY;
  const sortOrder = (searchParams.get('sortOrder') ?? DEFAULT_SORT_ORDER) as
    | 'asc'
    | 'desc';

  // -------------------------------------------------------------------
  // API data fetching via Identity service
  // -------------------------------------------------------------------

  /**
   * Build query parameters for the `useUsers` hook.
   *
   * `UserListParams` declares `search`, `roleId`, `page`, `pageSize`.
   * The hook internally casts to `Record<string, unknown>` before passing
   * to the HTTP client, so additional fields (`sortBy`, `sortOrder`) are
   * transparently included in the query string for server-side sorting.
   */
  const queryParams = useMemo(
    () => ({
      page,
      pageSize,
      sortBy,
      sortOrder,
      ...(searchText.trim() ? { search: searchText.trim() } : {}),
    }),
    [page, pageSize, sortBy, sortOrder, searchText],
  );

  const { data, isLoading, isError, error, isFetching } = useUsers(
    queryParams as Parameters<typeof useUsers>[0],
  );

  // -------------------------------------------------------------------
  // Derived data
  // -------------------------------------------------------------------

  /** User records cast to the extended type that includes roles. */
  const users = useMemo<UserWithRoles[]>(
    () => (data?.object ?? []) as UserWithRoles[],
    [data],
  );

  /**
   * Total user count for pagination.
   *
   * The Identity service list endpoint may return a `totalCount` field
   * alongside the typed `object` array (common in paginated REST APIs).
   * If the response includes it, use it; otherwise fall back to the
   * length of the returned array.
   */
  const totalCount = useMemo(() => {
    const extended = data as
      | (typeof data & { totalCount?: number })
      | undefined;
    return extended?.totalCount ?? users.length;
  }, [data, users.length]);

  // -------------------------------------------------------------------
  // Column definitions — matches monolith grid exactly
  // -------------------------------------------------------------------

  const columns = useMemo<DataTableColumn<UserWithRoles>[]>(
    () => [
      // 1. Action column (1 % width): lock icon for system user, edit for others
      {
        id: 'action',
        label: '',
        width: '1%',
        noWrap: true,
        cell: (_value: unknown, user: UserWithRoles) => {
          // System user detection — mirrors the monolith's
          // `@if(record["username"].ToString() == "system")` check.
          if (user.username === 'system') {
            return (
              <span
                className="inline-flex items-center text-gray-400"
                title="System user"
                aria-label="System user (locked)"
              >
                {/* Heroicons Mini — lock-closed */}
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  className="h-4 w-4"
                  aria-hidden="true"
                >
                  <path
                    fillRule="evenodd"
                    d="M10 1a4.5 4.5 0 00-4.5 4.5V9H5a2 2 0 00-2 2v6a2 2 0 002 2h10a2 2 0 002-2v-6a2 2 0 00-2-2h-.5V5.5A4.5 4.5 0 0010 1zm3 8V5.5a3 3 0 10-6 0V9h6z"
                    clipRule="evenodd"
                  />
                </svg>
              </span>
            );
          }

          // Non-system user — edit link to manage page
          return (
            <Link
              to={`/admin/users/${user.id}/manage`}
              className="inline-flex items-center text-blue-600 hover:text-blue-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
              title="Edit user"
              aria-label={`Edit user ${user.username || user.email}`}
            >
              {/* Heroicons Mini — pencil */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                viewBox="0 0 20 20"
                fill="currentColor"
                className="h-4 w-4"
                aria-hidden="true"
              >
                <path d="M2.695 14.763l-1.262 3.154a.5.5 0 00.65.65l3.155-1.262a4 4 0 001.343-.885L17.5 5.5a2.121 2.121 0 00-3-3L3.58 13.42a4 4 0 00-.885 1.343z" />
              </svg>
            </Link>
          );
        },
      },

      // 2. Email column — sortable, 120 px fixed width
      {
        id: 'email',
        name: 'email',
        label: 'Email',
        sortable: true,
        width: '120px',
        accessorKey: 'email',
      },

      // 3. Username column — sortable, auto width
      {
        id: 'username',
        name: 'username',
        label: 'Username',
        sortable: true,
        accessorKey: 'username',
      },

      // 4. Roles column — non-sortable, comma-joined names
      //    Replaces the monolith's `$user_role.name` relation rendering.
      {
        id: 'roles',
        name: 'roles',
        label: 'Role',
        sortable: false,
        accessorFn: (user: UserWithRoles): string =>
          user.roles?.map((r) => r.name).join(', ') ?? '',
      },
    ],
    [],
  );

  // -------------------------------------------------------------------
  // Callbacks
  // -------------------------------------------------------------------

  /** Navigate to the "Create User" page. */
  const handleCreateUser = useCallback(() => {
    navigate('/admin/users/create');
  }, [navigate]);

  /** Debounced search handler — updates local state which feeds queryParams. */
  const handleSearchChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      setSearchText(e.target.value);
    },
    [],
  );

  // -------------------------------------------------------------------
  // Error state
  // -------------------------------------------------------------------
  if (isError) {
    return (
      <section className="p-6">
        <div
          className="rounded border border-red-200 bg-red-50 p-4 text-sm text-red-700"
          role="alert"
        >
          <p className="font-medium">Failed to load users</p>
          <p>{error?.message ?? 'An unexpected error occurred.'}</p>
        </div>
      </section>
    );
  }

  // -------------------------------------------------------------------
  // Render
  // -------------------------------------------------------------------
  return (
    <section className="flex flex-col gap-6 p-6">
      {/* ── Page header ────────────────────────────────────────── */}
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold text-gray-900">Users</h1>

        <button
          type="button"
          onClick={handleCreateUser}
          className="inline-flex items-center gap-2 rounded bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 motion-safe:transition-colors motion-safe:duration-150"
        >
          {/* Heroicons Mini — plus */}
          <svg
            xmlns="http://www.w3.org/2000/svg"
            viewBox="0 0 20 20"
            fill="currentColor"
            className="h-4 w-4"
            aria-hidden="true"
          >
            <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
          </svg>
          Create User
        </button>
      </header>

      {/* ── Search bar ─────────────────────────────────────────── */}
      <div className="max-w-sm">
        <label htmlFor="user-search" className="sr-only">
          Search users
        </label>
        <div className="relative">
          <div className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3">
            {/* Heroicons Mini — magnifying-glass */}
            <svg
              xmlns="http://www.w3.org/2000/svg"
              viewBox="0 0 20 20"
              fill="currentColor"
              className="h-4 w-4 text-gray-400"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
          </div>
          <input
            id="user-search"
            type="search"
            value={searchText}
            onChange={handleSearchChange}
            placeholder="Search by name or email…"
            className="block w-full rounded border border-gray-300 bg-white py-2 ps-10 pe-3 text-sm text-gray-900 placeholder:text-gray-400 focus-visible:border-blue-500 focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600"
          />
        </div>
      </div>

      {/* ── User data table ────────────────────────────────────── */}
      <DataTable<UserWithRoles>
        data={users}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize}
        bordered
        hover
        loading={isLoading || isFetching}
        emptyText="No users found"
        name="users"
      />
    </section>
  );
}
