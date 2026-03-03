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
    /// Domain event published when an email is added to the mail processing queue.
    /// <para>
    /// In the monolith, email queueing was an implicit in-process side effect of
    /// <c>SmtpInternalService.SaveEmail()</c> creating a new email record with
    /// <c>Status = EmailStatus.Pending</c>. In the microservice architecture, this
    /// becomes an explicit domain event so other services (e.g., CRM, Project) can
    /// react to emails being queued asynchronously via MassTransit.
    /// </para>
    /// <para>
    /// All properties carry <c>[JsonProperty]</c> annotations per AAP 0.8.2 rule:
    /// "Maintain Newtonsoft.Json [JsonProperty] annotations for API contract stability."
    /// The event carries sufficient data (EmailId) for consumers to deduplicate,
    /// satisfying the AAP 0.8.2 idempotency requirement.
    /// </para>
    /// </summary>
    public class EmailQueuedEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the email was queued.
        /// Initialized to <see cref="DateTime.UtcNow"/> in the default constructor.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for distributed tracing.
        /// Initialized to <see cref="Guid.NewGuid()"/> in the default constructor,
        /// enabling tracing of event chains across microservice boundaries.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// Always set to <c>"email"</c> for mail queue events, matching the
        /// monolith's <c>email</c> entity name used by <c>RecordManager</c>.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the queued email record.
        /// Corresponds to <c>Email.Id</c> from the monolith's
        /// <c>WebVella.Erp.Plugins.Mail.Api.Email</c> DTO.
        /// </summary>
        [JsonProperty(PropertyName = "emailId")]
        public Guid EmailId { get; set; }

        /// <summary>
        /// Gets or sets the primary recipient email address.
        /// Derived from the first entry in the monolith's <c>Email.Recipients</c> list.
        /// </summary>
        [JsonProperty(PropertyName = "recipientEmail")]
        public string RecipientEmail { get; set; }

        /// <summary>
        /// Gets or sets the email subject line.
        /// Corresponds to <c>Email.Subject</c> from the monolith DTO.
        /// </summary>
        [JsonProperty(PropertyName = "subject")]
        public string Subject { get; set; }

        /// <summary>
        /// Gets or sets the queue priority as an integer value.
        /// Maps from the monolith's <c>EmailPriority</c> enum: Low=0, Normal=1, High=2.
        /// Higher values indicate higher processing priority in
        /// <c>SmtpInternalService.ProcessSmtpQueue()</c> (ORDER BY priority DESC).
        /// </summary>
        [JsonProperty(PropertyName = "priority")]
        public int Priority { get; set; }

        /// <summary>
        /// Gets or sets the scheduled processing time for the queued email.
        /// Null indicates the email should be processed as soon as possible.
        /// Corresponds to <c>Email.ScheduledOn</c> from the monolith DTO.
        /// </summary>
        [JsonProperty(PropertyName = "scheduledOn")]
        public DateTime? ScheduledOn { get; set; }

        /// <summary>
        /// Gets or sets the SMTP service identifier assigned to process this email.
        /// Corresponds to <c>Email.ServiceId</c> from the monolith DTO, used by
        /// <c>EmailServiceManager.GetSmtpService(Guid id)</c> to resolve SMTP settings.
        /// </summary>
        [JsonProperty(PropertyName = "serviceId")]
        public Guid ServiceId { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="EmailQueuedEvent"/> with
        /// default values for cross-cutting metadata: UTC timestamp, unique
        /// correlation ID, and entity name set to "email".
        /// </summary>
        public EmailQueuedEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            EntityName = "email";
            RecipientEmail = string.Empty;
            Subject = string.Empty;
        }
    }

    /// <summary>
    /// Publishes <see cref="EmailQueuedEvent"/> domain events via MassTransit when
    /// an email is added to the mail processing queue.
    /// <para>
    /// This publisher replaces the implicit in-process side effect of the monolith's
    /// <c>SmtpInternalService.SaveEmail()</c> method. In the monolith, when
    /// <c>SmtpService.QueueEmail()</c> is called, the email is saved with
    /// <c>Status = Pending</c> and <c>ScheduledOn</c> set — there was NO explicit
    /// hook for this. In the microservice architecture, this becomes an explicit
    /// domain event so other services can react asynchronously.
    /// </para>
    /// <para>
    /// Designed as a DI-injectable instance-based service (no static methods), in
    /// contrast to the monolith's <c>EmailServiceManager</c> which used static
    /// cache methods. Uses fire-and-forget resilience: publishing failures are
    /// logged but never propagated, matching the monolith's implicit notification
    /// pattern where email queueing succeeded independently of downstream reactions.
    /// </para>
    /// </summary>
    public class EmailQueuedEventPublisher
    {
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly ILogger<EmailQueuedEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="EmailQueuedEventPublisher"/>
        /// with MassTransit publish endpoint and structured logger.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for sending domain events to the message
        /// broker (RabbitMQ for local/Docker, SNS+SQS for AWS/LocalStack).
        /// </param>
        /// <param name="logger">
        /// Structured logger for Debug-level publish confirmations and
        /// Warning-level error logging when event publishing fails.
        /// </param>
        public EmailQueuedEventPublisher(
            IPublishEndpoint publishEndpoint,
            ILogger<EmailQueuedEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes an <see cref="EmailQueuedEvent"/> when an email is added to the
        /// mail processing queue.
        /// <para>
        /// Replaces the implicit in-process side effect of
        /// <c>SmtpInternalService.SaveEmail()</c> creating a new email record with
        /// <c>Status = Pending</c>. The event carries sufficient data (EmailId) for
        /// consumers to deduplicate per the AAP 0.8.2 idempotency requirement.
        /// </para>
        /// <para>
        /// Uses fire-and-forget resilience: publishing failures are caught, logged at
        /// Warning level, but never propagated to the caller — ensuring email queueing
        /// succeeds even when the message broker is temporarily unavailable.
        /// </para>
        /// </summary>
        /// <param name="emailId">
        /// The unique identifier of the queued email record. Must not be <see cref="Guid.Empty"/>.
        /// </param>
        /// <param name="recipientEmail">
        /// The primary recipient email address. Must not be null or whitespace.
        /// </param>
        /// <param name="subject">
        /// The email subject line. Null values are normalized to <see cref="string.Empty"/>.
        /// </param>
        /// <param name="priority">
        /// Queue priority from EmailPriority enum: Low=0, Normal=1, High=2.
        /// </param>
        /// <param name="scheduledOn">
        /// When the email is scheduled to be processed, or null for immediate processing.
        /// </param>
        /// <param name="serviceId">
        /// The SMTP service ID assigned to process this email.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for graceful shutdown support in the microservice architecture.
        /// </param>
        /// <returns>A task representing the asynchronous publish operation.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="emailId"/> is <see cref="Guid.Empty"/> or
        /// <paramref name="recipientEmail"/> is null or whitespace.
        /// </exception>
        public async Task PublishEmailQueuedAsync(
            Guid emailId,
            string recipientEmail,
            string subject,
            int priority,
            DateTime? scheduledOn,
            Guid serviceId,
            CancellationToken cancellationToken = default)
        {
            if (emailId == Guid.Empty)
            {
                throw new ArgumentException("Email ID must not be empty.", nameof(emailId));
            }

            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                throw new ArgumentException("Recipient email must not be null or whitespace.", nameof(recipientEmail));
            }

            var emailQueuedEvent = new EmailQueuedEvent
            {
                EmailId = emailId,
                RecipientEmail = recipientEmail,
                Subject = subject ?? string.Empty,
                Priority = priority,
                ScheduledOn = scheduledOn,
                ServiceId = serviceId,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            try
            {
                await _publishEndpoint.Publish(emailQueuedEvent, cancellationToken);

                _logger.LogDebug(
                    "Published EmailQueuedEvent: EmailId={EmailId}, Recipient={Recipient}, CorrelationId={CorrelationId}",
                    emailQueuedEvent.EmailId,
                    emailQueuedEvent.RecipientEmail,
                    emailQueuedEvent.CorrelationId);
            }
            catch (Exception ex)
            {
                // Fire-and-forget resilience: log the failure but do not propagate.
                // This matches the monolith's implicit notification pattern where
                // email queueing succeeded independently of downstream reactions.
                // The email record is already persisted; event delivery will be
                // retried by MassTransit's built-in retry/redelivery mechanisms.
                _logger.LogWarning(
                    ex,
                    "Failed to publish EmailQueuedEvent: EmailId={EmailId}, Recipient={Recipient}, CorrelationId={CorrelationId}",
                    emailQueuedEvent.EmailId,
                    emailQueuedEvent.RecipientEmail,
                    emailQueuedEvent.CorrelationId);
            }
        }
    }
}
