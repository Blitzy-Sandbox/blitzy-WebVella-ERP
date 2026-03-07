using System;
using System.Text;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="TextExtensions"/> static extension methods.
    /// Covers ToBase64 (Encoding extension for Base64 encoding with null guard),
    /// TryParseBase64 (Encoding extension for Base64 decoding with Try-pattern),
    /// and IsEmail (string extension for email validation using MailAddress with exact equality check).
    /// Targets 100% code coverage of the 54-line source class.
    /// </summary>
    public class TextExtensionsTests
    {
        // =====================================================================
        // ToBase64 Tests
        // =====================================================================

        /// <summary>
        /// Verifies that ToBase64 returns null when the input text is null.
        /// Exercises the null guard branch (line 13-14 in source).
        /// </summary>
        [Fact]
        public void ToBase64_NullInput_ReturnsNull()
        {
            // Arrange
            string input = null;

            // Act
            var result = Encoding.UTF8.ToBase64(input);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that ToBase64 returns an empty string when given an empty string input.
        /// Empty byte array encodes to empty Base64 string.
        /// </summary>
        [Fact]
        public void ToBase64_EmptyString_ReturnsEmptyBase64()
        {
            // Arrange
            string input = "";

            // Act
            var result = Encoding.UTF8.ToBase64(input);

            // Assert
            result.Should().Be("");
        }

        /// <summary>
        /// Verifies that ToBase64 produces the correct well-known Base64 encoding for "Hello".
        /// UTF-8 bytes for "Hello" = [72, 101, 108, 108, 111] → Base64 = "SGVsbG8=".
        /// </summary>
        [Fact]
        public void ToBase64_ValidString_ReturnsBase64()
        {
            // Arrange
            string input = "Hello";

            // Act
            var result = Encoding.UTF8.ToBase64(input);

            // Assert
            result.Should().Be("SGVsbG8=");
        }

        /// <summary>
        /// Verifies that ToBase64 with UTF-8 encoding produces valid Base64 output
        /// that can be round-tripped through Convert.FromBase64String and decoded back
        /// to the original string.
        /// </summary>
        [Fact]
        public void ToBase64_WithUtf8Encoding_EncodesCorrectly()
        {
            // Arrange
            string input = "WebVella ERP Microservices";

            // Act
            var result = Encoding.UTF8.ToBase64(input);

            // Assert — verify the result is valid Base64 and decodes back to original
            var decodedBytes = Convert.FromBase64String(result);
            var decoded = Encoding.UTF8.GetString(decodedBytes);
            decoded.Should().Be(input);
        }

        /// <summary>
        /// Verifies that ToBase64 with ASCII encoding produces valid Base64 output
        /// for ASCII-only input strings.
        /// </summary>
        [Fact]
        public void ToBase64_WithAsciiEncoding_EncodesCorrectly()
        {
            // Arrange
            string input = "ASCII-safe text 12345";

            // Act
            var result = Encoding.ASCII.ToBase64(input);

            // Assert — verify the result is valid Base64 and decodes back to original
            var decodedBytes = Convert.FromBase64String(result);
            var decoded = Encoding.ASCII.GetString(decodedBytes);
            decoded.Should().Be(input);
        }

        /// <summary>
        /// Verifies that encoding with ToBase64 then decoding with TryParseBase64
        /// produces the original input string (round-trip fidelity).
        /// </summary>
        [Fact]
        public void ToBase64_RoundTrip_WithTryParseBase64()
        {
            // Arrange
            string original = "Round trip test value!";

            // Act — encode then decode
            var encoded = Encoding.UTF8.ToBase64(original);
            var success = Encoding.UTF8.TryParseBase64(encoded, out string decoded);

            // Assert
            success.Should().BeTrue();
            decoded.Should().Be(original);
        }

        /// <summary>
        /// Verifies that ToBase64 correctly encodes strings containing Unicode/special
        /// characters (e.g., "café" with accented 'é') when using UTF-8 encoding.
        /// </summary>
        [Fact]
        public void ToBase64_SpecialCharacters_EncodesCorrectly()
        {
            // Arrange — "café" contains the multi-byte UTF-8 character 'é' (U+00E9)
            string input = "café";

            // Act
            var result = Encoding.UTF8.ToBase64(input);

            // Assert — verify valid Base64 and round-trip
            result.Should().NotBeNullOrEmpty();
            var decodedBytes = Convert.FromBase64String(result);
            var decoded = Encoding.UTF8.GetString(decodedBytes);
            decoded.Should().Be(input);
        }

        // =====================================================================
        // TryParseBase64 Tests
        // =====================================================================

        /// <summary>
        /// Verifies that TryParseBase64 returns false and sets output to null
        /// when the input encoded text is null. Exercises the null guard branch
        /// (lines 22-26 in source).
        /// </summary>
        [Fact]
        public void TryParseBase64_NullInput_ReturnsFalse()
        {
            // Arrange
            string encodedText = null;

            // Act
            var result = Encoding.UTF8.TryParseBase64(encodedText, out string decodedText);

            // Assert
            result.Should().BeFalse();
            decodedText.Should().BeNull();
        }

        /// <summary>
        /// Verifies that TryParseBase64 returns true and correctly decodes a known
        /// valid Base64 string. "SGVsbG8=" is the Base64 encoding of "Hello" in UTF-8.
        /// </summary>
        [Fact]
        public void TryParseBase64_ValidBase64_ReturnsTrue()
        {
            // Arrange
            string encodedText = "SGVsbG8=";

            // Act
            var result = Encoding.UTF8.TryParseBase64(encodedText, out string decodedText);

            // Assert
            result.Should().BeTrue();
            decodedText.Should().Be("Hello");
        }

        /// <summary>
        /// Verifies that TryParseBase64 returns false and sets output to null when
        /// given an invalid Base64 string. Exercises the catch(Exception) branch
        /// (lines 34-38 in source).
        /// </summary>
        [Fact]
        public void TryParseBase64_InvalidBase64_ReturnsFalse()
        {
            // Arrange — this string contains characters invalid for Base64 (!)
            string encodedText = "not-valid-base64!!!";

            // Act
            var result = Encoding.UTF8.TryParseBase64(encodedText, out string decodedText);

            // Assert
            result.Should().BeFalse();
            decodedText.Should().BeNull();
        }

        /// <summary>
        /// Verifies that TryParseBase64 returns true with an empty decoded string
        /// when given an empty string input. An empty string is valid Base64
        /// representing zero bytes.
        /// </summary>
        [Fact]
        public void TryParseBase64_EmptyString_ReturnsTrueWithEmpty()
        {
            // Arrange
            string encodedText = "";

            // Act
            var result = Encoding.UTF8.TryParseBase64(encodedText, out string decodedText);

            // Assert
            result.Should().BeTrue();
            decodedText.Should().Be("");
        }

        /// <summary>
        /// Verifies end-to-end round-trip: encode "Test" with ToBase64, then decode
        /// with TryParseBase64, and confirm the output matches the original input.
        /// </summary>
        [Fact]
        public void TryParseBase64_OutputMatchesOriginal()
        {
            // Arrange
            string original = "Test";
            string encoded = Encoding.UTF8.ToBase64(original);

            // Act
            var success = Encoding.UTF8.TryParseBase64(encoded, out string decoded);

            // Assert
            success.Should().BeTrue();
            decoded.Should().Be(original);
        }

        // =====================================================================
        // IsEmail Tests
        // =====================================================================

        /// <summary>
        /// Verifies that a standard valid email address is recognized as valid.
        /// MailAddress("user@example.com").Address == "user@example.com" → true.
        /// </summary>
        [Fact]
        public void IsEmail_ValidEmail_ReturnsTrue()
        {
            // Arrange
            string email = "user@example.com";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a valid email with a subdomain is recognized as valid.
        /// </summary>
        [Fact]
        public void IsEmail_ValidEmailWithSubdomain_ReturnsTrue()
        {
            // Arrange
            string email = "user@mail.example.com";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a string without an '@' sign is rejected.
        /// MailAddress("notanemail") throws FormatException → returns false.
        /// </summary>
        [Fact]
        public void IsEmail_InvalidEmail_NoAtSign_ReturnsFalse()
        {
            // Arrange
            string email = "notanemail";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that an empty string is rejected. MailAddress("") throws
        /// ArgumentException → returns false.
        /// </summary>
        [Fact]
        public void IsEmail_InvalidEmail_Empty_ReturnsFalse()
        {
            // Arrange
            string email = "";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that null is rejected. Since IsEmail is an extension method,
        /// calling it on null is valid syntactically — MailAddress(null) throws
        /// ArgumentNullException → returns false.
        /// </summary>
        [Fact]
        public void IsEmail_InvalidEmail_Null_ReturnsFalse()
        {
            // Arrange — cast to string to invoke the extension method on null
            string email = null;

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that an email with display name format ("User Name &lt;user@example.com&gt;")
        /// is rejected. MailAddress parses the display name format successfully but
        /// addr.Address ("user@example.com") != input ("User Name &lt;user@example.com&gt;")
        /// → returns false. This tests the exact equality check on line 46 in source.
        /// </summary>
        [Fact]
        public void IsEmail_DisplayNameFormat_ReturnsFalse()
        {
            // Arrange — MailAddress parses this but Address != full input string
            string email = "User Name <user@example.com>";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that an email with plus addressing (subaddressing) is recognized
        /// as valid. "user+tag@example.com" is a valid RFC 5321 email address.
        /// </summary>
        [Fact]
        public void IsEmail_WithPlus_ReturnsTrue()
        {
            // Arrange
            string email = "user+tag@example.com";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that "@example.com" (missing local part) is rejected.
        /// MailAddress("@example.com") throws FormatException → returns false.
        /// </summary>
        [Fact]
        public void IsEmail_InvalidFormat_ReturnsFalse()
        {
            // Arrange
            string email = "@example.com";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that an email with multiple '@' signs is rejected.
        /// MailAddress("user@@example.com") throws FormatException → returns false.
        /// </summary>
        [Fact]
        public void IsEmail_MultipleAtSigns_ReturnsFalse()
        {
            // Arrange
            string email = "user@@example.com";

            // Act
            var result = email.IsEmail();

            // Assert
            result.Should().BeFalse();
        }
    }
}
