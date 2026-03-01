using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebVellaErp.Reporting.Migrations;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Integration tests for <see cref="Migration_001_InitialSchema"/> FluentMigrator migration.
    /// Validates that the initial reporting schema is correctly created and can be cleanly
    /// rolled back against LocalStack RDS PostgreSQL on port 4510.
    ///
    /// <para>
    /// All tests execute against LocalStack — NO mocked AWS SDK calls (per AAP Section 0.8.4).
    /// Each test method gets a fresh database via <see cref="IAsyncLifetime"/>
    /// InitializeAsync/DisposeAsync lifecycle management.
    /// </para>
    ///
    /// <para>
    /// Source references:
    /// <list type="bullet">
    ///   <item>DbRepository.CreatePostgresqlExtensions() — uuid-ossp extension creation</item>
    ///   <item>DbDataSourceRepository — data_source table column mapping to report_definitions</item>
    ///   <item>DBTypeConverter — PostgreSQL field type mapping (uuid, timestamptz, text, boolean, integer)</item>
    /// </list>
    /// </para>
    /// </summary>
    [Trait("Category", "Integration")]
    public class MigrationIntegrationTests : IClassFixture<LocalStackFixture>, IAsyncLifetime
    {
        // ============================================================
        // Constants and Fields
        // ============================================================

        /// <summary>
        /// Name of the isolated test database created per test execution.
        /// Separate from the fixture's reporting_test database to avoid collisions.
        /// </summary>
        private const string TestDatabaseName = "reporting_migration_test";

        /// <summary>Shared LocalStack lifecycle fixture providing pre-verified RDS connectivity.</summary>
        private readonly LocalStackFixture _fixture;

        /// <summary>Connection string to PostgreSQL master database for DB lifecycle operations (CREATE/DROP DATABASE).</summary>
        private readonly string _masterConnectionString;

        /// <summary>Connection string to the isolated test database for migration verification queries.</summary>
        private readonly string _testConnectionString;

        // ============================================================
        // Constructor
        // ============================================================

        /// <summary>
        /// Initializes the test class with the shared <see cref="LocalStackFixture"/>.
        /// Derives master and test connection strings from the fixture's
        /// <see cref="LocalStackFixture.RdsConnectionString"/> base pattern.
        /// </summary>
        /// <param name="fixture">Shared LocalStack lifecycle fixture injected via IClassFixture.</param>
        public MigrationIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;

            // Derive connection strings from the fixture's RDS connection base (port 4510)
            var builder = new NpgsqlConnectionStringBuilder(_fixture.RdsConnectionString)
            {
                Database = "postgres"
            };
            _masterConnectionString = builder.ConnectionString;

            // Build the isolated test database connection string
            builder.Database = TestDatabaseName;
            _testConnectionString = builder.ConnectionString;
        }

        // ============================================================
        // IAsyncLifetime — Per-Test Database Lifecycle
        // ============================================================

        /// <summary>
        /// Creates a FRESH PostgreSQL database for each test class run (isolation).
        /// Drops any existing database with the same name first (from prior test runs).
        /// Runs NO migrations — individual tests control migration execution via
        /// <see cref="RunMigrationUp"/> and <see cref="RunMigrationDown"/>.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Clear Npgsql connection pools to release any stale connections to the test database
            NpgsqlConnection.ClearAllPools();

            await using var masterConn = new NpgsqlConnection(_masterConnectionString);
            await masterConn.OpenAsync();

            // Terminate any active connections to the test database (from previous test runs)
            await using (var termCmd = new NpgsqlCommand(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                $"WHERE datname = '{TestDatabaseName}' AND pid <> pg_backend_pid()",
                masterConn))
            {
                await termCmd.ExecuteNonQueryAsync();
            }

            // Drop existing test database if it exists
            await using (var dropCmd = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{TestDatabaseName}\"", masterConn))
            {
                await dropCmd.ExecuteNonQueryAsync();
            }

            // Create a fresh, empty test database
            await using (var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE \"{TestDatabaseName}\"", masterConn))
            {
                await createCmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Drops the test database after tests complete for clean isolation.
        /// Handles exceptions gracefully to avoid masking test failures.
        /// </summary>
        public async Task DisposeAsync()
        {
            try
            {
                // Clear all connection pools first to release connections to the test database
                NpgsqlConnection.ClearAllPools();

                await using var masterConn = new NpgsqlConnection(_masterConnectionString);
                await masterConn.OpenAsync();

                // Terminate any active connections to the test database
                await using (var termCmd = new NpgsqlCommand(
                    $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                    $"WHERE datname = '{TestDatabaseName}' AND pid <> pg_backend_pid()",
                    masterConn))
                {
                    await termCmd.ExecuteNonQueryAsync();
                }

                // Drop the test database
                await using (var dropCmd = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{TestDatabaseName}\"", masterConn))
                {
                    await dropCmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception)
            {
                // Swallow cleanup errors to avoid masking test failures
            }
        }

        // ============================================================
        // Private Helpers — FluentMigrator Runner
        // ============================================================

        /// <summary>
        /// Builds a configured FluentMigrator runner targeting the isolated test database.
        /// Uses PostgreSQL provider and scans the <see cref="Migration_001_InitialSchema"/>
        /// assembly for migration discovery. Pattern matches LocalStackFixture.RunMigrationsAsync().
        /// </summary>
        /// <returns>A configured <see cref="IMigrationRunner"/> ready to execute migrations.</returns>
        private IMigrationRunner BuildMigrationRunner()
        {
            var serviceProvider = new ServiceCollection()
                .AddFluentMigratorCore()
                .ConfigureRunner(rb => rb
                    .AddPostgres()
                    .WithGlobalConnectionString(_testConnectionString)
                    .ScanIn(typeof(Migration_001_InitialSchema).Assembly).For.Migrations())
                .BuildServiceProvider(validateScopes: false);

            return serviceProvider.GetRequiredService<IMigrationRunner>();
        }

        /// <summary>
        /// Executes Migration_001_InitialSchema.Up() via FluentMigrator Runner.
        /// Creates uuid-ossp extension, reporting schema, and all 3 tables with columns,
        /// constraints, indexes, and default values.
        /// </summary>
        private void RunMigrationUp()
        {
            var runner = BuildMigrationRunner();
            runner.MigrateUp();
        }

        /// <summary>
        /// Executes Migration_001_InitialSchema.Down() via FluentMigrator Runner.
        /// Rolls back to version 0, dropping all tables and the reporting schema.
        /// Preserves the uuid-ossp extension (other schemas may depend on it).
        /// </summary>
        private void RunMigrationDown()
        {
            var runner = BuildMigrationRunner();
            runner.MigrateDown(0);
        }

        // ============================================================
        // Private Helpers — Schema Verification Queries
        // ============================================================

        /// <summary>
        /// Checks whether a table exists in the specified schema via information_schema.tables.
        /// </summary>
        private async Task<bool> TableExistsAsync(string schemaName, string tableName)
        {
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = @schema AND table_name = @table",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            return count > 0;
        }

        /// <summary>
        /// Gets the list of column names for a table in the specified schema,
        /// ordered by ordinal position.
        /// </summary>
        private async Task<List<string>> GetColumnNamesAsync(string schemaName, string tableName)
        {
            var columns = new List<string>();
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = @table " +
                "ORDER BY ordinal_position",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
            return columns;
        }

        /// <summary>
        /// Gets the data_type for a specific column from information_schema.columns.
        /// </summary>
        private async Task<string> GetColumnDataTypeAsync(
            string schemaName, string tableName, string columnName)
        {
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT data_type FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = @table AND column_name = @column",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            cmd.Parameters.AddWithValue("column", columnName);
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Gets the is_nullable value for a specific column (YES or NO).
        /// </summary>
        private async Task<string> GetColumnNullabilityAsync(
            string schemaName, string tableName, string columnName)
        {
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT is_nullable FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = @table AND column_name = @column",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            cmd.Parameters.AddWithValue("column", columnName);
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Gets the character_maximum_length for a varchar column.
        /// Returns null for non-varchar columns.
        /// </summary>
        private async Task<int?> GetColumnMaxLengthAsync(
            string schemaName, string tableName, string columnName)
        {
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT character_maximum_length FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = @table AND column_name = @column",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            cmd.Parameters.AddWithValue("column", columnName);
            var result = await cmd.ExecuteScalarAsync();
            return result is DBNull or null ? null : Convert.ToInt32(result);
        }

        /// <summary>
        /// Gets the column_default value for a specific column from information_schema.
        /// Returns null if no default is defined.
        /// </summary>
        private async Task<string?> GetColumnDefaultAsync(
            string schemaName, string tableName, string columnName)
        {
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT column_default FROM information_schema.columns " +
                "WHERE table_schema = @schema AND table_name = @table AND column_name = @column",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            cmd.Parameters.AddWithValue("column", columnName);
            var result = await cmd.ExecuteScalarAsync();
            return result is DBNull or null ? null : result.ToString();
        }

        /// <summary>
        /// Gets all index names for a specific table in the specified schema from pg_indexes.
        /// </summary>
        private async Task<List<string>> GetIndexNamesAsync(string schemaName, string tableName)
        {
            var indexes = new List<string>();
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT indexname FROM pg_indexes " +
                "WHERE schemaname = @schema AND tablename = @table",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("table", tableName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
            return indexes;
        }

        /// <summary>
        /// Gets the index definition (CREATE INDEX DDL statement) for a specific index.
        /// Useful for verifying UNIQUE, GIN, and composite index properties.
        /// </summary>
        private async Task<string?> GetIndexDefinitionAsync(string schemaName, string indexName)
        {
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT indexdef FROM pg_indexes " +
                "WHERE schemaname = @schema AND indexname = @index",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            cmd.Parameters.AddWithValue("index", indexName);
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString();
        }

        /// <summary>
        /// Gets all index names across all tables in the specified schema from pg_indexes.
        /// Used by <see cref="Up_VerifyAllIndexes"/> for comprehensive index verification.
        /// </summary>
        private async Task<List<string>> GetAllSchemaIndexNamesAsync(string schemaName)
        {
            var indexes = new List<string>();
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT indexname FROM pg_indexes WHERE schemaname = @schema",
                conn);
            cmd.Parameters.AddWithValue("schema", schemaName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                indexes.Add(reader.GetString(0));
            }
            return indexes;
        }

        // ============================================================
        // Phase 2: Forward Migration (Up) Tests — Extension & Schema
        // ============================================================

        /// <summary>
        /// After running Up(), verifies uuid-ossp extension exists via pg_extension.
        /// Source reference: DbRepository.CreatePostgresqlExtensions() line 30:
        /// CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
        /// </summary>
        [RdsFact]
        public async Task Up_CreatesUuidOsspExtension()
        {
            // Arrange & Act
            RunMigrationUp();

            // Assert — verify uuid-ossp extension exists in pg_extension catalog
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_extension WHERE extname = 'uuid-ossp'", conn);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().BeGreaterThan(0,
                "uuid-ossp extension should be created by Up() migration");
        }

        /// <summary>
        /// After running Up(), verifies the reporting schema exists via information_schema.schemata.
        /// </summary>
        [RdsFact]
        public async Task Up_CreatesReportingSchema()
        {
            // Arrange & Act
            RunMigrationUp();

            // Assert — verify reporting schema exists
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'reporting'",
                conn);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().BeGreaterThan(0,
                "reporting schema should be created by Up() migration");
        }

        // ============================================================
        // Phase 2: Forward Migration (Up) Tests — report_definitions Table
        // ============================================================

        /// <summary>
        /// Verifies report_definitions table exists in the reporting schema after Up().
        /// </summary>
        [RdsFact]
        public async Task Up_CreatesReportDefinitionsTable()
        {
            RunMigrationUp();

            var exists = await TableExistsAsync("reporting", "report_definitions");
            exists.Should().BeTrue(
                "Up() should create the report_definitions table in the reporting schema");
        }

        /// <summary>
        /// Verifies report_definitions has exactly 12 columns matching the migration definition:
        /// id, name, description, sql_template, parameters_json, fields_json, entity_name,
        /// return_total, weight, created_by, created_at, updated_at.
        /// Source reference: DbDataSourceRepository INSERT columns mapping.
        /// </summary>
        [RdsFact]
        public async Task Up_ReportDefinitionsTable_HasAllColumns()
        {
            RunMigrationUp();

            var columns = await GetColumnNamesAsync("reporting", "report_definitions");

            columns.Should().HaveCount(12,
                "report_definitions should have exactly 12 columns");
            columns.Should().Contain("id");
            columns.Should().Contain("name");
            columns.Should().Contain("description");
            columns.Should().Contain("sql_template");
            columns.Should().Contain("parameters_json");
            columns.Should().Contain("fields_json");
            columns.Should().Contain("entity_name");
            columns.Should().Contain("return_total");
            columns.Should().Contain("weight");
            columns.Should().Contain("created_by");
            columns.Should().Contain("created_at");
            columns.Should().Contain("updated_at");
        }

        /// <summary>
        /// Verifies PK constraint named pk_report_definitions exists on the id column.
        /// </summary>
        [RdsFact]
        public async Task Up_ReportDefinitionsTable_HasPrimaryKey()
        {
            RunMigrationUp();

            // Query pg_constraint for primary key on report_definitions
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(@"
                SELECT c.conname
                FROM pg_constraint c
                JOIN pg_namespace n ON n.oid = c.connamespace
                WHERE n.nspname = 'reporting'
                  AND c.conrelid = 'reporting.report_definitions'::regclass
                  AND c.contype = 'p'", conn);
            var pkName = await cmd.ExecuteScalarAsync();

            pkName.Should().NotBeNull(
                "report_definitions should have a primary key constraint");
            pkName!.ToString().Should().Contain("pk_report_definitions",
                "Primary key should be named pk_report_definitions");
        }

        /// <summary>
        /// Verifies UNIQUE constraint/index uq_report_definitions_name exists on the name column.
        /// </summary>
        [RdsFact]
        public async Task Up_ReportDefinitionsTable_HasUniqueNameConstraint()
        {
            RunMigrationUp();

            var indexes = await GetIndexNamesAsync("reporting", "report_definitions");
            indexes.Should().Contain("uq_report_definitions_name",
                "report_definitions should have a unique index on the name column");

            // Verify the index definition includes UNIQUE keyword
            var indexDef = await GetIndexDefinitionAsync("reporting", "uq_report_definitions_name");
            indexDef.Should().NotBeNull();
            indexDef!.Should().Contain("UNIQUE",
                "uq_report_definitions_name should be a UNIQUE index");
        }

        // ============================================================
        // Phase 2: Forward Migration (Up) Tests — read_model_projections Table
        // ============================================================

        /// <summary>
        /// Verifies read_model_projections table exists in the reporting schema after Up().
        /// </summary>
        [RdsFact]
        public async Task Up_CreatesReadModelProjectionsTable()
        {
            RunMigrationUp();

            var exists = await TableExistsAsync("reporting", "read_model_projections");
            exists.Should().BeTrue(
                "Up() should create the read_model_projections table");
        }

        /// <summary>
        /// Verifies read_model_projections has exactly 7 columns:
        /// id, source_domain, source_entity, source_record_id, projection_data,
        /// created_at, updated_at.
        /// </summary>
        [RdsFact]
        public async Task Up_ReadModelProjectionsTable_HasAllColumns()
        {
            RunMigrationUp();

            var columns = await GetColumnNamesAsync("reporting", "read_model_projections");

            columns.Should().HaveCount(7,
                "read_model_projections should have exactly 7 columns");
            columns.Should().Contain("id");
            columns.Should().Contain("source_domain");
            columns.Should().Contain("source_entity");
            columns.Should().Contain("source_record_id");
            columns.Should().Contain("projection_data");
            columns.Should().Contain("created_at");
            columns.Should().Contain("updated_at");
        }

        /// <summary>
        /// Verifies composite index idx_rmp_domain_entity_record on
        /// (source_domain, source_entity, source_record_id).
        /// </summary>
        [RdsFact]
        public async Task Up_ReadModelProjectionsTable_HasCompositeIndex()
        {
            RunMigrationUp();

            var indexes = await GetIndexNamesAsync("reporting", "read_model_projections");
            indexes.Should().Contain("idx_rmp_domain_entity_record",
                "read_model_projections should have a composite index named idx_rmp_domain_entity_record");

            // Verify the index covers all three columns
            var indexDef = await GetIndexDefinitionAsync("reporting", "idx_rmp_domain_entity_record");
            indexDef.Should().NotBeNull();
            indexDef!.Should().Contain("source_domain");
            indexDef.Should().Contain("source_entity");
            indexDef.Should().Contain("source_record_id");
        }

        /// <summary>
        /// Verifies GIN index idx_rmp_projection_data on projection_data JSONB column.
        /// Per AAP Section 0.7.4: JSONB with GIN index for efficient query projections.
        /// </summary>
        [RdsFact]
        public async Task Up_ReadModelProjectionsTable_HasGinIndex()
        {
            RunMigrationUp();

            var indexes = await GetIndexNamesAsync("reporting", "read_model_projections");
            indexes.Should().Contain("idx_rmp_projection_data",
                "read_model_projections should have a GIN index on projection_data");

            // Verify the index definition confirms GIN access method
            var indexDef = await GetIndexDefinitionAsync("reporting", "idx_rmp_projection_data");
            indexDef.Should().NotBeNull();
            indexDef!.ToLower().Should().Contain("gin",
                "idx_rmp_projection_data should use GIN access method");
            indexDef.Should().Contain("projection_data",
                "GIN index should cover the projection_data column");
        }

        // ============================================================
        // Phase 2: Forward Migration (Up) Tests — event_offsets Table
        // ============================================================

        /// <summary>
        /// Verifies event_offsets table exists in the reporting schema after Up().
        /// </summary>
        [RdsFact]
        public async Task Up_CreatesEventOffsetsTable()
        {
            RunMigrationUp();

            var exists = await TableExistsAsync("reporting", "event_offsets");
            exists.Should().BeTrue(
                "Up() should create the event_offsets table");
        }

        /// <summary>
        /// Verifies event_offsets has exactly 4 columns:
        /// id, source_domain, last_event_id, last_processed_at.
        /// </summary>
        [RdsFact]
        public async Task Up_EventOffsetsTable_HasAllColumns()
        {
            RunMigrationUp();

            var columns = await GetColumnNamesAsync("reporting", "event_offsets");

            columns.Should().HaveCount(4,
                "event_offsets should have exactly 4 columns");
            columns.Should().Contain("id");
            columns.Should().Contain("source_domain");
            columns.Should().Contain("last_event_id");
            columns.Should().Contain("last_processed_at");
        }

        /// <summary>
        /// Verifies UNIQUE constraint uq_event_offsets_source_domain on source_domain column.
        /// Ensures each domain has exactly one offset tracking record.
        /// </summary>
        [RdsFact]
        public async Task Up_EventOffsetsTable_HasUniqueDomainConstraint()
        {
            RunMigrationUp();

            var indexes = await GetIndexNamesAsync("reporting", "event_offsets");
            indexes.Should().Contain("uq_event_offsets_source_domain",
                "event_offsets should have a unique constraint on source_domain");

            // Verify the index definition includes UNIQUE keyword
            var indexDef = await GetIndexDefinitionAsync("reporting", "uq_event_offsets_source_domain");
            indexDef.Should().NotBeNull();
            indexDef!.Should().Contain("UNIQUE",
                "uq_event_offsets_source_domain should be a UNIQUE index");
        }

        // ============================================================
        // Phase 3: Column Type Verification Tests
        // ============================================================

        /// <summary>
        /// Verifies data_type = 'uuid' for all UUID columns across all 3 tables:
        /// report_definitions.id, report_definitions.created_by,
        /// read_model_projections.id, read_model_projections.source_record_id,
        /// event_offsets.id.
        /// Source reference: DBTypeConverter GuidField → uuid mapping.
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyUuidColumnTypes()
        {
            RunMigrationUp();

            var uuidColumns = new[]
            {
                ("report_definitions", "id"),
                ("report_definitions", "created_by"),
                ("read_model_projections", "id"),
                ("read_model_projections", "source_record_id"),
                ("event_offsets", "id")
            };

            foreach (var (table, column) in uuidColumns)
            {
                var dataType = await GetColumnDataTypeAsync("reporting", table, column);
                dataType.Should().Be("uuid",
                    $"Column {table}.{column} should be of type uuid");
            }
        }

        /// <summary>
        /// Verifies character_varying type and correct character_maximum_length for all varchar columns:
        /// report_definitions.name (255), report_definitions.entity_name (255),
        /// read_model_projections.source_domain (100), read_model_projections.source_entity (100),
        /// event_offsets.source_domain (100), event_offsets.last_event_id (255).
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyVarcharColumnTypes()
        {
            RunMigrationUp();

            var varcharColumns = new[]
            {
                ("report_definitions", "name", 255),
                ("report_definitions", "entity_name", 255),
                ("read_model_projections", "source_domain", 100),
                ("read_model_projections", "source_entity", 100),
                ("event_offsets", "source_domain", 100),
                ("event_offsets", "last_event_id", 255)
            };

            foreach (var (table, column, expectedLength) in varcharColumns)
            {
                var dataType = await GetColumnDataTypeAsync("reporting", table, column);
                dataType.Should().Be("character varying",
                    $"Column {table}.{column} should be of type character varying (varchar)");

                var maxLength = await GetColumnMaxLengthAsync("reporting", table, column);
                maxLength.Should().Be(expectedLength,
                    $"Column {table}.{column} should have max length {expectedLength}");
            }
        }

        /// <summary>
        /// Verifies data_type = 'text' for text columns:
        /// report_definitions.description, report_definitions.sql_template.
        /// Source reference: DBTypeConverter TextField → text mapping.
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyTextColumnTypes()
        {
            RunMigrationUp();

            var textColumns = new[]
            {
                ("report_definitions", "description"),
                ("report_definitions", "sql_template")
            };

            foreach (var (table, column) in textColumns)
            {
                var dataType = await GetColumnDataTypeAsync("reporting", table, column);
                dataType.Should().Be("text",
                    $"Column {table}.{column} should be of type text");
            }
        }

        /// <summary>
        /// Verifies data_type = 'jsonb' for JSONB columns:
        /// report_definitions.parameters_json, report_definitions.fields_json,
        /// read_model_projections.projection_data.
        /// Per AAP Section 0.7.4: upgraded from text to JSONB for efficient query projections.
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyJsonbColumnTypes()
        {
            RunMigrationUp();

            var jsonbColumns = new[]
            {
                ("report_definitions", "parameters_json"),
                ("report_definitions", "fields_json"),
                ("read_model_projections", "projection_data")
            };

            foreach (var (table, column) in jsonbColumns)
            {
                var dataType = await GetColumnDataTypeAsync("reporting", table, column);
                dataType.Should().Be("jsonb",
                    $"Column {table}.{column} should be of type jsonb " +
                    "(upgraded from text for efficient query projections)");
            }
        }

        /// <summary>
        /// Verifies data_type = 'boolean' for: report_definitions.return_total.
        /// Source reference: DBTypeConverter CheckboxField → boolean mapping.
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyBooleanColumnTypes()
        {
            RunMigrationUp();

            var dataType = await GetColumnDataTypeAsync("reporting", "report_definitions", "return_total");
            dataType.Should().Be("boolean",
                "Column report_definitions.return_total should be of type boolean");
        }

        /// <summary>
        /// Verifies data_type = 'integer' for: report_definitions.weight.
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyIntegerColumnTypes()
        {
            RunMigrationUp();

            var dataType = await GetColumnDataTypeAsync("reporting", "report_definitions", "weight");
            dataType.Should().Be("integer",
                "Column report_definitions.weight should be of type integer");
        }

        /// <summary>
        /// Verifies data_type = 'timestamp with time zone' (timestamptz) for all timestamp columns:
        /// report_definitions.created_at, report_definitions.updated_at,
        /// read_model_projections.created_at, read_model_projections.updated_at,
        /// event_offsets.last_processed_at.
        /// Source reference: DBTypeConverter DateTimeField → timestamptz mapping.
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyTimestamptzColumnTypes()
        {
            RunMigrationUp();

            var timestamptzColumns = new[]
            {
                ("report_definitions", "created_at"),
                ("report_definitions", "updated_at"),
                ("read_model_projections", "created_at"),
                ("read_model_projections", "updated_at"),
                ("event_offsets", "last_processed_at")
            };

            foreach (var (table, column) in timestamptzColumns)
            {
                var dataType = await GetColumnDataTypeAsync("reporting", table, column);
                dataType.Should().Be("timestamp with time zone",
                    $"Column {table}.{column} should be of type timestamp with time zone (timestamptz)");
            }
        }

        // ============================================================
        // Phase 4: Default Value Verification Tests
        // ============================================================

        /// <summary>
        /// Verifies uuid_generate_v4() defaults on all id columns by:
        /// 1. Querying information_schema.columns for column_default containing uuid_generate_v4()
        /// 2. Inserting a row WITHOUT specifying id and verifying a valid UUID is auto-generated
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyUuidGenerateDefault()
        {
            RunMigrationUp();

            // Verify uuid_generate_v4() default on all id columns via information_schema
            var idColumns = new[]
            {
                ("report_definitions", "id"),
                ("read_model_projections", "id"),
                ("event_offsets", "id")
            };

            foreach (var (table, column) in idColumns)
            {
                var columnDefault = await GetColumnDefaultAsync("reporting", table, column);
                columnDefault.Should().NotBeNull(
                    $"Column {table}.{column} should have a default value defined");
                columnDefault!.Should().Contain("uuid_generate_v4()",
                    $"Column {table}.{column} should default to uuid_generate_v4()");
            }

            // Functional verification: insert a row WITHOUT specifying id
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO reporting.report_definitions (name, sql_template) " +
                "VALUES ('uuid_gen_test', 'SELECT 1') RETURNING id", conn);
            var generatedId = await cmd.ExecuteScalarAsync();
            generatedId.Should().NotBeNull(
                "UUID should be auto-generated when id is not specified");
            var guid = (Guid)generatedId!;
            guid.Should().NotBe(Guid.Empty,
                "Auto-generated UUID should not be an empty GUID");
        }

        /// <summary>
        /// Verifies now() defaults on all timestamp columns by:
        /// 1. Querying information_schema.columns for column_default containing now()
        /// 2. Inserting a row WITHOUT specifying timestamps and verifying they are populated
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyTimestampDefaults()
        {
            RunMigrationUp();

            // Verify now() default on all timestamp columns via information_schema
            var timestampColumns = new[]
            {
                ("report_definitions", "created_at"),
                ("report_definitions", "updated_at"),
                ("read_model_projections", "created_at"),
                ("read_model_projections", "updated_at"),
                ("event_offsets", "last_processed_at")
            };

            foreach (var (table, column) in timestampColumns)
            {
                var columnDefault = await GetColumnDefaultAsync("reporting", table, column);
                columnDefault.Should().NotBeNull(
                    $"Column {table}.{column} should have a default value defined");
                columnDefault!.Should().Contain("now()",
                    $"Column {table}.{column} should default to now()");
            }

            // Functional verification: insert a row WITHOUT specifying timestamps
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO reporting.report_definitions (name, sql_template) " +
                "VALUES ('ts_default_test', 'SELECT 1') RETURNING created_at, updated_at", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(
                "INSERT should return a row with generated timestamps");

            var createdAt = reader.GetDateTime(0);
            var updatedAt = reader.GetDateTime(1);
            createdAt.Should().NotBe(default(DateTime),
                "created_at should be automatically populated by now() default");
            updatedAt.Should().NotBe(default(DateTime),
                "updated_at should be automatically populated by now() default");
        }

        /// <summary>
        /// Inserts a report_definitions row WITHOUT specifying return_total and verifies
        /// it defaults to true (matching the migration's .WithDefaultValue(true)).
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyReturnTotalDefault()
        {
            RunMigrationUp();

            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO reporting.report_definitions (name, sql_template) " +
                "VALUES ('return_total_test', 'SELECT 1') RETURNING return_total", conn);
            var returnTotal = await cmd.ExecuteScalarAsync();

            returnTotal.Should().NotBeNull();
            ((bool)returnTotal!).Should().BeTrue(
                "report_definitions.return_total should default to true");
        }

        /// <summary>
        /// Inserts a report_definitions row WITHOUT specifying weight and verifies
        /// it defaults to 0 (matching the migration's .WithDefaultValue(0)).
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyWeightDefault()
        {
            RunMigrationUp();

            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO reporting.report_definitions (name, sql_template) " +
                "VALUES ('weight_default_test', 'SELECT 1') RETURNING weight", conn);
            var weight = await cmd.ExecuteScalarAsync();

            weight.Should().NotBeNull();
            ((int)weight!).Should().Be(0,
                "report_definitions.weight should default to 0");
        }

        // ============================================================
        // Phase 5: Rollback Migration (Down) Tests
        // ============================================================

        /// <summary>
        /// Runs Up() then Down(). Verifies all three tables no longer exist:
        /// 1. reporting.event_offsets — dropped first
        /// 2. reporting.read_model_projections — dropped second
        /// 3. reporting.report_definitions — dropped third
        /// Queries information_schema.tables for each and verifies 0 results.
        /// </summary>
        [RdsFact]
        public async Task Down_DropsTablesInCorrectOrder()
        {
            // Arrange — run Up() to create all objects
            RunMigrationUp();

            // Act — run Down() to roll back all migrations
            RunMigrationDown();

            // Assert — verify all three tables no longer exist
            var eventOffsetsExists = await TableExistsAsync("reporting", "event_offsets");
            eventOffsetsExists.Should().BeFalse(
                "event_offsets table should be dropped by Down()");

            var readModelProjectionsExists = await TableExistsAsync("reporting", "read_model_projections");
            readModelProjectionsExists.Should().BeFalse(
                "read_model_projections table should be dropped by Down()");

            var reportDefinitionsExists = await TableExistsAsync("reporting", "report_definitions");
            reportDefinitionsExists.Should().BeFalse(
                "report_definitions table should be dropped by Down()");
        }

        /// <summary>
        /// After Down(), verifies the reporting schema no longer exists by querying
        /// information_schema.schemata. The Down() method uses DROP SCHEMA IF EXISTS ... CASCADE.
        /// </summary>
        [RdsFact]
        public async Task Down_DropsReportingSchema()
        {
            // Arrange
            RunMigrationUp();

            // Act
            RunMigrationDown();

            // Assert — verify reporting schema no longer exists
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'reporting'",
                conn);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(0,
                "reporting schema should be dropped by Down() migration");
        }

        /// <summary>
        /// After Down(), verifies uuid-ossp extension still exists.
        /// The extension should NOT be dropped as other schemas may depend on it.
        /// The Down() migration intentionally omits DROP EXTENSION.
        /// </summary>
        [RdsFact]
        public async Task Down_PreservesUuidOsspExtension()
        {
            // Arrange
            RunMigrationUp();

            // Act
            RunMigrationDown();

            // Assert — uuid-ossp extension should still exist
            await using var conn = new NpgsqlConnection(_testConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_extension WHERE extname = 'uuid-ossp'", conn);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().BeGreaterThan(0,
                "uuid-ossp extension should be preserved after Down() — " +
                "other schemas may depend on it");
        }

        // ============================================================
        // Phase 6: Index Verification Tests
        // ============================================================

        /// <summary>
        /// Comprehensive index verification after Up(). Queries pg_indexes for
        /// schemaname = 'reporting' and verifies all 8 expected indexes exist:
        /// <list type="bullet">
        ///   <item>uq_report_definitions_name — UNIQUE on report_definitions.name</item>
        ///   <item>idx_report_definitions_entity_name — on report_definitions.entity_name</item>
        ///   <item>idx_report_definitions_created_by — on report_definitions.created_by</item>
        ///   <item>idx_rmp_domain_entity_record — composite on read_model_projections(source_domain, source_entity, source_record_id)</item>
        ///   <item>idx_rmp_source_domain — on read_model_projections.source_domain</item>
        ///   <item>idx_rmp_updated_at — on read_model_projections.updated_at</item>
        ///   <item>idx_rmp_projection_data — GIN on read_model_projections.projection_data</item>
        ///   <item>uq_event_offsets_source_domain — UNIQUE on event_offsets.source_domain</item>
        /// </list>
        /// </summary>
        [RdsFact]
        public async Task Up_VerifyAllIndexes()
        {
            RunMigrationUp();

            // Get all indexes in the reporting schema
            var allIndexes = await GetAllSchemaIndexNamesAsync("reporting");

            // Expected indexes per the migration definition
            var expectedIndexes = new[]
            {
                "uq_report_definitions_name",
                "idx_report_definitions_entity_name",
                "idx_report_definitions_created_by",
                "idx_rmp_domain_entity_record",
                "idx_rmp_source_domain",
                "idx_rmp_updated_at",
                "idx_rmp_projection_data",
                "uq_event_offsets_source_domain"
            };

            // Verify all expected indexes exist
            foreach (var expectedIndex in expectedIndexes)
            {
                allIndexes.Should().Contain(expectedIndex,
                    $"Index '{expectedIndex}' should exist in the reporting schema");
            }

            // Verify unique indexes are actually UNIQUE
            var uniqueNameDef = await GetIndexDefinitionAsync(
                "reporting", "uq_report_definitions_name");
            uniqueNameDef.Should().NotBeNull();
            uniqueNameDef!.Should().Contain("UNIQUE",
                "uq_report_definitions_name should be a UNIQUE index");

            var uniqueDomainDef = await GetIndexDefinitionAsync(
                "reporting", "uq_event_offsets_source_domain");
            uniqueDomainDef.Should().NotBeNull();
            uniqueDomainDef!.Should().Contain("UNIQUE",
                "uq_event_offsets_source_domain should be a UNIQUE index");

            // Verify GIN index uses the correct access method
            var ginDef = await GetIndexDefinitionAsync(
                "reporting", "idx_rmp_projection_data");
            ginDef.Should().NotBeNull();
            ginDef!.ToLower().Should().Contain("gin",
                "idx_rmp_projection_data should use GIN access method for JSONB queries");

            // Verify composite index covers all expected columns
            var compositeDef = await GetIndexDefinitionAsync(
                "reporting", "idx_rmp_domain_entity_record");
            compositeDef.Should().NotBeNull();
            compositeDef!.Should().Contain("source_domain",
                "Composite index should include source_domain");
            compositeDef.Should().Contain("source_entity",
                "Composite index should include source_entity");
            compositeDef.Should().Contain("source_record_id",
                "Composite index should include source_record_id");
        }
    }
}
