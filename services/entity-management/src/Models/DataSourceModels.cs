// =============================================================================
// DataSourceModels.cs — Datasource Models for Entity Management Service
// =============================================================================
// Consolidates 6 separate datasource-related model files from the monolith into
// a single file for the Entity Management bounded context:
//
//   Source files consolidated:
//     - WebVella.Erp/Api/Models/DataSourceType.cs      → DataSourceType enum
//     - WebVella.Erp/Api/Models/DataSourceBase.cs       → DataSourceBase abstract class
//     - WebVella.Erp/Api/Models/DatabaseDataSource.cs   → DatabaseDataSource class
//     - WebVella.Erp/Api/Models/CodeDataSource.cs       → CodeDataSource abstract class
//     - WebVella.Erp/Api/Models/DataSourceParameter.cs  → DataSourceParameter class
//     - WebVella.Erp/Api/Models/DataSourceModelFieldMeta.cs → DataSourceModelFieldMeta class
//
// These models define the data source metadata contracts used by the Entity
// Management service for dynamic query execution against DynamoDB (replacing
// the monolith's PostgreSQL-based EQL query engine).
//
// Namespace Migration:
//   Old: WebVella.Erp.Api.Models
//   New: WebVellaErp.EntityManagement.Models
//
// Serialization Migration:
//   Old: Newtonsoft.Json [JsonProperty(PropertyName = "...")]
//   New: System.Text.Json [JsonPropertyName("...")] (AOT-safe for .NET 9 Native AOT Lambda)
//
// Key Dependency Removals:
//   - WebVella.Erp.Eql.EqlFieldMeta — constructor dependency removed from
//     DataSourceModelFieldMeta; replaced with explicit parameter constructor
//     and AddChild() method for building field metadata trees without EQL coupling.
//   - WebVella.Erp.Database — no direct database dependencies retained.
//
// Internal Dependencies (same namespace):
//   - FieldType enum from Field.cs — used by DataSourceModelFieldMeta.Type
//   - SelectOptionAttribute from Definitions.cs — used on DataSourceType enum members
//
// All JSON property names are preserved exactly as snake_case for backward
// API compatibility with existing consumers.
// =============================================================================

using System.Text.Json.Serialization;

namespace WebVellaErp.EntityManagement.Models
{
    // =========================================================================
    // DataSourceType Enum
    // =========================================================================
    // Classifies a datasource as either a database-backed (EQL/SQL) query or
    // a code-backed (C# method) execution source.
    //
    // Migrated from: WebVella.Erp/Api/Models/DataSourceType.cs (lines 5-11)
    // Change: [SelectOption] attribute from Definitions.cs replaces the
    //         monolith's System.ComponentModel-based decoration.
    // =========================================================================

    /// <summary>
    /// Enumerates the types of data sources available in the entity management system.
    /// Each value is decorated with <see cref="SelectOptionAttribute"/> for UI rendering
    /// and serialization compatibility. Values 0 and 1 are explicit to preserve backward
    /// compatibility with persisted datasource metadata.
    /// </summary>
    public enum DataSourceType
    {
        /// <summary>
        /// Database-backed datasource — executes EQL or raw SQL text against
        /// the entity record store (DynamoDB query adapter in the target architecture).
        /// </summary>
        [SelectOption(Label = "database")]
        DATABASE = 0,

        /// <summary>
        /// Code-backed datasource — executes a C# method implementation that
        /// produces results programmatically rather than from a stored query.
        /// </summary>
        [SelectOption(Label = "code")]
        CODE = 1
    }

    // =========================================================================
    // DataSourceBase Abstract Class
    // =========================================================================
    // Abstract base class for all datasource types. Contains shared metadata
    // properties including identification, field definitions, parameters, and
    // result model configuration.
    //
    // Migrated from: WebVella.Erp/Api/Models/DataSourceBase.cs (lines 7-43)
    // Changes:
    //   - Newtonsoft.Json [JsonProperty] → System.Text.Json [JsonPropertyName]
    //   - List<T> initialization uses target-typed new() syntax
    //   - Nullable reference type annotations applied per project settings
    // =========================================================================

    /// <summary>
    /// Abstract base class for datasource definitions. Provides shared metadata
    /// properties for both database-backed and code-backed datasources including
    /// identity, field metadata tree, parameter definitions, and result model type.
    /// Subclassed by <see cref="DatabaseDataSource"/> and <see cref="CodeDataSource"/>.
    /// </summary>
    public abstract class DataSourceBase
    {
        /// <summary>
        /// Unique identifier for this datasource definition.
        /// Stored as a DynamoDB attribute; used for lookup and reference integrity.
        /// </summary>
        [JsonPropertyName("id")]
        public virtual Guid Id { get; set; }

        /// <summary>
        /// The classification of this datasource (DATABASE or CODE).
        /// Set by derived classes; private setter prevents external mutation.
        /// </summary>
        [JsonPropertyName("type")]
        public virtual DataSourceType Type { get; private set; }

        /// <summary>
        /// Human-readable name of the datasource. Used as the unique logical
        /// identifier in the datasource registry and for display in admin UIs.
        /// </summary>
        [JsonPropertyName("name")]
        public virtual string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional description providing context about the datasource's purpose,
        /// query behavior, or expected output format.
        /// </summary>
        [JsonPropertyName("description")]
        public virtual string Description { get; set; } = string.Empty;

        /// <summary>
        /// Sort weight for ordering datasources in listings and dropdowns.
        /// Lower values appear first. Default is 10 to allow insertion above/below.
        /// </summary>
        [JsonPropertyName("weight")]
        public virtual int Weight { get; set; } = 10;

        /// <summary>
        /// Whether the datasource execution should return the total count of
        /// matching records in addition to the paged result set. Default true
        /// for compatibility with the monolith's pagination behavior.
        /// </summary>
        [JsonPropertyName("return_total")]
        public virtual bool ReturnTotal { get; set; } = true;

        /// <summary>
        /// The name of the primary entity this datasource queries against.
        /// Empty string for datasources that span multiple entities or have
        /// no specific entity binding.
        /// </summary>
        [JsonPropertyName("entity_name")]
        public virtual string EntityName { get; set; } = string.Empty;

        /// <summary>
        /// Hierarchical metadata tree describing the fields returned by this
        /// datasource. Each node contains field name, type, entity context,
        /// and optional children for relation traversals. Private setter
        /// prevents replacement of the list — callers add to the existing list.
        /// </summary>
        [JsonPropertyName("fields")]
        public virtual List<DataSourceModelFieldMeta> Fields { get; private set; } = new();

        /// <summary>
        /// Parameter definitions for this datasource. Parameters provide dynamic
        /// values at execution time (e.g., filter values, page number, page size).
        /// Private setter prevents replacement — callers add to the existing list.
        /// </summary>
        [JsonPropertyName("parameters")]
        public virtual List<DataSourceParameter> Parameters { get; private set; } = new();

        /// <summary>
        /// The expected result model type name. Determines how the frontend and
        /// API layer interpret the datasource execution response. Default is
        /// "object" for generic results; overridden by subclasses (e.g.,
        /// "EntityRecordList" for database datasources).
        /// </summary>
        [JsonPropertyName("result_model")]
        public virtual string ResultModel { get; set; } = "object";

        /// <summary>
        /// Returns the datasource name as the string representation.
        /// Used for logging, debugging, and default display formatting.
        /// </summary>
        /// <returns>The <see cref="Name"/> property value.</returns>
        public override string ToString()
        {
            return Name;
        }
    }

    // =========================================================================
    // DatabaseDataSource Class
    // =========================================================================
    // Concrete datasource that executes queries defined as EQL text or raw SQL
    // text against the entity record store. In the microservice architecture,
    // EQL text is processed by the QueryAdapter service which translates it
    // to DynamoDB Query/Scan operations.
    //
    // Migrated from: WebVella.Erp/Api/Models/DatabaseDataSource.cs (lines 5-18)
    // Changes:
    //   - Newtonsoft.Json → System.Text.Json
    //   - ResultModel default preserved as "EntityRecordList"
    // =========================================================================

    /// <summary>
    /// A datasource that executes a stored query (EQL or SQL text) against the
    /// entity record store. The <see cref="Type"/> property always returns
    /// <see cref="DataSourceType.DATABASE"/>. The <see cref="ResultModel"/>
    /// defaults to "EntityRecordList" for structured record list responses.
    /// </summary>
    public class DatabaseDataSource : DataSourceBase
    {
        /// <summary>
        /// Overrides the base type to always indicate DATABASE classification.
        /// The getter-only property ensures immutable type identity for this subclass.
        /// </summary>
        [JsonPropertyName("type")]
        public override DataSourceType Type { get { return DataSourceType.DATABASE; } }

        /// <summary>
        /// The Entity Query Language (EQL) text defining the datasource query.
        /// In the target architecture, this text is processed by the QueryAdapter
        /// service to produce DynamoDB query/scan operations. Supports SELECT
        /// fields, FROM entity, WHERE filters, ORDER BY, PAGE, and PAGESIZE.
        /// </summary>
        [JsonPropertyName("eql_text")]
        public string EqlText { get; set; } = string.Empty;

        /// <summary>
        /// Optional raw SQL text for datasources that bypass EQL parsing.
        /// In the DynamoDB-backed architecture, this may contain PartiQL
        /// expressions or be used during migration from the PostgreSQL backend.
        /// </summary>
        [JsonPropertyName("sql_text")]
        public string SqlText { get; set; } = string.Empty;

        /// <summary>
        /// Overrides the default result model to "EntityRecordList", indicating
        /// that execution returns a structured list of entity records with
        /// metadata suitable for data table rendering.
        /// </summary>
        [JsonPropertyName("result_model")]
        public override string ResultModel { get; set; } = "EntityRecordList";
    }

    // =========================================================================
    // CodeDataSource Abstract Class
    // =========================================================================
    // Abstract datasource that produces results from a C# code execution
    // rather than a stored query. Subclasses implement the Execute method
    // with custom business logic for data retrieval or computation.
    //
    // Migrated from: WebVella.Erp/Api/Models/CodeDataSource.cs (lines 6-15)
    // Changes:
    //   - Newtonsoft.Json → System.Text.Json
    //   - ResultModel default preserved as "object"
    // =========================================================================

    /// <summary>
    /// Abstract datasource that produces results from programmatic execution
    /// rather than a stored query. The <see cref="Type"/> property always returns
    /// <see cref="DataSourceType.CODE"/>. Concrete implementations override
    /// <see cref="Execute"/> to provide custom data retrieval or computation logic.
    /// </summary>
    public abstract class CodeDataSource : DataSourceBase
    {
        /// <summary>
        /// Overrides the base type to always indicate CODE classification.
        /// The getter-only property ensures immutable type identity for this subclass.
        /// </summary>
        [JsonPropertyName("type")]
        public override DataSourceType Type { get { return DataSourceType.CODE; } }

        /// <summary>
        /// Overrides the default result model to "object", indicating that
        /// execution returns an arbitrary object whose shape is defined by the
        /// concrete implementation rather than a standard record list.
        /// </summary>
        [JsonPropertyName("result_model")]
        public override string ResultModel { get; set; } = "object";

        /// <summary>
        /// Executes the code datasource with the provided arguments dictionary.
        /// Each concrete implementation defines its own data retrieval, computation,
        /// or aggregation logic. Parameters from the datasource definition are
        /// resolved and passed as key-value pairs in the arguments dictionary.
        /// </summary>
        /// <param name="arguments">
        /// A dictionary of resolved parameter name-value pairs. Keys correspond to
        /// <see cref="DataSourceParameter.Name"/> definitions; values are the runtime
        /// resolved objects (which may be strings, numbers, dates, or complex objects).
        /// </param>
        /// <returns>
        /// The execution result as an object. The actual type depends on the concrete
        /// implementation and the <see cref="ResultModel"/> declaration. Callers should
        /// cast or serialize the result according to the declared result model.
        /// </returns>
        public abstract object Execute(Dictionary<string, object> arguments);
    }

    // =========================================================================
    // DataSourceParameter Class
    // =========================================================================
    // Defines a named parameter for datasource execution. Parameters are
    // resolved at execution time from the request context and passed to
    // the datasource query or code execution method.
    //
    // Migrated from: WebVella.Erp/Api/Models/DataSourceParameter.cs (lines 6-18)
    // Changes:
    //   - Newtonsoft.Json → System.Text.Json
    //   - Removed unused 'using System;' import
    // =========================================================================

    /// <summary>
    /// Represents a named parameter definition for a datasource. Parameters
    /// provide dynamic values at execution time (e.g., filter criteria, page
    /// number, current user ID). The parameter's <see cref="Type"/> string
    /// determines how the <see cref="Value"/> expression is parsed.
    /// </summary>
    public class DataSourceParameter
    {
        /// <summary>
        /// The parameter name used as the binding key in EQL parameter references
        /// (e.g., @paramName) and in code datasource argument dictionaries.
        /// Must be unique within a single datasource definition.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The type identifier string that determines how the Value expression
        /// is parsed and what CLR type the resolved parameter should be cast to.
        /// Common values include "text", "guid", "int", "decimal", "date", "bool".
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// The default value expression for this parameter. May contain literal
        /// values, request context references (e.g., "{{RequestContext.RecordId}}"),
        /// or empty string for required parameters that must be supplied at runtime.
        /// </summary>
        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// When true, parsing errors for this parameter's value are silently
        /// ignored and the parameter is treated as null/default. When false
        /// (default), parsing failures cause the datasource execution to fail
        /// with a validation error.
        /// </summary>
        [JsonPropertyName("ignore_parse_errors")]
        public bool IgnoreParseErrors { get; set; } = false;
    }

    // =========================================================================
    // DataSourceModelFieldMeta Class
    // =========================================================================
    // Describes a field in the datasource result model's metadata tree.
    // Supports hierarchical composition via Children for relation traversals
    // (e.g., $account.name contains a child "name" under "account").
    //
    // Migrated from: WebVella.Erp/Api/Models/DataSourceModelFieldMeta.cs
    // CRITICAL CHANGE: The constructor accepting EqlFieldMeta from
    //   WebVella.Erp.Eql namespace has been removed because the EQL engine
    //   dependency is not available in the microservice architecture.
    //   Replaced with:
    //     - A parameterless default constructor (for deserialization)
    //     - A parameterized constructor (name, type, entityName, relationName)
    //     - An AddChild() method for building the hierarchical tree
    // =========================================================================

    /// <summary>
    /// Represents metadata about a single field in a datasource's result model.
    /// Fields form a hierarchical tree where top-level fields belong to the primary
    /// entity and child fields represent relation traversals. Each node carries
    /// the field name, its <see cref="FieldType"/>, the owning entity name, and
    /// an optional relation name for cross-entity navigation.
    /// </summary>
    public class DataSourceModelFieldMeta
    {
        /// <summary>
        /// The field name as it appears in query results and API responses.
        /// For direct fields this matches the entity field name; for relation
        /// traversals this is the relation's navigation property name.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The field type classification from the <see cref="FieldType"/> enum.
        /// Determines how the field value should be displayed, validated, and
        /// serialized. For relation navigation nodes, this is typically
        /// <see cref="FieldType.RelationField"/>.
        /// </summary>
        [JsonPropertyName("type")]
        public FieldType Type { get; set; }

        /// <summary>
        /// The name of the entity that owns this field. Used to resolve the
        /// field definition for type checking and validation. May be null or
        /// empty for computed/virtual fields not bound to a specific entity.
        /// </summary>
        [JsonPropertyName("entity_name")]
        public string? EntityName { get; set; }

        /// <summary>
        /// The name of the entity relation used to reach this field's context.
        /// Non-null only for relation traversal nodes (where <see cref="Type"/>
        /// is <see cref="FieldType.RelationField"/>). Null for direct entity fields.
        /// </summary>
        [JsonPropertyName("relation_name")]
        public string? RelationName { get; set; }

        /// <summary>
        /// Child field metadata nodes representing fields accessible through
        /// this relation traversal. Empty for leaf fields (direct entity fields).
        /// Private setter prevents replacement — use <see cref="AddChild"/> to
        /// append children to the existing list.
        /// </summary>
        [JsonPropertyName("children")]
        public List<DataSourceModelFieldMeta> Children { get; private set; } = new();

        /// <summary>
        /// Default parameterless constructor required for JSON deserialization
        /// and for creating empty metadata nodes that are populated incrementally.
        /// </summary>
        public DataSourceModelFieldMeta()
        {
        }

        /// <summary>
        /// Constructs a field metadata node with explicit values. Replaces the
        /// monolith's EqlFieldMeta-based constructor that coupled datasource
        /// metadata construction to the EQL engine's internal data structures.
        /// </summary>
        /// <param name="name">
        /// The field name as it appears in query results and API responses.
        /// </param>
        /// <param name="type">
        /// The field type classification. Use <see cref="FieldType.RelationField"/>
        /// for relation navigation nodes.
        /// </param>
        /// <param name="entityName">
        /// Optional entity name that owns this field. Null for relation nodes
        /// that serve only as navigation containers.
        /// </param>
        /// <param name="relationName">
        /// Optional relation name for cross-entity traversals. Null for direct
        /// entity fields.
        /// </param>
        public DataSourceModelFieldMeta(string name, FieldType type, string? entityName = null, string? relationName = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type;
            EntityName = entityName;
            RelationName = relationName;
        }

        /// <summary>
        /// Appends a child field metadata node to this node's <see cref="Children"/>
        /// collection. Used to build the hierarchical field tree for datasources
        /// that traverse entity relations (e.g., adding "name" field under an
        /// "account" relation navigation node).
        /// </summary>
        /// <param name="child">
        /// The child field metadata node to add. Must not be null.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="child"/> is null.
        /// </exception>
        public void AddChild(DataSourceModelFieldMeta child)
        {
            if (child is null)
            {
                throw new ArgumentNullException(nameof(child));
            }

            Children.Add(child);
        }
    }
}
