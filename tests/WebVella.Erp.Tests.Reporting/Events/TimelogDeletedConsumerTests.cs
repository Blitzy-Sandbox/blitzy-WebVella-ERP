using System;
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
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.Service.Reporting.Events;

namespace WebVella.Erp.Tests.Reporting.Events
{
    /// <summary>
    /// Unit tests for <see cref="TimelogDeletedConsumer"/> — the MassTransit consumer
    /// that processes <see cref="RecordDeletedEvent"/> events where
    /// <c>EntityName == "timelog"</c>, removing the corresponding
    /// <see cref="TimelogProjection"/> record from the Reporting service's database.
    ///
    /// <para>
    /// <b>Monolith origin:</b> In the monolith, the timelog delete hook is registered
    /// in <c>WebVella.Erp.Plugins.Project/Hooks/Api/Timelog.cs</c> (lines 22-26) as
    /// <c>[HookAttachment("timelog")] IErpPreDeleteRecordHook</c>, delegating to
    /// <c>TimeLogService.PreDeleteApiHookLogic()</c>. In the microservice architecture,
    /// post-delete side effects are handled asynchronously by this consumer via
    /// <see cref="RecordDeletedEvent"/> published on the message bus.
    /// </para>
    ///
    /// <para>
    /// <b>Key distinction from Created/Updated consumers:</b>
    /// <see cref="RecordDeletedEvent"/> carries only <see cref="RecordDeletedEvent.RecordId"/>
    /// (a <see cref="Guid"/>), NOT a full <c>EntityRecord</c>. The consumer uses this
    /// RecordId to find and remove the projection.
    /// </para>
    ///
    /// <para>
    /// <b>Testing approach:</b> Uses EF Core InMemory provider for fast, isolated
    /// database access. Each test instance gets a unique in-memory database name
    /// (via <see cref="Guid.NewGuid()"/>) to prevent cross-test data contamination.
    /// <see cref="ILogger{T}"/> and <see cref="ConsumeContext{T}"/> are mocked via Moq.
    /// Assertions use FluentAssertions per AAP 0.8.2 testing standards.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency coverage (AAP 0.8.2):</b> Multiple tests verify that duplicate
    /// event delivery, deletion of non-existent projections, and repeated deletions
    /// for the same record all complete without exceptions or data corruption.
    /// </para>
    /// </summary>
    public class TimelogDeletedConsumerTests : IDisposable
    {
        private readonly ReportingDbContext _dbContext;
        private readonly Mock<ILogger<TimelogDeletedConsumer>> _loggerMock;
        private readonly TimelogDeletedConsumer _consumer;

        /// <summary>
        /// Initializes a new test instance with an isolated InMemory database,
        /// a mocked logger, and a fully constructed <see cref="TimelogDeletedConsumer"/>.
        /// Each test instance uses a unique database name to prevent cross-test
        /// data contamination when xUnit runs tests in parallel.
        /// </summary>
        public TimelogDeletedConsumerTests()
        {
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _dbContext = new ReportingDbContext(options);
            _loggerMock = new Mock<ILogger<TimelogDeletedConsumer>>();
            _consumer = new TimelogDeletedConsumer(_dbContext, _loggerMock.Object);
        }

        /// <summary>
        /// Disposes the <see cref="ReportingDbContext"/> to release InMemory database resources.
        /// Called automatically by xUnit after each test method completes.
        /// </summary>
        public void Dispose()
        {
            _dbContext.Dispose();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="RecordDeletedEvent"/> with <c>EntityName = "timelog"</c>
        /// and the specified <paramref name="recordId"/>. The event carries only the
        /// <see cref="RecordDeletedEvent.RecordId"/> (Guid), not a full EntityRecord,
        /// matching the simplified delete event contract.
        /// </summary>
        /// <param name="recordId">The unique identifier of the deleted timelog record.</param>
        /// <returns>A configured <see cref="RecordDeletedEvent"/> for timelog deletion.</returns>
        private static RecordDeletedEvent CreateTimelogDeletedEvent(Guid recordId)
        {
            return new RecordDeletedEvent
            {
                EntityName = "timelog",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                RecordId = recordId
            };
        }

        /// <summary>
        /// Creates a <see cref="RecordDeletedEvent"/> with a non-timelog
        /// <paramref name="entityName"/> for testing entity name filtering.
        /// The consumer should ignore events whose EntityName is not "timelog".
        /// </summary>
        /// <param name="recordId">The unique identifier carried by the event.</param>
        /// <param name="entityName">
        /// A non-timelog entity name (e.g., "task", "project", "account", "user", "").
        /// </param>
        /// <returns>A configured <see cref="RecordDeletedEvent"/> for the specified entity.</returns>
        private static RecordDeletedEvent CreateNonTimelogDeletedEvent(Guid recordId, string entityName)
        {
            return new RecordDeletedEvent
            {
                EntityName = entityName,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                RecordId = recordId
            };
        }

        /// <summary>
        /// Creates a mocked <see cref="ConsumeContext{T}"/> wrapping the specified
        /// <paramref name="message"/>. The mock returns the message from the
        /// <see cref="ConsumeContext{T}.Message"/> property and provides
        /// <see cref="CancellationToken.None"/> for the CancellationToken property.
        /// </summary>
        /// <param name="message">The <see cref="RecordDeletedEvent"/> to wrap in the context.</param>
        /// <returns>A configured <see cref="Mock{T}"/> of <see cref="ConsumeContext{T}"/>.</returns>
        private static Mock<ConsumeContext<RecordDeletedEvent>> CreateConsumeContext(RecordDeletedEvent message)
        {
            var contextMock = new Mock<ConsumeContext<RecordDeletedEvent>>();
            contextMock.Setup(c => c.Message).Returns(message);
            contextMock.Setup(c => c.CancellationToken).Returns(CancellationToken.None);
            return contextMock;
        }

        /// <summary>
        /// Seeds a <see cref="TimelogProjection"/> record into the InMemory database
        /// with the specified parameters. Default values are provided for optional
        /// parameters to simplify test setup.
        /// </summary>
        /// <param name="id">Primary key for the TimelogProjection — typically matches a RecordDeletedEvent.RecordId.</param>
        /// <param name="taskId">Optional task UUID. Defaults to a new Guid if not specified.</param>
        /// <param name="isBillable">Whether the timelog is billable. Defaults to <c>true</c>.</param>
        /// <param name="minutes">Number of minutes logged. Defaults to 60.</param>
        /// <returns>A task representing the asynchronous seed operation.</returns>
        private async Task SeedTimelogProjection(Guid id, Guid? taskId = null, bool isBillable = true, int minutes = 60)
        {
            _dbContext.TimelogProjections.Add(new TimelogProjection
            {
                Id = id,
                TaskId = taskId ?? Guid.NewGuid(),
                IsBillable = isBillable,
                Minutes = minutes,
                LoggedOn = DateTime.UtcNow.AddDays(-1),
                Scope = "projects",
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();
        }

        #endregion

        #region Entity Name Filter Tests

        /// <summary>
        /// Verifies that the consumer ignores events where <c>EntityName != "timelog"</c>.
        /// When a <see cref="RecordDeletedEvent"/> with <c>EntityName = "task"</c> is
        /// consumed, no <see cref="TimelogProjection"/> records should be deleted, even
        /// if the <c>RecordId</c> matches an existing projection's Id.
        ///
        /// This validates the consumer's entity name filter at line 106 of
        /// <c>TimelogDeletedConsumer.cs</c>: <c>if (message.EntityName != "timelog") return;</c>
        /// </summary>
        [Fact]
        public async Task Consume_NonTimelogEntity_ShouldBeIgnored()
        {
            // Arrange: Seed a projection and create an event with a non-timelog entity name
            var projectionId = Guid.NewGuid();
            await SeedTimelogProjection(projectionId);

            var deletedEvent = CreateNonTimelogDeletedEvent(projectionId, "task");
            var context = CreateConsumeContext(deletedEvent);

            // Act: Consume the event
            await _consumer.Consume(context.Object);

            // Assert: The projection should still exist — consumer ignored the event
            var remaining = _dbContext.TimelogProjections.FirstOrDefault(t => t.Id == projectionId);
            remaining.Should().NotBeNull("consumer should ignore events with EntityName != 'timelog'");
            _dbContext.TimelogProjections.Count().Should().Be(1);
        }

        /// <summary>
        /// Parameterized test verifying that various non-timelog entity names
        /// ("task", "project", "account", "user", "") are all correctly filtered out
        /// by the consumer's entity name check. For each entity name, a projection
        /// is seeded with a matching RecordId, and after consumption the projection
        /// must remain intact.
        ///
        /// Tests the boundary conditions of the <c>EntityName != "timelog"</c> filter
        /// including empty string edge case.
        /// </summary>
        /// <param name="entityName">The non-timelog entity name to test.</param>
        [Theory]
        [InlineData("task")]
        [InlineData("project")]
        [InlineData("account")]
        [InlineData("user")]
        [InlineData("")]
        public async Task Consume_NonTimelogEntityNames_ShouldNotDeleteProjection(string entityName)
        {
            // Arrange: Seed a projection and create a non-timelog event with matching RecordId
            var projectionId = Guid.NewGuid();
            await SeedTimelogProjection(projectionId, isBillable: false, minutes: 120);

            var deletedEvent = CreateNonTimelogDeletedEvent(projectionId, entityName);
            var context = CreateConsumeContext(deletedEvent);

            // Act: Consume the event
            await _consumer.Consume(context.Object);

            // Assert: Projection must still exist — not deleted by the consumer
            var remaining = _dbContext.TimelogProjections.FirstOrDefault(t => t.Id == projectionId);
            remaining.Should().NotBeNull(
                $"consumer should not delete projection for EntityName '{entityName}'");
            remaining.IsBillable.Should().BeFalse("original seeded value should be preserved");
            _dbContext.TimelogProjections.Count().Should().Be(1);
        }

        #endregion

        #region TimelogProjection Removal Tests

        /// <summary>
        /// Verifies that consuming a <see cref="RecordDeletedEvent"/> with
        /// <c>EntityName = "timelog"</c> and a matching <c>RecordId</c> removes
        /// the corresponding <see cref="TimelogProjection"/> from the database.
        ///
        /// This is the core happy-path test validating the consumer's primary
        /// responsibility: removing timelog projections to maintain accurate
        /// aggregation data in <c>ReportAggregationService.GetTimelogData()</c>.
        /// </summary>
        [Fact]
        public async Task Consume_ExistingTimelog_ShouldRemoveProjection()
        {
            // Arrange: Seed a projection
            var projectionId = Guid.NewGuid();
            await SeedTimelogProjection(projectionId, isBillable: true, minutes: 90);

            // Verify seed was successful
            _dbContext.TimelogProjections.Count().Should().Be(1);

            var deletedEvent = CreateTimelogDeletedEvent(projectionId);
            var context = CreateConsumeContext(deletedEvent);

            // Act: Consume the delete event
            await _consumer.Consume(context.Object);

            // Assert: The projection should be removed
            _dbContext.TimelogProjections.Count().Should().Be(0,
                "consumer should remove the projection matching the RecordId");
            _dbContext.TimelogProjections.FirstOrDefault(t => t.Id == projectionId)
                .Should().BeNull("targeted projection should no longer exist");
        }

        /// <summary>
        /// Verifies that the consumer removes ONLY the targeted projection when
        /// multiple projections exist in the database. The other projections
        /// must remain untouched.
        ///
        /// Seeds 3 projections with different IDs, targets the middle one for
        /// deletion, and verifies only the targeted one is removed while the
        /// other two remain with their original data intact.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldRemoveOnlyTargetedProjection()
        {
            // Arrange: Seed 3 projections with distinct IDs
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid(); // Target for deletion
            var id3 = Guid.NewGuid();

            await SeedTimelogProjection(id1, isBillable: true, minutes: 30);
            await SeedTimelogProjection(id2, isBillable: false, minutes: 45);
            await SeedTimelogProjection(id3, isBillable: true, minutes: 60);

            // Verify all 3 are seeded
            _dbContext.TimelogProjections.Count().Should().Be(3);

            // Create event targeting the middle projection (id2)
            var deletedEvent = CreateTimelogDeletedEvent(id2);
            var context = CreateConsumeContext(deletedEvent);

            // Act: Consume the delete event
            await _consumer.Consume(context.Object);

            // Assert: Only the targeted projection should be removed
            _dbContext.TimelogProjections.Count().Should().Be(2,
                "only the targeted projection should be removed");

            // Verify the targeted projection is gone
            _dbContext.TimelogProjections.Any(t => t.Id == id2)
                .Should().BeFalse("the targeted projection (id2) should be deleted");

            // Verify the other two remain with original data
            var remaining1 = _dbContext.TimelogProjections.FirstOrDefault(t => t.Id == id1);
            remaining1.Should().NotBeNull("non-targeted projection (id1) should survive");
            remaining1.IsBillable.Should().BeTrue("id1 original billable value should be preserved");

            var remaining3 = _dbContext.TimelogProjections.FirstOrDefault(t => t.Id == id3);
            remaining3.Should().NotBeNull("non-targeted projection (id3) should survive");
            remaining3.IsBillable.Should().BeTrue("id3 original billable value should be preserved");
        }

        #endregion

        #region Idempotent Deletion Tests (AAP 0.8.2)

        /// <summary>
        /// Verifies that consuming a delete event for a non-existent projection
        /// completes without throwing an exception and leaves the database unchanged.
        ///
        /// This is the KEY idempotency test per AAP 0.8.2: "Event consumers must be
        /// idempotent (duplicate event delivery must not cause data corruption)."
        /// The consumer should log a warning and return gracefully when the projection
        /// is not found (line 123-134 of <c>TimelogDeletedConsumer.cs</c>).
        /// </summary>
        [Fact]
        public async Task Consume_NonExistentProjection_ShouldReturnWithoutError()
        {
            // Arrange: Do NOT seed any projection — database is empty
            var randomId = Guid.NewGuid();
            var deletedEvent = CreateTimelogDeletedEvent(randomId);
            var context = CreateConsumeContext(deletedEvent);

            // Act: Consume the event — should NOT throw
            var act = async () => await _consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "consumer must handle non-existent projection gracefully (idempotent)");

            // Assert: Database should still be empty
            _dbContext.TimelogProjections.Count().Should().Be(0,
                "empty database should remain empty after consuming delete for non-existent record");
        }

        /// <summary>
        /// Verifies that consuming the same delete event twice does not throw an
        /// exception on the second invocation. The first call removes the projection;
        /// the second call finds nothing to delete and returns gracefully.
        ///
        /// This tests the MassTransit retry scenario where duplicate delivery of the
        /// same <see cref="RecordDeletedEvent"/> must be handled without errors.
        /// </summary>
        [Fact]
        public async Task Consume_DuplicateDeleteEvent_ShouldNotThrow()
        {
            // Arrange: Seed a projection
            var projectionId = Guid.NewGuid();
            await SeedTimelogProjection(projectionId, isBillable: true, minutes: 75);

            var deletedEvent = CreateTimelogDeletedEvent(projectionId);
            var context = CreateConsumeContext(deletedEvent);

            // Act 1: First consume — removes the projection
            await _consumer.Consume(context.Object);
            _dbContext.TimelogProjections.Count().Should().Be(0,
                "first consume should remove the projection");

            // Act 2: Second consume with same event — should NOT throw
            var act = async () => await _consumer.Consume(context.Object);
            await act.Should().NotThrowAsync(
                "duplicate delete event must be handled gracefully (idempotent)");

            // Assert: Database should still be empty
            _dbContext.TimelogProjections.Count().Should().Be(0,
                "database should remain empty after duplicate delete event");
        }

        /// <summary>
        /// Verifies that consuming the same delete event three times for a record
        /// that was never seeded completes without any exceptions on every call.
        ///
        /// This is an extended idempotency test covering the scenario where multiple
        /// retries are triggered by MassTransit for a record that never existed in
        /// the projection table. All three invocations must succeed without errors
        /// and the database state must remain unchanged.
        /// </summary>
        [Fact]
        public async Task Consume_MultipleDeletesForSameNonExistentRecord_ShouldAllSucceed()
        {
            // Arrange: No seeded data — record never existed
            var randomId = Guid.NewGuid();
            var deletedEvent = CreateTimelogDeletedEvent(randomId);
            var context = CreateConsumeContext(deletedEvent);

            // Act & Assert: Three consecutive consumes should all succeed
            var act1 = async () => await _consumer.Consume(context.Object);
            await act1.Should().NotThrowAsync("first consume of non-existent record should succeed");

            var act2 = async () => await _consumer.Consume(context.Object);
            await act2.Should().NotThrowAsync("second consume of non-existent record should succeed");

            var act3 = async () => await _consumer.Consume(context.Object);
            await act3.Should().NotThrowAsync("third consume of non-existent record should succeed");

            // Assert: Database state unchanged — still empty
            _dbContext.TimelogProjections.Count().Should().Be(0,
                "database should remain empty after multiple deletes for non-existent record");
            _dbContext.TimelogProjections.Any().Should().BeFalse(
                "no projections should exist after repeated deletes for non-existent record");
        }

        #endregion
    }
}
