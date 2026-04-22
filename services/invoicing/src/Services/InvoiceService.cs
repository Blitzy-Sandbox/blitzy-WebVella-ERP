using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WebVellaErp.Invoicing.DataAccess;
using WebVellaErp.Invoicing.Models;

namespace WebVellaErp.Invoicing.Services
{
    /// <summary>
    /// Defines the contract for invoice lifecycle management operations.
    /// Covers complete CRUD operations plus state machine transitions:
    /// Draft → Issued, Draft → Voided, Issued → Paid, Issued → Voided.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>Replaces <c>RecordManager.CreateRecord()</c> / <c>UpdateRecord()</c>
    ///     from <c>WebVella.Erp/Api/RecordManager.cs</c> with strongly-typed invoice operations.</description></item>
    ///   <item><description>Replaces <c>RecordHookManager</c> post-CRUD hooks with SNS domain events
    ///     per AAP §0.7.2.</description></item>
    /// </list>
    /// </summary>
    public interface IInvoiceService
    {
        /// <summary>
        /// Creates a new draft invoice with line items and computed totals.
        /// Validates input, builds the invoice model, calculates line item and invoice totals,
        /// persists via ACID transaction, and publishes <c>invoicing.invoice.created</c> event.
        /// </summary>
        /// <param name="request">Invoice creation request with customer, dates, and line items.</param>
        /// <param name="userId">ID of the user creating the invoice (from JWT claims).</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the created invoice or validation errors.</returns>
        Task<InvoiceResponse> CreateInvoiceAsync(CreateInvoiceRequest request, Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Updates an existing draft invoice with partial field updates and optional line item replacement.
        /// Only invoices in Draft status can be updated.
        /// Publishes <c>invoicing.invoice.updated</c> event on success.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to update.</param>
        /// <param name="request">Partial update request with nullable fields.</param>
        /// <param name="userId">ID of the user performing the update (from JWT claims).</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the updated invoice or validation errors.</returns>
        Task<InvoiceResponse> UpdateInvoiceAsync(Guid invoiceId, UpdateInvoiceRequest request, Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a single invoice by ID including all line items.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to retrieve.</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the invoice or a not-found error.</returns>
        Task<InvoiceResponse> GetInvoiceAsync(Guid invoiceId, CancellationToken ct = default);

        /// <summary>
        /// Lists invoices with pagination and optional status/customer filters.
        /// </summary>
        /// <param name="page">Page number (1-based).</param>
        /// <param name="pageSize">Number of items per page (1-1000).</param>
        /// <param name="statusFilter">Optional filter by invoice status.</param>
        /// <param name="customerFilter">Optional filter by customer ID.</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the list of invoices and total count.</returns>
        Task<InvoiceListResponse> ListInvoicesAsync(int page, int pageSize, InvoiceStatus? statusFilter = null, Guid? customerFilter = null, CancellationToken ct = default);

        /// <summary>
        /// Transitions an invoice from Draft to Issued status.
        /// Sets the issue date and publishes <c>invoicing.invoice.issued</c> domain event.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to issue.</param>
        /// <param name="userId">ID of the user issuing the invoice (from JWT claims).</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the issued invoice or state transition error.</returns>
        Task<InvoiceResponse> IssueInvoiceAsync(Guid invoiceId, Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Transitions an invoice to Voided status from Draft or Issued.
        /// Cannot void paid invoices. Publishes <c>invoicing.invoice.voided</c> domain event.
        /// Uses an idempotent WHERE clause via the repository per AAP §0.8.5.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to void.</param>
        /// <param name="userId">ID of the user voiding the invoice (from JWT claims).</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the voided invoice or state transition error.</returns>
        Task<InvoiceResponse> VoidInvoiceAsync(Guid invoiceId, Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Marks an invoice as fully paid. Called internally by PaymentService after payment processing.
        /// Does NOT publish a domain event — the PaymentService publishes the payment event to avoid duplicates.
        /// Only Issued invoices can be marked as paid.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to mark as paid.</param>
        /// <param name="userId">ID of the user recording the payment (from JWT claims).</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the paid invoice or state transition error.</returns>
        Task<InvoiceResponse> MarkInvoicePaidAsync(Guid invoiceId, Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Generates a unique invoice number following the pattern "INV-{YYYY}-{NNNNNN}".
        /// Replaces the monolith's AutoNumberField handling from RecordManager.cs lines 1867-1875.
        /// Uses a thread-safe counter within the Lambda instance; production environments
        /// should supplement with a PostgreSQL sequence via the repository for global uniqueness.
        /// </summary>
        /// <returns>A formatted invoice number string.</returns>
        string GenerateInvoiceNumber();
    }

    /// <summary>
    /// Invoice lifecycle management service implementing <see cref="IInvoiceService"/>.
    /// Manages CRUD operations and state machine transitions for invoices backed by RDS PostgreSQL
    /// with ACID transaction guarantees per AAP §0.4.2 (Database-Per-Service pattern).
    ///
    /// <para>All operations follow the validate → persist → publish-event pattern extracted from
    /// the monolith's <c>RecordManager.CreateRecord()</c> / <c>UpdateRecord()</c> orchestration
    /// (source <c>WebVella.Erp/Api/RecordManager.cs</c> lines 254-1577).</para>
    ///
    /// <para><b>State machine:</b></para>
    /// <list type="bullet">
    ///   <item><description>Draft → Issued (via <see cref="IssueInvoiceAsync"/>)</description></item>
    ///   <item><description>Draft → Voided (via <see cref="VoidInvoiceAsync"/>)</description></item>
    ///   <item><description>Issued → Paid (via <see cref="MarkInvoicePaidAsync"/>)</description></item>
    ///   <item><description>Issued → Voided (via <see cref="VoidInvoiceAsync"/>)</description></item>
    /// </list>
    ///
    /// <para><b>Architecture:</b></para>
    /// <list type="bullet">
    ///   <item><description>NO DbContext, NO SecurityContext, NO RecordHookManager, NO EntityManager.</description></item>
    ///   <item><description>User identity passed as <c>userId</c> parameter (extracted from JWT by Lambda handler).</description></item>
    ///   <item><description>All monetary calculations use <c>decimal</c> with <c>MidpointRounding.AwayFromZero</c>.</description></item>
    ///   <item><description>Structured JSON logging with correlation-ID per AAP §0.8.5.</description></item>
    /// </list>
    /// </summary>
    public class InvoiceService : IInvoiceService
    {
        private readonly IInvoiceRepository _repository;
        private readonly IInvoiceEventPublisher _eventPublisher;
        private readonly ILineItemCalculationService _calculationService;
        private readonly ILogger<InvoiceService> _logger;

        /// <summary>
        /// Thread-safe counter for invoice number generation within a Lambda instance lifecycle.
        /// Initialized from current timestamp ticks modulo 1,000,000 for uniqueness across cold starts.
        /// Production deployments should supplement with a PostgreSQL sequence via the repository
        /// for globally unique sequential numbers.
        /// </summary>
        private static long _invoiceCounter = DateTime.UtcNow.Ticks % 1000000;

        /// <summary>
        /// Static dictionary of common ISO 4217 currency configurations.
        /// Maps currency codes to <see cref="CurrencyInfo"/> objects with correct decimal digits.
        /// Preserves the <c>CurrencyType</c> pattern from source <c>Definitions.cs</c> (lines 64-90).
        /// </summary>
        private static readonly Dictionary<string, CurrencyInfo> CurrencyLookup =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["USD"] = new CurrencyInfo { Code = "USD", Symbol = "$", SymbolNative = "$", Name = "US Dollar", DecimalDigits = 2 },
                ["EUR"] = new CurrencyInfo { Code = "EUR", Symbol = "€", SymbolNative = "€", Name = "Euro", DecimalDigits = 2 },
                ["GBP"] = new CurrencyInfo { Code = "GBP", Symbol = "£", SymbolNative = "£", Name = "British Pound", DecimalDigits = 2 },
                ["JPY"] = new CurrencyInfo { Code = "JPY", Symbol = "¥", SymbolNative = "￥", Name = "Japanese Yen", DecimalDigits = 0 },
                ["CHF"] = new CurrencyInfo { Code = "CHF", Symbol = "CHF", SymbolNative = "CHF", Name = "Swiss Franc", DecimalDigits = 2 },
                ["CAD"] = new CurrencyInfo { Code = "CAD", Symbol = "CA$", SymbolNative = "$", Name = "Canadian Dollar", DecimalDigits = 2 },
                ["AUD"] = new CurrencyInfo { Code = "AUD", Symbol = "AU$", SymbolNative = "$", Name = "Australian Dollar", DecimalDigits = 2 },
                ["BGN"] = new CurrencyInfo { Code = "BGN", Symbol = "лв", SymbolNative = "лв.", Name = "Bulgarian Lev", DecimalDigits = 2 },
                ["CNY"] = new CurrencyInfo { Code = "CNY", Symbol = "CN¥", SymbolNative = "¥", Name = "Chinese Yuan", DecimalDigits = 2 },
                ["INR"] = new CurrencyInfo { Code = "INR", Symbol = "₹", SymbolNative = "₹", Name = "Indian Rupee", DecimalDigits = 2 },
            };

        /// <summary>
        /// Constructs the InvoiceService with required DI dependencies.
        /// Replaces the monolith's <c>RecordManager(DbContext, ignoreSecurity, executeHooks)</c>
        /// constructor pattern (source RecordManager.cs line 40) with clean DI injection.
        /// </summary>
        /// <param name="repository">RDS PostgreSQL data access for invoice persistence.</param>
        /// <param name="eventPublisher">SNS domain event publisher replacing post-CRUD hooks.</param>
        /// <param name="calculationService">Line item and invoice total computation service.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID propagation.</param>
        public InvoiceService(
            IInvoiceRepository repository,
            IInvoiceEventPublisher eventPublisher,
            ILineItemCalculationService calculationService,
            ILogger<InvoiceService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
            _calculationService = calculationService ?? throw new ArgumentNullException(nameof(calculationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<InvoiceResponse> CreateInvoiceAsync(
            CreateInvoiceRequest request, Guid userId, CancellationToken ct = default)
        {
            var response = new InvoiceResponse();

            try
            {
                // Step 1: Validation — replaces RecordManager.cs lines 268-293 null/permission checks
                if (request == null)
                {
                    response.Errors.Add(new ErrorModel("request", string.Empty,
                        "Invalid invoice request. Cannot be null."));
                    response.Success = false;
                    _logger.LogWarning("CreateInvoiceAsync failed: request is null for user {UserId}", userId);
                    return response;
                }

                ValidateCreateRequest(request, userId, response);

                if (response.Errors.Count > 0)
                {
                    response.Success = false;
                    _logger.LogWarning(
                        "CreateInvoiceAsync validation failed with {ErrorCount} errors for user {UserId}",
                        response.Errors.Count, userId);
                    return response;
                }

                // Step 2: Build Invoice Model — replaces RecordManager.cs line 322 Guid.NewGuid()
                var now = DateTime.UtcNow;
                var invoice = new Invoice
                {
                    Id = Guid.NewGuid(),
                    InvoiceNumber = GenerateInvoiceNumber(),
                    CustomerId = request.CustomerId!.Value,
                    Status = InvoiceStatus.Draft,
                    IssueDate = request.IssueDate,
                    DueDate = request.DueDate,
                    Currency = ResolveCurrencyInfo(request.Currency),
                    Notes = request.Notes ?? string.Empty,
                    CreatedBy = userId,
                    CreatedOn = now,
                    LastModifiedBy = userId,
                    LastModifiedOn = now
                };

                // Step 3: Build LineItems and Calculate Totals
                // Uses currency DecimalDigits for rounding per source RecordManager.cs line 1893
                var decimalDigits = invoice.Currency?.DecimalDigits ?? 2;
                invoice.LineItems = BuildLineItemsFromCreateRequests(
                    request.LineItems, invoice.Id, decimalDigits);

                // Calculate invoice-level totals (SubTotal, TaxAmount, TotalAmount)
                _calculationService.CalculateInvoiceTotals(invoice);

                // Step 4: Persist with ACID transaction via repository
                var persisted = await _repository.CreateInvoiceAsync(invoice, ct);

                // Step 5: Publish domain event — replaces RecordHookManager.ExecutePostCreateRecordHooks()
                // Event: invoicing.invoice.created per AAP §0.8.5
                await _eventPublisher.PublishInvoiceCreatedAsync(persisted, ct);

                // Step 6: Build success response
                response.Object = persisted;
                response.Message = "Invoice created successfully.";

                _logger.LogInformation(
                    "Invoice {InvoiceId} ({InvoiceNumber}) created with {LineItemCount} line items, " +
                    "total {TotalAmount} by user {UserId}",
                    persisted.Id, persisted.InvoiceNumber, persisted.LineItems.Count,
                    persisted.TotalAmount, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice for user {UserId}", userId);
                response.Success = false;
                response.Message = $"An error occurred while creating the invoice: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<InvoiceResponse> UpdateInvoiceAsync(
            Guid invoiceId, UpdateInvoiceRequest request, Guid userId, CancellationToken ct = default)
        {
            var response = new InvoiceResponse();

            try
            {
                // Validate inputs
                if (request == null)
                {
                    response.Errors.Add(new ErrorModel("request", string.Empty,
                        "Invalid update request. Cannot be null."));
                    response.Success = false;
                    return response;
                }

                if (invoiceId == Guid.Empty)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty,
                        "Invoice ID is required."));
                    response.Success = false;
                    return response;
                }

                // Fetch existing invoice — replaces RecordManager.cs lines 1029-1036
                var invoice = await _repository.GetInvoiceByIdAsync(invoiceId, ct);
                if (invoice == null)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(),
                        "Invoice not found."));
                    response.Success = false;
                    _logger.LogWarning("UpdateInvoiceAsync: Invoice {InvoiceId} not found", invoiceId);
                    return response;
                }

                // Validate state: only Draft invoices can be edited
                if (invoice.Status != InvoiceStatus.Draft)
                {
                    response.Errors.Add(new ErrorModel("status", invoice.Status.ToString(),
                        "Only draft invoices can be updated."));
                    response.Success = false;
                    _logger.LogWarning(
                        "UpdateInvoiceAsync: Cannot update invoice {InvoiceId} in {Status} status",
                        invoiceId, invoice.Status);
                    return response;
                }

                // Apply partial updates — nullable fields pattern from source InputEntity
                var now = DateTime.UtcNow;
                bool lineItemsChanged = false;

                if (request.CustomerId.HasValue)
                {
                    if (request.CustomerId.Value == Guid.Empty)
                    {
                        response.Errors.Add(new ErrorModel("customerId", string.Empty,
                            "Customer ID cannot be empty."));
                        response.Success = false;
                        return response;
                    }
                    invoice.CustomerId = request.CustomerId.Value;
                }

                if (request.IssueDate.HasValue)
                {
                    invoice.IssueDate = request.IssueDate.Value;
                }

                if (request.DueDate.HasValue)
                {
                    invoice.DueDate = request.DueDate.Value;
                }

                // Validate date relationship after applying updates
                if (invoice.DueDate < invoice.IssueDate)
                {
                    response.Errors.Add(new ErrorModel("dueDate", invoice.DueDate.ToString("o"),
                        "Due date must be on or after issue date."));
                    response.Success = false;
                    return response;
                }

                if (request.Notes != null)
                {
                    invoice.Notes = request.Notes;
                }

                // Handle line item replacement — when provided, replaces ALL existing line items
                if (request.LineItems != null)
                {
                    if (!request.LineItems.Any())
                    {
                        response.Errors.Add(new ErrorModel("lineItems", string.Empty,
                            "At least one line item is required."));
                        response.Success = false;
                        return response;
                    }

                    // Validate each update line item
                    ValidateUpdateLineItems(request.LineItems, response);
                    if (response.Errors.Count > 0)
                    {
                        response.Success = false;
                        return response;
                    }

                    var decimalDigits = invoice.Currency?.DecimalDigits ?? 2;
                    invoice.LineItems = BuildLineItemsFromUpdateRequests(
                        request.LineItems, invoice.Id, decimalDigits);
                    lineItemsChanged = true;
                }

                // Recalculate invoice totals if line items were replaced
                if (lineItemsChanged)
                {
                    _calculationService.CalculateInvoiceTotals(invoice);
                }

                // Set audit fields
                invoice.LastModifiedBy = userId;
                invoice.LastModifiedOn = now;

                // Persist updated invoice
                var updated = await _repository.UpdateInvoiceAsync(invoice, ct);

                // Publish domain event — replaces RecordHookManager.ExecutePostUpdateRecordHooks()
                // Event: invoicing.invoice.updated per AAP §0.8.5
                await _eventPublisher.PublishInvoiceUpdatedAsync(updated, ct);

                response.Object = updated;
                response.Message = "Invoice updated successfully.";

                _logger.LogInformation(
                    "Invoice {InvoiceId} updated by user {UserId}", invoiceId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating invoice {InvoiceId} for user {UserId}",
                    invoiceId, userId);
                response.Success = false;
                response.Message = $"An error occurred while updating the invoice: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<InvoiceResponse> GetInvoiceAsync(
            Guid invoiceId, CancellationToken ct = default)
        {
            var response = new InvoiceResponse();

            try
            {
                if (invoiceId == Guid.Empty)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty,
                        "Invoice ID is required."));
                    response.Success = false;
                    return response;
                }

                var invoice = await _repository.GetInvoiceByIdAsync(invoiceId, ct);
                if (invoice == null)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(),
                        "Invoice not found."));
                    response.Success = false;
                    return response;
                }

                response.Object = invoice;

                _logger.LogInformation("Invoice {InvoiceId} retrieved successfully", invoiceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving invoice {InvoiceId}", invoiceId);
                response.Success = false;
                response.Message = $"An error occurred while retrieving the invoice: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<InvoiceListResponse> ListInvoicesAsync(
            int page, int pageSize, InvoiceStatus? statusFilter = null,
            Guid? customerFilter = null, CancellationToken ct = default)
        {
            var response = new InvoiceListResponse();

            try
            {
                // Validate pagination parameters
                if (page < 1)
                {
                    response.Errors.Add(new ErrorModel("page", page.ToString(),
                        "Page number must be 1 or greater."));
                    response.Success = false;
                    return response;
                }

                if (pageSize < 1 || pageSize > 1000)
                {
                    response.Errors.Add(new ErrorModel("pageSize", pageSize.ToString(),
                        "Page size must be between 1 and 1000."));
                    response.Success = false;
                    return response;
                }

                var (items, totalCount) = await _repository.ListInvoicesAsync(
                    page, pageSize, statusFilter, customerFilter, ct);

                response.Object = items;
                response.TotalCount = totalCount;

                _logger.LogInformation(
                    "Listed {ItemCount} invoices (page {Page}, pageSize {PageSize}, total {TotalCount})",
                    items.Count, page, pageSize, totalCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing invoices (page {Page}, pageSize {PageSize})",
                    page, pageSize);
                response.Success = false;
                response.Message = $"An error occurred while listing invoices: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<InvoiceResponse> IssueInvoiceAsync(
            Guid invoiceId, Guid userId, CancellationToken ct = default)
        {
            var response = new InvoiceResponse();

            try
            {
                if (invoiceId == Guid.Empty)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty,
                        "Invoice ID is required."));
                    response.Success = false;
                    return response;
                }

                var invoice = await _repository.GetInvoiceByIdAsync(invoiceId, ct);
                if (invoice == null)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(),
                        "Invoice not found."));
                    response.Success = false;
                    return response;
                }

                // Validate state transition: only Draft → Issued is valid
                if (invoice.Status != InvoiceStatus.Draft)
                {
                    response.Errors.Add(new ErrorModel("status", invoice.Status.ToString(),
                        "Only draft invoices can be issued."));
                    response.Success = false;
                    _logger.LogWarning(
                        "IssueInvoiceAsync: Cannot issue invoice {InvoiceId} in {Status} status",
                        invoiceId, invoice.Status);
                    return response;
                }

                // Validate the invoice has line items before issuing
                if (invoice.LineItems == null || !invoice.LineItems.Any())
                {
                    response.Errors.Add(new ErrorModel("lineItems", string.Empty,
                        "Cannot issue an invoice with no line items."));
                    response.Success = false;
                    return response;
                }

                // Apply state transition
                var now = DateTime.UtcNow;
                invoice.Status = InvoiceStatus.Issued;
                invoice.IssueDate = now;
                invoice.LastModifiedBy = userId;
                invoice.LastModifiedOn = now;

                // Persist via repository
                var updated = await _repository.UpdateInvoiceAsync(invoice, ct);

                // Publish domain event: invoicing.invoice.issued per AAP §0.8.5
                await _eventPublisher.PublishInvoiceIssuedAsync(updated, ct);

                response.Object = updated;
                response.Message = "Invoice issued successfully.";

                _logger.LogInformation(
                    "Invoice {InvoiceId} ({InvoiceNumber}) issued by user {UserId}",
                    invoiceId, invoice.InvoiceNumber, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error issuing invoice {InvoiceId} for user {UserId}",
                    invoiceId, userId);
                response.Success = false;
                response.Message = $"An error occurred while issuing the invoice: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<InvoiceResponse> VoidInvoiceAsync(
            Guid invoiceId, Guid userId, CancellationToken ct = default)
        {
            var response = new InvoiceResponse();

            try
            {
                if (invoiceId == Guid.Empty)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty,
                        "Invoice ID is required."));
                    response.Success = false;
                    return response;
                }

                var invoice = await _repository.GetInvoiceByIdAsync(invoiceId, ct);
                if (invoice == null)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(),
                        "Invoice not found."));
                    response.Success = false;
                    return response;
                }

                // Validate state: cannot void an already-voided invoice (idempotency per AAP §0.8.5)
                if (invoice.Status == InvoiceStatus.Voided)
                {
                    response.Errors.Add(new ErrorModel("status", invoice.Status.ToString(),
                        "Invoice is already voided."));
                    response.Success = false;
                    return response;
                }

                // Validate state: cannot void a paid invoice
                if (invoice.Status == InvoiceStatus.Paid)
                {
                    response.Errors.Add(new ErrorModel("status", invoice.Status.ToString(),
                        "Cannot void a paid invoice."));
                    response.Success = false;
                    return response;
                }

                // Apply void via repository — uses idempotent WHERE clause for concurrent safety
                var voided = await _repository.VoidInvoiceAsync(invoiceId, userId, ct);
                if (!voided)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(),
                        "Failed to void invoice. It may have been modified concurrently."));
                    response.Success = false;
                    return response;
                }

                // Update local invoice state for event publishing
                invoice.Status = InvoiceStatus.Voided;
                invoice.LastModifiedBy = userId;
                invoice.LastModifiedOn = DateTime.UtcNow;

                // Publish domain event: invoicing.invoice.voided per AAP §0.8.5
                await _eventPublisher.PublishInvoiceVoidedAsync(invoice, ct);

                response.Object = invoice;
                response.Message = "Invoice voided successfully.";

                _logger.LogInformation(
                    "Invoice {InvoiceId} ({InvoiceNumber}) voided by user {UserId}",
                    invoiceId, invoice.InvoiceNumber, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error voiding invoice {InvoiceId} for user {UserId}",
                    invoiceId, userId);
                response.Success = false;
                response.Message = $"An error occurred while voiding the invoice: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public async Task<InvoiceResponse> MarkInvoicePaidAsync(
            Guid invoiceId, Guid userId, CancellationToken ct = default)
        {
            var response = new InvoiceResponse();

            try
            {
                if (invoiceId == Guid.Empty)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty,
                        "Invoice ID is required."));
                    response.Success = false;
                    return response;
                }

                var invoice = await _repository.GetInvoiceByIdAsync(invoiceId, ct);
                if (invoice == null)
                {
                    response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(),
                        "Invoice not found."));
                    response.Success = false;
                    return response;
                }

                // Validate state: only Issued invoices can be marked as paid
                if (invoice.Status != InvoiceStatus.Issued)
                {
                    response.Errors.Add(new ErrorModel("status", invoice.Status.ToString(),
                        $"Only issued invoices can be marked as paid. Current status: {invoice.Status}."));
                    response.Success = false;
                    _logger.LogWarning(
                        "MarkInvoicePaidAsync: Cannot mark invoice {InvoiceId} as paid, " +
                        "current status {Status}",
                        invoiceId, invoice.Status);
                    return response;
                }

                // Apply state transition
                var now = DateTime.UtcNow;
                invoice.Status = InvoiceStatus.Paid;
                invoice.LastModifiedBy = userId;
                invoice.LastModifiedOn = now;

                // Persist via repository
                var updated = await _repository.UpdateInvoiceAsync(invoice, ct);

                // NOTE: The payment event is published by PaymentService, NOT here,
                // to avoid duplicate domain events per AAP §0.7.2 guidance.
                response.Object = updated;
                response.Message = "Invoice marked as paid successfully.";

                _logger.LogInformation(
                    "Invoice {InvoiceId} ({InvoiceNumber}) marked as paid by user {UserId}",
                    invoiceId, invoice.InvoiceNumber, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking invoice {InvoiceId} as paid for user {UserId}",
                    invoiceId, userId);
                response.Success = false;
                response.Message = $"An error occurred while marking the invoice as paid: {ex.Message}";
                response.Errors.Add(new ErrorModel("exception", string.Empty, ex.Message));
            }

            return response;
        }

        /// <inheritdoc />
        public string GenerateInvoiceNumber()
        {
            // Thread-safe counter increment — replaces AutoNumberField handling
            // from source RecordManager.cs lines 1867-1875: (int)decimal.Parse() pattern.
            // In production, the repository layer should use a PostgreSQL sequence for
            // globally unique sequential numbers across all Lambda instances.
            var sequence = Interlocked.Increment(ref _invoiceCounter);
            return $"INV-{DateTime.UtcNow:yyyy}-{sequence:D6}";
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Private Helper Methods
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates a <see cref="CreateInvoiceRequest"/>, adding errors to the response.
        /// Follows the validation pattern from <c>RecordManager.cs</c> lines 268-293:
        /// validate each field → add <see cref="ErrorModel"/> to response → check error count.
        /// </summary>
        private void ValidateCreateRequest(
            CreateInvoiceRequest request, Guid userId, InvoiceResponse response)
        {
            if (!request.CustomerId.HasValue || request.CustomerId.Value == Guid.Empty)
            {
                response.Errors.Add(new ErrorModel("customerId", string.Empty,
                    "Customer ID is required."));
            }

            if (request.DueDate < request.IssueDate)
            {
                response.Errors.Add(new ErrorModel("dueDate", request.DueDate.ToString("o"),
                    "Due date must be on or after issue date."));
            }

            if (request.LineItems == null || !request.LineItems.Any())
            {
                response.Errors.Add(new ErrorModel("lineItems", string.Empty,
                    "At least one line item is required."));
            }
            else
            {
                ValidateCreateLineItems(request.LineItems, response);
            }

            if (userId == Guid.Empty)
            {
                response.Errors.Add(new ErrorModel("userId", string.Empty,
                    "User ID is required."));
            }
        }

        /// <summary>
        /// Validates individual line items within a create request.
        /// Checks: quantity &gt; 0, unit price &gt;= 0, tax rate between 0 and 1,
        /// and non-empty description for each line item.
        /// </summary>
        private static void ValidateCreateLineItems(
            List<CreateLineItemRequest> lineItems, InvoiceResponse response)
        {
            for (int i = 0; i < lineItems.Count; i++)
            {
                var li = lineItems[i];

                if (li.Quantity <= 0)
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].quantity", li.Quantity.ToString(),
                        "Quantity must be greater than zero."));
                }

                if (li.UnitPrice < 0)
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].unitPrice", li.UnitPrice.ToString(),
                        "Unit price must be zero or greater."));
                }

                if (li.TaxRate < 0 || li.TaxRate > 1)
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].taxRate", li.TaxRate.ToString(),
                        "Tax rate must be between 0 and 1 (decimal fraction)."));
                }

                if (string.IsNullOrWhiteSpace(li.Description))
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].description", string.Empty,
                        "Line item description is required."));
                }
            }
        }

        /// <summary>
        /// Validates individual line items within an update request.
        /// Uses nullable field checks since <see cref="UpdateLineItemRequest"/>
        /// supports partial updates.
        /// </summary>
        private static void ValidateUpdateLineItems(
            List<UpdateLineItemRequest> lineItems, InvoiceResponse response)
        {
            for (int i = 0; i < lineItems.Count; i++)
            {
                var li = lineItems[i];

                if (li.Quantity.HasValue && li.Quantity.Value <= 0)
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].quantity", li.Quantity.Value.ToString(),
                        "Quantity must be greater than zero."));
                }

                if (li.UnitPrice.HasValue && li.UnitPrice.Value < 0)
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].unitPrice", li.UnitPrice.Value.ToString(),
                        "Unit price must be zero or greater."));
                }

                if (li.TaxRate.HasValue && (li.TaxRate.Value < 0 || li.TaxRate.Value > 1))
                {
                    response.Errors.Add(new ErrorModel(
                        $"lineItems[{i}].taxRate", li.TaxRate.Value.ToString(),
                        "Tax rate must be between 0 and 1 (decimal fraction)."));
                }
            }
        }

        /// <summary>
        /// Builds <see cref="LineItem"/> models from <see cref="CreateLineItemRequest"/> items.
        /// Assigns new GUIDs, copies properties, and calculates per-line totals via
        /// <see cref="ILineItemCalculationService.CalculateLineItemTotals"/>.
        /// </summary>
        private List<LineItem> BuildLineItemsFromCreateRequests(
            List<CreateLineItemRequest> requests, Guid invoiceId, int decimalDigits)
        {
            var lineItems = new List<LineItem>();
            int sortIndex = 0;

            foreach (var req in requests)
            {
                var lineItem = new LineItem
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Description = req.Description,
                    Quantity = req.Quantity,
                    UnitPrice = req.UnitPrice,
                    TaxRate = req.TaxRate,
                    SortOrder = req.SortOrder ?? sortIndex
                };

                // Calculate line total via ILineItemCalculationService
                // Preserves currency rounding pattern from RecordManager.cs line 1893:
                // decimal.Round(value, DecimalDigits, MidpointRounding.AwayFromZero)
                _calculationService.CalculateLineItemTotals(lineItem, decimalDigits);
                lineItems.Add(lineItem);
                sortIndex++;
            }

            return lineItems;
        }

        /// <summary>
        /// Builds <see cref="LineItem"/> models from <see cref="UpdateLineItemRequest"/> items.
        /// Supports upsert: uses existing ID if provided, otherwise generates a new GUID.
        /// Replaces all existing line items (full replacement on update per agent spec).
        /// </summary>
        private List<LineItem> BuildLineItemsFromUpdateRequests(
            List<UpdateLineItemRequest> requests, Guid invoiceId, int decimalDigits)
        {
            var lineItems = new List<LineItem>();
            int sortIndex = 0;

            foreach (var req in requests)
            {
                var lineItem = new LineItem
                {
                    Id = req.Id ?? Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    Description = req.Description ?? string.Empty,
                    Quantity = req.Quantity ?? 0m,
                    UnitPrice = req.UnitPrice ?? 0m,
                    TaxRate = req.TaxRate ?? 0m,
                    SortOrder = req.SortOrder ?? sortIndex
                };

                _calculationService.CalculateLineItemTotals(lineItem, decimalDigits);
                lineItems.Add(lineItem);
                sortIndex++;
            }

            return lineItems;
        }

        /// <summary>
        /// Resolves an ISO 4217 currency code string to a full <see cref="CurrencyInfo"/> object.
        /// Returns a default USD <see cref="CurrencyInfo"/> if the code is null, empty, or not found
        /// in the static lookup. Preserves the <c>CurrencyType</c> pattern from source
        /// <c>Definitions.cs</c> (lines 64-90).
        /// </summary>
        private static CurrencyInfo ResolveCurrencyInfo(string? currencyCode)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
            {
                // Default to USD when no currency specified
                return new CurrencyInfo();
            }

            if (CurrencyLookup.TryGetValue(currencyCode, out var currency))
            {
                // Return a copy to prevent mutation of static lookup entries
                return new CurrencyInfo
                {
                    Code = currency.Code,
                    Symbol = currency.Symbol,
                    SymbolNative = currency.SymbolNative,
                    Name = currency.Name,
                    DecimalDigits = currency.DecimalDigits,
                    Rounding = currency.Rounding,
                    SymbolPlacement = currency.SymbolPlacement
                };
            }

            // Unknown currency code — create entry with the provided code and default decimal digits
            return new CurrencyInfo
            {
                Code = currencyCode.ToUpperInvariant(),
                Symbol = currencyCode.ToUpperInvariant(),
                SymbolNative = currencyCode.ToUpperInvariant(),
                Name = currencyCode.ToUpperInvariant(),
                DecimalDigits = 2
            };
        }
    }
}
