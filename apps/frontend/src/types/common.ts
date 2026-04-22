/**
 * Shared/Base TypeScript interfaces for the WebVella ERP frontend.
 *
 * This is the most foundational type file in the application — it defines
 * API response envelopes, error/warning models, screen messages, typeahead
 * responses, URL routing info, search models, and currency types.
 *
 * Converted from C# DTOs:
 *   - WebVella.Erp/Api/Models/BaseModels.cs
 *   - WebVella.Erp.Web/Models/ScreenMessage.cs
 *   - WebVella.Erp.Web/Models/TypeaheadResponse.cs
 *   - WebVella.Erp.Web/Models/UrlInfo.cs
 *   - WebVella.Erp/Api/Models/Currency.cs
 *   - WebVella.Erp/Api/Models/SearchQuery.cs
 *   - WebVella.Erp/Api/Models/SearchResult.cs
 *   - WebVella.Erp/Api/Models/SearchResultList.cs
 *   - WebVella.Erp/Api/Models/SearchType.cs
 *   - WebVella.Erp/Api/Models/SearchResultType.cs
 *
 * IMPORTANT: This file has ZERO imports from other type files.
 * All other type files may depend on this one.
 */

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/**
 * Screen message severity levels.
 *
 * Mirrors C# `ScreenMessageType` enum from ScreenMessage.cs (lines 22-32).
 * Used by toast/notification components to select icon and colour.
 */
export const enum ScreenMessageType {
  Success = 0,
  Info = 1,
  Warning = 2,
  Error = 3,
}

/**
 * Full-text search strategy selector.
 *
 * Mirrors C# `SearchType` enum from SearchType.cs.
 *   - `Contains` — simple substring/LIKE match
 *   - `Fts`      — PostgreSQL-backed full-text search with stemming
 */
export const enum SearchType {
  Contains = 0,
  Fts = 1,
}

/**
 * Controls the verbosity of search results returned by the API.
 *
 * Mirrors C# `SearchResultType` enum from SearchResultType.cs.
 *   - `Compact` — minimal fields (id, snippet, url)
 *   - `Full`    — all content fields included
 */
export const enum SearchResultType {
  Compact = 0,
  Full = 1,
}

// ---------------------------------------------------------------------------
// Base Response Models
// ---------------------------------------------------------------------------

/**
 * Represents a single validation or processing error returned by the API.
 *
 * Mirrors C# `ErrorModel` from BaseModels.cs (lines 62-83).
 * JSON property names: key, value, message.
 */
export interface ErrorModel {
  /** Machine-readable error key (e.g. field name or error code). */
  key: string;
  /** The offending value that caused the error. */
  value: string;
  /** Human-readable error description. */
  message: string;
}

/**
 * Represents an access-level warning attached to an API response.
 *
 * Mirrors C# `AccessWarningModel` from BaseModels.cs (lines 50-60).
 * JSON property names: key, code, message.
 */
export interface AccessWarningModel {
  /** Machine-readable warning key. */
  key: string;
  /** Warning code for programmatic handling. */
  code: string;
  /** Human-readable warning description. */
  message: string;
}

/**
 * Standard API response envelope shared by every backend endpoint.
 *
 * Mirrors C# `BaseResponseModel` from BaseModels.cs (lines 8-38).
 * NOTE: The C# `StatusCode` property has `[JsonIgnore]` and is intentionally
 * excluded from this TypeScript interface — it is never serialised over the wire.
 *
 * JSON property names: timestamp, success, message, hash, errors, accessWarnings.
 */
export interface BaseResponseModel {
  /** ISO 8601 timestamp of the response (C# DateTime). */
  timestamp: string;
  /** Whether the operation completed successfully. */
  success: boolean;
  /** Summary message describing the outcome. */
  message: string;
  /** Optional ETag-like hash for cache validation; null when not applicable. */
  hash: string | null;
  /** Validation or processing errors (empty array on success). */
  errors: ErrorModel[];
  /** Non-fatal access warnings (empty array when none). */
  accessWarnings: AccessWarningModel[];
}

/**
 * Generic response wrapper that extends the base envelope with a payload.
 *
 * Mirrors C# `ResponseModel` from BaseModels.cs (lines 40-48).
 * The `object` property carries the domain-specific result; its shape
 * depends on the endpoint. Consumers should narrow the type via generics
 * or type guards.
 */
export interface ResponseModel extends BaseResponseModel {
  /** The response payload. Shape varies by endpoint. */
  object: unknown;
}

// ---------------------------------------------------------------------------
// Screen Message
// ---------------------------------------------------------------------------

/**
 * A transient UI notification displayed as a toast or banner.
 *
 * Mirrors C# `ScreenMessage` from ScreenMessage.cs (lines 8-19).
 * JSON property names: type, title, message.
 */
export interface ScreenMessage {
  /** Severity / visual style of the message. */
  type: ScreenMessageType;
  /** Short headline (may be empty string). */
  title: string;
  /** Detailed message body (may be empty string). */
  message: string;
}

// ---------------------------------------------------------------------------
// Typeahead / Autocomplete Models
// ---------------------------------------------------------------------------

/**
 * A single row in a typeahead/autocomplete suggestion list.
 *
 * Mirrors C# `TypeaheadResponseRow` from TypeaheadResponse.cs (lines 18-37).
 * JSON property names: id, iconName, color, text, entityName, fieldName.
 */
export interface TypeaheadResponseRow {
  /** Unique identifier of the suggested record. */
  id: string;
  /** Icon name to display next to the suggestion (default: "database"). */
  iconName: string;
  /** Accent colour for the icon (default: "teal"). */
  color: string;
  /** Display text shown to the user. */
  text: string;
  /** Entity name the suggestion belongs to. */
  entityName: string;
  /** Field name that produced the match. */
  fieldName: string;
}

/**
 * Pagination metadata for typeahead results.
 *
 * Mirrors C# `TypeaheadResponsePagination` from TypeaheadResponse.cs (lines 38-41).
 */
export interface TypeaheadResponsePagination {
  /** Whether additional pages of results are available (default: false). */
  more: boolean;
}

/**
 * Full typeahead response including results and pagination.
 *
 * Mirrors C# `TypeaheadResponse` from TypeaheadResponse.cs (lines 9-16).
 * JSON property names: results, pagination.
 */
export interface TypeaheadResponse {
  /** Ordered list of matching suggestions. */
  results: TypeaheadResponseRow[];
  /** Pagination metadata indicating if more results exist. */
  pagination: TypeaheadResponsePagination;
}

// ---------------------------------------------------------------------------
// URL Info
// ---------------------------------------------------------------------------

/**
 * Parsed route/URL metadata used to identify the current ERP page context.
 *
 * Mirrors C# `UrlInfo` from UrlInfo.cs (lines 6-35).
 * JSON property names use snake_case on the wire; mapped to camelCase here.
 *
 * `pageType` is typed as `number` (rather than importing a PageType enum)
 * to avoid a circular dependency — the canonical `PageType` enum lives in
 * the page type file.
 */
export interface UrlInfo {
  /** Whether the current route involves a relation context (default: false). */
  hasRelation: boolean;
  /**
   * Numeric page type discriminator.
   * Uses `number` instead of the `PageType` enum to prevent circular imports.
   * Consumers should compare against `PageType` enum values at the call site.
   */
  pageType: number;
  /** Application slug from the URL (e.g. "crm"). */
  appName: string;
  /** Area slug from the URL. */
  areaName: string;
  /** Node slug from the URL. */
  nodeName: string;
  /** Page slug from the URL. */
  pageName: string;
  /** Record GUID when viewing/editing a specific record; null otherwise. */
  recordId?: string | null;
  /** Relation GUID when navigating a related-record route; null otherwise. */
  relationId?: string | null;
  /** Parent record GUID for related-record routes; null otherwise. */
  parentRecordId?: string | null;
}

// ---------------------------------------------------------------------------
// Search Models
// ---------------------------------------------------------------------------

/**
 * Parameters for a search request.
 *
 * Mirrors C# `SearchQuery` from SearchQuery.cs.
 * JSON property names use snake_case on the wire; mapped to camelCase here.
 */
export interface SearchQuery {
  /** Strategy used for matching (Contains or Fts). */
  searchType: SearchType;
  /** Verbosity of results (Compact or Full). */
  resultType: SearchResultType;
  /** Free-text search input. */
  text: string;
  /** Filter: only return results from these entity IDs (GUIDs as strings). */
  entities: string[];
  /** Filter: only return results from these application IDs (GUIDs as strings). */
  apps: string[];
  /** Filter: only return results matching these record IDs (GUIDs as strings). */
  records: string[];
  /** Number of results to skip (for pagination, default: 0). */
  skip: number;
  /** Maximum number of results to return (default: 20). */
  limit: number;
}

/**
 * A single search result returned by the search API.
 *
 * Mirrors C# `SearchResult` from SearchResult.cs.
 * JSON property names use snake_case on the wire; mapped to camelCase here.
 */
export interface SearchResult {
  /** Unique search-result identifier (GUID as string). */
  id: string;
  /** Entity IDs associated with this result (GUIDs as strings). */
  entities: string[];
  /** Application IDs associated with this result (GUIDs as strings). */
  apps: string[];
  /** Record IDs associated with this result (GUIDs as strings). */
  records: string[];
  /** Full indexed content of the record. */
  content: string;
  /** Stemmed content used for full-text search matching. */
  stemContent: string;
  /** Highlighted snippet showing the match in context. */
  snippet: string;
  /** Deep-link URL to the matched record. */
  url: string;
  /** Auxiliary/extra data attached to the search entry. */
  auxData: string;
  /** ISO 8601 timestamp when the search entry was last indexed. */
  timestamp: string;
}

/**
 * Paginated list of search results.
 *
 * Mirrors C# `SearchResultList` from SearchResultList.cs.
 * In C# this extends `List<SearchResult>` — here we use a flat interface
 * with an explicit `results` array for cleaner TypeScript ergonomics.
 */
export interface SearchResultList {
  /** The search results for the current page. */
  results: SearchResult[];
  /** Total number of matching results across all pages. */
  totalCount: number;
}

// ---------------------------------------------------------------------------
// Currency
// ---------------------------------------------------------------------------

/**
 * ISO 4217 currency definition with formatting metadata.
 *
 * Mirrors C# `Currency` from Currency.cs.
 * JSON property names use snake_case on the wire; mapped to camelCase here.
 */
export interface Currency {
  /** Internal currency identifier (e.g. "usd"). */
  id: string;
  /** List of alternative symbols for display (e.g. ["US$"]). */
  alternateSymbols: string[];
  /** Character used as the decimal separator (e.g. "."). */
  decimalMark: string;
  /** Symbol used when disambiguation is needed (e.g. "US$"). */
  disambiguateSymbol: string;
  /** HTML entity for the currency symbol (e.g. "&#x24;"). */
  htmlEntity: string;
  /** ISO 4217 alphabetic code (e.g. "USD"). */
  isoCode: string;
  /** ISO 4217 numeric code (e.g. "840"). */
  isoNumeric: string;
  /** Full English name of the currency (e.g. "United States Dollar"). */
  name: string;
  /** Display priority for sorting (lower = higher priority, default: 100). */
  priority: number;
  /** Smallest denomination in minor units (default: 1). */
  smallestDenomination: number;
  /** Name of the fractional monetary unit (e.g. "Cent"). */
  subUnit: string;
  /** Number of sub-units per main unit (e.g. 100 cents = 1 dollar). */
  subUnitToUnit: number;
  /** Primary currency symbol (e.g. "$"). */
  symbol: string;
  /** Whether the symbol appears before the amount (true) or after (false). */
  symbolFirst: boolean;
  /** Character used as the thousands separator (e.g. ","). */
  thousandsSeparator: string;
}
