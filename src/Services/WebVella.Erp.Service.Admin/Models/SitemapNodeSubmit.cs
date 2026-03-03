using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Service.Admin.Models
{
	/// <summary>
	/// Admin-specific DTO that extends SitemapNode with a Pages property,
	/// used for creating/updating sitemap nodes with page attachments.
	/// Migrated from WebVella.Erp.Plugins.SDK.Model.SitemapNodeSubmit.
	/// Inherits all 14 sitemap node properties from SitemapNode (Id, ParentId,
	/// Weight, GroupName, Label, Name, IconClass, Url, LabelTranslations,
	/// Access, Type, EntityId, EntityListPages, EntityCreatePages,
	/// EntityDetailsPages, EntityManagePages).
	/// </summary>
	public class SitemapNodeSubmit : SitemapNode
	{
		/// <summary>
		/// List of page GUIDs attached to this sitemap node submission.
		/// Used to associate application pages with the node during create/update operations.
		/// </summary>
		[JsonProperty("pages")]
		public List<Guid> Pages { get; set; } = new List<Guid>();
	}
}
