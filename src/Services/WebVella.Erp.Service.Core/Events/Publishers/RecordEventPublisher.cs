using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Core.Events.Publishers
{
    /// <summary>
    /// Publishes domain events for record CRUD operations via MassTransit,
    /// replacing the monolith's synchronous <c>RecordHookManager</c> hook execution.
    /// <para>
    /// In the monolith, <c>RecordHookManager</c> (an <c>internal static class</c>)
    /// discovered and executed hook implementations synchronously and in-process via
    /// <c>HookManager.GetHookedInstances&lt;T&gt;(entityName)</c>. After each record
    /// create, update, or delete operation, <c>RecordManager</c> called the corresponding
    /// <c>ExecutePost{Create|Update|Delete}RecordHooks(entityName, record)</c> method,
    /// which iterated all <c>[HookAttachment(Key=entityName)]</c>-decorated implementations
    /// and invoked them sequentially.
    /// </para>
    /// <para>
    /// In the microservice architecture, this publisher converts those synchronous,
    /// in-process hook invocations into asynchronous domain event publications via
    /// MassTransit's <see cref="IPublishEndpoint"/>. Downstream services (CRM, Project,
    /// Mail, Reporting, Admin) subscribe to these events to perform their domain-specific
    /// reactions — such as search index regeneration, feed updates, or audit logging —
    /// without requiring direct in-process coupling to the Core service.
    /// </para>
    /// <para>
    /// This class is registered as a scoped/transient service in the Core service's
    /// DI container and injected into <c>RecordManager</c> to publish events after
    /// successful record persistence.
    /// </para>
    /// </summary>
    public class RecordEventPublisher
    {
        /// <summary>
        /// MassTransit publish endpoint for sending domain events to the message bus.
        /// Configured to use RabbitMQ for local/Docker development and SNS/SQS for
        /// AWS/LocalStack validation environments.
        /// </summary>
        private readonly IPublishEndpoint _publishEndpoint;

        /// <summary>
        /// Structured logger for recording event publication activity.
        /// Logs at Debug level with entity name and correlation ID for distributed
        /// tracing and operational visibility.
        /// </summary>
        private readonly ILogger<RecordEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordEventPublisher"/> class.
        /// </summary>
        /// <param name="publishEndpoint">
        /// The MassTransit publish endpoint for sending domain events to the message bus.
        /// Must not be <c>null</c>.
        /// </param>
        /// <param name="logger">
        /// The logger instance for structured logging of event publications.
        /// Must not be <c>null</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="publishEndpoint"/> or <paramref name="logger"/> is <c>null</c>.
        /// </exception>
        public RecordEventPublisher(IPublishEndpoint publishEndpoint, ILogger<RecordEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes a <see cref="RecordCreatedEvent"/> to the message bus after a new
        /// entity record has been successfully created.
        /// <para>
        /// Replaces <c>RecordHookManager.ExecutePostCreateRecordHooks(string entityName, EntityRecord record)</c>
        /// (lines 45-53), which validated the entity name and then iterated all registered
        /// <c>IErpPostCreateRecordHook</c> instances calling
        /// <c>inst.OnPostCreateRecord(entityName, record)</c> synchronously.
        /// </para>
        /// </summary>
        /// <param name="entityName">
        /// The name of the entity whose record was created. Used by subscribers
        /// to filter events for entities they manage (e.g., CRM subscribes for
        /// "account", "contact", "case"). Must not be <c>null</c> or whitespace,
        /// preserving the validation pattern from the original <c>RecordHookManager</c>.
        /// </param>
        /// <param name="record">
        /// The created <see cref="EntityRecord"/> containing all field values as
        /// persisted by the Core service's <c>RecordManager</c>. Maps directly to
        /// the source hook's <c>EntityRecord record</c> parameter. Must not be <c>null</c>.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token for graceful shutdown support in the microservice
        /// architecture. Not present in the original synchronous <c>RecordHookManager</c>.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="entityName"/> is <c>null</c>, empty, or whitespace.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="record"/> is <c>null</c>.
        /// </exception>
        public async Task PublishRecordCreatedAsync(string entityName, EntityRecord record, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(entityName));

            if (record == null)
                throw new ArgumentNullException(nameof(record));

            var correlationId = Guid.NewGuid();

            var recordCreatedEvent = new RecordCreatedEvent
            {
                EntityName = entityName,
                Record = record,
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow
            };

            await _publishEndpoint.Publish(recordCreatedEvent, cancellationToken);

            _logger.LogDebug(
                "Published RecordCreatedEvent for entity '{EntityName}' with CorrelationId '{CorrelationId}'.",
                entityName,
                correlationId);
        }

        /// <summary>
        /// Publishes a <see cref="RecordUpdatedEvent"/> to the message bus after an
        /// entity record has been successfully updated.
        /// <para>
        /// Replaces <c>RecordHookManager.ExecutePostUpdateRecordHooks(string entityName, EntityRecord record)</c>
        /// (lines 68-76), which validated the entity name and then iterated all registered
        /// <c>IErpPostUpdateRecordHook</c> instances calling
        /// <c>inst.OnPostUpdateRecord(entityName, record)</c> synchronously.
        /// </para>
        /// <para>
        /// <b>Enrichment over source hook:</b> The original hook only received the post-update
        /// record state. This method accepts both the old and new record states, enabling
        /// downstream services to compute diffs without additional API calls. The calling
        /// <c>RecordManager</c> captures the old record state before performing the update.
        /// </para>
        /// </summary>
        /// <param name="entityName">
        /// The name of the entity whose record was updated. Must not be <c>null</c> or whitespace.
        /// </param>
        /// <param name="oldRecord">
        /// The record state <b>before</b> the update operation. May be <c>null</c> in cases
        /// where the old state was not captured (e.g., bulk operations or legacy code paths).
        /// This is an enrichment over the original hook which did not carry old state.
        /// </param>
        /// <param name="newRecord">
        /// The record state <b>after</b> the update operation. Maps to the source hook's
        /// <c>EntityRecord record</c> parameter. Must not be <c>null</c>.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token for graceful shutdown support.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="entityName"/> is <c>null</c>, empty, or whitespace.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="newRecord"/> is <c>null</c>.
        /// </exception>
        public async Task PublishRecordUpdatedAsync(string entityName, EntityRecord oldRecord, EntityRecord newRecord, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(entityName));

            if (newRecord == null)
                throw new ArgumentNullException(nameof(newRecord));

            // Note: oldRecord is intentionally allowed to be null — in cases where the
            // old state was not captured (bulk operations, legacy migration paths), the
            // event is still published with OldRecord = null so subscribers can still
            // react to the update even without diff capability.

            var correlationId = Guid.NewGuid();

            var recordUpdatedEvent = new RecordUpdatedEvent
            {
                EntityName = entityName,
                OldRecord = oldRecord,
                NewRecord = newRecord,
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow
            };

            await _publishEndpoint.Publish(recordUpdatedEvent, cancellationToken);

            _logger.LogDebug(
                "Published RecordUpdatedEvent for entity '{EntityName}' with CorrelationId '{CorrelationId}'.",
                entityName,
                correlationId);
        }

        /// <summary>
        /// Publishes a <see cref="RecordDeletedEvent"/> to the message bus after an
        /// entity record has been successfully deleted.
        /// <para>
        /// Replaces <c>RecordHookManager.ExecutePostDeleteRecordHooks(string entityName, EntityRecord record)</c>
        /// (lines 91-99), which validated the entity name and then iterated all registered
        /// <c>IErpPostDeleteRecordHook</c> instances calling
        /// <c>inst.OnPostDeleteRecord(entityName, record)</c> synchronously.
        /// </para>
        /// <para>
        /// <b>Simplification from source hook:</b> The original hook received the full
        /// <c>EntityRecord</c> at time of deletion. This method accepts only the
        /// <paramref name="recordId"/> (<see cref="Guid"/>) because after deletion the
        /// record no longer exists in the data store. The calling <c>RecordManager</c>
        /// extracts the record identifier before publishing this event.
        /// </para>
        /// </summary>
        /// <param name="entityName">
        /// The name of the entity whose record was deleted. Must not be <c>null</c> or whitespace.
        /// </param>
        /// <param name="recordId">
        /// The unique identifier of the deleted record, extracted from the <c>EntityRecord</c>
        /// by the calling service prior to deletion. Must not be <see cref="Guid.Empty"/>.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token for graceful shutdown support.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="entityName"/> is <c>null</c>, empty, or whitespace,
        /// or when <paramref name="recordId"/> equals <see cref="Guid.Empty"/>.
        /// </exception>
        public async Task PublishRecordDeletedAsync(string entityName, Guid recordId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(entityName))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(entityName));

            if (recordId == Guid.Empty)
                throw new ArgumentException("Record ID must not be an empty GUID.", nameof(recordId));

            var correlationId = Guid.NewGuid();

            var recordDeletedEvent = new RecordDeletedEvent
            {
                EntityName = entityName,
                RecordId = recordId,
                CorrelationId = correlationId,
                Timestamp = DateTimeOffset.UtcNow
            };

            await _publishEndpoint.Publish(recordDeletedEvent, cancellationToken);

            _logger.LogDebug(
                "Published RecordDeletedEvent for entity '{EntityName}', RecordId '{RecordId}', CorrelationId '{CorrelationId}'.",
                entityName,
                recordId,
                correlationId);
        }
    }
}
