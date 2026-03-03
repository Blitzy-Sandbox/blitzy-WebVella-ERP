using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Geographic field format enumeration.
	/// Database-persisted integer values — do not change assignments.
	/// Matches source: WebVella.Erp/Api/Models/FieldTypes/GeographyField.cs
	/// </summary>
	[Serializable]
	public enum GeographyFieldFormat
	{
		GeoJSON = 1, // ST_AsGeoJSON, default
		Text = 2, // STAsText
	}

	/// <summary>
	/// Select option model used by SelectField and MultiSelectField.
	/// All [JsonProperty] annotations preserved exactly from source for REST API contract stability.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/SelectField.cs
	/// </summary>
	[Serializable]
	public class SelectOption
	{
		[JsonProperty(PropertyName = "value")]
		public string Value { get; set; } = "";

		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; } = "";

		[JsonProperty(PropertyName = "icon_class")]
		public string IconClass { get; set; } = "";

		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; } = "";

		public SelectOption()
		{
		}

		public SelectOption(string value, string label)
		{
			Value = value;
			Label = label;
		}

		public SelectOption(string value, string label, string iconClass, string color)
		{
			Value = value;
			Label = label;
			IconClass = iconClass;
			Color = color;
		}

		public SelectOption(SelectOption option) : this(option.Value, option.Label)
		{
		}
	}

	#region Concrete Field Types (Field subclasses)

	/// <summary>
	/// Auto-incrementing number field with configurable display format and starting number.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/AutoNumberField.cs
	/// </summary>
	[Serializable]
	public class AutoNumberField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.AutoNumberField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "displayFormat")]
		public string DisplayFormat { get; set; }

		[JsonProperty(PropertyName = "startingNumber")]
		public decimal? StartingNumber { get; set; }
	}

	/// <summary>
	/// Boolean checkbox field with nullable default value.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/CheckboxField.cs
	/// </summary>
	[Serializable]
	public class CheckboxField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.CheckboxField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public bool? DefaultValue { get; set; }
	}

	/// <summary>
	/// Currency field with min/max validation and currency type selection.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/CurrencyField.cs
	/// </summary>
	[Serializable]
	public class CurrencyField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.CurrencyField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "minValue")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "maxValue")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "currency")]
		public CurrencyType Currency { get; set; }
	}

	/// <summary>
	/// Date-only field with configurable format and current-time default option.
	/// Critical dependency for DbRepository.CreateColumn and SetColumnDefaultValue.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/DateField.cs
	/// </summary>
	[Serializable]
	public class DateField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.DateField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public DateTime? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "useCurrentTimeAsDefaultValue")]
		public bool? UseCurrentTimeAsDefaultValue { get; set; }
	}

	/// <summary>
	/// Date and time field with configurable format and current-time default option.
	/// Critical dependency for DbRepository.CreateColumn and SetColumnDefaultValue.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/DateTimeField.cs
	/// </summary>
	[Serializable]
	public class DateTimeField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.DateTimeField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public DateTime? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "useCurrentTimeAsDefaultValue")]
		public bool? UseCurrentTimeAsDefaultValue { get; set; }
	}

	/// <summary>
	/// Email address field with max length validation.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/EmailField.cs
	/// </summary>
	[Serializable]
	public class EmailField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.EmailField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// File attachment field with default value path.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/FileField.cs
	/// </summary>
	[Serializable]
	public class FileField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.FileField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Geography/spatial field supporting GeoJSON and text formats with configurable SRID.
	/// Critical dependency for DbRepository.CreateIndex (GIST index type check).
	/// SRID defaults to 4326 (WGS84) — replaces ErpSettings.DefaultSRID which is not available in SharedKernel.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/GeographyField.cs
	/// </summary>
	[Serializable]
	public class GeographyField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.GeographyField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "visibleLineNumber")]
		public int? VisibleLineNumber { get; set; }

		[JsonProperty(PropertyName = "format")]
		public GeographyFieldFormat? Format { get; set; }

		[JsonProperty(PropertyName = "srid")]
		public int SRID { get; set; } = 4326;
	}

	/// <summary>
	/// GUID/UUID field with optional auto-generation of new IDs.
	/// Critical dependency for DbRepository.CreateColumn.
	/// Includes copy constructor for field cloning.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/GuidField.cs
	/// </summary>
	[Serializable]
	public class GuidField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.GuidField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public Guid? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "generateNewId")]
		public bool? GenerateNewId { get; set; }

		public GuidField()
		{
		}

		public GuidField(Field field) : base(field)
		{
		}
	}

	/// <summary>
	/// Rich HTML content field.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/HtmlField.cs
	/// </summary>
	[Serializable]
	public class HtmlField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.HtmlField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Image file reference field.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/ImageField.cs
	/// </summary>
	[Serializable]
	public class ImageField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.ImageField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Multi-line text field with max length and visible line count configuration.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/MultiLineTextField.cs
	/// </summary>
	[Serializable]
	public class MultiLineTextField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.MultiLineTextField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "visibleLineNumber")]
		public int? VisibleLineNumber { get; set; }
	}

	/// <summary>
	/// Multi-select field supporting multiple option selections.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/MultiSelectField.cs
	/// </summary>
	[Serializable]
	public class MultiSelectField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.MultiSelectField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public IEnumerable<string> DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public List<SelectOption> Options { get; set; }
	}

	/// <summary>
	/// Numeric field with min/max validation and configurable decimal places.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/NumberField.cs
	/// </summary>
	[Serializable]
	public class NumberField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.NumberField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "minValue")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "maxValue")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "decimalPlaces")]
		public byte? DecimalPlaces { get; set; }
	}

	/// <summary>
	/// Password field with min/max length and optional encryption.
	/// Note: No DefaultValue property — passwords should never have defaults.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/PasswordField.cs
	/// </summary>
	[Serializable]
	public class PasswordField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.PasswordField; } }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "minLength")]
		public int? MinLength { get; set; }

		[JsonProperty(PropertyName = "encrypted")]
		public bool? Encrypted { get; set; }
	}

	/// <summary>
	/// Percentage field with min/max validation and configurable decimal places.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/PercentField.cs
	/// </summary>
	[Serializable]
	public class PercentField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.PercentField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "minValue")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "maxValue")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "decimalPlaces")]
		public byte? DecimalPlaces { get; set; }
	}

	/// <summary>
	/// Phone number field with format pattern and max length.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/PhoneField.cs
	/// </summary>
	[Serializable]
	public class PhoneField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.PhoneField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// Single-select dropdown field with configurable options list.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/SelectField.cs
	/// </summary>
	[Serializable]
	public class SelectField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.SelectField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public List<SelectOption> Options { get; set; }
	}

	/// <summary>
	/// Single-line text field with max length validation.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/TextField.cs
	/// </summary>
	[Serializable]
	public class TextField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.TextField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// URL field with max length and configurable new-window target.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/UrlField.cs
	/// </summary>
	[Serializable]
	public class UrlField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.UrlField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "openTargetInNewWindow")]
		public bool? OpenTargetInNewWindow { get; set; }
	}

	/// <summary>
	/// Relation field metadata containing information about an entity relation and its related fields.
	/// Adapted for SharedKernel: uses object types for Entity/EntityRelation references not available
	/// in the shared kernel. Original source: WebVella.Erp/Api/Models/FieldTypes/RelationFieldMeta.cs
	/// Note: Source class was internal; made public for cross-service accessibility in SharedKernel.
	/// </summary>
	[Serializable]
	public class RelationFieldMeta : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.RelationField; } }

		[JsonProperty(PropertyName = "relationFields")]
		public List<Field> Fields { get; set; }

		[JsonProperty(PropertyName = "relation")]
		public object Relation { get; set; }

		[JsonIgnore]
		public object OriginEntity { get; set; }

		[JsonIgnore]
		public object TargetEntity { get; set; }

		[JsonIgnore]
		public Field OriginField { get; set; }

		[JsonIgnore]
		public Field TargetField { get; set; }

		[JsonIgnore]
		public object Entity { get; set; }

		[JsonIgnore]
		public string Direction { get; set; }

		public RelationFieldMeta()
		{
			Relation = null;
			Fields = new List<Field>();
		}

		public RelationFieldMeta(Field field) : base(field)
		{
			Entity = null;
			Relation = null;
			Fields = new List<Field>();
		}
	}

	#endregion

	#region Concrete InputField Types (InputField subclasses)

	/// <summary>
	/// Input model for AutoNumberField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/AutoNumberField.cs
	/// </summary>
	public class InputAutoNumberField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.AutoNumberField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "displayFormat")]
		public string DisplayFormat { get; set; }

		[JsonProperty(PropertyName = "startingNumber")]
		public decimal? StartingNumber { get; set; }
	}

	/// <summary>
	/// Input model for CheckboxField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/CheckboxField.cs
	/// </summary>
	public class InputCheckboxField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.CheckboxField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public bool? DefaultValue { get; set; }
	}

	/// <summary>
	/// Input model for CurrencyField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/CurrencyField.cs
	/// </summary>
	public class InputCurrencyField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.CurrencyField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "minValue")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "maxValue")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "currency")]
		public CurrencyType Currency { get; set; }
	}

	/// <summary>
	/// Input model for DateField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/DateField.cs
	/// </summary>
	public class InputDateField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.DateField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public DateTime? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "useCurrentTimeAsDefaultValue")]
		public bool? UseCurrentTimeAsDefaultValue { get; set; }
	}

	/// <summary>
	/// Input model for DateTimeField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/DateTimeField.cs
	/// </summary>
	public class InputDateTimeField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.DateTimeField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public DateTime? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "useCurrentTimeAsDefaultValue")]
		public bool? UseCurrentTimeAsDefaultValue { get; set; }
	}

	/// <summary>
	/// Input model for EmailField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/EmailField.cs
	/// </summary>
	public class InputEmailField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.EmailField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// Input model for FileField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/FileField.cs
	/// </summary>
	public class InputFileField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.FileField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Input model for GeographyField creation/update operations.
	/// SRID defaults to 4326 (WGS84) — replaces ErpSettings.DefaultSRID reference.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/GeographyField.cs
	/// </summary>
	public class InputGeographyField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.GeographyField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "visibleLineNumber")]
		public int? VisibleLineNumber { get; set; }

		[JsonProperty(PropertyName = "format")]
		public GeographyFieldFormat? Format { get; set; }

		[JsonProperty(PropertyName = "srid")]
		public int SRID { get; set; } = 4326;
	}

	/// <summary>
	/// Input model for GuidField creation/update operations.
	/// Includes copy constructor for field cloning.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/GuidField.cs
	/// </summary>
	public class InputGuidField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.GuidField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public Guid? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "generateNewId")]
		public bool? GenerateNewId { get; set; }

		public InputGuidField()
		{
		}

		public InputGuidField(InputField field) : base(field)
		{
		}
	}

	/// <summary>
	/// Input model for HtmlField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/HtmlField.cs
	/// </summary>
	public class InputHtmlField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.HtmlField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Input model for ImageField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/ImageField.cs
	/// </summary>
	public class InputImageField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.ImageField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Input model for MultiLineTextField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/MultiLineTextField.cs
	/// </summary>
	public class InputMultiLineTextField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.MultiLineTextField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "visibleLineNumber")]
		public int? VisibleLineNumber { get; set; }
	}

	/// <summary>
	/// Input model for MultiSelectField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/MultiSelectField.cs
	/// </summary>
	public class InputMultiSelectField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.MultiSelectField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public IEnumerable<string> DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public List<SelectOption> Options { get; set; }
	}

	/// <summary>
	/// Input model for NumberField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/NumberField.cs
	/// </summary>
	public class InputNumberField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.NumberField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "minValue")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "maxValue")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "decimalPlaces")]
		public byte? DecimalPlaces { get; set; }
	}

	/// <summary>
	/// Input model for PasswordField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/PasswordField.cs
	/// </summary>
	public class InputPasswordField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.PasswordField; } }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "minLength")]
		public int? MinLength { get; set; }

		[JsonProperty(PropertyName = "encrypted")]
		public bool? Encrypted { get; set; }
	}

	/// <summary>
	/// Input model for PercentField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/PercentField.cs
	/// </summary>
	public class InputPercentField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.PercentField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "minValue")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "maxValue")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "decimalPlaces")]
		public byte? DecimalPlaces { get; set; }
	}

	/// <summary>
	/// Input model for PhoneField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/PhoneField.cs
	/// </summary>
	public class InputPhoneField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.PhoneField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// Input model for SelectField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/SelectField.cs
	/// </summary>
	public class InputSelectField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.SelectField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public List<SelectOption> Options { get; set; }
	}

	/// <summary>
	/// Input model for TextField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/TextField.cs
	/// </summary>
	public class InputTextField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.TextField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// Input model for UrlField creation/update operations.
	/// Source: WebVella.Erp/Api/Models/FieldTypes/UrlField.cs
	/// </summary>
	public class InputUrlField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.UrlField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "openTargetInNewWindow")]
		public bool? OpenTargetInNewWindow { get; set; }
	}

	#endregion
}
