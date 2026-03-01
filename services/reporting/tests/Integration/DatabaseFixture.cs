using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebVellaErp.Reporting.Migrations;
using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// Manages the lifecycle of an RDS PostgreSQL test database for Reporting &amp; Analytics
    /// service integration tests. Creates a fresh, isolated database per test class,
    /// runs FluentMigrator migrations to establish the <c>reporting</c> schema, provides
    /// helper methods for test data seeding and cleanup, and drops the database on disposal.
    ///
    /// <para>
    /// This fixture replaces the monolith's ambient <c>DbContext.Current</c> singleton pattern
    /// (source <c>DbContext.cs</c>) with explicit <see cref="NpgsqlConnection"/> management.
    /// Each test class that uses this fixture via <c>IClassFixture&lt;DatabaseFixture&gt;</c>
    /// gets a completely isolated database instance — eliminating cross-test contamination.
    /// </para>
    ///
    /// <para>
    /// <b>CRITICAL:</b> Connects to LocalStack RDS PostgreSQL on port 4510.
    /// NO mocked database connections (per AAP Section 0.8.4).
    /// </para>
    ///
    /// <para>
    /// Architectural replacement mapping:
    /// <list type="bullet">
    ///   <item><c>DbContext.Current.CreateConnection()</c> → <see cref="CreateConnectionAsync()"/></item>
    ///   <item><c>DbConnection</c> wrapper with savepoints → Direct <see cref="NpgsqlConnection"/> + <see cref="NpgsqlTransaction"/></item>
    ///   <item><c>DbRepository.CreatePostgresqlExtensions()</c> → <see cref="Migration_001_InitialSchema.Up()"/> via FluentMigrator</item>
    ///   <item><c>DbRepository.CreateTable()/CreateColumn()</c> → FluentMigrator <c>Create.Table().InSchema()</c> fluent API</item>
    ///   <item>Shared single database → Schema-isolated <c>reporting.*</c> tables per Database-Per-Service</item>
    /// </list>
    /// </para>
    /// </summary>
    public class DatabaseFixture : IAsyncLifetime
    {
        // ============================================================
        // Constants — LocalStack RDS PostgreSQL Configuration
        // ============================================================

        /// <summary>Hostname for LocalStack RDS PostgreSQL.</summary>
        private const string RdsHost = "localhost";

        /// <summary>LocalStack RDS PostgreSQL port per AAP Section 0.8.6.</summary>
        private const int RdsPort = 4510;

        /// <summary>Default PostgreSQL username for LocalStack.</summary>
        private const string RdsUsername = "postgres";

        /// <summary>Default PostgreSQL password for LocalStack.</summary>
        private const string RdsPassword = "postgres";

        /// <summary>Master/admin database for CREATE/DROP DATABASE operations.</summary>
        private const string MasterDatabase = "postgres";

        /// <summary>
        /// Maximum length for PostgreSQL database identifiers.
        /// PostgreSQL limits identifiers to 63 bytes (matching source EntityManager.cs validation).
        /// </summary>
        private const int MaxIdentifierLength = 63;

        // ============================================================
        // Public Properties
        // ============================================================

        /// <summary>
        /// Connection string for the isolated test database, available to all test classes
        /// via <c>IClassFixture&lt;DatabaseFixture&gt;</c>.
        /// Format: <c>Host=localhost;Port=4510;Database={unique_name};Username=postgres;Password=postgres</c>
        /// </summary>
        public string ConnectionString { get; private set; } = string.Empty;

        // ============================================================
        // Private Fields
        // ============================================================

        /// <summary>
        /// Connection string to the master/postgres database for administrative operations
        /// (CREATE DATABASE, DROP DATABASE, pg_terminate_backend).
        /// </summary>
        private string _masterConnectionString = string.Empty;

        /// <summary>
        /// Unique database name for this fixture instance.
        /// Format: <c>reporting_test_{guid_hex}</c>, truncated to 63 characters.
        /// </summary>
        private string _databaseName = string.Empty;

        // ============================================================
        // IAsyncLifetime — Async Setup
        // ============================================================

        /// <summary>
        /// Creates a fresh test database, runs FluentMigrator migrations to establish
        /// the reporting schema (uuid-ossp extension, 3 tables with indexes), and verifies
        /// database connectivity.
        ///
        /// <para>
        /// Migration creates:
        /// <list type="bullet">
        ///   <item><c>reporting.report_definitions</c> — 12 columns, PK, UNIQUE on name, indexes</item>
        ///   <item><c>reporting.read_model_projections</c> — 7 columns, composite index, GIN index on JSONB</item>
        ///   <item><c>reporting.event_offsets</c> — 4 columns, UNIQUE on source_domain</item>
        /// </list>
        /// </para>
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Generate unique database name
            // PostgreSQL limits identifiers to 63 chars (matching EntityManager.cs validation).
            // Using Guid.NewGuid() hex format ensures globally unique names across parallel test runs.
            var rawName = $"reporting_test_{Guid.NewGuid():N}";
            _databaseName = rawName.Length > MaxIdentifierLength
                ? rawName.Substring(0, MaxIdentifierLength)
                : rawName;

            // Step 2: Build master connection string for admin operations (CREATE/DROP DATABASE)
            _masterConnectionString = BuildConnectionString(MasterDatabase);

            // Steps 3-6: RDS PostgreSQL setup — wrapped in try-catch to avoid fixture crash
            // when RDS is unavailable (LocalStack Pro required). Tests decorated with [RdsFact]
            // will be automatically skipped when RDS is not available.
            try
            {
                // Step 3: Create the fresh test database
                await CreateTestDatabaseAsync();

                // Step 4: Build connection string for the new test database
                ConnectionString = BuildConnectionString(_databaseName);

                // Step 5: Run FluentMigrator migrations to create the reporting schema
                // This replaces the monolith's DbRepository.CreatePostgresqlExtensions() +
                // DbRepository.CreateTable() + CreateColumn() pattern with versioned migrations.
                await RunFluentMigrationsAsync();

                // Step 6: Verify database connectivity with a simple query
                await VerifyDatabaseConnectionAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DatabaseFixture] WARNING: RDS PostgreSQL setup failed. " +
                    $"RDS-dependent integration tests will be skipped via [RdsFact]. Error: {ex.Message}");
            }
        }

        // ============================================================
        // IAsyncLifetime — Async Teardown
        // ============================================================

        /// <summary>
        /// Terminates all active connections to the test database and drops it.
        /// Handles exceptions gracefully to avoid masking test failures.
        ///
        /// <para>
        /// Cleanup order:
        /// <list type="number">
        ///   <item>Clear Npgsql connection pools to release pooled connections</item>
        ///   <item>Terminate active backend connections via <c>pg_terminate_backend()</c></item>
        ///   <item>Drop the test database with <c>DROP DATABASE IF EXISTS</c></item>
        /// </list>
        /// </para>
        /// </summary>
        public async Task DisposeAsync()
        {
            if (string.IsNullOrEmpty(_databaseName) || string.IsNullOrEmpty(_masterConnectionString))
            {
                return;
            }

            try
            {
                // Clear all Npgsql connection pools to release pooled connections
                // before attempting to drop the database. Pooled connections would
                // otherwise prevent DROP DATABASE from succeeding.
                NpgsqlConnection.ClearAllPools();

                await using var masterConn = new NpgsqlConnection(_masterConnectionString);
                await masterConn.OpenAsync();

                // Terminate all active backend connections to the test database.
                // pg_terminate_backend() sends SIGTERM to each backend process.
                // Excludes the current connection (pg_backend_pid()) to avoid self-termination.
                await using (var terminateCmd = new NpgsqlCommand(
                    $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                    $"WHERE datname = '{_databaseName}' AND pid <> pg_backend_pid()",
                    masterConn))
                {
                    await terminateCmd.ExecuteNonQueryAsync();
                }

                // Allow a brief pause for backends to terminate cleanly
                await Task.Delay(100);

                // Drop the test database. IF EXISTS prevents errors if the database
                // was already dropped (e.g., by a previous cleanup attempt).
                await using (var dropCmd = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{_databaseName}\"",
                    masterConn))
                {
                    await dropCmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                // Log but do not throw — cleanup failures should not mask test failures.
                // This follows the same graceful-cleanup pattern as LocalStackFixture.SafeExecuteAsync().
                Console.Error.WriteLine(
                    $"[DatabaseFixture] Warning: Error during cleanup of database '{_databaseName}': {ex.Message}");
            }
        }

        // ============================================================
        // Public Helper Methods — Connection Management
        // ============================================================

        /// <summary>
        /// Creates and opens a new <see cref="NpgsqlConnection"/> to the test database.
        /// Callers are responsible for disposing the returned connection.
        ///
        /// <para>
        /// Replaces the monolith's <c>DbContext.Current.CreateConnection()</c> pattern
        /// (source <c>DbContext.cs</c> lines 54-69) with explicit connection management.
        /// No ambient context, no connection stack, no advisory lock wrappers — just
        /// a clean, direct PostgreSQL connection.
        /// </para>
        /// </summary>
        /// <returns>An opened <see cref="NpgsqlConnection"/> to the test database.</returns>
        /// <exception cref="NpgsqlException">Thrown when the connection cannot be established.</exception>
        public async Task<NpgsqlConnection> CreateConnectionAsync()
        {
            if (string.IsNullOrEmpty(ConnectionString))
            {
                throw new InvalidOperationException(
                    "DatabaseFixture has not been initialized. Ensure InitializeAsync() completed successfully.");
            }

            var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        // ============================================================
        // Public Helper Methods — Table Cleanup
        // ============================================================

        /// <summary>
        /// Truncates all reporting tables for test isolation between test methods.
        /// Tables are truncated in dependency-safe order with CASCADE to handle
        /// any future FK relationships.
        ///
        /// <para>
        /// Tables truncated:
        /// <list type="bullet">
        ///   <item><c>reporting.event_offsets</c></item>
        ///   <item><c>reporting.read_model_projections</c></item>
        ///   <item><c>reporting.report_definitions</c></item>
        /// </list>
        /// </para>
        /// </summary>
        public async Task CleanAllTablesAsync()
        {
            await using var connection = await CreateConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                @"TRUNCATE TABLE reporting.event_offsets CASCADE;
                  TRUNCATE TABLE reporting.read_model_projections CASCADE;
                  TRUNCATE TABLE reporting.report_definitions CASCADE;",
                connection);
            await cmd.ExecuteNonQueryAsync();
        }

        // ============================================================
        // Public Helper Methods — Test Data Seeding
        // ============================================================

        /// <summary>
        /// Inserts a test report definition directly via SQL, bypassing the ReportRepository.
        /// Used for test setup when you need a known report definition in the database
        /// before exercising service/repository methods under test.
        ///
        /// <para>
        /// Maps to monolith's <c>DbDataSourceRepository.Create()</c> (source lines 79-100),
        /// adapted for the new <c>reporting.report_definitions</c> schema.
        /// </para>
        /// </summary>
        /// <param name="id">Unique identifier for the report definition.</param>
        /// <param name="name">Human-readable report name (must be unique per UNIQUE constraint).</param>
        /// <param name="sqlTemplate">SQL query template for report execution.</param>
        public async Task SeedTestReportAsync(Guid id, string name, string sqlTemplate)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Report name cannot be null or empty.", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(sqlTemplate))
            {
                throw new ArgumentException("SQL template cannot be null or empty.", nameof(sqlTemplate));
            }

            await using var connection = await CreateConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO reporting.report_definitions
                    (id, name, sql_template, return_total, weight, created_at, updated_at)
                  VALUES
                    (@id, @name, @sqlTemplate, true, 0, now(), now())",
                connection);

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@sqlTemplate", sqlTemplate);

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Inserts a test read-model projection directly via SQL, bypassing the ProjectionService.
        /// Used for test setup when you need a known projection in the database before
        /// exercising service/repository methods or EventConsumer logic under test.
        ///
        /// <para>
        /// Supports the CQRS read-model pattern (AAP Section 0.4.2) where projections
        /// are materialized from domain events with <c>{domain}.{entity}.{action}</c> naming.
        /// </para>
        /// </summary>
        /// <param name="domain">Source bounded context domain (e.g., "crm", "invoicing").</param>
        /// <param name="entity">Source entity type (e.g., "contact", "invoice").</param>
        /// <param name="recordId">Original record ID from the source bounded context.</param>
        /// <param name="projectionJson">JSON string representing the denormalized projection data.</param>
        public async Task SeedTestProjectionAsync(
            string domain,
            string entity,
            Guid recordId,
            string projectionJson)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new ArgumentException("Domain cannot be null or empty.", nameof(domain));
            }

            if (string.IsNullOrWhiteSpace(entity))
            {
                throw new ArgumentException("Entity cannot be null or empty.", nameof(entity));
            }

            if (string.IsNullOrWhiteSpace(projectionJson))
            {
                throw new ArgumentException("Projection JSON cannot be null or empty.", nameof(projectionJson));
            }

            await using var connection = await CreateConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                @"INSERT INTO reporting.read_model_projections
                    (id, source_domain, source_entity, source_record_id, projection_data, created_at, updated_at)
                  VALUES
                    (uuid_generate_v4(), @domain, @entity, @recordId, @projectionJson::jsonb, now(), now())",
                connection);

            cmd.Parameters.AddWithValue("@domain", domain);
            cmd.Parameters.AddWithValue("@entity", entity);
            cmd.Parameters.AddWithValue("@recordId", recordId);
            cmd.Parameters.AddWithValue("@projectionJson", projectionJson);

            await cmd.ExecuteNonQueryAsync();
        }

        // ============================================================
        // Private Methods — Database Lifecycle
        // ============================================================

        /// <summary>
        /// Creates the test database using a connection to the master/postgres database.
        /// </summary>
        private async Task CreateTestDatabaseAsync()
        {
            await using var masterConn = new NpgsqlConnection(_masterConnectionString);
            await masterConn.OpenAsync();

            // CREATE DATABASE requires a non-transactional connection.
            // PostgreSQL does not allow CREATE DATABASE inside a transaction block.
            await using var createCmd = new NpgsqlCommand(
                $"CREATE DATABASE \"{_databaseName}\"",
                masterConn);
            await createCmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Runs FluentMigrator migrations against the test database to create
        /// the complete reporting schema.
        ///
        /// <para>
        /// Replaces the monolith's manual DDL approach:
        /// <list type="bullet">
        ///   <item><c>DbRepository.CreatePostgresqlExtensions()</c> → uuid-ossp extension</item>
        ///   <item><c>DbRepository.CreateTable()</c> → <c>Create.Table().InSchema("reporting")</c></item>
        ///   <item><c>DbRepository.SetPrimaryKey()</c> → <c>.PrimaryKey()</c> in fluent chain</item>
        ///   <item><c>DbRepository.CreateIndex()</c> → <c>Create.Index()</c></item>
        ///   <item><c>DbRepository.CreateUniqueConstraint()</c> → <c>.WithOptions().Unique()</c></item>
        /// </list>
        /// </para>
        /// </summary>
        private async Task RunFluentMigrationsAsync()
        {
            // Build a DI container with FluentMigrator services.
            // This mirrors the production migration runner setup but targets the
            // isolated test database via ConnectionString.
            var serviceProvider = new ServiceCollection()
                .AddFluentMigratorCore()
                .ConfigureRunner(rb => rb
                    .AddPostgres()
                    .WithGlobalConnectionString(ConnectionString)
                    .ScanIn(typeof(Migration_001_InitialSchema).Assembly).For.Migrations())
                .AddLogging(lb => lb.AddConsole())
                .BuildServiceProvider(false);

            // Execute all pending migrations (Migration_001_InitialSchema.Up()).
            // This creates:
            //   1. uuid-ossp extension
            //   2. reporting schema
            //   3. reporting.report_definitions table (12 columns, PK, UNIQUE, indexes)
            //   4. reporting.read_model_projections table (7 columns, composite index, GIN)
            //   5. reporting.event_offsets table (4 columns, UNIQUE on source_domain)
            using (var scope = serviceProvider.CreateScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
                runner.MigrateUp();
            }

            // Dispose the service provider to release migration runner resources
            if (serviceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies the test database is accessible by executing a simple <c>SELECT 1</c> query.
        /// This confirms that the database was created successfully and migrations completed
        /// without leaving the database in an inconsistent state.
        /// </summary>
        private async Task VerifyDatabaseConnectionAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            await using var cmd = new NpgsqlCommand("SELECT 1", connection);
            var result = await cmd.ExecuteScalarAsync();

            if (result == null || Convert.ToInt32(result) != 1)
            {
                throw new InvalidOperationException(
                    $"Database verification failed for '{_databaseName}': " +
                    "SELECT 1 did not return the expected result.");
            }
        }

        /// <summary>
        /// Builds a PostgreSQL connection string for the specified database name
        /// targeting LocalStack RDS PostgreSQL on port 4510.
        /// </summary>
        /// <param name="database">The database name to connect to.</param>
        /// <returns>A formatted connection string.</returns>
        private static string BuildConnectionString(string database)
        {
            return $"Host={RdsHost};Port={RdsPort};Database={database};" +
                   $"Username={RdsUsername};Password={RdsPassword}";
        }
    }
}
