using System;
using System.Security.Cryptography;
using System.Text;

namespace WebVella.Erp.Utilities
{
	/// <summary>
	/// Default <see cref="IPasswordHasher"/> implementation using salted PBKDF2-HMAC-SHA256
	/// (OWASP A02/A07, CWE-327/CWE-916). New hashes use the self-describing format
	/// "$pbkdf2-sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;".
	/// Legacy unsalted MD5 hashes (32 hex characters, no scheme prefix) are verified for
	/// backward compatibility and flagged for transparent upgrade on next successful login.
	/// </summary>
	public class ErpPasswordHasher : IPasswordHasher
	{
		// Scheme identifier and tuning parameters.
		// Iteration count follows OWASP guidance for PBKDF2-HMAC-SHA256 (>= 600,000).
		private const string Pbkdf2SchemeName = "pbkdf2-sha256";
		private const int DefaultIterations = 600000;
		// SECURITY (OWASP A02/A07 - CWE-400): defensive upper bound on the iteration count read from a stored hash.
		// A crafted/corrupt stored hash carrying an enormous iteration count would otherwise amplify CPU work on the
		// verification path (resource-exhaustion DoS). The cap is far above the 600,000 target, so legitimate future
		// increases to DefaultIterations continue to verify successfully.
		private const int MaxIterations = 10_000_000;
		private const int SaltSizeInBytes = 16;   // 128-bit salt from a CSPRNG
		private const int HashSizeInBytes = 32;   // 256-bit derived key (SHA-256 output)

		/// <summary>
		/// Shared stateless default instance for core-library consumers that are instantiated with
		/// <c>new</c> (e.g. SecurityManager, RecordManager) and cannot use dependency injection.
		/// The Web layer registers this type in DI separately via
		/// <c>services.AddSingleton&lt;IPasswordHasher, ErpPasswordHasher&gt;()</c>.
		/// </summary>
		public static readonly ErpPasswordHasher Default = new ErpPasswordHasher();

		/// <summary>
		/// Public parameterless constructor (required for the static <see cref="Default"/> instance
		/// and for DI registration in the Web layer).
		/// </summary>
		public ErpPasswordHasher()
		{
		}

		/// <inheritdoc />
		public string HashPassword(string plaintext)
		{
			if (plaintext == null)
				throw new ArgumentNullException(nameof(plaintext));

			byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeInBytes);
			byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
				plaintext,
				salt,
				DefaultIterations,
				HashAlgorithmName.SHA256,
				HashSizeInBytes);

			return string.Concat(
				"$", Pbkdf2SchemeName,
				"$", DefaultIterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
				"$", Convert.ToBase64String(salt),
				"$", Convert.ToBase64String(hash));
		}

		/// <inheritdoc />
		public bool Verify(string plaintext, string storedHash, out bool needsUpgrade)
		{
			needsUpgrade = false;

			if (plaintext == null || string.IsNullOrWhiteSpace(storedHash))
				return false;

			// Legacy unsalted MD5: 32 hex characters with no scheme prefix.
			// Verify via the retained helper and flag for transparent upgrade.
			if (IsLegacyMd5(storedHash))
			{
				bool legacyOk = PasswordUtil.VerifyMd5Hash(plaintext, storedHash);
				needsUpgrade = legacyOk;
				return legacyOk;
			}

			// New self-describing format: $pbkdf2-sha256$<iterations>$<base64 salt>$<base64 hash>
			if (storedHash[0] != '$')
				return false;

			string[] parts = storedHash.Split('$');
			// parts[0] is empty (leading '$'); expected layout: ["", scheme, iterations, salt, hash]
			if (parts.Length != 5)
				return false;

			if (!string.Equals(parts[1], Pbkdf2SchemeName, StringComparison.Ordinal))
				return false;

			if (!int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int iterations) || iterations <= 0)
				return false;

			// SECURITY (CWE-400): reject an unreasonably high iteration count BEFORE performing any key derivation,
			// so a crafted/corrupt stored hash cannot force CPU-amplified work on the verification path.
			if (iterations > MaxIterations)
				return false;

			byte[] salt;
			byte[] expectedHash;
			try
			{
				salt = Convert.FromBase64String(parts[3]);
				expectedHash = Convert.FromBase64String(parts[4]);
			}
			catch (FormatException)
			{
				return false;
			}

			// SECURITY (CWE-916): enforce the self-describing format invariants — a 16-byte salt and a 32-byte derived
			// key. Rejecting any other length neutralizes a crafted stored hash that uses a very short expected hash
			// (e.g. 1 byte) to weaken verification. This is safe for authentication continuity because HashPassword has
			// only ever emitted 16-byte salts and 32-byte hashes (legacy MD5 is handled by the earlier branch).
			if (salt.Length != SaltSizeInBytes || expectedHash.Length != HashSizeInBytes)
				return false;

			// Always derive exactly HashSizeInBytes (32); never trust a length taken from the stored value.
			byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
				plaintext,
				salt,
				iterations,
				HashAlgorithmName.SHA256,
				HashSizeInBytes);

			// Constant-time comparison to avoid timing side-channels.
			bool ok = CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);

			// Flag a stale (below-target) work factor for transparent upgrade on next successful login.
			if (ok && iterations < DefaultIterations)
				needsUpgrade = true;

			return ok;
		}

		/// <summary>
		/// Returns true when the stored value looks like a legacy unsalted MD5 hash
		/// (exactly 32 hexadecimal characters, no scheme prefix).
		/// </summary>
		private static bool IsLegacyMd5(string storedHash)
		{
			if (string.IsNullOrEmpty(storedHash) || storedHash.Length != 32 || storedHash[0] == '$')
				return false;

			for (int i = 0; i < storedHash.Length; i++)
			{
				if (!Uri.IsHexDigit(storedHash[i]))
					return false;
			}

			return true;
		}
	}
}
