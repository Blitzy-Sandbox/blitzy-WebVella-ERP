using System;
using System.Collections.Concurrent;

namespace WebVella.Erp.Web.Services
{
	// SECURITY (OWASP A04 Insecure Design / A07 Authentication Failures; CWE-307 Improper Restriction of
	// Excessive Authentication Attempts / CWE-799 Improper Control of Interaction Frequency):
	// In-process, per-account failed-login tracking that provides account lockout / throttling. It is a
	// defense-in-depth complement to the per-host ASP.NET Core rate limiter registered in
	// WebVella.Erp.Site*/Startup.cs.
	//
	// This logic was originally embedded in the Razor login page (WebVella.Erp.Web/Pages/login.cshtml.cs).
	// It is centralized here so EVERY credential-verifying entry point shares ONE lockout state, in
	// particular both:
	//   * the interactive Razor login (LoginModel.OnPost), and
	//   * the JWT token issuance endpoints (WebApiController.GetJwtToken / GetNewJwtToken).
	// Sharing a single static store means an attacker cannot sidestep the Razor lockout by switching to the
	// JWT endpoint (and vice versa) — the failed-attempt counters and lockout windows are the same.
	//
	// NOTE (known limitation, documented in SECURITY.md): the store is per-process / per-instance and is not
	// distributed across a multi-instance deployment. That is acceptable as defense-in-depth alongside the
	// host rate limiter; a distributed store would be a separate enhancement beyond this security remediation.
	public static class LoginAttemptTracker
	{
		// Lockout policy (User Example 2: lockout after 5 failed attempts).
		private const int MAX_FAILED_LOGIN_ATTEMPTS = 5;
		private const double LOCKOUT_DURATION_MINUTES = 15;

		// SECURITY (A04/A05; CWE-400 Uncontrolled Resource Consumption): hard upper bound on the number of
		// distinct accounts tracked at once, so a flood of unique usernames (each with sub-threshold failures
		// that never trip a lockout) cannot grow this in-process map without limit. Paired with the time-based
		// eviction in CleanupIfNeeded below.
		private const int MAX_TRACKED_ACCOUNTS = 10000;

		// Value tuple carries LastSeenUtc so idle sub-threshold counters can be aged out (not just elapsed lockouts).
		private static readonly ConcurrentDictionary<string, (int Count, DateTime? LockoutUntilUtc, DateTime LastSeenUtc)> _failedLoginAttempts
			= new ConcurrentDictionary<string, (int Count, DateTime? LockoutUntilUtc, DateTime LastSeenUtc)>();

		// Throttle so the bounded sweep runs at most ~once per minute (or immediately when over the hard cap),
		// keeping the hot login path light under normal load. The sweep itself is guarded by _cleanupLock.
		private static readonly object _cleanupLock = new object();
		private static DateTime _lastCleanupUtc = DateTime.MinValue;

		/// <summary>
		/// Normalizes the identity so case/whitespace variants map to the same bucket. Generic by design:
		/// callers track by username/email regardless of whether the account exists, to avoid user enumeration.
		/// (Optionally combine with the remote IP for an IP-scoped key.)
		/// </summary>
		public static string BuildKey(string identity)
		{
			return (identity ?? string.Empty).Trim().ToLowerInvariant();
		}

		/// <summary>
		/// Returns true while the supplied key is within an active lockout window. Opportunistically drops a
		/// stale entry once its lockout window has elapsed, to bound memory growth.
		/// </summary>
		public static bool IsLockedOut(string key)
		{
			if (string.IsNullOrEmpty(key))
				return false;

			if (_failedLoginAttempts.TryGetValue(key, out var entry) && entry.LockoutUntilUtc.HasValue)
			{
				if (DateTime.UtcNow < entry.LockoutUntilUtc.Value)
					return true;

				// Lockout window elapsed — opportunistically drop the stale entry to bound memory growth.
				_failedLoginAttempts.TryRemove(key, out _);
			}

			return false;
		}

		/// <summary>
		/// Records one failed authentication attempt for the supplied key. The
		/// MAX_FAILED_LOGIN_ATTEMPTS-th failure trips the lockout window.
		/// </summary>
		public static void RegisterFailedAttempt(string key)
		{
			if (string.IsNullOrEmpty(key))
				return;

			DateTime nowUtc = DateTime.UtcNow;

			// SECURITY (A04/CWE-400): bound memory before inserting so a flood of unique usernames cannot grow this map without limit.
			CleanupIfNeeded(nowUtc);

			_failedLoginAttempts.AddOrUpdate(
				key,
				_ => (1, (DateTime?)null, nowUtc),
				(_, existing) =>
				{
					int newCount = existing.Count + 1;
					DateTime? lockoutUntil = newCount >= MAX_FAILED_LOGIN_ATTEMPTS
						? nowUtc.AddMinutes(LOCKOUT_DURATION_MINUTES)
						: existing.LockoutUntilUtc;
					return (newCount, lockoutUntil, nowUtc);
				});
		}

		/// <summary>
		/// Clears the failed-attempt counter for the supplied key. Call on successful authentication.
		/// </summary>
		public static void Reset(string key)
		{
			if (string.IsNullOrEmpty(key))
				return;

			_failedLoginAttempts.TryRemove(key, out _);
		}

		// SECURITY (A04/A05; CWE-400): bounded, allocation-light eviction that keeps the in-process tracker from
		// growing without limit. Throttled to ~once per minute unless the hard cap is exceeded. The sweep first
		// ages out entries whose lockout window or idle window has elapsed, then — only if still over the cap —
		// sheds counters that are not currently in an active lockout (the cheap sub-threshold entries an attacker
		// floods), always preserving active lockouts where possible. Uses only BCL primitives (no cache/DI).
		private static void CleanupIfNeeded(DateTime nowUtc)
		{
			bool overCap = _failedLoginAttempts.Count > MAX_TRACKED_ACCOUNTS;
			if (!overCap && (nowUtc - _lastCleanupUtc) < TimeSpan.FromMinutes(1))
				return;

			lock (_cleanupLock)
			{
				// Re-check under the lock so concurrent callers don't each run a full sweep.
				overCap = _failedLoginAttempts.Count > MAX_TRACKED_ACCOUNTS;
				if (!overCap && (nowUtc - _lastCleanupUtc) < TimeSpan.FromMinutes(1))
					return;
				_lastCleanupUtc = nowUtc;

				// Pass 1 — age out expired entries: elapsed lockouts, and idle sub-threshold counters whose last
				// failure is older than the lockout window (so a stale single failure cannot linger indefinitely).
				foreach (var pair in _failedLoginAttempts)
				{
					var entry = pair.Value;
					bool expired = entry.LockoutUntilUtc.HasValue
						? nowUtc >= entry.LockoutUntilUtc.Value
						: nowUtc >= entry.LastSeenUtc.AddMinutes(LOCKOUT_DURATION_MINUTES);
					if (expired)
						_failedLoginAttempts.TryRemove(pair.Key, out _);
				}

				// Pass 2 — if still over the hard cap, shed entries that are not in an active lockout.
				if (_failedLoginAttempts.Count > MAX_TRACKED_ACCOUNTS)
				{
					foreach (var pair in _failedLoginAttempts)
					{
						if (_failedLoginAttempts.Count <= MAX_TRACKED_ACCOUNTS)
							break;
						bool activelyLocked = pair.Value.LockoutUntilUtc.HasValue && nowUtc < pair.Value.LockoutUntilUtc.Value;
						if (!activelyLocked)
							_failedLoginAttempts.TryRemove(pair.Key, out _);
					}
				}

				// Pass 3 — pathological worst case (active lockouts alone exceed the cap): enforce the bound anyway.
				if (_failedLoginAttempts.Count > MAX_TRACKED_ACCOUNTS)
				{
					foreach (var pair in _failedLoginAttempts)
					{
						if (_failedLoginAttempts.Count <= MAX_TRACKED_ACCOUNTS)
							break;
						_failedLoginAttempts.TryRemove(pair.Key, out _);
					}
				}
			}
		}
	}
}
