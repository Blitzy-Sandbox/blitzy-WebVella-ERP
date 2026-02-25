import { useState, useMemo, useCallback, useEffect } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import {
  useEntity,
  useEntities,
  useRelations,
  useCreateRelation,
} from '../../hooks/useEntities';
import {
  EntityRelationType,
  FieldType,
} from '../../types/entity';
import type {
  Entity,
  EntityRelation,
  Field,
  AnyField,
} from '../../types/entity';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation } from '../../components/forms/DynamicForm';

/**
 * Represents a selectable origin or target option combining entity and field.
 * Value format mirrors the monolith's "${entityId}$${fieldId}" composite key.
 */
interface FieldOption {
  /** Composite value: "entityId$fieldId" */
  value: string;
  /** Display label: "entityName.fieldName" */
  label: string;
  entityId: string;
  fieldId: string;
}

/** Entity admin sub-navigation tab definition. */
interface SubNavTab {
  id: string;
  label: string;
  href: string;
}

/**
 * AdminEntityRelationCreate — Create a new entity relation.
 *
 * Route: /admin/entities/:entityId/relations/create
 *
 * Replaces `WebVella.Erp.Plugins.SDK/Pages/entity/relation-create.cshtml[.cs]`.
 * Provides a form with relation type selector, name, label, description,
 * system flag, and dynamic origin/target entity+field selectors.
 *
 * Origin fields: GUID fields that are both unique AND required (across all entities).
 * Target fields: GUID fields NOT already targeted by a non-ManyToMany relation.
 * Relation type: OneToOne is excluded; only OneToMany and ManyToMany are offered.
 */
function AdminEntityRelationCreate(): React.JSX.Element {
  const { entityId } = useParams<{ entityId: string }>();
  const navigate = useNavigate();

  /* ------------------------------------------------------------------ */
  /*  Data fetching                                                      */
  /* ------------------------------------------------------------------ */
  const {
    data: currentEntity,
    isLoading: entityLoading,
    isError: entityError,
  } = useEntity(entityId ?? '');

  const { data: allEntities, isLoading: entitiesLoading } = useEntities();
  const { data: allRelations, isLoading: relationsLoading } = useRelations();
  const createRelationMutation = useCreateRelation();

  /* ------------------------------------------------------------------ */
  /*  Form state                                                         */
  /* ------------------------------------------------------------------ */
  const [name, setName] = useState('');
  const [label, setLabel] = useState('');
  const [description, setDescription] = useState('');
  const [isSystem, setIsSystem] = useState(false);
  const [relationType, setRelationType] = useState<number>(
    EntityRelationType.OneToMany,
  );
  const [origin, setOrigin] = useState('');
  const [target, setTarget] = useState('');
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* Reset form whenever the parent entity changes (e.g. React Router reuse). */
  useEffect(() => {
    setName('');
    setLabel('');
    setDescription('');
    setIsSystem(false);
    setRelationType(EntityRelationType.OneToMany);
    setOrigin('');
    setTarget('');
    setValidation(undefined);
  }, [entityId]);

  /* ------------------------------------------------------------------ */
  /*  Relation type options                                              */
  /* ------------------------------------------------------------------ */
  /**
   * The source excludes OneToOne (value 1) from TypeOptions.
   * We build all three, then filter out OneToOne to match that behaviour.
   */
  const typeOptions = useMemo(() => {
    const all = [
      {
        value: EntityRelationType.OneToOne,
        label: 'One to One (1:1)',
      },
      {
        value: EntityRelationType.OneToMany,
        label: 'One to Many (1:N)',
      },
      {
        value: EntityRelationType.ManyToMany,
        label: 'Many to Many (N:N)',
      },
    ];
    return all.filter((opt) => opt.value !== EntityRelationType.OneToOne);
  }, []);

  /* ------------------------------------------------------------------ */
  /*  Origin field options                                               */
  /* ------------------------------------------------------------------ */
  /**
   * Iterate ALL entities' fields. Keep only GuidField type where both
   * `unique` and `required` are true.
   * Matches relation-create.cshtml.cs PageInit() lines 53-72.
   */
  const originOptions = useMemo<FieldOption[]>(() => {
    if (!allEntities) return [];
    const options: FieldOption[] = [];

    for (const entity of allEntities) {
      const fields: Field[] = entity.fields ?? [];
      for (const field of fields) {
        if (
          field.fieldType === FieldType.GuidField &&
          field.unique &&
          field.required
        ) {
          options.push({
            value: `${entity.id}$${field.id}`,
            label: `${entity.name}.${field.name}`,
            entityId: entity.id,
            fieldId: field.id,
          });
        }
      }
    }

    return options;
  }, [allEntities]);

  /* ------------------------------------------------------------------ */
  /*  Target field options                                               */
  /* ------------------------------------------------------------------ */
  /**
   * Iterate ALL entities' fields. Keep only GuidField type where the field
   * is NOT already targeted by a non-ManyToMany relation.
   * Matches relation-create.cshtml.cs PageInit() lines 75-98.
   */
  const targetOptions = useMemo<FieldOption[]>(() => {
    if (!allEntities) return [];
    const relations = allRelations ?? [];
    const options: FieldOption[] = [];

    for (const entity of allEntities) {
      const fields: Field[] = entity.fields ?? [];
      for (const field of fields) {
        if (field.fieldType === FieldType.GuidField) {
          /* AnyField-compatible check: narrow via fieldType discriminator */
          const anyField = field as AnyField;
          const isAlreadyTargeted = relations.some(
            (rel: EntityRelation) =>
              rel.targetFieldId === anyField.id &&
              rel.relationType !== EntityRelationType.ManyToMany,
          );
          if (!isAlreadyTargeted) {
            options.push({
              value: `${entity.id}$${field.id}`,
              label: `${entity.name}.${field.name}`,
              entityId: entity.id,
              fieldId: field.id,
            });
          }
        }
      }
    }

    return options;
  }, [allEntities, allRelations]);

  /* ------------------------------------------------------------------ */
  /*  Form submission                                                    */
  /* ------------------------------------------------------------------ */
  const handleSubmit = useCallback(
    async (e: React.FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setValidation(undefined);

      /* ---------- Client-side validation ---------- */
      const errors: { propertyName: string; message: string }[] = [];

      if (!name.trim()) {
        errors.push({ propertyName: 'name', message: 'Name is required.' });
      }
      if (!origin) {
        errors.push({
          propertyName: 'origin',
          message: 'Origin field is required.',
        });
      }
      if (!target) {
        errors.push({
          propertyName: 'target',
          message: 'Target field is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({
          message: 'Please correct the errors below.',
          errors,
        });
        return;
      }

      /* ---------- Parse composite selectors ---------- */
      const originParts = origin.split('$');
      const targetParts = target.split('$');

      if (originParts.length !== 2 || targetParts.length !== 2) {
        setValidation({
          message: 'Invalid origin or target selection.',
          errors: [
            {
              propertyName: 'origin',
              message: 'Selection format is invalid.',
            },
          ],
        });
        return;
      }

      /* ---------- Build EntityRelation payload ---------- */
      const trimmedName = name.trim();

      /* Resolve entity/field names for the payload from loaded entities. */
      const originEntity = allEntities?.find(
        (e: Entity) => e.id === originParts[0],
      );
      const originField = originEntity?.fields?.find(
        (f: Field) => f.id === originParts[1],
      );
      const targetEntity = allEntities?.find(
        (e: Entity) => e.id === targetParts[0],
      );
      const targetField = targetEntity?.fields?.find(
        (f: Field) => f.id === targetParts[1],
      );

      const newRelation: EntityRelation = {
        id: crypto.randomUUID(),
        name: trimmedName,
        /* Label defaults to Name when left blank (matching monolith behaviour). */
        label: label.trim() || trimmedName,
        description: description.trim(),
        system: isSystem,
        relationType: relationType as EntityRelation['relationType'],
        originEntityId: originParts[0],
        originFieldId: originParts[1],
        targetEntityId: targetParts[0],
        targetFieldId: targetParts[1],
        originEntityName: originEntity?.name ?? '',
        originFieldName: originField?.name ?? '',
        targetEntityName: targetEntity?.name ?? '',
        targetFieldName: targetField?.name ?? '',
      };

      /* ---------- Execute mutation ---------- */
      try {
        await createRelationMutation.mutateAsync(newRelation);
        navigate(`/admin/entities/${entityId}/relations`);
      } catch (err: unknown) {
        const errorMessage =
          err instanceof Error
            ? err.message
            : 'An unexpected error occurred while creating the relation.';
        setValidation({
          message: errorMessage,
          errors: [{ propertyName: '', message: errorMessage }],
        });
      }
    },
    [
      name,
      label,
      description,
      isSystem,
      relationType,
      origin,
      target,
      entityId,
      navigate,
      createRelationMutation,
      allEntities,
    ],
  );

  /* ------------------------------------------------------------------ */
  /*  Derived state                                                      */
  /* ------------------------------------------------------------------ */
  const isDataLoading = entityLoading || entitiesLoading || relationsLoading;

  /** Entity admin sub-navigation tabs — "Relations" is the active tab. */
  const entitySubNavTabs = useMemo<SubNavTab[]>(
    () => [
      { id: 'details', label: 'Details', href: `/admin/entities/${entityId}` },
      {
        id: 'fields',
        label: 'Fields',
        href: `/admin/entities/${entityId}/fields`,
      },
      {
        id: 'relations',
        label: 'Relations',
        href: `/admin/entities/${entityId}/relations`,
      },
      {
        id: 'data',
        label: 'Data',
        href: `/admin/entities/${entityId}/data`,
      },
      {
        id: 'pages',
        label: 'Pages',
        href: `/admin/entities/${entityId}/pages`,
      },
      {
        id: 'web-api',
        label: 'Web API',
        href: `/admin/entities/${entityId}/web-api`,
      },
    ],
    [entityId],
  );

  /* ------------------------------------------------------------------ */
  /*  Error state                                                        */
  /* ------------------------------------------------------------------ */
  if (entityError) {
    return (
      <div className="p-6">
        <div className="rounded-md bg-red-50 p-4" role="alert">
          <p className="text-sm text-red-700">
            Failed to load entity data. Please try again later.
          </p>
          <Link
            to="/admin/entities"
            className="mt-2 inline-block text-sm font-medium text-red-800 underline hover:text-red-900"
          >
            Back to Entities
          </Link>
        </div>
      </div>
    );
  }

  /* ------------------------------------------------------------------ */
  /*  Render                                                             */
  /* ------------------------------------------------------------------ */
  return (
    <div className="flex flex-col min-h-full">
      {/* ---- Page Header ---- */}
      <header
        className="flex items-center justify-between px-6 py-4"
        style={{
          backgroundColor: currentEntity?.color ?? '#dc3545',
        }}
      >
        <div className="flex items-center gap-3">
          {currentEntity?.iconName && (
            <span
              className="inline-flex items-center justify-center h-8 w-8 rounded text-white"
              aria-hidden="true"
            >
              <i className={`fa ${currentEntity.iconName}`} />
            </span>
          )}
          <div>
            <h1 className="text-lg font-semibold text-white leading-tight">
              {currentEntity?.label ?? currentEntity?.name ?? 'Entity'}
            </h1>
            <span className="text-sm text-white/80">Create Relation</span>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Link
            to={`/admin/entities/${entityId}/relations`}
            className="inline-flex items-center gap-1.5 rounded px-3 py-1.5 text-sm font-medium text-white bg-white/20 hover:bg-white/30 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white transition-colors"
          >
            Cancel
          </Link>
        </div>
      </header>

      {/* ---- Entity Admin Sub-Navigation ---- */}
      <nav
        className="border-b border-gray-200 bg-white px-6"
        aria-label="Entity admin navigation"
      >
        <ul className="flex gap-0 -mb-px" role="tablist">
          {entitySubNavTabs.map((tab) => (
            <li key={tab.id} role="presentation">
              <Link
                to={tab.href}
                role="tab"
                aria-selected={tab.id === 'relations'}
                className={`inline-block px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
                  tab.id === 'relations'
                    ? 'border-blue-600 text-blue-600'
                    : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                }`}
              >
                {tab.label}
              </Link>
            </li>
          ))}
        </ul>
      </nav>

      {/* ---- Main Content ---- */}
      <main className="flex-1 p-6">
        {isDataLoading ? (
          <div
            className="flex items-center justify-center py-12"
            aria-live="polite"
            aria-busy="true"
          >
            <div
              className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"
              role="status"
            >
              <span className="sr-only">Loading entity data…</span>
            </div>
          </div>
        ) : (
          <div className="max-w-2xl">
            <DynamicForm
              id="CreateRecord"
              name="CreateRecord"
              labelMode="stacked"
              onSubmit={handleSubmit}
              validation={validation}
              showValidation={!!validation}
            >
              {/* ---- Relation Type ---- */}
              <div className="mb-4">
                <label
                  htmlFor="relationType"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Type{' '}
                  <span className="text-red-500" aria-hidden="true">
                    *
                  </span>
                </label>
                <select
                  id="relationType"
                  name="relationType"
                  value={relationType}
                  onChange={(e) => setRelationType(Number(e.target.value))}
                  className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  required
                  aria-required="true"
                >
                  {typeOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
              </div>

              {/* ---- Name ---- */}
              <div className="mb-4">
                <label
                  htmlFor="relationName"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Name{' '}
                  <span className="text-red-500" aria-hidden="true">
                    *
                  </span>
                </label>
                <input
                  type="text"
                  id="relationName"
                  name="name"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  required
                  aria-required="true"
                  aria-invalid={
                    validation?.errors.some((err) => err.propertyName === 'name')
                      ? 'true'
                      : undefined
                  }
                  placeholder="e.g. user_role"
                  autoComplete="off"
                />
              </div>

              {/* ---- Label ---- */}
              <div className="mb-4">
                <label
                  htmlFor="relationLabel"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Label
                </label>
                <input
                  type="text"
                  id="relationLabel"
                  name="label"
                  value={label}
                  onChange={(e) => setLabel(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  placeholder="Defaults to Name if left empty"
                  autoComplete="off"
                />
              </div>

              {/* ---- Description ---- */}
              <div className="mb-4">
                <label
                  htmlFor="relationDescription"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Description
                </label>
                <textarea
                  id="relationDescription"
                  name="description"
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  rows={3}
                  className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                />
              </div>

              {/* ---- Is System ---- */}
              <div className="mb-4">
                <label className="inline-flex items-center gap-2 cursor-pointer">
                  <input
                    type="checkbox"
                    id="isSystem"
                    name="isSystem"
                    checked={isSystem}
                    onChange={(e) => setIsSystem(e.target.checked)}
                    className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                  />
                  <span className="text-sm font-medium text-gray-700">
                    System
                  </span>
                </label>
              </div>

              {/* ---- Origin (entity.field) ---- */}
              <div className="mb-4">
                <label
                  htmlFor="originSelect"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Origin{' '}
                  <span className="text-red-500" aria-hidden="true">
                    *
                  </span>
                </label>
                <select
                  id="originSelect"
                  name="origin"
                  value={origin}
                  onChange={(e) => setOrigin(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  required
                  aria-required="true"
                  aria-invalid={
                    validation?.errors.some(
                      (err) => err.propertyName === 'origin',
                    )
                      ? 'true'
                      : undefined
                  }
                >
                  <option value="">-- Select Origin --</option>
                  {originOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
                <p className="mt-1 text-xs text-gray-500">
                  GUID fields that are both unique and required.
                </p>
              </div>

              {/* ---- Target (entity.field) ---- */}
              <div className="mb-4">
                <label
                  htmlFor="targetSelect"
                  className="block text-sm font-medium text-gray-700 mb-1"
                >
                  Target{' '}
                  <span className="text-red-500" aria-hidden="true">
                    *
                  </span>
                </label>
                <select
                  id="targetSelect"
                  name="target"
                  value={target}
                  onChange={(e) => setTarget(e.target.value)}
                  className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  required
                  aria-required="true"
                  aria-invalid={
                    validation?.errors.some(
                      (err) => err.propertyName === 'target',
                    )
                      ? 'true'
                      : undefined
                  }
                >
                  <option value="">-- Select Target --</option>
                  {targetOptions.map((opt) => (
                    <option key={opt.value} value={opt.value}>
                      {opt.label}
                    </option>
                  ))}
                </select>
                <p className="mt-1 text-xs text-gray-500">
                  GUID fields not already targeted by a non-ManyToMany relation.
                </p>
              </div>

              {/* ---- Action Buttons ---- */}
              <div className="flex items-center gap-3 pt-4 border-t border-gray-200">
                <button
                  type="submit"
                  disabled={createRelationMutation.isPending}
                  className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {createRelationMutation.isPending ? (
                    <>
                      <span
                        className="animate-spin inline-block h-4 w-4 border-2 border-white/30 border-t-white rounded-full"
                        aria-hidden="true"
                      />
                      Creating…
                    </>
                  ) : (
                    <>
                      <i className="fa fa-plus" aria-hidden="true" />
                      Create Relation
                    </>
                  )}
                </button>
                <Link
                  to={`/admin/entities/${entityId}/relations`}
                  className="inline-flex items-center rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 transition-colors"
                >
                  Cancel
                </Link>
              </div>
            </DynamicForm>
          </div>
        )}
      </main>
    </div>
  );
}

export default AdminEntityRelationCreate;
