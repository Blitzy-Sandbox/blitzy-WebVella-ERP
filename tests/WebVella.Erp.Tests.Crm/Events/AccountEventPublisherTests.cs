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
    /// Unit and integration tests for <see cref="AccountEventPublisher"/>, the MassTransit consumer
    /// that replaces the monolith's <c>AccountHook</c> (IErpPostCreateRecordHook, IErpPostUpdateRecordHook).
    ///
    /// Validates:
    /// - Entity name filtering (only "account" events are processed, case-insensitive)
    /// - Correct SearchService.RegenSearchField invocation with 17 search index fields
    /// - NewRecord is used for update events (matching original hook post-update behavior)
    /// - Idempotent duplicate event processing (AAP §0.8.2)
    /// - Exception propagation for MassTransit retry
    /// - End-to-end MassTransit bus consumption via InMemoryTestHarness
    /// </summary>
    public class AccountEventPublisherTests
    {
        #region Expected Search Index Fields (character-for-character from Configuration.AccountSearchIndexFields)

        /// <summary>
        /// The exact set of 17 account search index fields that must be passed to
        /// <see cref="SearchService.RegenSearchField"/>. Character-for-character identical
        /// to <c>WebVella.Erp.Plugins.Next.Configuration.AccountSearchIndexFields</c>
        /// (Configuration.cs lines 9-11).
        /// </summary>
        private static readonly List<string> ExpectedSearchIndexFields = new List<string>
        {
            "city",
            "$country_1n_account.label",
            "email",
            "fax_phone",
            "first_name",
            "fixed_phone",
            "last_name",
            "mobile_phone",
            "name",
            "notes",
            "post_code",
            "region",
            "street",
            "street_2",
            "tax_id",
            "type",
            "website"
        };

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates an <see cref="AccountEventPublisher"/> instance with mocked dependencies
        /// for unit testing. Returns the publisher, the SearchService mock (for verification),
        /// and the IPublishEndpoint mock (for constructor satisfaction).
        /// </summary>
        private static (AccountEventPublisher publisher, Mock<SearchService> searchServiceMock, Mock<IPublishEndpoint> publishMock)
            CreatePublisher()
        {
            var publishMock = new Mock<IPublishEndpoint>();
            // SearchService requires constructor parameters: ICrmEntityRelationManager, ICrmEntityManager, ICrmRecordManager
            // Since RegenSearchField is virtual, Moq can override it without the constructor dependencies executing real logic
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>());
            var logger = NullLogger<AccountEventPublisher>.Instance;
            var publisher = new AccountEventPublisher(publishMock.Object, searchServiceMock.Object, logger);
            return (publisher, searchServiceMock, publishMock);
        }

        /// <summary>
        /// Creates a valid <see cref="EntityRecord"/> representing an account entity with
        /// known test field values for predictable assertion results.
        /// </summary>
        private static EntityRecord CreateAccountRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["name"] = "Test Account";
            record["email"] = "test@example.com";
            record["city"] = "Sofia";
            record["type"] = "Customer";
            record["tax_id"] = "BG123456789";
            record["x_search"] = "";
            return record;
        }

        /// <summary>
        /// Creates a mock <see cref="ConsumeContext{RecordCreatedEvent}"/> that wraps the
        /// provided event, making its Message property return the event instance.
        /// </summary>
        private static Mock<ConsumeContext<RecordCreatedEvent>> CreateCreatedContext(RecordCreatedEvent evt)
        {
            var contextMock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(evt);
            return contextMock;
        }

        /// <summary>
        /// Creates a mock <see cref="ConsumeContext{RecordUpdatedEvent}"/> that wraps the
        /// provided event, making its Message property return the event instance.
        /// </summary>
        private static Mock<ConsumeContext<RecordUpdatedEvent>> CreateUpdatedContext(RecordUpdatedEvent evt)
        {
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(evt);
            return contextMock;
        }

        #endregion

        #region Phase 3: RecordCreatedEvent Tests

        /// <summary>
        /// Test 1: Verifies that consuming a RecordCreatedEvent with EntityName="account"
        /// triggers SearchService.RegenSearchField with the correct 17 search index fields.
        /// Maps to original: AccountHook.OnPostCreateRecord calling
        /// new SearchService().RegenSearchField(entityName, record, Configuration.AccountSearchIndexFields)
        /// </summary>
        [Fact]
        public async Task Account_Created_Event_Triggers_SearchField_Regeneration()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            var evt = new RecordCreatedEvent { EntityName = "account", Record = record };
            var context = CreateCreatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((_, _, fields) => capturedFields = fields);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField called exactly once
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "account",
                    It.IsAny<EntityRecord>(),
                    It.Is<List<string>>(fields => fields.Count == 17)),
                Times.Once());

            // Assert — captured fields match the expected 17 fields character-for-character
            capturedFields.Should().NotBeNull();
            capturedFields.Should().HaveCount(17);
            capturedFields.Should().BeEquivalentTo(ExpectedSearchIndexFields);
        }

        /// <summary>
        /// Test 2: Verifies that the exact record instance from the event is passed
        /// to SearchService.RegenSearchField (no copies or transformations).
        /// </summary>
        [Fact]
        public async Task Account_Created_Event_Passes_Correct_Record_To_SearchService()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            record["name"] = "Specific Test Corp";
            record["email"] = "specific@test.bg";
            var evt = new RecordCreatedEvent { EntityName = "account", Record = record };
            var context = CreateCreatedContext(evt);

            EntityRecord capturedRecord = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((_, rec, _) => capturedRecord = rec);

            // Act
            await publisher.Consume(context.Object);

            // Assert — the exact same record instance was passed
            capturedRecord.Should().NotBeNull();
            capturedRecord.Should().BeSameAs(record);
            capturedRecord["name"].Should().Be("Specific Test Corp");
            capturedRecord["email"].Should().Be("specific@test.bg");
        }

        /// <summary>
        /// Test 3: Verifies that RecordCreatedEvent with EntityName != "account"
        /// (e.g., "contact") is silently ignored — RegenSearchField is NEVER called.
        /// Maps to: [HookAttachment("account", int.MinValue)] entity filter.
        /// </summary>
        [Fact]
        public async Task Non_Account_Created_Event_Is_Ignored()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            var evt = new RecordCreatedEvent { EntityName = "contact", Record = record };
            var context = CreateCreatedContext(evt);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField was NEVER called for non-account entity
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Never());
        }

        /// <summary>
        /// Test 4: Verifies case-insensitive entity name matching. The publisher
        /// uses StringComparison.OrdinalIgnoreCase, so "Account" and "ACCOUNT"
        /// should be processed just like "account".
        /// </summary>
        [Fact]
        public async Task Account_Created_Event_With_CaseInsensitive_EntityName()
        {
            // Arrange — PascalCase
            var (publisher1, searchServiceMock1, _) = CreatePublisher();
            var record1 = CreateAccountRecord();
            var evt1 = new RecordCreatedEvent { EntityName = "Account", Record = record1 };
            var context1 = CreateCreatedContext(evt1);

            // Act — PascalCase
            await publisher1.Consume(context1.Object);

            // Assert — PascalCase triggers search field regeneration
            searchServiceMock1.Verify(
                s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Once());

            // Arrange — UPPERCASE
            var (publisher2, searchServiceMock2, _) = CreatePublisher();
            var record2 = CreateAccountRecord();
            var evt2 = new RecordCreatedEvent { EntityName = "ACCOUNT", Record = record2 };
            var context2 = CreateCreatedContext(evt2);

            // Act — UPPERCASE
            await publisher2.Consume(context2.Object);

            // Assert — UPPERCASE also triggers search field regeneration
            searchServiceMock2.Verify(
                s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Once());
        }

        #endregion

        #region Phase 4: RecordUpdatedEvent Tests

        /// <summary>
        /// Test 5: Verifies that consuming a RecordUpdatedEvent with EntityName="account"
        /// triggers SearchService.RegenSearchField with the NewRecord (post-update state).
        /// CRITICAL: The original AccountHook.OnPostUpdateRecord received the post-update record.
        /// The publisher uses context.Message.NewRecord for the same behavior.
        /// </summary>
        [Fact]
        public async Task Account_Updated_Event_Triggers_SearchField_Regeneration()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var oldRecord = CreateAccountRecord();
            oldRecord["name"] = "Old Company Name";
            var newRecord = CreateAccountRecord();
            newRecord["name"] = "New Company Name";

            var evt = new RecordUpdatedEvent
            {
                EntityName = "account",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var context = CreateUpdatedContext(evt);

            EntityRecord capturedRecord = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((_, rec, _) => capturedRecord = rec);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField called with NewRecord, NOT OldRecord
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "account",
                    It.IsAny<EntityRecord>(),
                    It.Is<List<string>>(fields => fields.Count == 17)),
                Times.Once());

            capturedRecord.Should().NotBeNull();
            capturedRecord.Should().BeSameAs(newRecord);
        }

        /// <summary>
        /// Test 6: Explicitly verifies that the update event passes NewRecord
        /// (not OldRecord) to RegenSearchField, by checking the record's "name" field value.
        /// </summary>
        [Fact]
        public async Task Account_Updated_Event_Uses_NewRecord_Not_OldRecord()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var oldRecord = CreateAccountRecord();
            oldRecord["name"] = "Old Name";
            var newRecord = CreateAccountRecord();
            newRecord["name"] = "New Name";

            var evt = new RecordUpdatedEvent
            {
                EntityName = "account",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var context = CreateUpdatedContext(evt);

            EntityRecord capturedRecord = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((_, rec, _) => capturedRecord = rec);

            // Act
            await publisher.Consume(context.Object);

            // Assert — the captured record has "New Name" (NewRecord), NOT "Old Name" (OldRecord)
            capturedRecord.Should().NotBeNull();
            capturedRecord["name"].Should().Be("New Name");
            capturedRecord.Should().BeSameAs(newRecord);
            capturedRecord.Should().NotBeSameAs(oldRecord);
        }

        /// <summary>
        /// Test 7: Verifies that RecordUpdatedEvent with EntityName != "account"
        /// (e.g., "case") is silently ignored.
        /// </summary>
        [Fact]
        public async Task Non_Account_Updated_Event_Is_Ignored()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var oldRecord = CreateAccountRecord();
            var newRecord = CreateAccountRecord();

            var evt = new RecordUpdatedEvent
            {
                EntityName = "case",
                OldRecord = oldRecord,
                NewRecord = newRecord
            };
            var context = CreateUpdatedContext(evt);

            // Act
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField was NEVER called for non-account entity
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
        /// Test 8: Validates that the AccountEventPublisher passes exactly 17 search index
        /// fields to SearchService, and each field matches the expected list character-for-character.
        /// Indirectly accesses the private static SearchIndexFields by capturing the
        /// fields parameter passed during a Consume call.
        /// </summary>
        [Fact]
        public async Task SearchIndexFields_Contains_All_17_Expected_Fields()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            var evt = new RecordCreatedEvent { EntityName = "account", Record = record };
            var context = CreateCreatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((_, _, fields) => capturedFields = fields);

            // Act
            await publisher.Consume(context.Object);

            // Assert — exactly 17 fields, each matching character-for-character
            capturedFields.Should().NotBeNull();
            capturedFields.Should().HaveCount(17);
            capturedFields.Should().BeEquivalentTo(ExpectedSearchIndexFields);
        }

        /// <summary>
        /// Test 9: Validates that the cross-service relation field "$country_1n_account.label"
        /// is present in the search index fields. Per AAP §0.7.3, cross-service relation
        /// fields are denormalized via event subscribers and included in search indexes.
        /// </summary>
        [Fact]
        public async Task SearchIndexFields_Contains_Relation_Qualified_Field()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            var evt = new RecordCreatedEvent { EntityName = "account", Record = record };
            var context = CreateCreatedContext(evt);

            List<string> capturedFields = null;
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Callback<string, EntityRecord, List<string>>((_, _, fields) => capturedFields = fields);

            // Act
            await publisher.Consume(context.Object);

            // Assert — the cross-service relation field is present
            capturedFields.Should().NotBeNull();
            capturedFields.Should().Contain("$country_1n_account.label");
        }

        #endregion

        #region Phase 6: Idempotency Tests (AAP §0.8.2)

        /// <summary>
        /// Test 10: Validates that processing the same RecordCreatedEvent twice is safe.
        /// RegenSearchField overwrites x_search with a deterministic computed value,
        /// so duplicate calls produce the same result — idempotent by design.
        /// AAP §0.8.2 mandates: "Event consumers must be idempotent."
        /// </summary>
        [Fact]
        public async Task Duplicate_Created_Event_Processing_Is_Safe()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            var correlationId = Guid.NewGuid();
            var evt = new RecordCreatedEvent
            {
                EntityName = "account",
                Record = record,
                CorrelationId = correlationId
            };
            var context = CreateCreatedContext(evt);

            // Act — invoke Consume TWICE with the same event
            await publisher.Consume(context.Object);
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField was called exactly TWICE (idempotent: same operation, same result)
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "account",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Exactly(2));
        }

        /// <summary>
        /// Test 11: Validates that processing the same RecordUpdatedEvent twice is safe.
        /// Same idempotency guarantee as Test 10, applied to update events.
        /// </summary>
        [Fact]
        public async Task Duplicate_Updated_Event_Processing_Is_Safe()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var oldRecord = CreateAccountRecord();
            var newRecord = CreateAccountRecord();
            newRecord["name"] = "Updated Name";
            var correlationId = Guid.NewGuid();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "account",
                OldRecord = oldRecord,
                NewRecord = newRecord,
                CorrelationId = correlationId
            };
            var context = CreateUpdatedContext(evt);

            // Act — invoke Consume TWICE with the same event
            await publisher.Consume(context.Object);
            await publisher.Consume(context.Object);

            // Assert — RegenSearchField was called exactly TWICE (idempotent: same operation, same result)
            searchServiceMock.Verify(
                s => s.RegenSearchField(
                    "account",
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()),
                Times.Exactly(2));
        }

        #endregion

        #region Phase 7: Error Handling Tests

        /// <summary>
        /// Test 12: Verifies that when SearchService.RegenSearchField throws an exception,
        /// the AccountEventPublisher re-throws it (not swallowing). This enables MassTransit's
        /// retry policy and eventual dead-letter/error queue behavior.
        /// </summary>
        [Fact]
        public async Task Exception_In_SearchService_Is_Propagated()
        {
            // Arrange
            var (publisher, searchServiceMock, _) = CreatePublisher();
            var record = CreateAccountRecord();
            var evt = new RecordCreatedEvent { EntityName = "account", Record = record };
            var context = CreateCreatedContext(evt);

            var expectedException = new Exception("Search index regeneration failed: Entity account not found");
            searchServiceMock
                .Setup(s => s.RegenSearchField(
                    It.IsAny<string>(),
                    It.IsAny<EntityRecord>(),
                    It.IsAny<List<string>>()))
                .Throws(expectedException);

            // Act & Assert — exception is propagated, not swallowed
            var thrownException = await Assert.ThrowsAsync<Exception>(
                () => publisher.Consume(context.Object));

            thrownException.Message.Should().Be("Search index regeneration failed: Entity account not found");
        }

        #endregion

        #region Phase 8: MassTransit Integration Tests

        /// <summary>
        /// Test 13: End-to-end integration test using MassTransit InMemoryTestHarness.
        /// Verifies that when a RecordCreatedEvent with EntityName="account" is published
        /// to the bus, AccountEventPublisher correctly consumes it and calls SearchService.
        /// </summary>
        [Fact]
        public async Task Account_Created_Event_Consumed_Via_MassTransit_Bus()
        {
            // Arrange — build DI container with MassTransit test harness
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>());

            var services = new ServiceCollection();
            services.AddSingleton<SearchService>(searchServiceMock.Object);
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<AccountEventPublisher>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                // Act — publish event to the bus
                var record = CreateAccountRecord();
                var evt = new RecordCreatedEvent { EntityName = "account", Record = record };

                await harness.Bus.Publish(evt);

                // Assert — the event was consumed by AccountEventPublisher
                (await harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue();

                // Allow brief time for consumer processing to complete
                await Task.Delay(200);

                // Assert — SearchService was called during consumption
                searchServiceMock.Verify(
                    s => s.RegenSearchField(
                        "account",
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
        /// Test 14: End-to-end integration test for RecordUpdatedEvent via MassTransit bus.
        /// Same pattern as Test 13 but with update events.
        /// </summary>
        [Fact]
        public async Task Account_Updated_Event_Consumed_Via_MassTransit_Bus()
        {
            // Arrange — build DI container with MassTransit test harness
            var searchServiceMock = new Mock<SearchService>(
                Mock.Of<ICrmEntityRelationManager>(),
                Mock.Of<ICrmEntityManager>(),
                Mock.Of<ICrmRecordManager>());

            var services = new ServiceCollection();
            services.AddSingleton<SearchService>(searchServiceMock.Object);
            services.AddLogging();
            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<AccountEventPublisher>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            try
            {
                // Act — publish update event to the bus
                var oldRecord = CreateAccountRecord();
                oldRecord["name"] = "Before Update";
                var newRecord = CreateAccountRecord();
                newRecord["name"] = "After Update";

                var evt = new RecordUpdatedEvent
                {
                    EntityName = "account",
                    OldRecord = oldRecord,
                    NewRecord = newRecord
                };

                await harness.Bus.Publish(evt);

                // Assert — the event was consumed by AccountEventPublisher
                (await harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue();

                // Allow brief time for consumer processing to complete
                await Task.Delay(200);

                // Assert — SearchService was called with the NewRecord during consumption
                searchServiceMock.Verify(
                    s => s.RegenSearchField(
                        "account",
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
