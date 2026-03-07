using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.Service.Reporting.Events;

namespace WebVella.Erp.Tests.Reporting.Events
{
    /// <summary>
    /// Unit tests for <see cref="TaskUpdatedConsumer"/>, the MassTransit event consumer
    /// in the Reporting service that processes <see cref="RecordUpdatedEvent"/> events
    /// where <c>EntityName == "task"</c>.
    ///
    /// Validates the consumer's conversion of the monolith's synchronous hook pattern
    /// (<c>[HookAttachment("task")] IErpPostUpdateRecordHook</c> in
    /// WebVella.Erp.Plugins.Project/Hooks/Api/Task.cs lines 26-28, delegating to
    /// <c>TaskService.PostUpdateApiHookLogic</c>) into an asynchronous event-driven
    /// consumer that performs idempotent upserts on <see cref="TaskProjection"/>.
    ///
    /// Test scenarios cover:
    /// - Entity name filtering (only "task" events are processed)
    /// - New task projection creation from event data
    /// - Existing task projection update (upsert)
    /// - Idempotent duplicate event handling (AAP 0.8.2)
    /// - Subject extraction from <c>NewRecord["subject"]</c>
    /// - Task type label extraction from <c>$task_type_1n_task</c> relation
    ///   (maps to ReportService.cs line 111)
    /// - Graceful handling of missing or empty relation data
    ///
    /// Uses EF Core InMemory database for isolation, Moq for ILogger and ConsumeContext,
    /// and FluentAssertions for readable assertions per AAP 0.8.2 testing standards.
    /// </summary>
    public class TaskUpdatedConsumerTests : IDisposable
    {
        private readonly ReportingDbContext _dbContext;
        private readonly Mock<ILogger<TaskUpdatedConsumer>> _loggerMock;
        private readonly TaskUpdatedConsumer _consumer;

        /// <summary>
        /// Initializes test infrastructure with a unique InMemory EF Core database,
        /// a mocked ILogger, and a fully constructed <see cref="TaskUpdatedConsumer"/>
        /// instance. Each test instance gets its own isolated database to prevent
        /// cross-test state pollution.
        /// </summary>
        public TaskUpdatedConsumerTests()
        {
            // Create a unique InMemory database per test instance to avoid shared state.
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _dbContext = new ReportingDbContext(options);
            _loggerMock = new Mock<ILogger<TaskUpdatedConsumer>>();
            _consumer = new TaskUpdatedConsumer(_dbContext, _loggerMock.Object);
        }

        /// <summary>
        /// Disposes the EF Core InMemory database context after each test completes
        /// to release resources and ensure clean state.
        /// </summary>
        public void Dispose()
        {
            _dbContext.Dispose();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="RecordUpdatedEvent"/> configured for a task entity update.
        /// Builds the <see cref="EntityRecord"/> NewRecord with task field values that
        /// mirror the monolith's record structure used by TaskService and ReportService.
        /// </summary>
        /// <param name="taskId">The unique identifier for the task record.</param>
        /// <param name="subject">
        /// The task subject string. Maps to <c>task["subject"]</c> in the monolith's
        /// TaskService.SetCalculationFields() and <c>rec["task_subject"] = task["subject"]</c>
        /// in ReportService.cs line 109.
        /// </param>
        /// <param name="taskTypeLabel">
        /// Optional task type label. When provided, builds the <c>$task_type_1n_task</c>
        /// relation list matching the pattern from ReportService.cs line 111:
        /// <c>((List&lt;EntityRecord&gt;)task["$task_type_1n_task"])[0]["label"]</c>
        /// and TaskService.SetCalculationFields() lines 66-73.
        /// </param>
        /// <returns>A fully constructed <see cref="RecordUpdatedEvent"/> for test consumption.</returns>
        private static RecordUpdatedEvent CreateTaskUpdatedEvent(
            Guid taskId,
            string subject,
            string taskTypeLabel = null)
        {
            var newRecord = new EntityRecord();
            newRecord["id"] = taskId;
            newRecord["subject"] = subject;

            // Build the $task_type_1n_task relation list if a label is provided.
            // This mirrors the EQL result shape from the monolith:
            //   SELECT ..., $task_type_1n_task.label FROM task
            // where the relation returns a List<EntityRecord> with a "label" field.
            if (taskTypeLabel != null)
            {
                var taskTypeRecord = new EntityRecord();
                taskTypeRecord["label"] = taskTypeLabel;
                newRecord["$task_type_1n_task"] = new List<EntityRecord> { taskTypeRecord };
            }

            var oldRecord = new EntityRecord();

            return new RecordUpdatedEvent
            {
                EntityName = "task",
                NewRecord = newRecord,
                OldRecord = oldRecord,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Creates a mock <see cref="ConsumeContext{T}"/> wrapping the given
        /// <see cref="RecordUpdatedEvent"/> message. The mock's <c>Message</c>
        /// property returns the provided event, and <c>CancellationToken</c>
        /// returns <see cref="CancellationToken.None"/>.
        /// </summary>
        /// <param name="message">The domain event to wrap in the consume context.</param>
        /// <returns>A configured Moq mock of <see cref="ConsumeContext{RecordUpdatedEvent}"/>.</returns>
        private static Mock<ConsumeContext<RecordUpdatedEvent>> CreateConsumeContext(
            RecordUpdatedEvent message)
        {
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(message);
            contextMock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return contextMock;
        }

        /// <summary>
        /// Creates a <see cref="RecordUpdatedEvent"/> with a custom entity name
        /// (not "task") to test entity name filtering logic. The NewRecord still
        /// contains valid task-like fields to ensure filtering is based solely
        /// on EntityName, not record content.
        /// </summary>
        /// <param name="entityName">The entity name to set on the event (e.g., "project", "timelog").</param>
        /// <returns>A <see cref="RecordUpdatedEvent"/> with the specified entity name.</returns>
        private static RecordUpdatedEvent CreateNonTaskEvent(string entityName)
        {
            var newRecord = new EntityRecord();
            newRecord["id"] = Guid.NewGuid();
            newRecord["subject"] = "Some subject";

            var oldRecord = new EntityRecord();

            return new RecordUpdatedEvent
            {
                EntityName = entityName,
                NewRecord = newRecord,
                OldRecord = oldRecord,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        #endregion

        #region Entity Name Filter Tests

        /// <summary>
        /// Verifies that events with EntityName != "task" are silently ignored.
        /// The consumer should return immediately without creating any TaskProjection
        /// records, matching the monolith's <c>[HookAttachment("task")]</c> attribute
        /// filtering behavior where hooks only fire for their attached entity name.
        /// </summary>
        [Fact]
        public async Task Consume_NonTaskEntity_ShouldBeIgnored()
        {
            // Arrange — create an event for the "project" entity (not "task")
            var message = CreateNonTaskEvent("project");
            var context = CreateConsumeContext(message);

            // Act — consume the event
            await _consumer.Consume(context.Object);

            // Assert — no TaskProjection records should be created
            var count = await _dbContext.TaskProjections.CountAsync();
            count.Should().Be(0, because: "non-task entity events must be silently ignored");
        }

        /// <summary>
        /// Parameterized test verifying that various non-task entity names
        /// (timelog, project, account, user, empty string) are all correctly
        /// filtered out. None should produce a TaskProjection record.
        /// </summary>
        /// <param name="entityName">The non-task entity name to test.</param>
        [Theory]
        [InlineData("timelog")]
        [InlineData("project")]
        [InlineData("account")]
        [InlineData("user")]
        [InlineData("")]
        public async Task Consume_NonTaskEntityNames_ShouldNotCreateProjection(string entityName)
        {
            // Arrange — create an event with the specified non-task entity name
            var message = CreateNonTaskEvent(entityName);
            var context = CreateConsumeContext(message);

            // Act — consume the event
            await _consumer.Consume(context.Object);

            // Assert — no TaskProjection records should be created
            var count = await _dbContext.TaskProjections.CountAsync();
            count.Should().Be(0,
                because: $"entity name '{entityName}' is not 'task' and must be filtered out");
        }

        #endregion

        #region TaskProjection Upsert Tests

        /// <summary>
        /// Verifies that consuming a task updated event for a new (previously unseen)
        /// task creates a <see cref="TaskProjection"/> record with the correct field
        /// values: Id, Subject, and TaskTypeLabel extracted from the event's NewRecord.
        /// </summary>
        [Fact]
        public async Task Consume_NewTask_ShouldCreateProjection()
        {
            // Arrange — create a task updated event with full data
            var taskId = Guid.NewGuid();
            var message = CreateTaskUpdatedEvent(taskId, "Fix login bug", "Bug");
            var context = CreateConsumeContext(message);

            // Act — consume the event
            await _consumer.Consume(context.Object);

            // Assert — exactly 1 TaskProjection should exist
            var projections = await _dbContext.TaskProjections.ToListAsync();
            projections.Should().HaveCount(1,
                because: "a new task event should create exactly one projection");

            var projection = projections.First();
            projection.Id.Should().Be(taskId,
                because: "the projection ID must match the task record ID from the event");
            projection.Subject.Should().Be("Fix login bug",
                because: "the subject should be extracted from NewRecord['subject']");
            projection.TaskTypeLabel.Should().Be("Bug",
                because: "the task type label should be extracted from $task_type_1n_task relation");
        }

        /// <summary>
        /// Verifies that consuming a task updated event for an existing task
        /// (previously seen and projected) correctly updates the TaskProjection
        /// fields without creating a duplicate record. This tests the update
        /// branch of the idempotent upsert pattern.
        /// </summary>
        [Fact]
        public async Task Consume_ExistingTask_ShouldUpdateProjection()
        {
            // Arrange — seed an existing projection with initial values
            var taskId = Guid.NewGuid();
            _dbContext.TaskProjections.Add(new TaskProjection
            {
                Id = taskId,
                Subject = "Old Subject",
                TaskTypeLabel = "Feature",
                CreatedOn = DateTime.UtcNow.AddHours(-1),
                LastModifiedOn = DateTime.UtcNow.AddHours(-1)
            });
            await _dbContext.SaveChangesAsync();

            // Create an update event with new values
            var message = CreateTaskUpdatedEvent(taskId, "New Subject", "Bug");
            var context = CreateConsumeContext(message);

            // Act — consume the update event
            await _consumer.Consume(context.Object);

            // Assert — still exactly 1 TaskProjection (updated, not duplicated)
            var projections = await _dbContext.TaskProjections.ToListAsync();
            projections.Should().HaveCount(1,
                because: "updating an existing task should not create a duplicate");

            var projection = projections.First();
            projection.Subject.Should().Be("New Subject",
                because: "the subject should be updated from the new event");
            projection.TaskTypeLabel.Should().Be("Bug",
                because: "the task type label should be updated from the new event");
        }

        /// <summary>
        /// Verifies idempotent behavior per AAP 0.8.2: consuming the same task
        /// updated event twice should result in exactly one TaskProjection with
        /// the same final state. Duplicate event delivery must not cause data
        /// corruption or duplicate records.
        /// </summary>
        [Fact]
        public async Task Consume_DuplicateTaskEvent_ShouldResultInSameFinalState()
        {
            // Arrange — create a single task updated event
            var taskId = Guid.NewGuid();
            var message = CreateTaskUpdatedEvent(taskId, "Deploy microservices", "Epic");

            // Act — consume the same event TWICE to simulate duplicate delivery
            var context1 = CreateConsumeContext(message);
            await _consumer.Consume(context1.Object);

            var context2 = CreateConsumeContext(message);
            await _consumer.Consume(context2.Object);

            // Assert — exactly 1 projection with correct values (idempotent)
            var projections = await _dbContext.TaskProjections.ToListAsync();
            projections.Should().HaveCount(1,
                because: "duplicate event delivery must not create multiple projections");

            var projection = projections.First();
            projection.Id.Should().Be(taskId);
            projection.Subject.Should().Be("Deploy microservices",
                because: "the subject must remain consistent after duplicate events");
            projection.TaskTypeLabel.Should().Be("Epic",
                because: "the task type label must remain consistent after duplicate events");
        }

        #endregion

        #region Field Extraction Tests

        /// <summary>
        /// Verifies that the consumer correctly extracts the subject field from
        /// <c>NewRecord["subject"]</c>, mapping to the monolith's pattern in
        /// ReportService.cs line 109: <c>rec["task_subject"] = task["subject"]</c>.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractSubjectFromNewRecord()
        {
            // Arrange — create event with a specific subject
            var taskId = Guid.NewGuid();
            var message = CreateTaskUpdatedEvent(taskId, "Implement authentication");
            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert — verify subject was correctly extracted and persisted
            var projection = await _dbContext.TaskProjections.FirstOrDefaultAsync(t => t.Id == taskId);
            projection.Should().NotBeNull(because: "a task projection should have been created");
            projection.Subject.Should().Be("Implement authentication",
                because: "the subject must be extracted from NewRecord['subject']");
        }

        /// <summary>
        /// CRITICAL: Verifies extraction of the task type label from the
        /// <c>$task_type_1n_task</c> relation list in <c>NewRecord</c>.
        /// This tests the core business logic pattern from the monolith's
        /// ReportService.cs line 111:
        /// <c>rec["task_type"] = ((List&lt;EntityRecord&gt;)task["$task_type_1n_task"])[0]["label"]</c>
        /// and TaskService.SetCalculationFields() lines 66-73.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractTaskTypeLabelFromNewRecord()
        {
            // Arrange — create event with a task type label in the relation data
            var taskId = Guid.NewGuid();
            var message = CreateTaskUpdatedEvent(taskId, "Refactor database layer", "User Story");
            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert — verify task type label was correctly extracted from $task_type_1n_task[0]["label"]
            var projection = await _dbContext.TaskProjections.FirstOrDefaultAsync(t => t.Id == taskId);
            projection.Should().NotBeNull(because: "a task projection should have been created");
            projection.TaskTypeLabel.Should().Be("User Story",
                because: "the task type label must be extracted from $task_type_1n_task relation [0]['label']");
        }

        /// <summary>
        /// Verifies that when the <c>$task_type_1n_task</c> relation is missing from
        /// the event's NewRecord (e.g., because the publishing service only included
        /// changed fields), the existing TaskTypeLabel in the projection is preserved
        /// rather than being overwritten with null. This tests the consumer's defensive
        /// logic: only update TaskTypeLabel if relation data is actually included.
        /// </summary>
        [Fact]
        public async Task Consume_MissingTaskTypeRelation_ShouldPreserveExistingLabel()
        {
            // Arrange — seed a projection with an existing task type label
            var taskId = Guid.NewGuid();
            _dbContext.TaskProjections.Add(new TaskProjection
            {
                Id = taskId,
                Subject = "Existing task",
                TaskTypeLabel = "Bug",
                CreatedOn = DateTime.UtcNow.AddHours(-1),
                LastModifiedOn = DateTime.UtcNow.AddHours(-1)
            });
            await _dbContext.SaveChangesAsync();

            // Create event WITHOUT $task_type_1n_task relation in NewRecord
            // (pass null for taskTypeLabel — helper won't add the relation)
            var message = CreateTaskUpdatedEvent(taskId, "Updated task subject");
            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert — TaskTypeLabel should be preserved as "Bug" (not overwritten with null)
            var projection = await _dbContext.TaskProjections.FirstOrDefaultAsync(t => t.Id == taskId);
            projection.Should().NotBeNull();
            projection.Subject.Should().Be("Updated task subject",
                because: "the subject should be updated from the new event");
            projection.TaskTypeLabel.Should().Be("Bug",
                because: "the existing task type label must be preserved when relation data is absent");
        }

        /// <summary>
        /// Verifies that when the <c>$task_type_1n_task</c> relation is present
        /// but contains an empty list (no related task type records), the consumer
        /// handles it gracefully without throwing an exception. The TaskTypeLabel
        /// should remain unchanged (null for new projections, preserved for existing).
        /// </summary>
        [Fact]
        public async Task Consume_EmptyTaskTypeList_ShouldNotCrash()
        {
            // Arrange — create event with an empty $task_type_1n_task list
            var taskId = Guid.NewGuid();
            var newRecord = new EntityRecord();
            newRecord["id"] = taskId;
            newRecord["subject"] = "Task with empty type list";
            newRecord["$task_type_1n_task"] = new List<EntityRecord>(); // empty list

            var oldRecord = new EntityRecord();
            var message = new RecordUpdatedEvent
            {
                EntityName = "task",
                NewRecord = newRecord,
                OldRecord = oldRecord,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };
            var context = CreateConsumeContext(message);

            // Act — should NOT throw an exception
            await _consumer.Consume(context.Object);

            // Assert — projection should be created without a TaskTypeLabel
            var projection = await _dbContext.TaskProjections.FirstOrDefaultAsync(t => t.Id == taskId);
            projection.Should().NotBeNull(
                because: "the consumer should handle an empty relation list without crashing");
            projection.Subject.Should().Be("Task with empty type list");
            projection.TaskTypeLabel.Should().BeNull(
                because: "an empty $task_type_1n_task list should not produce a task type label");
        }

        #endregion
    }
}
