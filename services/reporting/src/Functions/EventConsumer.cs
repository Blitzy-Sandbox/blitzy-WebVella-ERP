// -------------------------------------------------------------------
// EventConsumer.cs — SQS-Triggered Lambda Consumer for CQRS Read-Model Updates
//
// Replaces the synchronous post-hook execution pattern from
// RecordHookManager.cs (ExecutePostCreateRecordHooks,
// ExecutePostUpdateRecordHooks, ExecutePostDeleteRecordHooks) with an
// asynchronous, event-driven SQS consumer that processes domain events
// from ALL bounded contexts to build read-optimized projections in
// RDS PostgreSQL.
//
// This is the CQRS read side — the Reporting service consumes events
// from all 9 domain services to build read-optimized projections.
// One of two ACID-critical services (with Invoicing) using RDS PostgreSQL.
//
// Architecture:
//   - SQS-triggered Lambda (NOT an HTTP handler)
//   - Processes events from: identity, entity-management, crm, inventory,
//     invoicing, notifications, file-management, workflow, plugin-system
//   - Event naming convention: {domain}.{entity}.{action}
//   - DLQ: reporting-event-consumer-dlq
//   - Partial batch failure via SQSBatchResponse
//   - ACID transactions via NpgsqlConnection / NpgsqlTransaction
//   - DB_CONNECTION_STRING from SSM SecureString (never env vars)
//   - Idempotency via reporting.processed_events table
// -------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Models;
using WebVellaErp.Reporting.Services;

// NOTE: The assembly-level [LambdaSerializer] attribute is defined in
// ReportHandler.cs. Only one per assembly is allowed — do not duplicate.

namespace WebVellaErp.Reporting.Functions;

/// <summary>
/// SNS notification envelope model for parsing SQS messages that arrive
/// via SNS-to-SQS subscription. SQS messages from SNS subscriptions are
/// double-wrapped — the outer JSON is an SNS notification containing the
/// actual domain event in the <see cref="Message"/> field.
/// </summary>
public class SnsNotification
{
    /// <summary>SNS notification type (always "Notification" for event messages).</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Unique SNS message identifier.</summary>
    [JsonPropertyName("MessageId")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Inner message payload — the actual domain event JSON string.</summary>
    [JsonPropertyName("Message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Timestamp when SNS published the notification.</summary>
    [JsonPropertyName("Timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>ARN of the source SNS topic that published this notification.</summary>
    [JsonPropertyName("TopicArn")]
    public string TopicArn { get; set; } = string.Empty;
}

#pragma warning disable IL2026 // RequiresUnreferencedCode — JsonSerializer.Deserialize in AOT context
#pragma warning disable IL3050 // RequiresDynamicCode — JsonSerializer.Deserialize in AOT context

/// <summary>
/// SQS-triggered Lambda consumer for CQRS read-model updates in the Reporting
/// &amp; Analytics bounded-context service. Replaces the monolith's synchronous
/// post-hook execution pattern (RecordHookManager.ExecutePost*RecordHooks) with
/// asynchronous event-driven SQS consumption.
///
/// Processes domain events from all 9 bounded contexts and updates read-optimized
/// projections in RDS PostgreSQL via <see cref="IProjectionService"/>.
/// </summary>
public class EventConsumer
{
    // ── Constants ──────────────────────────────────────────────────────

    /// <summary>SSM parameter path for RDS PostgreSQL connection string (SecureString).</summary>
    private const string SsmParameterName = "/reporting/db-connection-string";

    /// <summary>Table for idempotency tracking of processed events.</summary>
    private const string ProcessedEventsTable = "reporting.processed_events";

    /// <summary>SQS message attribute key for correlation ID.</summary>
    private const string CorrelationIdAttributeKey = "correlationId";

    /// <summary>Alternative SQS message attribute key for correlation ID.</summary>
    private const string AltCorrelationIdAttributeKey = "x-correlation-id";

    // ── Known Domains (per AAP §0.4.2 — all 9 bounded contexts) ──────

    private static readonly HashSet<string> KnownDomains =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "identity",
            "entity-management",
            "crm",
            "inventory",
            "invoicing",
            "notifications",
            "file-management",
            "workflow",
            "plugin-system"
        };

    /// <summary>
    /// Actions that map to create-like projection operations.
    /// These result in UPSERT to the read-model projection.
    /// </summary>
    private static readonly HashSet<string> CreateActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "created", "registered", "uploaded"
        };

    /// <summary>
    /// Actions that map to update-like projection operations.
    /// </summary>
    private static readonly HashSet<string> UpdateActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "updated", "sent", "failed", "processed", "started", "completed"
        };

    /// <summary>
    /// Actions that map to delete-like projection operations.
    /// Financial entities use soft-delete; non-financial use hard-delete.
    /// </summary>
    private static readonly HashSet<string> DeleteActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "deleted", "voided"
        };

    /// <summary>
    /// JSON serializer options for domain event deserialization.
    /// Uses case-insensitive matching and relies on [JsonPropertyName]
    /// attributes on DomainEvent and SnsNotification for property binding.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Instance Fields ───────────────────────────────────────────────

    private readonly IAmazonSimpleSystemsManagement _ssmClient;
    private readonly ILogger<EventConsumer> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Projection service for processing entity created/updated/deleted
    /// events. Lazily initialized after connection string retrieval.
    /// </summary>
    private IProjectionService? _projectionService;

    /// <summary>
    /// Report repository for event offset tracking and projection access.
    /// Lazily initialized after connection string retrieval from SSM.
    /// </summary>
    private IReportRepository? _reportRepository;

    /// <summary>
    /// Cached connection string retrieved from SSM Parameter Store.
    /// Cached for Lambda container reuse optimization.
    /// </summary>
    private string? _cachedConnectionString;

    // ── Constructors ──────────────────────────────────────────────────

    /// <summary>
    /// Parameterless constructor for AWS Lambda runtime.
    /// Sets up logging and SSM client during cold start. Data services
    /// (IProjectionService, IReportRepository) are lazily initialized on
    /// first invocation when the connection string becomes available.
    /// </summary>
    public EventConsumer()
    {
        var services = new ServiceCollection();

        // Create SSM client with LocalStack support
        _ssmClient = CreateSsmClient();
        services.AddSingleton<IAmazonSimpleSystemsManagement>(_ssmClient);

        // Configure structured JSON logging per AAP §0.8.5
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(
                string.Equals(
                    Environment.GetEnvironmentVariable("IS_LOCAL"),
                    "true",
                    StringComparison.OrdinalIgnoreCase)
                    ? Microsoft.Extensions.Logging.LogLevel.Debug
                    : Microsoft.Extensions.Logging.LogLevel.Information);
        });

        var serviceProvider = services.BuildServiceProvider();
        _logger = serviceProvider.GetRequiredService<ILogger<EventConsumer>>();
        _loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
    }

    /// <summary>
    /// Constructor for unit testing with pre-configured service provider.
    /// All dependencies are resolved from the provided container.
    /// </summary>
    /// <param name="serviceProvider">
    /// Pre-configured DI container with all required services.
    /// </param>
    public EventConsumer(IServiceProvider serviceProvider)
    {
        _ssmClient = serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
        _logger = serviceProvider.GetRequiredService<ILogger<EventConsumer>>();
        _loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _projectionService = serviceProvider.GetRequiredService<IProjectionService>();
        _reportRepository = serviceProvider.GetRequiredService<IReportRepository>();
    }

    // ── Primary Handler ───────────────────────────────────────────────

    /// <summary>
    /// SQS-triggered Lambda entry point for consuming domain events from
    /// all bounded contexts. Processes each message independently and
    /// reports partial batch failures via <see cref="SQSBatchResponse"/>
    /// so only failed messages are retried.
    ///
    /// Replaces the synchronous post-hook execution pipeline from
    /// <c>RecordHookManager.ExecutePostCreate/Update/DeleteRecordHooks</c>.
    /// </summary>
    /// <param name="sqsEvent">
    /// Batch of SQS messages delivered by the Lambda runtime.
    /// Each message wraps an SNS notification containing a domain event.
    /// </param>
    /// <param name="context">Lambda invocation context for tracing.</param>
    /// <returns>
    /// <see cref="SQSBatchResponse"/> listing any messages that failed
    /// processing. Successfully processed messages are removed from the
    /// queue; failed ones are retried up to the queue's maxReceiveCount
    /// before being sent to <c>reporting-event-consumer-dlq</c>.
    /// </returns>
    public async Task<SQSBatchResponse> HandleSqsEvent(
        SQSEvent sqsEvent,
        ILambdaContext context)
    {
        var batchSize = sqsEvent?.Records?.Count ?? 0;
        _logger.LogInformation(
            "EventConsumer: Starting batch processing. BatchSize={BatchSize} RequestId={RequestId}",
            batchSize,
            context.AwsRequestId);

        if (batchSize == 0)
        {
            _logger.LogWarning("EventConsumer: Received empty SQS batch.");
            return new SQSBatchResponse { BatchItemFailures = new List<SQSBatchResponse.BatchItemFailure>() };
        }

        // Ensure data services are ready (lazy-init on first invocation)
        await EnsureServicesInitializedAsync().ConfigureAwait(false);

        var batchItemFailures = new List<SQSBatchResponse.BatchItemFailure>();
        var successCount = 0;
        var skipCount = 0;
        var failCount = 0;

        foreach (var message in sqsEvent!.Records)
        {
            var correlationId = ExtractCorrelationId(message);
            using var logScope = _logger.BeginScope(
                new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId,
                    ["SqsMessageId"] = message.MessageId
                });

            try
            {
                var processed = await ProcessMessageAsync(
                    message,
                    correlationId,
                    CancellationToken.None)
                    .ConfigureAwait(false);

                if (processed)
                {
                    successCount++;
                }
                else
                {
                    // Message was skipped (duplicate / unknown event type)
                    skipCount++;
                }
            }
            catch (Exception ex)
            {
                failCount++;
                _logger.LogError(
                    ex,
                    "EventConsumer: Failed to process SQS message. " +
                    "MessageId={MessageId} CorrelationId={CorrelationId}",
                    message.MessageId,
                    correlationId);

                batchItemFailures.Add(
                    new SQSBatchResponse.BatchItemFailure
                    {
                        ItemIdentifier = message.MessageId
                    });
            }
        }

        _logger.LogInformation(
            "EventConsumer: Batch complete. " +
            "Total={Total} Success={Success} Skipped={Skipped} Failed={Failed} RequestId={RequestId}",
            batchSize,
            successCount,
            skipCount,
            failCount,
            context.AwsRequestId);

        // Log last processed event offset per domain for operational monitoring.
        // Uses IReportRepository.GetLastEventIdAsync directly for offset queries
        // that do not require a transaction context.
        if (successCount > 0)
        {
            await LogDomainOffsetsAsync(sqsEvent!.Records, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return new SQSBatchResponse { BatchItemFailures = batchItemFailures };
    }

    // ── Message Processing ────────────────────────────────────────────

    /// <summary>
    /// Processes a single SQS message containing an SNS-wrapped domain
    /// event. Opens a dedicated NpgsqlConnection with an explicit
    /// transaction for ACID guarantees on the read-model projection.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the event was processed; <c>false</c> if skipped
    /// (duplicate or unknown event type).
    /// </returns>
    private async Task<bool> ProcessMessageAsync(
        SQSEvent.SQSMessage message,
        string correlationId,
        CancellationToken cancellationToken)
    {
        // 1. Parse SNS envelope and extract DomainEvent
        var domainEvent = ParseDomainEventFromSnsMessage(message.Body);
        if (domainEvent == null)
        {
            _logger.LogWarning(
                "EventConsumer: Could not parse domain event from message body. " +
                "MessageId={MessageId}",
                message.MessageId);
            return false;
        }

        // Prefer the CorrelationId carried inside the domain event envelope
        // when available; fall back to the one extracted from SQS attributes.
        var effectiveCorrelationId = !string.IsNullOrWhiteSpace(domainEvent.CorrelationId)
            ? domainEvent.CorrelationId
            : correlationId;

        // Propagate the resolved correlation ID back onto the event so downstream
        // processors (e.g., BuildProjectionData) can include it in projection metadata.
        domainEvent.CorrelationId = effectiveCorrelationId;

        // Enrich log scope with event details
        using var eventScope = _logger.BeginScope(
            new Dictionary<string, object>
            {
                ["EventId"] = domainEvent.EventId,
                ["EventType"] = domainEvent.EventType,
                ["SourceDomain"] = domainEvent.SourceDomain,
                ["EntityName"] = domainEvent.EntityName,
                ["Action"] = domainEvent.Action,
                ["EventTimestamp"] = domainEvent.Timestamp,
                ["EffectiveCorrelationId"] = effectiveCorrelationId,
                ["HasPayload"] = domainEvent.Payload != null && domainEvent.Payload.Count > 0
            });

        // 2. Validate event domain
        if (!KnownDomains.Contains(domainEvent.SourceDomain))
        {
            _logger.LogWarning(
                "EventConsumer: Unknown source domain. " +
                "Domain={Domain} EventType={EventType}",
                domainEvent.SourceDomain,
                domainEvent.EventType);
            return false;
        }

        // 3. Determine action category
        if (!CreateActions.Contains(domainEvent.Action) &&
            !UpdateActions.Contains(domainEvent.Action) &&
            !DeleteActions.Contains(domainEvent.Action))
        {
            _logger.LogWarning(
                "EventConsumer: Unknown action type. " +
                "Action={Action} EventType={EventType}",
                domainEvent.Action,
                domainEvent.EventType);
            return false;
        }

        // 4. Open dedicated connection and begin ACID transaction
        var connectionString = await GetConnectionStringAsync(cancellationToken)
            .ConfigureAwait(false);

        var (connection, transaction) = await CreateDatabaseScopeAsync(
            connectionString, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "EventConsumer: Transaction started for EventId={EventId}",
            domainEvent.EventId);

        try
        {
            // 5. Idempotency check — skip if already processed
            var alreadyProcessed = await IsEventAlreadyProcessedAsync(
                domainEvent.EventId,
                connection,
                transaction,
                cancellationToken)
                .ConfigureAwait(false);

            if (alreadyProcessed)
            {
                _logger.LogWarning(
                    "EventConsumer: Duplicate event skipped. " +
                    "EventId={EventId} EventType={EventType}",
                    domainEvent.EventId,
                    domainEvent.EventType);
                return false;
            }

            // 6. Route to the appropriate entity handler.
            //    Log Payload presence for diagnostics (Payload may be null for deletes).
            _logger.LogDebug(
                "EventConsumer: Routing event. " +
                "EventId={EventId} Action={Action} " +
                "PayloadKeys={PayloadKeyCount} EventTimestamp={EventTimestamp:O}",
                domainEvent.EventId,
                domainEvent.Action,
                domainEvent.Payload?.Count ?? 0,
                domainEvent.Timestamp);

            if (CreateActions.Contains(domainEvent.Action))
            {
                await HandleEntityCreatedAsync(
                    domainEvent, connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (UpdateActions.Contains(domainEvent.Action))
            {
                await HandleEntityUpdatedAsync(
                    domainEvent, connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (DeleteActions.Contains(domainEvent.Action))
            {
                await HandleEntityDeletedAsync(
                    domainEvent, connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 7. Record the event as processed (idempotency marker)
            await RecordProcessedEventAsync(
                domainEvent.EventId,
                domainEvent.EventType,
                connection,
                transaction,
                cancellationToken)
                .ConfigureAwait(false);

            // 8. Update event offset for this domain for monitoring.
            //    Directly accesses IReportRepository for offset tracking
            //    rather than going through ProjectionService.
            await _reportRepository!.UpsertEventOffsetAsync(
                domainEvent.SourceDomain,
                domainEvent.EventId.ToString(),
                connection,
                transaction,
                cancellationToken)
                .ConfigureAwait(false);

            // 9. Commit the ACID transaction
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogDebug(
                "EventConsumer: Transaction committed for EventId={EventId}",
                domainEvent.EventId);

            return true;
        }
        catch (Exception)
        {
            // Rollback on any failure — let the exception propagate for
            // SQS retry / DLQ handling
            _logger.LogDebug(
                "EventConsumer: Rolling back transaction for EventId={EventId}",
                domainEvent.EventId);

            if (transaction != null)
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(
                        rollbackEx,
                        "EventConsumer: Rollback failed for EventId={EventId}",
                        domainEvent.EventId);
                }
            }

            throw; // Propagate so HandleSqsEvent records BatchItemFailure
        }
        finally
        {
            // Dispose connection and transaction resources
            if (transaction != null) await transaction.DisposeAsync();
            if (connection != null) await connection.DisposeAsync();
        }
    }

    // ── SNS / Domain-Event Parsing ────────────────────────────────────

    /// <summary>
    /// Parses a domain event from an SQS message body that wraps an SNS
    /// notification envelope. SQS messages from SNS subscriptions are
    /// double-wrapped: the SQS body contains the SNS notification JSON,
    /// whose <c>Message</c> field contains the actual event payload.
    /// </summary>
    /// <param name="messageBody">Raw SQS message body (SNS envelope).</param>
    /// <returns>
    /// Parsed <see cref="DomainEvent"/> or <c>null</c> if the body
    /// could not be parsed.
    /// </returns>
    private DomainEvent? ParseDomainEventFromSnsMessage(string messageBody)
    {
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            return null;
        }

        try
        {
            // Attempt to unwrap the SNS notification envelope
            var snsNotification = JsonSerializer.Deserialize<SnsNotification>(
                messageBody, JsonOptions);

            string innerMessage;
            if (snsNotification != null &&
                !string.IsNullOrWhiteSpace(snsNotification.Message) &&
                string.Equals(snsNotification.Type, "Notification", StringComparison.OrdinalIgnoreCase))
            {
                // Standard SNS → SQS path: extract inner message
                innerMessage = snsNotification.Message;
                _logger.LogDebug(
                    "EventConsumer: Unwrapped SNS envelope. " +
                    "SnsMessageId={SnsMessageId} TopicArn={TopicArn}",
                    snsNotification.MessageId ?? "unknown",
                    snsNotification.TopicArn ?? "unknown");
            }
            else
            {
                // Fallback: message might be a direct SQS publish (no SNS wrapper)
                innerMessage = messageBody;
                _logger.LogDebug(
                    "EventConsumer: No SNS envelope detected; treating body as direct event.");
            }

            // Deserialize the domain event from the inner payload
            var domainEvent = JsonSerializer.Deserialize<DomainEvent>(
                innerMessage, JsonOptions);

            if (domainEvent == null)
            {
                _logger.LogWarning(
                    "EventConsumer: Deserialized domain event is null.");
                return null;
            }

            // Validate required fields
            if (string.IsNullOrWhiteSpace(domainEvent.SourceDomain) ||
                string.IsNullOrWhiteSpace(domainEvent.EntityName) ||
                string.IsNullOrWhiteSpace(domainEvent.Action))
            {
                _logger.LogWarning(
                    "EventConsumer: Domain event missing required fields. " +
                    "Domain={Domain} Entity={Entity} Action={Action}",
                    domainEvent.SourceDomain ?? "(null)",
                    domainEvent.EntityName ?? "(null)",
                    domainEvent.Action ?? "(null)");
                return null;
            }

            // Ensure EventId is set (generate fallback for older producers)
            if (domainEvent.EventId == Guid.Empty)
            {
                var generated = Guid.NewGuid();
                _logger.LogWarning(
                    "EventConsumer: DomainEvent.EventId is empty; " +
                    "generated fallback {FallbackId}",
                    generated);
                domainEvent.EventId = generated;
            }

            return domainEvent;
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "EventConsumer: JSON deserialization failed for message body.");
            return null;
        }
    }

    // ── Entity Event Handlers ─────────────────────────────────────────

    /// <summary>
    /// Handles entity-created events by delegating to the
    /// <see cref="IProjectionService"/> to UPSERT a read-model
    /// projection row. Replaces
    /// <c>IErpPostCreateRecordHook.OnPostCreateRecord</c>.
    /// </summary>
    private async Task HandleEntityCreatedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "EventConsumer: Processing entity created. " +
            "Domain={Domain} Entity={Entity} EventId={EventId}",
            domainEvent.SourceDomain,
            domainEvent.EntityName,
            domainEvent.EventId);

        await _projectionService!.ProcessEntityCreatedAsync(
            domainEvent, connection!, transaction!, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "EventConsumer: Read-model projection created for " +
            "{Domain}.{Entity} EventId={EventId}",
            domainEvent.SourceDomain,
            domainEvent.EntityName,
            domainEvent.EventId);
    }

    /// <summary>
    /// Handles entity-updated events by delegating to the
    /// <see cref="IProjectionService"/> to UPDATE (or UPSERT for
    /// out-of-order event resilience) a read-model projection row.
    /// Replaces <c>IErpPostUpdateRecordHook.OnPostUpdateRecord</c>.
    /// </summary>
    private async Task HandleEntityUpdatedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "EventConsumer: Processing entity updated. " +
            "Domain={Domain} Entity={Entity} EventId={EventId}",
            domainEvent.SourceDomain,
            domainEvent.EntityName,
            domainEvent.EventId);

        await _projectionService!.ProcessEntityUpdatedAsync(
            domainEvent, connection!, transaction!, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "EventConsumer: Read-model projection updated for " +
            "{Domain}.{Entity} EventId={EventId}",
            domainEvent.SourceDomain,
            domainEvent.EntityName,
            domainEvent.EventId);
    }

    /// <summary>
    /// Handles entity-deleted events. For financial entities (invoicing
    /// domain) a soft-delete is applied to preserve audit trails; for
    /// all other entities a hard-delete removes the projection row.
    /// Replaces <c>IErpPostDeleteRecordHook.OnPostDeleteRecord</c>.
    /// </summary>
    private async Task HandleEntityDeletedAsync(
        DomainEvent domainEvent,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var isFinancial = _projectionService!.IsFinancialEntity(
            domainEvent.SourceDomain,
            domainEvent.EntityName);

        _logger.LogInformation(
            "EventConsumer: Processing entity deleted. " +
            "Domain={Domain} Entity={Entity} EventId={EventId} Financial={IsFinancial}",
            domainEvent.SourceDomain,
            domainEvent.EntityName,
            domainEvent.EventId,
            isFinancial);

        await _projectionService.ProcessEntityDeletedAsync(
            domainEvent, connection!, transaction!, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "EventConsumer: Read-model projection {DeleteType} for " +
            "{Domain}.{Entity} EventId={EventId}",
            isFinancial ? "soft-deleted" : "hard-deleted",
            domainEvent.SourceDomain,
            domainEvent.EntityName,
            domainEvent.EventId);
    }

    // ── Idempotency Helpers ───────────────────────────────────────────

    /// <summary>
    /// Checks the <c>reporting.processed_events</c> table to determine
    /// whether the specified event has already been processed. This
    /// implements the idempotency guarantee required by the at-least-once
    /// delivery model of SQS (per AAP §0.8.5).
    /// </summary>
    protected virtual async Task<bool> IsEventAlreadyProcessedAsync(
        Guid eventId,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT 1 FROM reporting.processed_events WHERE event_id = @eventId LIMIT 1";

        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter("@eventId", eventId.ToString()));

        var result = await cmd.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);

        return result != null;
    }

    /// <summary>
    /// Records a successfully processed event in the
    /// <c>reporting.processed_events</c> table using
    /// <c>ON CONFLICT DO NOTHING</c> to handle races gracefully.
    /// </summary>
    protected virtual async Task RecordProcessedEventAsync(
        Guid eventId,
        string eventType,
        NpgsqlConnection? connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        const string sql = @"
            INSERT INTO reporting.processed_events (event_id, event_type, processed_at)
            VALUES (@eventId, @eventType, @processedAt)
            ON CONFLICT DO NOTHING";

        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter("@eventId", eventId.ToString()));
        cmd.Parameters.Add(new NpgsqlParameter("@eventType", eventType));
        cmd.Parameters.Add(new NpgsqlParameter("@processedAt", DateTime.UtcNow));

        await cmd.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Database Scope Factory ────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="NpgsqlConnection"/> and begins a
    /// transaction for ACID-safe event processing. This method is
    /// <c>protected virtual</c> so that unit tests can override it
    /// to return <c>(null, null)</c> — avoiding real database I/O
    /// while still exercising the full event-routing pipeline via
    /// mocked <see cref="IProjectionService"/> and
    /// <see cref="IReportRepository"/> instances.
    /// </summary>
    protected virtual async Task<(NpgsqlConnection? Connection, NpgsqlTransaction? Transaction)>
        CreateDatabaseScopeAsync(
            string connectionString,
            CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        return (connection, transaction);
    }

    // ── Connection & Service Initialization ───────────────────────────

    /// <summary>
    /// Retrieves the <c>DB_CONNECTION_STRING</c> from AWS SSM Parameter
    /// Store (SecureString). The value is cached for the lifetime of the
    /// Lambda container to avoid repeated SSM calls on warm invocations.
    /// Per AAP §0.8.6: secrets are stored as SSM SecureString — NEVER
    /// as environment variables.
    /// </summary>
    private async Task<string> GetConnectionStringAsync(
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_cachedConnectionString))
        {
            return _cachedConnectionString;
        }

        _logger.LogDebug(
            "EventConsumer: Retrieving connection string from SSM " +
            "parameter {ParamName}",
            SsmParameterName);

        var response = await _ssmClient.GetParameterAsync(
            new GetParameterRequest
            {
                Name = SsmParameterName,
                WithDecryption = true
            },
            cancellationToken)
            .ConfigureAwait(false);

        _cachedConnectionString = response.Parameter.Value;

        _logger.LogInformation(
            "EventConsumer: Connection string retrieved from SSM successfully.");

        return _cachedConnectionString;
    }

    /// <summary>
    /// Creates an <see cref="IAmazonSimpleSystemsManagement"/> client.
    /// Respects the <c>AWS_ENDPOINT_URL</c> environment variable for
    /// LocalStack targeting (per AAP §0.8.6).
    /// </summary>
    private static IAmazonSimpleSystemsManagement CreateSsmClient()
    {
        var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

        if (!string.IsNullOrEmpty(endpointUrl))
        {
            var config = new AmazonSimpleSystemsManagementConfig
            {
                ServiceURL = endpointUrl,
                AuthenticationRegion = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1"
            };
            return new AmazonSimpleSystemsManagementClient(config);
        }

        return new AmazonSimpleSystemsManagementClient();
    }

    /// <summary>
    /// Lazily initializes the <see cref="IReportRepository"/> and
    /// <see cref="IProjectionService"/> on the first invocation.
    /// These depend on the connection string which is only available
    /// after an SSM call, so they cannot be set in the cold-start
    /// constructor.
    /// </summary>
    private async Task EnsureServicesInitializedAsync()
    {
        if (_projectionService != null && _reportRepository != null)
        {
            return;
        }

        var connectionString = await GetConnectionStringAsync()
            .ConfigureAwait(false);

        _reportRepository ??= new ReportRepository(
            connectionString,
            _loggerFactory.CreateLogger<ReportRepository>());
        _projectionService ??= new ProjectionService(
            _reportRepository,
            _loggerFactory.CreateLogger<ProjectionService>());

        _logger.LogDebug(
            "EventConsumer: Data services initialized for Lambda container.");
    }

    // ── Correlation ID ────────────────────────────────────────────────

    /// <summary>
    /// Logs the last processed event ID for each domain that appeared in
    /// the current batch for operational monitoring. Calls
    /// <see cref="IReportRepository.GetLastEventIdAsync"/> directly.
    /// </summary>
    private async Task LogDomainOffsetsAsync(
        IEnumerable<SQSEvent.SQSMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            // Collect distinct domains from the batch
            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var msg in messages)
            {
                var domainEvent = ParseDomainEventFromSnsMessage(msg.Body);
                if (domainEvent != null &&
                    !string.IsNullOrWhiteSpace(domainEvent.SourceDomain))
                {
                    domains.Add(domainEvent.SourceDomain);
                }
            }

            foreach (var domain in domains)
            {
                var lastEventId = await _reportRepository!.GetLastEventIdAsync(
                    domain, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "EventConsumer: Domain offset — " +
                    "Domain={Domain} LastEventId={LastEventId}",
                    domain,
                    lastEventId ?? "(none)");
            }
        }
        catch (Exception ex)
        {
            // Non-critical monitoring — log and continue
            _logger.LogWarning(
                ex,
                "EventConsumer: Failed to log domain offsets after batch.");
        }
    }

    /// <summary>
    /// Extracts a correlation-ID from the SQS message attributes.
    /// Checks both <c>correlationId</c> and <c>x-correlation-id</c>
    /// attribute keys. Falls back to the SQS <c>MessageId</c> if
    /// neither attribute is present.
    /// </summary>
    private string ExtractCorrelationId(SQSEvent.SQSMessage message)
    {
        if (message.MessageAttributes != null)
        {
            if (message.MessageAttributes.TryGetValue(
                    CorrelationIdAttributeKey, out var attr)
                && !string.IsNullOrWhiteSpace(attr?.StringValue))
            {
                return attr.StringValue;
            }

            if (message.MessageAttributes.TryGetValue(
                    AltCorrelationIdAttributeKey, out var altAttr)
                && !string.IsNullOrWhiteSpace(altAttr?.StringValue))
            {
                return altAttr.StringValue;
            }
        }

        return message.MessageId ?? Guid.NewGuid().ToString();
    }
}

#pragma warning restore IL2026, IL3050