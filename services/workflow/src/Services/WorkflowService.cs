using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.Workflow.Models;

namespace WebVellaErp.Workflow.Services
{
    /// <summary>
    /// Defines the contract for the Workflow Engine service, exposing all workflow type registry,
    /// workflow CRUD, schedule plan CRUD, trigger date calculation, and processing operations.
    /// Replaces the monolith's <c>JobManager</c>, <c>ScheduleManager</c>, and <c>JobDataService</c>
    /// singleton-based APIs with a DI-injected, DynamoDB-backed, Step Functions-orchestrated interface.
    /// </summary>
    public interface IWorkflowService
    {
        // ── Workflow Type Registry ───────────────────────────────────────────
        Task<bool> RegisterWorkflowTypeAsync(WorkflowType workflowType);
        Task<List<WorkflowType>> GetWorkflowTypesAsync();
        Task<WorkflowType?> GetWorkflowTypeAsync(Guid typeId);

        // ── Workflow CRUD ────────────────────────────────────────────────────
        Task<Models.Workflow?> CreateWorkflowAsync(
            Guid typeId,
            Dictionary<string, object>? attributes = null,
            WorkflowPriority priority = WorkflowPriority.Low,
            Guid? creatorId = null,
            Guid? schedulePlanId = null,
            Guid? workflowId = null);
        Task<bool> UpdateWorkflowAsync(Models.Workflow workflow);
        Task<Models.Workflow?> GetWorkflowAsync(Guid workflowId);
        Task<(List<Models.Workflow> Workflows, int TotalCount)> GetWorkflowsAsync(
            DateTime? startFromDate = null,
            DateTime? startToDate = null,
            DateTime? finishedFromDate = null,
            DateTime? finishedToDate = null,
            string? typeName = null,
            int? status = null,
            int? priority = null,
            Guid? schedulePlanId = null,
            int? page = null,
            int? pageSize = null);
        Task<bool> IsWorkflowFinishedAsync(Guid workflowId);
        Task<List<Models.Workflow>> GetPendingWorkflowsAsync(int? limit = null);
        Task<List<Models.Workflow>> GetRunningWorkflowsAsync(int? limit = null);

        // ── Crash Recovery ───────────────────────────────────────────────────
        Task RecoverAbortedWorkflowsAsync();

        // ── Schedule Plan CRUD ───────────────────────────────────────────────
        Task<bool> CreateSchedulePlanAsync(SchedulePlan schedulePlan);
        Task<bool> UpdateSchedulePlanAsync(SchedulePlan schedulePlan);
        Task<SchedulePlan?> GetSchedulePlanAsync(Guid id);
        Task<List<SchedulePlan>> GetSchedulePlansAsync();
        Task<List<SchedulePlan>> GetReadyForExecutionSchedulePlansAsync();
        Task<List<SchedulePlan>> GetSchedulePlansByTypeAsync(SchedulePlanType type);
        Task TriggerNowSchedulePlanAsync(SchedulePlan schedulePlan);

        // ── Schedule Trigger Date Calculation (pure logic) ───────────────────
        DateTime? FindSchedulePlanNextTriggerDate(SchedulePlan schedulePlan);

        // ── Processing (Lambda-triggered, no polling loops) ──────────────────
        Task ProcessSchedulesAsync(CancellationToken cancellationToken = default);
        Task ProcessWorkflowsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Core business logic service for the Workflow Engine microservice. Consolidates logic from
    /// three monolith source files into a single stateless, DI-injected service:
    /// <list type="bullet">
    ///   <item><c>JobManager.cs</c> (303 lines) — Singleton coordinator, type registry, CRUD facades, dispatcher loops</item>
    ///   <item><c>SheduleManager.cs</c> (724 lines) — Schedule plan CRUD, 4 trigger-date calculators, schedule processing</item>
    ///   <item><c>JobDataService.cs</c> (525 lines) — PostgreSQL Npgsql persistence for jobs and schedule_plan tables</item>
    /// </list>
    ///
    /// Replaces the monolith's singleton pattern (<c>JobManager.Current</c>, <c>ScheduleManager.Current</c>)
    /// with standard DI-injected service. All PostgreSQL persistence is replaced with DynamoDB single-table
    /// design, synchronous job dispatch (<c>JobPool.RunJobAsync</c>) replaced with AWS Step Functions
    /// <c>StartExecution</c>, and polling loops replaced with Lambda-triggered processing.
    /// </summary>
    public class WorkflowService : IWorkflowService
    {
        private readonly IAmazonDynamoDB _dynamoDb;
        private readonly IAmazonStepFunctions _stepFunctions;
        private readonly IAmazonSimpleNotificationService _sns;
        private readonly ILogger<WorkflowService> _logger;
        private readonly WorkflowSettings _settings;

        /// <summary>
        /// Initializes a new instance of <see cref="WorkflowService"/> with all required
        /// dependencies injected via the DI container.
        /// Replaces the monolith's static singleton constructors:
        /// <c>JobManager.Initialize(settings)</c> and <c>ScheduleManager.Initialize(settings)</c>.
        /// </summary>
        /// <param name="dynamoDb">DynamoDB client replacing Npgsql-based <c>JobDataService</c>.</param>
        /// <param name="stepFunctions">Step Functions client replacing <c>JobPool.RunJobAsync</c>.</param>
        /// <param name="sns">SNS client for domain event publishing replacing synchronous hooks.</param>
        /// <param name="logger">Structured logger replacing <c>WebVella.Erp.Diagnostics.Log</c>.</param>
        /// <param name="settings">Configuration replacing <c>JobManagerSettings</c>.</param>
        public WorkflowService(
            IAmazonDynamoDB dynamoDb,
            IAmazonStepFunctions stepFunctions,
            IAmazonSimpleNotificationService sns,
            ILogger<WorkflowService> logger,
            WorkflowSettings settings)
        {
            _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
            _stepFunctions = stepFunctions ?? throw new ArgumentNullException(nameof(stepFunctions));
            _sns = sns ?? throw new ArgumentNullException(nameof(sns));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _logger.LogInformation(
                "WorkflowService initialized. Table={Table}, Region={Region}, Endpoint={Endpoint}, " +
                "StateMachine={StateMachineArn}, SnsTopicArn={SnsTopicArn}, SqsQueueUrl={SqsQueueUrl}, Enabled={Enabled}.",
                _settings.DynamoDbTableName,
                _settings.AwsRegion ?? "default",
                _settings.AwsEndpointUrl ?? "AWS",
                _settings.StepFunctionsStateMachineArn,
                _settings.SnsTopicArn,
                _settings.SqsQueueUrl,
                _settings.Enabled);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Workflow Type Registry (from JobManager.cs lines 56-98) ─────────
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager.RegisterJobType()</c> (lines 85-98).
        /// Validates name uniqueness, then stores the type in DynamoDB with
        /// <c>PK=WORKFLOW_TYPE#</c> and <c>SK=TYPE#{typeId}</c>.
        /// </remarks>
        public async Task<bool> RegisterWorkflowTypeAsync(WorkflowType workflowType)
        {
            if (workflowType == null)
                throw new ArgumentNullException(nameof(workflowType));

            if (workflowType.Id == Guid.Empty)
                workflowType.Id = Guid.NewGuid();

            // Source line 87: name-uniqueness validation
            var existingTypes = await GetWorkflowTypesAsync().ConfigureAwait(false);
            if (existingTypes.Any(t => string.Equals(t.Name, workflowType.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Source lines 89-91: Log.Create(LogType.Error, ...)
                _logger.LogError(
                    "Register workflow type failed: type with name '{Name}' already exists.",
                    workflowType.Name);
                return false;
            }

            try
            {
                var item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = "WORKFLOW_TYPE#" },
                    ["SK"] = new AttributeValue { S = $"TYPE#{workflowType.Id}" },
                    ["id"] = new AttributeValue { S = workflowType.Id.ToString() },
                    ["name"] = new AttributeValue { S = workflowType.Name },
                    ["default_priority"] = new AttributeValue { N = ((int)workflowType.DefaultPriority).ToString() },
                    ["assembly"] = new AttributeValue { S = workflowType.Assembly ?? string.Empty },
                    ["complete_class_name"] = new AttributeValue { S = workflowType.CompleteClassName ?? string.Empty },
                    ["allow_single_instance"] = new AttributeValue { BOOL = workflowType.AllowSingleInstance },
                    ["entity_type"] = new AttributeValue { S = "WorkflowType" }
                };

                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Item = item
                }).ConfigureAwait(false);

                _logger.LogInformation(
                    "Workflow type '{Name}' (ID: {TypeId}) registered successfully.",
                    workflowType.Name, workflowType.Id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to register workflow type '{Name}' (ID: {TypeId}).",
                    workflowType.Name, workflowType.Id);
                return false;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager.JobTypes</c> static list (line 17).
        /// Queries DynamoDB for all items with <c>PK=WORKFLOW_TYPE#</c>.
        /// </remarks>
        public async Task<List<WorkflowType>> GetWorkflowTypesAsync()
        {
            var result = new List<WorkflowType>();
            try
            {
                var request = new QueryRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    KeyConditionExpression = "PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "WORKFLOW_TYPE#" }
                    }
                };

                QueryResponse? response = null;
                do
                {
                    if (response?.LastEvaluatedKey?.Count > 0)
                        request.ExclusiveStartKey = response.LastEvaluatedKey;

                    response = await _dynamoDb.QueryAsync(request).ConfigureAwait(false);
                    foreach (var item in response.Items)
                    {
                        result.Add(MapDynamoDbItemToWorkflowType(item));
                    }
                } while (response.LastEvaluatedKey?.Count > 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve workflow types from DynamoDB.");
            }
            return result;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: Implicit from <c>JobTypes.FirstOrDefault(t => t.Id == typeId)</c> (JobManager.cs line 102).
        /// DynamoDB GetItem with <c>PK=WORKFLOW_TYPE#</c> and <c>SK=TYPE#{typeId}</c>.
        /// </remarks>
        public async Task<WorkflowType?> GetWorkflowTypeAsync(Guid typeId)
        {
            try
            {
                var response = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = "WORKFLOW_TYPE#" },
                        ["SK"] = new AttributeValue { S = $"TYPE#{typeId}" }
                    }
                }).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                    return null;

                return MapDynamoDbItemToWorkflowType(response.Item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve workflow type {TypeId}.", typeId);
                return null;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Workflow CRUD (from JobManager.cs 100-144 + JobDataService.cs 25-289)
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager.CreateJob()</c> (lines 100-127) + <c>JobDataService.CreateJob()</c> (lines 25-74).
        /// Validates type exists, normalizes priority, creates Workflow in DynamoDB, publishes SNS event.
        /// </remarks>
        public async Task<Models.Workflow?> CreateWorkflowAsync(
            Guid typeId,
            Dictionary<string, object>? attributes = null,
            WorkflowPriority priority = WorkflowPriority.Low,
            Guid? creatorId = null,
            Guid? schedulePlanId = null,
            Guid? workflowId = null)
        {
            // Source lines 102-108: type lookup and validation
            var type = await GetWorkflowTypeAsync(typeId).ConfigureAwait(false);
            if (type == null)
            {
                _logger.LogError(
                    "Create workflow failed: workflow type with ID '{TypeId}' not found.",
                    typeId);
                return null;
            }

            // Source lines 110-111: normalize priority if invalid
            if (!Enum.IsDefined(typeof(WorkflowPriority), priority))
                priority = type.DefaultPriority;

            var now = DateTime.UtcNow;
            var id = workflowId ?? Guid.NewGuid();

            // Source lines 113-125: create Workflow object
            var workflow = new Models.Workflow
            {
                Id = id,
                TypeId = type.Id,
                Type = type,
                TypeName = type.Name,
                CompleteClassName = type.CompleteClassName,
                Status = WorkflowStatus.Pending,
                Priority = priority,
                Attributes = attributes,
                CreatedBy = creatorId,
                LastModifiedBy = creatorId,
                SchedulePlanId = schedulePlanId,
                CreatedOn = now,
                LastModifiedOn = now
            };

            try
            {
                // DynamoDB PutItem replacing JobDataService.CreateJob (lines 25-74)
                var item = MapWorkflowToDynamoDbItem(workflow);
                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Item = item
                }).ConfigureAwait(false);

                // Publish SNS domain event per AAP Section 0.8.5
                await PublishWorkflowEventAsync("created", workflow).ConfigureAwait(false);

                _logger.LogInformation(
                    "Workflow {WorkflowId} of type '{TypeName}' created successfully.",
                    workflow.Id, workflow.TypeName);
                return workflow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to create workflow of type '{TypeName}' (TypeId: {TypeId}).",
                    type.Name, typeId);
                return null;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager.UpdateJob()</c> (line 129-132) delegating to
        /// <c>JobDataService.UpdateJob()</c> (lines 76-117).
        /// DynamoDB PutItem replacing the conditional SET clause builder from source.
        /// </remarks>
        public async Task<bool> UpdateWorkflowAsync(Models.Workflow workflow)
        {
            if (workflow == null)
                throw new ArgumentNullException(nameof(workflow));

            workflow.LastModifiedOn = DateTime.UtcNow;

            try
            {
                var item = MapWorkflowToDynamoDbItem(workflow);
                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Item = item
                }).ConfigureAwait(false);

                await PublishWorkflowEventAsync("updated", workflow).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to update workflow {WorkflowId}.",
                    workflow.Id);
                return false;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetJob()</c> (lines 119-133).
        /// DynamoDB GetItem with <c>PK=WORKFLOW#{workflowId}</c>, <c>SK=META</c>.
        /// </remarks>
        public async Task<Models.Workflow?> GetWorkflowAsync(Guid workflowId)
        {
            try
            {
                var response = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                }).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                    return null;

                return MapDynamoDbItemToWorkflow(response.Item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve workflow {WorkflowId}.", workflowId);
                return null;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager.GetJobs()</c> (lines 139-144) + <c>JobDataService.GetJobs()</c> (lines 172-236)
        /// + <c>JobDataService.GetJobsTotalCount()</c> (lines 238-289).
        /// Returns tuple instead of <c>out int totalCount</c> since async cannot use out params.
        /// DynamoDB Scan with FilterExpression replacing SQL WHERE clauses.
        /// </remarks>
        public async Task<(List<Models.Workflow> Workflows, int TotalCount)> GetWorkflowsAsync(
            DateTime? startFromDate = null,
            DateTime? startToDate = null,
            DateTime? finishedFromDate = null,
            DateTime? finishedToDate = null,
            string? typeName = null,
            int? status = null,
            int? priority = null,
            Guid? schedulePlanId = null,
            int? page = null,
            int? pageSize = null)
        {
            var workflows = new List<Models.Workflow>();
            try
            {
                // Build filter expression parts — replaces SQL WHERE clause building
                // from JobDataService.GetJobs (lines 179-219)
                var filterParts = new List<string> { "entity_type = :entityType" };
                var exprValues = new Dictionary<string, AttributeValue>
                {
                    [":entityType"] = new AttributeValue { S = "Workflow" }
                };

                if (startFromDate.HasValue)
                {
                    filterParts.Add("started_on >= :startedFrom");
                    exprValues[":startedFrom"] = new AttributeValue { S = startFromDate.Value.ToString("o") };
                }
                if (startToDate.HasValue)
                {
                    filterParts.Add("started_on <= :startedTo");
                    exprValues[":startedTo"] = new AttributeValue { S = startToDate.Value.ToString("o") };
                }
                if (finishedFromDate.HasValue)
                {
                    filterParts.Add("finished_on >= :finishedFrom");
                    exprValues[":finishedFrom"] = new AttributeValue { S = finishedFromDate.Value.ToString("o") };
                }
                if (finishedToDate.HasValue)
                {
                    filterParts.Add("finished_on <= :finishedTo");
                    exprValues[":finishedTo"] = new AttributeValue { S = finishedToDate.Value.ToString("o") };
                }
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    // Source lines 199-204: ILIKE replaced with contains()
                    filterParts.Add("contains(type_name, :typeName)");
                    exprValues[":typeName"] = new AttributeValue { S = typeName };
                }
                if (status.HasValue)
                {
                    filterParts.Add("#st = :status");
                    exprValues[":status"] = new AttributeValue { N = status.Value.ToString() };
                }
                if (priority.HasValue)
                {
                    filterParts.Add("priority = :priority");
                    exprValues[":priority"] = new AttributeValue { N = priority.Value.ToString() };
                }
                if (schedulePlanId.HasValue)
                {
                    filterParts.Add("schedule_plan_id = :schedulePlanId");
                    exprValues[":schedulePlanId"] = new AttributeValue { S = schedulePlanId.Value.ToString() };
                }

                var filterExpression = string.Join(" AND ", filterParts);

                // Build expression attribute names for reserved words
                var exprNames = new Dictionary<string, string>();
                if (status.HasValue)
                    exprNames["#st"] = "status";

                // Scan all matching workflows
                var allItems = new List<Dictionary<string, AttributeValue>>();
                ScanResponse? scanResponse = null;
                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = _settings.DynamoDbTableName,
                        FilterExpression = filterExpression,
                        ExpressionAttributeValues = exprValues
                    };
                    if (exprNames.Count > 0)
                        scanRequest.ExpressionAttributeNames = exprNames;
                    if (scanResponse?.LastEvaluatedKey?.Count > 0)
                        scanRequest.ExclusiveStartKey = scanResponse.LastEvaluatedKey;

                    scanResponse = await _dynamoDb.ScanAsync(scanRequest).ConfigureAwait(false);
                    allItems.AddRange(scanResponse.Items);
                } while (scanResponse.LastEvaluatedKey?.Count > 0);

                int totalCount = allItems.Count;

                // Sort by created_on DESC (source line 221: ORDER BY created_on DESC)
                allItems.Sort((a, b) =>
                {
                    var dateA = a.ContainsKey("created_on") ? a["created_on"].S : string.Empty;
                    var dateB = b.ContainsKey("created_on") ? b["created_on"].S : string.Empty;
                    return string.Compare(dateB, dateA, StringComparison.Ordinal);
                });

                // Apply pagination (source lines 223-232: LIMIT @limit OFFSET @offset)
                int effectivePage = page ?? 1;
                int effectivePageSize = pageSize ?? 10;
                if (effectivePage < 1) effectivePage = 1;
                if (effectivePageSize < 1) effectivePageSize = 10;
                int skip = (effectivePage - 1) * effectivePageSize;

                var paginatedItems = allItems.Skip(skip).Take(effectivePageSize);
                foreach (var item in paginatedItems)
                {
                    workflows.Add(MapDynamoDbItemToWorkflow(item));
                }

                return (workflows, totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve workflows with filters.");
                return (workflows, 0);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.IsJobFinished()</c> (lines 135-143).
        /// Checks <c>FinishedOn.HasValue</c> from the DynamoDB item.
        /// </remarks>
        public async Task<bool> IsWorkflowFinishedAsync(Guid workflowId)
        {
            try
            {
                var response = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    },
                    ProjectionExpression = "finished_on"
                }).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                    return true; // Non-existent workflow considered finished

                return response.Item.ContainsKey("finished_on") &&
                       !response.Item["finished_on"].NULL;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check if workflow {WorkflowId} is finished.", workflowId);
                return true; // Fail-safe: treat error as finished to avoid re-triggering
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetPendingJobs()</c> (lines 145-148) delegating to
        /// <c>GetJobs(status, limit)</c> (lines 155-170).
        /// Scans DynamoDB for workflows with <c>status = Pending</c>, ordered by priority DESC, created_on ASC.
        /// </remarks>
        public async Task<List<Models.Workflow>> GetPendingWorkflowsAsync(int? limit = null)
        {
            return await GetWorkflowsByStatusAsync(WorkflowStatus.Pending, limit).ConfigureAwait(false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetRunningJobs()</c> (lines 150-153).
        /// Scans DynamoDB for workflows with <c>status = Running</c>.
        /// </remarks>
        public async Task<List<Models.Workflow>> GetRunningWorkflowsAsync(int? limit = null)
        {
            return await GetWorkflowsByStatusAsync(WorkflowStatus.Running, limit).ConfigureAwait(false);
        }

        /// <summary>
        /// Shared helper for <see cref="GetPendingWorkflowsAsync"/> and <see cref="GetRunningWorkflowsAsync"/>.
        /// Source: <c>JobDataService.GetJobs(int, int?)</c> (lines 155-170).
        /// Sort order: priority DESC, created_on ASC (source line 157).
        /// </summary>
        private async Task<List<Models.Workflow>> GetWorkflowsByStatusAsync(WorkflowStatus statusFilter, int? limit)
        {
            var workflows = new List<Models.Workflow>();
            try
            {
                var allItems = new List<Dictionary<string, AttributeValue>>();
                ScanResponse? scanResponse = null;
                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = _settings.DynamoDbTableName,
                        FilterExpression = "entity_type = :entityType AND #st = :status",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            ["#st"] = "status"
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":entityType"] = new AttributeValue { S = "Workflow" },
                            [":status"] = new AttributeValue { N = ((int)statusFilter).ToString() }
                        }
                    };
                    if (scanResponse?.LastEvaluatedKey?.Count > 0)
                        scanRequest.ExclusiveStartKey = scanResponse.LastEvaluatedKey;

                    scanResponse = await _dynamoDb.ScanAsync(scanRequest).ConfigureAwait(false);
                    allItems.AddRange(scanResponse.Items);
                } while (scanResponse.LastEvaluatedKey?.Count > 0);

                // Sort by priority DESC, created_on ASC (source line 157)
                allItems.Sort((a, b) =>
                {
                    int prioA = a.ContainsKey("priority") ? int.Parse(a["priority"].N) : 0;
                    int prioB = b.ContainsKey("priority") ? int.Parse(b["priority"].N) : 0;
                    int cmp = prioB.CompareTo(prioA); // DESC
                    if (cmp != 0) return cmp;
                    var dateA = a.ContainsKey("created_on") ? a["created_on"].S : string.Empty;
                    var dateB = b.ContainsKey("created_on") ? b["created_on"].S : string.Empty;
                    return string.Compare(dateA, dateB, StringComparison.Ordinal); // ASC
                });

                var limited = limit.HasValue ? allItems.Take(limit.Value) : allItems;
                foreach (var item in limited)
                {
                    workflows.Add(MapDynamoDbItemToWorkflow(item));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to retrieve workflows with status {Status}.",
                    statusFilter);
            }
            return workflows;
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Crash Recovery (from JobManager constructor, lines 27-42)
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager</c> constructor (lines 33-41).
        /// Queries all running workflows and sets each to Aborted. Called during Lambda cold
        /// start initialization rather than in constructor (per serverless pattern).
        /// </remarks>
        public async Task RecoverAbortedWorkflowsAsync()
        {
            try
            {
                var runningWorkflows = await GetRunningWorkflowsAsync().ConfigureAwait(false);
                if (runningWorkflows.Count == 0)
                    return;

                _logger.LogWarning(
                    "Crash recovery: found {Count} running workflow(s) to abort.",
                    runningWorkflows.Count);

                foreach (var workflow in runningWorkflows)
                {
                    // Source lines 37-40: abort each running workflow
                    workflow.Status = WorkflowStatus.Aborted;
                    workflow.AbortedBy = Guid.Empty; // System abort
                    workflow.FinishedOn = DateTime.UtcNow;

                    await UpdateWorkflowAsync(workflow).ConfigureAwait(false);
                    await PublishWorkflowEventAsync("aborted", workflow).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Crash recovery: workflow {WorkflowId} (type '{TypeName}') aborted.",
                        workflow.Id, workflow.TypeName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Crash recovery failed.");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Schedule Plan CRUD (from SheduleManager.cs 37-72 + JobDataService.cs 293-473)
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>ScheduleManager.CreateSchedulePlan()</c> (lines 37-45) +
        /// <c>JobDataService.CreateSchedule()</c> (lines 295-342).
        /// </remarks>
        public async Task<bool> CreateSchedulePlanAsync(SchedulePlan schedulePlan)
        {
            if (schedulePlan == null)
                throw new ArgumentNullException(nameof(schedulePlan));

            // Source lines 39-40: assign new Guid if empty
            if (schedulePlan.Id == Guid.Empty)
                schedulePlan.Id = Guid.NewGuid();

            // Source line 42: compute initial trigger time (only when not explicitly pre-set by caller)
            if (!schedulePlan.NextTriggerTime.HasValue)
                schedulePlan.NextTriggerTime = FindSchedulePlanNextTriggerDate(schedulePlan);
            schedulePlan.CreatedOn = DateTime.UtcNow;
            schedulePlan.LastModifiedOn = DateTime.UtcNow;

            try
            {
                var item = MapSchedulePlanToDynamoDbItem(schedulePlan);
                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Item = item
                }).ConfigureAwait(false);

                _logger.LogInformation(
                    "Schedule plan {SchedulePlanId} ('{Name}') created.",
                    schedulePlan.Id, schedulePlan.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to create schedule plan '{Name}'.",
                    schedulePlan.Name);
                return false;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>ScheduleManager.UpdateSchedulePlan()</c> (lines 47-50) +
        /// <c>JobDataService.UpdateSchedule(SchedulePlan)</c> (lines 344-391).
        /// Full update of all schedule plan fields.
        /// </remarks>
        public async Task<bool> UpdateSchedulePlanAsync(SchedulePlan schedulePlan)
        {
            if (schedulePlan == null)
                throw new ArgumentNullException(nameof(schedulePlan));

            schedulePlan.LastModifiedOn = DateTime.UtcNow;

            try
            {
                var item = MapSchedulePlanToDynamoDbItem(schedulePlan);
                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Item = item
                }).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to update schedule plan {SchedulePlanId}.",
                    schedulePlan.Id);
                return false;
            }
        }

        /// <summary>
        /// Partial update for schedule plan trigger-related fields only.
        /// Source: <c>ScheduleManager.UpdateSchedulePlanShort()</c> (lines 52-56) +
        /// <c>JobDataService.UpdateSchedule(Guid, ...)</c> (lines 393-423).
        /// </summary>
        private async Task<bool> UpdateSchedulePlanTriggerAsync(
            Guid schedulePlanId,
            DateTime? lastTriggerTime,
            DateTime? nextTriggerTime,
            Guid? modifiedBy,
            Guid? lastStartedWorkflowId)
        {
            try
            {
                var updateExprs = new List<string>();
                var exprValues = new Dictionary<string, AttributeValue>();

                // Always set last_modified_on (source line 407)
                updateExprs.Add("last_modified_on = :lastModOn");
                exprValues[":lastModOn"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") };

                if (lastTriggerTime.HasValue)
                {
                    updateExprs.Add("last_trigger_time = :lastTrigger");
                    exprValues[":lastTrigger"] = new AttributeValue { S = lastTriggerTime.Value.ToString("o") };
                }

                // Source lines 401-403: handle NextTriggerTime null (explicit DBNull.Value → NULL)
                if (nextTriggerTime.HasValue)
                {
                    updateExprs.Add("next_trigger_time = :nextTrigger");
                    exprValues[":nextTrigger"] = new AttributeValue { S = nextTriggerTime.Value.ToString("o") };
                }
                else
                {
                    updateExprs.Add("next_trigger_time = :nextTrigger");
                    exprValues[":nextTrigger"] = new AttributeValue { NULL = true };
                }

                if (modifiedBy.HasValue)
                {
                    updateExprs.Add("last_modified_by = :modBy");
                    exprValues[":modBy"] = new AttributeValue { S = modifiedBy.Value.ToString() };
                }

                if (lastStartedWorkflowId.HasValue)
                {
                    updateExprs.Add("last_started_job_id = :lastJobId");
                    exprValues[":lastJobId"] = new AttributeValue { S = lastStartedWorkflowId.Value.ToString() };
                }

                await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"SCHEDULE#{schedulePlanId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    },
                    UpdateExpression = "SET " + string.Join(", ", updateExprs),
                    ExpressionAttributeValues = exprValues
                }).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to update schedule plan trigger for {SchedulePlanId}.",
                    schedulePlanId);
                return false;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetSchedulePlan()</c> (lines 425-438).
        /// DynamoDB GetItem with <c>PK=SCHEDULE#{id}</c>, <c>SK=META</c>.
        /// </remarks>
        public async Task<SchedulePlan?> GetSchedulePlanAsync(Guid id)
        {
            try
            {
                var response = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _settings.DynamoDbTableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"SCHEDULE#{id}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                }).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                    return null;

                return MapDynamoDbItemToSchedulePlan(response.Item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve schedule plan {SchedulePlanId}.", id);
                return null;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetSchedulePlans()</c> (lines 441-448).
        /// DynamoDB Scan for all schedule plans, sorted by name (source line 443).
        /// </remarks>
        public async Task<List<SchedulePlan>> GetSchedulePlansAsync()
        {
            var plans = new List<SchedulePlan>();
            try
            {
                var allItems = new List<Dictionary<string, AttributeValue>>();
                ScanResponse? scanResponse = null;
                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = _settings.DynamoDbTableName,
                        FilterExpression = "entity_type = :entityType",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":entityType"] = new AttributeValue { S = "SchedulePlan" }
                        }
                    };
                    if (scanResponse?.LastEvaluatedKey?.Count > 0)
                        scanRequest.ExclusiveStartKey = scanResponse.LastEvaluatedKey;

                    scanResponse = await _dynamoDb.ScanAsync(scanRequest).ConfigureAwait(false);
                    allItems.AddRange(scanResponse.Items);
                } while (scanResponse.LastEvaluatedKey?.Count > 0);

                // Sort by name (source line 443: ORDER BY name)
                allItems.Sort((a, b) =>
                {
                    var nameA = a.ContainsKey("name") ? a["name"].S : string.Empty;
                    var nameB = b.ContainsKey("name") ? b["name"].S : string.Empty;
                    return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
                });

                foreach (var item in allItems)
                {
                    plans.Add(MapDynamoDbItemToSchedulePlan(item));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve schedule plans.");
            }
            return plans;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetReadyForExecutionScheduledPlans()</c> (lines 450-462).
        /// CRITICAL business logic preserved exactly from source SQL (lines 452-455):
        /// <c>enabled = true AND next_trigger_time &lt;= utc_now AND start_date &lt;= utc_now
        /// AND COALESCE(end_date, utc_now) &gt;= utc_now</c>.
        /// </remarks>
        public async Task<List<SchedulePlan>> GetReadyForExecutionSchedulePlansAsync()
        {
            var plans = new List<SchedulePlan>();
            try
            {
                var now = DateTime.UtcNow;

                // Fetch all enabled schedule plans, then filter in memory for complex date logic
                var allItems = new List<Dictionary<string, AttributeValue>>();
                ScanResponse? scanResponse = null;
                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = _settings.DynamoDbTableName,
                        FilterExpression = "entity_type = :entityType AND enabled = :enabled",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":entityType"] = new AttributeValue { S = "SchedulePlan" },
                            [":enabled"] = new AttributeValue { BOOL = true }
                        }
                    };
                    if (scanResponse?.LastEvaluatedKey?.Count > 0)
                        scanRequest.ExclusiveStartKey = scanResponse.LastEvaluatedKey;

                    scanResponse = await _dynamoDb.ScanAsync(scanRequest).ConfigureAwait(false);
                    allItems.AddRange(scanResponse.Items);
                } while (scanResponse.LastEvaluatedKey?.Count > 0);

                foreach (var item in allItems)
                {
                    var plan = MapDynamoDbItemToSchedulePlan(item);

                    // Source SQL lines 452-455:
                    // next_trigger_time <= @utc_now
                    if (!plan.NextTriggerTime.HasValue || plan.NextTriggerTime.Value > now)
                        continue;

                    // start_date <= @utc_now
                    if (plan.StartDate.HasValue && plan.StartDate.Value > now)
                        continue;

                    // COALESCE(end_date, @utc_now) >= @utc_now
                    var effectiveEndDate = plan.EndDate ?? now;
                    if (effectiveEndDate < now)
                        continue;

                    plans.Add(plan);
                }

                // Sort by next_trigger_time ASC (source line 455)
                plans.Sort((a, b) =>
                {
                    var tA = a.NextTriggerTime ?? DateTime.MaxValue;
                    var tB = b.NextTriggerTime ?? DateTime.MaxValue;
                    return tA.CompareTo(tB);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve ready-for-execution schedule plans.");
            }
            return plans;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobDataService.GetScheduledPlansByType()</c> (lines 464-473).
        /// DynamoDB Scan with filter on type, sorted by name.
        /// </remarks>
        public async Task<List<SchedulePlan>> GetSchedulePlansByTypeAsync(SchedulePlanType type)
        {
            var plans = new List<SchedulePlan>();
            try
            {
                var allItems = new List<Dictionary<string, AttributeValue>>();
                ScanResponse? scanResponse = null;
                do
                {
                    var scanRequest = new ScanRequest
                    {
                        TableName = _settings.DynamoDbTableName,
                        FilterExpression = "entity_type = :entityType AND #tp = :planType",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            ["#tp"] = "type"
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":entityType"] = new AttributeValue { S = "SchedulePlan" },
                            [":planType"] = new AttributeValue { N = ((int)type).ToString() }
                        }
                    };
                    if (scanResponse?.LastEvaluatedKey?.Count > 0)
                        scanRequest.ExclusiveStartKey = scanResponse.LastEvaluatedKey;

                    scanResponse = await _dynamoDb.ScanAsync(scanRequest).ConfigureAwait(false);
                    allItems.AddRange(scanResponse.Items);
                } while (scanResponse.LastEvaluatedKey?.Count > 0);

                // Sort by name (source line 466)
                allItems.Sort((a, b) =>
                {
                    var nameA = a.ContainsKey("name") ? a["name"].S : string.Empty;
                    var nameB = b.ContainsKey("name") ? b["name"].S : string.Empty;
                    return string.Compare(nameA, nameB, StringComparison.OrdinalIgnoreCase);
                });

                foreach (var item in allItems)
                {
                    plans.Add(MapDynamoDbItemToSchedulePlan(item));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to retrieve schedule plans by type {Type}.",
                    type);
            }
            return plans;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>ScheduleManager.TriggerNowSchedulePlan()</c> (lines 68-72).
        /// Sets <c>NextTriggerTime = DateTime.UtcNow.AddMinutes(1)</c> and updates via trigger-only update.
        /// </remarks>
        public async Task TriggerNowSchedulePlanAsync(SchedulePlan schedulePlan)
        {
            if (schedulePlan == null)
                throw new ArgumentNullException(nameof(schedulePlan));

            // Source line 70
            schedulePlan.NextTriggerTime = DateTime.UtcNow.AddMinutes(1);
            await UpdateSchedulePlanTriggerAsync(
                schedulePlan.Id,
                schedulePlan.LastTriggerTime,
                schedulePlan.NextTriggerTime,
                schedulePlan.LastModifiedBy,
                schedulePlan.LastStartedWorkflowId).ConfigureAwait(false);

            _logger.LogInformation(
                "Schedule plan {SchedulePlanId} ('{Name}') triggered for immediate execution.",
                schedulePlan.Id, schedulePlan.Name);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Schedule Trigger Date Calculators (from SheduleManager.cs 377-722)
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>ScheduleManager.FindSchedulePlanNextTriggerDate()</c> (lines 377-410).
        /// Dispatcher method — routes to type-specific calculator based on <see cref="SchedulePlanType"/>.
        /// Pure synchronous logic; no DynamoDB calls.
        /// </remarks>
        public DateTime? FindSchedulePlanNextTriggerDate(SchedulePlan schedulePlan)
        {
            if (schedulePlan == null)
                return null;

            // Source line 380
            var nowDateTime = DateTime.UtcNow;

            // Source lines 382-389: determine starting date
            DateTime startingDate;
            if (schedulePlan.StartDate.HasValue)
                startingDate = schedulePlan.StartDate.Value;
            else
                startingDate = nowDateTime;

            // Source lines 390-408: dispatch by schedule type
            switch (schedulePlan.Type)
            {
                case SchedulePlanType.Interval:
                    return FindIntervalSchedulePlanNextTriggerDate(
                        schedulePlan, nowDateTime, schedulePlan.LastTriggerTime);

                case SchedulePlanType.Daily:
                    return FindDailySchedulePlanNextTriggerDate(
                        schedulePlan, nowDateTime, startingDate);

                case SchedulePlanType.Weekly:
                    return FindWeeklySchedulePlanNextTriggerDate(
                        schedulePlan, nowDateTime, startingDate);

                case SchedulePlanType.Monthly:
                    return FindMonthlySchedulePlanNextTriggerDate(
                        schedulePlan, nowDateTime, startingDate);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Calculates the next trigger date for Interval-type schedule plans.
        /// Source: <c>ScheduleManager.FindIntervalSchedulePlanNextTriggerDate()</c> (lines 412-476).
        /// CRITICAL: All logic preserved exactly from source for full behavioral parity.
        /// </summary>
        private DateTime? FindIntervalSchedulePlanNextTriggerDate(
            SchedulePlan intervalPlan,
            DateTime nowDateTime,
            DateTime? lastExecution)
        {
            // Source lines 414-415: initialize ScheduledDays if null
            if (intervalPlan.ScheduledDays == null)
                intervalPlan.ScheduledDays = new SchedulePlanDaysOfWeek();

            try
            {
                // Source lines 420-423: return null if interval is not positive
                if (!intervalPlan.IntervalInMinutes.HasValue || intervalPlan.IntervalInMinutes.Value <= 0)
                    return null;

                // Source line 425: calculate starting date
                DateTime startingDate;
                if (lastExecution.HasValue)
                    startingDate = lastExecution.Value.AddMinutes(intervalPlan.IntervalInMinutes.Value);
                else
                    startingDate = nowDateTime;

                // Source line 428: while loop for date searching
                while (true)
                {
                    // Source lines 431-437: end date expiration check
                    if (intervalPlan.EndDate.HasValue && startingDate > intervalPlan.EndDate.Value)
                        return null;

                    // Source line 439: time as integer (minutes since midnight)
                    int timeAsInt = startingDate.Hour * 60 + startingDate.Minute;

                    // Source lines 440-444: isIntervalConnectedToFirstDay calculation
                    // When StartTimespan > EndTimespan, the interval spans midnight.
                    // If current time is before EndTimespan, we're on the "connected" portion
                    // of the previous day's interval.
                    bool isIntervalConnectedToFirstDay = false;
                    if (intervalPlan.StartTimespan.HasValue &&
                        intervalPlan.EndTimespan.HasValue &&
                        intervalPlan.StartTimespan.Value > intervalPlan.EndTimespan.Value &&
                        timeAsInt <= intervalPlan.EndTimespan.Value)
                    {
                        isIntervalConnectedToFirstDay = true;
                    }

                    // Source lines 447-449: check if time is in the defined timespan interval
                    if (IsTimeInTimespanInterval(startingDate, intervalPlan.StartTimespan, intervalPlan.EndTimespan))
                    {
                        // Check if this day-of-week is used in the schedule plan
                        if (IsDayUsedInSchedulePlan(startingDate, intervalPlan.ScheduledDays, isIntervalConnectedToFirstDay))
                        {
                            return startingDate;
                        }
                        else
                        {
                            // Move to next day's start timespan
                            startingDate = startingDate.AddDays(1);
                            if (intervalPlan.StartTimespan.HasValue)
                            {
                                startingDate = new DateTime(
                                    startingDate.Year, startingDate.Month, startingDate.Day,
                                    intervalPlan.StartTimespan.Value / 60,
                                    intervalPlan.StartTimespan.Value % 60,
                                    0, DateTimeKind.Utc);
                            }
                            continue;
                        }
                    }
                    else
                    {
                        // Source lines 457-469: advance to next day's start timespan
                        if (timeAsInt > (intervalPlan.EndTimespan ?? 1440))
                        {
                            startingDate = startingDate.AddDays(1);
                        }

                        if (intervalPlan.StartTimespan.HasValue)
                        {
                            startingDate = new DateTime(
                                startingDate.Year, startingDate.Month, startingDate.Day,
                                intervalPlan.StartTimespan.Value / 60,
                                intervalPlan.StartTimespan.Value % 60,
                                0, DateTimeKind.Utc);
                        }
                        continue;
                    }
                }
            }
            catch
            {
                // Source lines 472-475: catch-all returns null
                return null;
            }
        }

        /// <summary>
        /// Calculates the next trigger date for Daily-type schedule plans.
        /// Source: <c>ScheduleManager.FindDailySchedulePlanNextTriggerDate()</c> (lines 530-566).
        /// </summary>
        private DateTime? FindDailySchedulePlanNextTriggerDate(
            SchedulePlan dailyPlan,
            DateTime nowDateTime,
            DateTime startDate)
        {
            // Source lines 532-533: null-init ScheduledDays
            if (dailyPlan.ScheduledDays == null)
                dailyPlan.ScheduledDays = new SchedulePlanDaysOfWeek();

            try
            {
                // Build moved time with StartTimespan or start of day
                var movedTime = startDate;
                if (dailyPlan.StartTimespan.HasValue)
                {
                    movedTime = new DateTime(
                        startDate.Year, startDate.Month, startDate.Day,
                        dailyPlan.StartTimespan.Value / 60,
                        dailyPlan.StartTimespan.Value % 60,
                        0, DateTimeKind.Utc);
                }

                // Source lines 540-566: while loop searching for next trigger
                while (true)
                {
                    // End date expiration check (source lines 540-549)
                    if (dailyPlan.EndDate.HasValue && movedTime > dailyPlan.EndDate.Value)
                        return null;

                    // Source line 551: movedTime must be at least 10 seconds in the future
                    if (movedTime > nowDateTime.AddSeconds(10))
                    {
                        // Source line 552: day-of-week check
                        if (IsDayUsedInSchedulePlan(movedTime, dailyPlan.ScheduledDays, false))
                        {
                            return movedTime;
                        }
                    }

                    // Source line 558: advance by 1 day
                    movedTime = movedTime.AddDays(1);
                    if (dailyPlan.StartTimespan.HasValue)
                    {
                        movedTime = new DateTime(
                            movedTime.Year, movedTime.Month, movedTime.Day,
                            dailyPlan.StartTimespan.Value / 60,
                            dailyPlan.StartTimespan.Value % 60,
                            0, DateTimeKind.Utc);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates the next trigger date for Weekly-type schedule plans.
        /// Source: <c>ScheduleManager.FindWeeklySchedulePlanNextTriggerDate()</c> (lines 568-598).
        /// </summary>
        private DateTime? FindWeeklySchedulePlanNextTriggerDate(
            SchedulePlan weeklyPlan,
            DateTime nowDateTime,
            DateTime startDate)
        {
            try
            {
                var movedTime = startDate;
                if (weeklyPlan.StartTimespan.HasValue)
                {
                    movedTime = new DateTime(
                        startDate.Year, startDate.Month, startDate.Day,
                        weeklyPlan.StartTimespan.Value / 60,
                        weeklyPlan.StartTimespan.Value % 60,
                        0, DateTimeKind.Utc);
                }

                while (true)
                {
                    // Source lines 576-582: end date check
                    if (weeklyPlan.EndDate.HasValue && movedTime > weeklyPlan.EndDate.Value)
                        return null;

                    // Source line 584: movedTime must be at least 10 seconds in the future
                    if (movedTime > nowDateTime.AddSeconds(10))
                    {
                        return movedTime;
                    }

                    // Source line 590: advance by 7 days
                    movedTime = movedTime.AddDays(7);
                    if (weeklyPlan.StartTimespan.HasValue)
                    {
                        movedTime = new DateTime(
                            movedTime.Year, movedTime.Month, movedTime.Day,
                            weeklyPlan.StartTimespan.Value / 60,
                            weeklyPlan.StartTimespan.Value % 60,
                            0, DateTimeKind.Utc);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates the next trigger date for Monthly-type schedule plans.
        /// Source: <c>ScheduleManager.FindMonthlySchedulePlanNextTriggerDate()</c> (lines 600-630).
        /// </summary>
        private DateTime? FindMonthlySchedulePlanNextTriggerDate(
            SchedulePlan monthlyPlan,
            DateTime nowDateTime,
            DateTime startDate)
        {
            try
            {
                var movedTime = startDate;
                if (monthlyPlan.StartTimespan.HasValue)
                {
                    movedTime = new DateTime(
                        startDate.Year, startDate.Month, startDate.Day,
                        monthlyPlan.StartTimespan.Value / 60,
                        monthlyPlan.StartTimespan.Value % 60,
                        0, DateTimeKind.Utc);
                }

                while (true)
                {
                    // Source lines 607-613: end date check
                    if (monthlyPlan.EndDate.HasValue && movedTime > monthlyPlan.EndDate.Value)
                        return null;

                    // Source line 615: movedTime must be at least 10 seconds in the future
                    if (movedTime > nowDateTime.AddSeconds(10))
                    {
                        return movedTime;
                    }

                    // Source line 621: advance by 1 month
                    movedTime = movedTime.AddMonths(1);
                    if (monthlyPlan.StartTimespan.HasValue)
                    {
                        movedTime = new DateTime(
                            movedTime.Year, movedTime.Month, movedTime.Day,
                            monthlyPlan.StartTimespan.Value / 60,
                            monthlyPlan.StartTimespan.Value % 60,
                            0, DateTimeKind.Utc);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Determines if a given day matches the schedule plan's selected days-of-week,
        /// accounting for overnight interval connections.
        /// Source: <c>ScheduleManager.IsDayUsedInSchedulePlan()</c> (lines 632-702).
        /// CRITICAL: Logic preserved exactly from source for full behavioral parity.
        /// </summary>
        private static bool IsDayUsedInSchedulePlan(
            DateTime checkedDay,
            SchedulePlanDaysOfWeek selectedDays,
            bool isTimeConnectedToFirstDay)
        {
            if (selectedDays == null)
                return true;

            // If no day is selected, treat as "all days selected"
            if (!selectedDays.HasOneSelectedDay())
                return true;

            var dayToCheck = checkedDay;

            // Source lines 636-640: if overnight interval, check the previous day
            if (isTimeConnectedToFirstDay)
            {
                dayToCheck = dayToCheck.AddDays(-1);
            }

            // Source lines 641-699: switch on DayOfWeek
            switch (dayToCheck.DayOfWeek)
            {
                case DayOfWeek.Sunday:
                    return selectedDays.ScheduledOnSunday;
                case DayOfWeek.Monday:
                    return selectedDays.ScheduledOnMonday;
                case DayOfWeek.Tuesday:
                    return selectedDays.ScheduledOnTuesday;
                case DayOfWeek.Wednesday:
                    return selectedDays.ScheduledOnWednesday;
                case DayOfWeek.Thursday:
                    return selectedDays.ScheduledOnThursday;
                case DayOfWeek.Friday:
                    return selectedDays.ScheduledOnFriday;
                case DayOfWeek.Saturday:
                    return selectedDays.ScheduledOnSaturday;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Determines if a given date/time falls within the defined start/end timespan interval.
        /// Source: <c>ScheduleManager.IsTimeInTimespanInterval()</c> (lines 704-722).
        /// CRITICAL: Logic preserved exactly from source for full behavioral parity.
        /// </summary>
        private static bool IsTimeInTimespanInterval(DateTime date, int? startTimespan, int? endTimespan)
        {
            // Source line 706: time as minutes since midnight
            int timeAsInt = date.Hour * 60 + date.Minute;

            // Source lines 708-711: no start timespan means "any time"
            if (!startTimespan.HasValue)
                return true;

            // Source lines 713-721: normal vs day-overlap logic
            if (startTimespan.Value <= (endTimespan ?? 1440))
            {
                // Normal interval (same day): start <= time <= end
                return timeAsInt >= startTimespan.Value && timeAsInt <= (endTimespan ?? 1440);
            }
            else
            {
                // Overnight interval (spans midnight): (start <= time <= 1440) OR (0 <= time <= end)
                return (timeAsInt >= startTimespan.Value && timeAsInt <= 1440) ||
                       (timeAsInt >= 0 && timeAsInt <= (endTimespan ?? 0));
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Processing (from SheduleManager.Process + JobManager.Process)
        // ════════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>ScheduleManager.ProcessSchedulesAsync(CancellationToken)</c> (lines 228-375).
        /// Replaces the infinite polling loop with a single-invocation method triggered by
        /// Lambda (EventBridge rule or SQS). Business logic for per-schedule evaluation is preserved.
        /// </remarks>
        public async Task ProcessSchedulesAsync(CancellationToken cancellationToken = default)
        {
            if (!_settings.Enabled)
                return;

            try
            {
                // Source line 243: get ready-for-execution schedule plans
                var readyPlans = await GetReadyForExecutionSchedulePlansAsync().ConfigureAwait(false);
                if (readyPlans.Count == 0)
                    return;

                _logger.LogInformation(
                    "Processing {Count} ready schedule plan(s).",
                    readyPlans.Count);

                foreach (var schedulePlan in readyPlans)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Source lines 96-97 / 246-247: skip if no workflow type
                    if (schedulePlan.WorkflowTypeId == Guid.Empty)
                        continue;

                    var workflowType = await GetWorkflowTypeAsync(schedulePlan.WorkflowTypeId).ConfigureAwait(false);
                    if (workflowType == null)
                    {
                        _logger.LogWarning(
                            "Schedule plan {SchedulePlanId}: workflow type {TypeId} not found, skipping.",
                            schedulePlan.Id, schedulePlan.WorkflowTypeId);
                        continue;
                    }

                    // Source lines 100-102 / 249-251: check if last workflow is finished
                    bool startNewWorkflow = true;
                    if (schedulePlan.LastStartedWorkflowId.HasValue &&
                        schedulePlan.LastStartedWorkflowId.Value != Guid.Empty)
                    {
                        startNewWorkflow = await IsWorkflowFinishedAsync(schedulePlan.LastStartedWorkflowId.Value).ConfigureAwait(false);
                    }

                    // Source lines 105-184 / 254-333: calculate next trigger per schedule type
                    var nowDateTime = DateTime.UtcNow;
                    DateTime? nextTriggerTime = null;

                    switch (schedulePlan.Type)
                    {
                        case SchedulePlanType.Interval:
                            // Source lines 107-125 / 256-274
                            if (!schedulePlan.LastTriggerTime.HasValue && startNewWorkflow)
                            {
                                schedulePlan.LastTriggerTime = nowDateTime;
                            }
                            nextTriggerTime = FindIntervalSchedulePlanNextTriggerDate(
                                schedulePlan, nowDateTime, schedulePlan.LastTriggerTime);
                            break;

                        case SchedulePlanType.Daily:
                            // Source lines 126-145 / 275-294
                            if (startNewWorkflow)
                            {
                                var dailyStart = schedulePlan.StartDate ?? nowDateTime;
                                nextTriggerTime = FindDailySchedulePlanNextTriggerDate(
                                    schedulePlan, nowDateTime, dailyStart.AddMinutes(1));
                            }
                            else
                            {
                                nextTriggerTime = schedulePlan.NextTriggerTime;
                            }
                            break;

                        case SchedulePlanType.Weekly:
                            // Source lines 146-165 / 295-314
                            if (startNewWorkflow)
                            {
                                if (schedulePlan.LastTriggerTime.HasValue)
                                    nextTriggerTime = schedulePlan.LastTriggerTime.Value.AddDays(7);
                                else
                                    nextTriggerTime = nowDateTime.AddDays(7);

                                if (schedulePlan.EndDate.HasValue && nextTriggerTime > schedulePlan.EndDate.Value)
                                    nextTriggerTime = null;
                            }
                            else
                            {
                                nextTriggerTime = schedulePlan.NextTriggerTime;
                            }
                            break;

                        case SchedulePlanType.Monthly:
                            // Source lines 166-183 / 315-332
                            if (startNewWorkflow)
                            {
                                if (schedulePlan.LastTriggerTime.HasValue)
                                    nextTriggerTime = schedulePlan.LastTriggerTime.Value.AddMonths(1);
                                else
                                    nextTriggerTime = nowDateTime.AddMonths(1);

                                if (schedulePlan.EndDate.HasValue && nextTriggerTime > schedulePlan.EndDate.Value)
                                    nextTriggerTime = null;
                            }
                            else
                            {
                                nextTriggerTime = schedulePlan.NextTriggerTime;
                            }
                            break;
                    }

                    // Source lines 186-197 / 335-346: create workflow if starting new
                    if (startNewWorkflow)
                    {
                        var workflow = await CreateWorkflowAsync(
                            workflowType.Id,
                            schedulePlan.JobAttributes,
                            workflowType.DefaultPriority,
                            schedulePlanId: schedulePlan.Id).ConfigureAwait(false);

                        if (workflow != null)
                        {
                            // Source lines 191/339: set LastStartedWorkflowId
                            schedulePlan.LastStartedWorkflowId = workflow.Id;

                            // Start Step Functions execution
                            await StartStepFunctionsExecutionAsync(workflow).ConfigureAwait(false);
                        }
                    }

                    // Source line 198 / 347: update schedule plan trigger fields
                    await UpdateSchedulePlanTriggerAsync(
                        schedulePlan.Id,
                        startNewWorkflow ? nowDateTime : schedulePlan.LastTriggerTime,
                        nextTriggerTime,
                        null, // system-triggered, no user
                        schedulePlan.LastStartedWorkflowId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Source lines 352-369: error handling with structured logging
                _logger.LogError(ex, "Schedule processing failed.");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Source: <c>JobManager.ProcessJobsAsync(CancellationToken)</c> (lines 228-301).
        /// Replaces the infinite polling loop with a single-invocation method.
        /// Each pending workflow is dispatched to Step Functions.
        /// </remarks>
        public async Task ProcessWorkflowsAsync(CancellationToken cancellationToken = default)
        {
            // Source line 153/231: check if processing is enabled
            if (!_settings.Enabled)
                return;

            try
            {
                // Source line 171/246: get pending workflows
                var pendingWorkflows = await GetPendingWorkflowsAsync().ConfigureAwait(false);
                if (pendingWorkflows.Count == 0)
                    return;

                _logger.LogInformation(
                    "Processing {Count} pending workflow(s).",
                    pendingWorkflows.Count);

                foreach (var workflow in pendingWorkflows)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        // Source lines 177-178/252-253: single-instance constraint
                        var workflowType = workflow.Type ?? await GetWorkflowTypeAsync(workflow.TypeId).ConfigureAwait(false);
                        if (workflowType != null && workflowType.AllowSingleInstance)
                        {
                            var runningOfType = await GetRunningWorkflowsAsync().ConfigureAwait(false);
                            bool hasRunning = runningOfType.Any(w => w.TypeId == workflow.TypeId);
                            if (hasRunning)
                            {
                                _logger.LogInformation(
                                    "Skipping workflow {WorkflowId}: type '{TypeName}' allows single instance and one is already running.",
                                    workflow.Id, workflow.TypeName);
                                continue;
                            }
                        }

                        // Start Step Functions execution (replacing JobPool.RunJobAsync)
                        var started = await StartStepFunctionsExecutionAsync(workflow).ConfigureAwait(false);
                        if (started)
                        {
                            // Update workflow status to Running
                            workflow.Status = WorkflowStatus.Running;
                            workflow.StartedOn = DateTime.UtcNow;
                            await UpdateWorkflowAsync(workflow).ConfigureAwait(false);
                            await PublishWorkflowEventAsync("started", workflow).ConfigureAwait(false);
                        }
                        else
                        {
                            // Step Functions execution failed to start (exception was caught internally)
                            _logger.LogWarning(
                                "Step Functions execution failed to start for workflow {WorkflowId} (type '{TypeName}'). Marking as Failed.",
                                workflow.Id, workflow.TypeName);

                            workflow.Status = WorkflowStatus.Failed;
                            workflow.ErrorMessage = "Step Functions execution failed to start.";
                            workflow.FinishedOn = DateTime.UtcNow;
                            await UpdateWorkflowAsync(workflow).ConfigureAwait(false);
                            await PublishWorkflowEventAsync("failed", workflow).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Source lines 183-200/257-275: per-workflow error handling
                        _logger.LogError(ex,
                            "Failed to process workflow {WorkflowId} (type '{TypeName}').",
                            workflow.Id, workflow.TypeName);

                        workflow.Status = WorkflowStatus.Failed;
                        workflow.ErrorMessage = ex.Message;
                        workflow.FinishedOn = DateTime.UtcNow;
                        await UpdateWorkflowAsync(workflow).ConfigureAwait(false);
                        await PublishWorkflowEventAsync("failed", workflow).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workflow processing failed.");
            }
        }

        /// <summary>
        /// Starts a Step Functions execution for the given workflow.
        /// Replaces <c>JobPool.Current.RunJobAsync(job)</c> from source <c>JobManager.cs</c> line 180.
        /// </summary>
        private async Task<bool> StartStepFunctionsExecutionAsync(Models.Workflow workflow)
        {
            try
            {
                var stepContext = new StepContext
                {
                    WorkflowId = workflow.Id,
                    Aborted = false,
                    Priority = workflow.Priority,
                    Attributes = workflow.Attributes,
                    Result = workflow.Result,
                    Type = workflow.Type
                };

                var executionName = $"workflow-{workflow.Id:N}-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var response = await _stepFunctions.StartExecutionAsync(new StartExecutionRequest
                {
                    StateMachineArn = _settings.StepFunctionsStateMachineArn,
                    Name = executionName,
                    Input = JsonSerializer.Serialize(stepContext)
                }).ConfigureAwait(false);

                workflow.StepFunctionsExecutionArn = response.ExecutionArn;
                stepContext.StepFunctionsExecutionArn = response.ExecutionArn;

                _logger.LogInformation(
                    "Step Functions execution started for workflow {WorkflowId}: ARN {ExecutionArn}.",
                    workflow.Id, stepContext.StepFunctionsExecutionArn);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start Step Functions execution for workflow {WorkflowId}.",
                    workflow.Id);
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── SNS Domain Event Publishing (AAP Section 0.8.5)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Publishes a domain event to SNS following the AAP Section 0.8.5 event naming convention:
        /// <c>{domain}.{entity}.{action}</c> (e.g., <c>workflow.workflow.created</c>).
        /// All events include an idempotency key in message attributes per AAP requirement.
        /// </summary>
        private async Task PublishWorkflowEventAsync(string action, Models.Workflow workflow)
        {
            if (string.IsNullOrWhiteSpace(_settings.SnsTopicArn))
                return;

            try
            {
                var eventType = $"workflow.workflow.{action}";
                var idempotencyKey = $"{workflow.Id}-{action}-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var correlationId = workflow.Id.ToString();

                var eventPayload = new Dictionary<string, object>
                {
                    ["eventType"] = eventType,
                    ["workflowId"] = workflow.Id.ToString(),
                    ["status"] = workflow.Status.ToString(),
                    ["typeName"] = workflow.TypeName ?? string.Empty,
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["correlationId"] = correlationId,
                    ["idempotencyKey"] = idempotencyKey
                };

                var publishRequest = new PublishRequest
                {
                    TopicArn = _settings.SnsTopicArn,
                    Message = JsonSerializer.Serialize(eventPayload),
                    Subject = eventType,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["idempotencyKey"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = idempotencyKey
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        }
                    }
                };

                await _sns.PublishAsync(publishRequest).ConfigureAwait(false);

                _logger.LogInformation(
                    "Published SNS event '{EventType}' for workflow {WorkflowId}.",
                    eventType, workflow.Id);
            }
            catch (Exception ex)
            {
                // Log but do not throw — event publishing failure should not break workflow operations
                _logger.LogError(ex,
                    "Failed to publish SNS event 'workflow.workflow.{Action}' for workflow {WorkflowId}.",
                    action, workflow.Id);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── DynamoDB Helper / Mapping Methods (replacing JobDataService helpers)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a <see cref="Models.Workflow"/> to a DynamoDB item.
        /// Replaces the Npgsql parameterized INSERT from <c>JobDataService.CreateJob</c> (lines 25-74).
        /// </summary>
        private Dictionary<string, AttributeValue> MapWorkflowToDynamoDbItem(Models.Workflow workflow)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflow.Id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entity_type"] = new AttributeValue { S = "Workflow" },
                ["id"] = new AttributeValue { S = workflow.Id.ToString() },
                ["type_id"] = new AttributeValue { S = workflow.TypeId.ToString() },
                ["type_name"] = new AttributeValue { S = workflow.TypeName ?? string.Empty },
                ["complete_class_name"] = new AttributeValue { S = workflow.CompleteClassName ?? string.Empty },
                ["status"] = new AttributeValue { N = ((int)workflow.Status).ToString() },
                ["priority"] = new AttributeValue { N = ((int)workflow.Priority).ToString() },
                ["created_on"] = new AttributeValue { S = workflow.CreatedOn.ToString("o") },
                ["last_modified_on"] = new AttributeValue { S = workflow.LastModifiedOn.ToString("o") }
            };

            // Nullable fields — use NULL attribute or S/N
            if (workflow.Attributes != null)
                item["attributes"] = new AttributeValue { S = JsonSerializer.Serialize(workflow.Attributes) };
            else
                item["attributes"] = new AttributeValue { NULL = true };

            if (workflow.Result != null)
                item["result"] = new AttributeValue { S = JsonSerializer.Serialize(workflow.Result) };
            else
                item["result"] = new AttributeValue { NULL = true };

            if (workflow.StartedOn.HasValue)
                item["started_on"] = new AttributeValue { S = workflow.StartedOn.Value.ToString("o") };
            else
                item["started_on"] = new AttributeValue { NULL = true };

            if (workflow.FinishedOn.HasValue)
                item["finished_on"] = new AttributeValue { S = workflow.FinishedOn.Value.ToString("o") };
            else
                item["finished_on"] = new AttributeValue { NULL = true };

            if (workflow.AbortedBy.HasValue)
                item["aborted_by"] = new AttributeValue { S = workflow.AbortedBy.Value.ToString() };
            else
                item["aborted_by"] = new AttributeValue { NULL = true };

            if (workflow.CanceledBy.HasValue)
                item["canceled_by"] = new AttributeValue { S = workflow.CanceledBy.Value.ToString() };
            else
                item["canceled_by"] = new AttributeValue { NULL = true };

            if (!string.IsNullOrEmpty(workflow.ErrorMessage))
                item["error_message"] = new AttributeValue { S = workflow.ErrorMessage };
            else
                item["error_message"] = new AttributeValue { NULL = true };

            if (workflow.SchedulePlanId.HasValue)
                item["schedule_plan_id"] = new AttributeValue { S = workflow.SchedulePlanId.Value.ToString() };
            else
                item["schedule_plan_id"] = new AttributeValue { NULL = true };

            if (workflow.CreatedBy.HasValue)
                item["created_by"] = new AttributeValue { S = workflow.CreatedBy.Value.ToString() };
            else
                item["created_by"] = new AttributeValue { NULL = true };

            if (workflow.LastModifiedBy.HasValue)
                item["last_modified_by"] = new AttributeValue { S = workflow.LastModifiedBy.Value.ToString() };
            else
                item["last_modified_by"] = new AttributeValue { NULL = true };

            if (!string.IsNullOrEmpty(workflow.StepFunctionsExecutionArn))
                item["step_functions_execution_arn"] = new AttributeValue { S = workflow.StepFunctionsExecutionArn };
            else
                item["step_functions_execution_arn"] = new AttributeValue { NULL = true };

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item into a <see cref="Models.Workflow"/>.
        /// Replaces <c>DataTable.Rows[0].MapTo&lt;Job&gt;()</c> from <c>JobDataService.GetJob</c>.
        /// </summary>
        private Models.Workflow MapDynamoDbItemToWorkflow(Dictionary<string, AttributeValue> item)
        {
            var workflow = new Models.Workflow
            {
                Id = GetGuidAttribute(item, "id"),
                TypeId = GetGuidAttribute(item, "type_id"),
                TypeName = GetNullableStringAttribute(item, "type_name"),
                CompleteClassName = GetNullableStringAttribute(item, "complete_class_name"),
                Status = (WorkflowStatus)GetIntAttribute(item, "status"),
                Priority = (WorkflowPriority)GetIntAttribute(item, "priority"),
                CreatedOn = GetDateTimeAttribute(item, "created_on"),
                LastModifiedOn = GetDateTimeAttribute(item, "last_modified_on"),
                StartedOn = GetNullableDateTimeAttribute(item, "started_on"),
                FinishedOn = GetNullableDateTimeAttribute(item, "finished_on"),
                AbortedBy = GetNullableGuidAttribute(item, "aborted_by"),
                CanceledBy = GetNullableGuidAttribute(item, "canceled_by"),
                ErrorMessage = GetNullableStringAttribute(item, "error_message"),
                SchedulePlanId = GetNullableGuidAttribute(item, "schedule_plan_id"),
                CreatedBy = GetNullableGuidAttribute(item, "created_by"),
                LastModifiedBy = GetNullableGuidAttribute(item, "last_modified_by"),
                StepFunctionsExecutionArn = GetNullableStringAttribute(item, "step_functions_execution_arn")
            };

            // Deserialize JSON attributes
            var attrsStr = GetStringAttribute(item, "attributes");
            if (!string.IsNullOrEmpty(attrsStr))
            {
                workflow.Attributes = DeserializeDictionaryWithPrimitives(attrsStr);
            }

            var resultStr = GetStringAttribute(item, "result");
            if (!string.IsNullOrEmpty(resultStr))
            {
                workflow.Result = DeserializeDictionaryWithPrimitives(resultStr);
            }

            return workflow;
        }

        /// <summary>
        /// Converts a <see cref="SchedulePlan"/> to a DynamoDB item.
        /// Replaces <c>JobDataService.CreateSchedule</c> (lines 295-342).
        /// </summary>
        private Dictionary<string, AttributeValue> MapSchedulePlanToDynamoDbItem(SchedulePlan plan)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"SCHEDULE#{plan.Id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entity_type"] = new AttributeValue { S = "SchedulePlan" },
                ["id"] = new AttributeValue { S = plan.Id.ToString() },
                ["name"] = new AttributeValue { S = plan.Name ?? string.Empty },
                ["type"] = new AttributeValue { N = ((int)plan.Type).ToString() },
                ["enabled"] = new AttributeValue { BOOL = plan.Enabled },
                ["created_on"] = new AttributeValue { S = plan.CreatedOn.ToString("o") },
                ["last_modified_on"] = new AttributeValue { S = plan.LastModifiedOn.ToString("o") }
            };

            // Nullable DateTime fields
            if (plan.StartDate.HasValue)
                item["start_date"] = new AttributeValue { S = plan.StartDate.Value.ToString("o") };
            else
                item["start_date"] = new AttributeValue { NULL = true };

            if (plan.EndDate.HasValue)
                item["end_date"] = new AttributeValue { S = plan.EndDate.Value.ToString("o") };
            else
                item["end_date"] = new AttributeValue { NULL = true };

            if (plan.LastTriggerTime.HasValue)
                item["last_trigger_time"] = new AttributeValue { S = plan.LastTriggerTime.Value.ToString("o") };
            else
                item["last_trigger_time"] = new AttributeValue { NULL = true };

            if (plan.NextTriggerTime.HasValue)
                item["next_trigger_time"] = new AttributeValue { S = plan.NextTriggerTime.Value.ToString("o") };
            else
                item["next_trigger_time"] = new AttributeValue { NULL = true };

            // Nullable int fields
            if (plan.IntervalInMinutes.HasValue)
                item["interval_in_minutes"] = new AttributeValue { N = plan.IntervalInMinutes.Value.ToString() };
            else
                item["interval_in_minutes"] = new AttributeValue { NULL = true };

            if (plan.StartTimespan.HasValue)
                item["start_timespan"] = new AttributeValue { N = plan.StartTimespan.Value.ToString() };
            else
                item["start_timespan"] = new AttributeValue { NULL = true };

            if (plan.EndTimespan.HasValue)
                item["end_timespan"] = new AttributeValue { N = plan.EndTimespan.Value.ToString() };
            else
                item["end_timespan"] = new AttributeValue { NULL = true };

            // ScheduledDays serialized as JSON string (replacing NpgsqlDbType.Json)
            if (plan.ScheduledDays != null)
                item["scheduled_days"] = new AttributeValue { S = JsonSerializer.Serialize(plan.ScheduledDays) };
            else
                item["scheduled_days"] = new AttributeValue { NULL = true };

            // JobAttributes serialized as JSON string
            if (plan.JobAttributes != null)
                item["job_attributes"] = new AttributeValue { S = JsonSerializer.Serialize(plan.JobAttributes) };
            else
                item["job_attributes"] = new AttributeValue { NULL = true };

            // Guid fields
            if (plan.WorkflowTypeId != Guid.Empty)
                item["job_type_id"] = new AttributeValue { S = plan.WorkflowTypeId.ToString() };
            else
                item["job_type_id"] = new AttributeValue { NULL = true };

            if (plan.LastStartedWorkflowId.HasValue)
                item["last_started_job_id"] = new AttributeValue { S = plan.LastStartedWorkflowId.Value.ToString() };
            else
                item["last_started_job_id"] = new AttributeValue { NULL = true };

            if (plan.LastModifiedBy.HasValue)
                item["last_modified_by"] = new AttributeValue { S = plan.LastModifiedBy.Value.ToString() };
            else
                item["last_modified_by"] = new AttributeValue { NULL = true };

            return item;
        }

        /// <summary>
        /// Deserializes a DynamoDB item into a <see cref="SchedulePlan"/>.
        /// Replaces <c>DataTable.Rows[0].MapTo&lt;SchedulePlan&gt;()</c> from <c>JobDataService</c>.
        /// </summary>
        private SchedulePlan MapDynamoDbItemToSchedulePlan(Dictionary<string, AttributeValue> item)
        {
            var plan = new SchedulePlan
            {
                Id = GetGuidAttribute(item, "id"),
                Name = GetStringAttribute(item, "name"),
                Type = (SchedulePlanType)GetIntAttribute(item, "type"),
                Enabled = GetBoolAttribute(item, "enabled"),
                CreatedOn = GetDateTimeAttribute(item, "created_on"),
                LastModifiedOn = GetDateTimeAttribute(item, "last_modified_on"),
                StartDate = GetNullableDateTimeAttribute(item, "start_date"),
                EndDate = GetNullableDateTimeAttribute(item, "end_date"),
                LastTriggerTime = GetNullableDateTimeAttribute(item, "last_trigger_time"),
                NextTriggerTime = GetNullableDateTimeAttribute(item, "next_trigger_time"),
                IntervalInMinutes = GetNullableIntAttribute(item, "interval_in_minutes"),
                StartTimespan = GetNullableIntAttribute(item, "start_timespan"),
                EndTimespan = GetNullableIntAttribute(item, "end_timespan"),
                WorkflowTypeId = GetGuidAttribute(item, "job_type_id"),
                LastStartedWorkflowId = GetNullableGuidAttribute(item, "last_started_job_id"),
                LastModifiedBy = GetNullableGuidAttribute(item, "last_modified_by")
            };

            // Deserialize ScheduledDays JSON
            var scheduledDaysStr = GetStringAttribute(item, "scheduled_days");
            if (!string.IsNullOrEmpty(scheduledDaysStr))
            {
                plan.ScheduledDays = JsonSerializer.Deserialize<SchedulePlanDaysOfWeek>(scheduledDaysStr) ?? new SchedulePlanDaysOfWeek();
            }

            // Deserialize JobAttributes JSON
            var jobAttrsStr = GetStringAttribute(item, "job_attributes");
            if (!string.IsNullOrEmpty(jobAttrsStr))
            {
                plan.JobAttributes = DeserializeDictionaryWithPrimitives(jobAttrsStr);
            }

            return plan;
        }

        /// <summary>
        /// Deserializes a DynamoDB item into a <see cref="WorkflowType"/>.
        /// Used by <see cref="GetWorkflowTypesAsync"/> and <see cref="GetWorkflowTypeAsync"/>.
        /// </summary>
        private WorkflowType MapDynamoDbItemToWorkflowType(Dictionary<string, AttributeValue> item)
        {
            return new WorkflowType
            {
                Id = GetGuidAttribute(item, "id"),
                Name = GetStringAttribute(item, "name"),
                DefaultPriority = (WorkflowPriority)GetIntAttribute(item, "default_priority"),
                Assembly = GetStringAttribute(item, "assembly"),
                CompleteClassName = GetStringAttribute(item, "complete_class_name"),
                AllowSingleInstance = GetBoolAttribute(item, "allow_single_instance")
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // ── DynamoDB Attribute Extraction Helpers
        // ════════════════════════════════════════════════════════════════════

        private static string GetStringAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null && !attr.NULL)
                return attr.S;
            return string.Empty;
        }

        /// <summary>
        /// Retrieves a nullable string attribute from a DynamoDB item.
        /// Returns <c>null</c> when the attribute is missing, marked NULL, or has a null S value.
        /// Used for Workflow model properties typed as <c>string?</c> (ErrorMessage, TypeName, etc.).
        /// </summary>
        private static string? GetNullableStringAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.S != null && !attr.NULL)
                return attr.S;
            return null;
        }

        /// <summary>
        /// Deserializes a JSON string into <c>Dictionary&lt;string, object&gt;</c> with proper CLR types.
        /// <see cref="JsonSerializer.Deserialize{T}"/> for <c>Dictionary&lt;string, object&gt;</c> yields
        /// <see cref="JsonElement"/> values, which fail FluentAssertions equivalence checks against
        /// primitive CLR types. This method resolves <see cref="JsonElement"/> values to <c>string</c>,
        /// <c>long</c>/<c>double</c>, <c>bool</c>, or <c>null</c>.
        /// </summary>
        private static Dictionary<string, object> DeserializeDictionaryWithPrimitives(string json)
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (raw == null)
                return new Dictionary<string, object>();

            var result = new Dictionary<string, object>(raw.Count);
            foreach (var kvp in raw)
            {
                result[kvp.Key] = ConvertJsonElement(kvp.Value);
            }
            return result;
        }

        /// <summary>
        /// Converts a <see cref="JsonElement"/> to the most appropriate CLR primitive type.
        /// </summary>
        private static object ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString()!;
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var longVal))
                        return longVal;
                    return element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null!;
                case JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (var item in element.EnumerateArray())
                        list.Add(ConvertJsonElement(item));
                    return list;
                }
                case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in element.EnumerateObject())
                        dict[prop.Name] = ConvertJsonElement(prop.Value);
                    return dict;
                }
                default:
                    return element.GetRawText();
            }
        }

        private static Guid GetGuidAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            var str = GetStringAttribute(item, key);
            return Guid.TryParse(str, out var result) ? result : Guid.Empty;
        }

        private static Guid? GetNullableGuidAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (!item.TryGetValue(key, out var attr) || attr.NULL || string.IsNullOrEmpty(attr.S))
                return null;
            return Guid.TryParse(attr.S, out var result) ? result : null;
        }

        private static int GetIntAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && attr.N != null && !attr.NULL)
                return int.TryParse(attr.N, out var result) ? result : 0;
            return 0;
        }

        private static int? GetNullableIntAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (!item.TryGetValue(key, out var attr) || attr.NULL || string.IsNullOrEmpty(attr.N))
                return null;
            return int.TryParse(attr.N, out var result) ? result : null;
        }

        private static bool GetBoolAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && !attr.NULL)
                return attr.BOOL;
            return false;
        }

        private static DateTime GetDateTimeAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            var str = GetStringAttribute(item, key);
            if (DateTime.TryParse(str, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result))
                return result;
            return DateTime.MinValue;
        }

        private static DateTime? GetNullableDateTimeAttribute(Dictionary<string, AttributeValue> item, string key)
        {
            if (!item.TryGetValue(key, out var attr) || attr.NULL || string.IsNullOrEmpty(attr.S))
                return null;
            if (DateTime.TryParse(attr.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out var result))
                return result;
            return null;
        }
    }
}
