/**
 * Filter and Query TypeScript type definitions.
 *
 * Converted from the WebVella ERP monolith C# DTOs:
 *   - WebVella.Erp.Web/Models/Filter.cs
 *   - WebVella.Erp.Web/Models/FilterType.cs
 *   - WebVella.Erp.Web/Models/ListFilter.cs
 *   - WebVella.Erp.Web/Models/QuerySortJson.cs
 *   - WebVella.Erp.Web/Models/QueryFilterJson.cs
 *   - WebVella.Erp/Api/Models/QueryType.cs
 *   - WebVella.Erp/Api/Models/QuerySortType.cs
 *   - WebVella.Erp/Api/Models/QueryObject.cs
 *   - WebVella.Erp/Api/Models/QuerySortObject.cs
 */

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/**
 * Filter type options used in list filter definitions.
 * Each member includes a human-readable label derived from the original
 * C# `[SelectOption(Label)]` attribute.
 *
 * @see WebVella.Erp.Web.Models.FilterType
 */
export const enum FilterType {
  /** No filter type defined */
  Undefined = 0,
  /** Starts with */
  STARTSWITH = 1,
  /** Contains */
  CONTAINS = 2,
  /** Equals */
  EQ = 3,
  /** Does not equal */
  NOT = 4,
  /** Less than */
  LT = 5,
  /** Less than or equal to */
  LTE = 6,
  /** Greater than */
  GT = 7,
  /** Greater than or equal to */
  GTE = 8,
  /** Matches RegEx */
  REGEX = 9,
  /** Full text search */
  FTS = 10,
  /** Between */
  BETWEEN = 11,
  /** Not Between */
  NOTBETWEEN = 12,
}

/**
 * Query type used to define the logical/comparison operator in a
 * {@link QueryObject} tree.
 *
 * @see WebVella.Erp.Api.Models.QueryType
 */
export const enum QueryType {
  /** Equal */
  EQ = 0,
  /** Not equal */
  NOT = 1,
  /** Less than */
  LT = 2,
  /** Less than or equal to */
  LTE = 3,
  /** Greater than */
  GT = 4,
  /** Greater than or equal to */
  GTE = 5,
  /** Logical AND */
  AND = 6,
  /** Logical OR */
  OR = 7,
  /** Contains */
  CONTAINS = 8,
  /** Starts with */
  STARTSWITH = 9,
  /** Regular expression match */
  REGEX = 10,
  /** Related record */
  RELATED = 11,
  /** Not related record */
  NOTRELATED = 12,
  /** Full text search */
  FTS = 13,
}

/**
 * Sort direction for query sort operations.
 *
 * @see WebVella.Erp.Api.Models.QuerySortType
 */
export const enum QuerySortType {
  /** Ascending order (label: "asc") */
  Ascending = 0,
  /** Descending order (label: "desc") */
  Descending = 1,
}

/**
 * Regex operator mode for {@link QueryObject} regex queries.
 *
 * @see WebVella.Erp.Api.Models.QueryObjectRegexOperator
 */
export const enum QueryObjectRegexOperator {
  /** Match with case sensitivity */
  MatchCaseSensitive = 0,
  /** Match with case insensitivity */
  MatchCaseInsensitive = 1,
  /** Don't match with case sensitivity */
  DontMatchCaseSensitive = 2,
  /** Don't match with case insensitivity */
  DontMatchCaseInsensitive = 3,
}

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Represents a single filter criterion applied to a list view.
 *
 * `value` and `value2` are typed as `unknown` because the C# source uses
 * `dynamic` — the runtime type depends on the field being filtered.
 * `value2` is used exclusively for {@link FilterType.BETWEEN} and
 * {@link FilterType.NOTBETWEEN} operations.
 *
 * @see WebVella.Erp.Web.Models.Filter
 */
export interface Filter {
  /** Filter field name. May include a prefix for namespaced lists. */
  name: string;
  /** The comparison operation to apply (defaults to Undefined). */
  type: FilterType;
  /** The primary filter value. Runtime type depends on the target field. */
  value: unknown;
  /** Secondary filter value for range operations (Between / NotBetween). */
  value2: unknown;
  /** Optional prefix for lists that support namespaced filters. */
  prefix: string;
}

/**
 * Defines a filter entry within a list query configuration.
 *
 * @see WebVella.Erp.Web.Models.ListFilter
 */
export interface ListFilter {
  /** The query operator type for this filter. */
  queryType: QueryType;
  /** The field key targeted by this filter. */
  queryKey: string;
  /** Whether this filter participates in a general (cross-field) search. */
  isGeneralSearch: boolean;
}

/**
 * Settings for a {@link QuerySortJson} sort configuration entry.
 *
 * @see WebVella.Erp.Web.Models.QuerySortJsonSettings
 */
export interface QuerySortJsonSettings {
  /** Sort order identifier (e.g. "asc", "desc"). */
  order: string;
}

/**
 * JSON-serialisable sort definition used in list page configurations.
 *
 * @see WebVella.Erp.Web.Models.QuerySortJson
 */
export interface QuerySortJson {
  /** Sort field name. */
  name: string;
  /** Selected sort option identifier. */
  option: string;
  /** Default sort option identifier when none is explicitly chosen. */
  default: string;
  /** Optional extended settings for this sort entry. Null when unused. */
  settings: QuerySortJsonSettings | null;
}

/**
 * Settings for a {@link QueryFilterJson} filter configuration entry,
 * representing date/time component granularity.
 *
 * @see WebVella.Erp.Web.Models.QueryFilterJsonSettings
 */
export interface QueryFilterJsonSettings {
  /** Year component for date filters. */
  year: number;
  /** Month component for date filters. */
  month: number;
  /** Day component for date filters. */
  day: number;
  /** Hour component for time filters. */
  hour: number;
  /** Minute component for time filters. */
  minute: number;
}

/**
 * JSON-serialisable filter definition used in list page configurations.
 *
 * @see WebVella.Erp.Web.Models.QueryFilterJson
 */
export interface QueryFilterJson {
  /** Filter field name. */
  name: string;
  /** Selected filter option identifier. */
  option: string;
  /** Default filter option identifier when none is explicitly chosen. */
  default: string;
  /** Optional extended settings for this filter entry. Null when unused. */
  settings: QueryFilterJsonSettings | null;
}

/**
 * Recursive query object that forms a tree of filter conditions.
 *
 * Leaf nodes define a field comparison; branch nodes (AND / OR) hold child
 * queries in {@link subQueries}. `fieldValue` is `unknown` because the C#
 * source uses `object` — the runtime type varies by field.
 *
 * @see WebVella.Erp.Api.Models.QueryObject
 */
export interface QueryObject {
  /** The comparison or logical operator for this node. */
  queryType: QueryType;
  /** Target field name for leaf-level comparisons. */
  fieldName: string;
  /** Comparison value. Runtime type depends on the target field. */
  fieldValue: unknown;
  /** Regex match mode (only relevant when queryType is REGEX). */
  regexOperator?: QueryObjectRegexOperator;
  /** Language identifier for full-text search queries. */
  ftsLanguage?: string;
  /** Child query nodes for compound (AND/OR) expressions. */
  subQueries: QueryObject[];
}

/**
 * Defines a single sort directive applied to a query result set.
 *
 * The C# source uses private setters — the `readonly` modifier preserves
 * the immutability intent.
 *
 * @see WebVella.Erp.Api.Models.QuerySortObject
 */
export interface QuerySortObject {
  /** The field to sort by (immutable after construction). */
  readonly fieldName: string;
  /** The sort direction. */
  sortType: QuerySortType;
}
