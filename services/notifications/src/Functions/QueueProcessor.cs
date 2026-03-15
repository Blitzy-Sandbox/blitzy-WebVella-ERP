using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Models;
using WebVellaErp.Notifications.Services;

// Assembly attribute for Lambda JSON serialization (System.Text.Json for AOT compat)
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Notifications.Functions
{
    // ───────────────────────────────────────────────────────────────────
    // AOT Source-Generated JSON Serialization Context
    // ───────────────────────────────────────────────────────────────────
    // Registers every type that flows through JsonSerializer.Serialize /
    // Deserialize to eliminate IL2026/IL3050 Native-AOT trimming warnings.
    // ───────────────────────────────────────────────────────────────────

    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(QueueProcessorCommand))]
    [JsonSerializable(typeof(EmailSendRequest))]
    [JsonSerializable(typeof(EmailDomainEvent))]
    internal partial class QueueProcessorJsonContext : JsonSerializerContext { }

    // ───────────────────────────────────────────────────────────────────
    // Command DTO — Deserialized from SQS message body (Mode 1)
    // ───────────────────────────────────────────────────────────────────
    // When EventBridge / CloudWatch scheduled rule pushes a
    // "process-queue" command to the SQS queue, this DTO carries it.
    // ───────────────────────────────────────────────────────────────────

    internal sealed class QueueProcessorCommand
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;
    }

    // ───────────────────────────────────────────────────────────────────
    // Individual Email Send Request DTO (Mode 2)
    // ───────────────────────────────────────────────────────────────────
    // Queued by the EmailHandler or SmtpService.QueueEmailAsync when an
    // email is ready for async delivery through the SQS trigger.
    // ───────────────────────────────────────────────────────────────────

    internal sealed class EmailSendRequest
    {
        [JsonPropertyName("email_id")]
        public string EmailId { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;
    }

    // ───────────────────────────────────────────────────────────────────
    // Domain Event Payload for SNS Publishing
    // ───────────────────────────────────────────────────────────────────
    // Published after each email state transition per AAP §0.8.5:
    //   notifications.email.sent
    //   notifications.email.failed
    //   notifications.email.rescheduled
    // ───────────────────────────────────────────────────────────────────

    internal sealed class EmailDomainEvent
    {
        [JsonPropertyName("event_id")]
        public string EventId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("email_id")]
        public string EmailId { get; set; } = string.Empty;

        [JsonPropertyName("service_id")]
        public string ServiceId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("recipients_count")]
        public int RecipientsCount { get; set; }

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // QueueProcessor — SQS-Triggered Lambda for Email Queue Processing
    // ═══════════════════════════════════════════════════════════════════
    //
    // Replaces the monolith's scheduled background job for email queue
    // processing:
    //
    //   ProcessSmtpQueueJob.cs (18 lines) — ErpJob with GUID
    //     9b301dca-6c81-40dd-887c-efd31c23bd77, name "Process SMTP queue",
    //     enabled=true, priority=Low. Delegates to
    //     SmtpInternalService().ProcessSmtpQueue().
    //
    //   SmtpInternalService.ProcessSmtpQueue() (lines 829-878) — batch
    //     draining with static lock, batch of 10, priority DESC +
    //     scheduled_on ASC ordering, and retry logic.
    //
    // Source Architecture Replaced:
    //   JobManager singleton → SQS trigger
    //   JobPool 20-thread executor → Lambda concurrency
    //   SecurityContext.OpenSystemScope() → IAM role permissions
    //   static lockObject guard → SQS reserved concurrency (1)
    //   [Job] attribute / ErpJob base → Lambda function
    //   10-minute SchedulePlan → EventBridge scheduled rule
    //   Newtonsoft.Json → System.Text.Json (AOT compat)
    //   PostgreSQL Npgsql → DynamoDB via INotificationRepository
    //
    // Two Processing Modes:
    //   Mode 1 — Scheduled Queue Drain:
    //     SQS message body = {"command": "process-queue"}
    //     Delegates to ISmtpService.ProcessSmtpQueueAsync()
    //
    //   Mode 2 — Individual Email Send:
    //     SQS message body = {"email_id": "...", "action": "send"}
    //     Loads email, checks idempotency, sends via ISmtpService
    //
    // ═══════════════════════════════════════════════════════════════════

    public class QueueProcessor
    {
        // ── Constants ────────────────────────────────────────────────
        private const string SnsTopicArnEnvVar = "NOTIFICATIONS_SNS_TOPIC_ARN";
        private const string ProcessQueueCommand = "process-queue";
        private const string SendAction = "send";
        private const string SmtpServiceNotFoundError = "SMTP service not found.";

        // ── Dependencies ─────────────────────────────────────────────
        private readonly ISmtpService _smtpService;
        private readonly INotificationRepository _repository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<QueueProcessor> _logger;
        private readonly string _snsTopicArn;

        // ── Parameterless Constructor (Lambda Runtime) ───────────────
        /// <summary>
        /// Parameterless constructor invoked by the AWS Lambda runtime.
        /// Builds a DI ServiceCollection, registers all dependencies
        /// (AWS SDK clients, repository, SmtpService), and resolves them.
        /// AWS SDK clients use AWS_ENDPOINT_URL for LocalStack compatibility
        /// per AAP §0.7.6.
        ///
        /// Replaces the monolith's pattern:
        ///   new SmtpInternalService() — direct instantiation in ProcessSmtpQueueJob.cs line 14
        ///   SecurityContext.OpenSystemScope() — ProcessSmtpQueueJob.cs line 12
        /// With constructor DI from a per-Lambda ServiceCollection.
        /// </summary>
        public QueueProcessor()
        {
            var services = new ServiceCollection();

            // Structured JSON logging per AAP §0.8.5
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

            // Memory cache for NotificationRepository's SMTP service config caching
            services.AddMemoryCache();

            // AWS_ENDPOINT_URL for LocalStack dual-target (AAP §0.7.6)
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2>(_ =>
                    new Amazon.SimpleEmailV2.AmazonSimpleEmailServiceV2Client(
                        new Amazon.SimpleEmailV2.AmazonSimpleEmailServiceV2Config { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production AWS: default credential + region resolution
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
                services.AddSingleton<Amazon.SimpleEmailV2.IAmazonSimpleEmailServiceV2, Amazon.SimpleEmailV2.AmazonSimpleEmailServiceV2Client>();
            }

            // Application services
            services.AddTransient<INotificationRepository, NotificationRepository>();
            services.AddTransient<ISmtpService, SmtpService>();

            // Build the DI container and resolve dependencies
            var provider = services.BuildServiceProvider();
            _smtpService = provider.GetRequiredService<ISmtpService>();
            _repository = provider.GetRequiredService<INotificationRepository>();
            _snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<QueueProcessor>();

            // SNS topic ARN for domain event publishing
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar) ?? string.Empty;
        }

        // ── Testing Constructor (DI-injected) ────────────────────────
        /// <summary>
        /// Secondary constructor for unit/integration testing. Accepts
        /// pre-built service instances to inject test doubles.
        /// </summary>
        /// <param name="smtpService">SMTP engine service for email processing</param>
        /// <param name="repository">DynamoDB data access for email/SMTP records</param>
        /// <param name="snsClient">SNS client for domain event publishing</param>
        /// <param name="logger">Structured logger</param>
        /// <param name="snsTopicArn">SNS topic ARN (optional, defaults to env var)</param>
        public QueueProcessor(
            ISmtpService smtpService,
            INotificationRepository repository,
            IAmazonSimpleNotificationService snsClient,
            ILogger<QueueProcessor> logger,
            string? snsTopicArn = null)
        {
            _smtpService = smtpService ?? throw new ArgumentNullException(nameof(smtpService));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _snsTopicArn = snsTopicArn
                ?? Environment.GetEnvironmentVariable(SnsTopicArnEnvVar)
                ?? string.Empty;
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleAsync — Main SQS-Triggered Entry Point
        // ═════════════════════════════════════════════════════════════
        //
        // Returns Task<SQSBatchResponse> for partial batch failure
        // reporting: successfully processed messages are removed from
        // the queue, failed messages are returned for retry. After
        // SQS maxReceiveCount, failed messages go to DLQ
        // (notifications-email-queue-dlq per AAP §0.8.5).
        //
        // Two invocation modes:
        //   Mode 1: {"command": "process-queue"} — full queue drain
        //   Mode 2: {"email_id": "...", "action": "send"} — single email
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// SQS-triggered Lambda handler that processes email queue messages.
        /// Supports two modes: scheduled queue drain and individual email send.
        /// Uses partial batch failure reporting via SQSBatchResponse.
        /// </summary>
        /// <param name="sqsEvent">Batch of SQS messages from the notifications-email-queue.</param>
        /// <param name="context">Lambda execution context providing AwsRequestId for correlation-ID.</param>
        /// <returns>SQSBatchResponse listing failed message IDs for retry.</returns>
        public async Task<SQSBatchResponse> HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
        {
            var correlationId = context.AwsRequestId;
            var batchItemFailures = new List<SQSBatchResponse.BatchItemFailure>();

            _logger.LogInformation(
                "QueueProcessor.HandleAsync started. CorrelationId={CorrelationId}, MessageCount={Count}",
                correlationId, sqsEvent?.Records?.Count ?? 0);

            if (sqsEvent?.Records == null || sqsEvent.Records.Count == 0)
            {
                _logger.LogWarning(
                    "QueueProcessor.HandleAsync received empty SQS event. CorrelationId={CorrelationId}",
                    correlationId);
                return new SQSBatchResponse(batchItemFailures);
            }

            var startTime = DateTime.UtcNow;
            int successCount = 0;
            int failureCount = 0;

            foreach (var message in sqsEvent.Records)
            {
                var messageCorrelationId = $"{correlationId}:{message.MessageId}";

                try
                {
                    _logger.LogInformation(
                        "Processing SQS message. MessageId={MessageId}, CorrelationId={CorrelationId}",
                        message.MessageId, messageCorrelationId);

                    await ProcessMessageAsync(message, messageCorrelationId, CancellationToken.None)
                        .ConfigureAwait(false);

                    successCount++;
                }
                catch (Exception ex)
                {
                    failureCount++;

                    _logger.LogError(ex,
                        "Failed to process SQS message. MessageId={MessageId}, CorrelationId={CorrelationId}, Error={Error}",
                        message.MessageId, messageCorrelationId, ex.Message);

                    batchItemFailures.Add(new SQSBatchResponse.BatchItemFailure
                    {
                        ItemIdentifier = message.MessageId
                    });
                }
            }

            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

            _logger.LogInformation(
                "QueueProcessor.HandleAsync completed. CorrelationId={CorrelationId}, " +
                "TotalMessages={Total}, Success={Success}, Failures={Failures}, DurationMs={DurationMs}",
                correlationId, sqsEvent.Records.Count, successCount, failureCount, duration);

            return new SQSBatchResponse(batchItemFailures);
        }

        // ═════════════════════════════════════════════════════════════
        //  ProcessMessageAsync — Route to appropriate processing mode
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Routes an individual SQS message to the correct processing
        /// mode based on its body content.
        /// </summary>
        /// <param name="message">Single SQS message from the batch.</param>
        /// <param name="correlationId">Correlation ID for structured logging.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        private async Task ProcessMessageAsync(
            SQSEvent.SQSMessage message,
            string correlationId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message.Body))
            {
                _logger.LogWarning(
                    "Received SQS message with empty body. MessageId={MessageId}, CorrelationId={CorrelationId}",
                    message.MessageId, correlationId);
                return;
            }

            var body = message.Body;

            // Try to detect the message mode by checking for known fields.
            // Mode 1: Scheduled queue drain — {"command": "process-queue"}
            // Mode 2: Individual email send — {"email_id": "...", "action": "send"}
            //
            // Use case-insensitive property matching for resilience.

            if (IsProcessQueueCommand(body))
            {
                _logger.LogInformation(
                    "Mode 1: Scheduled queue drain. CorrelationId={CorrelationId}",
                    correlationId);

                await ProcessScheduledQueueDrainAsync(correlationId, ct).ConfigureAwait(false);
            }
            else if (TryParseEmailSendRequest(body, out var emailId))
            {
                _logger.LogInformation(
                    "Mode 2: Individual email send. EmailId={EmailId}, CorrelationId={CorrelationId}",
                    emailId, correlationId);

                await ProcessIndividualEmailSendAsync(emailId, correlationId, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Unrecognized SQS message format. MessageId={MessageId}, CorrelationId={CorrelationId}, Body={Body}",
                    message.MessageId, correlationId, body.Length > 500 ? body.Substring(0, 500) : body);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Mode 1: Scheduled Queue Drain
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces the monolith's ProcessSmtpQueueJob.Execute():
        //   using (SecurityContext.OpenSystemScope())
        //   {
        //       new SmtpInternalService().ProcessSmtpQueue();
        //   }
        //
        // Delegates to ISmtpService.ProcessSmtpQueueAsync() which
        // preserves the full queue draining logic from
        // SmtpInternalService.ProcessSmtpQueue (lines 829-878):
        //   1. Acquire SemaphoreSlim (replaces static lock)
        //   2. Loop: fetch batch of 10 pending emails
        //      (priority DESC, scheduled_on ASC)
        //   3. Per email: load SMTP service config
        //      - If null: abort with "SMTP service not found."
        //      - Else: SendEmail (with retry logic)
        //   4. Continue while batch has emails
        //   5. Release semaphore in finally
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Executes a full queue drain by delegating to ISmtpService.ProcessSmtpQueueAsync().
        /// This mode is triggered by an EventBridge/CloudWatch scheduled rule that sends
        /// a "process-queue" command to the SQS queue at regular intervals.
        /// </summary>
        /// <param name="correlationId">Correlation ID for structured logging.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        private async Task ProcessScheduledQueueDrainAsync(string correlationId, CancellationToken ct)
        {
            var startTime = DateTime.UtcNow;

            _logger.LogInformation(
                "Starting scheduled queue drain. CorrelationId={CorrelationId}, StartTime={StartTime}",
                correlationId, startTime.ToString("O"));

            try
            {
                await _smtpService.ProcessSmtpQueueAsync(ct).ConfigureAwait(false);

                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                _logger.LogInformation(
                    "Scheduled queue drain completed successfully. CorrelationId={CorrelationId}, DurationMs={DurationMs}",
                    correlationId, duration);
            }
            catch (Exception ex)
            {
                var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

                _logger.LogError(ex,
                    "Scheduled queue drain failed. CorrelationId={CorrelationId}, DurationMs={DurationMs}, Error={Error}",
                    correlationId, duration, ex.Message);

                throw; // Re-throw so the message goes back to SQS for retry → DLQ
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Mode 2: Individual Email Send
        // ═════════════════════════════════════════════════════════════
        //
        // Handles individual email send requests queued for async
        // processing. Replicates per-email processing from
        // SmtpInternalService.ProcessSmtpQueue (lines 851-866):
        //
        //   1. Load email by ID
        //   2. Idempotency check: skip if already processed
        //   3. Load SMTP service config by email.ServiceId
        //   4. If null: abort with "SMTP service not found."
        //      (source lines 854-860)
        //   5. If exists: SendEmail (source line 864)
        //   6. Publish domain event based on outcome
        //
        // Send + retry logic (SmtpInternalService.SendEmail lines 689-827):
        //   Success: SentOn=UtcNow, Status=Sent, ScheduledOn=null, ServerError=null
        //   Failure: ServerError=ex.Message, RetriesCount++
        //     If RetriesCount >= MaxRetriesCount: Status=Aborted, ScheduledOn=null
        //     Else: ScheduledOn=UtcNow.AddMinutes(RetryWaitMinutes), Status=Pending
        //   Always: SaveEmail in finally
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Processes a single email send request from the SQS queue.
        /// Implements full idempotency checking per AAP §0.8.5:
        /// if the email is no longer in Pending status, it has already
        /// been processed and is skipped to prevent duplicate sends.
        /// </summary>
        /// <param name="emailId">The unique ID of the email to send.</param>
        /// <param name="correlationId">Correlation ID for structured logging.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        private async Task ProcessIndividualEmailSendAsync(
            Guid emailId,
            string correlationId,
            CancellationToken ct)
        {
            // Step 1: Load email — maps to SmtpInternalService.GetEmail (source lines 674-681)
            var email = await _repository.GetEmailByIdAsync(emailId, ct).ConfigureAwait(false);

            if (email == null)
            {
                _logger.LogWarning(
                    "Email not found. EmailId={EmailId}, CorrelationId={CorrelationId}",
                    emailId, correlationId);
                return; // Nothing to process — message consumed silently
            }

            // Step 2: Idempotency check — if not Pending, already processed
            // Per AAP §0.8.5: "All event consumers MUST be idempotent"
            if (email.Status != EmailStatus.Pending)
            {
                _logger.LogInformation(
                    "Email already processed (idempotency skip). EmailId={EmailId}, " +
                    "CurrentStatus={Status}, CorrelationId={CorrelationId}",
                    emailId, email.Status, correlationId);
                return; // Consumed without error — already processed
            }

            _logger.LogInformation(
                "Processing email. EmailId={EmailId}, ServiceId={ServiceId}, " +
                "RecipientsCount={RecipientsCount}, CorrelationId={CorrelationId}",
                emailId, email.ServiceId, email.Recipients?.Count ?? 0, correlationId);

            // Step 3: Load SMTP service config — maps to per-email service lookup
            //         in SmtpInternalService.ProcessSmtpQueue (source lines 852-853)
            SmtpServiceConfig? service = null;

            try
            {
                service = await _repository.GetSmtpServiceByIdAsync(email.ServiceId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to load SMTP service config. EmailId={EmailId}, ServiceId={ServiceId}, " +
                    "CorrelationId={CorrelationId}",
                    emailId, email.ServiceId, correlationId);
                throw; // Let the batch failure handler deal with retry
            }

            // Step 4: Service null check — abort pattern from source lines 854-860
            if (service == null)
            {
                _logger.LogWarning(
                    "SMTP service not found, aborting email. EmailId={EmailId}, ServiceId={ServiceId}, " +
                    "CorrelationId={CorrelationId}",
                    emailId, email.ServiceId, correlationId);

                // Exact abort pattern from SmtpInternalService.cs lines 854-860:
                //   email.Status = EmailStatus.Aborted;
                //   email.ServerError = "SMTP service not found.";
                //   email.ScheduledOn = null;
                //   SaveEmail(email);
                email.Status = EmailStatus.Aborted;
                email.ServerError = SmtpServiceNotFoundError;
                email.ScheduledOn = null;

                await _repository.SaveEmailAsync(email, ct).ConfigureAwait(false);

                // Publish domain event: notifications.email.failed
                await PublishDomainEventAsync(
                    "failed", email, correlationId, SmtpServiceNotFoundError, ct)
                    .ConfigureAwait(false);

                return;
            }

            // Step 5: Send email — delegates to ISmtpService.SendEmailAsync(Email, SmtpServiceConfig)
            // which preserves the full send + retry logic from SmtpInternalService.SendEmail
            // (source lines 689-827) including:
            //   - Success: SentOn=UtcNow, Status=Sent, ScheduledOn=null, ServerError=null
            //   - Failure: ServerError=ex.Message, RetriesCount++
            //     If RetriesCount >= MaxRetriesCount: Status=Aborted, ScheduledOn=null
            //     Else: ScheduledOn=AddMinutes(RetryWaitMinutes), Status=Pending
            //   - Always: SaveEmail in finally
            try
            {
                await _smtpService.SendEmailAsync(email, service, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SendEmailAsync threw an unhandled exception. EmailId={EmailId}, " +
                    "CorrelationId={CorrelationId}, Error={Error}",
                    emailId, correlationId, ex.Message);

                // The SmtpService.SendEmailAsync should handle its own try/catch/save,
                // but if an unexpected exception escapes, re-throw for SQS retry.
                throw;
            }

            // Step 6: Reload email to get the final status after SmtpService processing
            // (SmtpService saves the email with updated status in its finally block)
            var updatedEmail = await _repository.GetEmailByIdAsync(emailId, ct).ConfigureAwait(false);

            if (updatedEmail != null)
            {
                // Publish domain event based on the post-send status
                string action;
                string? errorMessage = null;

                switch (updatedEmail.Status)
                {
                    case EmailStatus.Sent:
                        action = "sent";
                        break;

                    case EmailStatus.Aborted:
                        action = "failed";
                        errorMessage = updatedEmail.ServerError;
                        break;

                    case EmailStatus.Pending:
                        // Still pending = rescheduled for retry
                        action = "rescheduled";
                        errorMessage = updatedEmail.ServerError;
                        break;

                    default:
                        action = "processed";
                        break;
                }

                await PublishDomainEventAsync(action, updatedEmail, correlationId, errorMessage, ct)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Email processing completed. EmailId={EmailId}, FinalStatus={Status}, " +
                    "Action={Action}, CorrelationId={CorrelationId}",
                    emailId, updatedEmail.Status, action, correlationId);
            }
            else
            {
                _logger.LogWarning(
                    "Email not found after send processing. EmailId={EmailId}, CorrelationId={CorrelationId}",
                    emailId, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Domain Event Publishing via SNS
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces the monolith's synchronous in-process
        // HookManager/RecordHookManager post-CRUD hooks per AAP §0.7.2.
        //
        // Event naming: {domain}.{entity}.{action} per AAP §0.8.5
        //   notifications.email.sent
        //   notifications.email.failed
        //   notifications.email.rescheduled
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Publishes a domain event to the SNS notifications topic after
        /// an email state transition. Event payload includes email_id,
        /// service_id, status, recipients_count, and timestamp for
        /// downstream consumers (e.g., Reporting service read-model updates).
        /// </summary>
        /// <param name="action">Event action: "sent", "failed", or "rescheduled".</param>
        /// <param name="email">The email entity with current state.</param>
        /// <param name="correlationId">Correlation ID for tracing.</param>
        /// <param name="errorMessage">Optional error message for failed/rescheduled events.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task PublishDomainEventAsync(
            string action,
            Email email,
            string correlationId,
            string? errorMessage,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_snsTopicArn))
            {
                _logger.LogWarning(
                    "SNS topic ARN not configured; skipping domain event. " +
                    "Action={Action}, EmailId={EmailId}, CorrelationId={CorrelationId}",
                    action, email.Id, correlationId);
                return;
            }

            var eventSubject = $"notifications.email.{action}";

            var domainEvent = new EmailDomainEvent
            {
                EventId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow.ToString("O"),
                Action = action,
                EmailId = email.Id.ToString(),
                ServiceId = email.ServiceId.ToString(),
                Status = email.Status.ToString(),
                RecipientsCount = email.Recipients?.Count ?? 0,
                CorrelationId = correlationId,
                ErrorMessage = errorMessage
            };

            var jsonPayload = JsonSerializer.Serialize(
                domainEvent, QueueProcessorJsonContext.Default.EmailDomainEvent);

            try
            {
                var publishRequest = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Subject = eventSubject,
                    Message = jsonPayload
                };

                await _snsClient.PublishAsync(publishRequest, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Domain event published. Subject={Subject}, EmailId={EmailId}, " +
                    "CorrelationId={CorrelationId}",
                    eventSubject, email.Id, correlationId);
            }
            catch (Exception ex)
            {
                // Domain event publishing failures should NOT fail the email processing.
                // The email was already sent/aborted/rescheduled — the state transition
                // is committed. Log the error for monitoring and alerting.
                _logger.LogError(ex,
                    "Failed to publish domain event. Subject={Subject}, EmailId={EmailId}, " +
                    "CorrelationId={CorrelationId}, Error={Error}",
                    eventSubject, email.Id, correlationId, ex.Message);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Message Parsing Helpers
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Determines if the SQS message body represents a "process-queue"
        /// scheduled command (Mode 1).
        /// </summary>
        /// <param name="body">Raw SQS message body string.</param>
        /// <returns>True if the message is a process-queue command.</returns>
        private bool IsProcessQueueCommand(string body)
        {
            try
            {
                var command = JsonSerializer.Deserialize(
                    body, QueueProcessorJsonContext.Default.QueueProcessorCommand);

                return command != null
                    && string.Equals(command.Command, ProcessQueueCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Attempts to parse an individual email send request (Mode 2) from
        /// the SQS message body. Validates that the email_id is a valid GUID
        /// and the action is "send".
        /// </summary>
        /// <param name="body">Raw SQS message body string.</param>
        /// <param name="emailId">Parsed email ID if successful.</param>
        /// <returns>True if the message is a valid email send request.</returns>
        private bool TryParseEmailSendRequest(string body, out Guid emailId)
        {
            emailId = Guid.Empty;

            try
            {
                var request = JsonSerializer.Deserialize(
                    body, QueueProcessorJsonContext.Default.EmailSendRequest);

                if (request == null || string.IsNullOrEmpty(request.EmailId))
                {
                    return false;
                }

                if (!string.Equals(request.Action, SendAction, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return Guid.TryParse(request.EmailId, out emailId);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
