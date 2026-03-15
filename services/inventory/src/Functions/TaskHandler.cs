using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Inventory.DataAccess;
using WebVellaErp.Inventory.Models;
using WebVellaErp.Inventory.Services;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Inventory.Functions
{
    /// <summary>
    /// AWS Lambda handler for all Task CRUD operations in the Inventory (Project Management)
    /// microservice. Serves as the HTTP API Gateway v2 entry point for task creation, retrieval,
    /// status management, watch/unwatch, timelog start/stop, comment management, and the
    /// Step Functions-triggered scheduled task starting job.
    ///
    /// Replaces the monolith's task-related endpoints from:
    ///   - ProjectController.cs (TaskSetStatus, TaskSetWatch, StartTimeLog, StopTimeLog)
    ///   - ProjectController.cs (CreateNewPcPostListItem, DeletePcPostListItem)
    ///   - StartTasksOnStartDate.cs (scheduled job for auto-starting tasks)
    ///   - Hooks/Api/Task.cs (hook delegation to TaskService methods)
    ///
    /// This is NOT an MVC controller. It is a Lambda handler receiving API Gateway v2 proxy events.
    /// Authentication is handled by API Gateway JWT authorizer; this handler extracts JWT claims
    /// from the request context for identity and authorization.
    ///
    /// API Routes:
    ///   POST   /v1/inventory/tasks                          → CreateTask
    ///   GET    /v1/inventory/tasks/{id}                     → GetTask
    ///   POST   /v1/inventory/tasks/{id}/status              → SetTaskStatus
    ///   POST   /v1/inventory/tasks/{id}/watch               → WatchTask
    ///   POST   /v1/inventory/tasks/{id}/timelog/start       → StartTimelog
    ///   POST   /v1/inventory/tasks/{id}/timelog/stop        → StopTimelog
    ///   POST   /v1/inventory/tasks/{id}/comments            → CreateComment
    ///   DELETE /v1/inventory/tasks/{id}/comments/{commentId} → DeleteComment
    ///   (Step Functions/EventBridge trigger)                 → StartTasksOnStartDate
    ///   GET    /v1/inventory/tasks/health                   → HealthCheck
    /// </summary>
    public class TaskHandler
    {
        #region Fields and Constants

        private readonly ITaskService _taskService;
        private readonly IInventoryRepository _repository;
        private readonly ILogger<TaskHandler> _logger;

        /// <summary>
        /// Hard-coded "In Progress" status GUID preserved from source:
        /// StartTasksOnStartDate.cs line 23 — used when auto-starting tasks.
        /// </summary>
        private static readonly Guid InProgressStatusId = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");

        /// <summary>
        /// Hard-coded "Not Started" status GUID preserved from source:
        /// TaskService.cs line 548 — used for filtering tasks that need starting.
        /// </summary>
        private static readonly Guid NotStartedStatusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");

        /// <summary>
        /// System user ID used for scheduled operations (no JWT context).
        /// Matches source SystemIds.SystemUserId from Definitions.cs.
        /// </summary>
        private static readonly Guid SystemUserId = new Guid("bdc56420-caf0-4030-8a0e-d264f6f47b04");

        /// <summary>
        /// Shared JSON serializer options configured for snake_case property naming
        /// and AOT-compatible System.Text.Json serialization.
        /// Replaces Newtonsoft.Json per import transformation rules for .NET 9 Native AOT.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// Standard CORS and content-type headers applied to every API Gateway response.
        /// Per AAP requirements for cross-origin access and JSON content negotiation.
        /// </summary>
        private static readonly Dictionary<string, string> StandardResponseHeaders = new()
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS" },
            { "Access-Control-Allow-Headers", "Content-Type,Authorization,X-Correlation-Id,X-Idempotency-Key" }
        };

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor invoked by the AWS Lambda runtime.
        /// Builds the full DI container registering AWS SDK clients (DynamoDB, SNS),
        /// application services (TaskService, InventoryRepository), IConfiguration,
        /// and structured JSON logging.
        /// AWS_ENDPOINT_URL is respected for LocalStack dual-target compatibility (AAP §0.7.6).
        /// </summary>
        public TaskHandler()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            var serviceProvider = services.BuildServiceProvider();
            _taskService = serviceProvider.GetRequiredService<ITaskService>();
            _repository = serviceProvider.GetRequiredService<IInventoryRepository>();
            _logger = serviceProvider.GetRequiredService<ILogger<TaskHandler>>();
        }

        /// <summary>
        /// DI constructor for unit testing with pre-configured dependencies.
        /// Allows injection of mocked ITaskService and ILogger for isolated test scenarios
        /// without Lambda runtime or AWS SDK dependencies.
        /// </summary>
        /// <param name="taskService">Business logic service consolidating all task operations.</param>
        /// <param name="logger">Structured logger for correlation-ID tracking.</param>
        public TaskHandler(ITaskService taskService, ILogger<TaskHandler> logger, IInventoryRepository? repository = null)
        {
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _repository = repository!;
        }

        #endregion

        #region Lambda Handlers

        /// <summary>
        /// Lambda handler for POST /v1/inventory/tasks — creates a new task.
        ///
        /// Source mapping:
        ///   - Pre-create validation: TaskService.PreCreateRecordPageHookLogic() (lines 300-330)
        ///     Validates project is specified (exactly one project relation)
        ///   - Record creation: RecordManager.CreateRecord("task", record) via repository
        ///   - Post-create: TaskService.PostCreateApiHookLogic() (lines 332-394)
        ///     Calculates key field, seeds initial watchers, creates activity feed, publishes SNS event
        ///
        /// Flow:
        ///   1. Extract JWT user ID (replaces SecurityContext.CurrentUser.Id)
        ///   2. Deserialize request body to Task model
        ///   3. Validate via ITaskService.ValidateTaskCreation (pre-hook)
        ///   4. Persist via ITaskService.CreateTaskAsync
        ///   5. Execute post-creation hooks via ITaskService.PostCreateTaskAsync
        ///   6. Return 201 Created with task data
        /// </summary>

        /// <summary>
        /// Single entry point for managed .NET Lambda runtime (dotnet9).
        /// Routes API Gateway HTTP API v2 requests to the appropriate handler method
        /// based on HTTP method and request path.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

            // Extract the proxy path segment from {proxy+} parameter
            var proxy = "";
            if (request.PathParameters != null &&
                request.PathParameters.TryGetValue("proxy", out var proxyVal))
                proxy = proxyVal ?? "";

            var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var resource = segments.Length > 0 ? segments[0].ToLowerInvariant() : "";

            _logger.LogInformation(
                "FunctionHandler routing: method={Method}, proxy={Proxy}, resource={Resource}, segCount={SegCount}",
                method, proxy, resource, segments.Length);

            // ── /health ────────────────────────────────────────────────
            if (resource == "health")
                return await HealthCheck(request, context);

            // ── /projects ──────────────────────────────────────────────
            if (resource == "projects")
            {
                if (method == "GET")
                    return await ListProjects(request, context);
                return ErrorResponse(405, "Method not allowed for /projects");
            }

            // ── /tasks ─────────────────────────────────────────────────
            if (resource == "tasks")
            {
                var taskIdStr = segments.Length > 1 ? segments[1] : null;
                var subResource = segments.Length > 2 ? segments[2].ToLowerInvariant() : null;

                switch (method)
                {
                    case "GET":
                        if (taskIdStr != null && Guid.TryParse(taskIdStr, out _))
                            return await GetTask(request, context);
                        // List tasks (no ID)
                        return await ListTasks(request, context);

                    case "POST":
                        if (subResource == "status")
                            return await SetTaskStatus(request, context);
                        if (subResource == "watch")
                            return await WatchTask(request, context);
                        if (subResource == "start-timelog")
                            return await StartTimelog(request, context);
                        if (subResource == "stop-timelog")
                            return await StopTimelog(request, context);
                        return await CreateTask(request, context);

                    case "PUT":
                        return await UpdateTask(request, context);

                    case "DELETE":
                        return await DeleteTask(request, context);

                    default:
                        return ErrorResponse(405, "Method not allowed for /tasks");
                }
            }

            // ── /timelogs ──────────────────────────────────────────────
            if (resource == "timelogs")
            {
                var subResource = segments.Length > 1 ? segments[1].ToLowerInvariant() : null;

                switch (method)
                {
                    case "GET":
                        if (subResource == "summary")
                            return await GetTimelogSummary(request, context);
                        return await ListTimelogs(request, context);

                    case "POST":
                        return await CreateTimelog(request, context);

                    case "DELETE":
                        return await DeleteTimelog(request, context);

                    default:
                        return ErrorResponse(405, "Method not allowed for /timelogs");
                }
            }

            // ── /comments ──────────────────────────────────────────────
            if (resource == "comments")
            {
                switch (method)
                {
                    case "POST":
                        return await CreateComment(request, context);
                    case "DELETE":
                        return await DeleteComment(request, context);
                    default:
                        return ErrorResponse(405, "Method not allowed for /comments");
                }
            }

            // ── /feed ──────────────────────────────────────────────────
            if (resource == "feed")
            {
                if (method == "GET")
                    return await GetActivityFeed(request, context);
                return ErrorResponse(405, "Method not allowed for /feed");
            }

            // ── /dashboard ─────────────────────────────────────────────
            if (resource == "dashboard")
            {
                if (method == "GET")
                    return await GetDashboard(request, context);
                return ErrorResponse(405, "Method not allowed for /dashboard");
            }

            // Fallback — try to infer from method for backward compat
            _logger.LogWarning("Unrouted request: method={Method}, proxy={Proxy}", method, proxy);
            return ErrorResponse(404, $"Route not found: {method} /inventory/{proxy}");
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateTask(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "CreateTask invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Deserialize and validate request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    _logger.LogWarning("CreateTask: empty request body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Request body is required.");
                }

                // Parse raw JSON first so we can handle string labels for Guid fields
                // The frontend may send status_id/type_id as labels ("not started", "feature")
                // which cannot be deserialized directly to Guid?.
                JsonDocument? bodyDoc = null;
                try { bodyDoc = JsonDocument.Parse(request.Body); }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "CreateTask: invalid JSON body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Invalid JSON request body.");
                }

                // Preprocess the JSON: replace non-GUID status_id/type_id with null
                // so System.Text.Json can deserialize to Guid? without errors.
                string processedBody = request.Body;
                if (bodyDoc != null)
                {
                    var root = bodyDoc.RootElement;
                    bool needsPreprocessing = false;

                    if (root.TryGetProperty("status_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                    {
                        var sv = sid.GetString();
                        if (!string.IsNullOrWhiteSpace(sv) && !Guid.TryParse(sv, out _))
                            needsPreprocessing = true;
                    }
                    if (root.TryGetProperty("type_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                    {
                        var tv = tid.GetString();
                        if (!string.IsNullOrWhiteSpace(tv) && !Guid.TryParse(tv, out _))
                            needsPreprocessing = true;
                    }

                    if (needsPreprocessing)
                    {
                        // Rebuild the JSON with non-GUID status_id/type_id set to null
                        using var ms = new System.IO.MemoryStream();
                        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
                        {
                            writer.WriteStartObject();
                            foreach (var prop in root.EnumerateObject())
                            {
                                if ((prop.Name == "status_id" || prop.Name == "type_id") &&
                                    prop.Value.ValueKind == JsonValueKind.String)
                                {
                                    var val = prop.Value.GetString();
                                    if (!string.IsNullOrWhiteSpace(val) && !Guid.TryParse(val, out _))
                                    {
                                        writer.WriteNull(prop.Name);
                                        continue;
                                    }
                                }
                                prop.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        processedBody = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    }
                }

                Models.Task? task;
                try
                {
                    task = JsonSerializer.Deserialize<Models.Task>(processedBody, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "CreateTask: invalid JSON body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    bodyDoc?.Dispose();
                    return ErrorResponse(400, "Invalid JSON request body.");
                }

                if (task == null)
                {
                    bodyDoc?.Dispose();
                    return ErrorResponse(400, "Request body could not be deserialized to a valid Task.");
                }

                // Assign a new ID if not provided
                if (task.Id == Guid.Empty)
                {
                    task.Id = Guid.NewGuid();
                }

                // Set audit fields
                task.CreatedBy = currentUserId;
                task.CreatedOn = DateTime.UtcNow;

                // Build LRelatedRecords from frontend-provided project_id or $project_nn_task fields
                // The frontend sends { project_id: "guid", $project_nn_task: ["guid", ...] }
                if (string.IsNullOrWhiteSpace(task.LRelatedRecords) && bodyDoc != null)
                {
                    var root = bodyDoc.RootElement;
                    var projectIds = new List<string>();

                    // Check $project_nn_task array first
                    if (root.TryGetProperty("$project_nn_task", out var nnTask) && nnTask.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in nnTask.EnumerateArray())
                        {
                            var val = item.GetString();
                            if (!string.IsNullOrWhiteSpace(val) && Guid.TryParse(val, out _))
                                projectIds.Add(val);
                        }
                    }
                    // Check project_id scalar
                    if (root.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String)
                    {
                        var pidStr = pid.GetString();
                        if (!string.IsNullOrWhiteSpace(pidStr) && Guid.TryParse(pidStr, out _) && !projectIds.Contains(pidStr))
                            projectIds.Add(pidStr);
                    }

                    if (projectIds.Count > 0)
                    {
                        task.LRelatedRecords = JsonSerializer.Serialize(projectIds, JsonOptions);
                    }
                }

                // Resolve status_id and type_id from string labels if not valid GUIDs
                // The frontend may send labels like "not started" instead of GUIDs
                if (bodyDoc != null)
                {
                    await ResolveStatusAndTypeFromBody(task, bodyDoc.RootElement);
                }
                bodyDoc?.Dispose();

                // Step 3: Pre-hook validation — replaces IErpPreCreateRecordHook pipeline
                // Source: TaskService.PreCreateRecordPageHookLogic lines 300-330
                var errors = new List<string>();
                _taskService.ValidateTaskCreation(task, errors);
                if (errors.Any())
                {
                    _logger.LogWarning(
                        "CreateTask: validation failed with {ErrorCount} errors. CorrelationId: {CorrelationId}",
                        errors.Count, context.AwsRequestId);
                    return ErrorResponse(400, string.Join("; ", errors));
                }

                // Step 4: Persist the task — delegates to repository via service layer
                var createdTask = await _taskService.CreateTaskAsync(task);

                // Step 5: Post-hook processing — replaces IErpPostCreateRecordHook pipeline
                // Source: TaskService.PostCreateApiHookLogic lines 332-394
                // Sets key field via SetCalculationFields, seeds watchers, creates feed item, publishes SNS
                await _taskService.PostCreateTaskAsync(createdTask, currentUserId);

                _logger.LogInformation(
                    "CreateTask: successfully created task {TaskId} by user {UserId}. CorrelationId: {CorrelationId}",
                    createdTask.Id, currentUserId, context.AwsRequestId);

                return CreatedResponse("Task successfully created", createdTask);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "CreateTask: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "CreateTask: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "CreateTask: bad request. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateTask: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while creating the task.");
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/inventory/tasks/{id} — retrieves a single task by ID.
        ///
        /// Source mapping: TaskService.GetTask(taskId) (lines 93-104)
        /// Original EQL: SELECT * from task WHERE id = @taskId
        /// Now delegates to ITaskService.GetTaskAsync which uses DynamoDB repository.
        ///
        /// Returns 404 if task not found, matching source pattern (ProjectController.cs line 305).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetTask(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "GetTask invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract task ID from path parameter
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                {
                    _logger.LogWarning("GetTask: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                _logger.LogInformation(
                    "GetTask: taskId={TaskId}. CorrelationId: {CorrelationId}",
                    taskId, context.AwsRequestId);

                // Step 2: Retrieve the task — replaces EQL query
                var task = await _taskService.GetTaskAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("GetTask: task {TaskId} not found. CorrelationId: {CorrelationId}", taskId, context.AwsRequestId);
                    return ErrorResponse(404, "task not found");
                }

                _logger.LogInformation(
                    "GetTask: successfully retrieved task {TaskId}. CorrelationId: {CorrelationId}",
                    taskId, context.AwsRequestId);

                return SuccessResponse("Task retrieved successfully", task);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "GetTask: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetTask: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while retrieving the task.");
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/inventory/tasks/{id}/status — changes a task's status.
        ///
        /// Source mapping: ProjectController.TaskSetStatus() (lines 362-394)
        /// Original route: api/v3.0/p/project/task/status
        /// Now uses RESTful POST with path parameter for taskId and body for statusId.
        ///
        /// Pre-update hook: TaskService.PostPreUpdateApiHookLogic (lines 396-488) handles
        /// project change detection and watcher management.
        /// Post-update hook: TaskService.PostUpdateApiHookLogic (lines 490-532) recalculates
        /// key, ensures owner in watchers, publishes SNS event.
        ///
        /// Returns error if task not found or status already set (source lines 373, 378).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> SetTaskStatus(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "SetTaskStatus invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Extract task ID from path parameter
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                {
                    _logger.LogWarning("SetTaskStatus: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                // Step 3: Extract statusId from request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return ErrorResponse(400, "Request body is required.");
                }

                JsonElement bodyJson;
                try
                {
                    bodyJson = JsonSerializer.Deserialize<JsonElement>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "SetTaskStatus: invalid JSON body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Invalid JSON request body.");
                }

                if (!bodyJson.TryGetProperty("statusId", out var statusIdElement) &&
                    !bodyJson.TryGetProperty("StatusId", out statusIdElement) &&
                    !bodyJson.TryGetProperty("status_id", out statusIdElement))
                {
                    return ErrorResponse(400, "Field 'statusId' is required.");
                }

                if (!TryGetGuid(statusIdElement, out var statusId))
                {
                    return ErrorResponse(400, "Field 'statusId' must be a valid GUID.");
                }

                _logger.LogInformation(
                    "SetTaskStatus: taskId={TaskId}, statusId={StatusId}, userId={UserId}. CorrelationId: {CorrelationId}",
                    taskId, statusId, currentUserId, context.AwsRequestId);

                // Step 4: Validate task exists — source line 373
                var task = await _taskService.GetTaskAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("SetTaskStatus: task {TaskId} not found. CorrelationId: {CorrelationId}", taskId, context.AwsRequestId);
                    return ErrorResponse(404, "task not found");
                }

                // Step 5: Check status not already set — source line 378
                if (task.StatusId.HasValue && task.StatusId.Value == statusId)
                {
                    _logger.LogWarning("SetTaskStatus: task {TaskId} already has status {StatusId}. CorrelationId: {CorrelationId}", taskId, statusId, context.AwsRequestId);
                    return ErrorResponse(400, "status already set");
                }

                // Step 6: Update status — delegates to service → repository
                await _taskService.SetStatusAsync(taskId, statusId);

                // Step 7: Post-update hook — recalculates key, ensures watchers, publishes SNS
                // Refresh task after status update for post-hook processing
                var updatedTask = await _taskService.GetTaskAsync(taskId);
                if (updatedTask != null)
                {
                    await _taskService.PostUpdateTaskAsync(updatedTask, currentUserId);
                }

                _logger.LogInformation(
                    "SetTaskStatus: successfully updated task {TaskId} to status {StatusId}. CorrelationId: {CorrelationId}",
                    taskId, statusId, context.AwsRequestId);

                return SuccessResponse("Status updated", updatedTask);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "SetTaskStatus: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "SetTaskStatus: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SetTaskStatus: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while updating task status.");
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/inventory/tasks/{id}/watch — adds or removes a watcher on a task.
        ///
        /// Source mapping: ProjectController.TaskSetWatch() (lines 396-459)
        /// Original route: api/v3.0/p/project/task/watch
        /// Original parameters: [FromQuery]Guid? taskId, [FromQuery]Guid? userId, [FromQuery]bool startWatch = true
        /// Now uses RESTful POST with taskId from path, userId and startWatch from body.
        ///
        /// Source pattern:
        ///   - If userId is null, defaults to current user (source line 425)
        ///   - If startWatch=true, creates M:N relation (source line 436: RecordManager.CreateRelationManyToManyRecord)
        ///   - If startWatch=false, removes M:N relation (source line 443: RecordManager.RemoveRelationManyToManyRecord)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> WatchTask(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "WatchTask invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Extract task ID from path parameter
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                {
                    _logger.LogWarning("WatchTask: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                // Step 3: Parse optional userId and startWatch from body (defaults: userId=currentUser, startWatch=true)
                Guid watchUserId = currentUserId;
                bool startWatch = true;

                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    try
                    {
                        var bodyJson = JsonSerializer.Deserialize<JsonElement>(request.Body, JsonOptions);

                        // Optional userId override — source line 425: defaults to SecurityContext.CurrentUser.Id
                        if ((bodyJson.TryGetProperty("userId", out var userIdElement) ||
                             bodyJson.TryGetProperty("UserId", out userIdElement) ||
                             bodyJson.TryGetProperty("user_id", out userIdElement)) &&
                            TryGetGuid(userIdElement, out var parsedUserId))
                        {
                            watchUserId = parsedUserId;
                        }

                        // startWatch flag — source query parameter, default true
                        if (bodyJson.TryGetProperty("startWatch", out var startWatchElement) ||
                            bodyJson.TryGetProperty("StartWatch", out startWatchElement) ||
                            bodyJson.TryGetProperty("start_watch", out startWatchElement))
                        {
                            TryGetBoolean(startWatchElement, out startWatch);
                        }
                    }
                    catch (JsonException)
                    {
                        // Body parsing is optional; default values are used if body is malformed
                        _logger.LogWarning("WatchTask: could not parse body, using defaults. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    }
                }

                _logger.LogInformation(
                    "WatchTask: taskId={TaskId}, watchUserId={WatchUserId}, startWatch={StartWatch}. CorrelationId: {CorrelationId}",
                    taskId, watchUserId, startWatch, context.AwsRequestId);

                // Step 4: Validate task exists — source line 411
                var task = await _taskService.GetTaskAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("WatchTask: task {TaskId} not found. CorrelationId: {CorrelationId}", taskId, context.AwsRequestId);
                    return ErrorResponse(404, "task not found");
                }

                // Step 5: Add or remove watcher — source lines 436-447
                if (startWatch)
                {
                    // Replaces RecordManager.CreateRelationManyToManyRecord(watchRelation.Id, userId, taskId)
                    await _taskService.AddWatcherAsync(taskId, watchUserId);
                    _logger.LogInformation(
                        "WatchTask: added watcher {WatchUserId} to task {TaskId}. CorrelationId: {CorrelationId}",
                        watchUserId, taskId, context.AwsRequestId);
                    return SuccessResponse("Task watch started");
                }
                else
                {
                    // Replaces RecordManager.RemoveRelationManyToManyRecord(watchRelation.Id, userId, taskId)
                    await _taskService.RemoveWatcherAsync(taskId, watchUserId);
                    _logger.LogInformation(
                        "WatchTask: removed watcher {WatchUserId} from task {TaskId}. CorrelationId: {CorrelationId}",
                        watchUserId, taskId, context.AwsRequestId);
                    return SuccessResponse("Task watch stopped");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "WatchTask: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "WatchTask: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WatchTask: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while updating task watch status.");
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/inventory/tasks/{id}/timelog/start — starts a timelog timer on a task.
        ///
        /// Source mapping: ProjectController.StartTimeLog() (lines 295-326)
        /// Original route: api/v3.0/p/project/timelog/start
        /// Source took [FromQuery]Guid taskId, now from path parameter.
        ///
        /// Validates:
        ///   - Task exists (source line 301)
        ///   - No timelog already started (source line 310: task["timelog_started_on"] != null)
        ///
        /// Sets timelog_started_on = DateTime.UtcNow via service layer (source line 236).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> StartTimelog(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "StartTimelog invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract task ID from path parameter
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                {
                    _logger.LogWarning("StartTimelog: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                _logger.LogInformation(
                    "StartTimelog: taskId={TaskId}. CorrelationId: {CorrelationId}",
                    taskId, context.AwsRequestId);

                // Step 2: Validate task exists — source line 301
                var task = await _taskService.GetTaskAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("StartTimelog: task {TaskId} not found. CorrelationId: {CorrelationId}", taskId, context.AwsRequestId);
                    return ErrorResponse(404, "task not found");
                }

                // Step 3: Check timelog not already started — source line 310
                if (task.TimelogStartedOn != null)
                {
                    _logger.LogWarning(
                        "StartTimelog: timelog already active for task {TaskId} since {StartedOn}. CorrelationId: {CorrelationId}",
                        taskId, task.TimelogStartedOn, context.AwsRequestId);
                    return ErrorResponse(400, "timelog for the task already started");
                }

                // Step 4: Start the timelog — sets timelog_started_on = DateTime.UtcNow
                await _taskService.StartTaskTimelogAsync(taskId);

                _logger.LogInformation(
                    "StartTimelog: successfully started timelog for task {TaskId}. CorrelationId: {CorrelationId}",
                    taskId, context.AwsRequestId);

                return SuccessResponse("Log Started");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "StartTimelog: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "StartTimelog: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StartTimelog: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while starting the timelog.");
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/inventory/tasks/{id}/timelog/stop — stops a running timelog timer on a task.
        ///
        /// Source mapping: ProjectController.StopTimeLog() (lines 328-360, COMMENTED OUT in source)
        /// The source endpoint was commented out but the schema requires this route.
        ///
        /// Validates:
        ///   - Task exists
        ///   - A timelog is currently active (timelog_started_on is not null)
        ///
        /// Clears timelog_started_on = null via service layer (source line 247).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> StopTimelog(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "StopTimelog invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract task ID from path parameter
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                {
                    _logger.LogWarning("StopTimelog: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                _logger.LogInformation(
                    "StopTimelog: taskId={TaskId}. CorrelationId: {CorrelationId}",
                    taskId, context.AwsRequestId);

                // Step 2: Validate task exists
                var task = await _taskService.GetTaskAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("StopTimelog: task {TaskId} not found. CorrelationId: {CorrelationId}", taskId, context.AwsRequestId);
                    return ErrorResponse(404, "task not found");
                }

                // Step 3: Check timelog is currently active (must have a started time)
                if (task.TimelogStartedOn == null)
                {
                    _logger.LogWarning(
                        "StopTimelog: no active timelog for task {TaskId}. CorrelationId: {CorrelationId}",
                        taskId, context.AwsRequestId);
                    return ErrorResponse(400, "timelog for the task not started");
                }

                // Step 4: Stop the timelog — clears timelog_started_on to null
                await _taskService.StopTaskTimelogAsync(taskId);

                _logger.LogInformation(
                    "StopTimelog: successfully stopped timelog for task {TaskId}. CorrelationId: {CorrelationId}",
                    taskId, context.AwsRequestId);

                return SuccessResponse("Log Stopped");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "StopTimelog: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "StopTimelog: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StopTimelog: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while stopping the timelog.");
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/inventory/tasks/{id}/comments — creates a new comment on a task.
        ///
        /// Source mapping: ProjectController.CreateNewPcPostListItem() (lines 56-140)
        /// Original route: api/v3.0/p/project/pc-post-list/create (was POST with body)
        ///
        /// Flow:
        ///   1. Extract JWT user ID
        ///   2. Extract taskId from path parameter (was relatedRecordId in source — line 65)
        ///   3. Parse body for: parentId (optional), body (string), relatedRecords (optional list)
        ///   4. Build Comment model with Id, CreatedBy, CreatedOn, Body, ParentId, scope, relatedRecords
        ///   5. Persist via ITaskService.CreateCommentAsync
        ///   6. Execute pre-hook: HandleCommentCreationPreHookAsync (feed creation, watcher management)
        ///   7. Execute post-hook: HandleCommentCreationPostHookAsync (add author to watchers)
        ///   8. Return 201 with comment data
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateComment(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "CreateComment invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Extract taskId from path parameter
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                {
                    _logger.LogWarning("CreateComment: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                // Step 3: Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return ErrorResponse(400, "Request body is required.");
                }

                JsonElement bodyJson;
                try
                {
                    bodyJson = JsonSerializer.Deserialize<JsonElement>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "CreateComment: invalid JSON body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Invalid JSON request body.");
                }

                // Parse body text (required) — source line 102
                string commentBody = string.Empty;
                if (bodyJson.TryGetProperty("body", out var bodyElement) ||
                    bodyJson.TryGetProperty("Body", out bodyElement))
                {
                    commentBody = bodyElement.GetString() ?? string.Empty;
                }

                // Parse parentId (optional Guid) — source line 78
                Guid? parentId = null;
                if ((bodyJson.TryGetProperty("parentId", out var parentElement) ||
                     bodyJson.TryGetProperty("ParentId", out parentElement) ||
                     bodyJson.TryGetProperty("parent_id", out parentElement)) &&
                    TryGetGuid(parentElement, out var parsedParentId))
                {
                    parentId = parsedParentId;
                }

                // Parse relatedRecords (optional) — source line 90: JsonConvert.DeserializeObject<List<Guid>>
                var relatedRecords = new List<Guid> { taskId };
                if (bodyJson.TryGetProperty("relatedRecords", out var relatedElement) ||
                    bodyJson.TryGetProperty("RelatedRecords", out relatedElement) ||
                    bodyJson.TryGetProperty("related_records", out relatedElement))
                {
                    var additionalRecords = ParseGuidList(relatedElement);
                    foreach (var rec in additionalRecords)
                    {
                        if (!relatedRecords.Contains(rec))
                        {
                            relatedRecords.Add(rec);
                        }
                    }
                }

                // Build scope — source comment creation used "projects" scope
                var scope = new List<string> { "projects" };

                var commentId = Guid.NewGuid();

                _logger.LogInformation(
                    "CreateComment: commentId={CommentId}, taskId={TaskId}, userId={UserId}, hasParent={HasParent}. CorrelationId: {CorrelationId}",
                    commentId, taskId, currentUserId, parentId.HasValue, context.AwsRequestId);

                // Step 5: Persist the comment
                await _taskService.CreateCommentAsync(
                    id: commentId,
                    createdBy: currentUserId,
                    createdOn: DateTime.UtcNow,
                    body: commentBody,
                    parentId: parentId,
                    scope: scope,
                    relatedRecords: relatedRecords);

                // Build Comment model for hook processing
                var comment = new Comment
                {
                    Id = commentId,
                    Body = commentBody,
                    ParentId = parentId,
                    CreatedBy = currentUserId,
                    CreatedOn = DateTime.UtcNow,
                    LScope = JsonSerializer.Serialize(scope),
                    LRelatedRecords = JsonSerializer.Serialize(relatedRecords)
                };

                // Step 6: Pre-hook — feed item creation, watcher management
                var commentErrors = new List<string>();
                await _taskService.HandleCommentCreationPreHookAsync("comment", comment, currentUserId, commentErrors);
                if (commentErrors.Any())
                {
                    _logger.LogWarning(
                        "CreateComment: pre-hook validation failed with {ErrorCount} errors. CorrelationId: {CorrelationId}",
                        commentErrors.Count, context.AwsRequestId);
                    // Non-blocking: log warnings but don't fail the request (pre-hook errors are advisory)
                }

                // Step 7: Post-hook — add author to watchers
                await _taskService.HandleCommentCreationPostHookAsync("comment", comment, currentUserId);

                _logger.LogInformation(
                    "CreateComment: successfully created comment {CommentId} on task {TaskId}. CorrelationId: {CorrelationId}",
                    commentId, taskId, context.AwsRequestId);

                return CreatedResponse("Comment successfully created", comment);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "CreateComment: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "CreateComment: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "CreateComment: bad request. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateComment: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while creating the comment.");
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/inventory/tasks/{id}/comments/{commentId} — deletes a comment.
        ///
        /// Source mapping: ProjectController.DeletePcPostListItem() (lines 142-175)
        /// Original route: api/v3.0/p/project/pc-post-list/delete (was POST with body ID)
        /// Now uses RESTful DELETE with commentId path parameter.
        ///
        /// Validates author-only deletion + cascading child comment deletion via service layer.
        /// Source pattern: CommentService.Delete() checks created_by == currentUserId.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteComment(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "DeleteComment invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Extract commentId from path parameter
                // Source line 149: record["id"] was from body — now from path
                if (!TryGetPathParameterGuid(request, "commentId", out var commentId))
                {
                    _logger.LogWarning("DeleteComment: invalid or missing commentId path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'commentId' is required and must be a valid GUID.");
                }

                _logger.LogInformation(
                    "DeleteComment: commentId={CommentId}, userId={UserId}. CorrelationId: {CorrelationId}",
                    commentId, currentUserId, context.AwsRequestId);

                // Step 3: Delete the comment — validates author-only + cascading child deletion
                await _taskService.DeleteCommentAsync(commentId, currentUserId);

                _logger.LogInformation(
                    "DeleteComment: successfully deleted comment {CommentId}. CorrelationId: {CorrelationId}",
                    commentId, context.AwsRequestId);

                return SuccessResponse("Comment successfully deleted");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "DeleteComment: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // Catches "Only the author can delete" from service layer
                _logger.LogWarning(ex, "DeleteComment: permission denied. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(403, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "DeleteComment: bad request. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteComment: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while deleting the comment.");
            }
        }

        /// <summary>
        /// Step Functions/EventBridge triggered handler for auto-starting tasks whose start date has arrived.
        ///
        /// Source mapping: StartTasksOnStartDate.Execute(JobContext context) (lines 14-31)
        /// Original: Scheduled job in ErpJob framework with Job attribute:
        ///   [Job("3D18B8D8-74B8-45B1-B121-9582F7B8A4F4", "Start tasks on start_date", true, JobPriority.Low)]
        ///
        /// Runs as system operation (no JWT context) — replaces SecurityContext.OpenSystemScope().
        /// Queries tasks where status = NotStarted (f3fdd750-...) AND start_time &lt;= today,
        /// then sets each to In Progress (20d73f63-...).
        ///
        /// Returns a JSON summary string (not an API Gateway response) since this is invoked
        /// by Step Functions, not API Gateway.
        /// </summary>
        public async Task<string> StartTasksOnStartDate(ILambdaContext context)
        {
            _logger.LogInformation(
                "StartTasksOnStartDate invoked. CorrelationId: {CorrelationId}",
                context.AwsRequestId);

            try
            {
                // Step 1: Get tasks that need starting
                // Replaces EQL: SELECT * FROM task WHERE status_id = @notStartedStatus AND start_time <= @today
                var tasks = await _taskService.GetTasksThatNeedStartingAsync();

                _logger.LogInformation(
                    "StartTasksOnStartDate: found {Count} tasks needing status change. CorrelationId: {CorrelationId}",
                    tasks.Count, context.AwsRequestId);

                int tasksStarted = 0;
                var failedTasks = new List<string>();

                // Step 2: Set each task to "In Progress" status
                foreach (var task in tasks)
                {
                    try
                    {
                        // Source line 23: hard-coded "In Progress" status GUID
                        await _taskService.SetStatusAsync(task.Id, InProgressStatusId);
                        tasksStarted++;

                        _logger.LogInformation(
                            "StartTasksOnStartDate: task {TaskId} set to In Progress. CorrelationId: {CorrelationId}",
                            task.Id, context.AwsRequestId);
                    }
                    catch (Exception ex)
                    {
                        // Source lines 25-27: throw on failure
                        var errorMsg = $"Failed to start task {task.Id}: {ex.Message}";
                        failedTasks.Add(errorMsg);
                        _logger.LogError(ex,
                            "StartTasksOnStartDate: failed to update task {TaskId}. CorrelationId: {CorrelationId}",
                            task.Id, context.AwsRequestId);
                    }
                }

                // Build summary result for Step Functions
                var result = new Dictionary<string, object>
                {
                    { "tasksStarted", tasksStarted },
                    { "totalFound", tasks.Count },
                    { "failures", failedTasks },
                    { "timestamp", DateTime.UtcNow.ToString("o") },
                    { "correlationId", context.AwsRequestId }
                };

                if (failedTasks.Any())
                {
                    _logger.LogWarning(
                        "StartTasksOnStartDate: completed with {FailureCount} failures out of {Total}. CorrelationId: {CorrelationId}",
                        failedTasks.Count, tasks.Count, context.AwsRequestId);
                }
                else
                {
                    _logger.LogInformation(
                        "StartTasksOnStartDate: completed successfully. {Count} tasks started. CorrelationId: {CorrelationId}",
                        tasksStarted, context.AwsRequestId);
                }

                return JsonSerializer.Serialize(result, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "StartTasksOnStartDate: unhandled error. CorrelationId: {CorrelationId}",
                    context.AwsRequestId);

                var errorResult = new Dictionary<string, object>
                {
                    { "tasksStarted", 0 },
                    { "error", ex.Message },
                    { "timestamp", DateTime.UtcNow.ToString("o") },
                    { "correlationId", context.AwsRequestId }
                };
                return JsonSerializer.Serialize(errorResult, JsonOptions);
            }
        }

        /// <summary>
        // ═══════════════════════════════════════════════════════════════════
        // New routing endpoints for full E2E support
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>GET /v1/inventory/projects — list all projects</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ListProjects(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("ListProjects invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                var projects = await _repository.GetAllProjectsAsync();
                return SuccessResponse("Projects retrieved", projects);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ListProjects error");
                return ErrorResponse(500, "Failed to list projects.");
            }
        }

        /// <summary>GET /v1/inventory/tasks — list/filter tasks</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ListTasks(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("ListTasks invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                Guid? projectId = null;
                Guid? userId = null;
                string? status = null;
                int limit = 50;

                if (request.QueryStringParameters != null)
                {
                    if (request.QueryStringParameters.TryGetValue("projectId", out var pid) && Guid.TryParse(pid, out var pguid))
                        projectId = pguid;
                    if (request.QueryStringParameters.TryGetValue("userId", out var uid) && Guid.TryParse(uid, out var uguid))
                        userId = uguid;
                    if (request.QueryStringParameters.TryGetValue("status", out var s))
                        status = s;
                    if (request.QueryStringParameters.TryGetValue("pageSize", out var ps) && int.TryParse(ps, out var psi))
                        limit = Math.Min(psi, 200);
                }

                var tasks = await _taskService.GetTaskQueueAsync(projectId, userId, Models.TasksDueType.All, limit);
                // If status filter specified, filter after retrieval
                if (!string.IsNullOrEmpty(status))
                {
                    var statuses = await _taskService.GetTaskStatusesAsync();
                    var targetStatus = statuses.FirstOrDefault(st =>
                        string.Equals(st.Label, status, StringComparison.OrdinalIgnoreCase));
                    if (targetStatus != null)
                    {
                        tasks = tasks.Where(t => t.StatusId == targetStatus.Id).ToList();
                    }
                }

                var result = new { records = tasks, totalCount = tasks.Count };
                return SuccessResponse("Tasks retrieved", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ListTasks error");
                return ErrorResponse(500, "Failed to list tasks.");
            }
        }

        /// <summary>PUT /v1/inventory/tasks/{id} — update a task</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> UpdateTask(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("UpdateTask invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                    return ErrorResponse(400, "Task ID is required.");

                var existing = await _taskService.GetTaskAsync(taskId);
                if (existing == null) return ErrorResponse(404, "Task not found.");

                var currentUserId = GetCurrentUserId(request);
                if (string.IsNullOrWhiteSpace(request.Body))
                    return ErrorResponse(400, "Request body is required.");

                var updates = JsonSerializer.Deserialize<JsonElement>(request.Body);
                if (updates.ValueKind == JsonValueKind.Object)
                {
                    if (updates.TryGetProperty("subject", out var sub) && sub.ValueKind == JsonValueKind.String)
                        existing.Subject = sub.GetString() ?? existing.Subject;
                    if (updates.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                    {
                        var statuses = await _taskService.GetTaskStatusesAsync();
                        var match = statuses.FirstOrDefault(s => string.Equals(s.Label, st.GetString(), StringComparison.OrdinalIgnoreCase));
                        if (match != null) existing.StatusId = match.Id;
                    }
                    if (updates.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.String)
                        existing.Priority = pr.GetString() ?? existing.Priority;
                    if (updates.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
                        existing.Body = desc.GetString() ?? existing.Body;
                    if (updates.TryGetProperty("body", out var bd) && bd.ValueKind == JsonValueKind.String)
                        existing.Body = bd.GetString() ?? existing.Body;
                }

                existing.LastModifiedOn = DateTime.UtcNow;
                existing.LastModifiedBy = currentUserId;

                var errors = new List<string>();
                await _taskService.PreUpdateTaskAsync(existing, currentUserId, errors);
                if (errors.Any()) return ErrorResponse(400, string.Join("; ", errors));

                await _repository.UpdateTaskAsync(existing);
                await _taskService.PostUpdateTaskAsync(existing, currentUserId);

                return SuccessResponse("Task updated", existing);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateTask error");
                return ErrorResponse(500, "Failed to update task.");
            }
        }

        /// <summary>DELETE /v1/inventory/tasks/{id} — delete a task</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteTask(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("DeleteTask invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                if (!TryGetPathParameterGuid(request, "id", out var taskId))
                    return ErrorResponse(400, "Task ID is required.");

                var existing = await _taskService.GetTaskAsync(taskId);
                if (existing == null) return ErrorResponse(404, "Task not found.");

                await _repository.DeleteTaskAsync(taskId);
                return SuccessResponse("Task deleted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteTask error");
                return ErrorResponse(500, "Failed to delete task.");
            }
        }

        /// <summary>POST /v1/inventory/timelogs — create a timelog entry</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateTimelog(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("CreateTimelog invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                var currentUserId = GetCurrentUserId(request);
                if (string.IsNullOrWhiteSpace(request.Body))
                    return ErrorResponse(400, "Request body is required.");

                var body = JsonSerializer.Deserialize<JsonElement>(request.Body);
                int minutes = 0;
                bool isBillable = false;
                string description = "";
                DateTime loggedOn = DateTime.UtcNow;
                Guid? taskId = null;
                var scope = new List<string>();
                var relatedRecords = new List<Guid>();

                if (body.TryGetProperty("minutes", out var m)) minutes = m.GetInt32();
                if (body.TryGetProperty("hours", out var h) && h.ValueKind == JsonValueKind.String && decimal.TryParse(h.GetString(), out var hrs)) minutes = (int)(hrs * 60);
                if (body.TryGetProperty("is_billable", out var ib)) isBillable = ib.ValueKind == JsonValueKind.True;
                if (body.TryGetProperty("isBillable", out var ib2)) isBillable = ib2.ValueKind == JsonValueKind.True;
                if (body.TryGetProperty("body", out var bd)) description = bd.GetString() ?? "";
                if (body.TryGetProperty("description", out var dd)) description = dd.GetString() ?? "";
                if (body.TryGetProperty("logged_on", out var lo) && DateTime.TryParse(lo.GetString(), out var lop)) loggedOn = lop;
                if (body.TryGetProperty("date", out var dt) && DateTime.TryParse(dt.GetString(), out var dtp)) loggedOn = dtp;
                if (body.TryGetProperty("taskId", out var ti) && Guid.TryParse(ti.GetString(), out var tig)) { taskId = tig; relatedRecords.Add(tig); }
                if (body.TryGetProperty("task_id", out var ti2) && Guid.TryParse(ti2.GetString(), out var tig2)) { taskId = tig2; relatedRecords.Add(tig2); }

                scope.Add("projects");

                await _taskService.CreateTimelogAsync(null, currentUserId, DateTime.UtcNow, loggedOn, minutes, isBillable, description, scope, relatedRecords);

                return CreatedResponse("Timelog created", new { minutes, isBillable, loggedOn, taskId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateTimelog error");
                return ErrorResponse(500, "Failed to create timelog.");
            }
        }

        /// <summary>GET /v1/inventory/timelogs — list timelogs</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ListTimelogs(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("ListTimelogs invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                Guid? projectId = null;
                Guid? userId = null;
                var start = DateTime.UtcNow.AddMonths(-1);
                var end = DateTime.UtcNow.AddDays(1);

                if (request.QueryStringParameters != null)
                {
                    if (request.QueryStringParameters.TryGetValue("projectId", out var pid) && Guid.TryParse(pid, out var pg))
                        projectId = pg;
                    if (request.QueryStringParameters.TryGetValue("userId", out var uid) && Guid.TryParse(uid, out var ug))
                        userId = ug;
                    if (request.QueryStringParameters.TryGetValue("startDate", out var sd) && DateTime.TryParse(sd, out var sdp))
                        start = sdp;
                    if (request.QueryStringParameters.TryGetValue("endDate", out var ed) && DateTime.TryParse(ed, out var edp))
                        end = edp;
                }

                var timelogs = await _taskService.GetTimelogsForPeriodAsync(projectId, userId, start, end);
                var result = new { records = timelogs, totalCount = timelogs.Count };
                return SuccessResponse("Timelogs retrieved", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ListTimelogs error");
                return ErrorResponse(500, "Failed to list timelogs.");
            }
        }

        /// <summary>DELETE /v1/inventory/timelogs/{id} — delete timelog</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteTimelog(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("DeleteTimelog invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                if (!TryGetPathParameterGuid(request, "id", out var timelogId))
                    return ErrorResponse(400, "Timelog ID is required.");

                var currentUserId = GetCurrentUserId(request);
                await _taskService.DeleteTimelogAsync(timelogId, currentUserId);
                return SuccessResponse("Timelog deleted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteTimelog error");
                return ErrorResponse(500, "Failed to delete timelog.");
            }
        }

        /// <summary>GET /v1/inventory/timelogs/summary — timelog aggregations</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetTimelogSummary(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("GetTimelogSummary invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                int year = DateTime.UtcNow.Year;
                int month = DateTime.UtcNow.Month;
                Guid? accountId = null;

                if (request.QueryStringParameters != null)
                {
                    if (request.QueryStringParameters.TryGetValue("year", out var y) && int.TryParse(y, out var yi)) year = yi;
                    if (request.QueryStringParameters.TryGetValue("month", out var m) && int.TryParse(m, out var mi)) month = mi;
                    if (request.QueryStringParameters.TryGetValue("accountId", out var aid) && Guid.TryParse(aid, out var ag)) accountId = ag;
                }

                var data = await _taskService.GetTimelogReportDataAsync(year, month, accountId);
                return SuccessResponse("Timelog summary", data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetTimelogSummary error");
                return ErrorResponse(500, "Failed to get timelog summary.");
            }
        }

        /// <summary>GET /v1/inventory/feed — activity feed</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetActivityFeed(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("GetActivityFeed invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                int limit = 50;
                string? recordId = null;

                if (request.QueryStringParameters != null)
                {
                    if (request.QueryStringParameters.TryGetValue("pageSize", out var ps) && int.TryParse(ps, out var psi)) limit = Math.Min(psi, 200);
                    if (request.QueryStringParameters.TryGetValue("recordId", out var rid)) recordId = rid;
                }

                List<Models.FeedItem> items;
                if (!string.IsNullOrEmpty(recordId))
                    items = await _repository.GetFeedItemsByRelatedRecordAsync(recordId, limit);
                else
                {
                    var currentUserId = GetCurrentUserId(request);
                    items = await _repository.GetFeedItemsByUserAsync(currentUserId, limit);
                }
                var result = new { records = items, totalCount = items.Count };
                return SuccessResponse("Feed retrieved", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetActivityFeed error");
                return ErrorResponse(500, "Failed to get activity feed.");
            }
        }

        /// <summary>GET /v1/inventory/dashboard — project dashboard stats</summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetDashboard(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("GetDashboard invoked. CorrelationId: {Id}", context.AwsRequestId);
            try
            {
                Guid? projectId = null;
                if (request.QueryStringParameters != null &&
                    request.QueryStringParameters.TryGetValue("projectId", out var pid) &&
                    Guid.TryParse(pid, out var pg))
                    projectId = pg;

                var tasks = await _taskService.GetTaskQueueAsync(projectId, null, Models.TasksDueType.All);
                var statuses = await _taskService.GetTaskStatusesAsync();

                var dashboard = new Dictionary<string, object>
                {
                    ["totalTasks"] = tasks.Count,
                    ["statusBreakdown"] = statuses.Select(s => new
                    {
                        status = s.Label,
                        count = tasks.Count(t => t.StatusId == s.Id)
                    }),
                    ["priorityBreakdown"] = tasks
                        .GroupBy(t => t.Priority ?? "unset")
                        .Select(g => new { priority = g.Key, count = g.Count() })
                };

                return SuccessResponse("Dashboard data", dashboard);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDashboard error");
                return ErrorResponse(500, "Failed to get dashboard data.");
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/inventory/tasks/health — service health check endpoint.
        /// Returns a simple healthy status for load balancer and monitoring integration.
        /// Does not require authentication (per AAP §0.8.5 health check requirement).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HealthCheck(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("HealthCheck invoked. CorrelationId: {CorrelationId}", context.AwsRequestId);

            var healthResponse = new Dictionary<string, object>
            {
                { "status", "healthy" },
                { "service", "inventory-task" },
                { "timestamp", DateTime.UtcNow.ToString("o") },
                { "correlationId", context.AwsRequestId }
            };

            return await System.Threading.Tasks.Task.FromResult(new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(healthResponse, JsonOptions),
                Headers = new Dictionary<string, string>(StandardResponseHeaders)
            });
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Extracts the authenticated user's ID from JWT claims in the API Gateway v2 authorizer context.
        /// Replaces SecurityContext.CurrentUser.Id used throughout the monolith:
        ///   - ProjectController.cs line 200: SecurityContext.CurrentUser.Id with fallback to SystemIds.FirstUserId
        ///   - TaskService.cs: SecurityContext.CurrentUser in hook methods
        ///
        /// In the serverless architecture, ALL routes are behind API Gateway JWT authorizer,
        /// so the monolith's fallback to SystemIds.FirstUserId for guest/dev is removed.
        /// </summary>
        /// <param name="request">The API Gateway v2 proxy request containing JWT authorizer claims.</param>
        /// <returns>The authenticated user's GUID extracted from the "sub" claim.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown when JWT claims are missing or the "sub" claim is not a valid GUID.</exception>
        /// <summary>
        /// Resolves status_id and type_id from string labels (e.g., "not started", "bug")
        /// to their corresponding GUID identifiers when the frontend sends label strings
        /// instead of GUID values.
        /// </summary>
        private async System.Threading.Tasks.Task ResolveStatusAndTypeFromBody(Models.Task task, JsonElement root)
        {
            try
            {
                // Resolve status: check status_id or status field
                if (!task.StatusId.HasValue || task.StatusId == Guid.Empty)
                {
                    string? statusLabel = null;
                    if (root.TryGetProperty("status_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                        statusLabel = sid.GetString();
                    if (string.IsNullOrWhiteSpace(statusLabel) && root.TryGetProperty("status", out var sLabel) && sLabel.ValueKind == JsonValueKind.String)
                        statusLabel = sLabel.GetString();

                    if (!string.IsNullOrWhiteSpace(statusLabel) && !Guid.TryParse(statusLabel, out _))
                    {
                        // It's a label, not a GUID — look up
                        var statuses = await _repository.GetAllTaskStatusesAsync();
                        var matched = statuses.FirstOrDefault(s =>
                            string.Equals(s.Label, statusLabel, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                            task.StatusId = matched.Id;
                    }
                    else if (!string.IsNullOrWhiteSpace(statusLabel) && Guid.TryParse(statusLabel, out var parsedSid))
                    {
                        task.StatusId = parsedSid;
                    }
                }

                // Resolve type: check type_id or type field
                if (!task.TypeId.HasValue || task.TypeId == Guid.Empty)
                {
                    string? typeLabel = null;
                    if (root.TryGetProperty("type_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                        typeLabel = tid.GetString();
                    if (string.IsNullOrWhiteSpace(typeLabel) && root.TryGetProperty("type", out var tLabel) && tLabel.ValueKind == JsonValueKind.String)
                        typeLabel = tLabel.GetString();

                    if (!string.IsNullOrWhiteSpace(typeLabel) && !Guid.TryParse(typeLabel, out _))
                    {
                        // It's a label, not a GUID — look up
                        var types = await _repository.GetAllTaskTypesAsync();
                        var matched = types.FirstOrDefault(t =>
                            string.Equals(t.Label, typeLabel, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                            task.TypeId = matched.Id;
                    }
                    else if (!string.IsNullOrWhiteSpace(typeLabel) && Guid.TryParse(typeLabel, out var parsedTid))
                    {
                        task.TypeId = parsedTid;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve status/type labels to GUIDs");
            }
        }

        private Guid GetCurrentUserId(APIGatewayHttpApiV2ProxyRequest request)
        {
            // Try JWT authorizer claims first (native JWT authorizer)
            if (request.RequestContext?.Authorizer?.Jwt?.Claims != null)
            {
                var claims = request.RequestContext.Authorizer.Jwt.Claims;
                if (claims.TryGetValue("sub", out var sub) && Guid.TryParse(sub, out var userId))
                    return userId;
                if (claims.TryGetValue("userId", out var uid) && Guid.TryParse(uid, out var userId2))
                    return userId2;
                if (claims.TryGetValue("custom:erp_user_id", out var erpId) && Guid.TryParse(erpId, out var userId3))
                    return userId3;
            }

            // Try Lambda authorizer context (custom Lambda authorizer — used by LocalStack)
            if (request.RequestContext?.Authorizer?.Lambda != null)
            {
                foreach (var kv in request.RequestContext.Authorizer.Lambda)
                {
                    if (string.Equals(kv.Key, "userId", StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                    {
                        var val = kv.Value.ToString();
                        if (!string.IsNullOrWhiteSpace(val) && Guid.TryParse(val, out var lambdaUserId))
                            return lambdaUserId;
                    }
                }
            }

            throw new UnauthorizedAccessException("User identity not found in JWT claims.");
        }

        /// <summary>
        /// Builds a standard API response with the given status code and ResponseModel body.
        /// Uses System.Text.Json (NOT Newtonsoft.Json) for AOT compatibility per AAP requirements.
        /// Response includes standard CORS headers and snake_case JSON serialization.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse CreateResponse(int statusCode, ResponseModel body)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(body, JsonOptions),
                Headers = new Dictionary<string, string>(StandardResponseHeaders)
            };
        }

        /// <summary>
        /// Builds a 200 OK success response with optional data payload.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse SuccessResponse(string message, object? data = null)
        {
            return CreateResponse(200, new ResponseModel
            {
                Success = true,
                Message = message,
                Object = data
            });
        }

        /// <summary>
        /// Builds a 201 Created success response with optional data payload.
        /// Used for resource creation endpoints (CreateTask, CreateComment).
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse CreatedResponse(string message, object? data = null)
        {
            return CreateResponse(201, new ResponseModel
            {
                Success = true,
                Message = message,
                Object = data
            });
        }

        /// <summary>
        /// Builds an error response with the specified HTTP status code and error message.
        /// Error responses always have Success = false. Internal error details are NOT leaked
        /// to the caller per OWASP security requirements (AAP §0.8.3).
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse ErrorResponse(int statusCode, string message)
        {
            return CreateResponse(statusCode, new ResponseModel
            {
                Success = false,
                Message = message
            });
        }

        /// <summary>
        /// Attempts to extract a GUID path parameter from the API Gateway request.
        /// Returns false if the parameter is missing, empty, or not a valid GUID.
        /// </summary>
        private static bool TryGetPathParameterGuid(APIGatewayHttpApiV2ProxyRequest request, string paramName, out Guid value)
        {
            value = Guid.Empty;
            if (request.PathParameters != null)
            {
                if (request.PathParameters.TryGetValue(paramName, out var paramStr) &&
                    !string.IsNullOrWhiteSpace(paramStr))
                    return Guid.TryParse(paramStr, out value);
                // Fall back to {proxy+} parameter for HTTP API v2 catch-all routes.
                if (request.PathParameters.TryGetValue("proxy", out var proxy) &&
                    !string.IsNullOrEmpty(proxy))
                {
                    var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segments.Length - 1; i >= 0; i--)
                    {
                        if (Guid.TryParse(segments[i], out value))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Attempts to parse a JsonElement as a Guid value.
        /// Handles string representations of GUIDs.
        /// </summary>
        private static bool TryGetGuid(JsonElement element, out Guid value)
        {
            value = Guid.Empty;
            if (element.ValueKind == JsonValueKind.String)
            {
                return Guid.TryParse(element.GetString(), out value);
            }
            return false;
        }

        /// <summary>
        /// Attempts to parse a JsonElement as a Boolean value.
        /// Handles JSON true/false values and string representations ("true"/"false")
        /// matching the source pattern of Boolean.TryParse on form values.
        /// </summary>
        private static bool TryGetBoolean(JsonElement element, out bool value)
        {
            value = false;
            if (element.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }
            if (element.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }
            if (element.ValueKind == JsonValueKind.String)
            {
                return bool.TryParse(element.GetString(), out value);
            }
            return false;
        }

        /// <summary>
        /// Parses a JSON array element as a List of Guid values.
        /// Matches source pattern: JsonConvert.DeserializeObject&lt;List&lt;Guid&gt;&gt;()
        /// from ProjectController.cs line 90.
        /// Silently skips invalid entries to maintain robustness.
        /// </summary>
        private static List<Guid> ParseGuidList(JsonElement element)
        {
            var result = new List<Guid>();
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var guid))
                    {
                        result.Add(guid);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                // Handle the case where relatedRecords is passed as a JSON string (serialized array)
                var str = element.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<string>>(str);
                        if (parsed != null)
                        {
                            foreach (var s in parsed)
                            {
                                if (Guid.TryParse(s, out var guid))
                                {
                                    result.Add(guid);
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // If it's a single GUID string, try parsing it directly
                        if (Guid.TryParse(str, out var singleGuid))
                        {
                            result.Add(singleGuid);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Configures the DI service container for Lambda execution.
        /// Registers AWS SDK clients with LocalStack endpoint override when AWS_ENDPOINT_URL is set,
        /// all application services as singletons for Lambda execution lifetime,
        /// IConfiguration bound to environment variables, and structured JSON console logging
        /// for CloudWatch integration.
        ///
        /// Dependency registration order (mirrors TaskService constructor requirements):
        ///   IAmazonDynamoDB → IInventoryRepository (DynamoDB client + table name)
        ///   IAmazonSimpleNotificationService → ITaskService (SNS domain events)
        ///   IConfiguration → ITaskService (SNS topic ARN)
        ///   ILogger&lt;TaskService&gt; → ITaskService
        /// </summary>
        private static void ConfigureServices(IServiceCollection services)
        {
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            // Register IConfiguration from environment variables
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SNS:InventoryTopicArn"] = Environment.GetEnvironmentVariable("INVENTORY_EVENTS_TOPIC_ARN") ?? "arn:aws:sns:us-east-1:000000000000:inventory-events",
                ["DynamoDB:TableName"] = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME") ?? "inventory-table",
                ["AWS:EndpointUrl"] = endpointUrl ?? string.Empty
            });
            configBuilder.AddEnvironmentVariables();
            var configuration = configBuilder.Build();
            services.AddSingleton<IConfiguration>(configuration);

            // Register AWS SDK clients with LocalStack endpoint override when present
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new Amazon.DynamoDBv2.AmazonDynamoDBConfig { ServiceURL = endpointUrl }));
                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production: use default AWS credential chain and region
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
            }

            // Register application services in dependency order
            services.AddSingleton<IInventoryRepository, InventoryRepository>();
            services.AddSingleton<ITaskService, TaskService>();

            // Structured JSON logging for CloudWatch with correlation-ID scope support
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });
        }

        #endregion
    }
}
