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
	/// Page model for the record details view.
	/// Adapted from WebVella.Erp.Web.Pages.Application.RecordDetailsPageModel.
	///
	/// Handles record detail display and standard delete behavior. The delete
	/// operation is delegated to the Core service via HTTP DELETE rather than
	/// calling RecordManager.DeleteRecord() directly.
	///
	/// All monolith HookManager calls (IPageHook, IRecordDetailsPageHook) have
	/// been removed; hook-based logic now runs in backend microservices via
	/// domain events.
	/// </summary>
	public class RecordDetailsPageModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<RecordDetailsPageModel> _logger;

		/// <summary>
		/// Named HTTP client identifier for the Core Platform microservice.
		/// Matches the registration in Program.cs: builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Default locale used when constructing Core service API URLs.
		/// The monolith's RecordManager.DeleteRecord() did not use locale;
		/// the Gateway REST proxy requires one for the /api/v3/{locale}/ route.
		/// </summary>
		private const string DefaultLocale = "en";

		/// <summary>
		/// Constructs the RecordDetailsPageModel with required dependencies.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request routing context resolved via DI.</param>
		/// <param name="httpClientFactory">Factory for creating named HttpClient instances
		/// to communicate with the Core microservice.</param>
		/// <param name="logger">Structured logger replacing the monolith's Log diagnostic class.</param>
		public RecordDetailsPageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<RecordDetailsPageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory;
			_logger = logger;
		}

		/// <summary>
		/// Handles HTTP GET requests for the record details page.
		/// Performs request context initialization, page null-check, record
		/// existence verification, canonical redirect when the URL does not
		/// match the resolved page name, then invokes BeforeRender() for
		/// ViewData population before returning the Razor page result.
		///
		/// Adapted from source lines 17-47 of the monolith RecordDetailsPageModel.
		/// HookManager.GetHookedInstances&lt;IPageHook&gt; calls have been removed —
		/// hook-based logic now runs in backend microservices via domain events.
		/// </summary>
		/// <returns>
		/// An <see cref="IActionResult"/> — either a redirect to the canonical
		/// URL, a 404 result when the page or record is not found, or the
		/// rendered page.
		/// </returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// Canonical redirect: if the URL page name differs from the
				// resolved page name, redirect to the correct canonical URL
				// preserving the query string. This is the record details route
				// pattern: /{app}/{area}/{node}/r/{recordId}/{pageName}
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/r/{ErpRequestContext.RecordId}" +
						$"/{ErpRequestContext.Page.Name}{queryString}");
				}

				// NOTE: HookManager.GetHookedInstances<IPageHook>(HookKey) removed.
				// In the monolith, page hooks could intercept GET requests and return
				// alternate IActionResults. In the microservice architecture, this
				// extensibility is handled by domain events in the backend services.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordDetailsPageModel Error on GET");
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles HTTP POST requests for the record details page.
		/// Performs anti-forgery validation, request context initialization,
		/// page null-check, record existence verification, then handles the
		/// standard delete behavior by delegating to the Core service via
		/// HTTP DELETE.
		///
		/// Adapted from source lines 49-111 of the monolith RecordDetailsPageModel.
		///
		/// CRITICAL DELETE BEHAVIOR:
		/// The monolith called RecordManager.DeleteRecord() directly. In the
		/// Gateway, this is replaced with an HTTP DELETE request to the Core
		/// service at /api/v3/{locale}/record/{entityName}/{recordId}.
		/// On success, redirects to the list view. On failure, populates the
		/// Validation object with errors from the Core service response.
		///
		/// All HookManager calls (IPageHook, IRecordDetailsPageHook) have been
		/// removed — hook-based logic now runs in backend microservices via
		/// domain events.
		/// </summary>
		/// <returns>
		/// A <see cref="Task{IActionResult}"/> — the rendered page with
		/// validation state populated on error, a redirect to the list view
		/// on successful delete, or a 404 result when the page or record is
		/// not found.
		/// </returns>
		public async Task<IActionResult> OnPost()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");

				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// NOTE: HookManager.GetHookedInstances<IPageHook>(HookKey) removed.
				// In the monolith, global page hooks could intercept POST requests.
				// In the microservice architecture, this extensibility is handled
				// by domain events in the backend services.

				// Standard Page Delete behavior — delegates to Core service via HTTP DELETE.
				// Source: if (HookKey == "delete" && ErpRequestContext.Entity != null && ErpRequestContext.RecordId != null)
				//   new RecordManager().DeleteRecord(ErpRequestContext.Entity, (ErpRequestContext.RecordId ?? Guid.Empty))
				// Gateway: HTTP DELETE to /api/v3/{locale}/record/{entityName}/{recordId}
				if (HookKey == "delete" && ErpRequestContext.Entity != null && ErpRequestContext.RecordId != null)
				{
					var entityName = ErpRequestContext.Entity.Name;
					var recordId = ErpRequestContext.RecordId ?? Guid.Empty;

					using var client = _httpClientFactory.CreateClient(CoreServiceClientName);
					var response = await client.DeleteAsync(
						$"/api/v3/{DefaultLocale}/record/{entityName}/{recordId}");
					var responseContent = await response.Content.ReadAsStringAsync();
					var deleteResponse = JsonConvert.DeserializeObject<ResponseModel>(responseContent);

					if (deleteResponse != null && deleteResponse.Success)
					{
						// On successful delete, redirect to the list view for this node.
						// Preserves the original monolith redirect pattern:
						// Redirect($"/{App}/{Area}/{Node}/l/")
						return Redirect(
							$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
							$"/{ErpRequestContext.SitemapNode.Name}/l/");
					}
					else
					{
						// On failure, populate the Validation object with errors from the
						// Core service response, preserving the original monolith behavior.
						if (deleteResponse != null)
						{
							Validation.Message = deleteResponse.Message;
							if (deleteResponse.Errors != null)
							{
								foreach (var error in deleteResponse.Errors)
								{
									Validation.Errors.Add(
										new ValidationError(error.Key, error.Message));
								}
							}
						}
						else
						{
							// Response deserialization failed — report as a generic error
							Validation.Message = $"Failed to delete record. HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
						}

						BeforeRender();
						return Page();
					}
				}

				// NOTE: HookManager.GetHookedInstances<IRecordDetailsPageHook>(HookKey) removed.
				// In the monolith, record details page hooks could handle custom POST actions
				// based on HookKey. In the microservice architecture, this extensibility is
				// handled by domain events in the backend services.

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
				_logger.LogError(ex, "RecordDetailsPageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}
	}
}
