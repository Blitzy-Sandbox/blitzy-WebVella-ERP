using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;
using Xunit;

namespace WebVellaErp.Workflow.Tests.Integration
{
    /// <summary>
    /// Integration tests that verify the Workflow Engine service correctly publishes
    /// domain events to SNS topics when workflow lifecycle events occur.
    /// All tests run against LocalStack — NO mocked AWS SDK calls for SNS/SQS/DynamoDB
    /// (per AAP Section 0.8.1 and 0.8.4).
    ///
    /// Pattern: docker compose up -d → test → docker compose down
    /// Event naming: {domain}.{entity}.{action} → workflow.workflow.created, etc.
    /// AWS endpoint: http://localhost:4566 (AAP Section 0.8.6)
    ///
    /// Source context:
    /// - Job lifecycle (Pending→Running→Finished/Failed/Aborted/Canceled) from
    ///   <c>JobStatus</c> enum in Job.cs maps to workflow lifecycle events.
    /// - <c>JobPool.Process()</c> success path → <c>workflow.workflow.started</c>
    /// - <c>JobPool.Process()</c> error path → <c>workflow.workflow.failed</c>
    /// - <c>CreateWorkflowAsync</c> → <c>workflow.workflow.created</c>
    /// - <c>UpdateWorkflowAsync</c> with status Finished → <c>workflow.workflow.updated</c>
    ///   (the effective "completed" event in this architecture)
    /// </summary>
    public class SnsEventPublishingTests : IAsyncLifetime
    {
        // ════════════════════════════════════════════════════════════════
        // ── Constants
        // ════════════════════════════════════════════════════════════════

        private const string LocalStackEndpoint = "http://localhost:4566";
        private const string AwsRegion = "us-east-1";

        // ════════════════════════════════════════════════════════════════
        // ── AWS Client Fields (real LocalStack-backed, no mocks)
        // ════════════════════════════════════════════════════════════════

        private IAmazonSimpleNotificationService _snsClient = null!;
        private IAmazonSQS _sqsClient = null!;
        private IAmazonDynamoDB _dynamoDbClient = null!;

        // ════════════════════════════════════════════════════════════════
        // ── AWS Resource Tracking Fields
        // ════════════════════════════════════════════════════════════════

        private string _topicArn = string.Empty;
        private string _queueUrl = string.Empty;
        private string _queueArn = string.Empty;
        private string _subscriptionArn = string.Empty;
        private string _tableName = string.Empty;

        // ════════════════════════════════════════════════════════════════
        // ── Service Under Test + Dependencies
        // ════════════════════════════════════════════════════════════════

        private IWorkflowService _workflowService = null!;
        private WorkflowSettings _settings = null!;
        private Mock<IAmazonStepFunctions> _stepFunctionsMock = null!;
        private WorkflowType _testWorkflowType = null!;

        // ════════════════════════════════════════════════════════════════
        // ── Unique Suffix (xUnit creates new instance per test)
        // ════════════════════════════════════════════════════════════════

        private readonly string _uniqueSuffix = Guid.NewGuid().ToString("N")[..8];

        // ════════════════════════════════════════════════════════════════
        // ── IAsyncLifetime: InitializeAsync
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sets up all AWS resources in LocalStack before each test:
        /// 1. Creates SNS topic for workflow events
        /// 2. Creates SQS queue for capturing published events
        /// 3. Subscribes SQS queue to SNS topic
        /// 4. Creates DynamoDB table with PK/SK key schema and entity_type attribute
        /// 5. Configures and instantiates WorkflowService with real LocalStack clients
        /// </summary>
        public async Task InitializeAsync()
        {
            // ── Configure AWS SDK clients targeting LocalStack ──
            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = AwsRegion
            };
            _snsClient = new AmazonSimpleNotificationServiceClient("test", "test", snsConfig);

            var sqsConfig = new AmazonSQSConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = AwsRegion
            };
            _sqsClient = new AmazonSQSClient("test", "test", sqsConfig);

            var dynamoDbConfig = new AmazonDynamoDBConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = AwsRegion
            };
            _dynamoDbClient = new AmazonDynamoDBClient("test", "test", dynamoDbConfig);

            // ── Create SNS Topic ──
            var topicName = $"workflow-events-{_uniqueSuffix}";
            var createTopicResponse = await _snsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = topicName
            }).ConfigureAwait(false);
            _topicArn = createTopicResponse.TopicArn;

            // ── Create SQS Queue for message capture ──
            var queueName = $"workflow-events-test-subscriber-{_uniqueSuffix}";
            var createQueueResponse = await _sqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName
            }).ConfigureAwait(false);
            _queueUrl = createQueueResponse.QueueUrl;

            // Retrieve the queue ARN for SNS subscription
            var queueAttrsResponse = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = _queueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            }).ConfigureAwait(false);
            _queueArn = queueAttrsResponse.QueueARN;

            // ── Set SQS Queue Policy to allow SNS to deliver messages ──
            var sqsPolicy = JsonSerializer.Serialize(new
            {
                Version = "2012-10-17",
                Statement = new[]
                {
                    new
                    {
                        Sid = "AllowSNSPublish",
                        Effect = "Allow",
                        Principal = "*",
                        Action = "sqs:SendMessage",
                        Resource = _queueArn,
                        Condition = new
                        {
                            ArnEquals = new Dictionary<string, string>
                            {
                                ["aws:SourceArn"] = _topicArn
                            }
                        }
                    }
                }
            });

            await _sqsClient.SetQueueAttributesAsync(_queueUrl, new Dictionary<string, string>
            {
                ["Policy"] = sqsPolicy
            }).ConfigureAwait(false);

            // ── Subscribe SQS to SNS with RawMessageDelivery disabled ──
            // (we want the full SNS envelope so we can inspect MessageAttributes)
            var subscribeResponse = await _snsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = _topicArn,
                Protocol = "sqs",
                Endpoint = _queueArn,
                Attributes = new Dictionary<string, string>
                {
                    ["RawMessageDelivery"] = "false"
                }
            }).ConfigureAwait(false);
            _subscriptionArn = subscribeResponse.SubscriptionArn;

            // ── Create DynamoDB Table ──
            _tableName = $"workflow-table-{_uniqueSuffix}";
            var createTableRequest = new CreateTableRequest
            {
                TableName = _tableName,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = "PK", KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = "SK", KeyType = KeyType.RANGE }
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = "PK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "SK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "entity_type", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "status", AttributeType = ScalarAttributeType.N }
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new GlobalSecondaryIndex
                    {
                        IndexName = "StatusIndex",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement { AttributeName = "entity_type", KeyType = KeyType.HASH },
                            new KeySchemaElement { AttributeName = "status", KeyType = KeyType.RANGE }
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL }
                    }
                },
                BillingMode = BillingMode.PAY_PER_REQUEST
            };

            await _dynamoDbClient.CreateTableAsync(createTableRequest).ConfigureAwait(false);

            // Wait for table to become ACTIVE
            await WaitForTableActiveAsync(_tableName).ConfigureAwait(false);

            // ── Configure WorkflowSettings ──
            _settings = new WorkflowSettings
            {
                DynamoDbTableName = _tableName,
                SnsTopicArn = _topicArn,
                AwsEndpointUrl = LocalStackEndpoint,
                AwsRegion = AwsRegion,
                Enabled = true,
                StepFunctionsStateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:test-workflow"
            };

            // ── Mock IAmazonStepFunctions (not exercised in SNS tests) ──
            _stepFunctionsMock = new Mock<IAmazonStepFunctions>();
            _stepFunctionsMock
                .Setup(sf => sf.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartExecutionResponse
                {
                    ExecutionArn = $"arn:aws:states:us-east-1:000000000000:execution:test-workflow:{Guid.NewGuid()}",
                    StartDate = DateTime.UtcNow
                });

            // ── Instantiate WorkflowService with real LocalStack + mocked StepFunctions ──
            var logger = NullLogger<WorkflowService>.Instance;
            _workflowService = new WorkflowService(
                _dynamoDbClient,
                _stepFunctionsMock.Object,
                _snsClient,
                logger,
                _settings
            );

            // ── Register a test WorkflowType (required before creating workflows) ──
            _testWorkflowType = new WorkflowType
            {
                Id = Guid.NewGuid(),
                Name = $"TestWorkflowType-{_uniqueSuffix}",
                DefaultPriority = WorkflowPriority.Low,
                CompleteClassName = "WebVellaErp.Workflow.Tests.Integration.TestWorkflow",
                AllowSingleInstance = false
            };

            await _workflowService.RegisterWorkflowTypeAsync(_testWorkflowType).ConfigureAwait(false);
        }

        // ════════════════════════════════════════════════════════════════
        // ── IAsyncLifetime: DisposeAsync
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cleans up all AWS resources created in LocalStack after each test:
        /// 1. Unsubscribes SNS→SQS subscription
        /// 2. Deletes SQS queue
        /// 3. Deletes SNS topic
        /// 4. Deletes DynamoDB table
        /// 5. Disposes AWS SDK clients
        /// </summary>
        public async Task DisposeAsync()
        {
            try
            {
                // Unsubscribe SNS→SQS
                if (!string.IsNullOrEmpty(_subscriptionArn))
                {
                    await _snsClient.UnsubscribeAsync(new UnsubscribeRequest
                    {
                        SubscriptionArn = _subscriptionArn
                    }).ConfigureAwait(false);
                }

                // Delete SQS queue
                if (!string.IsNullOrEmpty(_queueUrl))
                {
                    await _sqsClient.DeleteQueueAsync(new DeleteQueueRequest
                    {
                        QueueUrl = _queueUrl
                    }).ConfigureAwait(false);
                }

                // Delete SNS topic
                if (!string.IsNullOrEmpty(_topicArn))
                {
                    await _snsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = _topicArn
                    }).ConfigureAwait(false);
                }

                // Delete DynamoDB table
                if (!string.IsNullOrEmpty(_tableName))
                {
                    await _dynamoDbClient.DeleteTableAsync(new DeleteTableRequest
                    {
                        TableName = _tableName
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup; resource may already have been deleted
            }
            finally
            {
                // Dispose AWS SDK clients
                (_snsClient as IDisposable)?.Dispose();
                (_sqsClient as IDisposable)?.Dispose();
                (_dynamoDbClient as IDisposable)?.Dispose();
            }
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 2: CreateWorkflow_PublishesCreatedEvent
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that creating a workflow via <c>CreateWorkflowAsync</c> publishes a
        /// <c>workflow.workflow.created</c> SNS domain event with status <c>Pending</c>.
        ///
        /// Source mapping: <c>CreateWorkflowAsync</c> line 332 calls
        /// <c>PublishWorkflowEventAsync("created", workflow)</c> after DynamoDB PutItem.
        /// </summary>
        [Fact]
        public async Task CreateWorkflow_PublishesCreatedEvent()
        {
            // Arrange — purge any residual messages
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);

            // Act — create a workflow (status=Pending, publishes "created" event)
            var workflowId = Guid.NewGuid();
            var createdWorkflow = await _workflowService.CreateWorkflowAsync(
                _testWorkflowType.Id,
                attributes: null,
                priority: WorkflowPriority.Low,
                creatorId: null,
                schedulePlanId: null,
                workflowId: workflowId
            ).ConfigureAwait(false);
            createdWorkflow.Should().NotBeNull("CreateWorkflowAsync must return the created workflow");

            // Assert — poll SQS for the created event
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().Be(1, "exactly one SNS event should be published for workflow creation");

            var eventPayload = DeserializeSnsMessageFromSqs(messages[0]);
            eventPayload.Should().NotBeNull();

            var eventType = eventPayload!.Value.GetProperty("eventType").GetString();
            var eventWorkflowId = eventPayload.Value.GetProperty("workflowId").GetString();
            var eventStatus = eventPayload.Value.GetProperty("status").GetString();
            var eventTypeName = eventPayload.Value.GetProperty("typeName").GetString();
            var eventTimestamp = eventPayload.Value.GetProperty("timestamp").GetString();
            var eventCorrelationId = eventPayload.Value.GetProperty("correlationId").GetString();

            eventType.Should().Be("workflow.workflow.created",
                "event naming convention is {domain}.{entity}.{action}");
            eventWorkflowId.Should().Be(workflowId.ToString(),
                "workflowId in event must match the created workflow ID");
            eventStatus.Should().Be("Pending",
                "initial workflow status is Pending (source JobStatus.Pending = 1)");
            eventTypeName.Should().Be(_testWorkflowType.Name,
                "typeName must match the registered workflow type name");
            eventTimestamp.Should().NotBeNullOrEmpty("timestamp must be a valid ISO 8601 UTC datetime");
            eventCorrelationId.Should().NotBeNullOrEmpty("correlationId is required per AAP Section 0.8.5");

            // Verify correlationId equals workflow ID (WorkflowService uses workflow.Id as correlationId)
            eventCorrelationId.Should().Be(workflowId.ToString(),
                "correlationId must equal the workflow ID");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 3: StartWorkflow_PublishesStartedEvent
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that processing a pending workflow (Pending→Running) publishes a
        /// <c>workflow.workflow.started</c> SNS domain event with status <c>Running</c>.
        ///
        /// Source mapping: <c>ProcessWorkflowsAsync</c> success path (line 1686) calls
        /// <c>PublishWorkflowEventAsync("started", workflow)</c> after Step Functions
        /// execution starts successfully.
        ///
        /// Note: ProcessWorkflowsAsync also calls UpdateWorkflowAsync which publishes
        /// an "updated" event. We filter for the "started" event specifically.
        /// </summary>
        [Fact]
        public async Task StartWorkflow_PublishesStartedEvent()
        {
            // Arrange — create a workflow, then purge created-event messages
            var testWorkflow = await CreateTestWorkflow().ConfigureAwait(false);
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);

            // Step Functions mock already configured to succeed in InitializeAsync

            // Act — process pending workflows (triggers Pending→Running transition)
            await _workflowService.ProcessWorkflowsAsync(CancellationToken.None).ConfigureAwait(false);

            // Assert — poll SQS for events (expect "updated" + "started" events)
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().BeGreaterThanOrEqualTo(1,
                "at least the 'started' event should be published");

            // Find the "started" event among potentially multiple events
            JsonElement? startedEvent = null;
            foreach (var msg in messages)
            {
                var payload = DeserializeSnsMessageFromSqs(msg);
                if (payload.HasValue)
                {
                    var et = payload.Value.GetProperty("eventType").GetString();
                    if (et == "workflow.workflow.started")
                    {
                        startedEvent = payload;
                        break;
                    }
                }
            }

            startedEvent.Should().NotBeNull("a 'workflow.workflow.started' event must be published");

            var eventType = startedEvent!.Value.GetProperty("eventType").GetString();
            var eventStatus = startedEvent.Value.GetProperty("status").GetString();
            var eventWorkflowId = startedEvent.Value.GetProperty("workflowId").GetString();
            var eventCorrelationId = startedEvent.Value.GetProperty("correlationId").GetString();

            eventType.Should().Be("workflow.workflow.started",
                "event must be 'workflow.workflow.started'");
            eventStatus.Should().Be("Running",
                "status must be 'Running' after successful start (source JobStatus.Running = 2)");
            eventWorkflowId.Should().Be(testWorkflow.Id.ToString(),
                "workflowId must match the processed workflow");
            eventCorrelationId.Should().NotBeNullOrEmpty(
                "correlationId is required per AAP Section 0.8.5");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 4: CompleteWorkflow_PublishesCompletedEvent
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that completing a workflow (Running→Finished) publishes an
        /// event with status <c>Finished</c>.
        ///
        /// Source mapping: <c>JobPool.Process</c> success path sets
        /// <c>job.Status = JobStatus.Finished</c> (source Job.cs enum value 5).
        /// In the target architecture, <c>UpdateWorkflowAsync</c> with status Finished
        /// publishes a <c>workflow.workflow.updated</c> event carrying status <c>Finished</c>.
        /// This is the effective "completed" event in this event-driven architecture.
        /// </summary>
        [Fact]
        public async Task CompleteWorkflow_PublishesCompletedEvent()
        {
            // Arrange — create a workflow and transition it to Running first
            var testWorkflow = await CreateTestWorkflow().ConfigureAwait(false);

            // Transition to Running via UpdateWorkflowAsync
            testWorkflow.Status = WorkflowStatus.Running;
            testWorkflow.StartedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(testWorkflow).ConfigureAwait(false);

            // Purge all previous messages to isolate the "completed" event
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);

            // Act — transition to Finished (simulating successful completion)
            testWorkflow.Status = WorkflowStatus.Finished;
            testWorkflow.FinishedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(testWorkflow).ConfigureAwait(false);

            // Assert — poll SQS for the updated event with Finished status
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().BeGreaterThanOrEqualTo(1,
                "at least one event should be published for workflow completion");

            // Find the event with status Finished
            JsonElement? completedEvent = null;
            foreach (var msg in messages)
            {
                var payload = DeserializeSnsMessageFromSqs(msg);
                if (payload.HasValue)
                {
                    var status = payload.Value.GetProperty("status").GetString();
                    if (status == "Finished")
                    {
                        completedEvent = payload;
                        break;
                    }
                }
            }

            completedEvent.Should().NotBeNull(
                "an event with status 'Finished' must be published when workflow completes");

            var eventType = completedEvent!.Value.GetProperty("eventType").GetString();
            var eventStatus = completedEvent.Value.GetProperty("status").GetString();
            var eventWorkflowId = completedEvent.Value.GetProperty("workflowId").GetString();
            var eventTypeName = completedEvent.Value.GetProperty("typeName").GetString();
            var eventCorrelationId = completedEvent.Value.GetProperty("correlationId").GetString();

            eventType.Should().StartWith("workflow.workflow.",
                "event must follow naming convention {domain}.{entity}.{action}");
            eventStatus.Should().Be("Finished",
                "status must be 'Finished' after successful completion (source JobStatus.Finished = 5)");
            eventWorkflowId.Should().Be(testWorkflow.Id.ToString(),
                "workflowId must match the completed workflow");
            eventTypeName.Should().NotBeNullOrEmpty("typeName must be present in event");
            eventCorrelationId.Should().NotBeNullOrEmpty(
                "correlationId is required per AAP Section 0.8.5");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 5: FailWorkflow_PublishesFailedEvent
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that a workflow failure (Running→Failed) publishes a
        /// <c>workflow.workflow.failed</c> SNS domain event with status <c>Failed</c>.
        ///
        /// Source mapping: <c>ProcessWorkflowsAsync</c> catch block (line 1700) calls
        /// <c>PublishWorkflowEventAsync("failed", workflow)</c> when Step Functions
        /// execution throws an exception. Mirrors <c>JobPool.Process</c> error path
        /// setting <c>job.Status = JobStatus.Failed</c> with <c>ErrorMessage</c>.
        /// </summary>
        [Fact]
        public async Task FailWorkflow_PublishesFailedEvent()
        {
            // Arrange — reconfigure StepFunctions mock to throw an exception
            _stepFunctionsMock.Reset();
            _stepFunctionsMock
                .Setup(sf => sf.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonStepFunctionsException("Simulated Step Functions failure"));

            // Create a fresh workflow (will be Pending)
            var testWorkflow = await CreateTestWorkflow().ConfigureAwait(false);

            // Purge creation event messages
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);

            // Act — process pending workflows (triggers failure path due to mock exception)
            await _workflowService.ProcessWorkflowsAsync(CancellationToken.None).ConfigureAwait(false);

            // Assert — poll SQS for events (expect "updated" + "failed" events)
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().BeGreaterThanOrEqualTo(1,
                "at least the 'failed' event should be published");

            // Find the "failed" event
            JsonElement? failedEvent = null;
            foreach (var msg in messages)
            {
                var payload = DeserializeSnsMessageFromSqs(msg);
                if (payload.HasValue)
                {
                    var et = payload.Value.GetProperty("eventType").GetString();
                    if (et == "workflow.workflow.failed")
                    {
                        failedEvent = payload;
                        break;
                    }
                }
            }

            failedEvent.Should().NotBeNull(
                "a 'workflow.workflow.failed' event must be published when workflow fails");

            var eventType = failedEvent!.Value.GetProperty("eventType").GetString();
            var eventStatus = failedEvent.Value.GetProperty("status").GetString();
            var eventWorkflowId = failedEvent.Value.GetProperty("workflowId").GetString();

            eventType.Should().Be("workflow.workflow.failed",
                "event must be 'workflow.workflow.failed'");
            eventStatus.Should().Be("Failed",
                "status must be 'Failed' after error (source JobStatus.Failed = 4)");
            eventWorkflowId.Should().Be(testWorkflow.Id.ToString(),
                "workflowId must match the failed workflow");

            // Verify the workflow in DynamoDB also has the error message set
            var storedWorkflow = await _workflowService.GetWorkflowAsync(testWorkflow.Id).ConfigureAwait(false);
            storedWorkflow.Should().NotBeNull("workflow must still exist in DynamoDB after failure");
            storedWorkflow!.ErrorMessage.Should().NotBeNullOrEmpty(
                "ErrorMessage must be set on failure (source: JobPool.Process error path)");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 6: EventMessage_HasCorrectStructure
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies the complete JSON structure of a published SNS domain event:
        /// - Top-level keys: eventType, workflowId, typeId (inferred), typeName, status,
        ///   timestamp, correlationId, idempotencyKey
        /// - eventType matches pattern <c>workflow.workflow.*</c>
        /// - workflowId is a valid GUID
        /// - timestamp is ISO 8601 format
        /// - correlationId is a valid GUID
        ///
        /// Source: <c>PublishWorkflowEventAsync</c> (WorkflowService.cs lines 1773-1782)
        /// defines the payload structure.
        /// </summary>
        [Fact]
        public async Task EventMessage_HasCorrectStructure()
        {
            // Arrange
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);

            // Act — create a workflow to trigger a "created" event
            var workflowId = Guid.NewGuid();
            await _workflowService.CreateWorkflowAsync(
                _testWorkflowType.Id,
                attributes: null,
                priority: WorkflowPriority.Low,
                creatorId: null,
                schedulePlanId: null,
                workflowId: workflowId
            ).ConfigureAwait(false);

            // Assert — capture the raw SNS message from SQS
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().BeGreaterThanOrEqualTo(1, "at least one event must be published");

            var eventPayload = DeserializeSnsMessageFromSqs(messages[0]);
            eventPayload.Should().NotBeNull("event payload must be deserializable");

            // Validate all required top-level keys exist
            var root = eventPayload!.Value;

            // eventType — must match pattern workflow.workflow.*
            var eventType = root.GetProperty("eventType").GetString();
            eventType.Should().NotBeNullOrEmpty();
            eventType!.Should().StartWith("workflow.workflow.",
                "eventType must follow {domain}.{entity}.{action} convention");

            // workflowId — must be a valid GUID
            var workflowIdStr = root.GetProperty("workflowId").GetString();
            workflowIdStr.Should().NotBeNullOrEmpty();
            Guid.TryParse(workflowIdStr, out var parsedWorkflowId).Should().BeTrue(
                "workflowId must be a valid GUID");
            parsedWorkflowId.Should().Be(workflowId, "workflowId must match the created workflow");

            // status — must be a non-empty string
            var status = root.GetProperty("status").GetString();
            status.Should().NotBeNullOrEmpty("status must be present");

            // typeName — must match registered type
            var typeName = root.GetProperty("typeName").GetString();
            typeName.Should().NotBeNullOrEmpty("typeName must be present");
            typeName.Should().Be(_testWorkflowType.Name);

            // timestamp — must be ISO 8601 format (parseable by DateTime)
            var timestamp = root.GetProperty("timestamp").GetString();
            timestamp.Should().NotBeNullOrEmpty("timestamp must be present");
            DateTime.TryParse(timestamp, out _).Should().BeTrue(
                "timestamp must be a valid ISO 8601 datetime string");

            // correlationId — must be a valid GUID
            var correlationId = root.GetProperty("correlationId").GetString();
            correlationId.Should().NotBeNullOrEmpty("correlationId is required per AAP Section 0.8.5");
            Guid.TryParse(correlationId, out _).Should().BeTrue(
                "correlationId must be a valid GUID");

            // idempotencyKey — must be a non-empty string
            var idempotencyKey = root.GetProperty("idempotencyKey").GetString();
            idempotencyKey.Should().NotBeNullOrEmpty(
                "idempotencyKey is required per AAP Section 0.8.5");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 7: EventPublishing_IncludesIdempotencyKey
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that SNS message attributes include an <c>IdempotencyKey</c> attribute
        /// as a non-empty string. Per AAP Section 0.8.5: "Idempotency keys on all write
        /// endpoints and event handlers."
        ///
        /// The idempotency key format is <c>{workflowId}-{action}-{yyyyMMddHHmmss}</c>
        /// as defined in <c>PublishWorkflowEventAsync</c> line 1770.
        /// </summary>
        [Fact]
        public async Task EventPublishing_IncludesIdempotencyKey()
        {
            // Arrange
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);

            // Act — create a workflow to trigger an event
            var workflowId = Guid.NewGuid();
            await _workflowService.CreateWorkflowAsync(
                _testWorkflowType.Id,
                attributes: null,
                priority: WorkflowPriority.Low,
                creatorId: null,
                schedulePlanId: null,
                workflowId: workflowId
            ).ConfigureAwait(false);

            // Assert — capture the raw SQS message (includes SNS envelope with MessageAttributes)
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().BeGreaterThanOrEqualTo(1);

            // Parse the full SNS notification envelope from SQS body
            var sqsBody = messages[0].Body;
            using var snsEnvelope = JsonDocument.Parse(sqsBody);
            var snsRoot = snsEnvelope.RootElement;

            // SNS envelope wraps message attributes in "MessageAttributes"
            snsRoot.TryGetProperty("MessageAttributes", out var messageAttributes).Should().BeTrue(
                "SNS envelope must contain MessageAttributes");

            // Check for idempotencyKey attribute
            messageAttributes.TryGetProperty("idempotencyKey", out var idempotencyKeyAttr).Should().BeTrue(
                "MessageAttributes must include 'idempotencyKey' per AAP Section 0.8.5");

            var idempotencyKeyValue = idempotencyKeyAttr.GetProperty("Value").GetString();
            idempotencyKeyValue.Should().NotBeNullOrEmpty(
                "idempotencyKey must be a non-empty string");

            // Verify format: {workflowId}-{action}-{yyyyMMddHHmmss}
            idempotencyKeyValue!.Should().StartWith(workflowId.ToString(),
                "idempotencyKey must start with the workflow ID");
            idempotencyKeyValue.Should().Contain("-created-",
                "idempotencyKey must contain the action name");

            // Also verify idempotencyKey in the event payload body itself
            var eventPayload = DeserializeSnsMessageFromSqs(messages[0]);
            eventPayload.Should().NotBeNull();
            var payloadIdempotencyKey = eventPayload!.Value.GetProperty("idempotencyKey").GetString();
            payloadIdempotencyKey.Should().NotBeNullOrEmpty();
            payloadIdempotencyKey.Should().Be(idempotencyKeyValue,
                "idempotencyKey in payload must match the one in MessageAttributes");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Phase 8: EventPublishing_PropagatesCorrelationId
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that the correlation ID from the workflow creation context is
        /// propagated to the published SNS event. Per AAP Section 0.8.5:
        /// "Correlation-ID propagation from all Lambda functions."
        ///
        /// In the current implementation, <c>PublishWorkflowEventAsync</c> uses
        /// <c>workflow.Id.ToString()</c> as the correlationId (line 1771).
        /// This test verifies that a known workflow ID appears as the correlationId
        /// in both the event payload and the SNS MessageAttributes.
        /// </summary>
        [Fact]
        public async Task EventPublishing_PropagatesCorrelationId()
        {
            // Arrange — use a known, pre-determined workflow ID
            await PurgeSqsQueue(_queueUrl).ConfigureAwait(false);
            var knownWorkflowId = Guid.NewGuid();

            // Act — create a workflow with the known ID
            await _workflowService.CreateWorkflowAsync(
                _testWorkflowType.Id,
                attributes: null,
                priority: WorkflowPriority.Low,
                creatorId: null,
                schedulePlanId: null,
                workflowId: knownWorkflowId
            ).ConfigureAwait(false);

            // Assert — capture the message and verify correlation ID propagation
            var messages = await PollSqsForMessages(_queueUrl, maxWaitSeconds: 15).ConfigureAwait(false);
            messages.Should().NotBeNull();
            messages.Count.Should().BeGreaterThanOrEqualTo(1);

            // 1. Verify correlationId in event payload body
            var eventPayload = DeserializeSnsMessageFromSqs(messages[0]);
            eventPayload.Should().NotBeNull();

            var payloadCorrelationId = eventPayload!.Value.GetProperty("correlationId").GetString();
            payloadCorrelationId.Should().NotBeNullOrEmpty("correlationId must be present in event payload");
            payloadCorrelationId.Should().Be(knownWorkflowId.ToString(),
                "correlationId in payload must equal the known workflow ID");

            // 2. Verify correlationId in SNS MessageAttributes
            var sqsBody = messages[0].Body;
            using var snsEnvelope = JsonDocument.Parse(sqsBody);
            var snsRoot = snsEnvelope.RootElement;

            snsRoot.TryGetProperty("MessageAttributes", out var messageAttributes).Should().BeTrue(
                "SNS envelope must contain MessageAttributes");

            messageAttributes.TryGetProperty("correlationId", out var correlationIdAttr).Should().BeTrue(
                "MessageAttributes must include 'correlationId' per AAP Section 0.8.5");

            var attrCorrelationId = correlationIdAttr.GetProperty("Value").GetString();
            attrCorrelationId.Should().NotBeNullOrEmpty();
            attrCorrelationId.Should().Be(knownWorkflowId.ToString(),
                "correlationId in MessageAttributes must match the known workflow ID");

            // 3. Verify payload correlationId matches MessageAttributes correlationId
            payloadCorrelationId.Should().Be(attrCorrelationId,
                "correlationId must be consistent between payload and MessageAttributes");
        }

        // ════════════════════════════════════════════════════════════════
        // ── Helper Methods
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Polls the SQS queue with long-polling until at least one message arrives
        /// or the timeout is reached. Returns all received messages.
        /// Uses SQS long-polling (WaitTimeSeconds) to reduce empty responses.
        /// </summary>
        /// <param name="queueUrl">The SQS queue URL to poll.</param>
        /// <param name="maxWaitSeconds">Maximum total wait time in seconds (default: 10).</param>
        /// <returns>List of received SQS messages; may be empty if timeout reached.</returns>
        public async Task<List<Message>> PollSqsForMessages(string queueUrl, int maxWaitSeconds = 10)
        {
            var allMessages = new List<Message>();
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

            while (DateTime.UtcNow < deadline)
            {
                var remainingSeconds = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalSeconds);
                var waitTime = Math.Min(remainingSeconds, 5); // SQS long-poll max is 20s, use 5s chunks

                var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = waitTime,
                    MessageAttributeNames = new List<string> { "All" }
                }).ConfigureAwait(false);

                if (receiveResponse.Messages != null && receiveResponse.Messages.Count > 0)
                {
                    allMessages.AddRange(receiveResponse.Messages);
                    // Give a brief pause then do one more quick poll to collect any remaining
                    await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);

                    var extraResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 1,
                        MessageAttributeNames = new List<string> { "All" }
                    }).ConfigureAwait(false);

                    if (extraResponse.Messages != null && extraResponse.Messages.Count > 0)
                    {
                        allMessages.AddRange(extraResponse.Messages);
                    }

                    break;
                }

                // Brief pause before retrying
                await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            }

            return allMessages;
        }

        /// <summary>
        /// Extracts and deserializes the inner SNS notification body from the SQS wrapper message.
        /// SQS delivers SNS notifications wrapped in an envelope with "Type", "MessageId",
        /// "TopicArn", "Message" (the actual payload), "MessageAttributes", etc.
        /// This method extracts the "Message" field and parses it as JSON.
        /// </summary>
        /// <param name="sqsMessage">The raw SQS message containing the SNS envelope.</param>
        /// <returns>Parsed JSON element of the event payload, or null if parsing fails.</returns>
        public JsonElement? DeserializeSnsMessageFromSqs(Message sqsMessage)
        {
            try
            {
                // SQS body contains the full SNS notification envelope as JSON
                using var snsEnvelope = JsonDocument.Parse(sqsMessage.Body);
                var snsRoot = snsEnvelope.RootElement;

                // The actual event payload is in the "Message" field (as a JSON string)
                if (!snsRoot.TryGetProperty("Message", out var messageElement))
                    return null;

                var messageStr = messageElement.GetString();
                if (string.IsNullOrEmpty(messageStr))
                    return null;

                // Parse the inner message JSON — need to use a new document that we own
                var innerDoc = JsonDocument.Parse(messageStr);
                return innerDoc.RootElement.Clone(); // Clone so it outlives the JsonDocument
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a standard test workflow for reuse across tests. Registers with the
        /// pre-configured test workflow type and returns the created workflow.
        /// </summary>
        /// <returns>The created workflow instance with status=Pending.</returns>
        public async Task<Models.Workflow> CreateTestWorkflow()
        {
            var workflowId = Guid.NewGuid();
            var workflow = await _workflowService.CreateWorkflowAsync(
                _testWorkflowType.Id,
                attributes: null,
                priority: WorkflowPriority.Low,
                creatorId: null,
                schedulePlanId: null,
                workflowId: workflowId
            ).ConfigureAwait(false);

            return workflow!;
        }

        /// <summary>
        /// Drains all remaining messages from the SQS queue to ensure test isolation.
        /// Uses short-polling with multiple iterations to ensure the queue is empty.
        /// </summary>
        /// <param name="queueUrl">The SQS queue URL to purge.</param>
        public async Task PurgeSqsQueue(string queueUrl)
        {
            try
            {
                // Use PurgeQueue API for immediate purge
                await _sqsClient.PurgeQueueAsync(new PurgeQueueRequest
                {
                    QueueUrl = queueUrl
                }).ConfigureAwait(false);

                // Brief delay to let the purge take effect
                await Task.Delay(TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false);
            }
            catch (Amazon.SQS.Model.PurgeQueueInProgressException)
            {
                // PurgeQueue can only be called once per 60 seconds — fall back to manual drain
                await DrainQueueManuallyAsync(queueUrl).ConfigureAwait(false);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // ── Private Helper Methods
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Waits for a DynamoDB table to reach ACTIVE status before proceeding.
        /// Polls DescribeTable every 500ms up to 30 seconds.
        /// </summary>
        private async Task WaitForTableActiveAsync(string tableName)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var describeResponse = await _dynamoDbClient.DescribeTableAsync(new DescribeTableRequest
                    {
                        TableName = tableName
                    }).ConfigureAwait(false);

                    if (describeResponse.Table.TableStatus == TableStatus.ACTIVE)
                        return;
                }
                catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
                {
                    // Table not yet created; keep waiting
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }

            throw new TimeoutException($"DynamoDB table '{tableName}' did not become ACTIVE within 30 seconds.");
        }

        /// <summary>
        /// Manually drains all messages from the SQS queue when PurgeQueue is rate-limited.
        /// Receives and deletes messages in batches until the queue is empty.
        /// </summary>
        private async Task DrainQueueManuallyAsync(string queueUrl)
        {
            for (int i = 0; i < 5; i++)
            {
                var response = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1
                }).ConfigureAwait(false);

                if (response.Messages == null || response.Messages.Count == 0)
                    break;

                foreach (var msg in response.Messages)
                {
                    await _sqsClient.DeleteMessageAsync(queueUrl, msg.ReceiptHandle).ConfigureAwait(false);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }
}
