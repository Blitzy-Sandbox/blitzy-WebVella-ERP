/**
 * RelationManage.tsx — Edit Relation Page
 *
 * React page component for editing an existing entity relation's mutable
 * properties: Label, Description, and IsSystem. Immutable properties (Name,
 * Type, Origin entity/field, Target entity/field) are displayed read-only.
 *
 * Replaces the monolith's:
 *   - WebVella.Erp.Plugins.SDK/Pages/entity/relation-manage.cshtml
 *   - WebVella.Erp.Plugins.SDK/Pages/entity/relation-manage.cshtml.cs
 *
 * Route: /entities/:entityId/relations/:relationId/manage
 *
 * CRITICAL immutability rules (from EntityRelationManager.Update):
 *   Name, Type, OriginEntityId, OriginFieldId, TargetEntityId, TargetFieldId
 *   are NEVER sent in update requests — only Label, Description, IsSystem.
 */

import React, { useState, useEffect, useCallback, type FormEvent } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, put, type ApiResponse } from '../../api/client';
import DynamicForm, {
  type FormValidation,
} from '../../components/forms/DynamicForm';
import {
  type EntityRelation,
  type Entity,
  EntityRelationType,
} from '../../types/entity';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Mutable form state for editing a relation.
 * Only these three properties may be sent in the PUT request.
 */
interface RelationForm {
  label: string;
  description: string;
  isSystem: boolean;
}

/**
 * Payload sent to PUT /v1/entity-management/relations/:relationId.
 * Matches the mutable subset of EntityRelation that
 * EntityRelationManager.Update() allows to be changed.
 */
interface RelationUpdatePayload {
  label: string;
  description: string;
  system: boolean;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Base URL for the Entity Management service's entity endpoints. */
const ENTITY_BASE = '/v1/entity-management/entities';

/** Base URL for the Entity Management service's relation endpoints. */
const RELATION_BASE = '/v1/entity-management/relations';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Maps an EntityRelationType enum value to a human-readable label.
 * Replaces the C# TypeOptions dropdown (display mode) from relation-manage.cshtml.
 */
function getRelationTypeName(relationType: EntityRelationType): string {
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

/**
 * Returns error message for a specific field, or empty string if none.
 */
function getFieldError(
  errors: Array<{ propertyName: string; message: string }>,
  propertyName: string,
): string {
  const match = errors.find(
    (e) => e.propertyName.toLowerCase() === propertyName.toLowerCase(),
  );
  return match ? match.message : '';
}

// ---------------------------------------------------------------------------
// Entity Admin Sub-Navigation
// ---------------------------------------------------------------------------

/**
 * Entity admin sub-navigation tabs definition.
 *
 * Replaces the monolith's SDK EntitySubNav that appears on every entity
 * admin page. The "Relations" tab is active on this page.
 */
interface EntitySubNavProps {
  entityId: string;
  activeTab: 'details' | 'fields' | 'relations' | 'data' | 'pages' | 'web-api';
}

/**
 * Renders the entity admin sub-navigation bar with tab-style links.
 */
function EntitySubNav({ entityId, activeTab }: EntitySubNavProps): React.JSX.Element {
  const tabs = [
    { id: 'details', label: 'Details', to: `/entities/${entityId}` },
    { id: 'fields', label: 'Fields', to: `/entities/${entityId}/fields` },
    { id: 'relations', label: 'Relations', to: `/entities/${entityId}/relations` },
    { id: 'data', label: 'Data', to: `/entities/${entityId}/data` },
    { id: 'pages', label: 'Pages', to: `/entities/${entityId}/pages` },
    { id: 'web-api', label: 'Web API', to: `/entities/${entityId}/web-api` },
  ] as const;

  return (
    <nav aria-label="Entity administration" className="mb-6 border-b border-gray-200">
      <ul className="flex flex-wrap -mb-px gap-x-1" role="tablist">
        {tabs.map((tab) => {
          const isActive = tab.id === activeTab;
          return (
            <li key={tab.id} role="presentation">
              <Link
                to={tab.to}
                role="tab"
                aria-selected={isActive}
                aria-current={isActive ? 'page' : undefined}
                className={
                  isActive
                    ? 'inline-block rounded-t-md border-b-2 border-blue-600 px-4 py-2.5 text-sm font-semibold text-blue-600'
                    : 'inline-block rounded-t-md border-b-2 border-transparent px-4 py-2.5 text-sm font-medium text-gray-500 hover:border-gray-300 hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600'
                }
              >
                {tab.label}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

// ---------------------------------------------------------------------------
// API Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches entity metadata for the sub-navigation header context and
 * resolving origin/target entity names when the relation doesn't
 * include them. GET /v1/entity-management/entities/:entityId
 */
function useEntity(entityId: string | undefined) {
  return useQuery<ApiResponse<Entity>>({
    queryKey: ['entity', entityId],
    queryFn: () => get<Entity>(`${ENTITY_BASE}/${entityId}`),
    enabled: !!entityId,
  });
}

/**
 * Fetches relation data for pre-populating the edit form.
 * GET /v1/entity-management/relations/:relationId
 */
function useRelation(relationId: string | undefined) {
  return useQuery<ApiResponse<EntityRelation>>({
    queryKey: ['relations', relationId],
    queryFn: () => get<EntityRelation>(`${RELATION_BASE}/${relationId}`),
    enabled: !!relationId,
  });
}

/**
 * Mutation for updating a relation's mutable properties.
 * PUT /v1/entity-management/relations/:relationId
 *
 * CRITICAL: Only sends Label, Description, IsSystem — all other fields
 * are immutable per EntityRelationManager.Update() rules.
 */
function useUpdateRelation(relationId: string | undefined) {
  const queryClient = useQueryClient();
  return useMutation<
    ApiResponse<EntityRelation>,
    { message: string; errors: Array<{ key: string; value: string; message: string }>; status: number },
    RelationUpdatePayload
  >({
    mutationFn: (data: RelationUpdatePayload) =>
      put<EntityRelation>(`${RELATION_BASE}/${relationId}`, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['relations'] });
      queryClient.invalidateQueries({ queryKey: ['relations', relationId] });
    },
  });
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * RelationManage — Page component for editing an existing entity relation.
 *
 * Route: /entities/:entityId/relations/:relationId/manage
 *
 * Pre-populates editable fields (Label, Description, IsSystem) from the
 * fetched relation data. Displays immutable fields (Name, Type, Origin,
 * Target) as read-only text. On successful update, navigates to the
 * relation details page.
 */
function RelationManage(): React.JSX.Element {
  // ── React Router hooks ──────────────────────────────────────────────
  const { entityId, relationId } = useParams<{
    entityId: string;
    relationId: string;
  }>();
  const navigate = useNavigate();

  // ── API hooks ───────────────────────────────────────────────────────
  const entityQuery = useEntity(entityId);
  const relationQuery = useRelation(relationId);
  const updateMutation = useUpdateRelation(relationId);

  // ── Form state (mutable fields only) ────────────────────────────────
  const [form, setForm] = useState<RelationForm>({
    label: '',
    description: '',
    isSystem: false,
  });
  const [serverValidation, setServerValidation] = useState<FormValidation | null>(null);

  // ── Pre-populate form when relation data arrives ────────────────────
  // Mirrors relation-manage.cshtml.cs OnGet(): Label = Relation.Label,
  // Description = Relation.Description
  useEffect(() => {
    if (relationQuery.data?.object) {
      const rel = relationQuery.data.object;
      setForm({
        label: rel.label ?? '',
        description: rel.description ?? '',
        isSystem: rel.system ?? false,
      });
    }
  }, [relationQuery.data]);

  // ── Handlers ────────────────────────────────────────────────────────

  /**
   * Handle form submission — only sends mutable fields (Label, Description,
   * IsSystem) to preserve immutable relation properties per
   * EntityRelationManager.Update() rules.
   */
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setServerValidation(null);

      const payload: RelationUpdatePayload = {
        label: form.label,
        description: form.description,
        system: form.isSystem,
      };

      updateMutation.mutate(payload, {
        onSuccess: () => {
          // Navigate to relation details page on successful update
          // Mirrors relation-manage.cshtml.cs OnPost() redirect
          navigate(`/entities/${entityId}/relations/${relationId}`);
        },
        onError: (err) => {
          const sv: FormValidation = {
            message: err.message || 'Failed to update relation.',
            errors:
              err.errors?.map((item) => ({
                propertyName: item.key || 'general',
                message: item.message,
              })) ?? [],
          };
          setServerValidation(sv);
        },
      });
    },
    [form, updateMutation, navigate, entityId, relationId],
  );

  // ── Derived state ───────────────────────────────────────────────────
  const isLoading = entityQuery.isLoading || relationQuery.isLoading;
  const isError = entityQuery.isError || relationQuery.isError;
  const isSaving = updateMutation.isPending;
  const relation = relationQuery.data?.object;
  const entity = entityQuery.data?.object;

  // ── Loading state ───────────────────────────────────────────────────
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[24rem]">
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-e-transparent"
            role="status"
            aria-label="Loading relation data"
          />
          <p className="text-sm text-gray-500">Loading relation…</p>
        </div>
      </div>
    );
  }

  // ── Error state ─────────────────────────────────────────────────────
  if (isError || !relation) {
    const errorMessage =
      relationQuery.error instanceof Error
        ? relationQuery.error.message
        : entityQuery.error instanceof Error
          ? entityQuery.error.message
          : 'An unexpected error occurred while loading the relation.';

    return (
      <div className="mx-auto max-w-4xl px-4 py-8">
        <div
          className="rounded-md border border-red-300 bg-red-50 p-4 text-sm text-red-800"
          role="alert"
        >
          <p className="font-semibold">Error loading relation</p>
          <p className="mt-1">{errorMessage}</p>
          <Link
            to={entityId ? `/entities/${entityId}/relations` : '/entities'}
            className="mt-3 inline-flex items-center rounded-md bg-red-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            Back to relations
          </Link>
        </div>
      </div>
    );
  }

  // ── Computed display values for immutable fields ────────────────────
  const relationTypeName = getRelationTypeName(relation.relationType);
  const originDisplay = `${relation.originEntityName || 'Unknown Entity'}.${relation.originFieldName || 'Unknown Field'}`;
  const targetDisplay = `${relation.targetEntityName || 'Unknown Entity'}.${relation.targetFieldName || 'Unknown Field'}`;

  // ── Main render ─────────────────────────────────────────────────────
  return (
    <div className="mx-auto max-w-4xl px-4 py-6">
      {/* Entity admin sub-navigation */}
      {entityId && (
        <EntitySubNav entityId={entityId} activeTab="relations" />
      )}

      {/* Page header with actions */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">
            Edit Relation
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            {entity?.name ? `Entity: ${entity.name}` : ''}{' '}
            {relation.name ? `/ Relation: ${relation.name}` : ''}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Link
            to={`/entities/${entityId}/relations/${relationId}`}
            className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            Cancel
          </Link>
          <button
            type="submit"
            form="relation-manage-form"
            disabled={isSaving}
            className="inline-flex items-center rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isSaving ? 'Saving…' : 'Save Relation'}
          </button>
        </div>
      </div>

      {/* Edit form */}
      <DynamicForm
        id="relation-manage-form"
        name="ManageRecord"
        method="post"
        labelMode="stacked"
        fieldMode="form"
        showValidation={serverValidation !== null}
        validation={serverValidation ?? undefined}
        onSubmit={handleSubmit}
        className="space-y-6"
      >
        {/* ── Immutable Fields Section (Read-Only Display) ──────────── */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Relation Properties
            <span className="ml-2 text-xs font-normal text-gray-400">
              (read-only)
            </span>
          </legend>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Name — immutable */}
            <div>
              <label className="block text-sm font-medium text-gray-700">
                Name
              </label>
              <p className="mt-1 rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-900">
                {relation.name}
              </p>
            </div>

            {/* Type — immutable */}
            <div>
              <label className="block text-sm font-medium text-gray-700">
                Type
              </label>
              <p className="mt-1 rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-900">
                <span className="inline-flex items-center rounded-full bg-blue-100 px-2.5 py-0.5 text-xs font-medium text-blue-800">
                  {relationTypeName}
                </span>
              </p>
            </div>

            {/* Origin Entity + Field — immutable */}
            <div>
              <label className="block text-sm font-medium text-gray-700">
                Origin
              </label>
              <p className="mt-1 rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-900">
                {originDisplay}
              </p>
            </div>

            {/* Target Entity + Field — immutable */}
            <div>
              <label className="block text-sm font-medium text-gray-700">
                Target
              </label>
              <p className="mt-1 rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-900">
                {targetDisplay}
              </p>
            </div>
          </div>
        </fieldset>

        {/* ── Editable Fields Section ──────────────────────────────── */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Editable Properties
          </legend>

          <div className="space-y-4">
            {/* Label — editable text input */}
            <div>
              <label
                htmlFor="relation-label"
                className="block text-sm font-medium text-gray-700"
              >
                Label
              </label>
              <input
                id="relation-label"
                type="text"
                value={form.label}
                onChange={(e) =>
                  setForm((prev) => ({ ...prev, label: e.target.value }))
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                  getFieldError(serverValidation?.errors ?? [], 'label')
                    ? 'border-red-300 text-red-900 placeholder:text-red-300'
                    : 'border-gray-300 text-gray-900 placeholder:text-gray-400'
                }`}
                placeholder="Enter relation label"
                autoComplete="off"
              />
              {getFieldError(serverValidation?.errors ?? [], 'label') && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {getFieldError(serverValidation?.errors ?? [], 'label')}
                </p>
              )}
            </div>

            {/* Description — editable textarea */}
            <div>
              <label
                htmlFor="relation-description"
                className="block text-sm font-medium text-gray-700"
              >
                Description
              </label>
              <textarea
                id="relation-description"
                rows={3}
                value={form.description}
                onChange={(e) =>
                  setForm((prev) => ({ ...prev, description: e.target.value }))
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                  getFieldError(serverValidation?.errors ?? [], 'description')
                    ? 'border-red-300 text-red-900 placeholder:text-red-300'
                    : 'border-gray-300 text-gray-900 placeholder:text-gray-400'
                }`}
                placeholder="Enter relation description"
              />
              {getFieldError(serverValidation?.errors ?? [], 'description') && (
                <p className="mt-1 text-sm text-red-600" role="alert">
                  {getFieldError(serverValidation?.errors ?? [], 'description')}
                </p>
              )}
            </div>

            {/* IsSystem — editable checkbox */}
            <div className="flex items-start gap-3">
              <div className="flex h-6 items-center">
                <input
                  id="relation-is-system"
                  type="checkbox"
                  checked={form.isSystem}
                  onChange={(e) =>
                    setForm((prev) => ({ ...prev, isSystem: e.target.checked }))
                  }
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                />
              </div>
              <div>
                <label
                  htmlFor="relation-is-system"
                  className="text-sm font-medium text-gray-700"
                >
                  System Relation
                </label>
                <p className="text-xs text-gray-500">
                  System relations are managed by the platform and are typically
                  not user-editable.
                </p>
              </div>
            </div>
          </div>
        </fieldset>

        {/* ── Relation Identifier (for reference) ──────────────────── */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Identifier
          </legend>
          <div>
            <label className="block text-sm font-medium text-gray-700">
              Relation ID
            </label>
            <p className="mt-1 font-mono text-xs text-gray-500 select-all">
              {relation.id}
            </p>
          </div>
        </fieldset>
      </DynamicForm>
    </div>
  );
}

export default RelationManage;
