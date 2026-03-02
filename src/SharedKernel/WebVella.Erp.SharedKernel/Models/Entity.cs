using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Input model for creating or updating an entity definition.
	/// Used by the EntityManager API when a client submits an entity
	/// creation or modification request via REST/gRPC.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.InputEntity</c>
	/// to maintain backward compatibility with the REST API v3 contract.
	/// </summary>
	public class InputEntity
	{
		[JsonProperty(PropertyName = "id")]
		public Guid? Id { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; } = "";

		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; } = "";

		[JsonProperty(PropertyName = "labelPlural")]
		public string LabelPlural { get; set; } = "";

		[JsonProperty(PropertyName = "system")]
		public bool? System { get; set; } = false;

		[JsonProperty(PropertyName = "iconName")]
		public string IconName { get; set; } = "";

		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; } = "";

		[JsonProperty(PropertyName = "recordPermissions")]
		public RecordPermissions RecordPermissions { get; set; }

		[JsonProperty(PropertyName = "record_screen_id_field")]
		public Guid? RecordScreenIdField { get; set; }
	}

	/// <summary>
	/// Full entity definition model containing the complete metadata for a
	/// dynamic ERP entity, including its fields collection, record permissions,
	/// and identity hash. This is the core data type used by EntityManager,
	/// EQL engine, and all services for entity schema operations.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.Entity</c>
	/// to maintain backward compatibility with all services, REST API v3
	/// responses, and cross-service entity management.
	/// </summary>
	[Serializable]
	public class Entity
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; } = "";

		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; } = "";

		[JsonProperty(PropertyName = "labelPlural")]
		public string LabelPlural { get; set; } = "";

		[JsonProperty(PropertyName = "system")]
		public bool System { get; set; } = false;

		[JsonProperty(PropertyName = "iconName")]
		public string IconName { get; set; } = "";

		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; } = "";

		/// <summary>
		/// Role-based permissions controlling which roles can read, create,
		/// update, or delete records of this entity.
		/// </summary>
		[JsonProperty(PropertyName = "recordPermissions")]
		public RecordPermissions RecordPermissions { get; set; }

		/// <summary>
		/// Polymorphic collection of field definitions for this entity.
		/// Each field is a subclass of <see cref="Field"/> (e.g., TextField,
		/// NumberField, GuidField, etc.) deserialized via JsonCreationConverter.
		/// </summary>
		[JsonProperty(PropertyName = "fields")]
		public List<Field> Fields { get; set; }

		/// <summary>
		/// Optional GUID of the field whose value is used as the "screen ID"
		/// (human-readable identifier) for records of this entity.
		/// If null, the record's Id field is used.
		/// </summary>
		[JsonProperty(PropertyName = "record_screen_id_field")]
		public Guid? RecordScreenIdField { get; set; }

		/// <summary>
		/// Hash of the entity's structure, used for change detection and
		/// cache invalidation. Computed by EntityManager on entity save.
		/// </summary>
		[JsonProperty(PropertyName = "hash")]
		public string Hash { get; internal set; }

		public override string ToString()
		{
			return Name;
		}
	}

	/// <summary>
	/// Response envelope for a single entity definition.
	/// Follows the standard BaseResponseModel envelope pattern
	/// (success, errors, timestamp, message, object).
	/// </summary>
	public class EntityResponse : BaseResponseModel
	{
		[JsonProperty(PropertyName = "object")]
		public Entity Object { get; set; }
	}

	/// <summary>
	/// Response envelope for a list of entity definitions.
	/// </summary>
	public class EntityListResponse : BaseResponseModel
	{
		[JsonProperty(PropertyName = "object")]
		public List<Entity> Object { get; set; }
	}

	/// <summary>
	/// Response envelope for entity library items used by the admin/SDK service.
	/// </summary>
	public class EntityLibraryItemsResponse : BaseResponseModel
	{
		[JsonProperty(PropertyName = "object")]
		public List<object> Object { get; set; }
	}
}
