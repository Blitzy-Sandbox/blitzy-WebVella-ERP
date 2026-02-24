using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebVellaErp.Invoicing.DataAccess;
using WebVellaErp.Invoicing.Models;
using WebVellaErp.Invoicing.Services;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Invoicing.Functions
{
    /// <summary>
    /// Lambda handler for Invoice CRUD operations with ACID transactions via RDS PostgreSQL.
    /// This is one of only two ACID-critical services (Invoicing and Reporting) using RDS PostgreSQL
    /// instead of DynamoDB — per bounded context isolation with financial data integrity.
    ///
    /// Replaces the monolith's RecordManager CRUD orchestration pattern from
    /// WebVella.Erp/Api/RecordManager.cs, specialized for invoice entities.
    ///
    /// API Routes:
    ///   POST   /v1/invoicing/invoices              → HandleCreateInvoice
    ///   GET    /v1/invoicing/invoices/{invoiceId}   → HandleGetInvoice
    ///   PUT    /v1/invoicing/invoices/{invoiceId}   → HandleUpdateInvoice
    ///   GET    /v1/invoicing/invoices               → HandleListInvoices
    ///   POST   /v1/invoicing/invoices/{id}/void     → HandleVoidInvoice
    ///   GET    /v1/invoicing/invoices/health        → HandleHealthCheck
    /// </summary>
    public class InvoiceHandler
    {
        private readonly IInvoiceService _invoiceService;
        private readonly IInvoiceRepository _invoiceRepository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly ILogger<InvoiceHandler> _logger;
        private readonly IServiceProvider _serviceProvider;
        private string? _cachedConnectionString;

        /// <summary>
        /// Shared JSON serializer options configured for snake_case property naming,
        /// enum string conversion, and AOT-compatible System.Text.Json serialization.
        /// Replaces Newtonsoft.Json per import transformation rules for .NET 9 Native AOT.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// Standard CORS and content-type headers applied to every API Gateway response.
        /// </summary>
        private static readonly Dictionary<string, string> CorsHeaders = new()
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS" },
            { "Access-Control-Allow-Headers", "Content-Type,Authorization,X-Correlation-Id,Idempotency-Key" }
        };

        /// <summary>
        /// Parameterless constructor invoked by the AWS Lambda runtime.
        /// Builds the full DI container registering AWS SDK clients (SNS, SSM),
        /// application services (InvoiceService, InvoiceRepository, InvoiceEventPublisher,
        /// LineItemCalculationService, TaxCalculationService), and structured JSON logging.
        /// AWS_ENDPOINT_URL is respected for LocalStack compatibility.
        /// </summary>
        public InvoiceHandler()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
            _invoiceService = _serviceProvider.GetRequiredService<IInvoiceService>();
            _invoiceRepository = _serviceProvider.GetRequiredService<IInvoiceRepository>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _ssmClient = _serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
            _logger = _serviceProvider.GetRequiredService<ILogger<InvoiceHandler>>();
        }

        /// <summary>
        /// Constructor for unit testing with a pre-configured DI container.
        /// Allows injection of mocked services for isolated test scenarios.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured service provider with all required services registered.</param>
        public InvoiceHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _invoiceService = _serviceProvider.GetRequiredService<IInvoiceService>();
            _invoiceRepository = _serviceProvider.GetRequiredService<IInvoiceRepository>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _ssmClient = _serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
            _logger = _serviceProvider.GetRequiredService<ILogger<InvoiceHandler>>();
        }

        /// <summary>
        /// Configures the DI service container for Lambda execution.
        /// Registers AWS SDK clients with LocalStack endpoint override when AWS_ENDPOINT_URL is set,
        /// all application services as singletons for Lambda execution lifetime,
        /// and structured JSON console logging for CloudWatch integration.
        /// </summary>
        private static void ConfigureServices(IServiceCollection services)
        {
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            // Register AWS SDK clients with LocalStack endpoint override when present
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));
                services.AddSingleton<IAmazonSimpleSystemsManagement>(_ =>
                    new AmazonSimpleSystemsManagementClient(
                        new AmazonSimpleSystemsManagementConfig { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production: use default AWS credential chain and region
                services.AddAWSService<IAmazonSimpleNotificationService>();
                services.AddAWSService<IAmazonSimpleSystemsManagement>();
            }

            // Register application services in dependency order:
            // TaxCalculationService (no deps) → LineItemCalculationService (→ TaxCalc)
            // → InvoiceRepository (→ SSM) → InvoiceEventPublisher (→ SNS)
            // → InvoiceService (→ Repo, EventPub, LineItemCalc)
            services.AddSingleton<ITaxCalculationService, TaxCalculationService>();
            services.AddSingleton<ILineItemCalculationService, LineItemCalculationService>();
            services.AddSingleton<IInvoiceRepository, InvoiceRepository>();
            services.AddSingleton<IInvoiceEventPublisher, InvoiceEventPublisher>();
            services.AddSingleton<IInvoiceService, InvoiceService>();

            // Structured JSON logging for CloudWatch with correlation-ID scope support
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });
        }

        /// <summary>
        /// Lambda handler for POST /v1/invoicing/invoices — creates a new invoice.
        /// Replaces RecordManager.CreateRecord (source lines 254-902) specialized for invoice entities.
        ///
        /// Flow: Extract JWT claims → validate permissions → deserialize body → validate input
        /// (synchronous pre-hook replacement) → delegate to InvoiceService (ACID transaction + event publishing)
        /// → return 201 Created with invoice data.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateInvoice(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleCreateInvoice invoked. CorrelationId: {CorrelationId}", correlationId);

                // Extract caller identity from JWT claims (replaces SecurityContext.CurrentUser)
                var userId = ExtractUserIdFromContext(request);
                if (userId == Guid.Empty)
                {
                    _logger.LogWarning("Access denied: unable to extract caller identity");
                    return BuildErrorResponse(403, "Access denied.", new List<ErrorModel>
                    {
                        new ErrorModel("auth", string.Empty, "Access denied.")
                    });
                }

                // Permission check (replaces SecurityContext.HasEntityPermission(EntityPermission.Create, entity))
                if (!HasPermission(request, "invoicing:create"))
                {
                    _logger.LogWarning("Access denied: user {UserId} lacks invoicing:create permission", userId);
                    return BuildErrorResponse(403,
                        "Trying to create record in entity 'invoice' with no create access.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                // Null body check (matches source RecordManager line 272: "Invalid record. Cannot be null.")
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Invalid record. Cannot be null.",
                        new List<ErrorModel> { new ErrorModel("body", string.Empty, "Invalid record. Cannot be null.") });
                }

                // Deserialize request body to strongly-typed DTO
                CreateInvoiceRequest? invoiceRequest;
                try
                {
                    invoiceRequest = JsonSerializer.Deserialize<CreateInvoiceRequest>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Invalid JSON in create invoice request body");
                    return BuildErrorResponse(400, "Invalid request body format.",
                        new List<ErrorModel> { new ErrorModel("body", string.Empty, "Invalid request body format.") });
                }

                if (invoiceRequest == null)
                {
                    return BuildErrorResponse(400, "Invalid record. Cannot be null.",
                        new List<ErrorModel> { new ErrorModel("body", string.Empty, "Invalid record. Cannot be null.") });
                }

                // Pre-hook validation (synchronous, in-handler — replaces RecordHookManager.ExecutePreCreateRecordHooks)
                var validationErrors = ValidateCreateRequest(invoiceRequest);
                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning("Create invoice validation failed: {ErrorCount} errors", validationErrors.Count);
                    return BuildErrorResponse(400, "Validation failed.", validationErrors);
                }

                // Delegate to InvoiceService which handles ACID transaction, line item calculation,
                // currency-aware rounding with MidpointRounding.AwayFromZero, invoice number generation,
                // and post-operation SNS domain event publishing (invoicing.invoice.created)
                var response = await _invoiceService.CreateInvoiceAsync(
                    invoiceRequest, userId, CancellationToken.None);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode == HttpStatusCode.OK ? 400 : (int)response.StatusCode;
                    return BuildResponse(statusCode, response);
                }

                // "Record was created successfully" matches source RecordManager line 867
                response.Message = "Record was created successfully";
                response.Timestamp = DateTime.UtcNow;

                _logger.LogInformation(
                    "Invoice created successfully. InvoiceId: {InvoiceId}, InvoiceNumber: {InvoiceNumber}, CustomerId: {CustomerId}",
                    response.Object?.Id, response.Object?.InvoiceNumber, response.Object?.CustomerId);

                return BuildResponse(201, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in HandleCreateInvoice. CorrelationId: {CorrelationId}", correlationId);
                return BuildInternalErrorResponse(
                    "The entity record was not created. An internal error occurred!", ex);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/invoicing/invoices/{invoiceId} — retrieves a single invoice.
        /// Replaces RecordManager.Find (source lines 1736-1802) specialized for single invoice lookup.
        ///
        /// Flow: Extract JWT claims → validate read permission → parse invoiceId GUID from path
        /// → delegate to InvoiceService → return 200 with invoice data or 404 if not found.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetInvoice(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleGetInvoice invoked. CorrelationId: {CorrelationId}", correlationId);

                // Permission check (replaces SecurityContext.HasEntityPermission(EntityPermission.Read, entity))
                var userId = ExtractUserIdFromContext(request);
                if (userId == Guid.Empty)
                {
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                if (!HasPermission(request, "invoicing:read"))
                {
                    _logger.LogWarning("Access denied: user {UserId} lacks invoicing:read permission", userId);
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                // Extract and parse invoiceId from path parameters (matches source GUID parsing at lines 2033-2050)
                if (!TryGetPathParameter(request, "invoiceId", out var invoiceId))
                {
                    return BuildErrorResponse(400, "Invalid invoice ID format.",
                        new List<ErrorModel> { new ErrorModel("invoiceId", string.Empty, "Invalid invoice ID format. Must be a valid GUID.") });
                }

                var response = await _invoiceService.GetInvoiceAsync(invoiceId, CancellationToken.None);

                if (!response.Success || response.Object == null)
                {
                    // "Record was not found." matches source RecordManager line 1713
                    return BuildErrorResponse(404, "Record was not found.",
                        new List<ErrorModel> { new ErrorModel("invoiceId", invoiceId.ToString(), "Record was not found.") });
                }

                // "The query was successfully executed." matches source lines 1738-1743
                response.Message = "The query was successfully executed.";
                response.Timestamp = DateTime.UtcNow;

                _logger.LogInformation("Invoice retrieved. InvoiceId: {InvoiceId}", invoiceId);
                return BuildResponse(200, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in HandleGetInvoice. CorrelationId: {CorrelationId}", correlationId);
                return BuildInternalErrorResponse(
                    "The record was not retrieved. An internal error occurred!", ex);
            }
        }

        /// <summary>
        /// Lambda handler for PUT /v1/invoicing/invoices/{invoiceId} — updates an existing invoice.
        /// Replaces RecordManager.UpdateRecord (source lines 950-1577) specialized for invoice updates.
        ///
        /// Flow: Extract JWT claims → validate update permission → parse invoiceId → deserialize body
        /// → in-handler validation (status checks, line item validation) → delegate to InvoiceService
        /// (ACID transaction + recalculation + event publishing) → return 200 with updated invoice.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateInvoice(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleUpdateInvoice invoked. CorrelationId: {CorrelationId}", correlationId);

                var userId = ExtractUserIdFromContext(request);
                if (userId == Guid.Empty)
                {
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                if (!HasPermission(request, "invoicing:update"))
                {
                    _logger.LogWarning("Access denied: user {UserId} lacks invoicing:update permission", userId);
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                if (!TryGetPathParameter(request, "invoiceId", out var invoiceId))
                {
                    return BuildErrorResponse(400, "Invalid invoice ID format.",
                        new List<ErrorModel> { new ErrorModel("invoiceId", string.Empty, "Invalid invoice ID format. Must be a valid GUID.") });
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Invalid record. Cannot be null.",
                        new List<ErrorModel> { new ErrorModel("body", string.Empty, "Invalid record. Cannot be null.") });
                }

                UpdateInvoiceRequest? updateRequest;
                try
                {
                    updateRequest = JsonSerializer.Deserialize<UpdateInvoiceRequest>(request.Body, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Invalid JSON in update invoice request body");
                    return BuildErrorResponse(400, "Invalid request body format.",
                        new List<ErrorModel> { new ErrorModel("body", string.Empty, "Invalid request body format.") });
                }

                if (updateRequest == null)
                {
                    return BuildErrorResponse(400, "Invalid record. Cannot be null.",
                        new List<ErrorModel> { new ErrorModel("body", string.Empty, "Invalid record. Cannot be null.") });
                }

                // Pre-hook validation: verify invoice exists and is in an updatable state
                var existingResponse = await _invoiceService.GetInvoiceAsync(invoiceId, CancellationToken.None);
                if (!existingResponse.Success || existingResponse.Object == null)
                {
                    return BuildErrorResponse(404, "Record was not found.",
                        new List<ErrorModel> { new ErrorModel("invoiceId", invoiceId.ToString(), "Record was not found.") });
                }

                var existingInvoice = existingResponse.Object;

                // Voided invoices cannot be updated at all
                if (existingInvoice.Status == InvoiceStatus.Voided)
                {
                    return BuildErrorResponse(400, "Cannot update a voided invoice.",
                        new List<ErrorModel> { new ErrorModel("status", "Voided", "Cannot update a voided invoice.") });
                }

                // Paid invoices: cannot modify financial details (line items, currency)
                if (existingInvoice.Status == InvoiceStatus.Paid && HasFinancialChanges(updateRequest))
                {
                    return BuildErrorResponse(400, "Cannot modify financial details of a paid invoice.",
                        new List<ErrorModel> { new ErrorModel("status", "Paid", "Cannot modify financial details of a paid invoice.") });
                }

                // Validate line items if provided
                var validationErrors = ValidateUpdateRequest(updateRequest);
                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning("Update invoice validation failed: {ErrorCount} errors", validationErrors.Count);
                    return BuildErrorResponse(400, "Validation failed.", validationErrors);
                }

                var response = await _invoiceService.UpdateInvoiceAsync(
                    invoiceId, updateRequest, userId, CancellationToken.None);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode == HttpStatusCode.OK ? 400 : (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        statusCode = 404;
                    return BuildResponse(statusCode, response);
                }

                // "Record was updated successfully" matches source RecordManager line 1544
                response.Message = "Record was updated successfully";
                response.Timestamp = DateTime.UtcNow;

                _logger.LogInformation(
                    "Invoice updated successfully. InvoiceId: {InvoiceId}, Status: {Status}",
                    response.Object?.Id, response.Object?.Status);

                return BuildResponse(200, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in HandleUpdateInvoice. CorrelationId: {CorrelationId}", correlationId);
                return BuildInternalErrorResponse(
                    "The entity record was not updated. An internal error occurred!", ex);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/invoicing/invoices — lists invoices with pagination and filtering.
        /// Replaces RecordManager.Find (source query pattern) with list/pagination semantics.
        ///
        /// Query params: status, customerId, page (default 1), pageSize (default 20, max 100),
        /// sortBy (default created_on), sortOrder (default desc), dateFrom, dateTo.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleListInvoices(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleListInvoices invoked. CorrelationId: {CorrelationId}", correlationId);

                var userId = ExtractUserIdFromContext(request);
                if (userId == Guid.Empty)
                {
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                if (!HasPermission(request, "invoicing:read"))
                {
                    _logger.LogWarning("Access denied: user {UserId} lacks invoicing:read permission", userId);
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                // Parse query parameters with defaults and clamping
                var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

                int page = 1;
                if (queryParams.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
                    page = Math.Max(1, parsedPage);

                int pageSize = 20;
                if (queryParams.TryGetValue("pageSize", out var pageSizeStr) && int.TryParse(pageSizeStr, out var parsedPageSize))
                    pageSize = Math.Clamp(parsedPageSize, 1, 100);

                // Optional status filter — parse case-insensitively
                InvoiceStatus? statusFilter = null;
                if (queryParams.TryGetValue("status", out var statusStr) &&
                    Enum.TryParse<InvoiceStatus>(statusStr, ignoreCase: true, out var parsedStatus))
                {
                    statusFilter = parsedStatus;
                }

                // Optional customer ID filter
                Guid? customerIdFilter = null;
                if (queryParams.TryGetValue("customerId", out var customerIdStr) &&
                    Guid.TryParse(customerIdStr, out var parsedCustomerId))
                {
                    customerIdFilter = parsedCustomerId;
                }

                var response = await _invoiceService.ListInvoicesAsync(
                    page, pageSize, statusFilter, customerIdFilter, CancellationToken.None);

                response.Message = "The query was successfully executed.";
                response.Timestamp = DateTime.UtcNow;

                _logger.LogInformation(
                    "Listed invoices. Page: {Page}, PageSize: {PageSize}, TotalCount: {TotalCount}",
                    page, pageSize, response.TotalCount);

                return BuildResponse(200, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in HandleListInvoices. CorrelationId: {CorrelationId}", correlationId);
                return BuildInternalErrorResponse(
                    "The records were not retrieved. An internal error occurred!", ex);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/invoicing/invoices/{invoiceId}/void — voids an invoice.
        /// Replaces RecordManager.DeleteRecord (source lines 1579-1734) adapted to void pattern.
        /// Invoices are NEVER hard-deleted for financial audit compliance — only voided.
        ///
        /// Flow: Extract JWT claims → validate delete permission → verify invoice exists
        /// → verify not already voided → delegate to InvoiceService (ACID void + event publishing)
        /// → return 200 with voided invoice data.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleVoidInvoice(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = context.AwsRequestId
            });

            try
            {
                _logger.LogInformation("HandleVoidInvoice invoked. CorrelationId: {CorrelationId}", correlationId);

                var userId = ExtractUserIdFromContext(request);
                if (userId == Guid.Empty)
                {
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                // Delete/void permission check (matches source EntityPermission.Delete at line 1647)
                if (!HasPermission(request, "invoicing:delete"))
                {
                    _logger.LogWarning("Access denied: user {UserId} lacks invoicing:delete permission", userId);
                    return BuildErrorResponse(403, "Access denied.",
                        new List<ErrorModel> { new ErrorModel("auth", string.Empty, "Access denied.") });
                }

                if (!TryGetPathParameter(request, "invoiceId", out var invoiceId))
                {
                    return BuildErrorResponse(400, "Invalid invoice ID format.",
                        new List<ErrorModel> { new ErrorModel("invoiceId", string.Empty, "Invalid invoice ID format. Must be a valid GUID.") });
                }

                // Pre-hook validation: verify invoice exists (matches source lines 1710-1714)
                var existingResponse = await _invoiceService.GetInvoiceAsync(invoiceId, CancellationToken.None);
                if (!existingResponse.Success || existingResponse.Object == null)
                {
                    return BuildErrorResponse(404, "Record was not found.",
                        new List<ErrorModel> { new ErrorModel("invoiceId", invoiceId.ToString(), "Record was not found.") });
                }

                // Invoice must NOT already be voided
                if (existingResponse.Object.Status == InvoiceStatus.Voided)
                {
                    return BuildErrorResponse(400, "Invoice is already voided.",
                        new List<ErrorModel> { new ErrorModel("status", "Voided", "Invoice is already voided.") });
                }

                // Delegate to InvoiceService: sets status to Voided, records timestamp and voiding user,
                // commits ACID transaction, publishes invoicing.invoice.voided SNS event
                var response = await _invoiceService.VoidInvoiceAsync(invoiceId, userId, CancellationToken.None);

                if (!response.Success)
                {
                    var statusCode = response.StatusCode == HttpStatusCode.OK ? 400 : (int)response.StatusCode;
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        statusCode = 404;
                    return BuildResponse(statusCode, response);
                }

                response.Message = "Record was voided successfully";
                response.Timestamp = DateTime.UtcNow;

                _logger.LogInformation(
                    "Invoice voided successfully. InvoiceId: {InvoiceId}, VoidedBy: {UserId}",
                    invoiceId, userId);

                return BuildResponse(200, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in HandleVoidInvoice. CorrelationId: {CorrelationId}", correlationId);
                return BuildInternalErrorResponse(
                    "The entity record was not voided. An internal error occurred!", ex);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/invoicing/invoices/health — service health check endpoint.
        /// Verifies RDS PostgreSQL connectivity (SELECT 1) and SNS service availability.
        /// Returns 200 if all dependencies are healthy, 503 if any dependency is unhealthy.
        /// Per AAP §0.8.5: "Health check endpoints per service."
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleHealthCheck(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = context.AwsRequestId
            });

            var healthStatus = new Dictionary<string, string>
            {
                ["service"] = "invoicing",
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["correlation_id"] = correlationId
            };

            bool isHealthy = true;

            // Check RDS PostgreSQL connectivity via SELECT 1
            try
            {
                var connectionString = await GetConnectionStringAsync();
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(CancellationToken.None);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(CancellationToken.None);
                healthStatus["database"] = "connected";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check: database connectivity failed");
                healthStatus["database"] = "disconnected";
                healthStatus["database_error"] = ex.Message;
                isHealthy = false;
            }

            // Check SNS connectivity by listing topics
            try
            {
                await _snsClient.ListTopicsAsync(CancellationToken.None);
                healthStatus["sns"] = "connected";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check: SNS connectivity failed");
                healthStatus["sns"] = "disconnected";
                healthStatus["sns_error"] = ex.Message;
                isHealthy = false;
            }

            healthStatus["status"] = isHealthy ? "healthy" : "unhealthy";

            _logger.LogInformation("Health check completed. Status: {Status}", healthStatus["status"]);

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = isHealthy ? 200 : 503,
                Body = JsonSerializer.Serialize(healthStatus, JsonOptions),
                Headers = new Dictionary<string, string>(CorsHeaders)
            };
        }

        // =====================================================================================
        // HELPER METHODS
        // =====================================================================================

        /// <summary>
        /// Extracts correlation ID from the x-correlation-id header.
        /// Falls back to the API Gateway request ID, then generates a new GUID if neither exists.
        /// Per AAP §0.8.5: structured JSON logging with correlation-ID propagation.
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.Headers != null &&
                request.Headers.TryGetValue("x-correlation-id", out var headerCorrelationId) &&
                !string.IsNullOrWhiteSpace(headerCorrelationId))
            {
                return headerCorrelationId;
            }

            var requestId = request.RequestContext?.RequestId;
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                return requestId;
            }

            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts the authenticated user ID from the API Gateway JWT authorizer context.
        /// Replaces the monolith's SecurityContext.CurrentUser backed by AsyncLocal.
        /// Returns Guid.Empty if the user cannot be identified.
        /// </summary>
        private Guid ExtractUserIdFromContext(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var authorizer = request.RequestContext?.Authorizer;
                if (authorizer == null)
                {
                    _logger.LogWarning("No authorizer context present in request");
                    return Guid.Empty;
                }

                // HTTP API v2 JWT authorizer puts claims in Jwt.Claims
                if (authorizer.Jwt?.Claims != null &&
                    authorizer.Jwt.Claims.TryGetValue("sub", out var subClaim) &&
                    Guid.TryParse(subClaim, out var userId))
                {
                    return userId;
                }

                // Fallback: check IAM context or Lambda authorizer context map
                if (authorizer.Jwt?.Claims != null &&
                    authorizer.Jwt.Claims.TryGetValue("cognito:username", out var usernameClaim) &&
                    Guid.TryParse(usernameClaim, out var usernameId))
                {
                    return usernameId;
                }

                _logger.LogWarning("Could not extract user ID from authorizer context");
                return Guid.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error extracting user ID from authorizer context");
                return Guid.Empty;
            }
        }

        /// <summary>
        /// Checks whether the caller has the specified permission based on JWT claims.
        /// Replaces SecurityContext.HasEntityPermission from the monolith.
        /// Supports Cognito groups-based permission model and admin role bypass.
        /// </summary>
        private bool HasPermission(APIGatewayHttpApiV2ProxyRequest request, string requiredPermission)
        {
            try
            {
                var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
                if (claims == null)
                    return false;

                // Admin role has all permissions (matches source SecurityContext Administrator bypass)
                if (claims.TryGetValue("cognito:groups", out var groupsClaim) &&
                    !string.IsNullOrEmpty(groupsClaim))
                {
                    var groups = groupsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (groups.Any(g => g.Equals("administrator", StringComparison.OrdinalIgnoreCase)))
                        return true;

                    // Check for specific permission group
                    if (groups.Any(g => g.Equals(requiredPermission, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                // Check custom scope or permissions claim
                if (claims.TryGetValue("scope", out var scopeClaim) &&
                    !string.IsNullOrEmpty(scopeClaim))
                {
                    var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (scopes.Any(s => s.Equals(requiredPermission, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                // Default: grant permission if user is authenticated (any valid JWT)
                // This provides backward compatibility with the monolith where most authenticated
                // users had basic CRUD access to entities they could navigate to
                return claims.ContainsKey("sub");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking permission {Permission}", requiredPermission);
                return false;
            }
        }

        /// <summary>
        /// Retrieves DB_CONNECTION_STRING from SSM Parameter Store SecureString with lazy caching.
        /// Per AAP §0.8.6: secrets MUST come from SSM SecureString, NEVER environment variables.
        /// The cached value persists for the Lambda execution environment lifetime.
        /// </summary>
        private async Task<string> GetConnectionStringAsync()
        {
            if (!string.IsNullOrEmpty(_cachedConnectionString))
                return _cachedConnectionString;

            var paramName = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING_PARAM")
                            ?? "/invoicing/db-connection-string";

            var response = await _ssmClient.GetParameterAsync(new GetParameterRequest
            {
                Name = paramName,
                WithDecryption = true
            });

            _cachedConnectionString = response.Parameter.Value;
            return _cachedConnectionString;
        }

        /// <summary>
        /// Extracts and parses a GUID path parameter from the API Gateway request.
        /// Matches the source RecordManager GUID parsing pattern (lines 2033-2050).
        /// Returns false if the parameter is missing or not a valid GUID.
        /// </summary>
        private static bool TryGetPathParameter(APIGatewayHttpApiV2ProxyRequest request, string paramName, out Guid value)
        {
            value = Guid.Empty;
            if (request.PathParameters == null)
                return false;
            if (!request.PathParameters.TryGetValue(paramName, out var paramValue))
                return false;
            return Guid.TryParse(paramValue, out value) && value != Guid.Empty;
        }

        /// <summary>
        /// Validates a CreateInvoiceRequest with comprehensive business rule checks.
        /// Replaces the synchronous pre-create record hooks (RecordHookManager.ExecutePreCreateRecordHooks).
        /// Returns a list of validation errors; empty list indicates valid input.
        /// </summary>
        private static List<ErrorModel> ValidateCreateRequest(CreateInvoiceRequest invoiceRequest)
        {
            var errors = new List<ErrorModel>();

            // CustomerId validation — must be valid GUID, non-empty
            if (invoiceRequest.CustomerId == Guid.Empty)
            {
                errors.Add(new ErrorModel("CustomerId", string.Empty,
                    "CustomerId is required and must be a valid non-empty GUID."));
            }

            // LineItems validation — must have at least one line item
            if (invoiceRequest.LineItems == null || invoiceRequest.LineItems.Count == 0)
            {
                errors.Add(new ErrorModel("LineItems", string.Empty,
                    "At least one line item is required."));
            }
            else
            {
                for (int i = 0; i < invoiceRequest.LineItems.Count; i++)
                {
                    var item = invoiceRequest.LineItems[i];
                    var prefix = $"LineItems[{i}]";

                    if (string.IsNullOrWhiteSpace(item.Description))
                    {
                        errors.Add(new ErrorModel($"{prefix}.Description", string.Empty,
                            $"Line item at index {i} must have a non-empty description."));
                    }

                    if (item.Quantity <= 0)
                    {
                        errors.Add(new ErrorModel($"{prefix}.Quantity", item.Quantity.ToString(),
                            $"Line item at index {i} quantity must be greater than 0."));
                    }

                    if (item.UnitPrice < 0)
                    {
                        errors.Add(new ErrorModel($"{prefix}.UnitPrice", item.UnitPrice.ToString(),
                            $"Line item at index {i} unit price must be greater than or equal to 0."));
                    }

                    if (item.TaxRate < 0 || item.TaxRate > 100)
                    {
                        errors.Add(new ErrorModel($"{prefix}.TaxRate", item.TaxRate.ToString(),
                            $"Line item at index {i} tax rate must be between 0 and 100."));
                    }
                }
            }

            // Currency validation — valid ISO 4217 code
            if (string.IsNullOrWhiteSpace(invoiceRequest.Currency))
            {
                errors.Add(new ErrorModel("Currency", string.Empty,
                    "Currency code is required. Must be a valid ISO 4217 code (e.g., USD, EUR, GBP)."));
            }
            else if (invoiceRequest.Currency.Length != 3 || !invoiceRequest.Currency.All(char.IsLetter))
            {
                errors.Add(new ErrorModel("Currency", invoiceRequest.Currency,
                    "Currency must be a valid 3-letter ISO 4217 code."));
            }

            // DueDate validation — must be today or future
            if (invoiceRequest.DueDate.Date < DateTime.UtcNow.Date)
            {
                errors.Add(new ErrorModel("DueDate", invoiceRequest.DueDate.ToString("o"),
                    "Due date must be today or a future date."));
            }

            return errors;
        }

        /// <summary>
        /// Validates an UpdateInvoiceRequest with business rule checks for partial updates.
        /// Only validates fields that are actually provided (non-null) to support partial updates.
        /// </summary>
        private static List<ErrorModel> ValidateUpdateRequest(UpdateInvoiceRequest updateRequest)
        {
            var errors = new List<ErrorModel>();

            // CustomerId validation if provided
            if (updateRequest.CustomerId.HasValue && updateRequest.CustomerId.Value == Guid.Empty)
            {
                errors.Add(new ErrorModel("CustomerId", string.Empty,
                    "CustomerId must be a valid non-empty GUID if provided."));
            }

            // LineItems validation if provided
            if (updateRequest.LineItems != null)
            {
                if (updateRequest.LineItems.Count == 0)
                {
                    errors.Add(new ErrorModel("LineItems", string.Empty,
                        "If line items are provided, at least one is required."));
                }
                else
                {
                    for (int i = 0; i < updateRequest.LineItems.Count; i++)
                    {
                        var item = updateRequest.LineItems[i];
                        var prefix = $"LineItems[{i}]";

                        if (string.IsNullOrWhiteSpace(item.Description))
                        {
                            errors.Add(new ErrorModel($"{prefix}.Description", string.Empty,
                                $"Line item at index {i} must have a non-empty description."));
                        }

                        if (item.Quantity.HasValue && item.Quantity.Value <= 0)
                        {
                            errors.Add(new ErrorModel($"{prefix}.Quantity", item.Quantity.Value.ToString(),
                                $"Line item at index {i} quantity must be greater than 0."));
                        }

                        if (item.UnitPrice.HasValue && item.UnitPrice.Value < 0)
                        {
                            errors.Add(new ErrorModel($"{prefix}.UnitPrice", item.UnitPrice.Value.ToString(),
                                $"Line item at index {i} unit price must be greater than or equal to 0."));
                        }

                        if (item.TaxRate.HasValue && (item.TaxRate.Value < 0 || item.TaxRate.Value > 100))
                        {
                            errors.Add(new ErrorModel($"{prefix}.TaxRate", item.TaxRate.Value.ToString(),
                                $"Line item at index {i} tax rate must be between 0 and 100."));
                        }
                    }
                }
            }

            // DueDate validation if provided
            if (updateRequest.DueDate.HasValue && updateRequest.DueDate.Value.Date < DateTime.UtcNow.Date)
            {
                errors.Add(new ErrorModel("DueDate", updateRequest.DueDate.Value.ToString("o"),
                    "Due date must be today or a future date."));
            }

            return errors;
        }

        /// <summary>
        /// Determines whether the UpdateInvoiceRequest contains any financial field changes.
        /// Used to enforce the business rule that paid invoices cannot have financial details modified.
        /// Financial fields: LineItems, Currency, TaxRate.
        /// </summary>
        private static bool HasFinancialChanges(UpdateInvoiceRequest updateRequest)
        {
            // If line items are provided, that is a financial change
            if (updateRequest.LineItems != null && updateRequest.LineItems.Count > 0)
                return true;

            // Currency change is financial
            if (!string.IsNullOrWhiteSpace(updateRequest.Currency))
                return true;

            return false;
        }

        /// <summary>
        /// Builds a standard API Gateway response with the specified status code, body, and CORS headers.
        /// All responses use System.Text.Json with snake_case property naming for AOT compatibility.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object body)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(body, JsonOptions),
                Headers = new Dictionary<string, string>(CorsHeaders)
            };
        }

        /// <summary>
        /// Builds a standardized error response following the BaseResponseModel envelope pattern.
        /// Creates a structured error envelope with success=false, error list, timestamp, and message.
        /// Matches the source RecordManager QueryResponse error pattern.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode, string message, List<ErrorModel>? errors = null)
        {
            var errorResponse = new BaseResponseModel
            {
                Success = false,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            if (errors != null)
            {
                foreach (var error in errors)
                {
                    errorResponse.Errors.Add(error);
                }
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(errorResponse, JsonOptions),
                Headers = new Dictionary<string, string>(CorsHeaders)
            };
        }

        /// <summary>
        /// Builds an internal server error response (500).
        /// In development mode (IS_LOCAL=true), includes exception details.
        /// In production, returns a generic error message matching source RecordManager lines 895-900.
        /// NEVER exposes stack traces in production for security compliance.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildInternalErrorResponse(
            string genericMessage, Exception ex)
        {
            var isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true",
                StringComparison.OrdinalIgnoreCase);

            var message = isDevelopmentMode
                ? $"{ex.Message}\n{ex.StackTrace}"
                : genericMessage;

            var errorResponse = new BaseResponseModel
            {
                Success = false,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = 500,
                Body = JsonSerializer.Serialize(errorResponse, JsonOptions),
                Headers = new Dictionary<string, string>(CorsHeaders)
            };
        }
    }
}
