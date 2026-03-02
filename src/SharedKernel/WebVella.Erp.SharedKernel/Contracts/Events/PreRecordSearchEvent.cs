using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Domain event replacing the monolith's <c>IErpPreSearchRecordHook</c> interface.
    /// <para>
    /// In the monolith, <c>RecordHookManager.ExecutePreSearchRecordHooks</c> invokes
    /// each <c>IErpPreSearchRecordHook.OnPreSearchRecord(string entityName, EqlSelectNode tree, List&lt;EqlError&gt; errors)</c>
    /// synchronously in-process. In the microservice architecture, this event is published
    /// to the message bus (MassTransit over RabbitMQ or SNS+SQS) before EQL query execution,
    /// allowing subscribers in any service to inspect and validate the EQL query tree and
    /// populate the mutable <see cref="EqlErrors"/> list with validation issues.
    /// </para>
    /// <para>
    /// The <see cref="EqlTree"/> property carries a JSON-serialized representation of the
    /// <c>EqlSelectNode</c> AST to avoid coupling subscribers to the full EQL parser types.
    /// Cross-service subscribers deserialize it back to <c>EqlSelectNode</c> if needed.
    /// </para>
    /// <para>
    /// As a pre-operation event, <see cref="EqlErrors"/> is intentionally mutable
    /// (<see cref="List{T}"/> rather than <see cref="IReadOnlyList{T}"/>), enabling
    /// subscribers to add validation errors that prevent the search from executing —
    /// mirroring the original hook's <c>List&lt;EqlError&gt; errors</c> out-parameter behavior.
    /// </para>
    /// </summary>
    public class PreRecordSearchEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when this pre-search event was created.
        /// Initialized to <see cref="DateTime.UtcNow"/> by the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event and any
        /// downstream events it triggers across distributed service boundaries.
        /// Initialized to <see cref="Guid.NewGuid()"/> by the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name that the EQL search targets.
        /// Mirrors the <c>entityName</c> parameter from the original
        /// <c>IErpPreSearchRecordHook.OnPreSearchRecord</c> method and the
        /// <c>RecordHookManager.ExecutePreSearchRecordHooks</c> dispatcher.
        /// Subscribers use this to filter events for entities they manage.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the JSON-serialized representation of the <c>EqlSelectNode</c> AST
        /// that defines the search query about to be executed.
        /// <para>
        /// The original hook passed the live <c>EqlSelectNode tree</c> object directly.
        /// In the microservice architecture, the AST is serialized to a JSON string for
        /// transport across service boundaries via the message broker. Subscribers that
        /// need to inspect or modify the query tree deserialize it back to <c>EqlSelectNode</c>
        /// using the shared EQL types in <c>WebVella.Erp.SharedKernel.Eql</c>.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "eqlTree")]
        public string EqlTree { get; set; }

        /// <summary>
        /// Gets or sets the mutable list of EQL validation errors that subscribers can
        /// populate to report parsing or semantic issues with the search query.
        /// <para>
        /// Matches the <c>List&lt;EqlError&gt; errors</c> parameter from the original
        /// <c>IErpPreSearchRecordHook.OnPreSearchRecord</c> method. When the event is
        /// published, this list is empty. Subscribers add <see cref="EqlError"/> entries
        /// to signal that the search should be aborted or adjusted. The publishing service
        /// inspects this list after all subscribers have processed the event.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "eqlErrors")]
        public List<EqlError> EqlErrors { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PreRecordSearchEvent"/> class
        /// with default values: <see cref="Timestamp"/> set to <see cref="DateTime.UtcNow"/>,
        /// <see cref="CorrelationId"/> set to a new <see cref="Guid"/>, and
        /// <see cref="EqlErrors"/> initialized to an empty list.
        /// </summary>
        public PreRecordSearchEvent()
        {
            Timestamp = DateTime.UtcNow;
            CorrelationId = Guid.NewGuid();
            EqlErrors = new List<EqlError>();
        }
    }
}
