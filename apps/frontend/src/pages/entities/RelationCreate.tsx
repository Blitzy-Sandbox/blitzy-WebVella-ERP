/**
 * RelationCreate.tsx — Create Entity Relation Page
 *
 * Replaces WebVella.Erp.Plugins.SDK/Pages/entity/relation-create.cshtml[.cs]
 * Route: /entities/:entityId/relations/create
 *
 * Provides a form to create a new entity relation with:
 * - Name, Label, Description, IsSystem fields
 * - Relation type selector (OneToOne, OneToMany, ManyToMany)
 * - Dynamic origin entity+field selector (GuidField only, unique + required)
 * - Dynamic target entity+field selector (GuidField only, non-unique, non-required)
 * - Field constraint filtering matching monolith business rules
 */

import { useState, useMemo, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  useEntities,
  useEntity,
  useRelations,
  useCreateRelation,
} from '../../hooks/useEntities';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation } from '../../components/forms/DynamicForm';
import type { Entity, EntityRelation, Field } from '../../types/entity';
import { EntityRelationType, FieldType } from '../../types/entity';

/* ------------------------------------------------------------------ */
/* Helper types and constants                                          */
/* ------------------------------------------------------------------ */

/** Select option for origin/target entity+field dropdowns */
interface FieldSelectOption {
  /** Combined key: "{entityId}${fieldId}" matching monolith format */
  value: string;
  /** Display text: "{entityName}.{fieldName}" */
  label: string;
}

/** Relation type dropdown options — excludes internal-only value "1" from
 *  the monolith's old enum while correctly mapping to the new TS enum. */
const RELATION_TYPE_OPTIONS: ReadonlyArray<{
  value: number;
  label: string;
}> = [
  { value: EntityRelationType.OneToOne, label: 'One to One' },
  { value: EntityRelationType.OneToMany, label: 'One to Many' },
  { value: EntityRelationType.ManyToMany, label: 'Many to Many' },
];

/** Entity admin sub-nav tabs shared across entity detail pages. */
const ENTITY_ADMIN_TABS: ReadonlyArray<{
  key: string;
  label: string;
  pathSuffix: string;
}> = [
  { key: 'details', label: 'Details', pathSuffix: '' },
  { key: 'fields', label: 'Fields', pathSuffix: '/fields' },
  { key: 'relations', label: 'Relations', pathSuffix: '/relations' },
];

/* ------------------------------------------------------------------ */
/* Helper: check if a field is already targeted by a non-M:N relation */
/* ------------------------------------------------------------------ */
function isFieldAlreadyTargeted(
  fieldId: string,
  relations: EntityRelation[],
): boolean {
  return relations.some(
    (r) =>
      r.targetFieldId === fieldId &&
      r.relationType !== EntityRelationType.ManyToMany,
  );
}

/* ------------------------------------------------------------------ */
/* RelationCreate component                                            */
/* ------------------------------------------------------------------ */

function RelationCreate() {
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  /* ---------------------------------------------------------------
   * Data fetching via TanStack Query hooks
   * --------------------------------------------------------------- */
  const {
    data: entity,
    isLoading: entityLoading,
    isError: entityError,
  } = useEntity(entityId ?? '');

  const { data: allEntities, isLoading: entitiesLoading } = useEntities();

  const { data: allRelations, isLoading: relationsLoading } = useRelations();

  const createRelation = useCreateRelation();

  /* ---------------------------------------------------------------
   * Form state — mirrors monolith BindProperty fields
   * --------------------------------------------------------------- */
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
  const [description, setDescription] = useState('');
  const [isSystem, setIsSystem] = useState(false);
  const [relationType, setRelationType] = useState<number>(
    EntityRelationType.OneToOne,
  );
  const [origin, setOrigin] = useState('');
  const [target, setTarget] = useState('');
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* ---------------------------------------------------------------
   * Compute origin field options.
   *
   * Monolith constraints (relation-create.cshtml.cs PageInit):
   *  - Field type MUST be GuidField (field is GuidField)
   *  - Field MUST be unique AND required
   *  - Field must NOT already be targeted by a 1:1 or 1:N relation
   * --------------------------------------------------------------- */
  const originOptions = useMemo<FieldSelectOption[]>(() => {
    if (!allEntities || !allRelations) return [];

    const options: FieldSelectOption[] = [];

    for (const ent of allEntities) {
      const fields = ent.fields;
      if (!fields) continue;

      for (const field of fields) {
        /* Only GuidField types */
        if (field.fieldType !== FieldType.GuidField) continue;

        /* Must be unique AND required */
        if (!field.unique || !field.required) continue;

        /* Must not already be targeted by OneToOne or OneToMany relation */
        if (isFieldAlreadyTargeted(field.id, allRelations)) continue;

        options.push({
          value: `${ent.id}$${field.id}`,
          label: `${ent.name}.${field.name}`,
        });
      }
    }

    return options.sort((a, b) => a.label.localeCompare(b.label));
  }, [allEntities, allRelations]);

  /* ---------------------------------------------------------------
   * Compute target field options.
   *
   * Monolith constraints (relation-create.cshtml.cs PageInit):
   *  - Field type MUST be GuidField
   *  - Field must NOT be unique AND must NOT be required
   *  - Field must NOT already be targeted by a 1:1 or 1:N relation
   * --------------------------------------------------------------- */
  const targetOptions = useMemo<FieldSelectOption[]>(() => {
    if (!allEntities || !allRelations) return [];

    const options: FieldSelectOption[] = [];

    for (const ent of allEntities) {
      const fields = ent.fields;
      if (!fields) continue;

      for (const field of fields) {
        /* Only GuidField types */
        if (field.fieldType !== FieldType.GuidField) continue;

        /* Must NOT be unique AND must NOT be required */
        if (field.unique || field.required) continue;

        /* Must not already be targeted by OneToOne or OneToMany relation */
        if (isFieldAlreadyTargeted(field.id, allRelations)) continue;

        options.push({
          value: `${ent.id}$${field.id}`,
          label: `${ent.name}.${field.name}`,
        });
      }
    }

    return options.sort((a, b) => a.label.localeCompare(b.label));
  }, [allEntities, allRelations]);

  /* ---------------------------------------------------------------
   * Form submission handler.
   *
   * Mirrors monolith OnPost():
   *  1. Parse Origin/Target combined keys by splitting on "$"
   *  2. Build EntityRelation payload
   *  3. Create via API
   *  4. Navigate to relation details on success
   *  5. Display validation errors on failure
   * --------------------------------------------------------------- */
  const handleSubmit = useCallback(async () => {
    /* Prevent re-entry while mutation is in-flight */
    if (createRelation.isPending) return;

    setValidation(undefined);

    /* --- Client-side validation --- */
    const errors: Array<{ propertyName: string; message: string }> = [];

    if (!name.trim()) {
      errors.push({ propertyName: 'name', message: 'Name is required.' });
    }
    if (!origin || !origin.includes('$')) {
      errors.push({
        propertyName: 'origin',
        message: 'Please select an origin entity and field.',
      });
    }
    if (!target || !target.includes('$')) {
      errors.push({
        propertyName: 'target',
        message: 'Please select a target entity and field.',
      });
    }

    if (errors.length > 0) {
      setValidation({
        message: 'Please correct the following errors:',
        errors,
      });
      return;
    }

    /* --- Parse combined keys (format: "entityId$fieldId") --- */
    const [originEntityId, originFieldId] = origin.split('$');
    const [targetEntityId, targetFieldId] = target.split('$');

    /* --- Build relation payload --- */
    const relationId = crypto.randomUUID();

    const relationPayload = {
      id: relationId,
      name: name.trim(),
      /* Label defaults to name if not provided (matches monolith convenience) */
      label: label.trim() || name.trim(),
      description: description.trim(),
      system: isSystem,
      relationType: relationType as EntityRelationType,
      originEntityId,
      originFieldId,
      targetEntityId,
      targetFieldId,
    } as EntityRelation;

    try {
      await createRelation.mutateAsync(relationPayload);
      /* Navigate to the newly created relation's details page */
      navigate(`/entities/${entityId}/relations/${relationId}`);
    } catch (err: unknown) {
      /* Handle API validation errors */
      if (err && typeof err === 'object' && 'errors' in err) {
        const apiErr = err as {
          message?: string;
          errors?: Array<{ propertyName: string; message: string }>;
        };
        setValidation({
          message: apiErr.message || 'Validation failed.',
          errors: apiErr.errors ?? [],
        });
      } else if (err instanceof Error) {
        setValidation({
          message: err.message,
          errors: [],
        });
      } else {
        setValidation({
          message: 'An unexpected error occurred while creating the relation.',
          errors: [],
        });
      }
    }
  }, [
    name,
    label,
    description,
    isSystem,
    relationType,
    origin,
    target,
    entityId,
    navigate,
    createRelation,
  ]);

  /* ---------------------------------------------------------------
   * Derived loading state
   * --------------------------------------------------------------- */
  const isLoading = entityLoading || entitiesLoading || relationsLoading;

  /* ---------------------------------------------------------------
   * Loading state
   * --------------------------------------------------------------- */
  if (isLoading) {
    return (
      <div
        className="flex items-center justify-center min-h-[50vh]"
        role="status"
        aria-label="Loading relation data"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">
            Loading relation data&hellip;
          </span>
        </div>
      </div>
    );
  }

  /* ---------------------------------------------------------------
   * Error / entity-not-found state
   * --------------------------------------------------------------- */
  if (entityError || !entity) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-center">
        <h2 className="text-lg font-semibold text-red-800">
          Entity Not Found
        </h2>
        <p className="mt-2 text-sm text-red-600">
          The requested entity could not be loaded. It may have been deleted or
          you may not have permission to view it.
        </p>
        <Link
          to="/entities"
          className="mt-4 inline-block rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
        >
          Back to Entities
        </Link>
      </div>
    );
  }

  /* ---------------------------------------------------------------
   * Render
   * --------------------------------------------------------------- */
  return (
    <div className="space-y-6">
      {/* ========== Entity Admin Sub-Navigation ========== */}
      <nav aria-label="Entity administration" className="border-b border-gray-200">
        <ul className="flex gap-0 -mb-px" role="tablist">
          {ENTITY_ADMIN_TABS.map((tab) => {
            const isActive = tab.key === 'relations';
            const to = `/entities/${entityId}${tab.pathSuffix}`;
            return (
              <li key={tab.key} role="presentation">
                <Link
                  to={to}
                  role="tab"
                  aria-selected={isActive}
                  aria-current={isActive ? 'page' : undefined}
                  className={[
                    'inline-block px-4 py-3 text-sm font-medium border-b-2 transition-colors',
                    isActive
                      ? 'border-blue-600 text-blue-600'
                      : 'border-transparent text-gray-500 hover:border-gray-300 hover:text-gray-700',
                  ].join(' ')}
                >
                  {tab.label}
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>

      {/* ========== Page Header ========== */}
      <header className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <div
            className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg"
            style={{ backgroundColor: entity.color || '#dc3545' }}
            aria-hidden="true"
          >
            <svg
              className="h-5 w-5 text-white"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1"
              />
            </svg>
          </div>
          <div>
            <h1 className="text-xl font-semibold text-gray-900">
              Create Relation
            </h1>
            <p className="text-sm text-gray-500">
              Entity: {entity.label || entity.name}
            </p>
          </div>
        </div>

        {/* Header actions */}
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={handleSubmit}
            disabled={createRelation.isPending}
            className="inline-flex items-center gap-2 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-green-600 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {createRelation.isPending ? (
              <>
                <span
                  className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                  aria-hidden="true"
                />
                Creating&hellip;
              </>
            ) : (
              <>
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
                    d="M5 13l4 4L19 7"
                  />
                </svg>
                Create Relation
              </>
            )}
          </button>
          <Link
            to={`/entities/${entityId}/relations`}
            className="inline-flex items-center gap-2 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-600"
          >
            Cancel
          </Link>
        </div>
      </header>

      {/* ========== Form ========== */}
      <DynamicForm
        labelMode="stacked"
        validation={validation}
        onSubmit={handleSubmit}
      >
        <div className="space-y-6 rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          {/* --- Name (required) --- */}
          <div>
            <label
              htmlFor="relation-name"
              className="block text-sm font-medium text-gray-700"
            >
              Name{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="relation-name"
              type="text"
              required
              autoComplete="off"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. account_contact"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none"
              aria-required="true"
              aria-describedby="relation-name-help"
            />
            <p id="relation-name-help" className="mt-1 text-xs text-gray-500">
              Unique relation identifier. Use lowercase with underscores.
            </p>
          </div>

          {/* --- Label --- */}
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
              autoComplete="off"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              placeholder="e.g. Account – Contact"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none"
              aria-describedby="relation-label-help"
            />
            <p
              id="relation-label-help"
              className="mt-1 text-xs text-gray-500"
            >
              Human-readable label. Defaults to the name if left empty.
            </p>
          </div>

          {/* --- Description --- */}
          <div>
            <label
              htmlFor="relation-description"
              className="block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <textarea
              id="relation-description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={3}
              placeholder="Describe the purpose of this relation…"
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none resize-y"
            />
          </div>

          {/* --- IsSystem checkbox --- */}
          <div className="flex items-start gap-3">
            <input
              id="relation-system"
              type="checkbox"
              checked={isSystem}
              onChange={(e) => setIsSystem(e.target.checked)}
              className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            <div>
              <label
                htmlFor="relation-system"
                className="text-sm font-medium text-gray-700"
              >
                System Relation
              </label>
              <p className="text-xs text-gray-500">
                System relations cannot be deleted. Use for core schema
                relations.
              </p>
            </div>
          </div>

          {/* --- Relation Type --- */}
          <div>
            <label
              htmlFor="relation-type"
              className="block text-sm font-medium text-gray-700"
            >
              Relation Type
            </label>
            <select
              id="relation-type"
              value={relationType}
              onChange={(e) => setRelationType(Number(e.target.value))}
              className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none"
            >
              {RELATION_TYPE_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </div>

          {/* --- Origin Entity+Field --- */}
          <div>
            <label
              htmlFor="relation-origin"
              className="block text-sm font-medium text-gray-700"
            >
              Origin{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <select
              id="relation-origin"
              value={origin}
              onChange={(e) => setOrigin(e.target.value)}
              required
              className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none"
              aria-required="true"
              aria-describedby="relation-origin-help"
            >
              <option value="">— Select origin entity.field —</option>
              {originOptions.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
            <p
              id="relation-origin-help"
              className="mt-1 text-xs text-gray-500"
            >
              Origin field must be a unique, required GUID field not already
              targeted by another relation.
              {originOptions.length === 0 && (
                <span className="block mt-1 font-medium text-amber-600">
                  No eligible origin fields found. Create a unique, required
                  GUID field first.
                </span>
              )}
            </p>
          </div>

          {/* --- Target Entity+Field --- */}
          <div>
            <label
              htmlFor="relation-target"
              className="block text-sm font-medium text-gray-700"
            >
              Target{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <select
              id="relation-target"
              value={target}
              onChange={(e) => setTarget(e.target.value)}
              required
              className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500 focus-visible:outline-none"
              aria-required="true"
              aria-describedby="relation-target-help"
            >
              <option value="">— Select target entity.field —</option>
              {targetOptions.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
            <p
              id="relation-target-help"
              className="mt-1 text-xs text-gray-500"
            >
              Target field must be a non-unique, non-required GUID field not
              already targeted by another relation.
              {targetOptions.length === 0 && (
                <span className="block mt-1 font-medium text-amber-600">
                  No eligible target fields found. Create a non-unique,
                  non-required GUID field first.
                </span>
              )}
            </p>
          </div>
        </div>
      </DynamicForm>
    </div>
  );
}

export default RelationCreate;
