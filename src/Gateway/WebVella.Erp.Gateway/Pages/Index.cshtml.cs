using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Home page model for the site root ("/").
	/// Adapted from WebVella.Erp.Web.Pages.HomePageModel.
	///
	/// Changes from monolith source:
	///   - Namespace: WebVella.Erp.Web.Pages → WebVella.Erp.Gateway.Pages
	///   - Removed: HookManager.GetHookedInstances&lt;IPageHook&gt;() loops (hook-to-event migration)
	///   - Removed: HookManager.GetHookedInstances&lt;IHomePageHook&gt;() loops (hook-to-event migration)
	///   - Replaced: new Log().Create(LogType.Error, ...) → ILogger&lt;HomePageModel&gt;.LogError(...)
	///   - Added: ILogger&lt;HomePageModel&gt; DI for structured logging
	///   - Preserved: Init() lifecycle, BeforeRender(), ErpRequestContext.Page null check,
	///     antiforgery validation, ValidationException catch, generic Exception catch
	/// </summary>
	public class HomePageModel : BaseErpPageModel
	{
		private readonly ILogger<HomePageModel> _logger;

		/// <summary>
		/// Initializes the HomePageModel with required dependencies.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context providing routing state (injected via DI).</param>
		/// <param name="logger">Structured logger for diagnostic and error output.</param>
		public HomePageModel([FromServices] ErpRequestContext reqCtx, ILogger<HomePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger;
		}

		/// <summary>
		/// Handles HTTP GET requests for the home page.
		/// Executes the Init/BeforeRender lifecycle, checks for a resolved page,
		/// and returns the rendered page or a 404 if no page is found.
		///
		/// Monolith hook execution (IPageHook.OnGet, IHomePageHook.OnGet) has been
		/// removed as part of the hook-to-event migration. Hook-based behavior should
		/// be implemented as event subscribers in the appropriate microservice.
		/// </summary>
		/// <returns>The rendered page result, or NotFound if no page is resolved.</returns>
		public IActionResult OnGet()
		{
			try
			{
				Debug.WriteLine("<><><><> ERP Index Start");
				var initResult = Init();
				Debug.WriteLine("<><><><> ERP Index Inited");
				if (initResult != null)
				{
					Debug.WriteLine("<><><><> ERP Index Inited With Result NULL - NOT FOUND");
					return initResult;
				}

				if (ErpRequestContext.Page == null) return NotFound();

				// Hook execution removed — IPageHook and IHomePageHook loops replaced
				// by event-driven architecture. See SharedKernel.Contracts.Events for
				// domain event contracts that replace the former hook interfaces.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "HomePageModel Error on GET");
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for the home page.
		/// Validates antiforgery token via ModelState, executes the Init/BeforeRender
		/// lifecycle, and returns the rendered page.
		///
		/// Monolith hook execution (IPageHook.OnPost, IHomePageHook.OnPost) has been
		/// removed as part of the hook-to-event migration.
		/// </summary>
		/// <returns>The rendered page result, or NotFound if no page is resolved.</returns>
		public IActionResult OnPost()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");

				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Hook execution removed — IPageHook and IHomePageHook loops replaced
				// by event-driven architecture. See SharedKernel.Contracts.Events for
				// domain event contracts that replace the former hook interfaces.

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
				_logger.LogError(ex, "HomePageModel Error on POST");
				BeforeRender();
				return Page();
			}
		}
	}
}
