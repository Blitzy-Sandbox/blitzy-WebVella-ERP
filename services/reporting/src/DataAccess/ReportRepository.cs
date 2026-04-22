using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;

namespace WebVellaErp.Reporting.DataAccess
{
    #region DTO Definitions

    /// <summary>
    /// Data transfer object for report definitions stored in reporting.report_definitions.
    /// Maps from the monolith's data_source table (DbDataSourceRepository.cs).
    /// Column mapping:
    ///   data_source.id             → ReportDefinitionDto.Id
    ///   data_source.name           → ReportDefinitionDto.Name
    ///   data_source.description    → ReportDefinitionDto.Description
    ///   data_source.eql_text + sql_text → ReportDefinitionDto.SqlTemplate (consolidated)
    ///   data_source.parameters_json → ReportDefinitionDto.ParametersJson (text → jsonb)
    ///   data_source.fields_json    → ReportDefinitionDto.FieldsJson (text → jsonb)
    ///   data_source.entity_name    → ReportDefinitionDto.EntityName
    ///   data_source.return_total   → ReportDefinitionDto.ReturnTotal
    ///   data_source.weight         → ReportDefinitionDto.Weight
    ///   (new audit)                → ReportDefinitionDto.CreatedBy, CreatedAt, UpdatedAt
    /// </summary>
    public record ReportDefinitionDto
    {
        /// <summary>Report definition unique identifier (maps from data_source.id).</summary>
        public Guid Id { get; init; }

        /// <summary>Human-readable report name with unique constraint (maps from data_source.name).</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Optional report description (maps from data_source.description).</summary>
        public string? Description { get; init; }

        /// <summary>SQL query template combining monolith's eql_text and sql_text into a single column.</summary>
        public string SqlTemplate { get; init; } = string.Empty;

        /// <summary>Report parameter definitions as JSON (JSONB in PostgreSQL, upgraded from text).</summary>
        public string? ParametersJson { get; init; }

        /// <summary>Report field definitions as JSON (JSONB in PostgreSQL, upgraded from text).</summary>
        public string? FieldsJson { get; init; }

        /// <summary>Optional source entity name for entity-scoped reports (maps from data_source.entity_name).</summary>
        public string? EntityName { get; init; }

        /// <summary>Whether to return total row count with results (maps from data_source.return_total).</summary>
        public bool ReturnTotal { get; init; } = true;

        /// <summary>Display ordering weight, lower values appear first (maps from data_source.weight).</summary>
        public int Weight { get; init; }

        /// <summary>Audit field: user who created the report definition.</summary>
        public Guid? CreatedBy { get; init; }

        /// <summary>Audit field: when the report definition was created.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Audit field: when the report definition was last modified.</summary>
        public DateTime UpdatedAt { get; init; }
    }

    /// <summary>
    /// Data transfer object for CQRS read-model projections stored in reporting.read_model_projections.
    /// These projections are materialized from domain events consumed from all bounded contexts
    /// (invoicing, CRM, inventory, entity-management, etc.) for efficient cross-domain reporting.
    /// </summary>
    public record ProjectionDto
    {
        /// <summary>Projection unique identifier (auto-generated UUID).</summary>
        public Guid Id { get; init; }

        /// <summary>Bounded context domain name (e.g., "invoicing", "crm", "inventory").</summary>
        public string SourceDomain { get; init; } = string.Empty;

        /// <summary>Entity name within the source domain (e.g., "invoice", "contact", "product").</summary>
        public string SourceEntity { get; init; } = string.Empty;

        /// <summary>Original record ID from the source domain's datastore.</summary>
        public Guid SourceRecordId { get; init; }

        /// <summary>Denormalized JSONB projection data for efficient reporting queries.</summary>
        public JsonElement ProjectionData { get; init; }

        /// <summary>When the projection was first materialized.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>When the projection was last updated from a domain event.</summary>
        public DateTime UpdatedAt { get; init; }
    }

    #endregion

    #region IReportRepository Interface

    /// <summary>
    /// Repository interface for the Reporting &amp; Analytics bounded-context service.
    /// Provides ACID-compliant data access for report definitions, CQRS read-model projections,
    /// and idempotent event offset tracking against RDS PostgreSQL.
    /// 
    /// Replaces the monolith's DbDataSourceRepository (synchronous, ambient DbContext.Current)
    /// with an async, DI-friendly, schema-isolated implementation.
    /// 
    /// All methods are async with CancellationToken support for Lambda runtime cancellation.
    /// Nullable return types for single-item lookups indicate "not found" semantics.
    /// </summary>
    public interface IReportRepository
    {
        // ── Report Definition CRUD ──────────────────────────────────────

        /// <summary>
        /// Retrieves a report definition by its unique identifier.
        /// Replaces DbDataSourceRepository.Get(Guid id).
        /// </summary>
        Task<ReportDefinitionDto?> GetReportByIdAsync(Guid id, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a report definition by its unique name.
        /// Replaces DbDataSourceRepository.Get(string name).
        /// </summary>
        Task<ReportDefinitionDto?> GetReportByNameAsync(string name, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a paginated, sorted list of all report definitions with total count.
        /// Replaces DbDataSourceRepository.GetAll() with added pagination support.
        /// </summary>
        Task<(List<ReportDefinitionDto> Items, int TotalCount)> GetAllReportsAsync(
            int page, int pageSize, string sortBy, string sortOrder, CancellationToken ct = default);

        /// <summary>
        /// Creates a new report definition with ACID transaction guarantee.
        /// Replaces DbDataSourceRepository.Create(...).
        /// </summary>
        Task<bool> CreateReportAsync(ReportDefinitionDto report, CancellationToken ct = default);

        /// <summary>
        /// Updates an existing report definition with ACID transaction guarantee.
        /// Replaces DbDataSourceRepository.Update(...).
        /// </summary>
        Task<bool> UpdateReportAsync(ReportDefinitionDto report, CancellationToken ct = default);

        /// <summary>
        /// Deletes a report definition by its unique identifier.
        /// Replaces DbDataSourceRepository.Delete(Guid id).
        /// </summary>
        Task DeleteReportAsync(Guid id, CancellationToken ct = default);

        // ── Read-Model Projection CRUD (CQRS) ──────────────────────────

        /// <summary>
        /// Inserts or updates a CQRS read-model projection for a source domain event.
        /// Uses PostgreSQL UPSERT (INSERT ... ON CONFLICT ... DO UPDATE) for idempotency.
        /// Optional connection/transaction parameters allow caller-managed transaction lifecycle.
        /// </summary>
        Task UpsertProjectionAsync(string sourceDomain, string sourceEntity, Guid sourceRecordId,
            JsonElement projectionData, NpgsqlConnection? connection = null,
            NpgsqlTransaction? transaction = null, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a single projection by its composite key (domain, entity, record ID).
        /// </summary>
        Task<ProjectionDto?> GetProjectionAsync(string sourceDomain, string sourceEntity,
            Guid sourceRecordId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a paginated list of projections filtered by source domain.
        /// Ordered by updated_at descending for most-recent-first display.
        /// </summary>
        Task<List<ProjectionDto>> GetProjectionsByDomainAsync(string sourceDomain,
            int page, int pageSize, CancellationToken ct = default);

        /// <summary>
        /// Hard-deletes a projection row by its composite key.
        /// Optional connection/transaction parameters allow caller-managed transaction lifecycle.
        /// </summary>
        Task DeleteProjectionAsync(string sourceDomain, string sourceEntity, Guid sourceRecordId,
            NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null,
            CancellationToken ct = default);

        /// <summary>
        /// Soft-deletes a projection by merging deletion metadata into projection_data JSONB.
        /// Sets "deleted": true and "deleted_at" timestamp without removing the row.
        /// Optional connection/transaction parameters allow caller-managed transaction lifecycle.
        /// </summary>
        Task SoftDeleteProjectionAsync(string sourceDomain, string sourceEntity, Guid sourceRecordId,
            NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null,
            CancellationToken ct = default);

        // ── Event Offset Tracking (Idempotent SQS Consumption) ─────────

        /// <summary>
        /// Tracks the last processed event ID per source domain for idempotent consumption.
        /// Uses PostgreSQL UPSERT (INSERT ... ON CONFLICT ... DO UPDATE).
        /// Optional connection/transaction parameters allow caller-managed transaction lifecycle.
        /// </summary>
        Task UpsertEventOffsetAsync(string sourceDomain, string lastEventId,
            NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null,
            CancellationToken ct = default);

        /// <summary>
        /// Retrieves the last processed event ID for a given source domain.
        /// Returns null if no events have been processed yet (first-time processing).
        /// </summary>
        Task<string?> GetLastEventIdAsync(string sourceDomain, CancellationToken ct = default);
    }

    #endregion

    #region ReportRepository Implementation

    /// <summary>
    /// RDS PostgreSQL data access repository for the Reporting &amp; Analytics bounded-context service.
    /// 
    /// Replaces the monolith's DbDataSourceRepository (synchronous, ambient DbContext.Current singleton)
    /// with a modernized, async Npgsql data access layer providing:
    ///   - Report definition CRUD (maps from monolith's public.data_source table)
    ///   - CQRS read-model projection management (new — event-sourced denormalized views)
    ///   - Idempotent event offset tracking (new — per-domain SQS consumption tracking)
    /// 
    /// Architecture decisions per AAP:
    ///   - No DbContext.Current ambient singleton — explicit DI injection of connection string
    ///   - No shared/ambient transactions — explicit BeginTransactionAsync per write operation
    ///   - Schema-level isolation: all tables under "reporting." schema prefix
    ///   - Connection string from SSM Parameter Store SecureString (never env vars)
    ///   - All methods async with CancellationToken for Lambda runtime cancellation
    ///   - Parameterized queries only (NpgsqlParameter) — zero SQL string concatenation
    ///   - Structured JSON logging with correlation-ID via ILogger
    /// </summary>
    public sealed class ReportRepository : IReportRepository
    {
        // ── Schema Constants ────────────────────────────────────────────
        // Schema-level isolation per AAP §0.4.2 Database-Per-Service pattern.
        // Replaces monolith's public.data_source and dynamic rec_* table patterns.

        private const string SchemaName = "reporting";
        private const string ReportDefinitionsTable = "reporting.report_definitions";
        private const string ProjectionsTable = "reporting.read_model_projections";
        private const string EventOffsetsTable = "reporting.event_offsets";

        /// <summary>
        /// Whitelist of allowed column names for ORDER BY clauses in GetAllReportsAsync.
        /// Prevents SQL injection in dynamically constructed ORDER BY expressions.
        /// </summary>
        private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "name", "description", "entity_name", "return_total",
            "weight", "created_by", "created_at", "updated_at"
        };

        /// <summary>
        /// Whitelist of allowed sort directions for ORDER BY clauses.
        /// </summary>
        private static readonly HashSet<string> AllowedSortDirections = new(StringComparer.OrdinalIgnoreCase)
        {
            "asc", "desc"
        };

        // ── Column lists for SELECT statements ──────────────────────────

        private const string ReportDefinitionColumns =
            "id, name, description, sql_template, parameters_json, fields_json, " +
            "entity_name, return_total, weight, created_by, created_at, updated_at";

        private const string ProjectionColumns =
            "id, source_domain, source_entity, source_record_id, projection_data, " +
            "created_at, updated_at";

        // ── Dependencies ────────────────────────────────────────────────

        private readonly string _connectionString;
        private readonly ILogger<ReportRepository> _logger;

        /// <summary>
        /// Initializes a new instance of the ReportRepository with explicit dependency injection.
        /// Connection string is retrieved from SSM Parameter Store by the calling service layer
        /// (per AAP §0.8.6: DB_CONNECTION_STRING stored as SSM SecureString, NEVER environment variables).
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string from SSM Parameter Store.</param>
        /// <param name="logger">Structured logger for correlation-ID propagation per AAP §0.8.5.</param>
        /// <exception cref="ArgumentNullException">Thrown if connectionString or logger is null/empty.</exception>
        public ReportRepository(string connectionString, ILogger<ReportRepository> logger)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString),
                    "Connection string must not be null or empty. " +
                    "Retrieve from SSM Parameter Store SecureString at service startup.");
            }

            _connectionString = connectionString;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // ── Private Connection Helper ───────────────────────────────────

        /// <summary>
        /// Creates and opens a new async PostgreSQL connection.
        /// Replaces monolith's DbContext.Current.CreateConnection() ambient pattern
        /// (source DbConnection.cs lines 37-43) with explicit async connection management.
        /// </summary>
        private async Task<NpgsqlConnection> CreateConnectionAsync(CancellationToken ct)
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }

        // ════════════════════════════════════════════════════════════════
        // Report Definition CRUD
        // ════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Replaces DbDataSourceRepository.Get(Guid id) (source lines 14-28):
        ///   SELECT * FROM public.data_source WHERE id = @id
        /// Modernized: async NpgsqlDataReader instead of synchronous NpgsqlDataAdapter.Fill().
        /// </remarks>
        public async Task<ReportDefinitionDto?> GetReportByIdAsync(Guid id, CancellationToken ct = default)
        {
            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
                await using var command = new NpgsqlCommand(
                    $"SELECT {ReportDefinitionColumns} FROM {ReportDefinitionsTable} WHERE id = @id",
                    connection);

                command.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = id });

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                ReportDefinitionDto? result = null;
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result = MapReportFromReader(reader);
                }

                _logger.LogDebug("GetReportByIdAsync - id={ReportId} found={Found}", id, result is not null);
                return result;
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "PostgreSQL error in GetReportByIdAsync - id={ReportId}", id);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces DbDataSourceRepository.Get(string name) (source lines 35-49):
        ///   SELECT * FROM public.data_source WHERE name = @name
        /// </remarks>
        public async Task<ReportDefinitionDto?> GetReportByNameAsync(string name, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name), "Report name must not be null or empty.");
            }

            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
                await using var command = new NpgsqlCommand(
                    $"SELECT {ReportDefinitionColumns} FROM {ReportDefinitionsTable} WHERE name = @name",
                    connection);

                command.Parameters.Add(new NpgsqlParameter("@name", NpgsqlDbType.Varchar) { Value = name });

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                ReportDefinitionDto? result = null;
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result = MapReportFromReader(reader);
                }

                _logger.LogDebug("GetReportByNameAsync - name={ReportName} found={Found}", name, result is not null);
                return result;
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "PostgreSQL error in GetReportByNameAsync - name={ReportName}", name);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces DbDataSourceRepository.GetAll() (source lines 55-64):
        ///   SELECT * FROM public.data_source
        /// Enhanced with pagination, sorting (with SQL-injection-safe column whitelist),
        /// and total count for UI pagination controls.
        /// </remarks>
        public async Task<(List<ReportDefinitionDto> Items, int TotalCount)> GetAllReportsAsync(
            int page, int pageSize, string sortBy, string sortOrder, CancellationToken ct = default)
        {
            // Validate sortBy against whitelist to prevent SQL injection in ORDER BY clause.
            if (!AllowedSortColumns.Contains(sortBy))
            {
                _logger.LogWarning("Invalid sortBy column '{SortBy}', defaulting to 'name'", sortBy);
                sortBy = "name";
            }

            // Validate sort direction (only "asc" or "desc" allowed).
            if (!AllowedSortDirections.Contains(sortOrder))
            {
                _logger.LogWarning("Invalid sortOrder '{SortOrder}', defaulting to 'asc'", sortOrder);
                sortOrder = "asc";
            }

            // Clamp pagination parameters to valid ranges.
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 25;
            if (pageSize > 1000) pageSize = 1000;

            int offset = (page - 1) * pageSize;

            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

                // Step 1: Get total count (pattern from DbRecordRepository.Count()).
                int totalCount;
                await using (var countCommand = new NpgsqlCommand(
                    $"SELECT COUNT(id) FROM {ReportDefinitionsTable}", connection))
                {
                    var countResult = await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    totalCount = Convert.ToInt32(countResult);
                }

                // Step 2: Get paginated results with safe ORDER BY.
                // sortBy and sortOrder are validated against whitelists above.
                var items = new List<ReportDefinitionDto>();
                var sql = $"SELECT {ReportDefinitionColumns} FROM {ReportDefinitionsTable} " +
                          $"ORDER BY {sortBy} {sortOrder} LIMIT @limit OFFSET @offset";

                await using (var dataCommand = new NpgsqlCommand(sql, connection))
                {
                    dataCommand.Parameters.Add(new NpgsqlParameter("@limit", NpgsqlDbType.Integer) { Value = pageSize });
                    dataCommand.Parameters.Add(new NpgsqlParameter("@offset", NpgsqlDbType.Integer) { Value = offset });

                    await using var reader = await dataCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        items.Add(MapReportFromReader(reader));
                    }
                }

                _logger.LogDebug(
                    "GetAllReportsAsync - page={Page} pageSize={PageSize} sortBy={SortBy} sortOrder={SortOrder} " +
                    "returned={Count} total={Total}",
                    page, pageSize, sortBy, sortOrder, items.Count, totalCount);

                return (items, totalCount);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "PostgreSQL error in GetAllReportsAsync - page={Page} pageSize={PageSize}", page, pageSize);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces DbDataSourceRepository.Create(...) (source lines 79-101).
        /// Modernized: async execution, ACID transaction guarantee via explicit
        /// BeginTransactionAsync/CommitAsync/RollbackAsync, typed NpgsqlDbType parameters,
        /// JSONB cast for parameters_json/fields_json (upgraded from text in monolith).
        /// </remarks>
        public async Task<bool> CreateReportAsync(ReportDefinitionDto report, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(report);

            await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                const string sql = @"
                    INSERT INTO reporting.report_definitions
                        (id, name, description, sql_template, parameters_json, fields_json,
                         entity_name, return_total, weight, created_by, created_at, updated_at)
                    VALUES
                        (@id, @name, @description, @sql_template, @parameters_json::jsonb, @fields_json::jsonb,
                         @entity_name, @return_total, @weight, @created_by, @created_at, @updated_at)";

                await using var command = new NpgsqlCommand(sql, connection, transaction);

                command.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = report.Id });
                command.Parameters.Add(new NpgsqlParameter("@name", NpgsqlDbType.Varchar) { Value = report.Name });
                command.Parameters.Add(new NpgsqlParameter("@description", NpgsqlDbType.Text)
                    { Value = (object?)report.Description ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@sql_template", NpgsqlDbType.Text) { Value = report.SqlTemplate });
                command.Parameters.Add(new NpgsqlParameter("@parameters_json", NpgsqlDbType.Jsonb)
                    { Value = (object?)report.ParametersJson ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@fields_json", NpgsqlDbType.Jsonb)
                    { Value = (object?)report.FieldsJson ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@entity_name", NpgsqlDbType.Varchar)
                    { Value = (object?)report.EntityName ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@return_total", NpgsqlDbType.Boolean) { Value = report.ReturnTotal });
                command.Parameters.Add(new NpgsqlParameter("@weight", NpgsqlDbType.Integer) { Value = report.Weight });
                command.Parameters.Add(new NpgsqlParameter("@created_by", NpgsqlDbType.Uuid)
                    { Value = report.CreatedBy.HasValue ? report.CreatedBy.Value : DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@created_at", NpgsqlDbType.TimestampTz) { Value = report.CreatedAt });
                command.Parameters.Add(new NpgsqlParameter("@updated_at", NpgsqlDbType.TimestampTz) { Value = report.UpdatedAt });

                int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                _logger.LogInformation("Report created: {ReportId} - {ReportName}", report.Id, report.Name);
                return affected > 0;
            }
            catch (NpgsqlException ex)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                _logger.LogError(ex, "PostgreSQL error creating report {ReportId} - {ReportName}", report.Id, report.Name);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces DbDataSourceRepository.Update(...) (source lines 116-141).
        /// Uses explicit ACID transaction with typed NpgsqlParameter bindings.
        /// </remarks>
        public async Task<bool> UpdateReportAsync(ReportDefinitionDto report, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(report);

            await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                const string sql = @"
                    UPDATE reporting.report_definitions SET
                        name = @name,
                        description = @description,
                        sql_template = @sql_template,
                        parameters_json = @parameters_json::jsonb,
                        fields_json = @fields_json::jsonb,
                        entity_name = @entity_name,
                        return_total = @return_total,
                        weight = @weight,
                        updated_at = @updated_at
                    WHERE id = @id";

                await using var command = new NpgsqlCommand(sql, connection, transaction);

                command.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = report.Id });
                command.Parameters.Add(new NpgsqlParameter("@name", NpgsqlDbType.Varchar) { Value = report.Name });
                command.Parameters.Add(new NpgsqlParameter("@description", NpgsqlDbType.Text)
                    { Value = (object?)report.Description ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@sql_template", NpgsqlDbType.Text) { Value = report.SqlTemplate });
                command.Parameters.Add(new NpgsqlParameter("@parameters_json", NpgsqlDbType.Jsonb)
                    { Value = (object?)report.ParametersJson ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@fields_json", NpgsqlDbType.Jsonb)
                    { Value = (object?)report.FieldsJson ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@entity_name", NpgsqlDbType.Varchar)
                    { Value = (object?)report.EntityName ?? DBNull.Value });
                command.Parameters.Add(new NpgsqlParameter("@return_total", NpgsqlDbType.Boolean) { Value = report.ReturnTotal });
                command.Parameters.Add(new NpgsqlParameter("@weight", NpgsqlDbType.Integer) { Value = report.Weight });
                command.Parameters.Add(new NpgsqlParameter("@updated_at", NpgsqlDbType.TimestampTz) { Value = report.UpdatedAt });

                int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                _logger.LogInformation("Report updated: {ReportId}", report.Id);
                return affected > 0;
            }
            catch (NpgsqlException ex)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                _logger.LogError(ex, "PostgreSQL error updating report {ReportId}", report.Id);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Replaces DbDataSourceRepository.Delete(Guid id) (source lines 147-155):
        ///   DELETE FROM public.data_source WHERE id = @id
        /// </remarks>
        public async Task DeleteReportAsync(Guid id, CancellationToken ct = default)
        {
            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
                await using var command = new NpgsqlCommand(
                    $"DELETE FROM {ReportDefinitionsTable} WHERE id = @id", connection);

                command.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = id });

                int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Report deleted: {ReportId} (rows affected: {Affected})", id, affected);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex, "PostgreSQL error deleting report {ReportId}", id);
                throw;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Read-Model Projection CRUD (CQRS event-sourced data)
        // ════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// NEW method — no direct monolith equivalent. Supports CQRS read-model pattern (AAP §0.4.2).
        /// Uses INSERT ... ON CONFLICT ... DO UPDATE (UPSERT) against a UNIQUE index on
        /// (source_domain, source_entity, source_record_id).
        ///
        /// NOTE: The migration creates a non-unique composite index idx_rmp_domain_entity_record.
        /// This UPSERT relies on a UNIQUE constraint. We ensure the unique index exists at runtime
        /// via CREATE UNIQUE INDEX IF NOT EXISTS as an idempotent safety guard (AAP §0.8.5).
        ///
        /// Accepts optional connection/transaction to allow caller-managed transaction lifecycle.
        /// </remarks>
        public async Task UpsertProjectionAsync(
            string sourceDomain,
            string sourceEntity,
            Guid sourceRecordId,
            JsonElement projectionData,
            NpgsqlConnection? connection = null,
            NpgsqlTransaction? transaction = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));
            if (string.IsNullOrWhiteSpace(sourceEntity))
                throw new ArgumentNullException(nameof(sourceEntity));

            bool ownsConnection = connection is null;
            NpgsqlConnection conn = connection ?? await CreateConnectionAsync(ct).ConfigureAwait(false);

            try
            {
                // Ensure unique constraint exists for UPSERT ON CONFLICT.
                // Idempotent — IF NOT EXISTS prevents errors on repeated calls.
                const string ensureUniqueSql = @"
                    CREATE UNIQUE INDEX IF NOT EXISTS uq_rmp_domain_entity_record
                    ON reporting.read_model_projections (source_domain, source_entity, source_record_id)";

                await using (var ensureCmd = new NpgsqlCommand(ensureUniqueSql, conn))
                {
                    if (transaction is not null) ensureCmd.Transaction = transaction;
                    await ensureCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                const string sql = @"
                    INSERT INTO reporting.read_model_projections
                        (id, source_domain, source_entity, source_record_id, projection_data, created_at, updated_at)
                    VALUES
                        (gen_random_uuid(), @source_domain, @source_entity, @source_record_id,
                         @projection_data::jsonb, @now, @now)
                    ON CONFLICT (source_domain, source_entity, source_record_id)
                    DO UPDATE SET
                        projection_data = @projection_data::jsonb,
                        updated_at = @now";

                await using var command = new NpgsqlCommand(sql, conn);
                if (transaction is not null) command.Transaction = transaction;

                var now = DateTime.UtcNow;
                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });
                command.Parameters.Add(new NpgsqlParameter("@source_entity", NpgsqlDbType.Varchar) { Value = sourceEntity });
                command.Parameters.Add(new NpgsqlParameter("@source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });
                command.Parameters.Add(new NpgsqlParameter("@projection_data", NpgsqlDbType.Jsonb)
                    { Value = projectionData.GetRawText() });
                command.Parameters.Add(new NpgsqlParameter("@now", NpgsqlDbType.TimestampTz) { Value = now });

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                _logger.LogDebug(
                    "Projection upserted for {SourceDomain}.{SourceEntity} record {SourceRecordId}",
                    sourceDomain, sourceEntity, sourceRecordId);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error upserting projection for {SourceDomain}.{SourceEntity} record {SourceRecordId}",
                    sourceDomain, sourceEntity, sourceRecordId);
                throw;
            }
            finally
            {
                if (ownsConnection)
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<ProjectionDto?> GetProjectionAsync(
            string sourceDomain,
            string sourceEntity,
            Guid sourceRecordId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));
            if (string.IsNullOrWhiteSpace(sourceEntity))
                throw new ArgumentNullException(nameof(sourceEntity));

            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

                const string sql = @"
                    SELECT id, source_domain, source_entity, source_record_id,
                           projection_data, created_at, updated_at
                    FROM reporting.read_model_projections
                    WHERE source_domain = @source_domain
                      AND source_entity = @source_entity
                      AND source_record_id = @source_record_id";

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });
                command.Parameters.Add(new NpgsqlParameter("@source_entity", NpgsqlDbType.Varchar) { Value = sourceEntity });
                command.Parameters.Add(new NpgsqlParameter("@source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                ProjectionDto? result = null;
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result = MapProjectionFromReader(reader);
                }

                _logger.LogDebug(
                    "GetProjectionAsync - {SourceDomain}.{SourceEntity} record {SourceRecordId} found={Found}",
                    sourceDomain, sourceEntity, sourceRecordId, result is not null);
                return result;
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error in GetProjectionAsync - {SourceDomain}.{SourceEntity} record {SourceRecordId}",
                    sourceDomain, sourceEntity, sourceRecordId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<ProjectionDto>> GetProjectionsByDomainAsync(
            string sourceDomain,
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 25;
            if (pageSize > 1000) pageSize = 1000;

            int offset = (page - 1) * pageSize;

            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

                const string sql = @"
                    SELECT id, source_domain, source_entity, source_record_id,
                           projection_data, created_at, updated_at
                    FROM reporting.read_model_projections
                    WHERE source_domain = @source_domain
                    ORDER BY updated_at DESC
                    LIMIT @limit OFFSET @offset";

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });
                command.Parameters.Add(new NpgsqlParameter("@limit", NpgsqlDbType.Integer) { Value = pageSize });
                command.Parameters.Add(new NpgsqlParameter("@offset", NpgsqlDbType.Integer) { Value = offset });

                var items = new List<ProjectionDto>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add(MapProjectionFromReader(reader));
                }

                _logger.LogDebug(
                    "GetProjectionsByDomainAsync - domain={SourceDomain} page={Page} pageSize={PageSize} returned={Count}",
                    sourceDomain, page, pageSize, items.Count);
                return items;
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error in GetProjectionsByDomainAsync - domain={SourceDomain}",
                    sourceDomain);
                throw;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Hard-deletes a projection row by composite key.
        /// Accepts optional connection/transaction for caller-managed lifecycle.
        /// Idempotent: no error if row does not exist.
        /// </remarks>
        public async Task DeleteProjectionAsync(
            string sourceDomain,
            string sourceEntity,
            Guid sourceRecordId,
            NpgsqlConnection? connection = null,
            NpgsqlTransaction? transaction = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));
            if (string.IsNullOrWhiteSpace(sourceEntity))
                throw new ArgumentNullException(nameof(sourceEntity));

            bool ownsConnection = connection is null;
            NpgsqlConnection conn = connection ?? await CreateConnectionAsync(ct).ConfigureAwait(false);

            try
            {
                const string sql = @"
                    DELETE FROM reporting.read_model_projections
                    WHERE source_domain = @source_domain
                      AND source_entity = @source_entity
                      AND source_record_id = @source_record_id";

                await using var command = new NpgsqlCommand(sql, conn);
                if (transaction is not null) command.Transaction = transaction;

                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });
                command.Parameters.Add(new NpgsqlParameter("@source_entity", NpgsqlDbType.Varchar) { Value = sourceEntity });
                command.Parameters.Add(new NpgsqlParameter("@source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

                int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                _logger.LogDebug(
                    "DeleteProjectionAsync - {SourceDomain}.{SourceEntity} record {SourceRecordId} (rows affected: {Affected})",
                    sourceDomain, sourceEntity, sourceRecordId, affected);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error deleting projection {SourceDomain}.{SourceEntity} record {SourceRecordId}",
                    sourceDomain, sourceEntity, sourceRecordId);
                throw;
            }
            finally
            {
                if (ownsConnection)
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Soft-deletes a projection by appending deletion metadata to the JSONB projection_data.
        /// Uses PostgreSQL JSONB concatenation operator (||) to merge deletion markers into existing data.
        /// Idempotent: updates are safe to repeat (overwriting same deletion markers).
        /// </remarks>
        public async Task SoftDeleteProjectionAsync(
            string sourceDomain,
            string sourceEntity,
            Guid sourceRecordId,
            NpgsqlConnection? connection = null,
            NpgsqlTransaction? transaction = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));
            if (string.IsNullOrWhiteSpace(sourceEntity))
                throw new ArgumentNullException(nameof(sourceEntity));

            bool ownsConnection = connection is null;
            NpgsqlConnection conn = connection ?? await CreateConnectionAsync(ct).ConfigureAwait(false);

            try
            {
                // Merge deletion metadata into existing JSONB data using || operator.
                const string sql = @"
                    UPDATE reporting.read_model_projections
                    SET projection_data = projection_data || @deletion_metadata::jsonb,
                        updated_at = @now
                    WHERE source_domain = @source_domain
                      AND source_entity = @source_entity
                      AND source_record_id = @source_record_id";

                await using var command = new NpgsqlCommand(sql, conn);
                if (transaction is not null) command.Transaction = transaction;

                var now = DateTime.UtcNow;
                // Manual JSON construction avoids anonymous-type serialization
                // which is incompatible with Native AOT (IL2026/IL3050).
                string deletionMetadata = $"{{\"deleted\":true,\"deleted_at\":\"{now.ToString("O")}\"}}";


                command.Parameters.Add(new NpgsqlParameter("@deletion_metadata", NpgsqlDbType.Jsonb) { Value = deletionMetadata });
                command.Parameters.Add(new NpgsqlParameter("@now", NpgsqlDbType.TimestampTz) { Value = now });
                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });
                command.Parameters.Add(new NpgsqlParameter("@source_entity", NpgsqlDbType.Varchar) { Value = sourceEntity });
                command.Parameters.Add(new NpgsqlParameter("@source_record_id", NpgsqlDbType.Uuid) { Value = sourceRecordId });

                int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                _logger.LogDebug(
                    "SoftDeleteProjectionAsync - {SourceDomain}.{SourceEntity} record {SourceRecordId} (rows affected: {Affected})",
                    sourceDomain, sourceEntity, sourceRecordId, affected);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error soft-deleting projection {SourceDomain}.{SourceEntity} record {SourceRecordId}",
                    sourceDomain, sourceEntity, sourceRecordId);
                throw;
            }
            finally
            {
                if (ownsConnection)
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Event Offset Tracking (Idempotent SQS Consumption)
        // ════════════════════════════════════════════════════════════════

        /// <inheritdoc />
        /// <remarks>
        /// Tracks the last processed event ID per source domain for idempotent
        /// SQS event consumption (AAP §0.8.5).
        /// Uses UPSERT against the UNIQUE constraint on event_offsets.source_domain
        /// (confirmed in migration: uq_event_offsets_source_domain).
        /// </remarks>
        public async Task UpsertEventOffsetAsync(
            string sourceDomain,
            string lastEventId,
            NpgsqlConnection? connection = null,
            NpgsqlTransaction? transaction = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));
            if (string.IsNullOrWhiteSpace(lastEventId))
                throw new ArgumentNullException(nameof(lastEventId));

            bool ownsConnection = connection is null;
            NpgsqlConnection conn = connection ?? await CreateConnectionAsync(ct).ConfigureAwait(false);

            try
            {
                const string sql = @"
                    INSERT INTO reporting.event_offsets
                        (id, source_domain, last_event_id, last_processed_at)
                    VALUES
                        (gen_random_uuid(), @source_domain, @last_event_id, @now)
                    ON CONFLICT (source_domain)
                    DO UPDATE SET
                        last_event_id = @last_event_id,
                        last_processed_at = @now";

                await using var command = new NpgsqlCommand(sql, conn);
                if (transaction is not null) command.Transaction = transaction;

                var now = DateTime.UtcNow;
                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });
                command.Parameters.Add(new NpgsqlParameter("@last_event_id", NpgsqlDbType.Varchar) { Value = lastEventId });
                command.Parameters.Add(new NpgsqlParameter("@now", NpgsqlDbType.TimestampTz) { Value = now });

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                _logger.LogDebug(
                    "Event offset upserted for domain={SourceDomain} lastEventId={LastEventId}",
                    sourceDomain, lastEventId);
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error upserting event offset for domain={SourceDomain}",
                    sourceDomain);
                throw;
            }
            finally
            {
                if (ownsConnection)
                {
                    await conn.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Retrieves the last processed event ID for a given source domain.
        /// Returns null if the domain has never been processed (first-time consumption).
        /// </remarks>
        public async Task<string?> GetLastEventIdAsync(string sourceDomain, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sourceDomain))
                throw new ArgumentNullException(nameof(sourceDomain));

            try
            {
                await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);

                const string sql = @"
                    SELECT last_event_id
                    FROM reporting.event_offsets
                    WHERE source_domain = @source_domain";

                await using var command = new NpgsqlCommand(sql, connection);
                command.Parameters.Add(new NpgsqlParameter("@source_domain", NpgsqlDbType.Varchar) { Value = sourceDomain });

                var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

                string? eventId = result is null || result == DBNull.Value ? null : (string)result;

                _logger.LogDebug(
                    "GetLastEventIdAsync - domain={SourceDomain} lastEventId={LastEventId}",
                    sourceDomain, eventId ?? "(none)");
                return eventId;
            }
            catch (NpgsqlException ex)
            {
                _logger.LogError(ex,
                    "PostgreSQL error in GetLastEventIdAsync for domain={SourceDomain}",
                    sourceDomain);
                throw;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Private Mapping Helpers
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Maps a NpgsqlDataReader row to a <see cref="ReportDefinitionDto"/>.
        /// Replaces monolith's DataTable row indexing pattern (DbRecordRepository lines 226-232)
        /// with strongly-typed reader accessors. Handles nullable columns via IsDBNull checks.
        /// Expected columns: id, name, description, sql_template, parameters_json,
        ///   fields_json, entity_name, return_total, weight, created_by, created_at, updated_at
        /// </summary>
        private static ReportDefinitionDto MapReportFromReader(NpgsqlDataReader reader)
        {
            return new ReportDefinitionDto
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Description = reader.IsDBNull(reader.GetOrdinal("description"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("description")),
                SqlTemplate = reader.GetString(reader.GetOrdinal("sql_template")),
                ParametersJson = reader.IsDBNull(reader.GetOrdinal("parameters_json"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("parameters_json")),
                FieldsJson = reader.IsDBNull(reader.GetOrdinal("fields_json"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("fields_json")),
                EntityName = reader.IsDBNull(reader.GetOrdinal("entity_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("entity_name")),
                ReturnTotal = reader.GetBoolean(reader.GetOrdinal("return_total")),
                Weight = reader.GetInt32(reader.GetOrdinal("weight")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("created_by"))
                    ? null
                    : reader.GetGuid(reader.GetOrdinal("created_by")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at"))
            };
        }

        /// <summary>
        /// Maps a NpgsqlDataReader row to a <see cref="ProjectionDto"/>.
        /// Parses the JSONB projection_data string into a <see cref="JsonElement"/>
        /// using System.Text.Json (AOT-compatible) per AAP §0.6.2 import rules.
        /// Expected columns: id, source_domain, source_entity, source_record_id,
        ///   projection_data, created_at, updated_at
        /// </summary>
        private static ProjectionDto MapProjectionFromReader(NpgsqlDataReader reader)
        {
            // Parse JSONB string from PostgreSQL into JsonElement via JsonDocument.
            string projectionDataRaw = reader.IsDBNull(reader.GetOrdinal("projection_data"))
                ? "{}"
                : reader.GetString(reader.GetOrdinal("projection_data"));

            JsonElement projectionElement;
            using (var doc = JsonDocument.Parse(projectionDataRaw))
            {
                // Clone to detach from the JsonDocument's pooled memory.
                projectionElement = doc.RootElement.Clone();
            }

            return new ProjectionDto
            {
                Id = reader.GetGuid(reader.GetOrdinal("id")),
                SourceDomain = reader.GetString(reader.GetOrdinal("source_domain")),
                SourceEntity = reader.GetString(reader.GetOrdinal("source_entity")),
                SourceRecordId = reader.GetGuid(reader.GetOrdinal("source_record_id")),
                ProjectionData = projectionElement,
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("created_at")),
                UpdatedAt = reader.GetDateTime(reader.GetOrdinal("updated_at"))
            };
        }
    }

    #endregion
}
