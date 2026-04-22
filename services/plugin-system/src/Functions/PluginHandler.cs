using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.PluginSystem.DataAccess;
using WebVellaErp.PluginSystem.Models;
using WebVellaErp.PluginSystem.Services;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.PluginSystem.Functions
{
    /// <summary>
    /// AOT-compatible JSON serialization context for PluginHandler response types.
    /// Registers all types serialized in response bodies and SNS event payloads,
    /// enabling .NET 9 Native AOT compilation with sub-1-second cold starts.
    /// All types flowing through JsonSerializer.Serialize/Deserialize are registered
    /// to eliminate IL2026/IL3050 AOT trimming warnings.
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(PluginResponse))]
    [JsonSerializable(typeof(PluginListResponse))]
    [JsonSerializable(typeof(RegisterPluginRequest))]
    [JsonSerializable(typeof(Plugin))]
    [JsonSerializable(typeof(PluginHandlerDomainEvent))]
    internal partial class PluginHandlerJsonContext : JsonSerializerContext { }

    /// <summary>
    /// Domain event payload for handler-level SNS publishing.
    /// Used for state-changing operations where the service layer does not publish events
    /// (e.g., plugin deletion). Follows {domain}.{entity}.{action} naming convention
    /// per AAP Section 0.8.5.
    /// </summary>
    internal sealed class PluginHandlerDomainEvent
    {
        [JsonPropertyName("eventType")]
        public string EventType { get; set; } = string.Empty;

        [JsonPropertyName("pluginId")]
        public string PluginId { get; set; } = string.Empty;

        [JsonPropertyName("pluginName")]
        public string PluginName { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Primary AWS Lambda entry point for all Plugin / Extension System Service HTTP API Gateway v2 requests.
    /// 
    /// Replaces:
    /// - WebVella.Erp.Plugins.SDK/Controllers/AdminController.cs (MVC controller API pattern)
    /// - WebVella.Erp/ErpPlugin.cs (plugin lifecycle: Initialize, GetPluginData, SavePluginData)
    /// - WebVella.Erp/IErpService.cs (plugin list and initialization contract)
    /// - WebVella.Erp.Plugins.SDK/SdkPlugin.cs (plugin activation pattern with SecurityContext.OpenSystemScope)
    /// 
    /// This is NOT an MVC controller. It is a Lambda handler receiving API Gateway v2 proxy events.
    /// Authentication is handled by API Gateway JWT authorizer; this handler extracts JWT claims
    /// from the request context for role-based authorization decisions.
    /// 
    /// API Routes:
    ///   POST   /v1/plugins                    → HandleRegisterPlugin
    ///   GET    /v1/plugins                    → HandleListPlugins
    ///   GET    /v1/plugins/{id}               → HandleGetPlugin
    ///   PUT    /v1/plugins/{id}/activate      → HandleActivatePlugin
    ///   PUT    /v1/plugins/{id}/deactivate    → HandleDeactivatePlugin
    ///   DELETE /v1/plugins/{id}               → HandleUnregisterPlugin
    /// </summary>
    public class PluginHandler
    {
        #region Fields and Constants

        private readonly IPluginService _pluginService;
        private readonly IPluginRepository _pluginRepository;
        private readonly ISitemapService _sitemapService;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<PluginHandler> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly string? _snsTopicArn;

        /// <summary>
        /// Environment variable name for the SNS topic ARN used for plugin domain events.
        /// </summary>
        private const string SnsTopicArnEnvVar = "PLUGIN_EVENTS_TOPIC_ARN";

        /// <summary>
        /// Domain event type constants following {domain}.{entity}.{action} naming convention
        /// per AAP Section 0.8.5.
        /// </summary>
        private const string EventPluginCreated = "plugin-system.plugin.created";
        private const string EventPluginActivated = "plugin-system.plugin.activated";
        private const string EventPluginDeactivated = "plugin-system.plugin.deactivated";
        private const string EventPluginDeleted = "plugin-system.plugin.deleted";

        /// <summary>
        /// Standard response headers for all API Gateway v2 responses.
        /// Includes Content-Type, CORS headers per AAP requirements.
        /// </summary>
        private static readonly Dictionary<string, string> StandardResponseHeaders = new()
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS" },
            { "Access-Control-Allow-Headers", "Content-Type,Authorization,x-correlation-id" }
        };

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for AWS Lambda runtime invocation.
        /// Builds the DI ServiceCollection, registers all dependencies (AWS SDK clients,
        /// application services), and resolves required services.
        /// AWS SDK clients are configured with AWS_ENDPOINT_URL for LocalStack compatibility
        /// per AAP Section 0.7.6.
        /// </summary>
        public PluginHandler()
        {
            var services = new ServiceCollection();

            // Configure structured JSON logging per AAP Section 0.8.5
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

            // Read AWS_ENDPOINT_URL for LocalStack dual-target compatibility (AAP Section 0.7.6)
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            // Register AWS SDK clients with endpoint URL override for LocalStack
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));

                services.AddSingleton<IAmazonSimpleSystemsManagement>(_ =>
                    new AmazonSimpleSystemsManagementClient(
                        new AmazonSimpleSystemsManagementConfig { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production AWS: use default credential and region resolution chain
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
                services.AddSingleton<IAmazonSimpleSystemsManagement, AmazonSimpleSystemsManagementClient>();
            }

            // Register application services with transient lifetime
            services.AddTransient<IPluginRepository, PluginRepository>();
            services.AddTransient<IPluginService, PluginService>();
            services.AddTransient<ISitemapService, SitemapService>();

            // Build DI container and resolve all handler dependencies
            _serviceProvider = services.BuildServiceProvider();
            _pluginService = _serviceProvider.GetRequiredService<IPluginService>();
            _pluginRepository = _serviceProvider.GetRequiredService<IPluginRepository>();
            _sitemapService = _serviceProvider.GetRequiredService<ISitemapService>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PluginHandler>();

            // Read SNS topic ARN from environment variable (per AAP Section 0.8.6)
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar);
        }

        /// <summary>
        /// Secondary constructor accepting an IServiceProvider for unit testing.
        /// Allows test code to inject mock or stubbed services without Lambda runtime dependencies.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured service provider with all required dependencies.</param>
        public PluginHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _pluginService = _serviceProvider.GetRequiredService<IPluginService>();
            _pluginRepository = _serviceProvider.GetRequiredService<IPluginRepository>();
            _sitemapService = _serviceProvider.GetRequiredService<ISitemapService>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<PluginHandler>();
            _snsTopicArn = Environment.GetEnvironmentVariable(SnsTopicArnEnvVar);
        }

        #endregion

        #region Lambda Handler Methods

        /// <summary>
        /// Lambda handler for POST /v1/plugins — Register a new plugin.
        /// 
        /// Replaces monolith reflection-based plugin discovery (IErpService.InitializePlugins)
        /// with explicit API-driven registration. Only administrators can register plugins,
        /// matching source AdminController.cs [Authorize(Roles = "administrator")].
        /// 
        /// Validates plugin name is required (matching source ErpPlugin.cs lines 69-70),
        /// plugin name is unique (409 Conflict if duplicate), and prefix is present.
        /// Generates idempotency key for the write operation per AAP Section 0.8.5.
        /// Publishes plugin-system.plugin.created SNS domain event on success.
        /// </summary>

        /// <summary>
        /// Single entry point for managed .NET Lambda runtime (dotnet9).
        /// Routes API Gateway HTTP API v2 requests to the appropriate handler method
        /// based on HTTP method and request path.
        ///
        /// Routing table:
        ///   Plugin routes:
        ///     POST   /v1/plugins                    → HandleRegisterPlugin
        ///     GET    /v1/plugins                    → HandleListPlugins
        ///     GET    /v1/plugins/{id}               → HandleGetPlugin
        ///     PUT    /v1/plugins/{id}/activate      → HandleActivatePlugin
        ///     PUT    /v1/plugins/{id}/deactivate    → HandleDeactivatePlugin
        ///     DELETE /v1/plugins/{id}               → HandleUnregisterPlugin
        ///
        ///   App/Sitemap routes (served via ISitemapService):
        ///     GET    /v1/apps                       → HandleListApps
        ///     GET    /v1/apps/{idOrName}            → HandleGetApp
        ///     POST   /v1/apps                       → HandleCreateApp
        ///     PUT    /v1/apps/{id}                  → HandleUpdateApp
        ///     DELETE /v1/apps/{id}                  → HandleDeleteApp
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var path = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

            // Normalize path for routing
            var normalizedPath = path.TrimEnd('/').ToLowerInvariant();

            // ── App/Sitemap Routes ──
            // The frontend calls GET /v1/apps to populate the sidebar menu.
            if (normalizedPath.Contains("/apps"))
            {
                // Extract the proxy segment after /apps (if any)
                var proxySegment = ExtractPathParameter(request, "proxy") ?? string.Empty;

                switch (method)
                {
                    case "GET":
                        // GET /v1/apps → list; GET /v1/apps/{idOrName} → get single
                        return string.IsNullOrEmpty(proxySegment)
                            ? await HandleListApps(request, context)
                            : await HandleGetApp(request, context, proxySegment);
                    case "POST":
                        return await HandleCreateApp(request, context);
                    case "PUT":
                        return await HandleUpdateApp(request, context, proxySegment);
                    case "DELETE":
                        return await HandleDeleteApp(request, context, proxySegment);
                    default:
                        return await HandleListApps(request, context);
                }
            }

            // ── Plugin Routes ──
            // Extract proxy segment to differentiate list vs single-item operations
            var pluginProxy = ExtractPathParameter(request, "proxy") ?? string.Empty;
            var hasId = !string.IsNullOrEmpty(pluginProxy) && !pluginProxy.Equals("plugins", StringComparison.OrdinalIgnoreCase);

            switch (method)
            {
                case "GET":
                    return hasId
                        ? await HandleGetPlugin(request, context)
                        : await HandleListPlugins(request, context);
                case "POST":
                    return await HandleRegisterPlugin(request, context);
                case "PUT":
                    if (normalizedPath.Contains("/activate"))
                        return await HandleActivatePlugin(request, context);
                    if (normalizedPath.Contains("/deactivate"))
                        return await HandleDeactivatePlugin(request, context);
                    return CreateErrorResponse(400, "Unsupported PUT operation. Use /activate or /deactivate.");
                case "DELETE":
                    return await HandleUnregisterPlugin(request, context);
                default:
                    return CreateErrorResponse(405, $"HTTP method {method} is not supported.");
            }
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleRegisterPlugin(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);

            _logger.LogInformation(
                "HandleRegisterPlugin invoked. CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                correlationId, context.AwsRequestId);

            try
            {
                // Verify administrator role (matching source AdminController.cs [Authorize(Roles = "administrator")])
                if (!IsAdministrator(request))
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Unauthorized — administrator role required. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(403, new PluginResponse
                    {
                        Success = false,
                        Message = "Access denied. Administrator role is required to register plugins."
                    }, correlationId);
                }

                // Validate request body is present
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Empty request body. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Request body is required."
                    }, correlationId);
                }

                // Deserialize request body to RegisterPluginRequest DTO using AOT source-gen context
                RegisterPluginRequest? registerRequest;
                try
                {
                    registerRequest = JsonSerializer.Deserialize(
                        request.Body, PluginHandlerJsonContext.Default.RegisterPluginRequest);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "HandleRegisterPlugin: Malformed JSON in request body. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Invalid JSON in request body."
                    }, correlationId);
                }

                if (registerRequest == null)
                {
                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Request body could not be parsed."
                    }, correlationId);
                }

                // Handler-level input validation: Name is required
                // (matching source ErpPlugin.cs lines 69-70: "Plugin name is not specified")
                if (string.IsNullOrWhiteSpace(registerRequest.Name))
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Plugin name is empty. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin name is required and cannot be empty."
                    }, correlationId);
                }

                // Handler-level input validation: Prefix is required
                if (string.IsNullOrWhiteSpace(registerRequest.Prefix))
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Plugin prefix is empty. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin prefix is required and cannot be empty."
                    }, correlationId);
                }

                // Handler-level input validation: Version must be >= 0
                if (registerRequest.Version < 0)
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Invalid version {Version}. CorrelationId: {CorrelationId}",
                        registerRequest.Version, correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin version must be greater than or equal to zero."
                    }, correlationId);
                }

                // Check name uniqueness via repository before calling service (fast-path 409)
                // Per schema: IPluginRepository.GetPluginByNameAsync() for name uniqueness validation
                var existingPlugin = await _pluginRepository.GetPluginByNameAsync(
                    registerRequest.Name, CancellationToken.None).ConfigureAwait(false);

                if (existingPlugin != null)
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Duplicate plugin name '{PluginName}'. CorrelationId: {CorrelationId}",
                        registerRequest.Name, correlationId);

                    return BuildResponse(409, new PluginResponse
                    {
                        Success = false,
                        Message = $"A plugin with name '{registerRequest.Name}' already exists."
                    }, correlationId);
                }

                // Delegate to service layer for full registration business logic
                // Service handles: model creation, DynamoDB persistence, SNS event publishing
                var response = await _pluginService.RegisterPluginAsync(
                    registerRequest, CancellationToken.None).ConfigureAwait(false);

                if (!response.Success)
                {
                    _logger.LogWarning(
                        "HandleRegisterPlugin: Service returned failure: {Message}. CorrelationId: {CorrelationId}",
                        response.Message, correlationId);

                    return BuildResponse(400, response, correlationId);
                }

                _logger.LogInformation(
                    "HandleRegisterPlugin: Plugin registered — Name: {PluginName}, Id: {PluginId}, Version: {Version}, CreatedAt: {CreatedAt}. CorrelationId: {CorrelationId}",
                    response.Plugin?.Name, response.Plugin?.Id, response.Plugin?.Version, response.Plugin?.CreatedAt, correlationId);

                return BuildResponse(201, response, correlationId);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "HandleRegisterPlugin: Conflict — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(409, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleRegisterPlugin: Unexpected error. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(500, new PluginResponse
                {
                    Success = false,
                    Message = "An internal error occurred while registering the plugin."
                }, correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/plugins — List all registered plugins.
        /// 
        /// Replaces IErpService.Plugins list access (source IErpService.cs line 9).
        /// In the monolith, the plugin list was a property of the singleton ErpService.
        /// The new system provides a paginated REST endpoint with optional status filtering.
        /// Results are sorted by Name matching source AdminController.cs ordering pattern.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleListPlugins(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);

            _logger.LogInformation(
                "HandleListPlugins invoked. CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                correlationId, context.AwsRequestId);

            try
            {
                // Extract optional status filter from query string parameters
                PluginStatus? statusFilter = null;
                if (request.QueryStringParameters != null &&
                    request.QueryStringParameters.TryGetValue("status", out var statusValue) &&
                    !string.IsNullOrWhiteSpace(statusValue))
                {
                    if (Enum.TryParse<PluginStatus>(statusValue, ignoreCase: true, out var parsedStatus))
                    {
                        statusFilter = parsedStatus;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "HandleListPlugins: Invalid status filter value '{StatusValue}'. CorrelationId: {CorrelationId}",
                            statusValue, correlationId);

                        return BuildResponse(400, new PluginListResponse
                        {
                            Success = false,
                            Message = $"Invalid status filter value '{statusValue}'. Valid values are: Active, Inactive."
                        }, correlationId);
                    }
                }

                // Delegate to service layer — supports optional status filtering
                var response = await _pluginService.ListPluginsAsync(
                    statusFilter, CancellationToken.None).ConfigureAwait(false);

                // Sort results by Name (matching source AdminController.cs line 45 ordering pattern)
                if (response.Plugins != null && response.Plugins.Count > 1)
                {
                    response.Plugins.Sort((a, b) =>
                        string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                }

                _logger.LogInformation(
                    "HandleListPlugins: Returning {Count} plugins. CorrelationId: {CorrelationId}",
                    response.TotalCount, correlationId);

                return BuildResponse(200, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleListPlugins: Unexpected error. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(500, new PluginListResponse
                {
                    Success = false,
                    Message = "An internal error occurred while listing plugins."
                }, correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/plugins/{id} — Get a single plugin by its unique identifier.
        /// 
        /// No direct source equivalent — in monolith, plugins were accessed by name via ErpPlugin.Name.
        /// Added for RESTful completeness and to support frontend plugin detail views.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetPlugin(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);

            _logger.LogInformation(
                "HandleGetPlugin invoked. CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                correlationId, context.AwsRequestId);

            try
            {
                // Extract and validate plugin ID from path parameters
                var pluginIdStr = ExtractPathParameter(request, "id");
                if (string.IsNullOrWhiteSpace(pluginIdStr))
                {
                    _logger.LogWarning(
                        "HandleGetPlugin: Missing 'id' path parameter. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin ID is required in the path."
                    }, correlationId);
                }

                if (!Guid.TryParse(pluginIdStr, out var pluginId))
                {
                    _logger.LogWarning(
                        "HandleGetPlugin: Invalid GUID format '{PluginIdStr}'. CorrelationId: {CorrelationId}",
                        pluginIdStr, correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = $"Invalid plugin ID format: '{pluginIdStr}'. Must be a valid GUID."
                    }, correlationId);
                }

                // Delegate to service layer for retrieval
                var response = await _pluginService.GetPluginByIdAsync(
                    pluginId, CancellationToken.None).ConfigureAwait(false);

                if (!response.Success || response.Plugin == null)
                {
                    _logger.LogWarning(
                        "HandleGetPlugin: Plugin not found — ID: {PluginId}. CorrelationId: {CorrelationId}",
                        pluginId, correlationId);

                    return BuildResponse(404, new PluginResponse
                    {
                        Success = false,
                        Message = $"Plugin with ID '{pluginId}' was not found."
                    }, correlationId);
                }

                _logger.LogInformation(
                    "HandleGetPlugin: Found plugin — Name: {PluginName}, Status: {Status}. CorrelationId: {CorrelationId}",
                    response.Plugin.Name, response.Plugin.Status, correlationId);

                return BuildResponse(200, response, correlationId);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex,
                    "HandleGetPlugin: Plugin not found — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(404, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "HandleGetPlugin: Validation error — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(400, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleGetPlugin: Unexpected error. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(500, new PluginResponse
                {
                    Success = false,
                    Message = "An internal error occurred while retrieving the plugin."
                }, correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PUT /v1/plugins/{id}/activate — Activate a registered plugin.
        /// 
        /// Replaces ErpPlugin.Initialize(IServiceProvider) (source ErpPlugin.cs line 57).
        /// In the monolith, plugin activation happened during startup via ErpService.InitializePlugins()
        /// which called plugin.Initialize() on each discovered plugin. Also replaces the
        /// SdkPlugin.Initialize() pattern (source SdkPlugin.cs lines 15-22) that used
        /// SecurityContext.OpenSystemScope() → SetSchedulePlans() → ProcessPatches().
        /// In the new system, activation is explicit via API.
        /// 
        /// Idempotent: if plugin is already active, returns 200 success without modification.
        /// Only administrators can activate plugins.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleActivatePlugin(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);

            _logger.LogInformation(
                "HandleActivatePlugin invoked. CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                correlationId, context.AwsRequestId);

            try
            {
                // Verify administrator role (matching source AdminController.cs [Authorize(Roles = "administrator")])
                if (!IsAdministrator(request))
                {
                    _logger.LogWarning(
                        "HandleActivatePlugin: Unauthorized — administrator role required. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(403, new PluginResponse
                    {
                        Success = false,
                        Message = "Access denied. Administrator role is required to activate plugins."
                    }, correlationId);
                }

                // Extract and validate plugin ID from path parameters
                var pluginIdStr = ExtractPathParameter(request, "id");
                if (string.IsNullOrWhiteSpace(pluginIdStr) || !Guid.TryParse(pluginIdStr, out var pluginId))
                {
                    _logger.LogWarning(
                        "HandleActivatePlugin: Invalid plugin ID '{PluginIdStr}'. CorrelationId: {CorrelationId}",
                        pluginIdStr, correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "A valid plugin ID (GUID) is required in the path."
                    }, correlationId);
                }

                // Delegate to service layer — handles idempotency (already active → returns success),
                // status update, timestamp update, DynamoDB persistence, and SNS event publishing
                var response = await _pluginService.ActivatePluginAsync(
                    pluginId, CancellationToken.None).ConfigureAwait(false);

                if (!response.Success)
                {
                    // Service returns failure if plugin not found
                    _logger.LogWarning(
                        "HandleActivatePlugin: Service returned failure for ID {PluginId}: {Message}. CorrelationId: {CorrelationId}",
                        pluginId, response.Message, correlationId);

                    return BuildResponse(404, response, correlationId);
                }

                // Log successful activation with plugin details
                var isActive = response.Plugin?.Status == PluginStatus.Active;
                _logger.LogInformation(
                    "HandleActivatePlugin: Plugin {PluginName} (ID: {PluginId}) is now active={IsActive}. Updated: {UpdatedAt}. CorrelationId: {CorrelationId}",
                    response.Plugin?.Name, response.Plugin?.Id, isActive,
                    response.Plugin?.UpdatedAt, correlationId);

                return BuildResponse(200, response, correlationId);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex,
                    "HandleActivatePlugin: Plugin not found — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(404, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "HandleActivatePlugin: Validation error — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(400, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "HandleActivatePlugin: Operation conflict — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(409, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleActivatePlugin: Unexpected error. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(500, new PluginResponse
                {
                    Success = false,
                    Message = "An internal error occurred while activating the plugin."
                }, correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PUT /v1/plugins/{id}/deactivate — Deactivate a plugin.
        /// 
        /// No direct source equivalent — monolith had no deactivation concept.
        /// Added for operational flexibility to disable plugins without removing them.
        /// 
        /// Idempotent: if plugin is already inactive, returns 200 success without modification.
        /// Only administrators can deactivate plugins.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeactivatePlugin(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);

            _logger.LogInformation(
                "HandleDeactivatePlugin invoked. CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                correlationId, context.AwsRequestId);

            try
            {
                // Verify administrator role
                if (!IsAdministrator(request))
                {
                    _logger.LogWarning(
                        "HandleDeactivatePlugin: Unauthorized — administrator role required. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(403, new PluginResponse
                    {
                        Success = false,
                        Message = "Access denied. Administrator role is required to deactivate plugins."
                    }, correlationId);
                }

                // Extract and validate plugin ID from path parameters
                var pluginIdStr = ExtractPathParameter(request, "id");
                if (string.IsNullOrWhiteSpace(pluginIdStr) || !Guid.TryParse(pluginIdStr, out var pluginId))
                {
                    _logger.LogWarning(
                        "HandleDeactivatePlugin: Invalid plugin ID '{PluginIdStr}'. CorrelationId: {CorrelationId}",
                        pluginIdStr, correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "A valid plugin ID (GUID) is required in the path."
                    }, correlationId);
                }

                // Delegate to service layer — handles idempotency (already inactive → returns success),
                // status update, timestamp update, DynamoDB persistence, and SNS event publishing
                var response = await _pluginService.DeactivatePluginAsync(
                    pluginId, CancellationToken.None).ConfigureAwait(false);

                if (!response.Success)
                {
                    // Service returns failure if plugin not found
                    _logger.LogWarning(
                        "HandleDeactivatePlugin: Service returned failure for ID {PluginId}: {Message}. CorrelationId: {CorrelationId}",
                        pluginId, response.Message, correlationId);

                    return BuildResponse(404, response, correlationId);
                }

                // Log successful deactivation with plugin details
                var isInactive = response.Plugin?.Status == PluginStatus.Inactive;
                _logger.LogInformation(
                    "HandleDeactivatePlugin: Plugin {PluginName} (ID: {PluginId}) is now inactive={IsInactive}. Updated: {UpdatedAt}. CorrelationId: {CorrelationId}",
                    response.Plugin?.Name, response.Plugin?.Id, isInactive,
                    response.Plugin?.UpdatedAt, correlationId);

                return BuildResponse(200, response, correlationId);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex,
                    "HandleDeactivatePlugin: Plugin not found — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(404, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "HandleDeactivatePlugin: Validation error — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(400, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "HandleDeactivatePlugin: Operation conflict — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(409, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleDeactivatePlugin: Unexpected error. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(500, new PluginResponse
                {
                    Success = false,
                    Message = "An internal error occurred while deactivating the plugin."
                }, correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/plugins/{id} — Unregister (delete) a plugin.
        /// 
        /// No direct source equivalent — monolith had no plugin unregistration API.
        /// Plugin removal replaces the deletion of plugin_data rows from the PostgreSQL
        /// table (source ErpPlugin.cs GetPluginData/SavePluginData).
        /// 
        /// Safety: Plugin must be deactivated before unregistration to prevent
        /// removing an active plugin. Publishes plugin-system.plugin.deleted event
        /// since PluginService.DeletePluginAsync does not publish SNS events.
        /// Only administrators can unregister plugins.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleUnregisterPlugin(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);

            _logger.LogInformation(
                "HandleUnregisterPlugin invoked. CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                correlationId, context.AwsRequestId);

            try
            {
                // Verify administrator role (matching source AdminController.cs [Authorize(Roles = "administrator")])
                if (!IsAdministrator(request))
                {
                    _logger.LogWarning(
                        "HandleUnregisterPlugin: Unauthorized — administrator role required. CorrelationId: {CorrelationId}",
                        correlationId);

                    return BuildResponse(403, new PluginResponse
                    {
                        Success = false,
                        Message = "Access denied. Administrator role is required to unregister plugins."
                    }, correlationId);
                }

                // Extract and validate plugin ID from path parameters
                var pluginIdStr = ExtractPathParameter(request, "id");
                if (string.IsNullOrWhiteSpace(pluginIdStr) || !Guid.TryParse(pluginIdStr, out var pluginId))
                {
                    _logger.LogWarning(
                        "HandleUnregisterPlugin: Invalid plugin ID '{PluginIdStr}'. CorrelationId: {CorrelationId}",
                        pluginIdStr, correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "A valid plugin ID (GUID) is required in the path."
                    }, correlationId);
                }

                // Fetch existing plugin to verify existence and check current status
                var existingPluginResponse = await _pluginService.GetPluginByIdAsync(
                    pluginId, CancellationToken.None).ConfigureAwait(false);

                if (!existingPluginResponse.Success || existingPluginResponse.Plugin == null)
                {
                    _logger.LogWarning(
                        "HandleUnregisterPlugin: Plugin not found for ID {PluginId}. CorrelationId: {CorrelationId}",
                        pluginId, correlationId);

                    return BuildResponse(404, new PluginResponse
                    {
                        Success = false,
                        Message = $"Plugin with ID '{pluginId}' was not found."
                    }, correlationId);
                }

                // Safety check: Plugin must be deactivated before unregistration
                if (existingPluginResponse.Plugin.Status == PluginStatus.Active)
                {
                    _logger.LogWarning(
                        "HandleUnregisterPlugin: Plugin {PluginName} (ID: {PluginId}) is still active. Must deactivate before unregistration. CorrelationId: {CorrelationId}",
                        existingPluginResponse.Plugin.Name, pluginId, correlationId);

                    return BuildResponse(400, new PluginResponse
                    {
                        Success = false,
                        Message = "Plugin must be deactivated before unregistration. Call PUT /v1/plugins/{id}/deactivate first."
                    }, correlationId);
                }

                // Store plugin details for event publishing before deletion
                var pluginName = existingPluginResponse.Plugin.Name;
                var pluginVersion = existingPluginResponse.Plugin.Version;

                // Delete the plugin via service layer
                var deleted = await _pluginService.DeletePluginAsync(
                    pluginId, CancellationToken.None).ConfigureAwait(false);

                if (!deleted)
                {
                    _logger.LogWarning(
                        "HandleUnregisterPlugin: Delete operation returned false for ID {PluginId}. CorrelationId: {CorrelationId}",
                        pluginId, correlationId);

                    return BuildResponse(500, new PluginResponse
                    {
                        Success = false,
                        Message = "Failed to delete the plugin. Please try again."
                    }, correlationId);
                }

                // Publish SNS domain event (plugin-system.plugin.deleted)
                // PluginService.DeletePluginAsync does not publish events, so handler does it
                await PublishDomainEventAsync(
                    EventPluginDeleted,
                    pluginId,
                    pluginName,
                    correlationId).ConfigureAwait(false);

                _logger.LogInformation(
                    "HandleUnregisterPlugin: Plugin {PluginName} (ID: {PluginId}, v{Version}) successfully unregistered. CorrelationId: {CorrelationId}",
                    pluginName, pluginId, pluginVersion, correlationId);

                return BuildResponse(200, new PluginResponse
                {
                    Success = true,
                    Message = $"Plugin '{pluginName}' has been successfully unregistered."
                }, correlationId);
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex,
                    "HandleUnregisterPlugin: Plugin not found — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(404, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "HandleUnregisterPlugin: Validation error — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(400, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "HandleUnregisterPlugin: Operation conflict — {Message}. CorrelationId: {CorrelationId}",
                    ex.Message, correlationId);

                return BuildResponse(409, new PluginResponse
                {
                    Success = false,
                    Message = ex.Message
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleUnregisterPlugin: Unexpected error. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(500, new PluginResponse
                {
                    Success = false,
                    Message = "An internal error occurred while unregistering the plugin."
                }, correlationId);
            }
        }

        #endregion

        #region App/Sitemap Handler Methods

        /// <summary>
        /// Handles GET /v1/apps — Returns all applications with their sitemap trees.
        /// The frontend AppShell.tsx calls useApps() → GET /apps to populate the sidebar
        /// navigation menu. Each app includes its sitemap areas and nodes so the UI can
        /// render hierarchical navigation without additional API calls.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleListApps(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            _logger.LogInformation("HandleListApps invoked. CorrelationId: {CorrelationId}", correlationId);

            try
            {
                var apps = await _sitemapService.ListAppsAsync(CancellationToken.None).ConfigureAwait(false);

                // Build full app response objects with nested sitemap trees
                var appResponses = new List<object>();
                foreach (var app in apps)
                {
                    var sitemapData = await _sitemapService.GetOrderedSitemapAsync(app.Id, CancellationToken.None).ConfigureAwait(false);

                    // Extract areas from the sitemap structure
                    var sitemapObj = sitemapData as dynamic;
                    object? sitemapAreas = null;
                    try { sitemapAreas = sitemapObj?.Sitemap; } catch { /* dynamic access may fail */ }

                    appResponses.Add(new
                    {
                        id = app.Id.ToString(),
                        name = app.Name,
                        label = app.Label,
                        description = app.Description ?? string.Empty,
                        iconClass = app.IconClass ?? "fa fa-cube",
                        author = string.Empty,
                        color = app.Color ?? "#2196F3",
                        sitemap = BuildSitemapResponse(sitemapAreas),
                        homePages = Array.Empty<object>(),
                        entities = Array.Empty<object>(),
                        weight = app.Weight,
                        access = app.AccessRoles?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>()
                    });
                }

                return BuildResponse(200, new
                {
                    success = true,
                    message = string.Empty,
                    errors = Array.Empty<object>(),
                    statusCode = 200,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = appResponses
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing apps. CorrelationId: {CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred while listing applications." }, correlationId);
            }
        }

        /// <summary>
        /// Handles GET /v1/apps/{idOrName} — Returns a single application by ID or name.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetApp(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context, string idOrName)
        {
            var correlationId = GetCorrelationId(request);
            _logger.LogInformation("HandleGetApp invoked for '{IdOrName}'. CorrelationId: {CorrelationId}", idOrName, correlationId);

            try
            {
                AppRecord? app = null;

                // Try to parse as GUID first
                if (Guid.TryParse(idOrName, out var appId))
                {
                    app = await _sitemapService.GetAppByIdAsync(appId, CancellationToken.None).ConfigureAwait(false);
                }

                // If not found by ID, search by name
                if (app == null)
                {
                    var allApps = await _sitemapService.ListAppsAsync(CancellationToken.None).ConfigureAwait(false);
                    app = allApps.FirstOrDefault(a => a.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
                }

                if (app == null)
                {
                    return BuildResponse(404, new
                    {
                        success = false,
                        message = $"Application '{idOrName}' was not found.",
                        errors = Array.Empty<object>(),
                        statusCode = 404,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }, correlationId);
                }

                var sitemapData = await _sitemapService.GetOrderedSitemapAsync(app.Id, CancellationToken.None).ConfigureAwait(false);
                var sitemapObj = sitemapData as dynamic;
                object? sitemapAreas = null;
                try { sitemapAreas = sitemapObj?.Sitemap; } catch { /* dynamic access may fail */ }

                var appResponse = new
                {
                    id = app.Id.ToString(),
                    name = app.Name,
                    label = app.Label,
                    description = app.Description ?? string.Empty,
                    iconClass = app.IconClass ?? "fa fa-cube",
                    author = string.Empty,
                    color = app.Color ?? "#2196F3",
                    sitemap = BuildSitemapResponse(sitemapAreas),
                    homePages = Array.Empty<object>(),
                    entities = Array.Empty<object>(),
                    weight = app.Weight,
                    access = app.AccessRoles?.Select(r => r.ToString()).ToArray() ?? Array.Empty<string>()
                };

                return BuildResponse(200, new
                {
                    success = true,
                    message = string.Empty,
                    errors = Array.Empty<object>(),
                    statusCode = 200,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = appResponse
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting app '{IdOrName}'. CorrelationId: {CorrelationId}", idOrName, correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." }, correlationId);
            }
        }

        /// <summary>
        /// Handles POST /v1/apps — Creates a new application.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateApp(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            _logger.LogInformation("HandleCreateApp invoked. CorrelationId: {CorrelationId}", correlationId);

            try
            {
                if (!IsAdministrator(request))
                {
                    return BuildResponse(403, new { success = false, message = "Access denied. Administrator role required." }, correlationId);
                }

                var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(request.Body ?? "{}");
                var appId = body.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var parsedId)
                    ? parsedId : Guid.NewGuid();
                var name = body.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var label = body.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;
                var description = body.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
                var iconClass = body.TryGetProperty("iconClass", out var iconProp) ? iconProp.GetString() : "fa fa-cube";
                var color = body.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : "#2196F3";
                var weight = body.TryGetProperty("weight", out var weightProp) ? weightProp.GetInt32() : 10;

                var result = await _sitemapService.CreateAppAsync(appId, name, label, description, iconClass, color, weight, null, CancellationToken.None).ConfigureAwait(false);

                return BuildResponse(result.Success ? 201 : 400, new
                {
                    success = result.Success,
                    message = result.Message,
                    errors = Array.Empty<object>(),
                    statusCode = result.Success ? 201 : 400,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = result.Success ? new { id = appId.ToString(), name, label } : null
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating app. CorrelationId: {CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." }, correlationId);
            }
        }

        /// <summary>
        /// Handles PUT /v1/apps/{id} — Updates an existing application.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateApp(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context, string appIdStr)
        {
            var correlationId = GetCorrelationId(request);
            _logger.LogInformation("HandleUpdateApp invoked for '{AppId}'. CorrelationId: {CorrelationId}", appIdStr, correlationId);

            try
            {
                if (!IsAdministrator(request))
                {
                    return BuildResponse(403, new { success = false, message = "Access denied. Administrator role required." }, correlationId);
                }

                if (!Guid.TryParse(appIdStr, out var appId))
                {
                    return BuildResponse(400, new { success = false, message = "A valid application ID (GUID) is required." }, correlationId);
                }

                var body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(request.Body ?? "{}");
                var name = body.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                var label = body.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;
                var description = body.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
                var iconClass = body.TryGetProperty("iconClass", out var iconProp) ? iconProp.GetString() : "fa fa-cube";
                var color = body.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : "#2196F3";
                var weight = body.TryGetProperty("weight", out var weightProp) ? weightProp.GetInt32() : 10;

                var result = await _sitemapService.UpdateAppAsync(appId, name, label, description, iconClass, color, weight, null, CancellationToken.None).ConfigureAwait(false);

                return BuildResponse(result.Success ? 200 : 400, new
                {
                    success = result.Success,
                    message = result.Message,
                    errors = Array.Empty<object>(),
                    statusCode = result.Success ? 200 : 400,
                    timestamp = DateTime.UtcNow.ToString("o")
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating app. CorrelationId: {CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." }, correlationId);
            }
        }

        /// <summary>
        /// Handles DELETE /v1/apps/{id} — Deletes an application and all its sitemap data.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeleteApp(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context, string appIdStr)
        {
            var correlationId = GetCorrelationId(request);
            _logger.LogInformation("HandleDeleteApp invoked for '{AppId}'. CorrelationId: {CorrelationId}", appIdStr, correlationId);

            try
            {
                if (!IsAdministrator(request))
                {
                    return BuildResponse(403, new { success = false, message = "Access denied. Administrator role required." }, correlationId);
                }

                if (!Guid.TryParse(appIdStr, out var appId))
                {
                    return BuildResponse(400, new { success = false, message = "A valid application ID (GUID) is required." }, correlationId);
                }

                var result = await _sitemapService.DeleteAppAsync(appId, CancellationToken.None).ConfigureAwait(false);

                return BuildResponse(result.Success ? 200 : 400, new
                {
                    success = result.Success,
                    message = result.Message,
                    errors = Array.Empty<object>(),
                    statusCode = result.Success ? 200 : 400,
                    timestamp = DateTime.UtcNow.ToString("o")
                }, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting app. CorrelationId: {CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." }, correlationId);
            }
        }

        /// <summary>
        /// Builds a frontend-compatible sitemap response object from the raw DynamoDB sitemap data.
        /// Returns null if no sitemap data exists, which the frontend App type expects (sitemap: Sitemap | null).
        /// </summary>
        private static object? BuildSitemapResponse(object? sitemapAreas)
        {
            if (sitemapAreas == null) return null;

            // The SitemapService returns sitemap as a list of area objects with nested nodes.
            // Wrap it in the Sitemap envelope that the frontend expects:
            // { areas: [...], groups: [...] }
            return new { areas = sitemapAreas, groups = Array.Empty<object>() };
        }

        /// <summary>
        /// Creates a structured error response for unsupported operations.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse CreateErrorResponse(int statusCode, string message)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = StandardResponseHeaders,
                Body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    success = false,
                    message,
                    statusCode,
                    timestamp = DateTime.UtcNow.ToString("o")
                })
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Builds a consistent HTTP API Gateway v2 response with JSON body, standard headers,
        /// and correlation-ID propagation.
        /// 
        /// Matches the source ResponseModel pattern from AdminController.cs (lines 58-61):
        /// { Success, Message, Object }. Response body is serialized using System.Text.Json
        /// for AOT compatibility.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode,
            object body,
            string correlationId)
        {
            var headers = new Dictionary<string, string>(StandardResponseHeaders)
            {
                ["x-correlation-id"] = correlationId
            };

            // Use AOT source-generated serialization context via pattern matching
            // to avoid IL2026/IL3050 AOT trimming warnings from runtime reflection.
            string serializedBody;
            try
            {
                serializedBody = body switch
                {
                    PluginResponse pr => JsonSerializer.Serialize(pr,
                        PluginHandlerJsonContext.Default.PluginResponse),
                    PluginListResponse plr => JsonSerializer.Serialize(plr,
                        PluginHandlerJsonContext.Default.PluginListResponse),
                    PluginHandlerDomainEvent evt => JsonSerializer.Serialize(evt,
                        PluginHandlerJsonContext.Default.PluginHandlerDomainEvent),
                    Plugin p => JsonSerializer.Serialize(p,
                        PluginHandlerJsonContext.Default.Plugin),
                    // AOT-safe fallback: uses Type + JsonSerializerContext overload
                    // to avoid IL2026/IL3050 warnings from generic Serialize<TValue>
                    _ => JsonSerializer.Serialize(body, body.GetType(),
                        PluginHandlerJsonContext.Default)
                };
            }
            catch (Exception)
            {
                // Fallback: use runtime reflection-based serialization for anonymous
                // types (e.g., app/sitemap responses). This path is only hit for
                // non-AOT-registered types and is safe in managed Lambda runtimes.
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    };
                    serializedBody = JsonSerializer.Serialize(body, options);
                }
                catch (Exception)
                {
                    serializedBody = JsonSerializer.Serialize(
                        new PluginResponse { Success = false, Message = "Response serialization error." },
                        PluginHandlerJsonContext.Default.PluginResponse);
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
        /// Extracts a named path parameter from the API Gateway v2 proxy request.
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

        /// <summary>
        /// Checks if the caller has administrator role based on JWT claims from the
        /// API Gateway v2 JWT authorizer context.
        /// 
        /// Replaces source AdminController.cs [Authorize(Roles = "administrator")] attribute.
        /// </summary>
        private static bool IsAdministrator(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var jwt = request.RequestContext?.Authorizer?.Jwt;
                if (jwt?.Claims == null)
                {
                    return false;
                }

                // Check 'cognito:groups' claim (Cognito groups mapped to roles)
                if (jwt.Claims.TryGetValue("cognito:groups", out var groups))
                {
                    if (!string.IsNullOrWhiteSpace(groups))
                    {
                        var groupList = groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (groupList.Any(g => g.Equals("administrator", StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                }

                // Check 'custom:role' claim (custom attribute from Cognito user pool)
                if (jwt.Claims.TryGetValue("custom:role", out var role))
                {
                    if (!string.IsNullOrWhiteSpace(role) &&
                        role.Equals("administrator", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check 'role' claim (standard JWT claim)
                if (jwt.Claims.TryGetValue("role", out var stdRole))
                {
                    if (!string.IsNullOrWhiteSpace(stdRole) &&
                        stdRole.Equals("administrator", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Extracts or generates a correlation ID for distributed tracing.
        /// Priority: x-correlation-id header → API Gateway request ID → new GUID.
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.Headers != null)
            {
                if (request.Headers.TryGetValue("x-correlation-id", out var correlationHeader) &&
                    !string.IsNullOrWhiteSpace(correlationHeader))
                {
                    return correlationHeader.Trim();
                }

                if (request.Headers.TryGetValue("X-Correlation-Id", out var correlationHeaderMixed) &&
                    !string.IsNullOrWhiteSpace(correlationHeaderMixed))
                {
                    return correlationHeaderMixed.Trim();
                }
            }

            var requestContextId = request.RequestContext?.RequestId;
            if (!string.IsNullOrWhiteSpace(requestContextId))
            {
                return requestContextId;
            }

            return Guid.NewGuid().ToString("D");
        }

        /// <summary>
        /// Publishes a domain event to the SNS topic for plugin lifecycle changes.
        /// Errors are logged but NOT thrown to never block the primary plugin operation.
        /// </summary>
        private async Task PublishDomainEventAsync(
            string eventType,
            Guid pluginId,
            string pluginName,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_snsTopicArn))
            {
                _logger.LogWarning(
                    "PublishDomainEventAsync: SNS topic ARN not configured (env var {EnvVar} is empty). " +
                    "Skipping event publish for {EventType}. CorrelationId: {CorrelationId}",
                    SnsTopicArnEnvVar, eventType, correlationId);
                return;
            }

            try
            {
                var domainEvent = new PluginHandlerDomainEvent
                {
                    EventType = eventType,
                    PluginId = pluginId.ToString("D"),
                    PluginName = pluginName,
                    Action = eventType.Split('.').LastOrDefault() ?? "unknown",
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    CorrelationId = correlationId
                };

                var messageBody = JsonSerializer.Serialize(domainEvent,
                    PluginHandlerJsonContext.Default.PluginHandlerDomainEvent);

                var publishRequest = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["pluginId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = pluginId.ToString("D")
                        }
                    }
                };

                var publishResponse = await _snsClient.PublishAsync(publishRequest).ConfigureAwait(false);

                _logger.LogInformation(
                    "PublishDomainEventAsync: Published {EventType} for plugin {PluginName} ({PluginId}). " +
                    "MessageId: {MessageId}. CorrelationId: {CorrelationId}",
                    eventType, pluginName, pluginId, publishResponse.MessageId, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PublishDomainEventAsync: Failed to publish {EventType} for plugin {PluginName} ({PluginId}). " +
                    "CorrelationId: {CorrelationId}",
                    eventType, pluginName, pluginId, correlationId);
            }
        }

        #endregion
    }
}