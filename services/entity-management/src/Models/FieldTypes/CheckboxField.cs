// -----------------------------------------------------------------------
// CheckboxField.cs — Checkbox Field Type Model
//
// Migrated from monolith: WebVella.Erp/Api/Models/FieldTypes/CheckboxField.cs
// Database reference: WebVella.Erp/Database/FieldTypes/DbCheckboxField.cs
//
// Contains the Input (request DTO) and persisted/returned model pair for
// the boolean checkbox field type. This is the simplest field type in the
// ERP system — it stores a nullable boolean value with an optional default.
//
// Serialization: Uses System.Text.Json [JsonPropertyName] attributes
// (AOT-safe) replacing the monolith's Newtonsoft.Json [JsonProperty].
// JSON property names are preserved exactly for backward compatibility:
//   "fieldType"    — static discriminator for polymorphic deserialization
//   "defaultValue" — nullable boolean default value
// -----------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Request DTO for creating or updating a checkbox field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from
    /// <see cref="InputField"/> and adds the checkbox-specific
    /// <see cref="DefaultValue"/> property.
    /// </summary>
    /// <remarks>
    /// Migrated from <c>WebVella.Erp.Api.Models.InputCheckboxField</c>.
    /// Used in entity field creation/update API requests where the caller
    /// specifies the desired field configuration.
    /// </remarks>
    public class InputCheckboxField : InputField
    {
        /// <summary>
        /// Static discriminator identifying this as a checkbox field type.
        /// Used by the polymorphic field deserialization pipeline in
        /// <see cref="InputField.ConvertField"/> to resolve the concrete type.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.CheckboxField;

        /// <summary>
        /// The default boolean value for new records when no explicit value is provided.
        /// <c>null</c> indicates no default; <c>true</c>/<c>false</c> sets the initial state.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public bool? DefaultValue { get; set; }
    }

    /// <summary>
    /// Persisted and returned model for a checkbox field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from
    /// <see cref="Field"/> and adds the checkbox-specific
    /// <see cref="DefaultValue"/> property.
    /// </summary>
    /// <remarks>
    /// Migrated from <c>WebVella.Erp.Api.Models.CheckboxField</c>.
    /// Decorated with <see cref="SerializableAttribute"/> for CLR serialization
    /// compatibility, preserving the original monolith pattern.
    /// Used in entity metadata responses and internal field processing.
    /// </remarks>
    [Serializable]
    public class CheckboxField : Field
    {
        /// <summary>
        /// Static discriminator identifying this as a checkbox field type.
        /// Used by the polymorphic field deserialization pipeline in
        /// <see cref="Field.GetFieldType(FieldType)"/> to resolve the concrete type.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.CheckboxField;

        /// <summary>
        /// The default boolean value for new records when no explicit value is provided.
        /// <c>null</c> indicates no default; <c>true</c>/<c>false</c> sets the initial state.
        /// Persisted in DynamoDB as a boolean attribute on the field metadata item.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public bool? DefaultValue { get; set; }
    }
}
