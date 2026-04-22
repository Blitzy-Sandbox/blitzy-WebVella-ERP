using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using WebVellaErp.Invoicing.Models;

namespace WebVellaErp.Invoicing.DataAccess;

/// <summary>
/// Repository interface for invoice and payment persistence operations against RDS PostgreSQL.
/// All methods are async with CancellationToken support for Lambda runtime cancellation.
/// </summary>
public interface IInvoiceRepository
{
    /// <summary>
    /// Retrieves a single invoice by ID, including all associated line items.
    /// </summary>
    /// <param name="id">The unique identifier of the invoice.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>The invoice with line items, or null if not found.</returns>
    Task<Invoice?> GetInvoiceByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Lists invoices with server-side pagination and optional filtering by status and customer.
    /// Line items are NOT loaded for list operations (performance optimization).
    /// </summary>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Maximum number of items per page.</param>
    /// <param name="statusFilter">Optional filter by invoice status.</param>
    /// <param name="customerFilter">Optional filter by customer ID.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>A tuple containing the page of invoices and the total matching count.</returns>
    Task<(List<Invoice> Items, int TotalCount)> ListInvoicesAsync(
        int page,
        int pageSize,
        InvoiceStatus? statusFilter = null,
        Guid? customerFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new invoice with all line items in a single ACID transaction.
    /// </summary>
    /// <param name="invoice">The invoice entity to persist, including line items.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>The persisted invoice.</returns>
    Task<Invoice> CreateInvoiceAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing invoice. Line items are replaced (delete + re-insert) within a transaction.
    /// </summary>
    /// <param name="invoice">The invoice entity with updated values.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>The updated invoice.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the invoice is not found.</exception>
    Task<Invoice> UpdateInvoiceAsync(Invoice invoice, CancellationToken ct = default);

    /// <summary>
    /// Voids an invoice. Idempotent — returns false if already voided or not found.
    /// Uses a WHERE status guard to prevent double-voiding.
    /// </summary>
    /// <param name="invoiceId">The invoice to void.</param>
    /// <param name="modifiedBy">The user performing the void operation.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>True if the invoice was voided; false if not found or already voided.</returns>
    Task<bool> VoidInvoiceAsync(Guid invoiceId, Guid modifiedBy, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single payment by ID.
    /// </summary>
    /// <param name="paymentId">The unique identifier of the payment.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>The payment, or null if not found.</returns>
    Task<Payment?> GetPaymentAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>
    /// Lists all payments for a given invoice, ordered by payment date ascending.
    /// </summary>
    /// <param name="invoiceId">The invoice whose payments to retrieve.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>A list of payments for the specified invoice.</returns>
    Task<List<Payment>> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new payment record within an ACID transaction.
    /// </summary>
    /// <param name="payment">The payment entity to persist.</param>
    /// <param name="ct">Cancellation token for Lambda runtime cancellation.</param>
    /// <returns>The persisted payment.</returns>
    Task<Payment> CreatePaymentAsync(Payment payment, CancellationToken ct = default);
}

/// <summary>
/// Strongly-typed PostgreSQL repository for invoice and payment persistence.
/// Replaces the monolith's dynamic DbRecordRepository pattern with schema-aware,
/// ACID-transactional data access using the invoicing schema in RDS PostgreSQL.
/// All write operations use explicit NpgsqlTransaction with Begin/Commit/Rollback.
/// Connection string is DI-injected (sourced from SSM Parameter Store at startup).
/// </summary>
public class InvoiceRepository : IInvoiceRepository
{
    // ────────────────────────────── Schema Constants ──────────────────────────────
    // Schema-level isolation per AAP §0.4.2 — replaces monolith's rec_* dynamic tables.
    private const string InvoicesTable = "invoicing.invoices";
    private const string LineItemsTable = "invoicing.line_items";
    private const string PaymentsTable = "invoicing.payments";

    // ────────────────────────────── Dependencies ──────────────────────────────────
    private readonly string _connectionString;
    private readonly ILogger<InvoiceRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="InvoiceRepository"/>.
    /// </summary>
    /// <param name="connectionString">
    /// RDS PostgreSQL connection string retrieved from SSM Parameter Store.
    /// Must never originate from an environment variable (AAP §0.8.6).
    /// </param>
    /// <param name="logger">Logger for structured JSON logging with correlation-ID propagation.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="connectionString"/> is null or whitespace.
    /// </exception>
    public InvoiceRepository(string connectionString, ILogger<InvoiceRepository> logger)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentNullException(nameof(connectionString), "Connection string must not be null or empty.");

        _connectionString = connectionString;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ────────────────────────────── Connection Helper ─────────────────────────────

    /// <summary>
    /// Creates and opens a new <see cref="NpgsqlConnection"/> asynchronously.
    /// Each method owns its own connection — no ambient DbContext singleton.
    /// </summary>
    private async Task<NpgsqlConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    // ══════════════════════════════ Invoice CRUD ══════════════════════════════════

    /// <inheritdoc />
    public async Task<Invoice> CreateInvoiceAsync(Invoice invoice, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // ── Insert invoice header ──
            const string insertInvoiceSql = @"
                INSERT INTO invoicing.invoices
                    (id, invoice_number, customer_id, status, issue_date, due_date,
                     sub_total, tax_amount, total_amount, currency, notes,
                     created_by, created_on, last_modified_by, last_modified_on)
                VALUES
                    (@id, @invoice_number, @customer_id, @status, @issue_date, @due_date,
                     @sub_total, @tax_amount, @total_amount, @currency, @notes,
                     @created_by, @created_on, @last_modified_by, @last_modified_on)";

            await using (var cmd = new NpgsqlCommand(insertInvoiceSql, connection, transaction))
            {
                AddInvoiceParameters(cmd, invoice);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // ── Insert line items ──
            if (invoice.LineItems is { Count: > 0 })
            {
                await InsertLineItemsAsync(connection, transaction, invoice.LineItems, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Invoice {InvoiceId} (#{InvoiceNumber}) created successfully with {LineItemCount} line items",
                invoice.Id, invoice.InvoiceNumber, invoice.LineItems?.Count ?? 0);

            return invoice;
        }
        catch (NpgsqlException ex)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex,
                "Failed to create invoice {InvoiceId} (#{InvoiceNumber}). Transaction rolled back",
                invoice.Id, invoice.InvoiceNumber);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Invoice> UpdateInvoiceAsync(Invoice invoice, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // ── Update invoice header ──
            const string updateInvoiceSql = @"
                UPDATE invoicing.invoices
                SET customer_id       = @customer_id,
                    status            = @status,
                    issue_date        = @issue_date,
                    due_date          = @due_date,
                    sub_total         = @sub_total,
                    tax_amount        = @tax_amount,
                    total_amount      = @total_amount,
                    currency          = @currency,
                    notes             = @notes,
                    last_modified_by  = @last_modified_by,
                    last_modified_on  = @last_modified_on
                WHERE id = @id";

            int affected;
            await using (var cmd = new NpgsqlCommand(updateInvoiceSql, connection, transaction))
            {
                AddInvoiceParameters(cmd, invoice);
                affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (affected == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Failed to update invoice {invoice.Id}. The record was not found.");
            }

            // ── Replace line items: delete existing, re-insert updated set ──
            const string deleteLineItemsSql = "DELETE FROM invoicing.line_items WHERE invoice_id = @invoice_id";
            await using (var cmd = new NpgsqlCommand(deleteLineItemsSql, connection, transaction))
            {
                cmd.Parameters.Add(new NpgsqlParameter("@invoice_id", NpgsqlDbType.Uuid) { Value = invoice.Id });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (invoice.LineItems is { Count: > 0 })
            {
                await InsertLineItemsAsync(connection, transaction, invoice.LineItems, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Invoice {InvoiceId} (#{InvoiceNumber}) updated successfully with {LineItemCount} line items",
                invoice.Id, invoice.InvoiceNumber, invoice.LineItems?.Count ?? 0);

            return invoice;
        }
        catch (NpgsqlException ex)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex,
                "Failed to update invoice {InvoiceId} (#{InvoiceNumber}). Transaction rolled back",
                invoice.Id, invoice.InvoiceNumber);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetInvoiceByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

        // ── Fetch invoice header ──
        const string selectInvoiceSql = @"
            SELECT id, invoice_number, customer_id, status, issue_date, due_date,
                   sub_total, tax_amount, total_amount, currency, notes,
                   created_by, created_on, last_modified_by, last_modified_on
            FROM invoicing.invoices
            WHERE id = @id";

        Invoice? invoice = null;

        await using (var cmd = new NpgsqlCommand(selectInvoiceSql, connection))
        {
            cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = id });

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                invoice = MapInvoiceFromReader(reader);
            }
        }

        if (invoice is null)
        {
            return null;
        }

        // ── Fetch associated line items ──
        const string selectLineItemsSql = @"
            SELECT id, invoice_id, description, quantity, unit_price, tax_rate, line_total, sort_order
            FROM invoicing.line_items
            WHERE invoice_id = @invoice_id
            ORDER BY sort_order";

        var lineItems = new List<LineItem>();

        await using (var cmd = new NpgsqlCommand(selectLineItemsSql, connection))
        {
            cmd.Parameters.Add(new NpgsqlParameter("@invoice_id", NpgsqlDbType.Uuid) { Value = id });

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                lineItems.Add(MapLineItemFromReader(reader));
            }
        }

        invoice.LineItems = lineItems;
        return invoice;
    }

    /// <inheritdoc />
    public async Task<(List<Invoice> Items, int TotalCount)> ListInvoicesAsync(
        int page,
        int pageSize,
        InvoiceStatus? statusFilter = null,
        Guid? customerFilter = null,
        CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

        // ── Build dynamic WHERE clause with parameterized filters ──
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (statusFilter.HasValue)
        {
            conditions.Add("status = @status");
            parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Text)
            {
                Value = statusFilter.Value.ToString()
            });
        }

        if (customerFilter.HasValue)
        {
            conditions.Add("customer_id = @customer_id");
            parameters.Add(new NpgsqlParameter("@customer_id", NpgsqlDbType.Uuid)
            {
                Value = customerFilter.Value
            });
        }

        string whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : string.Empty;

        // ── Count total matching records ──
        int totalCount;
        string countSql = $"SELECT COUNT(id) FROM invoicing.invoices {whereClause}";

        await using (var cmd = new NpgsqlCommand(countSql, connection))
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p.Clone());
            }

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            totalCount = Convert.ToInt32(result);
        }

        // ── Fetch paginated results ──
        int offset = (page - 1) * pageSize;

        string selectSql = $@"
            SELECT id, invoice_number, customer_id, status, issue_date, due_date,
                   sub_total, tax_amount, total_amount, currency, notes,
                   created_by, created_on, last_modified_by, last_modified_on
            FROM invoicing.invoices
            {whereClause}
            ORDER BY created_on DESC
            LIMIT @limit OFFSET @offset";

        var invoices = new List<Invoice>();

        await using (var cmd = new NpgsqlCommand(selectSql, connection))
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.Add(p.Clone());
            }

            cmd.Parameters.Add(new NpgsqlParameter("@limit", NpgsqlDbType.Integer) { Value = pageSize });
            cmd.Parameters.Add(new NpgsqlParameter("@offset", NpgsqlDbType.Integer) { Value = offset });

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                invoices.Add(MapInvoiceFromReader(reader));
            }
        }

        // Line items are NOT loaded for list operations (performance optimization).
        // The service layer should call GetInvoiceByIdAsync for detail views.
        return (invoices, totalCount);
    }

    /// <inheritdoc />
    public async Task<bool> VoidInvoiceAsync(Guid invoiceId, Guid modifiedBy, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Idempotent: WHERE guard prevents double-voiding (AAP §0.8.5).
            const string voidSql = @"
                UPDATE invoicing.invoices
                SET status           = @status,
                    last_modified_by = @modified_by,
                    last_modified_on = @modified_on
                WHERE id = @id AND status != @voided_status";

            int affected;
            await using (var cmd = new NpgsqlCommand(voidSql, connection, transaction))
            {
                cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = invoiceId });
                cmd.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Text)
                {
                    Value = InvoiceStatus.Voided.ToString()
                });
                cmd.Parameters.Add(new NpgsqlParameter("@modified_by", NpgsqlDbType.Uuid) { Value = modifiedBy });
                cmd.Parameters.Add(new NpgsqlParameter("@modified_on", NpgsqlDbType.TimestampTz)
                {
                    Value = DateTime.UtcNow
                });
                cmd.Parameters.Add(new NpgsqlParameter("@voided_status", NpgsqlDbType.Text)
                {
                    Value = InvoiceStatus.Voided.ToString()
                });

                affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (affected > 0)
            {
                _logger.LogInformation("Invoice {InvoiceId} voided successfully by user {ModifiedBy}",
                    invoiceId, modifiedBy);
            }
            else
            {
                _logger.LogWarning(
                    "Invoice {InvoiceId} void had no effect — either not found or already voided",
                    invoiceId);
            }

            return affected > 0;
        }
        catch (NpgsqlException ex)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex,
                "Failed to void invoice {InvoiceId}. Transaction rolled back", invoiceId);
            throw;
        }
    }

    // ══════════════════════════════ Payment CRUD ══════════════════════════════════

    /// <inheritdoc />
    public async Task<Payment> CreatePaymentAsync(Payment payment, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            const string insertPaymentSql = @"
                INSERT INTO invoicing.payments
                    (id, invoice_id, amount, payment_date, payment_method,
                     reference_number, notes, created_by, created_on)
                VALUES
                    (@id, @invoice_id, @amount, @payment_date, @payment_method,
                     @reference_number, @notes, @created_by, @created_on)";

            await using (var cmd = new NpgsqlCommand(insertPaymentSql, connection, transaction))
            {
                AddPaymentParameters(cmd, payment);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Payment {PaymentId} of {Amount} created for invoice {InvoiceId}",
                payment.Id, payment.Amount, payment.InvoiceId);

            return payment;
        }
        catch (NpgsqlException ex)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex,
                "Failed to create payment {PaymentId} for invoice {InvoiceId}. Transaction rolled back",
                payment.Id, payment.InvoiceId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Payment?> GetPaymentAsync(Guid paymentId, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

        const string selectPaymentSql = @"
            SELECT id, invoice_id, amount, payment_date, payment_method,
                   reference_number, notes, created_by, created_on
            FROM invoicing.payments
            WHERE id = @id";

        await using var cmd = new NpgsqlCommand(selectPaymentSql, connection);
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = paymentId });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return MapPaymentFromReader(reader);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<List<Payment>> ListPaymentsForInvoiceAsync(Guid invoiceId, CancellationToken ct = default)
    {
        await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

        const string selectPaymentsSql = @"
            SELECT id, invoice_id, amount, payment_date, payment_method,
                   reference_number, notes, created_by, created_on
            FROM invoicing.payments
            WHERE invoice_id = @invoice_id
            ORDER BY payment_date";

        var payments = new List<Payment>();

        await using var cmd = new NpgsqlCommand(selectPaymentsSql, connection);
        cmd.Parameters.Add(new NpgsqlParameter("@invoice_id", NpgsqlDbType.Uuid) { Value = invoiceId });

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            payments.Add(MapPaymentFromReader(reader));
        }

        return payments;
    }

    // ══════════════════════════════ Private Helpers ═══════════════════════════════

    /// <summary>
    /// Inserts a collection of line items within the supplied transaction.
    /// Each line item is inserted as a separate parameterized command.
    /// </summary>
    private static async Task InsertLineItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        List<LineItem> lineItems,
        CancellationToken ct)
    {
        const string insertLineItemSql = @"
            INSERT INTO invoicing.line_items
                (id, invoice_id, description, quantity, unit_price, tax_rate, line_total, sort_order)
            VALUES
                (@id, @invoice_id, @description, @quantity, @unit_price, @tax_rate, @line_total, @sort_order)";

        foreach (var item in lineItems)
        {
            await using var cmd = new NpgsqlCommand(insertLineItemSql, connection, transaction);
            cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = item.Id });
            cmd.Parameters.Add(new NpgsqlParameter("@invoice_id", NpgsqlDbType.Uuid) { Value = item.InvoiceId });
            cmd.Parameters.Add(new NpgsqlParameter("@description", NpgsqlDbType.Text)
            {
                Value = (object?)item.Description ?? DBNull.Value
            });
            cmd.Parameters.Add(new NpgsqlParameter("@quantity", NpgsqlDbType.Numeric) { Value = item.Quantity });
            cmd.Parameters.Add(new NpgsqlParameter("@unit_price", NpgsqlDbType.Numeric) { Value = item.UnitPrice });
            cmd.Parameters.Add(new NpgsqlParameter("@tax_rate", NpgsqlDbType.Numeric) { Value = item.TaxRate });
            cmd.Parameters.Add(new NpgsqlParameter("@line_total", NpgsqlDbType.Numeric) { Value = item.LineTotal });
            cmd.Parameters.Add(new NpgsqlParameter("@sort_order", NpgsqlDbType.Integer) { Value = item.SortOrder });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds all NpgsqlParameter bindings for an invoice to the given command.
    /// All monetary fields use NpgsqlDbType.Numeric for financial precision.
    /// </summary>
    private static void AddInvoiceParameters(NpgsqlCommand cmd, Invoice invoice)
    {
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = invoice.Id });
        cmd.Parameters.Add(new NpgsqlParameter("@invoice_number", NpgsqlDbType.Text)
        {
            Value = (object?)invoice.InvoiceNumber ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("@customer_id", NpgsqlDbType.Uuid) { Value = invoice.CustomerId });
        cmd.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Text) { Value = invoice.Status.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter("@issue_date", NpgsqlDbType.TimestampTz) { Value = invoice.IssueDate });
        cmd.Parameters.Add(new NpgsqlParameter("@due_date", NpgsqlDbType.TimestampTz) { Value = invoice.DueDate });
        cmd.Parameters.Add(new NpgsqlParameter("@sub_total", NpgsqlDbType.Numeric) { Value = invoice.SubTotal });
        cmd.Parameters.Add(new NpgsqlParameter("@tax_amount", NpgsqlDbType.Numeric) { Value = invoice.TaxAmount });
        cmd.Parameters.Add(new NpgsqlParameter("@total_amount", NpgsqlDbType.Numeric) { Value = invoice.TotalAmount });
        cmd.Parameters.Add(new NpgsqlParameter("@currency", NpgsqlDbType.Jsonb)
        {
            Value = invoice.Currency is not null
                ? (object)JsonSerializer.Serialize(invoice.Currency, InvoicingJsonContext.Default.CurrencyInfo)
                : DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("@notes", NpgsqlDbType.Text)
        {
            Value = (object?)invoice.Notes ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("@created_by", NpgsqlDbType.Uuid) { Value = invoice.CreatedBy });
        cmd.Parameters.Add(new NpgsqlParameter("@created_on", NpgsqlDbType.TimestampTz) { Value = invoice.CreatedOn });
        cmd.Parameters.Add(new NpgsqlParameter("@last_modified_by", NpgsqlDbType.Uuid) { Value = invoice.LastModifiedBy });
        cmd.Parameters.Add(new NpgsqlParameter("@last_modified_on", NpgsqlDbType.TimestampTz) { Value = invoice.LastModifiedOn });
    }

    /// <summary>
    /// Adds all NpgsqlParameter bindings for a payment to the given command.
    /// The amount field uses NpgsqlDbType.Numeric for financial precision.
    /// </summary>
    private static void AddPaymentParameters(NpgsqlCommand cmd, Payment payment)
    {
        cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = payment.Id });
        cmd.Parameters.Add(new NpgsqlParameter("@invoice_id", NpgsqlDbType.Uuid) { Value = payment.InvoiceId });
        cmd.Parameters.Add(new NpgsqlParameter("@amount", NpgsqlDbType.Numeric) { Value = payment.Amount });
        cmd.Parameters.Add(new NpgsqlParameter("@payment_date", NpgsqlDbType.TimestampTz) { Value = payment.PaymentDate });
        cmd.Parameters.Add(new NpgsqlParameter("@payment_method", NpgsqlDbType.Text)
        {
            Value = payment.PaymentMethod.ToString()
        });
        cmd.Parameters.Add(new NpgsqlParameter("@reference_number", NpgsqlDbType.Text)
        {
            Value = (object?)payment.ReferenceNumber ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("@notes", NpgsqlDbType.Text)
        {
            Value = (object?)payment.Notes ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter("@created_by", NpgsqlDbType.Uuid) { Value = payment.CreatedBy });
        cmd.Parameters.Add(new NpgsqlParameter("@created_on", NpgsqlDbType.TimestampTz) { Value = payment.CreatedOn });
    }

    // ────────────────────────────── Row Mappers ──────────────────────────────────

    /// <summary>
    /// Maps the current row of a <see cref="NpgsqlDataReader"/> to an <see cref="Invoice"/> model.
    /// Handles nullable columns (currency, notes) with IsDBNull checks.
    /// Currency is stored as JSONB and deserialized via System.Text.Json.
    /// </summary>
    private static Invoice MapInvoiceFromReader(NpgsqlDataReader reader)
    {
        var statusOrdinal = reader.GetOrdinal("status");
        var currencyOrdinal = reader.GetOrdinal("currency");
        var notesOrdinal = reader.GetOrdinal("notes");

        return new Invoice
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            InvoiceNumber = reader.IsDBNull(reader.GetOrdinal("invoice_number"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("invoice_number")),
            CustomerId = reader.GetGuid(reader.GetOrdinal("customer_id")),
            Status = Enum.Parse<InvoiceStatus>(reader.GetString(statusOrdinal), ignoreCase: true),
            IssueDate = reader.GetDateTime(reader.GetOrdinal("issue_date")),
            DueDate = reader.GetDateTime(reader.GetOrdinal("due_date")),
            SubTotal = reader.GetDecimal(reader.GetOrdinal("sub_total")),
            TaxAmount = reader.GetDecimal(reader.GetOrdinal("tax_amount")),
            TotalAmount = reader.GetDecimal(reader.GetOrdinal("total_amount")),
            Currency = reader.IsDBNull(currencyOrdinal)
                ? null
                : JsonSerializer.Deserialize(reader.GetString(currencyOrdinal), InvoicingJsonContext.Default.CurrencyInfo),
            Notes = reader.IsDBNull(notesOrdinal)
                ? string.Empty
                : reader.GetString(notesOrdinal),
            CreatedBy = reader.GetGuid(reader.GetOrdinal("created_by")),
            CreatedOn = reader.GetDateTime(reader.GetOrdinal("created_on")),
            LastModifiedBy = reader.GetGuid(reader.GetOrdinal("last_modified_by")),
            LastModifiedOn = reader.GetDateTime(reader.GetOrdinal("last_modified_on")),
            // Line items loaded separately in GetInvoiceByIdAsync; empty list for list operations.
            LineItems = new List<LineItem>()
        };
    }

    /// <summary>
    /// Maps the current row of a <see cref="NpgsqlDataReader"/> to a <see cref="Payment"/> model.
    /// PaymentMethod is stored as text and parsed back via Enum.Parse.
    /// </summary>
    private static Payment MapPaymentFromReader(NpgsqlDataReader reader)
    {
        var refOrdinal = reader.GetOrdinal("reference_number");
        var notesOrdinal = reader.GetOrdinal("notes");

        return new Payment
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            InvoiceId = reader.GetGuid(reader.GetOrdinal("invoice_id")),
            Amount = reader.GetDecimal(reader.GetOrdinal("amount")),
            PaymentDate = reader.GetDateTime(reader.GetOrdinal("payment_date")),
            PaymentMethod = Enum.Parse<PaymentMethod>(
                reader.GetString(reader.GetOrdinal("payment_method")), ignoreCase: true),
            ReferenceNumber = reader.IsDBNull(refOrdinal)
                ? string.Empty
                : reader.GetString(refOrdinal),
            Notes = reader.IsDBNull(notesOrdinal)
                ? string.Empty
                : reader.GetString(notesOrdinal),
            CreatedBy = reader.GetGuid(reader.GetOrdinal("created_by")),
            CreatedOn = reader.GetDateTime(reader.GetOrdinal("created_on"))
        };
    }

    /// <summary>
    /// Maps the current row of a <see cref="NpgsqlDataReader"/> to a <see cref="LineItem"/> model.
    /// All monetary fields (quantity, unit_price, tax_rate, line_total) are read as decimal.
    /// </summary>
    private static LineItem MapLineItemFromReader(NpgsqlDataReader reader)
    {
        var descOrdinal = reader.GetOrdinal("description");

        return new LineItem
        {
            Id = reader.GetGuid(reader.GetOrdinal("id")),
            InvoiceId = reader.GetGuid(reader.GetOrdinal("invoice_id")),
            Description = reader.IsDBNull(descOrdinal)
                ? string.Empty
                : reader.GetString(descOrdinal),
            Quantity = reader.GetDecimal(reader.GetOrdinal("quantity")),
            UnitPrice = reader.GetDecimal(reader.GetOrdinal("unit_price")),
            TaxRate = reader.GetDecimal(reader.GetOrdinal("tax_rate")),
            LineTotal = reader.GetDecimal(reader.GetOrdinal("line_total")),
            SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order"))
        };
    }
}

/// <summary>
/// AOT-compatible JSON source generator context for CurrencyInfo serialization/deserialization.
/// Required because PublishAot=true in Invoicing.csproj demands trimming-safe JSON handling.
/// Replaces monolith's reflection-based Newtonsoft.Json usage per AAP §0.5.2.
/// </summary>
[JsonSerializable(typeof(CurrencyInfo))]
internal partial class InvoicingJsonContext : JsonSerializerContext
{
}
