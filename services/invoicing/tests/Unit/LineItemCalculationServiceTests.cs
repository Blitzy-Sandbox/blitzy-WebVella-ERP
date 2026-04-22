// ---------------------------------------------------------------------------
// LineItemCalculationServiceTests.cs — xUnit Unit Tests for ILineItemCalculationService
// Bounded Context: Invoicing / Billing
// Namespace: WebVellaErp.Invoicing.Tests.Unit
// ---------------------------------------------------------------------------
// Comprehensive test suite validating:
//   Phase 2: CalculateLineTotal — basic multiplication, fractional quantities, zero handling
//   Phase 3: CalculateInvoiceTotals — single/multiple/empty line item aggregation
//   Phase 4: Currency rounding — USD(2), JPY(0), BHD(3), MidpointRounding.AwayFromZero
//   Phase 5: Null guard — ArgumentNullException for null invoice/lineItem/list
//   Phase 6: Delegation — verifies ITaxCalculationService.CalculateTax is called
//
// CRITICAL ROUNDING RULE (preserved from source RecordManager.cs line 1893):
//   decimal.Round(decimalValue, DecimalDigits, MidpointRounding.AwayFromZero)
//   - AwayFromZero rounds 0.5 UP (e.g., 2.345 → 2.35, NOT 2.34 banker's rounding)
//   - DecimalDigits from CurrencyType (Definitions.cs lines 82-83): USD=2, JPY=0, BHD=3
//
// ALL monetary values use decimal type — NEVER double or float (AAP §0.8.1).
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Invoicing.Models;
using WebVellaErp.Invoicing.Services;
using Xunit;

namespace WebVellaErp.Invoicing.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="LineItemCalculationService"/> covering line item
    /// total arithmetic, invoice-level aggregation, currency-aware rounding, null
    /// guards, and tax service delegation. All financial assertions use the
    /// <c>decimal</c> literal suffix (<c>m</c>) for precision.
    /// </summary>
    public class LineItemCalculationServiceTests
    {
        // -----------------------------------------------------------------
        // Mocked dependencies and System Under Test (SUT)
        // -----------------------------------------------------------------
        private readonly Mock<ITaxCalculationService> _taxCalculationServiceMock;
        private readonly Mock<ILogger<LineItemCalculationService>> _loggerMock;
        private readonly LineItemCalculationService _sut;

        /// <summary>
        /// Constructor initializes all mocks and creates the SUT instance.
        /// Runs before each test method (xUnit creates a new instance per test).
        /// </summary>
        public LineItemCalculationServiceTests()
        {
            _taxCalculationServiceMock = new Mock<ITaxCalculationService>();
            _loggerMock = new Mock<ILogger<LineItemCalculationService>>();
            _sut = new LineItemCalculationService(
                _taxCalculationServiceMock.Object,
                _loggerMock.Object);
        }

        // -----------------------------------------------------------------
        // Helper methods for test data creation
        // -----------------------------------------------------------------

        /// <summary>
        /// Creates a <see cref="LineItem"/> with configurable financial properties.
        /// Default values represent a standard single-unit item at 100 with 20% tax.
        /// </summary>
        /// <param name="quantity">Number of units (decimal for fractional support).</param>
        /// <param name="unitPrice">Price per unit.</param>
        /// <param name="taxRate">Tax rate as decimal fraction (0.20 = 20%).</param>
        /// <returns>A new <see cref="LineItem"/> with a unique Id.</returns>
        private static LineItem CreateTestLineItem(
            decimal quantity = 1m,
            decimal unitPrice = 100m,
            decimal taxRate = 0.20m)
        {
            return new LineItem
            {
                Id = Guid.NewGuid(),
                InvoiceId = Guid.Empty,
                Description = "Test Line Item",
                Quantity = quantity,
                UnitPrice = unitPrice,
                TaxRate = taxRate,
                LineTotal = 0m,
                SortOrder = 0
            };
        }

        /// <summary>
        /// Creates a test <see cref="Invoice"/> with the specified line items and
        /// currency precision. The <c>decimalDigits</c> parameter drives rounding
        /// behavior matching <c>CurrencyType.DecimalDigits</c> from the monolith's
        /// Definitions.cs lines 82-83.
        /// </summary>
        /// <param name="items">Line items to attach to the invoice.</param>
        /// <param name="decimalDigits">
        /// Number of decimal places for currency rounding (USD=2, JPY=0, BHD=3).
        /// </param>
        /// <returns>A new <see cref="Invoice"/> with a unique Id and configured currency.</returns>
        private static Invoice CreateTestInvoice(
            List<LineItem> items,
            int decimalDigits = 2)
        {
            return new Invoice
            {
                Id = Guid.NewGuid(),
                InvoiceNumber = "INV-TEST-001",
                CustomerId = Guid.NewGuid(),
                Status = InvoiceStatus.Draft,
                Currency = new CurrencyInfo
                {
                    Code = "USD",
                    DecimalDigits = decimalDigits
                },
                LineItems = items,
                SubTotal = 0m,
                TaxAmount = 0m,
                TotalAmount = 0m
            };
        }

        // =================================================================
        // Phase 2: CalculateLineTotal Tests
        // =================================================================
        // Tests the pure multiplication: quantity × unitPrice (no rounding).
        // -----------------------------------------------------------------

        /// <summary>
        /// Verifies basic integer multiplication: 2 × 50 = 100.
        /// </summary>
        [Fact]
        public void CalculateLineTotal_BasicMultiplication_ReturnsCorrectResult()
        {
            // Act
            var result = _sut.CalculateLineTotal(2m, 50m);

            // Assert
            result.Should().Be(100m);
        }

        /// <summary>
        /// Verifies fractional quantity multiplication: 1.5 × 100 = 150.
        /// Fractional quantities are common for hours, weight, partial units.
        /// </summary>
        [Fact]
        public void CalculateLineTotal_FractionalQuantity_ReturnsCorrectResult()
        {
            // Act
            var result = _sut.CalculateLineTotal(1.5m, 100m);

            // Assert
            result.Should().Be(150m);
        }

        /// <summary>
        /// Verifies zero quantity produces zero result: 0 × 100 = 0.
        /// Boundary condition for line items with zero quantity.
        /// </summary>
        [Fact]
        public void CalculateLineTotal_ZeroQuantity_ReturnsZero()
        {
            // Act
            var result = _sut.CalculateLineTotal(0m, 100m);

            // Assert
            result.Should().Be(0m);
        }

        /// <summary>
        /// Verifies zero unit price produces zero result: 5 × 0 = 0.
        /// Boundary condition for complimentary or zero-cost line items.
        /// </summary>
        [Fact]
        public void CalculateLineTotal_ZeroUnitPrice_ReturnsZero()
        {
            // Act
            var result = _sut.CalculateLineTotal(5m, 0m);

            // Assert
            result.Should().Be(0m);
        }

        /// <summary>
        /// Parameterized test exercising multiple quantity/price combinations.
        /// Uses double in InlineData (decimal is not a valid attribute parameter type)
        /// and casts to decimal in the test body for financial precision.
        /// </summary>
        /// <param name="quantity">Quantity (as double for InlineData compatibility).</param>
        /// <param name="unitPrice">Unit price (as double for InlineData compatibility).</param>
        /// <param name="expected">Expected result (as double for InlineData compatibility).</param>
        [Theory]
        [InlineData(1, 100, 100)]
        [InlineData(3, 25.50, 76.50)]
        [InlineData(10, 9.99, 99.90)]
        public void CalculateLineTotal_VariousInputs_ReturnsCorrectProduct(
            double quantity, double unitPrice, double expected)
        {
            // Act — cast from double to decimal for financial precision
            var result = _sut.CalculateLineTotal((decimal)quantity, (decimal)unitPrice);

            // Assert
            result.Should().Be((decimal)expected);
        }

        // =================================================================
        // Phase 3: CalculateInvoiceTotals Aggregation Tests
        // =================================================================
        // Tests invoice-level total computation: SubTotal, TaxAmount, TotalAmount.
        // Tax computation is delegated to ITaxCalculationService via mock.
        // -----------------------------------------------------------------

        /// <summary>
        /// Verifies single line item invoice calculation:
        /// Qty=2 × Price=50 = LineTotal 100, Tax 20% = 20, Total = 120.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_SingleLineItem_CorrectTotals()
        {
            // Arrange: Invoice with one line item (2 units at 50 each, 20% tax)
            var lineItem = CreateTestLineItem(quantity: 2m, unitPrice: 50m, taxRate: 0.20m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem });

            // After rounding: lineTotal = Round(2*50, 2, AwayFromZero) = 100
            // Mock tax service: CalculateTax(100, 0.20) → 20
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(100m, 0.20m))
                .Returns(20m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: SubTotal = 100, TaxAmount = 20, TotalAmount = 120
            invoice.SubTotal.Should().Be(100m);
            invoice.TaxAmount.Should().Be(20m);
            invoice.TotalAmount.Should().Be(120m);
        }

        /// <summary>
        /// Verifies multi-line item aggregation with different tax rates:
        /// Item 1: 1×100 @20% = 100 + 20 tax
        /// Item 2: 2×50  @10% = 100 + 10 tax
        /// Item 3: 5×10  @0%  =  50 +  0 tax
        /// SubTotal = 250, TaxAmount = 30, TotalAmount = 280.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_MultipleLineItems_AggregatesCorrectly()
        {
            // Arrange: Three line items with varying quantities, prices, and tax rates
            var item1 = CreateTestLineItem(quantity: 1m, unitPrice: 100m, taxRate: 0.20m);
            var item2 = CreateTestLineItem(quantity: 2m, unitPrice: 50m, taxRate: 0.10m);
            var item3 = CreateTestLineItem(quantity: 5m, unitPrice: 10m, taxRate: 0.00m);

            var invoice = CreateTestInvoice(new List<LineItem> { item1, item2, item3 });

            // Mock tax service for each line item's rounded lineTotal and taxRate
            // Item 1: lineTotal=100, tax rate=0.20 → tax=20
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(100m, 0.20m))
                .Returns(20m);
            // Item 2: lineTotal=100, tax rate=0.10 → tax=10
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(100m, 0.10m))
                .Returns(10m);
            // Item 3: lineTotal=50, tax rate=0.00 → tax=0
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(50m, 0.00m))
                .Returns(0m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: Aggregated totals across all three line items
            invoice.SubTotal.Should().Be(250m);     // 100 + 100 + 50
            invoice.TaxAmount.Should().Be(30m);      // 20 + 10 + 0
            invoice.TotalAmount.Should().Be(280m);   // 250 + 30
        }

        /// <summary>
        /// Verifies that an invoice with no line items produces zero totals.
        /// Edge case: empty invoice draft with no items yet.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_EmptyLineItems_ZeroTotals()
        {
            // Arrange: Invoice with empty line items list
            var invoice = CreateTestInvoice(new List<LineItem>());

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: All totals should be zero
            invoice.SubTotal.Should().Be(0m);
            invoice.TaxAmount.Should().Be(0m);
            invoice.TotalAmount.Should().Be(0m);
        }

        // =================================================================
        // Phase 4: Currency Rounding Tests
        // =================================================================
        // CRITICAL: Preserving the source rounding pattern from
        // RecordManager.cs line 1893:
        //   decimal.Round(decimalValue,
        //       ((CurrencyField)field).Currency.DecimalDigits,
        //       MidpointRounding.AwayFromZero);
        // And CurrencyType.DecimalDigits from Definitions.cs lines 82-83.
        // -----------------------------------------------------------------

        /// <summary>
        /// USD currency rounds to 2 decimal places.
        /// Qty=3, Price=33.33 → LineTotal=99.99, Tax=7.49925 → rounded to 7.50.
        /// Verifies: decimal.Round(7.49925, 2, AwayFromZero) = 7.50.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_USD_RoundsTo2DecimalPlaces()
        {
            // Arrange: USD invoice with DecimalDigits=2
            var lineItem = CreateTestLineItem(quantity: 3m, unitPrice: 33.33m, taxRate: 0.075m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem }, decimalDigits: 2);
            invoice.Currency = new CurrencyInfo { Code = "USD", DecimalDigits = 2 };

            // lineTotal = Round(3 × 33.33, 2, AwayFromZero) = Round(99.99, 2) = 99.99
            // Tax service returns raw (unrounded) tax: 99.99 × 0.075 = 7.49925
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(99.99m, 0.075m))
                .Returns(7.49925m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: Tax rounded with AwayFromZero → 7.50 (9 > 5, rounds up)
            invoice.SubTotal.Should().Be(99.99m);
            invoice.TaxAmount.Should().Be(7.50m);     // decimal.Round(7.49925, 2, AwayFromZero) = 7.50
            invoice.TotalAmount.Should().Be(107.49m);  // 99.99 + 7.50
        }

        /// <summary>
        /// JPY (Japanese Yen) rounds to 0 decimal places.
        /// Qty=1, Price=1234.56 → LineTotal=1235 (rounded to integer),
        /// Tax@10% = 123.5 → rounded to 124 (midpoint rounds away from zero).
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_JPY_RoundsTo0DecimalPlaces()
        {
            // Arrange: JPY invoice with DecimalDigits=0 (no fractional currency)
            var lineItem = CreateTestLineItem(quantity: 1m, unitPrice: 1234.56m, taxRate: 0.10m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem }, decimalDigits: 0);
            invoice.Currency = new CurrencyInfo { Code = "JPY", DecimalDigits = 0 };

            // lineTotal = Round(1234.56, 0, AwayFromZero) = 1235
            // Tax service returns raw: 1235 × 0.10 = 123.5
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(1235m, 0.10m))
                .Returns(123.5m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: All totals rounded to 0 decimal places
            invoice.SubTotal.Should().Be(1235m);
            invoice.TaxAmount.Should().Be(124m);     // Round(123.5, 0, AwayFromZero) = 124
            invoice.TotalAmount.Should().Be(1359m);   // 1235 + 124
        }

        /// <summary>
        /// BHD (Bahraini Dinar) rounds to 3 decimal places.
        /// Qty=1, Price=100.1234 → LineTotal=100.123 (rounded to 3 places),
        /// Tax@5% = 5.00615 → rounded to 5.006.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_BHD_RoundsTo3DecimalPlaces()
        {
            // Arrange: BHD invoice with DecimalDigits=3
            var lineItem = CreateTestLineItem(quantity: 1m, unitPrice: 100.1234m, taxRate: 0.05m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem }, decimalDigits: 3);
            invoice.Currency = new CurrencyInfo { Code = "BHD", DecimalDigits = 3 };

            // lineTotal = Round(100.1234, 3, AwayFromZero) = 100.123
            // Tax service returns raw: 100.123 × 0.05 = 5.00615
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(100.123m, 0.05m))
                .Returns(5.00615m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: All totals rounded to 3 decimal places
            invoice.SubTotal.Should().Be(100.123m);
            invoice.TaxAmount.Should().Be(5.006m);      // Round(5.00615, 3, AwayFromZero) = 5.006
            invoice.TotalAmount.Should().Be(105.129m);    // 100.123 + 5.006
        }

        /// <summary>
        /// CRITICAL: Verifies that MidpointRounding.AwayFromZero is used (NOT ToEven/banker's).
        /// decimal.Round(2.345, 2, AwayFromZero) = 2.35 (rounds UP at midpoint).
        /// decimal.Round(2.345, 2, ToEven) would give 2.34 (banker's rounding — WRONG).
        /// This test specifically validates the rounding pattern from RecordManager.cs line 1893.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_MidpointRoundsAwayFromZero()
        {
            // Arrange: Line item with unit price ending exactly at midpoint (.5)
            // when rounded to 2 decimal places: 2.345 → 2.35 (AwayFromZero) vs 2.34 (ToEven)
            var lineItem = CreateTestLineItem(quantity: 1m, unitPrice: 2.345m, taxRate: 0m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem });

            // lineTotal = Round(1 × 2.345, 2, AwayFromZero) = 2.35
            // Tax on 2.35 at 0% = 0
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(2.35m, 0m))
                .Returns(0m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: CRITICAL — SubTotal must be 2.35 (AwayFromZero), NOT 2.34 (ToEven)
            invoice.SubTotal.Should().Be(2.35m);
            invoice.TaxAmount.Should().Be(0m);
            invoice.TotalAmount.Should().Be(2.35m);
        }

        /// <summary>
        /// When Currency is null (e.g., draft invoice without currency set),
        /// the service defaults to 2 decimal places for rounding, matching
        /// the behavior of most world currencies (USD, EUR, GBP).
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_NullCurrency_DefaultsTo2DecimalPlaces()
        {
            // Arrange: Invoice with explicitly null Currency
            var lineItem = CreateTestLineItem(quantity: 1m, unitPrice: 33.333m, taxRate: 0.10m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem });
            invoice.Currency = null; // Force null currency — defaults to 2 decimal digits

            // lineTotal = Round(33.333, 2, AwayFromZero) = 33.33
            // Tax service returns raw: 33.33 × 0.10 = 3.333
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(33.33m, 0.10m))
                .Returns(3.333m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: Rounded to 2 decimal places (default when Currency is null)
            invoice.SubTotal.Should().Be(33.33m);
            invoice.TaxAmount.Should().Be(3.33m);      // Round(3.333, 2, AwayFromZero) = 3.33
            invoice.TotalAmount.Should().Be(36.66m);    // 33.33 + 3.33
        }

        // =================================================================
        // Phase 5: Null Guard Tests
        // =================================================================
        // Validates ArgumentNullException for null input parameters.
        // -----------------------------------------------------------------

        /// <summary>
        /// Verifies that passing a null invoice throws ArgumentNullException.
        /// Guards the entry point of the primary calculation method.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_NullInvoice_ThrowsArgumentNullException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateInvoiceTotals(null!))
                .Should().Throw<ArgumentNullException>();
        }

        /// <summary>
        /// Verifies that passing a null line item throws ArgumentNullException.
        /// Guards the per-item calculation method.
        /// </summary>
        [Fact]
        public void CalculateLineItemTotals_NullLineItem_ThrowsArgumentNullException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateLineItemTotals(null!))
                .Should().Throw<ArgumentNullException>();
        }

        /// <summary>
        /// Verifies that passing a null line items list throws ArgumentNullException.
        /// Guards the batch recalculation method.
        /// </summary>
        [Fact]
        public void RecalculateLineItems_NullList_ThrowsArgumentNullException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.RecalculateLineItems(null!))
                .Should().Throw<ArgumentNullException>();
        }

        // =================================================================
        // Phase 6: TaxCalculationService Delegation Tests
        // =================================================================
        // Verifies that tax computation is delegated to ITaxCalculationService.
        // -----------------------------------------------------------------

        /// <summary>
        /// Verifies that CalculateInvoiceTotals delegates tax computation to
        /// ITaxCalculationService.CalculateTax, called exactly once for a
        /// single-line-item invoice. This validates the single-responsibility
        /// principle: LineItemCalculationService does arithmetic and aggregation,
        /// TaxCalculationService handles tax computation.
        /// </summary>
        [Fact]
        public void CalculateInvoiceTotals_DelegatesTaxCalculationToTaxService()
        {
            // Arrange: Invoice with one line item
            var lineItem = CreateTestLineItem(quantity: 1m, unitPrice: 100m, taxRate: 0.20m);
            var invoice = CreateTestInvoice(new List<LineItem> { lineItem });

            // Setup flexible mock — accept any arguments and return a valid tax amount
            _taxCalculationServiceMock
                .Setup(t => t.CalculateTax(It.IsAny<decimal>(), It.IsAny<decimal>()))
                .Returns(20m);

            // Act
            _sut.CalculateInvoiceTotals(invoice);

            // Assert: Verify CalculateTax was called exactly once (one line item)
            _taxCalculationServiceMock.Verify(
                t => t.CalculateTax(It.IsAny<decimal>(), It.IsAny<decimal>()),
                Times.Once);
        }
    }
}
