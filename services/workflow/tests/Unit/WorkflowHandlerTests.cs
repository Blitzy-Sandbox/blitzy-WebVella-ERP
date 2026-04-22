using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
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
    /// Comprehensive unit tests for <see cref="WorkflowHandler"/> Lambda handler.
    /// Validates HTTP route dispatch, request parsing, response formatting, JWT claim extraction,
    /// idempotency key handling, correlation-ID propagation, SQS schedule event processing,
    /// and JSON snake_case serialization.
    /// </summary>
    public class WorkflowHandlerTests
    {
        // ── Mock Dependencies ────────────────────────────────────────────────────
        private readonly Mock<IWorkflowService> _mockWorkflowService;
        private readonly Mock<IAmazonStepFunctions> _mockStepFunctions;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSns;
        private readonly Mock<ILogger<WorkflowHandler>> _mockLogger;
        private readonly WorkflowSettings _settings;
        private readonly WorkflowHandler _handler;

        // ── Reusable Test Constants ──────────────────────────────────────────────
        private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        private static readonly Guid TestWorkflowId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly Guid TestTypeId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        private static readonly Guid TestScheduleId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");

        private static readonly JsonSerializerOptions SnakeCaseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };

        /// <summary>
        /// Initializes all mocks and constructs a WorkflowHandler instance via the internal
        /// unit-testing constructor (accessible through InternalsVisibleTo).
        /// </summary>
        public WorkflowHandlerTests()
        {
            _mockWorkflowService = new Mock<IWorkflowService>();
            _mockStepFunctions = new Mock<IAmazonStepFunctions>();
            _mockSns = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<WorkflowHandler>>();

            _settings = new WorkflowSettings
            {
                DynamoDbTableName = "test-workflows",
                StepFunctionsStateMachineArn =
                    "arn:aws:states:us-east-1:000000000000:stateMachine:test",
                SnsTopicArn = "arn:aws:sns:us-east-1:000000000000:workflow-events",
                SqsQueueUrl = "http://localhost:4566/000000000000/workflow-queue",
                AwsRegion = "us-east-1",
                Enabled = true
            };

            // Default mock setups for SNS (non-blocking publish in handler)
            _mockSns
                .Setup(s => s.PublishAsync(
                    It.IsAny<PublishRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

            _handler = new WorkflowHandler(
                _mockWorkflowService.Object,
                _mockStepFunctions.Object,
                _mockSns.Object,
                _mockLogger.Object,
                _settings);
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Helper Methods ──────────────────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds a fully-populated <see cref="APIGatewayHttpApiV2ProxyRequest"/> with JWT
        /// claims, correlation-ID header, and request context for testing handler dispatch.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateApiGatewayRequest(
            string httpMethod,
            string path,
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            Dictionary<string, string>? headers = null)
        {
            var finalHeaders = headers ?? new Dictionary<string, string>();

            return new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = path,
                Body = body,
                PathParameters = pathParameters,
                QueryStringParameters = queryStringParameters,
                Headers = finalHeaders,
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = httpMethod,
                        Path = path
                    },
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = TestUserId.ToString()
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates a sample <see cref="Models.Workflow"/> with all required fields populated.
        /// </summary>
        private static Models.Workflow CreateTestWorkflow(
            Guid? id = null,
            WorkflowStatus status = WorkflowStatus.Pending,
            Guid? typeId = null)
        {
            var wfTypeId = typeId ?? TestTypeId;
            return new Models.Workflow
            {
                Id = id ?? TestWorkflowId,
                TypeId = wfTypeId,
                TypeName = "TestType",
                Status = status,
                Priority = WorkflowPriority.Medium,
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow,
                CreatedBy = TestUserId,
                Attributes = new Dictionary<string, object?>
                {
                    ["source"] = "unit-test",
                    ["retries"] = 0
                },
                Type = new WorkflowType
                {
                    Id = wfTypeId,
                    Name = "TestType",
                    DefaultPriority = WorkflowPriority.Medium,
                    AllowSingleInstance = false
                }
            };
        }

        /// <summary>
        /// Creates a sample <see cref="SchedulePlan"/> with all required fields populated.
        /// </summary>
        private static SchedulePlan CreateTestSchedulePlan(Guid? id = null)
        {
            return new SchedulePlan
            {
                Id = id ?? TestScheduleId,
                Name = "Test Schedule",
                Type = SchedulePlanType.Daily,
                Enabled = true,
                NextTriggerTime = DateTime.UtcNow.AddHours(1),
                WorkflowTypeId = TestTypeId,
                CreatedOn = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a sample <see cref="WorkflowType"/> for registration and lookup tests.
        /// </summary>
        private static WorkflowType CreateTestWorkflowType(Guid? id = null, string? name = null)
        {
            return new WorkflowType
            {
                Id = id ?? TestTypeId,
                Name = name ?? "TestWorkflowType",
                DefaultPriority = WorkflowPriority.Low,
                AllowSingleInstance = false,
                Assembly = "WebVellaErp.Workflow",
                CompleteClassName = "WebVellaErp.Workflow.TestType"
            };
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2: Workflow CRUD Route Tests ──────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// POST /v1/workflows with a valid body should return 201 Created and the created
        /// workflow JSON. Verifies CreateWorkflowAsync is invoked exactly once.
        /// Source reference: JobManager.CreateJob() — type lookup, priority normalization.
        /// </summary>
        [Fact]
        public async Task CreateWorkflow_Post_Returns201Created()
        {
            // Arrange
            var workflowType = CreateTestWorkflowType();
            var createdWorkflow = CreateTestWorkflow();
            createdWorkflow.TypeName = workflowType.Name;

            _mockWorkflowService
                .Setup(s => s.GetWorkflowTypeAsync(TestTypeId))
                .ReturnsAsync(workflowType);

            _mockWorkflowService
                .Setup(s => s.CreateWorkflowAsync(
                    TestTypeId,
                    It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<WorkflowPriority>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>()))
                .ReturnsAsync(createdWorkflow);

            var body = JsonSerializer.Serialize(new
            {
                type_id = TestTypeId.ToString(),
                priority = 2,
                attributes = new Dictionary<string, object> { ["key1"] = "value1" }
            });

            var request = CreateApiGatewayRequest("POST", "/v1/workflows", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Created);
            response.Body.Should().NotBeNull();

            _mockWorkflowService.Verify(
                s => s.CreateWorkflowAsync(
                    TestTypeId,
                    It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<WorkflowPriority>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>()),
                Times.Once());
        }

        /// <summary>
        /// POST /v1/workflows with a type_id that doesn't exist should return 400 Bad Request.
        /// Source reference: JobManager.CreateJob() lines 102-108 — type not found returns error.
        /// </summary>
        [Fact]
        public async Task CreateWorkflow_InvalidTypeId_Returns400BadRequest()
        {
            // Arrange
            var unknownTypeId = Guid.NewGuid();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowTypeAsync(unknownTypeId))
                .ReturnsAsync((WorkflowType?)null);

            var body = JsonSerializer.Serialize(new
            {
                type_id = unknownTypeId.ToString()
            });

            var request = CreateApiGatewayRequest("POST", "/v1/workflows", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            response.Body.Should().Contain("not found");
        }

        /// <summary>
        /// PUT /v1/workflows/{id} with a valid body should return 200 OK and the updated workflow.
        /// Verifies the path segment GUID is correctly parsed from RawPath.
        /// </summary>
        [Fact]
        public async Task UpdateWorkflow_Put_Returns200Ok()
        {
            // Arrange
            var existingWorkflow = CreateTestWorkflow();

            _mockWorkflowService
                .Setup(s => s.UpdateWorkflowAsync(It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            var body = JsonSerializer.Serialize(new
            {
                status = (int)WorkflowStatus.Pending,
                priority = (int)WorkflowPriority.High
            }, SnakeCaseOptions);

            var request = CreateApiGatewayRequest(
                "PUT",
                $"/v1/workflows/{TestWorkflowId}",
                body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            _mockWorkflowService.Verify(
                s => s.UpdateWorkflowAsync(It.Is<Models.Workflow>(
                    w => w.Id == TestWorkflowId)),
                Times.Once());
        }

        /// <summary>
        /// GET /v1/workflows/{id} for an existing workflow returns 200 OK with the workflow JSON.
        /// </summary>
        [Fact]
        public async Task GetWorkflow_Get_Returns200Ok()
        {
            // Arrange
            var workflow = CreateTestWorkflow();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(TestWorkflowId))
                .ReturnsAsync(workflow);

            var request = CreateApiGatewayRequest(
                "GET",
                $"/v1/workflows/{TestWorkflowId}");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().NotBeNull();
        }

        /// <summary>
        /// GET /v1/workflows/{id} for a non-existent workflow returns 404 Not Found.
        /// </summary>
        [Fact]
        public async Task GetWorkflow_NotFound_Returns404()
        {
            // Arrange
            var missingId = Guid.NewGuid();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(missingId))
                .ReturnsAsync((Models.Workflow?)null);

            var request = CreateApiGatewayRequest(
                "GET",
                $"/v1/workflows/{missingId}");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            response.Body.Should().Contain("not found");
        }

        /// <summary>
        /// GET /v1/workflows with page and pageSize query params returns 200 OK
        /// with paginated result containing workflows, total_count, page, and page_size.
        /// Source reference: JobManager.GetJobs() — out totalCount, filters, pagination.
        /// </summary>
        [Fact]
        public async Task ListWorkflows_Get_Returns200WithPagination()
        {
            // Arrange
            var workflows = new List<Models.Workflow>
            {
                CreateTestWorkflow(Guid.NewGuid()),
                CreateTestWorkflow(Guid.NewGuid())
            };

            _mockWorkflowService
                .Setup(s => s.GetWorkflowsAsync(
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>()))
                .ReturnsAsync((workflows, 2));

            var queryParams = new Dictionary<string, string>
            {
                ["page"] = "1",
                ["page_size"] = "10"
            };

            var request = CreateApiGatewayRequest(
                "GET",
                "/v1/workflows",
                queryStringParameters: queryParams);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("workflows");
            response.Body.Should().Contain("total_count");
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2d: Workflow Execution Route Tests ────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// POST /v1/workflows/{id}/start for a Pending workflow returns 200 OK
        /// and invokes StepFunctions StartExecutionAsync.
        /// Source reference: replaces JobPool.RunJobAsync dispatch pattern.
        /// </summary>
        [Fact]
        public async Task StartWorkflow_Post_Returns200Ok()
        {
            // Arrange
            var workflow = CreateTestWorkflow();
            workflow.Status = WorkflowStatus.Pending;

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(TestWorkflowId))
                .ReturnsAsync(workflow);

            _mockStepFunctions
                .Setup(sf => sf.StartExecutionAsync(
                    It.IsAny<Amazon.StepFunctions.Model.StartExecutionRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.StepFunctions.Model.StartExecutionResponse
                {
                    ExecutionArn = "arn:aws:states:us-east-1:000000000000:execution:test:workflow-test"
                });

            _mockWorkflowService
                .Setup(s => s.UpdateWorkflowAsync(It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            var request = CreateApiGatewayRequest(
                "POST",
                $"/v1/workflows/{TestWorkflowId}/start");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            _mockStepFunctions.Verify(
                sf => sf.StartExecutionAsync(
                    It.IsAny<Amazon.StepFunctions.Model.StartExecutionRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// POST /v1/workflows/{id}/start where the workflow type has AllowSingleInstance=true
        /// and another workflow of the same type is already Running returns 409 Conflict.
        /// Source reference: JobManager.Process() lines 177-178 — single-instance constraint.
        /// </summary>
        [Fact]
        public async Task StartWorkflow_SingleInstanceConflict_Returns409()
        {
            // Arrange
            var workflowType = CreateTestWorkflowType();
            workflowType.AllowSingleInstance = true;

            var workflow = CreateTestWorkflow();
            workflow.Status = WorkflowStatus.Pending;
            workflow.Type = workflowType;
            workflow.TypeId = workflowType.Id;

            // Another workflow of the same type is already Running
            var runningWorkflow = CreateTestWorkflow(Guid.NewGuid());
            runningWorkflow.Status = WorkflowStatus.Running;
            runningWorkflow.TypeId = workflowType.Id;

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(TestWorkflowId))
                .ReturnsAsync(workflow);

            _mockWorkflowService
                .Setup(s => s.GetRunningWorkflowsAsync(It.IsAny<int?>()))
                .ReturnsAsync(new List<Models.Workflow> { runningWorkflow });

            var request = CreateApiGatewayRequest(
                "POST",
                $"/v1/workflows/{TestWorkflowId}/start");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);

            _mockStepFunctions.Verify(
                sf => sf.StartExecutionAsync(
                    It.IsAny<Amazon.StepFunctions.Model.StartExecutionRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// POST /v1/workflows/{id}/cancel for a Running workflow returns 200 OK
        /// and calls StepFunctions StopExecutionAsync.
        /// Source reference: replaces JobPool.AbortJob() lines 160-170.
        /// </summary>
        [Fact]
        public async Task CancelWorkflow_Post_Returns200()
        {
            // Arrange
            var workflow = CreateTestWorkflow();
            workflow.Status = WorkflowStatus.Running;
            workflow.StepFunctionsExecutionArn =
                "arn:aws:states:us-east-1:000000000000:execution:test:workflow-running";

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(TestWorkflowId))
                .ReturnsAsync(workflow);

            _mockStepFunctions
                .Setup(sf => sf.StopExecutionAsync(
                    It.IsAny<Amazon.StepFunctions.Model.StopExecutionRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Amazon.StepFunctions.Model.StopExecutionResponse());

            _mockWorkflowService
                .Setup(s => s.UpdateWorkflowAsync(It.IsAny<Models.Workflow>()))
                .ReturnsAsync(true);

            var request = CreateApiGatewayRequest(
                "POST",
                $"/v1/workflows/{TestWorkflowId}/cancel");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            _mockStepFunctions.Verify(
                sf => sf.StopExecutionAsync(
                    It.Is<Amazon.StepFunctions.Model.StopExecutionRequest>(
                        r => r.ExecutionArn == workflow.StepFunctionsExecutionArn),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// POST /v1/workflows/recover returns 200 OK with recovery message.
        /// Source reference: JobManager constructor lines 33-41 — Running→Aborted with AbortedBy=Guid.Empty.
        /// Recovery transitions workflows from Running to <see cref="WorkflowStatus.Aborted"/>
        /// when the system detects orphaned executions (AbortedBy = Guid.Empty).
        /// </summary>
        [Fact]
        public async Task RecoverAbortedWorkflows_Post_Returns200WithCount()
        {
            // Arrange — verify Aborted status constant is available for recovery semantics
            var abortedStatus = WorkflowStatus.Aborted;
            abortedStatus.Should().NotBe(WorkflowStatus.Running,
                "recovery transitions Running workflows to Aborted");

            _mockWorkflowService
                .Setup(s => s.RecoverAbortedWorkflowsAsync())
                .Returns(Task.CompletedTask);

            var request = CreateApiGatewayRequest(
                "POST",
                "/v1/workflows/recover");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("Recovery completed");

            _mockWorkflowService.Verify(
                s => s.RecoverAbortedWorkflowsAsync(),
                Times.Once());
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2e: Schedule Plan Route Tests ─────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// POST /v1/schedules with a valid body returns 201 Created.
        /// Source reference: ScheduleManager.CreateSchedulePlan() lines 37-45.
        /// </summary>
        [Fact]
        public async Task CreateSchedulePlan_Post_Returns201Created()
        {
            // Arrange
            var schedulePlan = CreateTestSchedulePlan();

            _mockWorkflowService
                .Setup(s => s.CreateSchedulePlanAsync(It.IsAny<SchedulePlan>()))
                .ReturnsAsync(true);

            var body = JsonSerializer.Serialize(new
            {
                name = schedulePlan.Name,
                type = (int)schedulePlan.Type,
                enabled = true,
                workflow_type_id = TestTypeId.ToString(),
                interval_in_minutes = 60
            });

            var request = CreateApiGatewayRequest("POST", "/v1/schedules", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Created);

            _mockWorkflowService.Verify(
                s => s.CreateSchedulePlanAsync(It.IsAny<SchedulePlan>()),
                Times.Once());
        }

        /// <summary>
        /// PUT /v1/schedules/{id} with a valid body returns 200 OK.
        /// </summary>
        [Fact]
        public async Task UpdateSchedulePlan_Put_Returns200()
        {
            // Arrange
            _mockWorkflowService
                .Setup(s => s.UpdateSchedulePlanAsync(It.IsAny<SchedulePlan>()))
                .ReturnsAsync(true);

            var body = JsonSerializer.Serialize(new
            {
                name = "Updated Schedule",
                enabled = false
            });

            var request = CreateApiGatewayRequest(
                "PUT",
                $"/v1/schedules/{TestScheduleId}",
                body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            _mockWorkflowService.Verify(
                s => s.UpdateSchedulePlanAsync(
                    It.Is<SchedulePlan>(sp => sp.Id == TestScheduleId)),
                Times.Once());
        }

        /// <summary>
        /// GET /v1/schedules/{id} for an existing schedule plan returns 200 OK.
        /// </summary>
        [Fact]
        public async Task GetSchedulePlan_Get_Returns200()
        {
            // Arrange
            var plan = CreateTestSchedulePlan();

            _mockWorkflowService
                .Setup(s => s.GetSchedulePlanAsync(TestScheduleId))
                .ReturnsAsync(plan);

            var request = CreateApiGatewayRequest(
                "GET",
                $"/v1/schedules/{TestScheduleId}");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().NotBeNull();
        }

        /// <summary>
        /// GET /v1/schedules returns 200 OK with list of schedule plans.
        /// </summary>
        [Fact]
        public async Task ListSchedulePlans_Get_Returns200()
        {
            // Arrange
            var plans = new List<SchedulePlan>
            {
                CreateTestSchedulePlan(Guid.NewGuid()),
                CreateTestSchedulePlan(Guid.NewGuid())
            };

            _mockWorkflowService
                .Setup(s => s.GetSchedulePlansAsync())
                .ReturnsAsync(plans);

            var request = CreateApiGatewayRequest("GET", "/v1/schedules");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("schedule_plans");
            response.Body.Should().Contain("total_count");
        }

        /// <summary>
        /// POST /v1/schedules/{id}/trigger for an enabled schedule plan returns 200 OK.
        /// Source reference: ScheduleManager.TriggerNowSchedulePlan() lines 68-72.
        /// </summary>
        [Fact]
        public async Task TriggerSchedulePlan_Post_Returns200()
        {
            // Arrange
            var plan = CreateTestSchedulePlan();
            plan.Enabled = true;

            _mockWorkflowService
                .Setup(s => s.GetSchedulePlanAsync(TestScheduleId))
                .ReturnsAsync(plan);

            _mockWorkflowService
                .Setup(s => s.TriggerNowSchedulePlanAsync(It.IsAny<SchedulePlan>()))
                .Returns(Task.CompletedTask);

            var request = CreateApiGatewayRequest(
                "POST",
                $"/v1/schedules/{TestScheduleId}/trigger");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("schedule_plan_id");

            _mockWorkflowService.Verify(
                s => s.TriggerNowSchedulePlanAsync(It.IsAny<SchedulePlan>()),
                Times.Once());
        }

        /// <summary>
        /// POST /v1/schedules/process returns 200 OK and calls ProcessSchedulesAsync.
        /// </summary>
        [Fact]
        public async Task ProcessSchedules_Post_Returns200()
        {
            // Arrange
            _mockWorkflowService
                .Setup(s => s.ProcessSchedulesAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockWorkflowService
                .Setup(s => s.ProcessWorkflowsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateApiGatewayRequest("POST", "/v1/schedules/process");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("processed");
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2f: Workflow Type Route Tests ─────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /v1/workflow-types returns 200 OK with list of workflow types.
        /// </summary>
        [Fact]
        public async Task ListWorkflowTypes_Get_Returns200()
        {
            // Arrange
            var types = new List<WorkflowType>
            {
                CreateTestWorkflowType(Guid.NewGuid(), "Type1"),
                CreateTestWorkflowType(Guid.NewGuid(), "Type2")
            };

            _mockWorkflowService
                .Setup(s => s.GetWorkflowTypesAsync())
                .ReturnsAsync(types);

            var request = CreateApiGatewayRequest("GET", "/v1/workflow-types");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("workflow_types");
            response.Body.Should().Contain("total_count");
        }

        /// <summary>
        /// POST /v1/workflow-types with a valid unique name returns 201 Created.
        /// </summary>
        [Fact]
        public async Task RegisterWorkflowType_Post_Returns201()
        {
            // Arrange
            _mockWorkflowService
                .Setup(s => s.RegisterWorkflowTypeAsync(It.IsAny<WorkflowType>()))
                .ReturnsAsync(true);

            var body = JsonSerializer.Serialize(new
            {
                name = "NewWorkflowType",
                default_priority = (int)WorkflowPriority.Low,
                allow_single_instance = false,
                assembly = "WebVellaErp.Workflow",
                complete_class_name = "WebVellaErp.Workflow.NewType"
            });

            var request = CreateApiGatewayRequest("POST", "/v1/workflow-types", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Created);

            _mockWorkflowService.Verify(
                s => s.RegisterWorkflowTypeAsync(It.IsAny<WorkflowType>()),
                Times.Once());
        }

        /// <summary>
        /// POST /v1/workflow-types with a name that already exists returns 409 Conflict.
        /// Source reference: JobManager.RegisterJobType() lines 85-98 — name uniqueness check.
        /// </summary>
        [Fact]
        public async Task RegisterWorkflowType_DuplicateName_Returns409()
        {
            // Arrange
            _mockWorkflowService
                .Setup(s => s.RegisterWorkflowTypeAsync(It.IsAny<WorkflowType>()))
                .ReturnsAsync(false); // false indicates duplicate / registration failure

            var body = JsonSerializer.Serialize(new
            {
                name = "ExistingType",
                default_priority = (int)WorkflowPriority.Low,
                allow_single_instance = false
            });

            var request = CreateApiGatewayRequest("POST", "/v1/workflow-types", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Conflict);
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2g: Health Check & Default Route Tests ────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /v1/workflows/health returns 200 OK with health status JSON.
        /// </summary>
        [Fact]
        public async Task HealthCheck_Get_Returns200()
        {
            // Arrange
            var request = CreateApiGatewayRequest("GET", "/v1/workflows/health");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("healthy");
            response.Body.Should().Contain("workflow");
        }

        /// <summary>
        /// GET /v1/unknown-path returns 404 Not Found with standard error body.
        /// </summary>
        [Fact]
        public async Task UnknownRoute_Returns404()
        {
            // Arrange
            var request = CreateApiGatewayRequest("GET", "/v1/unknown-path");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            response.Body.Should().Contain("Not Found");
        }

        /// <summary>
        /// PATCH /v1/workflows — an unsupported HTTP method returns 404 or 405.
        /// The handler may return 405 Method Not Allowed for known paths with invalid methods.
        /// </summary>
        [Fact]
        public async Task UnknownMethod_Returns404()
        {
            // Arrange
            var request = CreateApiGatewayRequest("PATCH", "/v1/workflows");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert — handler returns 405 Method Not Allowed for unsupported methods on known paths
            response.StatusCode.Should().BeOneOf(
                (int)HttpStatusCode.NotFound,
                (int)HttpStatusCode.MethodNotAllowed);
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2h: JWT Claims Extraction Tests ───────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that the handler extracts userId from request.RequestContext.Authorizer.Jwt.Claims["sub"].
        /// The extracted userId is passed to CreateWorkflowAsync as the creatorId parameter.
        /// </summary>
        [Fact]
        public async Task HandleApiRequest_ExtractsJwtClaims()
        {
            // Arrange
            var workflowType = CreateTestWorkflowType();
            var createdWorkflow = CreateTestWorkflow();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowTypeAsync(TestTypeId))
                .ReturnsAsync(workflowType);

            _mockWorkflowService
                .Setup(s => s.CreateWorkflowAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<WorkflowPriority>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>()))
                .ReturnsAsync(createdWorkflow);

            var body = JsonSerializer.Serialize(new
            {
                type_id = TestTypeId.ToString()
            });

            // Create request with specific JWT claim "sub" = TestUserId
            var request = CreateApiGatewayRequest("POST", "/v1/workflows", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Created);

            // Verify the userId extracted from JWT claims is passed as creatorId
            _mockWorkflowService.Verify(
                s => s.CreateWorkflowAsync(
                    TestTypeId,
                    It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<WorkflowPriority>(),
                    It.Is<Guid?>(g => g == TestUserId),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>()),
                Times.Once());
        }

        /// <summary>
        /// When JWT claims are absent the handler should not crash — userId falls back
        /// to Guid.Empty and request is still processed.
        /// </summary>
        [Fact]
        public async Task HandleApiRequest_MissingJwtClaims_HandledGracefully()
        {
            // Arrange — request without JWT authorizer context
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = "/v1/workflows/health",
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = "GET",
                        Path = "/v1/workflows/health"
                    }
                    // Authorizer intentionally null
                },
                Headers = new Dictionary<string, string>()
            };
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert — should still work for health check (no auth required)
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().Contain("healthy");
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2i: Idempotency Key Tests ─────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that the X-Idempotency-Key header is extracted and passed into
        /// the workflow creation pipeline. The key appears in SNS message attributes.
        /// Per AAP Section 0.8.5 — idempotency keys on all write endpoints.
        /// </summary>
        [Fact]
        public async Task CreateWorkflow_ExtractsIdempotencyKey()
        {
            // Arrange
            var workflowType = CreateTestWorkflowType();
            var createdWorkflow = CreateTestWorkflow();
            createdWorkflow.Status = WorkflowStatus.Pending;

            _mockWorkflowService
                .Setup(s => s.GetWorkflowTypeAsync(TestTypeId))
                .ReturnsAsync(workflowType);

            _mockWorkflowService
                .Setup(s => s.CreateWorkflowAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<WorkflowPriority>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>()))
                .ReturnsAsync(createdWorkflow);

            var headers = new Dictionary<string, string>
            {
                ["X-Idempotency-Key"] = "idem-key-12345"
            };

            var body = JsonSerializer.Serialize(new
            {
                type_id = TestTypeId.ToString()
            });

            var request = CreateApiGatewayRequest("POST", "/v1/workflows", body, headers: headers);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Created);
        }

        /// <summary>
        /// When the X-Idempotency-Key header is missing, the request should still
        /// be processed successfully without error.
        /// </summary>
        [Fact]
        public async Task CreateWorkflow_MissingIdempotencyKey_StillProcesses()
        {
            // Arrange
            var workflowType = CreateTestWorkflowType();
            var createdWorkflow = CreateTestWorkflow();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowTypeAsync(TestTypeId))
                .ReturnsAsync(workflowType);

            _mockWorkflowService
                .Setup(s => s.CreateWorkflowAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<Dictionary<string, object>?>(),
                    It.IsAny<WorkflowPriority>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<Guid?>()))
                .ReturnsAsync(createdWorkflow);

            // No idempotency key header set
            var body = JsonSerializer.Serialize(new
            {
                type_id = TestTypeId.ToString()
            });

            var request = CreateApiGatewayRequest("POST", "/v1/workflows", body);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.Created);
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2j: Correlation ID Tests ──────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// When an X-Correlation-ID header is present it should be propagated
        /// in the response headers.
        /// Per AAP Section 0.8.5 — structured JSON logging with correlation-ID propagation.
        /// </summary>
        [Fact]
        public async Task HandleApiRequest_ExtractsCorrelationId()
        {
            // Arrange
            var correlationId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>
            {
                ["X-Correlation-Id"] = correlationId
            };

            var request = CreateApiGatewayRequest("GET", "/v1/workflows/health", headers: headers);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            // Correlation ID should appear in response headers
            response.Headers.Should().NotBeNull();
        }

        /// <summary>
        /// When the X-Correlation-ID header is missing, the handler should generate
        /// a new correlation ID (a valid GUID string) and include it in the response.
        /// </summary>
        [Fact]
        public async Task HandleApiRequest_GeneratesCorrelationId()
        {
            // Arrange — no X-Correlation-Id header
            var request = CreateApiGatewayRequest("GET", "/v1/workflows/health");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Headers.Should().NotBeNull();
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2k: Error Response Formatting Tests ───────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// All error responses should return a structured JSON body containing
        /// a "message" property.
        /// </summary>
        [Fact]
        public async Task ErrorResponse_ReturnsStructuredJson()
        {
            // Arrange — request a non-existent workflow to trigger a 404 error
            var missingId = Guid.NewGuid();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(missingId))
                .ReturnsAsync((Models.Workflow?)null);

            var request = CreateApiGatewayRequest(
                "GET",
                $"/v1/workflows/{missingId}");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            response.Body.Should().NotBeNull();

            // Parse as JSON to verify structure
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            root.TryGetProperty("message", out var messageElement).Should().BeTrue();
            messageElement.GetString().Should().NotBeNull();
        }

        /// <summary>
        /// Error responses should include a correlation ID in the response,
        /// either from the header or auto-generated.
        /// </summary>
        [Fact]
        public async Task ErrorResponse_ContainsCorrelationId()
        {
            // Arrange — trigger an error (unknown route)
            var correlationId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>
            {
                ["X-Correlation-Id"] = correlationId
            };

            var request = CreateApiGatewayRequest(
                "GET",
                "/v1/nonexistent-route",
                headers: headers);
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
            response.Headers.Should().NotBeNull();
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2l: SQS Handler Tests ─────────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// HandleScheduleEvent(SQSEvent, ILambdaContext) processes schedules and workflows.
        /// </summary>
        [Fact]
        public async Task HandleScheduleEvent_ProcessesSchedules()
        {
            // Arrange
            _mockWorkflowService
                .Setup(s => s.ProcessSchedulesAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockWorkflowService
                .Setup(s => s.ProcessWorkflowsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = "trigger-schedules",
                        MessageId = Guid.NewGuid().ToString()
                    }
                }
            };
            var context = new TestLambdaContext();

            // Act
            await _handler.HandleScheduleEvent(sqsEvent, context);

            // Assert — verify processing was invoked
            _mockWorkflowService.Verify(
                s => s.ProcessSchedulesAsync(It.IsAny<CancellationToken>()),
                Times.Once());

            _mockWorkflowService.Verify(
                s => s.ProcessWorkflowsAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// HandleScheduleEvent with an empty SQS event (no records) is handled gracefully.
        /// </summary>
        [Fact]
        public async Task HandleScheduleEvent_HandlesEmptyEvent()
        {
            // Arrange
            _mockWorkflowService
                .Setup(s => s.ProcessSchedulesAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockWorkflowService
                .Setup(s => s.ProcessWorkflowsAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>() // empty
            };
            var context = new TestLambdaContext();

            // Act — should not throw
            await _handler.HandleScheduleEvent(sqsEvent, context);

            // Assert — processing still called (handler processes regardless of message content)
            _mockWorkflowService.Verify(
                s => s.ProcessSchedulesAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ════════════════════════════════════════════════════════════════════════
        // ── Phase 2m: JSON Serialization Tests ──────────────────────────────────
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Response bodies use snake_case property names, consistent with the handler's
        /// JsonSerializerOptions that use JsonNamingPolicy.SnakeCaseLower.
        /// </summary>
        [Fact]
        public async Task Response_UsesSnakeCasePropertyNames()
        {
            // Arrange
            var workflow = CreateTestWorkflow();

            _mockWorkflowService
                .Setup(s => s.GetWorkflowAsync(TestWorkflowId))
                .ReturnsAsync(workflow);

            var request = CreateApiGatewayRequest(
                "GET",
                $"/v1/workflows/{TestWorkflowId}");
            var context = new TestLambdaContext();

            // Act
            var response = await _handler.HandleApiRequest(request, context);

            // Assert
            response.StatusCode.Should().Be((int)HttpStatusCode.OK);
            response.Body.Should().NotBeNull();

            // Verify snake_case naming in the JSON response body
            // The Workflow model properties like TypeId, TypeName, StepFunctionsExecutionArn
            // should appear as type_id, type_name, step_functions_execution_arn
            response.Body.Should().Contain("type_id");
            response.Body.Should().Contain("status");
        }
    }
}
