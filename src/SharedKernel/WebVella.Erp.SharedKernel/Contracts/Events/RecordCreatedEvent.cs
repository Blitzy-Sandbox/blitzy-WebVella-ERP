using System;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published after a new entity record has been successfully created.
    /// <para>
    /// This event replaces the monolith's synchronous <c>IErpPostCreateRecordHook</c> interface,
    /// whose single method signature was:
    /// <code>void OnPostCreateRecord(string entityName, EntityRecord record)</code>
    /// In the monolith, <c>RecordHookManager.ExecutePostCreateRecordHooks</c> validated the
    /// <paramref name="entityName"/> parameter and then iterated all registered hook instances
    /// calling <c>inst.OnPostCreateRecord(entityName, record)</c> synchronously and in-process.
    /// </para>
    /// <para>
    /// In the microservice architecture, this event is published asynchronously via MassTransit
    /// (RabbitMQ for local/Docker development, SNS+SQS for AWS/LocalStack validation) after the
    /// Core service's <c>RecordManager</c> successfully persists a new record. Downstream services
    /// (CRM, Project, Mail, Reporting, Admin) subscribe to this event to perform their own
    /// post-creation logic — such as search index regeneration, feed updates, or audit logging —
    /// without requiring direct in-process coupling to the originating service.
    /// </para>
    /// <para>
    /// As a post-operation event, this class carries the immutable result data (the created
    /// <see cref="EntityRecord"/>) and does NOT include a <c>ValidationErrors</c> list. Validation
    /// errors are only relevant for pre-operation events (e.g., <c>PreRecordCreateEvent</c>) where
    /// subscribers can reject the operation before it commits.
    /// </para>
    /// <para>
    /// All properties are annotated with <see cref="JsonPropertyAttribute"/> to ensure stable
    /// JSON serialization contracts across MassTransit message transport boundaries, regardless
    /// of the underlying broker (RabbitMQ or SNS+SQS).
    /// </para>
    /// </summary>
    public class RecordCreatedEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the record creation event occurred.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// In the monolith, hook execution time was implicit (synchronous in-process call).
        /// In the microservice architecture, the explicit timestamp enables event ordering,
        /// idempotency checks, and audit trail reconstruction across distributed services.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event and all
        /// related downstream events across service boundaries.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// A single user action (e.g., creating a CRM account record) may trigger a cascade
        /// of asynchronous events across multiple services. The <see cref="CorrelationId"/>
        /// links all related events in the chain, enabling distributed tracing and debugging.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the name of the entity whose record was created.
        /// <para>
        /// Mirrors the <c>entityName</c> parameter from the original
        /// <c>IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)</c>
        /// signature. In the monolith, <c>HookManager.GetHookedInstances&lt;T&gt;(entityName)</c>
        /// used this value for routing hooks to the correct entity. Event subscribers use this
        /// property to filter events for entities they are responsible for (e.g., the CRM service
        /// subscribes only to events where <c>EntityName</c> matches "account", "contact", or "case").
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the created <see cref="EntityRecord"/> payload.
        /// <para>
        /// Directly maps to the <c>EntityRecord record</c> parameter from the original
        /// <c>IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)</c>
        /// signature. Contains the full dynamic record data (all field values) as persisted by the
        /// Core service's <c>RecordManager</c>. The <see cref="EntityRecord"/> type extends
        /// <c>Expando</c>, providing dictionary-based dynamic property access where field names
        /// are determined at runtime from entity metadata.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "record")]
        public EntityRecord Record { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordCreatedEvent"/> class with default
        /// metadata values. Sets <see cref="Timestamp"/> to the current UTC time and generates a
        /// new <see cref="CorrelationId"/> for distributed tracing.
        /// </summary>
        public RecordCreatedEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
        }
    }
}
