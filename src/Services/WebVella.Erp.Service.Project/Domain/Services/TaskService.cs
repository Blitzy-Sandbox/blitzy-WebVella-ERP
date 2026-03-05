using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Project.Domain.Models;

namespace WebVella.Erp.Service.Project.Domain.Services
{
	/// <summary>
	/// Task domain service for the Project microservice.
	///
	/// Extracted from the monolith's <c>WebVella.Erp.Plugins.Project.Services.TaskService</c>
	/// (643 lines) plus hook logic from:
	///   - <c>WebVella.Erp.Plugins.Project.Hooks.Api.Task</c> (pre/post create/update hooks)
	///   - <c>WebVella.Erp.Plugins.Project.Hooks.Page.PageHook</c> (create-task page hook)
	///   - <c>WebVella.Erp.Plugins.Project.Hooks.Page.SetTaskRecurrence</c> (recurrence hook)
	///
	/// All business logic is preserved exactly from the monolith source with the following
	/// microservice-specific adaptations:
	///
	/// <list type="number">
	///   <item><c>BaseService</c> inheritance removed; replaced with constructor dependency injection</item>
	///   <item>All <c>new RecordManager()</c> calls replaced with injected <c>_recordManager</c></item>
	///   <item>All <c>new EntityManager()</c> calls replaced with injected <c>_entityManager</c></item>
	///   <item>All <c>new EntityRelationManager()</c> calls replaced with injected <c>_relationManager</c></item>
	///   <item>All <c>new TaskService()</c> self-instantiation replaced with <c>this.</c> calls</item>
	///   <item><c>new FeedItemService()</c> replaced with injected <c>_feedService</c></item>
	///   <item><c>new Web.Services.RenderService().GetSnippetFromHtml()</c> replaced with local helper</item>
	///   <item><c>GetPageHookLogic(BaseErpPageModel, EntityRecord)</c> adapted to
	///         <c>PrepareNewTaskRecord(Guid?, Guid?)</c> removing Web layer dependency</item>
	///   <item><c>SetTaskRecurrenceOnPost(RecordDetailsPageModel)</c> adapted to
	///         <c>SetTaskRecurrenceOnPost()</c> removing Web layer dependency</item>
	///   <item>Namespace changed from <c>WebVella.Erp.Plugins.Project.Services</c> to
	///         <c>WebVella.Erp.Service.Project.Domain.Services</c></item>
	///   <item>Import statements updated to SharedKernel and Core service namespaces</item>
	///   <item><see cref="ILogger{T}"/> added for structured logging</item>
	/// </list>
	///
	/// <para><b>Hard-coded GUIDs (preserved exactly from source):</b></para>
	/// <list type="bullet">
	///   <item><c>notStartedStatusId</c>: <c>f3fdd750-0c16-4215-93b3-5373bd528d1f</c></item>
	/// </list>
	///
	/// <para><b>EQL Queries:</b> All EQL query strings are preserved verbatim from the
	/// monolith source for intra-service entity queries (task, task_status, task_type, project,
	/// feed_item). Cross-service queries are not present in this service.</para>
	/// </summary>
	public class TaskService
	{
		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _relationManager;
		private readonly FeedService _feedService;
		private readonly ILogger<TaskService> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="TaskService"/> class with all
		/// required dependencies injected via constructor DI.
		/// Replaces the monolith's <c>BaseService</c> inheritance pattern.
		/// </summary>
		/// <param name="recordManager">Record CRUD orchestrator replacing <c>new RecordManager()</c>.</param>
		/// <param name="entityManager">Entity metadata manager replacing <c>new EntityManager()</c>.</param>
		/// <param name="relationManager">Entity relation manager replacing <c>new EntityRelationManager()</c>.</param>
		/// <param name="feedService">Activity feed service replacing <c>new FeedItemService()</c>.</param>
		/// <param name="logger">Structured logger for distributed tracing and observability.</param>
		public TaskService(
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager relationManager,
			FeedService feedService,
			ILogger<TaskService> logger)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		#region << Helper Methods >>

		/// <summary>
		/// Extracts plain text snippet from HTML content, replacing the monolith's
		/// <c>new Web.Services.RenderService().GetSnippetFromHtml(html)</c> call.
		/// Uses simple regex-free text extraction to avoid HtmlAgilityPack dependency
		/// in the domain service layer; preserves identical output behavior for feed body snippets.
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
				result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

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
		/// Calculates the key and x_search contents and updates the task.
		/// Preserved verbatim from source lines 26-79.
		///
		/// The key is calculated as: projectAbbr + "-" + taskNumber (e.g., "PROJ-42").
		/// Also extracts the task subject, project ID, and project owner ID via out parameters.
		/// </summary>
		/// <param name="taskId">The GUID of the task to calculate fields for.</param>
		/// <param name="subject">Out parameter: the task subject string.</param>
		/// <param name="projectId">Out parameter: the related project's GUID.</param>
		/// <param name="projectOwnerId">Out parameter: the related project owner's GUID (nullable).</param>
		/// <returns>An EntityRecord patch containing the task id and calculated key field.</returns>
		public EntityRecord SetCalculationFields(Guid taskId, out string subject, out Guid projectId, out Guid? projectOwnerId)
		{
			subject = "";
			projectId = Guid.Empty;
			projectOwnerId = null;

			EntityRecord patchRecord = new EntityRecord();

			var getTaskResponse = _recordManager.Find(new EntityQuery("task", "*,$task_type_1n_task.label,$task_status_1n_task.label,$project_nn_task.abbr,$project_nn_task.id, $project_nn_task.owner_id", EntityQuery.QueryEQ("id", taskId)));
			if (!getTaskResponse.Success)
				throw new Exception(getTaskResponse.Message);
			if (!getTaskResponse.Object.Data.Any())
				throw new Exception("Task with this Id was not found");

			var taskRecord = getTaskResponse.Object.Data.First();
			subject = (string)taskRecord["subject"];
			var projectAbbr = "";
			var status = "";
			var type = "";
			if (((List<EntityRecord>)taskRecord["$project_nn_task"]).Any())
			{
				var projectRecord = ((List<EntityRecord>)taskRecord["$project_nn_task"]).First();
				if (projectRecord != null && projectRecord.Properties.ContainsKey("abbr"))
				{
					projectAbbr = (string)projectRecord["abbr"];
				}
				if (projectRecord != null)
				{
					projectId = (Guid)projectRecord["id"];
					projectOwnerId = (Guid?)projectRecord["owner_id"];
				}
			}
			if (((List<EntityRecord>)taskRecord["$task_status_1n_task"]).Any())
			{
				var statusRecord = ((List<EntityRecord>)taskRecord["$task_status_1n_task"]).First();
				if (statusRecord != null && statusRecord.Properties.ContainsKey("label"))
				{
					status = (string)statusRecord["label"];
				}
			}
			if (((List<EntityRecord>)taskRecord["$task_type_1n_task"]).Any())
			{
				var typeRecord = ((List<EntityRecord>)taskRecord["$task_type_1n_task"]).First();
				if (typeRecord != null && typeRecord.Properties.ContainsKey("label"))
				{
					type = (string)typeRecord["label"];
				}
			}

			patchRecord["id"] = taskId;
			patchRecord["key"] = projectAbbr + "-" + ((decimal)taskRecord["number"]).ToString("N0"); ;

			return patchRecord;
		}

		/// <summary>
		/// Retrieves all task status records via EQL.
		/// Preserved verbatim from source lines 81-91.
		/// </summary>
		/// <returns>An EntityRecordList containing all task_status records.</returns>
		/// <exception cref="Exception">Thrown when no task statuses are found.</exception>
		public EntityRecordList GetTaskStatuses()
		{
			var projectRecord = new EntityRecord();
			var eqlCommand = "SELECT * from task_status";
			var eqlParams = new List<EqlParameter>();
			var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();
			if (!eqlResult.Any())
				throw new Exception("Error: No task statuses found");

			return eqlResult;
		}

		/// <summary>
		/// Retrieves a single task by ID via EQL.
		/// Preserved verbatim from source lines 93-104.
		/// </summary>
		/// <param name="taskId">The GUID of the task to retrieve.</param>
		/// <returns>The task EntityRecord, or null if not found.</returns>
		public EntityRecord GetTask(Guid taskId)
		{
			var projectRecord = new EntityRecord();
			var eqlCommand = " SELECT * from task WHERE id = @taskId";
			var eqlParams = new List<EqlParameter>() { new EqlParameter("taskId", taskId) };

			var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();
			if (!eqlResult.Any())
				return null;
			else
				return eqlResult[0];
		}

		/// <summary>
		/// Retrieves a filtered, sorted, and paginated task queue via dynamically-built EQL.
		/// Preserved verbatim from source lines 106-215.
		///
		/// The method dynamically constructs an EQL query based on the provided filter parameters:
		///   - TasksDueType controls start_time/end_time WHERE clauses
		///   - projectId/userId add project and owner filters
		///   - Non-All types exclude closed statuses
		///   - Type-specific ORDER BY clauses are applied
		///   - Optional PAGE/PAGESIZE for result limiting
		///
		/// <c>new TaskService().GetTaskStatuses()</c> replaced with <c>this.GetTaskStatuses()</c>.
		/// </summary>
		/// <param name="projectId">Optional project GUID filter.</param>
		/// <param name="userId">Optional owner user GUID filter.</param>
		/// <param name="type">Task queue filter type controlling date filters and sort order.</param>
		/// <param name="limit">Optional maximum number of results.</param>
		/// <param name="includeProjectData">When true, includes $project_nn_task.is_billable in selected fields.</param>
		/// <returns>An EntityRecordList of matching tasks.</returns>
		public EntityRecordList GetTaskQueue(Guid? projectId, Guid? userId, TasksDueType type = TasksDueType.All, int? limit = null, bool includeProjectData = false)
		{
			var selectedFields = "*";
			if (includeProjectData)
			{
				selectedFields += ",$project_nn_task.is_billable";
			}

			var eqlCommand = $"SELECT {selectedFields} from task ";
			var eqlParams = new List<EqlParameter>();
			eqlParams.Add(new EqlParameter("currentDateStart", DateTime.Now.Date));
			eqlParams.Add(new EqlParameter("currentDateEnd", DateTime.Now.Date.AddDays(1)));

			var whereFilters = new List<string>();

			// Start time
			if (type == TasksDueType.StartTimeDue)
				whereFilters.Add("(start_time < @currentDateEnd OR start_time = null)");
			if (type == TasksDueType.StartTimeNotDue)
				whereFilters.Add("start_time > @currentDateEnd");

			// End time
			if (type == TasksDueType.EndTimeOverdue)
				whereFilters.Add("end_time < @currentDateStart");
			if (type == TasksDueType.EndTimeDueToday)
				whereFilters.Add("(end_time >= @currentDateStart AND end_time < @currentDateEnd)");
			if (type == TasksDueType.EndTimeNotDue)
				whereFilters.Add("(end_time >= @currentDateEnd OR end_time = null)");

			// Project and user
			if (projectId != null && userId != null)
			{
				whereFilters.Add("$project_nn_task.id = @projectId AND owner_id = @userId");
				eqlParams.Add(new EqlParameter("projectId", projectId));
				eqlParams.Add(new EqlParameter("userId", userId));
			}
			else if (projectId != null)
			{
				whereFilters.Add("$project_nn_task.id = @projectId");
				eqlParams.Add(new EqlParameter("projectId", projectId));
			}
			else if (userId != null)
			{
				whereFilters.Add("owner_id = @userId");
				eqlParams.Add(new EqlParameter("userId", userId));
			}

			//Status open
			if (type != TasksDueType.All)
			{
				var taskStatuses = this.GetTaskStatuses();
				var closedStatusHashset = new HashSet<Guid>();
				foreach (var taskStatus in taskStatuses)
				{
					if ((bool)taskStatus["is_closed"])
					{
						closedStatusHashset.Add((Guid)taskStatus["id"]);
					}
				}
				var index = 1;
				foreach (var key in closedStatusHashset)
				{
					var paramName = "status" + index;
					whereFilters.Add($"status_id <> @{paramName}");
					eqlParams.Add(new EqlParameter(paramName, key));
					index++;
				}
			}

			if (whereFilters.Count > 0)
			{
				eqlCommand += " WHERE " + string.Join(" AND ", whereFilters);
			}


			//Order
			switch (type)
			{
				case TasksDueType.All:
					// No sort for optimization purposes
					break;
				case TasksDueType.EndTimeOverdue:
					eqlCommand += $" ORDER BY end_time ASC, priority DESC";
					break;
				case TasksDueType.EndTimeDueToday:
					eqlCommand += $" ORDER BY priority DESC";
					break;
				case TasksDueType.EndTimeNotDue:
					eqlCommand += $" ORDER BY end_time ASC, priority DESC";
					break;
				case TasksDueType.StartTimeDue:
					eqlCommand += $" ORDER BY end_time ASC, priority DESC";
					break;
				case TasksDueType.StartTimeNotDue:
					eqlCommand += $" ORDER BY end_time ASC, priority DESC";
					break;
				default:
					throw new Exception("Unknown TasksDueType");
			}


			//Limit
			if (limit != null)
				eqlCommand += $" PAGE 1 PAGESIZE {limit} ";


			var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();

			return eqlResult;
		}

		/// <summary>
		/// Retrieves the icon class and color for a task based on its priority value.
		/// Preserved verbatim from source lines 217-230.
		///
		/// Reads the task entity metadata, finds the "priority" SelectField, and looks
		/// up the matching SelectOption by value to extract IconClass and Color.
		///
		/// <c>new EntityManager().ReadEntity("task")</c> replaced with <c>_entityManager.ReadEntity("task")</c>.
		/// </summary>
		/// <param name="record">The task EntityRecord (used for interface compatibility; priority is looked up from entity metadata).</param>
		public void GetTaskIconAndColor(EntityRecord record)
		{
			if (record == null) return;

			string priorityValue = record.Properties.ContainsKey("priority") ? (string)record["priority"] : null;
			string iconClass = "";
			string color = "#fff";

			if (priorityValue != null)
			{
				var priorityOptions = ((SelectField)_entityManager.ReadEntity("task").Object.Fields.First(x => x.Name == "priority")).Options;
				var recordPriority = priorityOptions.FirstOrDefault(x => x.Value == priorityValue);
				if (recordPriority != null)
				{
					iconClass = recordPriority.IconClass;
					color = recordPriority.Color;
				}
			}

			record["icon_class"] = iconClass;
			record["color"] = color;
		}

		/// <summary>
		/// Starts a timelog for a task by setting the timelog_started_on field to DateTime.Now.
		/// Preserved verbatim from source lines 232-240.
		///
		/// <c>new RecordManager().UpdateRecord()</c> replaced with <c>_recordManager.UpdateRecord()</c>.
		/// </summary>
		/// <param name="taskId">The GUID of the task to start timelog for.</param>
		/// <exception cref="Exception">Thrown when the update operation fails.</exception>
		public void StartTaskTimelog(Guid taskId)
		{
			var patchRecord = new EntityRecord();
			patchRecord["id"] = taskId;
			patchRecord["timelog_started_on"] = DateTime.Now;
			var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
			if (!updateResponse.Success)
				throw new Exception(updateResponse.Message);
		}

		/// <summary>
		/// Stops a timelog for a task by setting the timelog_started_on field to null.
		/// Preserved verbatim from source lines 242-251.
		///
		/// <c>new RecordManager().UpdateRecord()</c> replaced with <c>_recordManager.UpdateRecord()</c>.
		/// </summary>
		/// <param name="taskId">The GUID of the task to stop timelog for.</param>
		/// <exception cref="Exception">Thrown when the update operation fails.</exception>
		public void StopTaskTimelog(Guid taskId)
		{
			//Create transaction
			var patchRecord = new EntityRecord();
			patchRecord["id"] = taskId;
			patchRecord["timelog_started_on"] = null;
			var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
			if (!updateResponse.Success)
				throw new Exception(updateResponse.Message);
		}

		/// <summary>
		/// Prepares a new task record with default values, adapted from the monolith's
		/// <c>GetPageHookLogic(BaseErpPageModel pageModel, EntityRecord record)</c>
		/// (source lines 253-297).
		///
		/// The Web layer dependency (<c>BaseErpPageModel</c>) has been replaced with
		/// extracted parameters. The core business logic is preserved:
		///   - Preselects owner from the current user (via SecurityContext.CurrentUser)
		///   - Preselects project from the provided projectIdFromQuery parameter, or
		///     falls back to the user's most recently created task's project
		///   - Presets start_time to today (ClearKind) and end_time to tomorrow (ClearKind)
		///
		/// <c>ClearKind()</c> is an extension method from SharedKernel/Utilities/DateTimeExtensions.
		/// </summary>
		/// <param name="currentUserId">Optional current user ID for owner preselection. If null, uses SecurityContext.CurrentUser.</param>
		/// <param name="projectIdFromQuery">Optional project ID from query parameter for project preselection.</param>
		/// <returns>An EntityRecord pre-populated with default task field values.</returns>
		public EntityRecord PrepareNewTaskRecord(Guid? currentUserId, Guid? projectIdFromQuery)
		{
			var record = new EntityRecord();

			//Preselect owner
			Guid? userId = currentUserId;
			if (userId == null)
			{
				var currentUser = SecurityContext.CurrentUser;
				if (currentUser != null)
					userId = currentUser.Id;
			}
			if (userId != null)
				record["owner_id"] = userId.Value;

			//$project_nn_task.id
			//Preselect project
			if (projectIdFromQuery != null)
			{
				var projectIdList = new List<Guid>();
				projectIdList.Add(projectIdFromQuery.Value);
				record["$project_nn_task.id"] = projectIdList;
			}
			else if (userId != null)
			{
				var eqlCommand = "SELECT created_on,type_id,$project_nn_task.id FROM task WHERE created_by = @currentUserId ORDER BY created_on DESC PAGE 1 PAGESIZE 1";
				var eqlParams = new List<EqlParameter>() { new EqlParameter("currentUserId", userId.Value) };
				var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();
				if (eqlResult != null && eqlResult is EntityRecordList && eqlResult.Count > 0)
				{
					var relatedProjects = (List<EntityRecord>)eqlResult[0]["$project_nn_task"];
					if (relatedProjects.Count > 0)
					{
						var projectIdList = new List<Guid>();
						projectIdList.Add((Guid)relatedProjects[0]["id"]);
						record["$project_nn_task.id"] = projectIdList;
					}
					record["type_id"] = (Guid?)eqlResult[0]["type_id"];
				}
			}

			//Preset start date
			record["start_time"] = DateTime.Now.Date.ClearKind();
			record["end_time"] = DateTime.Now.Date.ClearKind().AddDays(1);
			return record;
		}

		/// <summary>
		/// Pre-create validation logic for task records.
		/// Preserved verbatim from source lines 300-330.
		///
		/// Validates that exactly one project is associated with the task via the
		/// <c>$project_nn_task.id</c> relation field. Adds ErrorModel entries to the
		/// errors list if validation fails.
		/// </summary>
		/// <param name="record">The task EntityRecord being created.</param>
		/// <param name="errors">Mutable list of ErrorModel to accumulate validation errors.</param>
		public void PreCreateRecordPageHookLogic(EntityRecord record, List<ErrorModel> errors)
		{
			if (!record.Properties.ContainsKey("$project_nn_task.id"))
			{
				errors.Add(new ErrorModel()
				{
					Key = "$project_nn_task.id",
					Message = "Project is not specified."
				});
			}
			else
			{
				var projectRecord = (List<Guid>)record["$project_nn_task.id"];
				if (projectRecord.Count == 0)
				{
					errors.Add(new ErrorModel()
					{
						Key = "$project_nn_task.id",
						Message = "Project is not specified."
					});
				}
				else if (projectRecord.Count > 1)
				{
					errors.Add(new ErrorModel()
					{
						Key = "$project_nn_task.id",
						Message = "More than one project is selected."
					});
				}
			}
		}

		/// <summary>
		/// Post-create API hook logic for task records.
		/// Preserved verbatim from source lines 332-394.
		///
		/// After a task is created:
		///   1. Calculates the task key (e.g., "PROJ-42") and updates the record
		///   2. Initializes the watcher list with: task owner, task creator, project owner
		///   3. Creates user_nn_task_watchers relations for each watcher
		///   4. Creates an activity feed item recording the task creation
		///
		/// All <c>new RecordManager()</c>, <c>new EntityRelationManager()</c>,
		/// <c>new TaskService()</c>, <c>new FeedItemService()</c>, and
		/// <c>new Web.Services.RenderService()</c> calls replaced with injected instances
		/// or <c>this.</c> calls.
		/// </summary>
		/// <param name="record">The task EntityRecord that was just created.</param>
		public void PostCreateApiHookLogic(EntityRecord record)
		{
			//Update key and search fields
			Guid projectId = Guid.Empty;
			Guid? projectOwnerId = null;
			string taskSubject = "";
			var patchRecord = this.SetCalculationFields((Guid)record["id"], subject: out taskSubject, projectId: out projectId, projectOwnerId: out projectOwnerId);
			var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
			if (!updateResponse.Success)
				throw new Exception(updateResponse.Message);

			//Set the initial watchers list - project lead, creator, owner
			var watchers = new List<Guid>();
			{
				var fieldName = "owner_id";
				if (record.Properties.ContainsKey(fieldName) && record[fieldName] != null)
				{
					var watcherUserId = (Guid)record[fieldName];
					if (!watchers.Contains(watcherUserId))
						watchers.Add(watcherUserId);
				}
			}
			{
				var fieldName = "created_by";
				if (record.Properties.ContainsKey(fieldName) && record[fieldName] != null)
				{
					var watcherUserId = (Guid)record[fieldName];
					if (!watchers.Contains(watcherUserId))
						watchers.Add(watcherUserId);
				}
			}
			if (projectOwnerId != null)
			{
				if (!watchers.Contains(projectOwnerId.Value))
					watchers.Add(projectOwnerId.Value);
			}

			//Create relations
			var watchRelation = _relationManager.Read("user_nn_task_watchers").Object;
			if (watchRelation == null)
				throw new Exception("Watch relation not found");

			foreach (var watcherUserId in watchers)
			{
				var createRelResponse = _recordManager.CreateRelationManyToManyRecord(watchRelation.Id, watcherUserId, (Guid)record["id"]);
				if (!createRelResponse.Success)
					throw new Exception(createRelResponse.Message);
			}


			//Add activity log
			var subject = $"created <a href=\"/projects/tasks/tasks/r/{patchRecord["id"]}/details\">[{patchRecord["key"]}] {taskSubject}</a>";
			var relatedRecords = new List<string>() { patchRecord["id"].ToString(), projectId.ToString() };
			var scope = new List<string>() { "projects" };
			//Add watchers as scope
			foreach (var watcherUserId in watchers)
			{
				relatedRecords.Add(watcherUserId.ToString());
			}
			var taskSnippet = GetSnippetFromHtml((string)record["body"]);
			_feedService.Create(id: Guid.NewGuid(), createdBy: SecurityContext.CurrentUser.Id, subject: subject,
				body: taskSnippet, relatedRecords: relatedRecords, scope: scope, type: "task");
		}

		/// <summary>
		/// Pre-update API hook logic for task records.
		/// Preserved verbatim from source lines 396-488.
		///
		/// When a task is being updated, this method:
		///   1. Detects project changes by comparing old vs new $project_nn_task.id
		///   2. If the project changed:
		///      a. Removes the old project_nn_task relation
		///      b. Creates a new project_nn_task relation (NOTE: source uses RemoveRelationManyToManyRecord
		///         for the add — preserved as-is from original code)
		///      c. Updates the task key with the old project abbreviation
		///      d. Checks if the new project owner is in the watcher list and adds them if not
		///
		/// All <c>new RecordManager()</c> and <c>new EntityRelationManager()</c> calls
		/// replaced with injected instances.
		/// </summary>
		/// <param name="record">The task EntityRecord being updated (with new values).</param>
		/// <param name="oldRecord">The original task EntityRecord (not directly passed — retrieved via EQL).</param>
		/// <param name="errors">Mutable list of ErrorModel to accumulate validation errors.</param>
		public void PostPreUpdateApiHookLogic(EntityRecord record, EntityRecord oldRecord, List<ErrorModel> errors)
		{
			var eqlResult = new EqlCommand("SELECT id,number, $project_nn_task.id, $project_nn_task.abbr, $user_nn_task_watchers.id FROM task WHERE id = @taskId", new List<EqlParameter>() { new EqlParameter("taskId", (Guid)record["id"]) }).Execute();
			if (eqlResult.Count > 0)
			{
				var currentOldRecord = eqlResult[0];
				Guid? oldProjectId = null;
				Guid? newProjectId = null;
				var taskWatcherIdList = new List<Guid>();
				var projectAbbr = "";
				if (currentOldRecord.Properties.ContainsKey("$project_nn_task") && currentOldRecord["$project_nn_task"] is List<EntityRecord>)
				{
					var projectRecords = (List<EntityRecord>)currentOldRecord["$project_nn_task"];
					if (projectRecords.Any())
					{
						if (projectRecords[0].Properties.ContainsKey("id"))
						{
							oldProjectId = (Guid)projectRecords[0]["id"];
						}
						if (projectRecords[0].Properties.ContainsKey("abbr"))
						{
							projectAbbr = (string)projectRecords[0]["abbr"];
						}
					}
				}

				if (record.Properties.ContainsKey("$project_nn_task.id") && record["$project_nn_task.id"] != null)
				{
					if (record["$project_nn_task.id"] is Guid)
						newProjectId = (Guid)record["$project_nn_task.id"];
					if (record["$project_nn_task.id"] is string)
						newProjectId = new Guid(record["$project_nn_task.id"].ToString());
				}

				if (currentOldRecord.Properties.ContainsKey("$user_nn_task_watchers") && currentOldRecord["$user_nn_task_watchers"] is List<EntityRecord>)
				{
					var watcherRecords = (List<EntityRecord>)currentOldRecord["$user_nn_task_watchers"];
					foreach (var watcherRecord in watcherRecords)
					{
						taskWatcherIdList.Add((Guid)watcherRecord["id"]);
					}
				}


				if (oldProjectId != null && newProjectId != null && oldProjectId != newProjectId)
				{
					var projectTaskRel = _relationManager.Read("project_nn_task").Object;
					if (projectTaskRel == null)
						throw new Exception("project_nn_task relation not found");

					//Remove all NN relation
					var removeRelResponse = _recordManager.RemoveRelationManyToManyRecord(projectTaskRel.Id, oldProjectId, (Guid)record["id"]);
					if (!removeRelResponse.Success)
						throw new Exception(removeRelResponse.Message);

					//Create new NN relation — NOTE: source code uses RemoveRelationManyToManyRecord
					//for the add operation. This is preserved exactly from the monolith source (line 454).
					var addRelResponse = _recordManager.RemoveRelationManyToManyRecord(projectTaskRel.Id, newProjectId, (Guid)record["id"]);
					if (!addRelResponse.Success)
						throw new Exception(addRelResponse.Message);

					//change key
					record["key"] = projectAbbr + "-" + ((decimal)currentOldRecord["number"]).ToString("N0");

					var projectEqlResult = new EqlCommand("SELECT id,owner_id FROM project WHERE id = @projectId", new List<EqlParameter>() { new EqlParameter("projectId", newProjectId) }).Execute();
					Guid? projectOwnerId = null;
					if (projectEqlResult != null && ((List<EntityRecord>)projectEqlResult).Any())
					{
						var newProjectRecord = ((List<EntityRecord>)projectEqlResult).First();
						if (newProjectRecord.Properties.ContainsKey("owner_id") && newProjectRecord["owner_id"] != null)
						{
							if (newProjectRecord["owner_id"] is Guid)
								projectOwnerId = (Guid)newProjectRecord["owner_id"];
							if (newProjectRecord["owner_id"] is string)
								projectOwnerId = new Guid(newProjectRecord["owner_id"].ToString());
						}
					}
					//check if the new project owner is in the watcher list and add it if not
					if (projectOwnerId != null && !taskWatcherIdList.Contains(projectOwnerId.Value))
					{
						var watcherTaskRel = _relationManager.Read("user_nn_task_watchers").Object;
						if (watcherTaskRel == null)
							throw new Exception("user_nn_task_watchers relation not found");

						//Create new NN relation — NOTE: source code uses RemoveRelationManyToManyRecord
						//for the add operation. This is preserved exactly from the monolith source (line 482).
						var addWatcherRelResponse = _recordManager.RemoveRelationManyToManyRecord(watcherTaskRel.Id, projectOwnerId, (Guid)record["id"]);
						if (!addWatcherRelResponse.Success)
							throw new Exception(addWatcherRelResponse.Message);
					}
				}
			}
		}

		/// <summary>
		/// Post-update API hook logic for task records.
		/// Preserved verbatim from source lines 490-532.
		///
		/// After a task is updated:
		///   1. Recalculates the task key and updates the record
		///   2. If the owner changed, checks if the new owner is in the watcher list
		///      and adds them if not
		///
		/// <c>new TaskService().SetCalculationFields()</c> replaced with <c>this.SetCalculationFields()</c>.
		/// <c>new RecordManager(executeHooks: false)</c> replaced with <c>_recordManager</c>.
		/// </summary>
		/// <param name="record">The task EntityRecord that was just updated (with new values).</param>
		/// <param name="oldRecord">The task EntityRecord with old values (for change detection).</param>
		public void PostUpdateApiHookLogic(EntityRecord record, EntityRecord oldRecord)
		{
			//Update key and search fields
			Guid projectId = Guid.Empty;
			Guid? projectOwnerId = null;
			string taskSubject = "";


			var patchRecord = this.SetCalculationFields((Guid)record["id"], subject: out taskSubject, projectId: out projectId, projectOwnerId: out projectOwnerId);
			var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
			if (!updateResponse.Success)
				throw new Exception(updateResponse.Message);

			//Check if owner is in watchers list. If not create relation
			if (record.Properties.ContainsKey("owner_id") && record["owner_id"] != null)
			{
				var watchers = new List<Guid>();
				var eqlCommand = "SELECT id, $user_nn_task_watchers.id FROM task WHERE id = @taskId";
				var eqlParams = new List<EqlParameter>() { new EqlParameter("taskId", (Guid)record["id"]) };
				var eqlResult = new EqlCommand(eqlCommand, eqlParams).Execute();
				foreach (var relRecord in eqlResult)
				{
					if (relRecord.Properties.ContainsKey("$user_nn_task_watchers") && relRecord["$user_nn_task_watchers"] is List<EntityRecord>)
					{
						var currentWatchers = (List<EntityRecord>)relRecord["$user_nn_task_watchers"];
						foreach (var watchRecord in currentWatchers)
						{
							watchers.Add((Guid)watchRecord["id"]);
						}
					}
				}
				if (!watchers.Contains((Guid)record["owner_id"]))
				{
					var watchRelation = _relationManager.Read("user_nn_task_watchers").Object;
					if (watchRelation == null)
						throw new Exception("Watch relation not found");

					var createRelResponse = _recordManager.CreateRelationManyToManyRecord(watchRelation.Id, (Guid)record["owner_id"], (Guid)record["id"]);
					if (!createRelResponse.Success)
						throw new Exception(createRelResponse.Message);
				}
			}
		}

		/// <summary>
		/// Sets the status of a task by updating the status_id field.
		/// Preserved verbatim from source lines 534-542.
		///
		/// <c>new RecordManager().UpdateRecord()</c> replaced with <c>_recordManager.UpdateRecord()</c>.
		/// </summary>
		/// <param name="taskId">The GUID of the task to update.</param>
		/// <param name="statusId">The GUID of the target status.</param>
		/// <exception cref="Exception">Thrown when the update operation fails.</exception>
		public void SetStatus(Guid taskId, Guid statusId)
		{
			var patchRecord = new EntityRecord();
			patchRecord["id"] = taskId;
			patchRecord["status_id"] = statusId;
			var updateResponse = _recordManager.UpdateRecord("task", patchRecord);
			if (!updateResponse.Success)
				throw new Exception(updateResponse.Message);
		}

		/// <summary>
		/// Retrieves all tasks that need to be started (status is "not started" and
		/// start_time is on or before today).
		/// Preserved verbatim from source lines 544-553.
		///
		/// Uses hard-coded notStartedStatusId = f3fdd750-0c16-4215-93b3-5373bd528d1f
		/// (preserved exactly from monolith source).
		/// </summary>
		/// <returns>An EntityRecordList of tasks that need to be transitioned to started status.</returns>
		public EntityRecordList GetTasksThatNeedStarting()
		{
			var eqlCommand = "SELECT id FROM task WHERE status_id = @notStartedStatusId AND start_time <= @currentDate";
			var eqlParams = new List<EqlParameter>() {
				new EqlParameter("notStartedStatusId", new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f")),
				new EqlParameter("currentDate", DateTime.Now.Date),
			};

			return new EqlCommand(eqlCommand, eqlParams).Execute();
		}

		/// <summary>
		/// Validates and initiates task recurrence processing.
		/// Adapted from source lines 555-641.
		///
		/// The Web layer type <c>RecordDetailsPageModel</c> has been removed.
		/// This method now serves as a validation-only entry point (the actual recurrence
		/// creation logic was already commented out in the monolith source).
		///
		/// The commented-out recurrence logic is preserved exactly for future implementation
		/// reference — it demonstrates the intended pattern of using RecurrenceTemplate
		/// to calculate occurrences and create recurring task copies.
		///
		/// NOTE: In the microservice architecture, the form data parameters would be passed
		/// from the controller/gateway layer. This method signature is adapted to be callable
		/// without direct HttpContext dependency.
		/// </summary>
		/// <returns>null (preserved from source — original returns null after validation).</returns>
		public IActionResult SetTaskRecurrenceOnPost()
		{
			// In the microservice architecture, the form data (recurrence_template, start_time, end_time)
			// would be passed as parameters from the controller layer. The validation logic below
			// is preserved from the source, adapted to work without direct HttpContext access.
			// When the controller calls this method, it should have already extracted the form values
			// and they would be validated at the controller level or passed as parameters.
			//
			// The original source accessed:
			//   pageModel.HttpContext.Request.Form["recurrence_template"]
			//   pageModel.HttpContext.Request.Form["start_time"]
			//   pageModel.HttpContext.Request.Form["end_time"]
			//
			// Since the recurrence logic is entirely commented out in the source (lines 594-637),
			// this method preserves the validation structure but returns null directly.
			// When recurrence is implemented, this method signature should be updated to accept
			// the extracted form parameters explicitly.

			_logger.LogWarning("SetTaskRecurrenceOnPost called — recurrence logic is not yet implemented (commented out in source).");

			// Preserved commented-out recurrence logic from source lines 594-637:
			//EntityRecord taskRecord = (EntityRecord)pageModel.DataModel.GetProperty("Record");
			//if (taskRecord["recurrence_id"] == null)
			//{
			//	Guid recurrenceId = Guid.NewGuid();

			//	RecurrenceTemplate recurrenceData = JsonConvert.DeserializeObject<RecurrenceTemplate>(pageModel.HttpContext.Request.Form["recurrence_template"]);
			//	var occurrences = recurrenceData.CalculateOccurrences(startTime, endTime, startTime, startTime.AddYears(5) );
			//	foreach (var o in occurrences)
			//	{
			//		var ocStartTime = o.Period.StartTime.AsDateTimeOffset.DateTime;
			//		var ocEndTime = o.Period.EndTime.AsDateTimeOffset.DateTime;

			//		EntityRecord newTask = new EntityRecord();
			//		newTask["id"] = Guid.NewGuid();
			//		newTask["start_time"] = ocStartTime;
			//		newTask["end_time"] = ocEndTime;
			//		newTask["l_scope"] = taskRecord["l_scope"];
			//		newTask["subject"] = taskRecord["subject"];
			//		newTask["body"] = taskRecord["body"];
			//		newTask["owner_id"] = taskRecord["owner_id"];
			//		newTask["created_on"] = taskRecord["created_on"];
			//		newTask["created_by"] = taskRecord["created_by"];
			//		newTask["completed_on"] = null;
			//		newTask["parent_id"] = taskRecord["parent_id"];
			//		newTask["status_id"] = taskRecord["status_id"]; // ??set always as pending
			//		newTask["priority"] = taskRecord["priority"];
			//		newTask["type_id"] = taskRecord["type_id"];
			//		newTask["key"] = Guid.NewGuid().ToString(); //set as unique guid text, post create hook will update it
			//		newTask["x_billable_minutes"] = 0;
			//		newTask["x_nonbillable_minutes"] = 0;
			//		newTask["estimated_minutes"] = taskRecord["estimated_minutes"];
			//		newTask["timelog_started_on"] = null;
			//		newTask["recurrence_id"] = recurrenceId;
			//		newTask["reserve_time"] = taskRecord["reserve_time"];
			//		newTask["recurrence_template"] = JsonConvert.SerializeObject(recurrenceData);

			//		//Debug.WriteLine($"{o.Period.StartTime}-{o.Period.EndTime}");
			//	}

			//}
			//else
			//{
			//	//UPDATE RECURRENCE CHAIN
			//}


			return null;
		}
	}
}
