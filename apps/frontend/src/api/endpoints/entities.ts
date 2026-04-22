/**
 * Entity/Field/Relation CRUD API Module
 *
 * Provides typed API functions for entity metadata, field, and relation CRUD
 * operations against the Entity Management bounded-context service.
 *
 * Replaces the monolith's WebApiController.cs entity management endpoints
 * (lines 1435–2100) with HTTP API calls through API Gateway.
 *
 * Server response envelopes follow these typed shapes:
 * - {@link EntityListResponse}.object — Entity[] for entity list operations
 * - {@link EntityResponse}.object — Entity for single entity operations
 * - {@link EntityRelationListResponse}.object — EntityRelation[] for relation lists
 * - {@link FieldResponse}.object — Field for field operations
 *
 * All entity admin endpoints require the administrator role.
 * JWT authorizer validates role claims at the API Gateway level.
 * Error handling is delegated to the centralized client.ts interceptor.
 *
 * @module api/endpoints/entities
 */

import { get, post, put, patch, del } from '../client';
import type { ApiResponse } from '../client';
import type {
  Entity,
  InputEntity,
  EntityRelation,
  EntityResponse,
  EntityListResponse,
  EntityRelationListResponse,
  FieldResponse,
  AnyField,
} from '../../types/entity';

/**
 * Base path for entity metadata management endpoints.
 * Maps from monolith route `api/v3/en_US/meta/entity/*`.
 */
const ENTITY_BASE = '/entity-management/entities';

/**
 * Base path for entity relation management endpoints.
 * Maps from monolith route `api/v3/en_US/meta/relation/*`.
 */
const RELATION_BASE = '/entity-management/relations';

// ─── Entity Metadata Operations ────────────────────────────────────────────────

/**
 * Retrieves the full list of entity metadata definitions.
 *
 * Supports cache validation via the optional `hash` parameter. When the
 * provided hash matches the server's current metadata hash, the response
 * returns `object: null` — indicating the client's cached data is still
 * valid. This mirrors the monolith's GetEntityMetaList cache behaviour
 * (WebApiController.cs line 1439).
 *
 * The server response follows the {@link EntityListResponse} envelope where
 * `EntityListResponse.object` is typed as `Entity[]`.
 *
 * The response {@link ApiResponse.hash} field carries the current metadata
 * hash that can be passed back on subsequent calls for cache validation.
 * The caller inspects {@link ApiResponse.success}, {@link ApiResponse.object},
 * {@link ApiResponse.errors}, and {@link ApiResponse.message} on the envelope.
 *
 * @param hash - Optional metadata hash for ETag-style cache validation.
 * @returns A promise resolving to the entity list or null when cache is valid.
 */
export function getEntityList(
  hash?: string,
): Promise<ApiResponse<Entity[]>> {
  return get<Entity[]>(
    ENTITY_BASE,
    hash !== undefined ? { hash } : undefined,
  );
}

/**
 * Retrieves a single entity metadata definition by its unique identifier.
 *
 * Uses the Entity type which includes {@link Entity.id}, {@link Entity.name},
 * {@link Entity.label}, {@link Entity.fields}, and {@link Entity.hash}.
 *
 * The server response follows the {@link EntityResponse} envelope where
 * `EntityResponse.object` is typed as `Entity`.
 *
 * @param entityId - GUID string identifying the entity.
 * @returns A promise resolving to the matched entity.
 */
export function getEntityById(
  entityId: string,
): Promise<ApiResponse<Entity>> {
  return get<Entity>(
    `${ENTITY_BASE}/id/${encodeURIComponent(entityId)}`,
  );
}

/**
 * Retrieves a single entity metadata definition by its unique name.
 *
 * The server response follows the {@link EntityResponse} envelope where
 * `EntityResponse.object` is typed as `Entity`.
 *
 * @param name - System name of the entity (e.g. "account", "contact").
 * @returns A promise resolving to the matched entity.
 */
export function getEntityByName(
  name: string,
): Promise<ApiResponse<Entity>> {
  return get<Entity>(
    `${ENTITY_BASE}/${encodeURIComponent(name)}`,
  );
}

/**
 * Retrieves all fields defined on a specific entity by entity ID.
 *
 * This fetches fields from the dedicated `/entities/{id}/fields` endpoint,
 * which is separate from the entity detail endpoint. The mock Lambda
 * stores fields in `FIELDS#{entityId}` DynamoDB partition, so they must
 * be fetched independently from the entity metadata.
 *
 * @param entityId - UUID of the entity whose fields to retrieve.
 * @returns A promise resolving to the list of fields for the entity.
 */
export function getEntityFields(
  entityId: string,
): Promise<ApiResponse<AnyField[]>> {
  return get<AnyField[]>(
    `${ENTITY_BASE}/${encodeURIComponent(entityId)}/fields`,
  );
}

/**
 * Creates a new entity metadata definition.
 *
 * The request body is validated server-side against entity naming rules,
 * duplicate detection, and permission constraints. The {@link InputEntity}
 * interface requires {@link InputEntity.name}, {@link InputEntity.label},
 * {@link InputEntity.labelPlural}, {@link InputEntity.iconName},
 * {@link InputEntity.color}, and {@link InputEntity.recordPermissions}.
 *
 * On success, the response follows the {@link EntityResponse} envelope
 * with the created entity.
 *
 * @param entity - Full entity definition conforming to InputEntity.
 * @returns A promise resolving to the newly created entity.
 */
export function createEntity(
  entity: InputEntity,
): Promise<ApiResponse<Entity>> {
  return post<Entity>(ENTITY_BASE, entity);
}

/**
 * Partially updates an existing entity metadata definition.
 *
 * Mirrors the monolith's PatchEntity logic (WebApiController.cs line 1497):
 * reads the current entity server-side, then applies only the submitted
 * fields (label, labelPlural, system, iconName, color, recordPermissions,
 * recordScreenIdField). Unsubmitted properties remain unchanged.
 *
 * Accepts a partial subset of the {@link InputEntity} interface.
 *
 * @param entityId - GUID string identifying the entity to patch.
 * @param fields   - Partial entity fields to update.
 * @returns A promise resolving to the updated entity.
 */
export function patchEntity(
  entityId: string,
  fields: Partial<InputEntity>,
): Promise<ApiResponse<Entity>> {
  return patch<Entity>(
    `${ENTITY_BASE}/${encodeURIComponent(entityId)}`,
    fields,
  );
}

/**
 * Deletes an entity metadata definition and its backing data store.
 *
 * This operation is destructive and irreversible — all records belonging
 * to the entity are permanently removed.
 *
 * @param entityId - GUID string identifying the entity to delete.
 * @returns A promise resolving to a success/error response with null object.
 */
export function deleteEntity(
  entityId: string,
): Promise<ApiResponse<null>> {
  return del<null>(
    `${ENTITY_BASE}/${encodeURIComponent(entityId)}`,
  );
}

// ─── Field Operations ──────────────────────────────────────────────────────────

/**
 * Creates a new field on an existing entity.
 *
 * The field body must include a `fieldType` discriminator that determines
 * the field's storage type and validation rules. Server-side, this mirrors
 * the InputField.ConvertField conversion (WebApiController.cs line 1595)
 * which interprets the JSON payload based on the fieldType value.
 *
 * Supports all 21 field types defined in the monolith (AutoNumber, Checkbox,
 * Currency, Date, DateTime, Email, File, Geography, Guid, Html, Image,
 * Multiline, MultiSelect, Number, Password, Percent, Phone, Select, Text,
 * Url, TreeSelect).
 *
 * The server response follows the {@link FieldResponse} envelope where
 * `FieldResponse.object` is typed as the created field.
 *
 * @param entityId - GUID string identifying the parent entity.
 * @param field    - Field definition JSON including fieldType discriminator.
 * @returns A promise resolving to the newly created field.
 */
export function createField(
  entityId: string,
  field: Record<string, unknown>,
): Promise<ApiResponse<FieldResponse['object']>> {
  return post<FieldResponse['object']>(
    `${ENTITY_BASE}/${encodeURIComponent(entityId)}/fields`,
    field,
  );
}

/**
 * Fully replaces an existing field definition on an entity.
 *
 * Validates all field properties server-side via InputField.ConvertField
 * (WebApiController.cs line 1622). The entire field object must be provided
 * since this is a full replacement (PUT), not a partial update.
 *
 * The server response follows the {@link FieldResponse} envelope where
 * `FieldResponse.object` is typed as the updated field.
 *
 * @param entityId - GUID string identifying the parent entity.
 * @param fieldId  - GUID string identifying the field to replace.
 * @param field    - Complete field definition JSON.
 * @returns A promise resolving to the replaced field.
 */
/**
 * Internal shared implementation for field mutation operations (PUT/PATCH).
 * Both updateField and patchField share identical URL construction and
 * payload forwarding; only the HTTP method differs.
 *
 * @param method   - HTTP method function (put or patch).
 * @param entityId - GUID string identifying the parent entity.
 * @param fieldId  - GUID string identifying the field.
 * @param payload  - Field properties to send.
 * @returns A promise resolving to the updated/patched field.
 */
function mutateField(
  method: typeof put | typeof patch,
  entityId: string,
  fieldId: string,
  payload: Record<string, unknown>,
): Promise<ApiResponse<FieldResponse['object']>> {
  return method<FieldResponse['object']>(
    `${ENTITY_BASE}/${encodeURIComponent(entityId)}/fields/${encodeURIComponent(fieldId)}`,
    payload,
  );
}

export function updateField(
  entityId: string,
  fieldId: string,
  field: Record<string, unknown>,
): Promise<ApiResponse<FieldResponse['object']>> {
  return mutateField(put, entityId, fieldId, field);
}

/**
 * Partially updates an existing field definition on an entity.
 *
 * Mirrors the monolith's complex PatchField logic (WebApiController.cs
 * line 1678): reads the existing field, then applies only the changed
 * properties respecting the field type discriminator. Common patchable
 * properties include label, placeholderText, description, helpText,
 * required, unique, searchable, auditable, and system flags, plus
 * per-fieldType specific properties (e.g. options for Select fields,
 * min/max for Number fields, currency for Currency fields, etc.).
 *
 * The server response follows the {@link FieldResponse} envelope where
 * `FieldResponse.object` is typed as the patched field.
 *
 * @param entityId - GUID string identifying the parent entity.
 * @param fieldId  - GUID string identifying the field to patch.
 * @param fields   - Partial field properties to merge.
 * @returns A promise resolving to the patched field.
 */
export function patchField(
  entityId: string,
  fieldId: string,
  fields: Record<string, unknown>,
): Promise<ApiResponse<FieldResponse['object']>> {
  return mutateField(patch, entityId, fieldId, fields);
}

/**
 * Deletes a field from an entity.
 *
 * Removes the field definition and its data column from the entity's
 * backing data store. This operation is irreversible.
 *
 * @param entityId - GUID string identifying the parent entity.
 * @param fieldId  - GUID string identifying the field to delete.
 * @returns A promise resolving to a success/error response with null object.
 */
export function deleteField(
  entityId: string,
  fieldId: string,
): Promise<ApiResponse<null>> {
  return del<null>(
    `${ENTITY_BASE}/${encodeURIComponent(entityId)}/fields/${encodeURIComponent(fieldId)}`,
  );
}

// ─── Relation Operations ───────────────────────────────────────────────────────

/**
 * Retrieves the full list of entity relation metadata definitions.
 *
 * The server response follows the {@link EntityRelationListResponse} envelope
 * where `EntityRelationListResponse.object` is typed as `EntityRelation[]`.
 *
 * Each relation includes {@link EntityRelation.id}, {@link EntityRelation.name},
 * {@link EntityRelation.relationType}, {@link EntityRelation.originEntityId},
 * and {@link EntityRelation.targetEntityId}.
 *
 * @returns A promise resolving to the complete list of entity relations.
 */
export function getRelationList(): Promise<ApiResponse<EntityRelation[]>> {
  return get<EntityRelation[]>(RELATION_BASE);
}

/**
 * Creates a new entity relation.
 *
 * Server-side auto-generates the relation `id` if not provided in the body
 * (mirrors CreateEntityRelation, WebApiController.cs line ~1870).
 *
 * @param relation - Partial relation definition including name, relationType,
 *                   originEntityId, originFieldId, targetEntityId, targetFieldId.
 * @returns A promise resolving to the newly created relation.
 */
export function createRelation(
  relation: Partial<EntityRelation>,
): Promise<ApiResponse<EntityRelation>> {
  return post<EntityRelation>(RELATION_BASE, relation);
}

/**
 * Updates an existing entity relation by its name.
 *
 * Mirrors UpdateEntityRelation (WebApiController.cs line ~1960).
 *
 * @param name     - The current relation name used as the route key.
 * @param relation - Partial relation fields to update (label, description, etc.).
 * @returns A promise resolving to the updated relation.
 */
export function updateRelation(
  name: string,
  relation: Partial<EntityRelation>,
): Promise<ApiResponse<EntityRelation>> {
  return put<EntityRelation>(
    `${RELATION_BASE}/${encodeURIComponent(name)}`,
    relation,
  );
}

/**
 * Deletes an entity relation by its name.
 *
 * Removes the relation definition and its backing foreign key or join table.
 * This operation is irreversible.
 *
 * @param name - The relation name to delete.
 * @returns A promise resolving to a success/error response with null object.
 */
export function deleteRelation(
  name: string,
): Promise<ApiResponse<null>> {
  return del<null>(
    `${RELATION_BASE}/${encodeURIComponent(name)}`,
  );
}
