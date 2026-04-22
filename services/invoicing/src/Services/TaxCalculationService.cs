// ---------------------------------------------------------------------------
// TaxCalculationService.cs
// Invoicing Bounded Context — Tax Computation Service
//
// Implements per-line tax calculation with configurable tax rates for the
// Invoicing / Billing microservice. This is a pure, stateless calculation
// service that computes tax amounts based on a line total and a decimal tax
// rate. It encapsulates all tax computation logic in a single, testable
// service with no I/O dependencies.
//
// The monolith did not have a dedicated tax calculation service — tax
// computation was embedded within RecordManager.ExtractFieldValue() for
// PercentField (lines 2022-2030) and CurrencyField (lines 1882-1893).
// This service extracts and formalizes that logic for the invoicing domain.
//
// DESIGN NOTES:
//   - PURE calculation service: zero database, zero repository, zero events
//   - Stateless and thread-safe: register as SINGLETON in DI
//   - All calculations use decimal type (NEVER double or float)
//   - Rounding is NOT applied here — caller (LineItemCalculationService)
//     applies currency-specific rounding using CurrencyType.DecimalDigits
//     with MidpointRounding.AwayFromZero (RecordManager.cs line 1893 pattern)
//   - Tax rates are decimal fractions: 0.20 = 20%, 0.075 = 7.5%, 0.0 = 0%
//     matching the monolith's PercentField storage convention
// ---------------------------------------------------------------------------

using System;
using Microsoft.Extensions.Logging;

namespace WebVellaErp.Invoicing.Services
{
    /// <summary>
    /// Defines the contract for per-line tax computation operations.
    /// All tax rates are expressed as decimal fractions (e.g., 0.20 for 20%).
    /// All return values are raw (unrounded) — the caller is responsible for
    /// applying currency-specific rounding via
    /// <c>decimal.Round(value, decimalDigits, MidpointRounding.AwayFromZero)</c>.
    /// </summary>
    public interface ITaxCalculationService
    {
        /// <summary>
        /// Computes the tax amount for a given amount and tax rate.
        /// Formula: <c>amount * taxRate</c>.
        /// </summary>
        /// <param name="amount">The taxable amount (must be non-negative).</param>
        /// <param name="taxRate">
        /// The tax rate as a decimal fraction (e.g., 0.20 for 20%). Must be non-negative.
        /// </param>
        /// <returns>The raw tax amount (unrounded).</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="amount"/> or <paramref name="taxRate"/> is negative.
        /// </exception>
        decimal CalculateTax(decimal amount, decimal taxRate);

        /// <summary>
        /// Reverse-calculates the tax component from a tax-inclusive (gross) amount.
        /// Formula: <c>grossAmount - (grossAmount / (1 + taxRate))</c>.
        /// </summary>
        /// <param name="grossAmount">The tax-inclusive amount (must be non-negative).</param>
        /// <param name="taxRate">
        /// The tax rate as a decimal fraction. Must be non-negative and not equal to -1.
        /// </param>
        /// <returns>The raw tax portion of the gross amount (unrounded).</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="grossAmount"/> or <paramref name="taxRate"/> is negative.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="taxRate"/> equals -1 (would cause division by zero).
        /// </exception>
        decimal CalculateTaxInclusive(decimal grossAmount, decimal taxRate);

        /// <summary>
        /// Computes the gross amount (net + tax) from a net amount and tax rate.
        /// Formula: <c>netAmount * (1 + taxRate)</c>.
        /// </summary>
        /// <param name="netAmount">The net (pre-tax) amount (must be non-negative).</param>
        /// <param name="taxRate">
        /// The tax rate as a decimal fraction. Must be non-negative.
        /// </param>
        /// <returns>The raw gross amount (unrounded).</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="netAmount"/> or <paramref name="taxRate"/> is negative.
        /// </exception>
        decimal CalculateGrossAmount(decimal netAmount, decimal taxRate);

        /// <summary>
        /// Reverse-calculates the net amount from a gross (tax-inclusive) amount.
        /// Formula: <c>grossAmount / (1 + taxRate)</c>.
        /// </summary>
        /// <param name="grossAmount">The tax-inclusive amount (must be non-negative).</param>
        /// <param name="taxRate">
        /// The tax rate as a decimal fraction. Must be non-negative.
        /// </param>
        /// <returns>The raw net amount (unrounded).</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="grossAmount"/> or <paramref name="taxRate"/> is negative.
        /// </exception>
        /// <exception cref="DivideByZeroException">
        /// Thrown when <c>(1 + taxRate)</c> equals zero (defensive guard).
        /// </exception>
        decimal CalculateNetAmount(decimal grossAmount, decimal taxRate);

        /// <summary>
        /// Validates whether a tax rate falls within acceptable bounds.
        /// Valid range: 0.0 (0%) through 1.0 (100%) inclusive.
        /// </summary>
        /// <param name="taxRate">The tax rate to validate.</param>
        /// <returns>
        /// <c>true</c> if the tax rate is between 0.0 and 1.0 inclusive; otherwise <c>false</c>.
        /// </returns>
        bool IsValidTaxRate(decimal taxRate);
    }

    /// <summary>
    /// Pure, stateless tax calculation service for the Invoicing bounded context.
    /// <para>
    /// This service centralizes all tax computation logic, enabling independent
    /// evolution of tax rules (e.g., compound taxes, regional rates) without
    /// affecting line-item or invoice-level calculation services.
    /// </para>
    /// <para>
    /// All methods use the <c>decimal</c> type exclusively for financial precision
    /// (28-29 significant digits). Rounding is intentionally NOT performed here —
    /// the calling service (typically <c>LineItemCalculationService</c>) applies
    /// currency-specific rounding using
    /// <c>decimal.Round(value, CurrencyType.DecimalDigits, MidpointRounding.AwayFromZero)</c>,
    /// preserving the exact rounding behavior from the monolith's
    /// <c>RecordManager.ExtractFieldValue()</c> at line 1893.
    /// </para>
    /// <para>
    /// Registration: register as <b>Singleton</b> in DI — this service is
    /// completely stateless and thread-safe with zero allocations beyond return values.
    /// </para>
    /// </summary>
    public class TaxCalculationService : ITaxCalculationService
    {
        private readonly ILogger<TaxCalculationService> _logger;

        /// <summary>
        /// Initializes a new instance of <see cref="TaxCalculationService"/>.
        /// </summary>
        /// <param name="logger">
        /// Structured JSON logger for consistency with the Invoicing service's
        /// logging patterns across all service classes.
        /// </param>
        public TaxCalculationService(ILogger<TaxCalculationService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Derives from the monolith's <c>PercentField</c> handling in
        /// <c>RecordManager.ExtractFieldValue()</c> (lines 2022-2030), which
        /// stores tax rates as raw decimal values. The tax rate parameter follows
        /// the same convention: 0.20 represents 20%.
        /// </para>
        /// <para>
        /// The result is NOT rounded — the caller applies currency-specific
        /// rounding via <c>decimal.Round(tax, decimalDigits, MidpointRounding.AwayFromZero)</c>
        /// after calling this method, matching the <c>CurrencyField</c> rounding
        /// pattern at <c>RecordManager.cs</c> line 1893.
        /// </para>
        /// </remarks>
        public decimal CalculateTax(decimal amount, decimal taxRate)
        {
            if (amount < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(amount),
                    amount,
                    "Amount cannot be negative.");
            }

            if (taxRate < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(taxRate),
                    taxRate,
                    "Tax rate cannot be negative.");
            }

            // Core tax calculation: amount * rate
            // Example: 100m * 0.20m = 20m
            return amount * taxRate;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Reverse-calculates the tax component from a tax-inclusive amount.
        /// Useful for scenarios where the total already includes tax and the
        /// tax portion needs to be extracted.
        /// <para>
        /// Example: grossAmount = 120, taxRate = 0.20
        ///   → tax = 120 - (120 / 1.20) = 120 - 100 = 20
        /// </para>
        /// </remarks>
        public decimal CalculateTaxInclusive(decimal grossAmount, decimal taxRate)
        {
            if (grossAmount < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(grossAmount),
                    grossAmount,
                    "Gross amount cannot be negative.");
            }

            if (taxRate < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(taxRate),
                    taxRate,
                    "Tax rate cannot be negative.");
            }

            // Guard against division by zero: 1 + (-1) = 0
            if (taxRate == -1m)
            {
                throw new ArgumentException(
                    "Tax rate of -1 would cause division by zero in the inclusive tax calculation.",
                    nameof(taxRate));
            }

            // Reverse-calculate tax from a tax-inclusive amount:
            // tax = grossAmount - (grossAmount / (1 + taxRate))
            decimal divisor = 1m + taxRate;
            return grossAmount - (grossAmount / divisor);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Computes the gross amount by applying the tax rate to the net amount.
        /// <para>
        /// Example: netAmount = 100, taxRate = 0.20
        ///   → gross = 100 * 1.20 = 120
        /// </para>
        /// </remarks>
        public decimal CalculateGrossAmount(decimal netAmount, decimal taxRate)
        {
            if (netAmount < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(netAmount),
                    netAmount,
                    "Net amount cannot be negative.");
            }

            if (taxRate < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(taxRate),
                    taxRate,
                    "Tax rate cannot be negative.");
            }

            // Gross = net * (1 + rate)
            // Example: 100m * 1.20m = 120m
            return netAmount * (1m + taxRate);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Reverse-calculates the net (pre-tax) amount from a gross (tax-inclusive) amount.
        /// <para>
        /// Example: grossAmount = 120, taxRate = 0.20
        ///   → net = 120 / 1.20 = 100
        /// </para>
        /// </remarks>
        public decimal CalculateNetAmount(decimal grossAmount, decimal taxRate)
        {
            if (grossAmount < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(grossAmount),
                    grossAmount,
                    "Gross amount cannot be negative.");
            }

            if (taxRate < 0m)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(taxRate),
                    taxRate,
                    "Tax rate cannot be negative.");
            }

            // Defensive guard: (1 + taxRate) must not be zero
            decimal divisor = 1m + taxRate;
            if (divisor == 0m)
            {
                throw new DivideByZeroException(
                    "Cannot calculate net amount because (1 + taxRate) equals zero.");
            }

            // Net = gross / (1 + rate)
            // Example: 120m / 1.20m = 100m
            return grossAmount / divisor;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Tax rates are expressed as decimal fractions following the monolith's
        /// <c>PercentField</c> storage convention (RecordManager.cs lines 2022-2030):
        /// <list type="bullet">
        ///   <item>0.0 represents 0% (tax exempt)</item>
        ///   <item>0.075 represents 7.5%</item>
        ///   <item>0.20 represents 20% (standard VAT in many jurisdictions)</item>
        ///   <item>1.0 represents 100% (maximum valid rate)</item>
        /// </list>
        /// Rates outside the [0.0, 1.0] range are considered invalid.
        /// </remarks>
        public bool IsValidTaxRate(decimal taxRate)
        {
            // Valid range: 0% to 100% expressed as decimal fraction [0.0, 1.0]
            return taxRate >= 0m && taxRate <= 1m;
        }
    }
}
