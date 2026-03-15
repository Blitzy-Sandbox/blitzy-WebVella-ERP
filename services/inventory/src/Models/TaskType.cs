using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed model for task types in the Inventory (Project Management) service.
    /// Replaces the dynamic <c>EntityRecord</c> dictionary pattern from the monolith's
    /// <c>task_type</c> lookup entity.
    ///
    /// In the monolith, <c>task_type</c> is referenced via the <c>task_type_1n_task</c>
    /// one-to-many relation, where a task record's <c>type_id</c> foreign key points
    /// to this entity's <c>Id</c>. The <c>Label</c> property provides the display name
    /// (e.g., "Bug", "Feature", "Task") surfaced in task lists and detail views.
    ///
    /// Source references:
    /// - <c>TaskService.SetCalculationFields()</c> line 34: EQL <c>$task_type_1n_task.label</c>
    /// - <c>TaskService.SetCalculationFields()</c> lines 66-73: Extracting type label from related records
    /// - <c>TaskService</c> line 289: <c>record["type_id"]</c> foreign key on task records
    /// - <c>ProjectPlugin.20190203.cs</c> line 9603+: task_type entity schema with <c>id</c> and <c>label</c> fields
    ///
    /// DynamoDB mapping:
    /// - PK: <c>TASK_TYPE#{id}</c>
    /// - SK: <c>META</c>
    /// </summary>
    public class TaskType
    {
        /// <summary>
        /// Primary key for the task type record.
        /// Referenced as <c>type_id</c> foreign key on task records and used in
        /// the <c>task_type_1n_task</c> relation queries.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Display name for the task type (e.g., "Bug", "Feature", "Task").
        /// Retrieved via the <c>$task_type_1n_task.label</c> EQL relation navigation
        /// in the monolith's <c>TaskService.SetCalculationFields()</c> method.
        /// </summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;
    }
}
