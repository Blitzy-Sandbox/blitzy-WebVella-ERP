using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputHtmlField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.HtmlField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }

    [Serializable]
    public class HtmlField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.HtmlField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }
}
