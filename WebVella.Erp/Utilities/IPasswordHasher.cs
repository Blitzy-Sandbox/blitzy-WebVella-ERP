using System;

namespace WebVella.Erp.Utilities
{
	/// <summary>
	/// Strategy abstraction for password hashing and verification (OWASP A02/A07).
	/// Implementations MUST use a salted, adaptive key-derivation function for newly created hashes,
	/// while still supporting transparent verification (and upgrade) of legacy hashes for
	/// authentication continuity.
	/// </summary>
	public interface IPasswordHasher
	{
		/// <summary>
		/// Produces a new, versioned, self-describing salted hash string for the supplied plaintext.
		/// Implementations MUST never emit a legacy (unsalted MD5) hash from this method.
		/// </summary>
		/// <param name="plaintext">The plaintext password to hash.</param>
		/// <returns>A self-describing hash string (e.g. "$pbkdf2-sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;").</returns>
		string HashPassword(string plaintext);

		/// <summary>
		/// Verifies a plaintext password against a stored hash.
		/// </summary>
		/// <param name="plaintext">The plaintext password to verify.</param>
		/// <param name="storedHash">
		/// The stored hash. May be the new self-describing format or a legacy MD5 hex string
		/// (32 hexadecimal characters with no scheme prefix).
		/// </param>
		/// <param name="needsUpgrade">
		/// Set to true when verification succeeded but the stored hash is legacy (MD5) or uses a
		/// stale (below-target) work factor, signalling the caller to re-hash and persist the
		/// credential transparently. Only meaningful when the method returns true.
		/// </param>
		/// <returns>True when the plaintext matches the stored hash; otherwise false.</returns>
		bool Verify(string plaintext, string storedHash, out bool needsUpgrade);
	}
}
