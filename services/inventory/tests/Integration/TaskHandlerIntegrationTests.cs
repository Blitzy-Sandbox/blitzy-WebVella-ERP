using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
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
    /// Integration tests for the <see cref="TaskHandler"/> Lambda handler verifying full task CRUD
    /// operations, status changes, watcher management, timelog start/stop, comment management, and
    /// scheduled task starting — all running against LocalStack with real DynamoDB persistence.
    ///
    /// NO mocked AWS SDK calls per AAP §0.8.4. All tests exercise the real handler → service →
    /// repository → DynamoDB pipeline backed by LocalStack.
    ///
    /// Pattern: docker compose up -d → test → docker compose down (AAP §0.8.6).
    ///
    /// Source mappings:
    ///   - TaskService.cs (task CRUD, status, watchers, timelogs, comments)
    ///   - CommentService.cs (comment creation, cascading deletion, author-only enforcement)
    ///   - StartTasksOnStartDate.cs (scheduled job for auto-starting tasks)
    ///   - Hooks/Api/Task.cs (post-create hook logic: key calculation, watcher seeding, feed items)
    ///   - Hooks/Api/Comment.cs (comment creation hooks: feed items, watcher management)
    /// </summary>
    [Collection("LocalStack")]
    public class TaskHandlerIntegrationTests
    {
        #region Fields and Constants

        private readonly LocalStackFixture _fixture;
        private readonly ITestOutputHelper _output;
        private readonly TaskHandler _handler;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Hard-coded "Not Started" status GUID preserved from source:
        /// StartTasksOnStartDate.cs line 14 — tasks in this status with past start_date
        /// will be auto-started. Seeded by <see cref="LocalStackFixture.SeedTaskStatusesAsync"/>.
        /// </summary>
        private static readonly Guid NotStartedStatusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");

        /// <summary>
        /// Hard-coded "In Progress" status GUID preserved from source:
        /// StartTasksOnStartDate.cs line 23 — target status when auto-starting tasks.
        /// Seeded by <see cref="LocalStackFixture.SeedTaskStatusesAsync"/>.
        /// </summary>
        private static readonly Guid InProgressStatusId = new Guid("20d73f63-3501-4565-a55e-2d291549a9bd");

        /// <summary>
        /// Hard-coded "Completed" status GUID from <see cref="LocalStackFixture.SeedTaskStatusesAsync"/>.
        /// Used in status transition verification tests.
        /// </summary>
        private static readonly Guid CompletedStatusId = new Guid("7a1c9d3e-5f2b-4e8a-b6c0-d4e9f1a2b3c4");

        /// <summary>
        /// Bug task type GUID from <see cref="LocalStackFixture.SeedTaskTypesAsync"/>.
        /// Used as the default type when seeding test tasks.
        /// </summary>
        private static readonly Guid BugTypeId = new Guid("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

        /// <summary>
        /// Stable test user ID used across tests. Ensures consistent JWT "sub" claim
        /// for deterministic watcher management and author-only validation.
        /// </summary>
        private static readonly Guid TestUserId = Guid.NewGuid();

        /// <summary>
        /// Stable test project ID used for GSI3 (project-task index) seeding.
        /// </summary>
        private static readonly Guid TestProjectId = Guid.NewGuid();

        #endregion

        #region Constructor

        /// <summary>
        /// Constructs the test class with the shared LocalStack fixture and xUnit output helper.
        /// Resolves <see cref="ITaskService"/> and <see cref="ILogger{TaskHandler}"/> from the
        /// fixture's DI container, then constructs a <see cref="TaskHandler"/> using the DI
        /// constructor (bypassing the parameterless Lambda runtime constructor).
        /// </summary>
        public TaskHandlerIntegrationTests(LocalStackFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
            _output = output ?? throw new ArgumentNullException(nameof(output));

            // Resolve real service instances from the fixture's DI container
            using var scope = _fixture.ServiceProvider.CreateScope();
            var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskHandler>>();

            // Verify repository is also resolvable (DI wiring health check)
            var repository = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            _output.WriteLine($"DI resolved: ITaskService={taskService.GetType().Name}, IInventoryRepository={repository.GetType().Name}");

            // Construct handler via DI constructor with real LocalStack-backed services
            _handler = new TaskHandler(taskService, logger);

            // Verify SNS topic is configured for domain event publishing
            _output.WriteLine($"SNS topic for integration tests: {_fixture.SnsTopicArn}");

            // JSON options matching handler's snake_case serialization
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };
        }

        #endregion

        #region Shared Helper Methods

        /// <summary>
        /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> simulating an HTTP API
        /// Gateway v2 proxy event. Sets JWT authorizer claims with the "sub" claim for
        /// user identity extraction by <see cref="TaskHandler"/>.GetCurrentUserId.
        /// </summary>
        /// <param name="method">HTTP method (GET, POST, DELETE).</param>
        /// <param name="path">Request path (e.g., /v1/inventory/tasks).</param>
        /// <param name="body">Optional JSON request body string.</param>
        /// <param name="pathParams">Optional path parameter dictionary (e.g., {"id": "guid"}).</param>
        /// <param name="queryParams">Optional query string parameter dictionary.</param>
        /// <param name="userId">Optional user GUID for JWT "sub" claim. Defaults to TestUserId.</param>
        /// <returns>Fully constructed API Gateway v2 proxy request.</returns>
        private APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string method,
            string path,
            string? body = null,
            Dictionary<string, string>? pathParams = null,
            Dictionary<string, string>? queryParams = null,
            Guid? userId = null)
        {
            var effectiveUserId = userId ?? TestUserId;

            return new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = path,
                Body = body,
                PathParameters = pathParams ?? new Dictionary<string, string>(),
                QueryStringParameters = queryParams ?? new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = method,
                        Path = path
                    },
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = effectiveUserId.ToString(),
                                ["email"] = $"testuser-{effectiveUserId.ToString()[..8]}@test.com"
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Seeds a task item directly into DynamoDB matching the
        /// <see cref="InventoryRepository"/>.SerializeTask schema.
        /// Populates all required fields including aggregate minute counters,
        /// timelog_started_on for timer state, and GSI key attributes.
        /// Bypasses the handler for strict test isolation.
        /// </summary>
        /// <param name="id">Task ID. Defaults to a new GUID.</param>
        /// <param name="subject">Task subject. Defaults to "Integration Test Task".</param>
        /// <param name="statusId">Task status ID. Defaults to NotStartedStatusId.</param>
        /// <param name="ownerId">Optional owner user ID for GSI2 key generation.</param>
        /// <param name="startTime">Optional start_time for scheduled task start tests.</param>
        /// <param name="timelogStartedOn">Optional timer start timestamp for timelog tests.</param>
        /// <param name="projectId">Optional project ID for GSI3 and l_related_records.</param>
        /// <param name="xBillableMinutes">Initial billable minutes aggregate. Default 0.</param>
        /// <param name="xNonBillableMinutes">Initial non-billable minutes aggregate. Default 0.</param>
        /// <returns>The seeded Models.Task with populated fields for assertion reference.</returns>
        private async Task<Models.Task> SeedTaskAsync(
            Guid? id = null,
            string subject = "Integration Test Task",
            Guid? statusId = null,
            Guid? ownerId = null,
            DateTime? startTime = null,
            DateTime? timelogStartedOn = null,
            Guid? projectId = null,
            decimal xBillableMinutes = 0,
            decimal xNonBillableMinutes = 0)
        {
            var taskId = id ?? Guid.NewGuid();
            var effectiveStatusId = statusId ?? NotStartedStatusId;
            var createdOn = DateTime.UtcNow;
            var createdBy = ownerId ?? TestUserId;
            var priority = "medium";

            // Build l_related_records JSON array with project ID if specified
            var relatedRecords = new List<Guid>();
            if (projectId.HasValue)
            {
                relatedRecords.Add(projectId.Value);
            }

            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = taskId.ToString() },
                ["subject"] = new AttributeValue { S = subject },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("o") },
                ["number"] = new AttributeValue { N = "1" },
                ["estimated_minutes"] = new AttributeValue { N = "0" },
                ["x_billable_minutes"] = new AttributeValue { N = xBillableMinutes.ToString() },
                ["x_nonbillable_minutes"] = new AttributeValue { N = xNonBillableMinutes.ToString() },
                ["reserve_time"] = new AttributeValue { BOOL = false },
                ["status_id"] = new AttributeValue { S = effectiveStatusId.ToString() },
                ["type_id"] = new AttributeValue { S = BugTypeId.ToString() },
                ["created_by"] = new AttributeValue { S = createdBy.ToString() },
                ["priority"] = new AttributeValue { S = priority },
                ["l_scope"] = new AttributeValue { S = JsonSerializer.Serialize(new List<string> { "projects" }) },
                ["l_related_records"] = new AttributeValue { S = JsonSerializer.Serialize(relatedRecords) },
                // GSI1: entity-type index for task listing
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#task" },
                ["GSI1SK"] = new AttributeValue { S = $"{priority}#{taskId}" },
            };

            // GSI2: user index — only set if owner specified
            if (ownerId.HasValue)
            {
                item["owner_id"] = new AttributeValue { S = ownerId.Value.ToString() };
                item["GSI2PK"] = new AttributeValue { S = $"USER#{ownerId.Value}" };
                item["GSI2SK"] = new AttributeValue { S = createdOn.ToString("o") };
            }

            // GSI3: project-task index — only set if project specified
            if (projectId.HasValue)
            {
                item["GSI3PK"] = new AttributeValue { S = $"PROJECT#{projectId.Value}" };
                item["GSI3SK"] = new AttributeValue { S = $"TASK#{taskId}" };
            }

            // Optional start_time for scheduled task start tests
            if (startTime.HasValue)
            {
                item["start_time"] = new AttributeValue { S = startTime.Value.ToString("o") };
            }

            // Nullable timelog_started_on for timer state
            if (timelogStartedOn.HasValue)
            {
                item["timelog_started_on"] = new AttributeValue { S = timelogStartedOn.Value.ToString("o") };
            }

            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _fixture.TableName,
                Item = item
            });

            _output.WriteLine($"Seeded task: {taskId}, subject={subject}, status={effectiveStatusId}, owner={createdBy}, startTime={startTime?.ToString("o") ?? "null"}, timerStarted={timelogStartedOn?.ToString("o") ?? "null"}");

            return new Models.Task
            {
                Id = taskId,
                Subject = subject,
                StatusId = effectiveStatusId,
                OwnerId = ownerId,
                StartTime = startTime,
                TimelogStartedOn = timelogStartedOn,
                XBillableMinutes = xBillableMinutes,
                XNonBillableMinutes = xNonBillableMinutes,
                CreatedBy = createdBy,
                CreatedOn = createdOn
            };
        }

        /// <summary>
        /// Seeds a comment item directly into DynamoDB matching the
        /// <see cref="InventoryRepository"/>.SerializeComment schema.
        /// PK=COMMENT#{id}, SK=META, with GSI1/GSI2 attributes.
        /// </summary>
        private async Task<Guid> SeedCommentAsync(
            Guid? id = null,
            string body = "Test comment",
            Guid? parentId = null,
            Guid? createdBy = null,
            Guid? relatedTaskId = null)
        {
            var commentId = id ?? Guid.NewGuid();
            var authorId = createdBy ?? TestUserId;
            var createdOn = DateTime.UtcNow;
            var relatedRecords = new List<Guid>();
            if (relatedTaskId.HasValue)
            {
                relatedRecords.Add(relatedTaskId.Value);
            }

            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"COMMENT#{commentId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = commentId.ToString() },
                ["body"] = new AttributeValue { S = body },
                ["created_by"] = new AttributeValue { S = authorId.ToString() },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("o") },
                ["l_scope"] = new AttributeValue { S = JsonSerializer.Serialize(new List<string> { "projects" }) },
                ["l_related_records"] = new AttributeValue { S = JsonSerializer.Serialize(relatedRecords) },
                // GSI1: parent index or entity type
                ["GSI1PK"] = new AttributeValue { S = parentId.HasValue ? $"PARENT#{parentId.Value}" : "ENTITY#comment" },
                ["GSI1SK"] = new AttributeValue { S = $"{createdOn:o}#{commentId}" },
                // GSI2: user index
                ["GSI2PK"] = new AttributeValue { S = $"USER#{authorId}" },
                ["GSI2SK"] = new AttributeValue { S = createdOn.ToString("o") },
            };

            if (parentId.HasValue)
            {
                item["parent_id"] = new AttributeValue { S = parentId.Value.ToString() };
            }

            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _fixture.TableName,
                Item = item
            });

            _output.WriteLine($"Seeded comment: {commentId}, body={body}, parentId={parentId?.ToString() ?? "null"}, createdBy={authorId}");
            return commentId;
        }

        /// <summary>
        /// Retrieves a task item directly from DynamoDB by its ID.
        /// Uses PK=TASK#{id}, SK=META key pattern matching InventoryRepository.
        /// Returns null if the item does not exist (for deletion verification).
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>?> GetTaskFromDynamoDb(Guid taskId)
        {
            var response = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _fixture.TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            return response.IsItemSet ? response.Item : null;
        }

        /// <summary>
        /// Retrieves a comment item directly from DynamoDB by its ID.
        /// Uses PK=COMMENT#{id}, SK=META key pattern matching InventoryRepository.
        /// Returns null if the item does not exist.
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>?> GetCommentFromDynamoDb(Guid commentId)
        {
            var response = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _fixture.TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"COMMENT#{commentId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            return response.IsItemSet ? response.Item : null;
        }

        /// <summary>
        /// Queries DynamoDB for watcher items associated with a task.
        /// Uses PK=TASK#{taskId} and SK begins_with "WATCHER#" to find all watcher entries.
        /// Returns the list of watcher items (each with SK=WATCHER#{userId}).
        /// </summary>
        private async Task<List<Dictionary<string, AttributeValue>>> GetWatchersFromDynamoDb(Guid taskId)
        {
            var response = await _fixture.DynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _fixture.TableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"TASK#{taskId}" },
                    [":skPrefix"] = new AttributeValue { S = "WATCHER#" }
                }
            });

            return response.Items;
        }

        /// <summary>
        /// Safely deletes a DynamoDB item by PK and SK.
        /// Used in test cleanup to remove seeded test data.
        /// Catches and logs any deletion failures without failing the test.
        /// </summary>
        private async System.Threading.Tasks.Task SafeDeleteItemAsync(string pk, string sk = "META")
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
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup warning: failed to delete {pk}/{sk}: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up a task and all related watcher items from DynamoDB.
        /// First queries for watchers then deletes each, followed by the task META item.
        /// </summary>
        private async System.Threading.Tasks.Task CleanupTaskAsync(Guid taskId)
        {
            // Delete all watcher items for the task
            var watchers = await GetWatchersFromDynamoDb(taskId);
            foreach (var watcher in watchers)
            {
                await SafeDeleteItemAsync(watcher["PK"].S, watcher["SK"].S);
            }

            // Delete the task META item
            await SafeDeleteItemAsync($"TASK#{taskId}");
        }

        /// <summary>
        /// Deserializes the handler response body into a <see cref="ResponseModel"/>.
        /// </summary>
        private ResponseModel? DeserializeResponse(APIGatewayHttpApiV2ProxyResponse response)
        {
            if (string.IsNullOrWhiteSpace(response.Body))
                return null;

            return JsonSerializer.Deserialize<ResponseModel>(response.Body, _jsonOptions);
        }

        #endregion

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: CreateTask — Persists in DynamoDB with correct PK/SK
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that CreateTask persists a task in DynamoDB with the correct
        /// PK=TASK#{id}, SK=META key pattern, and returns 201 Created with the task data.
        ///
        /// Source mapping: TaskHandler.CreateTask → TaskService.PostCreateTaskAsync
        /// Validates:
        ///   - HTTP 201 response with Success=true
        ///   - Task persisted in DynamoDB with correct PK/SK
        ///   - Subject field matches request body
        ///   - GSI attributes populated for entity-type, user, and project indexes
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CreateTask_PersistsInDynamoDB_WithCorrectPKSK()
        {
            // Arrange: build POST request with task creation payload
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var requestBody = JsonSerializer.Serialize(new
            {
                subject = "Integration Test Task — CreateTask",
                owner_id = userId.ToString(),
                type_id = BugTypeId.ToString(),
                status_id = NotStartedStatusId.ToString(),
                priority = "high",
                l_related_records = JsonSerializer.Serialize(new[] { projectId }),
                l_scope = JsonSerializer.Serialize(new[] { "projects" })
            });

            var request = BuildRequest("POST", "/v1/inventory/tasks", requestBody, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };
            Guid? createdTaskId = null;

            try
            {
                // Act: invoke the CreateTask handler
                var response = await _handler.CreateTask(request, context);

                // Assert: verify HTTP 201 Created response
                response.StatusCode.Should().Be(201, "CreateTask should return 201 Created");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull("response body should be deserializable");
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("successfully", "response should confirm task creation");

                // Extract created task ID from response object
                responseModel.Object.Should().NotBeNull("response Object should contain created task");
                var createdTaskJson = JsonSerializer.Serialize(responseModel.Object, _jsonOptions);
                var createdTask = JsonSerializer.Deserialize<Models.Task>(createdTaskJson, _jsonOptions);
                createdTask.Should().NotBeNull("task should deserialize from response Object");
                createdTask!.Id.Should().NotBe(Guid.Empty, "created task should have a valid ID");
                createdTaskId = createdTask.Id;

                _output.WriteLine($"Created task: Id={createdTask.Id}, Subject={createdTask.Subject}");

                // Verify task persisted in DynamoDB with correct PK/SK pattern
                var taskItem = await GetTaskFromDynamoDb(createdTask.Id);
                taskItem.Should().NotBeNull("task should be persisted in DynamoDB");

                // Verify PK/SK pattern
                taskItem!["PK"].S.Should().Be($"TASK#{createdTask.Id}", "PK should follow TASK#{id} pattern");
                taskItem["SK"].S.Should().Be("META", "SK should be META for task items");

                // Verify subject field matches request
                taskItem["subject"].S.Should().Be("Integration Test Task — CreateTask", "subject should match request body");

                // Verify GSI1 attributes populated (entity-type index)
                taskItem.ContainsKey("GSI1PK").Should().BeTrue("GSI1PK should be set for entity-type index");
                taskItem["GSI1PK"].S.Should().Be("ENTITY#task", "GSI1PK should be ENTITY#task");

                _output.WriteLine($"CreateTask verified: PK={taskItem["PK"].S}, SK={taskItem["SK"].S}, GSI1PK={taskItem["GSI1PK"].S}");
            }
            finally
            {
                // Cleanup: remove created task and watchers
                if (createdTaskId.HasValue)
                {
                    await CleanupTaskAsync(createdTaskId.Value);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: GetTask — Returns correct data
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetTask returns a 200 response with the correct task data
        /// when given a valid task ID as a path parameter.
        ///
        /// Source mapping: TaskHandler.GetTask → TaskService.GetTaskAsync
        /// Validates: subject, statusId, ownerId match seeded values.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task GetTask_ReturnsCorrectData()
        {
            // Arrange: seed a task with known field values
            var ownerId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "GetTask Integration Test",
                statusId: NotStartedStatusId,
                ownerId: ownerId,
                projectId: TestProjectId);

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("GET", $"/v1/inventory/tasks/{seededTask.Id}", pathParams: pathParams, userId: ownerId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the GetTask handler
                var response = await _handler.GetTask(request, context);

                // Assert: verify HTTP 200 response
                response.StatusCode.Should().Be(200, "GetTask should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");

                // Verify task data matches seeded values
                responseModel.Object.Should().NotBeNull("response Object should contain task data");
                var taskJson = JsonSerializer.Serialize(responseModel.Object, _jsonOptions);
                var returnedTask = JsonSerializer.Deserialize<Models.Task>(taskJson, _jsonOptions);
                returnedTask.Should().NotBeNull("task should deserialize from response Object");

                returnedTask!.Id.Should().Be(seededTask.Id, "returned task ID should match seeded ID");
                returnedTask.Subject.Should().Be("GetTask Integration Test", "subject should match seeded value");
                returnedTask.StatusId.Should().Be(NotStartedStatusId, "statusId should match seeded value");
                returnedTask.OwnerId.Should().Be(ownerId, "ownerId should match seeded value");

                _output.WriteLine($"GetTask verified: Id={returnedTask.Id}, Subject={returnedTask.Subject}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: GetTask — Non-existent ID returns 404
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetTask returns a 404 Not Found response when the
        /// requested task ID does not exist in DynamoDB.
        ///
        /// Source mapping: TaskHandler.GetTask — task == null → ErrorResponse(404, "task not found")
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task GetTask_NonExistentId_Returns404()
        {
            // Arrange: use a random GUID that does not exist in DynamoDB
            var nonExistentId = Guid.NewGuid();
            var pathParams = new Dictionary<string, string> { ["id"] = nonExistentId.ToString() };
            var request = BuildRequest("GET", $"/v1/inventory/tasks/{nonExistentId}", pathParams: pathParams);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            // Act: invoke the GetTask handler
            var response = await _handler.GetTask(request, context);

            // Assert: verify 404 response
            response.StatusCode.Should().Be(404, "GetTask with non-existent ID should return 404");

            var responseModel = DeserializeResponse(response);
            responseModel.Should().NotBeNull();
            responseModel!.Success.Should().BeFalse("response should indicate failure");
            responseModel.Message.Should().Contain("not found", "message should indicate task not found");

            _output.WriteLine($"GetTask 404 verified for non-existent ID: {nonExistentId}");
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: SetTaskStatus — Updates status in DynamoDB
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that SetTaskStatus updates the task's status_id in DynamoDB
        /// from Not Started to In Progress and returns 200 OK.
        ///
        /// Source mapping: TaskHandler.SetTaskStatus → TaskService.SetStatusAsync
        /// TaskService.SetStatusAsync sets CompletedOn when transitioning to closed status.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task SetTaskStatus_UpdatesStatusInDynamoDB()
        {
            // Arrange: seed task with Not Started status
            var ownerId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "SetTaskStatus Test",
                statusId: NotStartedStatusId,
                ownerId: ownerId,
                projectId: TestProjectId);

            var requestBody = JsonSerializer.Serialize(new
            {
                statusId = InProgressStatusId.ToString()
            });

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/status", requestBody, pathParams: pathParams, userId: ownerId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the SetTaskStatus handler
                var response = await _handler.SetTaskStatus(request, context);

                // Assert: verify HTTP 200 response
                response.StatusCode.Should().Be(200, "SetTaskStatus should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("updated", "message should confirm status update");

                // Verify status updated in DynamoDB
                var taskItem = await GetTaskFromDynamoDb(seededTask.Id);
                taskItem.Should().NotBeNull("task should still exist after status update");
                taskItem!["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "status_id should be updated to In Progress");

                _output.WriteLine($"SetTaskStatus verified: task {seededTask.Id} status now {taskItem["status_id"].S}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: SetTaskStatus — Same status returns error
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that SetTaskStatus returns a 400 error when attempting to
        /// set the same status that the task already has.
        ///
        /// Source mapping: TaskHandler.SetTaskStatus — task.StatusId.Value == statusId
        ///   → ErrorResponse(400, "status already set")
        /// Source reference: ProjectController.cs line 378
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task SetTaskStatus_SameStatus_ReturnsError()
        {
            // Arrange: seed task with In Progress status
            var ownerId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "SetTaskStatus Same Status Test",
                statusId: InProgressStatusId,
                ownerId: ownerId,
                projectId: TestProjectId);

            // Try to set the same status
            var requestBody = JsonSerializer.Serialize(new
            {
                statusId = InProgressStatusId.ToString()
            });

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/status", requestBody, pathParams: pathParams, userId: ownerId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the SetTaskStatus handler
                var response = await _handler.SetTaskStatus(request, context);

                // Assert: verify error response
                response.StatusCode.Should().Be(400, "SetTaskStatus with same status should return 400");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeFalse("response should indicate failure");
                responseModel.Message.Should().Contain("already set",
                    "message should indicate status is already set (source: ProjectController line 378)");

                _output.WriteLine($"SetTaskStatus same-status error verified: {responseModel.Message}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: WatchTask — Creates watcher item in DynamoDB
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that WatchTask creates a watcher item in DynamoDB with the correct
        /// PK=TASK#{taskId}, SK=WATCHER#{userId} key pattern.
        ///
        /// Source mapping: TaskHandler.WatchTask → TaskService.AddWatcherAsync
        ///   → InventoryRepository.AddTaskWatcherAsync
        /// Watcher item pattern: PK=TASK#{taskId}, SK=WATCHER#{userId}, stores user_id + task_id
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task WatchTask_CreatesWatcherItem_InDynamoDB()
        {
            // Arrange: seed a task
            var watcherId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "WatchTask Test",
                ownerId: watcherId,
                projectId: TestProjectId);

            var requestBody = JsonSerializer.Serialize(new
            {
                startWatch = true
            });

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/watch", requestBody, pathParams: pathParams, userId: watcherId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the WatchTask handler
                var response = await _handler.WatchTask(request, context);

                // Assert: verify HTTP 200 response with success message
                response.StatusCode.Should().Be(200, "WatchTask should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("started", "message should indicate watch started");

                // Query DynamoDB for watcher items: PK=TASK#{taskId}, SK begins_with WATCHER#
                var watchers = await GetWatchersFromDynamoDb(seededTask.Id);
                watchers.Should().NotBeEmpty("at least one watcher item should exist after watching");

                // Verify the watcher SK pattern matches: WATCHER#{userId}
                var expectedSk = $"WATCHER#{watcherId}";
                watchers.Any(w => w["SK"].S == expectedSk).Should().BeTrue(
                    $"watcher item with SK={expectedSk} should exist in DynamoDB");

                _output.WriteLine($"WatchTask verified: {watchers.Count} watcher(s) found for task {seededTask.Id}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: UnwatchTask — Removes watcher item from DynamoDB
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that WatchTask with startWatch=false removes the watcher item from DynamoDB.
        ///
        /// Source mapping: TaskHandler.WatchTask → TaskService.RemoveWatcherAsync
        ///   → InventoryRepository.RemoveTaskWatcherAsync (DeleteItem by PK+SK)
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task UnwatchTask_RemovesWatcherItem()
        {
            // Arrange: seed a task and manually add a watcher item
            var watcherId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "UnwatchTask Test",
                ownerId: watcherId,
                projectId: TestProjectId);

            // Manually seed a watcher item in DynamoDB
            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _fixture.TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"TASK#{seededTask.Id}" },
                    ["SK"] = new AttributeValue { S = $"WATCHER#{watcherId}" },
                    ["user_id"] = new AttributeValue { S = watcherId.ToString() },
                    ["task_id"] = new AttributeValue { S = seededTask.Id.ToString() }
                }
            });

            // Verify watcher exists before unwatch
            var watchersBefore = await GetWatchersFromDynamoDb(seededTask.Id);
            watchersBefore.Any(w => w["SK"].S == $"WATCHER#{watcherId}").Should().BeTrue(
                "watcher should exist before unwatch operation");

            var requestBody = JsonSerializer.Serialize(new
            {
                startWatch = false
            });

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/watch", requestBody, pathParams: pathParams, userId: watcherId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke WatchTask with startWatch=false (unwatch)
                var response = await _handler.WatchTask(request, context);

                // Assert: verify HTTP 200 response
                response.StatusCode.Should().Be(200, "UnwatchTask should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("stopped", "message should indicate watch stopped");

                // Verify watcher item no longer exists in DynamoDB
                var watchersAfter = await GetWatchersFromDynamoDb(seededTask.Id);
                watchersAfter.Any(w => w["SK"].S == $"WATCHER#{watcherId}").Should().BeFalse(
                    "watcher item should be removed after unwatch operation");

                _output.WriteLine($"UnwatchTask verified: watcher {watcherId} removed from task {seededTask.Id}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: StartTimelog — Sets timelog_started_on field
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that StartTimelog sets the timelog_started_on field on the task in DynamoDB.
        ///
        /// Source mapping: TaskHandler.StartTimelog → TaskService.StartTaskTimelogAsync
        /// Response message: "Log Started" (source: ProjectController line 317)
        /// The timelog_started_on value should be approximately DateTime.UtcNow (within 30 seconds).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTimelog_SetsTimelogStartedOnField()
        {
            // Arrange: seed task with timelog_started_on = null (no active timer)
            var ownerId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "StartTimelog Test",
                ownerId: ownerId,
                timelogStartedOn: null,
                projectId: TestProjectId);

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/timelog/start", pathParams: pathParams, userId: ownerId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };
            var beforeCall = DateTime.UtcNow;

            try
            {
                // Act: invoke the StartTimelog handler
                var response = await _handler.StartTimelog(request, context);

                // Assert: verify HTTP 200 response with "Log Started" message
                response.StatusCode.Should().Be(200, "StartTimelog should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Be("Log Started", "message should be 'Log Started' (source: ProjectController line 317)");

                // Verify timelog_started_on is set in DynamoDB
                var taskItem = await GetTaskFromDynamoDb(seededTask.Id);
                taskItem.Should().NotBeNull("task should still exist after StartTimelog");
                taskItem!.ContainsKey("timelog_started_on").Should().BeTrue(
                    "timelog_started_on should be set (not null) after StartTimelog");

                // Parse and verify the timestamp is approximately now
                var timelogStarted = DateTime.Parse(taskItem["timelog_started_on"].S);
                timelogStarted.Should().BeCloseTo(beforeCall, TimeSpan.FromSeconds(30),
                    "timelog_started_on should be approximately DateTime.UtcNow");

                _output.WriteLine($"StartTimelog verified: timelog_started_on={timelogStarted:o}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: StopTimelog — Clears timelog_started_on field
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that StopTimelog clears the timelog_started_on field on the task in DynamoDB.
        ///
        /// Source mapping: TaskHandler.StopTimelog → TaskService.StopTaskTimelogAsync
        ///   Line 247: patchRecord["timelog_started_on"] = null
        /// Response message: "Log Stopped"
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StopTimelog_ClearsTimelogStartedOnField()
        {
            // Arrange: seed task with timelog_started_on set (timer running for 30 minutes)
            var ownerId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "StopTimelog Test",
                ownerId: ownerId,
                timelogStartedOn: DateTime.UtcNow.AddMinutes(-30),
                projectId: TestProjectId);

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/timelog/stop", pathParams: pathParams, userId: ownerId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the StopTimelog handler
                var response = await _handler.StopTimelog(request, context);

                // Assert: verify HTTP 200 response with "Log Stopped" message
                response.StatusCode.Should().Be(200, "StopTimelog should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Be("Log Stopped", "message should be 'Log Stopped'");

                // Verify timelog_started_on is cleared (null/absent) in DynamoDB
                // Source: TaskService.StopTaskTimelogAsync line 247: patchRecord["timelog_started_on"] = null
                var taskItem = await GetTaskFromDynamoDb(seededTask.Id);
                taskItem.Should().NotBeNull("task should still exist after StopTimelog");
                taskItem!.ContainsKey("timelog_started_on").Should().BeFalse(
                    "timelog_started_on should be cleared (null/absent) after StopTimelog");

                _output.WriteLine($"StopTimelog verified: timelog_started_on cleared for task {seededTask.Id}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: StartTimelog — When already started, returns error
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that StartTimelog returns a 400 error when the task's timer
        /// is already running (timelog_started_on is not null).
        ///
        /// Source mapping: TaskHandler.StartTimelog
        ///   → task.TimelogStartedOn != null → ErrorResponse(400, "timelog for the task already started")
        /// Source reference: ProjectController line 310
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTimelog_WhenAlreadyStarted_ReturnsError()
        {
            // Arrange: seed task with timelog_started_on already set (timer running)
            var ownerId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "StartTimelog Already Started Test",
                ownerId: ownerId,
                timelogStartedOn: DateTime.UtcNow.AddMinutes(-10),
                projectId: TestProjectId);

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/timelog/start", pathParams: pathParams, userId: ownerId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: try to start timelog when already started
                var response = await _handler.StartTimelog(request, context);

                // Assert: verify 400 error response
                response.StatusCode.Should().Be(400, "StartTimelog when already started should return 400");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeFalse("response should indicate failure");
                responseModel.Message.Should().Contain("already started",
                    "message should indicate timelog already started (source: ProjectController line 310)");

                _output.WriteLine($"StartTimelog already-started error verified: {responseModel.Message}");
            }
            finally
            {
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: CreateComment — Persists in DynamoDB
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that CreateComment persists a comment in DynamoDB with the correct
        /// PK=COMMENT#{id}, SK=META key pattern, and returns 201 Created with comment data.
        ///
        /// Source mapping: TaskHandler.CreateComment → TaskService.CreateCommentAsync
        /// Validates:
        ///   - HTTP 201 response with Success=true
        ///   - Comment persisted in DynamoDB with correct fields (body, created_by, parent_id)
        ///   - Task ID included in related records automatically
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CreateComment_PersistsInDynamoDB()
        {
            // Arrange: seed a task (comments are related to tasks)
            var authorId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "CreateComment Test Task",
                ownerId: authorId,
                projectId: TestProjectId);

            var requestBody = JsonSerializer.Serialize(new
            {
                body = "Integration test comment — verified against LocalStack",
                parentId = (string?)null
            });

            var pathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var request = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/comments", requestBody, pathParams: pathParams, userId: authorId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };
            Guid? createdCommentId = null;

            try
            {
                // Act: invoke the CreateComment handler
                var response = await _handler.CreateComment(request, context);

                // Assert: verify HTTP 201 Created response
                response.StatusCode.Should().Be(201, "CreateComment should return 201 Created");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("successfully", "message should confirm comment creation");

                // Extract created comment from response Object
                responseModel.Object.Should().NotBeNull("response Object should contain created comment");
                var commentJson = JsonSerializer.Serialize(responseModel.Object, _jsonOptions);
                var createdComment = JsonSerializer.Deserialize<Comment>(commentJson, _jsonOptions);
                createdComment.Should().NotBeNull("comment should deserialize from response Object");
                createdComment!.Id.Should().NotBe(Guid.Empty, "created comment should have a valid ID");
                createdCommentId = createdComment.Id;

                // Verify body, created_by fields
                createdComment.Body.Should().Be("Integration test comment — verified against LocalStack",
                    "comment body should match request");
                createdComment.CreatedBy.Should().Be(authorId, "comment created_by should match requesting user");

                // Verify comment persisted in DynamoDB
                var commentItem = await GetCommentFromDynamoDb(createdComment.Id);
                commentItem.Should().NotBeNull("comment should be persisted in DynamoDB");
                commentItem!["PK"].S.Should().Be($"COMMENT#{createdComment.Id}", "PK should follow COMMENT#{id} pattern");
                commentItem["SK"].S.Should().Be("META", "SK should be META for comment items");
                commentItem["body"].S.Should().Be("Integration test comment — verified against LocalStack");
                commentItem["created_by"].S.Should().Be(authorId.ToString());

                _output.WriteLine($"CreateComment verified: Id={createdComment.Id}, Body={createdComment.Body}");
            }
            finally
            {
                if (createdCommentId.HasValue)
                {
                    await SafeDeleteItemAsync($"COMMENT#{createdCommentId.Value}");
                }
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: DeleteComment — Removes comment and child replies
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that DeleteComment removes the parent comment and all child replies
        /// (cascading deletion) from DynamoDB.
        ///
        /// Source mapping: TaskService.DeleteCommentAsync → InventoryRepository
        /// Original source: CommentService.Delete() lines 66-97 — cascading child deletion
        /// Line 73-79: SELECT id FROM comment WHERE parent_id = @commentId → batch delete
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DeleteComment_RemovesCommentAndChildReplies()
        {
            // Arrange: seed task + parent comment + 2 child comments
            var authorId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "DeleteComment Cascade Test",
                ownerId: authorId,
                projectId: TestProjectId);

            // Seed parent comment
            var parentCommentId = await SeedCommentAsync(
                body: "Parent comment for cascade delete test",
                createdBy: authorId,
                relatedTaskId: seededTask.Id);

            // Seed 2 child comments with parent_id pointing to parent
            var childComment1Id = await SeedCommentAsync(
                body: "Child reply 1",
                parentId: parentCommentId,
                createdBy: authorId,
                relatedTaskId: seededTask.Id);

            var childComment2Id = await SeedCommentAsync(
                body: "Child reply 2",
                parentId: parentCommentId,
                createdBy: authorId,
                relatedTaskId: seededTask.Id);

            // Verify all comments exist before deletion
            (await GetCommentFromDynamoDb(parentCommentId)).Should().NotBeNull("parent comment should exist before delete");
            (await GetCommentFromDynamoDb(childComment1Id)).Should().NotBeNull("child comment 1 should exist before delete");
            (await GetCommentFromDynamoDb(childComment2Id)).Should().NotBeNull("child comment 2 should exist before delete");

            var pathParams = new Dictionary<string, string>
            {
                ["id"] = seededTask.Id.ToString(),
                ["commentId"] = parentCommentId.ToString()
            };
            var request = BuildRequest("DELETE", $"/v1/inventory/tasks/{seededTask.Id}/comments/{parentCommentId}", pathParams: pathParams, userId: authorId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the DeleteComment handler
                var response = await _handler.DeleteComment(request, context);

                // Assert: verify HTTP 200 response
                response.StatusCode.Should().Be(200, "DeleteComment should return 200 OK");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("deleted", "message should confirm comment deletion");

                // Verify parent comment no longer exists in DynamoDB
                var parentItem = await GetCommentFromDynamoDb(parentCommentId);
                parentItem.Should().BeNull("parent comment should be deleted from DynamoDB");

                // Verify child comments also deleted (cascading deletion)
                // Source: CommentService.Delete() line 73-79: queries child comments by parent_id and deletes them
                var childItem1 = await GetCommentFromDynamoDb(childComment1Id);
                childItem1.Should().BeNull("child comment 1 should be cascade-deleted from DynamoDB");

                var childItem2 = await GetCommentFromDynamoDb(childComment2Id);
                childItem2.Should().BeNull("child comment 2 should be cascade-deleted from DynamoDB");

                _output.WriteLine($"DeleteComment cascade verified: parent {parentCommentId} + 2 children deleted");
            }
            finally
            {
                // Defensive cleanup in case deletion failed
                await SafeDeleteItemAsync($"COMMENT#{parentCommentId}");
                await SafeDeleteItemAsync($"COMMENT#{childComment1Id}");
                await SafeDeleteItemAsync($"COMMENT#{childComment2Id}");
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: DeleteComment — Non-author returns Forbidden
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that DeleteComment returns a 403 Forbidden error when a user
        /// other than the comment author attempts to delete it.
        ///
        /// Source mapping: TaskService.DeleteCommentAsync
        ///   → CreatedBy != currentUserId → throw InvalidOperationException("Only the author can delete this comment.")
        /// TaskHandler.DeleteComment catches InvalidOperationException → ErrorResponse(403, ex.Message)
        /// Source reference: CommentService.Delete() line 62-63
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DeleteComment_NonAuthor_ReturnsForbidden()
        {
            // Arrange: seed comment by user A
            var authorId = Guid.NewGuid();
            var nonAuthorId = Guid.NewGuid();
            var seededTask = await SeedTaskAsync(
                subject: "DeleteComment NonAuthor Test",
                ownerId: authorId,
                projectId: TestProjectId);

            // Create comment via handler as authorId (ensures service layer knows the author)
            var createBody = JsonSerializer.Serialize(new
            {
                body = "Comment by author A — should not be deletable by user B"
            });

            var createPathParams = new Dictionary<string, string> { ["id"] = seededTask.Id.ToString() };
            var createRequest = BuildRequest("POST", $"/v1/inventory/tasks/{seededTask.Id}/comments", createBody, pathParams: createPathParams, userId: authorId);
            var createContext = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };
            var createResponse = await _handler.CreateComment(createRequest, createContext);
            createResponse.StatusCode.Should().Be(201, "comment creation should succeed");

            // Extract created comment ID
            var createModel = DeserializeResponse(createResponse);
            var commentJson = JsonSerializer.Serialize(createModel!.Object, _jsonOptions);
            var createdComment = JsonSerializer.Deserialize<Comment>(commentJson, _jsonOptions);
            var commentId = createdComment!.Id;

            _output.WriteLine($"Created comment {commentId} by author {authorId}");

            // Try to delete as non-author (different JWT sub claim)
            var deletePathParams = new Dictionary<string, string>
            {
                ["id"] = seededTask.Id.ToString(),
                ["commentId"] = commentId.ToString()
            };
            var deleteRequest = BuildRequest("DELETE", $"/v1/inventory/tasks/{seededTask.Id}/comments/{commentId}", pathParams: deletePathParams, userId: nonAuthorId);
            var deleteContext = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: attempt to delete comment as non-author
                var response = await _handler.DeleteComment(deleteRequest, deleteContext);

                // Assert: verify 403 Forbidden response
                response.StatusCode.Should().Be(403, "DeleteComment by non-author should return 403");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeFalse("response should indicate failure");
                responseModel.Message.Should().Contain("author",
                    "message should indicate only the author can delete (source: CommentService line 62-63)");

                _output.WriteLine($"DeleteComment non-author forbidden verified: {responseModel.Message}");
            }
            finally
            {
                // Cleanup: delete comment as the actual author would, then cleanup task
                await SafeDeleteItemAsync($"COMMENT#{commentId}");
                await CleanupTaskAsync(seededTask.Id);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: StartTasksOnStartDate — Updates task statuses
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that StartTasksOnStartDate updates tasks with Not Started status
        /// and past start_date to In Progress status.
        ///
        /// Source mapping: TaskHandler.StartTasksOnStartDate
        ///   → TaskService.GetTasksThatNeedStartingAsync (filters NotStarted + start_time &lt;= today)
        ///   → TaskService.SetStatusAsync (per qualifying task → InProgressStatusId)
        /// Source reference: StartTasksOnStartDate.cs lines 14-31
        ///
        /// Note: This handler is Step Functions-triggered, so it takes only ILambdaContext
        /// (no APIGatewayHttpApiV2ProxyRequest) and returns Task&lt;string&gt; (JSON summary).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task StartTasksOnStartDate_UpdatesTaskStatuses()
        {
            // Arrange: seed tasks with different conditions
            // Task 1: Not Started + past start_date → should be started
            var task1 = await SeedTaskAsync(
                subject: "Past Due Task 1",
                statusId: NotStartedStatusId,
                startTime: DateTime.UtcNow.AddDays(-2),
                ownerId: TestUserId,
                projectId: TestProjectId);

            // Task 2: Not Started + past start_date → should be started
            var task2 = await SeedTaskAsync(
                subject: "Past Due Task 2",
                statusId: NotStartedStatusId,
                startTime: DateTime.UtcNow.AddDays(-1),
                ownerId: TestUserId,
                projectId: TestProjectId);

            // Task 3: Not Started + future start_date → should NOT be started
            var task3 = await SeedTaskAsync(
                subject: "Future Task",
                statusId: NotStartedStatusId,
                startTime: DateTime.UtcNow.AddDays(30),
                ownerId: TestUserId,
                projectId: TestProjectId);

            // Task 4: In Progress + past start_date → should NOT be changed (already started)
            var task4 = await SeedTaskAsync(
                subject: "Already Started Task",
                statusId: InProgressStatusId,
                startTime: DateTime.UtcNow.AddDays(-5),
                ownerId: TestUserId,
                projectId: TestProjectId);

            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the StartTasksOnStartDate handler (Step Functions trigger)
                var resultJson = await _handler.StartTasksOnStartDate(context);

                // Assert: verify JSON summary result
                resultJson.Should().NotBeNullOrWhiteSpace("handler should return a JSON summary string");
                _output.WriteLine($"StartTasksOnStartDate result: {resultJson}");

                // Parse the result JSON to verify task counts
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;
                root.TryGetProperty("tasks_started", out var tasksStarted).Should().BeTrue("result should contain tasks_started count");
                tasksStarted.GetInt32().Should().BeGreaterOrEqualTo(2, "at least 2 tasks with past start_date should be started");

                // Verify Task 1 status changed to In Progress in DynamoDB
                var task1Item = await GetTaskFromDynamoDb(task1.Id);
                task1Item.Should().NotBeNull("task1 should still exist");
                task1Item!["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "task1 (past start_date, Not Started) should be changed to In Progress");

                // Verify Task 2 status changed to In Progress
                var task2Item = await GetTaskFromDynamoDb(task2.Id);
                task2Item.Should().NotBeNull("task2 should still exist");
                task2Item!["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "task2 (past start_date, Not Started) should be changed to In Progress");

                // Verify Task 3 status remains Not Started (future start_date)
                var task3Item = await GetTaskFromDynamoDb(task3.Id);
                task3Item.Should().NotBeNull("task3 should still exist");
                task3Item!["status_id"].S.Should().Be(NotStartedStatusId.ToString(),
                    "task3 (future start_date) should remain Not Started");

                // Verify Task 4 status remains In Progress (was already In Progress)
                var task4Item = await GetTaskFromDynamoDb(task4.Id);
                task4Item.Should().NotBeNull("task4 should still exist");
                task4Item!["status_id"].S.Should().Be(InProgressStatusId.ToString(),
                    "task4 (already In Progress) should remain In Progress");

                _output.WriteLine($"StartTasksOnStartDate verified: tasks started, future/already-started tasks unchanged");
            }
            finally
            {
                await CleanupTaskAsync(task1.Id);
                await CleanupTaskAsync(task2.Id);
                await CleanupTaskAsync(task3.Id);
                await CleanupTaskAsync(task4.Id);
            }
        }
    }
}
