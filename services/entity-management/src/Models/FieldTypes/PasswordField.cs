using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputPasswordField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PasswordField;

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("minLength")]
        public int? MinLength { get; set; }

        [JsonPropertyName("encrypted")]
        public bool? Encrypted { get; set; }
    }

    [Serializable]
    public class PasswordField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PasswordField;

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("minLength")]
        public int? MinLength { get; set; }

        [JsonPropertyName("encrypted")]
        public bool? Encrypted { get; set; }
    }
}
