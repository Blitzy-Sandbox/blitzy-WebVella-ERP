using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputPhoneField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PhoneField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }

    [Serializable]
    public class PhoneField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PhoneField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }
    }
}
