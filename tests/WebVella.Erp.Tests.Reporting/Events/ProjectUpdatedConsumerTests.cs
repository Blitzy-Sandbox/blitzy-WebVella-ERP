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
    /// Unit tests for <see cref="ProjectUpdatedConsumer"/>, the MassTransit event consumer
    /// that processes <see cref="RecordUpdatedEvent"/> where EntityName == "project".
    ///
    /// Tests verify:
    /// - Entity name filtering: non-project events are ignored
    /// - Idempotent upsert on <see cref="ProjectProjection"/> (AAP 0.8.2)
    /// - Field extraction from NewRecord (name, account_id)
    /// - AccountId cascade to <see cref="TimelogProjection"/> records
    ///
    /// Source context: RecordHookManager.cs lines 68-76 (replaced synchronous hooks),
    /// ReportService.cs lines 59-61, 77-97 (project data model with account_id).
    /// </summary>
    public class ProjectUpdatedConsumerTests : IDisposable
    {
        private readonly ReportingDbContext _dbContext;
        private readonly Mock<ILogger<ProjectUpdatedConsumer>> _loggerMock;
        private readonly ProjectUpdatedConsumer _consumer;

        /// <summary>
        /// Initializes test infrastructure with an isolated InMemory database per test instance,
        /// a mocked logger, and the consumer under test.
        /// Each test gets a unique database name via Guid.NewGuid() to prevent cross-test pollution.
        /// </summary>
        public ProjectUpdatedConsumerTests()
        {
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _dbContext = new ReportingDbContext(options);
            _loggerMock = new Mock<ILogger<ProjectUpdatedConsumer>>();
            _consumer = new ProjectUpdatedConsumer(_dbContext, _loggerMock.Object);
        }

        /// <summary>
        /// Disposes the InMemory database context after each test to release resources.
        /// </summary>
        public void Dispose()
        {
            _dbContext?.Dispose();
        }

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="RecordUpdatedEvent"/> for a project entity with the specified
        /// field values. Populates NewRecord via EntityRecord string indexer with id, name,
        /// and account_id fields matching the project data model from ReportService.cs line 60:
        /// $project_nn_task.id, $project_nn_task.name, $project_nn_task.account_id.
        /// </summary>
        /// <param name="projectId">The project record UUID.</param>
        /// <param name="name">The project name.</param>
        /// <param name="accountId">The nullable account UUID associated with the project.</param>
        /// <returns>A fully populated RecordUpdatedEvent for testing.</returns>
        private static RecordUpdatedEvent CreateProjectUpdatedEvent(Guid projectId, string name, Guid? accountId)
        {
            var newRecord = new EntityRecord();
            newRecord["id"] = projectId;
            newRecord["name"] = name;
            if (accountId.HasValue)
                newRecord["account_id"] = accountId.Value;
            else
                newRecord["account_id"] = null;

            return new RecordUpdatedEvent
            {
                EntityName = "project",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                OldRecord = new EntityRecord(),
                NewRecord = newRecord
            };
        }

        /// <summary>
        /// Creates a mocked <see cref="ConsumeContext{RecordUpdatedEvent}"/> that returns
        /// the specified message when <c>.Message</c> is accessed.
        /// </summary>
        /// <param name="message">The event message payload.</param>
        /// <returns>A configured Moq mock of the consume context.</returns>
        private static Mock<ConsumeContext<RecordUpdatedEvent>> CreateConsumeContext(RecordUpdatedEvent message)
        {
            var contextMock = new Mock<ConsumeContext<RecordUpdatedEvent>>();
            contextMock.Setup(c => c.Message).Returns(message);
            return contextMock;
        }

        #endregion

        #region Entity Name Filter Tests

        /// <summary>
        /// Verifies that the consumer ignores events where EntityName is not "project".
        /// The consumer should return immediately without creating any ProjectProjection.
        /// Replaces the monolith's hook attachment filter: [HookAttachment("project")].
        /// </summary>
        [Fact]
        public async Task Consume_NonProjectEntity_ShouldBeIgnored()
        {
            // Arrange
            var evt = new RecordUpdatedEvent
            {
                EntityName = "task",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                OldRecord = new EntityRecord(),
                NewRecord = new EntityRecord()
            };
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            _dbContext.ProjectProjections.Count().Should().Be(0,
                "non-project events must not create ProjectProjection records");
        }

        /// <summary>
        /// Parameterized test verifying that various non-project entity names are all ignored
        /// by the consumer. Covers common entity types and edge case of empty string.
        /// </summary>
        [Theory]
        [InlineData("task")]
        [InlineData("timelog")]
        [InlineData("account")]
        [InlineData("user")]
        [InlineData("")]
        public async Task Consume_NonProjectEntityNames_ShouldNotCreateProjection(string entityName)
        {
            // Arrange
            var evt = new RecordUpdatedEvent
            {
                EntityName = entityName,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                OldRecord = new EntityRecord(),
                NewRecord = new EntityRecord()
            };
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            _dbContext.ProjectProjections.Count().Should().Be(0,
                $"entity name '{entityName}' is not 'project' and should be ignored");
        }

        #endregion

        #region Upsert Tests — ProjectProjection

        /// <summary>
        /// Verifies that consuming a project updated event for a new project ID creates
        /// a new ProjectProjection with correct field values extracted from NewRecord.
        /// Maps to the consumer's create path when no existing projection is found.
        /// </summary>
        [Fact]
        public async Task Consume_NewProject_ShouldCreateProjection()
        {
            // Arrange
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var evt = CreateProjectUpdatedEvent(projectId, "Test Project", accountId);
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projections = _dbContext.ProjectProjections.ToList();
            projections.Should().HaveCount(1, "a new ProjectProjection should be created");

            var projection = projections[0];
            projection.Id.Should().Be(projectId);
            projection.Name.Should().Be("Test Project");
            projection.AccountId.Should().Be(accountId);
        }

        /// <summary>
        /// Verifies that consuming a project updated event for an already-existing project ID
        /// updates the existing ProjectProjection rather than creating a duplicate.
        /// This tests the update path of the idempotent upsert pattern (AAP 0.8.2).
        /// </summary>
        [Fact]
        public async Task Consume_ExistingProject_ShouldUpdateProjection()
        {
            // Arrange — seed existing projection
            var projectId = Guid.NewGuid();
            var oldAccountId = Guid.NewGuid();
            _dbContext.ProjectProjections.Add(new ProjectProjection
            {
                Id = projectId,
                Name = "Old Name",
                AccountId = oldAccountId,
                CreatedOn = DateTime.UtcNow.AddDays(-1),
                LastModifiedOn = DateTime.UtcNow.AddDays(-1)
            });
            await _dbContext.SaveChangesAsync();

            var newAccountId = Guid.NewGuid();
            var evt = CreateProjectUpdatedEvent(projectId, "New Name", newAccountId);
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projections = _dbContext.ProjectProjections.ToList();
            projections.Should().HaveCount(1, "update should not create a duplicate projection");

            var projection = projections[0];
            projection.Name.Should().Be("New Name");
            projection.AccountId.Should().Be(newAccountId);
        }

        /// <summary>
        /// Verifies idempotent upsert behavior: consuming the same event twice produces
        /// the same final state without data corruption or duplicate records.
        /// This is a core requirement per AAP 0.8.2 — event consumers MUST be idempotent.
        /// </summary>
        [Fact]
        public async Task Consume_DuplicateProjectEvent_ShouldResultInSameFinalState()
        {
            // Arrange
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var evt = CreateProjectUpdatedEvent(projectId, "Idempotent Project", accountId);
            var context1 = CreateConsumeContext(evt);
            var context2 = CreateConsumeContext(evt);

            // Act — consume the same event twice
            await _consumer.Consume(context1.Object);
            await _consumer.Consume(context2.Object);

            // Assert
            var projections = _dbContext.ProjectProjections.ToList();
            projections.Should().HaveCount(1, "duplicate events must not create duplicate projections");

            var projection = projections[0];
            projection.Id.Should().Be(projectId);
            projection.Name.Should().Be("Idempotent Project");
            projection.AccountId.Should().Be(accountId);
        }

        /// <summary>
        /// Verifies that the consumer correctly extracts the name and account_id fields
        /// from the RecordUpdatedEvent.NewRecord EntityRecord via string indexer access.
        /// Maps to the project data model in ReportService.cs line 60:
        /// $project_nn_task.name and $project_nn_task.account_id.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldExtractNameAndAccountIdFromNewRecord()
        {
            // Arrange
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var evt = CreateProjectUpdatedEvent(projectId, "Extraction Test Project", accountId);
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = _dbContext.ProjectProjections.FirstOrDefault(p => p.Id == projectId);
            projection.Should().NotBeNull("projection should have been created");
            projection.Name.Should().Be("Extraction Test Project",
                "Name must be extracted from NewRecord[\"name\"]");
            projection.AccountId.Should().Be(accountId,
                "AccountId must be extracted from NewRecord[\"account_id\"]");
        }

        /// <summary>
        /// Verifies that the consumer handles a null account_id gracefully.
        /// A project may not be associated with any account — the monolith allows this
        /// (ReportService.cs line 81: project["account_id"] == null check).
        /// </summary>
        [Fact]
        public async Task Consume_ProjectWithNullAccountId_ShouldHandleGracefully()
        {
            // Arrange
            var projectId = Guid.NewGuid();
            var evt = CreateProjectUpdatedEvent(projectId, "No Account Project", null);
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert
            var projection = _dbContext.ProjectProjections.FirstOrDefault(p => p.Id == projectId);
            projection.Should().NotBeNull("projection should be created even with null account_id");
            projection.AccountId.Should().BeNull("null account_id in NewRecord should result in null AccountId");
        }

        #endregion

        #region AccountId Cascade Tests — TimelogProjection

        /// <summary>
        /// Verifies that when a project's account_id changes, the consumer cascades the
        /// new AccountId to all TimelogProjection records referencing that project.
        /// This preserves the monolith's dynamic join behavior from ReportService.GetTimelogData()
        /// (source lines 79-96) where timelogs are filtered by account_id through the
        /// timelog → task → project → account chain. In the microservice, this relationship
        /// is denormalized in TimelogProjection.AccountId.
        ///
        /// Also verifies that TimelogProjection records referencing a different project
        /// are NOT affected by the cascade.
        /// </summary>
        [Fact]
        public async Task Consume_ShouldCascadeAccountIdToTimelogProjections()
        {
            // Arrange — seed TimelogProjection records
            var projectId = Guid.NewGuid();
            var otherProjectId = Guid.NewGuid();
            var oldAccountId = Guid.NewGuid();
            var newAccountId = Guid.NewGuid();

            // Two timelogs linked to the target project
            _dbContext.TimelogProjections.Add(new TimelogProjection
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                ProjectId = projectId,
                AccountId = oldAccountId,
                IsBillable = true,
                Minutes = 60,
                LoggedOn = DateTime.UtcNow.AddDays(-1),
                Scope = "projects",
                CreatedOn = DateTime.UtcNow.AddDays(-2),
                LastModifiedOn = DateTime.UtcNow.AddDays(-2)
            });
            _dbContext.TimelogProjections.Add(new TimelogProjection
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                ProjectId = projectId,
                AccountId = oldAccountId,
                IsBillable = false,
                Minutes = 30,
                LoggedOn = DateTime.UtcNow.AddDays(-1),
                Scope = "projects",
                CreatedOn = DateTime.UtcNow.AddDays(-2),
                LastModifiedOn = DateTime.UtcNow.AddDays(-2)
            });

            // One timelog linked to a different project — should NOT be affected
            var unaffectedTimelogId = Guid.NewGuid();
            _dbContext.TimelogProjections.Add(new TimelogProjection
            {
                Id = unaffectedTimelogId,
                TaskId = Guid.NewGuid(),
                ProjectId = otherProjectId,
                AccountId = oldAccountId,
                IsBillable = true,
                Minutes = 90,
                LoggedOn = DateTime.UtcNow.AddDays(-1),
                Scope = "projects",
                CreatedOn = DateTime.UtcNow.AddDays(-2),
                LastModifiedOn = DateTime.UtcNow.AddDays(-2)
            });
            await _dbContext.SaveChangesAsync();

            var evt = CreateProjectUpdatedEvent(projectId, "Cascade Project", newAccountId);
            var context = CreateConsumeContext(evt);

            // Act
            await _consumer.Consume(context.Object);

            // Assert — affected timelogs should have updated AccountId
            var affectedTimelogs = _dbContext.TimelogProjections
                .Where(t => t.ProjectId == projectId)
                .ToList();
            affectedTimelogs.Should().HaveCount(2, "both timelogs linked to the project should exist");
            foreach (var timelog in affectedTimelogs)
            {
                timelog.AccountId.Should().Be(newAccountId,
                    "AccountId should be cascaded from the project update to related TimelogProjections");
            }

            // Assert — unaffected timelog should retain old AccountId
            var unaffectedTimelog = _dbContext.TimelogProjections
                .FirstOrDefault(t => t.Id == unaffectedTimelogId);
            unaffectedTimelog.Should().NotBeNull();
            unaffectedTimelog.AccountId.Should().Be(oldAccountId,
                "TimelogProjection linked to a different project must not be affected by the cascade");
        }

        /// <summary>
        /// Verifies that the consumer does not fail when there are no TimelogProjection
        /// records referencing the updated project. The cascade step should simply do nothing
        /// and the ProjectProjection should still be created successfully.
        /// </summary>
        [Fact]
        public async Task Consume_NoRelatedTimelogs_ShouldNotFail()
        {
            // Arrange — no TimelogProjection records seeded
            var projectId = Guid.NewGuid();
            var accountId = Guid.NewGuid();
            var evt = CreateProjectUpdatedEvent(projectId, "Solo Project", accountId);
            var context = CreateConsumeContext(evt);

            // Act — should not throw
            await _consumer.Consume(context.Object);

            // Assert
            _dbContext.ProjectProjections.Count().Should().Be(1,
                "ProjectProjection should be created even when no related timelogs exist");
            var projection = _dbContext.ProjectProjections.First();
            projection.Id.Should().Be(projectId);
            projection.Name.Should().Be("Solo Project");
            projection.AccountId.Should().Be(accountId);
        }

        #endregion
    }
}
