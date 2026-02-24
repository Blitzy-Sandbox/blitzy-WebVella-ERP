// ---------------------------------------------------------------------------
// PaymentHandler.cs — Lambda Handler for Payment Processing Operations
// Bounded Context: Invoicing / Billing (ACID-Critical — RDS PostgreSQL)
// ---------------------------------------------------------------------------
// AWS Lambda entry point for all payment processing HTTP API Gateway v2
// requests. Extracts payment CRUD patterns from the monolith's generic
// RecordManager.CreateRecord/Find and specializes them for financial payment
// operations with full ACID transaction guarantees via RDS PostgreSQL (Npgsql).
//
// SOURCE MAPPING:
//   RecordManager.CreateRecord()   → HandleRecordPayment (lines 254-902)
//   RecordManager.Find()           → HandleGetPayment / HandleListPayments (lines 1736-1802)
//   SecurityContext.HasPermission  → JWT claims from API Gateway authorizer
//   RecordHookManager.PostCreate   → SNS event: invoicing.payment.processed
//   DbContext.Current              → NpgsqlConnection via SSM connection string
//   CurrencyField rounding         → decimal.Round(v, digits, AwayFromZero)
//
// ARCHITECTURE RULES (per AAP):
//   - ACID-critical service: RDS PostgreSQL via Npgsql (NOT DynamoDB)
//   - DB_CONNECTION_STRING from SSM SecureString, NEVER env vars (§0.8.6)
//   - Lambda cold start < 1s (.NET 9 Native AOT) (§0.8.2)
//   - Zero cross-service database access (§0.8.1)
//   - Idempotency keys on write endpoints (§0.8.5)
//   - Structured JSON logging with correlation-ID propagation (§0.8.5)
// ---------------------------------------------------------------------------

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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

// NOTE: LambdaSerializer assembly attribute is defined in InvoiceHandler.cs for this project.

namespace WebVellaErp.Invoicing.Functions
{
    /// <summary>
    /// AWS Lambda handler for payment processing operations in the Invoicing bounded context.
    /// Provides CRUD operations for payments with ACID transaction guarantees via RDS PostgreSQL.
    ///
    /// <para><b>Handler Methods:</b></para>
    /// <list type="bullet">
    ///   <item><description><see cref="HandleRecordPayment"/> — POST /v1/invoicing/payments</description></item>
    ///   <item><description><see cref="HandleGetPayment"/> — GET /v1/invoicing/payments/{paymentId}</description></item>
    ///   <item><description><see cref="HandleListPayments"/> — GET /v1/invoicing/payments?invoiceId=...</description></item>
    ///   <item><description><see cref="HandleHealthCheck"/> — GET /v1/invoicing/payments/health</description></item>
    /// </list>
    ///
    /// <para><b>Source mapping:</b> Replaces <c>RecordManager.CreateRecord()</c> (lines 254-902)
    /// and <c>RecordManager.Find()</c> (lines 1736-1802) specialized for payment entities.</para>
    /// </summary>
    public class PaymentHandler
    {
        // ─────────────────────────── Constants ───────────────────────────

        /// <summary>
        /// SSM Parameter Store path for the RDS PostgreSQL connection string.
        /// Per AAP §0.8.6: DB_CONNECTION_STRING MUST come from SSM SecureString, NEVER environment variables.
        /// </summary>
        private const string SsmConnectionStringParam = "/invoicing/db-connection-string";

        /// <summary>
        /// Default page size for paginated list queries.
        /// </summary>
        private const int DefaultPageSize = 20;

        /// <summary>
        /// Maximum allowed page size to prevent excessive data retrieval.
        /// </summary>
        private const int MaxPageSize = 100;

        /// <summary>
        /// Header name for correlation ID propagation per AAP §0.8.5.
        /// </summary>
        private const string CorrelationIdHeader = "x-correlation-id";

        /// <summary>
        /// Header name for idempotency key per AAP §0.8.5.
        /// </summary>
        private const string IdempotencyKeyHeader = "idempotency-key";

        /// <summary>
        /// AsyncLocal storage for the current correlation ID so that static Build*
        /// helpers can include it in response headers without changing every call site.
        /// Safe across async/await boundaries — value flows with the execution context.
        /// </summary>
        private static readonly AsyncLocal<string?> _currentCorrelationId = new();

        // ─────────────────────────── JSON Options ───────────────────────

        /// <summary>
        /// Shared JSON serializer options for request deserialization.
        /// Case-insensitive property matching with enum string conversion.
        /// </summary>
        private static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        /// <summary>
        /// Shared JSON serializer options for response serialization.
        /// Respects [JsonPropertyName] attributes on model classes for snake_case output.
        /// </summary>
        private static readonly JsonSerializerOptions SerializeOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        // ─────────────────────────── Dependencies ───────────────────────

        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<PaymentHandler> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly bool _isDevelopmentMode;

        // Lazy-initialized after SSM connection string retrieval
        private IInvoiceRepository? _invoiceRepository;
        private IInvoiceService? _invoiceService;
        private IInvoiceEventPublisher? _eventPublisher;
        private string? _cachedConnectionString;
        private volatile bool _initialized;
        private readonly SemaphoreSlim _initLock = new(1, 1);

        // ─────────────────────────── Constructors ───────────────────────

        /// <summary>
        /// Parameterless constructor required by the AWS Lambda runtime.
        /// Builds a DI container with AWS SDK clients and pure services.
        /// Services that depend on the SSM-fetched connection string are lazily initialized
        /// on first handler invocation via <see cref="EnsureInitializedAsync"/>.
        /// </summary>
        public PaymentHandler()
        {
            var services = new ServiceCollection();

            // Structured JSON logging per AAP §0.8.5
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                builder.AddJsonConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                });
            });

            // AWS SDK clients with LocalStack / production dual-target support
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
                // Production: use default AWS credential chain and region via AddAWSService<T>()
                services.AddAWSService<IAmazonSimpleNotificationService>();
                services.AddAWSService<IAmazonSimpleSystemsManagement>();
            }

            // Pure calculation services — no DB dependency, safe to register eagerly
            services.AddSingleton<ITaxCalculationService, TaxCalculationService>();
            services.AddSingleton<ILineItemCalculationService, LineItemCalculationService>();

            _serviceProvider = services.BuildServiceProvider();
            _ssmClient = _serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = _serviceProvider.GetRequiredService<ILogger<PaymentHandler>>();
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Secondary constructor accepting an <see cref="IServiceProvider"/> for unit testing.
        /// Allows injection of mock dependencies without hitting AWS SDK or SSM.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured DI container with all required services.</param>
        public PaymentHandler(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _ssmClient = _serviceProvider.GetRequiredService<IAmazonSimpleSystemsManagement>();
            _snsClient = _serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = _serviceProvider.GetRequiredService<ILogger<PaymentHandler>>();
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);

            // Attempt to resolve pre-registered services for testing scenarios
            _invoiceService = serviceProvider.GetService<IInvoiceService>();
            _invoiceRepository = serviceProvider.GetService<IInvoiceRepository>();
            _eventPublisher = serviceProvider.GetService<IInvoiceEventPublisher>();
            if (_invoiceService != null && _invoiceRepository != null && _eventPublisher != null)
            {
                _initialized = true;
            }
        }

        // ─────────────────────────── Lazy Initialization ────────────────

        /// <summary>
        /// Lazily initializes services that depend on the SSM connection string.
        /// Thread-safe via <see cref="SemaphoreSlim"/> (defensive, even though Lambda
        /// processes one request at a time per container).
        /// </summary>
        private async Task EnsureInitializedAsync(CancellationToken ct)
        {
            if (_initialized) return;

            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_initialized) return;

                _cachedConnectionString = await GetConnectionStringAsync(ct).ConfigureAwait(false);

                _invoiceRepository = new InvoiceRepository(
                    _cachedConnectionString,
                    _serviceProvider.GetRequiredService<ILogger<InvoiceRepository>>());

                _eventPublisher = new InvoiceEventPublisher(
                    _snsClient,
                    _serviceProvider.GetRequiredService<ILogger<InvoiceEventPublisher>>());

                _invoiceService = new InvoiceService(
                    _invoiceRepository,
                    _eventPublisher,
                    _serviceProvider.GetRequiredService<ILineItemCalculationService>(),
                    _serviceProvider.GetRequiredService<ILogger<InvoiceService>>());

                _initialized = true;
                _logger.LogInformation("PaymentHandler lazy initialization complete.");
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Retrieves DB_CONNECTION_STRING from SSM Parameter Store SecureString.
        /// Results are cached for the lifetime of the Lambda container.
        /// Per AAP §0.8.6: secrets MUST come from SSM, NEVER environment variables.
        /// </summary>
        private async Task<string> GetConnectionStringAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_cachedConnectionString))
                return _cachedConnectionString;

            _logger.LogInformation("Fetching DB_CONNECTION_STRING from SSM Parameter Store.");

            var parameterName = Environment.GetEnvironmentVariable("SSM_DB_CONNECTION_STRING_PARAM")
                ?? SsmConnectionStringParam;

            var request = new GetParameterRequest
            {
                Name = parameterName,
                WithDecryption = true
            };

            var response = await _ssmClient.GetParameterAsync(request, ct).ConfigureAwait(false);
            _cachedConnectionString = response.Parameter.Value;

            _logger.LogInformation(
                "DB_CONNECTION_STRING retrieved from SSM ({ParamName}). Length={Length}",
                parameterName, _cachedConnectionString?.Length ?? 0);

            return _cachedConnectionString!;
        }

        // ══════════════════════════════════════════════════════════════════
        //  HandleRecordPayment — POST /v1/invoicing/payments
        //  Replaces: RecordManager.CreateRecord() (source lines 254-902)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Records a new payment against an invoice with full ACID transaction guarantees.
        /// Validates the payment amount against the invoice total, applies currency-aware
        /// rounding, persists the payment, auto-marks the invoice as paid if fully settled,
        /// and publishes an <c>invoicing.payment.processed</c> SNS domain event.
        /// </summary>
        /// <param name="request">HTTP API Gateway v2 proxy request containing payment data in Body.</param>
        /// <param name="context">Lambda execution context providing FunctionName, Logger, RemainingTime.</param>
        /// <returns>201 Created with <see cref="PaymentResponse"/> on success; 400/404/500 on error.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleRecordPayment(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            _currentCorrelationId.Value = correlationId;
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = request.RequestContext?.RequestId ?? context.AwsRequestId
            });

            try
            {
                await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation("Processing payment recording request.");

                // Extract idempotency key per AAP §0.8.5
                var idempotencyKey = ExtractHeader(request, IdempotencyKeyHeader);
                if (!string.IsNullOrEmpty(idempotencyKey))
                {
                    _logger.LogInformation("Idempotency key received: {IdempotencyKey}", idempotencyKey);
                }

                // Extract caller identity from JWT claims (replaces SecurityContext.OpenScope)
                var callerId = ExtractCallerIdFromContext(request);

                // ── Step 1: Deserialize request body ──
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(400, "Request body is required.", new List<ErrorModel>
                    {
                        new ErrorModel("body", string.Empty, "Request body cannot be null or empty.")
                    });
                }

                RecordPaymentRequest? paymentRequest;
                try
                {
                    paymentRequest = JsonSerializer.Deserialize<RecordPaymentRequest>(request.Body, DeserializeOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Invalid JSON in request body for payment recording.");
                    return BuildErrorResponse(400, "Invalid JSON in request body.", new List<ErrorModel>
                    {
                        new ErrorModel("body", string.Empty, "Failed to parse request body: " + ex.Message)
                    });
                }

                if (paymentRequest == null)
                {
                    return BuildErrorResponse(400, "Request body is required.", new List<ErrorModel>
                    {
                        new ErrorModel("body", string.Empty, "Request body deserialized to null.")
                    });
                }

                // ── Step 2: Pre-hook validation (synchronous, in-handler) ──
                // Replaces RecordHookManager.ExecutePreCreateRecordHooks (source lines 303-318)
                var errors = new List<ErrorModel>();

                if (paymentRequest.InvoiceId == Guid.Empty)
                {
                    errors.Add(new ErrorModel("invoice_id", paymentRequest.InvoiceId.ToString(),
                        "InvoiceId is required and must be a valid GUID."));
                }

                if (paymentRequest.Amount <= 0)
                {
                    errors.Add(new ErrorModel("amount", paymentRequest.Amount.ToString("F"),
                        "Amount must be a positive value."));
                }

                if (errors.Count > 0)
                {
                    return BuildErrorResponse(400, "Validation failed.", errors);
                }

                // ── Step 3: Fetch and validate invoice ──
                var invoiceResponse = await _invoiceService!.GetInvoiceAsync(
                    paymentRequest.InvoiceId, CancellationToken.None).ConfigureAwait(false);

                if (!invoiceResponse.Success || invoiceResponse.Object == null)
                {
                    _logger.LogWarning("Invoice {InvoiceId} not found for payment.", paymentRequest.InvoiceId);
                    return BuildErrorResponse(404, "Invoice not found.", new List<ErrorModel>
                    {
                        new ErrorModel("invoice_id", paymentRequest.InvoiceId.ToString(),
                            $"Invoice with ID '{paymentRequest.InvoiceId}' was not found.")
                    });
                }

                var invoice = invoiceResponse.Object;

                // Validate invoice status — replaces SecurityContext.HasEntityPermission
                if (invoice.Status == InvoiceStatus.Voided)
                {
                    _logger.LogWarning("Attempted payment against voided invoice {InvoiceId}.", invoice.Id);
                    return BuildErrorResponse(400, "Cannot record payment against a voided invoice.", new List<ErrorModel>
                    {
                        new ErrorModel("invoice_id", invoice.Id.ToString(),
                            "Cannot record payment against a voided invoice.")
                    });
                }

                if (invoice.Status == InvoiceStatus.Paid)
                {
                    _logger.LogWarning("Attempted payment against already-paid invoice {InvoiceId}.", invoice.Id);
                    return BuildErrorResponse(400, "Invoice is already fully paid.", new List<ErrorModel>
                    {
                        new ErrorModel("invoice_id", invoice.Id.ToString(),
                            "Invoice is already fully paid.")
                    });
                }

                if (invoice.Status == InvoiceStatus.Draft)
                {
                    _logger.LogWarning("Attempted payment against draft invoice {InvoiceId}.", invoice.Id);
                    return BuildErrorResponse(400, "Cannot record payment against a draft invoice. Issue the invoice first.", new List<ErrorModel>
                    {
                        new ErrorModel("invoice_id", invoice.Id.ToString(),
                            "Cannot record payment against a draft invoice. Issue the invoice first.")
                    });
                }

                // ── Step 4: Currency-aware rounding (source RecordManager.cs line 1893) ──
                var decimalDigits = invoice.Currency?.DecimalDigits ?? 2;
                var roundedAmount = decimal.Round(
                    paymentRequest.Amount, decimalDigits, MidpointRounding.AwayFromZero);

                // ── Step 5: Overpayment validation ──
                var existingPayments = await _invoiceRepository!.ListPaymentsForInvoiceAsync(
                    invoice.Id, CancellationToken.None).ConfigureAwait(false);
                var totalPaid = existingPayments.Sum(p => p.Amount);

                if (totalPaid + roundedAmount > invoice.TotalAmount)
                {
                    var maxAdditional = invoice.TotalAmount - totalPaid;
                    _logger.LogWarning(
                        "Payment of {Amount} would exceed invoice {InvoiceId} total {Total}. Already paid: {Paid}.",
                        roundedAmount, invoice.Id, invoice.TotalAmount, totalPaid);
                    return BuildErrorResponse(400, "Payment would exceed invoice total.", new List<ErrorModel>
                    {
                        new ErrorModel("amount", roundedAmount.ToString("F"),
                            $"Payment of {roundedAmount} would exceed invoice total of {invoice.TotalAmount}. " +
                            $"Already paid: {totalPaid}. Maximum additional payment: {maxAdditional}.")
                    });
                }

                // ── Step 6: Create payment record (Guid.NewGuid per source line 322) ──
                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    Amount = roundedAmount,
                    PaymentDate = paymentRequest.PaymentDate ?? DateTime.UtcNow,
                    PaymentMethod = paymentRequest.PaymentMethod,
                    ReferenceNumber = paymentRequest.ReferenceNumber ?? string.Empty,
                    Notes = paymentRequest.Notes ?? string.Empty,
                    CreatedBy = callerId,
                    CreatedOn = DateTime.UtcNow
                };

                // ── Step 7: Persist payment (repo handles ACID transaction internally) ──
                // Replaces DbConnection.BeginTransaction → INSERT → CommitTransaction (source lines 299, 874)
                var persistedPayment = await _invoiceRepository.CreatePaymentAsync(
                    payment, CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "Payment {PaymentId} recorded for invoice {InvoiceId}. Amount={Amount}, Method={Method}.",
                    persistedPayment.Id, persistedPayment.InvoiceId,
                    persistedPayment.Amount, persistedPayment.PaymentMethod);

                // ── Step 8: Auto-mark invoice as paid if fully settled ──
                var newTotalPaid = totalPaid + roundedAmount;
                string newInvoiceStatus = invoice.Status.ToString();
                if (newTotalPaid >= invoice.TotalAmount)
                {
                    _logger.LogInformation("Invoice {InvoiceId} is fully paid ({Paid}/{Total}). Marking as paid.",
                        invoice.Id, newTotalPaid, invoice.TotalAmount);
                    var paidResponse = await _invoiceService.MarkInvoicePaidAsync(
                        invoice.Id, callerId, CancellationToken.None).ConfigureAwait(false);
                    if (paidResponse.Success)
                    {
                        newInvoiceStatus = InvoiceStatus.Paid.ToString();
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Failed to auto-mark invoice {InvoiceId} as paid. Errors: {Errors}",
                            invoice.Id, string.Join("; ", paidResponse.Errors.Select(e => e.Message)));
                    }
                }

                // ── Step 9: Publish domain event (fire-and-forget per source line 870) ──
                // Replaces RecordHookManager.ExecutePostCreateRecordHooks
                await _eventPublisher!.PublishPaymentProcessedAsync(
                    persistedPayment, CancellationToken.None).ConfigureAwait(false);

                // ── Step 10: Build success response ──
                var paymentResponse = new PaymentResponse(persistedPayment)
                {
                    Message = "Payment was recorded successfully."
                };

                return BuildResponse(201, paymentResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing payment recording.");
                var message = _isDevelopmentMode
                    ? $"The payment was not processed. Error: {ex.Message}\n{ex.StackTrace}"
                    : "The payment was not processed. An internal error occurred!";
                return BuildErrorResponse(500, message);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  HandleGetPayment — GET /v1/invoicing/payments/{paymentId}
        //  Replaces: RecordManager.Find() (source lines 1736-1802)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Retrieves a single payment by its unique identifier.
        /// </summary>
        /// <param name="request">HTTP API Gateway v2 proxy request with paymentId in PathParameters.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>200 OK with <see cref="PaymentResponse"/> or 404 Not Found.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetPayment(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            _currentCorrelationId.Value = correlationId;
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = request.RequestContext?.RequestId ?? context.AwsRequestId
            });

            try
            {
                await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

                // Extract paymentId from path parameters
                string? paymentIdStr = null;
                if (request.PathParameters != null)
                {
                    request.PathParameters.TryGetValue("paymentId", out paymentIdStr);
                }

                if (string.IsNullOrWhiteSpace(paymentIdStr) || !Guid.TryParse(paymentIdStr, out var paymentId))
                {
                    return BuildErrorResponse(400, "Invalid or missing paymentId path parameter.", new List<ErrorModel>
                    {
                        new ErrorModel("paymentId", paymentIdStr ?? string.Empty,
                            "paymentId must be a valid GUID.")
                    });
                }

                _logger.LogInformation("Retrieving payment {PaymentId}.", paymentId);

                // Fetch payment — replaces RecordRepository.Find (source line 1788)
                var payment = await _invoiceRepository!.GetPaymentAsync(
                    paymentId, CancellationToken.None).ConfigureAwait(false);

                if (payment == null)
                {
                    _logger.LogWarning("Payment {PaymentId} not found.", paymentId);
                    return BuildErrorResponse(404, "Payment not found.", new List<ErrorModel>
                    {
                        new ErrorModel("paymentId", paymentId.ToString(),
                            $"Payment with ID '{paymentId}' was not found.")
                    });
                }

                var response = new PaymentResponse(payment)
                {
                    Message = "Payment retrieved successfully."
                };
                return BuildResponse(200, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving payment.");
                var message = _isDevelopmentMode
                    ? $"Failed to retrieve payment. Error: {ex.Message}\n{ex.StackTrace}"
                    : "Failed to retrieve payment. An internal error occurred!";
                return BuildErrorResponse(500, message);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  HandleListPayments — GET /v1/invoicing/payments?invoiceId=...
        //  Replaces: RecordManager.Find() with list semantics + pagination
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lists payments for a given invoice with pagination support.
        /// The <c>invoiceId</c> query parameter is required.
        /// </summary>
        /// <param name="request">HTTP API Gateway v2 proxy request with query string parameters.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>200 OK with <see cref="PaymentListResponse"/> containing paginated results.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleListPayments(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            _currentCorrelationId.Value = correlationId;
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName,
                ["RequestId"] = request.RequestContext?.RequestId ?? context.AwsRequestId
            });

            try
            {
                await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);

                // Parse query parameters
                var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

                // invoiceId is required for payment listing
                if (!queryParams.TryGetValue("invoiceId", out var invoiceIdStr) ||
                    !Guid.TryParse(invoiceIdStr, out var invoiceId))
                {
                    return BuildErrorResponse(400, "invoiceId query parameter is required.", new List<ErrorModel>
                    {
                        new ErrorModel("invoiceId", invoiceIdStr ?? string.Empty,
                            "invoiceId must be a valid GUID.")
                    });
                }

                // Parse pagination — defaults per schema
                var page = 1;
                var pageSize = DefaultPageSize;

                if (queryParams.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
                    page = Math.Max(1, parsedPage);

                if (queryParams.TryGetValue("pageSize", out var pageSizeStr) && int.TryParse(pageSizeStr, out var parsedPageSize))
                    pageSize = Math.Clamp(parsedPageSize, 1, MaxPageSize);

                // Parse sort parameters
                var sortBy = queryParams.TryGetValue("sortBy", out var sortByVal) ? sortByVal : "paymentDate";
                var sortOrder = queryParams.TryGetValue("sortOrder", out var sortOrderVal) ? sortOrderVal : "desc";

                _logger.LogInformation(
                    "Listing payments for invoice {InvoiceId}. Page={Page}, PageSize={PageSize}, Sort={SortBy} {SortOrder}.",
                    invoiceId, page, pageSize, sortBy, sortOrder);

                // Fetch all payments for the invoice
                var allPayments = await _invoiceRepository!.ListPaymentsForInvoiceAsync(
                    invoiceId, CancellationToken.None).ConfigureAwait(false);

                // Apply sorting (in-memory, since payment count per invoice is typically small)
                var sortedPayments = ApplySorting(allPayments, sortBy, sortOrder);

                // Apply pagination
                var totalCount = sortedPayments.Count;
                var skip = (page - 1) * pageSize;
                var pagedPayments = sortedPayments.Skip(skip).Take(pageSize).ToList();

                var response = new PaymentListResponse(pagedPayments, totalCount)
                {
                    Message = $"Retrieved {pagedPayments.Count} of {totalCount} payments."
                };

                return BuildResponse(200, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing payments.");
                var message = _isDevelopmentMode
                    ? $"Failed to list payments. Error: {ex.Message}\n{ex.StackTrace}"
                    : "Failed to list payments. An internal error occurred!";
                return BuildErrorResponse(500, message);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  HandleHealthCheck — GET /v1/invoicing/payments/health
        //  Per AAP §0.8.5: "Health check endpoints per service"
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies RDS PostgreSQL and SNS connectivity for the payment subsystem.
        /// Returns a health status JSON object with individual component statuses.
        /// </summary>
        /// <param name="request">HTTP API Gateway v2 proxy request.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>200 OK with health status JSON if all checks pass; 503 if degraded.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleHealthCheck(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            _currentCorrelationId.Value = correlationId;
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["Function"] = context.FunctionName
            });

            _logger.LogInformation("Executing payment handler health check.");

            var dbHealthy = false;
            string? dbError = null;
            var snsHealthy = false;
            string? snsError = null;

            // ── Check RDS PostgreSQL connectivity (SELECT 1) ──
            try
            {
                await EnsureInitializedAsync(CancellationToken.None).ConfigureAwait(false);
                await using var connection = new NpgsqlConnection(_cachedConnectionString);
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand("SELECT 1", connection);
                await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                dbHealthy = true;
            }
            catch (Exception ex)
            {
                dbError = _isDevelopmentMode ? ex.Message : "Database connectivity check failed.";
                _logger.LogError(ex, "Database health check failed.");
            }

            // ── Check SNS connectivity ──
            try
            {
                var topicArn = Environment.GetEnvironmentVariable("PAYMENT_SNS_TOPIC_ARN");
                if (!string.IsNullOrEmpty(topicArn))
                {
                    await _snsClient.GetTopicAttributesAsync(topicArn, CancellationToken.None)
                        .ConfigureAwait(false);
                    snsHealthy = true;
                }
                else
                {
                    // If topic ARN not configured, just verify SNS client can make API calls
                    await _snsClient.ListTopicsAsync(CancellationToken.None).ConfigureAwait(false);
                    snsHealthy = true;
                }
            }
            catch (Exception ex)
            {
                snsError = _isDevelopmentMode ? ex.Message : "SNS connectivity check failed.";
                _logger.LogError(ex, "SNS health check failed.");
            }

            var overallHealthy = dbHealthy && snsHealthy;
            var healthResult = new
            {
                status = overallHealthy ? "healthy" : "degraded",
                timestamp = DateTime.UtcNow.ToString("O"),
                service = "invoicing-payments",
                checks = new
                {
                    database = new { healthy = dbHealthy, error = dbError },
                    sns = new { healthy = snsHealthy, error = snsError }
                },
                remaining_time_ms = context.RemainingTime.TotalMilliseconds
            };

            var statusCode = overallHealthy ? 200 : 503;
            _logger.LogInformation("Health check complete. Status={Status}, DB={DbOk}, SNS={SnsOk}.",
                healthResult.status, dbHealthy, snsHealthy);

            return BuildResponse(statusCode, healthResult);
        }

        // ─────────────────────────── Sorting Helper ──────────────────────

        /// <summary>
        /// Applies in-memory sorting to a list of payments. Payment count per invoice
        /// is typically small (1-10), making in-memory sorting acceptable.
        /// </summary>
        private static List<Payment> ApplySorting(List<Payment> payments, string sortBy, string sortOrder)
        {
            var isDesc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);

            IEnumerable<Payment> sorted = sortBy.ToLowerInvariant() switch
            {
                "amount" => isDesc
                    ? payments.OrderByDescending(p => p.Amount)
                    : payments.OrderBy(p => p.Amount),
                "paymentmethod" or "payment_method" => isDesc
                    ? payments.OrderByDescending(p => p.PaymentMethod)
                    : payments.OrderBy(p => p.PaymentMethod),
                "createdon" or "created_on" => isDesc
                    ? payments.OrderByDescending(p => p.CreatedOn)
                    : payments.OrderBy(p => p.CreatedOn),
                _ => isDesc // Default: sort by PaymentDate
                    ? payments.OrderByDescending(p => p.PaymentDate)
                    : payments.OrderBy(p => p.PaymentDate)
            };

            return sorted.ToList();
        }

        // ─────────────────────────── Helper Methods ──────────────────────

        /// <summary>
        /// Extracts the correlation ID from the <c>x-correlation-id</c> request header
        /// or generates a new GUID if not present, per AAP §0.8.5.
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.Headers != null &&
                request.Headers.TryGetValue(CorrelationIdHeader, out var correlationId) &&
                !string.IsNullOrWhiteSpace(correlationId))
            {
                return correlationId;
            }

            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts a header value from the request headers (case-insensitive).
        /// </summary>
        private static string? ExtractHeader(APIGatewayHttpApiV2ProxyRequest request, string headerName)
        {
            if (request.Headers == null) return null;

            // HTTP API v2 headers are lowercase
            if (request.Headers.TryGetValue(headerName, out var value))
                return value;

            // Fallback: try case-insensitive search
            var match = request.Headers.FirstOrDefault(
                h => string.Equals(h.Key, headerName, StringComparison.OrdinalIgnoreCase));
            return match.Value;
        }

        /// <summary>
        /// Extracts the authenticated user's GUID from the API Gateway JWT authorizer context.
        /// Replaces the monolith's <c>SecurityContext.CurrentUser.Id</c> pattern.
        /// Falls back to the system user GUID if claims are unavailable.
        /// </summary>
        private static Guid ExtractCallerIdFromContext(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                // HTTP API v2 JWT authorizer puts claims in RequestContext.Authorizer.Jwt.Claims
                var authorizer = request.RequestContext?.Authorizer;
                if (authorizer?.Jwt?.Claims != null)
                {
                    // Cognito "sub" claim contains the user pool user ID
                    if (authorizer.Jwt.Claims.TryGetValue("sub", out var sub) &&
                        Guid.TryParse(sub, out var userId))
                    {
                        return userId;
                    }

                    // Fallback: custom claim "user_id" for backward compatibility
                    if (authorizer.Jwt.Claims.TryGetValue("user_id", out var userIdClaim) &&
                        Guid.TryParse(userIdClaim, out var customUserId))
                    {
                        return customUserId;
                    }
                }

                // Lambda authorizer context (LocalStack fallback)
                if (authorizer?.Lambda != null &&
                    authorizer.Lambda.TryGetValue("userId", out var lambdaUserId))
                {
                    if (lambdaUserId is string uidStr && Guid.TryParse(uidStr, out var lambdaGuid))
                        return lambdaGuid;
                }
            }
            catch
            {
                // Defensive: if authorizer parsing fails, fall back to system user
            }

            // Fallback: system administrator user ID from source Definitions.cs SystemIds
            return Guid.Parse("b0223152-ce92-4316-84ea-2a2a42620c41");
        }

        /// <summary>
        /// Builds a standard API Gateway HTTP v2 proxy response with JSON body and CORS headers.
        /// </summary>
        /// <param name="statusCode">HTTP status code.</param>
        /// <param name="body">Response body object to serialize as JSON.</param>
        /// <returns>Formatted <see cref="APIGatewayHttpApiV2ProxyResponse"/>.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object body)
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Access-Control-Allow-Origin"] = "*",
                ["Access-Control-Allow-Headers"] =
                    "Content-Type,X-Amz-Date,Authorization,X-Api-Key,x-correlation-id,Idempotency-Key",
                ["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS"
            };

            var cid = _currentCorrelationId.Value;
            if (!string.IsNullOrEmpty(cid))
            {
                headers[CorrelationIdHeader] = cid;
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(body, SerializeOptions),
                Headers = headers
            };
        }

        /// <summary>
        /// Builds a standardized error response matching the source <c>QueryResponse</c>
        /// envelope pattern (RecordManager.cs lines 274-280).
        /// </summary>
        /// <param name="statusCode">HTTP status code (400, 404, 500, etc.).</param>
        /// <param name="message">Human-readable error message.</param>
        /// <param name="errors">Optional list of field-level validation errors.</param>
        /// <returns>Formatted error <see cref="APIGatewayHttpApiV2ProxyResponse"/>.</returns>
        private static APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode, string message, List<ErrorModel>? errors = null)
        {
            var response = new BaseResponseModel
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                StatusCode = (HttpStatusCode)statusCode
            };

            if (errors != null)
            {
                response.Errors = errors;
            }

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Access-Control-Allow-Origin"] = "*",
                ["Access-Control-Allow-Headers"] =
                    "Content-Type,X-Amz-Date,Authorization,X-Api-Key,x-correlation-id,Idempotency-Key",
                ["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS"
            };

            var cid = _currentCorrelationId.Value;
            if (!string.IsNullOrEmpty(cid))
            {
                headers[CorrelationIdHeader] = cid;
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = JsonSerializer.Serialize(response, SerializeOptions),
                Headers = headers
            };
        }

        // ─────────────────────────── Request DTO ─────────────────────────

        /// <summary>
        /// Incoming request DTO for recording a payment against an invoice.
        /// Uses snake_case JSON property names matching the invoicing API convention.
        /// </summary>
        public record RecordPaymentRequest
        {
            /// <summary>Target invoice ID.</summary>
            [JsonPropertyName("invoice_id")]
            public Guid InvoiceId { get; init; }

            /// <summary>Payment amount (positive, validated with currency precision).</summary>
            [JsonPropertyName("amount")]
            public decimal Amount { get; init; }

            /// <summary>Date when payment was received. Defaults to UtcNow if omitted.</summary>
            [JsonPropertyName("payment_date")]
            public DateTime? PaymentDate { get; init; }

            /// <summary>Payment method (BankTransfer, CreditCard, DebitCard, Cash, Check, Other).</summary>
            [JsonPropertyName("payment_method")]
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.BankTransfer;

            /// <summary>Optional external payment reference (check number, wire transfer ID, etc.).</summary>
            [JsonPropertyName("reference_number")]
            public string? ReferenceNumber { get; init; }

            /// <summary>Optional free-text notes about the payment.</summary>
            [JsonPropertyName("notes")]
            public string? Notes { get; init; }
        }
    }
}
