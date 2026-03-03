using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;

namespace WebVella.Erp.Service.Core.Events.Publishers
{
    /// <summary>
    /// Publishes domain events for many-to-many relation lifecycle operations,
    /// replacing the monolith's synchronous hook execution pattern in
    /// <c>RecordHookManager.ExecutePostCreateManyToManyRelationHook</c> and
    /// <c>RecordHookManager.ExecutePostDeleteManyToManyRelationHook</c>.
    /// <para>
    /// In the monolith, <c>RecordHookManager</c> discovered hook implementations
    /// via <c>HookManager.GetHookedInstances&lt;T&gt;(relationName)</c> and invoked
    /// them synchronously in-process. This publisher converts those post-operation
    /// hooks into asynchronous domain event publications via MassTransit's
    /// <see cref="IPublishEndpoint"/>, enabling subscribers in any service to react
    /// to relation lifecycle events.
    /// </para>
    /// <para>
    /// CRITICAL: The create method uses non-nullable <see cref="Guid"/> for origin
    /// and target IDs (matching <c>IErpPostCreateManyToManyRelationHook.OnPostCreate</c>),
    /// while the delete method uses nullable <see cref="Nullable{Guid}"/> (matching
    /// <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete</c>) to support bulk
    /// deletes where one side of the relation is unspecified.
    /// </para>
    /// </summary>
    public class RelationEventPublisher
    {
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly ILogger<RelationEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RelationEventPublisher"/> class.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for sending domain events to the message bus
        /// (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack validation).
        /// </param>
        /// <param name="logger">
        /// Structured logger for recording debug-level event publication details
        /// including relation name, origin ID, and target ID for distributed tracing.
        /// </param>
        public RelationEventPublisher(
            IPublishEndpoint publishEndpoint,
            ILogger<RelationEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes a <see cref="RelationCreatedEvent"/> to the message bus after a
        /// many-to-many relation record has been successfully created.
        /// <para>
        /// Replaces the monolith's synchronous
        /// <c>RecordHookManager.ExecutePostCreateManyToManyRelationHook(string, Guid, Guid)</c>
        /// which validated the relation name, discovered hook implementations keyed by
        /// relation name via <c>HookManager.GetHookedInstances&lt;IErpPostCreateManyToManyRelationHook&gt;</c>,
        /// and invoked each hook's <c>OnPostCreate(relationName, originId, targetId)</c> in-process.
        /// </para>
        /// </summary>
        /// <param name="relationName">
        /// Name of the many-to-many relation. Must not be null or whitespace.
        /// Preserves validation from the original <c>RecordHookManager</c> (line 116-117).
        /// </param>
        /// <param name="originId">
        /// Non-nullable <see cref="Guid"/> identifying the record on the origin side
        /// of the many-to-many relation. Matches the original
        /// <c>IErpPostCreateManyToManyRelationHook.OnPostCreate(string, Guid, Guid)</c> signature.
        /// </param>
        /// <param name="targetId">
        /// Non-nullable <see cref="Guid"/> identifying the record on the target side
        /// of the many-to-many relation. Matches the original hook signature.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for graceful shutdown support in the microservice architecture.
        /// Not present in the original synchronous hook execution.
        /// </param>
        /// <returns>A task representing the asynchronous publish operation.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="relationName"/> is null or whitespace,
        /// preserving the monolith's validation pattern.
        /// </exception>
        public async Task PublishRelationCreatedAsync(
            string relationName,
            Guid originId,
            Guid targetId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(relationName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(relationName));

            var relationCreatedEvent = new RelationCreatedEvent
            {
                EntityName = string.Empty,
                RelationName = relationName,
                OriginId = originId,
                TargetId = targetId,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow
            };

            await _publishEndpoint.Publish(relationCreatedEvent, cancellationToken);

            _logger.LogDebug(
                "Published RelationCreatedEvent for relation '{RelationName}' with OriginId={OriginId} and TargetId={TargetId}, CorrelationId={CorrelationId}",
                relationName,
                originId,
                targetId,
                relationCreatedEvent.CorrelationId);
        }

        /// <summary>
        /// Publishes a <see cref="RelationDeletedEvent"/> to the message bus after a
        /// many-to-many relation record has been successfully deleted.
        /// <para>
        /// Replaces the monolith's synchronous
        /// <c>RecordHookManager.ExecutePostDeleteManyToManyRelationHook(string, Guid?, Guid?)</c>
        /// which validated the relation name, discovered hook implementations keyed by
        /// relation name via <c>HookManager.GetHookedInstances&lt;IErpPostDeleteManyToManyRelationHook&gt;</c>,
        /// and invoked each hook's <c>OnPostDelete(relationName, originId, targetId)</c> in-process.
        /// </para>
        /// <para>
        /// CRITICAL: <paramref name="originId"/> and <paramref name="targetId"/> are
        /// nullable <c>Guid?</c>, preserving the original
        /// <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete(string, Guid?, Guid?)</c>
        /// signature. A <c>null</c> value indicates a bulk delete where all records on
        /// that side of the relation were removed.
        /// </para>
        /// </summary>
        /// <param name="relationName">
        /// Name of the many-to-many relation. Must not be null or whitespace.
        /// Preserves validation from the original <c>RecordHookManager</c> (line 139-140).
        /// </param>
        /// <param name="originId">
        /// Nullable <see cref="Nullable{Guid}"/> identifying the record on the origin side
        /// of the many-to-many relation, or <c>null</c> for bulk deletes targeting all
        /// origin-side records. Matches the original
        /// <c>IErpPostDeleteManyToManyRelationHook.OnPostDelete(string, Guid?, Guid?)</c> signature.
        /// </param>
        /// <param name="targetId">
        /// Nullable <see cref="Nullable{Guid}"/> identifying the record on the target side
        /// of the many-to-many relation, or <c>null</c> for bulk deletes. Matches the
        /// original hook signature.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for graceful shutdown support in the microservice architecture.
        /// Not present in the original synchronous hook execution.
        /// </param>
        /// <returns>A task representing the asynchronous publish operation.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="relationName"/> is null or whitespace,
        /// preserving the monolith's validation pattern.
        /// </exception>
        public async Task PublishRelationDeletedAsync(
            string relationName,
            Guid? originId,
            Guid? targetId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(relationName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(relationName));

            var relationDeletedEvent = new RelationDeletedEvent
            {
                EntityName = string.Empty,
                RelationName = relationName,
                OriginId = originId,
                TargetId = targetId,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow
            };

            await _publishEndpoint.Publish(relationDeletedEvent, cancellationToken);

            _logger.LogDebug(
                "Published RelationDeletedEvent for relation '{RelationName}' with OriginId={OriginId} and TargetId={TargetId}, CorrelationId={CorrelationId}",
                relationName,
                originId,
                targetId,
                relationDeletedEvent.CorrelationId);
        }
    }
}
