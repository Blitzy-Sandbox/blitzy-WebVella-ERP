using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputCurrencyField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.CurrencyField;

        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        [JsonPropertyName("currency")]
        public CurrencyType Currency { get; set; } = new CurrencyType();
    }

    [Serializable]
    public class CurrencyField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.CurrencyField;

        [JsonPropertyName("defaultValue")]
        public decimal? DefaultValue { get; set; }

        [JsonPropertyName("minValue")]
        public decimal? MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public decimal? MaxValue { get; set; }

        [JsonPropertyName("currency")]
        public CurrencyType Currency { get; set; } = new CurrencyType();
    }
}
