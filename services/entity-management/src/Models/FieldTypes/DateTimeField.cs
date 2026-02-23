using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputDateTimeField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.DateTimeField;

        [JsonPropertyName("defaultValue")]
        public DateTime? DefaultValue { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("useCurrentTimeAsDefaultValue")]
        public bool? UseCurrentTimeAsDefaultValue { get; set; }
    }

    [Serializable]
    public class DateTimeField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.DateTimeField;

        [JsonPropertyName("defaultValue")]
        public DateTime? DefaultValue { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("useCurrentTimeAsDefaultValue")]
        public bool? UseCurrentTimeAsDefaultValue { get; set; }
    }
}
