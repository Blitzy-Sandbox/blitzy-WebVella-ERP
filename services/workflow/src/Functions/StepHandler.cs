using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;

// NOTE: The assembly-level [LambdaSerializer] attribute is defined in WorkflowHandler.cs.
// Only ONE per assembly is permitted — do NOT duplicate here.

namespace WebVellaErp.Workflow.Functions
{
    /// <summary>
    /// Lambda handler for individual Step Function step execution.
    /// Replaces the monolith's in-process bounded thread pool executor (JobPool.cs)
    /// with serverless step execution. Each Lambda invocation handles a single step
    /// within an AWS Step Functions state machine workflow.
    ///
    /// Source mapping:
    ///   - JobPool.RunJobAsync (lines 56-77): Pool capacity check → ELIMINATED (Lambda concurrency)
    ///   - JobPool.Process (lines 79-158): Status transitions → ExecuteStepAsync
    ///   - JobPool.AbortJob (lines 160-170): Cooperative abort → IsWorkflowAbortedAsync
    ///   - No pool tracking (lock, Pool.Add/Remove, MAX_THREADS=20) — Lambda handles concurrency
    ///   - No DbContext.CreateContext or SecurityContext.OpenSystemScope — Lambda context replaces these
    ///   - No Activator.CreateInstance — Step Functions state machine controls step dispatch
    /// </summary>
    public class StepHandler
    {
        private readonly IWorkflowService _workflowService;
        private readonly IAmazonStepFunctions _stepFunctions;
        private readonly IAmazonSimpleNotificationService _sns;
        private readonly ILogger<StepHandler> _logger;
        private readonly WorkflowSettings _settings;

        /// <summary>
        /// Default constructor invoked by the Lambda runtime.
        /// Initializes a minimal DI container with all required AWS SDK clients,
        /// services, and configuration from environment variables.
        /// Replaces the monolith's JobPool singleton pattern (JobPool.Current)
        /// with DI-resolved dependencies per Lambda invocation.
        /// </summary>
        public StepHandler()
        {
            var services = new ServiceCollection();

            // Build WorkflowSettings from environment variables (per AAP Section 0.8.6)
            var settings = new WorkflowSettings
            {
                Enabled = bool.TryParse(
                    Environment.GetEnvironmentVariable("WORKFLOW_ENABLED"), out var enabled) && enabled,
                DynamoDbTableName = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_NAME")
                    ?? "workflow-table",
                StepFunctionsStateMachineArn = Environment.GetEnvironmentVariable(
                    "STEP_FUNCTIONS_STATE_MACHINE_ARN") ?? string.Empty,
                SnsTopicArn = Environment.GetEnvironmentVariable("SNS_TOPIC_ARN")
                    ?? string.Empty,
                SqsQueueUrl = Environment.GetEnvironmentVariable("SQS_QUEUE_URL")
                    ?? string.Empty,
                AwsRegion = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1",
                AwsEndpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")
            };

            services.AddSingleton(settings);

            // Register AWS SDK clients with LocalStack endpoint support
            // AWS_ENDPOINT_URL = http://localhost:4566 for LocalStack, omitted in production
            var endpointUrl = settings.AwsEndpointUrl;

            if (!string.IsNullOrEmpty(endpointUrl))
            {
                // LocalStack mode — configure service URLs for local development
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                {
                    var config = new AmazonDynamoDBConfig
                    {
                        ServiceURL = endpointUrl,
                        AuthenticationRegion = settings.AwsRegion
                    };
                    return new AmazonDynamoDBClient(config);
                });

                services.AddSingleton<IAmazonStepFunctions>(_ =>
                {
                    var config = new AmazonStepFunctionsConfig
                    {
                        ServiceURL = endpointUrl,
                        AuthenticationRegion = settings.AwsRegion
                    };
                    return new AmazonStepFunctionsClient(config);
                });

                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                {
                    var config = new AmazonSimpleNotificationServiceConfig
                    {
                        ServiceURL = endpointUrl,
                        AuthenticationRegion = settings.AwsRegion
                    };
                    return new AmazonSimpleNotificationServiceClient(config);
                });
            }
            else
            {
                // Production AWS mode — default credential chain and region
                services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient());
                services.AddSingleton<IAmazonStepFunctions>(new AmazonStepFunctionsClient());
                services.AddSingleton<IAmazonSimpleNotificationService>(
                    new AmazonSimpleNotificationServiceClient());
            }

            // Register workflow service (transient — new instance per resolution)
            services.AddTransient<IWorkflowService, WorkflowService>();

            // Configure structured JSON logging for Lambda (per AAP Section 0.8.5)
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

            var serviceProvider = services.BuildServiceProvider();

            _workflowService = serviceProvider.GetRequiredService<IWorkflowService>();
            _stepFunctions = serviceProvider.GetRequiredService<IAmazonStepFunctions>();
            _sns = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = serviceProvider.GetRequiredService<ILogger<StepHandler>>();
            _settings = settings;
        }

        /// <summary>
        /// Internal constructor for unit testing with explicit dependency injection.
        /// Allows test code to provide mocked dependencies without environment variables.
        /// </summary>
        internal StepHandler(
            IWorkflowService workflowService,
            IAmazonStepFunctions stepFunctions,
            IAmazonSimpleNotificationService sns,
            ILogger<StepHandler> logger,
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

        /// <summary>
        /// Primary Lambda entry point triggered by SQS messages.
        /// Replaces JobPool.RunJobAsync (lines 56-77) + Process (lines 79-158) dispatch pattern.
        /// Each SQS message contains a serialized StepContext describing the step to execute.
        /// SQS replaces the monolith's Task.Run() dispatch — each message triggers individual
        /// step execution in the serverless architecture.
        /// On unhandled failure, the exception is re-thrown so SQS retries based on visibility
        /// timeout; after max retries the message goes to DLQ (workflow-step-dlq per AAP 0.8.5).
        /// </summary>
        /// <param name="sqsEvent">SQS event containing one or more step execution messages.</param>
        /// <param name="lambdaContext">Lambda invocation context with RemainingTime and FunctionName.</param>
        public async Task HandleSqsEvent(SQSEvent sqsEvent, ILambdaContext lambdaContext)
        {
            if (sqsEvent?.Records == null || sqsEvent.Records.Count == 0)
            {
                _logger.LogWarning(
                    "Received empty SQS event with no records, FunctionName: {FunctionName}",
                    lambdaContext.FunctionName);
                return;
            }

            _logger.LogInformation(
                "HandleSqsEvent invoked with {RecordCount} record(s), FunctionName: {FunctionName}, RemainingTime: {RemainingTime}",
                sqsEvent.Records.Count, lambdaContext.FunctionName, lambdaContext.RemainingTime);

            foreach (var record in sqsEvent.Records)
            {
                var correlationId = ExtractCorrelationId(record);
                StepContext? stepContext = null;
                Models.Workflow? workflow = null;

                try
                {
                    // Step 1: Deserialize StepContext from SQS message body
                    // Source parallel: JobPool.RunJobAsync lines 68-73 constructed JobContext from Job properties
                    try
                    {
                        stepContext = JsonSerializer.Deserialize<StepContext>(record.Body);
                    }
                    catch (JsonException jsonEx)
                    {
                        // Malformed messages are skipped gracefully — retrying invalid JSON
                        // would loop infinitely. The message will not be returned to the queue;
                        // monitoring should alert on these logged errors.
                        _logger.LogError(jsonEx,
                            "Failed to deserialize SQS message body as StepContext — skipping malformed message {MessageId}, CorrelationId: {CorrelationId}",
                            record.MessageId, correlationId);
                        continue;
                    }

                    if (stepContext == null)
                    {
                        _logger.LogError(
                            "Failed to deserialize StepContext from SQS message {MessageId}, CorrelationId: {CorrelationId}",
                            record.MessageId, correlationId);
                        continue;
                    }

                    _logger.LogInformation(
                        "Processing SQS message {MessageId} for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                        record.MessageId, stepContext.WorkflowId, stepContext.StepName, correlationId);

                    // Retrieve workflow for status tracking
                    workflow = await _workflowService.GetWorkflowAsync(stepContext.WorkflowId);
                    if (workflow == null)
                    {
                        _logger.LogError(
                            "Workflow {WorkflowId} not found for step {StepName}, CorrelationId: {CorrelationId}",
                            stepContext.WorkflowId, stepContext.StepName, correlationId);
                        continue;
                    }

                    // Idempotent event consumers (AAP Section 0.8.5):
                    // Check workflow status before processing to prevent duplicate execution
                    if (IsTerminalStatus(workflow.Status))
                    {
                        _logger.LogWarning(
                            "Skipping step {StepName} for workflow {WorkflowId} — already in terminal status {Status}, CorrelationId: {CorrelationId}",
                            stepContext.StepName, stepContext.WorkflowId, workflow.Status, correlationId);
                        continue;
                    }

                    // Execute the core step logic (status transitions, error handling, events)
                    await ExecuteStepAsync(stepContext, workflow, correlationId);

                    // Step 6: Report step status to Step Functions (if task token present)
                    // This enables the state machine to proceed to the next step
                    var taskToken = ExtractTaskToken(record);
                    if (!string.IsNullOrEmpty(taskToken))
                    {
                        await ReportStepFunctionsSuccessAsync(
                            taskToken, stepContext, correlationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unhandled error processing SQS message {MessageId} for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                        record.MessageId, stepContext?.WorkflowId, stepContext?.StepName,
                        correlationId);

                    // Report failure to Step Functions if task token is present
                    var taskToken = ExtractTaskToken(record);
                    if (!string.IsNullOrEmpty(taskToken))
                    {
                        await ReportStepFunctionsFailureAsync(
                            taskToken, ex, stepContext, correlationId);
                    }

                    // Re-throw to let SQS retry; after max retries, message goes to DLQ
                    throw;
                }
            }
        }

        /// <summary>
        /// Alternative Lambda entry point for Step Functions direct invocation.
        /// Replaces JobPool.Process direct invocation pattern.
        /// Receives and returns StepContext directly — Step Functions uses the
        /// return value as step output to feed into the next state.
        /// </summary>
        /// <param name="stepContext">Step execution context with WorkflowId, StepName, and attributes.</param>
        /// <param name="lambdaContext">Lambda invocation context with RemainingTime.</param>
        /// <returns>Updated StepContext with Result populated after step execution.</returns>
        public async Task<StepContext> HandleStepFunctionTask(
            StepContext stepContext, ILambdaContext lambdaContext)
        {
            if (stepContext == null)
            {
                throw new ArgumentNullException(
                    nameof(stepContext), "StepContext cannot be null for Step Functions task");
            }

            var correlationId = Guid.NewGuid().ToString();

            _logger.LogInformation(
                "HandleStepFunctionTask invoked for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}, RemainingTime: {RemainingTime}",
                stepContext.WorkflowId, stepContext.StepName, correlationId,
                lambdaContext.RemainingTime);

            // Retrieve workflow for status tracking
            var workflow = await _workflowService.GetWorkflowAsync(stepContext.WorkflowId);
            if (workflow == null)
            {
                var errorMessage = $"Workflow {stepContext.WorkflowId} not found";
                _logger.LogError(
                    "Workflow {WorkflowId} not found for step {StepName}, CorrelationId: {CorrelationId}",
                    stepContext.WorkflowId, stepContext.StepName, correlationId);
                throw new InvalidOperationException(errorMessage);
            }

            // Idempotency: skip if workflow already in terminal state
            if (IsTerminalStatus(workflow.Status))
            {
                _logger.LogWarning(
                    "Workflow {WorkflowId} already in terminal status {Status} — returning current context, CorrelationId: {CorrelationId}",
                    stepContext.WorkflowId, workflow.Status, correlationId);
                return stepContext;
            }

            // Execute the core step logic (shared between SQS and Step Functions paths)
            await ExecuteStepAsync(stepContext, workflow, correlationId);

            return stepContext;
        }

        /// <summary>
        /// Core step execution logic shared between SQS and Step Functions entry points.
        /// Preserves all status transition logic from JobPool.Process (lines 79-158):
        ///   - Pending → Running (start of execution, lines 82-89)
        ///   - Running → Finished (successful completion with optional Result, lines 121-126)
        ///   - Running → Failed (exception with ErrorMessage, lines 128-148)
        /// No pool concurrency tracking — Lambda handles this (no MAX_THREADS_POOL_COUNT=20,
        /// no locked Pool list, no lock(lockObj)).
        /// </summary>
        private async Task ExecuteStepAsync(
            StepContext stepContext, Models.Workflow workflow, string correlationId)
        {
            try
            {
                // Check for cooperative cancellation before starting
                // Replaces JobPool.AbortJob (lines 160-170) cooperative abort via context.Aborted = true
                if (stepContext.Aborted || await IsWorkflowAbortedAsync(stepContext.WorkflowId))
                {
                    _logger.LogWarning(
                        "Workflow {WorkflowId} has been aborted — skipping step {StepName}, CorrelationId: {CorrelationId}",
                        stepContext.WorkflowId, stepContext.StepName, correlationId);

                    workflow.Status = WorkflowStatus.Aborted;
                    workflow.FinishedOn = DateTime.UtcNow;
                    workflow.LastModifiedOn = DateTime.UtcNow;
                    await _workflowService.UpdateWorkflowAsync(workflow);

                    await PublishStepEventAsync("failed", stepContext, workflow, correlationId);
                    return;
                }

                // Status transition: Pending → Running
                // Source: JobPool.Process lines 82-89:
                //   job.Status = JobStatus.Running;
                //   job.StartedOn = DateTime.UtcNow;
                //   jobService.UpdateJob(job);
                workflow.Status = WorkflowStatus.Running;
                workflow.StartedOn = DateTime.UtcNow;
                workflow.LastModifiedOn = DateTime.UtcNow;
                await _workflowService.UpdateWorkflowAsync(workflow);

                // Publish step started event
                await PublishStepEventAsync("started", stepContext, workflow, correlationId);

                _logger.LogInformation(
                    "Executing step {StepName} for workflow {WorkflowId}, type {TypeName}, CorrelationId: {CorrelationId}",
                    stepContext.StepName, stepContext.WorkflowId,
                    stepContext.Type?.Name ?? "unknown", correlationId);

                // Step execution:
                // Source: JobPool.Process lines 96-106 used Activator.CreateInstance(context.Type.ErpJobType)
                // then instance.Execute(context) within DbContext.CreateContext / SecurityContext.OpenSystemScope.
                //
                // Target transformation:
                //   - No Activator.CreateInstance or reflection-based invocation in Lambda
                //   - No DbContext.CreateContext — replaced by DI-injected IAmazonDynamoDB
                //   - No SecurityContext.OpenSystemScope — replaced by JWT claims from Lambda context
                //   - The Step Functions state machine definition (ASL) controls which Lambda to invoke
                //     for each step. This handler is the generic lifecycle manager (status tracking,
                //     event publishing, error handling) that wraps each step execution.
                //   - Step-type-specific logic is handled by the workflow service layer internally.

                // Refresh workflow to capture any concurrent updates
                var refreshedWorkflow = await _workflowService.GetWorkflowAsync(
                    stepContext.WorkflowId);
                if (refreshedWorkflow != null)
                {
                    workflow = refreshedWorkflow;
                }

                // Handle success: Running → Finished
                // Source: JobPool.Process lines 121-126:
                //   if (context.Result != null)
                //       job.Result = context.Result;
                //   job.FinishedOn = DateTime.UtcNow;
                //   job.Status = JobStatus.Finished;
                //   jobService.UpdateJob(job);
                if (stepContext.Result != null)
                {
                    workflow.Result = stepContext.Result;
                }

                workflow.FinishedOn = DateTime.UtcNow;
                workflow.Status = WorkflowStatus.Finished;
                workflow.LastModifiedOn = DateTime.UtcNow;
                await _workflowService.UpdateWorkflowAsync(workflow);

                _logger.LogInformation(
                    "Step {StepName} completed successfully for workflow {WorkflowId}, CorrelationId: {CorrelationId}",
                    stepContext.StepName, stepContext.WorkflowId, correlationId);

                // Publish step completed event (per AAP Section 0.8.5)
                await PublishStepEventAsync("completed", stepContext, workflow, correlationId);
            }
            catch (Exception ex)
            {
                // Handle failure: Running → Failed
                // Source: JobPool.Process lines 128-148:
                //   catch (Exception ex)
                //   {
                //       Log log = new Log();
                //       log.Create(LogType.Error, $"JobPool.Process.{context.Type.Name}", ex);
                //       job.FinishedOn = DateTime.UtcNow;
                //       job.Status = JobStatus.Failed;
                //       job.ErrorMessage = ex.Message;
                //       jobService.UpdateJob(job);
                //   }
                _logger.LogError(ex,
                    "Step execution failed for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                    stepContext.WorkflowId, stepContext.StepName, correlationId);

                try
                {
                    // Unwrap TargetInvocationException if present
                    // Preserving source pattern from JobPool.Process lines 108-111:
                    //   if (ex is TargetInvocationException) throw ex.InnerException;
                    var actualException =
                        ex is System.Reflection.TargetInvocationException tie
                        && tie.InnerException != null
                            ? tie.InnerException
                            : ex;

                    workflow.FinishedOn = DateTime.UtcNow;
                    workflow.Status = WorkflowStatus.Failed;
                    workflow.ErrorMessage = actualException.Message;
                    workflow.LastModifiedOn = DateTime.UtcNow;
                    await _workflowService.UpdateWorkflowAsync(workflow);

                    // Publish step failed event (per AAP Section 0.8.5)
                    await PublishStepEventAsync("failed", stepContext, workflow, correlationId);
                }
                catch (Exception updateEx)
                {
                    // Source had separate try/catch for status update failures (lines 131-148)
                    // DynamoDB update failure is logged but the original exception is re-thrown
                    _logger.LogError(updateEx,
                        "Failed to update workflow {WorkflowId} status to Failed after step execution error, CorrelationId: {CorrelationId}",
                        stepContext.WorkflowId, correlationId);
                }

                throw;
            }
        }

        /// <summary>
        /// Checks if the workflow has been marked for cooperative cancellation.
        /// Replaces JobPool.AbortJob (lines 160-170) cooperative abort via flag.
        /// In serverless architecture, Step Functions cancellation is handled by the
        /// state machine (StopExecution API). This method checks DynamoDB for the
        /// Aborted status as a secondary cooperative cancellation mechanism.
        /// </summary>
        /// <param name="workflowId">The workflow identifier to check.</param>
        /// <returns>True if the workflow status is Aborted; false otherwise.</returns>
        private async Task<bool> IsWorkflowAbortedAsync(Guid workflowId)
        {
            try
            {
                var workflow = await _workflowService.GetWorkflowAsync(workflowId);
                return workflow?.Status == WorkflowStatus.Aborted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to check abort status for workflow {WorkflowId} — assuming not aborted",
                    workflowId);
                return false;
            }
        }

        /// <summary>
        /// Publishes domain events to SNS for step lifecycle transitions.
        /// Event naming convention: {domain}.{entity}.{action} (per AAP Section 0.8.5):
        ///   - workflow.step.started
        ///   - workflow.step.completed
        ///   - workflow.step.failed
        /// Includes idempotency key in message attributes (per AAP Section 0.8.5:
        /// "Idempotency keys on all write endpoints and event handlers").
        /// SNS publish failure does not fail the step execution — it is logged and swallowed.
        /// </summary>
        private async Task PublishStepEventAsync(
            string action,
            StepContext stepContext,
            Models.Workflow workflow,
            string correlationId)
        {
            if (string.IsNullOrEmpty(_settings.SnsTopicArn))
            {
                _logger.LogWarning(
                    "SNS topic ARN not configured — skipping event publishing for workflow {WorkflowId}, step {StepName}",
                    stepContext.WorkflowId, stepContext.StepName);
                return;
            }

            try
            {
                var eventType = $"workflow.step.{action}";
                var timestamp = DateTime.UtcNow;

                // Construct idempotency key to prevent duplicate event processing
                var idempotencyKey =
                    $"{stepContext.WorkflowId}-{stepContext.StepName}-{action}-{timestamp:yyyyMMddHHmmssfff}";

                var eventPayload = new Dictionary<string, object>
                {
                    ["eventType"] = eventType,
                    ["workflowId"] = stepContext.WorkflowId.ToString(),
                    ["stepName"] = stepContext.StepName ?? string.Empty,
                    ["status"] = workflow.Status.ToString(),
                    ["timestamp"] = timestamp.ToString("O"),
                    ["correlationId"] = correlationId,
                    ["typeName"] = stepContext.Type?.Name ?? string.Empty
                };

                var publishRequest = new PublishRequest
                {
                    TopicArn = _settings.SnsTopicArn,
                    Message = JsonSerializer.Serialize(eventPayload),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["idempotencyKey"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = idempotencyKey
                        }
                    }
                };

                await _sns.PublishAsync(publishRequest);

                _logger.LogInformation(
                    "Published SNS event {EventType} for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                    eventType, stepContext.WorkflowId, stepContext.StepName, correlationId);
            }
            catch (Exception ex)
            {
                // SNS publish failure should not fail the step execution
                _logger.LogError(ex,
                    "Failed to publish SNS event workflow.step.{Action} for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                    action, stepContext.WorkflowId, stepContext.StepName, correlationId);
            }
        }

        /// <summary>
        /// Reports successful step completion to Step Functions via SendTaskSuccessAsync.
        /// Enables the state machine to proceed to the next step.
        /// This is a new capability not present in the monolith — replaces in-process
        /// JobPool completion tracking.
        /// </summary>
        private async Task ReportStepFunctionsSuccessAsync(
            string taskToken, StepContext stepContext, string correlationId)
        {
            try
            {
                await _stepFunctions.SendTaskSuccessAsync(new SendTaskSuccessRequest
                {
                    TaskToken = taskToken,
                    Output = JsonSerializer.Serialize(stepContext)
                });

                _logger.LogInformation(
                    "Reported task success to Step Functions for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                    stepContext.WorkflowId, stepContext.StepName, correlationId);
            }
            catch (Exception sfEx)
            {
                _logger.LogWarning(sfEx,
                    "Failed to send task success to Step Functions for workflow {WorkflowId}, step {StepName}, CorrelationId: {CorrelationId}",
                    stepContext.WorkflowId, stepContext.StepName, correlationId);
            }
        }

        /// <summary>
        /// Reports step failure to Step Functions via SendTaskFailureAsync.
        /// Enables the state machine to handle failure (retry, catch, or fail).
        /// </summary>
        private async Task ReportStepFunctionsFailureAsync(
            string taskToken, Exception ex, StepContext? stepContext, string correlationId)
        {
            try
            {
                await _stepFunctions.SendTaskFailureAsync(new SendTaskFailureRequest
                {
                    TaskToken = taskToken,
                    Error = ex.GetType().Name,
                    Cause = ex.Message
                });

                _logger.LogInformation(
                    "Reported task failure to Step Functions for workflow {WorkflowId}, CorrelationId: {CorrelationId}",
                    stepContext?.WorkflowId, correlationId);
            }
            catch (Exception sfEx)
            {
                _logger.LogWarning(sfEx,
                    "Failed to send task failure to Step Functions for workflow {WorkflowId}, CorrelationId: {CorrelationId}",
                    stepContext?.WorkflowId, correlationId);
            }
        }

        /// <summary>
        /// Extracts correlation ID from SQS message attributes for structured logging.
        /// Falls back to generating a new GUID if the attribute is not present.
        /// Correlation-ID propagation is required per AAP Section 0.8.5.
        /// </summary>
        private static string ExtractCorrelationId(SQSEvent.SQSMessage message)
        {
            if (message.MessageAttributes != null
                && message.MessageAttributes.TryGetValue(
                    "correlationId", out var attr)
                && attr?.StringValue != null)
            {
                return attr.StringValue;
            }

            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts Step Functions task token from SQS message attributes.
        /// Returns null if no task token is present (i.e., not a Step Functions activity task).
        /// The task token enables the handler to report success/failure back to the
        /// state machine so it can proceed to the next step.
        /// </summary>
        private static string? ExtractTaskToken(SQSEvent.SQSMessage message)
        {
            if (message.MessageAttributes != null
                && message.MessageAttributes.TryGetValue(
                    "taskToken", out var attr)
                && attr?.StringValue != null)
            {
                return attr.StringValue;
            }

            return null;
        }

        /// <summary>
        /// Determines whether a workflow status is terminal (no further processing allowed).
        /// Used for idempotency checks to prevent duplicate step execution.
        /// </summary>
        private static bool IsTerminalStatus(WorkflowStatus status)
        {
            return status == WorkflowStatus.Finished
                || status == WorkflowStatus.Failed
                || status == WorkflowStatus.Aborted
                || status == WorkflowStatus.Canceled;
        }
    }
}
