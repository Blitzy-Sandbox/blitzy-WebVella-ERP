using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed domain model for time log entries in the Inventory (Project Management) service.
    /// Replaces the dynamic <c>EntityRecord</c> dictionary pattern used in the monolith's
    /// <c>TimeLogService.Create()</c> method (<c>WebVella.Erp.Plugins.Project/Services/TimeLogService.cs</c>).
    ///
    /// Timelogs track time spent on tasks with billable/non-billable classification.
    /// Each timelog entry records who logged time, when, for how long, and against which
    /// tasks/projects (via <see cref="LRelatedRecords"/>).
    ///
    /// JSON serialization uses <c>System.Text.Json</c> with snake_case property names for
    /// backward compatibility with the monolith's JSON conventions and AOT compatibility
    /// with .NET 9 Native AOT Lambda deployment (no Newtonsoft.Json dependency).
    ///
    /// Source mapping:
    ///   - <c>TimeLogService.Create()</c> lines 20-60: field assignments to EntityRecord dictionary
    ///   - <c>TimeLogService.PreCreateApiHookLogic()</c> lines 181-278: field type usage and casting
    ///   - <c>TimeLogService.GetTimelogsForPeriod()</c> lines 85-106: query field references
    ///   - <c>TimeLogService.PostApplicationNodePageHookLogic()</c> lines 108-179: form parsing types
    /// </summary>
    public class Timelog
    {
        /// <summary>
        /// Primary key for the timelog record.
        /// In the monolith, auto-generated via <c>Guid.NewGuid()</c> if null (TimeLogService.cs line 24-25).
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// The user who created/logged this time entry.
        /// Falls back to <c>SystemIds.SystemUserId</c> if null in the monolith (TimeLogService.cs line 27-28).
        /// Used for authorization checks in <c>TimeLogService.Delete()</c> (line 71).
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// Timestamp when the timelog record was created.
        /// Defaults to <c>DateTime.UtcNow</c> in the monolith (TimeLogService.cs line 30-31).
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// The date the work was actually performed — may differ from <see cref="CreatedOn"/>.
        /// Converted to UTC via <c>ConvertAppDateToUtc()</c> in the monolith (TimeLogService.cs line 43).
        /// Used in range queries: <c>WHERE logged_on &gt;= @startDate AND logged_on &lt; @endDate</c>
        /// (TimeLogService.GetTimelogsForPeriod, line 88).
        /// </summary>
        [JsonPropertyName("logged_on")]
        public DateTime LoggedOn { get; set; }

        /// <summary>
        /// Duration in minutes logged for this time entry.
        /// Type is <c>int</c> (not decimal) — confirmed by <c>Int32.TryParse</c> usage in the
        /// monolith controller (TimeLogService.cs line 136) and cast to <c>int</c> in
        /// PreCreateApiHookLogic (lines 232, 236). Default is 0 in the monolith (line 20).
        /// Zero-minute entries are typically not persisted (line 161-163).
        /// </summary>
        [JsonPropertyName("minutes")]
        public int Minutes { get; set; }

        /// <summary>
        /// Whether this time entry is billable.
        /// Default is <c>true</c> in the monolith (TimeLogService.cs line 20, line 127).
        /// Controls whether minutes are added to <c>x_billable_minutes</c> or
        /// <c>x_nonbillable_minutes</c> on the related task (PreCreateApiHookLogic lines 230-238).
        /// </summary>
        [JsonPropertyName("is_billable")]
        public bool IsBillable { get; set; }

        /// <summary>
        /// Description or notes for the time entry.
        /// Rendered as HTML snippet in feed items via <c>RenderService.GetSnippetFromHtml()</c>
        /// (TimeLogService.cs line 275). Default is empty string in the monolith (line 20).
        /// </summary>
        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        /// <summary>
        /// JSON-serialized list of scope strings (e.g., <c>["projects"]</c>).
        /// Stored as a JSON string for backward compatibility with the monolith's PostgreSQL
        /// column format where <c>JsonConvert.SerializeObject(scope)</c> was used
        /// (TimeLogService.cs line 47). Checked via <c>.Contains("projects")</c> in
        /// PreCreateApiHookLogic (line 190) to determine project-specific timelog handling.
        /// </summary>
        [JsonPropertyName("l_scope")]
        public string LScope { get; set; } = string.Empty;

        /// <summary>
        /// JSON-serialized list of related record GUIDs (task IDs, project IDs).
        /// Stored as a JSON string for backward compatibility with the monolith's PostgreSQL
        /// column format where <c>JsonConvert.SerializeObject(relatedRecords)</c> was used
        /// (TimeLogService.cs line 48). Deserialized via
        /// <c>JsonConvert.DeserializeObject&lt;List&lt;Guid&gt;&gt;()</c> in PreCreateApiHookLogic (line 202)
        /// to look up related tasks and projects. Also used in CONTAINS queries for
        /// period-based filtering (GetTimelogsForPeriod line 93).
        /// </summary>
        [JsonPropertyName("l_related_records")]
        public string LRelatedRecords { get; set; } = string.Empty;
    }
}
