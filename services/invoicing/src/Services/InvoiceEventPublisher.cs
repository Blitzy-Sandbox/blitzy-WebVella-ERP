using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.Invoicing.Models;

namespace WebVellaErp.Invoicing.Services;

/// <summary>
/// Defines the contract for publishing invoice and payment domain events via SNS.
/// Replaces the monolith's synchronous RecordHookManager post-CRUD hook dispatch
/// with asynchronous SNS topic-based domain events for cross-service communication.
/// </summary>
public interface IInvoiceEventPublisher
{
    /// <summary>
    /// Publishes an <c>invoicing.invoice.created</c> event when a new invoice is created.
    /// Replaces: <c>RecordHookManager.ExecutePostCreateRecordHooks(entityName, record)</c>
    /// from source RecordManager.cs lines 770-775.
    /// </summary>
    /// <param name="invoice">The newly created invoice entity.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    Task PublishInvoiceCreatedAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Publishes an <c>invoicing.invoice.updated</c> event when invoice details are modified
    /// (line items, dates, notes, etc.).
    /// Replaces: <c>RecordHookManager.ExecutePostUpdateRecordHooks(entityName, record)</c>
    /// from source RecordManager.cs lines 1544-1546.
    /// </summary>
    /// <param name="invoice">The updated invoice entity.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    Task PublishInvoiceUpdatedAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Publishes an <c>invoicing.invoice.issued</c> event when an invoice transitions
    /// from Draft to Issued status.
    /// </summary>
    /// <param name="invoice">The issued invoice entity.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    Task PublishInvoiceIssuedAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Publishes an <c>invoicing.invoice.paid</c> event when an invoice is fully paid.
    /// Per AAP §0.7.2: Post-update hook for status change maps to domain event.
    /// </summary>
    /// <param name="invoice">The paid invoice entity.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    Task PublishInvoicePaidAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Publishes an <c>invoicing.invoice.voided</c> event when an invoice is cancelled/voided.
    /// Replaces: <c>RecordHookManager.ExecutePostDeleteRecordHooks(entityName, record)</c>
    /// from source RecordManager.cs lines 1707-1708.
    /// </summary>
    /// <param name="invoice">The voided invoice entity.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    Task PublishInvoiceVoidedAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Publishes an <c>invoicing.payment.processed</c> event when a payment is recorded
    /// against an invoice. Replaces post-create hooks for payment records.
    /// </summary>
    /// <param name="payment">The processed payment entity.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    Task PublishPaymentProcessedAsync(Payment payment, CancellationToken ct = default);
}

/// <summary>
/// Publishes invoice and payment domain events to SNS topics, replacing the monolith's
/// synchronous hook system (<c>RecordHookManager</c> + <c>HookManager</c>) with asynchronous
/// event-driven architecture.
/// 
/// <para>
/// Events follow the naming convention: <c>{domain}.{entity}.{action}</c> per AAP §0.8.5.
/// All events use a standardized <c>DomainEvent</c> envelope with eventType, eventId,
/// timestamp, correlationId, source, version, and typed payload.
/// </para>
/// 
/// <para>
/// Error handling follows a fire-and-forget pattern matching the source RecordManager.cs
/// post-hook behavior: event publishing failures are logged but do NOT throw, ensuring
/// persistence operations are not rolled back due to event publishing issues.
/// </para>
/// 
/// <para>
/// The SNS client respects the <c>AWS_ENDPOINT_URL</c> environment variable for LocalStack
/// compatibility (per AAP §0.8.6). Topic ARNs are injected via environment variables
/// <c>INVOICE_SNS_TOPIC_ARN</c> and <c>PAYMENT_SNS_TOPIC_ARN</c>.
/// </para>
/// </summary>
public sealed partial class InvoiceEventPublisher : IInvoiceEventPublisher
{
    /// <summary>
    /// Identifies this service as the event source in domain event envelopes.
    /// </summary>
    private const string SourceService = "invoicing-service";

    /// <summary>
    /// Current event schema version. Incremented when breaking changes are made
    /// to the event envelope or payload structure.
    /// </summary>
    private const int EventSchemaVersion = 1;

    // Event type constants following {domain}.{entity}.{action} naming convention (AAP §0.8.5)
    private const string InvoiceCreatedEventType = "invoicing.invoice.created";
    private const string InvoiceUpdatedEventType = "invoicing.invoice.updated";
    private const string InvoiceIssuedEventType = "invoicing.invoice.issued";
    private const string InvoicePaidEventType = "invoicing.invoice.paid";
    private const string InvoiceVoidedEventType = "invoicing.invoice.voided";
    private const string PaymentProcessedEventType = "invoicing.payment.processed";

    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly ILogger<InvoiceEventPublisher> _logger;
    private readonly string _invoiceTopicArn;
    private readonly string _paymentTopicArn;

    /// <summary>
    /// Source-generated JSON serializer context for AOT-compatible serialization.
    /// Per AAP §0.5.2 import transformation rules: use System.Text.Json source
    /// generation instead of reflection-based serialization for Native AOT Lambda
    /// compatibility with sub-1-second cold start target. Eliminates IL2026/IL3050
    /// trimming warnings by providing compile-time type metadata.
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    [JsonSerializable(typeof(DomainEvent<Invoice>))]
    [JsonSerializable(typeof(DomainEvent<Payment>))]
    private sealed partial class EventJsonContext : JsonSerializerContext { }

    /// <summary>
    /// Initializes the <see cref="InvoiceEventPublisher"/> with required SNS client
    /// and logger dependencies. Topic ARNs are read from environment variables
    /// <c>INVOICE_SNS_TOPIC_ARN</c> and <c>PAYMENT_SNS_TOPIC_ARN</c> (non-secret
    /// config per AAP §0.8.6).
    /// </summary>
    /// <param name="snsClient">
    /// AWS SNS client injected via DI. Automatically respects <c>AWS_ENDPOINT_URL</c>
    /// environment variable for LocalStack compatibility (http://localhost:4566).
    /// Replaces the monolith's reflection-based <c>HookManager</c> discovery.
    /// </param>
    /// <param name="logger">
    /// Structured logger for correlation-ID propagation per AAP §0.8.5.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when snsClient or logger is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when required topic ARN environment variables are not configured.
    /// </exception>
    public InvoiceEventPublisher(
        IAmazonSimpleNotificationService snsClient,
        ILogger<InvoiceEventPublisher> logger)
    {
        _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _invoiceTopicArn = Environment.GetEnvironmentVariable("INVOICE_SNS_TOPIC_ARN")
            ?? throw new InvalidOperationException(
                "INVOICE_SNS_TOPIC_ARN environment variable is not configured. " +
                "Set this to the SNS topic ARN for invoice domain events.");

        _paymentTopicArn = Environment.GetEnvironmentVariable("PAYMENT_SNS_TOPIC_ARN")
            ?? throw new InvalidOperationException(
                "PAYMENT_SNS_TOPIC_ARN environment variable is not configured. " +
                "Set this to the SNS topic ARN for payment domain events.");
    }

    /// <inheritdoc />
    public async Task PublishInvoiceCreatedAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        await PublishInvoiceEventAsync(invoice, InvoiceCreatedEventType, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishInvoiceUpdatedAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        await PublishInvoiceEventAsync(invoice, InvoiceUpdatedEventType, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishInvoiceIssuedAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        await PublishInvoiceEventAsync(invoice, InvoiceIssuedEventType, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishInvoicePaidAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        await PublishInvoiceEventAsync(invoice, InvoicePaidEventType, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishInvoiceVoidedAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        await PublishInvoiceEventAsync(invoice, InvoiceVoidedEventType, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishPaymentProcessedAsync(Payment payment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var correlationId = GetCorrelationId();

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["PaymentId"] = payment.Id,
            ["InvoiceId"] = payment.InvoiceId,
            ["Amount"] = payment.Amount
        }))
        {
            try
            {
                var domainEvent = new DomainEvent<Payment>
                {
                    EventType = PaymentProcessedEventType,
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = correlationId,
                    Source = SourceService,
                    Version = EventSchemaVersion,
                    Payload = payment
                };

                var messageBody = JsonSerializer.Serialize(domainEvent, typeof(DomainEvent<Payment>), EventJsonContext.Default);

                var publishRequest = new PublishRequest
                {
                    TopicArn = _paymentTopicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = PaymentProcessedEventType
                        },
                        ["entityId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = payment.Id.ToString()
                        },
                        ["invoiceId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = payment.InvoiceId.ToString()
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Published {EventType} event for payment {PaymentId} on invoice {InvoiceId}, amount {Amount}",
                    PaymentProcessedEventType,
                    payment.Id,
                    payment.InvoiceId,
                    payment.Amount);
            }
            catch (AmazonSimpleNotificationServiceException ex)
            {
                // Fire-and-forget: event publishing failure must NOT roll back the persisted payment.
                // Matches source pattern where post-hooks in RecordManager.cs are called AFTER
                // successful persistence (lines 770-776) and do not affect the transaction outcome.
                _logger.LogError(
                    ex,
                    "Failed to publish {EventType} event for payment {PaymentId} on invoice {InvoiceId}. " +
                    "SNS error: {ErrorCode} - {ErrorMessage}",
                    PaymentProcessedEventType,
                    payment.Id,
                    payment.InvoiceId,
                    ex.ErrorCode,
                    ex.Message);
            }
            catch (Exception ex)
            {
                // Catch all other exceptions to maintain fire-and-forget resilience.
                // Domain event publishing should never cause the calling operation to fail.
                _logger.LogError(
                    ex,
                    "Unexpected error publishing {EventType} event for payment {PaymentId} on invoice {InvoiceId}",
                    PaymentProcessedEventType,
                    payment.Id,
                    payment.InvoiceId);
            }
        }
    }

    /// <summary>
    /// Centralized invoice event publishing method. All invoice lifecycle events
    /// (created, updated, issued, paid, voided) follow the same pattern with
    /// different event types. This reduces code duplication while maintaining
    /// consistent behavior across all event types.
    /// 
    /// <para>
    /// The method builds a <see cref="DomainEvent{T}"/> envelope, serializes it
    /// to JSON via System.Text.Json, publishes to the invoice SNS topic with
    /// message attributes for SQS subscription filter policies, and logs the
    /// outcome. All errors are caught and logged (fire-and-forget pattern).
    /// </para>
    /// </summary>
    /// <param name="invoice">The invoice entity to include as the event payload.</param>
    /// <param name="eventType">
    /// The event type string following <c>{domain}.{entity}.{action}</c> convention.
    /// </param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation propagation.</param>
    private async Task PublishInvoiceEventAsync(Invoice invoice, string eventType, CancellationToken ct)
    {
        var correlationId = GetCorrelationId();

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["InvoiceId"] = invoice.Id,
            ["InvoiceNumber"] = invoice.InvoiceNumber,
            ["InvoiceStatus"] = invoice.Status.ToString(),
            ["TotalAmount"] = invoice.TotalAmount
        }))
        {
            try
            {
                var domainEvent = new DomainEvent<Invoice>
                {
                    EventType = eventType,
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = correlationId,
                    Source = SourceService,
                    Version = EventSchemaVersion,
                    Payload = invoice
                };

                var messageBody = JsonSerializer.Serialize(domainEvent, typeof(DomainEvent<Invoice>), EventJsonContext.Default);

                var publishRequest = new PublishRequest
                {
                    TopicArn = _invoiceTopicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["entityId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = invoice.Id.ToString()
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Published {EventType} event for invoice {InvoiceId} " +
                    "(Number: {InvoiceNumber}, Status: {Status}, Total: {TotalAmount})",
                    eventType,
                    invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.Status,
                    invoice.TotalAmount);
            }
            catch (AmazonSimpleNotificationServiceException ex)
            {
                // Fire-and-forget: event publishing failure must NOT roll back the persisted invoice.
                // This matches the source RecordManager.cs pattern where post-hooks (lines 770-776,
                // 1544-1546, 1707-1708) are called AFTER successful persistence and do not affect
                // the transaction outcome.
                _logger.LogError(
                    ex,
                    "Failed to publish {EventType} event for invoice {InvoiceId}. " +
                    "SNS error: {ErrorCode} - {ErrorMessage}",
                    eventType,
                    invoice.Id,
                    ex.ErrorCode,
                    ex.Message);
            }
            catch (Exception ex)
            {
                // Catch all other exceptions to maintain fire-and-forget resilience.
                // Domain event publishing should never cause the calling operation to fail.
                _logger.LogError(
                    ex,
                    "Unexpected error publishing {EventType} event for invoice {InvoiceId}",
                    eventType,
                    invoice.Id);
            }
        }
    }

    /// <summary>
    /// Extracts correlation ID from the current execution context for distributed tracing.
    /// Uses <see cref="Activity.Current"/> (if available from Lambda or ASP.NET Core
    /// distributed tracing context) or generates a new unique correlation ID.
    /// Per AAP §0.8.5: Structured JSON logging with correlation-ID propagation
    /// from all Lambda functions.
    /// </summary>
    /// <returns>
    /// The correlation ID string — either from the current <see cref="Activity"/>
    /// or a newly generated GUID.
    /// </returns>
    private static string GetCorrelationId()
    {
        // Try to extract correlation ID from the current Activity (set by Lambda runtime
        // or ASP.NET Core middleware for distributed tracing propagation)
        var activityId = Activity.Current?.Id;
        if (!string.IsNullOrEmpty(activityId))
        {
            return activityId;
        }

        // Fall back to generating a new correlation ID for standalone invocations
        // or when no distributed tracing context is available
        return Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Standard domain event envelope following the JSON Schema pattern from
    /// <c>libs/shared-schemas/src/events/</c>. Provides a consistent structure
    /// for all domain events published to SNS topics.
    /// 
    /// <para>
    /// Each event carries a unique <see cref="EventId"/> (UUID) enabling consumers
    /// to implement deduplication and idempotency as required by the at-least-once
    /// delivery guarantee via SQS (per AAP §0.8.5).
    /// </para>
    /// </summary>
    /// <typeparam name="T">
    /// The type of the event payload — either <see cref="Invoice"/> or <see cref="Payment"/>.
    /// </typeparam>
    private sealed class DomainEvent<T>
    {
        /// <summary>
        /// Event type identifier following <c>{domain}.{entity}.{action}</c> convention.
        /// Examples: "invoicing.invoice.created", "invoicing.payment.processed".
        /// Used for SQS subscription filter policies to route events to specific consumers.
        /// </summary>
        public string EventType { get; init; } = string.Empty;

        /// <summary>
        /// Unique event identifier (UUID) for consumer deduplication and idempotency.
        /// Per AAP §0.8.5: At-least-once delivery via SQS means events may be delivered
        /// multiple times — consumers use this ID to detect and skip duplicates.
        /// </summary>
        public string EventId { get; init; } = string.Empty;

        /// <summary>
        /// UTC timestamp of when the event was generated by the publishing service.
        /// </summary>
        public DateTime Timestamp { get; init; }

        /// <summary>
        /// Correlation ID for distributed tracing across service boundaries.
        /// Per AAP §0.8.5: All events and log entries include correlation-ID
        /// for end-to-end request tracking.
        /// </summary>
        public string CorrelationId { get; init; } = string.Empty;

        /// <summary>
        /// Identifies the originating service (e.g., "invoicing-service").
        /// Enables consumers to identify the source of cross-domain events.
        /// </summary>
        public string Source { get; init; } = string.Empty;

        /// <summary>
        /// Event schema version for backward-compatible evolution.
        /// Consumers should be prepared to handle different versions
        /// and ignore unknown fields.
        /// </summary>
        public int Version { get; init; }

        /// <summary>
        /// The typed event payload containing the domain object (<see cref="Invoice"/>
        /// or <see cref="Payment"/>). Serialized to JSON as part of the SNS message body.
        /// </summary>
        public T? Payload { get; init; }
    }
}
