using System;
using System.Security.Cryptography;
using System.Text;
using BCrypt.Net; // SECURITY: BCrypt for secure password hashing (CWE-328 mitigation)

namespace WebVella.Erp.Utilities
{
    /// <summary>
    /// Utility class for secure password hashing and verification.
    /// SECURITY: This class implements BCrypt password hashing to mitigate CWE-328 (weak hash).
    /// BCrypt provides automatic salting, configurable cost factor, and timing-safe verification.
    /// </summary>
    public static class PasswordUtil
    {
        // SECURITY: Legacy MD5 instance - used only for backward compatibility verification during migration
        // New passwords MUST use BCrypt via HashPassword() method
        private static MD5 md5Hash = MD5.Create();

        /// <summary>
        /// Generates an MD5 hash of the input string.
        /// SECURITY WARNING: MD5 is cryptographically broken and should NOT be used for new password hashing.
        /// This method is kept internal for backward compatibility with existing code only.
        /// Use HashPassword() for new password hashing.
        /// </summary>
        /// <param name="input">The string to hash</param>
        /// <returns>MD5 hash as lowercase hex string</returns>
        [Obsolete("MD5 is cryptographically broken. Use HashPassword() for secure password hashing. This method is retained only for backward compatibility.")]
        internal static string GetMd5Hash(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            byte[] data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));

            StringBuilder sBuilder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
                sBuilder.Append(data[i].ToString("x2"));

            return sBuilder.ToString();
        }

        /// <summary>
        /// Hashes a password using BCrypt with cost factor 12.
        /// SECURITY: BCrypt provides automatic salting and is resistant to rainbow table attacks.
        /// Cost factor 12 results in ~250ms per hash operation, making brute force attacks impractical.
        /// </summary>
        /// <param name="password">The plaintext password to hash</param>
        /// <returns>BCrypt hash string in format $2a$12$... (60 characters)</returns>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return string.Empty;

            // SECURITY: Cost factor 12 = ~250ms per hash, prevents brute force attacks
            // Each password receives a unique 128-bit salt automatically embedded in hash
            return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
        }

        /// <summary>
        /// Verifies a password against a BCrypt hash using timing-safe comparison.
        /// SECURITY: Uses constant-time comparison to prevent timing attacks.
        /// </summary>
        /// <param name="password">The plaintext password to verify</param>
        /// <param name="hash">The BCrypt hash to verify against</param>
        /// <returns>True if password matches hash, false otherwise</returns>
        public static bool VerifyPassword(string password, string hash)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(hash))
                return false;

            try
            {
                // SECURITY: BCrypt.Verify uses timing-safe comparison internally
                return BCrypt.Net.BCrypt.Verify(password, hash);
            }
            catch
            {
                // Invalid hash format - return false rather than throwing
                // This prevents information leakage about hash format
                return false;
            }
        }

        /// <summary>
        /// Detects if a hash is in BCrypt format.
        /// BCrypt hashes start with $2a$, $2b$, or $2y$ followed by the cost factor.
        /// </summary>
        /// <param name="hash">The hash string to check</param>
        /// <returns>True if the hash is in BCrypt format, false otherwise</returns>
        public static bool IsBcryptHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return false;

            // BCrypt hash format: $2a$XX$... or $2b$XX$... or $2y$XX$...
            // Where XX is the cost factor (e.g., 12)
            return hash.StartsWith("$2a$") || hash.StartsWith("$2b$") || hash.StartsWith("$2y$");
        }

        /// <summary>
        /// Verifies a password against a legacy MD5 hash.
        /// SECURITY WARNING: This method is for backward compatibility ONLY during migration.
        /// After successful verification, passwords should be rehashed with BCrypt via HashPassword().
        /// 
        /// Recommended migration pattern:
        /// 1. Check IsBcryptHash() - if true, use VerifyPassword()
        /// 2. If false (MD5 hash), use VerifyMd5Password()
        /// 3. On successful MD5 verification, rehash with HashPassword() and update stored hash
        /// </summary>
        /// <param name="password">The plaintext password to verify</param>
        /// <param name="storedHash">The MD5 hash stored in database</param>
        /// <returns>True if password matches the MD5 hash, false otherwise</returns>
        public static bool VerifyMd5Password(string password, string storedHash)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
                return false;

            // Suppress obsolete warning - intentional use for backward compatibility
#pragma warning disable CS0618
            string hashOfInput = GetMd5Hash(password);
#pragma warning restore CS0618
            // Case-insensitive comparison for MD5 hex strings
            return StringComparer.OrdinalIgnoreCase.Compare(hashOfInput, storedHash) == 0;
        }

        /// <summary>
        /// Verifies an MD5 hash against an input string.
        /// </summary>
        /// <param name="input">The input string to verify</param>
        /// <param name="hash">The MD5 hash to compare against</param>
        /// <returns>True if input matches hash, false otherwise</returns>
        [Obsolete("Use VerifyPassword for BCrypt hashes or VerifyMd5Password for legacy hashes. This method will be removed in a future version.")]
        internal static bool VerifyMd5Hash(string input, string hash)
        {
            // Suppress obsolete warning - intentional use for backward compatibility
#pragma warning disable CS0618
            string hashOfInput = GetMd5Hash(input);
#pragma warning restore CS0618
            StringComparer comparer = StringComparer.OrdinalIgnoreCase;
            return (0 == comparer.Compare(hashOfInput, hash));
        }

    }
}
