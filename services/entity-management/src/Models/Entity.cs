// Entity.cs — Entity Metadata Models for Entity Management Service
//
// Migrated from: WebVella.Erp/Api/Models/Entity.cs
// Contains the core entity definition models (InputEntity, Entity, RecordPermissions)
// and response wrappers used by the Entity Management bounded context Lambda handlers.
//
// JSON Serialization: System.Text.Json [JsonPropertyName] attributes (AOT-safe)
// replacing Newtonsoft.Json [JsonProperty(PropertyName=...)] from the monolith source.

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input model for entity create/update operations. Properties are nullable
    /// to support partial update payloads where only changed fields are sent.
    /// </summary>
    public class InputEntity
    {
        [JsonPropertyName("id")]
        public Guid? Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("labelPlural")]
        public string LabelPlural { get; set; } = "";

        [JsonPropertyName("system")]
        public bool? System { get; set; } = false;

        [JsonPropertyName("iconName")]
        public string IconName { get; set; } = "";

        [JsonPropertyName("color")]
        public string Color { get; set; } = "";

        [JsonPropertyName("recordPermissions")]
        public RecordPermissions RecordPermissions { get; set; } = new();

        /// <summary>
        /// If null the ID field of the record is used as ScreenId.
        /// </summary>
        [JsonPropertyName("record_screen_id_field")]
        public Guid? RecordScreenIdField { get; set; }
    }

    /// <summary>
    /// Fully materialized entity metadata model. Represents a persisted entity definition
    /// including its field schema collection and access permissions.
    /// </summary>
    [Serializable]
    public class Entity
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("labelPlural")]
        public string LabelPlural { get; set; } = "";

        [JsonPropertyName("system")]
        public bool System { get; set; } = false;

        [JsonPropertyName("iconName")]
        public string IconName { get; set; } = "";

        [JsonPropertyName("color")]
        public string Color { get; set; } = "";

        [JsonPropertyName("recordPermissions")]
        public RecordPermissions RecordPermissions { get; set; } = new();

        /// <summary>
        /// Collection of field definitions that belong to this entity.
        /// Each field describes one column/attribute of the entity's records.
        /// </summary>
        [JsonPropertyName("fields")]
        public List<Field> Fields { get; set; } = new();

        /// <summary>
        /// If null the ID field of the record is used as ScreenId.
        /// </summary>
        [JsonPropertyName("record_screen_id_field")]
        public Guid? RecordScreenIdField { get; set; }

        /// <summary>
        /// Content hash for cache invalidation and optimistic concurrency.
        /// Set internally by the entity management service; not externally settable.
        /// </summary>
        [JsonInclude]
        [JsonPropertyName("hash")]
        public string Hash { get; internal set; } = string.Empty;

        /// <summary>
        /// Returns the entity name for display and debugging purposes.
        /// </summary>
        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Defines role-based access control permissions for record operations on an entity.
    /// Each list contains role GUIDs that are granted the corresponding permission.
    /// </summary>
    [Serializable]
    public class RecordPermissions
    {
        [JsonPropertyName("canRead")]
        public List<Guid> CanRead { get; set; } = new();

        [JsonPropertyName("canCreate")]
        public List<Guid> CanCreate { get; set; } = new();

        [JsonPropertyName("canUpdate")]
        public List<Guid> CanUpdate { get; set; } = new();

        [JsonPropertyName("canDelete")]
        public List<Guid> CanDelete { get; set; } = new();
    }

    /// <summary>
    /// API response wrapper for a single entity.
    /// Inherits standardized envelope from BaseResponseModel.
    /// </summary>
    public class EntityResponse : BaseResponseModel
    {
        [JsonPropertyName("object")]
        public Entity? Object { get; set; }
    }

    /// <summary>
    /// API response wrapper for a list of entities.
    /// Inherits standardized envelope from BaseResponseModel.
    /// </summary>
    public class EntityListResponse : BaseResponseModel
    {
        [JsonPropertyName("object")]
        public List<Entity>? Object { get; set; }
    }

    /// <summary>
    /// API response wrapper for entity library items (mixed-type collection).
    /// Inherits standardized envelope from BaseResponseModel.
    /// </summary>
    public class EntityLibraryItemsResponse : BaseResponseModel
    {
        [JsonPropertyName("object")]
        public List<object>? Object { get; set; }
    }
}
