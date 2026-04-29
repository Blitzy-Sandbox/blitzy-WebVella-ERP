using System;
using System.Security.Cryptography;
using System.Text;

namespace WebVella.Erp.Utilities
{
    public static class PasswordUtil
    {
        // Security fix: F-002 — PBKDF2-HMAC-SHA-256 parameters replace MD5 (CWE-327, CWE-328, CWE-916).
        private const int Pbkdf2Iterations = 100000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;
        private const string Pbkdf2Prefix = "pbkdf2$";

        /// <summary>
        /// Hashes a plaintext input using PBKDF2-HMAC-SHA-256 (Security fix F-002).
        /// </summary>
        /// <param name="input">The plaintext input to hash.</param>
        /// <returns>
        /// The hash encoded as <c>pbkdf2$&lt;iterations&gt;$&lt;base64-salt&gt;$&lt;base64-hash&gt;</c>,
        /// or <see cref="string.Empty"/> when the input is null/whitespace.
        /// </returns>
        /// <remarks>Method name is preserved for backward compatibility; the implementation now uses PBKDF2-HMAC-SHA-256 (Security fix F-002).</remarks>
        // Security fix: F-002 — Replace MD5 password hashing with PBKDF2-HMAC-SHA-256 (100k iterations, 16-byte CSPRNG salt).
        internal static string GetMd5Hash(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);

            // Security fix: F-002 — Use static Rfc2898DeriveBytes.Pbkdf2 (recommended on .NET 6+, eliminates SYSLIB0060) — preserves PBKDF2-HMAC-SHA-256, 100k iterations, 16-byte CSPRNG salt.
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(input, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);

            return $"{Pbkdf2Prefix}{Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        /// <summary>
        /// Verifies a plaintext input against a stored hash.
        /// Supports both modern PBKDF2 hashes (Security fix F-002) and legacy 32-hex-char MD5 hashes for transparent migration.
        /// </summary>
        /// <param name="input">The plaintext input to verify.</param>
        /// <param name="hash">The stored hash (PBKDF2 or legacy MD5 hex).</param>
        /// <returns><c>true</c> when the input matches the hash; <c>false</c> otherwise.</returns>
        /// <remarks>Method name is preserved for backward compatibility; the implementation now verifies PBKDF2 hashes and accepts legacy MD5 hashes for migration only (Security fix F-002).</remarks>
        // Security fix: F-002 — Verify PBKDF2 hash; accept legacy 32-hex-char MD5 hashes for transparent migration.
        internal static bool VerifyMd5Hash(string input, string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return false;

            // Branch 1: Modern PBKDF2 hash (preferred path)
            if (hash.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
            {
                try
                {
                    string[] parts = hash.Split('$');
                    if (parts.Length != 4)
                        return false;

                    int iterations = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (iterations <= 0)
                        return false;

                    byte[] salt = Convert.FromBase64String(parts[2]);
                    byte[] expected = Convert.FromBase64String(parts[3]);
                    if (expected.Length == 0)
                        return false;

                    // Security fix: F-002 — Use static Rfc2898DeriveBytes.Pbkdf2 (recommended on .NET 6+, eliminates SYSLIB0060) — preserves PBKDF2-HMAC-SHA-256 verification with stored iteration count and salt.
                    byte[] actual = Rfc2898DeriveBytes.Pbkdf2(input ?? string.Empty, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                    return CryptographicOperations.FixedTimeEquals(actual, expected);
                }
                catch (FormatException) { return false; }
                catch (ArgumentException) { return false; }
                catch (IndexOutOfRangeException) { return false; }
                catch (OverflowException) { return false; }
            }

            // Branch 2: Legacy 32-hex-char MD5 hash (transparent migration support).
            // Security fix: F-002 — MD5 retained ONLY for legacy hash compatibility during transparent migration.
            if (hash.Length == 32 && IsHexString(hash))
            {
                if (string.IsNullOrEmpty(input))
                    return false;

                using (var md5 = MD5.Create())
                {
                    byte[] data = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                    var sb = new StringBuilder(32);
                    for (int i = 0; i < data.Length; i++)
                        sb.Append(data[i].ToString("x2"));
                    string legacyHashOfInput = sb.ToString();
                    return string.Equals(legacyHashOfInput, hash, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Branch 3: Unknown format.
            return false;
        }

        private static bool IsHexString(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isHex = (c >= '0' && c <= '9')
                          || (c >= 'a' && c <= 'f')
                          || (c >= 'A' && c <= 'F');
                if (!isHex)
                    return false;
            }
            return true;
        }

    }
}

