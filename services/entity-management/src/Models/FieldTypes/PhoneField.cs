// =============================================================================
// PhoneField.cs — Phone Field Type Model
// =============================================================================
// Migrated from monolith: WebVella.Erp/Api/Models/FieldTypes/PhoneField.cs
// Database reference:      WebVella.Erp/Database/FieldTypes/DbPhoneField.cs
//
// Contains two classes:
//   - InputPhoneField  : InputField  (request DTO for create/update operations)
//   - PhoneField       : Field       (persisted/returned model, [Serializable])
//
// Both classes expose a static FieldType discriminator property
// (FieldType.PhoneField), a DefaultValue (string), a Format (string) for
// phone number formatting patterns, and an optional MaxLength (int?)
// constraint for maximum character count.
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
    /// Input DTO for creating or updating a phone field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from InputField.
    /// Adds phone-specific properties: DefaultValue, Format, and MaxLength.
    /// Migrated from: WebVella.Erp.Api.Models.InputPhoneField (PhoneField.cs lines 6-19)
    /// </summary>
    public class InputPhoneField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a PhoneField type.
        /// Returns <see cref="FieldType.PhoneField"/> (enum value 15).
        /// Used by polymorphic deserialization in <see cref="InputField.ConvertField"/>.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PhoneField;

        /// <summary>
        /// Default value assigned to new records when no explicit value is provided.
        /// Typically an empty string or a placeholder phone number value.
        /// Nullable in the target codebase (nullable reference types enabled).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Phone number formatting pattern string.
        /// Defines the display format for the phone field value (e.g., "(###) ###-####").
        /// Nullable — when null, no specific format is enforced.
        /// </summary>
        [JsonPropertyName("format")]
        public string? Format { get; set; }

        /// <summary>
        /// Maximum number of characters allowed for this phone field.
        /// Null indicates no limit. Enforced during record create/update validation.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }

    /// <summary>
    /// Persisted/returned model representing a fully materialized phone field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from Field.
    /// Adds phone-specific properties: DefaultValue, Format, and MaxLength.
    /// Marked [Serializable] for CLR serialization compatibility with the original monolith pattern.
    /// Migrated from: WebVella.Erp.Api.Models.PhoneField (PhoneField.cs lines 21-35)
    /// </summary>
    [Serializable]
    public class PhoneField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a PhoneField type.
        /// Returns <see cref="FieldType.PhoneField"/> (enum value 15).
        /// Used by <see cref="Field.GetFieldType"/> and <see cref="Field.GetFieldDefaultValue"/>
        /// for runtime type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PhoneField;

        /// <summary>
        /// Default value assigned to new records when no explicit value is provided.
        /// Retrieved by <see cref="Field.GetFieldDefaultValue"/> when the field type is PhoneField.
        /// Nullable in the target codebase (nullable reference types enabled).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Phone number formatting pattern string.
        /// Defines the display format for the phone field value (e.g., "(###) ###-####").
        /// Nullable — when null, no specific format is enforced.
        /// </summary>
        [JsonPropertyName("format")]
        public string? Format { get; set; }

        /// <summary>
        /// Maximum number of characters allowed for this phone field.
        /// Null indicates no limit. Enforced during record create/update validation.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }
}
