import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useEntity, useRelation, useDeleteRelation } from '../../hooks/useEntities';
import Modal from '../../components/common/Modal';
import type { EntityRelation, Entity } from '../../types/entity';
import { EntityRelationType } from '../../types/entity';

/**
 * Maps an EntityRelationType enum value to its human-readable display string.
 * Matches the monolith's TypeOptions select display from relation-details.cshtml.
 */
function getRelationTypeName(relationType: EntityRelationType): string {
  switch (relationType) {
    case EntityRelationType.OneToOne:
      return 'One-to-One';
    case EntityRelationType.OneToMany:
      return 'One-to-Many';
    case EntityRelationType.ManyToMany:
      return 'Many-to-Many';
    default:
      return 'Unknown';
  }
}

/**
 * Entity admin sub-navigation tab definitions.
 * Replaces the monolith's AdminPageUtils.GetEntityAdminSubNav(ErpEntity, "relations")
 * from relation-details.cshtml.cs header toolbar.
 */
const ADMIN_SUB_NAV_TABS = [
  { key: 'general', label: 'General', pathSuffix: '' },
  { key: 'fields', label: 'Fields', pathSuffix: '/fields' },
  { key: 'relations', label: 'Relations', pathSuffix: '/relations' },
] as const;

/** Active tab for this page */
const ACTIVE_TAB = 'relations';

/**
 * RelationDetails — Read-only relation detail view with delete capability.
 *
 * Route: /entities/:entityId/relations/:relationId
 *
 * Replaces the monolith's relation-details.cshtml[.cs]:
 * - Displays all relation properties in read-only mode
 * - Provides "Manage Relation" navigation link
 * - Provides "Delete Relation" action with confirmation modal
 *   (disabled for system relations with "Delete locked" tooltip)
 * - Renders entity admin sub-nav tabs with "Relations" active
 *
 * @returns React element for the relation detail page
 */
export default function RelationDetails(): React.JSX.Element {
  /* ------------------------------------------------------------------ */
  /*  URL Parameters & Navigation                                        */
  /* ------------------------------------------------------------------ */
  const { entityId, relationId } = useParams<{
    entityId: string;
    relationId: string;
  }>();
  const navigate = useNavigate();

  /* ------------------------------------------------------------------ */
  /*  Data Fetching (TanStack Query)                                     */
  /* ------------------------------------------------------------------ */

  /**
   * Fetch the parent entity for sub-nav context and validation.
   * Replaces: entMan.ReadEntity(ParentRecordId ?? Guid.Empty).Object
   */
  const {
    data: entity,
    isLoading: isEntityLoading,
    isError: isEntityError,
    error: entityError,
  } = useEntity(entityId ?? '');

  /**
   * Fetch the specific relation for read-only display.
   * Replaces: relMan.Read().Object filtering by relation ID
   */
  const {
    data: relation,
    isLoading: isRelationLoading,
    isError: isRelationError,
    error: relationError,
  } = useRelation(relationId ?? '');

  /**
   * Delete mutation for the relation.
   * Replaces: EntityRelationManager.Delete(Relation.Id) from OnPost
   * Invalidates ['relations'] and ['entities'] query keys on success.
   */
  const deleteRelationMutation = useDeleteRelation();

  /* ------------------------------------------------------------------ */
  /*  Local State                                                        */
  /* ------------------------------------------------------------------ */

  /** Controls visibility of the delete confirmation modal */
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* ------------------------------------------------------------------ */
  /*  Handlers                                                           */
  /* ------------------------------------------------------------------ */

  /**
   * Toggles the delete confirmation modal visibility.
   * Memoized to prevent unnecessary re-renders of the Modal child.
   */
  const handleToggleDeleteModal = useCallback(() => {
    setShowDeleteModal((prev) => !prev);
  }, []);

  /**
   * Handles the confirmed delete action.
   * Calls DELETE /v1/relations/{id} and navigates to the relation list
   * on success. Replaces the monolith's OnPost handler that calls
   * EntityRelationManager.Delete(Relation.Id) and redirects.
   */
  const handleConfirmDelete = useCallback(async () => {
    if (!relationId) return;
    try {
      await deleteRelationMutation.mutateAsync(relationId);
      navigate(`/entities/${entityId}/relations`, { replace: true });
    } catch {
      // Error is surfaced via deleteRelationMutation.error
      // Modal stays open so user can see the error or retry
    }
  }, [deleteRelationMutation, relationId, entityId, navigate]);

  /* ------------------------------------------------------------------ */
  /*  Loading State                                                      */
  /* ------------------------------------------------------------------ */
  if (isEntityLoading || isRelationLoading) {
    return (
      <main className="flex flex-1 items-center justify-center p-8">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading relation details"
          />
          <p className="text-sm text-gray-500">Loading relation details…</p>
        </div>
      </main>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Error State                                                        */
  /* ------------------------------------------------------------------ */
  if (isEntityError || isRelationError) {
    const errorMessage =
      (entityError instanceof Error ? entityError.message : '') ||
      (relationError instanceof Error ? relationError.message : '') ||
      'An error occurred while loading the relation.';

    return (
      <main className="flex flex-1 items-center justify-center p-8">
        <div className="w-full max-w-md rounded-lg border border-red-300 bg-red-50 p-6 text-center">
          <svg
            className="mx-auto mb-3 h-10 w-10 text-red-400"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
          <h2 className="mb-2 text-lg font-semibold text-red-800">
            Error Loading Relation
          </h2>
          <p className="mb-4 text-sm text-red-600">{errorMessage}</p>
          <Link
            to={`/entities/${entityId ?? ''}/relations`}
            className="inline-block rounded bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2"
          >
            Back to Relations
          </Link>
        </div>
      </main>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Not Found State                                                    */
  /* ------------------------------------------------------------------ */
  if (!entity || !relation) {
    return (
      <main className="flex flex-1 items-center justify-center p-8">
        <div className="w-full max-w-md rounded-lg border border-yellow-300 bg-yellow-50 p-6 text-center">
          <svg
            className="mx-auto mb-3 h-10 w-10 text-yellow-400"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
          <h2 className="mb-2 text-lg font-semibold text-yellow-800">
            {!entity ? 'Entity Not Found' : 'Relation Not Found'}
          </h2>
          <p className="mb-4 text-sm text-yellow-700">
            {!entity
              ? 'The requested entity does not exist or has been removed.'
              : 'The requested relation does not exist or has been removed.'}
          </p>
          <Link
            to={!entity ? '/entities' : `/entities/${entityId ?? ''}/relations`}
            className="inline-block rounded bg-yellow-600 px-4 py-2 text-sm font-medium text-white hover:bg-yellow-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-yellow-500 focus-visible:ring-offset-2"
          >
            {!entity ? 'Back to Entities' : 'Back to Relations'}
          </Link>
        </div>
      </main>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Main Render                                                        */
  /* ------------------------------------------------------------------ */
  const isSystem = relation.system === true;

  return (
    <main className="flex-1 overflow-y-auto">
      {/* ============================================================ */}
      {/* Entity Admin Sub-Nav Tabs                                     */}
      {/* Replaces: HeaderToolbar AdminPageUtils.GetEntityAdminSubNav   */}
      {/* ============================================================ */}
      <nav
        className="border-b border-gray-200 bg-white"
        aria-label="Entity administration"
      >
        <div className="mx-auto max-w-7xl px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-4">
              <h1 className="py-3 text-lg font-semibold text-gray-900">
                {entity.label || entity.name}
              </h1>
            </div>
            <ul className="flex gap-1" role="tablist">
              {ADMIN_SUB_NAV_TABS.map((tab) => {
                const isActive = tab.key === ACTIVE_TAB;
                return (
                  <li key={tab.key} role="presentation">
                    <Link
                      to={`/entities/${entityId}${tab.pathSuffix}`}
                      role="tab"
                      aria-selected={isActive}
                      aria-current={isActive ? 'page' : undefined}
                      className={`inline-block border-b-2 px-3 py-3 text-sm font-medium transition-colors ${
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
          </div>
        </div>
      </nav>

      {/* ============================================================ */}
      {/* Page Header with Actions                                      */}
      {/* Replaces: HeaderActions from relation-details.cshtml.cs        */}
      {/* ============================================================ */}
      <div className="border-b border-gray-200 bg-white">
        <div className="mx-auto flex max-w-7xl items-center justify-between px-4 py-4 sm:px-6 lg:px-8">
          <div>
            <h2 className="text-xl font-bold text-gray-900">
              Relation Details
            </h2>
            <p className="mt-1 text-sm text-gray-500">
              Viewing relation:{' '}
              <span className="font-medium text-gray-700">
                {relation.name}
              </span>
            </p>
          </div>

          <div className="flex items-center gap-3">
            {/* Delete Relation button */}
            {isSystem ? (
              /* System relations cannot be deleted — show disabled button with tooltip */
              <span
                className="inline-flex cursor-not-allowed items-center gap-1.5 rounded bg-gray-100 px-3 py-2 text-sm font-medium text-gray-400"
                title="System objects cannot be deleted"
                aria-disabled="true"
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
                    d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z"
                  />
                </svg>
                Delete locked
              </span>
            ) : (
              /* Non-system relations: confirmation-gated delete */
              <button
                type="button"
                onClick={handleToggleDeleteModal}
                disabled={deleteRelationMutation.isPending}
                className="inline-flex items-center gap-1.5 rounded bg-red-600 px-3 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
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
                Delete Relation
              </button>
            )}

            {/* Manage Relation link */}
            <Link
              to={`/entities/${entityId}/relations/${relationId}/manage`}
              className="inline-flex items-center gap-1.5 rounded bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
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
              Manage Relation
            </Link>
          </div>
        </div>
      </div>

      {/* ============================================================ */}
      {/* Read-Only Relation Detail Fields                              */}
      {/* Replaces: wv-field-* display-mode fields from cshtml          */}
      {/* ============================================================ */}
      <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6 lg:px-8">
        {/* Delete mutation error banner */}
        {deleteRelationMutation.isError && (
          <div
            className="mb-6 rounded-lg border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-700"
            role="alert"
          >
            <span className="font-medium">Delete failed:</span>{' '}
            {deleteRelationMutation.error instanceof Error
              ? deleteRelationMutation.error.message
              : 'An error occurred while deleting the relation.'}
          </div>
        )}

        <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
          <div className="border-b border-gray-200 bg-gray-50 px-6 py-4">
            <h3 className="text-base font-semibold text-gray-900">
              Relation Properties
            </h3>
          </div>

          <dl className="divide-y divide-gray-200">
            {/* Name */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">Name</dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                {relation.name || '—'}
              </dd>
            </div>

            {/* Label */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">Label</dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                {relation.label || '—'}
              </dd>
            </div>

            {/* Description */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">
                Description
              </dt>
              <dd className="whitespace-pre-wrap text-sm text-gray-900 sm:col-span-2">
                {relation.description || '—'}
              </dd>
            </div>

            {/* System (boolean badge) */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">System</dt>
              <dd className="sm:col-span-2">
                <span
                  className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${
                    isSystem
                      ? 'bg-blue-100 text-blue-800'
                      : 'bg-gray-100 text-gray-800'
                  }`}
                >
                  {isSystem ? 'Yes' : 'No'}
                </span>
              </dd>
            </div>

            {/* Type */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">Type</dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                <span className="inline-flex items-center rounded-full bg-purple-100 px-2.5 py-0.5 text-xs font-medium text-purple-800">
                  {getRelationTypeName(relation.relationType)}
                </span>
              </dd>
            </div>

            {/* Origin Entity */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">
                Origin Entity
              </dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                {relation.originEntityName || relation.originEntityId || '—'}
              </dd>
            </div>

            {/* Origin Field */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">
                Origin Field
              </dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                {relation.originFieldName || relation.originFieldId || '—'}
              </dd>
            </div>

            {/* Target Entity */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">
                Target Entity
              </dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                {relation.targetEntityName || relation.targetEntityId || '—'}
              </dd>
            </div>

            {/* Target Field */}
            <div className="grid grid-cols-1 gap-1 px-6 py-4 sm:grid-cols-3 sm:gap-4">
              <dt className="text-sm font-medium text-gray-500">
                Target Field
              </dt>
              <dd className="text-sm text-gray-900 sm:col-span-2">
                {relation.targetFieldName || relation.targetFieldId || '—'}
              </dd>
            </div>
          </dl>
        </div>
      </div>

      {/* ============================================================ */}
      {/* Delete Confirmation Modal                                     */}
      {/* Replaces: hidden form + confirm() JS dialog from cshtml       */}
      {/* ============================================================ */}
      <Modal
        isVisible={showDeleteModal}
        id="delete-relation-modal"
        title="Delete Relation"
        onClose={handleToggleDeleteModal}
        footer={
          <div className="flex items-center justify-end gap-3">
            <button
              type="button"
              onClick={handleToggleDeleteModal}
              disabled={deleteRelationMutation.isPending}
              className="rounded border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={deleteRelationMutation.isPending}
              className="inline-flex items-center gap-1.5 rounded bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {deleteRelationMutation.isPending ? (
                <>
                  <span
                    className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                    aria-hidden="true"
                  />
                  Deleting…
                </>
              ) : (
                'Delete'
              )}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete the relation{' '}
          <span className="font-semibold text-gray-900">
            {relation.name}
          </span>
          ? This action cannot be undone.
        </p>
        {deleteRelationMutation.isError && (
          <div
            className="mt-3 rounded border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-700"
            role="alert"
          >
            {deleteRelationMutation.error instanceof Error
              ? deleteRelationMutation.error.message
              : 'Failed to delete the relation. Please try again.'}
          </div>
        )}
      </Modal>
    </main>
  );
}
