using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event representing a pre-update operation on an entity record.
    /// <para>
    /// This event replaces the monolith's synchronous <c>IErpPreUpdateRecordHook</c> interface
    /// whose signature was:
    /// <code>
    ///   void OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)
    /// </code>
    /// In the monolith, <c>RecordHookManager.ExecutePreUpdateRecordHooks</c> validated that
    /// <c>entityName</c> was not null/whitespace and <c>errors</c> was not null before iterating
    /// all registered hook instances synchronously in-process.
    /// </para>
    /// <para>
    /// In the microservice architecture, this event is published on the message bus
    /// (MassTransit over RabbitMQ or SNS+SQS via LocalStack) <b>before</b> a record update
    /// is committed. Subscribers may populate <see cref="ValidationErrors"/> to signal that the
    /// update should be rejected, preserving the monolith's pre-operation validation semantics.
    /// </para>
    /// <para>
    /// This is a pure data contract — no business logic, no service dependencies.
    /// All properties are annotated with <see cref="JsonPropertyAttribute"/> for stable
    /// serialization across MassTransit message transports.
    /// </para>
    /// </summary>
    public class PreRecordUpdateEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the pre-update event was raised.
        /// Defaults to <see cref="DateTime.UtcNow"/> at construction time.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event and
        /// all related events across distributed service boundaries.
        /// Defaults to <see cref="Guid.NewGuid()"/> at construction time.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the name of the entity whose record is about to be updated.
        /// Mirrors the <c>entityName</c> parameter from the original
        /// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord</c> signature.
        /// Subscribers use this property to filter events for entities they manage.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the <see cref="EntityRecord"/> being updated.
        /// Mirrors the <c>record</c> parameter from the original
        /// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord</c> signature.
        /// Contains the dynamic field values (via the <see cref="EntityRecord"/>
        /// Expando-based dictionary) representing the proposed update state.
        /// </summary>
        [JsonProperty(PropertyName = "record")]
        public EntityRecord Record { get; set; }

        /// <summary>
        /// Gets or sets the mutable list of validation errors that pre-update subscribers
        /// can populate to block the record update operation.
        /// Mirrors the <c>errors</c> parameter from the original
        /// <c>IErpPreUpdateRecordHook.OnPreUpdateRecord</c> signature.
        /// <para>
        /// If any subscriber adds an <see cref="ErrorModel"/> to this list, the publishing
        /// service should treat the update as invalid and abort the operation, preserving
        /// the monolith's pre-operation validation contract.
        /// </para>
        /// Defaults to an empty <see cref="List{ErrorModel}"/> at construction time.
        /// </summary>
        [JsonProperty(PropertyName = "validationErrors")]
        public List<ErrorModel> ValidationErrors { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PreRecordUpdateEvent"/> class
        /// with default values for <see cref="Timestamp"/>, <see cref="CorrelationId"/>,
        /// and an empty <see cref="ValidationErrors"/> list.
        /// </summary>
        public PreRecordUpdateEvent()
        {
            Timestamp = DateTime.UtcNow;
            CorrelationId = Guid.NewGuid();
            ValidationErrors = new List<ErrorModel>();
        }
    }
}
