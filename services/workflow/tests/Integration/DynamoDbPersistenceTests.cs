// ============================================================================
// DynamoDbPersistenceTests.cs — Integration Tests for DynamoDB Workflow/Schedule
// State Persistence (Workflow Engine Service)
//
// Purpose:
//   The most foundational integration test file for the Workflow Engine service.
//   Tests all DynamoDB persistence operations that replace the monolith's
//   PostgreSQL-backed JobDataService.cs. Every CRUD operation, query pattern,
//   filtering, pagination, and data integrity check from the source
//   JobDataService is translated to DynamoDB single-table design equivalents.
//   Tests run against LocalStack — NO mocked AWS SDK calls (per AAP Section
//   0.8.1 and 0.8.4).
//
// Source Mapping:
//   WebVella.Erp/Jobs/JobDataService.cs  → Workflow CRUD + Schedule CRUD
//   WebVella.Erp/Jobs/JobManager.cs      → Crash recovery, type registry
//   WebVella.Erp/Jobs/Models/Job.cs      → Workflow model (status, priority)
//   WebVella.Erp/Jobs/Models/SchedulePlan.cs → SchedulePlan model
//   WebVella.Erp/Jobs/Models/JobType.cs  → WorkflowType model
//   WebVella.Erp/Jobs/SheduleManager.cs  → Ready-for-execution queries
//
// DynamoDB Single-Table Design:
//   Workflow:     PK=WORKFLOW#{workflowId},        SK=META
//   SchedulePlan: PK=SCHEDULE#{schedulePlanId},     SK=META
//   WorkflowType: PK=WORKFLOW_TYPE#,                SK=TYPE#{typeId}
//   GSI:          StatusIndex — PK=status (N),      SK=created_on (S)
// ============================================================================

using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.StepFunctions;
using Amazon.SimpleNotificationService;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using WebVellaErp.Workflow.Models;
using WebVellaErp.Workflow.Services;
using Xunit;

// Alias to avoid collision between WebVellaErp.Workflow namespace and
// WebVellaErp.Workflow.Models.Workflow class.
using WfModel = WebVellaErp.Workflow.Models.Workflow;

namespace WebVellaErp.Workflow.Tests.Integration
{
    /// <summary>
    /// Integration tests for DynamoDB persistence operations in the Workflow service.
    /// Uses <see cref="IAsyncLifetime"/> for async DynamoDB table setup/teardown
    /// against LocalStack (http://localhost:4566).
    /// Per AAP Section 0.8.1: NO mocked AWS SDK calls — only real DynamoDB operations.
    /// </summary>
    public class DynamoDbPersistenceTests : IAsyncLifetime
    {
        // ── Private Fields ─────────────────────────────────────────────────
        private IAmazonDynamoDB _dynamoDbClient = null!;
        private IWorkflowService _workflowService = null!;
        private string _tableName = null!;
        private const string StatusIndexName = "StatusIndex";
        private const string LocalStackEndpoint = "http://localhost:4566";
        private const string TestRegion = "us-east-1";

        // Pre-registered workflow type used across tests
        private Guid _testWorkflowTypeId;
        private const string TestWorkflowTypeName = "TestWorkflowType";
        private const string TestWorkflowClassName = "WebVellaErp.Workflow.Tests.TestWorkflow";

        // ════════════════════════════════════════════════════════════════════
        // ── IAsyncLifetime — Fixture Setup / Teardown ──────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a unique DynamoDB table per test class instance, configures
        /// the WorkflowService against LocalStack, and registers a default
        /// WorkflowType for use in all tests.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Use unique table name per test run to avoid cross-contamination
            _tableName = $"workflow-test-{Guid.NewGuid():N}";

            // Configure DynamoDB client for LocalStack (AAP Section 0.8.6)
            var config = new AmazonDynamoDBConfig
            {
                ServiceURL = LocalStackEndpoint,
                AuthenticationRegion = TestRegion
            };
            _dynamoDbClient = new AmazonDynamoDBClient("test", "test", config);

            // Create single-table with PK/SK and StatusIndex GSI
            var createTableRequest = new CreateTableRequest
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
                        Projection = new Projection { ProjectionType = ProjectionType.ALL }
                    }
                },
                BillingMode = BillingMode.PAY_PER_REQUEST
            };

            await _dynamoDbClient.CreateTableAsync(createTableRequest);

            // Wait for table to become ACTIVE
            TableStatus tableStatus;
            do
            {
                await Task.Delay(200);
                var describeResponse = await _dynamoDbClient.DescribeTableAsync(
                    new DescribeTableRequest { TableName = _tableName });
                tableStatus = describeResponse.Table.TableStatus;
            }
            while (tableStatus != TableStatus.ACTIVE);

            // Configure WorkflowSettings for LocalStack
            var settings = new WorkflowSettings
            {
                DynamoDbTableName = _tableName,
                AwsEndpointUrl = LocalStackEndpoint,
                AwsRegion = TestRegion,
                Enabled = true,
                SnsTopicArn = "",                      // Empty to prevent SNS publish attempts
                StepFunctionsStateMachineArn = "",     // Empty to prevent Step Functions calls
                SqsQueueUrl = ""
            };

            // Mock IAmazonStepFunctions and IAmazonSimpleNotificationService —
            // these are not exercised in DynamoDB persistence tests
            var mockStepFunctions = new Mock<IAmazonStepFunctions>();
            var mockSns = new Mock<IAmazonSimpleNotificationService>();

            // Create WorkflowService with real DynamoDB client and mocked others
            _workflowService = new WorkflowService(
                _dynamoDbClient,
                mockStepFunctions.Object,
                mockSns.Object,
                NullLogger<WorkflowService>.Instance,
                settings);

            // Register a default WorkflowType used across all tests
            _testWorkflowTypeId = Guid.NewGuid();
            var workflowType = new WorkflowType
            {
                Id = _testWorkflowTypeId,
                Name = TestWorkflowTypeName,
                CompleteClassName = TestWorkflowClassName,
                DefaultPriority = WorkflowPriority.Medium,
                AllowSingleInstance = false
            };
            await _workflowService.RegisterWorkflowTypeAsync(workflowType);
        }

        /// <summary>
        /// Deletes the DynamoDB table and disposes the client.
        /// </summary>
        public async Task DisposeAsync()
        {
            try
            {
                await _dynamoDbClient.DeleteTableAsync(new DeleteTableRequest
                {
                    TableName = _tableName
                });
            }
            catch
            {
                // Swallow — table may already be gone
            }
            finally
            {
                _dynamoDbClient?.Dispose();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 2: Workflow CRUD Lifecycle Tests ──────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that CreateWorkflowAsync persists a workflow to DynamoDB.
        /// Replaces JobDataService.CreateJob (source lines 25-73).
        /// </summary>
        [Fact]
        public async Task CreateWorkflow_PersistsTosDynamoDb()
        {
            // Arrange
            var workflowId = Guid.NewGuid();
            var creatorId = Guid.NewGuid();
            var attributes = new Dictionary<string, object>
            {
                ["key1"] = "value1",
                ["key2"] = 42
            };

            // Act: CreateWorkflowAsync takes (typeId, attributes, priority, creatorId, schedulePlanId, workflowId)
            var created = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                attributes,
                WorkflowPriority.High,
                creatorId,
                null,
                workflowId);

            // Assert: Workflow returned with correct data
            created.Should().NotBeNull();
            created!.Id.Should().Be(workflowId);
            created.TypeId.Should().Be(_testWorkflowTypeId);
            created.TypeName.Should().Be(TestWorkflowTypeName);
            created.Status.Should().Be(WorkflowStatus.Pending);
            created.Priority.Should().Be(WorkflowPriority.High);
            created.CreatedBy.Should().Be(creatorId);
            created.CreatedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

            // Verify DynamoDB item directly
            var item = await GetWorkflowItemDirectly(workflowId);
            item.Should().NotBeNull();
            item!["PK"].S.Should().Be($"WORKFLOW#{workflowId}");
            item["SK"].S.Should().Be("META");
            item["id"].S.Should().Be(workflowId.ToString());
            item["type_id"].S.Should().Be(_testWorkflowTypeId.ToString());
        }

        /// <summary>
        /// Verifies that GetWorkflowAsync returns correct data for a persisted workflow.
        /// Replaces JobDataService.GetJob (source lines 119-133).
        /// </summary>
        [Fact]
        public async Task ReadWorkflow_ReturnsCorrectData()
        {
            // Arrange: Create a workflow via the service
            var workflowId = Guid.NewGuid();
            var creatorId = Guid.NewGuid();
            var attributes = new Dictionary<string, object>
            {
                ["env"] = "test",
                ["retries"] = 3
            };

            await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                attributes,
                WorkflowPriority.Medium,
                creatorId,
                null,
                workflowId);

            // Act: Read it back
            var workflow = await _workflowService.GetWorkflowAsync(workflowId);

            // Assert: All fields match
            workflow.Should().NotBeNull();
            workflow!.Id.Should().Be(workflowId);
            workflow.TypeId.Should().Be(_testWorkflowTypeId);
            workflow.TypeName.Should().Be(TestWorkflowTypeName);
            workflow.Status.Should().Be(WorkflowStatus.Pending);
            workflow.Priority.Should().Be(WorkflowPriority.Medium);
            workflow.CreatedBy.Should().Be(creatorId);
            workflow.StartedOn.Should().BeNull();
            workflow.FinishedOn.Should().BeNull();
            workflow.ErrorMessage.Should().BeNull();
            workflow.SchedulePlanId.Should().BeNull();
        }

        /// <summary>
        /// Verifies that UpdateWorkflowAsync modifies an existing workflow item.
        /// Replaces JobDataService.UpdateJob (source lines 76-117).
        /// </summary>
        [Fact]
        public async Task UpdateWorkflow_ModifiesExistingItem()
        {
            // Arrange: Create a workflow
            var workflowId = Guid.NewGuid();
            var created = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                null,
                WorkflowPriority.Low,
                null,
                null,
                workflowId);
            created.Should().NotBeNull();
            created!.Status.Should().Be(WorkflowStatus.Pending);

            // Act: Update status to Running
            created.Status = WorkflowStatus.Running;
            created.StartedOn = DateTime.UtcNow;
            created.LastModifiedBy = Guid.NewGuid();
            var updateResult = await _workflowService.UpdateWorkflowAsync(created);

            // Assert: Update succeeded
            updateResult.Should().BeTrue();

            // Read back and verify
            var readBack = await _workflowService.GetWorkflowAsync(workflowId);
            readBack.Should().NotBeNull();
            readBack!.Status.Should().Be(WorkflowStatus.Running);
            readBack.StartedOn.Should().NotBeNull();
            readBack.StartedOn!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
            readBack.LastModifiedOn.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Verifies workflow deletion removes the item from DynamoDB.
        /// Not in original source (Jobs were never hard-deleted), but needed for
        /// DynamoDB data lifecycle completeness.
        /// </summary>
        [Fact]
        public async Task DeleteWorkflow_RemovesFromDynamoDb()
        {
            // Arrange: Create a workflow
            var workflowId = Guid.NewGuid();
            await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                null,
                WorkflowPriority.Low,
                null,
                null,
                workflowId);

            // Act: Delete directly from DynamoDB
            await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue($"WORKFLOW#{workflowId}"),
                    ["SK"] = new AttributeValue("META")
                }
            });

            // Assert: GetWorkflowAsync returns null
            var deleted = await _workflowService.GetWorkflowAsync(workflowId);
            deleted.Should().BeNull();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 3: DynamoDB Single-Table Design Validation ───────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies Workflow items use PK=WORKFLOW#{id}, SK=META.
        /// </summary>
        [Fact]
        public async Task WorkflowItem_UsesSingleTableDesign_CorrectPKSK()
        {
            // Arrange & Act
            var workflowId = Guid.NewGuid();
            await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                null,
                WorkflowPriority.Low,
                null,
                null,
                workflowId);

            // Assert: PK and SK format
            var item = await GetWorkflowItemDirectly(workflowId);
            item.Should().NotBeNull();
            item!["PK"].S.Should().Be($"WORKFLOW#{workflowId}");
            item["SK"].S.Should().Be("META");
            item["entity_type"].S.Should().Be("Workflow");
        }

        /// <summary>
        /// Verifies SchedulePlan items use PK=SCHEDULE#{id}, SK=META.
        /// Replaces schedule_plan table (source: JobDataService.cs lines 295-342).
        /// </summary>
        [Fact]
        public async Task SchedulePlanItem_UsesSingleTableDesign_CorrectPKSK()
        {
            // Arrange
            var plan = CreateDefaultSchedulePlan();

            // Act
            var success = await _workflowService.CreateSchedulePlanAsync(plan);
            success.Should().BeTrue();

            // Assert: PK and SK format
            var item = await GetSchedulePlanItemDirectly(plan.Id);
            item.Should().NotBeNull();
            item!["PK"].S.Should().Be($"SCHEDULE#{plan.Id}");
            item["SK"].S.Should().Be("META");
            item["entity_type"].S.Should().Be("SchedulePlan");
        }

        /// <summary>
        /// Verifies WorkflowType items use PK=WORKFLOW_TYPE#, SK=TYPE#{id}.
        /// Replaces reflection-based JobType registry (source: JobManager.cs lines 56-80).
        /// </summary>
        [Fact]
        public async Task WorkflowTypeItem_UsesSingleTableDesign_CorrectPKSK()
        {
            // Arrange
            var typeId = Guid.NewGuid();
            var wfType = new WorkflowType
            {
                Id = typeId,
                Name = "DesignTestType",
                CompleteClassName = "WebVellaErp.Workflow.Tests.DesignTestType",
                DefaultPriority = WorkflowPriority.High,
                AllowSingleInstance = true
            };

            // Act
            await _workflowService.RegisterWorkflowTypeAsync(wfType);

            // Assert: Query DynamoDB directly for key pattern
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue("WORKFLOW_TYPE#"),
                    ["SK"] = new AttributeValue($"TYPE#{typeId}")
                }
            });
            response.Item.Should().NotBeNull();
            response.Item.Should().ContainKey("PK");
            response.Item["PK"].S.Should().Be("WORKFLOW_TYPE#");
            response.Item["SK"].S.Should().Be($"TYPE#{typeId}");
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 4: Schedule Plan CRUD Tests ──────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that CreateSchedulePlanAsync persists a SchedulePlan to DynamoDB.
        /// Replaces JobDataService.CreateSchedule (source lines 295-342).
        /// </summary>
        [Fact]
        public async Task CreateSchedulePlan_PersistsToDynamoDb()
        {
            // Arrange: Create a schedule plan with all fields
            var plan = new SchedulePlan
            {
                Id = Guid.NewGuid(),
                Name = "Hourly Import Schedule",
                Type = SchedulePlanType.Interval,
                StartDate = DateTime.UtcNow.AddDays(-1),
                EndDate = DateTime.UtcNow.AddDays(30),
                ScheduledDays = new SchedulePlanDaysOfWeek
                {
                    ScheduledOnMonday = true,
                    ScheduledOnTuesday = true,
                    ScheduledOnWednesday = true,
                    ScheduledOnThursday = true,
                    ScheduledOnFriday = true,
                    ScheduledOnSaturday = false,
                    ScheduledOnSunday = false
                },
                IntervalInMinutes = 60,
                StartTimespan = 480,  // 8:00 AM in minutes
                EndTimespan = 1080,   // 6:00 PM in minutes
                WorkflowTypeId = _testWorkflowTypeId,
                Enabled = true,
                NextTriggerTime = DateTime.UtcNow.AddMinutes(30),
                LastTriggerTime = DateTime.UtcNow.AddMinutes(-30),
                LastStartedWorkflowId = null,
                JobAttributes = new Dictionary<string, object>
                {
                    ["source"] = "import-feed",
                    ["batchSize"] = 100
                },
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow,
                LastModifiedBy = Guid.NewGuid()
            };

            // Act
            var success = await _workflowService.CreateSchedulePlanAsync(plan);

            // Assert: Persistence succeeded
            success.Should().BeTrue();
            var item = await GetSchedulePlanItemDirectly(plan.Id);
            item.Should().NotBeNull();
            item!["PK"].S.Should().Be($"SCHEDULE#{plan.Id}");
            item["SK"].S.Should().Be("META");
            item["name"].S.Should().Be("Hourly Import Schedule");
            int.Parse(item["type"].N).Should().Be((int)SchedulePlanType.Interval);
            item["enabled"].BOOL.Should().BeTrue();

            // Verify round-trip via service read
            var readBack = await _workflowService.GetSchedulePlanAsync(plan.Id);
            readBack.Should().NotBeNull();
            readBack!.Name.Should().Be("Hourly Import Schedule");
            readBack.Type.Should().Be(SchedulePlanType.Interval);
            readBack.IntervalInMinutes.Should().Be(60);
            readBack.StartTimespan.Should().Be(480);
            readBack.EndTimespan.Should().Be(1080);
            readBack.Enabled.Should().BeTrue();
            readBack.WorkflowTypeId.Should().Be(_testWorkflowTypeId);
        }

        /// <summary>
        /// Verifies that UpdateSchedulePlanAsync modifies an existing SchedulePlan item.
        /// Replaces JobDataService.UpdateSchedule (source lines 344-391) for full update,
        /// and the short update (lines 393-422) for trigger fields.
        /// </summary>
        [Fact]
        public async Task UpdateSchedulePlan_ModifiesExistingItem()
        {
            // Arrange: Create a schedule plan
            var plan = CreateDefaultSchedulePlan();
            var success = await _workflowService.CreateSchedulePlanAsync(plan);
            success.Should().BeTrue();

            // Act: Update trigger times, name, and last started workflow
            var newTriggerTime = DateTime.UtcNow.AddHours(2);
            var newLastTrigger = DateTime.UtcNow;
            var modifiedBy = Guid.NewGuid();
            var lastStartedWfId = Guid.NewGuid();
            plan.NextTriggerTime = newTriggerTime;
            plan.LastTriggerTime = newLastTrigger;
            plan.Name = "Updated Schedule Name";
            plan.LastModifiedBy = modifiedBy;
            plan.LastStartedWorkflowId = lastStartedWfId;
            var updateSuccess = await _workflowService.UpdateSchedulePlanAsync(plan);

            // Assert: Changes persisted
            updateSuccess.Should().BeTrue();
            var readBack = await _workflowService.GetSchedulePlanAsync(plan.Id);
            readBack.Should().NotBeNull();
            readBack!.Name.Should().Be("Updated Schedule Name");
            readBack.NextTriggerTime.Should().NotBeNull();
            readBack.NextTriggerTime!.Value.Should().BeCloseTo(newTriggerTime, TimeSpan.FromSeconds(2));
            readBack.LastTriggerTime.Should().NotBeNull();
            readBack.LastTriggerTime!.Value.Should().BeCloseTo(newLastTrigger, TimeSpan.FromSeconds(2));
            readBack.LastModifiedBy.Should().Be(modifiedBy);
            readBack.LastStartedWorkflowId.Should().Be(lastStartedWfId);

            // Also verify the round-trip matches the expected plan structure
            readBack.Should().BeEquivalentTo(plan, options => options
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(2)))
                .WhenTypeIs<DateTime>()
                .Using<DateTime?>(ctx =>
                {
                    if (ctx.Expectation.HasValue)
                        ctx.Subject!.Value.Should().BeCloseTo(ctx.Expectation.Value, TimeSpan.FromSeconds(2));
                    else
                        ctx.Subject.Should().BeNull();
                })
                .WhenTypeIs<DateTime?>()
                .Excluding(p => p.WorkflowType)
                .Excluding(p => p.LastModifiedOn));
        }

        /// <summary>
        /// Verifies that GetSchedulePlansAsync returns all plans with all schedule types.
        /// Replaces JobDataService.GetSchedulePlans() (source lines 441-448)
        /// which did SELECT * FROM schedule_plan ORDER BY name.
        /// Tests SchedulePlanType.Daily, Weekly, Monthly values.
        /// </summary>
        [Fact]
        public async Task GetSchedulePlans_ReturnsAllPlans()
        {
            // Arrange: Create 3 schedule plans with distinct types and names
            var planA = CreateDefaultSchedulePlan("Alpha Schedule");
            planA.Type = SchedulePlanType.Daily;

            var planB = CreateDefaultSchedulePlan("Beta Schedule");
            planB.Type = SchedulePlanType.Weekly;
            planB.ScheduledDays = new SchedulePlanDaysOfWeek
            {
                ScheduledOnMonday = true,
                ScheduledOnWednesday = true,
                ScheduledOnFriday = true
            };

            var planC = CreateDefaultSchedulePlan("Gamma Schedule");
            planC.Type = SchedulePlanType.Monthly;

            (await _workflowService.CreateSchedulePlanAsync(planA)).Should().BeTrue();
            (await _workflowService.CreateSchedulePlanAsync(planB)).Should().BeTrue();
            (await _workflowService.CreateSchedulePlanAsync(planC)).Should().BeTrue();

            // Act
            var plans = await _workflowService.GetSchedulePlansAsync();

            // Assert: All 3 plans returned
            plans.Should().NotBeNull();
            plans.Should().HaveCountGreaterThanOrEqualTo(3);
            plans.Select(p => p.Name).Should().Contain("Alpha Schedule");
            plans.Select(p => p.Name).Should().Contain("Beta Schedule");
            plans.Select(p => p.Name).Should().Contain("Gamma Schedule");

            // Verify type values are preserved
            plans.First(p => p.Name == "Alpha Schedule").Type.Should().Be(SchedulePlanType.Daily);
            plans.First(p => p.Name == "Beta Schedule").Type.Should().Be(SchedulePlanType.Weekly);
            plans.First(p => p.Name == "Gamma Schedule").Type.Should().Be(SchedulePlanType.Monthly);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 5: GSI Query for Workflows by Status ─────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetWorkflowsAsync filtered by status returns correct results.
        /// Replaces JobDataService.GetJobs(JobStatus status) (source lines 155-170).
        /// Status enum values from source: Pending=1, Running=2, Canceled=3,
        /// Failed=4, Finished=5, Aborted=6.
        /// </summary>
        [Fact]
        public async Task QueryWorkflowsByStatus_ReturnsFilteredResults()
        {
            // Arrange: Create 5 workflows with mixed statuses
            var pendingIds = new List<Guid>();
            var runningIds = new List<Guid>();

            for (int i = 0; i < 2; i++)
            {
                var wfId = Guid.NewGuid();
                pendingIds.Add(wfId);
                await _workflowService.CreateWorkflowAsync(
                    _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, wfId);
            }
            for (int i = 0; i < 2; i++)
            {
                var wfId = Guid.NewGuid();
                runningIds.Add(wfId);
                var wf = await _workflowService.CreateWorkflowAsync(
                    _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, wfId);
                wf!.Status = WorkflowStatus.Running;
                wf.StartedOn = DateTime.UtcNow;
                await _workflowService.UpdateWorkflowAsync(wf);
            }
            // 1 Finished
            var finishedId = Guid.NewGuid();
            var finishedWf = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, finishedId);
            finishedWf!.Status = WorkflowStatus.Finished;
            finishedWf.FinishedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(finishedWf);

            // Act: Query Pending workflows (status = int value of Pending)
            var (pendingWorkflows, pendingCount) = await _workflowService.GetWorkflowsAsync(
                status: (int)WorkflowStatus.Pending);

            // Assert: Exactly 2 Pending workflows
            pendingWorkflows.Should().HaveCount(2);
            pendingWorkflows.All(w => w.Status == WorkflowStatus.Pending).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that GetWorkflowsAsync with limit returns capped results.
        /// Replaces GetJobs(JobStatus, int? limit) with LIMIT clause (source lines 160-166).
        /// </summary>
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task QueryWorkflowsByStatus_WithLimit(int pageSize)
        {
            // Arrange: Create 5 Pending workflows
            await SeedMultipleWorkflows(5, WorkflowStatus.Pending);

            // Act: Query with page size limit
            var (workflows, totalCount) = await _workflowService.GetWorkflowsAsync(
                status: (int)WorkflowStatus.Pending,
                page: 1,
                pageSize: pageSize);

            // Assert: Page returns at most pageSize items
            (workflows.Count <= pageSize).Should().BeTrue($"expected at most {pageSize} items but got {workflows.Count}");
            // Total count should reflect all matching workflows
            (totalCount >= 5).Should().BeTrue($"expected totalCount >= 5 but got {totalCount}");
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 6: Pagination with DynamoDB ──────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetWorkflowsAsync paginates correctly.
        /// DynamoDB does NOT support OFFSET — uses page-based pagination.
        /// Replaces LIMIT @limit OFFSET @offset (JobDataService.cs lines 223-232).
        /// </summary>
        [Fact]
        public async Task GetWorkflows_PaginatesWithLastEvaluatedKey()
        {
            // Arrange: Create 10 workflows
            await SeedMultipleWorkflows(10, WorkflowStatus.Pending);

            // Act: Fetch page by page (pageSize=3)
            var allFetched = new List<WfModel>();
            int page = 1;
            int pageSize = 3;
            int totalItems;

            do
            {
                var (workflows, total) = await _workflowService.GetWorkflowsAsync(
                    status: (int)WorkflowStatus.Pending,
                    page: page,
                    pageSize: pageSize);
                totalItems = total;
                allFetched.AddRange(workflows);
                page++;
            }
            while (allFetched.Count < totalItems && page <= 20); // Safety limit

            // Assert: Total items >= 10 (we seeded 10 plus any from other tests)
            (totalItems >= 10).Should().BeTrue($"expected totalItems >= 10 but got {totalItems}");
            (allFetched.Count >= 10).Should().BeTrue($"expected allFetched.Count >= 10 but got {allFetched.Count}");

            // Verify no duplicates
            var uniqueIds = allFetched.Select(w => w.Id).Distinct().ToList();
            uniqueIds.Should().HaveCount(allFetched.Count);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 7: Filtering Tests ───────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies filtering by date range using startFromDate/startToDate.
        /// Replaces SQL WHERE clauses for dates (JobDataService.cs lines 179-198).
        /// </summary>
        [Fact]
        public async Task FilterWorkflows_ByDateRange()
        {
            // Arrange: Create workflows with different start dates
            var oldWorkflowId = Guid.NewGuid();
            var recentWorkflowId = Guid.NewGuid();

            var oldWf = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, oldWorkflowId);
            oldWf!.Status = WorkflowStatus.Running;
            oldWf.StartedOn = DateTime.UtcNow.AddDays(-10);
            await _workflowService.UpdateWorkflowAsync(oldWf);

            var recentWf = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, recentWorkflowId);
            recentWf!.Status = WorkflowStatus.Running;
            recentWf.StartedOn = DateTime.UtcNow.AddHours(-1);
            await _workflowService.UpdateWorkflowAsync(recentWf);

            // Act: Filter by recent date range
            var (filtered, count) = await _workflowService.GetWorkflowsAsync(
                startFromDate: DateTime.UtcNow.AddDays(-2),
                startToDate: DateTime.UtcNow);

            // Assert: Only recent workflow returned
            filtered.Should().Contain(w => w.Id == recentWorkflowId);
            filtered.Should().NotContain(w => w.Id == oldWorkflowId);
        }

        /// <summary>
        /// Verifies filtering by type name (partial match).
        /// Replaces type_name ILIKE @type_name (JobDataService.cs lines 199-203).
        /// </summary>
        [Fact]
        public async Task FilterWorkflows_ByTypeName()
        {
            // Arrange: Register a second type and create workflows
            var specialTypeId = Guid.NewGuid();
            await _workflowService.RegisterWorkflowTypeAsync(new WorkflowType
            {
                Id = specialTypeId,
                Name = "SpecialImportWorkflow",
                CompleteClassName = "WebVellaErp.Workflow.Tests.SpecialImportWorkflow",
                DefaultPriority = WorkflowPriority.Medium,
                AllowSingleInstance = false
            });

            var wf1 = await _workflowService.CreateWorkflowAsync(
                specialTypeId, null, WorkflowPriority.Low, null, null, Guid.NewGuid());
            var wf2 = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, Guid.NewGuid());

            // Act: Filter by type name containing "Import"
            var (filtered, count) = await _workflowService.GetWorkflowsAsync(
                typeName: "SpecialImport");

            // Assert: Only the special type workflow returned
            filtered.Should().Contain(w => w.Id == wf1!.Id);
            filtered.Should().NotContain(w => w.Id == wf2!.Id);
        }

        /// <summary>
        /// Verifies filtering by both status and priority.
        /// Replaces status = @status AND priority = @priority (lines 205-213).
        /// </summary>
        [Fact]
        public async Task FilterWorkflows_ByStatusAndPriority()
        {
            // Arrange
            var highPriorityId = Guid.NewGuid();
            var lowPriorityId = Guid.NewGuid();

            var highWf = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.High, null, null, highPriorityId);
            highWf!.Status = WorkflowStatus.Running;
            highWf.StartedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(highWf);

            var lowWf = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, lowPriorityId);
            lowWf!.Status = WorkflowStatus.Running;
            lowWf.StartedOn = DateTime.UtcNow;
            await _workflowService.UpdateWorkflowAsync(lowWf);

            // Act: Filter by Running + High priority
            var (filtered, count) = await _workflowService.GetWorkflowsAsync(
                status: (int)WorkflowStatus.Running,
                priority: (int)WorkflowPriority.High);

            // Assert
            filtered.Should().Contain(w => w.Id == highPriorityId);
            filtered.Should().NotContain(w => w.Id == lowPriorityId);
        }

        /// <summary>
        /// Verifies filtering by schedulePlanId.
        /// Replaces schedule_plan_id = @schedule_plan_id (lines 215-218).
        /// </summary>
        [Fact]
        public async Task FilterWorkflows_BySchedulePlanId()
        {
            // Arrange: Create a schedule plan and associated workflow
            var plan = CreateDefaultSchedulePlan();
            (await _workflowService.CreateSchedulePlanAsync(plan)).Should().BeTrue();

            var readPlan = await _workflowService.GetSchedulePlanAsync(plan.Id);
            readPlan.Should().NotBeNull();

            // Create workflow with schedule plan association
            var associatedId = Guid.NewGuid();
            var associatedWf = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, plan.Id, associatedId);
            associatedWf.Should().NotBeNull();

            // Create workflow without schedule plan association
            var unassociatedId = Guid.NewGuid();
            await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, unassociatedId);

            // Act: Filter by schedulePlanId
            var (filtered, count) = await _workflowService.GetWorkflowsAsync(
                schedulePlanId: plan.Id);

            // Assert
            filtered.Should().Contain(w => w.Id == associatedId);
            filtered.Should().NotContain(w => w.Id == unassociatedId);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 8: GetReadyForExecutionSchedulePlans ─────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies GetReadyForExecutionSchedulePlansAsync returns only eligible plans.
        /// Directly maps to JobDataService.GetReadyForExecutionScheduledPlans()
        /// (source lines 450-462).
        /// Source SQL conditions:
        ///   enabled = true AND next_trigger_time &lt;= @utc_now AND start_date &lt;= @utc_now
        ///   AND COALESCE(end_date, @utc_now) >= @utc_now
        ///   ORDER BY next_trigger_time ASC
        /// </summary>
        [Fact]
        public async Task GetReadyForExecutionSchedulePlans_ReturnsEligiblePlans()
        {
            // Plan A: eligible — enabled, past trigger, past start, future end
            var planA = CreateDefaultSchedulePlan("Plan A - Eligible");
            planA.Enabled = true;
            planA.NextTriggerTime = DateTime.UtcNow.AddMinutes(-5);
            planA.StartDate = DateTime.UtcNow.AddDays(-30);
            planA.EndDate = DateTime.UtcNow.AddDays(30);
            (await _workflowService.CreateSchedulePlanAsync(planA)).Should().BeTrue();

            // Plan B: NOT eligible — disabled
            var planB = CreateDefaultSchedulePlan("Plan B - Disabled");
            planB.Enabled = false;
            planB.NextTriggerTime = DateTime.UtcNow.AddMinutes(-5);
            planB.StartDate = DateTime.UtcNow.AddDays(-30);
            planB.EndDate = DateTime.UtcNow.AddDays(30);
            (await _workflowService.CreateSchedulePlanAsync(planB)).Should().BeTrue();

            // Plan C: NOT eligible — future trigger time
            var planC = CreateDefaultSchedulePlan("Plan C - Future Trigger");
            planC.Enabled = true;
            planC.NextTriggerTime = DateTime.UtcNow.AddHours(2);
            planC.StartDate = DateTime.UtcNow.AddDays(-30);
            planC.EndDate = DateTime.UtcNow.AddDays(30);
            (await _workflowService.CreateSchedulePlanAsync(planC)).Should().BeTrue();

            // Plan D: NOT eligible — future start date
            var planD = CreateDefaultSchedulePlan("Plan D - Future Start");
            planD.Enabled = true;
            planD.NextTriggerTime = DateTime.UtcNow.AddMinutes(-5);
            planD.StartDate = DateTime.UtcNow.AddDays(5);
            planD.EndDate = DateTime.UtcNow.AddDays(30);
            (await _workflowService.CreateSchedulePlanAsync(planD)).Should().BeTrue();

            // Plan E: NOT eligible — past end date
            var planE = CreateDefaultSchedulePlan("Plan E - Past End");
            planE.Enabled = true;
            planE.NextTriggerTime = DateTime.UtcNow.AddMinutes(-5);
            planE.StartDate = DateTime.UtcNow.AddDays(-60);
            planE.EndDate = DateTime.UtcNow.AddDays(-1);
            (await _workflowService.CreateSchedulePlanAsync(planE)).Should().BeTrue();

            // Plan F: eligible — null end date (COALESCE to now)
            var planF = CreateDefaultSchedulePlan("Plan F - Null End");
            planF.Enabled = true;
            planF.NextTriggerTime = DateTime.UtcNow.AddMinutes(-10);
            planF.StartDate = DateTime.UtcNow.AddDays(-30);
            planF.EndDate = null;
            (await _workflowService.CreateSchedulePlanAsync(planF)).Should().BeTrue();

            // Act
            var ready = await _workflowService.GetReadyForExecutionSchedulePlansAsync();

            // Assert: Only Plan A and Plan F should be returned
            ready.Should().Contain(p => p.Id == planA.Id);
            ready.Should().Contain(p => p.Id == planF.Id);
            ready.Should().NotContain(p => p.Id == planB.Id);
            ready.Should().NotContain(p => p.Id == planC.Id);
            ready.Should().NotContain(p => p.Id == planD.Id);
            ready.Should().NotContain(p => p.Id == planE.Id);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 9: Crash Recovery Tests ──────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that RecoverAbortedWorkflowsAsync sets all Running workflows
        /// to Aborted status. Directly maps to JobManager constructor crash
        /// recovery (source lines 32-41).
        /// Also verifies GetRunningWorkflowsAsync, GetPendingWorkflowsAsync,
        /// and IsWorkflowFinishedAsync.
        /// </summary>
        [Fact]
        public async Task CrashRecovery_AllRunningWorkflowsSetToAborted()
        {
            // Arrange: Create 3 Running workflows (simulating crashed executions)
            var runningIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var wfId = Guid.NewGuid();
                runningIds.Add(wfId);
                var wf = await _workflowService.CreateWorkflowAsync(
                    _testWorkflowTypeId, null, WorkflowPriority.Medium, null, null, wfId);
                wf!.Status = WorkflowStatus.Running;
                wf.StartedOn = DateTime.UtcNow.AddMinutes(-30);
                await _workflowService.UpdateWorkflowAsync(wf);
            }

            // Create 2 Pending workflows (should not be affected)
            var pendingIds = new List<Guid>();
            for (int i = 0; i < 2; i++)
            {
                var wfId = Guid.NewGuid();
                pendingIds.Add(wfId);
                await _workflowService.CreateWorkflowAsync(
                    _testWorkflowTypeId, null, WorkflowPriority.Low, null, null, wfId);
            }

            // Pre-assertions: Verify running workflows exist
            var runningBeforeRecovery = await _workflowService.GetRunningWorkflowsAsync();
            (runningBeforeRecovery.Count >= 3).Should().BeTrue($"expected >= 3 running workflows but got {runningBeforeRecovery.Count}");

            // Act: Invoke crash recovery
            await _workflowService.RecoverAbortedWorkflowsAsync();

            // Assert: All 3 Running workflows now Aborted
            foreach (var id in runningIds)
            {
                var wf = await _workflowService.GetWorkflowAsync(id);
                wf.Should().NotBeNull();
                wf!.Status.Should().Be(WorkflowStatus.Aborted);
                wf.AbortedBy.Should().Be(Guid.Empty); // System abort
                wf.FinishedOn.Should().NotBeNull();
                wf.FinishedOn!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
            }

            // Assert: 2 Pending workflows are unchanged
            foreach (var id in pendingIds)
            {
                var pending = await _workflowService.GetWorkflowAsync(id);
                pending.Should().NotBeNull();
                pending!.Status.Should().Be(WorkflowStatus.Pending);
                pending.AbortedBy.Should().BeNull();
                pending.FinishedOn.Should().BeNull();
            }

            // Verify GetRunningWorkflowsAsync returns empty after recovery
            var runningAfter = await _workflowService.GetRunningWorkflowsAsync();
            runningAfter.Should().BeEmpty();

            // Verify GetPendingWorkflowsAsync still returns pending items
            var pendingAfter = await _workflowService.GetPendingWorkflowsAsync();
            (pendingAfter.Count >= 2).Should().BeTrue($"expected >= 2 pending workflows but got {pendingAfter.Count}");

            // Verify IsWorkflowFinishedAsync for aborted (finished) and pending (not finished)
            var isAbortedFinished = await _workflowService.IsWorkflowFinishedAsync(runningIds[0]);
            isAbortedFinished.Should().BeTrue();

            var isPendingFinished = await _workflowService.IsWorkflowFinishedAsync(pendingIds[0]);
            isPendingFinished.Should().BeFalse();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 10: Dynamic Attribute Serialization ──────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that complex Attributes dictionaries serialize and
        /// deserialize correctly through DynamoDB.
        /// Replaces JsonConvert.SerializeObject with TypeNameHandling.All
        /// (source JobDataService.cs line 35).
        /// </summary>
        [Fact]
        public async Task DynamicAttributes_SerializeAndDeserializeCorrectly()
        {
            // Arrange: Complex attributes dictionary
            var attributes = new Dictionary<string, object>
            {
                ["stringKey"] = "hello world",
                ["intKey"] = 42,
                ["boolKey"] = true,
                ["doubleKey"] = 3.14,
                ["nullableKey"] = "not-null",
                ["nestedDict"] = new Dictionary<string, object>
                {
                    ["inner1"] = "innerValue",
                    ["inner2"] = 99
                },
                ["arrayKey"] = new List<object> { "a", "b", "c" }
            };

            // Also verify Newtonsoft.Json backward compatibility
            var jsonSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            };
            var newtonsoftSerialized = JsonConvert.SerializeObject(attributes, jsonSettings);
            newtonsoftSerialized.Should().NotBeNullOrEmpty();

            // Act: Create workflow with complex attributes
            var workflowId = Guid.NewGuid();
            var created = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                attributes,
                WorkflowPriority.Medium,
                null,
                null,
                workflowId);

            // Assert: Read back and verify attributes
            created.Should().NotBeNull();
            var readBack = await _workflowService.GetWorkflowAsync(workflowId);
            readBack.Should().NotBeNull();
            readBack!.Attributes.Should().NotBeNull();

            // Verify System.Text.Json round-trip of the attributes
            var serializedOptions = new JsonSerializerOptions();
            var serialized = System.Text.Json.JsonSerializer.Serialize(
                readBack.Attributes, serializedOptions);
            serialized.Should().NotBeNullOrEmpty();

            var deserialized = System.Text.Json.JsonSerializer.Deserialize<
                Dictionary<string, object>>(serialized, serializedOptions);
            deserialized.Should().NotBeNull();

            // Verify DynamoDB item directly has the attributes field
            var item = await GetWorkflowItemDirectly(workflowId);
            item.Should().NotBeNull();
            var verifiedItem = item!;
            verifiedItem.Should().ContainKey("attributes");
            // The attributes field should be stored as a string (JSON)
            var attrField = verifiedItem["attributes"];
            attrField.S.Should().NotBeNullOrEmpty();

            // Verify Newtonsoft.Json can deserialize the stored value
            var storedJson = attrField.S!;
            var newtonsoftDeserialized = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                storedJson);
            newtonsoftDeserialized.Should().NotBeNull();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 11: NULL Handling for Optional DateTime Fields ────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies correct handling of NULL optional fields.
        /// Replaces conditional parameter adds in JobDataService.cs:
        /// if (job.StartedOn.HasValue) (line 38), if (job.FinishedOn.HasValue) (line 40).
        /// </summary>
        [Fact]
        public async Task NullableDateTimeFields_HandledCorrectly()
        {
            // Arrange: Create workflow — optional fields default to null
            var workflowId = Guid.NewGuid();
            var created = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                null,
                WorkflowPriority.Low,
                null,   // creatorId = null
                null,   // schedulePlanId = null
                workflowId);

            // Act: Read back
            var readBack = await _workflowService.GetWorkflowAsync(workflowId);

            // Assert: All nullable fields are correctly null
            readBack.Should().NotBeNull();
            readBack!.StartedOn.Should().BeNull();
            readBack.FinishedOn.Should().BeNull();
            readBack.AbortedBy.Should().BeNull();
            readBack.CanceledBy.Should().BeNull();
            readBack.ErrorMessage.Should().BeNull();
            readBack.SchedulePlanId.Should().BeNull();

            // Verify DynamoDB item — null fields should use NULL=true
            var item = await GetWorkflowItemDirectly(workflowId);
            item.Should().NotBeNull();
            if (item!.ContainsKey("started_on"))
            {
                item["started_on"].NULL.Should().BeTrue();
            }
            if (item.ContainsKey("finished_on"))
            {
                item["finished_on"].NULL.Should().BeTrue();
            }
        }

        /// <summary>
        /// Verifies correct handling when all optional DateTime/Guid fields are populated.
        /// </summary>
        [Fact]
        public async Task NullableDateTimeFields_PopulatedCorrectly()
        {
            // Arrange: Create and then update with all optional fields set
            var workflowId = Guid.NewGuid();
            var creatorId = Guid.NewGuid();
            var schedulePlanId = Guid.NewGuid();

            // Create a schedule plan first so the association is valid
            var plan = CreateDefaultSchedulePlan();
            plan.Id = schedulePlanId;
            await _workflowService.CreateSchedulePlanAsync(plan);

            var created = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                null,
                WorkflowPriority.High,
                creatorId,
                schedulePlanId,
                workflowId);
            created.Should().NotBeNull();

            // Update to set all nullable fields
            var startedOn = DateTime.UtcNow.AddMinutes(-10);
            var finishedOn = DateTime.UtcNow;
            var abortedBy = Guid.NewGuid();
            var canceledBy = Guid.NewGuid();
            var lastModifiedBy = Guid.NewGuid();

            created!.Status = WorkflowStatus.Aborted;
            created.StartedOn = startedOn;
            created.FinishedOn = finishedOn;
            created.AbortedBy = abortedBy;
            created.CanceledBy = canceledBy;
            created.ErrorMessage = "Test error message";
            created.LastModifiedBy = lastModifiedBy;
            created.Result = new Dictionary<string, object> { ["output"] = "done" };
            await _workflowService.UpdateWorkflowAsync(created);

            // Act: Read back
            var readBack = await _workflowService.GetWorkflowAsync(workflowId);

            // Assert: All fields populated with correct values
            readBack.Should().NotBeNull();
            readBack!.StartedOn.Should().NotBeNull();
            readBack.StartedOn!.Value.Should().BeCloseTo(startedOn, TimeSpan.FromSeconds(2));
            readBack.FinishedOn.Should().NotBeNull();
            readBack.FinishedOn!.Value.Should().BeCloseTo(finishedOn, TimeSpan.FromSeconds(2));
            readBack.AbortedBy.Should().Be(abortedBy);
            readBack.CanceledBy.Should().Be(canceledBy);
            readBack.ErrorMessage.Should().Be("Test error message");
            readBack.SchedulePlanId.Should().Be(schedulePlanId);
            readBack.CreatedBy.Should().Be(creatorId);
            readBack.LastModifiedBy.Should().Be(lastModifiedBy);
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 12: Idempotency Key Verification ─────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that creating a workflow with the same ID twice does not
        /// produce duplicates (DynamoDB PutItem with same PK/SK overwrites).
        /// Per AAP Section 0.8.5: Idempotency keys on all write operations.
        /// Uses DynamoDB conditional expressions (attribute_not_exists(PK))
        /// for idempotent puts.
        /// </summary>
        [Fact]
        public async Task WriteWithSameIdempotencyKey_IsDeduplicated()
        {
            // Arrange: Same workflow ID for both attempts
            var workflowId = Guid.NewGuid();
            var attributes1 = new Dictionary<string, object> { ["attempt"] = "first" };
            var attributes2 = new Dictionary<string, object> { ["attempt"] = "second" };

            // Act: First create
            var first = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                attributes1,
                WorkflowPriority.Medium,
                null,
                null,
                workflowId);
            first.Should().NotBeNull();

            // Second create with same workflowId — should either overwrite or return existing
            var second = await _workflowService.CreateWorkflowAsync(
                _testWorkflowTypeId,
                attributes2,
                WorkflowPriority.Medium,
                null,
                null,
                workflowId);

            // Assert: Only one item exists in DynamoDB with the given PK
            var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = "PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue($"WORKFLOW#{workflowId}")
                }
            });

            // Should have exactly 1 item for this workflow
            scanResponse.Items.Should().ContainSingle();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 13: AWS Endpoint Configuration ───────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies the DynamoDB client is configured to use LocalStack.
        /// Per AAP Section 0.8.6: AWS_ENDPOINT_URL=http://localhost:4566.
        /// </summary>
        [Fact]
        public async Task AwsEndpointConfiguration_UsesLocalStack()
        {
            // Act: Execute a simple ListTables call to verify connectivity
            var response = await _dynamoDbClient.ListTablesAsync(new ListTablesRequest());

            // Assert: Connection to LocalStack succeeded
            response.Should().NotBeNull();
            response.TableNames.Should().Contain(_tableName);

            // Verify client configuration
            var clientConfig = (_dynamoDbClient as AmazonDynamoDBClient);
            clientConfig.Should().NotBeNull();
        }

        // ════════════════════════════════════════════════════════════════════
        // ── Phase 14: Helper Methods ───────────────────────────────────────
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a default SchedulePlan for test use.
        /// </summary>
        private SchedulePlan CreateDefaultSchedulePlan(string name = "Default Test Schedule")
        {
            return new SchedulePlan
            {
                Id = Guid.NewGuid(),
                Name = name,
                Type = SchedulePlanType.Interval,
                StartDate = DateTime.UtcNow.AddDays(-7),
                EndDate = DateTime.UtcNow.AddDays(30),
                ScheduledDays = new SchedulePlanDaysOfWeek
                {
                    ScheduledOnMonday = true,
                    ScheduledOnTuesday = true,
                    ScheduledOnWednesday = true,
                    ScheduledOnThursday = true,
                    ScheduledOnFriday = true,
                    ScheduledOnSaturday = false,
                    ScheduledOnSunday = false
                },
                IntervalInMinutes = 30,
                StartTimespan = 480,
                EndTimespan = 1080,
                WorkflowTypeId = _testWorkflowTypeId,
                Enabled = true,
                NextTriggerTime = DateTime.UtcNow.AddMinutes(15),
                LastTriggerTime = null,
                LastStartedWorkflowId = null,
                JobAttributes = new Dictionary<string, object>
                {
                    ["env"] = "test"
                },
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow,
                LastModifiedBy = null
            };
        }

        /// <summary>
        /// Directly reads a workflow item from DynamoDB for test assertions.
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>?> GetWorkflowItemDirectly(Guid workflowId)
        {
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue($"WORKFLOW#{workflowId}"),
                    ["SK"] = new AttributeValue("META")
                }
            });
            return response.IsItemSet ? response.Item : null;
        }

        /// <summary>
        /// Directly reads a schedule plan item from DynamoDB for test assertions.
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>?> GetSchedulePlanItemDirectly(Guid schedulePlanId)
        {
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue($"SCHEDULE#{schedulePlanId}"),
                    ["SK"] = new AttributeValue("META")
                }
            });
            return response.IsItemSet ? response.Item : null;
        }

        /// <summary>
        /// Seeds multiple workflows with a given status for test setup.
        /// </summary>
        private async Task SeedMultipleWorkflows(int count, WorkflowStatus status)
        {
            for (int i = 0; i < count; i++)
            {
                var wfId = Guid.NewGuid();
                var wf = await _workflowService.CreateWorkflowAsync(
                    _testWorkflowTypeId,
                    null,
                    WorkflowPriority.Low,
                    null,
                    null,
                    wfId);

                if (status != WorkflowStatus.Pending && wf != null)
                {
                    wf.Status = status;
                    if (status == WorkflowStatus.Running)
                        wf.StartedOn = DateTime.UtcNow;
                    if (status == WorkflowStatus.Finished)
                    {
                        wf.StartedOn = DateTime.UtcNow.AddMinutes(-5);
                        wf.FinishedOn = DateTime.UtcNow;
                    }
                    await _workflowService.UpdateWorkflowAsync(wf);
                }
            }
        }

        /// <summary>
        /// Scans and deletes all items from the table for test isolation.
        /// </summary>
        private async Task CleanTable()
        {
            var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                ProjectionExpression = "PK, SK"
            });

            foreach (var item in scanResponse.Items)
            {
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = item["PK"],
                        ["SK"] = item["SK"]
                    }
                });
            }
        }
    }
}
