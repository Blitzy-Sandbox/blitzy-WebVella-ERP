using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Database
{
	/// <summary>
	/// Default SRID constant for geography fields. WGS84 standard.
	/// Replaces ErpSettings.DefaultSRID which was removed from SharedKernel ErpSettings.
	/// </summary>
	internal static class DbFieldDefaults
	{
		public const int DefaultSRID = 4326;
	}

	/// <summary>
	/// Auto-incrementing number field. Maps to PostgreSQL 'serial' type.
	/// </summary>
	public class DbAutoNumberField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "display_format")]
		public string DisplayFormat { get; set; }

		[JsonProperty(PropertyName = "starting_number")]
		public decimal? StartingNumber { get; set; }
	}

	/// <summary>
	/// Boolean checkbox field. Maps to PostgreSQL 'boolean' type.
	/// </summary>
	public class DbCheckboxField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public bool DefaultValue { get; set; }
	}

	/// <summary>
	/// Currency type descriptor used by DbCurrencyField.
	/// Uses snake_case JSON property names (different from API-level CurrencyType which uses camelCase).
	/// </summary>
	public class DbCurrencyType
	{
		[JsonProperty(PropertyName = "symbol")]
		public string Symbol { get; set; }

		[JsonProperty(PropertyName = "symbol_native")]
		public string SymbolNative { get; set; }

		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "name_plural")]
		public string NamePlural { get; set; }

		[JsonProperty(PropertyName = "code")]
		public string Code { get; set; }

		[JsonProperty(PropertyName = "decimal_digits")]
		public int DecimalDigits { get; set; }

		[JsonProperty(PropertyName = "rounding")]
		public int Rounding { get; set; }

		[JsonProperty(PropertyName = "symbol_placement")]
		public CurrencySymbolPlacement SymbolPlacement { get; set; }
	}

	/// <summary>
	/// Currency field with min/max constraints and currency type descriptor.
	/// Maps to PostgreSQL 'numeric' type.
	/// </summary>
	public class DbCurrencyField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "min_value")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "max_value")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "currency")]
		public DbCurrencyType Currency { get; set; }
	}

	/// <summary>
	/// Date-only field with optional current-time default. Maps to PostgreSQL 'date' type.
	/// </summary>
	public class DbDateField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public DateTime? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "use_current_time_as_default_value")]
		public bool UseCurrentTimeAsDefaultValue { get; set; }
	}

	/// <summary>
	/// Date-time field with optional current-time default. Maps to PostgreSQL 'timestamptz' type.
	/// </summary>
	public class DbDateTimeField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public DateTime? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "use_current_time_as_default_value")]
		public bool UseCurrentTimeAsDefaultValue { get; set; }
	}

	/// <summary>
	/// Email address field. Maps to PostgreSQL 'varchar(500)' type.
	/// </summary>
	public class DbEmailField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// File reference field. Maps to PostgreSQL 'varchar(1000)' type.
	/// </summary>
	public class DbFileField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Format enum for geography field data representation.
	/// </summary>
	public enum DbGeographyFieldFormat
	{
		GeoJSON = 1, // STAsGeoJson
		Text = 2, // STAsText
	}

	/// <summary>
	/// Geography/GIS field. Maps to PostgreSQL 'geography' type.
	/// Not fully supported at the moment.
	/// </summary>
	public class DbGeographyField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "visible_line_number")]
		public int? VisibleLineNumber { get; set; }

		[JsonProperty(PropertyName = "format")]
		public DbGeographyFieldFormat? Format { get; set; }

		[JsonProperty(PropertyName = "srid")]
		public int SRID { get; set; } = DbFieldDefaults.DefaultSRID;
	}

	/// <summary>
	/// GUID/UUID field. Maps to PostgreSQL 'uuid' type.
	/// </summary>
	public class DbGuidField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public Guid? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "generate_new_id")]
		public bool? GenerateNewId { get; set; }
	}

	/// <summary>
	/// HTML content field. Maps to PostgreSQL 'text' type.
	/// </summary>
	public class DbHtmlField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Image reference field. Maps to PostgreSQL 'varchar(1000)' type.
	/// </summary>
	public class DbImageField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }
	}

	/// <summary>
	/// Multi-line text field. Maps to PostgreSQL 'text' type.
	/// </summary>
	public class DbMultiLineTextField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "visible_line_number")]
		public int? VisibleLineNumber { get; set; }
	}

	/// <summary>
	/// Option type used by DbSelectField and DbMultiSelectField.
	/// Represents a single selectable option with label, value, icon class, and color.
	/// </summary>
	public class DbSelectFieldOption
	{
		[JsonProperty(PropertyName = "label")]
		public string Label { get; set; } = "";

		[JsonProperty(PropertyName = "value")]
		public string Value { get; set; } = "";

		[JsonProperty(PropertyName = "icon_class")]
		public string IconClass { get; set; } = "";

		[JsonProperty(PropertyName = "color")]
		public string Color { get; set; } = "";
	}

	/// <summary>
	/// Multi-select field with text array storage. Maps to PostgreSQL 'text[]' type.
	/// </summary>
	public class DbMultiSelectField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public IEnumerable<string> DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public IList<DbSelectFieldOption> Options { get; set; }
	}

	/// <summary>
	/// Numeric field with precision and range constraints. Maps to PostgreSQL 'numeric' type.
	/// </summary>
	public class DbNumberField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "min_value")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "max_value")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "decimal_places")]
		public byte DecimalPlaces { get; set; }
	}

	/// <summary>
	/// Password field with length constraints and encryption flag.
	/// Maps to PostgreSQL 'varchar(500)' type.
	/// </summary>
	public class DbPasswordField : DbBaseField
	{
		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "min_length")]
		public int? MinLength { get; set; }

		[JsonProperty(PropertyName = "encrypted")]
		public bool Encrypted { get; set; }
	}

	/// <summary>
	/// Percentage field with precision and range constraints. Maps to PostgreSQL 'numeric' type.
	/// </summary>
	public class DbPercentField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public decimal? DefaultValue { get; set; }

		[JsonProperty(PropertyName = "min_value")]
		public decimal? MinValue { get; set; }

		[JsonProperty(PropertyName = "max_value")]
		public decimal? MaxValue { get; set; }

		[JsonProperty(PropertyName = "decimal_places")]
		public byte DecimalPlaces { get; set; }
	}

	/// <summary>
	/// Phone number field. Maps to PostgreSQL 'varchar(100)' type.
	/// </summary>
	public class DbPhoneField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "format")]
		public string Format { get; set; }

		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// Single-select dropdown field. Maps to PostgreSQL 'varchar(200)' type.
	/// </summary>
	public class DbSelectField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "options")]
		public IList<DbSelectFieldOption> Options { get; set; }
	}

	/// <summary>
	/// Single-line text field. Maps to PostgreSQL 'text' type.
	/// </summary>
	public class DbTextField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }
	}

	/// <summary>
	/// Tree-select field for hierarchical entity selection.
	/// Maps to PostgreSQL 'uuid[]' (array of UUIDs) type.
	/// </summary>
	public class DbTreeSelectField : DbBaseField
	{
		[JsonProperty(PropertyName = "related_entity_id")]
		public Guid RelatedEntityId { get; set; }

		[JsonProperty(PropertyName = "relation_id")]
		public Guid RelationId { get; set; }

		[JsonProperty(PropertyName = "selected_tree_id")]
		public Guid SelectedTreeId { get; set; }

		[JsonProperty(PropertyName = "selection_type")]
		public string SelectionType { get; set; }

		[JsonProperty(PropertyName = "selection_target")]
		public string SelectionTarget { get; set; }
	}

	/// <summary>
	/// URL field with optional new-window target. Maps to PostgreSQL 'varchar(1000)' type.
	/// </summary>
	public class DbUrlField : DbBaseField
	{
		[JsonProperty(PropertyName = "default_value")]
		public string DefaultValue { get; set; }

		[JsonProperty(PropertyName = "max_length")]
		public int? MaxLength { get; set; }

		[JsonProperty(PropertyName = "open_target_in_new_window")]
		public bool OpenTargetInNewWindow { get; set; }
	}
}
