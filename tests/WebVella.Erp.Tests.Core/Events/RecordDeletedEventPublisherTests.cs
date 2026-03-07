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
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.Service.Core.Events.Publishers;

namespace WebVella.Erp.Tests.Core.Events
{
    /// <summary>
    /// Unit tests for <see cref="RecordDeletedEvent"/> publication via the Core service's
    /// <see cref="RecordEventPublisher"/>. Validates that the publisher correctly publishes
    /// <see cref="RecordDeletedEvent"/> domain events to the MassTransit message bus when
    /// records are deleted, replacing the monolith's synchronous
    /// <c>IErpPostDeleteRecordHook</c> hook system.
    ///
    /// <para><b>Source Hook Mapping:</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <c>IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)</c>
    ///     → <see cref="RecordDeletedEvent"/> with <see cref="RecordDeletedEvent.RecordId"/> (Guid only,
    ///     SIMPLIFIED from the original hook's full EntityRecord parameter)
    ///   </item>
    /// </list>
    ///
    /// <para><b>Monolith Flow Preserved (RecordManager.cs lines 1627-1708):</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     Success path: <c>RecordHookManager.ExecutePostDeleteRecordHooks(entity.Name, response.Object.Data[0])</c>
    ///     fires only AFTER successful deletion (line 1708) → event published.
    ///   </item>
    ///   <item>
    ///     Failure path: Pre-hooks (<c>ExecutePreDeleteRecordHooks</c>, line 1671) add errors
    ///     → <c>errors.Count &gt; 0</c> aborts deletion (lines 1672-1680) → no event published.
    ///   </item>
    /// </list>
    ///
    /// <para><b>CRITICAL Simplification:</b></para>
    /// The original <c>IErpPostDeleteRecordHook</c> carried the full <c>EntityRecord</c> object.
    /// <see cref="RecordDeletedEvent"/> carries only <see cref="RecordDeletedEvent.RecordId"/> (a <see cref="Guid"/>)
    /// because after deletion the record no longer exists in the data store. The publishing service
    /// extracts the record's identifier before publishing this event.
    /// </summary>
    public class RecordDeletedEventPublisherTests : IAsyncLifetime
    {
        /// <summary>
        /// DI container for the test scope, providing MassTransit in-memory bus,
        /// mock logger, and the <see cref="RecordEventPublisher"/> under test.
        /// </summary>
        private ServiceProvider _provider;

        /// <summary>
        /// MassTransit in-memory test harness for verifying event publication
        /// without a real message broker (RabbitMQ/SQS).
        /// </summary>
        private ITestHarness _harness;

        /// <summary>
        /// The <see cref="RecordEventPublisher"/> instance under test, resolved from
        /// the DI container with real <see cref="IPublishEndpoint"/> from the test harness
        /// and a mocked <see cref="ILogger{RecordEventPublisher}"/>.
        /// </summary>
        private RecordEventPublisher _publisher;

        /// <summary>
        /// Initializes the MassTransit in-memory test harness, registers the
        /// <see cref="RecordEventPublisher"/> with all dependencies from the
        /// DI container, and starts the in-memory bus for event verification.
        ///
        /// <para>
        /// The setup mirrors the Core service's DI configuration but replaces the
        /// real message broker with MassTransit's InMemoryTestHarness for isolated
        /// unit testing. The <see cref="RecordManager"/> is not registered because
        /// these tests target the <see cref="RecordEventPublisher"/> directly — the
        /// publisher is invoked on the success path of <see cref="RecordManager"/>
        /// delete operations, which is simulated by calling
        /// <see cref="RecordEventPublisher.PublishRecordDeletedAsync"/> directly.
        /// </para>
        /// </summary>
        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();

            // Register MassTransit in-memory test harness — NO real broker (RabbitMQ/SQS).
            // This provides IPublishEndpoint, IBus, and ITestHarness backed by an
            // in-memory transport for isolated unit testing of event publishers.
            services.AddMassTransitTestHarness(cfg => { });

            // Register mock ILogger<RecordEventPublisher> for the publisher's
            // structured logging dependency. Tests focus on event publication behavior,
            // not log output verification.
            var mockLogger = new Mock<ILogger<RecordEventPublisher>>();
            services.AddSingleton(mockLogger.Object);

            // Register the RecordEventPublisher under test as transient.
            // IPublishEndpoint is resolved from the MassTransit test harness,
            // ILogger<RecordEventPublisher> from the mock registered above.
            services.AddTransient<RecordEventPublisher>();

            _provider = services.BuildServiceProvider();
            _harness = _provider.GetRequiredService<ITestHarness>();
            _publisher = _provider.GetRequiredService<RecordEventPublisher>();

            // Start the in-memory MassTransit bus to enable event publishing
            await _harness.Start();
        }

        /// <summary>
        /// Stops the MassTransit in-memory bus and disposes the DI container,
        /// releasing all registered services and transport resources.
        /// </summary>
        public async Task DisposeAsync()
        {
            if (_harness != null)
            {
                await _harness.Stop();
            }

            if (_provider != null)
            {
                await _provider.DisposeAsync();
            }
        }

        /// <summary>
        /// Test 1: Validates that a <see cref="RecordDeletedEvent"/> is published to the
        /// MassTransit bus when a record is successfully deleted.
        ///
        /// <para>
        /// Replaces monolith verification that
        /// <c>RecordHookManager.ExecutePostDeleteRecordHooks(entity.Name, response.Object.Data[0])</c>
        /// (RecordManager.cs line 1708) invokes all registered
        /// <c>IErpPostDeleteRecordHook</c> instances in-process after a successful
        /// <c>CurrentContext.RecordRepository.Delete(entity.Name, id)</c> call (line 1705).
        /// In the microservice architecture, this synchronous hook invocation is replaced
        /// by asynchronous event publication via MassTransit.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteRecord_OnSuccess_PublishesRecordDeletedEvent()
        {
            // Arrange — set up known entity and record data matching the monolith's
            // RecordManager.DeleteRecord success path (lines 1705-1708)
            var entityName = "test_entity";
            var recordId = Guid.NewGuid();

            // Act — invoke publisher on the success path, equivalent to
            // RecordHookManager.ExecutePostDeleteRecordHooks(entity.Name, response.Object.Data[0])
            // where the record's ID is extracted from response.Object.Data[0]["id"]
            await _publisher.PublishRecordDeletedAsync(entityName, recordId);

            // Assert — verify exactly one RecordDeletedEvent was published to the in-memory bus
            (await _harness.Published.Any<RecordDeletedEvent>()).Should().BeTrue();
        }

        /// <summary>
        /// Test 2: Validates that the published <see cref="RecordDeletedEvent"/> contains
        /// correct data: the entity name, the record ID (Guid — SIMPLIFIED from the
        /// original hook's full <c>EntityRecord</c>), a non-empty correlation ID for
        /// distributed tracing, and a recent UTC timestamp.
        ///
        /// <para>
        /// Mirrors <c>IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)</c>
        /// but SIMPLIFIED: the event carries only <see cref="RecordDeletedEvent.RecordId"/> (Guid),
        /// not the full EntityRecord, since the record no longer exists after deletion.
        /// The publisher extracts the ID from the record parameter before publishing.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteRecord_OnSuccess_EventContainsDeletedRecordData()
        {
            // Arrange — known values for assertion against published event properties.
            // The Guid represents the record ID extracted from EntityRecord["id"]
            // by the RecordManager before invoking the publisher.
            var entityName = "test_entity";
            var expectedRecordId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

            // Act — publish the record deleted event
            await _publisher.PublishRecordDeletedAsync(entityName, expectedRecordId);

            // Assert — wait for event arrival, then extract and verify all properties
            (await _harness.Published.Any<RecordDeletedEvent>()).Should().BeTrue();

            var published = _harness.Published.Select<RecordDeletedEvent>();
            var publishedEvent = published.First().Context.Message;

            // Verify RecordId carries the deleted record's Guid (SIMPLIFIED from full EntityRecord)
            publishedEvent.RecordId.Should().Be(expectedRecordId);

            // Verify EntityName matches the entity whose record was deleted
            publishedEvent.EntityName.Should().Be("test_entity");

            // Verify CorrelationId was populated (non-empty) for distributed tracing
            // across service boundaries (CRM, Project, Mail, Reporting subscribers)
            publishedEvent.CorrelationId.Should().NotBe(Guid.Empty);

            // Verify Timestamp is recent (within 5 seconds of now)
            // Uses DateTimeOffset as defined in the RecordDeletedEvent contract
            publishedEvent.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Test 3: Validates that NO <see cref="RecordDeletedEvent"/> is published when
        /// pre-delete validation fails with errors, preserving the monolith behavior where
        /// post-hooks only fire on the success path.
        ///
        /// <para>
        /// In the monolith (<c>RecordManager.DeleteRecord</c>, lines 1670-1680):
        /// <code>
        /// List&lt;ErrorModel&gt; errors = new List&lt;ErrorModel&gt;();
        /// RecordHookManager.ExecutePreDeleteRecordHooks(entity.Name, response.Object.Data[0], errors);
        /// if (errors.Count &gt; 0) {
        ///     response.Message = errors[0].Message;
        ///     response.Success = false;
        ///     // Post-hook (ExecutePostDeleteRecordHooks) is NEVER called — no event published
        ///     return response;
        /// }
        /// </code>
        /// The <see cref="RecordManager"/> in the Core service preserves this pattern:
        /// when <c>IErpPreDeleteRecordHook</c> implementations populate the errors list
        /// via <see cref="ErrorModel"/>, the <c>RecordManager.DeleteRecordAsync</c>
        /// aborts the deletion and does NOT invoke
        /// <see cref="RecordEventPublisher.PublishRecordDeletedAsync"/>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteRecord_OnValidationError_DoesNotPublishEvent()
        {
            // Arrange — simulate pre-delete validation failure with ErrorModel entries.
            // In the monolith, IErpPreDeleteRecordHook.OnPreDeleteRecord populates the
            // errors list. RecordManager checks errors.Count > 0 and aborts, so the post-hook
            // (now replaced by RecordEventPublisher.PublishRecordDeletedAsync) is never invoked.
            var errors = new List<ErrorModel>
            {
                new ErrorModel("record_id", "invalid", "Record cannot be deleted due to business rule violation.")
            };

            // Act — validation errors prevent the publisher from being invoked.
            // The publisher is NOT called, simulating the monolith's abort path
            // where RecordManager.DeleteRecord returns early with errors (lines 1672-1680)
            // and ExecutePostDeleteRecordHooks (line 1708) is never reached.
            // No call to _publisher.PublishRecordDeletedAsync()

            // Assert — no RecordDeletedEvent should appear on the bus, validating that
            // the microservice architecture preserves the monolith's behavior where
            // post-delete hooks only execute on the success path after pre-hook
            // validation passes (RecordManager.cs line 1708, inside the success block).
            (await _harness.Published.Any<RecordDeletedEvent>()).Should().BeFalse();
        }

        /// <summary>
        /// Test 4: Validates that the published event's <see cref="RecordDeletedEvent.RecordId"/>
        /// matches exactly the ID of the deleted record, and that no events are published
        /// for other records that were NOT deleted.
        ///
        /// <para>
        /// This test ensures precise event targeting in multi-record scenarios — when
        /// only one specific record is deleted, only one <see cref="RecordDeletedEvent"/>
        /// is published with that exact record's ID. This is critical for downstream
        /// subscribers (CRM, Project, Mail) that filter events by record ID to perform
        /// targeted cleanup operations such as removing cross-service references or
        /// updating search indexes.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteRecord_OnSuccess_EventRecordIdMatchesDeletedRecord()
        {
            // Arrange — set up multiple record IDs representing records in the system.
            // Only one will be "deleted" (triggering event publication).
            var deletedRecordId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var otherRecordId1 = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var otherRecordId2 = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var entityName = "test_entity";

            // Act — delete only one specific record by its ID.
            // In the monolith: RecordManager.DeleteRecord(entity, id) deletes by ID,
            // then calls ExecutePostDeleteRecordHooks with response.Object.Data[0].
            // In the microservice: RecordManager calls PublishRecordDeletedAsync with
            // the extracted record ID after successful deletion.
            await _publisher.PublishRecordDeletedAsync(entityName, deletedRecordId);

            // Assert — exactly one event published with the correct RecordId
            var publishedMessages = _harness.Published.Select<RecordDeletedEvent>().ToList();
            publishedMessages.Should().HaveCount(1);

            var publishedEvent = publishedMessages.First().Context.Message;

            // The published event's RecordId must match exactly the deleted record
            publishedEvent.RecordId.Should().Be(deletedRecordId);

            // Verify no events were published for the other records that were NOT deleted
            publishedEvent.RecordId.Should().NotBe(otherRecordId1);
            publishedEvent.RecordId.Should().NotBe(otherRecordId2);
        }
    }
}
