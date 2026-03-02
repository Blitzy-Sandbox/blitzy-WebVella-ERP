using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Web;
using WebVella.Erp.SharedKernel;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Gateway.Models
{
	#region Gateway-Local Helper Types

	/// <summary>
	/// Represents a navigation menu item in the Gateway UI.
	/// Adapted from WebVella.Erp.Web.Models.MenuItem for the Gateway/BFF layer.
	/// </summary>
	public class MenuItem
	{
		public Guid Id { get; set; } = Guid.NewGuid();
		public Guid? ParentId { get; set; }
		public string Content { get; set; } = string.Empty;
		public string Class { get; set; } = string.Empty;
		public bool IsHtml { get; set; } = true;
		public bool RenderWrapper { get; set; } = true;
		public List<MenuItem> Nodes { get; set; } = new List<MenuItem>();
		public bool IsDropdownRight { get; set; } = false;
		public int SortOrder { get; set; } = 10;
	}

	/// <summary>
	/// Result of URL path parsing for the ERP routing system.
	/// Adapted from WebVella.Erp.Web.Models.UrlInfo.
	/// </summary>
	public class UrlInfo
	{
		public bool HasRelation { get; set; } = false;
		public PageType PageType { get; set; } = PageType.Home;
		public string AppName { get; set; } = string.Empty;
		public string AreaName { get; set; } = string.Empty;
		public string NodeName { get; set; } = string.Empty;
		public string PageName { get; set; } = string.Empty;
		public Guid? RecordId { get; set; }
		public Guid? RelationId { get; set; }
		public Guid? ParentRecordId { get; set; }
	}

	/// <summary>
	/// Named property within a page data model, carrying both name and typed value.
	/// </summary>
	public class PageDataModelProperty
	{
		public string Name { get; set; } = string.Empty;
		public Type Type { get; set; }
		public object Value { get; set; }
	}

	/// <summary>
	/// Container for page-level data sources and computed properties.
	/// Adapted from WebVella.Erp.Web.Models.PageDataModel.
	/// </summary>
	public class PageDataModel
	{
		public List<PageDataModelProperty> Properties { get; set; } = new List<PageDataModelProperty>();

		/// <summary>
		/// Gets or sets a data property by name.
		/// </summary>
		public object this[string key]
		{
			get
			{
				var prop = Properties.FirstOrDefault(x => x.Name == key);
				return prop?.Value;
			}
			set
			{
				var prop = Properties.FirstOrDefault(x => x.Name == key);
				if (prop != null)
				{
					prop.Value = value;
				}
				else
				{
					Properties.Add(new PageDataModelProperty
					{
						Name = key,
						Value = value,
						Type = value?.GetType()
					});
				}
			}
		}

		/// <summary>
		/// Retrieves a named property value from the data model.
		/// </summary>
		public object GetProperty(string propertyName)
		{
			var prop = Properties.FirstOrDefault(x => x.Name == propertyName);
			return prop?.Value;
		}
	}

	/// <summary>
	/// Gateway-local application model representing a registered ERP application.
	/// Replaces the monolith WebVella.Erp.Web.Models.App for the Gateway/BFF layer.
	/// </summary>
	public class App
	{
		public Guid Id { get; set; } = Guid.Empty;
		public string Name { get; set; } = string.Empty;
		public string Label { get; set; } = string.Empty;
		public string Description { get; set; } = string.Empty;
		public string IconClass { get; set; } = string.Empty;
		public string Author { get; set; } = string.Empty;
		public string Color { get; set; } = string.Empty;
		public int Weight { get; set; } = 10;
		public List<Guid> Access { get; set; } = new List<Guid>();
		public AppSitemap Sitemap { get; set; } = new AppSitemap();
	}

	/// <summary>
	/// Application sitemap containing area definitions with their nodes.
	/// </summary>
	public class AppSitemap
	{
		public List<SitemapArea> Areas { get; set; } = new List<SitemapArea>();
	}

	/// <summary>
	/// Gateway-local page representation for the ERP page system.
	/// Replaces the monolith WebVella.Erp.Web.Models.ErpPage.
	/// </summary>
	public class ErpPage
	{
		public Guid Id { get; set; } = Guid.Empty;
		public string Name { get; set; } = string.Empty;
		public string Label { get; set; } = string.Empty;
		public PageType Type { get; set; } = PageType.Application;
		public Guid? AppId { get; set; }
		public Guid? EntityId { get; set; }
		public Guid? AreaId { get; set; }
		public Guid? NodeId { get; set; }
		public int Weight { get; set; } = 10;
		public bool IsRazorBody { get; set; } = false;
		public string System { get; set; } = string.Empty;
		public List<Guid> Roles { get; set; } = new List<Guid>();
	}

	/// <summary>
	/// Lightweight application context for the Gateway/BFF layer.
	/// Replaces the monolith ErpAppContext singleton with a DI-friendly instance.
	/// </summary>
	public class GatewayAppContext
	{
		public IServiceProvider ServiceProvider { get; set; }
		public string Theme { get; set; } = string.Empty;
		public string Scripts { get; set; } = string.Empty;

		/// <summary>
		/// Creates a GatewayAppContext from the current request service provider.
		/// </summary>
		public static GatewayAppContext FromServices(IServiceProvider serviceProvider)
		{
			return new GatewayAppContext { ServiceProvider = serviceProvider };
		}

		/// <summary>
		/// Singleton-like accessor for backward compatibility with the monolith
		/// pattern ErpAppContext.Current. In the Gateway, this is set during
		/// application startup and provides the root service provider.
		/// </summary>
		public static GatewayAppContext Current { get; set; }
	}

	#endregion

	/// <summary>
	/// Base page model for ALL Razor Pages in the Gateway/BFF layer.
	/// Every Gateway Razor Page (Login, Logout, Index, Site, ApplicationHome,
	/// ApplicationNode, RecordManage, RecordList, RecordDetails, RecordCreate,
	/// and all RecordRelatedRecord* pages) inherits from this class.
	///
	/// Preserves the public API surface of WebVella.Erp.Web.Models.BaseErpPageModel
	/// while replacing direct database access and local service calls with
	/// Gateway-compatible alternatives (JWT claims, Core service HTTP calls,
	/// cached sitemap data).
	/// </summary>
	[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
	public class BaseErpPageModel : PageModel
	{
		#region Bind Properties

		[BindProperty(SupportsGet = true)]
		public string AppName { get; set; } = "";

		[BindProperty(SupportsGet = true)]
		public string AreaName { get; set; } = "";

		[BindProperty(SupportsGet = true)]
		public string NodeName { get; set; } = "";

		[BindProperty(SupportsGet = true)]
		public string PageName { get; set; } = "";

		[BindProperty(SupportsGet = true)]
		public Guid? RecordId { get; set; } = null;

		[BindProperty(SupportsGet = true)]
		public Guid? RelationId { get; set; } = null;

		[BindProperty(SupportsGet = true)]
		public Guid? ParentRecordId { get; set; } = null;

		[BindProperty(Name = "returnUrl", SupportsGet = true)]
		public string ReturnUrl { get; set; } = "";

		#endregion

		#region Properties

		private ErpUser currentUser = null;

		/// <summary>
		/// The currently authenticated ERP user, extracted from JWT/cookie claims.
		/// Replaces AuthService.GetUser(User) which previously hit the database.
		/// </summary>
		public ErpUser CurrentUser
		{
			get
			{
				if (currentUser == null && User?.Identity?.IsAuthenticated == true)
				{
					currentUser = ErpUser.FromClaims(User.Claims);
				}
				return currentUser;
			}
		}

		/// <summary>
		/// Scoped request context containing resolved app, area, node, page,
		/// and entity information. Populated via DI in derived page constructors.
		/// </summary>
		public ErpRequestContext ErpRequestContext { get; protected set; }

		/// <summary>
		/// Gateway application context replacing the monolith singleton
		/// ErpAppContext.Current. Populated from DI during Init().
		/// </summary>
		public GatewayAppContext ErpAppContext { get; protected set; }

		/// <summary>
		/// Page data model container for data source properties used during rendering.
		/// </summary>
		public PageDataModel DataModel { get; protected set; } = null;

		/// <summary>
		/// Validation exception accumulator for form submissions.
		/// Collects field-level errors during ValidateRecordSubmission.
		/// </summary>
		public ValidationException Validation { get; private set; } = new ValidationException();

		/// <summary>
		/// The current request URL (path + query string, excluding returnUrl param).
		/// </summary>
		public string CurrentUrl { get; set; } = "";

		/// <summary>
		/// Hook key for page lifecycle hooks, read from query string.
		/// Preserved for backward compatibility with the hook-to-event migration.
		/// </summary>
		public string HookKey
		{
			get
			{
				string hookKey = string.Empty;
				if (PageContext?.HttpContext?.Request?.Query?.ContainsKey("hookKey") == true)
					hookKey = HttpContext.Request.Query["hookKey"].ToString();
				return hookKey;
			}
		}

		/// <summary>Toolbar menu items rendered at the top of the page.</summary>
		public List<MenuItem> ToolbarMenu { get; private set; } = new List<MenuItem>();

		/// <summary>Sidebar menu items rendered in the left navigation panel.</summary>
		public List<MenuItem> SidebarMenu { get; private set; } = new List<MenuItem>();

		/// <summary>Global site-level menu items.</summary>
		public List<MenuItem> SiteMenu { get; private set; } = new List<MenuItem>();

		/// <summary>Application-level navigation menu built from the sitemap.</summary>
		public List<MenuItem> ApplicationMenu { get; private set; } = new List<MenuItem>();

		/// <summary>User account menu items (profile, settings, logout).</summary>
		public List<MenuItem> UserMenu { get; private set; } = new List<MenuItem>();

		#endregion

		#region Init Method

		/// <summary>
		/// Initializes the page model: parses the URL, resolves the current
		/// app/area/node/page, performs role-based access control, and builds
		/// navigation menus.
		///
		/// Adapted from monolith BaseErpPageModel.Init(). Replaces:
		///   - new PageService().GetInfoFromPath() → Gateway-local ParseUrlInfo()
		///   - ErpRequestContext.SetCurrentApp() with DB calls → Gateway version using HTTP/cache
		///   - ErpRequestContext.SetCurrentPage() with DB calls → Gateway version using HTTP/cache
		///   - new PageService().GetAppControlledPages() → Gateway navigation building
		///   - ErpAppContext.Current → GatewayAppContext from DI
		/// </summary>
		/// <returns>Null on success; redirect IActionResult on access denial.</returns>
		public virtual IActionResult Init(string appName = "", string areaName = "",
			string nodeName = "", string pageName = "",
			Guid? recordId = null, Guid? relationId = null,
			Guid? parentRecordId = null)
		{
			// Phase 1: Parameter normalization — use local variables like the original
			if (string.IsNullOrWhiteSpace(appName)) appName = AppName;
			if (string.IsNullOrWhiteSpace(areaName)) areaName = AreaName;
			if (string.IsNullOrWhiteSpace(nodeName)) nodeName = NodeName;
			if (string.IsNullOrWhiteSpace(pageName)) pageName = PageName;
			if (recordId == null) recordId = RecordId;
			if (relationId == null) relationId = RelationId;
			if (parentRecordId == null) parentRecordId = ParentRecordId;

			// Phase 2: URL parsing — replaces new PageService().GetInfoFromPath()
			var urlInfo = ParseUrlInfo(HttpContext.Request.Path);
			if (string.IsNullOrWhiteSpace(appName))
			{
				appName = urlInfo.AppName;
				if (AppName != appName)
					AppName = appName;
			}
			if (string.IsNullOrWhiteSpace(areaName))
			{
				areaName = urlInfo.AreaName;
				if (AreaName != areaName)
					AreaName = areaName;
			}
			if (string.IsNullOrWhiteSpace(nodeName))
			{
				nodeName = urlInfo.NodeName;
				if (NodeName != nodeName)
					NodeName = nodeName;
			}
			if (string.IsNullOrWhiteSpace(pageName))
			{
				pageName = urlInfo.PageName;
				if (PageName != pageName)
					PageName = pageName;
			}
			if (recordId == null)
			{
				recordId = urlInfo.RecordId;
				if (RecordId != recordId)
					RecordId = recordId;
			}
			if (relationId == null)
			{
				relationId = urlInfo.RelationId;
				if (RelationId != relationId)
					RelationId = relationId;
			}
			if (parentRecordId == null)
			{
				parentRecordId = urlInfo.ParentRecordId;
				if (ParentRecordId != parentRecordId)
					ParentRecordId = parentRecordId;
			}

			// Phase 3: Resolve ErpRequestContext from DI if not already injected
			if (ErpRequestContext == null)
			{
				ErpRequestContext = HttpContext.RequestServices.GetService<ErpRequestContext>();
			}

			// Phase 4: Set current app/area/node — Gateway version delegates to
			// ErpRequestContext which uses HTTP calls or cached sitemap
			if (ErpRequestContext != null)
			{
				ErpRequestContext.SetCurrentApp(appName, areaName, nodeName);
				ErpRequestContext.SetCurrentPage(PageContext, pageName, appName, areaName,
					nodeName, recordId, relationId, parentRecordId);
			}

			// Phase 5: Role-based access control (preserved from monolith)
			List<Guid> currentUserRoles = new List<Guid>();
			if (CurrentUser != null)
				currentUserRoles.AddRange(CurrentUser.Roles.Select(x => x.Id));

			if (ErpRequestContext?.App != null)
			{
				if (ErpRequestContext.App.Access == null || ErpRequestContext.App.Access.Count == 0)
					return new LocalRedirectResult("/error?401");

				IEnumerable<Guid> rolesWithAccess = ErpRequestContext.App.Access.Intersect(currentUserRoles);
				if (!rolesWithAccess.Any())
					return new LocalRedirectResult("/error?401");
			}
			else if (!currentUserRoles.Contains(SystemIds.AdministratorRoleId)
				&& urlInfo.PageType != PageType.Home && urlInfo.PageType != PageType.Site)
			{
				return new LocalRedirectResult("/error?401");
			}

			// Phase 6: Propagate IDs and page context
			if (ErpRequestContext != null)
			{
				ErpRequestContext.RecordId = recordId;
				ErpRequestContext.RelationId = relationId;
				ErpRequestContext.ParentRecordId = parentRecordId;
				ErpRequestContext.PageContext = PageContext;
			}

			// Phase 7: ReturnUrl from query string
			if (PageContext.HttpContext.Request.Query.ContainsKey("returnUrl"))
			{
				ReturnUrl = HttpUtility.UrlDecode(
					PageContext.HttpContext.Request.Query["returnUrl"].ToString());
			}

			// Phase 8: GatewayAppContext — replaces ErpAppContext.Current
			ErpAppContext = GatewayAppContext.Current
				?? GatewayAppContext.FromServices(HttpContext.RequestServices);

			// Phase 9: CurrentUrl — replaces PageUtils.GetCurrentUrl()
			CurrentUrl = GetCurrentUrl(PageContext.HttpContext);

			// Phase 10: Build navigation menus
			BuildNavigationMenus(urlInfo);

			// Phase 11: Seed DataModel
			DataModel = new PageDataModel();
			DataModel["AppName"] = AppName;
			DataModel["AreaName"] = AreaName;
			DataModel["NodeName"] = NodeName;
			DataModel["PageName"] = PageName;
			if (RecordId.HasValue) DataModel["RecordId"] = RecordId.Value;
			if (RelationId.HasValue) DataModel["RelationId"] = RelationId.Value;
			if (ParentRecordId.HasValue) DataModel["ParentRecordId"] = ParentRecordId.Value;

			return null;
		}

		#endregion

		#region Navigation Menu Building

		/// <summary>
		/// Builds navigation menus from the current app's sitemap.
		/// Adapted from the monolith Init() method navigation logic.
		/// Replaces new PageService().GetAppControlledPages() and
		/// new PageService().GetSitePages() with Gateway-local menu building.
		/// </summary>
		protected virtual void BuildNavigationMenus(UrlInfo urlInfo)
		{
			ApplicationMenu = new List<MenuItem>();
			SiteMenu = new List<MenuItem>();

			if (ErpRequestContext?.App == null)
				return;

			var sitemap = ErpRequestContext.App.Sitemap;
			if (sitemap?.Areas == null)
				return;

			// Build application navigation from sitemap areas and nodes
			foreach (var area in sitemap.Areas)
			{
				if (area.Nodes == null || area.Nodes.Count == 0)
					continue;

				var areaMenuItem = new MenuItem();

				if (area.Nodes.Count > 1)
				{
					// Area with multiple nodes: dropdown with caret
					var areaLink = $"<a href=\"javascript: void(0)\" title=\"{area.Label}\" data-navclick-handler>";
					areaLink += $"<span class=\"menu-label\">{area.Label}</span>";
					areaLink += $"<span class=\"menu-nav-icon fa fa-angle-down nav-caret\"></span>";
					areaLink += $"</a>";
					areaMenuItem = new MenuItem()
					{
						Id = area.Id,
						Content = areaLink
					};

					foreach (var node in area.Nodes)
					{
						var nodeUrl = ResolveNodeUrl(node, area);
						var nodeLink = !string.IsNullOrWhiteSpace(nodeUrl)
							? $"<a class=\"dropdown-item\" href=\"{nodeUrl}\" title=\"{node.Label}\"><span class=\"{node.IconClass} icon fa-fw\"></span>{node.Label}</a>"
							: $"<a class=\"dropdown-item\" href=\"#\" onclick=\"return false\" title=\"{node.Label}\"><span class=\"{node.IconClass} icon fa-fw\"></span>{node.Label}</a>";

						areaMenuItem.Nodes.Add(new MenuItem()
						{
							Content = nodeLink,
							Id = node.Id,
							ParentId = node.ParentId,
							SortOrder = node.Weight
						});
					}
				}
				else if (area.Nodes.Count == 1)
				{
					// Single-node area: direct link
					var singleNode = area.Nodes[0];
					var nodeUrl = ResolveNodeUrl(singleNode, area);
					var areaLink = $"<a href=\"{nodeUrl ?? "#"}\" title=\"{area.Label}\">";
					areaLink += $"<span class=\"menu-label\">{area.Label}</span>";
					areaLink += $"</a>";
					areaMenuItem = new MenuItem()
					{
						Content = areaLink,
						Id = singleNode.Id,
						ParentId = singleNode.ParentId,
						SortOrder = singleNode.Weight
					};
				}

				// Mark current area as active
				if (ErpRequestContext.SitemapArea == null && ErpRequestContext.Page != null
					&& ErpRequestContext.Page.Type != PageType.Application)
				{
					// Page not found scenario — handled in derived pages if needed
				}

				if (ErpRequestContext.SitemapArea != null && area.Id == ErpRequestContext.SitemapArea.Id)
					areaMenuItem.Class = "current";

				// Handle URL-type nodes for current detection
				if (ErpRequestContext.SitemapArea == null)
				{
					var urlNodes = area.Nodes.FindAll(x => x.Type == SitemapNodeType.Url);
					var path = HttpContext.Request.Path;
					foreach (var urlNode in urlNodes)
					{
						if (path == urlNode.Url)
						{
							areaMenuItem.Class = "current";
						}
					}
				}

				ApplicationMenu.Add(areaMenuItem);
			}
		}

		/// <summary>
		/// Resolves URL for a sitemap node by type.
		/// Replaces the monolith logic that used PageService.GetAppControlledPages()
		/// to determine first list/app page per node. In the Gateway, URLs are
		/// resolved from the node type and stored URL data.
		/// </summary>
		protected virtual string ResolveNodeUrl(SitemapNode node, SitemapArea area)
		{
			if (ErpRequestContext?.App == null) return "#";

			switch (node.Type)
			{
				case SitemapNodeType.Url:
					return node.Url ?? "#";

				case SitemapNodeType.ApplicationPage:
					// Use stored URL or generate from naming convention
					if (!string.IsNullOrWhiteSpace(node.Url))
						return node.Url;
					return $"/{ErpRequestContext.App.Name}/{area.Name}/{node.Name}/a/";

				case SitemapNodeType.EntityList:
					// Use stored URL or generate entity list URL
					if (!string.IsNullOrWhiteSpace(node.Url))
						return node.Url;
					return $"/{ErpRequestContext.App.Name}/{area.Name}/{node.Name}/l/";

				default:
					return "#";
			}
		}

		#endregion

		#region Helper Methods

		/// <summary>
		/// Checks whether the DataModel contains the expected record properties.
		/// Adapted from monolith: checks both Record (for RecordId) and
		/// ParentRecord (for ParentRecordId).
		/// </summary>
		public virtual bool RecordsExists()
		{
			if (RecordId.HasValue && DataModel?.GetProperty("Record") == null)
				return false;
			if (ParentRecordId.HasValue && DataModel?.GetProperty("ParentRecord") == null)
				return false;

			return true;
		}

		/// <summary>
		/// Validates a submitted entity record against required field constraints.
		/// Adapted from monolith: iterates record properties, skips relation-prefixed
		/// keys ($), looks up Field metadata, checks Required constraint.
		/// </summary>
		public virtual void ValidateRecordSubmission(EntityRecord postObject, Entity entity,
			ValidationException validation)
		{
			if (entity == null || postObject == null || postObject.Properties.Count == 0 || validation == null)
				return;

			foreach (var property in postObject.Properties)
			{
				// Skip relation properties
				if (property.Key.StartsWith("$"))
					continue;

				Field fieldMeta = entity.Fields.FirstOrDefault(x => x.Name == property.Key);
				if (fieldMeta != null)
				{
					if (fieldMeta.Required &&
						(property.Value == null || string.IsNullOrWhiteSpace(property.Value.ToString())))
					{
						validation.Errors.Add(new ValidationError(property.Key, "Required"));
					}
				}
			}
		}

		/// <summary>
		/// Sets ViewData properties for the layout: border color, CSS classes,
		/// app name, background style.
		/// Adapted from monolith BeforeRender(): preserves BodyBorderColor default
		/// of "#555", sidebar size from user preferences, and background image styling.
		/// </summary>
		public virtual void BeforeRender()
		{
			// BodyBorderColor: default #555, override with app color
			ViewData["BodyBorderColor"] = "#555";
			if (ErpRequestContext?.App != null && !string.IsNullOrWhiteSpace(ErpRequestContext.App.Color))
			{
				ViewData["BodyBorderColor"] = ErpRequestContext.App.Color;
			}

			// BodyClass: toolbar and sidebar classes
			if (ToolbarMenu.Count > 0)
			{
				var bodyClass = ViewData.ContainsKey("BodyClass") ? ViewData["BodyClass"]?.ToString()?.ToLowerInvariant() ?? "" : "";
				if (!bodyClass.Contains("has-toolbar"))
				{
					ViewData["BodyClass"] = bodyClass + " has-toolbar ";
				}
			}
			if (SidebarMenu.Count > 0)
			{
				var bodyClass = ViewData.ContainsKey("BodyClass") ? ViewData["BodyClass"]?.ToString()?.ToLowerInvariant() ?? "" : "";
				var classAddon = "";
				if (!bodyClass.Contains("sidebar-"))
				{
					if (CurrentUser != null && CurrentUser.Preferences != null
						&& !string.IsNullOrWhiteSpace(CurrentUser.Preferences.SidebarSize))
					{
						if (CurrentUser.Preferences.SidebarSize != "lg")
							CurrentUser.Preferences.SidebarSize = "sm";

						classAddon = $" sidebar-{CurrentUser.Preferences.SidebarSize} ";
					}
					else
					{
						classAddon = " sidebar-sm ";
					}
					ViewData["BodyClass"] = bodyClass + classAddon;
				}
			}

			// AppName from ErpSettings
			ViewData["AppName"] = ErpSettings.IsInitialized
				? (ErpSettings.AppName ?? "WebVella ERP") : "WebVella ERP";

			// SystemMasterBodyStyle — background image from configuration
			ViewData["SystemMasterBodyStyle"] = "";
			var bgImageUrl = ErpSettings.Configuration?["Settings:SystemMasterBackgroundImageUrl"];
			if (!string.IsNullOrWhiteSpace(bgImageUrl))
			{
				ViewData["SystemMasterBodyStyle"] = "background-image: url('" + bgImageUrl
					+ "');background-position: top center;background-repeat: repeat;min-height: 100vh; ";
			}
		}

		/// <summary>Retrieves a data source property by name, or null.</summary>
		public virtual object TryGetDataSourceProperty(string propertyName)
		{
			if (DataModel == null)
				return null;

			var dataSource = DataModel.GetProperty(propertyName);
			if (dataSource != null)
				return dataSource;

			return null;
		}

		/// <summary>Retrieves a typed data source property, or default(T).</summary>
		public virtual T TryGetDataSourceProperty<T>(string propertyName)
		{
			if (DataModel == null)
				return default(T);

			var dataSource = DataModel.GetProperty(propertyName);
			if (dataSource != null && dataSource is T)
				return (T)dataSource;

			return default(T);
		}

		/// <summary>
		/// Factory creating a simulated BaseErpPageModel for testing or
		/// programmatic use outside normal Razor Pages lifecycle.
		/// Preserves the original monolith signature: takes ErpRequestContext
		/// and ErpUser rather than HttpContext.
		/// </summary>
		public static BaseErpPageModel CreatePageModelSimulation(
			ErpRequestContext erpRequestContext,
			ErpUser currentUserParam)
		{
			var pageModel = new BaseErpPageModel();
			pageModel.ErpRequestContext = erpRequestContext;
			pageModel.currentUser = currentUserParam;
			pageModel.AppName = erpRequestContext?.App != null ? erpRequestContext.App.Name : "";
			pageModel.AreaName = erpRequestContext?.SitemapArea != null ? erpRequestContext.SitemapArea.Name : "";
			pageModel.NodeName = erpRequestContext?.SitemapNode != null ? erpRequestContext.SitemapNode.Name : "";
			pageModel.PageName = erpRequestContext?.Page != null ? erpRequestContext.Page.Name : "";
			pageModel.RecordId = erpRequestContext?.RecordId;
			pageModel.DataModel = new PageDataModel();
			return pageModel;
		}

		/// <summary>Adds a menu item to UserMenu, re-sorting by SortOrder.</summary>
		public virtual void AddUserMenu(MenuItem menu)
		{
			UserMenu.Add(menu);
			UserMenu = UserMenu.OrderBy(x => x.SortOrder).ToList();
		}

		#endregion

		#region Static URL Parsing Helpers

		/// <summary>
		/// Parses the request path to extract ERP routing info.
		/// Pure logic — no DB/service calls. Adapted from PageService.GetInfoFromPath().
		/// Handles patterns:
		///   /                                    → Home
		///   /s/{pageName}                        → Site page
		///   /{app}                               → Application home
		///   /{app}/a/{area}/{node}               → Application page
		///   /{app}/a/{area}/{node}/l/{page}      → RecordList
		///   /{app}/a/{area}/{node}/c/{page}      → RecordCreate
		///   /{app}/a/{area}/{node}/r/{page}/{id} → RecordDetails
		///   /{app}/a/{area}/{node}/m/{page}/{id} → RecordManage
		///   .../rel/{relId}/{parentId}            → Relation data
		/// </summary>
		internal static UrlInfo ParseUrlInfo(string path)
		{
			var result = new UrlInfo();
			if (string.IsNullOrWhiteSpace(path) || path == "/")
			{
				result.PageType = PageType.Home;
				return result;
			}

			path = path.TrimEnd('/');
			if (!path.StartsWith("/")) path = "/" + path;
			var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

			if (segments.Length == 0) { result.PageType = PageType.Home; return result; }

			// Site pages: /s/{pageName}
			if (segments.Length >= 2 && segments[0] == "s")
			{
				result.PageType = PageType.Site;
				result.PageName = segments[1];
				return result;
			}

			result.AppName = segments[0];

			// Need /{appName}/a/{area}/{node} minimum for area/node resolution
			if (segments.Length < 4 || segments[1] != "a")
			{
				result.PageType = PageType.Application;
				return result;
			}

			result.AreaName = segments[2];
			result.NodeName = segments[3];

			if (segments.Length < 6)
			{
				result.PageType = PageType.Application;
				return result;
			}

			var typeIndicator = segments[4];
			result.PageName = segments[5];

			switch (typeIndicator)
			{
				case "l": result.PageType = PageType.RecordList; break;
				case "c": result.PageType = PageType.RecordCreate; break;
				case "r": result.PageType = PageType.RecordDetails; break;
				case "m": result.PageType = PageType.RecordManage; break;
				case "a": result.PageType = PageType.Application; break;
				default: result.PageType = PageType.Application; break;
			}

			// Record ID
			if (segments.Length > 6 && Guid.TryParse(segments[6], out var recId))
				result.RecordId = recId;

			// Relation data: .../rel/{relId}/{parentRecordId}
			if (segments.Length > 7 && segments[7] == "rel")
			{
				result.HasRelation = true;
				if (segments.Length > 8 && Guid.TryParse(segments[8], out var relId))
					result.RelationId = relId;
				if (segments.Length > 9 && Guid.TryParse(segments[9], out var parentRecId))
					result.ParentRecordId = parentRecId;
			}

			return result;
		}

		/// <summary>
		/// Extracts current URL stripping the returnUrl query parameter.
		/// Pure logic from PageUtils.GetCurrentUrl().
		/// </summary>
		internal static string GetCurrentUrl(HttpContext httpContext)
		{
			if (httpContext?.Request == null) return "/";

			var path = httpContext.Request.Path.Value ?? "/";
			var qs = httpContext.Request.QueryString.Value ?? string.Empty;
			if (string.IsNullOrEmpty(qs)) return path;

			var queryDict = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(qs);
			var filtered = new Dictionary<string, string>();
			foreach (var kvp in queryDict)
			{
				if (!string.Equals(kvp.Key, "returnUrl", StringComparison.OrdinalIgnoreCase))
					filtered[kvp.Key] = kvp.Value.ToString();
			}

			if (filtered.Count == 0) return path;
			var rebuilt = string.Join("&",
				filtered.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
			return $"{path}?{rebuilt}";
		}

		#endregion
	}
}
