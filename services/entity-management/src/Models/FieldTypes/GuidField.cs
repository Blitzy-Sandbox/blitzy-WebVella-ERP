using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    public class InputGuidField : InputField
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.GuidField;

        [JsonPropertyName("defaultValue")]
        public Guid? DefaultValue { get; set; }

        [JsonPropertyName("generateNewId")]
        public bool? GenerateNewId { get; set; }

        /// <summary>
        /// Default parameterless constructor.
        /// </summary>
        public InputGuidField() { }

        /// <summary>
        /// Copy constructor that initializes an InputGuidField from any InputField base.
        /// </summary>
        public InputGuidField(InputField field) : base(field)
        {
        }
    }

    [Serializable]
    public class GuidField : Field
    {
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.GuidField;

        [JsonPropertyName("defaultValue")]
        public Guid? DefaultValue { get; set; }

        [JsonPropertyName("generateNewId")]
        public bool? GenerateNewId { get; set; }

        /// <summary>
        /// Default parameterless constructor.
        /// </summary>
        public GuidField() { }

        /// <summary>
        /// Copy constructor that initializes a GuidField from any Field base.
        /// </summary>
        public GuidField(Field field) : base(field)
        {
        }
    }
}
