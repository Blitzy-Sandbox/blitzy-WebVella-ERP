using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputMultiLineTextField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiLineTextField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }
    }

    [Serializable]
    public class MultiLineTextField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.MultiLineTextField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }
    }
}
