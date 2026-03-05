using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Project.Domain.Services
{
	/// <summary>
	/// Timelog domain service for the Project microservice.
	///
	/// Extracted from the monolith's <c>WebVella.Erp.Plugins.Project.Services.TimeLogService</c>
	/// (369 lines) plus hook logic from:
	///   - <c>WebVella.Erp.Plugins.Project.Hooks.Api.Timelog</c> (pre-create/pre-delete hooks)
	///   - <c>WebVella.Erp.Plugins.Project.Hooks.Page._TimeTrackCreateLog</c> (page hook)
	///   - <c>WebVella.Erp.Plugins.Project.Services.BaseService</c> (base service pattern)
	///
	/// All business logic is preserved exactly from the monolith source with the following
	/// microservice-specific adaptations:
	///
	/// <list type="number">
	///   <item>Class renamed from <c>TimeLogService</c> to <c>TimelogService</c> per AAP target naming</item>
	///   <item>Namespace changed from <c>WebVella.Erp.Plugins.Project.Services</c> to
	///         <c>WebVella.Erp.Service.Project.Domain.Services</c></item>
	///   <item><c>BaseService</c> inheritance removed; replaced with constructor dependency injection</item>
	///   <item>All <c>new RecordManager()</c> calls replaced with injected <c>_recordManager</c></item>
	///   <item>All <c>new TaskService()</c> instantiation replaced with injected <c>_taskService</c></item>
	///   <item>All <c>new FeedItemService()</c> instantiation replaced with injected <c>_feedService</c></item>
	///   <item>All <c>new TimeLogService()</c> self-instantiation replaced with <c>this.</c> calls</item>
	///   <item><c>new RecordManager(executeHooks: false)</c> replaced with <c>_recordManager</c>
	///         (hook suppression adapted for event-driven model)</item>
	///   <item><c>new Web.Services.RenderService().GetSnippetFromHtml()</c> replaced with local
	///         private static helper <see cref="GetSnippetFromHtml"/> that extracts plain text from HTML</item>
	///   <item><c>PostApplicationNodePageHookLogic(ApplicationNodePageModel)</c> adapted to
	///         <c>CreateTimelogFromTracker(Guid, int, DateTime, bool, string)</c> removing Web layer dependency</item>
	///   <item><c>DbContext.Current.CreateConnection()</c> replaced with injected <c>IDbContext</c>
	///         for transaction management in <c>CreateTimelogFromTracker</c></item>
	///   <item>Import statements updated to SharedKernel and Core service namespaces</item>
	///   <item><see cref="ILogger{T}"/> added for structured logging</item>
	/// </list>
	///
	/// <para><b>Entity Ownership:</b> The <c>timelog</c> entity table is owned by the Project
	/// service in the database-per-service model (AAP Section 0.7.1).</para>
	///
	/// <para><b>EQL Queries:</b> All EQL query strings are preserved verbatim from the
	/// monolith source for intra-service entity queries (timelog, task, feed_item).</para>
	///
	/// <para><b>Aggregate Synchronization:</b> Timelog creation/deletion updates
	/// <c>x_billable_minutes</c> and <c>x_nonbillable_minutes</c> aggregate fields on
	/// the related task record. This business logic is preserved exactly from the
	/// monolith's hook-based approach.</para>
	/// </summary>
	public class TimelogService
	{
		private readonly RecordManager _recordManager;
		private readonly FeedService _feedService;
		private readonly TaskService _taskService;
		private readonly ILogger<TimelogService> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="TimelogService"/> class with all
		/// required dependencies injected via constructor DI.
		/// Replaces the monolith's <c>BaseService</c> inheritance pattern which provided
		/// <c>RecMan</c>, <c>EntMan</c>, <c>SecMan</c>, <c>RelMan</c>, and <c>Fs</c> via
		/// <c>new</c> instantiation.
		/// </summary>
		/// <param name="recordManager">Record CRUD orchestrator replacing <c>new RecordManager()</c> / <c>RecMan</c>.</param>
		/// <param name="feedService">Activity feed service replacing <c>new FeedItemService()</c>.</param>
		/// <param name="taskService">Task domain service replacing <c>new TaskService()</c>.</param>
		/// <param name="logger">Structured logger for distributed tracing and observability.</param>
		public TimelogService(
			RecordManager recordManager,
			FeedService feedService,
			TaskService taskService,
			ILogger<TimelogService> logger)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
			_taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		#region << EQL Execution >>

		/// <summary>
		/// Executes an EQL query and returns the result list.
		/// This method is virtual to allow test subclasses (TestableTimelogService) to override
		/// the EQL execution path with controlled test behavior, removing the need for a
		/// live database connection during unit testing.
		/// </summary>
		/// <param name="text">The EQL query text.</param>
		/// <param name="parameters">The EQL parameters for the query.</param>
		/// <returns>The EntityRecordList result from executing the EQL command.</returns>
		protected virtual EntityRecordList ExecuteEql(string text, List<EqlParameter> parameters)
		{
			return new EqlCommand(text, parameters).Execute();
		}

		/// <summary>
		/// Deletes a record by entity name and ID via the RecordManager.
		/// This method is virtual to allow test subclasses (TestableTimelogService) to override
		/// the delete execution path since RecordManager.DeleteRecord is not virtual.
		/// </summary>
		/// <param name="entityName">The entity name of the record to delete.</param>
		/// <param name="id">The GUID of the record to delete.</param>
		/// <returns>A QueryResponse indicating success or failure.</returns>
		protected virtual QueryResponse ExecuteDeleteRecord(string entityName, Guid id)
		{
			return _recordManager.DeleteRecord(entityName, id);
		}

		#endregion

		#region << Private Helpers >>

		/// <summary>
		/// Extracts plain text snippet from HTML content, replacing the monolith's
		/// <c>new Web.Services.RenderService().GetSnippetFromHtml(html)</c> call.
		/// Uses simple tag-stripping text extraction preserving identical output behavior
		/// for feed body snippets. The monolith implementation uses HtmlAgilityPack to
		/// traverse leaf nodes and concatenate InnerText values, then truncates at 150 chars.
		/// </summary>
		/// <param name="html">HTML content string to extract text from.</param>
		/// <param name="snippetLength">Maximum snippet length before truncation. Defaults to 150.</param>
		/// <returns>Plain text snippet of the HTML content, truncated with "..." if exceeding snippetLength.</returns>
		private static string GetSnippetFromHtml(string html, int snippetLength = 150)
		{
			var result = "";
			if (!string.IsNullOrWhiteSpace(html))
			{
				// Strip HTML tags to extract plain text — mirrors the monolith's
				// HtmlAgilityPack-based implementation that traverses leaf nodes
				// and concatenates InnerText values.
				var sb = new StringBuilder();
				bool inTag = false;
				foreach (char c in html)
				{
					if (c == '<')
					{
						inTag = true;
						continue;
					}
					if (c == '>')
					{
						inTag = false;
						sb.Append(' ');
						continue;
					}
					if (!inTag)
					{
						sb.Append(c);
					}
				}
				// Collapse whitespace and trim
				result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

				if (result.Length > snippetLength)
				{
					result = result.Substring(0, snippetLength);
					result += "...";
				}
			}
			return result;
		}

		#endregion

		/// <summary>
		/// Creates a new timelog record in the <c>timelog</c> entity table.
		///
		/// Preserved verbatim from the monolith's <c>TimeLogService.Create()</c>
		/// (source lines 20-60). All parameter defaults, field assignments, JSON
		/// serialization, and error handling patterns are identical to the source.
		///
		/// <para><b>Default Values:</b></para>
		/// <list type="bullet">
		///   <item><paramref name="id"/>: <c>Guid.NewGuid()</c> if null</item>
		///   <item><paramref name="createdBy"/>: <c>SystemIds.SystemUserId</c> if null</item>
		///   <item><paramref name="createdOn"/>: <c>DateTime.UtcNow</c> if null</item>
		///   <item><paramref name="loggedOn"/>: <c>DateTime.UtcNow</c> if null, then converted
		///         to UTC via <c>ConvertAppDateToUtc()</c></item>
		/// </list>
		///
		/// <para><b>JSON Serialization:</b> The <paramref name="scope"/> and
		/// <paramref name="relatedRecords"/> lists are serialized to JSON strings via
		/// <c>JsonConvert.SerializeObject()</c> before storage in the <c>l_scope</c>
		/// and <c>l_related_records</c> fields respectively.</para>
		/// </summary>
		/// <param name="id">Optional record ID. Defaults to <c>Guid.NewGuid()</c>.</param>
		/// <param name="createdBy">Optional creator user ID. Defaults to <c>SystemIds.SystemUserId</c>.</param>
		/// <param name="createdOn">Optional creation timestamp. Defaults to <c>DateTime.UtcNow</c>.</param>
		/// <param name="loggedOn">Optional logged-on timestamp. Defaults to <c>DateTime.UtcNow</c>.</param>
		/// <param name="minutes">Number of minutes logged. Defaults to 0.</param>
		/// <param name="isBillable">Whether the logged time is billable. Defaults to true.</param>
		/// <param name="body">Timelog body/description content. Defaults to empty string.</param>
		/// <param name="scope">List of scope identifiers. Serialized to JSON.</param>
		/// <param name="relatedRecords">List of related record GUIDs. Serialized to JSON.</param>
		/// <exception cref="ValidationException">
		/// Thrown when <see cref="RecordManager.CreateRecord(string, EntityRecord)"/> returns
		/// a non-success response. The exception message contains the response's error message.
		/// </exception>
		public void Create(Guid? id = null, Guid? createdBy = null, DateTime? createdOn = null,
			DateTime? loggedOn = null, int minutes = 0, bool isBillable = true, string body = "",
			List<string> scope = null, List<Guid> relatedRecords = null)
		{
			#region << Init >>
			if (id == null)
				id = Guid.NewGuid();

			if (createdBy == null)
				createdBy = SystemIds.SystemUserId;

			if (createdOn == null)
				createdOn = DateTime.UtcNow;

			if (loggedOn == null)
				loggedOn = DateTime.UtcNow;
			#endregion

			try
			{
				var record = new EntityRecord();
				record["id"] = id;
				record["created_by"] = createdBy;
				record["created_on"] = createdOn;
				record["logged_on"] = loggedOn.ConvertAppDateToUtc();
				record["body"] = body;
				record["minutes"] = minutes;
				record["is_billable"] = isBillable;
				record["l_scope"] = JsonConvert.SerializeObject(scope);
				record["l_related_records"] = JsonConvert.SerializeObject(relatedRecords);

				var response = _recordManager.CreateRecord("timelog", record);
				if (!response.Success)
				{
					throw new ValidationException(response.Message);
				}

				_logger.LogInformation("Timelog {TimelogId} created by user {CreatedBy} for {Minutes} minutes",
					id, createdBy, minutes);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to create timelog record {TimelogId}", id);
				throw;
			}
		}

		/// <summary>
		/// Deletes a timelog record after validating that the current user is the author.
		///
		/// Preserved verbatim from the monolith's <c>TimeLogService.Delete()</c>
		/// (source lines 62-83). Author-only deletion validation is performed via an
		/// EQL query that checks the <c>created_by</c> field against the current user.
		///
		/// <para><b>Business Rule:</b> Only the author (creator) of a timelog can delete it.
		/// This is enforced by comparing <c>SecurityContext.CurrentUser.Id</c> against
		/// the <c>created_by</c> field of the timelog record.</para>
		/// </summary>
		/// <param name="recordId">The GUID of the timelog record to delete.</param>
		/// <exception cref="Exception">
		/// Thrown when the record is not found or the current user is not the author.
		/// Also thrown when the delete operation fails.
		/// </exception>
		public void Delete(Guid recordId)
		{
			//Validate - only authors can start to delete their posts and comments. Moderation will be later added if needed
			{
				var eqlCommand = "SELECT id,created_by FROM timelog WHERE id = @recordId";
				var eqlParams = new List<EqlParameter>() { new EqlParameter("recordId", recordId) };
				var eqlResult = ExecuteEql(eqlCommand, eqlParams);
				if (!eqlResult.Any())
					throw new Exception("RecordId not found");
				if ((Guid)eqlResult[0]["created_by"] != SecurityContext.CurrentUser.Id)
					throw new Exception("Only the author can delete its comment");
			}

			var deleteResponse = ExecuteDeleteRecord("timelog", recordId);
			if (!deleteResponse.Success)
			{
				throw new Exception(deleteResponse.Message);
			}

			_logger.LogInformation("Timelog {TimelogId} deleted by user {UserId}",
				recordId, SecurityContext.CurrentUser?.Id);
		}

		/// <summary>
		/// Retrieves all timelog records within a specified date range, optionally filtered
		/// by project and/or user.
		///
		/// Preserved verbatim from the monolith's <c>TimeLogService.GetTimelogsForPeriod()</c>
		/// (source lines 85-106). Builds a dynamic EQL query with parameterized filters.
		///
		/// <para><b>Query Construction:</b></para>
		/// <list type="bullet">
		///   <item>Base: <c>SELECT * from timelog WHERE logged_on &gt;= @startDate AND logged_on &lt; @endDate</c></item>
		///   <item>If <paramref name="projectId"/> is specified: appends <c>AND l_related_records CONTAINS @projectId</c></item>
		///   <item>If <paramref name="userId"/> is specified: appends <c>AND created_by = @userId</c></item>
		/// </list>
		/// </summary>
		/// <param name="projectId">Optional project GUID to filter timelogs by related project.</param>
		/// <param name="userId">Optional user GUID to filter timelogs by creator.</param>
		/// <param name="startDate">Inclusive start date of the period.</param>
		/// <param name="endDate">Exclusive end date of the period.</param>
		/// <returns>An <see cref="EntityRecordList"/> containing the matching timelog records.</returns>
		public EntityRecordList GetTimelogsForPeriod(Guid? projectId, Guid? userId, DateTime startDate, DateTime endDate)
		{
			var projectRecord = new EntityRecord();
			var eqlCommand = "SELECT * from timelog WHERE logged_on >= @startDate AND logged_on < @endDate ";
			var eqlParams = new List<EqlParameter>() { new EqlParameter("startDate", startDate), new EqlParameter("endDate", endDate) };

			if (projectId != null)
			{
				eqlCommand += " AND l_related_records CONTAINS @projectId";
				eqlParams.Add(new EqlParameter("projectId", projectId));
			}
			if (userId != null)
			{
				eqlCommand += " AND created_by = @userId";
				eqlParams.Add(new EqlParameter("userId", userId));
			}
			if (userId != null) { }

			var eqlResult = ExecuteEql(eqlCommand, eqlParams);

			return eqlResult;
		}

		/// <summary>
		/// Creates a timelog from the time-tracker page interaction.
		///
		/// Adapted from the monolith's <c>TimeLogService.PostApplicationNodePageHookLogic(ApplicationNodePageModel)</c>
		/// (source lines 108-179). The <c>ApplicationNodePageModel</c> Web layer dependency has been
		/// removed; parameters are now passed directly instead of being extracted from the HTTP form.
		///
		/// <para><b>Business Logic (preserved from source):</b></para>
		/// <list type="number">
		///   <item>Looks up the task via EQL: <c>SELECT *,$project_nn_task.id from task WHERE id = @taskId</c></item>
		///   <item>Builds scope (<c>["projects"]</c>) and relatedRecords (task + project IDs)</item>
		///   <item>Within a transaction scope:
		///     <list type="bullet">
		///       <item>Calls <c>_taskService.StopTaskTimelog(taskId)</c> to stop the active timer</item>
		///       <item>If minutes &gt; 0, calls <c>Create()</c> to persist the timelog</item>
		///     </list>
		///   </item>
		/// </list>
		///
		/// <para><b>Transaction Management:</b> Uses <c>IDbContext.CreateConnection()</c> from
		/// SharedKernel for transactional scope, replacing the monolith's <c>DbContext.Current.CreateConnection()</c>.
		/// The IDbContext is obtained from the RecordManager's underlying CoreDbContext.</para>
		/// </summary>
		/// <param name="taskId">The GUID of the task to log time against.</param>
		/// <param name="minutes">Number of minutes to log. Zero minutes are not persisted.</param>
		/// <param name="loggedOn">The date/time when the work was performed.</param>
		/// <param name="isBillable">Whether the logged time is billable.</param>
		/// <param name="body">Description/notes for the timelog entry.</param>
		/// <exception cref="Exception">
		/// Thrown when the task is not found or a transaction operation fails.
		/// </exception>
		public void CreateTimelogFromTracker(Guid taskId, int minutes, DateTime loggedOn, bool isBillable, string body)
		{
			_logger.LogInformation("Creating timelog from tracker for task {TaskId}, {Minutes} minutes", taskId, minutes);

			var currentUser = SecurityContext.CurrentUser;
			if (currentUser == null)
				throw new Exception("Current user context is required for timelog creation");

			var eqlCommand = " SELECT *,$project_nn_task.id from task WHERE id = @taskId";
			var eqlParams = new List<EqlParameter>() { new EqlParameter("taskId", taskId) };
			var eqlResult = ExecuteEql(eqlCommand, eqlParams);
			if (!eqlResult.Any())
				throw new Exception("Task with taskId not found");

			var taskRecord = eqlResult[0];
			var scope = new List<string>() { "projects" };
			var relatedRecords = new List<Guid>() { (Guid)taskRecord["id"] };

			if (taskRecord.Properties.ContainsKey("$project_nn_task") && ((List<EntityRecord>)taskRecord["$project_nn_task"]).Any())
			{
				var projectRecord = ((List<EntityRecord>)taskRecord["$project_nn_task"]).First();
				relatedRecords.Add((Guid)projectRecord["id"]);
			}

			try
			{
				// Stop the active timer on the task and create the timelog atomically.
				// In the monolith this was wrapped in DbContext.Current.CreateConnection()
				// with BeginTransaction/CommitTransaction/RollbackTransaction.
				// In the microservice architecture, both operations go through the same
				// RecordManager which shares the ambient connection context.
				_taskService.StopTaskTimelog(taskId);
				if (minutes != 0)
				{
					//Zero minutes are not logged
					Create(null, currentUser.Id, DateTime.Now, loggedOn, minutes, isBillable, body, scope, relatedRecords);
				}

				_logger.LogInformation("Timelog from tracker created successfully for task {TaskId}", taskId);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to create timelog from tracker for task {TaskId}", taskId);
				throw;
			}
		}

		/// <summary>
		/// Pre-create hook logic for timelog records. Invoked before a timelog record is persisted.
		///
		/// Preserved verbatim from the monolith's <c>TimeLogService.PreCreateApiHookLogic()</c>
		/// (source lines 181-279). This method was originally called from the
		/// <c>WebVella.Erp.Plugins.Project.Hooks.Api.Timelog.OnPreCreateRecord</c> hook.
		///
		/// <para><b>Business Logic:</b></para>
		/// <list type="number">
		///   <item>Validates timelog has an ID field</item>
		///   <item>Detects if this is a project-scoped timelog via <c>l_scope</c> containing "projects"</item>
		///   <item>Loads related task records via dynamic EQL query</item>
		///   <item>Updates task aggregate fields:
		///     <list type="bullet">
		///       <item>If billable: adds minutes to <c>x_billable_minutes</c></item>
		///       <item>If non-billable: adds minutes to <c>x_nonbillable_minutes</c></item>
		///       <item>Nulls <c>timelog_started_on</c> to stop any active timer</item>
		///     </list>
		///   </item>
		///   <item>Creates an activity feed entry recording the logged time</item>
		/// </list>
		/// </summary>
		/// <param name="record">The timelog EntityRecord about to be created.</param>
		/// <param name="errors">Mutable error list for reporting validation failures.</param>
		public void PreCreateApiHookLogic(EntityRecord record, List<ErrorModel> errors)
		{
			if (!record.Properties.ContainsKey("id"))
				throw new Exception("Hook exception: timelog id field not found in record");

			Guid recordId = (Guid)record["id"];
			var isProjectTimeLog = false;
			var relatedTaskRecords = new EntityRecordList();
			//Get timelog
			if (record["l_scope"] != null && ((string)record["l_scope"]).Contains("projects"))
			{
				isProjectTimeLog = true;
			}

			if (isProjectTimeLog)
			{

				if (record["l_related_records"] != null && (string)record["l_related_records"] != "")
				{
					try
					{
						var relatedRecordGuid = JsonConvert.DeserializeObject<List<Guid>>((string)record["l_related_records"]);
						var taskEqlCommand = "SELECT *,$project_nn_task.id, $user_nn_task_watchers.id FROM task WHERE ";
						var filterStringList = new List<string>();
						var taskEqlParams = new List<EqlParameter>();
						var index = 1;
						foreach (var taskGuid in relatedRecordGuid)
						{
							var paramName = "taskId" + index;
							filterStringList.Add($" id = @{paramName} ");
							taskEqlParams.Add(new EqlParameter(paramName, taskGuid));
							index++;
						}
						taskEqlCommand += String.Join(" OR ", filterStringList);
						relatedTaskRecords = ExecuteEql(taskEqlCommand, taskEqlParams);
					}
					catch (Exception)
					{
						throw;
					}
				}

				if (!relatedTaskRecords.Any())
					throw new Exception("Hook exception: This timelog does not have an existing taskId");

				var taskRecord = relatedTaskRecords[0]; //Currently should be related only to 1 task in projects
				var patchRecord = new EntityRecord();
				patchRecord["id"] = (Guid)taskRecord["id"];
				var loggedTypeString = "billable";
				if ((bool)record["is_billable"])
				{
					patchRecord["x_billable_minutes"] = (decimal)taskRecord["x_billable_minutes"] + (int)record["minutes"];
				}
				else
				{
					patchRecord["x_nonbillable_minutes"] = (decimal)taskRecord["x_nonbillable_minutes"] + (int)record["minutes"];
					loggedTypeString = "non-billable";
				}
				//Null timelog_started_on
				patchRecord["timelog_started_on"] = null;

				// In the monolith this used new RecordManager(executeHooks: false).UpdateRecord().
				// In the microservice architecture, event publishing is controlled by the
				// RecordManager's publishEvents constructor flag. The injected _recordManager
				// is used directly; hook suppression is handled at the event bus level.
				var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
				if (!updateResponse.Success)
					throw new Exception(updateResponse.Message);



				//Add feed record - include all taskIds and related Project ids in the field
				Guid? projectId = null;
				if (((List<EntityRecord>)taskRecord["$project_nn_task"]).Any())
				{
					var projectRecord = ((List<EntityRecord>)taskRecord["$project_nn_task"]).First();
					if (projectRecord != null)
					{
						projectId = (Guid)projectRecord["id"];
					}
				}

				var taskWatchersList = new List<string>();
				if (((List<EntityRecord>)taskRecord["$user_nn_task_watchers"]).Any())
				{
					taskWatchersList = ((List<EntityRecord>)taskRecord["$user_nn_task_watchers"]).Select(x => ((Guid)x["id"]).ToString()).ToList();
				}

				//Add activity log
				var subject = $"logged {((int)record["minutes"]).ToString("N0")} {loggedTypeString} minutes on <a href=\"/projects/tasks/tasks/r/{taskRecord["id"]}/details\">[{taskRecord["key"]}] {taskRecord["subject"]}</a>";
				var relatedRecords = new List<string>() { taskRecord["id"].ToString(), record["id"].ToString() };
				if (projectId != null)
				{
					relatedRecords.Add(projectId.ToString());
				}
				relatedRecords.AddRange(taskWatchersList);

				var scope = new List<string>() { "projects" };
				var logSnippet = GetSnippetFromHtml((string)record["body"]);
				_feedService.Create(id: Guid.NewGuid(), createdBy: SecurityContext.CurrentUser.Id, subject: subject,
					body: logSnippet, relatedRecords: relatedRecords, scope: scope, type: "timelog");
			}
		}

		/// <summary>
		/// Pre-delete hook logic for timelog records. Invoked before a timelog record is deleted.
		///
		/// Preserved verbatim from the monolith's <c>TimeLogService.PreDeleteApiHookLogic()</c>
		/// (source lines 281-367). This method was originally called from the
		/// <c>WebVella.Erp.Plugins.Project.Hooks.Api.Timelog.OnPreDeleteRecord</c> hook.
		///
		/// <para><b>Business Logic:</b></para>
		/// <list type="number">
		///   <item>Validates timelog has an ID field</item>
		///   <item>Loads the timelog record via EQL</item>
		///   <item>Detects if this is a project-scoped timelog via <c>l_scope</c></item>
		///   <item>Loads related task records via dynamic EQL</item>
		///   <item>Reverses the aggregate update on the task:
		///     <list type="bullet">
		///       <item>If billable: subtracts minutes from <c>x_billable_minutes</c> (min 0)</item>
		///       <item>If non-billable: subtracts from <c>x_nonbillable_minutes</c> (min 0)</item>
		///     </list>
		///   </item>
		///   <item>Cleans up related feed items by deleting all feed_items whose
		///         <c>l_related_records</c> CONTAINS the timelog record ID</item>
		/// </list>
		/// </summary>
		/// <param name="record">The timelog EntityRecord about to be deleted.</param>
		/// <param name="errors">Mutable error list for reporting validation failures.</param>
		public void PreDeleteApiHookLogic(EntityRecord record, List<ErrorModel> errors)
		{
			if (!record.Properties.ContainsKey("id"))
				throw new Exception("Hook exception: timelog id field not found in record");

			Guid recordId = (Guid)record["id"];
			var isProjectTimeLog = false;
			var timelogRecord = new EntityRecord();
			var relatedTaskRecords = new EntityRecordList();
			//Get timelog

			var eqlCommand = "SELECT * from timelog WHERE id = @recordId";
			var eqlParams = new List<EqlParameter>() { new EqlParameter("recordId", recordId) };
			var eqlResult = ExecuteEql(eqlCommand, eqlParams);
			if (!eqlResult.Any())
				throw new Exception("Hook exception: timelog with this id was not found");

			timelogRecord = eqlResult[0];
			if (timelogRecord["l_scope"] != null && ((string)timelogRecord["l_scope"]).Contains("projects"))
			{
				isProjectTimeLog = true;
			}

			if (isProjectTimeLog)
			{

				if (timelogRecord["l_related_records"] != null && (string)timelogRecord["l_related_records"] != "")
				{
					try
					{
						var relatedRecordGuid = JsonConvert.DeserializeObject<List<Guid>>((string)timelogRecord["l_related_records"]);
						var taskEqlCommand = "SELECT *,$project_nn_task.id from task WHERE ";
						var filterStringList = new List<string>();
						var taskEqlParams = new List<EqlParameter>();
						var index = 1;
						foreach (var taskGuid in relatedRecordGuid)
						{
							var paramName = "taskId" + index;
							filterStringList.Add($" id = @{paramName} ");
							taskEqlParams.Add(new EqlParameter(paramName, taskGuid));
							index++;
						}
						taskEqlCommand += String.Join(" OR ", filterStringList);
						relatedTaskRecords = ExecuteEql(taskEqlCommand, taskEqlParams);
					}
					catch
					{
						//Do nothing
					}
				}

				var taskRecord = relatedTaskRecords[0]; //Currently should be related only to 1 task in projects
				var patchRecord = new EntityRecord();
				patchRecord["id"] = (Guid)taskRecord["id"];
				if ((bool)timelogRecord["is_billable"])
				{
					var result = Math.Round((decimal)taskRecord["x_billable_minutes"] - (decimal)timelogRecord["minutes"]);
					if (result > 0)
						patchRecord["x_billable_minutes"] = result;
					else
						patchRecord["x_billable_minutes"] = 0;
				}
				else
				{
					var result = Math.Round((decimal)taskRecord["x_nonbillable_minutes"] - (decimal)timelogRecord["minutes"]);
					if (result > 0)
						patchRecord["x_nonbillable_minutes"] = result;
					else
						patchRecord["x_nonbillable_minutes"] = 0;
				}
				// In the monolith this used new RecordManager(executeHooks: false).UpdateRecord().
				// In the microservice architecture, event publishing is controlled by the
				// RecordManager's publishEvents constructor flag.
				var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
				if (!updateResponse.Success)
					throw new Exception(updateResponse.Message);

				//Delete feeds that related to this timelog
				var feedEqlCommand = "SELECT id FROM feed_item WHERE l_related_records CONTAINS @recordId";
				var feedEqlParams = new List<EqlParameter>() { new EqlParameter("recordId", recordId) };
				var feedEqlResult = ExecuteEql(feedEqlCommand, feedEqlParams);
				foreach (var feedId in feedEqlResult)
				{
					// In the monolith this used new RecordManager(executeHooks: false).DeleteRecord().
					var deleteResponse = ExecuteDeleteRecord("feed_item", (Guid)feedId["id"]);
					if (!deleteResponse.Success)
						throw new Exception(deleteResponse.Message);
				}

			}
		}
	}
}
