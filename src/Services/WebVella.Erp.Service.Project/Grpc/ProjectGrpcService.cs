// =============================================================================
// ProjectGrpcService.cs — Server-Side gRPC Service for Project Microservice
// =============================================================================
// Implements all 18 RPCs defined in proto/project.proto, enabling other
// microservices (CRM, Mail, Core, Gateway, Reporting) to invoke Project-domain
// operations across service boundaries.
//
// In the monolith, these operations were accessed via direct in-process method
// calls on ProjectController and domain services (TaskService, TimelogService,
// CommentService, FeedService, ReportService). This gRPC service replaces
// those direct calls with a well-defined inter-service API contract.
//
// Proto source: proto/project.proto (ProjectService definition — 18 RPCs)
// Pattern template: CRM gRPC service (CrmGrpcService.cs)
//
// Dependencies (9 DI-injected):
//   - TaskService: Task CRUD, status management, queue queries, calculation fields
//   - TimelogService: Timelog CRUD, period queries
//   - CommentService: Comment CRUD with threading support
//   - FeedService: Feed item creation for activity tracking
//   - ReportingService: Monthly timelog aggregation (GetTimelogData)
//   - RecordManager: Generic record CRUD, relation management, Find queries
//   - EntityManager: Entity metadata validation (ReadEntity)
//   - EntityRelationManager: Relation management (watcher subscriptions)
//   - ILogger: Structured logging (Debug for lookups, Warning for validation, Error for failures)
//
// Cross-service reference analysis (AAP 0.7.1):
//   - Task → Account: resolved via CRM gRPC (account_id in project)
//   - Task → User: resolved via Core gRPC (owner_id, created_by)
//   - Timelog → User: resolved via Core gRPC (created_by)
//   - Timelog → Reporting: aggregation data via ReportingService
//
// Error handling: Every method catches ValidationException (→ InvalidArgument),
// ArgumentException (→ InvalidArgument), UnauthorizedAccessException (→ PermissionDenied),
// and general Exception (→ Internal) with structured logging.
//
// Security: [Authorize] attribute enforces JWT Bearer authentication. Every method
// opens SecurityContext.OpenScope(user) for entity-level permission enforcement.
//
// Serialization: Newtonsoft.Json with TypeNameHandling.Auto for polymorphic
// EntityQuery/QueryObject deserialization (AAP 0.8.2).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Project.Domain.Models;
using WebVella.Erp.Service.Project.Domain.Services;

// Alias to disambiguate proto-generated ErrorModel from SharedKernel ErrorModel
using GrpcErrorModel = WebVella.Erp.SharedKernel.Grpc.ErrorModel;

namespace WebVella.Erp.Service.Project.Grpc
{
	/// <summary>
	/// gRPC service implementation for the Project microservice providing inter-service
	/// operations for tasks, timelogs, comments, and feed items. Inherits from the
	/// proto-generated <see cref="ProjectService.ProjectServiceBase"/> to implement
	/// all 18 RPC methods defined in proto/project.proto.
	///
	/// Design decisions (AAP-derived):
	/// <list type="bullet">
	///   <item>SecurityContext.OpenScope on every method — preserving monolith
	///     per-request authentication semantics across gRPC boundaries</item>
	///   <item>Maps domain EntityRecord objects to proto-generated message types
	///     for type-safe inter-service communication</item>
	///   <item>Delegates to domain services (TaskService, TimelogService,
	///     CommentService, FeedService) for business logic preservation</item>
	/// </list>
	/// </summary>
	[Authorize]
	public class ProjectGrpcService : ProjectService.ProjectServiceBase
	{
		#region ===== Constants =====

		/// <summary>
		/// Set of entity names owned by the Project service boundary.
		/// </summary>
		private static readonly HashSet<string> ProjectEntityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"task",
			"timelog",
			"comment",
			"feed_item",
			"task_status",
			"task_type",
			"project"
		};

		#endregion

		#region ===== Private Fields =====

		private readonly TaskService _taskService;
		private readonly TimelogService _timelogService;
		private readonly CommentService _commentService;
		private readonly FeedService _feedService;
		private readonly ReportingService _reportingService;
		private readonly RecordManager _recordManager;
		private readonly EntityManager _entityManager;
		private readonly EntityRelationManager _relationManager;
		private readonly ILogger<ProjectGrpcService> _logger;

		/// <summary>
		/// JSON serialization settings using TypeNameHandling.Auto for polymorphic
		/// QueryObject deserialization and NullValueHandling.Ignore for compact transport.
		/// Per AAP 0.8.2 — preserve Newtonsoft.Json serialization for API contract stability.
		/// </summary>
		private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
		{
			TypeNameHandling = TypeNameHandling.Auto,
			NullValueHandling = NullValueHandling.Ignore,
			Formatting = Formatting.None
		};

		#endregion

		#region ===== Constructor =====

		/// <summary>
		/// Constructs a ProjectGrpcService with all required domain service dependencies.
		/// All dependencies are injected via ASP.NET Core DI — no static singletons per AAP 0.8.3.
		/// </summary>
		public ProjectGrpcService(
			TaskService taskService,
			TimelogService timelogService,
			CommentService commentService,
			FeedService feedService,
			ReportingService reportingService,
			RecordManager recordManager,
			EntityManager entityManager,
			EntityRelationManager relationManager,
			ILogger<ProjectGrpcService> logger)
		{
			_taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
			_timelogService = timelogService ?? throw new ArgumentNullException(nameof(timelogService));
			_commentService = commentService ?? throw new ArgumentNullException(nameof(commentService));
			_feedService = feedService ?? throw new ArgumentNullException(nameof(feedService));
			_reportingService = reportingService ?? throw new ArgumentNullException(nameof(reportingService));
			_recordManager = recordManager ?? throw new ArgumentNullException(nameof(recordManager));
			_entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
			_relationManager = relationManager ?? throw new ArgumentNullException(nameof(relationManager));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		#endregion

		#region ===== Helper Methods =====

		/// <summary>
		/// Extracts the authenticated user from the gRPC call context by reading
		/// JWT claims from the underlying HttpContext. Follows the CRM gRPC pattern.
		/// </summary>
		private ErpUser ExtractUserFromContext(ServerCallContext context)
		{
			var httpContext = context.GetHttpContext();
			if (httpContext?.User == null)
			{
				throw new RpcException(new Status(StatusCode.Unauthenticated, "User not authenticated"));
			}

			var user = SecurityContext.ExtractUserFromClaims(httpContext.User.Claims);
			if (user == null)
			{
				throw new RpcException(new Status(StatusCode.Unauthenticated, "User not authenticated"));
			}

			return user;
		}

		/// <summary>
		/// Safely extracts a string field from an EntityRecord, returning empty string
		/// if the field is null or missing.
		/// </summary>
		private static string SafeString(EntityRecord record, string fieldName)
		{
			if (record == null) return string.Empty;
			object val = null;
			val = record.Properties.ContainsKey(fieldName) ? record[fieldName] : null;
			return val?.ToString() ?? string.Empty;
		}

		/// <summary>
		/// Safely extracts a DateTime field and converts to protobuf Timestamp.
		/// Returns null if the field is not a valid DateTime.
		/// </summary>
		private static Timestamp SafeTimestamp(EntityRecord record, string fieldName)
		{
			if (record == null) return null;
			object val = null;
			val = record.Properties.ContainsKey(fieldName) ? record[fieldName] : null;
			if (val is DateTime dt)
			{
				if (dt.Kind == DateTimeKind.Unspecified)
					dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
				return Timestamp.FromDateTime(dt.ToUniversalTime());
			}
			return null;
		}

		/// <summary>
		/// Safely extracts an integer field from an EntityRecord.
		/// Returns 0 if the field is null or not a valid number.
		/// </summary>
		private static int SafeInt(EntityRecord record, string fieldName)
		{
			if (record == null) return 0;
			object val = null;
			val = record.Properties.ContainsKey(fieldName) ? record[fieldName] : null;
			if (val is int i) return i;
			if (val is long l) return (int)l;
			if (val is decimal d) return (int)d;
			if (val != null && int.TryParse(val.ToString(), out var parsed)) return parsed;
			return 0;
		}

		/// <summary>
		/// Safely extracts a boolean field from an EntityRecord.
		/// Returns false if the field is null or not a valid boolean.
		/// </summary>
		private static bool SafeBool(EntityRecord record, string fieldName)
		{
			if (record == null) return false;
			object val = null;
			val = record.Properties.ContainsKey(fieldName) ? record[fieldName] : null;
			if (val is bool b) return b;
			if (val != null && bool.TryParse(val.ToString(), out var parsed)) return parsed;
			return false;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated TaskRecord message.
		/// </summary>
		private static TaskRecord MapToTaskRecord(EntityRecord record)
		{
			var task = new TaskRecord
			{
				Id = SafeString(record, "id"),
				Subject = SafeString(record, "subject"),
				Body = SafeString(record, "body"),
				Priority = SafeString(record, "priority"),
				StatusId = SafeString(record, "status_id"),
				TypeId = SafeString(record, "type_id"),
				ProjectId = SafeString(record, "project_id"),
				OwnerId = SafeString(record, "owner_id"),
				Estimation = SafeString(record, "estimation"),
				XBillableMinutes = SafeString(record, "x_billable_minutes"),
				XNonbillableMinutes = SafeString(record, "x_nonbillable_minutes"),
				CreatedBy = SafeString(record, "created_by"),
				XSearch = SafeString(record, "x_search"),
				RecurrenceId = SafeString(record, "recurrence_id")
			};

			var startDate = SafeTimestamp(record, "start_date");
			if (startDate != null) task.StartDate = startDate;

			var endDate = SafeTimestamp(record, "end_date");
			if (endDate != null) task.EndDate = endDate;

			var createdOn = SafeTimestamp(record, "created_on");
			if (createdOn != null) task.CreatedOn = createdOn;

			// Extract watcher IDs if available (stored as List<Guid> in relation)
			object watcherVal = record.Properties.ContainsKey("watcher_ids") ? record["watcher_ids"] : null;
			if (watcherVal is List<Guid> watcherGuids)
			{
				foreach (var wg in watcherGuids)
					task.WatcherIds.Add(wg.ToString());
			}
			else if (watcherVal is IEnumerable<object> watcherList)
			{
				foreach (var w in watcherList)
					task.WatcherIds.Add(w?.ToString() ?? string.Empty);
			}

			return task;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated ProjectRecord message.
		/// </summary>
		private static ProjectRecord MapToProjectRecord(EntityRecord record)
		{
			var project = new ProjectRecord
			{
				Id = SafeString(record, "id"),
				Name = SafeString(record, "name"),
				Description = SafeString(record, "description"),
				OwnerId = SafeString(record, "owner_id"),
				AccountId = SafeString(record, "account_id"),
				CreatedBy = SafeString(record, "created_by")
			};

			var createdOn = SafeTimestamp(record, "created_on");
			if (createdOn != null) project.CreatedOn = createdOn;

			return project;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated TimelogRecord message.
		/// </summary>
		private static TimelogRecord MapToTimelogRecord(EntityRecord record)
		{
			var timelog = new TimelogRecord
			{
				Id = SafeString(record, "id"),
				TaskId = SafeString(record, "task_id"),
				CreatedBy = SafeString(record, "created_by"),
				Minutes = SafeInt(record, "minutes"),
				IsBillable = SafeBool(record, "is_billable"),
				Body = SafeString(record, "body")
			};

			var createdOn = SafeTimestamp(record, "created_on");
			if (createdOn != null) timelog.CreatedOn = createdOn;

			var loggedOn = SafeTimestamp(record, "logged_on");
			if (loggedOn != null) timelog.LoggedOn = loggedOn;

			return timelog;
		}

		/// <summary>
		/// Maps an EntityRecord to the proto-generated TaskStatusRecord message.
		/// </summary>
		private static TaskStatusRecord MapToTaskStatusRecord(EntityRecord record)
		{
			return new TaskStatusRecord
			{
				Id = SafeString(record, "id"),
				Label = SafeString(record, "label"),
				IconClass = SafeString(record, "icon_class"),
				Color = SafeString(record, "color"),
				IsClosed = SafeBool(record, "is_closed")
			};
		}

		/// <summary>
		/// Converts SharedKernel ErrorModel list to proto-generated ErrorModel list.
		/// </summary>
		private static List<GrpcErrorModel> MapErrors(List<ErrorModel> errors)
		{
			if (errors == null || errors.Count == 0) return new List<GrpcErrorModel>();
			return errors.Select(e => new GrpcErrorModel
			{
				Key = e.Key ?? string.Empty,
				Value = e.Value ?? string.Empty,
				Message = e.Message ?? string.Empty
			}).ToList();
		}

		/// <summary>
		/// Converts the proto TasksDueType enum to the domain TasksDueType enum.
		/// </summary>
		private static Domain.Models.TasksDueType MapTasksDueType(Grpc.TasksDueType protoDueType)
		{
			return protoDueType switch
			{
				Grpc.TasksDueType.All => Domain.Models.TasksDueType.All,
				Grpc.TasksDueType.Overdue => Domain.Models.TasksDueType.EndTimeOverdue,
				Grpc.TasksDueType.Today => Domain.Models.TasksDueType.EndTimeDueToday,
				Grpc.TasksDueType.ThisWeek => Domain.Models.TasksDueType.EndTimeNotDue,
				Grpc.TasksDueType.ThisMonth => Domain.Models.TasksDueType.StartTimeDue,
				_ => Domain.Models.TasksDueType.All
			};
		}

		/// <summary>
		/// Parses a Guid from a proto string field. Throws RpcException on invalid format.
		/// </summary>
		private static Guid ParseGuid(string value, string fieldName)
		{
			if (string.IsNullOrWhiteSpace(value))
				throw new RpcException(new Status(StatusCode.InvalidArgument, $"{fieldName} is required."));
			if (!Guid.TryParse(value, out var result))
				throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid {fieldName} format: {value}"));
			return result;
		}

		/// <summary>
		/// Optionally parses a Guid from a proto string field. Returns null if empty.
		/// </summary>
		private static Guid? ParseOptionalGuid(string value)
		{
			if (string.IsNullOrWhiteSpace(value)) return null;
			if (Guid.TryParse(value, out var result)) return result;
			return null;
		}

		/// <summary>
		/// Serializes an object to JSON using the shared serialization settings.
		/// Uses Newtonsoft.Json with TypeNameHandling.Auto for polymorphic QueryObject
		/// deserialization and NullValueHandling.Ignore for compact transport.
		/// Per AAP 0.8.2 — preserve Newtonsoft.Json serialization exclusively.
		/// </summary>
		private static string SerializeToJson(object obj)
		{
			if (obj == null) return null;
			return JsonConvert.SerializeObject(obj, _jsonSettings);
		}

		/// <summary>
		/// Validates that the given entity name belongs to the Project service boundary.
		/// Uses <see cref="EntityManager.ReadEntity(string)"/> to verify entity exists
		/// and checks against the static <see cref="ProjectEntityNames"/> set.
		/// </summary>
		private void ValidateProjectEntity(string entityName)
		{
			if (string.IsNullOrWhiteSpace(entityName))
				throw new RpcException(new Status(StatusCode.InvalidArgument, "Entity name is required."));

			if (!ProjectEntityNames.Contains(entityName))
				throw new RpcException(new Status(StatusCode.InvalidArgument,
					$"Entity '{entityName}' is not owned by the Project service. " +
					$"Valid entities: {string.Join(", ", ProjectEntityNames)}"));

			// Verify entity exists in metadata via EntityManager
			var entityResponse = _entityManager.ReadEntity(entityName);
			if (entityResponse == null || !entityResponse.Success || entityResponse.Object == null)
			{
				_logger.LogWarning("Entity metadata not found for '{EntityName}' in Project service", entityName);
				throw new RpcException(new Status(StatusCode.NotFound,
					$"Entity metadata not found for '{entityName}'."));
			}
		}

		#endregion

		#region ===== Project Operations =====

		/// <summary>
		/// Retrieves a project by its unique identifier.
		/// Proto: rpc GetProject(GetProjectRequest) returns (GetProjectResponse)
		/// </summary>
		public override async Task<GetProjectResponse> GetProject(
			GetProjectRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var projectId = ParseGuid(request.ProjectId, "project_id");
					_logger.LogDebug("GetProject: Looking up project {ProjectId}", projectId);

					var query = new EntityQuery("project", "*", EntityQuery.QueryEQ("id", projectId));
					var response = _recordManager.Find(query);

					if (!response.Success || response.Object?.Data == null || response.Object.Data.Count == 0)
					{
						_logger.LogDebug("GetProject: Project not found {ProjectId}", projectId);
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Project not found with ID: {request.ProjectId}"));
					}

					var projectRecord = MapToProjectRecord(response.Object.Data[0]);
					return await Task.FromResult(new GetProjectResponse
					{
						Success = true,
						Project = projectRecord
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetProject), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (ArgumentException ex)
			{
				_logger.LogWarning(ex, "Invalid argument in {Method}: {Message}", nameof(GetProject), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(GetProject));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetProject), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves all timelogs associated with a project.
		/// Proto: rpc GetProjectTimelogs(GetProjectTimelogsRequest) returns (GetProjectTimelogsResponse)
		/// </summary>
		public override async Task<GetProjectTimelogsResponse> GetProjectTimelogs(
			GetProjectTimelogsRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var projectId = ParseGuid(request.ProjectId, "project_id");
					_logger.LogDebug("GetProjectTimelogs: Querying timelogs for project {ProjectId}", projectId);

					var timelogs = _timelogService.GetTimelogsForPeriod(
						projectId, null, DateTime.MinValue, DateTime.MaxValue);

					var result = new GetProjectTimelogsResponse { Success = true };
					if (timelogs != null)
					{
						foreach (var tl in timelogs)
						{
							result.Timelogs.Add(MapToTimelogRecord(tl));
						}
					}
					return await Task.FromResult(result);
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetProjectTimelogs), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (ArgumentException ex)
			{
				_logger.LogWarning(ex, "Invalid argument in {Method}: {Message}", nameof(GetProjectTimelogs), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(GetProjectTimelogs));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetProjectTimelogs), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Task Operations =====

		/// <summary>
		/// Retrieves a single task by its unique identifier.
		/// Proto: rpc GetTask(GetTaskRequest) returns (GetTaskResponse)
		/// </summary>
		public override async Task<GetTaskResponse> GetTask(
			GetTaskRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					_logger.LogDebug("GetTask: Looking up task {TaskId}", taskId);

					var taskRecord = _taskService.GetTask(taskId);

					if (taskRecord == null)
					{
						_logger.LogDebug("GetTask: Task not found {TaskId}", taskId);
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Task not found with ID: {request.TaskId}"));
					}

					return await Task.FromResult(new GetTaskResponse
					{
						Success = true,
						Task = MapToTaskRecord(taskRecord)
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetTask), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (ArgumentException ex)
			{
				_logger.LogWarning(ex, "Invalid argument in {Method}: {Message}", nameof(GetTask), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(GetTask));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetTask), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Queries the task queue with filtering and pagination.
		/// Proto: rpc GetTaskQueue(GetTaskQueueRequest) returns (GetTaskQueueResponse)
		/// </summary>
		public override async Task<GetTaskQueueResponse> GetTaskQueue(
			GetTaskQueueRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var projectId = ParseOptionalGuid(request.ProjectId);
					var userId = ParseOptionalGuid(request.UserId);
					var dueType = MapTasksDueType(request.DueType);
					int? limit = request.Limit > 0 ? request.Limit : null;

					_logger.LogDebug("GetTaskQueue: project={ProjectId}, user={UserId}, dueType={DueType}, limit={Limit}",
						projectId, userId, dueType, limit);

					var tasks = _taskService.GetTaskQueue(projectId, userId, dueType, limit, request.IncludeProjectData);

					var result = new GetTaskQueueResponse { Success = true };
					if (tasks != null)
					{
						foreach (var t in tasks)
						{
							result.Tasks.Add(MapToTaskRecord(t));
						}
					}
					return await Task.FromResult(result);
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetTaskQueue), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (ArgumentException ex)
			{
				_logger.LogWarning(ex, "Invalid argument in {Method}: {Message}", nameof(GetTaskQueue), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(GetTaskQueue));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetTaskQueue), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves all available task statuses.
		/// Proto: rpc GetTaskStatuses(GetTaskStatusesRequest) returns (GetTaskStatusesResponse)
		/// </summary>
		public override async Task<GetTaskStatusesResponse> GetTaskStatuses(
			GetTaskStatusesRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					_logger.LogDebug("GetTaskStatuses: Retrieving all task statuses");
					var statuses = _taskService.GetTaskStatuses();

					var result = new GetTaskStatusesResponse { Success = true };
					if (statuses != null)
					{
						foreach (var s in statuses)
						{
							result.Statuses.Add(MapToTaskStatusRecord(s));
						}
					}
					return await Task.FromResult(result);
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetTaskStatuses), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetTaskStatuses), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Sets a task's status to a new value.
		/// Proto: rpc SetTaskStatus(SetTaskStatusRequest) returns (SetTaskStatusResponse)
		/// </summary>
		public override async Task<SetTaskStatusResponse> SetTaskStatus(
			SetTaskStatusRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					var statusId = ParseGuid(request.StatusId, "status_id");

					// Validate task exists before status update (preserving monolith business rule
					// from ProjectController.TaskSetStatus lines 362-394)
					var existingTask = _taskService.GetTask(taskId);
					if (existingTask == null)
					{
						_logger.LogWarning("SetTaskStatus: Task not found {TaskId}", taskId);
						throw new RpcException(new Status(StatusCode.NotFound,
							$"Task not found with ID: {request.TaskId}"));
					}

					// Check for redundant status update
					var currentStatusId = existingTask["status_id"];
					if (currentStatusId != null && currentStatusId is Guid currentGuid && currentGuid == statusId)
					{
						return await Task.FromResult(new SetTaskStatusResponse
						{
							Success = true,
							Message = "Status already set to the requested value."
						});
					}

					_logger.LogDebug("SetTaskStatus: Updating task {TaskId} status to {StatusId}", taskId, statusId);
					_taskService.SetStatus(taskId, statusId);

					return await Task.FromResult(new SetTaskStatusResponse
					{
						Success = true,
						Message = "Task status updated."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(SetTaskStatus), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (ArgumentException ex)
			{
				_logger.LogWarning(ex, "Invalid argument in {Method}: {Message}", nameof(SetTaskStatus), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(SetTaskStatus));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(SetTaskStatus), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Manages task watch subscriptions (start/stop watching).
		/// Proto: rpc SetTaskWatch(SetTaskWatchRequest) returns (SetTaskWatchResponse)
		/// </summary>
		public override async Task<SetTaskWatchResponse> SetTaskWatch(
			SetTaskWatchRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					var userId = !string.IsNullOrWhiteSpace(request.UserId)
						? Guid.Parse(request.UserId)
						: (user?.Id ?? Guid.Empty);

					var relationName = "$user_nn_task_watchers";
					var relationResponse = _relationManager.Read(relationName);
					if (!relationResponse.Success || relationResponse.Object == null)
					{
						throw new RpcException(new Status(StatusCode.Internal, "Watcher relation not found."));
					}

					var relation = relationResponse.Object;
					if (request.StartWatch)
					{
						_recordManager.CreateRelationManyToManyRecord(relation.Id, userId, taskId);
					}
					else
					{
						_recordManager.RemoveRelationManyToManyRecord(relation.Id, userId, taskId);
					}

					return await Task.FromResult(new SetTaskWatchResponse
					{
						Success = true,
						Message = request.StartWatch ? "Task watch started." : "Task watch stopped."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(SetTaskWatch), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(SetTaskWatch));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(SetTaskWatch), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Starts the timelog timer for a task.
		/// Proto: rpc StartTaskTimelog(StartTaskTimelogRequest) returns (StartTaskTimelogResponse)
		/// </summary>
		public override async Task<StartTaskTimelogResponse> StartTaskTimelog(
			StartTaskTimelogRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					_taskService.StartTaskTimelog(taskId);

					return await Task.FromResult(new StartTaskTimelogResponse
					{
						Success = true,
						Message = "Task timelog started."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(StartTaskTimelog), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(StartTaskTimelog));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(StartTaskTimelog), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Stops the timelog timer for a task.
		/// Proto: rpc StopTaskTimelog(StopTaskTimelogRequest) returns (StopTaskTimelogResponse)
		/// </summary>
		public override async Task<StopTaskTimelogResponse> StopTaskTimelog(
			StopTaskTimelogRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					_taskService.StopTaskTimelog(taskId);

					return await Task.FromResult(new StopTaskTimelogResponse
					{
						Success = true,
						Message = "Task timelog stopped."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(StopTaskTimelog), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(StopTaskTimelog));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(StopTaskTimelog), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Retrieves tasks that need starting (where start_time has passed and status is "not started").
		/// Used by the StartTasksOnStartDate background job.
		/// Proto: rpc GetTasksNeedingStart(GetTasksNeedingStartRequest) returns (GetTasksNeedingStartResponse)
		/// </summary>
		public override async Task<GetTasksNeedingStartResponse> GetTasksNeedingStart(
			GetTasksNeedingStartRequest request, ServerCallContext context)
		{
			try
			{
				// Use SystemScope for background/scheduled queries that run without
				// a specific user context (e.g., StartTasksOnStartDate job).
				// Falls back to user scope if authenticated user is present.
				var user = ExtractUserFromContext(context);
				using (user != null ? SecurityContext.OpenScope(user) : SecurityContext.OpenSystemScope())
				{
					_logger.LogDebug("GetTasksNeedingStart: Querying tasks that need starting");
					var tasks = _taskService.GetTasksThatNeedStarting();

					var result = new GetTasksNeedingStartResponse { Success = true };
					if (tasks != null)
					{
						foreach (var t in tasks)
						{
							result.Tasks.Add(MapToTaskRecord(t));
						}
					}
					return await Task.FromResult(result);
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetTasksNeedingStart), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetTasksNeedingStart), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Recalculates task key, x_search, and project association fields.
		/// Proto: rpc SetCalculationFields(SetCalculationFieldsRequest) returns (SetCalculationFieldsResponse)
		/// </summary>
		public override async Task<SetCalculationFieldsResponse> SetCalculationFields(
			SetCalculationFieldsRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");

					_taskService.SetCalculationFields(taskId, out string subject, out Guid projectId, out Guid? projectOwnerId);

					return await Task.FromResult(new SetCalculationFieldsResponse
					{
						Success = true,
						Subject = subject ?? string.Empty,
						ProjectId = projectId.ToString(),
						ProjectOwnerId = projectOwnerId?.ToString() ?? string.Empty
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(SetCalculationFields), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(SetCalculationFields));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(SetCalculationFields), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Timelog Operations =====

		/// <summary>
		/// Creates a new timelog entry for a task.
		/// Proto: rpc CreateTimelog(CreateTimelogRequest) returns (CreateTimelogResponse)
		/// </summary>
		public override async Task<CreateTimelogResponse> CreateTimelog(
			CreateTimelogRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					var createdBy = ParseOptionalGuid(request.CreatedBy) ?? user?.Id;
					var id = ParseOptionalGuid(request.Id);
					var loggedOn = request.LoggedOn != null ? request.LoggedOn.ToDateTime() : DateTime.UtcNow;

					// Store task association via relatedRecords as in the monolith
					var relatedRecords = new List<Guid> { taskId };

					_timelogService.Create(
						id: id,
						createdBy: createdBy,
						loggedOn: loggedOn,
						minutes: request.Minutes,
						isBillable: request.IsBillable,
						body: request.Body ?? string.Empty,
						relatedRecords: relatedRecords);

					return await Task.FromResult(new CreateTimelogResponse
					{
						Success = true,
						TimelogId = (id ?? Guid.Empty).ToString(),
						Message = "Timelog created."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(CreateTimelog), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(CreateTimelog));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(CreateTimelog), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Deletes a timelog entry by its unique identifier.
		/// Proto: rpc DeleteTimelog(DeleteTimelogRequest) returns (DeleteTimelogResponse)
		/// </summary>
		public override async Task<DeleteTimelogResponse> DeleteTimelog(
			DeleteTimelogRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var timelogId = ParseGuid(request.Id, "id");
					_timelogService.Delete(timelogId);

					return await Task.FromResult(new DeleteTimelogResponse
					{
						Success = true,
						Message = "Timelog deleted."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(DeleteTimelog), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(DeleteTimelog));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(DeleteTimelog), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Queries timelogs within a date range, optionally filtered by project and user.
		/// Proto: rpc GetTimelogsForPeriod(GetTimelogsForPeriodRequest) returns (GetTimelogsForPeriodResponse)
		/// </summary>
		public override async Task<GetTimelogsForPeriodResponse> GetTimelogsForPeriod(
			GetTimelogsForPeriodRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var projectId = ParseOptionalGuid(request.ProjectId);
					var userId = ParseOptionalGuid(request.UserId);
					var startDate = request.StartDate != null ? request.StartDate.ToDateTime() : DateTime.UtcNow.AddDays(-30);
					var endDate = request.EndDate != null ? request.EndDate.ToDateTime() : DateTime.UtcNow;

					_logger.LogDebug("GetTimelogsForPeriod: project={ProjectId}, user={UserId}, start={StartDate}, end={EndDate}",
						projectId, userId, startDate, endDate);

					var timelogs = _timelogService.GetTimelogsForPeriod(projectId, userId, startDate, endDate);

					var result = new GetTimelogsForPeriodResponse { Success = true };
					if (timelogs != null)
					{
						_logger.LogDebug("GetTimelogsForPeriod: Found {Count} timelogs (TotalCount={TotalCount})",
							timelogs.Count, timelogs.TotalCount);
						foreach (var tl in timelogs)
						{
							result.Timelogs.Add(MapToTimelogRecord(tl));
						}
					}
					return await Task.FromResult(result);
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetTimelogsForPeriod), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (ArgumentException ex)
			{
				_logger.LogWarning(ex, "Invalid argument in {Method}: {Message}", nameof(GetTimelogsForPeriod), ex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(GetTimelogsForPeriod));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetTimelogsForPeriod), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Starts a timelog for a task via the controller flow.
		/// Proto: rpc StartTimelog(StartTimelogRequest) returns (StartTimelogResponse)
		/// </summary>
		public override async Task<StartTimelogResponse> StartTimelog(
			StartTimelogRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					_taskService.StartTaskTimelog(taskId);

					return await Task.FromResult(new StartTimelogResponse
					{
						Success = true,
						Message = "Timelog started."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(StartTimelog), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(StartTimelog));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(StartTimelog), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Comment Operations =====

		/// <summary>
		/// Creates a new comment, optionally threaded under a parent comment.
		/// Proto: rpc CreateComment(CreateCommentRequest) returns (CreateCommentResponse)
		/// </summary>
		public override async Task<CreateCommentResponse> CreateComment(
			CreateCommentRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var taskId = ParseGuid(request.TaskId, "task_id");
					var createdBy = ParseOptionalGuid(request.CreatedBy) ?? user?.Id;
					var id = ParseOptionalGuid(request.Id);
					var parentId = ParseOptionalGuid(request.ParentId);

					// Store task association via relatedRecords as in the monolith
					var relatedRecords = new List<Guid> { taskId };

					_commentService.Create(
						id: id,
						createdBy: createdBy,
						body: request.Body ?? string.Empty,
						parentId: parentId,
						relatedRecords: relatedRecords);

					return await Task.FromResult(new CreateCommentResponse
					{
						Success = true,
						CommentId = (id ?? Guid.Empty).ToString(),
						Message = "Comment created."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(CreateComment), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(CreateComment));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(CreateComment), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		/// <summary>
		/// Deletes a comment and all its child comments.
		/// Proto: rpc DeleteComment(DeleteCommentRequest) returns (DeleteCommentResponse)
		/// </summary>
		public override async Task<DeleteCommentResponse> DeleteComment(
			DeleteCommentRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var commentId = ParseGuid(request.Id, "id");
					_commentService.Delete(commentId);

					return await Task.FromResult(new DeleteCommentResponse
					{
						Success = true,
						Message = "Comment deleted."
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(DeleteComment), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(DeleteComment));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(DeleteComment), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Feed Operations =====

		/// <summary>
		/// Creates a feed item recording task activity.
		/// Proto: rpc CreateFeedItem(CreateFeedItemRequest) returns (CreateFeedItemResponse)
		/// </summary>
		public override async Task<CreateFeedItemResponse> CreateFeedItem(
			CreateFeedItemRequest request, ServerCallContext context)
		{
			try
			{
				var user = ExtractUserFromContext(context);
				using (SecurityContext.OpenScope(user))
				{
					var createdBy = ParseOptionalGuid(request.CreatedBy) ?? user?.Id ?? Guid.Empty;
					var id = ParseOptionalGuid(request.Id);

					// Feed items store related entity IDs in the relatedRecords list
					var relatedRecords = new List<string>();
					if (!string.IsNullOrWhiteSpace(request.TaskId))
						relatedRecords.Add(request.TaskId);

					_feedService.Create(
						id: id,
						createdBy: createdBy,
						body: request.Body ?? string.Empty,
						relatedRecords: relatedRecords,
						type: request.Type ?? "system");

					return await Task.FromResult(new CreateFeedItemResponse
					{
						Success = true,
						FeedItemId = (id ?? Guid.Empty).ToString()
					});
				}
			}
			catch (RpcException) { throw; }
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(CreateFeedItem), vex.Message);
				throw new RpcException(new Status(StatusCode.InvalidArgument, vex.Message));
			}
			catch (UnauthorizedAccessException ex)
			{
				_logger.LogError(ex, "Permission denied in {Method}", nameof(CreateFeedItem));
				throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(CreateFeedItem), ex.Message);
				throw new RpcException(new Status(StatusCode.Internal, ex.Message));
			}
		}

		#endregion

		#region ===== Reporting Support =====

		/// <summary>
		/// Provides timelog aggregation data for the Reporting service.
		/// Delegates to <see cref="ReportingService.GetTimelogData(int, int, Guid?)"/>
		/// which performs monthly timelog aggregation with task/project joins and
		/// billable/non-billable minute computation.
		///
		/// This is a public helper method (not a gRPC override) that exposes the
		/// ReportingService capability through the gRPC service for internal use
		/// by background jobs and cross-service composition in the Gateway.
		/// </summary>
		/// <param name="year">The year for the aggregation period.</param>
		/// <param name="month">The month for the aggregation period (1-12).</param>
		/// <param name="accountId">Optional account ID filter.</param>
		/// <returns>List of aggregated timelog EntityRecord instances serialized as JSON.</returns>
		public async Task<string> GetTimelogAggregationJson(int year, int month, Guid? accountId)
		{
			try
			{
				_logger.LogDebug("GetTimelogAggregationJson: year={Year}, month={Month}, account={AccountId}",
					year, month, accountId);

				var data = await _reportingService.GetTimelogData(year, month, accountId);
				return SerializeToJson(data);
			}
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(GetTimelogAggregationJson), vex.Message);
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(GetTimelogAggregationJson), ex.Message);
				throw;
			}
		}

		/// <summary>
		/// Validates that a given entity name belongs to the Project service and returns
		/// query results. Uses <see cref="EntityManager.ReadEntity(string)"/> for entity validation
		/// and <see cref="RecordManager.Find(EntityQuery)"/> for execution.
		/// This public utility is available for cross-service entity resolution requests.
		/// </summary>
		/// <param name="entityName">The entity name to validate.</param>
		/// <param name="queryJson">JSON-serialized EntityQuery to execute.</param>
		/// <returns>JSON-serialized QueryResponse result.</returns>
		public string FindProjectRecordsJson(string entityName, string queryJson)
		{
			try
			{
				ValidateProjectEntity(entityName);

				_logger.LogDebug("FindProjectRecordsJson: entity={EntityName}", entityName);

				var query = JsonConvert.DeserializeObject<EntityQuery>(queryJson, _jsonSettings);
				if (query == null)
					throw new ArgumentException("Invalid query JSON.");

				var response = _recordManager.Find(query);
				if (!response.Success)
				{
					_logger.LogWarning("FindProjectRecordsJson query failed: {Message}", response.Message);
				}
				return SerializeToJson(response);
			}
			catch (ValidationException vex)
			{
				_logger.LogWarning(vex, "Validation error in {Method}: {Message}", nameof(FindProjectRecordsJson), vex.Message);
				throw;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error in {Method}: {Message}", nameof(FindProjectRecordsJson), ex.Message);
				throw;
			}
		}

		/// <summary>
		/// Finds multiple project records by a list of IDs using QueryOR with QueryEQ predicates.
		/// Uses <see cref="EntityQuery.QueryOR(QueryObject[])"/> and 
		/// <see cref="EntityQuery.QueryEQ(string, object)"/> for bulk lookups.
		/// Supports cross-service bulk resolution (e.g., Gateway composing multiple project references).
		/// </summary>
		/// <param name="entityName">Entity name (must be Project-owned).</param>
		/// <param name="ids">List of record IDs to resolve.</param>
		/// <returns>List of found EntityRecord instances.</returns>
		public List<EntityRecord> FindProjectRecordsByIds(string entityName, IEnumerable<Guid> ids)
		{
			ValidateProjectEntity(entityName);

			var idArray = ids?.ToArray() ?? Array.Empty<Guid>();
			if (idArray.Length == 0)
				return new List<EntityRecord>();

			_logger.LogDebug("FindProjectRecordsByIds: entity={EntityName}, count={Count}", entityName, idArray.Length);

			// Build OR-combined EQ predicates for bulk lookup
			var predicates = idArray.Select(id => EntityQuery.QueryEQ("id", id)).ToArray();
			var query = new EntityQuery(entityName, "*", EntityQuery.QueryOR(predicates));

			var response = _recordManager.Find(query);
			if (response.Success && response.Object?.Data != null)
			{
				return response.Object.Data;
			}
			return new List<EntityRecord>();
		}

		#endregion
	}
}
