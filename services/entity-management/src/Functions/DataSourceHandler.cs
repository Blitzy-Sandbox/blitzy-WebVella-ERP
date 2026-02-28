using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;

// Note: Assembly-level [LambdaSerializer(typeof(DefaultLambdaJsonSerializer))] attribute
// is already defined in EntityHandler.cs — only one per assembly is allowed.

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler for datasource management and execution in the Entity Management bounded context.
    /// Replaces the datasource endpoints from <c>WebApiController.cs</c> (routes <c>api/v3/en_US/eql</c>,
    /// <c>api/v3/en_US/eql-ds</c>, <c>api/v3/en_US/eql-ds-select2</c>) and transforms the
    /// <c>DataSourceManager.cs</c> business logic into a serverless Lambda function.
    ///
    /// The key architectural change is replacing the EQL-to-PostgreSQL SQL translation pipeline
    /// (Irony grammar → AST → SQL via <c>EqlBuilder</c>/<c>EqlCommand</c>) with the DynamoDB-backed
    /// <see cref="IQueryAdapter"/> that translates EQL-like queries into DynamoDB operations.
    ///
    /// Route mapping (API Gateway HTTP API v2):
    ///   GET    /v1/entity-management/datasources                → GetDataSources
    ///   GET    /v1/entity-management/datasources/{idOrName}     → GetDataSource
    ///   POST   /v1/entity-management/datasources                → CreateDataSource
    ///   PUT    /v1/entity-management/datasources/{id}           → UpdateDataSource
    ///   DELETE /v1/entity-management/datasources/{id}           → DeleteDataSource
    ///   POST   /v1/entity-management/datasources/{id}/execute   → ExecuteDataSource (stored)
    ///   POST   /v1/entity-management/eql                        → ExecuteDataSource (ad-hoc EQL)
    ///   POST   /v1/entity-management/eql-ds                     → ExecuteDataSource (named datasource)
    ///   POST   /v1/entity-management/eql-ds-select2             → ExecuteDataSourceSelect2
    ///
    /// Authorization: Mutation endpoints require administrator role (JWT claims check).
    ///                Execution endpoints require authenticated user with Read permission.
    /// Events: All mutations publish SNS domain events (entity-management.datasource.{action}).
    /// Caching: Datasource list uses IMemoryCache with 1-hour TTL matching source pattern.
    /// </summary>
    public class DataSourceHandler
    {
        // ─── Dependencies (injected via constructor DI) ───────────────────

        /// <summary>EQL-like query adapter translating to DynamoDB operations.</summary>
        private readonly IQueryAdapter _queryAdapter;

        /// <summary>Entity service for metadata resolution during datasource validation.</summary>
        private readonly IEntityService _entityService;

        /// <summary>DynamoDB client for direct datasource definition persistence.</summary>
        private readonly IAmazonDynamoDB _dynamoDbClient;

        /// <summary>SNS client for publishing domain events after mutations.</summary>
        private readonly IAmazonSimpleNotificationService _snsClient;

        /// <summary>In-process cache for datasource list with 1-hour TTL.</summary>
        private readonly IMemoryCache _cache;

        /// <summary>Structured JSON logger with correlation-ID propagation.</summary>
        private readonly ILogger<DataSourceHandler> _logger;

        // ─── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// DynamoDB table name for datasource definitions. Retrieved from DATASOURCE_TABLE_NAME
        /// environment variable. Defaults to "entity-management-datasources".
        /// </summary>
        private readonly string _datasourceTableName;

        /// <summary>
        /// SNS topic ARN for datasource domain events. Retrieved from DATASOURCE_TOPIC_ARN
        /// environment variable. When empty/null, event publishing is skipped with a warning log.
        /// </summary>
        private readonly string? _datasourceTopicArn;

        /// <summary>
        /// When true, detailed exception messages and stack traces are included in error responses.
        /// Controlled by IS_LOCAL environment variable.
        /// </summary>
        private readonly bool _isDevelopmentMode;

        // ─── Constants ────────────────────────────────────────────────────

        /// <summary>Cache key for the merged datasource list (code + database).</summary>
        private const string CACHE_KEY = "DATASOURCES_ALL";

        /// <summary>Cache TTL in hours matching source DataSourceManager.cs pattern.</summary>
        private const double CACHE_TTL_HOURS = 1.0;

        /// <summary>DynamoDB partition key attribute name.</summary>
        private const string PK_ATTR = "PK";

        /// <summary>DynamoDB sort key attribute name.</summary>
        private const string SK_ATTR = "SK";

        /// <summary>Fixed partition key value for all datasource items.</summary>
        private const string DATASOURCE_PK = "DATASOURCE";

        /// <summary>Default page size for Select2 queries (source: WebApiController.cs line ~300).</summary>
        private const int SELECT2_PAGE_SIZE = 10;

        // ─── JSON Serialization Options ───────────────────────────────────

        /// <summary>
        /// Shared JsonSerializerOptions for System.Text.Json deserialization/serialization.
        /// PropertyNameCaseInsensitive ensures flexible request body parsing.
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ─── Constructor ──────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the DataSourceHandler with all required dependencies.
        /// Dependencies are resolved via the Lambda service provider (configured in Startup/Program).
        /// </summary>
        /// <param name="queryAdapter">EQL-like query adapter replacing EqlBuilder + EqlCommand.</param>
        /// <param name="entityService">Entity metadata service for validation lookups.</param>
        /// <param name="dynamoDbClient">DynamoDB client for datasource definition persistence.</param>
        /// <param name="snsClient">SNS client for publishing domain events.</param>
        /// <param name="cache">In-memory cache for datasource list with 1-hour TTL.</param>
        /// <param name="logger">Structured JSON logger with correlation-ID propagation.</param>
        public DataSourceHandler(
            IQueryAdapter queryAdapter,
            IEntityService entityService,
            IAmazonDynamoDB dynamoDbClient,
            IAmazonSimpleNotificationService snsClient,
            IMemoryCache cache,
            ILogger<DataSourceHandler> logger)
        {
            _queryAdapter = queryAdapter ?? throw new ArgumentNullException(nameof(queryAdapter));
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _datasourceTableName = Environment.GetEnvironmentVariable("DATASOURCE_TABLE_NAME")
                                   ?? "entity-management-datasources";
            _datasourceTopicArn = Environment.GetEnvironmentVariable("DATASOURCE_TOPIC_ARN");
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
        }

        // ═════════════════════════════════════════════════════════════════
        // PUBLIC LAMBDA HANDLER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lambda handler for GET /v1/entity-management/datasources.
        /// Returns a list of all registered datasources (both code-discovered and database-persisted).
        /// Source: DataSourceManager.GetAll() — merges code + DB datasources, cached with 1-hour TTL.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetDataSources(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "GetDataSources started. CorrelationId={CorrelationId}, FunctionName={FunctionName}",
                correlationId, context.FunctionName);

            try
            {
                if (!HasPermission(request, EntityPermission.Read))
                {
                    _logger.LogWarning(
                        "GetDataSources access denied. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Read permission required.",
                        correlationId);
                }

                var allDataSources = await GetAllDataSourcesCached();

                // Optionally enrich with entity validation. Uses ReadEntities for batch lookup.
                var entitiesResponse = await _entityService.ReadEntities();
                var knownEntityNames = entitiesResponse.Success && entitiesResponse.Object != null
                    ? entitiesResponse.Object.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Log any datasources referencing entities that no longer exist.
                foreach (var ds in allDataSources.OfType<DatabaseDataSource>())
                {
                    if (!string.IsNullOrWhiteSpace(ds.EntityName) && !knownEntityNames.Contains(ds.EntityName))
                    {
                        _logger.LogWarning(
                            "Datasource '{DsName}' references non-existent entity '{EntityName}'.",
                            ds.Name, ds.EntityName);
                    }
                }

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Object = allDataSources
                };

                _logger.LogInformation(
                    "GetDataSources completed. Count={Count}, CorrelationId={CorrelationId}",
                    allDataSources.Count, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetDataSources error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for GET /v1/entity-management/datasources/{idOrName}.
        /// Returns a single datasource by its GUID identifier or programmatic name.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> GetDataSource(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "GetDataSource started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                if (!HasPermission(request, EntityPermission.Read))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Read permission required.",
                        correlationId);
                }

                // Extract route parameter — can be a Guid ID or a string name.
                string? idOrName = null;
                request.PathParameters?.TryGetValue("idOrName", out idOrName);
                if (string.IsNullOrWhiteSpace(idOrName))
                {
                    request.PathParameters?.TryGetValue("id", out idOrName);
                }

                if (string.IsNullOrWhiteSpace(idOrName))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Datasource ID or name is required.",
                        correlationId);
                }

                var allDataSources = await GetAllDataSourcesCached();

                DataSourceBase? found = null;
                if (Guid.TryParse(idOrName, out var dsId))
                {
                    found = allDataSources.FirstOrDefault(ds => ds.Id == dsId);
                }

                // Fall back to name lookup if not found by ID.
                if (found == null)
                {
                    found = allDataSources.FirstOrDefault(ds =>
                        string.Equals(ds.Name, idOrName, StringComparison.OrdinalIgnoreCase));
                }

                if (found == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Datasource '{idOrName}' not found.",
                        correlationId);
                }

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Object = found
                };

                _logger.LogInformation(
                    "GetDataSource completed. Id={DataSourceId}, Name={DataSourceName}, CorrelationId={CorrelationId}",
                    found.Id, found.Name, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetDataSource error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/datasources.
        /// Creates a new DatabaseDataSource definition with EQL validation, parameter parsing,
        /// DynamoDB persistence, cache invalidation, and SNS event publishing.
        /// Source: DataSourceManager.Create() + ProcessParametersText + ProcessFieldsMeta.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> CreateDataSource(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "CreateDataSource started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                // Admin-only operation (matches monolith's permission model).
                if (!HasPermission(request, EntityPermission.Create))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator permission required to create datasources.",
                        correlationId);
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                DatabaseDataSource? dsInput;
                try
                {
                    dsInput = JsonConvert.DeserializeObject<DatabaseDataSource>(request.Body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "CreateDataSource body parse failed. CorrelationId={CorrelationId}", correlationId);
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid request body: " + ex.Message,
                        correlationId);
                }

                if (dsInput == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Datasource definition is required.",
                        correlationId);
                }

                var errors = new List<ErrorModel>();

                // Validate name uniqueness.
                if (string.IsNullOrWhiteSpace(dsInput.Name))
                {
                    errors.Add(new ErrorModel { Key = "name", Value = "", Message = "Datasource name is required." });
                }
                else
                {
                    var existing = await GetAllDataSourcesCached();
                    if (existing.Any(ds => string.Equals(ds.Name, dsInput.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new ErrorModel
                        {
                            Key = "name",
                            Value = dsInput.Name,
                            Message = $"Datasource with name '{dsInput.Name}' already exists."
                        });
                    }
                }

                // Validate EQL text.
                if (string.IsNullOrWhiteSpace(dsInput.EqlText))
                {
                    errors.Add(new ErrorModel { Key = "eqlText", Value = "", Message = "EQL text is required." });
                }

                if (errors.Any())
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest, errors, correlationId);
                }

                // Parse parameter definitions from newline-separated text format.
                var parsedParams = ProcessParametersText(dsInput);

                // Validate EQL text via IQueryAdapter.Build().
                var eqlParams = parsedParams
                    .Select(ConvertDataSourceParameterToEqlParameter)
                    .ToList();

                var eqlSettings = new EqlSettings { IncludeTotal = dsInput.ReturnTotal };
                var buildResult = _queryAdapter.Build(dsInput.EqlText, eqlParams, eqlSettings);

                if (buildResult.Errors != null && buildResult.Errors.Any())
                {
                    foreach (var eqlError in buildResult.Errors)
                    {
                        var lineInfo = eqlError.Line.HasValue && eqlError.Column.HasValue
                            ? $" (line {eqlError.Line}, column {eqlError.Column})"
                            : "";
                        errors.Add(new ErrorModel
                        {
                            Key = "eql",
                            Value = "",
                            Message = eqlError.Message + lineInfo
                        });
                    }
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest, errors, correlationId);
                }

                // Check that all declared parameters are referenced in EQL.
                if (buildResult.ExpectedParameters != null)
                {
                    foreach (var expectedParam in buildResult.ExpectedParameters)
                    {
                        if (!parsedParams.Any(p =>
                            string.Equals("@" + p.Name, expectedParam, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.Name, expectedParam, StringComparison.OrdinalIgnoreCase)))
                        {
                            errors.Add(new ErrorModel
                            {
                                Key = "parameter",
                                Value = expectedParam,
                                Message = $"EQL references parameter '{expectedParam}' which is not declared."
                            });
                        }
                    }
                }

                if (errors.Any())
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest, errors, correlationId);
                }

                // Process field metadata from build result.
                if (buildResult.Meta != null && buildResult.Meta.Any())
                {
                    dsInput.Fields.Clear();
                    dsInput.Fields.AddRange(ProcessFieldsMetaList(buildResult.Meta));
                }

                // Resolve and validate the entity referenced in the EQL query.
                if (buildResult.FromEntity != null && !string.IsNullOrWhiteSpace(buildResult.FromEntity.Name))
                {
                    dsInput.EntityName = buildResult.FromEntity.Name;

                    // Verify entity exists in the metadata store via IEntityService.
                    var resolvedEntity = await _entityService.GetEntity(buildResult.FromEntity.Name);
                    if (resolvedEntity == null)
                    {
                        errors.Add(new ErrorModel
                        {
                            Key = "entity",
                            Value = buildResult.FromEntity.Name,
                            Message = $"Entity '{buildResult.FromEntity.Name}' referenced in EQL does not exist."
                        });
                        return BuildErrorResponse(
                            (int)HttpStatusCode.BadRequest, errors, correlationId);
                    }
                }

                // Assign a new ID if not already set.
                if (dsInput.Id == Guid.Empty)
                {
                    dsInput.Id = Guid.NewGuid();
                }
                // Type is read-only on DatabaseDataSource (always returns DATABASE).

                // Persist to DynamoDB.
                await SaveDataSource(dsInput);

                // Invalidate cache.
                _cache.Remove(CACHE_KEY);

                _logger.LogInformation(
                    "CreateDataSource completed. Id={DataSourceId}, Name={DataSourceName}, CorrelationId={CorrelationId}",
                    dsInput.Id, dsInput.Name, correlationId);

                // Publish domain event.
                await PublishDomainEvent(
                    "entity-management.datasource.created",
                    new { DataSourceId = dsInput.Id, DataSourceName = dsInput.Name },
                    correlationId);

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Datasource created successfully.",
                    Object = dsInput
                };

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CreateDataSource error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for PUT /v1/entity-management/datasources/{id}.
        /// Updates an existing DatabaseDataSource definition. Performs the same EQL validation
        /// pipeline as CreateDataSource, with additional name-uniqueness check excluding self.
        /// Source: DataSourceManager.Update().
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> UpdateDataSource(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "UpdateDataSource started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                if (!HasPermission(request, EntityPermission.Update))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator permission required to update datasources.",
                        correlationId);
                }

                // Extract datasource ID from route.
                string? idStr = null;
                request.PathParameters?.TryGetValue("id", out idStr);
                if (!Guid.TryParse(idStr, out var dsId) || dsId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "A valid datasource ID is required.",
                        correlationId);
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                DatabaseDataSource? dsInput;
                try
                {
                    dsInput = JsonConvert.DeserializeObject<DatabaseDataSource>(request.Body);
                }
                catch (Exception ex)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid request body: " + ex.Message,
                        correlationId);
                }

                if (dsInput == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Datasource definition is required.",
                        correlationId);
                }

                // Verify the datasource exists.
                var allDataSources = await GetAllDataSourcesCached();
                var existingDs = allDataSources.FirstOrDefault(ds => ds.Id == dsId);
                if (existingDs == null || existingDs is not DatabaseDataSource)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"DatabaseDataSource with ID '{dsId}' not found.",
                        correlationId);
                }

                dsInput.Id = dsId;
                // Type is read-only on DatabaseDataSource (always returns DATABASE).

                var errors = new List<ErrorModel>();

                // Validate name uniqueness excluding self.
                if (string.IsNullOrWhiteSpace(dsInput.Name))
                {
                    errors.Add(new ErrorModel { Key = "name", Value = "", Message = "Datasource name is required." });
                }
                else
                {
                    if (allDataSources.Any(ds =>
                        ds.Id != dsId &&
                        string.Equals(ds.Name, dsInput.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new ErrorModel
                        {
                            Key = "name",
                            Value = dsInput.Name,
                            Message = $"Another datasource with name '{dsInput.Name}' already exists."
                        });
                    }
                }

                // Validate EQL text.
                if (string.IsNullOrWhiteSpace(dsInput.EqlText))
                {
                    errors.Add(new ErrorModel { Key = "eqlText", Value = "", Message = "EQL text is required." });
                }

                if (errors.Any())
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest, errors, correlationId);
                }

                // Parse parameter definitions.
                var parsedParams = ProcessParametersText(dsInput);

                // Validate EQL via IQueryAdapter.Build().
                var eqlParams = parsedParams
                    .Select(ConvertDataSourceParameterToEqlParameter)
                    .ToList();

                var eqlSettings = new EqlSettings { IncludeTotal = dsInput.ReturnTotal };
                var buildResult = _queryAdapter.Build(dsInput.EqlText, eqlParams, eqlSettings);

                if (buildResult.Errors != null && buildResult.Errors.Any())
                {
                    foreach (var eqlError in buildResult.Errors)
                    {
                        var lineInfo = eqlError.Line.HasValue && eqlError.Column.HasValue
                            ? $" (line {eqlError.Line}, column {eqlError.Column})"
                            : "";
                        errors.Add(new ErrorModel
                        {
                            Key = "eql",
                            Value = "",
                            Message = eqlError.Message + lineInfo
                        });
                    }
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest, errors, correlationId);
                }

                // Check EQL-referenced parameter availability.
                if (buildResult.ExpectedParameters != null)
                {
                    foreach (var expectedParam in buildResult.ExpectedParameters)
                    {
                        if (!parsedParams.Any(p =>
                            string.Equals("@" + p.Name, expectedParam, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.Name, expectedParam, StringComparison.OrdinalIgnoreCase)))
                        {
                            errors.Add(new ErrorModel
                            {
                                Key = "parameter",
                                Value = expectedParam,
                                Message = $"EQL references parameter '{expectedParam}' which is not declared."
                            });
                        }
                    }
                }

                if (errors.Any())
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest, errors, correlationId);
                }

                // Process field metadata.
                if (buildResult.Meta != null && buildResult.Meta.Any())
                {
                    dsInput.Fields.Clear();
                    dsInput.Fields.AddRange(ProcessFieldsMetaList(buildResult.Meta));
                }

                if (buildResult.FromEntity != null && !string.IsNullOrWhiteSpace(buildResult.FromEntity.Name))
                {
                    dsInput.EntityName = buildResult.FromEntity.Name;

                    // Verify entity exists in the metadata store via IEntityService.
                    var resolvedEntity = await _entityService.GetEntity(buildResult.FromEntity.Name);
                    if (resolvedEntity == null)
                    {
                        errors.Add(new ErrorModel
                        {
                            Key = "entity",
                            Value = buildResult.FromEntity.Name,
                            Message = $"Entity '{buildResult.FromEntity.Name}' referenced in EQL does not exist."
                        });
                        return BuildErrorResponse(
                            (int)HttpStatusCode.BadRequest, errors, correlationId);
                    }
                }

                // Persist updated definition.
                await SaveDataSource(dsInput);
                _cache.Remove(CACHE_KEY);

                _logger.LogInformation(
                    "UpdateDataSource completed. Id={DataSourceId}, Name={DataSourceName}, CorrelationId={CorrelationId}",
                    dsInput.Id, dsInput.Name, correlationId);

                await PublishDomainEvent(
                    "entity-management.datasource.updated",
                    new { DataSourceId = dsInput.Id, DataSourceName = dsInput.Name },
                    correlationId);

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Datasource updated successfully.",
                    Object = dsInput
                };

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UpdateDataSource error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for DELETE /v1/entity-management/datasources/{id}.
        /// Deletes a datasource definition from DynamoDB, invalidates cache,
        /// and publishes an SNS deletion event.
        /// Source: DataSourceManager.Delete().
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> DeleteDataSource(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "DeleteDataSource started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                if (!HasPermission(request, EntityPermission.Delete))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Administrator permission required to delete datasources.",
                        correlationId);
                }

                string? idStr = null;
                request.PathParameters?.TryGetValue("id", out idStr);
                if (!Guid.TryParse(idStr, out var dsId) || dsId == Guid.Empty)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "A valid datasource ID is required.",
                        correlationId);
                }

                // Verify existence.
                var allDataSources = await GetAllDataSourcesCached();
                var existingDs = allDataSources.FirstOrDefault(ds => ds.Id == dsId);
                if (existingDs == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Datasource with ID '{dsId}' not found.",
                        correlationId);
                }

                if (existingDs is not DatabaseDataSource)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Only DatabaseDataSource definitions can be deleted. CodeDataSource instances are code-registered.",
                        correlationId);
                }

                await DeleteDataSourceFromDynamo(dsId);
                _cache.Remove(CACHE_KEY);

                _logger.LogInformation(
                    "DeleteDataSource completed. Id={DataSourceId}, Name={DataSourceName}, CorrelationId={CorrelationId}",
                    dsId, existingDs.Name, correlationId);

                await PublishDomainEvent(
                    "entity-management.datasource.deleted",
                    new { DataSourceId = dsId, DataSourceName = existingDs.Name },
                    correlationId);

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Datasource deleted successfully."
                };

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeleteDataSource error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/datasources/{id}/execute,
        /// POST /v1/entity-management/eql, and POST /v1/entity-management/eql-ds.
        /// Executes a stored DatabaseDataSource or ad-hoc EQL query.
        /// Two execution modes (from source DataSourceManager.Execute):
        ///   1. Stored datasource execution (by ID or name with runtime parameter overrides)
        ///   2. Ad-hoc EQL execution (raw EQL text + parameters + returnTotal flag)
        /// For DatabaseDataSource: translates EQL to DynamoDB query via IQueryAdapter.Execute.
        /// For CodeDataSource: calls CodeDataSource.Execute(Dictionary) directly.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ExecuteDataSource(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "ExecuteDataSource started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                if (!HasPermission(request, EntityPermission.Read))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Read permission required to execute datasources.",
                        correlationId);
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                // Determine execution mode based on route.
                string? dsIdStr = null;
                request.PathParameters?.TryGetValue("id", out dsIdStr);

                // Try to parse body as a generic JSON object to determine request type.
                Dictionary<string, object?>? bodyDict;
                try
                {
                    bodyDict = JsonConvert.DeserializeObject<Dictionary<string, object?>>(request.Body);
                }
                catch
                {
                    bodyDict = null;
                }

                // Mode 1: Stored datasource execution by ID from route.
                if (!string.IsNullOrWhiteSpace(dsIdStr) && Guid.TryParse(dsIdStr, out var routeDsId))
                {
                    return await ExecuteStoredDataSource(routeDsId, bodyDict, correlationId);
                }

                // Mode 2: Named datasource execution (eql-ds endpoint).
                if (bodyDict != null && bodyDict.ContainsKey("name"))
                {
                    var dsName = bodyDict["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(dsName))
                    {
                        return BuildErrorResponse(
                            (int)HttpStatusCode.BadRequest,
                            "Datasource name is required.",
                            correlationId);
                    }

                    var allDs = await GetAllDataSourcesCached();
                    var namedDs = allDs.FirstOrDefault(ds =>
                        string.Equals(ds.Name, dsName, StringComparison.OrdinalIgnoreCase));

                    if (namedDs == null)
                    {
                        return BuildErrorResponse(
                            (int)HttpStatusCode.NotFound,
                            $"Datasource '{dsName}' not found.",
                            correlationId);
                    }

                    // Extract parameter overrides from body.
                    Dictionary<string, object?>? paramOverrides = null;
                    if (bodyDict.ContainsKey("parameters") && bodyDict["parameters"] != null)
                    {
                        try
                        {
                            paramOverrides = JsonConvert.DeserializeObject<Dictionary<string, object?>>(
                                bodyDict["parameters"]!.ToString()!);
                        }
                        catch
                        {
                            paramOverrides = new Dictionary<string, object?>();
                        }
                    }

                    return await ExecuteDataSourceByBase(namedDs, paramOverrides, correlationId);
                }

                // Mode 3: Ad-hoc EQL execution (eql endpoint).
                if (bodyDict != null && bodyDict.ContainsKey("eql"))
                {
                    var eqlText = bodyDict["eql"]?.ToString();
                    if (string.IsNullOrWhiteSpace(eqlText))
                    {
                        return BuildErrorResponse(
                            (int)HttpStatusCode.BadRequest,
                            "EQL text is required.",
                            correlationId);
                    }

                    var parametersText = bodyDict.ContainsKey("parameters")
                        ? bodyDict["parameters"]?.ToString() ?? ""
                        : "";
                    var returnTotal = bodyDict.ContainsKey("returnTotal") &&
                        bool.TryParse(bodyDict["returnTotal"]?.ToString(), out var rt) && rt;

                    return await ExecuteAdHocEql(eqlText, parametersText, returnTotal, correlationId);
                }

                return BuildErrorResponse(
                    (int)HttpStatusCode.BadRequest,
                    "Invalid request. Provide either 'eql' for ad-hoc execution, 'name' for named datasource, or use the /datasources/{id}/execute route.",
                    correlationId);
            }
            catch (EqlException eqlEx)
            {
                _logger.LogWarning(eqlEx,
                    "ExecuteDataSource EQL error. CorrelationId={CorrelationId}", correlationId);
                var eqlErrors = eqlEx.Errors?.Select(e => new ErrorModel
                {
                    Key = "eql",
                    Value = "",
                    Message = e.Message
                }).ToList() ?? new List<ErrorModel>
                {
                    new ErrorModel { Key = "eql", Value = "", Message = eqlEx.Message }
                };
                return BuildErrorResponse(
                    (int)HttpStatusCode.BadRequest, eqlErrors, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ExecuteDataSource error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        /// <summary>
        /// Lambda handler for POST /v1/entity-management/eql-ds-select2.
        /// Executes a datasource and normalizes results into Select2-compatible
        /// {id, text} format with pagination support.
        /// Source: WebApiController.DataSourceQueryActionForSelect2().
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ExecuteDataSourceSelect2(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation(
                "ExecuteDataSourceSelect2 started. CorrelationId={CorrelationId}", correlationId);

            try
            {
                if (!HasPermission(request, EntityPermission.Read))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.Forbidden,
                        "Access denied. Read permission required.",
                        correlationId);
                }

                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                Dictionary<string, object?>? bodyDict;
                try
                {
                    bodyDict = JsonConvert.DeserializeObject<Dictionary<string, object?>>(request.Body);
                }
                catch
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Invalid request body format.",
                        correlationId);
                }

                if (bodyDict == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Request body is required.",
                        correlationId);
                }

                var dsName = bodyDict.ContainsKey("name") ? bodyDict["name"]?.ToString() : null;
                if (string.IsNullOrWhiteSpace(dsName))
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Datasource name is required for Select2 queries.",
                        correlationId);
                }

                var allDs = await GetAllDataSourcesCached();
                var dataSource = allDs.FirstOrDefault(ds =>
                    string.Equals(ds.Name, dsName, StringComparison.OrdinalIgnoreCase));

                if (dataSource == null)
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.NotFound,
                        $"Datasource '{dsName}' not found.",
                        correlationId);
                }

                // Extract page number for pagination.
                int page = 1;
                if (bodyDict.ContainsKey("page") && int.TryParse(bodyDict["page"]?.ToString(), out var parsedPage))
                {
                    page = Math.Max(1, parsedPage);
                }

                // Extract parameter overrides.
                Dictionary<string, object?>? paramOverrides = null;
                if (bodyDict.ContainsKey("parameters") && bodyDict["parameters"] != null)
                {
                    try
                    {
                        paramOverrides = JsonConvert.DeserializeObject<Dictionary<string, object?>>(
                            bodyDict["parameters"]!.ToString()!);
                    }
                    catch
                    {
                        paramOverrides = new Dictionary<string, object?>();
                    }
                }

                // Execute the datasource.
                EntityRecordList? records = null;
                if (dataSource is DatabaseDataSource dbDs)
                {
                    var eqlParams = BuildRuntimeParameters(dbDs, paramOverrides);
                    var eqlSettings = new EqlSettings { IncludeTotal = true };
                    var queryResult = await _queryAdapter.Execute(dbDs.EqlText, eqlParams, eqlSettings);

                    records = new EntityRecordList();
                    if (queryResult.Data != null)
                    {
                        records.AddRange(queryResult.Data);
                    }
                    records.TotalCount = queryResult.Data?.Count ?? 0;
                }
                else if (dataSource is CodeDataSource codeDs)
                {
                    var execArgs = BuildCodeDataSourceArgs(paramOverrides);
                    var codeResult = codeDs.Execute(execArgs);
                    records = ConvertToEntityRecordList(codeResult);
                }
                else
                {
                    return BuildErrorResponse(
                        (int)HttpStatusCode.BadRequest,
                        "Unsupported datasource type.",
                        correlationId);
                }

                // Transform records into Select2 {id, text} format.
                var select2Results = new List<object>();
                if (records != null)
                {
                    foreach (var rec in records)
                    {
                        var idVal = rec["id"]?.ToString() ?? "";
                        var textVal = rec.ContainsKey("text")
                            ? rec["text"]?.ToString() ?? ""
                            : rec.ContainsKey("label")
                                ? rec["label"]?.ToString() ?? ""
                                : rec.ContainsKey("name")
                                    ? rec["name"]?.ToString() ?? ""
                                    : idVal;

                        select2Results.Add(new { id = idVal, text = textVal });
                    }
                }

                var totalCount = records?.TotalCount ?? 0;
                var hasMore = totalCount > page * SELECT2_PAGE_SIZE;

                var result = new
                {
                    results = select2Results,
                    pagination = new { more = hasMore }
                };

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Object = result
                };

                _logger.LogInformation(
                    "ExecuteDataSourceSelect2 completed. DsName={DataSourceName}, Results={ResultCount}, CorrelationId={CorrelationId}",
                    dsName, select2Results.Count, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            catch (EqlException eqlEx)
            {
                var eqlErrors = eqlEx.Errors?.Select(e => new ErrorModel
                {
                    Key = "eql", Value = "", Message = e.Message
                }).ToList() ?? new List<ErrorModel>
                {
                    new ErrorModel { Key = "eql", Value = "", Message = eqlEx.Message }
                };
                return BuildErrorResponse((int)HttpStatusCode.BadRequest, eqlErrors, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ExecuteDataSourceSelect2 error. CorrelationId={CorrelationId}", correlationId);
                return BuildErrorResponse(
                    (int)HttpStatusCode.InternalServerError,
                    _isDevelopmentMode ? ex.Message : "An internal error occurred.",
                    correlationId);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // PRIVATE EXECUTION HELPER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Executes a stored datasource by its GUID identifier with optional parameter overrides.
        /// Source: DataSourceManager.Execute(Guid id, ...).
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ExecuteStoredDataSource(
            Guid dsId,
            Dictionary<string, object?>? bodyDict,
            string correlationId)
        {
            var allDs = await GetAllDataSourcesCached();
            var dataSource = allDs.FirstOrDefault(ds => ds.Id == dsId);

            if (dataSource == null)
            {
                return BuildErrorResponse(
                    (int)HttpStatusCode.NotFound,
                    $"Datasource with ID '{dsId}' not found.",
                    correlationId);
            }

            // Validate entity still exists for DatabaseDataSource execution.
            if (dataSource is DatabaseDataSource dbDataSource &&
                !string.IsNullOrWhiteSpace(dbDataSource.EntityName))
            {
                var entityResponse = await _entityService.ReadEntity(dbDataSource.EntityName);
                if (!entityResponse.Success || entityResponse.Object == null)
                {
                    _logger.LogWarning(
                        "Entity '{EntityName}' for datasource '{DsName}' no longer exists. CorrelationId={CorrelationId}",
                        dbDataSource.EntityName, dbDataSource.Name, correlationId);
                }
            }

            Dictionary<string, object?>? paramOverrides = null;
            if (bodyDict != null && bodyDict.ContainsKey("parameters") && bodyDict["parameters"] != null)
            {
                try
                {
                    paramOverrides = JsonConvert.DeserializeObject<Dictionary<string, object?>>(
                        bodyDict["parameters"]!.ToString()!);
                }
                catch
                {
                    paramOverrides = new Dictionary<string, object?>();
                }
            }

            return await ExecuteDataSourceByBase(dataSource, paramOverrides, correlationId);
        }

        /// <summary>
        /// Executes a datasource (DatabaseDataSource or CodeDataSource) with parameter overrides.
        /// Dispatches to IQueryAdapter.Execute for database DS, or CodeDataSource.Execute for code DS.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ExecuteDataSourceByBase(
            DataSourceBase dataSource,
            Dictionary<string, object?>? paramOverrides,
            string correlationId)
        {
            if (dataSource is DatabaseDataSource dbDs)
            {
                var eqlParams = BuildRuntimeParameters(dbDs, paramOverrides);
                var eqlSettings = new EqlSettings { IncludeTotal = dbDs.ReturnTotal };

                _logger.LogInformation(
                    "Executing DatabaseDataSource. Id={DataSourceId}, Name={DataSourceName}, EQL={EqlText}, CorrelationId={CorrelationId}",
                    dbDs.Id, dbDs.Name, dbDs.EqlText, correlationId);

                var queryResult = await _queryAdapter.Execute(dbDs.EqlText, eqlParams, eqlSettings);

                var resultObj = new
                {
                    list = queryResult.Data ?? new List<EntityRecord>(),
                    total_count = queryResult.Data?.Count ?? 0,
                    fields_meta = queryResult.FieldsMeta
                };

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Object = resultObj
                };

                _logger.LogInformation(
                    "DatabaseDataSource executed. Id={DataSourceId}, RecordCount={Count}, CorrelationId={CorrelationId}",
                    dbDs.Id, queryResult.Data?.Count ?? 0, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            else if (dataSource is CodeDataSource codeDs)
            {
                var execArgs = BuildCodeDataSourceArgs(paramOverrides);

                _logger.LogInformation(
                    "Executing CodeDataSource. Id={DataSourceId}, Name={DataSourceName}, CorrelationId={CorrelationId}",
                    codeDs.Id, codeDs.Name, correlationId);

                var codeResult = codeDs.Execute(execArgs);

                var responseModel = new ResponseModel
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Object = codeResult
                };

                _logger.LogInformation(
                    "CodeDataSource executed. Id={DataSourceId}, CorrelationId={CorrelationId}",
                    codeDs.Id, correlationId);

                return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
            }
            else
            {
                _logger.LogWarning(
                    "Unsupported datasource type {Type}. Expected {Database} or {Code}. CorrelationId={CorrelationId}",
                    dataSource.Type, DataSourceType.DATABASE, DataSourceType.CODE, correlationId);

                return BuildErrorResponse(
                    (int)HttpStatusCode.BadRequest,
                    $"Unsupported datasource type: {dataSource.Type}.",
                    correlationId);
            }
        }

        /// <summary>
        /// Executes ad-hoc EQL text with inline parameters and returnTotal flag.
        /// Source: WebApiController EqlQueryAction — parses EQL + parameter text,
        /// delegates to IQueryAdapter.Execute.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ExecuteAdHocEql(
            string eqlText,
            string parametersText,
            bool returnTotal,
            string correlationId)
        {
            _logger.LogInformation(
                "Executing ad-hoc EQL. ReturnTotal={ReturnTotal}, CorrelationId={CorrelationId}",
                returnTotal, correlationId);

            // Parse inline parameters from newline-separated text.
            var eqlParams = ParseEqlParametersFromText(parametersText);
            var eqlSettings = new EqlSettings { IncludeTotal = returnTotal };

            // Validate via Build first.
            var buildResult = _queryAdapter.Build(eqlText, eqlParams, eqlSettings);
            if (buildResult.Errors != null && buildResult.Errors.Any())
            {
                var errors = buildResult.Errors.Select(e =>
                {
                    var lineInfo = e.Line.HasValue && e.Column.HasValue
                        ? $" (line {e.Line}, column {e.Column})"
                        : "";
                    return new ErrorModel
                    {
                        Key = "eql",
                        Value = "",
                        Message = e.Message + lineInfo
                    };
                }).ToList();

                return BuildErrorResponse((int)HttpStatusCode.BadRequest, errors, correlationId);
            }

            var queryResult = await _queryAdapter.Execute(eqlText, eqlParams, eqlSettings);

            var resultObj = new
            {
                list = queryResult.Data ?? new List<EntityRecord>(),
                total_count = queryResult.Data?.Count ?? 0,
                fields_meta = queryResult.FieldsMeta
            };

            var responseModel = new ResponseModel
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Object = resultObj
            };

            _logger.LogInformation(
                "Ad-hoc EQL executed. RecordCount={Count}, CorrelationId={CorrelationId}",
                queryResult.Data?.Count ?? 0, correlationId);

            return BuildResponse((int)HttpStatusCode.OK, responseModel, correlationId);
        }

        // ═════════════════════════════════════════════════════════════════
        // CACHE AND DATA ACCESS HELPERS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns a cached list of all datasources (code-discovered + database-stored),
        /// merging both sources. Cache has a 1-hour absolute expiration.
        /// Source: DataSourceManager.GetAll() + Cache pattern.
        /// </summary>
        private async Task<List<DataSourceBase>> GetAllDataSourcesCached()
        {
            if (_cache.TryGetValue(CACHE_KEY, out List<DataSourceBase>? cached) && cached != null)
            {
                _logger.LogInformation("Datasource cache hit. Count={Count}", cached.Count);
                return cached;
            }

            _logger.LogInformation("Datasource cache miss. Loading from DynamoDB and code discovery.");

            var codeDataSources = InitCodeDataSources();
            var dbDataSources = await LoadDatabaseDataSources();

            // Merge: code datasources + database datasources (DB wins on ID collision).
            var merged = new List<DataSourceBase>(codeDataSources);
            foreach (var dbDs in dbDataSources)
            {
                if (!merged.Any(existing => existing.Id == dbDs.Id))
                {
                    merged.Add(dbDs);
                }
            }

            // Sort by weight then name for consistent ordering.
            merged = merged
                .OrderBy(ds => ds.Weight)
                .ThenBy(ds => ds.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(CACHE_TTL_HOURS));

            _cache.Set(CACHE_KEY, merged, cacheOptions);

            _logger.LogInformation(
                "Datasource cache populated. CodeCount={CodeCount}, DbCount={DbCount}, TotalCount={TotalCount}",
                codeDataSources.Count, dbDataSources.Count, merged.Count);

            return merged;
        }

        /// <summary>
        /// Discovers all CodeDataSource subclasses via reflection in the current AppDomain.
        /// Source: DataSourceManager.InitCodeDataSources() — scans assemblies for types
        /// inheriting from CodeDataSource abstract class.
        /// </summary>
        private List<DataSourceBase> InitCodeDataSources()
        {
            var codeDataSources = new List<DataSourceBase>();

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        var types = assembly.GetTypes()
                            .Where(t => t.IsClass && !t.IsAbstract && typeof(CodeDataSource).IsAssignableFrom(t));

                        foreach (var type in types)
                        {
                            try
                            {
                                var instance = (CodeDataSource?)Activator.CreateInstance(type);
                                if (instance != null)
                                {
                                    codeDataSources.Add(instance);
                                    _logger.LogInformation(
                                        "CodeDataSource discovered: {TypeName}, Id={Id}, Name={Name}",
                                        type.FullName, instance.Id, instance.Name);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Failed to instantiate CodeDataSource type: {TypeName}", type.FullName);
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // Safely skip assemblies that fail to reflect (common in AOT scenarios).
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during CodeDataSource assembly scanning.");
            }

            return codeDataSources;
        }

        /// <summary>
        /// Loads all DatabaseDataSource definitions from the DynamoDB datasource table.
        /// Uses a Query on PK="DATASOURCE" to retrieve all datasource items.
        /// </summary>
        private async Task<List<DatabaseDataSource>> LoadDatabaseDataSources()
        {
            var dataSources = new List<DatabaseDataSource>();

            try
            {
                var queryRequest = new Amazon.DynamoDBv2.Model.QueryRequest
                {
                    TableName = _datasourceTableName,
                    KeyConditionExpression = "#pk = :pkVal",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#pk", PK_ATTR }
                    },
                    ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
                    {
                        { ":pkVal", new Amazon.DynamoDBv2.Model.AttributeValue { S = DATASOURCE_PK } }
                    }
                };

                var response = await _dynamoDbClient.QueryAsync(queryRequest);

                foreach (var item in response.Items)
                {
                    try
                    {
                        if (item.TryGetValue("Data", out var dataAttr) && dataAttr.S != null)
                        {
                            var ds = JsonConvert.DeserializeObject<DatabaseDataSource>(
                                dataAttr.S,
                                new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });

                            if (ds != null)
                            {
                                dataSources.Add(ds);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to deserialize datasource item from DynamoDB.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading datasource definitions from DynamoDB.");
                throw;
            }

            return dataSources;
        }

        /// <summary>
        /// Persists a DatabaseDataSource definition to DynamoDB.
        /// PK="DATASOURCE", SK=datasource ID (Guid string).
        /// The full datasource object is serialized as JSON in the "Data" attribute.
        /// </summary>
        private async Task SaveDataSource(DatabaseDataSource dataSource)
        {
            var item = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                { PK_ATTR, new Amazon.DynamoDBv2.Model.AttributeValue { S = DATASOURCE_PK } },
                { SK_ATTR, new Amazon.DynamoDBv2.Model.AttributeValue { S = dataSource.Id.ToString() } },
                { "Data", new Amazon.DynamoDBv2.Model.AttributeValue
                    {
                        S = JsonConvert.SerializeObject(dataSource,
                            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto })
                    }
                },
                { "Name", new Amazon.DynamoDBv2.Model.AttributeValue { S = dataSource.Name ?? "" } },
                { "EntityName", new Amazon.DynamoDBv2.Model.AttributeValue { S = dataSource.EntityName ?? "" } },
                { "CreatedAt", new Amazon.DynamoDBv2.Model.AttributeValue { S = DateTime.UtcNow.ToString("o") } }
            };

            var putRequest = new Amazon.DynamoDBv2.Model.PutItemRequest
            {
                TableName = _datasourceTableName,
                Item = item
            };

            await _dynamoDbClient.PutItemAsync(putRequest);
        }

        /// <summary>
        /// Deletes a datasource definition from DynamoDB by its GUID ID.
        /// </summary>
        private async Task DeleteDataSourceFromDynamo(Guid dsId)
        {
            var deleteRequest = new Amazon.DynamoDBv2.Model.DeleteItemRequest
            {
                TableName = _datasourceTableName,
                Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
                {
                    { PK_ATTR, new Amazon.DynamoDBv2.Model.AttributeValue { S = DATASOURCE_PK } },
                    { SK_ATTR, new Amazon.DynamoDBv2.Model.AttributeValue { S = dsId.ToString() } }
                }
            };

            await _dynamoDbClient.DeleteItemAsync(deleteRequest);
        }

        // ═════════════════════════════════════════════════════════════════
        // PARAMETER PROCESSING HELPERS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Parses newline-separated parameter definitions from the datasource's Parameters list.
        /// Source: DataSourceManager.ProcessParametersText() — each parameter definition is
        /// "name,type,value[,ignoreParseErrors]" format stored in DataSourceParameter objects.
        /// Returns the list of parsed DataSourceParameter objects with validated types and values.
        /// </summary>
        private List<DataSourceParameter> ProcessParametersText(DatabaseDataSource dataSource)
        {
            if (dataSource.Parameters == null || !dataSource.Parameters.Any())
            {
                return new List<DataSourceParameter>();
            }

            // Parameters are already parsed into DataSourceParameter list from deserialization.
            // Validate each parameter has required fields.
            var result = new List<DataSourceParameter>();
            foreach (var param in dataSource.Parameters)
            {
                if (string.IsNullOrWhiteSpace(param.Name))
                {
                    continue;
                }

                // Ensure parameter name does not start with @ (strip it if present for storage).
                var cleanName = param.Name.TrimStart('@');
                result.Add(new DataSourceParameter
                {
                    Name = cleanName,
                    Type = param.Type ?? "text",
                    Value = param.Value,
                    IgnoreParseErrors = param.IgnoreParseErrors
                });
            }

            return result;
        }

        /// <summary>
        /// Converts a DataSourceParameter to an EqlParameter for query execution.
        /// Applies @-prefix normalization and typed value parsing.
        /// Source: DataSourceManager.ConvertDataSourceParameterToEqlParameter().
        /// </summary>
        private EqlParameter ConvertDataSourceParameterToEqlParameter(DataSourceParameter dsParam)
        {
            var paramName = dsParam.Name.StartsWith("@") ? dsParam.Name : "@" + dsParam.Name;
            var value = GetDataSourceParameterValue(dsParam.Type, dsParam.Value);

            return new EqlParameter { ParameterName = paramName, Value = value, Type = dsParam.Type };
        }

        /// <summary>
        /// Parses a parameter value string into a typed object based on the declared parameter type.
        /// Supports special literal values: null, guid.empty, now, utc_now, string.empty.
        /// Source: DataSourceManager.GetDataSourceParameterValue() — handles types:
        /// guid, int, decimal, date, text, bool.
        /// </summary>
        private object? GetDataSourceParameterValue(string? type, string? valueStr)
        {
            // Handle special literal values first (case-insensitive).
            if (string.IsNullOrWhiteSpace(valueStr) ||
                string.Equals(valueStr, "null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(valueStr, "guid.empty", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.Empty;
            }

            if (string.Equals(valueStr, "now", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.Now;
            }

            if (string.Equals(valueStr, "utc_now", StringComparison.OrdinalIgnoreCase))
            {
                return DateTime.UtcNow;
            }

            if (string.Equals(valueStr, "string.empty", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Type-specific parsing.
            var normalizedType = (type ?? "text").Trim().ToLowerInvariant();

            switch (normalizedType)
            {
                case "guid":
                    if (Guid.TryParse(valueStr, out var guidVal))
                        return guidVal;
                    return valueStr; // Fall back to string for invalid guids.

                case "int":
                case "integer":
                    if (int.TryParse(valueStr, out var intVal))
                        return intVal;
                    return 0;

                case "decimal":
                case "number":
                    try
                    {
                        return Convert.ToDecimal(valueStr,
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        return 0m;
                    }

                case "date":
                case "datetime":
                    if (DateTime.TryParse(valueStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dateVal))
                        return dateVal;
                    return DateTime.MinValue;

                case "bool":
                case "boolean":
                    if (bool.TryParse(valueStr, out var boolVal))
                        return boolVal;
                    return false;

                case "text":
                case "string":
                default:
                    return valueStr;
            }
        }

        /// <summary>
        /// Builds a list of EqlParameter from a DatabaseDataSource's declared parameters
        /// with runtime value overrides from the request body.
        /// Source: DataSourceManager.Execute(Guid id, ...) — merges default params with overrides.
        /// </summary>
        private List<EqlParameter> BuildRuntimeParameters(
            DatabaseDataSource dataSource,
            Dictionary<string, object?>? paramOverrides)
        {
            var result = new List<EqlParameter>();

            if (dataSource.Parameters == null || !dataSource.Parameters.Any())
            {
                return result;
            }

            foreach (var dsParam in dataSource.Parameters)
            {
                var paramName = dsParam.Name.TrimStart('@');
                var eqlParamName = "@" + paramName;

                // Check if runtime override exists for this parameter.
                object? runtimeValue = null;
                bool hasOverride = false;

                if (paramOverrides != null)
                {
                    // Try both with and without @ prefix.
                    if (paramOverrides.TryGetValue(paramName, out var ov1))
                    {
                        runtimeValue = ov1;
                        hasOverride = true;
                    }
                    else if (paramOverrides.TryGetValue("@" + paramName, out var ov2))
                    {
                        runtimeValue = ov2;
                        hasOverride = true;
                    }
                }

                object? finalValue;
                if (hasOverride)
                {
                    // Parse the override value through the type system.
                    if (runtimeValue == null)
                    {
                        finalValue = null;
                    }
                    else
                    {
                        finalValue = GetDataSourceParameterValue(dsParam.Type, runtimeValue.ToString());
                    }
                }
                else
                {
                    // Use the default value from the parameter definition.
                    finalValue = GetDataSourceParameterValue(dsParam.Type, dsParam.Value);
                }

                result.Add(new EqlParameter { ParameterName = eqlParamName, Value = finalValue, Type = dsParam.Type });
            }

            return result;
        }

        /// <summary>
        /// Parses inline EQL parameters from newline-separated text format.
        /// Each line: "name,type,value[,ignoreParseErrors]".
        /// Source: Part of DataSourceManager.ProcessParametersText adapted for ad-hoc EQL.
        /// </summary>
        private List<EqlParameter> ParseEqlParametersFromText(string parametersText)
        {
            var result = new List<EqlParameter>();

            if (string.IsNullOrWhiteSpace(parametersText))
            {
                return result;
            }

            var lines = parametersText.Split(
                new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                var parts = trimmed.Split(',');
                if (parts.Length < 2)
                    continue;

                var name = parts[0].Trim();
                var type = parts[1].Trim();
                var value = parts.Length >= 3 ? parts[2].Trim() : null;

                var paramName = name.StartsWith("@") ? name : "@" + name;
                var typedValue = GetDataSourceParameterValue(type, value);

                result.Add(new EqlParameter { ParameterName = paramName, Value = typedValue, Type = type });
            }

            return result;
        }

        /// <summary>
        /// Builds a dictionary of arguments for CodeDataSource.Execute() from parameter overrides.
        /// </summary>
        private Dictionary<string, object> BuildCodeDataSourceArgs(
            Dictionary<string, object?>? paramOverrides)
        {
            var args = new Dictionary<string, object>();

            if (paramOverrides == null)
                return args;

            foreach (var kvp in paramOverrides)
            {
                if (kvp.Value != null)
                {
                    args[kvp.Key] = kvp.Value;
                }
            }

            return args;
        }

        /// <summary>
        /// Converts a generic object result from CodeDataSource.Execute() to EntityRecordList.
        /// Handles List of dictionaries, EntityRecordList direct return, and single object cases.
        /// </summary>
        private EntityRecordList ConvertToEntityRecordList(object? result)
        {
            if (result == null)
                return new EntityRecordList();

            if (result is EntityRecordList erl)
                return erl;

            if (result is List<EntityRecord> recList)
            {
                var list = new EntityRecordList();
                list.AddRange(recList);
                list.TotalCount = recList.Count;
                return list;
            }

            // Try to convert from generic IEnumerable.
            if (result is System.Collections.IEnumerable enumerable)
            {
                var list = new EntityRecordList();
                foreach (var item in enumerable)
                {
                    if (item is EntityRecord rec)
                    {
                        list.Add(rec);
                    }
                    else if (item is Dictionary<string, object?> dict)
                    {
                        var record = new EntityRecord();
                        foreach (var kvp in dict)
                        {
                            record[kvp.Key] = kvp.Value;
                        }
                        list.Add(record);
                    }
                }
                list.TotalCount = list.Count;
                return list;
            }

            // Single object — wrap in a record.
            var singleRecord = new EntityRecord();
            singleRecord["result"] = result;
            var singleList = new EntityRecordList { singleRecord };
            singleList.TotalCount = 1;
            return singleList;
        }

        // ═════════════════════════════════════════════════════════════════
        // FIELD METADATA PROCESSING
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a list of EqlFieldMeta nodes into a list of DataSourceModelFieldMeta.
        /// Entry point for processing the Meta list from EqlBuildResult.
        /// </summary>
        private List<DataSourceModelFieldMeta> ProcessFieldsMetaList(List<EqlFieldMeta> metaList)
        {
            var result = new List<DataSourceModelFieldMeta>();
            foreach (var meta in metaList)
            {
                var fieldMeta = ConvertSingleFieldMeta(meta);
                if (fieldMeta != null)
                {
                    result.Add(fieldMeta);
                }
            }
            return result;
        }

        /// <summary>
        /// Converts a single EqlFieldMeta node to DataSourceModelFieldMeta.
        /// </summary>
        private DataSourceModelFieldMeta? ConvertSingleFieldMeta(EqlFieldMeta meta)
        {
            var fieldMeta = new DataSourceModelFieldMeta();

            if (meta.Field != null)
            {
                fieldMeta.Name = meta.Field.Name;
                fieldMeta.Type = meta.Field.GetFieldType();
            }
            else
            {
                // Relation field — name starts with "$".
                fieldMeta.Name = "$" + (meta.Entity?.Name ?? "unknown");
            }

            if (meta.Entity != null)
            {
                fieldMeta.EntityName = meta.Entity.Name;
            }

            // Recursively process children (relation navigation).
            if (meta.Children != null && meta.Children.Any())
            {
                foreach (var child in meta.Children)
                {
                    var childField = ConvertSingleFieldMeta(child);
                    if (childField != null)
                    {
                        fieldMeta.AddChild(childField);
                    }
                }
            }

            return fieldMeta;
        }

        /// <summary>
        /// Recursively converts EqlFieldMeta tree into DataSourceModelFieldMeta tree.
        /// Processes field metadata from the EQL build result to populate the datasource's
        /// field definitions for client-side consumption.
        /// Source: DataSourceManager.ProcessFieldsMeta() — handles relation prefix "$".
        /// </summary>
        private List<DataSourceModelFieldMeta> ProcessFieldsMeta(EqlFieldMeta rootMeta)
        {
            var result = new List<DataSourceModelFieldMeta>();

            if (rootMeta.Children == null || !rootMeta.Children.Any())
            {
                return result;
            }

            foreach (var childMeta in rootMeta.Children)
            {
                var fieldMeta = new DataSourceModelFieldMeta();

                if (childMeta.Field != null)
                {
                    fieldMeta.Name = childMeta.Field.Name;
                    fieldMeta.Type = childMeta.Field.GetFieldType();
                }
                else
                {
                    // Relation field — name starts with "$".
                    fieldMeta.Name = "$" + (childMeta.Entity?.Name ?? "unknown");
                }

                if (childMeta.Entity != null)
                {
                    fieldMeta.EntityName = childMeta.Entity.Name;
                }

                // Recursively process children (relation navigation).
                if (childMeta.Children != null && childMeta.Children.Any())
                {
                    var childFields = ProcessFieldsMeta(childMeta);
                    foreach (var cf in childFields)
                    {
                        fieldMeta.AddChild(cf);
                    }
                }

                result.Add(fieldMeta);
            }

            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        // INFRASTRUCTURE HELPER METHODS
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if the request's JWT claims include the required entity permission.
        /// Supports both API Gateway native JWT authorizer (cognito:groups, custom:roles, scope)
        /// and custom Lambda authorizer context (isAdmin flag, roles string).
        /// Source: Adapted from EntityHandler.HasPermission pattern.
        /// </summary>
        private bool HasPermission(APIGatewayHttpApiV2ProxyRequest request, EntityPermission requiredPermission)
        {
            try
            {
                var authorizer = request.RequestContext?.Authorizer;
                if (authorizer == null)
                {
                    // No authorizer context — deny by default in production, allow in dev.
                    return _isDevelopmentMode;
                }

                // Check API Gateway native JWT authorizer claims.
                if (authorizer.Jwt?.Claims != null)
                {
                    var claims = authorizer.Jwt.Claims;

                    // Check cognito:groups for administrator role.
                    if (claims.TryGetValue("cognito:groups", out var groups) && !string.IsNullOrWhiteSpace(groups))
                    {
                        if (groups.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
                            groups.Contains(SystemIds.AdministratorRoleId.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check custom:roles claim.
                    if (claims.TryGetValue("custom:roles", out var roles) && !string.IsNullOrWhiteSpace(roles))
                    {
                        if (roles.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
                            roles.Contains(SystemIds.AdministratorRoleId.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Check scope claim.
                    if (claims.TryGetValue("scope", out var scope) && !string.IsNullOrWhiteSpace(scope))
                    {
                        var requiredScope = requiredPermission switch
                        {
                            EntityPermission.Read => "entity:read",
                            EntityPermission.Create => "entity:write",
                            EntityPermission.Update => "entity:write",
                            EntityPermission.Delete => "entity:delete",
                            _ => "entity:read"
                        };

                        if (scope.Contains(requiredScope, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // For Read operations, any authenticated user is allowed.
                    if (requiredPermission == EntityPermission.Read &&
                        claims.ContainsKey("sub"))
                    {
                        return true;
                    }
                }

                // Check custom Lambda authorizer context (used with LocalStack fallback).
                if (authorizer.Lambda != null)
                {
                    if (authorizer.Lambda.TryGetValue("isAdmin", out var isAdminObj))
                    {
                        var isAdmin = isAdminObj?.ToString();
                        if (string.Equals(isAdmin, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    if (authorizer.Lambda.TryGetValue("roles", out var rolesObj))
                    {
                        var rolesStr = rolesObj?.ToString() ?? "";
                        if (rolesStr.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
                            rolesStr.Contains(SystemIds.AdministratorRoleId.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // For Read operations, any authenticated Lambda context is sufficient.
                    if (requiredPermission == EntityPermission.Read &&
                        authorizer.Lambda.ContainsKey("principalId"))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error evaluating permissions. Denying access.");
                return false;
            }
        }

        /// <summary>
        /// Extracts the correlation ID from request headers, falling back to the Lambda request ID.
        /// </summary>
        private string ExtractCorrelationId(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            if (request.Headers != null)
            {
                if (request.Headers.TryGetValue("x-correlation-id", out var correlationId) &&
                    !string.IsNullOrWhiteSpace(correlationId))
                {
                    return correlationId;
                }

                if (request.Headers.TryGetValue("X-Correlation-Id", out var correlationId2) &&
                    !string.IsNullOrWhiteSpace(correlationId2))
                {
                    return correlationId2;
                }
            }

            return context.AwsRequestId ?? Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Builds an API Gateway response with JSON body and standard headers.
        /// Uses System.Text.Json for AOT-safe serialization.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode,
            object body,
            string correlationId)
        {
            string jsonBody;
            try
            {
                jsonBody = System.Text.Json.JsonSerializer.Serialize(body, _jsonOptions);
            }
            catch
            {
                // Fall back to Newtonsoft for complex anonymous types.
                jsonBody = JsonConvert.SerializeObject(body);
            }

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = jsonBody,
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "X-Correlation-Id", correlationId }
                }
            };
        }

        /// <summary>
        /// Builds an error response with a single error message in the BaseResponseModel envelope.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode,
            string errorMessage,
            string correlationId)
        {
            return BuildErrorResponse(statusCode, new List<ErrorModel>
            {
                new ErrorModel { Key = "general", Value = "", Message = errorMessage }
            }, correlationId);
        }

        /// <summary>
        /// Builds an error response with multiple error models in the BaseResponseModel envelope.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            int statusCode,
            List<ErrorModel> errors,
            string correlationId)
        {
            var response = new BaseResponseModel
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = errors.FirstOrDefault()?.Message ?? "An error occurred.",
                Errors = errors,
                StatusCode = (HttpStatusCode)statusCode
            };

            return BuildResponse(statusCode, response, correlationId);
        }

        /// <summary>
        /// Publishes a domain event to the configured SNS topic.
        /// Event structure includes eventType, source, timestamp, correlationId, and data payload.
        /// Source: Adapted from EntityHandler.PublishDomainEvent pattern using Newtonsoft serialization.
        /// </summary>
        private async Task PublishDomainEvent(
            string eventType,
            object eventData,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_datasourceTopicArn))
            {
                _logger.LogWarning(
                    "DATASOURCE_TOPIC_ARN not configured. Skipping SNS event: {EventType}. CorrelationId={CorrelationId}",
                    eventType, correlationId);
                return;
            }

            try
            {
                var eventPayload = new
                {
                    source = "entity-management",
                    detailType = eventType,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    correlationId,
                    data = eventData
                };

                var publishRequest = new Amazon.SimpleNotificationService.Model.PublishRequest
                {
                    TopicArn = _datasourceTopicArn,
                    Message = JsonConvert.SerializeObject(eventPayload),
                    MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
                    {
                        {
                            "eventType",
                            new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = eventType
                            }
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest);

                _logger.LogInformation(
                    "Domain event published. EventType={EventType}, CorrelationId={CorrelationId}",
                    eventType, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to publish domain event. EventType={EventType}, CorrelationId={CorrelationId}",
                    eventType, correlationId);
                // Do not rethrow — event publishing failure should not break the main operation.
            }
        }
    }
}
