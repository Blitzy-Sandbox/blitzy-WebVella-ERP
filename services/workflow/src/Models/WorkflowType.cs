using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.Workflow.Models
{
    /// <summary>
    /// Defines the priority levels for workflow execution scheduling.
    /// Replaces the monolith's <c>JobPriority</c> enum from
    /// <c>WebVella.Erp/Jobs/Models/JobType.cs</c> (lines 10-17).
    ///
    /// Integer values are preserved exactly from the source for deterministic
    /// DynamoDB persistence and backward-compatible data migration from the
    /// monolith's PostgreSQL <c>schedule_plans.type</c> column.
    ///
    /// The <see cref="JsonStringEnumConverter"/> attribute enables AOT-friendly
    /// string-based JSON serialization (e.g., <c>"Medium"</c> instead of <c>2</c>),
    /// replacing the Newtonsoft.Json default numeric serialization in the source.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowPriority>))]
    public enum WorkflowPriority
    {
        /// <summary>
        /// Lowest execution priority. Workflow runs after all higher-priority items.
        /// </summary>
        Low = 1,

        /// <summary>
        /// Standard execution priority. Default for most workflows.
        /// </summary>
        Medium = 2,

        /// <summary>
        /// Elevated execution priority. Preferred over Low and Medium workflows.
        /// </summary>
        High = 3,

        /// <summary>
        /// Second-highest execution priority.
        /// </summary>
        Higher = 4,

        /// <summary>
        /// Maximum execution priority. Scheduled before all other workflows.
        /// </summary>
        Highest = 5
    }

    /// <summary>
    /// Describes a concrete workflow implementation's metadata, including its
    /// identity, default priority, and the assembly-qualified class name that
    /// implements the workflow logic.
    ///
    /// Replaces the monolith's <c>JobType</c> class from
    /// <c>WebVella.Erp/Jobs/Models/JobType.cs</c> (lines 20-42).
    ///
    /// Key differences from the source <c>JobType</c>:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Renamed from <c>JobType</c> to <c>WorkflowType</c> to reflect the
    ///       serverless workflow engine domain (Step Functions + SQS-triggered Lambdas).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Removed <c>[Serializable]</c> attribute — not needed for Lambda/DynamoDB
    ///       serialization; replaced by System.Text.Json attributes for AOT compatibility.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Removed <c>[JsonIgnore] public Type ErpJobType</c> property — no
    ///       reflection-based <c>Activator.CreateInstance()</c> instantiation in Lambda.
    ///       Lambda handlers are directly invoked by the Step Functions state machine.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Replaced Newtonsoft.Json <c>[JsonProperty]</c> attributes with
    ///       System.Text.Json <c>[JsonPropertyName]</c> for Native AOT compatibility
    ///       (cold start &lt; 1 second per AAP Section 0.8.2).
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// All JSON property names (snake_case keys) are preserved exactly from the
    /// source for backward-compatible API responses and data migration.
    ///
    /// This model is referenced by <c>Workflow.cs</c>, <c>StepContext.cs</c>,
    /// and <c>SchedulePlan.cs</c> within the Workflow Engine service.
    /// </summary>
    public class WorkflowType
    {
        /// <summary>
        /// Unique identifier for this workflow type.
        /// Maps to the source <c>JobType.Id</c> (Guid) for data migration compatibility.
        /// JSON key: <c>"id"</c>.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Human-readable name of the workflow type (e.g., "SendEmailWorkflow",
        /// "StartTasksOnStartDate").
        /// Maps to the source <c>JobType.Name</c>.
        /// JSON key: <c>"name"</c>.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Default execution priority assigned to new workflow instances of this type.
        /// Type changed from <c>JobPriority</c> to <c>WorkflowPriority</c>.
        /// JSON key preserved as <c>"default_job_priority_id"</c> for backward compatibility
        /// with the monolith's serialized data in the <c>schedule_plans</c> table.
        /// </summary>
        [JsonPropertyName("default_job_priority_id")]
        public WorkflowPriority DefaultPriority { get; set; }

        /// <summary>
        /// The assembly name containing the workflow implementation class.
        /// Used for workflow type registration and discovery within the service.
        /// Maps to the source <c>JobType.Assembly</c>.
        /// JSON key: <c>"assembly"</c>.
        /// </summary>
        [JsonPropertyName("assembly")]
        public string Assembly { get; set; } = string.Empty;

        /// <summary>
        /// Fully qualified class name of the workflow implementation
        /// (e.g., "WebVellaErp.Workflow.Implementations.SendEmailWorkflow").
        /// In the monolith, this was used by <c>JobPool.cs</c> with
        /// <c>Activator.CreateInstance()</c> for reflection-based instantiation.
        /// In the serverless architecture, this identifies the Lambda handler
        /// or Step Function step configuration.
        /// Maps to the source <c>JobType.CompleteClassName</c>.
        /// JSON key: <c>"complete_class_name"</c>.
        /// </summary>
        [JsonPropertyName("complete_class_name")]
        public string CompleteClassName { get; set; } = string.Empty;

        /// <summary>
        /// When <c>true</c>, only one instance of this workflow type may execute
        /// concurrently. In the monolith, this was enforced by <c>JobPool.cs</c>
        /// using a locked dictionary check before dispatching. In the serverless
        /// architecture, this is enforced via DynamoDB conditional writes with an
        /// idempotency key per AAP Section 0.8.5.
        /// Maps to the source <c>JobType.AllowSingleInstance</c>.
        /// JSON key: <c>"allow_single_instance"</c>.
        /// </summary>
        [JsonPropertyName("allow_single_instance")]
        public bool AllowSingleInstance { get; set; }
    }
}
