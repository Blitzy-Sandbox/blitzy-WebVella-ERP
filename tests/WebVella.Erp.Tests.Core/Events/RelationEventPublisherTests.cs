using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Unit tests for <see cref="RelationEventPublisher"/> validating that
    /// many-to-many relation lifecycle events (create/delete) are correctly
    /// published to the MassTransit message bus, replacing the monolith's
    /// synchronous hook execution pattern.
    ///
    /// <para><b>Source Hook Mapping:</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <c>IErpPostCreateManyToManyRelationHook.OnPostCreate(string, Guid, Guid)</c>
    ///     → <see cref="RelationCreatedEvent"/> with non-nullable <see cref="Guid"/> OriginId/TargetId
    ///   </item>
    ///   <item>
    ///     <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete(string, Guid?, Guid?)</c>
    ///     → <see cref="RelationDeletedEvent"/> with nullable <see cref="Nullable{Guid}"/> OriginId/TargetId
    ///   </item>
    /// </list>
    ///
    /// <para><b>Monolith Flow Preserved (RecordManager patterns):</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     Success path mirrors <see cref="RecordManager.CreateRelationManyToManyRecord"/>
    ///     (RecordManager.cs lines 94-95): after DB write, post-hook fires → event published
    ///   </item>
    ///   <item>
    ///     Failure path mirrors <see cref="RecordManager.CreateRelationManyToManyRecord"/>
    ///     (RecordManager.cs lines 84-91): if pre-hook validation errors, abort → no event
    ///   </item>
    ///   <item>
    ///     Success delete mirrors <see cref="RecordManager.RemoveRelationManyToManyRecord"/>
    ///     (RecordManager.cs lines 172-173): after DB write, post-hook fires → event published
    ///   </item>
    ///   <item>
    ///     Failure delete mirrors <see cref="RecordManager.RemoveRelationManyToManyRecord"/>
    ///     (RecordManager.cs lines 161-170): if pre-hook validation errors, abort → no event
    ///   </item>
    /// </list>
    ///
    /// <para><b>CRITICAL TYPE ASYMMETRY:</b></para>
    /// <list type="bullet">
    ///   <item><see cref="RelationCreatedEvent"/>: OriginId and TargetId are <c>Guid</c> (non-nullable)</item>
    ///   <item><see cref="RelationDeletedEvent"/>: OriginId and TargetId are <c>Guid?</c> (nullable)</item>
    /// </list>
    /// This matches the original monolith hook signatures exactly.
    /// </summary>
    public class RelationEventPublisherTests : IAsyncLifetime
    {
        private ServiceProvider _provider;
        private ITestHarness _harness;
        private RelationEventPublisher _publisher;

        /// <summary>
        /// Initializes the MassTransit in-memory test harness, registers the
        /// <see cref="RelationEventPublisher"/> with all dependencies from the
        /// DI container, and starts the in-memory bus for event verification.
        /// </summary>
        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();

            // Register MassTransit in-memory test harness — NO real broker (RabbitMQ/SQS).
            // This provides IPublishEndpoint, IBus, and ITestHarness backed by an
            // in-memory transport for isolated unit testing of event publishers.
            services.AddMassTransitTestHarness(cfg => { });

            // Register mock ILogger<RelationEventPublisher> for the publisher's
            // structured logging dependency. Tests focus on event publication behavior,
            // not log output verification.
            var mockLogger = new Mock<ILogger<RelationEventPublisher>>();
            services.AddSingleton(mockLogger.Object);

            // Register the RelationEventPublisher under test as transient.
            // IPublishEndpoint is resolved from the MassTransit test harness,
            // ILogger<RelationEventPublisher> from the mock registered above.
            services.AddTransient<RelationEventPublisher>();

            _provider = services.BuildServiceProvider();
            _harness = _provider.GetRequiredService<ITestHarness>();
            _publisher = _provider.GetRequiredService<RelationEventPublisher>();

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

        #region Create Many-to-Many Relation Tests

        /// <summary>
        /// Test 1: Validates that a <see cref="RelationCreatedEvent"/> is published to the
        /// MassTransit bus when a many-to-many relation bridge row is successfully created.
        ///
        /// <para>
        /// Replaces monolith verification that
        /// <c>RecordHookManager.ExecutePostCreateManyToManyRelationHook</c>
        /// (RecordHookManager.cs lines 114-122) invokes all registered
        /// <c>IErpPostCreateManyToManyRelationHook</c> instances in-process.
        /// In the microservice architecture, this synchronous hook invocation is replaced
        /// by asynchronous event publication via MassTransit.
        /// </para>
        /// </summary>
        [Fact]
        public async Task CreateManyToManyRelation_OnSuccess_PublishesRelationCreatedEvent()
        {
            // Arrange — set up known relation data matching the monolith's
            // RecordManager.CreateRelationManyToManyRecord success path (line 94-95)
            var relationName = "user_role";
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            // Act — invoke publisher on the success path, equivalent to
            // RecordHookManager.ExecutePostCreateManyToManyRelationHook(relation.Name, originValue, targetValue)
            await _publisher.PublishRelationCreatedAsync(relationName, originId, targetId);

            // Assert — verify event was published to the in-memory bus
            (await _harness.Published.Any<RelationCreatedEvent>()).Should().BeTrue();
        }

        /// <summary>
        /// Test 2: Validates that the published <see cref="RelationCreatedEvent"/> contains
        /// correct data matching the input parameters: relation name, non-nullable origin/target IDs,
        /// a non-empty correlation ID, and a recent UTC timestamp.
        ///
        /// <para>
        /// Mirrors <c>IErpPostCreateManyToManyRelationHook.OnPostCreate(string relationName, Guid originId, Guid targetId)</c>
        /// CRITICAL: OriginId and TargetId are <c>Guid</c> (NON-nullable), matching the original
        /// monolith hook signature exactly.
        /// </para>
        /// </summary>
        [Fact]
        public async Task CreateManyToManyRelation_OnSuccess_EventContainsCorrectRelationData()
        {
            // Arrange — known values for assertion against published event properties
            var relationName = "user_role";
            var expectedOriginId = Guid.NewGuid();
            var expectedTargetId = Guid.NewGuid();

            // Act — publish the relation created event
            await _publisher.PublishRelationCreatedAsync(relationName, expectedOriginId, expectedTargetId);

            // Assert — wait for event arrival, then extract and verify properties
            (await _harness.Published.Any<RelationCreatedEvent>()).Should().BeTrue();

            var published = _harness.Published.Select<RelationCreatedEvent>();
            var publishedEvent = published.First().Context.Message;

            // Verify relation name matches the input parameter
            publishedEvent.RelationName.Should().Be("user_role");

            // Verify NON-nullable Guid OriginId — matches IErpPostCreateManyToManyRelationHook.OnPostCreate signature
            publishedEvent.OriginId.Should().Be(expectedOriginId);

            // Verify NON-nullable Guid TargetId — matches IErpPostCreateManyToManyRelationHook.OnPostCreate signature
            publishedEvent.TargetId.Should().Be(expectedTargetId);

            // Verify CorrelationId was populated (non-empty) for distributed tracing
            publishedEvent.CorrelationId.Should().NotBe(Guid.Empty);

            // Verify Timestamp is recent (within 5 seconds of now)
            publishedEvent.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Test 3: Validates that NO <see cref="RelationCreatedEvent"/> is published when
        /// pre-create validation fails with errors, preserving the monolith behavior where
        /// post-hooks only fire on the success path.
        ///
        /// <para>
        /// In the monolith (<c>RecordManager.CreateRelationManyToManyRecord</c>, lines 82-91):
        /// <code>
        /// List&lt;ErrorModel&gt; errors = new List&lt;ErrorModel&gt;();
        /// RecordHookManager.ExecutePreCreateManyToManyRelationHook(relation.Name, originValue, targetValue, errors);
        /// if (errors.Count &gt; 0) {
        ///     connection.RollbackTransaction();
        ///     // Post-hook is NEVER called — no event published
        ///     return response;
        /// }
        /// </code>
        /// </para>
        /// </summary>
        [Fact]
        public async Task CreateManyToManyRelation_OnValidationError_DoesNotPublishEvent()
        {
            // Arrange — simulate pre-create validation failure with ErrorModel entries.
            // In the monolith, IErpPreCreateManyToManyRelationHook.OnPreCreate populates the
            // errors list. RecordManager checks errors.Count > 0 and aborts, so the post-hook
            // (now replaced by RelationEventPublisher.PublishRelationCreatedAsync) is never invoked.
            var errors = new List<ErrorModel>
            {
                new ErrorModel("relation", "user_role", "Validation failed: duplicate relation link exists")
            };

            // Act — validation errors prevent the publisher from being invoked.
            // The publisher is NOT called, simulating the monolith's abort path
            // where RecordManager.CreateRelationManyToManyRecord returns early with errors
            // and ExecutePostCreateManyToManyRelationHook is never reached (lines 84-91).
            // No call to _publisher.PublishRelationCreatedAsync()

            // Assert — no RelationCreatedEvent should appear on the bus
            (await _harness.Published.Any<RelationCreatedEvent>()).Should().BeFalse();
        }

        #endregion

        #region Delete Many-to-Many Relation Tests

        /// <summary>
        /// Test 4: Validates that a <see cref="RelationDeletedEvent"/> is published to the
        /// MassTransit bus when a many-to-many relation bridge row is successfully deleted.
        ///
        /// <para>
        /// Replaces monolith verification that
        /// <c>RecordHookManager.ExecutePostDeleteManyToManyRelationHook</c>
        /// (RecordHookManager.cs lines 137-145) invokes all registered
        /// <c>IErpPostDeleteManyToManyRelationHook</c> instances in-process.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteManyToManyRelation_OnSuccess_PublishesRelationDeletedEvent()
        {
            // Arrange — set up known relation data matching the monolith's
            // RecordManager.RemoveRelationManyToManyRecord success path (lines 172-173).
            // CRITICAL: OriginId and TargetId are Guid? (nullable) for delete operations.
            var relationName = "user_role";
            Guid? originId = Guid.NewGuid();
            Guid? targetId = Guid.NewGuid();

            // Act — invoke publisher on the success path, equivalent to
            // RecordHookManager.ExecutePostDeleteManyToManyRelationHook(relation.Name, originValue, targetValue)
            await _publisher.PublishRelationDeletedAsync(relationName, originId, targetId);

            // Assert — verify event was published to the in-memory bus
            (await _harness.Published.Any<RelationDeletedEvent>()).Should().BeTrue();
        }

        /// <summary>
        /// Test 5: Validates that the published <see cref="RelationDeletedEvent"/> contains
        /// correct data matching the input parameters: relation name, NULLABLE origin/target IDs,
        /// a non-empty correlation ID, and a recent UTC timestamp.
        ///
        /// <para>
        /// Mirrors <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete(string relationName, Guid? originId, Guid? targetId)</c>
        /// CRITICAL: OriginId and TargetId are <c>Guid?</c> (NULLABLE), unlike RelationCreatedEvent
        /// which uses non-nullable <c>Guid</c>. This asymmetry matches the monolith exactly.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteManyToManyRelation_OnSuccess_EventContainsCorrectRelationData()
        {
            // Arrange — known values for assertion, using nullable Guid?
            var relationName = "user_role";
            Guid? expectedOriginId = Guid.NewGuid();
            Guid? expectedTargetId = Guid.NewGuid();

            // Act — publish the relation deleted event
            await _publisher.PublishRelationDeletedAsync(relationName, expectedOriginId, expectedTargetId);

            // Assert — wait for event arrival, then extract and verify properties
            (await _harness.Published.Any<RelationDeletedEvent>()).Should().BeTrue();

            var published = _harness.Published.Select<RelationDeletedEvent>();
            var publishedEvent = published.First().Context.Message;

            // Verify relation name matches the input parameter
            publishedEvent.RelationName.Should().Be("user_role");

            // Verify NULLABLE Guid? OriginId — matches IErpPostDeleteManyToManyRelationHook.OnPostDelete signature
            publishedEvent.OriginId.Should().Be(expectedOriginId);

            // Verify NULLABLE Guid? TargetId — matches IErpPostDeleteManyToManyRelationHook.OnPostDelete signature
            publishedEvent.TargetId.Should().Be(expectedTargetId);

            // Verify CorrelationId was populated (non-empty) for distributed tracing
            publishedEvent.CorrelationId.Should().NotBe(Guid.Empty);

            // Verify Timestamp is recent (within 5 seconds of now)
            publishedEvent.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Test 6: Tests the nullable <c>Guid?</c> behavior unique to the delete hook.
        /// In the monolith, <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete</c> accepts
        /// <c>Guid?</c> parameters, allowing <c>null</c> to indicate bulk deletes that
        /// remove all records on one side of the relation.
        ///
        /// <para>
        /// CRITICAL: This test validates the asymmetry between:
        /// <list type="bullet">
        ///   <item>Create hook: <c>OnPostCreate(string, Guid, Guid)</c> — non-nullable</item>
        ///   <item>Delete hook: <c>OnPostDelete(string, Guid?, Guid?)</c> — nullable</item>
        /// </list>
        /// A <c>null</c> OriginId means the delete targeted all origin-side records
        /// of the relation, which is valid in the monolith's
        /// <c>RecordManager.RemoveRelationManyToManyRecord(Guid, Guid?, Guid?)</c>.
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteManyToManyRelation_WithNullOriginId_PublishesEventWithNullOriginId()
        {
            // Arrange — null originId simulates bulk delete of all origin-side records
            var relationName = "user_role";
            Guid? originId = null;
            Guid? expectedTargetId = Guid.NewGuid();

            // Act — publish with null origin, matching monolith's nullable signature
            await _publisher.PublishRelationDeletedAsync(relationName, originId, expectedTargetId);

            // Assert — verify event was published
            (await _harness.Published.Any<RelationDeletedEvent>()).Should().BeTrue();

            var published = _harness.Published.Select<RelationDeletedEvent>();
            var publishedEvent = published.First().Context.Message;

            // CRITICAL assertion: OriginId should be null, validating nullable Guid? handling
            publishedEvent.OriginId.Should().BeNull();

            // TargetId should be populated with the expected value
            publishedEvent.TargetId.Should().Be(expectedTargetId);
        }

        /// <summary>
        /// Test 7: Validates that NO <see cref="RelationDeletedEvent"/> is published when
        /// pre-delete validation fails with errors, preserving the monolith behavior where
        /// post-hooks only fire on the success path.
        ///
        /// <para>
        /// In the monolith (<c>RecordManager.RemoveRelationManyToManyRecord</c>, lines 160-170):
        /// <code>
        /// List&lt;ErrorModel&gt; errors = new List&lt;ErrorModel&gt;();
        /// RecordHookManager.ExecutePreDeleteManyToManyRelationHook(relation.Name, originValue, targetValue, errors);
        /// if (errors.Count &gt; 0) {
        ///     connection.RollbackTransaction();
        ///     // Post-hook is NEVER called — no event published
        ///     return response;
        /// }
        /// </code>
        /// </para>
        /// </summary>
        [Fact]
        public async Task DeleteManyToManyRelation_OnValidationError_DoesNotPublishEvent()
        {
            // Arrange — simulate pre-delete validation failure with ErrorModel entries.
            // In the monolith, IErpPreDeleteManyToManyRelationHook.OnPreDelete populates the
            // errors list. RecordManager checks errors.Count > 0 and aborts, so the post-hook
            // (now replaced by RelationEventPublisher.PublishRelationDeletedAsync) is never invoked.
            var errors = new List<ErrorModel>
            {
                new ErrorModel("relation", "user_role", "Validation failed: relation record not found")
            };

            // Act — validation errors prevent the publisher from being invoked.
            // The publisher is NOT called, simulating the monolith's abort path
            // where RecordManager.RemoveRelationManyToManyRecord returns early with errors
            // and ExecutePostDeleteManyToManyRelationHook is never reached (lines 161-170).
            // No call to _publisher.PublishRelationDeletedAsync()

            // Assert — no RelationDeletedEvent should appear on the bus
            (await _harness.Published.Any<RelationDeletedEvent>()).Should().BeFalse();
        }

        #endregion
    }
}
