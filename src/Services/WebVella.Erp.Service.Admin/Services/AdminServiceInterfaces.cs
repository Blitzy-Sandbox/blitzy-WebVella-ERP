using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Admin.Services
{
	#region Supporting Types

	/// <summary>
	/// Represents the application sitemap containing ordered navigation areas.
	/// This is a lightweight container used by <see cref="IAppService.OrderSitemap"/>
	/// to sort and return a fully-ordered sitemap structure for rendering.
	/// Derived from the monolith's <c>WebVella.Erp.Web.Models.Sitemap</c>.
	/// </summary>
	public class Sitemap
	{
		/// <summary>
		/// Collection of top-level sitemap areas, each containing groups and navigation nodes.
		/// Areas are the primary navigation containers for application sections.
		/// </summary>
		public List<SitemapArea> Areas { get; set; } = new List<SitemapArea>();
	}

	/// <summary>
	/// Represents the full application state including identity, metadata, and navigation sitemap.
	/// Used by <see cref="IAppService.Application"/> property to expose the current application's
	/// state for sitemap rendering, area/node management, and admin console operations.
	/// Derived from the monolith's <c>WebVella.Erp.Web.Models.App</c>.
	/// </summary>
	public class AppState
	{
		/// <summary>
		/// Unique identifier for the application.
		/// </summary>
		public Guid Id { get; set; } = Guid.Empty;

		/// <summary>
		/// Machine-readable name used in URL routing (e.g., "sdk", "crm").
		/// Must be unique across all applications.
		/// </summary>
		public string Name { get; set; } = "";

		/// <summary>
		/// Human-readable display label for the application.
		/// </summary>
		public string Label { get; set; } = "";

		/// <summary>
		/// Descriptive text for the application, shown in admin console.
		/// </summary>
		public string Description { get; set; } = "";

		/// <summary>
		/// CSS icon class (e.g., "fa fa-cog") for UI rendering.
		/// </summary>
		public string IconClass { get; set; } = "";

		/// <summary>
		/// Author or owner name metadata for the application.
		/// </summary>
		public string Author { get; set; } = "";

		/// <summary>
		/// Theme color identifier for UI rendering (e.g., "#2196F3").
		/// </summary>
		public string Color { get; set; } = "";

		/// <summary>
		/// Display ordering weight. Lower values appear first.
		/// </summary>
		public int Weight { get; set; }

		/// <summary>
		/// List of role GUIDs that have access to this application.
		/// Empty list means unrestricted access.
		/// </summary>
		public List<Guid> Access { get; set; } = new List<Guid>();

		/// <summary>
		/// The application's navigation sitemap containing areas, groups, and nodes.
		/// Used by AdminController for sitemap rendering and reordering operations.
		/// </summary>
		public Sitemap Sitemap { get; set; } = new Sitemap();
	}

	/// <summary>
	/// Minimal ERP page data transfer object containing the core properties needed by
	/// the Admin service for page CRUD operations and sitemap-node-to-page mapping.
	/// Derived from the monolith's <c>WebVella.Erp.Web.Models.ErpPage</c>.
	/// </summary>
	public class ErpPage
	{
		/// <summary>
		/// Unique identifier for the page.
		/// </summary>
		public Guid Id { get; set; } = Guid.Empty;

		/// <summary>
		/// Machine-readable page name used in URL routing.
		/// </summary>
		public string Name { get; set; } = "";

		/// <summary>
		/// Human-readable display label for the page.
		/// </summary>
		public string Label { get; set; } = "";

		/// <summary>
		/// Localized label translations for multi-language support.
		/// </summary>
		public List<TranslationResource> LabelTranslations { get; set; } = new List<TranslationResource>();

		/// <summary>
		/// CSS icon class for UI rendering (e.g., "fa fa-file").
		/// </summary>
		public string IconClass { get; set; } = "";

		/// <summary>
		/// Indicates whether this page is a system page (protected from deletion).
		/// </summary>
		public bool System { get; set; }

		/// <summary>
		/// Display ordering weight. Lower values appear first.
		/// </summary>
		public int Weight { get; set; }

		/// <summary>
		/// Classification of the page type (Home, Site, Application, RecordList, etc.).
		/// </summary>
		public PageType Type { get; set; }

		/// <summary>
		/// Optional application identifier that owns this page.
		/// Null for unbound or shared pages.
		/// </summary>
		public Guid? AppId { get; set; }

		/// <summary>
		/// Optional entity identifier this page is associated with.
		/// Required for entity-driven page types (RecordList, RecordCreate, RecordDetails, RecordManage).
		/// </summary>
		public Guid? EntityId { get; set; }

		/// <summary>
		/// Optional sitemap node identifier this page is bound to.
		/// </summary>
		public Guid? NodeId { get; set; }

		/// <summary>
		/// Optional sitemap area identifier this page belongs to.
		/// </summary>
		public Guid? AreaId { get; set; }

		/// <summary>
		/// Indicates whether the page body content is stored as Razor markup on disk.
		/// </summary>
		public bool IsRazorBody { get; set; }

		/// <summary>
		/// Raw Razor body content of the page, starting with "@page" declaration.
		/// Only populated when <see cref="IsRazorBody"/> is true.
		/// </summary>
		public string RazorBody { get; set; } = "";

		/// <summary>
		/// Layout template name for the page (e.g., "default", "full-width").
		/// </summary>
		public string Layout { get; set; } = "";
	}

	#endregion

	#region Service Contract Interfaces

	/// <summary>
	/// Service contract for application and sitemap management operations.
	/// Replaces direct instantiation of <c>WebVella.Erp.Web.Services.AppService</c>
	/// from the monolith with a DI-injectable interface. Implementations may be
	/// local database-backed services or gRPC client proxies to the Core service.
	/// </summary>
	public interface IAppService
	{
		/// <summary>
		/// Gets the current application state including sitemap structure.
		/// Used by AdminController for sitemap rendering and area/node management.
		/// Replaces <c>ErpAppContext.Current</c> singleton access from the monolith.
		/// </summary>
		AppState Application { get; }

		/// <summary>
		/// Retrieves an application by its unique identifier.
		/// Returns null if no application exists with the specified ID.
		/// Derived from <c>AppService.GetApplication(Guid id)</c>.
		/// </summary>
		/// <param name="id">The unique identifier of the application to retrieve.</param>
		/// <returns>The application state, or null if not found.</returns>
		AppState GetApplication(Guid id);

		/// <summary>
		/// Creates a new sitemap area within the specified application.
		/// Validates uniqueness and required fields before persistence.
		/// Derived from <c>AppService.CreateArea</c> (source lines 277-295).
		/// </summary>
		/// <param name="areaId">Unique identifier for the new area.</param>
		/// <param name="appId">The parent application identifier.</param>
		/// <param name="name">Machine-readable area name (unique within app).</param>
		/// <param name="label">Human-readable display label.</param>
		/// <param name="description">Descriptive text for the area.</param>
		/// <param name="iconClass">CSS icon class for UI rendering.</param>
		/// <param name="color">Theme color identifier.</param>
		/// <param name="showGroupNames">Whether to display group names in navigation.</param>
		/// <param name="weight">Display ordering weight.</param>
		/// <param name="access">List of role GUIDs with access to this area.</param>
		void CreateArea(Guid areaId, Guid appId, string name, string label, string description,
			string iconClass, string color, bool showGroupNames, int weight, List<Guid> access);

		/// <summary>
		/// Updates an existing sitemap area within the specified application.
		/// Derived from <c>AppService.UpdateArea</c> (source lines 313-331).
		/// </summary>
		/// <param name="areaId">Identifier of the area to update.</param>
		/// <param name="appId">The parent application identifier.</param>
		/// <param name="name">Updated machine-readable area name.</param>
		/// <param name="label">Updated human-readable display label.</param>
		/// <param name="description">Updated descriptive text.</param>
		/// <param name="iconClass">Updated CSS icon class.</param>
		/// <param name="color">Updated theme color.</param>
		/// <param name="showGroupNames">Whether to display group names.</param>
		/// <param name="weight">Updated display ordering weight.</param>
		/// <param name="access">Updated list of role GUIDs with access.</param>
		void UpdateArea(Guid areaId, Guid appId, string name, string label, string description,
			string iconClass, string color, bool showGroupNames, int weight, List<Guid> access);

		/// <summary>
		/// Deletes a sitemap area and cascades deletion to all child nodes and page unbindings.
		/// Derived from <c>AppService.DeleteArea</c> (source lines 338-378).
		/// </summary>
		/// <param name="areaId">Identifier of the area to delete.</param>
		void DeleteArea(Guid areaId);

		/// <summary>
		/// Creates a new sitemap area node within the specified area.
		/// Nodes represent navigable pages or URLs in the application sitemap.
		/// Derived from <c>AppService.CreateAreaNode</c> (source lines 491-519).
		/// </summary>
		/// <param name="nodeId">Unique identifier for the new node.</param>
		/// <param name="areaId">The parent area identifier.</param>
		/// <param name="name">Machine-readable node name.</param>
		/// <param name="label">Human-readable display label.</param>
		/// <param name="iconClass">CSS icon class for UI rendering.</param>
		/// <param name="url">URL for Url-type nodes; ignored for entity-driven nodes.</param>
		/// <param name="type">The node type classification (EntityList, ApplicationPage, Url).</param>
		/// <param name="entityId">Optional entity identifier for entity-driven nodes.</param>
		/// <param name="weight">Display ordering weight.</param>
		/// <param name="access">List of role GUIDs with access to this node.</param>
		/// <param name="parentId">Optional parent node identifier for hierarchical navigation.</param>
		void CreateAreaNode(Guid nodeId, Guid areaId, string name, string label,
			string iconClass, string url, SitemapNodeType type, Guid? entityId,
			int weight, List<Guid> access, Guid? parentId);

		/// <summary>
		/// Updates an existing sitemap area node.
		/// Derived from <c>AppService.UpdateAreaNode</c> (source lines 536-565).
		/// </summary>
		/// <param name="nodeId">Identifier of the node to update.</param>
		/// <param name="areaId">The parent area identifier.</param>
		/// <param name="name">Updated machine-readable node name.</param>
		/// <param name="label">Updated human-readable display label.</param>
		/// <param name="iconClass">Updated CSS icon class.</param>
		/// <param name="url">Updated URL for Url-type nodes.</param>
		/// <param name="type">Updated node type classification.</param>
		/// <param name="entityId">Updated optional entity identifier.</param>
		/// <param name="weight">Updated display ordering weight.</param>
		/// <param name="access">Updated list of role GUIDs with access.</param>
		/// <param name="parentId">Updated optional parent node identifier.</param>
		void UpdateAreaNode(Guid nodeId, Guid areaId, string name, string label,
			string iconClass, string url, SitemapNodeType type, Guid? entityId,
			int weight, List<Guid> access, Guid? parentId);

		/// <summary>
		/// Deletes a sitemap area node and unbinds associated pages.
		/// Derived from <c>AppService.DeleteAreaNode</c> (source lines 572-581).
		/// </summary>
		/// <param name="nodeId">Identifier of the node to delete.</param>
		void DeleteAreaNode(Guid nodeId);

		/// <summary>
		/// Orders the sitemap areas and their nodes by weight and name.
		/// Returns a new Sitemap instance with sorted areas and nodes.
		/// Derived from <c>AppService.OrderSitemap</c> (source lines 583-589).
		/// </summary>
		/// <param name="sitemap">The sitemap to sort.</param>
		/// <returns>The same sitemap instance with sorted areas and nodes.</returns>
		Sitemap OrderSitemap(Sitemap sitemap);
	}

	/// <summary>
	/// Service contract for page CRUD operations and page-to-node mapping.
	/// Replaces direct instantiation of <c>WebVella.Erp.Web.Services.PageService</c>
	/// from the monolith with a DI-injectable interface. Implementations may be
	/// local database-backed services or gRPC client proxies to the Core service.
	/// </summary>
	public interface IPageService
	{
		/// <summary>
		/// Retrieves a page by its unique identifier.
		/// Returns null if no page exists with the specified ID.
		/// Derived from <c>PageService.GetPage(Guid pageId)</c> (source lines 64-69).
		/// </summary>
		/// <param name="pageId">The unique identifier of the page to retrieve.</param>
		/// <returns>The page DTO, or null if not found.</returns>
		ErpPage GetPage(Guid pageId);

		/// <summary>
		/// Updates an existing page with the provided data.
		/// Validates required fields and page type constraints before persistence.
		/// Derived from <c>PageService.UpdatePage</c> (source lines 249-305).
		/// </summary>
		/// <param name="page">The page DTO containing updated field values.</param>
		void UpdatePage(ErpPage page);

		/// <summary>
		/// Retrieves all pages in the system, ordered by weight then label.
		/// Results may be served from cache when available.
		/// Derived from <c>PageService.GetAll()</c> (source lines 42-57).
		/// </summary>
		/// <returns>A list of all page DTOs in the system.</returns>
		List<ErpPage> GetAllPages();

		/// <summary>
		/// Retrieves all pages that are controlled by (belong to) the specified application.
		/// Pages are filtered by AppId and ordered by weight then label.
		/// Derived from <c>PageService.GetAppControlledPages(Guid appId)</c> (source lines 96-100).
		/// </summary>
		/// <param name="appId">The application identifier to filter pages by.</param>
		/// <returns>A list of page DTOs belonging to the specified application.</returns>
		List<ErpPage> GetAppControlledPages(Guid appId);

		/// <summary>
		/// Builds a dictionary mapping sitemap node GUIDs to the page GUIDs bound to each node.
		/// Used by the admin console to display which pages are attached to each sitemap node.
		/// Derived from <c>PageUtils.GetNodePageDictionary(Guid? appId)</c> (source lines 218-253).
		/// </summary>
		/// <param name="appId">
		/// Optional application identifier. When provided, only pages belonging to
		/// the specified application are included. When null, all pages are considered.
		/// </param>
		/// <returns>A dictionary where keys are node GUIDs and values are lists of page GUIDs bound to each node.</returns>
		Dictionary<Guid, List<Guid>> GetNodePageDictionary(Guid? appId);
	}

	/// <summary>
	/// Service contract for data source listing and retrieval operations.
	/// Replaces direct instantiation of <c>WebVella.Erp.Api.DataSourceManager</c>
	/// from the monolith with a DI-injectable interface.
	/// </summary>
	public interface IDataSourceManager
	{
		/// <summary>
		/// Retrieves all registered data sources (both database-backed and code-based).
		/// Results include cached instances and are deduplicated by data source ID.
		/// Derived from <c>DataSourceManager.GetAll()</c> (source lines 87-107).
		/// </summary>
		/// <returns>A list of all data source definitions.</returns>
		List<DataSourceBase> GetAll();
	}

	/// <summary>
	/// Service contract for entity metadata retrieval operations.
	/// Replaces direct instantiation of <c>WebVella.Erp.Api.EntityManager</c>
	/// from the monolith with a DI-injectable interface.
	/// </summary>
	public interface IEntityManager
	{
		/// <summary>
		/// Reads all entity definitions from the entity metadata store.
		/// Returns an <see cref="EntityListResponse"/> containing the list of entities
		/// accessible via the <c>.Object</c> property (List&lt;Entity&gt;).
		/// Derived from <c>EntityManager.ReadEntities()</c>.
		/// </summary>
		/// <returns>An <see cref="EntityListResponse"/> containing all entity definitions.</returns>
		EntityListResponse ReadEntities();
	}

	/// <summary>
	/// Service contract for record CRUD operations.
	/// Replaces direct instantiation of <c>WebVella.Erp.Api.RecordManager</c>
	/// from the monolith with a DI-injectable interface.
	/// Currently a marker interface — specific methods will be added as
	/// AdminController usage patterns are expanded.
	/// </summary>
	public interface IRecordManager
	{
	}

	/// <summary>
	/// Service contract for user and role security operations.
	/// Replaces direct instantiation of <c>WebVella.Erp.Api.SecurityManager</c>
	/// from the monolith with a DI-injectable interface.
	/// Currently a marker interface — specific methods will be added as
	/// AdminController usage patterns are expanded.
	/// </summary>
	public interface ISecurityManager
	{
	}

	/// <summary>
	/// Service contract for entity relation management operations.
	/// Replaces direct instantiation of <c>WebVella.Erp.Api.EntityRelationManager</c>
	/// from the monolith with a DI-injectable interface.
	/// Currently a marker interface — specific methods will be added as
	/// AdminController usage patterns are expanded.
	/// </summary>
	public interface IEntityRelationManager
	{
	}

	#endregion
}
