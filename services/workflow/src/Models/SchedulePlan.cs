using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace WebVellaErp.Workflow.Models
{
    /// <summary>
    /// Defines the scheduling strategy for a <see cref="SchedulePlan"/>.
    /// Preserved from the monolith's <c>SchedulePlanType</c> enum in
    /// <c>WebVella.Erp/Jobs/Models/SchedulePlan.cs</c> (lines 12-22).
    ///
    /// Key differences from source:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Removed <c>[Serializable]</c> attribute — not needed for Lambda/DynamoDB
    ///       serialization.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Removed <c>[SelectOption(Label = "...")]</c> attributes from enum values —
    ///       that attribute was from <c>WebVella.Erp.Api.Models.SelectOption</c> which
    ///       does not exist in this bounded context.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Added <see cref="JsonStringEnumConverter"/> for AOT-friendly string
    ///       serialization (e.g., <c>"Daily"</c> instead of <c>2</c>).
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// Integer values are preserved exactly from the source for deterministic
    /// DynamoDB persistence and backward-compatible data migration from the
    /// monolith's PostgreSQL <c>schedule_plans.type</c> column.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter<SchedulePlanType>))]
    public enum SchedulePlanType
    {
        /// <summary>
        /// Schedule triggers at a fixed interval (in minutes) between
        /// <see cref="SchedulePlan.StartTimespan"/> and
        /// <see cref="SchedulePlan.EndTimespan"/>.
        /// </summary>
        Interval = 1,

        /// <summary>
        /// Schedule triggers once per day at <see cref="SchedulePlan.StartTimespan"/>.
        /// </summary>
        Daily = 2,

        /// <summary>
        /// Schedule triggers on selected days of the week as defined by
        /// <see cref="SchedulePlan.ScheduledDays"/>.
        /// </summary>
        Weekly = 3,

        /// <summary>
        /// Schedule triggers once per month on specified days.
        /// </summary>
        Monthly = 4
    }

    /// <summary>
    /// Represents a recurring schedule plan that triggers workflow executions
    /// via Step Functions state machines in the serverless architecture.
    ///
    /// Preserved from the monolith's <c>SchedulePlan</c> class in
    /// <c>WebVella.Erp/Jobs/Models/SchedulePlan.cs</c> (lines 25-83).
    ///
    /// Key differences from source:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Removed <c>[Serializable]</c> attribute — not needed for Lambda/DynamoDB.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Replaced Newtonsoft.Json <c>[JsonProperty]</c> attributes with
    ///       System.Text.Json <c>[JsonPropertyName]</c> for Native AOT compatibility.
    ///       Dual serialization retained only for <see cref="JobAttributes"/> which
    ///       was originally <c>dynamic</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Renamed <c>JobTypeId</c> property to <c>WorkflowTypeId</c> and
    ///       <c>JobType</c> property to <c>WorkflowType</c> to reflect the
    ///       serverless workflow domain. JSON keys preserved as <c>"job_type_id"</c>
    ///       and <c>"job_type"</c> for backward compatibility.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Renamed <c>LastStartedJobId</c> to <c>LastStartedWorkflowId</c>.
    ///       JSON key preserved as <c>"last_started_job_id"</c> for backward compatibility.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Changed <c>dynamic JobAttributes</c> to
    ///       <c>Dictionary&lt;string, object&gt;?</c> for Native AOT compatibility.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Changed <c>CreatedOn</c> and <c>LastModifiedOn</c> setters from
    ///       <c>internal set</c> to <c>set</c> for Lambda deserialization compatibility.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// All snake_case JSON property names are preserved exactly from the source
    /// for backward-compatible API responses and data migration.
    /// </summary>
    public class SchedulePlan
    {
        /// <summary>
        /// Unique identifier for this schedule plan.
        /// Maps to source <c>SchedulePlan.Id</c> (line 28).
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Human-readable name of the schedule plan.
        /// Maps to source <c>SchedulePlan.Name</c> (line 31).
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Scheduling strategy (Interval, Daily, Weekly, Monthly).
        /// Maps to source <c>SchedulePlan.Type</c> (line 34).
        /// </summary>
        [JsonPropertyName("type")]
        public SchedulePlanType Type { get; set; }

        /// <summary>
        /// Optional start date from which the schedule becomes active.
        /// Maps to source <c>SchedulePlan.StartDate</c> (line 37).
        /// </summary>
        [JsonPropertyName("start_date")]
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// Optional end date after which the schedule is deactivated.
        /// Maps to source <c>SchedulePlan.EndDate</c> (line 40).
        /// </summary>
        [JsonPropertyName("end_date")]
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// Days of the week on which this schedule triggers (for Weekly type).
        /// Maps to source <c>SchedulePlan.ScheduledDays</c> (line 43).
        /// </summary>
        [JsonPropertyName("schedule_days")]
        public SchedulePlanDaysOfWeek ScheduledDays { get; set; } = new();

        /// <summary>
        /// Interval between triggers in minutes (for Interval type).
        /// Maps to source <c>SchedulePlan.IntervalInMinutes</c> (line 46).
        /// </summary>
        [JsonPropertyName("interval_in_minutes")]
        public int? IntervalInMinutes { get; set; }

        /// <summary>
        /// Start of the daily execution window as minutes-since-midnight.
        /// Maps to source <c>SchedulePlan.StartTimespan</c> (line 49).
        /// </summary>
        [JsonPropertyName("start_timespan")]
        public int? StartTimespan { get; set; }

        /// <summary>
        /// End of the daily execution window as minutes-since-midnight.
        /// Maps to source <c>SchedulePlan.EndTimespan</c> (line 52).
        /// </summary>
        [JsonPropertyName("end_timespan")]
        public int? EndTimespan { get; set; }

        /// <summary>
        /// Timestamp of the last time this schedule triggered a workflow execution.
        /// Maps to source <c>SchedulePlan.LastTriggerTime</c> (line 55).
        /// </summary>
        [JsonPropertyName("last_trigger_time")]
        public DateTime? LastTriggerTime { get; set; }

        /// <summary>
        /// Calculated timestamp for the next expected trigger.
        /// Maps to source <c>SchedulePlan.NextTriggerTime</c> (line 58).
        /// </summary>
        [JsonPropertyName("next_trigger_time")]
        public DateTime? NextTriggerTime { get; set; }

        /// <summary>
        /// Identifier of the associated <see cref="WorkflowType"/>.
        /// Renamed from <c>JobTypeId</c>; JSON key preserved as <c>"job_type_id"</c>
        /// for backward compatibility with monolith data.
        /// Maps to source <c>SchedulePlan.JobTypeId</c> (line 61).
        /// </summary>
        [JsonPropertyName("job_type_id")]
        public Guid WorkflowTypeId { get; set; }

        /// <summary>
        /// Navigation property for the associated workflow type metadata.
        /// Type changed from monolith's <c>JobType</c> to <see cref="Models.WorkflowType"/>.
        /// JSON key preserved as <c>"job_type"</c> for backward compatibility.
        /// Maps to source <c>SchedulePlan.JobType</c> (line 64).
        /// </summary>
        [JsonPropertyName("job_type")]
        public WorkflowType? WorkflowType { get; set; }

        /// <summary>
        /// Arbitrary key-value attributes passed to the workflow execution context.
        /// Changed from <c>dynamic</c> to <c>Dictionary&lt;string, object&gt;?</c>
        /// for Native AOT compatibility. Dual serialization attributes are applied
        /// to support both System.Text.Json (Lambda) and Newtonsoft.Json (data migration).
        /// Maps to source <c>SchedulePlan.JobAttributes</c> (line 67).
        /// </summary>
        [JsonPropertyName("job_attributes")]
        [JsonProperty(PropertyName = "job_attributes")]
        public Dictionary<string, object>? JobAttributes { get; set; }

        /// <summary>
        /// Whether this schedule plan is active and eligible for triggering.
        /// Maps to source <c>SchedulePlan.Enabled</c> (line 70).
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Identifier of the last workflow instance started by this schedule.
        /// Renamed from <c>LastStartedJobId</c>; JSON key preserved as
        /// <c>"last_started_job_id"</c> for backward compatibility.
        /// Maps to source <c>SchedulePlan.LastStartedJobId</c> (line 73).
        /// </summary>
        [JsonPropertyName("last_started_job_id")]
        public Guid? LastStartedWorkflowId { get; set; }

        /// <summary>
        /// Timestamp when this schedule plan was created.
        /// Setter changed from <c>internal set</c> to <c>set</c> for Lambda
        /// deserialization compatibility.
        /// Maps to source <c>SchedulePlan.CreatedOn</c> (line 76).
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Identifier of the user who last modified this schedule plan.
        /// Maps to source <c>SchedulePlan.LastModifiedBy</c> (line 79).
        /// </summary>
        [JsonPropertyName("last_modified_by")]
        public Guid? LastModifiedBy { get; set; }

        /// <summary>
        /// Timestamp of the last modification to this schedule plan.
        /// Setter changed from <c>internal set</c> to <c>set</c> for Lambda
        /// deserialization compatibility.
        /// Maps to source <c>SchedulePlan.LastModifiedOn</c> (line 82).
        /// </summary>
        [JsonPropertyName("last_modified_on")]
        public DateTime LastModifiedOn { get; set; }
    }

    /// <summary>
    /// Represents the days of the week on which a <see cref="SchedulePlan"/>
    /// is configured to trigger workflow executions (for Weekly schedule type).
    ///
    /// Preserved from the monolith's <c>SchedulePlanDaysOfWeek</c> class in
    /// <c>WebVella.Erp/Jobs/Models/SchedulePlan.cs</c> (lines 86-115).
    ///
    /// Key differences from source:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Removed <c>[Serializable]</c> attribute — not needed for Lambda/DynamoDB.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Replaced Newtonsoft.Json <c>[JsonProperty]</c> attributes with
    ///       System.Text.Json <c>[JsonPropertyName]</c> for Native AOT compatibility.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// All snake_case JSON property names match the source exactly.
    /// </summary>
    public class SchedulePlanDaysOfWeek
    {
        /// <summary>
        /// Whether the schedule triggers on Sunday.
        /// Maps to source line 89.
        /// </summary>
        [JsonPropertyName("scheduled_on_sunday")]
        public bool ScheduledOnSunday { get; set; }

        /// <summary>
        /// Whether the schedule triggers on Monday.
        /// Maps to source line 91.
        /// </summary>
        [JsonPropertyName("scheduled_on_monday")]
        public bool ScheduledOnMonday { get; set; }

        /// <summary>
        /// Whether the schedule triggers on Tuesday.
        /// Maps to source line 93.
        /// </summary>
        [JsonPropertyName("scheduled_on_tuesday")]
        public bool ScheduledOnTuesday { get; set; }

        /// <summary>
        /// Whether the schedule triggers on Wednesday.
        /// Maps to source line 95.
        /// </summary>
        [JsonPropertyName("scheduled_on_wednesday")]
        public bool ScheduledOnWednesday { get; set; }

        /// <summary>
        /// Whether the schedule triggers on Thursday.
        /// Maps to source line 97.
        /// </summary>
        [JsonPropertyName("scheduled_on_thursday")]
        public bool ScheduledOnThursday { get; set; }

        /// <summary>
        /// Whether the schedule triggers on Friday.
        /// Maps to source line 99.
        /// </summary>
        [JsonPropertyName("scheduled_on_friday")]
        public bool ScheduledOnFriday { get; set; }

        /// <summary>
        /// Whether the schedule triggers on Saturday.
        /// Maps to source line 101.
        /// </summary>
        [JsonPropertyName("scheduled_on_saturday")]
        public bool ScheduledOnSaturday { get; set; }

        /// <summary>
        /// Validates that at least one day of the week is selected for scheduling.
        /// Preserved exactly from source <c>SchedulePlanDaysOfWeek.HasOneSelectedDay()</c>
        /// (lines 110-114).
        /// </summary>
        /// <returns>
        /// <c>true</c> if any day-of-week flag is set; <c>false</c> if none are selected.
        /// </returns>
        public bool HasOneSelectedDay()
        {
            return ScheduledOnSunday || ScheduledOnMonday || ScheduledOnTuesday || ScheduledOnWednesday ||
                ScheduledOnThursday || ScheduledOnFriday || ScheduledOnSaturday;
        }
    }

    /// <summary>
    /// Output-facing DTO for schedule plan data returned in API responses.
    /// Differs from <see cref="SchedulePlan"/> in that <see cref="StartTimespan"/>
    /// and <see cref="EndTimespan"/> are <c>DateTime?</c> (time-of-day representation)
    /// instead of <c>int?</c> (minutes-since-midnight).
    ///
    /// Preserved from the monolith's <c>OutputSchedulePlan</c> class in
    /// <c>WebVella.Erp/Jobs/Models/SchedulePlan.cs</c> (lines 117-175).
    ///
    /// Key differences from source:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       Replaced Newtonsoft.Json <c>[JsonProperty]</c> attributes with
    ///       System.Text.Json <c>[JsonPropertyName]</c> for Native AOT compatibility.
    ///       Dual serialization retained only for <see cref="JobAttributes"/>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Renamed <c>JobTypeId</c> to <c>WorkflowTypeId</c>, <c>JobType</c> to
    ///       <c>WorkflowType</c>, and <c>LastStartedJobId</c> to
    ///       <c>LastStartedWorkflowId</c>. JSON keys preserved for backward compatibility.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Changed <c>dynamic JobAttributes</c> to
    ///       <c>Dictionary&lt;string, object&gt;?</c> for Native AOT compatibility.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Changed <c>CreatedOn</c> and <c>LastModifiedOn</c> setters from
    ///       <c>internal set</c> to <c>set</c> for Lambda deserialization compatibility.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// All snake_case JSON property names are preserved exactly from the source
    /// for backward-compatible API responses and data migration.
    /// </summary>
    public class OutputSchedulePlan
    {
        /// <summary>
        /// Unique identifier for this schedule plan.
        /// Maps to source <c>OutputSchedulePlan.Id</c> (line 120).
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Human-readable name of the schedule plan.
        /// Maps to source <c>OutputSchedulePlan.Name</c> (line 123).
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Scheduling strategy (Interval, Daily, Weekly, Monthly).
        /// Maps to source <c>OutputSchedulePlan.Type</c> (line 126).
        /// </summary>
        [JsonPropertyName("type")]
        public SchedulePlanType Type { get; set; }

        /// <summary>
        /// Optional start date from which the schedule becomes active.
        /// Maps to source <c>OutputSchedulePlan.StartDate</c> (line 129).
        /// </summary>
        [JsonPropertyName("start_date")]
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// Optional end date after which the schedule is deactivated.
        /// Maps to source <c>OutputSchedulePlan.EndDate</c> (line 132).
        /// </summary>
        [JsonPropertyName("end_date")]
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// Days of the week on which this schedule triggers.
        /// Maps to source <c>OutputSchedulePlan.ScheduledDays</c> (line 135).
        /// </summary>
        [JsonPropertyName("schedule_days")]
        public SchedulePlanDaysOfWeek ScheduledDays { get; set; } = new();

        /// <summary>
        /// Interval between triggers in minutes (for Interval type).
        /// Maps to source <c>OutputSchedulePlan.IntervalInMinutes</c> (line 138).
        /// </summary>
        [JsonPropertyName("interval_in_minutes")]
        public int? IntervalInMinutes { get; set; }

        /// <summary>
        /// Start of the daily execution window as a <c>DateTime?</c> time-of-day
        /// representation. This is the KEY DIFFERENCE from <see cref="SchedulePlan"/>
        /// where it is <c>int?</c> (minutes-since-midnight).
        /// Maps to source <c>OutputSchedulePlan.StartTimespan</c> (line 141).
        /// </summary>
        [JsonPropertyName("start_timespan")]
        public DateTime? StartTimespan { get; set; }

        /// <summary>
        /// End of the daily execution window as a <c>DateTime?</c> time-of-day
        /// representation. This is the KEY DIFFERENCE from <see cref="SchedulePlan"/>
        /// where it is <c>int?</c> (minutes-since-midnight).
        /// Maps to source <c>OutputSchedulePlan.EndTimespan</c> (line 144).
        /// </summary>
        [JsonPropertyName("end_timespan")]
        public DateTime? EndTimespan { get; set; }

        /// <summary>
        /// Timestamp of the last time this schedule triggered a workflow execution.
        /// Maps to source <c>OutputSchedulePlan.LastTriggerTime</c> (line 147).
        /// </summary>
        [JsonPropertyName("last_trigger_time")]
        public DateTime? LastTriggerTime { get; set; }

        /// <summary>
        /// Calculated timestamp for the next expected trigger.
        /// Maps to source <c>OutputSchedulePlan.NextTriggerTime</c> (line 150).
        /// </summary>
        [JsonPropertyName("next_trigger_time")]
        public DateTime? NextTriggerTime { get; set; }

        /// <summary>
        /// Identifier of the associated <see cref="WorkflowType"/>.
        /// Renamed from <c>JobTypeId</c>; JSON key preserved as <c>"job_type_id"</c>
        /// for backward compatibility.
        /// Maps to source <c>OutputSchedulePlan.JobTypeId</c> (line 153).
        /// </summary>
        [JsonPropertyName("job_type_id")]
        public Guid WorkflowTypeId { get; set; }

        /// <summary>
        /// Navigation property for the associated workflow type metadata.
        /// Type changed from monolith's <c>JobType</c> to <see cref="Models.WorkflowType"/>.
        /// JSON key preserved as <c>"job_type"</c> for backward compatibility.
        /// Maps to source <c>OutputSchedulePlan.JobType</c> (line 156).
        /// </summary>
        [JsonPropertyName("job_type")]
        public WorkflowType? WorkflowType { get; set; }

        /// <summary>
        /// Arbitrary key-value attributes passed to the workflow execution context.
        /// Changed from <c>dynamic</c> to <c>Dictionary&lt;string, object&gt;?</c>
        /// for Native AOT compatibility. Dual serialization attributes preserve
        /// migration compatibility with monolith Newtonsoft.Json data.
        /// Maps to source <c>OutputSchedulePlan.JobAttributes</c> (line 159).
        /// </summary>
        [JsonPropertyName("job_attributes")]
        [JsonProperty(PropertyName = "job_attributes")]
        public Dictionary<string, object>? JobAttributes { get; set; }

        /// <summary>
        /// Whether this schedule plan is active and eligible for triggering.
        /// Maps to source <c>OutputSchedulePlan.Enabled</c> (line 162).
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Identifier of the last workflow instance started by this schedule.
        /// Renamed from <c>LastStartedJobId</c>; JSON key preserved as
        /// <c>"last_started_job_id"</c> for backward compatibility.
        /// Maps to source <c>OutputSchedulePlan.LastStartedJobId</c> (line 165).
        /// </summary>
        [JsonPropertyName("last_started_job_id")]
        public Guid? LastStartedWorkflowId { get; set; }

        /// <summary>
        /// Timestamp when this schedule plan was created.
        /// Setter changed from <c>internal set</c> to <c>set</c> for Lambda
        /// deserialization compatibility.
        /// Maps to source <c>OutputSchedulePlan.CreatedOn</c> (line 168).
        /// </summary>
        [JsonPropertyName("created_on")]
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Identifier of the user who last modified this schedule plan.
        /// Maps to source <c>OutputSchedulePlan.LastModifiedBy</c> (line 171).
        /// </summary>
        [JsonPropertyName("last_modified_by")]
        public Guid? LastModifiedBy { get; set; }

        /// <summary>
        /// Timestamp of the last modification to this schedule plan.
        /// Setter changed from <c>internal set</c> to <c>set</c> for Lambda
        /// deserialization compatibility.
        /// Maps to source <c>OutputSchedulePlan.LastModifiedOn</c> (line 174).
        /// </summary>
        [JsonPropertyName("last_modified_on")]
        public DateTime LastModifiedOn { get; set; }
    }
}
