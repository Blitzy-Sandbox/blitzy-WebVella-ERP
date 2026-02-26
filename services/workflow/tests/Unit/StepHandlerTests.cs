using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Workflow.Functions;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;
using Xunit;

namespace WebVellaErp.Workflow.Tests.Unit
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="StepHandler"/> Lambda handler.
    /// Replaces the monolith's JobPool.cs bounded thread-pool executor (180 lines)
    /// with serverless step execution tests covering: SQS event deserialization,
    /// status transitions, error handling, abort detection, Step Functions task
    /// reporting, idempotent execution, and SNS domain event publishing.
    /// </summary>
    public class StepHandlerTests
    {
        private readonly Mock<IWorkflowService> _mockWorkflowService;
        private readonly Mock<IAmazonStepFunctions> _mockStepFunctions;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSns;
        private readonly Mock<ILogger<StepHandler>> _mockLogger;
        private readonly WorkflowSettings _settings;
        private readonly StepHandler _handler;

        /// <summary>
        /// Initializes all mocks, settings, and handler for each test.
        /// Follows established pattern from WorkflowHandlerTests.cs.
        /// </summary>
        public StepHandlerTests()
        {
            _mockWorkflowService = new Mock<IWorkflowService>();
            _mockStepFunctions = new Mock<IAmazonStepFunctions>();
            _mockSns = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<StepHandler>>();

            _settings = new WorkflowSettings
            {
                DynamoDbTableName = "test-workflows",
                StepFunctionsStateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:test",
                SnsTopicArn = "arn:aws:sns:us-east-1:000000000000:workflow-events",
                SqsQueueUrl = "http://localhost:4566/000000000000/workflow-queue",
                AwsRegion = "us-east-1",
                Enabled = true
            };

            // Default SNS mock returns success for all publishes
            _mockSns.Setup(s => s.PublishAsync(
                    It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

            // Default Step Functions mocks return success
            _mockStepFunctions.Setup(sf => sf.SendTaskSuccessAsync(
                    It.IsAny<SendTaskSuccessRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendTaskSuccessResponse());
            _mockStepFunctions.Setup(sf => sf.SendTaskFailureAsync(
                    It.IsAny<SendTaskFailureRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendTaskFailureResponse());

            // Construct handler via internal constructor (accessible via InternalsVisibleTo)
            _handler = new StepHandler(
                _mockWorkflowService.Object,
                _mockStepFunctions.Object,
                _mockSns.Object,
                _mockLogger.Object,
                _settings);
        }

        #region Helper Methods

        /// <summary>
        /// Builds an SQSEvent wrapping a single serialized StepContext as message body,
        /// with optional task token in message attributes.
        /// </summary>
        private static SQSEvent CreateSqsEvent(StepContext stepContext, string? taskToken = null)
        {
            var message = new SQSEvent.SQSMessage
            {
                MessageId = Guid.NewGuid().ToString(),
                Body = JsonSerializer.Serialize(stepContext),
                MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
            };

            if (taskToken != null)
            {
                message.MessageAttributes["taskToken"] = new SQSEvent.MessageAttribute
                {
                    DataType = "String",
                    StringValue = taskToken
                };
            }

            return new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage> { message }
            };
        }

        /// <summary>
        /// Builds an SQSEvent with a correlation ID in message attributes.
        /// Per AAP Section 0.8.5: structured logging with correlation-ID propagation.
        /// </summary>
        private static SQSEvent CreateSqsEventWithCorrelationId(
            StepContext stepContext, string correlationId, string? taskToken = null)
        {
            var sqsEvent = CreateSqsEvent(stepContext, taskToken);
            sqsEvent.Records[0].MessageAttributes["correlationId"] = new SQSEvent.MessageAttribute
            {
                DataType = "String",
                StringValue = correlationId
            };
            return sqsEvent;
        }

        /// <summary>
        /// Creates a Workflow test instance with sensible defaults.
        /// Maps from monolith's Job model status transitions.
        /// </summary>
        private static Models.Workflow CreateTestWorkflow(
            Guid? id = null, WorkflowStatus status = WorkflowStatus.Pending)
        {
            var workflowId = id ?? Guid.NewGuid();
            return new Models.Workflow
            {
                Id = workflowId,
                Status = status,
                TypeId = Guid.NewGuid(),
                TypeName = "TestWorkflowType",
                CompleteClassName = "WebVellaErp.Workflow.Tests.TestStep",
                Priority = WorkflowPriority.Medium,
                CreatedOn = DateTime.UtcNow.AddMinutes(-5),
                CreatedBy = Guid.NewGuid(),
                Attributes = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Creates a WorkflowType test instance with configurable properties.
        /// Maps from monolith's JobType model.
        /// </summary>
        private static WorkflowType CreateTestWorkflowType(
            string name = "TestType", bool allowSingleInstance = false)
        {
            return new WorkflowType
            {
                Id = Guid.NewGuid(),
                Name = name,
                DefaultPriority = WorkflowPriority.Medium,
                AllowSingleInstance = allowSingleInstance
            };
        }

        /// <summary>
        /// Creates a StepContext for testing with default properties.
        /// Mirrors monolith's JobContext construction in JobPool.RunJobAsync() lines 68-73.
        /// </summary>
        private static StepContext CreateTestStepContext(Guid? workflowId = null)
        {
            return new StepContext
            {
                WorkflowId = workflowId ?? Guid.NewGuid(),
                Aborted = false,
                Priority = WorkflowPriority.Medium,
                Attributes = new Dictionary<string, object> { { "key", "value" } },
                Result = null,
                Type = CreateTestWorkflowType(),
                StepName = "test-step",
                StepFunctionsExecutionArn = "arn:aws:states:us-east-1:000000000000:execution:test:exec-1"
            };
        }

        /// <summary>
        /// Sets up a failure scenario where the 3rd GetWorkflowAsync call (refresh in ExecuteStepAsync)
        /// throws InvalidOperationException to trigger error handling paths.
        /// Calls: (1) initial fetch in HandleSqsEvent, (2) abort check, (3) refresh throws.
        /// </summary>
        private void SetupFailureMocks(Models.Workflow workflow, string errorMessage = "Step execution failed")
        {
            var callCount = 0;
            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    if (callCount >= 3)
                        throw new InvalidOperationException(errorMessage);
                    return CreateTestWorkflow(workflow.Id, workflow.Status);
                });
        }

        #endregion

        #region Phase 2: SQS Event Deserialization Tests

        /// <summary>
        /// Verifies that HandleSqsEvent correctly deserializes StepContext from SQS message body.
        /// Source: JobPool.RunJobAsync() lines 68-73 — constructed JobContext from Job properties.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_DeserializesStepContextFromMessageBody()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));

            var capturedUpdates = new List<WorkflowStatus>();
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) => capturedUpdates.Add(w.Status))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — handler successfully processed the deserialized StepContext
            _mockWorkflowService.Verify(s => s.GetWorkflowAsync(
                workflowId), Times.AtLeastOnce());
            capturedUpdates.Should().NotBeEmpty("handler should transition workflow status after deserialization");
        }

        /// <summary>
        /// Verifies that HandleSqsEvent processes each SQS record sequentially.
        /// Each record contains a separate StepContext for a different workflow.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_HandlesMultipleRecords()
        {
            // Arrange
            var workflowId1 = Guid.NewGuid();
            var workflowId2 = Guid.NewGuid();
            var stepContext1 = CreateTestStepContext(workflowId1);
            var stepContext2 = CreateTestStepContext(workflowId2);

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = JsonSerializer.Serialize(stepContext1),
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    },
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = JsonSerializer.Serialize(stepContext2),
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    }
                }
            };
            var lambdaContext = new TestLambdaContext();

            // Return fresh workflow for each ID to avoid mutation contamination
            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync((Guid id) => CreateTestWorkflow(id));

            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — both workflows were retrieved (at least once each)
            _mockWorkflowService.Verify(s => s.GetWorkflowAsync(
                workflowId1), Times.AtLeastOnce());
            _mockWorkflowService.Verify(s => s.GetWorkflowAsync(
                workflowId2), Times.AtLeastOnce());
        }

        /// <summary>
        /// Verifies that an SQSEvent with empty Records list is handled gracefully.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_HandlesEmptyRecords()
        {
            // Arrange
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>()
            };
            var lambdaContext = new TestLambdaContext();

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — no workflow service calls should be made
            _mockWorkflowService.Verify(s => s.GetWorkflowAsync(
                It.IsAny<Guid>()), Times.Never());
            _mockWorkflowService.Verify(s => s.UpdateWorkflowAsync(
                It.IsAny<Models.Workflow>()), Times.Never());
        }

        /// <summary>
        /// Verifies correlation ID is extracted from SQS message attributes for structured logging.
        /// Per AAP Section 0.8.5: correlation-ID propagation from all Lambda functions.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_ExtractsCorrelationIdFromMessageAttributes()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var correlationId = Guid.NewGuid().ToString();
            var sqsEvent = CreateSqsEventWithCorrelationId(stepContext, correlationId);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — correlation ID is propagated to SNS event publishing
            _mockSns.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.MessageAttributes != null &&
                    p.MessageAttributes.ContainsKey("correlationId") &&
                    p.MessageAttributes["correlationId"].StringValue == correlationId),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        #endregion

        #region Phase 3: Status Transition Preservation Tests

        /// <summary>
        /// Verifies workflow status is updated to Running at start of processing.
        /// Source: JobPool.Process() lines 82-89: job.Status = JobStatus.Running; job.StartedOn = DateTime.UtcNow;
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_TransitionsWorkflowToRunning()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));

            var capturedStatuses = new List<(WorkflowStatus Status, DateTime? StartedOn)>();
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                    capturedStatuses.Add((w.Status, w.StartedOn)))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — first update should be Running with StartedOn set
            capturedStatuses.Should().NotBeEmpty();
            capturedStatuses[0].Status.Should().Be(WorkflowStatus.Running);
            capturedStatuses[0].StartedOn.Should().HaveValue();
            capturedStatuses[0].StartedOn!.Value.Should().BeCloseTo(
                DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Verifies full success path: Pending → Running → Finished.
        /// Source: JobPool.Process() lines 121-126: job.FinishedOn; job.Status = JobStatus.Finished;
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_TransitionsToFinished_OnSuccess()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));

            var capturedStatuses = new List<(WorkflowStatus Status, DateTime? FinishedOn)>();
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                    capturedStatuses.Add((w.Status, w.FinishedOn)))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — final update should be Finished with FinishedOn set
            var lastUpdate = capturedStatuses[capturedStatuses.Count - 1];
            lastUpdate.Status.Should().Be(WorkflowStatus.Finished);
            lastUpdate.FinishedOn.Should().HaveValue();
            lastUpdate.FinishedOn!.Value.Should().BeCloseTo(
                DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Verifies failure path: Pending → Running → Failed when exception occurs.
        /// Source: JobPool.Process() lines 128-148: catch block sets Failed + ErrorMessage.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_TransitionsToFailed_OnException()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            SetupFailureMocks(CreateTestWorkflow(workflowId), "Step execution failed");

            var capturedStatuses = new List<(WorkflowStatus Status, string? ErrorMessage)>();
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                    capturedStatuses.Add((w.Status, w.ErrorMessage)))
                .ReturnsAsync(true);

            // Act & Assert — HandleSqsEvent re-throws after error handling
            Func<Task> act = () => _handler.HandleSqsEvent(sqsEvent, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert — should have at least one Failed status update
            capturedStatuses.Should().Contain(t => t.Status == WorkflowStatus.Failed);
        }

        #endregion

        #region Phase 4: Error Handling Tests

        /// <summary>
        /// Verifies workflow.ErrorMessage = ex.Message is captured on failure.
        /// Source: JobPool.Process() line 141: job.ErrorMessage = ex.Message;
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_CapturesErrorMessage_OnFailure()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();
            var expectedErrorMessage = "Test error message for validation";

            SetupFailureMocks(CreateTestWorkflow(workflowId), expectedErrorMessage);

            string? capturedErrorMessage = null;
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                {
                    if (w.Status == WorkflowStatus.Failed)
                        capturedErrorMessage = w.ErrorMessage;
                })
                .ReturnsAsync(true);

            // Act
            Func<Task> act = () => _handler.HandleSqsEvent(sqsEvent, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert — error message should be captured from the exception
            capturedErrorMessage.Should().NotBeNull();
            capturedErrorMessage.Should().Contain(expectedErrorMessage);
        }

        /// <summary>
        /// Verifies that structured logging occurs on failure.
        /// Source: JobPool.Process() lines 136-137: Log.Create(LogType.Error, ..., ex);
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_LogsError_OnFailure()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            SetupFailureMocks(CreateTestWorkflow(workflowId), "Logging test failure");

            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            Func<Task> act = () => _handler.HandleSqsEvent(sqsEvent, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert — verify logger received an error-level log call
            _mockLogger.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.AtLeastOnce());
        }

        /// <summary>
        /// Verifies FinishedOn timestamp is set on failure path.
        /// Source: JobPool.Process() line 139: job.FinishedOn = DateTime.UtcNow; (in catch block)
        /// Also: JobPool.Process() line 124: job.FinishedOn = DateTime.UtcNow; (in try block)
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_SetsFinishedOnTimestamp_OnFailure()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            SetupFailureMocks(CreateTestWorkflow(workflowId), "Timestamp test failure");

            DateTime? capturedFinishedOn = null;
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                {
                    if (w.Status == WorkflowStatus.Failed)
                        capturedFinishedOn = w.FinishedOn;
                })
                .ReturnsAsync(true);

            // Act
            Func<Task> act = () => _handler.HandleSqsEvent(sqsEvent, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert — FinishedOn should be set even on failure
            capturedFinishedOn.Should().HaveValue();
            capturedFinishedOn!.Value.Should().BeCloseTo(
                DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        #endregion

        #region Phase 5: Result Propagation Tests

        /// <summary>
        /// Verifies that if stepContext.Result is non-null, it is saved to the workflow.
        /// Source: JobPool.Process() lines 121-122: if (context.Result != null) job.Result = context.Result;
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_PropagatesResult_WhenNotNull()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            stepContext.Result = new Dictionary<string, object>
            {
                { "output", "test-value" },
                { "count", 42 }
            };
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));

            Dictionary<string, object>? capturedResult = null;
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                {
                    if (w.Status == WorkflowStatus.Finished && w.Result != null)
                        capturedResult = new Dictionary<string, object>(w.Result);
                })
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — result should be propagated to the workflow
            capturedResult.Should().NotBeNull();
            capturedResult.Should().ContainKey("output");
        }

        /// <summary>
        /// Verifies that if stepContext.Result is null, workflow.Result is not overwritten.
        /// Source: the if (context.Result != null) guard at JobPool.Process() line 121.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_DoesNotOverwriteResult_WhenNull()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            stepContext.Result = null; // Explicitly null

            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            // Return workflow with existing result that should not be overwritten
            var existingResult = new Dictionary<string, object> { { "existing", "data" } };
            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() =>
                {
                    var w = CreateTestWorkflow(workflowId);
                    w.Result = existingResult;
                    return w;
                });

            Dictionary<string, object>? capturedResult = null;
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                {
                    if (w.Status == WorkflowStatus.Finished)
                        capturedResult = w.Result != null
                            ? new Dictionary<string, object>(w.Result)
                            : null;
                })
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — the existing result should be preserved (not overwritten with null)
            capturedResult.Should().NotBeNull();
            capturedResult.Should().ContainKey("existing");
        }

        #endregion

        #region Phase 6: Abort Detection Tests

        /// <summary>
        /// Verifies that if the workflow is already Aborted in the DB, handler detects it.
        /// Replaces JobPool.AbortJob() lines 161-169 cooperative abort via context.Aborted = true.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_DetectsAbortedWorkflow()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            // First call: return Pending workflow for initial fetch
            // But IsWorkflowAbortedAsync or ExecuteStepAsync will detect abort
            var callCount = 0;
            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    // Second call is the abort check — return Aborted
                    if (callCount >= 2)
                        return CreateTestWorkflow(workflowId, WorkflowStatus.Aborted);
                    return CreateTestWorkflow(workflowId, WorkflowStatus.Pending);
                });

            var capturedStatuses = new List<WorkflowStatus>();
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                    capturedStatuses.Add(w.Status))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — workflow should be set to Aborted, not Finished
            capturedStatuses.Should().Contain(WorkflowStatus.Aborted);
            capturedStatuses.Should().NotContain(WorkflowStatus.Finished);
        }

        /// <summary>
        /// Verifies that when stepContext.Aborted is true, processing is skipped.
        /// Source: JobPool.AbortJob() lines 161-169 — cooperative abort.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_SkipsProcessing_WhenAborted()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            stepContext.Aborted = true; // Pre-marked as aborted
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));

            var capturedStatuses = new List<WorkflowStatus>();
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) =>
                    capturedStatuses.Add(w.Status))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — should NOT transition to Finished; aborted path taken
            capturedStatuses.Should().NotContain(WorkflowStatus.Finished);
        }

        #endregion

        #region Phase 7: Step Functions Task Reporting Tests

        /// <summary>
        /// Verifies SendTaskSuccessAsync is called on successful completion with task token.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_SendsTaskSuccess_OnCompletion()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var taskToken = "test-task-token-success-" + Guid.NewGuid();
            var sqsEvent = CreateSqsEvent(stepContext, taskToken);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — SendTaskSuccessAsync should be called with the task token
            _mockStepFunctions.Verify(sf => sf.SendTaskSuccessAsync(
                It.Is<SendTaskSuccessRequest>(r => r.TaskToken == taskToken),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        /// <summary>
        /// Verifies SendTaskFailureAsync is called on exception with task token.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_SendsTaskFailure_OnException()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var taskToken = "test-task-token-failure-" + Guid.NewGuid();
            var sqsEvent = CreateSqsEvent(stepContext, taskToken);
            var lambdaContext = new TestLambdaContext();

            SetupFailureMocks(CreateTestWorkflow(workflowId), "Task failure test");

            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            Func<Task> act = () => _handler.HandleSqsEvent(sqsEvent, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert — SendTaskFailureAsync should be called with the task token
            _mockStepFunctions.Verify(sf => sf.SendTaskFailureAsync(
                It.Is<SendTaskFailureRequest>(r => r.TaskToken == taskToken),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        /// <summary>
        /// Verifies that Step Functions reporting is skipped when no task token is present.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_SkipsTaskReporting_WhenNoTaskToken()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            // No task token in the SQS event
            var sqsEvent = CreateSqsEvent(stepContext, taskToken: null);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — neither SendTaskSuccess nor SendTaskFailure should be called
            _mockStepFunctions.Verify(sf => sf.SendTaskSuccessAsync(
                It.IsAny<SendTaskSuccessRequest>(),
                It.IsAny<CancellationToken>()), Times.Never());
            _mockStepFunctions.Verify(sf => sf.SendTaskFailureAsync(
                It.IsAny<SendTaskFailureRequest>(),
                It.IsAny<CancellationToken>()), Times.Never());
        }

        #endregion

        #region Phase 8: Direct Step Functions Invocation Tests

        /// <summary>
        /// Verifies HandleStepFunctionTask returns an updated StepContext.
        /// Direct Step Functions Lambda invoke (not SQS-triggered).
        /// </summary>
        [Fact]
        public async Task HandleStepFunctionTask_ReturnsUpdatedStepContext()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            var result = await _handler.HandleStepFunctionTask(stepContext, lambdaContext);

            // Assert — should return a non-null StepContext
            result.Should().NotBeNull();
            result.WorkflowId.Should().Be(workflowId);
        }

        /// <summary>
        /// Verifies HandleStepFunctionTask sets Result on the returned StepContext.
        /// </summary>
        [Fact]
        public async Task HandleStepFunctionTask_SetsResultOnSuccess()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            stepContext.Result = new Dictionary<string, object> { { "step_output", "success" } };
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            var result = await _handler.HandleStepFunctionTask(stepContext, lambdaContext);

            // Assert — result should be preserved on the returned StepContext
            result.Should().NotBeNull();
            result.Result.Should().NotBeNull();
            result.Result.Should().ContainKey("step_output");
        }

        /// <summary>
        /// Verifies HandleStepFunctionTask propagates exceptions for Step Functions error handling.
        /// When a failure occurs, the exception propagates to Step Functions.
        /// </summary>
        [Fact]
        public async Task HandleStepFunctionTask_ThrowsOnFailure()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var lambdaContext = new TestLambdaContext();

            SetupFailureMocks(CreateTestWorkflow(workflowId), "Direct invocation failure");

            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act & Assert — exception should propagate for Step Functions error handling
            Func<Task> act = () => _handler.HandleStepFunctionTask(stepContext, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Direct invocation failure*");
        }

        #endregion

        #region Phase 9: Idempotent Execution Tests

        /// <summary>
        /// Verifies that if a workflow is already Finished, duplicate SQS message is not re-processed.
        /// Per AAP Section 0.8.5: All event consumers MUST be idempotent.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_PreventsDuplicateProcessing()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            // Return already-Finished workflow — IsTerminalStatus check should prevent re-processing
            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(CreateTestWorkflow(workflowId, WorkflowStatus.Finished));

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — no status transitions should occur for an already-finished workflow
            _mockWorkflowService.Verify(s => s.UpdateWorkflowAsync(
                It.IsAny<Models.Workflow>()), Times.Never());
        }

        /// <summary>
        /// Verifies idempotent handling when the same workflow ID appears in two SQS messages.
        /// The first processes normally; the second is skipped because workflow is now terminal.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_IdempotentForSameWorkflowId()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext1 = CreateTestStepContext(workflowId);
            var stepContext2 = CreateTestStepContext(workflowId);

            // First message processes normally, making the workflow Finished
            // Second message should find it in terminal state and skip
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = JsonSerializer.Serialize(stepContext1),
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    },
                    new SQSEvent.SQSMessage
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = JsonSerializer.Serialize(stepContext2),
                        MessageAttributes = new Dictionary<string, SQSEvent.MessageAttribute>()
                    }
                }
            };
            var lambdaContext = new TestLambdaContext();

            // Track GetWorkflowAsync call count to return terminal status after first processing
            var getCallCount = 0;
            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    workflowId))
                .ReturnsAsync(() =>
                {
                    getCallCount++;
                    // After the first record fully processes (calls 1,2,3),
                    // the 4th+ calls (second record) return Finished
                    if (getCallCount > 3)
                        return CreateTestWorkflow(workflowId, WorkflowStatus.Finished);
                    return CreateTestWorkflow(workflowId, WorkflowStatus.Pending);
                });

            var updateCount = 0;
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .Callback<Models.Workflow>((w) => updateCount++)
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — only the first record should trigger updates (Running + Finished = 2)
            // The second record should be skipped (terminal status check)
            updateCount.Should().BeLessOrEqualTo(3,
                "second duplicate message should be skipped due to terminal status");
        }

        #endregion

        #region Phase 10: SNS Event Publishing Tests

        /// <summary>
        /// Verifies SNS publish for workflow.step.completed on successful step execution.
        /// Per AAP Section 0.8.5: domain events via SNS.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_PublishesCompletedEvent_OnSuccess()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — SNS should receive a "completed" event
            _mockSns.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.TopicArn == _settings.SnsTopicArn &&
                    p.Message.Contains("completed")),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        /// <summary>
        /// Verifies SNS publish for workflow.step.failed on exception.
        /// Per AAP Section 0.8.5: domain events via SNS.
        /// </summary>
        [Fact]
        public async Task HandleSqsEvent_PublishesFailedEvent_OnException()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            SetupFailureMocks(CreateTestWorkflow(workflowId), "SNS failure test");

            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            // Act
            Func<Task> act = () => _handler.HandleSqsEvent(sqsEvent, lambdaContext);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert — SNS should receive a "failed" event
            _mockSns.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(p =>
                    p.TopicArn == _settings.SnsTopicArn &&
                    p.Message.Contains("failed")),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        /// <summary>
        /// Verifies that published events follow the {domain}.{entity}.{action} naming convention.
        /// Per AAP Section 0.8.5: Event naming convention.
        /// </summary>
        [Fact]
        public async Task PublishedEvents_FollowNamingConvention()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var stepContext = CreateTestStepContext(workflowId);
            var sqsEvent = CreateSqsEvent(stepContext);
            var lambdaContext = new TestLambdaContext();

            _mockWorkflowService.Setup(s => s.GetWorkflowAsync(
                    It.IsAny<Guid>()))
                .ReturnsAsync(() => CreateTestWorkflow(workflowId));
            _mockWorkflowService.Setup(s => s.UpdateWorkflowAsync(
                    It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            var capturedMessages = new List<string>();
            _mockSns.Setup(s => s.PublishAsync(
                    It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((p, _) =>
                    capturedMessages.Add(p.Message))
                .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

            // Act
            await _handler.HandleSqsEvent(sqsEvent, lambdaContext);

            // Assert — all published events should follow workflow.step.{action} pattern
            capturedMessages.Should().NotBeEmpty();
            foreach (var message in capturedMessages)
            {
                // Event type in the message should match domain.entity.action pattern
                message.Should().Contain("workflow.step.",
                    "event type should follow {domain}.{entity}.{action} convention");
            }
        }

        #endregion
    }
}
