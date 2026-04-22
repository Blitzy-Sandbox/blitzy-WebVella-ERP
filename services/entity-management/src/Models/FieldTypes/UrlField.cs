using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputUrlField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.UrlField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("openTargetInNewWindow")]
        public bool? OpenTargetInNewWindow { get; set; }
    }

    [Serializable]
    public class UrlField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.UrlField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("openTargetInNewWindow")]
        public bool? OpenTargetInNewWindow { get; set; }
    }
}
