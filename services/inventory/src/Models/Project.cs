using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Inventory.Models
{
    /// <summary>
    /// Strongly-typed domain model for projects in the Inventory (Project Management) service.
    /// Replaces the dynamic <c>EntityRecord</c> dictionary pattern used in the monolith's
    /// <c>ProjectService.Get()</c> and throughout the task/timelog service hooks.
    ///
    /// Source mapping:
    ///   - ProjectService.cs: <c>SELECT * from project WHERE id = @projectId</c>
    ///   - TaskService.cs: <c>$project_nn_task.abbr</c>, <c>$project_nn_task.id</c>,
    ///     <c>$project_nn_task.owner_id</c>, <c>$project_nn_task.is_billable</c>
    ///   - ProjectPlugin.20190203.cs: entity definition with name "projects"
    ///
    /// JSON property names use snake_case to maintain backward compatibility with the
    /// monolith's <c>EntityRecord</c> key conventions (e.g., "owner_id", "account_id").
    /// Uses <c>System.Text.Json</c> attributes for Native AOT compatibility with
    /// .NET 9 Lambda deployment (no Newtonsoft.Json dependency).
    /// </summary>
    public class Project
    {
        /// <summary>
        /// Primary key for the project record.
        /// Maps to the monolith's <c>project.id</c> entity field (AutoNumber/Guid).
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Project display name shown in the UI and referenced in dashboards,
        /// task lists, and project selection dropdowns.
        /// Maps to the monolith's <c>project.name</c> entity field.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Project abbreviation used in task key generation.
        /// For example, if <c>Abbr</c> is "PROJ" and a task's number is 42,
        /// the resulting task key is "PROJ-42".
        /// Referenced in <c>TaskService.SetCalculationFields()</c> line 76:
        /// <c>projectAbbr + "-" + number</c>.
        /// Maps to the monolith's <c>project.abbr</c> entity field.
        /// </summary>
        [JsonPropertyName("abbr")]
        public string Abbr { get; set; } = string.Empty;

        /// <summary>
        /// User ID of the project owner/lead.
        /// Referenced in <c>TaskService.SetCalculationFields()</c> line 55:
        /// <c>(Guid?)projectRecord["owner_id"]</c>.
        /// Maps to the monolith's <c>project.owner_id</c> entity field.
        /// Links to the Identity service's user records.
        /// </summary>
        [JsonPropertyName("owner_id")]
        public Guid OwnerId { get; set; }

        /// <summary>
        /// Associated CRM account ID (nullable, cross-domain reference).
        /// Not all projects have an associated CRM account, so this field is nullable.
        /// Links to the CRM service's account records for billing and client tracking.
        /// Maps to the monolith's <c>project.account_id</c> entity field.
        /// </summary>
        [JsonPropertyName("account_id")]
        public Guid? AccountId { get; set; }

        /// <summary>
        /// Whether the project's time entries are billable.
        /// Referenced in <c>TaskService.GetTaskQueue()</c> line 111:
        /// <c>$project_nn_task.is_billable</c>.
        /// Used by timelog and budget reporting to classify tracked hours.
        /// Maps to the monolith's <c>project.is_billable</c> entity field.
        /// </summary>
        [JsonPropertyName("is_billable")]
        public bool IsBillable { get; set; }

        /// <summary>
        /// User ID of the user who created this project record.
        /// Used for audit trail and ownership tracking.
        /// Maps to the monolith's <c>project.created_by</c> entity field.
        /// Links to the Identity service's user records.
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid CreatedBy { get; set; }

        /// <summary>
        /// Timestamp when the project record was created.
        /// Stored in UTC for consistency across services.
        /// Used for audit trail, sorting, and reporting.
        /// Maps to the monolith's <c>project.created_on</c> entity field.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }
    }
}
