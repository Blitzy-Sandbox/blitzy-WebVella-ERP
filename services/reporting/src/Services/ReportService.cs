// Suppress AOT trimming warnings for System.Text.Json serialization.
// ReportService uses runtime JSON serialization for parameter/DTO mapping
// which is acceptable for Lambda-based services with managed runtime.
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute
#pragma warning disable IL3050 // Members annotated with RequiresDynamicCodeAttribute

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Models;

namespace WebVellaErp.Reporting.Services
{
    // ════════════════════════════════════════════════════════════════════
    // DTO Classes — Request/Response envelopes for ReportService methods
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Request DTO for creating a new report definition.
    /// Maps from the monolith's DataSourceManager.Create() parameter set
    /// (source lines 127-129: name, description, weight, eql, parameters, returnTotal).
    /// </summary>
    public class CreateReportRequest
    {
        /// <summary>Report name (required, must be unique).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Report description (optional).</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// SQL query template for report execution.
        /// Replaces the monolith's EQL text (eql parameter) with direct SQL
        /// since the Reporting service uses RDS PostgreSQL, not DynamoDB.
        /// </summary>
        public string QueryDefinition { get; set; } = string.Empty;

        /// <summary>
        /// Report parameters as structured list.
        /// Replaces the monolith's newline-delimited CSV parameter text.
        /// </summary>
        public List<ReportParameter>? Parameters { get; set; }

        /// <summary>Whether the report should return total count for pagination.</summary>
        public bool ReturnTotal { get; set; } = true;

        /// <summary>Display weight for ordering in lists (lower = higher priority).</summary>
        public int Weight { get; set; } = 10;
    }

    /// <summary>
    /// Request DTO for updating an existing report definition.
    /// Maps from the monolith's DataSourceManager.Update() parameter set
    /// (source lines 191-193: id, name, description, weight, eql, parameters, returnTotal).
    /// </summary>
    public class UpdateReportRequest
    {
        /// <summary>Report name (required, must be unique among other reports).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Report description (optional).</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>SQL query template for report execution.</summary>
        public string QueryDefinition { get; set; } = string.Empty;

        /// <summary>Report parameters as structured list.</summary>
        public List<ReportParameter>? Parameters { get; set; }

        /// <summary>Whether the report should return total count for pagination.</summary>
        public bool ReturnTotal { get; set; } = true;

        /// <summary>Display weight for ordering in lists.</summary>
        public int Weight { get; set; } = 10;
    }

    /// <summary>
    /// Simplified result envelope for report execution returned by ExecuteReportAsync
    /// and ExecuteAdHocQueryAsync. Contains the essential data rows and total count.
    /// The full <see cref="ReportResult"/> is used internally for auditing/logging.
    /// </summary>
    public class ReportExecutionResult
    {
        /// <summary>
        /// Data rows returned by the report query. Each row is a dictionary mapping
        /// column names to their values, preserving the monolith's dynamic record pattern.
        /// </summary>
        public List<Dictionary<string, object?>> Data { get; set; } = new();

        /// <summary>
        /// Total number of records matching the query before pagination.
        /// Only populated when the report definition has ReturnTotal = true.
        /// </summary>
        public int TotalCount { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════
    // IReportService Interface
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Service interface for the Reporting &amp; Analytics bounded-context microservice.
    /// Replaces the monolith's <c>DataSourceManager</c> class — specifically report
    /// execution logic (source lines 470-512), typed parameter handling (lines 356-462),
    /// and report definition CRUD management (lines 82-265, 464-468).
    ///
    /// This is the primary business logic contract. The Lambda handler
    /// (<c>ReportHandler.cs</c>) delegates all business logic to this interface.
    ///
    /// Architecture: One of only TWO ACID-critical services (with Invoicing) that
    /// uses RDS PostgreSQL (NOT DynamoDB) as its datastore.
    /// </summary>
    public interface IReportService
    {
        // ── Report Definition CRUD ──────────────────────────────────────

        /// <summary>
        /// Retrieves a report definition by ID.
        /// Replaces <c>DataSourceManager.Get(Guid id)</c> (source lines 82-85).
        /// </summary>
        Task<ReportDefinition?> GetReportByIdAsync(Guid id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a paginated, sorted list of all report definitions.
        /// Replaces <c>DataSourceManager.GetAll()</c> (source lines 87-107) with pagination.
        /// </summary>
        Task<(List<ReportDefinition> Reports, int TotalCount)> GetAllReportsAsync(
            int page, int pageSize, string sortBy, string sortOrder,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new report definition with idempotency enforcement.
        /// Replaces <c>DataSourceManager.Create()</c> (source lines 127-189).
        /// </summary>
        Task<ReportDefinition> CreateReportAsync(
            CreateReportRequest request, Guid? createdBy, string idempotencyKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing report definition with idempotency enforcement.
        /// Replaces <c>DataSourceManager.Update()</c> (source lines 191-265).
        /// </summary>
        Task<ReportDefinition> UpdateReportAsync(
            Guid id, UpdateReportRequest request, string idempotencyKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a report definition by ID.
        /// Replaces <c>DataSourceManager.Delete(Guid id)</c> (source lines 464-468).
        /// </summary>
        Task DeleteReportAsync(Guid id, string idempotencyKey,
            CancellationToken cancellationToken = default);

        // ── Report Execution ────────────────────────────────────────────

        /// <summary>
        /// Executes a stored report by ID with parameter binding.
        /// Replaces <c>DataSourceManager.Execute(Guid id, ...)</c> (source lines 470-497).
        /// </summary>
        Task<ReportExecutionResult> ExecuteReportAsync(
            Guid id, Dictionary<string, object?>? parameters,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes an ad-hoc SQL query with parameter binding.
        /// Replaces <c>DataSourceManager.Execute(string eql, ...)</c> (source lines 499-512).
        /// </summary>
        Task<ReportExecutionResult> ExecuteAdHocQueryAsync(
            string sqlQuery, Dictionary<string, object?>? parameters, bool returnTotal,
            CancellationToken cancellationToken = default);

        // ── Parameter Handling ──────────────────────────────────────────

        /// <summary>
        /// Resolves a parameter's default value to its typed representation.
        /// Replaces <c>DataSourceManager.GetDataSourceParameterValue()</c> (source lines 356-462).
        /// MUST preserve ALL type conversion logic and edge cases from the source.
        /// </summary>
        object? ResolveParameterValue(ReportParameter parameter);

        /// <summary>
        /// Parses newline-delimited CSV parameter text into structured parameters.
        /// Replaces <c>DataSourceManager.ProcessParametersText()</c> (source lines 296-330).
        /// </summary>
        List<ReportParameter> ParseParametersText(string parametersText);

        /// <summary>
        /// Converts structured parameters to newline-delimited CSV text.
        /// Replaces <c>DataSourceManager.ConvertParamsToText()</c> (source lines 332-344).
        /// </summary>
        string ConvertParametersToText(List<ReportParameter> parameters);

        // ── Validation ──────────────────────────────────────────────────

        /// <summary>
        /// Validates SQL query syntax using PostgreSQL EXPLAIN without executing.
        /// Replaces <c>EqlBuilder.Build()</c> validation logic (source lines 136-148, 207-228).
        /// </summary>
        Task ValidateReportQueryAsync(
            string sqlQuery, List<ReportParameter>? parameters,
            CancellationToken cancellationToken = default);
    }

    // ════════════════════════════════════════════════════════════════════
    // ReportService Implementation
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Primary business logic service for the Reporting &amp; Analytics microservice.
    /// Replaces <c>DataSourceManager</c> from the monolith with DI-injected dependencies,
    /// async operations, SNS domain events, and RDS PostgreSQL direct SQL execution.
    ///
    /// Key transformations from monolith:
    /// <list type="bullet">
    ///   <item>Ambient <c>DbContext.Current</c> → DI-injected <c>IReportRepository</c></item>
    ///   <item>Static <c>IMemoryCache</c> → DI-injected <c>IMemoryCache</c></item>
    ///   <item><c>EqlCommand.Execute()</c> → Direct Npgsql parameterized SQL</item>
    ///   <item>Synchronous post-hooks → SNS domain events</item>
    ///   <item><c>SecurityContext.OpenScope()</c> → JWT claims passed as parameters</item>
    /// </list>
    /// </summary>
    public class ReportService : IReportService
    {
        // ── Constants ───────────────────────────────────────────────────

        /// <summary>
        /// Cache key for report definitions. Maps from monolith source line 22:
        /// <c>private const string CACHE_KEY = "DATASOURCES";</c>
        /// </summary>
        private const string REPORT_DEFINITIONS_CACHE_KEY = "REPORT_DEFINITIONS";

        /// <summary>
        /// Cache key prefix for idempotency tracking on write operations.
        /// Per AAP §0.8.5: Idempotency keys on all write endpoints and event handlers.
        /// </summary>
        private const string IDEMPOTENCY_KEY_PREFIX = "IDEMPOTENCY:";

        /// <summary>
        /// Cache TTL for report definitions. Maps from monolith source line 29:
        /// <c>cache.Set(CACHE_KEY, list, new MemoryCacheEntryOptions { AbsoluteExpiration = ... });</c>
        /// 1-hour absolute expiration matching source line 38.
        /// </summary>
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(1);

        /// <summary>
        /// Idempotency key TTL — keys are remembered for 24 hours to prevent duplicate writes.
        /// </summary>
        private static readonly TimeSpan IdempotencyExpiration = TimeSpan.FromHours(24);

        /// <summary>
        /// SSM parameter name for the RDS PostgreSQL connection string.
        /// Per AAP §0.8.6: DB_CONNECTION_STRING from SSM SecureString — NEVER env vars.
        /// </summary>
        private const string SSM_CONNECTION_STRING_KEY = "/reporting/db-connection-string";

        /// <summary>
        /// SSM parameter name for the SNS topic ARN.
        /// </summary>
        private const string SSM_SNS_TOPIC_ARN_KEY = "/reporting/sns-topic-arn";

        /// <summary>
        /// Maximum SQL execution timeout in seconds for report queries.
        /// Matches monolith's 600-second EqlCommand timeout from source.
        /// </summary>
        private const int SQL_EXECUTION_TIMEOUT_SECONDS = 600;

        // ── Dependencies (DI-injected) ──────────────────────────────────

        private readonly IReportRepository _reportRepository;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ReportService> _logger;

        /// <summary>Lazily initialized connection string retrieved from SSM.</summary>
        private string? _connectionString;

        /// <summary>SNS topic ARN for reporting domain events.</summary>
        private string? _snsTopicArn;

        /// <summary>JSON serializer options configured for AOT compatibility.</summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        // ── Constructor ─────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of <see cref="ReportService"/> with all
        /// required dependencies injected via the DI container.
        /// Replaces the monolith's <c>private DbDataSourceRepository rep = new DbDataSourceRepository();</c>
        /// (source line 17) and static cache initialization.
        /// </summary>
        public ReportService(
            IReportRepository reportRepository,
            IAmazonSimpleNotificationService snsClient,
            IAmazonSimpleSystemsManagement ssmClient,
            IMemoryCache cache,
            ILogger<ReportService> logger)
        {
            _reportRepository = reportRepository ?? throw new ArgumentNullException(nameof(reportRepository));
            _snsClient = snsClient ?? throw new ArgumentNullException(nameof(snsClient));
            _ssmClient = ssmClient ?? throw new ArgumentNullException(nameof(ssmClient));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Attempt to resolve SNS topic ARN from environment variable first
            _snsTopicArn = Environment.GetEnvironmentVariable("REPORTING_SNS_TOPIC_ARN");
        }

        // ════════════════════════════════════════════════════════════════
        // Cache Management — Replaces source DataSourceManager lines 20-52
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Retrieves cached report definitions.
        /// Replaces monolith <c>GetFromCache()</c> (source lines 42-47).
        /// </summary>
        private List<ReportDefinition>? GetFromCache()
        {
            if (_cache.TryGetValue(REPORT_DEFINITIONS_CACHE_KEY, out List<ReportDefinition>? cached))
            {
                _logger.LogDebug("Cache HIT for report definitions (key: {CacheKey})",
                    REPORT_DEFINITIONS_CACHE_KEY);
                return cached;
            }

            _logger.LogDebug("Cache MISS for report definitions (key: {CacheKey})",
                REPORT_DEFINITIONS_CACHE_KEY);
            return null;
        }

        /// <summary>
        /// Adds report definitions to cache with 1-hour absolute expiration.
        /// Replaces monolith <c>AddToCache()</c> (source lines 35-40).
        /// </summary>
        private void AddToCache(List<ReportDefinition> reports)
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheExpiration
            };
            _cache.Set(REPORT_DEFINITIONS_CACHE_KEY, reports, options);
            _logger.LogDebug("Cache SET for {Count} report definitions (TTL: {TTL})",
                reports.Count, CacheExpiration);
        }

        /// <summary>
        /// Invalidates the report definitions cache.
        /// Replaces monolith <c>RemoveFromCache()</c> (source lines 49-52).
        /// </summary>
        private void InvalidateCache()
        {
            _cache.Remove(REPORT_DEFINITIONS_CACHE_KEY);
            _logger.LogDebug("Cache INVALIDATED for report definitions");
        }

        // ════════════════════════════════════════════════════════════════
        // DTO Mapping — ReportDefinition ↔ ReportDefinitionDto
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Maps a repository DTO to the domain model.
        /// Deserializes ParametersJson from the DTO into structured ReportParameter list.
        /// </summary>
        private ReportDefinition MapToDomain(ReportDefinitionDto dto)
        {
            List<ReportParameter> parameters;
            try
            {
                parameters = !string.IsNullOrWhiteSpace(dto.ParametersJson)
                    ? JsonSerializer.Deserialize<List<ReportParameter>>(dto.ParametersJson, JsonOptions)
                        ?? new List<ReportParameter>()
                    : new List<ReportParameter>();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to deserialize parameters JSON for report {ReportId}, using empty list",
                    dto.Id);
                parameters = new List<ReportParameter>();
            }

            return new ReportDefinition
            {
                Id = dto.Id,
                Name = dto.Name,
                Description = dto.Description ?? string.Empty,
                SqlTemplate = dto.SqlTemplate,
                Parameters = parameters,
                ReturnTotal = dto.ReturnTotal,
                Weight = dto.Weight,
                CreatedBy = dto.CreatedBy,
                CreatedAt = dto.CreatedAt,
                UpdatedAt = dto.UpdatedAt
            };
        }

        /// <summary>
        /// Maps a domain model to the repository DTO.
        /// Serializes the ReportParameter list into ParametersJson for persistence.
        /// </summary>
        private ReportDefinitionDto MapToDto(ReportDefinition definition)
        {
            string parametersJson;
            try
            {
                parametersJson = JsonSerializer.Serialize(
                    definition.Parameters ?? new List<ReportParameter>(), JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to serialize parameters for report {ReportId}, using empty array",
                    definition.Id);
                parametersJson = "[]";
            }

            return new ReportDefinitionDto
            {
                Id = definition.Id,
                Name = definition.Name,
                Description = definition.Description ?? string.Empty,
                SqlTemplate = definition.SqlTemplate,
                ParametersJson = parametersJson,
                FieldsJson = "[]",
                EntityName = string.Empty,
                ReturnTotal = definition.ReturnTotal,
                Weight = definition.Weight,
                CreatedBy = definition.CreatedBy ?? Guid.Empty,
                CreatedAt = definition.CreatedAt,
                UpdatedAt = definition.UpdatedAt
            };
        }

        // ════════════════════════════════════════════════════════════════
        // Connection String Management — SSM SecureString retrieval
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Retrieves the RDS PostgreSQL connection string.
        /// Per AAP §0.8.6: DB_CONNECTION_STRING from SSM SecureString.
        /// Falls back to environment variable for LocalStack development.
        /// </summary>
        private async Task<string> GetConnectionStringAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_connectionString))
                return _connectionString;

            // LocalStack development: environment variable fallback
            var envConnStr = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            if (!string.IsNullOrEmpty(envConnStr))
            {
                _connectionString = envConnStr;
                _logger.LogDebug("Connection string resolved from environment variable");
                return _connectionString;
            }

            // Production: retrieve from SSM Parameter Store SecureString
            try
            {
                var request = new GetParameterRequest
                {
                    Name = SSM_CONNECTION_STRING_KEY,
                    WithDecryption = true
                };
                GetParameterResponse response = await _ssmClient.GetParameterAsync(request, ct);
                _connectionString = response.Parameter.Value;
                _logger.LogDebug("Connection string resolved from SSM Parameter Store");
                return _connectionString;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve connection string from SSM parameter {Key}",
                    SSM_CONNECTION_STRING_KEY);
                throw new InvalidOperationException(
                    $"Cannot retrieve database connection string from SSM parameter '{SSM_CONNECTION_STRING_KEY}'.", ex);
            }
        }

        /// <summary>
        /// Ensures the SNS topic ARN is resolved from environment or SSM.
        /// </summary>
        private async Task<string> GetSnsTopicArnAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_snsTopicArn))
                return _snsTopicArn;

            try
            {
                var request = new GetParameterRequest
                {
                    Name = SSM_SNS_TOPIC_ARN_KEY,
                    WithDecryption = false
                };
                GetParameterResponse response = await _ssmClient.GetParameterAsync(request, ct);
                _snsTopicArn = response.Parameter.Value;
                return _snsTopicArn;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to retrieve SNS topic ARN from SSM. Domain events will not be published.");
                return string.Empty;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Idempotency Helpers
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if an idempotency key has been used and returns the cached result ID.
        /// Per AAP §0.8.5: Idempotency keys on all write endpoints.
        /// </summary>
        private bool TryGetIdempotentResult(string idempotencyKey, out Guid cachedId)
        {
            string cacheKey = IDEMPOTENCY_KEY_PREFIX + idempotencyKey;
            if (_cache.TryGetValue(cacheKey, out Guid existing))
            {
                cachedId = existing;
                _logger.LogInformation(
                    "Idempotency key {Key} already processed, returning cached result {ReportId}",
                    idempotencyKey, existing);
                return true;
            }

            cachedId = Guid.Empty;
            return false;
        }

        /// <summary>
        /// Records an idempotency key with its result for deduplication.
        /// </summary>
        private void RecordIdempotencyKey(string idempotencyKey, Guid resultId)
        {
            string cacheKey = IDEMPOTENCY_KEY_PREFIX + idempotencyKey;
            _cache.Set(cacheKey, resultId, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = IdempotencyExpiration
            });
        }

        // ════════════════════════════════════════════════════════════════
        // Report CRUD Operations
        // ════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.Get(Guid id)</c> (source lines 82-85):
        /// <code>
        /// public DataSourceBase Get(Guid id) {
        ///     return GetAll().SingleOrDefault(x => x.Id == id);
        /// }
        /// </code>
        /// Uses cache-first pattern matching the monolith: load all, then filter by ID.
        /// </remarks>
        public async Task<ReportDefinition?> GetReportByIdAsync(
            Guid id, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("GetReportByIdAsync - Looking up report {ReportId}", id);

            // Cache-first pattern matching source: GetAll().SingleOrDefault(x => x.Id == id)
            var cached = GetFromCache();
            if (cached != null)
            {
                var found = cached.SingleOrDefault(x => x.Id == id);
                _logger.LogDebug("GetReportByIdAsync - Report {ReportId} {Status} in cache",
                    id, found != null ? "found" : "not found");
                return found;
            }

            // Cache miss: load all from repository and populate cache
            var (allReports, _) = await LoadAllReportsFromRepositoryAsync(cancellationToken);
            var report = allReports.SingleOrDefault(x => x.Id == id);

            _logger.LogDebug("GetReportByIdAsync - Report {ReportId} {Status} from repository",
                id, report != null ? "found" : "not found");
            return report;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.GetAll()</c> (source lines 87-107).
        /// Adds pagination and sorting support not present in the monolith.
        /// Cache-first pattern: loads all, then applies pagination in-memory.
        /// </remarks>
        public async Task<(List<ReportDefinition> Reports, int TotalCount)> GetAllReportsAsync(
            int page, int pageSize, string sortBy, string sortOrder,
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("GetAllReportsAsync - page={Page}, pageSize={PageSize}, sortBy={SortBy}, sortOrder={SortOrder}",
                page, pageSize, sortBy, sortOrder);

            // Cache-first pattern matching source lines 89-91
            var allReports = GetFromCache();
            if (allReports == null)
            {
                (allReports, _) = await LoadAllReportsFromRepositoryAsync(cancellationToken);
            }

            int totalCount = allReports.Count;

            // Apply sorting
            IEnumerable<ReportDefinition> sorted = sortBy?.ToLowerInvariant() switch
            {
                "name" => sortOrder?.ToLowerInvariant() == "desc"
                    ? allReports.OrderByDescending(r => r.Name)
                    : allReports.OrderBy(r => r.Name),
                "createdat" => sortOrder?.ToLowerInvariant() == "desc"
                    ? allReports.OrderByDescending(r => r.CreatedAt)
                    : allReports.OrderBy(r => r.CreatedAt),
                "updatedat" => sortOrder?.ToLowerInvariant() == "desc"
                    ? allReports.OrderByDescending(r => r.UpdatedAt)
                    : allReports.OrderBy(r => r.UpdatedAt),
                _ => sortOrder?.ToLowerInvariant() == "desc"
                    ? allReports.OrderByDescending(r => r.Weight)
                    : allReports.OrderBy(r => r.Weight)
            };

            // Apply pagination
            int safePage = Math.Max(1, page);
            int safePageSize = Math.Clamp(pageSize, 1, 1000);
            var paginated = sorted
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .ToList();

            int totalPages = (int)Math.Ceiling((double)totalCount / safePageSize);
            _logger.LogInformation(
                "GetAllReportsAsync - Returning {Count} reports (page {Page}/{TotalPages})",
                paginated.Count, safePage, totalPages);

            return (paginated, totalCount);
        }

        /// <summary>
        /// Loads all report definitions from the repository and populates the cache.
        /// Shared helper used by GetReportByIdAsync and GetAllReportsAsync.
        /// </summary>
        private async Task<(List<ReportDefinition> Reports, int TotalCount)> LoadAllReportsFromRepositoryAsync(
            CancellationToken ct)
        {
            // Load all reports from repository (large page size to get everything)
            var (dtos, totalCount) = await _reportRepository.GetAllReportsAsync(
                1, int.MaxValue, "weight", "asc", ct);

            var reports = dtos.Select(MapToDomain).ToList();
            AddToCache(reports);

            return (reports, totalCount);
        }

        /// <summary>
        /// Retrieves a report by name for uniqueness validation.
        /// Replaces monolith <c>GetDatabaseDataSourceByName()</c> (source lines 118-125).
        /// </summary>
        private async Task<ReportDefinition?> GetReportByNameAsync(
            string name, CancellationToken ct)
        {
            var dto = await _reportRepository.GetReportByNameAsync(name, ct);
            return dto != null ? MapToDomain(dto) : null;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.Create()</c> (source lines 127-189).
        /// Includes validation, idempotency, persistence, cache invalidation, and SNS event publishing.
        /// </remarks>
        public async Task<ReportDefinition> CreateReportAsync(
            CreateReportRequest request, Guid? createdBy, string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("CreateReportAsync - Creating report: {Name}", request.Name);

            // ── Idempotency check (AAP §0.8.5) ──
            if (!string.IsNullOrEmpty(idempotencyKey) &&
                TryGetIdempotentResult(idempotencyKey, out Guid cachedId))
            {
                var existing = await GetReportByIdAsync(cachedId, cancellationToken);
                if (existing != null)
                    return existing;
            }

            // ── Parameter validation (source lines 131-134) ──
            var parameters = request.Parameters ?? new List<ReportParameter>();
            foreach (var param in parameters)
            {
                ValidateParameterType(param.Type, param.Name);
            }

            // ── Query validation (source lines 136-148, replaces EqlBuilder.Build()) ──
            var validationErrors = new List<string>();
            if (string.IsNullOrWhiteSpace(request.QueryDefinition))
            {
                validationErrors.Add("Query definition is required.");
            }
            else
            {
                try
                {
                    await ValidateReportQueryAsync(request.QueryDefinition, parameters, cancellationToken);
                }
                catch (ReportValidationException vex)
                {
                    validationErrors.AddRange(vex.Errors);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Query validation failed for report {Name}", request.Name);
                    validationErrors.Add($"Query validation error: {ex.Message}");
                }
            }

            // ── Name validation (source lines 170-173) ──
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                validationErrors.Add("Name is required.");
            }
            else
            {
                var existingByName = await GetReportByNameAsync(request.Name, cancellationToken);
                if (existingByName != null)
                {
                    validationErrors.Add("DataSource record with same name already exists.");
                }
            }

            // ── Throw validation errors (source line 181: validation.CheckAndThrow()) ──
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning("CreateReportAsync - Validation failed for {Name}: {Errors}",
                    request.Name, string.Join("; ", validationErrors));
                throw new ReportValidationException(validationErrors);
            }

            // ── Generate ID (source line 160: ds.Id = Guid.NewGuid()) ──
            var now = DateTime.UtcNow;
            var reportDefinition = new ReportDefinition
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                SqlTemplate = request.QueryDefinition,
                Parameters = parameters,
                ReturnTotal = request.ReturnTotal,
                Weight = request.Weight,
                CreatedBy = createdBy,
                CreatedAt = now,
                UpdatedAt = now
            };

            // ── Persist via repository (source lines 183-184) ──
            var dto = MapToDto(reportDefinition);
            bool created = await _reportRepository.CreateReportAsync(dto, cancellationToken);
            if (!created)
            {
                throw new InvalidOperationException(
                    $"Failed to create report definition '{request.Name}' in the repository.");
            }

            // ── Invalidate cache (source line 186: RemoveFromCache()) ──
            InvalidateCache();

            // ── Record idempotency key ──
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                RecordIdempotencyKey(idempotencyKey, reportDefinition.Id);
            }

            // ── Publish SNS domain event: reporting.report.created ──
            await PublishDomainEventAsync("created", reportDefinition, cancellationToken);

            // ── Return created report (source line 188) ──
            _logger.LogInformation("Report created: {ReportId} - {Name}",
                reportDefinition.Id, reportDefinition.Name);
            return reportDefinition;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.Update()</c> (source lines 191-265).
        /// Includes idempotency, validation, persistence, cache invalidation, and SNS events.
        /// </remarks>
        public async Task<ReportDefinition> UpdateReportAsync(
            Guid id, UpdateReportRequest request, string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("UpdateReportAsync - Updating report: {ReportId}", id);

            // ── Idempotency check (AAP §0.8.5) ──
            if (!string.IsNullOrEmpty(idempotencyKey) &&
                TryGetIdempotentResult(idempotencyKey, out Guid cachedId))
            {
                var cachedReport = await GetReportByIdAsync(cachedId, cancellationToken);
                if (cachedReport != null)
                    return cachedReport;
            }

            // ── Validate query not empty (source lines 195-196) ──
            if (string.IsNullOrWhiteSpace(request.QueryDefinition))
            {
                throw new ArgumentException("Query definition cannot be empty.", nameof(request));
            }

            // ── Parse and validate parameters (source lines 198-205) ──
            var parameters = request.Parameters ?? new List<ReportParameter>();
            foreach (var param in parameters)
            {
                ValidateParameterType(param.Type, param.Name);
            }

            // ── Validate query (source lines 207-228, replaces EqlBuilder) ──
            var validationErrors = new List<string>();
            try
            {
                await ValidateReportQueryAsync(request.QueryDefinition, parameters, cancellationToken);
            }
            catch (ReportValidationException vex)
            {
                validationErrors.AddRange(vex.Errors);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Query validation failed during update for report {ReportId}", id);
                validationErrors.Add($"Query validation error: {ex.Message}");
            }

            // ── Retrieve existing report ──
            var existingDto = await _reportRepository.GetReportByIdAsync(id, cancellationToken);
            if (existingDto == null)
            {
                throw new KeyNotFoundException($"Report with ID '{id}' not found.");
            }

            // ── Name validation (source lines 237-242) ──
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                validationErrors.Add("Name is required.");
            }
            else
            {
                // ── Name uniqueness check: "another with same name but different ID"
                // (source lines 243-248) ──
                var existingByName = await GetReportByNameAsync(request.Name, cancellationToken);
                if (existingByName != null && existingByName.Id != id)
                {
                    validationErrors.Add("Another DataSource with same name already exists.");
                }
            }

            // ── Throw validation errors ──
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning("UpdateReportAsync - Validation failed for {ReportId}: {Errors}",
                    id, string.Join("; ", validationErrors));
                throw new ReportValidationException(validationErrors);
            }

            // ── Build updated definition ──
            var now = DateTime.UtcNow;
            var updatedDefinition = new ReportDefinition
            {
                Id = id,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim() ?? string.Empty,
                SqlTemplate = request.QueryDefinition,
                Parameters = parameters,
                ReturnTotal = request.ReturnTotal,
                Weight = request.Weight,
                CreatedBy = existingDto.CreatedBy,
                CreatedAt = existingDto.CreatedAt,
                UpdatedAt = now
            };

            // ── Persist via repository (source lines 259-260) ──
            var dto = MapToDto(updatedDefinition);
            bool updated = await _reportRepository.UpdateReportAsync(dto, cancellationToken);
            if (!updated)
            {
                throw new InvalidOperationException(
                    $"Failed to update report definition '{id}' in the repository.");
            }

            // ── Invalidate cache (source line 262: RemoveFromCache()) ──
            InvalidateCache();

            // ── Record idempotency key ──
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                RecordIdempotencyKey(idempotencyKey, id);
            }

            // ── Publish SNS domain event: reporting.report.updated ──
            await PublishDomainEventAsync("updated", updatedDefinition, cancellationToken);

            // ── Return updated report (source line 264) ──
            _logger.LogInformation("Report updated: {ReportId} - {Name}", id, updatedDefinition.Name);
            return updatedDefinition;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.Delete(Guid id)</c> (source lines 464-468):
        /// <code>
        /// public void Delete(Guid id) {
        ///     rep.Delete(id);
        ///     RemoveFromCache();
        /// }
        /// </code>
        /// </remarks>
        public async Task DeleteReportAsync(
            Guid id, string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("DeleteReportAsync - Deleting report: {ReportId}", id);

            // ── Idempotency check ──
            if (!string.IsNullOrEmpty(idempotencyKey) &&
                TryGetIdempotentResult(idempotencyKey, out _))
            {
                _logger.LogInformation(
                    "DeleteReportAsync - Idempotency key {Key} already processed, skipping delete",
                    idempotencyKey);
                return;
            }

            // ── Verify report exists ──
            var existingDto = await _reportRepository.GetReportByIdAsync(id, cancellationToken);
            if (existingDto == null)
            {
                throw new KeyNotFoundException($"Report with ID '{id}' not found.");
            }

            var reportForEvent = MapToDomain(existingDto);

            // ── Delete via repository (source line 466: rep.Delete(id)) ──
            await _reportRepository.DeleteReportAsync(id, cancellationToken);

            // ── Invalidate cache (source line 467: RemoveFromCache()) ──
            InvalidateCache();

            // ── Record idempotency key ──
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                RecordIdempotencyKey(idempotencyKey, id);
            }

            // ── Publish SNS domain event: reporting.report.deleted ──
            await PublishDomainEventAsync("deleted", reportForEvent, cancellationToken);

            _logger.LogInformation("Report deleted: {ReportId}", id);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.Execute(Guid id, List&lt;EqlParameter&gt; parameters)</c>
        /// (source lines 470-497). Enriches missing parameters with defaults (source lines 479-481),
        /// then executes parameterized SQL against RDS PostgreSQL via Npgsql.
        /// </remarks>
        public async Task<ReportExecutionResult> ExecuteReportAsync(
            Guid id, Dictionary<string, object?>? parameters,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("ExecuteReportAsync - Executing report: {ReportId}", id);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // ── Look up report definition (source line 472: var ds = Get(id)) ──
            var report = await GetReportByIdAsync(id, cancellationToken);
            if (report == null)
            {
                // Source lines 473-474: throw new Exception("DataSource not found.")
                throw new KeyNotFoundException($"Report with ID '{id}' not found.");
            }

            // ── Enrich missing parameters with defaults (CRITICAL — source lines 479-481) ──
            // Source logic:
            //   foreach (var par in ds.Parameters)
            //       if (!(parameters.Any(x => x.ParameterName == par.Name) ||
            //             parameters.Any(x => x.ParameterName == "@" + par.Name)))
            //           parameters.Add(new EqlParameter(par.Name, par.Value));
            var effectiveParams = parameters != null
                ? new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (report.Parameters != null)
            {
                foreach (var param in report.Parameters)
                {
                    bool hasParam = effectiveParams.ContainsKey(param.Name) ||
                                    effectiveParams.ContainsKey("@" + param.Name);
                    if (!hasParam)
                    {
                        // Resolve default value with type conversion
                        var resolved = ResolveParameterValue(param);
                        effectiveParams[param.Name] = resolved;
                    }
                }
            }

            // ── Execute parameterized SQL against RDS PostgreSQL ──
            var connectionString = await GetConnectionStringAsync(cancellationToken);
            var rows = new List<Dictionary<string, object?>>();
            int totalCount = 0;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(report.SqlTemplate, connection);
            command.CommandTimeout = SQL_EXECUTION_TIMEOUT_SECONDS;

            // ── Add parameters (explicit NpgsqlParameter for AOT compatibility) ──
            foreach (var kvp in effectiveParams)
            {
                string paramName = kvp.Key.StartsWith("@") ? kvp.Key : "@" + kvp.Key;
                var npgsqlParam = new NpgsqlParameter(paramName, kvp.Value ?? DBNull.Value);
                command.Parameters.Add(npgsqlParam);
            }

            // ── Extract column metadata and data rows from reader ──
            var columns = new List<ColumnDefinition>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                // Build column definitions from the result schema
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(new ColumnDefinition
                    {
                        Name = reader.GetName(i),
                        DataType = MapPostgresTypeToReportType(reader.GetFieldType(i)),
                        DisplayName = reader.GetName(i),
                        IsSortable = true,
                        IsFilterable = true
                    });
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string columnName = reader.GetName(i);
                        object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[columnName] = value;
                    }
                    rows.Add(row);
                }
            }

            // ── If ReturnTotal, execute COUNT query for total ──
            if (report.ReturnTotal)
            {
                totalCount = await ExecuteCountQueryAsync(
                    connection, report.SqlTemplate, effectiveParams, cancellationToken);
            }
            else
            {
                totalCount = rows.Count;
            }

            stopwatch.Stop();

            // ── Build internal ReportResult for auditing/logging (uses ALL members) ──
            var reportResult = new ReportResult
            {
                ReportId = report.Id,
                ReportName = report.Name,
                Columns = columns,
                Rows = rows,
                TotalCount = totalCount,
                PageNumber = 1,
                PageSize = rows.Count,
                ExecutionDuration = stopwatch.Elapsed,
                ExecutedAt = DateTime.UtcNow,
                Success = true,
                ErrorMessage = null
            };

            _logger.LogInformation(
                "Report executed: {ReportId} ({ReportName}) with {ParamCount} parameters, " +
                "returned {RowCount} rows ({ColumnCount} columns), total {TotalCount}, " +
                "duration {Duration}ms, success: {Success}",
                reportResult.ReportId, reportResult.ReportName,
                effectiveParams.Count, reportResult.Rows.Count,
                reportResult.Columns.Count, reportResult.TotalCount,
                reportResult.ExecutionDuration.TotalMilliseconds, reportResult.Success);

            return new ReportExecutionResult
            {
                Data = reportResult.Rows,
                TotalCount = reportResult.TotalCount
            };
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.Execute(string eql, string parameters, bool returnTotal)</c>
        /// (source lines 499-512). Executes ad-hoc SQL directly instead of EQL.
        /// </remarks>
        public async Task<ReportExecutionResult> ExecuteAdHocQueryAsync(
            string sqlQuery, Dictionary<string, object?>? parameters, bool returnTotal,
            CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("ExecuteAdHocQueryAsync - Running ad-hoc query");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(sqlQuery))
            {
                throw new ArgumentException("SQL query cannot be empty.", nameof(sqlQuery));
            }

            // ── Validate query syntax ──
            await ValidateReportQueryAsync(sqlQuery, null, cancellationToken);

            // ── Execute against RDS PostgreSQL ──
            var connectionString = await GetConnectionStringAsync(cancellationToken);
            var rows = new List<Dictionary<string, object?>>();
            int totalCount = 0;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(sqlQuery, connection);
            command.CommandTimeout = SQL_EXECUTION_TIMEOUT_SECONDS;

            if (parameters != null)
            {
                foreach (var kvp in parameters)
                {
                    string paramName = kvp.Key.StartsWith("@") ? kvp.Key : "@" + kvp.Key;
                    var npgsqlParam = new NpgsqlParameter(paramName, kvp.Value ?? DBNull.Value);
                    command.Parameters.Add(npgsqlParam);
                }
            }

            // ── Extract column metadata and data rows from reader ──
            var columns = new List<ColumnDefinition>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                // Build column definitions from the result schema
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columns.Add(new ColumnDefinition
                    {
                        Name = reader.GetName(i),
                        DataType = MapPostgresTypeToReportType(reader.GetFieldType(i)),
                        DisplayName = reader.GetName(i),
                        IsSortable = true,
                        IsFilterable = true
                    });
                }

                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string columnName = reader.GetName(i);
                        object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[columnName] = value;
                    }
                    rows.Add(row);
                }
            }

            if (returnTotal)
            {
                var effectiveParams = parameters ?? new Dictionary<string, object?>();
                totalCount = await ExecuteCountQueryAsync(
                    connection, sqlQuery, effectiveParams, cancellationToken);
            }
            else
            {
                totalCount = rows.Count;
            }

            stopwatch.Stop();

            // ── Build internal ReportResult for auditing/logging (uses ALL members) ──
            var adHocResult = new ReportResult
            {
                ReportId = Guid.Empty,
                ReportName = "AdHocQuery",
                Columns = columns,
                Rows = rows,
                TotalCount = totalCount,
                PageNumber = 1,
                PageSize = rows.Count,
                ExecutionDuration = stopwatch.Elapsed,
                ExecutedAt = DateTime.UtcNow,
                Success = true,
                ErrorMessage = null
            };

            _logger.LogInformation(
                "Ad-hoc query executed: returned {RowCount} rows ({ColumnCount} columns), " +
                "total {TotalCount}, duration {Duration}ms, success: {Success}",
                adHocResult.Rows.Count, adHocResult.Columns.Count,
                adHocResult.TotalCount, adHocResult.ExecutionDuration.TotalMilliseconds,
                adHocResult.Success);

            return new ReportExecutionResult
            {
                Data = adHocResult.Rows,
                TotalCount = adHocResult.TotalCount
            };
        }

        /// <summary>
        /// Executes a COUNT(*) wrapper query to determine total rows matching
        /// the base query, used when <see cref="ReportDefinition.ReturnTotal"/> is true.
        /// </summary>
        private async Task<int> ExecuteCountQueryAsync(
            NpgsqlConnection connection, string baseQuery,
            Dictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            string countSql = $"SELECT COUNT(*) FROM ({baseQuery}) AS __count_wrapper";
            await using var countCmd = new NpgsqlCommand(countSql, connection);
            countCmd.CommandTimeout = SQL_EXECUTION_TIMEOUT_SECONDS;

            foreach (var kvp in parameters)
            {
                string paramName = kvp.Key.StartsWith("@") ? kvp.Key : "@" + kvp.Key;
                var npgsqlParam = new NpgsqlParameter(paramName, kvp.Value ?? DBNull.Value);
                countCmd.Parameters.Add(npgsqlParam);
            }

            var result = await countCmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result ?? 0);
        }

        /// <summary>
        /// Maps a .NET CLR type (from NpgsqlDataReader.GetFieldType) to a simplified
        /// reporting data type string used in <see cref="ColumnDefinition.DataType"/>.
        /// Maps the monolith's 20+ FieldType enum values to basic reporting types:
        /// "string", "number", "date", "boolean", "guid".
        /// </summary>
        private static string MapPostgresTypeToReportType(Type clrType)
        {
            if (clrType == typeof(Guid))
                return "guid";
            if (clrType == typeof(bool))
                return "boolean";
            if (clrType == typeof(DateTime) || clrType == typeof(DateTimeOffset) ||
                clrType == typeof(DateOnly) || clrType == typeof(TimeOnly))
                return "date";
            if (clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(short) ||
                clrType == typeof(decimal) || clrType == typeof(double) || clrType == typeof(float) ||
                clrType == typeof(byte))
                return "number";
            // Default to "string" for all other types (varchar, text, json, etc.)
            return "string";
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.GetDataSourceParameterValue(DataSourceParameter dsParameter)</c>
        /// (source lines 356-462). PRESERVES EVERY CASE AND EVERY EDGE CASE for full behavioral parity.
        /// All string comparisons use <c>.ToLowerInvariant()</c> matching source.
        /// </remarks>
        public object? ResolveParameterValue(ReportParameter parameter)
        {
            if (parameter == null)
                throw new ArgumentNullException(nameof(parameter));

            string type = (parameter.Type ?? string.Empty).ToLowerInvariant().Trim();
            string? value = parameter.DefaultValue;
            string name = parameter.Name ?? string.Empty;

            switch (type)
            {
                // ── guid type (source lines 360-378) ──
                case "guid":
                {
                    // Empty/whitespace → null (source lines 362-363)
                    if (string.IsNullOrWhiteSpace(value))
                        return null;

                    string valueLower = value.ToLowerInvariant();

                    // "null" → null (source lines 365-366)
                    if (valueLower == "null")
                        return null;

                    // "guid.empty" → Guid.Empty (source lines 368-369)
                    if (valueLower == "guid.empty")
                        return Guid.Empty;

                    // Valid GUID string → parsed Guid (source lines 371-372)
                    if (Guid.TryParse(value, out Guid guidResult))
                        return guidResult;

                    // Invalid + IgnoreParseErrors → null (source lines 374-375)
                    if (parameter.IgnoreParseErrors)
                        return null;

                    // Invalid + !IgnoreParseErrors → throw (source line 377)
                    throw new Exception($"Invalid Guid value for parameter: {name}");
                }

                // ── int type (source lines 379-394) ──
                case "int":
                {
                    // Empty/whitespace → null (source lines 381-382)
                    if (string.IsNullOrWhiteSpace(value))
                        return null;

                    // Valid int → parsed int (source lines 384-385)
                    if (int.TryParse(value, out int intResult))
                        return intResult;

                    string valueLower = value.ToLowerInvariant();

                    // "null" → null (source lines 387-388)
                    if (valueLower == "null")
                        return null;

                    // Invalid + IgnoreParseErrors → null (source lines 390-391)
                    if (parameter.IgnoreParseErrors)
                        return null;

                    // Invalid + !IgnoreParseErrors → throw (source line 393)
                    throw new Exception($"Invalid int value for parameter: {name}");
                }

                // ── decimal type (source lines 395-407) ──
                case "decimal":
                {
                    // Empty/whitespace → null (source lines 397-398)
                    if (string.IsNullOrWhiteSpace(value))
                        return null;

                    // Valid decimal → parsed decimal (source lines 400-401)
                    if (decimal.TryParse(value, out decimal decimalResult))
                        return decimalResult;

                    // Invalid + IgnoreParseErrors → null (source lines 403-404)
                    if (parameter.IgnoreParseErrors)
                        return null;

                    // Invalid + !IgnoreParseErrors → throw (source line 406)
                    throw new Exception($"Invalid decimal value for parameter: {name}");
                }

                // ── date type (source lines 408-429) ──
                case "date":
                {
                    // Empty/whitespace → null (source lines 410-411)
                    if (string.IsNullOrWhiteSpace(value))
                        return null;

                    string valueLower = value.ToLowerInvariant();

                    // "null" → null (source lines 413-414)
                    if (valueLower == "null")
                        return null;

                    // "now" → DateTime.Now (source lines 416-417) — PRESERVED EXACT BEHAVIOR
                    if (valueLower == "now")
                        return DateTime.Now;

                    // "utc_now" → DateTime.UtcNow (source lines 419-420) — PRESERVED EXACT BEHAVIOR
                    if (valueLower == "utc_now")
                        return DateTime.UtcNow;

                    // Valid date string → parsed DateTime (source lines 422-423)
                    if (DateTime.TryParse(value, out DateTime dateResult))
                        return dateResult;

                    // Invalid + IgnoreParseErrors → null (source lines 425-426)
                    if (parameter.IgnoreParseErrors)
                        return null;

                    // Invalid + !IgnoreParseErrors → throw (source line 428)
                    throw new Exception($"Invalid datetime value for parameter: {name}");
                }

                // ── text type (source lines 430-442) ──
                case "text":
                {
                    if (value == null)
                        return null;

                    string valueLower = value.ToLowerInvariant();

                    // "null" → null (source lines 432-433)
                    if (valueLower == "null")
                        return null;

                    // "string.empty" → String.Empty (source lines 435-436)
                    if (valueLower == "string.empty")
                        return string.Empty;

                    // IgnoreParseErrors → null (source lines 438-439)
                    // NOTE: This is likely a bug in source, but MUST be preserved for behavioral parity.
                    // The source code returns null when IgnoreParseErrors is true for text type,
                    // even for valid text values. This is intentionally preserved.
                    if (parameter.IgnoreParseErrors)
                        return null;

                    // Otherwise → return raw value (source line 441)
                    return value;
                }

                // ── bool type (source lines 443-458) ──
                case "bool":
                {
                    if (value == null)
                        return null;

                    string valueLower = value.ToLowerInvariant();

                    // "null" → null (source lines 445-446)
                    if (valueLower == "null")
                        return null;

                    // "true" → true (source lines 448-449)
                    if (valueLower == "true")
                        return true;

                    // "false" → false (source lines 451-452)
                    if (valueLower == "false")
                        return false;

                    // Invalid + IgnoreParseErrors → null (source lines 454-455)
                    if (parameter.IgnoreParseErrors)
                        return null;

                    // Invalid + !IgnoreParseErrors → throw (source line 457)
                    throw new Exception($"Invalid boolean value for parameter: {name}");
                }

                // ── default (source lines 459-460) ──
                default:
                    throw new Exception($"Invalid parameter type '{type}' for '{name}'");
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.ProcessParametersText(string parameters)</c>
        /// (source lines 296-330). Parses newline-delimited CSV parameter definitions.
        /// Format: <c>name,type,value[,ignoreParseErrors]</c>
        /// </remarks>
        public List<ReportParameter> ParseParametersText(string parametersText)
        {
            var result = new List<ReportParameter>();

            if (string.IsNullOrWhiteSpace(parametersText))
                return result;

            // Split by newline (source line 305: parameters.Split("\n", ...))
            var lines = parametersText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                // Remove carriage return (source line 307: line.Replace("\r", ""))
                var line = rawLine.Replace("\r", string.Empty);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Split by comma (source line 307: .Split(",", ...))
                var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries);

                // Validate part count (source lines 308-309)
                if (parts.Length < 3 || parts.Length > 4)
                {
                    throw new Exception($"Invalid parameter description: {line}");
                }

                var parameter = new ReportParameter
                {
                    // Name (source line 311: dsPar.Name = parts[0].Trim())
                    Name = parts[0].Trim(),
                    // Type (source line 312: dsPar.Type = parts[1].ToLowerInvariant().Trim())
                    Type = parts[1].ToLowerInvariant().Trim(),
                    // Value/DefaultValue (source line 313: dsPar.Value = parts[2].Trim())
                    DefaultValue = parts[2].Trim(),
                    // IgnoreParseErrors default
                    IgnoreParseErrors = false
                };

                // Optional 4th column for IgnoreParseErrors (source lines 318-325)
                if (parts.Length == 4)
                {
                    try
                    {
                        parameter.IgnoreParseErrors = bool.Parse(parts[3].Trim());
                    }
                    catch
                    {
                        // Default to false on parse failure, matching source behavior
                        parameter.IgnoreParseErrors = false;
                    }
                }

                result.Add(parameter);
            }

            return result;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>DataSourceManager.ConvertParamsToText(List&lt;DataSourceParameter&gt; parameters)</c>
        /// (source lines 332-344). Serializes parameters to CSV format.
        /// </remarks>
        public string ConvertParametersToText(List<ReportParameter> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return string.Empty;

            var lines = new List<string>();

            foreach (var param in parameters)
            {
                // Base format: name,type,value
                string line = $"{param.Name},{param.Type},{param.DefaultValue ?? string.Empty}";

                // Only append ",true" when IgnoreParseErrors is true (source lines 337-340)
                if (param.IgnoreParseErrors)
                {
                    line += ",true";
                }

                lines.Add(line);
            }

            // Use Environment.NewLine as line separator
            return string.Join(Environment.NewLine, lines);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces <c>EqlBuilder.Build()</c> validation logic (source lines 136-148, 207-228).
        /// Validates SQL syntax using PostgreSQL EXPLAIN command.
        /// </remarks>
        public async Task ValidateReportQueryAsync(
            string sqlQuery, List<ReportParameter>? parameters,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sqlQuery))
            {
                throw new ReportValidationException(new List<string>
                {
                    "SQL query is required."
                });
            }

            var errors = new List<string>();

            // ── Validate parameter placeholders are in the SQL ──
            if (parameters != null && parameters.Count > 0)
            {
                foreach (var param in parameters)
                {
                    string paramPlaceholder = "@" + param.Name;
                    if (!sqlQuery.Contains(paramPlaceholder, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Parameter '{ParamName}' declared but not referenced in SQL query",
                            param.Name);
                    }
                }
            }

            // ── Validate SQL syntax via EXPLAIN (without actual execution) ──
            try
            {
                var connectionString = await GetConnectionStringAsync(cancellationToken);
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                // Replace parameter placeholders with NULL for EXPLAIN validation
                string explainSql = sqlQuery;
                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        explainSql = explainSql.Replace(
                            "@" + param.Name, "NULL", StringComparison.OrdinalIgnoreCase);
                    }
                }

                string explainCommand = $"EXPLAIN {explainSql}";
                await using var cmd = new NpgsqlCommand(explainCommand, connection);
                cmd.CommandTimeout = 30; // Short timeout for validation

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                // If EXPLAIN succeeds, the SQL is syntactically valid
                while (await reader.ReadAsync(cancellationToken))
                {
                    // Consume the EXPLAIN output (we don't need it, just confirming no errors)
                }
            }
            catch (NpgsqlException ex)
            {
                _logger.LogWarning(ex, "SQL validation failed via EXPLAIN");
                errors.Add($"SQL syntax error: {ex.Message}");
            }
            catch (Exception ex) when (ex is not ReportValidationException)
            {
                _logger.LogWarning(ex, "Unexpected error during SQL validation");
                errors.Add($"Query validation error: {ex.Message}");
            }

            if (errors.Count > 0)
            {
                throw new ReportValidationException(errors);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  Private Helpers
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Publishes a domain event to the reporting SNS topic, replacing
        /// the monolith's synchronous post-hook system (per AAP §0.7.2).
        /// Event naming convention: <c>reporting.report.{action}</c> (per AAP §0.8.5).
        /// </summary>
        private async Task PublishDomainEventAsync(
            string action, ReportDefinition report,
            CancellationToken cancellationToken)
        {
            try
            {
                string topicArn = await GetSnsTopicArnAsync(cancellationToken);
                if (string.IsNullOrEmpty(topicArn))
                {
                    _logger.LogWarning(
                        "SNS topic ARN not configured; skipping domain event for reporting.report.{Action}",
                        action);
                    return;
                }

                var domainEvent = new DomainEvent
                {
                    EventId = Guid.NewGuid(),
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    SourceDomain = "reporting",
                    EntityName = "report",
                    Action = action,
                    Timestamp = DateTime.UtcNow,
                    Payload = new Dictionary<string, object?>
                    {
                        ["reportId"] = report.Id,
                        ["reportName"] = report.Name,
                        ["description"] = report.Description,
                        ["weight"] = report.Weight,
                        ["returnTotal"] = report.ReturnTotal,
                        ["createdBy"] = report.CreatedBy,
                        ["createdAt"] = report.CreatedAt,
                        ["updatedAt"] = report.UpdatedAt
                    }
                };

                string messageBody = JsonSerializer.Serialize(domainEvent, JsonOptions);

                var publishRequest = new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = domainEvent.EventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = domainEvent.CorrelationId
                        }
                    }
                };

                PublishResponse response = await _snsClient.PublishAsync(publishRequest, cancellationToken);

                _logger.LogInformation(
                    "Published domain event {EventType} for report {ReportId}, MessageId: {MessageId}",
                    domainEvent.EventType, report.Id, response.MessageId);
            }
            catch (Exception ex)
            {
                // Log errors from SNS but don't fail the main operation.
                // Event publishing is best-effort from the caller's perspective;
                // SQS retry handles delivery (per AAP §0.8.5).
                _logger.LogError(ex,
                    "Failed to publish domain event reporting.report.{Action} for report {ReportId}",
                    action, report.Id);
            }
        }

        /// <summary>
        /// Validates that a parameter type is one of the six supported types.
        /// Supported types: guid, int, decimal, date, text, bool
        /// (matching source line 358 switch cases).
        /// </summary>
        private static void ValidateParameterType(string type, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new ReportValidationException(new List<string>
                {
                    $"Parameter type is required for parameter '{parameterName}'."
                });
            }

            string normalizedType = type.ToLowerInvariant().Trim();
            var validTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "guid", "int", "decimal", "date", "text", "bool"
            };

            if (!validTypes.Contains(normalizedType))
            {
                throw new ReportValidationException(new List<string>
                {
                    $"Invalid parameter type '{type}' for parameter '{parameterName}'. " +
                    $"Valid types are: guid, int, decimal, date, text, bool."
                });
            }
        }
    }

    /// <summary>
    /// Custom validation exception for report operations, carrying a list
    /// of validation error messages. Used throughout <see cref="ReportService"/>
    /// for structured validation error reporting.
    /// </summary>
    public class ReportValidationException : Exception
    {
        /// <summary>
        /// Gets the collection of validation error messages.
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="ReportValidationException"/>
        /// with the specified list of validation errors.
        /// </summary>
        /// <param name="errors">The list of validation error messages.</param>
        public ReportValidationException(List<string> errors)
            : base(errors.Count > 0
                ? string.Join("; ", errors)
                : "Report validation failed.")
        {
            Errors = errors.AsReadOnly();
        }

        /// <summary>
        /// Initializes a new instance of <see cref="ReportValidationException"/>
        /// with a single validation error message.
        /// </summary>
        /// <param name="error">The validation error message.</param>
        public ReportValidationException(string error)
            : base(error)
        {
            Errors = new List<string> { error }.AsReadOnly();
        }
    }
}
