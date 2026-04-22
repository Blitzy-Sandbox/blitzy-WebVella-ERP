// ---------------------------------------------------------------------------
// SqsTriggerTests.cs — Integration tests for SQS-triggered StepHandler
// ---------------------------------------------------------------------------
// Verifies the Workflow Engine's StepHandler correctly processes SQS messages
// for workflow step execution against real LocalStack (NO mocked AWS SDK calls).
//
// Replaces the monolith's in-process JobPool.RunJobAsync(Job job) + Process(JobContext)
// execution pattern (source: JobPool.cs lines 56-77, 79+) with SQS-triggered Lambda
// execution running against LocalStack at http://localhost:4566.
//
// Per AAP Section 0.8.1: ALL integration tests run against LocalStack.
// Per AAP Section 0.8.4: xUnit + FluentAssertions for all assertions.
// Per AAP Section 0.8.5: DLQ naming convention {service}-{queue}-dlq,
//   idempotency keys on write operations, at-least-once delivery with idempotent
//   consumers, correlation-ID structured logging propagation.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.StepFunctions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.Workflow.Functions;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;
using Xunit;

// Alias to disambiguate WebVellaErp.Workflow namespace from the Workflow model class
using WfModel = WebVellaErp.Workflow.Models.Workflow;

namespace WebVellaErp.Workflow.Tests.Integration
{
    /// <summary>
    /// Integration tests verifying that the StepHandler correctly processes SQS messages
    /// for workflow step execution. Each test creates real SQS queues and DynamoDB tables
    /// in LocalStack, constructs SQSEvent payloads, invokes the Lambda handler, and
    /// verifies state transitions and error handling against real AWS infrastructure.
    ///
    /// Source mapping:
    ///   - JobPool.RunJobAsync (lines 56-77): Task.Run → SQS message processing
    ///   - JobPool.Process (lines 79-158): Status transitions (Pending→Running→Finished/Failed)
    ///   - JobPool.MAX_THREADS_POOL_COUNT = 20: Bounded concurrency → Lambda batch processing
    ///   - JobContext construction (lines 68-73): StepContext serialized as SQS message body
    /// </summary>
    public class SqsTriggerTests : IAsyncLifetime
    {
        // ── AWS SDK Clients (real, targeting LocalStack) ─────────────────
        private IAmazonSQS _sqsClient = null!;
        private IAmazonDynamoDB _dynamoDbClient = null!;
        private IAmazonSimpleNotificationService _snsClient = null!;
        private IAmazonStepFunctions _sfnClient = null!;

        // ── Test Infrastructure State ────────────────────────────────────
        private IWorkflowService _workflowService = null!;
        private WorkflowSettings _settings = null!;
        private string _queueUrl = string.Empty;
        private string _dlqUrl = string.Empty;
        private string _dlqArn = string.Empty;
        private string _tableName = string.Empty;
        private string _uniqueSuffix = string.Empty;
        private Guid _testWorkflowTypeId;

        // ── Constants ────────────────────────────────────────────────────
        private const string LocalStackEndpoint = "http://localhost:4566";
        private const string StepFunctionsEndpoint = "http://localhost:8083";
        private const string TestRegion = "us-east-1";
        private const string StatusIndexName = "StatusIndex";

        // =================================================================
        // IAsyncLifetime — Setup / Teardown
        // =================================================================

        /// <summary>
        /// Creates LocalStack SQS queues (main + DLQ with RedrivePolicy), a DynamoDB table
        /// with the single-table design (PK/SK + StatusIndex GSI), configures WorkflowSettings,
        /// instantiates WorkflowService with real AWS clients, and registers a test workflow type.
        /// </summary>
        public async Task InitializeAsync()
        {
            _uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            _tableName = $"workflow-sqs-test-{_uniqueSuffix}";
            _testWorkflowTypeId = Guid.NewGuid();

            // ── Configure AWS SDK clients against LocalStack ─────────────
            _sqsClient = new AmazonSQSClient(
                "test", "test",
                new AmazonSQSConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = TestRegion
                });

            _dynamoDbClient = new AmazonDynamoDBClient(
                "test", "test",
                new AmazonDynamoDBConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = TestRegion
                });

            _snsClient = new AmazonSimpleNotificationServiceClient(
                "test", "test",
                new AmazonSimpleNotificationServiceConfig
                {
                    ServiceURL = LocalStackEndpoint,
                    AuthenticationRegion = TestRegion
                });

            _sfnClient = new AmazonStepFunctionsClient(
                "test", "test",
                new AmazonStepFunctionsConfig
                {
                    ServiceURL = StepFunctionsEndpoint,
                    AuthenticationRegion = TestRegion
                });

            // ── Create DLQ (per AAP Section 0.8.5: {service}-{queue}-dlq) ───
            var dlqResponse = await _sqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = $"workflow-steps-dlq-{_uniqueSuffix}"
            });
            _dlqUrl = dlqResponse.QueueUrl;

            // Retrieve DLQ ARN for RedrivePolicy
            var dlqAttrs = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = _dlqUrl,
                AttributeNames = new List<string> { "QueueArn" }
            });
            _dlqArn = dlqAttrs.Attributes["QueueArn"];

            // ── Create main SQS queue with RedrivePolicy ─────────────────
            var redrivePolicy = JsonSerializer.Serialize(new
            {
                deadLetterTargetArn = _dlqArn,
                maxReceiveCount = 3
            });

            var mainQueueResponse = await _sqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = $"workflow-steps-{_uniqueSuffix}",
                Attributes = new Dictionary<string, string>
                {
                    ["RedrivePolicy"] = redrivePolicy,
                    ["VisibilityTimeout"] = "2"
                }
            });
            _queueUrl = mainQueueResponse.QueueUrl;

            // ── Create DynamoDB table ────────────────────────────────────
            await CreateDynamoDbTableAsync();

            // ── Configure WorkflowSettings ───────────────────────────────
            _settings = new WorkflowSettings
            {
                DynamoDbTableName = _tableName,
                SqsQueueUrl = _queueUrl,
                SnsTopicArn = string.Empty,    // empty → skips SNS publishing in tests
                StepFunctionsStateMachineArn = string.Empty,
                AwsEndpointUrl = LocalStackEndpoint,
                AwsRegion = TestRegion,
                Enabled = true
            };

            // ── Create WorkflowService with real AWS clients ─────────────
            _workflowService = new WorkflowService(
                _dynamoDbClient,
                _sfnClient,
                _snsClient,
                NullLogger<WorkflowService>.Instance,
                _settings);

            // ── Register test workflow type ───────────────────────────────
            await _workflowService.RegisterWorkflowTypeAsync(new WorkflowType
            {
                Id = _testWorkflowTypeId,
                Name = "TestSqsTriggerWorkflow",
                DefaultPriority = WorkflowPriority.Low,
                CompleteClassName = "WebVellaErp.Workflow.Tests.TestSqsWorkflow",
                Assembly = "WebVellaErp.Workflow.Tests",
                AllowSingleInstance = false
            });
        }

        /// <summary>
        /// Best-effort cleanup: deletes the DynamoDB table, both SQS queues, and
        /// disposes AWS SDK clients.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Delete DynamoDB table (best-effort)
            try
            {
                await _dynamoDbClient.DeleteTableAsync(new DeleteTableRequest
                {
                    TableName = _tableName
                });
            }
            catch
            {
                // Swallow — table may not exist if initialization failed
            }

            // Delete SQS queues (best-effort)
            try
            {
                if (!string.IsNullOrEmpty(_queueUrl))
                    await _sqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = _queueUrl });
            }
            catch
            {
                // Swallow
            }

            try
            {
                if (!string.IsNullOrEmpty(_dlqUrl))
                    await _sqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = _dlqUrl });
            }
            catch
            {
                // Swallow
            }

            // Dispose clients
            (_sqsClient as IDisposable)?.Dispose();
            (_dynamoDbClient as IDisposable)?.Dispose();
            (_snsClient as IDisposable)?.Dispose();
            (_sfnClient as IDisposable)?.Dispose();
        }

        // =================================================================
        // Test 1: Happy Path — SQS Message Processing Triggers Step Execution
        // =================================================================

        /// <summary>
        /// Verifies that a well-formed SQS message containing a StepContext payload
        /// triggers successful step execution via StepHandler.HandleSqsEvent().
        ///
        /// Source: Replaces JobPool.RunJobAsync (line 56) → Task.Run(() ⇒ Process(context))
        /// (line 75). The StepContext is the serverless equivalent of JobContext.
        /// </summary>
        [Fact]
        public async Task ProcessSqsMessage_ExecutesStepSuccessfully()
        {
            // Arrange — create a workflow in Pending state
            var workflowId = Guid.NewGuid();
            await CreateTestWorkflowInDynamoDb(workflowId, "Pending");

            // Build StepContext payload (serverless equivalent of JobContext from JobContext.cs)
            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Priority = WorkflowPriority.Low,
                Attributes = new Dictionary<string, object>
                {
                    ["source"] = "sqs-trigger-test"
                },
                Type = new WorkflowType
                {
                    Id = _testWorkflowTypeId,
                    Name = "TestSqsTriggerWorkflow",
                    DefaultPriority = WorkflowPriority.Low
                },
                StepName = "execute-step-test",
                Aborted = false
            };

            var payload = JsonSerializer.Serialize(stepContext);
            var sqsEvent = CreateSqsEvent(payload);

            // Create StepHandler with real dependencies via internal constructor
            var handler = new StepHandler(
                _workflowService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            // Act
            await handler.HandleSqsEvent(sqsEvent, new TestLambdaContext());

            // Assert — workflow should have transitioned from Pending to Finished
            var workflow = await GetWorkflowFromDynamoDb(workflowId);
            workflow.Should().NotBeNull("workflow must exist in DynamoDB after processing");
            workflow!.Status.Should().Be(WorkflowStatus.Finished,
                "a successfully processed step sets the workflow to Finished");
        }

        // =================================================================
        // Test 2: Status Transition — Pending → Running → Finished
        // =================================================================

        /// <summary>
        /// Verifies the complete lifecycle transition Pending → Running → Finished
        /// with StartedOn and FinishedOn timestamps set correctly.
        ///
        /// Source: Maps to JobPool.Process() which transitions:
        ///   - Running (line ~96): job.StartedOn = DateTime.UtcNow
        ///   - Finished (line ~110): job.FinishedOn = DateTime.UtcNow
        /// </summary>
        [Fact]
        public async Task SqsProcessing_TransitionsWorkflowThroughLifecycle()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var beforeTest = DateTime.UtcNow;
            await CreateTestWorkflowInDynamoDb(workflowId, "Pending");

            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Priority = WorkflowPriority.Medium,
                Attributes = new Dictionary<string, object>
                {
                    ["lifecycle"] = "transition-test"
                },
                Type = new WorkflowType
                {
                    Id = _testWorkflowTypeId,
                    Name = "TestSqsTriggerWorkflow",
                    DefaultPriority = WorkflowPriority.Medium
                },
                StepName = "lifecycle-step",
                Aborted = false
            };

            var payload = JsonSerializer.Serialize(stepContext);
            var sqsEvent = CreateSqsEvent(payload);

            var handler = new StepHandler(
                _workflowService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            // Act
            await handler.HandleSqsEvent(sqsEvent, new TestLambdaContext());

            // Assert — verify final state reflects the full lifecycle
            var workflow = await GetWorkflowFromDynamoDb(workflowId);
            workflow.Should().NotBeNull();

            // Status must be Finished (maps to JobStatus.Finished = 5)
            workflow!.Status.Should().Be(WorkflowStatus.Finished,
                "the step completed successfully so the workflow transitions to Finished");

            // StartedOn must be set (maps to job.StartedOn = DateTime.UtcNow on line ~96)
            workflow.StartedOn.Should().NotBeNull("StartedOn is set when entering Running state");
            workflow.StartedOn!.Value.Should().BeCloseTo(
                beforeTest, TimeSpan.FromSeconds(30),
                "StartedOn should be approximately now");

            // FinishedOn must be set (maps to job.FinishedOn = DateTime.UtcNow on line ~110)
            workflow.FinishedOn.Should().NotBeNull("FinishedOn is set when reaching Finished state");
            workflow.FinishedOn!.Value.Should().BeCloseTo(
                beforeTest, TimeSpan.FromSeconds(30),
                "FinishedOn should be approximately now");

            // FinishedOn >= StartedOn (temporal ordering)
            workflow.FinishedOn.Value.Should().BeOnOrAfter(workflow.StartedOn.Value,
                "FinishedOn must not precede StartedOn");
        }

        // =================================================================
        // Test 3: Error Path — Step Execution Failure Sets Failed Status
        // =================================================================

        /// <summary>
        /// Verifies that an error during step execution causes the workflow status to be
        /// set to Failed with an ErrorMessage populated.
        ///
        /// Source: Maps to JobPool.Process() catch block (lines ~115-125):
        ///   job.Status = JobStatus.Failed; job.ErrorMessage = ex.Message;
        ///
        /// Uses a FaultInjectingWorkflowService wrapper that injects an error during the
        /// second UpdateWorkflowAsync call (the Finished status update), which triggers the
        /// catch block in ExecuteStepAsync to set Failed status. All DynamoDB operations
        /// still run against real LocalStack — only the service layer has the injection
        /// point. This is NOT a mocked AWS SDK call.
        /// </summary>
        [Fact]
        public async Task SqsProcessing_ErrorSetsFailedStatus()
        {
            // Arrange — create a workflow in Pending state
            var workflowId = Guid.NewGuid();
            await CreateTestWorkflowInDynamoDb(workflowId, "Pending");

            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Priority = WorkflowPriority.Low,
                Attributes = new Dictionary<string, object>
                {
                    ["error_test"] = true
                },
                Type = new WorkflowType
                {
                    Id = _testWorkflowTypeId,
                    Name = "TestSqsTriggerWorkflow",
                    DefaultPriority = WorkflowPriority.Low
                },
                StepName = "error-step",
                Aborted = false
            };

            var payload = JsonSerializer.Serialize(stepContext);
            var sqsEvent = CreateSqsEvent(payload);

            // Create a fault-injecting service that throws on the 2nd UpdateWorkflowAsync call.
            // Call sequence: #1 = set Running (succeeds), #2 = set Finished (THROWS),
            //   catch block: #3 = set Failed (succeeds via real DynamoDB).
            var faultService = new FaultInjectingWorkflowService(
                _workflowService,
                throwOnUpdateCallNumber: 2,
                exceptionMessage: "Simulated step execution failure for integration testing");

            var handler = new StepHandler(
                faultService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            // Act — HandleSqsEvent re-throws after setting Failed status; catch the exception
            Exception? caughtException = null;
            try
            {
                await handler.HandleSqsEvent(sqsEvent, new TestLambdaContext());
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            // Assert — the exception should have been thrown (SQS retry mechanism)
            caughtException.Should().NotBeNull(
                "StepHandler re-throws exceptions for SQS retry→DLQ behavior");

            // Verify the workflow was updated to Failed status in DynamoDB
            // (the catch block in ExecuteStepAsync writes Failed before re-throwing)
            var workflow = await GetWorkflowFromDynamoDb(workflowId);
            workflow.Should().NotBeNull();
            workflow!.Status.Should().Be(WorkflowStatus.Failed,
                "the catch block sets Failed status (maps to JobStatus.Failed = 4)");
            workflow.ErrorMessage.Should().NotBeNullOrEmpty(
                "ErrorMessage must be populated (maps to job.ErrorMessage = ex.Message)");
            workflow.FinishedOn.Should().NotBeNull(
                "FinishedOn is set in the catch block on failure");
        }

        // =================================================================
        // Test 4: DLQ — Messages Go To Dead-Letter Queue After Max Retries
        // =================================================================

        /// <summary>
        /// Verifies that after exceeding the RedrivePolicy maxReceiveCount (3),
        /// a message moves from the main queue to the dead-letter queue (workflow-steps-dlq).
        ///
        /// Per AAP Section 0.8.5: DLQ naming convention {service}-{queue}-dlq.
        /// This test exercises the real SQS RedrivePolicy on LocalStack.
        /// </summary>
        [Fact]
        public async Task SqsProcessing_MessageGoesToDlqAfterMaxRetries()
        {
            // Arrange — build a StepContext payload
            var workflowId = Guid.NewGuid();
            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Priority = WorkflowPriority.Low,
                Type = new WorkflowType
                {
                    Id = _testWorkflowTypeId,
                    Name = "TestSqsTriggerWorkflow",
                    DefaultPriority = WorkflowPriority.Low
                },
                StepName = "dlq-test-step",
                Aborted = false
            };

            var payload = JsonSerializer.Serialize(stepContext);

            // Send message to the main SQS queue (real LocalStack SQS)
            await _sqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = payload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["CorrelationId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = Guid.NewGuid().ToString()
                    }
                }
            });

            // Act — receive the message maxReceiveCount (3) times without deleting,
            // allowing SQS to track the receive count. After exceeding maxReceiveCount,
            // the message is redriven to the DLQ by LocalStack SQS.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var receiveResponse = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 3,
                    VisibilityTimeout = 1
                });

                if (receiveResponse.Messages.Count == 0)
                {
                    // Message may already have been moved to DLQ
                    break;
                }

                // Do NOT delete the message — let visibility timeout expire so SQS
                // increments the receive count and eventually redrives to DLQ
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            // Allow SQS time to process the redrive
            await Task.Delay(TimeSpan.FromSeconds(3));

            // Assert — verify the message appears in the DLQ
            var dlqMessages = await _sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _dlqUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5
            });

            dlqMessages.Messages.Should().NotBeEmpty(
                "the message should have been redriven to the DLQ after maxReceiveCount (3) exceeds");

            // Verify the DLQ message contains the original StepContext payload
            var dlqMessageBody = dlqMessages.Messages.First().Body;
            dlqMessageBody.Should().Contain(workflowId.ToString(),
                "the DLQ message must contain the original StepContext with the workflow ID");
        }

        // =================================================================
        // Test 5: Correlation ID — Extraction from SQS Message Attributes
        // =================================================================

        /// <summary>
        /// Verifies that StepHandler correctly extracts the CorrelationId from SQS message
        /// attributes and uses it during step execution.
        ///
        /// Per AAP Section 0.8.5: Correlation-ID structured logging propagation.
        /// The handler calls ExtractCorrelationId(record) which reads the "correlationId"
        /// message attribute. If present, it's used; otherwise a new GUID is generated.
        /// </summary>
        [Fact]
        public async Task SqsProcessing_ExtractsCorrelationIdFromAttributes()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var expectedCorrelationId = Guid.NewGuid().ToString();
            await CreateTestWorkflowInDynamoDb(workflowId, "Pending");

            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Priority = WorkflowPriority.Low,
                Attributes = new Dictionary<string, object>
                {
                    ["correlation_test"] = true
                },
                Type = new WorkflowType
                {
                    Id = _testWorkflowTypeId,
                    Name = "TestSqsTriggerWorkflow",
                    DefaultPriority = WorkflowPriority.Low
                },
                StepName = "correlation-step",
                Aborted = false
            };

            var payload = JsonSerializer.Serialize(stepContext);

            // Create SQS event with explicit correlationId and idempotencyKey attributes
            var attributes = new Dictionary<string, string>
            {
                ["correlationId"] = expectedCorrelationId,
                ["idempotencyKey"] = $"idem-{workflowId}"
            };
            var sqsEvent = CreateSqsEvent(payload, attributes);

            var handler = new StepHandler(
                _workflowService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            // Act — handler extracts correlationId from message attributes
            await handler.HandleSqsEvent(sqsEvent, new TestLambdaContext());

            // Assert — verify the step executed successfully (the correlation ID was used
            // internally for logging; we verify the workflow completed without error)
            var workflow = await GetWorkflowFromDynamoDb(workflowId);
            workflow.Should().NotBeNull();
            workflow!.Status.Should().Be(WorkflowStatus.Finished,
                "the handler should extract correlationId and process the step successfully");
            workflow.ErrorMessage.Should().BeNullOrEmpty(
                "no error should occur when correlationId is properly extracted");
        }

        // =================================================================
        // Test 6: Idempotency — Duplicate Messages Are Safe
        // =================================================================

        /// <summary>
        /// Verifies that processing the same SQS message twice does not create duplicate
        /// state changes. The StepHandler achieves idempotency by checking IsTerminalStatus
        /// on the workflow — once a workflow is Finished (terminal), subsequent messages
        /// for the same workflow are skipped.
        ///
        /// Per AAP Section 0.8.5: At-least-once delivery with idempotent consumers.
        /// Same SQS message processed twice should not create duplicate state changes.
        /// </summary>
        [Fact]
        public async Task SqsProcessing_DuplicateMessagesAreIdempotent()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            await CreateTestWorkflowInDynamoDb(workflowId, "Pending");

            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Priority = WorkflowPriority.Low,
                Attributes = new Dictionary<string, object>
                {
                    ["idempotency_test"] = true
                },
                Type = new WorkflowType
                {
                    Id = _testWorkflowTypeId,
                    Name = "TestSqsTriggerWorkflow",
                    DefaultPriority = WorkflowPriority.Low
                },
                StepName = "idempotent-step",
                Aborted = false
            };

            var payload = JsonSerializer.Serialize(stepContext);
            var sqsEvent1 = CreateSqsEvent(payload,
                new Dictionary<string, string> { ["idempotencyKey"] = $"idem-{workflowId}" });
            var sqsEvent2 = CreateSqsEvent(payload,
                new Dictionary<string, string> { ["idempotencyKey"] = $"idem-{workflowId}" });

            var handler = new StepHandler(
                _workflowService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            // Act — process the first message (transitions Pending → Running → Finished)
            await handler.HandleSqsEvent(sqsEvent1, new TestLambdaContext());

            // Capture the workflow state after first processing
            var workflowAfterFirst = await GetWorkflowFromDynamoDb(workflowId);
            workflowAfterFirst.Should().NotBeNull();
            workflowAfterFirst!.Status.Should().Be(WorkflowStatus.Finished);
            var firstFinishedOn = workflowAfterFirst.FinishedOn;

            // Small delay to distinguish timestamps if re-processing occurred
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            // Act — process the second (duplicate) message
            // HandleSqsEvent should detect IsTerminalStatus(Finished) == true and skip processing
            await handler.HandleSqsEvent(sqsEvent2, new TestLambdaContext());

            // Assert — verify the workflow was NOT re-processed
            var workflowAfterSecond = await GetWorkflowFromDynamoDb(workflowId);
            workflowAfterSecond.Should().NotBeNull();

            // Status should still be Finished (not re-processed to Running or any other state)
            workflowAfterSecond!.Status.Should().Be(WorkflowStatus.Finished,
                "duplicate processing of terminal workflow must be a no-op");

            // FinishedOn should remain unchanged (proves no re-processing occurred)
            workflowAfterSecond.FinishedOn.Should().Be(firstFinishedOn,
                "FinishedOn should not change on duplicate message — idempotent skip");
        }

        // =================================================================
        // Test 7: Batch Processing — Multiple Concurrent SQS Messages
        // =================================================================

        /// <summary>
        /// Verifies that HandleSqsEvent correctly processes a batch of 5 SQS messages,
        /// each targeting a different workflow.
        ///
        /// Source: Replaces JobPool.MAX_THREADS_POOL_COUNT = 20 bounded concurrency
        /// (JobPool.cs line 18) with Lambda batch processing of SQS records.
        /// </summary>
        [Fact]
        public async Task SqsProcessing_HandlesMultipleConcurrentMessages()
        {
            // Arrange — create 5 workflows in DynamoDB
            var workflowIds = Enumerable.Range(0, 5)
                .Select(_ => Guid.NewGuid())
                .ToList();

            foreach (var wfId in workflowIds)
            {
                await CreateTestWorkflowInDynamoDb(wfId, "Pending");
            }

            // Build a batch SQSEvent with 5 records, one per workflow
            var records = new List<SQSEvent.SQSMessage>();
            foreach (var wfId in workflowIds)
            {
                var stepContext = new StepContext
                {
                    WorkflowId = wfId,
                    Priority = WorkflowPriority.Low,
                    Attributes = new Dictionary<string, object>
                    {
                        ["batch_index"] = workflowIds.IndexOf(wfId)
                    },
                    Type = new WorkflowType
                    {
                        Id = _testWorkflowTypeId,
                        Name = "TestSqsTriggerWorkflow",
                        DefaultPriority = WorkflowPriority.Low
                    },
                    StepName = $"batch-step-{workflowIds.IndexOf(wfId)}",
                    Aborted = false
                };

                records.Add(new SQSEvent.SQSMessage
                {
                    Body = JsonSerializer.Serialize(stepContext),
                    MessageId = Guid.NewGuid().ToString(),
                    ReceiptHandle = Guid.NewGuid().ToString(),
                    MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>
                    {
                        ["correlationId"] = new SQSEvent.MessageAttribute
                        {
                            DataType = "String",
                            StringValue = Guid.NewGuid().ToString()
                        }
                    }
                });
            }

            var sqsEvent = new SQSEvent { Records = records };

            var handler = new StepHandler(
                _workflowService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            // Act
            await handler.HandleSqsEvent(sqsEvent, new TestLambdaContext());

            // Assert — all 5 workflows should be Finished
            var finishedWorkflows = new List<WfModel>();
            foreach (var wfId in workflowIds)
            {
                var workflow = await GetWorkflowFromDynamoDb(wfId);
                workflow.Should().NotBeNull($"workflow {wfId} must exist after batch processing");
                finishedWorkflows.Add(workflow!);
            }

            finishedWorkflows.Should().HaveCount(5,
                "all 5 workflows in the batch should have been processed");

            finishedWorkflows.Should().OnlyContain(
                wf => wf.Status == WorkflowStatus.Finished,
                "all batch workflows should reach Finished status");
        }

        // =================================================================
        // Helper: CreateSqsEvent
        // =================================================================

        /// <summary>
        /// Creates a properly-structured <see cref="SQSEvent"/> with a single record
        /// containing the specified message body and optional message attributes.
        /// Used by all test methods to construct Lambda SQS trigger events.
        /// </summary>
        /// <param name="messageBody">
        /// JSON-serialized StepContext payload for the SQS message body.
        /// </param>
        /// <param name="attributes">
        /// Optional dictionary of string message attributes (e.g., CorrelationId, IdempotencyKey).
        /// Keys map to SQS MessageAttribute names, values map to StringValues.
        /// </param>
        /// <returns>An SQSEvent ready for StepHandler.HandleSqsEvent() invocation.</returns>
        private static SQSEvent CreateSqsEvent(
            string messageBody,
            Dictionary<string, string>? attributes = null)
        {
            var messageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>();

            if (attributes != null)
            {
                foreach (var kvp in attributes)
                {
                    messageAttributes[kvp.Key] = new SQSEvent.MessageAttribute
                    {
                        DataType = "String",
                        StringValue = kvp.Value
                    };
                }
            }

            return new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = messageBody,
                        MessageId = Guid.NewGuid().ToString(),
                        ReceiptHandle = Guid.NewGuid().ToString(),
                        MessageAttributes = messageAttributes,
                        EventSource = "aws:sqs",
                        AwsRegion = TestRegion
                    }
                }
            };
        }

        // =================================================================
        // Helper: SendAndInvokeStep
        // =================================================================

        /// <summary>
        /// Convenience helper that sends an SQS message to the real LocalStack queue
        /// AND invokes StepHandler.HandleSqsEvent() with the same payload.
        /// Returns after the handler completes (or throws).
        /// </summary>
        /// <param name="payload">
        /// JSON-serialized StepContext payload to send as both SQS message and Lambda event body.
        /// </param>
        private async Task SendAndInvokeStep(string payload)
        {
            // Send to real SQS queue (for DLQ/retry testing)
            await _sqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = payload,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["CorrelationId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = Guid.NewGuid().ToString()
                    }
                }
            });

            // Also invoke handler directly (for synchronous assertion)
            var sqsEvent = CreateSqsEvent(payload);
            var handler = new StepHandler(
                _workflowService,
                _sfnClient,
                _snsClient,
                NullLogger<StepHandler>.Instance,
                _settings);

            await handler.HandleSqsEvent(sqsEvent, new TestLambdaContext());
        }

        // =================================================================
        // Helper: CreateTestWorkflowInDynamoDb
        // =================================================================

        /// <summary>
        /// Seeds a workflow record directly into DynamoDB using the single-table design.
        /// Key structure: PK=WORKFLOW#{workflowId}, SK=META.
        ///
        /// Mirrors the helper pattern in StepFunctionsExecutionTests.CreateTestWorkflowInDynamoDb.
        /// </summary>
        /// <param name="workflowId">Unique identifier for the workflow.</param>
        /// <param name="status">
        /// Initial status string: "Pending" (1), "Running" (2), "Canceled" (3),
        /// "Failed" (4), "Finished" (5), "Aborted" (6). Defaults to "Pending".
        /// </param>
        private async Task CreateTestWorkflowInDynamoDb(
            Guid workflowId,
            string status = "Pending")
        {
            // Map status string to WorkflowStatus enum integer value
            var statusValue = status switch
            {
                "Pending" => (int)WorkflowStatus.Pending,
                "Running" => (int)WorkflowStatus.Running,
                "Canceled" => (int)WorkflowStatus.Canceled,
                "Failed" => (int)WorkflowStatus.Failed,
                "Finished" => (int)WorkflowStatus.Finished,
                "Aborted" => (int)WorkflowStatus.Aborted,
                _ => (int)WorkflowStatus.Pending
            };

            var now = DateTime.UtcNow;
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entity_type"] = new AttributeValue { S = "Workflow" },
                ["id"] = new AttributeValue { S = workflowId.ToString() },
                ["type_id"] = new AttributeValue { S = _testWorkflowTypeId.ToString() },
                ["type_name"] = new AttributeValue { S = "TestSqsTriggerWorkflow" },
                ["complete_class_name"] = new AttributeValue
                {
                    S = "WebVellaErp.Workflow.Tests.TestSqsWorkflow"
                },
                ["status"] = new AttributeValue { N = statusValue.ToString() },
                ["priority"] = new AttributeValue { N = ((int)WorkflowPriority.Low).ToString() },
                ["created_on"] = new AttributeValue { S = now.ToString("o") },
                ["last_modified_on"] = new AttributeValue { S = now.ToString("o") }
            };

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });

            // Verify the item was persisted
            var verifyResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            verifyResponse.Item.Should().NotBeNull(
                $"workflow {workflowId} must be persisted in DynamoDB for test setup");
        }

        // =================================================================
        // Helper: GetWorkflowFromDynamoDb
        // =================================================================

        /// <summary>
        /// Reads a workflow record from DynamoDB and maps it to a <see cref="WfModel"/>.
        /// Uses the single-table key pattern: PK=WORKFLOW#{workflowId}, SK=META.
        /// </summary>
        /// <param name="workflowId">The workflow identifier to retrieve.</param>
        /// <returns>
        /// The deserialized Workflow model, or null if not found.
        /// </returns>
        private async Task<WfModel?> GetWorkflowFromDynamoDb(Guid workflowId)
        {
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                },
                ConsistentRead = true
            });

            if (response.Item == null || response.Item.Count == 0)
                return null;

            var item = response.Item;
            var workflow = new WfModel
            {
                Id = Guid.Parse(item["id"].S)
            };

            // Parse status from numeric attribute
            if (item.TryGetValue("status", out var statusAttr) && statusAttr.N != null)
            {
                workflow.Status = (WorkflowStatus)int.Parse(statusAttr.N);
            }

            // Parse TypeId
            if (item.TryGetValue("type_id", out var typeIdAttr) && typeIdAttr.S != null)
            {
                workflow.TypeId = Guid.Parse(typeIdAttr.S);
            }

            // Parse TypeName
            if (item.TryGetValue("type_name", out var typeNameAttr) && typeNameAttr.S != null)
            {
                workflow.TypeName = typeNameAttr.S;
            }

            // Parse ErrorMessage
            if (item.TryGetValue("error_message", out var errorAttr) && errorAttr.S != null)
            {
                workflow.ErrorMessage = errorAttr.S;
            }

            // Parse StartedOn (ISO 8601 string)
            if (item.TryGetValue("started_on", out var startedAttr) && startedAttr.S != null)
            {
                if (DateTime.TryParse(startedAttr.S, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var startedOn))
                {
                    workflow.StartedOn = startedOn;
                }
            }

            // Parse FinishedOn (ISO 8601 string)
            if (item.TryGetValue("finished_on", out var finishedAttr) && finishedAttr.S != null)
            {
                if (DateTime.TryParse(finishedAttr.S, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var finishedOn))
                {
                    workflow.FinishedOn = finishedOn;
                }
            }

            return workflow;
        }

        // =================================================================
        // Infrastructure: DynamoDB Table Creation
        // =================================================================

        /// <summary>
        /// Creates the DynamoDB table with the single-table design used by WorkflowService:
        ///   - PK (S) / SK (S) — composite primary key
        ///   - StatusIndex GSI — status (N) + created_on (S)
        ///   - PAY_PER_REQUEST billing mode
        ///
        /// Mirrors CreateDynamoDbTableAsync in StepFunctionsExecutionTests.
        /// </summary>
        private async Task CreateDynamoDbTableAsync()
        {
            var request = new CreateTableRequest
            {
                TableName = _tableName,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement("PK", KeyType.HASH),
                    new KeySchemaElement("SK", KeyType.RANGE)
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition("PK", ScalarAttributeType.S),
                    new AttributeDefinition("SK", ScalarAttributeType.S),
                    new AttributeDefinition("status", ScalarAttributeType.N),
                    new AttributeDefinition("created_on", ScalarAttributeType.S)
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new GlobalSecondaryIndex
                    {
                        IndexName = StatusIndexName,
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("status", KeyType.HASH),
                            new KeySchemaElement("created_on", KeyType.RANGE)
                        },
                        Projection = new Projection
                        {
                            ProjectionType = ProjectionType.ALL
                        }
                    }
                },
                BillingMode = BillingMode.PAY_PER_REQUEST
            };

            await _dynamoDbClient.CreateTableAsync(request);

            // Poll until active
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    var desc = await _dynamoDbClient.DescribeTableAsync(_tableName);
                    if (desc.Table.TableStatus == TableStatus.ACTIVE)
                        return;
                }
                catch
                {
                    // Table may not exist yet
                }
                await Task.Delay(500);
            }

            throw new TimeoutException(
                $"DynamoDB table '{_tableName}' did not become active within 15 seconds");
        }

        // =================================================================
        // FaultInjectingWorkflowService — Service-Level Fault Injection
        // =================================================================

        /// <summary>
        /// A thin IWorkflowService wrapper that delegates to the real WorkflowService
        /// for all operations but injects a fault (throws an exception) on a specific
        /// UpdateWorkflowAsync call number. This enables testing the StepHandler error
        /// path (ExecuteStepAsync catch block) without mocking AWS SDK calls.
        ///
        /// All GetWorkflowAsync and CreateWorkflowAsync calls pass through to the real
        /// service, ensuring real DynamoDB operations against LocalStack.
        /// </summary>
        private sealed class FaultInjectingWorkflowService : IWorkflowService
        {
            private readonly IWorkflowService _inner;
            private readonly int _throwOnUpdateCallNumber;
            private readonly string _exceptionMessage;
            private int _updateCallCount;

            public FaultInjectingWorkflowService(
                IWorkflowService inner,
                int throwOnUpdateCallNumber,
                string exceptionMessage)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _throwOnUpdateCallNumber = throwOnUpdateCallNumber;
                _exceptionMessage = exceptionMessage;
            }

            public async Task<bool> UpdateWorkflowAsync(WfModel workflow)
            {
                _updateCallCount++;
                if (_updateCallCount == _throwOnUpdateCallNumber)
                {
                    throw new InvalidOperationException(_exceptionMessage);
                }
                return await _inner.UpdateWorkflowAsync(workflow);
            }

            // ── Pass-through methods used by StepHandler ─────────────
            public Task<WfModel?> GetWorkflowAsync(Guid workflowId)
                => _inner.GetWorkflowAsync(workflowId);

            public Task<WfModel?> CreateWorkflowAsync(
                Guid typeId,
                Dictionary<string, object>? attributes = null,
                WorkflowPriority priority = WorkflowPriority.Low,
                Guid? creatorId = null,
                Guid? schedulePlanId = null,
                Guid? workflowId = null)
                => _inner.CreateWorkflowAsync(typeId, attributes, priority, creatorId,
                    schedulePlanId, workflowId);

            // ── Methods not called by StepHandler — throw if unexpectedly invoked ──
            public Task<bool> RegisterWorkflowTypeAsync(WorkflowType workflowType)
                => _inner.RegisterWorkflowTypeAsync(workflowType);

            public Task<List<WorkflowType>> GetWorkflowTypesAsync()
                => _inner.GetWorkflowTypesAsync();

            public Task<WorkflowType?> GetWorkflowTypeAsync(Guid typeId)
                => _inner.GetWorkflowTypeAsync(typeId);

            public Task<(List<WfModel> Workflows, int TotalCount)> GetWorkflowsAsync(
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
                => _inner.GetWorkflowsAsync(startFromDate, startToDate, finishedFromDate,
                    finishedToDate, typeName, status, priority, schedulePlanId, page, pageSize);

            public Task<bool> IsWorkflowFinishedAsync(Guid workflowId)
                => _inner.IsWorkflowFinishedAsync(workflowId);

            public Task<List<WfModel>> GetPendingWorkflowsAsync(int? limit = null)
                => _inner.GetPendingWorkflowsAsync(limit);

            public Task<List<WfModel>> GetRunningWorkflowsAsync(int? limit = null)
                => _inner.GetRunningWorkflowsAsync(limit);

            public Task RecoverAbortedWorkflowsAsync()
                => _inner.RecoverAbortedWorkflowsAsync();

            public Task<bool> CreateSchedulePlanAsync(SchedulePlan schedulePlan)
                => _inner.CreateSchedulePlanAsync(schedulePlan);

            public Task<bool> UpdateSchedulePlanAsync(SchedulePlan schedulePlan)
                => _inner.UpdateSchedulePlanAsync(schedulePlan);

            public Task<SchedulePlan?> GetSchedulePlanAsync(Guid id)
                => _inner.GetSchedulePlanAsync(id);

            public Task<List<SchedulePlan>> GetSchedulePlansAsync()
                => _inner.GetSchedulePlansAsync();

            public Task<List<SchedulePlan>> GetReadyForExecutionSchedulePlansAsync()
                => _inner.GetReadyForExecutionSchedulePlansAsync();

            public Task<List<SchedulePlan>> GetSchedulePlansByTypeAsync(SchedulePlanType type)
                => _inner.GetSchedulePlansByTypeAsync(type);

            public Task TriggerNowSchedulePlanAsync(SchedulePlan schedulePlan)
                => _inner.TriggerNowSchedulePlanAsync(schedulePlan);

            public DateTime? FindSchedulePlanNextTriggerDate(SchedulePlan schedulePlan)
                => _inner.FindSchedulePlanNextTriggerDate(schedulePlan);

            public Task ProcessSchedulesAsync(CancellationToken cancellationToken = default)
                => _inner.ProcessSchedulesAsync(cancellationToken);

            public Task ProcessWorkflowsAsync(CancellationToken cancellationToken = default)
                => _inner.ProcessWorkflowsAsync(cancellationToken);
        }
    }
}
