// =============================================================================
// DateTimeField.cs — DateTime Field Type Model
// =============================================================================
// Migrated from: WebVella.Erp/Api/Models/FieldTypes/DateTimeField.cs
// Reference:     WebVella.Erp/Database/FieldTypes/DbDateTimeField.cs
//
// Contains two classes for the DateTime field type:
//   - InputDateTimeField : InputField  (request DTO for create/update operations)
//   - DateTimeField : Field            (persisted/returned field definition model)
//
// Both classes expose the same set of DateTime-specific properties:
//   - FieldType (static) → FieldType.DateTimeField
//   - DefaultValue (DateTime?) → optional default datetime value
//   - Format (string) → display format string for datetime rendering
//   - UseCurrentTimeAsDefaultValue (bool?) → when true, DateTime.Now is used as default
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
//
// The DateTimeField type supports storing date-and-time values with optional
// formatting. When UseCurrentTimeAsDefaultValue is true, the system uses
// DateTime.Now as the default value instead of the static DefaultValue property.
// This behavior is consumed by Field.GetFieldDefaultValue() in Field.cs.
// =============================================================================

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input DTO for creating or updating a DateTime field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from InputField.
    /// Properties are nullable to support partial updates where only changed values are provided.
    /// Migrated from: WebVella.Erp.Api.Models.InputDateTimeField (DateTimeField.cs lines 6-19)
    /// </summary>
    public class InputDateTimeField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a DateTimeField type.
        /// Used during polymorphic deserialization in InputField.ConvertField().
        /// Always returns FieldType.DateTimeField (enum value 5).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.DateTimeField;

        /// <summary>
        /// Optional static default value for new records. When UseCurrentTimeAsDefaultValue
        /// is true, this value is ignored and DateTime.Now is used instead.
        /// Nullable to indicate "no default" when not set.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public DateTime? DefaultValue { get; set; }

        /// <summary>
        /// Display format string for rendering the datetime value in the UI.
        /// Common formats include "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy hh:mm tt", etc.
        /// The format string follows standard .NET DateTime format specifiers.
        /// </summary>
        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        /// <summary>
        /// When set to true, the system uses DateTime.Now as the default value for
        /// new records instead of the static DefaultValue property. This allows
        /// automatic timestamping of record creation without specifying a fixed default.
        /// Nullable to distinguish between "not specified" (null) and explicitly set (true/false).
        /// </summary>
        [JsonPropertyName("useCurrentTimeAsDefaultValue")]
        public bool? UseCurrentTimeAsDefaultValue { get; set; }
    }

    /// <summary>
    /// Persisted/returned model for a DateTime field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from Field base class.
    /// Properties represent fully materialized field metadata stored in the entity management datastore.
    /// Marked [Serializable] for CLR serialization compatibility with the original monolith pattern.
    /// Migrated from: WebVella.Erp.Api.Models.DateTimeField (DateTimeField.cs lines 21-35)
    /// </summary>
    [Serializable]
    public class DateTimeField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a DateTimeField type.
        /// Used by Field.GetFieldType() for runtime type resolution and by
        /// serialization for polymorphic field type dispatch.
        /// Always returns FieldType.DateTimeField (enum value 5).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.DateTimeField;

        /// <summary>
        /// Static default value for new records. When UseCurrentTimeAsDefaultValue
        /// is true, Field.GetFieldDefaultValue() returns DateTime.Now instead.
        /// Nullable to indicate "no default" when not set.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public DateTime? DefaultValue { get; set; }

        /// <summary>
        /// Display format string for rendering the datetime value in the UI.
        /// Common formats include "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy hh:mm tt", etc.
        /// The format string follows standard .NET DateTime format specifiers.
        /// </summary>
        [JsonPropertyName("format")]
        public string Format { get; set; } = string.Empty;

        /// <summary>
        /// When set to true, the system uses DateTime.Now as the default value for
        /// new records instead of the static DefaultValue property. This enables
        /// automatic timestamping of record creation. Consumed by
        /// Field.GetFieldDefaultValue() in the base Field class.
        /// Nullable to distinguish between "not specified" (null) and explicitly set (true/false).
        /// </summary>
        [JsonPropertyName("useCurrentTimeAsDefaultValue")]
        public bool? UseCurrentTimeAsDefaultValue { get; set; }
    }
}
