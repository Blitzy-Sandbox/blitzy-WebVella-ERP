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

namespace WebVellaErp.Inventory.Functions
{
    /// <summary>
    /// AWS Lambda handler for all Timelog CRUD operations in the Inventory (Project Management)
    /// microservice. Serves as the HTTP API Gateway v2 entry point for timelog creation, deletion,
    /// period-based queries with filtering, and the atomic track-time (stop + log) workflow.
    ///
    /// Replaces the monolith's timelog-related endpoints:
    ///   - ProjectController.CreateTimelog() (lines 177-255)
    ///   - ProjectController.DeleteTimelog() (lines 257-293)
    ///   - TimeLogService.GetTimelogsForPeriod() (lines 85-106)
    ///   - TimeLogService.PostApplicationNodePageHookLogic() (lines 108-179) — track-time page hook
    ///   - TimeLogService.PreCreateApiHookLogic() (lines 181-278) — inline via ITaskService
    ///   - TimeLogService.PreDeleteApiHookLogic() (lines 281-367) — inline via ITaskService
    ///   - Hooks/Api/Timelog.cs — delegates to TimeLogService pre-hooks
    ///
    /// This is NOT an MVC controller. It is a Lambda handler receiving API Gateway v2 proxy events.
    /// Authentication is handled by API Gateway JWT authorizer; this handler extracts JWT claims
    /// from the request context for identity and authorization.
    ///
    /// API Routes:
    ///   POST   /v1/inventory/timelogs          → CreateTimelog
    ///   DELETE /v1/inventory/timelogs/{id}      → DeleteTimelog
    ///   GET    /v1/inventory/timelogs           → GetTimelogs (date range + optional project/user filters)
    ///   POST   /v1/inventory/timelogs/track     → TrackTime (atomic stop task timelog + create timelog)
    ///   GET    /v1/inventory/timelogs/health    → HealthCheck
    /// </summary>
    public class TimelogHandler
    {
        #region Fields and Constants

        private readonly ITaskService _taskService;
        private readonly ILogger<TimelogHandler> _logger;

        /// <summary>
        /// Shared JSON serializer options configured for snake_case property naming
        /// and AOT-compatible System.Text.Json serialization.
        /// Replaces Newtonsoft.Json per import transformation rules for .NET 9 Native AOT.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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
        public TimelogHandler()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            var serviceProvider = services.BuildServiceProvider();
            _taskService = serviceProvider.GetRequiredService<ITaskService>();
            _logger = serviceProvider.GetRequiredService<ILogger<TimelogHandler>>();
        }

        /// <summary>
        /// DI constructor for unit testing with pre-configured dependencies.
        /// Allows injection of mocked ITaskService and ILogger for isolated test scenarios
        /// without Lambda runtime or AWS SDK dependencies.
        /// </summary>
        /// <param name="taskService">Business logic service consolidating all timelog operations.</param>
        /// <param name="logger">Structured logger for correlation-ID tracking.</param>
        public TimelogHandler(ITaskService taskService, ILogger<TimelogHandler> logger)
        {
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #endregion

        #region Lambda Handlers

        /// <summary>
        /// Lambda handler for POST /v1/inventory/timelogs — creates a new timelog entry.
        ///
        /// Source mapping: ProjectController.CreateTimelog() (lines 177-255)
        /// Original route: api/v3.0/p/project/pc-timelog-list/create
        ///
        /// Request body fields:
        ///   - minutes (int, required) — source lines 205-212 via Int32.TryParse
        ///   - isBillable (bool, optional, default false) — source lines 214-221 via Boolean.TryParse
        ///   - loggedOn (DateTime, required) — source lines 223-227
        ///   - body (string, optional) — source lines 194-198
        ///   - relatedRecords (List&lt;Guid&gt;, optional) — source lines 189-193 via JsonConvert.DeserializeObject
        ///
        /// Flow:
        ///   1. Extract JWT user ID (replaces SecurityContext.CurrentUser.Id)
        ///   2. Parse and validate request body fields
        ///   3. Generate new record ID (source line 183: Guid.NewGuid())
        ///   4. Build scope ["projects"] (source line 186)
        ///   5. Call ITaskService.CreateTimelogAsync (replaces TimeLogService.Create)
        ///   6. Call ITaskService.HandleTimelogCreationHookAsync (replaces PreCreateApiHookLogic —
        ///      updates task aggregate minutes, clears timelog_started_on, creates activity feed)
        ///   7. Return 201 Created with timelog data
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateTimelog(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "CreateTimelog invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Deserialize and validate request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    _logger.LogWarning("CreateTimelog: empty request body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Request body is required.");
                }

                JsonElement bodyJson;
                try
                {
                    bodyJson = JsonSerializer.Deserialize<JsonElement>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "CreateTimelog: invalid JSON body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Invalid JSON request body.");
                }

                // Parse minutes (required) — source lines 205-212: Int32.TryParse
                if (!bodyJson.TryGetProperty("minutes", out var minutesElement) &&
                    !bodyJson.TryGetProperty("Minutes", out minutesElement))
                {
                    return ErrorResponse(400, "Field 'minutes' is required.");
                }
                if (!TryGetInt32(minutesElement, out var minutes))
                {
                    return ErrorResponse(400, "Field 'minutes' must be a valid integer.");
                }

                // Parse loggedOn (required) — source lines 223-227
                if (!bodyJson.TryGetProperty("loggedOn", out var loggedOnElement) &&
                    !bodyJson.TryGetProperty("LoggedOn", out loggedOnElement) &&
                    !bodyJson.TryGetProperty("logged_on", out loggedOnElement))
                {
                    return ErrorResponse(400, "Field 'loggedOn' is required.");
                }
                if (!TryGetDateTime(loggedOnElement, out var loggedOn))
                {
                    return ErrorResponse(400, "Field 'loggedOn' must be a valid ISO 8601 date.");
                }

                // Parse isBillable (optional, default false) — source lines 214-221: Boolean.TryParse
                bool isBillable = false;
                if (bodyJson.TryGetProperty("isBillable", out var isBillableElement) ||
                    bodyJson.TryGetProperty("IsBillable", out isBillableElement) ||
                    bodyJson.TryGetProperty("is_billable", out isBillableElement))
                {
                    TryGetBoolean(isBillableElement, out isBillable);
                }

                // Parse body (optional, default "") — source lines 194-198
                string body = string.Empty;
                if (bodyJson.TryGetProperty("body", out var bodyElement) ||
                    bodyJson.TryGetProperty("Body", out bodyElement))
                {
                    body = bodyElement.GetString() ?? string.Empty;
                }

                // Parse relatedRecords (optional) — source lines 189-193: JsonConvert.DeserializeObject<List<Guid>>
                var relatedRecords = new List<Guid>();
                if (bodyJson.TryGetProperty("relatedRecords", out var relatedElement) ||
                    bodyJson.TryGetProperty("RelatedRecords", out relatedElement) ||
                    bodyJson.TryGetProperty("related_records", out relatedElement))
                {
                    relatedRecords = ParseGuidList(relatedElement);
                }

                // Step 3: Generate new record ID — source line 183: Guid.NewGuid()
                var recordId = Guid.NewGuid();

                // Step 4: Build scope — source line 186: hard-coded "projects"
                var scope = new List<string> { "projects" };

                _logger.LogInformation(
                    "CreateTimelog: recordId={RecordId}, minutes={Minutes}, isBillable={IsBillable}, userId={UserId}. CorrelationId: {CorrelationId}",
                    recordId, minutes, isBillable, currentUserId, context.AwsRequestId);

                // Step 5: Create timelog — replaces TimeLogService.Create() (lines 20-60)
                await _taskService.CreateTimelogAsync(
                    id: recordId,
                    createdBy: currentUserId,
                    createdOn: DateTime.UtcNow,
                    loggedOn: loggedOn,
                    minutes: minutes,
                    isBillable: isBillable,
                    body: body,
                    scope: scope,
                    relatedRecords: relatedRecords);

                // Step 6: Execute pre-create hook logic — replaces TimeLogService.PreCreateApiHookLogic()
                // (lines 181-278): updates task x_billable_minutes/x_nonbillable_minutes,
                // clears timelog_started_on, creates activity feed item
                var createdTimelog = new Timelog
                {
                    Id = recordId,
                    CreatedBy = currentUserId,
                    CreatedOn = DateTime.UtcNow,
                    LoggedOn = loggedOn,
                    Minutes = minutes,
                    IsBillable = isBillable,
                    Body = body,
                    LScope = JsonSerializer.Serialize(scope),
                    LRelatedRecords = JsonSerializer.Serialize(relatedRecords)
                };

                await _taskService.HandleTimelogCreationHookAsync(createdTimelog, currentUserId);

                _logger.LogInformation(
                    "CreateTimelog: successfully created timelog {RecordId}. CorrelationId: {CorrelationId}",
                    recordId, context.AwsRequestId);

                // Step 7: Return 201 Created — source line 241: Success = true, Message = "..."
                return CreatedResponse("Timelog successfully created", createdTimelog);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "CreateTimelog: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "CreateTimelog: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "CreateTimelog: bad request. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateTimelog: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while creating the timelog.");
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/inventory/timelogs/{id} — deletes a timelog entry.
        ///
        /// Source mapping: ProjectController.DeleteTimelog() (lines 257-293)
        /// Original route: api/v3.0/p/project/pc-timelog-list/delete (was POST with body)
        /// Now uses RESTful DELETE with path parameter.
        ///
        /// Flow:
        ///   1. Extract timelog ID from path parameters (replaces body-based ID extraction)
        ///   2. Extract JWT user ID
        ///   3. Call ITaskService.HandleTimelogDeletionHookAsync (replaces PreDeleteApiHookLogic —
        ///      reverses task aggregate minutes, deletes related feed items)
        ///   4. Call ITaskService.DeleteTimelogAsync (validates author-only deletion per
        ///      TimeLogService.Delete line 72: "Only the author can delete its comment")
        ///   5. Return 200 OK
        ///
        /// Bug fix: Source line 289 returns "Comment successfully deleted" — corrected to
        /// "Timelog successfully deleted" as this is clearly a copy-paste error from CommentService.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteTimelog(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "DeleteTimelog invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract timelog ID from path parameter — was body-based in source
                if (!TryGetPathParameterGuid(request, "id", out var timelogId))
                {
                    _logger.LogWarning("DeleteTimelog: invalid or missing id path parameter. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Path parameter 'id' is required and must be a valid GUID.");
                }

                // Step 2: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                _logger.LogInformation(
                    "DeleteTimelog: timelogId={TimelogId}, userId={UserId}. CorrelationId: {CorrelationId}",
                    timelogId, currentUserId, context.AwsRequestId);

                // Step 3: Execute pre-delete hook logic — replaces TimeLogService.PreDeleteApiHookLogic()
                // (lines 281-367): reverses task x_billable_minutes/x_nonbillable_minutes, deletes
                // related feed items where l_related_records CONTAINS recordId
                await _taskService.HandleTimelogDeletionHookAsync(timelogId);

                // Step 4: Delete the timelog — replaces TimeLogService.Delete() (lines 62-83)
                // Validates author-only deletion (source line 72: created_by != currentUserId → throw)
                await _taskService.DeleteTimelogAsync(timelogId, currentUserId);

                _logger.LogInformation(
                    "DeleteTimelog: successfully deleted timelog {TimelogId}. CorrelationId: {CorrelationId}",
                    timelogId, context.AwsRequestId);

                // Bug fix: source line 289 had "Comment successfully deleted" — corrected to "Timelog"
                return SuccessResponse("Timelog successfully deleted");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "DeleteTimelog: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // Catches "Only the author can delete" from TaskService (source line 72)
                _logger.LogWarning(ex, "DeleteTimelog: forbidden operation. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(403, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "DeleteTimelog: bad request. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeleteTimelog: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while deleting the timelog.");
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/inventory/timelogs — queries timelogs for a date range
        /// with optional project and user filters.
        ///
        /// Source mapping: TimeLogService.GetTimelogsForPeriod() (lines 85-106)
        /// This was an internal method in the monolith called by components and report service;
        /// now exposed as an explicit API endpoint.
        ///
        /// Query parameters:
        ///   - startDate (DateTime, required) — ISO 8601 format
        ///   - endDate (DateTime, required) — ISO 8601 format, must be after startDate
        ///   - projectId (Guid, optional) — filter by related project
        ///   - userId (Guid, optional) — filter by creator
        ///
        /// Source code analysis (TimeLogService.cs lines 85-106):
        ///   EQL: SELECT * from timelog WHERE logged_on &gt;= @startDate AND logged_on &lt; @endDate
        ///   + optional: AND l_related_records CONTAINS @projectId
        ///   + optional: AND created_by = @userId
        ///   Now translates to DynamoDB queries via IInventoryRepository.GetTimelogsByDateRangeAsync.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetTimelogs(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "GetTimelogs invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

                // Parse startDate (required) — ISO 8601
                if (!queryParams.TryGetValue("startDate", out var startDateStr) || string.IsNullOrWhiteSpace(startDateStr))
                {
                    return ErrorResponse(400, "Query parameter 'startDate' is required.");
                }
                if (!DateTime.TryParse(startDateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var startDate))
                {
                    return ErrorResponse(400, "Query parameter 'startDate' must be a valid ISO 8601 date.");
                }

                // Parse endDate (required) — ISO 8601
                if (!queryParams.TryGetValue("endDate", out var endDateStr) || string.IsNullOrWhiteSpace(endDateStr))
                {
                    return ErrorResponse(400, "Query parameter 'endDate' is required.");
                }
                if (!DateTime.TryParse(endDateStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var endDate))
                {
                    return ErrorResponse(400, "Query parameter 'endDate' must be a valid ISO 8601 date.");
                }

                // Validate date range
                if (endDate <= startDate)
                {
                    return ErrorResponse(400, "endDate must be after startDate");
                }

                // Parse optional projectId filter — source lines 96-100
                Guid? projectId = null;
                if (queryParams.TryGetValue("projectId", out var projectIdStr) && !string.IsNullOrWhiteSpace(projectIdStr))
                {
                    if (!Guid.TryParse(projectIdStr, out var parsedProjectId))
                    {
                        return ErrorResponse(400, "Query parameter 'projectId' must be a valid GUID.");
                    }
                    projectId = parsedProjectId;
                }

                // Parse optional userId filter — source lines 101-105
                Guid? userId = null;
                if (queryParams.TryGetValue("userId", out var userIdStr) && !string.IsNullOrWhiteSpace(userIdStr))
                {
                    if (!Guid.TryParse(userIdStr, out var parsedUserId))
                    {
                        return ErrorResponse(400, "Query parameter 'userId' must be a valid GUID.");
                    }
                    userId = parsedUserId;
                }

                _logger.LogInformation(
                    "GetTimelogs: startDate={StartDate}, endDate={EndDate}, projectId={ProjectId}, userId={UserId}. CorrelationId: {CorrelationId}",
                    startDate, endDate, projectId, userId, context.AwsRequestId);

                // Query timelogs — replaces EqlCommand execution (source line 105)
                var timelogs = await _taskService.GetTimelogsForPeriodAsync(projectId, userId, startDate, endDate);

                _logger.LogInformation(
                    "GetTimelogs: returned {Count} timelogs. CorrelationId: {CorrelationId}",
                    timelogs.Count, context.AwsRequestId);

                return SuccessResponse("Timelogs retrieved successfully", timelogs);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "GetTimelogs: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetTimelogs: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while retrieving timelogs.");
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/inventory/timelogs/track — atomic stop task timelog + create
        /// timelog entry in a single operation.
        ///
        /// Source mapping: TimeLogService.PostApplicationNodePageHookLogic() (lines 108-179)
        /// This was a page-level hook triggered by form POST on the track-time page in the monolith;
        /// in the new architecture it becomes an explicit API endpoint.
        ///
        /// Request body fields:
        ///   - taskId (Guid, required) — source line 117
        ///   - minutes (int, required) — source line 119
        ///   - loggedOn (DateTime, required) — source line 121
        ///   - body (string, optional) — source line 126
        ///   - isBillable (bool, optional, default true) — source line 134
        ///
        /// Flow:
        ///   1. Parse and validate request body fields
        ///   2. Extract JWT user ID
        ///   3. Call ITaskService.HandleTrackTimePagePostAsync which atomically:
        ///      a. Stops task timelog (StopTaskTimelogAsync) — source line 160
        ///      b. If minutes != 0: creates timelog entry (CreateTimelogAsync) — source line 164
        ///      (Source used DbContext.BeginTransaction/CommitTransaction — replaced by DynamoDB
        ///       TransactWriteItems for atomic multi-item operation)
        ///   4. Return 200 OK (source returned RedirectResult which is N/A for API)
        ///
        /// Note: Zero minutes are NOT logged (source line 161 check).
        /// Note: Default isBillable is true for TrackTime (different from CreateTimelog where default is false).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> TrackTime(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation(
                "TrackTime invoked. Route: {Route}, CorrelationId: {CorrelationId}",
                request.RawPath, context.AwsRequestId);

            try
            {
                // Step 1: Extract caller identity from JWT claims
                var currentUserId = GetCurrentUserId(request);

                // Step 2: Deserialize and validate request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    _logger.LogWarning("TrackTime: empty request body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Request body is required.");
                }

                JsonElement bodyJson;
                try
                {
                    bodyJson = JsonSerializer.Deserialize<JsonElement>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "TrackTime: invalid JSON body. CorrelationId: {CorrelationId}", context.AwsRequestId);
                    return ErrorResponse(400, "Invalid JSON request body.");
                }

                // Parse taskId (required) — source line 117: if (String.IsNullOrWhiteSpace(postForm["task_id"])) throw
                if (!bodyJson.TryGetProperty("taskId", out var taskIdElement) &&
                    !bodyJson.TryGetProperty("TaskId", out taskIdElement) &&
                    !bodyJson.TryGetProperty("task_id", out taskIdElement))
                {
                    return ErrorResponse(400, "Field 'taskId' is required.");
                }
                if (!TryGetGuid(taskIdElement, out var taskId))
                {
                    return ErrorResponse(400, "Field 'taskId' must be a valid GUID.");
                }

                // Parse minutes (required) — source line 119
                if (!bodyJson.TryGetProperty("minutes", out var minutesElement) &&
                    !bodyJson.TryGetProperty("Minutes", out minutesElement))
                {
                    return ErrorResponse(400, "Field 'minutes' is required.");
                }
                if (!TryGetInt32(minutesElement, out var minutes))
                {
                    return ErrorResponse(400, "Field 'minutes' must be a valid integer.");
                }

                // Parse loggedOn (required) — source line 121
                if (!bodyJson.TryGetProperty("loggedOn", out var loggedOnElement) &&
                    !bodyJson.TryGetProperty("LoggedOn", out loggedOnElement) &&
                    !bodyJson.TryGetProperty("logged_on", out loggedOnElement))
                {
                    return ErrorResponse(400, "Field 'loggedOn' is required.");
                }
                if (!TryGetDateTime(loggedOnElement, out var loggedOn))
                {
                    return ErrorResponse(400, "Field 'loggedOn' must be a valid ISO 8601 date.");
                }

                // Parse body (optional) — source line 126
                string body = string.Empty;
                if (bodyJson.TryGetProperty("body", out var bodyElement) ||
                    bodyJson.TryGetProperty("Body", out bodyElement))
                {
                    body = bodyElement.GetString() ?? string.Empty;
                }

                // Parse isBillable (optional, default true for TrackTime) — source line 134
                bool isBillable = true;
                if (bodyJson.TryGetProperty("isBillable", out var isBillableElement) ||
                    bodyJson.TryGetProperty("IsBillable", out isBillableElement) ||
                    bodyJson.TryGetProperty("is_billable", out isBillableElement))
                {
                    TryGetBoolean(isBillableElement, out isBillable);
                }

                _logger.LogInformation(
                    "TrackTime: taskId={TaskId}, minutes={Minutes}, isBillable={IsBillable}, userId={UserId}. CorrelationId: {CorrelationId}",
                    taskId, minutes, isBillable, currentUserId, context.AwsRequestId);

                // Step 3: Load task to verify it exists — source lines 139-153
                var task = await _taskService.GetTaskAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("TrackTime: task {TaskId} not found. CorrelationId: {CorrelationId}", taskId, context.AwsRequestId);
                    return ErrorResponse(404, $"Task with ID '{taskId}' was not found.");
                }

                // Log task state including timer status — source line 240: clears timelog_started_on
                // Task.TimelogStartedOn indicates whether a timer is currently running for this task
                _logger.LogInformation(
                    "TrackTime: found task {TaskId}, timerActive={TimerActive}. CorrelationId: {CorrelationId}",
                    task.Id, task.TimelogStartedOn.HasValue, context.AwsRequestId);

                // Step 4: Execute atomic track-time operation — replaces PostApplicationNodePageHookLogic
                // Source lines 155-173: within a DB transaction, stops task timelog + creates timelog
                // Zero minutes are not logged (source line 161: if (postForm["minutes"].ToString() != "0"))
                await _taskService.HandleTrackTimePagePostAsync(
                    taskId, minutes, loggedOn, isBillable, body, currentUserId);

                _logger.LogInformation(
                    "TrackTime: successfully tracked time for task {TaskId}. CorrelationId: {CorrelationId}",
                    task.Id, context.AwsRequestId);

                return SuccessResponse("Time tracked successfully");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "TrackTime: unauthorized. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(401, "Unauthorized: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "TrackTime: validation error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "TrackTime: bad request. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TrackTime: unhandled error. CorrelationId: {CorrelationId}", context.AwsRequestId);
                return ErrorResponse(500, "An internal error occurred while tracking time.");
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/inventory/timelogs/health — service health check endpoint.
        /// Returns a simple healthy status for load balancer and monitoring integration.
        /// Does not require authentication.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HealthCheck(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            _logger.LogInformation("HealthCheck invoked. CorrelationId: {CorrelationId}", context.AwsRequestId);

            var healthResponse = new Dictionary<string, object>
            {
                { "status", "healthy" },
                { "service", "inventory-timelog" },
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
        ///   - TimeLogService.cs line 71: SecurityContext.CurrentUser.Id for author validation
        ///   - ProjectController.cs line 200: SecurityContext.CurrentUser.Id with fallback
        ///   - TimeLogService.PostApplicationNodePageHookLogic line 113: SecurityContext.CurrentUser
        /// </summary>
        /// <param name="request">The API Gateway v2 proxy request containing JWT authorizer claims.</param>
        /// <returns>The authenticated user's GUID extracted from the "sub" claim.</returns>
        /// <exception cref="UnauthorizedAccessException">Thrown when JWT claims are missing or the "sub" claim is not a valid GUID.</exception>
        private Guid GetCurrentUserId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.RequestContext?.Authorizer?.Jwt?.Claims != null &&
                request.RequestContext.Authorizer.Jwt.Claims.TryGetValue("sub", out var sub))
            {
                if (Guid.TryParse(sub, out var userId))
                {
                    return userId;
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
        /// Used for resource creation endpoints (CreateTimelog).
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
            if (request.PathParameters != null &&
                request.PathParameters.TryGetValue(paramName, out var paramStr) &&
                !string.IsNullOrWhiteSpace(paramStr))
            {
                return Guid.TryParse(paramStr, out value);
            }
            return false;
        }

        /// <summary>
        /// Attempts to parse a JsonElement as an Int32 value.
        /// Handles both numeric JSON values and string representations (matching
        /// source pattern of Int32.TryParse on form values).
        /// </summary>
        private static bool TryGetInt32(JsonElement element, out int value)
        {
            value = 0;
            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetInt32(out value);
            }
            if (element.ValueKind == JsonValueKind.String)
            {
                return int.TryParse(element.GetString(), out value);
            }
            return false;
        }

        /// <summary>
        /// Attempts to parse a JsonElement as a DateTime value.
        /// Handles ISO 8601 string format and JSON date representations.
        /// </summary>
        private static bool TryGetDateTime(JsonElement element, out DateTime value)
        {
            value = default;
            if (element.ValueKind == JsonValueKind.String)
            {
                return DateTime.TryParse(element.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out value);
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
        /// Parses a JSON array element as a List of Guid values.
        /// Matches source pattern: JsonConvert.DeserializeObject&lt;List&lt;Guid&gt;&gt;()
        /// from ProjectController.cs line 189-193.
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
