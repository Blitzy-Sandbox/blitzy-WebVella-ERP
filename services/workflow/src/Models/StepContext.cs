using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Workflow.Models
{
    /// <summary>
    /// Represents the execution context passed to individual Step Functions steps,
    /// replacing the monolith's in-process <c>JobContext</c> class from
    /// <c>WebVella.Erp/Jobs/Models/JobContext.cs</c>.
    ///
    /// In the monolith, <c>JobContext</c> was instantiation-restricted via an
    /// <c>internal</c> constructor (infrastructure-owned creation by <c>JobPool.cs</c>).
    /// In the serverless architecture, <c>StepContext</c> is externally constructible
    /// because Lambda handlers construct it from Step Functions state machine input.
    ///
    /// Key differences from the source <c>JobContext</c>:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Renamed from <c>JobContext</c> to <c>StepContext</c> to reflect the
    ///       Step Functions + SQS-triggered Lambda execution model.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Property <c>JobId</c> renamed to <c>WorkflowId</c> (JSON key preserved
    ///       as <c>"job_id"</c> for backward data migration compatibility).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Type references updated: <c>JobPriority</c> → <c>WorkflowPriority</c>,
    ///       <c>JobType</c> → <c>WorkflowType</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <c>dynamic</c> properties (<c>Attributes</c>, <c>Result</c>) replaced
    ///       with <c>Dictionary&lt;string, object&gt;?</c> for Native AOT Lambda
    ///       serialization compatibility (cold start &lt; 1 second per AAP Section 0.8.2).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Internal constructor removed — Lambda context is externally constructed
    ///       from Step Functions input payloads.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Added <c>StepFunctionsExecutionArn</c> and <c>StepName</c> properties
    ///       for AWS Step Functions execution correlation and step identification.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Dual serialization on dynamic-origin properties: System.Text.Json
    ///       <c>[JsonPropertyName]</c> for AOT, Newtonsoft.Json <c>[JsonProperty]</c>
    ///       for backward compatibility with monolith-serialized data.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public class StepContext
    {
        /// <summary>
        /// Unique identifier for this workflow execution instance.
        /// Renamed from <c>JobContext.JobId</c> to align with the workflow domain.
        /// JSON key preserved as <c>"job_id"</c> for backward data migration
        /// compatibility with the monolith's PostgreSQL <c>jobs</c> table.
        /// </summary>
        [JsonPropertyName("job_id")]
        public Guid WorkflowId { get; set; }

        /// <summary>
        /// Indicates whether this workflow execution has been aborted.
        /// When <c>true</c>, the Step Functions step should terminate gracefully.
        /// Preserves the abort signaling mechanism from the monolith's
        /// <c>JobPool.cs</c> thread cancellation pattern.
        /// JSON key: <c>"aborted"</c>.
        /// </summary>
        [JsonPropertyName("aborted")]
        public bool Aborted { get; set; }

        /// <summary>
        /// Execution priority level for this workflow step.
        /// Type changed from <c>JobPriority</c> to <c>WorkflowPriority</c>.
        /// Used by the workflow scheduler to order pending step executions.
        /// JSON key: <c>"priority"</c>.
        /// </summary>
        [JsonPropertyName("priority")]
        public WorkflowPriority Priority { get; set; }

        /// <summary>
        /// Key-value bag of input attributes passed to this workflow step.
        /// Changed from <c>dynamic</c> to <c>Dictionary&lt;string, object&gt;?</c>
        /// for Native AOT Lambda serialization compatibility.
        ///
        /// Dual serialization attributes are applied for backward compatibility:
        /// System.Text.Json <c>[JsonPropertyName]</c> for AOT-optimized Lambda
        /// handlers, and Newtonsoft.Json <c>[JsonProperty]</c> for deserializing
        /// data migrated from the monolith's PostgreSQL <c>jobs</c> table where
        /// attributes were serialized with Newtonsoft.Json.
        ///
        /// JSON key: <c>"attributes"</c>.
        /// </summary>
        [JsonPropertyName("attributes")]
        [Newtonsoft.Json.JsonProperty(PropertyName = "attributes")]
        public Dictionary<string, object>? Attributes { get; set; }

        /// <summary>
        /// Key-value bag of output results produced by this workflow step.
        /// Changed from <c>dynamic</c> to <c>Dictionary&lt;string, object&gt;?</c>
        /// for Native AOT Lambda serialization compatibility.
        ///
        /// Dual serialization attributes are applied for backward compatibility:
        /// System.Text.Json <c>[JsonPropertyName]</c> for AOT-optimized Lambda
        /// handlers, and Newtonsoft.Json <c>[JsonProperty]</c> for deserializing
        /// result data migrated from the monolith's PostgreSQL <c>jobs</c> table.
        ///
        /// JSON key: <c>"result"</c>.
        /// </summary>
        [JsonPropertyName("result")]
        [Newtonsoft.Json.JsonProperty(PropertyName = "result")]
        public Dictionary<string, object>? Result { get; set; }

        /// <summary>
        /// The workflow type metadata describing the executing workflow's identity,
        /// default priority, and implementation class name.
        /// Type changed from <c>JobType</c> to <c>WorkflowType</c>.
        /// Nullable to support partial deserialization from Step Functions input
        /// payloads where type metadata may be resolved separately.
        /// JSON key: <c>"type"</c>.
        /// </summary>
        [JsonPropertyName("type")]
        public WorkflowType? Type { get; set; }

        /// <summary>
        /// The AWS Step Functions execution ARN that owns this step context.
        /// Links the step to its parent state machine execution for distributed
        /// tracing and correlation-ID propagation (per AAP Section 0.8.5).
        /// This is a new property not present in the monolith's <c>JobContext</c>,
        /// added for Step Functions integration.
        /// JSON key: <c>"step_functions_execution_arn"</c>.
        /// </summary>
        [JsonPropertyName("step_functions_execution_arn")]
        public string? StepFunctionsExecutionArn { get; set; }

        /// <summary>
        /// The name of the current step within the Step Functions state machine
        /// (e.g., "ValidateInput", "ProcessPayment", "SendNotification").
        /// Used for structured logging, step-specific branching logic, and
        /// CloudWatch Logs correlation.
        /// This is a new property not present in the monolith's <c>JobContext</c>.
        /// JSON key: <c>"step_name"</c>.
        /// </summary>
        [JsonPropertyName("step_name")]
        public string? StepName { get; set; }
    }
}
