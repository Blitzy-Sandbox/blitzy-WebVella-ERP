using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
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
    /// Integration tests verifying that SNS domain events are correctly published by the
    /// Inventory (Project Management) microservice after task and timelog operations.
    ///
    /// Test strategy: An SQS queue is subscribed to the SNS topic by <see cref="LocalStackFixture"/>.
    /// After each handler invocation, the test polls the SQS queue for published messages,
    /// parses the SNS notification wrapper, and asserts on the inner event payload.
    ///
    /// All tests run against LocalStack — NO mocked AWS SDK calls (AAP §0.8.4).
    /// Event naming follows {domain}.{entity}.{action} convention (AAP §0.8.5).
    ///
    /// Source references:
    ///   - TaskService.PublishDomainEventAsync (line 1411): SNS publish with eventType MessageAttribute
    ///   - TaskService.PostCreateTaskAsync (line 536): inventory.task.created event
    ///   - TaskService.PostUpdateTaskAsync (line 626): inventory.task.updated event
    ///   - TaskService.HandleTimelogCreationHookAsync (line 833): inventory.timelog.created event
    ///   - TaskService.HandleTimelogDeletionHookAsync (line 907): inventory.timelog.deleted event
    /// </summary>
    public class SnsEventPublishingTests : IClassFixture<LocalStackFixture>
    {
        #region Fields and Constants

        private readonly LocalStackFixture _fixture;
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Not Started task status GUID preserved from source:
        /// TaskService.cs line 548 / StartTasksOnStartDate.cs scheduling check.
        /// </summary>
        private static readonly Guid NotStartedStatusId = Guid.Parse("f3fdd750-0c16-4215-93b3-5373bd528d1f");

        /// <summary>
        /// In Progress task status GUID preserved from source:
        /// StartTasksOnStartDate.cs line 23 — used for auto-starting tasks.
        /// </summary>
        private static readonly Guid InProgressStatusId = Guid.Parse("20d73f63-3501-4565-a55e-2d291549a9bd");

        /// <summary>
        /// System user ID from monolith SystemIds.SystemUserId.
        /// Used as the default authenticated user for test requests.
        /// </summary>
        private static readonly Guid SystemUserId = Guid.Parse("bdc56420-caf0-4030-8a0e-d264f6f47b04");

        /// <summary>
        /// Bug task type GUID seeded by LocalStackFixture.SeedTaskTypesAsync.
        /// </summary>
        private static readonly Guid BugTaskTypeId = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

        /// <summary>
        /// Shared snake_case JSON options matching the handler serialization convention.
        /// </summary>
        private static readonly JsonSerializerOptions SnakeCaseOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// Maximum time to wait while polling SQS for event messages.
        /// Accounts for LocalStack SNS-to-SQS propagation latency.
        /// </summary>
        private static readonly TimeSpan DefaultPollingTimeout = TimeSpan.FromSeconds(15);

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the test class with the shared LocalStack fixture and xUnit output helper.
        /// The fixture provides pre-configured AWS SDK clients, DynamoDB table, SNS topic,
        /// SQS queue (subscribed to SNS), and DI ServiceProvider.
        /// </summary>
        /// <param name="fixture">Shared xUnit IAsyncLifetime fixture wired to LocalStack.</param>
        /// <param name="output">xUnit test output helper for diagnostic logging.</param>
        public SnsEventPublishingTests(LocalStackFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region Test Methods

        /// <summary>
        /// Verifies that creating a task via TaskHandler.CreateTask publishes an
        /// "inventory.task.created" SNS event with the expected payload fields.
        ///
        /// Flow:
        ///   1. Purge SQS queue
        ///   2. Invoke TaskHandler.CreateTask with a valid task payload
        ///   3. Poll SQS for the published event
        ///   4. Assert event type and payload contain taskId, subject, timestamp
        ///
        /// Source: TaskService.PostCreateTaskAsync (lines 475-553) publishes
        /// {taskId, key, subject, projectId, ownerId, createdBy, timestamp}
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CreateTask_PublishesTaskCreatedEvent()
        {
            // ── Arrange ──
            _output.WriteLine("=== CreateTask_PublishesTaskCreatedEvent: Starting ===");
            await PurgeSqsQueue();

            var taskId = Guid.NewGuid();
            var testSubject = $"Integration Test Task {taskId:N}";

            // Build the task JSON body with snake_case property names matching handler's JsonOptions.
            // The handler deserializes to Models.Task using SnakeCaseLower naming policy.
            var taskPayload = new
            {
                id = taskId.ToString(),
                subject = testSubject,
                status_id = NotStartedStatusId.ToString(),
                type_id = BugTaskTypeId.ToString(),
                priority = "medium",
                owner_id = SystemUserId.ToString()
            };

            var request = BuildApiRequest(
                httpMethod: "POST",
                rawPath: "/v1/inventory/tasks",
                body: JsonSerializer.Serialize(taskPayload, SnakeCaseOptions),
                userId: SystemUserId);

            var lambdaContext = new TestLambdaContext
            {
                AwsRequestId = Guid.NewGuid().ToString(),
                FunctionName = "inventory-task-handler"
            };

            // Resolve real handler from fixture DI (wired to LocalStack)
            var handler = CreateTaskHandler();

            // ── Act ──
            _output.WriteLine("Invoking TaskHandler.CreateTask...");
            var response = await handler.CreateTask(request, lambdaContext);

            _output.WriteLine($"Response StatusCode: {response.StatusCode}");
            _output.WriteLine($"Response Body: {response.Body}");

            // ── Assert ──
            response.StatusCode.Should().Be(201, "CreateTask should return 201 Created");

            // Poll SQS for the published SNS event
            var messages = await PollSqsMessages(expectedCount: 1, timeout: DefaultPollingTimeout);
            messages.Should().HaveCountGreaterOrEqualTo(1,
                "at least one SNS event should be published after task creation");

            // Find the task.created event among collected messages
            var taskCreatedMessage = FindMessageByEventType(messages, "inventory.task.created");
            taskCreatedMessage.Should().NotBeNull(
                "an 'inventory.task.created' event should be present in SQS messages");

            // Verify event payload
            AssertEventMessage(taskCreatedMessage!, "inventory.task.created");

            var eventPayload = ExtractEventPayload(taskCreatedMessage!);
            eventPayload.RootElement.TryGetProperty("taskId", out var taskIdProp).Should().BeTrue(
                "event payload should contain 'taskId' field");
            eventPayload.RootElement.TryGetProperty("subject", out var subjectProp).Should().BeTrue(
                "event payload should contain 'subject' field");
            eventPayload.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue(
                "event payload should contain 'timestamp' field");

            _output.WriteLine($"Event taskId: {taskIdProp.GetString()}");
            _output.WriteLine($"Event subject: {subjectProp.GetString()}");

            // ── Cleanup ──
            _output.WriteLine("Cleaning up test data...");
            await CleanupTask(taskId);
            _output.WriteLine("=== CreateTask_PublishesTaskCreatedEvent: Complete ===");
        }

        /// <summary>
        /// Verifies that changing a task's status via TaskHandler.SetTaskStatus publishes an
        /// "inventory.task.updated" SNS event with the expected payload fields.
        ///
        /// Flow:
        ///   1. Seed a task with Not Started status in DynamoDB
        ///   2. Purge SQS queue
        ///   3. Invoke TaskHandler.SetTaskStatus to transition to In Progress
        ///   4. Poll SQS for the published event
        ///   5. Assert event type and payload contain taskId, statusId, timestamp
        ///
        /// Source: TaskService.PostUpdateTaskAsync (lines 607-636) publishes
        /// {taskId, key, subject, statusId, ownerId, updatedBy, timestamp}
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task SetTaskStatus_PublishesTaskUpdatedEvent()
        {
            // ── Arrange ──
            _output.WriteLine("=== SetTaskStatus_PublishesTaskUpdatedEvent: Starting ===");

            var taskId = Guid.NewGuid();
            var seededTask = new Models.Task
            {
                Id = taskId,
                Subject = $"Status Change Test {taskId:N}",
                StatusId = NotStartedStatusId,
                TypeId = BugTaskTypeId,
                Priority = "medium",
                OwnerId = SystemUserId,
                CreatedBy = SystemUserId,
                CreatedOn = DateTime.UtcNow
            };

            // Seed the task directly via repository
            var repository = ResolveRepository();
            await repository.CreateTaskAsync(seededTask);
            _output.WriteLine($"Seeded task {taskId} with status Not Started");

            await PurgeSqsQueue();

            var statusBody = new { statusId = InProgressStatusId.ToString() };
            var request = BuildApiRequest(
                httpMethod: "POST",
                rawPath: $"/v1/inventory/tasks/{taskId}/status",
                body: JsonSerializer.Serialize(statusBody),
                userId: SystemUserId,
                pathParameters: new Dictionary<string, string> { { "id", taskId.ToString() } });

            var lambdaContext = new TestLambdaContext
            {
                AwsRequestId = Guid.NewGuid().ToString(),
                FunctionName = "inventory-task-handler"
            };

            var handler = CreateTaskHandler();

            // ── Act ──
            _output.WriteLine("Invoking TaskHandler.SetTaskStatus...");
            var response = await handler.SetTaskStatus(request, lambdaContext);

            _output.WriteLine($"Response StatusCode: {response.StatusCode}");
            _output.WriteLine($"Response Body: {response.Body}");

            // ── Assert ──
            response.StatusCode.Should().Be(200, "SetTaskStatus should return 200 OK");

            var messages = await PollSqsMessages(expectedCount: 1, timeout: DefaultPollingTimeout);
            messages.Should().HaveCountGreaterOrEqualTo(1,
                "at least one SNS event should be published after status change");

            var taskUpdatedMessage = FindMessageByEventType(messages, "inventory.task.updated");
            taskUpdatedMessage.Should().NotBeNull(
                "an 'inventory.task.updated' event should be present in SQS messages");

            AssertEventMessage(taskUpdatedMessage!, "inventory.task.updated");

            var eventPayload = ExtractEventPayload(taskUpdatedMessage!);
            eventPayload.RootElement.TryGetProperty("taskId", out var taskIdProp).Should().BeTrue(
                "event payload should contain 'taskId' field");
            eventPayload.RootElement.TryGetProperty("statusId", out var statusIdProp).Should().BeTrue(
                "event payload should contain 'statusId' field");
            eventPayload.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue(
                "event payload should contain 'timestamp' field");

            _output.WriteLine($"Event taskId: {taskIdProp.GetString()}");
            _output.WriteLine($"Event statusId: {statusIdProp.GetString()}");

            // ── Cleanup ──
            _output.WriteLine("Cleaning up test data...");
            await CleanupTask(taskId);
            _output.WriteLine("=== SetTaskStatus_PublishesTaskUpdatedEvent: Complete ===");
        }

        /// <summary>
        /// Verifies that creating a timelog via TimelogHandler.CreateTimelog publishes an
        /// "inventory.timelog.created" SNS event with the expected payload fields.
        ///
        /// Flow:
        ///   1. Seed a parent task in DynamoDB (timelog creation updates task aggregate minutes)
        ///   2. Purge SQS queue
        ///   3. Invoke TimelogHandler.CreateTimelog with minutes, isBillable, loggedOn, relatedRecords
        ///   4. Poll SQS for the published event
        ///   5. Assert event type and payload contain timelogId, minutes, isBillable, timestamp
        ///
        /// Source: TaskService.HandleTimelogCreationHookAsync (lines 739-842) publishes
        /// {timelogId, taskId, minutes, isBillable, createdBy, timestamp}
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CreateTimelog_PublishesTimelogCreatedEvent()
        {
            // ── Arrange ──
            _output.WriteLine("=== CreateTimelog_PublishesTimelogCreatedEvent: Starting ===");

            // Seed a parent task — timelog creation hook needs a related task to update aggregate minutes
            var taskId = Guid.NewGuid();
            var seededTask = new Models.Task
            {
                Id = taskId,
                Subject = $"Timelog Parent Task {taskId:N}",
                StatusId = NotStartedStatusId,
                TypeId = BugTaskTypeId,
                Priority = "low",
                OwnerId = SystemUserId,
                CreatedBy = SystemUserId,
                CreatedOn = DateTime.UtcNow,
                XBillableMinutes = 0m,
                XNonBillableMinutes = 0m
            };

            var repository = ResolveRepository();
            await repository.CreateTaskAsync(seededTask);
            _output.WriteLine($"Seeded parent task {taskId}");

            await PurgeSqsQueue();

            // Build timelog creation body — fields match TimelogHandler.CreateTimelog parsing
            var timelogBody = new
            {
                minutes = 30,
                isBillable = true,
                loggedOn = "2024-01-15T10:00:00Z",
                body = "Integration test timelog entry",
                relatedRecords = new[] { taskId.ToString() }
            };

            var request = BuildApiRequest(
                httpMethod: "POST",
                rawPath: "/v1/inventory/timelogs",
                body: JsonSerializer.Serialize(timelogBody),
                userId: SystemUserId);

            var lambdaContext = new TestLambdaContext
            {
                AwsRequestId = Guid.NewGuid().ToString(),
                FunctionName = "inventory-timelog-handler"
            };

            var handler = CreateTimelogHandler();

            // ── Act ──
            _output.WriteLine("Invoking TimelogHandler.CreateTimelog...");
            var response = await handler.CreateTimelog(request, lambdaContext);

            _output.WriteLine($"Response StatusCode: {response.StatusCode}");
            _output.WriteLine($"Response Body: {response.Body}");

            // ── Assert ──
            response.StatusCode.Should().Be(201, "CreateTimelog should return 201 Created");

            // The timelog creation hook publishes the event — may take a moment to propagate
            var messages = await PollSqsMessages(expectedCount: 1, timeout: DefaultPollingTimeout);

            // Filter for timelog.created events specifically (task.created events may also appear)
            var timelogCreatedMessage = FindMessageByEventType(messages, "inventory.timelog.created");
            timelogCreatedMessage.Should().NotBeNull(
                "an 'inventory.timelog.created' event should be present in SQS messages");

            AssertEventMessage(timelogCreatedMessage!, "inventory.timelog.created");

            var eventPayload = ExtractEventPayload(timelogCreatedMessage!);
            eventPayload.RootElement.TryGetProperty("minutes", out var minutesProp).Should().BeTrue(
                "event payload should contain 'minutes' field");
            eventPayload.RootElement.TryGetProperty("isBillable", out var billableProp).Should().BeTrue(
                "event payload should contain 'isBillable' field");
            eventPayload.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue(
                "event payload should contain 'timestamp' field");

            _output.WriteLine($"Event minutes: {minutesProp}");
            _output.WriteLine($"Event isBillable: {billableProp}");

            // ── Cleanup ──
            // Extract the created timelog ID from the response for cleanup
            var createdTimelogId = ExtractCreatedEntityId(response.Body);
            _output.WriteLine($"Cleaning up timelog {createdTimelogId} and task {taskId}...");
            if (createdTimelogId != Guid.Empty)
            {
                await SafeDeleteTimelog(repository, createdTimelogId);
            }
            await CleanupTask(taskId);
            _output.WriteLine("=== CreateTimelog_PublishesTimelogCreatedEvent: Complete ===");
        }

        /// <summary>
        /// Verifies that deleting a timelog via TimelogHandler.DeleteTimelog publishes an
        /// "inventory.timelog.deleted" SNS event with the expected payload fields.
        ///
        /// Flow:
        ///   1. Seed a parent task and a timelog in DynamoDB
        ///   2. Purge SQS queue
        ///   3. Invoke TimelogHandler.DeleteTimelog (JWT sub must match timelog CreatedBy for author-only deletion)
        ///   4. Poll SQS for the published event
        ///   5. Assert event type and payload contain timelogId, minutes, isBillable, timestamp
        ///
        /// Source: TaskService.HandleTimelogDeletionHookAsync (lines 845-914) publishes
        /// {timelogId, minutes, isBillable, timestamp}
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DeleteTimelog_PublishesTimelogDeletedEvent()
        {
            // ── Arrange ──
            _output.WriteLine("=== DeleteTimelog_PublishesTimelogDeletedEvent: Starting ===");

            // Seed parent task
            var taskId = Guid.NewGuid();
            var seededTask = new Models.Task
            {
                Id = taskId,
                Subject = $"Delete Timelog Test Task {taskId:N}",
                StatusId = NotStartedStatusId,
                TypeId = BugTaskTypeId,
                Priority = "low",
                OwnerId = SystemUserId,
                CreatedBy = SystemUserId,
                CreatedOn = DateTime.UtcNow,
                XBillableMinutes = 45m,
                XNonBillableMinutes = 0m
            };

            var repository = ResolveRepository();
            await repository.CreateTaskAsync(seededTask);
            _output.WriteLine($"Seeded parent task {taskId}");

            // Seed a timelog linked to the task
            var timelogId = Guid.NewGuid();
            var seededTimelog = new Timelog
            {
                Id = timelogId,
                CreatedBy = SystemUserId,
                CreatedOn = DateTime.UtcNow.AddHours(-1),
                LoggedOn = DateTime.UtcNow.Date,
                Minutes = 45,
                IsBillable = true,
                Body = "Timelog to be deleted",
                LScope = JsonSerializer.Serialize(new List<string> { "projects" }),
                LRelatedRecords = JsonSerializer.Serialize(new List<Guid> { taskId })
            };

            await repository.CreateTimelogAsync(seededTimelog);
            _output.WriteLine($"Seeded timelog {timelogId} (45 min, billable)");

            await PurgeSqsQueue();

            // Build DELETE request — JWT sub must match timelog's CreatedBy for author-only deletion
            var request = BuildApiRequest(
                httpMethod: "DELETE",
                rawPath: $"/v1/inventory/timelogs/{timelogId}",
                body: null,
                userId: SystemUserId,
                pathParameters: new Dictionary<string, string> { { "id", timelogId.ToString() } });

            var lambdaContext = new TestLambdaContext
            {
                AwsRequestId = Guid.NewGuid().ToString(),
                FunctionName = "inventory-timelog-handler"
            };

            var handler = CreateTimelogHandler();

            // ── Act ──
            _output.WriteLine("Invoking TimelogHandler.DeleteTimelog...");
            var response = await handler.DeleteTimelog(request, lambdaContext);

            _output.WriteLine($"Response StatusCode: {response.StatusCode}");
            _output.WriteLine($"Response Body: {response.Body}");

            // ── Assert ──
            response.StatusCode.Should().Be(200, "DeleteTimelog should return 200 OK");

            var messages = await PollSqsMessages(expectedCount: 1, timeout: DefaultPollingTimeout);
            var timelogDeletedMessage = FindMessageByEventType(messages, "inventory.timelog.deleted");
            timelogDeletedMessage.Should().NotBeNull(
                "an 'inventory.timelog.deleted' event should be present in SQS messages");

            AssertEventMessage(timelogDeletedMessage!, "inventory.timelog.deleted");

            var eventPayload = ExtractEventPayload(timelogDeletedMessage!);
            eventPayload.RootElement.TryGetProperty("timelogId", out var tlIdProp).Should().BeTrue(
                "event payload should contain 'timelogId' field");
            eventPayload.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue(
                "event payload should contain 'timestamp' field");

            _output.WriteLine($"Event timelogId: {tlIdProp.GetString()}");

            // ── Cleanup ──
            _output.WriteLine("Cleaning up test data...");
            await CleanupTask(taskId);
            _output.WriteLine("=== DeleteTimelog_PublishesTimelogDeletedEvent: Complete ===");
        }

        /// <summary>
        /// Verifies the JSON format of published SNS domain events, ensuring they conform to the
        /// platform's event contract:
        ///   - SQS message body is an SNS notification wrapper with "Type": "Notification"
        ///   - Inner "Message" is valid JSON containing event data fields
        ///   - "MessageAttributes" contains "eventType" String attribute matching {domain}.{entity}.{action}
        ///   - Event body includes "timestamp" and core entity fields
        ///
        /// This test creates a task to trigger an "inventory.task.created" event,
        /// then validates the full SNS/SQS message structure.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task PublishedEvents_HaveCorrectJsonFormat()
        {
            // ── Arrange ──
            _output.WriteLine("=== PublishedEvents_HaveCorrectJsonFormat: Starting ===");
            await PurgeSqsQueue();

            var taskId = Guid.NewGuid();
            var taskPayload = new
            {
                id = taskId.ToString(),
                subject = $"Format Test Task {taskId:N}",
                status_id = NotStartedStatusId.ToString(),
                type_id = BugTaskTypeId.ToString(),
                priority = "high",
                owner_id = SystemUserId.ToString()
            };

            var request = BuildApiRequest(
                httpMethod: "POST",
                rawPath: "/v1/inventory/tasks",
                body: JsonSerializer.Serialize(taskPayload, SnakeCaseOptions),
                userId: SystemUserId);

            var lambdaContext = new TestLambdaContext
            {
                AwsRequestId = Guid.NewGuid().ToString(),
                FunctionName = "inventory-task-handler"
            };

            var handler = CreateTaskHandler();

            // ── Act ──
            _output.WriteLine("Invoking TaskHandler.CreateTask for format verification...");
            var response = await handler.CreateTask(request, lambdaContext);
            response.StatusCode.Should().Be(201);

            var messages = await PollSqsMessages(expectedCount: 1, timeout: DefaultPollingTimeout);
            messages.Should().HaveCountGreaterOrEqualTo(1,
                "at least one message should be available for format verification");

            // ── Assert: SNS notification wrapper structure ──
            var rawMessage = messages.First();
            rawMessage.Body.Should().NotBeNullOrEmpty("SQS message body should not be empty");

            // Parse the SNS notification wrapper
            using var snsWrapper = JsonDocument.Parse(rawMessage.Body);
            var root = snsWrapper.RootElement;

            // Verify SNS notification envelope fields
            root.TryGetProperty("Type", out var typeProp).Should().BeTrue(
                "SNS wrapper should contain 'Type' field");
            typeProp.GetString().Should().Be("Notification",
                "SNS wrapper Type should be 'Notification'");

            root.TryGetProperty("Message", out var messageProp).Should().BeTrue(
                "SNS wrapper should contain 'Message' field (inner event JSON)");
            var innerMessageJson = messageProp.GetString();
            innerMessageJson.Should().NotBeNullOrEmpty(
                "inner Message should not be empty");

            // Verify the inner Message is valid JSON
            JsonDocument? innerDoc = null;
            var parseAction = () => { innerDoc = JsonDocument.Parse(innerMessageJson!); };
            parseAction.Should().NotThrow(
                "inner Message should be valid JSON");

            // Verify inner event body has timestamp
            if (innerDoc != null)
            {
                innerDoc.RootElement.TryGetProperty("timestamp", out var timestampProp).Should().BeTrue(
                    "event payload should contain 'timestamp' field");
                timestampProp.GetString().Should().NotBeNullOrEmpty(
                    "timestamp should not be empty");

                _output.WriteLine($"Event timestamp: {timestampProp.GetString()}");
                innerDoc.Dispose();
            }

            // Verify MessageAttributes in the SNS wrapper contain eventType
            root.TryGetProperty("MessageAttributes", out var attrsProp).Should().BeTrue(
                "SNS wrapper should contain 'MessageAttributes'");

            attrsProp.TryGetProperty("eventType", out var eventTypeProp).Should().BeTrue(
                "MessageAttributes should contain 'eventType'");

            eventTypeProp.TryGetProperty("Type", out var attrTypeProp).Should().BeTrue(
                "eventType attribute should have 'Type' field");
            attrTypeProp.GetString().Should().Be("String",
                "eventType attribute Type should be 'String'");

            eventTypeProp.TryGetProperty("Value", out var attrValueProp).Should().BeTrue(
                "eventType attribute should have 'Value' field");
            var eventTypeValue = attrValueProp.GetString();
            eventTypeValue.Should().NotBeNullOrEmpty(
                "eventType attribute value should not be empty");

            // Verify the event type follows {domain}.{entity}.{action} naming convention
            var parts = eventTypeValue!.Split('.');
            parts.Should().HaveCountGreaterOrEqualTo(3,
                "event type should follow {domain}.{entity}.{action} pattern with at least 3 parts");
            parts[0].Should().Be("inventory",
                "event domain should be 'inventory'");

            _output.WriteLine($"Event type attribute: {eventTypeValue}");
            _output.WriteLine($"Event type parts: domain={parts[0]}, entity={parts[1]}, action={parts[2]}");

            // ── Cleanup ──
            _output.WriteLine("Cleaning up test data...");
            await CleanupTask(taskId);
            _output.WriteLine("=== PublishedEvents_HaveCorrectJsonFormat: Complete ===");
        }

        /// <summary>
        /// Verifies that the SNS topic to SQS queue subscription established by
        /// <see cref="LocalStackFixture"/> is active and correctly configured.
        ///
        /// This is a prerequisite check — if the subscription is not active,
        /// no event publishing tests can succeed.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task SnsToSqsSubscription_IsActive()
        {
            // ── Arrange ──
            _output.WriteLine("=== SnsToSqsSubscription_IsActive: Starting ===");

            // Verify fixture properties are populated
            _fixture.SqsSubscriptionArn.Should().NotBeNullOrEmpty(
                "LocalStackFixture should have established an SNS-to-SQS subscription ARN");

            _fixture.SnsTopicArn.Should().NotBeNullOrEmpty(
                "LocalStackFixture should have created an SNS topic ARN");

            _fixture.SqsQueueUrl.Should().NotBeNullOrEmpty(
                "LocalStackFixture should have created an SQS queue URL");

            _output.WriteLine($"SNS Topic ARN: {_fixture.SnsTopicArn}");
            _output.WriteLine($"SQS Queue URL: {_fixture.SqsQueueUrl}");
            _output.WriteLine($"Subscription ARN: {_fixture.SqsSubscriptionArn}");

            // Verify additional fixture resources are configured and accessible
            _fixture.TableName.Should().NotBeNullOrEmpty(
                "LocalStackFixture should have configured a DynamoDB table name");
            _output.WriteLine($"DynamoDB Table: {_fixture.TableName}");
            _output.WriteLine($"DynamoDB Client endpoint: {_fixture.DynamoDbClient.Config.ServiceURL}");
            _output.WriteLine($"AWS Endpoint from config: {_fixture.Configuration["AWS:ServiceURL"] ?? "default"}");

            // ── Act: List subscriptions on the SNS topic ──
            var listResult = await _fixture.SnsClient.ListSubscriptionsByTopicAsync(
                _fixture.SnsTopicArn);

            // ── Assert ──
            listResult.Should().NotBeNull("ListSubscriptionsByTopicAsync should return a result");
            listResult.Subscriptions.Should().NotBeNull("Subscriptions list should not be null");
            listResult.Subscriptions.Should().HaveCountGreaterOrEqualTo(1,
                "at least one subscription should exist on the SNS topic");

            // Use LINQ to filter and inspect SQS protocol subscriptions
            var sqsProtocolArns = listResult.Subscriptions
                .Where(s => s.Protocol == "sqs")
                .Select(s => s.SubscriptionArn)
                .ToList();
            sqsProtocolArns.Any().Should().BeTrue(
                "at least one SQS protocol subscription should exist on the topic");
            sqsProtocolArns.Count().Should().BeGreaterOrEqualTo(1,
                "subscription count should be at least 1");

            // Verify the specific SQS subscription details
            var sqsSubscription = listResult.Subscriptions
                .FirstOrDefault(s => s.Protocol == "sqs");
            sqsSubscription.Should().NotBeNull(
                "there should be an SQS protocol subscription on the topic");

            sqsSubscription!.SubscriptionArn.Should().NotBeNullOrEmpty(
                "the SQS subscription should have a valid ARN");
            sqsSubscription.SubscriptionArn.Should().Contain("arn:",
                "subscription ARN should contain 'arn:' prefix");
            sqsSubscription.SubscriptionArn.Should().NotBe("PendingConfirmation",
                "the SQS subscription should not be pending confirmation");

            _output.WriteLine($"Found active SQS subscription: {sqsSubscription.SubscriptionArn}");
            _output.WriteLine($"Subscription endpoint: {sqsSubscription.Endpoint}");
            _output.WriteLine("=== SnsToSqsSubscription_IsActive: Complete ===");
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Polls the test SQS queue for published SNS event messages using long polling.
        /// Accumulates messages until either <paramref name="expectedCount"/> messages are
        /// collected or <paramref name="timeout"/> is exceeded.
        ///
        /// Uses WaitTimeSeconds=5 for long polling per AWS best practices to reduce
        /// empty-response cycles and minimize API call overhead.
        ///
        /// After collecting all messages, each consumed message is deleted from the queue
        /// to prevent interference with subsequent test methods.
        /// </summary>
        /// <param name="expectedCount">Minimum number of messages to collect before returning early.</param>
        /// <param name="timeout">Maximum duration to poll before returning whatever was collected.</param>
        /// <returns>List of SQS messages received from the queue.</returns>
        private async Task<List<Message>> PollSqsMessages(int expectedCount, TimeSpan timeout)
        {
            var collected = new List<Message>();
            var deadline = DateTime.UtcNow.Add(timeout);

            _output.WriteLine($"Polling SQS queue for up to {expectedCount} messages (timeout: {timeout.TotalSeconds}s)...");

            while (DateTime.UtcNow < deadline && collected.Count < expectedCount)
            {
                var receiveRequest = new ReceiveMessageRequest
                {
                    QueueUrl = _fixture.SqsQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 5,
                    MessageAttributeNames = new List<string> { "All" }
                };

                try
                {
                    var receiveResponse = await _fixture.SqsClient.ReceiveMessageAsync(receiveRequest);

                    if (receiveResponse.Messages != null && receiveResponse.Messages.Count > 0)
                    {
                        _output.WriteLine($"Received {receiveResponse.Messages.Count} message(s) from SQS");
                        collected.AddRange(receiveResponse.Messages);
                    }
                    else
                    {
                        _output.WriteLine("No messages received in this polling cycle, retrying...");
                    }
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"SQS polling error: {ex.Message}");
                    // Continue polling rather than fail — transient errors are expected with LocalStack
                }
            }

            _output.WriteLine($"Polling complete. Collected {collected.Count} message(s).");

            // Delete consumed messages to prevent interference with subsequent tests
            foreach (var msg in collected)
            {
                try
                {
                    await _fixture.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                    {
                        QueueUrl = _fixture.SqsQueueUrl,
                        ReceiptHandle = msg.ReceiptHandle
                    });
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Warning: Failed to delete SQS message {msg.MessageId}: {ex.Message}");
                }
            }

            return collected;
        }

        /// <summary>
        /// Asserts that an SQS message conforms to the expected SNS notification wrapper format
        /// and contains the specified event type in its MessageAttributes.
        ///
        /// SNS wraps published messages in a JSON envelope with the structure:
        /// {
        ///   "Type": "Notification",
        ///   "Message": "&lt;inner JSON string&gt;",
        ///   "MessageAttributes": { "eventType": { "Type": "String", "Value": "..." } }
        /// }
        ///
        /// This method validates:
        ///   1. The SQS message body is valid JSON (SNS wrapper)
        ///   2. The wrapper contains "Type": "Notification"
        ///   3. The inner "Message" is a valid, non-empty JSON string
        ///   4. The "MessageAttributes" contain "eventType" with the expected value
        /// </summary>
        /// <param name="message">The SQS message to validate.</param>
        /// <param name="expectedEventType">The expected eventType attribute value (e.g., "inventory.task.created").</param>
        private void AssertEventMessage(Message message, string expectedEventType)
        {
            message.Body.Should().NotBeNullOrEmpty("SQS message body should not be empty");

            // Parse the SNS notification wrapper
            using var snsDoc = JsonDocument.Parse(message.Body);
            var root = snsDoc.RootElement;

            // Verify SNS notification type
            if (root.TryGetProperty("Type", out var typeProp))
            {
                typeProp.GetString().Should().Be("Notification",
                    "SNS wrapper Type should be 'Notification'");
            }

            // Extract and validate inner Message
            root.TryGetProperty("Message", out var messageProp).Should().BeTrue(
                "SNS wrapper must contain 'Message' field");
            var innerJson = messageProp.GetString();
            innerJson.Should().NotBeNullOrEmpty(
                "inner Message JSON should not be empty");

            // Verify inner message is valid JSON
            var innerParseAction = () => JsonDocument.Parse(innerJson!);
            innerParseAction.Should().NotThrow(
                "inner Message should be parseable as valid JSON");

            // Verify eventType in MessageAttributes
            if (root.TryGetProperty("MessageAttributes", out var attrsProp))
            {
                if (attrsProp.TryGetProperty("eventType", out var eventTypeProp))
                {
                    if (eventTypeProp.TryGetProperty("Value", out var valueProp))
                    {
                        valueProp.GetString().Should().Be(expectedEventType,
                            $"eventType attribute should be '{expectedEventType}'");
                    }
                    else
                    {
                        // Fallback: some LocalStack versions may format attributes differently
                        _output.WriteLine("Warning: eventType attribute 'Value' field not found in expected location");
                    }
                }
                else
                {
                    _output.WriteLine("Warning: 'eventType' not found in MessageAttributes");
                }
            }
            else
            {
                _output.WriteLine("Warning: 'MessageAttributes' not found in SNS wrapper");
            }
        }

        /// <summary>
        /// Searches the collected SQS messages for one matching the specified event type.
        /// Parses each message's SNS wrapper to find the eventType in MessageAttributes.
        /// </summary>
        /// <param name="messages">List of SQS messages to search.</param>
        /// <param name="eventType">The event type to search for (e.g., "inventory.task.created").</param>
        /// <returns>The matching SQS message, or null if not found.</returns>
        private Message? FindMessageByEventType(List<Message> messages, string eventType)
        {
            foreach (var msg in messages)
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg.Body);
                    var root = doc.RootElement;

                    // Check MessageAttributes.eventType.Value in SNS wrapper
                    if (root.TryGetProperty("MessageAttributes", out var attrs) &&
                        attrs.TryGetProperty("eventType", out var eventTypeProp))
                    {
                        string? value = null;

                        // Standard SNS format: { "Type": "String", "Value": "..." }
                        if (eventTypeProp.TryGetProperty("Value", out var valueProp))
                        {
                            value = valueProp.GetString();
                        }

                        if (string.Equals(value, eventType, StringComparison.OrdinalIgnoreCase))
                        {
                            _output.WriteLine($"Found message matching eventType '{eventType}'");
                            return msg;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _output.WriteLine($"Warning: Failed to parse SQS message body: {ex.Message}");
                }
            }

            _output.WriteLine($"No message found matching eventType '{eventType}' among {messages.Count} messages");
            return null;
        }

        /// <summary>
        /// Extracts the inner event payload JSON from an SNS notification wrapper SQS message.
        /// The SNS wrapper's "Message" field contains the serialized domain event as a JSON string.
        /// </summary>
        /// <param name="message">The SQS message containing the SNS wrapper.</param>
        /// <returns>Parsed JsonDocument of the inner event payload. Caller must dispose.</returns>
        private JsonDocument ExtractEventPayload(Message message)
        {
            using var snsDoc = JsonDocument.Parse(message.Body);
            var innerJson = snsDoc.RootElement.GetProperty("Message").GetString();
            return JsonDocument.Parse(innerJson!);
        }

        /// <summary>
        /// Builds a fully-formed APIGatewayHttpApiV2ProxyRequest for invoking Lambda handlers in tests.
        /// Constructs the RequestContext with HTTP method, JWT authorizer claims, and optional path parameters.
        /// </summary>
        /// <param name="httpMethod">HTTP method (GET, POST, PUT, DELETE).</param>
        /// <param name="rawPath">Full request path (e.g., "/v1/inventory/tasks").</param>
        /// <param name="body">JSON request body, or null for bodyless requests.</param>
        /// <param name="userId">User GUID to set as the JWT "sub" claim.</param>
        /// <param name="pathParameters">Optional dictionary of path parameter names to values.</param>
        /// <returns>Configured APIGatewayHttpApiV2ProxyRequest ready for handler invocation.</returns>
        private static APIGatewayHttpApiV2ProxyRequest BuildApiRequest(
            string httpMethod,
            string rawPath,
            string? body,
            Guid userId,
            Dictionary<string, string>? pathParameters = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = rawPath,
                Body = body,
                PathParameters = pathParameters ?? new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = httpMethod,
                        Path = rawPath
                    },
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                { "sub", userId.ToString() },
                                { "email", "test@webvella.com" },
                                { "cognito:groups", "Administrators" }
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates a TaskHandler instance using the DI ServiceProvider from the fixture,
        /// which has real AWS SDK clients wired to LocalStack.
        /// </summary>
        private TaskHandler CreateTaskHandler()
        {
            using var scope = _fixture.ServiceProvider.CreateScope();
            var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<TaskHandler>();
            return new TaskHandler(taskService, logger);
        }

        /// <summary>
        /// Creates a TimelogHandler instance using the DI ServiceProvider from the fixture,
        /// which has real AWS SDK clients wired to LocalStack.
        /// </summary>
        private TimelogHandler CreateTimelogHandler()
        {
            using var scope = _fixture.ServiceProvider.CreateScope();
            var taskService = scope.ServiceProvider.GetRequiredService<ITaskService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger<TimelogHandler>();
            return new TimelogHandler(taskService, logger);
        }

        /// <summary>
        /// Resolves the IInventoryRepository from the fixture's DI container for direct
        /// data seeding and cleanup operations in DynamoDB.
        /// </summary>
        private IInventoryRepository ResolveRepository()
        {
            var scope = _fixture.ServiceProvider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
        }

        /// <summary>
        /// Purges all messages from the test SQS queue before a test run.
        /// Ensures clean state for event assertion — no leftover messages from prior tests.
        /// </summary>
        private async System.Threading.Tasks.Task PurgeSqsQueue()
        {
            try
            {
                _output.WriteLine("Purging SQS queue...");
                await _fixture.SqsClient.PurgeQueueAsync(new PurgeQueueRequest
                {
                    QueueUrl = _fixture.SqsQueueUrl
                });

                // PurgeQueue has a 60-second cooldown per AWS docs. On LocalStack this is
                // typically instant, but add a brief delay for safety.
                await System.Threading.Tasks.Task.Delay(500);
                _output.WriteLine("SQS queue purged.");
            }
            catch (Amazon.SQS.Model.PurgeQueueInProgressException)
            {
                _output.WriteLine("SQS purge already in progress, waiting...");
                await System.Threading.Tasks.Task.Delay(2000);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Warning: SQS purge failed (non-fatal): {ex.Message}");
                // Non-fatal: proceed with test — stale messages may cause extra assertions
                // but the test should still find its specific event type
            }
        }

        /// <summary>
        /// Cleans up a task from DynamoDB after test execution.
        /// Uses the repository directly to avoid triggering additional domain events.
        /// </summary>
        private async System.Threading.Tasks.Task CleanupTask(Guid taskId)
        {
            try
            {
                var repository = ResolveRepository();
                await repository.DeleteTaskAsync(taskId);
                _output.WriteLine($"Cleaned up task {taskId}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Warning: Failed to clean up task {taskId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Safely deletes a timelog from DynamoDB, ignoring errors for resilient cleanup.
        /// </summary>
        private async System.Threading.Tasks.Task SafeDeleteTimelog(IInventoryRepository repository, Guid timelogId)
        {
            try
            {
                await repository.DeleteTimelogAsync(timelogId);
                _output.WriteLine($"Cleaned up timelog {timelogId}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Warning: Failed to clean up timelog {timelogId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts the created entity's ID from the handler's JSON response body.
        /// The response follows ResponseModel structure: { "object": { "id": "..." } }
        /// </summary>
        private Guid ExtractCreatedEntityId(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // Navigate: root.object.id (snake_case serialization)
                if (root.TryGetProperty("object", out var objectProp))
                {
                    if (objectProp.TryGetProperty("id", out var idProp))
                    {
                        var idStr = idProp.GetString();
                        if (Guid.TryParse(idStr, out var id))
                        {
                            return id;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Warning: Could not extract entity ID from response: {ex.Message}");
            }

            return Guid.Empty;
        }

        #endregion
    }
}
