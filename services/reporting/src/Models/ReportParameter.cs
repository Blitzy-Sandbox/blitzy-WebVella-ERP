using System.Text.Json.Serialization;

namespace WebVellaErp.Reporting.Models
{
    /// <summary>
    /// Defines a typed parameter for parameterized report queries in the Reporting &amp; Analytics service.
    /// 
    /// Replaces the monolith's <c>DataSourceParameter</c> model with enhanced reporting-specific
    /// metadata for the React frontend's report parameter form. Parameters are resolved at execution
    /// time by parsing <see cref="DefaultValue"/> according to the declared <see cref="Type"/>.
    /// 
    /// Supported parameter types (from the monolith's <c>DataSourceManager.GetDataSourceParameterValue()</c>):
    /// <list type="bullet">
    ///   <item><term>guid</term><description>Parsed as <see cref="Guid"/>. Special values: "null", "guid.empty".</description></item>
    ///   <item><term>int</term><description>Parsed as <see cref="int"/>. Special values: "null".</description></item>
    ///   <item><term>decimal</term><description>Parsed as <see cref="decimal"/>. Special values: "null".</description></item>
    ///   <item><term>date</term><description>Parsed as <see cref="DateTime"/>. Special values: "null", "now", "utc_now".</description></item>
    ///   <item><term>text</term><description>Raw string value. Special values: "null", "string.empty".</description></item>
    ///   <item><term>bool</term><description>Parsed as <see cref="bool"/>. Special values: "null", "true", "false".</description></item>
    /// </list>
    /// 
    /// This model is referenced by <c>ReportDefinition.Parameters</c> as <c>List&lt;ReportParameter&gt;</c>.
    /// All JSON serialization uses <see cref="JsonPropertyNameAttribute"/> (System.Text.Json) for
    /// Native AOT compatibility — the monolith's Newtonsoft.Json attributes are not used.
    /// </summary>
    public class ReportParameter
    {
        /// <summary>
        /// Parameter name used in the SQL template placeholder (e.g., "@startDate", "@userId").
        /// Maps directly from the monolith's <c>DataSourceParameter.Name</c>.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Parameter data type controlling how <see cref="DefaultValue"/> is parsed at execution time.
        /// Must be one of the six supported types: "guid", "int", "decimal", "date", "text", "bool".
        /// Maps directly from the monolith's <c>DataSourceParameter.Type</c>.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Default value for the parameter as a string, parsed according to <see cref="Type"/> at execution time.
        /// Renamed from the monolith's <c>DataSourceParameter.Value</c> to clarify its role in a report context.
        /// 
        /// Supports special values inherited from the monolith:
        /// "null" (all types), "guid.empty" (guid), "now"/"utc_now" (date),
        /// "string.empty" (text), "true"/"false" (bool).
        /// When <c>null</c>, the parameter has no default and must be supplied at execution time
        /// (unless <see cref="IsRequired"/> is <c>false</c>).
        /// </summary>
        [JsonPropertyName("default_value")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// When <c>true</c>, parse failures for this parameter's value silently return <c>null</c>
        /// instead of throwing an exception. When <c>false</c> (default), invalid values cause
        /// an execution error. Maps directly from the monolith's <c>DataSourceParameter.IgnoreParseErrors</c>.
        /// </summary>
        [JsonPropertyName("ignore_parse_errors")]
        public bool IgnoreParseErrors { get; set; } = false;

        /// <summary>
        /// Human-readable label for the parameter, displayed in the React frontend's report
        /// parameter input form. When <c>null</c>, the UI falls back to <see cref="Name"/>.
        /// This is a NEW property not present in the monolith.
        /// </summary>
        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        /// <summary>
        /// Description or help text for the parameter, shown as a tooltip or helper in the
        /// React frontend's report parameter form.
        /// This is a NEW property not present in the monolith.
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Whether this parameter must be provided for report execution. When <c>true</c> (default),
        /// the report engine will reject execution if this parameter is not supplied and has no
        /// <see cref="DefaultValue"/>. This is a NEW property not present in the monolith.
        /// </summary>
        [JsonPropertyName("is_required")]
        public bool IsRequired { get; set; } = true;

        /// <summary>
        /// Display ordering position for this parameter in the UI. Lower values appear first.
        /// Default is <c>0</c>. This is a NEW property not present in the monolith.
        /// </summary>
        [JsonPropertyName("sort_order")]
        public int SortOrder { get; set; } = 0;
    }
}
