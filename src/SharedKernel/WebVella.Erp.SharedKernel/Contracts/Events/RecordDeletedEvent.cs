using System;
using Newtonsoft.Json;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published after a record is deleted from an entity's data store.
    /// <para>
    /// This event replaces the monolith's synchronous <c>IErpPostDeleteRecordHook</c> interface,
    /// whose hook signature was <c>void OnPostDeleteRecord(string entityName, EntityRecord record)</c>.
    /// In the monolith, <c>RecordHookManager.ExecutePostDeleteRecordHooks</c> validated the entity name
    /// and then iterated all registered hook instances synchronously within the same process.
    /// </para>
    /// <para>
    /// In the microservice architecture, this event is published asynchronously via MassTransit
    /// (RabbitMQ for local/Docker development, SNS+SQS for AWS/LocalStack validation) after a
    /// record has been successfully deleted. Subscribers in other services consume this event to
    /// perform follow-up actions such as cleaning up cross-service references, updating search
    /// indexes, or triggering cascading deletions.
    /// </para>
    /// <para>
    /// <b>Simplification from source hook:</b> The original <c>IErpPostDeleteRecordHook</c> carried
    /// the full <c>EntityRecord</c> object. This event contract carries only the <see cref="RecordId"/>
    /// (a <see cref="Guid"/>) because after deletion the record no longer exists in the data store.
    /// The publishing service extracts the record's identifier from the <c>EntityRecord</c> before
    /// publishing this event.
    /// </para>
    /// </summary>
    public class RecordDeletedEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the record deletion occurred.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// Enables event ordering, idempotency checks, and audit trail reconstruction
        /// across distributed services.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this deletion event
        /// and any downstream events it triggers across service boundaries.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// Links all related events in a deletion chain for distributed tracing and debugging.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the name of the entity whose record was deleted.
        /// <para>
        /// Mirrors the <c>entityName</c> parameter from the original
        /// <c>IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)</c>
        /// signature. Subscribers use this property to filter events for entities they manage.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the deleted record.
        /// <para>
        /// This is a simplification from the original hook, which carried the full
        /// <c>EntityRecord</c> object. Since the record no longer exists after deletion,
        /// only the identifier is transported. The publishing service extracts the record's
        /// <c>Id</c> from the <c>EntityRecord</c> prior to publishing this event.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "recordId")]
        public Guid RecordId { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordDeletedEvent"/> class
        /// with default values for <see cref="Timestamp"/> and <see cref="CorrelationId"/>.
        /// <para>
        /// Required by MassTransit for deserialization. Sets <see cref="Timestamp"/> to
        /// <see cref="DateTime.UtcNow"/> and <see cref="CorrelationId"/> to a new
        /// <see cref="Guid"/> to ensure every event instance has meaningful defaults
        /// even when created by the message broker infrastructure.
        /// </para>
        /// </summary>
        public RecordDeletedEvent()
        {
            Timestamp = DateTime.UtcNow;
            CorrelationId = Guid.NewGuid();
        }
    }
}
