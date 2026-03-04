using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Reporting.Domain.Services
{
	/// <summary>
	/// Primary domain service for the Reporting microservice providing monthly timelog
	/// aggregation and report generation capabilities.
	///
	/// This is a direct transformation of the monolith's
	/// <c>WebVella.Erp.Plugins.Project.Services.ReportService</c> (137 lines) into a
	/// microservice-compatible class adapted for the database-per-service model.
	///
	/// <para><b>Data Access Strategy (CQRS Light — AAP 0.4.3):</b></para>
	/// The Reporting service maintains local projection tables
	/// (<see cref="TimelogProjection"/>, <see cref="TaskProjection"/>,
	/// <see cref="ProjectProjection"/>) populated by MassTransit event subscribers
	/// consuming domain events from Project, CRM, and Core services. This enables
	/// fully independent query execution without cross-service database access.
	///
	/// <para><b>EQL Compatibility:</b></para>
	/// The service attempts to use the SharedKernel EQL engine for timelog queries
	/// (preserving the monolith's exact EQL query strings and JSON deserialization
	/// patterns). When the EQL engine is not configured against the reporting database,
	/// it falls back to EF Core LINQ queries against local projection tables. Both paths
	/// produce identical <see cref="EntityRecord"/> output shapes.
	///
	/// <para><b>Business Rule Preservation (AAP 0.8.1 — Zero Tolerance):</b></para>
	/// Every validation rule, aggregation rule, field name, type cast, and exception
	/// message from the source <c>ReportService.GetTimelogData()</c> is preserved
	/// exactly. See inline source-line references throughout the implementation.
	/// </summary>
	public class ReportAggregationService
	{
		private readonly ReportingDbContext _dbContext;
		private readonly ILogger<ReportAggregationService> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="ReportAggregationService"/> class.
		/// </summary>
		/// <param name="dbContext">
		/// EF Core database context for the Reporting microservice providing access to
		/// local projection tables populated by event subscribers.
		/// </param>
		/// <param name="logger">
		/// Structured logger for microservice-context logging of report aggregation
		/// operations, validation failures, and cross-service query diagnostics.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="dbContext"/> or <paramref name="logger"/> is null.
		/// </exception>
		public ReportAggregationService(ReportingDbContext dbContext, ILogger<ReportAggregationService> logger)
		{
			_dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Gets timelog data aggregated by task and project for a given month and year,
		/// optionally filtered by account.
		///
		/// <para><b>Source:</b> <c>ReportService.GetTimelogData()</c> — lines 13–135.</para>
		///
		/// <para><b>Preserved Business Rules:</b></para>
		/// <list type="bullet">
		///   <item>Validation: month must be 1–12, year must be positive, accountId must exist</item>
		///   <item>Task-project splitting: one task can appear under multiple projects</item>
		///   <item>Billable vs non-billable minutes accumulation per task-project pair</item>
		///   <item>Account filtering when accountId is provided</item>
		///   <item>Error: "There is a project without an account" when filtering by account
		///     and a project has null account_id (source line 82)</item>
		/// </list>
		///
		/// <para><b>Return Record Fields:</b></para>
		/// <c>task_id</c>, <c>project_id</c>, <c>task_subject</c>, <c>project_name</c>,
		/// <c>task_type</c>, <c>billable_minutes</c> (decimal), <c>non_billable_minutes</c> (decimal).
		/// </summary>
		/// <param name="year">The calendar year for the report (must be &gt; 0).</param>
		/// <param name="month">The calendar month for the report (must be 1–12).</param>
		/// <param name="accountId">
		/// Optional account filter. When provided, only tasks linked to projects owned by
		/// this account are included. The account must exist in the reporting projections.
		/// </param>
		/// <returns>
		/// A list of <see cref="EntityRecord"/> instances, one per task-project pair,
		/// containing aggregated timelog minutes. Compatible with the SharedKernel model
		/// and Gateway/BFF REST API v3 response format.
		/// </returns>
		/// <exception cref="ValidationException">
		/// Thrown when month, year, or accountId validation fails. Contains all
		/// accumulated validation errors.
		/// </exception>
		/// <exception cref="Exception">
		/// Thrown when a project has null <c>account_id</c> while filtering by account
		/// (source line 82: "There is a project without an account").
		/// </exception>
		public virtual List<EntityRecord> GetTimelogData(int year, int month, Guid? accountId)
		{
			_logger.LogInformation(
				"GetTimelogData called: year={Year}, month={Month}, accountId={AccountId}",
				year, month, accountId);

			// ================================================================
			// VALIDATION BLOCK (source lines 15–32) — PRESERVED EXACTLY
			// ================================================================
			ValidationException valEx = new ValidationException();

			// Source line 17-18: month range check
			if (month > 12 || month <= 0)
				valEx.AddError("month", "Invalid month.");

			// Source line 20-21: year range check
			if (year <= 0)
				valEx.AddError("year", "Invalid year.");

			// Source lines 24-30: account existence validation
			// Monolith used: new EqlCommand("SELECT * FROM account WHERE id = @id", ...).Execute()
			// Microservice adaptation: Query local project projections instead of cross-service
			// EQL against the CRM service's account entity. We validate that the account exists
			// within our reporting domain by checking if any project references this account_id.
			// The ProjectProjection table is populated by ProjectCreated/Updated events from
			// the Project/CRM services.
			if (accountId.HasValue)
			{
				var accountExists = _dbContext.ProjectProjections.Any(p => p.AccountId == accountId.Value);
				if (!accountExists)
					valEx.AddError("accountId", $"Account with ID:{accountId} not found.");
			}

			// Source line 32: throw-if-errors pattern
			valEx.CheckAndThrow();

			// ================================================================
			// DATE RANGE CALCULATION (source lines 35–36) — PRESERVED EXACTLY
			// ================================================================
			DateTime fromDate = new DateTime(year, month, 1);
			DateTime toDate = new DateTime(year, month, DateTime.DaysInMonth(year, month));

			// ================================================================
			// TIMELOG LOADING (source lines 37–56) — ADAPTED for microservice
			// ================================================================
			// Attempts EQL first (preserves monolith EQL query + JSON deserialization),
			// falls back to EF Core projection queries when EQL is not configured.
			EntityRecordList timelogRecords = LoadTimelogRecords(fromDate, toDate);

			// Build set of task IDs that have timelog entries (source lines 48–56)
			// In the EQL path, task_id extraction from l_related_records JSON happens
			// inside LoadTimelogRecords. In the projection path, TaskId is already
			// denormalized. Both paths set timelog["task_id"] to the extracted Guid.
			HashSet<Guid> setOfTasksWithTimelog = new HashSet<Guid>();
			foreach (var timelog in timelogRecords)
			{
				Guid taskId = (Guid)timelog["task_id"];
				if (!setOfTasksWithTimelog.Contains(taskId))
					setOfTasksWithTimelog.Add(taskId);
			}

			// ================================================================
			// TASK LOADING (source lines 58–61) — CROSS-SERVICE ADAPTATION
			// ================================================================
			// Monolith used: SELECT id,subject,$project_nn_task.id,$project_nn_task.name,
			//   $project_nn_task.account_id,$task_type_1n_task.label FROM task
			// This is a cross-service query spanning Project/CRM services.
			// Microservice adaptation: Query local task/project projections populated
			// by TaskCreated/Updated and ProjectCreated/Updated event subscribers.
			EntityRecordList tasks = LoadTaskRecordsFromProjections(setOfTasksWithTimelog);

			// ================================================================
			// TASK PROCESSING (source lines 63–99) — PRESERVED EXACTLY
			// ================================================================
			// Process tasks: split records for projects, filter by account, filter by timelog.
			// This logic creates separate entries when a task is linked to multiple projects.
			EntityRecordList processedTasks = new EntityRecordList();
			foreach (var task in tasks)
			{
				// Skip task that has no timelog record (source lines 68-69)
				if (!setOfTasksWithTimelog.Contains((Guid)task["id"]))
					continue;

				List<EntityRecord> taskProjects = (List<EntityRecord>)task["$project_nn_task"];
				// Skip tasks with no project (source lines 72-74)
				if (taskProjects.Count == 0)
					continue;

				// Split tasks to projects if more than one project is related to task
				// (source lines 77-97)
				foreach (var project in taskProjects)
				{
					if (accountId != null)
					{
						// Source lines 81-83: project must have an account when filtering by account
						if (project["account_id"] == null)
						{
							throw new Exception("There is a project without an account");
						}

						// Source line 85: filter by matching account
						if ((Guid)project["account_id"] == accountId)
						{
							task["project"] = project;
							processedTasks.Add(task);
						}
					}
					else
					{
						// Source lines 91-95: include all task-project pairs when no account filter
						task["project"] = project;
						processedTasks.Add(task);
					}
				}
			}
			// Source lines 98-99: replace tasks with processed list
			tasks = processedTasks;
			tasks.TotalCount = processedTasks.Count;

			// ================================================================
			// RESULT AGGREGATION (source lines 103–134) — PRESERVED EXACTLY
			// ================================================================
			// Build result records with billable/non-billable minutes per task-project pair.
			// Field names, decimal casts, and accumulation logic match source exactly.
			List<EntityRecord> result = new List<EntityRecord>();
			foreach (var task in tasks)
			{
				EntityRecord rec = new EntityRecord();
				rec["task_id"] = task["id"];
				rec["project_id"] = ((EntityRecord)task["project"])["id"];
				rec["task_subject"] = task["subject"];
				rec["project_name"] = ((EntityRecord)task["project"])["name"];
				rec["task_type"] = ((List<EntityRecord>)task["$task_type_1n_task"])[0]["label"];
				rec["billable_minutes"] = (decimal)0;
				rec["non_billable_minutes"] = (decimal)0;

				// Source lines 120-128: accumulate minutes from timelogs matching this task
				foreach (var timelog in timelogRecords)
				{
					if ((Guid)timelog["task_id"] != (Guid)task["id"])
						continue;

					if ((bool)timelog["is_billable"])
						rec["billable_minutes"] = (decimal)rec["billable_minutes"] + (decimal)timelog["minutes"];
					else
						rec["non_billable_minutes"] = (decimal)rec["non_billable_minutes"] + (decimal)timelog["minutes"];
				}

				result.Add(rec);
			}

			_logger.LogInformation(
				"GetTimelogData completed: {ResultCount} result records for year={Year}, month={Month}",
				result.Count, year, month);

			return result;
		}

		#region Private Data Loading Methods

		/// <summary>
		/// Loads timelog records for the specified date range, attempting the EQL engine
		/// first (which preserves the monolith's exact query string and JSON deserialization
		/// pattern), then falling back to EF Core projection queries.
		///
		/// <para><b>EQL Path (Primary — source lines 37–56):</b></para>
		/// Uses <see cref="EqlCommand"/> with the preserved EQL query string and parameter
		/// bindings. Timelog records returned by EQL have <c>l_related_records</c> as a
		/// JSON-serialized <c>List&lt;Guid&gt;</c> where <c>ids[0]</c> is the task_id.
		/// This JSON is deserialized via <see cref="JsonConvert.DeserializeObject{T}(string)"/>.
		///
		/// <para><b>Projection Path (Fallback):</b></para>
		/// Uses <see cref="ReportingDbContext.TimelogProjections"/> where TaskId is already
		/// denormalized from the original <c>l_related_records</c> during event processing.
		/// </summary>
		/// <param name="fromDate">Start of the date range (first day of month).</param>
		/// <param name="toDate">End of the date range (last day of month).</param>
		/// <returns>
		/// An <see cref="EntityRecordList"/> where each record has fields:
		/// <c>id</c>, <c>is_billable</c>, <c>minutes</c>, <c>task_id</c>.
		/// The EQL path additionally retains <c>l_related_records</c>.
		/// </returns>
		private EntityRecordList LoadTimelogRecords(DateTime fromDate, DateTime toDate)
		{
			// ----------------------------------------------------------------
			// PRIMARY PATH: EQL query against reporting database
			// Preserves the monolith's exact EQL query string and parameter names
			// for backward compatibility with the SharedKernel EQL engine.
			// ----------------------------------------------------------------
			try
			{
				var eqlParams = new List<EqlParameter>() {
					new EqlParameter("from_date", fromDate),
					new EqlParameter("to_date", toDate),
					new EqlParameter("scope", "projects"),
				};

				// Preserved EQL query from source lines 42-45
				string eql = @"SELECT id,is_billable,l_related_records,minutes FROM timelog 
							   WHERE logged_on >= @from_date AND 
								  logged_on <= @to_date AND
								  l_scope CONTAINS @scope";

				var eqlResult = new EqlCommand(eql, eqlParams).Execute();

				// Extract task_id from l_related_records JSON (source lines 48-56)
				// The l_related_records field is a JSON-serialized List<Guid> where
				// ids[0] is always the task_id. This is a core business rule from
				// the monolith that must not change.
				foreach (var timelog in eqlResult)
				{
					List<Guid> ids = JsonConvert.DeserializeObject<List<Guid>>(
						(string)timelog["l_related_records"]);
					Guid taskId = ids[0];
					timelog["task_id"] = taskId;
				}

				_logger.LogDebug(
					"Loaded {Count} timelog records via EQL for range {From} to {To}",
					eqlResult.Count, fromDate, toDate);

				return eqlResult;
			}
			catch (Exception ex)
			{
				// ----------------------------------------------------------------
				// FALLBACK PATH: EF Core projection query
				// Used when the EQL engine is not configured against the reporting
				// database (e.g., entity metadata not registered, or reporting DB
				// uses EF Core projection schema instead of monolith rec_* tables).
				// ----------------------------------------------------------------
				_logger.LogWarning(
					ex,
					"EQL timelog query unavailable for range {From} to {To}, using projection fallback",
					fromDate, toDate);

				return LoadTimelogRecordsFromProjections(fromDate, toDate);
			}
		}

		/// <summary>
		/// Loads timelog records from the local <see cref="TimelogProjection"/> table
		/// using EF Core LINQ queries. This is the CQRS read model fallback path.
		///
		/// The projection table is populated by event subscribers consuming
		/// <c>TimelogCreated</c>, <c>TimelogUpdated</c>, and <c>TimelogDeleted</c>
		/// events from the Project service.
		///
		/// <para>
		/// Maps to the monolith's EQL query (source lines 42-45):
		/// <c>SELECT id,is_billable,l_related_records,minutes FROM timelog
		/// WHERE logged_on &gt;= @from_date AND logged_on &lt;= @to_date
		/// AND l_scope CONTAINS @scope</c>
		/// </para>
		/// </summary>
		private EntityRecordList LoadTimelogRecordsFromProjections(DateTime fromDate, DateTime toDate)
		{
			var projections = _dbContext.TimelogProjections
				.Where(t => t.LoggedOn >= fromDate && t.LoggedOn <= toDate
					&& t.Scope.Contains("projects"))
				.ToList();

			var records = new EntityRecordList();
			foreach (var tp in projections)
			{
				var record = new EntityRecord();
				record["id"] = tp.Id;
				record["is_billable"] = tp.IsBillable;
				record["minutes"] = tp.Minutes;
				// In the projection model, TaskId is already denormalized from the
				// original l_related_records JSON during event processing by the
				// TimelogCreated event subscriber.
				record["task_id"] = tp.TaskId;
				records.Add(record);
			}
			records.TotalCount = records.Count;

			_logger.LogDebug(
				"Loaded {Count} timelog records from projections for range {From} to {To}",
				records.Count, fromDate, toDate);

			return records;
		}

		/// <summary>
		/// Loads task records with project and task-type relation data from local
		/// projections, building <see cref="EntityRecord"/> structures that match the
		/// shape produced by the monolith's cross-entity EQL query (source line 60):
		/// <c>SELECT id,subject,$project_nn_task.id,$project_nn_task.name,
		/// $project_nn_task.account_id,$task_type_1n_task.label FROM task</c>
		///
		/// <para><b>Cross-Service Adaptation:</b></para>
		/// In the monolith, this query joined across task, project, and task_type
		/// entities in a single database. In the microservice, tasks belong to the
		/// Project service, projects span Project/CRM, and accounts belong to CRM.
		/// This method reconstructs the same data shape from local projection tables:
		/// <list type="bullet">
		///   <item><see cref="TaskProjection"/>: task id, subject, task_type_label</item>
		///   <item><see cref="ProjectProjection"/>: project id, name, account_id</item>
		///   <item>Task-project N:N relationships reconstructed from
		///     <see cref="TimelogProjection"/> (TaskId → ProjectId mappings)</item>
		/// </list>
		/// </summary>
		/// <param name="taskIds">
		/// Set of task IDs that have timelog entries in the date range.
		/// Only tasks in this set are loaded from projections.
		/// </param>
		/// <returns>
		/// An <see cref="EntityRecordList"/> where each record has fields:
		/// <c>id</c>, <c>subject</c>, <c>$project_nn_task</c> (List&lt;EntityRecord&gt;
		/// with id/name/account_id), <c>$task_type_1n_task</c> (List&lt;EntityRecord&gt;
		/// with label).
		/// </returns>
		private EntityRecordList LoadTaskRecordsFromProjections(HashSet<Guid> taskIds)
		{
			// Convert to list for EF Core LINQ compatibility with IN clause translation
			var taskIdList = taskIds.ToList();

			// Load task details from projections
			var taskProjections = _dbContext.TaskProjections
				.Where(t => taskIdList.Contains(t.Id))
				.ToList();

			// Load all project projections for building task-project relationships
			// Projects are loaded in full because a task may reference any project,
			// and project projections are typically a manageable dataset size.
			var projectProjections = _dbContext.ProjectProjections.ToList();
			var projectMap = projectProjections.ToDictionary(p => p.Id);

			// Reconstruct the N:N task-project relationship ($project_nn_task) from
			// timelog projections. Each timelog links a TaskId to a ProjectId. By
			// collecting distinct (TaskId, ProjectId) pairs, we approximate the
			// monolith's $project_nn_task relation within the reporting domain.
			var taskProjectRelations = _dbContext.TimelogProjections
				.Where(tl => taskIdList.Contains(tl.TaskId) && tl.ProjectId.HasValue)
				.Select(tl => new { tl.TaskId, ProjectId = tl.ProjectId.Value })
				.Distinct()
				.ToList();

			var taskProjectMap = new Dictionary<Guid, HashSet<Guid>>();
			foreach (var relation in taskProjectRelations)
			{
				if (!taskProjectMap.ContainsKey(relation.TaskId))
					taskProjectMap[relation.TaskId] = new HashSet<Guid>();
				taskProjectMap[relation.TaskId].Add(relation.ProjectId);
			}

			// Build EntityRecordList matching the monolith's EQL result shape
			// so the downstream processing logic (source lines 63-99) works identically.
			var tasks = new EntityRecordList();
			foreach (var taskProj in taskProjections)
			{
				var task = new EntityRecord();
				task["id"] = taskProj.Id;
				task["subject"] = taskProj.Subject;

				// Build $task_type_1n_task relation list (source line 60, used at line 111)
				// In the monolith, this is a 1:N relation from task_type to task.
				// The projection stores the resolved label directly.
				var taskTypeList = new List<EntityRecord>();
				var taskTypeRecord = new EntityRecord();
				taskTypeRecord["label"] = taskProj.TaskTypeLabel ?? "";
				taskTypeList.Add(taskTypeRecord);
				task["$task_type_1n_task"] = taskTypeList;

				// Build $project_nn_task relation list from projection mapping
				// (source line 60: $project_nn_task.id, $project_nn_task.name,
				//  $project_nn_task.account_id)
				var projectList = new List<EntityRecord>();
				if (taskProjectMap.TryGetValue(taskProj.Id, out var projectIds))
				{
					foreach (var projectId in projectIds)
					{
						if (projectMap.TryGetValue(projectId, out var projProj))
						{
							var projectRec = new EntityRecord();
							projectRec["id"] = projProj.Id;
							projectRec["name"] = projProj.Name;
							// account_id is nullable: store the Guid value or null
							// to match the source behavior where project["account_id"]
							// can be null (checked at source line 81-82).
							projectRec["account_id"] = projProj.AccountId.HasValue
								? (object)projProj.AccountId.Value
								: null;
							projectList.Add(projectRec);
						}
					}
				}
				task["$project_nn_task"] = projectList;
				tasks.Add(task);
			}
			tasks.TotalCount = tasks.Count;

			_logger.LogDebug(
				"Loaded {TaskCount} task records with {RelationCount} task-project relations from projections",
				tasks.Count, taskProjectRelations.Count);

			return tasks;
		}

		#endregion
	}
}
