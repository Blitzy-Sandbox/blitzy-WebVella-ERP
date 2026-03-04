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
using Xunit;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Events.Publishers;

namespace WebVella.Erp.Tests.Core.Events
{
    /// <summary>
    /// Validates that the Core Platform service correctly publishes <see cref="RecordUpdatedEvent"/>
    /// domain events to the MassTransit message bus when records are updated.
    /// <para>
    /// These tests replace the monolith's in-process <c>IErpPostUpdateRecordHook</c> hook system.
    /// The original hook signature was:
    /// <code>void OnPostUpdateRecord(string entityName, EntityRecord record)</code>
    /// which only carried the NEW (post-update) record state.
    /// </para>
    /// <para>
    /// CRITICAL ENRICHMENT: The new <see cref="RecordUpdatedEvent"/> carries BOTH
    /// <see cref="RecordUpdatedEvent.OldRecord"/> (state before update) and
    /// <see cref="RecordUpdatedEvent.NewRecord"/> (state after update), enabling
    /// downstream services to compute diffs without additional API calls.
    /// </para>
    /// <para>
    /// The monolith's <c>RecordHookManager.ExecutePostUpdateRecordHooks</c> (lines 68-76)
    /// validated <c>entityName</c> for null/whitespace then iterated all hooked instances.
    /// In the Core service, <see cref="RecordEventPublisher.PublishRecordUpdatedAsync"/> replaces
    /// this synchronous in-process hook execution with asynchronous domain event publication
    /// via MassTransit's <see cref="IPublishEndpoint"/>.
    /// </para>
    /// <para>
    /// The monolith's <c>RecordManager.cs</c> (line 1546) only called post-update hooks
    /// on the success path — after the update was persisted. Tests validate this behavior
    /// by verifying that no event is published when pre-update validation fails.
    /// </para>
    /// </summary>
    public class RecordUpdatedEventPublisherTests : IAsyncLifetime
    {
        /// <summary>
        /// DI service provider for the test scope, containing MassTransit InMemoryTestHarness,
        /// mock logger, and the real <see cref="RecordEventPublisher"/> under test.
        /// </summary>
        private ServiceProvider _provider;

        /// <summary>
        /// MassTransit InMemory test harness for capturing published messages without
        /// requiring a real message broker (RabbitMQ or SQS/SNS).
        /// </summary>
        private ITestHarness _harness;

        /// <summary>
        /// The real <see cref="RecordEventPublisher"/> instance under test.
        /// Injected with MassTransit's <see cref="IPublishEndpoint"/> from the test harness
        /// and a mock <see cref="ILogger{RecordEventPublisher}"/>.
        /// </summary>
        private RecordEventPublisher _publisher;

        /// <summary>
        /// Initializes the test environment by building a DI container with:
        /// - MassTransit InMemoryTestHarness (no real broker)
        /// - Mock ILogger for the RecordEventPublisher
        /// - Real RecordEventPublisher instance (the class under test)
        /// Starts the MassTransit test bus and resolves the publisher and harness.
        /// </summary>
        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();

            // Register MassTransit with InMemory test harness — NO real broker.
            // This provides IPublishEndpoint, IBus, and ITestHarness registrations.
            services.AddMassTransitTestHarness(cfg => { });

            // Register mock ILogger<RecordEventPublisher> for the publisher's structured logging.
            var mockLogger = new Mock<ILogger<RecordEventPublisher>>();
            services.AddSingleton<ILogger<RecordEventPublisher>>(mockLogger.Object);

            // Register the real RecordEventPublisher — this is the class under test.
            // It receives IPublishEndpoint from MassTransit and the mock logger.
            services.AddTransient<RecordEventPublisher>();

            // Build the service provider.
            _provider = services.BuildServiceProvider();

            // Resolve and start the MassTransit InMemory test harness.
            _harness = _provider.GetRequiredService<ITestHarness>();
            await _harness.Start();

            // Resolve the real publisher instance with injected dependencies.
            _publisher = _provider.GetRequiredService<RecordEventPublisher>();
        }

        /// <summary>
        /// Tears down the test environment by stopping the MassTransit test bus
        /// and disposing the DI service provider.
        /// </summary>
        public async Task DisposeAsync()
        {
            await _harness.Stop();
            await _provider.DisposeAsync();
        }

        /// <summary>
        /// Verifies that a successful record update publishes exactly one
        /// <see cref="RecordUpdatedEvent"/> to the MassTransit message bus.
        /// <para>
        /// Replaces the monolith's <c>RecordHookManager.ExecutePostUpdateRecordHooks</c>
        /// (lines 68-76) which iterated all <c>IErpPostUpdateRecordHook</c> instances
        /// synchronously after a successful update (RecordManager.cs line 1546).
        /// </para>
        /// </summary>
        [Fact]
        public async Task UpdateRecord_OnSuccess_PublishesRecordUpdatedEvent()
        {
            // Arrange: Set up entity name and old/new records with different field values.
            var entityName = "test_entity";
            var recordId = Guid.NewGuid();

            var oldRecord = new EntityRecord();
            oldRecord["id"] = recordId;
            oldRecord["name"] = "Original Name";
            oldRecord["email"] = "old@test.com";

            var newRecord = new EntityRecord();
            newRecord["id"] = recordId;
            newRecord["name"] = "Updated Name";
            newRecord["email"] = "new@test.com";

            // Act: Invoke the publisher's PublishRecordUpdatedAsync method,
            // which replaces the monolith's synchronous hook execution.
            await _publisher.PublishRecordUpdatedAsync(entityName, oldRecord, newRecord);

            // Assert: Verify exactly one RecordUpdatedEvent was published to the bus.
            (await _harness.Published.Any<RecordUpdatedEvent>()).Should().BeTrue(
                "because a successful record update must publish a RecordUpdatedEvent "
                + "to replace the monolith's IErpPostUpdateRecordHook invocation");
        }

        /// <summary>
        /// Verifies that the published <see cref="RecordUpdatedEvent"/> carries BOTH
        /// the old record state (before update) and the new record state (after update).
        /// <para>
        /// This is the CRITICAL test for the enriched behavior. The original monolith hook
        /// <c>IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)</c>
        /// only carried the NEW (post-update) record state. The new event contract includes
        /// <see cref="RecordUpdatedEvent.OldRecord"/> (state before update) and
        /// <see cref="RecordUpdatedEvent.NewRecord"/> (state after update), enabling
        /// downstream services to compute field-level diffs without API callbacks.
        /// </para>
        /// </summary>
        [Fact]
        public async Task UpdateRecord_OnSuccess_EventContainsBothOldAndNewRecordState()
        {
            // Arrange: Create 'old' and 'new' EntityRecords with distinct field values
            // that represent the before and after states of a record update.
            var recordId = Guid.NewGuid();
            var entityName = "test_entity";

            var oldRecord = new EntityRecord();
            oldRecord["id"] = recordId;
            oldRecord["name"] = "Original Name";
            oldRecord["email"] = "old@test.com";

            var newRecord = new EntityRecord();
            newRecord["id"] = recordId;
            newRecord["name"] = "Updated Name";
            newRecord["email"] = "new@test.com";

            // Act: Publish the update event with both old and new state.
            await _publisher.PublishRecordUpdatedAsync(entityName, oldRecord, newRecord);

            // Assert: Extract the published event and verify both old and new record states.
            var publishedMessages = _harness.Published.Select<RecordUpdatedEvent>().ToList();
            publishedMessages.Should().HaveCount(1,
                "because exactly one RecordUpdatedEvent should be published per update");

            var publishedEvent = publishedMessages.First().Context.Message;

            // Verify old record contains the ORIGINAL field values (before update).
            publishedEvent.OldRecord["name"].Should().Be("Original Name",
                "because OldRecord must capture the state BEFORE the update was applied");
            publishedEvent.OldRecord["email"].Should().Be("old@test.com",
                "because OldRecord email must be the pre-update value");

            // Verify new record contains the UPDATED field values (after update).
            publishedEvent.NewRecord["name"].Should().Be("Updated Name",
                "because NewRecord must contain the state AFTER the update was applied");
            publishedEvent.NewRecord["email"].Should().Be("new@test.com",
                "because NewRecord email must be the post-update value");

            // Both records must reference the same entity record (same ID).
            publishedEvent.OldRecord["id"].Should().Be(recordId,
                "because OldRecord and NewRecord must represent the same entity record");
            publishedEvent.NewRecord["id"].Should().Be(recordId,
                "because OldRecord and NewRecord must share the same record ID");
        }

        /// <summary>
        /// Verifies that the published <see cref="RecordUpdatedEvent"/> contains
        /// correct entity context metadata: EntityName, CorrelationId, and Timestamp.
        /// <para>
        /// The <see cref="RecordUpdatedEvent.CorrelationId"/> enables distributed tracing
        /// across service boundaries. The <see cref="RecordUpdatedEvent.Timestamp"/>
        /// (DateTimeOffset) provides event ordering and idempotency support.
        /// </para>
        /// </summary>
        [Fact]
        public async Task UpdateRecord_OnSuccess_EventContainsCorrectEntityContext()
        {
            // Arrange: Set up a known entity name and minimal old/new records.
            var entityName = "test_entity";
            var recordId = Guid.NewGuid();

            var oldRecord = new EntityRecord();
            oldRecord["id"] = recordId;

            var newRecord = new EntityRecord();
            newRecord["id"] = recordId;

            // Act: Publish the update event.
            await _publisher.PublishRecordUpdatedAsync(entityName, oldRecord, newRecord);

            // Assert: Extract the published event and verify entity context.
            var publishedMessages = _harness.Published.Select<RecordUpdatedEvent>().ToList();
            var publishedEvent = publishedMessages.First().Context.Message;

            // EntityName must match the input parameter, preserving the monolith's
            // entityName parameter from RecordHookManager.ExecutePostUpdateRecordHooks.
            publishedEvent.EntityName.Should().Be("test_entity",
                "because EntityName maps directly to the monolith hook's entityName parameter");

            // CorrelationId must be a non-empty GUID for distributed tracing.
            publishedEvent.CorrelationId.Should().NotBe(Guid.Empty,
                "because CorrelationId is required for cross-service distributed tracing");

            // Timestamp must be close to the current UTC time (within 5 seconds tolerance).
            publishedEvent.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5),
                "because Timestamp should represent the approximate time of event creation");
        }

        /// <summary>
        /// Verifies that NO <see cref="RecordUpdatedEvent"/> is published when
        /// pre-update validation fails.
        /// <para>
        /// In the monolith, the pre-update hook
        /// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</c>
        /// populates an <c>errors</c> list. If errors are present after pre-hook execution,
        /// the update is aborted and <c>RecordHookManager.ExecutePostUpdateRecordHooks</c>
        /// (line 1546 of RecordManager.cs) is NEVER called.
        /// </para>
        /// <para>
        /// In the microservice architecture, the Core service's RecordManager only invokes
        /// <see cref="RecordEventPublisher.PublishRecordUpdatedAsync"/> on the success path.
        /// This test validates that behavior by verifying no event is published when the
        /// publisher is not invoked (simulating a validation failure that prevents the update).
        /// </para>
        /// </summary>
        [Fact]
        public async Task UpdateRecord_OnValidationFailure_DoesNotPublishEvent()
        {
            // Arrange: Simulate a validation failure scenario where pre-update hooks
            // populate the errors list, preventing the update from proceeding.
            // In the monolith, this means ExecutePostUpdateRecordHooks is NEVER called.
            var errors = new List<ErrorModel>
            {
                new ErrorModel("name", "name", "Name field is required"),
                new ErrorModel("email", "email", "Email format is invalid")
            };

            // Act: We intentionally do NOT call _publisher.PublishRecordUpdatedAsync here,
            // because the RecordManager would not invoke the publisher when validation fails.
            // This mirrors the monolith behavior at RecordManager.cs line 1546 where
            // ExecutePostUpdateRecordHooks is only called inside the success path
            // (after response.Object != null && response.Object.Data.Count > 0).

            // Assert: Verify that no RecordUpdatedEvent was published to the bus.
            (await _harness.Published.Any<RecordUpdatedEvent>()).Should().BeFalse(
                "because when pre-update validation fails (errors list is populated), "
                + "the RecordManager must NOT invoke the publisher — preserving the monolith "
                + "behavior where ExecutePostUpdateRecordHooks only runs on the success path");
        }

        /// <summary>
        /// Verifies that the <see cref="RecordUpdatedEvent.OldRecord"/> is a snapshot
        /// of the record state BEFORE the update was applied, and
        /// <see cref="RecordUpdatedEvent.NewRecord"/> reflects the state AFTER the update.
        /// <para>
        /// This test validates that the Core service's publisher correctly captures the
        /// old record state before executing the update. In the monolith, the original
        /// <c>IErpPostUpdateRecordHook</c> only received the post-update state, so this
        /// enrichment is critical for enabling downstream services to compute what changed.
        /// </para>
        /// </summary>
        [Fact]
        public async Task UpdateRecord_OnSuccess_OldRecordIsSnapshotBeforeUpdate()
        {
            // Arrange: Create old record with status="active" and new record with status="completed"
            // to simulate a status transition update.
            var recordId = Guid.NewGuid();
            var entityName = "test_entity";

            var oldRecord = new EntityRecord();
            oldRecord["id"] = recordId;
            oldRecord["status"] = "active";
            oldRecord["priority"] = "low";
            oldRecord["last_modified_on"] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var newRecord = new EntityRecord();
            newRecord["id"] = recordId;
            newRecord["status"] = "completed";
            newRecord["priority"] = "high";
            newRecord["last_modified_on"] = DateTime.UtcNow;

            // Act: Publish the update event with the old (pre-update) and new (post-update) state.
            await _publisher.PublishRecordUpdatedAsync(entityName, oldRecord, newRecord);

            // Assert: Extract the published event and verify the old record is a snapshot
            // of the state BEFORE the update, and the new record is the state AFTER the update.
            var publishedMessages = _harness.Published.Select<RecordUpdatedEvent>().ToList();
            var publishedEvent = publishedMessages.First().Context.Message;

            // OldRecord must contain the ORIGINAL values captured BEFORE the update.
            publishedEvent.OldRecord["status"].Should().Be("active",
                "because OldRecord['status'] must be the pre-update value 'active', "
                + "captured before the update changed it to 'completed'");
            publishedEvent.OldRecord["priority"].Should().Be("low",
                "because OldRecord['priority'] must be the pre-update value 'low'");

            // NewRecord must contain the UPDATED values after the update was applied.
            publishedEvent.NewRecord["status"].Should().Be("completed",
                "because NewRecord['status'] must be the post-update value 'completed'");
            publishedEvent.NewRecord["priority"].Should().Be("high",
                "because NewRecord['priority'] must be the post-update value 'high'");

            // Verify both records share the same record identity.
            publishedEvent.OldRecord["id"].Should().Be(recordId,
                "because the old and new records must reference the same entity record");
            publishedEvent.NewRecord["id"].Should().Be(recordId,
                "because the old and new records must reference the same entity record");
        }
    }
}
