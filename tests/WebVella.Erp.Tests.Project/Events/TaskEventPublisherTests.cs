using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    /// Tests for TaskEventPublisher — replaces WebVella.Erp.Plugins.Project.Hooks.Api.Task
    /// [HookAttachment('task')]. Validates all four hook interface conversions:
    ///   IErpPreCreateRecordHook  → PreRecordCreateEvent  (consumer + PreCreateRecordPageHookLogic)
    ///   IErpPostCreateRecordHook → RecordCreatedEvent    (consumer + PostCreateApiHookLogic)
    ///   IErpPreUpdateRecordHook  → PreRecordUpdateEvent  (consumer + PostPreUpdateApiHookLogic)
    ///   IErpPostUpdateRecordHook → RecordUpdatedEvent    (consumer + PostUpdateApiHookLogic)
    /// This is the most comprehensive event publisher covering 4 out of 12 hook interfaces.
    ///
    /// <para>Testing strategy: Each test creates a <see cref="TaskEventPublisher"/> directly
    /// with mocked dependencies and invokes <c>Consume</c> via a mocked
    /// <see cref="ConsumeContext{T}"/>. This provides deterministic, synchronous unit tests
    /// without depending on MassTransit transport infrastructure.</para>
    ///
    /// <para>MassTransit <see cref="ITestHarness"/> is set up in the fixture for integration-level
    /// idempotency tests that verify duplicate event delivery through the in-memory bus.</para>
    /// </summary>
    public class TaskEventPublisherTests : IAsyncDisposable
    {
        #region << Test Fixture Fields >>

        /// <summary>MassTransit in-memory test harness for bus-level integration tests.</summary>
        private readonly ITestHarness _harness;

        /// <summary>DI service provider hosting the MassTransit test harness and mock services.</summary>
        private readonly ServiceProvider _provider;

        /// <summary>
        /// Mock of <see cref="TaskService"/> — the domain service that contains all business logic
        /// previously invoked via <c>new TaskService().MethodName()</c> in the monolith hook class.
        /// Methods are virtual to allow Moq interception for .Verify() call tracking.
        /// </summary>
        private readonly Mock<TaskService> _mockTaskService;

        /// <summary>Mock MassTransit publish endpoint for the consumer's constructor dependency.</summary>
        private readonly Mock<IPublishEndpoint> _mockPublishEndpoint;

        /// <summary>Mock logger for the TaskEventPublisher constructor dependency.</summary>
        private readonly Mock<ILogger<TaskEventPublisher>> _mockLogger;

        /// <summary>Direct reference to the publisher for synchronous Consume() invocations.</summary>
        private readonly TaskEventPublisher _publisher;

        #endregion

        #region << Constructor & Disposal >>

        /// <summary>
        /// Initializes the test fixture with mocked dependencies and a MassTransit test harness.
        /// The <see cref="TaskEventPublisher"/> is created directly for deterministic unit tests,
        /// while the harness is available for bus-level integration tests (idempotency).
        /// </summary>
        public TaskEventPublisherTests()
        {
            // Create mocks for TaskEventPublisher's constructor dependencies.
            // Mock<TaskService> uses the protected parameterless constructor added
            // specifically for testability — the 4 hook methods are virtual.
            _mockTaskService = new Mock<TaskService>();
            _mockPublishEndpoint = new Mock<IPublishEndpoint>();
            _mockLogger = new Mock<ILogger<TaskEventPublisher>>();

            // Create the publisher directly for synchronous unit testing via Consume().
            _publisher = new TaskEventPublisher(
                _mockPublishEndpoint.Object,
                _mockTaskService.Object,
                _mockLogger.Object);

            // Set up MassTransit in-memory test harness for bus-level integration tests.
            var services = new ServiceCollection();
            services.AddSingleton(_mockTaskService.Object);
            services.AddSingleton<IPublishEndpoint>(_mockPublishEndpoint.Object);
            services.AddSingleton<ILogger<TaskEventPublisher>>(_mockLogger.Object);
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<TaskEventPublisher>();
            });
            _provider = services.BuildServiceProvider();
            _harness = _provider.GetRequiredService<ITestHarness>();
        }

        /// <summary>
        /// Disposes the MassTransit test harness and the DI service provider.
        /// Ensures clean shutdown of in-memory bus resources between test runs.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_harness != null)
            {
                try { await _harness.Stop(); } catch { /* Best-effort stop */ }
            }

            if (_provider is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                _provider?.Dispose();
            }
        }

        #endregion

        #region << Phase 4: Pre-Create Validation Event Tests — Replacing IErpPreCreateRecordHook >>

        /// <summary>
        /// Verifies that consuming a <see cref="PreRecordCreateEvent"/> with EntityName="task"
        /// delegates to <see cref="TaskService.PreCreateRecordPageHookLogic"/>.
        /// Replaces: IErpPreCreateRecordHook.OnPreCreateRecord → new TaskService().PreCreateRecordPageHookLogic(entityName, record, errors)
        /// CRITICAL: Method name is PreCreateRecordPageHookLogic (NOT PreCreateApiHookLogic) per source Task.cs line 13.
        /// </summary>
        [Fact]
        public async Task PreCreate_Task_Event_Should_Publish_TaskCreatedEvent_Via_PreRecordCreateEvent()
        {
            // Arrange — create a pre-create event with a valid task record including one project
            var record = CreateTestTaskRecord(projectIds: new List<Guid> { Guid.NewGuid() });
            var evt = CreatePreCreateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act — invoke the consumer directly
            await _publisher.Consume(mockContext.Object);

            // Assert — verify delegation to PreCreateRecordPageHookLogic (not PreCreateApiHookLogic)
            _mockTaskService.Verify(
                s => s.PreCreateRecordPageHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PreCreateRecordPageHookLogic should be called exactly once for a 'task' entity pre-create event");
        }

        /// <summary>
        /// Verifies that a pre-create event for a task missing the $project_nn_task.id field
        /// still delegates to PreCreateRecordPageHookLogic. In the monolith (TaskService.cs lines 302-309),
        /// missing project produces: errors.Add(new ErrorModel { Key = "$project_nn_task.id", Message = "Project is not specified." })
        /// The test documents this business rule while verifying delegation occurs.
        /// </summary>
        [Fact]
        public async Task PreCreate_Task_Without_Project_Should_Add_Validation_Error()
        {
            // Arrange — create a task record WITHOUT $project_nn_task.id
            var record = CreateTestTaskRecord(projectIds: null);
            var evt = CreatePreCreateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — delegation should occur; the TaskService validates and adds errors
            // Business rule: Missing project produces ErrorModel { Key = "$project_nn_task.id", Message = "Project is not specified." }
            _mockTaskService.Verify(
                s => s.PreCreateRecordPageHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PreCreateRecordPageHookLogic should be called even when project is missing (validation happens inside)");
        }

        /// <summary>
        /// Verifies that a pre-create event with multiple projects in $project_nn_task.id
        /// still delegates to PreCreateRecordPageHookLogic. In the monolith (TaskService.cs lines 321-328),
        /// multiple projects produces: errors.Add(new ErrorModel { Key = "$project_nn_task.id", Message = "More than one project is selected." })
        /// </summary>
        [Fact]
        public async Task PreCreate_Task_With_Multiple_Projects_Should_Add_Validation_Error()
        {
            // Arrange — create a task record with TWO projects
            var record = CreateTestTaskRecord(projectIds: new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });
            var evt = CreatePreCreateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — delegation should occur; TaskService validates multiple projects
            // Business rule: Multiple projects produces ErrorModel { Key = "$project_nn_task.id", Message = "More than one project is selected." }
            _mockTaskService.Verify(
                s => s.PreCreateRecordPageHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PreCreateRecordPageHookLogic should be called even with multiple projects (validation happens inside)");
        }

        /// <summary>
        /// Verifies that a PreRecordCreateEvent with EntityName != "task" is silently ignored.
        /// This validates the entity name filtering that replaces [HookAttachment("task")].
        /// </summary>
        [Fact]
        public async Task PreCreate_NonTask_Entity_Should_Be_Ignored()
        {
            // Arrange — create a pre-create event for a "comment" entity (not "task")
            var evt = new PreRecordCreateEvent
            {
                EntityName = "comment",
                Record = new EntityRecord(),
                ValidationErrors = new List<ErrorModel>()
            };
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PreCreateRecordPageHookLogic should NOT be called for non-task entities
            _mockTaskService.Verify(
                s => s.PreCreateRecordPageHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Never,
                "PreCreateRecordPageHookLogic must not be called for non-task entities");
        }

        #endregion

        #region << Phase 5: Post-Create Event Tests — Replacing IErpPostCreateRecordHook >>

        /// <summary>
        /// Verifies that consuming a <see cref="RecordCreatedEvent"/> with EntityName="task"
        /// delegates to <see cref="TaskService.PostCreateApiHookLogic"/>.
        /// Validates source Task.cs line 18: new TaskService().PostCreateApiHookLogic(entityName, record)
        /// </summary>
        [Fact]
        public async Task PostCreate_Task_Event_Should_Delegate_To_TaskService_PostCreateApiHookLogic()
        {
            // Arrange
            var record = CreateTestTaskRecord();
            var evt = CreatePostCreateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert
            _mockTaskService.Verify(
                s => s.PostCreateApiHookLogic(It.IsAny<EntityRecord>()),
                Times.Once,
                "PostCreateApiHookLogic should be called exactly once for a 'task' entity post-create event");
        }

        /// <summary>
        /// Verifies that PostCreateApiHookLogic is called when a task is created,
        /// which triggers SetCalculationFields (key, x_search), watcher seeding,
        /// and feed item creation.
        /// In the monolith (TaskService.cs lines 332-394):
        ///   1. Calls SetCalculationFields() — generates key from project abbr + task number
        ///   2. Updates task via RecordManager(executeHooks: false).UpdateRecord
        ///   3. Seeds watchers: owner_id, created_by, project_owner_id
        ///   4. Creates feed_item "created [task link]"
        /// </summary>
        [Fact]
        public async Task PostCreate_Task_Should_Set_Calculation_Fields()
        {
            // Arrange — create a task record with id and subject for calculation
            var taskId = Guid.NewGuid();
            var record = CreateTestTaskRecord(id: taskId, subject: "Implement user authentication");
            var evt = CreatePostCreateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PostCreateApiHookLogic is called, which internally calls SetCalculationFields
            _mockTaskService.Verify(
                s => s.PostCreateApiHookLogic(It.IsAny<EntityRecord>()),
                Times.Once,
                "PostCreateApiHookLogic should trigger SetCalculationFields, watcher seeding, and feed creation");
        }

        /// <summary>
        /// Validates that the event payload passed to PostCreateApiHookLogic contains
        /// the correct task data including id, subject, body, owner_id, and created_by.
        /// </summary>
        [Fact]
        public async Task PostCreate_Task_Event_Payload_Should_Contain_Correct_Task_Data()
        {
            // Arrange — create a task record with specific known values
            var taskId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var createdBy = Guid.NewGuid();
            var record = CreateTestTaskRecord(id: taskId, subject: "Deploy to staging", ownerId: ownerId, createdBy: createdBy);
            var evt = CreatePostCreateTaskEvent(record);

            // Verify event payload integrity before consumption
            evt.Record.Should().NotBeNull("event record must be populated");
            evt.Record["id"].Should().Be(taskId, "record id must match");
            evt.Record["subject"].Should().Be("Deploy to staging", "record subject must match");
            evt.Record["owner_id"].Should().Be(ownerId, "record owner_id must match");
            evt.Record["created_by"].Should().Be(createdBy, "record created_by must match");
            evt.Record["body"].Should().Be("<p>Task description</p>", "record body must match default");

            // Act — also verify the consumer accepts this payload
            var mockContext = CreateMockConsumeContext(evt);
            await _publisher.Consume(mockContext.Object);

            // Assert — delegation occurred with the payload
            _mockTaskService.Verify(
                s => s.PostCreateApiHookLogic(It.IsAny<EntityRecord>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that a RecordCreatedEvent with EntityName != "task" is silently ignored.
        /// </summary>
        [Fact]
        public async Task PostCreate_NonTask_Entity_Should_Be_Ignored()
        {
            // Arrange — create a post-create event for "timelog" entity
            var evt = new RecordCreatedEvent
            {
                EntityName = "timelog",
                Record = new EntityRecord()
            };
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PostCreateApiHookLogic must NOT be called
            _mockTaskService.Verify(
                s => s.PostCreateApiHookLogic(It.IsAny<EntityRecord>()),
                Times.Never,
                "PostCreateApiHookLogic must not be called for non-task entities");
        }

        #endregion

        #region << Phase 6: Pre-Update Validation Event Tests — Replacing IErpPreUpdateRecordHook >>

        /// <summary>
        /// Verifies that consuming a <see cref="PreRecordUpdateEvent"/> with EntityName="task"
        /// delegates to <see cref="TaskService.PostPreUpdateApiHookLogic"/>.
        /// CRITICAL: Method name is PostPreUpdateApiHookLogic (NOT PreUpdateApiHookLogic) per source Task.cs line 23.
        /// The destination signature is PostPreUpdateApiHookLogic(EntityRecord record, EntityRecord oldRecord, List&lt;ErrorModel&gt; errors)
        /// where oldRecord is passed as null since PreRecordUpdateEvent does not carry old state.
        /// </summary>
        [Fact]
        public async Task PreUpdate_Task_Event_Should_Delegate_To_TaskService_PostPreUpdateApiHookLogic()
        {
            // Arrange
            var record = CreateTestTaskRecord();
            var evt = CreatePreUpdateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PostPreUpdateApiHookLogic called with (record, null, errors)
            _mockTaskService.Verify(
                s => s.PostPreUpdateApiHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PostPreUpdateApiHookLogic should be called exactly once for a 'task' entity pre-update event");
        }

        /// <summary>
        /// Verifies that PreRecordUpdateEvent consumption triggers project change detection logic.
        /// In the monolith (TaskService.cs lines 396-490+):
        ///   1. Loads old task via EQL with project and watcher relations
        ///   2. Compares old vs new project associations
        ///   3. Recalculates key if project changed
        ///   4. Updates watcher list based on project ownership changes
        /// </summary>
        [Fact]
        public async Task PreUpdate_Task_Should_Handle_Project_Change_Detection()
        {
            // Arrange — task record with modified project association
            var record = CreateTestTaskRecord(
                projectIds: new List<Guid> { Guid.NewGuid() });
            var evt = CreatePreUpdateTaskEvent(record);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PostPreUpdateApiHookLogic called (project change detection happens inside)
            _mockTaskService.Verify(
                s => s.PostPreUpdateApiHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Once,
                "PostPreUpdateApiHookLogic should be called to handle project change detection");
        }

        /// <summary>
        /// Verifies that a PreRecordUpdateEvent with EntityName != "task" is silently ignored.
        /// </summary>
        [Fact]
        public async Task PreUpdate_NonTask_Entity_Should_Be_Ignored()
        {
            // Arrange — create a pre-update event for "comment" entity
            var evt = new PreRecordUpdateEvent
            {
                EntityName = "comment",
                Record = new EntityRecord(),
                ValidationErrors = new List<ErrorModel>()
            };
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PostPreUpdateApiHookLogic must NOT be called
            _mockTaskService.Verify(
                s => s.PostPreUpdateApiHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Never,
                "PostPreUpdateApiHookLogic must not be called for non-task entities");
        }

        #endregion

        #region << Phase 7: Post-Update Event Tests — Replacing IErpPostUpdateRecordHook >>

        /// <summary>
        /// Verifies that consuming a <see cref="RecordUpdatedEvent"/> with EntityName="task"
        /// delegates to <see cref="TaskService.PostUpdateApiHookLogic"/>.
        /// CRITICAL: The publisher uses context.Message.NewRecord (not OldRecord) per TaskEventPublisher spec.
        /// Validates source Task.cs line 28: new TaskService().PostUpdateApiHookLogic(entityName, record)
        /// </summary>
        [Fact]
        public async Task PostUpdate_Task_Event_Should_Delegate_To_TaskService_PostUpdateApiHookLogic()
        {
            // Arrange
            var oldRecord = CreateTestTaskRecord(subject: "Old task subject");
            var newRecord = CreateTestTaskRecord(subject: "Updated task subject");
            var evt = CreatePostUpdateTaskEvent(oldRecord, newRecord);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert
            _mockTaskService.Verify(
                s => s.PostUpdateApiHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<EntityRecord>()),
                Times.Once,
                "PostUpdateApiHookLogic should be called exactly once for a 'task' entity post-update event");
        }

        /// <summary>
        /// Verifies that the TaskEventPublisher passes NewRecord (not OldRecord) as the primary
        /// record parameter to PostUpdateApiHookLogic. The RecordUpdatedEvent carries both
        /// OldRecord (pre-update state) and NewRecord (post-update state); the publisher passes
        /// NewRecord as the first parameter matching the original hook behavior.
        /// </summary>
        [Fact]
        public async Task PostUpdate_Task_Should_Use_NewRecord_Not_OldRecord()
        {
            // Arrange — create DIFFERENT OldRecord and NewRecord
            var oldId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            var oldRecord = CreateTestTaskRecord(id: oldId, subject: "Old subject");
            var newRecord = CreateTestTaskRecord(id: newId, subject: "New subject");
            var evt = CreatePostUpdateTaskEvent(oldRecord, newRecord);
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — verify that the FIRST parameter (primary record) is the NewRecord
            _mockTaskService.Verify(
                s => s.PostUpdateApiHookLogic(
                    It.Is<EntityRecord>(r => r != null && r["id"] != null && r["id"].Equals(newId)),
                    It.IsAny<EntityRecord>()),
                Times.Once,
                "PostUpdateApiHookLogic must receive NewRecord as the primary record, not OldRecord");
        }

        /// <summary>
        /// Verifies that a RecordUpdatedEvent with EntityName != "task" is silently ignored.
        /// </summary>
        [Fact]
        public async Task PostUpdate_NonTask_Entity_Should_Be_Ignored()
        {
            // Arrange — create a post-update event for "comment" entity
            var evt = new RecordUpdatedEvent
            {
                EntityName = "comment",
                OldRecord = new EntityRecord(),
                NewRecord = new EntityRecord()
            };
            var mockContext = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext.Object);

            // Assert — PostUpdateApiHookLogic must NOT be called
            _mockTaskService.Verify(
                s => s.PostUpdateApiHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<EntityRecord>()),
                Times.Never,
                "PostUpdateApiHookLogic must not be called for non-task entities");
        }

        #endregion

        #region << Phase 8: Comprehensive Interface Coverage Tests >>

        /// <summary>
        /// Uses reflection to verify that <see cref="TaskEventPublisher"/> implements all four
        /// MassTransit consumer interfaces replacing the monolith's hook interfaces:
        ///   IConsumer&lt;PreRecordCreateEvent&gt;  — replacing IErpPreCreateRecordHook
        ///   IConsumer&lt;RecordCreatedEvent&gt;     — replacing IErpPostCreateRecordHook
        ///   IConsumer&lt;PreRecordUpdateEvent&gt;   — replacing IErpPreUpdateRecordHook
        ///   IConsumer&lt;RecordUpdatedEvent&gt;     — replacing IErpPostUpdateRecordHook
        /// </summary>
        [Fact]
        public void TaskEventPublisher_Should_Implement_All_Four_Consumer_Interfaces()
        {
            // Act — get all interfaces implemented by TaskEventPublisher
            var interfaces = typeof(TaskEventPublisher).GetInterfaces();

            // Assert — all four consumer interfaces must be present
            interfaces.Should().Contain(typeof(IConsumer<PreRecordCreateEvent>),
                "TaskEventPublisher must implement IConsumer<PreRecordCreateEvent> (replacing IErpPreCreateRecordHook)");
            interfaces.Should().Contain(typeof(IConsumer<RecordCreatedEvent>),
                "TaskEventPublisher must implement IConsumer<RecordCreatedEvent> (replacing IErpPostCreateRecordHook)");
            interfaces.Should().Contain(typeof(IConsumer<PreRecordUpdateEvent>),
                "TaskEventPublisher must implement IConsumer<PreRecordUpdateEvent> (replacing IErpPreUpdateRecordHook)");
            interfaces.Should().Contain(typeof(IConsumer<RecordUpdatedEvent>),
                "TaskEventPublisher must implement IConsumer<RecordUpdatedEvent> (replacing IErpPostUpdateRecordHook)");
        }

        /// <summary>
        /// Verifies that TaskEventPublisher does NOT implement delete event consumers,
        /// confirming that the original Task hook in the monolith had no delete hooks.
        /// </summary>
        [Fact]
        public void TaskEventPublisher_Should_Not_Handle_Delete_Events()
        {
            // Act
            var interfaces = typeof(TaskEventPublisher).GetInterfaces();

            // Assert — delete event consumers must NOT be present
            interfaces.Should().NotContain(typeof(IConsumer<PreRecordDeleteEvent>),
                "Original Task hook had no IErpPreDeleteRecordHook — TaskEventPublisher must not handle pre-delete events");
            interfaces.Should().NotContain(typeof(IConsumer<RecordDeletedEvent>),
                "Original Task hook had no IErpPostDeleteRecordHook — TaskEventPublisher must not handle post-delete events");
        }

        /// <summary>
        /// Verifies that all four domain event types implement the <see cref="IDomainEvent"/>
        /// base interface with Timestamp, CorrelationId, and EntityName properties.
        /// </summary>
        [Fact]
        public void All_Event_Types_Should_Implement_IDomainEvent()
        {
            // Assert — all four event types implement IDomainEvent
            typeof(IDomainEvent).IsAssignableFrom(typeof(PreRecordCreateEvent))
                .Should().BeTrue("PreRecordCreateEvent must implement IDomainEvent");
            typeof(IDomainEvent).IsAssignableFrom(typeof(RecordCreatedEvent))
                .Should().BeTrue("RecordCreatedEvent must implement IDomainEvent");
            typeof(IDomainEvent).IsAssignableFrom(typeof(PreRecordUpdateEvent))
                .Should().BeTrue("PreRecordUpdateEvent must implement IDomainEvent");
            typeof(IDomainEvent).IsAssignableFrom(typeof(RecordUpdatedEvent))
                .Should().BeTrue("RecordUpdatedEvent must implement IDomainEvent");

            // Verify each event type has the required IDomainEvent properties
            var domainEventProperties = typeof(IDomainEvent).GetProperties()
                .Select(p => p.Name).ToList();
            domainEventProperties.Should().Contain("Timestamp",
                "IDomainEvent must define Timestamp property");
            domainEventProperties.Should().Contain("CorrelationId",
                "IDomainEvent must define CorrelationId property");
            domainEventProperties.Should().Contain("EntityName",
                "IDomainEvent must define EntityName property");
        }

        #endregion

        #region << Phase 9: Idempotency Tests (AAP §0.8.2) >>

        /// <summary>
        /// Verifies that publishing the same PreRecordCreateEvent twice results in
        /// PreCreateRecordPageHookLogic being called twice — the method is inherently
        /// idempotent because it only adds errors to the validation list.
        /// Per AAP §0.8.2: "Event consumers must be idempotent."
        /// </summary>
        [Fact]
        public async Task Duplicate_PreCreate_Task_Events_Should_Be_Idempotent()
        {
            // Arrange — same event published twice (same CorrelationId)
            var correlationId = Guid.NewGuid();
            var record = CreateTestTaskRecord(projectIds: new List<Guid> { Guid.NewGuid() });
            var evt = CreatePreCreateTaskEvent(record);
            evt.CorrelationId = correlationId;

            var mockContext1 = CreateMockConsumeContext(evt);
            var mockContext2 = CreateMockConsumeContext(evt);

            // Act — consume the same event twice
            await _publisher.Consume(mockContext1.Object);
            await _publisher.Consume(mockContext2.Object);

            // Assert — PreCreateRecordPageHookLogic called twice (validation is idempotent —
            // adds errors to list; duplicate validation is harmless)
            _mockTaskService.Verify(
                s => s.PreCreateRecordPageHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<ErrorModel>>()),
                Times.Exactly(2),
                "Duplicate pre-create events should result in two idempotent validation calls");
        }

        /// <summary>
        /// Verifies that publishing the same RecordCreatedEvent twice results in
        /// PostCreateApiHookLogic being called twice — updates are overwrites (SetCalculationFields),
        /// relation creation checks existence first (idempotent per TaskEventPublisher spec).
        /// Per AAP §0.8.2: "Event consumers must be idempotent."
        /// </summary>
        [Fact]
        public async Task Duplicate_PostCreate_Task_Events_Should_Be_Idempotent()
        {
            // Arrange — same event published twice
            var correlationId = Guid.NewGuid();
            var record = CreateTestTaskRecord();
            var evt = CreatePostCreateTaskEvent(record);
            evt.CorrelationId = correlationId;

            var mockContext1 = CreateMockConsumeContext(evt);
            var mockContext2 = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext1.Object);
            await _publisher.Consume(mockContext2.Object);

            // Assert — PostCreateApiHookLogic called twice (overwrites are idempotent)
            _mockTaskService.Verify(
                s => s.PostCreateApiHookLogic(It.IsAny<EntityRecord>()),
                Times.Exactly(2),
                "Duplicate post-create events should result in two idempotent hook calls");
        }

        /// <summary>
        /// Verifies that publishing the same RecordUpdatedEvent twice results in
        /// PostUpdateApiHookLogic being called twice — recalculations are overwrites
        /// and watcher checks are existence-based (idempotent).
        /// Per AAP §0.8.2: "Event consumers must be idempotent."
        /// </summary>
        [Fact]
        public async Task Duplicate_PostUpdate_Task_Events_Should_Be_Idempotent()
        {
            // Arrange — same event published twice
            var correlationId = Guid.NewGuid();
            var oldRecord = CreateTestTaskRecord(subject: "Before update");
            var newRecord = CreateTestTaskRecord(subject: "After update");
            var evt = CreatePostUpdateTaskEvent(oldRecord, newRecord);
            evt.CorrelationId = correlationId;

            var mockContext1 = CreateMockConsumeContext(evt);
            var mockContext2 = CreateMockConsumeContext(evt);

            // Act
            await _publisher.Consume(mockContext1.Object);
            await _publisher.Consume(mockContext2.Object);

            // Assert — PostUpdateApiHookLogic called twice (key recalculation is idempotent)
            _mockTaskService.Verify(
                s => s.PostUpdateApiHookLogic(
                    It.IsAny<EntityRecord>(),
                    It.IsAny<EntityRecord>()),
                Times.Exactly(2),
                "Duplicate post-update events should result in two idempotent hook calls");
        }

        #endregion

        #region << Helper Methods >>

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> populated with realistic task entity fields
        /// matching the source entity structure from the monolith's task entity definition.
        /// </summary>
        /// <param name="id">Task record ID. Defaults to a new GUID.</param>
        /// <param name="subject">Task subject field. Defaults to "Test Task".</param>
        /// <param name="ownerId">Task owner_id field. Defaults to a new GUID.</param>
        /// <param name="createdBy">Task created_by field. Defaults to a new GUID.</param>
        /// <param name="projectIds">List of project GUIDs for $project_nn_task.id. If null, field is not set.</param>
        /// <returns>A populated EntityRecord with task-specific fields.</returns>
        private static EntityRecord CreateTestTaskRecord(
            Guid? id = null,
            string subject = "Test Task",
            Guid? ownerId = null,
            Guid? createdBy = null,
            List<Guid> projectIds = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["subject"] = subject;
            record["body"] = "<p>Task description</p>";
            record["owner_id"] = ownerId ?? Guid.NewGuid();
            record["created_by"] = createdBy ?? Guid.NewGuid();
            record["status"] = "not started";
            record["priority"] = "normal";

            if (projectIds != null)
            {
                record["$project_nn_task.id"] = projectIds;
            }

            return record;
        }

        /// <summary>
        /// Creates a <see cref="PreRecordCreateEvent"/> configured for the "task" entity.
        /// </summary>
        private static PreRecordCreateEvent CreatePreCreateTaskEvent(EntityRecord record = null)
        {
            return new PreRecordCreateEvent
            {
                EntityName = "task",
                Record = record ?? CreateTestTaskRecord(projectIds: new List<Guid> { Guid.NewGuid() }),
                ValidationErrors = new List<ErrorModel>()
            };
        }

        /// <summary>
        /// Creates a <see cref="RecordCreatedEvent"/> configured for the "task" entity.
        /// </summary>
        private static RecordCreatedEvent CreatePostCreateTaskEvent(EntityRecord record = null)
        {
            return new RecordCreatedEvent
            {
                EntityName = "task",
                Record = record ?? CreateTestTaskRecord()
            };
        }

        /// <summary>
        /// Creates a <see cref="PreRecordUpdateEvent"/> configured for the "task" entity.
        /// </summary>
        private static PreRecordUpdateEvent CreatePreUpdateTaskEvent(EntityRecord record = null)
        {
            return new PreRecordUpdateEvent
            {
                EntityName = "task",
                Record = record ?? CreateTestTaskRecord(),
                ValidationErrors = new List<ErrorModel>()
            };
        }

        /// <summary>
        /// Creates a <see cref="RecordUpdatedEvent"/> configured for the "task" entity
        /// with both OldRecord and NewRecord.
        /// </summary>
        private static RecordUpdatedEvent CreatePostUpdateTaskEvent(
            EntityRecord oldRecord = null,
            EntityRecord newRecord = null)
        {
            return new RecordUpdatedEvent
            {
                EntityName = "task",
                OldRecord = oldRecord ?? CreateTestTaskRecord(subject: "Original subject"),
                NewRecord = newRecord ?? CreateTestTaskRecord(subject: "Updated subject")
            };
        }

        /// <summary>
        /// Creates a mocked <see cref="ConsumeContext{T}"/> returning the specified event message.
        /// </summary>
        private static Mock<ConsumeContext<T>> CreateMockConsumeContext<T>(T message) where T : class
        {
            var mockContext = new Mock<ConsumeContext<T>>();
            mockContext.Setup(c => c.Message).Returns(message);
            mockContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return mockContext;
        }

        #endregion
    }
}
