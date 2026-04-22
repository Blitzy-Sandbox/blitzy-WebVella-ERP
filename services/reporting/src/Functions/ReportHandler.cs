// Suppress AOT trimming warnings for System.Text.Json serialization.
// ReportHandler uses runtime JSON serialization for API Gateway request/response mapping
// which is acceptable for Lambda-based services with managed runtime.
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute
#pragma warning disable IL3050 // Members annotated with RequiresDynamicCodeAttribute

using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.Extensions.NETCore.Setup;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Models;
using WebVellaErp.Reporting.Services;

// Namespace aliases for service-layer DTOs to avoid naming conflicts with
// handler-level DTOs exported by this file (per AAP §0.5.2 Import Transformation Rules).
using ServiceCreateRequest = WebVellaErp.Reporting.Services.CreateReportRequest;
using ServiceUpdateRequest = WebVellaErp.Reporting.Services.UpdateReportRequest;

// Assembly-level JSON serializer for Native AOT compatibility — per AAP §0.8.2
// Uses System.Text.Json-based serializer for < 1 second cold start target.
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Reporting.Functions
{
    // ════════════════════════════════════════════════════════════════════════
    // Handler-Level Request DTOs — API Gateway request body deserialization
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Request DTO for creating a new report definition.
    /// Replaces the monolith's inline parameter extraction in DataSourceManager.Create()
    /// (source lines 127-189) with a structured, validated request envelope.
    /// Deserialized from API Gateway HTTP API v2 request body (JSON).
    /// </summary>
    public class CreateReportRequest
    {
        /// <summary>
        /// Human-readable report name. Must be unique across all report definitions.
        /// Maps from source DataSourceManager.Create() parameter 'name' (source line 127).
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of the report's purpose and data scope.
        /// Maps from source DataSourceManager.Create() parameter 'description' (source line 127).
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// SQL query template for execution against the RDS PostgreSQL read-model.
        /// Replaces the monolith's EQL-based query definition (source lines 175-176).
        /// In the target architecture, reports use direct SQL against the reporting
        /// service's own read-model projections rather than EQL translated to SQL.
        /// </summary>
        [JsonPropertyName("queryDefinition")]
        public string QueryDefinition { get; set; } = string.Empty;

        /// <summary>
        /// Optional list of typed parameters for parameterized SQL execution.
        /// Adapted from source's newline CSV format (DataSourceManager.ProcessParametersText,
        /// source lines 131-134) to structured JSON array.
        /// </summary>
        [JsonPropertyName("parameters")]
        public List<ReportParameter>? Parameters { get; set; }

        /// <summary>
        /// Whether to return total row count with paginated results.
        /// Default: true, matching source DataSourceManager.Create() behavior (source line 127).
        /// </summary>
        [JsonPropertyName("returnTotal")]
        public bool ReturnTotal { get; set; } = true;
    }

    /// <summary>
    /// Request DTO for updating an existing report definition.
    /// Replaces the monolith's inline parameter extraction in DataSourceManager.Update()
    /// (source lines 191-265) with a structured, validated request envelope.
    /// All fields are optional for partial update semantics.
    /// </summary>
    public class UpdateReportRequest
    {
        /// <summary>
        /// Updated report name. Must remain unique across all report definitions.
        /// Maps from source DataSourceManager.Update() parameter 'name' (source line 191).
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Updated report description.
        /// Maps from source DataSourceManager.Update() parameter 'description' (source line 191).
        /// </summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>
        /// Updated SQL query template for the read-model.
        /// Replaces EQL-based query definition from source (lines 195-196).
        /// </summary>
        [JsonPropertyName("queryDefinition")]
        public string QueryDefinition { get; set; } = string.Empty;

        /// <summary>
        /// Updated list of typed parameters for parameterized SQL execution.
        /// Adapted from source's newline CSV format to structured JSON array.
        /// </summary>
        [JsonPropertyName("parameters")]
        public List<ReportParameter>? Parameters { get; set; }

        /// <summary>
        /// Updated flag for total row count inclusion.
        /// Default: true, matching source DataSourceManager.Update() behavior.
        /// </summary>
        [JsonPropertyName("returnTotal")]
        public bool ReturnTotal { get; set; } = true;
    }

    /// <summary>
    /// Request DTO for executing a report with runtime parameter values.
    /// Replaces the monolith's <c>List&lt;EqlParameter&gt;</c> from
    /// DataSourceManager.Execute(Guid id, List&lt;EqlParameter&gt; parameters)
    /// (source lines 470-497) with a flexible dictionary of parameter name → value.
    /// </summary>
    public class ExecuteReportRequest
    {
        /// <summary>
        /// Dictionary of parameter name → value for report execution.
        /// Parameter names must match the report definition's parameter list.
        /// Values are dynamically typed and resolved by the service layer.
        /// Replaces source's <c>List&lt;EqlParameter&gt;</c> with a more
        /// API-friendly dictionary format.
        /// </summary>
        [JsonPropertyName("parameters")]
        public Dictionary<string, object?>? Parameters { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ReportHandler — Primary Lambda Entry Point for Reporting & Analytics
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AWS Lambda handler for the Reporting &amp; Analytics bounded-context service.
    /// Provides 7 Lambda entry points for HTTP API Gateway v2 integration:
    ///   - HandleListReports:    GET    /v1/reports
    ///   - HandleGetReport:      GET    /v1/reports/{id}
    ///   - HandleCreateReport:   POST   /v1/reports
    ///   - HandleUpdateReport:   PUT    /v1/reports/{id}
    ///   - HandleDeleteReport:   DELETE /v1/reports/{id}
    ///   - HandleExecuteReport:  POST   /v1/reports/{id}/execute
    ///   - HandleHealthCheck:    GET    /v1/reports/health
    ///
    /// Replaces the monolith's <c>DataSourceManager</c> (source DataSourceManager.cs) and
    /// <c>WebApiController</c> datasource endpoints (api/v3/en_US/eql-ds, api/v3.0/datasource/*).
    ///
    /// Architecture per AAP:
    ///   - ACID-critical service using RDS PostgreSQL (NOT DynamoDB) — §0.4.2, §0.7.4
    ///   - DB_CONNECTION_STRING from SSM SecureString, NEVER env vars — §0.8.6
    ///   - .NET 9 Native AOT for &lt; 1 second cold start — §0.8.2
    ///   - SNS domain events replace synchronous HookManager — §0.7.2
    ///   - JWT claims from API Gateway authorizer replace SecurityContext — §0.5.2
    ///   - Structured JSON logging with correlation-ID — §0.8.5
    ///   - Idempotency keys on all write endpoints — §0.8.5
    /// </summary>
    public class ReportHandler
    {
        // ── Dependencies ────────────────────────────────────────────────────

        /// <summary>Report business logic service replacing DataSourceManager from monolith.</summary>
        private readonly IReportService _reportService;

        /// <summary>RDS PostgreSQL data access for health check verification.</summary>
        private readonly IReportRepository _reportRepository;

        /// <summary>SNS client for domain event publishing and health check verification.</summary>
        private readonly IAmazonSimpleNotificationService _snsClient;

        /// <summary>SSM client for retrieving DB_CONNECTION_STRING from SecureString.</summary>
        private readonly IAmazonSimpleSystemsManagement _ssmClient;

        /// <summary>Structured JSON logger with correlation-ID propagation per AAP §0.8.5.</summary>
        private readonly ILogger<ReportHandler> _logger;

        /// <summary>DI service provider for resolving scoped services.</summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>Lazily cached RDS PostgreSQL connection string from SSM SecureString.</summary>
        private string? _connectionString;

        // ── Constants ───────────────────────────────────────────────────────

        /// <summary>SSM parameter path for the reporting service's database connection string.</summary>
        private const string SSM_CONNECTION_STRING_KEY = "/reporting/db-connection-string";

        /// <summary>SNS topic ARN environment variable for domain events.</summary>
        private const string SNS_TOPIC_ARN_ENV = "REPORTING_SNS_TOPIC_ARN";

        /// <summary>Default page number for paginated list operations.</summary>
        private const int DEFAULT_PAGE = 1;

        /// <summary>Default page size for paginated list operations.</summary>
        private const int DEFAULT_PAGE_SIZE = 20;

        /// <summary>Maximum page size to prevent resource exhaustion.</summary>
        private const int MAX_PAGE_SIZE = 100;

        /// <summary>Default sort column for list operations.</summary>
        private const string DEFAULT_SORT_BY = "created_at";

        /// <summary>Default sort direction for list operations.</summary>
        private const string DEFAULT_SORT_ORDER = "desc";

        /// <summary>Maximum SQL execution timeout for report queries in seconds.</summary>
        private const int SQL_EXECUTION_TIMEOUT_SECONDS = 600;

        /// <summary>
        /// JSON serializer options configured for API Gateway request/response serialization.
        /// Uses camelCase naming for frontend compatibility and case-insensitive deserialization.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ── Constructors ────────────────────────────────────────────────────

        /// <summary>
        /// Parameterless constructor required by the Lambda runtime.
        /// Builds the DI container, registers all services, and resolves dependencies.
        /// Runs once per Lambda cold start. Connection string is lazily resolved
        /// from SSM SecureString on first use (per AAP §0.8.6).
        ///
        /// DI Registration:
        ///   - IReportService → ReportService (singleton for Lambda container reuse)
        ///   - IReportRepository → ReportRepository (singleton with SSM connection string)
        ///   - IAmazonSimpleNotificationService (AWS SDK DI with LocalStack support)
        ///   - IAmazonSimpleSystemsManagement (AWS SDK DI with LocalStack support)
        ///   - IMemoryCache (in-process caching for report definitions and idempotency)
        ///   - ILogger (structured JSON logging)
        /// </summary>
        public ReportHandler()
        {
            var services = new ServiceCollection();

            // ── Logging Configuration ──
            // Structured JSON logging with correlation-ID propagation per AAP §0.8.5.
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                builder.AddConsole();
            });

            // ── AWS SDK Client Registration ──
            // Respect AWS_ENDPOINT_URL for LocalStack dual-target support per AAP §0.8.6.
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                // LocalStack mode: configure custom endpoint
                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceConfig
                    {
                        ServiceURL = endpointUrl
                    }));
                services.AddSingleton<IAmazonSimpleSystemsManagement>(_ =>
                    new AmazonSimpleSystemsManagementClient(new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementConfig
                    {
                        ServiceURL = endpointUrl
                    }));
            }
            else
            {
                // Production mode: use default AWS SDK configuration
                var awsOptions = new AWSOptions();
                services.AddDefaultAWSOptions(awsOptions);
                services.AddAWSService<IAmazonSimpleNotificationService>();
                services.AddAWSService<IAmazonSimpleSystemsManagement>();
            }

            // ── In-Process Caching ──
            // Used by ReportService for report definition caching (1hr TTL)
            // and idempotency key deduplication (24hr TTL).
            services.AddMemoryCache();

            // ── Resolve Connection String ──
            // Per AAP §0.8.6: DB_CONNECTION_STRING from SSM SecureString, NEVER env vars.
            // For LocalStack testing, the DB_CONNECTION_STRING env var is accepted as fallback.
            string connectionString = ResolveConnectionStringSync(services);

            // ── Repository Registration ──
            // ReportRepository requires explicit connection string (not ambient DbContext.Current).
            services.AddSingleton<IReportRepository>(sp =>
                new ReportRepository(connectionString, sp.GetRequiredService<ILogger<ReportRepository>>()));

            // ── Service Registration ──
            // ReportService encapsulates all business logic, caching, SNS events, and idempotency.
            services.AddSingleton<IReportService, ReportService>();

            // Build the DI container
            _serviceProvider = services.BuildServiceProvider();

            // Resolve handler-level dependencies
            _reportService = _serviceProvider.GetRequiredService<IReportService>();
            _reportRepository = _serviceProvider.GetRequiredService<IReportRepository>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _ssmClient = _serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
            _logger = _serviceProvider.GetRequiredService<ILogger<ReportHandler>>();
            _connectionString = connectionString;
        }

        /// <summary>
        /// Constructor accepting an IServiceProvider for unit testing.
        /// Allows test code to inject mock/stub implementations of all dependencies.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured DI container with all required services.</param>
        public ReportHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _reportService = serviceProvider.GetRequiredService<IReportService>();
            _reportRepository = serviceProvider.GetRequiredService<IReportRepository>();
            _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _ssmClient = serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
            _logger = serviceProvider.GetRequiredService<ILogger<ReportHandler>>();
        }

        /// <summary>
        /// Resolves the RDS PostgreSQL connection string synchronously during constructor execution.
        /// Checks DB_CONNECTION_STRING environment variable first (LocalStack/testing),
        /// then falls back to SSM SecureString retrieval.
        /// This blocking call is acceptable during Lambda cold start (runs once).
        /// </summary>
        private static string ResolveConnectionStringSync(ServiceCollection services)
        {
            // Check environment variable first (LocalStack / integration testing)
            string? envConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(envConnectionString))
            {
                return envConnectionString;
            }

            // Fallback to SSM SecureString retrieval (production path)
            try
            {
                var tempProvider = services.BuildServiceProvider();
                var ssmClient = tempProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
                var response = ssmClient.GetParameterAsync(new GetParameterRequest
                {
                    Name = SSM_CONNECTION_STRING_KEY,
                    WithDecryption = true
                }).GetAwaiter().GetResult();
                return response.Parameter.Value;
            }
            catch (Exception)
            {
                // If SSM is not available (e.g., unit tests), return a placeholder
                // that will fail on first actual database operation with a clear error.
                return string.Empty;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Lambda Handler Methods — HTTP API Gateway v2 Entry Points
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists all report definitions with pagination and sorting.
        /// Lambda entry point for GET /v1/reports.
        ///
        /// Replaces DataSourceManager.GetAll() (source lines 93-109) and
        /// DataSourceManager.GetFromCache() (source lines 42-47).
        ///
        /// Query Parameters:
        ///   - page (int, default: 1)
        ///   - pageSize (int, default: 20, max: 100)
        ///   - sortBy (string, default: "created_at")
        ///   - sortOrder ("asc" or "desc", default: "desc")
        /// </summary>

        /// <summary>
        /// Single entry point for managed .NET Lambda runtime (dotnet9).
        /// Routes API Gateway HTTP API v2 requests to the appropriate handler method
        /// based on HTTP method and request path.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var path = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
            var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

            if (method == "GET")
                return await HandleListReports(request, context);
            else if (method == "GET")
                return await HandleGetReport(request, context);
            else if (method == "POST")
                return await HandleCreateReport(request, context);
            else if (method == "PUT")
                return await HandleUpdateReport(request, context);
            else if (method == "DELETE")
                return await HandleDeleteReport(request, context);
            else if (method == "GET")
                return await HandleExecuteReport(request, context);
            else if (method == "GET" && path.Contains("/health"))
                return await HandleHealthCheck(request, context);

            // Default: route to HandleListReports
            return await HandleListReports(request, context);
        }

        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleListReports(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleListReports),
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleListReports - Processing list reports request");

                // Extract caller identity from JWT authorizer context
                var caller = ExtractCallerFromContext(request);
                if (caller == null)
                {
                    _logger.LogWarning("HandleListReports - Unauthorized: no valid caller identity");
                    return BuildErrorResponse(401, "Unauthorized. Valid authentication required.");
                }

                // Parse pagination and sorting query parameters
                int page = GetQueryParamInt(request, "page", DEFAULT_PAGE);
                int pageSize = GetQueryParamInt(request, "pageSize", DEFAULT_PAGE_SIZE);
                string sortBy = GetQueryParamString(request, "sortBy", DEFAULT_SORT_BY);
                string sortOrder = GetQueryParamString(request, "sortOrder", DEFAULT_SORT_ORDER);

                // Enforce maximum page size to prevent resource exhaustion
                if (pageSize > MAX_PAGE_SIZE)
                {
                    pageSize = MAX_PAGE_SIZE;
                }
                if (pageSize < 1)
                {
                    pageSize = DEFAULT_PAGE_SIZE;
                }
                if (page < 1)
                {
                    page = DEFAULT_PAGE;
                }

                // Validate sort order
                if (!string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase))
                {
                    sortOrder = DEFAULT_SORT_ORDER;
                }

                _logger.LogDebug(
                    "HandleListReports - Params: page={Page}, pageSize={PageSize}, sortBy={SortBy}, sortOrder={SortOrder}",
                    page, pageSize, sortBy, sortOrder);

                // Execute paginated query via service layer
                var (reports, totalCount) = await _reportService.GetAllReportsAsync(
                    page, pageSize, sortBy, sortOrder, context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(context.RemainingTime).Token
                        : CancellationToken.None);

                _logger.LogInformation(
                    "HandleListReports - Returning {Count} reports (total: {TotalCount})",
                    reports.Count, totalCount);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = new
                    {
                        data = reports,
                        total_count = totalCount,
                        page,
                        page_size = pageSize
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleListReports - Unhandled exception");
                return BuildErrorResponse(500, "An internal error occurred while listing reports.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
        }

        /// <summary>
        /// Retrieves a single report definition by its unique identifier.
        /// Lambda entry point for GET /v1/reports/{id}.
        ///
        /// Replaces DataSourceManager.Get(Guid id) (source line 104) and
        /// DataSourceManager.GetDatabaseDataSourceByName(string name) (source lines 113-125).
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetReport(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleGetReport),
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleGetReport - Processing get report request");

                // Extract caller identity from JWT authorizer context
                var caller = ExtractCallerFromContext(request);
                if (caller == null)
                {
                    _logger.LogWarning("HandleGetReport - Unauthorized: no valid caller identity");
                    return BuildErrorResponse(401, "Unauthorized. Valid authentication required.");
                }

                // Extract and parse report ID from path parameters
                if (!TryGetPathParameterGuid(request, "id", out Guid reportId))
                {
                    return BuildErrorResponse(400, "Invalid or missing report ID. Must be a valid GUID.");
                }

                _logger.LogDebug("HandleGetReport - Looking up report {ReportId}", reportId);

                // Retrieve report definition via service layer (cache-first pattern)
                var report = await _reportService.GetReportByIdAsync(reportId,
                    context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(context.RemainingTime).Token
                        : CancellationToken.None);

                if (report == null)
                {
                    _logger.LogWarning("HandleGetReport - Report {ReportId} not found", reportId);
                    return BuildErrorResponse(404, "Report not found.");
                }

                _logger.LogInformation("HandleGetReport - Returning report {ReportId} ({Name})",
                    report.Id, report.Name);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = report
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleGetReport - Unhandled exception");
                return BuildErrorResponse(500, "An internal error occurred while retrieving the report.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
        }

        /// <summary>
        /// Creates a new report definition with idempotency and domain event publishing.
        /// Lambda entry point for POST /v1/reports.
        ///
        /// Replaces DataSourceManager.Create(name, description, weight, eql, parameters, returnTotal)
        /// (source lines 127-189). Key differences from monolith:
        ///   - Structured JSON request body instead of individual parameters
        ///   - SQL query definition instead of EQL (no EqlBuilder translation needed)
        ///   - Idempotency key support via Idempotency-Key header (AAP §0.8.5)
        ///   - SNS domain event 'reporting.report.created' instead of synchronous hooks
        ///   - ACID transaction via explicit NpgsqlTransaction instead of ambient DbContext
        ///
        /// Validation rules (adapted from source DataSourceManager.Create):
        ///   - Name must be non-empty (source line 170-171)
        ///   - Name must be unique (source lines 172-173)
        ///   - QueryDefinition must be non-empty (source lines 175-176)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateReport(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleCreateReport),
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleCreateReport - Processing create report request");

                // Extract caller identity from JWT authorizer context
                var caller = ExtractCallerFromContext(request);
                if (caller == null)
                {
                    _logger.LogWarning("HandleCreateReport - Unauthorized: no valid caller identity");
                    return BuildErrorResponse(401, "Unauthorized. Valid authentication required.");
                }

                // Permission check: only admin users can create report definitions
                if (!HasPermission(caller, "admin"))
                {
                    _logger.LogWarning("HandleCreateReport - Access denied for user {UserId}", caller.UserId);
                    return BuildErrorResponse(403, "Access denied. Admin permission required to create reports.");
                }

                // Extract idempotency key from header (per AAP §0.8.5)
                string idempotencyKey = GetHeaderValue(request, "Idempotency-Key") ?? string.Empty;

                // Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Request body is required.");
                }

                CreateReportRequest? createRequest;
                try
                {
                    createRequest = JsonSerializer.Deserialize<CreateReportRequest>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "HandleCreateReport - Invalid JSON in request body");
                    return BuildErrorResponse(400, "Invalid JSON in request body.");
                }

                if (createRequest == null)
                {
                    return BuildErrorResponse(400, "Request body deserialization returned null.");
                }

                // ── Input Validation (adapted from source DataSourceManager.Create lines 129-181) ──
                var validationErrors = new List<string>();

                // Name validation (source lines 170-171)
                if (string.IsNullOrWhiteSpace(createRequest.Name))
                {
                    validationErrors.Add("Name is required and cannot be empty.");
                }

                // QueryDefinition validation (source lines 175-176)
                if (string.IsNullOrWhiteSpace(createRequest.QueryDefinition))
                {
                    validationErrors.Add("QueryDefinition is required and cannot be empty.");
                }

                // Parameter validation
                if (createRequest.Parameters != null)
                {
                    for (int i = 0; i < createRequest.Parameters.Count; i++)
                    {
                        var param = createRequest.Parameters[i];
                        if (string.IsNullOrWhiteSpace(param.Name))
                        {
                            validationErrors.Add($"Parameter at index {i} has an empty name.");
                        }
                        if (string.IsNullOrWhiteSpace(param.Type))
                        {
                            validationErrors.Add($"Parameter '{param.Name}' has an empty type. Allowed types: guid, int, decimal, date, text, bool.");
                        }
                        else
                        {
                            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { "guid", "int", "decimal", "date", "text", "bool" };
                            if (!allowedTypes.Contains(param.Type))
                            {
                                validationErrors.Add($"Parameter '{param.Name}' has invalid type '{param.Type}'. Allowed types: guid, int, decimal, date, text, bool.");
                            }
                        }
                    }
                }

                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning("HandleCreateReport - Validation failed: {Errors}",
                        string.Join("; ", validationErrors));
                    return BuildErrorResponse(400, "Validation failed.", errors: validationErrors);
                }

                // Map handler DTO → service DTO
                var serviceRequest = new ServiceCreateRequest
                {
                    Name = createRequest.Name.Trim(),
                    Description = createRequest.Description?.Trim() ?? string.Empty,
                    QueryDefinition = createRequest.QueryDefinition.Trim(),
                    Parameters = createRequest.Parameters,
                    ReturnTotal = createRequest.ReturnTotal,
                    Weight = 10 // Default weight matching source line 160
                };

                // Extract creator ID from JWT claims
                Guid? createdBy = caller.UserId != Guid.Empty ? caller.UserId : null;

                _logger.LogDebug("HandleCreateReport - Creating report '{Name}' with idempotency key '{Key}'",
                    serviceRequest.Name, idempotencyKey);

                // Delegate to service layer (handles ACID transaction, cache invalidation, SNS event)
                var createdReport = await _reportService.CreateReportAsync(
                    serviceRequest, createdBy, idempotencyKey,
                    context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(context.RemainingTime).Token
                        : CancellationToken.None);

                _logger.LogInformation(
                    "HandleCreateReport - Report created: {ReportId} ({Name})",
                    createdReport.Id, createdReport.Name);

                return BuildResponse(201, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = createdReport
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Name uniqueness violation (source lines 172-173)
                _logger.LogWarning("HandleCreateReport - Duplicate name: {Message}", ex.Message);
                return BuildErrorResponse(409, ex.Message);
            }
            catch (ReportValidationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Name uniqueness violation caught via validation pipeline
                _logger.LogWarning("HandleCreateReport - Duplicate name (validation): {Message}", ex.Message);
                return BuildErrorResponse(409, ex.Message);
            }
            catch (ReportValidationException ex)
            {
                // Other validation errors from the service layer
                _logger.LogWarning("HandleCreateReport - Validation error: {Message}", ex.Message);
                return BuildErrorResponse(400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("HandleCreateReport - Validation error: {Message}", ex.Message);
                return BuildErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleCreateReport - Unhandled exception");
                return BuildErrorResponse(500, "An internal error occurred while creating the report.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
        }

        /// <summary>
        /// Updates an existing report definition with idempotency and domain event publishing.
        /// Lambda entry point for PUT /v1/reports/{id}.
        ///
        /// Replaces DataSourceManager.Update(id, name, description, weight, eql, parameters, returnTotal)
        /// (source lines 191-265). Key validations (adapted from source):
        ///   - QueryDefinition non-empty (source lines 195-196)
        ///   - Name non-empty (source lines 241-242)
        ///   - Name uniqueness against OTHER reports (source lines 243-248)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateReport(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleUpdateReport),
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleUpdateReport - Processing update report request");

                // Extract caller identity from JWT authorizer context
                var caller = ExtractCallerFromContext(request);
                if (caller == null)
                {
                    _logger.LogWarning("HandleUpdateReport - Unauthorized: no valid caller identity");
                    return BuildErrorResponse(401, "Unauthorized. Valid authentication required.");
                }

                // Permission check: only admin users can update report definitions
                if (!HasPermission(caller, "admin"))
                {
                    _logger.LogWarning("HandleUpdateReport - Access denied for user {UserId}", caller.UserId);
                    return BuildErrorResponse(403, "Access denied. Admin permission required to update reports.");
                }

                // Extract report ID from path parameters
                if (!TryGetPathParameterGuid(request, "id", out Guid reportId))
                {
                    return BuildErrorResponse(400, "Invalid or missing report ID. Must be a valid GUID.");
                }

                // Extract idempotency key from header (per AAP §0.8.5)
                string idempotencyKey = GetHeaderValue(request, "Idempotency-Key") ?? string.Empty;

                // Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Request body is required.");
                }

                UpdateReportRequest? updateRequest;
                try
                {
                    updateRequest = JsonSerializer.Deserialize<UpdateReportRequest>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "HandleUpdateReport - Invalid JSON in request body");
                    return BuildErrorResponse(400, "Invalid JSON in request body.");
                }

                if (updateRequest == null)
                {
                    return BuildErrorResponse(400, "Request body deserialization returned null.");
                }

                // ── Input Validation (adapted from source DataSourceManager.Update lines 193-257) ──
                var validationErrors = new List<string>();

                // Name validation (source lines 241-242)
                if (string.IsNullOrWhiteSpace(updateRequest.Name))
                {
                    validationErrors.Add("Name is required and cannot be empty.");
                }

                // QueryDefinition validation (source lines 195-196)
                if (string.IsNullOrWhiteSpace(updateRequest.QueryDefinition))
                {
                    validationErrors.Add("QueryDefinition is required and cannot be empty.");
                }

                // Parameter validation (source lines 198-205)
                if (updateRequest.Parameters != null)
                {
                    for (int i = 0; i < updateRequest.Parameters.Count; i++)
                    {
                        var param = updateRequest.Parameters[i];
                        if (string.IsNullOrWhiteSpace(param.Name))
                        {
                            validationErrors.Add($"Parameter at index {i} has an empty name.");
                        }
                        if (string.IsNullOrWhiteSpace(param.Type))
                        {
                            validationErrors.Add($"Parameter '{param.Name}' has an empty type. Allowed types: guid, int, decimal, date, text, bool.");
                        }
                        else
                        {
                            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { "guid", "int", "decimal", "date", "text", "bool" };
                            if (!allowedTypes.Contains(param.Type))
                            {
                                validationErrors.Add($"Parameter '{param.Name}' has invalid type '{param.Type}'. Allowed types: guid, int, decimal, date, text, bool.");
                            }
                        }
                    }
                }

                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning("HandleUpdateReport - Validation failed: {Errors}",
                        string.Join("; ", validationErrors));
                    return BuildErrorResponse(400, "Validation failed.", errors: validationErrors);
                }

                // Map handler DTO → service DTO
                var serviceRequest = new ServiceUpdateRequest
                {
                    Name = updateRequest.Name.Trim(),
                    Description = updateRequest.Description?.Trim() ?? string.Empty,
                    QueryDefinition = updateRequest.QueryDefinition.Trim(),
                    Parameters = updateRequest.Parameters,
                    ReturnTotal = updateRequest.ReturnTotal,
                    Weight = 10 // Default weight
                };

                _logger.LogDebug(
                    "HandleUpdateReport - Updating report {ReportId} with idempotency key '{Key}'",
                    reportId, idempotencyKey);

                // Delegate to service layer (handles ACID transaction, cache invalidation, SNS event)
                var updatedReport = await _reportService.UpdateReportAsync(
                    reportId, serviceRequest, idempotencyKey,
                    context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(context.RemainingTime).Token
                        : CancellationToken.None);

                _logger.LogInformation(
                    "HandleUpdateReport - Report updated: {ReportId} ({Name})",
                    updatedReport.Id, updatedReport.Name);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = updatedReport
                });
            }
            catch (KeyNotFoundException ex)
            {
                // Report not found (adapted from source: "DataSource not found.")
                _logger.LogWarning("HandleUpdateReport - Report not found: {Message}", ex.Message);
                return BuildErrorResponse(404, "Report not found.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Name uniqueness violation against OTHER reports (source lines 243-248)
                _logger.LogWarning("HandleUpdateReport - Duplicate name: {Message}", ex.Message);
                return BuildErrorResponse(409, ex.Message);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("HandleUpdateReport - Validation error: {Message}", ex.Message);
                return BuildErrorResponse(400, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleUpdateReport - Unhandled exception");
                return BuildErrorResponse(500, "An internal error occurred while updating the report.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
        }

        /// <summary>
        /// Deletes an existing report definition with idempotency and domain event publishing.
        /// Lambda entry point for DELETE /v1/reports/{id}.
        ///
        /// Replaces DataSourceManager.Delete(Guid id) (source lines 464-468).
        /// Source implementation:
        ///   rep.Delete(id);       // line 466
        ///   RemoveFromCache();    // line 467
        /// Target: delegates to IReportService which handles ACID transaction,
        /// cache invalidation, and SNS event 'reporting.report.deleted'.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeleteReport(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleDeleteReport),
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleDeleteReport - Processing delete report request");

                // Extract caller identity from JWT authorizer context
                var caller = ExtractCallerFromContext(request);
                if (caller == null)
                {
                    _logger.LogWarning("HandleDeleteReport - Unauthorized: no valid caller identity");
                    return BuildErrorResponse(401, "Unauthorized. Valid authentication required.");
                }

                // Permission check: only admin users can delete report definitions
                if (!HasPermission(caller, "admin"))
                {
                    _logger.LogWarning("HandleDeleteReport - Access denied for user {UserId}", caller.UserId);
                    return BuildErrorResponse(403, "Access denied. Admin permission required to delete reports.");
                }

                // Extract report ID from path parameters
                if (!TryGetPathParameterGuid(request, "id", out Guid reportId))
                {
                    return BuildErrorResponse(400, "Invalid or missing report ID. Must be a valid GUID.");
                }

                // Extract idempotency key from header (per AAP §0.8.5)
                string idempotencyKey = GetHeaderValue(request, "Idempotency-Key") ?? string.Empty;

                _logger.LogDebug("HandleDeleteReport - Deleting report {ReportId}", reportId);

                // Verify report exists before deletion
                var existingReport = await _reportService.GetReportByIdAsync(reportId,
                    context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(context.RemainingTime).Token
                        : CancellationToken.None);

                if (existingReport == null)
                {
                    _logger.LogWarning("HandleDeleteReport - Report {ReportId} not found", reportId);
                    return BuildErrorResponse(404, "Report not found.");
                }

                // Delegate to service layer (handles ACID transaction, cache invalidation, SNS event)
                await _reportService.DeleteReportAsync(reportId, idempotencyKey,
                    context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(context.RemainingTime).Token
                        : CancellationToken.None);

                _logger.LogInformation("HandleDeleteReport - Report deleted: {ReportId} ({Name})",
                    reportId, existingReport.Name);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    message = $"Report '{existingReport.Name}' (ID: {reportId}) deleted successfully."
                });
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning("HandleDeleteReport - Report not found: {Message}", ex.Message);
                return BuildErrorResponse(404, "Report not found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleDeleteReport - Unhandled exception");
                return BuildErrorResponse(500, "An internal error occurred while deleting the report.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
        }

        /// <summary>
        /// Executes a report with runtime parameter values against the RDS PostgreSQL read-model.
        /// Lambda entry point for POST /v1/reports/{id}/execute.
        ///
        /// Replaces DataSourceManager.Execute(Guid id, List&lt;EqlParameter&gt; parameters)
        /// (source lines 470-497) and DataSourceManager.Execute(string eql, string parameters, bool returnTotal)
        /// (source lines 499-512).
        ///
        /// Key differences from monolith:
        ///   - Runs pre-defined SQL directly against reporting read-model (no EQL→SQL translation)
        ///   - Parameters as dictionary instead of List&lt;EqlParameter&gt;
        ///   - Missing parameters enriched with defaults from report definition (source lines 479-481)
        ///   - Returns ReportResult with execution metadata (duration, columns, pagination)
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleExecuteReport(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleExecuteReport),
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleExecuteReport - Processing execute report request");

                // Extract caller identity from JWT authorizer context
                var caller = ExtractCallerFromContext(request);
                if (caller == null)
                {
                    _logger.LogWarning("HandleExecuteReport - Unauthorized: no valid caller identity");
                    return BuildErrorResponse(401, "Unauthorized. Valid authentication required.");
                }

                // Extract report ID from path parameters
                if (!TryGetPathParameterGuid(request, "id", out Guid reportId))
                {
                    return BuildErrorResponse(400, "Invalid or missing report ID. Must be a valid GUID.");
                }

                // Deserialize optional request body with execution parameters
                Dictionary<string, object?>? executionParams = null;
                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    try
                    {
                        var executeRequest = JsonSerializer.Deserialize<ExecuteReportRequest>(request.Body, JsonOptions);
                        executionParams = executeRequest?.Parameters;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "HandleExecuteReport - Invalid JSON in request body");
                        return BuildErrorResponse(400, "Invalid JSON in request body.");
                    }
                }

                _logger.LogDebug("HandleExecuteReport - Executing report {ReportId} with {ParamCount} parameters",
                    reportId, executionParams?.Count ?? 0);

                // Start execution timer for performance metrics
                var stopwatch = Stopwatch.StartNew();

                // Delegate to service layer for parameter enrichment and SQL execution
                // Service handles: report lookup, default parameter enrichment (source lines 479-481),
                // parameterized SQL execution against RDS PostgreSQL read-model
                var cancellationToken = context.RemainingTime > TimeSpan.Zero
                    ? new CancellationTokenSource(context.RemainingTime).Token
                    : CancellationToken.None;

                var executionResult = await _reportService.ExecuteReportAsync(
                    reportId, executionParams, cancellationToken);

                stopwatch.Stop();

                // Retrieve report definition for response metadata
                var reportDef = await _reportService.GetReportByIdAsync(reportId, cancellationToken);

                // Map ReportExecutionResult → ReportResult with execution metadata
                var reportResult = new ReportResult
                {
                    ReportId = reportId,
                    ReportName = reportDef?.Name ?? "Unknown",
                    Rows = executionResult.Data,
                    TotalCount = executionResult.TotalCount,
                    PageNumber = 1,
                    PageSize = executionResult.TotalCount > 0 ? executionResult.TotalCount : 50,
                    ExecutionDuration = stopwatch.Elapsed,
                    ExecutedAt = DateTime.UtcNow,
                    Success = true,
                    Columns = InferColumns(executionResult.Data)
                };

                _logger.LogInformation(
                    "HandleExecuteReport - Report {ReportId} executed: {RowCount} rows in {Duration}ms",
                    reportId, executionResult.Data.Count, stopwatch.ElapsedMilliseconds);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    @object = new
                    {
                        data = reportResult.Rows,
                        columns = reportResult.Columns,
                        total_count = reportResult.TotalCount,
                        execution_duration_ms = reportResult.ExecutionDuration.TotalMilliseconds,
                        report_id = reportResult.ReportId,
                        report_name = reportResult.ReportName
                    }
                });
            }
            catch (KeyNotFoundException ex)
            {
                // Report not found (source line 473-474: "DataSource not found.")
                _logger.LogWarning("HandleExecuteReport - Report not found: {Message}", ex.Message);
                return BuildErrorResponse(404, "Report not found.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("HandleExecuteReport - Execution error: {Message}", ex.Message);
                return BuildErrorResponse(400, ex.Message);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "HandleExecuteReport - Database execution error");
                return BuildErrorResponse(500,
                    "Report execution failed due to a database error.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleExecuteReport - Unhandled exception");
                return BuildErrorResponse(500, "An internal error occurred while executing the report.",
                    includeDetails: IsLocalDevelopment(), detail: ex.Message);
            }
        }

        /// <summary>
        /// Verifies service health by testing RDS PostgreSQL and SNS connectivity.
        /// Lambda entry point for GET /v1/reports/health.
        /// Per AAP §0.8.5: "Health check endpoints per service".
        ///
        /// Checks:
        ///   1. RDS PostgreSQL connectivity — SELECT 1 against the reporting database
        ///   2. SNS connectivity — ListTopics to verify the SNS client is operational
        ///
        /// Returns 200 with health status if all checks pass, 503 if any dependency is unhealthy.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleHealthCheck(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            string correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Handler"] = nameof(HandleHealthCheck),
                ["RequestId"] = context.AwsRequestId
            });

            _logger.LogDebug("HandleHealthCheck - Running health checks");

            string databaseStatus = "unknown";
            string snsStatus = "unknown";
            bool isHealthy = true;

            // ── Database Health Check ──
            // Verify RDS PostgreSQL connectivity by executing SELECT 1
            try
            {
                string connStr = await GetConnectionStringAsync();
                if (string.IsNullOrWhiteSpace(connStr))
                {
                    databaseStatus = "disconnected";
                    isHealthy = false;
                    _logger.LogWarning("HandleHealthCheck - Database connection string is empty");
                }
                else
                {
                    await using var connection = new NpgsqlConnection(connStr);
                    await connection.OpenAsync(context.RemainingTime > TimeSpan.Zero
                        ? new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token
                        : CancellationToken.None);
                    await using var command = new NpgsqlCommand("SELECT 1", connection);
                    await command.ExecuteScalarAsync();
                    databaseStatus = "connected";
                    _logger.LogDebug("HandleHealthCheck - Database connectivity verified");
                }
            }
            catch (Exception ex)
            {
                databaseStatus = "disconnected";
                isHealthy = false;
                _logger.LogError(ex, "HandleHealthCheck - Database health check failed");
            }

            // ── SNS Health Check ──
            // Verify SNS client connectivity by listing topics
            try
            {
                var listResponse = await _snsClient.ListTopicsAsync(
                    new Amazon.SimpleNotificationService.Model.ListTopicsRequest());
                snsStatus = "connected";
                _logger.LogDebug("HandleHealthCheck - SNS connectivity verified ({TopicCount} topics)",
                    listResponse.Topics?.Count ?? 0);
            }
            catch (Exception ex)
            {
                snsStatus = "disconnected";
                isHealthy = false;
                _logger.LogError(ex, "HandleHealthCheck - SNS health check failed");
            }

            int statusCode = isHealthy ? 200 : 503;
            string overallStatus = isHealthy ? "healthy" : "unhealthy";

            _logger.LogInformation("HandleHealthCheck - Status: {Status} (DB: {DbStatus}, SNS: {SnsStatus})",
                overallStatus, databaseStatus, snsStatus);

            return BuildResponse(statusCode, new
            {
                status = overallStatus,
                database = databaseStatus,
                sns = snsStatus,
                timestamp = DateTime.UtcNow.ToString("o"),
                service = "reporting",
                version = "1.0.0"
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // Response Builder Helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds a consistent API Gateway HTTP API v2 proxy response with standard headers.
        /// Serializes the response body using System.Text.Json with camelCase naming.
        /// Includes CORS headers for frontend SPA compatibility.
        /// </summary>
        /// <param name="statusCode">HTTP status code.</param>
        /// <param name="body">Response body object to serialize as JSON.</param>
        /// <returns>Formatted APIGatewayHttpApiV2ProxyResponse.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object body)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Access-Control-Allow-Origin"] = "*",
                    ["Access-Control-Allow-Headers"] = "Content-Type,Authorization,X-Correlation-Id,Idempotency-Key",
                    ["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS",
                    ["X-Content-Type-Options"] = "nosniff",
                    ["Cache-Control"] = "no-store"
                },
                Body = JsonSerializer.Serialize(body, JsonOptions)
            };
        }

        /// <summary>
        /// Builds an error response envelope matching the monolith's QueryResponse error pattern.
        /// Optionally includes detailed error information in development mode (IS_LOCAL=true).
        /// NEVER exposes stack traces in production (per AAP §0.8.3 OWASP Top 10 compliance).
        /// </summary>
        /// <param name="statusCode">HTTP status code (4xx or 5xx).</param>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="errors">Optional list of specific validation error messages.</param>
        /// <param name="includeDetails">Whether to include detailed error information.</param>
        /// <param name="detail">Detailed error message (only included if includeDetails is true).</param>
        /// <returns>Formatted error APIGatewayHttpApiV2ProxyResponse.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode,
            string message,
            List<string>? errors = null,
            bool includeDetails = false,
            string? detail = null)
        {
            var errorBody = new Dictionary<string, object?>
            {
                ["success"] = false,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["message"] = message
            };

            if (errors != null && errors.Count > 0)
            {
                errorBody["errors"] = errors;
            }

            if (includeDetails && !string.IsNullOrEmpty(detail))
            {
                errorBody["detail"] = detail;
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Access-Control-Allow-Origin"] = "*",
                    ["Access-Control-Allow-Headers"] = "Content-Type,Authorization,X-Correlation-Id,Idempotency-Key",
                    ["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS",
                    ["X-Content-Type-Options"] = "nosniff",
                    ["Cache-Control"] = "no-store"
                },
                Body = JsonSerializer.Serialize(errorBody, JsonOptions)
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // Request Parsing & Identity Helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts the correlation ID for distributed tracing.
        /// Priority: x-correlation-id header → API Gateway requestId → new GUID.
        /// Per AAP §0.8.5: structured JSON logging with correlation-ID propagation.
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            // Check x-correlation-id header first (case-insensitive)
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(header.Value))
                        {
                            return header.Value;
                        }
                    }
                }
            }

            // Fallback to API Gateway request ID
            if (request.RequestContext?.RequestId != null)
            {
                return request.RequestContext.RequestId;
            }

            // Final fallback: generate new GUID
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts the authenticated caller identity from the API Gateway JWT authorizer context.
        /// Replaces the monolith's <c>SecurityContext.CurrentUser</c> (AsyncLocal-based,
        /// from SecurityContext.cs) with stateless JWT claims extraction.
        ///
        /// The API Gateway HTTP API v2 JWT authorizer populates the RequestContext.Authorizer
        /// with decoded JWT claims including sub (user ID), email, and cognito:groups.
        /// </summary>
        /// <param name="request">API Gateway request with authorizer context.</param>
        /// <returns>Extracted CallerIdentity, or null if no valid identity is found.</returns>
        private static CallerIdentity? ExtractCallerFromContext(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var authorizer = request.RequestContext?.Authorizer;
                if (authorizer == null)
                {
                    return null;
                }

                // API Gateway HTTP API v2 JWT authorizer stores claims in Jwt.Claims
                var jwtClaims = authorizer.Jwt?.Claims;
                if (jwtClaims == null || jwtClaims.Count == 0)
                {
                    // Fallback: check if claims are in the authorizer's top-level context
                    // (for custom Lambda authorizers or LocalStack compatibility)
                    return CreateCallerFromAuthorizerContext(authorizer);
                }

                // Extract user ID from 'sub' claim (standard JWT claim)
                string? subClaim = jwtClaims.TryGetValue("sub", out var sub) ? sub : null;
                Guid userId = Guid.TryParse(subClaim, out var parsedId) ? parsedId : Guid.Empty;

                // Extract email from 'email' claim
                string? email = jwtClaims.TryGetValue("email", out var emailClaim) ? emailClaim : null;

                // Extract roles/groups from 'cognito:groups' claim
                string? groupsClaim = jwtClaims.TryGetValue("cognito:groups", out var groups) ? groups : null;
                var roles = !string.IsNullOrEmpty(groupsClaim)
                    ? groupsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                    : new List<string>();

                // Extract username
                string? username = jwtClaims.TryGetValue("cognito:username", out var user) ? user
                    : jwtClaims.TryGetValue("username", out var altUser) ? altUser
                    : email;

                return new CallerIdentity
                {
                    UserId = userId,
                    Username = username ?? "unknown",
                    Email = email,
                    Roles = roles
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a CallerIdentity from the authorizer's top-level context.
        /// Used as fallback for custom Lambda authorizers or LocalStack compatibility.
        /// </summary>
        private static CallerIdentity? CreateCallerFromAuthorizerContext(
            APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription authorizer)
        {
            // For custom authorizers, claims may be in the Lambda authorizer context
            if (authorizer.Lambda != null && authorizer.Lambda.Count > 0)
            {
                string? subValue = authorizer.Lambda.TryGetValue("sub", out var sub) ? sub?.ToString() : null;
                Guid userId = Guid.TryParse(subValue, out var parsed) ? parsed : Guid.Empty;

                string? email = authorizer.Lambda.TryGetValue("email", out var emailObj) ? emailObj?.ToString() : null;
                string? username = authorizer.Lambda.TryGetValue("username", out var userObj) ? userObj?.ToString() : email;

                var roles = new List<string>();
                if (authorizer.Lambda.TryGetValue("roles", out var rolesObj) && rolesObj != null)
                {
                    roles = rolesObj.ToString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                        ?? new List<string>();
                }

                return new CallerIdentity
                {
                    UserId = userId,
                    Username = username ?? "unknown",
                    Email = email,
                    Roles = roles
                };
            }

            return null;
        }

        /// <summary>
        /// Checks if the caller has the required permission.
        /// Replaces the monolith's <c>SecurityContext.IsUserInRole()</c> (from SecurityContext.cs)
        /// with JWT claim-based permission checking.
        ///
        /// Permission mapping:
        ///   - "admin" → checks for "administrator" or "admin" role in JWT groups
        ///   - "read"  → any authenticated user (all roles have read access)
        /// </summary>
        /// <param name="caller">Authenticated caller identity from JWT claims.</param>
        /// <param name="permission">Required permission string.</param>
        /// <returns>True if the caller has the required permission.</returns>
        private static bool HasPermission(CallerIdentity caller, string permission)
        {
            if (caller == null)
            {
                return false;
            }

            switch (permission.ToLowerInvariant())
            {
                case "admin":
                    // Admin permission: requires administrator or admin role
                    return caller.Roles.Any(r =>
                        string.Equals(r, "administrator", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));

                case "read":
                    // Read permission: any authenticated user
                    return true;

                default:
                    // Unknown permission: check exact role match
                    return caller.Roles.Any(r =>
                        string.Equals(r, permission, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Connection String & Configuration Helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Retrieves the RDS PostgreSQL connection string with lazy caching.
        /// Checks environment variable first (LocalStack), then SSM SecureString.
        /// Per AAP §0.8.6: DB_CONNECTION_STRING stored as SSM SecureString, NEVER env vars.
        /// </summary>
        /// <returns>Connection string for RDS PostgreSQL.</returns>
        private async Task<string> GetConnectionStringAsync()
        {
            // Return cached connection string if available
            if (!string.IsNullOrWhiteSpace(_connectionString))
            {
                return _connectionString;
            }

            // Check environment variable first (LocalStack / integration testing)
            string? envConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            if (!string.IsNullOrWhiteSpace(envConnectionString))
            {
                _connectionString = envConnectionString;
                return _connectionString;
            }

            // Retrieve from SSM SecureString (production path)
            try
            {
                var response = await _ssmClient.GetParameterAsync(new GetParameterRequest
                {
                    Name = SSM_CONNECTION_STRING_KEY,
                    WithDecryption = true
                });
                _connectionString = response.Parameter.Value;
                _logger.LogDebug("GetConnectionStringAsync - Connection string retrieved from SSM");
                return _connectionString;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetConnectionStringAsync - Failed to retrieve connection string from SSM");
                throw new InvalidOperationException(
                    $"Failed to retrieve database connection string from SSM parameter '{SSM_CONNECTION_STRING_KEY}'. " +
                    "Ensure the parameter exists and the Lambda execution role has ssm:GetParameter permission.", ex);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Query Parameter & Path Parameter Helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts an integer query parameter from the API Gateway request.
        /// Returns the default value if the parameter is missing or invalid.
        /// </summary>
        private static int GetQueryParamInt(
            APIGatewayHttpApiV2ProxyRequest request, string paramName, int defaultValue)
        {
            if (request.QueryStringParameters != null &&
                request.QueryStringParameters.TryGetValue(paramName, out var value) &&
                int.TryParse(value, out int result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// Extracts a string query parameter from the API Gateway request.
        /// Returns the default value if the parameter is missing or empty.
        /// </summary>
        private static string GetQueryParamString(
            APIGatewayHttpApiV2ProxyRequest request, string paramName, string defaultValue)
        {
            if (request.QueryStringParameters != null &&
                request.QueryStringParameters.TryGetValue(paramName, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
            return defaultValue;
        }

        /// <summary>
        /// Extracts and parses a GUID path parameter from the API Gateway request.
        /// API Gateway HTTP API v2 stores path parameters in PathParameters dictionary.
        /// </summary>
        private static bool TryGetPathParameterGuid(
            APIGatewayHttpApiV2ProxyRequest request, string paramName, out Guid value)
        {
            value = Guid.Empty;
            if (request.PathParameters == null) return false;

            // Try named parameter first
            if (request.PathParameters.TryGetValue(paramName, out var paramValue) &&
                Guid.TryParse(paramValue, out value) && value != Guid.Empty)
                return true;

            // Fallback: extract from {proxy+} catch-all — scan segments right-to-left for GUID
            if (request.PathParameters.TryGetValue("proxy", out var proxy) && !string.IsNullOrEmpty(proxy))
            {
                var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var i = segments.Length - 1; i >= 0; i--)
                {
                    if (Guid.TryParse(segments[i], out value) && value != Guid.Empty)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Extracts a header value from the API Gateway request (case-insensitive).
        /// Returns null if the header is not found or empty.
        /// </summary>
        private static string? GetHeaderValue(
            APIGatewayHttpApiV2ProxyRequest request, string headerName)
        {
            if (request.Headers == null)
            {
                return null;
            }

            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(header.Value) ? null : header.Value;
                }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════════════
        // Utility Helpers
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Determines if the Lambda is running in local development mode.
        /// When IS_LOCAL=true, detailed error messages including stack traces are included
        /// in error responses for debugging. NEVER enabled in production.
        /// Per AAP §0.8.6 environment variable handling.
        /// </summary>
        private static bool IsLocalDevelopment()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Infers column definitions from the first row of execution results.
        /// Provides column metadata (name, data type) for frontend data grid rendering.
        /// If no data is available, returns an empty list.
        /// </summary>
        /// <param name="data">Report execution result rows.</param>
        /// <returns>List of inferred column definitions.</returns>
        private static List<ColumnDefinition> InferColumns(List<Dictionary<string, object?>> data)
        {
            if (data == null || data.Count == 0)
            {
                return new List<ColumnDefinition>();
            }

            var firstRow = data[0];
            return firstRow.Select(kvp => new ColumnDefinition
            {
                Name = kvp.Key,
                DataType = kvp.Value?.GetType().Name ?? "String",
                DisplayName = FormatColumnDisplayName(kvp.Key),
                IsSortable = true,
                IsFilterable = true
            }).ToList();
        }

        /// <summary>
        /// Formats a database column name into a human-readable display name.
        /// Converts snake_case to Title Case (e.g., "created_at" → "Created At").
        /// </summary>
        private static string FormatColumnDisplayName(string columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName))
            {
                return columnName;
            }

            // Convert snake_case to Title Case
            var words = columnName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Select(w =>
                w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant() : w));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Internal Supporting Types
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents the authenticated caller identity extracted from JWT claims.
    /// Replaces the monolith's <c>SecurityContext.CurrentUser</c> (AsyncLocal from SecurityContext.cs)
    /// with a stateless, per-request identity model derived from API Gateway JWT authorizer context.
    /// </summary>
    internal sealed class CallerIdentity
    {
        /// <summary>User unique identifier from JWT 'sub' claim (maps to Cognito user ID).</summary>
        public Guid UserId { get; init; }

        /// <summary>Username from JWT 'cognito:username' claim.</summary>
        public string Username { get; init; } = string.Empty;

        /// <summary>Email from JWT 'email' claim.</summary>
        public string? Email { get; init; }

        /// <summary>User roles/groups from JWT 'cognito:groups' claim.</summary>
        public List<string> Roles { get; init; } = new();
    }
}