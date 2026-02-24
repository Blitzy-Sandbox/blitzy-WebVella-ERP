using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Request DTO for creating or updating a password field definition.
    /// Inherits common field metadata from <see cref="InputField"/>.
    /// Password fields intentionally omit a DefaultValue property — password defaults are always null.
    /// Migrated from monolith WebVella.Erp/Api/Models/FieldTypes/PasswordField.cs (InputPasswordField).
    /// </summary>
    public class InputPasswordField : InputField
    {
        /// <summary>
        /// Gets the field type discriminator for password fields.
        /// Always returns <see cref="Models.FieldType.PasswordField"/>.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PasswordField;

        /// <summary>
        /// Gets or sets the maximum allowed character length for the password value.
        /// Null indicates no maximum length constraint.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        /// <summary>
        /// Gets or sets the minimum required character length for the password value.
        /// Null indicates no minimum length constraint.
        /// </summary>
        [JsonPropertyName("minLength")]
        public int? MinLength { get; set; }

        /// <summary>
        /// Gets or sets whether the password value should be stored in encrypted form.
        /// When true, password values are encrypted before persistence.
        /// </summary>
        [JsonPropertyName("encrypted")]
        public bool? Encrypted { get; set; }
    }

    /// <summary>
    /// Persisted and returned model representing a password field definition.
    /// Inherits common field metadata from <see cref="Field"/>.
    /// Password fields intentionally omit a DefaultValue property — password defaults are always null.
    /// Migrated from monolith WebVella.Erp/Api/Models/FieldTypes/PasswordField.cs (PasswordField).
    /// </summary>
    [Serializable]
    public class PasswordField : Field
    {
        /// <summary>
        /// Gets the field type discriminator for password fields.
        /// Always returns <see cref="Models.FieldType.PasswordField"/>.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.PasswordField;

        /// <summary>
        /// Gets or sets the maximum allowed character length for the password value.
        /// Null indicates no maximum length constraint.
        /// </summary>
        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        /// <summary>
        /// Gets or sets the minimum required character length for the password value.
        /// Null indicates no minimum length constraint.
        /// </summary>
        [JsonPropertyName("minLength")]
        public int? MinLength { get; set; }

        /// <summary>
        /// Gets or sets whether the password value should be stored in encrypted form.
        /// When true, password values are encrypted before persistence.
        /// </summary>
        [JsonPropertyName("encrypted")]
        public bool? Encrypted { get; set; }
    }
}
