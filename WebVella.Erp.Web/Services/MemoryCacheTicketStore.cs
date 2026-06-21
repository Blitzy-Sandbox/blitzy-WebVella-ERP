using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;

namespace WebVella.Erp.Web.Services
{
	/// <summary>
	/// Server-side authentication ticket store backed by <see cref="IMemoryCache"/>.
	///
	/// SECURITY (OWASP A07 - Identification &amp; Authentication Failures / CWE-613 Insufficient Session Expiration,
	/// addressing the QA finding "Logout does not invalidate replayed authentication cookies"):
	/// By default the ASP.NET Core cookie handler serializes the entire <see cref="AuthenticationTicket"/> into the
	/// authentication cookie itself (a self-contained, encrypted token). In that mode a call to
	/// <c>SignOutAsync</c> only instructs the browser to delete its copy of the cookie - it has no way to revoke a
	/// ticket that an attacker (or the original user) already captured, so a replayed pre-logout cookie keeps
	/// decrypting to a valid principal until its <see cref="AuthenticationProperties.ExpiresUtc"/> elapses.
	///
	/// When an <see cref="ITicketStore"/> is configured as the cookie handler's <c>SessionStore</c>, the ticket
	/// payload is held server-side under an opaque session key and the cookie carries only that key. Sign-in calls
	/// <see cref="StoreAsync(AuthenticationTicket)"/>; sign-out calls <see cref="RemoveAsync(string)"/>, which deletes
	/// the server-side entry. A replayed cookie then resolves to a key that no longer exists, the handler's
	/// <see cref="RetrieveAsync(string)"/> returns <c>null</c>, and the request is treated as unauthenticated -
	/// giving logout true server-side session invalidation.
	///
	/// This store is registered once in <c>ErpMvcServicesExtensions.AddErp()</c> and attached to the default cookie
	/// scheme there, so all WebVella.Erp.Site* hosts inherit identical behavior without per-host wiring.
	///
	/// Design notes / trade-offs (intentional, behavior-preserving per the minimal-change clause):
	/// <list type="bullet">
	/// <item><description>In-memory, per-process: this matches the existing single-process hosting model and the
	/// per-host cookie names (erp_auth_base, erp_auth_sdk, ...). It introduces no database schema change.</description></item>
	/// <item><description>A process restart / redeploy clears the cache and forces one re-login. That is a
	/// security-positive side effect, not a functional regression.</description></item>
	/// <item><description>Each entry's absolute expiration mirrors the ticket's own <c>ExpiresUtc</c>, so server-side
	/// entries never outlive the cookie lifetime (bounded to <c>COOKIE_EXPIRY_DURATION_MINUTES</c>) and the cache
	/// self-evicts abandoned sessions without unbounded growth.</description></item>
	/// </list>
	/// </summary>
	public sealed class MemoryCacheTicketStore : ITicketStore
	{
		// Namespacing prefix so ticket entries never collide with other consumers of the shared IMemoryCache
		// (e.g. WebVella.Erp.Api.Cache / DataSourceManager).
		private const string KeyPrefix = "erp-auth-ticket-";

		// Defensive fallback lifetime applied only if a ticket somehow carries no ExpiresUtc. WebVella tickets always
		// set ExpiresUtc (AuthService.Authenticate sets it to UtcNow + COOKIE_EXPIRY_DURATION_MINUTES = 480 min), so
		// this is a safety net that prevents an entry from living forever, never the normal path.
		private static readonly TimeSpan DefaultEntryLifetime = TimeSpan.FromMinutes(480);

		private readonly IMemoryCache _cache;

		public MemoryCacheTicketStore(IMemoryCache cache)
		{
			_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		}

		/// <summary>
		/// Persists a new ticket server-side under a freshly generated opaque key and returns that key, which the
		/// cookie handler then stores in the (small) authentication cookie in place of the full ticket payload.
		/// </summary>
		public Task<string> StoreAsync(AuthenticationTicket ticket)
		{
			if (ticket == null)
				throw new ArgumentNullException(nameof(ticket));

			// Cryptographically-unique, unguessable session key. Guid.NewGuid is sufficient as an opaque cache key
			// because the value is never exposed in plaintext to the client - it travels inside the encrypted,
			// signed authentication cookie - and authorization still depends on the server-side entry existing.
			string key = KeyPrefix + Guid.NewGuid().ToString("N");
			SetEntry(key, ticket);
			return Task.FromResult(key);
		}

		/// <summary>
		/// Replaces the ticket stored under an existing key (used by the cookie handler when it slides/renews a
		/// ticket, e.g. on AllowRefresh), preserving the same session key.
		/// </summary>
		public Task RenewAsync(string key, AuthenticationTicket ticket)
		{
			if (string.IsNullOrEmpty(key))
				throw new ArgumentNullException(nameof(key));
			if (ticket == null)
				throw new ArgumentNullException(nameof(ticket));

			SetEntry(key, ticket);
			return Task.CompletedTask;
		}

		/// <summary>
		/// Resolves a session key back to its ticket. Returns <c>null</c> when the key is unknown or has been
		/// removed (e.g. after logout) or expired - which is exactly what causes a replayed post-logout cookie to be
		/// rejected as unauthenticated.
		/// </summary>
		public Task<AuthenticationTicket> RetrieveAsync(string key)
		{
			AuthenticationTicket ticket = null;
			if (!string.IsNullOrEmpty(key))
				_cache.TryGetValue(key, out ticket);
			return Task.FromResult(ticket);
		}

		/// <summary>
		/// Destroys the server-side ticket for a session key. Invoked by the cookie handler during
		/// <c>SignOutAsync</c>, this is the operation that makes logout invalidate the session so the cookie can no
		/// longer be replayed.
		/// </summary>
		public Task RemoveAsync(string key)
		{
			if (!string.IsNullOrEmpty(key))
				_cache.Remove(key);
			return Task.CompletedTask;
		}

		// Writes/overwrites a cache entry, anchoring its absolute expiration to the ticket's own ExpiresUtc so the
		// server-side session can never outlive the cookie it backs.
		private void SetEntry(string key, AuthenticationTicket ticket)
		{
			var options = new MemoryCacheEntryOptions();

			DateTimeOffset? expiresUtc = ticket.Properties?.ExpiresUtc;
			if (expiresUtc.HasValue)
				options.SetAbsoluteExpiration(expiresUtc.Value);
			else
				options.SetAbsoluteExpiration(DefaultEntryLifetime);

			_cache.Set(key, ticket, options);
		}
	}
}
