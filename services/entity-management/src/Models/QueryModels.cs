// =============================================================================
// QueryModels.cs — Query DSL Models for Entity Management Service
// =============================================================================
// Consolidates 9 separate query-related model files from the monolith into
// a single cohesive file:
//   - WebVella.Erp/Api/Models/QueryType.cs           → QueryType enum
//   - WebVella.Erp/Api/Models/QueryObject.cs          → QueryObject class + QueryObjectRegexOperator enum
//   - WebVella.Erp/Api/Models/QuerySortType.cs        → QuerySortType enum
//   - WebVella.Erp/Api/Models/QuerySortObject.cs      → QuerySortObject class
//   - WebVella.Erp/Api/Models/EntityQuery.cs           → EntityQuery class
//   - WebVella.Erp/Api/Models/QueryResult.cs           → QueryResult class
//   - WebVella.Erp/Api/Models/QueryResponse.cs         → QueryResponse class
//   - WebVella.Erp/Api/Models/QueryCountResponse.cs    → QueryCountResponse class
//   - WebVella.Erp/Api/Models/QuerySecurity.cs         → QuerySecurity class
//
// These models define the complete query DSL used by the Entity Management
// service for record filtering, sorting, and pagination — the core building
// blocks for all record query operations.
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] (AOT-safe)
//   Old: Newtonsoft.Json [JsonIgnore]
//   New: System.Text.Json.Serialization [JsonIgnore]
// =============================================================================

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    // =========================================================================
    // QueryType Enum
    // =========================================================================
    // Defines all filter comparison operators supported by the query DSL.
    // Used by QueryObject.QueryType to specify the comparison operation for
    // each filter node in the recursive query tree.
    //
    // NO explicit int values — uses compiler-assigned defaults (0, 1, 2, ...).
    // This is intentional per the AAP specification; the monolith source also
    // used default enum values.
    //
    // Source: WebVella.Erp/Api/Models/QueryType.cs (lines 5-21)
    // =========================================================================

    /// <summary>
    /// Enumerates all supported query filter comparison operators for the
    /// Entity Management service query DSL. Each value maps to a specific
    /// DynamoDB condition expression or scan filter operation in the
    /// QueryAdapter service layer.
    /// </summary>
    public enum QueryType
    {
        /// <summary>Exact equality comparison (field == value).</summary>
        EQ,

        /// <summary>Inequality / negated equality (field != value).</summary>
        NOT,

        /// <summary>Less-than comparison (field &lt; value).</summary>
        LT,

        /// <summary>Less-than-or-equal comparison (field &lt;= value).</summary>
        LTE,

        /// <summary>Greater-than comparison (field &gt; value).</summary>
        GT,

        /// <summary>Greater-than-or-equal comparison (field &gt;= value).</summary>
        GTE,

        /// <summary>Logical AND — all SubQueries must be true.</summary>
        AND,

        /// <summary>Logical OR — at least one SubQuery must be true.</summary>
        OR,

        /// <summary>Substring containment match (LIKE '%value%' equivalent).</summary>
        CONTAINS,

        /// <summary>String prefix match (LIKE 'value%' equivalent).</summary>
        STARTSWITH,

        /// <summary>Regular expression pattern match with configurable case sensitivity.</summary>
        REGEX,

        /// <summary>Relational filter — records related via a named relation.</summary>
        RELATED,

        /// <summary>Negated relational filter — records NOT related via a named relation.</summary>
        NOTRELATED,

        /// <summary>Full-text search with optional language specification.</summary>
        FTS
    }

    // =========================================================================
    // QueryObjectRegexOperator Enum
    // =========================================================================
    // Controls regex matching behavior for QueryType.REGEX filter operations.
    //
    // Source: WebVella.Erp/Api/Models/QueryObject.cs (lines 29-35)
    // =========================================================================

    /// <summary>
    /// Specifies regex matching behavior for <see cref="QueryType.REGEX"/> queries.
    /// Controls both case sensitivity and whether the pattern should match or not match.
    /// </summary>
    public enum QueryObjectRegexOperator
    {
        /// <summary>Pattern must match, case-sensitive comparison.</summary>
        MatchCaseSensitive,

        /// <summary>Pattern must match, case-insensitive comparison.</summary>
        MatchCaseInsensitive,

        /// <summary>Pattern must NOT match, case-sensitive comparison.</summary>
        DontMatchCaseSensitive,

        /// <summary>Pattern must NOT match, case-insensitive comparison.</summary>
        DontMatchCaseInsensitive,
    }

    // =========================================================================
    // QueryObject Class
    // =========================================================================
    // Represents a single node in a recursive filter expression tree.
    // Leaf nodes have FieldName + FieldValue; branch nodes (AND/OR) have
    // SubQueries containing child QueryObject nodes.
    //
    // Source: WebVella.Erp/Api/Models/QueryObject.cs (lines 7-27)
    // =========================================================================

    /// <summary>
    /// Represents a single filter expression node in the query DSL's recursive
    /// filter tree. Leaf nodes specify a field comparison (FieldName, FieldValue,
    /// QueryType); branch nodes (AND/OR) contain SubQueries of child nodes.
    /// <para>
    /// This class is the building block for all record query filters. Complex
    /// filter expressions are composed by nesting QueryObject instances via
    /// the <see cref="EntityQuery"/> static factory methods (QueryAND, QueryOR, etc.).
    /// </para>
    /// </summary>
    [Serializable]
    public class QueryObject
    {
        /// <summary>
        /// The comparison operator for this filter node.
        /// Determines how FieldName and FieldValue are compared.
        /// </summary>
        [JsonPropertyName("queryType")]
        public QueryType QueryType { get; set; }

        /// <summary>
        /// The entity field name to filter on. For RELATED/NOTRELATED queries,
        /// this holds the relation name instead of a field name.
        /// </summary>
        [JsonPropertyName("fieldName")]
        public string FieldName { get; set; } = string.Empty;

        /// <summary>
        /// The value to compare the field against. The type depends on the
        /// field type (string, Guid, DateTime, numeric, etc.). For RELATED/NOTRELATED
        /// queries, this holds the direction string ("origin-target" or "target-origin").
        /// </summary>
        [JsonPropertyName("fieldValue")]
        public object? FieldValue { get; set; }

        /// <summary>
        /// Controls regex matching behavior when <see cref="QueryType"/> is
        /// <see cref="Models.QueryType.REGEX"/>. Ignored for other query types.
        /// </summary>
        [JsonPropertyName("regexOperator")]
        public QueryObjectRegexOperator RegexOperator { get; set; }

        /// <summary>
        /// Language specification for full-text search when <see cref="QueryType"/>
        /// is <see cref="Models.QueryType.FTS"/>. Null uses the default language.
        /// </summary>
        [JsonPropertyName("ftsLanguage")]
        public string? FtsLanguage { get; set; }

        /// <summary>
        /// Child filter nodes for branch operations (AND/OR). Null or empty for
        /// leaf nodes. The QueryAdapter evaluates all sub-queries according to
        /// the parent's <see cref="QueryType"/> (AND = all must match, OR = any must match).
        /// </summary>
        [JsonPropertyName("subQueries")]
        public List<QueryObject>? SubQueries { get; set; }
    }

    // =========================================================================
    // QuerySortType Enum
    // =========================================================================
    // Sort direction for query result ordering.
    // Annotated with [SelectOption] for UI display and serialization labels.
    //
    // Source: WebVella.Erp/Api/Models/QuerySortType.cs (lines 6-12)
    // =========================================================================

    /// <summary>
    /// Defines the sort direction for query result ordering.
    /// Each value is annotated with <see cref="SelectOptionAttribute"/> to provide
    /// human-readable labels ("asc"/"desc") for serialization and UI display.
    /// </summary>
    public enum QuerySortType
    {
        /// <summary>Ascending sort order (A→Z, 0→9, oldest→newest).</summary>
        [SelectOption(Label = "asc")]
        Ascending,

        /// <summary>Descending sort order (Z→A, 9→0, newest→oldest).</summary>
        [SelectOption(Label = "desc")]
        Descending
    }

    // =========================================================================
    // QuerySortObject Class
    // =========================================================================
    // Immutable sort specification pairing a field name with a sort direction.
    // Used in EntityQuery.Sort array to define multi-column ordering.
    //
    // Source: WebVella.Erp/Api/Models/QuerySortObject.cs (lines 6-20)
    // =========================================================================

    /// <summary>
    /// Represents a single sort column specification pairing a field name with
    /// a sort direction. Instances are immutable after construction — properties
    /// have private setters to prevent modification after creation.
    /// <para>
    /// Used in <see cref="EntityQuery.Sort"/> array to define multi-column ordering
    /// for record query results.
    /// </para>
    /// </summary>
    [Serializable]
    public class QuerySortObject
    {
        /// <summary>
        /// The entity field name to sort by.
        /// </summary>
        [JsonPropertyName("fieldName")]
        public string FieldName { get; private set; }

        /// <summary>
        /// The sort direction (ascending or descending).
        /// </summary>
        [JsonPropertyName("sortType")]
        public QuerySortType SortType { get; private set; }

        /// <summary>
        /// Creates a new sort specification for the given field and direction.
        /// </summary>
        /// <param name="fieldName">The entity field name to sort by.</param>
        /// <param name="sortType">The sort direction.</param>
        public QuerySortObject(string fieldName, QuerySortType sortType)
        {
            FieldName = fieldName;
            SortType = sortType;
        }
    }

    // =========================================================================
    // EntityQuery Class
    // =========================================================================
    // Complete query specification combining entity target, field projection,
    // filter tree, sort columns, and pagination. Also provides static factory
    // methods for building QueryObject filter trees with a fluent API.
    //
    // Source: WebVella.Erp/Api/Models/EntityQuery.cs (full file)
    // =========================================================================

    /// <summary>
    /// Represents a complete record query specification for the Entity Management
    /// service. Combines the target entity, field projection, recursive filter tree,
    /// multi-column sort, and pagination parameters into a single query object.
    /// <para>
    /// The static factory methods (QueryEQ, QueryAND, QueryOR, etc.) provide a
    /// fluent API for constructing <see cref="QueryObject"/> filter trees without
    /// manually instantiating and configuring QueryObject instances.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// var query = new EntityQuery("account", "*",
    ///     EntityQuery.QueryAND(
    ///         EntityQuery.QueryEQ("status", "active"),
    ///         EntityQuery.QueryGTE("revenue", 100000)
    ///     ),
    ///     new[] { new QuerySortObject("name", QuerySortType.Ascending) },
    ///     skip: 0, limit: 50);
    /// </code>
    /// </example>
    [Serializable]
    public class EntityQuery
    {
        /// <summary>
        /// The name of the entity to query. Must not be null or whitespace.
        /// Maps to the entity metadata stored in the Entity Management service's
        /// DynamoDB table (PK=ENTITY#{entityName}).
        /// </summary>
        public string EntityName { get; set; }

        /// <summary>
        /// Comma-separated list of field names to include in the result projection.
        /// Use "*" to return all fields. Defaults to "*" if null or whitespace.
        /// </summary>
        public string Fields { get; set; }

        /// <summary>
        /// The root of the recursive filter expression tree. Null means no filtering
        /// (return all records). Constructed using the static factory methods.
        /// </summary>
        public QueryObject? Query { get; set; }

        /// <summary>
        /// Array of sort specifications applied in order. Null means no sorting
        /// (default DynamoDB sort key ordering).
        /// </summary>
        public QuerySortObject[]? Sort { get; set; }

        /// <summary>
        /// Number of records to skip for pagination. Null means start from the beginning.
        /// </summary>
        public int? Skip { get; set; }

        /// <summary>
        /// Maximum number of records to return. Null means no limit.
        /// </summary>
        public int? Limit { get; set; }

        /// <summary>
        /// Server-only property for query argument overwriting.
        /// Excluded from JSON serialization as it is used only during
        /// server-side query processing, never transmitted to API consumers.
        /// </summary>
        [JsonIgnore]
        public List<KeyValuePair<string, string>>? OverwriteArgs { get; set; }

        /// <summary>
        /// Creates a new EntityQuery targeting the specified entity with optional
        /// field projection, filter, sort, pagination, and overwrite arguments.
        /// </summary>
        /// <param name="entityName">
        /// The name of the entity to query. Must not be null or whitespace.
        /// </param>
        /// <param name="fields">
        /// Comma-separated field names to project, or "*" for all fields.
        /// Defaults to "*" if null or whitespace.
        /// </param>
        /// <param name="query">
        /// The root filter expression node. Null for unfiltered queries.
        /// </param>
        /// <param name="sort">
        /// Array of sort column specifications. Null for default ordering.
        /// </param>
        /// <param name="skip">
        /// Number of records to skip (pagination offset). Null to start from beginning.
        /// </param>
        /// <param name="limit">
        /// Maximum number of records to return. Null for no limit.
        /// </param>
        /// <param name="overwriteArgs">
        /// Server-side query argument overrides. Null if not needed.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="entityName"/> is null, empty, or whitespace.
        /// </exception>
        public EntityQuery(string entityName, string fields = "*", QueryObject? query = null,
            QuerySortObject[]? sort = null, int? skip = null, int? limit = null,
            List<KeyValuePair<string, string>>? overwriteArgs = null)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Invalid entity name.");

            if (string.IsNullOrWhiteSpace(fields))
                fields = "*";

            EntityName = entityName;
            Fields = fields;
            Query = query;
            Sort = sort;
            Skip = skip;
            Limit = limit;
            OverwriteArgs = overwriteArgs;
        }

        #region <=== Static Factory Methods ===>

        /// <summary>
        /// Creates an equality filter: field == value.
        /// </summary>
        /// <param name="fieldName">The field to compare. Must not be null or whitespace.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A QueryObject configured for equality comparison.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="fieldName"/> is null or whitespace.
        /// </exception>
        public static QueryObject QueryEQ(string fieldName, object? value)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new ArgumentNullException(nameof(fieldName));

            return new QueryObject { QueryType = QueryType.EQ, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates an inequality filter: field != value.
        /// </summary>
        /// <param name="fieldName">The field to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A QueryObject configured for inequality comparison.</returns>
        public static QueryObject QueryNOT(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.NOT, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a less-than filter: field &lt; value.
        /// </summary>
        /// <param name="fieldName">The field to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A QueryObject configured for less-than comparison.</returns>
        public static QueryObject QueryLT(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.LT, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a less-than-or-equal filter: field &lt;= value.
        /// </summary>
        /// <param name="fieldName">The field to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A QueryObject configured for less-than-or-equal comparison.</returns>
        public static QueryObject QueryLTE(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.LTE, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a greater-than filter: field &gt; value.
        /// </summary>
        /// <param name="fieldName">The field to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A QueryObject configured for greater-than comparison.</returns>
        public static QueryObject QueryGT(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.GT, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a greater-than-or-equal filter: field &gt;= value.
        /// </summary>
        /// <param name="fieldName">The field to compare.</param>
        /// <param name="value">The value to compare against.</param>
        /// <returns>A QueryObject configured for greater-than-or-equal comparison.</returns>
        public static QueryObject QueryGTE(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.GTE, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a logical AND compound filter. All child queries must evaluate
        /// to true for the overall filter to match a record.
        /// </summary>
        /// <param name="queries">
        /// One or more child QueryObject nodes. None may be null.
        /// </param>
        /// <returns>A QueryObject with QueryType.AND and the provided SubQueries.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when any element in <paramref name="queries"/> is null.
        /// </exception>
        public static QueryObject QueryAND(params QueryObject[] queries)
        {
            foreach (var query in queries)
            {
                if (query == null)
                    throw new ArgumentException("Queries contains null values.");
            }

            return new QueryObject { QueryType = QueryType.AND, SubQueries = new List<QueryObject>(queries) };
        }

        /// <summary>
        /// Creates a logical OR compound filter. At least one child query must
        /// evaluate to true for the overall filter to match a record.
        /// </summary>
        /// <param name="queries">
        /// One or more child QueryObject nodes. None may be null.
        /// </param>
        /// <returns>A QueryObject with QueryType.OR and the provided SubQueries.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when any element in <paramref name="queries"/> is null.
        /// </exception>
        public static QueryObject QueryOR(params QueryObject[] queries)
        {
            foreach (var query in queries)
            {
                if (query == null)
                    throw new ArgumentException("Queries contains null values.");
            }

            return new QueryObject { QueryType = QueryType.OR, SubQueries = new List<QueryObject>(queries) };
        }

        /// <summary>
        /// Creates a substring containment filter (LIKE '%value%' equivalent).
        /// </summary>
        /// <param name="fieldName">The field to search within.</param>
        /// <param name="value">The substring to search for.</param>
        /// <returns>A QueryObject configured for containment matching.</returns>
        public static QueryObject QueryContains(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.CONTAINS, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a string prefix filter (LIKE 'value%' equivalent).
        /// </summary>
        /// <param name="fieldName">The field to match against.</param>
        /// <param name="value">The prefix to match.</param>
        /// <returns>A QueryObject configured for prefix matching.</returns>
        public static QueryObject QueryStartsWith(string fieldName, object? value)
        {
            return new QueryObject { QueryType = QueryType.STARTSWITH, FieldName = fieldName, FieldValue = value };
        }

        /// <summary>
        /// Creates a regular expression pattern match filter with configurable
        /// case sensitivity and match/no-match semantics.
        /// </summary>
        /// <param name="fieldName">The field to match against.</param>
        /// <param name="value">The regex pattern.</param>
        /// <param name="op">
        /// The regex operator controlling case sensitivity and match semantics.
        /// Defaults to <see cref="QueryObjectRegexOperator.MatchCaseSensitive"/>.
        /// </param>
        /// <returns>A QueryObject configured for regex matching.</returns>
        public static QueryObject QueryRegex(string fieldName, object? value,
            QueryObjectRegexOperator op = QueryObjectRegexOperator.MatchCaseSensitive)
        {
            return new QueryObject
            {
                QueryType = QueryType.REGEX,
                FieldName = fieldName,
                FieldValue = value,
                RegexOperator = op
            };
        }

        /// <summary>
        /// Creates a full-text search filter with optional language specification.
        /// </summary>
        /// <param name="fieldName">The field to search (must be a searchable/FTS-indexed field).</param>
        /// <param name="value">The search terms.</param>
        /// <param name="language">
        /// The FTS language configuration. Null uses the default language.
        /// </param>
        /// <returns>A QueryObject configured for full-text search.</returns>
        public static QueryObject QueryFTS(string fieldName, object? value, string? language = null)
        {
            return new QueryObject
            {
                QueryType = QueryType.FTS,
                FieldName = fieldName,
                FieldValue = value,
                FtsLanguage = language
            };
        }

        /// <summary>
        /// Creates a relational filter — matches records that ARE related to another
        /// entity via the specified relation.
        /// </summary>
        /// <param name="relationName">The name of the entity relation to filter by.</param>
        /// <param name="direction">
        /// The relation direction: "origin-target" (default) or "target-origin".
        /// </param>
        /// <returns>A QueryObject configured for relational filtering.</returns>
        public static QueryObject Related(string relationName, string direction = "origin-target")
        {
            return new QueryObject { QueryType = QueryType.RELATED, FieldName = relationName, FieldValue = direction };
        }

        /// <summary>
        /// Creates a negated relational filter — matches records that are NOT related
        /// to another entity via the specified relation.
        /// </summary>
        /// <param name="relationName">The name of the entity relation to filter by.</param>
        /// <param name="direction">
        /// The relation direction: "origin-target" (default) or "target-origin".
        /// </param>
        /// <returns>A QueryObject configured for negated relational filtering.</returns>
        public static QueryObject NotRelated(string relationName, string direction = "origin-target")
        {
            return new QueryObject { QueryType = QueryType.NOTRELATED, FieldName = relationName, FieldValue = direction };
        }

        #endregion
    }

    // =========================================================================
    // QueryResult Class
    // =========================================================================
    // Holds the actual data returned from a record query: field metadata and
    // the list of matching records.
    //
    // Source: WebVella.Erp/Api/Models/QueryResult.cs (lines 6-13)
    // =========================================================================

    /// <summary>
    /// Holds the result data from a record query operation. Contains metadata
    /// about the fields included in the result set (<see cref="FieldsMeta"/>)
    /// and the actual data rows (<see cref="Data"/>).
    /// <para>
    /// Used as the payload type for <see cref="QueryResponse.Object"/>.
    /// </para>
    /// </summary>
    public class QueryResult
    {
        /// <summary>
        /// Metadata about the fields included in the query result projection.
        /// Each <see cref="Field"/> entry describes the field's type, name, label,
        /// and other metadata, enabling consumers to interpret the Data records.
        /// </summary>
        [JsonPropertyName("fieldsMeta")]
        public List<Field>? FieldsMeta { get; set; }

        /// <summary>
        /// The list of matching entity records. Each <see cref="EntityRecord"/>
        /// is a dictionary-based dynamic record containing field name → value pairs.
        /// </summary>
        [JsonPropertyName("data")]
        public List<EntityRecord>? Data { get; set; }
    }

    // =========================================================================
    // QueryResponse Class
    // =========================================================================
    // API response envelope wrapping a QueryResult. Inherits the standard
    // response structure (Success, Message, Errors, AccessWarnings, Timestamp)
    // from BaseResponseModel.
    //
    // Source: WebVella.Erp/Api/Models/QueryResponse.cs
    // =========================================================================

    /// <summary>
    /// API response envelope for record query operations. Wraps a
    /// <see cref="QueryResult"/> payload within the standard response structure
    /// inherited from <see cref="BaseResponseModel"/>.
    /// <para>
    /// The constructor initializes <see cref="Object"/> to an empty
    /// <see cref="QueryResult"/> instance to ensure the property is never null
    /// in successful responses.
    /// </para>
    /// </summary>
    public class QueryResponse : BaseResponseModel
    {
        /// <summary>
        /// Creates a new QueryResponse with an empty QueryResult payload.
        /// Inherits Timestamp, Success, Message, Errors, and AccessWarnings
        /// initialization from <see cref="BaseResponseModel"/>.
        /// </summary>
        public QueryResponse()
        {
            Object = new QueryResult();
        }

        /// <summary>
        /// The query result payload containing field metadata and matching records.
        /// Serialized with JSON key "object" for backward API compatibility.
        /// </summary>
        [JsonPropertyName("object")]
        public QueryResult Object { get; set; }
    }

    // =========================================================================
    // QueryCountResponse Class
    // =========================================================================
    // API response envelope wrapping a record count (long). Used for
    // count-only query operations that return the number of matching records
    // without fetching the actual data.
    //
    // Source: WebVella.Erp/Api/Models/QueryCountResponse.cs
    // =========================================================================

    /// <summary>
    /// API response envelope for record count query operations. Wraps a
    /// <c>long</c> count value within the standard response structure
    /// inherited from <see cref="BaseResponseModel"/>.
    /// <para>
    /// Used when the caller needs only the number of matching records,
    /// not the actual record data — reducing payload size and query cost.
    /// </para>
    /// </summary>
    public class QueryCountResponse : BaseResponseModel
    {
        /// <summary>
        /// The count of records matching the query criteria.
        /// Serialized with JSON key "object" for backward API compatibility.
        /// </summary>
        [JsonPropertyName("object")]
        public long Object { get; set; }
    }

    // =========================================================================
    // QuerySecurity Class
    // =========================================================================
    // Placeholder for query-level security context. Currently empty, preserved
    // from the monolith source for future extension of per-query permission
    // checking in the Entity Management service.
    //
    // Source: WebVella.Erp/Api/Models/QuerySecurity.cs (lines 3-5)
    // =========================================================================

    /// <summary>
    /// Placeholder class for query-level security context information.
    /// Reserved for future implementation of per-query permission checking
    /// in the Entity Management service's QueryAdapter layer.
    /// <para>
    /// In the monolith, this class was also empty — preserved for API
    /// compatibility and future extension.
    /// </para>
    /// </summary>
    public class QuerySecurity
    {
    }
}
