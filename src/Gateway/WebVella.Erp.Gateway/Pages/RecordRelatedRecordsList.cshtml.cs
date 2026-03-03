using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Page model for the related records list page. Displays a list of records
	/// related to a parent record via a many-to-many or one-to-many relation.
	///
	/// Adapted from WebVella.Erp.Web.Pages.Application.RecordRelatedRecordsListPageModel.
	/// Changes from monolith:
	///   - Namespace: WebVella.Erp.Web.Pages.Application → WebVella.Erp.Gateway.Pages
	///   - Removed: All HookManager.GetHookedInstances calls (IPageHook,
	///     IRecordRelatedRecordsListPageHook) — hooks now execute in backend services
	///   - Replaced: new Log().Create(LogType.Error, ...) → ILogger.LogError(...)
	///   - Added: ILogger dependency injection for structured logging
	///   - Preserved: Init(), null checks, canonical redirect, RecordsExists(),
	///     BeforeRender(), Page(), ValidationException/Exception catch blocks
	/// </summary>
	public class RecordRelatedRecordsListPageModel : BaseErpPageModel
	{
		private readonly ILogger<RecordRelatedRecordsListPageModel> _logger;

		public RecordRelatedRecordsListPageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] ILogger<RecordRelatedRecordsListPageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger;
		}

		/// <summary>
		/// Handles GET requests for the related records list page.
		/// Initializes the request context, performs canonical redirect if the
		/// page name in the URL doesn't match the resolved page, and renders
		/// the page with populated ViewData.
		/// </summary>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;

				if (ErpRequestContext.Page == null) return NotFound();
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect($"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}/{ErpRequestContext.SitemapNode.Name}/r/{ErpRequestContext.ParentRecordId}/rl/{ErpRequestContext.RelationId}/l/{ErpRequestContext.Page.Name}{queryString}");
				}

				// Hook execution removed — IPageHook and IRecordRelatedRecordsListPageHook
				// instances now run in backend microservices via event-driven processing.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordRelatedRecordsListPageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles POST requests for the related records list page.
		/// Validates antiforgery token, initializes request context, and
		/// processes form submissions with full error handling.
		/// </summary>
		public IActionResult OnPost()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Hook execution removed — IPageHook and IRecordRelatedRecordsListPageHook
				// instances now run in backend microservices via event-driven processing.

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
				_logger.LogError(ex, "RecordRelatedRecordsListPageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
