using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Geographic field format enumeration.
	/// </summary>
	public enum GeographyFieldFormat
	{
		GeoJSON = 1,
		Text = 2
	}

	/// <summary>
	/// Select option model used by SelectField and MultiSelectField.
	/// </summary>
	[Serializable]
	public class SelectOption
	{
		[JsonProperty(PropertyName = "value")]
		public string Value { get; set; }

		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; }

		[JsonProperty(PropertyName = "iconClass")]
		public string IconClass { get; set; }

		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; }

		public SelectOption()
		{
		}

		public SelectOption(string value, string label, string iconClass = null, string color = null)
		{
			Value = value;
			Label = label;
			IconClass = iconClass;
			Color = color;
		}
	}

	#region Concrete Field Types (Field subclasses)

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

	[Serializable]
	public class CheckboxField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.CheckboxField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public bool? DefaultValue { get; set; }
	}

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

	[Serializable]
	public class FileField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.FileField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

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

	[Serializable]
	public class GuidField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.GuidField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public Guid? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "generateNewId")]
		public bool? GenerateNewId { get; set; }

		public GuidField() : base()
		{
		}

		public GuidField(Field field) : base(field)
		{
		}
	}

	[Serializable]
	public class HtmlField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.HtmlField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	[Serializable]
	public class ImageField : Field
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.ImageField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

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

	[Serializable]
	public class RelationFieldMeta : Field
	{
		[JsonProperty(PropertyName = "relation")]
		public object Relation { get; set; }

		[JsonProperty(PropertyName = "fields")]
		public new List<Field> Fields { get; set; }

		[JsonIgnore]
		public string Entity { get; set; }

		[JsonIgnore]
		public string Direction { get; set; }

		[JsonIgnore]
		public string OriginEntity { get; set; }

		[JsonIgnore]
		public string OriginField { get; set; }

		[JsonIgnore]
		public string TargetEntity { get; set; }

		[JsonIgnore]
		public string TargetField { get; set; }
	}

	#endregion

	#region Concrete InputField Types (InputField subclasses)

	[Serializable]
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

	[Serializable]
	public class InputCheckboxField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.CheckboxField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public bool? DefaultValue { get; set; }
	}

	[Serializable]
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

	[Serializable]
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

	[Serializable]
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

	[Serializable]
	public class InputEmailField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.EmailField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	[Serializable]
	public class InputFileField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.FileField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	[Serializable]
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

	[Serializable]
	public class InputGuidField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.GuidField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public Guid? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "generateNewId")]
		public bool? GenerateNewId { get; set; }

		public InputGuidField() : base()
		{
		}

		public InputGuidField(InputField field) : base(field)
		{
		}
	}

	[Serializable]
	public class InputHtmlField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.HtmlField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	[Serializable]
	public class InputImageField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.ImageField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }
	}

	[Serializable]
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

	[Serializable]
	public class InputMultiSelectField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.MultiSelectField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public IEnumerable<string> DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public List<SelectOption> Options { get; set; }
	}

	[Serializable]
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

	[Serializable]
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

	[Serializable]
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

	[Serializable]
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

	[Serializable]
	public class InputSelectField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.SelectField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public List<SelectOption> Options { get; set; }
	}

	[Serializable]
	public class InputTextField : InputField
	{
		[JsonProperty(PropertyName = "fieldType")]
		public static FieldType FieldType { get { return FieldType.TextField; } }

		[JsonProperty(PropertyName = "defaultValue")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "maxLength")]
		public int? MaxLength { get; set; }
	}

	[Serializable]
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
