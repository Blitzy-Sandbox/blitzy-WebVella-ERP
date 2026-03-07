using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using WebVella.Erp.Gateway.Models;

// Alias to disambiguate SharedKernel.Models.EntityRecord from Gateway.Models.EntityRecord
using SharedModels = WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Razor Page model for managing (updating) a related record within the ERP application.
	/// Adapted from WebVella.Erp.Web.Pages.Application.RecordRelatedRecordManagePageModel (146 lines).
	///
	/// Key microservice adaptations:
	///   - All HookManager usage removed — page lifecycle hooks now execute in backend services via domain events
	///   - PageService.ConvertFormPostToEntityRecord() replaced with Gateway-local form-to-record conversion
	///   - RecordManager.UpdateRecord() replaced with HTTP PUT to Core service at /api/v3/{locale}/record/{entityName}/{recordId}
	///   - new Log().Create() replaced with ILogger structured logging
	///   - IHttpClientFactory injected for Core service communication via named "CoreService" HttpClient
	///
	/// Route: /{AppName}/{AreaName}/{NodeName}/r/{ParentRecordId}/rl/{RelationId}/m/{RecordId}/{PageName?}
	/// </summary>
	public class RecordRelatedRecordManagePageModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<RecordRelatedRecordManagePageModel> _logger;

		/// <summary>
		/// Named HttpClient key for the Core microservice, registered in Gateway Program.cs via
		/// builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Constructs the page model with required DI services.
		/// Preserves the monolith's ErpRequestContext DI pattern and adds
		/// IHttpClientFactory for Core service communication and ILogger for structured logging.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request routing context resolved from the URL.</param>
		/// <param name="httpClientFactory">Factory for creating named HttpClient instances.</param>
		/// <param name="logger">Structured logger replacing the monolith's Log class.</param>
		public RecordRelatedRecordManagePageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<RecordRelatedRecordManagePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory;
			_logger = logger;
		}

		/// <summary>
		/// Handles HTTP GET requests for the related record manage page.
		/// Preserves the monolith's Init → null checks → canonical redirect → RecordsExists → BeforeRender flow.
		/// HookManager.GetHookedInstances&lt;IPageHook&gt; calls removed — hooks run in backend services.
		/// </summary>
		/// <returns>The rendered page, a redirect for canonical URL correction, or NotFound.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Canonical redirect: if the PageName bind property doesn't match the resolved page name,
				// redirect to the correct canonical URL preserving query string parameters.
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}/{ErpRequestContext.SitemapNode.Name}" +
						$"/r/{ErpRequestContext.ParentRecordId}/rl/{ErpRequestContext.RelationId}" +
						$"/m/{ErpRequestContext.RecordId}/{ErpRequestContext.Page.Name}{queryString}");
				}

				if (!RecordsExists()) return NotFound();

				// Page lifecycle hooks removed — IPageHook instances previously executed here
				// now run in backend services via domain events published on the message bus.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordRelatedRecordManagePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for the related record manage form submission.
		/// Preserves the monolith's antiforgery → Init → null checks → form conversion → update → redirect flow.
		///
		/// Adaptations:
		///   - PageService.ConvertFormPostToEntityRecord() → Gateway-local ConvertFormPostToEntityRecord()
		///   - HookManager pre/post manage hooks → removed (backend services handle via domain events)
		///   - RecordManager.UpdateRecord() → HTTP PUT to Core service
		///   - Log().Create() → ILogger.LogError()
		/// </summary>
		/// <returns>Redirect on success, or the re-rendered page with validation errors on failure.</returns>
		public async Task<IActionResult> OnPostAsync()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");

				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// Canonical redirect for POST — same pattern as monolith
				if (PageName != ErpRequestContext.Page.Name)
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}/{ErpRequestContext.SitemapNode.Name}" +
						$"/r/{ErpRequestContext.ParentRecordId}/rl/{ErpRequestContext.RelationId}" +
						$"/m/{ErpRequestContext.Page.Name}");

				// Gateway-local form conversion replacing new PageService().ConvertFormPostToEntityRecord(...)
				var PostObject = ConvertFormPostToEntityRecord();
				DataModel["Record"] = PostObject;

				// Page lifecycle hooks removed — IPageHook instances previously executed here
				// now run in backend services via domain events.

				// Validate record submission — field-level required validation from BaseErpPageModel.
				// In the monolith this was commented out (deferred to RecordManager), but in the
				// Gateway architecture we validate locally before making the HTTP call to the Core service.
				if (ErpRequestContext.Entity != null)
					ValidateRecordSubmission(PostObject, ErpRequestContext.Entity, Validation);

				if (!Validation.Errors.Any())
				{
					// Ensure the record ID is present in the POST object
					if (!PostObject.Properties.ContainsKey("id"))
						PostObject["id"] = RecordId.Value;

					// Pre-manage hooks removed — IRecordRelatedRecordManagePageHook.OnPreManageRecord
					// now runs in backend Core service via domain events after record update.

					// HTTP PUT to Core service replacing direct RecordManager.UpdateRecord()
					var client = _httpClientFactory.CreateClient(CoreServiceClientName);
					var entityName = ErpRequestContext.Entity?.Name ?? string.Empty;
					var recordId = RecordId?.ToString() ?? string.Empty;
					var response = await client.PutAsJsonAsync(
						$"/api/v3/en_US/record/{entityName}/{recordId}",
						PostObject);

					var responseContent = await response.Content.ReadAsStringAsync();
					var updateResponse = JsonConvert.DeserializeObject<SharedModels.ResponseModel>(responseContent);

					if (updateResponse == null || !updateResponse.Success)
					{
						Validation.Message = updateResponse?.Message ?? "Record update failed.";
						if (updateResponse?.Errors != null)
						{
							foreach (var error in updateResponse.Errors)
								Validation.Errors.Add(new ValidationError(error.Key, error.Message));
						}

						ErpRequestContext.PageContext = PageContext;
						BeforeRender();
						return Page();
					}

					// Post-manage hooks removed — IRecordRelatedRecordManagePageHook.OnPostManageRecord
					// now runs in backend Core service via domain events after record update.

					// Redirect to related record details view or custom ReturnUrl
					if (string.IsNullOrWhiteSpace(ReturnUrl))
						return Redirect(
							$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}/{ErpRequestContext.SitemapNode.Name}" +
							$"/r/{ErpRequestContext.ParentRecordId}/rl/{ErpRequestContext.RelationId}/r/{RecordId}");
					else
						return Redirect(ReturnUrl);
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
				_logger.LogError(ex, "RecordRelatedRecordManagePageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Gateway-local form data to EntityRecord conversion.
		/// Replaces the monolith's new PageService().ConvertFormPostToEntityRecord().
		///
		/// Reads form key-value pairs from the HTTP POST body and creates
		/// a Gateway-local EntityRecord with properties matching the submitted form fields.
		/// Skips ASP.NET Core system fields (__RequestVerificationToken, etc.) and the
		/// standard anti-forgery token field.
		///
		/// Note: The Core service's RecordManager performs full field-type coercion
		/// and validation on the backend — the Gateway sends string values which
		/// the Core service converts to appropriate database types.
		/// </summary>
		/// <returns>An EntityRecord populated with the form submission data.</returns>
		private EntityRecord ConvertFormPostToEntityRecord()
		{
			var record = new EntityRecord();
			if (PageContext?.HttpContext?.Request?.Form == null)
				return record;

			foreach (var key in PageContext.HttpContext.Request.Form.Keys)
			{
				// Skip ASP.NET Core system fields (anti-forgery tokens, hidden infrastructure fields)
				if (key.StartsWith("__") || string.Equals(key, "RequestVerificationToken", StringComparison.OrdinalIgnoreCase))
					continue;

				var value = PageContext.HttpContext.Request.Form[key].ToString();

				// Preserve null semantics: empty strings submitted by cleared form fields
				// are passed through as empty strings. The Core service handles null
				// coercion based on field type metadata.
				record[key] = value;
			}

			return record;
		}
	}
}
