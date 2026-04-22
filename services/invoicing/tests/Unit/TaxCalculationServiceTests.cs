// ---------------------------------------------------------------------------
// TaxCalculationServiceTests.cs
// Invoicing Bounded Context — Unit Tests for ITaxCalculationService
//
// Comprehensive xUnit unit tests for the TaxCalculationService, a pure
// stateless calculation service with zero external dependencies (only ILogger
// is mocked). Tests validate all 5 public methods across standard, boundary,
// and edge case inputs using the decimal type exclusively for financial
// precision per AAP §0.8.1.
//
// Tax rates follow the monolith's PercentField storage convention
// (RecordManager.cs lines 2022-2030): decimal fractions where 0.20 = 20%.
// Currency precision uses MidpointRounding.AwayFromZero (RecordManager.cs
// line 1893) — though rounding is the caller's responsibility, not this
// service's.
//
// Methods Under Test:
//   1. CalculateTax(amount, taxRate)          — amount * taxRate
//   2. CalculateTaxInclusive(gross, taxRate)  — gross - (gross / (1 + rate))
//   3. CalculateGrossAmount(net, taxRate)     — net * (1 + taxRate)
//   4. CalculateNetAmount(gross, taxRate)     — gross / (1 + taxRate)
//   5. IsValidTaxRate(taxRate)                — [0.0, 1.0] inclusive
// ---------------------------------------------------------------------------

using System;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Invoicing.Services;
using Xunit;

namespace WebVellaErp.Invoicing.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="TaxCalculationService"/> validating all 5 public
    /// methods of <see cref="ITaxCalculationService"/>. This is a pure calculation
    /// service — only <see cref="ILogger{TaxCalculationService}"/> is mocked.
    /// All monetary values use the <c>decimal</c> type exclusively (never
    /// <c>double</c> or <c>float</c>) per AAP §0.8.1.
    /// </summary>
    public class TaxCalculationServiceTests
    {
        private readonly TaxCalculationService _sut;

        /// <summary>
        /// Initializes the System Under Test with a mocked ILogger.
        /// TaxCalculationService is a pure calculation service — no repository,
        /// no event publisher, no SNS client mocks are needed.
        /// </summary>
        public TaxCalculationServiceTests()
        {
            var loggerMock = new Mock<ILogger<TaxCalculationService>>();
            _sut = new TaxCalculationService(loggerMock.Object);
        }

        // =====================================================================
        // CalculateTax Tests — amount * taxRate
        // =====================================================================

        /// <summary>
        /// Standard rate: 100 × 0.20 = 20 (20% tax on 100).
        /// Primary test case verifying the core tax calculation formula.
        /// </summary>
        [Fact]
        public void CalculateTax_StandardRate_ReturnsCorrectTax()
        {
            // Act
            var result = _sut.CalculateTax(100m, 0.20m);

            // Assert
            result.Should().Be(20m);
        }

        /// <summary>
        /// Zero tax rate: 100 × 0 = 0 (tax-exempt scenario).
        /// </summary>
        [Fact]
        public void CalculateTax_ZeroTaxRate_ReturnsZero()
        {
            // Act
            var result = _sut.CalculateTax(100m, 0.0m);

            // Assert
            result.Should().Be(0m);
        }

        /// <summary>
        /// Zero amount: 0 × 0.20 = 0 (no amount to tax).
        /// </summary>
        [Fact]
        public void CalculateTax_ZeroAmount_ReturnsZero()
        {
            // Act
            var result = _sut.CalculateTax(0m, 0.20m);

            // Assert
            result.Should().Be(0m);
        }

        /// <summary>
        /// Small rate: 1000 × 0.001 = 1 (0.1% tax on 1000).
        /// Verifies correct behavior with small fractional rates.
        /// </summary>
        [Fact]
        public void CalculateTax_SmallRate_ReturnsCorrectTax()
        {
            // Act
            var result = _sut.CalculateTax(1000m, 0.001m);

            // Assert
            result.Should().Be(1m);
        }

        /// <summary>
        /// Large amount: 1,000,000 × 0.25 = 250,000 (no overflow with decimal).
        /// Validates that decimal type handles large monetary values without overflow.
        /// </summary>
        [Fact]
        public void CalculateTax_LargeAmount_ReturnsCorrectTax()
        {
            // Act
            var result = _sut.CalculateTax(1_000_000m, 0.25m);

            // Assert
            result.Should().Be(250_000m);
        }

        /// <summary>
        /// Full rate (100% tax): 100 × 1.0 = 100 (tax equals the full amount).
        /// Boundary condition — maximum valid tax rate.
        /// </summary>
        [Fact]
        public void CalculateTax_FullRate_ReturnsAmountItself()
        {
            // Act
            var result = _sut.CalculateTax(100m, 1.0m);

            // Assert
            result.Should().Be(100m);
        }

        /// <summary>
        /// Fractional result: 33.33 × 0.075 = 2.49975.
        /// Validates raw decimal precision — rounding is the caller's
        /// responsibility per the service's design contract (see
        /// RecordManager.cs line 1893 MidpointRounding.AwayFromZero pattern).
        /// </summary>
        [Fact]
        public void CalculateTax_FractionalResults_MaintainsPrecision()
        {
            // Act
            var result = _sut.CalculateTax(33.33m, 0.075m);

            // Assert
            result.Should().Be(2.49975m);
        }

        /// <summary>
        /// Negative amount guard: throws <see cref="ArgumentOutOfRangeException"/>
        /// with parameter name "amount" for negative input.
        /// </summary>
        [Fact]
        public void CalculateTax_NegativeAmount_ThrowsArgumentOutOfRangeException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateTax(-100m, 0.20m))
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        /// <summary>
        /// Negative tax rate guard: throws <see cref="ArgumentOutOfRangeException"/>
        /// with parameter name "taxRate" for negative rate input.
        /// </summary>
        [Fact]
        public void CalculateTax_NegativeTaxRate_ThrowsArgumentOutOfRangeException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateTax(100m, -0.05m))
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        // =====================================================================
        // CalculateTaxInclusive Tests — gross - (gross / (1 + rate))
        // =====================================================================

        /// <summary>
        /// Standard inclusive: 120 - (120 / 1.20) = 120 - 100 = 20.
        /// Reverse-calculates the tax from a tax-inclusive (gross) amount.
        /// </summary>
        [Fact]
        public void CalculateTaxInclusive_StandardRate_ReturnsCorrectTax()
        {
            // Act
            var result = _sut.CalculateTaxInclusive(120m, 0.20m);

            // Assert
            result.Should().Be(20m);
        }

        /// <summary>
        /// Zero rate inclusive: 100 - (100 / 1.0) = 100 - 100 = 0.
        /// No tax extracted when the rate is zero.
        /// </summary>
        [Fact]
        public void CalculateTaxInclusive_ZeroRate_ReturnsZero()
        {
            // Act
            var result = _sut.CalculateTaxInclusive(100m, 0.0m);

            // Assert
            result.Should().Be(0m);
        }

        /// <summary>
        /// Negative gross amount guard: throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        [Fact]
        public void CalculateTaxInclusive_NegativeAmount_ThrowsException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateTaxInclusive(-120m, 0.20m))
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        // =====================================================================
        // CalculateGrossAmount Tests — net * (1 + rate)
        // =====================================================================

        /// <summary>
        /// Standard gross: 100 × (1 + 0.20) = 100 × 1.20 = 120.
        /// Computes the gross (tax-inclusive) amount from a net amount.
        /// </summary>
        [Fact]
        public void CalculateGrossAmount_StandardRate_ReturnsCorrectGross()
        {
            // Act
            var result = _sut.CalculateGrossAmount(100m, 0.20m);

            // Assert
            result.Should().Be(120m);
        }

        /// <summary>
        /// Zero rate gross: 100 × (1 + 0) = 100 × 1 = 100.
        /// No tax added when the rate is zero.
        /// </summary>
        [Fact]
        public void CalculateGrossAmount_ZeroRate_ReturnsSameAmount()
        {
            // Act
            var result = _sut.CalculateGrossAmount(100m, 0.0m);

            // Assert
            result.Should().Be(100m);
        }

        /// <summary>
        /// Negative net amount guard: throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        [Fact]
        public void CalculateGrossAmount_NegativeAmount_ThrowsException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateGrossAmount(-100m, 0.20m))
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        // =====================================================================
        // CalculateNetAmount Tests — gross / (1 + rate)
        // =====================================================================

        /// <summary>
        /// Standard net: 120 / (1 + 0.20) = 120 / 1.20 = 100.
        /// Reverse-calculates the net (pre-tax) amount from a gross amount.
        /// </summary>
        [Fact]
        public void CalculateNetAmount_StandardRate_ReturnsCorrectNet()
        {
            // Act
            var result = _sut.CalculateNetAmount(120m, 0.20m);

            // Assert
            result.Should().Be(100m);
        }

        /// <summary>
        /// Zero rate net: 100 / (1 + 0) = 100 / 1 = 100.
        /// Amount unchanged when no tax rate is applied.
        /// </summary>
        [Fact]
        public void CalculateNetAmount_ZeroRate_ReturnsSameAmount()
        {
            // Act
            var result = _sut.CalculateNetAmount(100m, 0.0m);

            // Assert
            result.Should().Be(100m);
        }

        /// <summary>
        /// Negative gross amount guard: throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        [Fact]
        public void CalculateNetAmount_NegativeAmount_ThrowsException()
        {
            // Act & Assert
            FluentActions.Invoking(() => _sut.CalculateNetAmount(-120m, 0.20m))
                .Should().Throw<ArgumentOutOfRangeException>();
        }

        // =====================================================================
        // IsValidTaxRate Boundary Tests — [0.0, 1.0] inclusive
        // =====================================================================

        /// <summary>
        /// Validates that tax rates within the valid range [0.0, 1.0] return true.
        /// Uses [Theory] with [InlineData] for boundary and representative values.
        /// InlineData uses double (C# attribute limitation) — cast to decimal in test.
        /// </summary>
        [Theory]
        [InlineData(0.0)]    // 0% tax — lower boundary (valid)
        [InlineData(0.5)]    // 50% tax — midpoint
        [InlineData(1.0)]    // 100% tax — upper boundary (valid)
        [InlineData(0.20)]   // 20% — standard VAT rate
        [InlineData(0.075)]  // 7.5% — fractional rate
        public void IsValidTaxRate_ValidRates_ReturnsTrue(double rateValue)
        {
            // Arrange — cast double to decimal (InlineData cannot use decimal literals)
            var rate = (decimal)rateValue;

            // Act
            var result = _sut.IsValidTaxRate(rate);

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Validates that tax rates outside the valid range [0.0, 1.0] return false.
        /// Uses [Theory] with [InlineData] for boundary-violating values.
        /// </summary>
        [Theory]
        [InlineData(-0.01)]  // Just below zero — invalid
        [InlineData(1.01)]   // Just above 100% — invalid
        [InlineData(-1.0)]   // Large negative — invalid
        [InlineData(2.0)]    // 200% — invalid
        public void IsValidTaxRate_InvalidRates_ReturnsFalse(double rateValue)
        {
            // Arrange — cast double to decimal (InlineData cannot use decimal literals)
            var rate = (decimal)rateValue;

            // Act
            var result = _sut.IsValidTaxRate(rate);

            // Assert
            result.Should().BeFalse();
        }

        // =====================================================================
        // Edge Case Tests — precision, overflow, inverse consistency
        // =====================================================================

        /// <summary>
        /// Very small rate: 10000 × 0.00001 = 0.1.
        /// Demonstrates that the decimal type correctly handles tiny fractional
        /// rates without floating-point precision loss (unlike double).
        /// </summary>
        [Fact]
        public void CalculateTax_VerySmallRate_MaintainsPrecision()
        {
            // Act
            var result = _sut.CalculateTax(10000m, 0.00001m);

            // Assert
            result.Should().Be(0.1m);
        }

        /// <summary>
        /// Very large amount: 999,999,999.99 × 0.25 = 249,999,999.9975.
        /// Validates that the decimal type handles large monetary values
        /// without overflow (decimal supports up to ±7.9 × 10^28).
        /// </summary>
        [Fact]
        public void CalculateTax_VeryLargeAmount_NoOverflow()
        {
            // Act
            var result = _sut.CalculateTax(999_999_999.99m, 0.25m);

            // Assert
            result.Should().Be(249_999_999.9975m);
        }

        /// <summary>
        /// Round-trip inverse: CalculateGrossAmount and CalculateNetAmount are
        /// mathematical inverses. Starting from net = 100 with rate = 0.20:
        ///   gross = CalculateGrossAmount(100, 0.20) = 120
        ///   netBack = CalculateNetAmount(120, 0.20) = 100
        /// The round-trip should be exact with decimal arithmetic.
        /// </summary>
        [Fact]
        public void CalculateGrossAmount_And_CalculateNetAmount_AreInverse()
        {
            // Arrange
            var net = 100m;
            var rate = 0.20m;

            // Act
            var gross = _sut.CalculateGrossAmount(net, rate);
            var netBack = _sut.CalculateNetAmount(gross, rate);

            // Assert
            netBack.Should().Be(net);
        }

        /// <summary>
        /// Consistency: CalculateTax (exclusive) and CalculateTaxInclusive
        /// should yield the same tax amount for corresponding inputs.
        ///   tax1 = CalculateTax(100, 0.20)            = 20
        ///   gross = CalculateGrossAmount(100, 0.20)    = 120
        ///   tax2 = CalculateTaxInclusive(120, 0.20)    = 20
        /// Both approaches must yield the same tax.
        /// </summary>
        [Fact]
        public void CalculateTax_And_CalculateTaxInclusive_Consistent()
        {
            // Arrange
            var net = 100m;
            var rate = 0.20m;

            // Act
            var tax1 = _sut.CalculateTax(net, rate);
            var gross = _sut.CalculateGrossAmount(net, rate);
            var tax2 = _sut.CalculateTaxInclusive(gross, rate);

            // Assert
            tax1.Should().Be(tax2);
        }
    }
}
