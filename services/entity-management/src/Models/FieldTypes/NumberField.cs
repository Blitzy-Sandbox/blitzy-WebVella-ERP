using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputNumberField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.NumberField;

        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        [JsonPropertyName("decimalPlaces")]
        public byte? DecimalPlaces { get; set; }
    }

    [Serializable]
    public class NumberField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.NumberField;

        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        [JsonPropertyName("decimalPlaces")]
        public byte? DecimalPlaces { get; set; }
    }
}
