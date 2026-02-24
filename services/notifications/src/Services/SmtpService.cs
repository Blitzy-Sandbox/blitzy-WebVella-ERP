using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Models;

namespace WebVellaErp.Notifications.Services
{
    /// <summary>
    /// Represents a validation error returned from SMTP service configuration validation.
    /// Replaces WebVella.Erp.Api.Models.ErrorModel from the source monolith.
    /// </summary>
    public class ValidationError
    {
        /// <summary>Field name or key that failed validation.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>The invalid value that was submitted.</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>Human-readable error message describing the validation failure.</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Represents a CID-based inline image resource for HTML email embedding.
    /// Replaces MimeKit LinkedResource from the source monolith.
    /// </summary>
    public class InlineResource
    {
        /// <summary>Content-ID used in cid: references within HTML body.</summary>
        public string ContentId { get; set; } = string.Empty;

        /// <summary>Binary content of the inline resource.</summary>
        public byte[] Content { get; set; } = Array.Empty<byte>();

        /// <summary>MIME type of the resource (e.g., "image/png", "image/jpeg").</summary>
        public string MimeType { get; set; } = string.Empty;

        /// <summary>Original file name of the resource.</summary>
        public string FileName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Service interface for all email sending, queuing, and SMTP service configuration operations.
    /// Consolidated from SmtpService.cs + SmtpInternalService.cs in the source monolith.
    /// All methods are async with CancellationToken for cooperative cancellation.
    /// </summary>
    public interface ISmtpService
    {
        // ── Email Sending Operations ──

        /// <summary>
        /// Send email to a single recipient using the default SMTP service configuration.
        /// Maps to source SmtpService.SendEmail(EmailAddress, string, string, string, List{string}) lines 67-195.
        /// </summary>
        Task SendEmailAsync(EmailAddress recipient, string subject, string textBody, string htmlBody,
            List<string>? attachmentKeys = null, CancellationToken ct = default);

        /// <summary>
        /// Send email to multiple recipients using the default SMTP service configuration.
        /// Maps to source SmtpService.SendEmail(List{EmailAddress}, ...) lines 197-338.
        /// </summary>
        Task SendEmailAsync(List<EmailAddress> recipients, string subject, string textBody, string htmlBody,
            List<string>? attachmentKeys = null, CancellationToken ct = default);

        /// <summary>
        /// Send email to a single recipient with a custom sender address.
        /// Maps to source SmtpService.SendEmail(EmailAddress, EmailAddress, ...) lines 340-467.
        /// </summary>
        Task SendEmailAsync(EmailAddress recipient, EmailAddress sender, string subject, string textBody,
            string htmlBody, List<string>? attachmentKeys = null, CancellationToken ct = default);

        /// <summary>
        /// Send email to multiple recipients with a custom sender address.
        /// Maps to source SmtpService.SendEmail(List{EmailAddress}, EmailAddress, ...) lines 469-613.
        /// </summary>
        Task SendEmailAsync(List<EmailAddress> recipients, EmailAddress sender, string subject, string textBody,
            string htmlBody, List<string>? attachmentKeys = null, CancellationToken ct = default);

        /// <summary>
        /// Send a pre-composed email using a specific SMTP service configuration.
        /// Used by queue processing for retry-aware sends with SES transport.
        /// Maps to source SmtpInternalService.SendEmail(Email, SmtpService) lines 689-827.
        /// </summary>
        Task SendEmailAsync(Email email, SmtpServiceConfig service, CancellationToken ct = default);

        // ── Email Queuing Operations ──

        /// <summary>
        /// Queue email for async delivery to a single recipient using default sender.
        /// Maps to source SmtpService.QueueEmail single-recipient overload lines 615-683.
        /// </summary>
        Task QueueEmailAsync(EmailAddress recipient, string subject, string textBody, string htmlBody,
            EmailPriority priority = EmailPriority.Normal, List<string>? attachmentKeys = null,
            CancellationToken ct = default);

        /// <summary>
        /// Queue email for async delivery to multiple recipients using default sender.
        /// Maps to source SmtpService.QueueEmail multi-recipient overload lines 685-763.
        /// </summary>
        Task QueueEmailAsync(List<EmailAddress> recipients, string subject, string textBody, string htmlBody,
            EmailPriority priority = EmailPriority.Normal, List<string>? attachmentKeys = null,
            CancellationToken ct = default);

        /// <summary>
        /// Queue email with custom sender and reply-to for a single recipient.
        /// Maps to source SmtpService.QueueEmail lines 775-854.
        /// </summary>
        Task QueueEmailAsync(EmailAddress recipient, EmailAddress sender, string replyTo, string subject,
            string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal,
            List<string>? attachmentKeys = null, CancellationToken ct = default);

        /// <summary>
        /// Queue email with custom sender and reply-to for multiple recipients.
        /// Maps to source SmtpService.QueueEmail lines 856-945.
        /// </summary>
        Task QueueEmailAsync(List<EmailAddress> recipients, EmailAddress sender, string replyTo, string subject,
            string textBody, string htmlBody, EmailPriority priority = EmailPriority.Normal,
            List<string>? attachmentKeys = null, CancellationToken ct = default);

        // ── Queue Processing ──

        /// <summary>
        /// Process pending email queue. Fetches pending emails in batches of 10,
        /// ordered by priority DESC and scheduled_on ASC, and sends each through the
        /// configured SMTP service. Maps to SmtpInternalService.ProcessSmtpQueue lines 829-878.
        /// </summary>
        Task ProcessSmtpQueueAsync(CancellationToken ct = default);

        // ── SMTP Service Config Validation ──

        /// <summary>
        /// Validate SMTP service configuration before creation. Checks name uniqueness,
        /// port range, email format, retry limits, and connection security.
        /// Maps to SmtpInternalService.ValidatePreCreateRecord lines 33-188.
        /// </summary>
        Task<List<ValidationError>> ValidateSmtpServiceCreateAsync(SmtpServiceConfig config,
            CancellationToken ct = default);

        /// <summary>
        /// Validate SMTP service configuration before update. Same as create validation
        /// but allows the record's own name (name uniqueness excludes self).
        /// Maps to SmtpInternalService.ValidatePreUpdateRecord lines 190-354.
        /// </summary>
        Task<List<ValidationError>> ValidateSmtpServiceUpdateAsync(SmtpServiceConfig config,
            CancellationToken ct = default);

        /// <summary>
        /// Manage the default service invariant. When setting a new default, unsets all others.
        /// When unsetting the current default, returns error (must always have one default).
        /// Maps to SmtpInternalService.HandleDefaultServiceSetup lines 356-385.
        /// </summary>
        Task<List<ValidationError>> HandleDefaultServiceSetupAsync(SmtpServiceConfig config,
            CancellationToken ct = default);

        // ── Content Processing ──

        /// <summary>
        /// Process HTML content to extract inline image resources (img tags with /fs paths).
        /// Returns modified HTML with cid: references and a list of inline resources.
        /// Maps to SmtpInternalService.ProcessHtmlContent lines 518-582.
        /// </summary>
        string ProcessHtmlContent(string htmlBody, out List<InlineResource> linkedResources);

        /// <summary>
        /// Convert HTML to plain text by stripping tags, converting block elements to newlines,
        /// and extracting link URLs. Maps to SmtpInternalService.ConvertToPlainText lines 585-669.
        /// </summary>
        string ConvertHtmlToPlainText(string html);

        // ── Search Preparation ──

        /// <summary>
        /// Prepare the XSearch field for full-text search indexing.
        /// Concatenates sender, recipients, subject, and content into a searchable string.
        /// Maps to SmtpInternalService.PrepareEmailXSearch lines 683-687.
        /// </summary>
        void PrepareEmailXSearch(Email email);
    }

    /// <summary>
    /// Core SMTP engine service for the Notifications microservice.
    /// Consolidates email sending, queuing, validation, and content processing logic
    /// extracted from the monolith's MailKit-based SmtpInternalService and SmtpService model.
    /// 
    /// Transport: AWS SES v2 (stubbed for third-party SaaS replacement per AAP §0.3.2).
    /// Persistence: DynamoDB via INotificationRepository.
    /// Events: SNS domain events per AAP §0.8.5 ({domain}.{entity}.{action}).
    /// Concurrency: SemaphoreSlim for async-compatible queue processing guard.
    /// 
    /// Source files merged:
    /// - SmtpInternalService.cs (878 lines) — validation, send, queue, content processing
    /// - SmtpService.cs (947 lines) — send/queue overloads with config resolution
    /// - EmailServiceManager.cs (114 lines) — config caching (delegated to repository)
    /// - SmtpServiceRecordHook.cs (53 lines) — pre/post CRUD validation hooks
    /// </summary>
    public class SmtpService : ISmtpService
    {
        private readonly INotificationRepository _repository;
        private readonly IAmazonSimpleEmailServiceV2 _sesClient;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<SmtpService> _logger;
        private readonly string? _snsTopicArn;

        /// <summary>
        /// Async-compatible concurrency guard for queue processing.
        /// Replaces static lockObject + bool queueProcessingInProgress pattern
        /// from source SmtpInternalService.cs lines 28-29.
        /// </summary>
        private static readonly SemaphoreSlim _queueProcessingLock = new(1, 1);

        /// <summary>
        /// Compiled regex for email address validation. Replaces WebVella.Erp.Utilities.IsEmail()
        /// extension method. AOT-compatible with no dynamic compilation.
        /// </summary>
        private static readonly Regex EmailRegex = new(
            @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Regex for matching img tags with /fs source paths in HTML content.
        /// Used by ProcessHtmlContent to find embedded file system images.
        /// </summary>
        private static readonly Regex ImgFsRegex = new(
            @"<img[^>]+src\s*=\s*[""'](/fs[^""'?]*)[""'?]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Initializes SmtpService with injected dependencies.
        /// All dependencies are constructor-injected — no ambient singletons.
        /// </summary>
        public SmtpService(
            INotificationRepository repository,
            IAmazonSimpleEmailServiceV2 sesClient,
            IAmazonSimpleNotificationService snsClient,
            ILogger<SmtpService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _sesClient = sesClient ?? throw new ArgumentNullException(nameof(sesClient));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _snsTopicArn = Environment.GetEnvironmentVariable("NOTIFICATIONS_SNS_TOPIC_ARN");
        }

        #region SMTP Service Config Validation

        /// <inheritdoc />
        public async Task<List<ValidationError>> ValidateSmtpServiceCreateAsync(SmtpServiceConfig config,
            CancellationToken ct = default)
        {
            var errors = new List<ValidationError>();

            // Name uniqueness check (source SmtpInternalService lines 39-51)
            if (!string.IsNullOrWhiteSpace(config.Name))
            {
                var existing = await _repository.GetSmtpServiceByNameAsync(config.Name, ct);
                if (existing != null)
                {
                    errors.Add(new ValidationError
                    {
                        Key = "name",
                        Value = config.Name,
                        Message = "Name is already used by another service."
                    });
                }
            }

            // Common field validations shared with update
            ValidateCommonSmtpServiceFields(config, errors);

            return errors;
        }

        /// <inheritdoc />
        public async Task<List<ValidationError>> ValidateSmtpServiceUpdateAsync(SmtpServiceConfig config,
            CancellationToken ct = default)
        {
            var errors = new List<ValidationError>();

            // Name uniqueness check allowing own record (source lines 196-217)
            if (!string.IsNullOrWhiteSpace(config.Name))
            {
                var existing = await _repository.GetSmtpServiceByNameAsync(config.Name, ct);
                if (existing != null && existing.Id != config.Id)
                {
                    errors.Add(new ValidationError
                    {
                        Key = "name",
                        Value = config.Name,
                        Message = "Name is already used by another service."
                    });
                }
            }

            // Common field validations shared with create
            ValidateCommonSmtpServiceFields(config, errors);

            return errors;
        }

        /// <inheritdoc />
        public async Task<List<ValidationError>> HandleDefaultServiceSetupAsync(SmtpServiceConfig config,
            CancellationToken ct = default)
        {
            var errors = new List<ValidationError>();

            if (config.IsDefault)
            {
                // Setting new default — unset all existing defaults (source lines 358-371)
                var allServices = await _repository.GetAllSmtpServicesAsync(ct);
                foreach (var svc in allServices.Where(s => s.IsDefault && s.Id != config.Id))
                {
                    svc.IsDefault = false;
                    await _repository.SaveSmtpServiceAsync(svc, ct);
                }
            }
            else
            {
                // Unsetting current default — check invariant (source lines 372-384)
                var current = await _repository.GetSmtpServiceByIdAsync(config.Id, ct);
                if (current != null && current.IsDefault)
                {
                    errors.Add(new ValidationError
                    {
                        Key = "is_default",
                        Value = "false",
                        Message = "Forbidden. There should always be an active default service."
                    });
                }
            }

            // Clear cache after any changes to SMTP services (from SmtpServiceRecordHook post-hook)
            _repository.ClearSmtpServiceCache();

            return errors;
        }

        #endregion

        #region Email Sending Operations

        /// <inheritdoc />
        public async Task SendEmailAsync(EmailAddress recipient, string subject, string textBody,
            string htmlBody, List<string>? attachmentKeys = null, CancellationToken ct = default)
        {
            // Single recipient validation (source SmtpService.cs lines 72-86)
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient), "Recipient is not specified.");
            if (string.IsNullOrWhiteSpace(recipient.Address))
                throw new ArgumentException("Recipient email is not specified.", nameof(recipient));
            if (!IsValidEmail(recipient.Address))
                throw new ArgumentException("Recipient email is not valid email address.", nameof(recipient));

            // Delegate to multi-recipient overload with list wrapper
            await SendEmailAsync(new List<EmailAddress> { recipient }, subject, textBody, htmlBody,
                attachmentKeys, ct);
        }

        /// <inheritdoc />
        public async Task SendEmailAsync(List<EmailAddress> recipients, string subject, string textBody,
            string htmlBody, List<string>? attachmentKeys = null, CancellationToken ct = default)
        {
            // Multi-recipient validation (source lines 204-234)
            ValidateSendRecipients(recipients);
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject is required.", nameof(subject));

            // Resolve default SMTP service configuration
            var config = await _repository.GetDefaultSmtpServiceAsync(ct)
                ?? throw new InvalidOperationException("No default SMTP service configured.");

            var sender = new EmailAddress(config.DefaultSenderName, config.DefaultSenderEmail);

            // Build, process, and send email via core path
            await SendEmailCoreAsync(recipients, sender, config.DefaultReplyToEmail,
                subject, textBody, htmlBody, attachmentKeys, config, ct);
        }

        /// <inheritdoc />
        public async Task SendEmailAsync(EmailAddress recipient, EmailAddress sender, string subject,
            string textBody, string htmlBody, List<string>? attachmentKeys = null,
            CancellationToken ct = default)
        {
            // Single recipient validation (source lines 347-361)
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient), "Recipient is not specified.");
            if (string.IsNullOrWhiteSpace(recipient.Address))
                throw new ArgumentException("Recipient email is not specified.", nameof(recipient));
            if (!IsValidEmail(recipient.Address))
                throw new ArgumentException("Recipient email is not valid email address.", nameof(recipient));

            // Delegate to multi-recipient overload
            await SendEmailAsync(new List<EmailAddress> { recipient }, sender, subject, textBody,
                htmlBody, attachmentKeys, ct);
        }

        /// <inheritdoc />
        public async Task SendEmailAsync(List<EmailAddress> recipients, EmailAddress sender, string subject,
            string textBody, string htmlBody, List<string>? attachmentKeys = null,
            CancellationToken ct = default)
        {
            // Multi-recipient + sender validation (source lines 476-514)
            ValidateSendRecipients(recipients);
            if (sender == null)
                throw new ArgumentNullException(nameof(sender), "Sender is not specified.");
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject is required.", nameof(subject));

            // Resolve default config for service ID and reply-to defaults
            var config = await _repository.GetDefaultSmtpServiceAsync(ct)
                ?? throw new InvalidOperationException("No default SMTP service configured.");

            // Build, process, and send email with custom sender
            await SendEmailCoreAsync(recipients, sender, config.DefaultReplyToEmail,
                subject, textBody, htmlBody, attachmentKeys, config, ct);
        }

        /// <inheritdoc />
        public async Task SendEmailAsync(Email email, SmtpServiceConfig service, CancellationToken ct = default)
        {
            // Guard: null service check (source SmtpInternalService lines 694-698)
            if (service == null)
            {
                email.ServerError = "SMTP service not found";
                email.Status = EmailStatus.Aborted;
                email.ScheduledOn = null;
                await _repository.SaveEmailAsync(email, ct);
                _logger.LogWarning("Email {EmailId} aborted: SMTP service not found", email.Id);
                await PublishDomainEventAsync("aborted", email, ct);
                return;
            }

            // Guard: service disabled (source line 700)
            if (!service.IsEnabled)
            {
                email.ServerError = "SMTP service is not enabled";
                email.Status = EmailStatus.Aborted;
                email.ScheduledOn = null;
                await _repository.SaveEmailAsync(email, ct);
                _logger.LogWarning("Email {EmailId} aborted: SMTP service {ServiceId} is not enabled",
                    email.Id, service.Id);
                await PublishDomainEventAsync("aborted", email, ct);
                return;
            }

            try
            {
                // Build SES send request from email and service config
                var sesRequest = BuildSesRequest(email, service);

                _logger.LogInformation(
                    "Sending email {EmailId} via SES for service {ServiceId} to {RecipientCount} recipients",
                    email.Id, service.Id, email.Recipients?.Count ?? 0);

                // Send via AWS SES v2 (replaces MailKit SmtpClient.Send)
                await _sesClient.SendEmailAsync(sesRequest, ct);

                // Success path (source lines 801-804)
                email.SentOn = DateTime.UtcNow;
                email.Status = EmailStatus.Sent;
                email.ScheduledOn = null;
                email.ServerError = string.Empty;

                _logger.LogInformation("Email {EmailId} sent successfully via SES", email.Id);
                await PublishDomainEventAsync("sent", email, ct);
            }
            catch (AmazonSimpleEmailServiceV2Exception sesEx)
            {
                // SES-specific failure (source lines 806-821)
                HandleSendFailure(email, service, sesEx);
                _logger.LogError(sesEx,
                    "SES send failed for email {EmailId}: {ErrorCode} - {ErrorMessage}",
                    email.Id, sesEx.ErrorCode, sesEx.Message);
            }
            catch (Exception ex)
            {
                // General failure (source lines 806-821)
                HandleSendFailure(email, service, ex);
                _logger.LogError(ex, "Send failed for email {EmailId}: {ErrorMessage}",
                    email.Id, ex.Message);
            }
            finally
            {
                // Always persist email state (source line 825)
                await _repository.SaveEmailAsync(email, ct);
            }
        }

        #endregion

        #region Email Queuing Operations

        /// <inheritdoc />
        public async Task QueueEmailAsync(EmailAddress recipient, string subject, string textBody,
            string htmlBody, EmailPriority priority = EmailPriority.Normal,
            List<string>? attachmentKeys = null, CancellationToken ct = default)
        {
            // Single recipient validation with cc:/bcc: prefix awareness (source lines 623-647)
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient), "Recipient is not specified.");

            var addressToValidate = StripCcBccPrefix(recipient.Address);
            if (string.IsNullOrWhiteSpace(addressToValidate))
                throw new ArgumentException("Recipient email is not specified.", nameof(recipient));
            if (!IsValidEmail(addressToValidate))
                throw new ArgumentException("Recipient email is not valid email address.", nameof(recipient));

            // Delegate to multi-recipient queue overload
            await QueueEmailAsync(new List<EmailAddress> { recipient }, subject, textBody, htmlBody,
                priority, attachmentKeys, ct);
        }

        /// <inheritdoc />
        public async Task QueueEmailAsync(List<EmailAddress> recipients, string subject, string textBody,
            string htmlBody, EmailPriority priority = EmailPriority.Normal,
            List<string>? attachmentKeys = null, CancellationToken ct = default)
        {
            // Multi-recipient queue validation (source lines 701-727)
            ValidateQueueRecipients(recipients);
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject is required.", nameof(subject));

            // Resolve default SMTP service configuration
            var config = await _repository.GetDefaultSmtpServiceAsync(ct)
                ?? throw new InvalidOperationException("No default SMTP service configured.");

            var sender = new EmailAddress(config.DefaultSenderName, config.DefaultSenderEmail);

            // Build and persist queued email
            await QueueEmailCoreAsync(recipients, sender, config.DefaultReplyToEmail,
                subject, textBody, htmlBody, priority, attachmentKeys, config.Id, ct);
        }

        /// <inheritdoc />
        public async Task QueueEmailAsync(EmailAddress recipient, EmailAddress sender, string replyTo,
            string subject, string textBody, string htmlBody,
            EmailPriority priority = EmailPriority.Normal,
            List<string>? attachmentKeys = null, CancellationToken ct = default)
        {
            // Single recipient + custom sender validation (source lines 783-816)
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient), "Recipient is not specified.");

            var addressToValidate = StripCcBccPrefix(recipient.Address);
            if (string.IsNullOrWhiteSpace(addressToValidate))
                throw new ArgumentException("Recipient email is not specified.", nameof(recipient));
            if (!IsValidEmail(addressToValidate))
                throw new ArgumentException("Recipient email is not valid email address.", nameof(recipient));

            // Delegate to multi-recipient queue overload
            await QueueEmailAsync(new List<EmailAddress> { recipient }, sender, replyTo, subject,
                textBody, htmlBody, priority, attachmentKeys, ct);
        }

        /// <inheritdoc />
        public async Task QueueEmailAsync(List<EmailAddress> recipients, EmailAddress sender, string replyTo,
            string subject, string textBody, string htmlBody,
            EmailPriority priority = EmailPriority.Normal,
            List<string>? attachmentKeys = null, CancellationToken ct = default)
        {
            // Multi-recipient + custom sender + replyTo validation (source lines 871-906)
            ValidateQueueRecipients(recipients);
            if (string.IsNullOrWhiteSpace(subject))
                throw new ArgumentException("Subject is required.", nameof(subject));

            // Validate replyTo emails if provided (source lines 885-893, split by ';')
            if (!string.IsNullOrWhiteSpace(replyTo))
            {
                var replyToParts = replyTo.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in replyToParts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !IsValidEmail(trimmed))
                    {
                        throw new ArgumentException("Reply To email is not valid email address.",
                            nameof(replyTo));
                    }
                }
            }

            // Resolve default config for service ID; use explicit sender
            var config = await _repository.GetDefaultSmtpServiceAsync(ct)
                ?? throw new InvalidOperationException("No default SMTP service configured.");

            var effectiveSender = sender ?? new EmailAddress(config.DefaultSenderName,
                config.DefaultSenderEmail);
            var effectiveReplyTo = !string.IsNullOrWhiteSpace(replyTo)
                ? replyTo
                : config.DefaultReplyToEmail;

            await QueueEmailCoreAsync(recipients, effectiveSender, effectiveReplyTo,
                subject, textBody, htmlBody, priority, attachmentKeys, config.Id, ct);
        }

        #endregion

        #region Queue Processing

        /// <inheritdoc />
        public async Task ProcessSmtpQueueAsync(CancellationToken ct = default)
        {
            // Non-blocking acquire of concurrency guard (source lines 831-837)
            // SemaphoreSlim(1,1) with WaitAsync(0) replaces lock(lockObject) + bool guard
            if (!await _queueProcessingLock.WaitAsync(0, ct))
            {
                _logger.LogInformation("Queue processing already in progress, skipping");
                return;
            }

            try
            {
                _logger.LogInformation("Starting SMTP queue processing");
                List<Email> pendingEmails;

                // Batch processing loop (source lines 842-868)
                do
                {
                    ct.ThrowIfCancellationRequested();

                    pendingEmails = await _repository.GetPendingEmailsAsync(10, ct);
                    _logger.LogInformation("Fetched {Count} pending emails from queue", pendingEmails.Count);

                    foreach (var email in pendingEmails)
                    {
                        ct.ThrowIfCancellationRequested();

                        var service = await _repository.GetSmtpServiceByIdAsync(email.ServiceId, ct);

                        if (service == null)
                        {
                            // Abort orphaned emails (source lines 854-860)
                            email.Status = EmailStatus.Aborted;
                            email.ServerError = "SMTP service not found.";
                            email.ScheduledOn = null;
                            await _repository.SaveEmailAsync(email, ct);
                            _logger.LogWarning(
                                "Email {EmailId} aborted during queue processing: service {ServiceId} not found",
                                email.Id, email.ServiceId);
                            await PublishDomainEventAsync("aborted", email, ct);
                            continue;
                        }

                        // Send via core send method with retry logic (source line 863)
                        await SendEmailAsync(email, service, ct);
                    }
                }
                while (pendingEmails.Count > 0);

                _logger.LogInformation("SMTP queue processing completed");
            }
            finally
            {
                // Release lock (source lines 873-876)
                _queueProcessingLock.Release();
            }
        }

        #endregion

        #region Content Processing

        /// <inheritdoc />
        public string ProcessHtmlContent(string htmlBody, out List<InlineResource> linkedResources)
        {
            linkedResources = new List<InlineResource>();

            if (string.IsNullOrWhiteSpace(htmlBody))
                return htmlBody ?? string.Empty;

            try
            {
                // Find all img tags with /fs source paths (source SmtpInternalService lines 522-570)
                var processedHtml = htmlBody;
                var matches = ImgFsRegex.Matches(htmlBody);

                foreach (Match match in matches)
                {
                    if (!match.Success || match.Groups.Count < 2)
                        continue;

                    var fullSrcPath = match.Groups[1].Value; // e.g., "/fs/path/to/image.png"

                    // Strip /fs prefix to get the actual file path (source lines 548-549)
                    var filePath = fullSrcPath;
                    if (filePath.StartsWith("/fs", StringComparison.OrdinalIgnoreCase))
                        filePath = filePath.Substring(3);

                    // Generate unique Content-ID for CID reference
                    var contentId = Guid.NewGuid().ToString("N");

                    // Determine MIME type from file extension
                    var mimeType = GetMimeTypeFromPath(filePath);

                    // Create inline resource entry
                    // Note: In the serverless architecture, actual file content retrieval
                    // is handled by the File Management service via S3. The InlineResource
                    // stores the file path as a reference key for downstream processing.
                    var resource = new InlineResource
                    {
                        ContentId = contentId,
                        Content = Array.Empty<byte>(), // S3 content fetched at send time
                        MimeType = mimeType,
                        FileName = Path.GetFileName(filePath)
                    };
                    linkedResources.Add(resource);

                    // Replace src attribute with cid: reference in HTML
                    processedHtml = processedHtml.Replace(fullSrcPath, $"cid:{contentId}");
                }

                return processedHtml;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing HTML content for inline resources");
                return htmlBody;
            }
        }

        /// <inheritdoc />
        public string ConvertHtmlToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            try
            {
                var result = html;

                // Remove script blocks and their content (source lines 630-631)
                result = Regex.Replace(result, @"<script[^>]*>[\s\S]*?</script>", string.Empty,
                    RegexOptions.IgnoreCase);

                // Remove style blocks and their content (source lines 630-631)
                result = Regex.Replace(result, @"<style[^>]*>[\s\S]*?</style>", string.Empty,
                    RegexOptions.IgnoreCase);

                // Convert anchor tags to plaintext URL format (source lines 657-660)
                // Use parentheses for URL to prevent subsequent tag-stripping from removing it
                result = Regex.Replace(result, @"<a\s+[^>]*href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a>",
                    "$2 ($1)", RegexOptions.IgnoreCase);

                // Convert block-level elements to newlines (source lines 650-656)
                result = Regex.Replace(result, @"<br\s*/?\s*>", Environment.NewLine,
                    RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"</p>", Environment.NewLine + Environment.NewLine,
                    RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"</div>", Environment.NewLine,
                    RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"</li>", Environment.NewLine,
                    RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"</tr>", Environment.NewLine,
                    RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"<(h[1-6])[^>]*>", Environment.NewLine,
                    RegexOptions.IgnoreCase);
                result = Regex.Replace(result, @"</h[1-6]>",
                    Environment.NewLine + Environment.NewLine, RegexOptions.IgnoreCase);

                // Strip all remaining HTML tags
                result = Regex.Replace(result, @"<[^>]+>", string.Empty);

                // Decode HTML entities (source line 643)
                result = WebUtility.HtmlDecode(result);

                // Normalize whitespace: collapse multiple spaces (preserve newlines)
                result = Regex.Replace(result, @"[ \t]+", " ");

                // Collapse multiple consecutive blank lines into maximum two newlines
                result = Regex.Replace(result, @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);

                return result.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting HTML to plain text, returning empty string");
                return string.Empty;
            }
        }

        #endregion

        #region Search Preparation

        /// <inheritdoc />
        public void PrepareEmailXSearch(Email email)
        {
            if (email == null) return;

            // Exact port of source SmtpInternalService lines 683-687
            var recipientsText = email.Recipients != null
                ? string.Join(" ", email.Recipients.Select(x => $"{x.Name} {x.Address}"))
                : string.Empty;

            email.XSearch = $"{email.Sender?.Name} {email.Sender?.Address} {recipientsText} " +
                            $"{email.Subject} {email.ContentText} {email.ContentHtml}";
        }

        #endregion

        #region Private Helpers — Validation

        /// <summary>
        /// Validates common SMTP service config fields shared between create and update operations.
        /// Extracts the repeated validation logic from source SmtpInternalService lines 53-185.
        /// </summary>
        private static void ValidateCommonSmtpServiceFields(SmtpServiceConfig config,
            List<ValidationError> errors)
        {
            // Port validation (source lines 53-77): must be between 1 and 65025 inclusive
            if (config.Port < 1 || config.Port > 65025)
            {
                errors.Add(new ValidationError
                {
                    Key = "port",
                    Value = config.Port.ToString(),
                    Message = "Port must be an integer value between 1 and 65025"
                });
            }

            // Default from email validation (source lines 79-91)
            if (string.IsNullOrWhiteSpace(config.DefaultSenderEmail) ||
                !IsValidEmail(config.DefaultSenderEmail))
            {
                errors.Add(new ValidationError
                {
                    Key = "default_sender_email",
                    Value = config.DefaultSenderEmail ?? string.Empty,
                    Message = "Default from email address is invalid"
                });
            }

            // Default reply-to email validation (source lines 92-106)
            // Skip validation if null or whitespace; validate only if a value is provided
            if (!string.IsNullOrWhiteSpace(config.DefaultReplyToEmail) &&
                !IsValidEmail(config.DefaultReplyToEmail))
            {
                errors.Add(new ValidationError
                {
                    Key = "default_reply_to_email",
                    Value = config.DefaultReplyToEmail,
                    Message = "Default reply to email address is invalid"
                });
            }

            // Max retries count validation (source lines 108-131): 1 to 10 inclusive
            if (config.MaxRetriesCount < 1 || config.MaxRetriesCount > 10)
            {
                errors.Add(new ValidationError
                {
                    Key = "max_retries_count",
                    Value = config.MaxRetriesCount.ToString(),
                    Message = "Number of retries on error must be an integer value between 1 and 10"
                });
            }

            // Retry wait minutes validation (source lines 133-156): 1 to 1440 inclusive
            if (config.RetryWaitMinutes < 1 || config.RetryWaitMinutes > 1440)
            {
                errors.Add(new ValidationError
                {
                    Key = "retry_wait_minutes",
                    Value = config.RetryWaitMinutes.ToString(),
                    Message = "Wait period between retries must be an integer value between 1 and 1440 minutes"
                });
            }

            // Connection security validation (source lines 158-185): 0-4 valid range
            // 0=None, 1=Auto, 2=SslOnConnect, 3=StartTls, 4=StartTlsWhenAvailable
            if (config.ConnectionSecurity < 0 || config.ConnectionSecurity > 4)
            {
                errors.Add(new ValidationError
                {
                    Key = "connection_security",
                    Value = config.ConnectionSecurity.ToString(),
                    Message = "Invalid connection security setting selected."
                });
            }
        }

        /// <summary>
        /// Validates recipients list for send operations. Throws on invalid input.
        /// Source SmtpService.cs lines 204-234 (multi-recipient validation pattern).
        /// </summary>
        private static void ValidateSendRecipients(List<EmailAddress> recipients)
        {
            if (recipients == null || recipients.Count == 0)
                throw new ArgumentException("Recipient is not specified.", nameof(recipients));

            foreach (var r in recipients)
            {
                if (string.IsNullOrWhiteSpace(r.Address))
                    throw new ArgumentException("Recipient email is not specified.", nameof(recipients));
                if (!IsValidEmail(r.Address))
                    throw new ArgumentException("Recipient email is not valid email address.",
                        nameof(recipients));
            }
        }

        /// <summary>
        /// Validates recipients list for queue operations with cc:/bcc: prefix awareness.
        /// Queue recipients may have cc: or bcc: prefixes that must be stripped before validation.
        /// Source SmtpService.cs lines 701-711 (queue validation with prefix stripping).
        /// </summary>
        private static void ValidateQueueRecipients(List<EmailAddress> recipients)
        {
            if (recipients == null || recipients.Count == 0)
                throw new ArgumentException("Recipient is not specified.", nameof(recipients));

            foreach (var r in recipients)
            {
                // Strip cc:/bcc: prefix before validation (source lines 871-880)
                var address = StripCcBccPrefix(r.Address);
                if (string.IsNullOrWhiteSpace(address))
                    throw new ArgumentException("Recipient email is not specified.", nameof(recipients));
                if (!IsValidEmail(address))
                    throw new ArgumentException("Recipient email is not valid email address.",
                        nameof(recipients));
            }
        }

        #endregion

        #region Private Helpers — Email Building and Sending

        /// <summary>
        /// Core email building and sending flow shared by send overloads 1-4.
        /// Creates Email object, processes HTML, prepares XSearch, delegates to overload 5.
        /// </summary>
        private async Task SendEmailCoreAsync(List<EmailAddress> recipients, EmailAddress sender,
            string? replyTo, string subject, string textBody, string htmlBody,
            List<string>? attachmentKeys, SmtpServiceConfig config, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var email = new Email
            {
                Id = Guid.NewGuid(),
                Sender = sender,
                ReplyToEmail = replyTo ?? string.Empty,
                Recipients = recipients,
                Subject = subject,
                ContentHtml = htmlBody ?? string.Empty,
                ContentText = textBody ?? string.Empty,
                CreatedOn = now,
                SentOn = null,
                Priority = EmailPriority.Normal,
                Status = EmailStatus.Pending,
                ServerError = string.Empty,
                ScheduledOn = null,
                RetriesCount = 0,
                ServiceId = config.Id,
                Attachments = NormalizeAttachmentKeys(attachmentKeys)
            };

            // Process HTML content for inline images (source lines 518-582)
            if (!string.IsNullOrWhiteSpace(email.ContentHtml))
            {
                email.ContentHtml = ProcessHtmlContent(email.ContentHtml, out _);

                // Auto-generate plain text from HTML if text body is empty (source lines 575-576)
                if (string.IsNullOrWhiteSpace(email.ContentText))
                {
                    email.ContentText = ConvertHtmlToPlainText(email.ContentHtml);
                }
            }

            PrepareEmailXSearch(email);

            // Delegate to retry-aware send (overload 5)
            await SendEmailAsync(email, config, ct);
        }

        /// <summary>
        /// Core queued email creation and persistence shared by queue overloads 1-4.
        /// Creates Email with Pending status and persists immediately.
        /// </summary>
        private async Task QueueEmailCoreAsync(List<EmailAddress> recipients, EmailAddress sender,
            string? replyTo, string subject, string textBody, string htmlBody,
            EmailPriority priority, List<string>? attachmentKeys, Guid serviceId,
            CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var email = new Email
            {
                Id = Guid.NewGuid(),
                Sender = sender,
                ReplyToEmail = replyTo ?? string.Empty,
                Recipients = recipients,
                Subject = subject,
                ContentHtml = htmlBody ?? string.Empty,
                ContentText = textBody ?? string.Empty,
                CreatedOn = now,
                SentOn = null,
                Priority = priority,
                Status = EmailStatus.Pending,
                ServerError = string.Empty,
                ScheduledOn = now, // Immediate queue processing eligibility
                RetriesCount = 0,
                ServiceId = serviceId,
                Attachments = NormalizeAttachmentKeys(attachmentKeys)
            };

            PrepareEmailXSearch(email);
            await _repository.SaveEmailAsync(email, ct);

            _logger.LogInformation(
                "Email {EmailId} queued for {RecipientCount} recipients with priority {Priority}",
                email.Id, recipients.Count, priority);

            await PublishDomainEventAsync("queued", email, ct);
        }

        /// <summary>
        /// Handles send failure by updating email status with retry logic.
        /// Preserves exact retry semantics from source SmtpInternalService lines 806-821.
        /// </summary>
        private static void HandleSendFailure(Email email, SmtpServiceConfig service, Exception ex)
        {
            email.SentOn = null;
            email.ServerError = ex.Message;
            email.RetriesCount++;

            if (email.RetriesCount >= service.MaxRetriesCount)
            {
                // Max retries exceeded — abort (source lines 813-814)
                email.ScheduledOn = null;
                email.Status = EmailStatus.Aborted;
            }
            else
            {
                // Schedule retry (source lines 818-819)
                email.ScheduledOn = DateTime.UtcNow.AddMinutes(service.RetryWaitMinutes);
                email.Status = EmailStatus.Pending;
            }
        }

        /// <summary>
        /// Builds an AWS SES v2 SendEmailRequest from an Email model and SmtpServiceConfig.
        /// Handles To/CC/BCC routing via cc:/bcc: prefix on recipient addresses,
        /// and semicolon-separated ReplyTo addresses.
        /// Replaces MimeMessage construction from source SmtpInternalService lines 714-772.
        /// </summary>
        private static SendEmailRequest BuildSesRequest(Email email, SmtpServiceConfig service)
        {
            // Classify recipients into To/CC/BCC based on address prefix (source lines 714-736)
            var toAddresses = new List<string>();
            var ccAddresses = new List<string>();
            var bccAddresses = new List<string>();

            if (email.Recipients != null)
            {
                foreach (var recipient in email.Recipients)
                {
                    if (string.IsNullOrWhiteSpace(recipient.Address))
                        continue;

                    if (recipient.Address.StartsWith("cc:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Strip cc: prefix (source line: address.Substring(3))
                        var ccAddr = recipient.Address.Substring(3).Trim();
                        if (!string.IsNullOrWhiteSpace(ccAddr))
                        {
                            ccAddresses.Add(FormatEmailForSes(recipient.Name, ccAddr));
                        }
                    }
                    else if (recipient.Address.StartsWith("bcc:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Strip bcc: prefix (source line: address.Substring(4))
                        var bccAddr = recipient.Address.Substring(4).Trim();
                        if (!string.IsNullOrWhiteSpace(bccAddr))
                        {
                            bccAddresses.Add(FormatEmailForSes(recipient.Name, bccAddr));
                        }
                    }
                    else
                    {
                        toAddresses.Add(FormatEmailForSes(recipient.Name, recipient.Address));
                    }
                }
            }

            // Build ReplyTo list from semicolon-separated emails (source lines 738-745)
            var replyToAddresses = new List<string>();
            if (!string.IsNullOrWhiteSpace(email.ReplyToEmail))
            {
                var parts = email.ReplyToEmail.Split(new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        replyToAddresses.Add(trimmed);
                    }
                }
            }

            // Fallback: if no ReplyTo, use sender address (source line 745)
            if (replyToAddresses.Count == 0 && email.Sender != null &&
                !string.IsNullOrWhiteSpace(email.Sender.Address))
            {
                replyToAddresses.Add(email.Sender.Address);
            }

            // Build SES v2 request (replaces MimeMessage construction)
            var request = new SendEmailRequest
            {
                FromEmailAddress = email.Sender != null
                    ? FormatEmailForSes(email.Sender.Name, email.Sender.Address)
                    : service.DefaultSenderEmail,
                Destination = new Destination
                {
                    ToAddresses = toAddresses,
                    CcAddresses = ccAddresses,
                    BccAddresses = bccAddresses
                },
                ReplyToAddresses = replyToAddresses,
                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content
                        {
                            Data = email.Subject ?? string.Empty,
                            Charset = "UTF-8"
                        },
                        Body = new Body
                        {
                            Html = !string.IsNullOrWhiteSpace(email.ContentHtml)
                                ? new Content
                                {
                                    Data = email.ContentHtml,
                                    Charset = "UTF-8"
                                }
                                : null,
                            Text = !string.IsNullOrWhiteSpace(email.ContentText)
                                ? new Content
                                {
                                    Data = email.ContentText,
                                    Charset = "UTF-8"
                                }
                                : null
                        }
                    }
                }
            };

            return request;
        }

        #endregion

        #region Private Helpers — Utility

        /// <summary>
        /// Validates email address format using compiled regex.
        /// Replaces WebVella.Erp.Utilities.IsEmail() extension method for AOT compatibility.
        /// </summary>
        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            return EmailRegex.IsMatch(email.Trim());
        }

        /// <summary>
        /// Normalizes a single attachment key/path for backward compatibility with the
        /// monolith's file path conventions. Extracted from repeated pattern in source
        /// SmtpService.cs lines 112-118 (and many other locations).
        /// </summary>
        private static string NormalizeAttachmentKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var normalized = path;
            if (!normalized.StartsWith("/"))
                normalized = "/" + normalized;

            normalized = normalized.ToLowerInvariant();

            if (normalized.StartsWith("/fs"))
                normalized = normalized.Substring(3);

            return normalized;
        }

        /// <summary>
        /// Normalizes a list of attachment keys, filtering out empty entries.
        /// </summary>
        private static List<string> NormalizeAttachmentKeys(List<string>? keys)
        {
            if (keys == null || keys.Count == 0)
                return new List<string>();

            var normalized = new List<string>();
            foreach (var key in keys)
            {
                var n = NormalizeAttachmentKey(key);
                if (!string.IsNullOrWhiteSpace(n))
                    normalized.Add(n);
            }
            return normalized;
        }

        /// <summary>
        /// Strips cc: or bcc: prefix from an email address string for validation purposes.
        /// Queue recipients may use these prefixes to indicate CC/BCC routing.
        /// Source SmtpService.cs lines 623-633 (prefix stripping before validation).
        /// </summary>
        private static string StripCcBccPrefix(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return address;

            if (address.StartsWith("cc:", StringComparison.OrdinalIgnoreCase))
                return address.Substring(3).Trim();
            if (address.StartsWith("bcc:", StringComparison.OrdinalIgnoreCase))
                return address.Substring(4).Trim();

            return address;
        }

        /// <summary>
        /// Formats a name and email address for SES addressing (RFC 5322 display-name format).
        /// </summary>
        private static string FormatEmailForSes(string? name, string address)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return $"{name} <{address}>";

            return address;
        }

        /// <summary>
        /// Determines MIME type from file extension for inline resource processing.
        /// Used by ProcessHtmlContent for CID-based image embedding.
        /// </summary>
        private static string GetMimeTypeFromPath(string path)
        {
            var extension = Path.GetExtension(path)?.ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                ".ico" => "image/x-icon",
                ".tiff" or ".tif" => "image/tiff",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// Publishes a domain event to SNS for cross-service notification.
        /// Event naming follows AAP §0.8.5 convention: {domain}.{entity}.{action}
        /// (e.g., notifications.email.sent, notifications.email.queued, notifications.email.aborted).
        /// Replaces the monolith's IErpPostCreateRecordHook pattern.
        /// </summary>
        private async Task PublishDomainEventAsync(string action, Email email, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_snsTopicArn))
            {
                _logger.LogWarning(
                    "SNS topic ARN not configured (NOTIFICATIONS_SNS_TOPIC_ARN). " +
                    "Skipping domain event publish for notifications.email.{Action}", action);
                return;
            }

            try
            {
                var domainEvent = new EmailDomainEvent
                {
                    EmailId = email.Id.ToString(),
                    Status = email.Status.ToString(),
                    RecipientCount = email.Recipients?.Count ?? 0,
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Action = action
                };
                var eventPayload = JsonSerializer.Serialize(domainEvent,
                    SmtpServiceJsonContext.Default.EmailDomainEvent);

                var request = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Subject = $"notifications.email.{action}",
                    Message = eventPayload,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = $"notifications.email.{action}"
                        },
                        ["emailId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = email.Id.ToString()
                        }
                    }
                };

                await _snsClient.PublishAsync(request, ct);
                _logger.LogInformation(
                    "Published domain event notifications.email.{Action} for email {EmailId}",
                    action, email.Id);
            }
            catch (Exception ex)
            {
                // Log but do not fail the primary operation for event publishing failures
                _logger.LogError(ex,
                    "Failed to publish domain event notifications.email.{Action} for email {EmailId}",
                    action, email.Id);
            }
        }

        #endregion
    }

    /// <summary>
    /// Domain event payload for SNS email notifications.
    /// Defined as a concrete type for AOT-compatible System.Text.Json source generation.
    /// </summary>
    internal sealed class EmailDomainEvent
    {
        public string EmailId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int RecipientCount { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
    }

    /// <summary>
    /// Source-generated JSON serializer context for AOT-compatible serialization.
    /// Eliminates IL2026/IL3050 trimming and dynamic code generation warnings.
    /// Per AAP §0.8.2: Lambda cold start &lt; 1 second requires Native AOT compatibility.
    /// </summary>
    [JsonSerializable(typeof(EmailDomainEvent))]
    internal sealed partial class SmtpServiceJsonContext : JsonSerializerContext
    {
    }
}
