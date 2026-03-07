using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Logout page model for the Gateway/BFF layer.
	/// Adapted from WebVella.Erp.Web/Pages/logout.cshtml.cs.
	///
	/// Changes from monolith:
	///   - Replaced authService.Logout() with ASP.NET Core
	///     HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
	///     to clear the authentication cookie directly.
	///   - Removed all HookManager usage (IPageHook, ILogoutPageHook) — hook-based
	///     lifecycle events are now handled by backend services via the event bus.
	///   - Added ILogger&lt;LogoutModel&gt; for structured logging replacing the
	///     monolith's Log class diagnostics.
	///   - Namespace changed from WebVella.Erp.Web.Pages to WebVella.Erp.Gateway.Pages.
	///
	/// The [Authorize] attribute is inherited from BaseErpPageModel, ensuring only
	/// authenticated users can reach the logout endpoint.
	/// </summary>
	public class LogoutModel : BaseErpPageModel
	{
		private readonly ILogger<LogoutModel> _logger;

		/// <summary>
		/// Constructs the LogoutModel with required dependencies.
		/// </summary>
		/// <param name="reqCtx">
		/// Scoped per-request context providing routing state (App, SitemapArea,
		/// SitemapNode, Entity, Page). Injected via [FromServices] and assigned
		/// to the inherited ErpRequestContext property from BaseErpPageModel.
		/// </param>
		/// <param name="logger">
		/// Structured logger for recording logout operations and any errors
		/// encountered during the sign-out process.
		/// </param>
		public LogoutModel([FromServices] ErpRequestContext reqCtx, ILogger<LogoutModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger;
		}

		/// <summary>
		/// Handles GET requests to the /logout page.
		/// Initializes the request context, signs out the user by clearing the
		/// authentication cookie, and redirects to the site root.
		///
		/// Adapted from monolith OnGet (source lines 13-34):
		///   - Preserved: Init() call for request context initialization
		///   - Replaced: authService.Logout() → HttpContext.SignOutAsync(...)
		///   - Removed: HookManager.GetHookedInstances&lt;IPageHook&gt; loop
		///   - Removed: HookManager.GetHookedInstances&lt;ILogoutPageHook&gt; loop
		///   - Preserved: LocalRedirectResult("/") redirect after sign-out
		/// </summary>
		/// <returns>A redirect to the site root ("/") after successful sign-out.</returns>
		public async Task<IActionResult> OnGet()
		{
			// Call Init() for side-effects (request context initialization) but
			// intentionally discard the return value — matching the monolith's
			// original behavior where logout always proceeds regardless of whether
			// the URL maps to a valid application. The "/logout" path causes
			// ParseUrlInfo to interpret "logout" as an AppName, which will not
			// resolve to any real application, but that is irrelevant for sign-out.
			try
			{
				Init();
			}
			catch (Exception ex)
			{
				// Init() may fail due to URL parsing or app resolution for the
				// "/logout" path — this is expected and must not prevent sign-out.
				_logger.LogDebug(ex, "Init() encountered an error during logout; proceeding with sign-out.");
			}

			_logger.LogInformation("User logout initiated via GET for user {UserId}.",
				CurrentUser?.Id);

			// Clear the authentication cookie — replaces monolith authService.Logout()
			// which internally called HttpContext.SignOutAsync under the covers.
			// Using CookieAuthenticationDefaults.AuthenticationScheme ("Cookies")
			// to match the monolith's cookie-based authentication scheme.
			await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

			_logger.LogInformation("User logout completed successfully via GET.");

			return new LocalRedirectResult("/");
		}

		/// <summary>
		/// Handles POST requests to the /logout page.
		/// Initializes the request context, signs out the user by clearing the
		/// authentication cookie, and redirects to the site root.
		///
		/// Adapted from monolith OnPost (source lines 36-57):
		///   - Preserved: Init() call for request context initialization
		///   - Replaced: authService.Logout() → HttpContext.SignOutAsync(...)
		///   - Removed: HookManager.GetHookedInstances&lt;IPageHook&gt; loop
		///   - Removed: HookManager.GetHookedInstances&lt;ILogoutPageHook&gt; loop
		///   - Preserved: LocalRedirectResult("/") redirect after sign-out
		/// </summary>
		/// <returns>A redirect to the site root ("/") after successful sign-out.</returns>
		public async Task<IActionResult> OnPost()
		{
			// Call Init() for side-effects but discard the return value —
			// same rationale as OnGet: logout must always proceed.
			try
			{
				Init();
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Init() encountered an error during logout; proceeding with sign-out.");
			}

			_logger.LogInformation("User logout initiated via POST for user {UserId}.",
				CurrentUser?.Id);

			// Clear the authentication cookie — replaces monolith authService.Logout()
			await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

			_logger.LogInformation("User logout completed successfully via POST.");

			return new LocalRedirectResult("/");
		}
	}
}
