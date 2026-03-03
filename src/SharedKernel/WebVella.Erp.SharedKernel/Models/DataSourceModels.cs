using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.SharedKernel.Models
{
	/// <summary>
	/// Enumerates the supported data source types in the ERP platform.
	/// DATABASE sources execute EQL/SQL queries; CODE sources execute
	/// arbitrary C# logic via a derived <see cref="CodeDataSource"/> class.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.DataSourceType</c>.
	/// </summary>
	public enum DataSourceType
	{
		[SelectOption(Label = "database")]
		DATABASE = 0,
		[SelectOption(Label = "code")]
		CODE = 1
	}

	/// <summary>
	/// Abstract base class for all data source definitions. Contains common
	/// metadata (id, name, description, weight), field metadata, parameters,
	/// and a result model descriptor.
	///
	/// All properties are annotated with <see cref="JsonPropertyAttribute"/>
	/// to preserve the REST API v3 JSON contract (AAP 0.8.1).
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.DataSourceBase</c>.
	/// </summary>
	public abstract class DataSourceBase
	{
		[JsonProperty(PropertyName = "id")]
		public virtual Guid Id { get; set; }

		[JsonProperty(PropertyName = "type")]
		public virtual DataSourceType Type { get; private set; }

		[JsonProperty(PropertyName = "name")]
		public virtual string Name { get; set; }

		[JsonProperty(PropertyName = "description")]
		public virtual string Description { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "weight")]
		public virtual int Weight { get; set; } = 10;

		[JsonProperty(PropertyName = "return_total")]
		public virtual bool ReturnTotal { get; set; } = true;

		[JsonProperty(PropertyName = "entity_name")]
		public virtual string EntityName { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "fields")]
		public virtual List<DataSourceModelFieldMeta> Fields { get; private set; } = new List<DataSourceModelFieldMeta>();

		[JsonProperty(PropertyName = "parameters")]
		public virtual List<DataSourceParameter> Parameters { get; private set; } = new List<DataSourceParameter>();

		[JsonProperty(PropertyName = "result_model")]
		public virtual string ResultModel { get; set; } = "object";

		public override string ToString()
		{
			return Name;
		}
	}

	/// <summary>
	/// Abstract specialization of <see cref="DataSourceBase"/> for code-based
	/// data sources. Derived classes must implement <see cref="Execute"/> to
	/// provide custom C# query/transformation logic at runtime.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.CodeDataSource</c>.
	/// </summary>
	public abstract class CodeDataSource : DataSourceBase
	{
		[JsonProperty(PropertyName = "type")]
		public override DataSourceType Type { get { return DataSourceType.CODE; } }

		[JsonProperty(PropertyName = "result_model")]
		public override string ResultModel { get; set; } = "object";

		/// <summary>
		/// Executes the code data source with the provided arguments dictionary.
		/// </summary>
		/// <param name="arguments">A dictionary of named parameters passed to the data source at runtime.</param>
		/// <returns>The data source result object, typically an <c>EntityRecordList</c> or custom object.</returns>
		public abstract object Execute(Dictionary<string, object> arguments);
	}

	/// <summary>
	/// Concrete specialization of <see cref="DataSourceBase"/> for database-backed
	/// data sources that use EQL or raw SQL text to query records.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.DatabaseDataSource</c>.
	/// </summary>
	public class DatabaseDataSource : DataSourceBase
	{
		[JsonProperty(PropertyName = "type")]
		public override DataSourceType Type { get { return DataSourceType.DATABASE; } }

		[JsonProperty(PropertyName = "eql_text")]
		public string EqlText { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "sql_text")]
		public string SqlText { get; set; } = string.Empty;

		[JsonProperty(PropertyName = "result_model")]
		public override string ResultModel { get; set; } = "EntityRecordList";
	}

	/// <summary>
	/// Represents a single named parameter for a data source definition,
	/// including its type, default value, and whether parse errors should
	/// be silently ignored.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.DataSourceParameter</c>.
	/// </summary>
	public class DataSourceParameter
	{
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "type")]
		public string Type { get; set; }

		[JsonProperty(PropertyName = "value")]
		public string Value { get; set; }

		[JsonProperty(PropertyName = "ignore_parse_errors")]
		public bool IgnoreParseErrors { get; set; } = false;
	}

	/// <summary>
	/// Represents a single field in a data source's result model metadata tree.
	/// Fields can be nested via the <see cref="Children"/> property to represent
	/// relation traversal paths (e.g., <c>$relation.field</c> in EQL).
	///
	/// The <see cref="DataSourceModelFieldMeta(EqlFieldMeta)"/> constructor maps
	/// EQL field metadata to this DTO recursively for UI/API consumption.
	///
	/// Preserved from the monolith's <c>WebVella.Erp.Api.Models.DataSourceModelFieldMeta</c>.
	/// </summary>
	public class DataSourceModelFieldMeta
	{
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		[JsonProperty(PropertyName = "type")]
		public FieldType Type { get; set; }

		[JsonProperty(PropertyName = "entity_name")]
		public string EntityName { get; set; }

		[JsonProperty(PropertyName = "relation_name")]
		public string RelationName { get; set; }

		[JsonProperty(PropertyName = "children")]
		public List<DataSourceModelFieldMeta> Children { get; private set; } = new List<DataSourceModelFieldMeta>();

		/// <summary>
		/// Default parameterless constructor for deserialization.
		/// </summary>
		public DataSourceModelFieldMeta() { }

		/// <summary>
		/// Constructs a <see cref="DataSourceModelFieldMeta"/> from an <see cref="EqlFieldMeta"/>
		/// instance. If the EQL field has a direct field reference, EntityName and Type are
		/// populated from the field. Otherwise (relation traversal), RelationName and
		/// <see cref="FieldType.RelationField"/> are used. Children are mapped recursively.
		/// </summary>
		/// <param name="fieldMeta">The EQL field metadata to convert.</param>
		public DataSourceModelFieldMeta(EqlFieldMeta fieldMeta)
		{
			Name = fieldMeta.Name;
			if (fieldMeta.Field != null)
			{
				EntityName = fieldMeta.Field.EntityName;
				Type = fieldMeta.Field.GetFieldType();
			}
			else
			{
				RelationName = fieldMeta.Relation.Name;
				Type = FieldType.RelationField;
			}
			foreach (var child in fieldMeta.Children)
				Children.Add(new DataSourceModelFieldMeta(child));
		}
	}
}
