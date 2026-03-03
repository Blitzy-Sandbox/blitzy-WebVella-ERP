using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event published after a record search (EQL query) completes successfully.
    /// <para>
    /// Replaces the monolith's synchronous <c>IErpPostSearchRecordHook</c> interface
    /// whose hook signature was:
    /// <code>void OnPostSearchRecord(string entityName, List&lt;EntityRecord&gt; record)</code>
    /// and whose execution in <c>RecordHookManager.ExecutePostSearchRecordHooks</c>
    /// validated <c>entityName</c> not null/whitespace, then iterated all hooked
    /// instances calling <c>inst.OnPostSearchRecord(entityName, record)</c>.
    /// </para>
    /// <para>
    /// In the microservice architecture, this event is published on the message bus
    /// (MassTransit over RabbitMQ or SNS+SQS) after the EQL engine returns search
    /// results. Subscribers in other services (e.g., CRM search indexing, Reporting
    /// aggregation) consume this event to react to search completions without
    /// requiring direct in-process hook registration.
    /// </para>
    /// <para>
    /// This is a post-operation event — the <see cref="Results"/> property carries
    /// the immutable search result data. Subscribers should treat the results as
    /// read-only; any modifications have no effect on the originating service.
    /// </para>
    /// <para>
    /// NOTE: The original monolith hook attribute text stated "before entity record
    /// search" despite being named <c>IErpPostSearchRecordHook</c>. This is a known
    /// inconsistency in the monolith source. This event correctly represents
    /// post-search behavior, fired after the search has completed and results are
    /// available.
    /// </para>
    /// </summary>
    public class RecordSearchEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the search event occurred.
        /// <para>
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// Enables event ordering, idempotency checks, and audit trail reconstruction
        /// across distributed services that subscribe to search events.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this search event
        /// and any downstream events it triggers across service boundaries.
        /// <para>
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// Links all related events in a chain (e.g., a search event triggering a
        /// cache invalidation or a reporting aggregation update) for distributed tracing.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name that the search was executed against.
        /// <para>
        /// Maps directly to the <c>entityName</c> parameter from the monolith's
        /// <c>IErpPostSearchRecordHook.OnPostSearchRecord(string entityName, ...)</c>
        /// method and the <c>RecordHookManager.ExecutePostSearchRecordHooks</c>
        /// execution pattern. Subscribers use this property to filter search events
        /// for entities they are responsible for (e.g., CRM service filters for
        /// "account", "contact", "case" entities).
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the search results returned by the EQL query engine.
        /// <para>
        /// Maps to the <c>List&lt;EntityRecord&gt; record</c> parameter from the
        /// monolith's <c>IErpPostSearchRecordHook.OnPostSearchRecord</c> method,
        /// renamed from "record" to "Results" for semantic clarity in the event
        /// contract (the parameter carries a list of results, not a single record).
        /// </para>
        /// <para>
        /// Initialized to an empty <see cref="List{T}"/> in the parameterless
        /// constructor to prevent null reference exceptions during serialization
        /// and deserialization via MassTransit/Newtonsoft.Json.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "results")]
        public List<EntityRecord> Results { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordSearchEvent"/> class
        /// with default values for all properties.
        /// <para>
        /// Sets <see cref="Timestamp"/> to <see cref="DateTime.UtcNow"/>,
        /// <see cref="CorrelationId"/> to a new <see cref="Guid"/>, and
        /// <see cref="Results"/> to an empty list. This parameterless constructor
        /// is required for MassTransit message deserialization and Newtonsoft.Json
        /// object instantiation.
        /// </para>
        /// </summary>
        public RecordSearchEvent()
        {
            Timestamp = DateTime.UtcNow;
            CorrelationId = Guid.NewGuid();
            Results = new List<EntityRecord>();
        }
    }
}
