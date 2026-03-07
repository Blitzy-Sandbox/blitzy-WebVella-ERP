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
    /// Domain event published when an email is successfully sent via SMTP.
    /// <para>
    /// Implements <see cref="IDomainEvent"/> to participate in the MassTransit
    /// event-driven messaging system that replaces the monolith's synchronous
    /// hook-based communication.
    /// </para>
    /// <para>
    /// In the monolith (<c>SmtpInternalService.SendEmail()</c>, lines 801-804),
    /// the successful delivery was an implicit side effect — <c>email.SentOn</c>
    /// was set to <c>DateTime.UtcNow</c> and <c>email.Status</c> to
    /// <c>EmailStatus.Sent</c>. In the microservice architecture, this event is
    /// explicitly published so other services (CRM, Project) can subscribe and
    /// react to email delivery confirmations asynchronously.
    /// </para>
    /// <para>
    /// This event is ONLY published on successful SMTP delivery. It is NOT
    /// published on errors, retries, or aborts (see <c>SmtpInternalService</c>
    /// error handling at line 806 in the monolith source).
    /// </para>
    /// <para>
    /// All properties are annotated with <c>[JsonProperty]</c> per AAP 0.8.2 to
    /// maintain Newtonsoft.Json serialization stability across MassTransit message
    /// transport (RabbitMQ for local/Docker, SNS+SQS for LocalStack validation).
    /// </para>
    /// </summary>
    public class EmailSentEvent : IDomainEvent
    {
        #region IDomainEvent Properties

        /// <summary>
        /// Gets or sets the UTC timestamp indicating when this event was created.
        /// <para>
        /// Initialized to <c>DateTime.UtcNow</c> in the parameterless constructor.
        /// Used for event ordering, idempotency checks, and audit trail
        /// reconstruction across distributed services.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for tracing this event
        /// through the distributed service chain.
        /// <para>
        /// Initialized to <c>Guid.NewGuid()</c> in the parameterless constructor.
        /// Links all related events from a single email delivery operation, enabling
        /// distributed tracing and debugging across microservice boundaries.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// <para>
        /// Always set to <c>"email"</c> for <see cref="EmailSentEvent"/>,
        /// corresponding to the <c>email</c> entity in the ERP data model.
        /// Subscribers use this property to filter events by entity type.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        #endregion

        #region Mail-Specific Properties

        /// <summary>
        /// Gets or sets the unique identifier of the sent email record.
        /// <para>
        /// Corresponds to <c>Email.Id</c> from the monolith's
        /// <c>WebVella.Erp.Plugins.Mail.Api.Email</c> DTO. Consumers can use this
        /// for idempotent event processing and deduplication.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "emailId")]
        public Guid EmailId { get; set; }

        /// <summary>
        /// Gets or sets the primary recipient email address.
        /// <para>
        /// Extracted from the first entry in the <c>Email.Recipients</c> list
        /// (the monolith's <c>List&lt;EmailAddress&gt;</c>). For multi-recipient
        /// emails, this represents the primary addressee; the full list is available
        /// in the email record itself.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "recipientEmail")]
        public string RecipientEmail { get; set; }

        /// <summary>
        /// Gets or sets the email subject line.
        /// <para>
        /// Preserved from the monolith's <c>Email.Subject</c> property.
        /// May be empty for automated system notifications.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "subject")]
        public string Subject { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp of successful SMTP delivery.
        /// <para>
        /// Corresponds to the monolith's <c>email.SentOn = DateTime.UtcNow</c>
        /// assignment at line 801 of <c>SmtpInternalService.SendEmail()</c>,
        /// which is only reached after a successful <c>client.Send(message)</c>
        /// call via MailKit.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "sentOn")]
        public DateTime SentOn { get; set; }

        /// <summary>
        /// Gets or sets the identifier of the SMTP service used for delivery.
        /// <para>
        /// Corresponds to <c>Email.ServiceId</c> from the monolith — the GUID
        /// of the <c>smtp_service</c> record that was used to send this email.
        /// Useful for diagnostic tracking when multiple SMTP services are configured.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "serviceId")]
        public Guid ServiceId { get; set; }

        /// <summary>
        /// Gets or sets the sender email address.
        /// <para>
        /// Corresponds to <c>Email.Sender.Address</c> from the monolith's
        /// <c>EmailAddress</c> type. Represents the From address used in the
        /// SMTP envelope and MIME message.
        /// </para>
        /// </summary>
        [JsonProperty(PropertyName = "senderEmail")]
        public string SenderEmail { get; set; }

        #endregion

        /// <summary>
        /// Initializes a new instance of <see cref="EmailSentEvent"/> with default values.
        /// <para>
        /// Sets <see cref="Timestamp"/> to <c>DateTime.UtcNow</c>,
        /// <see cref="CorrelationId"/> to <c>Guid.NewGuid()</c>, and
        /// <see cref="EntityName"/> to <c>"email"</c>. All other properties
        /// default to their type defaults and should be set by the publisher.
        /// </para>
        /// </summary>
        public EmailSentEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            EntityName = "email";
            RecipientEmail = string.Empty;
            Subject = string.Empty;
            SenderEmail = string.Empty;
        }
    }

    /// <summary>
    /// Publishes <see cref="EmailSentEvent"/> domain events when an email is
    /// successfully sent via SMTP, replacing the monolith's implicit delivery
    /// notification in <c>SmtpInternalService.SendEmail()</c>.
    /// <para>
    /// In the monolith, after successful SMTP delivery (lines 801-804 of
    /// <c>SmtpInternalService.cs</c>), the email record was updated in-process
    /// with <c>SentOn</c>, <c>Status = Sent</c>, etc. There was no explicit
    /// hook or event — delivery was an implicit side effect. In the microservice
    /// architecture, this publisher makes the successful delivery an explicit
    /// domain event via MassTransit's <see cref="IPublishEndpoint"/>, enabling
    /// other services (CRM, Project) to subscribe and react asynchronously.
    /// </para>
    /// <para>
    /// This publisher follows the fire-and-forget resilience pattern: if event
    /// publishing fails (e.g., message broker is temporarily unavailable), the
    /// failure is logged at Warning level but does NOT propagate. This matches
    /// the monolith's behavior where hook execution failures did not abort the
    /// primary email send operation.
    /// </para>
    /// <para>
    /// Registered as a scoped or transient service in the Mail service's DI
    /// container. Not static — fully injectable for testability.
    /// </para>
    /// </summary>
    public class EmailSentEventPublisher
    {
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly ILogger<EmailSentEventPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="EmailSentEventPublisher"/>.
        /// </summary>
        /// <param name="publishEndpoint">
        /// MassTransit publish endpoint for sending domain events to the message
        /// broker (RabbitMQ for local/Docker, SNS+SQS for LocalStack validation).
        /// </param>
        /// <param name="logger">
        /// Logger for structured event publishing diagnostics. Debug-level messages
        /// include email ID, recipient, sent timestamp, and correlation ID.
        /// Warning-level messages log publish failures with exception details.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="publishEndpoint"/> or <paramref name="logger"/> is null.
        /// </exception>
        public EmailSentEventPublisher(
            IPublishEndpoint publishEndpoint,
            ILogger<EmailSentEventPublisher> logger)
        {
            _publishEndpoint = publishEndpoint ?? throw new ArgumentNullException(nameof(publishEndpoint));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes an <see cref="EmailSentEvent"/> indicating that an email was
        /// successfully delivered via SMTP.
        /// <para>
        /// This method replaces the implicit successful delivery notification from
        /// the monolith's <c>SmtpInternalService.SendEmail()</c> lines 801-804,
        /// where <c>email.SentOn = DateTime.UtcNow</c> and
        /// <c>email.Status = EmailStatus.Sent</c> were set after a successful
        /// <c>client.Send(message)</c> call.
        /// </para>
        /// <para>
        /// The event is published to the MassTransit message broker with
        /// fire-and-forget resilience: publishing failures are caught and logged
        /// at Warning level but do not propagate, ensuring the email send
        /// operation is not disrupted by messaging infrastructure issues.
        /// </para>
        /// </summary>
        /// <param name="emailId">
        /// The unique identifier of the sent email record. Must not be <c>Guid.Empty</c>.
        /// Corresponds to <c>Email.Id</c> in the monolith's email DTO.
        /// </param>
        /// <param name="recipientEmail">
        /// The primary recipient email address. Must not be null or whitespace.
        /// Extracted from the email's <c>Recipients</c> list.
        /// </param>
        /// <param name="senderEmail">
        /// The sender email address. May be null (defaults to <c>string.Empty</c>
        /// in the event). Corresponds to <c>Email.Sender.Address</c>.
        /// </param>
        /// <param name="subject">
        /// The email subject line. May be null (defaults to <c>string.Empty</c>
        /// in the event). Corresponds to <c>Email.Subject</c>.
        /// </param>
        /// <param name="sentOn">
        /// The UTC timestamp of successful SMTP delivery. Corresponds to the
        /// <c>email.SentOn = DateTime.UtcNow</c> assignment at line 801 of the
        /// monolith's <c>SmtpInternalService.SendEmail()</c>.
        /// </param>
        /// <param name="serviceId">
        /// The identifier of the SMTP service used for delivery. Corresponds to
        /// <c>Email.ServiceId</c> in the monolith — the GUID of the
        /// <c>smtp_service</c> record that processed this email.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancellation token for cooperative cancellation of the publish
        /// operation, supporting graceful shutdown of the Mail service.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="emailId"/> equals <c>Guid.Empty</c> or
        /// <paramref name="recipientEmail"/> is null or whitespace.
        /// </exception>
        public async Task PublishEmailSentAsync(
            Guid emailId,
            string recipientEmail,
            string senderEmail,
            string subject,
            DateTime sentOn,
            Guid serviceId,
            CancellationToken cancellationToken = default)
        {
            if (emailId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Email ID must not be empty. A valid email record ID is required for event publishing and consumer idempotency.",
                    nameof(emailId));
            }

            if (string.IsNullOrWhiteSpace(recipientEmail))
            {
                throw new ArgumentException(
                    "Recipient email must not be null or whitespace. A valid recipient address is required for the EmailSentEvent.",
                    nameof(recipientEmail));
            }

            var emailSentEvent = new EmailSentEvent
            {
                EmailId = emailId,
                RecipientEmail = recipientEmail,
                SenderEmail = senderEmail ?? string.Empty,
                Subject = subject ?? string.Empty,
                SentOn = sentOn,
                ServiceId = serviceId,
                CorrelationId = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow
            };

            try
            {
                await _publishEndpoint.Publish(emailSentEvent, cancellationToken);

                _logger.LogDebug(
                    "Published EmailSentEvent for email {EmailId} to {RecipientEmail}, " +
                    "sent on {SentOn:O}, service {ServiceId}, correlation {CorrelationId}",
                    emailSentEvent.EmailId,
                    emailSentEvent.RecipientEmail,
                    emailSentEvent.SentOn,
                    emailSentEvent.ServiceId,
                    emailSentEvent.CorrelationId);
            }
            catch (Exception ex)
            {
                // Fire-and-forget resilience: log the failure but do not propagate.
                // This matches the monolith's behavior where hook execution failures
                // did not abort the primary email send operation. The email was already
                // sent successfully via SMTP; the event is for downstream notifications
                // only (CRM, Project service subscribers).
                _logger.LogWarning(
                    ex,
                    "Failed to publish EmailSentEvent for email {EmailId} to {RecipientEmail}. " +
                    "Event publishing is fire-and-forget; the email was still sent successfully via SMTP. " +
                    "Correlation {CorrelationId}",
                    emailSentEvent.EmailId,
                    emailSentEvent.RecipientEmail,
                    emailSentEvent.CorrelationId);
            }
        }
    }
}
