using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.Gateway.Models;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// PageModel for the related record details page in the Gateway/BFF layer.
	/// Adapted from WebVella.Erp.Web.Pages.Application.RecordRelatedRecordDetailsPageModel.
	///
	/// Handles related record detail view and record existence checks. OnGet renders
	/// the detail view; OnPostAsync handles standard delete behavior by delegating
	/// to the Core service via HTTP DELETE instead of the monolith's direct
	/// RecordManager.DeleteRecord() call.
	///
	/// All hook-based processing (IPageHook, IRecordRelatedRecordDetailsPageHook) has
	/// been removed — hooks now execute as domain events in backend microservices.
	/// Diagnostics via new Log().Create() replaced with ILogger structured logging.
	/// </summary>
	public class RecordRelatedRecordDetailsPageModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<RecordRelatedRecordDetailsPageModel> _logger;

		/// <summary>
		/// Named HttpClient key for the Core microservice, registered in Program.cs
		/// via builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Constructs the related record details page model with required dependencies.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context providing routing state
		/// (App, SitemapArea, SitemapNode, Entity, Page, RecordId, RelationId, ParentRecordId).</param>
		/// <param name="httpClientFactory">Factory for creating named HttpClient instances
		/// used to call the Core microservice for record deletion.</param>
		/// <param name="logger">Structured logger for error diagnostics in OnGet/OnPostAsync
		/// catch blocks, replacing the monolith's Log().Create() pattern.</param>
		public RecordRelatedRecordDetailsPageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<RecordRelatedRecordDetailsPageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory;
			_logger = logger;
		}

		/// <summary>
		/// Handles GET requests for the related record details page.
		/// Performs request initialization, page/record existence validation,
		/// canonical URL redirect, and renders the page via BeforeRender().
		///
		/// Adapted from monolith: Init(), null checks, canonical redirect,
		/// RecordsExists() preserved. HookManager calls (IPageHook,
		/// IRecordRelatedRecordDetailsPageHook) removed — hooks now execute
		/// as events in backend services.
		/// </summary>
		/// <returns>Page result on success; NotFound if page/records missing;
		/// redirect if canonical URL differs from current.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// Canonical redirect: if the current PageName doesn't match the resolved
				// page name, redirect to the canonical URL preserving query string.
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/r/{ErpRequestContext.ParentRecordId}" +
						$"/rl/{ErpRequestContext.RelationId}/r/{ErpRequestContext.RecordId}" +
						$"/{ErpRequestContext.Page.Name}{queryString}");
				}

				// Hook execution removed — IPageHook and IRecordRelatedRecordDetailsPageHook
				// instances now run as domain event subscribers in backend microservices.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordRelatedRecordDetailsPageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles POST requests for the related record details page.
		/// Supports standard delete behavior when HookKey == "delete" by sending
		/// an HTTP DELETE request to the Core service at
		/// /api/v3/{locale}/record/{entityName}/{recordId}.
		///
		/// Adapted from monolith: Antiforgery check, Init(), null checks,
		/// RecordsExists() preserved. All HookManager calls removed.
		/// RecordManager.DeleteRecord() replaced with async HTTP DELETE to Core service.
		/// </summary>
		/// <returns>Redirect to related records list on successful delete;
		/// Page result with validation errors on failure.</returns>
		public async Task<IActionResult> OnPostAsync()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// Standard delete behavior — replaces the hook-based delete pattern.
				// When HookKey == "delete" and entity/record context is available,
				// delegate the deletion to the Core microservice via HTTP DELETE.
				if (HookKey == "delete"
					&& ErpRequestContext.Entity != null
					&& ErpRequestContext.RecordId != null)
				{
					var entityName = ErpRequestContext.Entity.Name;
					var recordId = ErpRequestContext.RecordId ?? Guid.Empty;

					var client = _httpClientFactory.CreateClient(CoreServiceClientName);
					var response = await client.DeleteAsync(
						$"/api/v3/en_US/record/{entityName}/{recordId}");
					var responseBody = await response.Content.ReadAsStringAsync();
					var responseModel = JsonConvert.DeserializeObject<ResponseModel>(responseBody);

					if (responseModel != null && responseModel.Success)
					{
						// On successful delete, redirect to the parent record's related records list.
						return Redirect(
							$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
							$"/{ErpRequestContext.SitemapNode.Name}/r/{ErpRequestContext.ParentRecordId}" +
							$"/rl/{ErpRequestContext.RelationId}/l/");
					}
					else if (responseModel != null)
					{
						// Populate validation state from Core service error response
						// for display in the page's error summary.
						Validation.Message = responseModel.Message;
						if (responseModel.Errors != null)
						{
							foreach (var error in responseModel.Errors)
							{
								Validation.Errors.Add(
									new ValidationError(
										error.Key ?? string.Empty,
										error.Message ?? "Validation error"));
							}
						}
					}
				}

				// Hook execution removed — IPageHook and IRecordRelatedRecordDetailsPageHook
				// instances now run as domain event subscribers in backend microservices.

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
				_logger.LogError(ex, "RecordRelatedRecordDetailsPageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
