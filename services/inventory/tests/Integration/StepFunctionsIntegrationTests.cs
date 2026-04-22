using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Inventory.DataAccess;
using WebVellaErp.Inventory.Functions;
using WebVellaErp.Inventory.Models;
using WebVellaErp.Inventory.Services;
using Xunit;
using Xunit.Abstractions;

namespace WebVellaErp.Inventory.Tests.Integration
{
    /// <summary>
    /// Integration tests for the Step Functions-triggered <see cref="TaskHandler.StartTasksOnStartDate"/>
    /// scheduled job, verifying that tasks with past or current start_date and "Not Started" status
    /// are automatically transitioned to "In Progress" status.
    ///
    /// All tests run against <b>LocalStack</b> — NO mocked AWS SDK calls (AAP §0.8.4).
    /// Pattern: docker compose up -d → test → docker compose down (AAP §0.8.6).
    ///
    /// Source mappings:
    ///   - <c>WebVella.Erp.Plugins.Project/Jobs/StartTasksOnStartDate.cs</c> lines 14-31 (the Execute method)
    ///   - <c>WebVella.Erp.Plugins.Project/Services/TaskService.cs</c> lines 544-553 (GetTasksThatNeedStarting query logic)
    ///
    /// The original monolith uses an EQL query:
    ///   <c>SELECT id FROM task WHERE status_id = @notStartedStatusId AND start_time &lt;= @currentDate</c>
    /// The microservice equivalent uses <see cref="ITaskService.GetTasksThatNeedStartingAsync"/>,
    /// which queries via <see cref="IInventoryRepository.GetTasksByStatusAsync"/> then filters
    /// <c>StartTime.HasValue &amp;&amp; StartTime.Value &lt;= DateTime.UtcNow.Date.AddDays(1)</c>.
    /// </summary>
    [Collection("LocalStack")]
    public class StepFunctionsIntegrationTests
    {
        #region Fields and Constants

        private readonly LocalStackFixture _fixture;
        private readonly ITestOutputHelper _output;
        private readonly TaskHandler _handler;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Hard-coded "Not Started" status GUID preserved from source:
        /// <c>TaskService.GetTasksThatNeedStarting()</c> line 548:
        /// <code>new EqlParameter("notStartedStatusId", new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f"))</code>
        /// Seeded by <see cref="LocalStackFixture"/> during InitializeAsync.
        /// </summary>
        private static readonly Guid NotStartedStatusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");

        /// <summary>
        /// Hard-coded "In Progress" status GUID preserved from source:
        /// <c>StartTasksOnStartDate.Execute()</c> line 23:
        /// <code>patchRecord["status_id"] = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd")</code>
        /// Seeded by <see cref="LocalStackFixture"/> during InitializeAsync.
        /// </summary>
        private static readonly Guid InProgressStatusId = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");

        /// <summary>
        /// Hard-coded "Completed" status GUID from <see cref="LocalStackFixture"/> seed data.
        /// Used as a closed status that should NOT be affected by StartTasksOnStartDate.
        /// <c>IsClosed = true</c> per fixture seeding.
        /// </summary>
        private static readonly Guid CompletedStatusId = new Guid("7a1c9d3e-5f2b-4e8a-b6c0-d4e9f1a2b3c4");

        /// <summary>
        /// System User GUID from <c>WebVella.Erp/Api/Definitions.cs</c> — <c>SystemIds.SystemUserId</c>.
        /// Used as default <c>created_by</c> value when seeding test tasks.
        /// </summary>
        private static readonly Guid SystemUserId = new Guid("bdc56420-caf0-4030-8a0e-d264f6f47b04");

        /// <summary>
        /// Bug task type GUID from <see cref="LocalStackFixture"/> seed data.
        /// Used as default type_id when seeding test tasks.
        /// </summary>
        private static readonly Guid BugTypeId = new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

        #endregion

        #region Constructor

        /// <summary>
        /// Constructs the test class with the shared LocalStack fixture and xUnit output helper.
        /// Resolves <see cref="ITaskService"/> and <see cref="ILogger{TaskHandler}"/> from the
        /// fixture's DI container, then constructs a <see cref="TaskHandler"/> using the DI
        /// constructor (bypassing the parameterless Lambda runtime constructor).
        /// </summary>
        public StepFunctionsIntegrationTests(LocalStackFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
            _output = output ?? throw new ArgumentNullException(nameof(output));

            // Resolve real service instances from the fixture's DI container
            using var scope = _fixture.ServiceProvider.CreateScope();
            var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskHandler>>();

            // Verify repository is also resolvable (DI wiring health check)
            var repository = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            _output.WriteLine(
                $"DI resolved: ITaskService={taskService.GetType().Name}, " +
                $"IInventoryRepository={repository.GetType().Name}");

            // Construct handler via DI constructor with real LocalStack-backed services
            _handler = new TaskHandler(taskService, logger);

            // JSON options matching handler's snake_case serialization
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };
        }

        #endregion

        #region DynamoDB Helper Methods

        /// <summary>
        /// Seeds a task item directly into DynamoDB matching the
        /// <see cref="InventoryRepository"/>.SerializeTask schema.
        /// 
        /// DynamoDB item structure:
        ///   PK = TASK#{taskId}
        ///   SK = META
        ///   GSI1PK = ENTITY#task
        ///   GSI1SK = {priority}#{taskId}
        ///   status_id, start_time, subject, created_by, created_on, number, estimated_minutes, etc.
        /// </summary>
        /// <param name="taskId">Unique task identifier.</param>
        /// <param name="subject">Task subject/title.</param>
        /// <param name="statusId">Foreign key to task_status entity.</param>
        /// <param name="startTime">Optional start date/time for scheduling.</param>
        private async System.Threading.Tasks.Task SeedTaskInDynamoDb(
            Guid taskId, string subject, Guid statusId, DateTime? startTime)
        {
            var createdOn = DateTime.UtcNow;
            var priority = "medium";

            var item = new Dictionary<string, AttributeValue>
            {
                // Primary key — matches TASK_PK_PREFIX + id, META_SK from InventoryRepository
                ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                ["SK"] = new AttributeValue { S = "META" },

                // Core identifiers
                ["id"] = new AttributeValue { S = taskId.ToString() },
                ["subject"] = new AttributeValue { S = subject },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("o") },
                ["created_by"] = new AttributeValue { S = SystemUserId.ToString() },

                // Status — the field tested by StartTasksOnStartDate
                ["status_id"] = new AttributeValue { S = statusId.ToString() },

                // Type ID — required field per SerializeTask
                ["type_id"] = new AttributeValue { S = BugTypeId.ToString() },

                // Numeric fields — required per SerializeTask
                ["number"] = new AttributeValue { N = "1" },
                ["estimated_minutes"] = new AttributeValue { N = "0" },
                ["x_billable_minutes"] = new AttributeValue { N = "0" },
                ["x_nonbillable_minutes"] = new AttributeValue { N = "0" },

                // Boolean fields
                ["reserve_time"] = new AttributeValue { BOOL = false },

                // String fields
                ["priority"] = new AttributeValue { S = priority },

                // GSI1: entity-type index — matches InventoryRepository.ENTITY_TYPE_TASK
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task" },
                ["GSI1SK"] = new AttributeValue { S = $"{priority}#{taskId}" }
            };

            // Optional start_time — the filter condition in GetTasksThatNeedStartingAsync
            if (startTime.HasValue)
            {
                item["start_time"] = new AttributeValue { S = startTime.Value.ToString("o") };
            }

            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _fixture.TableName,
                Item = item
            });

            _output.WriteLine(
                $"Seeded task: id={taskId}, subject={subject}, " +
                $"status_id={statusId}, start_time={startTime?.ToString("o") ?? "null"}");
        }

        /// <summary>
        /// Seeds a task_status reference record directly into DynamoDB matching the
        /// <see cref="LocalStackFixture"/>.SeedTaskStatusesAsync schema.
        ///
        /// DynamoDB item structure:
        ///   PK = TASK_STATUS#{statusId}
        ///   SK = META
        ///   GSI1PK = ENTITY#task_status
        ///   GSI1SK = {sort_order:D4}
        ///   id, label, is_closed, sort_order, EntityType, created_at, updated_at
        ///
        /// NOTE: <see cref="LocalStackFixture"/> already seeds the standard three statuses
        /// (Not Started, In Progress, Completed) during InitializeAsync. This helper exists
        /// for tests that need additional custom status records.
        /// </summary>
        /// <param name="statusId">Unique status identifier.</param>
        /// <param name="label">Display label for the status.</param>
        /// <param name="isClosed">Whether this status represents a closed/completed state.</param>
        private async System.Threading.Tasks.Task SeedTaskStatusInDynamoDb(
            Guid statusId, string label, bool isClosed)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_STATUS#{statusId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task_status" },
                ["GSI1SK"] = new AttributeValue { S = "9999" }, // high sort_order for custom statuses
                ["id"] = new AttributeValue { S = statusId.ToString() },
                ["label"] = new AttributeValue { S = label },
                ["is_closed"] = new AttributeValue { BOOL = isClosed },
                ["sort_order"] = new AttributeValue { N = "99" },
                ["EntityType"] = new AttributeValue { S = "TASK_STATUS" },
                ["created_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") },
                ["updated_at"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
            };

            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _fixture.TableName,
                Item = item
            });

            _output.WriteLine(
                $"Seeded task_status: id={statusId}, label={label}, is_closed={isClosed}");
        }

        /// <summary>
        /// Reads a task item from DynamoDB by its ID for assertion verification.
        /// Uses GetItemAsync with <c>PK=TASK#{taskId}</c>, <c>SK=META</c>.
        /// Returns the full attribute map for field-level assertions.
        /// </summary>
        /// <param name="taskId">Task identifier to retrieve.</param>
        /// <returns>Full DynamoDB item attribute map, or null if not found.</returns>
        private async Task<Dictionary<string, AttributeValue>> ReadTaskFromDynamoDb(Guid taskId)
        {
            var response = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _fixture.TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                },
                ConsistentRead = true // Ensure we read the latest committed write
            });

            if (response.Item == null || response.Item.Count == 0)
            {
                _output.WriteLine($"ReadTaskFromDynamoDb: task {taskId} not found");
                return new Dictionary<string, AttributeValue>();
            }

            _output.WriteLine(
                $"ReadTaskFromDynamoDb: task {taskId}, " +
                $"status_id={response.Item.GetValueOrDefault("status_id")?.S ?? "N/A"}");

            return response.Item;
        }

        /// <summary>
        /// Deletes a single item from DynamoDB by PK and SK for test cleanup.
        /// Catches and logs deletion failures without failing the test to prevent
        /// cleanup errors from masking actual test failures.
        /// </summary>
        /// <param name="pk">Partition key value (e.g., "TASK#{taskId}").</param>
        /// <param name="sk">Sort key value (defaults to "META").</param>
        private async System.Threading.Tasks.Task DeleteItemFromDynamoDb(string pk, string sk)
        {
            try
            {
                await _fixture.DynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _fixture.TableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = pk },
                        ["SK"] = new AttributeValue { S = sk }
                    }
                });

                _output.WriteLine($"Deleted item: PK={pk}, SK={sk}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup warning: failed to delete PK={pk}, SK={sk}: {ex.Message}");
            }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST 1: StartTasksOnStartDate Transitions Not-Started Tasks to In Progress
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="TaskHandler.StartTasksOnStartDate"/> correctly transitions
        /// tasks with "Not Started" status and past/current start_time to "In Progress" status,
        /// while leaving future tasks untouched.
        ///
        /// Arrange:
        ///   - Task A: Not Started, start_time = yesterday → SHOULD be started
        ///   - Task B: Not Started, start_time = today → SHOULD be started (source: start_time &lt;= @currentDate)
        ///   - Task C: Not Started, start_time = 2 days from now → should NOT be started
        ///
        /// Assert:
        ///   - Task A status_id = In Progress (20d73f63-...)
        ///   - Task B status_id = In Progress (20d73f63-...)
        ///   - Task C status_id = Not Started (f3fdd750-...) — unchanged
        ///   - Response: tasks_started = 2
        ///
        /// Source reference:
        ///   - <c>StartTasksOnStartDate.Execute()</c> lines 14-31
        ///   - <c>TaskService.GetTasksThatNeedStarting()</c> lines 544-553
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTasksOnStartDate_TransitionsNotStartedTasksToInProgress()
        {
            // Arrange — Seed 3 tasks with different start_time values
            var taskAId = Guid.NewGuid();
            var taskBId = Guid.NewGuid();
            var taskCId = Guid.NewGuid();

            // Task A: past start_time → should be started
            await SeedTaskInDynamoDb(
                taskAId,
                "Task A - Past Start Date",
                NotStartedStatusId,
                DateTime.UtcNow.Date.AddDays(-1));

            // Task B: today start_time → should be started (source: start_time <= @currentDate)
            await SeedTaskInDynamoDb(
                taskBId,
                "Task B - Today Start Date",
                NotStartedStatusId,
                DateTime.UtcNow.Date);

            // Task C: future start_time (2 days out) → should NOT be started
            // Using AddDays(2) to be safely beyond the currentDateEnd = DateTime.UtcNow.Date.AddDays(1) boundary
            await SeedTaskInDynamoDb(
                taskCId,
                "Task C - Future Start Date",
                NotStartedStatusId,
                DateTime.UtcNow.Date.AddDays(2));

            try
            {
                // Act — Invoke the Step Functions-triggered handler
                var context = new TestLambdaContext
                {
                    AwsRequestId = Guid.NewGuid().ToString()
                };
                var responseJson = await _handler.StartTasksOnStartDate(context);
                _output.WriteLine($"StartTasksOnStartDate response: {responseJson}");

                // Parse the JSON response — keys are snake_case due to JsonOptions
                var responseDoc = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Assert — Verify response counts
                responseDoc.GetProperty("tasks_started").GetInt32().Should().Be(2,
                    "Tasks A (past) and B (today) should be started, but not C (future)");
                responseDoc.GetProperty("total_found").GetInt32().Should().Be(2,
                    "Only 2 tasks should match the query filter (past and today start_time)");

                // Assert — Verify Task A was transitioned to In Progress
                var taskAItem = await ReadTaskFromDynamoDb(taskAId);
                taskAItem.Should().NotBeEmpty("Task A should exist in DynamoDB");
                taskAItem["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "Task A (past start_time, Not Started) should have been transitioned to In Progress");

                // Assert — Verify Task B was transitioned to In Progress
                var taskBItem = await ReadTaskFromDynamoDb(taskBId);
                taskBItem.Should().NotBeEmpty("Task B should exist in DynamoDB");
                taskBItem["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "Task B (today start_time, Not Started) should have been transitioned to In Progress");

                // Assert — Verify Task C was NOT transitioned (still Not Started)
                var taskCItem = await ReadTaskFromDynamoDb(taskCId);
                taskCItem.Should().NotBeEmpty("Task C should exist in DynamoDB");
                taskCItem["status_id"].S.Should().Be(NotStartedStatusId.ToString(),
                    "Task C (future start_time) should still be in Not Started status");
            }
            finally
            {
                // Cleanup — Remove all test data from DynamoDB
                await DeleteItemFromDynamoDb($"TASK#{taskAId}", "META");
                await DeleteItemFromDynamoDb($"TASK#{taskBId}", "META");
                await DeleteItemFromDynamoDb($"TASK#{taskCId}", "META");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST 2: StartTasksOnStartDate When No Tasks Qualify Returns Zero
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="TaskHandler.StartTasksOnStartDate"/> returns zero tasks_started
        /// when no tasks match the query criteria (either wrong status or future start_time).
        ///
        /// Arrange:
        ///   - Task D: In Progress status, start_time = yesterday → already started, should NOT be affected
        ///   - Task E: Not Started status, start_time = 2 days from now → not yet due, should NOT be started
        ///
        /// Assert:
        ///   - Response: tasks_started = 0
        ///   - Task D status unchanged (still In Progress)
        ///   - Task E status unchanged (still Not Started)
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTasksOnStartDate_WhenNoTasksQualify_ReturnsZero()
        {
            // Arrange — Seed tasks that do NOT qualify for starting
            var taskDId = Guid.NewGuid();
            var taskEId = Guid.NewGuid();

            // Task D: already In Progress — wrong status, should NOT be touched
            await SeedTaskInDynamoDb(
                taskDId,
                "Task D - Already In Progress",
                InProgressStatusId,
                DateTime.UtcNow.Date.AddDays(-1));

            // Task E: Not Started but future start_time — not yet due
            await SeedTaskInDynamoDb(
                taskEId,
                "Task E - Future Not Started",
                NotStartedStatusId,
                DateTime.UtcNow.Date.AddDays(2));

            try
            {
                // Act
                var context = new TestLambdaContext
                {
                    AwsRequestId = Guid.NewGuid().ToString()
                };
                var responseJson = await _handler.StartTasksOnStartDate(context);
                _output.WriteLine($"StartTasksOnStartDate response: {responseJson}");

                // Parse response
                var responseDoc = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Assert — Zero tasks should have been started
                responseDoc.GetProperty("tasks_started").GetInt32().Should().Be(0,
                    "No tasks match the criteria: Task D has wrong status, Task E has future start_time");

                // Assert — Task D status unchanged
                var taskDItem = await ReadTaskFromDynamoDb(taskDId);
                taskDItem.Should().NotBeEmpty("Task D should exist in DynamoDB");
                taskDItem["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "Task D (already In Progress) should remain unchanged");

                // Assert — Task E status unchanged
                var taskEItem = await ReadTaskFromDynamoDb(taskEId);
                taskEItem.Should().NotBeEmpty("Task E should exist in DynamoDB");
                taskEItem["status_id"].S.Should().Be(NotStartedStatusId.ToString(),
                    "Task E (future start_time) should remain in Not Started status");
            }
            finally
            {
                // Cleanup
                await DeleteItemFromDynamoDb($"TASK#{taskDId}", "META");
                await DeleteItemFromDynamoDb($"TASK#{taskEId}", "META");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST 3: StartTasksOnStartDate Only Affects Not-Started Status
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that <see cref="TaskHandler.StartTasksOnStartDate"/> only transitions tasks
        /// with "Not Started" status (f3fdd750-...), leaving tasks with other statuses untouched
        /// even when their start_time qualifies.
        ///
        /// Arrange — All tasks have start_time = yesterday (all qualify by date):
        ///   - Task F: Not Started → SHOULD be started
        ///   - Task G: In Progress → should NOT be changed (wrong status)
        ///   - Task H: Completed (closed status) → should NOT be changed (wrong status)
        ///
        /// Assert:
        ///   - Task F: status_id = In Progress (20d73f63-...)
        ///   - Task G: status_id = In Progress (unchanged — was already In Progress)
        ///   - Task H: status_id = Completed (unchanged — closed status)
        ///   - Response: tasks_started = 1 (only Task F)
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTasksOnStartDate_OnlyAffectsNotStartedStatus()
        {
            // Arrange — Seed 3 tasks all with past start_time but different statuses
            var taskFId = Guid.NewGuid();
            var taskGId = Guid.NewGuid();
            var taskHId = Guid.NewGuid();
            var yesterdayStart = DateTime.UtcNow.Date.AddDays(-1);

            // Task F: Not Started + past start_time → SHOULD be started
            await SeedTaskInDynamoDb(
                taskFId,
                "Task F - Not Started Past Due",
                NotStartedStatusId,
                yesterdayStart);

            // Task G: In Progress + past start_time → should NOT be changed
            await SeedTaskInDynamoDb(
                taskGId,
                "Task G - Already In Progress",
                InProgressStatusId,
                yesterdayStart);

            // Task H: Completed (closed) + past start_time → should NOT be changed
            await SeedTaskInDynamoDb(
                taskHId,
                "Task H - Already Completed",
                CompletedStatusId,
                yesterdayStart);

            try
            {
                // Act
                var context = new TestLambdaContext
                {
                    AwsRequestId = Guid.NewGuid().ToString()
                };
                var responseJson = await _handler.StartTasksOnStartDate(context);
                _output.WriteLine($"StartTasksOnStartDate response: {responseJson}");

                // Parse response
                var responseDoc = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Assert — Only Task F (Not Started) should have been started
                responseDoc.GetProperty("tasks_started").GetInt32().Should().Be(1,
                    "Only Task F (Not Started + past start_time) should be started");

                // Assert — Task F was transitioned to In Progress
                var taskFItem = await ReadTaskFromDynamoDb(taskFId);
                taskFItem.Should().NotBeEmpty("Task F should exist in DynamoDB");
                taskFItem["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "Task F (Not Started) should have been transitioned to In Progress");

                // Assert — Task G unchanged (was already In Progress)
                var taskGItem = await ReadTaskFromDynamoDb(taskGId);
                taskGItem.Should().NotBeEmpty("Task G should exist in DynamoDB");
                taskGItem["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "Task G (already In Progress) should remain In Progress");

                // Assert — Task H unchanged (Completed/closed status)
                var taskHItem = await ReadTaskFromDynamoDb(taskHId);
                taskHItem.Should().NotBeEmpty("Task H should exist in DynamoDB");
                taskHItem["status_id"].S.Should().Be(CompletedStatusId.ToString(),
                    "Task H (Completed) should remain in Completed status");
            }
            finally
            {
                // Cleanup
                await DeleteItemFromDynamoDb($"TASK#{taskFId}", "META");
                await DeleteItemFromDynamoDb($"TASK#{taskGId}", "META");
                await DeleteItemFromDynamoDb($"TASK#{taskHId}", "META");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST 4: StartTasksOnStartDate — Boundary: start_time Equals Today
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies the boundary condition where <c>start_time</c> equals today's date.
        ///
        /// Per source code <c>TaskService.GetTasksThatNeedStarting()</c> line 546:
        /// <code>
        /// var eqlCommand = "SELECT id FROM task WHERE status_id = @notStartedStatusId AND start_time &lt;= @currentDate";
        /// var eqlParams = new List&lt;EqlParameter&gt;() {
        ///     new EqlParameter("notStartedStatusId", new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f")),
        ///     new EqlParameter("currentDate", DateTime.Now.Date),
        /// };
        /// </code>
        ///
        /// The microservice equivalent in <see cref="TaskService.GetTasksThatNeedStartingAsync"/>:
        /// <code>
        /// var currentDateEnd = DateTime.UtcNow.Date.AddDays(1);
        /// tasksToStart = notStartedTasks.Where(t => t.StartTime.HasValue &amp;&amp; t.StartTime.Value &lt;= currentDateEnd);
        /// </code>
        ///
        /// The <c>&lt;=</c> comparison means tasks with <c>start_time == DateTime.UtcNow.Date</c> (today at
        /// midnight) SHOULD be started — they are within the "up to end of today" window.
        ///
        /// Arrange:
        ///   - Task I: Not Started, start_time = DateTime.UtcNow.Date (today at midnight)
        ///
        /// Assert:
        ///   - Task I status = In Progress (today's tasks should be auto-started)
        ///   - Response: tasks_started = 1
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTasksOnStartDate_StartsTasksWithStartTimeEqualToToday()
        {
            // Arrange — Seed a task with start_time exactly at today midnight
            var taskIId = Guid.NewGuid();
            var todayMidnight = DateTime.UtcNow.Date; // Today at 00:00:00.000

            await SeedTaskInDynamoDb(
                taskIId,
                "Task I - Today Boundary",
                NotStartedStatusId,
                todayMidnight);

            try
            {
                // Act
                var context = new TestLambdaContext
                {
                    AwsRequestId = Guid.NewGuid().ToString()
                };
                var responseJson = await _handler.StartTasksOnStartDate(context);
                _output.WriteLine($"StartTasksOnStartDate response: {responseJson}");

                // Parse response
                var responseDoc = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Assert — The task with today's start_time should have been started
                responseDoc.GetProperty("tasks_started").GetInt32().Should().BeGreaterOrEqualTo(1,
                    "A task with start_time == today should be auto-started per the <= comparison");

                // Assert — Task I was transitioned to In Progress
                var taskIItem = await ReadTaskFromDynamoDb(taskIId);
                taskIItem.Should().NotBeEmpty("Task I should exist in DynamoDB");
                taskIItem["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "Task I (Not Started, start_time = today) should have been transitioned to In Progress");
            }
            finally
            {
                // Cleanup
                await DeleteItemFromDynamoDb($"TASK#{taskIId}", "META");
            }
        }
    }
}
