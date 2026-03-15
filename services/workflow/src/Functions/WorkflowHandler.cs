using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;

// Assembly-level Lambda serializer attribute — only ONE per assembly.
// No other file in this project defines this attribute; this is the authoritative location.
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Workflow.Functions
{
    /// <summary>
    /// Primary Lambda handler for the Workflow Engine microservice, serving as the HTTP API
    /// Gateway v2 entry point for all workflow management operations.
    ///
    /// Replaces the monolith's:
    /// - JobManager singleton (CreateJob, UpdateJob, GetJob, GetJobs, RegisterJobType, ProcessJobsAsync)
    /// - ScheduleManager singleton (CreateSchedulePlan, UpdateSchedulePlan, GetSchedulePlan, Process)
    /// - ErpBackgroundServices (ErpJobScheduleService, ErpJobProcessService polling loops)
    ///
    /// With serverless, event-driven architecture:
    /// - HTTP API Gateway v2 routes → Lambda handler methods
    /// - Step Functions orchestration (replacing JobPool 20-thread executor)
    /// - DynamoDB persistence (replacing PostgreSQL jobs/schedule_plans tables)
    /// - SNS domain events (replacing synchronous HookManager hooks)
    /// - SQS/EventBridge triggers (replacing BackgroundService polling)
    /// </summary>
    public class WorkflowHandler
    {
        private readonly IWorkflowService _workflowService;
        private readonly IAmazonStepFunctions _stepFunctions;
        private readonly IAmazonSimpleNotificationService _sns;
        private readonly ILogger<WorkflowHandler> _logger;
        private readonly WorkflowSettings _settings;

        /// <summary>
        /// Shared JSON serializer options for consistent snake_case API responses.
        /// Uses System.Text.Json with JsonNamingPolicy.SnakeCaseLower for Native AOT
        /// compatibility per AAP Section 0.8.2 cold start requirement (&lt; 1 second).
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        /// <summary>
        /// Parameterless constructor invoked by the Lambda runtime. Builds a lightweight DI
        /// container with all required AWS SDK clients, services, and configuration.
        ///
        /// Replaces the monolith's static singleton initialization:
        ///   JobManager.Initialize(settings, additionalJobTypes) → DI container
        ///   ScheduleManager.Initialize(settings) → DI container
        ///   JobManager.Current (private ctor, line 23) → per-invocation DI
        /// </summary>
        public WorkflowHandler()
        {
            var services = new ServiceCollection();

            // Bind configuration from environment variables (AAP Section 0.8.6)
            var settings = new WorkflowSettings
            {
                DynamoDbTableName = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME")
                    ?? "workflow-table",
                StepFunctionsStateMachineArn =
                    Environment.GetEnvironmentVariable("STEP_FUNCTIONS_STATE_MACHINE_ARN")
                    ?? string.Empty,
                SnsTopicArn = Environment.GetEnvironmentVariable("SNS_TOPIC_ARN")
                    ?? string.Empty,
                SqsQueueUrl = Environment.GetEnvironmentVariable("SQS_QUEUE_URL")
                    ?? string.Empty,
                AwsRegion = Environment.GetEnvironmentVariable("AWS_REGION")
                    ?? "us-east-1",
                AwsEndpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL"),
                Enabled = !string.Equals(
                    Environment.GetEnvironmentVariable("WORKFLOW_ENABLED"),
                    "false", StringComparison.OrdinalIgnoreCase)
            };
            services.AddSingleton(settings);

            // Register AWS SDK clients with optional LocalStack endpoint override
            // per AAP Section 0.8.6: AWS_ENDPOINT_URL = http://localhost:4566 for LocalStack
            var endpointUrl = settings.AwsEndpointUrl;
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(
                    new AmazonDynamoDBConfig
                    {
                        ServiceURL = endpointUrl,
                        AuthenticationRegion = settings.AwsRegion
                    }));
                services.AddSingleton<IAmazonStepFunctions>(_ => new AmazonStepFunctionsClient(
                    new AmazonStepFunctionsConfig
                    {
                        ServiceURL = endpointUrl,
                        AuthenticationRegion = settings.AwsRegion
                    }));
                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig
                        {
                            ServiceURL = endpointUrl,
                            AuthenticationRegion = settings.AwsRegion
                        }));
            }
            else
            {
                // Production AWS mode — SDK auto-discovers credentials and region
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonStepFunctions, AmazonStepFunctionsClient>();
                services.AddSingleton<IAmazonSimpleNotificationService,
                    AmazonSimpleNotificationServiceClient>();
            }

            // Register workflow service (replaces JobManager.Current + ScheduleManager.Current)
            services.AddTransient<IWorkflowService, WorkflowService>();

            // Structured JSON logging per AAP Section 0.8.5
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                builder.AddConsole();
            });

            var provider = services.BuildServiceProvider();
            _workflowService = provider.GetRequiredService<IWorkflowService>();
            _stepFunctions = provider.GetRequiredService<IAmazonStepFunctions>();
            _sns = provider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = provider.GetRequiredService<ILogger<WorkflowHandler>>();
            _settings = settings;
        }

        /// <summary>
        /// Constructor for unit testing with pre-configured dependencies.
        /// </summary>
        internal WorkflowHandler(
            IWorkflowService workflowService,
            IAmazonStepFunctions stepFunctions,
            IAmazonSimpleNotificationService sns,
            ILogger<WorkflowHandler> logger,
            WorkflowSettings settings)
        {
            _workflowService = workflowService
                ?? throw new ArgumentNullException(nameof(workflowService));
            _stepFunctions = stepFunctions
                ?? throw new ArgumentNullException(nameof(stepFunctions));
            _sns = sns
                ?? throw new ArgumentNullException(nameof(sns));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings
                ?? throw new ArgumentNullException(nameof(settings));
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Primary Entry Point: HTTP API Gateway v2 Proxy Handler ─────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Primary Lambda entry point for HTTP API Gateway v2 proxy integration.
        /// Routes requests to appropriate handler methods based on HTTP method and path.
        ///
        /// Route table (path-based /v1/ versioning per AAP Section 0.8.6):
        ///   POST   /v1/workflows              → CreateWorkflowAsync
        ///   GET    /v1/workflows               → ListWorkflowsAsync
        ///   GET    /v1/workflows/health        → HealthCheckAsync
        ///   POST   /v1/workflows/recover       → RecoverAbortedWorkflowsAsync
        ///   GET    /v1/workflows/{id}          → GetWorkflowAsync
        ///   PUT    /v1/workflows/{id}          → UpdateWorkflowAsync
        ///   POST   /v1/workflows/{id}/start    → StartWorkflowAsync
        ///   POST   /v1/workflows/{id}/cancel   → CancelWorkflowAsync
        ///   POST   /v1/schedules               → CreateSchedulePlanAsync
        ///   GET    /v1/schedules               → ListSchedulePlansAsync
        ///   POST   /v1/schedules/process       → ProcessSchedulesAsync
        ///   GET    /v1/schedules/{id}          → GetSchedulePlanAsync
        ///   PUT    /v1/schedules/{id}          → UpdateSchedulePlanAsync
        ///   POST   /v1/schedules/{id}/trigger  → TriggerSchedulePlanAsync
        ///   GET    /v1/workflow-types           → ListWorkflowTypesAsync
        ///   POST   /v1/workflow-types           → RegisterWorkflowTypeAsync
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleApiRequest(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext lambdaContext)
        {
            var correlationId = ExtractCorrelationId(request);
            var userId = ExtractUserIdFromRequest(request);
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";
            var path = request.RawPath?.TrimEnd('/') ?? string.Empty;

            _logger.LogInformation(
                "Workflow API request: {Method} {Path}, CorrelationId={CorrelationId}, UserId={UserId}",
                method, path, correlationId, userId);

            try
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2)
                    return CreateErrorResponse(HttpStatusCode.NotFound, "Not Found");

                var resource = segments[1];
                return resource switch
                {
                    "workflows" => await RouteWorkflowsAsync(
                        segments, method, request, userId, correlationId),
                    "schedules" => await RouteSchedulesAsync(
                        segments, method, request, userId, correlationId),
                    "workflow-types" => await RouteWorkflowTypesAsync(
                        segments, method, request, userId, correlationId),
                    _ => CreateErrorResponse(HttpStatusCode.NotFound, "Not Found")
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unhandled exception processing {Method} {Path}, CorrelationId={CorrelationId}",
                    method, path, correlationId);
                return CreateErrorResponse(
                    HttpStatusCode.InternalServerError, "Internal Server Error");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Secondary Entry Point: SQS Schedule/Process Trigger ────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Secondary Lambda entry point triggered by SQS/EventBridge for scheduled
        /// workflow processing. Replaces the monolith's two BackgroundService polling loops:
        ///   ErpJobScheduleService.ExecuteAsync() → ProcessSchedulesAsync
        ///   ErpJobProcessService.ExecuteAsync()  → ProcessWorkflowsAsync
        ///
        /// No startup delays (monolith had Thread.Sleep(10000) debug / Thread.Sleep(120000) prod).
        /// No infinite loops — single invocation processes one batch.
        /// </summary>
        public async Task HandleScheduleEvent(SQSEvent sqsEvent, ILambdaContext lambdaContext)
        {
            var messageCount = sqsEvent?.Records?.Count ?? 0;
            _logger.LogInformation(
                "HandleScheduleEvent invoked with {MessageCount} SQS message(s), " +
                "Function={FunctionName}, RemainingTime={RemainingTime}",
                messageCount, lambdaContext.FunctionName, lambdaContext.RemainingTime);

            // Safety margin: stop processing 5 seconds before Lambda timeout.
            // Guard against negative or zero remaining time (e.g., in test contexts).
            var safetyMargin = TimeSpan.FromSeconds(5);
            var remaining = lambdaContext.RemainingTime;
            var timeout = remaining > safetyMargin
                ? remaining.Subtract(safetyMargin)
                : remaining > TimeSpan.Zero
                    ? remaining
                    : TimeSpan.FromMinutes(5); // Fallback for test contexts
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                if (!_settings.Enabled)
                {
                    _logger.LogWarning(
                        "Workflow processing is disabled (Enabled=false). Skipping batch.");
                    return;
                }

                // Log each SQS message for observability
                if (sqsEvent?.Records != null)
                {
                    foreach (SQSEvent.SQSMessage message in sqsEvent.Records)
                    {
                        _logger.LogInformation(
                            "Processing SQS message: MessageId={MessageId}, EventSource={EventSource}",
                            message.MessageId, message.EventSource);
                    }
                }

                // Process schedules first — check which plans need triggering and
                // create pending workflows for due schedule plans
                await _workflowService.ProcessSchedulesAsync(cts.Token).ConfigureAwait(false);

                // Then process pending workflows — start Step Functions executions
                await _workflowService.ProcessWorkflowsAsync(cts.Token).ConfigureAwait(false);

                _logger.LogInformation(
                    "Schedule/workflow processing completed successfully.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Schedule/workflow processing canceled — Lambda timeout approaching.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during schedule/workflow processing.");
                throw; // Rethrow to trigger SQS retry / DLQ per AAP Section 0.8.5
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Route Dispatch Methods ──────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>Routes /v1/workflows/* requests to the appropriate handler.</summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> RouteWorkflowsAsync(
            string[] segments, string method, APIGatewayHttpApiV2ProxyRequest request,
            Guid userId, string correlationId)
        {
            // /v1/workflows — collection-level routes
            if (segments.Length == 2)
            {
                return method switch
                {
                    "GET" => await ListWorkflowsAsync(request),
                    "POST" => await CreateWorkflowAsync(request, userId, correlationId),
                    _ => CreateErrorResponse(HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
                };
            }

            // /v1/workflows/{sub} — named routes FIRST, then GUID-based
            if (segments.Length == 3)
            {
                var sub = segments[2];

                // Named keyword routes (checked before GUID parsing)
                if (string.Equals(sub, "health", StringComparison.OrdinalIgnoreCase)
                    && method == "GET")
                    return HealthCheckAsync();

                if (string.Equals(sub, "recover", StringComparison.OrdinalIgnoreCase)
                    && method == "POST")
                    return await RecoverAbortedWorkflowsAsync(correlationId);

                // GUID-based routes: /v1/workflows/{id}
                if (!Guid.TryParse(sub, out var workflowId))
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid workflow ID format.");

                return method switch
                {
                    "GET" => await GetWorkflowAsync(workflowId),
                    "PUT" => await UpdateWorkflowAsync(workflowId, request, userId),
                    _ => CreateErrorResponse(HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
                };
            }

            // /v1/workflows/{id}/{action} — action routes
            if (segments.Length == 4)
            {
                if (!Guid.TryParse(segments[2], out var workflowId))
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid workflow ID format.");

                if (method != "POST")
                    return CreateErrorResponse(
                        HttpStatusCode.MethodNotAllowed, "Method Not Allowed");

                var action = segments[3].ToLowerInvariant();
                return action switch
                {
                    "start" => await StartWorkflowAsync(workflowId, userId, correlationId),
                    "cancel" => await CancelWorkflowAsync(workflowId, userId, correlationId),
                    _ => CreateErrorResponse(HttpStatusCode.NotFound, "Not Found")
                };
            }

            return CreateErrorResponse(HttpStatusCode.NotFound, "Not Found");
        }

        /// <summary>Routes /v1/schedules/* requests to the appropriate handler.</summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> RouteSchedulesAsync(
            string[] segments, string method, APIGatewayHttpApiV2ProxyRequest request,
            Guid userId, string correlationId)
        {
            // /v1/schedules — collection-level routes
            if (segments.Length == 2)
            {
                return method switch
                {
                    "GET" => await ListSchedulePlansAsync(),
                    "POST" => await CreateSchedulePlanAsync(request, userId),
                    _ => CreateErrorResponse(HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
                };
            }

            // /v1/schedules/{sub} — named routes FIRST, then GUID-based
            if (segments.Length == 3)
            {
                var sub = segments[2];

                if (string.Equals(sub, "process", StringComparison.OrdinalIgnoreCase)
                    && method == "POST")
                    return await ProcessSchedulesAsync(correlationId);

                if (!Guid.TryParse(sub, out var scheduleId))
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid schedule ID format.");

                return method switch
                {
                    "GET" => await GetSchedulePlanAsync(scheduleId),
                    "PUT" => await UpdateSchedulePlanAsync(scheduleId, request, userId),
                    _ => CreateErrorResponse(HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
                };
            }

            // /v1/schedules/{id}/trigger — action route
            if (segments.Length == 4)
            {
                if (!Guid.TryParse(segments[2], out var scheduleId))
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid schedule ID format.");

                if (method == "POST" && string.Equals(
                    segments[3], "trigger", StringComparison.OrdinalIgnoreCase))
                    return await TriggerSchedulePlanAsync(scheduleId, correlationId);

                return CreateErrorResponse(HttpStatusCode.NotFound, "Not Found");
            }

            return CreateErrorResponse(HttpStatusCode.NotFound, "Not Found");
        }

        /// <summary>Routes /v1/workflow-types requests to the appropriate handler.</summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> RouteWorkflowTypesAsync(
            string[] segments, string method, APIGatewayHttpApiV2ProxyRequest request,
            Guid userId, string correlationId)
        {
            // /v1/workflow-types — collection-level only
            if (segments.Length != 2)
                return CreateErrorResponse(HttpStatusCode.NotFound, "Not Found");

            return method switch
            {
                "GET" => await ListWorkflowTypesAsync(),
                "POST" => await RegisterWorkflowTypeAsync(request, correlationId),
                _ => CreateErrorResponse(HttpStatusCode.MethodNotAllowed, "Method Not Allowed")
            };
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Workflow CRUD Handlers (source: JobManager.cs lines 100-144) ────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a new workflow instance.
        /// Source: JobManager.CreateJob() lines 100-127.
        /// Preserves: type-lookup validation, Enum.IsDefined priority normalization
        /// with DefaultPriority fallback, and all Job construction fields mapped to
        /// Workflow properties.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> CreateWorkflowAsync(
            APIGatewayHttpApiV2ProxyRequest request, Guid userId, string correlationId)
        {
            if (string.IsNullOrEmpty(request.Body))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "Request body is required.");

            try
            {
                using var doc = JsonDocument.Parse(request.Body);
                var root = doc.RootElement;

                // Extract type_id (required) — source line 101: typeId parameter
                if (!root.TryGetProperty("type_id", out var typeIdProp) ||
                    !Guid.TryParse(typeIdProp.GetString(), out var typeId))
                {
                    return CreateErrorResponse(HttpStatusCode.BadRequest,
                        "Field 'type_id' is required and must be a valid GUID.");
                }

                // Validate type exists — preserves source lines 102-108:
                //   var type = JobTypes.FirstOrDefault(t => t.Id == typeId);
                //   if (type == null) { Log.Create(Error, "type not found"); return null; }
                var type = await _workflowService.GetWorkflowTypeAsync(typeId)
                    .ConfigureAwait(false);
                if (type == null)
                {
                    _logger.LogError(
                        "Create workflow failed: type with ID '{TypeId}' not found. " +
                        "CorrelationId={CorrelationId}",
                        typeId, correlationId);
                    return CreateErrorResponse(HttpStatusCode.BadRequest,
                        $"Workflow type with ID '{typeId}' not found.");
                }

                // Extract optional attributes
                Dictionary<string, object>? attributes = null;
                if (root.TryGetProperty("attributes", out var attrProp) &&
                    attrProp.ValueKind == JsonValueKind.Object)
                {
                    attributes = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        attrProp.GetRawText(), JsonOptions);
                }

                // Priority normalization — preserves source lines 110-111:
                //   if (!Enum.IsDefined(typeof(JobPriority), priority))
                //     priority = type.DefaultPriority;
                var priority = WorkflowPriority.Low;
                var priorityDefined = false;
                if (root.TryGetProperty("priority", out var priProp))
                {
                    if (priProp.ValueKind == JsonValueKind.Number)
                    {
                        var priVal = priProp.GetInt32();
                        if (Enum.IsDefined(typeof(WorkflowPriority), priVal))
                        {
                            priority = (WorkflowPriority)priVal;
                            priorityDefined = true;
                        }
                    }
                    else if (priProp.ValueKind == JsonValueKind.String &&
                             Enum.TryParse<WorkflowPriority>(priProp.GetString(), true,
                                 out var parsedPri))
                    {
                        priority = parsedPri;
                        priorityDefined = true;
                    }
                }
                // Fallback to type's DefaultPriority (source line 111)
                if (!priorityDefined)
                    priority = type.DefaultPriority;

                // Extract optional schedule plan ID and workflow ID
                Guid? schedulePlanId = null;
                if (root.TryGetProperty("schedule_plan_id", out var spIdProp) &&
                    Guid.TryParse(spIdProp.GetString(), out var parsedSpId))
                {
                    schedulePlanId = parsedSpId;
                }

                Guid? workflowId = null;
                if (root.TryGetProperty("workflow_id", out var wfIdProp) &&
                    Guid.TryParse(wfIdProp.GetString(), out var parsedWfId))
                {
                    workflowId = parsedWfId;
                }

                // Delegate to service — handles DynamoDB persistence and SNS event
                var workflow = await _workflowService.CreateWorkflowAsync(
                    typeId, attributes, priority, userId, schedulePlanId, workflowId)
                    .ConfigureAwait(false);

                if (workflow == null)
                {
                    _logger.LogError(
                        "Create workflow failed for type '{TypeId}', CorrelationId={CorrelationId}",
                        typeId, correlationId);
                    return CreateErrorResponse(HttpStatusCode.InternalServerError,
                        "Failed to create workflow.");
                }

                _logger.LogInformation(
                    "Workflow {WorkflowId} created, TypeName={TypeName}, Priority={Priority}, " +
                    "CorrelationId={CorrelationId}",
                    workflow.Id, workflow.TypeName, workflow.Priority, correlationId);

                return CreateResponse(HttpStatusCode.Created, workflow);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in create workflow request body.");
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest, "Invalid JSON request body.");
            }
        }

        /// <summary>
        /// Updates an existing workflow.
        /// Source: JobManager.UpdateJob() lines 129-132.
        /// Delegates to JobDataService.UpdateJob(job) — now IWorkflowService.UpdateWorkflowAsync.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> UpdateWorkflowAsync(
            Guid workflowId, APIGatewayHttpApiV2ProxyRequest request, Guid userId)
        {
            if (string.IsNullOrEmpty(request.Body))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "Request body is required.");

            try
            {
                var workflow = JsonSerializer.Deserialize<Models.Workflow>(
                    request.Body, JsonOptions);
                if (workflow == null)
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid workflow data.");

                // Ensure path parameter ID takes precedence over body
                workflow.Id = workflowId;
                workflow.LastModifiedBy = userId;
                workflow.LastModifiedOn = DateTime.UtcNow;

                var success = await _workflowService.UpdateWorkflowAsync(workflow)
                    .ConfigureAwait(false);

                if (!success)
                    return CreateErrorResponse(HttpStatusCode.NotFound,
                        $"Workflow '{workflowId}' not found.");

                _logger.LogInformation(
                    "Workflow {WorkflowId} updated by user {UserId}.",
                    workflowId, userId);

                return CreateResponse(HttpStatusCode.OK, workflow);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in update workflow request body.");
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest, "Invalid JSON request body.");
            }
        }

        /// <summary>
        /// Retrieves a workflow by ID.
        /// Source: JobManager.GetJob() lines 134-137.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> GetWorkflowAsync(Guid workflowId)
        {
            var workflow = await _workflowService.GetWorkflowAsync(workflowId)
                .ConfigureAwait(false);

            if (workflow == null)
                return CreateErrorResponse(HttpStatusCode.NotFound,
                    $"Workflow '{workflowId}' not found.");

            return CreateResponse(HttpStatusCode.OK, workflow);
        }

        /// <summary>
        /// Lists workflows with optional filtering and pagination.
        /// Source: JobManager.GetJobs() lines 139-144.
        /// Preserves date range, typeName substring, status, priority, schedulePlanId filters,
        /// and page/pageSize pagination with totalCount (source used out int totalCount).
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ListWorkflowsAsync(
            APIGatewayHttpApiV2ProxyRequest request)
        {
            var qp = request.QueryStringParameters ?? new Dictionary<string, string>();

            DateTime? startFromDate = ParseNullableDateTime(qp, "start_from_date");
            DateTime? startToDate = ParseNullableDateTime(qp, "start_to_date");
            DateTime? finishedFromDate = ParseNullableDateTime(qp, "finished_from_date");
            DateTime? finishedToDate = ParseNullableDateTime(qp, "finished_to_date");
            string? typeName = qp.TryGetValue("type_name", out var tn) ? tn : null;
            int? status = qp.TryGetValue("status", out var st)
                && int.TryParse(st, out var stVal) ? stVal : null;
            int? priority = qp.TryGetValue("priority", out var pr)
                && int.TryParse(pr, out var prVal) ? prVal : null;
            Guid? schedulePlanId = qp.TryGetValue("schedule_plan_id", out var sp)
                && Guid.TryParse(sp, out var spGuid) ? spGuid : null;
            int? page = qp.TryGetValue("page", out var pg)
                && int.TryParse(pg, out var pgVal) ? pgVal : null;
            int? pageSize = qp.TryGetValue("page_size", out var ps)
                && int.TryParse(ps, out var psVal) ? psVal : null;

            var (workflows, totalCount) = await _workflowService.GetWorkflowsAsync(
                startFromDate, startToDate, finishedFromDate, finishedToDate,
                typeName, status, priority, schedulePlanId, page, pageSize)
                .ConfigureAwait(false);

            return CreateResponse(HttpStatusCode.OK, new
            {
                workflows,
                total_count = totalCount,
                page = page ?? 1,
                page_size = pageSize ?? 10
            });
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Workflow Execution Control ───────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Starts execution of a pending workflow via Step Functions.
        /// Replaces monolith's JobManager.ProcessJobsAsync dispatch + JobPool.RunJobAsync.
        ///
        /// Preserves single-instance constraint (source lines 177-178):
        ///   if (job.Type.AllowSingleInstance &amp;&amp;
        ///       JobPool.Current.HasJobFromTypeInThePool(job.Type.Id)) continue;
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> StartWorkflowAsync(
            Guid workflowId, Guid userId, string correlationId)
        {
            var workflow = await _workflowService.GetWorkflowAsync(workflowId)
                .ConfigureAwait(false);

            if (workflow == null)
                return CreateErrorResponse(HttpStatusCode.NotFound,
                    $"Workflow '{workflowId}' not found.");

            // Validate Pending status — only pending workflows can be started.
            // Returns 409 Conflict: the resource state conflicts with the requested operation.
            if (workflow.Status != WorkflowStatus.Pending)
            {
                return CreateErrorResponse(HttpStatusCode.Conflict,
                    $"Workflow '{workflowId}' cannot be started. " +
                    $"Current status: {workflow.Status}. Expected: {WorkflowStatus.Pending}.");
            }

            // Single-instance constraint check (source JobManager.cs line 177):
            //   if (job.Type.AllowSingleInstance &&
            //       JobPool.Current.HasJobFromTypeInThePool(job.Type.Id)) continue;
            if (workflow.Type != null && workflow.Type.AllowSingleInstance)
            {
                var runningWorkflows = await _workflowService
                    .GetRunningWorkflowsAsync(null).ConfigureAwait(false);
                bool hasRunningOfSameType = false;
                foreach (var rw in runningWorkflows)
                {
                    if (rw.TypeId == workflow.TypeId)
                    {
                        hasRunningOfSameType = true;
                        break;
                    }
                }

                if (hasRunningOfSameType)
                {
                    _logger.LogWarning(
                        "Single-instance constraint blocked workflow {WorkflowId} " +
                        "of type '{TypeName}' (TypeId={TypeId}). " +
                        "A running instance already exists.",
                        workflowId, workflow.Type.Name, workflow.Type.Id);
                    return CreateErrorResponse(HttpStatusCode.Conflict,
                        $"Workflow type '{workflow.Type.Name}' allows only a single " +
                        "running instance. A running workflow of this type already exists.");
                }
            }

            // Construct Step Functions execution context
            // (replacing monolith's JobContext created by JobPool.RunJobAsync)
            var stepContext = new StepContext
            {
                WorkflowId = workflow.Id,
                Aborted = false,
                Priority = workflow.Priority,
                Attributes = workflow.Attributes,
                Type = workflow.Type
            };

            try
            {
                // Start Step Functions execution (replaces JobPool.Current.RunJobAsync dispatch)
                var executionResponse = await _stepFunctions.StartExecutionAsync(
                    new StartExecutionRequest
                    {
                        StateMachineArn = _settings.StepFunctionsStateMachineArn,
                        Name = $"workflow-{workflow.Id}",
                        Input = JsonSerializer.Serialize(stepContext, JsonOptions)
                    }).ConfigureAwait(false);

                // Update workflow with execution metadata
                workflow.StepFunctionsExecutionArn = executionResponse.ExecutionArn;
                workflow.Status = WorkflowStatus.Running;
                workflow.StartedOn = DateTime.UtcNow;
                workflow.LastModifiedBy = userId;
                workflow.LastModifiedOn = DateTime.UtcNow;

                await _workflowService.UpdateWorkflowAsync(workflow).ConfigureAwait(false);

                // Publish domain event per AAP Section 0.8.5
                await PublishWorkflowEventAsync("started", workflow, correlationId)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Workflow {WorkflowId} started. ExecutionArn={ExecutionArn}, " +
                    "CorrelationId={CorrelationId}",
                    workflow.Id, executionResponse.ExecutionArn, correlationId);

                return CreateResponse(HttpStatusCode.OK, workflow);
            }
            catch (ExecutionAlreadyExistsException ex)
            {
                _logger.LogWarning(ex,
                    "Step Functions execution already exists for workflow {WorkflowId}.",
                    workflowId);
                return CreateErrorResponse(HttpStatusCode.Conflict,
                    $"A Step Functions execution for workflow '{workflowId}' already exists.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start workflow {WorkflowId}, CorrelationId={CorrelationId}",
                    workflowId, correlationId);
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Failed to start workflow execution.");
            }
        }

        /// <summary>
        /// Cancels a running workflow by stopping its Step Functions execution.
        /// Replaces the monolith's cooperative abort flag (context.Aborted = true)
        /// with an explicit StopExecution call. Updates workflow status to Canceled
        /// with CanceledBy and FinishedOn timestamps.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> CancelWorkflowAsync(
            Guid workflowId, Guid userId, string correlationId)
        {
            var workflow = await _workflowService.GetWorkflowAsync(workflowId)
                .ConfigureAwait(false);

            if (workflow == null)
                return CreateErrorResponse(HttpStatusCode.NotFound,
                    $"Workflow '{workflowId}' not found.");

            if (workflow.Status != WorkflowStatus.Running)
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest,
                    $"Workflow '{workflowId}' cannot be canceled. " +
                    $"Current status: {workflow.Status}. Expected: {WorkflowStatus.Running}.");
            }

            try
            {
                // Stop Step Functions execution (replacing cooperative abort flag)
                if (!string.IsNullOrEmpty(workflow.StepFunctionsExecutionArn))
                {
                    await _stepFunctions.StopExecutionAsync(new StopExecutionRequest
                    {
                        ExecutionArn = workflow.StepFunctionsExecutionArn,
                        Cause = $"Cancelled by user {userId}"
                    }).ConfigureAwait(false);
                }

                // Update workflow state
                workflow.Status = WorkflowStatus.Canceled;
                workflow.CanceledBy = userId;
                workflow.FinishedOn = DateTime.UtcNow;
                workflow.LastModifiedBy = userId;
                workflow.LastModifiedOn = DateTime.UtcNow;

                await _workflowService.UpdateWorkflowAsync(workflow).ConfigureAwait(false);

                // Publish domain event
                await PublishWorkflowEventAsync("cancelled", workflow, correlationId)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Workflow {WorkflowId} canceled by user {UserId}, " +
                    "CorrelationId={CorrelationId}",
                    workflowId, userId, correlationId);

                return CreateResponse(HttpStatusCode.OK, workflow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to cancel workflow {WorkflowId}, CorrelationId={CorrelationId}",
                    workflowId, correlationId);
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Failed to cancel workflow execution.");
            }
        }

        /// <summary>
        /// Recovers all workflows stuck in Running state (crash recovery).
        /// Preserves exact behavior from JobManager constructor lines 28-41:
        ///   var runningJobs = JobService.GetRunningJobs();
        ///   foreach (var job in runningJobs) {
        ///       job.Status = JobStatus.Aborted;
        ///       job.AbortedBy = Guid.Empty; // by system
        ///       job.FinishedOn = DateTime.UtcNow;
        ///       JobService.UpdateJob(job);
        ///   }
        /// The service method RecoverAbortedWorkflowsAsync preserves this exact logic:
        /// queries Running workflows, sets Status=Aborted, AbortedBy=Guid.Empty,
        /// FinishedOn=UtcNow.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> RecoverAbortedWorkflowsAsync(
            string correlationId)
        {
            try
            {
                await _workflowService.RecoverAbortedWorkflowsAsync().ConfigureAwait(false);

                _logger.LogInformation(
                    "Aborted workflow recovery completed, CorrelationId={CorrelationId}",
                    correlationId);

                return CreateResponse(HttpStatusCode.OK, new
                {
                    message = "Recovery completed successfully."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to recover aborted workflows, CorrelationId={CorrelationId}",
                    correlationId);
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Failed to recover aborted workflows.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Schedule Plan Management (source: SheduleManager.cs) ────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a new schedule plan.
        /// Source: ScheduleManager.CreateSchedulePlan() lines 37-45.
        /// Service assigns ID if empty and computes initial NextTriggerTime.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> CreateSchedulePlanAsync(
            APIGatewayHttpApiV2ProxyRequest request, Guid userId)
        {
            if (string.IsNullOrEmpty(request.Body))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "Request body is required.");

            try
            {
                var schedulePlan = JsonSerializer.Deserialize<SchedulePlan>(
                    request.Body, JsonOptions);
                if (schedulePlan == null)
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid schedule plan data.");

                // Assign ID if not provided (source line 39)
                if (schedulePlan.Id == Guid.Empty)
                    schedulePlan.Id = Guid.NewGuid();

                var success = await _workflowService.CreateSchedulePlanAsync(schedulePlan)
                    .ConfigureAwait(false);

                if (!success)
                    return CreateErrorResponse(HttpStatusCode.InternalServerError,
                        "Failed to create schedule plan.");

                _logger.LogInformation(
                    "Schedule plan '{Name}' (ID: {SchedulePlanId}) created.",
                    schedulePlan.Name, schedulePlan.Id);

                return CreateResponse(HttpStatusCode.Created, schedulePlan);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in create schedule plan request body.");
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest, "Invalid JSON request body.");
            }
        }

        /// <summary>
        /// Updates an existing schedule plan.
        /// Source: ScheduleManager.UpdateSchedulePlan() lines 47-50.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> UpdateSchedulePlanAsync(
            Guid scheduleId, APIGatewayHttpApiV2ProxyRequest request, Guid userId)
        {
            if (string.IsNullOrEmpty(request.Body))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "Request body is required.");

            try
            {
                var schedulePlan = JsonSerializer.Deserialize<SchedulePlan>(
                    request.Body, JsonOptions);
                if (schedulePlan == null)
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid schedule plan data.");

                // Ensure path parameter ID takes precedence
                schedulePlan.Id = scheduleId;

                var success = await _workflowService.UpdateSchedulePlanAsync(schedulePlan)
                    .ConfigureAwait(false);

                if (!success)
                    return CreateErrorResponse(HttpStatusCode.NotFound,
                        $"Schedule plan '{scheduleId}' not found.");

                return CreateResponse(HttpStatusCode.OK, schedulePlan);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in update schedule plan request body.");
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest, "Invalid JSON request body.");
            }
        }

        /// <summary>
        /// Retrieves a schedule plan by ID.
        /// Source: ScheduleManager.GetSchedulePlan() lines 58-61.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> GetSchedulePlanAsync(
            Guid scheduleId)
        {
            var plan = await _workflowService.GetSchedulePlanAsync(scheduleId)
                .ConfigureAwait(false);

            if (plan == null)
                return CreateErrorResponse(HttpStatusCode.NotFound,
                    $"Schedule plan '{scheduleId}' not found.");

            return CreateResponse(HttpStatusCode.OK, plan);
        }

        /// <summary>
        /// Lists all schedule plans.
        /// Source: ScheduleManager.GetSchedulePlans() lines 63-66.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ListSchedulePlansAsync()
        {
            var plans = await _workflowService.GetSchedulePlansAsync().ConfigureAwait(false);
            return CreateResponse(HttpStatusCode.OK, new
            {
                schedule_plans = plans,
                total_count = plans.Count
            });
        }

        /// <summary>
        /// Triggers immediate execution of a schedule plan.
        /// Source: ScheduleManager.TriggerNowSchedulePlan() lines 68-72.
        /// Internally sets NextTriggerTime to UtcNow + 1 minute (source line 70).
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> TriggerSchedulePlanAsync(
            Guid scheduleId, string correlationId)
        {
            var plan = await _workflowService.GetSchedulePlanAsync(scheduleId)
                .ConfigureAwait(false);

            if (plan == null)
                return CreateErrorResponse(HttpStatusCode.NotFound,
                    $"Schedule plan '{scheduleId}' not found.");

            if (!plan.Enabled)
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest,
                    $"Schedule plan '{scheduleId}' is disabled and cannot be triggered.");
            }

            try
            {
                await _workflowService.TriggerNowSchedulePlanAsync(plan)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Schedule plan '{Name}' (ID: {SchedulePlanId}) triggered. " +
                    "NextTriggerTime={NextTriggerTime}, CorrelationId={CorrelationId}",
                    plan.Name, plan.Id, plan.NextTriggerTime, correlationId);

                return CreateResponse(HttpStatusCode.OK, new
                {
                    message = "Schedule plan triggered successfully.",
                    schedule_plan_id = plan.Id,
                    next_trigger_time = plan.NextTriggerTime
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to trigger schedule plan '{SchedulePlanId}', " +
                    "CorrelationId={CorrelationId}",
                    scheduleId, correlationId);
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Failed to trigger schedule plan.");
            }
        }

        /// <summary>
        /// Processes all due schedule plans, creating pending workflows for triggered plans.
        /// Source: ScheduleManager.Process() lines 79-226.
        /// Replaces infinite polling loop with single-invocation processing.
        /// Can be triggered by EventBridge scheduled rule or manual API call.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ProcessSchedulesAsync(
            string correlationId)
        {
            try
            {
                if (!_settings.Enabled)
                {
                    _logger.LogWarning(
                        "Workflow processing is disabled. Skipping schedule processing.");
                    return CreateResponse(HttpStatusCode.OK, new
                    {
                        message = "Workflow processing is disabled.",
                        processed = false
                    });
                }

                await _workflowService.ProcessSchedulesAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Schedule processing completed, CorrelationId={CorrelationId}",
                    correlationId);

                return CreateResponse(HttpStatusCode.OK, new
                {
                    message = "Schedule processing completed successfully.",
                    processed = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Schedule processing failed, CorrelationId={CorrelationId}",
                    correlationId);
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Schedule processing failed.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Workflow Type Registry (source: JobManager.cs lines 56-98) ───────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists all registered workflow types.
        /// Source: JobManager.JobTypes static list (line 17).
        /// Target: DynamoDB-backed type registry via IWorkflowService.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ListWorkflowTypesAsync()
        {
            var types = await _workflowService.GetWorkflowTypesAsync().ConfigureAwait(false);
            return CreateResponse(HttpStatusCode.OK, new
            {
                workflow_types = types,
                total_count = types.Count
            });
        }

        /// <summary>
        /// Registers a new workflow type.
        /// Source: JobManager.RegisterJobType() lines 85-98.
        /// Preserves name-uniqueness validation (source line 87):
        ///   if (JobTypes.Any(t => t.Name == typeName)) return error
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> RegisterWorkflowTypeAsync(
            APIGatewayHttpApiV2ProxyRequest request, string correlationId)
        {
            if (string.IsNullOrEmpty(request.Body))
                return CreateErrorResponse(HttpStatusCode.BadRequest, "Request body is required.");

            try
            {
                var workflowType = JsonSerializer.Deserialize<WorkflowType>(
                    request.Body, JsonOptions);
                if (workflowType == null)
                    return CreateErrorResponse(
                        HttpStatusCode.BadRequest, "Invalid workflow type data.");

                if (string.IsNullOrWhiteSpace(workflowType.Name))
                    return CreateErrorResponse(HttpStatusCode.BadRequest,
                        "Field 'name' is required for workflow type registration.");

                if (workflowType.Id == Guid.Empty)
                    workflowType.Id = Guid.NewGuid();

                var success = await _workflowService.RegisterWorkflowTypeAsync(workflowType)
                    .ConfigureAwait(false);

                if (!success)
                {
                    _logger.LogError(
                        "Register workflow type failed: type with name '{TypeName}' " +
                        "already exists. CorrelationId={CorrelationId}",
                        workflowType.Name, correlationId);
                    return CreateErrorResponse(HttpStatusCode.Conflict,
                        $"Workflow type with name '{workflowType.Name}' already exists.");
                }

                _logger.LogInformation(
                    "Workflow type '{Name}' (ID: {TypeId}) registered, " +
                    "CorrelationId={CorrelationId}",
                    workflowType.Name, workflowType.Id, correlationId);

                return CreateResponse(HttpStatusCode.Created, workflowType);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in register workflow type request body.");
                return CreateErrorResponse(
                    HttpStatusCode.BadRequest, "Invalid JSON request body.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Health Check ─────────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Health check endpoint per AAP Section 0.8.5.
        /// Returns service status, configuration flag state, and current timestamp.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse HealthCheckAsync()
        {
            return CreateResponse(HttpStatusCode.OK, new
            {
                status = "healthy",
                service = "workflow",
                timestamp = DateTime.UtcNow,
                enabled = _settings.Enabled
            });
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── SNS Domain Event Publishing ──────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Publishes a domain event to the configured SNS topic.
        /// Event naming convention: {domain}.{entity}.{action} per AAP Section 0.8.5.
        /// Events published from handler: workflow.workflow.started, workflow.workflow.cancelled.
        /// (workflow.workflow.created is published by the WorkflowService layer.)
        /// Includes idempotency key in message attributes per AAP Section 0.8.5.
        /// </summary>
        private async Task PublishWorkflowEventAsync(
            string action, Models.Workflow workflow, string correlationId)
        {
            if (string.IsNullOrEmpty(_settings.SnsTopicArn))
            {
                _logger.LogWarning(
                    "SNS topic ARN not configured. Skipping event publish for " +
                    "workflow.workflow.{Action}.",
                    action);
                return;
            }

            var eventType = $"workflow.workflow.{action}";
            var eventPayload = new
            {
                event_type = eventType,
                workflow_id = workflow.Id.ToString(),
                type_id = workflow.TypeId.ToString(),
                type_name = workflow.TypeName ?? string.Empty,
                status = workflow.Status.ToString().ToLowerInvariant(),
                timestamp = DateTime.UtcNow.ToString("O"),
                correlation_id = correlationId
            };

            try
            {
                var publishRequest = new PublishRequest
                {
                    TopicArn = _settings.SnsTopicArn,
                    Message = JsonSerializer.Serialize(eventPayload, JsonOptions),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["event_type"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlation_id"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["idempotency_key"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = $"{eventType}:{workflow.Id}:" +
                                          $"{DateTime.UtcNow:yyyyMMddHHmmss}"
                        }
                    }
                };

                await _sns.PublishAsync(publishRequest).ConfigureAwait(false);

                _logger.LogInformation(
                    "Published SNS event {EventType} for workflow {WorkflowId}, " +
                    "CorrelationId={CorrelationId}",
                    eventType, workflow.Id, correlationId);
            }
            catch (Exception ex)
            {
                // Non-blocking: event publish failure should not fail the API response.
                // The workflow state change already persisted to DynamoDB.
                _logger.LogError(ex,
                    "Failed to publish SNS event {EventType} for workflow {WorkflowId}. " +
                    "Event will need manual reconciliation.",
                    eventType, workflow.Id);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Response Helper Methods ──────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a standard HTTP response with JSON body and CORS headers.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse CreateResponse(
            HttpStatusCode statusCode, object? body = null)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)statusCode,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Access-Control-Allow-Origin"] = "*",
                    ["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS",
                    ["Access-Control-Allow-Headers"] =
                        "Content-Type,Authorization,X-Correlation-Id,X-Idempotency-Key"
                },
                Body = body != null
                    ? JsonSerializer.Serialize(body, JsonOptions)
                    : string.Empty
            };
        }

        /// <summary>
        /// Creates an error response with a structured JSON error message body.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse CreateErrorResponse(
            HttpStatusCode statusCode, string message)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)statusCode,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Access-Control-Allow-Origin"] = "*",
                    ["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS",
                    ["Access-Control-Allow-Headers"] =
                        "Content-Type,Authorization,X-Correlation-Id,X-Idempotency-Key"
                },
                Body = JsonSerializer.Serialize(new { message }, JsonOptions)
            };
        }

        // ════════════════════════════════════════════════════════════════════════════
        // ── Utility Methods ──────────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts the authenticated user ID from JWT authorizer claims.
        /// Replaces monolith's SecurityContext.OpenSystemScope(user) pattern
        /// (source JobManager.cs lines 102, 187-188, 206, 261-262).
        /// Falls back to Guid.Empty for system/internal invocations.
        /// </summary>
        private static Guid ExtractUserIdFromRequest(
            APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
                if (claims != null)
                {
                    // Standard Cognito "sub" claim (user pool unique ID)
                    if (claims.TryGetValue("sub", out var sub) &&
                        Guid.TryParse(sub, out var userId))
                        return userId;

                    // Custom "user_id" claim for backward compatibility
                    if (claims.TryGetValue("user_id", out var uid) &&
                        Guid.TryParse(uid, out var parsedUid))
                        return parsedUid;
                }
            }
            catch
            {
                // Silently fall through to system default
            }

            // Default to system user (Guid.Empty) for internal/service calls
            return Guid.Empty;
        }

        /// <summary>
        /// Extracts or generates a correlation ID for distributed request tracing.
        /// Per AAP Section 0.8.5: structured JSON logging with correlation-ID propagation.
        /// </summary>
        private static string ExtractCorrelationId(
            APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.Headers != null)
            {
                // HTTP headers are case-insensitive; check common casings
                if (request.Headers.TryGetValue("x-correlation-id", out var corId) &&
                    !string.IsNullOrEmpty(corId))
                    return corId;

                if (request.Headers.TryGetValue("X-Correlation-Id", out var corId2) &&
                    !string.IsNullOrEmpty(corId2))
                    return corId2;
            }

            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Parses an optional DateTime value from query string parameters.
        /// Supports ISO 8601 round-trip format for date range filters.
        /// </summary>
        private static DateTime? ParseNullableDateTime(
            IDictionary<string, string> queryParams, string key)
        {
            if (queryParams.TryGetValue(key, out var value) &&
                DateTime.TryParse(value, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
