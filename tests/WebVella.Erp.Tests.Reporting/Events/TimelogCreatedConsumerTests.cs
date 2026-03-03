using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.Service.Reporting.Events;

namespace WebVella.Erp.Tests.Reporting.Events
{
    /// <summary>
    /// Unit tests for <see cref="TimelogCreatedConsumer"/>, the MassTransit
    /// <c>IConsumer&lt;RecordCreatedEvent&gt;</c> in the Reporting service that
    /// processes timelog creation events and persists <see cref="TimelogProjection"/>
    /// records for report aggregation.
    ///
    /// Covers:
    /// - Entity name filtering (only "timelog" events are processed)
    /// - Field extraction from <see cref="EntityRecord"/> payload (id, is_billable,
    ///   minutes, logged_on, l_scope, l_related_records)
    /// - Critical JSON parsing of <c>l_related_records</c> to extract taskId = ids[0],
    ///   matching the monolith pattern from ReportService.cs lines 51-53
    /// - Idempotent insert behavior per AAP 0.8.2 (duplicate events must not
    ///   create duplicate projections)
    /// - Edge cases: null/empty l_related_records, empty ids arrays
    ///
    /// Uses EF Core InMemory provider for isolated unit testing with a unique
    /// database per test instance to avoid cross-test state pollution.
    /// </summary>
    public class TimelogCreatedConsumerTests : IDisposable
    {
        private readonly ReportingDbContext _dbContext;
        private readonly Mock<ILogger<TimelogCreatedConsumer>> _loggerMock;
        private readonly TimelogCreatedConsumer _consumer;

        /// <summary>
        /// Initializes test infrastructure: creates a fresh InMemory database,
        /// mocks ILogger, and instantiates the consumer under test.
        /// Each test instance gets a unique database name to prevent cross-test
        /// state pollution when xUnit creates new instances per test method.
        /// </summary>
        public TimelogCreatedConsumerTests()
        {
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _dbContext = new ReportingDbContext(options);
            _loggerMock = new Mock<ILogger<TimelogCreatedConsumer>>();
            _consumer = new TimelogCreatedConsumer(_dbContext, _loggerMock.Object);
        }

        /// <summary>
        /// Disposes the InMemory database context to release resources after each test.
        /// </summary>
        public void Dispose()
        {
            _dbContext.Dispose();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="RecordCreatedEvent"/> simulating a timelog record
        /// creation, matching the monolith's <c>TimeLogService.Create()</c> record
        /// structure (source lines 39-48).
        ///
        /// The <c>l_related_records</c> field is serialized as <c>List&lt;Guid&gt;</c>
        /// matching the monolith convention where <c>ids[0]</c> is always the task_id
        /// (ReportService.cs lines 51-53).
        /// </summary>
        /// <param name="timelogId">Unique timelog record identifier.</param>
        /// <param name="isBillable">Whether the timelog entry is billable.</param>
        /// <param name="minutes">Number of minutes logged.</param>
        /// <param name="loggedOn">Date/time when work was performed.</param>
        /// <param name="scope">Scope identifier; defaults to "projects".</param>
        /// <param name="taskId">
        /// Optional task ID. When provided, serialized into l_related_records as
        /// a <c>List&lt;Guid&gt;</c> JSON array. When null, l_related_records is omitted.
        /// </param>
        /// <returns>A fully populated <see cref="RecordCreatedEvent"/> for timelog.</returns>
        private static RecordCreatedEvent CreateTimelogCreatedEvent(
            Guid timelogId,
            bool isBillable,
            int minutes,
            DateTime loggedOn,
            string scope = "projects",
            Guid? taskId = null)
        {
            var record = new EntityRecord();
            record["id"] = timelogId;
            record["is_billable"] = isBillable;
            record["minutes"] = minutes;
            record["logged_on"] = loggedOn;
            // The consumer reads l_scope as a raw string via (string)message.Record["l_scope"].
            // We set it directly as the scope string, matching what the consumer stores
            // in TimelogProjection.Scope.
            record["l_scope"] = scope;

            // l_related_records: serialized as List<Guid> matching the monolith convention.
            // TimeLogService.Create() serializes: JsonConvert.SerializeObject(relatedRecords)
            // where relatedRecords is List<Guid>. ReportService.cs deserializes the same way:
            //   List<Guid> ids = JsonConvert.DeserializeObject<List<Guid>>(...);
            //   Guid taskId = ids[0];
            // The consumer follows this exact pattern.
            if (taskId.HasValue)
            {
                record["l_related_records"] = JsonConvert.SerializeObject(
                    new List<Guid> { taskId.Value });
            }

            return new RecordCreatedEvent
            {
                EntityName = "timelog",
                Record = record,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Creates a <see cref="Mock{ConsumeContext{RecordCreatedEvent}}"/> configured
        /// to return the specified message from <c>ConsumeContext.Message</c>.
        /// </summary>
        /// <param name="message">The <see cref="RecordCreatedEvent"/> to wrap.</param>
        /// <returns>A configured mock consume context.</returns>
        private static Mock<ConsumeContext<RecordCreatedEvent>> CreateConsumeContext(
            RecordCreatedEvent message)
        {
            var mockContext = new Mock<ConsumeContext<RecordCreatedEvent>>();
            mockContext.Setup(c => c.Message).Returns(message);
            mockContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return mockContext;
        }

        #endregion

        #region Entity Name Filter Tests

        /// <summary>
        /// Verifies that a non-timelog entity event (e.g., "task") is ignored by
        /// the consumer and no TimelogProjection is created.
        /// The consumer filters by <c>message.EntityName != "timelog"</c> at the
        /// top of the Consume method and returns immediately for non-matching entities.
        /// </summary>
        [Fact]
        public async Task Consume_NonTimelogEntity_ShouldBeIgnored()
        {
            // Arrange: create a "task" entity event (not "timelog")
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();

            var message = new RecordCreatedEvent
            {
                EntityName = "task",
                Record = record,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert: no projection should have been created
            var count = await _dbContext.TimelogProjections.CountAsync();
            count.Should().Be(0, "non-timelog entity events should be completely ignored");
        }

        /// <summary>
        /// Parameterized test verifying that multiple non-timelog entity names
        /// are all filtered out by the consumer. Tests entity name filtering
        /// across the most common entity types that share RecordCreatedEvent.
        /// </summary>
        [Theory]
        [InlineData("task")]
        [InlineData("project")]
        [InlineData("account")]
        [InlineData("user")]
        [InlineData("")]
        public async Task Consume_NonTimelogEntityNames_ShouldNotCreateProjection(string entityName)
        {
            // Arrange: create an event with a non-timelog entity name
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();

            var message = new RecordCreatedEvent
            {
                EntityName = entityName,
                Record = record,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert: no projection for any non-timelog entity
            var any = await _dbContext.TimelogProjections.AnyAsync();
            any.Should().BeFalse(
                $"entity name '{entityName}' is not 'timelog' and should not create a projection");
        }

        #endregion

        #region Field Extraction Tests

        /// <summary>
        /// Comprehensive test verifying that all timelog record fields are correctly
        /// extracted from the <see cref="EntityRecord"/> payload and persisted in
        /// the <see cref="TimelogProjection"/>.
        ///
        /// Maps to the monolith's <c>TimeLogService.Create()</c> record structure:
        ///   record["id"]               → TimelogProjection.Id
        ///   record["is_billable"]      → TimelogProjection.IsBillable
        ///   record["minutes"]          → TimelogProjection.Minutes
        ///   record["logged_on"]        → TimelogProjection.LoggedOn
        ///   record["l_scope"]          → TimelogProjection.Scope
        ///   ids[0] from l_related_records → TimelogProjection.TaskId
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractAllFieldsFromRecord()
        {
            // Arrange
            var timelogId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var loggedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 120,
                loggedOn: loggedOn,
                scope: "projects",
                taskId: taskId);

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert: retrieve the created projection
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull("consumer should create a projection for timelog events");
            projection.Id.Should().Be(timelogId, "Id should match the timelog record id");
            projection.IsBillable.Should().BeTrue("is_billable was set to true");
            projection.Minutes.Should().Be(120m, "minutes should be extracted as decimal");
            projection.LoggedOn.Should().Be(loggedOn, "logged_on should match the event timestamp");
            projection.Scope.Should().Be("projects", "l_scope should be preserved");
            projection.TaskId.Should().Be(taskId, "TaskId should be extracted from l_related_records ids[0]");
        }

        /// <summary>
        /// Verifies that the timelog record's primary key (id field) is correctly
        /// preserved as the projection's Id, ensuring record traceability back to
        /// the source timelog entity.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldPreserveTimelogId()
        {
            // Arrange
            var timelogId = Guid.NewGuid();
            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 30,
                loggedOn: DateTime.UtcNow);

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull("projection should exist for the given timelog id");
            projection.Id.Should().Be(timelogId, "projection Id must exactly match the record id");
        }

        /// <summary>
        /// Verifies that the is_billable field is correctly extracted for both
        /// true and false values. The monolith's ReportService.GetTimelogData()
        /// uses this field to split minutes into billable_minutes and
        /// non_billable_minutes (source lines 125-128).
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractIsBillable()
        {
            // Arrange: create two events — one billable, one non-billable
            var billableId = Guid.NewGuid();
            var nonBillableId = Guid.NewGuid();

            var billableEvent = CreateTimelogCreatedEvent(
                timelogId: billableId,
                isBillable: true,
                minutes: 60,
                loggedOn: DateTime.UtcNow);

            var nonBillableEvent = CreateTimelogCreatedEvent(
                timelogId: nonBillableId,
                isBillable: false,
                minutes: 30,
                loggedOn: DateTime.UtcNow);

            // Act
            await _consumer.Consume(CreateConsumeContext(billableEvent).Object);
            await _consumer.Consume(CreateConsumeContext(nonBillableEvent).Object);

            // Assert
            var billableProjection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == billableId);
            var nonBillableProjection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == nonBillableId);

            billableProjection.Should().NotBeNull();
            billableProjection.IsBillable.Should().BeTrue("is_billable was set to true");

            nonBillableProjection.Should().NotBeNull();
            nonBillableProjection.IsBillable.Should().BeFalse("is_billable was set to false");
        }

        /// <summary>
        /// Verifies that the minutes field is correctly extracted from the record
        /// and stored as a decimal in the projection. The minutes value is used
        /// by ReportAggregationService for billable/non-billable aggregation.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractMinutes()
        {
            // Arrange
            var timelogId = Guid.NewGuid();
            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 45,
                loggedOn: DateTime.UtcNow);

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull();
            projection.Minutes.Should().Be(45m,
                "minutes should be extracted from the record and stored as decimal");
        }

        /// <summary>
        /// Verifies that the logged_on field (DateTime) is correctly extracted
        /// and preserved in the projection. This field is critical for date-range
        /// filtering in report queries, matching the monolith's WHERE clause:
        ///   logged_on >= @from_date AND logged_on &lt;= @to_date
        /// (ReportService.cs source lines 42-45).
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractLoggedOn()
        {
            // Arrange: use a specific date that can be precisely verified
            var timelogId = Guid.NewGuid();
            var loggedOn = new DateTime(2024, 3, 15, 14, 30, 0, DateTimeKind.Utc);

            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 90,
                loggedOn: loggedOn);

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull();
            projection.LoggedOn.Should().Be(loggedOn,
                "logged_on date should be exactly preserved for date-range report filtering");
        }

        /// <summary>
        /// Verifies that the l_scope field is correctly extracted from the record
        /// and stored in the projection's Scope property. The consumer reads
        /// l_scope as a raw string via <c>(string)message.Record["l_scope"]</c>.
        /// In the monolith, scope defaults to "projects" and is used in the
        /// WHERE clause: <c>l_scope CONTAINS @scope</c> (ReportService.cs line 45).
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractScope()
        {
            // Arrange
            var timelogId = Guid.NewGuid();
            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 60,
                loggedOn: DateTime.UtcNow,
                scope: "projects");

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull();
            projection.Scope.Should().Be("projects",
                "l_scope value should be preserved as the Scope field");
        }

        #endregion

        #region l_related_records JSON Parsing Tests (CRITICAL)

        /// <summary>
        /// CRITICAL TEST: Verifies that the consumer correctly parses the
        /// <c>l_related_records</c> JSON field and extracts <c>taskId = ids[0]</c>.
        ///
        /// This test directly validates the monolith pattern from ReportService.cs
        /// lines 51-53:
        /// <code>
        /// List&lt;Guid&gt; ids = JsonConvert.DeserializeObject&lt;List&lt;Guid&gt;&gt;(
        ///     (string)timelog["l_related_records"]);
        /// Guid taskId = ids[0];
        /// </code>
        ///
        /// The consumer follows this exact deserialization pattern, where
        /// <c>l_related_records</c> is a JSON-serialized <c>List&lt;Guid&gt;</c>
        /// and <c>ids[0]</c> is always the task_id by monolith convention.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldParseRelatedRecordsAndExtractTaskId()
        {
            // Arrange
            var timelogId = Guid.NewGuid();
            var taskId = Guid.NewGuid();

            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 60,
                loggedOn: DateTime.UtcNow,
                taskId: taskId);

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull();
            projection.TaskId.Should().Be(taskId,
                "TaskId must be extracted from l_related_records ids[0] per monolith convention");
        }

        /// <summary>
        /// Verifies that when <c>l_related_records</c> is not present in the
        /// record (field not set), the consumer gracefully handles the absence
        /// and sets TaskId to <c>Guid.Empty</c> (the null-equivalent for
        /// non-nullable Guid). The projection should still be created successfully.
        /// </summary>
        [Fact]
        public async Task Consume_NullRelatedRecords_ShouldSetTaskIdToNull()
        {
            // Arrange: create event WITHOUT l_related_records field
            var timelogId = Guid.NewGuid();
            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 30,
                loggedOn: DateTime.UtcNow,
                taskId: null); // no taskId → no l_related_records in helper

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert: projection created with TaskId = Guid.Empty
            // (TimelogProjection.TaskId is non-nullable Guid; consumer uses ?? Guid.Empty)
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull(
                "projection should still be created even without l_related_records");
            projection.TaskId.Should().Be(Guid.Empty,
                "TaskId should be Guid.Empty when l_related_records is not present");
        }

        /// <summary>
        /// Verifies that when <c>l_related_records</c> is set to an empty JSON
        /// array ("[]"), the consumer correctly handles it and sets TaskId to
        /// <c>Guid.Empty</c>. The deserialized <c>List&lt;Guid&gt;</c> would be
        /// empty (Count == 0), so the <c>ids[0]</c> extraction is skipped.
        /// </summary>
        [Fact]
        public async Task Consume_EmptyRelatedRecords_ShouldSetTaskIdToNull()
        {
            // Arrange: create event with empty JSON array for l_related_records
            var timelogId = Guid.NewGuid();
            var record = new EntityRecord();
            record["id"] = timelogId;
            record["is_billable"] = true;
            record["minutes"] = 30;
            record["logged_on"] = DateTime.UtcNow;
            record["l_scope"] = "projects";
            record["l_related_records"] = "[]"; // empty JSON array

            var message = new RecordCreatedEvent
            {
                EntityName = "timelog",
                Record = record,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert: TaskId should be Guid.Empty because ids list is empty
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull(
                "projection should be created even with empty l_related_records");
            projection.TaskId.Should().Be(Guid.Empty,
                "TaskId should be Guid.Empty when l_related_records is an empty array");
        }

        /// <summary>
        /// Verifies that when <c>l_related_records</c> contains a JSON array
        /// with zero GUIDs (effectively an empty ids list), the consumer
        /// correctly sets TaskId to <c>Guid.Empty</c>.
        ///
        /// This tests the <c>ids.Count > 0</c> guard in the consumer that
        /// prevents IndexOutOfRangeException on empty id lists.
        /// </summary>
        [Fact]
        public async Task Consume_RelatedRecordsWithEmptyIds_ShouldSetTaskIdToNull()
        {
            // Arrange: serialize an empty List<Guid> to get a valid but empty JSON array
            var timelogId = Guid.NewGuid();
            var record = new EntityRecord();
            record["id"] = timelogId;
            record["is_billable"] = false;
            record["minutes"] = 15;
            record["logged_on"] = DateTime.UtcNow;
            record["l_scope"] = "projects";
            // Empty List<Guid> serialized: this produces "[]"
            record["l_related_records"] = JsonConvert.SerializeObject(new List<Guid>());

            var message = new RecordCreatedEvent
            {
                EntityName = "timelog",
                Record = record,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            var context = CreateConsumeContext(message);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull(
                "projection should be created even with empty ids in l_related_records");
            projection.TaskId.Should().Be(Guid.Empty,
                "TaskId should be Guid.Empty when ids array is empty");
        }

        #endregion

        #region Idempotent Insert Tests (AAP 0.8.2)

        /// <summary>
        /// Verifies that consuming the same event twice does NOT create duplicate
        /// <see cref="TimelogProjection"/> records. This directly tests the AAP 0.8.2
        /// requirement: "Event consumers MUST be idempotent — duplicate event delivery
        /// must not cause data corruption."
        ///
        /// The consumer checks for existing projections by timelog ID before inserting
        /// and logs a warning for skipped duplicates.
        /// </summary>
        [Fact]
        public async Task Consume_DuplicateEvent_ShouldNotCreateDuplicateProjection()
        {
            // Arrange
            var timelogId = Guid.NewGuid();
            var taskId = Guid.NewGuid();
            var loggedOn = new DateTime(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);

            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: true,
                minutes: 60,
                loggedOn: loggedOn,
                taskId: taskId);

            var context1 = CreateConsumeContext(message);
            var context2 = CreateConsumeContext(message);

            // Act: consume the same event TWICE
            await _consumer.Consume(context1.Object);
            await _consumer.Consume(context2.Object);

            // Assert: exactly 1 projection with that Id (no duplicates)
            var projections = await _dbContext.TimelogProjections
                .Where(t => t.Id == timelogId)
                .ToListAsync();

            projections.Should().HaveCount(1,
                "duplicate event delivery must not create duplicate projections (AAP 0.8.2 idempotency)");
        }

        /// <summary>
        /// Verifies that when a <see cref="TimelogProjection"/> already exists
        /// (pre-seeded), consuming an event with the same timelog ID is silently
        /// skipped without throwing an exception, and the original projection data
        /// remains unchanged.
        ///
        /// This validates the consumer's idempotent "check-then-skip" pattern:
        /// <code>
        /// bool exists = await _dbContext.TimelogProjections.AnyAsync(t =&gt; t.Id == timelogId);
        /// if (exists) { _logger.LogWarning(...); return; }
        /// </code>
        /// </summary>
        [Fact]
        public async Task Consume_DuplicateEvent_ShouldSkipWithoutError()
        {
            // Arrange: seed an existing TimelogProjection
            var timelogId = Guid.NewGuid();
            var originalTaskId = Guid.NewGuid();
            var originalLoggedOn = new DateTime(2024, 5, 1, 8, 0, 0, DateTimeKind.Utc);

            var existingProjection = new TimelogProjection
            {
                Id = timelogId,
                TaskId = originalTaskId,
                IsBillable = true,
                Minutes = 60m,
                LoggedOn = originalLoggedOn,
                Scope = "projects",
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            };
            _dbContext.TimelogProjections.Add(existingProjection);
            await _dbContext.SaveChangesAsync();

            // Create a DIFFERENT event payload with the same timelogId but
            // different field values — if idempotency is broken, these values
            // would create a duplicate or overwrite the original.
            var message = CreateTimelogCreatedEvent(
                timelogId: timelogId,
                isBillable: false,       // different from seeded
                minutes: 999,            // different from seeded
                loggedOn: DateTime.UtcNow,
                taskId: Guid.NewGuid()); // different from seeded

            var context = CreateConsumeContext(message);

            // Act: should NOT throw
            var act = async () => await _consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "duplicate event should be silently skipped without error");

            // Assert: still exactly 1 projection, with original data unchanged
            var count = await _dbContext.TimelogProjections
                .CountAsync(t => t.Id == timelogId);
            count.Should().Be(1, "no duplicate projection should be created");

            var projection = await _dbContext.TimelogProjections
                .FirstOrDefaultAsync(t => t.Id == timelogId);

            projection.Should().NotBeNull();
            projection.TaskId.Should().Be(originalTaskId,
                "original TaskId should be unchanged after duplicate event");
            projection.IsBillable.Should().BeTrue(
                "original IsBillable should be unchanged after duplicate event");
            projection.Minutes.Should().Be(60m,
                "original Minutes should be unchanged after duplicate event");
            projection.LoggedOn.Should().Be(originalLoggedOn,
                "original LoggedOn should be unchanged after duplicate event");
        }

        #endregion
    }
}
