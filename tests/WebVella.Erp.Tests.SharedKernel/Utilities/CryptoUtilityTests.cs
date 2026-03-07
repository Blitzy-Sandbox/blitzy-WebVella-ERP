using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="CryptoUtility"/>, validating all public
    /// symmetric encryption/decryption methods (AES/DES via SymmetricAlgorithm), four
    /// distinct MD5 hashing variants, and the CryptKey fallback property.
    /// </summary>
    public class CryptoUtilityTests
    {
        /// <summary>
        /// The hard-coded default encryption key used by CryptoUtility when
        /// ErpSettings.EncryptionKey is null or empty.
        /// </summary>
        private const string DefaultCryptKey = "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658";

        /// <summary>
        /// Resets the cached static <c>cryptKey</c> field in CryptoUtility via reflection,
        /// forcing CryptKey to re-evaluate ErpSettings.EncryptionKey on next access.
        /// This is necessary because CryptoUtility caches the key on first access.
        /// </summary>
        private static void ResetCryptKeyCache()
        {
            var field = typeof(CryptoUtility).GetField(
                "cryptKey",
                BindingFlags.NonPublic | BindingFlags.Static);
            field?.SetValue(null, null);
        }

        #region EncryptText / DecryptText Round-Trip Tests

        /// <summary>
        /// Verifies that encrypting then decrypting with AES returns the original plaintext.
        /// Uses the default CryptKey (no explicit key parameter).
        /// </summary>
        [Fact]
        public void EncryptText_DecryptText_RoundTrip_WithAes()
        {
            // Arrange
            const string plainText = "Hello World";

            // Act — need separate algorithm instances since EncryptText modifies Key/IV
            string encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptText(plainText, aesEncrypt);
            }

            string decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptText(encrypted, aesDecrypt);
            }

            // Assert
            decrypted.Should().Be(plainText);
        }

        /// <summary>
        /// Verifies that encrypting then decrypting with legacy DES returns the original plaintext.
        /// DES is obsolete but still supported by CryptoUtility.
        /// </summary>
        [Fact]
        public void EncryptText_DecryptText_RoundTrip_WithDes()
        {
            // Arrange
            const string plainText = "Test data";

#pragma warning disable SYSLIB0021 // DES.Create() is obsolete
            // Act
            string encrypted;
            using (var desEncrypt = DES.Create())
            {
                encrypted = CryptoUtility.EncryptText(plainText, desEncrypt);
            }

            string decrypted;
            using (var desDecrypt = DES.Create())
            {
                decrypted = CryptoUtility.DecryptText(encrypted, desDecrypt);
            }
#pragma warning restore SYSLIB0021

            // Assert
            decrypted.Should().Be(plainText);
        }

        /// <summary>
        /// Verifies round-trip with an explicitly provided custom key string.
        /// </summary>
        [Fact]
        public void EncryptText_DecryptText_RoundTrip_WithCustomKey()
        {
            // Arrange
            const string plainText = "Custom key test data with special chars: ñ, ö, ü!";
            const string customKey = "MyCustomEncryptionKey1234567890AB";

            // Act
            string encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptText(plainText, customKey, aesEncrypt);
            }

            string decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptText(encrypted, customKey, aesDecrypt);
            }

            // Assert
            decrypted.Should().Be(plainText);
        }

        /// <summary>
        /// Verifies that EncryptText output is valid Base64 by attempting to decode it.
        /// Convert.FromBase64String throws FormatException on invalid input.
        /// </summary>
        [Fact]
        public void EncryptText_ProducesBase64Output()
        {
            // Arrange
            const string plainText = "Base64 validation check";

            // Act
            string encrypted;
            using (var aes = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptText(plainText, aes);
            }

            // Assert — should not throw; valid Base64 decodes to non-empty bytes
            encrypted.Should().NotBeNullOrEmpty();
            byte[] decoded = Convert.FromBase64String(encrypted);
            decoded.Should().NotBeNull();
            decoded.Length.Should().BeGreaterThan(0);
        }

        /// <summary>
        /// Verifies deterministic encryption: encrypting the same text twice with
        /// the same default key and algorithm type yields identical ciphertext.
        /// This is expected because GetValidKey/GetValidIV derive key and IV
        /// deterministically from the key string.
        /// </summary>
        [Fact]
        public void EncryptText_SameInputProducesSameOutput()
        {
            // Arrange
            const string plainText = "Deterministic encryption test";

            // Act
            string encrypted1;
            using (var aes1 = Aes.Create())
            {
                encrypted1 = CryptoUtility.EncryptText(plainText, aes1);
            }

            string encrypted2;
            using (var aes2 = Aes.Create())
            {
                encrypted2 = CryptoUtility.EncryptText(plainText, aes2);
            }

            // Assert
            encrypted1.Should().Be(encrypted2);
        }

        /// <summary>
        /// Verifies that decrypting with a different key than was used for encryption
        /// either throws a CryptographicException (due to padding validation) or
        /// returns a string that does not match the original plaintext.
        /// </summary>
        [Fact]
        public void DecryptText_WithWrongKey_FailsOrProducesGarbage()
        {
            // Arrange
            const string plainText = "Secret data to protect";
            const string correctKey = "CorrectEncryptionKey123456ABCDEF";
            const string wrongKey = "WrongDecryptionKey987654ZYXWVU!!";

            string encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptText(plainText, correctKey, aesEncrypt);
            }

            // Act & Assert — either throws CryptographicException or returns garbage
            bool threwException = false;
            string decrypted = null;
            try
            {
                using (var aesDecrypt = Aes.Create())
                {
                    decrypted = CryptoUtility.DecryptText(encrypted, wrongKey, aesDecrypt);
                }
            }
            catch (CryptographicException)
            {
                threwException = true;
            }

            // At least one of these must be true:
            // 1) A CryptographicException was thrown (typical for AES with PKCS7 padding)
            // 2) The decrypted output doesn't match the original
            if (!threwException)
            {
                decrypted.Should().NotBe(plainText,
                    "decrypting with a wrong key should not produce the original plaintext");
            }
            else
            {
                threwException.Should().BeTrue(
                    "a CryptographicException is the expected result when decrypting with wrong key");
            }
        }

        #endregion

        #region EncryptData / DecryptData Round-Trip Tests

        /// <summary>
        /// Verifies that encrypting then decrypting byte data with AES returns the original bytes.
        /// </summary>
        [Fact]
        public void EncryptData_DecryptData_RoundTrip_WithAes()
        {
            // Arrange
            byte[] plainData = Encoding.UTF8.GetBytes("Binary test payload");

            // Act
            byte[] encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptData(plainData, aesEncrypt);
            }

            byte[] decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptData(encrypted, aesDecrypt);
            }

            // Assert
            decrypted.Should().BeEquivalentTo(plainData);
        }

        /// <summary>
        /// Verifies byte array round-trip with an explicitly provided custom key.
        /// </summary>
        [Fact]
        public void EncryptData_DecryptData_RoundTrip_WithCustomKey()
        {
            // Arrange
            byte[] plainData = Encoding.UTF8.GetBytes("Custom key binary data for encryption");
            const string customKey = "DataEncryptionKeyForByteArrays!!";

            // Act
            byte[] encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptData(plainData, customKey, aesEncrypt);
            }

            byte[] decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptData(encrypted, customKey, aesDecrypt);
            }

            // Assert
            decrypted.Should().BeEquivalentTo(plainData);
        }

        /// <summary>
        /// Verifies that encrypted bytes are different from the original plaintext bytes.
        /// This validates that encryption actually transforms the data.
        /// </summary>
        [Fact]
        public void EncryptData_ProducesDifferentOutputThanInput()
        {
            // Arrange
            byte[] plainData = Encoding.UTF8.GetBytes("This should be transformed by encryption");

            // Act
            byte[] encrypted;
            using (var aes = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptData(plainData, aes);
            }

            // Assert — encrypted output must differ from input
            encrypted.Should().NotBeEquivalentTo(plainData);
        }

        /// <summary>
        /// Verifies that an empty byte array can be encrypted and decrypted back
        /// to an empty byte array. With PKCS7 padding, the encrypted output will
        /// contain a full padding block, but decryption strips it back.
        /// </summary>
        [Fact]
        public void EncryptData_EmptyArray_RoundTrips()
        {
            // Arrange
            byte[] emptyData = Array.Empty<byte>();

            // Act
            byte[] encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptData(emptyData, aesEncrypt);
            }

            byte[] decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptData(encrypted, aesDecrypt);
            }

            // Assert — round-trip should produce empty array
            decrypted.Should().BeEmpty();
        }

        #endregion

        #region Deterministic Derivation Tests (GetValidKey / GetValidIV Observable Effects)

        /// <summary>
        /// Verifies that the same key string and algorithm type always produce
        /// the same ciphertext for the same input — confirming GetValidKey and
        /// GetValidIV are deterministic.
        /// </summary>
        [Fact]
        public void EncryptText_DeterministicWithSameKeyAndAlgorithm()
        {
            // Arrange
            const string plainText = "Deterministic derivation test input";
            const string key = "SameKeyForBothEncryptionAttempts!";

            // Act
            string encrypted1;
            using (var aes1 = Aes.Create())
            {
                encrypted1 = CryptoUtility.EncryptText(plainText, key, aes1);
            }

            string encrypted2;
            using (var aes2 = Aes.Create())
            {
                encrypted2 = CryptoUtility.EncryptText(plainText, key, aes2);
            }

            // Assert
            encrypted1.Should().Be(encrypted2);
        }

        /// <summary>
        /// Verifies that two different key strings produce different ciphertext
        /// for the same input, confirming that key material affects the output.
        /// </summary>
        [Fact]
        public void EncryptText_DifferentKeys_ProduceDifferentCiphertext()
        {
            // Arrange
            const string plainText = "Same input, different keys test";
            const string key1 = "FirstEncryptionKeyABCDEF1234567890";
            const string key2 = "SecondEncryptionKey9876543210ZYXW";

            // Act
            string encrypted1;
            using (var aes1 = Aes.Create())
            {
                encrypted1 = CryptoUtility.EncryptText(plainText, key1, aes1);
            }

            string encrypted2;
            using (var aes2 = Aes.Create())
            {
                encrypted2 = CryptoUtility.EncryptText(plainText, key2, aes2);
            }

            // Assert
            encrypted1.Should().NotBe(encrypted2);
        }

        #endregion

        #region ComputeMD5Hash Tests (Unicode-based, dash-separated uppercase hex)

        /// <summary>
        /// Verifies that ComputeMD5Hash returns the BitConverter.ToString format:
        /// 16 hex byte pairs separated by dashes, uppercase (e.g., "AB-CD-EF-01-...").
        /// The method uses UnicodeEncoding (UTF-16 LE) for byte conversion.
        /// </summary>
        [Fact]
        public void ComputeMD5Hash_ReturnsExpectedDashFormat()
        {
            // Arrange
            const string input = "test string for MD5";

            // Act
            string hash = CryptoUtility.ComputeMD5Hash(input);

            // Assert — format: exactly 16 uppercase hex pairs with dashes between them
            // Total length: 16 * 2 (hex chars) + 15 (dashes) = 47 characters
            hash.Should().NotBeNullOrEmpty();
            hash.Should().MatchRegex(@"^[0-9A-F]{2}(-[0-9A-F]{2}){15}$");
        }

        /// <summary>
        /// Verifies that ComputeMD5Hash is deterministic: same input always produces same hash.
        /// </summary>
        [Fact]
        public void ComputeMD5Hash_SameInput_SameOutput()
        {
            // Arrange
            const string input = "deterministic hash test";

            // Act
            string hash1 = CryptoUtility.ComputeMD5Hash(input);
            string hash2 = CryptoUtility.ComputeMD5Hash(input);

            // Assert
            hash1.Should().Be(hash2);
        }

        /// <summary>
        /// Verifies that ComputeMD5Hash produces a valid (non-empty) hash for
        /// an empty string input. MD5 of empty input is still a valid 16-byte hash.
        /// </summary>
        [Fact]
        public void ComputeMD5Hash_EmptyString_ReturnsValidHash()
        {
            // Act
            string hash = CryptoUtility.ComputeMD5Hash(string.Empty);

            // Assert — empty string still produces a valid MD5 hash
            hash.Should().NotBeNullOrEmpty();
            hash.Should().MatchRegex(@"^[0-9A-F]{2}(-[0-9A-F]{2}){15}$");
        }

        #endregion

        #region ComputeMD5HashBytes Tests (raw 16-byte MD5)

        /// <summary>
        /// Verifies that ComputeMD5HashBytes always returns exactly 16 bytes,
        /// which is the fixed size of an MD5 digest.
        /// </summary>
        [Fact]
        public void ComputeMD5HashBytes_Returns16Bytes()
        {
            // Arrange
            const string input = "hash bytes test input";

            // Act
            byte[] hash = CryptoUtility.ComputeMD5HashBytes(input);

            // Assert
            hash.Should().NotBeNull();
            hash.Should().HaveCount(16);
        }

        /// <summary>
        /// Verifies that ComputeMD5HashBytes is deterministic: same input yields identical byte arrays.
        /// </summary>
        [Fact]
        public void ComputeMD5HashBytes_SameInput_SameOutput()
        {
            // Arrange
            const string input = "byte hash determinism test";

            // Act
            byte[] hash1 = CryptoUtility.ComputeMD5HashBytes(input);
            byte[] hash2 = CryptoUtility.ComputeMD5HashBytes(input);

            // Assert
            hash1.Should().BeEquivalentTo(hash2);
        }

        #endregion

        #region ComputeOddMD5Hash Tests (lowercase hex, no dashes, 32 chars)

        /// <summary>
        /// Verifies that ComputeOddMD5Hash returns lowercase hex without dashes,
        /// using the {0:x2} format specifier. Result is always exactly 32 characters.
        /// Uses Encoding.Unicode (UTF-16 LE) for byte conversion.
        /// </summary>
        [Fact]
        public void ComputeOddMD5Hash_ReturnsLowercaseHex()
        {
            // Arrange
            const string input = "odd hash format test";

            // Act
            string hash = CryptoUtility.ComputeOddMD5Hash(input);

            // Assert — 32 lowercase hex characters, no dashes
            hash.Should().NotBeNullOrEmpty();
            hash.Should().HaveLength(32);
            hash.Should().MatchRegex(@"^[0-9a-f]{32}$");
        }

        /// <summary>
        /// Verifies that ComputeOddMD5Hash is deterministic for the same input.
        /// </summary>
        [Fact]
        public void ComputeOddMD5Hash_SameInput_SameOutput()
        {
            // Arrange
            const string input = "odd md5 determinism verification";

            // Act
            string hash1 = CryptoUtility.ComputeOddMD5Hash(input);
            string hash2 = CryptoUtility.ComputeOddMD5Hash(input);

            // Assert
            hash1.Should().Be(hash2);
        }

        #endregion

        #region ComputePhpLikeMD5Hash Tests (Encoding.Default-based, lowercase hex, 32 chars)

        /// <summary>
        /// Verifies that ComputePhpLikeMD5Hash returns lowercase hex, 32 characters,
        /// using Encoding.Default for byte conversion (UTF-8 on .NET Core/.NET 5+).
        /// The manual hex formatting ensures lowercase output compatible with PHP's md5().
        /// </summary>
        [Fact]
        public void ComputePhpLikeMD5Hash_ReturnsLowercaseHex()
        {
            // Arrange
            const string input = "php like hash format test";

            // Act
            string hash = CryptoUtility.ComputePhpLikeMD5Hash(input);

            // Assert — 32 lowercase hex characters
            hash.Should().NotBeNullOrEmpty();
            hash.Should().HaveLength(32);
            hash.Should().MatchRegex(@"^[0-9a-f]{32}$");
        }

        /// <summary>
        /// Verifies that ComputePhpLikeMD5Hash is deterministic for the same input.
        /// </summary>
        [Fact]
        public void ComputePhpLikeMD5Hash_SameInput_SameOutput()
        {
            // Arrange
            const string input = "php md5 determinism verification";

            // Act
            string hash1 = CryptoUtility.ComputePhpLikeMD5Hash(input);
            string hash2 = CryptoUtility.ComputePhpLikeMD5Hash(input);

            // Assert
            hash1.Should().Be(hash2);
        }

        #endregion

        #region CryptKey Property Tests

        /// <summary>
        /// Verifies that when ErpSettings.EncryptionKey is null or empty (default state),
        /// CryptKey returns the hardcoded fallback constant.
        /// Uses reflection to reset the cached static field to force re-evaluation.
        /// </summary>
        [Fact]
        public void CryptKey_WhenErpSettingsEncryptionKeyIsEmpty_ReturnsFallbackDefault()
        {
            // Arrange — reset the static cache so CryptKey re-evaluates from ErpSettings
            ResetCryptKeyCache();

            // ErpSettings.EncryptionKey defaults to null when Initialize() has not been called,
            // which triggers the fallback to the hardcoded default key.

            // Act
            string key = CryptoUtility.CryptKey;

            // Assert
            key.Should().NotBeNullOrEmpty();
            key.Should().Be(DefaultCryptKey);
        }

        /// <summary>
        /// Verifies that CryptKey always returns a non-null, non-empty string
        /// regardless of ErpSettings state. This is a safety net assertion.
        /// </summary>
        [Fact]
        public void CryptKey_IsNotNullOrEmpty()
        {
            // Act
            string key = CryptoUtility.CryptKey;

            // Assert
            key.Should().NotBeNullOrEmpty();
        }

        #endregion
    }
}
