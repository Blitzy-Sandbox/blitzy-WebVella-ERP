using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.Gateway.Models;

namespace WebVella.Erp.Gateway.Pages
{
	/// <summary>
	/// Gateway-adapted page model for creating a new related record and establishing
	/// a many-to-many relationship link in a single atomic operation.
	///
	/// Adapted from WebVella.Erp.Web/Pages/RecordRelatedRecordCreate.cshtml.cs (170 lines).
	/// This is the most complex related-record page — it performs transactional record
	/// creation plus M2M relation linking. In the Gateway, all direct database calls
	/// (DbContext, RecordManager, EntityRelationManager) and hook execution (HookManager)
	/// are replaced with HTTP calls to the Core microservice, which handles transactional
	/// integrity on the backend.
	///
	/// Key transformations from monolith:
	///   - Namespace: WebVella.Erp.Web.Pages.Application → WebVella.Erp.Gateway.Pages
	///   - DB transactions: DbContext.CreateConnection/BeginTransaction → Core service HTTP POST
	///   - RecordManager.CreateRecord → HTTP POST /api/v3/{locale}/record/{entity}/create-with-relation
	///   - RecordManager.CreateRelationManyToManyRecord → included in atomic Core service call
	///   - EntityRelationManager.Read → relation direction sent as request metadata
	///   - HookManager.GetHookedInstances → removed (hooks run as domain events in backend)
	///   - PageService.ConvertFormPostToEntityRecord → Gateway-local form conversion
	///   - new Log().Create → ILogger.LogError
	/// </summary>
	public class RecordRelatedRecordCreatePageModel : BaseErpPageModel
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<RecordRelatedRecordCreatePageModel> _logger;

		/// <summary>
		/// Named HttpClient key for the Core microservice, registered in Gateway Program.cs
		/// via builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// Constructs the page model with required Gateway dependencies.
		/// Preserves the monolith's [FromServices] ErpRequestContext DI pattern while adding
		/// IHttpClientFactory for Core service HTTP calls and ILogger for structured logging.
		/// </summary>
		/// <param name="reqCtx">Scoped per-request routing context resolved by DI.</param>
		/// <param name="httpClientFactory">Factory for creating named HttpClient instances.</param>
		/// <param name="logger">Structured logger replacing the monolith's Log class.</param>
		public RecordRelatedRecordCreatePageModel(
			[FromServices] ErpRequestContext reqCtx,
			[FromServices] IHttpClientFactory httpClientFactory,
			[FromServices] ILogger<RecordRelatedRecordCreatePageModel> logger)
		{
			ErpRequestContext = reqCtx;
			_httpClientFactory = httpClientFactory;
			_logger = logger;
		}

		/// <summary>
		/// Handles GET requests for the related record creation page.
		/// Preserves the monolith's Init/BeforeRender lifecycle while removing hook execution
		/// (hooks now run as domain events in backend microservices).
		///
		/// Adapted from source lines 23-53:
		///   - Init() call and null checks: preserved
		///   - Canonical redirect when PageName != ErpRequestContext.Page.Name: preserved
		///   - HookManager.GetHookedInstances&lt;IPageHook&gt; loop: removed
		///   - HookManager.GetHookedInstances&lt;IRecordRelatedRecordCreatePageHook&gt;: removed
		///   - new Log().Create(LogType.Error, ...): replaced with _logger.LogError
		/// </summary>
		/// <returns>Page result for rendering, redirect for canonical URL, or error page.</returns>
		public IActionResult OnGet()
		{
			try
			{
				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Canonical URL redirect — ensures the URL uses the resolved page name
				if (PageName != ErpRequestContext.Page.Name)
				{
					var queryString = HttpContext.Request.QueryString.ToString();
					return Redirect(
						$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
						$"/{ErpRequestContext.SitemapNode.Name}/r/{ErpRequestContext.ParentRecordId}" +
						$"/rl/{ErpRequestContext.RelationId}/c/{ErpRequestContext.Page.Name}{queryString}");
				}

				// Hook execution removed — IPageHook and IRecordRelatedRecordCreatePageHook
				// instances previously executed here now run as domain events in backend services.

				BeforeRender();
				return Page();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "RecordRelatedRecordCreatePageModel Error on GET");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		/// <summary>
		/// Handles POST requests for creating a new record and linking it via a
		/// many-to-many relation. This is the most complex handler — the monolith
		/// used a DB transaction wrapping CreateRecord + CreateRelationManyToManyRecord.
		/// In the Gateway, this is replaced by a single HTTP POST to the Core service
		/// which handles transactional integrity atomically.
		///
		/// Adapted from source lines 55-169:
		///   - Antiforgery check: preserved
		///   - Init()/null checks: preserved
		///   - PageService.ConvertFormPostToEntityRecord: replaced with Gateway-local conversion
		///   - HookManager pre/post hooks: removed (backend domain events)
		///   - DbContext transaction + RecordManager + EntityRelationManager: replaced with
		///     HTTP POST to Core service create-with-relation endpoint
		///   - Redirect logic: preserved (related record view or ReturnUrl)
		///   - ValidationException/Exception catch: preserved with ILogger
		/// </summary>
		/// <returns>Redirect on success, page with validation errors on failure.</returns>
		public async Task<IActionResult> OnPostAsync()
		{
			try
			{
				if (!ModelState.IsValid)
					throw new Exception("Antiforgery check failed.");

				var initResult = Init();
				if (initResult != null) return initResult;
				if (ErpRequestContext.Page == null) return NotFound();

				// Gateway-local form data conversion — replaces monolith's
				// new PageService().ConvertFormPostToEntityRecord(PageContext.HttpContext,
				//     entity: ErpRequestContext.Entity, recordId: RecordId)
				var PostObject = ConvertFormPostToEntityRecord();

				// Ensure the record has an ID, generating one if the form didn't supply it
				if (!PostObject.Properties.ContainsKey("id"))
					PostObject["id"] = Guid.NewGuid();

				// Bind the form data to the page data model for re-rendering on validation failure
				DataModel["Record"] = PostObject;

				// Hook execution removed — IPageHook.OnPost() and
				// IRecordRelatedRecordCreatePageHook.OnPreCreateRecord/OnPostCreateRecord
				// instances previously executed here now run as domain events in backend services.
				// The HookKey (from query string) is forwarded to the Core service so backend
				// event subscribers can filter by hook context if needed.

				// Gateway-local field validation — checks required fields against entity metadata.
				// Replaces monolith's pre-hook validation step. If the entity has required fields
				// and the form post is missing values, validation errors are added to the
				// Validation collection before any HTTP call to the Core service.
				ValidateRecordSubmission(PostObject, ErpRequestContext.Entity, Validation);

				if (!Validation.Errors.Any())
				{
					// Create the record and establish the M2M relation link via a single
					// atomic HTTP POST to the Core service. The Core service handles the
					// transactional integrity that was previously managed by DbContext
					// transactions in the monolith (BeginTransaction/CommitTransaction).
					var httpClient = _httpClientFactory.CreateClient(CoreServiceClientName);
					var entityName = ErpRequestContext.Entity?.Name ?? string.Empty;

					// Build the combined create-with-relation payload:
					//   - record: the entity record field data
					//   - relationId: the M2M relation to link through
					//   - parentRecordId: the parent record to link with
					//   - originEntityId: determines link direction (origin vs target)
					var createPayload = new Dictionary<string, object>
					{
						["record"] = PostObject.Properties,
						["relationId"] = RelationId ?? Guid.Empty,
						["parentRecordId"] = ParentRecordId ?? Guid.Empty,
						["originEntityId"] = ErpRequestContext.ParentEntity?.Id ?? Guid.Empty,
						["recordId"] = RecordId ?? Guid.Empty,
						["hookKey"] = HookKey ?? string.Empty
					};

					var response = await httpClient.PostAsJsonAsync(
						$"/api/v3/en_US/record/{entityName}/create-with-relation",
						createPayload);

					var responseBody = await response.Content.ReadAsStringAsync();
					var createResponse = JsonConvert.DeserializeObject<WebVella.Erp.SharedKernel.Models.ResponseModel>(responseBody);

					if (createResponse != null && !createResponse.Success)
					{
						Validation.Message = createResponse.Message;
						if (createResponse.Errors != null)
						{
							foreach (var error in createResponse.Errors)
								Validation.Errors.Add(new ValidationError(error.Key, error.Message));
						}

						BeforeRender();
						return Page();
					}

					// Extract the created record ID from the Core service response.
					// The response envelope follows the monolith pattern:
					// { success: true, object: { data: [{ "id": "guid", ... }] } }
					Guid createdRecordId = ExtractCreatedRecordId(createResponse!, PostObject);

					// Post-create redirect — preserved from monolith:
					// Navigate to the created related record's detail view, or to ReturnUrl
					if (string.IsNullOrWhiteSpace(ReturnUrl))
					{
						return Redirect(
							$"/{ErpRequestContext.App.Name}/{ErpRequestContext.SitemapArea.Name}" +
							$"/{ErpRequestContext.SitemapNode.Name}/r/{ErpRequestContext.ParentRecordId}" +
							$"/rl/{ErpRequestContext.RelationId}/r/{createdRecordId}");
					}
					else
					{
						return Redirect(ReturnUrl);
					}
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
				_logger.LogError(ex, "RecordRelatedRecordCreatePageModel Error on POST");
				Validation.Message = ex.Message;
				BeforeRender();
				return Page();
			}
		}

		#region Private Helper Methods

		/// <summary>
		/// Converts HTTP form post data into a Gateway-local EntityRecord.
		/// Replaces the monolith's PageService.ConvertFormPostToEntityRecord() method
		/// which read form fields and mapped them to entity record properties.
		///
		/// Skips ASP.NET Core system fields (prefixed with "__") such as the
		/// antiforgery token (__RequestVerificationToken).
		/// </summary>
		/// <returns>An EntityRecord populated with form field values.</returns>
		private EntityRecord ConvertFormPostToEntityRecord()
		{
			var record = new EntityRecord();
			var form = PageContext.HttpContext.Request.Form;

			foreach (var key in form.Keys)
			{
				// Skip ASP.NET Core system fields (antiforgery tokens, handler names, etc.)
				if (key.StartsWith("__"))
					continue;

				var value = form[key];

				// Handle multi-value form fields (e.g., multi-select, checkbox groups)
				if (value.Count > 1)
				{
					record[key] = value.ToArray();
				}
				else
				{
					var stringValue = value.ToString();
					// Preserve empty strings as null for nullable field compatibility
					record[key] = string.IsNullOrEmpty(stringValue) ? null! : stringValue;
				}
			}

			return record;
		}

		/// <summary>
		/// Extracts the created record's GUID from the Core service response envelope.
		/// The response follows the monolith's pattern:
		///   { "success": true, "object": { "data": [{ "id": "guid-value", ... }] } }
		///
		/// Falls back to the locally-generated ID from PostObject if the response
		/// does not contain a parseable record ID.
		/// </summary>
		/// <param name="createResponse">The deserialized Core service response envelope.</param>
		/// <param name="postObject">The submitted record containing the generated ID fallback.</param>
		/// <returns>The GUID of the created record.</returns>
		private static Guid ExtractCreatedRecordId(
			WebVella.Erp.SharedKernel.Models.ResponseModel createResponse,
			EntityRecord postObject)
		{
			Guid createdRecordId = Guid.Empty;

			if (createResponse?.Object != null)
			{
				try
				{
					// Parse the response object which follows the standard ERP response shape
					var responseObject = JObject.FromObject(createResponse.Object);
					var dataArray = responseObject["data"] as JArray;
					if (dataArray != null && dataArray.Count > 0)
					{
						var firstRecord = dataArray[0];
						var idToken = firstRecord["id"];
						if (idToken != null && Guid.TryParse(idToken.ToString(), out var parsedId))
						{
							createdRecordId = parsedId;
						}
					}
				}
				catch
				{
					// Response format did not match expected shape — fall through to fallback
				}
			}

			// Fallback: use the locally-generated ID from the form post object
			if (createdRecordId == Guid.Empty && postObject["id"] != null)
			{
				Guid.TryParse(postObject["id"].ToString(), out createdRecordId);
			}

			return createdRecordId;
		}

		#endregion
	}
}
