using Newtonsoft.Json;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Service.Core.Jobs
{
	/// <summary>
	/// Internal data access service for job and schedule plan persistence.
	/// Adapted from monolith WebVella.Erp.Jobs.JobDataService (526 lines).
	/// 
	/// Uses direct NpgsqlConnection (not CoreDbContext) for all SQL operations,
	/// as the monolith JobDataService was designed to operate independently of
	/// the ambient DbContext pattern. This is intentional — job data operations
	/// must succeed even when the ambient database context is in an error state
	/// (e.g., during job failure handling).
	/// 
	/// Key change from monolith:
	/// - using WebVella.Erp.Api.Models.AutoMapper → using WebVella.Erp.SharedKernel.Utilities
	///   (MapTo&lt;T&gt;() extension method is now in shared kernel)
	/// - Namespace changed from WebVella.Erp.Jobs → WebVella.Erp.Service.Core.Jobs
	/// 
	/// All SQL queries, parameter bindings, and business logic are preserved
	/// exactly as in the monolith to ensure zero behavior change.
	/// </summary>
	internal class JobDataService
	{
		private JobManagerSettings Settings { get; set; }

		/// <summary>
		/// Initializes JobDataService with connection settings.
		/// The connection string is resolved from IConfiguration["ConnectionStrings:Default"]
		/// by the caller (JobPool, JobManager, ScheduleManager) and passed via JobManagerSettings.
		/// </summary>
		/// <param name="settings">Settings containing the database connection string.</param>
		public JobDataService(JobManagerSettings settings)
		{
			Settings = settings;
		}

		#region << Jobs >>

		/// <summary>
		/// Creates a new job record in the 'jobs' PostgreSQL table.
		/// Dynamically builds INSERT SQL from non-null job properties.
		/// Serializes Attributes using TypeNameHandling.All for polymorphic deserialization.
		/// Returns the created job by re-reading it from the database, or null on failure.
		/// </summary>
		/// <param name="job">The job to persist. Must have Id, Type, TypeName, CompleteClassName set.</param>
		/// <returns>The persisted Job with server-set timestamps, or null if INSERT failed.</returns>
		public Job CreateJob(Job job)
		{
			JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", job.Id) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("type_id", job.Type.Id) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("type_name", job.TypeName) { NpgsqlDbType = NpgsqlDbType.Text });
			parameters.Add(new NpgsqlParameter("complete_class_name", job.CompleteClassName) { NpgsqlDbType = NpgsqlDbType.Text });
			if (job.Attributes != null)
				parameters.Add(new NpgsqlParameter("attributes", JsonConvert.SerializeObject(job.Attributes, settings).ToString()) { NpgsqlDbType = NpgsqlDbType.Text });
			parameters.Add(new NpgsqlParameter("status", (int)job.Status) { NpgsqlDbType = NpgsqlDbType.Integer });
			parameters.Add(new NpgsqlParameter("priority", (int)job.Priority) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (job.StartedOn.HasValue)
				parameters.Add(new NpgsqlParameter("started_on", job.StartedOn.HasValue) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (job.FinishedOn.HasValue)
				parameters.Add(new NpgsqlParameter("finished_on", job.FinishedOn.HasValue) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (job.AbortedBy.HasValue)
				parameters.Add(new NpgsqlParameter("aborted_by", job.AbortedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (job.CanceledBy.HasValue)
				parameters.Add(new NpgsqlParameter("canceled_by", job.CanceledBy) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (!string.IsNullOrEmpty(job.ErrorMessage))
				parameters.Add(new NpgsqlParameter("error_message", job.ErrorMessage) { NpgsqlDbType = NpgsqlDbType.Text });
			if (job.SchedulePlanId.HasValue)
				parameters.Add(new NpgsqlParameter("schedule_plan_id", job.SchedulePlanId) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("created_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			parameters.Add(new NpgsqlParameter("last_modified_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (job.CreatedBy.HasValue)
				parameters.Add(new NpgsqlParameter("created_by", job.CreatedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (job.LastModifiedBy.HasValue)
				parameters.Add(new NpgsqlParameter("last_modified_by", job.LastModifiedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string columns = "";
			string values = "";
			foreach (NpgsqlParameter param in parameters)
			{
				columns += $"{param.ParameterName}, ";
				values += $"@{param.ParameterName}, ";
			}

			columns = columns.Remove(columns.Length - 2, 2);
			values = values.Remove(values.Length - 2, 2);

			string sql = $"INSERT INTO jobs ({columns}) VALUES ({values})";

			if (ExecuteNonQuerySqlCommand(sql, parameters))
				return GetJob(job.Id);

			return null;
		}

		/// <summary>
		/// Updates an existing job record in the 'jobs' table.
		/// Dynamically builds SET clause from non-null job properties.
		/// Serializes Result via JobResultWrapper with TypeNameHandling.All for polymorphic support.
		/// Used by JobPool.Process() for state transitions (Running → Finished/Failed).
		/// </summary>
		/// <param name="job">The job with updated fields. Must have Id set for WHERE clause.</param>
		/// <returns>True if at least one row was affected; otherwise false.</returns>
		public bool UpdateJob(Job job)
		{
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", job.Id) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("status", (int)job.Status) { NpgsqlDbType = NpgsqlDbType.Integer });
			parameters.Add(new NpgsqlParameter("priority", (int)job.Priority) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (job.StartedOn.HasValue)
				parameters.Add(new NpgsqlParameter("started_on", job.StartedOn) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (job.FinishedOn.HasValue)
				parameters.Add(new NpgsqlParameter("finished_on", job.FinishedOn.HasValue ? job.FinishedOn : null) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (job.AbortedBy.HasValue)
				parameters.Add(new NpgsqlParameter("aborted_by", job.AbortedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (job.CanceledBy.HasValue)
				parameters.Add(new NpgsqlParameter("canceled_by", job.CanceledBy) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
				parameters.Add(new NpgsqlParameter("error_message", job.ErrorMessage) { NpgsqlDbType = NpgsqlDbType.Text });

			if (job.Result != null)
			{
				JobResultWrapper jrWrap = new JobResultWrapper { Result = job.Result };
				JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
				string result = JsonConvert.SerializeObject(jrWrap, settings);
				parameters.Add(new NpgsqlParameter("result", result) { NpgsqlDbType = NpgsqlDbType.Text });
			}

			parameters.Add(new NpgsqlParameter("last_modified_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (job.LastModifiedBy.HasValue)
				parameters.Add(new NpgsqlParameter("last_modified_by", job.LastModifiedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string setClause = "";
			foreach (NpgsqlParameter param in parameters)
			{
				if (param.ParameterName != "id")
					setClause += $"{param.ParameterName} = @{param.ParameterName}, ";
			}

			setClause = setClause.Remove(setClause.Length - 2, 2);

			string sql = $"UPDATE jobs SET {setClause} WHERE id = @id";

			return ExecuteNonQuerySqlCommand(sql, parameters);
		}

		/// <summary>
		/// Retrieves a single job by its ID from the 'jobs' table.
		/// Uses MapTo&lt;Job&gt;() from SharedKernel.Utilities for DataRow-to-Job mapping.
		/// </summary>
		/// <param name="jobId">The unique identifier of the job.</param>
		/// <returns>The Job if found; otherwise null.</returns>
		public Job GetJob(Guid jobId)
		{
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", jobId) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string sql = "SELECT * FROM jobs WHERE id = @id";

			DataTable resultTable = ExecuteQuerySqlCommand(sql, parameters);

			Job job = null;
			if (resultTable.Rows != null && resultTable.Rows.Count > 0)
				job = resultTable.Rows[0].MapTo<Job>();

			return job;
		}

		/// <summary>
		/// Checks whether a job has completed execution (has a FinishedOn timestamp).
		/// Returns true if the job is not found (considered finished/removed).
		/// </summary>
		/// <param name="id">The unique identifier of the job to check.</param>
		/// <returns>True if the job is finished or not found; otherwise false.</returns>
		public bool IsJobFinished(Guid id)
		{
			Job job = GetJob(id);

			if (job == null)
				return true;

			return job.FinishedOn.HasValue;
		}

		/// <summary>
		/// Retrieves all jobs with Pending status, ordered by priority DESC then created_on ASC.
		/// Used by JobManager to find jobs ready for dispatch to JobPool.
		/// </summary>
		/// <param name="limit">Optional maximum number of jobs to return.</param>
		/// <returns>List of pending jobs.</returns>
		public List<Job> GetPendingJobs(int? limit = null)
		{
			return GetJobs(JobStatus.Pending, limit);
		}

		/// <summary>
		/// Retrieves all jobs with Running status.
		/// Used by JobManager on startup to detect interrupted jobs and mark them Aborted.
		/// </summary>
		/// <param name="limit">Optional maximum number of jobs to return.</param>
		/// <returns>List of running jobs.</returns>
		public List<Job> GetRunningJobs(int? limit = null)
		{
			return GetJobs(JobStatus.Running, limit);
		}

		/// <summary>
		/// Retrieves jobs by status with optional limit.
		/// Orders by priority DESC (highest first), then created_on ASC (oldest first).
		/// </summary>
		/// <param name="status">The job status to filter by.</param>
		/// <param name="limit">Optional maximum number of jobs to return. Negative values are treated as 0.</param>
		/// <returns>List of matching jobs.</returns>
		public List<Job> GetJobs(JobStatus status, int? limit = null)
		{
			string sql = "SELECT * FROM jobs WHERE status = @status ORDER BY priority DESC, created_on ASC";
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("status", (int)status) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (limit.HasValue)
			{
				if (limit.Value < 0)
					limit = 0;
				parameters.Add(new NpgsqlParameter("limit", limit) { NpgsqlDbType = NpgsqlDbType.Integer });
				sql += " LIMIT @limit";
			}

			DataTable dtJobs = ExecuteQuerySqlCommand(sql, parameters);
			return dtJobs.Rows.MapTo<Job>();
		}

		/// <summary>
		/// Retrieves jobs with comprehensive filtering and pagination.
		/// Supports filtering by date ranges, type name (ILIKE), status, priority,
		/// and schedule plan. Results ordered by created_on DESC.
		/// Used by the Admin service for job history browsing.
		/// </summary>
		public List<Job> GetJobs(DateTime? startFromDate = null, DateTime? startToDate = null, DateTime? finishedFromDate = null,
			DateTime? finishedToDate = null, string typeName = null, int? status = null, int? priority = null, Guid? schedulePlanId = null, int? page = null, int? pageSize = null)
		{
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();

			string sql = "SELECT * FROM jobs WHERE id IS NOT NULL";

			if (startFromDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("started_from", startFromDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND started_on >= @started_from";
			}
			if (startToDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("started_to", startToDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND started_on <= @started_to";
			}
			if (finishedFromDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("finished_from", finishedFromDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND finished_on <= @finished_from";
			}
			if (finishedToDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("finished_to", finishedFromDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND finished_on <= @finished_to";
			}
			if (!string.IsNullOrWhiteSpace(typeName))
			{
				var typeParameter = "%" + typeName + "%";
				parameters.Add(new NpgsqlParameter("type_name", typeParameter) { NpgsqlDbType = NpgsqlDbType.Text });
				sql += " AND type_name ILIKE @type_name";
			}
			if (status.HasValue)
			{
				parameters.Add(new NpgsqlParameter("status", status.Value) { NpgsqlDbType = NpgsqlDbType.Integer });
				sql += " AND status = @status";
			}
			if (priority.HasValue)
			{
				parameters.Add(new NpgsqlParameter("priority", priority.Value) { NpgsqlDbType = NpgsqlDbType.Integer });
				sql += " AND priority = @priority";
			}
			if (schedulePlanId.HasValue)
			{
				parameters.Add(new NpgsqlParameter("schedule_plan_id", schedulePlanId.Value) { NpgsqlDbType = NpgsqlDbType.Uuid });
				sql += " AND schedule_plan_id = @schedule_plan_id";
			}

			sql += " ORDER BY created_on DESC";

			if (pageSize.HasValue)
			{
				page = page ?? 1;
				int limit = pageSize.Value;
				int skip = (page.Value - 1) * limit;

				parameters.Add(new NpgsqlParameter("limit", limit) { NpgsqlDbType = NpgsqlDbType.Integer });
				parameters.Add(new NpgsqlParameter("offset", skip) { NpgsqlDbType = NpgsqlDbType.Integer });
				sql += " LIMIT @limit OFFSET @offset";
			}

			DataTable dtJobs = ExecuteQuerySqlCommand(sql, parameters);
			return dtJobs.Rows.MapTo<Job>();
		}

		/// <summary>
		/// Returns the total count of jobs matching the specified filters.
		/// Used for pagination support in the Admin service job history UI.
		/// </summary>
		internal long GetJobsTotalCount(DateTime? startFromDate = null, DateTime? startToDate = null, DateTime? finishedFromDate = null,
			DateTime? finishedToDate = null, string typeName = null, int? status = null, int? priority = null, Guid? schedulePlanId = null)
		{
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();

			string sql = "SELECT COUNT(id) FROM jobs WHERE id IS NOT NULL";

			if (startFromDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("started_from", startFromDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND started_on >= @started_from";
			}
			if (startToDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("started_to", startToDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND started_on <= @started_to";
			}
			if (finishedFromDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("finished_from", finishedFromDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND finished_on <= @finished_from";
			}
			if (finishedToDate.HasValue)
			{
				parameters.Add(new NpgsqlParameter("finished_to", finishedFromDate.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
				sql += " AND finished_on <= @finished_to";
			}
			if (!string.IsNullOrWhiteSpace(typeName))
			{
				var typeParameter = "%" + typeName + "%";
				parameters.Add(new NpgsqlParameter("type_name", typeParameter) { NpgsqlDbType = NpgsqlDbType.Text });
				sql += " AND type_name ILIKE @type_name";
			}
			if (status.HasValue)
			{
				parameters.Add(new NpgsqlParameter("status", status.Value) { NpgsqlDbType = NpgsqlDbType.Integer });
				sql += " AND status = @status";
			}
			if (priority.HasValue)
			{
				parameters.Add(new NpgsqlParameter("priority", priority.Value) { NpgsqlDbType = NpgsqlDbType.Integer });
				sql += " AND priority = @priority";
			}
			if (schedulePlanId.HasValue)
			{
				parameters.Add(new NpgsqlParameter("schedule_plan_id", schedulePlanId.Value) { NpgsqlDbType = NpgsqlDbType.Uuid });
				sql += " AND schedule_plan_id = @schedule_plan_id";
			}

			DataTable dtResult = ExecuteQuerySqlCommand(sql, parameters);
			return (long)dtResult.Rows[0][0];
		}

		#endregion

		#region << Schedule >>

		/// <summary>
		/// Creates a new schedule plan record in the 'schedule_plan' table.
		/// Serializes ScheduledDays as JSON and JobAttributes with TypeNameHandling.All.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan to persist.</param>
		/// <returns>True if the INSERT affected at least one row; otherwise false.</returns>
		public bool CreateSchedule(SchedulePlan schedulePlan)
		{
			JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", schedulePlan.Id) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("name", schedulePlan.Name) { NpgsqlDbType = NpgsqlDbType.Text });
			parameters.Add(new NpgsqlParameter("type", (int)schedulePlan.Type) { NpgsqlDbType = NpgsqlDbType.Integer });
			parameters.Add(new NpgsqlParameter("job_type_id", schedulePlan.JobTypeId) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (schedulePlan.StartDate.HasValue)
				parameters.Add(new NpgsqlParameter("start_date", schedulePlan.StartDate) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (schedulePlan.EndDate.HasValue)
				parameters.Add(new NpgsqlParameter("end_date", schedulePlan.EndDate) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			parameters.Add(new NpgsqlParameter("schedule_days", JsonConvert.SerializeObject(schedulePlan.ScheduledDays, settings).ToString()) { NpgsqlDbType = NpgsqlDbType.Json });
			if (schedulePlan.IntervalInMinutes.HasValue)
				parameters.Add(new NpgsqlParameter("interval_in_minutes", schedulePlan.IntervalInMinutes) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.StartTimespan.HasValue)
				parameters.Add(new NpgsqlParameter("start_timespan", schedulePlan.StartTimespan) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.EndTimespan.HasValue)
				parameters.Add(new NpgsqlParameter("end_timespan", schedulePlan.EndTimespan) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.LastTriggerTime.HasValue)
				parameters.Add(new NpgsqlParameter("last_trigger_time", schedulePlan.LastTriggerTime) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (schedulePlan.NextTriggerTime.HasValue)
				parameters.Add(new NpgsqlParameter("next_trigger_time", schedulePlan.NextTriggerTime) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			parameters.Add(new NpgsqlParameter("job_attributes", JsonConvert.SerializeObject(schedulePlan.JobAttributes, settings).ToString()) { NpgsqlDbType = NpgsqlDbType.Text });
			parameters.Add(new NpgsqlParameter("enabled", schedulePlan.Enabled) { NpgsqlDbType = NpgsqlDbType.Boolean });
			if (schedulePlan.LastStartedJobId.HasValue)
				parameters.Add(new NpgsqlParameter("last_started_job_id", schedulePlan.LastStartedJobId) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("created_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			parameters.Add(new NpgsqlParameter("last_modified_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (schedulePlan.LastModifiedBy.HasValue)
				parameters.Add(new NpgsqlParameter("last_modified_by", schedulePlan.LastModifiedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string columns = "";
			string values = "";
			foreach (NpgsqlParameter param in parameters)
			{
				columns += $"{param.ParameterName}, ";
				values += $"@{param.ParameterName}, ";
			}

			columns = columns.Remove(columns.Length - 2, 2);
			values = values.Remove(values.Length - 2, 2);

			string sql = $"INSERT INTO schedule_plan ({columns}) VALUES ({values})";

			return ExecuteNonQuerySqlCommand(sql, parameters);
		}

		/// <summary>
		/// Updates an existing schedule plan record with all mutable fields.
		/// </summary>
		/// <param name="schedulePlan">The schedule plan with updated fields.</param>
		/// <returns>True if at least one row was affected; otherwise false.</returns>
		public bool UpdateSchedule(SchedulePlan schedulePlan)
		{
			JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };

			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", schedulePlan.Id) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("name", schedulePlan.Name) { NpgsqlDbType = NpgsqlDbType.Text });
			parameters.Add(new NpgsqlParameter("type", (int)schedulePlan.Type) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.StartDate.HasValue)
				parameters.Add(new NpgsqlParameter("start_date", schedulePlan.StartDate) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (schedulePlan.EndDate.HasValue)
				parameters.Add(new NpgsqlParameter("end_date", schedulePlan.EndDate) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (schedulePlan.ScheduledDays != null)
				parameters.Add(new NpgsqlParameter("schedule_days", JsonConvert.SerializeObject(schedulePlan.ScheduledDays, settings).ToString()) { NpgsqlDbType = NpgsqlDbType.Json });
			if (schedulePlan.IntervalInMinutes.HasValue)
				parameters.Add(new NpgsqlParameter("interval_in_minutes", schedulePlan.IntervalInMinutes) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.StartTimespan.HasValue)
				parameters.Add(new NpgsqlParameter("start_timespan", schedulePlan.StartTimespan) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.EndTimespan.HasValue)
				parameters.Add(new NpgsqlParameter("end_timespan", schedulePlan.EndTimespan) { NpgsqlDbType = NpgsqlDbType.Integer });
			if (schedulePlan.LastTriggerTime.HasValue)
				parameters.Add(new NpgsqlParameter("last_trigger_time", schedulePlan.LastTriggerTime) { NpgsqlDbType = NpgsqlDbType.Timestamp });

			if (schedulePlan.NextTriggerTime.HasValue)
				parameters.Add(new NpgsqlParameter("next_trigger_time", schedulePlan.NextTriggerTime) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			else
				parameters.Add(new NpgsqlParameter("next_trigger_time", DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });

			parameters.Add(new NpgsqlParameter("enabled", schedulePlan.Enabled) { NpgsqlDbType = NpgsqlDbType.Boolean });
			if (schedulePlan.LastStartedJobId.HasValue)
				parameters.Add(new NpgsqlParameter("last_started_job_id", schedulePlan.LastStartedJobId) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("last_modified_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (schedulePlan.LastModifiedBy.HasValue)
				parameters.Add(new NpgsqlParameter("last_modified_by", schedulePlan.LastModifiedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string setClause = "";
			foreach (NpgsqlParameter param in parameters)
			{
				if (param.ParameterName != "id")
					setClause += $"{param.ParameterName} = @{param.ParameterName}, ";
			}

			setClause = setClause.Remove(setClause.Length - 2, 2);

			string sql = $"UPDATE schedule_plan SET {setClause} WHERE id = @id";

			return ExecuteNonQuerySqlCommand(sql, parameters);
		}

		/// <summary>
		/// Lightweight schedule plan update for trigger time progression.
		/// Used by ScheduleManager after a scheduled job is dispatched to update
		/// last/next trigger times and the last started job reference.
		/// </summary>
		/// <param name="schedulePlanId">The schedule plan ID to update.</param>
		/// <param name="lastTriggerTime">When the schedule last triggered.</param>
		/// <param name="nextTriggerTime">When the schedule should next trigger. Null clears the value.</param>
		/// <param name="modifiedBy">The user who triggered the update (may be null for system).</param>
		/// <param name="lastStartedJobId">The ID of the job most recently started by this plan.</param>
		/// <returns>True if at least one row was affected; otherwise false.</returns>
		public bool UpdateSchedule(Guid schedulePlanId, DateTime? lastTriggerTime, DateTime? nextTriggerTime,
			Guid? modifiedBy, Guid? lastStartedJobId)
		{
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", schedulePlanId) { NpgsqlDbType = NpgsqlDbType.Uuid });
			if (lastTriggerTime.HasValue)
				parameters.Add(new NpgsqlParameter("last_trigger_time", lastTriggerTime) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (nextTriggerTime.HasValue)
				parameters.Add(new NpgsqlParameter("next_trigger_time", nextTriggerTime.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			else
				parameters.Add(new NpgsqlParameter("next_trigger_time", DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Timestamp });

			if (lastStartedJobId.HasValue)
				parameters.Add(new NpgsqlParameter("last_started_job_id", lastStartedJobId) { NpgsqlDbType = NpgsqlDbType.Uuid });
			parameters.Add(new NpgsqlParameter("last_modified_on", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });
			if (modifiedBy.HasValue)
				parameters.Add(new NpgsqlParameter("last_modified_by", modifiedBy) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string setClause = "";
			foreach (NpgsqlParameter param in parameters)
			{
				if (param.ParameterName != "id")
					setClause += $"{param.ParameterName} = @{param.ParameterName}, ";
			}

			setClause = setClause.Remove(setClause.Length - 2, 2);

			string sql = $"UPDATE schedule_plan SET {setClause} WHERE id = @id";

			return ExecuteNonQuerySqlCommand(sql, parameters);
		}

		/// <summary>
		/// Retrieves a single schedule plan by its ID.
		/// Uses MapTo&lt;SchedulePlan&gt;() for DataRow mapping.
		/// </summary>
		/// <param name="id">The unique identifier of the schedule plan.</param>
		/// <returns>The SchedulePlan if found; otherwise null.</returns>
		public SchedulePlan GetSchedulePlan(Guid id)
		{
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("id", id) { NpgsqlDbType = NpgsqlDbType.Uuid });

			string sql = "SELECT * FROM schedule_plan WHERE id = @id";

			DataTable dtSchedulePlans = ExecuteQuerySqlCommand(sql, parameters);

			SchedulePlan schedulePlan = null;
			if (dtSchedulePlans.Rows != null && dtSchedulePlans.Rows.Count > 0)
				schedulePlan = dtSchedulePlans.Rows[0].MapTo<SchedulePlan>();

			return schedulePlan;
		}

		/// <summary>
		/// Retrieves all schedule plans ordered by name.
		/// Used by the Admin service for schedule plan management UI.
		/// </summary>
		/// <returns>List of all schedule plans.</returns>
		public List<SchedulePlan> GetSchedulePlans()
		{
			string sql = "SELECT * FROM schedule_plan ORDER BY name";

			DataTable dtSchedulePlans = ExecuteQuerySqlCommand(sql);

			return dtSchedulePlans.Rows.MapTo<SchedulePlan>();
		}

		/// <summary>
		/// Retrieves schedule plans that are ready for execution.
		/// A plan is ready when: enabled=true, next_trigger_time &lt;= now,
		/// start_date &lt;= now, and end_date (if set) &gt;= now.
		/// Results ordered by next_trigger_time ASC (most overdue first).
		/// Used by ScheduleManager's 1-second polling loop.
		/// </summary>
		/// <returns>List of schedule plans ready for job creation.</returns>
		public List<SchedulePlan> GetReadyForExecutionScheduledPlans()
		{
			string sql = "SELECT * FROM schedule_plan" +
				" WHERE enabled = true AND next_trigger_time <= @utc_now AND start_date <= @utc_now" +
				" AND COALESCE(end_date, @utc_now) >= @utc_now" +
				" ORDER BY next_trigger_time ASC";
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("utc_now", DateTime.UtcNow) { NpgsqlDbType = NpgsqlDbType.Timestamp });

			DataTable dtSchedulePlans = ExecuteQuerySqlCommand(sql, parameters);

			return dtSchedulePlans.Rows.MapTo<SchedulePlan>();
		}

		/// <summary>
		/// Retrieves schedule plans filtered by type (Interval, Daily, Weekly, Monthly).
		/// </summary>
		/// <param name="type">The schedule plan type to filter by.</param>
		/// <returns>List of matching schedule plans ordered by name.</returns>
		public List<SchedulePlan> GetScheduledPlansByType(SchedulePlanType type)
		{
			string sql = "SELECT * FROM schedule_plan WHERE type = @type ORDER BY name";
			List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
			parameters.Add(new NpgsqlParameter("type", (int)type) { NpgsqlDbType = NpgsqlDbType.Integer });

			DataTable dtSchedulePlans = ExecuteQuerySqlCommand(sql, parameters);

			return dtSchedulePlans.Rows.MapTo<SchedulePlan>();
		}

		#endregion

		#region << Helper methods >>

		/// <summary>
		/// Executes a non-query SQL command (INSERT, UPDATE, DELETE) against the job database.
		/// Opens its own NpgsqlConnection using the configured connection string.
		/// This isolation from the ambient DbContext is intentional — job data operations
		/// must succeed independently of any ongoing transactions.
		/// </summary>
		/// <param name="sql">The parameterized SQL command text.</param>
		/// <param name="parameters">Optional list of NpgsqlParameter instances.</param>
		/// <returns>True if at least one row was affected; otherwise false.</returns>
		private bool ExecuteNonQuerySqlCommand(string sql, List<NpgsqlParameter> parameters = null)
		{
			using (NpgsqlConnection con = new NpgsqlConnection(Settings.DbConnectionString))
			{
				try
				{
					con.Open();
					NpgsqlCommand command = new NpgsqlCommand(sql, con);
					command.CommandType = CommandType.Text;
					if (parameters != null && parameters.Count > 0)
						command.Parameters.AddRange(parameters.ToArray());
					return command.ExecuteNonQuery() > 0;
				}
				finally
				{
					con.Close();
				}
			}
		}

		/// <summary>
		/// Executes a query SQL command and returns the results as a DataTable.
		/// Uses NpgsqlDataAdapter for filling the DataTable, which supports
		/// the MapTo&lt;T&gt;() extension method for row-level object mapping.
		/// </summary>
		/// <param name="sql">The parameterized SQL query text.</param>
		/// <param name="parameters">Optional list of NpgsqlParameter instances.</param>
		/// <returns>DataTable containing the query results.</returns>
		private DataTable ExecuteQuerySqlCommand(string sql, List<NpgsqlParameter> parameters = null)
		{
			using (NpgsqlConnection con = new NpgsqlConnection(Settings.DbConnectionString))
			{
				try
				{
					con.Open();
					NpgsqlCommand command = new NpgsqlCommand(sql, con);
					command.CommandType = CommandType.Text;
					if (parameters != null && parameters.Count > 0)
						command.Parameters.AddRange(parameters.ToArray());

					DataTable resultTable = new DataTable();
					NpgsqlDataAdapter adapter = new NpgsqlDataAdapter(command);
					adapter.Fill(resultTable);
					return resultTable;
				}
				finally
				{
					con.Close();
				}
			}
		}

		#endregion
	}
}
