using System;
using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Request DTO for creating or updating an image field definition.
    /// Inherits common field metadata from <see cref="InputField"/> and adds
    /// image-specific configuration (default image URL/path).
    /// Migrated from monolith WebVella.Erp.Api.Models.InputImageField with
    /// Newtonsoft.Json replaced by System.Text.Json for Native AOT compatibility.
    /// </summary>
    public class InputImageField : InputField
    {
        /// <summary>
        /// Discriminator property identifying this field as an image field type.
        /// Used during polymorphic deserialization to route to the correct concrete type.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.ImageField;

        /// <summary>
        /// The default image URL or path assigned to new records when no explicit
        /// value is provided. May be null when no default image is configured.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }

    /// <summary>
    /// Persisted and returned model representing an image field definition.
    /// Inherits common field metadata from <see cref="Field"/> and adds
    /// image-specific configuration (default image URL/path).
    /// Marked <c>[Serializable]</c> for CLR serialization compatibility,
    /// preserving the pattern from the original monolith.
    /// Migrated from monolith WebVella.Erp.Api.Models.ImageField with
    /// Newtonsoft.Json replaced by System.Text.Json for Native AOT compatibility.
    /// </summary>
    [Serializable]
    public class ImageField : Field
    {
        /// <summary>
        /// Discriminator property identifying this field as an image field type.
        /// Used during polymorphic deserialization to route to the correct concrete type.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.ImageField;

        /// <summary>
        /// The default image URL or path assigned to new records when no explicit
        /// value is provided. May be null when no default image is configured.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }
    }
}
