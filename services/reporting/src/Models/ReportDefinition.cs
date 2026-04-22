using System.Text.Json.Serialization;

namespace WebVellaErp.Reporting.Models
{
    /// <summary>
    /// Core report metadata model for the Reporting &amp; Analytics microservice.
    /// 
    /// Defines the structure of a report definition including its identity, parameterized SQL
    /// query template, parameter schema, ownership tracking, and timestamps. This model replaces
    /// the monolith's <c>DatabaseDataSource</c> (from <c>DataSourceManager.cs</c>) and
    /// <c>DataSourceBase</c> abstract class with a reporting-specific, purpose-built POCO.
    /// 
    /// Key differences from monolith source:
    /// <list type="bullet">
    ///   <item><term>SqlTemplate</term><description>
    ///     Replaces <c>DatabaseDataSource.SqlText</c> and <c>DatabaseDataSource.EqlText</c> as
    ///     the primary query definition. The Reporting service connects to RDS PostgreSQL directly
    ///     and does not need EQL translation (see AAP §0.7.1).
    ///   </description></item>
    ///   <item><term>EqlTemplate</term><description>
    ///     Retained as nullable for backward compatibility during migration from the monolith's
    ///     EQL-based datasources.
    ///   </description></item>
    ///   <item><term>Parameters</term><description>
    ///     Uses <see cref="ReportParameter"/> instead of <c>DataSourceParameter</c>, with enhanced
    ///     metadata (DisplayName, Description, IsRequired, SortOrder) for the React frontend.
    ///   </description></item>
    ///   <item><term>Ownership</term><description>
    ///     Adds <see cref="CreatedBy"/>, <see cref="CreatedAt"/>, <see cref="UpdatedAt"/>, and
    ///     <see cref="IsActive"/> — fields absent from the monolith's datasource model. User identity
    ///     is extracted from JWT claims in the Lambda event context (AAP §0.5.2).
    ///   </description></item>
    ///   <item><term>Category</term><description>
    ///     New organizational field for grouping reports in the UI.
    ///   </description></item>
    /// </list>
    /// 
    /// This model represents report metadata stored in the Reporting service's RDS PostgreSQL
    /// <c>report_definitions</c> table. No database-specific or ORM annotations are present —
    /// persistence mapping is handled by <c>ReportRepository</c>.
    /// 
    /// All JSON serialization uses <see cref="JsonPropertyNameAttribute"/> (System.Text.Json)
    /// for Native AOT compatibility (AAP §0.8.2) — the monolith's Newtonsoft.Json attributes
    /// are not used on this model.
    /// </summary>
    public class ReportDefinition
    {
        /// <summary>
        /// Primary key identifier for the report definition.
        /// Default value generates a new unique identifier on construction.
        /// Maps directly from <c>DataSourceBase.Id</c> (Guid).
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Report name, unique within the Reporting service.
        /// Used as the primary human-readable identifier in the React frontend and API responses.
        /// Maps from <c>DataSourceBase.Name</c>.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description of the report's purpose, data scope, and intended audience.
        /// Displayed in the report listing and detail views in the React frontend.
        /// Maps from <c>DataSourceBase.Description</c>.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The parameterized SQL query template for report execution against RDS PostgreSQL.
        /// Parameters are referenced as <c>@paramName</c> placeholders and resolved at execution
        /// time from the <see cref="Parameters"/> collection using Npgsql parameterized queries.
        /// 
        /// This replaces both <c>DatabaseDataSource.EqlText</c> and <c>DatabaseDataSource.SqlText</c>
        /// from the monolith. The Reporting service uses direct SQL against RDS PostgreSQL — no EQL
        /// translation is needed (AAP §0.7.1: "Invoicing / Reporting Services — Use standard Npgsql
        /// SQL (no EQL translation needed) since they connect to RDS PostgreSQL directly").
        /// </summary>
        [JsonPropertyName("sql_template")]
        public string SqlTemplate { get; set; } = string.Empty;

        /// <summary>
        /// Optional legacy EQL (Entity Query Language) template retained for backward compatibility
        /// during migration from the monolith's EQL-based datasources. When present, this preserves
        /// the original EQL query text that was used to generate the <see cref="SqlTemplate"/>.
        /// 
        /// This field is nullable because new report definitions created in the target architecture
        /// will only use direct SQL. Maps from <c>DatabaseDataSource.EqlText</c>.
        /// </summary>
        [JsonPropertyName("eql_template")]
        public string? EqlTemplate { get; set; }

        /// <summary>
        /// List of typed parameters that the <see cref="SqlTemplate"/> expects. Each parameter
        /// defines its name (matching the SQL placeholder), data type, optional default value,
        /// and UI metadata for the React frontend's report parameter input form.
        /// 
        /// Maps from <c>DataSourceBase.Parameters</c> (<c>List&lt;DataSourceParameter&gt;</c>),
        /// but uses the enhanced <see cref="ReportParameter"/> model with additional properties
        /// for display name, description, required flag, and sort order.
        /// 
        /// The collection is initialized to an empty list to prevent null reference exceptions
        /// during serialization and parameter iteration, following the monolith's pattern where
        /// <c>DataSourceBase.Parameters</c> was initialized as <c>new List&lt;DataSourceParameter&gt;()</c>.
        /// </summary>
        [JsonPropertyName("parameters")]
        public List<ReportParameter> Parameters { get; set; } = new List<ReportParameter>();

        /// <summary>
        /// Whether the report query should also return the total count of matching rows for
        /// pagination support. When <c>true</c> (default), the report execution engine wraps
        /// the <see cref="SqlTemplate"/> with a <c>COUNT(*) OVER()</c> window function or
        /// executes a separate count query.
        /// 
        /// Maps directly from <c>DataSourceBase.ReturnTotal</c> (default <c>true</c>).
        /// </summary>
        [JsonPropertyName("return_total")]
        public bool ReturnTotal { get; set; } = true;

        /// <summary>
        /// The expected result model type descriptor that indicates the shape of the report
        /// execution output. Used by the frontend to select the appropriate rendering component
        /// for the report results.
        /// 
        /// Default value is <c>"ReportResult"</c>, replacing the monolith's
        /// <c>DatabaseDataSource.ResultModel</c> default of <c>"EntityRecordList"</c>.
        /// Maps from <c>DataSourceBase.ResultModel</c>.
        /// </summary>
        [JsonPropertyName("result_model")]
        public string ResultModel { get; set; } = "ReportResult";

        /// <summary>
        /// User ID of the report creator, extracted from JWT claims in the Lambda event context
        /// (AAP §0.5.2). This is a NEW property not present in the monolith's datasource model,
        /// where ownership was not tracked.
        /// 
        /// Nullable because system-seeded reports may not have a specific creator. Uses <c>Guid?</c>
        /// matching the monolith's pattern where user IDs are <c>Guid</c> (per <c>SystemIds</c>
        /// in <c>Definitions.cs</c>).
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid? CreatedBy { get; set; }

        /// <summary>
        /// UTC timestamp of when the report definition was created. Automatically set to
        /// <see cref="DateTime.UtcNow"/> on construction for new report definitions.
        /// 
        /// This is a NEW property not present in the monolith's datasource model.
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC timestamp of the last update to the report definition. Automatically set to
        /// <see cref="DateTime.UtcNow"/> on construction and should be updated on every
        /// modification by the service layer.
        /// 
        /// This is a NEW property not present in the monolith's datasource model.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether the report definition is active and available for execution. When <c>false</c>,
        /// the report is hidden from the React frontend listing and cannot be executed via the API.
        /// Supports soft-delete semantics — reports can be deactivated without data loss.
        /// 
        /// Default is <c>true</c>, following the monolith's pattern where boolean flags default
        /// to <c>true</c> for active/enabled states.
        /// This is a NEW property not present in the monolith's datasource model.
        /// </summary>
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Optional category for grouping reports in the React frontend UI (e.g., "Financial",
        /// "CRM", "Project", "Inventory"). Enables filtering and organized navigation of report
        /// definitions in the report listing page.
        /// 
        /// Nullable because categorization is optional — uncategorized reports appear in a
        /// default/general group. This is a NEW property not present in the monolith.
        /// </summary>
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        /// <summary>
        /// Sort weight for display ordering in the React frontend's report listing.
        /// Lower values appear first. Default is <c>10</c>, matching the monolith's
        /// <c>DataSourceBase.Weight</c> default value.
        /// </summary>
        [JsonPropertyName("weight")]
        public int Weight { get; set; } = 10;
    }
}
