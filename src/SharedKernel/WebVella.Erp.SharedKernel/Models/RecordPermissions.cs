using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// GUID-based permission model that controls which roles can perform CRUD
	/// operations on records of a given entity. Each permission list contains
	/// role GUIDs that are authorized for the corresponding operation.
	///
	/// Used by <see cref="Entity.RecordPermissions"/> and enforced by the
	/// RecordManager during record CRUD operations. Permission checks compare
	/// the current user's role GUIDs against these lists.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.RecordPermissions</c>
	/// (originally defined in Entity.cs) to maintain backward compatibility with
	/// all entity definitions, REST API v3 responses, and cross-service
	/// permission propagation via JWT claims.
	/// </summary>
	[Serializable]
	public class RecordPermissions
	{
		/// <summary>
		/// List of role GUIDs authorized to read records of the entity.
		/// An empty list means no role-based read restriction (but other checks may apply).
		/// </summary>
		[JsonProperty(PropertyName = "canRead")]
		public List<Guid> CanRead { get; set; } = new List<Guid>();

		/// <summary>
		/// List of role GUIDs authorized to create records of the entity.
		/// </summary>
		[JsonProperty(PropertyName = "canCreate")]
		public List<Guid> CanCreate { get; set; } = new List<Guid>();

		/// <summary>
		/// List of role GUIDs authorized to update records of the entity.
		/// </summary>
		[JsonProperty(PropertyName = "canUpdate")]
		public List<Guid> CanUpdate { get; set; } = new List<Guid>();

		/// <summary>
		/// List of role GUIDs authorized to delete records of the entity.
		/// </summary>
		[JsonProperty(PropertyName = "canDelete")]
		public List<Guid> CanDelete { get; set; } = new List<Guid>();
	}
}
