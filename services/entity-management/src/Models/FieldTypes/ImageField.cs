using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputImageField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.ImageField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }

    [Serializable]
    public class ImageField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.ImageField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }
}
