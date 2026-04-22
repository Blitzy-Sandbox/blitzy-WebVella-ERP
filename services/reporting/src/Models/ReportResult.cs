using System.Text.Json.Serialization;

namespace WebVellaErp.Reporting.Models
{
    /// <summary>
    /// Represents metadata about a single column in a report result set.
    /// Replaces the monolith's recursive <c>DataSourceModelFieldMeta</c> (Name, Type, EntityName,
    /// RelationName, Children) with a flattened, reporting-specific column descriptor.
    /// The monolith's 20+ <c>FieldType</c> enum values are simplified to basic reporting data
    /// types: "string", "number", "date", "boolean", "guid".
    /// </summary>
    public class ColumnDefinition
    {
        /// <summary>
        /// Column/field name identifier used as the key in each row dictionary.
        /// Maps from <c>DataSourceModelFieldMeta.Name</c> in the monolith.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The data type of the column for display and sorting purposes.
        /// Simplified from the monolith's <c>FieldType</c> enum (which had 20+ types
        /// such as AutoNumberField, CurrencyField, DateTimeField, etc.) to basic
        /// reporting data types: "string", "number", "date", "boolean", "guid".
        /// </summary>
        [JsonPropertyName("data_type")]
        public string DataType { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable display name for the column header in report output.
        /// When null, consumers should fall back to <see cref="Name"/>.
        /// </summary>
        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        /// <summary>
        /// Indicates whether this column supports sorting in the report result set.
        /// Defaults to <c>true</c> since most reporting columns are sortable.
        /// </summary>
        [JsonPropertyName("is_sortable")]
        public bool IsSortable { get; set; } = true;

        /// <summary>
        /// Indicates whether this column supports filtering in the report result set.
        /// Defaults to <c>true</c> since most reporting columns are filterable.
        /// </summary>
        [JsonPropertyName("is_filterable")]
        public bool IsFilterable { get; set; } = true;
    }

    /// <summary>
    /// Report execution result envelope returned by the Reporting &amp; Analytics microservice.
    /// Replaces the monolith's <c>EntityRecordList</c> return type from
    /// <c>DataSourceManager.Execute()</c> and the <c>QueryResult</c> model (which carried
    /// <c>FieldsMeta</c> + <c>Data</c>). This model combines column metadata, row data,
    /// pagination, execution metrics, and success/error tracking into a single structured
    /// response envelope.
    ///
    /// Key mappings from monolith source:
    /// <list type="bullet">
    ///   <item><c>Columns</c> — replaces <c>QueryResult.FieldsMeta : List&lt;Field&gt;</c>
    ///     and recursive <c>DataSourceModelFieldMeta</c> tree</item>
    ///   <item><c>Rows</c> — replaces <c>QueryResult.Data : List&lt;EntityRecord&gt;</c>
    ///     (where <c>EntityRecord</c> extends <c>Dictionary&lt;string, object&gt;</c>
    ///     via <c>Expando</c>)</item>
    ///   <item><c>TotalCount</c> — maps directly from <c>EntityRecordList.TotalCount</c></item>
    ///   <item><c>Success</c>/<c>ErrorMessage</c> — maps from <c>BaseResponseModel</c>
    ///     (Timestamp, Success, Message, Errors)</item>
    ///   <item><c>ExecutionDuration</c> — new field for observability per AAP §0.8.5</item>
    /// </list>
    /// </summary>
    public class ReportResult
    {
        /// <summary>
        /// The unique identifier of the report definition that was executed.
        /// Corresponds to the datasource ID passed to <c>DataSourceManager.Execute(Guid id, ...)</c>
        /// in the monolith.
        /// </summary>
        [JsonPropertyName("report_id")]
        public Guid ReportId { get; set; }

        /// <summary>
        /// Human-readable name of the executed report.
        /// Derived from the report definition's display name.
        /// Non-nullable; defaults to <see cref="string.Empty"/> to prevent null reference issues.
        /// </summary>
        [JsonPropertyName("report_name")]
        public string ReportName { get; set; } = string.Empty;

        /// <summary>
        /// Column metadata describing the structure of each row in the result set.
        /// Replaces the monolith's <c>QueryResult.FieldsMeta : List&lt;Field&gt;</c> and the
        /// recursive <c>DataSourceModelFieldMeta</c> tree with a flattened, reporting-specific
        /// column definition list.
        /// Non-nullable; initialized to an empty list to prevent null collection issues.
        /// </summary>
        [JsonPropertyName("columns")]
        public List<ColumnDefinition> Columns { get; set; } = new List<ColumnDefinition>();

        /// <summary>
        /// The actual data rows returned by the report query execution.
        /// Each row is a dictionary mapping column names to their values, preserving
        /// the monolith's dynamic record pattern where <c>EntityRecord</c> extends
        /// <c>Expando</c> (which is <c>IDictionary&lt;string, object&gt;</c>).
        /// Values are nullable to support database NULL values.
        /// Non-nullable list; initialized to an empty list to prevent null collection issues.
        /// </summary>
        [JsonPropertyName("rows")]
        public List<Dictionary<string, object?>> Rows { get; set; } = new List<Dictionary<string, object?>>();

        /// <summary>
        /// Total number of records matching the query before pagination is applied.
        /// Maps directly from <c>EntityRecordList.TotalCount</c> in the monolith.
        /// When pagination is active, this value may be greater than <c>Rows.Count</c>.
        /// </summary>
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        /// <summary>
        /// Current page number in the paginated result set (1-based indexing).
        /// Corresponds to the EQL <c>PAGE</c> clause from the monolith's query engine.
        /// Defaults to 1 (first page).
        /// </summary>
        [JsonPropertyName("page_number")]
        public int PageNumber { get; set; } = 1;

        /// <summary>
        /// Number of records per page in the paginated result set.
        /// Corresponds to the EQL <c>PAGESIZE</c> clause from the monolith's query engine.
        /// Defaults to 50 records per page.
        /// </summary>
        [JsonPropertyName("page_size")]
        public int PageSize { get; set; } = 50;

        /// <summary>
        /// Time taken to execute the report query, measured from query submission
        /// to result retrieval. This is a new field not present in the monolith —
        /// adds observability for report execution performance monitoring per AAP §0.8.5.
        /// Consumers can use this for performance dashboards and SLA tracking.
        /// </summary>
        [JsonPropertyName("execution_duration")]
        public TimeSpan ExecutionDuration { get; set; }

        /// <summary>
        /// UTC timestamp indicating when the report query execution completed.
        /// Used for cache invalidation, audit trails, and temporal correlation
        /// with other system events.
        /// Defaults to <c>DateTime.UtcNow</c> at object creation time.
        /// </summary>
        [JsonPropertyName("executed_at")]
        public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Indicates whether the report execution completed successfully.
        /// Maps from <c>BaseResponseModel.Success</c> in the monolith.
        /// When <c>false</c>, the <see cref="ErrorMessage"/> property contains
        /// details about the failure.
        /// Defaults to <c>true</c> (optimistic success).
        /// </summary>
        [JsonPropertyName("success")]
        public bool Success { get; set; } = true;

        /// <summary>
        /// Descriptive error message when the report execution fails.
        /// Null when execution is successful (<see cref="Success"/> is <c>true</c>).
        /// Replaces the monolith's <c>BaseResponseModel.Errors : List&lt;ErrorModel&gt;</c>
        /// with a single consolidated error string suitable for API responses.
        /// </summary>
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }
}
