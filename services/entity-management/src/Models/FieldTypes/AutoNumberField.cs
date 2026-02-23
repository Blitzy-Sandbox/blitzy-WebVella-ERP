using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputAutoNumberField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.AutoNumberField;

        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        [JsonPropertyName("displayFormat")]
        public string? DisplayFormat { get; set; }

        [JsonPropertyName("startingNumber")]
        public decimal? StartingNumber { get; set; }
    }

    [Serializable]
    public class AutoNumberField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.AutoNumberField;

        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        [JsonPropertyName("displayFormat")]
        public string? DisplayFormat { get; set; }

        [JsonPropertyName("startingNumber")]
        public decimal? StartingNumber { get; set; }
    }
}
