using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputEmailField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.EmailField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }

    [Serializable]
    public class EmailField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.EmailField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }
}
