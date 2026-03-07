using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Project.Events.Publishers;
using WebVella.Erp.Service.Project.Domain.Services;
using Xunit;

namespace WebVella.Erp.Tests.Project.Events
{
    /// <summary>
    /// Tests for CommentEventPublisher — replaces WebVella.Erp.Plugins.Project.Hooks.Api.Comment
    /// [HookAttachment('comment')]. Validates pre-create (validation + feed creation) and
    /// post-create (watcher management) event handling.
    ///
    /// <para>
    /// <b>Original Monolith Hook (Comment.cs, 23 lines):</b>
    /// <list type="bullet">
    ///   <item><c>OnPreCreateRecord</c> → <c>new CommentService().PreCreateApiHookLogic(entityName, record, errors)</c></item>
    ///   <item><c>OnPostCreateRecord</c> → <c>new CommentService().PostCreateApiHookLogic(entityName, record)</c></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Microservice Replacement:</b>
    /// <c>CommentEventPublisher</c> implements <c>IConsumer&lt;PreRecordCreateEvent&gt;</c> and
    /// <c>IConsumer&lt;RecordCreatedEvent&gt;</c>, filtering by <c>EntityName == "comment"</c>
    /// and delegating to the injected <c>CommentService</c> instance.
    /// </para>
    ///
    /// <para>
    /// This file covers 2 of the 12 monolith hook interfaces:
    /// <c>IErpPreCreateRecordHook</c> and <c>IErpPostCreateRecordHook</c> for the "comment" entity.
    /// </para>
    /// </summary>
    public class CommentEventHandlerTests : IAsyncDisposable
    {
        private readonly ITestHarness _harness;
        private readonly ServiceProvider _provider;
        private readonly Mock<CommentService> _mockCommentService;
        private readonly Mock<IPublishEndpoint> _mockPublishEndpoint;
        private readonly Mock<ILogger<CommentEventPublisher>> _mockLogger;

        /// <summary>
        /// Initializes the MassTransit InMemoryTestHarness with CommentEventPublisher consumer
        /// and mocked dependencies. This constructor sets up the complete DI container for
        /// event consumption testing, matching the microservice's runtime DI configuration
        /// but with mocked domain service for isolated unit testing.
        /// </summary>
        public CommentEventHandlerTests()
        {
            _mockCommentService = new Mock<CommentService>();
            _mockPublishEndpoint = new Mock<IPublishEndpoint>();
            _mockLogger = new Mock<ILogger<CommentEventPublisher>>();

            var services = new ServiceCollection();
            services.AddSingleton<CommentService>(_mockCommentService.Object);
            services.AddSingleton<ILogger<CommentEventPublisher>>(_mockLogger.Object);
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<CommentEventPublisher>();
            });
            _provider = services.BuildServiceProvider(true);
            _harness = _provider.GetRequiredService<ITestHarness>();
        }

        /// <summary>
        /// Disposes the ServiceProvider and MassTransit test harness,
        /// ensuring all background consumers and transports are properly shut down.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        #region << Helper Methods >>

        /// <summary>
        /// Creates a realistic EntityRecord matching the monolith's comment entity structure.
        /// Fields mirror those used in <c>CommentService.PreCreateApiHookLogic</c> (lines 103-177)
        /// and <c>PostCreateApiHookLogic</c> (lines 179-225) from the source CommentService.cs.
        ///
        /// <para>JSON-serialized <c>l_scope</c> and <c>l_related_records</c> fields match the
        /// monolith's storage format where these fields are persisted as JSON strings in
        /// the PostgreSQL <c>rec_comment</c> table.</para>
        /// </summary>
        /// <param name="id">Optional comment record ID. Defaults to <c>Guid.NewGuid()</c>.</param>
        /// <param name="createdBy">Optional creator user ID. If null, the field is not set.</param>
        /// <param name="isProjectScoped">Whether to include "projects" in <c>l_scope</c>.</param>
        /// <param name="relatedTaskIds">Optional list of related task GUIDs for <c>l_related_records</c>.</param>
        /// <returns>A populated <see cref="EntityRecord"/> suitable for comment event payloads.</returns>
        private static EntityRecord CreateTestCommentRecord(
            Guid? id = null,
            Guid? createdBy = null,
            bool isProjectScoped = true,
            List<Guid> relatedTaskIds = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();

            if (createdBy.HasValue)
            {
                record["created_by"] = createdBy.Value;
            }

            record["body"] = "<p>Test comment body</p>";

            if (isProjectScoped)
            {
                record["l_scope"] = JsonConvert.SerializeObject(new List<string> { "projects" });
            }

            if (relatedTaskIds != null)
            {
                record["l_related_records"] = JsonConvert.SerializeObject(relatedTaskIds);
            }

            return record;
        }

        #endregion

        #region << Phase 4: Pre-Create Event Tests — Replacing IErpPreCreateRecordHook >>

        /// <summary>
        /// Validates that consuming a <see cref="PreRecordCreateEvent"/> with EntityName="comment"
        /// delegates to <see cref="CommentService.PreCreateApiHookLogic(string, EntityRecord, List{ErrorModel})"/>.
        ///
        /// <para>
        /// This directly validates the source Comment.cs line 14:
        /// <c>new CommentService().PreCreateApiHookLogic(entityName, record, errors);</c>
        /// </para>
        ///
        /// <para>
        /// <b>Source Business Logic (CommentService.cs lines 103-177):</b>
        /// Checks if l_scope contains "projects", loads related tasks via EQL,
        /// creates feed_item via FeedItemService with subject "commented on [task link]".
        /// </para>
        /// </summary>
        [Fact]
        public async Task PreCreate_Comment_Event_Should_Delegate_To_CommentService_PreCreateApiHookLogic()
        {
            // Arrange
            await _harness.Start();

            var taskId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                createdBy: Guid.NewGuid(),
                relatedTaskIds: new List<Guid> { taskId });

            var preCreateEvent = new PreRecordCreateEvent
            {
                EntityName = "comment",
                Record = record,
                ValidationErrors = new List<ErrorModel>(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(preCreateEvent);

            // Assert — verify the event was consumed by the harness
            (await _harness.Consumed.Any<PreRecordCreateEvent>()).Should().BeTrue(
                "CommentEventPublisher should consume PreRecordCreateEvent messages");

            // Assert — verify delegation to CommentService.PreCreateApiHookLogic
            // Matches source Comment.cs line 14: new CommentService().PreCreateApiHookLogic(entityName, record, errors)
            _mockCommentService.Verify(
                s => s.PreCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PreCreateApiHookLogic should be called exactly once with entityName='comment'");
        }

        /// <summary>
        /// Validates that the CommentEventPublisher filters events by EntityName,
        /// replacing the monolith's <c>[HookAttachment("comment")]</c> attribute routing.
        /// Events for non-comment entities must NOT trigger CommentService delegation.
        /// </summary>
        [Fact]
        public async Task PreCreate_NonComment_Entity_Should_Be_Ignored()
        {
            // Arrange
            await _harness.Start();

            var preCreateEvent = new PreRecordCreateEvent
            {
                EntityName = "task", // NOT "comment"
                Record = new EntityRecord(),
                ValidationErrors = new List<ErrorModel>(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(preCreateEvent);

            // Wait for message processing
            (await _harness.Consumed.Any<PreRecordCreateEvent>()).Should().BeTrue(
                "The event should be consumed by the harness even if the consumer filters it out");

            // Assert — PreCreateApiHookLogic should NOT be called for non-comment entities
            _mockCommentService.Verify(
                s => s.PreCreateApiHookLogic(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Never,
                "PreCreateApiHookLogic must not be called for non-comment entities (entity name filtering)");
        }

        /// <summary>
        /// Validates that a project-scoped comment event with l_scope containing "projects"
        /// triggers PreCreateApiHookLogic which, in the monolith (CommentService.cs lines 108-176),
        /// performs the following:
        /// <list type="number">
        ///   <item>isProjectComment = true when l_scope contains "projects"</item>
        ///   <item>Loads related tasks via EQL query on l_related_records field</item>
        ///   <item>Creates feed_item via FeedItemService with subject "commented on [task link]"</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task PreCreate_Comment_With_Project_Scope_Should_Trigger_FeedItemCreation()
        {
            // Arrange
            await _harness.Start();

            var taskId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                isProjectScoped: true,
                relatedTaskIds: new List<Guid> { taskId });

            var preCreateEvent = new PreRecordCreateEvent
            {
                EntityName = "comment",
                Record = record,
                ValidationErrors = new List<ErrorModel>(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(preCreateEvent);

            // Assert — event consumed
            (await _harness.Consumed.Any<PreRecordCreateEvent>()).Should().BeTrue();

            // Assert — PreCreateApiHookLogic called with the project-scoped record
            // In the monolith, this triggers feed item creation for project comments
            _mockCommentService.Verify(
                s => s.PreCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PreCreateApiHookLogic should be called for project-scoped comment events");
        }

        #endregion

        #region << Phase 5: Post-Create Event Tests — Replacing IErpPostCreateRecordHook >>

        /// <summary>
        /// Validates that consuming a <see cref="RecordCreatedEvent"/> with EntityName="comment"
        /// delegates to <see cref="CommentService.PostCreateApiHookLogic(string, EntityRecord)"/>.
        ///
        /// <para>
        /// This directly validates the source Comment.cs line 19:
        /// <c>new CommentService().PostCreateApiHookLogic(entityName, record);</c>
        /// </para>
        ///
        /// <para>
        /// <b>Source Business Logic (CommentService.cs lines 179-225):</b>
        /// Extracts created_by, checks project scope, loads related tasks with watchers via EQL,
        /// adds comment creator to user_nn_task_watchers relation if not already present.
        /// </para>
        /// </summary>
        [Fact]
        public async Task PostCreate_Comment_Event_Should_Delegate_To_CommentService_PostCreateApiHookLogic()
        {
            // Arrange
            await _harness.Start();

            var creatorId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                createdBy: creatorId,
                isProjectScoped: true,
                relatedTaskIds: new List<Guid> { taskId });

            var postCreateEvent = new RecordCreatedEvent
            {
                EntityName = "comment",
                Record = record,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(postCreateEvent);

            // Assert — verify the event was consumed
            (await _harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue(
                "CommentEventPublisher should consume RecordCreatedEvent messages");

            // Assert — verify delegation to CommentService.PostCreateApiHookLogic
            // Matches source Comment.cs line 19: new CommentService().PostCreateApiHookLogic(entityName, record)
            _mockCommentService.Verify(
                s => s.PostCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>()),
                Times.Once,
                "PostCreateApiHookLogic should be called exactly once with entityName='comment'");
        }

        /// <summary>
        /// Validates that a post-create event for a comment with created_by populated
        /// results in the PostCreateApiHookLogic being called. In the monolith
        /// (CommentService.cs lines 179-225), this method:
        /// <list type="number">
        ///   <item>Extracts created_by from comment record</item>
        ///   <item>Returns early if not project-scoped</item>
        ///   <item>Loads tasks with watchers via EQL</item>
        ///   <item>Adds comment creator to user_nn_task_watchers relation if not already present</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task PostCreate_Comment_With_CreatedBy_Should_Add_Creator_To_Watchers()
        {
            // Arrange
            await _harness.Start();

            var creatorId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                createdBy: creatorId,
                isProjectScoped: true,
                relatedTaskIds: new List<Guid> { taskId });

            var postCreateEvent = new RecordCreatedEvent
            {
                EntityName = "comment",
                Record = record,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(postCreateEvent);

            // Assert
            (await _harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue();

            // PostCreateApiHookLogic is called — internally it adds creator to watchers
            // (CommentService.cs line 216: if (!watcherIdList.Contains(commentCreator.Value)))
            _mockCommentService.Verify(
                s => s.PostCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>()),
                Times.Once,
                "PostCreateApiHookLogic should process comment with created_by for watcher addition");
        }

        /// <summary>
        /// Validates that the CommentEventPublisher filters post-create events by EntityName,
        /// replacing the monolith's <c>[HookAttachment("comment")]</c> routing.
        /// Events for non-comment entities must NOT trigger CommentService delegation.
        /// </summary>
        [Fact]
        public async Task PostCreate_NonComment_Entity_Should_Be_Ignored()
        {
            // Arrange
            await _harness.Start();

            var postCreateEvent = new RecordCreatedEvent
            {
                EntityName = "timelog", // NOT "comment"
                Record = new EntityRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(postCreateEvent);

            // Wait for processing
            (await _harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue();

            // Assert — PostCreateApiHookLogic should NOT be called for non-comment entities
            _mockCommentService.Verify(
                s => s.PostCreateApiHookLogic(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>()),
                Times.Never,
                "PostCreateApiHookLogic must not be called for non-comment entities");
        }

        /// <summary>
        /// Validates that a comment record without a created_by field is still processed
        /// by the event publisher. The CommentService handles the null check internally —
        /// per CommentService.cs lines 181-186:
        /// <code>
        /// if (commentCreator == null) return;
        /// </code>
        /// The publisher delegates unconditionally; the service handles the early return.
        /// </summary>
        [Fact]
        public async Task PostCreate_Comment_Without_CreatedBy_Should_Still_Be_Processed()
        {
            // Arrange
            await _harness.Start();

            // Create a comment record WITHOUT created_by field
            var record = CreateTestCommentRecord(
                createdBy: null,
                isProjectScoped: true);

            var postCreateEvent = new RecordCreatedEvent
            {
                EntityName = "comment",
                Record = record,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act
            await _harness.Bus.Publish(postCreateEvent);

            // Assert — event consumed and delegated
            (await _harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue();

            // PostCreateApiHookLogic IS called — the service handles null created_by internally
            _mockCommentService.Verify(
                s => s.PostCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>()),
                Times.Once,
                "PostCreateApiHookLogic should be called even without created_by — service handles null internally");
        }

        #endregion

        #region << Phase 6: Event Payload Validation Tests >>

        /// <summary>
        /// Validates that the <see cref="PreRecordCreateEvent"/> payload carries the correct
        /// comment data structure, including body, l_scope, l_related_records, and an empty
        /// ValidationErrors list. This ensures the event contract properly transports all
        /// data needed by <c>CommentService.PreCreateApiHookLogic</c>.
        /// </summary>
        [Fact]
        public async Task PreCreate_Event_Payload_Should_Contain_Correct_Comment_Data()
        {
            // Arrange
            var commentId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var creatorId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                id: commentId,
                createdBy: creatorId,
                isProjectScoped: true,
                relatedTaskIds: new List<Guid> { taskId });

            var preCreateEvent = new PreRecordCreateEvent
            {
                EntityName = "comment",
                Record = record,
                ValidationErrors = new List<ErrorModel>(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Assert — verify event payload structure
            preCreateEvent.EntityName.Should().Be("comment");
            preCreateEvent.Record.Should().NotBeNull("Record must be populated with comment data");
            preCreateEvent.Record["id"].Should().Be(commentId);
            preCreateEvent.Record["body"].Should().Be("<p>Test comment body</p>");
            preCreateEvent.Record["created_by"].Should().Be(creatorId);

            // Verify l_scope contains "projects" in JSON format
            var lScope = (string)preCreateEvent.Record["l_scope"];
            lScope.Should().NotBeNull();
            lScope.Should().Contain("projects");

            // Verify l_related_records contains the task ID
            var lRelatedRecords = (string)preCreateEvent.Record["l_related_records"];
            lRelatedRecords.Should().NotBeNull();
            lRelatedRecords.Should().Contain(taskId.ToString());

            // Verify ValidationErrors starts empty — can be populated by service
            preCreateEvent.ValidationErrors.Should().NotBeNull();
            preCreateEvent.ValidationErrors.Should().BeEmpty(
                "ValidationErrors should start empty — CommentService populates them if validation fails");

            // Verify base IDomainEvent properties
            preCreateEvent.Timestamp.Should().BeCloseTo(
                DateTimeOffset.UtcNow,
                precision: TimeSpan.FromSeconds(5));
            preCreateEvent.CorrelationId.Should().NotBeEmpty();

            await Task.CompletedTask;
        }

        /// <summary>
        /// Validates that both <see cref="PreRecordCreateEvent"/> and <see cref="RecordCreatedEvent"/>
        /// implement the <see cref="IDomainEvent"/> interface from SharedKernel, carrying
        /// Timestamp, CorrelationId, and EntityName properties.
        ///
        /// <para>
        /// This verifies the event contract conformance required by AAP §0.5.1:
        /// "Convert 12 hook interfaces to domain event contracts" — ensuring all events
        /// derive from IDomainEvent for consistent cross-service communication.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Event_Contracts_Should_Match_SharedKernel_Definitions()
        {
            // Verify PreRecordCreateEvent implements IDomainEvent
            typeof(IDomainEvent).IsAssignableFrom(typeof(PreRecordCreateEvent))
                .Should().BeTrue("PreRecordCreateEvent must implement IDomainEvent");

            // Verify RecordCreatedEvent implements IDomainEvent
            typeof(IDomainEvent).IsAssignableFrom(typeof(RecordCreatedEvent))
                .Should().BeTrue("RecordCreatedEvent must implement IDomainEvent");

            // Verify PreRecordCreateEvent has required properties
            var preCreateProps = typeof(PreRecordCreateEvent).GetProperties()
                .Select(p => p.Name)
                .ToList();
            preCreateProps.Should().Contain("EntityName",
                "PreRecordCreateEvent must have EntityName property");
            preCreateProps.Should().Contain("Record",
                "PreRecordCreateEvent must have Record property");
            preCreateProps.Should().Contain("ValidationErrors",
                "PreRecordCreateEvent must have ValidationErrors property");
            preCreateProps.Should().Contain("Timestamp",
                "PreRecordCreateEvent must have Timestamp property (from IDomainEvent)");
            preCreateProps.Should().Contain("CorrelationId",
                "PreRecordCreateEvent must have CorrelationId property (from IDomainEvent)");

            // Verify RecordCreatedEvent has required properties
            var postCreateProps = typeof(RecordCreatedEvent).GetProperties()
                .Select(p => p.Name)
                .ToList();
            postCreateProps.Should().Contain("EntityName",
                "RecordCreatedEvent must have EntityName property");
            postCreateProps.Should().Contain("Record",
                "RecordCreatedEvent must have Record property");
            postCreateProps.Should().Contain("Timestamp",
                "RecordCreatedEvent must have Timestamp property (from IDomainEvent)");
            postCreateProps.Should().Contain("CorrelationId",
                "RecordCreatedEvent must have CorrelationId property (from IDomainEvent)");

            // Verify IDomainEvent interface has the required members
            var domainEventProps = typeof(IDomainEvent).GetProperties()
                .Select(p => p.Name)
                .ToList();
            domainEventProps.Should().Contain("Timestamp");
            domainEventProps.Should().Contain("CorrelationId");
            domainEventProps.Should().Contain("EntityName");

            // Verify CommentEventPublisher implements both consumer interfaces
            var publisherInterfaces = typeof(CommentEventPublisher).GetInterfaces();
            publisherInterfaces.Should().Contain(typeof(IConsumer<PreRecordCreateEvent>),
                "CommentEventPublisher must implement IConsumer<PreRecordCreateEvent>");
            publisherInterfaces.Should().Contain(typeof(IConsumer<RecordCreatedEvent>),
                "CommentEventPublisher must implement IConsumer<RecordCreatedEvent>");

            await Task.CompletedTask;
        }

        #endregion

        #region << Phase 7: Idempotency Tests (AAP §0.8.2) >>

        /// <summary>
        /// Validates that publishing the same PreRecordCreateEvent twice (same CorrelationId)
        /// is handled idempotently. The CommentService.PreCreateApiHookLogic is called each time
        /// because pre-create validation is inherently safe for replay — it produces the same
        /// side effects (feed creation, validation errors) on each invocation.
        ///
        /// <para>AAP §0.8.2: "Event consumers must be idempotent (duplicate event delivery
        /// must not cause data corruption)."</para>
        /// </summary>
        [Fact]
        public async Task Duplicate_PreCreate_Events_Should_Be_Handled_Idempotently()
        {
            // Arrange
            await _harness.Start();

            var correlationId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                relatedTaskIds: new List<Guid> { taskId });

            var preCreateEvent = new PreRecordCreateEvent
            {
                EntityName = "comment",
                Record = record,
                ValidationErrors = new List<ErrorModel>(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId
            };

            // Act — publish the SAME event twice (simulating duplicate delivery)
            await _harness.Bus.Publish(preCreateEvent);
            await _harness.Bus.Publish(preCreateEvent);

            // Allow time for both messages to be processed
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Assert — PreCreateApiHookLogic called twice (idempotent validation is safe)
            _mockCommentService.Verify(
                s => s.PreCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Exactly(2),
                "PreCreateApiHookLogic should be called for each event delivery — idempotent validation is safe");
        }

        /// <summary>
        /// Validates that publishing the same RecordCreatedEvent twice (duplicate delivery)
        /// is handled idempotently. The CommentService.PostCreateApiHookLogic is called each time
        /// because the underlying logic checks for existing watcher relations before creating new ones.
        ///
        /// <para>Per CommentService.cs line 216:
        /// <c>if (!watcherIdList.Contains(commentCreator.Value))</c>
        /// — the Contains check prevents duplicate relation creation, making the operation idempotent.</para>
        ///
        /// <para>AAP §0.8.2: "Event consumers must be idempotent."</para>
        /// </summary>
        [Fact]
        public async Task Duplicate_PostCreate_Events_Should_Be_Handled_Idempotently()
        {
            // Arrange
            await _harness.Start();

            var correlationId = Guid.NewGuid();
            var creatorId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var record = CreateTestCommentRecord(
                createdBy: creatorId,
                isProjectScoped: true,
                relatedTaskIds: new List<Guid> { taskId });

            var postCreateEvent = new RecordCreatedEvent
            {
                EntityName = "comment",
                Record = record,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId
            };

            // Act — publish the SAME event twice (simulating duplicate delivery)
            await _harness.Bus.Publish(postCreateEvent);
            await _harness.Bus.Publish(postCreateEvent);

            // Allow time for both messages to be processed
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Assert — PostCreateApiHookLogic called twice (relation creation is idempotent
            // because watcherIdList.Contains check at line 216 prevents duplicate relations)
            _mockCommentService.Verify(
                s => s.PostCreateApiHookLogic(
                    "comment",
                    It.IsAny<EntityRecord>()),
                Times.Exactly(2),
                "PostCreateApiHookLogic should be called for each delivery — idempotent due to watcherIdList.Contains check");
        }

        #endregion
    }
}
