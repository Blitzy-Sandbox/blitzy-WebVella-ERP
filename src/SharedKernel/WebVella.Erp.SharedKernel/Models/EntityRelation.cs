using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Defines the type of relationship between two entities.
	/// Used by the EQL engine to determine join strategy and by
	/// EntityRelationManager for integrity validation.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.EntityRelationType</c>.
	/// </summary>
	[Serializable]
	public enum EntityRelationType
	{
		/// <summary>
		/// 1. Origin field should be an unique, required Guid field
		/// 2. Target field should be an unique, required Guid field
		/// 3. Target field record values should match one origin record field values
		/// </summary>
		[SelectOption(Label = "(1:1) One to One")]
		OneToOne = 1,

		/// <summary>
		/// 1. Origin field should be an unique,required Guid field
		/// 2. Target field should be a Guid field 
		/// 3. Target field record values should match atleast one origin record field values or null if field value is not required
		/// </summary>
		[SelectOption(Label = "(1:N) One to Many")]
		OneToMany = 2,

		/// <summary>
		/// 1. Origin field should be an unique, required Guid field
		/// 2. Target field should be an unique, required Guid field
		/// </summary>
		[SelectOption(Label = "(N:N) Many to Many")]
		ManyToMany = 3
	}

	/// <summary>
	/// Full definition of a relation between two entities, specifying the origin
	/// and target entity/field pairs and the cardinality. Used by the EQL engine
	/// for $relation and $$relation traversal, by EntityRelationManager for CRUD,
	/// and by all services for cross-entity data resolution.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.EntityRelation</c>
	/// to maintain backward compatibility with all services, REST API v3 responses,
	/// and cross-service relation resolution (AAP 0.7.1).
	/// </summary>
	[Serializable]
	public class EntityRelation
	{
		[JsonProperty(PropertyName = "id")]
		public Guid Id { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; }

		[JsonProperty(PropertyName = "description")]
		public string Description { get; set; }

		[JsonProperty(PropertyName = "system")]
		public bool System { get; set; }

		[JsonProperty(PropertyName = "relationType")]
		public EntityRelationType RelationType { get; set; }

		[JsonProperty(PropertyName = "originEntityId")]
		public Guid OriginEntityId { get; set; }

		[JsonProperty(PropertyName = "originFieldId")]
		public Guid OriginFieldId { get; set; }

		[JsonProperty(PropertyName = "targetEntityId")]
		public Guid TargetEntityId { get; set; }

		[JsonProperty(PropertyName = "targetFieldId")]
		public Guid TargetFieldId { get; set; }

		[JsonProperty(PropertyName = "originEntityName")]
		public string OriginEntityName { get; set; }

		[JsonProperty(PropertyName = "originFieldName")]
		public string OriginFieldName { get; set; }

		[JsonProperty(PropertyName = "targetEntityName")]
		public string TargetEntityName { get; set; }

		[JsonProperty(PropertyName = "targetFieldName")]
		public string TargetFieldName { get; set; }

		public override string ToString()
		{
			return $"{Name} org:{OriginEntityName}.{OriginFieldName}  tar:{TargetEntityName}.{TargetFieldName}";
		}
	}

	/// <summary>
	/// Relation options item for UI selection in entity configuration screens.
	/// </summary>
	[Serializable]
	public class EntityRelationOptionsItem
	{
		[JsonProperty(PropertyName = "type")]
		public static string ItemType { get { return "relationOptions"; } }

		[JsonProperty(PropertyName = "relationId")]
		public Guid? RelationId { get; set; }

		[JsonProperty(PropertyName = "relationName")]
		public string RelationName { get; set; }

		[JsonProperty(PropertyName = "direction")]
		public string Direction { get; set; }
	}

	/// <summary>
	/// Response envelope for a single entity relation.
	/// </summary>
	[Serializable]
	public class EntityRelationResponse : BaseResponseModel
	{
		[JsonProperty(PropertyName = "object")]
		public EntityRelation Object { get; set; }
	}

	/// <summary>
	/// Response envelope for a list of entity relations.
	/// </summary>
	[Serializable]
	public class EntityRelationListResponse : BaseResponseModel
	{
		[JsonProperty(PropertyName = "object")]
		public List<EntityRelation> Object { get; set; }
	}

	/// <summary>
	/// Input model for updating relation records (attach/detach target records
	/// from an origin record). Used by the REST API for relation management.
	/// </summary>
	[Serializable]
	public class InputEntityRelationRecordUpdateModel
	{
		[JsonProperty(PropertyName = "relationName")]
		public string RelationName { get; set; }

		[JsonProperty(PropertyName = "originFieldRecordId")]
		public Guid OriginFieldRecordId { get; set; }

		[JsonProperty(PropertyName = "attachTargetFieldRecordIds")]
		public List<Guid> AttachTargetFieldRecordIds { get; set; }

		[JsonProperty(PropertyName = "detachTargetFieldRecordIds")]
		public List<Guid> DetachTargetFieldRecordIds { get; set; }
	}

	/// <summary>
	/// Input model for reverse-direction relation record updates (attach/detach
	/// origin records from a target record).
	/// </summary>
	[Serializable]
	public class InputEntityRelationRecordReverseUpdateModel
	{
		[JsonProperty(PropertyName = "relationName")]
		public string RelationName { get; set; }

		[JsonProperty(PropertyName = "targetFieldRecordId")]
		public Guid TargetFieldRecordId { get; set; }

		[JsonProperty(PropertyName = "attachOriginFieldRecordIds")]
		public List<Guid> AttachOriginFieldRecordIds { get; set; }

		[JsonProperty(PropertyName = "detachOriginFieldRecordIds")]
		public List<Guid> DetachOriginFieldRecordIds { get; set; }
	}

	/// <summary>
	/// Relation options configuration model used in entity field metadata.
	/// </summary>
	[Serializable]
	public class EntityRelationOptions
	{
		[JsonProperty(PropertyName = "relationId")]
		public Guid? RelationId { get; set; }

		[JsonProperty(PropertyName = "relationName")]
		public string RelationName { get; set; }

		[JsonProperty(PropertyName = "direction")]
		public string Direction { get; set; }
	}
}
