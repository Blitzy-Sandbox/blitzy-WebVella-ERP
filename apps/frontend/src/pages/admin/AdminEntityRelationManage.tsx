/**
 * AdminEntityRelationManage — Relation Edit Page
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/relation-manage.cshtml[.cs]`.
 * Provides a form to edit relation properties — primarily the IsSystem toggle
 * while preserving immutable endpoint and type information in display-only mode.
 *
 * Route: `/admin/entities/:entityId/relations/:relationId/manage`
 *
 * Source mapping:
 *  - relation-manage.cshtml.cs OnGet()  → useRelation() TanStack Query + useEffect sync
 *  - relation-manage.cshtml.cs OnPost() → useUpdateRelation() mutation + form onSubmit
 *  - PcForm (ManageRecord)               → DynamicForm with stacked labels
 *  - PcFieldSelect (Type, display-only)  → read-only text display
 *  - PcFieldCheckbox (IsSystem)          → native <input type="checkbox">
 *  - AdminPageUtils.GetEntityAdminSubNav → ENTITY_SUB_NAV Link tabs
 */

import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useEntity, useRelation, useUpdateRelation } from '../../hooks/useEntities';
import type { EntityRelation } from '../../types/entity';
import { EntityRelationType } from '../../types/entity';
import DynamicForm from '../../components/forms/DynamicForm';

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
 * SelectOptions filtering out OneToOne (value "1"). Here we display
 * all types as text since the field is always read-only.
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
 *  Form State Interface
 * ──────────────────────────────────────────────────────────────── */

/**
 * Local form state for editable relation fields.
 * Mirrors the monolith's `[BindProperty] Name`, `Label`,
 * `Description`, and `IsSystem` — although in the source,
 * Label and Description are commented out in the template
 * and hardcoded in OnPost(). We expose all four per the
 * schema specification.
 */
interface RelationFormState {
  name: string;
  label: string;
  description: string;
  system: boolean;
}

/** Initial blank state used before server data arrives. */
const INITIAL_FORM_STATE: RelationFormState = {
  name: '',
  label: '',
  description: '',
  system: false,
};

/* ────────────────────────────────────────────────────────────────
 *  Component
 * ──────────────────────────────────────────────────────────────── */

export default function AdminEntityRelationManage() {
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
    mutateAsync: updateRelation,
    isPending: isUpdating,
    isError: updateError,
    error: updateErrorObj,
    reset: resetMutation,
  } = useUpdateRelation();

  /* --- Local form state -------------------------------------------- */
  const [formState, setFormState] = useState<RelationFormState>(INITIAL_FORM_STATE);
  const [hasInitialized, setHasInitialized] = useState(false);

  /**
   * Synchronise fetched relation data into local form state.
   * Replaces the monolith's OnGet() pre-population of bind properties:
   *   Name = Relation.Name
   *   Label = Relation.Label
   *   Description = Relation.Description
   *   IsSystem = Relation.System
   */
  useEffect(() => {
    if (relation && !hasInitialized) {
      setFormState({
        name: relation.name ?? '',
        label: relation.label ?? '',
        description: relation.description ?? '',
        system: relation.system ?? false,
      });
      setHasInitialized(true);
    }
  }, [relation, hasInitialized]);

  /* --- Form handlers ----------------------------------------------- */

  /**
   * Handles form submission. Replaces the monolith's OnPost() logic:
   *   1. Read relation via EntityRelationManager
   *   2. Update mutable fields
   *   3. Call relMan.Update(updRelation)
   *   4. Redirect on success
   */
  const handleSubmit = useCallback(
    async (event: React.FormEvent<HTMLFormElement>) => {
      event.preventDefault();
      resetMutation();

      try {
        await updateRelation({
          id: relationId,
          relation: {
            name: formState.name,
            label: formState.label,
            description: formState.description,
            system: formState.system,
          },
        });
        /* On success, navigate back to the relation details page,
         * matching the monolith's redirect after successful update. */
        navigate(
          `/admin/entities/${entityId}/relations/${relationId}`,
          { replace: true },
        );
      } catch {
        /* Error state is captured by mutation — displayed in the form. */
      }
    },
    [
      formState,
      entityId,
      relationId,
      updateRelation,
      resetMutation,
      navigate,
    ],
  );

  /** Updates a single form field value. */
  const handleFieldChange = useCallback(
    (field: keyof RelationFormState, value: string | boolean) => {
      setFormState((prev) => ({ ...prev, [field]: value }));
    },
    [],
  );

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
          {!relation && !isError
            ? 'Relation not found.'
            : errorMessage}
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
              Manage Relation
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
              Manage Relation
            </h1>
            <p className="text-sm text-gray-500">
              {entity?.label || entity?.name || ''}
              {' — '}
              {relation.name}
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

      {/* ── Mutation Error Banner ─────────────────────────────── */}
      {updateError && (
        <div
          className="mb-4 rounded-md border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <p className="text-sm font-medium text-red-800">
            Failed to update relation.
          </p>
          <p className="mt-1 text-sm text-red-700">
            {updateErrorObj?.message || 'An unexpected error occurred. Please try again.'}
          </p>
        </div>
      )}

      {/* ── Relation Manage Form ──────────────────────────────── */}
      <DynamicForm
        name="ManageRecord"
        labelMode="stacked"
        onSubmit={handleSubmit}
      >
        {/* -- Display-Only Section ----------------------------- */}
        <fieldset className="mb-6">
          <legend className="sr-only">Relation Information (read-only)</legend>

          {/* Relation Type (display-only) */}
          <div className="mb-4">
            <label className="mb-1 block text-sm font-semibold text-gray-700">
              Relation Type
            </label>
            <p className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-800">
              {relationTypeLabel(relation.relationType)}
            </p>
          </div>

          {/* Relation ID (display-only) */}
          <div className="mb-4">
            <label className="mb-1 block text-sm font-semibold text-gray-700">
              Relation ID
            </label>
            <p className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 font-mono text-sm text-gray-600">
              {relation.id}
            </p>
          </div>

          {/* Origin Entity (display-only) */}
          <div className="mb-4">
            <label className="mb-1 block text-sm font-semibold text-gray-700">
              Origin Entity
            </label>
            <p className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-800">
              {relation.originEntityName || relation.originEntityId || '—'}
            </p>
          </div>

          {/* Origin Field (display-only) */}
          <div className="mb-4">
            <label className="mb-1 block text-sm font-semibold text-gray-700">
              Origin Field
            </label>
            <p className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-800">
              {relation.originFieldName || relation.originFieldId || '—'}
            </p>
          </div>

          {/* Target Entity (display-only) */}
          <div className="mb-4">
            <label className="mb-1 block text-sm font-semibold text-gray-700">
              Target Entity
            </label>
            <p className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-800">
              {relation.targetEntityName || relation.targetEntityId || '—'}
            </p>
          </div>

          {/* Target Field (display-only) */}
          <div className="mb-4">
            <label className="mb-1 block text-sm font-semibold text-gray-700">
              Target Field
            </label>
            <p className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-800">
              {relation.targetFieldName || relation.targetFieldId || '—'}
            </p>
          </div>
        </fieldset>

        {/* -- Editable Section --------------------------------- */}
        <fieldset className="mb-6">
          <legend className="sr-only">Editable Relation Properties</legend>

          {/* Name (editable) */}
          <div className="mb-4">
            <label
              htmlFor="relation-name"
              className="mb-1 block text-sm font-semibold text-gray-700"
            >
              Name
            </label>
            <input
              id="relation-name"
              type="text"
              value={formState.name}
              onChange={(e) => handleFieldChange('name', e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              autoComplete="off"
            />
          </div>

          {/* Label (editable) */}
          <div className="mb-4">
            <label
              htmlFor="relation-label"
              className="mb-1 block text-sm font-semibold text-gray-700"
            >
              Label
            </label>
            <input
              id="relation-label"
              type="text"
              value={formState.label}
              onChange={(e) => handleFieldChange('label', e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              autoComplete="off"
            />
          </div>

          {/* Description (editable) */}
          <div className="mb-4">
            <label
              htmlFor="relation-description"
              className="mb-1 block text-sm font-semibold text-gray-700"
            >
              Description
            </label>
            <textarea
              id="relation-description"
              value={formState.description}
              onChange={(e) => handleFieldChange('description', e.target.value)}
              rows={3}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm focus-visible:border-blue-500 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
            />
          </div>

          {/* System (editable checkbox) */}
          <div className="mb-4 flex items-center gap-2">
            <input
              id="relation-system"
              type="checkbox"
              checked={formState.system}
              onChange={(e) => handleFieldChange('system', e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
            />
            <label
              htmlFor="relation-system"
              className="text-sm font-semibold text-gray-700"
            >
              Is System Relation
            </label>
          </div>
        </fieldset>

        {/* -- Action Buttons ----------------------------------- */}
        <div className="flex items-center gap-3 border-t border-gray-200 pt-4">
          <button
            type="submit"
            disabled={isUpdating}
            className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isUpdating && (
              <span
                className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                aria-hidden="true"
              />
            )}
            {isUpdating ? 'Saving…' : 'Save Relation'}
          </button>
          <Link
            to={`/admin/entities/${entityId}/relations/${relationId}`}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
          >
            Cancel
          </Link>
        </div>
      </DynamicForm>
    </div>
  );
}
