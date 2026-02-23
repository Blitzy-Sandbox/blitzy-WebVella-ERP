using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputCheckboxField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.CheckboxField;

        [JsonPropertyName("defaultValue")]
        public bool? DefaultValue { get; set; }
    }

    [Serializable]
    public class CheckboxField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.CheckboxField;

        [JsonPropertyName("defaultValue")]
        public bool? DefaultValue { get; set; }
    }
}
