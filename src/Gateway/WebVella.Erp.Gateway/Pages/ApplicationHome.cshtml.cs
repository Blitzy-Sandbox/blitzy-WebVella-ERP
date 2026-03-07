using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using WebVella.Erp.Gateway.Models;
// ValidationException is used from WebVella.Erp.Gateway.Models (Gateway-local definition)
// which matches the type of BaseErpPageModel.Validation property. The SharedKernel version
// (WebVella.Erp.SharedKernel.Exceptions.ValidationException) is structurally identical but
// resides in a different namespace; using the Gateway.Models version avoids CS0104 ambiguity
// and ensures catch blocks match the type thrown within the Gateway page lifecycle.

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Page model for the Application Home page — the landing page when a user
	/// navigates to an application root (e.g., /{AppName}/a/{PageName?}).
	///
	/// Adapted from WebVella.Erp.Web.Pages.Application.ApplicationHomePageModel.
	/// Changes from monolith:
	///   - Namespace: WebVella.Erp.Web.Pages.Application → WebVella.Erp.Gateway.Pages
	///   - Removed all HookManager usage (IPageHook, IApplicationHomePageHook)
	///   - Replaced new Log().Create(LogType.Error, ...) with ILogger.LogError(...)
	///   - Added ILogger&lt;ApplicationHomePageModel&gt; via constructor DI
	///   - Preserved Init()/BeforeRender()/Page()/NotFound() lifecycle
	///   - Preserved ValidationException and generic Exception catch blocks
	/// </summary>
	public class ApplicationHomePageModel : BaseErpPageModel
	{
		private readonly ILogger<ApplicationHomePageModel> _logger;

		/// <summary>
		/// Constructs the Application Home page model with required services.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context providing routing state
		/// (app, area, node, page resolution). Injected via DI.</param>
		/// <param name="logger">Structured logger replacing the monolith's
		/// Diagnostics.Log pattern with standard Microsoft.Extensions.Logging.</param>
		public ApplicationHomePageModel(
			[FromServices] ErpRequestContext reqCtx,
			ILogger<ApplicationHomePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger;
		}

		/// <summary>
		/// Handles HTTP GET requests for the application home page.
		/// Initializes the page context, verifies the resolved page exists,
		/// prepares ViewData for rendering, and returns the Razor Page result.
		/// </summary>
		/// <returns>
		/// The rendered Razor Page on success; NotFound (404) if the resolved
		/// page is null; or a redirect result from Init() if access is denied.
		/// On exception, returns the page with Validation.Message populated.
		/// </returns>
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
				_logger.LogError(ex, "ApplicationHomePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for the application home page.
		/// Validates the antiforgery token via ModelState, initializes the page
		/// context, verifies the resolved page exists, and returns the Razor Page
		/// result.
		/// </summary>
		/// <returns>
		/// The rendered Razor Page on success; NotFound (404) if the resolved
		/// page is null; or a redirect result from Init() if access is denied.
		/// On ValidationException, populates Validation.Message and Validation.Errors.
		/// On generic Exception, logs the error and populates Validation.Message.
		/// </returns>
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
			catch (ValidationException valEx)
			{
				Validation.Message = valEx.Message;
				Validation.Errors.AddRange(valEx.Errors);
				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "ApplicationHomePageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
