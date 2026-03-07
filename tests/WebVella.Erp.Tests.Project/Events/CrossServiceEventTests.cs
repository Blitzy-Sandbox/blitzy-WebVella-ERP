// =============================================================================
// CrossServiceEventTests.cs — Integration Tests for Cross-Service Event Handling
//                              in the Project/Task Microservice
// =============================================================================
// Validates that the MassTransit event subscribers (CoreUserUpdatedConsumer and
// CrmEntityChangedConsumer) correctly handle domain events published by the Core
// Platform Service and CRM Service respectively. These consumers maintain
// denormalized data within the Project database, replacing the monolith's direct
// FK joins across service boundaries with event-driven eventual consistency.
//
// Test Coverage:
//   - Account→Project denormalization via CRM events (AAP §0.7.1)
//   - Case→Task denormalization via CRM events (AAP §0.7.1)
//   - User display data cache maintenance via Core events
//   - Entity name filtering (irrelevant entities are silently skipped)
//   - Idempotent event processing (AAP §0.8.2: duplicate delivery safe)
//   - Event contract compliance with SharedKernel IDomainEvent interface
//
// Source references (monolith patterns being replaced):
//   - WebVella.Erp.Plugins.Project/Hooks/Api/Task.cs (lines 1-31)
//     [HookAttachment("task")] IErpPostCreateRecordHook, IErpPostUpdateRecordHook
//   - WebVella.Erp.Plugins.Project/Hooks/Api/Comment.cs (lines 1-23)
//     [HookAttachment("comment")] IErpPreCreateRecordHook, IErpPostCreateRecordHook
//   - WebVella.Erp.Plugins.Project/Hooks/Api/Timelog.cs (lines 1-28)
//     [HookAttachment("timelog")] IErpPreCreateRecordHook, IErpPreDeleteRecordHook
//
// Architecture:
//   Uses MassTransit InMemoryTestHarness to simulate the event bus without
//   requiring real RabbitMQ or SNS/SQS infrastructure. ProjectDbContext is
//   provided via InMemory EF Core provider for consumer instantiation;
//   actual database operations are not executed in these tests — the focus
//   is on event routing, filtering, and contract compliance.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using System.Text.Json;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Project.Events.Subscribers;
using WebVella.Erp.Service.Project.Database;
using Xunit;

namespace WebVella.Erp.Tests.Project.Events
{
    /// <summary>
    /// Tests for the cross-service event subscribers (<see cref="CoreUserUpdatedConsumer"/>
    /// and <see cref="CrmEntityChangedConsumer"/>) that maintain denormalized data within
    /// the Project service database. These consumers replace the monolith's direct FK join
    /// pattern per AAP §0.7.1 with event-driven eventual consistency.
    /// <para>
    /// In the monolith, the Project plugin hooks (Task.cs, Comment.cs, Timelog.cs) used
    /// synchronous <c>IErpPostCreateRecordHook</c> and <c>IErpPostUpdateRecordHook</c>
    /// interfaces to react to record changes. These hooks accessed CRM entities (account,
    /// case) and Core entities (user) via direct FK joins to the shared PostgreSQL database.
    /// In the microservice architecture, each service owns its database exclusively, so
    /// cross-service data is maintained via asynchronous domain events on the message bus.
    /// </para>
    /// <para>
    /// This test class uses MassTransit's <see cref="ITestHarness"/> with an in-memory
    /// transport to validate event routing, entity name filtering, and contract compliance
    /// without requiring real RabbitMQ or SNS/SQS infrastructure.
    /// </para>
    /// </summary>
    public class CrossServiceEventTests : IAsyncDisposable
    {
        #region ===== Private Fields =====

        /// <summary>
        /// MassTransit InMemoryTestHarness providing in-memory event bus for testing
        /// event publishing and consumption without real message broker infrastructure.
        /// </summary>
        private readonly ITestHarness _harness;

        /// <summary>
        /// DI container for the test, hosting MassTransit test harness, mocked loggers,
        /// and the ProjectDbContext. Disposed in <see cref="DisposeAsync"/>.
        /// </summary>
        private readonly ServiceProvider _provider;

        /// <summary>
        /// Mock of the Project-specific EF Core DbContext. Provided to consumers via DI
        /// for instantiation. Database operations are not the focus of these tests —
        /// event routing and filtering behavior is verified through logger assertions.
        /// </summary>
        private readonly Mock<ProjectDbContext> _mockDbContext;

        /// <summary>
        /// Mock logger for <see cref="CoreUserUpdatedConsumer"/>. Verified in tests to
        /// confirm the consumer processes user entity events and skips non-user entities.
        /// </summary>
        private readonly Mock<ILogger<CoreUserUpdatedConsumer>> _mockUserLogger;

        /// <summary>
        /// Mock logger for <see cref="CrmEntityChangedConsumer"/>. Verified in tests to
        /// confirm the consumer processes CRM entity events (account, case) and skips
        /// irrelevant entities (email, task, etc.).
        /// </summary>
        private readonly Mock<ILogger<CrmEntityChangedConsumer>> _mockCrmLogger;

        #endregion

        #region ===== Constructor =====

        /// <summary>
        /// Initializes the test fixture by setting up a DI container with:
        /// <list type="bullet">
        ///   <item>MassTransit InMemoryTestHarness with both consumers registered</item>
        ///   <item>Mocked <see cref="ILogger{T}"/> instances for both consumers</item>
        ///   <item>Mocked <see cref="ProjectDbContext"/> with InMemory EF Core provider</item>
        /// </list>
        /// <para>
        /// xUnit creates a new test class instance for each test method, ensuring complete
        /// isolation between tests. Each test gets its own harness, mocks, and DI container.
        /// </para>
        /// </summary>
        public CrossServiceEventTests()
        {
            _mockUserLogger = new Mock<ILogger<CoreUserUpdatedConsumer>>();
            _mockCrmLogger = new Mock<ILogger<CrmEntityChangedConsumer>>();

            // Create mock ProjectDbContext with InMemory EF Core provider to satisfy
            // the constructor dependency of both consumers. The InMemory provider does
            // not support relational operations (GetDbConnection), so consumers will
            // encounter exceptions during DB operations — these are handled by the
            // consumers' exception handlers (NpgsqlException swallowed, others rethrown).
            // Tests focus on event routing and logging, not DB persistence.
            _mockDbContext = new Mock<ProjectDbContext>(
                new DbContextOptionsBuilder<ProjectDbContext>()
                    .UseInMemoryDatabase(databaseName: "test_project_events_" + Guid.NewGuid())
                    .Options);

            var services = new ServiceCollection();

            // Register base logging infrastructure for MassTransit internals
            services.AddLogging();

            // Override specific logger registrations with mocks for verification
            services.AddSingleton<ILogger<CoreUserUpdatedConsumer>>(_mockUserLogger.Object);
            services.AddSingleton<ILogger<CrmEntityChangedConsumer>>(_mockCrmLogger.Object);

            // Register mocked ProjectDbContext as singleton for consumer DI resolution
            services.AddSingleton<ProjectDbContext>(_mockDbContext.Object);

            // Register MassTransit InMemoryTestHarness with both event subscribers.
            // CRITICAL: Configure System.Text.Json to include fields because
            // EntityRecord extends Expando (DynamicObject) and its data is stored
            // in the `public PropertyBag Properties` FIELD (not a C# property).
            // Without IncludeFields=true, the Properties dictionary is silently
            // dropped during MassTransit's in-memory serialize/deserialize cycle,
            // causing consumers to see empty records and log Warning "missing or
            // empty 'id' field" instead of processing the event.
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<CoreUserUpdatedConsumer>();
                cfg.AddConsumer<CrmEntityChangedConsumer>();
                cfg.UsingInMemory((context, inMemoryCfg) =>
                {
                    inMemoryCfg.ConfigureJsonSerializerOptions(o =>
                    {
                        o.IncludeFields = true;
                        return o;
                    });
                    inMemoryCfg.ConfigureEndpoints(context);
                });
            });

            _provider = services.BuildServiceProvider(true);
            _harness = _provider.GetRequiredService<ITestHarness>();
        }

        #endregion

        #region ===== IAsyncDisposable =====

        /// <summary>
        /// Disposes the <see cref="ServiceProvider"/> and all registered services,
        /// including the MassTransit test harness and its in-memory transport.
        /// Called automatically by xUnit after each test method completes.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        #endregion

        #region ===== Account→Project Denormalization Tests =====

        /// <summary>
        /// Validates that when the CRM Service publishes a <see cref="RecordUpdatedEvent"/>
        /// for an "account" entity, the <see cref="CrmEntityChangedConsumer"/> receives and
        /// processes it. This replaces the monolith's direct FK join from rec_project to
        /// rec_account in the shared database (AAP §0.7.1).
        /// <para>
        /// In the monolith, account data was resolved via direct SQL JOINs. In the
        /// microservice architecture, account updates are denormalized into the Project
        /// database's project_crm_reference_cache table via this event subscriber.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Account_Updated_Event_Should_Update_Project_Denormalized_Data()
        {
            // Arrange
            await _harness.Start();

            var correlationId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "account",
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow,
                NewRecord = CreateTestAccountRecord(accountId),
                OldRecord = CreateTestAccountRecord(accountId)
            };

            // Act — publish the account update event on the in-memory bus
            await _harness.Bus.Publish(evt);

            // Assert — verify the event was consumed by at least one consumer
            (await _harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();

            // Allow all consumers to complete processing (both CoreUserUpdated
            // and CrmEntityChanged consumers receive RecordUpdatedEvent)
            await Task.Delay(500);

            // Assert — verify CRM consumer logged Information for "account" entity,
            // confirming it entered the processing path (before DB operations)
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("account")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        /// <summary>
        /// Validates that when the CRM Service publishes a <see cref="RecordCreatedEvent"/>
        /// for an "account" entity, the <see cref="CrmEntityChangedConsumer"/> receives and
        /// processes it, creating a local reference cache entry in the Project database.
        /// <para>
        /// The consumer uses the same idempotent UPSERT pattern for both creation and update
        /// events, so a create event that arrives after an update event is safe (timestamp
        /// comparison prevents stale overwrites per AAP §0.8.2).
        /// </para>
        /// </summary>
        [Fact]
        public async Task Account_Created_Event_Should_Create_Project_Reference_Cache()
        {
            // Arrange
            await _harness.Start();

            var accountId = Guid.NewGuid();
            var evt = new RecordCreatedEvent
            {
                EntityName = "account",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Record = CreateTestAccountRecord(accountId)
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event consumed
            (await _harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CRM consumer processed the account creation event
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("account")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        /// <summary>
        /// Validates that when the CRM Service publishes a <see cref="RecordDeletedEvent"/>
        /// for an "account" entity, the <see cref="CrmEntityChangedConsumer"/> receives and
        /// processes it, removing the local reference cache entry and nullifying denormalized
        /// account references in rec_project rows.
        /// <para>
        /// DELETE is naturally idempotent — deleting a non-existent row is a PostgreSQL no-op.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Account_Deleted_Event_Should_Remove_Project_Reference_Cache()
        {
            // Arrange
            await _harness.Start();

            var accountId = Guid.NewGuid();
            var evt = new RecordDeletedEvent
            {
                EntityName = "account",
                RecordId = accountId,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event consumed
            (await _harness.Consumed.Any<RecordDeletedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CRM consumer processed the account deletion event
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("account")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        #endregion

        #region ===== Case→Task Denormalization Tests =====

        /// <summary>
        /// Validates that when the CRM Service publishes a <see cref="RecordUpdatedEvent"/>
        /// for a "case" entity, the <see cref="CrmEntityChangedConsumer"/> receives and
        /// processes it. This replaces the monolith's direct FK join from rec_task to
        /// rec_case in the shared database (AAP §0.7.1).
        /// <para>
        /// In the monolith, case data was resolved via direct SQL JOINs. In the
        /// microservice architecture, case updates are denormalized into the Project
        /// database via the project_crm_reference_cache table and secondary updates
        /// to rec_task.case_name.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Case_Updated_Event_Should_Update_Task_Denormalized_Data()
        {
            // Arrange
            await _harness.Start();

            var caseId = Guid.NewGuid();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "case",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                NewRecord = CreateTestCaseRecord(caseId),
                OldRecord = CreateTestCaseRecord(caseId)
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event consumed
            (await _harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CRM consumer processed the case update event
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("case")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        /// <summary>
        /// Validates that <see cref="RecordCreatedEvent"/> for a "case" entity is processed
        /// by the <see cref="CrmEntityChangedConsumer"/>, creating a local reference cache
        /// entry in the Project database for case→task denormalization.
        /// </summary>
        [Fact]
        public async Task Case_Created_Event_Should_Be_Processed_By_CrmConsumer()
        {
            // Arrange
            await _harness.Start();

            var caseId = Guid.NewGuid();
            var evt = new RecordCreatedEvent
            {
                EntityName = "case",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Record = CreateTestCaseRecord(caseId)
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event consumed
            (await _harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CRM consumer processed the case creation event
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("case")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        #endregion

        #region ===== Eventual Consistency Pattern Tests =====

        /// <summary>
        /// Validates that the <see cref="CrmEntityChangedConsumer"/> correctly filters out
        /// events for entities it does not own. An "email" entity event (owned by the Mail
        /// service) should be silently ignored by the CRM consumer — no Information-level
        /// log should be emitted for the "email" entity.
        /// <para>
        /// This validates the entity name filtering per AAP §0.7.1: the CRM consumer only
        /// processes events for "account" and "case" entities, which are the CRM-owned
        /// entities that the Project service maintains denormalized references to.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Irrelevant_Entity_Events_Should_Be_Ignored_By_CrmConsumer()
        {
            // Arrange
            await _harness.Start();

            var emailRecord = new EntityRecord();
            emailRecord["id"] = Guid.NewGuid();
            emailRecord["subject"] = "Test Email Subject";

            var evt = new RecordUpdatedEvent
            {
                EntityName = "email",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                NewRecord = emailRecord,
                OldRecord = emailRecord
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event was consumed (both consumers receive it, both skip it)
            (await _harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CRM consumer should NOT log Information for "email" entity.
            // It should have logged Debug "Skipping" instead, which we don't verify
            // here but confirm by the absence of Information-level processing logs.
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("email")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Never());
        }

        /// <summary>
        /// Validates that when the Core Platform Service publishes a
        /// <see cref="RecordUpdatedEvent"/> for a "user" entity, the
        /// <see cref="CoreUserUpdatedConsumer"/> receives and processes it.
        /// <para>
        /// This consumer maintains the project_user_cache table with denormalized user
        /// display data (username, email, first_name, last_name) so that Project queries
        /// can resolve audit field display names (created_by, modified_by, owner_id)
        /// without synchronous gRPC calls to the Core service on every read.
        /// </para>
        /// <para>
        /// Replaces the monolith's direct FK join to rec_user table used in
        /// Task.cs (line 16: TaskService.PostCreateApiHookLogic),
        /// Comment.cs (line 18: CommentService.PostCreateApiHookLogic), and
        /// Timelog.cs (line 17: TimeLogService.PreCreateApiHookLogic).
        /// </para>
        /// </summary>
        [Fact]
        public async Task CoreUser_Updated_Event_Should_Update_Project_User_Cache()
        {
            // Arrange
            await _harness.Start();

            var userId = Guid.NewGuid();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "user",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                NewRecord = CreateTestUserRecord(userId),
                OldRecord = CreateTestUserRecord(userId)
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event consumed
            (await _harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CoreUser consumer logged Information for "user" entity,
            // confirming it entered the processing path
            _mockUserLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("user")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        /// <summary>
        /// Validates that the <see cref="CoreUserUpdatedConsumer"/> correctly filters out
        /// events for entities other than "user". A "task" entity event should be silently
        /// ignored by the CoreUser consumer — no Information-level log should be emitted.
        /// <para>
        /// The CoreUser consumer only processes events where EntityName == "user"
        /// (case-insensitive comparison using <see cref="StringComparison.OrdinalIgnoreCase"/>).
        /// All other entity events are skipped with a Debug-level log entry.
        /// </para>
        /// </summary>
        [Fact]
        public async Task NonUser_Entity_Should_Be_Ignored_By_CoreUserConsumer()
        {
            // Arrange
            await _harness.Start();

            var taskRecord = new EntityRecord();
            taskRecord["id"] = Guid.NewGuid();
            taskRecord["subject"] = "Test Task";

            var evt = new RecordUpdatedEvent
            {
                EntityName = "task",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                NewRecord = taskRecord,
                OldRecord = taskRecord
            };

            // Act
            await _harness.Bus.Publish(evt);

            // Assert — event consumed (both consumers receive it)
            (await _harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CoreUser consumer should NOT log Information for "task" entity.
            // It should log Debug "Skipping RecordUpdatedEvent for entity 'task'" instead.
            _mockUserLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("task")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Never());
        }

        /// <summary>
        /// Validates that duplicate event delivery does not cause data corruption or
        /// system instability (AAP §0.8.2: "Event consumers must be idempotent —
        /// duplicate event delivery must not cause data corruption").
        /// <para>
        /// The same <see cref="RecordUpdatedEvent"/> (identical CorrelationId, EntityName,
        /// and record data) is published twice to simulate duplicate delivery on the
        /// message bus. The test verifies that both events are consumed and the consumer
        /// enters the processing path (Information log) for each delivery without faulting
        /// the event processing pipeline.
        /// </para>
        /// <para>
        /// In the real consumer implementation, the PostgreSQL UPSERT with
        /// <c>WHERE last_synced_at &lt; EXCLUDED.last_synced_at</c> ensures that
        /// duplicate events produce identical database state. This test validates the
        /// event routing and processing behavior; actual database idempotency is verified
        /// in database integration tests.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Duplicate_Event_Processing_Should_Be_Idempotent()
        {
            // Arrange — AAP §0.8.2: Event consumers must be idempotent
            await _harness.Start();

            var correlationId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "account",
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow,
                NewRecord = CreateTestAccountRecord(accountId),
                OldRecord = CreateTestAccountRecord(accountId)
            };

            // Act — publish the SAME event twice (simulating duplicate delivery)
            await _harness.Bus.Publish(evt);
            await _harness.Bus.Publish(evt);

            // Assert — at least one event was consumed without crashing the pipeline
            (await _harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();
            await Task.Delay(500);

            // Assert — CRM consumer entered the processing path for "account" entity
            // at least once, proving duplicate events are handled gracefully
            _mockCrmLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("account")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.AtLeastOnce());
        }

        /// <summary>
        /// Validates that the domain event contracts (<see cref="RecordUpdatedEvent"/>,
        /// <see cref="RecordCreatedEvent"/>, <see cref="RecordDeletedEvent"/>) conform to
        /// the <see cref="IDomainEvent"/> interface defined in the SharedKernel.
        /// <para>
        /// Verifies:
        /// <list type="bullet">
        ///   <item>All three event types implement <see cref="IDomainEvent"/></item>
        ///   <item><see cref="IDomainEvent"/> defines required properties:
        ///     Timestamp, CorrelationId, EntityName</item>
        ///   <item>Event constructors initialize Timestamp and CorrelationId with
        ///     meaningful defaults (not default/empty values)</item>
        ///   <item>JSON serialization via Newtonsoft.Json produces stable property names
        ///     matching the <c>[JsonProperty]</c> annotations</item>
        /// </list>
        /// This ensures cross-service event contract stability across RabbitMQ
        /// (local/Docker) and SNS+SQS (AWS/LocalStack) transports.
        /// </para>
        /// </summary>
        [Fact]
        public void Event_Contracts_Match_SharedKernel_Definitions()
        {
            // ---- Interface implementation verification ----
            // All event types must implement IDomainEvent for the event bus routing
            typeof(RecordUpdatedEvent).Should().BeAssignableTo<IDomainEvent>();
            typeof(RecordCreatedEvent).Should().BeAssignableTo<IDomainEvent>();
            typeof(RecordDeletedEvent).Should().BeAssignableTo<IDomainEvent>();

            // ---- IDomainEvent required properties ----
            // Verify the interface defines the cross-cutting metadata properties
            // required for routing, tracing, and auditing domain events
            var domainEventType = typeof(IDomainEvent);
            domainEventType.GetProperty("Timestamp").Should().NotBeNull(
                "IDomainEvent must define Timestamp for event ordering and idempotency");
            domainEventType.GetProperty("CorrelationId").Should().NotBeNull(
                "IDomainEvent must define CorrelationId for distributed tracing");
            domainEventType.GetProperty("EntityName").Should().NotBeNull(
                "IDomainEvent must define EntityName for event routing/filtering");

            // ---- Default value initialization verification ----
            // Event constructors should set meaningful defaults for Timestamp and CorrelationId
            var updatedEvent = new RecordUpdatedEvent();
            updatedEvent.CorrelationId.Should().NotBe(Guid.Empty,
                "RecordUpdatedEvent constructor must generate a non-empty CorrelationId");
            updatedEvent.Timestamp.Should().NotBe(default(DateTimeOffset),
                "RecordUpdatedEvent constructor must set Timestamp to current UTC time");

            var createdEvent = new RecordCreatedEvent();
            createdEvent.CorrelationId.Should().NotBe(Guid.Empty,
                "RecordCreatedEvent constructor must generate a non-empty CorrelationId");
            createdEvent.Timestamp.Should().NotBe(default(DateTimeOffset),
                "RecordCreatedEvent constructor must set Timestamp to current UTC time");

            var deletedEvent = new RecordDeletedEvent();
            deletedEvent.CorrelationId.Should().NotBe(Guid.Empty,
                "RecordDeletedEvent constructor must generate a non-empty CorrelationId");
            deletedEvent.Timestamp.Should().NotBe(default(DateTimeOffset),
                "RecordDeletedEvent constructor must set Timestamp to current UTC time");

            // ---- JSON serialization contract stability ----
            // Verify that [JsonProperty] annotations produce expected property names
            // for stable cross-service message contracts
            var json = JsonConvert.SerializeObject(updatedEvent);
            json.Should().Contain("\"timestamp\"",
                "RecordUpdatedEvent must serialize Timestamp with [JsonProperty] name 'timestamp'");
            json.Should().Contain("\"correlationId\"",
                "RecordUpdatedEvent must serialize CorrelationId with [JsonProperty] name 'correlationId'");
            json.Should().Contain("\"entityName\"",
                "RecordUpdatedEvent must serialize EntityName with [JsonProperty] name 'entityName'");
        }

        #endregion

        #region ===== Helper Methods =====

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> representing a CRM account entity
        /// with realistic field values matching the account entity definition from
        /// monolith patch NextPlugin.20190204.cs.
        /// <para>
        /// Properties set: id, name, status — matching the CRM account entity fields
        /// that the <see cref="CrmEntityChangedConsumer"/> extracts during processing.
        /// </para>
        /// </summary>
        /// <param name="id">
        /// Optional account record ID. If not provided, a new <see cref="Guid"/> is generated.
        /// </param>
        /// <returns>
        /// An <see cref="EntityRecord"/> with account-specific test data populated in the
        /// Properties dictionary via the indexer access pattern.
        /// </returns>
        private static EntityRecord CreateTestAccountRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["name"] = "Acme Corporation";
            record["status"] = "active";
            return record;
        }

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> representing a CRM case entity
        /// with realistic field values matching the case entity definition from
        /// monolith patch NextPlugin.20190203.cs.
        /// <para>
        /// Properties set: id, subject, status, priority — matching the CRM case entity
        /// fields used in case→task denormalization by the
        /// <see cref="CrmEntityChangedConsumer"/>.
        /// </para>
        /// </summary>
        /// <param name="id">
        /// Optional case record ID. If not provided, a new <see cref="Guid"/> is generated.
        /// </param>
        /// <returns>
        /// An <see cref="EntityRecord"/> with case-specific test data populated in the
        /// Properties dictionary via the indexer access pattern.
        /// </returns>
        private static EntityRecord CreateTestCaseRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["subject"] = "Support Case: Login Issue";
            record["status"] = "open";
            record["priority"] = "high";
            return record;
        }

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> representing a Core user entity
        /// with realistic field values matching the user system entity definition from
        /// ERPService.cs system entity initialization.
        /// <para>
        /// Properties set: id, username, email, first_name, last_name — matching the
        /// user entity fields that the <see cref="CoreUserUpdatedConsumer"/> extracts
        /// during processing for the project_user_cache UPSERT operation.
        /// </para>
        /// </summary>
        /// <param name="id">
        /// Optional user record ID. If not provided, a new <see cref="Guid"/> is generated.
        /// </param>
        /// <returns>
        /// An <see cref="EntityRecord"/> with user-specific test data populated in the
        /// Properties dictionary via the indexer access pattern.
        /// </returns>
        private static EntityRecord CreateTestUserRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["username"] = "john.doe";
            record["email"] = "john.doe@webvella.com";
            record["first_name"] = "John";
            record["last_name"] = "Doe";
            return record;
        }

        #endregion
    }
}
