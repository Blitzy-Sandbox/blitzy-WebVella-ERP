using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Represents a localized translation resource entry.
	/// Used by SitemapArea, SitemapGroup, and SitemapNode for label/description translations.
	/// Source: WebVella.Erp.Web/Models/TranslationResource.cs
	/// </summary>
	public class TranslationResource
	{
		[JsonProperty("locale")]
		public string Locale { get; set; } = "";

		[JsonProperty("key")]
		public string Key { get; set; } = "";

		[JsonProperty("value")]
		public string Value { get; set; } = "";
	}

	/// <summary>
	/// Defines the type of a sitemap node for navigation rendering.
	/// Source: WebVella.Erp.Web/Models/SitemapNodeType.cs
	/// </summary>
	public enum SitemapNodeType
	{
		[SelectOption(Label = "entity list")]
		EntityList = 1,
		[SelectOption(Label = "application page")]
		ApplicationPage = 2,
		[SelectOption(Label = "url")]
		Url = 3,
	}

	/// <summary>
	/// Defines the type classification of a page within the application.
	/// Source: WebVella.Erp.Web/Models/PageType.cs
	/// </summary>
	public enum PageType
	{
		[SelectOption(Label = "home")]
		Home = 0,
		[SelectOption(Label = "site")]
		Site = 1,
		[SelectOption(Label = "application")]
		Application = 2,
		[SelectOption(Label = "record list")]
		RecordList = 3,
		[SelectOption(Label = "record create")]
		RecordCreate = 4,
		[SelectOption(Label = "record details")]
		RecordDetails = 5,
		[SelectOption(Label = "record manage")]
		RecordManage = 6
	}

	/// <summary>
	/// Represents a navigation group within a sitemap area.
	/// Groups organize sitemap nodes into logical sections.
	/// Source: WebVella.Erp.Web/Models/SitemapGroup.cs
	/// </summary>
	public class SitemapGroup
	{
		[JsonProperty("id")]
		public Guid Id { get; set; } = Guid.Empty;

		[JsonProperty("weight")]
		public int Weight { get; set; } = 1;

		[JsonProperty("label")]
		public string Label { get; set; } = "";

		[JsonProperty("name")]
		public string Name { get; set; } = ""; //identifier for the sitemap nodes

		[JsonProperty("label_translations")]
		public List<TranslationResource> LabelTranslations { get; set; } = new List<TranslationResource>(); //To be easily discoverd when stored in the db one idea is to generate keys based on "sitemapId-areaName-groupName-title"

		[JsonProperty("render_roles")]
		public List<Guid> RenderRoles { get; set; } = new List<Guid>(); //show in menu for the added roles, or for all if no roles are selected
	}

	/// <summary>
	/// Represents a navigable node (page or URL entry) within a sitemap area.
	/// Nodes can reference entities, application pages, or external URLs.
	/// Source: WebVella.Erp.Web/Models/SitemapNode.cs
	/// </summary>
	public class SitemapNode
	{
		[JsonProperty("id")]
		public Guid Id { get; set; } = Guid.Empty;

		[JsonProperty("parent_id")]
		public Guid? ParentId { get; set; } = null;

		[JsonProperty("weight")]
		public int Weight { get; set; } = 1;

		[JsonProperty("group_name")]
		public string GroupName { get; set; } = ""; // If empty this means the items has no group

		[JsonProperty("label")]
		public string Label { get; set; } = "";

		[JsonProperty("name")]
		public string Name { get; set; } = "";

		[JsonProperty("icon_class")]
		public string IconClass { get; set; } = "";

		[JsonProperty("url")]
		public string Url { get; set; } = ""; //can have hardcoded URL

		[JsonProperty("label_translations")]
		public List<TranslationResource> LabelTranslations { get; set; } = new List<TranslationResource>(); //To be easily discoverd when stored in the db one idea is to generate keys based on "sitemapId-areaName-title"

		[JsonProperty("access")]
		public List<Guid> Access { get; set; } = new List<Guid>(); //show in menu for the added roles, or for all if no roles are selected

		[JsonProperty("type")]
		public SitemapNodeType Type { get; set; } = SitemapNodeType.EntityList;

		[JsonProperty("entity_id")]
		public Guid? EntityId { get; set; } = null;

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
	/// Represents a top-level sitemap area that contains groups and navigation nodes.
	/// Areas are the primary navigation containers for application sections.
	/// Source: WebVella.Erp.Web/Models/SitemapArea.cs
	/// </summary>
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

		[JsonProperty("show_group_names")]
		public bool ShowGroupNames { get; set; } = false;

		[JsonProperty("color")]
		public string Color { get; set; } = "";

		[JsonProperty("label_translations")]
		public List<TranslationResource> LabelTranslations { get; set; } = new List<TranslationResource>(); //To be easily discovered when stored in the db one idea is to generate keys based on "sitemapId-areaName-title"

		[JsonProperty("description_translations")]
		public List<TranslationResource> DescriptionTranslations { get; set; } = new List<TranslationResource>(); //To be easily discovered when stored in the db one idea is to generate keys based on "sitemapId-areaName-title"

		[JsonProperty("groups")]
		public List<SitemapGroup> Groups { get; set; } = new List<SitemapGroup>(); //can have hardcoded URL

		[JsonProperty("nodes")]
		public List<SitemapNode> Nodes { get; set; } = new List<SitemapNode>(); //can have hardcoded URL

		[JsonProperty("access")]
		public List<Guid> Access { get; set; } = new List<Guid>(); //show in menu for the added roles, or for all if no roles are selected
	}
}
