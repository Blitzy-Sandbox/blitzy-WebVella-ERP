using System;
using System.Security.Cryptography;
using System.Text;

namespace WebVella.Erp.Utilities
{
    /// <summary>
    /// Legacy MD5 hashing helpers retained for backward-compatible verification ONLY.
    /// Do NOT use for new password hashing - use <c>IPasswordHasher</c>/<c>ErpPasswordHasher</c>
    /// (salted PBKDF2). These remain for the transparent MD5-to-KDF migration (existing
    /// users authenticate and are upgraded on next login) and for existing internal callers.
    /// </summary>
    public static class PasswordUtil
    {
        private static MD5 md5Hash = MD5.Create();

        /// <summary>
        /// Computes a lowercase-hex MD5 hash of the UTF-8 encoded input.
        /// Legacy/compatibility hashing ONLY - retained for verifying pre-existing MD5
        /// credentials during the MD5-to-KDF migration. Do NOT use for new password hashing.
        /// </summary>
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
        /// Verifies a plaintext value against a stored legacy MD5 hash using a
        /// case-insensitive comparison. Used by the <c>ErpPasswordHasher</c> legacy
        /// verification branch to authenticate existing MD5 users during migration.
        /// </summary>
        internal static bool VerifyMd5Hash(string input, string hash)
        {
            string hashOfInput = GetMd5Hash(input);
            StringComparer comparer = StringComparer.OrdinalIgnoreCase;
            return (0 == comparer.Compare(hashOfInput, hash));
        }

    }
}

