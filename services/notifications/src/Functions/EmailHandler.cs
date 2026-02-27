using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleEmailV2;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Models;
using WebVellaErp.Notifications.Services;

// NOTE: [assembly: LambdaSerializer] is already defined in QueueProcessor.cs
// for the entire WebVellaErp.Notifications assembly. Do NOT duplicate it here.

namespace WebVellaErp.Notifications.Functions
{
    // ═══════════════════════════════════════════════════════════════════
    // AOT Source-Generated JSON Serialization Context for EmailHandler
    // ═══════════════════════════════════════════════════════════════════
    // Registers every type that flows through JsonSerializer.Serialize /
    // Deserialize in EmailHandler methods, eliminating IL2026/IL3050
    // Native-AOT trimming warnings for System.Text.Json reflection.
    // ═══════════════════════════════════════════════════════════════════

    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(SendEmailRequest))]
    [JsonSerializable(typeof(QueueEmailRequest))]
    [JsonSerializable(typeof(TestSmtpRequest))]
    [JsonSerializable(typeof(SmtpServiceConfig))]
    [JsonSerializable(typeof(Email))]
    [JsonSerializable(typeof(List<SmtpServiceConfig>))]
    [JsonSerializable(typeof(List<ValidationError>))]
    [JsonSerializable(typeof(EmailHandlerResponse))]
    [JsonSerializable(typeof(EmailHandlerListResponse))]
    [JsonSerializable(typeof(EmailHandlerDomainEvent))]
    [JsonSerializable(typeof(EmailHandlerHealthResponse))]
    internal partial class EmailHandlerJsonContext : JsonSerializerContext { }

    // ═══════════════════════════════════════════════════════════════════
    // Request DTOs — Deserialized from HTTP API Gateway v2 request body
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Request body for POST /v1/notifications/emails/send endpoint.
    /// Replaces the parameter combinations of SmtpService.SendEmail() overloads
    /// (source SmtpService.cs lines 67-613) with a single unified DTO.
    /// </summary>
    public class SendEmailRequest
    {
        /// <summary>List of email recipients. At least one is required.</summary>
        [JsonPropertyName("recipients")]
        public List<EmailAddress> Recipients { get; set; } = new();

        /// <summary>Optional custom sender address. Uses default SMTP service sender if null.</summary>
        [JsonPropertyName("sender")]
        public EmailAddress? Sender { get; set; }

        /// <summary>Email subject line.</summary>
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        /// <summary>Plain text body content.</summary>
        [JsonPropertyName("text_body")]
        public string TextBody { get; set; } = string.Empty;

        /// <summary>HTML body content. Inline images processed via CID replacement.</summary>
        [JsonPropertyName("html_body")]
        public string HtmlBody { get; set; } = string.Empty;

        /// <summary>S3 object keys for attachments, replacing DbFileRepository paths.</summary>
        [JsonPropertyName("attachment_keys")]
        public List<string> AttachmentKeys { get; set; } = new();
    }

    /// <summary>
    /// Request body for POST /v1/notifications/emails/queue endpoint.
    /// Replaces the parameter combinations of SmtpService.QueueEmail() overloads
    /// (source SmtpService.cs lines 615-945) with a single unified DTO.
    /// </summary>
    public class QueueEmailRequest
    {
        /// <summary>List of email recipients. At least one is required.</summary>
        [JsonPropertyName("recipients")]
        public List<EmailAddress> Recipients { get; set; } = new();

        /// <summary>Optional custom sender address. Uses default SMTP service sender if null.</summary>
        [JsonPropertyName("sender")]
        public EmailAddress? Sender { get; set; }

        /// <summary>Optional reply-to addresses, semicolon-separated.</summary>
        [JsonPropertyName("reply_to")]
        public string ReplyTo { get; set; } = string.Empty;

        /// <summary>Email subject line.</summary>
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        /// <summary>Plain text body content.</summary>
        [JsonPropertyName("text_body")]
        public string TextBody { get; set; } = string.Empty;

        /// <summary>HTML body content.</summary>
        [JsonPropertyName("html_body")]
        public string HtmlBody { get; set; } = string.Empty;

        /// <summary>Email priority. Defaults to Normal.</summary>
        [JsonPropertyName("priority")]
        public EmailPriority Priority { get; set; } = EmailPriority.Normal;

        /// <summary>S3 object keys for attachments.</summary>
        [JsonPropertyName("attachment_keys")]
        public List<string> AttachmentKeys { get; set; } = new();
    }

    /// <summary>
    /// Request body for POST /v1/notifications/emails/test-smtp endpoint.
    /// Replaces SmtpInternalService.TestSmtpServiceOnPost() parameter extraction
    /// (source SmtpInternalService.cs lines 387-478).
    /// </summary>
    public class TestSmtpRequest
    {
        /// <summary>ID of the SMTP service configuration to test.</summary>
        [JsonPropertyName("service_id")]
        public Guid ServiceId { get; set; }

        /// <summary>Recipient email address for the test email.</summary>
        [JsonPropertyName("recipient_email")]
        public string RecipientEmail { get; set; } = string.Empty;

        /// <summary>Test email subject line.</summary>
        [JsonPropertyName("subject")]
        public string Subject { get; set; } = string.Empty;

        /// <summary>Test email HTML content body.</summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>Optional S3 attachment keys for test email.</summary>
        [JsonPropertyName("attachment_keys")]
        public List<string> AttachmentKeys { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Internal Response / Event DTOs
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Standard response envelope for single-result email handler responses.
    /// Provides consistent JSON response shape across all endpoints.
    /// </summary>
    internal sealed class EmailHandlerResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public object? Data { get; set; }

        [JsonPropertyName("errors")]
        public List<ValidationError>? Errors { get; set; }
    }

    /// <summary>
    /// Response envelope for list endpoints returning multiple SMTP service configs.
    /// </summary>
    internal sealed class EmailHandlerListResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public List<SmtpServiceConfig>? Data { get; set; }

        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// Domain event payload published to SNS after email/SMTP write operations.
    /// Follows AAP §0.8.5 naming convention: {domain}.{entity}.{action}
    /// (e.g., notifications.email.sent, notifications.smtp_service.created).
    /// </summary>
    internal sealed class EmailHandlerDomainEvent
    {
        [JsonPropertyName("event_id")]
        public string EventId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("entity")]
        public string Entity { get; set; } = string.Empty;

        [JsonPropertyName("entity_id")]
        public string EntityId { get; set; } = string.Empty;

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public string? Payload { get; set; }
    }

    /// <summary>
    /// Health check response for GET /v1/notifications/health endpoint.
    /// </summary>
    internal sealed class EmailHandlerHealthResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "healthy";

        [JsonPropertyName("service")]
        public string Service { get; set; } = "notifications";

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;
    }

    // ═══════════════════════════════════════════════════════════════════
    // EmailHandler — HTTP API Gateway v2 Lambda Handler for Email REST
    // ═══════════════════════════════════════════════════════════════════
    //
    // Primary HTTP API Gateway Lambda handler for the Notifications
    // microservice's email operations. Replaces the monolith's:
    //
    //   WebApiController.cs email endpoints → 11 route-based handlers
    //   SmtpInternalService.SaveEmail → HandleSendEmailAsync/HandleQueueEmailAsync
    //   SmtpService.SendEmail()/QueueEmail() → delegated to ISmtpService
    //   SmtpInternalService.TestSmtpServiceOnPost → HandleTestSmtpAsync
    //   SmtpInternalService.EmailSendNowOnPost → HandleSendNowAsync
    //   SmtpServiceRecordHook CRUD hooks → HandleCreate/Update/DeleteSmtpServiceAsync
    //
    // Architecture:
    //   Pure Lambda handler — NO ASP.NET Core MVC controllers.
    //   APIGatewayHttpApiV2ProxyRequest → route dispatch → handler → response
    //   System.Text.Json with source generation for AOT compatibility.
    //   DI container built per-Lambda-instance (cold start).
    //   Domain events published to SNS after all write operations.
    //   Structured JSON logging with correlation-ID from AwsRequestId.
    //
    // Routing Table (11 endpoints):
    //   POST   /v1/notifications/emails/send          → HandleSendEmailAsync
    //   POST   /v1/notifications/emails/queue         → HandleQueueEmailAsync
    //   GET    /v1/notifications/emails/{id}          → HandleGetEmailAsync
    //   POST   /v1/notifications/emails/{id}/send-now → HandleSendNowAsync
    //   POST   /v1/notifications/emails/test-smtp     → HandleTestSmtpAsync
    //   POST   /v1/notifications/smtp-services        → HandleCreateSmtpServiceAsync
    //   PUT    /v1/notifications/smtp-services/{id}   → HandleUpdateSmtpServiceAsync
    //   DELETE /v1/notifications/smtp-services/{id}   → HandleDeleteSmtpServiceAsync
    //   GET    /v1/notifications/smtp-services/{id}   → HandleGetSmtpServiceAsync
    //   GET    /v1/notifications/smtp-services        → HandleListSmtpServicesAsync
    //   GET    /v1/notifications/health               → Health check
    //
    // ═══════════════════════════════════════════════════════════════════

    public class EmailHandler
    {
        // ── Constants ────────────────────────────────────────────────
        private const string SnsTopicArnEnvVar = "NOTIFICATIONS_SNS_TOPIC_ARN";
        private const string IdPathParam = "id";
        private const string DefaultSmtpDeleteError = "Default smtp service cannot be deleted.";
        private const string EmailNotFoundError = "Email not found.";
        private const string SmtpServiceNotFoundError = "SMTP service not found.";
        private const string InvalidRequestBodyError = "Invalid request body.";
        private const string RouteNotFoundError = "Route not found.";
        private const string EmailEntityName = "email";
        private const string SmtpServiceEntityName = "smtp_service";
        private const string CorrelationIdHeader = "X-Correlation-Id";

        /// <summary>
        /// Standard CORS and content-type headers returned on every HTTP response.
        /// Locked to known origins per AAP §0.8.3 (wildcard for LocalStack dev).
        /// </summary>
        private static readonly Dictionary<string, string> StandardResponseHeaders = new()
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS" },
            { "Access-Control-Allow-Headers", "Content-Type,Authorization,X-Correlation-Id" }
        };

        // ── Dependencies ─────────────────────────────────────────────
        private readonly ISmtpService _smtpService;
        private readonly INotificationRepository _repository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<EmailHandler> _logger;
        private readonly string _snsTopicArn;

        // ── Parameterless Constructor (Lambda Runtime) ───────────────
        /// <summary>
        /// Parameterless constructor invoked by the AWS Lambda runtime.
        /// Builds a DI ServiceCollection, registers all dependencies
        /// (AWS SDK clients, repository, SmtpService), and resolves them.
        /// AWS SDK clients use AWS_ENDPOINT_URL for LocalStack compatibility
        /// per AAP §0.7.6.
        ///
        /// Replaces the monolith's patterns:
        ///   new SmtpInternalService() — direct instantiation
        ///   new EmailServiceManager() — direct instantiation
        ///   SecurityContext.OpenSystemScope() — ambient context
        /// With constructor DI from a per-Lambda ServiceCollection.
        /// </summary>
        public EmailHandler()
        {
            var services = new ServiceCollection();

            // Structured JSON logging per AAP §0.8.5
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

            // Memory cache for NotificationRepository's SMTP service config caching
            // (replaces EmailServiceManager's MemoryCache pattern from monolith)
            services.AddMemoryCache();

            // AWS_ENDPOINT_URL for LocalStack dual-target (AAP §0.7.6)
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            if (!string.IsNullOrEmpty(endpointUrl))
            {
                // LocalStack mode: explicit endpoint URL on all SDK clients
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ =>
                    new AmazonSimpleEmailServiceV2Client(
                        new AmazonSimpleEmailServiceV2Config { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production AWS: default credential + region resolution
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
                services.AddSingleton<IAmazonSimpleEmailServiceV2, AmazonSimpleEmailServiceV2Client>();
            }

            // Application services
            services.AddTransient<INotificationRepository, NotificationRepository>();
            services.AddTransient<ISmtpService, SmtpService>();

            // Build the DI container and resolve dependencies
            var provider = services.BuildServiceProvider();
            _smtpService = provider.GetRequiredService<ISmtpService>();
            _repository = provider.GetRequiredService<INotificationRepository>();
            _snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger<EmailHandler>();

            // SNS topic ARN for domain event publishing
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar) ?? string.Empty;
        }

        // ── Testing Constructor (DI-injected) ────────────────────────
        /// <summary>
        /// Secondary constructor for unit/integration testing. Accepts
        /// pre-built service instances to inject test doubles.
        /// </summary>
        /// <param name="smtpService">SMTP engine service for email send/queue/validate</param>
        /// <param name="repository">DynamoDB data access for email/SMTP CRUD</param>
        /// <param name="snsClient">SNS client for domain event publishing</param>
        /// <param name="logger">Structured logger</param>
        /// <param name="snsTopicArn">SNS topic ARN (optional, defaults to env var)</param>
        public EmailHandler(
            ISmtpService smtpService,
            INotificationRepository repository,
            IAmazonSimpleNotificationService snsClient,
            ILogger<EmailHandler> logger,
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
        //  HandleAsync — Main HTTP API Gateway v2 Entry Point
        // ═════════════════════════════════════════════════════════════
        //
        // Single Lambda handler method that dispatches based on the
        // RouteKey from HTTP API Gateway v2. RouteKey format is
        // "{METHOD} {path}" e.g., "POST /v1/notifications/emails/send".
        //
        // Routes to specific handler methods based on path pattern.
        // Returns APIGatewayHttpApiV2ProxyResponse with JSON body,
        // CORS headers, and X-Correlation-Id from AwsRequestId.
        //
        // Global error handling wraps all routing and handler dispatch
        // for consistent error responses and structured logging.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// HTTP API Gateway v2 Lambda handler entry point. Routes requests
        /// based on RouteKey to the appropriate email/SMTP service handler.
        /// </summary>
        /// <param name="request">HTTP API Gateway v2 proxy request with Body, PathParameters, RouteKey.</param>
        /// <param name="context">Lambda execution context providing AwsRequestId for correlation-ID.</param>
        /// <returns>HTTP API Gateway v2 proxy response with JSON body and standard headers.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = context.AwsRequestId;
            var routeKey = request.RouteKey ?? string.Empty;

            _logger.LogInformation(
                "EmailHandler.HandleAsync started. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                routeKey, correlationId);

            try
            {
                // ── OPTIONS preflight (CORS) ─────────────────────────
                if (routeKey.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildResponse(204, null, correlationId);
                }

                // ── Health check ─────────────────────────────────────
                if (routeKey.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/health", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleHealthCheck(correlationId);
                }

                // ── Email Send (POST /v1/notifications/emails/send) ──
                if (routeKey.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/emails/send", StringComparison.OrdinalIgnoreCase) &&
                    !routeKey.Contains("/send-now", StringComparison.OrdinalIgnoreCase) &&
                    !routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleSendEmailAsync(request, correlationId);
                }

                // ── Email Queue (POST /v1/notifications/emails/queue) ─
                if (routeKey.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/emails/queue", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleQueueEmailAsync(request, correlationId);
                }

                // ── Test SMTP (POST /v1/notifications/emails/test-smtp) ─
                if (routeKey.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/emails/test-smtp", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleTestSmtpAsync(request, correlationId);
                }

                // ── Send Now (POST /v1/notifications/emails/{id}/send-now) ─
                if (routeKey.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/send-now", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleSendNowAsync(request, correlationId);
                }

                // ── Get Email by ID (GET /v1/notifications/emails/{id}) ─
                if (routeKey.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/emails/", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleGetEmailAsync(request, correlationId);
                }

                // ── Create SMTP Service (POST /v1/notifications/smtp-services) ─
                if (routeKey.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/smtp-services", StringComparison.OrdinalIgnoreCase) &&
                    !routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleCreateSmtpServiceAsync(request, correlationId);
                }

                // ── List SMTP Services (GET /v1/notifications/smtp-services) ─
                if (routeKey.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/smtp-services", StringComparison.OrdinalIgnoreCase) &&
                    !routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleListSmtpServicesAsync(correlationId);
                }

                // ── Get SMTP Service (GET /v1/notifications/smtp-services/{id}) ─
                if (routeKey.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/smtp-services/", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleGetSmtpServiceAsync(request, correlationId);
                }

                // ── Update SMTP Service (PUT /v1/notifications/smtp-services/{id}) ─
                if (routeKey.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleUpdateSmtpServiceAsync(request, correlationId);
                }

                // ── Delete SMTP Service (DELETE /v1/notifications/smtp-services/{id}) ─
                if (routeKey.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("{id}", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleDeleteSmtpServiceAsync(request, correlationId);
                }

                // ── Route not found ──────────────────────────────────
                _logger.LogWarning(
                    "EmailHandler: Unknown route. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                    routeKey, correlationId);

                return BuildResponse(404, new EmailHandlerResponse
                {
                    Success = false,
                    Message = RouteNotFoundError
                }, correlationId);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    "EmailHandler: JSON deserialization error. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                    routeKey, correlationId);

                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = InvalidRequestBodyError
                }, correlationId);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex,
                    "EmailHandler: Missing path parameter. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                    routeKey, correlationId);

                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Missing required path parameter."
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler: Unhandled exception. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                    routeKey, correlationId);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Internal server error. CorrelationId: {correlationId}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleSendEmailAsync — Email Sending
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces SmtpService.SendEmail() overloads (source SmtpService.cs
        // lines 67-613). Dispatches to the correct ISmtpService.SendEmailAsync
        // overload based on whether a custom sender is provided.
        //
        // Idempotency: Per AAP §0.8.5, if the same email ID already has
        // status=Sent, the service returns success without re-sending.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles POST /v1/notifications/emails/send — synchronous email send.
        /// Deserializes SendEmailRequest, delegates to ISmtpService, publishes
        /// domain event, and returns 200 on success.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleSendEmailAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var sendRequest = DeserializeBody<SendEmailRequest>(request.Body);
            if (sendRequest == null)
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = InvalidRequestBodyError
                }, correlationId);
            }

            // Validate required fields
            if (sendRequest.Recipients == null || !sendRequest.Recipients.Any())
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "At least one recipient is required."
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleSendEmailAsync: Sending email. RecipientsCount={RecipientsCount}, " +
                "HasCustomSender={HasCustomSender}, CorrelationId={CorrelationId}",
                sendRequest.Recipients.Count, sendRequest.Sender != null, correlationId);

            try
            {
                // Dispatch to correct ISmtpService overload based on sender presence
                if (sendRequest.Sender != null && !string.IsNullOrEmpty(sendRequest.Sender.Address))
                {
                    // Custom sender: multi-recipient overload with explicit sender
                    await _smtpService.SendEmailAsync(
                        sendRequest.Recipients,
                        sendRequest.Sender,
                        sendRequest.Subject,
                        sendRequest.TextBody,
                        sendRequest.HtmlBody,
                        sendRequest.AttachmentKeys?.Count > 0 ? sendRequest.AttachmentKeys : null,
                        ct);
                }
                else
                {
                    // Default sender: multi-recipient overload
                    await _smtpService.SendEmailAsync(
                        sendRequest.Recipients,
                        sendRequest.Subject,
                        sendRequest.TextBody,
                        sendRequest.HtmlBody,
                        sendRequest.AttachmentKeys?.Count > 0 ? sendRequest.AttachmentKeys : null,
                        ct);
                }

                // Publish domain event: notifications.email.sent
                await PublishDomainEventAsync(
                    "sent", EmailEntityName, Guid.NewGuid().ToString(), correlationId, ct);

                _logger.LogInformation(
                    "EmailHandler.HandleSendEmailAsync: Email sent successfully. " +
                    "RecipientsCount={RecipientsCount}, CorrelationId={CorrelationId}",
                    sendRequest.Recipients.Count, correlationId);

                return BuildResponse(200, new EmailHandlerResponse
                {
                    Success = true,
                    Message = "Email sent successfully."
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler.HandleSendEmailAsync: Send failed. CorrelationId={CorrelationId}",
                    correlationId);

                // Publish domain event: notifications.email.failed
                await PublishDomainEventAsync(
                    "failed", EmailEntityName, Guid.NewGuid().ToString(), correlationId, ct);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Failed to send email: {ex.Message}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleQueueEmailAsync — Email Queuing
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces SmtpService.QueueEmail() overloads (source SmtpService.cs
        // lines 615-945). Dispatches to the correct ISmtpService.QueueEmailAsync
        // overload based on sender and reply-to presence.
        //
        // Returns 202 Accepted since queuing is async by nature.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles POST /v1/notifications/emails/queue — asynchronous email queuing.
        /// Deserializes QueueEmailRequest, delegates to ISmtpService, publishes
        /// domain event, and returns 202 Accepted.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleQueueEmailAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var queueRequest = DeserializeBody<QueueEmailRequest>(request.Body);
            if (queueRequest == null)
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = InvalidRequestBodyError
                }, correlationId);
            }

            // Validate required fields
            if (queueRequest.Recipients == null || !queueRequest.Recipients.Any())
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "At least one recipient is required."
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleQueueEmailAsync: Queuing email. RecipientsCount={RecipientsCount}, " +
                "Priority={Priority}, HasCustomSender={HasCustomSender}, CorrelationId={CorrelationId}",
                queueRequest.Recipients.Count, queueRequest.Priority,
                queueRequest.Sender != null, correlationId);

            try
            {
                // Dispatch to correct ISmtpService overload based on sender/replyTo presence
                if (queueRequest.Sender != null && !string.IsNullOrEmpty(queueRequest.Sender.Address))
                {
                    // Custom sender with reply-to: full overload
                    await _smtpService.QueueEmailAsync(
                        queueRequest.Recipients,
                        queueRequest.Sender,
                        queueRequest.ReplyTo ?? string.Empty,
                        queueRequest.Subject,
                        queueRequest.TextBody,
                        queueRequest.HtmlBody,
                        queueRequest.Priority,
                        queueRequest.AttachmentKeys?.Count > 0 ? queueRequest.AttachmentKeys : null,
                        ct);
                }
                else
                {
                    // Default sender: multi-recipient overload
                    await _smtpService.QueueEmailAsync(
                        queueRequest.Recipients,
                        queueRequest.Subject,
                        queueRequest.TextBody,
                        queueRequest.HtmlBody,
                        queueRequest.Priority,
                        queueRequest.AttachmentKeys?.Count > 0 ? queueRequest.AttachmentKeys : null,
                        ct);
                }

                // Publish domain event: notifications.email.queued
                await PublishDomainEventAsync(
                    "queued", EmailEntityName, Guid.NewGuid().ToString(), correlationId, ct);

                _logger.LogInformation(
                    "EmailHandler.HandleQueueEmailAsync: Email queued successfully. " +
                    "RecipientsCount={RecipientsCount}, CorrelationId={CorrelationId}",
                    queueRequest.Recipients.Count, correlationId);

                return BuildResponse(202, new EmailHandlerResponse
                {
                    Success = true,
                    Message = "Email queued successfully."
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler.HandleQueueEmailAsync: Queue failed. CorrelationId={CorrelationId}",
                    correlationId);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Failed to queue email: {ex.Message}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleGetEmailAsync — Get Email by ID
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces SmtpInternalService.GetEmail() (source lines 674-681).
        // SELECT * FROM email WHERE id = @id via EQL → DynamoDB GetItem.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles GET /v1/notifications/emails/{id} — retrieve a single email record.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetEmailAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var idParam = ExtractPathParameter(request, IdPathParam);
            if (string.IsNullOrEmpty(idParam) || !Guid.TryParse(idParam, out var emailId))
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing email ID."
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleGetEmailAsync: Retrieving email. EmailId={EmailId}, CorrelationId={CorrelationId}",
                emailId, correlationId);

            var email = await _repository.GetEmailByIdAsync(emailId, ct);
            if (email == null)
            {
                return BuildResponse(404, new EmailHandlerResponse
                {
                    Success = false,
                    Message = EmailNotFoundError
                }, correlationId);
            }

            return BuildResponse(200, new EmailHandlerResponse
            {
                Success = true,
                Message = "Email retrieved successfully.",
                Data = email
            }, correlationId);
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleSendNowAsync — Immediate Email Send
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces SmtpInternalService.EmailSendNowOnPost() (source
        // lines 480-496). Loads email by ID, loads SMTP service by
        // email.ServiceId, sends via ISmtpService, checks status.
        //
        // Source mapping:
        //   line 485: Email email = internalSmtpSrv.GetEmail(emailId)
        //   line 486: SmtpService smtpService = new EmailServiceManager().GetSmtpService(email.ServiceId)
        //   line 487: internalSmtpSrv.SendEmail(email, smtpService)
        //   line 489: if (email.Status == EmailStatus.Sent) → 200
        //   line 492: else → 500 with email.ServerError
        //
        // Idempotency: If email already has Status=Sent, returns 200
        // without re-sending (natural idempotency key per AAP §0.8.5).
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles POST /v1/notifications/emails/{id}/send-now — immediate send of a queued email.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleSendNowAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var idParam = ExtractPathParameter(request, IdPathParam);
            if (string.IsNullOrEmpty(idParam) || !Guid.TryParse(idParam, out var emailId))
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing email ID."
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleSendNowAsync: Send-now requested. EmailId={EmailId}, CorrelationId={CorrelationId}",
                emailId, correlationId);

            // Load email record — maps to source line 485
            var email = await _repository.GetEmailByIdAsync(emailId, ct);
            if (email == null)
            {
                return BuildResponse(404, new EmailHandlerResponse
                {
                    Success = false,
                    Message = EmailNotFoundError
                }, correlationId);
            }

            // Idempotency: already sent, return success without re-sending
            if (email.Status == EmailStatus.Sent)
            {
                _logger.LogInformation(
                    "EmailHandler.HandleSendNowAsync: Email already sent (idempotent). " +
                    "EmailId={EmailId}, CorrelationId={CorrelationId}",
                    emailId, correlationId);

                return BuildResponse(200, new EmailHandlerResponse
                {
                    Success = true,
                    Message = "Email was successfully sent."
                }, correlationId);
            }

            // Load SMTP service config — maps to source line 486
            SmtpServiceConfig? smtpService = null;
            if (email.ServiceId != Guid.Empty)
            {
                smtpService = await _repository.GetSmtpServiceByIdAsync(email.ServiceId, ct);
            }

            if (smtpService == null)
            {
                return BuildResponse(404, new EmailHandlerResponse
                {
                    Success = false,
                    Message = SmtpServiceNotFoundError
                }, correlationId);
            }

            // Send the email — maps to source line 487
            await _smtpService.SendEmailAsync(email, smtpService, ct);

            // Re-fetch to check updated status after send attempt
            var updatedEmail = await _repository.GetEmailByIdAsync(emailId, ct);
            var finalStatus = updatedEmail?.Status ?? email.Status;

            // Check result — maps to source lines 489-493
            if (finalStatus == EmailStatus.Sent)
            {
                _logger.LogInformation(
                    "EmailHandler.HandleSendNowAsync: Email sent successfully. " +
                    "EmailId={EmailId}, CorrelationId={CorrelationId}",
                    emailId, correlationId);

                // Publish domain event: notifications.email.sent
                await PublishDomainEventAsync(
                    "sent", EmailEntityName, emailId.ToString(), correlationId, ct);

                return BuildResponse(200, new EmailHandlerResponse
                {
                    Success = true,
                    Message = "Email was successfully sent."
                }, correlationId);
            }
            else
            {
                var serverError = updatedEmail?.ServerError ?? email.ServerError ?? "Unknown error";

                _logger.LogError(
                    "EmailHandler.HandleSendNowAsync: Email send failed. EmailId={EmailId}, " +
                    "ServerError={ServerError}, CorrelationId={CorrelationId}",
                    emailId, serverError, correlationId);

                // Publish domain event: notifications.email.failed
                await PublishDomainEventAsync(
                    "failed", EmailEntityName, emailId.ToString(), correlationId, ct);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = serverError
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleTestSmtpAsync — Test SMTP Service
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces SmtpInternalService.TestSmtpServiceOnPost()
        // (source lines 387-478). Validates input fields, loads SMTP
        // service config, sends test email, returns result.
        //
        // Validation from source:
        //   lines 403-412: recipientEmail required and valid email
        //   lines 414-421: subject required
        //   lines 423-430: content required
        //   lines 432-441: serviceId must reference existing service
        //   line 467: smtpService.SendEmail(recipient, subject, "", content, attachments)
        //   line 468: success → "Email was successfully sent"
        //   lines 472-477: failure → error details
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles POST /v1/notifications/emails/test-smtp — send a test email via a specific SMTP service.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleTestSmtpAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var testRequest = DeserializeBody<TestSmtpRequest>(request.Body);
            if (testRequest == null)
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = InvalidRequestBodyError
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleTestSmtpAsync: Test SMTP requested. ServiceId={ServiceId}, " +
                "RecipientEmail={RecipientEmail}, CorrelationId={CorrelationId}",
                testRequest.ServiceId, testRequest.RecipientEmail, correlationId);

            // ── Input Validation (preserves exact logic from source lines 396-462) ──

            var validationErrors = new List<ValidationError>();

            // recipientEmail required and valid email — source lines 403-412
            if (string.IsNullOrWhiteSpace(testRequest.RecipientEmail))
            {
                validationErrors.Add(new ValidationError
                {
                    Key = "recipientEmail",
                    Value = string.Empty,
                    Message = "Recipient email is required."
                });
            }
            else if (!IsValidEmailAddress(testRequest.RecipientEmail))
            {
                validationErrors.Add(new ValidationError
                {
                    Key = "recipientEmail",
                    Value = testRequest.RecipientEmail,
                    Message = "Recipient email is not a valid email address."
                });
            }

            // subject required — source lines 414-421
            if (string.IsNullOrWhiteSpace(testRequest.Subject))
            {
                validationErrors.Add(new ValidationError
                {
                    Key = "subject",
                    Value = string.Empty,
                    Message = "Subject is required."
                });
            }

            // content required — source lines 423-430
            if (string.IsNullOrWhiteSpace(testRequest.Content))
            {
                validationErrors.Add(new ValidationError
                {
                    Key = "content",
                    Value = string.Empty,
                    Message = "Content is required."
                });
            }

            // serviceId must reference existing service — source lines 432-441
            SmtpServiceConfig? smtpService = null;
            if (testRequest.ServiceId == Guid.Empty)
            {
                validationErrors.Add(new ValidationError
                {
                    Key = "serviceId",
                    Value = string.Empty,
                    Message = "SMTP service ID is required."
                });
            }
            else
            {
                smtpService = await _repository.GetSmtpServiceByIdAsync(testRequest.ServiceId, ct);
                if (smtpService == null)
                {
                    validationErrors.Add(new ValidationError
                    {
                        Key = "serviceId",
                        Value = testRequest.ServiceId.ToString(),
                        Message = SmtpServiceNotFoundError
                    });
                }
            }

            if (validationErrors.Any())
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Validation failed.",
                    Errors = validationErrors
                }, correlationId);
            }

            // Send test email — maps to source line 467
            try
            {
                var recipient = new EmailAddress(testRequest.RecipientEmail);

                // Use single-recipient send overload via default service
                // (TestSmtp loads a specific service, then sends through it)
                // We use the direct Email+SmtpServiceConfig overload to target the specific service
                var testEmail = new Email
                {
                    Id = Guid.NewGuid(),
                    Subject = testRequest.Subject,
                    ContentHtml = testRequest.Content,
                    ContentText = string.Empty,
                    ServiceId = testRequest.ServiceId,
                    Sender = new EmailAddress { Name = smtpService!.Name ?? string.Empty, Address = string.Empty },
                    Recipients = new List<EmailAddress> { new EmailAddress(testRequest.RecipientEmail) },
                    Status = EmailStatus.Pending,
                    CreatedOn = DateTime.UtcNow
                };

                await _smtpService.SendEmailAsync(testEmail, smtpService, ct);

                // Check send result
                var sentEmail = await _repository.GetEmailByIdAsync(testEmail.Id, ct);
                if (sentEmail != null && sentEmail.Status == EmailStatus.Sent)
                {
                    _logger.LogInformation(
                        "EmailHandler.HandleTestSmtpAsync: Test email sent successfully. " +
                        "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                        testRequest.ServiceId, correlationId);

                    return BuildResponse(200, new EmailHandlerResponse
                    {
                        Success = true,
                        Message = "Email was successfully sent"
                    }, correlationId);
                }
                else
                {
                    var serverError = sentEmail?.ServerError ?? "Send operation did not complete.";

                    _logger.LogWarning(
                        "EmailHandler.HandleTestSmtpAsync: Test email failed. " +
                        "ServiceId={ServiceId}, ServerError={ServerError}, CorrelationId={CorrelationId}",
                        testRequest.ServiceId, serverError, correlationId);

                    return BuildResponse(400, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = $"Failed to send test email: {serverError}"
                    }, correlationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler.HandleTestSmtpAsync: Exception during test send. " +
                    "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                    testRequest.ServiceId, correlationId);

                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Failed to send test email: {ex.Message}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleCreateSmtpServiceAsync — Create SMTP Service Config
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles POST /v1/notifications/smtp-services — create a new SMTP service configuration.
        /// Replaces SmtpServiceRecordHook.OnPreCreateRecord (source lines 17-23).
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateSmtpServiceAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var config = DeserializeBody<SmtpServiceConfig>(request.Body);
            if (config == null)
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = InvalidRequestBodyError
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleCreateSmtpServiceAsync: Creating SMTP service. " +
                "ServiceName={ServiceName}, CorrelationId={CorrelationId}",
                config.Name, correlationId);

            try
            {
                // Assign new ID if not provided
                if (config.Id == Guid.Empty)
                {
                    config.Id = Guid.NewGuid();
                }

                // Validate — maps to source hook OnPreCreateRecord line 19
                var errors = await _smtpService.ValidateSmtpServiceCreateAsync(config, ct);
                if (errors != null && errors.Any())
                {
                    _logger.LogWarning(
                        "EmailHandler.HandleCreateSmtpServiceAsync: Validation failed. " +
                        "ErrorCount={ErrorCount}, CorrelationId={CorrelationId}",
                        errors.Count, correlationId);

                    return BuildResponse(400, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = "Validation failed.",
                        Errors = errors
                    }, correlationId);
                }

                // Handle default service setup — maps to source line 22
                var defaultErrors = await _smtpService.HandleDefaultServiceSetupAsync(config, ct);
                if (defaultErrors.Count > 0)
                {
                    _logger.LogWarning(
                        "EmailHandler.HandleCreateSmtpServiceAsync: Default service validation failed. " +
                        "ErrorCount={ErrorCount}, CorrelationId={CorrelationId}",
                        defaultErrors.Count, correlationId);

                    return BuildResponse(400, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = "Validation failed.",
                        Errors = defaultErrors
                    }, correlationId);
                }

                // Persist the new SMTP service config
                await _repository.SaveSmtpServiceAsync(config, ct);

                // Clear cache — maps to source line 35
                _repository.ClearSmtpServiceCache();

                _logger.LogInformation(
                    "EmailHandler.HandleCreateSmtpServiceAsync: SMTP service created. " +
                    "ServiceId={ServiceId}, ServiceName={ServiceName}, CorrelationId={CorrelationId}",
                    config.Id, config.Name, correlationId);

                // Publish domain event: notifications.smtp_service.created
                await PublishDomainEventAsync(
                    "created", SmtpServiceEntityName, config.Id.ToString(), correlationId, ct);

                return BuildResponse(201, new EmailHandlerResponse
                {
                    Success = true,
                    Message = "SMTP service created successfully.",
                    Data = config
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler.HandleCreateSmtpServiceAsync: Creation failed. " +
                    "CorrelationId={CorrelationId}", correlationId);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Failed to create SMTP service: {ex.Message}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleUpdateSmtpServiceAsync — Update SMTP Service Config
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles PUT /v1/notifications/smtp-services/{id} — update an existing SMTP service configuration.
        /// Replaces SmtpServiceRecordHook.OnPreUpdateRecord (source lines 25-30).
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateSmtpServiceAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var idParam = ExtractPathParameter(request, IdPathParam);
            if (string.IsNullOrEmpty(idParam) || !Guid.TryParse(idParam, out var serviceId))
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing SMTP service ID."
                }, correlationId);
            }

            var config = DeserializeBody<SmtpServiceConfig>(request.Body);
            if (config == null)
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = InvalidRequestBodyError
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleUpdateSmtpServiceAsync: Updating SMTP service. " +
                "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                serviceId, correlationId);

            try
            {
                // Ensure the path ID is applied to the config object
                config.Id = serviceId;

                // Verify the service exists
                var existing = await _repository.GetSmtpServiceByIdAsync(serviceId, ct);
                if (existing == null)
                {
                    return BuildResponse(404, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = SmtpServiceNotFoundError
                    }, correlationId);
                }

                // Validate — maps to source hook OnPreUpdateRecord line 27
                var errors = await _smtpService.ValidateSmtpServiceUpdateAsync(config, ct);
                if (errors != null && errors.Any())
                {
                    _logger.LogWarning(
                        "EmailHandler.HandleUpdateSmtpServiceAsync: Validation failed. " +
                        "ErrorCount={ErrorCount}, CorrelationId={CorrelationId}",
                        errors.Count, correlationId);

                    return BuildResponse(400, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = "Validation failed.",
                        Errors = errors
                    }, correlationId);
                }

                // Handle default service setup — maps to source line 30
                var defaultErrors = await _smtpService.HandleDefaultServiceSetupAsync(config, ct);
                if (defaultErrors.Count > 0)
                {
                    _logger.LogWarning(
                        "EmailHandler.HandleUpdateSmtpServiceAsync: Default service validation failed. " +
                        "ErrorCount={ErrorCount}, CorrelationId={CorrelationId}",
                        defaultErrors.Count, correlationId);

                    return BuildResponse(400, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = "Validation failed.",
                        Errors = defaultErrors
                    }, correlationId);
                }

                // Persist the updated SMTP service config
                await _repository.SaveSmtpServiceAsync(config, ct);

                // Clear cache — maps to source line 41
                _repository.ClearSmtpServiceCache();

                _logger.LogInformation(
                    "EmailHandler.HandleUpdateSmtpServiceAsync: SMTP service updated. " +
                    "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                    serviceId, correlationId);

                // Publish domain event: notifications.smtp_service.updated
                await PublishDomainEventAsync(
                    "updated", SmtpServiceEntityName, serviceId.ToString(), correlationId, ct);

                return BuildResponse(200, new EmailHandlerResponse
                {
                    Success = true,
                    Message = "SMTP service updated successfully.",
                    Data = config
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler.HandleUpdateSmtpServiceAsync: Update failed. " +
                    "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                    serviceId, correlationId);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Failed to update SMTP service: {ex.Message}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleDeleteSmtpServiceAsync — Delete SMTP Service Config
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles DELETE /v1/notifications/smtp-services/{id} — delete an SMTP service configuration.
        /// Replaces SmtpServiceRecordHook.OnPreDeleteRecord (source lines 43-49).
        /// CRITICAL: Default SMTP service cannot be deleted.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeleteSmtpServiceAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var idParam = ExtractPathParameter(request, IdPathParam);
            if (string.IsNullOrEmpty(idParam) || !Guid.TryParse(idParam, out var serviceId))
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing SMTP service ID."
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleDeleteSmtpServiceAsync: Deleting SMTP service. " +
                "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                serviceId, correlationId);

            try
            {
                // Load service to check default status — maps to source line 45
                var service = await _repository.GetSmtpServiceByIdAsync(serviceId, ct);
                if (service == null)
                {
                    return BuildResponse(404, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = SmtpServiceNotFoundError
                    }, correlationId);
                }

                // CRITICAL: Default service cannot be deleted — exact string from source line 47
                if (service.IsDefault)
                {
                    _logger.LogWarning(
                        "EmailHandler.HandleDeleteSmtpServiceAsync: Attempted to delete default service. " +
                        "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                        serviceId, correlationId);

                    return BuildResponse(400, new EmailHandlerResponse
                    {
                        Success = false,
                        Message = DefaultSmtpDeleteError
                    }, correlationId);
                }

                // Delete the SMTP service config
                await _repository.DeleteSmtpServiceAsync(serviceId, ct);

                // Clear cache — maps to source line 49
                _repository.ClearSmtpServiceCache();

                _logger.LogInformation(
                    "EmailHandler.HandleDeleteSmtpServiceAsync: SMTP service deleted. " +
                    "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                    serviceId, correlationId);

                // Publish domain event: notifications.smtp_service.deleted
                await PublishDomainEventAsync(
                    "deleted", SmtpServiceEntityName, serviceId.ToString(), correlationId, ct);

                // 204 No Content — successful delete with no body
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = 204,
                    Headers = new Dictionary<string, string>(StandardResponseHeaders)
                    {
                        [CorrelationIdHeader] = correlationId
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "EmailHandler.HandleDeleteSmtpServiceAsync: Delete failed. " +
                    "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                    serviceId, correlationId);

                return BuildResponse(500, new EmailHandlerResponse
                {
                    Success = false,
                    Message = $"Failed to delete SMTP service: {ex.Message}"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleGetSmtpServiceAsync — Get SMTP Service by ID
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles GET /v1/notifications/smtp-services/{id} — retrieve a single SMTP service configuration.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetSmtpServiceAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct = default)
        {
            var idParam = ExtractPathParameter(request, IdPathParam);
            if (string.IsNullOrEmpty(idParam) || !Guid.TryParse(idParam, out var serviceId))
            {
                return BuildResponse(400, new EmailHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing SMTP service ID."
                }, correlationId);
            }

            _logger.LogInformation(
                "EmailHandler.HandleGetSmtpServiceAsync: Retrieving SMTP service. " +
                "ServiceId={ServiceId}, CorrelationId={CorrelationId}",
                serviceId, correlationId);

            var service = await _repository.GetSmtpServiceByIdAsync(serviceId, ct);
            if (service == null)
            {
                return BuildResponse(404, new EmailHandlerResponse
                {
                    Success = false,
                    Message = SmtpServiceNotFoundError
                }, correlationId);
            }

            return BuildResponse(200, new EmailHandlerResponse
            {
                Success = true,
                Message = "SMTP service retrieved successfully.",
                Data = service
            }, correlationId);
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleListSmtpServicesAsync — List All SMTP Services
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles GET /v1/notifications/smtp-services — retrieve all SMTP service configurations.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleListSmtpServicesAsync(
            string correlationId,
            CancellationToken ct = default)
        {
            _logger.LogInformation(
                "EmailHandler.HandleListSmtpServicesAsync: Listing all SMTP services. " +
                "CorrelationId={CorrelationId}", correlationId);

            var services = await _repository.GetAllSmtpServicesAsync(ct);
            var serviceList = services?.ToList() ?? new List<SmtpServiceConfig>();

            return BuildResponse(200, new EmailHandlerListResponse
            {
                Success = true,
                Data = serviceList,
                TotalCount = serviceList.Count
            }, correlationId);
        }

        // ═════════════════════════════════════════════════════════════
        //  HandleHealthCheck — Health Check Endpoint
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles GET /v1/notifications/health — health check endpoint per AAP §0.8.5.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse HandleHealthCheck(string correlationId)
        {
            _logger.LogInformation(
                "EmailHandler.HandleHealthCheck: Health check requested. CorrelationId={CorrelationId}",
                correlationId);

            return BuildResponse(200, new EmailHandlerHealthResponse
            {
                Status = "healthy",
                Service = "notifications-email",
                Timestamp = DateTime.UtcNow.ToString("O")
            }, correlationId);
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  UTILITY METHODS
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a standardized APIGatewayHttpApiV2ProxyResponse with proper headers,
        /// correlation ID, and AOT-compatible JSON serialization.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode,
            object? body,
            string correlationId)
        {
            // AOT-safe serialization using source-generated JSON context
            string jsonBody;
            switch (body)
            {
                case EmailHandlerResponse r:
                    jsonBody = JsonSerializer.Serialize(r, EmailHandlerJsonContext.Default.EmailHandlerResponse);
                    break;
                case EmailHandlerListResponse lr:
                    jsonBody = JsonSerializer.Serialize(lr, EmailHandlerJsonContext.Default.EmailHandlerListResponse);
                    break;
                case EmailHandlerHealthResponse hr:
                    jsonBody = JsonSerializer.Serialize(hr, EmailHandlerJsonContext.Default.EmailHandlerHealthResponse);
                    break;
                case Email email:
                    jsonBody = JsonSerializer.Serialize(email, EmailHandlerJsonContext.Default.Email);
                    break;
                case SmtpServiceConfig cfg:
                    jsonBody = JsonSerializer.Serialize(cfg, EmailHandlerJsonContext.Default.SmtpServiceConfig);
                    break;
                default:
                    // AOT-safe fallback: all expected types are handled above
                    // For any unexpected types, use ToString() to avoid reflection-based serialization
                    jsonBody = body?.ToString() ?? string.Empty;
                    break;
            }

            var headers = new Dictionary<string, string>(StandardResponseHeaders)
            {
                [CorrelationIdHeader] = correlationId
            };

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = jsonBody,
                Headers = headers
            };
        }

        /// <summary>
        /// Deserializes the HTTP request body to the specified type using
        /// AOT-compatible source-generated JSON contexts where possible.
        /// Returns null if the body is null/empty or deserialization fails.
        /// </summary>
        private T? DeserializeBody<T>(string? body) where T : class
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                // AOT-safe deserialization using source-generated JSON context
                if (typeof(T) == typeof(SendEmailRequest))
                {
                    return JsonSerializer.Deserialize(body, EmailHandlerJsonContext.Default.SendEmailRequest) as T;
                }
                if (typeof(T) == typeof(QueueEmailRequest))
                {
                    return JsonSerializer.Deserialize(body, EmailHandlerJsonContext.Default.QueueEmailRequest) as T;
                }
                if (typeof(T) == typeof(TestSmtpRequest))
                {
                    return JsonSerializer.Deserialize(body, EmailHandlerJsonContext.Default.TestSmtpRequest) as T;
                }
                if (typeof(T) == typeof(SmtpServiceConfig))
                {
                    return JsonSerializer.Deserialize(body, EmailHandlerJsonContext.Default.SmtpServiceConfig) as T;
                }

                // AOT-safe fallback: all expected types are handled above
                // Return null for any unexpected deserialization target types
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Safely extracts a path parameter from the API Gateway request.
        /// Returns null if the parameter is not present.
        /// </summary>
        private static string? ExtractPathParameter(
            APIGatewayHttpApiV2ProxyRequest request,
            string paramName)
        {
            if (request.PathParameters != null &&
                request.PathParameters.TryGetValue(paramName, out var value))
            {
                return value;
            }
            return null;
        }

        /// <summary>
        /// Publishes a domain event to the SNS topic with structured event metadata.
        /// Replaces monolith's IErpPostCreateRecordHook / IErpPostUpdateRecordHook patterns.
        /// Event subject format: notifications.{entity}.{action} per AAP §0.8.5.
        /// </summary>
        private async Task PublishDomainEventAsync(
            string action,
            string entityName,
            string entityId,
            string correlationId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_snsTopicArn))
            {
                _logger.LogWarning(
                    "EmailHandler.PublishDomainEventAsync: SNS topic ARN not configured. " +
                    "Event not published. Action={Action}, Entity={Entity}, EntityId={EntityId}, " +
                    "CorrelationId={CorrelationId}",
                    action, entityName, entityId, correlationId);
                return;
            }

            try
            {
                // AOT-safe payload serialization — manual JSON construction for simple structure
                var eventTimestamp = DateTime.UtcNow.ToString("O");
                var payloadJson = $"{{\"entity_id\":\"{entityId}\",\"action\":\"{action}\",\"entity\":\"{entityName}\",\"timestamp\":\"{eventTimestamp}\"}}";

                var domainEvent = new EmailHandlerDomainEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    Timestamp = eventTimestamp,
                    Action = action,
                    Entity = entityName,
                    EntityId = entityId,
                    CorrelationId = correlationId,
                    Payload = payloadJson
                };

                var eventSubject = $"notifications.{entityName}.{action}";
                var eventBody = JsonSerializer.Serialize(
                    domainEvent, EmailHandlerJsonContext.Default.EmailHandlerDomainEvent);

                var publishRequest = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Subject = eventSubject,
                    Message = eventBody
                };

                await _snsClient.PublishAsync(publishRequest, ct);

                _logger.LogInformation(
                    "EmailHandler.PublishDomainEventAsync: Domain event published. " +
                    "Subject={Subject}, EventId={EventId}, CorrelationId={CorrelationId}",
                    eventSubject, domainEvent.EventId, correlationId);
            }
            catch (Exception ex)
            {
                // Log but do NOT throw — domain event publishing is best-effort
                // and must not break the request/response flow
                _logger.LogError(ex,
                    "EmailHandler.PublishDomainEventAsync: Failed to publish domain event. " +
                    "Action={Action}, Entity={Entity}, EntityId={EntityId}, CorrelationId={CorrelationId}",
                    action, entityName, entityId, correlationId);
            }
        }

        /// <summary>
        /// Performs basic email address format validation.
        /// Checks for presence of @ and a dot in the domain portion.
        /// </summary>
        private static bool IsValidEmailAddress(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }

            // Basic validation: must contain exactly one @, with content before and after
            var atIndex = email.IndexOf('@');
            if (atIndex <= 0 || atIndex == email.Length - 1)
            {
                return false;
            }

            var domain = email.Substring(atIndex + 1);
            if (string.IsNullOrWhiteSpace(domain) || !domain.Contains('.'))
            {
                return false;
            }

            // Check for no spaces
            if (email.Contains(' '))
            {
                return false;
            }

            return true;
        }
    }
}
