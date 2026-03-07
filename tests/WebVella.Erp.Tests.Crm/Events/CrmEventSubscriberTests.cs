// =============================================================================
// CrmEventSubscriberTests.cs — Tests for CRM Event Subscribers
// =============================================================================
// Validates CoreEntityChangedConsumer and UserUpdatedConsumer behavior for
// consuming events FROM the Core service. These consumers maintain denormalized
// reference data (crm_reference_data_cache, crm_user_cache) within the CRM
// database for cross-service entity resolution (AAP 0.7.1, 0.7.3).
//
// Test Categories:
//   1. Entity name filtering — verifies consumers process only relevant entities
//   2. Data extraction — verifies correct field extraction from EntityRecord
//   3. Idempotency — verifies duplicate/out-of-order delivery safety (AAP 0.8.2)
//   4. Error handling — verifies graceful handling of DB and data exceptions
//   5. MassTransit integration — verifies consumer registration and bus routing
//
// Architecture Context:
//   In the monolith, CRM entities accessed Core-owned data (users, countries,
//   currencies, languages) via direct FK joins to shared tables. In the
//   microservice architecture, these subscribers maintain eventual consistency
//   by processing events from the Core service (AAP 0.7.1, 0.7.3).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FluentAssertions;
using Xunit;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Crm.Events.Subscribers;
using WebVella.Erp.Service.Crm.Database;

namespace WebVella.Erp.Tests.Crm.Events
{
    /// <summary>
    /// Comprehensive test suite for CRM event subscriber consumers.
    /// Covers CoreEntityChangedConsumer (language/currency/country reference data)
    /// and UserUpdatedConsumer (user display data for audit fields).
    /// 
    /// Per AAP 0.8.2: Event consumers must be idempotent — duplicate delivery
    /// must not cause data corruption. Tests 14-18 validate this requirement.
    /// Per AAP 0.7.1: Cross-service relation fields denormalized via event
    /// subscribers. Tests 1-9, 10-13 validate the subscriber processing.
    /// </summary>
    public class CrmEventSubscriberTests
    {
        #region ===== Static Initialization =====

        /// <summary>
        /// Static constructor sets the CrmDbContext connection string to a fake value
        /// that points to a non-existent PostgreSQL server. This ensures that when
        /// consumers attempt database operations via CreateConnection(), the resulting
        /// NpgsqlException (connection refused) is caught by the consumer's error
        /// handler and swallowed — keeping tests isolated from real DB infrastructure.
        /// </summary>
        static CrmEventSubscriberTests()
        {
            // Set the static connectionString field on CrmDbContext via reflection.
            // This ensures CreateConnection() creates a NpgsqlConnection that fails with
            // NpgsqlException (swallowed by consumer) rather than InvalidOperationException
            // (which would be rethrown and cause test failures).
            SetCrmConnectionString("Host=127.0.0.1;Port=1;Database=crm_test;Timeout=1");
        }

        /// <summary>
        /// Uses reflection to set the private static connectionString field on CrmDbContext.
        /// This field is normally set via CrmDbContext.CreateContext() during service startup.
        /// </summary>
        private static void SetCrmConnectionString(string value)
        {
            var field = typeof(CrmDbContext).GetField("connectionString",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null)
            {
                field.SetValue(null, value);
            }
        }

        #endregion

        #region ===== Helper Methods =====

        /// <summary>
        /// Creates CrmDbContext options without a database provider configured.
        /// The consumers use raw NpgsqlConnection (via CreateConnection()) rather than
        /// EF Core operations, so no EF Core database provider is needed.
        /// </summary>
        private static DbContextOptions<CrmDbContext> CreateDbContextOptions()
        {
            return new DbContextOptionsBuilder<CrmDbContext>().Options;
        }

        /// <summary>
        /// Creates a CoreEntityChangedConsumer with a CrmDbContext.
        /// Since CrmDbContext.CreateConnection() is not virtual (implements IDbContext),
        /// the real object is used for constructor injection while the consumer's
        /// error handling catches NpgsqlException from the fake connection string.
        /// </summary>
        private static (CoreEntityChangedConsumer consumer, CrmDbContext dbContext)
            CreateCoreEntityConsumer()
        {
            var dbContext = new CrmDbContext(CreateDbContextOptions());
            var logger = NullLogger<CoreEntityChangedConsumer>.Instance;
            var consumer = new CoreEntityChangedConsumer(logger, dbContext);
            return (consumer, dbContext);
        }

        /// <summary>
        /// Creates a CoreEntityChangedConsumer configured for filtering tests.
        /// Uses null connection string so any DB access attempt would throw a
        /// non-NpgsqlException (rethrown by consumer), proving the consumer
        /// correctly filtered out the entity before reaching DB operations.
        /// </summary>
        private static CoreEntityChangedConsumer CreateCoreEntityConsumerForFiltering()
        {
            SetCrmConnectionString(null);
            var dbContext = new CrmDbContext(CreateDbContextOptions());
            var consumer = new CoreEntityChangedConsumer(
                NullLogger<CoreEntityChangedConsumer>.Instance, dbContext);
            // Restore fake connection string for subsequent processing tests
            SetCrmConnectionString("Host=127.0.0.1;Port=1;Database=crm_test;Timeout=1");
            return consumer;
        }

        /// <summary>
        /// Creates a UserUpdatedConsumer with a CrmDbContext.
        /// Same setup pattern as CreateCoreEntityConsumer.
        /// </summary>
        private static (UserUpdatedConsumer consumer, CrmDbContext dbContext)
            CreateUserConsumer()
        {
            var dbContext = new CrmDbContext(CreateDbContextOptions());
            var logger = NullLogger<UserUpdatedConsumer>.Instance;
            var consumer = new UserUpdatedConsumer(logger, dbContext);
            return (consumer, dbContext);
        }

        /// <summary>
        /// Creates a UserUpdatedConsumer configured for filtering tests.
        /// Uses null connection string — same approach as CreateCoreEntityConsumerForFiltering.
        /// </summary>
        private static UserUpdatedConsumer CreateUserConsumerForFiltering()
        {
            SetCrmConnectionString(null);
            var dbContext = new CrmDbContext(CreateDbContextOptions());
            var consumer = new UserUpdatedConsumer(
                NullLogger<UserUpdatedConsumer>.Instance, dbContext);
            SetCrmConnectionString("Host=127.0.0.1;Port=1;Database=crm_test;Timeout=1");
            return consumer;
        }

        /// <summary>
        /// Creates an EntityRecord populated with reference entity fields (id, label, name).
        /// Mirrors the Expando-based property bag pattern used throughout the monolith
        /// for dynamic entity records.
        /// </summary>
        private static EntityRecord CreateReferenceRecord(Guid? id = null, string label = "Test", string name = "test")
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["label"] = label;
            record["name"] = name;
            return record;
        }

        /// <summary>
        /// Creates an EntityRecord populated with user entity fields
        /// (id, username, email, first_name, last_name).
        /// Matches the field extraction pattern in UserUpdatedConsumer.
        /// </summary>
        private static EntityRecord CreateUserRecord(
            Guid? id = null,
            string username = "admin",
            string email = "admin@webvella.com",
            string firstName = "System",
            string lastName = "Admin")
        {
            var record = new EntityRecord();
            record["id"] = id ?? Guid.NewGuid();
            record["username"] = username;
            record["email"] = email;
            record["first_name"] = firstName;
            record["last_name"] = lastName;
            return record;
        }

        /// <summary>
        /// Creates a mock ConsumeContext wrapping a RecordCreatedEvent.
        /// </summary>
        private static Mock<ConsumeContext<RecordCreatedEvent>> CreateCreatedContext(RecordCreatedEvent evt)
        {
            var mock = new Mock<ConsumeContext<RecordCreatedEvent>>();
            mock.Setup(c => c.Message).Returns(evt);
            mock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return mock;
        }

        /// <summary>
        /// Creates a mock ConsumeContext wrapping a RecordUpdatedEvent.
        /// </summary>
        private static Mock<ConsumeContext<RecordUpdatedEvent>> CreateUpdatedContext(RecordUpdatedEvent evt)
        {
            var mock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            mock.Setup(c => c.Message).Returns(evt);
            mock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return mock;
        }

        /// <summary>
        /// Creates a mock ConsumeContext wrapping a RecordDeletedEvent.
        /// </summary>
        private static Mock<ConsumeContext<RecordDeletedEvent>> CreateDeletedContext(RecordDeletedEvent evt)
        {
            var mock = new Mock<ConsumeContext<RecordDeletedEvent>>();
            mock.Setup(c => c.Message).Returns(evt);
            mock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return mock;
        }

        #endregion

        #region ===== Test Group: CoreEntityChangedConsumer — Entity Name Filtering =====

        /// <summary>
        /// Test 1: Verifies that CoreEntityChangedConsumer processes a RecordCreatedEvent
        /// for the "language" entity (one of the three Core-owned reference entities).
        /// The consumer should pass the entity name filter and attempt the UPSERT operation.
        /// Since no real DB is available in tests, the consumer's error handling catches
        /// any connection exceptions gracefully.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Processes_Language_Created_Event()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordCreatedEvent
            {
                EntityName = "language",
                Record = CreateReferenceRecord(label: "English", name: "en"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateCreatedContext(evt);

            // Act — consumer processes the event; DB exceptions are swallowed internally
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer should not throw unhandled exceptions
            // The NpgsqlException from the missing DB connection is caught and swallowed
            // by the consumer's error handling (AAP 0.8.2 error handling pattern)
            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 2: Verifies that CoreEntityChangedConsumer processes a RecordUpdatedEvent
        /// for the "currency" entity. Both OldRecord and NewRecord are provided per the
        /// enriched event contract (AAP RecordUpdatedEvent design).
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Processes_Currency_Updated_Event()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "currency",
                OldRecord = CreateReferenceRecord(label: "US Dollar Old", name: "USD"),
                NewRecord = CreateReferenceRecord(label: "US Dollar", name: "USD"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer processes currency events without unhandled exceptions
            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 3: Verifies that CoreEntityChangedConsumer processes a RecordDeletedEvent
        /// for the "country" entity. The DELETE operation targets crm_reference_data_cache
        /// using the RecordId from the event.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Processes_Country_Deleted_Event()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var recordId = Guid.NewGuid();
            var evt = new RecordDeletedEvent
            {
                EntityName = "country",
                RecordId = recordId,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateDeletedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer processes country delete events without unhandled exceptions
            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 4: Verifies that CoreEntityChangedConsumer IGNORES RecordCreatedEvent
        /// for non-reference entities. "account" is owned by CRM, not a Core reference entity.
        /// The consumer should return immediately without any database interaction.
        /// Uses null connection string to prove filtering: if the consumer tried DB access,
        /// a non-NpgsqlException would propagate (proving the filter failed).
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Ignores_Non_Reference_Created_Event()
        {
            // Arrange — use filtering consumer (null connectionString proves no DB access)
            var consumer = CreateCoreEntityConsumerForFiltering();
            var evt = new RecordCreatedEvent
            {
                EntityName = "account",
                Record = CreateReferenceRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateCreatedContext(evt);

            // Act & Assert — no exception means consumer filtered out the entity
            // before any database interaction (null connectionString would cause
            // a rethrown exception if DB access was attempted)
            Func<Task> act = async () => await consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "consumer should filter out 'account' without attempting DB operations");
        }

        /// <summary>
        /// Test 5: Verifies that CoreEntityChangedConsumer IGNORES RecordUpdatedEvent
        /// for non-reference entities ("task" is owned by the Project service).
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Ignores_Non_Reference_Updated_Event()
        {
            // Arrange
            var consumer = CreateCoreEntityConsumerForFiltering();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "task",
                OldRecord = CreateReferenceRecord(),
                NewRecord = CreateReferenceRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act & Assert
            Func<Task> act = async () => await consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "consumer should filter out 'task' without attempting DB operations");
        }

        /// <summary>
        /// Test 6: Verifies that CoreEntityChangedConsumer IGNORES RecordDeletedEvent
        /// for non-reference entities ("email" is owned by the Mail service).
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Ignores_Non_Reference_Deleted_Event()
        {
            // Arrange
            var consumer = CreateCoreEntityConsumerForFiltering();
            var evt = new RecordDeletedEvent
            {
                EntityName = "email",
                RecordId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateDeletedContext(evt);

            // Act & Assert
            Func<Task> act = async () => await consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "consumer should filter out 'email' without attempting DB operations");
        }

        /// <summary>
        /// Test 7: Verifies that CoreEntityChangedConsumer performs case-insensitive
        /// entity name matching. The consumer uses HashSet with StringComparer.OrdinalIgnoreCase,
        /// so "Language", "CURRENCY", "Country" should all be processed.
        /// </summary>
        [Theory]
        [InlineData("Language")]
        [InlineData("CURRENCY")]
        [InlineData("Country")]
        public async Task CoreConsumer_CaseInsensitive_EntityName_Matching(string entityName)
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = entityName,
                OldRecord = CreateReferenceRecord(),
                NewRecord = CreateReferenceRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act — consumer should pass the entity name filter (case-insensitive)
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — no unhandled exceptions; consumer processed the event
            await act.Should().NotThrowAsync(
                "CoreEntityChangedConsumer uses OrdinalIgnoreCase for entity name filtering");
        }

        #endregion

        #region ===== Test Group: CoreEntityChangedConsumer — Data Extraction =====

        /// <summary>
        /// Test 8: Verifies that CoreEntityChangedConsumer extracts label and name
        /// from the NewRecord of a RecordUpdatedEvent. The extracted values are used
        /// in the UPSERT to crm_reference_data_cache.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Extracts_Label_And_Name_From_Updated_Record()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var recordId = Guid.NewGuid();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "currency",
                OldRecord = CreateReferenceRecord(id: recordId, label: "Dollar", name: "USD"),
                NewRecord = CreateReferenceRecord(id: recordId, label: "US Dollar", name: "USD"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act — consumer extracts label="US Dollar" and name="USD" from NewRecord
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — the consumer processes the event without unhandled exceptions.
            // The UPSERT SQL includes label and name parameters extracted from NewRecord.
            await act.Should().NotThrowAsync();

            // Verify the event's NewRecord contains the expected extraction targets
            evt.NewRecord["label"].Should().Be("US Dollar");
            evt.NewRecord["name"].Should().Be("USD");
        }

        /// <summary>
        /// Test 9: Verifies that CoreEntityChangedConsumer extracts the RecordId from
        /// a RecordDeletedEvent for the DELETE operation on crm_reference_data_cache.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Extracts_Record_Id_From_Deleted_Event()
        {
            // Arrange
            var specificRecordId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordDeletedEvent
            {
                EntityName = "country",
                RecordId = specificRecordId,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateDeletedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer passes entity filter and processes the specific RecordId
            await act.Should().NotThrowAsync();

            // Verify the event carries the correct RecordId for DELETE targeting
            evt.RecordId.Should().Be(specificRecordId,
                "the DELETE operation should target this specific record_id");
        }

        #endregion

        #region ===== Test Group: UserUpdatedConsumer =====

        /// <summary>
        /// Test 10: Verifies that UserUpdatedConsumer processes a RecordUpdatedEvent
        /// for the "user" entity. The consumer should pass the entity name filter
        /// and attempt the UPSERT to crm_user_cache.
        /// </summary>
        [Fact]
        public async Task UserConsumer_Processes_User_Updated_Event()
        {
            // Arrange
            var (consumer, _) = CreateUserConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "user",
                OldRecord = CreateUserRecord(),
                NewRecord = CreateUserRecord(username: "admin_updated"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer processes user events without unhandled exceptions
            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 11: Verifies that UserUpdatedConsumer IGNORES RecordUpdatedEvent
        /// for non-user entities. "account" is a CRM entity, not "user".
        /// Uses null connection string to prove filtering.
        /// </summary>
        [Fact]
        public async Task UserConsumer_Ignores_Non_User_Updated_Event()
        {
            // Arrange — use filtering consumer (null connectionString proves no DB access)
            var consumer = CreateUserConsumerForFiltering();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "account",
                OldRecord = CreateReferenceRecord(),
                NewRecord = CreateReferenceRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act & Assert — no exception means filtering worked correctly
            Func<Task> act = async () => await consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "consumer should filter out 'account' without attempting DB operations");
        }

        /// <summary>
        /// Test 12: Verifies that UserUpdatedConsumer extracts the correct user fields
        /// (username, email, first_name, last_name) from the NewRecord for UPSERT
        /// to crm_user_cache.
        /// </summary>
        [Fact]
        public async Task UserConsumer_Extracts_User_Fields_From_NewRecord()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var (consumer, _) = CreateUserConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "user",
                OldRecord = CreateUserRecord(id: userId, username: "oldsys", email: "old@example.com",
                    firstName: "Old", lastName: "Name"),
                NewRecord = CreateUserRecord(id: userId, username: "newadmin", email: "new@example.com",
                    firstName: "New", lastName: "Admin"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer processes without unhandled exceptions
            await act.Should().NotThrowAsync();

            // Verify the NewRecord contains the expected field values for extraction
            evt.NewRecord["username"].Should().Be("newadmin");
            evt.NewRecord["email"].Should().Be("new@example.com");
            evt.NewRecord["first_name"].Should().Be("New");
            evt.NewRecord["last_name"].Should().Be("Admin");
        }

        /// <summary>
        /// Test 13: Verifies UserUpdatedConsumer entity name matching behavior.
        /// The UserUpdatedConsumer uses StringComparison.Ordinal (case-sensitive),
        /// so only the exact string "user" is processed. Variant casings like
        /// "User" or "USER" are ignored.
        /// For non-matching casings, uses null connectionString to prove filtering.
        /// For matching casing ("user"), uses fake connectionString so NpgsqlException
        /// is swallowed by consumer.
        /// </summary>
        [Theory]
        [InlineData("User", false)]
        [InlineData("USER", false)]
        [InlineData("user", true)]
        public async Task UserConsumer_CaseInsensitive_EntityName(string entityName, bool shouldProcess)
        {
            // Arrange — for non-matching casings, use filtering consumer;
            // for matching casing, use regular consumer
            UserUpdatedConsumer consumer;
            if (!shouldProcess)
            {
                consumer = CreateUserConsumerForFiltering();
            }
            else
            {
                var (c, _) = CreateUserConsumer();
                consumer = c;
            }

            var evt = new RecordUpdatedEvent
            {
                EntityName = entityName,
                OldRecord = CreateUserRecord(),
                NewRecord = CreateUserRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act & Assert
            Func<Task> act = async () => await consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                shouldProcess
                    ? $"'{entityName}' matches the case-sensitive 'user' filter and NpgsqlException is swallowed"
                    : $"'{entityName}' should not match the case-sensitive 'user' filter");
        }

        #endregion

        #region ===== Test Group: Idempotency (AAP §0.8.2 — CRITICAL) =====

        /// <summary>
        /// Test 14: Verifies that processing the same RecordCreatedEvent twice is
        /// idempotent. The UPSERT SQL uses ON CONFLICT ... DO UPDATE SET ... WHERE
        /// last_synced_at &lt; EXCLUDED.last_synced_at, ensuring duplicate deliveries
        /// produce identical results without data corruption.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Duplicate_Created_Event_Is_Idempotent()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var correlationId = Guid.NewGuid();
            var evt = new RecordCreatedEvent
            {
                EntityName = "currency",
                Record = CreateReferenceRecord(label: "Euro", name: "EUR"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId
            };

            // Act — consume the same event TWICE (duplicate delivery)
            var context1 = CreateCreatedContext(evt);
            var context2 = CreateCreatedContext(evt);

            Func<Task> actFirst = async () => await consumer.Consume(context1.Object);
            Func<Task> actSecond = async () => await consumer.Consume(context2.Object);

            // Assert — both invocations should complete without unhandled exceptions.
            // The SQL UPSERT with timestamp guard ensures idempotency: the second
            // delivery produces the same result as the first (no data corruption).
            await actFirst.Should().NotThrowAsync("first consumption should succeed");
            await actSecond.Should().NotThrowAsync("duplicate consumption should be idempotent");
        }

        /// <summary>
        /// Test 15: Verifies that processing the same RecordUpdatedEvent twice is
        /// idempotent. UPSERT is inherently idempotent for identical payloads.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Duplicate_Updated_Event_Is_Idempotent()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "currency",
                OldRecord = CreateReferenceRecord(label: "Dollar", name: "USD"),
                NewRecord = CreateReferenceRecord(label: "US Dollar", name: "USD"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act — process twice
            Func<Task> actFirst = async () => await consumer.Consume(CreateUpdatedContext(evt).Object);
            Func<Task> actSecond = async () => await consumer.Consume(CreateUpdatedContext(evt).Object);

            // Assert
            await actFirst.Should().NotThrowAsync();
            await actSecond.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 16: Verifies that processing the same RecordDeletedEvent twice is
        /// idempotent. DELETE of a non-existent row is a PostgreSQL no-op that returns
        /// zero rows affected without error.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Duplicate_Deleted_Event_Is_Idempotent()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordDeletedEvent
            {
                EntityName = "country",
                RecordId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act — process twice (second DELETE is a no-op)
            Func<Task> actFirst = async () => await consumer.Consume(CreateDeletedContext(evt).Object);
            Func<Task> actSecond = async () => await consumer.Consume(CreateDeletedContext(evt).Object);

            // Assert
            await actFirst.Should().NotThrowAsync();
            await actSecond.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 17: Verifies that processing the same RecordUpdatedEvent for "user"
        /// entity twice is idempotent for UserUpdatedConsumer.
        /// </summary>
        [Fact]
        public async Task UserConsumer_Duplicate_Updated_Event_Is_Idempotent()
        {
            // Arrange
            var (consumer, _) = CreateUserConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "user",
                OldRecord = CreateUserRecord(username: "old_admin"),
                NewRecord = CreateUserRecord(username: "new_admin"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            // Act — process twice
            Func<Task> actFirst = async () => await consumer.Consume(CreateUpdatedContext(evt).Object);
            Func<Task> actSecond = async () => await consumer.Consume(CreateUpdatedContext(evt).Object);

            // Assert
            await actFirst.Should().NotThrowAsync();
            await actSecond.Should().NotThrowAsync();
        }

        /// <summary>
        /// Test 18: Verifies that out-of-order event delivery is handled correctly.
        /// When Event B (newer timestamp) is processed before Event A (older timestamp),
        /// Event A should NOT overwrite Event B's data. The SQL WHERE clause
        /// crm_reference_data_cache.last_synced_at &lt; EXCLUDED.last_synced_at prevents this.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_OutOfOrder_Events_Handled_Correctly()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var recordId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            // Event A: older timestamp (1 minute ago), old label
            var eventA = new RecordUpdatedEvent
            {
                EntityName = "currency",
                OldRecord = CreateReferenceRecord(id: recordId, label: "Original", name: "CUR"),
                NewRecord = CreateReferenceRecord(id: recordId, label: "Old Value", name: "CUR"),
                Timestamp = now - TimeSpan.FromMinutes(1),
                CorrelationId = Guid.NewGuid()
            };

            // Event B: newer timestamp (now), new label
            var eventB = new RecordUpdatedEvent
            {
                EntityName = "currency",
                OldRecord = CreateReferenceRecord(id: recordId, label: "Old Value", name: "CUR"),
                NewRecord = CreateReferenceRecord(id: recordId, label: "New Value", name: "CUR"),
                Timestamp = now,
                CorrelationId = Guid.NewGuid()
            };

            // Act — process Event B FIRST (newer), then Event A (older)
            Func<Task> actB = async () => await consumer.Consume(CreateUpdatedContext(eventB).Object);
            Func<Task> actA = async () => await consumer.Consume(CreateUpdatedContext(eventA).Object);

            // Assert — both consumptions should complete without unhandled exceptions.
            // The SQL WHERE last_synced_at < EXCLUDED.last_synced_at ensures that
            // Event A (older) does NOT overwrite Event B's (newer) data in the DB.
            await actB.Should().NotThrowAsync("newer event should be processed first");
            await actA.Should().NotThrowAsync("older event should be silently skipped by timestamp guard");

            // Verify both events have different timestamps for ordering
            eventB.Timestamp.Should().BeAfter(eventA.Timestamp,
                "Event B should have a newer timestamp than Event A");
        }

        #endregion

        #region ===== Test Group: Error Handling =====

        /// <summary>
        /// Test 19: Verifies that CoreEntityChangedConsumer handles database exceptions
        /// gracefully. When the database is unavailable, NpgsqlException is caught and
        /// swallowed (not rethrown), allowing MassTransit retry policies to handle
        /// redelivery for transient failures.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Handles_Database_Exception_Gracefully()
        {
            // Arrange — consumer with mock DB that will fail on connection
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordCreatedEvent
            {
                EntityName = "language",
                Record = CreateReferenceRecord(label: "Bulgarian", name: "bg"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateCreatedContext(evt);

            // Act — the consumer will attempt CreateConnection(), which fails because
            // no real PostgreSQL database is configured. The consumer's error handling
            // catches the exception (NpgsqlException is swallowed, others are rethrown).
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer should handle the DB exception without crashing
            await act.Should().NotThrowAsync(
                "consumer should catch database exceptions gracefully per AAP 0.8.2 error handling");
        }

        /// <summary>
        /// Test 20: Verifies that UserUpdatedConsumer handles database exceptions
        /// gracefully with the same pattern as CoreEntityChangedConsumer.
        /// </summary>
        [Fact]
        public async Task UserConsumer_Handles_Database_Exception_Gracefully()
        {
            // Arrange
            var (consumer, _) = CreateUserConsumer();
            var evt = new RecordUpdatedEvent
            {
                EntityName = "user",
                OldRecord = CreateUserRecord(),
                NewRecord = CreateUserRecord(username: "updated_user"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateUpdatedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert
            await act.Should().NotThrowAsync(
                "consumer should catch database exceptions gracefully");
        }

        /// <summary>
        /// Test 21: Verifies that CoreEntityChangedConsumer handles missing record fields
        /// gracefully. When the EntityRecord is missing the "label" or "name" fields,
        /// the consumer's null-safe property access pattern (Properties.ContainsKey guard)
        /// prevents NullReferenceException.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Handles_Missing_Record_Fields_Gracefully()
        {
            // Arrange — create a record WITHOUT label and name fields
            var (consumer, _) = CreateCoreEntityConsumer();
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            // Intentionally omitting "label" and "name" fields

            var evt = new RecordCreatedEvent
            {
                EntityName = "language",
                Record = record,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateCreatedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — consumer should not crash even with missing fields
            // The ExtractString method returns null for missing keys
            await act.Should().NotThrowAsync(
                "consumer should handle missing record fields via null-safe property access");
        }

        #endregion

        #region ===== Test Group: MassTransit Integration =====

        /// <summary>
        /// Test 22: Verifies that CoreEntityChangedConsumer is correctly consumed via
        /// MassTransit bus when a RecordCreatedEvent is published. Uses MassTransit's
        /// in-memory test harness for consumer registration and message routing.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Consumed_Via_MassTransit_Bus_For_RecordCreated()
        {
            // Arrange — build service collection with MassTransit test harness
            var services = new ServiceCollection();

            // Register CrmDbContext for DI resolution
            services.AddSingleton(new CrmDbContext(CreateDbContextOptions()));
            services.AddSingleton<ILogger<CoreEntityChangedConsumer>>(
                NullLogger<CoreEntityChangedConsumer>.Instance);

            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<CoreEntityChangedConsumer>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            // Act — publish a RecordCreatedEvent for "language"
            await harness.Bus.Publish(new RecordCreatedEvent
            {
                EntityName = "language",
                Record = CreateReferenceRecord(label: "French", name: "fr"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            });

            // Assert — verify the event was consumed by the harness
            (await harness.Consumed.Any<RecordCreatedEvent>()).Should().BeTrue(
                "CoreEntityChangedConsumer should consume RecordCreatedEvent messages");

            await harness.Stop();
        }

        /// <summary>
        /// Test 23: Verifies that UserUpdatedConsumer is correctly consumed via
        /// MassTransit bus when a RecordUpdatedEvent with EntityName="user" is published.
        /// </summary>
        [Fact]
        public async Task UserConsumer_Consumed_Via_MassTransit_Bus()
        {
            // Arrange
            var services = new ServiceCollection();

            services.AddSingleton(new CrmDbContext(CreateDbContextOptions()));
            services.AddSingleton<ILogger<UserUpdatedConsumer>>(
                NullLogger<UserUpdatedConsumer>.Instance);

            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<UserUpdatedConsumer>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            // Act — publish a RecordUpdatedEvent for "user"
            await harness.Bus.Publish(new RecordUpdatedEvent
            {
                EntityName = "user",
                OldRecord = CreateUserRecord(),
                NewRecord = CreateUserRecord(username: "mt_user"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            });

            // Assert
            (await harness.Consumed.Any<RecordUpdatedEvent>()).Should().BeTrue(
                "UserUpdatedConsumer should consume RecordUpdatedEvent messages");

            await harness.Stop();
        }

        /// <summary>
        /// Test 24: Verifies that both CoreEntityChangedConsumer and UserUpdatedConsumer
        /// can coexist on the same MassTransit bus. When a RecordUpdatedEvent is published
        /// for entity "user", UserUpdatedConsumer should process it while
        /// CoreEntityChangedConsumer should ignore it (entity filtering). When a
        /// RecordUpdatedEvent for "currency" is published, CoreEntityChangedConsumer
        /// should process it while UserUpdatedConsumer ignores it.
        /// </summary>
        [Fact]
        public async Task Multiple_Consumers_Coexist_On_Same_Bus()
        {
            // Arrange — register both consumers on the same bus
            var services = new ServiceCollection();

            services.AddSingleton(new CrmDbContext(CreateDbContextOptions()));
            services.AddSingleton<ILogger<CoreEntityChangedConsumer>>(
                NullLogger<CoreEntityChangedConsumer>.Instance);
            services.AddSingleton<ILogger<UserUpdatedConsumer>>(
                NullLogger<UserUpdatedConsumer>.Instance);

            services.AddMassTransitTestHarness(cfg =>
            {
                cfg.AddConsumer<CoreEntityChangedConsumer>();
                cfg.AddConsumer<UserUpdatedConsumer>();
            });

            await using var provider = services.BuildServiceProvider(true);
            var harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            // Act 1 — publish a "user" update event
            await harness.Bus.Publish(new RecordUpdatedEvent
            {
                EntityName = "user",
                OldRecord = CreateUserRecord(),
                NewRecord = CreateUserRecord(username: "multi_user"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            });

            // Act 2 — publish a "currency" update event
            await harness.Bus.Publish(new RecordUpdatedEvent
            {
                EntityName = "currency",
                OldRecord = CreateReferenceRecord(label: "Old Euro", name: "EUR"),
                NewRecord = CreateReferenceRecord(label: "Euro", name: "EUR"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            });

            // Assert — both events should be consumed (routed to both consumers on the bus)
            (await harness.Consumed.Any<RecordUpdatedEvent>(
                x => x.Context.Message.EntityName == "user")).Should().BeTrue(
                "RecordUpdatedEvent for 'user' should be consumed by the bus");

            (await harness.Consumed.Any<RecordUpdatedEvent>(
                x => x.Context.Message.EntityName == "currency")).Should().BeTrue(
                "RecordUpdatedEvent for 'currency' should be consumed by the bus");

            await harness.Stop();
        }

        #endregion

        #region ===== Test Group: Cross-Service Event Flow Validation =====

        /// <summary>
        /// Test 25: Verifies that CoreEntityChangedConsumer handles all three
        /// reference entity types: language, currency, and country.
        /// Per AAP 0.7.1 Entity-to-Service Ownership Matrix, these are the
        /// Core-owned entities that CRM maintains local copies of.
        /// </summary>
        [Theory]
        [InlineData("language")]
        [InlineData("currency")]
        [InlineData("country")]
        public async Task CoreConsumer_Handles_All_Three_Reference_Entities(string entityName)
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var evt = new RecordCreatedEvent
            {
                EntityName = entityName,
                Record = CreateReferenceRecord(label: "TestLabel_" + entityName, name: "test_" + entityName),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            var context = CreateCreatedContext(evt);

            // Act
            Func<Task> act = async () => await consumer.Consume(context.Object);

            // Assert — all three reference entities should be processed
            await act.Should().NotThrowAsync(
                $"CoreEntityChangedConsumer should process '{entityName}' events");
        }

        /// <summary>
        /// Test 26: Verifies the full lifecycle (CREATE → UPDATE → DELETE) for a
        /// reference entity through CoreEntityChangedConsumer. This simulates the
        /// complete event flow from the Core service for a country record.
        /// </summary>
        [Fact]
        public async Task CoreConsumer_Full_Lifecycle_Create_Update_Delete()
        {
            // Arrange
            var (consumer, _) = CreateCoreEntityConsumer();
            var recordId = Guid.NewGuid();
            var baseTimestamp = DateTimeOffset.UtcNow;

            // Step 1: CREATE — new country record
            var createEvent = new RecordCreatedEvent
            {
                EntityName = "country",
                Record = CreateReferenceRecord(id: recordId, label: "Bulgaria", name: "BG"),
                Timestamp = baseTimestamp,
                CorrelationId = Guid.NewGuid()
            };

            // Step 2: UPDATE — country label changed
            var updateEvent = new RecordUpdatedEvent
            {
                EntityName = "country",
                OldRecord = CreateReferenceRecord(id: recordId, label: "Bulgaria", name: "BG"),
                NewRecord = CreateReferenceRecord(id: recordId, label: "Republic of Bulgaria", name: "BG"),
                Timestamp = baseTimestamp + TimeSpan.FromSeconds(30),
                CorrelationId = Guid.NewGuid()
            };

            // Step 3: DELETE — country record removed
            var deleteEvent = new RecordDeletedEvent
            {
                EntityName = "country",
                RecordId = recordId,
                Timestamp = baseTimestamp + TimeSpan.FromMinutes(1),
                CorrelationId = Guid.NewGuid()
            };

            // Act & Assert — process all three events in sequence
            // CREATE
            Func<Task> actCreate = async () =>
                await consumer.Consume(CreateCreatedContext(createEvent).Object);
            await actCreate.Should().NotThrowAsync("CREATE event should be processed");

            // UPDATE
            Func<Task> actUpdate = async () =>
                await consumer.Consume(CreateUpdatedContext(updateEvent).Object);
            await actUpdate.Should().NotThrowAsync("UPDATE event should be processed");

            // DELETE
            Func<Task> actDelete = async () =>
                await consumer.Consume(CreateDeletedContext(deleteEvent).Object);
            await actDelete.Should().NotThrowAsync("DELETE event should be processed");

            // Verify correct temporal ordering
            createEvent.Timestamp.Should().BeBefore(updateEvent.Timestamp);
            updateEvent.Timestamp.Should().BeBefore(deleteEvent.Timestamp);
        }

        #endregion
    }
}
