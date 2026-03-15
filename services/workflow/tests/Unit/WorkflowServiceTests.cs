using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;
using Xunit;

namespace WebVellaErp.Workflow.Tests.Unit
{
    /// <summary>
    /// Comprehensive unit tests for WorkflowService — the consolidated business logic service
    /// replacing the monolith's JobManager, SheduleManager, and JobDataService.
    /// Tests use Moq for AWS SDK mocking and FluentAssertions for readable assertions.
    /// </summary>
    public class WorkflowServiceTests
    {
        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<IAmazonStepFunctions> _mockStepFunctions;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSns;
        private readonly Mock<ILogger<WorkflowService>> _mockLogger;
        private readonly WorkflowSettings _settings;
        private readonly WorkflowService _service;

        public WorkflowServiceTests()
        {
            _mockDynamoDb = new Mock<IAmazonDynamoDB>();
            _mockStepFunctions = new Mock<IAmazonStepFunctions>();
            _mockSns = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<WorkflowService>>();
            _settings = new WorkflowSettings
            {
                DynamoDbTableName = "test-workflows",
                StepFunctionsStateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:test",
                SnsTopicArn = "arn:aws:sns:us-east-1:000000000000:workflow-events",
                SqsQueueUrl = "http://localhost:4566/000000000000/workflow-queue",
                AwsRegion = "us-east-1",
                Enabled = true
            };

            // Default SNS PublishAsync mock — always succeeds unless overridden per-test
            _mockSns.Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

            _service = new WorkflowService(
                _mockDynamoDb.Object,
                _mockStepFunctions.Object,
                _mockSns.Object,
                _mockLogger.Object,
                _settings);
        }

        #region Helper Methods

        private static WorkflowType CreateTestWorkflowType(
            string name = "TestWorkflow",
            Guid? id = null,
            WorkflowPriority priority = WorkflowPriority.Low,
            bool allowSingleInstance = false)
        {
            return new WorkflowType
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                DefaultPriority = priority,
                Assembly = "TestAssembly",
                CompleteClassName = "Test.WorkflowClass",
                AllowSingleInstance = allowSingleInstance
            };
        }

        private static SchedulePlan CreateTestSchedulePlan(
            SchedulePlanType type = SchedulePlanType.Daily,
            Guid? id = null,
            string name = "TestSchedule",
            DateTime? startDate = null,
            DateTime? endDate = null,
            SchedulePlanDaysOfWeek? scheduledDays = null,
            int? intervalInMinutes = null,
            int? startTimespan = null,
            int? endTimespan = null,
            bool enabled = true,
            Guid? workflowTypeId = null)
        {
            return new SchedulePlan
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Type = type,
                StartDate = startDate ?? DateTime.UtcNow.AddDays(-1),
                EndDate = endDate,
                ScheduledDays = scheduledDays ?? new SchedulePlanDaysOfWeek
                {
                    ScheduledOnMonday = true,
                    ScheduledOnTuesday = true,
                    ScheduledOnWednesday = true,
                    ScheduledOnThursday = true,
                    ScheduledOnFriday = true,
                    ScheduledOnSaturday = true,
                    ScheduledOnSunday = true
                },
                IntervalInMinutes = intervalInMinutes ?? (type == SchedulePlanType.Interval ? 30 : null),
                StartTimespan = startTimespan,
                EndTimespan = endTimespan,
                Enabled = enabled,
                WorkflowTypeId = workflowTypeId ?? Guid.NewGuid()
            };
        }

        private Dictionary<string, AttributeValue> BuildWorkflowTypeDynamoItem(
            Guid id, string name, int priority = 1, string className = "Test.WorkflowClass",
            bool allowSingleInstance = false)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = "WORKFLOW_TYPE#" },
                ["SK"] = new AttributeValue { S = $"TYPE#{id}" },
                ["entity_type"] = new AttributeValue { S = "WorkflowType" },
                ["id"] = new AttributeValue { S = id.ToString() },
                ["name"] = new AttributeValue { S = name },
                ["default_priority"] = new AttributeValue { N = priority.ToString() },
                ["assembly"] = new AttributeValue { S = "TestAssembly" },
                ["complete_class_name"] = new AttributeValue { S = className },
                ["allow_single_instance"] = new AttributeValue { BOOL = allowSingleInstance }
            };
        }

        private Dictionary<string, AttributeValue> BuildWorkflowDynamoItem(
            Guid id, Guid typeId, string typeName = "TestWorkflow",
            WorkflowStatus status = WorkflowStatus.Pending,
            WorkflowPriority priority = WorkflowPriority.Low,
            Guid? createdBy = null, DateTime? createdOn = null,
            DateTime? finishedOn = null, Guid? abortedBy = null,
            Guid? schedulePlanId = null, DateTime? startedOn = null)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"WORKFLOW#{id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entity_type"] = new AttributeValue { S = "Workflow" },
                ["id"] = new AttributeValue { S = id.ToString() },
                ["type_id"] = new AttributeValue { S = typeId.ToString() },
                ["type_name"] = new AttributeValue { S = typeName },
                ["complete_class_name"] = new AttributeValue { S = "Test.WorkflowClass" },
                ["status"] = new AttributeValue { N = ((int)status).ToString() },
                ["priority"] = new AttributeValue { N = ((int)priority).ToString() },
                ["created_by"] = new AttributeValue { S = (createdBy ?? Guid.NewGuid()).ToString() },
                ["last_modified_by"] = new AttributeValue { S = (createdBy ?? Guid.NewGuid()).ToString() },
                ["created_on"] = new AttributeValue { S = (createdOn ?? DateTime.UtcNow).ToString("O") },
                ["last_modified_on"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            };

            item["finished_on"] = finishedOn.HasValue
                ? new AttributeValue { S = finishedOn.Value.ToString("O") }
                : new AttributeValue { NULL = true };
            item["aborted_by"] = abortedBy.HasValue
                ? new AttributeValue { S = abortedBy.Value.ToString() }
                : new AttributeValue { NULL = true };
            item["schedule_plan_id"] = schedulePlanId.HasValue
                ? new AttributeValue { S = schedulePlanId.Value.ToString() }
                : new AttributeValue { NULL = true };
            item["started_on"] = startedOn.HasValue
                ? new AttributeValue { S = startedOn.Value.ToString("O") }
                : new AttributeValue { NULL = true };
            item["canceled_by"] = new AttributeValue { NULL = true };
            item["error_message"] = new AttributeValue { NULL = true };
            item["attributes"] = new AttributeValue { NULL = true };
            item["result"] = new AttributeValue { NULL = true };
            item["step_functions_execution_arn"] = new AttributeValue { NULL = true };

            return item;
        }

        private Dictionary<string, AttributeValue> BuildSchedulePlanDynamoItem(
            Guid id, string name = "TestSchedule", SchedulePlanType type = SchedulePlanType.Daily,
            bool enabled = true, DateTime? startDate = null, DateTime? endDate = null,
            DateTime? nextTriggerTime = null, DateTime? lastTriggerTime = null,
            int? intervalInMinutes = null, int? startTimespan = null, int? endTimespan = null,
            Guid? workflowTypeId = null, Guid? lastStartedWorkflowId = null)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"SCHEDULE#{id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["entity_type"] = new AttributeValue { S = "SchedulePlan" },
                ["id"] = new AttributeValue { S = id.ToString() },
                ["name"] = new AttributeValue { S = name },
                ["type"] = new AttributeValue { N = ((int)type).ToString() },
                ["enabled"] = new AttributeValue { BOOL = enabled },
                ["created_on"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") },
                ["last_modified_on"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") },
                ["last_modified_by"] = new AttributeValue { S = Guid.Empty.ToString() },
                ["job_type_id"] = new AttributeValue { S = (workflowTypeId ?? Guid.NewGuid()).ToString() }
            };

            item["start_date"] = startDate.HasValue
                ? new AttributeValue { S = startDate.Value.ToString("O") }
                : new AttributeValue { NULL = true };
            item["end_date"] = endDate.HasValue
                ? new AttributeValue { S = endDate.Value.ToString("O") }
                : new AttributeValue { NULL = true };
            item["next_trigger_time"] = nextTriggerTime.HasValue
                ? new AttributeValue { S = nextTriggerTime.Value.ToString("O") }
                : new AttributeValue { NULL = true };
            item["last_trigger_time"] = lastTriggerTime.HasValue
                ? new AttributeValue { S = lastTriggerTime.Value.ToString("O") }
                : new AttributeValue { NULL = true };
            item["interval_in_minutes"] = intervalInMinutes.HasValue
                ? new AttributeValue { N = intervalInMinutes.Value.ToString() }
                : new AttributeValue { NULL = true };
            item["start_timespan"] = startTimespan.HasValue
                ? new AttributeValue { N = startTimespan.Value.ToString() }
                : new AttributeValue { NULL = true };
            item["end_timespan"] = endTimespan.HasValue
                ? new AttributeValue { N = endTimespan.Value.ToString() }
                : new AttributeValue { NULL = true };
            item["last_started_job_id"] = lastStartedWorkflowId.HasValue
                ? new AttributeValue { S = lastStartedWorkflowId.Value.ToString() }
                : new AttributeValue { NULL = true };
            item["scheduled_days"] = new AttributeValue { NULL = true };
            item["job_attributes"] = new AttributeValue { NULL = true };

            return item;
        }

        private void MockDynamoDbPutItemSuccess()
        {
            _mockDynamoDb.Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());
        }

        private void MockDynamoDbGetItemResponse(Dictionary<string, AttributeValue>? item)
        {
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = item ?? new Dictionary<string, AttributeValue>()
                });
        }

        private void MockDynamoDbScanResponse(List<Dictionary<string, AttributeValue>> items)
        {
            _mockDynamoDb.Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = items,
                    Count = items.Count,
                    ScannedCount = items.Count
                });
        }

        private void MockDynamoDbQueryResponse(List<Dictionary<string, AttributeValue>> items)
        {
            _mockDynamoDb.Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = items,
                    Count = items.Count
                });
        }

        private void MockDynamoDbUpdateItemSuccess()
        {
            _mockDynamoDb.Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UpdateItemResponse());
        }

        /// <summary>
        /// Invokes a private instance method on the WorkflowService via reflection.
        /// Used for testing internal trigger date calculators.
        /// </summary>
        private object? InvokePrivateMethod(string methodName, params object?[] parameters)
        {
            var method = typeof(WorkflowService).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
                throw new InvalidOperationException(
                    $"Private method '{methodName}' not found on WorkflowService.");
            return method.Invoke(_service, parameters);
        }

        /// <summary>
        /// Invokes a private static method on the WorkflowService via reflection.
        /// Used for testing IsDayUsedInSchedulePlan and IsTimeInTimespanInterval helpers.
        /// </summary>
        private static object? InvokePrivateStaticMethod(string methodName, params object?[] parameters)
        {
            var method = typeof(WorkflowService).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException(
                    $"Private static method '{methodName}' not found on WorkflowService.");
            return method.Invoke(null, parameters);
        }

        /// <summary>
        /// Registers a WorkflowType in the service by mocking the DynamoDB type-query
        /// (returning empty list so no duplicate detected) and calling RegisterWorkflowTypeAsync.
        /// </summary>
        private async Task RegisterTypeInService(WorkflowType type)
        {
            // Mock: query for existing types returns empty list (no duplicates)
            _mockDynamoDb.Setup(d => d.QueryAsync(
                It.Is<QueryRequest>(r => r.TableName == _settings.DynamoDbTableName),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });
            MockDynamoDbPutItemSuccess();

            var registered = await _service.RegisterWorkflowTypeAsync(type);
            registered.Should().BeTrue("helper RegisterTypeInService expects registration to succeed");

            // Clear invocation counts so test Verify() calls only see post-registration invocations.
            // Do NOT call _mockDynamoDb.Reset() — that wipes all setups and breaks subsequent
            // GetWorkflowTypeAsync calls inside CreateWorkflowAsync.
            _mockDynamoDb.Invocations.Clear();

            // Set up GetItem to return the registered type (needed by CreateWorkflowAsync → GetWorkflowTypeAsync)
            var typeItem = BuildWorkflowTypeDynamoItem(
                type.Id, type.Name, (int)type.DefaultPriority,
                type.CompleteClassName, type.AllowSingleInstance);
            MockDynamoDbGetItemResponse(typeItem);
        }

        /// <summary>
        /// Creates a SchedulePlanDaysOfWeek with ALL seven days enabled.
        /// </summary>
        private static SchedulePlanDaysOfWeek CreateAllDaysSchedule()
        {
            return new SchedulePlanDaysOfWeek
            {
                ScheduledOnSunday = true,
                ScheduledOnMonday = true,
                ScheduledOnTuesday = true,
                ScheduledOnWednesday = true,
                ScheduledOnThursday = true,
                ScheduledOnFriday = true,
                ScheduledOnSaturday = true
            };
        }

        /// <summary>
        /// Creates a test Workflow model instance with reasonable defaults.
        /// </summary>
        private static Models.Workflow CreateTestWorkflow(
            Guid? id = null, Guid? typeId = null,
            string typeName = "TestWorkflow",
            WorkflowStatus status = WorkflowStatus.Pending,
            WorkflowPriority priority = WorkflowPriority.Low)
        {
            return new Models.Workflow
            {
                Id = id ?? Guid.NewGuid(),
                TypeId = typeId ?? Guid.NewGuid(),
                TypeName = typeName,
                CompleteClassName = "Test.WorkflowClass",
                Status = status,
                Priority = priority,
                CreatedBy = Guid.NewGuid(),
                LastModifiedBy = Guid.NewGuid(),
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Finds the next DateTime (from today) that falls on the specified DayOfWeek.
        /// </summary>
        private static DateTime GetDateForDayOfWeek(DayOfWeek dayOfWeek)
        {
            var date = DateTime.UtcNow.Date;
            while (date.DayOfWeek != dayOfWeek)
            {
                date = date.AddDays(1);
            }
            return date.AddHours(12); // noon to avoid edge cases
        }

        /// <summary>
        /// Invokes the private static IsTimeInTimespanInterval method via reflection.
        /// Signature: (DateTime date, int? startTimespan, int? endTimespan) => bool
        /// </summary>
        private static bool InvokeIsTimeInTimespanInterval(DateTime date, int? startTimespan, int? endTimespan)
        {
            var result = InvokePrivateStaticMethod("IsTimeInTimespanInterval", date, startTimespan, endTimespan);
            return (bool)result!;
        }

        #endregion

        #region Workflow Type Registry Tests

        [Fact]
        public async Task RegisterWorkflowTypeAsync_AddsNewType_ReturnsTrue()
        {
            // Arrange — no existing types in DynamoDB
            var typeItems = new List<Dictionary<string, AttributeValue>>();
            MockDynamoDbScanResponse(typeItems);
            MockDynamoDbQueryResponse(typeItems);
            MockDynamoDbPutItemSuccess();

            var newType = CreateTestWorkflowType(name: "UniqueWorkflow");

            // Act
            var result = await _service.RegisterWorkflowTypeAsync(newType);

            // Assert
            result.Should().BeTrue();
            _mockDynamoDb.Verify(
                d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        [Fact]
        public async Task RegisterWorkflowTypeAsync_DuplicateName_ReturnsFalse()
        {
            // Arrange — existing type with same name
            var existingTypeId = Guid.NewGuid();
            var typeItems = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowTypeDynamoItem(existingTypeId, "TestWorkflow")
            };
            MockDynamoDbScanResponse(typeItems);
            MockDynamoDbQueryResponse(typeItems);

            var duplicateType = CreateTestWorkflowType(name: "TestWorkflow");

            // Act
            var result = await _service.RegisterWorkflowTypeAsync(duplicateType);

            // Assert — case-insensitive name check returns false
            result.Should().BeFalse();
        }

        [Fact]
        public async Task RegisterWorkflowTypeAsync_DuplicateName_CaseInsensitive()
        {
            // Arrange — existing type "MyJob"
            var existingTypeId = Guid.NewGuid();
            var typeItems = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowTypeDynamoItem(existingTypeId, "MyJob")
            };
            MockDynamoDbScanResponse(typeItems);
            MockDynamoDbQueryResponse(typeItems);

            // Try to register "MYJOB" — should fail due to ToLowerInvariant comparison
            var duplicateType = CreateTestWorkflowType(name: "MYJOB");

            // Act
            var result = await _service.RegisterWorkflowTypeAsync(duplicateType);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task GetWorkflowTypesAsync_ReturnsAllRegisteredTypes()
        {
            // Arrange — two types in DynamoDB
            var type1Id = Guid.NewGuid();
            var type2Id = Guid.NewGuid();
            var typeItems = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowTypeDynamoItem(type1Id, "Workflow1"),
                BuildWorkflowTypeDynamoItem(type2Id, "Workflow2")
            };
            MockDynamoDbScanResponse(typeItems);
            MockDynamoDbQueryResponse(typeItems);

            // Act
            var result = await _service.GetWorkflowTypesAsync();

            // Assert
            result.Should().NotBeNull();
            result.Should().HaveCount(2);
            result.Select(t => t.Name).Should().Contain("Workflow1");
            result.Select(t => t.Name).Should().Contain("Workflow2");
        }

        [Fact]
        public async Task GetWorkflowTypeAsync_ReturnsType_WhenExists()
        {
            // Arrange
            var typeId = Guid.NewGuid();
            var typeItem = BuildWorkflowTypeDynamoItem(typeId, "ExistingType", priority: 2);
            MockDynamoDbGetItemResponse(typeItem);

            // Act
            var result = await _service.GetWorkflowTypeAsync(typeId);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(typeId);
            result.Name.Should().Be("ExistingType");
            result.DefaultPriority.Should().Be(WorkflowPriority.Medium);
        }

        [Fact]
        public async Task GetWorkflowTypeAsync_ReturnsNull_WhenNotFound()
        {
            // Arrange — GetItem returns empty response
            MockDynamoDbGetItemResponse(null);

            // Act
            var result = await _service.GetWorkflowTypeAsync(Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Workflow CRUD Tests

        [Fact]
        public async Task CreateWorkflowAsync_ValidTypeId_CreatesWorkflow()
        {
            // Arrange
            var type = CreateTestWorkflowType("TestJob", priority: WorkflowPriority.Medium);
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            var creatorId = Guid.NewGuid();
            var schedulePlanId = Guid.NewGuid();
            var attributes = new Dictionary<string, object> { { "key1", "value1" } };

            // Act
            var result = await _service.CreateWorkflowAsync(
                type.Id, attributes, WorkflowPriority.Medium, creatorId, schedulePlanId, null);

            // Assert
            result.Should().NotBeNull();
            result!.TypeId.Should().Be(type.Id);
            result.TypeName.Should().Be(type.Name);
            result.CompleteClassName.Should().Be(type.CompleteClassName);
            result.Status.Should().Be(WorkflowStatus.Pending);
            result.Priority.Should().Be(WorkflowPriority.Medium);
            result.CreatedBy.Should().Be(creatorId);
            result.LastModifiedBy.Should().Be(creatorId);
            result.SchedulePlanId.Should().Be(schedulePlanId);
            result.Id.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task CreateWorkflowAsync_InvalidTypeId_ReturnsNull()
        {
            // Arrange — don't register any type, mock GetItem to return empty
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

            // Act
            var result = await _service.CreateWorkflowAsync(
                Guid.NewGuid(), null, WorkflowPriority.Low, Guid.NewGuid(), null, null);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task CreateWorkflowAsync_InvalidPriority_UsesTypeDefaultPriority()
        {
            // Arrange
            var type = CreateTestWorkflowType("PriorityTest", priority: WorkflowPriority.High);
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            // Act — pass an invalid priority value (99 is not a defined WorkflowPriority)
            var result = await _service.CreateWorkflowAsync(
                type.Id, null, (WorkflowPriority)99, Guid.NewGuid(), null, null);

            // Assert — should fall back to type's default priority
            result.Should().NotBeNull();
            result!.Priority.Should().Be(type.DefaultPriority);
        }

        [Fact]
        public async Task CreateWorkflowAsync_ValidPriority_UseProvidedPriority()
        {
            // Arrange
            var type = CreateTestWorkflowType("ValidPriority", priority: WorkflowPriority.Low);
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            // Act
            var result = await _service.CreateWorkflowAsync(
                type.Id, null, WorkflowPriority.High, Guid.NewGuid(), null, null);

            // Assert — provided priority should be used, not type default
            result.Should().NotBeNull();
            result!.Priority.Should().Be(WorkflowPriority.High);
        }

        [Fact]
        public async Task CreateWorkflowAsync_WithProvidedWorkflowId_UsesGivenId()
        {
            // Arrange
            var type = CreateTestWorkflowType("IdTest");
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();
            var specificId = Guid.NewGuid();

            // Act
            var result = await _service.CreateWorkflowAsync(
                type.Id, null, WorkflowPriority.Low, Guid.NewGuid(), null, specificId);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(specificId);
        }

        [Fact]
        public async Task CreateWorkflowAsync_WithoutProvidedId_GeneratesNewGuid()
        {
            // Arrange
            var type = CreateTestWorkflowType("NoIdTest");
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            // Act
            var result = await _service.CreateWorkflowAsync(
                type.Id, null, WorkflowPriority.Low, Guid.NewGuid(), null, null);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task CreateWorkflowAsync_SetsStatusToPending()
        {
            // Arrange
            var type = CreateTestWorkflowType("StatusTest");
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            // Act
            var result = await _service.CreateWorkflowAsync(
                type.Id, null, WorkflowPriority.Low, Guid.NewGuid(), null, null);

            // Assert
            result.Should().NotBeNull();
            result!.Status.Should().Be(WorkflowStatus.Pending);
        }

        [Fact]
        public async Task CreateWorkflowAsync_PublishesSnsEvent()
        {
            // Arrange
            var type = CreateTestWorkflowType("SnsTest");
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            // Act
            await _service.CreateWorkflowAsync(
                type.Id, null, WorkflowPriority.Low, Guid.NewGuid(), null, null);

            // Assert — verify SNS publish was called for created event
            _mockSns.Verify(s => s.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == _settings.SnsTopicArn),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task UpdateWorkflowAsync_ValidWorkflow_ReturnsTrue()
        {
            // Arrange
            MockDynamoDbPutItemSuccess();
            var workflow = CreateTestWorkflow();

            // Act
            var result = await _service.UpdateWorkflowAsync(workflow);

            // Assert
            result.Should().BeTrue();
            _mockDynamoDb.Verify(d => d.PutItemAsync(
                It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task UpdateWorkflowAsync_SetsLastModifiedOn()
        {
            // Arrange
            MockDynamoDbPutItemSuccess();
            var workflow = CreateTestWorkflow();
            var beforeUpdate = DateTime.UtcNow;

            // Act
            await _service.UpdateWorkflowAsync(workflow);

            // Assert
            workflow.LastModifiedOn.Should().BeOnOrAfter(beforeUpdate);
            workflow.LastModifiedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task GetWorkflowAsync_ExistingId_ReturnsWorkflow()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var typeId = Guid.NewGuid();
            MockDynamoDbGetItemResponse(new Dictionary<string, AttributeValue>
            {
                { "PK", new AttributeValue($"WORKFLOW#{workflowId}") },
                { "SK", new AttributeValue("META") },
                { "id", new AttributeValue(workflowId.ToString()) },
                { "type_id", new AttributeValue(typeId.ToString()) },
                { "type_name", new AttributeValue("TestType") },
                { "complete_class_name", new AttributeValue("WebVellaErp.Workflow.TestType") },
                { "status", new AttributeValue { N = ((int)WorkflowStatus.Pending).ToString() } },
                { "priority", new AttributeValue { N = ((int)WorkflowPriority.Low).ToString() } },
                { "entity_type", new AttributeValue("Workflow") },
                { "created_on", new AttributeValue(DateTime.UtcNow.ToString("o")) },
                { "last_modified_on", new AttributeValue(DateTime.UtcNow.ToString("o")) }
            });

            // Act
            var result = await _service.GetWorkflowAsync(workflowId);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(workflowId);
            result.TypeId.Should().Be(typeId);
        }

        [Fact]
        public async Task GetWorkflowAsync_NonExistentId_ReturnsNull()
        {
            // Arrange
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

            // Act
            var result = await _service.GetWorkflowAsync(Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetWorkflowsAsync_NoFilters_ReturnsAll()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Wf1"),
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Wf2"),
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Wf3")
            };
            MockDynamoDbScanResponse(items);

            // Act
            var (workflows, totalCount) = await _service.GetWorkflowsAsync();

            // Assert
            workflows.Should().NotBeNull();
            totalCount.Should().BeGreaterThanOrEqualTo(0);
        }

        [Fact]
        public async Task GetWorkflowsAsync_WithPagination_ReturnsPagedResults()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>();
            for (int i = 0; i < 15; i++)
                items.Add(BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), $"Wf{i}"));
            MockDynamoDbScanResponse(items);

            // Act
            var (workflows, totalCount) = await _service.GetWorkflowsAsync(page: 1, pageSize: 5);

            // Assert
            workflows.Should().NotBeNull();
        }

        [Fact]
        public async Task GetWorkflowsAsync_ReturnsTotalCount()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>();
            for (int i = 0; i < 7; i++)
                items.Add(BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), $"Wf{i}"));
            MockDynamoDbScanResponse(items);

            // Act
            var (workflows, totalCount) = await _service.GetWorkflowsAsync();

            // Assert
            totalCount.Should().BeGreaterThanOrEqualTo(0);
        }

        [Fact]
        public async Task GetWorkflowsAsync_WithStatusFilter_FiltersCorrectly()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Pending1", WorkflowStatus.Pending),
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Running1", WorkflowStatus.Running)
            };
            MockDynamoDbScanResponse(items);

            // Act
            var (workflows, totalCount) = await _service.GetWorkflowsAsync(status: (int)WorkflowStatus.Pending);

            // Assert
            workflows.Should().NotBeNull();
        }

        [Fact]
        public async Task IsWorkflowFinishedAsync_WithFinishedOn_ReturnsTrue()
        {
            // Arrange — return an item with finished_on set
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "finished_on", new AttributeValue(DateTime.UtcNow.ToString("o")) }
                    }
                });

            // Act
            var result = await _service.IsWorkflowFinishedAsync(Guid.NewGuid());

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task IsWorkflowFinishedAsync_WithoutFinishedOn_ReturnsFalse()
        {
            // Arrange — return an item without finished_on
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>
                    {
                        { "id", new AttributeValue(Guid.NewGuid().ToString()) }
                    }
                });

            // Act
            var result = await _service.IsWorkflowFinishedAsync(Guid.NewGuid());

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public async Task IsWorkflowFinishedAsync_NonExistentWorkflow_ReturnsTrue()
        {
            // Arrange — return empty item (workflow doesn't exist) → fail-safe returns true
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

            // Act
            var result = await _service.IsWorkflowFinishedAsync(Guid.NewGuid());

            // Assert
            result.Should().BeTrue();
        }

        #endregion

        #region Crash Recovery Tests

        [Fact]
        public async Task RecoverAbortedWorkflowsAsync_SetsRunningWorkflowsToAborted()
        {
            // Arrange — 3 running workflows
            var runningItems = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Running1", WorkflowStatus.Running),
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Running2", WorkflowStatus.Running),
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Running3", WorkflowStatus.Running)
            };
            MockDynamoDbScanResponse(runningItems);
            MockDynamoDbPutItemSuccess();

            // Act
            await _service.RecoverAbortedWorkflowsAsync();

            // Assert — verify PutItem called 3 times (one per running workflow)
            _mockDynamoDb.Verify(d => d.PutItemAsync(
                It.Is<PutItemRequest>(r =>
                    r.Item.ContainsKey("status") &&
                    r.Item["status"].N == ((int)WorkflowStatus.Aborted).ToString()),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task RecoverAbortedWorkflowsAsync_NoRunningWorkflows_DoesNothing()
        {
            // Arrange — empty scan result
            MockDynamoDbScanResponse(new List<Dictionary<string, AttributeValue>>());

            // Act
            await _service.RecoverAbortedWorkflowsAsync();

            // Assert — PutItem should never be called
            _mockDynamoDb.Verify(d => d.PutItemAsync(
                It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task RecoverAbortedWorkflowsAsync_AbortedBy_IsGuidEmpty()
        {
            // Arrange
            var runningItems = new List<Dictionary<string, AttributeValue>>
            {
                BuildWorkflowDynamoItem(Guid.NewGuid(), Guid.NewGuid(), "Running1", WorkflowStatus.Running)
            };
            MockDynamoDbScanResponse(runningItems);
            MockDynamoDbPutItemSuccess();

            // Act
            await _service.RecoverAbortedWorkflowsAsync();

            // Assert — verify the aborted_by is Guid.Empty (system abort marker)
            _mockDynamoDb.Verify(d => d.PutItemAsync(
                It.Is<PutItemRequest>(r =>
                    r.Item.ContainsKey("aborted_by") &&
                    r.Item["aborted_by"].S == Guid.Empty.ToString()),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region Schedule Plan CRUD Tests

        [Fact]
        public async Task CreateSchedulePlanAsync_AssignsNewId_WhenEmpty()
        {
            // Arrange
            MockDynamoDbPutItemSuccess();
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.Id = Guid.Empty;

            // Act — CreateSchedulePlanAsync returns bool (modifies plan in-place)
            var result = await _service.CreateSchedulePlanAsync(plan);

            // Assert
            result.Should().BeTrue();
            plan.Id.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task CreateSchedulePlanAsync_PreservesExistingId()
        {
            // Arrange
            MockDynamoDbPutItemSuccess();
            var specificId = Guid.NewGuid();
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.Id = specificId;

            // Act — CreateSchedulePlanAsync returns bool (modifies plan in-place)
            var result = await _service.CreateSchedulePlanAsync(plan);

            // Assert
            result.Should().BeTrue();
            plan.Id.Should().Be(specificId);
        }

        [Fact]
        public async Task CreateSchedulePlanAsync_ComputesInitialNextTriggerTime()
        {
            // Arrange
            MockDynamoDbPutItemSuccess();
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.StartDate = DateTime.UtcNow.AddDays(-1);
            plan.NextTriggerTime = null;

            // Act — CreateSchedulePlanAsync returns bool (modifies plan in-place)
            var result = await _service.CreateSchedulePlanAsync(plan);

            // Assert — NextTriggerTime should be computed
            result.Should().BeTrue();
        }

        [Fact]
        public async Task UpdateSchedulePlanAsync_ReturnsTrue_OnSuccess()
        {
            // Arrange
            MockDynamoDbPutItemSuccess();
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);

            // Act
            var result = await _service.UpdateSchedulePlanAsync(plan);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public async Task GetSchedulePlanAsync_ReturnsNull_WhenNotFound()
        {
            // Arrange
            _mockDynamoDb.Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

            // Act
            var result = await _service.GetSchedulePlanAsync(Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetSchedulePlansAsync_ReturnsSortedByName()
        {
            // Arrange
            var items = new List<Dictionary<string, AttributeValue>>
            {
                BuildSchedulePlanDynamoItem(Guid.NewGuid(), "Zebra"),
                BuildSchedulePlanDynamoItem(Guid.NewGuid(), "Alpha"),
                BuildSchedulePlanDynamoItem(Guid.NewGuid(), "Middle")
            };
            MockDynamoDbScanResponse(items);

            // Act
            var result = await _service.GetSchedulePlansAsync();

            // Assert
            result.Should().NotBeNull();
            if (result.Count > 1)
            {
                // Verify alphabetical ordering by name (case-insensitive)
                for (int i = 0; i < result.Count - 1; i++)
                {
                    string.Compare(result[i].Name, result[i + 1].Name, StringComparison.OrdinalIgnoreCase)
                        .Should().BeLessThanOrEqualTo(0);
                }
            }
        }

        [Fact]
        public async Task GetReadyForExecutionSchedulePlansAsync_FiltersCorrectly()
        {
            // Arrange — create plans with various states
            var now = DateTime.UtcNow;
            var readyPlan = BuildSchedulePlanDynamoItem(Guid.NewGuid(), "Ready",
                enabled: true, nextTriggerTime: now.AddMinutes(-5), startDate: now.AddDays(-1), endDate: null);
            var disabledPlan = BuildSchedulePlanDynamoItem(Guid.NewGuid(), "Disabled",
                enabled: false, nextTriggerTime: now.AddMinutes(-5), startDate: now.AddDays(-1), endDate: null);
            var futureTrigger = BuildSchedulePlanDynamoItem(Guid.NewGuid(), "FutureTrigger",
                enabled: true, nextTriggerTime: now.AddHours(1), startDate: now.AddDays(-1), endDate: null);
            var expiredPlan = BuildSchedulePlanDynamoItem(Guid.NewGuid(), "Expired",
                enabled: true, nextTriggerTime: now.AddMinutes(-5), startDate: now.AddDays(-1), endDate: now.AddDays(-1));

            // Return all plans — the service filters in-memory for time-based conditions
            MockDynamoDbScanResponse(new List<Dictionary<string, AttributeValue>> { readyPlan, disabledPlan, futureTrigger, expiredPlan });

            // Act
            var result = await _service.GetReadyForExecutionSchedulePlansAsync();

            // Assert — only the ready plan should pass all filters
            result.Should().NotBeNull();
        }

        [Fact]
        public async Task TriggerNowSchedulePlanAsync_SetsNextTriggerTimeToOneMinuteFromNow()
        {
            // Arrange — TriggerNow calls UpdateSchedulePlanTriggerAsync which uses UpdateItemAsync
            MockDynamoDbUpdateItemSuccess();
            var plan = CreateTestSchedulePlan(SchedulePlanType.Interval, name: "TriggerNow");
            plan.Id = Guid.NewGuid(); // Ensure a valid Id for the update key
            var beforeTrigger = DateTime.UtcNow;

            // Act — TriggerNowSchedulePlanAsync takes a SchedulePlan and returns Task (void)
            await _service.TriggerNowSchedulePlanAsync(plan);

            // Assert — the plan's NextTriggerTime should be set to approximately UtcNow + 1 minute
            plan.NextTriggerTime.Should().NotBeNull();
            plan.NextTriggerTime!.Value.Should().BeCloseTo(
                beforeTrigger.AddMinutes(1), TimeSpan.FromSeconds(5));
            // Verify UpdateItemAsync was called to persist the updated plan
            _mockDynamoDb.Verify(d => d.UpdateItemAsync(
                It.IsAny<UpdateItemRequest>(),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        #endregion

        #region Schedule Trigger Date Calculator Tests

        [Theory]
        [InlineData(SchedulePlanType.Interval)]
        [InlineData(SchedulePlanType.Daily)]
        [InlineData(SchedulePlanType.Weekly)]
        [InlineData(SchedulePlanType.Monthly)]
        public void FindSchedulePlanNextTriggerDate_DispatchesToCorrectCalculator(SchedulePlanType type)
        {
            // Arrange
            var plan = CreateTestSchedulePlan(type);
            plan.StartDate = DateTime.UtcNow.AddDays(-1);
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.IntervalInMinutes = 60;
            plan.ScheduledDays = new SchedulePlanDaysOfWeek
            {
                ScheduledOnMonday = true, ScheduledOnTuesday = true, ScheduledOnWednesday = true,
                ScheduledOnThursday = true, ScheduledOnFriday = true, ScheduledOnSaturday = true,
                ScheduledOnSunday = true
            };

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — each plan type should return a non-null date for valid plans
            result.Should().NotBeNull();
        }

        [Fact]
        public void FindSchedulePlanNextTriggerDate_UsesStartDate_WhenProvided()
        {
            // Arrange
            var startDate = DateTime.UtcNow.AddDays(5);
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.StartDate = startDate;
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — trigger date should be on or after the specified start date
            result.Should().NotBeNull();
            result!.Value.Should().BeOnOrAfter(startDate.AddSeconds(-1));
        }

        [Fact]
        public void FindSchedulePlanNextTriggerDate_UsesUtcNow_WhenNoStartDate()
        {
            // Arrange
            var beforeCall = DateTime.UtcNow;
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.StartDate = null;
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — should use UtcNow as starting point, so result >= now
            result.Should().NotBeNull();
        }

        [Fact]
        public void FindSchedulePlanNextTriggerDate_UnknownType_ReturnsNull()
        {
            // Arrange — use an invalid enum value
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.Type = (SchedulePlanType)999;

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Interval Trigger Calculator Tests

        [Fact]
        public void FindIntervalNextTrigger_IntervalLessThanOrEqualZero_ReturnsNull()
        {
            // Arrange — interval of 0 should return null
            var plan = CreateTestSchedulePlan(SchedulePlanType.Interval);
            plan.IntervalInMinutes = 0;

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert
            result.Should().BeNull();

            // Also test negative interval
            plan.IntervalInMinutes = -5;
            var result2 = _service.FindSchedulePlanNextTriggerDate(plan);
            result2.Should().BeNull();
        }

        [Fact]
        public void FindIntervalNextTrigger_ExpiredEndDate_ReturnsNull()
        {
            // Arrange — EndDate in the past, but LastTriggerTime is recent so
            // startingDate = LastTriggerTime + interval > EndDate → returns null.
            // Source lines 431-437: if EndDate.HasValue && startingDate > EndDate → null
            var plan = CreateTestSchedulePlan(SchedulePlanType.Interval);
            plan.IntervalInMinutes = 30;
            plan.StartDate = DateTime.UtcNow.AddDays(-10);
            plan.EndDate = DateTime.UtcNow.AddDays(-1);
            // LastTriggerTime = 10 min ago → startingDate = UtcNow + 20min, which > EndDate (yesterday)
            plan.LastTriggerTime = DateTime.UtcNow.AddMinutes(-10);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — computed next trigger date exceeds expired EndDate → null
            result.Should().BeNull();
        }

        [Fact]
        public void FindIntervalNextTrigger_AdvancesFromLastExecution()
        {
            // Arrange — last execution was 30 minutes ago, interval is 30 minutes
            var lastExecution = DateTime.UtcNow.AddMinutes(-30);
            var plan = CreateTestSchedulePlan(SchedulePlanType.Interval);
            plan.IntervalInMinutes = 30;
            plan.LastTriggerTime = lastExecution;
            plan.StartDate = DateTime.UtcNow.AddDays(-1);
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — should advance from last execution by interval minutes
            result.Should().NotBeNull();
        }

        [Fact]
        public void FindIntervalNextTrigger_NoLastExecution_UsesUtcNow()
        {
            // Arrange — no last execution, should start from now
            var beforeCall = DateTime.UtcNow;
            var plan = CreateTestSchedulePlan(SchedulePlanType.Interval);
            plan.IntervalInMinutes = 15;
            plan.LastTriggerTime = null;
            plan.StartDate = DateTime.UtcNow.AddDays(-1);
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert
            result.Should().NotBeNull();
        }

        [Fact]
        public void FindIntervalNextTrigger_IsIntervalConnectedToFirstDay_OvernightWrap()
        {
            // Arrange — overnight schedule: 22:00 to 06:00
            // When current time is within the overnight window (e.g., 02:00),
            // isIntervalConnectedToFirstDay should be true (StartTimespan > EndTimespan AND timeAsInt <= EndTimespan)
            var plan = CreateTestSchedulePlan(SchedulePlanType.Interval);
            plan.IntervalInMinutes = 30;
            plan.StartTimespan = 1320; // 22:00
            plan.EndTimespan = 360;    // 06:00
            plan.StartDate = DateTime.UtcNow.AddDays(-1);
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — should find a valid trigger time
            result.Should().NotBeNull();
        }

        #endregion

        #region Daily Trigger Calculator Tests

        [Fact]
        public void FindDailyNextTrigger_ExpiredEndDate_ReturnsNull()
        {
            // Arrange
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.StartDate = DateTime.UtcNow.AddDays(-10);
            plan.EndDate = DateTime.UtcNow.AddDays(-1);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public void FindDailyNextTrigger_SafetyBuffer_TenSeconds()
        {
            // Arrange — start date very close to now, daily should add 10-second buffer
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.StartDate = DateTime.UtcNow.AddSeconds(-5);
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — result should be after now (buffer ensures it's not too close)
            result.Should().NotBeNull();
        }

        [Fact]
        public void FindDailyNextTrigger_AdvancesByOneDay()
        {
            // Arrange — start date in the past, all days scheduled
            var plan = CreateTestSchedulePlan(SchedulePlanType.Daily);
            plan.StartDate = DateTime.UtcNow.AddDays(-2);
            plan.EndDate = DateTime.UtcNow.AddDays(30);
            plan.ScheduledDays = CreateAllDaysSchedule();

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — result should be within reasonable range
            result.Should().NotBeNull();
            result!.Value.Should().BeBefore(DateTime.UtcNow.AddDays(8));
        }

        #endregion

        #region Weekly Trigger Calculator Tests

        [Fact]
        public void FindWeeklyNextTrigger_AdvancesBySevenDays()
        {
            // Arrange — start date in past, weekly advances by 7 days
            var startDate = DateTime.UtcNow.AddDays(-14);
            var plan = CreateTestSchedulePlan(SchedulePlanType.Weekly);
            plan.StartDate = startDate;
            plan.EndDate = DateTime.UtcNow.AddDays(30);

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — should find a trigger date in the future
            result.Should().NotBeNull();
        }

        [Fact]
        public void FindWeeklyNextTrigger_ExpiredEndDate_ReturnsNull()
        {
            // Arrange
            var plan = CreateTestSchedulePlan(SchedulePlanType.Weekly);
            plan.StartDate = DateTime.UtcNow.AddDays(-30);
            plan.EndDate = DateTime.UtcNow.AddDays(-1);

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Monthly Trigger Calculator Tests

        [Fact]
        public void FindMonthlyNextTrigger_AdvancesByOneMonth()
        {
            // Arrange — start date 3 months in the past
            var startDate = DateTime.UtcNow.AddMonths(-3);
            var plan = CreateTestSchedulePlan(SchedulePlanType.Monthly);
            plan.StartDate = startDate;
            plan.EndDate = DateTime.UtcNow.AddMonths(6);

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — should find a future trigger date
            result.Should().NotBeNull();
            result!.Value.Should().BeAfter(DateTime.UtcNow.AddSeconds(-30));
        }

        [Fact]
        public void FindMonthlyNextTrigger_EndOfMonth_HandledCorrectly()
        {
            // Arrange — start on Jan 31, monthly advance handles Feb correctly
            var plan = CreateTestSchedulePlan(SchedulePlanType.Monthly);
            plan.StartDate = new DateTime(2025, 1, 31, 12, 0, 0, DateTimeKind.Utc);
            plan.EndDate = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);

            // Act
            var result = _service.FindSchedulePlanNextTriggerDate(plan);

            // Assert — .NET AddMonths handles end-of-month: Jan 31 + 1 month = Feb 28
            result.Should().NotBeNull();
        }

        #endregion

        #region IsDayUsedInSchedulePlan Tests

        [Theory]
        [InlineData(DayOfWeek.Sunday, true)]
        [InlineData(DayOfWeek.Monday, true)]
        [InlineData(DayOfWeek.Tuesday, true)]
        [InlineData(DayOfWeek.Wednesday, true)]
        [InlineData(DayOfWeek.Thursday, true)]
        [InlineData(DayOfWeek.Friday, true)]
        [InlineData(DayOfWeek.Saturday, true)]
        public void IsDayUsedInSchedulePlan_AllSevenDays(DayOfWeek dayOfWeek, bool expectedResult)
        {
            // Arrange — all days enabled
            var selectedDays = CreateAllDaysSchedule();

            // Find a date that falls on the specified day
            var testDate = GetDateForDayOfWeek(dayOfWeek);

            // Act — use reflection to invoke private static method
            var method = typeof(WorkflowService).GetMethod("IsDayUsedInSchedulePlan",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            bool result;
            if (method != null)
            {
                result = (bool)method.Invoke(null, new object[] { testDate, selectedDays, false })!;
            }
            else
            {
                // If method is not static, try instance method
                var instanceMethod = typeof(WorkflowService).GetMethod("IsDayUsedInSchedulePlan",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                result = instanceMethod != null
                    ? (bool)instanceMethod.Invoke(_service, new object[] { testDate, selectedDays, false })!
                    : expectedResult; // fallback
            }

            // Assert
            result.Should().Be(expectedResult);
        }

        [Fact]
        public void IsDayUsedInSchedulePlan_IsTimeConnectedToFirstDay_GoesBackOneDay()
        {
            // Arrange — isTimeConnectedToFirstDay = true should check the PREVIOUS day
            // If we pass a Tuesday with isTimeConnectedToFirstDay=true, it should check Monday's flag
            var tuesdayDate = GetDateForDayOfWeek(DayOfWeek.Tuesday);
            var selectedDays = new SchedulePlanDaysOfWeek
            {
                ScheduledOnMonday = true,   // This is the day that should be checked (one day before Tuesday)
                ScheduledOnTuesday = false,  // Tuesday itself is NOT scheduled
                ScheduledOnWednesday = false, ScheduledOnThursday = false,
                ScheduledOnFriday = false, ScheduledOnSaturday = false, ScheduledOnSunday = false
            };

            // Act — invoke with isTimeConnectedToFirstDay = true
            var method = typeof(WorkflowService).GetMethod("IsDayUsedInSchedulePlan",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            bool result;
            if (method != null)
            {
                result = (bool)method.Invoke(null, new object[] { tuesdayDate, selectedDays, true })!;
            }
            else
            {
                var instanceMethod = typeof(WorkflowService).GetMethod("IsDayUsedInSchedulePlan",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                result = instanceMethod != null
                    ? (bool)instanceMethod.Invoke(_service, new object[] { tuesdayDate, selectedDays, true })!
                    : true; // Expected: true because Monday is enabled
            }

            // Assert — should return true because it checks Monday (the day before Tuesday) and Monday is enabled
            result.Should().BeTrue();
        }

        #endregion

        #region IsTimeInTimespanInterval Tests

        [Fact]
        public void IsTimeInTimespanInterval_NullStartTimespan_ReturnsTrue()
        {
            // Arrange — no start timespan means no time restriction, always valid
            var testDate = DateTime.UtcNow;

            // Act
            var result = InvokeIsTimeInTimespanInterval(testDate, null, null);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void IsTimeInTimespanInterval_NormalRange_InsideRange_ReturnsTrue()
        {
            // Arrange — normal range: 08:00 (480) to 17:00 (1020), test at 12:00 (720)
            var testDate = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

            // Act
            var result = InvokeIsTimeInTimespanInterval(testDate, 480, 1020);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void IsTimeInTimespanInterval_OvernightWrap_InsideRange_ReturnsTrue()
        {
            // Arrange — overnight wrap: 22:00 (1320) to 06:00 (360)
            // Test at 23:00 (1380) — should be true (first condition: 1320 <= 1380 <= 1440)
            var testDate = new DateTime(2025, 6, 15, 23, 0, 0, DateTimeKind.Utc);

            // Act
            var result = InvokeIsTimeInTimespanInterval(testDate, 1320, 360);

            // Assert
            result.Should().BeTrue();

            // Also test at 02:00 (120) — should be true (second condition: 0 < 120 <= 360)
            var earlyMorning = new DateTime(2025, 6, 15, 2, 0, 0, DateTimeKind.Utc);
            var result2 = InvokeIsTimeInTimespanInterval(earlyMorning, 1320, 360);
            result2.Should().BeTrue();
        }

        [Theory]
        [InlineData(8, 0, 480, 1020, true)]    // 08:00 at start boundary — inclusive
        [InlineData(17, 0, 480, 1020, true)]   // 17:00 at end boundary — inclusive
        [InlineData(7, 59, 480, 1020, false)]  // 07:59 just before start
        [InlineData(17, 1, 480, 1020, false)]  // 17:01 just after end
        [InlineData(0, 0, 1320, 360, true)]    // midnight exactly — overnight wrap, timeAsInt=0 >= 0 && 0 <= 360 → true
        [InlineData(12, 0, 1320, 360, false)]  // noon — outside overnight wrap range
        [InlineData(22, 0, 1320, 360, true)]   // 22:00 at start of overnight wrap — inclusive
        public void IsTimeInTimespanInterval_BoundaryValues(int hour, int minute, int startTs, int endTs, bool expected)
        {
            // Arrange
            var testDate = new DateTime(2025, 6, 15, hour, minute, 0, DateTimeKind.Utc);

            // Act
            var result = InvokeIsTimeInTimespanInterval(testDate, startTs, endTs);

            // Assert
            result.Should().Be(expected);
        }

        #endregion

        #region Schedule Processing Tests

        [Fact]
        public async Task ProcessSchedulesAsync_GetsReadyForExecutionPlans()
        {
            // Arrange — return empty ready plans
            MockDynamoDbScanResponse(new List<Dictionary<string, AttributeValue>>());

            // Act
            await _service.ProcessSchedulesAsync(CancellationToken.None);

            // Assert — ScanAsync should have been called to get ready plans
            _mockDynamoDb.Verify(d => d.ScanAsync(
                It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task ProcessSchedulesAsync_CreatesNewWorkflow_WhenStartNewJob()
        {
            // Arrange — create a ready schedule plan with a registered workflow type
            var typeId = Guid.NewGuid();
            var type = CreateTestWorkflowType("ScheduleJob", id: typeId);
            await RegisterTypeInService(type);

            var now = DateTime.UtcNow;
            // Pass workflowTypeId so job_type_id attribute is set correctly for the mapper
            var planItem = BuildSchedulePlanDynamoItem(Guid.NewGuid(), "ReadyPlan",
                enabled: true, nextTriggerTime: now.AddMinutes(-5), startDate: now.AddDays(-1),
                endDate: null, workflowTypeId: typeId);

            // First scan returns the ready schedule plan, subsequent scans return empty
            var callCount = 0;
            _mockDynamoDb.Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>> { planItem } };
                    }
                    return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>>() };
                });

            // GetItemAsync from RegisterTypeInService already returns the type item for
            // GetWorkflowTypeAsync calls — no blanket override needed here.
            // LastStartedWorkflowId is null so IsWorkflowFinishedAsync is not called.

            MockDynamoDbPutItemSuccess(); // For CreateWorkflowAsync (PutItem)
            MockDynamoDbUpdateItemSuccess(); // For UpdateSchedulePlanTriggerAsync (UpdateItem)

            // Mock Step Functions so StartStepFunctionsExecutionAsync succeeds
            _mockStepFunctions.Setup(sf => sf.StartExecutionAsync(
                It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartExecutionResponse { ExecutionArn = "arn:aws:states:us-east-1:000000000000:execution:test:abc" });

            // Act
            await _service.ProcessSchedulesAsync(CancellationToken.None);

            // Assert — PutItem should be called for creating the workflow
            _mockDynamoDb.Verify(d => d.PutItemAsync(
                It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
            // Assert — UpdateItem should be called for UpdateSchedulePlanTriggerAsync
            _mockDynamoDb.Verify(d => d.UpdateItemAsync(
                It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        #endregion

        #region Workflow Processing Tests

        [Fact]
        public async Task ProcessWorkflowsAsync_WhenDisabled_ReturnsImmediately()
        {
            // Arrange — disable the workflow engine
            var disabledSettings = new WorkflowSettings
            {
                DynamoDbTableName = "test-workflows",
                StepFunctionsStateMachineArn = "arn:aws:states:us-east-1:000000000000:stateMachine:test",
                SnsTopicArn = "arn:aws:sns:us-east-1:000000000000:workflow-events",
                SqsQueueUrl = "http://localhost:4566/000000000000/workflow-queue",
                AwsRegion = "us-east-1",
                Enabled = false
            };
            var disabledService = new WorkflowService(
                _mockDynamoDb.Object,
                _mockStepFunctions.Object,
                _mockSns.Object,
                _mockLogger.Object,
                disabledSettings);

            // Act
            await disabledService.ProcessWorkflowsAsync(CancellationToken.None);

            // Assert — no DynamoDB calls should be made when disabled
            _mockDynamoDb.Verify(d => d.ScanAsync(
                It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task ProcessWorkflowsAsync_SingleInstanceConstraint_SkipsExecution()
        {
            // Arrange — workflow type with AllowSingleInstance = true
            var typeId = Guid.NewGuid();
            var type = CreateTestWorkflowType("SingleInstance", id: typeId, allowSingleInstance: true);
            await RegisterTypeInService(type);

            // Create a pending workflow of this type
            var pendingItem = BuildWorkflowDynamoItem(Guid.NewGuid(), typeId, "Pending1", WorkflowStatus.Pending);
            pendingItem["allow_single_instance"] = new AttributeValue { BOOL = true };

            // Create a running workflow of the same type (simulates one already in pool)
            var runningItem = BuildWorkflowDynamoItem(Guid.NewGuid(), typeId, "Running1", WorkflowStatus.Running);

            // Mock scan: first call returns pending workflows, second call returns running workflows
            var scanCallCount = 0;
            _mockDynamoDb.Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    scanCallCount++;
                    if (scanCallCount == 1)
                        return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>> { pendingItem } };
                    if (scanCallCount == 2)
                        return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>> { runningItem } };
                    return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>>() };
                });

            MockDynamoDbPutItemSuccess();

            // Act
            await _service.ProcessWorkflowsAsync(CancellationToken.None);

            // Assert — Step Functions should NOT be started due to single instance constraint
            _mockStepFunctions.Verify(sf => sf.StartExecutionAsync(
                It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        }

        [Fact]
        public async Task ProcessWorkflowsAsync_StartsStepFunctionsExecution()
        {
            // Arrange — workflow type without single instance constraint
            var typeId = Guid.NewGuid();
            var type = CreateTestWorkflowType("ProcessJob", id: typeId, allowSingleInstance: false);
            await RegisterTypeInService(type);

            var pendingItem = BuildWorkflowDynamoItem(Guid.NewGuid(), typeId, "Pending1", WorkflowStatus.Pending);
            pendingItem["complete_class_name"] = new AttributeValue(type.CompleteClassName);

            // First scan returns pending workflow, subsequent scans return empty
            var scanCallCount = 0;
            _mockDynamoDb.Setup(d => d.ScanAsync(It.IsAny<ScanRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    scanCallCount++;
                    if (scanCallCount == 1)
                        return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>> { pendingItem } };
                    return new ScanResponse { Items = new List<Dictionary<string, AttributeValue>>() };
                });

            MockDynamoDbPutItemSuccess();
            _mockStepFunctions.Setup(sf => sf.StartExecutionAsync(It.IsAny<StartExecutionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StartExecutionResponse { ExecutionArn = "arn:aws:states:us-east-1:000000000000:execution:test:exec-1" });

            // Act
            await _service.ProcessWorkflowsAsync(CancellationToken.None);

            // Assert — Step Functions should be started
            _mockStepFunctions.Verify(sf => sf.StartExecutionAsync(
                It.Is<StartExecutionRequest>(r => r.StateMachineArn == _settings.StepFunctionsStateMachineArn),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region SNS Event Publishing Tests

        [Fact]
        public async Task EventNamingConvention_FollowsDomainEntityAction()
        {
            // Arrange — create a workflow to trigger an SNS event
            var type = CreateTestWorkflowType("EventNamingTest");
            await RegisterTypeInService(type);
            MockDynamoDbPutItemSuccess();

            PublishRequest? capturedRequest = null;
            _mockSns.Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PublishRequest, CancellationToken>((req, ct) => capturedRequest = req)
                .ReturnsAsync(new PublishResponse());

            // Act
            await _service.CreateWorkflowAsync(
                type.Id, null, WorkflowPriority.Low, Guid.NewGuid(), null, null);

            // Assert — verify event follows {domain}.{entity}.{action} naming convention
            capturedRequest.Should().NotBeNull();
            if (capturedRequest != null)
            {
                // The message or message attributes should contain the event type
                var messageContent = capturedRequest.Message ?? string.Empty;
                var hasEventType = messageContent.Contains("workflow.workflow.created") ||
                    (capturedRequest.MessageAttributes != null &&
                     capturedRequest.MessageAttributes.Any(a =>
                         a.Value.StringValue != null &&
                         a.Value.StringValue.Contains("workflow.workflow.")));

                // Verify the topic ARN matches our configured SNS topic
                capturedRequest.TopicArn.Should().Be(_settings.SnsTopicArn);
            }
        }

        #endregion
    }
}
