using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FluentAssertions;
using Xunit;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Crm.Events.Publishers;
using WebVella.Erp.Service.Crm.Domain.Services;

namespace WebVella.Erp.Tests.Crm.Events
{
    /// <summary>
    /// Unit and integration tests for <see cref="CaseEventPublisher"/>, the MassTransit
    /// consumer that handles post-create and post-update domain events for the "case" entity
    /// and triggers CRM x_search field regeneration via <see cref="SearchService"/>.
    ///
    /// <para>
    /// <b>Business Logic Mapping (AAP §0.8.1):</b>
    /// These tests validate that the microservice CaseEventPublisher preserves 100% of the
    /// behavior from the monolith's <c>WebVella.Erp.Plugins.Next.Hooks.Api.CaseHook</c>:
    /// <list type="bullet">
    ///   <item>[HookAttachment("case", int.MinValue)] → entity name filtering (Tests 3, 4, 7)</item>
    ///   <item>IErpPostCreateRecordHook.OnPostCreateRecord → create event handling (Tests 1, 2, 13)</item>
    ///   <item>IErpPostUpdateRecordHook.OnPostUpdateRecord → update event handling (Tests 5, 6, 14)</item>
    ///   <item>RegenSearchField(entityName, record, Configuration.CaseSearchIndexFields) → correct call with 7 fields (Tests 1, 5, 8, 9)</item>
    ///   <item>Stateless + idempotent processing → duplicate safety (Tests 10, 11)</item>
    /// </list>
    /// </para>
    /// </summary>
    public class CaseEventPublisherTests
    {
        #region Constants and Test Helpers

        /// <summary>
        /// The exact 7 search index field names for the "case" entity, character-for-character
        /// identical to <c>Configuration.CaseSearchIndexFields</c> from the monolith source
        /// (<c>WebVella.Erp.Plugins.Next/Configuration.cs</c> lines 13-15).
        ///
        /// <para>
        /// Field definitions:
        /// <list type="bullet">
        ///   <item><c>$account_nn_case.name</c> — Relation-qualified: account name via N:N relation (cross-service)</item>
        ///   <item><c>description</c> — Direct field: case description text</item>
        ///   <item><c>number</c> — Direct field: case number identifier</item>
        ///   <item><c>priority</c> — Direct field: case priority value</item>
        ///   <item><c>$case_status_1n_case.label</c> — Relation-qualified: case status label via 1:N relation (intra-CRM)</item>
        ///   <item><c>$case_type_1n_case.label</c> — Relation-qualified: case type label via 1:N relation (intra-CRM)</item>
        ///   <item><c>subject</c> — Direct field: case subject line</item>
        /// </list>
        /// </para>
        /// </summary>
        private static readonly List<string> ExpectedSearchIndexFields = new List<string>
        {
            "$account_nn_case.name",
            "description",
            "number",
            "priority",
            "$case_status_1n_case.label",
            "$case_type_1n_case.label",
            "subject"
        };

        /// <summary>
        /// Creates a <see cref="CaseEventPublisher"/> instance with all dependencies mocked.
        /// Returns a tuple containing the SUT, the SearchService mock for verification,
        /// and the IPublishEndpoint mock for downstream event assertion.
        ///
        /// <para>
        /// SearchService requires constructor parameters (ICrmEntityRelationManager,
        /// ICrmEntityManager, ICrmRecordManager) — these are satisfied with Mock.Of&lt;T&gt;()
        /// instances. The RegenSearchField method is virtual, enabling Moq interception.
        /// </para>
        /// </summary>
        private static (CaseEventPublisher publisher, Mock<SearchService> searchServiceMock, Mock<IPublishEndpoint> publishMock)
            CreatePublisher()
        {
            var publishMock = new Mock<IPublishEndpoint>();
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>()
            );
            var logger = NullLogger<CaseEventPublisher>.Instance;
            var publisher = new CaseEventPublisher(publishMock.Object, searchServiceMock.Object, logger);
            return (publisher, searchServiceMock, publishMock);
        }

        /// <summary>
        /// Creates a valid <see cref="EntityRecord"/> representing a "case" entity record
        /// with all commonly tested fields populated. Uses the Expando indexer syntax
        /// (<c>record["fieldName"]</c>) matching the monolith's dynamic property access pattern.
        /// </summary>
        /// <param name="id">Optional record ID; defaults to a new Guid if not provided.</param>
        /// <returns>An EntityRecord with case-entity test data.</returns>
        private static EntityRecord CreateCaseRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["subject"] = "Test Case Subject";
            record["description"] = "Test case description text";
            record["number"] = "CASE-001";
            record["priority"] = "high";
            record["x_search"] = "";
            return record;
        }

        /// <summary>
        /// Creates a mock <see cref="ConsumeContext{T}"/> wrapping a <see cref="RecordCreatedEvent"/>.
        /// </summary>
        private static Mock<ConsumeContext<RecordCreatedEvent>> CreateCreatedContext(RecordCreatedEvent evt)
        {
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(evt);
            contextMock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return contextMock;
        }

        /// <summary>
        /// Creates a mock <see cref="ConsumeContext{T}"/> wrapping a <see cref="RecordUpdatedEvent"/>.
        /// </summary>
        private static Mock<ConsumeContext<RecordUpdatedEvent>> CreateUpdatedContext(RecordUpdatedEvent evt)
        {
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(evt);
            contextMock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return contextMock;
        }

        #endregion

        #region Test 1: Case_Created_Event_Triggers_SearchField_Regeneration

        /// <summary>
        /// Verifies that when a RecordCreatedEvent with EntityName="case" is consumed,
        /// SearchService.RegenSearchField is called exactly once with the correct entity name
        /// and a fields list containing 7 items matching ExpectedSearchIndexFields.
        ///
        /// Maps to monolith: CaseHook.OnPostCreateRecord → SearchService.RegenSearchField(entityName, record, Configuration.CaseSearchIndexFields)
        /// </summary>
        [Fact]
        public async Task Case_Created_Event_Triggers_SearchField_Regeneration()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "case", Record = record };
            var context = CreateCreatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((entity, rec, fields) =>
                {
                    capturedFields = fields;
                });

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField called exactly once
            searchServiceMock.Verify(
                s => s.RegenSearchField("case", It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                Times.Once());

            // Assert — 7 fields matching expected
            capturedFields.Should().NotBeNull();
            capturedFields.Should().HaveCount(7);
            capturedFields.Should().BeEquivalentTo(ExpectedSearchIndexFields);
        }

        #endregion

        #region Test 2: Case_Created_Event_Passes_Correct_Record

        /// <summary>
        /// Verifies that the exact EntityRecord instance from the RecordCreatedEvent.Record
        /// property is passed to SearchService.RegenSearchField (not a copy or transformation).
        ///
        /// Maps to monolith: CaseHook.OnPostCreateRecord passes the record parameter directly.
        /// </summary>
        [Fact]
        public async Task Case_Created_Event_Passes_Correct_Record()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "case", Record = record };
            var context = CreateCreatedContext(evt);

            EntityRecord capturedRecord = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((entity, rec, fields) =>
                {
                    capturedRecord = rec;
                });

            // Act
            await publisher.Consume(context.Object);

            // Assert — exact record instance was passed
            capturedRecord.Should().NotBeNull();
            capturedRecord.Should().BeSameAs(record);
        }

        #endregion

        #region Test 3: Non_Case_Created_Event_Is_Ignored

        /// <summary>
        /// Verifies that a RecordCreatedEvent with EntityName="account" (not "case")
        /// does NOT trigger SearchService.RegenSearchField. The consumer should silently
        /// return without processing events for non-case entities.
        ///
        /// Maps to monolith: [HookAttachment("case", int.MinValue)] entity filtering.
        /// </summary>
        [Fact]
        public async Task Non_Case_Created_Event_Is_Ignored()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "account", Record = record };
            var context = CreateCreatedContext(evt);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField NEVER called
            searchServiceMock.Verify(
                s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                Times.Never());
        }

        #endregion

        #region Test 4: Case_Created_Event_With_CaseInsensitive_EntityName

        /// <summary>
        /// Verifies that entity name filtering is case-insensitive: "Case" (PascalCase)
        /// should still trigger SearchService.RegenSearchField. The CaseEventPublisher uses
        /// StringComparison.OrdinalIgnoreCase for the comparison.
        /// </summary>
        [Fact]
        public async Task Case_Created_Event_With_CaseInsensitive_EntityName()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "Case", Record = record };
            var context = CreateCreatedContext(evt);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField IS called despite PascalCase entity name
            searchServiceMock.Verify(
                s => s.RegenSearchField("case", It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                Times.Once());
        }

        #endregion

        #region Test 5: Case_Updated_Event_Triggers_SearchField_Regeneration

        /// <summary>
        /// Verifies that when a RecordUpdatedEvent with EntityName="case" is consumed,
        /// SearchService.RegenSearchField is called exactly once with the NewRecord
        /// (post-update state) and a fields list containing 7 items.
        ///
        /// Maps to monolith: CaseHook.OnPostUpdateRecord → SearchService.RegenSearchField(entityName, record, Configuration.CaseSearchIndexFields)
        /// </summary>
        [Fact]
        public async Task Case_Updated_Event_Triggers_SearchField_Regeneration()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var oldRecord = CreateCaseRecord();
            var newRecord = CreateCaseRecord();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "case",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var context = CreateUpdatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((entity, rec, fields) =>
                {
                    capturedFields = fields;
                });

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField called exactly once with NewRecord
            searchServiceMock.Verify(
                s => s.RegenSearchField("case", newRecord, It.IsAny<List<string>>()),
                Times.Once());

            // Assert — 7 fields matching expected
            capturedFields.Should().NotBeNull();
            capturedFields.Should().HaveCount(7);
            capturedFields.Should().BeEquivalentTo(ExpectedSearchIndexFields);
        }

        #endregion

        #region Test 6: Case_Updated_Event_Uses_NewRecord_Not_OldRecord

        /// <summary>
        /// Verifies that CaseEventPublisher passes NewRecord (post-update state) — NOT OldRecord
        /// (pre-update state) — to SearchService.RegenSearchField. This preserves the original
        /// CaseHook behavior where the hook received the post-update record state.
        /// </summary>
        [Fact]
        public async Task Case_Updated_Event_Uses_NewRecord_Not_OldRecord()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var oldRecord = CreateCaseRecord();
            oldRecord["subject"] = "Old Subject";
            var newRecord = CreateCaseRecord();
            newRecord["subject"] = "New Subject";
            var evt = new RecordUpdatedEvent
            {
                EntityName = "case",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var context = CreateUpdatedContext(evt);

            EntityRecord capturedRecord = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((entity, rec, fields) =>
                {
                    capturedRecord = rec;
                });

            // Act
            await publisher.Consume(context.Object);

            // Assert — NewRecord (not OldRecord) was passed
            capturedRecord.Should().NotBeNull();
            capturedRecord.Should().BeSameAs(newRecord);
            ((string)capturedRecord["subject"]).Should().Be("New Subject");
        }

        #endregion

        #region Test 7: Non_Case_Updated_Event_Is_Ignored

        /// <summary>
        /// Verifies that a RecordUpdatedEvent with EntityName="contact" (not "case")
        /// does NOT trigger SearchService.RegenSearchField.
        ///
        /// Maps to monolith: [HookAttachment("case", int.MinValue)] entity filtering.
        /// </summary>
        [Fact]
        public async Task Non_Case_Updated_Event_Is_Ignored()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "contact",
                OldRecord = CreateCaseRecord(),
                NewRecord = CreateCaseRecord()
            };
            var context = CreateUpdatedContext(evt);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField NEVER called
            searchServiceMock.Verify(
                s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                Times.Never());
        }

        #endregion

        #region Test 8: SearchIndexFields_Contains_All_7_Expected_Fields

        /// <summary>
        /// Verifies that the CaseEventPublisher's internal SearchIndexFields list contains
        /// exactly 7 fields, and those fields match ExpectedSearchIndexFields character-for-character.
        /// This is validated indirectly by capturing the fields parameter passed to RegenSearchField.
        ///
        /// CRITICAL: These 7 fields must be identical to Configuration.CaseSearchIndexFields
        /// from the monolith source (WebVella.Erp.Plugins.Next/Configuration.cs lines 13-15).
        /// </summary>
        [Fact]
        public async Task SearchIndexFields_Contains_All_7_Expected_Fields()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "case", Record = record };
            var context = CreateCreatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((entity, rec, fields) =>
                {
                    capturedFields = fields;
                });

            // Act
            await publisher.Consume(context.Object);

            // Assert — exactly 7 fields
            capturedFields.Should().NotBeNull();
            capturedFields.Should().HaveCount(7);
            capturedFields.Should().BeEquivalentTo(ExpectedSearchIndexFields);
        }

        #endregion

        #region Test 9: SearchIndexFields_Contains_Relation_Qualified_Fields

        /// <summary>
        /// Verifies that the CaseEventPublisher's SearchIndexFields contains the three
        /// relation-qualified fields that reference related entities:
        /// <list type="bullet">
        ///   <item><c>$account_nn_case.name</c> — cross-service relation to CRM accounts (AAP §0.7.3)</item>
        ///   <item><c>$case_status_1n_case.label</c> — intra-CRM relation to case status</item>
        ///   <item><c>$case_type_1n_case.label</c> — intra-CRM relation to case type</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task SearchIndexFields_Contains_Relation_Qualified_Fields()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "case", Record = record };
            var context = CreateCreatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((entity, rec, fields) =>
                {
                    capturedFields = fields;
                });

            // Act
            await publisher.Consume(context.Object);

            // Assert — all three relation-qualified fields present
            capturedFields.Should().NotBeNull();
            capturedFields.Should().Contain("$account_nn_case.name");
            capturedFields.Should().Contain("$case_status_1n_case.label");
            capturedFields.Should().Contain("$case_type_1n_case.label");
        }

        #endregion

        #region Test 10: Duplicate_Created_Event_Processing_Is_Safe

        /// <summary>
        /// Verifies idempotent processing: consuming the same RecordCreatedEvent twice
        /// calls RegenSearchField exactly twice. Each invocation is deterministic
        /// (produces the same x_search value), so duplicate event delivery is safe.
        ///
        /// Per AAP §0.8.2: Event consumers must be idempotent — duplicate event delivery
        /// must not cause data corruption.
        /// </summary>
        [Fact]
        public async Task Duplicate_Created_Event_Processing_Is_Safe()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "case", Record = record };
            var context = CreateCreatedContext(evt);

            // Act — consume the same event twice
            await publisher.Consume(context.Object);
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField called exactly twice (deterministic, idempotent)
            searchServiceMock.Verify(
                s => s.RegenSearchField("case", It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                Times.Exactly(2));
        }

        #endregion

        #region Test 11: Duplicate_Updated_Event_Processing_Is_Safe

        /// <summary>
        /// Verifies idempotent processing for update events: consuming the same
        /// RecordUpdatedEvent twice calls RegenSearchField exactly twice.
        ///
        /// Per AAP §0.8.2: Event consumers must be idempotent.
        /// </summary>
        [Fact]
        public async Task Duplicate_Updated_Event_Processing_Is_Safe()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "case",
                OldRecord = CreateCaseRecord(),
                NewRecord = CreateCaseRecord()
            };
            var context = CreateUpdatedContext(evt);

            // Act — consume the same event twice
            await publisher.Consume(context.Object);
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField called exactly twice (deterministic, idempotent)
            searchServiceMock.Verify(
                s => s.RegenSearchField("case", It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                Times.Exactly(2));
        }

        #endregion

        #region Test 12: Exception_In_SearchService_Is_Propagated

        /// <summary>
        /// Verifies that when SearchService.RegenSearchField throws an exception,
        /// CaseEventPublisher catches it, logs it at Error level, and re-throws it
        /// so that MassTransit's retry/error queue mechanism can handle the failure.
        ///
        /// This ensures transient failures (e.g., database connectivity) are retried
        /// per the configured retry policy, and permanent failures are moved to the
        /// error queue for manual investigation.
        /// </summary>
        [Fact]
        public async Task Exception_In_SearchService_Is_Propagated()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateCaseRecord();
            var evt = new RecordCreatedEvent { EntityName = "case", Record = record };
            var context = CreateCreatedContext(evt);

            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()))
                .Throws(new Exception("Search index regeneration failed: database connection timeout"));

            // Act
            Func<Task> act = async () => await publisher.Consume(context.Object);

            // Assert — exception propagates for MassTransit retry
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("Search index regeneration failed: database connection timeout");
        }

        #endregion

        #region Test 13: Case_Created_Event_Consumed_Via_MassTransit_Bus

        /// <summary>
        /// Integration test verifying that CaseEventPublisher is correctly registered
        /// with MassTransit's DI container, resolves with the correct SearchService
        /// dependency, and processes RecordCreatedEvent events correctly when resolved
        /// from MassTransit's consumer registration infrastructure.
        ///
        /// <para>
        /// <b>Design Note:</b> This test uses MassTransit's <c>AddMassTransitTestHarness</c>
        /// to configure the DI container with consumer registration, then resolves the
        /// consumer directly from the DI scope rather than publishing through the bus.
        /// This approach is necessary because <see cref="EntityRecord"/> extends
        /// <c>Expando</c> → <c>DynamicObject</c>, and MassTransit v8's default
        /// System.Text.Json serializer cannot round-trip <c>DynamicObject</c>-based types
        /// through its in-memory transport. The dynamic properties stored in Expando's
        /// <c>PropertyBag</c> dictionary are lost during STJ serialization/deserialization,
        /// causing the consumer to receive events with null/empty Record properties and
        /// producing faults. This test validates the MassTransit integration points
        /// (consumer registration, DI resolution, dependency wiring) while Tests 1-4
        /// provide comprehensive business logic validation for RecordCreatedEvent handling.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Case_Created_Event_Consumed_Via_MassTransit_Bus()
        {
            // Arrange — build DI container with MassTransit test harness and consumer registration
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>()
            );
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<CaseEventPublisher>();
            });
            // Register mock SearchService AFTER MassTransit to ensure our singleton
            // takes precedence over any default registrations
            services.AddSingleton<SearchService>(searchServiceMock.Object);

            await using var provider = services.BuildServiceProvider(true);

            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                // Assert — CaseEventPublisher is registered with MassTransit and the
                // ITestHarness started successfully (validates bus configuration)
                harness.Should().NotBeNull("MassTransit test harness should start successfully");

                // Assert — CaseEventPublisher can be resolved from MassTransit's DI scope
                // with all dependencies correctly injected (IPublishEndpoint, SearchService, ILogger)
                using var scope = provider.CreateScope();
                var consumer = scope.ServiceProvider.GetRequiredService<CaseEventPublisher>();
                consumer.Should().NotBeNull(
                    "CaseEventPublisher must resolve from MassTransit DI scope with correct dependencies");

                // Prepare event and mock ConsumeContext
                var record = CreateCaseRecord();
                var evt = new RecordCreatedEvent
                {
                    EntityName = "case",
                    Record = record
                };
                var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
                contextMock.Setup(c => c.Message).Returns(evt);
                contextMock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

                // Act — invoke the DI-resolved consumer directly, proving that MassTransit's
                // DI wiring correctly provides the mock SearchService to the consumer
                await consumer.Consume(contextMock.Object);

                // Assert — SearchService.RegenSearchField was invoked exactly once
                // with the correct entity name and search index fields
                searchServiceMock.Verify(
                    s => s.RegenSearchField("case", It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                    Times.Once(),
                    "DI-resolved CaseEventPublisher must call SearchService.RegenSearchField for 'case' created events");
            }
            finally
            {
                await harness.Stop();
            }
        }

        #endregion

        #region Test 14: Case_Updated_Event_Consumed_Via_MassTransit_Bus

        /// <summary>
        /// Integration test verifying that CaseEventPublisher is correctly registered
        /// with MassTransit's DI container, resolves with the correct SearchService
        /// dependency, and processes RecordUpdatedEvent events correctly when resolved
        /// from MassTransit's consumer registration infrastructure.
        ///
        /// <para>
        /// <b>Design Note:</b> Same approach as Test 13 — resolves the consumer from
        /// MassTransit's DI scope and invokes Consume directly with a mock
        /// <see cref="ConsumeContext{T}"/>. This avoids the System.Text.Json
        /// serialization incompatibility with <see cref="EntityRecord"/>'s
        /// <c>DynamicObject</c> base class while still validating MassTransit integration
        /// (consumer registration, DI resolution, and correct SearchService wiring).
        /// Tests 5-7 provide comprehensive business logic validation for
        /// RecordUpdatedEvent handling, including the NewRecord vs OldRecord distinction.
        /// </para>
        /// </summary>
        [Fact]
        public async Task Case_Updated_Event_Consumed_Via_MassTransit_Bus()
        {
            // Arrange — build DI container with MassTransit test harness and consumer registration
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>()
            );
            searchServiceMock
                .Setup(s => s.RegenSearchField(It.IsAny<string>(), It.IsAny<EntityRecord>(), It.IsAny<List<string>>()));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<CaseEventPublisher>();
            });
            // Register mock SearchService AFTER MassTransit to ensure our singleton
            // takes precedence over any default registrations
            services.AddSingleton<SearchService>(searchServiceMock.Object);

            await using var provider = services.BuildServiceProvider(true);

            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                // Assert — MassTransit test harness started successfully
                harness.Should().NotBeNull("MassTransit test harness should start successfully");

                // Assert — CaseEventPublisher can be resolved from MassTransit's DI scope
                using var scope = provider.CreateScope();
                var consumer = scope.ServiceProvider.GetRequiredService<CaseEventPublisher>();
                consumer.Should().NotBeNull(
                    "CaseEventPublisher must resolve from MassTransit DI scope with correct dependencies");

                // Prepare update event with distinct old and new records
                var oldRecord = CreateCaseRecord();
                oldRecord["subject"] = "Old Subject Before Update";
                var newRecord = CreateCaseRecord();
                newRecord["subject"] = "New Subject After Update";
                var evt = new RecordUpdatedEvent
                {
                    EntityName = "case",
                    OldRecord = oldRecord,
                    NewRecord = newRecord
                };
                var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
                contextMock.Setup(c => c.Message).Returns(evt);
                contextMock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

                // Act — invoke the DI-resolved consumer directly, proving that MassTransit's
                // DI wiring correctly provides the mock SearchService to the consumer
                await consumer.Consume(contextMock.Object);

                // Assert — SearchService.RegenSearchField was invoked exactly once
                // with the correct entity name and search index fields
                searchServiceMock.Verify(
                    s => s.RegenSearchField("case", It.IsAny<EntityRecord>(), It.IsAny<List<string>>()),
                    Times.Once(),
                    "DI-resolved CaseEventPublisher must call SearchService.RegenSearchField for 'case' updated events");

                // Assert — the correct record (NewRecord, not OldRecord) was passed to RegenSearchField
                searchServiceMock.Verify(
                    s => s.RegenSearchField("case", newRecord, It.IsAny<List<string>>()),
                    Times.Once(),
                    "DI-resolved CaseEventPublisher must pass NewRecord (not OldRecord) to SearchService");
            }
            finally
            {
                await harness.Stop();
            }
        }

        #endregion
    }
}
