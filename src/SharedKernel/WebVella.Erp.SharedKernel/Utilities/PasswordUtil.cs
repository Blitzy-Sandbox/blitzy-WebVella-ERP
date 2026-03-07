using System;
using System.Security.Cryptography;
using System.Text;

namespace WebVella.Erp.SharedKernel.Utilities
{
    /// <summary>
    /// Password hashing utilities supporting both legacy MD5 (for backward compatibility
    /// with existing monolith user records) and modern bcrypt-based hashing using
    /// RFC 2898 PBKDF2 with SHA-256 for new passwords.
    ///
    /// Migration strategy: On successful login with a legacy MD5 hash, the caller
    /// (SecurityManager) should rehash the password with <see cref="HashPassword"/>
    /// and update the stored hash. This provides a transparent migration path from
    /// MD5 to PBKDF2-SHA256 without requiring a password reset for all users.
    /// </summary>
    public static class PasswordUtil
    {
        private static readonly MD5 md5Hash = MD5.Create();

        /// <summary>
        /// Prefix used to identify PBKDF2-hashed passwords in the database.
        /// Legacy MD5 hashes do not have this prefix.
        /// </summary>
        private const string Pbkdf2Prefix = "$PBKDF2$";

        /// <summary>
        /// Number of PBKDF2 iterations (OWASP recommendation: ≥600,000 for SHA-256).
        /// </summary>
        private const int Pbkdf2Iterations = 600_000;

        /// <summary>
        /// Salt size in bytes (128 bits).
        /// </summary>
        private const int SaltSize = 16;

        /// <summary>
        /// Derived key size in bytes (256 bits).
        /// </summary>
        private const int KeySize = 32;

        /// <summary>
        /// Computes an MD5 hash of the input string for backward compatibility
        /// with existing monolith password records. New passwords should use
        /// <see cref="HashPassword"/> instead.
        /// </summary>
        /// <param name="input">The plaintext password to hash.</param>
        /// <returns>The MD5 hash as a lowercase hex string.</returns>
        internal static string GetMd5Hash(string input)
        {
			if (string.IsNullOrWhiteSpace(input))
				return string.Empty;

            byte[] data;
            lock (md5Hash)
            {
                data = md5Hash.ComputeHash(Encoding.UTF8.GetBytes(input));
            }

            StringBuilder sBuilder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
                sBuilder.Append(data[i].ToString("x2"));

            return sBuilder.ToString();
        }

        /// <summary>
        /// Verifies a plaintext password against a legacy MD5 hash.
        /// </summary>
        /// <param name="input">The plaintext password to verify.</param>
        /// <param name="hash">The stored MD5 hash to compare against.</param>
        /// <returns>True if the password matches the hash.</returns>
        internal static bool VerifyMd5Hash(string input, string hash)
        {
            string hashOfInput = GetMd5Hash(input);
            StringComparer comparer = StringComparer.OrdinalIgnoreCase;
            return (0 == comparer.Compare(hashOfInput, hash));
        }

        /// <summary>
        /// Hashes a password using PBKDF2 with SHA-256, a cryptographically secure
        /// random salt, and 600,000 iterations per OWASP guidelines.
        /// Returns a self-describing string format: $PBKDF2${iterations}${base64-salt}${base64-hash}
        /// </summary>
        /// <param name="password">The plaintext password to hash.</param>
        /// <returns>A PBKDF2-hashed password string with embedded salt and parameters.</returns>
        /// <exception cref="ArgumentException">Thrown when password is null or whitespace.</exception>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password cannot be null or whitespace.", nameof(password));

            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                KeySize);

            return $"{Pbkdf2Prefix}{Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// Verifies a plaintext password against a stored hash. Supports both
        /// modern PBKDF2 hashes (prefixed with $PBKDF2$) and legacy MD5 hashes.
        /// </summary>
        /// <param name="password">The plaintext password to verify.</param>
        /// <param name="storedHash">The stored hash (either PBKDF2 or legacy MD5).</param>
        /// <returns>True if the password matches the stored hash.</returns>
        public static bool VerifyPassword(string password, string storedHash)
        {
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
                return false;

            if (storedHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            {
                return VerifyPbkdf2Hash(password, storedHash);
            }

            // Legacy MD5 verification for pre-migration passwords
            return VerifyMd5Hash(password, storedHash);
        }

        /// <summary>
        /// Determines whether a stored hash is a legacy MD5 hash that should be
        /// upgraded to PBKDF2 on next successful login.
        /// </summary>
        /// <param name="storedHash">The stored password hash.</param>
        /// <returns>True if the hash is a legacy MD5 hash needing upgrade.</returns>
        public static bool NeedsUpgrade(string storedHash)
        {
            if (string.IsNullOrWhiteSpace(storedHash))
                return false;

            return !storedHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies a password against a PBKDF2-hashed string in the format:
        /// $PBKDF2${iterations}${base64-salt}${base64-hash}
        /// </summary>
        private static bool VerifyPbkdf2Hash(string password, string storedHash)
        {
            try
            {
                // Format: $PBKDF2${iterations}${base64-salt}${base64-hash}
                string withoutPrefix = storedHash.Substring(Pbkdf2Prefix.Length);
                string[] parts = withoutPrefix.Split('$');
                if (parts.Length != 3)
                    return false;

                int iterations = int.Parse(parts[0]);
                byte[] salt = Convert.FromBase64String(parts[1]);
                byte[] expectedHash = Convert.FromBase64String(parts[2]);

                byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password),
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    expectedHash.Length);

                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            catch
            {
                return false;
            }
        }
    }
}
