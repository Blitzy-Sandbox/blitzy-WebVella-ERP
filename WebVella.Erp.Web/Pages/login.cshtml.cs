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
		[BindProperty]
		public string Username { get; set; }

		[BindProperty]
		public string Password { get; set; }

		[BindProperty(Name = "returnUrl")]
		public new string ReturnUrl { get; set; }

		[BindProperty]
		public string Error { get; set; }

		public string BrandLogo { get; set; }

		//Security (A04 Insecure Design; CWE-307/CWE-799): in-process, per-account failed-login tracking for lockout/throttling.
		//Defense-in-depth complement to the per-host ASP.NET Core rate limiter registered in WebVella.Erp.Site*/Startup.cs.
		private const int MAX_FAILED_LOGIN_ATTEMPTS = 5;
		private const double LOCKOUT_DURATION_MINUTES = 15;
		private static readonly ConcurrentDictionary<string, (int Count, DateTime? LockoutUntilUtc)> _failedLoginAttempts
			= new ConcurrentDictionary<string, (int Count, DateTime? LockoutUntilUtc)>();

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

			//Security (A04; CWE-307/CWE-799): per-account lockout pre-check (defense-in-depth with the per-host rate limiter).
			//If currently locked, do NOT call Authenticate; return a generic, non-enumerating message
			//(do not reveal remaining attempts or whether the account exists).
			string lockoutKey = BuildLockoutKey(Username);
			if (IsLockedOut(lockoutKey))
			{
				Error = "Too many failed login attempts. Please try again later.";
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
				//Security (A04): count this failed attempt; the MAX_FAILED_LOGIN_ATTEMPTS-th trips the lockout window.
				RegisterFailedLoginAttempt(lockoutKey);
				Error = "Invalid username or password";
				BeforeRender();
				return Page();
			}

			//Security (A04): successful authentication clears the failed-attempt counter for this account.
			ResetFailedLoginAttempts(lockoutKey);

			if (!string.IsNullOrWhiteSpace(ReturnUrl))
				return new LocalRedirectResult(ReturnUrl);
			else
				return new LocalRedirectResult("/");

		}

		private static string BuildLockoutKey(string username)
		{
			//Normalize the identity so case/whitespace variants map to the same bucket. Generic by design:
			//we track by username regardless of whether the account exists, to avoid user enumeration.
			//(Optionally combine with HttpContext.Connection.RemoteIpAddress for an IP-scoped key.)
			return (username ?? string.Empty).Trim().ToLowerInvariant();
		}

		private static bool IsLockedOut(string key)
		{
			if (string.IsNullOrEmpty(key))
				return false;

			if (_failedLoginAttempts.TryGetValue(key, out var entry) && entry.LockoutUntilUtc.HasValue)
			{
				if (DateTime.UtcNow < entry.LockoutUntilUtc.Value)
					return true;

				//Lockout window elapsed — opportunistically drop the stale entry to bound memory growth.
				_failedLoginAttempts.TryRemove(key, out _);
			}

			return false;
		}

		private static void RegisterFailedLoginAttempt(string key)
		{
			if (string.IsNullOrEmpty(key))
				return;

			_failedLoginAttempts.AddOrUpdate(
				key,
				_ => (1, (DateTime?)null),
				(_, existing) =>
				{
					int newCount = existing.Count + 1;
					DateTime? lockoutUntil = newCount >= MAX_FAILED_LOGIN_ATTEMPTS
						? DateTime.UtcNow.AddMinutes(LOCKOUT_DURATION_MINUTES)
						: existing.LockoutUntilUtc;
					return (newCount, lockoutUntil);
				});
		}

		private static void ResetFailedLoginAttempts(string key)
		{
			if (string.IsNullOrEmpty(key))
				return;

			_failedLoginAttempts.TryRemove(key, out _);
		}


	}
}
/*
 * system actions: OnPost: success,error
 * custom actions: none
 */
