// =============================================================================
// MultiSelectField.cs — Multi-Select Field Type Model
// =============================================================================
// Migrates the multi-select field type from the monolith source files:
//   - WebVella.Erp/Api/Models/FieldTypes/MultiSelectField.cs
//     (InputMultiSelectField + MultiSelectField classes)
//   - WebVella.Erp/Database/FieldTypes/DbMultiSelectField.cs (DB layer reference)
//
// Contains two classes:
//   1. InputMultiSelectField : InputField  — request DTO for field creation/update
//   2. MultiSelectField : Field            — persisted/returned field model
//
// Both classes use SelectOption from SelectField.cs (same namespace) for the
// Options property, enabling reuse of the shared option DTO across single-select
// and multi-select field types.
//
// Key difference from SelectField: DefaultValue is IEnumerable<string> (multiple
// selected values) rather than a single string.
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
//
// JSON Property Name Contracts (must be preserved exactly):
//   InputMultiSelectField / MultiSelectField: "fieldType", "defaultValue", "options"
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input DTO for creating or updating a multi-select field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="InputField"/> and adds multi-select-specific properties:
    /// DefaultValue (multiple pre-selected values) and Options (available choices).
    ///
    /// Unlike <see cref="InputSelectField"/> which allows a single default string,
    /// this class uses <see cref="IEnumerable{T}"/> of string for DefaultValue to
    /// support multiple simultaneous default selections.
    ///
    /// Migrated from: WebVella.Erp.Api.Models.InputMultiSelectField
    /// (MultiSelectField.cs lines 8-18)
    /// </summary>
    public class InputMultiSelectField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a MultiSelectField type.
        /// Returns <see cref="FieldType.MultiSelectField"/> (value 11).
        /// Static so the value is available without instantiation for type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiSelectField;

        /// <summary>
        /// The default set of selected values for new records using this field.
        /// Each value should match one of the <see cref="SelectOption.Value"/> entries
        /// in <see cref="Options"/>. May be null or empty for no default selections.
        /// Uses <see cref="IEnumerable{T}"/> to accept any collection type (arrays,
        /// lists, etc.) during deserialization while remaining read-flexible.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public IEnumerable<string> DefaultValue { get; set; }

        /// <summary>
        /// The list of selectable options available for this multi-select field.
        /// Each option has a value, display label, optional icon class, and color.
        /// Users may select zero or more options from this list.
        /// Shares the <see cref="SelectOption"/> type with <see cref="InputSelectField"/>.
        /// </summary>
        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; }
    }

    /// <summary>
    /// Persisted/returned model for a multi-select field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="Field"/> and adds multi-select-specific properties: DefaultValue
    /// (multiple pre-selected values) and Options (available choices).
    /// Marked [Serializable] for CLR serialization compatibility matching the
    /// original monolith pattern.
    ///
    /// Unlike <see cref="SelectField"/> which stores a single default string,
    /// this class uses <see cref="IEnumerable{T}"/> of string for DefaultValue to
    /// support multiple simultaneous default selections.
    ///
    /// Migrated from: WebVella.Erp.Api.Models.MultiSelectField
    /// (MultiSelectField.cs lines 20-31)
    /// </summary>
    [Serializable]
    public class MultiSelectField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a MultiSelectField type.
        /// Returns <see cref="FieldType.MultiSelectField"/> (value 11).
        /// Static so the value is available without instantiation for type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiSelectField;

        /// <summary>
        /// The default set of selected values for new records using this field.
        /// Each value should match one of the <see cref="SelectOption.Value"/> entries
        /// in <see cref="Options"/>. May be null or empty for no default selections.
        /// Uses <see cref="IEnumerable{T}"/> to accept any collection type (arrays,
        /// lists, etc.) during deserialization while remaining read-flexible.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public IEnumerable<string> DefaultValue { get; set; }

        /// <summary>
        /// The list of selectable options available for this multi-select field.
        /// Each option has a value, display label, optional icon class, and color.
        /// Users may select zero or more options from this list.
        /// Shares the <see cref="SelectOption"/> type with <see cref="SelectField"/>.
        /// </summary>
        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; }
    }
}
