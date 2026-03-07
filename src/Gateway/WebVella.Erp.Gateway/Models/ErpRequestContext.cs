using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Gateway.Models
{
	// NOTE: App, AppSitemap, and ErpPage types are defined in BaseErpPageModel.cs
	// within this same namespace (WebVella.Erp.Gateway.Models).
	// SitemapArea, SitemapNode, PageType, TranslationResource, Entity, and EntityRecord
	// are defined in WebVella.Erp.SharedKernel.Models (referenced via using above).

	/// <summary>
	/// Internal DTO for deserializing Core service page resolution responses.
	/// The Core service returns both the resolved page and optional parent page in a single response.
	/// </summary>
	internal class PageResolutionResult
	{
		[JsonProperty("page")]
		public ErpPage Page { get; set; }

		[JsonProperty("parent_page")]
		public ErpPage ParentPage { get; set; }
	}

	// ────────────────────────────────────────────────────────────────────────
	// Gateway-local type definitions for types that will eventually be provided
	// by WebVella.Erp.SharedKernel.Models (currently not yet created by the
	// SharedKernel agent). These minimal definitions carry only the properties
	// that the Gateway actually accesses, plus [JsonProperty] annotations so
	// that Core-service JSON responses deserialize correctly.
	//
	// When SharedKernel publishes Entity, EntityRecord, SitemapArea, and
	// SitemapNode, remove these local stubs and rely on the shared types.
	// ────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Gateway-local SitemapArea definition for deserialization of Core service responses.
	/// Minimal subset — only properties used by ErpRequestContext and other Gateway files.
	/// </summary>
	[Serializable]
	public class SitemapArea
	{
		[JsonProperty("id")]
		public Guid Id { get; set; } = Guid.Empty;

		[JsonProperty("app_id")]
		public Guid AppId { get; set; } = Guid.Empty;

		[JsonProperty("weight")]
		public int Weight { get; set; } = 1;

		[JsonProperty("label")]
		public string Label { get; set; } = "";

		[JsonProperty("description")]
		public string Description { get; set; } = "";

		[JsonProperty("name")]
		public string Name { get; set; } = "";

		[JsonProperty("icon_class")]
		public string IconClass { get; set; } = "";

		[JsonProperty("color")]
		public string Color { get; set; } = "";

		[JsonProperty("nodes")]
		public List<SitemapNode> Nodes { get; set; } = new List<SitemapNode>();

		[JsonProperty("access")]
		public List<Guid> Access { get; set; } = new List<Guid>();
	}

	/// <summary>
	/// Gateway-local SitemapNode definition for deserialization of Core service responses.
	/// Minimal subset — only properties used by ErpRequestContext and other Gateway files.
	/// </summary>
	/// <summary>
	/// Gateway-local SitemapNodeType enum. Matches the monolith's node type classification.
	/// Will be superseded by WebVella.Erp.SharedKernel.Models.SitemapNodeType when created.
	/// </summary>
	public enum SitemapNodeType
	{
		EntityList = 0,
		ApplicationPage = 1,
		Url = 2
	}

	[Serializable]
	public class SitemapNode
	{
		[JsonProperty("id")]
		public Guid Id { get; set; } = Guid.Empty;

		[JsonProperty("parent_id")]
		public Guid? ParentId { get; set; } = null;

		[JsonProperty("weight")]
		public int Weight { get; set; } = 1;

		[JsonProperty("group_name")]
		public string GroupName { get; set; } = "";

		[JsonProperty("label")]
		public string Label { get; set; } = "";

		[JsonProperty("name")]
		public string Name { get; set; } = "";

		[JsonProperty("icon_class")]
		public string IconClass { get; set; } = "";

		[JsonProperty("url")]
		public string Url { get; set; } = "";

		[JsonProperty("type")]
		public SitemapNodeType Type { get; set; } = SitemapNodeType.EntityList;

		[JsonProperty("entity_id")]
		public Guid? EntityId { get; set; } = null;

		[JsonProperty("access")]
		public List<Guid> Access { get; set; } = new List<Guid>();

		[JsonProperty("entity_list_pages")]
		public List<Guid> EntityListPages { get; set; } = new List<Guid>();

		[JsonProperty("entity_create_pages")]
		public List<Guid> EntityCreatePages { get; set; } = new List<Guid>();

		[JsonProperty("entity_details_pages")]
		public List<Guid> EntityDetailsPages { get; set; } = new List<Guid>();

		[JsonProperty("entity_manage_pages")]
		public List<Guid> EntityManagePages { get; set; } = new List<Guid>();
	}

	/// <summary>
	/// Gateway-local Entity definition for deserialization of Core service responses.
	/// Minimal subset — only properties used by ErpRequestContext (Name, Fields,
	/// RecordScreenIdField) and general display (Id, Label, LabelPlural).
	/// </summary>
	[Serializable]
	public class Entity
	{
		[JsonProperty("id")]
		public Guid Id { get; set; }

		[JsonProperty("name")]
		public string Name { get; set; } = "";

		[JsonProperty("label")]
		public string Label { get; set; } = "";

		[JsonProperty("labelPlural")]
		public string LabelPlural { get; set; } = "";

		[JsonProperty("system")]
		public bool System { get; set; } = false;

		[JsonProperty("iconName")]
		public string IconName { get; set; } = "";

		[JsonProperty("color")]
		public string Color { get; set; } = "";

		[JsonProperty("fields")]
		public List<Field> Fields { get; set; } = new List<Field>();

		[JsonProperty("record_screen_id_field")]
		public Guid? RecordScreenIdField { get; set; }

		public override string ToString()
		{
			return Name;
		}
	}

	/// <summary>
	/// Gateway-local EntityRecord definition for deserialization of Core service responses.
	/// Provides dictionary-based property access matching the original Expando-based type.
	/// All JSON properties not mapped to explicit members are captured via [JsonExtensionData].
	/// </summary>
	[Serializable]
	public class EntityRecord
	{
		/// <summary>
		/// Dynamic property bag capturing all record field values from the API response.
		/// </summary>
		[JsonExtensionData]
		public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

		/// <summary>
		/// String-based indexer providing access to record field values by name.
		/// Returns null for missing keys instead of throwing.
		/// </summary>
		public object this[string key]
		{
			get => Properties.ContainsKey(key) ? Properties[key] : null;
			set => Properties[key] = value;
		}
	}

	/// <summary>
	/// Gateway-local PageType enum for page classification.
	/// Matches the monolith's WebVella.Erp.Web.Models.PageType values exactly.
	/// Will be superseded by WebVella.Erp.SharedKernel.Models.PageType when created.
	/// </summary>
	public enum PageType
	{
		Home = 0,
		Site = 1,
		Application = 2,
		RecordList = 3,
		RecordCreate = 4,
		RecordDetails = 5,
		RecordManage = 6
	}

	/// <summary>
	/// Gateway-local ErpUserPreferences for user display preferences.
	/// Will be superseded by WebVella.Erp.SharedKernel.Models.ErpUserPreferences when created.
	/// </summary>
	[Serializable]
	public class ErpUserPreferences
	{
		[JsonProperty("sidebar_size")]
		public string SidebarSize { get; set; } = "lg";
	}

	/// <summary>
	/// Gateway-local ErpUser definition for deserialization of Core service authentication responses.
	/// Will be superseded by WebVella.Erp.SharedKernel.Models.ErpUser when created.
	/// </summary>
	[Serializable]
	public class ErpUser
	{
		public ErpUser()
		{
			Id = Guid.Empty;
			Email = string.Empty;
			Password = string.Empty;
			FirstName = string.Empty;
			LastName = string.Empty;
			Username = string.Empty;
			Enabled = true;
			Verified = true;
		}

		[JsonProperty("id")]
		public Guid Id { get; set; }

		[JsonProperty("username")]
		public string Username { get; set; }

		[JsonProperty("email")]
		public string Email { get; set; }

		[JsonIgnore]
		public string Password { get; set; }

		[JsonProperty("firstName")]
		public string FirstName { get; set; }

		[JsonProperty("lastName")]
		public string LastName { get; set; }

		[JsonProperty("image")]
		public string Image { get; set; }

		[JsonIgnore]
		public bool Enabled { get; set; }

		[JsonIgnore]
		public bool Verified { get; set; }

		[JsonProperty("createdOn")]
		public DateTime CreatedOn { get; set; }

		[JsonProperty("lastLoggedIn")]
		public DateTime? LastLoggedIn { get; set; }

		[JsonIgnore]
		public List<ErpRole> Roles { get; set; } = new List<ErpRole>();

		[JsonProperty("is_admin")]
		public bool IsAdmin
		{
			get { return Roles.Any(x => x.Id == SystemIds.AdministratorRoleId); }
		}

		[JsonProperty("preferences")]
		public ErpUserPreferences Preferences { get; set; }

		/// <summary>
		/// Factory method to create an ErpUser from JWT/cookie claims.
		/// Extracts standard claims (sub/nameidentifier for Id, email, given_name, family_name, name).
		/// Used by BaseErpPageModel to hydrate CurrentUser from HttpContext.User.
		/// </summary>
		public static ErpUser FromClaims(System.Collections.Generic.IEnumerable<System.Security.Claims.Claim> claims)
		{
			var user = new ErpUser();
			if (claims == null) return user;

			foreach (var claim in claims)
			{
				switch (claim.Type)
				{
					case System.Security.Claims.ClaimTypes.NameIdentifier:
					case "sub":
						if (Guid.TryParse(claim.Value, out var userId))
							user.Id = userId;
						break;
					case System.Security.Claims.ClaimTypes.Email:
					case "email":
						user.Email = claim.Value;
						break;
					case System.Security.Claims.ClaimTypes.GivenName:
					case "given_name":
						user.FirstName = claim.Value;
						break;
					case System.Security.Claims.ClaimTypes.Surname:
					case "family_name":
						user.LastName = claim.Value;
						break;
					case System.Security.Claims.ClaimTypes.Name:
					case "name":
					case "preferred_username":
						user.Username = claim.Value;
						break;
					case "image":
						user.Image = claim.Value;
						break;
					case System.Security.Claims.ClaimTypes.Role:
					case "role":
						if (Guid.TryParse(claim.Value, out var roleId))
							user.Roles.Add(new ErpRole { Id = roleId });
						break;
				}
			}

			return user;
		}
	}

	/// <summary>
	/// Gateway-local ValidationError for field-level validation errors.
	/// Will be superseded by WebVella.Erp.SharedKernel.Exceptions.ValidationError when created.
	/// </summary>
	public class ValidationError
	{
		public long Index { get; set; }
		public string PropertyName { get; set; }
		public string Message { get; set; }
		public bool IsSystem { get; set; }

		public ValidationError(string fieldName, string message, bool isSystem = false, long index = 0)
		{
			if (index < 0)
				throw new ArgumentException("index");
			if (string.IsNullOrWhiteSpace(message))
				throw new ArgumentException("message");

			PropertyName = fieldName?.ToLowerInvariant();
			Message = message;
			Index = index;
			IsSystem = isSystem;
		}
	}

	/// <summary>
	/// Gateway-local ValidationException for accumulating field-level errors during form submission.
	/// Will be superseded by WebVella.Erp.SharedKernel.Exceptions.ValidationException when created.
	/// </summary>
	public class ValidationException : Exception
	{
		public List<ValidationError> Errors { get; set; } = new List<ValidationError>();

		public new string Message { get; set; } = "";

		public ValidationException() : this(null, null) { }

		public ValidationException(string message) : this(message, null) { }

		public ValidationException(string message = null, Exception inner = null) : base(message, inner)
		{
			Message = message;
			Errors = new List<ValidationError>();
		}

		public void AddError(string fieldName, string message, long index = 0)
		{
			if (string.IsNullOrWhiteSpace(Message))
				Message = message;
			Errors.Add(new ValidationError(fieldName, message, false, index));
		}

		public void CheckAndThrow()
		{
			if (Errors != null && Errors.Count > 0)
				throw this;
		}
	}

	/// <summary>
	/// Scoped per-request context that holds routing state (App, SitemapArea, SitemapNode, Entity, Page)
	/// resolved from the request URL. Injected into all page model constructors via [FromServices].
	///
	/// This is the Gateway-adapted version of WebVella.Erp.Web.ErpRequestContext. All local database calls
	/// (AppService, PageService, EntityManager, RecordManager) are replaced with HTTP calls to the Core
	/// microservice via IHttpClientFactory. The public API surface is preserved for backward compatibility
	/// with all page model constructors.
	///
	/// Registration: Must be registered as Scoped service in Gateway's Program.cs DI container.
	/// </summary>
	public class ErpRequestContext
	{
		private readonly IHttpClientFactory _httpClientFactory;
		private readonly ILogger<ErpRequestContext> _logger;

		/// <summary>
		/// Named HttpClient key for the Core microservice, registered in Program.cs via
		/// builder.Services.AddHttpClient("CoreService", ...).
		/// </summary>
		private const string CoreServiceClientName = "CoreService";

		/// <summary>
		/// The root service provider for this request scope.
		/// Used by page models to resolve additional services.
		/// </summary>
		public IServiceProvider ServiceProvider { get; private set; }

		/// <summary>
		/// The Razor Pages PageContext for the current request.
		/// Set by the page infrastructure during request processing.
		/// </summary>
		public PageContext PageContext { get; set; }

		/// <summary>
		/// The resolved application from the current URL route.
		/// Populated by SetCurrentApp from Core service API response.
		/// </summary>
		public App App { get; internal set; } = null;

		/// <summary>
		/// The resolved sitemap area within the current application.
		/// Populated by SetCurrentApp from the App's Sitemap.Areas collection.
		/// </summary>
		public SitemapArea SitemapArea { get; internal set; } = null;

		/// <summary>
		/// The resolved sitemap node within the current area.
		/// Populated by SetCurrentApp from the SitemapArea.Nodes collection.
		/// </summary>
		public SitemapNode SitemapNode { get; internal set; } = null;

		/// <summary>
		/// The resolved entity metadata for the current page's entity.
		/// Populated by SetCurrentPage from Core service entity API.
		/// </summary>
		public Entity Entity { get; internal set; } = null;

		/// <summary>
		/// The resolved parent entity metadata (for related record pages).
		/// Populated by SetCurrentPage from Core service entity API.
		/// </summary>
		public Entity ParentEntity { get; internal set; } = null;

		/// <summary>
		/// The resolved ERP page definition for the current route.
		/// Populated by SetCurrentPage from Core service page API.
		/// </summary>
		public ErpPage Page { get; internal set; } = null;

		/// <summary>
		/// The resolved parent ERP page definition (for related record pages).
		/// Populated by SetCurrentPage from Core service page API.
		/// </summary>
		public ErpPage ParentPage { get; internal set; } = null;

		/// <summary>
		/// The record ID extracted from the current URL route (e.g., /app/area/node/r/{recordId}).
		/// </summary>
		public Guid? RecordId { get; internal set; } = null;

		/// <summary>
		/// The relation ID extracted from the current URL route for related record pages.
		/// </summary>
		public Guid? RelationId { get; internal set; } = null;

		/// <summary>
		/// The parent record ID extracted from the current URL route for related record pages.
		/// </summary>
		public Guid? ParentRecordId { get; internal set; } = null;

		/// <summary>
		/// Constructs a new ErpRequestContext with the given service provider.
		/// Resolves IHttpClientFactory and ILogger from DI for Core service communication.
		/// </summary>
		/// <param name="serviceProvider">The scoped service provider for this request.</param>
		public ErpRequestContext([FromServices] IServiceProvider serviceProvider)
		{
			ServiceProvider = serviceProvider;
			_httpClientFactory = serviceProvider.GetService<IHttpClientFactory>();
			_logger = serviceProvider.GetService<ILogger<ErpRequestContext>>();
		}

		/// <summary>
		/// Sets the current application context by resolving the app, area, and node from
		/// the URL routing parameters. Replaces the monolith's AppService.GetApplication()
		/// with an HTTP GET to the Core service's application endpoint.
		/// </summary>
		/// <param name="appName">The application name from the URL route.</param>
		/// <param name="areaName">The sitemap area name from the URL route.</param>
		/// <param name="nodeName">The sitemap node name from the URL route.</param>
		public void SetCurrentApp(string appName, string areaName, string nodeName)
		{
			if (!String.IsNullOrWhiteSpace(appName))
			{
				App = FetchApplicationAsync(appName).GetAwaiter().GetResult();
			}
			else
			{
				App = null;
			}

			if (App != null && !String.IsNullOrWhiteSpace(areaName))
			{
				if (App.Sitemap != null && App.Sitemap.Areas.Count > 0)
				{
					SitemapArea = App.Sitemap.Areas.FirstOrDefault(x => x.Name == areaName);
				}
				else
				{
					SitemapArea = null;
				}
			}
			else
			{
				SitemapArea = null;
			}

			if (App != null && SitemapArea != null && !String.IsNullOrWhiteSpace(nodeName))
			{
				if (SitemapArea.Nodes.Count > 0)
				{
					SitemapNode = SitemapArea.Nodes.FirstOrDefault(x => x.Name == nodeName);
				}
				else
				{
					SitemapNode = null;
				}
			}
			else
			{
				SitemapNode = null;
			}
		}

		/// <summary>
		/// Sets the current page context by resolving the page definition, entity metadata,
		/// and parent page from the URL routing parameters. Replaces the monolith's
		/// PageService.GetCurrentPage() and EntityManager.ReadEntity() with HTTP calls
		/// to the Core service.
		/// </summary>
		/// <param name="pageContext">The Razor Pages PageContext for the current request.</param>
		/// <param name="pageName">The page name from the URL route.</param>
		/// <param name="appName">The application name from the URL route.</param>
		/// <param name="areaName">The sitemap area name from the URL route.</param>
		/// <param name="nodeName">The sitemap node name from the URL route.</param>
		/// <param name="recordId">Optional record ID from the URL route.</param>
		/// <param name="relationId">Optional relation ID from the URL route.</param>
		/// <param name="parentRecordId">Optional parent record ID from the URL route.</param>
		public void SetCurrentPage(PageContext pageContext, string pageName, string appName,
			string areaName, string nodeName, Guid? recordId = null, Guid? relationId = null,
			Guid? parentRecordId = null)
		{
			var resolution = FetchCurrentPageAsync(pageName, appName, areaName, nodeName,
				recordId, relationId, parentRecordId).GetAwaiter().GetResult();

			if (resolution != null)
			{
				Page = resolution.Page;

				if (Page != null && Page.EntityId != null
					&& (Page.Type == PageType.RecordList || Page.Type == PageType.RecordDetails
					|| Page.Type == PageType.RecordCreate || Page.Type == PageType.RecordManage))
				{
					Entity = FetchEntityByIdAsync(Page.EntityId ?? Guid.Empty).GetAwaiter().GetResult();
				}

				if (resolution.ParentPage != null)
				{
					ParentPage = resolution.ParentPage;
					if (ParentPage.EntityId != null)
					{
						ParentEntity = FetchEntityByIdAsync(ParentPage.EntityId ?? Guid.Empty)
							.GetAwaiter().GetResult();
					}
				}
			}
			else
			{
				Page = null;
			}
		}

		/// <summary>
		/// Generates the base URL for the current page based on the resolved routing context.
		/// This is pure URL generation logic with no database or service calls — preserved as-is
		/// from the monolith implementation.
		/// </summary>
		/// <returns>The base URL string for the current page context.</returns>
		public string GenerateCurrentPageBaseUrl()
		{
			var context = this;
			// Case 1. Entity Record Page | App:SET, SitemapArea:SET, SitemapNode:SET, Entity:SET, RecordId:SET
			if (context.App != null && context.SitemapArea != null && context.SitemapNode != null
				&& context.Entity != null && context.RecordId != null)
			{
				// Node.Name should equal the Entity.Name
				return $"/{context.App.Name}/{context.SitemapArea.Name}/{context.SitemapNode.Name}/r/{context.RecordId}/";
			}
			// Case 2. Entity List Page | App:SET, SitemapArea:SET, SitemapNode:SET, Entity:SET, RecordId:NOT SET
			else if (context.App != null && context.SitemapArea != null && context.SitemapNode != null
				&& context.Entity != null && context.RecordId == null)
			{
				// Node.Name should equal the Entity.Name
				return $"/{context.App.Name}/{context.SitemapArea.Name}/{context.SitemapNode.Name}/l/";
			}
			// Case 3. Application page | App:SET, SitemapArea:SET, SitemapNode:SET, Entity:NOT SET, RecordId:NOT SET
			else if (context.App != null && context.SitemapArea != null && context.SitemapNode != null
				&& context.Entity == null && context.RecordId == null)
			{
				return $"/{context.App.Name}/{context.SitemapArea.Name}/{context.SitemapNode.Name}/a/";
			}
			// Case 4. Site Page
			else if (context.App != null && context.SitemapArea != null && context.SitemapNode != null
				&& context.Entity != null && context.RecordId != null)
			{
				return $"/s/";
			}
			// Case 5. Default return "#" so it can be created as anchor
			else
			{
				return "#";
			}
		}

		/// <summary>
		/// Gets the identity field value for the current record. Replaces the monolith's direct
		/// EntityManager.ReadEntity() and RecordManager.Find() calls with HTTP calls to the
		/// Core service's entity and record endpoints.
		/// </summary>
		/// <param name="recordId">Optional record ID override. Falls back to context RecordId.</param>
		/// <param name="entityName">Optional entity name override. Falls back to context Entity.Name.</param>
		/// <returns>The string value of the identity field for the specified record.</returns>
		public string GetCurrentRecordIdentityFieldValue(Guid? recordId = null, string entityName = "")
		{
			var context = this;
			if (recordId == null)
			{
				recordId = context.RecordId;
			}
			if (String.IsNullOrWhiteSpace(entityName) && context.Entity != null)
			{
				entityName = context.Entity.Name;
			}

			if (recordId == null)
			{
				throw new Exception("No suitable record Id found");
			}

			if (String.IsNullOrWhiteSpace(entityName))
			{
				throw new Exception("No suitable entity Name found");
			}

			var currentEntity = FetchEntityByNameAsync(entityName).GetAwaiter().GetResult();
			if (currentEntity != null)
			{
				var identityFieldName = "id";
				if (currentEntity.RecordScreenIdField != null)
				{
					var screenField = currentEntity.Fields.FirstOrDefault(x => x.Id == currentEntity.RecordScreenIdField);
					if (screenField != null)
					{
						identityFieldName = screenField.Name;
					}
				}

				var recordData = FetchRecordFieldAsync(entityName, recordId.Value, identityFieldName)
					.GetAwaiter().GetResult();
				if (recordData != null && recordData.Properties.ContainsKey(identityFieldName))
				{
					return recordData[identityFieldName]?.ToString() ?? "";
				}
			}
			return "";
		}

		/// <summary>
		/// Sets simulated route data for programmatic page rendering outside of normal URL routing.
		/// Replaces the monolith's direct EntityManager.ReadEntity() and PageService.GetPage()
		/// calls with HTTP calls to the Core service.
		/// </summary>
		/// <param name="entityId">Optional entity ID to resolve.</param>
		/// <param name="parentEntityId">Optional parent entity ID to resolve.</param>
		/// <param name="pageId">Optional page ID to resolve.</param>
		/// <param name="parentPageId">Optional parent page ID to resolve.</param>
		/// <param name="recordId">Optional record ID to set.</param>
		/// <param name="relationId">Optional relation ID to set.</param>
		/// <param name="parentRecordId">Optional parent record ID to set.</param>
		public void SetSimulatedRouteData(Guid? entityId = null, Guid? parentEntityId = null,
			Guid? pageId = null, Guid? parentPageId = null, Guid? recordId = null,
			Guid? relationId = null, Guid? parentRecordId = null)
		{
			if (entityId != null)
			{
				Entity = FetchEntityByIdAsync(entityId ?? Guid.Empty).GetAwaiter().GetResult();
			}

			if (parentEntityId != null)
			{
				ParentEntity = FetchEntityByIdAsync(parentEntityId ?? Guid.Empty).GetAwaiter().GetResult();
			}

			if (pageId != null)
			{
				Page = FetchPageByIdAsync(pageId ?? Guid.Empty).GetAwaiter().GetResult();
			}

			if (parentPageId != null)
			{
				ParentPage = FetchPageByIdAsync(parentPageId ?? Guid.Empty).GetAwaiter().GetResult();
			}

			if (recordId != null)
			{
				RecordId = recordId;
			}

			if (relationId != null)
			{
				RelationId = relationId;
			}

			if (parentRecordId != null)
			{
				ParentRecordId = parentRecordId;
			}
		}

		#region Private HTTP Helper Methods

		/// <summary>
		/// Creates an HttpClient for the Core microservice using the named client factory.
		/// Falls back to a default HttpClient if the factory is not available.
		/// </summary>
		private HttpClient CreateCoreServiceClient()
		{
			if (_httpClientFactory != null)
			{
				return _httpClientFactory.CreateClient(CoreServiceClientName);
			}

			_logger?.LogWarning("IHttpClientFactory is not available. Creating default HttpClient for Core service.");
			return new HttpClient();
		}

		/// <summary>
		/// Fetches application metadata from the Core service by application name.
		/// Replaces: new AppService().GetApplication(appName)
		/// Endpoint: GET /api/v3/en_US/app/{appName}
		/// </summary>
		private async Task<App> FetchApplicationAsync(string appName)
		{
			try
			{
				using var client = CreateCoreServiceClient();
				var encodedAppName = Uri.EscapeDataString(appName);
				var response = await client.GetAsync($"/api/v3/en_US/app/{encodedAppName}").ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					_logger?.LogWarning("Core service returned {StatusCode} for app '{AppName}'.",
						response.StatusCode, appName);
					return null;
				}

				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var envelope = JsonConvert.DeserializeObject<CoreServiceResponse<App>>(json);
				if (envelope != null && envelope.Success)
				{
					return envelope.Object;
				}

				if (envelope?.Errors != null && envelope.Errors.Count > 0)
				{
					_logger?.LogWarning("Core service returned errors for app '{AppName}': {Errors}",
						appName, string.Join("; ", envelope.Errors.Select(e => e.Message)));
				}

				return null;
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogError(ex, "Failed to fetch application '{AppName}' from Core service.", appName);
				return null;
			}
			catch (TaskCanceledException ex)
			{
				_logger?.LogError(ex, "Request timed out fetching application '{AppName}' from Core service.", appName);
				return null;
			}
			catch (JsonException ex)
			{
				_logger?.LogError(ex, "Failed to deserialize application response for '{AppName}'.", appName);
				return null;
			}
		}

		/// <summary>
		/// Fetches page resolution data from the Core service for the given routing parameters.
		/// Replaces: new PageService().GetCurrentPage(pageContext, pageName, appName, areaName, nodeName, out parentPage, ...)
		/// Endpoint: GET /api/v3/en_US/page/resolve?pageName={pageName}&appName={appName}&...
		/// </summary>
		private async Task<PageResolutionResult> FetchCurrentPageAsync(string pageName,
			string appName, string areaName, string nodeName,
			Guid? recordId, Guid? relationId, Guid? parentRecordId)
		{
			try
			{
				using var client = CreateCoreServiceClient();

				var queryParams = new List<string>();
				if (!string.IsNullOrWhiteSpace(pageName))
					queryParams.Add($"pageName={Uri.EscapeDataString(pageName)}");
				if (!string.IsNullOrWhiteSpace(appName))
					queryParams.Add($"appName={Uri.EscapeDataString(appName)}");
				if (!string.IsNullOrWhiteSpace(areaName))
					queryParams.Add($"areaName={Uri.EscapeDataString(areaName)}");
				if (!string.IsNullOrWhiteSpace(nodeName))
					queryParams.Add($"nodeName={Uri.EscapeDataString(nodeName)}");
				if (recordId.HasValue)
					queryParams.Add($"recordId={recordId.Value}");
				if (relationId.HasValue)
					queryParams.Add($"relationId={relationId.Value}");
				if (parentRecordId.HasValue)
					queryParams.Add($"parentRecordId={parentRecordId.Value}");

				var queryString = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
				var response = await client.GetAsync($"/api/v3/en_US/page/resolve{queryString}").ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					_logger?.LogWarning("Core service returned {StatusCode} for page resolution " +
						"(page='{PageName}', app='{AppName}', area='{AreaName}', node='{NodeName}').",
						response.StatusCode, pageName, appName, areaName, nodeName);
					return null;
				}

				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var envelope = JsonConvert.DeserializeObject<CoreServiceResponse<PageResolutionResult>>(json);
				if (envelope != null && envelope.Success)
				{
					return envelope.Object;
				}

				return null;
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogError(ex, "Failed to fetch page resolution from Core service " +
					"(page='{PageName}', app='{AppName}').", pageName, appName);
				return null;
			}
			catch (TaskCanceledException ex)
			{
				_logger?.LogError(ex, "Request timed out fetching page resolution from Core service.");
				return null;
			}
			catch (JsonException ex)
			{
				_logger?.LogError(ex, "Failed to deserialize page resolution response.");
				return null;
			}
		}

		/// <summary>
		/// Fetches entity metadata from the Core service by entity ID.
		/// Replaces: new EntityManager().ReadEntity(entityId).Object
		/// Endpoint: GET /api/v3/en_US/entity/{entityId}
		/// </summary>
		private async Task<Entity> FetchEntityByIdAsync(Guid entityId)
		{
			try
			{
				using var client = CreateCoreServiceClient();
				var response = await client.GetAsync($"/api/v3/en_US/entity/{entityId}").ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					_logger?.LogWarning("Core service returned {StatusCode} for entity ID '{EntityId}'.",
						response.StatusCode, entityId);
					return null;
				}

				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var envelope = JsonConvert.DeserializeObject<CoreServiceResponse<Entity>>(json);
				if (envelope != null && envelope.Success)
				{
					return envelope.Object;
				}

				return null;
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogError(ex, "Failed to fetch entity '{EntityId}' from Core service.", entityId);
				return null;
			}
			catch (TaskCanceledException ex)
			{
				_logger?.LogError(ex, "Request timed out fetching entity '{EntityId}' from Core service.", entityId);
				return null;
			}
			catch (JsonException ex)
			{
				_logger?.LogError(ex, "Failed to deserialize entity response for '{EntityId}'.", entityId);
				return null;
			}
		}

		/// <summary>
		/// Fetches entity metadata from the Core service by entity name.
		/// Replaces: new EntityManager().ReadEntity(entityName).Object
		/// Endpoint: GET /api/v3/en_US/entity/name/{entityName}
		/// </summary>
		private async Task<Entity> FetchEntityByNameAsync(string entityName)
		{
			try
			{
				using var client = CreateCoreServiceClient();
				var encodedName = Uri.EscapeDataString(entityName);
				var response = await client.GetAsync($"/api/v3/en_US/entity/name/{encodedName}").ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					_logger?.LogWarning("Core service returned {StatusCode} for entity name '{EntityName}'.",
						response.StatusCode, entityName);
					return null;
				}

				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var envelope = JsonConvert.DeserializeObject<CoreServiceResponse<Entity>>(json);
				if (envelope != null && envelope.Success)
				{
					return envelope.Object;
				}

				return null;
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogError(ex, "Failed to fetch entity by name '{EntityName}' from Core service.", entityName);
				return null;
			}
			catch (TaskCanceledException ex)
			{
				_logger?.LogError(ex, "Request timed out fetching entity by name '{EntityName}'.", entityName);
				return null;
			}
			catch (JsonException ex)
			{
				_logger?.LogError(ex, "Failed to deserialize entity response for name '{EntityName}'.", entityName);
				return null;
			}
		}

		/// <summary>
		/// Fetches a specific field value for a record from the Core service.
		/// Replaces: new RecordManager().Find(new EntityQuery(entityName, fieldName, QueryEQ("id", recordId)))
		/// Endpoint: GET /api/v3/en_US/record/{entityName}/{recordId}?fields={fieldName}
		/// </summary>
		private async Task<EntityRecord> FetchRecordFieldAsync(string entityName, Guid recordId, string fieldName)
		{
			try
			{
				using var client = CreateCoreServiceClient();
				var encodedEntityName = Uri.EscapeDataString(entityName);
				var encodedFieldName = Uri.EscapeDataString(fieldName);
				var response = await client.GetAsync(
					$"/api/v3/en_US/record/{encodedEntityName}/{recordId}?fields={encodedFieldName}")
					.ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					_logger?.LogWarning("Core service returned {StatusCode} for record " +
						"(entity='{EntityName}', id='{RecordId}', field='{FieldName}').",
						response.StatusCode, entityName, recordId, fieldName);
					return null;
				}

				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var envelope = JsonConvert.DeserializeObject<CoreServiceResponse<EntityRecord>>(json);
				if (envelope != null && envelope.Success)
				{
					return envelope.Object;
				}

				return null;
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogError(ex, "Failed to fetch record field from Core service " +
					"(entity='{EntityName}', id='{RecordId}').", entityName, recordId);
				return null;
			}
			catch (TaskCanceledException ex)
			{
				_logger?.LogError(ex, "Request timed out fetching record from Core service.");
				return null;
			}
			catch (JsonException ex)
			{
				_logger?.LogError(ex, "Failed to deserialize record response.");
				return null;
			}
		}

		/// <summary>
		/// Fetches a page definition from the Core service by page ID.
		/// Replaces: new PageService().GetPage(pageId)
		/// Endpoint: GET /api/v3/en_US/page/{pageId}
		/// </summary>
		private async Task<ErpPage> FetchPageByIdAsync(Guid pageId)
		{
			try
			{
				using var client = CreateCoreServiceClient();
				var response = await client.GetAsync($"/api/v3/en_US/page/{pageId}").ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					_logger?.LogWarning("Core service returned {StatusCode} for page ID '{PageId}'.",
						response.StatusCode, pageId);
					return null;
				}

				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				var envelope = JsonConvert.DeserializeObject<CoreServiceResponse<ErpPage>>(json);
				if (envelope != null && envelope.Success)
				{
					return envelope.Object;
				}

				return null;
			}
			catch (HttpRequestException ex)
			{
				_logger?.LogError(ex, "Failed to fetch page '{PageId}' from Core service.", pageId);
				return null;
			}
			catch (TaskCanceledException ex)
			{
				_logger?.LogError(ex, "Request timed out fetching page '{PageId}' from Core service.", pageId);
				return null;
			}
			catch (JsonException ex)
			{
				_logger?.LogError(ex, "Failed to deserialize page response for '{PageId}'.", pageId);
				return null;
			}
		}

		#endregion

		#region Internal Response Envelope

		/// <summary>
		/// Generic response envelope for deserializing Core service API responses.
		/// Follows the standard BaseResponseModel pattern: { success, errors, message, object }.
		/// </summary>
		/// <typeparam name="T">The type of the response payload.</typeparam>
		private class CoreServiceResponse<T>
		{
			[JsonProperty("success")]
			public bool Success { get; set; }

			[JsonProperty("message")]
			public string Message { get; set; }

			[JsonProperty("errors")]
			public List<ErrorModel> Errors { get; set; } = new List<ErrorModel>();

			[JsonProperty("object")]
			public T Object { get; set; }

			[JsonProperty("timestamp")]
			public DateTime Timestamp { get; set; }
		}

		#endregion
	}
}
