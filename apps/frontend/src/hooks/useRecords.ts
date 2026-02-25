/**
 * Record CRUD TanStack Query Hooks
 *
 * TanStack Query 5 hooks for dynamic record CRUD operations (list, create,
 * read, update, delete), relation management, and CSV import/export against
 * the Entity Management microservice. Replaces:
 *
 *  - `RecordManager.cs`         — Central CRUD orchestrator with hooks,
 *                                  permission checks, relation-aware processing,
 *                                  field normalization, and file handling
 *  - `ImportExportManager.cs`   — CSV import/export pipelines with field
 *                                  mapping and validation
 *
 * Architecture:
 *  - Record hooks are **generic** — every hook accepts `entityName` as a
 *    parameter so a single set of hooks serves all 20+ entity types
 *  - Field value normalization (currency rounding, date timezone handling,
 *    GUID parsing, multiselect conversion, password hashing) happens
 *    **server-side** in the Entity Management Lambda handler
 *  - Relation-aware input parsing (`$relation_name.field_name` keys) is
 *    forwarded as-is; the server routes to the owning service
 *  - Permission enforcement (CanRead/CanCreate/CanUpdate/CanDelete) is
 *    server-side via 403 responses handled by the API client interceptor
 *  - Hook execution (pre/post CRUD) is replaced by domain event publishing
 *    on the server — the frontend never observes hook side-effects
 *
 * Query keys:
 *  - `['records', entityName, params]`                                — Paginated record list
 *  - `['records', entityName, id]`                                    — Single record
 *  - `['records', entityName, 'count', query]`                        — Record count
 *  - `['records', entityName, recordId, 'relations', relationName, …] — Related records
 *
 * @module hooks/useRecords
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type {
  EntityRecord,
  EntityRecordList,
  QueryResult,
  QueryResponse,
  QueryCountResponse,
  RecordResponse,
  RecordListResponse,
} from '../types/record';
import type { QueryObject, QuerySortObject } from '../types/filter';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Keys
// ---------------------------------------------------------------------------

/**
 * Centralised query-key factory for all record-domain caches.
 * Prevents key collisions and enables targeted invalidation.
 *
 * Mirrors the monolith's per-entity cache partitioning — each entity
 * name is a distinct cache namespace so that mutations on one entity
 * do not unnecessarily invalidate another entity's record list.
 *
 * The `all` key enables broad invalidation (e.g. after entity deletion),
 * while `list` / `detail` / `count` / `related` enable granular control.
 */
const RECORD_QUERY_KEYS = {
  /** Root key for all record queries — used for broadest invalidation */
  all: ['records'] as const,
  /** Paginated record list — matches RecordManager.Find(entityName, query) */
  list: (entityName: string, params?: RecordsParams) =>
    ['records', entityName, params] as const,
  /** Single record by ID — matches RecordManager.Find with id filter */
  detail: (entityName: string, id: string) =>
    ['records', entityName, id] as const,
  /** Record count — matches RecordManager.Count(entityName, query) */
  count: (entityName: string, query?: QueryObject | null) =>
    ['records', entityName, 'count', query] as const,
  /** Related records via relation — matches $relation_name query pattern */
  related: (
    entityName: string,
    recordId: string,
    relationName: string,
    params?: RelatedRecordsParams,
  ) =>
    ['records', entityName, recordId, 'relations', relationName, params] as const,
  /** Entity-scoped list prefix — used for invalidating all queries for an entity */
  entity: (entityName: string) => ['records', entityName] as const,
} as const;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * staleTime for record query hooks — 30 seconds (30 000 ms).
 *
 * Records are more volatile than entity metadata (which uses 5 min staleTime).
 * The monolith served fresh records on every request via direct DB queries
 * in RecordManager.Find(). The 30-second staleTime strikes a balance between
 * reducing unnecessary network calls and keeping data reasonably fresh.
 */
const RECORD_STALE_TIME_MS = 30 * 1000;

// ---------------------------------------------------------------------------
// Parameter Interfaces
// ---------------------------------------------------------------------------

/**
 * Query parameters for the {@link useRecords} hook.
 *
 * Maps to the query string parameters accepted by
 * `GET /v1/entities/{entityName}/records`:
 *  - `fields`  → comma-separated field selection (default: "*")
 *  - `query`   → recursive filter tree (JSON)
 *  - `sort`    → ordered list of sort directives
 *  - `skip`    → pagination offset
 *  - `limit`   → page size
 *
 * Replaces the C# `EntityQuery` DTO used by `RecordManager.Find()`.
 */
export interface RecordsParams {
  /** Comma-separated list of field names to include. Use "*" for all fields. */
  fields?: string;
  /** Recursive filter tree for WHERE clause conditions. */
  query?: QueryObject | null;
  /** Ordered sort directives applied to the result set. */
  sort?: QuerySortObject[];
  /** Number of records to skip (pagination offset). */
  skip?: number;
  /** Maximum number of records to return (page size). */
  limit?: number;
}

/**
 * Query parameters for the {@link useRelatedRecords} hook.
 *
 * Simplified parameter set for fetching records via a relation endpoint.
 * The relation itself defines the entity linkage — only result shaping
 * parameters are needed.
 */
export interface RelatedRecordsParams {
  /** Comma-separated list of field names to include. Use "*" for all fields. */
  fields?: string;
  /** Number of records to skip (pagination offset). */
  skip?: number;
  /** Maximum number of records to return (page size). */
  limit?: number;
}

// ---------------------------------------------------------------------------
// Mutation Variable Interfaces
// ---------------------------------------------------------------------------

/**
 * Variables for the {@link useCreateRecord} mutation.
 *
 * Replaces `RecordManager.CreateRecord(entityName, record)` which:
 *  1. Resolved entity metadata to validate field names/types
 *  2. Normalised values per field type (ExtractFieldValue)
 *  3. Processed relation-prefixed keys (`$relation_name.field_name`)
 *  4. Executed pre-create hooks
 *  5. Persisted via DbRecordRepository
 *  6. Executed post-create hooks → SNS domain events
 */
interface CreateRecordVariables {
  /** Target entity name (e.g. "contact", "account", "task"). */
  entityName: string;
  /**
   * Record data as a dynamic key-value object. Supports relation-prefixed
   * keys (`$relation_name.field_name`) for cross-entity field assignment.
   */
  data: EntityRecord;
}

/**
 * Variables for the {@link useUpdateRecord} mutation.
 *
 * Replaces `RecordManager.UpdateRecord(entityName, record)` which used
 * merge semantics — only supplied fields are updated; unspecified fields
 * retain their current values.
 */
interface UpdateRecordVariables {
  /** Target entity name. */
  entityName: string;
  /** Record ID (GUID string) to update. */
  id: string;
  /** Partial record update payload — only changed fields required. */
  data: EntityRecord;
}

/**
 * Variables for the {@link useDeleteRecord} mutation.
 *
 * Replaces `RecordManager.DeleteRecord(entityName, recordId)` which:
 *  1. Enforced delete permission
 *  2. Ran pre-delete hooks
 *  3. Cleaned up file fields
 *  4. Deleted the record
 *  5. Ran post-delete hooks → SNS domain events
 */
interface DeleteRecordVariables {
  /** Target entity name. */
  entityName: string;
  /** Record ID (GUID string) to delete. */
  id: string;
}

/**
 * Variables for the {@link useCreateManyToManyRelation} mutation.
 *
 * Replaces `RecordManager.CreateRelationManyToManyRecord(relationId, originValue, targetValue)`
 * which created a bridge record in the M2M junction table with pre/post hooks.
 */
interface CreateManyToManyRelationVariables {
  /** Relation ID (GUID string) identifying the M2M relation. */
  relationId: string;
  /** Origin record ID (GUID string) — the "left" side of the relation. */
  originId: string;
  /** Target record ID (GUID string) — the "right" side of the relation. */
  targetId: string;
}

/**
 * Variables for the {@link useRemoveManyToManyRelation} mutation.
 *
 * Replaces `RecordManager.RemoveRelationManyToManyRecord(relationId, originValue, targetValue)`
 * which deleted the bridge record from the M2M junction table with pre/post hooks.
 */
interface RemoveManyToManyRelationVariables {
  /** Relation ID (GUID string) identifying the M2M relation. */
  relationId: string;
  /** Origin record ID (GUID string) to disconnect. */
  originId: string;
  /** Target record ID (GUID string) to disconnect. */
  targetId: string;
}

/**
 * Variables for the {@link useImportRecords} mutation.
 *
 * Replaces `ImportExportManager.ImportEntityRecordsFromCsv(entityName, fileTempPath)`.
 * The monolith expected a server-local file path (from a prior upload);
 * the microservice accepts a multipart form upload directly.
 */
interface ImportRecordsVariables {
  /** Target entity name for the import. */
  entityName: string;
  /** CSV file to upload. */
  file: File;
  /**
   * Optional field mapping — keys are CSV column headers, values are
   * entity field names. When omitted, CSV headers must match field names.
   */
  fieldMapping?: Record<string, string>;
}

/**
 * Variables for the {@link useExportRecords} mutation.
 *
 * Replaces `ImportExportManager` CSV export pipeline. The microservice
 * generates the CSV server-side and returns a download URL or inline data.
 */
interface ExportRecordsVariables {
  /** Target entity name for the export. */
  entityName: string;
  /** List of field names to include in the export. Omit for all fields. */
  fields?: string[];
  /** Optional filter to restrict exported records. */
  query?: QueryObject | null;
  /** Export format (default: "csv"). */
  format?: string;
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
 * `ErrorModel[]` with `{ key, value, message }` per error, which is
 * structurally identical to the client's `ApiErrorItem[]`.
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

/**
 * Serialises {@link RecordsParams} into query-string-ready key-value pairs
 * for the `GET /v1/entities/{entityName}/records` endpoint.
 *
 * Complex nested objects (query, sort) are JSON-stringified so the API
 * Gateway can forward them as query parameters to the Lambda handler,
 * which deserialises them back into structured objects.
 */
function buildRecordQueryParams(
  params?: RecordsParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.fields !== undefined && params.fields !== '') {
    queryParams['fields'] = params.fields;
  }
  if (params.query !== undefined && params.query !== null) {
    queryParams['query'] = JSON.stringify(params.query);
  }
  if (params.sort !== undefined && params.sort.length > 0) {
    queryParams['sort'] = JSON.stringify(params.sort);
  }
  if (params.skip !== undefined) {
    queryParams['skip'] = params.skip;
  }
  if (params.limit !== undefined) {
    queryParams['limit'] = params.limit;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

/**
 * Serialises {@link RelatedRecordsParams} into query-string-ready pairs.
 */
function buildRelatedQueryParams(
  params?: RelatedRecordsParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.fields !== undefined && params.fields !== '') {
    queryParams['fields'] = params.fields;
  }
  if (params.skip !== undefined) {
    queryParams['skip'] = params.skip;
  }
  if (params.limit !== undefined) {
    queryParams['limit'] = params.limit;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

// ---------------------------------------------------------------------------
// Record Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of records from an entity.
 *
 * Replaces `RecordManager.Find(entityName, query)` which:
 *  1. Validated entity existence and read permissions
 *  2. Delegated to `DbRecordRepository.Find()` with filter/sort/pagination
 *  3. Returned a `QueryResponse` with `QueryResult { fieldsMeta, data }`
 *  4. Executed pre/post-search hooks
 *
 * The Lambda handler performs all validation, permission checking, and field
 * metadata extraction. The frontend receives the fully formed result.
 *
 * API: `GET /v1/entities/{entityName}/records`
 * Query params: fields, query (JSON), sort (JSON), skip, limit
 * Response shape: {@link QueryResponse} `{ success, object: QueryResult }`
 *
 * @param entityName - Entity name to query (e.g. "contact", "account")
 * @param params     - Optional query parameters (fields, filters, sort, pagination)
 * @returns TanStack Query result with `QueryResult` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function ContactList() {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useRecords('contact', {
 *     fields: 'id,first_name,last_name,email',
 *     sort: [{ fieldName: 'last_name', sortType: QuerySortType.Ascending }],
 *     limit: 50,
 *     skip: 0,
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <DataTable fieldsMeta={data?.fieldsMeta} records={data?.data} />;
 * }
 * ```
 */
export function useRecords(entityName: string, params?: RecordsParams) {
  return useQuery<QueryResponse['object'], Error>({
    queryKey: RECORD_QUERY_KEYS.list(entityName, params),

    queryFn: async (): Promise<QueryResponse['object']> => {
      const response = await get<QueryResult>(
        `/entities/${encodeURIComponent(entityName)}/records`,
        buildRecordQueryParams(params),
      );
      assertApiSuccess(response, `Failed to fetch records for entity "${entityName}"`);

      if (!response.object) {
        throw new Error(`Record list response missing data for entity "${entityName}"`);
      }

      return response.object;
    },

    staleTime: RECORD_STALE_TIME_MS,

    // Only fetch when entityName is provided
    enabled: entityName.length > 0,
  });
}

/**
 * Fetches a single record by ID from an entity.
 *
 * Replaces `RecordManager.Find(entityName, query)` invoked with an ID
 * equality filter. The monolith used the same `Find` method for both list
 * and detail views, differing only in the query filter. The microservice
 * exposes a dedicated `/records/{id}` endpoint for single-record lookups.
 *
 * API: `GET /v1/entities/{entityName}/records/{id}`
 * Response shape: {@link RecordResponse} `{ success, object: EntityRecord }`
 *
 * @param entityName - Entity name
 * @param id         - Record ID (GUID string)
 * @returns TanStack Query result with `EntityRecord` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function ContactDetail({ id }: { id: string }) {
 *   const { data: record, isLoading, isError, error, refetch } = useRecord('contact', id);
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <RecordView record={record} />;
 * }
 * ```
 */
export function useRecord(entityName: string, id: string) {
  return useQuery<RecordResponse['object'], Error>({
    queryKey: RECORD_QUERY_KEYS.detail(entityName, id),

    queryFn: async (): Promise<RecordResponse['object']> => {
      const response = await get<EntityRecord>(
        `/entities/${encodeURIComponent(entityName)}/records/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to fetch record "${id}" for entity "${entityName}"`);

      if (!response.object) {
        throw new Error(`Record response missing data for "${entityName}/${id}"`);
      }

      return response.object;
    },

    staleTime: RECORD_STALE_TIME_MS,

    // Only fetch when both entityName and id are provided
    enabled: entityName.length > 0 && id.length > 0,
  });
}

/**
 * Fetches the count of records matching an optional filter.
 *
 * Replaces `RecordManager.Count(entityName, query)` which counted records
 * via a PostgreSQL COUNT(*) query on the `rec_{entityName}` table with
 * optional WHERE clause from the query filter tree.
 *
 * API: `GET /v1/entities/{entityName}/records/count`
 * Query params: query (JSON filter, optional)
 * Response shape: {@link QueryCountResponse} `{ success, object: number }`
 *
 * @param entityName - Entity name
 * @param query      - Optional filter tree to restrict the count
 * @returns TanStack Query result with `number` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function ActiveContactCount() {
 *   const { data: count, isLoading } = useRecordCount('contact', {
 *     queryType: QueryType.EQ,
 *     fieldName: 'status',
 *     fieldValue: 'active',
 *     subQueries: [],
 *   });
 *   return <Badge>{isLoading ? '…' : count}</Badge>;
 * }
 * ```
 */
export function useRecordCount(entityName: string, query?: QueryObject | null) {
  return useQuery<QueryCountResponse['object'], Error>({
    queryKey: RECORD_QUERY_KEYS.count(entityName, query),

    queryFn: async (): Promise<QueryCountResponse['object']> => {
      const queryParams: Record<string, unknown> | undefined =
        query !== undefined && query !== null
          ? { query: JSON.stringify(query) }
          : undefined;

      const response = await get<number>(
        `/entities/${encodeURIComponent(entityName)}/records/count`,
        queryParams,
      );
      assertApiSuccess(
        response,
        `Failed to fetch record count for entity "${entityName}"`,
      );

      if (response.object === undefined || response.object === null) {
        throw new Error(
          `Record count response missing data for entity "${entityName}"`,
        );
      }

      return response.object;
    },

    staleTime: RECORD_STALE_TIME_MS,

    enabled: entityName.length > 0,
  });
}

/**
 * Fetches records related to a specific record via a named relation.
 *
 * Replaces the monolith's `$relation_name` query pattern from
 * `RecordManager.Find()` where callers could navigate relations via
 * `$relation_name.field_name` keys in the query. The microservice
 * exposes a dedicated relation endpoint for cleaner API design.
 *
 * API: `GET /v1/entities/{entityName}/records/{recordId}/relations/{relationName}`
 * Query params: fields, skip, limit
 * Response shape: {@link RecordListResponse} `{ success, object: EntityRecordList }`
 *
 * @param entityName   - Source entity name
 * @param recordId     - Source record ID (GUID string)
 * @param relationName - Name of the relation to traverse
 * @param params       - Optional result shaping parameters
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function ContactAddresses({ contactId }: { contactId: string }) {
 *   const { data, isLoading, isFetching, refetch } = useRelatedRecords(
 *     'contact', contactId, 'contact_address', { limit: 10 },
 *   );
 *   if (isLoading) return <Spinner />;
 *   return <DataTable records={data?.records} totalCount={data?.totalCount} />;
 * }
 * ```
 */
export function useRelatedRecords(
  entityName: string,
  recordId: string,
  relationName: string,
  params?: RelatedRecordsParams,
) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: RECORD_QUERY_KEYS.related(
      entityName,
      recordId,
      relationName,
      params,
    ),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const url = `/entities/${encodeURIComponent(entityName)}/records/${encodeURIComponent(recordId)}/relations/${encodeURIComponent(relationName)}`;
      const response = await get<EntityRecordList>(
        url,
        buildRelatedQueryParams(params),
      );
      assertApiSuccess(
        response,
        `Failed to fetch related records via "${relationName}" for "${entityName}/${recordId}"`,
      );

      if (!response.object) {
        throw new Error(
          `Related records response missing data for "${entityName}/${recordId}/${relationName}"`,
        );
      }

      return response.object;
    },

    staleTime: RECORD_STALE_TIME_MS,

    // Only fetch when all required parameters are provided
    enabled:
      entityName.length > 0 &&
      recordId.length > 0 &&
      relationName.length > 0,
  });
}

// ---------------------------------------------------------------------------
// Record Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new record for the specified entity.
 *
 * Replaces `RecordManager.CreateRecord(entityName, record)` which executed
 * the full create pipeline:
 *  1. Entity metadata resolution and field validation
 *  2. Field value normalisation per type (ExtractFieldValue)
 *  3. Relation-prefixed property processing (`$relation_name.field_name`)
 *  4. Pre-create hook execution (now API-level validation in Lambda)
 *  5. Record persistence via DbRecordRepository
 *  6. Post-create hook execution (now SNS domain event publishing)
 *
 * The mutation body is an {@link EntityRecord} (dynamic key-value). Relation-
 * prefixed keys are forwarded to the server for cross-entity processing.
 *
 * API: `POST /v1/entities/{entityName}/records`
 * Body: {@link EntityRecord}
 * Response shape: {@link RecordResponse} `{ success, object: EntityRecord }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateContactForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateRecord();
 *   const handleSubmit = (formData: EntityRecord) => {
 *     mutate({ entityName: 'contact', data: formData });
 *   };
 * }
 * ```
 */
export function useCreateRecord() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, CreateRecordVariables>({
    mutationFn: async ({
      entityName,
      data,
    }: CreateRecordVariables): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>(
        `/entities/${encodeURIComponent(entityName)}/records`,
        data,
      );
      assertApiSuccess(
        response,
        `Failed to create record for entity "${entityName}"`,
      );

      if (!response.object) {
        throw new Error(
          `Create record response missing data for entity "${entityName}"`,
        );
      }

      return response.object;
    },

    onSuccess: (_data, variables) => {
      // Invalidate all record list/count queries for this entity so the
      // new record appears in lists and counts are refreshed
      void queryClient.invalidateQueries({
        queryKey: RECORD_QUERY_KEYS.entity(variables.entityName),
      });
    },
  });
}

/**
 * Updates an existing record with partial data (merge semantics).
 *
 * Replaces `RecordManager.UpdateRecord(entityName, record)` which used
 * merge semantics — only supplied fields are updated; unspecified fields
 * retain their current values. The server normalises file paths, currency
 * values, and other type-specific transformations.
 *
 * Implements **optimistic updates** for single-record detail views:
 *  - `onMutate`: Cancels in-flight queries, snapshots previous data,
 *    merges new data into the cache immediately
 *  - `onError`: Rolls back to the snapshot on failure
 *  - `onSettled`: Invalidates to ensure consistency after mutation completes
 *
 * API: `PUT /v1/entities/{entityName}/records/{id}`
 * Body: partial {@link EntityRecord}
 * Response shape: {@link RecordResponse} `{ success, object: EntityRecord }`
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditContactForm({ entityName, recordId }: Props) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateRecord();
 *   const handleSubmit = (changes: EntityRecord) => {
 *     mutate({ entityName, id: recordId, data: changes });
 *   };
 * }
 * ```
 */
export function useUpdateRecord() {
  const queryClient = useQueryClient();

  return useMutation<
    RecordResponse['object'],
    Error,
    UpdateRecordVariables,
    { previousRecord?: RecordResponse['object'] }
  >({
    mutationFn: async ({
      entityName,
      id,
      data,
    }: UpdateRecordVariables): Promise<RecordResponse['object']> => {
      const response = await put<EntityRecord>(
        `/entities/${encodeURIComponent(entityName)}/records/${encodeURIComponent(id)}`,
        data,
      );
      assertApiSuccess(
        response,
        `Failed to update record "${id}" for entity "${entityName}"`,
      );

      if (!response.object) {
        throw new Error(
          `Update record response missing data for "${entityName}/${id}"`,
        );
      }

      return response.object;
    },

    onMutate: async (variables) => {
      // Cancel any in-flight queries for this specific record to prevent
      // them from overwriting our optimistic update
      const detailKey = RECORD_QUERY_KEYS.detail(
        variables.entityName,
        variables.id,
      );
      await queryClient.cancelQueries({ queryKey: detailKey });

      // Snapshot the previous record data for rollback on error
      const previousRecord = queryClient.getQueryData<
        RecordResponse['object']
      >(detailKey);

      // Optimistically merge the new data into the cached record
      if (previousRecord) {
        queryClient.setQueryData<RecordResponse['object']>(detailKey, {
          ...previousRecord,
          ...variables.data,
        });
      }

      return { previousRecord };
    },

    onError: (_error, variables, context) => {
      // Roll back the optimistic update on failure
      if (context?.previousRecord) {
        queryClient.setQueryData(
          RECORD_QUERY_KEYS.detail(variables.entityName, variables.id),
          context.previousRecord,
        );
      }
    },

    onSettled: (_data, _error, variables) => {
      // Always invalidate after mutation settles (success or error) to
      // ensure the cache is consistent with the server state
      void queryClient.invalidateQueries({
        queryKey: RECORD_QUERY_KEYS.entity(variables.entityName),
      });
    },
  });
}

/**
 * Deletes a record from an entity.
 *
 * Replaces `RecordManager.DeleteRecord(entityName, recordId)` which:
 *  1. Enforced delete permission (EntityPermission.CanDelete)
 *  2. Executed pre-delete hooks (now API-level validation)
 *  3. Cleaned up file fields (file references removed server-side)
 *  4. Deleted the record from `rec_{entityName}` table
 *  5. Executed post-delete hooks (now SNS domain events)
 *
 * API: `DELETE /v1/entities/{entityName}/records/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteRecordButton({ entityName, recordId }: Props) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteRecord();
 *   return (
 *     <button onClick={() => mutate({ entityName, id: recordId })} disabled={isPending}>
 *       Delete
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteRecord() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, DeleteRecordVariables>({
    mutationFn: async ({
      entityName,
      id,
    }: DeleteRecordVariables): Promise<void> => {
      const response = await del(
        `/entities/${encodeURIComponent(entityName)}/records/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(
        response,
        `Failed to delete record "${id}" for entity "${entityName}"`,
      );
    },

    onSuccess: (_data, variables) => {
      // Invalidate all record queries for this entity — the deleted record
      // must disappear from lists and the count must decrease
      void queryClient.invalidateQueries({
        queryKey: RECORD_QUERY_KEYS.entity(variables.entityName),
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Relation Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a many-to-many relation bridge record between two records.
 *
 * Replaces `RecordManager.CreateRelationManyToManyRecord(relationId, originValue, targetValue)`
 * which validated the relation, executed pre-create M2M hooks within a
 * transaction, inserted the bridge record, and executed post-create hooks.
 *
 * API: `POST /v1/relations/{relationId}/records`
 * Body: `{ originId, targetId }`
 * Response: {@link BaseResponseModel} success envelope
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function LinkContactToAccount({ contactId, accountId, relationId }: Props) {
 *   const { mutate, isPending, isSuccess, data, reset } = useCreateManyToManyRelation();
 *   const handleLink = () => {
 *     mutate({ relationId, originId: contactId, targetId: accountId });
 *   };
 * }
 * ```
 */
export function useCreateManyToManyRelation() {
  const queryClient = useQueryClient();

  return useMutation<
    BaseResponseModel,
    Error,
    CreateManyToManyRelationVariables
  >({
    mutationFn: async ({
      relationId,
      originId,
      targetId,
    }: CreateManyToManyRelationVariables): Promise<BaseResponseModel> => {
      const response = await post<undefined>(
        `/relations/${encodeURIComponent(relationId)}/records`,
        { originId, targetId },
      );
      assertApiSuccess(
        response,
        `Failed to create M2M relation record for relation "${relationId}"`,
      );

      return {
        success: response.success,
        errors: response.errors ?? [],
        message: response.message,
        timestamp: response.timestamp,
        hash: response.hash ?? null,
        accessWarnings: [],
      };
    },

    onSuccess: () => {
      // M2M relation changes can affect records across multiple entities,
      // so invalidate all record queries broadly
      void queryClient.invalidateQueries({
        queryKey: RECORD_QUERY_KEYS.all,
      });
    },
  });
}

/**
 * Removes a many-to-many relation bridge record between two records.
 *
 * Replaces `RecordManager.RemoveRelationManyToManyRecord(relationId, originValue, targetValue)`
 * which validated the relation, executed pre-delete M2M hooks, deleted the
 * bridge record, and executed post-delete hooks.
 *
 * API: `DELETE /v1/relations/{relationId}/records/{originId}/{targetId}`
 * Response: {@link BaseResponseModel} success envelope
 *
 * Note: Origin and target IDs are encoded in the URL path since the `del`
 * client helper does not support request bodies on DELETE requests.
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function UnlinkContactFromAccount({ contactId, accountId, relationId }: Props) {
 *   const { mutate, isPending, isSuccess, reset } = useRemoveManyToManyRelation();
 *   const handleUnlink = () => {
 *     mutate({ relationId, originId: contactId, targetId: accountId });
 *   };
 * }
 * ```
 */
export function useRemoveManyToManyRelation() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, RemoveManyToManyRelationVariables>({
    mutationFn: async ({
      relationId,
      originId,
      targetId,
    }: RemoveManyToManyRelationVariables): Promise<void> => {
      const response = await del(
        `/relations/${encodeURIComponent(relationId)}/records/${encodeURIComponent(originId)}/${encodeURIComponent(targetId)}`,
      );
      assertApiSuccess(
        response,
        `Failed to remove M2M relation record for relation "${relationId}"`,
      );
    },

    onSuccess: () => {
      // M2M relation removal can affect records across multiple entities
      void queryClient.invalidateQueries({
        queryKey: RECORD_QUERY_KEYS.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Import / Export Hooks
// ---------------------------------------------------------------------------

/**
 * Imports records from a CSV file into an entity.
 *
 * Replaces `ImportExportManager.ImportEntityRecordsFromCsv(entityName, fileTempPath)`
 * which:
 *  1. Validated the file path and entity existence
 *  2. Read the CSV file from PostgreSQL large-object storage
 *  3. Parsed CSV columns (first column "id" for update-or-create semantics)
 *  4. For each row: if `id` is "null" → create; otherwise → update
 *  5. Processed relation-prefixed columns (`$relation_name.field_name`)
 *
 * The microservice accepts a multipart form upload with the CSV file and
 * optional field mapping. The server handles parsing, validation, and
 * record creation/update.
 *
 * API: `POST /v1/entities/{entityName}/records/import`
 * Body: FormData with CSV file + optional field mapping JSON
 * Response: {@link BaseResponseModel} with import summary
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function ImportContactsForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useImportRecords();
 *   const handleFileUpload = (file: File) => {
 *     mutate({
 *       entityName: 'contact',
 *       file,
 *       fieldMapping: { 'First Name': 'first_name', 'Last Name': 'last_name' },
 *     });
 *   };
 * }
 * ```
 */
export function useImportRecords() {
  const queryClient = useQueryClient();

  return useMutation<BaseResponseModel, Error, ImportRecordsVariables>({
    mutationFn: async ({
      entityName,
      file,
      fieldMapping,
    }: ImportRecordsVariables): Promise<BaseResponseModel> => {
      const formData = new FormData();
      formData.append('file', file);
      if (fieldMapping) {
        formData.append('fieldMapping', JSON.stringify(fieldMapping));
      }

      const response = await post<undefined>(
        `/entities/${encodeURIComponent(entityName)}/records/import`,
        formData,
      );
      assertApiSuccess(
        response,
        `Failed to import records for entity "${entityName}"`,
      );

      return {
        success: response.success,
        errors: response.errors ?? [],
        message: response.message,
        timestamp: response.timestamp,
        hash: response.hash ?? null,
        accessWarnings: [],
      };
    },

    onSuccess: (_data, variables) => {
      // Import may have created/updated many records — invalidate all
      // record queries for this entity
      void queryClient.invalidateQueries({
        queryKey: RECORD_QUERY_KEYS.entity(variables.entityName),
      });
    },
  });
}

/**
 * Exports records from an entity to a downloadable CSV file.
 *
 * Replaces the `ImportExportManager` CSV export pipeline which:
 *  1. Queried records using RecordManager.Find()
 *  2. Generated CSV output with CsvHelper
 *  3. Returned the file as an HTTP response
 *
 * The microservice generates the CSV server-side and returns either a
 * presigned S3 download URL or inline base64-encoded data.
 *
 * API: `POST /v1/entities/{entityName}/records/export`
 * Body: `{ fields?, query?, format? }`
 * Response: {@link BaseResponseModel} with download URL or inline data
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function ExportContactsButton() {
 *   const { mutate, isPending, isSuccess, data, reset } = useExportRecords();
 *   const handleExport = () => {
 *     mutate({
 *       entityName: 'contact',
 *       fields: ['id', 'first_name', 'last_name', 'email'],
 *       format: 'csv',
 *     });
 *   };
 * }
 * ```
 */
export function useExportRecords() {
  return useMutation<BaseResponseModel, Error, ExportRecordsVariables>({
    mutationFn: async ({
      entityName,
      fields,
      query,
      format,
    }: ExportRecordsVariables): Promise<BaseResponseModel> => {
      const body: Record<string, unknown> = {};

      if (fields !== undefined && fields.length > 0) {
        body['fields'] = fields;
      }
      if (query !== undefined && query !== null) {
        body['query'] = query;
      }
      if (format !== undefined && format !== '') {
        body['format'] = format;
      }

      const response = await post<undefined>(
        `/entities/${encodeURIComponent(entityName)}/records/export`,
        body,
      );
      assertApiSuccess(
        response,
        `Failed to export records for entity "${entityName}"`,
      );

      return {
        success: response.success,
        errors: response.errors ?? [],
        message: response.message,
        timestamp: response.timestamp,
        hash: response.hash ?? null,
        accessWarnings: [],
      };
    },

    // Export is a read-only operation — no cache invalidation needed.
  });
}
