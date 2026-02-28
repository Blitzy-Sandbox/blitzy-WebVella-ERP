// =============================================================================
// Field.cs — Base Field Abstractions and Polymorphic Dispatch
// =============================================================================
// Consolidates three monolith source files into one:
//   - WebVella.Erp/Api/Models/FieldTypes/BaseField.cs (InputField + Field)
//   - WebVella.Erp/Api/Models/FieldTypes/FieldType.cs (FieldType enum)
//   - WebVella.Erp/Api/Models/FieldTypes/RelationFieldMeta.cs (RelationFieldMeta)
//
// This file defines the FieldType enum, abstract InputField/Field classes,
// response wrappers (FieldResponse, FieldListResponse), FieldPermissions,
// and RelationFieldMeta. These are the foundation for all 20+ concrete field
// type models in the FieldTypes/ subfolder.
//
// Namespace Migration:
//   Old: WebVella.Erp (InputField) + WebVella.Erp.Api.Models (Field subtypes)
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] for ALL property annotations
//   Exception: InputField.ConvertField(JObject) retains Newtonsoft.Json.Linq for
//              polymorphic field type deserialization requiring JObject.
// =============================================================================

using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;

namespace WebVellaErp.EntityManagement.Models
{
    /// <summary>
    /// Enumerates all supported field types in the entity management system.
    /// Each value is annotated with a SelectOption label for UI display and serialization.
    /// Values 1–21 are explicit to maintain backward compatibility with persisted metadata.
    /// Migrated from: WebVella.Erp.Api.Models.FieldType (FieldType.cs lines 5-49)
    /// </summary>
    public enum FieldType
    {
        [SelectOption(Label = "autonumber")]
        AutoNumberField = 1,

        [SelectOption(Label = "checkbox")]
        CheckboxField = 2,

        [SelectOption(Label = "currency")]
        CurrencyField = 3,

        [SelectOption(Label = "date")]
        DateField = 4,

        [SelectOption(Label = "datetime")]
        DateTimeField = 5,

        [SelectOption(Label = "email")]
        EmailField = 6,

        [SelectOption(Label = "file")]
        FileField = 7,

        [SelectOption(Label = "html")]
        HtmlField = 8,

        [SelectOption(Label = "image")]
        ImageField = 9,

        [SelectOption(Label = "multilinetext")]
        MultiLineTextField = 10,

        [SelectOption(Label = "multiselect")]
        MultiSelectField = 11,

        [SelectOption(Label = "number")]
        NumberField = 12,

        [SelectOption(Label = "password")]
        PasswordField = 13,

        [SelectOption(Label = "percent")]
        PercentField = 14,

        [SelectOption(Label = "phone")]
        PhoneField = 15,

        [SelectOption(Label = "guid")]
        GuidField = 16,

        [SelectOption(Label = "select")]
        SelectField = 17,

        [SelectOption(Label = "text")]
        TextField = 18,

        [SelectOption(Label = "url")]
        UrlField = 19,

        [SelectOption(Label = "relation")]
        RelationField = 20,

        [SelectOption(Label = "geography")]
        GeographyField = 21
    }

    /// <summary>
    /// Abstract base class for field creation and update input payloads.
    /// Properties are nullable to support partial updates where only changed values are provided.
    /// All concrete InputXField subclasses (InputAutoNumberField, InputCheckboxField, etc.)
    /// inherit from this class and add type-specific properties such as DefaultValue,
    /// MaxLength, MinValue, etc.
    /// Migrated from: WebVella.Erp.InputField (BaseField.cs lines 10-71)
    /// </summary>
    public abstract class InputField
    {
        [JsonPropertyName("id")]
        public Guid? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("label")]
        public string? Label { get; set; }

        [JsonPropertyName("placeholderText")]
        public string? PlaceholderText { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("helpText")]
        public string? HelpText { get; set; }

        [JsonPropertyName("required")]
        public bool? Required { get; set; }

        [JsonPropertyName("unique")]
        public bool? Unique { get; set; }

        [JsonPropertyName("searchable")]
        public bool? Searchable { get; set; }

        [JsonPropertyName("auditable")]
        public bool? Auditable { get; set; }

        [JsonPropertyName("system")]
        public bool? System { get; set; }

        [JsonPropertyName("permissions")]
        public FieldPermissions Permissions { get; set; }

        [JsonPropertyName("enableSecurity")]
        public bool EnableSecurity { get; set; }

        /// <summary>
        /// Default constructor initializes permissions to an empty set.
        /// </summary>
        public InputField()
        {
            Permissions = new FieldPermissions();
        }

        /// <summary>
        /// Copy constructor for creating a deep copy of an InputField instance.
        /// Copies all base properties from the source field.
        /// </summary>
        /// <param name="field">The source InputField to copy properties from.</param>
        public InputField(InputField field)
        {
            Id = field.Id;
            Name = field.Name;
            Label = field.Label;
            PlaceholderText = field.PlaceholderText;
            Description = field.Description;
            HelpText = field.HelpText;
            Required = field.Required;
            Unique = field.Unique;
            Searchable = field.Searchable;
            Auditable = field.Auditable;
            System = field.System;
            Permissions = field.Permissions;
            EnableSecurity = field.EnableSecurity;
        }

        /// <summary>
        /// Polymorphic deserialization: converts a JObject to the appropriate concrete InputField
        /// subclass based on the fieldType property value. Uses Newtonsoft.Json JObject for
        /// runtime type dispatch because System.Text.Json does not support polymorphic
        /// deserialization with runtime type resolution from a discriminator property value.
        /// Migrated from: BaseField.cs lines 73-149
        /// </summary>
        /// <param name="inputField">A JObject containing field properties including a fieldType discriminator.</param>
        /// <returns>The concrete InputField subclass instance matching the fieldType value.</returns>
        /// <exception cref="Exception">Thrown when fieldType property is missing or has an invalid value.</exception>
        public static InputField ConvertField(JObject inputField)
        {
            var fieldTypeProp = inputField.Properties().SingleOrDefault(k => k.Name.ToLower() == "fieldtype");
            if (fieldTypeProp == null)
                throw new Exception("fieldType is required");

            FieldType fieldType = (FieldType)Enum.ToObject(typeof(FieldType), fieldTypeProp.Value.ToObject<int>());

            switch (fieldType)
            {
                case FieldType.AutoNumberField:
                    return inputField.ToObject<InputAutoNumberField>()!;
                case FieldType.CheckboxField:
                    return inputField.ToObject<InputCheckboxField>()!;
                case FieldType.CurrencyField:
                    return inputField.ToObject<InputCurrencyField>()!;
                case FieldType.DateField:
                    return inputField.ToObject<InputDateField>()!;
                case FieldType.DateTimeField:
                    return inputField.ToObject<InputDateTimeField>()!;
                case FieldType.EmailField:
                    return inputField.ToObject<InputEmailField>()!;
                case FieldType.FileField:
                    return inputField.ToObject<InputFileField>()!;
                case FieldType.HtmlField:
                    return inputField.ToObject<InputHtmlField>()!;
                case FieldType.ImageField:
                    return inputField.ToObject<InputImageField>()!;
                case FieldType.MultiLineTextField:
                    return inputField.ToObject<InputMultiLineTextField>()!;
                case FieldType.MultiSelectField:
                    return inputField.ToObject<InputMultiSelectField>()!;
                case FieldType.NumberField:
                    return inputField.ToObject<InputNumberField>()!;
                case FieldType.PasswordField:
                    return inputField.ToObject<InputPasswordField>()!;
                case FieldType.PercentField:
                    return inputField.ToObject<InputPercentField>()!;
                case FieldType.PhoneField:
                    return inputField.ToObject<InputPhoneField>()!;
                case FieldType.GuidField:
                    return inputField.ToObject<InputGuidField>()!;
                case FieldType.SelectField:
                    return inputField.ToObject<InputSelectField>()!;
                case FieldType.TextField:
                    return inputField.ToObject<InputTextField>()!;
                case FieldType.UrlField:
                    return inputField.ToObject<InputUrlField>()!;
                case FieldType.GeographyField:
                    return inputField.ToObject<InputGeographyField>()!;
                default:
                    throw new Exception("Invalid FieldType");
            }
        }

        /// <summary>
        /// Maps a FieldType enum value to the corresponding CLR Type of the concrete InputField subclass.
        /// Used for reflection-based field creation and validation scenarios.
        /// Note: GeographyField maps to typeof(GeographyField) per source behavior.
        /// Migrated from: BaseField.cs lines 152-223
        /// </summary>
        /// <param name="fieldType">The FieldType enum value to resolve.</param>
        /// <returns>The CLR Type of the concrete InputField or Field subclass.</returns>
        public static Type GetFieldType(FieldType fieldType)
        {
            switch (fieldType)
            {
                case FieldType.AutoNumberField:
                    return typeof(InputAutoNumberField);
                case FieldType.CheckboxField:
                    return typeof(InputCheckboxField);
                case FieldType.CurrencyField:
                    return typeof(InputCurrencyField);
                case FieldType.DateField:
                    return typeof(InputDateField);
                case FieldType.DateTimeField:
                    return typeof(InputDateTimeField);
                case FieldType.EmailField:
                    return typeof(InputEmailField);
                case FieldType.FileField:
                    return typeof(InputFileField);
                case FieldType.HtmlField:
                    return typeof(InputHtmlField);
                case FieldType.ImageField:
                    return typeof(InputImageField);
                case FieldType.MultiLineTextField:
                    return typeof(InputMultiLineTextField);
                case FieldType.MultiSelectField:
                    return typeof(InputMultiSelectField);
                case FieldType.NumberField:
                    return typeof(InputNumberField);
                case FieldType.PasswordField:
                    return typeof(InputPasswordField);
                case FieldType.PercentField:
                    return typeof(InputPercentField);
                case FieldType.PhoneField:
                    return typeof(InputPhoneField);
                case FieldType.GuidField:
                    return typeof(InputGuidField);
                case FieldType.SelectField:
                    return typeof(InputSelectField);
                case FieldType.TextField:
                    return typeof(InputTextField);
                case FieldType.UrlField:
                    return typeof(InputUrlField);
                case FieldType.GeographyField:
                    return typeof(GeographyField);
                default:
                    return typeof(InputGuidField);
            }
        }
    }

    /// <summary>
    /// Abstract base class for persisted/returned field definitions.
    /// Properties are non-nullable (represent fully materialized field metadata).
    /// All concrete field subclasses (AutoNumberField, CheckboxField, etc.) inherit
    /// from this class and add type-specific properties.
    /// Migrated from: WebVella.Erp.Api.Models.Field (BaseField.cs lines 226-412)
    /// </summary>
    [Serializable]
    public abstract class Field
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string Label { get; set; } = string.Empty;

        [JsonPropertyName("placeholderText")]
        public string PlaceholderText { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("helpText")]
        public string HelpText { get; set; } = string.Empty;

        [JsonPropertyName("required")]
        public bool Required { get; set; }

        [JsonPropertyName("unique")]
        public bool Unique { get; set; }

        [JsonPropertyName("searchable")]
        public bool Searchable { get; set; }

        [JsonPropertyName("auditable")]
        public bool Auditable { get; set; }

        [JsonPropertyName("system")]
        public bool System { get; set; }

        [JsonPropertyName("permissions")]
        public FieldPermissions? Permissions { get; set; }

        [JsonPropertyName("enableSecurity")]
        public bool EnableSecurity { get; set; }

        [JsonPropertyName("entityName")]
        public string EntityName { get; set; } = string.Empty;

        /// <summary>
        /// Default constructor initializes all boolean properties to false and creates
        /// empty permissions. Follows source pattern of setting Permissions = null first,
        /// then immediately assigning a new FieldPermissions instance.
        /// </summary>
        public Field()
        {
            Required = false;
            Unique = false;
            Searchable = false;
            Auditable = false;
            System = false;
            EnableSecurity = false;
            Permissions = null;
            Permissions = new FieldPermissions();
        }

        /// <summary>
        /// Copy constructor creates a deep copy of the given Field. Calls the default
        /// constructor via : this() to initialize defaults first, then overwrites with
        /// the source field's property values.
        /// </summary>
        /// <param name="field">The source Field to copy properties from.</param>
        public Field(Field field) : this()
        {
            Id = field.Id;
            Name = field.Name;
            Label = field.Label;
            PlaceholderText = field.PlaceholderText;
            Description = field.Description;
            HelpText = field.HelpText;
            Required = field.Required;
            Unique = field.Unique;
            Searchable = field.Searchable;
            Auditable = field.Auditable;
            System = field.System;
            Permissions = field.Permissions;
            EnableSecurity = field.EnableSecurity;
            EntityName = field.EntityName;
        }

        /// <summary>
        /// Returns the appropriate default value for this field based on its concrete type.
        /// Handles type-specific logic: DateField/DateTimeField check UseCurrentTimeAsDefaultValue,
        /// GuidField checks Name=="id" and GenerateNewId, PasswordField always returns null.
        /// Migrated from: BaseField.cs lines 301-366
        /// </summary>
        /// <returns>The default value for this field, or null for types without defaults.</returns>
        public object? GetFieldDefaultValue()
        {
            FieldType fieldType = GetFieldType();

            switch (fieldType)
            {
                case FieldType.AutoNumberField:
                    return ((AutoNumberField)this).DefaultValue;
                case FieldType.CheckboxField:
                    return ((CheckboxField)this).DefaultValue;
                case FieldType.CurrencyField:
                    return ((CurrencyField)this).DefaultValue;
                case FieldType.DateField:
                    {
                        var dateField = (DateField)this;
                        if (dateField.UseCurrentTimeAsDefaultValue.HasValue &&
                            dateField.UseCurrentTimeAsDefaultValue.Value)
                            return DateTime.Now.Date;
                        return dateField.DefaultValue;
                    }
                case FieldType.DateTimeField:
                    {
                        var dateTimeField = (DateTimeField)this;
                        if (dateTimeField.UseCurrentTimeAsDefaultValue.HasValue &&
                            dateTimeField.UseCurrentTimeAsDefaultValue.Value)
                            return DateTime.Now;
                        return dateTimeField.DefaultValue;
                    }
                case FieldType.EmailField:
                    return ((EmailField)this).DefaultValue;
                case FieldType.FileField:
                    return ((FileField)this).DefaultValue;
                case FieldType.ImageField:
                    return ((ImageField)this).DefaultValue;
                case FieldType.GeographyField:
                    return ((GeographyField)this).DefaultValue;
                case FieldType.HtmlField:
                    return ((HtmlField)this).DefaultValue;
                case FieldType.MultiLineTextField:
                    return ((MultiLineTextField)this).DefaultValue;
                case FieldType.MultiSelectField:
                    return ((MultiSelectField)this).DefaultValue;
                case FieldType.NumberField:
                    return ((NumberField)this).DefaultValue;
                case FieldType.PercentField:
                    return ((PercentField)this).DefaultValue;
                case FieldType.PhoneField:
                    return ((PhoneField)this).DefaultValue;
                case FieldType.SelectField:
                    return ((SelectField)this).DefaultValue;
                case FieldType.TextField:
                    return ((TextField)this).DefaultValue;
                case FieldType.UrlField:
                    return ((UrlField)this).DefaultValue;
                case FieldType.PasswordField:
                    return null;
                case FieldType.GuidField:
                    {
                        var guidField = (GuidField)this;
                        if (Name == "id")
                            return null;
                        if (guidField.GenerateNewId.HasValue && guidField.GenerateNewId.Value)
                            return Guid.NewGuid();
                        return guidField.DefaultValue;
                    }
                default:
                    return null;
            }
        }

        /// <summary>
        /// Determines the FieldType enum value for this field based on its concrete runtime type.
        /// Uses type checking (is pattern) to resolve the runtime subclass to the enum.
        /// Default return is FieldType.GuidField if no matching subclass is found.
        /// Migrated from: BaseField.cs lines 368-412
        /// </summary>
        /// <returns>The FieldType enum value corresponding to this field's concrete type.</returns>
        public virtual FieldType GetFieldType()
        {
            if (this is AutoNumberField)
                return FieldType.AutoNumberField;
            if (this is CheckboxField)
                return FieldType.CheckboxField;
            if (this is CurrencyField)
                return FieldType.CurrencyField;
            if (this is DateField)
                return FieldType.DateField;
            if (this is DateTimeField)
                return FieldType.DateTimeField;
            if (this is EmailField)
                return FieldType.EmailField;
            if (this is FileField)
                return FieldType.FileField;
            if (this is HtmlField)
                return FieldType.HtmlField;
            if (this is ImageField)
                return FieldType.ImageField;
            if (this is MultiLineTextField)
                return FieldType.MultiLineTextField;
            if (this is MultiSelectField)
                return FieldType.MultiSelectField;
            if (this is NumberField)
                return FieldType.NumberField;
            if (this is PasswordField)
                return FieldType.PasswordField;
            if (this is PercentField)
                return FieldType.PercentField;
            if (this is PhoneField)
                return FieldType.PhoneField;
            if (this is GuidField)
                return FieldType.GuidField;
            if (this is SelectField)
                return FieldType.SelectField;
            if (this is TextField)
                return FieldType.TextField;
            if (this is UrlField)
                return FieldType.UrlField;
            if (this is GeographyField)
                return FieldType.GeographyField;

            return FieldType.GuidField;
        }
    }

    /// <summary>
    /// Container for a list of field definitions. Used as the Object payload
    /// in FieldListResponse for multi-field API responses.
    /// Migrated from: BaseField.cs lines 415-425
    /// </summary>
    [Serializable]
    public class FieldList
    {
        [JsonPropertyName("fields")]
        public List<Field> Fields { get; set; } = new List<Field>();
    }

    /// <summary>
    /// API response envelope for a single field definition.
    /// Inherits standard response metadata from BaseResponseModel.
    /// Migrated from: BaseField.cs lines 427-433
    /// </summary>
    [Serializable]
    public class FieldResponse : BaseResponseModel
    {
        [JsonPropertyName("object")]
        public Field? Object { get; set; }
    }

    /// <summary>
    /// API response envelope for a list of field definitions.
    /// Inherits standard response metadata from BaseResponseModel.
    /// Migrated from: BaseField.cs lines 435-439
    /// </summary>
    [Serializable]
    public class FieldListResponse : BaseResponseModel
    {
        [JsonPropertyName("object")]
        public FieldList? Object { get; set; }
    }

    /// <summary>
    /// Defines role-based read and update permissions for a field.
    /// Each list contains Guid identifiers of roles that have the
    /// corresponding permission on the field.
    /// Migrated from: BaseField.cs lines 441-455
    /// </summary>
    [Serializable]
    public class FieldPermissions
    {
        [JsonPropertyName("canRead")]
        public List<Guid> CanRead { get; set; } = new List<Guid>();

        [JsonPropertyName("canUpdate")]
        public List<Guid> CanUpdate { get; set; } = new List<Guid>();
    }

    /// <summary>
    /// Internal model representing a relation-type field with associated entity and
    /// relation metadata. Extends Field to add relation-specific properties.
    /// Used internally for building relation field metadata views that combine field
    /// definitions with the owning relation and participating entity references.
    /// The Entity-typed properties (OriginEntity, TargetEntity, Entity) are marked
    /// [JsonIgnore] and are resolved at runtime — they are not serialized.
    /// Migrated from: WebVella.Erp.Api.Models.RelationFieldMeta (RelationFieldMeta.cs lines 7-51)
    /// </summary>
    [Serializable]
    internal class RelationFieldMeta : Field
    {
        /// <summary>
        /// Static field type identifier — always returns RelationField.
        /// </summary>
        [JsonPropertyName("fieldType")]
        public static FieldType FieldType { get { return FieldType.RelationField; } }

        /// <summary>
        /// The list of fields exposed through this relation, resolved from the
        /// target (or origin) entity depending on the navigation direction.
        /// </summary>
        [JsonPropertyName("relationFields")]
        public List<Field> Fields { get; set; }

        /// <summary>
        /// The EntityRelation metadata describing the relation type, origin entity/field,
        /// and target entity/field for this relation field.
        /// </summary>
        [JsonPropertyName("relation")]
        public EntityRelation? Relation { get; set; }

        /// <summary>
        /// The origin (parent/source) entity in the relation. Not serialized.
        /// </summary>
        [JsonIgnore]
        internal Entity? OriginEntity { get; set; }

        /// <summary>
        /// The target (child/destination) entity in the relation. Not serialized.
        /// </summary>
        [JsonIgnore]
        internal Entity? TargetEntity { get; set; }

        /// <summary>
        /// The origin field participating in the relation. Not serialized.
        /// </summary>
        [JsonIgnore]
        internal Field? OriginField { get; set; }

        /// <summary>
        /// The target field participating in the relation. Not serialized.
        /// </summary>
        [JsonIgnore]
        internal Field? TargetField { get; set; }

        /// <summary>
        /// The entity context from which this relation field is being viewed. Not serialized.
        /// </summary>
        [JsonIgnore]
        public Entity? Entity { get; set; }

        /// <summary>
        /// The navigation direction of the relation ("origin-target" or "target-origin").
        /// Not serialized.
        /// </summary>
        [JsonIgnore]
        public string? Direction { get; set; }

        /// <summary>
        /// Default constructor initializes relation to null and fields list to empty.
        /// </summary>
        public RelationFieldMeta()
        {
            Relation = null;
            Fields = new List<Field>();
        }

        /// <summary>
        /// Copy constructor creates a RelationFieldMeta from a base Field instance.
        /// Initializes entity/relation references to null and fields list to empty.
        /// </summary>
        /// <param name="field">The source Field to copy base properties from.</param>
        public RelationFieldMeta(Field field) : base(field)
        {
            Entity = null;
            Relation = null;
            Fields = new List<Field>();
        }
    }
}
