/**
 * Entity Metadata CRUD TanStack Query Hooks
 *
 * TanStack Query 5 hooks for entity, field, and relation metadata CRUD
 * operations against the Entity Management microservice. Replaces:
 *
 *  - `EntityManager.cs`           — Entity/field lifecycle with validation,
 *                                    security gating, cache management
 *  - `EntityRelationManager.cs`   — Relation CRUD with immutability rules
 *  - `Cache.cs`                   — IMemoryCache wrapper for entities/relations
 *
 * Architecture:
 *  - Entity metadata is the MOST cached data in the monolith — staleTime is
 *    set to 5 minutes to match the monolith's aggressive metadata caching
 *  - Field mutations invalidate the parent entity (entity contains embedded
 *    fields in its `fields: Field[]` array)
 *  - Relation mutations invalidate both entities and relations caches because
 *    relations connect two entities and change their metadata
 *  - All validation happens server-side (name length 63 chars, uniqueness,
 *    type-specific field rules) — the frontend delegates to the service
 *  - Meta permission (admin-only) is enforced server-side via 403 responses
 *  - The 20+ field types are sent as JSON with `fieldType` discriminator;
 *    the server routes to the appropriate type handler
 *
 * Query keys:
 *  - `['entities']`               — All entities list
 *  - `['entities', idOrName]`     — Single entity by ID or name
 *  - `['relations']`              — All entity relations list
 *  - `['relations', idOrName]`    — Single relation by ID or name
 *  - `['records']`                — Records (invalidated on entity deletion)
 *
 * @module hooks/useEntities
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type {
  Entity,
  InputEntity,
  EntityRelation,
  Field,
  AnyField,
  EntityListResponse,
  EntityResponse,
  EntityRelationResponse,
  EntityRelationListResponse,
} from '../types/entity';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Keys
// ---------------------------------------------------------------------------

/**
 * Centralised query-key factory for all entity/relation metadata caches.
 * Prevents key collisions and enables targeted invalidation.
 *
 * Mirrors the monolith's Cache.cs key organisation:
 *  - `Cache.GetEntities()`            → `ENTITY_QUERY_KEYS.all`
 *  - `EntityManager.ReadEntity(id)`   → `ENTITY_QUERY_KEYS.detail(id)`
 *  - `EntityRelationManager.Read()`   → `ENTITY_QUERY_KEYS.relations`
 *  - `EntityRelationManager.Read(id)` → `ENTITY_QUERY_KEYS.relation(id)`
 */
const ENTITY_QUERY_KEYS = {
  /** All entities list — matches cache key for EntityManager.ReadEntities() */
  all: ['entities'] as const,
  /** Single entity by ID or name — matches EntityManager.ReadEntity(id/name) */
  detail: (idOrName: string) => ['entities', idOrName] as const,
  /** All entity relations — matches EntityRelationManager.Read() */
  relations: ['relations'] as const,
  /** Single relation by ID or name — matches EntityRelationManager.Read(id/name) */
  relation: (idOrName: string) => ['relations', idOrName] as const,
  /** Records key — used for cross-query invalidation when entities are deleted */
  records: ['records'] as const,
} as const;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * staleTime for entity and relation metadata queries — 5 minutes (300 000 ms).
 *
 * Matches the monolith's aggressive metadata caching strategy from Cache.cs
 * which used IMemoryCache with 1-hour absolute expiration but practically
 * refreshed on every mutation via cache clearing with lock-based refresh and
 * MD5 hash computation (`Cache.GetEntities()` / `Cache.AddEntities()`).
 *
 * TanStack Query's staleTime prevents unnecessary refetches while the
 * `onSuccess` cache invalidation ensures mutations trigger immediate refresh.
 */
const ENTITY_METADATA_STALE_TIME_MS = 5 * 60 * 1000;

// ---------------------------------------------------------------------------
// Mutation Variable Types
// ---------------------------------------------------------------------------

/** Variables for the {@link useUpdateEntity} mutation */
interface UpdateEntityVariables {
  /** Entity ID (GUID string) */
  id: string;
  /** Partial entity update payload — name, label, labelPlural, iconName, color, recordPermissions */
  entity: InputEntity;
}

/** Variables for the {@link useCreateField} mutation */
interface CreateFieldVariables {
  /** Target entity ID (GUID) that owns the field */
  entityId: string;
  /**
   * Full field definition including type-specific properties.
   * Uses the AnyField discriminated union so the `fieldType` discriminator
   * routes to the correct concrete type (TextField, NumberField, DateField, etc.).
   */
  field: AnyField;
}

/** Variables for the {@link useUpdateField} mutation */
interface UpdateFieldVariables {
  /** Target entity ID (GUID) that owns the field */
  entityId: string;
  /** Field ID (GUID) to update */
  fieldId: string;
  /**
   * Updated field definition — accepts either a base Field shape (for
   * common property updates like label, description, required) or a
   * specific AnyField variant for type-specific property changes.
   */
  field: Field | AnyField;
}

/** Variables for the {@link useDeleteField} mutation */
interface DeleteFieldVariables {
  /** Target entity ID (GUID) that owns the field */
  entityId: string;
  /** Field ID (GUID) to remove */
  fieldId: string;
}

/** Variables for the {@link useUpdateRelation} mutation */
interface UpdateRelationVariables {
  /** Relation ID (GUID string) */
  id: string;
  /**
   * Partial relation update — only `label` and `description` are mutable.
   * Structural properties (relationType, originEntityId, originFieldId,
   * targetEntityId, targetFieldId) are immutable after creation per
   * monolith's EntityRelationManager.Update() business rules.
   */
  relation: Partial<EntityRelation>;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Validates an API response envelope and throws a descriptive error when
 * the operation failed (`success === false`).
 *
 * Accepts a response shape matching the monolith's BaseResponseModel
 * members (`success`, `errors`, `message`). The `errors` array mirrors
 * `ErrorModel[]` from the monolith with `{ key, value, message }` per error,
 * which is structurally identical to the client's `ApiErrorItem[]`.
 *
 * @param response - API response with success flag and structured error details
 * @param fallbackMessage - Default error message when no specific errors returned
 * @throws Error with concatenated error messages from the response envelope
 */
function assertApiSuccess(
  response: Pick<BaseResponseModel, 'success' | 'errors' | 'message'>,
  fallbackMessage: string,
): void {
  if (!response.success) {
    const errorMessages = response.errors
      ?.map((err) => err.message)
      .filter(Boolean);
    throw new Error(
      errorMessages && errorMessages.length > 0
        ? errorMessages.join('; ')
        : response.message || fallbackMessage,
    );
  }
}

// ---------------------------------------------------------------------------
// Entity Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches all entity definitions from the Entity Management service.
 *
 * Replaces `EntityManager.ReadEntities()` which was cache-first with
 * lock-based refresh, computing per-entity MD5 hash for change detection.
 * The monolith loaded ALL entities with ALL fields into memory on first
 * access and cached them aggressively via `Cache.AddEntities()`.
 *
 * In the serverless target, the Entity Management service handles caching
 * internally (DynamoDB). TanStack Query provides client-side stale-while-
 * revalidate behaviour with a 5-minute staleTime.
 *
 * API: `GET /v1/entities`
 * Response shape: {@link EntityListResponse} `{ success, object: Entity[] }`
 *
 * @returns TanStack Query result with `Entity[]` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function EntityList() {
 *   const { data: entities, isLoading, isError, error, refetch } = useEntities();
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return entities?.map((e) => <EntityRow key={e.id} entity={e} />);
 * }
 * ```
 */
export function useEntities() {
  return useQuery<EntityListResponse['object'], Error>({
    queryKey: ENTITY_QUERY_KEYS.all,

    queryFn: async (): Promise<EntityListResponse['object']> => {
      const response = await get<Entity[]>('/v1/entities');
      assertApiSuccess(response, 'Failed to fetch entities');

      if (!response.object) {
        throw new Error('Entity list response missing data');
      }

      return response.object;
    },

    // Match monolith's aggressive entity metadata caching (Cache.cs)
    staleTime: ENTITY_METADATA_STALE_TIME_MS,
  });
}

/**
 * Fetches a single entity definition by ID or name.
 *
 * Replaces:
 *  - `EntityManager.ReadEntity(Guid id)` — Lookup by GUID from cache/DB
 *  - `EntityManager.ReadEntity(string name)` — Lookup by entity name
 *
 * Returns the full entity with all fields embedded (type-specific properties
 * included). The monolith's entity lookup always returned the complete entity
 * with all fields from the entities JSON doc store.
 *
 * API: `GET /v1/entities/{idOrName}`
 * Response shape: {@link EntityResponse} `{ success, object: Entity }`
 *
 * @param idOrName - Entity ID (GUID string) or entity name. Query is
 *                   disabled when this is an empty string or falsy.
 * @returns TanStack Query result with single `Entity` data
 *
 * @example
 * ```tsx
 * function EntityEditor({ entityId }: { entityId: string }) {
 *   const { data: entity, isLoading, isError, error, isSuccess, refetch } = useEntity(entityId);
 *   if (isLoading) return <Skeleton />;
 *   return <EntityForm entity={entity} />;
 * }
 * ```
 */
export function useEntity(idOrName: string) {
  return useQuery<EntityResponse['object'], Error>({
    queryKey: ENTITY_QUERY_KEYS.detail(idOrName),

    queryFn: async (): Promise<EntityResponse['object']> => {
      const response = await get<Entity>(
        `/v1/entities/${encodeURIComponent(idOrName)}`,
      );
      assertApiSuccess(response, `Failed to fetch entity '${idOrName}'`);

      if (!response.object) {
        throw new Error(`Entity '${idOrName}' not found`);
      }

      return response.object;
    },

    // Disable query when no idOrName provided — prevents empty API calls
    enabled: !!idOrName,

    staleTime: ENTITY_METADATA_STALE_TIME_MS,
  });
}

// ---------------------------------------------------------------------------
// Entity Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new entity definition.
 *
 * Replaces `EntityManager.CreateEntity(InputEntity)` which:
 *  1. Validated name/label/labelPlural (63-char PostgreSQL identifier limit,
 *     uniqueness check, reserved-name guard)
 *  2. Enforced `HasMetaPermission` (admin-only gating)
 *  3. Created default fields (id, created_on, created_by, last_modified_on,
 *     last_modified_by) via `CreateDefaultFieldsForEntity()`
 *  4. Persisted entity metadata and `rec_{entityName}` table via
 *     `DbEntityRepository.Create()`
 *
 * All validation now happens server-side on the Entity Management Lambda.
 *
 * API: `POST /v1/entities`
 * Body: {@link InputEntity} `{ name, label, labelPlural, iconName, color, recordPermissions }`
 * Response shape: {@link EntityResponse} `{ success, object: Entity }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateEntityForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateEntity();
 *   const handleSubmit = (input: InputEntity) => {
 *     mutate(input, {
 *       onSuccess: (entity) => navigate(`/entities/${entity.id}`),
 *     });
 *   };
 * }
 * ```
 */
export function useCreateEntity() {
  const queryClient = useQueryClient();

  return useMutation<EntityResponse['object'], Error, InputEntity>({
    mutationFn: async (
      entity: InputEntity,
    ): Promise<EntityResponse['object']> => {
      const response = await post<Entity>('/v1/entities', entity);
      assertApiSuccess(response, 'Failed to create entity');

      if (!response.object) {
        throw new Error('Create entity response missing data');
      }

      return response.object;
    },

    onSuccess: () => {
      // Clear entity list cache to include the newly created entity
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
    },
  });
}

/**
 * Updates an existing entity definition.
 *
 * Replaces `EntityManager.UpdateEntity(InputEntity)` which validated the
 * entity metadata, enforced meta permission, and updated both the metadata
 * JSON doc and the record storage schema (column types, indexes).
 *
 * API: `PUT /v1/entities/{id}`
 * Body: {@link InputEntity} partial update
 * Response shape: {@link EntityResponse} `{ success, object: Entity }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditEntityForm({ entityId }: { entityId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateEntity();
 *   const handleSubmit = (input: InputEntity) => {
 *     mutate({ id: entityId, entity: input });
 *   };
 * }
 * ```
 */
export function useUpdateEntity() {
  const queryClient = useQueryClient();

  return useMutation<EntityResponse['object'], Error, UpdateEntityVariables>({
    mutationFn: async ({
      id,
      entity,
    }: UpdateEntityVariables): Promise<EntityResponse['object']> => {
      const response = await put<Entity>(
        `/v1/entities/${encodeURIComponent(id)}`,
        entity,
      );
      assertApiSuccess(response, 'Failed to update entity');

      if (!response.object) {
        throw new Error('Update entity response missing data');
      }

      return response.object;
    },

    onSuccess: (_data, variables) => {
      // Invalidate both the list and the specific entity detail cache
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes an entity definition and all its associated data.
 *
 * Replaces `EntityManager.DeleteEntity(Guid id)` which enforced meta
 * permission, deleted the entity metadata from the JSON doc store, dropped
 * the `rec_{entityName}` PostgreSQL table, removed all relation endpoints
 * on the entity, and cleared the entity cache via `Cache.Clear()`.
 *
 * CRITICAL: Entity deletion is cascading — the server removes all fields,
 * all records stored in the entity's table, and all relation endpoints
 * that reference the deleted entity.
 *
 * API: `DELETE /v1/entities/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteEntityButton({ entityId }: { entityId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteEntity();
 *   return (
 *     <button onClick={() => mutate(entityId)} disabled={isPending}>
 *       Delete Entity
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteEntity() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(`/v1/entities/${encodeURIComponent(id)}`);
      assertApiSuccess(response, 'Failed to delete entity');
    },

    onSuccess: () => {
      // Invalidate entity caches — the deleted entity is gone
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
      // Also invalidate records — records of the deleted entity no longer exist
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.records,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Field Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new field on an existing entity.
 *
 * Replaces `EntityManager.CreateField(Guid entityId, InputField field)` which:
 *  1. Validated type-specific rules (default values, options for SelectField,
 *     decimal places for CurrencyField/NumberField/PercentField, etc.)
 *  2. Wrapped the operation in a DB transaction (entity metadata update +
 *     record schema column addition via `DbEntityRepository.Update()` +
 *     `DbRepository.CreateColumn()`)
 *  3. Supported 20+ field types routed via the `fieldType` discriminator
 *
 * The server handles all validation, default value processing, and storage
 * schema updates. The AnyField discriminated union ensures the correct
 * type-specific properties are sent.
 *
 * API: `POST /v1/entities/{entityId}/fields`
 * Body: {@link AnyField} with `fieldType` discriminator for type routing
 * Response shape: {@link EntityResponse} `{ success, object: Entity }` (updated entity with new field)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function AddFieldForm({ entityId }: { entityId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateField();
 *   const handleSubmit = (field: AnyField) => {
 *     mutate({ entityId, field });
 *   };
 * }
 * ```
 */
export function useCreateField() {
  const queryClient = useQueryClient();

  return useMutation<EntityResponse['object'], Error, CreateFieldVariables>({
    mutationFn: async ({
      entityId,
      field,
    }: CreateFieldVariables): Promise<EntityResponse['object']> => {
      const response = await post<Entity>(
        `/v1/entities/${encodeURIComponent(entityId)}/fields`,
        field,
      );
      assertApiSuccess(response, 'Failed to create field');

      if (!response.object) {
        throw new Error('Create field response missing data');
      }

      return response.object;
    },

    onSuccess: (_data, variables) => {
      // Field mutations invalidate the parent entity (entity contains
      // embedded fields in its fields: Field[] array)
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.detail(variables.entityId),
      });
    },
  });
}

/**
 * Updates an existing field on an entity.
 *
 * Replaces `EntityManager.UpdateField(Guid entityId, InputField field)` which
 * validated the update, modified field metadata in the entity JSON doc, and
 * potentially altered the storage type (e.g., changing a text field's max
 * length triggers a column type change).
 *
 * Accepts either a base {@link Field} shape (for common property updates like
 * label, description, required, searchable) or a specific {@link AnyField}
 * variant for type-specific property changes.
 *
 * API: `PUT /v1/entities/{entityId}/fields/{fieldId}`
 * Body: {@link Field} or {@link AnyField}
 * Response shape: {@link EntityResponse} `{ success, object: Entity }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditFieldForm({ entityId, fieldId }: { entityId: string; fieldId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateField();
 *   const handleSubmit = (field: Field) => {
 *     mutate({ entityId, fieldId, field });
 *   };
 * }
 * ```
 */
export function useUpdateField() {
  const queryClient = useQueryClient();

  return useMutation<EntityResponse['object'], Error, UpdateFieldVariables>({
    mutationFn: async ({
      entityId,
      fieldId,
      field,
    }: UpdateFieldVariables): Promise<EntityResponse['object']> => {
      const response = await put<Entity>(
        `/v1/entities/${encodeURIComponent(entityId)}/fields/${encodeURIComponent(fieldId)}`,
        field,
      );
      assertApiSuccess(response, 'Failed to update field');

      if (!response.object) {
        throw new Error('Update field response missing data');
      }

      return response.object;
    },

    onSuccess: (_data, variables) => {
      // Field mutations invalidate the parent entity
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.detail(variables.entityId),
      });
    },
  });
}

/**
 * Deletes a field from an entity.
 *
 * Replaces `EntityManager.DeleteField(Guid entityId, Guid fieldId)` which
 * removed the field from entity metadata, dropped the storage column from
 * the `rec_{entityName}` table, and cleared the entity cache.
 *
 * API: `DELETE /v1/entities/{entityId}/fields/{fieldId}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteFieldButton({ entityId, fieldId }: { entityId: string; fieldId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteField();
 *   return (
 *     <button onClick={() => mutate({ entityId, fieldId })} disabled={isPending}>
 *       Delete Field
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteField() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, DeleteFieldVariables>({
    mutationFn: async ({
      entityId,
      fieldId,
    }: DeleteFieldVariables): Promise<void> => {
      const response = await del(
        `/v1/entities/${encodeURIComponent(entityId)}/fields/${encodeURIComponent(fieldId)}`,
      );
      assertApiSuccess(response, 'Failed to delete field');
    },

    onSuccess: (_data, variables) => {
      // Field mutations invalidate the parent entity
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.detail(variables.entityId),
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Relation Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches all entity relation definitions from the Entity Management service.
 *
 * Replaces `EntityRelationManager.Read()` which loaded all relations from
 * the cached entity metadata (relations were part of the aggregated
 * `EntityManager.ReadEntities()` cache with lock-based refresh).
 *
 * API: `GET /v1/relations`
 * Response shape: {@link EntityRelationListResponse} `{ success, object: EntityRelation[] }`
 *
 * @returns TanStack Query result with `EntityRelation[]` data, plus
 *          `isLoading`, `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function RelationList() {
 *   const { data: relations, isLoading, isError, error, refetch } = useRelations();
 *   if (isLoading) return <Spinner />;
 *   return relations?.map((r) => <RelationRow key={r.id} relation={r} />);
 * }
 * ```
 */
export function useRelations() {
  return useQuery<EntityRelationListResponse['object'], Error>({
    queryKey: ENTITY_QUERY_KEYS.relations,

    queryFn: async (): Promise<EntityRelationListResponse['object']> => {
      const response = await get<EntityRelation[]>('/v1/relations');
      assertApiSuccess(response, 'Failed to fetch relations');

      if (!response.object) {
        throw new Error('Relations list response missing data');
      }

      return response.object;
    },

    // Same staleTime as entities — relation metadata is equally stable
    staleTime: ENTITY_METADATA_STALE_TIME_MS,
  });
}

/**
 * Fetches a single entity relation by ID or name.
 *
 * Replaces:
 *  - `EntityRelationManager.Read(Guid id)` — Lookup by GUID
 *  - `EntityRelationManager.Read(string name)` — Lookup by relation name
 *
 * API: `GET /v1/relations/{idOrName}`
 * Response shape: {@link EntityRelationResponse} `{ success, object: EntityRelation }`
 *
 * @param idOrName - Relation ID (GUID string) or relation name. Query is
 *                   disabled when this is an empty string or falsy.
 * @returns TanStack Query result with single `EntityRelation` data
 *
 * @example
 * ```tsx
 * function RelationEditor({ relationId }: { relationId: string }) {
 *   const { data: relation, isLoading, isError, error, isSuccess, refetch } = useRelation(relationId);
 *   return <RelationForm relation={relation} />;
 * }
 * ```
 */
export function useRelation(idOrName: string) {
  return useQuery<EntityRelationResponse['object'], Error>({
    queryKey: ENTITY_QUERY_KEYS.relation(idOrName),

    queryFn: async (): Promise<EntityRelationResponse['object']> => {
      const response = await get<EntityRelation>(
        `/v1/relations/${encodeURIComponent(idOrName)}`,
      );
      assertApiSuccess(response, `Failed to fetch relation '${idOrName}'`);

      if (!response.object) {
        throw new Error(`Relation '${idOrName}' not found`);
      }

      return response.object;
    },

    // Disable query when no idOrName provided — prevents empty API calls
    enabled: !!idOrName,

    staleTime: ENTITY_METADATA_STALE_TIME_MS,
  });
}

// ---------------------------------------------------------------------------
// Relation Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new entity relation.
 *
 * Replaces `EntityRelationManager.Create(EntityRelation)` which:
 *  1. Validated origin/target entity and field existence
 *  2. Enforced `HasMetaPermission` (admin-only gating)
 *  3. Created FK constraints and join tables based on relation type
 *     (OneToOne → FK, OneToMany → FK, ManyToMany → join table)
 *  4. Persisted relation metadata to the `entity_relations` JSON doc store
 *
 * API: `POST /v1/relations`
 * Body: {@link EntityRelation} `{ name, label, relationType, originEntityId, originFieldId, targetEntityId, targetFieldId }`
 * Response shape: {@link EntityRelationResponse} `{ success, object: EntityRelation }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateRelationForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateRelation();
 *   const handleSubmit = (relation: EntityRelation) => {
 *     mutate(relation);
 *   };
 * }
 * ```
 */
export function useCreateRelation() {
  const queryClient = useQueryClient();

  return useMutation<
    EntityRelationResponse['object'],
    Error,
    EntityRelation
  >({
    mutationFn: async (
      relation: EntityRelation,
    ): Promise<EntityRelationResponse['object']> => {
      const response = await post<EntityRelation>('/v1/relations', relation);
      assertApiSuccess(response, 'Failed to create relation');

      if (!response.object) {
        throw new Error('Create relation response missing data');
      }

      return response.object;
    },

    onSuccess: () => {
      // Relation mutations invalidate both relations and entities caches
      // because a relation connects two entities and changes their metadata
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.relations,
      });
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
    },
  });
}

/**
 * Updates an existing entity relation.
 *
 * Replaces `EntityRelationManager.Update(EntityRelation)` which enforced
 * strict immutability rules — only `label` and `description` are mutable
 * after creation. The structural properties (`relationType`, `originEntityId`,
 * `originFieldId`, `targetEntityId`, `targetFieldId`) are permanently locked
 * to preserve referential integrity.
 *
 * API: `PUT /v1/relations/{id}`
 * Body: `Partial<EntityRelation>` (limited to label, description)
 * Response shape: {@link EntityRelationResponse} `{ success, object: EntityRelation }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditRelationForm({ relationId }: { relationId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateRelation();
 *   const handleSubmit = (updates: Partial<EntityRelation>) => {
 *     mutate({ id: relationId, relation: updates });
 *   };
 * }
 * ```
 */
export function useUpdateRelation() {
  const queryClient = useQueryClient();

  return useMutation<
    EntityRelationResponse['object'],
    Error,
    UpdateRelationVariables
  >({
    mutationFn: async ({
      id,
      relation,
    }: UpdateRelationVariables): Promise<EntityRelationResponse['object']> => {
      const response = await put<EntityRelation>(
        `/v1/relations/${encodeURIComponent(id)}`,
        relation,
      );
      assertApiSuccess(response, 'Failed to update relation');

      if (!response.object) {
        throw new Error('Update relation response missing data');
      }

      return response.object;
    },

    onSuccess: (_data, variables) => {
      // Invalidate both the relations list and the specific relation detail
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.relations,
      });
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.relation(variables.id),
      });
    },
  });
}

/**
 * Deletes an entity relation.
 *
 * Replaces `EntityRelationManager.Delete(Guid id)` which deleted the
 * relation metadata from the `entity_relations` JSON doc store and cleaned
 * up FK constraints or join tables in PostgreSQL based on the relation type.
 *
 * API: `DELETE /v1/relations/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteRelationButton({ relationId }: { relationId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteRelation();
 *   return (
 *     <button onClick={() => mutate(relationId)} disabled={isPending}>
 *       Delete Relation
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteRelation() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(
        `/v1/relations/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, 'Failed to delete relation');
    },

    onSuccess: () => {
      // Relation mutations invalidate both relations and entities caches
      void queryClient.invalidateQueries({
        queryKey: ENTITY_QUERY_KEYS.relations,
      });
      void queryClient.invalidateQueries({ queryKey: ENTITY_QUERY_KEYS.all });
    },
  });
}
