import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useUser, useDeleteUser, useRoles } from '../../hooks/useUsers';
import type { ErpUser, ErpRole } from '../../types/user';
import Modal, { ModalSize } from '../../components/common/Modal';

/**
 * System user email address — this account cannot be deleted.
 * Matches the monolith's seed user `erp@webvella.com` from SecurityManager.
 */
const SYSTEM_USER_EMAIL = 'erp@webvella.com';

/**
 * Formats an ISO date string into a human-readable locale string.
 * Returns an em-dash when the value is null or undefined.
 */
function formatDate(dateStr?: string | null): string {
  if (!dateStr) return '\u2014';
  try {
    return new Date(dateStr).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'long',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return dateStr;
  }
}

/**
 * Extracts role IDs from the user API response.
 *
 * The Identity service API may include role associations that are not part of
 * the typed `ErpUser` interface (the C# model marks `Roles` as `[JsonIgnore]`
 * for the generic DTO, but the detail endpoint may include them). This helper
 * defensively probes common shapes returned by the API.
 */
function extractUserRoleIds(user: ErpUser): string[] {
  const raw = user as unknown as Record<string, unknown>;

  // Shape 1: flat string array — e.g. { roles: ["id-1", "id-2"] }
  if (Array.isArray(raw.roles)) {
    return (raw.roles as unknown[]).map((r) =>
      typeof r === 'string' ? r : (r as Record<string, string>)?.id ?? '',
    ).filter(Boolean);
  }

  // Shape 2: relation-style — e.g. { $user_role: [{ id: "id-1" }] }
  if (Array.isArray(raw.$user_role)) {
    return (raw.$user_role as unknown[]).map((r) =>
      typeof r === 'string' ? r : (r as Record<string, string>)?.id ?? '',
    ).filter(Boolean);
  }

  // Shape 3: roleIds shorthand — e.g. { roleIds: ["id-1"] }
  if (Array.isArray(raw.roleIds)) {
    return raw.roleIds as string[];
  }

  return [];
}

/**
 * UserDetails — Read-only user details page.
 *
 * Route: `/admin/users/:userId`
 *
 * Fetches user data by ID from the Identity service, displays all available
 * user properties in a read-only layout, renders role badges by cross-
 * referencing the user's role associations with the full role list, and
 * provides action buttons for editing (Manage link) and deletion (with a
 * confirmation modal that blocks deletion of the system user).
 */
function UserDetails(): React.JSX.Element {
  const { userId } = useParams<{ userId: string }>();
  const navigate = useNavigate();

  /* ------------------------------------------------------------------ */
  /*  Data fetching                                                      */
  /* ------------------------------------------------------------------ */
  const {
    data: userData,
    isLoading: isUserLoading,
    isError: isUserError,
  } = useUser(userId);

  const { data: rolesData } = useRoles();
  const deleteUserMutation = useDeleteUser();

  /* ------------------------------------------------------------------ */
  /*  Local state                                                        */
  /* ------------------------------------------------------------------ */
  const [isDeleteModalVisible, setIsDeleteModalVisible] = useState(false);

  /* ------------------------------------------------------------------ */
  /*  Derived values                                                     */
  /* ------------------------------------------------------------------ */
  const user: ErpUser | undefined = userData?.object;
  const allRoles: ErpRole[] = rolesData?.object ?? [];
  const isSystemUser = user?.email === SYSTEM_USER_EMAIL;

  const userRoleIds = user ? extractUserRoleIds(user) : [];
  const userRoles: ErpRole[] =
    userRoleIds.length > 0
      ? allRoles.filter((role) => userRoleIds.includes(role.id))
      : isSystemUser
        ? allRoles.filter((r) => r.name.toLowerCase() === 'administrator')
        : [];

  /* ------------------------------------------------------------------ */
  /*  Event handlers                                                     */
  /* ------------------------------------------------------------------ */
  const handleDeleteClick = useCallback(() => {
    setIsDeleteModalVisible(true);
  }, []);

  const handleCloseModal = useCallback(() => {
    setIsDeleteModalVisible(false);
  }, []);

  const handleConfirmDelete = useCallback(() => {
    if (!userId) return;
    deleteUserMutation.mutate(userId, {
      onSuccess: () => {
        setIsDeleteModalVisible(false);
        navigate('/admin/users');
      },
    });
  }, [userId, deleteUserMutation, navigate]);

  /* ------------------------------------------------------------------ */
  /*  Loading state                                                      */
  /* ------------------------------------------------------------------ */
  if (isUserLoading) {
    return (
      <div
        className="flex items-center justify-center"
        style={{ minBlockSize: '24rem' }}
        role="status"
        aria-label="Loading user details"
      >
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-red-600" />
      </div>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Error / not-found state                                            */
  /* ------------------------------------------------------------------ */
  if (isUserError || !user) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center">
        <h2 className="text-lg font-semibold text-red-800">User not found</h2>
        <p className="mt-2 text-sm text-red-600">
          The requested user could not be loaded. It may have been deleted or
          the ID is invalid.
        </p>
        <Link
          to="/admin/users"
          className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
        >
          Back to Users
        </Link>
      </div>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Main render                                                        */
  /* ------------------------------------------------------------------ */
  const displayName =
    `${user.firstName ?? ''} ${user.lastName ?? ''}`.trim() || user.username;

  return (
    <div className="space-y-6">
      {/* ---- Page header ---- */}
      <div className="flex flex-wrap items-center justify-between gap-4 border-b border-gray-200 pb-4">
        {/* Breadcrumb + title */}
        <div className="flex items-center gap-3">
          <Link
            to="/admin/users"
            className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            aria-label="Back to user list"
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M15 19l-7-7 7-7"
              />
            </svg>
            Users
          </Link>

          <span className="text-gray-300" aria-hidden="true">
            /
          </span>

          <h1 className="text-xl font-semibold text-gray-900">User Details</h1>
        </div>

        {/* Action buttons */}
        <div className="flex items-center gap-2">
          <Link
            to={`/admin/users/${user.id}/manage`}
            data-testid="edit-user-btn"
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z"
              />
            </svg>
            Manage
          </Link>

          <button
            type="button"
            onClick={handleDeleteClick}
            disabled={isSystemUser}
            className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            aria-label={
              isSystemUser
                ? 'System user cannot be deleted'
                : 'Delete this user'
            }
            title={
              isSystemUser ? 'System user cannot be deleted' : 'Delete user'
            }
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
              />
            </svg>
            Delete
          </button>
        </div>
      </div>

      {/* ---- Detail card ---- */}
      <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="grid grid-cols-1 gap-6 p-6 md:grid-cols-12">
          {/* Left column — avatar & primary info */}
          <div className="flex flex-col items-center gap-4 md:col-span-4">
            {/* Avatar */}
            <div
              className="flex h-20 w-20 shrink-0 items-center justify-center overflow-hidden rounded-full bg-gray-100 text-gray-400"
              aria-label="User avatar"
            >
              {user.image ? (
                <img
                  src={user.image}
                  alt={displayName}
                  width={80}
                  height={80}
                  className="h-20 w-20 rounded-full object-cover"
                  loading="lazy"
                  decoding="async"
                />
              ) : (
                <svg
                  className="h-10 w-10"
                  fill="currentColor"
                  viewBox="0 0 24 24"
                  aria-hidden="true"
                >
                  <path d="M12 12c2.7 0 4.8-2.1 4.8-4.8S14.7 2.4 12 2.4 7.2 4.5 7.2 7.2 9.3 12 12 12zm0 2.4c-3.2 0-9.6 1.6-9.6 4.8v2.4h19.2v-2.4c0-3.2-6.4-4.8-9.6-4.8z" />
                </svg>
              )}
            </div>

            {/* Display name & email */}
            <div className="text-center">
              <p className="text-lg font-semibold text-gray-900">
                {displayName}
              </p>
              <p className="text-sm text-gray-500">{user.email}</p>
            </div>

            {/* Status badges */}
            <div className="flex flex-wrap justify-center gap-2">
              {user.isAdmin && (
                <span className="inline-flex items-center rounded-full bg-purple-100 px-2.5 py-0.5 text-xs font-medium text-purple-800">
                  Administrator
                </span>
              )}
              {isSystemUser && (
                <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-800">
                  System User
                </span>
              )}
            </div>
          </div>

          {/* Right column — field details */}
          <div className="md:col-span-8">
            <dl className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
              <div>
                <dt className="text-sm font-medium text-gray-500">Email</dt>
                <dd className="mt-1 text-sm text-gray-900">{user.email}</dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">Username</dt>
                <dd className="mt-1 text-sm text-gray-900">{user.username}</dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">
                  First Name
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {user.firstName || '\u2014'}
                </dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">
                  Last Name
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {user.lastName || '\u2014'}
                </dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">Admin</dt>
                <dd className="mt-1">
                  <span
                    className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${
                      user.isAdmin
                        ? 'bg-green-100 text-green-800'
                        : 'bg-gray-100 text-gray-800'
                    }`}
                  >
                    {user.isAdmin ? 'Yes' : 'No'}
                  </span>
                </dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">ID</dt>
                <dd className="mt-1 break-all font-mono text-sm text-gray-600">
                  {user.id}
                </dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">
                  Created On
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {formatDate(user.createdOn)}
                </dd>
              </div>

              <div>
                <dt className="text-sm font-medium text-gray-500">
                  Last Logged In
                </dt>
                <dd className="mt-1 text-sm text-gray-900">
                  {formatDate(user.lastLoggedIn)}
                </dd>
              </div>
            </dl>
          </div>
        </div>

        {/* Roles section */}
        <div className="border-t border-gray-200 px-6 py-4">
          <h3 className="mb-2 text-sm font-medium text-gray-500">Roles</h3>
          <div className="flex flex-wrap gap-2">
            {userRoles.length > 0 ? (
              userRoles.map((role) => (
                <span
                  key={role.id}
                  className="inline-flex items-center rounded-full bg-blue-100 px-3 py-0.5 text-xs font-medium text-blue-800"
                >
                  {role.name}
                </span>
              ))
            ) : (
              <span className="text-sm italic text-gray-400">
                No roles assigned
              </span>
            )}
          </div>
        </div>
      </div>

      {/* ---- Delete confirmation modal ---- */}
      <Modal
        isVisible={isDeleteModalVisible}
        title="Confirm Delete"
        size={ModalSize.Small}
        onClose={handleCloseModal}
        footer={
          <>
            <button
              type="button"
              onClick={handleCloseModal}
              className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={deleteUserMutation.isPending}
              className="rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50"
            >
              {deleteUserMutation.isPending ? 'Deleting\u2026' : 'Delete User'}
            </button>
          </>
        }
      >
        <div className="space-y-2">
          <p className="text-sm text-gray-600">
            Are you sure you want to delete user{' '}
            <strong className="text-gray-900">{user.username}</strong>?
          </p>
          <p className="text-sm text-red-600">This action cannot be undone.</p>
          {deleteUserMutation.isError && (
            <p className="text-sm text-red-600" role="alert">
              Failed to delete user. Please try again.
            </p>
          )}
        </div>
      </Modal>
    </div>
  );
}

export default UserDetails;
