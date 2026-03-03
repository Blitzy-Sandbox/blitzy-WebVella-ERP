using System;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="DateTimeExtensions"/> static extension methods
    /// declared in the <c>System</c> namespace. These methods handle ERP-wide datetime conversions
    /// tied to <see cref="ErpSettings.TimeZoneName"/>.
    ///
    /// Covers all 6 extension methods:
    ///   - ClearKind (non-nullable + nullable overloads)
    ///   - ConvertToAppDate (non-nullable + nullable overloads)
    ///   - ConvertAppDateToUtc (non-nullable + nullable overloads)
    ///
    /// Each test method sets <see cref="ErpSettings.TimeZoneName"/> explicitly to ensure
    /// deterministic behavior. The class implements <see cref="IDisposable"/> to restore
    /// the original timezone name after each test, guaranteeing test isolation.
    ///
    /// Cross-platform timezone handling uses IANA timezone identifiers (e.g., "America/New_York")
    /// which are supported on all platforms with .NET 6+ via ICU.
    /// </summary>
    public class DateTimeExtensionsTests : IDisposable
    {
        /// <summary>
        /// IANA timezone identifier for Eastern Time (UTC-5 standard / UTC-4 daylight).
        /// Used as the default app timezone for most tests. Works on Linux, macOS, and
        /// Windows (.NET 6+ supports IANA IDs on all platforms).
        /// </summary>
        private const string TestTimeZoneId = "America/New_York";

        /// <summary>
        /// Stores the original <see cref="ErpSettings.TimeZoneName"/> value before each test
        /// so it can be restored in <see cref="Dispose"/> for test isolation.
        /// </summary>
        private readonly string _originalTimeZoneName;

        /// <summary>
        /// Constructor runs before each test method (xUnit creates a new instance per test).
        /// Saves the current timezone setting and sets a known test timezone.
        /// </summary>
        public DateTimeExtensionsTests()
        {
            _originalTimeZoneName = ErpSettings.TimeZoneName;
            ErpSettings.TimeZoneName = TestTimeZoneId;
        }

        /// <summary>
        /// Restores the original <see cref="ErpSettings.TimeZoneName"/> after each test
        /// to prevent cross-test contamination.
        /// </summary>
        public void Dispose()
        {
            ErpSettings.TimeZoneName = _originalTimeZoneName;
        }

        /// <summary>
        /// Returns a timezone ID that is guaranteed to differ from the machine's local timezone.
        /// This is necessary for testing the special code path in <c>ConvertAppDateToUtc</c>
        /// that handles <c>DateTimeKind.Local</c> when the app timezone differs from local.
        /// </summary>
        private static string GetNonLocalTimeZoneId()
        {
            // Try several IANA timezone IDs — pick the first one that differs from local
            string[] candidates = { "America/New_York", "Asia/Tokyo", "Europe/London", "Australia/Sydney" };
            foreach (var tzId in candidates)
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                    if (!tz.Equals(TimeZoneInfo.Local))
                        return tzId;
                }
                catch
                {
                    // Timezone not available on this platform — try next candidate
                    continue;
                }
            }

            // Ultimate fallback: if all candidates match local, use UTC or a different one
            return TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero ? "America/New_York" : "UTC";
        }

        // =====================================================================
        // ClearKind Tests — Non-Nullable Overload
        // =====================================================================

        /// <summary>
        /// Verifies that ClearKind on a non-nullable DateTime with DateTimeKind.Utc
        /// produces a result with DateTimeKind.Unspecified.
        /// Exercises the non-nullable → nullable delegation path (line 10) and
        /// the new DateTime(ticks, Unspecified) creation (line 42).
        /// </summary>
        [Fact]
        public void ClearKind_NonNullable_StripsKindToUnspecified()
        {
            // Arrange
            var input = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

            // Act
            var result = input.ClearKind();

            // Assert
            result.Kind.Should().Be(DateTimeKind.Unspecified);
        }

        /// <summary>
        /// Verifies that ClearKind preserves the exact tick value of the input DateTime.
        /// The tick count represents the date/time independent of Kind, so clearing Kind
        /// must not alter the numeric value.
        /// </summary>
        [Fact]
        public void ClearKind_NonNullable_PreservesTicks()
        {
            // Arrange
            var input = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

            // Act
            var result = input.ClearKind();

            // Assert
            result.Ticks.Should().Be(input.Ticks);
        }

        /// <summary>
        /// Verifies that ClearKind strips DateTimeKind.Local to Unspecified.
        /// Tests the Local kind scenario which is distinct from Utc in
        /// downstream conversion methods.
        /// </summary>
        [Fact]
        public void ClearKind_NonNullable_LocalKind_StrippedToUnspecified()
        {
            // Arrange
            var input = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Local);

            // Act
            var result = input.ClearKind();

            // Assert
            result.Kind.Should().Be(DateTimeKind.Unspecified);
            result.Ticks.Should().Be(input.Ticks);
        }

        /// <summary>
        /// Verifies that ClearKind on an already-Unspecified DateTime returns
        /// the same Kind (Unspecified) and preserves ticks. This is a no-op scenario
        /// that should still produce a valid result.
        /// </summary>
        [Fact]
        public void ClearKind_NonNullable_UnspecifiedKind_RemainsUnspecified()
        {
            // Arrange
            var input = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Unspecified);

            // Act
            var result = input.ClearKind();

            // Assert
            result.Kind.Should().Be(DateTimeKind.Unspecified);
            result.Ticks.Should().Be(input.Ticks);
        }

        // =====================================================================
        // ClearKind Tests — Nullable Overload
        // =====================================================================

        /// <summary>
        /// Verifies that ClearKind returns null when the input is a null DateTime?.
        /// Exercises the null guard branch (line 39-40 in dest).
        /// </summary>
        [Fact]
        public void ClearKind_Nullable_Null_ReturnsNull()
        {
            // Arrange
            DateTime? input = null;

            // Act
            var result = input.ClearKind();

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that ClearKind on a non-null nullable DateTime strips the Kind
        /// to Unspecified while preserving the tick value.
        /// Exercises the happy path of the nullable overload (line 42).
        /// </summary>
        [Fact]
        public void ClearKind_Nullable_WithValue_StripsKind()
        {
            // Arrange
            DateTime? input = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);

            // Act
            var result = input.ClearKind();

            // Assert
            result.Should().NotBeNull();
            result.Should().HaveValue();
            result.Value.Kind.Should().Be(DateTimeKind.Unspecified);
            result.Value.Ticks.Should().Be(input.Value.Ticks);
        }

        // =====================================================================
        // ConvertToAppDate Tests
        // =====================================================================

        /// <summary>
        /// Verifies that ConvertToAppDate returns null for a null DateTime? input.
        /// Exercises the null guard (line 66-67 in dest).
        /// </summary>
        [Fact]
        public void ConvertToAppDate_Nullable_Null_ReturnsNull()
        {
            // Arrange
            DateTime? input = null;

            // Act
            var result = input.ConvertToAppDate();

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that ConvertToAppDate returns the input unchanged when its Kind is
        /// DateTimeKind.Unspecified. Per the source code comment: "If unspecified assume
        /// it is already in app TZ" (line 69-71 in dest).
        /// </summary>
        [Fact]
        public void ConvertToAppDate_UnspecifiedKind_ReturnsAsIs()
        {
            // Arrange
            var input = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Unspecified);
            DateTime? nullableInput = input;

            // Act
            var result = nullableInput.ConvertToAppDate();

            // Assert — returned value should be identical to input (same ticks and kind)
            result.Should().NotBeNull();
            result.Value.Should().Be(input);
            result.Value.Kind.Should().Be(DateTimeKind.Unspecified);
            result.Value.Ticks.Should().Be(input.Ticks);
        }

        /// <summary>
        /// Verifies that ConvertToAppDate correctly converts a UTC DateTime to the
        /// application timezone (America/New_York = EST, UTC-5 in January).
        /// Uses an independently computed expected value via TimeZoneInfo.ConvertTimeFromUtc
        /// to validate the result.
        /// </summary>
        [Fact]
        public void ConvertToAppDate_UtcKind_ConvertsToAppTimeZone()
        {
            // Arrange — use January 15 to avoid DST transition ambiguity
            // UTC 15:00 → EST (UTC-5) should be 10:00
            var utcInput = new DateTime(2024, 1, 15, 15, 0, 0, DateTimeKind.Utc);
            var appTz = TimeZoneInfo.FindSystemTimeZoneById(TestTimeZoneId);
            var expected = TimeZoneInfo.ConvertTimeFromUtc(utcInput, appTz);

            // Act
            var result = utcInput.ConvertToAppDate();

            // Assert
            result.Ticks.Should().Be(expected.Ticks);
        }

        /// <summary>
        /// Verifies that ConvertToAppDate correctly converts a Local DateTime to the
        /// application timezone. The expected value is independently computed by first
        /// converting Local → UTC, then UTC → app timezone.
        /// </summary>
        [Fact]
        public void ConvertToAppDate_LocalKind_ConvertsToAppTimeZone()
        {
            // Arrange
            var localInput = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Local);

            // Compute expected independently: Local → UTC → appTz
            var utcEquivalent = localInput.ToUniversalTime();
            var appTz = TimeZoneInfo.FindSystemTimeZoneById(TestTimeZoneId);
            var expected = TimeZoneInfo.ConvertTimeFromUtc(utcEquivalent, appTz);

            // Act
            var result = localInput.ConvertToAppDate();

            // Assert
            result.Ticks.Should().Be(expected.Ticks);
        }

        /// <summary>
        /// Verifies that the non-nullable ConvertToAppDate overload delegates to the
        /// nullable overload and produces identical results. The non-nullable overload
        /// calls ((DateTime?)datetime).ConvertToAppDate().Value (line 52-53 in dest).
        /// </summary>
        [Fact]
        public void ConvertToAppDate_NonNullable_DelegatesToNullable()
        {
            // Arrange
            var input = new DateTime(2024, 1, 15, 15, 0, 0, DateTimeKind.Utc);
            DateTime? nullableInput = input;

            // Act
            var nonNullableResult = input.ConvertToAppDate();
            var nullableResult = nullableInput.ConvertToAppDate();

            // Assert — both overloads should produce identical tick values
            nonNullableResult.Ticks.Should().Be(nullableResult.Value.Ticks);
            nonNullableResult.Kind.Should().Be(nullableResult.Value.Kind);
        }

        // =====================================================================
        // ConvertAppDateToUtc Tests
        // =====================================================================

        /// <summary>
        /// Verifies that ConvertAppDateToUtc returns null for a null DateTime? input.
        /// Exercises the null guard (line 99-100 in dest).
        /// </summary>
        [Fact]
        public void ConvertAppDateToUtc_Nullable_Null_ReturnsNull()
        {
            // Arrange
            DateTime? input = null;

            // Act
            var result = input.ConvertAppDateToUtc();

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that ConvertAppDateToUtc returns the input unchanged when its Kind is
        /// DateTimeKind.Utc. Already-UTC values need no conversion (line 104-105 in dest).
        /// </summary>
        [Fact]
        public void ConvertAppDateToUtc_UtcKind_ReturnsAsIs()
        {
            // Arrange
            var utcInput = new DateTime(2024, 1, 15, 15, 0, 0, DateTimeKind.Utc);
            DateTime? nullableInput = utcInput;

            // Act
            var result = nullableInput.ConvertAppDateToUtc();

            // Assert — returned value should be identical to input
            result.Should().NotBeNull();
            result.Value.Should().Be(utcInput);
            result.Value.Kind.Should().Be(DateTimeKind.Utc);
            result.Value.Ticks.Should().Be(utcInput.Ticks);
        }

        /// <summary>
        /// Verifies that ConvertAppDateToUtc converts a DateTimeKind.Unspecified value
        /// (treated as being in the app timezone) to UTC. Uses independently computed
        /// expected value via TimeZoneInfo.ConvertTimeToUtc.
        /// For January 15 with EST (UTC-5): 10:00 EST → 15:00 UTC.
        /// </summary>
        [Fact]
        public void ConvertAppDateToUtc_UnspecifiedKind_ConvertsFromAppTzToUtc()
        {
            // Arrange — 10:00 EST (Unspecified, treated as app TZ) → 15:00 UTC
            var appDate = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Unspecified);
            var appTz = TimeZoneInfo.FindSystemTimeZoneById(TestTimeZoneId);
            var expected = TimeZoneInfo.ConvertTimeToUtc(appDate, appTz);

            DateTime? nullableInput = appDate;

            // Act
            var result = nullableInput.ConvertAppDateToUtc();

            // Assert
            result.Should().NotBeNull();
            result.Value.Ticks.Should().Be(expected.Ticks);
            result.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        /// <summary>
        /// Verifies the special code path in ConvertAppDateToUtc when the input has
        /// DateTimeKind.Local AND the app timezone differs from the machine's local timezone.
        /// This path (lines 106-110 in dest) first converts Local → app timezone, then
        /// app timezone → UTC. The net result should equal the input's UTC equivalent.
        ///
        /// The test dynamically selects a timezone that differs from the machine's local
        /// timezone to guarantee the special path is exercised.
        /// </summary>
        [Fact]
        public void ConvertAppDateToUtc_LocalKind_WhenAppTzDiffersFromLocal_ConvertsCorrectly()
        {
            // Arrange — ensure app timezone differs from machine local timezone
            string nonLocalTzId = GetNonLocalTimeZoneId();
            ErpSettings.TimeZoneName = nonLocalTzId;

            var localDate = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Local);

            // The special path converts Local → appTz → UTC, which is equivalent to Local → UTC
            var expectedUtc = localDate.ToUniversalTime();

            DateTime? nullableInput = localDate;

            // Act
            var result = nullableInput.ConvertAppDateToUtc();

            // Assert
            result.Should().NotBeNull();
            result.Value.Ticks.Should().Be(expectedUtc.Ticks);
        }

        /// <summary>
        /// Verifies that the non-nullable ConvertAppDateToUtc overload delegates to the
        /// nullable overload and produces identical results. The non-nullable overload
        /// calls ((DateTime?)datetime).ConvertAppDateToUtc().Value (line 84 in dest).
        /// </summary>
        [Fact]
        public void ConvertAppDateToUtc_NonNullable_DelegatesToNullable()
        {
            // Arrange — use Unspecified kind to exercise the main conversion path
            var input = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Unspecified);
            DateTime? nullableInput = input;

            // Act
            var nonNullResult = input.ConvertAppDateToUtc();
            var nullableResult = nullableInput.ConvertAppDateToUtc();

            // Assert — both overloads should produce identical results
            nonNullResult.Ticks.Should().Be(nullableResult.Value.Ticks);
            nonNullResult.Kind.Should().Be(nullableResult.Value.Kind);
        }

        /// <summary>
        /// Validates the round-trip identity: converting a UTC DateTime to the app timezone
        /// and then back to UTC should recover the original UTC value exactly.
        /// This exercises both ConvertToAppDate (UTC → app TZ) and ConvertAppDateToUtc
        /// (app TZ → UTC) in sequence, verifying they are inverse operations.
        /// </summary>
        [Fact]
        public void ConvertAppDateToUtc_RoundTrip_WithConvertToAppDate()
        {
            // Arrange — start with a known UTC timestamp
            var originalUtc = new DateTime(2024, 1, 15, 15, 0, 0, DateTimeKind.Utc);

            // Act — round-trip: UTC → app timezone → UTC
            var appDate = originalUtc.ConvertToAppDate();
            var roundTripped = appDate.ConvertAppDateToUtc();

            // Assert — ticks should be exactly preserved through the round-trip
            roundTripped.Ticks.Should().Be(originalUtc.Ticks);
        }

        // =====================================================================
        // Timezone Configuration Tests
        // =====================================================================

        /// <summary>
        /// Verifies that changing <see cref="ErpSettings.TimeZoneName"/> to different
        /// timezone IDs produces different conversion results for the same UTC input.
        /// Uses two timezones with different UTC offsets (America/New_York = UTC-5 in Jan,
        /// Asia/Tokyo = UTC+9 always) to ensure observably different results.
        /// </summary>
        [Fact]
        public void TimeZoneNameChange_AffectsConversionResults()
        {
            // Arrange — use a fixed UTC date/time
            var utcDate = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

            // Act — convert with America/New_York (EST = UTC-5 in January)
            ErpSettings.TimeZoneName = "America/New_York";
            var estResult = utcDate.ConvertToAppDate();

            // Act — convert with Asia/Tokyo (JST = UTC+9, no DST)
            ErpSettings.TimeZoneName = "Asia/Tokyo";
            var tokyoResult = utcDate.ConvertToAppDate();

            // Assert — EST 07:00 vs JST 21:00 — clearly different
            estResult.Ticks.Should().NotBe(tokyoResult.Ticks);

            // Verify actual values independently for additional confidence
            var estTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var tokyoTz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
            var expectedEst = TimeZoneInfo.ConvertTimeFromUtc(utcDate, estTz);
            var expectedTokyo = TimeZoneInfo.ConvertTimeFromUtc(utcDate, tokyoTz);

            estResult.Ticks.Should().Be(expectedEst.Ticks);
            tokyoResult.Ticks.Should().Be(expectedTokyo.Ticks);
        }

        /// <summary>
        /// Verifies that when <see cref="ErpSettings.TimeZoneName"/> is set to "UTC",
        /// converting a UTC DateTime to app date and back is an identity transformation
        /// (ticks are preserved both ways since app timezone IS UTC).
        /// </summary>
        [Fact]
        public void UtcTimeZone_IdentityConversion()
        {
            // Arrange — set app timezone to UTC for identity conversion
            ErpSettings.TimeZoneName = "UTC";
            var utcDate = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

            // Act — convert UTC → app date (which is also UTC)
            var appDate = utcDate.ConvertToAppDate();

            // Assert — ticks should be identical since app TZ = UTC
            appDate.Ticks.Should().Be(utcDate.Ticks);

            // Act — convert back: app date → UTC
            var backToUtc = appDate.ConvertAppDateToUtc();

            // Assert — round-trip preserves ticks
            backToUtc.Ticks.Should().Be(utcDate.Ticks);
        }
    }
}
