using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Defines the serialization/display format for geography field values.
    /// </summary>
    public enum GeographyFieldFormat
    {
        GeoJSON = 1,
        Text = 2
    }

    public class InputGeographyField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.GeographyField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }

        [JsonPropertyName("format")]
        public GeographyFieldFormat? Format { get; set; }

        [JsonPropertyName("srid")]
        public int SRID { get; set; } = 4326;
    }

    [Serializable]
    public class GeographyField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.GeographyField;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("visibleLineNumber")]
        public int? VisibleLineNumber { get; set; }

        [JsonPropertyName("format")]
        public GeographyFieldFormat? Format { get; set; }

        [JsonPropertyName("srid")]
        public int SRID { get; set; } = 4326;
    }
}
