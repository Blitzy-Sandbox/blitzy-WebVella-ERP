// =============================================================================
// EntityRelation.cs — Relation Metadata Models for Entity Management Service
// =============================================================================
// Migrated from: WebVella.Erp/Api/Models/EntityRelation.cs
//
// Contains the full set of relation-related models:
//   - EntityRelationType enum (OneToOne, OneToMany, ManyToMany)
//   - EntityRelation DTO (relation metadata with origin/target entity/field info)
//   - EntityRelationOptionsItem (relation option metadata for UI selection)
//   - EntityRelationResponse / EntityRelationListResponse (API response wrappers)
//   - InputEntityRelationRecordUpdateModel (forward attach/detach operations)
//   - InputEntityRelationRecordReverseUpdateModel (reverse attach/detach operations)
//   - EntityRelationOptions (simplified relation option reference)
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json.Serialization [JsonPropertyName("...")]
//        (AOT-safe for .NET 9 Native AOT Lambda deployment)
//
// Internal Dependencies:
//   - BaseResponseModel (from BaseModels.cs) — response envelope base class
//   - SelectOptionAttribute (from Definitions.cs) — enum display label metadata
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Defines the cardinality type of a relation between two entities.
    /// Each relation type imposes specific constraints on the origin and target
    /// fields that participate in the relation.
    /// Migrated from: WebVella.Erp.Api.Models.EntityRelationType
    /// </summary>
    [Serializable]
    public enum EntityRelationType
    {
        /// <summary>
        /// One-to-One relation type.
        /// 1. Origin field should be a unique, required Guid field.
        /// 2. Target field should be a unique, required Guid field.
        /// 3. Target field record values should match one origin record field value.
        /// </summary>
        [SelectOption(Label = "(1:1) One to One")]
        OneToOne = 1,

        /// <summary>
        /// One-to-Many relation type.
        /// 1. Origin field should be a unique, required Guid field.
        /// 2. Target field should be a Guid field.
        /// 3. Target field record values should match at least one origin record
        ///    field value, or null if the field value is not required.
        /// </summary>
        [SelectOption(Label = "(1:N) One to Many")]
        OneToMany = 2,

        /// <summary>
        /// Many-to-Many relation type.
        /// 1. Origin field should be a unique, required Guid field.
        /// 2. Target field should be a unique, required Guid field.
        /// The runtime creates a junction/join table to link records from
        /// both entities via their respective fields.
        /// </summary>
        [SelectOption(Label = "(N:N) Many to Many")]
        ManyToMany = 3
    }

    /// <summary>
    /// Represents a relation definition between two entities in the system.
    /// Captures the full metadata including the participating entities and fields
    /// on both origin and target sides, the relation cardinality type, and
    /// whether the relation is a system-defined (immutable) relation.
    /// Migrated from: WebVella.Erp.Api.Models.EntityRelation
    /// </summary>
    [Serializable]
    public class EntityRelation
    {
        /// <summary>
        /// The unique identifier for this entity relation.
        /// </summary>
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// The programmatic name of the relation (e.g., "user_role").
        /// Must be unique across all entity relations in the system.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The human-readable display label for the relation.
        /// Rendered in UI components such as relation pickers and admin panels.
        /// </summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Optional descriptive text explaining the purpose of the relation.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether this is a system-defined relation.
        /// System relations are immutable and cannot be deleted by users.
        /// </summary>
        [JsonPropertyName("system")]
        public bool System { get; set; }

        /// <summary>
        /// The cardinality type of the relation (OneToOne, OneToMany, or ManyToMany).
        /// Determines how records on the origin side relate to records on the target side.
        /// </summary>
        [JsonPropertyName("relationType")]
        public EntityRelationType RelationType { get; set; }

        /// <summary>
        /// The unique identifier of the origin (parent/source) entity.
        /// </summary>
        [JsonPropertyName("originEntityId")]
        public Guid OriginEntityId { get; set; }

        /// <summary>
        /// The unique identifier of the field on the origin entity that
        /// participates in the relation (typically a Guid-type field).
        /// </summary>
        [JsonPropertyName("originFieldId")]
        public Guid OriginFieldId { get; set; }

        /// <summary>
        /// The unique identifier of the target (child/destination) entity.
        /// </summary>
        [JsonPropertyName("targetEntityId")]
        public Guid TargetEntityId { get; set; }

        /// <summary>
        /// The unique identifier of the field on the target entity that
        /// participates in the relation (typically a Guid-type field).
        /// </summary>
        [JsonPropertyName("targetFieldId")]
        public Guid TargetFieldId { get; set; }

        /// <summary>
        /// The programmatic name of the origin entity. Populated at runtime
        /// for convenience, enabling relation display without additional entity lookups.
        /// </summary>
        [JsonPropertyName("originEntityName")]
        public string OriginEntityName { get; set; } = string.Empty;

        /// <summary>
        /// The programmatic name of the origin field. Populated at runtime
        /// for convenience alongside <see cref="OriginEntityName"/>.
        /// </summary>
        [JsonPropertyName("originFieldName")]
        public string OriginFieldName { get; set; } = string.Empty;

        /// <summary>
        /// The programmatic name of the target entity. Populated at runtime
        /// for convenience, enabling relation display without additional entity lookups.
        /// </summary>
        [JsonPropertyName("targetEntityName")]
        public string TargetEntityName { get; set; } = string.Empty;

        /// <summary>
        /// The programmatic name of the target field. Populated at runtime
        /// for convenience alongside <see cref="TargetEntityName"/>.
        /// </summary>
        [JsonPropertyName("targetFieldName")]
        public string TargetFieldName { get; set; } = string.Empty;

        /// <summary>
        /// Returns a human-readable string representation of this relation,
        /// showing the relation name and origin/target entity.field pairs.
        /// </summary>
        /// <returns>Formatted string: "{Name} org:{OriginEntityName}.{OriginFieldName}  tar:{TargetEntityName}.{TargetFieldName}"</returns>
        public override string ToString()
        {
            return $"{Name} org:{OriginEntityName}.{OriginFieldName}  tar:{TargetEntityName}.{TargetFieldName}";
        }
    }

    /// <summary>
    /// Represents a relation option item used in UI selection components.
    /// Contains the relation reference (ID and name) plus a direction indicator
    /// for distinguishing origin-to-target vs target-to-origin navigation.
    /// The <see cref="ItemType"/> static property returns a fixed discriminator
    /// value "relationOptions" for polymorphic JSON deserialization scenarios.
    /// Migrated from: WebVella.Erp.Api.Models.EntityRelationOptionsItem
    /// </summary>
    [Serializable]
    public class EntityRelationOptionsItem
    {
        /// <summary>
        /// Type discriminator for polymorphic item identification.
        /// Always returns "relationOptions" to distinguish this item type
        /// in mixed collections of option items.
        /// </summary>
        [JsonPropertyName("type")]
        public static string ItemType { get { return "relationOptions"; } }

        /// <summary>
        /// The unique identifier of the referenced relation. Nullable to support
        /// placeholder/default option items with no specific relation selected.
        /// </summary>
        [JsonPropertyName("relationId")]
        public Guid? RelationId { get; set; }

        /// <summary>
        /// The programmatic name of the referenced relation.
        /// </summary>
        [JsonPropertyName("relationName")]
        public string RelationName { get; set; } = string.Empty;

        /// <summary>
        /// The direction of the relation from the context entity's perspective.
        /// Typically "origin-target" or "target-origin" to indicate navigation direction.
        /// </summary>
        [JsonPropertyName("direction")]
        public string Direction { get; set; } = string.Empty;
    }

    /// <summary>
    /// API response wrapper for a single <see cref="EntityRelation"/> object.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/>
    /// (Timestamp, Success, Message, Errors, AccessWarnings) and adds the
    /// typed Object property for the relation payload.
    /// Migrated from: WebVella.Erp.Api.Models.EntityRelationResponse
    /// </summary>
    [Serializable]
    public class EntityRelationResponse : BaseResponseModel
    {
        /// <summary>
        /// The entity relation object returned by the API operation.
        /// May be null if the operation failed or the requested relation was not found.
        /// </summary>
        [JsonPropertyName("object")]
        public EntityRelation? Object { get; set; }
    }

    /// <summary>
    /// API response wrapper for a list of <see cref="EntityRelation"/> objects.
    /// Inherits the standard response envelope from <see cref="BaseResponseModel"/>
    /// (Timestamp, Success, Message, Errors, AccessWarnings) and adds the
    /// typed Object property for the relation list payload.
    /// Migrated from: WebVella.Erp.Api.Models.EntityRelationListResponse
    /// </summary>
    [Serializable]
    public class EntityRelationListResponse : BaseResponseModel
    {
        /// <summary>
        /// The list of entity relations returned by the API operation.
        /// Initialized to an empty list to prevent null reference exceptions.
        /// </summary>
        [JsonPropertyName("object")]
        public List<EntityRelation> Object { get; set; } = new List<EntityRelation>();
    }

    /// <summary>
    /// Input model for forward relation record update operations.
    /// Used to attach or detach target-side records to/from a specific
    /// origin-side record via a named relation.
    /// Migrated from: WebVella.Erp.Api.Models.InputEntityRelationRecordUpdateModel
    /// </summary>
    [Serializable]
    public class InputEntityRelationRecordUpdateModel
    {
        /// <summary>
        /// The programmatic name of the relation through which
        /// the attach/detach operations will be performed.
        /// </summary>
        [JsonPropertyName("relationName")]
        public string RelationName { get; set; } = string.Empty;

        /// <summary>
        /// The record ID on the origin side of the relation.
        /// This is the "parent" record that target records will be attached to or detached from.
        /// </summary>
        [JsonPropertyName("originFieldRecordId")]
        public Guid OriginFieldRecordId { get; set; }

        /// <summary>
        /// List of target-side record IDs to attach (link) to the origin record.
        /// Initialized to an empty list to prevent null reference exceptions
        /// when no records need to be attached.
        /// </summary>
        [JsonPropertyName("attachTargetFieldRecordIds")]
        public List<Guid> AttachTargetFieldRecordIds { get; set; } = new List<Guid>();

        /// <summary>
        /// List of target-side record IDs to detach (unlink) from the origin record.
        /// Initialized to an empty list to prevent null reference exceptions
        /// when no records need to be detached.
        /// </summary>
        [JsonPropertyName("detachTargetFieldRecordIds")]
        public List<Guid> DetachTargetFieldRecordIds { get; set; } = new List<Guid>();
    }

    /// <summary>
    /// Input model for reverse relation record update operations.
    /// Used to attach or detach origin-side records to/from a specific
    /// target-side record via a named relation. This is the mirror of
    /// <see cref="InputEntityRelationRecordUpdateModel"/> for reverse navigation.
    /// Migrated from: WebVella.Erp.Api.Models.InputEntityRelationRecordReverseUpdateModel
    /// </summary>
    [Serializable]
    public class InputEntityRelationRecordReverseUpdateModel
    {
        /// <summary>
        /// The programmatic name of the relation through which
        /// the reverse attach/detach operations will be performed.
        /// </summary>
        [JsonPropertyName("relationName")]
        public string RelationName { get; set; } = string.Empty;

        /// <summary>
        /// The record ID on the target side of the relation.
        /// This is the "child" record that origin records will be attached to or detached from.
        /// </summary>
        [JsonPropertyName("targetFieldRecordId")]
        public Guid TargetFieldRecordId { get; set; }

        /// <summary>
        /// List of origin-side record IDs to attach (link) to the target record.
        /// Initialized to an empty list to prevent null reference exceptions
        /// when no records need to be attached.
        /// </summary>
        [JsonPropertyName("attachOriginFieldRecordIds")]
        public List<Guid> AttachOriginFieldRecordIds { get; set; } = new List<Guid>();

        /// <summary>
        /// List of origin-side record IDs to detach (unlink) from the target record.
        /// Initialized to an empty list to prevent null reference exceptions
        /// when no records need to be detached.
        /// </summary>
        [JsonPropertyName("detachOriginFieldRecordIds")]
        public List<Guid> DetachOriginFieldRecordIds { get; set; } = new List<Guid>();
    }

    /// <summary>
    /// Simplified relation option reference model used for configuration and
    /// UI field selection. Contains just the relation identifier, name, and
    /// direction for lightweight serialization in option lists.
    /// Migrated from: WebVella.Erp.Api.Models.EntityRelationOptions
    /// </summary>
    [Serializable]
    public class EntityRelationOptions
    {
        /// <summary>
        /// The unique identifier of the referenced relation. Nullable to support
        /// placeholder/default options with no specific relation selected.
        /// </summary>
        [JsonPropertyName("relationId")]
        public Guid? RelationId { get; set; }

        /// <summary>
        /// The programmatic name of the referenced relation.
        /// </summary>
        [JsonPropertyName("relationName")]
        public string RelationName { get; set; } = string.Empty;

        /// <summary>
        /// The direction of the relation from the context entity's perspective.
        /// Typically "origin-target" or "target-origin" to indicate navigation direction.
        /// </summary>
        [JsonPropertyName("direction")]
        public string Direction { get; set; } = string.Empty;
    }
}
