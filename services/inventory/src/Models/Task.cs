using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed domain model for tasks — the primary entity of the Inventory
    /// (Project Management) service. Replaces the dynamic <c>EntityRecord</c> dictionary
    /// pattern used throughout the monolith's <c>TaskService.cs</c>, hook logic, and
    /// controller endpoints with a type-safe POCO.
    ///
    /// <para>
    /// <b>IMPORTANT:</b> This class name intentionally shadows <see cref="System.Threading.Tasks.Task"/>.
    /// When referencing the async Task type within files that import this model, use the
    /// fully-qualified name <c>System.Threading.Tasks.Task</c> or a using alias:
    /// <c>using SystemTask = System.Threading.Tasks.Task;</c>
    /// </para>
    ///
    /// <para>
    /// All JSON property names use snake_case to maintain backward compatibility with the
    /// monolith's PostgreSQL column naming convention and existing API consumers. Serialization
    /// uses <see cref="System.Text.Json"/> (NOT Newtonsoft.Json) for .NET 9 Native AOT
    /// compatibility per AAP Section 0.8.
    /// </para>
    ///
    /// <para>
    /// Source mapping:
    /// <list type="bullet">
    ///   <item><description><c>WebVella.Erp.Plugins.Project/Services/TaskService.cs</c> — field usage patterns</description></item>
    ///   <item><description><c>WebVella.Erp.Plugins.Project/ProjectPlugin.20190203.cs</c> (line 9727) — entity field definitions</description></item>
    ///   <item><description><c>WebVella.Erp.Plugins.Project/Services/TimeLogService.cs</c> — billable/non-billable minute calculations</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public class Task
    {
        /// <summary>
        /// Primary key — unique task identifier.
        /// Sourced from <c>taskRecord["id"]</c> (Guid) in the monolith's <c>TaskService</c>.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Task title/subject line displayed in listings and detail views.
        /// Sourced from <c>taskRecord["subject"]</c> (string) in <c>TaskService.SetCalculationFields()</c> line 41.
        /// Used in task queue display, feed activity subjects, and search indexing.
        /// </summary>
        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        /// <summary>
        /// Task description body, may contain HTML content.
        /// Sourced from <c>record["body"]</c> (string) in <c>TaskService.PostCreateApiHookLogic()</c> line 391.
        /// The monolith passes this through <c>RenderService.GetSnippetFromHtml()</c> for feed item creation.
        /// </summary>
        [JsonPropertyName("body")]
        public string? Body { get; set; }

        /// <summary>
        /// Auto-incrementing task number within its parent project scope.
        /// Type is <c>decimal</c> per the monolith pattern: <c>(decimal)taskRecord["number"]</c>
        /// (TaskService.cs line 76). The monolith's AutoNumber field type stores numbers as decimal.
        /// Used to construct the <see cref="Key"/> as <c>{project_abbr}-{number:N0}</c>.
        /// </summary>
        [JsonPropertyName("number")]
        public decimal? Number { get; set; }

        /// <summary>
        /// Computed task key combining project abbreviation and task number.
        /// Format: <c>{project_abbr}-{number}</c> (e.g., "PROJ-1", "PROJ-42").
        /// Set by <c>TaskService.SetCalculationFields()</c> line 76:
        /// <c>patchRecord["key"] = projectAbbr + "-" + ((decimal)taskRecord["number"]).ToString("N0")</c>.
        /// This is a read-friendly identifier for display and URL construction.
        /// </summary>
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        /// <summary>
        /// Foreign key reference to the task_status entity.
        /// Sourced from <c>taskRecord["status_id"]</c> (Guid) in <c>TaskService.SetStatus()</c> line 538
        /// and filtered in <c>GetTaskQueue()</c> line 169: <c>status_id &lt;&gt; @paramName</c>.
        /// Nullable because a task may not yet have a status assigned during creation.
        /// </summary>
        [JsonPropertyName("status_id")]
        public Guid? StatusId { get; set; }

        /// <summary>
        /// Foreign key reference to the task_type entity.
        /// Sourced from <c>newTask["type_id"]</c> in the recurrence template code (line 620).
        /// Nullable per usage patterns — tasks may exist without a type classification.
        /// </summary>
        [JsonPropertyName("type_id")]
        public Guid? TypeId { get; set; }

        /// <summary>
        /// Task priority value from a select field with icon/color rendering.
        /// Type is <c>string</c> (NOT enum) per EQL field type 17 (SelectField).
        /// Used in <c>TaskService.GetTaskIconAndColor()</c> for priority-based styling
        /// and as a sort field in <c>GetTaskQueue()</c> lines 188-200: <c>ORDER BY priority DESC</c>.
        /// Typical values: "1" (urgent), "2" (high), "3" (medium), "4" (low).
        /// </summary>
        [JsonPropertyName("priority")]
        public string? Priority { get; set; }

        /// <summary>
        /// Task assignee user ID — the user responsible for completing this task.
        /// Sourced from <c>record["owner_id"]</c> (Guid) in <c>PostCreateApiHookLogic()</c> line 347
        /// and filtered in <c>GetTaskQueue()</c> line 149: <c>owner_id = @userId</c>.
        /// Nullable because tasks may be created without an initial assignee.
        /// </summary>
        [JsonPropertyName("owner_id")]
        public Guid? OwnerId { get; set; }

        /// <summary>
        /// Task start date/time. Used for scheduling and queue filtering.
        /// Sourced from EQL filters in <c>GetTaskQueue()</c> lines 122-125:
        /// <c>(start_time &lt; @currentDateEnd OR start_time = null)</c>.
        /// Nullable — tasks may not have a defined start time.
        /// </summary>
        [JsonPropertyName("start_time")]
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// Task due date/time. Used for overdue detection and queue filtering.
        /// Sourced from EQL filters in <c>GetTaskQueue()</c> lines 128-133:
        /// <c>end_time &lt; @currentDateStart</c> (overdue),
        /// <c>(end_time &gt;= @currentDateStart AND end_time &lt; @currentDateEnd)</c> (due today).
        /// Nullable — tasks may not have a defined due date.
        /// </summary>
        [JsonPropertyName("end_time")]
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// Estimated effort in minutes for this task.
        /// Type is <c>decimal</c> per monolith field type 12 (NumberField).
        /// Sourced from <c>newTask["estimated_minutes"]</c> in recurrence template code (line 624).
        /// Used for project planning and capacity calculations.
        /// </summary>
        [JsonPropertyName("estimated_minutes")]
        public decimal? EstimatedMinutes { get; set; }

        /// <summary>
        /// Timestamp when the active timelog tracking was started for this task.
        /// Set to <c>DateTime.Now</c> in <c>TaskService.StartTaskTimelog()</c> (line 236)
        /// and cleared to <c>null</c> in <c>StopTaskTimelog()</c> (line 247).
        /// Nullable — null indicates no timelog is currently being tracked.
        /// </summary>
        [JsonPropertyName("timelog_started_on")]
        public DateTime? TimelogStartedOn { get; set; }

        /// <summary>
        /// Task completion timestamp. Set when the task transitions to a completed status.
        /// Sourced from <c>newTask["completed_on"] = null</c> in recurrence template code (line 616).
        /// Nullable — null indicates the task has not been completed.
        /// </summary>
        [JsonPropertyName("completed_on")]
        public DateTime? CompletedOn { get; set; }

        /// <summary>
        /// User ID of the task creator.
        /// Sourced from <c>record["created_by"]</c> (Guid) in <c>PostCreateApiHookLogic()</c> line 355.
        /// Used for watcher list initialization and activity feed attribution.
        /// Non-nullable — every task must have a creator.
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// Task creation timestamp.
        /// Sourced from <c>newTask["created_on"]</c> in recurrence template code (line 614).
        /// Non-nullable — every task must have a creation time.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// User ID of the last person who modified this task.
        /// New field for microservice audit trail — not present in the monolith's task entity
        /// but required per the target service specification for comprehensive audit logging.
        /// Nullable — null indicates the task has not been modified since creation.
        /// </summary>
        [JsonPropertyName("last_modified_by")]
        public Guid? LastModifiedBy { get; set; }

        /// <summary>
        /// Timestamp of the last modification to this task.
        /// New field for microservice audit trail — not present in the monolith's task entity
        /// but required per the target service specification for comprehensive audit logging.
        /// Nullable — null indicates the task has not been modified since creation.
        /// </summary>
        [JsonPropertyName("last_modified_on")]
        public DateTime? LastModifiedOn { get; set; }

        /// <summary>
        /// Accumulated billable minutes from all associated timelogs.
        /// Type is <c>decimal</c> per <c>TimeLogService.PreCreateApiHookLogic()</c> line 232:
        /// <c>(decimal)taskRecord["x_billable_minutes"]</c>.
        /// Updated on each timelog create/update/delete to maintain a running total.
        /// Initialized to 0 in recurrence template code (line 622).
        /// </summary>
        [JsonPropertyName("x_billable_minutes")]
        public decimal? XBillableMinutes { get; set; }

        /// <summary>
        /// Accumulated non-billable minutes from all associated timelogs.
        /// Type is <c>decimal</c> per <c>TimeLogService.PreCreateApiHookLogic()</c> line 236:
        /// <c>(decimal)taskRecord["x_nonbillable_minutes"]</c>.
        /// Updated on each timelog create/update/delete to maintain a running total.
        /// Initialized to 0 in recurrence template code (line 623).
        /// </summary>
        [JsonPropertyName("x_nonbillable_minutes")]
        public decimal? XNonBillableMinutes { get; set; }

        /// <summary>
        /// Groups recurring task instances together. All tasks generated from the same
        /// recurrence pattern share the same <c>RecurrenceId</c>.
        /// Sourced from <c>newTask["recurrence_id"] = recurrenceId</c> in recurrence template
        /// code (line 626), where <c>recurrenceId = Guid.NewGuid()</c>.
        /// Nullable — null indicates a non-recurring (standalone) task.
        /// </summary>
        [JsonPropertyName("recurrence_id")]
        public Guid? RecurrenceId { get; set; }

        /// <summary>
        /// JSON-serialized iCal.Net <c>RecurrenceTemplate</c> defining the recurrence pattern.
        /// Sourced from <c>newTask["recurrence_template"] = JsonConvert.SerializeObject(recurrenceData)</c>
        /// in recurrence template code (line 628).
        /// Stored as a JSON string for backward compatibility with the monolith's storage format.
        /// Nullable — null or empty for non-recurring tasks.
        /// </summary>
        [JsonPropertyName("recurrence_template")]
        public string? RecurrenceTemplate { get; set; }

        /// <summary>
        /// Whether this task should reserve time in the user's calendar.
        /// Type is <c>bool</c> per monolith field type 2 (CheckboxField).
        /// Sourced from <c>newTask["reserve_time"] = taskRecord["reserve_time"]</c>
        /// in recurrence template code (line 627).
        /// </summary>
        [JsonPropertyName("reserve_time")]
        public bool ReserveTime { get; set; }

        /// <summary>
        /// JSON-serialized list of scope strings defining the application context for this task.
        /// Example value: <c>["projects"]</c>.
        /// Sourced from <c>newTask["l_scope"] = taskRecord["l_scope"]</c> in recurrence template
        /// code (line 610). Used for activity feed scoping and access control.
        /// Stored as a JSON string for backward compatibility with the monolith's storage format.
        /// </summary>
        [JsonPropertyName("l_scope")]
        public string? LScope { get; set; }

        /// <summary>
        /// JSON-serialized list of related record GUIDs associated with this task.
        /// Used to link tasks to projects, users, and other entities for feed item correlation.
        /// Sourced from patterns in <c>PostCreateApiHookLogic()</c> lines 384-389 where
        /// <c>relatedRecords</c> aggregates task ID, project ID, and watcher user IDs.
        /// Stored as a JSON string for backward compatibility with the monolith's storage format.
        /// </summary>
        [JsonPropertyName("l_related_records")]
        public string? LRelatedRecords { get; set; }
    }
}
