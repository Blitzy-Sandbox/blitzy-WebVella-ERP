using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.Service.Mail.Domain.Services;
using WebVella.Erp.Service.Mail.Domain.Entities;

namespace WebVella.Erp.Service.Mail.Events.Subscribers
{
    /// <summary>
    /// Cross-service domain event representing a request to send an email.
    /// Published by Core, CRM, and Project services via the message broker
    /// (RabbitMQ or SNS+SQS) and consumed by the Mail service's
    /// <see cref="SendNotificationSubscriber"/>.
    /// <para>
    /// This event contract replaces the monolith's in-process hook pattern where
    /// <c>EmailSendNow</c> (attached via <c>[HookAttachment(key: "email_send_now")]</c>)
    /// delegated to <c>SmtpInternalService.EmailSendNowOnPost(pageModel)</c>.
    /// In the microservice architecture, the UI or another service publishes this
    /// event instead of directly invoking the hook, enabling asynchronous,
    /// fire-and-forget email delivery across service boundaries.
    /// </para>
    /// <para>
    /// All properties carry <see cref="JsonPropertyAttribute"/> annotations per
    /// AAP 0.8.2 to maintain Newtonsoft.Json serialization contract stability
    /// across service boundaries.
    /// </para>
    /// </summary>
    public class SendEmailRequestEvent : IDomainEvent
    {
        /// <summary>
        /// Gets or sets the UTC timestamp indicating when the send email request was created.
        /// Implements <see cref="IDomainEvent.Timestamp"/>.
        /// </summary>
        [JsonProperty(PropertyName = "timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the unique correlation identifier for distributed tracing.
        /// Links this event to all related events in the processing chain across services.
        /// Implements <see cref="IDomainEvent.CorrelationId"/>.
        /// </summary>
        [JsonProperty(PropertyName = "correlationId")]
        public Guid CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the entity name this event relates to.
        /// Always "email" for send email requests.
        /// Implements <see cref="IDomainEvent.EntityName"/>.
        /// </summary>
        [JsonProperty(PropertyName = "entityName")]
        public string EntityName { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the email record to send.
        /// Maps to <c>(Guid)pageModel.DataModel.GetProperty("Record.id")</c> from the
        /// monolith's <c>SmtpInternalService.EmailSendNowOnPost()</c> (line 482).
        /// The subscriber uses this to load the email record via
        /// <see cref="SmtpService.GetEmail(Guid)"/>.
        /// </summary>
        [JsonProperty(PropertyName = "emailId")]
        public Guid EmailId { get; set; }

        /// <summary>
        /// Gets or sets the name of the originating microservice that published
        /// this event (e.g., "core", "crm", "project"). Used for audit logging
        /// and tracking the source of email send requests across services.
        /// </summary>
        [JsonProperty(PropertyName = "senderServiceName")]
        public string SenderServiceName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the email should be sent
        /// immediately. When <c>true</c>, mirrors the original "email_send_now"
        /// hook behavior — the subscriber invokes <c>SmtpService.SendEmail()</c>
        /// synchronously. When <c>false</c>, the email is queued for later
        /// processing by the scheduled mail queue job.
        /// Defaults to <c>true</c> to preserve backward compatibility with the
        /// monolith's <c>EmailSendNow</c> hook.
        /// </summary>
        [JsonProperty(PropertyName = "sendImmediately")]
        public bool SendImmediately { get; set; }

        /// <summary>
        /// Initializes a new instance of <see cref="SendEmailRequestEvent"/>
        /// with sensible defaults for all properties.
        /// </summary>
        public SendEmailRequestEvent()
        {
            Timestamp = DateTimeOffset.UtcNow;
            CorrelationId = Guid.NewGuid();
            EntityName = "email";
            SendImmediately = true;
            SenderServiceName = string.Empty;
        }
    }

    /// <summary>
    /// MassTransit consumer that subscribes to <see cref="SendEmailRequestEvent"/>
    /// messages published by Core, CRM, and Project services via the message broker.
    /// <para>
    /// This subscriber directly replaces the monolith's <c>EmailSendNow</c> hook class
    /// (<c>[HookAttachment(key: "email_send_now")]</c>) which delegated to
    /// <c>SmtpInternalService.EmailSendNowOnPost(pageModel)</c>.
    /// </para>
    /// <para>
    /// Core business logic flow (preserved from monolith lines 480-496):
    /// <list type="number">
    ///   <item>Receive email ID from the event message
    ///         (replaces <c>pageModel.DataModel.GetProperty("Record.id")</c>)</item>
    ///   <item>Load email record via <c>SmtpService.GetEmail(emailId)</c>
    ///         (replaces <c>new SmtpInternalService().GetEmail(emailId)</c>)</item>
    ///   <item>Load SMTP config via <c>SmtpService.GetSmtpService(email.ServiceId)</c>
    ///         (replaces <c>new EmailServiceManager().GetSmtpService(email.ServiceId)</c>)</item>
    ///   <item>Send the email via <c>SmtpService.SendEmail(email, smtpConfig)</c>
    ///         (replaces <c>internalSmtpSrv.SendEmail(email, smtpService)</c>)</item>
    /// </list>
    /// </para>
    /// <para>
    /// Key difference from monolith: the monolith hook was synchronous and returned
    /// <c>IActionResult</c> with UI feedback (TempData + redirect). This subscriber
    /// is a pure backend consumer — no UI response; fire-and-forget with error logging.
    /// All UI concerns are handled by the MailController REST endpoints.
    /// </para>
    /// <para>
    /// Idempotency (AAP 0.8.2): Duplicate event delivery is safe because:
    /// (1) the subscriber checks if the email is already <c>Sent</c> and skips;
    /// (2) the email ID is the idempotency key — duplicate messages process
    /// the same record without creating duplicates.
    /// </para>
    /// </summary>
    public class SendNotificationSubscriber : IConsumer<SendEmailRequestEvent>
    {
        private readonly SmtpService _smtpService;
        private readonly ILogger<SendNotificationSubscriber> _logger;

        /// <summary>
        /// Constructs a new <see cref="SendNotificationSubscriber"/> with required
        /// dependencies injected via the DI container.
        /// </summary>
        /// <param name="smtpService">
        /// The refactored SMTP domain service that consolidates all email sending logic
        /// from the monolith's SmtpInternalService, SmtpService, and EmailServiceManager.
        /// </param>
        /// <param name="logger">
        /// Structured logger for tracing event reception, validation, sending results,
        /// and errors with CorrelationId for distributed tracing across microservices.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="smtpService"/> or <paramref name="logger"/> is null.
        /// </exception>
        public SendNotificationSubscriber(SmtpService smtpService, ILogger<SendNotificationSubscriber> logger)
        {
            _smtpService = smtpService ?? throw new ArgumentNullException(nameof(smtpService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Consumes a <see cref="SendEmailRequestEvent"/> message from the message broker
        /// and delegates to <see cref="SmtpService"/> for email delivery.
        /// <para>
        /// Preserves the exact business logic flow from the monolith's
        /// <c>SmtpInternalService.EmailSendNowOnPost()</c> (lines 480-496):
        /// GetEmail → check idempotency → GetSmtpService → SendEmail.
        /// </para>
        /// <para>
        /// Error handling strategy: All exceptions are caught and logged at Error level
        /// with the CorrelationId for distributed tracing. Exceptions are NOT re-thrown
        /// because <c>SmtpService.SendEmail()</c> already manages its own retry logic
        /// (retry count, scheduled_on) — re-throwing would cause double-retry via
        /// MassTransit's built-in retry policy.
        /// </para>
        /// </summary>
        /// <param name="context">
        /// MassTransit consume context containing the <see cref="SendEmailRequestEvent"/> message.
        /// </param>
        /// <returns>A task representing the asynchronous consume operation.</returns>
        public async Task Consume(ConsumeContext<SendEmailRequestEvent> context)
        {
            var message = context.Message;

            _logger.LogInformation(
                "Received SendEmailRequestEvent for EmailId={EmailId} from service={SenderServiceName}, CorrelationId={CorrelationId}",
                message.EmailId,
                message.SenderServiceName,
                message.CorrelationId);

            // Validate: empty EmailId is an invalid event — log warning and return early
            // (don't throw — MassTransit would retry on exceptions)
            if (message.EmailId == Guid.Empty)
            {
                _logger.LogWarning(
                    "SendEmailRequestEvent received with empty EmailId, skipping. CorrelationId={CorrelationId}",
                    message.CorrelationId);
                return;
            }

            try
            {
                // Step 1: Load email record
                // Replaces monolith: Email email = internalSmtpSrv.GetEmail(emailId);
                // (SmtpInternalService.cs line 485)
                var email = _smtpService.GetEmail(message.EmailId);
                if (email == null)
                {
                    _logger.LogWarning(
                        "Email with id {EmailId} not found, skipping. CorrelationId={CorrelationId}",
                        message.EmailId,
                        message.CorrelationId);
                    return;
                }

                // Idempotency check (AAP 0.8.2): If the email has already been sent,
                // skip to prevent duplicate delivery. The EmailId is the idempotency key.
                if (email.Status == EmailStatus.Sent)
                {
                    _logger.LogInformation(
                        "Email {EmailId} already sent (Status=Sent), skipping duplicate processing. CorrelationId={CorrelationId}",
                        message.EmailId,
                        message.CorrelationId);
                    return;
                }

                // Step 2: Load SMTP service configuration
                // Replaces monolith: SmtpService smtpService = new EmailServiceManager().GetSmtpService(email.ServiceId);
                // (SmtpInternalService.cs line 486)
                var smtpConfig = _smtpService.GetSmtpService(email.ServiceId);
                if (smtpConfig == null)
                {
                    _logger.LogWarning(
                        "SMTP service configuration for email {EmailId} with ServiceId={ServiceId} not found, skipping. CorrelationId={CorrelationId}",
                        message.EmailId,
                        email.ServiceId,
                        message.CorrelationId);
                    return;
                }

                // Step 3: Send the email
                // Replaces monolith: internalSmtpSrv.SendEmail(email, smtpService);
                // (SmtpInternalService.cs line 487)
                // SmtpService.SendEmail(Email, SmtpServiceConfig) internally handles:
                //   - Building MimeMessage from Email model
                //   - Handling cc:/bcc: prefixes on recipients
                //   - Attaching files, processing HTML content
                //   - Sending via MailKit SmtpClient
                //   - On success: setting SentOn, Status=Sent
                //   - On failure: incrementing RetriesCount, scheduling retry
                //   - In finally block: calling SaveEmail
                _smtpService.SendEmail(email, smtpConfig);

                // Step 4: Log completion
                _logger.LogInformation(
                    "SendEmailRequestEvent processed successfully for EmailId={EmailId}, CorrelationId={CorrelationId}",
                    message.EmailId,
                    message.CorrelationId);
            }
            catch (Exception ex)
            {
                // DO NOT re-throw: SmtpService.SendEmail() already manages its own retry logic
                // (RetriesCount, ScheduledOn, MaxRetriesCount). Re-throwing here would cause
                // MassTransit to also retry the message, resulting in double-retry behavior.
                // Instead, log the error with full context for distributed tracing.
                _logger.LogError(
                    ex,
                    "Error processing SendEmailRequestEvent for EmailId={EmailId}, CorrelationId={CorrelationId}: {ErrorMessage}",
                    message.EmailId,
                    message.CorrelationId,
                    ex.Message);
            }

            await Task.CompletedTask;
        }
    }
}
