/**
 * Record-level TypeScript interfaces for the WebVella ERP frontend.
 *
 * This file defines the core record data types used throughout the
 * application — dynamic key-value records, record list collections,
 * query results with field metadata, API response wrappers, and the
 * query request DTO.
 *
 * Converted from C# DTOs:
 *   - WebVella.Erp/Api/Models/EntityRecord.cs
 *   - WebVella.Erp/Api/Models/EntityRecordList.cs
 *   - WebVella.Erp/Api/Models/QueryResult.cs
 *   - WebVella.Erp/Api/Models/QueryResponse.cs
 *   - WebVella.Erp/Api/Models/QueryCountResponse.cs
 *   - WebVella.Erp/Api/Models/EntityQuery.cs
 *   - WebVella.Erp/Api/Models/BaseModels.cs (BaseResponseModel)
 */

import type { BaseResponseModel } from './common';
import type { Field } from './entity';
import type { QueryObject, QuerySortObject } from './filter';

// ---------------------------------------------------------------------------
// Record Models
// ---------------------------------------------------------------------------

/**
 * A dynamic key-value record whose shape is determined at runtime
 * by the entity's field definitions.
 *
 * Mirrors C# `EntityRecord` from EntityRecord.cs, which extends `Expando`
 * (a `DynamicObject` / `IDictionary<string, object>` hybrid). In TypeScript
 * this is modelled as an interface with an index signature so that
 * arbitrary fields are accessible without casting while the well-known
 * `id` property receives a typed declaration.
 *
 * Keys are field names (e.g. "first_name", "email") and values can be
 * any JSON-serialisable type (string, number, boolean, GUID string,
 * array, nested object, or null).
 */
export interface EntityRecord {
  /** Index signature allowing dynamic field access. */
  [key: string]: unknown;
  /**
   * Primary record identifier (GUID as string).
   *
   * Nearly every entity record has an `id` field, but it is technically
   * optional because the entity definition controls which fields exist.
   */
  id?: string;
}

// ---------------------------------------------------------------------------
// Record List
// ---------------------------------------------------------------------------

/**
 * A paginated collection of {@link EntityRecord} items with a total count.
 *
 * Mirrors C# `EntityRecordList` from EntityRecordList.cs, which extends
 * `List<EntityRecord>` and adds a `total_count` property. In TypeScript
 * we model this as a plain object rather than extending Array because
 * TypeScript does not cleanly support adding properties to array types.
 *
 * The `totalCount` property corresponds to the C# JSON wire name
 * "total_count" (via `[JsonProperty(PropertyName = "total_count")]`).
 */
export interface EntityRecordList {
  /** The array of records in this page of results. */
  records: EntityRecord[];
  /**
   * Total number of records matching the query across all pages.
   *
   * @jsonProperty total_count
   */
  totalCount: number;
}

// ---------------------------------------------------------------------------
// Query Result
// ---------------------------------------------------------------------------

/**
 * Contains the data payload and field metadata returned by a record query.
 *
 * Mirrors C# `QueryResult` from QueryResult.cs.
 * JSON property names: fieldsMeta, data.
 */
export interface QueryResult {
  /**
   * Metadata for every field included in the result set.
   *
   * Provides display labels, field types, and validation rules so the
   * frontend can dynamically render field components without a separate
   * metadata lookup.
   */
  fieldsMeta: Field[];
  /** The array of records returned by the query. */
  data: EntityRecord[];
}

// ---------------------------------------------------------------------------
// API Response Wrappers
// ---------------------------------------------------------------------------

/**
 * API response envelope wrapping a {@link QueryResult}.
 *
 * Mirrors C# `QueryResponse` from QueryResponse.cs, which extends
 * `BaseResponseModel` and adds an `object` property typed as
 * `QueryResult`.
 *
 * Inherits: timestamp, success, message, hash, errors, accessWarnings.
 */
export interface QueryResponse extends BaseResponseModel {
  /** The query result payload containing field metadata and record data. */
  object: QueryResult;
}

/**
 * API response envelope wrapping a record count (number).
 *
 * Mirrors C# `QueryCountResponse` from QueryCountResponse.cs, which
 * extends `BaseResponseModel` and adds an `object` property typed as
 * `long` (mapped to `number` in TypeScript).
 *
 * Inherits: timestamp, success, message, hash, errors, accessWarnings.
 */
export interface QueryCountResponse extends BaseResponseModel {
  /** The total count of records matching the query. */
  object: number;
}

/**
 * API response envelope wrapping a single {@link EntityRecord}.
 *
 * Used for create, read, and update operations that return one record.
 *
 * Inherits: timestamp, success, message, hash, errors, accessWarnings.
 */
export interface RecordResponse extends BaseResponseModel {
  /** The single record payload. */
  object: EntityRecord;
}

/**
 * API response envelope wrapping an {@link EntityRecordList}.
 *
 * Used for list/search operations that return multiple records with
 * pagination metadata.
 *
 * Inherits: timestamp, success, message, hash, errors, accessWarnings.
 */
export interface RecordListResponse extends BaseResponseModel {
  /** The record list payload including records and total count. */
  object: EntityRecordList;
}

// ---------------------------------------------------------------------------
// Query Request DTO
// ---------------------------------------------------------------------------

/**
 * Request DTO for querying entity records.
 *
 * Mirrors C# `EntityQuery` from EntityQuery.cs. Only the JSON-serialised
 * properties are included — server-only properties marked with
 * `[JsonIgnore]` (e.g. `OverwriteArgs`) are intentionally excluded.
 *
 * Usage:
 * ```ts
 * const query: EntityQuery = {
 *   entityName: 'contact',
 *   fields: 'id,first_name,last_name,email',
 *   query: { queryType: QueryType.EQ, fieldName: 'status', fieldValue: 'active', subQueries: [] },
 *   sort: [{ fieldName: 'last_name', sortType: QuerySortType.Ascending }],
 *   skip: 0,
 *   limit: 50,
 * };
 * ```
 */
export interface EntityQuery {
  /**
   * Name of the entity to query (e.g. "contact", "account").
   *
   * Required — the C# constructor throws if this is null or whitespace.
   */
  entityName: string;
  /**
   * Comma-separated list of field names to include in the result.
   *
   * Use `"*"` to select all fields (the default in the C# constructor).
   */
  fields: string;
  /**
   * Recursive filter tree defining the WHERE clause.
   *
   * Null means no filtering (return all records).
   */
  query: QueryObject | null;
  /**
   * Ordered list of sort directives applied to the result set.
   *
   * An empty array or null means no explicit sort order.
   */
  sort: QuerySortObject[];
  /**
   * Number of records to skip (offset) for pagination.
   *
   * Undefined / null means no skip (start from the first record).
   */
  skip?: number;
  /**
   * Maximum number of records to return.
   *
   * Undefined / null means no limit (return all matching records).
   */
  limit?: number;
}
