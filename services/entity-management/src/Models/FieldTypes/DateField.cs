// =============================================================================
// DateField.cs — Date Field Type Model
// =============================================================================
// Migrates the date field type from the monolith source:
//   - WebVella.Erp/Api/Models/FieldTypes/DateField.cs (InputDateField + DateField)
//   - WebVella.Erp/Database/FieldTypes/DbDateField.cs  (DB-layer reference)
//
// Contains two classes:
//   1. InputDateField : InputField — Request DTO for date field creation/update
//   2. DateField : Field           — Persisted/returned date field model
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
    /// Request DTO for creating or updating a date field.
    /// Properties are nullable to support partial updates where only changed values
    /// are provided. Inherits common field metadata from <see cref="InputField"/>.
    /// Migrated from: WebVella.Erp.Api.Models.InputDateField (DateField.cs lines 6-19)
    /// </summary>
    public class InputDateField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a DateField type.
        /// Returns <see cref="FieldType.DateField"/> (enum value 4).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.DateField;

        /// <summary>
        /// Optional default date value for new records.
        /// When <see cref="UseCurrentTimeAsDefaultValue"/> is true, this value is ignored
        /// and the current date (DateTime.Now.Date) is used instead.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public DateTime? DefaultValue { get; set; }

        /// <summary>
        /// Display format string for the date value (e.g., "yyyy-MM-dd").
        /// Controls how the date is rendered in the UI and formatted in exports.
        /// </summary>
        [JsonPropertyName("format")]
        public string Format { get; set; }

        /// <summary>
        /// When true, the current date (DateTime.Now.Date) is used as the default
        /// value for new records, overriding <see cref="DefaultValue"/>.
        /// </summary>
        [JsonPropertyName("useCurrentTimeAsDefaultValue")]
        public bool? UseCurrentTimeAsDefaultValue { get; set; }
    }

    /// <summary>
    /// Persisted/returned date field model representing a fully materialized date field
    /// definition in the entity metadata system. Inherits common field metadata from
    /// <see cref="Field"/>. Marked [Serializable] for CLR serialization compatibility
    /// with the original monolith pattern.
    /// Migrated from: WebVella.Erp.Api.Models.DateField (DateField.cs lines 21-35)
    /// </summary>
    [Serializable]
    public class DateField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a DateField type.
        /// Returns <see cref="FieldType.DateField"/> (enum value 4).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.DateField;

        /// <summary>
        /// Default date value for new records. When <see cref="UseCurrentTimeAsDefaultValue"/>
        /// is true, the runtime default is DateTime.Now.Date (handled by
        /// <see cref="Field.GetFieldDefaultValue"/>), not this stored value.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public DateTime? DefaultValue { get; set; }

        /// <summary>
        /// Display format string for the date value (e.g., "yyyy-MM-dd").
        /// Controls how the date is rendered in the UI and formatted in exports.
        /// </summary>
        [JsonPropertyName("format")]
        public string Format { get; set; }

        /// <summary>
        /// When true, the current date (DateTime.Now.Date) is used as the default
        /// value for new records, overriding <see cref="DefaultValue"/>.
        /// This behavior is implemented in <see cref="Field.GetFieldDefaultValue"/>.
        /// </summary>
        [JsonPropertyName("useCurrentTimeAsDefaultValue")]
        public bool? UseCurrentTimeAsDefaultValue { get; set; }
    }
}
