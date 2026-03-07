using Newtonsoft.Json;
using System;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Defines the scheduling frequency type for a schedule plan.
	/// Preserved from monolith WebVella.Erp.Jobs.SchedulePlanType with identical values.
	/// Used by ScheduleManager to determine how to compute next trigger times.
	/// </summary>
	[Serializable]
	public enum SchedulePlanType
	{
		[SelectOption(Label = "interval")]
		Interval = 1,
		[SelectOption(Label = "daily")]
		Daily = 2,
		[SelectOption(Label = "weekly")]
		Weekly = 3,
		[SelectOption(Label = "monthly")]
		Monthly = 4
	}

	/// <summary>
	/// Schedule plan model representing a recurring job execution schedule.
	/// Persisted in the 'schedule_plan' PostgreSQL table via JobDataService.
	/// Preserved from monolith WebVella.Erp.Jobs.SchedulePlan with identical fields
	/// and JSON property names for backward-compatible serialization.
	/// </summary>
	[Serializable]
	public class SchedulePlan
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "type")]
		public SchedulePlanType Type { get; set; }

		[JsonProperty(PropertyName = "start_date")]
		public DateTime? StartDate { get; set; }

		[JsonProperty(PropertyName = "end_date")]
		public DateTime? EndDate { get; set; }

		[JsonProperty(PropertyName = "schedule_days")]
		public SchedulePlanDaysOfWeek ScheduledDays { get; set; }

		[JsonProperty(PropertyName = "interval_in_minutes")]
		public int? IntervalInMinutes { get; set; }

		[JsonProperty(PropertyName = "start_timespan")]
		public int? StartTimespan { get; set; }

		[JsonProperty(PropertyName = "end_timespan")]
		public int? EndTimespan { get; set; }

		[JsonProperty(PropertyName = "last_trigger_time")]
		public DateTime? LastTriggerTime { get; set; }

		[JsonProperty(PropertyName = "next_trigger_time")]
		public DateTime? NextTriggerTime { get; set; }

		[JsonProperty(PropertyName = "job_type_id")]
		public Guid JobTypeId { get; set; }

		[JsonProperty(PropertyName = "job_type")]
		public JobType JobType { get; set; }

		[JsonProperty(PropertyName = "job_attributes")]
		public dynamic JobAttributes { get; set; }

		[JsonProperty(PropertyName = "enabled")]
		public bool Enabled { get; set; }

		[JsonProperty(PropertyName = "last_started_job_id")]
		public Guid? LastStartedJobId { get; set; }

		[JsonProperty(PropertyName = "created_on")]
		public DateTime CreatedOn { get; internal set; }

		[JsonProperty(PropertyName = "last_modified_by")]
		public Guid? LastModifiedBy { get; set; }

		[JsonProperty(PropertyName = "last_modified_on")]
		public DateTime LastModifiedOn { get; internal set; }
	}

	/// <summary>
	/// Represents the days of the week on which a schedule plan should execute.
	/// Used for Weekly schedule plans. Each boolean flag indicates whether
	/// the schedule should trigger on that day.
	/// Preserved from monolith WebVella.Erp.Jobs.SchedulePlanDaysOfWeek.
	/// </summary>
	[Serializable]
	public class SchedulePlanDaysOfWeek
	{
		[JsonProperty(PropertyName = "scheduled_on_sunday")]
		public bool ScheduledOnSunday { get; set; }

		[JsonProperty(PropertyName = "scheduled_on_monday")]
		public bool ScheduledOnMonday { get; set; }

		[JsonProperty(PropertyName = "scheduled_on_tuesday")]
		public bool ScheduledOnTuesday { get; set; }

		[JsonProperty(PropertyName = "scheduled_on_wednesday")]
		public bool ScheduledOnWednesday { get; set; }

		[JsonProperty(PropertyName = "scheduled_on_thursday")]
		public bool ScheduledOnThursday { get; set; }

		[JsonProperty(PropertyName = "scheduled_on_friday")]
		public bool ScheduledOnFriday { get; set; }

		[JsonProperty(PropertyName = "scheduled_on_saturday")]
		public bool ScheduledOnSaturday { get; set; }

		/// <summary>
		/// Checks if at least one day is selected for the schedule.
		/// Required validation before enabling a weekly schedule plan.
		/// </summary>
		/// <returns>True if at least one day is selected; otherwise false.</returns>
		public bool HasOneSelectedDay()
		{
			return ScheduledOnSunday || ScheduledOnMonday || ScheduledOnTuesday || ScheduledOnWednesday ||
				ScheduledOnThursday || ScheduledOnFriday || ScheduledOnSaturday;
		}
	}

	/// <summary>
	/// Output-specific schedule plan model used for API response serialization.
	/// Differs from SchedulePlan in that StartTimespan/EndTimespan are DateTime?
	/// instead of int? for human-readable output formatting.
	/// Preserved from monolith WebVella.Erp.Jobs.OutputSchedulePlan.
	/// </summary>
	public class OutputSchedulePlan
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "type")]
		public SchedulePlanType Type { get; set; }

		[JsonProperty(PropertyName = "start_date")]
		public DateTime? StartDate { get; set; }

		[JsonProperty(PropertyName = "end_date")]
		public DateTime? EndDate { get; set; }

		[JsonProperty(PropertyName = "schedule_days")]
		public SchedulePlanDaysOfWeek ScheduledDays { get; set; }

		[JsonProperty(PropertyName = "interval_in_minutes")]
		public int? IntervalInMinutes { get; set; }

		[JsonProperty(PropertyName = "start_timespan")]
		public DateTime? StartTimespan { get; set; }

		[JsonProperty(PropertyName = "end_timespan")]
		public DateTime? EndTimespan { get; set; }

		[JsonProperty(PropertyName = "last_trigger_time")]
		public DateTime? LastTriggerTime { get; set; }

		[JsonProperty(PropertyName = "next_trigger_time")]
		public DateTime? NextTriggerTime { get; set; }

		[JsonProperty(PropertyName = "job_type_id")]
		public Guid JobTypeId { get; set; }

		[JsonProperty(PropertyName = "job_type")]
		public JobType JobType { get; set; }

		[JsonProperty(PropertyName = "job_attributes")]
		public dynamic JobAttributes { get; set; }

		[JsonProperty(PropertyName = "enabled")]
		public bool Enabled { get; set; }

		[JsonProperty(PropertyName = "last_started_job_id")]
		public Guid? LastStartedJobId { get; set; }

		[JsonProperty(PropertyName = "created_on")]
		public DateTime CreatedOn { get; internal set; }

		[JsonProperty(PropertyName = "last_modified_by")]
		public Guid? LastModifiedBy { get; set; }

		[JsonProperty(PropertyName = "last_modified_on")]
		public DateTime LastModifiedOn { get; internal set; }
	}
}
