// Suppress AOT trimming/dynamic-code warnings for System.Text.Json serialization of
// Dictionary<string, object?> payload data. The Dictionary type contains runtime-typed values
// from deserialized domain events that cannot be statically analyzed for source generation.
// This matches the pattern used by the sibling ReportService.cs in this module.
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute
#pragma warning disable IL3050 // Members annotated with RequiresDynamicCodeAttribute

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Models;

namespace WebVellaErp.Reporting.Services;

/// <summary>
/// Defines the contract for CQRS read-model projection management operations.
/// Replaces the monolith's synchronous post-hook execution pattern from RecordHookManager
/// (ExecutePostCreateRecordHooks, ExecutePostUpdateRecordHooks, ExecutePostDeleteRecordHooks)
/// with an asynchronous, event-driven projection update service.
///
/// All methods are idempotent via UPSERT pattern, supporting the at-least-once
/// SQS delivery guarantee specified in AAP §0.8.5.
///
/// The calling EventConsumer Lambda handler manages the NpgsqlConnection/NpgsqlTransaction
/// lifecycle (begin/commit/rollback); this service operates within that transaction scope.
/// </summary>
public interface IProjectionService
{
    /// <summary>
    /// Processes an entity creation domain event by upserting a read-model projection row
    /// into reporting.read_model_projections.
    /// Replaces IErpPostCreateRecordHook.OnPostCreateRecord from the monolith's RecordHookManager
    /// (source RecordHookManager.cs lines 45-53).
    /// Uses UPSERT pattern for idempotency: handles out-of-order and duplicate create events gracefully.
    /// </summary>
    /// <param name="domainEvent">The domain event containing source domain, entity, action, and payload.</param>
    /// <param name="connection">The active NpgsqlConnection managed by the caller.</param>
    /// <param name="transaction">The active NpgsqlTransaction managed by the caller.</param>
    /// <param name="cancellationToken">Cancellation token for async operation cancellation.</param>
    Task ProcessEntityCreatedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes an entity update domain event by upserting the read-model projection row
    /// in reporting.read_model_projections.
    /// Replaces IErpPostUpdateRecordHook.OnPostUpdateRecord from the monolith's RecordHookManager
    /// (source RecordHookManager.cs lines 67-76).
    /// Uses UPSERT pattern for idempotency: handles out-of-order events where update arrives before create.
    /// </summary>
    /// <param name="domainEvent">The domain event containing source domain, entity, action, and payload.</param>
    /// <param name="connection">The active NpgsqlConnection managed by the caller.</param>
    /// <param name="transaction">The active NpgsqlTransaction managed by the caller.</param>
    /// <param name="cancellationToken">Cancellation token for async operation cancellation.</param>
    Task ProcessEntityUpdatedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes an entity deletion domain event. Financial entities (invoicing domain) are
    /// soft-deleted to preserve audit trail; all other entities are hard-deleted.
    /// Replaces IErpPostDeleteRecordHook.OnPostDeleteRecord from the monolith's RecordHookManager
    /// (source RecordHookManager.cs lines 91-99).
    /// Idempotent: silently succeeds if the projection row does not exist.
    /// </summary>
    /// <param name="domainEvent">The domain event containing source domain, entity, action, and payload.</param>
    /// <param name="connection">The active NpgsqlConnection managed by the caller.</param>
    /// <param name="transaction">The active NpgsqlTransaction managed by the caller.</param>
    /// <param name="cancellationToken">Cancellation token for async operation cancellation.</param>
    Task ProcessEntityDeletedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the event processing offset for a given source domain.
    /// Supports exactly-once processing semantics by tracking the last successfully
    /// processed event ID per domain via UPSERT into reporting.event_offsets.
    /// </summary>
    /// <param name="sourceDomain">The bounded-context domain name (e.g., "invoicing", "crm").</param>
    /// <param name="lastEventId">The ID of the last successfully processed event.</param>
    /// <param name="connection">The active NpgsqlConnection managed by the caller.</param>
    /// <param name="transaction">The active NpgsqlTransaction managed by the caller.</param>
    /// <param name="cancellationToken">Cancellation token for async operation cancellation.</param>
    Task UpdateEventOffsetAsync(
        string sourceDomain,
        string lastEventId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the last successfully processed event ID for a given source domain.
    /// Returns null if no events have been processed for the domain (first-time processing).
    /// </summary>
    /// <param name="sourceDomain">The bounded-context domain name (e.g., "invoicing", "crm").</param>
    /// <param name="connection">The active NpgsqlConnection for read consistency.</param>
    /// <param name="cancellationToken">Cancellation token for async operation cancellation.</param>
    /// <returns>The last processed event ID, or null if none exists.</returns>
    Task<string?> GetLastProcessedEventIdAsync(
        string sourceDomain,
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether an entity belongs to a financial domain requiring soft-delete
    /// for audit trail compliance. Currently, all entities in the 'invoicing' domain
    /// are classified as financial per AAP §0.8.1 data integrity requirements.
    /// </summary>
    /// <param name="domain">The bounded-context domain name.</param>
    /// <param name="entity">The entity name within the domain.</param>
    /// <returns>True if the entity requires soft-delete for audit trail; false otherwise.</returns>
    bool IsFinancialEntity(string domain, string entity);
}

/// <summary>
/// CQRS read-model projection management service for the Reporting &amp; Analytics bounded context.
///
/// This service is the business logic layer that the EventConsumer Lambda handler delegates to.
/// The EventConsumer handles SQS message parsing, idempotency checks, and batch orchestration;
/// ProjectionService handles the actual read-model projection updates against RDS PostgreSQL.
///
/// Handles domain events from ALL bounded contexts:
///   - identity: user.created/updated/deleted
///   - entity-management: {entity}.created/updated/deleted
///   - crm: account.created/updated/deleted, contact.created/updated/deleted
///   - inventory: task.created/updated/deleted, timelog.created/updated/deleted
///   - invoicing: invoice.created/updated/voided, payment.processed (soft-delete for audit trail)
///   - notifications: email.sent/failed
///   - file-management: file.uploaded/deleted
///   - workflow: workflow.started/completed/failed
///   - plugin-system: plugin.registered/updated
///
/// Architecture notes:
///   - This is one of two ACID-critical services (Reporting + Invoicing) using RDS PostgreSQL
///   - All methods are idempotent via UPSERT/conditional-delete pattern (at-least-once SQS delivery)
///   - Transaction lifecycle (begin/commit/rollback) is managed by the calling EventConsumer
///   - Zero cross-service database access; events arrive exclusively via SNS/SQS event bus
///   - Structured JSON logging with correlation-ID propagation per AAP §0.8.5
///   - Event naming convention: {domain}.{entity}.{action} per AAP §0.8.5
/// </summary>
public class ProjectionService : IProjectionService
{
    private readonly IReportRepository _reportRepository;
    private readonly ILogger<ProjectionService> _logger;

    /// <summary>
    /// Set of domains classified as financial, requiring soft-delete for audit trail compliance.
    /// Per AAP §0.8.1 data integrity requirements, invoicing domain entities must never be
    /// permanently deleted to preserve complete audit history for financial reporting.
    /// </summary>
    private static readonly HashSet<string> FinancialDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoicing"
    };

    /// <summary>
    /// JSON serializer options configured for AOT compatibility and consistent JSONB storage.
    /// Uses snake_case naming for PostgreSQL JSONB column compatibility.
    /// Preferred over Newtonsoft.Json for Native AOT compatibility with the &lt; 1 second cold start target.
    /// </summary>
    private static readonly JsonSerializerOptions ProjectionSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectionService"/> class.
    /// </summary>
    /// <param name="reportRepository">
    /// RDS PostgreSQL data access layer providing UpsertProjectionAsync, DeleteProjectionAsync,
    /// SoftDeleteProjectionAsync, UpsertEventOffsetAsync, and GetLastEventIdAsync operations.
    /// Replaces the monolith's direct DbDataSourceRepository instantiation with DI-injected pattern.
    /// </param>
    /// <param name="logger">
    /// Structured JSON logger with correlation-ID propagation for audit trail and diagnostics.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null.</exception>
    public ProjectionService(IReportRepository reportRepository, ILogger<ProjectionService> logger)
    {
        _reportRepository = reportRepository ?? throw new ArgumentNullException(nameof(reportRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessEntityCreatedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ValidateDomainEvent(domainEvent);
        ValidateConnectionAndTransaction(connection, transaction);

        var recordId = ExtractRecordId(domainEvent);

        _logger.LogInformation(
            "Processing entity created projection for {EventType} record {RecordId}, CorrelationId: {CorrelationId}",
            domainEvent.EventType,
            recordId,
            domainEvent.CorrelationId);

        var projectionData = BuildProjectionData(domainEvent);

        try
        {
            await _reportRepository.UpsertProjectionAsync(
                domainEvent.SourceDomain,
                domainEvent.EntityName,
                recordId,
                projectionData,
                connection,
                transaction,
                cancellationToken);

            _logger.LogDebug(
                "Projection row upserted for {Domain}.{Entity} record {RecordId}",
                domainEvent.SourceDomain,
                domainEvent.EntityName,
                recordId);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(
                ex,
                "Failed to upsert projection for {EventType} record {RecordId}, CorrelationId: {CorrelationId}",
                domainEvent.EventType,
                recordId,
                domainEvent.CorrelationId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ProcessEntityUpdatedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ValidateDomainEvent(domainEvent);
        ValidateConnectionAndTransaction(connection, transaction);

        var recordId = ExtractRecordId(domainEvent);

        _logger.LogInformation(
            "Processing entity updated projection for {EventType} record {RecordId}, CorrelationId: {CorrelationId}",
            domainEvent.EventType,
            recordId,
            domainEvent.CorrelationId);

        // UPSERT pattern handles out-of-order events where an update may arrive before
        // the corresponding create event due to SQS ordering guarantees (best-effort FIFO).
        var projectionData = BuildProjectionData(domainEvent);

        try
        {
            await _reportRepository.UpsertProjectionAsync(
                domainEvent.SourceDomain,
                domainEvent.EntityName,
                recordId,
                projectionData,
                connection,
                transaction,
                cancellationToken);

            _logger.LogDebug(
                "Projection row updated for {Domain}.{Entity} record {RecordId}",
                domainEvent.SourceDomain,
                domainEvent.EntityName,
                recordId);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(
                ex,
                "Failed to update projection for {EventType} record {RecordId}, CorrelationId: {CorrelationId}",
                domainEvent.EventType,
                recordId,
                domainEvent.CorrelationId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ProcessEntityDeletedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ValidateDomainEvent(domainEvent);
        ValidateConnectionAndTransaction(connection, transaction);

        var recordId = ExtractRecordId(domainEvent);

        _logger.LogInformation(
            "Processing entity deleted projection for {EventType} record {RecordId}, CorrelationId: {CorrelationId}",
            domainEvent.EventType,
            recordId,
            domainEvent.CorrelationId);

        bool isFinancial = IsFinancialEntity(domainEvent.SourceDomain, domainEvent.EntityName);

        try
        {
            if (isFinancial)
            {
                // Financial entities (invoicing domain): soft-delete preserves audit trail.
                // Merges {"deleted": true, "deleted_at": "..."} into projection_data JSONB column
                // via the repository's SoftDeleteProjectionAsync (uses PostgreSQL || JSONB merge operator).
                await _reportRepository.SoftDeleteProjectionAsync(
                    domainEvent.SourceDomain,
                    domainEvent.EntityName,
                    recordId,
                    connection,
                    transaction,
                    cancellationToken);

                _logger.LogDebug(
                    "Projection row soft-deleted for {Domain}.{Entity} record {RecordId} (financial entity — audit trail preserved)",
                    domainEvent.SourceDomain,
                    domainEvent.EntityName,
                    recordId);
            }
            else
            {
                // Non-financial entities: hard-delete removes the row entirely.
                // Idempotent: silently succeeds if the projection row does not exist
                // (handles duplicate delete events from at-least-once SQS delivery).
                await _reportRepository.DeleteProjectionAsync(
                    domainEvent.SourceDomain,
                    domainEvent.EntityName,
                    recordId,
                    connection,
                    transaction,
                    cancellationToken);

                _logger.LogDebug(
                    "Projection row deleted for {Domain}.{Entity} record {RecordId}",
                    domainEvent.SourceDomain,
                    domainEvent.EntityName,
                    recordId);
            }
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(
                ex,
                "Failed to {DeleteType} projection for {EventType} record {RecordId}, CorrelationId: {CorrelationId}",
                isFinancial ? "soft-delete" : "delete",
                domainEvent.EventType,
                recordId,
                domainEvent.CorrelationId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateEventOffsetAsync(
        string sourceDomain,
        string lastEventId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDomain))
            throw new ArgumentException("Source domain must not be null or empty.", nameof(sourceDomain));

        if (string.IsNullOrWhiteSpace(lastEventId))
            throw new ArgumentException("Last event ID must not be null or empty.", nameof(lastEventId));

        ValidateConnectionAndTransaction(connection, transaction);

        try
        {
            await _reportRepository.UpsertEventOffsetAsync(
                sourceDomain,
                lastEventId,
                connection,
                transaction,
                cancellationToken);

            _logger.LogDebug(
                "Event offset updated for {SourceDomain}: {LastEventId}",
                sourceDomain,
                lastEventId);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(
                ex,
                "Failed to update event offset for {SourceDomain} with event {LastEventId}",
                sourceDomain,
                lastEventId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetLastProcessedEventIdAsync(
        string sourceDomain,
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDomain))
            throw new ArgumentException("Source domain must not be null or empty.", nameof(sourceDomain));

        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        try
        {
            // The repository's GetLastEventIdAsync manages its own connection for this read-only query.
            // The connection parameter is accepted in the interface for consistency but the repository
            // implementation creates a separate connection for offset reads.
            var lastEventId = await _reportRepository.GetLastEventIdAsync(
                sourceDomain,
                cancellationToken);

            _logger.LogDebug(
                "Retrieved last processed event ID for {SourceDomain}: {LastEventId}",
                sourceDomain,
                lastEventId ?? "(none — first-time processing for this domain)");

            return lastEventId;
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(
                ex,
                "Failed to retrieve last processed event ID for {SourceDomain}",
                sourceDomain);
            throw;
        }
    }

    /// <inheritdoc />
    public bool IsFinancialEntity(string domain, string entity)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return false;

        bool isFinancial = FinancialDomains.Contains(domain);

        _logger.LogDebug(
            "Financial entity check for {Domain}.{Entity}: {IsFinancial}",
            domain,
            entity ?? "(null)",
            isFinancial);

        return isFinancial;
    }

    #region Private Helper Methods

    /// <summary>
    /// Validates that a DomainEvent contains all required fields for projection processing.
    /// Enforces the event naming convention {domain}.{entity}.{action} per AAP §0.8.5.
    /// </summary>
    /// <param name="domainEvent">The domain event to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when domainEvent is null.</exception>
    /// <exception cref="ArgumentException">Thrown when required fields are missing or empty.</exception>
    private static void ValidateDomainEvent(DomainEvent domainEvent)
    {
        if (domainEvent == null)
            throw new ArgumentNullException(nameof(domainEvent));

        if (string.IsNullOrWhiteSpace(domainEvent.SourceDomain))
            throw new ArgumentException(
                "DomainEvent.SourceDomain must not be null or empty. " +
                "Event naming convention requires {domain}.{entity}.{action} format.",
                nameof(domainEvent));

        if (string.IsNullOrWhiteSpace(domainEvent.EntityName))
            throw new ArgumentException(
                "DomainEvent.EntityName must not be null or empty. " +
                "Event naming convention requires {domain}.{entity}.{action} format.",
                nameof(domainEvent));

        if (string.IsNullOrWhiteSpace(domainEvent.Action))
            throw new ArgumentException(
                "DomainEvent.Action must not be null or empty. " +
                "Event naming convention requires {domain}.{entity}.{action} format.",
                nameof(domainEvent));

        if (domainEvent.EventId == Guid.Empty)
            throw new ArgumentException(
                "DomainEvent.EventId must not be an empty GUID.",
                nameof(domainEvent));
    }

    /// <summary>
    /// Validates that the NpgsqlConnection and NpgsqlTransaction are not null.
    /// The caller (EventConsumer) is responsible for managing the transaction lifecycle.
    /// </summary>
    /// <param name="connection">The NpgsqlConnection to validate.</param>
    /// <param name="transaction">The NpgsqlTransaction to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown when connection or transaction is null.</exception>
    private static void ValidateConnectionAndTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        if (connection == null)
            throw new ArgumentNullException(nameof(connection));

        if (transaction == null)
            throw new ArgumentNullException(nameof(transaction));
    }

    /// <summary>
    /// Extracts the source record ID from the DomainEvent payload.
    /// The record ID is expected in the 'id' field of the Payload dictionary.
    /// Supports multiple value representations: native Guid, string GUID, or JsonElement.
    /// </summary>
    /// <param name="domainEvent">The domain event containing the payload with record ID.</param>
    /// <returns>The extracted source record GUID.</returns>
    /// <exception cref="ArgumentException">Thrown when no valid 'id' field is found in Payload.</exception>
    private static Guid ExtractRecordId(DomainEvent domainEvent)
    {
        if (domainEvent.Payload != null)
        {
            // Try 'id' key (standard record identifier)
            if (TryExtractGuidFromPayload(domainEvent.Payload, "id", out var recordId))
                return recordId;

            // Try 'record_id' key (alternative naming convention)
            if (TryExtractGuidFromPayload(domainEvent.Payload, "record_id", out recordId))
                return recordId;

            // Try 'Id' key (PascalCase variant for .NET service events)
            if (TryExtractGuidFromPayload(domainEvent.Payload, "Id", out recordId))
                return recordId;
        }

        throw new ArgumentException(
            "DomainEvent Payload must contain an 'id' or 'record_id' field with a valid GUID " +
            "for projection processing. Ensure the source domain service includes the record " +
            "identifier in the event payload.",
            nameof(domainEvent));
    }

    /// <summary>
    /// Attempts to extract a GUID value from the payload dictionary for a given key.
    /// Handles multiple value representations that may appear from different source domain
    /// serialization formats: native Guid, string, and JsonElement.
    /// </summary>
    /// <param name="payload">The event payload dictionary.</param>
    /// <param name="key">The key to look up.</param>
    /// <param name="result">The extracted GUID if successful.</param>
    /// <returns>True if a valid GUID was extracted; false otherwise.</returns>
    private static bool TryExtractGuidFromPayload(
        Dictionary<string, object?> payload,
        string key,
        out Guid result)
    {
        result = Guid.Empty;

        if (!payload.TryGetValue(key, out var value) || value == null)
            return false;

        // Direct Guid value (from strongly-typed .NET services)
        if (value is Guid guidValue)
        {
            result = guidValue;
            return true;
        }

        // String representation of GUID (from cross-language services or JSON deserialization)
        if (value is string stringValue && Guid.TryParse(stringValue, out var parsedGuid))
        {
            result = parsedGuid;
            return true;
        }

        // JsonElement from System.Text.Json deserialization pipeline
        if (value is JsonElement jsonElement)
        {
            var rawText = jsonElement.ValueKind == JsonValueKind.String
                ? jsonElement.GetString()
                : jsonElement.GetRawText().Trim('"');

            if (rawText != null && Guid.TryParse(rawText, out var jsonGuid))
            {
                result = jsonGuid;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the projection data as a JsonElement from the DomainEvent payload.
    /// Enriches the payload with source metadata for audit trail and tracking purposes.
    ///
    /// Metadata fields added (prefixed with underscore to avoid collision with record fields):
    ///   - _source_event_type: The full event type string ({domain}.{entity}.{action})
    ///   - _source_event_id: The unique event identifier for deduplication tracking
    ///   - _source_timestamp: ISO 8601 timestamp of the original event
    ///   - _source_correlation_id: Distributed tracing correlation identifier
    /// </summary>
    /// <param name="domainEvent">The domain event containing the record payload.</param>
    /// <returns>A JsonElement containing the enriched projection data for JSONB storage.</returns>
    private static JsonElement BuildProjectionData(DomainEvent domainEvent)
    {
        var projectionDict = new Dictionary<string, object?>();

        // Copy all record fields from the event payload, preserving original structure.
        // This ensures the read-model projection contains a complete snapshot of the
        // source record at the time of the event.
        if (domainEvent.Payload != null)
        {
            foreach (var kvp in domainEvent.Payload)
            {
                projectionDict[kvp.Key] = kvp.Value;
            }
        }

        // Add source metadata for audit trail and event tracking.
        // Underscore prefix prevents collision with record field names.
        projectionDict["_source_event_type"] = domainEvent.EventType;
        projectionDict["_source_event_id"] = domainEvent.EventId.ToString();
        projectionDict["_source_timestamp"] = domainEvent.Timestamp.ToString("O");
        projectionDict["_source_correlation_id"] = domainEvent.CorrelationId;

        // Serialize to JSON string then deserialize to JsonElement for the repository's
        // parameterized JSONB insertion. Uses System.Text.Json for AOT compatibility.
        var json = JsonSerializer.Serialize(projectionDict, ProjectionSerializerOptions);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    #endregion
}
