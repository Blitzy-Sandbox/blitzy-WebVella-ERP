using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="Helpers"/> static utility class.
    /// Validates currency catalog parsing, double-dollar-sign key renaming in EntityRecord,
    /// and image dimension extraction from byte arrays.
    /// </summary>
    public class HelpersTests
    {
        // ═══════════════════════════════════════════════════════════════════
        //  GetAllCurrency Tests
        //  Validates the embedded currency JSON blob parsing, list population,
        //  and sort-order behavior of Helpers.GetAllCurrency().
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void GetAllCurrency_ReturnsNonEmptyList()
        {
            // Act — retrieve the full currency catalog from the embedded JSON blob
            List<Currency> currencies = Helpers.GetAllCurrency();

            // Assert — the embedded JSON contains ~170+ currencies; list must not be empty
            currencies.Should().NotBeEmpty(
                "the embedded currency JSON blob contains a large catalog of world currencies");
        }

        [Fact]
        public void GetAllCurrency_ResultIsSortedByPriority()
        {
            // Act — retrieve the currency list (source returns OrderBy Priority ascending)
            List<Currency> currencies = Helpers.GetAllCurrency();

            // Assert — the list should be in ascending order by Priority property
            currencies.Should().BeInAscendingOrder(
                c => c.Priority,
                "GetAllCurrency() returns currencies ordered by Priority ascending");
        }

        [Fact]
        public void GetAllCurrency_ContainsUsd()
        {
            // Act
            List<Currency> currencies = Helpers.GetAllCurrency();

            // Assert — USD (United States Dollar) must be present in the catalog
            currencies.Should().Contain(
                c => c.IsoCode == "USD",
                "USD is a fundamental world currency that must be in the catalog");
        }

        [Fact]
        public void GetAllCurrency_ContainsEur()
        {
            // Act
            List<Currency> currencies = Helpers.GetAllCurrency();

            // Assert — EUR (Euro) must be present in the catalog
            currencies.Should().Contain(
                c => c.IsoCode == "EUR",
                "EUR is a fundamental world currency that must be in the catalog");
        }

        [Fact]
        public void GetAllCurrency_EachCurrencyHasIsoCode()
        {
            // Act
            List<Currency> currencies = Helpers.GetAllCurrency();

            // Assert — every currency in the list must have a non-null, non-empty IsoCode
            currencies.All(c => !string.IsNullOrEmpty(c.IsoCode))
                .Should().BeTrue(
                    "every currency entry must have a valid ISO code identifier");
        }

        [Fact]
        public void GetAllCurrency_EachCurrencyHasName()
        {
            // Act
            List<Currency> currencies = Helpers.GetAllCurrency();

            // Assert — every currency must have a non-null, non-empty Name
            currencies.All(c => !string.IsNullOrEmpty(c.Name))
                .Should().BeTrue(
                    "every currency entry must have a human-readable name");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  GetCurrency Tests
        //  Validates case-insensitive ISO code lookup and null/empty handling.
        // ═══════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData("USD")]
        [InlineData("EUR")]
        [InlineData("GBP")]
        public void GetCurrency_ValidCode_ReturnsCurrency(string code)
        {
            // Act — look up a known valid currency code
            Currency result = Helpers.GetCurrency(code);

            // Assert — result should be non-null with matching IsoCode
            result.Should().NotBeNull(
                "a valid ISO currency code should return a matching Currency object");
            result.IsoCode.Should().Be(code,
                "the returned Currency must have the exact ISO code that was requested");
        }

        [Fact]
        public void GetCurrency_CaseInsensitive()
        {
            // Act — look up the same currency using different casings
            Currency lower = Helpers.GetCurrency("usd");
            Currency upper = Helpers.GetCurrency("USD");
            Currency mixed = Helpers.GetCurrency("Usd");

            // Assert — all three lookups should return a non-null Currency with IsoCode "USD"
            lower.Should().NotBeNull("lowercase 'usd' should resolve to USD");
            upper.Should().NotBeNull("uppercase 'USD' should resolve to USD");
            mixed.Should().NotBeNull("mixed-case 'Usd' should resolve to USD");

            lower.IsoCode.Should().Be("USD");
            upper.IsoCode.Should().Be("USD");
            mixed.IsoCode.Should().Be("USD");

            // All lookups should yield the same currency name
            lower.Name.Should().Be(upper.Name,
                "case-insensitive lookup must return identical currency objects regardless of input casing");
        }

        [Fact]
        public void GetCurrency_InvalidCode_ReturnsNull()
        {
            // Act — look up a code that does not exist in any currency catalog
            Currency result = Helpers.GetCurrency("XYZ999");

            // Assert — no match found, FirstOrDefault returns null
            result.Should().BeNull(
                "a non-existent ISO code should return null from GetCurrency");
        }

        [Fact]
        public void GetCurrency_EmptyString_ReturnsNull()
        {
            // Act — look up an empty string (ToLowerInvariant on "" returns "",
            // which does not match any IsoCode, so FirstOrDefault returns null)
            Currency result = Helpers.GetCurrency("");

            // Assert — empty string yields no match
            result.Should().BeNull(
                "an empty currency code string should return null since no IsoCode matches empty");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  FixDoubleDollarSignProblem Tests
        //  Validates the key-renaming logic that converts "_$" prefixed keys
        //  to "$$" prefixed keys in EntityRecord objects. This is used to work
        //  around Angular's $http service stripping $$ prefixed properties.
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void FixDoubleDollarSignProblem_NoSpecialKeys_ReturnsSameRecord()
        {
            // Arrange — create an EntityRecord with only normal keys (no _$ prefix)
            var record = new EntityRecord();
            record["name"] = "Alice";
            record["age"] = 30;

            // Act
            EntityRecord result = Helpers.FixDoubleDollarSignProblem(record);

            // Assert — record should have the same keys and values, untouched
            result.Properties.Should().ContainKey("name");
            result.Properties.Should().ContainKey("age");
            result["name"].Should().Be("Alice");
            result["age"].Should().Be(30);
            result.Properties.Should().HaveCount(2,
                "no keys should be added or removed when there are no _$ prefixed keys");
        }

        [Fact]
        public void FixDoubleDollarSignProblem_UnderscoreDollarKey_RenamedToDoubleDollar()
        {
            // Arrange — create an EntityRecord with a single _$ prefixed key
            var record = new EntityRecord();
            record["_$foo"] = "bar";

            // Act
            EntityRecord result = Helpers.FixDoubleDollarSignProblem(record);

            // Assert — _$foo should be renamed to $$foo, old key removed
            result.Properties.Should().ContainKey("$$foo",
                "the _$ prefix should be replaced with $$ prefix");
            result.Properties.Should().NotContainKey("_$foo",
                "the original _$ prefixed key should be removed after renaming");
            result["$$foo"].Should().Be("bar",
                "the value should be preserved during the key rename");
        }

        [Fact]
        public void FixDoubleDollarSignProblem_MultipleSpecialKeys_AllRenamed()
        {
            // Arrange — create a record with multiple _$ prefixed keys
            var record = new EntityRecord();
            record["_$alpha"] = 1;
            record["_$beta"] = 2;
            record["_$gamma"] = 3;

            // Act
            EntityRecord result = Helpers.FixDoubleDollarSignProblem(record);

            // Assert — all _$ keys should be renamed to $$, all old keys removed
            result.Properties.Should().ContainKey("$$alpha");
            result.Properties.Should().ContainKey("$$beta");
            result.Properties.Should().ContainKey("$$gamma");
            result.Properties.Should().NotContainKey("_$alpha");
            result.Properties.Should().NotContainKey("_$beta");
            result.Properties.Should().NotContainKey("_$gamma");
            result.Properties.Should().HaveCount(3,
                "the total number of properties should remain the same after renaming");
        }

        [Fact]
        public void FixDoubleDollarSignProblem_MixedKeys_OnlySpecialRenamed()
        {
            // Arrange — mix of normal keys and _$ prefixed keys
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            record["name"] = "TestEntity";
            record["_$relation"] = "linked-entity";
            record["_$computed"] = 42;

            // Act
            EntityRecord result = Helpers.FixDoubleDollarSignProblem(record);

            // Assert — normal keys preserved, _$ keys renamed
            result.Properties.Should().ContainKey("id",
                "normal keys should remain unchanged");
            result.Properties.Should().ContainKey("name",
                "normal keys should remain unchanged");
            result.Properties.Should().ContainKey("$$relation",
                "_$relation should be renamed to $$relation");
            result.Properties.Should().ContainKey("$$computed",
                "_$computed should be renamed to $$computed");
            result.Properties.Should().NotContainKey("_$relation",
                "original _$ key should be removed");
            result.Properties.Should().NotContainKey("_$computed",
                "original _$ key should be removed");
            result.Properties.Should().HaveCount(4,
                "total property count should remain the same");
        }

        [Fact]
        public void FixDoubleDollarSignProblem_EmptyRecord_ReturnsEmptyRecord()
        {
            // Arrange — create an EntityRecord with no properties
            var record = new EntityRecord();

            // Act
            EntityRecord result = Helpers.FixDoubleDollarSignProblem(record);

            // Assert — empty record should remain empty
            result.Properties.Should().BeEmpty(
                "an empty record should remain empty after FixDoubleDollarSignProblem");
        }

        [Fact]
        public void FixDoubleDollarSignProblem_ValuePreserved()
        {
            // Arrange — test with various value types to ensure all are preserved
            var complexValue = new List<string> { "a", "b", "c" };
            var record = new EntityRecord();
            record["_$intField"] = 42;
            record["_$stringField"] = "hello world";
            record["_$decimalField"] = 3.14m;
            record["_$listField"] = complexValue;
            record["_$nullField"] = null;

            // Act
            EntityRecord result = Helpers.FixDoubleDollarSignProblem(record);

            // Assert — all values should be preserved exactly, with keys renamed
            result["$$intField"].Should().Be(42,
                "integer values must be preserved during key rename");
            result["$$stringField"].Should().Be("hello world",
                "string values must be preserved during key rename");
            result["$$decimalField"].Should().Be(3.14m,
                "decimal values must be preserved during key rename");
            result["$$listField"].Should().BeSameAs(complexValue,
                "reference-type values must preserve the same object reference during key rename");
            result["$$nullField"].Should().BeNull(
                "null values must remain null during key rename");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  GetImageDimension Tests
        //  Validates image dimension extraction using System.Drawing.Common.
        //  System.Drawing.Common 10.0.1 in .NET 10+ uses managed Windows GDI+
        //  and does NOT support non-Windows platforms (no libgdiplus fallback).
        //  The SUT itself (Helpers.GetImageDimension) calls
        //  System.Drawing.Image.FromStream, so both the SUT and test creation
        //  require GDI+ to be operational. Tests gracefully skip on platforms
        //  where GDI+ is unavailable.
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void GetImageDimension_ValidImage_ReturnsWidthAndHeight()
        {
            // Guard: System.Drawing.Common requires native Windows GDI+ in .NET 10+.
            // On non-Windows platforms, both the test image creation and the SUT
            // (Helpers.GetImageDimension → Image.FromStream) will fail.
            if (!IsGdiPlusAvailable())
            {
                return;
            }

            // Arrange — programmatically create a small valid bitmap image (10 x 20 pixels)
            byte[] imageBytes = CreateTestBitmapBytes(width: 10, height: 20);

            // Act
            EntityRecord result = Helpers.GetImageDimension(imageBytes);

            // Assert — the result should contain "width" and "height" keys with correct decimal values
            result.Properties.Should().ContainKey("width",
                "GetImageDimension must return an EntityRecord with a 'width' key");
            result.Properties.Should().ContainKey("height",
                "GetImageDimension must return an EntityRecord with a 'height' key");

            result["width"].Should().Be(10m,
                "the width should match the source image width as a decimal value");
            result["height"].Should().Be(20m,
                "the height should match the source image height as a decimal value");
        }

        [Fact]
        public void GetImageDimension_ReturnedValuesAreDecimal()
        {
            // Guard: System.Drawing.Common requires native Windows GDI+ in .NET 10+.
            if (!IsGdiPlusAvailable())
            {
                return;
            }

            // Arrange — create a small valid bitmap image (1 x 1 pixel)
            byte[] imageBytes = CreateTestBitmapBytes(width: 1, height: 1);

            // Act
            EntityRecord result = Helpers.GetImageDimension(imageBytes);

            // Assert — both width and height values should be of type decimal
            result["width"].Should().BeOfType<decimal>(
                "GetImageDimension casts image.Width to (decimal), so the stored value must be of type decimal");
            result["height"].Should().BeOfType<decimal>(
                "GetImageDimension casts image.Height to (decimal), so the stored value must be of type decimal");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Private Helper Methods
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Determines whether the managed GDI+ implementation is operational on
        /// the current platform. System.Drawing.Common 10.0.1 (shipped with
        /// .NET 10) uses the Windows GDI+ implementation exclusively and does
        /// NOT fall back to libgdiplus on non-Windows platforms.
        /// </summary>
        /// <returns>
        /// <c>true</c> on Windows (or any platform where GDI+ initializes
        /// successfully); <c>false</c> on Linux/macOS where the native library
        /// cannot be loaded.
        /// </returns>
        private static bool IsGdiPlusAvailable()
        {
            try
            {
#pragma warning disable CA1416 // Validate platform compatibility
                using var bitmap = new System.Drawing.Bitmap(1, 1);
#pragma warning restore CA1416
                return true;
            }
            catch
            {
                // TypeInitializationException or DllNotFoundException when GDI+ is absent
                return false;
            }
        }

        /// <summary>
        /// Creates a minimal valid BMP image byte array using System.Drawing.Bitmap.
        /// This produces a genuine bitmap that System.Drawing.Image.FromStream can parse,
        /// ensuring test isolation without relying on external image files.
        /// </summary>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <returns>A byte array containing a valid BMP image.</returns>
        private static byte[] CreateTestBitmapBytes(int width, int height)
        {
#pragma warning disable CA1416 // Validate platform compatibility
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            return ms.ToArray();
#pragma warning restore CA1416
        }
    }
}
