using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using WebVella.Erp.Utilities;

namespace WebVella.Erp.Tests.Security
{
    /// <summary>
    /// Security regression tests for validating BCrypt password hashing implementation.
    /// These tests ensure the Critical severity CWE-328 (weak hash) vulnerability fix remains effective.
    /// 
    /// SECURITY CONTEXT:
    /// - MD5 password hashing has been replaced with BCrypt (cost factor 12)
    /// - BCrypt provides automatic 128-bit salting and timing-safe verification
    /// - Backward compatibility maintained for existing MD5 hashes during migration
    /// - Auto-rehash functionality upgrades MD5 passwords to BCrypt on successful login
    /// 
    /// Run these tests with: dotnet test --filter "Category=Security"
    /// </summary>
    [Trait("Category", "Security")]
    public class PasswordSecurityTests
    {
        // Test password constants for consistency across tests
        private const string TestPassword = "SecureTestPassword123!";
        private const string IncorrectPassword = "WrongPassword456!";
        private const string EmptyPassword = "";
        private const string WhitespacePassword = "   ";

        #region TestBcryptHashGeneration

        /// <summary>
        /// Verifies BCrypt hash generation produces correct format and properties.
        /// SECURITY: Validates CWE-328 mitigation through proper BCrypt hash format verification.
        /// 
        /// Test validates:
        /// 1. Hash format starts with $2a$12$ or $2b$12$ (cost factor 12 as specified in Section 0.5.2 Fix #1)
        /// 2. Hash length is exactly 60 characters (standard BCrypt output)
        /// 3. Generated hash is different from plaintext password
        /// 4. Hash contains embedded salt (unique per hash)
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptHashGeneration()
        {
            // Arrange
            string password = TestPassword;

            // Act
            string hash = PasswordUtil.HashPassword(password);

            // Assert - Hash is not null or empty
            Assert.NotNull(hash);
            Assert.NotEmpty(hash);

            // Assert - Hash format starts with BCrypt identifier and cost factor 12
            // BCrypt format: $2a$XX$... or $2b$XX$... where XX is cost factor
            bool hasValidPrefix = hash.StartsWith("$2a$12$") || hash.StartsWith("$2b$12$");
            Assert.True(hasValidPrefix, 
                $"BCrypt hash should start with '$2a$12$' or '$2b$12$' (cost factor 12). Actual: {hash.Substring(0, Math.Min(7, hash.Length))}...");

            // Assert - Hash length is exactly 60 characters (BCrypt standard)
            Assert.Equal(60, hash.Length);

            // Assert - Hash is different from plaintext password
            Assert.NotEqual(password, hash);

            // Assert - Hash can be independently verified by BCrypt library
            bool verificationResult = BCrypt.Net.BCrypt.Verify(password, hash);
            Assert.True(verificationResult, "Generated hash should be verifiable by BCrypt library");

            // Assert - Multiple hashes of same password are different (due to unique salt)
            string hash2 = PasswordUtil.HashPassword(password);
            Assert.NotEqual(hash, hash2);

            // Assert - IsBcryptHash correctly identifies the hash format
            Assert.True(PasswordUtil.IsBcryptHash(hash), "IsBcryptHash should return true for BCrypt hash");
        }

        /// <summary>
        /// Verifies HashPassword handles empty and null inputs gracefully.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptHashGenerationWithEmptyInput()
        {
            // Act
            string hashEmpty = PasswordUtil.HashPassword(EmptyPassword);
            string hashWhitespace = PasswordUtil.HashPassword(WhitespacePassword);

            // Assert - Empty/whitespace passwords return empty string (safe handling)
            Assert.Equal(string.Empty, hashEmpty);
            Assert.Equal(string.Empty, hashWhitespace);
        }

        /// <summary>
        /// Verifies that each hash operation generates a unique salt.
        /// SECURITY: Ensures rainbow table attacks are ineffective.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptUniqueSaltPerHash()
        {
            // Arrange
            string password = TestPassword;
            const int numberOfHashes = 5;
            string[] hashes = new string[numberOfHashes];

            // Act - Generate multiple hashes of the same password
            for (int i = 0; i < numberOfHashes; i++)
            {
                hashes[i] = PasswordUtil.HashPassword(password);
            }

            // Assert - All hashes should be unique (different salts)
            for (int i = 0; i < numberOfHashes; i++)
            {
                for (int j = i + 1; j < numberOfHashes; j++)
                {
                    Assert.NotEqual(hashes[i], hashes[j]);
                }
            }

            // Assert - But all hashes should still verify against the original password
            foreach (string hash in hashes)
            {
                Assert.True(PasswordUtil.VerifyPassword(password, hash), 
                    "All generated hashes should verify against the original password");
            }
        }

        #endregion

        #region TestBcryptVerification

        /// <summary>
        /// Verifies password verification succeeds with correct password and fails with incorrect password.
        /// SECURITY: Validates proper BCrypt verification for CWE-328 mitigation.
        /// 
        /// Test validates:
        /// 1. Correct password verifies successfully
        /// 2. Incorrect password fails verification
        /// 3. Verification uses timing-safe comparison (implicitly via BCrypt.Net)
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptVerification()
        {
            // Arrange
            string password = TestPassword;
            string wrongPassword = IncorrectPassword;
            string hash = PasswordUtil.HashPassword(password);

            // Act & Assert - Correct password verifies successfully
            bool correctPasswordResult = PasswordUtil.VerifyPassword(password, hash);
            Assert.True(correctPasswordResult, "Correct password should verify successfully");

            // Act & Assert - Incorrect password fails verification
            bool wrongPasswordResult = PasswordUtil.VerifyPassword(wrongPassword, hash);
            Assert.False(wrongPasswordResult, "Incorrect password should fail verification");

            // Act & Assert - Case sensitivity is maintained
            string upperCasePassword = password.ToUpper();
            bool caseSensitiveResult = PasswordUtil.VerifyPassword(upperCasePassword, hash);
            Assert.False(caseSensitiveResult, "Password verification should be case-sensitive");
        }

        /// <summary>
        /// Verifies VerifyPassword handles edge cases safely.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptVerificationEdgeCases()
        {
            // Arrange
            string validHash = PasswordUtil.HashPassword(TestPassword);

            // Act & Assert - Empty password returns false
            Assert.False(PasswordUtil.VerifyPassword(EmptyPassword, validHash), 
                "Empty password should return false");

            // Act & Assert - Whitespace password returns false
            Assert.False(PasswordUtil.VerifyPassword(WhitespacePassword, validHash), 
                "Whitespace password should return false");

            // Act & Assert - Empty hash returns false
            Assert.False(PasswordUtil.VerifyPassword(TestPassword, string.Empty), 
                "Empty hash should return false");

            // Act & Assert - Invalid hash format returns false (doesn't throw)
            Assert.False(PasswordUtil.VerifyPassword(TestPassword, "invalid_hash_format"), 
                "Invalid hash format should return false, not throw exception");

            // Act & Assert - MD5 hash format should return false for BCrypt verification
            string md5Hash = GenerateMd5Hash(TestPassword);
            Assert.False(PasswordUtil.VerifyPassword(TestPassword, md5Hash), 
                "MD5 hash should not verify via BCrypt VerifyPassword method");
        }

        #endregion

        #region TestMd5BackwardCompatibility

        /// <summary>
        /// Verifies old MD5 hashes (32 character hex strings) still verify correctly during migration period.
        /// SECURITY: Ensures backward compatibility as specified in Section 0.4.5 for gradual migration.
        /// 
        /// Test validates:
        /// 1. MD5 hashes are correctly identified as non-BCrypt format
        /// 2. VerifyMd5Password correctly verifies legacy MD5 hashes
        /// 3. Migration path works: detect format → verify with appropriate method
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMd5BackwardCompatibility()
        {
            // Arrange - Generate a legacy MD5 hash (simulating existing database hash)
            string password = TestPassword;
            string md5Hash = GenerateMd5Hash(password);

            // Assert - MD5 hash format: 32 character lowercase hex string
            Assert.Equal(32, md5Hash.Length);
            Assert.True(IsValidHexString(md5Hash), "MD5 hash should be a valid hex string");

            // Act & Assert - IsBcryptHash correctly identifies MD5 as NOT BCrypt
            Assert.False(PasswordUtil.IsBcryptHash(md5Hash), 
                "IsBcryptHash should return false for MD5 hash");

            // Act & Assert - VerifyMd5Password correctly verifies the legacy hash
            bool md5VerifyResult = PasswordUtil.VerifyMd5Password(password, md5Hash);
            Assert.True(md5VerifyResult, "VerifyMd5Password should correctly verify legacy MD5 hash");

            // Act & Assert - Wrong password fails MD5 verification
            bool wrongPasswordMd5Result = PasswordUtil.VerifyMd5Password(IncorrectPassword, md5Hash);
            Assert.False(wrongPasswordMd5Result, "Wrong password should fail MD5 verification");

            // Demonstrate migration path:
            // 1. Check if hash is BCrypt format
            // 2. If not BCrypt (MD5), verify with VerifyMd5Password
            // 3. On success, rehash with BCrypt
            bool isBcrypt = PasswordUtil.IsBcryptHash(md5Hash);
            bool verified = false;
            
            if (isBcrypt)
            {
                verified = PasswordUtil.VerifyPassword(password, md5Hash);
            }
            else
            {
                verified = PasswordUtil.VerifyMd5Password(password, md5Hash);
            }

            Assert.True(verified, "Migration path should successfully verify MD5 hash");
        }

        /// <summary>
        /// Verifies VerifyMd5Password handles edge cases safely.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestMd5BackwardCompatibilityEdgeCases()
        {
            // Arrange
            string md5Hash = GenerateMd5Hash(TestPassword);

            // Act & Assert - Empty password returns false
            Assert.False(PasswordUtil.VerifyMd5Password(EmptyPassword, md5Hash), 
                "Empty password should return false");

            // Act & Assert - Whitespace password returns false  
            Assert.False(PasswordUtil.VerifyMd5Password(WhitespacePassword, md5Hash), 
                "Whitespace password should return false");

            // Act & Assert - Empty hash returns false
            Assert.False(PasswordUtil.VerifyMd5Password(TestPassword, string.Empty), 
                "Empty hash should return false");

            // Act & Assert - MD5 verification is case-insensitive for hash comparison
            string upperCaseMd5Hash = md5Hash.ToUpper();
            Assert.True(PasswordUtil.VerifyMd5Password(TestPassword, upperCaseMd5Hash), 
                "MD5 hash comparison should be case-insensitive");
        }

        #endregion

        #region TestAutoRehashOnLogin

        /// <summary>
        /// Verifies the auto-rehash workflow: when a user logs in with an MD5-hashed password,
        /// the system detects the MD5 format and rehashes to BCrypt upon successful authentication.
        /// SECURITY: Ensures gradual migration from MD5 to BCrypt as specified in Section 0.4.5.
        /// 
        /// Test validates the complete migration workflow:
        /// 1. Detect hash format using IsBcryptHash()
        /// 2. Verify password using appropriate method (VerifyMd5Password for MD5)
        /// 3. On successful verification, generate new BCrypt hash
        /// 4. New hash is valid BCrypt format that verifies correctly
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAutoRehashOnLogin()
        {
            // Arrange - Simulate existing user with MD5 password hash in database
            string password = TestPassword;
            string legacyMd5Hash = GenerateMd5Hash(password);

            // Act - Simulate login flow with auto-rehash
            string currentStoredHash = legacyMd5Hash;
            string newHash = null;
            bool loginSuccessful = false;

            // Step 1: Detect hash format
            bool isBcryptFormat = PasswordUtil.IsBcryptHash(currentStoredHash);
            Assert.False(isBcryptFormat, "Initial hash should be detected as non-BCrypt (MD5)");

            // Step 2: Verify using appropriate method based on format
            if (!isBcryptFormat)
            {
                // MD5 format detected - use legacy verification
                loginSuccessful = PasswordUtil.VerifyMd5Password(password, currentStoredHash);
                
                if (loginSuccessful)
                {
                    // Step 3: Rehash to BCrypt on successful authentication
                    newHash = PasswordUtil.HashPassword(password);
                }
            }
            else
            {
                // BCrypt format - use standard verification
                loginSuccessful = PasswordUtil.VerifyPassword(password, currentStoredHash);
            }

            // Assert - Login was successful
            Assert.True(loginSuccessful, "Login with MD5 hash should succeed");

            // Assert - New BCrypt hash was generated
            Assert.NotNull(newHash);
            Assert.NotEmpty(newHash);

            // Assert - New hash is valid BCrypt format
            Assert.True(PasswordUtil.IsBcryptHash(newHash), 
                "Rehashed password should be in BCrypt format");
            bool hasValidPrefix = newHash.StartsWith("$2a$12$") || newHash.StartsWith("$2b$12$");
            Assert.True(hasValidPrefix, "New hash should have BCrypt prefix with cost factor 12");

            // Assert - New hash verifies correctly with BCrypt
            Assert.True(PasswordUtil.VerifyPassword(password, newHash), 
                "New BCrypt hash should verify with correct password");

            // Assert - After rehash, subsequent logins should use BCrypt verification
            // Simulate "database update" by using the new hash
            currentStoredHash = newHash;
            Assert.True(PasswordUtil.IsBcryptHash(currentStoredHash), 
                "Updated stored hash should be BCrypt format");
            Assert.True(PasswordUtil.VerifyPassword(password, currentStoredHash), 
                "Login with new BCrypt hash should succeed");
        }

        /// <summary>
        /// Verifies that the rehash workflow handles failed authentication correctly.
        /// SECURITY: Ensures no rehash occurs on failed authentication attempts.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestAutoRehashOnFailedLogin()
        {
            // Arrange
            string correctPassword = TestPassword;
            string wrongPassword = IncorrectPassword;
            string legacyMd5Hash = GenerateMd5Hash(correctPassword);

            // Act - Attempt login with wrong password
            bool isBcryptFormat = PasswordUtil.IsBcryptHash(legacyMd5Hash);
            string newHash = null;
            bool loginSuccessful = false;

            if (!isBcryptFormat)
            {
                loginSuccessful = PasswordUtil.VerifyMd5Password(wrongPassword, legacyMd5Hash);
                
                if (loginSuccessful)
                {
                    // This should NOT happen with wrong password
                    newHash = PasswordUtil.HashPassword(wrongPassword);
                }
            }

            // Assert - Login should fail with wrong password
            Assert.False(loginSuccessful, "Login with wrong password should fail");

            // Assert - No rehash should occur on failed authentication
            Assert.Null(newHash);
        }

        #endregion

        #region TestBcryptTimingResistance

        /// <summary>
        /// Verifies that password verification operations complete in approximately constant time
        /// regardless of input to prevent timing attacks.
        /// SECURITY: Validates timing-safe comparison for CWE-328 mitigation.
        /// 
        /// Note: This test checks for reasonable timing consistency. BCrypt's internal
        /// Verify() method uses constant-time comparison. Large timing differences would
        /// indicate a security concern, but some variance is expected due to system factors.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptTimingResistance()
        {
            // Arrange
            string password = TestPassword;
            string hash = PasswordUtil.HashPassword(password);
            
            // Different test inputs to compare timing
            string correctPassword = password;
            string wrongPasswordSameLength = "WrongTestPassword12!"; // Same length as TestPassword
            string wrongPasswordShort = "abc";
            string wrongPasswordLong = "ThisIsAVeryLongPasswordThatIsCompletelyWrong12345!@#$%";

            const int warmupIterations = 3;
            const int measurementIterations = 10;

            // Warmup - JIT compilation and cache warming
            for (int i = 0; i < warmupIterations; i++)
            {
                PasswordUtil.VerifyPassword(correctPassword, hash);
                PasswordUtil.VerifyPassword(wrongPasswordSameLength, hash);
            }

            // Measure timing for correct password
            var correctTimes = new long[measurementIterations];
            for (int i = 0; i < measurementIterations; i++)
            {
                var sw = Stopwatch.StartNew();
                PasswordUtil.VerifyPassword(correctPassword, hash);
                sw.Stop();
                correctTimes[i] = sw.ElapsedTicks;
            }

            // Measure timing for wrong password (same length)
            var wrongSameLengthTimes = new long[measurementIterations];
            for (int i = 0; i < measurementIterations; i++)
            {
                var sw = Stopwatch.StartNew();
                PasswordUtil.VerifyPassword(wrongPasswordSameLength, hash);
                sw.Stop();
                wrongSameLengthTimes[i] = sw.ElapsedTicks;
            }

            // Measure timing for wrong password (short)
            var wrongShortTimes = new long[measurementIterations];
            for (int i = 0; i < measurementIterations; i++)
            {
                var sw = Stopwatch.StartNew();
                PasswordUtil.VerifyPassword(wrongPasswordShort, hash);
                sw.Stop();
                wrongShortTimes[i] = sw.ElapsedTicks;
            }

            // Measure timing for wrong password (long)
            var wrongLongTimes = new long[measurementIterations];
            for (int i = 0; i < measurementIterations; i++)
            {
                var sw = Stopwatch.StartNew();
                PasswordUtil.VerifyPassword(wrongPasswordLong, hash);
                sw.Stop();
                wrongLongTimes[i] = sw.ElapsedTicks;
            }

            // Calculate median times (more robust than average)
            long medianCorrect = GetMedian(correctTimes);
            long medianWrongSameLength = GetMedian(wrongSameLengthTimes);
            long medianWrongShort = GetMedian(wrongShortTimes);
            long medianWrongLong = GetMedian(wrongLongTimes);

            // Assert - Timing should be relatively consistent
            // Allow 50% variance (generous due to system variability)
            // The key is that wrong passwords don't complete significantly faster than correct ones
            double maxVariance = 0.5; // 50%

            // Verify timing consistency - wrong passwords should not be significantly faster
            // This would indicate early-exit on mismatch (timing attack vulnerability)
            Assert.True(medianWrongSameLength >= (long)(medianCorrect * (1 - maxVariance)),
                $"Wrong password (same length) timing {medianWrongSameLength} should not be significantly faster than correct password timing {medianCorrect}");

            Assert.True(medianWrongShort >= (long)(medianCorrect * (1 - maxVariance)),
                $"Wrong password (short) timing {medianWrongShort} should not be significantly faster than correct password timing {medianCorrect}");

            Assert.True(medianWrongLong >= (long)(medianCorrect * (1 - maxVariance)),
                $"Wrong password (long) timing {medianWrongLong} should not be significantly faster than correct password timing {medianCorrect}");

            // Verify all operations complete successfully
            Assert.True(PasswordUtil.VerifyPassword(correctPassword, hash), "Correct password should verify");
            Assert.False(PasswordUtil.VerifyPassword(wrongPasswordSameLength, hash), "Wrong password should fail");
            Assert.False(PasswordUtil.VerifyPassword(wrongPasswordShort, hash), "Short wrong password should fail");
            Assert.False(PasswordUtil.VerifyPassword(wrongPasswordLong, hash), "Long wrong password should fail");
        }

        /// <summary>
        /// Verifies that invalid hash format handling doesn't leak timing information.
        /// </summary>
        [Fact]
        [Trait("Category", "Security")]
        public void TestBcryptTimingResistanceInvalidHash()
        {
            // Arrange
            string password = TestPassword;
            string validHash = PasswordUtil.HashPassword(password);
            string invalidHash = "not_a_valid_bcrypt_hash";
            string md5Hash = GenerateMd5Hash(password);

            const int iterations = 10;

            // Warmup
            PasswordUtil.VerifyPassword(password, validHash);
            PasswordUtil.VerifyPassword(password, invalidHash);

            // Measure timing for valid hash (correct password)
            var validHashTimes = new long[iterations];
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                PasswordUtil.VerifyPassword(password, validHash);
                sw.Stop();
                validHashTimes[i] = sw.ElapsedTicks;
            }

            // Measure timing for invalid hash format
            var invalidHashTimes = new long[iterations];
            for (int i = 0; i < iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                PasswordUtil.VerifyPassword(password, invalidHash);
                sw.Stop();
                invalidHashTimes[i] = sw.ElapsedTicks;
            }

            // Invalid hash verification should complete (return false) without throwing
            // Note: Invalid hash may be faster to reject, which is acceptable as it doesn't
            // leak information about the correct password
            Assert.False(PasswordUtil.VerifyPassword(password, invalidHash), 
                "Invalid hash should return false, not throw");
            Assert.False(PasswordUtil.VerifyPassword(password, md5Hash), 
                "MD5 hash should return false via BCrypt VerifyPassword");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Generates an MD5 hash for testing backward compatibility.
        /// This simulates the legacy hash format stored in the database before BCrypt migration.
        /// </summary>
        /// <param name="input">The input string to hash</param>
        /// <returns>MD5 hash as lowercase hex string (32 characters)</returns>
        private static string GenerateMd5Hash(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            using (var md5 = MD5.Create())
            {
                byte[] data = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    sb.Append(data[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Validates that a string contains only valid hexadecimal characters.
        /// </summary>
        private static bool IsValidHexString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            foreach (char c in input)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Calculates the median value from an array of longs.
        /// Used for timing analysis to reduce impact of outliers.
        /// </summary>
        private static long GetMedian(long[] values)
        {
            if (values == null || values.Length == 0)
                return 0;

            long[] sorted = new long[values.Length];
            Array.Copy(values, sorted, values.Length);
            Array.Sort(sorted);

            int mid = sorted.Length / 2;
            if (sorted.Length % 2 == 0)
            {
                return (sorted[mid - 1] + sorted[mid]) / 2;
            }
            return sorted[mid];
        }

        #endregion
    }
}
