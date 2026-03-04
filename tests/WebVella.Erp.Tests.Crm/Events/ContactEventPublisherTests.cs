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
    /// Unit and integration tests for <see cref="ContactEventPublisher"/>.
    /// Validates that contact CRUD domain events correctly trigger x_search
    /// regeneration via <see cref="SearchService"/>, preserving the monolith's
    /// <c>ContactHook</c> behavior from <c>WebVella.Erp.Plugins.Next.Hooks.Api</c>.
    ///
    /// <para>
    /// <b>Business Logic Mapping (AAP §0.8.1):</b>
    /// <list type="bullet">
    ///   <item><c>[HookAttachment("contact", int.MinValue)]</c> → entity name filtering (Tests 3, 4, 7)</item>
    ///   <item><c>IErpPostCreateRecordHook.OnPostCreateRecord</c> → create event handling (Tests 1, 2, 13)</item>
    ///   <item><c>IErpPostUpdateRecordHook.OnPostUpdateRecord</c> → update event handling (Tests 5, 6, 14)</item>
    ///   <item><c>RegenSearchField(entityName, record, ContactSearchIndexFields)</c> → correct 15-field call (Tests 1, 5, 8, 9)</item>
    ///   <item>Stateless + idempotent operation → duplicate safety (Tests 10, 11)</item>
    /// </list>
    /// </para>
    /// </summary>
    public class ContactEventPublisherTests
    {
        /// <summary>
        /// The exact 15 contact search index fields, character-for-character identical to
        /// <c>Configuration.ContactSearchIndexFields</c> from the monolith (Configuration.cs lines 17-19).
        /// Contains 13 direct fields and 2 relation-qualified fields:
        /// <c>$country_1n_contact.label</c> (cross-service to Core per AAP §0.7.3) and
        /// <c>$account_nn_contact.name</c> (intra-CRM relation).
        /// </summary>
        private static readonly List<string> ExpectedSearchIndexFields = new List<string>
        {
            "city",
            "$country_1n_contact.label",
            "$account_nn_contact.name",
            "email",
            "fax_phone",
            "first_name",
            "fixed_phone",
            "job_title",
            "last_name",
            "mobile_phone",
            "notes",
            "post_code",
            "region",
            "street",
            "street_2"
        };

        /// <summary>
        /// Creates a <see cref="ContactEventPublisher"/> with mocked dependencies for unit testing.
        /// Replaces the monolith pattern of direct <c>new SearchService()</c> instantiation
        /// with constructor-injected mock dependencies.
        /// </summary>
        /// <returns>
        /// A tuple containing the publisher under test, the mock SearchService for verification,
        /// and the mock IPublishEndpoint.
        /// </returns>
        private static (ContactEventPublisher publisher, Mock<SearchService> searchServiceMock, Mock<IPublishEndpoint> publishMock)
            CreatePublisher()
        {
            var publishMock = new Mock<IPublishEndpoint>();
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>());
            var logger = NullLogger<ContactEventPublisher>.Instance;
            var publisher = new ContactEventPublisher(publishMock.Object, searchServiceMock.Object, logger);
            return (publisher, searchServiceMock, publishMock);
        }

        /// <summary>
        /// Creates a test <see cref="EntityRecord"/> representing a contact with standard field values.
        /// Uses the Expando-based dynamic property access pattern (<c>record["fieldName"]</c>).
        /// </summary>
        /// <param name="id">Optional record ID; defaults to a new <see cref="Guid"/>.</param>
        /// <returns>A populated EntityRecord suitable for use in domain event payloads.</returns>
        private static EntityRecord CreateContactRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["first_name"] = "John";
            record["last_name"] = "Doe";
            record["email"] = "john.doe@example.com";
            record["city"] = "Plovdiv";
            record["job_title"] = "Engineer";
            record["x_search"] = "";
            return record;
        }

        #region Phase 3: RecordCreatedEvent Tests

        /// <summary>
        /// Test 1: Validates that a <see cref="RecordCreatedEvent"/> for entity "contact"
        /// triggers <see cref="SearchService.RegenSearchField"/> exactly once with
        /// the correct 15-field search index configuration.
        /// Maps to: <c>ContactHook.OnPostCreateRecord</c> calling
        /// <c>new SearchService().RegenSearchField(entityName, record, Configuration.ContactSearchIndexFields)</c>.
        /// </summary>
        [Fact]
        public async Task Contact_Created_Event_Triggers_SearchField_Regeneration()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — RegenSearchField called exactly once with 15 fields
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "contact",
                    It.IsAny<EntityRecord>(),
                    It.Is<List<string>>(fields => fields.Count == 15)),
                Times.Once());
        }

        /// <summary>
        /// Test 2: Validates that the exact <see cref="EntityRecord"/> instance from the
        /// <see cref="RecordCreatedEvent.Record"/> property is passed to
        /// <see cref="SearchService.RegenSearchField"/>.
        /// Ensures no record cloning or transformation occurs during event processing.
        /// </summary>
        [Fact]
        public async Task Contact_Created_Event_Passes_Correct_Record()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — the exact record instance was passed
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "contact",
                    It.Is<EntityRecord>(r => r == record),
                    It.IsAny<List<string>>()),
                Times.Once());
        }

        /// <summary>
        /// Test 3: Validates that a <see cref="RecordCreatedEvent"/> with EntityName "account"
        /// (NOT "contact") is ignored — <see cref="SearchService.RegenSearchField"/> is never called.
        /// Maps to: <c>[HookAttachment("contact", int.MinValue)]</c> entity name filtering.
        /// </summary>
        [Fact]
        public async Task Non_Contact_Created_Event_Is_Ignored()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "account", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — RegenSearchField was NEVER called for non-contact entities
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Never());
        }

        /// <summary>
        /// Test 4: Validates that entity name matching is case-insensitive (OrdinalIgnoreCase).
        /// A <see cref="RecordCreatedEvent"/> with EntityName "Contact" (PascalCase) should
        /// still trigger search field regeneration.
        /// </summary>
        [Fact]
        public async Task Contact_Created_Event_With_CaseInsensitive_EntityName()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "Contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — case-insensitive matching triggers processing
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "contact",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Once());
        }

        #endregion

        #region Phase 4: RecordUpdatedEvent Tests

        /// <summary>
        /// Test 5: Validates that a <see cref="RecordUpdatedEvent"/> for entity "contact"
        /// triggers <see cref="SearchService.RegenSearchField"/> with the NewRecord
        /// (NOT OldRecord), preserving the original <c>ContactHook.OnPostUpdateRecord</c>
        /// behavior which operated on the post-update record state.
        /// </summary>
        [Fact]
        public async Task Contact_Updated_Event_Triggers_SearchField_Regeneration()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var recordId = Guid.NewGuid();
            var oldRecord = CreateContactRecord(recordId);
            var newRecord = CreateContactRecord(recordId);
            newRecord["first_name"] = "Jane";
            var updatedEvent = new RecordUpdatedEvent
            {
                EntityName = "contact",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(updatedEvent);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — called with NewRecord, not OldRecord
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "contact",
                    It.Is<EntityRecord>(r => r == newRecord),
                    It.Is<List<string>>(fields => fields.Count == 15)),
                Times.Once());
        }

        /// <summary>
        /// Test 6: Explicitly confirms that <see cref="RecordUpdatedEvent.NewRecord"/>
        /// (post-update state) is used, not <see cref="RecordUpdatedEvent.OldRecord"/>
        /// (pre-update state). The captured record must have first_name="Jane" (NewRecord),
        /// not first_name="John" (OldRecord).
        /// </summary>
        [Fact]
        public async Task Contact_Updated_Event_Uses_NewRecord_Not_OldRecord()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var recordId = Guid.NewGuid();
            var oldRecord = CreateContactRecord(recordId);
            oldRecord["first_name"] = "John";
            var newRecord = CreateContactRecord(recordId);
            newRecord["first_name"] = "Jane";
            var updatedEvent = new RecordUpdatedEvent
            {
                EntityName = "contact",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(updatedEvent);

            EntityRecord capturedRecord = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((name, rec, fields) => capturedRecord = rec);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — NewRecord (first_name="Jane") was passed, NOT OldRecord
            capturedRecord.Should().NotBeNull();
            capturedRecord.Should().BeSameAs(newRecord);
            ((string)capturedRecord["first_name"]).Should().Be("Jane");
            capturedRecord.Should().NotBeSameAs(oldRecord);
        }

        /// <summary>
        /// Test 7: Validates that a <see cref="RecordUpdatedEvent"/> with EntityName "task"
        /// (NOT "contact") is ignored — <see cref="SearchService.RegenSearchField"/> is never called.
        /// Maps to: <c>[HookAttachment("contact", int.MinValue)]</c> entity name filtering.
        /// </summary>
        [Fact]
        public async Task Non_Contact_Updated_Event_Is_Ignored()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var updatedEvent = new RecordUpdatedEvent
            {
                EntityName = "task",
                OldRecord = CreateContactRecord(),
                NewRecord = CreateContactRecord()
            };
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(updatedEvent);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — RegenSearchField was NEVER called for non-contact entities
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Never());
        }

        #endregion

        #region Phase 5: Search Index Fields Validation Tests

        /// <summary>
        /// Test 8: Validates that exactly 15 search index fields are passed to
        /// <see cref="SearchService.RegenSearchField"/>, and they match the
        /// <see cref="ExpectedSearchIndexFields"/> list character-for-character.
        /// This is the critical assertion for AAP §0.8.1 business rule preservation.
        /// </summary>
        [Fact]
        public async Task SearchIndexFields_Contains_All_15_Expected_Fields()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((name, rec, fields) => capturedFields = fields);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — exactly 15 fields, matching ExpectedSearchIndexFields
            capturedFields.Should().NotBeNull();
            capturedFields.Should().HaveCount(15);
            capturedFields.Should().BeEquivalentTo(ExpectedSearchIndexFields);
        }

        /// <summary>
        /// Test 9: Validates that the two relation-qualified fields are present in the
        /// search index field list:
        /// <list type="bullet">
        ///   <item><c>$country_1n_contact.label</c> — cross-service relation to Core service (AAP §0.7.3)</item>
        ///   <item><c>$account_nn_contact.name</c> — intra-CRM many-to-many relation</item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task SearchIndexFields_Contains_Cross_Service_Relation_Fields()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((name, rec, fields) => capturedFields = fields);

            // Act
            await publisher.Consume(contextMock.Object);

            // Assert — cross-service relation fields are present
            capturedFields.Should().NotBeNull();
            capturedFields.Should().Contain("$country_1n_contact.label",
                "Cross-service relation to Core service for country label (AAP §0.7.3)");
            capturedFields.Should().Contain("$account_nn_contact.name",
                "Intra-CRM many-to-many relation for account name");
        }

        #endregion

        #region Phase 6: Idempotency Tests (AAP §0.8.2)

        /// <summary>
        /// Test 10: Validates that processing the same <see cref="RecordCreatedEvent"/> twice
        /// is safe — <see cref="SearchService.RegenSearchField"/> is called exactly twice,
        /// each time producing the same deterministic result because <c>x_search</c> is
        /// computed from current record state and overwritten atomically.
        /// Maps to: AAP §0.8.2 requirement for idempotent event consumers.
        /// </summary>
        [Fact]
        public async Task Duplicate_Created_Event_Processing_Is_Safe()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            // Act — consume the same event twice (simulating duplicate MassTransit delivery)
            await publisher.Consume(contextMock.Object);
            await publisher.Consume(contextMock.Object);

            // Assert — deterministic, idempotent: same computation produces same result
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "contact",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Exactly(2));
        }

        /// <summary>
        /// Test 11: Validates that processing the same <see cref="RecordUpdatedEvent"/> twice
        /// is safe — idempotent behavior preserved for update events.
        /// </summary>
        [Fact]
        public async Task Duplicate_Updated_Event_Processing_Is_Safe()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var updatedEvent = new RecordUpdatedEvent
            {
                EntityName = "contact",
                OldRecord = CreateContactRecord(),
                NewRecord = CreateContactRecord()
            };
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(updatedEvent);

            // Act — consume the same event twice
            await publisher.Consume(contextMock.Object);
            await publisher.Consume(contextMock.Object);

            // Assert — deterministic, idempotent
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "contact",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Exactly(2));
        }

        #endregion

        #region Phase 7: Error Handling Tests

        /// <summary>
        /// Test 12: Validates that exceptions from <see cref="SearchService.RegenSearchField"/>
        /// are propagated (re-thrown) to allow MassTransit's retry and error queue policies
        /// to handle transient failures automatically. The publisher logs the error with
        /// structured context (record ID) before re-throwing.
        /// </summary>
        [Fact]
        public async Task Exception_In_SearchService_Is_Propagated()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateContactRecord();
            var createdEvent = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(createdEvent);

            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Throws(new Exception("SearchService failure — database unreachable"));

            // Act & Assert — exception must propagate for MassTransit retry/error queue
            Func<Task> act = () => publisher.Consume(contextMock.Object);
            await act.Should().ThrowAsync<Exception>()
                .WithMessage("SearchService failure — database unreachable");
        }

        #endregion

        #region Phase 8: MassTransit Integration Tests

        /// <summary>
        /// Test 13: End-to-end integration test validating that a <see cref="RecordCreatedEvent"/>
        /// published to the MassTransit in-memory bus is correctly routed to and consumed by
        /// <see cref="ContactEventPublisher"/>, triggering search field regeneration.
        /// Uses MassTransit 8.x <c>AddMassTransitTestHarness</c> with DI-resolved consumer.
        /// </summary>
        [Fact]
        public async Task Contact_Created_Event_Consumed_Via_MassTransit_Bus()
        {
            // Arrange — configure DI with MassTransit test harness and mocked SearchService
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>());

            var services = new ServiceCollection();
            services.AddSingleton<SearchService>(searchServiceMock.Object);
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<ContactEventPublisher>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                // Act — publish a contact created event to the bus
                var record = CreateContactRecord();
                await harness.Bus.Publish(new RecordCreatedEvent
                {
                    EntityName = "contact",
                    Record = record
                });

                // Assert — event was consumed by ContactEventPublisher
                var consumerHarness = harness.GetConsumerHarness<ContactEventPublisher>();
                (await consumerHarness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue(
                    "ContactEventPublisher should consume RecordCreatedEvent for contact entity");

                // Verify SearchService was called via the bus-routed consumer
                searchServiceMock.Verify(
                    s => s.RegenSearchField(
                        "contact",
                        It.IsAny<EntityRecord>(),
                        It.IsAny<List<string>>()),
                    Times.Once());
            }
            finally
            {
                await harness.Stop();
            }
        }

        /// <summary>
        /// Test 14: End-to-end integration test validating that a <see cref="RecordUpdatedEvent"/>
        /// published to the MassTransit in-memory bus is correctly routed to and consumed by
        /// <see cref="ContactEventPublisher"/>, triggering search field regeneration with NewRecord.
        /// </summary>
        [Fact]
        public async Task Contact_Updated_Event_Consumed_Via_MassTransit_Bus()
        {
            // Arrange — configure DI with MassTransit test harness and mocked SearchService
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>());

            var services = new ServiceCollection();
            services.AddSingleton<SearchService>(searchServiceMock.Object);
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<ContactEventPublisher>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                // Act — publish a contact updated event to the bus
                var oldRecord = CreateContactRecord();
                var newRecord = CreateContactRecord((Guid)oldRecord["id"]);
                newRecord["first_name"] = "Updated";
                await harness.Bus.Publish(new RecordUpdatedEvent
                {
                    EntityName = "contact",
                    OldRecord = oldRecord,
                    NewRecord = newRecord
                });

                // Assert — event was consumed by ContactEventPublisher
                var consumerHarness = harness.GetConsumerHarness<ContactEventPublisher>();
                (await consumerHarness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue(
                    "ContactEventPublisher should consume RecordUpdatedEvent for contact entity");

                // Verify SearchService was called via the bus-routed consumer
                searchServiceMock.Verify(
                    s => s.RegenSearchField(
                        "contact",
                        It.IsAny<EntityRecord>(),
                        It.IsAny<List<string>>()),
                    Times.Once());
            }
            finally
            {
                await harness.Stop();
            }
        }

        #endregion
    }
}
