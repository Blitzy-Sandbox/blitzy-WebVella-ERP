using System;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published after an entity record has been successfully updated.
    /// <para>
    /// This event replaces the monolith's synchronous <c>IErpPostUpdateRecordHook</c> interface
    /// whose hook signature was:
    /// <code>void OnPostUpdateRecord(string entityName, EntityRecord record)</code>
    /// The monolith's <c>RecordHookManager.ExecutePostUpdateRecordHooks</c> (lines 68-76) validated
    /// <c>entityName</c> for null/whitespace, then iterated all hooked instances calling
    /// <c>OnPostUpdateRecord(entityName, record)</c> synchronously in-process.
    /// </para>
    /// <para>
    /// <b>Enrichment over source hook:</b> The original hook only carried the post-update record state.
    /// This event is enriched with both <see cref="OldRecord"/> (state before update) and
    /// <see cref="NewRecord"/> (state after update), enabling event consumers to understand exactly
    /// what changed without requiring a separate lookup. The publishing service (Core) is responsible
    /// for capturing the old record state before the update operation and including it in the event.
    /// </para>
    /// <para>
    /// This is a post-operation event — it carries immutable result data and does not include
    /// validation errors (unlike pre-operation events which carry <c>List&lt;ErrorModel&gt;</c>).
    /// </para>
    /// <para>
    /// Serialized via Newtonsoft.Json with explicit <see cref="JsonPropertyAttribute"/> annotations
    /// on all properties to ensure stable MassTransit message contract compatibility across
    /// RabbitMQ (local/Docker) and SNS+SQS (AWS/LocalStack) transports.
    /// </para>
    /// </summary>
    public class RecordUpdatedEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the record update event occurred.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// Used for event ordering, idempotency checks, and distributed audit trail reconstruction.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this update event
        /// across distributed service boundaries.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// Links all related events triggered by a single user action across multiple services,
        /// enabling distributed tracing and debugging.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the name of the entity whose record was updated.
        /// <para>
        /// Maps directly to the <c>entityName</c> parameter from the source
        /// <c>IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)</c>.
        /// Subscribers use this property to filter events for entities they own or monitor.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the record state <b>before</b> the update operation.
        /// <para>
        /// <b>Enrichment:</b> This property is not present in the original
        /// <c>IErpPostUpdateRecordHook</c> interface, which only carried the post-update state.
        /// The publishing service (Core) captures the old record state before executing the
        /// update and includes it here so that event consumers can compute a diff without
        /// requiring a separate read-back operation.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "oldRecord")]
        public EntityRecord OldRecord { get; set; }

        /// <summary>
        /// Gets or sets the record state <b>after</b> the update operation.
        /// <para>
        /// Maps to the <c>EntityRecord record</c> parameter from the source
        /// <c>IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)</c>.
        /// Contains the full updated record with all field values as persisted to the database.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "newRecord")]
        public EntityRecord NewRecord { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordUpdatedEvent"/> class
        /// with default metadata values.
        /// <para>
        /// Sets <see cref="Timestamp"/> to <see cref="DateTime.UtcNow"/> and
        /// <see cref="CorrelationId"/> to a new <see cref="Guid"/> for immediate use
        /// by event publishers without requiring additional initialization.
        /// </para>
        /// </summary>
        public RecordUpdatedEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
        }
    }
}
