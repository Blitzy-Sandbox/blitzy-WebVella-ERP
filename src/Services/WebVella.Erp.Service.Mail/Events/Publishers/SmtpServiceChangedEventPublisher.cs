using System;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Contracts.Events;

namespace WebVella.Erp.Service.Mail.Events.Publishers
{
    /// <summary>
    /// Domain event published when an SMTP service configuration record is created, updated, or deleted.
    /// <para>
    /// This event replaces the monolith's synchronous <c>EmailServiceManager.ClearCache()</c> calls
    /// that were triggered by <c>SmtpServiceRecordHook</c> (attached to the "smtp_service" entity).
    /// In the microservice architecture, publishing this event enables:
    /// <list type="bullet">
    ///   <item>The Mail service to invalidate its own Redis-backed SMTP configuration cache.</item>
    ///   <item>Other services that cache SMTP configuration to invalidate their local caches.</item>
    ///   <item>Distributed tracing and auditing of SMTP configuration changes.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Implements <see cref="IDomainEvent"/> to participate in the MassTransit event-driven
    /// messaging system, carrying <see cref="IDomainEvent.Timestamp"/>,
    /// <see cref="IDomainEvent.CorrelationId"/>, and <see cref="IDomainEvent.EntityName"/>
    /// properties required for distributed tracing and event routing across microservices.
    /// </para>
    /// </summary>
    public class SmtpServiceChangedEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when this SMTP service change event occurred.
        /// Initialized to <see cref="DateTime.UtcNow"/> in the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event across services.
        /// Initialized to <see cref="Guid.NewGuid()"/> in the parameterless constructor.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// Always set to <c>"smtp_service"</c>, matching the monolith's
        /// <c>[HookAttachment("smtp_service")]</c> entity binding.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the SMTP service record that was changed.
        /// Corresponds to <c>record["id"]</c> from the monolith's hook parameter
        /// (see <c>SmtpServiceRecordHook.OnPreDeleteRecord</c> line 45).
        /// </summary>
        [JsonProperty(PropertyName = "smtpServiceId")]
        public Guid SmtpServiceId { get; set; }

        /// <summary>
        /// Gets or sets the type of change that triggered this event.
        /// One of <c>"Created"</c>, <c>"Updated"</c>, or <c>"Deleted"</c>,
        /// indicating which hook operation produced this event.
        /// Subscribers can use this value for idempotency checks and
        /// to determine the appropriate cache invalidation strategy.
        /// </summary>
        [JsonProperty(PropertyName = "changeType")]
        public string ChangeType { get; set; }

        /// <summary>
        /// Gets or sets the display name of the SMTP service that was changed.
        /// Useful for structured logging and diagnostics by event subscribers.
        /// </summary>
        [JsonProperty(PropertyName = "smtpServiceName")]
        public string SmtpServiceName { get; set; }

        /// <summary>
        /// Gets or sets whether the changed SMTP service is the default service.
        /// <para>
        /// This flag is critical for the deletion business rule: the monolith's
        /// <c>SmtpServiceRecordHook.OnPreDeleteRecord</c> (line 46) blocks deletion
        /// of default SMTP services. In the microservice architecture, that validation
        /// is enforced by the domain service before publishing; consequently, delete
        /// events always carry <c>IsDefault = false</c>.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "isDefault")]
        public bool IsDefault { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="SmtpServiceChangedEvent"/>
        /// with default cross-cutting metadata: current UTC timestamp, new correlation ID,
        /// and entity name set to <c>"smtp_service"</c>.
        /// </summary>
        public SmtpServiceChangedEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            EntityName = "smtp_service";
            ChangeType = string.Empty;
            SmtpServiceName = string.Empty;
        }
    }

    /// <summary>
    /// Publishes <see cref="SmtpServiceChangedEvent"/> domain events when SMTP service
    /// configuration records are created, updated, or deleted.
    /// <para>
    /// This publisher directly replaces the three post-operation hooks from the monolith's
    /// <c>SmtpServiceRecordHook</c> class:
    /// <list type="bullet">
    ///   <item><c>OnPostCreateRecord()</c> → <c>EmailServiceManager.ClearCache()</c>
    ///         → now <see cref="PublishSmtpServiceCreatedAsync"/></item>
    ///   <item><c>OnPostUpdateRecord()</c> → <c>EmailServiceManager.ClearCache()</c>
    ///         → now <see cref="PublishSmtpServiceUpdatedAsync"/></item>
    ///   <item><c>OnPreDeleteRecord()</c> → <c>EmailServiceManager.ClearCache()</c> (when allowed)
    ///         → now <see cref="PublishSmtpServiceDeletedAsync"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Design notes:</b>
    /// <list type="bullet">
    ///   <item>Pre-create/pre-update validation logic is NOT included — that validation
    ///         (<c>ValidatePreCreateRecord</c>, <c>HandleDefaultServiceSetup</c>) is enforced
    ///         synchronously by the Mail service's domain service (<c>SmtpService</c>) before
    ///         persistence and before this publisher is invoked.</item>
    ///   <item>The default-service deletion constraint (<c>service.IsDefault → block delete</c>)
    ///         is also enforced by the domain service; this publisher is only called when
    ///         deletion is permitted (i.e., the service is NOT the default).</item>
    ///   <item>This is an instance-based injectable service via DI, replacing the monolith's
    ///         static <c>EmailServiceManager.ClearCache()</c> pattern.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class SmtpServiceChangedEventPublisher
    {
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly ILogger<SmtpServiceChangedEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="SmtpServiceChangedEventPublisher"/>
        /// with the required MassTransit publish endpoint and structured logger.
        /// </summary>
        /// <param name="publishEndpoint">
        /// The MassTransit publish endpoint used to send domain events to the message broker
        /// (RabbitMQ for local/Docker, SNS+SQS for LocalStack validation).
        /// </param>
        /// <param name="logger">
        /// Logger for structured diagnostics including SMTP service ID, name, change type,
        /// and correlation ID for distributed tracing.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="publishEndpoint"/> or <paramref name="logger"/> is <c>null</c>.
        /// </exception>
        public SmtpServiceChangedEventPublisher(
            IPublishEndpoint publishEndpoint,
            ILogger<SmtpServiceChangedEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes a <see cref="SmtpServiceChangedEvent"/> with <c>ChangeType = "Created"</c>.
        /// <para>
        /// Replaces <c>SmtpServiceRecordHook.OnPostCreateRecord()</c> (source lines 33-36)
        /// which called <c>EmailServiceManager.ClearCache()</c> to dispose and recreate the
        /// process-local <c>IMemoryCache</c>. In the microservice architecture, this event
        /// enables distributed cache invalidation across all services.
        /// </para>
        /// </summary>
        /// <param name="smtpServiceId">The unique identifier of the newly created SMTP service record.</param>
        /// <param name="smtpServiceName">The display name of the SMTP service (may be <c>null</c>, coerced to empty string).</param>
        /// <param name="isDefault">Whether the newly created SMTP service is the default service.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation of the publish operation.</param>
        /// <returns>A task representing the asynchronous publish operation.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="smtpServiceId"/> is <see cref="Guid.Empty"/>.</exception>
        public async Task PublishSmtpServiceCreatedAsync(
            Guid smtpServiceId,
            string smtpServiceName,
            bool isDefault,
            CancellationToken cancellationToken = default)
        {
            if (smtpServiceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "SMTP service ID cannot be an empty GUID.",
                    nameof(smtpServiceId));
            }

            var smtpServiceChangedEvent = new SmtpServiceChangedEvent
            {
                SmtpServiceId = smtpServiceId,
                SmtpServiceName = smtpServiceName ?? string.Empty,
                IsDefault = isDefault,
                ChangeType = "Created",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            await _publishEndpoint.Publish(smtpServiceChangedEvent, cancellationToken);

            _logger.LogDebug(
                "Published SmtpServiceChangedEvent: SmtpServiceId={SmtpServiceId}, " +
                "SmtpServiceName={SmtpServiceName}, ChangeType={ChangeType}, " +
                "CorrelationId={CorrelationId}",
                smtpServiceChangedEvent.SmtpServiceId,
                smtpServiceChangedEvent.SmtpServiceName,
                smtpServiceChangedEvent.ChangeType,
                smtpServiceChangedEvent.CorrelationId);
        }

        /// <summary>
        /// Publishes a <see cref="SmtpServiceChangedEvent"/> with <c>ChangeType = "Updated"</c>.
        /// <para>
        /// Replaces <c>SmtpServiceRecordHook.OnPostUpdateRecord()</c> (source lines 38-41)
        /// which called <c>EmailServiceManager.ClearCache()</c> to dispose and recreate the
        /// process-local <c>IMemoryCache</c>. In the microservice architecture, this event
        /// enables distributed cache invalidation across all services.
        /// </para>
        /// </summary>
        /// <param name="smtpServiceId">The unique identifier of the updated SMTP service record.</param>
        /// <param name="smtpServiceName">The display name of the SMTP service (may be <c>null</c>, coerced to empty string).</param>
        /// <param name="isDefault">Whether the updated SMTP service is the default service.</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation of the publish operation.</param>
        /// <returns>A task representing the asynchronous publish operation.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="smtpServiceId"/> is <see cref="Guid.Empty"/>.</exception>
        public async Task PublishSmtpServiceUpdatedAsync(
            Guid smtpServiceId,
            string smtpServiceName,
            bool isDefault,
            CancellationToken cancellationToken = default)
        {
            if (smtpServiceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "SMTP service ID cannot be an empty GUID.",
                    nameof(smtpServiceId));
            }

            var smtpServiceChangedEvent = new SmtpServiceChangedEvent
            {
                SmtpServiceId = smtpServiceId,
                SmtpServiceName = smtpServiceName ?? string.Empty,
                IsDefault = isDefault,
                ChangeType = "Updated",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            await _publishEndpoint.Publish(smtpServiceChangedEvent, cancellationToken);

            _logger.LogDebug(
                "Published SmtpServiceChangedEvent: SmtpServiceId={SmtpServiceId}, " +
                "SmtpServiceName={SmtpServiceName}, ChangeType={ChangeType}, " +
                "CorrelationId={CorrelationId}",
                smtpServiceChangedEvent.SmtpServiceId,
                smtpServiceChangedEvent.SmtpServiceName,
                smtpServiceChangedEvent.ChangeType,
                smtpServiceChangedEvent.CorrelationId);
        }

        /// <summary>
        /// Publishes a <see cref="SmtpServiceChangedEvent"/> with <c>ChangeType = "Deleted"</c>
        /// and <c>IsDefault = false</c>.
        /// <para>
        /// Replaces the cache-clearing branch of <c>SmtpServiceRecordHook.OnPreDeleteRecord()</c>
        /// (source lines 48-49) which called <c>EmailServiceManager.ClearCache()</c> when deletion
        /// was allowed. The deletion business rule check (<c>service.IsDefault → block delete</c>
        /// from source line 46-47) is enforced by the domain service; this publisher is only
        /// invoked when the SMTP service is NOT the default and deletion is permitted.
        /// </para>
        /// </summary>
        /// <param name="smtpServiceId">The unique identifier of the deleted SMTP service record.</param>
        /// <param name="smtpServiceName">The display name of the SMTP service (may be <c>null</c>, coerced to empty string).</param>
        /// <param name="cancellationToken">Cancellation token for cooperative cancellation of the publish operation.</param>
        /// <returns>A task representing the asynchronous publish operation.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="smtpServiceId"/> is <see cref="Guid.Empty"/>.</exception>
        public async Task PublishSmtpServiceDeletedAsync(
            Guid smtpServiceId,
            string smtpServiceName,
            CancellationToken cancellationToken = default)
        {
            if (smtpServiceId == Guid.Empty)
            {
                throw new ArgumentException(
                    "SMTP service ID cannot be an empty GUID.",
                    nameof(smtpServiceId));
            }

            var smtpServiceChangedEvent = new SmtpServiceChangedEvent
            {
                SmtpServiceId = smtpServiceId,
                SmtpServiceName = smtpServiceName ?? string.Empty,
                IsDefault = false,
                ChangeType = "Deleted",
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            await _publishEndpoint.Publish(smtpServiceChangedEvent, cancellationToken);

            _logger.LogDebug(
                "Published SmtpServiceChangedEvent: SmtpServiceId={SmtpServiceId}, " +
                "SmtpServiceName={SmtpServiceName}, ChangeType={ChangeType}, " +
                "CorrelationId={CorrelationId}",
                smtpServiceChangedEvent.SmtpServiceId,
                smtpServiceChangedEvent.SmtpServiceName,
                smtpServiceChangedEvent.ChangeType,
                smtpServiceChangedEvent.CorrelationId);
        }
    }
}
