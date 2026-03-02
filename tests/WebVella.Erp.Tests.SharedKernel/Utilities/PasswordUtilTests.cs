using System;
using System.Text.RegularExpressions;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="PasswordUtil"/> — an internal static MD5 hex
    /// digest helper used for password hashing within the SharedKernel library.
    ///
    /// <para>
    /// <b>InternalsVisibleTo Requirement:</b> The <c>GetMd5Hash</c> and <c>VerifyMd5Hash</c>
    /// methods are declared <c>internal</c> in <c>PasswordUtil</c>. The SharedKernel assembly
    /// must include <c>&lt;InternalsVisibleTo Include="WebVella.Erp.Tests.SharedKernel" /&gt;</c>
    /// in its <c>.csproj</c> file for these tests to compile successfully.
    /// </para>
    ///
    /// <para>
    /// <b>Thread Safety Note:</b> The source implementation uses a static <c>MD5</c> instance
    /// (<c>MD5.Create()</c>) which is not guaranteed to be thread-safe. This is preserved
    /// behavior from the original monolith. Tests are designed to run sequentially to avoid
    /// potential concurrent access issues with the shared MD5 instance.
    /// </para>
    /// </summary>
    public class PasswordUtilTests
    {
        #region GetMd5Hash Tests

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> produces a result
        /// consisting entirely of lowercase hexadecimal characters matching the
        /// pattern [0-9a-f]{32} for a valid input string.
        /// </summary>
        [Fact]
        public void GetMd5Hash_ValidInput_ReturnsLowercaseHex()
        {
            // Arrange
            string input = "password";

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — MD5 hex digest must be exactly 32 lowercase hex characters
            result.Should().MatchRegex("^[0-9a-f]{32}$",
                "MD5 hex digest should consist of exactly 32 lowercase hexadecimal characters");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> always produces a
        /// 32-character string for any valid (non-null, non-whitespace) input,
        /// since MD5 produces a 128-bit (16-byte) digest formatted as 32 hex chars.
        /// </summary>
        [Fact]
        public void GetMd5Hash_ValidInput_Returns32CharString()
        {
            // Arrange
            string input = "password";

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — MD5 always produces a 128-bit digest = 32 hex characters
            result.Should().HaveLength(32,
                "MD5 produces a 128-bit digest which is always represented as 32 hexadecimal characters");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> returns <see cref="string.Empty"/>
        /// when given an empty string, because <c>string.IsNullOrWhiteSpace("")</c> evaluates to true.
        /// </summary>
        [Fact]
        public void GetMd5Hash_EmptyString_ReturnsEmptyString()
        {
            // Arrange
            string input = string.Empty;

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — empty string triggers the IsNullOrWhiteSpace guard
            result.Should().BeEmpty(
                "empty string is treated as null/whitespace and should return string.Empty");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> returns <see cref="string.Empty"/>
        /// when given a null input, because <c>string.IsNullOrWhiteSpace(null)</c> evaluates to true.
        /// </summary>
        [Fact]
        public void GetMd5Hash_NullInput_ReturnsEmptyString()
        {
            // Arrange
            string input = null;

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — null triggers the IsNullOrWhiteSpace guard
            result.Should().BeEmpty(
                "null input is treated as null/whitespace and should return string.Empty");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> returns <see cref="string.Empty"/>
        /// when given a whitespace-only input string, because <c>string.IsNullOrWhiteSpace("   ")</c>
        /// evaluates to true.
        /// </summary>
        [Fact]
        public void GetMd5Hash_WhitespaceInput_ReturnsEmptyString()
        {
            // Arrange
            string input = "   ";

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — whitespace-only triggers the IsNullOrWhiteSpace guard
            result.Should().BeEmpty(
                "whitespace-only input is treated as null/whitespace and should return string.Empty");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> is deterministic — calling it
        /// multiple times with the same input always produces the same hash output.
        /// </summary>
        [Fact]
        public void GetMd5Hash_SameInput_SameOutput()
        {
            // Arrange
            string input = "test123";

            // Act
            string firstHash = PasswordUtil.GetMd5Hash(input);
            string secondHash = PasswordUtil.GetMd5Hash(input);

            // Assert — MD5 is a deterministic function; same input must yield same output
            firstHash.Should().Be(secondHash,
                "MD5 is a deterministic hash function; identical inputs must produce identical outputs");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> produces different outputs
        /// for different inputs, demonstrating the hash function's sensitivity to input changes.
        /// </summary>
        [Fact]
        public void GetMd5Hash_DifferentInput_DifferentOutput()
        {
            // Arrange
            string inputA = "abc";
            string inputB = "def";

            // Act
            string hashA = PasswordUtil.GetMd5Hash(inputA);
            string hashB = PasswordUtil.GetMd5Hash(inputB);

            // Assert — different inputs must produce different MD5 digests
            hashA.Should().NotBe(hashB,
                "different inputs should produce different MD5 hash values");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> correctly handles multi-byte
        /// UTF-8 characters by producing a valid 32-character hexadecimal hash. The implementation
        /// uses <c>Encoding.UTF8.GetBytes(input)</c>, so multi-byte characters like "é" (U+00E9,
        /// encoded as 0xC3 0xA9 in UTF-8) must be hashed correctly.
        /// </summary>
        [Fact]
        public void GetMd5Hash_UsesUtf8Encoding()
        {
            // Arrange — "café" contains the multi-byte UTF-8 character é (U+00E9)
            string input = "café";

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — must produce a valid 32-char lowercase hex hash even with UTF-8 multi-byte input
            result.Should().HaveLength(32,
                "UTF-8 encoded multi-byte input should still produce a valid 32-character MD5 hex digest");
            result.Should().MatchRegex("^[0-9a-f]{32}$",
                "hash of UTF-8 multi-byte input should be valid lowercase hexadecimal");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.GetMd5Hash"/> produces the well-known MD5 hash
        /// for the input "password". The MD5 digest of "password" (UTF-8 encoded) is the widely
        /// documented value <c>5f4dcc3b5aa765d61d8327deb882cf99</c>.
        /// </summary>
        [Fact]
        public void GetMd5Hash_KnownValue()
        {
            // Arrange — well-known MD5 hash of "password" (UTF-8)
            string input = "password";
            string expectedHash = "5f4dcc3b5aa765d61d8327deb882cf99";

            // Act
            string result = PasswordUtil.GetMd5Hash(input);

            // Assert — must match the independently computed MD5 digest
            result.Should().Be(expectedHash,
                "the MD5 hash of 'password' in UTF-8 is the well-known value 5f4dcc3b5aa765d61d8327deb882cf99");
        }

        #endregion

        #region VerifyMd5Hash Tests

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.VerifyMd5Hash"/> returns true when the
        /// provided hash matches the MD5 digest of the input string.
        /// </summary>
        [Fact]
        public void VerifyMd5Hash_CorrectHash_ReturnsTrue()
        {
            // Arrange — compute hash of "password", then verify with same input
            string input = "password";
            string hash = PasswordUtil.GetMd5Hash(input);

            // Act
            bool result = PasswordUtil.VerifyMd5Hash(input, hash);

            // Assert — verification against the correct hash must succeed
            result.Should().BeTrue(
                "verifying an input against its own MD5 hash should return true");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.VerifyMd5Hash"/> returns false when the
        /// provided hash does not match the MD5 digest of the input string.
        /// </summary>
        [Fact]
        public void VerifyMd5Hash_WrongHash_ReturnsFalse()
        {
            // Arrange — hash of "wrong" does not match hash of "password"
            string input = "password";
            string wrongHash = PasswordUtil.GetMd5Hash("wrong");

            // Act
            bool result = PasswordUtil.VerifyMd5Hash(input, wrongHash);

            // Assert — verification against a non-matching hash must fail
            result.Should().BeFalse(
                "verifying an input against a different input's MD5 hash should return false");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.VerifyMd5Hash"/> performs case-insensitive
        /// comparison using <see cref="StringComparer.OrdinalIgnoreCase"/>. The hash produced
        /// by <c>GetMd5Hash</c> is lowercase, but verification against an uppercase version
        /// must still succeed.
        /// </summary>
        [Fact]
        public void VerifyMd5Hash_CaseInsensitiveComparison()
        {
            // Arrange — get lowercase hash, then convert to uppercase for comparison
            string input = "password";
            string lowercaseHash = PasswordUtil.GetMd5Hash(input);
            string uppercaseHash = lowercaseHash.ToUpperInvariant();

            // Act — verify against the uppercase hash
            bool result = PasswordUtil.VerifyMd5Hash(input, uppercaseHash);

            // Assert — OrdinalIgnoreCase comparison means case should not matter
            result.Should().BeTrue(
                "VerifyMd5Hash uses StringComparer.OrdinalIgnoreCase, so case differences should not affect verification");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.VerifyMd5Hash"/> returns true when both
        /// the input is empty and the expected hash is empty. Since <c>GetMd5Hash("")</c>
        /// returns <see cref="string.Empty"/>, comparing empty with empty yields equality.
        /// </summary>
        [Fact]
        public void VerifyMd5Hash_EmptyInput_WithEmptyHash_ReturnsTrue()
        {
            // Arrange — both input and hash are empty
            string input = string.Empty;
            string hash = string.Empty;

            // Act
            bool result = PasswordUtil.VerifyMd5Hash(input, hash);

            // Assert — GetMd5Hash("") returns "", which equals "" via OrdinalIgnoreCase
            result.Should().BeTrue(
                "empty input produces empty hash, and comparing empty with empty should return true");
        }

        /// <summary>
        /// Verifies that <see cref="PasswordUtil.VerifyMd5Hash"/> returns false when the
        /// input is empty (producing an empty hash) but the expected hash is a non-empty string.
        /// </summary>
        [Fact]
        public void VerifyMd5Hash_EmptyInput_WithNonEmptyHash_ReturnsFalse()
        {
            // Arrange — empty input but a non-empty expected hash
            string input = string.Empty;
            string nonEmptyHash = "5f4dcc3b5aa765d61d8327deb882cf99";

            // Act
            bool result = PasswordUtil.VerifyMd5Hash(input, nonEmptyHash);

            // Assert — empty hash does not match a non-empty expected hash
            result.Should().BeFalse(
                "empty input produces empty hash, which should not match a non-empty expected hash");
        }

        /// <summary>
        /// Performs a complete round-trip test: generates an MD5 hash from an input string,
        /// then verifies the hash against the same input. This validates the integration
        /// between <c>GetMd5Hash</c> and <c>VerifyMd5Hash</c>.
        /// </summary>
        [Fact]
        public void VerifyMd5Hash_RoundTrip()
        {
            // Arrange — use a representative input and generate its hash
            string input = "microservices-refactoring-2025";
            string hash = PasswordUtil.GetMd5Hash(input);

            // Act — verify the hash against the original input
            bool result = PasswordUtil.VerifyMd5Hash(input, hash);

            // Assert — round-trip (hash then verify) must always succeed
            result.Should().BeTrue(
                "a round-trip of hashing an input and then verifying it against the same input must always succeed");
        }

        #endregion
    }
}
