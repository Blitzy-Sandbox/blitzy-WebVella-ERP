// =============================================================================
// MultiLineTextField.cs — Multi-Line Text Field Type Model
// =============================================================================
// Migrates the multi-line text field type from:
//   Source: WebVella.Erp/Api/Models/FieldTypes/MultiLineTextField.cs
//   DB Ref: WebVella.Erp/Database/FieldTypes/DbMultiLineTextField.cs
//
// Contains two classes:
//   1. InputMultiLineTextField : InputField — Request DTO for create/update operations
//   2. MultiLineTextField : Field — Persisted/returned field definition (Serializable)
//
// Both classes expose type-specific properties: DefaultValue, MaxLength,
// and VisibleLineNumber, in addition to base properties inherited from
// InputField / Field respectively.
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
    /// Request DTO for creating or updating a multi-line text field definition.
    /// Properties are nullable to support partial updates where only changed values
    /// are provided. Inherits common field metadata (Id, Name, Label, etc.) from
    /// the abstract InputField base class.
    /// Migrated from: WebVella.Erp.Api.Models.InputMultiLineTextField (lines 6-19)
    /// </summary>
    public class InputMultiLineTextField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a MultiLineTextField type.
        /// Used for polymorphic deserialization dispatch in InputField.ConvertField().
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiLineTextField;

        /// <summary>
        /// The default value pre-populated when creating a new record with this field.
        /// Typically an empty string or a template multi-line text value.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; }

        /// <summary>
        /// Maximum number of characters allowed in this multi-line text field.
        /// Null indicates no maximum length constraint is enforced.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        /// <summary>
        /// Number of visible text lines to display in the UI textarea control.
        /// Controls the initial height of the input element. Null uses the UI default.
        /// </summary>
        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }
    }

    /// <summary>
    /// Persisted/returned model representing a fully materialized multi-line text
    /// field definition. Properties are non-nullable (represent fully hydrated metadata).
    /// Inherits common field metadata (Id, Name, Label, Required, etc.) from the
    /// abstract Field base class. Marked [Serializable] for CLR serialization compatibility.
    /// Migrated from: WebVella.Erp.Api.Models.MultiLineTextField (lines 21-35)
    /// </summary>
    [Serializable]
    public class MultiLineTextField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a MultiLineTextField type.
        /// Used for runtime type resolution in Field.GetFieldType() and serialization.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiLineTextField;

        /// <summary>
        /// The default value pre-populated when creating a new record with this field.
        /// Retrieved by Field.GetFieldDefaultValue() for record initialization.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; }

        /// <summary>
        /// Maximum number of characters allowed in this multi-line text field.
        /// Null indicates no maximum length constraint is enforced.
        /// Validated during record create/update operations by the entity service.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        /// <summary>
        /// Number of visible text lines to display in the UI textarea control.
        /// Controls the initial height of the input element. Null uses the UI default.
        /// </summary>
        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }
    }
}
