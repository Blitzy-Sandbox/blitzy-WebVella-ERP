using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Razor Page model for application node pages.
	/// Adapted from WebVella.Erp.Web.Pages.Application.ApplicationNodePageModel
	/// with hook-based processing removed in favor of the Gateway/BFF pattern.
	///
	/// The hookKey query parameter is preserved for backward compatibility.
	/// In the monolith, hookKey was used to locate specific IPageHook and
	/// IApplicationNodePageHook instances via HookManager.GetHookedInstances.
	/// In the Gateway, the parameter is read and logged but hook execution
	/// is replaced by event-driven communication with backend microservices.
	/// </summary>
	public class ApplicationNodePageModel : BaseErpPageModel
	{
		private readonly ILogger<ApplicationNodePageModel> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="ApplicationNodePageModel"/> class.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context providing routing state.</param>
		/// <param name="logger">Logger for structured error and diagnostic logging.</param>
		public ApplicationNodePageModel(
			[FromServices] ErpRequestContext reqCtx,
			ILogger<ApplicationNodePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger;
		}

		/// <summary>
		/// Handles HTTP GET requests for application node pages.
		/// Initializes the page model, checks for page existence, reads the
		/// hookKey query parameter for backward compatibility, and renders the page.
		///
		/// Adapted from monolith source lines 20-56. All HookManager calls to
		/// IPageHook and IApplicationNodePageHook have been removed. The hookKey
		/// query parameter is preserved and can be forwarded to backend services.
		/// </summary>
		/// <returns>The page result, a redirect from Init(), or a not-found result.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Preserve hookKey query parameter reading for backward compatibility.
				// In the monolith, this value was passed to:
				//   HookManager.GetHookedInstances<IPageHook>(hookKey)
				//   HookManager.GetHookedInstances<IApplicationNodePageHook>(hookKey)
				// In the Gateway, hookKey is read and logged for diagnostic traceability.
				// The HookKey property (inherited from BaseErpPageModel) provides the
				// same value and can be forwarded to backend services as a query parameter.
				string hookKey = string.Empty;
				if (PageContext.HttpContext.Request.Query.ContainsKey("hookKey"))
					hookKey = HttpContext.Request.Query["hookKey"].ToString();

				if (!string.IsNullOrEmpty(hookKey))
				{
					_logger.LogDebug("ApplicationNode OnGet received hookKey: {HookKey}", hookKey);
				}

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "ApplicationNodePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for application node pages.
		/// Validates the antiforgery token, initializes the page model,
		/// checks for page existence, and processes the form submission.
		///
		/// Adapted from monolith source lines 62-101. All HookManager calls to
		/// IPageHook and IApplicationNodePageHook have been removed. The HookKey
		/// property from BaseErpPageModel is preserved for backward compatibility
		/// and diagnostic logging.
		/// </summary>
		/// <returns>The page result, a redirect from Init(), or a not-found result.</returns>
		public IActionResult OnPost()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// HookKey (from BaseErpPageModel) reads the "hookKey" query parameter.
				// In the monolith, it was passed to:
				//   HookManager.GetHookedInstances<IPageHook>(HookKey)
				//   HookManager.GetHookedInstances<IApplicationNodePageHook>(HookKey)
				// In the Gateway, hookKey is logged for diagnostic traceability
				// and can be forwarded to backend services as needed.
				if (!string.IsNullOrEmpty(HookKey))
				{
					_logger.LogDebug("ApplicationNode OnPost received hookKey: {HookKey}", HookKey);
				}

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
				_logger.LogError(ex, "ApplicationNodePageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
