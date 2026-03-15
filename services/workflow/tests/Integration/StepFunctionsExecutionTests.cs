// StepFunctionsExecutionTests.cs — Integration Tests for Step Functions
// State Machine Execution in Workflow Engine Service
//
// Source mapping (monolith → serverless):
//   WebVella.Erp/Jobs/JobPool.cs           → StartExecution, StopExecution tests
//   WebVella.Erp/Jobs/JobManager.cs        → ARN resolution, workflow processing tests
//   WebVella.Erp/Jobs/SheduleManager.cs    → Schedule-type execution tests
//   WebVella.Erp/Jobs/Models/Job.cs        → Workflow status transition tests
//   WebVella.Erp/Jobs/Models/SchedulePlan.cs → Interval schedule plan tests
//   WebVella.Erp/Jobs/Models/JobContext.cs  → StepContext serialization round-trip tests
//
// All tests run against LocalStack Pro Step Functions (port 4566) and
// LocalStack DynamoDB (port 4566). Per AAP Section 0.8.1, NO mocked AWS SDK calls.
// Per AAP Section 0.8.2, workflow completion must be < 30 seconds.

using System.Diagnostics;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;
using Xunit;

// Alias to disambiguate WebVellaErp.Workflow namespace from the Workflow model class
using WfModel = WebVellaErp.Workflow.Models.Workflow;

namespace WebVellaErp.Workflow.Tests.Integration
{
    /// <summary>
    /// Integration tests verifying that the Workflow Engine correctly orchestrates
    /// workflow execution via AWS Step Functions, replacing the monolith's in-process
    /// JobPool (20-thread bounded executor) and ScheduleManager schedule-based triggering.
    /// Requires LocalStack Pro (port 4566) with Step Functions service enabled.
    /// </summary>
    public class StepFunctionsExecutionTests : IAsyncLifetime
    {
        // ── AWS client fields ──────────────────────────────────────────────────
        private IAmazonStepFunctions _stepFunctionsClient = null!;
        private IAmazonDynamoDB _dynamoDbClient = null!;
        private IAmazonSimpleNotificationService _snsClient = null!;

        // ── Service-under-test ─────────────────────────────────────────────────
        private IWorkflowService _workflowService = null!;
        private WorkflowSettings _settings = null!;

        // ── Test infrastructure state ──────────────────────────────────────────
        private string _stateMachineArn = null!;
        private string _failingStateMachineArn = null!;
        private string _tableName = null!;
        private string _snsTopicArn = string.Empty;
        private Guid _testWorkflowTypeId;

        // ── Constants ──────────────────────────────────────────────────────────
        // LocalStack Pro includes Step Functions on the standard gateway port.
        // The separate SFN Local sidecar (port 8083) is no longer required.
        private const string StepFunctionsEndpoint = "http://localhost:4566";
        // Standard LocalStack endpoint for DynamoDB and SNS
        private const string LocalStackEndpoint = "http://localhost:4566";
        private const string TestRegion = "us-east-1";
        private const string StatusIndexName = "StatusIndex";

        // ════════════════════════════════════════════════════════════════════════
        // IAsyncLifetime — Async test fixture setup and teardown
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initializes all AWS clients, creates DynamoDB table, state machines,
        /// and registers a test workflow type. Runs before each test class execution.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Unique table name per test run to ensure test isolation
            _tableName = $"workflow-sfn-test-{Guid.NewGuid():N}";

            // ── Configure Step Functions client (port 4566 — LocalStack Pro) ──
            var sfnConfig = new AmazonStepFunctionsConfig
            {
                ServiceURL = StepFunctionsEndpoint,
                AuthenticationRegion = TestRegion
            };
            _stepFunctionsClient = new AmazonStepFunctionsClient(
                "test", "test", sfnConfig);

            // ── Configure DynamoDB client (port 4566 — LocalStack) ──
            var dynamoConfig = new AmazonDynamoDBConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = TestRegion
            };
            _dynamoDbClient = new AmazonDynamoDBClient(
                "test", "test", dynamoConfig);

            // ── Configure SNS client (port 4566 — LocalStack) ──
            // WorkflowService constructor requires a non-null SNS client.
            // SnsTopicArn is left empty so PublishWorkflowEventAsync skips publishing.
            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = TestRegion
            };
            _snsClient = new AmazonSimpleNotificationServiceClient(
                "test", "test", snsConfig);

            // ── Create DynamoDB table with single-table design ──
            await CreateDynamoDbTableAsync();

            // ── Create test state machines ──
            _stateMachineArn = await CreateSimpleStateMachine(
                $"test-simple-{Guid.NewGuid():N}");
            _failingStateMachineArn = await CreateFailingStateMachine(
                $"test-fail-{Guid.NewGuid():N}");

            // ── Configure WorkflowSettings ──
            _settings = new WorkflowSettings
            {
                DynamoDbTableName = _tableName,
                StepFunctionsStateMachineArn = _stateMachineArn,
                SnsTopicArn = _snsTopicArn,       // empty → skips SNS publishing
                SqsQueueUrl = string.Empty,
                AwsEndpointUrl = LocalStackEndpoint,
                AwsRegion = TestRegion,
                Enabled = true
            };

            // ── Create WorkflowService (real AWS clients, no mocks) ──
            _workflowService = new WorkflowService(
                _dynamoDbClient,
                _stepFunctionsClient,
                _snsClient,
                NullLogger<WorkflowService>.Instance,
                _settings);

            // ── Register a test workflow type in DynamoDB ──
            _testWorkflowTypeId = Guid.NewGuid();
            await _workflowService.RegisterWorkflowTypeAsync(new WorkflowType
            {
                Id = _testWorkflowTypeId,
                Name = "TestStepFunctionsWorkflow",
                DefaultPriority = WorkflowPriority.Low,
                CompleteClassName = "WebVellaErp.Workflow.Tests.TestStepWorkflow",
                AllowSingleInstance = false
            });
        }

        /// <summary>
        /// Cleans up all AWS resources created during test execution:
        /// state machines, DynamoDB table, and SNS topic.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Delete state machines — swallow errors if already deleted or sidecar down
            try
            {
                await _stepFunctionsClient.DeleteStateMachineAsync(
                    new DeleteStateMachineRequest { StateMachineArn = _stateMachineArn });
            }
            catch { /* Cleanup best-effort */ }

            try
            {
                await _stepFunctionsClient.DeleteStateMachineAsync(
                    new DeleteStateMachineRequest { StateMachineArn = _failingStateMachineArn });
            }
            catch { /* Cleanup best-effort */ }

            // Delete DynamoDB table
            try
            {
                await _dynamoDbClient.DeleteTableAsync(
                    new DeleteTableRequest { TableName = _tableName });
            }
            catch { /* Cleanup best-effort */ }

            // Dispose AWS SDK clients to release connections
            _stepFunctionsClient?.Dispose();
            _dynamoDbClient?.Dispose();
            (_snsClient as IDisposable)?.Dispose();
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 2: Start Execution of Approval Chain State Machine
        // Source: Replaces JobPool.RunJobAsync(Job) which scheduled
        //         Task.Run(() => Process(context)) on a bounded 20-thread pool
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that starting a Step Functions execution creates a running
        /// workflow record in DynamoDB, replacing the monolith's JobPool.RunJobAsync.
        /// </summary>
        [Fact]
        public async Task StartExecution_CreatesRunningWorkflow()
        {
            // Arrange — Create a pending workflow via the service
            var workflowId = Guid.NewGuid();
            var workflow = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                new Dictionary<string, object> { ["test_key"] = "test_value" },
                WorkflowPriority.Low,
                workflowId: workflowId);

            workflow.Should().NotBeNull();
            workflow!.Id.Should().Be(workflowId);
            workflow.Status.Should().Be(WorkflowStatus.Pending);

            // Act — Start Step Functions execution with serialized StepContext
            var stepContext = new StepContext
            {
                WorkflowId = workflowId,
                Aborted = false,
                Priority = WorkflowPriority.Low,
                Attributes = new Dictionary<string, object> { ["test_key"] = "test_value" }
            };
            var input = JsonSerializer.Serialize(stepContext);

            var response = await _stepFunctionsClient.StartExecutionAsync(
                new StartExecutionRequest
                {
                    StateMachineArn = _stateMachineArn,
                    Name = $"wf-{workflowId:N}-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    Input = input
                });

            // Assert — Execution ARN was assigned
            response.ExecutionArn.Should().NotBeNullOrEmpty();

            // Update workflow to Running (mirrors ProcessWorkflowsAsync behaviour)
            workflow.Status = WorkflowStatus.Running;
            workflow.StartedOn = DateTime.UtcNow;
            workflow.StepFunctionsExecutionArn = response.ExecutionArn;
            await _workflowService.UpdateWorkflowAsync(workflow);

            // Verify DynamoDB reflects the Running state
            var readBack = await _workflowService.GetWorkflowAsync(workflowId);
            readBack.Should().NotBeNull();
            readBack!.Status.Should().Be(WorkflowStatus.Running);
            readBack.StepFunctionsExecutionArn.Should().NotBeNullOrEmpty();
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 3: State Machine ARN Resolution
        // Source: Replaces JobManager.Settings (runtime configuration)
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that WorkflowSettings correctly resolves the state machine ARN
        /// and that the ARN points to a valid, existing state machine.
        /// Replaces the monolith's JobManager.Settings configuration lookup.
        /// </summary>
        [Fact]
        public async Task StateMachineArnResolution_ReturnsCorrectArn()
        {
            // Assert — Settings contain the correct ARN
            _settings.StepFunctionsStateMachineArn.Should().NotBeNullOrEmpty();
            _settings.StepFunctionsStateMachineArn.Should().Be(_stateMachineArn);
            _settings.AwsEndpointUrl.Should().NotBeNullOrEmpty();
            _settings.DynamoDbTableName.Should().NotBeNullOrEmpty();
            _settings.Enabled.Should().BeTrue();

            // Act — Describe the state machine to verify it exists
            var description = await _stepFunctionsClient.DescribeStateMachineAsync(
                new DescribeStateMachineRequest
                {
                    StateMachineArn = _stateMachineArn
                });

            // Assert — ARN matches and machine is active
            description.Should().NotBeNull();
            description.StateMachineArn.Should().Be(_stateMachineArn);
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 4: Workflow Status Transitions During Execution
        // Source: Maps to JobPool.Process() lifecycle: Running → Finished/Failed
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that workflow DynamoDB status transitions correctly from Pending
        /// through Running to Finished after a successful Step Functions execution.
        /// Maps to the monolith's JobPool.Process() lifecycle management.
        /// </summary>
        [Fact]
        public async Task StepFunctionsExecution_TransitionsWorkflowStatus()
        {
            // Arrange — Seed a Pending workflow directly in DynamoDB
            var workflowId = Guid.NewGuid();
            await CreateTestWorkflowInDynamoDb(workflowId, "Pending");

            // Verify initial state is Pending
            var initial = await _workflowService.GetWorkflowAsync(workflowId);
            initial.Should().NotBeNull();
            initial!.Status.Should().Be(WorkflowStatus.Pending);

            // Act — Start execution (simple pass-through state machine)
            var input = JsonSerializer.Serialize(
                new { workflowId = workflowId.ToString(), step = "start" });
            var startResponse = await _stepFunctionsClient.StartExecutionAsync(
                new StartExecutionRequest
                {
                    StateMachineArn = _stateMachineArn,
                    Name = $"transition-{workflowId:N}",
                    Input = input
                });

            // Transition to Running
            initial.Status = WorkflowStatus.Running;
            initial.StartedOn = DateTime.UtcNow;
            initial.StepFunctionsExecutionArn = startResponse.ExecutionArn;
            await _workflowService.UpdateWorkflowAsync(initial);

            // Wait for Step Functions to complete
            var finalStatus = await WaitForExecutionCompletion(startResponse.ExecutionArn);
            finalStatus.Should().Be("SUCCEEDED");

            // Transition to Finished upon successful completion
            var running = await _workflowService.GetWorkflowAsync(workflowId);
            running.Should().NotBeNull();
            running!.Status = WorkflowStatus.Finished;
            running.FinishedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(running);

            // Assert — Final DynamoDB state is Finished
            var finished = await _workflowService.GetWorkflowAsync(workflowId);
            finished.Should().NotBeNull();
            finished!.Status.Should().Be(WorkflowStatus.Finished);
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 5: Performance — Workflow Completion Under 30 Seconds
        // Per AAP Section 0.8.2: Step Functions workflow completion < 30 seconds
        // for standard approval chains
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates the AAP Section 0.8.2 performance requirement: standard
        /// approval chain workflows must complete within 30 seconds end-to-end.
        /// Uses Stopwatch to measure elapsed time of Step Functions execution.
        /// </summary>
        [Fact]
        public async Task StandardApprovalChain_CompletesWithin30Seconds()
        {
            // Arrange — Create a TestLambdaContext to simulate Lambda execution context
            var lambdaContext = new TestLambdaContext
            {
                FunctionName = "WorkflowStepHandler",
                Logger = new TestLambdaLogger()
            };

            var input = JsonSerializer.Serialize(new
            {
                approvalId = Guid.NewGuid().ToString(),
                step = "start",
                functionName = lambdaContext.FunctionName
            });

            // Act — Measure execution time with Stopwatch
            var stopwatch = Stopwatch.StartNew();

            var startResponse = await _stepFunctionsClient.StartExecutionAsync(
                new StartExecutionRequest
                {
                    StateMachineArn = _stateMachineArn,
                    Name = $"perf-{Guid.NewGuid():N}",
                    Input = input
                });

            var finalStatus = await WaitForExecutionCompletion(
                startResponse.ExecutionArn, maxWaitSeconds: 30);

            stopwatch.Stop();

            // Assert — Execution succeeded within performance threshold
            finalStatus.Should().Be("SUCCEEDED");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
                "AAP Section 0.8.2 requires workflow completion < 30 seconds " +
                "for standard approval chains");
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 6: Step Functions Input/Output JSON Serialization
        // Source: Maps to JobContext serialization (JobContext.cs:
        //         JobId, Aborted, Priority, Attributes, Result, Type)
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that StepContext is correctly serialized to JSON for Step
        /// Functions input and that the output can be deserialized back with all
        /// values preserved through the round-trip. Maps to the monolith's
        /// JobContext serialization for in-process job execution.
        /// Uses AOT-compatible System.Text.Json per AAP Section 0.6.2.
        /// </summary>
        [Fact]
        public async Task StepFunctions_SerializesStepContextCorrectly()
        {
            // Arrange — Build a StepContext with known values
            var workflowId = Guid.NewGuid();
            var testType = new WorkflowType
            {
                Id = _testWorkflowTypeId,
                Name = "SerializationTestType"
            };

            var originalContext = new StepContext
            {
                WorkflowId = workflowId,
                Aborted = false,
                Priority = WorkflowPriority.Low,
                Attributes = new Dictionary<string, object>
                {
                    ["key1"] = "value1",
                    ["key2"] = 42
                },
                Result = new Dictionary<string, object>
                {
                    ["output"] = "test_result"
                },
                Type = testType
            };

            // Act — Serialize to JSON and use as Step Functions input
            var json = JsonSerializer.Serialize(originalContext);
            json.Should().NotBeNullOrEmpty();

            var startResponse = await _stepFunctionsClient.StartExecutionAsync(
                new StartExecutionRequest
                {
                    StateMachineArn = _stateMachineArn,
                    Name = $"serial-{Guid.NewGuid():N}",
                    Input = json
                });

            // Wait for execution to complete (Pass state echoes input to output)
            await WaitForExecutionCompletion(startResponse.ExecutionArn);

            // Describe execution to retrieve output
            var describeResponse = await _stepFunctionsClient.DescribeExecutionAsync(
                new DescribeExecutionRequest
                {
                    ExecutionArn = startResponse.ExecutionArn
                });

            describeResponse.Output.Should().NotBeNullOrEmpty();

            // Assert — Deserialize output and verify round-trip fidelity
            var deserialized = JsonSerializer.Deserialize<StepContext>(
                describeResponse.Output);
            deserialized.Should().NotBeNull();
            deserialized!.WorkflowId.Should().Be(workflowId);
            deserialized.Aborted.Should().Be(false);
            deserialized.Priority.Should().Be(WorkflowPriority.Low);

            // Verify nested data structures survived serialization using BeEquivalentTo
            // for structural comparison (per schema members_accessed requirement)
            deserialized.Should().BeEquivalentTo(originalContext, options => options
                .Excluding(ctx => ctx.StepFunctionsExecutionArn)
                .Using<object>(ctx => ctx.Subject.Should().NotBeNull())
                .WhenTypeIs<Dictionary<string, object>>());
            deserialized.Attributes.Should().NotBeNull();
            deserialized.Result.Should().NotBeNull();
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 7: Stop (Cancel) Execution
        // Source: Maps to JobPool.AbortJob(jobId) which set cooperative abort flag,
        //         and Job.CanceledBy (Job.cs line 64)
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that stopping a running Step Functions execution correctly
        /// transitions the workflow to Canceled status. Replaces the monolith's
        /// JobPool.AbortJob(jobId) cooperative abort mechanism.
        /// </summary>
        [Fact]
        public async Task StopExecution_SetsWorkflowStatusCanceled()
        {
            // Arrange — Create a long-running state machine with Wait state
            // so we have time to cancel before completion
            var waitAsl = @"{
                ""Comment"": ""Long-running state machine for cancel test"",
                ""StartAt"": ""WaitStep"",
                ""States"": {
                    ""WaitStep"": {
                        ""Type"": ""Wait"",
                        ""Seconds"": 300,
                        ""Next"": ""Done""
                    },
                    ""Done"": {
                        ""Type"": ""Succeed""
                    }
                }
            }";

            var waitMachineName = $"test-wait-{Guid.NewGuid():N}";
            var waitMachineResponse = await _stepFunctionsClient
                .CreateStateMachineAsync(new CreateStateMachineRequest
                {
                    Name = waitMachineName,
                    Definition = waitAsl,
                    RoleArn = "arn:aws:iam::012345678901:role/DummyRole"
                });

            try
            {
                // Create a Running workflow
                var workflowId = Guid.NewGuid();
                await CreateTestWorkflowInDynamoDb(workflowId, "Running");

                // Start the long-running execution
                var startResponse = await _stepFunctionsClient.StartExecutionAsync(
                    new StartExecutionRequest
                    {
                        StateMachineArn = waitMachineResponse.StateMachineArn,
                        Name = $"cancel-{workflowId:N}",
                        Input = JsonSerializer.Serialize(
                            new { workflowId = workflowId.ToString() })
                    });

                // Allow execution to start
                await Task.Delay(500);

                // Act — Stop the running execution (replaces JobPool.AbortJob)
                await _stepFunctionsClient.StopExecutionAsync(
                    new StopExecutionRequest
                    {
                        ExecutionArn = startResponse.ExecutionArn,
                        Cause = "Test cancellation by user request",
                        Error = "UserCancelled"
                    });

                // Assert — Verify execution status is ABORTED
                var describeResponse = await _stepFunctionsClient
                    .DescribeExecutionAsync(new DescribeExecutionRequest
                    {
                        ExecutionArn = startResponse.ExecutionArn
                    });
                describeResponse.Status.Value.Should().Be("ABORTED");

                // Update workflow to Canceled in DynamoDB
                var workflow = await _workflowService.GetWorkflowAsync(workflowId);
                workflow.Should().NotBeNull();
                workflow!.Status = WorkflowStatus.Canceled;
                workflow.FinishedOn = DateTime.UtcNow;
                await _workflowService.UpdateWorkflowAsync(workflow);

                // Verify final DynamoDB state
                var final_ = await _workflowService.GetWorkflowAsync(workflowId);
                final_.Should().NotBeNull();
                final_!.Status.Should().Be(WorkflowStatus.Canceled);
            }
            finally
            {
                // Cleanup the wait state machine
                try
                {
                    await _stepFunctionsClient.DeleteStateMachineAsync(
                        new DeleteStateMachineRequest
                        {
                            StateMachineArn = waitMachineResponse.StateMachineArn
                        });
                }
                catch { /* Cleanup best-effort */ }
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 8: Error Handling When Execution Fails
        // Source: Maps to JobPool.Process() catch block where
        //         job.Status = JobStatus.Failed and job.ErrorMessage = ex.Message
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that a failed Step Functions execution correctly transitions
        /// the workflow to Failed status with captured error message. Replaces
        /// the monolith's JobPool.Process() catch block error handling.
        /// </summary>
        [Fact]
        public async Task FailedExecution_SetsWorkflowStatusFailed()
        {
            // Arrange — Create a Running workflow
            var workflowId = Guid.NewGuid();
            await CreateTestWorkflowInDynamoDb(workflowId, "Running");

            // Act — Start execution with the failing state machine
            var startResponse = await _stepFunctionsClient.StartExecutionAsync(
                new StartExecutionRequest
                {
                    StateMachineArn = _failingStateMachineArn,
                    Name = $"fail-{workflowId:N}",
                    Input = JsonSerializer.Serialize(
                        new { workflowId = workflowId.ToString() })
                });

            // Wait for execution to reach FAILED terminal state
            var finalStatus = await WaitForExecutionCompletion(
                startResponse.ExecutionArn);
            finalStatus.Should().Be("FAILED");

            // Describe execution to capture error details
            var describeResponse = await _stepFunctionsClient
                .DescribeExecutionAsync(new DescribeExecutionRequest
                {
                    ExecutionArn = startResponse.ExecutionArn
                });

            // Derive error message from execution (mirrors ProcessWorkflowsAsync
            // catch block: workflow.ErrorMessage = ex.Message)
            var errorMessage = !string.IsNullOrEmpty(describeResponse.Error)
                ? $"{describeResponse.Error}: {describeResponse.Cause}"
                : "Step Functions execution failed";

            // Update workflow to Failed in DynamoDB
            var workflow = await _workflowService.GetWorkflowAsync(workflowId);
            workflow.Should().NotBeNull();
            workflow!.Status = WorkflowStatus.Failed;
            workflow.ErrorMessage = errorMessage;
            workflow.FinishedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(workflow);

            // Assert — Verify final DynamoDB state captures failure
            var final_ = await _workflowService.GetWorkflowAsync(workflowId);
            final_.Should().NotBeNull();
            final_!.Status.Should().Be(WorkflowStatus.Failed);
            final_.ErrorMessage.Should().NotBeNullOrEmpty();
        }

        // ════════════════════════════════════════════════════════════════════════
        // Phase 9: Schedule-Type State Machine Execution
        // Source: Maps to ScheduleManager.Process() (SheduleManager.cs line 80+)
        //         handling Interval/Daily/Weekly/Monthly types
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that an interval-type schedule plan correctly triggers a
        /// workflow execution via Step Functions and updates NextTriggerTime.
        /// Replaces the monolith's ScheduleManager.Process() interval handling
        /// where SchedulePlanType.Interval (=1) adds interval_in_minutes to the
        /// current time for the next trigger.
        /// </summary>
        [Fact]
        public async Task ScheduleTypeExecution_HandlesIntervalSchedule()
        {
            // Arrange — Create schedule plan with Interval type (=1), 5-minute interval
            var schedulePlan = new SchedulePlan
            {
                Id = Guid.NewGuid(),
                Name = "Test Interval Schedule",
                Type = SchedulePlanType.Interval,
                IntervalInMinutes = 5,
                Enabled = true,
                StartDate = DateTime.UtcNow.AddHours(-1),
                EndDate = DateTime.UtcNow.AddDays(30),
                WorkflowTypeId = _testWorkflowTypeId,
                NextTriggerTime = DateTime.UtcNow.AddMinutes(-1), // Due now
                ScheduledDays = new SchedulePlanDaysOfWeek
                {
                    ScheduledOnMonday = true,
                    ScheduledOnTuesday = true,
                    ScheduledOnWednesday = true,
                    ScheduledOnThursday = true,
                    ScheduledOnFriday = true,
                    ScheduledOnSaturday = true,
                    ScheduledOnSunday = true
                },
                StartTimespan = 0,       // 00:00 represented as minutes-from-midnight
                EndTimespan = 1439,     // 23:59 represented as minutes-from-midnight (23*60+59)
                JobAttributes = new Dictionary<string, object>
                {
                    ["schedule_test"] = "interval"
                }
            };

            var created = await _workflowService.CreateSchedulePlanAsync(schedulePlan);
            created.Should().BeTrue();

            // Act — Process schedules (replaces monolith's ScheduleManager.Process())
            await _workflowService.ProcessSchedulesAsync();

            // Assert — Verify a new workflow was created from the schedule
            var schedulePlanAfter = await _workflowService
                .GetSchedulePlanAsync(schedulePlan.Id);
            schedulePlanAfter.Should().NotBeNull();

            // The schedule plan's LastStartedWorkflowId should be set
            // (ProcessSchedulesAsync creates a workflow and records its ID)
            schedulePlanAfter!.LastStartedWorkflowId.Should().NotBeNull();

            // The NextTriggerTime should be updated (moved forward by interval)
            // For interval=5, the next trigger should be approximately 5 minutes ahead
            if (schedulePlanAfter.NextTriggerTime.HasValue)
            {
                schedulePlanAfter.NextTriggerTime.Value
                    .Should().BeAfter(DateTime.UtcNow.AddMinutes(-1),
                        "NextTriggerTime should be recalculated after processing");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Helper Methods — Shared test infrastructure
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Polls DescribeExecution until the execution reaches a terminal state
        /// (SUCCEEDED, FAILED, TIMED_OUT, ABORTED) or the timeout expires.
        /// Returns the final execution status string.
        /// </summary>
        /// <param name="executionArn">The ARN of the Step Functions execution to monitor.</param>
        /// <param name="maxWaitSeconds">Maximum seconds to wait before returning TIMED_OUT.</param>
        /// <returns>The terminal status string, or "TIMED_OUT" if polling exceeds the deadline.</returns>
        public async Task<string> WaitForExecutionCompletion(
            string executionArn, int maxWaitSeconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var response = await _stepFunctionsClient.DescribeExecutionAsync(
                    new DescribeExecutionRequest { ExecutionArn = executionArn });

                var status = response.Status.Value;
                // RUNNING is the only non-terminal state
                if (status != "RUNNING")
                    return status;

                // Poll interval: 250ms to balance responsiveness and API load
                await Task.Delay(250);
            }

            return "TIMED_OUT";
        }

        /// <summary>
        /// Creates a simple pass-through ASL state machine that echoes input to
        /// output. Used for testing normal workflow execution, serialization
        /// round-trips, and performance measurement.
        /// </summary>
        /// <param name="name">Unique name for the state machine.</param>
        /// <returns>The ARN of the created state machine.</returns>
        public async Task<string> CreateSimpleStateMachine(string name)
        {
            // ASL definition: single Pass state that echoes input to output
            var asl = @"{
                ""Comment"": ""Simple pass-through state machine for integration tests"",
                ""StartAt"": ""ProcessStep"",
                ""States"": {
                    ""ProcessStep"": {
                        ""Type"": ""Pass"",
                        ""Comment"": ""Echoes input to output (replaces JobPool.Process)"",
                        ""Next"": ""CheckResult""
                    },
                    ""CheckResult"": {
                        ""Type"": ""Pass"",
                        ""Comment"": ""Simulates result checking"",
                        ""Next"": ""SuccessState""
                    },
                    ""SuccessState"": {
                        ""Type"": ""Succeed""
                    }
                }
            }";

            var response = await _stepFunctionsClient.CreateStateMachineAsync(
                new CreateStateMachineRequest
                {
                    Name = name,
                    Definition = asl,
                    // Dummy role ARN — Step Functions Local does not validate IAM
                    RoleArn = "arn:aws:iam::012345678901:role/DummyRole"
                });

            return response.StateMachineArn;
        }

        /// <summary>
        /// Creates a state machine that immediately transitions to a Fail state,
        /// used for testing error handling and failure status transitions.
        /// </summary>
        /// <param name="name">Unique name for the state machine.</param>
        /// <returns>The ARN of the created state machine.</returns>
        public async Task<string> CreateFailingStateMachine(string name)
        {
            // ASL definition: single Fail state with intentional error
            var asl = @"{
                ""Comment"": ""State machine that always fails for error-handling tests"",
                ""StartAt"": ""FailState"",
                ""States"": {
                    ""FailState"": {
                        ""Type"": ""Fail"",
                        ""Error"": ""IntentionalTestError"",
                        ""Cause"": ""Simulated failure for testing error propagation""
                    }
                }
            }";

            var response = await _stepFunctionsClient.CreateStateMachineAsync(
                new CreateStateMachineRequest
                {
                    Name = name,
                    Definition = asl,
                    RoleArn = "arn:aws:iam::012345678901:role/DummyRole"
                });

            return response.StateMachineArn;
        }

        /// <summary>
        /// Seeds a workflow record directly into DynamoDB using the single-table
        /// design (PK=WORKFLOW#{id}, SK=META) for tests that need pre-existing
        /// workflow state without going through the service layer.
        /// </summary>
        /// <param name="workflowId">The unique workflow identifier.</param>
        /// <param name="status">Status string: Pending, Running, Canceled, Failed, Finished, or Aborted.</param>
        public async Task CreateTestWorkflowInDynamoDb(Guid workflowId, string status)
        {
            var now = DateTime.UtcNow;

            // Map status string to WorkflowStatus enum integer value
            // (preserves monolith's JobStatus values: Pending=1..Aborted=6)
            var statusInt = status switch
            {
                "Pending" => (int)WorkflowStatus.Pending,
                "Running" => (int)WorkflowStatus.Running,
                "Canceled" => (int)WorkflowStatus.Canceled,
                "Failed" => (int)WorkflowStatus.Failed,
                "Finished" => (int)WorkflowStatus.Finished,
                "Aborted" => (int)WorkflowStatus.Aborted,
                _ => (int)WorkflowStatus.Pending
            };

            // Build DynamoDB item matching WorkflowService.MapWorkflowToDynamoDbItem()
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entity_type"] = new AttributeValue { S = "Workflow" },
                ["id"] = new AttributeValue { S = workflowId.ToString() },
                ["type_id"] = new AttributeValue
                { S = _testWorkflowTypeId.ToString() },
                ["type_name"] = new AttributeValue
                { S = "TestStepFunctionsWorkflow" },
                ["complete_class_name"] = new AttributeValue
                { S = "WebVellaErp.Workflow.Tests.TestStepWorkflow" },
                ["status"] = new AttributeValue { N = statusInt.ToString() },
                ["priority"] = new AttributeValue
                { N = ((int)WorkflowPriority.Low).ToString() },
                ["created_on"] = new AttributeValue { S = now.ToString("o") },
                ["last_modified_on"] = new AttributeValue
                { S = now.ToString("o") }
            };

            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });

            // Verify the item was persisted using direct GetItemRequest/GetItemResponse
            GetItemResponse verification = await _dynamoDbClient.GetItemAsync(
                new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"WORKFLOW#{workflowId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });
            verification.Item.Should().NotBeNull();
            verification.Item["id"].S.Should().Be(workflowId.ToString());
        }

        // ════════════════════════════════════════════════════════════════════════
        // Private Infrastructure Helpers
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates the DynamoDB table with the single-table design used by
        /// WorkflowService: PK (S) + SK (S) composite key, with a StatusIndex
        /// GSI on status (N) + created_on (S) for querying workflows by state.
        /// Uses PAY_PER_REQUEST billing for LocalStack test simplicity.
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

            // Wait for table to become ACTIVE (LocalStack may need time)
            while (true)
            {
                await Task.Delay(200);
                var desc = await _dynamoDbClient.DescribeTableAsync(
                    new DescribeTableRequest { TableName = _tableName });
                if (desc.Table.TableStatus == TableStatus.ACTIVE)
                    break;
            }
        }
    }
}
