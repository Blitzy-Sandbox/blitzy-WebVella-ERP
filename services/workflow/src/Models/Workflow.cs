using System;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Workflow.Models
{
    /// <summary>
    /// Defines the execution lifecycle states of a workflow instance.
    /// Replaces the monolith's <c>JobStatus</c> enum from
    /// <c>WebVella.Erp/Jobs/Models/Job.cs</c> (lines 7-15).
    ///
    /// Integer values are preserved exactly from the source for deterministic
    /// DynamoDB persistence and backward-compatible data migration from the
    /// monolith's PostgreSQL <c>jobs.status</c> column.
    ///
    /// The <see cref="JsonStringEnumConverter"/> attribute enables AOT-friendly
    /// string-based JSON serialization (e.g., <c>"Running"</c> instead of <c>2</c>),
    /// replacing the Newtonsoft.Json default numeric serialization in the source.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    public enum WorkflowStatus
    {
        /// <summary>Workflow is queued and awaiting execution.</summary>
        Pending = 1,

        /// <summary>Workflow is currently executing (Step Functions state machine active).</summary>
        Running = 2,

        /// <summary>Workflow was explicitly canceled by a user before completion.</summary>
        Canceled = 3,

        /// <summary>Workflow execution encountered an unrecoverable error.</summary>
        Failed = 4,

        /// <summary>Workflow completed successfully.</summary>
        Finished = 5,

        /// <summary>Workflow was forcibly terminated by an administrator.</summary>
        Aborted = 6
    }

    /// <summary>
    /// Primary domain DTO for the Workflow Engine microservice, representing a single
    /// workflow execution instance with its full lifecycle metadata.
    ///
    /// Replaces the monolith's <c>Job</c> class from
    /// <c>WebVella.Erp/Jobs/Models/Job.cs</c> (lines 24-83).
    ///
    /// Key differences from the source <c>Job</c> class:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Renamed from <c>Job</c> to <c>Workflow</c> to reflect the serverless
    ///       workflow engine domain (Step Functions + SQS-triggered Lambdas).
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
    ///       Changed <c>dynamic Attributes</c> and <c>dynamic Result</c> to
    ///       <c>Dictionary&lt;string, object&gt;?</c> for Native AOT compatibility.
    ///       These properties retain dual serialization (System.Text.Json + Newtonsoft.Json)
    ///       for backward-compatible data migration from the monolith's PostgreSQL persistence.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Changed <c>JobType Type</c> to <c>WorkflowType? Type</c>,
    ///       <c>JobStatus Status</c> to <c>WorkflowStatus Status</c>, and
    ///       <c>JobPriority Priority</c> to <c>WorkflowPriority Priority</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Added <c>StepFunctionsExecutionArn</c> for AWS Step Functions integration —
    ///       new property not present in the source, required by the serverless architecture.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Replaced Newtonsoft.Json <c>[JsonProperty]</c> with System.Text.Json
    ///       <c>[JsonPropertyName]</c> for most properties (AOT-compatible).
    ///       The <c>Attributes</c> and <c>Result</c> properties retain both attributes
    ///       for backward compatibility with the monolith's serialization format.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// The internal <c>JobResultWrapper</c> class from the source (lines 17-22) is
    /// intentionally excluded — it was a Newtonsoft.Json <c>TypeNameHandling.All</c>
    /// serialization helper used by <c>JobDataService.cs</c> for PostgreSQL persistence.
    /// DynamoDB uses a different serialization approach.
    ///
    /// All JSON property names (snake_case keys) are preserved exactly from the source
    /// for backward-compatible API responses and data migration per AAP Section 0.8.1.
    /// </summary>
    public class Workflow
    {
        /// <summary>
        /// Unique identifier for this workflow execution instance.
        /// Maps to the source <c>Job.Id</c> (Guid) for data migration compatibility.
        /// JSON key: <c>"id"</c>.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Foreign key reference to the <see cref="WorkflowType"/> that defines this
        /// workflow's implementation. Maps to the source <c>Job.TypeId</c>.
        /// JSON key: <c>"type_id"</c>.
        /// </summary>
        [JsonPropertyName("type_id")]
        public Guid TypeId { get; set; }

        /// <summary>
        /// Navigation property to the <see cref="WorkflowType"/> describing the workflow
        /// implementation metadata. Type changed from <c>JobType</c> to <c>WorkflowType</c>.
        /// JSON key: <c>"type"</c>.
        /// </summary>
        [JsonPropertyName("type")]
        public WorkflowType? Type { get; set; }

        /// <summary>
        /// Human-readable name of the workflow type for display purposes.
        /// Maps to the source <c>Job.TypeName</c>.
        /// JSON key: <c>"type_name"</c>.
        /// </summary>
        [JsonPropertyName("type_name")]
        public string? TypeName { get; set; }

        /// <summary>
        /// Fully qualified class name of the workflow implementation. In the monolith,
        /// this was used for reflection-based instantiation via <c>Activator.CreateInstance()</c>
        /// in <c>JobPool.cs</c>. In the serverless architecture, this identifies the
        /// Lambda handler or Step Function step configuration.
        /// Maps to the source <c>Job.CompleteClassName</c>.
        /// JSON key: <c>"complete_class_name"</c>.
        /// </summary>
        [JsonPropertyName("complete_class_name")]
        public string? CompleteClassName { get; set; }

        /// <summary>
        /// Arbitrary key-value attributes passed to the workflow at creation time.
        /// Equivalent to the source <c>Job.Attributes</c> (was <c>dynamic</c>).
        ///
        /// Type changed from <c>dynamic</c> to <c>Dictionary&lt;string, object&gt;?</c>
        /// for Native AOT compatibility with System.Text.Json serialization.
        ///
        /// Retains dual serialization (both <c>[JsonPropertyName]</c> and
        /// <c>[Newtonsoft.Json.JsonProperty]</c>) for backward-compatible data migration
        /// from the monolith's PostgreSQL persistence format.
        /// JSON key: <c>"attributes"</c>.
        /// </summary>
        [JsonPropertyName("attributes")]
        [Newtonsoft.Json.JsonProperty(PropertyName = "attributes")]
        public Dictionary<string, object>? Attributes { get; set; }

        /// <summary>
        /// Workflow execution result data populated upon completion or failure.
        /// Equivalent to the source <c>Job.Result</c> (was <c>dynamic</c>).
        ///
        /// Type changed from <c>dynamic</c> to <c>Dictionary&lt;string, object&gt;?</c>
        /// for Native AOT compatibility with System.Text.Json serialization.
        ///
        /// Retains dual serialization (both <c>[JsonPropertyName]</c> and
        /// <c>[Newtonsoft.Json.JsonProperty]</c>) for backward-compatible data migration
        /// from the monolith's PostgreSQL persistence format.
        /// JSON key: <c>"result"</c>.
        /// </summary>
        [JsonPropertyName("result")]
        [Newtonsoft.Json.JsonProperty(PropertyName = "result")]
        public Dictionary<string, object>? Result { get; set; }

        /// <summary>
        /// Current execution lifecycle state of this workflow instance.
        /// Type changed from <c>JobStatus</c> to <c>WorkflowStatus</c>.
        /// JSON key: <c>"status"</c>.
        /// </summary>
        [JsonPropertyName("status")]
        public WorkflowStatus Status { get; set; }

        /// <summary>
        /// Execution priority level for this workflow instance.
        /// Type changed from <c>JobPriority</c> to <c>WorkflowPriority</c>.
        /// JSON key: <c>"priority"</c>.
        /// </summary>
        [JsonPropertyName("priority")]
        public WorkflowPriority Priority { get; set; }

        /// <summary>
        /// Timestamp when the workflow execution began (Step Functions StartExecution).
        /// Null if the workflow is still in <see cref="WorkflowStatus.Pending"/> state.
        /// Maps to the source <c>Job.StartedOn</c>.
        /// JSON key: <c>"started_on"</c>.
        /// </summary>
        [JsonPropertyName("started_on")]
        public DateTime? StartedOn { get; set; }

        /// <summary>
        /// Timestamp when the workflow execution completed (any terminal state).
        /// Null if the workflow has not yet reached a terminal state.
        /// Maps to the source <c>Job.FinishedOn</c>.
        /// JSON key: <c>"finished_on"</c>.
        /// </summary>
        [JsonPropertyName("finished_on")]
        public DateTime? FinishedOn { get; set; }

        /// <summary>
        /// Identifier of the user who aborted this workflow, if applicable.
        /// Null unless <see cref="Status"/> is <see cref="WorkflowStatus.Aborted"/>.
        /// Maps to the source <c>Job.AbortedBy</c>.
        /// JSON key: <c>"aborted_by"</c>.
        /// </summary>
        [JsonPropertyName("aborted_by")]
        public Guid? AbortedBy { get; set; }

        /// <summary>
        /// Identifier of the user who canceled this workflow, if applicable.
        /// Null unless <see cref="Status"/> is <see cref="WorkflowStatus.Canceled"/>.
        /// Maps to the source <c>Job.CanceledBy</c>.
        /// JSON key: <c>"canceled_by"</c>.
        /// </summary>
        [JsonPropertyName("canceled_by")]
        public Guid? CanceledBy { get; set; }

        /// <summary>
        /// Error message captured when the workflow transitions to
        /// <see cref="WorkflowStatus.Failed"/> state.
        /// Null when no error has occurred.
        /// Maps to the source <c>Job.ErrorMessage</c>.
        /// JSON key: <c>"error_message"</c>.
        /// </summary>
        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Foreign key reference to the schedule plan that triggered this workflow instance.
        /// Null for ad-hoc (manually triggered) workflow executions.
        /// Maps to the source <c>Job.SchedulePlanId</c>.
        /// JSON key: <c>"schedule_plan_id"</c>.
        /// </summary>
        [JsonPropertyName("schedule_plan_id")]
        public Guid? SchedulePlanId { get; set; }

        /// <summary>
        /// Timestamp when the workflow record was first created.
        /// Maps to the source <c>Job.CreatedOn</c>.
        /// JSON key: <c>"created_on"</c>.
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Identifier of the user who created this workflow instance.
        /// Null for system-initiated workflows (e.g., scheduled triggers).
        /// Maps to the source <c>Job.CreatedBy</c>.
        /// JSON key: <c>"created_by"</c>.
        /// </summary>
        [JsonPropertyName("created_by")]
        public Guid? CreatedBy { get; set; }

        /// <summary>
        /// Timestamp of the most recent modification to this workflow record.
        /// Updated on every status transition, result update, or metadata change.
        /// Maps to the source <c>Job.LastModifiedOn</c>.
        /// JSON key: <c>"last_modified_on"</c>.
        /// </summary>
        [JsonPropertyName("last_modified_on")]
        public DateTime LastModifiedOn { get; set; }

        /// <summary>
        /// Identifier of the user who last modified this workflow record.
        /// Null for system-initiated modifications (e.g., Step Functions callbacks).
        /// Maps to the source <c>Job.LastModifiedBy</c>.
        /// JSON key: <c>"last_modified_by"</c>.
        /// </summary>
        [JsonPropertyName("last_modified_by")]
        public Guid? LastModifiedBy { get; set; }

        /// <summary>
        /// AWS Step Functions execution ARN for this workflow instance.
        /// This property is NEW — not present in the source <c>Job</c> class.
        /// Required by the AAP's transformation to Step Functions-based workflow
        /// orchestration, enabling direct correlation between the domain model
        /// and the AWS Step Functions execution lifecycle (start, stop, status queries).
        /// JSON key: <c>"step_functions_execution_arn"</c>.
        /// </summary>
        [JsonPropertyName("step_functions_execution_arn")]
        public string? StepFunctionsExecutionArn { get; set; }
    }
}
