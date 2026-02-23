using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputTextField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.TextField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }

    [Serializable]
    public class TextField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.TextField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }
}
