using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputMultiSelectField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiSelectField;

        [JsonPropertyName("defaultValue")]
        public IEnumerable<string>? DefaultValue { get; set; }

        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; } = new();
    }

    [Serializable]
    public class MultiSelectField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiSelectField;

        [JsonPropertyName("defaultValue")]
        public IEnumerable<string>? DefaultValue { get; set; }

        [JsonPropertyName("options")]
        public List<SelectOption> Options { get; set; } = new();
    }
}
