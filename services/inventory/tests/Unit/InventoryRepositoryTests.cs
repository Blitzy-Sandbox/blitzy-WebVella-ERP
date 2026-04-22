using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Inventory.DataAccess;
using Models = WebVellaErp.Inventory.Models;
using Xunit;

namespace WebVellaErp.Inventory.Tests.Unit
{
    /// <summary>
    /// Comprehensive xUnit unit tests for InventoryRepository DynamoDB single-table operations.
    /// Tests verify key pattern generation (PK/SK/GSI), serialization/deserialization,
    /// conditional expressions for idempotency, batch chunking (25-item limit),
    /// transactional operations (100-item limit), and error handling.
    /// All DynamoDB calls are mocked via Moq — no actual AWS SDK calls.
    /// </summary>
    public class InventoryRepositoryTests
    {
        private readonly Mock<IAmazonDynamoDB> _dynamoDbMock;
        private readonly Mock<ILogger<InventoryRepository>> _loggerMock;
        private readonly Mock<IConfiguration> _configMock;
        private readonly IInventoryRepository _sut;
        private const string TestTableName = "inventory-test-table";

        public InventoryRepositoryTests()
        {
            _dynamoDbMock = new Mock<IAmazonDynamoDB>();
            _loggerMock = new Mock<ILogger<InventoryRepository>>();
            _configMock = new Mock<IConfiguration>();
            _configMock.Setup(c => c["DynamoDB:TableName"]).Returns(TestTableName);
            _sut = new InventoryRepository(
                _dynamoDbMock.Object,
                _loggerMock.Object,
                _configMock.Object);
        }

        #region Helper Methods

        private static Models.Task CreateTestTask(
            Guid? id = null,
            Guid? ownerId = null,
            string? subject = "Test Task",
            decimal number = 42m,
            string? priority = "3",
            string? lRelatedRecords = null,
            DateTime? createdOn = null)
        {
            return new Models.Task
            {
                Id = id ?? Guid.NewGuid(),
                Subject = subject,
                Body = "Test body",
                Number = number,
                Key = "PROJ-42",
                StatusId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                TypeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Priority = priority,
                OwnerId = ownerId,
                StartTime = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2024, 1, 20, 17, 0, 0, DateTimeKind.Utc),
                EstimatedMinutes = 120m,
                TimelogStartedOn = null,
                CompletedOn = null,
                CreatedBy = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CreatedOn = createdOn ?? new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                XBillableMinutes = 100.5m,
                XNonBillableMinutes = 19.5m,
                RecurrenceId = null,
                ReserveTime = true,
                LScope = "project",
                LRelatedRecords = lRelatedRecords ?? "[\"33333333-3333-3333-3333-333333333333\"]"
            };
        }

        private static Models.Timelog CreateTestTimelog(Guid? id = null, Guid? createdBy = null)
        {
            return new Models.Timelog
            {
                Id = id ?? Guid.NewGuid(),
                CreatedBy = createdBy ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CreatedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                LoggedOn = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                Minutes = 60,
                IsBillable = true,
                Body = "Test timelog",
                LScope = "project",
                LRelatedRecords = "[\"33333333-3333-3333-3333-333333333333\"]"
            };
        }

        private static Models.Comment CreateTestComment(Guid? id = null, Guid? parentId = null)
        {
            return new Models.Comment
            {
                Id = id ?? Guid.NewGuid(),
                Body = "Test comment",
                ParentId = parentId,
                CreatedBy = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CreatedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                LScope = "project",
                LRelatedRecords = "[\"33333333-3333-3333-3333-333333333333\"]"
            };
        }

        private static Models.FeedItem CreateTestFeedItem(Guid? id = null)
        {
            return new Models.FeedItem
            {
                Id = id ?? Guid.NewGuid(),
                Subject = "Test feed item",
                Body = "Feed body",
                Type = "task_created",
                CreatedBy = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CreatedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                LRelatedRecords = "[\"33333333-3333-3333-3333-333333333333\"]",
                LScope = "project"
            };
        }

        private static Models.Project CreateTestProject(Guid? id = null, Guid? ownerId = null)
        {
            return new Models.Project
            {
                Id = id ?? Guid.NewGuid(),
                Name = "Test Project",
                Abbr = "PROJ",
                OwnerId = ownerId ?? Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CreatedBy = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                CreatedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc)
            };
        }

        private static Models.TaskType CreateTestTaskType(Guid? id = null, string label = "Bug")
        {
            return new Models.TaskType
            {
                Id = id ?? Guid.NewGuid(),
                Label = label
            };
        }

        private static Models.TaskStatus CreateTestTaskStatus(
            Guid? id = null, string label = "Open", int sortOrder = 1, bool isClosed = false)
        {
            return new Models.TaskStatus
            {
                Id = id ?? Guid.NewGuid(),
                Label = label,
                IsClosed = isClosed,
                SortOrder = sortOrder
            };
        }

        /// <summary>
        /// Creates a complete DynamoDB attribute map representing a persisted task,
        /// suitable for use as a GetItemResponse or QueryResponse item.
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateFullTaskAttributeMap(Guid taskId)
        {
            var createdBy = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var createdOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            var statusId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var typeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var ownerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = taskId.ToString() },
                ["subject"] = new AttributeValue { S = "Test Task" },
                ["body"] = new AttributeValue { S = "Test body" },
                ["number"] = new AttributeValue { N = "42" },
                ["key"] = new AttributeValue { S = "PROJ-42" },
                ["status_id"] = new AttributeValue { S = statusId.ToString() },
                ["type_id"] = new AttributeValue { S = typeId.ToString() },
                ["priority"] = new AttributeValue { S = "3" },
                ["owner_id"] = new AttributeValue { S = ownerId.ToString() },
                ["start_time"] = new AttributeValue { S = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc).ToString("O") },
                ["end_time"] = new AttributeValue { S = new DateTime(2024, 1, 20, 17, 0, 0, DateTimeKind.Utc).ToString("O") },
                ["estimated_minutes"] = new AttributeValue { N = "120" },
                ["created_by"] = new AttributeValue { S = createdBy.ToString() },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("O") },
                ["x_billable_minutes"] = new AttributeValue { N = "100.5" },
                ["x_nonbillable_minutes"] = new AttributeValue { N = "19.5" },
                ["reserve_time"] = new AttributeValue { BOOL = true },
                ["l_scope"] = new AttributeValue { S = "project" },
                ["l_related_records"] = new AttributeValue { S = "[\"33333333-3333-3333-3333-333333333333\"]" },
            };
        }

        /// <summary>
        /// Creates a complete DynamoDB attribute map for a persisted timelog.
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateFullTimelogAttributeMap(Guid timelogId)
        {
            var createdBy = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var createdOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            var loggedOn = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TIMELOG#{timelogId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = timelogId.ToString() },
                ["created_by"] = new AttributeValue { S = createdBy.ToString() },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("O") },
                ["logged_on"] = new AttributeValue { S = loggedOn.ToString("O") },
                ["minutes"] = new AttributeValue { N = "60" },
                ["is_billable"] = new AttributeValue { BOOL = true },
                ["body"] = new AttributeValue { S = "Test timelog" },
                ["l_scope"] = new AttributeValue { S = "project" },
                ["l_related_records"] = new AttributeValue { S = "[\"33333333-3333-3333-3333-333333333333\"]" },
            };
        }

        /// <summary>
        /// Creates a complete DynamoDB attribute map for a persisted comment.
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateFullCommentAttributeMap(Guid commentId, Guid? parentId = null)
        {
            var createdBy = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var createdOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

            var map = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"COMMENT#{commentId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = commentId.ToString() },
                ["body"] = new AttributeValue { S = "Test comment" },
                ["created_by"] = new AttributeValue { S = createdBy.ToString() },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("O") },
                ["l_scope"] = new AttributeValue { S = "project" },
                ["l_related_records"] = new AttributeValue { S = "[\"33333333-3333-3333-3333-333333333333\"]" },
            };

            if (parentId.HasValue)
            {
                map["parent_id"] = new AttributeValue { S = parentId.Value.ToString() };
            }

            return map;
        }

        #endregion

        #region Phase 2 — DynamoDB Single-Table Key Pattern Tests

        // ═══════════════════════════════════════════════════════════
        // Task Key Patterns
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateTaskAsync_ShouldUsePK_TASK_HashTaskId()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var task = CreateTestTask(id: taskId, ownerId: Guid.NewGuid());

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"TASK#{taskId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
            capturedRequest.TableName.Should().Be(TestTableName);
        }

        [Fact]
        public async Task CreateTaskAsync_ShouldSetGSI1PK_EntityTask()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["GSI1PK"].S.Should().Be("ENTITY#task");
        }

        [Fact]
        public async Task CreateTaskAsync_ShouldSetGSI2PK_UserOwnerId()
        {
            var ownerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var task = CreateTestTask(ownerId: ownerId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("GSI2PK");
            capturedRequest.Item["GSI2PK"].S.Should().Be($"USER#{ownerId}");
        }

        [Fact]
        public async Task CreateTaskAsync_ShouldSetGSI3PK_ProjectId()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var projectId = "33333333-3333-3333-3333-333333333333";
            var task = CreateTestTask(
                id: taskId,
                ownerId: Guid.NewGuid(),
                lRelatedRecords: $"[\"{projectId}\"]");

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("GSI3PK");
            capturedRequest.Item["GSI3PK"].S.Should().Be($"PROJECT#{projectId}");
            capturedRequest.Item["GSI3SK"].S.Should().Be($"TASK#{taskId}");
        }

        [Fact]
        public async Task CreateTaskAsync_ShouldOmitGSI2_WhenOwnerIdIsNull()
        {
            var task = CreateTestTask(ownerId: null);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().NotContainKey("GSI2PK");
        }

        // ═══════════════════════════════════════════════════════════
        // Timelog Key Patterns
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateTimelogAsync_ShouldUsePK_TIMELOG_HashTimelogId()
        {
            var timelogId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var timelog = CreateTestTimelog(id: timelogId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTimelogAsync(timelog);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"TIMELOG#{timelogId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
        }

        [Fact]
        public async Task CreateTimelogAsync_ShouldSetGSI1PK_EntityTimelog()
        {
            var timelog = CreateTestTimelog();

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTimelogAsync(timelog);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["GSI1PK"].S.Should().Be("ENTITY#timelog");
        }

        [Fact]
        public async Task CreateTimelogAsync_ShouldSetGSI2PK_UserCreatedBy()
        {
            var createdBy = Guid.Parse("66666666-6666-6666-6666-666666666666");
            var timelog = CreateTestTimelog(createdBy: createdBy);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTimelogAsync(timelog);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("GSI2PK");
            capturedRequest.Item["GSI2PK"].S.Should().Be($"USER#{createdBy}");
        }

        // ═══════════════════════════════════════════════════════════
        // Project Key Patterns
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateProjectAsync_ShouldUsePK_PROJECT_HashProjectId()
        {
            var projectId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            var project = CreateTestProject(id: projectId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateProjectAsync(project);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"PROJECT#{projectId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
        }

        // ═══════════════════════════════════════════════════════════
        // Comment Key Patterns
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateCommentAsync_ShouldUsePK_COMMENT_HashCommentId()
        {
            var commentId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var comment = CreateTestComment(id: commentId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateCommentAsync(comment);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"COMMENT#{commentId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
        }

        [Fact]
        public async Task CreateCommentAsync_ShouldSetGSI1PK_ParentId_WhenReply()
        {
            var parentId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            var comment = CreateTestComment(parentId: parentId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateCommentAsync(comment);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["GSI1PK"].S.Should().Be($"PARENT#{parentId}");
        }

        [Fact]
        public async Task CreateCommentAsync_ShouldSetGSI1PK_EntityComment_WhenTopLevel()
        {
            var comment = CreateTestComment(parentId: null);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateCommentAsync(comment);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["GSI1PK"].S.Should().Be("ENTITY#comment");
        }

        // ═══════════════════════════════════════════════════════════
        // FeedItem Key Patterns
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateFeedItemAsync_ShouldUsePK_FEEDITEM_HashFeedItemId()
        {
            var feedItemId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var feedItem = CreateTestFeedItem(id: feedItemId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateFeedItemAsync(feedItem);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"FEEDITEM#{feedItemId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
        }

        // ═══════════════════════════════════════════════════════════
        // TaskType / TaskStatus Key Patterns
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateTaskTypeAsync_ShouldUsePK_TASKTYPE_HashId()
        {
            var typeId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
            var taskType = CreateTestTaskType(id: typeId, label: "Feature");

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskTypeAsync(taskType);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"TASK_TYPE#{typeId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
            capturedRequest.Item["GSI1PK"].S.Should().Be("ENTITY#task_type");
        }

        [Fact]
        public async Task CreateTaskStatusAsync_ShouldUsePK_TASKSTATUS_HashId()
        {
            var statusId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            var taskStatus = CreateTestTaskStatus(id: statusId, label: "Done", sortOrder: 5);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskStatusAsync(taskStatus);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"TASK_STATUS#{statusId}");
            capturedRequest.Item["SK"].S.Should().Be("META");
            capturedRequest.Item["GSI1PK"].S.Should().Be("ENTITY#task_status");
        }

        #endregion

        #region Phase 3 — Task Watcher Operation Tests

        [Fact]
        public async Task AddTaskWatcherAsync_ShouldUseSK_WATCHER_HashUserId()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var userId = Guid.Parse("22222222-2222-2222-2222-222222222222");

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.AddTaskWatcherAsync(taskId, userId);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item["PK"].S.Should().Be($"TASK#{taskId}");
            capturedRequest.Item["SK"].S.Should().Be($"WATCHER#{userId}");
        }

        [Fact]
        public async Task RemoveTaskWatcherAsync_ShouldDeleteWatcherItem()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var userId = Guid.Parse("22222222-2222-2222-2222-222222222222");

            _dynamoDbMock
                .Setup(d => d.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteItemResponse());

            await _sut.RemoveTaskWatcherAsync(taskId, userId);

            _dynamoDbMock.Verify(d => d.DeleteItemAsync(
                It.Is<DeleteItemRequest>(r =>
                    r.Key["PK"].S == $"TASK#{taskId}" &&
                    r.Key["SK"].S == $"WATCHER#{userId}"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetTaskWatchersAsync_ShouldQueryWithSKPrefix_WATCHER()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var watcher1 = Guid.Parse("aaaa1111-1111-1111-1111-111111111111");
            var watcher2 = Guid.Parse("bbbb2222-2222-2222-2222-222222222222");
            var watcher3 = Guid.Parse("cccc3333-3333-3333-3333-333333333333");

            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new()
                        {
                            ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                            ["SK"] = new AttributeValue { S = $"WATCHER#{watcher1}" },
                            ["user_id"] = new AttributeValue { S = watcher1.ToString() }
                        },
                        new()
                        {
                            ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                            ["SK"] = new AttributeValue { S = $"WATCHER#{watcher2}" },
                            ["user_id"] = new AttributeValue { S = watcher2.ToString() }
                        },
                        new()
                        {
                            ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                            ["SK"] = new AttributeValue { S = $"WATCHER#{watcher3}" },
                            ["user_id"] = new AttributeValue { S = watcher3.ToString() }
                        }
                    }
                });

            var result = await _sut.GetTaskWatchersAsync(taskId);

            result.Should().HaveCount(3);
            result.Should().Contain(watcher1);
            result.Should().Contain(watcher2);
            result.Should().Contain(watcher3);

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.KeyConditionExpression.Contains("begins_with") &&
                    r.ExpressionAttributeValues[":pk"].S == $"TASK#{taskId}" &&
                    r.ExpressionAttributeValues[":skPrefix"].S == "WATCHER#"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region Phase 4 — Conditional Expression Tests (Idempotency)

        [Fact]
        public async Task CreateTaskAsync_ShouldUseConditionalExpression_AttributeNotExists()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.ConditionExpression.Should().Contain("attribute_not_exists(PK)");
            capturedRequest.ConditionExpression.Should().Contain("attribute_not_exists(SK)");
        }

        [Fact]
        public async Task CreateTimelogAsync_ShouldUseConditionalExpression_AttributeNotExists()
        {
            var timelog = CreateTestTimelog();

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTimelogAsync(timelog);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.ConditionExpression.Should().Contain("attribute_not_exists(PK)");
            capturedRequest.ConditionExpression.Should().Contain("attribute_not_exists(SK)");
        }

        [Fact]
        public async Task CreateCommentAsync_ShouldUseConditionalExpression_AttributeNotExists()
        {
            var comment = CreateTestComment();

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateCommentAsync(comment);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.ConditionExpression.Should().Contain("attribute_not_exists(PK)");
            capturedRequest.ConditionExpression.Should().Contain("attribute_not_exists(SK)");
        }

        [Fact]
        public async Task CreateFeedItemAsync_ShouldUseConditionalExpression_AttributeNotExists()
        {
            var feedItem = CreateTestFeedItem();

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateFeedItemAsync(feedItem);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.ConditionExpression.Should().Contain("attribute_not_exists(PK)");
            capturedRequest.ConditionExpression.Should().Contain("attribute_not_exists(SK)");
        }

        [Fact]
        public async Task CreateTaskAsync_ShouldThrowInvalidOperation_WhenItemAlreadyExists()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Condition not satisfied"));

            Func<Task> act = () => _sut.CreateTaskAsync(task);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already exists*");
        }

        #endregion

        #region Phase 5 — Update Operation Tests

        [Fact]
        public async Task UpdateTaskAsync_ShouldUseConditionExpression_AttributeExists()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.UpdateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.ConditionExpression.Should().Contain("attribute_exists(PK)");
            capturedRequest.ConditionExpression.Should().Contain("attribute_exists(SK)");
        }

        [Fact]
        public async Task UpdateTaskAsync_ShouldThrowInvalidOperation_WhenTaskNotFound()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Condition not satisfied"));

            Func<Task> act = () => _sut.UpdateTaskAsync(task);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*update task*not found*");
        }

        [Fact]
        public async Task UpdateTaskAsync_ShouldUpdateGSIAttributes_WhenOwnerChanges()
        {
            var newOwnerId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            var task = CreateTestTask(ownerId: newOwnerId);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.UpdateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Item.Should().ContainKey("GSI2PK");
            capturedRequest.Item["GSI2PK"].S.Should().Be($"USER#{newOwnerId}");
        }

        #endregion

        #region Phase 6 — Delete Operation Tests

        [Fact]
        public async Task DeleteTaskAsync_ShouldThrow_WhenTaskNotFound()
        {
            var taskId = Guid.NewGuid();

            // Mock GetItemAsync to return empty (task not found)
            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });

            Func<Task> act = () => _sut.DeleteTaskAsync(taskId);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*no task*delete*");
        }

        [Fact]
        public async Task DeleteTaskAsync_ShouldDeleteMainItemAndAllWatchers()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var taskPk = $"TASK#{taskId}";

            // Mock GetItemAsync to return a valid task (existence check)
            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateFullTaskAttributeMap(taskId)
                });

            // Mock QueryAsync to return META item + 3 watcher items
            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        new()
                        {
                            ["PK"] = new AttributeValue { S = taskPk },
                            ["SK"] = new AttributeValue { S = "META" }
                        },
                        new()
                        {
                            ["PK"] = new AttributeValue { S = taskPk },
                            ["SK"] = new AttributeValue { S = $"WATCHER#{Guid.NewGuid()}" }
                        },
                        new()
                        {
                            ["PK"] = new AttributeValue { S = taskPk },
                            ["SK"] = new AttributeValue { S = $"WATCHER#{Guid.NewGuid()}" }
                        },
                        new()
                        {
                            ["PK"] = new AttributeValue { S = taskPk },
                            ["SK"] = new AttributeValue { S = $"WATCHER#{Guid.NewGuid()}" }
                        }
                    }
                });

            // Mock BatchWriteItemAsync to succeed
            _dynamoDbMock
                .Setup(d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BatchWriteItemResponse());

            await _sut.DeleteTaskAsync(taskId);

            // Verify batch delete was called with 4 items (1 META + 3 watchers)
            _dynamoDbMock.Verify(d => d.BatchWriteItemAsync(
                It.Is<BatchWriteItemRequest>(r =>
                    r.RequestItems.ContainsKey(TestTableName) &&
                    r.RequestItems[TestTableName].Count == 4),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region Phase 7 — Batch/Transactional Operation Tests

        [Fact]
        public async Task BatchCreateItemsAsync_ShouldChunkInBatchesOf25()
        {
            var items = Enumerable.Range(0, 60)
                .Select(i => CreateTestTaskType(id: Guid.NewGuid(), label: $"Type_{i}"))
                .ToList();

            _dynamoDbMock
                .Setup(d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BatchWriteItemResponse());

            await _sut.BatchCreateItemsAsync(items, item => new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_TYPE#{item.Id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = item.Id.ToString() },
                ["label"] = new AttributeValue { S = item.Label }
            });

            // 60 items / 25 per batch = 3 batches (25 + 25 + 10)
            _dynamoDbMock.Verify(
                d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Exactly(3));
        }

        [Fact]
        public async Task BatchCreateItemsAsync_ShouldRetryUnprocessedItems()
        {
            var items = Enumerable.Range(0, 5)
                .Select(i => CreateTestTaskType(id: Guid.NewGuid(), label: $"Type_{i}"))
                .ToList();

            // First call: returns 1 unprocessed item requiring retry
            var unprocessedItem = new WriteRequest
            {
                PutRequest = new PutRequest
                {
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"TASK_TYPE#{Guid.NewGuid()}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                }
            };

            var firstResponse = new BatchWriteItemResponse
            {
                UnprocessedItems = new Dictionary<string, List<WriteRequest>>
                {
                    [TestTableName] = new List<WriteRequest> { unprocessedItem }
                }
            };

            // Second call: all items processed
            var secondResponse = new BatchWriteItemResponse
            {
                UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
            };

            _dynamoDbMock
                .SetupSequence(d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstResponse)
                .ReturnsAsync(secondResponse);

            await _sut.BatchCreateItemsAsync(items, item => new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK_TYPE#{item.Id}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = item.Id.ToString() },
                ["label"] = new AttributeValue { S = item.Label }
            });

            // Original call + retry = 2 calls
            _dynamoDbMock.Verify(
                d => d.BatchWriteItemAsync(It.IsAny<BatchWriteItemRequest>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task TransactWriteAsync_ShouldPassAllItemsToTransactWriteItemsAsync()
        {
            var transactItems = Enumerable.Range(0, 5).Select(i => new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = TestTableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"TASK#{Guid.NewGuid()}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                }
            }).ToList();

            _dynamoDbMock
                .Setup(d => d.TransactWriteItemsAsync(It.IsAny<TransactWriteItemsRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TransactWriteItemsResponse());

            await _sut.TransactWriteAsync(transactItems);

            _dynamoDbMock.Verify(d => d.TransactWriteItemsAsync(
                It.Is<TransactWriteItemsRequest>(r => r.TransactItems.Count == 5),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task TransactWriteAsync_ShouldEnforce100ItemLimit()
        {
            var transactItems = Enumerable.Range(0, 101).Select(i => new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = TestTableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"TASK#{Guid.NewGuid()}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                }
            }).ToList();

            Func<Task> act = () => _sut.TransactWriteAsync(transactItems);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*maximum of 100*");
        }

        #endregion

        #region Phase 8 — Serialization / Deserialization Tests

        [Fact]
        public async Task SerializeTask_ShouldMapGuidToStringAttribute()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var task = CreateTestTask(id: taskId, ownerId: Guid.NewGuid());

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            // Guid serialized as DynamoDB String (S) attribute
            capturedRequest!.Item["id"].S.Should().Be(taskId.ToString());
        }

        [Fact]
        public async Task SerializeTask_ShouldMapDecimalToNumberAttribute()
        {
            var task = CreateTestTask(number: 42m, ownerId: Guid.NewGuid());
            task.XBillableMinutes = 100.5m;

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            // Decimal serialized as DynamoDB Number (N) attribute using InvariantCulture
            capturedRequest!.Item["number"].N.Should().Be("42");
            capturedRequest.Item["x_billable_minutes"].N.Should().Be("100.5");
        }

        [Fact]
        public async Task SerializeTask_ShouldMapDateTimeToISO8601String()
        {
            var createdOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            var task = CreateTestTask(ownerId: Guid.NewGuid(), createdOn: createdOn);

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            // DateTime serialized as ISO 8601 "O" format in DynamoDB String (S) attribute
            capturedRequest!.Item["created_on"].S.Should().Be(createdOn.ToString("O"));
        }

        [Fact]
        public async Task SerializeTask_ShouldMapBoolToBoolAttribute()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());
            task.ReserveTime = true;

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            // Boolean serialized as DynamoDB BOOL attribute
            capturedRequest!.Item["reserve_time"].BOOL.Should().BeTrue();
        }

        [Fact]
        public async Task SerializeTask_ShouldSkipNullDateTime()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());
            task.StartTime = null;
            task.CompletedOn = null;

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            // Null DateTime fields should NOT be present in the DynamoDB item
            capturedRequest!.Item.Should().NotContainKey("completed_on");
        }

        [Fact]
        public async Task SerializeTask_ShouldSkipNullGuid()
        {
            var task = CreateTestTask(ownerId: null);
            task.RecurrenceId = null;

            PutItemRequest? capturedRequest = null;
            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new PutItemResponse());

            await _sut.CreateTaskAsync(task);

            capturedRequest.Should().NotBeNull();
            // Null Guid? fields should NOT be present in the DynamoDB item
            capturedRequest!.Item.Should().NotContainKey("owner_id");
            capturedRequest.Item.Should().NotContainKey("recurrence_id");
        }

        [Fact]
        public async Task DeserializeTask_ShouldReconstructTaskFromAttributes()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var createdBy = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var statusId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var typeId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var ownerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var createdOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            var startTime = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc);

            var attributeMap = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = taskId.ToString() },
                ["subject"] = new AttributeValue { S = "Deserialized Task" },
                ["body"] = new AttributeValue { S = "Test body content" },
                ["number"] = new AttributeValue { N = "42" },
                ["key"] = new AttributeValue { S = "PROJ-42" },
                ["status_id"] = new AttributeValue { S = statusId.ToString() },
                ["type_id"] = new AttributeValue { S = typeId.ToString() },
                ["priority"] = new AttributeValue { S = "3" },
                ["owner_id"] = new AttributeValue { S = ownerId.ToString() },
                ["start_time"] = new AttributeValue { S = startTime.ToString("O") },
                ["estimated_minutes"] = new AttributeValue { N = "120" },
                ["created_by"] = new AttributeValue { S = createdBy.ToString() },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("O") },
                ["x_billable_minutes"] = new AttributeValue { N = "100.5" },
                ["x_nonbillable_minutes"] = new AttributeValue { N = "19.5" },
                ["reserve_time"] = new AttributeValue { BOOL = true },
                ["l_scope"] = new AttributeValue { S = "project" },
                ["l_related_records"] = new AttributeValue { S = "[\"33333333-3333-3333-3333-333333333333\"]" },
            };

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = attributeMap });

            var result = await _sut.GetTaskByIdAsync(taskId);

            result.Should().NotBeNull();
            result!.Id.Should().Be(taskId);
            result.Subject.Should().Be("Deserialized Task");
            result.Body.Should().Be("Test body content");
            result.Number.Should().Be(42m);
            result.Key.Should().Be("PROJ-42");
            result.StatusId.Should().Be(statusId);
            result.TypeId.Should().Be(typeId);
            result.Priority.Should().Be("3");
            result.OwnerId.Should().Be(ownerId);
            result.StartTime.Should().Be(startTime);
            result.EstimatedMinutes.Should().Be(120m);
            result.CreatedBy.Should().Be(createdBy);
            result.CreatedOn.Should().Be(createdOn);
            result.XBillableMinutes.Should().Be(100.5m);
            result.XNonBillableMinutes.Should().Be(19.5m);
            result.ReserveTime.Should().BeTrue();
            result.LScope.Should().Be("project");
            result.LRelatedRecords.Should().Be("[\"33333333-3333-3333-3333-333333333333\"]");
        }

        [Fact]
        public async Task DeserializeTask_ShouldHandleMissingAttributes_Gracefully()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");

            // Minimal attribute map — only PK, SK, id
            var minimalMap = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"TASK#{taskId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["id"] = new AttributeValue { S = taskId.ToString() },
            };

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = minimalMap });

            var result = await _sut.GetTaskByIdAsync(taskId);

            // Should return a task with defaults for missing fields, not throw
            result.Should().NotBeNull();
            result!.Id.Should().Be(taskId);
            result.Subject.Should().BeEmpty();
            result.Number.Should().Be(0m);
            result.OwnerId.Should().BeNull();
            result.StartTime.Should().BeNull();
            result.ReserveTime.Should().BeFalse();
        }

        [Fact]
        public async Task DeserializeTimelog_ShouldReconstructTimelogFromAttributes()
        {
            var timelogId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var createdBy = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var loggedOn = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

            var attributeMap = CreateFullTimelogAttributeMap(timelogId);

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = attributeMap });

            var result = await _sut.GetTimelogByIdAsync(timelogId);

            result.Should().NotBeNull();
            result!.Id.Should().Be(timelogId);
            result.CreatedBy.Should().Be(createdBy);
            result.LoggedOn.Should().Be(loggedOn);
            result.Minutes.Should().Be(60);
            result.IsBillable.Should().BeTrue();
            result.Body.Should().Be("Test timelog");
        }

        [Fact]
        public async Task DeserializeComment_ShouldReconstructCommentFromAttributes()
        {
            var commentId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var parentId = Guid.Parse("99999999-9999-9999-9999-999999999999");

            var attributeMap = CreateFullCommentAttributeMap(commentId, parentId: parentId);

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = attributeMap });

            var result = await _sut.GetCommentByIdAsync(commentId);

            result.Should().NotBeNull();
            result!.Id.Should().Be(commentId);
            result.ParentId.Should().Be(parentId);
            result.Body.Should().Be("Test comment");
            result.CreatedBy.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        }

        #endregion

        #region Phase 9 — GSI Query Tests

        [Fact]
        public async Task GetTasksByProjectAsync_ShouldQueryGSI3()
        {
            var projectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var taskId = Guid.NewGuid();

            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateFullTaskAttributeMap(taskId)
                    }
                });

            var tasks = await _sut.GetTasksByProjectAsync(projectId);

            tasks.Should().HaveCount(1);

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI3" &&
                    r.KeyConditionExpression.Contains("GSI3PK") &&
                    r.KeyConditionExpression.Contains("begins_with") &&
                    r.ExpressionAttributeValues[":pk"].S == $"PROJECT#{projectId}" &&
                    r.ExpressionAttributeValues[":skPrefix"].S == "TASK#"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetTasksByOwnerAsync_ShouldQueryGSI2()
        {
            var ownerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetTasksByOwnerAsync(ownerId);

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI2" &&
                    r.KeyConditionExpression.Contains("GSI2PK") &&
                    r.ExpressionAttributeValues[":pk"].S == $"USER#{ownerId}" &&
                    r.FilterExpression != null &&
                    r.FilterExpression.Contains("begins_with(PK")),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetAllTaskStatusesAsync_ShouldQueryGSI1()
        {
            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetAllTaskStatusesAsync();

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI1" &&
                    r.ExpressionAttributeValues[":pk"].S == "ENTITY#task_status"),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task GetAllTaskTypesAsync_ShouldQueryGSI1()
        {
            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetAllTaskTypesAsync();

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI1" &&
                    r.ExpressionAttributeValues[":pk"].S == "ENTITY#task_type"),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task GetCommentsByParentAsync_ShouldQueryGSI1_WithParentPrefix()
        {
            var parentId = Guid.Parse("99999999-9999-9999-9999-999999999999");

            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetCommentsByParentAsync(parentId);

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI1" &&
                    r.ExpressionAttributeValues[":pk"].S == $"PARENT#{parentId}"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetTimelogsByDateRangeAsync_ShouldQueryGSI1_WithBetweenCondition()
        {
            var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = new DateTime(2024, 1, 31, 23, 59, 59, DateTimeKind.Utc);

            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetTimelogsByDateRangeAsync(startDate, endDate);

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI1" &&
                    r.KeyConditionExpression.Contains("BETWEEN") &&
                    r.ExpressionAttributeValues[":pk"].S == "ENTITY#timelog" &&
                    r.ExpressionAttributeValues[":start"].S == startDate.ToString("O") &&
                    r.ExpressionAttributeValues[":end"].S.StartsWith(endDate.ToString("O"))),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        }

        [Fact]
        public async Task GetFeedItemsByRelatedRecordAsync_ShouldQueryGSI1_WithFilterExpression()
        {
            var relatedRecordId = "33333333-3333-3333-3333-333333333333";

            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetFeedItemsByRelatedRecordAsync(relatedRecordId);

            _dynamoDbMock.Verify(d => d.QueryAsync(
                It.Is<QueryRequest>(r =>
                    r.IndexName == "GSI1" &&
                    r.ExpressionAttributeValues[":pk"].S == "ENTITY#feeditem" &&
                    r.FilterExpression != null &&
                    r.FilterExpression.Contains("contains") &&
                    r.FilterExpression.Contains("l_related_records") &&
                    r.ExpressionAttributeValues[":relatedId"].S == relatedRecordId),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region Phase 10 — Read/Get by ID Tests

        [Fact]
        public async Task GetTaskByIdAsync_ShouldCallGetItemAsync_WithCorrectKey()
        {
            var taskId = Guid.Parse("11111111-1111-1111-1111-111111111111");

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateFullTaskAttributeMap(taskId)
                });

            await _sut.GetTaskByIdAsync(taskId);

            _dynamoDbMock.Verify(d => d.GetItemAsync(
                It.Is<GetItemRequest>(r =>
                    r.TableName == TestTableName &&
                    r.Key["PK"].S == $"TASK#{taskId}" &&
                    r.Key["SK"].S == "META"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetTaskByIdAsync_ShouldReturnNull_WhenItemNotFound()
        {
            var taskId = Guid.NewGuid();

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });

            var result = await _sut.GetTaskByIdAsync(taskId);

            result.Should().BeNull();
        }

        [Fact]
        public async Task GetTimelogByIdAsync_ShouldCallGetItemAsync_WithCorrectKey()
        {
            var timelogId = Guid.Parse("55555555-5555-5555-5555-555555555555");

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateFullTimelogAttributeMap(timelogId)
                });

            await _sut.GetTimelogByIdAsync(timelogId);

            _dynamoDbMock.Verify(d => d.GetItemAsync(
                It.Is<GetItemRequest>(r =>
                    r.Key["PK"].S == $"TIMELOG#{timelogId}" &&
                    r.Key["SK"].S == "META"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetCommentByIdAsync_ShouldCallGetItemAsync_WithCorrectKey()
        {
            var commentId = Guid.Parse("88888888-8888-8888-8888-888888888888");

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateFullCommentAttributeMap(commentId)
                });

            await _sut.GetCommentByIdAsync(commentId);

            _dynamoDbMock.Verify(d => d.GetItemAsync(
                It.Is<GetItemRequest>(r =>
                    r.Key["PK"].S == $"COMMENT#{commentId}" &&
                    r.Key["SK"].S == "META"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        [Fact]
        public async Task GetProjectByIdAsync_ShouldCallGetItemAsync_WithCorrectKey()
        {
            var projectId = Guid.Parse("77777777-7777-7777-7777-777777777777");

            _dynamoDbMock
                .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"PROJECT#{projectId}" },
                        ["SK"] = new AttributeValue { S = "META" },
                        ["id"] = new AttributeValue { S = projectId.ToString() },
                        ["name"] = new AttributeValue { S = "Test Project" },
                        ["abbr"] = new AttributeValue { S = "PROJ" },
                        ["owner_id"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                        ["created_by"] = new AttributeValue { S = Guid.NewGuid().ToString() },
                        ["created_on"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") },
                    }
                });

            await _sut.GetProjectByIdAsync(projectId);

            _dynamoDbMock.Verify(d => d.GetItemAsync(
                It.Is<GetItemRequest>(r =>
                    r.Key["PK"].S == $"PROJECT#{projectId}" &&
                    r.Key["SK"].S == "META"),
                It.IsAny<CancellationToken>()), Times.Once());
        }

        #endregion

        #region Phase 11 — Error Handling Tests

        [Fact]
        public async Task CreateTaskAsync_ShouldLogAndThrow_OnDynamoDBException()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonDynamoDBException("DynamoDB service error"));

            Func<Task> act = () => _sut.CreateTaskAsync(task);

            await act.Should().ThrowAsync<AmazonDynamoDBException>()
                .WithMessage("*DynamoDB*error*");
        }

        [Fact]
        public async Task UpdateTaskAsync_ShouldTranslateConditionalCheckFailed_ToInvalidOperation()
        {
            var task = CreateTestTask(ownerId: Guid.NewGuid());

            _dynamoDbMock
                .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("Condition not met"));

            Func<Task> act = () => _sut.UpdateTaskAsync(task);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        #endregion

        #region Phase 12 — Pagination Tests

        [Fact]
        public async Task GetTasksByProjectAsync_ShouldPassExclusiveStartKey_WhenProvided()
        {
            var projectId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            // Build a Base64-encoded pagination key (matches DeserializePaginationKey format)
            var paginationKeyDict = new Dictionary<string, string>
            {
                { "GSI3PK", $"PROJECT#{projectId}" },
                { "GSI3SK", $"TASK#{Guid.NewGuid()}" },
                { "PK", $"TASK#{Guid.NewGuid()}" },
                { "SK", "META" }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(paginationKeyDict);
            var base64Key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

            QueryRequest? capturedQuery = null;
            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedQuery = req)
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetTasksByProjectAsync(projectId, exclusiveStartKey: base64Key);

            capturedQuery.Should().NotBeNull();
            capturedQuery!.ExclusiveStartKey.Should().NotBeNull();
            capturedQuery.ExclusiveStartKey.Should().NotBeEmpty();
        }

        [Fact]
        public async Task GetTasksByProjectAsync_ShouldApplyLimit_WhenProvided()
        {
            var projectId = Guid.Parse("33333333-3333-3333-3333-333333333333");

            QueryRequest? capturedQuery = null;
            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedQuery = req)
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetTasksByProjectAsync(projectId, limit: 10);

            capturedQuery.Should().NotBeNull();
            capturedQuery!.Limit.Should().Be(10);
        }

        [Fact]
        public async Task GetAllProjectsAsync_ShouldScanForward_ForAlphabeticalOrder()
        {
            QueryRequest? capturedQuery = null;
            _dynamoDbMock
                .Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedQuery = req)
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            await _sut.GetAllProjectsAsync();

            capturedQuery.Should().NotBeNull();
            capturedQuery!.ScanIndexForward.Should().BeTrue();
        }

        #endregion
    }
}
