// =============================================================================
// EmailField.cs — Email Field Type Model
// =============================================================================
// Migrated from monolith: WebVella.Erp/Api/Models/FieldTypes/EmailField.cs
// Database reference:      WebVella.Erp/Database/FieldTypes/DbEmailField.cs
//
// Contains two classes:
//   - InputEmailField  : InputField  (request DTO for create/update operations)
//   - EmailField       : Field       (persisted/returned model, [Serializable])
//
// Both classes expose a static FieldType discriminator property (FieldType.EmailField),
// a DefaultValue (string), and an optional MaxLength (int?) constraint.
//
// The email field type stores a single email address string value. MaxLength
// constrains the maximum number of characters allowed (null means no limit).
// Validation of email format is performed at the service layer, not in the model.
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
    /// Input DTO for creating or updating an email field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from InputField.
    /// Adds email-specific properties: DefaultValue and MaxLength.
    /// Migrated from: WebVella.Erp.Api.Models.InputEmailField (EmailField.cs lines 6-16)
    /// </summary>
    public class InputEmailField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as an EmailField type.
        /// Returns <see cref="FieldType.EmailField"/> (enum value 6).
        /// Used by polymorphic deserialization in <see cref="InputField.ConvertField"/>.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.EmailField;

        /// <summary>
        /// Default value assigned to new records when no explicit value is provided.
        /// Typically null or a placeholder email address.
        /// Nullable in the target codebase (nullable reference types enabled).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Maximum number of characters allowed for this email field.
        /// Null indicates no limit. Enforced during record create/update validation.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }

    /// <summary>
    /// Persisted/returned model representing a fully materialized email field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from Field.
    /// Adds email-specific properties: DefaultValue and MaxLength.
    /// Marked [Serializable] for CLR serialization compatibility with the original monolith pattern.
    /// Migrated from: WebVella.Erp.Api.Models.EmailField (EmailField.cs lines 18-29)
    /// </summary>
    [Serializable]
    public class EmailField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as an EmailField type.
        /// Returns <see cref="FieldType.EmailField"/> (enum value 6).
        /// Used by <see cref="Field.GetFieldType"/> and <see cref="Field.GetFieldDefaultValue"/>
        /// for runtime type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.EmailField;

        /// <summary>
        /// Default value assigned to new records when no explicit value is provided.
        /// Retrieved by <see cref="Field.GetFieldDefaultValue"/> when the field type is EmailField.
        /// Nullable in the target codebase (nullable reference types enabled).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        /// <summary>
        /// Maximum number of characters allowed for this email field.
        /// Null indicates no limit. Enforced during record create/update validation.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }
}
