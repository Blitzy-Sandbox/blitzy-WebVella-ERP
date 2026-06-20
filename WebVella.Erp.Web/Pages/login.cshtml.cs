using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
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

		//Security (A04 Insecure Design; CWE-307/CWE-799): per-account failed-login lockout/throttling state and
		//logic now live in the shared WebVella.Erp.Web.Services.LoginAttemptTracker, so this interactive Razor
		//login and the JWT token endpoints (WebApiController) enforce ONE coherent lockout. Defense-in-depth
		//complement to the per-host ASP.NET Core rate limiter registered in WebVella.Erp.Site*/Startup.cs.

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
			string lockoutKey = LoginAttemptTracker.BuildKey(Username);
			if (LoginAttemptTracker.IsLockedOut(lockoutKey))
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
				//Security (A04): count this failed attempt; the 5th failure trips the lockout window.
				LoginAttemptTracker.RegisterFailedAttempt(lockoutKey);
				Error = "Invalid username or password";
				BeforeRender();
				return Page();
			}

			//Security (A04): successful authentication clears the failed-attempt counter for this account.
			LoginAttemptTracker.Reset(lockoutKey);

			if (!string.IsNullOrWhiteSpace(ReturnUrl))
				return new LocalRedirectResult(ReturnUrl);
			else
				return new LocalRedirectResult("/");

		}

	}
}
/*
 * system actions: OnPost: success,error
 * custom actions: none
 */
