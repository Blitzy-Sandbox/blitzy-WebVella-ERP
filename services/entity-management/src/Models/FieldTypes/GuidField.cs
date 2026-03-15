// =============================================================================
// GuidField.cs — GUID Field Type Model
// =============================================================================
// Migrated from monolith source:
//   - WebVella.Erp/Api/Models/FieldTypes/GuidField.cs (lines 7-46)
//   - WebVella.Erp/Database/FieldTypes/DbGuidField.cs (reference)
//
// Contains the InputGuidField (request DTO) and GuidField (persisted/returned
// model) pair for the GUID field type. Both classes include copy constructors
// that delegate to their respective base class copy constructors defined in
// Field.cs (InputField and Field).
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for AOT-safe serialization
// =============================================================================

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Input DTO for creating or updating a GUID field definition.
    /// Inherits common field metadata properties (Id, Name, Label, Required, etc.)
    /// from <see cref="InputField"/> and adds GUID-specific configuration:
    /// an optional default value and a flag to auto-generate new GUIDs.
    /// Migrated from: WebVella.Erp.Api.Models.InputGuidField (GuidField.cs lines 7-25)
    /// </summary>
    public class InputGuidField : InputField
    {
        /// <summary>
        /// Returns the discriminator value identifying this field as a GUID field type.
        /// Used during polymorphic deserialization to resolve the concrete InputField subclass.
        /// Always returns <see cref="Models.FieldType.GuidField"/> (enum value 16).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.GuidField;

        /// <summary>
        /// Optional default GUID value assigned to new records when no explicit value
        /// is provided. When null, the field has no static default. When combined with
        /// <see cref="GenerateNewId"/> = true, the GenerateNewId behavior takes precedence
        /// (a new GUID is generated instead of using this value), unless the field name
        /// is "id" in which case the default value logic is bypassed entirely.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public Guid? DefaultValue { get; set; }

        /// <summary>
        /// When true, a new <see cref="Guid"/> is automatically generated for each new
        /// record. This is the standard pattern for secondary GUID fields that need unique
        /// identifiers without relying on the primary "id" field. When false or null,
        /// the <see cref="DefaultValue"/> is used if set, otherwise the field is left empty.
        /// </summary>
        [JsonPropertyName("generateNewId")]
        public bool? GenerateNewId { get; set; }

        /// <summary>
        /// Default parameterless constructor. Initializes the base <see cref="InputField"/>
        /// which sets <see cref="InputField.Permissions"/> to a new empty
        /// <see cref="FieldPermissions"/> instance.
        /// </summary>
        public InputGuidField()
        {
        }

        /// <summary>
        /// Copy constructor that creates an <see cref="InputGuidField"/> from any
        /// <see cref="InputField"/> base instance. Delegates to
        /// <see cref="InputField(InputField)"/> to copy all common field properties
        /// (Id, Name, Label, Required, Unique, Searchable, Auditable, System,
        /// Permissions, EnableSecurity). GUID-specific properties (DefaultValue,
        /// GenerateNewId) are left at their default values and must be set separately.
        /// </summary>
        /// <param name="field">The source InputField whose base properties are copied.</param>
        public InputGuidField(InputField field) : base(field)
        {
        }
    }

    /// <summary>
    /// Persisted and returned model for a GUID field definition. Represents a fully
    /// materialized GUID field with all metadata properties resolved (non-nullable
    /// base properties from <see cref="Field"/>). Inherits Id, Name, Label, Required,
    /// Unique, Searchable, Auditable, System, Permissions, EnableSecurity, and
    /// EntityName from the base class.
    ///
    /// The <see cref="Field.GetFieldDefaultValue"/> method in the base class contains
    /// GUID-specific logic: if the field name is "id", it returns null (the record ID
    /// is handled separately); if <see cref="GenerateNewId"/> is true, it returns a
    /// new <see cref="Guid"/>; otherwise it returns <see cref="DefaultValue"/>.
    ///
    /// Migrated from: WebVella.Erp.Api.Models.GuidField (GuidField.cs lines 27-46)
    /// </summary>
    [Serializable]
    public class GuidField : Field
    {
        /// <summary>
        /// Returns the discriminator value identifying this field as a GUID field type.
        /// Used during polymorphic serialization and runtime type resolution via
        /// <see cref="Field.GetFieldType"/>. Always returns
        /// <see cref="Models.FieldType.GuidField"/> (enum value 16).
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType => FieldType.GuidField;

        /// <summary>
        /// Optional default GUID value assigned to new records when no explicit value
        /// is provided and <see cref="GenerateNewId"/> is not enabled. When null,
        /// the field has no static default value. See <see cref="Field.GetFieldDefaultValue"/>
        /// for the complete default value resolution logic.
        /// </summary>
        [JsonPropertyName("defaultValue")]
        public Guid? DefaultValue { get; set; }

        /// <summary>
        /// When true, a new <see cref="Guid"/> is automatically generated for each new
        /// record during the default value resolution phase. This flag is checked by
        /// <see cref="Field.GetFieldDefaultValue"/> and takes precedence over
        /// <see cref="DefaultValue"/> when both are set.
        /// </summary>
        [JsonPropertyName("generateNewId")]
        public bool? GenerateNewId { get; set; }

        /// <summary>
        /// Default parameterless constructor. Initializes the base <see cref="Field"/>
        /// which sets all boolean properties to false and creates an empty
        /// <see cref="FieldPermissions"/> instance.
        /// </summary>
        public GuidField()
        {
        }

        /// <summary>
        /// Copy constructor that creates a <see cref="GuidField"/> from any
        /// <see cref="Field"/> base instance. Delegates to <see cref="Field(Field)"/>
        /// which first calls the default constructor (via <c>: this()</c>) to initialize
        /// defaults, then copies all common field properties (Id, Name, Label, Required,
        /// Unique, Searchable, Auditable, System, Permissions, EnableSecurity, EntityName).
        /// GUID-specific properties (DefaultValue, GenerateNewId) are left at their default
        /// values and must be set separately.
        /// </summary>
        /// <param name="field">The source Field whose base properties are copied.</param>
        public GuidField(Field field) : base(field)
        {
        }
    }
}
