// =============================================================================
// FileField.cs — File Field Type Model
// =============================================================================
// Migrated from monolith:
//   - WebVella.Erp/Api/Models/FieldTypes/FileField.cs (InputFileField + FileField)
//   - WebVella.Erp/Database/FieldTypes/DbFileField.cs (DB-layer reference)
//
// Contains InputFileField (request DTO for create/update) and FileField
// (persisted/returned model). Both expose a static FieldType discriminator
// and a string DefaultValue property for file path defaults.
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
// =============================================================================

using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Request DTO for creating or updating a file field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from InputField.
    /// The DefaultValue property specifies the default file path or reference string
    /// assigned when a new record is created with this field.
    /// Migrated from: WebVella.Erp.Api.Models.InputFileField (FileField.cs lines 6-13)
    /// </summary>
    public class InputFileField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a file field type.
        /// Returns <see cref="FieldType.FileField"/> (enum value 7).
        /// Used by polymorphic deserialization in <see cref="InputField.ConvertField"/>.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.FileField;

        /// <summary>
        /// Default file path or reference string assigned to new records.
        /// Typically empty or null for file fields, as files are uploaded after record creation.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = string.Empty;
    }

    /// <summary>
    /// Persisted/returned model for a file field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from Field.
    /// Represents the fully materialized metadata for a file-type field attached to an entity.
    /// The [Serializable] attribute is preserved for CLR serialization compatibility.
    /// Migrated from: WebVella.Erp.Api.Models.FileField (FileField.cs lines 15-23)
    /// </summary>
    [Serializable]
    public class FileField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a file field type.
        /// Returns <see cref="FieldType.FileField"/> (enum value 7).
        /// Used by <see cref="Field.GetFieldType"/> for runtime type resolution.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.FileField;

        /// <summary>
        /// Default file path or reference string for this field.
        /// Retrieved by <see cref="Field.GetFieldDefaultValue"/> when computing
        /// default values for new record creation.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = string.Empty;
    }
}
