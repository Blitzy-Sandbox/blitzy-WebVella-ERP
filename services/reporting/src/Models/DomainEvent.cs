using System.Text.Json.Serialization;

namespace WebVellaErp.Reporting.Models
{
    /// <summary>
    /// Base domain event envelope model consumed by the Reporting service's EventConsumer Lambda
    /// function via SQS. The Reporting service acts as a CQRS read side — it subscribes to domain
    /// events from ALL bounded-context services (identity, entity-management, crm, inventory,
    /// invoicing, notifications, file-management, workflow, plugin-system) to build read-optimized
    /// projections in RDS PostgreSQL.
    ///
    /// This model defines the SQS message payload structure for deserialization and replaces the
    /// monolith's synchronous hook dispatch pattern:
    ///   - IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)
    ///     → DomainEvent with Action = "created"
    ///   - IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)
    ///     → DomainEvent with Action = "updated"
    ///   - IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)
    ///     → DomainEvent with Action = "deleted"
    ///
    /// The monolith's RecordHookManager dispatched hooks by entityName; in the target architecture,
    /// event routing is performed by SourceDomain + EntityName + Action via SNS/SQS.
    ///
    /// All properties use System.Text.Json [JsonPropertyName] attributes with snake_case naming
    /// for Native AOT compatibility (NOT Newtonsoft.Json [JsonProperty]).
    /// </summary>
    public class DomainEvent
    {
        /// <summary>
        /// Unique event identifier serving as the idempotency key for at-least-once delivery
        /// processing via SQS. Each event consumer uses this ID for deduplication to ensure
        /// idempotent event handling per AAP Section 0.8.5.
        /// </summary>
        [JsonPropertyName("event_id")]
        public Guid EventId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Correlation ID propagated from the originating HTTP request for distributed tracing
        /// across all bounded-context services. Enables end-to-end request tracking in structured
        /// JSON logging per AAP Section 0.8.5 ("Structured JSON logging with correlation-ID
        /// propagation from all Lambda functions").
        /// </summary>
        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        /// <summary>
        /// The bounded context that emitted the event. Valid values correspond to the 10
        /// microservices in the target architecture: "identity", "entity-management", "crm",
        /// "inventory", "invoicing", "reporting", "notifications", "file-management",
        /// "workflow", "plugin-system".
        /// </summary>
        [JsonPropertyName("source_domain")]
        public string SourceDomain { get; set; } = string.Empty;

        /// <summary>
        /// The entity within the source domain that the event pertains to (e.g., "invoice",
        /// "contact", "account", "task", "user", "role", "email"). Maps from the monolith's
        /// <c>entityName</c> parameter in hook interfaces such as
        /// <c>IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)</c>.
        /// </summary>
        [JsonPropertyName("entity_name")]
        public string EntityName { get; set; } = string.Empty;

        /// <summary>
        /// The CRUD action that triggered this event: "created", "updated", or "deleted".
        /// Combined with <see cref="SourceDomain"/> and <see cref="EntityName"/> to form the
        /// full event type following the naming convention: {domain}.{entity}.{action}
        /// (e.g., "invoicing.invoice.created") per AAP Section 0.8.5.
        /// </summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp of when the event was produced by the source bounded-context service.
        /// All timestamps use UTC to ensure consistency across distributed services.
        /// </summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The event payload data containing the entity record's field values as key-value pairs.
        /// Nullable — may be null for delete events that only carry the record's identity.
        /// Maps from the monolith's <c>EntityRecord record</c> parameter in hook interfaces,
        /// where <c>EntityRecord</c> extends <c>Dictionary&lt;string, object&gt;</c> via the
        /// <c>Expando</c> base class. The nullable object values support heterogeneous field
        /// types (string, int, decimal, DateTime, Guid, bool, arrays, nested objects).
        /// </summary>
        [JsonPropertyName("payload")]
        public Dictionary<string, object?>? Payload { get; set; }

        /// <summary>
        /// Event schema version for forward compatibility and consumer version negotiation.
        /// Consumers can use this to apply version-specific deserialization or transformation
        /// logic when the event schema evolves. Defaults to "1.0" for the initial release.
        /// </summary>
        [JsonPropertyName("version")]
        public string? Version { get; set; } = "1.0";

        /// <summary>
        /// Computed event type following the AAP Section 0.8.5 event naming convention:
        /// <c>{domain}.{entity}.{action}</c>. Examples:
        /// <list type="bullet">
        ///   <item><description>"invoicing.invoice.created"</description></item>
        ///   <item><description>"crm.contact.updated"</description></item>
        ///   <item><description>"entity-management.entity.deleted"</description></item>
        ///   <item><description>"identity.user.created"</description></item>
        /// </list>
        /// This property is read-only and derived from <see cref="SourceDomain"/>,
        /// <see cref="EntityName"/>, and <see cref="Action"/>.
        /// Used for event routing, filtering, and logging purposes.
        /// </summary>
        [JsonPropertyName("event_type")]
        public string EventType => $"{SourceDomain}.{EntityName}.{Action}";
    }
}
