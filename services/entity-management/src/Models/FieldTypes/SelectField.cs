using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Represents a single option within a SelectField or MultiSelectField dropdown.
    /// </summary>
    [Serializable]
    public class SelectOption
    {
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("icon_class")]
        public string? IconClass { get; set; }

        [JsonPropertyName("color")]
        public string? Color { get; set; }

        /// <summary>
        /// Parameterless constructor for deserialization.
        /// </summary>
        public SelectOption() { }

        /// <summary>
        /// Constructs a SelectOption with value and label.
        /// </summary>
        public SelectOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>
        /// Constructs a SelectOption with all properties.
        /// </summary>
        public SelectOption(string value, string label, string? iconClass, string? color)
        {
            Value = value;
            Label = label;
            IconClass = iconClass;
            Color = color;
        }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        public SelectOption(SelectOption source)
        {
            Value = source.Value;
            Label = source.Label;
            IconClass = source.IconClass;
            Color = source.Color;
        }
    }

    public class InputSelectField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.SelectField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; } = new();
    }

    [Serializable]
    public class SelectField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.SelectField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; } = new();
    }
}
