using System;

namespace WebVella.Erp.SharedKernel.Contracts.Events
{
    /// <summary>
    /// Base contract for all domain events in the WebVella ERP microservice architecture.
    /// <para>
    /// This interface replaces the monolith's synchronous hook system
    /// (<c>IErpPreCreateRecordHook</c>, <c>IErpPostCreateRecordHook</c>, etc.)
    /// with asynchronous, event-driven communication between independently
    /// deployable services.
    /// </para>
    /// <para>
    /// All implementations must be pure data contracts — no business logic,
    /// no service dependencies. Events are serialized via Newtonsoft.Json and
    /// transported via MassTransit (RabbitMQ for local/Docker, SNS+SQS for
    /// AWS/LocalStack validation).
    /// </para>
    /// <para>
    /// Derived interfaces define operation-specific payloads (e.g., record data,
    /// relation identifiers, error lists) while this base interface captures the
    /// cross-cutting metadata required for routing, tracing, and auditing every
    /// domain event across distributed service boundaries.
    /// </para>
    /// </summary>
    public interface IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the event occurred.
        /// <para>
        /// In the monolith, hook execution time is implicit (synchronous in-process call).
        /// In the microservice architecture, the explicit timestamp enables event ordering,
        /// idempotency checks, and audit trail reconstruction across distributed services.
        /// </para>
        /// </summary>
        DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing event chains across services.
        /// <para>
        /// In the monolith, hook calls are synchronous and in-process, so tracing is trivial.
        /// In microservices, a single user action (e.g., creating a CRM account) may trigger
        /// a cascade of asynchronous events across multiple services. The <see cref="CorrelationId"/>
        /// links all related events in the chain, enabling distributed tracing and debugging.
        /// </para>
        /// </summary>
        Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// <para>
        /// Mirrors the <c>entityName</c> parameter present in all monolith hook interfaces
        /// (used by <c>HookManager.GetHookedInstances&lt;T&gt;(entityName)</c> for lookup and routing).
        /// Subscribers use this property to filter events for entities they are responsible for.
        /// For relation-specific events, this property may be empty or null while a separate
        /// <c>RelationName</c> property on the derived event interface carries the relation identifier.
        /// </para>
        /// </summary>
        string EntityName { get; set; }
    }
}
