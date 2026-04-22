/**
 * Search Operations API Module
 *
 * Provides typed API functions for search operations against the Entity Management
 * service. Replaces the monolith's WebApiController.cs GetQuickSearch endpoint
 * (lines 3020–3250) and routes to `/entity-management/search/*`.
 *
 * Three search patterns are supported:
 *  - quickSearch: Primary quick-search with all 12 parameters from the monolith
 *  - fullTextSearch: Full-text search across multiple entities
 *  - typeaheadSearch: Typeahead/autocomplete for select/lookup fields
 */

import { get } from '../client';
import type { ApiResponse } from '../client';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Route prefix for all search endpoints
// ---------------------------------------------------------------------------
const SEARCH_BASE = '/entity-management/search';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Parameters for the primary quick-search endpoint.
 *
 * Maps 1:1 to the monolith's GetQuickSearch query-string signature
 * (WebApiController.cs lines 3022-3023):
 *   query, entityName, lookupFieldsCsv, sortField, sortType, returnFieldsCsv,
 *   matchMethod, matchAllFields, skipRecords, limitRecords, findType,
 *   forceFiltersCsv
 *
 * Match method logic (lines 3043-3137):
 *   - "EQ"         → EntityQuery.QueryEQ per lookup field
 *   - "contains"   → EntityQuery.QueryContains
 *   - "startsWith" → EntityQuery.QueryStartsWith
 *   - "FTS"        → EntityQuery.QueryFTS (full-text search)
 *
 * Multiple lookup fields are combined with OR (default) or AND when
 * matchAllFields is true.
 *
 * Force filters format (lines 3141-3191):
 *   "fieldName1:dataType1:eqValue1,fieldName2:dataType2:eqValue2"
 *   Supported data types: guid, bool, datetime, int, string
 *   Each parsed as QueryEQ, combined with AND, then AND'd with the match filter.
 *
 * Find type logic (lines 3211-3229):
 *   - "records"            → returns { records }
 *   - "count"              → returns { count }
 *   - "records-and-count"  → returns both
 */
export interface QuickSearchParams {
  /** Search text (required, default empty string in monolith) */
  query: string;

  /** Target entity name — REQUIRED */
  entityName: string;

  /** Comma-separated field names to search in — REQUIRED */
  lookupFieldsCsv: string;

  /** Comma-separated fields to include in response — REQUIRED */
  returnFieldsCsv: string;

  /** Field name to sort results by */
  sortField?: string;

  /** Sort direction — defaults to 'asc' */
  sortType?: 'asc' | 'desc';

  /**
   * Match method applied to each lookup field.
   *  - "EQ"         – exact equality
   *  - "contains"   – substring match
   *  - "startsWith" – prefix match
   *  - "FTS"        – PostgreSQL full-text search
   * Defaults to "EQ".
   */
  matchMethod?: 'EQ' | 'contains' | 'startsWith' | 'FTS';

  /**
   * When true, all lookup-field filters are combined with AND.
   * When false (default), they are combined with OR.
   */
  matchAllFields?: boolean;

  /** Pagination offset — defaults to 0 */
  skipRecords?: number;

  /** Page size — defaults to 5 */
  limitRecords?: number;

  /**
   * Determines the shape of the result:
   *  - "records"           – only EntityRecord[]
   *  - "count"             – only the total count
   *  - "records-and-count" – both records and count
   * Defaults to "records".
   */
  findType?: 'records' | 'count' | 'records-and-count';

  /**
   * Additional forced equality filters applied with AND.
   * Format: "fieldName1:dataType1:eqValue1,fieldName2:dataType2:eqValue2"
   * Supported data types: guid, bool, datetime, int, string.
   */
  forceFiltersCsv?: string;
}

/**
 * Result envelope returned by the quick-search endpoint.
 *
 * Depending on the `findType` parameter the server may populate one or both
 * properties:
 *  - findType "records"           → records populated, count omitted
 *  - findType "count"             → count populated, records omitted
 *  - findType "records-and-count" → both populated
 */
export interface QuickSearchResult {
  /** Array of matching entity records (present when findType includes records) */
  records?: EntityRecord[];

  /** Total number of matching records (present when findType includes count) */
  count?: number;
}

/**
 * Parameters for the typeahead/autocomplete search endpoint.
 *
 * Used by select and lookup field components to provide real-time suggestions
 * as the user types.
 */
export interface TypeaheadSearchParams {
  /** Search text entered by the user */
  query: string;

  /** Target entity to search within */
  entityName: string;

  /** Single field name to match against */
  lookupField: string;

  /** Comma-separated list of fields to include in the response */
  returnFields?: string;

  /** Maximum number of suggestions to return */
  limit?: number;
}

// ---------------------------------------------------------------------------
// Full-text search result type (array of EntityRecord)
// ---------------------------------------------------------------------------

/**
 * Result envelope for full-text search, which returns matching records across
 * one or more entity types.
 */
export interface FullTextSearchResult {
  /** Array of matching entity records */
  records: EntityRecord[];

  /** Total number of matches found */
  totalCount: number;
}

// ---------------------------------------------------------------------------
// API Functions
// ---------------------------------------------------------------------------

/**
 * Performs a quick-search against the Entity Management service.
 *
 * Replaces the monolith's `GetQuickSearch` endpoint (WebApiController.cs
 * lines 3020–3250). All 12 original query parameters are forwarded to the
 * service which replicates the match-method, force-filter, and find-type
 * logic server-side.
 *
 * @param params - QuickSearchParams containing search criteria
 * @returns Promise resolving to the API response envelope with QuickSearchResult
 *
 * @example
 * ```ts
 * const result = await quickSearch({
 *   query: 'Acme',
 *   entityName: 'account',
 *   lookupFieldsCsv: 'name,email',
 *   returnFieldsCsv: 'id,name,email,phone',
 *   matchMethod: 'contains',
 *   limitRecords: 10,
 *   findType: 'records-and-count',
 * });
 * if (result.success && result.object) {
 *   console.log(result.object.records, result.object.count);
 * }
 * ```
 */
export async function quickSearch(
  params: QuickSearchParams,
): Promise<ApiResponse<QuickSearchResult>> {
  // Build the query-param map, omitting undefined optional values so that
  // the server applies its own defaults (matching monolith behavior).
  const queryParams: Record<string, unknown> = {
    query: params.query,
    entityName: params.entityName,
    lookupFieldsCsv: params.lookupFieldsCsv,
    returnFieldsCsv: params.returnFieldsCsv,
  };

  if (params.sortField !== undefined) {
    queryParams.sortField = params.sortField;
  }
  if (params.sortType !== undefined) {
    queryParams.sortType = params.sortType;
  }
  if (params.matchMethod !== undefined) {
    queryParams.matchMethod = params.matchMethod;
  }
  if (params.matchAllFields !== undefined) {
    queryParams.matchAllFields = params.matchAllFields;
  }
  if (params.skipRecords !== undefined) {
    queryParams.skipRecords = params.skipRecords;
  }
  if (params.limitRecords !== undefined) {
    queryParams.limitRecords = params.limitRecords;
  }
  if (params.findType !== undefined) {
    queryParams.findType = params.findType;
  }
  if (params.forceFiltersCsv !== undefined) {
    queryParams.forceFiltersCsv = params.forceFiltersCsv;
  }

  return get<QuickSearchResult>(`${SEARCH_BASE}/quick`, queryParams);
}

/**
 * Performs a full-text search across one or more entities.
 *
 * This endpoint replaces the monolith's SearchManager-based FTS operations
 * (SearchManager.cs) which used PostgreSQL `system_search` table for
 * tsvector-based full-text matching. In the target architecture the Entity
 * Management service provides an equivalent FTS capability backed by
 * DynamoDB secondary indexes or an internal search adapter.
 *
 * @param query    - The search text to match
 * @param entities - Optional array of entity names to restrict the search to.
 *                   When omitted the search spans all searchable entities.
 * @returns Promise resolving to the API response envelope with EntityRecord[]
 *
 * @example
 * ```ts
 * const result = await fullTextSearch('urgent fix', ['task', 'comment']);
 * if (result.success && result.object) {
 *   result.object.forEach(record => console.log(record.id));
 * }
 * ```
 */
export async function fullTextSearch(
  query: string,
  entities?: string[],
): Promise<ApiResponse<EntityRecord[]>> {
  const queryParams: Record<string, unknown> = { query };

  if (entities !== undefined && entities.length > 0) {
    // Pass entity names as a comma-separated string to match the
    // monolith's convention for multi-value query params.
    queryParams.entities = entities.join(',');
  }

  return get<EntityRecord[]>(`${SEARCH_BASE}/fts`, queryParams);
}

/**
 * Performs a typeahead/autocomplete search for select and lookup fields.
 *
 * Returns a small number of matching records suitable for display in a
 * dropdown or suggestion list. Replaces the monolith's quick-search usage
 * pattern where `limitRecords` was set to a small value (typically 5) with
 * a single lookup field and minimal return fields.
 *
 * @param params - TypeaheadSearchParams with search criteria
 * @returns Promise resolving to the API response envelope with EntityRecord[]
 *
 * @example
 * ```ts
 * const result = await typeaheadSearch({
 *   query: 'Joh',
 *   entityName: 'contact',
 *   lookupField: 'first_name',
 *   returnFields: 'id,first_name,last_name',
 *   limit: 10,
 * });
 * if (result.success && result.object) {
 *   result.object.forEach(record => console.log(record.id));
 * }
 * ```
 */
export async function typeaheadSearch(
  params: TypeaheadSearchParams,
): Promise<ApiResponse<EntityRecord[]>> {
  const queryParams: Record<string, unknown> = {
    query: params.query,
    entityName: params.entityName,
    lookupField: params.lookupField,
  };

  if (params.returnFields !== undefined) {
    queryParams.returnFields = params.returnFields;
  }
  if (params.limit !== undefined) {
    queryParams.limit = params.limit;
  }

  return get<EntityRecord[]>(`${SEARCH_BASE}/typeahead`, queryParams);
}
