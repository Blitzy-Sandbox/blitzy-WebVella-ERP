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

			ErpUser user = authService.Authenticate(Username, Password);

			foreach (ILoginPageHook inst in hookInstances)
			{
				var result = inst.OnPostAfterLogin(user, this);
				if (result != null) return result;
			}

			if (user == null)
			{
				RegisterFailedAttempt(loginKey);
				Error = "Invalid username or password";
				BeforeRender();
				return Page();
			}

			ResetFailedAttempts(loginKey);

			if (!string.IsNullOrWhiteSpace(ReturnUrl))
				return new LocalRedirectResult(ReturnUrl);
			else
				return new LocalRedirectResult("/");

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
