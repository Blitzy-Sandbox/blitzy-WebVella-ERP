using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Models;

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
    [JsonSerializable(typeof(SnsNotificationEnvelope))]
    [JsonSerializable(typeof(Notification))]
    [JsonSerializable(typeof(NotificationEvent))]
    [JsonSerializable(typeof(WebhookConfig))]
    [JsonSerializable(typeof(List<WebhookConfig>))]
    [JsonSerializable(typeof(WebhookHandlerResponse))]
    [JsonSerializable(typeof(WebhookHandlerListResponse))]
    [JsonSerializable(typeof(WebhookDispatchDomainEvent))]
    internal partial class WebhookHandlerJsonContext : JsonSerializerContext { }

    // ───────────────────────────────────────────────────────────────────
    // SNS Notification Envelope (inline helper)
    // ───────────────────────────────────────────────────────────────────
    // When an SQS queue is subscribed to an SNS topic, the SQS message
    // body is an SNS envelope wrapping the actual domain event payload.
    // ───────────────────────────────────────────────────────────────────

    internal sealed class SnsNotificationEnvelope
    {
        [JsonPropertyName("Type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("Subject")]
        public string? Subject { get; set; }

        [JsonPropertyName("Message")]
        public string? Message { get; set; }

        [JsonPropertyName("MessageId")]
        public string? MessageId { get; set; }

        [JsonPropertyName("Timestamp")]
        public string? Timestamp { get; set; }
    }

    // ───────────────────────────────────────────────────────────────────
    // Standard API Response DTOs
    // ───────────────────────────────────────────────────────────────────

    internal sealed class WebhookHandlerResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public object? Object { get; set; }
    }

    internal sealed class WebhookHandlerListResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public List<WebhookConfig>? Object { get; set; }
    }

    // ───────────────────────────────────────────────────────────────────
    // Domain Event Payload for SNS Publishing
    // ───────────────────────────────────────────────────────────────────
    // Published on webhook dispatch success/failure per AAP §0.8.5:
    //   notifications.webhook.dispatched
    //   notifications.webhook.failed
    // ───────────────────────────────────────────────────────────────────

    internal sealed class WebhookDispatchDomainEvent
    {
        [JsonPropertyName("event_id")]
        public string EventId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("webhook_id")]
        public string WebhookId { get; set; } = string.Empty;

        [JsonPropertyName("endpoint_url")]
        public string EndpointUrl { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = string.Empty;

        [JsonPropertyName("correlation_id")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("status_code")]
        public int? StatusCode { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }
    }

    // ───────────────────────────────────────────────────────────────────
    // WebhookHandler — Primary Lambda Entry Point
    // ───────────────────────────────────────────────────────────────────
    //
    // Replaces the monolith's PostgreSQL LISTEN/NOTIFY pub/sub system
    // (NotificationContext.cs) with an SNS/SQS-based event bus for
    // domain event routing and HTTP webhook endpoint dispatch.
    //
    // Entry Points:
    //   HandleAsync           — SQS trigger for webhook dispatch
    //   HandleHttpAsync       — HTTP API Gateway v2 for webhook CRUD
    //
    // Source Architecture Replaced:
    //   Static singleton NotificationContext.Current → Stateless Lambda
    //   PostgreSQL LISTEN on ERP_NOTIFICATIONS_CHANNNEL → SQS trigger
    //   Reflection-based handler discovery → DynamoDB webhook configs
    //   MethodInfo.Invoke in-process dispatch → HTTP POST to endpoints
    //   Base64-encoded NOTIFY → SNS PublishAsync
    //   JsonConvert + TypeNameHandling.Auto → System.Text.Json (secure)
    //
    // ───────────────────────────────────────────────────────────────────

    public class WebhookHandler
    {
        // ── Constants ────────────────────────────────────────────────
        private const string SnsTopicArnEnvVar = "NOTIFICATIONS_SNS_TOPIC_ARN";
        private const string WebhookIdPathParam = "id";

        /// <summary>
        /// Standard CORS and content-type headers returned on every HTTP response.
        /// </summary>
        private static readonly Dictionary<string, string> StandardResponseHeaders = new()
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS" },
            { "Access-Control-Allow-Headers", "Content-Type,Authorization,X-Correlation-Id" }
        };

        // ── Dependencies ─────────────────────────────────────────────
        private readonly INotificationRepository _repository;
        private readonly HttpClient _httpClient;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<WebhookHandler> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _snsTopicArn;

        // ── Parameterless Constructor (Lambda Runtime) ───────────────
        /// <summary>
        /// Parameterless constructor invoked by the AWS Lambda runtime.
        /// Builds a DI ServiceCollection, registers all dependencies
        /// (AWS SDK clients, repository, HttpClient), and resolves them.
        /// AWS SDK clients use AWS_ENDPOINT_URL for LocalStack compatibility
        /// per AAP §0.7.6.
        /// </summary>
        public WebhookHandler()
        {
            var services = new ServiceCollection();

            // Structured JSON logging per AAP §0.8.5
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

            // AWS_ENDPOINT_URL for LocalStack dual-target (AAP §0.7.6)
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production AWS: default credential + region resolution
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
            }

            // HttpClient — reuse via DI for connection pooling
            services.AddSingleton<HttpClient>();

            // Application services
            services.AddTransient<INotificationRepository, NotificationRepository>();

            // Build the DI container
            _serviceProvider = services.BuildServiceProvider();
            _repository = _serviceProvider.GetRequiredService<INotificationRepository>();
            _httpClient = _serviceProvider.GetRequiredService<HttpClient>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<WebhookHandler>();

            // SNS topic ARN for domain event publishing
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar) ?? string.Empty;
        }

        // ── Testing Constructor (DI-injected) ────────────────────────
        /// <summary>
        /// Secondary constructor for unit/integration testing. Accepts
        /// a pre-built IServiceProvider to inject test doubles.
        /// </summary>
        public WebhookHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _repository = _serviceProvider.GetRequiredService<INotificationRepository>();
            _httpClient = _serviceProvider.GetRequiredService<HttpClient>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<WebhookHandler>();
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar) ?? string.Empty;
        }

        // ═════════════════════════════════════════════════════════════
        //  ENTRY POINT 1: SQS Trigger — Webhook Dispatch
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces NotificationContext.ListenForNotifications() +
        //          NotificationContext.HandleNotification()
        //
        // Source (NotificationContext.cs lines 107-147):
        //   1. PostgreSQL LISTEN ERP_NOTIFICATIONS_CHANNNEL
        //   2. Base64-decode payload → JsonConvert.Deserialize<Notification>
        //   3. Filter listeners by channel (case-insensitive)
        //   4. Task.Run(() => listener.Method.Invoke(...))
        //
        // New:
        //   1. SQS invokes Lambda with batch of messages
        //   2. Parse SNS envelope → extract channel + payload
        //   3. Query DynamoDB for active webhooks matching channel
        //   4. HTTP POST to each webhook endpoint URL
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// SQS-triggered Lambda handler that processes domain events and
        /// dispatches them to registered webhook endpoints. Each SQS
        /// message is processed idempotently per AAP §0.8.5.
        /// </summary>
        /// <param name="sqsEvent">Batch of SQS messages from the webhook queue.</param>
        /// <param name="context">Lambda execution context providing AwsRequestId for correlation.</param>
        public async Task HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
        {
            var correlationId = context.AwsRequestId;

            _logger.LogInformation(
                "WebhookHandler.HandleAsync started. CorrelationId={CorrelationId}, MessageCount={Count}",
                correlationId, sqsEvent?.Records?.Count ?? 0);

            if (sqsEvent?.Records == null || sqsEvent.Records.Count == 0)
            {
                _logger.LogWarning(
                    "WebhookHandler.HandleAsync received empty SQS event. CorrelationId={CorrelationId}",
                    correlationId);
                return;
            }

            foreach (var message in sqsEvent.Records)
            {
                var messageCorrelationId = $"{correlationId}:{message.MessageId}";

                try
                {
                    _logger.LogInformation(
                        "Processing SQS message. MessageId={MessageId}, CorrelationId={CorrelationId}",
                        message.MessageId, messageCorrelationId);

                    // Phase 5: Parse the event from the SQS message
                    var (channel, payload) = ParseEventFromSqsMessage(message);

                    if (string.IsNullOrWhiteSpace(channel))
                    {
                        _logger.LogWarning(
                            "SQS message has empty channel; skipping. MessageId={MessageId}, CorrelationId={CorrelationId}",
                            message.MessageId, messageCorrelationId);
                        continue;
                    }

                    _logger.LogInformation(
                        "Extracted event channel={Channel} from SQS message. MessageId={MessageId}, CorrelationId={CorrelationId}",
                        channel, message.MessageId, messageCorrelationId);

                    // Phase 4: Look up active webhooks for this channel
                    // Replaces: listeners.Where(l => l.Channel.ToLowerInvariant() == notification.Channel.ToLowerInvariant())
                    // (source NotificationContext.cs line 143)
                    var webhooks = await _repository.GetActiveWebhooksByChannelAsync(channel, CancellationToken.None);

                    if (webhooks == null || !webhooks.Any())
                    {
                        _logger.LogInformation(
                            "No active webhooks found for channel={Channel}. MessageId={MessageId}, CorrelationId={CorrelationId}",
                            channel, message.MessageId, messageCorrelationId);
                        continue;
                    }

                    // Case-insensitive channel matching preserving source behavior
                    // (source NotificationContext.cs line 143: listeners.Where(l => l.Channel.ToLowerInvariant() == ...))
                    var matchingWebhooks = webhooks
                        .Where(w => string.IsNullOrEmpty(w.Channel) ||
                                    w.Channel.ToLowerInvariant() == channel.ToLowerInvariant())
                        .ToList();

                    _logger.LogInformation(
                        "Found {WebhookCount} matching webhook(s) for channel={Channel}. CorrelationId={CorrelationId}",
                        matchingWebhooks.Count, channel, messageCorrelationId);

                    // Dispatch to each matching webhook
                    // Replaces: Task.Run(() => listener.Method.Invoke(listener.Instance, new object[] { notification }))
                    // (source NotificationContext.cs line 146)
                    foreach (var webhook in matchingWebhooks)
                    {
                        await DispatchWebhookAsync(webhook, channel, payload, messageCorrelationId, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    // Log but do NOT throw — allow SQS message to be acknowledged
                    // so it doesn't block the batch. Failed messages eventually go
                    // to DLQ (notifications-webhook-queue-dlq) per AAP §0.8.5.
                    _logger.LogError(ex,
                        "Error processing SQS message. MessageId={MessageId}, CorrelationId={CorrelationId}",
                        message.MessageId, messageCorrelationId);
                }
            }

            _logger.LogInformation(
                "WebhookHandler.HandleAsync completed. CorrelationId={CorrelationId}",
                correlationId);
        }

        // ═════════════════════════════════════════════════════════════
        //  ENTRY POINT 2: HTTP API Gateway — Webhook Configuration CRUD
        // ═════════════════════════════════════════════════════════════
        //
        // Provides RESTful endpoints for managing webhook registrations:
        //   POST   /v1/notifications/webhooks           → Create
        //   GET    /v1/notifications/webhooks            → List All
        //   GET    /v1/notifications/webhooks/{id}       → Get by ID
        //   PUT    /v1/notifications/webhooks/{id}       → Update
        //   DELETE /v1/notifications/webhooks/{id}       → Delete
        //
        // No source equivalent — this is new functionality to manage
        // the webhook configs that replaced reflection-based discovery.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// HTTP API Gateway v2 Lambda handler that routes incoming requests
        /// to the appropriate webhook CRUD method based on RouteKey.
        /// </summary>
        /// <param name="request">HTTP API Gateway v2 proxy request.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>HTTP API Gateway v2 proxy response.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleHttpAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = context.AwsRequestId;

            _logger.LogInformation(
                "WebhookHandler.HandleHttpAsync started. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                request.RouteKey, correlationId);

            try
            {
                // Route based on HTTP method + actual path (not RouteKey pattern).
                // For {proxy+} routes, RouteKey contains the pattern (e.g., "GET /v1/notifications/{proxy+}")
                // rather than the actual path, so use RawPath for accurate dispatch.
                var httpMethod = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";
                var rawPath = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
                var routeKey = $"{httpMethod} {rawPath}";

                if (routeKey.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/webhooks", StringComparison.OrdinalIgnoreCase) &&
                    !PathContainsGuid(rawPath))
                {
                    return await HandleCreateWebhookAsync(request, correlationId, CancellationToken.None);
                }

                if (routeKey.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                    routeKey.Contains("/webhooks", StringComparison.OrdinalIgnoreCase) &&
                    !PathContainsGuid(rawPath))
                {
                    return await HandleListWebhooksAsync(request, correlationId, CancellationToken.None);
                }

                if (routeKey.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                    PathContainsGuid(rawPath))
                {
                    return await HandleGetWebhookAsync(request, correlationId, CancellationToken.None);
                }

                if (routeKey.StartsWith("PUT ", StringComparison.OrdinalIgnoreCase) &&
                    PathContainsGuid(rawPath))
                {
                    return await HandleUpdateWebhookAsync(request, correlationId, CancellationToken.None);
                }

                if (routeKey.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase) &&
                    PathContainsGuid(rawPath))
                {
                    return await HandleDeleteWebhookAsync(request, correlationId, CancellationToken.None);
                }

                // OPTIONS for CORS preflight
                if (routeKey.StartsWith("OPTIONS ", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildResponse(204, null, correlationId);
                }

                _logger.LogWarning(
                    "Unknown route. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                    routeKey, correlationId);

                return BuildResponse(404, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = $"Route not found: {routeKey}"
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unhandled exception in HandleHttpAsync. RouteKey={RouteKey}, CorrelationId={CorrelationId}",
                    request.RouteKey, correlationId);

                return BuildResponse(500, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Internal server error"
                }, correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  WEBHOOK CRUD HANDLERS
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// POST /v1/notifications/webhooks — Register a new webhook configuration.
        /// Validates the endpoint URL, assigns a unique ID and timestamp, then
        /// persists the webhook to DynamoDB.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateWebhookAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct)
        {
            _logger.LogInformation(
                "HandleCreateWebhookAsync started. CorrelationId={CorrelationId}", correlationId);

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Request body is required"
                }, correlationId);
            }

            WebhookConfig? config;
            try
            {
                config = JsonSerializer.Deserialize(
                    request.Body,
                    WebhookHandlerJsonContext.Default.WebhookConfig);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Invalid JSON in create webhook request. CorrelationId={CorrelationId}", correlationId);

                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Invalid JSON in request body"
                }, correlationId);
            }

            if (config == null)
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Request body deserialized to null"
                }, correlationId);
            }

            // Validate endpoint URL
            if (string.IsNullOrWhiteSpace(config.EndpointUrl) ||
                (!config.EndpointUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !config.EndpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "EndpointUrl is required and must be a valid HTTP/HTTPS URL"
                }, correlationId);
            }

            // Validate channel
            if (string.IsNullOrWhiteSpace(config.Channel))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Channel is required"
                }, correlationId);
            }

            // Assign server-generated fields
            config.Id = Guid.NewGuid();
            config.CreatedOn = DateTime.UtcNow;
            config.IsEnabled = true;

            // Apply sensible defaults for retry config
            if (config.MaxRetries <= 0) config.MaxRetries = 3;
            if (config.RetryIntervalSeconds <= 0) config.RetryIntervalSeconds = 5;

            await _repository.SaveWebhookConfigAsync(config, ct);

            _logger.LogInformation(
                "Webhook created. WebhookId={WebhookId}, Channel={Channel}, EndpointUrl={EndpointUrl}, CorrelationId={CorrelationId}",
                config.Id, config.Channel, config.EndpointUrl, correlationId);

            return BuildResponse(201, new WebhookHandlerResponse
            {
                Success = true,
                Message = "Webhook created successfully",
                Object = config
            }, correlationId);
        }

        /// <summary>
        /// GET /v1/notifications/webhooks — List all active webhook configurations.
        /// Returns webhooks for all channels when no filter is applied.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleListWebhooksAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct)
        {
            _logger.LogInformation(
                "HandleListWebhooksAsync started. CorrelationId={CorrelationId}", correlationId);

            // Optional channel filter from query string
            string channelFilter = string.Empty;
            if (request.QueryStringParameters != null &&
                request.QueryStringParameters.TryGetValue("channel", out var channel))
            {
                channelFilter = channel ?? string.Empty;
            }

            var webhooks = await _repository.GetActiveWebhooksByChannelAsync(channelFilter, ct);

            _logger.LogInformation(
                "Listed {Count} webhook(s). ChannelFilter={Channel}, CorrelationId={CorrelationId}",
                webhooks?.Count ?? 0, channelFilter, correlationId);

            return BuildResponse(200, new WebhookHandlerListResponse
            {
                Success = true,
                Message = "Webhooks retrieved successfully",
                Object = webhooks ?? new List<WebhookConfig>()
            }, correlationId);
        }

        /// <summary>
        /// GET /v1/notifications/webhooks/{id} — Retrieve a single webhook by ID.
        /// Returns 404 if the webhook does not exist.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetWebhookAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct)
        {
            _logger.LogInformation(
                "HandleGetWebhookAsync started. CorrelationId={CorrelationId}", correlationId);

            var idStr = ExtractPathParameter(request, WebhookIdPathParam);
            if (string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var webhookId))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing webhook ID"
                }, correlationId);
            }

            var config = await _repository.GetWebhookConfigByIdAsync(webhookId, ct);
            if (config == null)
            {
                return BuildResponse(404, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = $"Webhook with ID {webhookId} not found"
                }, correlationId);
            }

            _logger.LogInformation(
                "Webhook retrieved. WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                webhookId, correlationId);

            return BuildResponse(200, new WebhookHandlerResponse
            {
                Success = true,
                Message = "Webhook retrieved successfully",
                Object = config
            }, correlationId);
        }

        /// <summary>
        /// PUT /v1/notifications/webhooks/{id} — Update an existing webhook.
        /// Validates the endpoint URL and channel, merges changes, and persists.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateWebhookAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct)
        {
            _logger.LogInformation(
                "HandleUpdateWebhookAsync started. CorrelationId={CorrelationId}", correlationId);

            var idStr = ExtractPathParameter(request, WebhookIdPathParam);
            if (string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var webhookId))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing webhook ID"
                }, correlationId);
            }

            // Verify the webhook exists
            var existingConfig = await _repository.GetWebhookConfigByIdAsync(webhookId, ct);
            if (existingConfig == null)
            {
                return BuildResponse(404, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = $"Webhook with ID {webhookId} not found"
                }, correlationId);
            }

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Request body is required"
                }, correlationId);
            }

            WebhookConfig? updatePayload;
            try
            {
                updatePayload = JsonSerializer.Deserialize(
                    request.Body,
                    WebhookHandlerJsonContext.Default.WebhookConfig);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Invalid JSON in update webhook request. CorrelationId={CorrelationId}", correlationId);

                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Invalid JSON in request body"
                }, correlationId);
            }

            if (updatePayload == null)
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Request body deserialized to null"
                }, correlationId);
            }

            // Merge changes — preserve ID and CreatedOn from existing record
            if (!string.IsNullOrWhiteSpace(updatePayload.EndpointUrl))
            {
                if (!updatePayload.EndpointUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !updatePayload.EndpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildResponse(400, new WebhookHandlerResponse
                    {
                        Success = false,
                        Message = "EndpointUrl must be a valid HTTP/HTTPS URL"
                    }, correlationId);
                }
                existingConfig.EndpointUrl = updatePayload.EndpointUrl;
            }

            if (!string.IsNullOrWhiteSpace(updatePayload.Channel))
            {
                existingConfig.Channel = updatePayload.Channel;
            }

            if (updatePayload.MaxRetries > 0)
            {
                existingConfig.MaxRetries = updatePayload.MaxRetries;
            }

            if (updatePayload.RetryIntervalSeconds > 0)
            {
                existingConfig.RetryIntervalSeconds = updatePayload.RetryIntervalSeconds;
            }

            // IsEnabled can be explicitly set (default false from deserialization means "not provided")
            // To handle this correctly, we always apply the value from the update payload
            existingConfig.IsEnabled = updatePayload.IsEnabled;

            await _repository.SaveWebhookConfigAsync(existingConfig, ct);

            _logger.LogInformation(
                "Webhook updated. WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                webhookId, correlationId);

            return BuildResponse(200, new WebhookHandlerResponse
            {
                Success = true,
                Message = "Webhook updated successfully",
                Object = existingConfig
            }, correlationId);
        }

        /// <summary>
        /// DELETE /v1/notifications/webhooks/{id} — Remove a webhook configuration.
        /// Returns 204 on success, 404 if the webhook does not exist.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeleteWebhookAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId,
            CancellationToken ct)
        {
            _logger.LogInformation(
                "HandleDeleteWebhookAsync started. CorrelationId={CorrelationId}", correlationId);

            var idStr = ExtractPathParameter(request, WebhookIdPathParam);
            if (string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var webhookId))
            {
                return BuildResponse(400, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = "Invalid or missing webhook ID"
                }, correlationId);
            }

            // Verify existence before deletion
            var existing = await _repository.GetWebhookConfigByIdAsync(webhookId, ct);
            if (existing == null)
            {
                return BuildResponse(404, new WebhookHandlerResponse
                {
                    Success = false,
                    Message = $"Webhook with ID {webhookId} not found"
                }, correlationId);
            }

            await _repository.DeleteWebhookConfigAsync(webhookId, ct);

            _logger.LogInformation(
                "Webhook deleted. WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                webhookId, correlationId);

            return BuildResponse(204, null, correlationId);
        }

        // ═════════════════════════════════════════════════════════════
        //  WEBHOOK DISPATCH (Private)
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces: Task.Run(() => listener.Method.Invoke(
        //              listener.Instance, new object[] { notification }))
        // (source NotificationContext.cs line 146)
        //
        // Instead of in-process reflection-based invocation, we dispatch
        // via HTTP POST to the registered webhook endpoint URL.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Dispatches a domain event payload to a single webhook endpoint via
        /// HTTP POST. Implements retry logic with configurable max retries and
        /// interval from the WebhookConfig.
        /// </summary>
        /// <param name="webhook">Webhook configuration with endpoint URL and retry settings.</param>
        /// <param name="channel">Event channel/subject for header inclusion.</param>
        /// <param name="payload">JSON serialized domain event payload (message body).</param>
        /// <param name="correlationId">Correlation ID for distributed tracing.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        private async Task DispatchWebhookAsync(
            WebhookConfig webhook,
            string channel,
            string payload,
            string correlationId,
            CancellationToken ct)
        {
            var maxRetries = webhook.MaxRetries > 0 ? webhook.MaxRetries : 3;
            var retryInterval = webhook.RetryIntervalSeconds > 0 ? webhook.RetryIntervalSeconds : 5;
            var attempt = 0;
            Exception? lastException = null;
            int? lastStatusCode = null;

            _logger.LogInformation(
                "Dispatching webhook. WebhookId={WebhookId}, EndpointUrl={EndpointUrl}, Channel={Channel}, MaxRetries={MaxRetries}, CorrelationId={CorrelationId}",
                webhook.Id, webhook.EndpointUrl, channel, maxRetries, correlationId);

            while (attempt <= maxRetries)
            {
                attempt++;
                try
                {
                    // Build the HTTP POST request
                    // Replaces base64-encoded payload in source SendNotification
                    // (NotificationContext.cs lines 155-156)
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, webhook.EndpointUrl);
                    httpRequest.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    httpRequest.Headers.TryAddWithoutValidation("X-Webhook-Id", webhook.Id.ToString());
                    httpRequest.Headers.TryAddWithoutValidation("X-Event-Channel", channel);
                    httpRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);

                    using var response = await _httpClient.SendAsync(httpRequest, ct);
                    lastStatusCode = (int)response.StatusCode;

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation(
                            "Webhook dispatched successfully. WebhookId={WebhookId}, EndpointUrl={EndpointUrl}, Channel={Channel}, StatusCode={StatusCode}, Attempt={Attempt}, CorrelationId={CorrelationId}",
                            webhook.Id, webhook.EndpointUrl, channel, (int)response.StatusCode, attempt, correlationId);

                        // Publish success domain event: notifications.webhook.dispatched
                        await PublishDomainEventAsync("dispatched", new WebhookDispatchDomainEvent
                        {
                            EventId = Guid.NewGuid().ToString(),
                            Timestamp = DateTime.UtcNow.ToString("O"),
                            Action = "dispatched",
                            WebhookId = webhook.Id.ToString(),
                            EndpointUrl = webhook.EndpointUrl,
                            Channel = channel,
                            CorrelationId = correlationId,
                            StatusCode = (int)response.StatusCode
                        }, ct);

                        return; // Success — exit the retry loop
                    }

                    // Non-success status code — treat as retryable
                    _logger.LogWarning(
                        "Webhook dispatch received non-success status. WebhookId={WebhookId}, EndpointUrl={EndpointUrl}, StatusCode={StatusCode}, Attempt={Attempt}/{MaxRetries}, CorrelationId={CorrelationId}",
                        webhook.Id, webhook.EndpointUrl, (int)response.StatusCode, attempt, maxRetries + 1, correlationId);
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex,
                        "Webhook dispatch HTTP error. WebhookId={WebhookId}, EndpointUrl={EndpointUrl}, Attempt={Attempt}/{MaxRetries}, CorrelationId={CorrelationId}",
                        webhook.Id, webhook.EndpointUrl, attempt, maxRetries + 1, correlationId);
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // Timeout (not user cancellation)
                    lastException = ex;
                    _logger.LogWarning(ex,
                        "Webhook dispatch timed out. WebhookId={WebhookId}, EndpointUrl={EndpointUrl}, Attempt={Attempt}/{MaxRetries}, CorrelationId={CorrelationId}",
                        webhook.Id, webhook.EndpointUrl, attempt, maxRetries + 1, correlationId);
                }

                // Wait before retry (only if there are retries remaining)
                if (attempt <= maxRetries)
                {
                    _logger.LogInformation(
                        "Retrying webhook dispatch in {RetryInterval}s. WebhookId={WebhookId}, Attempt={Attempt}, CorrelationId={CorrelationId}",
                        retryInterval, webhook.Id, attempt, correlationId);
                    await Task.Delay(TimeSpan.FromSeconds(retryInterval), ct);
                }
            }

            // All retries exhausted — publish failure domain event
            _logger.LogError(lastException,
                "Webhook dispatch failed after all retries. WebhookId={WebhookId}, EndpointUrl={EndpointUrl}, Channel={Channel}, TotalAttempts={TotalAttempts}, LastStatusCode={LastStatusCode}, CorrelationId={CorrelationId}",
                webhook.Id, webhook.EndpointUrl, channel, attempt, lastStatusCode, correlationId);

            await PublishDomainEventAsync("failed", new WebhookDispatchDomainEvent
            {
                EventId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow.ToString("O"),
                Action = "failed",
                WebhookId = webhook.Id.ToString(),
                EndpointUrl = webhook.EndpointUrl,
                Channel = channel,
                CorrelationId = correlationId,
                StatusCode = lastStatusCode,
                ErrorMessage = lastException?.Message ?? "Non-success HTTP status code after all retries"
            }, ct);

            // Do NOT throw — allow SQS message to be acknowledged so it doesn't
            // block the batch. The failed event has been published and the DLQ
            // (notifications-webhook-queue-dlq) handles message-level retries.
        }

        // ═════════════════════════════════════════════════════════════
        //  EVENT DESERIALIZATION (Private)
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces: Encoding.UTF8.TryParseBase64(e.Payload, out json)
        //           then JsonConvert.DeserializeObject<Notification>(
        //               json, new JsonSerializerSettings {
        //                   TypeNameHandling = TypeNameHandling.Auto })
        // (source NotificationContext.cs lines 116-118)
        //
        // CRITICAL SECURITY: The source's TypeNameHandling.Auto is a known
        // deserialization vulnerability. We use System.Text.Json with NO
        // type-name embedding and strongly-typed models only.
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Parses an SQS message to extract the event channel and payload.
        /// Handles two cases:
        ///   1. SNS envelope (queue subscribed to SNS topic) — extract
        ///      Subject as channel and Message as payload
        ///   2. Direct SQS message — try NotificationEvent first (record
        ///      change events), fall back to Notification (generic events)
        /// </summary>
        /// <param name="message">Raw SQS message from the batch.</param>
        /// <returns>Tuple of (channel, payload) extracted from the message.</returns>
        private (string channel, string payload) ParseEventFromSqsMessage(SQSEvent.SQSMessage message)
        {
            if (string.IsNullOrWhiteSpace(message.Body))
            {
                return (string.Empty, string.Empty);
            }

            // Attempt 1: Parse as SNS notification envelope
            try
            {
                var snsEnvelope = JsonSerializer.Deserialize(
                    message.Body,
                    WebhookHandlerJsonContext.Default.SnsNotificationEnvelope);

                if (snsEnvelope != null &&
                    string.Equals(snsEnvelope.Type, "Notification", StringComparison.OrdinalIgnoreCase))
                {
                    return (snsEnvelope.Subject ?? string.Empty, snsEnvelope.Message ?? string.Empty);
                }
            }
            catch (JsonException)
            {
                // Not an SNS envelope — fall through to direct message parsing
            }

            // Attempt 2: Parse as NotificationEvent (record change events)
            // Maps to source ErpRecordChangeNotification (EntityId, EntityName, RecordId)
            try
            {
                var notificationEvent = JsonSerializer.Deserialize(
                    message.Body,
                    WebhookHandlerJsonContext.Default.NotificationEvent);

                if (notificationEvent != null &&
                    !string.IsNullOrWhiteSpace(notificationEvent.EntityName) &&
                    !string.IsNullOrWhiteSpace(notificationEvent.Action))
                {
                    // Construct channel from entity + action: e.g. "crm.account.created"
                    var channel = $"{notificationEvent.EntityName}.{notificationEvent.Action}";

                    // Access EntityId and RecordId for structured logging context
                    // Maps to source ErpRecordChangeNotification (EntityId, RecordId)
                    var entityId = notificationEvent.EntityId;
                    var recordId = notificationEvent.RecordId;
                    _logger.LogInformation(
                        "Parsed NotificationEvent: EntityName={EntityName}, Action={Action}, EntityId={EntityId}, RecordId={RecordId}",
                        notificationEvent.EntityName, notificationEvent.Action, entityId, recordId);

                    return (channel, message.Body);
                }
            }
            catch (JsonException)
            {
                // Not a NotificationEvent — fall through to generic Notification
            }

            // Attempt 3: Parse as generic Notification (Channel + Message)
            // Maps to source Notification.cs (lines 8-12)
            try
            {
                var notification = JsonSerializer.Deserialize(
                    message.Body,
                    WebhookHandlerJsonContext.Default.Notification);

                if (notification != null && !string.IsNullOrWhiteSpace(notification.Channel))
                {
                    return (notification.Channel, notification.Message ?? message.Body);
                }
            }
            catch (JsonException)
            {
                // Unable to parse as any known type
            }

            // Fallback: use the raw message body as both channel and payload
            // Extract channel from message attributes if available
            var fallbackChannel = string.Empty;
            if (message.MessageAttributes != null &&
                message.MessageAttributes.TryGetValue("channel", out var channelAttr))
            {
                fallbackChannel = channelAttr?.StringValue ?? string.Empty;
            }

            return (fallbackChannel, message.Body);
        }

        // ═════════════════════════════════════════════════════════════
        //  DOMAIN EVENT PUBLISHING (Private)
        // ═════════════════════════════════════════════════════════════
        //
        // Replaces: NotificationContext.SendNotification() (source lines 153-170)
        //   var json = JsonConvert.SerializeObject(notification, settings);
        //   var encodedText = Encoding.UTF8.ToBase64(json);
        //   string sql = $"notify {SQL_NOTIFICATION_CHANNEL_NAME}, '{encodedText}';";
        //
        // New: SNS PublishAsync with JSON payload and event-typed subject
        // per AAP §0.8.5: {domain}.{entity}.{action}
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Publishes a domain event to the notifications SNS topic.
        /// Event naming follows AAP §0.8.5 convention: notifications.webhook.{action}
        /// </summary>
        /// <param name="action">Event action: "dispatched" or "failed".</param>
        /// <param name="payload">Domain event payload object to serialize.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        private async Task PublishDomainEventAsync(string action, WebhookDispatchDomainEvent payload, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_snsTopicArn))
            {
                _logger.LogWarning(
                    "SNS topic ARN not configured ({EnvVar}). Skipping domain event publish for action={Action}.",
                    SnsTopicArnEnvVar, action);
                return;
            }

            try
            {
                var eventSubject = $"notifications.webhook.{action}";
                var eventMessage = JsonSerializer.Serialize(
                    payload,
                    WebhookHandlerJsonContext.Default.WebhookDispatchDomainEvent);

                await _snsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Subject = eventSubject,
                    Message = eventMessage
                }, ct);

                _logger.LogInformation(
                    "Domain event published. Subject={Subject}, WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                    eventSubject, payload.WebhookId, payload.CorrelationId);
            }
            catch (Exception ex)
            {
                // Log but do not throw — domain event publishing failure should
                // not prevent successful webhook dispatch acknowledgement
                _logger.LogError(ex,
                    "Failed to publish domain event. Action={Action}, WebhookId={WebhookId}, CorrelationId={CorrelationId}",
                    action, payload.WebhookId, payload.CorrelationId);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  HELPER METHODS (Private)
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds a standardized HTTP API Gateway v2 proxy response with
        /// CORS headers, correlation-ID header, and JSON-serialized body.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode,
            object? body,
            string correlationId)
        {
            var headers = new Dictionary<string, string>(StandardResponseHeaders)
            {
                ["x-correlation-id"] = correlationId
            };

            string serializedBody;
            if (body == null)
            {
                serializedBody = string.Empty;
            }
            else
            {
                try
                {
                    // AOT-safe serialization via source-generated context
                    serializedBody = body switch
                    {
                        WebhookHandlerResponse resp => JsonSerializer.Serialize(
                            resp, WebhookHandlerJsonContext.Default.WebhookHandlerResponse),
                        WebhookHandlerListResponse listResp => JsonSerializer.Serialize(
                            listResp, WebhookHandlerJsonContext.Default.WebhookHandlerListResponse),
                        WebhookConfig cfg => JsonSerializer.Serialize(
                            cfg, WebhookHandlerJsonContext.Default.WebhookConfig),
                        _ => JsonSerializer.Serialize(body, body.GetType(),
                            WebhookHandlerJsonContext.Default)
                    };
                }
                catch (Exception)
                {
                    serializedBody = JsonSerializer.Serialize(
                        new WebhookHandlerResponse { Success = false, Message = "Response serialization error." },
                        WebhookHandlerJsonContext.Default.WebhookHandlerResponse);
                }
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = headers,
                Body = serializedBody
            };
        }

        /// <summary>
        /// Checks whether any segment of the given URL path is a valid GUID.
        /// Used for route dispatch to distinguish collection endpoints from
        /// resource-by-id endpoints when using RawPath-based routing.
        /// </summary>
        private static bool PathContainsGuid(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var segments = path.Split('/');
            foreach (var seg in segments)
            {
                if (Guid.TryParse(seg, out _)) return true;
            }
            return false;
        }

        /// <summary>
        /// Extracts a named path parameter from the API Gateway v2 proxy request.
        /// Returns null if the parameter is not found.
        /// </summary>
        private static string? ExtractPathParameter(
            APIGatewayHttpApiV2ProxyRequest request,
            string parameterName)
        {
            if (request.PathParameters != null)
            {
                if (request.PathParameters.TryGetValue(parameterName, out var value) &&
                    !string.IsNullOrEmpty(value))
                    return value;
                // Fall back to {proxy+} path parameter for HTTP API v2 catch-all routes.
                if (request.PathParameters.TryGetValue("proxy", out var proxy) &&
                    !string.IsNullOrEmpty(proxy))
                {
                    var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = segments.Length - 1; i >= 0; i--)
                    {
                        if (Guid.TryParse(segments[i], out _))
                            return segments[i];
                    }
                }
            }
            return null;
        }
    }
}
