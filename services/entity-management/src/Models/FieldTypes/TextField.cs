// =============================================================================
// TextField.cs — Text Field Type Model
// =============================================================================
// Migrated from monolith: WebVella.Erp/Api/Models/FieldTypes/TextField.cs
// Database reference:      WebVella.Erp/Database/FieldTypes/DbTextField.cs
//
// Contains two classes:
//   - InputTextField  : InputField  (request DTO for create/update operations)
//   - TextField       : Field       (persisted/returned model, [Serializable])
//
// Both classes expose a static FieldType discriminator property (FieldType.TextField),
// a DefaultValue (string), and an optional MaxLength (int?) constraint.
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
    /// Input DTO for creating or updating a text field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from InputField.
    /// Adds text-specific properties: DefaultValue and MaxLength.
    /// Migrated from: WebVella.Erp.Api.Models.InputTextField (TextField.cs lines 6-16)
    /// </summary>
    public class InputTextField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a TextField type.
        /// Returns <see cref="FieldType.TextField"/> (enum value 18).
        /// Used by polymorphic deserialization in <see cref="InputField.ConvertField"/>.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.TextField;

        /// <summary>
        /// Default value assigned to new records when no explicit value is provided.
        /// Typically an empty string or a placeholder text value.
        /// Nullable in the target codebase (nullable reference types enabled).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Maximum number of characters allowed for this text field.
        /// Null indicates no limit. Enforced during record create/update validation.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }

    /// <summary>
    /// Persisted/returned model representing a fully materialized text field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from Field.
    /// Adds text-specific properties: DefaultValue and MaxLength.
    /// Marked [Serializable] for CLR serialization compatibility with the original monolith pattern.
    /// Migrated from: WebVella.Erp.Api.Models.TextField (TextField.cs lines 18-29)
    /// </summary>
    [Serializable]
    public class TextField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a TextField type.
        /// Returns <see cref="FieldType.TextField"/> (enum value 18).
        /// Used by <see cref="Field.GetFieldType"/> and <see cref="Field.GetFieldDefaultValue"/>
        /// for runtime type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.TextField;

        /// <summary>
        /// Default value assigned to new records when no explicit value is provided.
        /// Retrieved by <see cref="Field.GetFieldDefaultValue"/> when the field type is TextField.
        /// Nullable in the target codebase (nullable reference types enabled).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Maximum number of characters allowed for this text field.
        /// Null indicates no limit. Enforced during record create/update validation.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }
}
