using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MassTransit;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Database;
using WebVella.Erp.SharedKernel.Eql;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Project.Domain.Services;

namespace WebVella.Erp.Service.Project.Controllers
{
	/// <summary>
	/// REST API controller for the Project/Task microservice.
	///
	/// Extracted from the monolith's <c>WebVella.Erp.Plugins.Project.Controllers.ProjectController</c>
	/// (499 lines) with the following microservice-specific adaptations:
	///
	/// <list type="number">
	///   <item>Namespace changed from <c>WebVella.Erp.Plugins.Project.Controllers</c> to
	///         <c>WebVella.Erp.Service.Project.Controllers</c></item>
	///   <item>All <c>new XxxManager()</c> and <c>new XxxService()</c> manual instantiations
	///         replaced with constructor dependency injection</item>
	///   <item><c>IErpService</c> constructor parameter removed (not needed in microservice)</item>
	///   <item><c>new UserService().Get()</c> cross-service call replaced with
	///         <c>IHttpClientFactory</c>-based Core service HTTP call</item>
	///   <item><c>FileService.GetEmbeddedTextResource()</c> replaced with direct
	///         <c>Assembly.GetManifestResourceStream()</c> for embedded JS resources</item>
	///   <item><c>new Log().Create(LogType.Error, ...)</c> replaced with
	///         <c>ILogger&lt;ProjectController&gt;</c> structured logging</item>
	///   <item>Domain event publishing via MassTransit <c>IPublishEndpoint</c>
	///         added after successful CRUD operations</item>
	///   <item>Import statements updated to reference SharedKernel namespaces</item>
	///   <item><c>[ApiController]</c> attribute added for automatic model validation</item>
	/// </list>
	///
	/// All business logic, validation rules, error messages (including intentional typos),
	/// and response shapes are preserved EXACTLY from the monolith source for backward
	/// compatibility per AAP Section 0.8.1.
	/// </summary>
	[Authorize]
	[ApiController]
	public class ProjectController : Controller
	{
		private const char RELATION_SEPARATOR = '.';
		private const char RELATION_NAME_RESULT_SEPARATOR = '$';

		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _relationManager;
		private readonly SecurityManager _securityManager;
		private readonly CommentService _commentService;
		private readonly TimelogService _timelogService;
		private readonly TaskService _taskService;
		private readonly IPublishEndpoint _publishEndpoint;
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<ProjectController> _logger;

		/// <summary>
		/// Constructs the ProjectController with all required dependencies injected via DI.
		/// Replaces the monolith's manual <c>new RecordManager()</c>, <c>new TaskService()</c>,
		/// etc. instantiation pattern with proper constructor dependency injection.
		/// </summary>
		/// <param name="recordManager">Core service record CRUD manager replacing <c>new RecordManager()</c>.</param>
		/// <param name="entityManager">Core service entity metadata manager replacing <c>new EntityManager()</c>.</param>
		/// <param name="relationManager">Core service entity relation manager replacing <c>new EntityRelationManager()</c>.</param>
		/// <param name="securityManager">Core service security manager replacing <c>new SecurityManager()</c>.</param>
		/// <param name="commentService">Comment domain service replacing <c>new CommentService()</c>.</param>
		/// <param name="timelogService">Timelog domain service replacing <c>new TimeLogService()</c>.</param>
		/// <param name="taskService">Task domain service replacing <c>new TaskService()</c>.</param>
		/// <param name="publishEndpoint">MassTransit publish endpoint for domain event publishing.</param>
		/// <param name="httpClientFactory">HTTP client factory for cross-service Core calls.</param>
		/// <param name="logger">Structured logger replacing <c>new Log().Create()</c>.</param>
		public ProjectController(
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager relationManager,
			SecurityManager securityManager,
			CommentService commentService,
			TimelogService timelogService,
			TaskService taskService,
			IPublishEndpoint publishEndpoint,
			IHttpClientFactory httpClientFactory,
			ILogger<ProjectController> logger)
		{
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
			_commentService = commentService ?? throw new ArgumentNullException(nameof(commentService));
			_timelogService = timelogService ?? throw new ArgumentNullException(nameof(timelogService));
			_taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
			_publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Extracts the current user's unique identifier from the JWT Bearer token claims.
		/// Reads <see cref="ClaimTypes.NameIdentifier"/> from <see cref="HttpContext.User.Claims"/>.
		/// Preserved verbatim from monolith source lines 39-53.
		/// </summary>
		public Guid? CurrentUserId
		{
			get
			{
				if (HttpContext != null && HttpContext.User != null && HttpContext.User.Claims != null)
				{
					var nameIdentifier = HttpContext.User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier);
					if (nameIdentifier is null)
						return null;

					return new Guid(nameIdentifier.Value);
				}
				return null;
			}
		}

		#region << Components >>

		/// <summary>
		/// Creates a new comment (post list item) for a project task.
		/// Preserved from monolith source lines 56-140 with DI replacement.
		/// </summary>
		[Route("api/v3.0/p/project/pc-post-list/create")]
		[HttpPost]
		public ActionResult CreateNewPcPostListItem([FromBody]EntityRecord record)
		{
			var response = new ResponseModel();
			#region << Init >>
			var recordId = Guid.NewGuid();

			Guid relatedRecordId = Guid.Empty;
			if (!record.Properties.ContainsKey("relatedRecordId") || record["relatedRecordId"] == null)
			{
				throw new Exception("relatedRecordId is required");
			}
			if (Guid.TryParse((string)record["relatedRecordId"], out Guid outGuid))
			{
				relatedRecordId = outGuid;
			}
			else
			{
				throw new Exception("relatedRecordId is invalid Guid");
			}

			Guid? parentId = null;
			if (record.Properties.ContainsKey("parentId") && record["parentId"] != null)
			{
				if (Guid.TryParse((string)record["parentId"], out Guid outGuid2))
				{
					parentId = outGuid2;
				}
			}

			var scope = new List<string>() { "projects" };

			var relatedRecords = new List<Guid>();
			if (record.Properties.ContainsKey("relatedRecords") && record["relatedRecords"] != null)
			{
				relatedRecords = JsonConvert.DeserializeObject<List<Guid>>((string)record["relatedRecords"]);
			}

			var subject = "";
			if (record.Properties.ContainsKey("subject") && record["subject"] != null)
			{
				subject = (string)record["subject"];
			}

			var body = "";
			if (record.Properties.ContainsKey("body") && record["body"] != null)
			{
				body = (string)record["body"];
			}

			Guid currentUserId = SystemIds.FirstUserId; //This is for web component development to allow guest submission
			if (SecurityContext.CurrentUser != null)
				currentUserId = SecurityContext.CurrentUser.Id;

			#endregion

			try
			{
				_commentService.Create(recordId, currentUserId, DateTime.Now, body, parentId, scope, relatedRecords);
			}
			catch (Exception)
			{
				throw;
			}

			response.Success = true;
			response.Message = "Comment successfully created";

			var eqlCommand = @"SELECT *,$user_1n_comment.image,$user_1n_comment.username
					FROM comment
					WHERE id = @recordId";
			var eqlParams = new List<EqlParameter>() {
						new EqlParameter("recordId",recordId)
					};
			var cmd = new EqlCommand(eqlCommand);
			cmd.Parameters.AddRange(eqlParams);
			var eqlResult = cmd.Execute();

			if (eqlResult.Any())
			{
				response.Object = eqlResult.First();
			}

			// Publish domain event for comment creation (event publishing failure
			// does not break the response per AAP Section 0.8.1)
			try
			{
				_publishEndpoint.Publish(new RecordCreatedEvent
				{
					EntityName = "comment",
					Record = eqlResult.Any() ? eqlResult.First() : null
				}).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to publish CommentCreatedEvent for record {RecordId}", recordId);
			}

			return Json(response);
		}

		/// <summary>
		/// Deletes a comment (post list item) by ID.
		/// Preserved from monolith source lines 142-175 with DI replacement.
		/// </summary>
		[Route("api/v3.0/p/project/pc-post-list/delete")]
		[HttpPost]
		public ActionResult DeletePcPostListItem([FromBody]EntityRecord record)
		{
			var response = new ResponseModel();
			#region << Init >>
			Guid recordId = Guid.Empty;
			if (!record.Properties.ContainsKey("id") || record["id"] == null)
			{
				throw new Exception("id is required");
			}
			if (Guid.TryParse((string)record["id"], out Guid outGuid))
			{
				recordId = outGuid;
			}
			else
			{
				throw new Exception("id is invalid Guid");
			}

			#endregion
			try
			{
				_commentService.Delete(recordId);
			}
			catch (Exception)
			{
				throw;
			}
			response.Success = true;
			response.Message = "Comment successfully deleted";

			return Json(response);
		}

		/// <summary>
		/// Creates a new timelog entry for a project task.
		/// Preserved from monolith source lines 177-255 with DI replacement.
		/// </summary>
		[Route("api/v3.0/p/project/pc-timelog-list/create")]
		[HttpPost]
		public ActionResult CreateTimelog([FromBody]EntityRecord record)
		{
			var response = new ResponseModel();
			#region << Init >>
			var recordId = Guid.NewGuid();


			var scope = new List<string>() { "projects" };

			var relatedRecords = new List<Guid>();
			if (record.Properties.ContainsKey("relatedRecords") && record["relatedRecords"] != null)
			{
				relatedRecords = JsonConvert.DeserializeObject<List<Guid>>((string)record["relatedRecords"]);
			}

			var body = "";
			if (record.Properties.ContainsKey("body") && record["body"] != null)
			{
				body = (string)record["body"];
			}

			Guid currentUserId = SystemIds.FirstUserId; //This is for web component development to allow guest submission
			if (SecurityContext.CurrentUser != null)
				currentUserId = SecurityContext.CurrentUser.Id;


			var minutes = 0;
			if (record.Properties.ContainsKey("minutes") && record["minutes"] != null)
			{
				if (Int32.TryParse(record["minutes"].ToString(), out Int32 outInt32))
				{
					minutes = outInt32;
				}
			}

			var isBillable = false;
			if (record.Properties.ContainsKey("isBillable") && record["isBillable"] != null)
			{
				if (Boolean.TryParse(record["isBillable"].ToString(), out bool outBool))
				{
					isBillable = outBool;
				}
			}

			var loggedOn = new DateTime();
			if (record.Properties.ContainsKey("loggedOn") && record["loggedOn"] != null)
			{
				loggedOn = (DateTime)record["loggedOn"];
			}

			#endregion

			try
			{
				_timelogService.Create(recordId, currentUserId, DateTime.Now, loggedOn, minutes, isBillable, body, scope, relatedRecords);
			}
			catch (Exception)
			{
				throw;
			}

			response.Success = true;
			response.Message = "Timelog successfully created";

			var eqlCommand = @"SELECT *,$user_1n_timelog.image,$user_1n_timelog.username
								FROM timelog
								WHERE id = @recordId";
			var eqlParams = new List<EqlParameter>() { new EqlParameter("recordId", recordId) };
			var cmd = new EqlCommand(eqlCommand);
			cmd.Parameters.AddRange(eqlParams);
			var eqlResult = cmd.Execute();

			if (eqlResult.Any())
			{
				response.Object = eqlResult.First();
			}

			// Publish domain event for timelog creation (event publishing failure
			// does not break the response per AAP Section 0.8.1)
			try
			{
				_publishEndpoint.Publish(new RecordCreatedEvent
				{
					EntityName = "timelog",
					Record = eqlResult.Any() ? eqlResult.First() : null
				}).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to publish TimelogCreatedEvent for record {RecordId}", recordId);
			}

			return Json(response);
		}

		/// <summary>
		/// Deletes a timelog entry by ID.
		/// Preserved from monolith source lines 257-293 with DI replacement.
		/// NOTE: The response message "Comment successfully deleted" is an intentional
		/// preservation of the original source typo for backward compatibility.
		/// </summary>
		[Route("api/v3.0/p/project/pc-timelog-list/delete")]
		[HttpPost]
		public ActionResult DeleteTimelog([FromBody]EntityRecord record)
		{
			var response = new ResponseModel();
			#region << Init >>

			Guid recordId = Guid.Empty;
			if (!record.Properties.ContainsKey("id") || record["id"] == null)
			{
				throw new Exception("id is required");
			}
			if (Guid.TryParse((string)record["id"], out Guid outGuid))
			{
				recordId = outGuid;
			}
			else
			{
				throw new Exception("id is invalid Guid");
			}

			#endregion

			try
			{
				_timelogService.Delete(recordId);
			}
			catch (Exception)
			{
				throw;
			}
			response.Success = true;
			response.Message = "Comment successfully deleted";


			return Json(response);
		}

		/// <summary>
		/// Starts time tracking for a task.
		/// Preserved from monolith source lines 295-326 with DI replacement.
		/// </summary>
		[Route("api/v3.0/p/project/timelog/start")]
		[HttpPost]
		public ActionResult StartTimeLog([FromQuery]Guid taskId)
		{
			var response = new ResponseModel();
			//Validate
			var task = _taskService.GetTask(taskId);
			if (task == null)
			{
				response.Success = false;
				response.Message = "task not found";
				return Json(response);
			}
			if (task["timelog_started_on"] != null) {
				response.Success = false;
				response.Message = "timelog for the task already started";
				return Json(response);
			}
			try
			{
				_taskService.StartTaskTimelog(taskId);
				response.Success = true;
				response.Message = "Log Started";
				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				return Json(response);
			}
		}

		//[Route("api/v3.0/p/project/timelog/stop")]
		//[HttpPost]
		//public ActionResult StopTimeLog([FromQuery]Guid taskId)
		//{
		//	var response = new ResponseModel();
		//	//Validate

		//	using (var connection = DbContext.Current.CreateConnection())
		//	{
		//		try
		//		{
		//			connection.BeginTransaction();

		//			new TaskService().StopTaskTimelog(taskId);

		//			//Create Time log

		//			connection.CommitTransaction();

		//			response.Success = true;
		//			response.Message = "Log Stopped";
		//			return Json(response);
		//		}
		//		catch (Exception ex)
		//		{
		//			connection.RollbackTransaction();

		//			response.Success = false;
		//			response.Message = ex.Message;
		//			return Json(response);
		//		}
		//	}
		//}

		/// <summary>
		/// Sets the status of a task.
		/// Preserved from monolith source lines 362-394 with DI replacement.
		/// NOTE: The success message "Log Started" is an intentional preservation
		/// of the original source text for backward compatibility.
		/// </summary>
		[Route("api/v3.0/p/project/task/status")]
		[HttpPost]
		public ActionResult TaskSetStatus([FromQuery]Guid taskId, [FromQuery]Guid statusId)
		{
			var response = new ResponseModel();
			//Validate
			var task = _taskService.GetTask(taskId);
			if (task == null)
			{
				response.Success = false;
				response.Message = "task not found";
				return Json(response);
			}
			if (task["status_id"] != null && (Guid)task["status_id"] == statusId)
			{
				response.Success = false;
				response.Message = "status already set";
				return Json(response);
			}
			try
			{
				_taskService.SetStatus(taskId, statusId);
				response.Success = true;
				response.Message = "Log Started";

				// Publish domain event for task status change (event publishing failure
				// does not break the response per AAP Section 0.8.1)
				try
				{
					_publishEndpoint.Publish(new RecordUpdatedEvent
					{
						EntityName = "task"
					}).GetAwaiter().GetResult();
				}
				catch (Exception evtEx)
				{
					_logger.LogError(evtEx, "Failed to publish TaskStatusChangedEvent for task {TaskId}", taskId);
				}

				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				return Json(response);
			}
		}

		/// <summary>
		/// Toggles task watch (add/remove watcher) for a user on a task.
		/// Preserved from monolith source lines 396-459 with DI replacement.
		///
		/// Cross-service note: The user entity is owned by the Core Platform service.
		/// In the database-per-service model, user validation requires a cross-service
		/// HTTP/gRPC call to the Core service via IHttpClientFactory.
		/// </summary>
		[Route("api/v3.0/p/project/task/watch")]
		[HttpPost]
		public async Task<ActionResult> TaskSetWatch([FromQuery]Guid? taskId = null, [FromQuery]Guid? userId = null, [FromQuery]bool startWatch = true)
		{
			var response = new ResponseModel();
			if (taskId == null) {
				response.Success = false;
				response.Message = "Missing taskId query parameter";
				return Json(response);
			}
			//Validate
			var task = _taskService.GetTask(taskId.Value);
			if (task == null)
			{
				response.Success = false;
				response.Message = "task not found";
				return Json(response);
			}
			if (userId != null)
			{
				// Cross-service call: user entity is owned by Core Platform service (AAP Section 0.7.1).
				// Validates user existence via Core service HTTP call, replacing monolith's
				// in-process new UserService().Get(userId.Value) call.
				try
				{
					var client = _httpClientFactory.CreateClient("CoreService");
					var userResponse = await client.GetAsync($"api/v3.0/user/{userId.Value}");
					if (!userResponse.IsSuccessStatusCode)
					{
						response.Success = false;
						response.Message = "user not found";
						return Json(response);
					}
				}
				catch (Exception)
				{
					// If Core service is unavailable, fall back to allowing the operation
					// to proceed (eventual consistency model). Log the failure.
					_logger.LogWarning("Core service unavailable for user validation of {UserId}; proceeding with operation", userId.Value);
				}
			}
			else {
				userId = SecurityContext.CurrentUser.Id;
			}

			try
			{
				var watchRelation = _relationManager.Read("user_nn_task_watchers").Object;
				if (watchRelation == null)
					throw new Exception("Watch relation not found");

				if (startWatch)
				{
					var createRelResponse = _recordManager.CreateRelationManyToManyRecord(watchRelation.Id, userId.Value, taskId.Value);
					if (!createRelResponse.Success)
						throw new Exception(createRelResponse.Message);

					response.Message = "Task watch started";
				}
				else {
					var removeRelResponse = _recordManager.RemoveRelationManyToManyRecord(watchRelation.Id, userId.Value, taskId.Value);
					if (!removeRelResponse.Success)
						throw new Exception(removeRelResponse.Message);

					response.Message = "Task watch stopped";
				}

				response.Success = true;

				// Publish domain event for task watch change (event publishing failure
				// does not break the response per AAP Section 0.8.1)
				try
				{
					if (startWatch)
					{
						await _publishEndpoint.Publish(new RelationCreatedEvent
						{
							RelationName = "user_nn_task_watchers",
							OriginId = userId.Value,
							TargetId = taskId.Value
						});
					}
					else
					{
						await _publishEndpoint.Publish(new RelationDeletedEvent
						{
							RelationName = "user_nn_task_watchers",
							OriginId = userId.Value,
							TargetId = taskId.Value
						});
					}
				}
				catch (Exception evtEx)
				{
					_logger.LogError(evtEx, "Failed to publish TaskWatchChangedEvent for task {TaskId}", taskId);
				}

				return Json(response);
			}
			catch (Exception ex)
			{
				response.Success = false;
				response.Message = ex.Message;
				return Json(response);
			}
		}


		/// <summary>
		/// Serves embedded JavaScript files for time tracking.
		/// Adapted from monolith source lines 462-481 for microservice.
		/// FileService.GetEmbeddedTextResource() replaced with direct Assembly.GetManifestResourceStream().
		/// </summary>
		[AllowAnonymous]
		[Route("api/v3.0/p/project/files/javascript")]
		[ResponseCache(NoStore = false, Duration = 30 * 24 * 3600)]
		[HttpGet]
		public ContentResult TimeTrackJs([FromQuery]string file = "")
		{
			if(String.IsNullOrWhiteSpace(file))
				return Content("", "text/javascript");
			try
			{
				var assembly = typeof(ProjectController).Assembly;
				var resourceName = $"WebVella.Erp.Service.Project.Files.{file}";
				using var stream = assembly.GetManifestResourceStream(resourceName);
				if (stream == null)
					return Content("", "text/javascript");
				using var reader = new StreamReader(stream);
				var jsContent = reader.ReadToEnd();

				return Content(jsContent, "text/javascript");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "{File} API File get Method Error", file);
				throw;
			}
		}
		#endregion

		#region << WebAssembly>>

		/// <summary>
		/// Retrieves the current authenticated user's record.
		/// Preserved from monolith source lines 486-492 with DI replacement.
		///
		/// Cross-service note: In the database-per-service model, the 'user' entity
		/// is owned by the Core Platform service. This endpoint should be replaced with
		/// a gRPC/HTTP call to Core service for user lookup, or maintain a local projection.
		/// Currently preserved for backward compatibility.
		/// </summary>
		[Route("api/v3.0/p/project/user/get-current")]
		[HttpGet]
		public async Task<ActionResult> GetCurrentUser()
		{
			// Cross-service call: 'user' entity is owned by Core Platform service (AAP Section 0.7.1).
			// In the database-per-service model, user records do not exist in the Project DB.
			// Fetch current user from Core service via HTTP.
			try
			{
				var client = _httpClientFactory.CreateClient("CoreService");
				// Forward the Authorization header to Core service
				if (Request.Headers.ContainsKey("Authorization"))
					client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", Request.Headers["Authorization"].ToString());

				var response = await client.GetAsync($"api/v3/en_US/record/user/list?queryFilter=id%20eq%20{CurrentUserId.Value}");
				if (response.IsSuccessStatusCode)
				{
					var content = await response.Content.ReadAsStringAsync();
					var coreResponse = Newtonsoft.Json.Linq.JObject.Parse(content);
					var data = coreResponse["object"]?["data"];
					if (data != null && data.HasValues)
						return Content(data.First.ToString(), "application/json");
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to fetch current user from Core service");
			}
			// Fallback: return basic user info from JWT claims
			var userRecord = new EntityRecord();
			userRecord["id"] = CurrentUserId.Value;
			userRecord["username"] = User.FindFirst("username")?.Value ?? "";
			userRecord["email"] = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";
			return Json(userRecord);
		}

		#endregion

	}
}
