using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Razor Page model for the Site page route.
	/// Adapted from WebVella.Erp.Web.Pages.Application.SitePageModel.
	///
	/// Changes from monolith:
	///   - Removed HookManager.GetHookedInstances&lt;IPageHook&gt; and ISitePageHook calls
	///     (hook-based page lifecycle replaced by event-driven architecture)
	///   - Replaced new Log().Create(LogType.Error, ...) with ILogger&lt;SitePageModel&gt;
	///   - Updated namespace from WebVella.Erp.Web.Pages.Application to WebVella.Erp.Gateway.Pages
	///   - Replaced WebVella.Erp.Web.Models imports with WebVella.Erp.Gateway.Models
	/// </summary>
	public class SitePageModel : BaseErpPageModel
	{
		private readonly ILogger<SitePageModel> _logger;

		/// <summary>
		/// Constructs the SitePageModel with required dependencies.
		/// ErpRequestContext is injected via [FromServices] for per-request routing state.
		/// ILogger provides structured logging replacing the monolith's database-backed Log class.
		/// </summary>
		/// <param name="reqCtx">Scoped request context providing page routing state.</param>
		/// <param name="logger">Structured logger for error and diagnostic logging.</param>
		public SitePageModel([FromServices] ErpRequestContext reqCtx, ILogger<SitePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Handles HTTP GET requests for the Site page.
		/// Executes the Init → page existence check → BeforeRender → Page lifecycle.
		/// On error, logs the exception and renders the page with the validation message populated.
		/// </summary>
		/// <returns>The rendered page result or a NotFound result if the page does not exist.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;

				if (ErpRequestContext.Page == null) return NotFound();

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SitePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for the Site page.
		/// Validates antiforgery token via ModelState, then executes the
		/// Init → page existence check → BeforeRender → Page lifecycle.
		/// On error, logs the exception and renders the page with the validation message populated.
		/// </summary>
		/// <returns>The rendered page result, a NotFound result, or an error page with validation messages.</returns>
		public IActionResult OnPost()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");

				var initResult = Init();
				if (initResult != null) return initResult;

				if (ErpRequestContext.Page == null) return NotFound();

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SitePageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
