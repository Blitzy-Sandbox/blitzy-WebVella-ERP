// ---------------------------------------------------------------------------
// LineItemCalculationService.cs — Line Item & Invoice Total Calculation Service
// Bounded Context: Invoicing / Billing
// ---------------------------------------------------------------------------
// Pure calculation service for computing line-item totals and aggregating
// invoice-level totals (SubTotal, TaxAmount, TotalAmount). This service
// encapsulates all financial arithmetic following the currency rounding
// patterns extracted from the monolith's RecordManager.ExtractFieldValue()
// for CurrencyField (source RecordManager.cs line 1882-1893):
//
//   decimal.Round(decimalValue,
//       ((CurrencyField)field).Currency.DecimalDigits,
//       MidpointRounding.AwayFromZero);
//
// CRITICAL ROUNDING RULE:
//   MidpointRounding.AwayFromZero rounds 0.5 UP — this is the standard
//   accounting/financial rounding convention. Banker's rounding (ToEven) is
//   NOT used. Example: decimal.Round(2.345m, 2, AwayFromZero) = 2.35.
//
// DESIGN NOTES:
// - Pure calculation — zero database access, zero event publishing, zero I/O.
// - Stateless and thread-safe — safe for SINGLETON DI registration.
// - All monetary values use the 'decimal' type — NEVER double or float.
// - Tax computation is delegated to ITaxCalculationService (single responsibility).
// - Rounding is applied at the line-item and invoice aggregation level using
//   CurrencyInfo.DecimalDigits (default 2, matching most currencies like USD;
//   JPY uses 0). The DecimalDigits value originates from CurrencyType in the
//   source Definitions.cs lines 82-83.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using WebVellaErp.Invoicing.Models;

namespace WebVellaErp.Invoicing.Services;

/// <summary>
/// Defines the contract for line item and invoice total calculation operations.
/// All financial arithmetic uses <see cref="decimal"/> exclusively and applies
/// currency-aware rounding via <see cref="MidpointRounding.AwayFromZero"/>.
/// </summary>
public interface ILineItemCalculationService
{
    /// <summary>
    /// Computes the raw line total as <paramref name="quantity"/> × <paramref name="unitPrice"/>.
    /// No rounding is applied — intermediate precision is preserved until aggregation.
    /// </summary>
    /// <param name="quantity">The quantity of items on the line.</param>
    /// <param name="unitPrice">The unit price per item.</param>
    /// <returns>The raw line total (quantity × unitPrice).</returns>
    decimal CalculateLineTotal(decimal quantity, decimal unitPrice);

    /// <summary>
    /// Computes the tax amount for a line item by delegating to <see cref="ITaxCalculationService"/>.
    /// Returns the raw (unrounded) tax amount — the caller applies currency rounding.
    /// </summary>
    /// <param name="lineTotal">The pre-tax line total.</param>
    /// <param name="taxRate">The tax rate as a decimal fraction (e.g., 0.20 = 20%).</param>
    /// <returns>The raw tax amount (lineTotal × taxRate).</returns>
    decimal CalculateLineTax(decimal lineTotal, decimal taxRate);

    /// <summary>
    /// Computes the gross total for a line item (net + tax).
    /// </summary>
    /// <param name="lineTotal">The pre-tax line total.</param>
    /// <param name="taxAmount">The tax amount for the line.</param>
    /// <returns>The gross line total (lineTotal + taxAmount).</returns>
    decimal CalculateLineGrossTotal(decimal lineTotal, decimal taxAmount);

    /// <summary>
    /// Computes and mutates <see cref="LineItem.LineTotal"/> on a single line item.
    /// Applies currency rounding with <see cref="MidpointRounding.AwayFromZero"/>
    /// using the specified <paramref name="decimalDigits"/>.
    /// </summary>
    /// <param name="lineItem">The line item to compute totals for. Must not be null.</param>
    /// <param name="decimalDigits">
    /// Number of decimal places for rounding (from CurrencyInfo.DecimalDigits).
    /// Default is 2, matching most currencies (USD, EUR). Use 0 for JPY.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lineItem"/> is null.</exception>
    void CalculateLineItemTotals(LineItem lineItem, int decimalDigits = 2);

    /// <summary>
    /// Iterates all line items on the invoice, computes per-line totals and tax,
    /// then aggregates into <see cref="Invoice.SubTotal"/>, <see cref="Invoice.TaxAmount"/>,
    /// and <see cref="Invoice.TotalAmount"/>. Uses <see cref="Invoice.Currency"/>
    /// for rounding precision (falls back to 2 decimal digits if Currency is null).
    /// </summary>
    /// <param name="invoice">The invoice to compute totals for. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="invoice"/> is null.</exception>
    void CalculateInvoiceTotals(Invoice invoice);

    /// <summary>
    /// Batch recalculation of all line items (used during invoice update when
    /// line items change). Calls <see cref="CalculateLineItemTotals"/> for each item.
    /// </summary>
    /// <param name="lineItems">The list of line items to recalculate. Must not be null.</param>
    /// <param name="decimalDigits">
    /// Number of decimal places for rounding (from CurrencyInfo.DecimalDigits). Default is 2.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lineItems"/> is null.</exception>
    void RecalculateLineItems(List<LineItem> lineItems, int decimalDigits = 2);
}

/// <summary>
/// Production implementation of <see cref="ILineItemCalculationService"/>.
/// This is a pure, stateless calculation service with no I/O or side effects.
/// Thread-safe — register as Singleton in DI.
/// </summary>
/// <remarks>
/// Currency rounding follows the exact pattern from the monolith's
/// <c>RecordManager.ExtractFieldValue()</c> for <c>CurrencyField</c>
/// (source RecordManager.cs line 1893):
/// <code>
/// decimal.Round(decimalValue,
///     ((CurrencyField)field).Currency.DecimalDigits,
///     MidpointRounding.AwayFromZero);
/// </code>
/// </remarks>
public class LineItemCalculationService : ILineItemCalculationService
{
    private readonly ITaxCalculationService _taxCalculationService;
    private readonly ILogger<LineItemCalculationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineItemCalculationService"/> class.
    /// </summary>
    /// <param name="taxCalculationService">
    /// The tax calculation service used for per-line tax computation.
    /// Must not be null.
    /// </param>
    /// <param name="logger">
    /// The logger for structured JSON logging with correlation-ID propagation.
    /// Must not be null.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="taxCalculationService"/> or <paramref name="logger"/> is null.
    /// </exception>
    public LineItemCalculationService(
        ITaxCalculationService taxCalculationService,
        ILogger<LineItemCalculationService> logger)
    {
        _taxCalculationService = taxCalculationService
            ?? throw new ArgumentNullException(nameof(taxCalculationService));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns the raw product of quantity × unitPrice without rounding.
    /// Rounding is deferred to <see cref="CalculateLineItemTotals"/> or
    /// <see cref="CalculateInvoiceTotals"/> where the currency's
    /// <c>DecimalDigits</c> is known.
    /// </remarks>
    public decimal CalculateLineTotal(decimal quantity, decimal unitPrice)
    {
        // Pure multiplication — no rounding applied here.
        // Intermediate precision is preserved until the aggregation level
        // applies currency-specific rounding via DecimalDigits.
        return quantity * unitPrice;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <see cref="ITaxCalculationService.CalculateTax"/> which
    /// returns a raw (unrounded) tax amount. The caller is responsible for
    /// applying <c>decimal.Round(value, decimalDigits, MidpointRounding.AwayFromZero)</c>.
    /// </remarks>
    public decimal CalculateLineTax(decimal lineTotal, decimal taxRate)
    {
        // Delegate to ITaxCalculationService for single-responsibility tax computation.
        // TaxCalculationService.CalculateTax returns amount * taxRate as raw decimal.
        return _taxCalculationService.CalculateTax(lineTotal, taxRate);
    }

    /// <inheritdoc />
    public decimal CalculateLineGrossTotal(decimal lineTotal, decimal taxAmount)
    {
        // Gross = net line total + tax amount.
        return lineTotal + taxAmount;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Applies the CRITICAL rounding pattern from source RecordManager.cs line 1893:
    /// <c>decimal.Round(value, decimalDigits, MidpointRounding.AwayFromZero)</c>
    /// where <c>decimalDigits</c> corresponds to <c>CurrencyType.DecimalDigits</c>
    /// from Definitions.cs lines 82-83.
    /// </remarks>
    public void CalculateLineItemTotals(LineItem lineItem, int decimalDigits = 2)
    {
        if (lineItem is null)
        {
            throw new ArgumentNullException(nameof(lineItem));
        }

        // Compute line total = Quantity × UnitPrice, rounded to the currency's
        // decimal precision using MidpointRounding.AwayFromZero (standard
        // accounting convention — rounds 0.5 UP, NOT banker's rounding).
        lineItem.LineTotal = decimal.Round(
            lineItem.Quantity * lineItem.UnitPrice,
            decimalDigits,
            MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This is the primary invoice calculation method. It performs three operations:
    /// </para>
    /// <list type="number">
    ///   <item>Iterates all line items and calculates per-line totals (LineTotal).</item>
    ///   <item>Computes per-line tax via <see cref="ITaxCalculationService.CalculateTax"/>.</item>
    ///   <item>Aggregates SubTotal, TaxAmount, and TotalAmount with currency rounding.</item>
    /// </list>
    /// <para>
    /// Currency precision is determined by <c>invoice.Currency.DecimalDigits</c>
    /// (defaults to 2 when Currency is null). This preserves the pattern from
    /// source Definitions.cs lines 82-83 where <c>CurrencyType.DecimalDigits</c>
    /// drives rounding precision.
    /// </para>
    /// </remarks>
    public void CalculateInvoiceTotals(Invoice invoice)
    {
        if (invoice is null)
        {
            throw new ArgumentNullException(nameof(invoice));
        }

        // Determine the rounding precision from the invoice's currency.
        // CurrencyInfo.DecimalDigits defaults to 2 in the model constructor,
        // matching most world currencies (USD, EUR, GBP). JPY would use 0.
        // When Currency is null (e.g., draft invoice without currency set),
        // fall back to 2 as the safe default.
        int decimalDigits = invoice.Currency?.DecimalDigits ?? 2;

        // Accumulators for running totals — use decimal for full precision.
        decimal subTotal = 0m;
        decimal totalTax = 0m;

        // Safely handle null or empty line items collection.
        if (invoice.LineItems is not null)
        {
            foreach (var lineItem in invoice.LineItems)
            {
                // Step 1: Compute line total = Quantity × UnitPrice, rounded
                // to the currency's decimal precision.
                // CRITICAL: decimal.Round(value, decimalDigits, MidpointRounding.AwayFromZero)
                // exactly matches the monolith pattern at RecordManager.cs line 1893.
                lineItem.LineTotal = decimal.Round(
                    lineItem.Quantity * lineItem.UnitPrice,
                    decimalDigits,
                    MidpointRounding.AwayFromZero);

                // Step 2: Compute tax for this line item via TaxCalculationService.
                // TaxCalculationService.CalculateTax returns raw (unrounded) value.
                decimal lineTax = _taxCalculationService.CalculateTax(
                    lineItem.LineTotal,
                    lineItem.TaxRate);

                // Step 3: Round the tax amount to the same currency precision.
                lineTax = decimal.Round(
                    lineTax,
                    decimalDigits,
                    MidpointRounding.AwayFromZero);

                // Step 4: Accumulate into running totals.
                subTotal += lineItem.LineTotal;
                totalTax += lineTax;
            }
        }

        // Set invoice-level totals with final currency rounding.
        // Each aggregate is individually rounded to prevent floating-point
        // drift across many line items.
        invoice.SubTotal = decimal.Round(
            subTotal,
            decimalDigits,
            MidpointRounding.AwayFromZero);

        invoice.TaxAmount = decimal.Round(
            totalTax,
            decimalDigits,
            MidpointRounding.AwayFromZero);

        invoice.TotalAmount = decimal.Round(
            invoice.SubTotal + invoice.TaxAmount,
            decimalDigits,
            MidpointRounding.AwayFromZero);

        _logger.LogDebug(
            "Calculated invoice totals: SubTotal={SubTotal}, TaxAmount={TaxAmount}, " +
            "TotalAmount={TotalAmount}, DecimalDigits={DecimalDigits}, LineItemCount={LineItemCount}",
            invoice.SubTotal,
            invoice.TaxAmount,
            invoice.TotalAmount,
            decimalDigits,
            invoice.LineItems?.Count ?? 0);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Iterates the list and calls <see cref="CalculateLineItemTotals"/> for each
    /// item. Used during invoice update when line items are replaced or modified.
    /// </remarks>
    public void RecalculateLineItems(List<LineItem> lineItems, int decimalDigits = 2)
    {
        if (lineItems is null)
        {
            throw new ArgumentNullException(nameof(lineItems));
        }

        foreach (var lineItem in lineItems)
        {
            CalculateLineItemTotals(lineItem, decimalDigits);
        }
    }
}
