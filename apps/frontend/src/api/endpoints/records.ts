/**
 * Record CRUD Operations API Module
 *
 * Typed API functions for record CRUD, EQL queries, datasource execution,
 * and entity relation record management. Replaces WebApiController.cs
 * lines 63–337 (EQL/DS queries) and lines 2102–3012 (record CRUD, relation records).
 * Routes to Entity Management service via API Gateway.
 *
 * Route prefix mapping:
 *   - EQL/DS:    /entity-management/query/*
 *   - Records:   /entity-management/records/{entityName}/*
 *   - Relations: /entity-management/relations/records/*
 *   - CSV:       /entity-management/records/{entityName}/import
 */

import { get, post, del } from '../client';
import type { ApiResponse } from '../client';
import apiClient from '../client';
import type {
  EntityRecord,
  QueryResponse,
  EntityRecordList,
} from '../../types/record';
import type { EqlParameter } from '../../types/datasource';

// ──────────────────────────────────────────────────────────────
// Interface Definitions
// ──────────────────────────────────────────────────────────────

/**
 * Parameters for updating entity relation records (forward direction).
 *
 * Maps to InputEntityRelationRecordUpdateModel from WebApiController.cs
 * line 2106. Handles attach/detach operations for 1:1, 1:N, and N:N
 * relations with transactional processing on the server.
 */
export interface RelationRecordUpdateParams {
  /** Name of the entity relation to modify */
  relationName: string;
  /** Record ID of the origin field in the relation */
  originFieldRecordId: string;
  /** Array of target record IDs to attach to the origin record */
  attachTargetFieldRecordIds: string[];
  /** Array of target record IDs to detach from the origin record */
  detachTargetFieldRecordIds: string[];
}

/**
 * Parameters for updating entity relation records (reverse direction).
 *
 * Maps to InputEntityRelationRecordReverseUpdateModel from
 * WebApiController.cs line 2305. Same attach/detach pattern as
 * RelationRecordUpdateParams but from the target field perspective.
 */
export interface RelationRecordReverseUpdateParams {
  /** Name of the entity relation to modify */
  relationName: string;
  /** Record ID of the target field in the relation */
  targetFieldRecordId: string;
  /** Array of origin record IDs to attach to the target record */
  attachOriginFieldRecordIds: string[];
  /** Array of origin record IDs to detach from the target record */
  detachOriginFieldRecordIds: string[];
}

/**
 * Select2-compatible response format for datasource queries.
 *
 * Maps to the special format produced by DataSourceQueryActionForSelect2
 * (WebApiController.cs line 192). The server resolves the text field via
 * fallback chain: text → label → name → id (lines 297–321).
 */
export interface Select2Response {
  /** Array of Select2-formatted results with id and text properties */
  results: Array<{ id: string; text: string }>;
  /** Pagination metadata indicating if more results are available */
  pagination: { more: boolean };
}

// ──────────────────────────────────────────────────────────────
// EQL / DataSource Query Functions
// ──────────────────────────────────────────────────────────────

/**
 * Execute an EQL (Entity Query Language) query.
 *
 * Replaces WebApiController.EqlQueryAction (line 63).
 * POST api/v3/en_US/eql → POST /entity-management/query/eql
 *
 * Returns a QueryResponse containing fieldsMeta and data arrays.
 * On EqlException the server returns an errors array with key "eql".
 *
 * @param eql - The EQL query string (SELECT/FROM/WHERE/ORDER/PAGE syntax)
 * @param parameters - Optional array of EQL parameters for parameterized queries
 * @returns Promise resolving to QueryResponse with fieldsMeta and data
 */
export async function executeEql(
  eql: string,
  parameters?: EqlParameter[],
): Promise<ApiResponse<QueryResponse>> {
  return post<QueryResponse>('/entity-management/query/eql', {
    eql,
    parameters: parameters ?? [],
  });
}

/**
 * Execute a datasource query by name.
 *
 * Replaces WebApiController.DataSourceQueryAction (line 98).
 * POST api/v3/en_US/eql-ds → POST /entity-management/query/datasource
 *
 * For DatabaseDataSource returns {list, total_count} mapped to
 * EntityRecordList. For CodeDataSource returns the raw result.
 *
 * @param name - The datasource name to execute
 * @param parameters - Optional EQL parameters for datasource parameter overrides
 * @returns Promise resolving to EntityRecordList with records and totalCount
 */
export async function executeDataSource(
  name: string,
  parameters?: EqlParameter[],
): Promise<ApiResponse<EntityRecordList>> {
  return post<EntityRecordList>('/entity-management/query/datasource', {
    name,
    parameters: parameters ?? [],
  });
}

/**
 * Execute a datasource query formatted for Select2 dropdown integration.
 *
 * Replaces WebApiController.DataSourceQueryActionForSelect2 (line 192).
 * POST api/v3/en_US/eql-ds-select2 →
 * POST /entity-management/query/datasource-select2
 *
 * Returns results in {id, text} format with pagination.more indicator.
 * Server-side text field resolution: text → label → name → id (fallback).
 * Pagination: more is true while page * 10 < totalCount.
 *
 * @param name - The datasource name to execute
 * @param parameters - Optional EQL parameters including page for pagination
 * @returns Promise resolving to Select2Response with results and pagination
 */
export async function executeDataSourceSelect2(
  name: string,
  parameters?: EqlParameter[],
): Promise<ApiResponse<Select2Response>> {
  return post<Select2Response>(
    '/entity-management/query/datasource-select2',
    {
      name,
      parameters: parameters ?? [],
    },
  );
}

// ──────────────────────────────────────────────────────────────
// Record CRUD Functions
// ──────────────────────────────────────────────────────────────

/**
 * Get a single record by entity name and record ID.
 *
 * Replaces WebApiController.GetRecord (line 2504).
 * GET api/v3/en_US/record/{entityName}/{recordId} →
 * GET /entity-management/records/{entityName}/{recordId}
 *
 * @param entityName - Name of the entity to retrieve from
 * @param recordId - UUID of the record to retrieve
 * @param fields - Optional comma-separated field names (defaults to "*" server-side)
 * @returns Promise resolving to the retrieved EntityRecord
 */
export async function getRecord(
  entityName: string,
  recordId: string,
  fields?: string,
): Promise<ApiResponse<EntityRecord>> {
  const params: Record<string, unknown> = {};
  if (fields) {
    params.fields = fields;
  }
  return get<EntityRecord>(
    `/entity-management/records/${encodeURIComponent(entityName)}/${encodeURIComponent(recordId)}`,
    params,
  );
}

/**
 * Delete a record by entity name and record ID.
 *
 * Replaces WebApiController.DeleteRecord (line 2521).
 * DELETE api/v3/en_US/record/{entityName}/{recordId} →
 * DELETE /entity-management/records/{entityName}/{recordId}
 *
 * Server-side executes within a database transaction.
 *
 * @param entityName - Name of the entity containing the record
 * @param recordId - UUID of the record to delete
 * @returns Promise resolving on successful deletion
 */
export async function deleteRecord(
  entityName: string,
  recordId: string,
): Promise<ApiResponse<null>> {
  return del<null>(
    `/entity-management/records/${encodeURIComponent(entityName)}/${encodeURIComponent(recordId)}`,
  );
}

/**
 * Search records by field value matching a regex pattern.
 *
 * Replaces WebApiController.GetRecordsByFieldAndRegex (line 2555).
 * POST api/v3/en_US/record/{entityName}/regex/{fieldName} →
 * POST /entity-management/records/{entityName}/regex/{fieldName}
 *
 * @param entityName - Name of the entity to search
 * @param fieldName - Name of the field to match against
 * @param pattern - Regex pattern to match field values
 * @returns Promise resolving to array of matching EntityRecords
 */
export async function getRecordsByRegex(
  entityName: string,
  fieldName: string,
  pattern: string,
): Promise<ApiResponse<EntityRecord[]>> {
  return post<EntityRecord[]>(
    `/entity-management/records/${encodeURIComponent(entityName)}/regex/${encodeURIComponent(fieldName)}`,
    { pattern },
  );
}

/**
 * Create a new entity record.
 *
 * Replaces WebApiController.CreateEntityRecord (line 2573).
 * POST api/v3/en_US/record/{entityName} →
 * POST /entity-management/records/{entityName}
 *
 * Server-side auto-generates an "id" field if not provided in the record
 * body. Handles $$ property naming normalization (FixDoubleDollarSignProblem).
 *
 * @param entityName - Name of the entity to create a record in
 * @param record - Record data with dynamic fields per entity definition
 * @returns Promise resolving to the created EntityRecord (includes auto-generated id)
 */
export async function createRecord(
  entityName: string,
  record: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(
    `/entity-management/records/${encodeURIComponent(entityName)}`,
    record,
  );
}

/**
 * Create a new entity record and simultaneously establish a relation.
 *
 * Replaces WebApiController.CreateEntityRecordWithRelation (line 2614).
 * POST api/v3/en_US/record/{entityName}/with-relation/{relationName}/{relatedRecordId} →
 * POST /entity-management/records/{entityName}/with-relation/{relationName}/{relatedRecordId}
 *
 * Server-side validates: relation exists, entity is part of relation,
 * related record exists and has target field value. Auto-generates id
 * if not provided. Handles 1:1/1:N (assigns target field) and N:N
 * (creates relation record) within a single transaction.
 *
 * @param entityName - Name of the entity to create a record in
 * @param relationName - Name of the relation to establish
 * @param relatedRecordId - UUID of the existing related record
 * @param record - Record data with dynamic fields per entity definition
 * @returns Promise resolving to the created EntityRecord with relation established
 */
export async function createRecordWithRelation(
  entityName: string,
  relationName: string,
  relatedRecordId: string,
  record: EntityRecord,
): Promise<ApiResponse<EntityRecord>> {
  const url = [
    '/entity-management/records',
    encodeURIComponent(entityName),
    'with-relation',
    encodeURIComponent(relationName),
    encodeURIComponent(relatedRecordId),
  ].join('/');

  return post<EntityRecord>(url, record);
}

/**
 * Get a list of records by entity name with optional filtering.
 *
 * Replaces WebApiController.GetRecordsByEntityName (line ~2878).
 * GET api/v3/en_US/record/{entityName}/list →
 * GET /entity-management/records/{entityName}/list
 *
 * Supports optional query parameters for batch retrieval and field
 * selection. The "id" field is always included server-side regardless
 * of the fields parameter.
 *
 * @param entityName - Name of the entity to list records from
 * @param fields - Optional comma-separated field names to return
 * @param ids - Optional comma-separated record IDs for batch retrieval (OR query)
 * @param limit - Optional maximum number of records to return
 * @returns Promise resolving to EntityRecordList with records and totalCount
 */
export async function getRecordsByEntityName(
  entityName: string,
  fields?: string,
  ids?: string,
  limit?: number,
): Promise<ApiResponse<EntityRecordList>> {
  const params: Record<string, unknown> = {};
  if (fields) {
    params.fields = fields;
  }
  if (ids) {
    params.ids = ids;
  }
  if (limit !== undefined) {
    params.limit = limit;
  }
  return get<EntityRecordList>(
    `/entity-management/records/${encodeURIComponent(entityName)}/list`,
    params,
  );
}

// ──────────────────────────────────────────────────────────────
// Entity Relation Record Functions
// ──────────────────────────────────────────────────────────────

/**
 * Update entity relation records (forward direction: origin → target).
 *
 * Replaces WebApiController.UpdateEntityRelationRecord (line 2106).
 * POST api/v3/en_US/record/relation →
 * POST /entity-management/relations/records
 *
 * Handles complex attach/detach logic for 1:1, 1:N, and N:N relations
 * with transactional processing. For 1:1/1:N relations, updates the
 * target field directly. For N:N relations, creates/removes join table
 * records.
 *
 * @param params - Relation update parameters with attach/detach target IDs
 * @returns Promise resolving on successful relation update
 */
export async function updateRelationRecord(
  params: RelationRecordUpdateParams,
): Promise<ApiResponse<null>> {
  return post<null>('/entity-management/relations/records', params);
}

/**
 * Update entity relation records (reverse direction: target → origin).
 *
 * Replaces WebApiController.UpdateEntityRelationRecordReverse (line 2305).
 * POST api/v3/en_US/record/relation/reverse →
 * POST /entity-management/relations/records/reverse
 *
 * Same attach/detach pattern as updateRelationRecord but operates from
 * the target field perspective (reverse direction).
 *
 * @param params - Reverse relation update parameters with attach/detach origin IDs
 * @returns Promise resolving on successful reverse relation update
 */
export async function updateRelationRecordReverse(
  params: RelationRecordReverseUpdateParams,
): Promise<ApiResponse<null>> {
  return post<null>('/entity-management/relations/records/reverse', params);
}

// ──────────────────────────────────────────────────────────────
// CSV Import Function
// ──────────────────────────────────────────────────────────────

/**
 * Import entity records from a CSV file.
 *
 * Replaces WebApiController.ImportEntityRecordsFromCsv (line ~2989).
 * POST api/v3/en_US/record/{entityName}/import →
 * POST /entity-management/records/{entityName}/import
 *
 * Uses multipart/form-data upload via the raw apiClient instance
 * (not the convenience helpers) to support File upload with the
 * correct Content-Type header.
 *
 * @param entityName - Name of the entity to import records into
 * @param file - CSV file to import
 * @returns Promise resolving to the import result
 */
export async function importRecordsFromCsv(
  entityName: string,
  file: File,
): Promise<ApiResponse<EntityRecord>> {
  const formData = new FormData();
  formData.append('file', file);

  const response = await apiClient.post<ApiResponse<EntityRecord>>(
    `/entity-management/records/${encodeURIComponent(entityName)}/import`,
    formData,
    {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    },
  );

  return response.data;
}
