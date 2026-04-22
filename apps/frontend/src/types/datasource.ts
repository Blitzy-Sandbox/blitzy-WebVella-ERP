/**
 * DataSource TypeScript Interfaces
 *
 * TypeScript interface definitions for data source-related models, converted from
 * the WebVella ERP monolith C# DTOs:
 *   - WebVella.Erp.Web.Models.DatasourceVariable (DatasourceVariable.cs)
 *   - WebVella.Erp.Web.Models.EqlQuery (EqlQuery.cs)
 *   - WebVella.Erp.Web.Models.DataSourceTestModel (DataSourceTestModel.cs)
 *   - WebVella.Erp.Web.Models.DataSourceCodeTestModel (DataSourceCodeTestModel.cs)
 *   - WebVella.Erp.Api.Models.DataSourceBase (DataSourceBase.cs)
 *   - WebVella.Erp.Api.Models.DataSourceParameter (DataSourceParameter.cs)
 *   - WebVella.Erp.Api.Models.DataSourceType (DataSourceType.cs)
 *   - WebVella.Erp.Api.Models.DataSourceModelFieldMeta (DataSourceModelFieldMeta.cs)
 *   - WebVella.Erp.Api.Models.DatabaseDataSource (DatabaseDataSource.cs)
 *   - WebVella.Erp.Api.Models.CodeDataSource (CodeDataSource.cs)
 *   - WebVella.Erp.Eql.EqlParameter (EqlParameter.cs)
 *
 * Conversion rules applied:
 *   - C# Guid → string (UUID strings)
 *   - C# DateTime → string (ISO 8601)
 *   - C# List<T> → T[]
 *   - C# enums → const enum with original numeric values
 *   - C# object/dynamic → unknown
 *   - JSON property names from [JsonProperty] → camelCase equivalents
 */

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/**
 * Classifies the type of a data source variable.
 *
 * Maps from C# `DataSourceVariableType` in DatasourceVariable.cs:
 *   DATASOURCE = 0, CODE = 1, HTML = 2, SNIPPET = 3
 */
export const enum DataSourceVariableType {
  /** Variable backed by a registered datasource */
  Datasource = 0,
  /** Variable backed by inline code */
  Code = 1,
  /** Variable backed by raw HTML content */
  Html = 2,
  /** Variable backed by a reusable snippet */
  Snippet = 3,
}

/**
 * Discriminates the two kinds of data source definitions.
 *
 * Maps from C# `DataSourceType` in DataSourceType.cs:
 *   DATABASE = 0, CODE = 1
 */
export const enum DataSourceType {
  /** EQL/SQL-backed data source that queries a database */
  Database = 0,
  /** Code-backed data source executed server-side */
  Code = 1,
}

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * A named variable reference used within page data-source binding.
 *
 * Maps from C# `DataSourceVariable` in DatasourceVariable.cs.
 *
 * NOTE: The JSON wire format uses literal property keys `"string"` and
 * `"default"` (per `[JsonProperty]`). TypeScript allows reserved words
 * as interface property names, so we keep them for API compatibility.
 */
export interface DataSourceVariable {
  /**
   * The kind of variable.
   * @jsonProperty type
   * @default DataSourceVariableType.Datasource
   */
  type: DataSourceVariableType;

  /**
   * The variable expression or identifier string.
   * @jsonProperty string
   */
  string: string;

  /**
   * Fallback default value when the variable cannot be resolved.
   * @jsonProperty default
   */
  default: string;
}

/**
 * A single typed parameter for a data source query.
 *
 * Maps from C# `DataSourceParameter` in DataSourceParameter.cs.
 */
export interface DataSourceParameter {
  /**
   * Parameter name (e.g. `"@recordId"`).
   * @jsonProperty name
   */
  name: string;

  /**
   * Data-type hint for the parameter value (e.g. `"text"`, `"guid"`, `"int"`).
   * @jsonProperty type
   */
  type: string;

  /**
   * Serialized parameter value.
   * @jsonProperty value
   */
  value: string;

  /**
   * When `true`, parsing errors on this parameter are silently ignored.
   * @jsonProperty ignore_parse_errors
   * @default false
   */
  ignoreParseErrors: boolean;
}

/**
 * Recursive tree node describing a single field in a data source result model.
 *
 * Maps from C# `DataSourceModelFieldMeta` in DataSourceModelFieldMeta.cs.
 * The `children` array enables nested field hierarchies (e.g. relation fields).
 */
export interface DataSourceModelFieldMeta {
  /**
   * Field name within the result set.
   * @jsonProperty name
   */
  name: string;

  /**
   * Child field metadata nodes (recursive tree structure).
   * @jsonProperty children
   */
  children: DataSourceModelFieldMeta[];
}

/**
 * Abstract base definition shared by all data source types.
 *
 * Maps from C# `DataSourceBase` in DataSourceBase.cs.
 */
export interface DataSourceBase {
  /**
   * Unique identifier (UUID) of the data source.
   * @jsonProperty id
   */
  id: string;

  /**
   * Discriminator indicating whether this is a Database or Code data source.
   * @jsonProperty type
   */
  type: DataSourceType;

  /**
   * Programmatic name of the data source.
   * @jsonProperty name
   */
  name: string;

  /**
   * Human-readable description.
   * @jsonProperty description
   * @default ""
   */
  description: string;

  /**
   * Sort weight used for ordering data sources in listings.
   * @jsonProperty weight
   * @default 10
   */
  weight: number;

  /**
   * Whether the query should return the total record count alongside results.
   * @jsonProperty return_total
   * @default true
   */
  returnTotal: boolean;

  /**
   * The name of the entity this data source operates on.
   * @jsonProperty entity_name
   * @default ""
   */
  entityName: string;

  /**
   * Metadata describing the fields present in the result set.
   * @jsonProperty fields
   */
  fields: DataSourceModelFieldMeta[];

  /**
   * Typed parameters required to execute the data source query.
   * @jsonProperty parameters
   */
  parameters: DataSourceParameter[];

  /**
   * String identifier of the result model type (e.g. `"object"`, `"EntityRecordList"`).
   * @jsonProperty result_model
   * @default "object"
   */
  resultModel: string;
}

/**
 * A database-backed data source that uses EQL/SQL to query records.
 * Extends `DataSourceBase` with EQL and generated SQL text.
 *
 * Maps from C# `DatabaseDataSource` in DatabaseDataSource.cs.
 */
export interface DatabaseDataSource extends DataSourceBase {
  /**
   * EQL (Entity Query Language) text authored by the user.
   * @jsonProperty eql_text
   * @default ""
   */
  eqlText: string;

  /**
   * Generated SQL text produced by the EQL compiler (read-only at runtime).
   * @jsonProperty sql_text
   * @default ""
   */
  sqlText: string;
}

/**
 * A key-value parameter used within an EQL query.
 *
 * Maps from C# `EqlParameter` in Eql/EqlParameter.cs.
 * The `value` is typed as `unknown` because EQL parameters can carry any
 * serializable value (string, number, boolean, Guid, DateTime, null, etc.).
 */
export interface EqlParameter {
  /**
   * Parameter name (typically prefixed with `@`).
   * @jsonProperty name
   */
  name: string;

  /**
   * Parameter value — may be any JSON-serializable type.
   * @jsonProperty value
   */
  value: unknown;
}

/**
 * A self-contained EQL query with its associated parameters.
 *
 * Maps from C# `EqlQuery` in EqlQuery.cs.
 */
export interface EqlQuery {
  /**
   * EQL query text (e.g. `"SELECT * FROM account WHERE ..."`).
   * @jsonProperty eql
   */
  eql: string;

  /**
   * Ordered list of parameters to bind into the EQL query.
   * @jsonProperty parameters
   */
  parameters: EqlParameter[];
}

/**
 * A reference to a registered data source by name, along with runtime parameters.
 *
 * Maps from C# `EqlDataSourceQuery` in EqlQuery.cs.
 */
export interface EqlDataSourceQuery {
  /**
   * Programmatic name of the registered data source to execute.
   * @jsonProperty name
   */
  name: string;

  /**
   * Runtime parameters to pass to the data source.
   * @jsonProperty parameters
   */
  parameters: EqlParameter[];
}

/**
 * Request payload for testing a database (EQL) data source.
 *
 * Maps from C# `DataSourceTestModel` in DataSourceTestModel.cs.
 */
export interface DataSourceTestModel {
  /**
   * Action to perform (e.g. `"test"`, `"preview"`).
   * @jsonProperty action
   */
  action: string;

  /**
   * EQL query text to execute.
   * @jsonProperty eql
   */
  eql: string;

  /**
   * Raw serialized parameter string (legacy format).
   * @jsonProperty parameters
   */
  parameters: string;

  /**
   * Structured parameter list for the query.
   * @jsonProperty param_list
   */
  paramList: DataSourceParameter[];

  /**
   * Whether the test query should return the total record count.
   * @jsonProperty return_total
   * @default true
   */
  returnTotal: boolean;
}

/**
 * Request payload for testing a code-backed data source.
 *
 * Maps from C# `DataSourceCodeTestModel` in DataSourceCodeTestModel.cs.
 */
export interface DataSourceCodeTestModel {
  /**
   * C# source code to compile and execute server-side.
   * @jsonProperty csCode
   */
  csCode: string;
}
