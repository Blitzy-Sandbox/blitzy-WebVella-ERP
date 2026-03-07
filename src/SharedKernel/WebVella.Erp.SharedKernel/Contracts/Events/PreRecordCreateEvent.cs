using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event raised before a record create operation is executed in any service.
    /// <para>
    /// This event replaces the monolith's synchronous <c>IErpPreCreateRecordHook</c> interface
    /// whose sole method was:
    /// <code>void OnPreCreateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)</code>
    /// In the monolith, <c>RecordHookManager.ExecutePreCreateRecordHooks</c> validated that
    /// <c>entityName</c> was not null/whitespace and <c>errors</c> was not null, then iterated
    /// all registered hook instances calling <c>OnPreCreateRecord</c> synchronously.
    /// </para>
    /// <para>
    /// In the microservice architecture this hook is replaced by publishing a
    /// <see cref="PreRecordCreateEvent"/> onto the message bus (MassTransit over RabbitMQ
    /// or SNS+SQS via LocalStack). Subscribers consume the event, inspect or modify the
    /// <see cref="Record"/> payload, and append to <see cref="ValidationErrors"/> to signal
    /// that the create operation should be rejected.
    /// </para>
    /// <para>
    /// <b>Pre-operation semantics:</b> Because this is a pre-operation event the
    /// <see cref="ValidationErrors"/> list is intentionally mutable. If any subscriber
    /// populates it with one or more <see cref="ErrorModel"/> entries, the originating
    /// service must abort the record creation and return the accumulated errors to the
    /// caller — exactly matching the monolith behavior where hooks could block the
    /// operation by adding errors to the shared list.
    /// </para>
    /// <para>
    /// <b>Serialization:</b> All properties carry <c>[JsonProperty]</c> annotations to
    /// guarantee stable JSON property names across MassTransit message serialization
    /// (Newtonsoft.Json) regardless of C# property casing changes.
    /// </para>
    /// </summary>
    public class PreRecordCreateEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the event was created.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// Downstream subscribers can use this value for ordering, idempotency windows,
        /// and audit trail reconstruction.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for distributed tracing.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// All events triggered by the same originating user action should share the
        /// same <see cref="CorrelationId"/> to enable end-to-end trace correlation
        /// across service boundaries.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the name of the entity whose record is about to be created.
        /// <para>
        /// Mirrors the <c>entityName</c> parameter from the source hook signature
        /// <c>IErpPreCreateRecordHook.OnPreCreateRecord(string entityName, ...)</c>.
        /// Subscribers filter on this value to process events only for entities they
        /// are responsible for (e.g., "account", "contact", "task").
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="EntityRecord"/> that is about to be created.
        /// <para>
        /// Mirrors the <c>record</c> parameter from the source hook signature
        /// <c>IErpPreCreateRecordHook.OnPreCreateRecord(..., EntityRecord record, ...)</c>.
        /// The record contains all field values (dynamic key-value pairs stored in
        /// the <see cref="EntityRecord"/> Expando-based Properties dictionary) that the
        /// caller provided for the new record.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "record")]
        public EntityRecord Record { get; set; }

        /// <summary>
        /// Gets or sets the mutable list of validation errors that subscribers can
        /// populate to block the record create operation.
        /// <para>
        /// Mirrors the <c>errors</c> parameter from the source hook signature
        /// <c>IErpPreCreateRecordHook.OnPreCreateRecord(..., List&lt;ErrorModel&gt; errors)</c>.
        /// Each <see cref="ErrorModel"/> carries a <see cref="ErrorModel.Key"/>,
        /// <see cref="ErrorModel.Value"/>, and <see cref="ErrorModel.Message"/> describing
        /// the validation failure. If this list is non-empty after all subscribers have
        /// processed the event, the originating service must abort the create operation
        /// and return the errors to the caller.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "validationErrors")]
        public List<ErrorModel> ValidationErrors { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PreRecordCreateEvent"/> class
        /// with sensible defaults for all cross-cutting metadata properties.
        /// <para>
        /// Sets <see cref="Timestamp"/> to <see cref="DateTime.UtcNow"/>,
        /// <see cref="CorrelationId"/> to a new <see cref="Guid"/>, and
        /// <see cref="ValidationErrors"/> to an empty list. The <see cref="EntityName"/>
        /// and <see cref="Record"/> properties remain at their default values and must
        /// be set by the publisher before dispatching the event.
        /// </para>
        /// </summary>
        public PreRecordCreateEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            ValidationErrors = new List<ErrorModel>();
        }
    }
}
