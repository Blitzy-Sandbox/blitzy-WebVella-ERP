// -----------------------------------------------------------------------
// NumberField.cs — Number Field Type Model
//
// Migrated from monolith source:
//   WebVella.Erp/Api/Models/FieldTypes/NumberField.cs
//   WebVella.Erp/Database/FieldTypes/DbNumberField.cs
//
// Contains InputNumberField (request DTO for create/update operations) and
// NumberField (persisted/returned model with [Serializable] for CLR compat).
//
// Import transformations applied:
//   - Newtonsoft.Json [JsonProperty(PropertyName=...)] → System.Text.Json [JsonPropertyName(...)]
//   - Namespace WebVella.Erp.Api.Models → WebVellaErp.EntityManagement.Models
//
// Both classes expose: FieldType, DefaultValue, MinValue, MaxValue, DecimalPlaces.
// -----------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Request DTO for creating or updating a number field definition.
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from <see cref="InputField"/>.
    /// Adds number-specific configuration: default value, min/max range, and decimal precision.
    /// </summary>
    public class InputNumberField : InputField
    {
        /// <summary>
        /// Discriminator identifying this as a number field type.
        /// Always returns <see cref="Models.FieldType.NumberField"/>.
        /// Used by polymorphic deserialization in <see cref="InputField.ConvertField"/> to
        /// dispatch JSON payloads to the correct concrete field type.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.NumberField;

        /// <summary>
        /// Default numeric value assigned to new records when no explicit value is provided.
        /// Nullable to allow fields with no default. Stored as decimal for precision.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// Minimum allowed value for this number field. Validation enforced during
        /// record create/update operations. Null means no lower bound constraint.
        /// </summary>
        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        /// <summary>
        /// Maximum allowed value for this number field. Validation enforced during
        /// record create/update operations. Null means no upper bound constraint.
        /// </summary>
        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        /// <summary>
        /// Number of decimal places for display and storage precision.
        /// Byte type matches the monolith source (0–255 range, typically 0–10 in practice).
        /// Null means no explicit precision constraint (service defaults apply).
        /// </summary>
        [JsonPropertyName("decimalPlaces")]
        public byte? DecimalPlaces { get; set; }
    }

    /// <summary>
    /// Persisted and returned model representing a number field definition.
    /// Inherits common persisted field metadata (Id, Name, Label, Required, etc.) from <see cref="Field"/>.
    /// Marked <c>[Serializable]</c> for CLR serialization compatibility, matching the original
    /// monolith pattern from WebVella.Erp.Api.Models.NumberField.
    /// </summary>
    /// <remarks>
    /// This class is used for:
    /// <list type="bullet">
    ///   <item>Entity metadata retrieval (GET /v1/entities/{id}/fields)</item>
    ///   <item>Field type dispatch in <see cref="Field.GetFieldDefaultValue"/> (casts to NumberField)</item>
    ///   <item>Field type identification in <see cref="Field.GetFieldType"/> (checks <c>is NumberField</c>)</item>
    /// </list>
    /// </remarks>
    [Serializable]
    public class NumberField : Field
    {
        /// <summary>
        /// Discriminator identifying this as a number field type.
        /// Always returns <see cref="Models.FieldType.NumberField"/>.
        /// Used for serialization and polymorphic field type identification.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.NumberField;

        /// <summary>
        /// Default numeric value assigned to new records when no explicit value is provided.
        /// Nullable to allow fields with no default. Stored as decimal for precision.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// Minimum allowed value for this number field. Validation enforced during
        /// record create/update operations. Null means no lower bound constraint.
        /// </summary>
        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        /// <summary>
        /// Maximum allowed value for this number field. Validation enforced during
        /// record create/update operations. Null means no upper bound constraint.
        /// </summary>
        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        /// <summary>
        /// Number of decimal places for display and storage precision.
        /// Byte type matches the monolith source (0–255 range, typically 0–10 in practice).
        /// Null means no explicit precision constraint (service defaults apply).
        /// </summary>
        [JsonPropertyName("decimalPlaces")]
        public byte? DecimalPlaces { get; set; }
    }
}
