using System.Text.Json.Serialization;

namespace WebVellaErp.Reporting.Models;

/// <summary>
/// Tracks the event processing offset for the Reporting service's CQRS read-model projections.
///
/// The Reporting service acts as the CQRS read side — it consumes domain events from ALL
/// bounded-context services (identity, entity-management, crm, inventory, invoicing, reporting,
/// notifications, file-management, workflow, plugin-system) via SQS and materializes them into
/// read-optimized projections in RDS PostgreSQL.
///
/// This model represents a row in the <c>read_model_projections</c> tracking table and enables:
/// <list type="bullet">
///   <item><description>
///     <b>Idempotent event processing</b> — <see cref="LastProcessedEventId"/> allows deduplication
///     of events under SQS at-least-once delivery guarantees (AAP §0.8.5).
///   </description></item>
///   <item><description>
///     <b>Resumable consumption</b> — After failures, the consumer resumes from the last
///     successfully processed event using <see cref="LastProcessedTimestamp"/> and
///     <see cref="EventCount"/>.
///   </description></item>
///   <item><description>
///     <b>Operational observability</b> — <see cref="Status"/> and <see cref="LastError"/>
///     provide runtime insight into projection health.
///   </description></item>
/// </list>
///
/// Replaces the monolith's synchronous <c>RecordHookManager</c> dispatch pattern with
/// asynchronous SQS event tracking. Analogous to the monolith's <c>data_source</c> table
/// metadata tracking from <c>DbDataSourceRepository</c>.
///
/// Serialization uses <see cref="JsonPropertyNameAttribute"/> (System.Text.Json) for Native AOT
/// compatibility — NOT Newtonsoft.Json (AAP §0.8.2).
/// </summary>
public class ReadModelProjection
{
    /// <summary>
    /// Primary key identifier for the projection tracking record.
    /// Generated automatically on construction; overwritten when loading from the database.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The name of the source bounded-context service whose events are being tracked.
    /// Must be one of the 10 bounded contexts: identity, entity-management, crm, inventory,
    /// invoicing, reporting, notifications, file-management, workflow, plugin-system.
    /// Examples: "invoicing", "crm", "entity-management".
    /// </summary>
    [JsonPropertyName("service_name")]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// The name of the specific read-model projection being built from the source service's events.
    /// Each service may produce multiple projections (e.g., "invoice_summary", "crm_account_metrics").
    /// Together with <see cref="ServiceName"/>, forms a unique composite key for the projection.
    /// </summary>
    [JsonPropertyName("projection_name")]
    public string ProjectionName { get; set; } = string.Empty;

    /// <summary>
    /// The EventId (Guid as string) of the last successfully processed domain event.
    /// Null when no events have been processed yet for this projection.
    /// Used as the idempotency checkpoint — incoming events with an ID less than or equal
    /// to this value can be safely skipped (AAP §0.8.5: all event consumers MUST be idempotent).
    /// </summary>
    [JsonPropertyName("last_processed_event_id")]
    public string? LastProcessedEventId { get; set; }

    /// <summary>
    /// UTC timestamp of the last successfully processed event.
    /// Null when no events have been processed yet.
    /// Used alongside <see cref="LastProcessedEventId"/> for resumable event consumption
    /// after consumer failures or restarts.
    /// </summary>
    [JsonPropertyName("last_processed_timestamp")]
    public DateTime? LastProcessedTimestamp { get; set; }

    /// <summary>
    /// Total number of events successfully processed for this projection since creation.
    /// Monotonically increasing counter used for operational monitoring and throughput metrics.
    /// </summary>
    [JsonPropertyName("event_count")]
    public long EventCount { get; set; }

    /// <summary>
    /// Current operational status of the projection.
    /// Valid values: "active" (normal processing), "paused" (manually suspended),
    /// "error" (processing halted due to repeated failures).
    /// Consumers check this status before processing events — paused/error projections
    /// are skipped until manually resumed.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    /// <summary>
    /// Error message from the most recent processing failure, if any.
    /// Null when the projection is healthy. Populated when <see cref="Status"/> transitions
    /// to "error". Cleared when the projection is resumed and processes successfully.
    /// Supports the at-least-once delivery guarantee (AAP §0.8.5) by preserving failure
    /// context for diagnostic purposes.
    /// </summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    /// <summary>
    /// UTC timestamp when this projection tracking record was first created.
    /// Set once at record creation and never modified thereafter.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when this tracking record was last updated.
    /// Refreshed on every successful event processing cycle or status change.
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
