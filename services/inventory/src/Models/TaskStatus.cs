using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed domain model representing a task status in the Inventory (Project Management) service.
    /// Replaces the dynamic <c>EntityRecord</c> dictionary pattern used in the monolith's
    /// <c>TaskService.GetTaskStatuses()</c> and task queue filtering logic.
    ///
    /// Source mapping:
    ///   - TaskService.cs lines 81-91: <c>GetTaskStatuses()</c> — retrieves all task_status records
    ///   - TaskService.cs lines 156-173: <c>GetTaskQueue()</c> — filters tasks by closed/open status
    ///   - ProjectPlugin.20190203.cs line 9603: task_status entity JSON schema definition
    ///
    /// DynamoDB single-table design:
    ///   PK: TASK_STATUS#{Id}
    ///   SK: META
    ///
    /// Example statuses: "Not Started", "In Progress", "Completed"
    /// </summary>
    public class TaskStatus
    {
        /// <summary>
        /// Primary key for the task status record.
        /// Maps to the monolith's <c>task_status.id</c> field, cast as <c>(Guid)taskStatus["id"]</c>
        /// in <c>TaskService.GetTaskQueue()</c> (line 162) for building the closed-status hashset
        /// used to exclude completed tasks from the task queue.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Human-readable display label for this task status.
        /// Maps to the monolith's <c>task_status.label</c> field, accessed as
        /// <c>(string)statusRecord["label"]</c> in <c>TaskService.SetCalculationFields()</c> (line 63).
        /// Typical values: "Not Started", "In Progress", "Completed", "On Hold", "Cancelled".
        /// </summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether this status represents a closed or completed state.
        /// Maps to the monolith's <c>task_status.is_closed</c> field, cast as
        /// <c>(bool)taskStatus["is_closed"]</c> in <c>TaskService.GetTaskQueue()</c> (line 160).
        /// When <c>true</c>, tasks with this status are excluded from active task queue queries
        /// by adding <c>status_id &lt;&gt; @statusN</c> filters (lines 166-172).
        /// </summary>
        [JsonPropertyName("is_closed")]
        public bool IsClosed { get; set; }

        /// <summary>
        /// Numeric ordering value for displaying task statuses in a sorted list.
        /// Maps to the monolith's <c>task_status.sort_order</c> field defined in the
        /// task_status entity JSON schema (ProjectPlugin.20190203.cs line 9603).
        /// Lower values appear first in status selection dropdowns and list views.
        /// </summary>
        [JsonPropertyName("sort_order")]
        public int SortOrder { get; set; }
    }
}
