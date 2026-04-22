// =========================================================================
// QueryAdapter.cs — EQL-like Query to DynamoDB Query/Scan Translation
// =========================================================================
// Replaces the entire Irony-based EQL engine (13 source files in
// WebVella.Erp/Eql/) with a hand-written recursive descent parser and
// DynamoDB query translation layer. This is the most complex service file
// in the entity-management module.
//
// Source files replaced:
//   EqlGrammar.cs, EqlAbstractTree.cs, EqlBuilder.cs, EqlBuilder.Sql.cs,
//   EqlCommand.cs, EqlBuildResult.cs, EqlFieldMeta.cs, EqlParameter.cs,
//   EqlSettings.cs, EqlError.cs, EqlException.cs, EqlNodeType.cs,
//   EqlRelationDirectionType.cs
//
// Design principles (AAP §0.7.1):
//   - No Irony dependency — hand-written recursive descent parser
//   - No PostgreSQL SQL generation — DynamoDB query translation
//   - Relation navigation ($/$$ notation) via separate DynamoDB queries
//     with in-memory join (no SQL JOINs)
//   - REGEX/FTS gracefully degrade to client-side filtering
//   - Full behavioral parity with source EQL engine
// =========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;

namespace WebVellaErp.EntityManagement.Services
{
    // =====================================================================
    // Phase 1: Supporting Types — Internalized EQL AST Models
    // =====================================================================

    #region Enums

    /// <summary>
    /// AST node type identifiers. Mirrors the monolith's EqlNodeType.cs enum
    /// values used during abstract syntax tree construction and traversal.
    /// </summary>
    internal enum EqlNodeType
    {
        Keyword,
        NumberValue,
        TextValue,
        ArgumentValue,
        Select,
        Field,
        RelationField,
        RelationWildcardField,
        WildcardField,
        From,
        Where,
        BinaryExpression,
        OrderBy,
        OrderByField,
        Page,
        PageSize
    }

    /// <summary>
    /// Relation traversal direction for $ and $$ prefix notation.
    /// Source: EqlRelationDirectionType.cs
    /// $ = TargetOrigin (default), $$ = OriginTarget
    /// </summary>
    internal enum EqlRelationDirectionType
    {
        /// <summary>Navigate from target entity to origin entity ($ prefix).</summary>
        TargetOrigin = 0,
        /// <summary>Navigate from origin entity to target entity ($$ prefix).</summary>
        OriginTarget = 1
    }

    #endregion

    #region Internal AST Node Classes

    /// <summary>
    /// Holds relation name and traversal direction for relation-qualified
    /// field references ($relation.field or $$relation.field).
    /// </summary>
    internal sealed class EqlRelationInfo
    {
        public string Name { get; set; } = string.Empty;
        public EqlRelationDirectionType Direction { get; set; }
    }

    /// <summary>Base class for all AST nodes produced by the recursive descent parser.</summary>
    internal abstract class EqlNode
    {
        public virtual EqlNodeType Type { get; }
    }

    /// <summary>Root SELECT statement node containing all clause references.</summary>
    internal sealed class EqlSelectNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.Select;
        public List<EqlFieldNodeBase> Fields { get; set; } = new();
        public EqlFromNode? From { get; set; }
        public EqlWhereNode? Where { get; set; }
        public EqlOrderByNode? OrderBy { get; set; }
        public EqlPageNode? Page { get; set; }
        public EqlPageSizeNode? PageSize { get; set; }
    }

    /// <summary>Base for field reference nodes (simple, wildcard, relation).</summary>
    internal abstract class EqlFieldNodeBase : EqlNode
    {
        public string FieldName { get; set; } = string.Empty;
    }

    /// <summary>Simple field reference: field_name.</summary>
    internal sealed class EqlFieldNode : EqlFieldNodeBase
    {
        public override EqlNodeType Type => EqlNodeType.Field;
    }

    /// <summary>Wildcard field reference: *.</summary>
    internal sealed class EqlWildcardFieldNode : EqlFieldNodeBase
    {
        public override EqlNodeType Type => EqlNodeType.WildcardField;
    }

    /// <summary>Relation-qualified field: $relation.field or $$relation.field.</summary>
    internal sealed class EqlRelationFieldNode : EqlFieldNodeBase
    {
        public override EqlNodeType Type => EqlNodeType.RelationField;
        public List<EqlRelationInfo> Relations { get; set; } = new();
    }

    /// <summary>Relation-qualified wildcard: $relation.* or $$relation.*.</summary>
    internal sealed class EqlRelationWildcardFieldNode : EqlFieldNodeBase
    {
        public override EqlNodeType Type => EqlNodeType.RelationWildcardField;
        public List<EqlRelationInfo> Relations { get; set; } = new();
    }

    /// <summary>FROM clause node with entity name.</summary>
    internal sealed class EqlFromNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.From;
        public string EntityName { get; set; } = string.Empty;
    }

    /// <summary>WHERE clause node with root expression.</summary>
    internal sealed class EqlWhereNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.Where;
        public EqlExpressionNode? Expression { get; set; }
    }

    /// <summary>Binary expression node with operator and two operands.</summary>
    internal sealed class EqlExpressionNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.BinaryExpression;
        public string Operator { get; set; } = string.Empty;
        public EqlNode? Left { get; set; }
        public EqlNode? Right { get; set; }
    }

    /// <summary>ORDER BY clause with list of sort fields.</summary>
    internal sealed class EqlOrderByNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.OrderBy;
        public List<EqlOrderByFieldNode> Fields { get; set; } = new();
    }

    /// <summary>Single ORDER BY field with name and direction.</summary>
    internal sealed class EqlOrderByFieldNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.OrderByField;
        public string FieldName { get; set; } = string.Empty;
        public string Direction { get; set; } = "ASC";
    }

    /// <summary>PAGE clause node (1-based page number).</summary>
    internal sealed class EqlPageNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.Page;
        public decimal? Number { get; set; }
        public string? ArgumentName { get; set; }
    }

    /// <summary>PAGESIZE clause node.</summary>
    internal sealed class EqlPageSizeNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.PageSize;
        public decimal? Number { get; set; }
        public string? ArgumentName { get; set; }
    }

    /// <summary>Literal number value node.</summary>
    internal sealed class EqlNumberValueNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.NumberValue;
        public decimal Value { get; set; }
    }

    /// <summary>Literal text (string) value node.</summary>
    internal sealed class EqlTextValueNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.TextValue;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Parameter argument node (@name).</summary>
    internal sealed class EqlArgumentValueNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.ArgumentValue;
        public string ArgumentName { get; set; } = string.Empty;
    }

    /// <summary>SQL keyword value node (NULL, TRUE, FALSE).</summary>
    internal sealed class EqlKeywordNode : EqlNode
    {
        public override EqlNodeType Type => EqlNodeType.Keyword;
        public string Keyword { get; set; } = string.Empty;
    }

    #endregion

    #region Internal Relation Projection Descriptor

    /// <summary>
    /// Tracks a pending relation projection that requires a separate
    /// DynamoDB query after the primary query completes.
    /// </summary>
    internal sealed class RelationProjection
    {
        public EntityRelation Relation { get; set; } = null!;
        public EqlRelationInfo RelationInfo { get; set; } = null!;
        public Entity TargetEntity { get; set; } = null!;
        public List<Field> TargetFields { get; set; } = new();
        public List<RelationProjection> Children { get; set; } = new();
        public Entity ParentEntity { get; set; } = null!;
    }

    #endregion

    // =====================================================================
    // Phase 1 (cont.): Public Supporting Types — Exported DTOs
    // =====================================================================

    #region EqlSettings

    /// <summary>
    /// Query execution settings controlling total count inclusion and
    /// distinct result deduplication. Maps to monolith EqlSettings.cs.
    /// </summary>
    public class EqlSettings
    {
        /// <summary>
        /// When true (default), a separate COUNT query is executed and
        /// attached to results for pagination UI support.
        /// </summary>
        [JsonPropertyName("include_total")]
        public bool IncludeTotal { get; set; } = true;

        /// <summary>
        /// When true, duplicate records are removed from results via
        /// client-side deduplication by record ID.
        /// </summary>
        [JsonPropertyName("distinct")]
        public bool Distinct { get; set; } = false;
    }

    #endregion

    #region EqlParameter

    /// <summary>
    /// Named query parameter with value and type hint. Replaces NpgsqlParameter
    /// with DynamoDB AttributeValue conversion. Source: EqlParameter.cs.
    /// </summary>
    public class EqlParameter
    {
        private string _parameterName = string.Empty;

        /// <summary>
        /// Parameter name, auto-normalized with '@' prefix.
        /// </summary>
        public string ParameterName
        {
            get => _parameterName;
            set
            {
                if (!string.IsNullOrEmpty(value) && !value.StartsWith("@"))
                    _parameterName = "@" + value;
                else
                    _parameterName = value ?? string.Empty;
            }
        }

        /// <summary>Runtime value of the parameter.</summary>
        public object? Value { get; set; }

        /// <summary>
        /// Type hint string: text, bool, date, int, decimal, guid.
        /// Controls DynamoDB AttributeValue conversion.
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// Ensures the parameter name has the '@' prefix.
        /// </summary>
        public void Normalize()
        {
            if (!string.IsNullOrEmpty(_parameterName) && !_parameterName.StartsWith("@"))
                _parameterName = "@" + _parameterName;
        }

        /// <summary>
        /// Converts this parameter's value to a DynamoDB AttributeValue
        /// based on the Type hint. Replaces ToNpgsqlParameter().
        /// </summary>
        public AttributeValue ToAttributeValue()
        {
            if (Value == null)
                return new AttributeValue { NULL = true };

            var strVal = Value.ToString() ?? string.Empty;

            switch (Type?.ToLowerInvariant())
            {
                case "int":
                case "integer":
                case "decimal":
                case "numeric":
                    return new AttributeValue { N = strVal };

                case "bool":
                case "boolean":
                    if (bool.TryParse(strVal, out var boolVal))
                        return new AttributeValue { BOOL = boolVal };
                    return new AttributeValue { S = strVal };

                case "guid":
                case "uuid":
                    return new AttributeValue { S = strVal };

                case "date":
                case "datetime":
                case "timestamptz":
                    if (Value is DateTime dt)
                        return new AttributeValue { S = dt.ToString("O") };
                    return new AttributeValue { S = strVal };

                case "text":
                default:
                    return new AttributeValue { S = strVal };
            }
        }
    }

    #endregion

    #region EqlError

    /// <summary>
    /// Represents a single parse or validation error with optional
    /// source location. Source: EqlError.cs.
    /// </summary>
    public class EqlError
    {
        /// <summary>Human-readable error description.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Source line number (1-based), null if not applicable.</summary>
        public int? Line { get; set; }

        /// <summary>Source column number (1-based), null if not applicable.</summary>
        public int? Column { get; set; }
    }

    #endregion

    #region EqlException

    /// <summary>
    /// Exception carrying one or more EQL errors. Source: EqlException.cs.
    /// </summary>
    public class EqlException : Exception
    {
        public List<EqlError> Errors { get; }

        public EqlException(string message)
            : base(message)
        {
            Errors = new List<EqlError> { new EqlError { Message = message } };
        }

        public EqlException(List<EqlError> errors)
            : base(errors.Count > 0 ? errors[0].Message : "EQL error")
        {
            Errors = errors ?? new List<EqlError>();
        }

        public EqlException(string message, Exception innerException)
            : base(message, innerException)
        {
            Errors = new List<EqlError> { new EqlError { Message = message } };
        }
    }

    #endregion

    #region EqlFieldMeta

    /// <summary>
    /// Metadata about a field in the query result projection. Used for
    /// building field metadata trees including relation sub-projections.
    /// Source: EqlFieldMeta.cs — extended with Entity property per schema.
    /// </summary>
    public class EqlFieldMeta
    {
        /// <summary>
        /// Field name as it appears in query results.
        /// For relation projections, prefixed with $ (e.g., "$customer_account").
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The resolved field definition, null for relation meta nodes.</summary>
        public Field? Field { get; set; }

        /// <summary>The owning entity of this field.</summary>
        public Entity? Entity { get; set; }

        /// <summary>The relation definition, non-null for relation projection nodes.</summary>
        public EntityRelation? Relation { get; set; }

        /// <summary>Child field metadata for nested relation projections.</summary>
        public List<EqlFieldMeta> Children { get; set; } = new();
    }

    #endregion

    #region EqlBuildResult

    /// <summary>
    /// Result of parsing and translating an EQL query. Contains errors,
    /// field metadata, resolved parameters, and the DynamoDB query spec.
    /// Source: EqlBuildResult.cs — Sql property replaced with DynamoDbQuery.
    /// </summary>
    public class EqlBuildResult
    {
        /// <summary>Parse/validation errors. Empty list means success.</summary>
        public List<EqlError> Errors { get; set; } = new();

        /// <summary>Field metadata tree for projecting results.</summary>
        public List<EqlFieldMeta> Meta { get; set; } = new();

        /// <summary>Resolved parameter values bound to the query.</summary>
        public List<EqlParameter> Parameters { get; set; } = new();

        /// <summary>Parameter names expected by the query (@name tokens).</summary>
        public List<string> ExpectedParameters { get; set; } = new();

        /// <summary>The resolved entity from the FROM clause.</summary>
        public Entity? FromEntity { get; set; }

        /// <summary>
        /// The translated DynamoDB query specification. Replaces the
        /// monolith's Sql string property.
        /// </summary>
        public DynamoDbQuerySpec? DynamoDbQuery { get; set; }

        /// <summary>Relation projections requiring separate DynamoDB queries.</summary>
        internal List<RelationProjection> RelationProjections { get; set; } = new();

        /// <summary>Parsed ORDER BY fields for in-memory sorting.</summary>
        internal List<EqlOrderByFieldNode> OrderByFields { get; set; } = new();

        /// <summary>Parsed page number (1-based) for OFFSET paging.</summary>
        internal int? PageNumber { get; set; }

        /// <summary>Parsed page size for LIMIT paging.</summary>
        internal int? PageSizeValue { get; set; }

        /// <summary>Whether REGEX filters require client-side post-filtering.</summary>
        internal List<RegexPostFilter> RegexPostFilters { get; set; } = new();

        /// <summary>Whether FTS filters require client-side post-filtering.</summary>
        internal List<FtsPostFilter> FtsPostFilters { get; set; } = new();
    }

    /// <summary>Describes a regex pattern to apply as client-side post-filter.</summary>
    internal sealed class RegexPostFilter
    {
        public string FieldName { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public bool CaseInsensitive { get; set; }
        public bool Negate { get; set; }
    }

    /// <summary>Describes a full-text search to apply as client-side post-filter.</summary>
    internal sealed class FtsPostFilter
    {
        public string FieldName { get; set; } = string.Empty;
        public string SearchText { get; set; } = string.Empty;
    }

    #endregion

    #region DynamoDbQuerySpec

    /// <summary>
    /// Specification for a DynamoDB Query or Scan operation. Replaces
    /// the monolith's SQL string generation from EqlBuilder.Sql.cs.
    /// </summary>
    public class DynamoDbQuerySpec
    {
        /// <summary>DynamoDB table name (e.g., "entity-management-records").</summary>
        public string TableName { get; set; } = string.Empty;

        /// <summary>Key condition expression for Query operations (PK/SK conditions).</summary>
        public string? KeyConditionExpression { get; set; }

        /// <summary>Filter expression for WHERE clause translation.</summary>
        public string? FilterExpression { get; set; }

        /// <summary>Projection expression for SELECT field list.</summary>
        public string? ProjectionExpression { get; set; }

        /// <summary>Name aliases for reserved word handling (#alias → actual_name).</summary>
        public Dictionary<string, string> ExpressionAttributeNames { get; set; } = new();

        /// <summary>Parameter values for expression placeholders (:val → AttributeValue).</summary>
        public Dictionary<string, AttributeValue> ExpressionAttributeValues { get; set; } = new();

        /// <summary>Pagination cursor for continuation queries.</summary>
        public Dictionary<string, AttributeValue>? ExclusiveStartKey { get; set; }

        /// <summary>Maximum items to return per page.</summary>
        public int? Limit { get; set; }

        /// <summary>Sort direction for sort key. True = ascending, false = descending.</summary>
        public bool ScanIndexForward { get; set; } = true;

        /// <summary>Global secondary index name, null for primary table query.</summary>
        public string? IndexName { get; set; }

        /// <summary>True to use Query operation, false for Scan.</summary>
        public bool UseQuery { get; set; } = true;

        /// <summary>DynamoDB Select mode (e.g., Select.COUNT for count-only queries).</summary>
        public Select? Select { get; set; }
    }

    #endregion

    // =====================================================================
    // Phase 2: Interface Definition — IQueryAdapter
    // =====================================================================

    /// <summary>
    /// Service interface for executing EQL-like queries and EntityQuery DSL
    /// queries against DynamoDB. Registered in DI for injection into handlers.
    /// </summary>
    public interface IQueryAdapter
    {
        /// <summary>
        /// Parses, translates, and executes an EQL query string against DynamoDB.
        /// </summary>
        /// <param name="eqlText">The EQL query text (SELECT ... FROM ... WHERE ...).</param>
        /// <param name="parameters">Optional named parameters for @arg binding.</param>
        /// <param name="settings">Optional query settings (IncludeTotal, Distinct).</param>
        /// <returns>QueryResult with field metadata and matching records.</returns>
        Task<QueryResult> Execute(string eqlText, List<EqlParameter>? parameters = null, EqlSettings? settings = null);

        /// <summary>
        /// Parses and executes an EQL query, returning only the count of matching records.
        /// </summary>
        /// <param name="eqlText">The EQL query text.</param>
        /// <param name="parameters">Optional named parameters.</param>
        /// <returns>Count of matching records.</returns>
        Task<long> ExecuteCount(string eqlText, List<EqlParameter>? parameters = null);

        /// <summary>
        /// Parses and translates an EQL query without executing it.
        /// Returns the build result for inspection or deferred execution.
        /// </summary>
        /// <param name="eqlText">The EQL query text.</param>
        /// <param name="parameters">Optional named parameters.</param>
        /// <param name="settings">Optional query settings.</param>
        /// <returns>Build result with DynamoDB query spec, metadata, and any errors.</returns>
        EqlBuildResult Build(string eqlText, List<EqlParameter>? parameters = null, EqlSettings? settings = null);

        /// <summary>
        /// Executes a query using the EntityQuery DSL (non-EQL path).
        /// Delegates to RecordRepository.Find() with metadata enrichment.
        /// </summary>
        /// <param name="query">The EntityQuery descriptor.</param>
        /// <returns>QueryResult with field metadata and matching records.</returns>
        Task<QueryResult> ExecuteQuery(EntityQuery query);

        /// <summary>
        /// Counts records matching the EntityQuery DSL (non-EQL path).
        /// </summary>
        /// <param name="query">The EntityQuery descriptor.</param>
        /// <returns>Count of matching records.</returns>
        Task<long> ExecuteCount(EntityQuery query);
    }

    // =====================================================================
    // Phase 3-10: QueryAdapter Implementation
    // =====================================================================

    /// <summary>
    /// Production implementation of IQueryAdapter. Replaces the entire
    /// Irony-based EQL engine with a hand-written recursive descent parser
    /// and DynamoDB query/scan translation layer.
    ///
    /// Supports: SELECT, FROM, WHERE (14 operators), ORDER BY, PAGE/PAGESIZE,
    /// $/$$ relation navigation, REGEX (client-side), FTS (contains-based),
    /// DISTINCT, IncludeTotal, and DataSource execution.
    /// </summary>
    public class QueryAdapter : IQueryAdapter
    {
        // ─── Constants ────────────────────────────────────────────────────
        private const string DEFAULT_TABLE_NAME = "entity-management-records";
        private const string PK_PREFIX = "ENTITY#";
        private const string PK_ATTR = "PK";
        private const string SK_ATTR = "SK";
        private const string SK_PREFIX = "RECORD#";

        // ─── Dependencies ─────────────────────────────────────────────────
        private readonly IEntityService _entityService;
        private readonly IRecordRepository _recordRepository;
        private readonly IEntityRepository _entityRepository;
        private readonly ILogger<QueryAdapter> _logger;

        // ─── Counter for unique expression attribute placeholders ─────────
        private int _attrCounter;

        /// <summary>
        /// Initializes a new QueryAdapter with required service dependencies.
        /// </summary>
        public QueryAdapter(
            IEntityService entityService,
            IRecordRepository recordRepository,
            IEntityRepository entityRepository,
            ILogger<QueryAdapter> logger)
        {
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _recordRepository = recordRepository ?? throw new ArgumentNullException(nameof(recordRepository));
            _entityRepository = entityRepository ?? throw new ArgumentNullException(nameof(entityRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // =================================================================
        // IQueryAdapter Implementation
        // =================================================================

        /// <inheritdoc />
        public async Task<QueryResult> Execute(string eqlText, List<EqlParameter>? parameters = null, EqlSettings? settings = null)
        {
            settings ??= new EqlSettings();
            _logger.LogInformation("Executing EQL query: {EqlText}", TruncateForLog(eqlText));

            var buildResult = Build(eqlText, parameters, settings);
            if (buildResult.Errors.Count > 0)
                throw new EqlException(buildResult.Errors);

            return await ExecuteFromBuildResult(buildResult, settings);
        }

        /// <inheritdoc />
        public async Task<long> ExecuteCount(string eqlText, List<EqlParameter>? parameters = null)
        {
            var settings = new EqlSettings { IncludeTotal = false, Distinct = false };
            _logger.LogInformation("Executing EQL count query: {EqlText}", TruncateForLog(eqlText));

            var buildResult = Build(eqlText, parameters, settings);
            if (buildResult.Errors.Count > 0)
                throw new EqlException(buildResult.Errors);

            if (buildResult.FromEntity == null)
                throw new EqlException("Entity not resolved from EQL query.");

            var countQuery = new EntityQuery(buildResult.FromEntity.Name);
            return await _recordRepository.Count(countQuery);
        }

        /// <inheritdoc />
        public EqlBuildResult Build(string eqlText, List<EqlParameter>? parameters = null, EqlSettings? settings = null)
        {
            _attrCounter = 0;
            settings ??= new EqlSettings();
            var result = new EqlBuildResult();

            if (string.IsNullOrWhiteSpace(eqlText))
            {
                result.Errors.Add(new EqlError { Message = "Source is empty." });
                return result;
            }

            // Normalize parameters
            var normalizedParams = NormalizeParameters(parameters);
            result.Parameters = normalizedParams;

            // Parse
            EqlSelectNode? selectNode;
            try
            {
                selectNode = ParseEql(eqlText, result.Errors, result.ExpectedParameters);
            }
            catch (EqlException ex)
            {
                result.Errors.AddRange(ex.Errors);
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add(new EqlError { Message = ex.Message });
                return result;
            }

            if (selectNode == null || result.Errors.Count > 0)
                return result;

            // Resolve FROM entity
            var fromEntity = ResolveFromEntity(selectNode, result.Errors);
            if (fromEntity == null)
                return result;
            result.FromEntity = fromEntity;

            // Resolve page/pagesize parameters
            ResolvePageParameters(selectNode, normalizedParams, result);
            if (result.Errors.Count > 0)
                return result;

            // Get all relations for relation navigation
            var relations = GetAllRelationsSync();

            // Translate to DynamoDB query spec
            var querySpec = TranslateToQuery(selectNode, fromEntity, relations, result, settings);
            result.DynamoDbQuery = querySpec;

            return result;
        }

        /// <inheritdoc />
        public async Task<QueryResult> ExecuteQuery(EntityQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            _logger.LogInformation("Executing EntityQuery for entity: {EntityName}", query.EntityName);

            var entity = await _entityService.GetEntity(query.EntityName);
            if (entity == null)
                throw new EqlException($"Entity '{query.EntityName}' not found");

            var records = await _recordRepository.Find(query);
            var totalCount = await _recordRepository.Count(query);

            var fieldsMeta = BuildFieldsMetaFromEntity(entity, query.Fields);
            var recordList = new EntityRecordList();
            recordList.AddRange(records);
            recordList.TotalCount = (int)totalCount;

            return new QueryResult
            {
                FieldsMeta = fieldsMeta,
                Data = recordList.Cast<EntityRecord>().ToList()
            };
        }

        /// <inheritdoc />
        public async Task<long> ExecuteCount(EntityQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            _logger.LogInformation("Executing EntityQuery count for entity: {EntityName}", query.EntityName);
            return await _recordRepository.Count(query);
        }

        /// <summary>
        /// Executes a DatabaseDataSource by parsing its EqlText and running
        /// through the standard EQL pipeline.
        /// </summary>
        /// <param name="dataSource">The database datasource definition.</param>
        /// <param name="runtimeParameters">Runtime parameter overrides.</param>
        /// <returns>QueryResult from executing the datasource's EQL query.</returns>
        public async Task<object> ExecuteDataSource(DatabaseDataSource dataSource, Dictionary<string, object>? runtimeParameters = null)
        {
            if (dataSource == null)
                throw new ArgumentNullException(nameof(dataSource));
            if (string.IsNullOrWhiteSpace(dataSource.EqlText))
                throw new EqlException("DataSource EQL text is empty.");

            _logger.LogInformation("Executing DataSource: {DataSourceName}", dataSource.Name);

            // Build EQL parameters from datasource parameter definitions and runtime overrides
            var eqlParams = new List<EqlParameter>();
            if (dataSource.Parameters != null)
            {
                foreach (var dsp in dataSource.Parameters)
                {
                    var param = new EqlParameter
                    {
                        ParameterName = dsp.Name,
                        Type = dsp.Type
                    };

                    // Check for runtime override
                    if (runtimeParameters != null &&
                        runtimeParameters.TryGetValue(dsp.Name, out var runtimeVal))
                    {
                        param.Value = runtimeVal;
                    }
                    else
                    {
                        param.Value = string.IsNullOrEmpty(dsp.Value) ? null : dsp.Value;
                    }

                    eqlParams.Add(param);
                }
            }

            var settings = new EqlSettings
            {
                IncludeTotal = dataSource.ReturnTotal
            };

            return await Execute(dataSource.EqlText, eqlParams, settings);
        }

        // =================================================================
        // Phase 4-5: Recursive Descent EQL Parser
        // =================================================================

        #region EQL Parser

        /// <summary>
        /// Entry point for the recursive descent parser. Parses the full
        /// EQL statement: SELECT columns FROM entity [WHERE expr]
        /// [ORDER BY list] [PAGE n] [PAGESIZE n]
        /// </summary>
        private EqlSelectNode? ParseEql(string eqlText, List<EqlError> errors, List<string> expectedParameters)
        {
            // Strip comments
            var cleaned = StripComments(eqlText).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                errors.Add(new EqlError { Message = "Source is empty." });
                return null;
            }

            var tokens = Tokenize(cleaned);
            var pos = 0;

            var selectNode = new EqlSelectNode();

            // Parse SELECT keyword
            if (!ExpectKeyword(tokens, ref pos, "SELECT", errors))
                return null;

            // Parse column list
            selectNode.Fields = ParseColumnList(tokens, ref pos, errors);
            if (errors.Count > 0) return null;

            // Parse FROM keyword and entity name
            if (!ExpectKeyword(tokens, ref pos, "FROM", errors))
                return null;

            if (pos >= tokens.Count || tokens[pos].Type == TokenType.Keyword)
            {
                errors.Add(new EqlError { Message = "Expected entity name after FROM." });
                return null;
            }
            selectNode.From = new EqlFromNode { EntityName = tokens[pos].Value };
            pos++;

            // Parse optional WHERE
            if (pos < tokens.Count && IsKeyword(tokens[pos], "WHERE"))
            {
                pos++; // skip WHERE
                var whereExpr = ParseOrExpression(tokens, ref pos, errors, expectedParameters);
                if (whereExpr != null)
                    selectNode.Where = new EqlWhereNode { Expression = whereExpr };
                if (errors.Count > 0) return null;
            }

            // Parse optional ORDER BY
            if (pos < tokens.Count && IsKeyword(tokens[pos], "ORDER"))
            {
                pos++; // skip ORDER
                if (!ExpectKeyword(tokens, ref pos, "BY", errors))
                    return null;
                selectNode.OrderBy = ParseOrderByList(tokens, ref pos, errors, expectedParameters);
                if (errors.Count > 0) return null;
            }

            // Parse optional PAGE
            if (pos < tokens.Count && IsKeyword(tokens[pos], "PAGE"))
            {
                pos++; // skip PAGE
                selectNode.Page = ParsePageValue(tokens, ref pos, errors, expectedParameters, "PAGE") as EqlPageNode;
                if (errors.Count > 0) return null;
            }

            // Parse optional PAGESIZE
            if (pos < tokens.Count && IsKeyword(tokens[pos], "PAGESIZE"))
            {
                pos++; // skip PAGESIZE
                var pageSizeVal = ParsePageValue(tokens, ref pos, errors, expectedParameters, "PAGESIZE");
                if (pageSizeVal is EqlPageSizeNode psn)
                    selectNode.PageSize = psn;
                if (errors.Count > 0) return null;
            }

            return selectNode;
        }

        /// <summary>
        /// Strips block comments (/* ... */) and line comments (-- ... \n).
        /// </summary>
        private static string StripComments(string input)
        {
            // Block comments
            var result = System.Text.RegularExpressions.Regex.Replace(input, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            // Line comments
            result = System.Text.RegularExpressions.Regex.Replace(result, @"--[^\r\n]*", " ");
            return result;
        }

        #region Tokenizer

        private enum TokenType
        {
            Identifier,
            Number,
            StringLiteral,
            Argument,      // @name
            Operator,      // =, <>, <, <=, >, >=, ~, ~*, !~, !~*, @@
            Keyword,       // SELECT, FROM, WHERE, ORDER, BY, AND, OR, CONTAINS, STARTSWITH, ASC, DESC, PAGE, PAGESIZE, NULL, TRUE, FALSE
            Symbol,        // (, ), ,, ., *
            Dollar,        // $
            DoubleDollar   // $$
        }

        private sealed class Token
        {
            public TokenType Type { get; set; }
            public string Value { get; set; } = string.Empty;
            public int Position { get; set; }
        }

        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "ORDER", "BY", "AND", "OR",
            "CONTAINS", "STARTSWITH", "ASC", "DESC", "PAGE", "PAGESIZE",
            "NULL", "TRUE", "FALSE"
        };

        /// <summary>
        /// Tokenizes the cleaned EQL text into a list of typed tokens.
        /// Handles identifiers, numbers, string literals, @arguments,
        /// operators, keywords, $/$$ relation prefixes, and symbols.
        /// </summary>
        private static List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            int i = 0;
            int len = input.Length;

            while (i < len)
            {
                char c = input[i];

                // Whitespace
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                // String literal (single-quoted)
                if (c == '\'')
                {
                    int start = i;
                    i++; // skip opening quote
                    var sb = new System.Text.StringBuilder();
                    while (i < len)
                    {
                        if (input[i] == '\'')
                        {
                            if (i + 1 < len && input[i + 1] == '\'')
                            {
                                sb.Append('\''); // doubled quote escape
                                i += 2;
                            }
                            else
                            {
                                i++; // skip closing quote
                                break;
                            }
                        }
                        else
                        {
                            sb.Append(input[i]);
                            i++;
                        }
                    }
                    tokens.Add(new Token { Type = TokenType.StringLiteral, Value = sb.ToString(), Position = start });
                    continue;
                }

                // @argument
                if (c == '@')
                {
                    if (i + 1 < len && input[i + 1] == '@')
                    {
                        // @@ operator (FTS)
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "@@", Position = i });
                        i += 2;
                        continue;
                    }
                    int start = i;
                    i++; // skip @
                    int nameStart = i;
                    while (i < len && (char.IsLetterOrDigit(input[i]) || input[i] == '_'))
                        i++;
                    tokens.Add(new Token { Type = TokenType.Argument, Value = "@" + input[nameStart..i], Position = start });
                    continue;
                }

                // $$ or $ prefix
                if (c == '$')
                {
                    if (i + 1 < len && input[i + 1] == '$')
                    {
                        tokens.Add(new Token { Type = TokenType.DoubleDollar, Value = "$$", Position = i });
                        i += 2;
                        continue;
                    }
                    tokens.Add(new Token { Type = TokenType.Dollar, Value = "$", Position = i });
                    i++;
                    continue;
                }

                // Operators: !=, !~*, !~, <>, <=, >=, ~*, ~, <, >, =
                if (c == '!' && i + 1 < len)
                {
                    if (input[i + 1] == '=' )
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "<>", Position = i });
                        i += 2;
                        continue;
                    }
                    if (input[i + 1] == '~')
                    {
                        if (i + 2 < len && input[i + 2] == '*')
                        {
                            tokens.Add(new Token { Type = TokenType.Operator, Value = "!~*", Position = i });
                            i += 3;
                        }
                        else
                        {
                            tokens.Add(new Token { Type = TokenType.Operator, Value = "!~", Position = i });
                            i += 2;
                        }
                        continue;
                    }
                }

                if (c == '<')
                {
                    if (i + 1 < len && input[i + 1] == '>')
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "<>", Position = i });
                        i += 2;
                    }
                    else if (i + 1 < len && input[i + 1] == '=')
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "<=", Position = i });
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "<", Position = i });
                        i++;
                    }
                    continue;
                }

                if (c == '>')
                {
                    if (i + 1 < len && input[i + 1] == '=')
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = ">=", Position = i });
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = ">", Position = i });
                        i++;
                    }
                    continue;
                }

                if (c == '~')
                {
                    if (i + 1 < len && input[i + 1] == '*')
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "~*", Position = i });
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "~", Position = i });
                        i++;
                    }
                    continue;
                }

                if (c == '=')
                {
                    tokens.Add(new Token { Type = TokenType.Operator, Value = "=", Position = i });
                    i++;
                    continue;
                }

                // Symbols: (, ), ,, .
                if (c == '(' || c == ')' || c == ',')
                {
                    tokens.Add(new Token { Type = TokenType.Symbol, Value = c.ToString(), Position = i });
                    i++;
                    continue;
                }

                if (c == '.')
                {
                    tokens.Add(new Token { Type = TokenType.Symbol, Value = ".", Position = i });
                    i++;
                    continue;
                }

                // * (wildcard or multiply — context-dependent)
                if (c == '*')
                {
                    tokens.Add(new Token { Type = TokenType.Symbol, Value = "*", Position = i });
                    i++;
                    continue;
                }

                // Number (integer or decimal)
                if (char.IsDigit(c) || (c == '-' && i + 1 < len && char.IsDigit(input[i + 1])))
                {
                    int start = i;
                    if (c == '-') i++;
                    while (i < len && (char.IsDigit(input[i]) || input[i] == '.'))
                        i++;
                    tokens.Add(new Token { Type = TokenType.Number, Value = input[start..i], Position = start });
                    continue;
                }

                // Identifier or keyword
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < len && (char.IsLetterOrDigit(input[i]) || input[i] == '_'))
                        i++;
                    string word = input[start..i];
                    if (Keywords.Contains(word))
                        tokens.Add(new Token { Type = TokenType.Keyword, Value = word.ToUpperInvariant(), Position = start });
                    else
                        tokens.Add(new Token { Type = TokenType.Identifier, Value = word, Position = start });
                    continue;
                }

                // Unknown character — skip
                i++;
            }

            return tokens;
        }

        #endregion

        #region Parser Helpers

        private static bool ExpectKeyword(List<Token> tokens, ref int pos, string keyword, List<EqlError> errors)
        {
            if (pos >= tokens.Count || !IsKeyword(tokens[pos], keyword))
            {
                errors.Add(new EqlError
                {
                    Message = $"Expected '{keyword}' keyword.",
                    Column = pos < tokens.Count ? tokens[pos].Position : null
                });
                return false;
            }
            pos++;
            return true;
        }

        private static bool IsKeyword(Token token, string keyword)
        {
            return token.Type == TokenType.Keyword &&
                   string.Equals(token.Value, keyword, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses the column list after SELECT: field names, *, $relation.field, etc.
        /// </summary>
        private static List<EqlFieldNodeBase> ParseColumnList(List<Token> tokens, ref int pos, List<EqlError> errors)
        {
            var fields = new List<EqlFieldNodeBase>();

            while (pos < tokens.Count)
            {
                if (IsKeyword(tokens[pos], "FROM"))
                    break;

                // Skip commas between fields
                if (tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == ",")
                {
                    pos++;
                    continue;
                }

                // Wildcard *
                if (tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == "*")
                {
                    fields.Add(new EqlWildcardFieldNode());
                    pos++;
                    continue;
                }

                // $ or $$ relation prefix
                if (tokens[pos].Type == TokenType.Dollar || tokens[pos].Type == TokenType.DoubleDollar)
                {
                    var relField = ParseRelationColumn(tokens, ref pos, errors);
                    if (relField != null) fields.Add(relField);
                    continue;
                }

                // Simple identifier field
                if (tokens[pos].Type == TokenType.Identifier)
                {
                    fields.Add(new EqlFieldNode { FieldName = tokens[pos].Value });
                    pos++;
                    continue;
                }

                // Unexpected token
                errors.Add(new EqlError { Message = $"Unexpected token in column list: '{tokens[pos].Value}'", Column = tokens[pos].Position });
                return fields;
            }

            return fields;
        }

        /// <summary>
        /// Parses a relation-qualified column: $relation.field or $$relation.field
        /// or $rel1.rel2.field (chained) or $relation.*
        /// </summary>
        private static EqlFieldNodeBase? ParseRelationColumn(List<Token> tokens, ref int pos, List<EqlError> errors)
        {
            var relations = new List<EqlRelationInfo>();
            var direction = tokens[pos].Type == TokenType.DoubleDollar
                ? EqlRelationDirectionType.OriginTarget
                : EqlRelationDirectionType.TargetOrigin;
            pos++; // skip $ or $$

            // Parse relation chain: rel1.rel2...
            while (pos < tokens.Count)
            {
                if (tokens[pos].Type != TokenType.Identifier)
                {
                    errors.Add(new EqlError { Message = "Expected relation name.", Column = tokens[pos].Position });
                    return null;
                }

                string relName = tokens[pos].Value;
                pos++;

                // Check for dot separator
                if (pos < tokens.Count && tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == ".")
                {
                    pos++; // skip dot

                    // Check if next is * (wildcard)
                    if (pos < tokens.Count && tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == "*")
                    {
                        relations.Add(new EqlRelationInfo { Name = relName, Direction = direction });
                        pos++; // skip *
                        return new EqlRelationWildcardFieldNode { Relations = relations };
                    }

                    // Check if next is another $ or identifier that looks like a field
                    if (pos < tokens.Count && tokens[pos].Type == TokenType.Identifier)
                    {
                        // Peek ahead to see if followed by dot (another chained relation)
                        if (pos + 1 < tokens.Count && tokens[pos + 1].Type == TokenType.Symbol && tokens[pos + 1].Value == ".")
                        {
                            // This is a chained relation
                            relations.Add(new EqlRelationInfo { Name = relName, Direction = direction });
                            continue;
                        }
                        else
                        {
                            // This is the final field name
                            relations.Add(new EqlRelationInfo { Name = relName, Direction = direction });
                            string fieldName = tokens[pos].Value;
                            pos++;
                            return new EqlRelationFieldNode { FieldName = fieldName, Relations = relations };
                        }
                    }
                    else
                    {
                        relations.Add(new EqlRelationInfo { Name = relName, Direction = direction });
                        errors.Add(new EqlError { Message = "Expected field name after relation.", Column = pos < tokens.Count ? tokens[pos].Position : null });
                        return null;
                    }
                }
                else
                {
                    // No dot: invalid syntax
                    errors.Add(new EqlError { Message = "Expected '.' after relation name.", Column = pos < tokens.Count ? tokens[pos].Position : null });
                    return null;
                }
            }

            errors.Add(new EqlError { Message = "Unexpected end of input in relation field." });
            return null;
        }

        /// <summary>
        /// Parses an OR-level expression (lowest precedence).
        /// </summary>
        private EqlExpressionNode? ParseOrExpression(List<Token> tokens, ref int pos, List<EqlError> errors, List<string> expectedParameters)
        {
            var left = ParseAndExpression(tokens, ref pos, errors, expectedParameters);
            if (left == null) return null;

            while (pos < tokens.Count && IsKeyword(tokens[pos], "OR"))
            {
                pos++; // skip OR
                var right = ParseAndExpression(tokens, ref pos, errors, expectedParameters);
                if (right == null) return null;
                left = new EqlExpressionNode { Operator = "OR", Left = left, Right = right };
            }

            return left is EqlExpressionNode expr ? expr : WrapAsExpression(left);
        }

        /// <summary>
        /// Parses an AND-level expression.
        /// </summary>
        private EqlExpressionNode? ParseAndExpression(List<Token> tokens, ref int pos, List<EqlError> errors, List<string> expectedParameters)
        {
            var left = ParseComparisonExpression(tokens, ref pos, errors, expectedParameters);
            if (left == null) return null;

            while (pos < tokens.Count && IsKeyword(tokens[pos], "AND"))
            {
                pos++; // skip AND
                var right = ParseComparisonExpression(tokens, ref pos, errors, expectedParameters);
                if (right == null) return null;
                left = new EqlExpressionNode { Operator = "AND", Left = left, Right = right };
            }

            return left is EqlExpressionNode expr ? expr : WrapAsExpression(left);
        }

        /// <summary>
        /// Parses a comparison or higher-precedence expression.
        /// </summary>
        private EqlNode? ParseComparisonExpression(List<Token> tokens, ref int pos, List<EqlError> errors, List<string> expectedParameters)
        {
            // Parenthesized expression
            if (pos < tokens.Count && tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == "(")
            {
                pos++; // skip (
                var inner = ParseOrExpression(tokens, ref pos, errors, expectedParameters);
                if (pos < tokens.Count && tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == ")")
                    pos++; // skip )
                else
                    errors.Add(new EqlError { Message = "Expected closing ')'." });
                return inner;
            }

            // Left operand
            var left = ParseOperand(tokens, ref pos, errors, expectedParameters);
            if (left == null) return null;

            // Check for operator
            if (pos < tokens.Count)
            {
                string? op = null;

                if (tokens[pos].Type == TokenType.Operator)
                {
                    op = tokens[pos].Value;
                    pos++;
                }
                else if (tokens[pos].Type == TokenType.Keyword)
                {
                    var kw = tokens[pos].Value;
                    if (kw == "CONTAINS" || kw == "STARTSWITH")
                    {
                        op = kw;
                        pos++;
                    }
                }

                if (op != null)
                {
                    var right = ParseOperand(tokens, ref pos, errors, expectedParameters);
                    if (right == null)
                    {
                        errors.Add(new EqlError { Message = $"Expected right operand for operator '{op}'." });
                        return null;
                    }
                    return new EqlExpressionNode { Operator = op, Left = left, Right = right };
                }
            }

            return left;
        }

        /// <summary>
        /// Parses a single operand: identifier, number, string literal,
        /// @argument, NULL, TRUE, FALSE, or $-prefixed relation field.
        /// </summary>
        private static EqlNode? ParseOperand(List<Token> tokens, ref int pos, List<EqlError> errors, List<string> expectedParameters)
        {
            if (pos >= tokens.Count)
            {
                errors.Add(new EqlError { Message = "Unexpected end of expression." });
                return null;
            }

            var token = tokens[pos];

            switch (token.Type)
            {
                case TokenType.Identifier:
                    pos++;
                    return new EqlFieldNode { FieldName = token.Value };

                case TokenType.Number:
                    pos++;
                    if (decimal.TryParse(token.Value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var numVal))
                        return new EqlNumberValueNode { Value = numVal };
                    errors.Add(new EqlError { Message = $"Invalid number: '{token.Value}'." });
                    return null;

                case TokenType.StringLiteral:
                    pos++;
                    return new EqlTextValueNode { Value = token.Value };

                case TokenType.Argument:
                    pos++;
                    if (!expectedParameters.Contains(token.Value))
                        expectedParameters.Add(token.Value);
                    return new EqlArgumentValueNode { ArgumentName = token.Value };

                case TokenType.Keyword when string.Equals(token.Value, "NULL", StringComparison.OrdinalIgnoreCase):
                    pos++;
                    return new EqlKeywordNode { Keyword = "NULL" };

                case TokenType.Keyword when string.Equals(token.Value, "TRUE", StringComparison.OrdinalIgnoreCase):
                    pos++;
                    return new EqlKeywordNode { Keyword = "TRUE" };

                case TokenType.Keyword when string.Equals(token.Value, "FALSE", StringComparison.OrdinalIgnoreCase):
                    pos++;
                    return new EqlKeywordNode { Keyword = "FALSE" };

                case TokenType.Dollar:
                case TokenType.DoubleDollar:
                    // Relation field in WHERE clause
                    var relDirection = token.Type == TokenType.DoubleDollar
                        ? EqlRelationDirectionType.OriginTarget
                        : EqlRelationDirectionType.TargetOrigin;
                    pos++; // skip $ or $$
                    if (pos >= tokens.Count || tokens[pos].Type != TokenType.Identifier)
                    {
                        errors.Add(new EqlError { Message = "Expected relation name after $." });
                        return null;
                    }
                    string relName = tokens[pos].Value;
                    pos++;
                    if (pos < tokens.Count && tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == ".")
                    {
                        pos++; // skip dot
                        if (pos < tokens.Count && tokens[pos].Type == TokenType.Identifier)
                        {
                            string fld = tokens[pos].Value;
                            pos++;
                            return new EqlRelationFieldNode
                            {
                                FieldName = fld,
                                Relations = new List<EqlRelationInfo>
                                {
                                    new EqlRelationInfo { Name = relName, Direction = relDirection }
                                }
                            };
                        }
                    }
                    errors.Add(new EqlError { Message = "Expected field name after relation." });
                    return null;

                default:
                    // Could be an operator keyword that's not an operand
                    errors.Add(new EqlError { Message = $"Unexpected token: '{token.Value}'.", Column = token.Position });
                    return null;
            }
        }

        /// <summary>Parses the ORDER BY field list.</summary>
        private static EqlOrderByNode ParseOrderByList(List<Token> tokens, ref int pos, List<EqlError> errors, List<string> expectedParameters)
        {
            var orderBy = new EqlOrderByNode();

            while (pos < tokens.Count)
            {
                // Stop at PAGE, PAGESIZE, or end
                if (IsKeyword(tokens[pos], "PAGE") || IsKeyword(tokens[pos], "PAGESIZE"))
                    break;

                if (tokens[pos].Type == TokenType.Symbol && tokens[pos].Value == ",")
                {
                    pos++;
                    continue;
                }

                if (tokens[pos].Type == TokenType.Identifier)
                {
                    var field = new EqlOrderByFieldNode { FieldName = tokens[pos].Value };
                    pos++;

                    // Check for ASC/DESC or argument
                    if (pos < tokens.Count)
                    {
                        if (IsKeyword(tokens[pos], "ASC"))
                        {
                            field.Direction = "ASC";
                            pos++;
                        }
                        else if (IsKeyword(tokens[pos], "DESC"))
                        {
                            field.Direction = "DESC";
                            pos++;
                        }
                        else if (tokens[pos].Type == TokenType.Argument)
                        {
                            // Direction from parameter
                            if (!expectedParameters.Contains(tokens[pos].Value))
                                expectedParameters.Add(tokens[pos].Value);
                            field.Direction = tokens[pos].Value; // will be resolved at execution
                            pos++;
                        }
                    }

                    orderBy.Fields.Add(field);
                }
                else
                {
                    break;
                }
            }

            return orderBy;
        }

        /// <summary>Parses a PAGE or PAGESIZE value (number literal or @argument).</summary>
        private static EqlNode? ParsePageValue(List<Token> tokens, ref int pos, List<EqlError> errors, List<string> expectedParameters, string clauseName)
        {
            if (pos >= tokens.Count)
            {
                errors.Add(new EqlError { Message = $"Expected value after {clauseName}." });
                return null;
            }

            if (tokens[pos].Type == TokenType.Number)
            {
                if (decimal.TryParse(tokens[pos].Value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var val))
                {
                    pos++;
                    if (clauseName == "PAGESIZE")
                        return new EqlPageSizeNode { Number = val };
                    return new EqlPageNode { Number = val };
                }
            }

            if (tokens[pos].Type == TokenType.Argument)
            {
                string argName = tokens[pos].Value;
                if (!expectedParameters.Contains(argName))
                    expectedParameters.Add(argName);
                pos++;
                if (clauseName == "PAGESIZE")
                    return new EqlPageSizeNode { ArgumentName = argName };
                return new EqlPageNode { ArgumentName = argName };
            }

            errors.Add(new EqlError { Message = $"Expected number or parameter after {clauseName}." });
            return null;
        }

        private static EqlPageSizeNode? ConvertToPageSize(EqlNode? node)
        {
            if (node is EqlPageSizeNode psn) return psn;
            return null;
        }

        private static EqlExpressionNode WrapAsExpression(EqlNode node)
        {
            if (node is EqlExpressionNode expr) return expr;
            // A standalone operand in a WHERE is invalid but handle gracefully
            return new EqlExpressionNode { Operator = "EQ", Left = node, Right = new EqlKeywordNode { Keyword = "TRUE" } };
        }

        #endregion

        #endregion

        // =================================================================
        // Phase 6: Query Translation — AST to DynamoDB Operations
        // =================================================================

        #region Query Translation

        /// <summary>
        /// Translates the parsed AST into a DynamoDbQuerySpec.
        /// </summary>
        private DynamoDbQuerySpec? TranslateToQuery(
            EqlSelectNode selectNode,
            Entity fromEntity,
            List<EntityRelation> relations,
            EqlBuildResult result,
            EqlSettings settings)
        {
            var spec = new DynamoDbQuerySpec
            {
                TableName = DEFAULT_TABLE_NAME,
                UseQuery = true,
                ScanIndexForward = true
            };

            // Key condition: PK = ENTITY#{entityName}
            spec.KeyConditionExpression = $"{PK_ATTR} = :pk";
            spec.ExpressionAttributeValues[":pk"] = new AttributeValue { S = PK_PREFIX + fromEntity.Name };

            // Process SELECT clause — build projection and field metadata
            ProcessSelectClause(selectNode, fromEntity, relations, result, spec, settings);

            // Process WHERE clause — build filter expression
            if (selectNode.Where?.Expression != null)
            {
                var filterParts = new List<string>();
                TranslateWhereExpression(selectNode.Where.Expression, fromEntity, relations,
                    result, spec, filterParts);
                if (filterParts.Count > 0)
                    spec.FilterExpression = string.Join(" AND ", filterParts);
            }

            // Process ORDER BY — stored for in-memory sorting
            if (selectNode.OrderBy?.Fields != null && selectNode.OrderBy.Fields.Count > 0)
            {
                foreach (var orderField in selectNode.OrderBy.Fields)
                {
                    if (!fromEntity.Fields.Any(f => string.Equals(f.Name, orderField.FieldName, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Errors.Add(new EqlError
                        {
                            Message = $"Order field '{orderField.FieldName}' is not found in entity '{fromEntity.Name}'"
                        });
                        return null;
                    }
                }
                result.OrderByFields = selectNode.OrderBy.Fields;
            }

            // Process PAGING
            if (result.PageNumber.HasValue || result.PageSizeValue.HasValue)
            {
                if ((result.PageNumber.HasValue && !result.PageSizeValue.HasValue) ||
                    (!result.PageNumber.HasValue && result.PageSizeValue.HasValue))
                {
                    result.Errors.Add(new EqlError
                    {
                        Message = "When PAGE or PAGESIZE commands are used, both of them should be used together."
                    });
                    return null;
                }

                if (result.PageNumber.HasValue && result.PageNumber.Value <= 0)
                {
                    result.Errors.Add(new EqlError { Message = "PAGE should be positive number" });
                    return null;
                }

                if (result.PageSizeValue.HasValue && result.PageSizeValue.Value <= 0)
                {
                    result.Errors.Add(new EqlError { Message = "PAGESIZE should be positive number" });
                    return null;
                }

                // DynamoDB limit is set to fetch enough records for paging
                // We fetch page*pageSize records and skip client-side for OFFSET emulation
                spec.Limit = result.PageNumber!.Value * result.PageSizeValue!.Value;
            }

            return spec;
        }

        /// <summary>
        /// Processes the SELECT clause to build projection expression and field metadata.
        /// </summary>
        private void ProcessSelectClause(
            EqlSelectNode selectNode,
            Entity fromEntity,
            List<EntityRelation> relations,
            EqlBuildResult result,
            DynamoDbQuerySpec spec,
            EqlSettings settings)
        {
            bool hasWildcard = selectNode.Fields.Any(f => f is EqlWildcardFieldNode);
            var projectionFields = new List<string>();

            foreach (var fieldNode in selectNode.Fields)
            {
                switch (fieldNode)
                {
                    case EqlWildcardFieldNode:
                        // No ProjectionExpression needed — return all attributes
                        foreach (var field in fromEntity.Fields)
                        {
                            result.Meta.Add(new EqlFieldMeta
                            {
                                Name = field.Name,
                                Field = field,
                                Entity = fromEntity
                            });
                        }
                        break;

                    case EqlFieldNode fn:
                        var resolvedField = fromEntity.Fields.FirstOrDefault(
                            f => string.Equals(f.Name, fn.FieldName, StringComparison.OrdinalIgnoreCase));
                        if (resolvedField == null)
                        {
                            result.Errors.Add(new EqlError { Message = $"Field '{fn.FieldName}' not found." });
                            return;
                        }
                        if (!projectionFields.Contains(resolvedField.Name))
                            projectionFields.Add(resolvedField.Name);
                        result.Meta.Add(new EqlFieldMeta
                        {
                            Name = resolvedField.Name,
                            Field = resolvedField,
                            Entity = fromEntity
                        });
                        break;

                    case EqlRelationFieldNode rfn:
                        ProcessRelationFieldProjection(rfn, fromEntity, relations, result);
                        break;

                    case EqlRelationWildcardFieldNode rwfn:
                        ProcessRelationWildcardProjection(rwfn, fromEntity, relations, result);
                        break;
                }
            }

            // Build projection expression for non-wildcard queries
            if (!hasWildcard && projectionFields.Count > 0)
            {
                var nameParts = new List<string>();
                foreach (var field in projectionFields)
                {
                    string alias = "#f_" + field.Replace("-", "_");
                    spec.ExpressionAttributeNames[alias] = field;
                    nameParts.Add(alias);
                }
                // Always include PK, SK, id for join support
                if (!spec.ExpressionAttributeNames.ContainsValue("id"))
                {
                    spec.ExpressionAttributeNames["#f_id"] = "id";
                    nameParts.Add("#f_id");
                }
                spec.ExpressionAttributeNames["#pk"] = PK_ATTR;
                spec.ExpressionAttributeNames["#sk"] = SK_ATTR;
                nameParts.Add("#pk");
                nameParts.Add("#sk");
                spec.ProjectionExpression = string.Join(", ", nameParts);
            }
        }

        /// <summary>
        /// Processes a relation-qualified field reference in SELECT and registers
        /// it as a pending relation projection requiring a separate DynamoDB query.
        /// </summary>
        private void ProcessRelationFieldProjection(
            EqlRelationFieldNode rfn,
            Entity parentEntity,
            List<EntityRelation> allRelations,
            EqlBuildResult result)
        {
            Entity currentEntity = parentEntity;
            var relationProjections = result.RelationProjections;

            foreach (var relInfo in rfn.Relations)
            {
                var relation = allRelations.FirstOrDefault(
                    r => string.Equals(r.Name, relInfo.Name, StringComparison.OrdinalIgnoreCase));
                if (relation == null)
                {
                    result.Errors.Add(new EqlError { Message = $"Relation '{relInfo.Name}' not found." });
                    return;
                }

                // Determine target entity based on relation direction
                Entity targetEntity;
                if (relation.OriginEntityId == currentEntity.Id)
                    targetEntity = GetEntityByIdSync(relation.TargetEntityId);
                else
                    targetEntity = GetEntityByIdSync(relation.OriginEntityId);

                if (targetEntity == null)
                {
                    result.Errors.Add(new EqlError { Message = $"Target entity for relation '{relInfo.Name}' not found." });
                    return;
                }

                // Find or create the relation projection
                var existing = relationProjections.FirstOrDefault(
                    rp => rp.Relation.Id == relation.Id);
                if (existing == null)
                {
                    existing = new RelationProjection
                    {
                        Relation = relation,
                        RelationInfo = relInfo,
                        TargetEntity = targetEntity,
                        ParentEntity = currentEntity,
                        TargetFields = new List<Field>()
                    };
                    // Always include id field
                    var idField = targetEntity.Fields.FirstOrDefault(f => f.Name == "id");
                    if (idField != null)
                        existing.TargetFields.Add(idField);
                    relationProjections.Add(existing);
                }

                // Add the specific field
                var targetField = targetEntity.Fields.FirstOrDefault(
                    f => string.Equals(f.Name, rfn.FieldName, StringComparison.OrdinalIgnoreCase));
                if (targetField == null)
                {
                    result.Errors.Add(new EqlError
                    {
                        Message = $"Field '{rfn.FieldName}' not found in entity '{targetEntity.Name}' for relation '{relInfo.Name}'."
                    });
                    return;
                }

                if (!existing.TargetFields.Any(f => f.Id == targetField.Id))
                    existing.TargetFields.Add(targetField);

                // Build metadata
                var childMeta = new List<EqlFieldMeta>
                {
                    new EqlFieldMeta { Name = rfn.FieldName, Field = targetField, Entity = targetEntity }
                };
                result.Meta.Add(new EqlFieldMeta
                {
                    Name = $"${relation.Name}",
                    Relation = relation,
                    Entity = targetEntity,
                    Children = childMeta
                });

                currentEntity = targetEntity;
            }
        }

        /// <summary>
        /// Processes a relation-qualified wildcard reference ($relation.*) in SELECT.
        /// </summary>
        private void ProcessRelationWildcardProjection(
            EqlRelationWildcardFieldNode rwfn,
            Entity parentEntity,
            List<EntityRelation> allRelations,
            EqlBuildResult result)
        {
            Entity currentEntity = parentEntity;

            foreach (var relInfo in rwfn.Relations)
            {
                var relation = allRelations.FirstOrDefault(
                    r => string.Equals(r.Name, relInfo.Name, StringComparison.OrdinalIgnoreCase));
                if (relation == null)
                {
                    result.Errors.Add(new EqlError { Message = $"Relation '{relInfo.Name}' not found." });
                    return;
                }

                Entity targetEntity;
                if (relation.OriginEntityId == currentEntity.Id)
                    targetEntity = GetEntityByIdSync(relation.TargetEntityId);
                else
                    targetEntity = GetEntityByIdSync(relation.OriginEntityId);

                if (targetEntity == null)
                {
                    result.Errors.Add(new EqlError { Message = $"Target entity for relation '{relInfo.Name}' not found." });
                    return;
                }

                // Create projection with all fields
                var existing = result.RelationProjections.FirstOrDefault(rp => rp.Relation.Id == relation.Id);
                if (existing == null)
                {
                    existing = new RelationProjection
                    {
                        Relation = relation,
                        RelationInfo = relInfo,
                        TargetEntity = targetEntity,
                        ParentEntity = currentEntity,
                        TargetFields = new List<Field>(targetEntity.Fields)
                    };
                    result.RelationProjections.Add(existing);
                }
                else
                {
                    // Add any missing fields
                    foreach (var field in targetEntity.Fields)
                    {
                        if (!existing.TargetFields.Any(f => f.Id == field.Id))
                            existing.TargetFields.Add(field);
                    }
                }

                // Build metadata for all fields
                var childMeta = targetEntity.Fields.Select(f => new EqlFieldMeta
                {
                    Name = f.Name,
                    Field = f,
                    Entity = targetEntity
                }).ToList();

                result.Meta.Add(new EqlFieldMeta
                {
                    Name = $"${relation.Name}",
                    Relation = relation,
                    Entity = targetEntity,
                    Children = childMeta
                });

                currentEntity = targetEntity;
            }
        }

        /// <summary>
        /// Translates a WHERE clause expression node into DynamoDB filter expression parts.
        /// </summary>
        private void TranslateWhereExpression(
            EqlNode node,
            Entity fromEntity,
            List<EntityRelation> relations,
            EqlBuildResult result,
            DynamoDbQuerySpec spec,
            List<string> filterParts)
        {
            if (node is not EqlExpressionNode expr)
                return;

            var op = expr.Operator.ToUpperInvariant();

            // Logical operators
            if (op == "AND" || op == "OR")
            {
                var leftParts = new List<string>();
                var rightParts = new List<string>();
                TranslateWhereExpression(expr.Left!, fromEntity, relations, result, spec, leftParts);
                TranslateWhereExpression(expr.Right!, fromEntity, relations, result, spec, rightParts);

                var leftStr = leftParts.Count > 0 ? string.Join(" AND ", leftParts) : null;
                var rightStr = rightParts.Count > 0 ? string.Join(" AND ", rightParts) : null;

                if (leftStr != null && rightStr != null)
                    filterParts.Add($"({leftStr} {op} {rightStr})");
                else if (leftStr != null)
                    filterParts.Add(leftStr);
                else if (rightStr != null)
                    filterParts.Add(rightStr);
                return;
            }

            // Extract field name and value from left/right operands
            string? fieldName = ExtractFieldName(expr.Left);
            var valueNode = expr.Right;

            // If field is on the right, swap
            if (fieldName == null)
            {
                fieldName = ExtractFieldName(expr.Right);
                valueNode = expr.Left;
            }

            if (fieldName == null)
            {
                _logger.LogWarning("Could not resolve field name in WHERE expression with operator {Op}", op);
                return;
            }

            // Resolve the attribute value
            var attrValue = ResolveOperandValue(valueNode, result.Parameters);
            string fieldAlias = RegisterFieldAlias(fieldName, spec);
            string valuePlaceholder = RegisterValuePlaceholder(attrValue, spec);

            // Translate operator to DynamoDB filter expression
            switch (op)
            {
                case "=":
                    if (IsNullValue(attrValue))
                        filterParts.Add($"(attribute_not_exists({fieldAlias}) OR {fieldAlias} = {valuePlaceholder})");
                    else
                        filterParts.Add($"{fieldAlias} = {valuePlaceholder}");
                    break;

                case "<>":
                case "!=":
                    if (IsNullValue(attrValue))
                        filterParts.Add($"(attribute_exists({fieldAlias}) AND {fieldAlias} <> {valuePlaceholder})");
                    else
                        filterParts.Add($"{fieldAlias} <> {valuePlaceholder}");
                    break;

                case "<":
                    filterParts.Add($"{fieldAlias} < {valuePlaceholder}");
                    break;

                case "<=":
                    filterParts.Add($"{fieldAlias} <= {valuePlaceholder}");
                    break;

                case ">":
                    filterParts.Add($"{fieldAlias} > {valuePlaceholder}");
                    break;

                case ">=":
                    filterParts.Add($"{fieldAlias} >= {valuePlaceholder}");
                    break;

                case "CONTAINS":
                    filterParts.Add($"contains({fieldAlias}, {valuePlaceholder})");
                    break;

                case "STARTSWITH":
                    filterParts.Add($"begins_with({fieldAlias}, {valuePlaceholder})");
                    break;

                case "~":
                    // Regex case-sensitive match — client-side post-filter
                    _logger.LogWarning("EQL REGEX operator (~) degraded to client-side filtering in DynamoDB mode");
                    result.RegexPostFilters.Add(new RegexPostFilter
                    {
                        FieldName = fieldName,
                        Pattern = ExtractStringValue(valueNode, result.Parameters),
                        CaseInsensitive = false,
                        Negate = false
                    });
                    break;

                case "~*":
                    // Regex case-insensitive match — client-side post-filter
                    _logger.LogWarning("EQL REGEX operator (~*) degraded to client-side filtering in DynamoDB mode");
                    result.RegexPostFilters.Add(new RegexPostFilter
                    {
                        FieldName = fieldName,
                        Pattern = ExtractStringValue(valueNode, result.Parameters),
                        CaseInsensitive = true,
                        Negate = false
                    });
                    break;

                case "!~":
                    _logger.LogWarning("EQL REGEX operator (!~) degraded to client-side filtering in DynamoDB mode");
                    result.RegexPostFilters.Add(new RegexPostFilter
                    {
                        FieldName = fieldName,
                        Pattern = ExtractStringValue(valueNode, result.Parameters),
                        CaseInsensitive = false,
                        Negate = true
                    });
                    break;

                case "!~*":
                    _logger.LogWarning("EQL REGEX operator (!~*) degraded to client-side filtering in DynamoDB mode");
                    result.RegexPostFilters.Add(new RegexPostFilter
                    {
                        FieldName = fieldName,
                        Pattern = ExtractStringValue(valueNode, result.Parameters),
                        CaseInsensitive = true,
                        Negate = true
                    });
                    break;

                case "@@":
                    // Full-text search — adapted to contains-based search
                    _logger.LogInformation("EQL FTS operator adapted to DynamoDB contains-based search");
                    var searchText = ExtractStringValue(valueNode, result.Parameters);
                    // Split into tokens and create contains() for each
                    var ftsTokens = searchText.Split(new[] { ' ', '\t', '\n', '\r' },
                        StringSplitOptions.RemoveEmptyEntries);
                    foreach (var ftsToken in ftsTokens)
                    {
                        string ftsValuePlaceholder = RegisterValuePlaceholder(
                            new AttributeValue { S = ftsToken.ToLowerInvariant() }, spec);
                        filterParts.Add($"contains({fieldAlias}, {ftsValuePlaceholder})");
                    }
                    if (ftsTokens.Length == 0)
                    {
                        result.FtsPostFilters.Add(new FtsPostFilter
                        {
                            FieldName = fieldName,
                            SearchText = searchText
                        });
                    }
                    break;

                default:
                    _logger.LogWarning("Not supported operator in abstract tree building: {Op}", op);
                    result.Errors.Add(new EqlError { Message = $"Not supported operator in abstract tree building." });
                    break;
            }
        }

        #endregion

        // =================================================================
        // Phase 7: Query Execution
        // =================================================================

        #region Query Execution

        /// <summary>
        /// Executes a build result against DynamoDB, applies client-side
        /// post-processing (regex, FTS, sorting, paging, distinct), and
        /// resolves relation projections.
        /// </summary>
        private async Task<QueryResult> ExecuteFromBuildResult(EqlBuildResult buildResult, EqlSettings settings)
        {
            if (buildResult.FromEntity == null || buildResult.DynamoDbQuery == null)
                throw new EqlException("Build result is incomplete — missing entity or query spec.");

            // Execute primary query via EntityQuery DSL
            var primaryQuery = new EntityQuery(buildResult.FromEntity.Name, "*", null, null, null, null);
            List<EntityRecord> records;

            try
            {
                records = await _recordRepository.Find(primaryQuery);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DynamoDB query execution failed for entity {Entity}", buildResult.FromEntity.Name);
                throw new EqlException($"Query execution failed: {ex.Message}", ex);
            }

            // Apply DynamoDB filter expression results are already filtered by repository
            // Apply client-side REGEX post-filters
            if (buildResult.RegexPostFilters.Count > 0)
            {
                records = ApplyRegexPostFilters(records, buildResult.RegexPostFilters);
            }

            // Apply client-side FTS post-filters
            if (buildResult.FtsPostFilters.Count > 0)
            {
                records = ApplyFtsPostFilters(records, buildResult.FtsPostFilters);
            }

            // Get total count before paging (for IncludeTotal)
            long totalCount = records.Count;
            if (settings.IncludeTotal)
            {
                // Total count is pre-filter/paging count
                totalCount = records.Count;
            }

            // Apply DISTINCT
            if (settings.Distinct)
            {
                records = records
                    .GroupBy(r => r.ContainsKey("id") ? r["id"]?.ToString() : Guid.NewGuid().ToString())
                    .Select(g => g.First())
                    .ToList();
                if (settings.IncludeTotal)
                    totalCount = records.Count;
            }

            // Apply ORDER BY (in-memory sort)
            if (buildResult.OrderByFields.Count > 0)
            {
                records = ApplyOrderBy(records, buildResult.OrderByFields, buildResult.Parameters);
            }

            // Apply PAGE/PAGESIZE (OFFSET emulation)
            if (buildResult.PageNumber.HasValue && buildResult.PageSizeValue.HasValue)
            {
                int skip = (buildResult.PageNumber.Value - 1) * buildResult.PageSizeValue.Value;
                int take = buildResult.PageSizeValue.Value;
                records = records.Skip(skip).Take(take).ToList();
            }

            // Resolve relation projections
            if (buildResult.RelationProjections.Count > 0)
            {
                await ResolveAllRelationProjections(buildResult.RelationProjections, records);
            }

            // Normalize geography field values — geography data is stored as raw GeoJSON/WKT
            // strings in DynamoDB (no PostGIS ST_ functions). Ensure empty defaults are applied.
            NormalizeGeographyFields(records, buildResult.FromEntity);

            // Build field metadata for projection
            var fieldsMeta = buildResult.Meta
                .Where(m => m.Field != null)
                .Select(m => m.Field!)
                .Distinct()
                .ToList();

            // If no specific fields were requested (wildcard), use all entity fields
            if (fieldsMeta.Count == 0 && buildResult.FromEntity != null)
                fieldsMeta = buildResult.FromEntity.Fields.ToList();

            var recordList = new EntityRecordList();
            recordList.AddRange(records);
            recordList.TotalCount = (int)totalCount;

            return new QueryResult
            {
                FieldsMeta = fieldsMeta,
                Data = recordList.Cast<EntityRecord>().ToList()
            };
        }

        /// <summary>
        /// Applies regex post-filters to records client-side.
        /// </summary>
        private List<EntityRecord> ApplyRegexPostFilters(List<EntityRecord> records, List<RegexPostFilter> filters)
        {
            foreach (var filter in filters)
            {
                var options = filter.CaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
                try
                {
                    var regex = new Regex(filter.Pattern, options);
                    records = records.Where(r =>
                    {
                        var val = r.ContainsKey(filter.FieldName) ? r[filter.FieldName]?.ToString() ?? string.Empty : string.Empty;
                        bool matches = regex.IsMatch(val);
                        return filter.Negate ? !matches : matches;
                    }).ToList();
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(ex, "Invalid regex pattern '{Pattern}' for field '{Field}'", filter.Pattern, filter.FieldName);
                }
            }
            return records;
        }

        /// <summary>
        /// Applies FTS post-filters using token-level contains matching.
        /// </summary>
        private List<EntityRecord> ApplyFtsPostFilters(List<EntityRecord> records, List<FtsPostFilter> filters)
        {
            foreach (var filter in filters)
            {
                var searchTokens = filter.SearchText
                    .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.ToLowerInvariant())
                    .ToArray();

                if (searchTokens.Length == 0) continue;

                records = records.Where(r =>
                {
                    var val = r.ContainsKey(filter.FieldName) ? r[filter.FieldName]?.ToString()?.ToLowerInvariant() ?? string.Empty : string.Empty;
                    return searchTokens.All(token => val.Contains(token));
                }).ToList();
            }
            return records;
        }

        /// <summary>
        /// Applies ORDER BY fields to records using in-memory LINQ sorting.
        /// Resolves argument-based direction from parameters.
        /// </summary>
        private List<EntityRecord> ApplyOrderBy(
            List<EntityRecord> records,
            List<EqlOrderByFieldNode> orderByFields,
            List<EqlParameter> parameters)
        {
            if (orderByFields.Count == 0 || records.Count <= 1)
                return records;

            IOrderedEnumerable<EntityRecord>? ordered = null;

            for (int i = 0; i < orderByFields.Count; i++)
            {
                var orderField = orderByFields[i];
                string direction = ResolveOrderDirection(orderField.Direction, parameters);
                bool descending = string.Equals(direction, "DESC", StringComparison.OrdinalIgnoreCase);

                Func<EntityRecord, object?> keySelector = r =>
                    r.ContainsKey(orderField.FieldName) ? r[orderField.FieldName] : null;

                if (i == 0)
                {
                    ordered = descending
                        ? records.OrderByDescending(keySelector, NullSafeComparer.Instance)
                        : records.OrderBy(keySelector, NullSafeComparer.Instance);
                }
                else
                {
                    ordered = descending
                        ? ordered!.ThenByDescending(keySelector, NullSafeComparer.Instance)
                        : ordered!.ThenBy(keySelector, NullSafeComparer.Instance);
                }
            }

            return ordered?.ToList() ?? records;
        }

        /// <summary>
        /// Resolves order direction — if it starts with @, looks up the parameter value.
        /// </summary>
        private static string ResolveOrderDirection(string direction, List<EqlParameter> parameters)
        {
            if (string.IsNullOrEmpty(direction)) return "ASC";
            if (direction.StartsWith("@"))
            {
                var param = parameters.FirstOrDefault(p =>
                    string.Equals(p.ParameterName, direction, StringComparison.OrdinalIgnoreCase));
                if (param?.Value != null)
                {
                    var val = param.Value.ToString()?.ToUpperInvariant();
                    return val == "DESC" ? "DESC" : "ASC";
                }
            }
            return direction.ToUpperInvariant() == "DESC" ? "DESC" : "ASC";
        }

        #endregion

        // =================================================================
        // Phase 8: Relation Projection Resolution
        // =================================================================

        #region Relation Projections

        /// <summary>
        /// Resolves all pending relation projections by executing separate
        /// DynamoDB queries and merging results into primary records.
        /// </summary>
        private async Task ResolveAllRelationProjections(
            List<RelationProjection> projections,
            List<EntityRecord> primaryRecords)
        {
            foreach (var projection in projections)
            {
                try
                {
                    var relatedRecords = await ResolveRelationProjection(projection, primaryRecords);

                    // Merge into primary records
                    string relKey = $"${projection.Relation.Name}";
                    foreach (var record in primaryRecords)
                    {
                        var recordId = record.ContainsKey("id") ? record["id"] : null;
                        if (recordId == null) continue;

                        Guid recordGuid;
                        if (recordId is Guid g)
                            recordGuid = g;
                        else if (!Guid.TryParse(recordId.ToString(), out recordGuid))
                            continue;

                        if (relatedRecords.TryGetValue(recordGuid, out var relRecords))
                        {
                            if (projection.Relation.RelationType == EntityRelationType.OneToOne ||
                                (projection.Relation.RelationType == EntityRelationType.OneToMany &&
                                 projection.Relation.OriginEntityId == projection.ParentEntity.Id))
                            {
                                // Return as list for consistency
                                record[relKey] = relRecords;
                            }
                            else
                            {
                                record[relKey] = relRecords;
                            }
                        }
                        else
                        {
                            record[relKey] = new List<EntityRecord>();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resolve relation projection for relation '{Relation}'",
                        projection.Relation.Name);
                }
            }
        }

        /// <summary>
        /// Resolves a single relation projection — executes separate DynamoDB
        /// queries for OneToOne/OneToMany (FK-based) or ManyToMany (junction-based).
        /// Source: EqlBuilder.Sql.cs OTM_RELATION_TEMPLATE and MTM_RELATION_TEMPLATE.
        /// </summary>
        private async Task<Dictionary<Guid, List<EntityRecord>>> ResolveRelationProjection(
            RelationProjection projection,
            List<EntityRecord> primaryRecords)
        {
            var results = new Dictionary<Guid, List<EntityRecord>>();

            if (projection.Relation.RelationType == EntityRelationType.ManyToMany)
            {
                // ManyToMany: query junction items, then batch fetch target records
                return await ResolveManyToManyProjection(projection, primaryRecords);
            }
            else
            {
                // OneToOne / OneToMany: FK-based lookup
                return await ResolveOneToManyProjection(projection, primaryRecords);
            }
        }

        /// <summary>
        /// Resolves OneToOne/OneToMany relation by collecting FK values from
        /// primary records and batch-querying the related entity's records.
        /// </summary>
        private async Task<Dictionary<Guid, List<EntityRecord>>> ResolveOneToManyProjection(
            RelationProjection projection,
            List<EntityRecord> primaryRecords)
        {
            var results = new Dictionary<Guid, List<EntityRecord>>();
            var relation = projection.Relation;

            // Determine which field in primary records holds the FK
            string primaryFkFieldName;
            string relatedFkFieldName;

            if (relation.OriginEntityId == projection.ParentEntity.Id)
            {
                primaryFkFieldName = relation.OriginFieldName;
                relatedFkFieldName = relation.TargetFieldName;
            }
            else
            {
                primaryFkFieldName = relation.TargetFieldName;
                relatedFkFieldName = relation.OriginFieldName;
            }

            // Collect all FK values from primary records
            var fkValues = new HashSet<string>();
            var recordFkMap = new Dictionary<string, List<Guid>>(); // fkValue -> list of primary record IDs

            foreach (var record in primaryRecords)
            {
                var recordId = record.ContainsKey("id") ? record["id"] : null;
                if (recordId == null) continue;

                Guid recordGuid;
                if (recordId is Guid g)
                    recordGuid = g;
                else if (!Guid.TryParse(recordId.ToString(), out recordGuid))
                    continue;

                var fkVal = record.ContainsKey(primaryFkFieldName) ? record[primaryFkFieldName]?.ToString() : null;
                if (fkVal == null) continue;

                fkValues.Add(fkVal);
                if (!recordFkMap.ContainsKey(fkVal))
                    recordFkMap[fkVal] = new List<Guid>();
                recordFkMap[fkVal].Add(recordGuid);
            }

            if (fkValues.Count == 0)
                return results;

            // Query related entity records
            var relatedEntityName = relation.OriginEntityId == projection.ParentEntity.Id
                ? relation.TargetEntityName
                : relation.OriginEntityName;

            foreach (var fkVal in fkValues)
            {
                if (Guid.TryParse(fkVal, out var fkGuid))
                {
                    var relatedRecord = await _recordRepository.FindRecord(relatedEntityName, fkGuid);
                    if (relatedRecord != null)
                    {
                        // Map back to all primary records with this FK value
                        if (recordFkMap.TryGetValue(fkVal, out var primaryIds))
                        {
                            foreach (var primaryId in primaryIds)
                            {
                                if (!results.ContainsKey(primaryId))
                                    results[primaryId] = new List<EntityRecord>();
                                results[primaryId].Add(relatedRecord);
                            }
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Resolves ManyToMany relation by querying junction items from
        /// EntityRepository.GetManyToManyRecords() and then batch-fetching
        /// target records.
        /// </summary>
        private async Task<Dictionary<Guid, List<EntityRecord>>> ResolveManyToManyProjection(
            RelationProjection projection,
            List<EntityRecord> primaryRecords)
        {
            var results = new Dictionary<Guid, List<EntityRecord>>();
            var relation = projection.Relation;

            // Collect primary record IDs
            var primaryIds = new List<Guid>();
            foreach (var record in primaryRecords)
            {
                var recordId = record.ContainsKey("id") ? record["id"] : null;
                if (recordId == null) continue;

                if (recordId is Guid g)
                    primaryIds.Add(g);
                else if (Guid.TryParse(recordId.ToString(), out var parsed))
                    primaryIds.Add(parsed);
            }

            if (primaryIds.Count == 0)
                return results;

            // Determine direction: are primary records origins or targets?
            bool primaryIsOrigin = relation.OriginEntityId == projection.ParentEntity.Id;

            // For each primary record, get M2M associations
            foreach (var primaryId in primaryIds)
            {
                try
                {
                    List<KeyValuePair<Guid, Guid>> associations;
                    if (primaryIsOrigin)
                        associations = await _entityRepository.GetManyToManyRecords(relation.Id, originId: primaryId);
                    else
                        associations = await _entityRepository.GetManyToManyRecords(relation.Id, targetId: primaryId);

                    var targetIds = primaryIsOrigin
                        ? associations.Select(a => a.Value).Distinct().ToList()
                        : associations.Select(a => a.Key).Distinct().ToList();

                    var targetEntityName = primaryIsOrigin
                        ? relation.TargetEntityName
                        : relation.OriginEntityName;

                    var relatedRecords = new List<EntityRecord>();
                    foreach (var targetId in targetIds)
                    {
                        var targetRecord = await _recordRepository.FindRecord(targetEntityName, targetId);
                        if (targetRecord != null)
                            relatedRecords.Add(targetRecord);
                    }

                    results[primaryId] = relatedRecords;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to resolve M2M projection for relation '{Relation}', record {RecordId}",
                        relation.Name, primaryId);
                    results[primaryId] = new List<EntityRecord>();
                }
            }

            return results;
        }

        #endregion

        // =================================================================
        // Phase 9-10: Helper Methods
        // =================================================================

        #region Helpers

        /// <summary>Normalizes parameter list, ensuring @ prefix on all names.</summary>
        private static List<EqlParameter> NormalizeParameters(List<EqlParameter>? parameters)
        {
            if (parameters == null)
                return new List<EqlParameter>();

            foreach (var p in parameters)
                p.Normalize();

            return parameters;
        }

        /// <summary>Resolves the FROM entity from the AST node.</summary>
        private Entity? ResolveFromEntity(EqlSelectNode selectNode, List<EqlError> errors)
        {
            if (selectNode.From == null || string.IsNullOrWhiteSpace(selectNode.From.EntityName))
            {
                errors.Add(new EqlError { Message = "FROM clause is missing or empty." });
                return null;
            }

            var entity = _entityService.GetEntity(selectNode.From.EntityName).GetAwaiter().GetResult();
            if (entity == null)
            {
                errors.Add(new EqlError { Message = $"Entity '{selectNode.From.EntityName}' not found" });
                return null;
            }

            return entity;
        }

        /// <summary>Resolves PAGE/PAGESIZE parameter values from AST and binds arguments.</summary>
        private static void ResolvePageParameters(EqlSelectNode selectNode, List<EqlParameter> parameters, EqlBuildResult result)
        {
            if (selectNode.Page != null)
            {
                if (selectNode.Page.Number.HasValue)
                {
                    result.PageNumber = (int)selectNode.Page.Number.Value;
                }
                else if (!string.IsNullOrEmpty(selectNode.Page.ArgumentName))
                {
                    var param = parameters.FirstOrDefault(p =>
                        string.Equals(p.ParameterName, selectNode.Page.ArgumentName, StringComparison.OrdinalIgnoreCase));
                    if (param?.Value != null)
                    {
                        try
                        {
                            result.PageNumber = Convert.ToInt32(param.Value);
                        }
                        catch
                        {
                            result.Errors.Add(new EqlError
                            {
                                Message = $"Invalid page value for parameter '{selectNode.Page.ArgumentName}'."
                            });
                        }
                    }
                }
            }

            if (selectNode.PageSize != null)
            {
                if (selectNode.PageSize.Number.HasValue)
                {
                    result.PageSizeValue = (int)selectNode.PageSize.Number.Value;
                }
                else if (!string.IsNullOrEmpty(selectNode.PageSize.ArgumentName))
                {
                    var param = parameters.FirstOrDefault(p =>
                        string.Equals(p.ParameterName, selectNode.PageSize.ArgumentName, StringComparison.OrdinalIgnoreCase));
                    if (param?.Value != null)
                    {
                        try
                        {
                            result.PageSizeValue = Convert.ToInt32(param.Value);
                        }
                        catch
                        {
                            result.Errors.Add(new EqlError
                            {
                                Message = $"Invalid page value for parameter '{selectNode.PageSize.ArgumentName}'."
                            });
                        }
                    }
                }
            }
        }

        /// <summary>Gets all entity relations synchronously for query translation.</summary>
        private List<EntityRelation> GetAllRelationsSync()
        {
            try
            {
                var response = _entityService.ReadRelations().GetAwaiter().GetResult();
                return response?.Object?.ToList() ?? new List<EntityRelation>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read relations during query translation");
                return new List<EntityRelation>();
            }
        }

        /// <summary>Gets an entity by ID synchronously.</summary>
        private Entity GetEntityByIdSync(Guid entityId)
        {
            try
            {
                var entity = _entityService.GetEntity(entityId).GetAwaiter().GetResult();
                return entity!;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read entity {EntityId}", entityId);
                return null!;
            }
        }

        /// <summary>Extracts a field name from an AST operand node.</summary>
        private static string? ExtractFieldName(EqlNode? node)
        {
            return node switch
            {
                EqlFieldNode fn => fn.FieldName,
                EqlRelationFieldNode rfn => rfn.FieldName,
                _ => null
            };
        }

        /// <summary>Resolves an AST operand node to a DynamoDB AttributeValue.</summary>
        private static AttributeValue ResolveOperandValue(EqlNode? node, List<EqlParameter> parameters)
        {
            return node switch
            {
                EqlTextValueNode tvn => new AttributeValue { S = tvn.Value },
                EqlNumberValueNode nvn => new AttributeValue { N = nvn.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                EqlKeywordNode kwn when kwn.Keyword == "NULL" => new AttributeValue { NULL = true },
                EqlKeywordNode kwn when kwn.Keyword == "TRUE" => new AttributeValue { BOOL = true },
                EqlKeywordNode kwn when kwn.Keyword == "FALSE" => new AttributeValue { BOOL = false },
                EqlArgumentValueNode avn => ResolveParameterValue(avn.ArgumentName, parameters),
                _ => new AttributeValue { NULL = true }
            };
        }

        /// <summary>Resolves a parameter argument to a DynamoDB AttributeValue.</summary>
        private static AttributeValue ResolveParameterValue(string argumentName, List<EqlParameter> parameters)
        {
            var param = parameters.FirstOrDefault(p =>
                string.Equals(p.ParameterName, argumentName, StringComparison.OrdinalIgnoreCase));
            if (param == null)
                return new AttributeValue { NULL = true };
            return param.ToAttributeValue();
        }

        /// <summary>Extracts a string value from an operand node for regex/FTS patterns.</summary>
        private static string ExtractStringValue(EqlNode? node, List<EqlParameter> parameters)
        {
            return node switch
            {
                EqlTextValueNode tvn => tvn.Value,
                EqlArgumentValueNode avn =>
                    parameters.FirstOrDefault(p =>
                        string.Equals(p.ParameterName, avn.ArgumentName, StringComparison.OrdinalIgnoreCase))
                        ?.Value?.ToString() ?? string.Empty,
                _ => string.Empty
            };
        }

        /// <summary>Checks if an AttributeValue represents null.</summary>
        private static bool IsNullValue(AttributeValue av)
        {
            return av.NULL;
        }

        /// <summary>
        /// Registers a field name alias in ExpressionAttributeNames and returns the alias.
        /// </summary>
        private string RegisterFieldAlias(string fieldName, DynamoDbQuerySpec spec)
        {
            string alias = "#f_" + fieldName.Replace("-", "_").Replace(".", "_");
            if (!spec.ExpressionAttributeNames.ContainsKey(alias))
                spec.ExpressionAttributeNames[alias] = fieldName;
            return alias;
        }

        /// <summary>
        /// Registers a value placeholder in ExpressionAttributeValues and returns the placeholder.
        /// </summary>
        private string RegisterValuePlaceholder(AttributeValue value, DynamoDbQuerySpec spec)
        {
            string placeholder = $":v{_attrCounter++}";
            spec.ExpressionAttributeValues[placeholder] = value;
            return placeholder;
        }

        /// <summary>Builds field metadata from an entity definition for a given fields string.</summary>
        private List<Field> BuildFieldsMetaFromEntity(Entity entity, string? fieldsStr)
        {
            if (string.IsNullOrEmpty(fieldsStr) || fieldsStr == "*")
                return entity.Fields.ToList();

            var fieldNames = fieldsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .ToList();

            var result = new List<Field>();
            foreach (var name in fieldNames)
            {
                var field = entity.Fields.FirstOrDefault(
                    f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field != null)
                    result.Add(field);
            }

            return result;
        }

        /// <summary>
        /// Normalizes geography field values in query results.
        /// In DynamoDB mode, geography data is stored as raw GeoJSON or WKT strings
        /// (no PostGIS ST_AsGeoJSON / ST_AsText needed). This method ensures that
        /// null or missing geography fields get proper empty default values.
        /// </summary>
        /// <remarks>
        /// Source: EqlBuilder.Sql.cs handled geography via ST_As{Format}() PostgreSQL functions.
        /// DynamoDB adaptation: geography is stored/returned as-is with empty defaults applied.
        /// </remarks>
        private static void NormalizeGeographyFields(List<EntityRecord> records, Entity? entity)
        {
            if (entity == null || records.Count == 0) return;

            // Identify geography fields by calling GetFieldType() on each field instance
            var geoFields = entity.Fields
                .Where(f => f.GetFieldType() == FieldType.GeographyField)
                .ToList();

            if (geoFields.Count == 0) return;

            const string emptyGeoJson = "{\"type\":\"GeometryCollection\",\"geometries\":[]}";

            foreach (var record in records)
            {
                foreach (var geoField in geoFields)
                {
                    if (!record.ContainsKey(geoField.Name) || record[geoField.Name] == null
                        || (record[geoField.Name] is string s && string.IsNullOrWhiteSpace(s)))
                    {
                        record[geoField.Name] = emptyGeoJson;
                    }
                }
            }
        }

        /// <summary>
        /// Builds field metadata for the projection list, identifying and marking
        /// relation fields with the <see cref="RelationFieldMeta"/> type so that
        /// downstream consumers can distinguish embedded relation data from scalar fields.
        /// </summary>
        private static List<Field> BuildRelationAwareFieldsMeta(
            List<EqlFieldMeta> meta, Entity? fromEntity)
        {
            var result = new List<Field>();

            foreach (var m in meta)
            {
                if (m.Field != null)
                {
                    result.Add(m.Field);
                }
                else if (m.Relation != null && m.Entity != null && m.Children.Count > 0)
                {
                    // This is a relation projection — wrap as RelationFieldMeta
                    // so downstream consumers can distinguish embedded relation data
                    var relFieldMeta = new RelationFieldMeta
                    {
                        Id = Guid.NewGuid(),
                        Name = m.Name,
                        Relation = m.Relation,
                        Entity = m.Entity,
                        Fields = m.Children
                            .Where(c => c.Field != null)
                            .Select(c => c.Field!)
                            .ToList()
                    };
                    result.Add(relFieldMeta);
                }
            }

            if (result.Count == 0 && fromEntity != null)
                result.AddRange(fromEntity.Fields);

            return result;
        }

        /// <summary>Truncates EQL text for log output to prevent excessive log sizes.</summary>
        private static string TruncateForLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length > 500 ? text[..500] + "..." : text;
        }

        #endregion

        // =================================================================
        // Internal Comparer for null-safe sorting
        // =================================================================

        /// <summary>
        /// Null-safe comparer for ORDER BY sorting that handles mixed types
        /// and null values gracefully.
        /// </summary>
        private sealed class NullSafeComparer : IComparer<object?>
        {
            public static readonly NullSafeComparer Instance = new();

            public int Compare(object? x, object? y)
            {
                if (x == null && y == null) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                if (x is IComparable cx && y is IComparable cy)
                {
                    try
                    {
                        return cx.CompareTo(cy);
                    }
                    catch
                    {
                        // Fall back to string comparison
                        return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
                    }
                }

                return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
            }
        }
    }
}
