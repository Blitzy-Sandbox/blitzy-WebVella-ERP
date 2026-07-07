using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Concurrent;
using WebVella.Erp.Api.Models;
using WebVella.Erp.Hooks;
using WebVella.Erp.Web.Hooks;
using WebVella.Erp.Web.Models;
using WebVella.Erp.Web.Services;

namespace WebVella.Erp.Web.Pages
{
	[AllowAnonymous]
	public class LoginModel : BaseErpPageModel
	{
		// SECURITY (A07 / CWE-307): throttle credential brute-forcing by locking a submitted identifier after
		// MAX_FAILED_ATTEMPTS failed logins within a rolling LOCKOUT_MINUTES window, WITHOUT leaking account
		// existence (the enumeration-safe "Invalid username or password" message is preserved on every path).
		private const int MAX_FAILED_ATTEMPTS = 5;
		private const int LOCKOUT_MINUTES = 15;
		private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStartUtc)> _failedLoginAttempts
			= new ConcurrentDictionary<string, (int Count, DateTime WindowStartUtc)>();

		[BindProperty]
		public string Username { get; set; }

		[BindProperty]
		public string Password { get; set; }

		[BindProperty(Name = "returnUrl")]
		public new string ReturnUrl { get; set; }

		[BindProperty]
		public string Error { get; set; }

		// SECURITY (A07 / CWE-620 Unverified Password Change; AAP 0.6.1 "force rotation at first login"):
		// inputs for the enforced first-login rotation of the seeded administrator's bootstrap credential
		// (consumed only when MustChangePassword is set on the bootstrap-login path in OnPost).
		[BindProperty]
		public string NewPassword { get; set; }

		[BindProperty]
		public string ConfirmPassword { get; set; }

		// True while the seeded administrator must rotate the bootstrap credential; drives the login view
		// to reveal the new-password fields. Never persisted.
		public bool MustChangePassword { get; set; }

		public string BrandLogo { get; set; }

		public LoginModel([FromServices] ErpRequestContext reqCtx) { ErpRequestContext = reqCtx; }

		public IActionResult OnGet([FromServices] AuthService authService)
		{
			var initResult = Init();
			if (initResult != null) return initResult;
			var globalHookInstances = HookManager.GetHookedInstances<IPageHook>(HookKey);
			foreach (IPageHook inst in globalHookInstances)
			{
				var result = inst.OnGet(this);
				if (result != null) return result;
			}

			if (CurrentUser != null)
			{
				if (!string.IsNullOrWhiteSpace(ReturnUrl))
					return new LocalRedirectResult(ReturnUrl);
				else
					return new LocalRedirectResult("/");
			}

			var appContext = ErpAppContext.Current;
			var currentApp = ErpRequestContext.App;
			var theme = appContext.Theme;
			BrandLogo = theme.BrandLogo;
			if (!String.IsNullOrWhiteSpace(ErpSettings.NavLogoUrl))
			{
				BrandLogo = ErpSettings.NavLogoUrl;
			}
			BeforeRender();
			return Page();
		}

		public IActionResult OnPost([FromServices] AuthService authService)
		{
			if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");

			var initResult = Init();
			if (initResult != null) return initResult;

			var globalHookInstances = HookManager.GetHookedInstances<IPageHook>(HookKey);
			foreach (IPageHook inst in globalHookInstances)
			{
				var result = inst.OnPost(this);
				if (result != null) return result;
			}

			var hookInstances = HookManager.GetHookedInstances<ILoginPageHook>(HookKey);
			try
			{
				foreach (ILoginPageHook inst in hookInstances)
				{
					var result = inst.OnPostPreLogin(this);
					if (result != null) return result;
				}
			}
			catch (Exception ex)
			{
				Error = ex.Message;
				BeforeRender();
				return Page();
			}

			// SECURITY (A07 / CWE-307): before evaluating credentials, refuse the attempt if this identifier has
			// exceeded the failed-attempt threshold within the active lockout window. The identifier is tracked
			// whether or not the account exists, so lockout cannot be used as an account-existence oracle. We do
			// NOT call Authenticate while locked (no credential check, no sign-in).
			string loginKey = (Username ?? string.Empty).Trim().ToLowerInvariant();
			if (IsIdentifierLockedOut(loginKey))
			{
				Error = "Invalid username or password";
				BeforeRender();
				return Page();
			}

			// SECURITY (A07 / CWE-620 Unverified Password Change + CWE-384 Session Fixation / antiforgery integrity):
			// when the submitted password matches the operator-supplied bootstrap secret, the seeded administrator may
			// need a FORCED rotation before ANY application access is granted. On that path we must NOT establish a
			// session yet: signing in first would (a) let the mandatory rotation be bypassed by navigating away from
			// /login (the cookie already grants access), and (b) mint the rotation form's antiforgery token under an
			// anonymous identity while the follow-up POST is authenticated, producing the validation failure (HTTP 400)
			// that blocks the rotation. So on the bootstrap path we VALIDATE WITHOUT signing in (SecurityManager.GetUser
			// performs the SAME PBKDF2 verify + rehash-on-login as Authenticate, only without the sign-in) and defer the
			// sign-in until AFTER a successful rotation. Normal logins are unchanged - they keep the single Authenticate
			// (validate + sign-in) call, with no extra credential check and no behavioral change on the hot path.
			string bootstrapSecret = (WebVella.Erp.ErpSettings.Configuration?["Settings:InitialAdminPassword"] ?? string.Empty).Trim();
			bool deferSignIn = !string.IsNullOrWhiteSpace(bootstrapSecret)
				&& string.Equals((Password ?? string.Empty).Trim(), bootstrapSecret, StringComparison.Ordinal);

			ErpUser user = deferSignIn
				? new WebVella.Erp.Api.SecurityManager().GetUser(Username, Password)   // validate only - NO session yet
				: authService.Authenticate(Username, Password);                        // validate + sign in (normal path)

			foreach (ILoginPageHook inst in hookInstances)
			{
				var result = inst.OnPostAfterLogin(user, this);
				if (result != null) return result;
			}

			// SECURITY (A07): reject unknown/invalid credentials AND disabled accounts. On the normal path
			// Authenticate already returns null for a disabled user; on the deferred-sign-in (bootstrap) path
			// GetUser does not gate on Enabled, so this explicit check enforces the same rule on both paths.
			if (user == null || !user.Enabled)
			{
				RegisterFailedAttempt(loginKey);
				Error = "Invalid username or password";
				BeforeRender();
				return Page();
			}

			ResetFailedAttempts(loginKey);

			// SECURITY (A07 Identification & Authentication Failures - CWE-620 Unverified Password Change;
			// AAP 0.6.1 "force rotation at first login"): the seeded administrator's bootstrap credential
			// (Settings:InitialAdminPassword) is a shared, operator-known secret and must be rotated BEFORE it
			// grants normal application access. Detection is STATELESS and self-clearing - the just-authenticated
			// credential is compared (ordinal) to the configured bootstrap value, so once the operator sets a
			// different password this branch is never taken again (no user-schema/preferences change required).
			if (RequiresFirstLoginRotation(user))
			{
				MustChangePassword = true;

				// Step 1 - no replacement supplied yet: reveal the rotation fields and stop BEFORE app access.
				if (string.IsNullOrWhiteSpace(NewPassword) || string.IsNullOrWhiteSpace(ConfirmPassword))
				{
					Error = "You must set a new administrator password before continuing.";
					BeforeRender();
					return Page();
				}

				string proposed = NewPassword.Trim();
				string bootstrap = (WebVella.Erp.ErpSettings.Configuration?["Settings:InitialAdminPassword"] ?? string.Empty).Trim();

				// Enforce the enterprise length policy (12+, capped at the user password field maximum of 24),
				// confirm-match, and reject reuse of the bootstrap secret (the rotation must actually change it).
				if (proposed.Length < 12 || proposed.Length > 24 || !string.Equals(proposed, ConfirmPassword.Trim(), StringComparison.Ordinal))
				{
					Error = "The new password must be 12-24 characters and match the confirmation.";
					BeforeRender();
					return Page();
				}
				if (proposed.Length == bootstrap.Length && string.Equals(proposed, bootstrap, StringComparison.Ordinal))
				{
					Error = "The new password must be different from the initial bootstrap password.";
					BeforeRender();
					return Page();
				}

				// Persist through the existing credential path - RecordManager PBKDF2-hashes the password field on
				// update (identical to the create path), so the rotated secret is salted+iterated, never plaintext.
				try
				{
					// No session has been established on this deferred-sign-in path, so the CURRENT request context is
					// anonymous (not elevated); open a system scope so the RecordManager permission check on the user
					// entity update (EntityPermission.Update) passes - mirroring SecurityManager.GetUser, which also
					// runs system-scoped. The sign-in is performed only AFTER this rotation succeeds (see below).
					using (WebVella.Erp.Api.SecurityContext.OpenSystemScope())
					{
						var securityManager = new WebVella.Erp.Api.SecurityManager();
						var adminUser = securityManager.GetUser(user.Id);
						if (adminUser != null)
						{
							adminUser.Password = proposed;
							securityManager.SaveUser(adminUser);
						}
					}
				}
				catch (Exception)
				{
					Error = "Could not update the administrator password. Please try again.";
					BeforeRender();
					return Page();
				}
				MustChangePassword = false;
				// rotation complete -> the freshly-persisted secret becomes the credential to sign in with. 'Password'
				// still held the OLD bootstrap value (which no longer verifies), so point it at the new secret for the
				// deferred sign-in below. GetUser re-reads the just-saved PBKDF2 hash from the DB (no cache), so the
				// deferred Authenticate validates the new credential and establishes the session seamlessly.
				Password = proposed;
				// fall through to the deferred sign-in + redirect below.
			}

			// SECURITY (A07): the deferred-sign-in (bootstrap) path has not established a session yet. Now that either
			// (a) the mandatory rotation completed - 'Password' holds the freshly-persisted new secret - or (b) no
			// rotation was required (a non-admin whose password merely equals the bootstrap value), establish the
			// session. GetUser above already validated the ORIGINAL credential, so a null here signals only a genuine
			// persist/read problem for the rotated hash, surfaced via the enumeration-safe message.
			if (deferSignIn)
			{
				if (authService.Authenticate(Username, Password) == null)
				{
					Error = "Invalid username or password";
					BeforeRender();
					return Page();
				}
			}

			if (!string.IsNullOrWhiteSpace(ReturnUrl))
				return new LocalRedirectResult(ReturnUrl);
			else
				return new LocalRedirectResult("/");

		}

		// SECURITY (A07): true ONLY while the seeded first administrator (SystemIds.FirstUserId) is signing
		// in with the still-unchanged operator-supplied bootstrap password. Stateless and self-clearing: it
		// never forces rotation for a normal user, and once the admin's stored password differs from the
		// configured bootstrap value the check returns false. Because 'user' has just authenticated with
		// 'Password', that value IS the account's current credential, so an ordinal equality check suffices.
		private bool RequiresFirstLoginRotation(ErpUser user)
		{
			if (user == null || user.Id != WebVella.Erp.Api.SystemIds.FirstUserId)
				return false;
			var bootstrap = WebVella.Erp.ErpSettings.Configuration?["Settings:InitialAdminPassword"];
			if (string.IsNullOrWhiteSpace(bootstrap))
				return false;
			return string.Equals((Password ?? string.Empty).Trim(), bootstrap.Trim(), StringComparison.Ordinal);
		}

		private static bool IsIdentifierLockedOut(string key)
		{
			if (string.IsNullOrEmpty(key)) return false;
			if (_failedLoginAttempts.TryGetValue(key, out var entry))
			{
				// Expired window: clear and treat as not locked.
				if (DateTime.UtcNow - entry.WindowStartUtc > TimeSpan.FromMinutes(LOCKOUT_MINUTES))
				{
					_failedLoginAttempts.TryRemove(key, out _);
					return false;
				}
				return entry.Count >= MAX_FAILED_ATTEMPTS;
			}
			return false;
		}

		private static void RegisterFailedAttempt(string key)
		{
			if (string.IsNullOrEmpty(key)) return;
			_failedLoginAttempts.AddOrUpdate(
				key,
				(1, DateTime.UtcNow),
				(k, existing) =>
				{
					// Roll the window if the previous one has expired; otherwise increment within it.
					if (DateTime.UtcNow - existing.WindowStartUtc > TimeSpan.FromMinutes(LOCKOUT_MINUTES))
						return (1, DateTime.UtcNow);
					return (existing.Count + 1, existing.WindowStartUtc);
				});
		}

		private static void ResetFailedAttempts(string key)
		{
			if (string.IsNullOrEmpty(key)) return;
			_failedLoginAttempts.TryRemove(key, out _);
		}
	}
}
/*
 * system actions: OnPost: success,error
 * custom actions: none
 */
