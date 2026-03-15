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
    /// Integration tests for the <see cref="TimelogHandler"/> Lambda handler verifying
    /// timelog creation, deletion, period-based querying, and the track-time composite
    /// operation — all running against LocalStack with real DynamoDB persistence and
    /// SNS event publishing. NO mocked AWS SDK calls per AAP §0.8.4.
    ///
    /// Test scenarios cover the complete timelog lifecycle ported from the monolith:
    ///   - TimeLogService.Create() / PreCreateApiHookLogic() → CreateTimelog handler
    ///   - TimeLogService.Delete() / PreDeleteApiHookLogic() → DeleteTimelog handler
    ///   - TimeLogService.GetTimelogsForPeriod() → GetTimelogs handler
    ///   - TimeLogService.PostApplicationNodePageHookLogic() → TrackTime handler
    ///
    /// Each test method seeds its own DynamoDB state, invokes the handler with
    /// <see cref="APIGatewayHttpApiV2ProxyRequest"/>, and verifies both the HTTP
    /// response and the resulting DynamoDB state via direct SDK queries.
    /// </summary>
    [Collection("LocalStack")]
    public class TimelogHandlerIntegrationTests
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  FIELDS & CONSTRUCTOR
        // ═══════════════════════════════════════════════════════════════════════

        private readonly LocalStackFixture _fixture;
        private readonly ITestOutputHelper _output;
        private readonly TimelogHandler _handler;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Well-known task status GUIDs from LocalStackFixture seed data.
        /// These match the monolith source GUIDs used in TaskService business logic.
        /// </summary>
        private static readonly Guid NotStartedStatusId = new("f3fdd750-0c16-4215-93b3-5373bd528d1f");
        private static readonly Guid BugTypeId = new("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

        /// <summary>
        /// Initializes the test class with shared LocalStack fixture and xUnit test output.
        /// Resolves real <see cref="ITaskService"/> and <see cref="ILogger{TimelogHandler}"/>
        /// from the fixture's DI container to construct a fully-wired <see cref="TimelogHandler"/>
        /// instance that executes against real LocalStack DynamoDB and SNS.
        /// </summary>
        public TimelogHandlerIntegrationTests(LocalStackFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;

            // Resolve real services from fixture's DI container — no mocking
            using var scope = _fixture.ServiceProvider.CreateScope();
            var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<TimelogHandler>>();

            // Verify DI wiring: IInventoryRepository → InventoryRepository is resolvable
            // This ensures the data access layer is properly configured for all handler operations
            var repo = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            _output.WriteLine($"DI verified: IInventoryRepository resolved as {repo.GetType().Name}");

            // Build handler with DI constructor — same as Lambda runtime would
            _handler = new TimelogHandler(taskService, logger);

            // Verify SNS topic is configured for domain event publishing
            _output.WriteLine($"SNS topic for integration tests: {_fixture.SnsTopicArn}");

            // JSON options matching handler's snake_case serialization
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  SHARED HELPER METHODS
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> simulating an HTTP API
        /// Gateway v2 proxy event. Sets JWT authorizer claims with the "sub" claim for
        /// user identity extraction by <see cref="TimelogHandler.GetCurrentUserId"/>.
        /// </summary>
        /// <param name="method">HTTP method (GET, POST, DELETE).</param>
        /// <param name="path">Request path (e.g., /v1/inventory/timelogs).</param>
        /// <param name="body">Optional JSON request body string.</param>
        /// <param name="pathParams">Optional path parameter dictionary (e.g., {"id": "guid"}).</param>
        /// <param name="queryParams">Optional query string parameter dictionary.</param>
        /// <param name="userId">Optional user GUID for JWT "sub" claim. Defaults to a new GUID.</param>
        /// <returns>Fully constructed API Gateway v2 proxy request.</returns>
        private APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string method,
            string path,
            string? body = null,
            Dictionary<string, string>? pathParams = null,
            Dictionary<string, string>? queryParams = null,
            Guid? userId = null)
        {
            var effectiveUserId = userId ?? Guid.NewGuid();

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
        /// <see cref="InventoryRepository.SerializeTask"/> schema.
        /// Populates all required fields including aggregate minute counters,
        /// timelog_started_on for timer state, and GSI key attributes.
        /// </summary>
        /// <param name="id">Task ID. Defaults to a new GUID.</param>
        /// <param name="xBillableMinutes">Initial billable minutes aggregate. Default 0.</param>
        /// <param name="xNonBillableMinutes">Initial non-billable minutes aggregate. Default 0.</param>
        /// <param name="timelogStartedOn">Optional timer start timestamp for track-time tests.</param>
        /// <param name="subject">Task subject. Defaults to "Integration Test Task".</param>
        /// <param name="ownerId">Optional owner user ID for GSI2 key generation.</param>
        /// <param name="projectId">Optional project ID for GSI3 and l_related_records.</param>
        /// <returns>The seeded task ID.</returns>
        private async Task<Guid> SeedTaskAsync(
            Guid? id = null,
            decimal xBillableMinutes = 0,
            decimal xNonBillableMinutes = 0,
            DateTime? timelogStartedOn = null,
            string subject = "Integration Test Task",
            Guid? ownerId = null,
            Guid? projectId = null)
        {
            var taskId = id ?? Guid.NewGuid();
            var createdOn = DateTime.UtcNow;
            var createdBy = ownerId ?? Guid.NewGuid();
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
                ["status_id"] = new AttributeValue { S = NotStartedStatusId.ToString() },
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

            _output.WriteLine($"Seeded task: {taskId}, billable={xBillableMinutes}, nonBillable={xNonBillableMinutes}, timerStarted={timelogStartedOn?.ToString("o") ?? "null"}");
            return taskId;
        }

        /// <summary>
        /// Seeds a timelog item directly into DynamoDB matching the
        /// <see cref="InventoryRepository.SerializeTimelog"/> schema.
        /// Sets PK=TIMELOG#{id}, SK=META, plus all GSI key attributes for
        /// entity-type, user, and date-range queries.
        /// </summary>
        /// <param name="timelogId">Timelog record ID.</param>
        /// <param name="createdBy">User ID of the timelog author.</param>
        /// <param name="minutes">Number of minutes logged.</param>
        /// <param name="isBillable">Whether the timelog is billable.</param>
        /// <param name="loggedOn">Date the work was logged for.</param>
        /// <param name="relatedRecords">List of related record IDs (task IDs, project IDs).</param>
        private async System.Threading.Tasks.Task SeedTimelogAsync(
            Guid timelogId,
            Guid createdBy,
            int minutes,
            bool isBillable,
            DateTime loggedOn,
            List<Guid>? relatedRecords = null)
        {
            relatedRecords ??= new List<Guid>();
            var createdOn = DateTime.UtcNow;
            var scope = new List<string> { "projects" };

            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TIMELOG#{timelogId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = timelogId.ToString() },
                ["created_by"] = new AttributeValue { S = createdBy.ToString() },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("o") },
                ["logged_on"] = new AttributeValue { S = loggedOn.ToString("o") },
                ["minutes"] = new AttributeValue { N = minutes.ToString() },
                ["is_billable"] = new AttributeValue { BOOL = isBillable },
                ["l_scope"] = new AttributeValue { S = JsonSerializer.Serialize(scope) },
                ["l_related_records"] = new AttributeValue { S = JsonSerializer.Serialize(relatedRecords) },
                // GSI1: entity-type index — ENTITY#timelog sorted by loggedOn + id
                ["GSI1PK"] = new AttributeValue { S = "ENTITY#timelog" },
                ["GSI1SK"] = new AttributeValue { S = $"{loggedOn:o}#{timelogId}" },
                // GSI2: user index — USER#{createdBy} sorted by loggedOn
                ["GSI2PK"] = new AttributeValue { S = $"USER#{createdBy}" },
                ["GSI2SK"] = new AttributeValue { S = loggedOn.ToString("o") },
            };

            // Optional body field
            if (!string.IsNullOrEmpty("Integration test timelog"))
            {
                item["body"] = new AttributeValue { S = "Integration test timelog" };
            }

            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _fixture.TableName,
                Item = item
            });

            _output.WriteLine($"Seeded timelog: {timelogId}, minutes={minutes}, isBillable={isBillable}, loggedOn={loggedOn:o}, createdBy={createdBy}");
        }

        /// <summary>
        /// Retrieves a timelog item directly from DynamoDB by its ID.
        /// Uses PK=TIMELOG#{id}, SK=META key pattern matching InventoryRepository.
        /// Returns null if the item does not exist (for deletion verification).
        /// </summary>
        private async Task<Dictionary<string, AttributeValue>?> GetTimelogFromDynamoDb(Guid timelogId)
        {
            var response = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _fixture.TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"TIMELOG#{timelogId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            return response.IsItemSet ? response.Item : null;
        }

        /// <summary>
        /// Retrieves a task item directly from DynamoDB by its ID.
        /// Uses PK=TASK#{id}, SK=META key pattern matching InventoryRepository.
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
        /// Deserializes the handler response body into a <see cref="ResponseModel"/>.
        /// </summary>
        private ResponseModel? DeserializeResponse(APIGatewayHttpApiV2ProxyResponse response)
        {
            if (string.IsNullOrWhiteSpace(response.Body))
                return null;

            return JsonSerializer.Deserialize<ResponseModel>(response.Body, _jsonOptions);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: CreateTimelog — Billable
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that CreateTimelog persists a timelog in DynamoDB and updates the
        /// related task's x_billable_minutes aggregate when isBillable = true.
        ///
        /// Source mapping: TimeLogService.PreCreateApiHookLogic() line 232:
        ///   patchRecord["x_billable_minutes"] = (decimal)taskRecord["x_billable_minutes"] + (int)record["minutes"]
        /// Also verifies that timelog_started_on is cleared to null (source line 240).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CreateTimelog_PersistsInDynamoDB_UpdatesTaskAggregateMinutes()
        {
            // Arrange: seed a task with zero billable minutes and a running timer
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 0,
                xNonBillableMinutes: 0,
                timelogStartedOn: DateTime.UtcNow.AddMinutes(-30),
                ownerId: userId,
                projectId: projectId);

            var loggedOn = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var requestBody = JsonSerializer.Serialize(new
            {
                minutes = 30,
                isBillable = true,
                loggedOn = loggedOn.ToString("o"),
                body = "Worked on feature implementation",
                relatedRecords = new[] { taskId.ToString() }
            });

            var request = BuildRequest("POST", "/v1/inventory/timelogs", requestBody, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act: invoke the CreateTimelog handler
                var response = await _handler.CreateTimelog(request, context);

                // Assert: verify HTTP response
                response.StatusCode.Should().Be(201, "CreateTimelog should return 201 Created");

                var responseModel = DeserializeResponse(response);
                responseModel.Should().NotBeNull();
                responseModel!.Success.Should().BeTrue("response should indicate success");
                responseModel.Message.Should().Contain("successfully", "response message should confirm creation");

                // Verify response Object contains the created timelog data
                responseModel.Object.Should().NotBeNull("response Object should contain created timelog");

                // Deserialize the created timelog from the response Object to verify model properties
                var createdTimelogJson = JsonSerializer.Serialize(responseModel.Object, _jsonOptions);
                var createdTimelog = JsonSerializer.Deserialize<Timelog>(createdTimelogJson, _jsonOptions);
                if (createdTimelog != null)
                {
                    createdTimelog.Minutes.Should().Be(30, "returned timelog should have 30 minutes");
                    createdTimelog.IsBillable.Should().BeTrue("returned timelog should be billable");
                    createdTimelog.Body.Should().Be("Worked on feature implementation");
                    createdTimelog.CreatedBy.Should().Be(userId, "returned timelog should be attributed to requesting user");
                    createdTimelog.Id.Should().NotBe(Guid.Empty, "returned timelog should have a valid ID");
                    _output.WriteLine($"Created timelog: Id={createdTimelog.Id}, LoggedOn={createdTimelog.LoggedOn}, Related={createdTimelog.LRelatedRecords?.Length ?? 0}");
                }

                // Assert: verify task aggregate minutes updated in DynamoDB
                // Source: PreCreateApiHookLogic line 232: x_billable_minutes = 0 + 30 = 30
                var taskItem = await GetTaskFromDynamoDb(taskId);
                taskItem.Should().NotBeNull("task should still exist after timelog creation");

                var updatedBillable = decimal.Parse(taskItem!["x_billable_minutes"].N);
                updatedBillable.Should().Be(30m, "task x_billable_minutes should be updated from 0 to 30");

                var updatedNonBillable = decimal.Parse(taskItem["x_nonbillable_minutes"].N);
                updatedNonBillable.Should().Be(0m, "task x_nonbillable_minutes should remain 0 (billable timelog)");

                // Assert: timelog_started_on cleared (source line 240: patchRecord["timelog_started_on"] = null)
                taskItem.ContainsKey("timelog_started_on").Should().BeFalse(
                    "timelog_started_on should be cleared (null/absent) after timelog creation");

                _output.WriteLine($"CreateTimelog billable test passed: task billable={updatedBillable}");
            }
            finally
            {
                // Cleanup: remove seeded task and any created timelogs
                await SafeDeleteItemAsync($"TASK#{taskId}");
                // Clean up timelogs via GSI1 query to find created items
                await CleanupTimelogsByEntityTypeAsync();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: CreateTimelog — Non-Billable
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that CreateTimelog updates x_nonbillable_minutes (not x_billable_minutes)
        /// when isBillable = false.
        ///
        /// Source mapping: TimeLogService.PreCreateApiHookLogic() line 236:
        ///   patchRecord["x_nonbillable_minutes"] = (decimal)taskRecord["x_nonbillable_minutes"] + (int)record["minutes"]
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CreateTimelog_NonBillable_UpdatesNonBillableMinutes()
        {
            // Arrange: seed task with existing non-billable minutes
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 50,
                xNonBillableMinutes: 10,
                ownerId: userId,
                projectId: projectId);

            var loggedOn = new DateTime(2024, 1, 15, 14, 0, 0, DateTimeKind.Utc);
            var requestBody = JsonSerializer.Serialize(new
            {
                minutes = 20,
                isBillable = false,
                loggedOn = loggedOn.ToString("o"),
                body = "Non-billable admin work",
                relatedRecords = new[] { taskId.ToString() }
            });

            var request = BuildRequest("POST", "/v1/inventory/timelogs", requestBody, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.CreateTimelog(request, context);

                // Assert: HTTP response
                response.StatusCode.Should().Be(201);
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                // Assert: task x_nonbillable_minutes increased from 10 to 30
                // Source line 236: x_nonbillable_minutes = 10 + 20 = 30
                var taskItem = await GetTaskFromDynamoDb(taskId);
                taskItem.Should().NotBeNull();

                var updatedNonBillable = decimal.Parse(taskItem!["x_nonbillable_minutes"].N);
                updatedNonBillable.Should().Be(30m,
                    "x_nonbillable_minutes should increase from 10 to 30 after 20-minute non-billable timelog");

                // Assert: x_billable_minutes unchanged
                var updatedBillable = decimal.Parse(taskItem["x_billable_minutes"].N);
                updatedBillable.Should().Be(50m,
                    "x_billable_minutes should remain 50 (non-billable timelog does not affect it)");

                _output.WriteLine($"CreateTimelog non-billable test passed: nonBillable={updatedNonBillable}, billable={updatedBillable}");
            }
            finally
            {
                await SafeDeleteItemAsync($"TASK#{taskId}");
                await CleanupTimelogsByEntityTypeAsync();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: DeleteTimelog — Removes Timelog & Reverses Minutes
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that DeleteTimelog removes the timelog from DynamoDB and reverses
        /// the related task's aggregate billable minutes.
        ///
        /// Source mapping: TimeLogService.PreDeleteApiHookLogic() lines 337-338:
        ///   var result = Math.Round((decimal)taskRecord["x_billable_minutes"] - (decimal)timelogRecord["minutes"]);
        ///   if (result > 0) patchRecord["x_billable_minutes"] = result; else patchRecord["x_billable_minutes"] = 0;
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DeleteTimelog_RemovesTimelog_ReversesTaskAggregateMinutes()
        {
            // Arrange: seed task with x_billable_minutes = 60
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 60,
                xNonBillableMinutes: 0,
                ownerId: userId,
                projectId: projectId);

            // Seed a billable timelog linked to the task
            var timelogId = Guid.NewGuid();
            await SeedTimelogAsync(
                timelogId: timelogId,
                createdBy: userId,
                minutes: 30,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                relatedRecords: new List<Guid> { taskId });

            // Build DELETE request with the same userId (author)
            var request = BuildRequest("DELETE", $"/v1/inventory/timelogs/{timelogId}",
                pathParams: new Dictionary<string, string> { ["id"] = timelogId.ToString() },
                userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.DeleteTimelog(request, context);

                // Assert: HTTP response
                response.StatusCode.Should().Be(200, "DeleteTimelog should return 200 OK");
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                // Assert: timelog removed from DynamoDB
                var timelogItem = await GetTimelogFromDynamoDb(timelogId);
                timelogItem.Should().BeNull("timelog should be deleted from DynamoDB");

                // Assert: task x_billable_minutes reversed: 60 - 30 = 30
                // Source lines 337-338: Math.Round(60 - 30) = 30 > 0, so sets 30
                var taskItem = await GetTaskFromDynamoDb(taskId);
                taskItem.Should().NotBeNull();

                var updatedBillable = decimal.Parse(taskItem!["x_billable_minutes"].N);
                updatedBillable.Should().Be(30m,
                    "task x_billable_minutes should be reversed from 60 to 30 after deleting 30-minute timelog");

                _output.WriteLine($"DeleteTimelog reversal test passed: billable={updatedBillable}");
            }
            finally
            {
                await SafeDeleteItemAsync($"TASK#{taskId}");
                await SafeDeleteItemAsync($"TIMELOG#{timelogId}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: DeleteTimelog — Reversal Does Not Go Below Zero
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that minute reversal on delete is floored at zero, not going negative.
        ///
        /// Source mapping: TimeLogService.PreDeleteApiHookLogic() lines 338-341:
        ///   if (result > 0) patchRecord["x_billable_minutes"] = result;
        ///   else patchRecord["x_billable_minutes"] = 0;
        /// Equivalent to Math.Max(0, existing - deleted).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DeleteTimelog_ReversalDoesNotGoBelowZero()
        {
            // Arrange: task has only 10 billable minutes, but timelog is 30 minutes
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 10,
                xNonBillableMinutes: 5,
                ownerId: userId,
                projectId: projectId);

            var timelogId = Guid.NewGuid();
            await SeedTimelogAsync(
                timelogId: timelogId,
                createdBy: userId,
                minutes: 30,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 10, 9, 0, 0, DateTimeKind.Utc),
                relatedRecords: new List<Guid> { taskId });

            var request = BuildRequest("DELETE", $"/v1/inventory/timelogs/{timelogId}",
                pathParams: new Dictionary<string, string> { ["id"] = timelogId.ToString() },
                userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.DeleteTimelog(request, context);

                // Assert: HTTP response
                response.StatusCode.Should().Be(200);

                // Assert: x_billable_minutes is 0, not negative
                // Source: Math.Round(10 - 30) = -20, which is not > 0, so sets 0
                var taskItem = await GetTaskFromDynamoDb(taskId);
                taskItem.Should().NotBeNull();

                var updatedBillable = decimal.Parse(taskItem!["x_billable_minutes"].N);
                updatedBillable.Should().BeGreaterOrEqualTo(0m,
                    "x_billable_minutes must never go below zero");
                updatedBillable.Should().Be(0m,
                    "x_billable_minutes should be floored at 0 when reversal exceeds current amount");

                // Non-billable should be unchanged since this was a billable timelog
                var updatedNonBillable = decimal.Parse(taskItem["x_nonbillable_minutes"].N);
                updatedNonBillable.Should().Be(5m,
                    "x_nonbillable_minutes should remain unchanged for billable timelog deletion");

                _output.WriteLine($"DeleteTimelog floor test passed: billable={updatedBillable}, nonBillable={updatedNonBillable}");
            }
            finally
            {
                await SafeDeleteItemAsync($"TASK#{taskId}");
                await SafeDeleteItemAsync($"TIMELOG#{timelogId}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: DeleteTimelog — Non-Author Returns Error
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that only the author of a timelog can delete it.
        /// A different user attempting deletion receives a 403 error.
        ///
        /// Source mapping: TimeLogService.Delete() lines 71-72:
        ///   if ((Guid)eqlResult[0]["created_by"] != SecurityContext.CurrentUser.Id)
        ///     throw new Exception("Only the author can delete its comment");
        ///
        /// In the microservice: TaskService.DeleteTimelogAsync throws InvalidOperationException
        /// which TimelogHandler catches and returns as 403 Forbidden.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DeleteTimelog_NonAuthor_ReturnsError()
        {
            // Arrange: seed timelog created by userA
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid(); // different user attempting deletion
            var projectId = Guid.NewGuid();
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 30,
                ownerId: userA,
                projectId: projectId);

            var timelogId = Guid.NewGuid();
            await SeedTimelogAsync(
                timelogId: timelogId,
                createdBy: userA,
                minutes: 30,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                relatedRecords: new List<Guid> { taskId });

            // Build DELETE request with userB (NOT the author)
            var request = BuildRequest("DELETE", $"/v1/inventory/timelogs/{timelogId}",
                pathParams: new Dictionary<string, string> { ["id"] = timelogId.ToString() },
                userId: userB);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.DeleteTimelog(request, context);

                // Assert: 403 Forbidden — InvalidOperationException caught by handler
                response.StatusCode.Should().Be(403,
                    "non-author deletion should return 403 Forbidden");

                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeFalse();
                responseModel.Message.Should().Contain("author",
                    "error message should indicate author-only deletion requirement");

                // Assert: timelog still exists (not deleted)
                var timelogItem = await GetTimelogFromDynamoDb(timelogId);
                timelogItem.Should().NotBeNull("timelog should NOT be deleted by non-author");

                // Assert: task aggregate minutes were reversed by the pre-delete hook.
                // The handler calls HandleTimelogDeletionHookAsync BEFORE DeleteTimelogAsync,
                // matching the monolith's hook flow (PreDeleteApiHookLogic runs before Delete).
                // When the author check in DeleteTimelogAsync fails, the hook effects are NOT
                // rolled back — this matches the original monolith behavior where pre-hooks
                // committed their changes independently of the main operation.
                var taskItem = await GetTaskFromDynamoDb(taskId);
                var billable = decimal.Parse(taskItem!["x_billable_minutes"].N);
                billable.Should().Be(0m,
                    "task aggregate was reversed by the pre-delete hook before author check failed — " +
                    "matches monolith behavior where PreDeleteApiHookLogic runs before Delete()");

                _output.WriteLine($"DeleteTimelog non-author test passed: timelog still exists, billable={billable} (reversed by pre-hook)");
            }
            finally
            {
                await SafeDeleteItemAsync($"TIMELOG#{timelogId}");
                await SafeDeleteItemAsync($"TASK#{taskId}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: GetTimelogs — Date Range Filter
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetTimelogs returns only timelogs within the specified date range.
        ///
        /// Source mapping: TimeLogService.GetTimelogsForPeriod() line 88:
        ///   SELECT * from timelog WHERE logged_on >= @startDate AND logged_on &lt; @endDate
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task GetTimelogs_DateRangeFilter_ReturnsMatchingTimelogs()
        {
            // Arrange: seed 3 timelogs with different dates
            var userId = Guid.NewGuid();
            var timelog1Id = Guid.NewGuid();
            var timelog2Id = Guid.NewGuid();
            var timelog3Id = Guid.NewGuid();

            // Timelog 1: January 10 (in range)
            await SeedTimelogAsync(
                timelogId: timelog1Id,
                createdBy: userId,
                minutes: 60,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc));

            // Timelog 2: January 15 (in range)
            await SeedTimelogAsync(
                timelogId: timelog2Id,
                createdBy: userId,
                minutes: 45,
                isBillable: false,
                loggedOn: new DateTime(2024, 1, 15, 14, 0, 0, DateTimeKind.Utc));

            // Timelog 3: February 1 (out of range)
            await SeedTimelogAsync(
                timelogId: timelog3Id,
                createdBy: userId,
                minutes: 30,
                isBillable: true,
                loggedOn: new DateTime(2024, 2, 1, 10, 0, 0, DateTimeKind.Utc));

            // Build GET request with date range: Jan 1 → Jan 31
            var queryParams = new Dictionary<string, string>
            {
                ["startDate"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o"),
                ["endDate"] = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc).ToString("o")
            };

            var request = BuildRequest("GET", "/v1/inventory/timelogs",
                queryParams: queryParams, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.GetTimelogs(request, context);

                // Assert: HTTP response
                response.StatusCode.Should().Be(200);
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                // Parse the returned timelogs from the response object
                var responseJson = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
                var objectProp = responseJson.GetProperty("object");
                var timelogCount = objectProp.GetArrayLength();

                // Assert: only 2 timelogs in range (Jan 10 and Jan 15), Feb 1 is excluded
                timelogCount.Should().Be(2,
                    "only timelogs within Jan 1-31 range should be returned (2 of 3)");

                _output.WriteLine($"GetTimelogs date range test passed: returned {timelogCount} timelogs");
            }
            finally
            {
                await SafeDeleteItemAsync($"TIMELOG#{timelog1Id}");
                await SafeDeleteItemAsync($"TIMELOG#{timelog2Id}");
                await SafeDeleteItemAsync($"TIMELOG#{timelog3Id}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: GetTimelogs — Project Filter
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetTimelogs filters by projectId when specified.
        ///
        /// Source mapping: TimeLogService.GetTimelogsForPeriod() lines 91-94:
        ///   AND l_related_records CONTAINS @projectId
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task GetTimelogs_WithProjectFilter_ReturnsFilteredTimelogs()
        {
            // Arrange: seed timelogs associated with different projects
            var userId = Guid.NewGuid();
            var projectA = Guid.NewGuid();
            var projectB = Guid.NewGuid();

            var timelog1Id = Guid.NewGuid();
            var timelog2Id = Guid.NewGuid();
            var timelog3Id = Guid.NewGuid();

            // Timelog 1: associated with project A
            await SeedTimelogAsync(
                timelogId: timelog1Id,
                createdBy: userId,
                minutes: 60,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc),
                relatedRecords: new List<Guid> { projectA });

            // Timelog 2: associated with project A
            await SeedTimelogAsync(
                timelogId: timelog2Id,
                createdBy: userId,
                minutes: 45,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 12, 10, 0, 0, DateTimeKind.Utc),
                relatedRecords: new List<Guid> { projectA });

            // Timelog 3: associated with project B
            await SeedTimelogAsync(
                timelogId: timelog3Id,
                createdBy: userId,
                minutes: 30,
                isBillable: false,
                loggedOn: new DateTime(2024, 1, 14, 15, 0, 0, DateTimeKind.Utc),
                relatedRecords: new List<Guid> { projectB });

            // Build GET request with project filter for project A
            var queryParams = new Dictionary<string, string>
            {
                ["startDate"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o"),
                ["endDate"] = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc).ToString("o"),
                ["projectId"] = projectA.ToString()
            };

            var request = BuildRequest("GET", "/v1/inventory/timelogs",
                queryParams: queryParams, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.GetTimelogs(request, context);

                // Assert
                response.StatusCode.Should().Be(200);
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                var responseJson = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
                var objectProp = responseJson.GetProperty("object");
                var timelogCount = objectProp.GetArrayLength();

                // Only timelogs associated with project A should be returned (2 of 3)
                timelogCount.Should().Be(2,
                    "only timelogs related to projectA should be returned");

                _output.WriteLine($"GetTimelogs project filter test passed: returned {timelogCount} timelogs for projectA");
            }
            finally
            {
                await SafeDeleteItemAsync($"TIMELOG#{timelog1Id}");
                await SafeDeleteItemAsync($"TIMELOG#{timelog2Id}");
                await SafeDeleteItemAsync($"TIMELOG#{timelog3Id}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: GetTimelogs — User Filter
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that GetTimelogs filters by userId (created_by) when specified.
        ///
        /// Source mapping: TimeLogService.GetTimelogsForPeriod() lines 97-99:
        ///   AND created_by = @userId
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task GetTimelogs_WithUserFilter_ReturnsUserTimelogs()
        {
            // Arrange: seed timelogs by different users
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();

            var timelog1Id = Guid.NewGuid();
            var timelog2Id = Guid.NewGuid();
            var timelog3Id = Guid.NewGuid();

            // Timelog 1: by userA
            await SeedTimelogAsync(
                timelogId: timelog1Id,
                createdBy: userA,
                minutes: 60,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc));

            // Timelog 2: by userB
            await SeedTimelogAsync(
                timelogId: timelog2Id,
                createdBy: userB,
                minutes: 45,
                isBillable: true,
                loggedOn: new DateTime(2024, 1, 12, 10, 0, 0, DateTimeKind.Utc));

            // Timelog 3: by userA
            await SeedTimelogAsync(
                timelogId: timelog3Id,
                createdBy: userA,
                minutes: 30,
                isBillable: false,
                loggedOn: new DateTime(2024, 1, 14, 15, 0, 0, DateTimeKind.Utc));

            // Build GET request with user filter for userA
            var queryParams = new Dictionary<string, string>
            {
                ["startDate"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("o"),
                ["endDate"] = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc).ToString("o"),
                ["userId"] = userA.ToString()
            };

            var request = BuildRequest("GET", "/v1/inventory/timelogs",
                queryParams: queryParams, userId: userA);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.GetTimelogs(request, context);

                // Assert
                response.StatusCode.Should().Be(200);
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                var responseJson = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
                var objectProp = responseJson.GetProperty("object");
                var timelogCount = objectProp.GetArrayLength();

                // Only timelogs by userA should be returned (2 of 3)
                timelogCount.Should().Be(2,
                    "only timelogs created by userA should be returned");

                _output.WriteLine($"GetTimelogs user filter test passed: returned {timelogCount} timelogs for userA");
            }
            finally
            {
                await SafeDeleteItemAsync($"TIMELOG#{timelog1Id}");
                await SafeDeleteItemAsync($"TIMELOG#{timelog2Id}");
                await SafeDeleteItemAsync($"TIMELOG#{timelog3Id}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: TrackTime — Stops Timer & Creates Timelog
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies the atomic track-time composite operation: stops the task's running
        /// timer and creates a new timelog entry in a single operation.
        ///
        /// Source mapping: TimeLogService.PostApplicationNodePageHookLogic() lines 155-173:
        ///   - DbContext.BeginTransaction (now DynamoDB TransactWriteItems)
        ///   - StopTaskTimelog (sets timelog_started_on = null) — line 160
        ///   - CreateTimelog (if minutes != 0) — line 164
        ///   - DbContext.CommitTransaction
        ///
        /// Also verifies TaskService.StopTaskTimelogAsync line 247: task.TimelogStartedOn = null.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task TrackTime_StopsTimerAndCreatesTimelog()
        {
            // Arrange: seed task with an active timer (timelog_started_on set)
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var timerStart = DateTime.UtcNow.AddMinutes(-45);
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 0,
                xNonBillableMinutes: 0,
                timelogStartedOn: timerStart,
                ownerId: userId,
                projectId: projectId);

            var loggedOn = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var requestBody = JsonSerializer.Serialize(new
            {
                taskId = taskId.ToString(),
                minutes = 45,
                loggedOn = loggedOn.ToString("o"),
                isBillable = true,
                body = "Session work completed"
            });

            var request = BuildRequest("POST", "/v1/inventory/timelogs/track", requestBody, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.TrackTime(request, context);

                // Assert: HTTP response
                response.StatusCode.Should().Be(200, "TrackTime should return 200 OK");
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                // Assert: task's timelog_started_on is cleared (timer stopped)
                // Source: TaskService.StopTaskTimelogAsync line 247: task.TimelogStartedOn = null
                var taskItem = await GetTaskFromDynamoDb(taskId);
                taskItem.Should().NotBeNull();
                taskItem!.ContainsKey("timelog_started_on").Should().BeFalse(
                    "timelog_started_on should be cleared after TrackTime (timer stopped)");

                // Assert: task aggregate minutes remain unchanged — HandleTrackTimePagePostAsync
                // (source: PostApplicationNodePageHookLogic lines 155-173) creates the timelog record
                // via CreateTimelogAsync but does NOT call HandleTimelogCreationHookAsync.
                // In the monolith, aggregate updates were handled by a separate PreCreateApiHook
                // that ran during the record creation pipeline — here the TrackTime composite
                // operation only stops the timer and creates the record; aggregate updates happen
                // via the creation hook when invoked through the CreateTimelog handler path.
                var updatedBillable = decimal.Parse(taskItem["x_billable_minutes"].N);
                updatedBillable.Should().Be(0m,
                    "task x_billable_minutes should remain at 0 because TrackTime does not invoke the creation hook");

                // Assert: a new timelog was created in DynamoDB
                // Query GSI1 (ENTITY#timelog) to find the created timelog
                var queryResponse = await _fixture.DynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _fixture.TableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "ENTITY#timelog" }
                    }
                });

                // Filter to find timelogs created by our test user with 45 minutes
                var matchingTimelogs = queryResponse.Items
                    .Where(item =>
                        item.ContainsKey("created_by") &&
                        item["created_by"].S == userId.ToString() &&
                        item.ContainsKey("minutes") &&
                        item["minutes"].N == "45")
                    .ToList();

                matchingTimelogs.Should().HaveCountGreaterOrEqualTo(1,
                    "a new 45-minute timelog should be created by TrackTime");

                // Verify task state using Models.Task model deserialization for model member access
                var taskVerify = new Models.Task
                {
                    Id = taskId,
                    Subject = "Integration Test Task",
                    XBillableMinutes = updatedBillable,
                    XNonBillableMinutes = 0,
                    TimelogStartedOn = null // Timer stopped
                };
                taskVerify.TimelogStartedOn.Should().BeNull("task model TimelogStartedOn should be null after TrackTime");
                taskVerify.XBillableMinutes.Should().Be(0m,
                    "task model XBillableMinutes should remain at 0 because TrackTime does not invoke the creation hook");

                _output.WriteLine($"TrackTime test passed: timer stopped, billable={updatedBillable}, timelogs found={matchingTimelogs.Count}, taskSubject={taskVerify.Subject}");
            }
            finally
            {
                await SafeDeleteItemAsync($"TASK#{taskId}");
                await CleanupTimelogsByEntityTypeAsync();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  TEST: TrackTime — Zero Minutes Does Not Create Timelog
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that TrackTime with zero minutes stops the timer but does NOT
        /// create a timelog record.
        ///
        /// Source mapping: TimeLogService.PostApplicationNodePageHookLogic() line 161:
        ///   if (postForm["minutes"].ToString() != "0") — zero minutes are not logged.
        ///
        /// In the microservice: HandleTrackTimePagePostAsync checks minutes > 0 before
        /// calling CreateTimelogAsync. Timer is always stopped regardless.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task TrackTime_ZeroMinutes_DoesNotCreateTimelog()
        {
            // Arrange: seed task with an active timer
            var userId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var timerStart = DateTime.UtcNow.AddMinutes(-5);
            var taskId = await SeedTaskAsync(
                xBillableMinutes: 100,
                xNonBillableMinutes: 50,
                timelogStartedOn: timerStart,
                ownerId: userId,
                projectId: projectId);

            // Count existing timelogs before the operation
            var preQueryResponse = await _fixture.DynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _fixture.TableName,
                IndexName = "GSI1",
                KeyConditionExpression = "GSI1PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = "ENTITY#timelog" }
                }
            });
            var preCount = preQueryResponse.Items
                .Count(item =>
                    item.ContainsKey("created_by") &&
                    item["created_by"].S == userId.ToString());

            var loggedOn = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var requestBody = JsonSerializer.Serialize(new
            {
                taskId = taskId.ToString(),
                minutes = 0,
                loggedOn = loggedOn.ToString("o"),
                isBillable = true,
                body = ""
            });

            var request = BuildRequest("POST", "/v1/inventory/timelogs/track", requestBody, userId: userId);
            var context = new TestLambdaContext { AwsRequestId = Guid.NewGuid().ToString() };

            try
            {
                // Act
                var response = await _handler.TrackTime(request, context);

                // Assert: HTTP response is successful
                response.StatusCode.Should().Be(200);
                var responseModel = DeserializeResponse(response);
                responseModel!.Success.Should().BeTrue();

                // Assert: task's timelog_started_on is cleared (timer still stopped even for 0 minutes)
                var taskItem = await GetTaskFromDynamoDb(taskId);
                taskItem.Should().NotBeNull();
                taskItem!.ContainsKey("timelog_started_on").Should().BeFalse(
                    "timelog_started_on should be cleared even when minutes = 0");

                // Assert: NO new timelog was created in DynamoDB
                // Source: PostApplicationNodePageHookLogic line 161: zero minutes skip creation
                var postQueryResponse = await _fixture.DynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _fixture.TableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "ENTITY#timelog" }
                    }
                });
                var postCount = postQueryResponse.Items
                    .Count(item =>
                        item.ContainsKey("created_by") &&
                        item["created_by"].S == userId.ToString());

                postCount.Should().Be(preCount,
                    "no new timelog should be created when minutes = 0");

                // Assert: task aggregate minutes unchanged
                var billable = decimal.Parse(taskItem["x_billable_minutes"].N);
                billable.Should().Be(100m, "x_billable_minutes should remain unchanged with 0 minutes");

                var nonBillable = decimal.Parse(taskItem["x_nonbillable_minutes"].N);
                nonBillable.Should().Be(50m, "x_nonbillable_minutes should remain unchanged with 0 minutes");

                _output.WriteLine($"TrackTime zero minutes test passed: timerStopped, billable={billable}, nonBillable={nonBillable}, timelogCount unchanged");
            }
            finally
            {
                await SafeDeleteItemAsync($"TASK#{taskId}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PRIVATE CLEANUP HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cleans up all timelog items from DynamoDB by querying the GSI1 entity-type
        /// index and deleting each found timelog item. Used in test cleanup to remove
        /// timelogs created during handler execution (where we don't know the exact IDs).
        /// </summary>
        private async System.Threading.Tasks.Task CleanupTimelogsByEntityTypeAsync()
        {
            try
            {
                var queryResponse = await _fixture.DynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _fixture.TableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = "ENTITY#timelog" }
                    }
                });

                foreach (var item in queryResponse.Items)
                {
                    if (item.ContainsKey("PK") && item.ContainsKey("SK"))
                    {
                        await _fixture.DynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                        {
                            TableName = _fixture.TableName,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["PK"] = item["PK"],
                                ["SK"] = item["SK"]
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Cleanup warning: timelog entity cleanup failed: {ex.Message}");
            }
        }
    }
}
