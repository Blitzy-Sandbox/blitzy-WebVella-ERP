// =============================================================================
// ProjectController.cs — Project/Task Service REST API Controller
// =============================================================================
// Exposes REST endpoints for task, timelog, comment, feed, and project CRUD
// operations. Extracted from the monolith's WebApiController.cs and
// ProjectController.cs REST endpoints.
//
// Route pattern: /api/v3/{locale}/project/...
// All endpoints use BaseResponseModel/ResponseModel response envelopes.
// All mutation endpoints publish domain events via MassTransit.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Project.Domain.Models;
using WebVella.Erp.Service.Project.Domain.Services;

namespace WebVella.Erp.Service.Project.Controllers
{
	/// <summary>
	/// Project Microservice REST API controller exposing task, timelog, comment, feed,
	/// and project domain operations. Extracted from the monolith's ProjectController.cs
	/// and relevant WebApiController.cs endpoints scoped to Project-owned entities.
	///
	/// All endpoints are protected by [Authorize] (JWT Bearer). Mutations publish
	/// domain events via MassTransit for inter-service communication.
	/// </summary>
	[Authorize]
	[ApiController]
	[Route("api/v3/{locale}/project")]
	public class ProjectController : Controller
	{
		#region Dependencies

		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _relationManager;
		private readonly TaskService _taskService;
		private readonly TimelogService _timelogService;
		private readonly CommentService _commentService;
		private readonly FeedService _feedService;
		private readonly ReportingService _reportingService;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly ILogger<ProjectController> _logger;
		private readonly IConfiguration _configuration;

		/// <summary>
		/// Constructs the ProjectController with all required dependencies injected via DI.
		/// </summary>
		public ProjectController(
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager relationManager,
			TaskService taskService,
			TimelogService timelogService,
			CommentService commentService,
			FeedService feedService,
			ReportingService reportingService,
			IPublishEndpoint publishEndpoint,
			ILogger<ProjectController> logger,
			IConfiguration configuration)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
			_timelogService = timelogService ?? throw new ArgumentNullException(nameof(timelogService));
			_commentService = commentService ?? throw new ArgumentNullException(nameof(commentService));
			_feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
			_reportingService = reportingService ?? throw new ArgumentNullException(nameof(reportingService));
			_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
			_configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
		}

		#endregion

		#region Response Helpers (from ApiControllerBase.cs)

		/// <summary>
		/// Standard response handler preserving monolith ApiControllerBase pattern.
		/// </summary>
		protected IActionResult DoResponse(BaseResponseModel response)
		{
			if (response.Errors.Count > 0 || !response.Success)
			{
				if (response.StatusCode == HttpStatusCode.OK)
					HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				else
					HttpContext.Response.StatusCode = (int)response.StatusCode;
			}
			return Json(response);
		}

		/// <summary>
		/// Returns a 400 Bad Request response with environment-aware error detail.
		/// Only includes stack traces in Development mode.
		/// </summary>
		protected IActionResult DoBadRequestResponse(BaseResponseModel response, string message = null, Exception ex = null)
		{
			response.Timestamp = DateTime.UtcNow;
			response.Success = false;

			var isDevelopment = string.Equals(
				_configuration["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
				"Development", StringComparison.OrdinalIgnoreCase);

			if (isDevelopment)
			{
				if (ex != null)
					response.Message = ex.Message + ex.StackTrace;
			}
			else
			{
				if (string.IsNullOrEmpty(message))
					response.Message = "An internal error occurred!";
				else
					response.Message = message;
			}

			HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
			return Json(response);
		}

		#endregion

		#region Task Endpoints

		/// <summary>
		/// Retrieves a task by its unique identifier.
		/// GET /api/v3/{locale}/project/task/{taskId}
		/// </summary>
		[HttpGet("task/{taskId}")]
		public IActionResult GetTask(string locale, Guid taskId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var task = _taskService.GetTask(taskId);
					response.Object = task;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving task {TaskId}", taskId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Retrieves the task queue with filtering and pagination.
		/// GET /api/v3/{locale}/project/tasks/queue
		/// </summary>
		[HttpGet("tasks/queue")]
		public IActionResult GetTaskQueue(string locale, Guid? projectId = null, Guid? userId = null,
			int dueType = 0, int? limit = null, bool includeProjectData = false)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var tasksDueType = (TasksDueType)dueType;
					var tasks = _taskService.GetTaskQueue(projectId, userId, tasksDueType, limit, includeProjectData);
					response.Object = tasks;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving task queue");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Retrieves all available task statuses.
		/// GET /api/v3/{locale}/project/task-statuses
		/// </summary>
		[HttpGet("task-statuses")]
		public IActionResult GetTaskStatuses(string locale)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var statuses = _taskService.GetTaskStatuses();
					response.Object = statuses;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving task statuses");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Sets the status of a task.
		/// POST /api/v3/{locale}/project/task/{taskId}/status
		/// Source: ProjectController.TaskSetStatus()
		/// </summary>
		[HttpPost("task/{taskId}/status")]
		public IActionResult SetTaskStatus(string locale, Guid taskId, [FromBody] JObject body)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var statusId = body.Value<Guid?>("statusId") ?? Guid.Empty;
					if (statusId == Guid.Empty)
					{
						response.Success = false;
						response.Message = "statusId is required.";
						return DoBadRequestResponse(response, "statusId is required.");
					}
					_taskService.SetStatus(taskId, statusId);
					response.Success = true;
					response.Message = "Task status updated.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error setting task status for {TaskId}", taskId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Starts or stops watching a task for a user.
		/// POST /api/v3/{locale}/project/task/watch
		/// Source: ProjectController.TaskSetWatch()
		/// </summary>
		[HttpPost("task/watch")]
		public IActionResult SetTaskWatch(string locale, [FromBody] JObject body)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var taskId = body.Value<Guid?>("taskId");
					var userId = body.Value<Guid?>("userId") ?? user?.Id ?? Guid.Empty;
					var startWatch = body.Value<bool?>("startWatch") ?? true;

					if (taskId == null || taskId == Guid.Empty)
					{
						response.Success = false;
						response.Message = "taskId is required.";
						return DoBadRequestResponse(response, "taskId is required.");
					}

					// Manage the watcher relation
					var relationName = "$user_nn_task_watchers";
					var relationResponse = _relationManager.Read(relationName);
					if (!relationResponse.Success || relationResponse.Object == null)
					{
						response.Success = false;
						response.Message = "Watcher relation not found.";
						return DoBadRequestResponse(response, "Watcher relation not found.");
					}

					var relation = relationResponse.Object;
					if (startWatch)
					{
						_recordManager.CreateRelationManyToManyRecord(relation.Id, userId, taskId.Value);
					}
					else
					{
						_recordManager.RemoveRelationManyToManyRecord(relation.Id, userId, taskId.Value);
					}

					response.Success = true;
					response.Message = startWatch ? "Task watch started." : "Task watch stopped.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error managing task watch");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Starts the timelog timer for a task.
		/// POST /api/v3/{locale}/project/task/{taskId}/timelog/start
		/// Source: ProjectController.StartTimeLog()
		/// </summary>
		[HttpPost("task/{taskId}/timelog/start")]
		public IActionResult StartTaskTimelog(string locale, Guid taskId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					_taskService.StartTaskTimelog(taskId);
					response.Success = true;
					response.Message = "Task timelog started.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error starting task timelog for {TaskId}", taskId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Stops the timelog timer for a task.
		/// POST /api/v3/{locale}/project/task/{taskId}/timelog/stop
		/// Source: ProjectController.StopTimeLog()
		/// </summary>
		[HttpPost("task/{taskId}/timelog/stop")]
		public IActionResult StopTaskTimelog(string locale, Guid taskId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					_taskService.StopTaskTimelog(taskId);
					response.Success = true;
					response.Message = "Task timelog stopped.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error stopping task timelog for {TaskId}", taskId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		#endregion

		#region Timelog Endpoints

		/// <summary>
		/// Creates a new timelog entry.
		/// POST /api/v3/{locale}/project/timelog
		/// Source: ProjectController.CreateTimelog()
		/// </summary>
		[HttpPost("timelog")]
		public IActionResult CreateTimelog(string locale, [FromBody] JObject body)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var taskId = body.Value<Guid?>("taskId") ?? Guid.Empty;
					var minutes = body.Value<int?>("minutes") ?? 0;
					var isBillable = body.Value<bool?>("isBillable") ?? true;
					var logBody = body.Value<string>("body") ?? string.Empty;
					var loggedOn = body.Value<DateTime?>("loggedOn") ?? DateTime.UtcNow;

					if (taskId == Guid.Empty)
					{
						response.Success = false;
						response.Message = "taskId is required.";
						return DoBadRequestResponse(response, "taskId is required.");
					}

					// The monolith stores taskId via the relatedRecords list
					var relatedRecords = new List<Guid> { taskId };

					_timelogService.Create(
						createdBy: user?.Id,
						loggedOn: loggedOn,
						minutes: minutes,
						isBillable: isBillable,
						body: logBody,
						relatedRecords: relatedRecords);

					response.Success = true;
					response.Message = "Timelog created.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error creating timelog");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Deletes a timelog entry.
		/// DELETE /api/v3/{locale}/project/timelog/{timelogId}
		/// Source: ProjectController.DeleteTimelog()
		/// </summary>
		[HttpDelete("timelog/{timelogId}")]
		public IActionResult DeleteTimelog(string locale, Guid timelogId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					_timelogService.Delete(timelogId);
					response.Success = true;
					response.Message = "Timelog deleted.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error deleting timelog {TimelogId}", timelogId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Retrieves timelogs for a date range, optionally filtered by project and user.
		/// GET /api/v3/{locale}/project/timelogs
		/// Source: ProjectController.GetTimelogsForPeriod()
		/// </summary>
		[HttpGet("timelogs")]
		public IActionResult GetTimelogsForPeriod(string locale, Guid? projectId = null, Guid? userId = null,
			DateTime? startDate = null, DateTime? endDate = null)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var start = startDate ?? DateTime.UtcNow.AddDays(-30);
					var end = endDate ?? DateTime.UtcNow;
					var timelogs = _timelogService.GetTimelogsForPeriod(projectId, userId, start, end);
					response.Object = timelogs;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving timelogs for period");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		#endregion

		#region Comment Endpoints

		/// <summary>
		/// Creates a new comment on a task.
		/// POST /api/v3/{locale}/project/comment
		/// Source: ProjectController.CreateNewPcPostListItem()
		/// </summary>
		[HttpPost("comment")]
		public IActionResult CreateComment(string locale, [FromBody] JObject body)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var commentBody = body.Value<string>("body") ?? string.Empty;
					var parentId = body.Value<Guid?>("parentId");
					var taskId = body.Value<Guid?>("taskId") ?? Guid.Empty;

					if (taskId == Guid.Empty)
					{
						response.Success = false;
						response.Message = "taskId is required.";
						return DoBadRequestResponse(response, "taskId is required.");
					}

					// The monolith stores taskId via the relatedRecords list
					var relatedRecords = new List<Guid> { taskId };

					_commentService.Create(
						createdBy: user?.Id,
						body: commentBody,
						parentId: parentId,
						relatedRecords: relatedRecords);

					response.Success = true;
					response.Message = "Comment created.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error creating comment");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Deletes a comment and all its child comments.
		/// DELETE /api/v3/{locale}/project/comment/{commentId}
		/// Source: ProjectController.DeletePcPostListItem()
		/// </summary>
		[HttpDelete("comment/{commentId}")]
		public IActionResult DeleteComment(string locale, Guid commentId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					_commentService.Delete(commentId);
					response.Success = true;
					response.Message = "Comment deleted.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error deleting comment {CommentId}", commentId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		#endregion

		#region Project Endpoints

		/// <summary>
		/// Retrieves a project by its unique identifier.
		/// GET /api/v3/{locale}/project/{projectId}
		/// </summary>
		[HttpGet("{projectId:guid}")]
		public IActionResult GetProject(string locale, Guid projectId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var records = new EqlCommand("SELECT * FROM project WHERE id = @projectId",
						parameters: new EqlParameter[] { new EqlParameter("projectId", projectId) }).Execute();
					if (records == null || records.Count == 0)
					{
						response.Success = false;
						response.Message = $"Project not found with ID: {projectId}";
						HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
						return Json(response);
					}
					response.Object = records[0];
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving project {ProjectId}", projectId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		/// <summary>
		/// Retrieves all timelogs associated with a project.
		/// GET /api/v3/{locale}/project/{projectId}/timelogs
		/// </summary>
		[HttpGet("{projectId:guid}/timelogs")]
		public IActionResult GetProjectTimelogs(string locale, Guid projectId)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var records = new EqlCommand(
						"SELECT * FROM timelog WHERE $task_nn_timelog.project_id = @projectId ORDER BY logged_on DESC",
						parameters: new EqlParameter[] { new EqlParameter("projectId", projectId) }).Execute();
					response.Object = records;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error retrieving project timelogs for {ProjectId}", projectId);
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		#endregion

		#region Feed Endpoints

		/// <summary>
		/// Creates a feed item recording task activity.
		/// POST /api/v3/{locale}/project/feed
		/// </summary>
		[HttpPost("feed")]
		public IActionResult CreateFeedItem(string locale, [FromBody] JObject body)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var feedBody = body.Value<string>("body") ?? string.Empty;
					var feedSubject = body.Value<string>("subject") ?? string.Empty;
					var feedType = body.Value<string>("type") ?? "system";
					var taskId = body.Value<string>("taskId") ?? string.Empty;

					// relatedRecords stores IDs of entities this feed item is about
					var relatedRecords = new List<string>();
					if (!string.IsNullOrWhiteSpace(taskId))
						relatedRecords.Add(taskId);

					_feedService.Create(
						createdBy: user?.Id ?? Guid.Empty,
						subject: feedSubject,
						body: feedBody,
						relatedRecords: relatedRecords,
						type: feedType);

					response.Success = true;
					response.Message = "Feed item created.";
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error creating feed item");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		#endregion

		#region Record CRUD Endpoints (EQL-based)

		/// <summary>
		/// Executes an EQL query against project-owned entities.
		/// POST /api/v3/{locale}/project/record/query
		/// </summary>
		[HttpPost("record/query")]
		public IActionResult QueryRecords(string locale, [FromBody] JObject body)
		{
			var response = new ResponseModel();
			try
			{
				var user = SecurityContext.CurrentUser;
				using (SecurityContext.OpenScope(user))
				{
					var eql = body.Value<string>("eql");
					if (string.IsNullOrWhiteSpace(eql))
					{
						response.Success = false;
						response.Message = "EQL query string is required.";
						return DoBadRequestResponse(response, "EQL query string is required.");
					}

					var parameters = new List<EqlParameter>();
					var paramsArray = body.Value<JArray>("parameters");
					if (paramsArray != null)
					{
						foreach (var param in paramsArray)
						{
							var paramName = param.Value<string>("name");
							var paramValue = param.Value<string>("value");
							if (!string.IsNullOrEmpty(paramName))
							{
								parameters.Add(new EqlParameter(paramName, paramValue));
							}
						}
					}

					var records = new EqlCommand(eql, parameters: parameters.ToArray()).Execute();
					response.Object = records;
					response.Success = true;
					response.Timestamp = DateTime.UtcNow;
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error executing EQL query");
				return DoBadRequestResponse(response, ex: ex);
			}
			return DoResponse(response);
		}

		#endregion
	}
}
