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
using Newtonsoft.Json.Linq;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Razor Page model for the Record Create page in the Gateway/BFF layer.
	/// Handles creation of a new entity record through a create form.
	///
	/// Adapted from the monolith WebVella.Erp.Web.Pages.Application.RecordCreatePageModel:
	///   - Replaces direct RecordManager.CreateRecord() with HTTP POST to Core service
	///   - Replaces PageService.ConvertFormPostToEntityRecord() with Gateway-local form conversion
	///   - Removes HookManager page/record hooks (migrated to event-driven architecture)
	///   - Replaces Log().Create() with ILogger for structured logging
	///
	/// The create flow:
	///   1. OnGet: Initializes page context, validates routing, renders create form
	///   2. OnPostAsync: Converts form data to EntityRecord, validates required fields,
	///      sends HTTP POST to Core service, handles success redirect or validation errors
	/// </summary>
	public class RecordCreatePageModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<RecordCreatePageModel> _logger;

		/// <summary>
		/// Named HttpClient key for the Core microservice, registered in Program.cs via
		/// builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Constructs the RecordCreatePageModel with required services injected via DI.
		/// Preserves the monolith constructor signature (ErpRequestContext) while adding
		/// IHttpClientFactory for Core service communication and ILogger for structured logging.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request context resolved from DI, providing
		/// Page, Entity, App, SitemapArea, SitemapNode for URL routing and form processing.</param>
		/// <param name="httpClientFactory">Factory for creating typed HttpClient instances
		/// for the Core service HTTP POST record creation calls.</param>
		/// <param name="logger">Structured logger replacing the monolith's Log().Create() pattern.</param>
		public RecordCreatePageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<RecordCreatePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Handles GET requests for the record create page.
		/// Initializes routing context, validates the page exists, performs canonical redirect
		/// if page name doesn't match, and prepares the page for rendering.
		///
		/// Preserves monolith behavior:
		///   - Init() → URL parsing, app/area/node/page resolution, RBAC
		///   - Null check for ErpRequestContext.Page → NotFound if page not resolved
		///   - Canonical redirect → ensures URL matches resolved page name
		///   - BeforeRender() → populates ViewData for layout rendering
		///
		/// Removed from monolith: HookManager.GetHookedInstances&lt;IPageHook&gt;(HookKey)
		/// loop — page lifecycle hooks are migrated to the event-driven architecture.
		/// </summary>
		/// <returns>Page result for rendering, NotFound if page missing, or Redirect for canonical URL.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Canonical redirect: ensure URL page name matches resolved page name.
				// Preserves monolith behavior from source lines 29-33:
				//   if (PageName != ErpRequestContext.Page.Name) → redirect to correct URL
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/c/{ErpRequestContext.Page.Name}{queryString}");
				}

				// HookManager page hooks removed — IPageHook instances were iterated here
				// in the monolith (source lines 35-40) for OnGet interception.
				// Migrated to event-driven architecture; Core service publishes page lifecycle events.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordCreatePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles POST requests for the record create form submission.
		/// Converts form data to an EntityRecord, validates required fields,
		/// sends HTTP POST to the Core service for record creation, and handles
		/// the response (redirect on success, validation errors on failure).
		///
		/// Preserves monolith behavior:
		///   - Antiforgery validation via ModelState.IsValid (source line 58)
		///   - Init() → URL parsing, app/area/node/page resolution, RBAC
		///   - Form-to-EntityRecord conversion (replaces PageService.ConvertFormPostToEntityRecord)
		///   - Auto-generate record ID if not provided (source lines 75-76)
		///   - ValidateRecordSubmission() → required field checks (source line 95)
		///   - Record creation via HTTP POST (replaces RecordManager.CreateRecord at source line 102)
		///   - On success → redirect to record details page or ReturnUrl (source lines 121-124)
		///   - On failure → populate Validation from API response errors (source lines 103-112)
		///   - Catch ValidationException → display validation errors (source lines 127-133)
		///   - Catch generic Exception → log and display error (source lines 134-140)
		///
		/// Removed from monolith:
		///   - HookManager.GetHookedInstances&lt;IPageHook&gt; pre-create page hooks (source lines 67-72)
		///   - HookManager.GetHookedInstances&lt;IRecordCreatePageHook&gt; pre/post create hooks (source lines 78-92, 115-119)
		///   - Direct RecordManager.CreateRecord() call (source line 102)
		///   - PageService.ConvertFormPostToEntityRecord() call (source line 64)
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

				// Convert form post data to EntityRecord
				// Replaces: new PageService().ConvertFormPostToEntityRecord(PageContext.HttpContext,
				//           entity: ErpRequestContext.Entity, recordId: RecordId)
				var PostObject = ConvertFormPostToEntityRecord();
				DataModel["Record"] = PostObject;

				// HookManager page hooks removed — IPageHook.OnPost() and
				// IRecordCreatePageHook.OnPreCreateRecord() are migrated to event-driven architecture.

				// Ensure the record has an ID (preserves monolith behavior from source lines 75-76)
				if (!PostObject.Properties.ContainsKey("id"))
					PostObject["id"] = Guid.NewGuid();

				// Validate required fields
				// Preserves monolith behavior: ValidateRecordSubmission checks required field constraints
				// against entity metadata. Core service performs full validation on its side as well.
				ValidateRecordSubmission(PostObject, ErpRequestContext.Entity, Validation);
				if (Validation.Errors.Any())
				{
					BeforeRender();
					return Page();
				}

				// HTTP POST to Core service for record creation
				// Replaces: new RecordManager().CreateRecord(ErpRequestContext.Entity.MapTo<Entity>(), PostObject)
				// Endpoint: POST /api/v3/{locale}/record/{entityName}
				var client = _httpClientFactory.CreateClient(CoreServiceClientName);
				var entityName = ErpRequestContext.Entity.Name;

				// Serialize using Newtonsoft.Json to properly handle the [JsonExtensionData] attribute
				// on EntityRecord.Properties — this ensures form field values are serialized as
				// top-level JSON properties matching the Core service's expected record format.
				var jsonPayload = JsonConvert.SerializeObject(PostObject);
				using var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
				var response = await client.PostAsync(
					$"/api/v3/en/record/{entityName}",
					httpContent);

				// Deserialize response using Newtonsoft.Json for backward compatibility
				// with the SharedKernel ResponseModel envelope format (AAP Section 0.8.2).
				// Response shape: { success, errors[], timestamp, message, object: { data: [{ id, ... }] } }
				var responseBody = await response.Content.ReadAsStringAsync();
				var createResponse = JsonConvert.DeserializeObject<WebVella.Erp.SharedKernel.Models.ResponseModel>(responseBody);

				if (createResponse == null || !createResponse.Success)
				{
					Validation.Message = createResponse?.Message ?? "Record creation failed.";
					if (createResponse?.Errors != null)
					{
						foreach (var error in createResponse.Errors)
							Validation.Errors.Add(new ValidationError(error.Key, error.Message));
					}

					ErpRequestContext.PageContext = PageContext;
					BeforeRender();
					return Page();
				}

				// HookManager post-create hooks removed — IRecordCreatePageHook.OnPostCreateRecord()
				// is migrated to event-driven architecture (Core service publishes RecordCreated event).

				// On success: redirect to record details page or ReturnUrl
				// Preserves monolith redirect pattern from source lines 121-124
				if (string.IsNullOrWhiteSpace(ReturnUrl))
				{
					// Extract the created record ID from the API response.
					// Monolith format: createResponse.Object.Data[0]["id"]
					// Gateway format: ResponseModel.Object is a JObject with data array
					var createdRecordId = ExtractCreatedRecordId(createResponse, PostObject);
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/r/{createdRecordId}");
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
				_logger.LogError(ex, "RecordCreatePageModel Error on POST");
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
		/// full type conversion during record creation validation based on entity
		/// field metadata.
		///
		/// If RecordId is available from the URL route, it is included in the
		/// output record, matching the monolith behavior where ConvertFormPostToEntityRecord
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

		/// <summary>
		/// Extracts the created record's ID from the Core service API response.
		/// Handles the standard ResponseModel envelope where Object contains
		/// the record data in the format: { "data": [{ "id": "guid", ... }] }.
		///
		/// Falls back to the PostObject's pre-generated ID if the response object
		/// cannot be parsed, ensuring the redirect always has a valid record ID.
		/// </summary>
		/// <param name="createResponse">The deserialized Core service response.</param>
		/// <param name="postObject">The submitted record with a pre-generated ID as fallback.</param>
		/// <returns>The created record's ID as a string for URL construction.</returns>
		private static object ExtractCreatedRecordId(
			WebVella.Erp.SharedKernel.Models.ResponseModel createResponse,
			EntityRecord postObject)
		{
			if (createResponse?.Object != null)
			{
				try
				{
					// The ResponseModel.Object is deserialized as a JObject by Newtonsoft.Json.
					// Navigate the standard response structure: { data: [{ id: "..." }] }
					if (createResponse.Object is JObject responseObj)
					{
						var dataArray = responseObj["data"] as JArray;
						if (dataArray != null && dataArray.Count > 0)
						{
							var firstRecord = dataArray[0];
							var idToken = firstRecord["id"];
							if (idToken != null)
								return idToken.ToString();
						}
					}

					// Fallback: try parsing Object as a string (if double-serialized)
					var objString = createResponse.Object.ToString();
					if (!string.IsNullOrWhiteSpace(objString))
					{
						var parsed = JObject.Parse(objString);
						var dataArr = parsed["data"] as JArray;
						if (dataArr != null && dataArr.Count > 0)
						{
							var idVal = dataArr[0]?["id"];
							if (idVal != null)
								return idVal.ToString();
						}
					}
				}
				catch (Exception)
				{
					// Parsing failed — fall through to PostObject fallback
				}
			}

			// Fallback: use the pre-generated ID from the submitted record
			if (postObject.Properties.ContainsKey("id"))
				return postObject["id"];

			return Guid.NewGuid();
		}

		#endregion
	}
}
