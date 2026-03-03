using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Page model for the record list view.
	/// Adapted from WebVella.Erp.Web.Pages.Application.RecordListPageModel.
	///
	/// Handles record listing with canonical redirect when the URL page name
	/// does not match the resolved page. Data loading for the list itself is
	/// performed by ViewComponents at render time — this page model only
	/// manages the Init/BeforeRender lifecycle, canonical routing, and
	/// exception handling.
	///
	/// All monolith HookManager calls (IPageHook, IRecordListPageHook) have
	/// been removed; hook-based logic now runs in backend microservices via
	/// domain events.
	/// </summary>
	public class RecordListPageModel : BaseErpPageModel
	{
		private readonly ILogger<RecordListPageModel> _logger;

		/// <summary>
		/// Constructs the RecordListPageModel with required dependencies.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request routing context resolved via DI.</param>
		/// <param name="logger">Structured logger replacing the monolith's Log diagnostic class.</param>
		public RecordListPageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] ILogger<RecordListPageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_logger = logger;
		}

		/// <summary>
		/// Handles HTTP GET requests for the record list page.
		/// Performs request context initialization, page null-check, canonical
		/// redirect when the URL does not match the resolved page name, then
		/// invokes BeforeRender() for ViewData population before returning the
		/// Razor page result.
		/// </summary>
		/// <returns>
		/// An <see cref="IActionResult"/> — either a redirect to the canonical
		/// URL, a 404 result when the page is not found, or the rendered page.
		/// </returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Canonical redirect: if the URL page name differs from the
				// resolved page name, redirect to the correct canonical URL
				// preserving the query string.
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}/{ErpRequestContext.SitemapNode.Name}/l/{ErpRequestContext.Page.Name}{queryString}");
				}

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordListPageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for the record list page.
		/// Performs anti-forgery validation, request context initialization,
		/// page null-check, then invokes BeforeRender() before returning the
		/// Razor page. Catches <see cref="ValidationException"/> for form
		/// validation errors and generic exceptions for unexpected failures.
		/// </summary>
		/// <returns>
		/// An <see cref="IActionResult"/> — the rendered page with validation
		/// state populated on error, or a 404 result when the page is not found.
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
				_logger.LogError(ex, "RecordListPageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
