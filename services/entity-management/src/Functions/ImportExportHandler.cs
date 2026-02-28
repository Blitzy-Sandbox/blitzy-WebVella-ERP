// ---------------------------------------------------------------------------
// ImportExportHandler.cs — CSV Import/Export Lambda Handler
// Entity Management Service — WebVella ERP Serverless Rewrite
//
// Replaces: WebVella.Erp/Api/ImportExportManager.cs
//           WebVella.Erp.Web/Controllers/WebApiController.cs (import endpoints)
//
// Routes:
//   POST /v1/entity-management/entities/{entityName}/import         → ImportFromCsv
//   POST /v1/entity-management/entities/{entityName}/import/evaluate → EvaluateImport
//
// NOTE: Do NOT add [assembly: LambdaSerializer(...)] here.
//       It is already declared in EntityHandler.cs (CS0579 if duplicated).
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

using CsvHelper;
using CsvHelper.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;

namespace WebVellaErp.EntityManagement.Functions
{
    /// <summary>
    /// Lambda handler for CSV import and import-evaluation operations on entity records.
    /// Provides two entry points:
    /// <list type="bullet">
    ///   <item><see cref="ImportFromCsv"/> — Simple CSV import (create/update records from file)</item>
    ///   <item><see cref="EvaluateImport"/> — Evaluation + optional import pipeline with per-column analysis</item>
    /// </list>
    /// </summary>
    public class ImportExportHandler
    {
        // ---------------------------------------------------------------
        // Constants — preserved exactly from monolith ImportExportManager.cs
        // ---------------------------------------------------------------

        /// <summary>
        /// Separator between relation name and field name in CSV header columns.
        /// Example: "$customer.name" where '.' separates relation from field.
        /// </summary>
        public const char RELATION_SEPARATOR = '.';

        /// <summary>
        /// Prefix/suffix character for relation direction notation in CSV headers.
        /// '$' before relation name = origin→target direction.
        /// '$$' = target→origin (reverse) direction.
        /// </summary>
        public const char RELATION_NAME_RESULT_SEPARATOR = '$';

        // ---------------------------------------------------------------
        // DI Fields
        // ---------------------------------------------------------------

        private readonly IRecordService _recordService;
        private readonly IEntityService _entityService;
        private readonly IAmazonS3 _s3Client;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<ImportExportHandler> _logger;
        private readonly IConfiguration _configuration;

        // ---------------------------------------------------------------
        // Configuration Fields
        // ---------------------------------------------------------------

        private readonly string _importTopicArn;
        private readonly bool _isDevelopmentMode;
        private readonly string _s3BucketName;
        private readonly string _filesTempPrefix;

        /// <summary>
        /// Shared System.Text.Json options for request/response serialization.
        /// Matches the sibling handler pattern (PropertyNameCaseInsensitive, ignore null).
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions;

        // ---------------------------------------------------------------
        // Constructor — DI injection matching sibling handler pattern
        // ---------------------------------------------------------------

        /// <summary>
        /// Constructs the ImportExportHandler with all required dependencies.
        /// </summary>
        /// <param name="recordService">Record CRUD service (replaces RecordManager).</param>
        /// <param name="entityService">Entity metadata service (replaces EntityManager + EntityRelationManager).</param>
        /// <param name="s3Client">S3 client for CSV file retrieval (replaces DbFileRepository).</param>
        /// <param name="snsClient">SNS client for domain event publishing (replaces synchronous post-hooks).</param>
        /// <param name="logger">Structured JSON logger with correlation-ID support.</param>
        /// <param name="configuration">Application configuration for environment settings.</param>
        public ImportExportHandler(
            IRecordService recordService,
            IEntityService entityService,
            IAmazonS3 s3Client,
            IAmazonSimpleNotificationService snsClient,
            ILogger<ImportExportHandler> logger,
            IConfiguration configuration)
        {
            _recordService = recordService ?? throw new ArgumentNullException(nameof(recordService));
            _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
            _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _importTopicArn = Environment.GetEnvironmentVariable("IMPORT_TOPIC_ARN")
                              ?? Environment.GetEnvironmentVariable("RECORD_TOPIC_ARN")
                              ?? string.Empty;
            _isDevelopmentMode = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
            _s3BucketName = Environment.GetEnvironmentVariable("FILES_S3_BUCKET") ?? "webvella-erp-files";
            _filesTempPrefix = Environment.GetEnvironmentVariable("FILES_TEMP_PREFIX") ?? "temp/";

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        // ===============================================================
        // PUBLIC LAMBDA HANDLER — ImportFromCsv
        // ===============================================================

        /// <summary>
        /// Lambda handler for simple CSV import. Creates or updates entity records
        /// from a CSV file stored in S3.
        /// <para>Route: POST /v1/entity-management/entities/{entityName}/import</para>
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>API Gateway response with <see cref="ResponseModel"/> body.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> ImportFromCsv(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] ImportFromCsv invoked", correlationId);

            try
            {
                // 1. Extract entity name from path parameters
                var entityName = GetParam(request.PathParameters, "entityName");
                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest, "Entity name is required.", correlationId);
                }

                // 2. Parse request body for fileTempPath
                var fileTempPath = string.Empty;
                if (!string.IsNullOrWhiteSpace(request.Body))
                {
                    try
                    {
                        var bodyObj = JObject.Parse(request.Body);
                        if (bodyObj.Properties().Any(p => p.Name == "fileTempPath"))
                        {
                            fileTempPath = bodyObj["fileTempPath"]?.ToString() ?? string.Empty;
                        }
                    }
                    catch (Newtonsoft.Json.JsonReaderException)
                    {
                        return BuildErrorResponse(HttpStatusCode.BadRequest, "Invalid request body JSON.", correlationId);
                    }
                }

                // 3. Delegate to internal import logic
                var response = await ImportEntityRecordsFromCsvInternal(entityName, fileTempPath, request, correlationId);

                // 4. Publish SNS event on success
                if (response.Success)
                {
                    await PublishDomainEvent(
                        "entity-management.records.imported",
                        new { entityName, source = "csv-import", timestamp = DateTime.UtcNow.ToString("o") },
                        correlationId);
                }

                // 5. Return appropriate HTTP status
                var statusCode = response.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    statusCode = HttpStatusCode.Forbidden;
                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] ImportFromCsv failed with exception", correlationId);
                var message = _isDevelopmentMode
                    ? $"{ex.Message}\n{ex.StackTrace}"
                    : "An internal error occurred during import.";
                return BuildErrorResponse(HttpStatusCode.InternalServerError, message, correlationId);
            }
        }

        // ===============================================================
        // PUBLIC LAMBDA HANDLER — EvaluateImport
        // ===============================================================

        /// <summary>
        /// Lambda handler for import evaluation (and optional execution).
        /// Analyzes CSV data against entity schema, validates field types,
        /// resolves relation references, and optionally executes the import.
        /// <para>Route: POST /v1/entity-management/entities/{entityName}/import/evaluate</para>
        /// </summary>
        /// <param name="request">API Gateway HTTP API v2 proxy request.</param>
        /// <param name="context">Lambda execution context.</param>
        /// <returns>API Gateway response with <see cref="ResponseModel"/> body containing evaluation results.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> EvaluateImport(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = ExtractCorrelationId(request, context);
            _logger.LogInformation("[{CorrelationId}] EvaluateImport invoked", correlationId);

            try
            {
                // 1. Extract entity name from path parameters
                var entityName = GetParam(request.PathParameters, "entityName");
                if (string.IsNullOrWhiteSpace(entityName))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest, "Entity name is required.", correlationId);
                }

                // 2. Parse request body as JObject
                JObject postObject;
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest, "Request body is required.", correlationId);
                }
                try
                {
                    postObject = JObject.Parse(request.Body);
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    return BuildErrorResponse(HttpStatusCode.BadRequest, "Invalid request body JSON.", correlationId);
                }

                // 3. Delegate to internal evaluate logic
                var response = await EvaluateImportEntityRecordsFromCsvInternal(entityName, postObject, request, correlationId);

                // 4. Publish SNS event if evaluate-import succeeded
                if (response.Success)
                {
                    var generalCommand = postObject["general_command"]?.ToString() ?? "evaluate";
                    if (generalCommand == "evaluate-import")
                    {
                        await PublishDomainEvent(
                            "entity-management.records.imported",
                            new { entityName, source = "evaluate-import", timestamp = DateTime.UtcNow.ToString("o") },
                            correlationId);
                    }
                }

                var statusCode = response.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    statusCode = HttpStatusCode.Forbidden;
                return BuildResponse(statusCode, response, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] EvaluateImport failed with exception", correlationId);
                var message = _isDevelopmentMode
                    ? $"{ex.Message}\n{ex.StackTrace}"
                    : "An internal error occurred during import evaluation.";
                return BuildErrorResponse(HttpStatusCode.InternalServerError, message, correlationId);
            }
        }

        // ===============================================================
        // PRIVATE — ImportEntityRecordsFromCsvInternal (core logic)
        // Migrated from: ImportExportManager.ImportEntityRecordsFromCsv
        // ===============================================================

        /// <summary>
        /// Core import logic: retrieves CSV from S3, parses rows, creates/updates records.
        /// </summary>
        private async Task<ResponseModel> ImportEntityRecordsFromCsvInternal(
            string entityName, string fileTempPath,
            APIGatewayHttpApiV2ProxyRequest request, string correlationId)
        {
            var response = new ResponseModel();
            response.Timestamp = DateTime.UtcNow;

            // --- Load primary entity via GetEntity for direct lookup ---
            var entity = await _entityService.GetEntity(entityName);
            if (entity == null)
            {
                response.Success = false;
                response.Message = $"Entity '{entityName}' not found.";
                return response;
            }

            // --- Load all entities for relation resolution across bounded contexts ---
            EntityListResponse entityListResp = await _entityService.ReadEntities();
            var entityList = entityListResp?.Object ?? new List<Entity>();

            if (!entityList.Any(e => e.Id == entity.Id))
            {
                // Entity was found by name but not in full list — stale cache, force refresh
                _entityService.ClearCache();
                EntityListResponse refreshedResp = await _entityService.ReadEntities();
                entityList = refreshedResp?.Object ?? new List<Entity>();
            }

            // --- Permission check ---
            if (!HasPermission(request, entity, EntityPermission.Create))
            {
                response.Success = false;
                response.Message = "You do not have permission to import records for this entity.";
                response.StatusCode = HttpStatusCode.Forbidden;
                return response;
            }

            // --- File path normalization (preserving monolith behavior) ---
            // Source: ImportExportManager.cs lines 34-50
            if (fileTempPath.StartsWith("/fs"))
                fileTempPath = fileTempPath.Substring(3);
            if (fileTempPath.StartsWith("fs/"))
                fileTempPath = fileTempPath.Substring(3);
            if (fileTempPath.StartsWith("fs"))
                fileTempPath = fileTempPath.Substring(2);
            if (!fileTempPath.StartsWith("/"))
                fileTempPath = "/" + fileTempPath;
            fileTempPath = fileTempPath.ToLowerInvariant();

            // --- Retrieve CSV file from S3 ---
            Stream csvStream;
            try
            {
                var s3Key = _filesTempPrefix + fileTempPath.TrimStart('/');
                var getObjectRequest = new GetObjectRequest
                {
                    BucketName = _s3BucketName,
                    Key = s3Key
                };
                var s3Response = await _s3Client.GetObjectAsync(getObjectRequest);
                csvStream = s3Response.ResponseStream;
            }
            catch (Amazon.S3.AmazonS3Exception s3Ex)
            {
                _logger.LogError(s3Ex, "[{CorrelationId}] Failed to retrieve CSV from S3: {Path}", correlationId, fileTempPath);
                response.Success = false;
                response.Message = $"CSV file not found: {fileTempPath}";
                return response;
            }

            // --- Load all entity relations for relation field processing ---
            var relationsResp = await _entityService.ReadRelations();
            var relations = relationsResp?.Object ?? new List<EntityRelation>();

            // --- Parse CSV with CsvHelper ---
            int createdCount = 0;
            int updatedCount = 0;
            var errors = new List<ErrorModel>();

            try
            {
                using var reader = new StreamReader(csvStream, Encoding.UTF8);
                var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    HeaderValidated = null
                };
                using var csvReader = new CsvReader(reader, csvConfig);

                csvReader.Read();
                csvReader.ReadHeader();
                var headerColumns = csvReader.Context.Reader.HeaderRecord ?? Array.Empty<string>();

                // Build column-to-field metadata map
                var columnFieldMap = BuildColumnFieldMap(headerColumns, entity, relations, entityList);

                int rowIndex = 0;
                while (csvReader.Read())
                {
                    rowIndex++;
                    try
                    {
                        var record = new EntityRecord();
                        bool hasIdColumn = false;
                        Guid recordId = Guid.Empty;

                        for (int col = 0; col < headerColumns.Length; col++)
                        {
                            var columnName = headerColumns[col];
                            var rawValue = csvReader.GetField(col);

                            // Handle "id" column
                            if (string.Equals(columnName, "id", StringComparison.OrdinalIgnoreCase))
                            {
                                hasIdColumn = true;
                                if (!string.IsNullOrWhiteSpace(rawValue) && Guid.TryParse(rawValue, out var parsedId))
                                {
                                    recordId = parsedId;
                                    record["id"] = recordId;
                                }
                                continue;
                            }

                            // Check if this is a relation field ($ prefix)
                            if (columnName.StartsWith(RELATION_NAME_RESULT_SEPARATOR))
                            {
                                await ProcessRelationFieldForImport(
                                    record, columnName, rawValue, entity, entityList, relations, correlationId);
                                continue;
                            }

                            // Regular field: look up field metadata and convert value
                            if (columnFieldMap.TryGetValue(columnName, out var fieldMeta) && fieldMeta != null)
                            {
                                var typedValue = ConvertCsvFieldValue(rawValue, fieldMeta);
                                record[columnName] = typedValue;
                            }
                            else
                            {
                                // Unknown column — store as raw string
                                record[columnName] = rawValue;
                            }
                        }

                        // Create or update based on id presence
                        if (hasIdColumn && recordId != Guid.Empty)
                        {
                            var updateResp = await _recordService.UpdateRecord(entityName, record);
                            if (updateResp != null && updateResp.Success)
                                updatedCount++;
                            else
                                errors.Add(new ErrorModel("row", rowIndex.ToString(),
                                    $"Failed to update record at row {rowIndex}: {updateResp?.Message ?? "Unknown error"}"));
                        }
                        else
                        {
                            if (!record.ContainsKey("id") || record["id"] == null)
                                record["id"] = Guid.NewGuid();

                            var createResp = await _recordService.CreateRecord(entityName, record);
                            if (createResp != null && createResp.Success)
                                createdCount++;
                            else
                                errors.Add(new ErrorModel("row", rowIndex.ToString(),
                                    $"Failed to create record at row {rowIndex}: {createResp?.Message ?? "Unknown error"}"));
                        }
                    }
                    catch (Exception rowEx)
                    {
                        _logger.LogWarning(rowEx, "[{CorrelationId}] Error processing row {Row}", correlationId, rowIndex);
                        errors.Add(new ErrorModel("row", rowIndex.ToString(), $"Error at row {rowIndex}: {rowEx.Message}"));
                    }
                }
            }
            catch (Exception csvEx)
            {
                _logger.LogError(csvEx, "[{CorrelationId}] CSV parsing failed", correlationId);
                response.Success = false;
                response.Message = _isDevelopmentMode
                    ? $"CSV parsing error: {csvEx.Message}\n{csvEx.StackTrace}"
                    : "Failed to parse CSV file.";
                return response;
            }

            response.Success = errors.Count == 0;
            response.Message = $"Import completed. Created: {createdCount}, Updated: {updatedCount}, Errors: {errors.Count}";
            response.Errors = errors.Count > 0 ? errors : null;
            response.Object = new
            {
                created = createdCount,
                updated = updatedCount,
                errors = errors.Count,
                total = createdCount + updatedCount + errors.Count
            };

            _logger.LogInformation(
                "[{CorrelationId}] ImportFromCsv completed — Created: {Created}, Updated: {Updated}, Errors: {Errors}",
                correlationId, createdCount, updatedCount, errors.Count);

            return response;
        }

        // ===============================================================
        // PRIVATE — EvaluateImportEntityRecordsFromCsvInternal
        // Migrated from: ImportExportManager.EvaluateImportEntityRecordsFromCsv
        // ===============================================================

        /// <summary>
        /// Core evaluation pipeline: analyzes CSV data against entity schema,
        /// validates per-column field types, resolves relation references,
        /// checks permissions, and optionally executes the import.
        /// </summary>
        private async Task<ResponseModel> EvaluateImportEntityRecordsFromCsvInternal(
            string entityName, JObject postObject,
            APIGatewayHttpApiV2ProxyRequest request, string correlationId)
        {
            var response = new ResponseModel();
            response.Timestamp = DateTime.UtcNow;

            // --- Load primary entity via GetEntity for direct lookup ---
            var entity = await _entityService.GetEntity(entityName);
            if (entity == null)
            {
                response.Success = false;
                response.Message = $"Entity '{entityName}' not found.";
                return response;
            }

            // --- Load all entities and relations for cross-entity resolution ---
            EntityListResponse entityListResp = await _entityService.ReadEntities();
            var entityList = entityListResp?.Object ?? new List<Entity>();

            var relationsResp = await _entityService.ReadRelations();
            var relations = relationsResp?.Object ?? new List<EntityRelation>();

            // --- Parse input parameters ---
            var fileTempPath = postObject["fileTempPath"]?.ToString() ?? string.Empty;
            var clipboard = postObject["clipboard"]?.ToString() ?? string.Empty;
            var generalCommand = postObject["general_command"]?.ToString() ?? "evaluate";

            // Parse per-column commands from request (key = column index or name, value = command)
            var columnCommands = new Dictionary<string, string>();
            if (postObject["commands"] is JObject commandsObj)
            {
                foreach (var prop in commandsObj.Properties())
                {
                    columnCommands[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                }
            }

            // --- Determine CSV source: file from S3 or clipboard text ---
            TextReader textReader;
            string delimiter = ",";

            if (!string.IsNullOrWhiteSpace(clipboard))
            {
                // Clipboard source — tab-delimited
                textReader = new StringReader(clipboard);
                delimiter = "\t";
            }
            else if (!string.IsNullOrWhiteSpace(fileTempPath))
            {
                // File path normalization (same as ImportFromCsv)
                if (fileTempPath.StartsWith("/fs"))
                    fileTempPath = fileTempPath.Substring(3);
                if (fileTempPath.StartsWith("fs/"))
                    fileTempPath = fileTempPath.Substring(3);
                if (fileTempPath.StartsWith("fs"))
                    fileTempPath = fileTempPath.Substring(2);
                if (!fileTempPath.StartsWith("/"))
                    fileTempPath = "/" + fileTempPath;
                fileTempPath = fileTempPath.ToLowerInvariant();

                try
                {
                    var s3Key = _filesTempPrefix + fileTempPath.TrimStart('/');
                    var s3Response = await _s3Client.GetObjectAsync(new GetObjectRequest
                    {
                        BucketName = _s3BucketName,
                        Key = s3Key
                    });
                    textReader = new StreamReader(s3Response.ResponseStream, Encoding.UTF8);
                }
                catch (Amazon.S3.AmazonS3Exception s3Ex)
                {
                    _logger.LogError(s3Ex, "[{CorrelationId}] Failed to retrieve CSV from S3: {Path}", correlationId, fileTempPath);
                    response.Success = false;
                    response.Message = $"CSV file not found: {fileTempPath}";
                    return response;
                }
            }
            else
            {
                response.Success = false;
                response.Message = "Either 'fileTempPath' or 'clipboard' data must be provided.";
                return response;
            }

            // --- Initialize evaluation result object ---
            // Structure matches monolith's evaluation response with columns, records, errors, warnings, stats
            var evalColumns = new List<Dictionary<string, object?>>();
            var evalRecords = new List<EntityRecord>();
            var evalErrors = new List<ErrorModel>();
            var evalWarnings = new List<string>();
            var stats = new Dictionary<string, int>
            {
                ["to_create"] = 0,
                ["to_update"] = 0,
                ["errors"] = 0,
                ["total_records"] = 0,
                ["created"] = 0,
                ["updated"] = 0
            };

            try
            {
                var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    MissingFieldFound = null,
                    HeaderValidated = null,
                    Delimiter = delimiter
                };
                using var csvReader = new CsvReader(textReader, csvConfig);
                csvReader.Read();
                csvReader.ReadHeader();
                var headerColumns = csvReader.Context.Reader.HeaderRecord ?? Array.Empty<string>();

                // ============================================================
                // PHASE A: Per-column header analysis
                // ============================================================
                var columnMetas = new List<ColumnAnalysis>();

                for (int colIdx = 0; colIdx < headerColumns.Length; colIdx++)
                {
                    var colName = headerColumns[colIdx].Trim();
                    var analysis = AnalyzeColumn(
                        colIdx, colName, entity, entityList, relations,
                        columnCommands, request, correlationId);
                    columnMetas.Add(analysis);

                    // Build column descriptor for response
                    var colDesc = new Dictionary<string, object?>
                    {
                        ["index"] = colIdx,
                        ["name"] = colName,
                        ["field_name"] = analysis.MappedFieldName,
                        ["field_type"] = analysis.MappedFieldType?.ToString(),
                        ["command"] = analysis.Command,
                        ["is_relation"] = analysis.IsRelationField,
                        ["relation_name"] = analysis.RelationName,
                        ["relation_field_name"] = analysis.RelationFieldName,
                        ["relation_direction"] = analysis.RelationDirection,
                        ["errors"] = analysis.Errors.Count > 0 ? analysis.Errors : null,
                        ["warnings"] = analysis.Warnings.Count > 0 ? analysis.Warnings : null
                    };
                    evalColumns.Add(colDesc);

                    // Accumulate column-level errors
                    foreach (var err in analysis.Errors)
                    {
                        evalErrors.Add(new ErrorModel("column", colIdx.ToString(), err));
                    }
                }

                // ============================================================
                // PHASE B: Row-by-row data validation
                // ============================================================
                int rowIndex = 0;
                while (csvReader.Read())
                {
                    rowIndex++;
                    stats["total_records"] = rowIndex;

                    var record = new EntityRecord();
                    bool hasId = false;
                    Guid rowId = Guid.Empty;
                    var rowErrors = new List<string>();

                    for (int colIdx = 0; colIdx < headerColumns.Length; colIdx++)
                    {
                        var rawValue = csvReader.GetField(colIdx) ?? string.Empty;
                        var colAnalysis = columnMetas[colIdx];
                        var colName = headerColumns[colIdx].Trim();

                        // Skip columns with "no_import" command
                        if (colAnalysis.Command == "no_import")
                            continue;

                        // Handle "id" column
                        if (string.Equals(colName, "id", StringComparison.OrdinalIgnoreCase))
                        {
                            hasId = true;
                            if (!string.IsNullOrWhiteSpace(rawValue))
                            {
                                if (Guid.TryParse(rawValue, out var parsedId))
                                {
                                    rowId = parsedId;
                                    record["id"] = rowId;
                                }
                                else
                                {
                                    rowErrors.Add($"Row {rowIndex}, column 'id': Invalid GUID value '{rawValue}'.");
                                }
                            }
                            continue;
                        }

                        // Relation field
                        if (colAnalysis.IsRelationField && colAnalysis.RelationEntity != null &&
                            colAnalysis.RelationField != null && colAnalysis.Relation != null)
                        {
                            // Validate referenced record exists
                            var relationValidation = await ValidateRelationFieldValue(
                                rawValue, colAnalysis, entity, entityList, relations, rowIndex, correlationId);
                            if (relationValidation.HasError)
                            {
                                rowErrors.Add(relationValidation.ErrorMessage!);
                            }
                            else if (relationValidation.ResolvedValue != null)
                            {
                                record[colAnalysis.TargetFieldInRecord!] = relationValidation.ResolvedValue;
                            }
                            continue;
                        }

                        // Regular field validation
                        if (colAnalysis.MappedField != null)
                        {
                            var fieldValidation = ValidateFieldValue(rawValue, colAnalysis.MappedField, rowIndex, colName);
                            if (fieldValidation.HasError)
                            {
                                rowErrors.Add(fieldValidation.ErrorMessage!);
                            }
                            else
                            {
                                record[colAnalysis.MappedFieldName!] = fieldValidation.ConvertedValue;
                            }
                        }
                        else if (colAnalysis.Command == "to_create")
                        {
                            // New field will be created — store raw value
                            record[colName] = rawValue;
                        }
                    }

                    // Track create vs update
                    if (hasId && rowId != Guid.Empty)
                        stats["to_update"]++;
                    else
                        stats["to_create"]++;

                    // Add row-level errors to overall errors
                    foreach (var err in rowErrors)
                    {
                        evalErrors.Add(new ErrorModel("row", rowIndex.ToString(), err));
                        stats["errors"]++;
                    }

                    evalRecords.Add(record);
                }

                // ============================================================
                // PHASE C: Execute import if "evaluate-import" and no errors
                // ============================================================
                if (generalCommand == "evaluate-import" && evalErrors.Count == 0)
                {
                    // Permission check for both create and update
                    bool canCreate = HasPermission(request, entity, EntityPermission.Create);
                    bool canUpdate = HasPermission(request, entity, EntityPermission.Update);

                    if (stats["to_create"] > 0 && !canCreate)
                    {
                        response.Success = false;
                        response.Message = "You do not have permission to create records for this entity.";
                        response.StatusCode = HttpStatusCode.Forbidden;
                        return response;
                    }
                    if (stats["to_update"] > 0 && !canUpdate)
                    {
                        response.Success = false;
                        response.Message = "You do not have permission to update records for this entity.";
                        response.StatusCode = HttpStatusCode.Forbidden;
                        return response;
                    }

                    // Create new fields if any columns have "to_create" command
                    foreach (var colAnalysis in columnMetas)
                    {
                        if (colAnalysis.Command == "to_create" && !colAnalysis.IsRelationField)
                        {
                            try
                            {
                                InputField newField = new InputTextField
                                {
                                    Id = Guid.NewGuid(),
                                    Name = colAnalysis.ColumnName,
                                    Label = colAnalysis.ColumnName,
                                    Required = false,
                                    Unique = false,
                                    Searchable = false,
                                    Auditable = false,
                                    System = false
                                };
                                await _entityService.CreateField(entity.Id, newField);
                                _logger.LogInformation(
                                    "[{CorrelationId}] Created new field '{FieldName}' (Id={FieldId}) on entity '{Entity}'",
                                    correlationId, colAnalysis.ColumnName, newField.Id, entityName);
                            }
                            catch (Exception fieldEx)
                            {
                                _logger.LogWarning(fieldEx,
                                    "[{CorrelationId}] Failed to create field '{Field}' on entity '{Entity}'",
                                    correlationId, colAnalysis.ColumnName, entityName);
                                evalErrors.Add(new ErrorModel("field", colAnalysis.ColumnName,
                                    $"Failed to create field '{colAnalysis.ColumnName}': {fieldEx.Message}"));
                            }
                        }
                    }

                    // Refresh entity definition after field creation to pick up new fields
                    var refreshedEntityResp = await _entityService.ReadEntity(entity.Name);
                    if (refreshedEntityResp?.Object != null)
                    {
                        entity = refreshedEntityResp.Object;
                    }

                    // Import records if no errors from field creation
                    if (evalErrors.Count == 0)
                    {
                        foreach (var record in evalRecords)
                        {
                            try
                            {
                                bool hasId = record.ContainsKey("id") && record["id"] != null
                                             && record["id"] is Guid gid && gid != Guid.Empty;
                                if (hasId)
                                {
                                    var updateResp = await _recordService.UpdateRecord(entityName, record);
                                    if (updateResp != null && updateResp.Success)
                                        stats["updated"]++;
                                    else
                                        evalErrors.Add(new ErrorModel("record", record.ContainsKey("id") ? record["id"]?.ToString() ?? "" : "",
                                            $"Update failed: {updateResp?.Message ?? "Unknown"}"));
                                }
                                else
                                {
                                    if (!record.ContainsKey("id") || record["id"] == null)
                                        record["id"] = Guid.NewGuid();

                                    var createResp = await _recordService.CreateRecord(entityName, record);
                                    if (createResp != null && createResp.Success)
                                        stats["created"]++;
                                    else
                                        evalErrors.Add(new ErrorModel("record", record.ContainsKey("id") ? record["id"]?.ToString() ?? "" : "",
                                            $"Create failed: {createResp?.Message ?? "Unknown"}"));
                                }
                            }
                            catch (Exception recEx)
                            {
                                evalErrors.Add(new ErrorModel("record", "",
                                    $"Record operation failed: {recEx.Message}"));
                            }
                        }

                        // Clear entity cache after import
                        _entityService.ClearCache();
                    }
                }
            }
            catch (Exception csvEx)
            {
                _logger.LogError(csvEx, "[{CorrelationId}] Evaluate CSV parsing failed", correlationId);
                response.Success = false;
                response.Message = _isDevelopmentMode
                    ? $"CSV parsing error: {csvEx.Message}\n{csvEx.StackTrace}"
                    : "Failed to parse CSV data.";
                return response;
            }
            finally
            {
                textReader.Dispose();
            }

            // Build evaluation response
            response.Success = evalErrors.Count == 0;
            response.Message = evalErrors.Count == 0
                ? (generalCommand == "evaluate-import"
                    ? $"Import completed. Created: {stats["created"]}, Updated: {stats["updated"]}."
                    : "Evaluation completed successfully.")
                : $"Evaluation found {evalErrors.Count} error(s).";
            response.Errors = evalErrors.Count > 0 ? evalErrors : null;
            response.Object = new
            {
                columns = evalColumns,
                records = evalRecords,
                stats,
                warnings = evalWarnings.Count > 0 ? evalWarnings : null,
                command = generalCommand
            };

            return response;
        }

        // ===============================================================
        // PRIVATE — Column Analysis
        // ===============================================================

        /// <summary>
        /// Analyzes a single CSV column header against the entity schema.
        /// Determines field mapping, relation resolution, and default commands.
        /// Migrated from: ImportExportManager.cs evaluation header analysis loop.
        /// </summary>
        private ColumnAnalysis AnalyzeColumn(
            int colIdx, string colName, Entity entity, List<Entity> entityList,
            List<EntityRelation> relations, Dictionary<string, string> columnCommands,
            APIGatewayHttpApiV2ProxyRequest request, string correlationId)
        {
            var analysis = new ColumnAnalysis
            {
                ColumnIndex = colIdx,
                ColumnName = colName,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };

            // Check for explicit command override
            var colIdxStr = colIdx.ToString();
            if (columnCommands.ContainsKey(colIdxStr))
                analysis.Command = columnCommands[colIdxStr];
            else if (columnCommands.ContainsKey(colName))
                analysis.Command = columnCommands[colName];

            // Handle "id" column
            if (string.Equals(colName, "id", StringComparison.OrdinalIgnoreCase))
            {
                analysis.MappedFieldName = "id";
                analysis.MappedFieldType = FieldType.GuidField;
                analysis.Command = analysis.Command ?? "to_update";
                return analysis;
            }

            // Check if column is a relation field ($relationName.fieldName or $$relationName.fieldName)
            if (colName.StartsWith(RELATION_NAME_RESULT_SEPARATOR))
            {
                analysis.IsRelationField = true;
                ParseRelationColumnHeader(analysis, colName, entity, entityList, relations);

                // Default command for relation fields
                if (string.IsNullOrEmpty(analysis.Command))
                    analysis.Command = analysis.Errors.Count > 0 ? "no_import" : "to_update";

                return analysis;
            }

            // Regular field — look up in entity fields
            var field = entity.Fields?.FirstOrDefault(f =>
                string.Equals(f.Name, colName, StringComparison.OrdinalIgnoreCase));

            if (field != null)
            {
                analysis.MappedField = field;
                analysis.MappedFieldName = field.Name;
                analysis.MappedFieldType = field.GetFieldType();

                if (string.IsNullOrEmpty(analysis.Command))
                    analysis.Command = "to_update";
            }
            else
            {
                // Unknown field — default to "to_create" (new field will be created)
                analysis.MappedFieldName = colName;
                analysis.MappedFieldType = FieldType.TextField;

                if (string.IsNullOrEmpty(analysis.Command))
                    analysis.Command = "to_create";

                analysis.Warnings.Add($"Field '{colName}' does not exist on entity '{entity.Name}'. " +
                                      "It will be created as a TextField on import execution.");
            }

            return analysis;
        }

        /// <summary>
        /// Parses a relation column header ($relationName.fieldName or $$relationName.fieldName)
        /// and validates the relation metadata.
        /// Migrated from: ImportExportManager.cs lines ~500-600 relation header analysis.
        /// </summary>
        private void ParseRelationColumnHeader(
            ColumnAnalysis analysis, string colName,
            Entity entity, List<Entity> entityList, List<EntityRelation> relations)
        {
            // Strip leading $ characters to determine direction
            // $  = single dollar: standard direction
            // $$ = double dollar: reverse direction
            var stripped = colName.TrimStart(RELATION_NAME_RESULT_SEPARATOR);
            var dollarCount = colName.Length - stripped.Length;
            analysis.RelationDirection = dollarCount == 1 ? "origin-to-target" : "target-to-origin";

            // Split by RELATION_SEPARATOR to get relationName and fieldName
            var separatorIdx = stripped.IndexOf(RELATION_SEPARATOR);
            if (separatorIdx <= 0 || separatorIdx >= stripped.Length - 1)
            {
                analysis.Errors.Add($"Invalid relation column format: '{colName}'. " +
                                    $"Expected format: ${RELATION_NAME_RESULT_SEPARATOR}relationName{RELATION_SEPARATOR}fieldName");
                return;
            }

            var relationName = stripped.Substring(0, separatorIdx);
            var relationFieldName = stripped.Substring(separatorIdx + 1);
            analysis.RelationName = relationName;
            analysis.RelationFieldName = relationFieldName;

            // Find the relation by name
            var relation = relations.FirstOrDefault(r =>
                string.Equals(r.Name, relationName, StringComparison.OrdinalIgnoreCase));

            if (relation == null)
            {
                analysis.Errors.Add($"Relation '{relationName}' not found.");
                return;
            }

            analysis.Relation = relation;

            // Determine which entity is the "other" side based on direction
            // $ (single) = standard: if entity is origin, look at target; if entity is target, look at origin
            // $$ (double) = reverse: flip the standard direction
            Guid otherEntityId;
            Guid thisFieldId;

            bool isEntityOrigin = relation.OriginEntityId == entity.Id;
            bool isEntityTarget = relation.TargetEntityId == entity.Id;

            if (!isEntityOrigin && !isEntityTarget)
            {
                // Self-referencing entity: both sides are the same entity
                if (relation.OriginEntityId == relation.TargetEntityId && relation.OriginEntityId == entity.Id)
                {
                    isEntityOrigin = true;
                    isEntityTarget = true;
                }
                else
                {
                    analysis.Errors.Add($"Relation '{relationName}' does not belong to entity '{entity.Name}'.");
                    return;
                }
            }

            if (dollarCount == 1)
            {
                // Standard direction
                if (isEntityOrigin)
                {
                    otherEntityId = relation.TargetEntityId;
                    thisFieldId = relation.OriginFieldId;
                }
                else
                {
                    otherEntityId = relation.OriginEntityId;
                    thisFieldId = relation.TargetFieldId;
                }
            }
            else
            {
                // Reverse direction ($$)
                if (isEntityTarget)
                {
                    otherEntityId = relation.OriginEntityId;
                    thisFieldId = relation.TargetFieldId;
                }
                else
                {
                    otherEntityId = relation.TargetEntityId;
                    thisFieldId = relation.OriginFieldId;
                }
            }

            // Find the other entity
            var otherEntity = entityList.FirstOrDefault(e => e.Id == otherEntityId);
            if (otherEntity == null)
            {
                analysis.Errors.Add($"Related entity not found for relation '{relationName}'.");
                return;
            }

            analysis.RelationEntity = otherEntity;

            // Find the field on the other entity that we're matching against
            var otherField = otherEntity.Fields?.FirstOrDefault(f =>
                string.Equals(f.Name, relationFieldName, StringComparison.OrdinalIgnoreCase));

            if (otherField == null)
            {
                analysis.Errors.Add($"Field '{relationFieldName}' not found on related entity '{otherEntity.Name}'.");
                return;
            }

            analysis.RelationField = otherField;

            // Find the local field that holds the FK value
            var thisField = entity.Fields?.FirstOrDefault(f => f.Id == thisFieldId);
            analysis.TargetFieldInRecord = thisField?.Name ?? "id";

            // Validate: MultiSelectField cannot be used as relation key
            if (otherField.GetFieldType() == FieldType.MultiSelectField)
            {
                analysis.Errors.Add($"MultiSelect fields cannot be used as relation lookup keys " +
                                    $"(field '{relationFieldName}' on entity '{otherEntity.Name}').");
                return;
            }

            // Validate OneToOne restrictions with id-based relations
            if (relation.RelationType == EntityRelationType.OneToOne)
            {
                var originField = FindFieldById(entityList, relation.OriginEntityId, relation.OriginFieldId);
                var targetField = FindFieldById(entityList, relation.TargetEntityId, relation.TargetFieldId);

                if (originField != null && targetField != null
                    && string.Equals(originField.Name, "id", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(targetField.Name, "id", StringComparison.OrdinalIgnoreCase))
                {
                    analysis.Errors.Add($"OneToOne relations with both sides using 'id' fields are " +
                                        "not supported for CSV import.");
                    return;
                }
            }
        }

        // ===============================================================
        // PRIVATE — Relation Field Processing for Simple Import
        // ===============================================================

        /// <summary>
        /// Processes a relation field column during simple CSV import.
        /// Looks up the referenced record and sets the FK value in the record.
        /// </summary>
        private async Task ProcessRelationFieldForImport(
            EntityRecord record, string columnName, string? rawValue,
            Entity entity, List<Entity> entityList, List<EntityRelation> relations,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return;

            // Parse the column header
            var analysis = new ColumnAnalysis
            {
                ColumnName = columnName,
                Errors = new List<string>(),
                Warnings = new List<string>()
            };
            ParseRelationColumnHeader(analysis, columnName, entity, entityList, relations);

            if (analysis.Errors.Count > 0 || analysis.RelationEntity == null ||
                analysis.RelationField == null || analysis.Relation == null)
            {
                _logger.LogWarning("[{CorrelationId}] Skipping invalid relation column '{Column}': {Errors}",
                    correlationId, columnName, string.Join("; ", analysis.Errors));
                return;
            }

            // Look up the referenced record using the field value
            var lookupQuery = new EntityQuery(
                analysis.RelationEntity.Name,
                "*",
                EntityQuery.QueryEQ(analysis.RelationFieldName!, rawValue));

            QueryResponse lookupResult = await _recordService.Find(lookupQuery);
            var data = lookupResult?.Object?.Data;

            if (data != null && data.Count > 0)
            {
                // Get the "id" of the matched record
                var matchedRecord = data.First();
                if (matchedRecord.ContainsKey("id") && matchedRecord["id"] != null)
                {
                    record[analysis.TargetFieldInRecord!] = matchedRecord["id"];
                }
            }
            else
            {
                _logger.LogWarning(
                    "[{CorrelationId}] No matching record found for relation column '{Column}' with value '{Value}'",
                    correlationId, columnName, rawValue);
            }
        }

        // ===============================================================
        // PRIVATE — Relation Field Validation for EvaluateImport
        // ===============================================================

        /// <summary>
        /// Validates a relation field value during the evaluation pipeline.
        /// Checks that referenced records exist and are unique when required.
        /// Migrated from: ImportExportManager.cs row iteration relation validation.
        /// </summary>
        private async Task<FieldValidationResult> ValidateRelationFieldValue(
            string rawValue, ColumnAnalysis colAnalysis,
            Entity entity, List<Entity> entityList, List<EntityRelation> relations,
            int rowIndex, string correlationId)
        {
            var result = new FieldValidationResult();

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                // Empty relation value — acceptable (nullable FK)
                result.ResolvedValue = null;
                return result;
            }

            if (colAnalysis.RelationEntity == null || colAnalysis.RelationField == null)
            {
                result.HasError = true;
                result.ErrorMessage = $"Row {rowIndex}: Relation metadata not available for column '{colAnalysis.ColumnName}'.";
                return result;
            }

            // Build lookup query — handle multi-value (comma-separated for ManyToMany)
            var values = rawValue.Contains(',')
                ? rawValue.Split(',').Select(v => v.Trim()).Where(v => !string.IsNullOrEmpty(v)).ToList()
                : new List<string> { rawValue.Trim() };

            QueryObject? queryObj = null;
            if (values.Count == 1)
            {
                queryObj = EntityQuery.QueryEQ(colAnalysis.RelationFieldName!, values[0]);
            }
            else
            {
                // Multiple values: build OR query
                var orQueries = values
                    .Select(v => EntityQuery.QueryEQ(colAnalysis.RelationFieldName!, v))
                    .ToList();
                queryObj = orQueries.Count > 0 ? EntityQuery.QueryOR(orQueries.ToArray()) : null;
            }

            if (queryObj == null)
            {
                result.HasError = true;
                result.ErrorMessage = $"Row {rowIndex}: Could not build query for relation column '{colAnalysis.ColumnName}'.";
                return result;
            }

            var lookupQuery = new EntityQuery(
                colAnalysis.RelationEntity.Name,
                "*",
                queryObj);

            QueryResponse lookupResult = await _recordService.Find(lookupQuery);
            var data = lookupResult?.Object?.Data;

            if (data == null || data.Count == 0)
            {
                result.HasError = true;
                result.ErrorMessage = $"Row {rowIndex}, column '{colAnalysis.ColumnName}': " +
                                      $"No matching record found in entity '{colAnalysis.RelationEntity.Name}' " +
                                      $"with {colAnalysis.RelationFieldName} = '{rawValue}'.";
                return result;
            }

            // Validate cardinality constraints
            if (colAnalysis.Relation!.RelationType == EntityRelationType.OneToOne ||
                colAnalysis.Relation.RelationType == EntityRelationType.OneToMany)
            {
                if (values.Count > 1)
                {
                    result.HasError = true;
                    result.ErrorMessage = $"Row {rowIndex}, column '{colAnalysis.ColumnName}': " +
                                          $"OneToOne/OneToMany relation does not allow multiple values.";
                    return result;
                }

                if (data.Count > 1)
                {
                    result.HasError = true;
                    result.ErrorMessage = $"Row {rowIndex}, column '{colAnalysis.ColumnName}': " +
                                          $"Multiple records found for value '{rawValue}' — expected unique match.";
                    return result;
                }
            }

            // Extract the FK value from matched record(s)
            var matchedRecord = data.First();
            if (matchedRecord.ContainsKey("id") && matchedRecord["id"] != null)
            {
                result.ResolvedValue = matchedRecord["id"];
            }
            else
            {
                result.HasError = true;
                result.ErrorMessage = $"Row {rowIndex}, column '{colAnalysis.ColumnName}': " +
                                      "Matched record has no 'id' field.";
            }

            return result;
        }

        // ===============================================================
        // PRIVATE — Field Value Validation for EvaluateImport
        // ===============================================================

        /// <summary>
        /// Validates a regular field value during the evaluation pipeline.
        /// Checks type constraints, requiredness, and SelectField option membership.
        /// Migrated from: ImportExportManager.cs row iteration field validation.
        /// </summary>
        private FieldValidationResult ValidateFieldValue(
            string rawValue, Field field, int rowIndex, string columnName)
        {
            var result = new FieldValidationResult();
            var fieldType = field.GetFieldType();

            // Required field check
            if (field.Required && string.IsNullOrWhiteSpace(rawValue))
            {
                result.HasError = true;
                result.ErrorMessage = $"Row {rowIndex}, column '{columnName}': " +
                                      $"Required field '{field.Name}' has empty value.";
                return result;
            }

            // Empty value is valid for optional fields
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                result.ConvertedValue = null;
                return result;
            }

            try
            {
                // Use RecordRepository.ExtractFieldValue for type conversion
                Field fieldMeta = field;
                result.ConvertedValue = RecordRepository.ExtractFieldValue(rawValue, fieldMeta, true);

                // Additional validation for SelectField: check option membership
                if (fieldType == FieldType.SelectField && field is SelectField selectField)
                {
                    if (selectField.Options != null && selectField.Options.Count > 0)
                    {
                        var strValue = rawValue.Trim();
                        if (!selectField.Options.Any(o =>
                            string.Equals(o.Value, strValue, StringComparison.OrdinalIgnoreCase)))
                        {
                            result.HasError = true;
                            result.ErrorMessage = $"Row {rowIndex}, column '{columnName}': " +
                                                  $"Value '{rawValue}' is not a valid option for select field '{field.Name}'. " +
                                                  $"Valid options: {string.Join(", ", selectField.Options.Select(o => o.Value))}";
                            result.ConvertedValue = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.HasError = true;
                result.ErrorMessage = $"Row {rowIndex}, column '{columnName}': " +
                                      $"Cannot convert value '{rawValue}' to {fieldType}: {ex.Message}";
            }

            return result;
        }

        // ===============================================================
        // PRIVATE — CSV Field Value Conversion for Simple Import
        // ===============================================================

        /// <summary>
        /// Converts a raw CSV string value to a typed field value based on field metadata.
        /// Uses <see cref="RecordRepository.ExtractFieldValue"/> for standard type coercion,
        /// with additional handling for JSON array notation in multi-select fields.
        /// Migrated from: ImportExportManager.cs FieldType switch in import loop.
        /// </summary>
        private static object? ConvertCsvFieldValue(string? rawValue, Field field)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
                return null;

            var fieldType = field.GetFieldType();

            // Special handling for multi-select: detect JSON array notation [...]
            if (fieldType == FieldType.MultiSelectField)
            {
                var trimmed = rawValue.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    try
                    {
                        var list = JsonConvert.DeserializeObject<List<string>>(trimmed);
                        return list ?? new List<string>();
                    }
                    catch
                    {
                        // Fall through to standard extraction
                    }
                }

                // Comma-separated values fallback
                if (trimmed.Contains(','))
                {
                    return trimmed.Split(',')
                        .Select(v => v.Trim())
                        .Where(v => !string.IsNullOrEmpty(v))
                        .ToList();
                }

                return new List<string> { trimmed };
            }

            // Delegate to RecordRepository.ExtractFieldValue for all standard types
            return RecordRepository.ExtractFieldValue(rawValue, field, true);
        }

        // ===============================================================
        // PRIVATE — Column-to-Field Mapping Builder
        // ===============================================================

        /// <summary>
        /// Builds a dictionary mapping CSV column names to entity field metadata.
        /// Excludes relation columns ($ prefix) and the "id" column.
        /// </summary>
        private static Dictionary<string, Field> BuildColumnFieldMap(
            string[] headerColumns, Entity entity,
            List<EntityRelation> relations, List<Entity> entityList)
        {
            var map = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);

            if (entity.Fields == null)
                return map;

            foreach (var colName in headerColumns)
            {
                var trimmed = colName.Trim();

                // Skip relation columns and id
                if (trimmed.StartsWith(RELATION_NAME_RESULT_SEPARATOR))
                    continue;
                if (string.Equals(trimmed, "id", StringComparison.OrdinalIgnoreCase))
                    continue;

                var field = entity.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, trimmed, StringComparison.OrdinalIgnoreCase));

                if (field != null && !map.ContainsKey(trimmed))
                {
                    map[trimmed] = field;
                }
            }

            return map;
        }

        // ===============================================================
        // PRIVATE HELPER — Find field by ID in entity list
        // ===============================================================

        /// <summary>
        /// Finds a field by its ID within a specific entity, searching across all entities.
        /// </summary>
        private static Field? FindFieldById(List<Entity> entityList, Guid entityId, Guid fieldId)
        {
            var entity = entityList.FirstOrDefault(e => e.Id == entityId);
            return entity?.Fields?.FirstOrDefault(f => f.Id == fieldId);
        }

        // ===============================================================
        // PRIVATE HELPER — Request Parameter Extraction
        // ===============================================================

        /// <summary>
        /// Extracts a path parameter value from the request, returning empty string if not found.
        /// </summary>
        private static string GetParam(IDictionary<string, string>? parameters, string key)
        {
            if (parameters == null)
                return string.Empty;
            return parameters.TryGetValue(key, out var value) ? value : string.Empty;
        }

        // ===============================================================
        // PRIVATE HELPER — Correlation ID Extraction
        // ===============================================================

        /// <summary>
        /// Extracts correlation ID from request headers, falling back to Lambda request ID.
        /// Matches the sibling handler pattern (RecordHandler/EntityHandler).
        /// </summary>
        private static string ExtractCorrelationId(
            APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
        {
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
                    {
                        return header.Value;
                    }
                }
            }
            return context.AwsRequestId;
        }

        // ===============================================================
        // PRIVATE HELPER — User ID Extraction from JWT
        // ===============================================================

        /// <summary>
        /// Extracts the authenticated user ID from JWT claims in the request.
        /// Checks sub, userId, custom:userId claims and Lambda authorizer context.
        /// </summary>
        private static string? ExtractUserId(APIGatewayHttpApiV2ProxyRequest request)
        {
            var jwt = request.RequestContext?.Authorizer?.Jwt;
            if (jwt?.Claims != null)
            {
                if (jwt.Claims.TryGetValue("sub", out var sub) && !string.IsNullOrEmpty(sub))
                    return sub;
                if (jwt.Claims.TryGetValue("userId", out var uid) && !string.IsNullOrEmpty(uid))
                    return uid;
                if (jwt.Claims.TryGetValue("custom:userId", out var customUid) && !string.IsNullOrEmpty(customUid))
                    return customUid;
            }

            // Fallback: Lambda authorizer context
            var lambdaAuth = request.RequestContext?.Authorizer?.Lambda;
            if (lambdaAuth != null)
            {
                if (lambdaAuth.TryGetValue("userId", out var lambdaUid))
                    return lambdaUid?.ToString();
            }

            return null;
        }

        // ===============================================================
        // PRIVATE HELPER — Permission Checking
        // ===============================================================

        /// <summary>
        /// Checks if the request has the specified permission on the entity.
        /// Admin users bypass all permission checks.
        /// Matches the RecordHandler HasPermission pattern.
        /// </summary>
        private static bool HasPermission(
            APIGatewayHttpApiV2ProxyRequest request, Entity entity, EntityPermission permission)
        {
            // Admin bypass
            if (IsAdminUser(request))
                return true;

            var userRoles = ExtractUserRoles(request);

            // Check if any user role is in the entity's permission list
            return HasRoleInPermissionList(userRoles, entity, permission);
        }

        /// <summary>
        /// Checks if any of the user's roles appear in the entity's permission list
        /// for the specified permission type.
        /// </summary>
        private static bool HasRoleInPermissionList(
            List<Guid> userRoles, Entity entity, EntityPermission permission)
        {
            if (entity.RecordPermissions == null)
                return false;

            List<Guid>? permissionList = permission switch
            {
                EntityPermission.Read => entity.RecordPermissions.CanRead,
                EntityPermission.Create => entity.RecordPermissions.CanCreate,
                EntityPermission.Update => entity.RecordPermissions.CanUpdate,
                EntityPermission.Delete => entity.RecordPermissions.CanDelete,
                _ => null
            };

            if (permissionList == null || permissionList.Count == 0)
                return false;

            return userRoles.Any(role => permissionList.Contains(role));
        }

        /// <summary>
        /// Determines if the authenticated user is an administrator.
        /// Checks JWT cognito:groups, custom:roles, scope claims, and Lambda authorizer context.
        /// </summary>
        private static bool IsAdminUser(APIGatewayHttpApiV2ProxyRequest request)
        {
            var adminRoleId = SystemIds.AdministratorRoleId.ToString();

            var jwt = request.RequestContext?.Authorizer?.Jwt;
            if (jwt?.Claims != null)
            {
                // Check cognito:groups
                if (jwt.Claims.TryGetValue("cognito:groups", out var groups) && !string.IsNullOrEmpty(groups))
                {
                    if (groups.Contains(adminRoleId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Check custom:roles
                if (jwt.Claims.TryGetValue("custom:roles", out var roles) && !string.IsNullOrEmpty(roles))
                {
                    if (roles.Contains(adminRoleId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                // Check scope
                if (jwt.Claims.TryGetValue("scope", out var scope) && !string.IsNullOrEmpty(scope))
                {
                    if (scope.Contains(adminRoleId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Lambda authorizer context
            var lambdaAuth = request.RequestContext?.Authorizer?.Lambda;
            if (lambdaAuth != null)
            {
                if (lambdaAuth.TryGetValue("isAdmin", out var isAdmin))
                {
                    if (string.Equals(isAdmin?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                if (lambdaAuth.TryGetValue("roles", out var lambdaRoles))
                {
                    var rolesStr = lambdaRoles?.ToString() ?? string.Empty;
                    if (rolesStr.Contains(adminRoleId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Extracts all role GUIDs from the authenticated user's JWT claims.
        /// Parses from cognito:groups, custom:roles, and Lambda authorizer context.
        /// </summary>
        private static List<Guid> ExtractUserRoles(APIGatewayHttpApiV2ProxyRequest request)
        {
            var roles = new List<Guid>();

            var jwt = request.RequestContext?.Authorizer?.Jwt;
            if (jwt?.Claims != null)
            {
                if (jwt.Claims.TryGetValue("cognito:groups", out var groups))
                    ParseRoleGuids(groups, roles);
                if (jwt.Claims.TryGetValue("custom:roles", out var customRoles))
                    ParseRoleGuids(customRoles, roles);
            }

            // Lambda authorizer context
            var lambdaAuth = request.RequestContext?.Authorizer?.Lambda;
            if (lambdaAuth != null)
            {
                if (lambdaAuth.TryGetValue("roles", out var lambdaRoles))
                    ParseRoleGuids(lambdaRoles?.ToString(), roles);
            }

            // Guest role fallback if no roles found
            if (roles.Count == 0)
            {
                roles.Add(SystemIds.GuestRoleId);
            }

            return roles.Distinct().ToList();
        }

        /// <summary>
        /// Parses a delimited string of GUIDs into the roles list.
        /// Supports comma, space, pipe, and semicolon delimiters.
        /// </summary>
        private static void ParseRoleGuids(string? input, List<Guid> roles)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            var parts = input.Split(new[] { ',', ' ', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (Guid.TryParse(part.Trim(), out var guid))
                {
                    roles.Add(guid);
                }
            }
        }

        // ===============================================================
        // PRIVATE HELPER — Response Builders
        // ===============================================================

        /// <summary>
        /// Builds an API Gateway response with the specified status code, body, and correlation ID header.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildResponse(
            HttpStatusCode statusCode, object body, string correlationId)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)statusCode,
                Body = System.Text.Json.JsonSerializer.Serialize(body, _jsonOptions),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["X-Correlation-Id"] = correlationId
                }
            };
        }

        /// <summary>
        /// Builds an error response with the specified status code and message.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildErrorResponse(
            HttpStatusCode statusCode, string message, string correlationId,
            List<ErrorModel>? errors = null)
        {
            var responseBody = new BaseResponseModel
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Errors = errors,
                StatusCode = statusCode
            };
            return BuildResponse(statusCode, responseBody, correlationId);
        }

        /// <summary>
        /// Builds a validation error response with structured error details.
        /// </summary>
        private APIGatewayHttpApiV2ProxyResponse BuildValidationErrorResponse(
            HttpStatusCode statusCode, string message, List<ErrorModel> errors, string correlationId)
        {
            return BuildErrorResponse(statusCode, message, correlationId, errors);
        }

        // ===============================================================
        // PRIVATE HELPER — SNS Domain Event Publishing
        // ===============================================================

        /// <summary>
        /// Publishes a domain event to SNS for cross-service communication.
        /// Non-blocking: failures are logged but do not fail the request.
        /// Replaces monolith's synchronous post-hook pattern.
        /// </summary>
        private async Task PublishDomainEvent(string eventType, object eventData, string correlationId)
        {
            if (string.IsNullOrEmpty(_importTopicArn))
            {
                _logger.LogWarning("[{CorrelationId}] SNS topic ARN not configured — skipping domain event '{EventType}'",
                    correlationId, eventType);
                return;
            }

            try
            {
                var messageBody = JsonConvert.SerializeObject(eventData);
                var publishRequest = new PublishRequest
                {
                    TopicArn = _importTopicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["timestamp"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = DateTime.UtcNow.ToString("o")
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest);
                _logger.LogInformation("[{CorrelationId}] Published domain event '{EventType}'", correlationId, eventType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Failed to publish domain event '{EventType}'",
                    correlationId, eventType);
                // Non-blocking: do not rethrow — event publication failure should not fail the import
            }
        }

        // ===============================================================
        // PRIVATE INNER CLASSES — Analysis and Validation Models
        // ===============================================================

        /// <summary>
        /// Internal model capturing the analysis results for a single CSV column.
        /// Used during EvaluateImport header analysis phase.
        /// </summary>
        private class ColumnAnalysis
        {
            public int ColumnIndex { get; set; }
            public string ColumnName { get; set; } = string.Empty;
            public string? MappedFieldName { get; set; }
            public FieldType? MappedFieldType { get; set; }
            public Field? MappedField { get; set; }
            public string? Command { get; set; }
            public bool IsRelationField { get; set; }
            public string? RelationName { get; set; }
            public string? RelationFieldName { get; set; }
            public string? RelationDirection { get; set; }
            public EntityRelation? Relation { get; set; }
            public Entity? RelationEntity { get; set; }
            public Field? RelationField { get; set; }
            public string? TargetFieldInRecord { get; set; }
            public List<string> Errors { get; set; } = new();
            public List<string> Warnings { get; set; } = new();
        }

        /// <summary>
        /// Internal model capturing the validation result for a single field value.
        /// Used during EvaluateImport row iteration phase.
        /// </summary>
        private class FieldValidationResult
        {
            public bool HasError { get; set; }
            public string? ErrorMessage { get; set; }
            public object? ConvertedValue { get; set; }
            public object? ResolvedValue { get; set; }
        }
    }
}
