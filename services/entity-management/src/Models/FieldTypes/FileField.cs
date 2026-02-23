using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputFileField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.FileField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }

    [Serializable]
    public class FileField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.FileField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }
}
