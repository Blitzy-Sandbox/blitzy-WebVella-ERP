using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Response model returned by CodeGenService.EvaluateMetaChanges() containing
	/// generated code and a list of detected metadata changes. Used by the Admin
	/// service and consumed by multiple services for entity-schema comparison.
	/// </summary>
	public class MetaChangeResponseModel
	{
		/// <summary>
		/// Generated C# code reflecting current entity metadata state.
		/// </summary>
		[JsonProperty(PropertyName = "code")]
		public string Code { get; set; } = "";

		/// <summary>
		/// Collection of individual metadata changes detected between the
		/// persisted schema and the runtime entity definitions.
		/// </summary>
		[JsonProperty(PropertyName = "changes")]
		public List<MetaChangeModel> Changes { get; set; } = new List<MetaChangeModel>();

		/// <summary>
		/// Indicates whether the metadata evaluation completed successfully.
		/// Defaults to <c>true</c>.
		/// </summary>
		[JsonProperty(PropertyName = "success")]
		public bool Success { get; set; } = true;

		/// <summary>
		/// Optional human-readable message providing additional context about
		/// the evaluation result (e.g. error details when Success is false).
		/// </summary>
		[JsonProperty(PropertyName = "message")]
		public string Message { get; set; } = "";
	}

	/// <summary>
	/// Represents a single metadata change detected during entity schema comparison.
	/// Each instance describes what element changed, the change type, the element
	/// name, and a list of individual change descriptions.
	/// </summary>
	public class MetaChangeModel
	{
		/// <summary>
		/// The kind of schema element that changed (e.g. "entity", "field", "relation").
		/// </summary>
		[JsonProperty(PropertyName = "element")]
		public string Element { get; set; }

		/// <summary>
		/// The type of change detected (e.g. "added", "removed", "modified").
		/// </summary>
		[JsonProperty(PropertyName = "type")]
		public string Type { get; set; }

		/// <summary>
		/// The name or identifier of the changed schema element.
		/// </summary>
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		/// <summary>
		/// Detailed list of individual change descriptions for this element.
		/// </summary>
		[JsonProperty(PropertyName = "change_list")]
		public List<string> ChangeList { get; set; } = new List<string>();
	}

	/// <summary>
	/// Lightweight response indicating whether a metadata update is available
	/// and providing the generated code and change descriptions if so. Used as
	/// a quick check before performing a full metadata evaluation.
	/// </summary>
	public class UpdateCheckResponse
	{
		/// <summary>
		/// Indicates whether any metadata changes were detected. Defaults to <c>false</c>.
		/// </summary>
		[JsonProperty(PropertyName = "hasUpdate")]
		public bool HasUpdate { get; set; } = false;

		/// <summary>
		/// Generated C# code reflecting the updated metadata state.
		/// Empty when <see cref="HasUpdate"/> is <c>false</c>.
		/// </summary>
		[JsonProperty(PropertyName = "code")]
		public string Code { get; set; } = "";

		/// <summary>
		/// List of human-readable change descriptions. Empty when
		/// <see cref="HasUpdate"/> is <c>false</c>.
		/// </summary>
		[JsonProperty(PropertyName = "change_list")]
		public List<string> ChangeList { get; set; } = new List<string>();
	}
}
