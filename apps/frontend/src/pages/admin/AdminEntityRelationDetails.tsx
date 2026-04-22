/**
 * AdminEntityRelationDetails — Read-Only Relation Detail Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/relation-details.cshtml[.cs]`.
 * Displays all relation properties in read-only mode and provides a delete
 * action for non-system relations with a confirmation modal.
 *
 * Route: `/admin/entities/:entityId/relations/:relationId`
 *
 * Source mapping:
 *  - relation-details.cshtml.cs PageInit() → useEntity() + useRelation() TanStack Query
 *  - relation-details.cshtml.cs OnPost()   → useDeleteRelation() mutation + Modal confirmation
 *  - PcFieldSelect (Type, display-only)    → read-only text with relationTypeLabel()
 *  - PcFieldGuid (Id, display-only)        → monospace read-only text
 *  - PcFieldText (Name, display-only)      → read-only text
 *  - PcFieldCheckbox (System, display-only) → read-only badge
 *  - Origin/Target Entity/Field (display)  → read-only text rows
 *  - AdminPageUtils.GetEntityAdminSubNav   → ENTITY_SUB_NAV Link tabs
 *  - "Manage Relation" anchor              → Link to manage route
 *  - "Delete Relation" / "Delete locked"   → button + Modal or disabled button
 */

import { useState, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useEntity, useRelation, useDeleteRelation } from '../../hooks/useEntities';
import type { EntityRelation } from '../../types/entity';
import { EntityRelationType } from '../../types/entity';
import Modal from '../../components/common/Modal';

/* ────────────────────────────────────────────────────────────────
 *  Constants
 * ──────────────────────────────────────────────────────────────── */

/**
 * Entity admin sub-navigation entries matching the monolith's
 * `AdminPageUtils.GetEntityAdminSubNav` toolbar.
 */
const ENTITY_SUB_NAV: ReadonlyArray<{
  id: string;
  label: string;
  pathSuffix: string;
}> = [
  { id: 'details', label: 'Details', pathSuffix: '' },
  { id: 'fields', label: 'Fields', pathSuffix: '/fields' },
  { id: 'relations', label: 'Relations', pathSuffix: '/relations' },
  { id: 'data', label: 'Data', pathSuffix: '/data' },
  { id: 'pages', label: 'Pages', pathSuffix: '/pages' },
  { id: 'web-api', label: 'Web API', pathSuffix: '/web-api' },
];

/* ────────────────────────────────────────────────────────────────
 *  Helpers
 * ──────────────────────────────────────────────────────────────── */

/**
 * Converts an `EntityRelationType` enum value to a human-readable label.
 * The source monolith renders this via a PcFieldSelect with
 * SelectOptions that originally filtered out OneToOne (value "1").
 * In the details view we display all types as text since the field
 * is always read-only.
 */
function relationTypeLabel(relationType: EntityRelationType | undefined): string {
  switch (relationType) {
    case EntityRelationType.OneToOne:
      return 'One to One';
    case EntityRelationType.OneToMany:
      return 'One to Many';
    case EntityRelationType.ManyToMany:
      return 'Many to Many';
    default:
      return 'Unknown';
  }
}

/* ────────────────────────────────────────────────────────────────
 *  Component
 * ──────────────────────────────────────────────────────────────── */

/**
 * AdminEntityRelationDetails renders a read-only view of a single
 * entity relation with all its properties, plus a delete action
 * (disabled for system relations) and a link to the manage/edit page.
 *
 * Replaces the monolith's `relation-details.cshtml` Razor Page.
 */
export default function AdminEntityRelationDetails() {
  /* --- Route params ------------------------------------------------ */
  const { entityId = '', relationId = '' } = useParams<{
    entityId: string;
    relationId: string;
  }>();

  const navigate = useNavigate();

  /* --- Server state ------------------------------------------------ */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
    error: entityErrorObj,
  } = useEntity(entityId);

  const {
    data: relation,
    isLoading: relationLoading,
    isError: relationError,
    error: relationErrorObj,
  } = useRelation(relationId);

  const {
    mutateAsync: deleteRelation,
    isPending: isDeleting,
    isError: deleteError,
    error: deleteErrorObj,
    reset: resetDeleteMutation,
  } = useDeleteRelation();

  /* --- Local state: delete modal ----------------------------------- */
  const [isDeleteModalVisible, setIsDeleteModalVisible] = useState(false);

  /* --- Handlers ---------------------------------------------------- */

  /**
   * Opens the delete confirmation modal.
   * Resets any previous mutation error state before showing.
   */
  const handleOpenDeleteModal = useCallback(() => {
    resetDeleteMutation();
    setIsDeleteModalVisible(true);
  }, [resetDeleteMutation]);

  /** Closes the delete confirmation modal. */
  const handleCloseDeleteModal = useCallback(() => {
    setIsDeleteModalVisible(false);
  }, []);

  /**
   * Confirms relation deletion. Replaces the monolith's OnPost() logic:
   *   1. Call EntityRelationManager.Delete(Relation.Id)
   *   2. Redirect to relations list on success
   *
   * The useDeleteRelation mutation automatically invalidates the
   * relations and entities query caches.
   */
  const handleConfirmDelete = useCallback(async () => {
    try {
      await deleteRelation(relationId);
      setIsDeleteModalVisible(false);
      navigate(`/admin/entities/${entityId}/relations`, { replace: true });
    } catch {
      /* Error state captured by the mutation — displayed in modal. */
    }
  }, [deleteRelation, relationId, entityId, navigate]);

  /* --- Derived state ----------------------------------------------- */
  const isLoading = entityLoading || relationLoading;
  const isError = entityError || relationError;
  const errorMessage =
    entityErrorObj?.message ||
    relationErrorObj?.message ||
    'Failed to load relation data.';

  /* --- Loading state ----------------------------------------------- */
  if (isLoading) {
    return (
      <div className="flex min-h-[200px] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            role="status"
            aria-label="Loading relation data"
          />
          <p className="text-sm text-gray-500">Loading relation…</p>
        </div>
      </div>
    );
  }

  /* --- Error state ------------------------------------------------- */
  if (isError || !relation) {
    return (
      <div className="rounded-md border border-red-200 bg-red-50 p-6" role="alert">
        <h2 className="mb-2 text-lg font-semibold text-red-800">
          Error Loading Relation
        </h2>
        <p className="text-sm text-red-700">
          {!relation && !isError ? 'Relation not found.' : errorMessage}
        </p>
        <Link
          to={`/admin/entities/${entityId}/relations`}
          className="mt-4 inline-block text-sm font-medium text-red-700 underline hover:text-red-900"
        >
          ← Back to Relations
        </Link>
      </div>
    );
  }

  /*
   * Extract all relation fields for the read-only display.
   * Each field from the EntityRelation interface is displayed as a
   * labeled read-only row, mirroring the source relation-details.cshtml.
   */
  const relationData: EntityRelation = relation;

  /* --- Render ------------------------------------------------------ */
  return (
    <div className="mx-auto max-w-screen-xl px-4 py-6">
      {/* ── Page Header ─────────────────────────────────────────── */}
      <header className="mb-6">
        <nav aria-label="Breadcrumb" className="mb-2">
          <ol className="flex items-center gap-1.5 text-sm text-gray-500">
            <li>
              <Link
                to="/admin/entities"
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                Entities
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to={`/admin/entities/${entityId}`}
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                {entity?.label || entity?.name || 'Entity'}
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li>
              <Link
                to={`/admin/entities/${entityId}/relations`}
                className="hover:text-blue-600 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
              >
                Relations
              </Link>
            </li>
            <li aria-hidden="true">/</li>
            <li className="font-medium text-gray-800" aria-current="page">
              {relationData.name || 'Relation Details'}
            </li>
          </ol>
        </nav>

        <div className="flex items-center gap-3">
          {entity?.iconName && (
            <span
              className="flex h-10 w-10 items-center justify-center rounded-md text-white"
              style={{ backgroundColor: entity.color || '#1d4ed8' }}
              aria-hidden="true"
            >
              <i className={`fa fa-${entity.iconName}`} />
            </span>
          )}
          <div>
            <h1 className="text-2xl font-bold text-gray-900">
              Relation Details
            </h1>
            <p className="text-sm text-gray-500">
              {entity?.label || entity?.name || ''}
              {' — '}
              {relationData.name}
            </p>
          </div>
        </div>
      </header>

      {/* ── Entity Sub-Navigation ─────────────────────────────── */}
      <nav aria-label="Entity sections" className="mb-6 border-b border-gray-200">
        <ul className="flex gap-0" role="tablist">
          {ENTITY_SUB_NAV.map((tab) => {
            const isActive = tab.id === 'relations';
            return (
              <li key={tab.id} role="presentation">
                <Link
                  to={`/admin/entities/${entityId}${tab.pathSuffix}`}
                  role="tab"
                  aria-selected={isActive}
                  className={[
                    'inline-block px-4 py-2.5 text-sm font-medium transition-colors',
                    isActive
                      ? 'border-b-2 border-blue-600 text-blue-600'
                      : 'text-gray-500 hover:border-b-2 hover:border-gray-300 hover:text-gray-700',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* ── Action Toolbar ────────────────────────────────────── */}
      <div className="mb-6 flex items-center gap-3">
        <Link
          to={`/admin/entities/${entityId}/relations/${relationId}/manage`}
          className="inline-flex items-center gap-2 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
        >
          <i className="fa fa-pencil-alt text-xs" aria-hidden="true" />
          Manage Relation
        </Link>

        {relationData.system ? (
          <button
            type="button"
            disabled
            className="inline-flex items-center gap-2 rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-400 shadow-sm ring-1 ring-inset ring-gray-200 cursor-not-allowed"
            title="System relations cannot be deleted"
          >
            <i className="fa fa-lock text-xs" aria-hidden="true" />
            Delete Locked
          </button>
        ) : (
          <button
            type="button"
            onClick={handleOpenDeleteModal}
            className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2"
          >
            <i className="fa fa-trash-alt text-xs" aria-hidden="true" />
            Delete Relation
          </button>
        )}
      </div>

      {/* ── Relation Properties (Read-Only) ──────────────────── */}
      <section
        className="rounded-lg border border-gray-200 bg-white shadow-sm"
        aria-labelledby="relation-properties-heading"
      >
        <h2
          id="relation-properties-heading"
          className="border-b border-gray-200 px-6 py-4 text-lg font-semibold text-gray-900"
        >
          Relation Properties
        </h2>

        <div className="divide-y divide-gray-100 px-6">
          {/* Type */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Type</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationTypeLabel(relationData.relationType)}
            </dd>
          </div>

          {/* Id */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Id</dt>
            <dd className="font-mono text-sm text-gray-600 sm:col-span-2">
              {relationData.id}
            </dd>
          </div>

          {/* Name */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Name</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.name || '—'}
            </dd>
          </div>

          {/* Label */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Label</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.label || '—'}
            </dd>
          </div>

          {/* Description */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Description</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.description || '—'}
            </dd>
          </div>

          {/* System */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">System</dt>
            <dd className="sm:col-span-2">
              {relationData.system ? (
                <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                  Yes
                </span>
              ) : (
                <span className="inline-flex items-center rounded-full bg-gray-100 px-2.5 py-0.5 text-xs font-medium text-gray-600">
                  No
                </span>
              )}
            </dd>
          </div>

          {/* Origin Entity */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Origin Entity</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.originEntityName || relationData.originEntityId || '—'}
            </dd>
          </div>

          {/* Origin Field */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Origin Field</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.originFieldName || relationData.originFieldId || '—'}
            </dd>
          </div>

          {/* Target Entity */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Target Entity</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.targetEntityName || relationData.targetEntityId || '—'}
            </dd>
          </div>

          {/* Target Field */}
          <div className="grid grid-cols-1 gap-1 py-4 sm:grid-cols-3 sm:gap-4">
            <dt className="text-sm font-semibold text-gray-700">Target Field</dt>
            <dd className="text-sm text-gray-900 sm:col-span-2">
              {relationData.targetFieldName || relationData.targetFieldId || '—'}
            </dd>
          </div>
        </div>
      </section>

      {/* ── Delete Confirmation Modal ─────────────────────────── */}
      <Modal
        isVisible={isDeleteModalVisible}
        title="Delete Relation"
        onClose={handleCloseDeleteModal}
      >
        <div className="space-y-4">
          <p className="text-sm text-gray-700">
            Are you sure you want to delete the relation{' '}
            <strong className="font-semibold text-gray-900">
              {relationData.name}
            </strong>
            ? This action cannot be undone.
          </p>

          {deleteError && (
            <div
              className="rounded-md border border-red-200 bg-red-50 p-3"
              role="alert"
            >
              <p className="text-sm text-red-700">
                {deleteErrorObj?.message || 'Failed to delete relation. Please try again.'}
              </p>
            </div>
          )}

          <div className="flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
            <button
              type="button"
              onClick={handleCloseDeleteModal}
              disabled={isDeleting}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={isDeleting}
              className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isDeleting && (
                <span
                  className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                  aria-hidden="true"
                />
              )}
              {isDeleting ? 'Deleting…' : 'Confirm Delete'}
            </button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
