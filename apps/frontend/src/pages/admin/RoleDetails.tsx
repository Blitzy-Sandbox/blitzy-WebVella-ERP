import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useRole, useDeleteRole } from '../../hooks/useUsers';
import type { ErpRole } from '../../types/user';
import Modal, { ModalSize } from '../../components/common/Modal';

/**
 * Well-known system role names that cannot be deleted.
 *
 * These mirror the monolith's `SystemIds` definitions in
 * `WebVella.Erp/Api/Definitions.cs`:
 * - AdministratorRoleId → "administrator"
 * - RegularRoleId       → "regular"
 * - GuestRoleId         → "guest"
 */
const SYSTEM_ROLE_NAMES: ReadonlyArray<string> = [
  'administrator',
  'regular',
  'guest',
];

/**
 * Determines whether a role is a protected system role by matching its
 * lowercased name against the known system role names.
 */
function isSystemRole(role: ErpRole): boolean {
  return SYSTEM_ROLE_NAMES.includes(role.name.toLowerCase());
}

/**
 * RoleDetails — Read-only role details page.
 *
 * Route: `/admin/roles/:roleId`
 *
 * Fetches a single role by ID from the Identity service via
 * `GET /v1/identity/roles/:id`, displays the role's Name and Description
 * in a read-only layout, and provides action buttons for editing
 * (Manage link) and deletion (with a confirmation modal that blocks
 * deletion of the three built-in system roles: administrator, regular,
 * and guest).
 *
 * This page has no direct equivalent in the monolith source (the
 * monolith's SDK plugin only had list, create, and manage pages for
 * roles). It was introduced as a read-only intermediate view before
 * editing, consistent with the UserDetails page pattern.
 */
function RoleDetails(): React.JSX.Element {
  const { roleId } = useParams<{ roleId: string }>();
  const navigate = useNavigate();

  /* ------------------------------------------------------------------ */
  /*  Data fetching                                                      */
  /* ------------------------------------------------------------------ */
  const {
    data: roleData,
    isLoading,
    isError,
    error,
  } = useRole(roleId);

  const deleteRoleMutation = useDeleteRole();

  /* ------------------------------------------------------------------ */
  /*  Local state                                                        */
  /* ------------------------------------------------------------------ */
  const [isDeleteModalVisible, setIsDeleteModalVisible] = useState(false);

  /* ------------------------------------------------------------------ */
  /*  Derived values                                                     */
  /* ------------------------------------------------------------------ */
  const role: ErpRole | undefined = roleData?.object;
  const isProtected = role ? isSystemRole(role) : false;

  /* ------------------------------------------------------------------ */
  /*  Event handlers                                                     */
  /* ------------------------------------------------------------------ */
  const handleDeleteClick = useCallback(() => {
    setIsDeleteModalVisible(true);
  }, []);

  const handleCloseModal = useCallback(() => {
    setIsDeleteModalVisible(false);
  }, []);

  const handleConfirmDelete = useCallback(async () => {
    if (!roleId) return;
    try {
      await deleteRoleMutation.mutateAsync(roleId);
      setIsDeleteModalVisible(false);
      navigate('/admin/roles');
    } catch {
      /* Mutation error state is surfaced inside the modal via
         deleteRoleMutation.isError — no additional handling needed. */
    }
  }, [roleId, deleteRoleMutation, navigate]);

  /* ------------------------------------------------------------------ */
  /*  Loading state                                                      */
  /* ------------------------------------------------------------------ */
  if (isLoading) {
    return (
      <div
        className="flex items-center justify-center"
        style={{ minBlockSize: '24rem' }}
        role="status"
        aria-label="Loading role details"
      >
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-red-600" />
      </div>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Error / not-found state                                            */
  /* ------------------------------------------------------------------ */
  if (isError || !role) {
    const errorMessage =
      error instanceof Error
        ? error.message
        : 'The requested role could not be loaded. It may have been deleted or the ID is invalid.';

    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center">
        <h2 className="text-lg font-semibold text-red-800">Role not found</h2>
        <p className="mt-2 text-sm text-red-600">{errorMessage}</p>
        <Link
          to="/admin/roles"
          className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
        >
          Back to Roles
        </Link>
      </div>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Main render                                                        */
  /* ------------------------------------------------------------------ */
  return (
    <div className="space-y-6">
      {/* ---- Page header ---- */}
      <div className="flex flex-wrap items-center justify-between gap-4 border-b border-gray-200 pb-4">
        {/* Breadcrumb + title */}
        <div className="flex items-center gap-3">
          <Link
            to="/admin/roles"
            className="inline-flex items-center gap-1 text-sm text-gray-500 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            aria-label="Back to role list"
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
            Roles
          </Link>

          <span className="text-gray-300" aria-hidden="true">
            /
          </span>

          <h1 className="text-xl font-semibold text-gray-900">Role Details</h1>
        </div>

        {/* Action buttons */}
        <div className="flex items-center gap-2">
          <Link
            to={`/admin/roles/${role.id}/manage`}
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
            disabled={isProtected}
            className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
            aria-label={
              isProtected
                ? 'System role cannot be deleted'
                : 'Delete this role'
            }
            title={
              isProtected ? 'System role cannot be deleted' : 'Delete role'
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
        <div className="px-6 py-5">
          {/* Role icon + name header */}
          <div className="flex items-center gap-3 mb-6">
            <div
              className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-red-100 text-red-600"
              aria-hidden="true"
            >
              <svg
                className="h-5 w-5"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z"
                />
              </svg>
            </div>
            <div>
              <h2 className="text-lg font-semibold text-gray-900">
                {role.name}
              </h2>
              {isProtected && (
                <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-800">
                  System Role
                </span>
              )}
            </div>
          </div>

          {/* Field details */}
          <dl className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
            <div>
              <dt className="text-sm font-medium text-gray-500">Name</dt>
              <dd className="mt-1 text-sm text-gray-900">{role.name}</dd>
            </div>

            <div>
              <dt className="text-sm font-medium text-gray-500">ID</dt>
              <dd className="mt-1 break-all font-mono text-sm text-gray-600">
                {role.id}
              </dd>
            </div>

            <div className="sm:col-span-2">
              <dt className="text-sm font-medium text-gray-500">
                Description
              </dt>
              <dd className="mt-1 text-sm text-gray-900">
                {role.description || '\u2014'}
              </dd>
            </div>
          </dl>
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
              disabled={deleteRoleMutation.isPending}
              className="rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50"
            >
              {deleteRoleMutation.isPending ? 'Deleting\u2026' : 'Delete Role'}
            </button>
          </>
        }
      >
        <div className="space-y-2">
          <p className="text-sm text-gray-600">
            Are you sure you want to delete role{' '}
            <strong className="text-gray-900">{role.name}</strong>?
          </p>
          <p className="text-sm text-red-600">This action cannot be undone.</p>
          {deleteRoleMutation.isError && (
            <p className="text-sm text-red-600" role="alert">
              Failed to delete role. Please try again.
            </p>
          )}
        </div>
      </Modal>
    </div>
  );
}

export default RoleDetails;
