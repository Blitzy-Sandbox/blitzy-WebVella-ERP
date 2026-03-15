// =============================================================================
// SelectField.cs — Select Field Type Model with SelectOption DTO
// =============================================================================
// Migrates the select field type from the monolith source files:
//   - WebVella.Erp/Api/Models/FieldTypes/SelectField.cs (InputSelectField,
//     SelectField, SelectOption classes)
//   - WebVella.Erp/Database/FieldTypes/DbSelectField.cs (DB layer reference)
//
// Contains three classes:
//   1. InputSelectField : InputField  — request DTO for field creation/update
//   2. SelectField : Field            — persisted/returned field model
//   3. SelectOption                   — shared option DTO used by both
//                                       SelectField and MultiSelectField
//
// CRITICAL: SelectOption is a shared dependency consumed by MultiSelectField.cs
// in the same namespace, so no additional imports are needed by consumers.
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
//   InputSelectField / SelectField: "fieldType", "defaultValue", "options"
//   SelectOption: "value", "label", "icon_class" (snake_case!), "color"
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input DTO for creating or updating a select field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="InputField"/> and adds select-specific properties: DefaultValue
    /// and Options.
    /// Migrated from: WebVella.Erp.Api.Models.InputSelectField
    /// (SelectField.cs lines 8-18)
    /// </summary>
    public class InputSelectField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this as a SelectField type.
        /// Returns <see cref="FieldType.SelectField"/> (value 17).
        /// Static so the value is available without instantiation for type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.SelectField;

        /// <summary>
        /// The default selected value for new records using this field.
        /// Should match one of the <see cref="SelectOption.Value"/> entries in
        /// <see cref="Options"/>, or be empty/null for no default selection.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = string.Empty;

        /// <summary>
        /// The list of selectable options available for this field.
        /// Each option has a value, display label, optional icon class, and color.
        /// </summary>
        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; } = new();
    }

    /// <summary>
    /// Persisted/returned model for a select field definition.
    /// Inherits common field properties (Id, Name, Label, Required, etc.) from
    /// <see cref="Field"/> and adds select-specific properties: DefaultValue
    /// and Options. Marked [Serializable] for CLR serialization compatibility
    /// matching the original monolith pattern.
    /// Migrated from: WebVella.Erp.Api.Models.SelectField
    /// (SelectField.cs lines 20-31)
    /// </summary>
    [Serializable]
    public class SelectField : Field
    {
        /// <summary>
        /// Discriminator property identifying this as a SelectField type.
        /// Returns <see cref="FieldType.SelectField"/> (value 17).
        /// Static so the value is available without instantiation for type dispatch.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.SelectField;

        /// <summary>
        /// The default selected value for new records using this field.
        /// Should match one of the <see cref="SelectOption.Value"/> entries in
        /// <see cref="Options"/>, or be empty/null for no default selection.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; } = string.Empty;

        /// <summary>
        /// The list of selectable options available for this field.
        /// Each option has a value, display label, optional icon class, and color.
        /// </summary>
        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; } = new();
    }

    /// <summary>
    /// Represents a single selectable option within a SelectField or MultiSelectField.
    /// This is a SHARED DTO class — it is used by both SelectField.Options and
    /// MultiSelectField.Options properties. All consumers are in the same namespace
    /// (WebVellaErp.EntityManagement.Models), so no cross-file imports are needed.
    ///
    /// CRITICAL: The JSON property name for IconClass is "icon_class" (snake_case),
    /// NOT camelCase. This must be preserved exactly for backward compatibility
    /// with existing serialized data and API contracts.
    ///
    /// Migrated from: WebVella.Erp.Api.Models.SelectOption
    /// (SelectField.cs lines 33-70)
    /// </summary>
    [Serializable]
    public class SelectOption
    {
        /// <summary>
        /// The internal value of this option, used for storage and comparison.
        /// Defaults to empty string for safe initialization.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = "";

        /// <summary>
        /// The human-readable display label for this option.
        /// Shown in UI dropdowns and selection controls.
        /// Defaults to empty string for safe initialization.
        /// </summary>
        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>
        /// CSS icon class for visual representation of this option (e.g., Font Awesome class).
        /// Defaults to empty string for safe initialization.
        /// CRITICAL: JSON key is "icon_class" (snake_case) — NOT "iconClass" (camelCase).
        /// </summary>
        [JsonPropertyName("icon_class")]
        public string IconClass { get; set; } = "";

        /// <summary>
        /// Color code (hex or named) for visual styling of this option.
        /// Defaults to empty string for safe initialization.
        /// </summary>
        [JsonPropertyName("color")]
        public string Color { get; set; } = "";

        /// <summary>
        /// Parameterless constructor required for JSON deserialization.
        /// All properties default to empty string via property initializers.
        /// </summary>
        public SelectOption()
        {
        }

        /// <summary>
        /// Constructs a SelectOption with the specified value and label.
        /// IconClass and Color retain their default empty string values.
        /// </summary>
        /// <param name="value">The internal value of this option.</param>
        /// <param name="label">The human-readable display label.</param>
        public SelectOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>
        /// Constructs a SelectOption with all four properties specified.
        /// </summary>
        /// <param name="value">The internal value of this option.</param>
        /// <param name="label">The human-readable display label.</param>
        /// <param name="iconClass">CSS icon class for visual representation.</param>
        /// <param name="color">Color code for visual styling.</param>
        public SelectOption(string value, string label, string iconClass, string color)
        {
            Value = value;
            Label = label;
            IconClass = iconClass;
            Color = color;
        }

        /// <summary>
        /// Copy constructor — creates a shallow copy of the given SelectOption.
        /// Chains to the 2-argument constructor, copying Value and Label.
        /// IconClass and Color retain their default empty string values (matching
        /// the original monolith behavior where the copy constructor only copies
        /// Value and Label).
        /// </summary>
        /// <param name="option">The source SelectOption to copy from.</param>
        public SelectOption(SelectOption option) : this(option.Value, option.Label)
        {
        }
    }
}
