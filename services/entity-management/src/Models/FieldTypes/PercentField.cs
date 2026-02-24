// =============================================================================
// PercentField.cs — Percent Field Type Model (Input DTO + Persisted Model)
// =============================================================================
// Migrates the percent field type from the WebVella ERP monolith:
//   Source: WebVella.Erp/Api/Models/FieldTypes/PercentField.cs (lines 1-42)
//   Ref:    WebVella.Erp/Database/FieldTypes/DbPercentField.cs (DB layer, not ported)
//
// Contains two classes:
//   InputPercentField : InputField — Request DTO for creating/updating percent fields.
//   PercentField : Field           — Persisted/returned percent field model ([Serializable]).
//
// Both classes expose identical type-specific properties:
//   FieldType (static), DefaultValue, MinValue, MaxValue, DecimalPlaces
//
// This file follows the identical structure of NumberField.cs (same 4 properties,
// same types) — the only difference is the FieldType enum value returned.
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
//
// Dependency: Field.cs (InputField base, Field base, FieldType enum)
// =============================================================================

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input DTO for creating or updating a percent field definition on an entity.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="InputField"/> and adds percent-specific configuration: default value,
    /// min/max range constraints, and decimal precision.
    /// <para>
    /// Properties are nullable to support partial updates where only changed values
    /// are provided in the request payload.
    /// </para>
    /// <para>
    /// Migrated from: WebVella.Erp.Api.Models.InputPercentField (PercentField.cs lines 6-22)
    /// </para>
    /// </summary>
    public class InputPercentField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a PercentField type.
        /// Returns <see cref="FieldType.PercentField"/> (enum value 14).
        /// Used by polymorphic deserialization in <see cref="InputField.ConvertField"/>
        /// to dispatch incoming JSON payloads to the correct concrete InputField subclass.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PercentField;

        /// <summary>
        /// Default value assigned to this percent field when a new record is created
        /// and no explicit value is provided. Stored as a decimal representing a
        /// percentage (e.g., 0.25 for 25%). Null means no default is applied.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// Minimum allowed value for this percent field. Null means no lower bound
        /// constraint is enforced during record validation.
        /// </summary>
        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        /// <summary>
        /// Maximum allowed value for this percent field. Null means no upper bound
        /// constraint is enforced during record validation.
        /// </summary>
        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        /// <summary>
        /// Number of decimal places to use when storing and displaying this percent field.
        /// Controls numeric precision for the percentage value. Null means no explicit
        /// precision constraint (service default applies).
        /// </summary>
        [JsonPropertyName("decimalPlaces")]
        public byte? DecimalPlaces { get; set; }
    }

    /// <summary>
    /// Persisted/returned model representing a fully materialized percent field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="Field"/> and adds percent-specific configuration: default value,
    /// min/max range constraints, and decimal precision.
    /// <para>
    /// Unlike <see cref="InputPercentField"/>, properties on this class represent
    /// the complete, non-partial state of a stored field definition as read from
    /// the DynamoDB entity metadata table.
    /// </para>
    /// <para>
    /// Marked [Serializable] for CLR serialization compatibility, preserving the
    /// original monolith pattern where Field subclasses are cached and cloned.
    /// </para>
    /// <para>
    /// Migrated from: WebVella.Erp.Api.Models.PercentField (PercentField.cs lines 24-41)
    /// </para>
    /// </summary>
    [Serializable]
    public class PercentField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a PercentField type.
        /// Returns <see cref="FieldType.PercentField"/> (enum value 14).
        /// Used by <see cref="Field.GetFieldType"/> for runtime type resolution
        /// and by <see cref="Field.GetFieldDefaultValue"/> for default value dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PercentField;

        /// <summary>
        /// Default value assigned to this percent field when a new record is created
        /// and no explicit value is provided. Stored as a decimal representing a
        /// percentage (e.g., 0.25 for 25%). Null means no default is applied.
        /// Referenced by <see cref="Field.GetFieldDefaultValue"/> (Field.cs line 449).
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        /// <summary>
        /// Minimum allowed value for this percent field. Null means no lower bound
        /// constraint is enforced during record validation.
        /// </summary>
        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        /// <summary>
        /// Maximum allowed value for this percent field. Null means no upper bound
        /// constraint is enforced during record validation.
        /// </summary>
        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        /// <summary>
        /// Number of decimal places to use when storing and displaying this percent field.
        /// Controls numeric precision for the percentage value. Null means no explicit
        /// precision constraint (service default applies).
        /// </summary>
        [JsonPropertyName("decimalPlaces")]
        public byte? DecimalPlaces { get; set; }
    }
}
