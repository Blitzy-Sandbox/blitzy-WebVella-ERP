using System;
using System.Security.Cryptography;
using System.Text;

namespace WebVella.Erp.Utilities
{
    // SECURITY (A02 — Cryptographic Failures). CWE-916 (insufficient computational effort / unsalted hash),
    // CWE-759 (missing salt), CWE-327 (broken/risky algorithm: MD5), CWE-208 (non-constant-time comparison).
    // Mitigation: HashPassword uses salted, iterated PBKDF2-HMAC-SHA256; VerifyPassword compares in constant
    // time and transparently rehashes legacy MD5 credentials on next login. Legacy MD5 helpers are retained
    // ONLY for that backward-compatible verification.
    public static class PasswordUtil
    {
        // PBKDF2 tuning. Kept as constants for easy adjustment. 210,000 iterations meets OWASP guidance
        // for PBKDF2-HMAC-SHA256 and stays well within the login performance envelope (login is infrequent).
        private const string Pbkdf2Prefix = "PBKDF2$";
        private const int Pbkdf2Iterations = 210000;
        // SECURITY (CWE-400): reject an absurd iteration count in a stored hash so a tampered value cannot
        // turn each verification into a CPU denial-of-service. 10,000,000 is far above any legitimate hash.
        private const int Pbkdf2MaxIterations = 10_000_000;
        private const int SaltByteSize = 16;    // 128-bit salt
        private const int SubkeyByteSize = 32;   // 256-bit subkey

        // Tri-state result: SuccessRehashNeeded lets callers upgrade a verified legacy (MD5) credential
        // to the modern PBKDF2 format transparently on the next successful login.
        public enum PasswordVerificationResult
        {
            Failed = 0,
            Success = 1,
            SuccessRehashNeeded = 2
        }

        /// <summary>
        /// SECURITY (A02 — CWE-916/CWE-327): hashes a password with salted, iterated PBKDF2-HMAC-SHA256
        /// (replaces unsalted MD5). Returns a self-describing, version-tagged string
        /// "PBKDF2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 subkey&gt;" that is trivially distinguishable
        /// from a 32-char lowercase-hex MD5 digest. Uses only in-box System.Security.Cryptography (no new dependency).
        /// </summary>
        public static string HashPassword(string password)
        {
            // Preserve legacy GetMd5Hash behavior: empty/whitespace input hashes to String.Empty.
            if (string.IsNullOrWhiteSpace(password))
                return string.Empty;

            // SECURITY (CWE-330/CWE-338): 128-bit salt from a cryptographically secure RNG.
            byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);

            // In-box PBKDF2 (HMAC-SHA256), 256-bit derived subkey.
            byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, SubkeyByteSize);

            return $"{Pbkdf2Prefix}{Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(subkey)}";
        }

        /// <summary>
        /// SECURITY (A02/A07): verifies a password against a stored hash, supporting BOTH the modern PBKDF2
        /// format and legacy unsalted MD5. Returns SuccessRehashNeeded when a legacy MD5 credential verifies,
        /// signalling the caller to persist a fresh HashPassword value (transparent, non-disruptive migration).
        /// </summary>
        public static PasswordVerificationResult VerifyPassword(string storedHash, string providedPassword)
        {
            if (string.IsNullOrEmpty(storedHash))
                return PasswordVerificationResult.Failed;

            // Modern PBKDF2 path.
            if (storedHash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            {
                // Format: "PBKDF2" $ iterations $ base64(salt) $ base64(subkey). Base64 never contains '$'.
                string[] parts = storedHash.Split('$');
                if (parts.Length != 4)
                    return PasswordVerificationResult.Failed;

                if (!int.TryParse(parts[1], out int iterations))
                    return PasswordVerificationResult.Failed;

                byte[] salt;
                byte[] storedSubkey;
                try
                {
                    salt = Convert.FromBase64String(parts[2]);
                    storedSubkey = Convert.FromBase64String(parts[3]);
                }
                catch (FormatException)
                {
                    return PasswordVerificationResult.Failed;
                }

                // SECURITY (A02 / CWE-916, CWE-330): enforce the mandated PBKDF2 cost/format BEFORE deriving so a
                // tampered or downgraded stored hash (e.g. "PBKDF2$1$...", a short salt, or a 1-byte subkey) can
                // NEVER verify as Success. HashPassword always emits exactly SaltByteSize/SubkeyByteSize at
                // Pbkdf2Iterations, so anything with fewer iterations or a wrong salt/subkey length is invalid.
                // The upper bound blocks a stored-hash CPU denial-of-service via an absurd iteration count. A
                // null password and a zero-length subkey are both rejected here (FixedTimeEquals(empty,empty) is true).
                if (providedPassword == null
                    || salt.Length != SaltByteSize
                    || storedSubkey.Length != SubkeyByteSize
                    || iterations < Pbkdf2Iterations
                    || iterations > Pbkdf2MaxIterations)
                    return PasswordVerificationResult.Failed;

                byte[] computedSubkey = Rfc2898DeriveBytes.Pbkdf2(providedPassword, salt, iterations, HashAlgorithmName.SHA256, SubkeyByteSize);

                // SECURITY (CWE-208): constant-time comparison to avoid timing side-channels.
                if (!CryptographicOperations.FixedTimeEquals(computedSubkey, storedSubkey))
                    return PasswordVerificationResult.Failed;

                // A verified hash whose (still-valid) iteration count differs from the current policy is
                // re-normalized to Pbkdf2Iterations on the next successful login via the rehash signal.
                return iterations == Pbkdf2Iterations
                    ? PasswordVerificationResult.Success
                    : PasswordVerificationResult.SuccessRehashNeeded;
            }

            // Legacy MD5 path: verify against the old digest and signal a rehash on success.
            if (VerifyMd5Hash(providedPassword, storedHash))
                return PasswordVerificationResult.SuccessRehashNeeded;

            return PasswordVerificationResult.Failed;
        }

        private static MD5 md5Hash = MD5.Create();

        // LEGACY / deprecated — retained only for backward-compatible verification via VerifyPassword; do not use for new password storage.
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

        // LEGACY / deprecated — retained only for backward-compatible verification via VerifyPassword; do not use for new password storage.
        internal static bool VerifyMd5Hash(string input, string hash)
        {
            string hashOfInput = GetMd5Hash(input);
            StringComparer comparer = StringComparer.OrdinalIgnoreCase;
            return (0 == comparer.Compare(hashOfInput, hash));
        }

    }
}

