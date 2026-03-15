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
    /// Defines the contract for payment reconciliation operations within the Invoicing bounded context.
    /// Covers payment processing (with partial-payment support), retrieval, listing, and balance calculations.
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item><description>Replaces <c>RecordManager.CreateRecord()</c> (source <c>WebVella.Erp/Api/RecordManager.cs</c>
    ///     lines 254-900) with strongly-typed, domain-specific payment processing.</description></item>
    ///   <item><description>Replaces <c>RecordHookManager.ExecutePostCreateRecordHooks()</c> post-CRUD hooks
    ///     with SNS domain events (<c>invoicing.payment.processed</c>, <c>invoicing.invoice.paid</c>)
    ///     per AAP §0.7.2.</description></item>
    ///   <item><description>Replaces <c>RecordManager.Find()</c> (source lines 1736-1850) with
    ///     strongly-typed payment retrieval and listing.</description></item>
    /// </list>
    ///
    /// <para><b>Architecture decisions (from AAP):</b></para>
    /// <list type="bullet">
    ///   <item><description>§0.8.1 — Full behavioral parity: every payment processing step from
    ///     RecordManager has a functional equivalent.</description></item>
    ///   <item><description>§0.8.1 — Self-contained bounded context: payment belongs exclusively
    ///     to the Invoicing service with its own RDS PostgreSQL datastore.</description></item>
    ///   <item><description>§0.4.2 — Database-Per-Service: payments stored in RDS PostgreSQL for
    ///     ACID transaction guarantees.</description></item>
    ///   <item><description>§0.8.5 — Idempotency keys on all write endpoints; structured JSON logging
    ///     with correlation-ID propagation.</description></item>
    /// </list>
    /// </summary>
    public interface IPaymentService
    {
        /// <summary>
        /// Validates and records a payment against an invoice, updates invoice status if fully paid,
        /// and publishes domain events via SNS.
        ///
        /// <para>Replaces the monolith's <c>RecordManager.CreateRecord(entity, record)</c> flow
        /// (source lines 254-900) with: validate → persist (ACID) → update invoice status → publish events.</para>
        ///
        /// <para>Supports partial payments: multiple payments can be applied to the same invoice
        /// until the total paid equals or exceeds the invoice total amount.</para>
        /// </summary>
        /// <param name="request">Payment creation request with invoice ID, amount, date, and method.</param>
        /// <param name="userId">ID of the user creating the payment (extracted from JWT claims by Lambda handler).</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the created payment or validation errors.</returns>
        Task<PaymentResponse> ProcessPaymentAsync(CreatePaymentRequest request, Guid userId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a single payment by its unique identifier.
        /// Replaces <c>RecordManager.Find(entityQuery)</c> (source lines 1736-1850).
        /// </summary>
        /// <param name="paymentId">Unique identifier of the payment to retrieve.</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the payment or a not-found error.</returns>
        Task<PaymentResponse> GetPaymentAsync(Guid paymentId, CancellationToken ct = default);

        /// <summary>
        /// Lists all payments recorded against a specific invoice.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to list payments for.</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Response containing the list of payments and total count.</returns>
        Task<PaymentListResponse> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct = default);

        /// <summary>
        /// Calculates the total amount paid across all payments for a given invoice.
        /// Uses <c>decimal</c> arithmetic with LINQ <c>.Sum()</c> for financial precision.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to calculate total paid for.</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Total amount paid as a decimal value.</returns>
        Task<decimal> GetTotalPaidAmountAsync(Guid invoiceId, CancellationToken ct = default);

        /// <summary>
        /// Calculates the remaining balance for an invoice (TotalAmount - TotalPaid).
        /// Returns 0 if the invoice is fully paid or overpaid.
        /// </summary>
        /// <param name="invoiceId">ID of the invoice to calculate remaining balance for.</param>
        /// <param name="ct">Cancellation token for Lambda runtime propagation.</param>
        /// <returns>Remaining balance as a decimal value (never negative).</returns>
        Task<decimal> GetRemainingBalanceAsync(Guid invoiceId, CancellationToken ct = default);
    }

    /// <summary>
    /// Payment reconciliation service implementing <see cref="IPaymentService"/>.
    /// Manages payment recording, validation, partial-payment tracking, and invoice status
    /// transitions backed by RDS PostgreSQL with ACID transaction guarantees per AAP §0.4.2.
    ///
    /// <para>All operations follow the validate → persist → publish-event pattern extracted from
    /// the monolith's <c>RecordManager.CreateRecord()</c> orchestration
    /// (source <c>WebVella.Erp/Api/RecordManager.cs</c> lines 254-900).</para>
    ///
    /// <para><b>Key transformations from monolith:</b></para>
    /// <list type="bullet">
    ///   <item><description>NO <c>DbContext.Current</c> ambient singleton — repository handles its own connections.</description></item>
    ///   <item><description>NO <c>SecurityContext</c> — user identity passed as <c>userId</c> parameter from JWT claims.</description></item>
    ///   <item><description>NO <c>RecordHookManager</c> — post-CRUD hooks replaced by SNS domain events.</description></item>
    ///   <item><description>All monetary calculations use <c>decimal</c> with <c>MidpointRounding.AwayFromZero</c>.</description></item>
    ///   <item><description>Structured JSON logging with correlation-ID per AAP §0.8.5.</description></item>
    /// </list>
    /// </summary>
    public class PaymentService : IPaymentService
    {
        private readonly IInvoiceRepository _repository;
        private readonly IInvoiceEventPublisher _eventPublisher;
        private readonly IInvoiceService _invoiceService;
        private readonly ILogger<PaymentService> _logger;

        /// <summary>
        /// Default number of decimal digits for currency rounding when no CurrencyInfo is available.
        /// Matches the CurrencyInfo.DecimalDigits default value of 2 defined in Invoice.cs.
        /// </summary>
        private const int DefaultCurrencyDecimalDigits = 2;

        /// <summary>
        /// Initializes a new instance of <see cref="PaymentService"/> with all required dependencies
        /// injected via the DI container. Replaces the monolith's <c>new RecordManager(context, ignoreSecurity, executeHooks)</c>
        /// constructor pattern (source <c>RecordManager.cs</c> line 40).
        ///
        /// <para><b>Dependency injection replaces:</b></para>
        /// <list type="bullet">
        ///   <item><description><c>DbContext.Current.RecordRepository</c> → <paramref name="repository"/></description></item>
        ///   <item><description><c>RecordHookManager.ExecutePostCreateRecordHooks()</c> → <paramref name="eventPublisher"/></description></item>
        ///   <item><description><c>SecurityContext.CurrentUser.Id</c> → <c>userId</c> parameter on methods</description></item>
        /// </list>
        /// </summary>
        /// <param name="repository">RDS PostgreSQL data access for invoice and payment operations.</param>
        /// <param name="eventPublisher">SNS domain event publisher for payment and invoice events.</param>
        /// <param name="invoiceService">Invoice lifecycle service for marking invoices as paid.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID support per AAP §0.8.5.</param>
        /// <exception cref="ArgumentNullException">Thrown when any dependency is null.</exception>
        public PaymentService(
            IInvoiceRepository repository,
            IInvoiceEventPublisher eventPublisher,
            IInvoiceService invoiceService,
            ILogger<PaymentService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
            _invoiceService = invoiceService ?? throw new ArgumentNullException(nameof(invoiceService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para><b>Processing pipeline:</b></para>
        /// <list type="number">
        ///   <item><description><b>Input Validation</b> — Null request, empty invoice ID, non-positive amount.</description></item>
        ///   <item><description><b>Invoice Lookup</b> — Retrieve invoice and validate status (must be Issued).</description></item>
        ///   <item><description><b>Amount Validation</b> — Calculate remaining balance; reject overpayments.</description></item>
        ///   <item><description><b>Persistence</b> — Build Payment model, round amount by currency decimals, persist in ACID transaction.</description></item>
        ///   <item><description><b>Invoice Status Update</b> — Mark invoice as Paid if total paid >= total amount.</description></item>
        ///   <item><description><b>Event Publishing</b> — Publish <c>invoicing.payment.processed</c> and optionally <c>invoicing.invoice.paid</c> via SNS.</description></item>
        /// </list>
        ///
        /// <para><b>Currency rounding:</b> Uses <c>decimal.Round(amount, currency.DecimalDigits, MidpointRounding.AwayFromZero)</c>
        /// following the CurrencyField pattern from source <c>RecordManager.cs</c> line 1893.</para>
        ///
        /// <para><b>Error handling:</b> All validation errors follow the <see cref="ErrorModel"/> pattern
        /// from source <c>WebVella.Erp/Api/Models/BaseModels.cs</c> lines 62-83.</para>
        /// </remarks>
        public async Task<PaymentResponse> ProcessPaymentAsync(
            CreatePaymentRequest request,
            Guid userId,
            CancellationToken ct = default)
        {
            var response = new PaymentResponse
            {
                Timestamp = DateTime.UtcNow,
                Success = true
            };

            try
            {
                // ─── Phase 1: Input Validation ───────────────────────────────────
                // Follows source RecordManager.cs lines 268-293 validation pattern:
                // null check → field validation → early return on errors.

                if (request == null)
                {
                    _logger.LogWarning("ProcessPaymentAsync called with null request by user {UserId}.", userId);
                    response.Errors.Add(new ErrorModel("request", string.Empty, "Invalid payment request. Cannot be null."));
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                if (request.InvoiceId == Guid.Empty)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync called with empty InvoiceId by user {UserId}.",
                        userId);
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty, "Invoice ID is required."));
                }

                if (request.Amount <= 0m)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync called with non-positive amount {Amount} by user {UserId}.",
                        request.Amount, userId);
                    response.Errors.Add(new ErrorModel(
                        "amount",
                        request.Amount.ToString("F2"),
                        "Payment amount must be greater than zero."));
                }

                // Early return if basic input validation failed (source pattern lines 274-280)
                if (response.Errors.Any())
                {
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                // ─── Phase 2: Invoice Lookup ─────────────────────────────────────
                // Retrieve the target invoice and validate its current status.
                // Follows source RecordManager.cs pattern of entity existence check before record creation.

                var invoice = await _repository.GetInvoiceByIdAsync(request.InvoiceId, ct).ConfigureAwait(false);

                if (invoice == null)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync: Invoice {InvoiceId} not found. User: {UserId}.",
                        request.InvoiceId, userId);
                    response.Errors.Add(new ErrorModel(
                        "invoiceId",
                        request.InvoiceId.ToString(),
                        "Invoice not found."));
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                // Validate invoice status — only Issued invoices may accept payments.
                // Draft invoices have not been formally issued; Voided invoices are cancelled;
                // Paid invoices have already received full payment.
                if (invoice.Status == InvoiceStatus.Draft)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync: Cannot process payment for draft invoice {InvoiceId}. User: {UserId}.",
                        request.InvoiceId, userId);
                    response.Errors.Add(new ErrorModel(
                        "invoiceStatus",
                        InvoiceStatus.Draft.ToString(),
                        "Cannot process payment for a draft invoice. Issue the invoice first."));
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                if (invoice.Status == InvoiceStatus.Voided)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync: Cannot process payment for voided invoice {InvoiceId}. User: {UserId}.",
                        request.InvoiceId, userId);
                    response.Errors.Add(new ErrorModel(
                        "invoiceStatus",
                        InvoiceStatus.Voided.ToString(),
                        "Cannot process payment for a voided invoice."));
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                if (invoice.Status == InvoiceStatus.Paid)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync: Invoice {InvoiceId} is already fully paid. User: {UserId}.",
                        request.InvoiceId, userId);
                    response.Errors.Add(new ErrorModel(
                        "invoiceStatus",
                        InvoiceStatus.Paid.ToString(),
                        "Invoice is already fully paid."));
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                // ─── Phase 3: Payment Amount Validation ──────────────────────────
                // Calculate how much has already been paid and verify the new payment
                // does not exceed the remaining balance. Uses decimal arithmetic
                // (never double) for financial precision.

                var existingPayments = await _repository.ListPaymentsForInvoiceAsync(request.InvoiceId, ct)
                    .ConfigureAwait(false);

                decimal totalAlreadyPaid = existingPayments.Any()
                    ? existingPayments.Sum(p => p.Amount)
                    : 0m;

                decimal remainingBalance = invoice.TotalAmount - totalAlreadyPaid;

                // Determine the number of decimal digits for currency rounding.
                // Uses Invoice.Currency.DecimalDigits when available, falling back to default of 2.
                // Preserves the CurrencyField rounding pattern from source RecordManager.cs line 1893.
                int decimalDigits = invoice.Currency?.DecimalDigits ?? DefaultCurrencyDecimalDigits;

                decimal roundedAmount = decimal.Round(request.Amount, decimalDigits, MidpointRounding.AwayFromZero);
                decimal roundedRemaining = decimal.Round(remainingBalance, decimalDigits, MidpointRounding.AwayFromZero);

                if (roundedAmount > roundedRemaining)
                {
                    _logger.LogWarning(
                        "ProcessPaymentAsync: Payment amount {Amount} exceeds remaining balance {Remaining} for invoice {InvoiceId}. User: {UserId}.",
                        roundedAmount, roundedRemaining, request.InvoiceId, userId);
                    response.Errors.Add(new ErrorModel(
                        "amount",
                        roundedAmount.ToString("F" + decimalDigits),
                        $"Payment amount ({roundedAmount}) exceeds remaining balance ({roundedRemaining})."));
                    response.Success = false;
                    response.Message = "Payment validation failed.";
                    return response;
                }

                // ─── Phase 4: Persistence ────────────────────────────────────────
                // Build the Payment domain model from the validated request.
                // Replaces source RecordManager.cs lines 296-900 record creation.
                // Id generated via Guid.NewGuid() following source line 322 pattern.

                var payment = new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = request.InvoiceId,
                    Amount = roundedAmount,
                    PaymentDate = request.PaymentDate,
                    PaymentMethod = request.PaymentMethod,
                    ReferenceNumber = request.ReferenceNumber ?? string.Empty,
                    Notes = request.Notes ?? string.Empty,
                    CreatedBy = userId,
                    CreatedOn = DateTime.UtcNow
                };

                // Persist payment via ACID transaction in RDS PostgreSQL.
                // The repository wraps this in a transaction per AAP §0.4.2.
                await _repository.CreatePaymentAsync(payment, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Payment {PaymentId} of {Amount} recorded for invoice {InvoiceId} by user {UserId}.",
                    payment.Id, payment.Amount, payment.InvoiceId, userId);

                // ─── Phase 5: Invoice Status Update ──────────────────────────────
                // Recalculate total paid after recording this payment.
                // If total paid >= invoice total, transition invoice to Paid status.
                // Delegated to IInvoiceService.MarkInvoicePaidAsync to maintain separation of concerns.

                decimal newTotalPaid = totalAlreadyPaid + roundedAmount;
                bool invoiceFullyPaid = newTotalPaid >= invoice.TotalAmount;

                if (invoiceFullyPaid)
                {
                    _logger.LogInformation(
                        "Invoice {InvoiceId} fully paid (total paid: {TotalPaid}, total amount: {TotalAmount}). Marking as Paid.",
                        invoice.Id, newTotalPaid, invoice.TotalAmount);

                    var markPaidResult = await _invoiceService.MarkInvoicePaidAsync(invoice.Id, userId, ct)
                        .ConfigureAwait(false);

                    if (!markPaidResult.Success)
                    {
                        // Log the warning but do not fail the payment — the payment itself was persisted successfully.
                        // The invoice status update is a follow-on action; eventual consistency is acceptable.
                        _logger.LogWarning(
                            "Failed to mark invoice {InvoiceId} as Paid after payment {PaymentId}. Errors: {Errors}",
                            invoice.Id,
                            payment.Id,
                            string.Join("; ", markPaidResult.Errors.Select(e => e.Message ?? string.Empty)));
                    }
                }

                // ─── Phase 6: Event Publishing ───────────────────────────────────
                // Publish SNS domain events replacing the monolith's post-CRUD hooks.
                // RecordHookManager.ExecutePostCreateRecordHooks() → PublishPaymentProcessedAsync()
                // Per AAP §0.7.2 and §0.8.5 event naming: invoicing.payment.processed

                await _eventPublisher.PublishPaymentProcessedAsync(payment, ct).ConfigureAwait(false);

                if (invoiceFullyPaid)
                {
                    // Publish the invoice.paid event so downstream services (Reporting, Notifications)
                    // can react to the fully-paid state transition.
                    // Event name: invoicing.invoice.paid (per AAP §0.8.5 naming convention).
                    await _eventPublisher.PublishInvoicePaidAsync(invoice, ct).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Published invoicing.invoice.paid event for invoice {InvoiceId}.",
                        invoice.Id);
                }

                // ─── Phase 7: Response ───────────────────────────────────────────
                response.Object = payment;
                response.Success = true;
                response.Timestamp = DateTime.UtcNow;
                response.Message = invoiceFullyPaid
                    ? "Payment processed successfully. Invoice is now fully paid."
                    : "Payment processed successfully.";

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error in ProcessPaymentAsync for invoice {InvoiceId}, user {UserId}.",
                    request?.InvoiceId, userId);

                response.Success = false;
                response.Timestamp = DateTime.UtcNow;
                response.Message = "An unexpected error occurred while processing the payment.";
                response.Errors.Add(new ErrorModel("payment", string.Empty, ex.Message));
                return response;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Retrieves a single payment by ID from the repository.
        /// Replaces <c>RecordManager.Find(entityQuery)</c> (source lines 1736-1850).
        /// Returns a not-found error via <see cref="ErrorModel"/> if the payment does not exist.
        /// </remarks>
        public async Task<PaymentResponse> GetPaymentAsync(Guid paymentId, CancellationToken ct = default)
        {
            var response = new PaymentResponse
            {
                Timestamp = DateTime.UtcNow,
                Success = true
            };

            try
            {
                if (paymentId == Guid.Empty)
                {
                    _logger.LogWarning("GetPaymentAsync called with empty payment ID.");
                    response.Errors.Add(new ErrorModel("paymentId", string.Empty, "Payment ID is required."));
                    response.Success = false;
                    response.Message = "Payment retrieval failed.";
                    return response;
                }

                var payment = await _repository.GetPaymentAsync(paymentId, ct).ConfigureAwait(false);

                if (payment == null)
                {
                    _logger.LogWarning("GetPaymentAsync: Payment {PaymentId} not found.", paymentId);
                    response.Errors.Add(new ErrorModel(
                        "paymentId",
                        paymentId.ToString(),
                        "Payment not found."));
                    response.Success = false;
                    response.Message = "Payment not found.";
                    return response;
                }

                response.Object = payment;
                response.Success = true;
                response.Timestamp = DateTime.UtcNow;
                response.Message = "Payment retrieved successfully.";

                _logger.LogInformation("Payment {PaymentId} retrieved successfully.", paymentId);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in GetPaymentAsync for payment {PaymentId}.", paymentId);

                response.Success = false;
                response.Timestamp = DateTime.UtcNow;
                response.Message = "An unexpected error occurred while retrieving the payment.";
                response.Errors.Add(new ErrorModel("payment", string.Empty, ex.Message));
                return response;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Lists all payments for a specific invoice. Returns an empty list (not an error)
        /// if no payments have been recorded yet.
        /// </remarks>
        public async Task<PaymentListResponse> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct = default)
        {
            var response = new PaymentListResponse
            {
                Timestamp = DateTime.UtcNow,
                Success = true
            };

            try
            {
                if (invoiceId == Guid.Empty)
                {
                    _logger.LogWarning("ListPaymentsForInvoiceAsync called with empty invoice ID.");
                    response.Errors.Add(new ErrorModel("invoiceId", string.Empty, "Invoice ID is required."));
                    response.Success = false;
                    response.Message = "Payment listing failed.";
                    return response;
                }

                var payments = await _repository.ListPaymentsForInvoiceAsync(invoiceId, ct).ConfigureAwait(false);

                response.Object = payments ?? new List<Payment>();
                response.TotalCount = response.Object.Count;
                response.Success = true;
                response.Timestamp = DateTime.UtcNow;
                response.Message = $"Retrieved {response.TotalCount} payment(s) for invoice.";

                _logger.LogInformation(
                    "Listed {PaymentCount} payments for invoice {InvoiceId}.",
                    response.TotalCount, invoiceId);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error in ListPaymentsForInvoiceAsync for invoice {InvoiceId}.",
                    invoiceId);

                response.Success = false;
                response.Timestamp = DateTime.UtcNow;
                response.Message = "An unexpected error occurred while listing payments.";
                response.Errors.Add(new ErrorModel("invoiceId", invoiceId.ToString(), ex.Message));
                return response;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Calculates the sum of all payment amounts for a given invoice using LINQ <c>.Sum()</c>
        /// with <c>decimal</c> arithmetic for financial precision. Returns 0 if no payments exist.
        /// </remarks>
        public async Task<decimal> GetTotalPaidAmountAsync(Guid invoiceId, CancellationToken ct = default)
        {
            try
            {
                if (invoiceId == Guid.Empty)
                {
                    _logger.LogWarning("GetTotalPaidAmountAsync called with empty invoice ID.");
                    return 0m;
                }

                var payments = await _repository.ListPaymentsForInvoiceAsync(invoiceId, ct).ConfigureAwait(false);

                decimal totalPaid = payments.Any()
                    ? payments.Sum(p => p.Amount)
                    : 0m;

                _logger.LogInformation(
                    "Total paid amount for invoice {InvoiceId}: {TotalPaid}.",
                    invoiceId, totalPaid);

                return totalPaid;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error in GetTotalPaidAmountAsync for invoice {InvoiceId}.",
                    invoiceId);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Calculates the remaining balance for an invoice: <c>Invoice.TotalAmount - TotalPaid</c>.
        /// Returns 0 if the invoice does not exist, is fully paid, or is overpaid (never returns negative).
        /// Uses <c>decimal</c> comparison for financial precision per AAP requirements.
        /// </remarks>
        public async Task<decimal> GetRemainingBalanceAsync(Guid invoiceId, CancellationToken ct = default)
        {
            try
            {
                if (invoiceId == Guid.Empty)
                {
                    _logger.LogWarning("GetRemainingBalanceAsync called with empty invoice ID.");
                    return 0m;
                }

                var invoice = await _repository.GetInvoiceByIdAsync(invoiceId, ct).ConfigureAwait(false);

                if (invoice == null)
                {
                    _logger.LogWarning(
                        "GetRemainingBalanceAsync: Invoice {InvoiceId} not found. Returning 0.",
                        invoiceId);
                    return 0m;
                }

                var payments = await _repository.ListPaymentsForInvoiceAsync(invoiceId, ct).ConfigureAwait(false);

                decimal totalPaid = payments.Any()
                    ? payments.Sum(p => p.Amount)
                    : 0m;

                // Determine currency decimal digits for consistent rounding.
                int decimalDigits = invoice.Currency?.DecimalDigits ?? DefaultCurrencyDecimalDigits;

                decimal remaining = decimal.Round(
                    invoice.TotalAmount - totalPaid,
                    decimalDigits,
                    MidpointRounding.AwayFromZero);

                // Never return a negative remaining balance — overpayments are clamped to zero.
                decimal result = remaining < 0m ? 0m : remaining;

                _logger.LogInformation(
                    "Remaining balance for invoice {InvoiceId}: {Remaining} (total: {Total}, paid: {Paid}).",
                    invoiceId, result, invoice.TotalAmount, totalPaid);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error in GetRemainingBalanceAsync for invoice {InvoiceId}.",
                    invoiceId);
                throw;
            }
        }
    }
}
