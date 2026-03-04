using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using WebVella.Erp.Service.Admin.Database;

namespace WebVella.Erp.Tests.Admin.Fixtures
{
    /// <summary>
    /// xUnit <see cref="IAsyncLifetime"/> fixture that manages the full lifecycle of a
    /// Docker-based PostgreSQL 16 container for Admin/SDK service database integration tests.
    ///
    /// <para><b>Responsibilities:</b></para>
    /// <list type="bullet">
    ///   <item>Provisions an isolated <c>erp_admin</c> PostgreSQL database using
    ///         <c>Testcontainers.PostgreSql</c> (postgres:16-alpine image)</item>
    ///   <item>Creates prerequisite PostgreSQL extensions (<c>uuid-ossp</c>, <c>pg_trgm</c>)
    ///         matching the monolith's <c>DbRepository.CreatePostgresqlExtensions()</c></item>
    ///   <item>Creates prerequisite system tables (<c>system_log</c>, <c>jobs</c>,
    ///         <c>plugin_data</c>, <c>entities</c>, <c>entity_relations</c>,
    ///         <c>system_settings</c>) required by LogService and ClearJobAndErrorLogsJob</item>
    ///   <item>Instantiates <see cref="AdminWebApplicationFactory"/> with the live
    ///         Testcontainer connection string for in-memory test server hosting</item>
    ///   <item>Runs EF Core migrations via <see cref="AdminDbContext.Database"/> to
    ///         ensure the Admin service schema is up-to-date</item>
    ///   <item>Provides <see cref="SeedTestDataAsync"/> for populating test data
    ///         (&gt;1000 records) to trigger LogService cleanup threshold logic</item>
    /// </list>
    ///
    /// <para>This fixture is shared across all Admin service test classes via
    /// <c>ICollectionFixture&lt;PostgreSqlContainerFixture&gt;</c> in the
    /// <c>AdminTestCollection</c> collection definition, ensuring the container is
    /// started once and reused across the entire test collection.</para>
    ///
    /// <para><b>Source patterns preserved:</b></para>
    /// <list type="bullet">
    ///   <item><c>Startup.cs</c> line 40: <c>AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)</c></item>
    ///   <item><c>DbRepository.cs</c> lines 26-46: PostgreSQL extension creation</item>
    ///   <item><c>LogService.cs</c> lines 19-50: <c>system_log</c> and <c>jobs</c> table queries</item>
    ///   <item><c>SdkPlugin._.cs</c> lines 68-71: <c>plugin_data</c> JSON persistence</item>
    /// </list>
    /// </summary>
    public class PostgreSqlContainerFixture : IAsyncLifetime
    {
        #region Fields

        /// <summary>
        /// The Testcontainers PostgreSQL container instance managing the Docker lifecycle.
        /// Configured with <c>postgres:16-alpine</c> image matching the AAP Section 0.7.4
        /// Docker Compose stack specification.
        /// </summary>
        private readonly PostgreSqlContainer _postgresContainer;

        #endregion

        #region Properties

        /// <summary>
        /// Live connection string from the running PostgreSQL Testcontainer.
        /// This connection string is passed to <see cref="AdminWebApplicationFactory"/>
        /// to override the production <c>ConnectionStrings:Default</c> configuration.
        /// Only valid after <see cref="InitializeAsync"/> has completed successfully.
        /// </summary>
        public string ConnectionString => _postgresContainer.GetConnectionString();

        /// <summary>
        /// The custom <see cref="AdminWebApplicationFactory"/> hosting the Admin service
        /// in-memory test server. Initialized during <see cref="InitializeAsync"/> after
        /// the PostgreSQL container is started and prerequisite tables are created.
        /// Provides <c>Services</c> for DI container access and <c>CreateClient()</c>
        /// for HTTP client creation in integration tests.
        /// </summary>
        public AdminWebApplicationFactory Factory { get; private set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the PostgreSQL Testcontainer configuration.
        /// The container is NOT started here — it starts in <see cref="InitializeAsync"/>.
        ///
        /// <para>Container configuration:</para>
        /// <list type="bullet">
        ///   <item>Image: <c>postgres:16-alpine</c> — matches AAP 0.7.4 Docker Compose</item>
        ///   <item>Database: <c>erp_admin</c> — matches database-per-service naming (AAP 0.4.1)</item>
        ///   <item>Username: <c>test</c> / Password: <c>test</c> — test-only credentials</item>
        ///   <item>CleanUp: <c>true</c> — ensures container removal after test run</item>
        /// </list>
        ///
        /// <para>Also sets the Npgsql legacy timestamp behavior switch that must be
        /// configured before any Npgsql connections are created (from Startup.cs line 40).</para>
        /// </summary>
        public PostgreSqlContainerFixture()
        {
            // CRITICAL: Preserve Npgsql legacy timestamp behavior from monolith Startup.cs line 40.
            // This must be set before ANY Npgsql connection is created to ensure DateTime values
            // are handled with the legacy behavior the monolith's schema and queries depend on.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("erp_admin")
                .WithUsername("test")
                .WithPassword("test")
                .WithCleanUp(true)
                .Build();
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Called ONCE by xUnit before any tests in the <c>"AdminService"</c> collection run.
        /// Performs the complete Admin service database initialization sequence:
        /// <list type="number">
        ///   <item>Starts the PostgreSQL Docker container</item>
        ///   <item>Creates prerequisite tables and extensions via raw SQL</item>
        ///   <item>Instantiates <see cref="AdminWebApplicationFactory"/> with the live connection string</item>
        ///   <item>Runs EF Core migrations to apply the Admin service schema</item>
        /// </list>
        /// </summary>
        /// <returns>A task representing the async initialization operation.</returns>
        public async Task InitializeAsync()
        {
            // 1. Start the PostgreSQL container — blocks until the container is healthy
            //    and accepting connections on the dynamically mapped port.
            await _postgresContainer.StartAsync();

            // 2. Create prerequisite tables and PostgreSQL extensions needed by the
            //    Admin service before EF Core migrations run. These include system tables
            //    (system_log, jobs, plugin_data) referenced by LogService and
            //    ClearJobAndErrorLogsJob, plus metadata tables (entities, entity_relations,
            //    system_settings) needed for EntityManager operations.
            await CreatePrerequisiteTablesAsync();

            // 3. Create the AdminWebApplicationFactory with the live connection string
            //    from the running Testcontainer. The factory overrides the production
            //    ConnectionStrings:Default with this test-specific connection.
            Factory = new AdminWebApplicationFactory(ConnectionString);

            // 4. Run EF Core migrations for AdminDbContext to apply the Admin service
            //    database schema. Uses the Factory's DI container to resolve the DbContext
            //    with the test connection string already configured.
            try
            {
                using (var scope = Factory.Services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
                    await dbContext.Database.MigrateAsync();
                }
            }
            catch (Exception ex)
            {
                // EF Core migrations may fail if no migration files exist yet in the
                // Admin service project. In that case, the prerequisite tables created
                // above are sufficient for Admin service tests to run.
                // Log the exception but don't rethrow — the tables are already created.
                System.Diagnostics.Debug.WriteLine(
                    $"[PostgreSqlContainerFixture] EF Core migration warning: {ex.Message}. " +
                    "Prerequisite tables were created via raw SQL and should be sufficient.");
            }
        }

        /// <summary>
        /// Called ONCE by xUnit after all tests in the <c>"AdminService"</c> collection complete.
        /// Disposes the <see cref="AdminWebApplicationFactory"/> (stops the in-memory test server)
        /// and then disposes the PostgreSQL Testcontainer (stops and removes the Docker container).
        /// Resources are disposed in reverse order of creation for clean shutdown.
        /// </summary>
        /// <returns>A task representing the async disposal operation.</returns>
        public async Task DisposeAsync()
        {
            // Dispose the Factory first (stops the ASP.NET Core test server and closes
            // all database connections held by the DI container) before stopping the
            // PostgreSQL container to avoid connection errors during shutdown.
            if (Factory != null)
            {
                Factory.Dispose();
                Factory = null;
            }

            // Dispose the PostgreSQL container — stops the Docker container and removes
            // it (WithCleanUp(true) ensures cleanup even if DisposeAsync isn't called).
            await _postgresContainer.DisposeAsync();
        }

        #endregion

        #region Prerequisite Table Creation

        /// <summary>
        /// Creates PostgreSQL extensions and prerequisite system tables required by the
        /// Admin service before EF Core migrations run. These tables match the monolith's
        /// schema from <c>DbRepository.CheckCreateSystemTables()</c> and are referenced by:
        /// <list type="bullet">
        ///   <item><c>LogService.ClearJobAndErrorLogs()</c> — queries <c>system_log</c> and <c>jobs</c></item>
        ///   <item><c>LogService.ClearErrorLogs()</c> — queries <c>system_log</c></item>
        ///   <item><c>LogService.ClearJobLogs()</c> — queries <c>jobs</c></item>
        ///   <item><c>SdkPlugin.ProcessPatches()</c> — reads/writes <c>plugin_data</c></item>
        ///   <item><c>EntityManager</c> — reads <c>entities</c> metadata</item>
        ///   <item><c>EntityRelationManager</c> — reads <c>entity_relations</c></item>
        /// </list>
        ///
        /// All tables use <c>CREATE TABLE IF NOT EXISTS</c> to be idempotent with
        /// subsequent EF Core migrations that may also create these tables.
        /// </summary>
        private async Task CreatePrerequisiteTablesAsync()
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            // ================================================================
            // PostgreSQL Extensions
            // Matching DbRepository.CreatePostgresqlExtensions() (DbRepository.cs lines 26-46)
            // uuid-ossp: Required for uuid_generate_v1() default values
            // pg_trgm:   Required for trigram-based text similarity search
            // ================================================================
            await ExecuteSqlAsync(connection, "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
            await ExecuteSqlAsync(connection, "CREATE EXTENSION IF NOT EXISTS \"pg_trgm\";");

            // ================================================================
            // system_log table
            // Referenced by LogService.ClearJobAndErrorLogs() (LogService.cs line 19):
            //   SELECT id, created_on FROM system_log ORDER BY created_on ASC
            // Referenced by LogService.ClearErrorLogs() (LogService.cs line 72):
            //   SELECT id FROM system_log
            // Delete operations (LogService.cs line 28):
            //   DELETE FROM system_log WHERE id = @id
            // Columns match monolith WebVella.Erp.Diagnostics.Log schema.
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS system_log (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    created_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT now(),
                    type INTEGER NOT NULL DEFAULT 0,
                    source TEXT DEFAULT '',
                    message TEXT DEFAULT '',
                    details TEXT,
                    notification_status INTEGER NOT NULL DEFAULT 0
                );
            ");

            // ================================================================
            // jobs table
            // Referenced by LogService.ClearJobAndErrorLogs() (LogService.cs line 36):
            //   SELECT id, created_on FROM jobs
            //     WHERE status = 3 OR status = 4 OR status = 5 OR status = 6
            //     ORDER BY created_on ASC
            // Referenced by LogService.ClearJobLogs() (LogService.cs line 56):
            //   SELECT id, created_on FROM jobs
            //     WHERE status = 3 OR status = 4 OR status = 5 OR status = 6
            // Delete operations (LogService.cs line 45):
            //   DELETE FROM jobs WHERE id = @id
            // Status enum: Pending=1, Running=2, Canceled=3, Failed=4, Finished=5, Aborted=6
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS jobs (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    type_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    type_name TEXT NOT NULL DEFAULT '',
                    complete_class_name TEXT NOT NULL DEFAULT '',
                    attributes TEXT,
                    status INTEGER NOT NULL DEFAULT 1,
                    priority INTEGER NOT NULL DEFAULT 1,
                    started_on TIMESTAMP WITHOUT TIME ZONE,
                    finished_on TIMESTAMP WITHOUT TIME ZONE,
                    aborted_by UUID,
                    canceled_by UUID,
                    error_message TEXT,
                    schedule_plan_id UUID,
                    created_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT now(),
                    created_by UUID,
                    last_modified_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT now(),
                    last_modified_by UUID,
                    result TEXT
                );
            ");

            // ================================================================
            // schedule_plans table
            // Referenced by AdminDbContext.SchedulePlans DbSet for schedule
            // management operations in the Admin service.
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS schedule_plans (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    name TEXT NOT NULL DEFAULT '',
                    type INTEGER NOT NULL DEFAULT 1,
                    start_date TIMESTAMP WITHOUT TIME ZONE,
                    end_date TIMESTAMP WITHOUT TIME ZONE,
                    schedule_days TEXT,
                    interval_in_minutes INTEGER,
                    start_timespan INTEGER,
                    end_timespan INTEGER,
                    last_trigger_time TIMESTAMP WITHOUT TIME ZONE,
                    next_trigger_time TIMESTAMP WITHOUT TIME ZONE,
                    job_type_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
                    job_attributes TEXT,
                    enabled BOOLEAN NOT NULL DEFAULT true,
                    last_started_job_id UUID,
                    created_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT now(),
                    last_modified_by UUID,
                    last_modified_on TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT now()
                );
            ");

            // ================================================================
            // plugin_data table
            // Referenced by SdkPlugin._.cs (lines 68-71) via GetPluginData()/SavePluginData():
            //   Tracks patch versions as JSON in plugin_data.data column
            // Source: WebVella.Erp/Database/DbRepository.cs — CheckCreateSystemTables()
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS plugin_data (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    name TEXT NOT NULL UNIQUE,
                    data TEXT
                );
            ");

            // ================================================================
            // entities table
            // Needed for EntityManager metadata operations.
            // Stores entity definitions as JSON text.
            // Source: WebVella.Erp/Database/DbEntityRepository.cs
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS entities (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    json TEXT NOT NULL DEFAULT '{}'
                );
            ");

            // ================================================================
            // entity_relations table
            // Needed for EntityRelationManager relation metadata.
            // Stores relation definitions as JSON text.
            // Source: WebVella.Erp/Database/DbRelationRepository.cs
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS entity_relations (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    json TEXT NOT NULL DEFAULT '{}'
                );
            ");

            // ================================================================
            // system_settings table
            // Stores global system settings as a JSON text field.
            // Source: WebVella.Erp/Database/DbSystemSettingsRepository.cs
            // ================================================================
            await ExecuteSqlAsync(connection, @"
                CREATE TABLE IF NOT EXISTS system_settings (
                    id UUID PRIMARY KEY DEFAULT uuid_generate_v1(),
                    version INTEGER NOT NULL DEFAULT 1,
                    settings TEXT
                );
            ");

            // ================================================================
            // Create indexes matching AdminDbContext.OnModelCreating configuration
            // ================================================================
            await ExecuteSqlAsync(connection,
                "CREATE INDEX IF NOT EXISTS ix_system_log_created_on ON system_log (created_on DESC);");
            await ExecuteSqlAsync(connection,
                "CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs (status);");
            await ExecuteSqlAsync(connection,
                "CREATE INDEX IF NOT EXISTS ix_jobs_created_on ON jobs (created_on DESC);");
        }

        /// <summary>
        /// Executes a raw SQL command against the provided PostgreSQL connection.
        /// Used for DDL operations (CREATE TABLE, CREATE EXTENSION, CREATE INDEX)
        /// during prerequisite table creation.
        /// </summary>
        /// <param name="connection">An open <see cref="NpgsqlConnection"/> to execute the SQL on.</param>
        /// <param name="sql">The SQL statement to execute.</param>
        private static async Task ExecuteSqlAsync(NpgsqlConnection connection, string sql)
        {
            using var command = new NpgsqlCommand(sql, connection);
            command.CommandTimeout = 60; // Generous timeout for DDL operations
            await command.ExecuteNonQueryAsync();
        }

        #endregion

        #region Test Data Seeding

        /// <summary>
        /// Seeds test data into the <c>system_log</c> and <c>jobs</c> tables for Admin
        /// service integration tests. Creates &gt;1000 records older than 30 days in each
        /// table to properly test the LogService cleanup threshold logic.
        ///
        /// <para><b>LogService threshold logic (LogService.cs lines 22-23, 39-40):</b></para>
        /// <code>
        /// if (logRows.Count &gt; 1000 &amp;&amp; (DateTime)logRows[0]["created_on"] &lt; logThreshold)
        /// </code>
        /// <para>This method creates 1100 records (exceeding the 1000 threshold) with
        /// <c>created_on</c> dates older than 30 days (exceeding the 30-day threshold)
        /// to trigger the cleanup logic in <c>ClearJobAndErrorLogs()</c>.</para>
        ///
        /// <para><b>Job status values used for seeding:</b></para>
        /// <list type="bullet">
        ///   <item>Canceled = 3 — matches LogService.cs line 36 WHERE clause</item>
        ///   <item>Failed = 4 — matches LogService.cs line 36 WHERE clause</item>
        ///   <item>Finished = 5 — matches LogService.cs line 36 WHERE clause</item>
        ///   <item>Aborted = 6 — matches LogService.cs line 36 WHERE clause</item>
        /// </list>
        ///
        /// <para>This method truncates existing data before seeding to ensure idempotent
        /// test state. Can be called by individual test classes to reset test data.</para>
        /// </summary>
        /// <returns>A task representing the async seeding operation.</returns>
        public async Task SeedTestDataAsync()
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            // Clear existing test data for idempotent seeding.
            // Uses TRUNCATE for fast deletion and automatic sequence reset.
            // CASCADE handles any foreign key references.
            await ExecuteSqlAsync(connection, "TRUNCATE system_log, jobs, plugin_data CASCADE;");

            // ================================================================
            // Seed system_log records for testing cleanup logic
            // Creates 1100 records with created_on older than 30 days to trigger
            // the threshold: logRows.Count > 1000 && created_on < threshold
            // ================================================================
            await SeedSystemLogRecordsAsync(connection, 1100);

            // ================================================================
            // Seed jobs records for testing cleanup logic
            // Creates 1100 records with various completed statuses (3, 4, 5, 6)
            // and created_on older than 30 days to trigger the threshold:
            // jobRows.Count > 1000 && created_on < threshold
            // Status filter: WHERE status = 3 OR status = 4 OR status = 5 OR status = 6
            // ================================================================
            await SeedJobRecordsAsync(connection, 1100);
        }

        /// <summary>
        /// Seeds <paramref name="count"/> system_log records into the database using
        /// batched INSERT statements for performance. Each record has a <c>created_on</c>
        /// date older than 30 days (starting from 31 days ago, incrementing backward).
        /// </summary>
        /// <param name="connection">An open PostgreSQL connection.</param>
        /// <param name="count">The number of log records to create (default 1100).</param>
        private static async Task SeedSystemLogRecordsAsync(NpgsqlConnection connection, int count)
        {
            const int batchSize = 100;
            var baseTime = DateTime.UtcNow;

            for (int batchStart = 0; batchStart < count; batchStart += batchSize)
            {
                var sb = new StringBuilder();
                sb.AppendLine("INSERT INTO system_log (id, created_on, type, source, message) VALUES");

                int batchEnd = Math.Min(batchStart + batchSize, count);
                for (int i = batchStart; i < batchEnd; i++)
                {
                    // created_on: 31+ days ago to exceed the 30-day threshold
                    var createdOn = baseTime.AddDays(-31 - i);
                    var formattedDate = createdOn.ToString("yyyy-MM-dd HH:mm:ss.ffffff");

                    if (i > batchStart)
                    {
                        sb.AppendLine(",");
                    }

                    sb.Append($"(uuid_generate_v1(), '{formattedDate}', 1, 'test', 'Test log entry {i}')");
                }

                sb.Append(';');
                await ExecuteSqlAsync(connection, sb.ToString());
            }
        }

        /// <summary>
        /// Seeds <paramref name="count"/> job records into the database using batched
        /// INSERT statements for performance. Records cycle through completed statuses
        /// (Canceled=3, Failed=4, Finished=5, Aborted=6) matching the LogService
        /// WHERE clause filter. Each record has a <c>created_on</c> date older than 30 days.
        /// </summary>
        /// <param name="connection">An open PostgreSQL connection.</param>
        /// <param name="count">The number of job records to create (default 1100).</param>
        private static async Task SeedJobRecordsAsync(NpgsqlConnection connection, int count)
        {
            const int batchSize = 100;
            var baseTime = DateTime.UtcNow;
            // Status values matching LogService.cs line 36 WHERE clause:
            // status = 3 (Canceled) OR status = 4 (Failed) OR status = 5 (Finished) OR status = 6 (Aborted)
            var jobStatuses = new[] { 3, 4, 5, 6 };

            for (int batchStart = 0; batchStart < count; batchStart += batchSize)
            {
                var sb = new StringBuilder();
                sb.AppendLine("INSERT INTO jobs (id, created_on, type_id, type_name, complete_class_name, status, priority, last_modified_on) VALUES");

                int batchEnd = Math.Min(batchStart + batchSize, count);
                for (int i = batchStart; i < batchEnd; i++)
                {
                    var status = jobStatuses[i % jobStatuses.Length];
                    // created_on: 31+ days ago to exceed the 30-day threshold
                    var createdOn = baseTime.AddDays(-31 - i);
                    var formattedDate = createdOn.ToString("yyyy-MM-dd HH:mm:ss.ffffff");

                    if (i > batchStart)
                    {
                        sb.AppendLine(",");
                    }

                    sb.Append($"(uuid_generate_v1(), '{formattedDate}', uuid_generate_v1(), 'TestJob', 'TestJobClass', {status}, 1, '{formattedDate}')");
                }

                sb.Append(';');
                await ExecuteSqlAsync(connection, sb.ToString());
            }
        }

        #endregion
    }
}
