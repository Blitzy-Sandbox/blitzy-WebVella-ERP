using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Razor Page model for the Record Manage page in the Gateway/BFF layer.
	/// Handles viewing and updating an existing entity record through a manage form.
	///
	/// Adapted from the monolith WebVella.Erp.Web.Pages.Application.RecordManagePageModel:
	///   - Replaces direct RecordManager.UpdateRecord() with HTTP PUT to Core service
	///   - Replaces PageService.ConvertFormPostToEntityRecord() with Gateway-local form conversion
	///   - Removes HookManager page/record hooks (migrated to event-driven architecture)
	///   - Replaces Log().Create() with ILogger for structured logging
	/// </summary>
	public class RecordManagePageModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<RecordManagePageModel> _logger;

		/// <summary>
		/// Named HttpClient key for the Core microservice, registered in Program.cs via
		/// builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Constructs the RecordManagePageModel with required services injected via DI.
		/// Preserves the monolith constructor signature (ErpRequestContext) while adding
		/// IHttpClientFactory for Core service communication and ILogger for structured logging.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context resolved from DI, providing
		/// Page, Entity, App, SitemapArea, SitemapNode, and RecordId.</param>
		/// <param name="httpClientFactory">Factory for creating typed HttpClient instances
		/// for the Core service HTTP PUT record update calls.</param>
		/// <param name="logger">Structured logger replacing the monolith's Log().Create() pattern.</param>
		public RecordManagePageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<RecordManagePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Handles GET requests for the record manage page.
		/// Initializes routing context, validates the record exists, performs canonical redirect
		/// if page name doesn't match, and prepares the page for rendering.
		///
		/// Preserves monolith behavior:
		///   - Init() → URL parsing, app/area/node/page resolution, RBAC
		///   - RecordsExists() → validates record data is loaded in DataModel
		///   - Canonical redirect → ensures URL matches resolved page name
		///   - BeforeRender() → populates ViewData for layout rendering
		///
		/// Removed from monolith: HookManager.GetHookedInstances&lt;IPageHook&gt;(HookKey)
		/// loop — page lifecycle hooks are migrated to the event-driven architecture.
		/// </summary>
		/// <returns>Page result for rendering, NotFound if record missing, or Redirect for canonical URL.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// Canonical redirect: ensure URL page name matches resolved page name
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/m/{ErpRequestContext.RecordId}" +
						$"/{ErpRequestContext.Page.Name}{queryString}");
				}

				// HookManager page hooks removed — migrated to event-driven architecture.
				// In the monolith, IPageHook instances were iterated here for OnGet interception.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordManagePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles POST requests for the record manage form submission.
		/// Converts form data to an EntityRecord, validates required fields,
		/// sends HTTP PUT to the Core service for the record update, and handles
		/// the response (redirect on success, validation errors on failure).
		///
		/// Preserves monolith behavior:
		///   - Antiforgery validation via ModelState.IsValid
		///   - Init() → URL parsing, app/area/node/page resolution, RBAC
		///   - RecordsExists() → validates record data is loaded
		///   - Canonical redirect → ensures URL matches resolved page
		///   - Form-to-EntityRecord conversion (replaces PageService.ConvertFormPostToEntityRecord)
		///   - ValidateRecordSubmission() → required field checks
		///   - Record update via HTTP PUT (replaces RecordManager.UpdateRecord)
		///   - On success → redirect to record details page or ReturnUrl
		///   - On failure → populate Validation from API response errors
		///
		/// Removed from monolith:
		///   - HookManager.GetHookedInstances&lt;IPageHook&gt; pre-update page hooks
		///   - HookManager.GetHookedInstances&lt;IRecordManagePageHook&gt; pre/post manage hooks
		///   - Direct RecordManager.UpdateRecord() call
		///   - PageService.ConvertFormPostToEntityRecord() call
		/// </summary>
		/// <returns>Redirect on success, Page result with validation errors on failure.</returns>
		public async Task<IActionResult> OnPostAsync()
		{
			try
			{
				if (!ModelState.IsValid) throw new Exception("Antiforgery check failed.");

				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();
				if (!RecordsExists()) return NotFound();

				// Canonical redirect for POST requests
				if (PageName != ErpRequestContext.Page.Name)
				{
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/m/{ErpRequestContext.RecordId}" +
						$"/{ErpRequestContext.Page.Name}");
				}

				// Convert form post data to EntityRecord
				// Replaces: new PageService().ConvertFormPostToEntityRecord(PageContext.HttpContext, entity: ErpRequestContext.Entity, recordId: RecordId)
				var PostObject = ConvertFormPostToEntityRecord();
				DataModel["Record"] = PostObject;

				// HookManager page hooks removed — IPageHook.OnPost() and
				// IRecordManagePageHook.OnPreManageRecord() are migrated to event-driven architecture.

				// Ensure record ID is set on the post object (preserves monolith behavior)
				if (!PostObject.Properties.ContainsKey("id"))
					PostObject["id"] = RecordId.Value;

				// Validate required fields
				// Replaces monolith: ValidateRecordSubmission(PostObject, ErpRequestContext.Entity, Validation)
				ValidateRecordSubmission(PostObject, ErpRequestContext.Entity, Validation);
				if (Validation.Errors.Any())
				{
					BeforeRender();
					return Page();
				}

				// HTTP PUT to Core service for record update
				// Replaces: new RecordManager().UpdateRecord(ErpRequestContext.Entity.MapTo<Entity>(), PostObject)
				// Endpoint: PUT /api/v3/{locale}/record/{entityName}/{recordId}
				var client = _httpClientFactory.CreateClient(CoreServiceClientName);
				var entityName = ErpRequestContext.Entity.Name;
				var recordIdValue = RecordId.Value;

				// Serialize using Newtonsoft.Json to properly handle the [JsonExtensionData] attribute
				// on EntityRecord.Properties — this ensures form field values are serialized as
				// top-level JSON properties matching the Core service's expected record format.
				var jsonPayload = JsonConvert.SerializeObject(PostObject);
				using var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
				var response = await client.PutAsync(
					$"/api/v3/en/record/{entityName}/{recordIdValue}",
					httpContent);

				// Deserialize response using Newtonsoft.Json for backward compatibility
				// with the SharedKernel ResponseModel envelope format (AAP Section 0.8.2)
				var responseBody = await response.Content.ReadAsStringAsync();
				var updateResponse = JsonConvert.DeserializeObject<WebVella.Erp.SharedKernel.Models.ResponseModel>(responseBody);

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

				// HookManager post-manage hooks removed — IRecordManagePageHook.OnPostManageRecord()
				// is migrated to event-driven architecture (Core service publishes RecordUpdated event).

				// On success: redirect to record details page or ReturnUrl
				if (string.IsNullOrWhiteSpace(ReturnUrl))
				{
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/r/{recordIdValue}");
				}
				else
				{
					return Redirect(ReturnUrl);
				}
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
				_logger.LogError(ex, "RecordManagePageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		#region Private Helpers

		/// <summary>
		/// Converts HTTP form post data to a Gateway-local EntityRecord.
		/// Replaces the monolith's PageService.ConvertFormPostToEntityRecord().
		///
		/// Reads all form values from the current request, skipping ASP.NET Core
		/// internal fields (prefixed with "__"). All values are stored as strings
		/// in the EntityRecord properties dictionary — the Core service handles
		/// full type conversion during record update validation based on entity
		/// field metadata.
		///
		/// The RecordId is automatically included in the output if available,
		/// matching the monolith behavior where ConvertFormPostToEntityRecord
		/// accepted a recordId parameter.
		/// </summary>
		/// <returns>An EntityRecord populated with form field values.</returns>
		private EntityRecord ConvertFormPostToEntityRecord()
		{
			var record = new EntityRecord();

			if (PageContext?.HttpContext?.Request?.Form == null)
				return record;

			var form = PageContext.HttpContext.Request.Form;

			foreach (var formField in form)
			{
				// Skip ASP.NET Core anti-forgery token and other framework-internal fields
				if (formField.Key.StartsWith("__"))
					continue;

				var value = formField.Value.ToString();
				record[formField.Key] = value;
			}

			return record;
		}

		#endregion
	}
}
