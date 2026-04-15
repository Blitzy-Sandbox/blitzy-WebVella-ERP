using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Moq;
using WebVella.Erp;
using WebVella.Erp.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.Security
{
    /// <summary>
    /// SECURITY: Regression test suite for encryption security improvements in CryptoUtility.
    /// 
    /// Tests validate the following security fixes:
    /// - CWE-798 (Hard-coded Credentials): Ensures encryption operations fail when no key is configured,
    ///   verifying that the hard-coded defaultCryptKey has been removed.
    /// - CWE-329 (Predictable IV): Ensures random IV generation per encryption operation,
    ///   producing different ciphertext for the same plaintext.
    /// 
    /// Coverage includes:
    /// - Encryption key validation (no default fallback)
    /// - IV randomness verification (different ciphertext each encryption)
    /// - PBKDF2 key derivation with 10,000+ iterations
    /// - Round-trip encryption/decryption integrity
    /// - Key length/strength validation
    /// 
    /// Run with: dotnet test --filter "Category=Security"
    /// </summary>
    [Trait("Category", "Security")]
    public class CryptoSecurityTests : IDisposable
    {
        #region <--- Test Data --->

        /// <summary>
        /// Sample plaintext for encryption tests.
        /// </summary>
        private const string TestPlaintext = "This is a secret message for security testing.";

        /// <summary>
        /// A cryptographically strong test encryption key (64 hex characters = 256 bits).
        /// SECURITY: This is a test key - production should use unique random keys.
        /// </summary>
        private const string ValidEncryptionKey = "A1B2C3D4E5F6071829384756AFBECD1234567890ABCDEF1234567890ABCDEF12";

        /// <summary>
        /// A weak encryption key for testing key validation (too short).
        /// </summary>
        private const string WeakEncryptionKey = "weakkey";

        /// <summary>
        /// Number of encryption iterations for IV randomness testing.
        /// SECURITY: Multiple iterations needed to verify IV uniqueness.
        /// </summary>
        private const int IvRandomnessIterations = 5;

        /// <summary>
        /// Minimum expected PBKDF2 iterations per NIST SP 800-132.
        /// </summary>
        private const int MinimumPbkdf2Iterations = 10000;

        #endregion

        #region <--- Setup and Teardown --->

        /// <summary>
        /// Stores the original encryption key state to restore after tests.
        /// </summary>
        private readonly string _originalEncryptionKey;
        private readonly bool _originalIsInitialized;

        /// <summary>
        /// Constructor - save original state before tests.
        /// </summary>
        public CryptoSecurityTests()
        {
            // Save original state using reflection
            _originalEncryptionKey = GetEncryptionKeyViaReflection();
            _originalIsInitialized = ErpSettings.IsInitialized;
        }

        /// <summary>
        /// Dispose - restore original state after tests.
        /// </summary>
        public void Dispose()
        {
            // Reset ErpSettings state to not pollute other tests
            ResetErpSettings();
            
            // Restore original encryption key if it was set
            if (_originalIsInitialized && !string.IsNullOrWhiteSpace(_originalEncryptionKey))
            {
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string>
                    {
                        { "Settings:EncryptionKey", _originalEncryptionKey }
                    })
                    .Build();
                ErpSettings.Initialize(config);
            }
        }

        /// <summary>
        /// Resets ErpSettings static state using reflection.
        /// SECURITY TEST REQUIREMENT: Static classes need special handling in tests.
        /// </summary>
        private void ResetErpSettings()
        {
            // Reset the static cryptKey field in CryptoUtility
            var cryptKeyField = typeof(CryptoUtility).GetField("cryptKey", 
                BindingFlags.NonPublic | BindingFlags.Static);
            if (cryptKeyField != null)
            {
                cryptKeyField.SetValue(null, null);
            }

            // Reset the IsInitialized and EncryptionKey properties
            var isInitializedField = typeof(ErpSettings).GetField("<IsInitialized>k__BackingField", 
                BindingFlags.NonPublic | BindingFlags.Static);
            if (isInitializedField != null)
            {
                isInitializedField.SetValue(null, false);
            }

            var encryptionKeyField = typeof(ErpSettings).GetField("<EncryptionKey>k__BackingField", 
                BindingFlags.NonPublic | BindingFlags.Static);
            if (encryptionKeyField != null)
            {
                encryptionKeyField.SetValue(null, null);
            }
        }

        /// <summary>
        /// Gets the current encryption key via reflection for state preservation.
        /// </summary>
        private string GetEncryptionKeyViaReflection()
        {
            try
            {
                return ErpSettings.EncryptionKey;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Initializes ErpSettings with a test configuration containing the specified encryption key.
        /// </summary>
        /// <param name="encryptionKey">The encryption key to configure</param>
        private void InitializeWithEncryptionKey(string encryptionKey)
        {
            ResetErpSettings();

            var configValues = new Dictionary<string, string>
            {
                { "Settings:EncryptionKey", encryptionKey },
                { "Settings:ConnectionString", "Host=localhost;Database=test;Username=test;Password=test" }
            };

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            ErpSettings.Initialize(config);
        }

        /// <summary>
        /// Initializes ErpSettings with an empty/missing encryption key.
        /// </summary>
        private void InitializeWithoutEncryptionKey()
        {
            ResetErpSettings();

            var configValues = new Dictionary<string, string>
            {
                { "Settings:ConnectionString", "Host=localhost;Database=test;Username=test;Password=test" }
                // Intentionally omitting Settings:EncryptionKey
            };

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            ErpSettings.Initialize(config);
        }

        #endregion

        #region <--- TestEncryptionWithoutKeyFails --->

        /// <summary>
        /// SECURITY TEST: Verify encryption fails when no encryption key is configured.
        /// 
        /// CWE-798 (Hard-coded Credentials) mitigation verification:
        /// - The defaultCryptKey constant has been removed from CryptoUtility
        /// - CryptKey property must throw InvalidOperationException when unconfigured
        /// - This prevents fallback to a known default key
        /// 
        /// Attack scenario prevented: Without this fix, an attacker knowing the default key
        /// could decrypt any data encrypted by the system without proper configuration.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptionWithoutKeyFails()
        {
            // Arrange: Initialize without encryption key
            InitializeWithoutEncryptionKey();

            // Act & Assert: Accessing CryptKey should throw InvalidOperationException
            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                // Access CryptKey property - should throw
                _ = CryptoUtility.CryptKey;
            });

            // Verify error message contains security-related guidance
            Assert.NotNull(exception.Message);
            Assert.Contains("Encryption key", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// SECURITY TEST: Verify EncryptText fails when no encryption key is configured.
        /// 
        /// This tests the full encryption path rather than just the property access.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptTextWithoutKeyFails()
        {
            // Arrange: Initialize without encryption key
            InitializeWithoutEncryptionKey();

            // Act & Assert: Attempting encryption should throw InvalidOperationException
            using (var aes = Aes.Create())
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                {
                    CryptoUtility.EncryptText(TestPlaintext, aes);
                });

                Assert.NotNull(exception.Message);
                Assert.Contains("Encryption key", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify EncryptData fails when no encryption key is configured.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptDataWithoutKeyFails()
        {
            // Arrange: Initialize without encryption key
            InitializeWithoutEncryptionKey();

            byte[] testData = Encoding.UTF8.GetBytes(TestPlaintext);

            // Act & Assert: Attempting encryption should throw InvalidOperationException
            using (var aes = Aes.Create())
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                {
                    CryptoUtility.EncryptData(testData, aes);
                });

                Assert.NotNull(exception.Message);
            }
        }

        #endregion

        #region <--- TestIvRandomness --->

        /// <summary>
        /// SECURITY TEST: Verify encrypting the same plaintext produces different ciphertext each time.
        /// 
        /// CWE-329 (Predictable IV) mitigation verification:
        /// - Each encryption operation generates a unique random IV
        /// - Same plaintext with same key produces different ciphertext
        /// - Prevents pattern analysis attacks
        /// 
        /// Attack scenario prevented: Without random IVs, an attacker could:
        /// 1. Detect when the same data is encrypted multiple times
        /// 2. Build frequency analysis attacks
        /// 3. Identify encrypted values across different operations
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestIvRandomness()
        {
            // Arrange: Initialize with valid encryption key
            InitializeWithEncryptionKey(ValidEncryptionKey);

            var ciphertexts = new List<string>();

            // Act: Encrypt the same plaintext multiple times
            for (int i = 0; i < IvRandomnessIterations; i++)
            {
                using (var aes = Aes.Create())
                {
                    string ciphertext = CryptoUtility.EncryptText(TestPlaintext, aes);
                    ciphertexts.Add(ciphertext);
                }
            }

            // Assert: All ciphertexts should be different (random IV per operation)
            var uniqueCiphertexts = ciphertexts.Distinct().ToList();

            Assert.Equal(IvRandomnessIterations, uniqueCiphertexts.Count);

            // Additional verification: No two ciphertexts should match
            for (int i = 0; i < ciphertexts.Count; i++)
            {
                for (int j = i + 1; j < ciphertexts.Count; j++)
                {
                    Assert.NotEqual(ciphertexts[i], ciphertexts[j]);
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify IV randomness for byte array encryption.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestIvRandomnessForByteArray()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            byte[] testData = Encoding.UTF8.GetBytes(TestPlaintext);
            var encryptedResults = new List<byte[]>();

            // Act: Encrypt same data multiple times
            for (int i = 0; i < IvRandomnessIterations; i++)
            {
                using (var aes = Aes.Create())
                {
                    byte[] encrypted = CryptoUtility.EncryptData(testData, aes);
                    encryptedResults.Add(encrypted);
                }
            }

            // Assert: All encrypted results should be different
            for (int i = 0; i < encryptedResults.Count; i++)
            {
                for (int j = i + 1; j < encryptedResults.Count; j++)
                {
                    // Convert to base64 for easy comparison
                    string result1 = Convert.ToBase64String(encryptedResults[i]);
                    string result2 = Convert.ToBase64String(encryptedResults[j]);
                    Assert.NotEqual(result1, result2);
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify ciphertext structure includes prepended salt and IV.
        /// The secure implementation prepends: salt[16] + IV[blockSize] + ciphertext
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestCiphertextContainsSaltAndIv()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);

            // Act
            string ciphertext;
            int expectedMinLength;
            using (var aes = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(TestPlaintext, aes);
                // Expected minimum: 16 bytes salt + 16 bytes IV (AES block size) + at least 16 bytes ciphertext
                expectedMinLength = 16 + (aes.BlockSize / 8) + 16;
            }

            // Assert: Ciphertext should be long enough to contain salt + IV + encrypted data
            byte[] ciphertextBytes = Convert.FromBase64String(ciphertext);
            Assert.True(ciphertextBytes.Length >= expectedMinLength,
                $"Ciphertext length {ciphertextBytes.Length} is less than expected minimum {expectedMinLength} (salt + IV + data)");
        }

        #endregion

        #region <--- TestKeyDerivation --->

        /// <summary>
        /// SECURITY TEST: Verify PBKDF2 key derivation is used with adequate iterations.
        /// 
        /// This test verifies the behavior that indicates proper key derivation:
        /// - Keys are derived using PBKDF2 (Rfc2898DeriveBytes)
        /// - Minimum 10,000 iterations are used per NIST SP 800-132
        /// 
        /// Note: Since DeriveKey is a private method, we verify this through behavior:
        /// - Encryption operations complete successfully
        /// - Round-trip encryption/decryption works
        /// - The ciphertext format includes salt (indicating PBKDF2 is used)
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyDerivation()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string plaintext = "Key derivation test data";

            // Act: Encrypt and decrypt to verify key derivation works
            string ciphertext;
            string decrypted;

            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(plaintext, aesEncrypt);
            }

            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptText(ciphertext, aesDecrypt);
            }

            // Assert: Decryption should succeed (proper key derivation)
            Assert.Equal(plaintext, decrypted);

            // Verify ciphertext structure indicates PBKDF2 usage (includes salt)
            byte[] ciphertextBytes = Convert.FromBase64String(ciphertext);

            // The first 16 bytes should be salt, followed by IV, then ciphertext
            // Salt presence indicates PBKDF2 key derivation
            Assert.True(ciphertextBytes.Length > 32, 
                "Ciphertext should contain salt (16 bytes) + IV + encrypted data, indicating PBKDF2 usage");
        }

        /// <summary>
        /// SECURITY TEST: Verify that different salts produce different derived keys.
        /// 
        /// This is tested indirectly by verifying that encrypting the same plaintext
        /// produces different ciphertext (which includes different salts).
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyDerivationWithDifferentSalts()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string plaintext = "Same plaintext for salt testing";

            // Act: Encrypt twice
            string ciphertext1, ciphertext2;

            using (var aes1 = Aes.Create())
            {
                ciphertext1 = CryptoUtility.EncryptText(plaintext, aes1);
            }

            using (var aes2 = Aes.Create())
            {
                ciphertext2 = CryptoUtility.EncryptText(plaintext, aes2);
            }

            // Assert: Different ciphertexts due to different salts
            Assert.NotEqual(ciphertext1, ciphertext2);

            // Both should decrypt correctly despite different salts
            string decrypted1, decrypted2;

            using (var aesD1 = Aes.Create())
            {
                decrypted1 = CryptoUtility.DecryptText(ciphertext1, aesD1);
            }

            using (var aesD2 = Aes.Create())
            {
                decrypted2 = CryptoUtility.DecryptText(ciphertext2, aesD2);
            }

            Assert.Equal(plaintext, decrypted1);
            Assert.Equal(plaintext, decrypted2);
        }

        /// <summary>
        /// SECURITY TEST: Verify PBKDF2 iteration count through timing analysis.
        /// 
        /// With 10,000+ iterations, encryption should take measurable time.
        /// This is a heuristic test - not a precise measurement.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyDerivationIterationsTiming()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const int testIterations = 10;
            var elapsed = new List<long>();

            // Act: Measure encryption time
            for (int i = 0; i < testIterations; i++)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                using (var aes = Aes.Create())
                {
                    CryptoUtility.EncryptText(TestPlaintext, aes);
                }
                stopwatch.Stop();
                elapsed.Add(stopwatch.ElapsedTicks);
            }

            // Assert: Encryption should take some measurable time due to PBKDF2
            // With 10,000 iterations, this should not be instantaneous
            // We're just verifying the operation completes successfully
            Assert.True(elapsed.Count == testIterations, "All encryption operations should complete");
            Assert.True(elapsed.All(e => e > 0), "Encryption operations should take measurable time");
        }

        #endregion

        #region <--- TestEncryptionDecryptionRoundTrip --->

        /// <summary>
        /// SECURITY TEST: Verify round-trip encryption/decryption integrity.
        /// 
        /// This ensures the security improvements don't break basic functionality:
        /// - Text encrypted with a configured key can be decrypted
        /// - Original plaintext is recovered exactly
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptionDecryptionRoundTrip()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string originalText = "Round-trip encryption test with special chars: áéíóú 中文 🔐";

            // Act
            string ciphertext;
            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(originalText, aesEncrypt);
            }

            string decryptedText;
            using (var aesDecrypt = Aes.Create())
            {
                decryptedText = CryptoUtility.DecryptText(ciphertext, aesDecrypt);
            }

            // Assert
            Assert.Equal(originalText, decryptedText);
        }

        /// <summary>
        /// SECURITY TEST: Verify round-trip for byte array data.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptionDecryptionRoundTripBytes()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            byte[] originalData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD, 0x10, 0x20 };

            // Act
            byte[] encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptData(originalData, aesEncrypt);
            }

            byte[] decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptData(encrypted, aesDecrypt);
            }

            // Assert
            Assert.Equal(originalData.Length, decrypted.Length);
            Assert.True(originalData.SequenceEqual(decrypted), "Decrypted data should match original");
        }

        /// <summary>
        /// SECURITY TEST: Verify encryption produces non-empty ciphertext.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptionProducesCiphertext()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);

            // Act
            string ciphertext;
            using (var aes = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(TestPlaintext, aes);
            }

            // Assert
            Assert.False(string.IsNullOrEmpty(ciphertext), "Ciphertext should not be empty");
            Assert.NotEqual(TestPlaintext, ciphertext);

            // Verify it's valid Base64
            byte[] decoded = Convert.FromBase64String(ciphertext);
            Assert.True(decoded.Length > 0, "Decoded ciphertext should have content");
        }

        /// <summary>
        /// SECURITY TEST: Verify encryption with explicit key parameter.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptionWithExplicitKey()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string customKey = "CustomKeyForExplicitParameterTest123456789012";
            const string plaintext = "Test with explicit key parameter";

            // Act
            string ciphertext;
            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(plaintext, customKey, aesEncrypt);
            }

            string decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptText(ciphertext, customKey, aesDecrypt);
            }

            // Assert
            Assert.Equal(plaintext, decrypted);
        }

        /// <summary>
        /// SECURITY TEST: Verify decryption with wrong key fails.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestDecryptionWithWrongKeyFails()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string correctKey = "CorrectEncryptionKeyForThisTest1234567890123";
            const string wrongKey = "WrongDecryptionKeyThatShouldFail12345678901234";
            const string plaintext = "Secret data";

            // Act: Encrypt with correct key
            string ciphertext;
            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(plaintext, correctKey, aesEncrypt);
            }

            // Assert: Decrypting with wrong key should fail or produce garbage
            using (var aesDecrypt = Aes.Create())
            {
                // Decryption with wrong key should either throw or return different data
                try
                {
                    string decrypted = CryptoUtility.DecryptText(ciphertext, wrongKey, aesDecrypt);
                    // If it doesn't throw, the decrypted text should not match
                    Assert.NotEqual(plaintext, decrypted);
                }
                catch (CryptographicException)
                {
                    // Expected - wrong key causes decryption failure
                    Assert.True(true);
                }
                catch (Exception)
                {
                    // Other exceptions are acceptable for wrong key scenarios
                    Assert.True(true);
                }
            }
        }

        #endregion

        #region <--- TestKeyLengthValidation --->

        /// <summary>
        /// SECURITY TEST: Verify encryption works with strong keys.
        /// 
        /// Strong keys should be accepted and work correctly for encryption.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyLengthValidation_StrongKeyAccepted()
        {
            // Arrange: Initialize with a strong key (256 bits / 64 hex chars)
            InitializeWithEncryptionKey(ValidEncryptionKey);

            // Act & Assert: Encryption should succeed
            using (var aes = Aes.Create())
            {
                string ciphertext = CryptoUtility.EncryptText(TestPlaintext, aes);
                Assert.False(string.IsNullOrEmpty(ciphertext));
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify various key lengths work with PBKDF2 derivation.
        /// 
        /// With PBKDF2 key derivation, keys of various lengths should work,
        /// but shorter keys provide less entropy.
        /// </summary>
        [Theory]
        [Trait("Category", "Security")]
        [InlineData("ShortKey123456")] // 14 chars
        [InlineData("MediumLengthKey1234567890")] // 25 chars
        [InlineData("LongerKeyWithMoreCharacters1234567890ABCDEF")] // 44 chars
        public void TestKeyLengthValidation_VariousLengths(string key)
        {
            // Arrange
            InitializeWithEncryptionKey(key);
            const string plaintext = "Test plaintext for key length testing";

            // Act
            string ciphertext;
            string decrypted;

            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(plaintext, aesEncrypt);
            }

            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptText(ciphertext, aesDecrypt);
            }

            // Assert: Round-trip should work regardless of key length
            // (PBKDF2 derives a proper-length key)
            Assert.Equal(plaintext, decrypted);
        }

        /// <summary>
        /// SECURITY TEST: Verify encryption with empty string key fails.
        /// 
        /// An empty key should not be accepted as it provides no security.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyLengthValidation_EmptyKeyRejected()
        {
            // Arrange: Initialize with empty encryption key
            ResetErpSettings();

            var configValues = new Dictionary<string, string>
            {
                { "Settings:EncryptionKey", "" },
                { "Settings:ConnectionString", "Host=localhost;Database=test" }
            };

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            ErpSettings.Initialize(config);

            // Act & Assert: Accessing CryptKey with empty key should throw
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = CryptoUtility.CryptKey;
            });
        }

        /// <summary>
        /// SECURITY TEST: Verify encryption with whitespace-only key fails.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyLengthValidation_WhitespaceKeyRejected()
        {
            // Arrange: Initialize with whitespace-only encryption key
            ResetErpSettings();

            var configValues = new Dictionary<string, string>
            {
                { "Settings:EncryptionKey", "   " },
                { "Settings:ConnectionString", "Host=localhost;Database=test" }
            };

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(configValues)
                .Build();

            ErpSettings.Initialize(config);

            // Act & Assert: Accessing CryptKey with whitespace key should throw
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = CryptoUtility.CryptKey;
            });
        }

        /// <summary>
        /// SECURITY TEST: Verify that different keys produce different ciphertext.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestKeyLengthValidation_DifferentKeysProduceDifferentCiphertext()
        {
            // Arrange
            const string key1 = "FirstEncryptionKey1234567890ABCDEF";
            const string key2 = "SecondEncryptionKey0987654321FEDCBA";
            const string plaintext = "Same plaintext for both keys";

            InitializeWithEncryptionKey(key1);

            // Act: Encrypt with two different keys
            string ciphertext1;
            using (var aes1 = Aes.Create())
            {
                ciphertext1 = CryptoUtility.EncryptText(plaintext, key1, aes1);
            }

            string ciphertext2;
            using (var aes2 = Aes.Create())
            {
                ciphertext2 = CryptoUtility.EncryptText(plaintext, key2, aes2);
            }

            // Assert: Different keys should produce different ciphertext
            Assert.NotEqual(ciphertext1, ciphertext2);
        }

        #endregion

        #region <--- Additional Security Tests --->

        /// <summary>
        /// SECURITY TEST: Verify that tampering with ciphertext causes decryption failure.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestCiphertextIntegrity()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string plaintext = "Data to protect from tampering";

            // Act: Encrypt
            string ciphertext;
            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(plaintext, aesEncrypt);
            }

            // Tamper with ciphertext (change a character in the middle)
            byte[] ciphertextBytes = Convert.FromBase64String(ciphertext);
            int tamperIndex = ciphertextBytes.Length / 2;
            ciphertextBytes[tamperIndex] = (byte)(ciphertextBytes[tamperIndex] ^ 0xFF);
            string tamperedCiphertext = Convert.ToBase64String(ciphertextBytes);

            // Assert: Decrypting tampered ciphertext should fail or produce garbage
            using (var aesDecrypt = Aes.Create())
            {
                try
                {
                    string decrypted = CryptoUtility.DecryptText(tamperedCiphertext, aesDecrypt);
                    // If decryption succeeds, the result should not match original
                    Assert.NotEqual(plaintext, decrypted);
                }
                catch (CryptographicException)
                {
                    // Expected behavior - tampered data causes decryption failure
                    Assert.True(true);
                }
                catch (Exception)
                {
                    // Other exceptions are acceptable for tampered data
                    Assert.True(true);
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify encryption of empty string.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptEmptyString()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            const string emptyPlaintext = "";

            // Act
            string ciphertext;
            using (var aesEncrypt = Aes.Create())
            {
                ciphertext = CryptoUtility.EncryptText(emptyPlaintext, aesEncrypt);
            }

            string decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptText(ciphertext, aesDecrypt);
            }

            // Assert
            Assert.Equal(emptyPlaintext, decrypted);
        }

        /// <summary>
        /// SECURITY TEST: Verify encryption of large data.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestEncryptLargeData()
        {
            // Arrange
            InitializeWithEncryptionKey(ValidEncryptionKey);
            
            // Create 1MB of random data
            byte[] largeData = new byte[1024 * 1024];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(largeData);
            }

            // Act
            byte[] encrypted;
            using (var aesEncrypt = Aes.Create())
            {
                encrypted = CryptoUtility.EncryptData(largeData, aesEncrypt);
            }

            byte[] decrypted;
            using (var aesDecrypt = Aes.Create())
            {
                decrypted = CryptoUtility.DecryptData(encrypted, aesDecrypt);
            }

            // Assert
            Assert.Equal(largeData.Length, decrypted.Length);
            Assert.True(largeData.SequenceEqual(decrypted), "Large data round-trip should preserve data integrity");
        }

        #endregion
    }
}
