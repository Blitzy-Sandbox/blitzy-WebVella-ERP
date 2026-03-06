// =============================================================================
// WebVella ERP — Project/Task Service Test Infrastructure
// ProjectDatabaseFixture.cs
// =============================================================================
// Primary xUnit class fixture for all Project/Task service tests. Manages the
// complete lifecycle of:
//   - PostgreSQL 16 Testcontainer for isolated database integration testing
//   - ProjectWebApplicationFactory for in-memory ASP.NET Core test hosting
//   - EF Core migration execution against the Testcontainer database
//   - Test data seeding (projects, tasks, timelogs, comments, feed items)
//   - Shared well-known IDs matching the monolith's Definitions.cs SystemIds
//
// All test classes in the "ProjectService" collection share a single instance
// of this fixture, ensuring:
//   - One PostgreSQL container per test run (not per test class)
//   - One EF Core migration execution (schema creation happens once)
//   - One seed data population (consistent test baseline)
//   - One HttpClient creation for REST controller integration tests
//
// Key source references:
//   - WebVella.Erp/Api/Definitions.cs (lines 6-21): SystemIds GUIDs
//   - WebVella.Erp/Database/DbContext.cs: Original ambient context pattern
//   - WebVella.Erp.Plugins.Project/ProjectPlugin.cs: Plugin initialization flow
//   - WebVella.Erp.Plugins.Project/ProjectPlugin._.cs: Patch-based migrations
//   - WebVella.Erp.Plugins.Project/Services/*.cs: Domain service field structures
//   - src/Services/WebVella.Erp.Service.Project/Database/ProjectDbContext.cs:
//     EF Core DbContext with all entity mappings and seed data
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using Npgsql;
using WebVella.Erp.Service.Project.Database;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Project.Fixtures
{
    // =========================================================================
    // xUnit Collection Definition — enables sharing ProjectDatabaseFixture
    // across all test classes annotated with [Collection("ProjectService")]
    // =========================================================================

    /// <summary>
    /// Defines the "ProjectService" xUnit test collection. All test classes that
    /// use <c>[Collection("ProjectService")]</c> share a single instance of
    /// <see cref="ProjectDatabaseFixture"/>, ensuring that the PostgreSQL
    /// Testcontainer, EF Core migrations, and seed data are initialized once
    /// per test run rather than once per test class.
    ///
    /// This follows the xUnit collection fixture pattern for sharing expensive
    /// resources across multiple test classes while maintaining test isolation
    /// through table-level cleanup methods.
    /// </summary>
    [CollectionDefinition("ProjectService")]
    public class ProjectServiceCollectionDefinition : ICollectionFixture<ProjectDatabaseFixture>
    {
        // This class has no code — it exists solely to apply the
        // [CollectionDefinition] attribute and implement ICollectionFixture<T>.
        // xUnit discovers this class via reflection and uses it to manage
        // the ProjectDatabaseFixture lifecycle.
    }

    // =========================================================================
    // ProjectDatabaseFixture — Primary Test Fixture
    // =========================================================================

    /// <summary>
    /// Primary xUnit class fixture for Project/Task service tests. Manages the
    /// PostgreSQL Testcontainer lifecycle, EF Core migrations, and test data
    /// seeding. Implements <see cref="IAsyncLifetime"/> for async container
    /// startup and teardown.
    ///
    /// <para>
    /// <strong>Container Lifecycle:</strong> The PostgreSQL 16-alpine container
    /// is started in <see cref="InitializeAsync"/> and stopped in
    /// <see cref="DisposeAsync"/>. The container uses <c>WithCleanUp(true)</c>
    /// to ensure cleanup even if the test process crashes.
    /// </para>
    ///
    /// <para>
    /// <strong>Migration:</strong> EF Core migrations from
    /// <see cref="ProjectDbContext"/> are applied against the Testcontainer
    /// database during initialization, establishing the complete Project
    /// service schema (rec_task, rec_timelog, rec_comment, rec_feed_item,
    /// rec_project, rec_task_type, rec_task_status, rec_milestone, and
    /// rel_* join tables).
    /// </para>
    ///
    /// <para>
    /// <strong>Seed Data:</strong> Representative domain data (one project,
    /// one task, one timelog, one comment, one feed item) is inserted using
    /// the <see cref="TestDataBuilders"/> fluent builder classes via raw
    /// Npgsql parameterized INSERT statements.
    /// </para>
    ///
    /// <para>
    /// <strong>Well-Known IDs:</strong> Static <see cref="Guid"/> fields
    /// match the exact values from the monolith's <c>Definitions.cs</c>
    /// SystemIds class, ensuring test data references are consistent with
    /// the production system.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Usage in a test class:
    /// <code>
    /// [Collection("ProjectService")]
    /// public class TaskServiceTests
    /// {
    ///     private readonly ProjectDatabaseFixture _fixture;
    ///     public TaskServiceTests(ProjectDatabaseFixture fixture) => _fixture = fixture;
    ///
    ///     [Fact]
    ///     public async Task GetTask_ReturnsSeededTask()
    ///     {
    ///         var response = await _fixture.HttpClient.GetAsync("/api/v3/en/record/task/list");
    ///         response.EnsureSuccessStatusCode();
    ///     }
    /// }
    /// </code>
    /// </remarks>
    public class ProjectDatabaseFixture : IAsyncLifetime
    {
        // =================================================================
        // Private Fields — Container Infrastructure
        // =================================================================

        /// <summary>
        /// PostgreSQL Testcontainer instance running postgres:16-alpine.
        /// Initialized in the constructor and started in <see cref="InitializeAsync"/>.
        /// The container is configured with <c>WithCleanUp(true)</c> to ensure
        /// automatic cleanup via Docker resource reaper even if the test process
        /// terminates abnormally.
        /// </summary>
        private readonly PostgreSqlContainer _postgresContainer;

        // =================================================================
        // Public Properties — Test Infrastructure Access
        // =================================================================

        /// <summary>
        /// Gets the live PostgreSQL connection string from the running
        /// Testcontainer. This string is passed to
        /// <see cref="ProjectWebApplicationFactory"/> and used directly
        /// by helper methods for raw SQL operations.
        /// </summary>
        /// <remarks>
        /// This property is only valid after <see cref="InitializeAsync"/>
        /// has completed. Accessing it before container startup will throw.
        /// </remarks>
        public string PostgresConnectionString => _postgresContainer.GetConnectionString();

        /// <summary>
        /// Gets the custom <see cref="ProjectWebApplicationFactory"/> that
        /// provides an in-memory ASP.NET Core test server hosting the
        /// Project/Task microservice. The factory overrides production
        /// configuration with the Testcontainer connection string,
        /// in-memory distributed cache, InMemory MassTransit, and
        /// disabled background jobs.
        /// </summary>
        public ProjectWebApplicationFactory Factory { get; private set; }

        /// <summary>
        /// Gets the pre-configured <see cref="HttpClient"/> created from
        /// <see cref="Factory"/> for REST controller integration testing.
        /// All requests through this client are routed to the in-memory
        /// test server without network overhead.
        /// </summary>
        public HttpClient HttpClient { get; private set; }

        // =================================================================
        // Well-Known System IDs — Exact Match to Definitions.cs SystemIds
        // =================================================================
        // These GUID values are copied verbatim from the monolith's
        // WebVella.Erp/Api/Definitions.cs (lines 19-20) and the SharedKernel's
        // WebVella.Erp.SharedKernel/Models/SystemIds.cs to ensure absolute
        // consistency between test data and production system expectations.
        // =================================================================

        /// <summary>
        /// System user ID — the built-in administrative user created during
        /// ERP bootstrap. Matches <c>SystemIds.SystemUserId</c> in both
        /// the monolith (<c>WebVella.Erp.Api.Definitions</c>) and SharedKernel.
        /// Value: <c>10000000-0000-0000-0000-000000000000</c>
        /// </summary>
        public static readonly Guid SystemUserId = new Guid("10000000-0000-0000-0000-000000000000");

        /// <summary>
        /// First user ID — the initial non-system user created during ERP
        /// bootstrap. Matches <c>SystemIds.FirstUserId</c>.
        /// Value: <c>EABD66FD-8DE1-4D79-9674-447EE89921C2</c>
        /// </summary>
        public static readonly Guid FirstUserId = new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");

        /// <summary>
        /// Administrator role ID — the built-in admin role with full system
        /// permissions. Matches <c>SystemIds.AdministratorRoleId</c>.
        /// Value: <c>BDC56420-CAF0-4030-8A0E-D264938E0CDA</c>
        /// </summary>
        public static readonly Guid AdministratorRoleId = new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA");

        /// <summary>
        /// Regular user role ID — the built-in role for standard users.
        /// Matches <c>SystemIds.RegularRoleId</c>.
        /// Value: <c>F16EC6DB-626D-4C27-8DE0-3E7CE542C55F</c>
        /// </summary>
        public static readonly Guid RegularRoleId = new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F");

        /// <summary>
        /// Guest role ID — the built-in role for unauthenticated/guest access.
        /// Matches <c>SystemIds.GuestRoleId</c>.
        /// Value: <c>987148B1-AFA8-4B33-8616-55861E5FD065</c>
        /// </summary>
        public static readonly Guid GuestRoleId = new Guid("987148B1-AFA8-4B33-8616-55861E5FD065");

        // =================================================================
        // Test-Specific GUIDs — Pre-seeded Domain Data Identifiers
        // =================================================================
        // These GUIDs are generated once per test run (static readonly) and
        // used consistently across all test classes in the ProjectService
        // collection. They identify the seed data records created in
        // SeedTestDataAsync().
        // =================================================================

        /// <summary>
        /// Pre-seeded test project record ID. Used to reference the seeded
        /// project across all test classes without re-querying the database.
        /// </summary>
        public static readonly Guid TestProjectId = Guid.NewGuid();

        /// <summary>
        /// Pre-seeded test task record ID. Used to reference the seeded
        /// task across all test classes without re-querying the database.
        /// </summary>
        public static readonly Guid TestTaskId = Guid.NewGuid();

        /// <summary>
        /// Pre-seeded test timelog record ID. Used to reference the seeded
        /// timelog across all test classes.
        /// </summary>
        public static readonly Guid TestTimelogId = Guid.NewGuid();

        /// <summary>
        /// Pre-seeded test comment record ID. Used to reference the seeded
        /// comment across all test classes.
        /// </summary>
        public static readonly Guid TestCommentId = Guid.NewGuid();

        /// <summary>
        /// Pre-seeded test feed item record ID. Used to reference the seeded
        /// feed item across all test classes.
        /// </summary>
        public static readonly Guid TestFeedItemId = Guid.NewGuid();

        // =================================================================
        // Constructor — Testcontainer Initialization
        // =================================================================

        /// <summary>
        /// Initializes a new instance of <see cref="ProjectDatabaseFixture"/>.
        /// Configures the PostgreSQL Testcontainer with:
        /// <list type="bullet">
        ///   <item><c>postgres:16-alpine</c> image (matching AAP docker-compose spec)</item>
        ///   <item><c>erp_project_test</c> database name (database-per-service pattern)</item>
        ///   <item><c>test/test</c> credentials (test-only, non-production)</item>
        ///   <item><c>WithCleanUp(true)</c> for automatic Docker resource cleanup</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// The container is NOT started here — it is started asynchronously
        /// in <see cref="InitializeAsync"/> when xUnit invokes the
        /// <see cref="IAsyncLifetime"/> interface.
        /// </remarks>
        public ProjectDatabaseFixture()
        {
            _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("erp_project_test")
                .WithUsername("test")
                .WithPassword("test")
                .WithCleanUp(true)
                .Build();
        }

        // =================================================================
        // IAsyncLifetime.InitializeAsync — Container Startup & Seeding
        // =================================================================

        /// <summary>
        /// Performs async initialization of the test infrastructure. Called
        /// once by xUnit before any tests in the "ProjectService" collection
        /// execute.
        ///
        /// <para>Initialization sequence:</para>
        /// <list type="number">
        ///   <item>Start PostgreSQL Testcontainer</item>
        ///   <item>Create <see cref="ProjectWebApplicationFactory"/> with the
        ///         live connection string</item>
        ///   <item>Run EF Core migrations against the Testcontainer to create
        ///         the complete Project service schema</item>
        ///   <item>Seed test data using builder helpers</item>
        ///   <item>Create the shared <see cref="HttpClient"/></item>
        /// </list>
        /// </summary>
        /// <returns>A task representing the async initialization operation.</returns>
        public async Task InitializeAsync()
        {
            // Step 1: Start the PostgreSQL container.
            // This pulls postgres:16-alpine (if not cached) and starts a fresh
            // PostgreSQL instance with the configured database, user, and password.
            await _postgresContainer.StartAsync();

            // Step 2: Create the WebApplicationFactory with the live connection string.
            // This configures the Project service test host to use the Testcontainer
            // database instead of a production PostgreSQL instance.
            Factory = new ProjectWebApplicationFactory(PostgresConnectionString);

            // Step 3: Apply EF Core migrations to establish the complete schema.
            // This creates all tables (rec_task, rec_timelog, rec_comment, rec_feed_item,
            // rec_project, rec_task_type, rec_task_status, rec_milestone, rel_* join tables)
            // and seeds reference data (task types, task statuses) defined in
            // ProjectDbContext.OnModelCreating().
            using (var scope = Factory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ProjectDbContext>();
                await dbContext.Database.MigrateAsync();
            }

            // Step 4: Seed representative domain data for tests.
            // Uses the TestDataBuilders to create one project, task, timelog,
            // comment, and feed item with well-known IDs.
            await SeedTestDataAsync();

            // Step 5: Create the shared HTTP client for REST integration tests.
            // This client routes requests to the in-memory test server without
            // network overhead.
            HttpClient = Factory.CreateClient();
        }

        // =================================================================
        // IAsyncLifetime.DisposeAsync — Container Teardown & Cleanup
        // =================================================================

        /// <summary>
        /// Performs async cleanup of the test infrastructure. Called once by
        /// xUnit after all tests in the "ProjectService" collection have
        /// completed.
        ///
        /// <para>Cleanup sequence:</para>
        /// <list type="number">
        ///   <item>Dispose <see cref="HttpClient"/></item>
        ///   <item>Dispose <see cref="Factory"/> (stops the in-memory test server)</item>
        ///   <item>Dispose the PostgreSQL Testcontainer (stops and removes the
        ///         Docker container)</item>
        /// </list>
        /// </summary>
        /// <returns>A task representing the async disposal operation.</returns>
        public async Task DisposeAsync()
        {
            // Dispose the HTTP client first — it depends on the factory.
            if (HttpClient != null)
            {
                HttpClient.Dispose();
                HttpClient = null;
            }

            // Dispose the factory — stops the in-memory test server and
            // releases all DI-scoped services.
            if (Factory != null)
            {
                Factory.Dispose();
                Factory = null;
            }

            // Dispose the PostgreSQL container — stops the Docker container
            // and removes it. WithCleanUp(true) ensures the Docker resource
            // reaper also cleans up if this code path is not reached.
            await _postgresContainer.DisposeAsync();
        }

        // =================================================================
        // SeedTestDataAsync — Test Data Population
        // =================================================================

        /// <summary>
        /// Seeds the test database with representative Project domain data
        /// using the <see cref="TestDataBuilders"/> fluent builder classes.
        /// Each record is inserted via raw Npgsql parameterized SQL using
        /// <see cref="InsertRecordAsync"/>.
        ///
        /// <para>The following records are created:</para>
        /// <list type="bullet">
        ///   <item>One project: "Test Project" (abbr: "TP", owner: FirstUserId)</item>
        ///   <item>One task: "Test Task Subject" (number: 1, priority: "1")</item>
        ///   <item>One timelog: 60 minutes, billable, linked to task and project</item>
        ///   <item>One comment: "Test comment body", linked to task and project</item>
        ///   <item>One feed item: "Test feed item", type "task"</item>
        /// </list>
        ///
        /// <para>
        /// Seeding is idempotent — the INSERT statements use
        /// <c>ON CONFLICT (id) DO NOTHING</c> so re-running this method
        /// will not fail or duplicate data.
        /// </para>
        /// </summary>
        /// <returns>A task representing the async seeding operation.</returns>
        private async Task SeedTestDataAsync()
        {
            // -----------------------------------------------------------------
            // 7.1: Seed a test project
            // Field structure matches ProjectService.Get() and TaskService
            // references to projectRecord["abbr"], projectRecord["owner_id"].
            // -----------------------------------------------------------------
            var project = new ProjectRecordBuilder()
                .WithId(TestProjectId)
                .WithName("Test Project")
                .WithAbbr("TP")
                .WithOwnerId(FirstUserId)
                .Build();
            await InsertRecordAsync("project", project);

            // -----------------------------------------------------------------
            // 7.2: Seed a test task
            // Field structure matches TaskService.SetCalculationFields(),
            // GetTask(), GetPageHookLogic(), StartTaskTimelog(), and
            // PreCreateRecordPageHookLogic().
            // Note: status_id is null (no default status seeded in this fixture;
            // EF Core seed data provides task_status records if migrations include
            // them). Priority "1" matches the monolith's default.
            // -----------------------------------------------------------------
            var task = new TaskRecordBuilder()
                .WithId(TestTaskId)
                .WithSubject("Test Task Subject")
                .WithNumber(1)
                .WithStatusId(null)
                .WithPriority("1")
                .WithOwnerId(FirstUserId)
                .WithStartTime(DateTime.UtcNow.Date)
                .WithEndTime(DateTime.UtcNow.Date.AddDays(7))
                .Build();
            await InsertRecordAsync("task", task);

            // -----------------------------------------------------------------
            // 7.3: Seed a test timelog
            // Field structure matches TimeLogService.Create() (lines 39-48).
            // l_scope and l_related_records are JSON-serialized by the builder.
            // -----------------------------------------------------------------
            var timelog = new TimelogRecordBuilder()
                .WithId(TestTimelogId)
                .WithMinutes(60)
                .WithIsBillable(true)
                .WithLoggedOn(DateTime.UtcNow)
                .WithScope(new List<string> { "projects" })
                .WithRelatedRecords(new List<Guid> { TestTaskId, TestProjectId })
                .Build();
            await InsertRecordAsync("timelog", timelog);

            // -----------------------------------------------------------------
            // 7.4: Seed a test comment
            // Field structure matches CommentService.Create() (lines 32-38).
            // l_scope and l_related_records are JSON-serialized by the builder.
            // -----------------------------------------------------------------
            var comment = new CommentRecordBuilder()
                .WithId(TestCommentId)
                .WithBody("Test comment body")
                .WithScope(new List<string> { "projects" })
                .WithRelatedRecords(new List<Guid> { TestTaskId, TestProjectId })
                .Build();
            await InsertRecordAsync("comment", comment);

            // -----------------------------------------------------------------
            // 7.5: Seed a test feed item
            // Field structure matches FeedItemService.Create() (lines 35-43).
            // l_related_records and l_scope are JSON-serialized by the builder.
            // -----------------------------------------------------------------
            var feedItem = new FeedItemRecordBuilder()
                .WithId(TestFeedItemId)
                .WithSubject("Test feed item")
                .WithBody("Test feed body")
                .WithType("task")
                .Build();
            await InsertRecordAsync("feed_item", feedItem);
        }

        // =================================================================
        // InsertRecordAsync — Raw SQL Record Insertion
        // =================================================================

        /// <summary>
        /// Inserts a single <see cref="EntityRecord"/> into the test database
        /// using a parameterized Npgsql INSERT statement. The record's dynamic
        /// properties (via <see cref="EntityRecord.Properties"/>) are iterated
        /// to build column names and parameter placeholders.
        ///
        /// <para>
        /// The INSERT uses <c>ON CONFLICT (id) DO NOTHING</c> to ensure
        /// idempotent seeding — calling this method multiple times with
        /// the same record ID will not produce duplicate key errors.
        /// </para>
        ///
        /// <para>
        /// Type handling for Npgsql parameters:
        /// <list type="bullet">
        ///   <item><see cref="Guid"/>: Stored as <c>uuid</c></item>
        ///   <item><see cref="DateTime"/>: Stored as <c>timestamptz</c></item>
        ///   <item><see cref="bool"/>: Stored as <c>boolean</c></item>
        ///   <item><see cref="decimal"/>/<see cref="int"/>: Stored as <c>numeric</c></item>
        ///   <item><see cref="string"/>: Stored as <c>text</c></item>
        ///   <item><c>null</c>: Stored as <see cref="DBNull.Value"/></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="entityName">
        /// The entity name (without the <c>rec_</c> prefix). The table name
        /// is constructed as <c>rec_{entityName}</c> matching the monolith's
        /// dynamic entity table naming convention.
        /// </param>
        /// <param name="record">
        /// An <see cref="EntityRecord"/> instance populated by a TestDataBuilder.
        /// Its <see cref="EntityRecord.Properties"/> dictionary provides the
        /// column names and values for the INSERT statement.
        /// </param>
        /// <returns>A task representing the async insert operation.</returns>
        private async Task InsertRecordAsync(string entityName, EntityRecord record)
        {
            string tableName = "rec_" + entityName;

            // Extract column names and values from the EntityRecord's Properties dictionary.
            // The Properties field is a PropertyBag (Dictionary<string, object>) inherited
            // from the Expando base class.
            var columns = new List<string>();
            var paramNames = new List<string>();
            var parameters = new List<NpgsqlParameter>();
            int paramIndex = 0;

            foreach (var key in record.Properties.Keys)
            {
                string paramName = "@p" + paramIndex;
                columns.Add("\"" + key + "\"");
                paramNames.Add(paramName);

                object value = record.Properties[key];

                // Convert null values to DBNull.Value for Npgsql compatibility.
                // All other types are passed through directly — Npgsql handles
                // Guid, DateTime, bool, decimal, int, and string natively.
                var parameter = new NpgsqlParameter(paramName, value ?? DBNull.Value);
                parameters.Add(parameter);
                paramIndex++;
            }

            // Build the INSERT SQL with ON CONFLICT for idempotent seeding.
            // Table and column names are constructed from trusted internal values
            // (entity names from TestDataBuilders), not from user input.
            string sql = string.Format(
                "INSERT INTO {0} ({1}) VALUES ({2}) ON CONFLICT (id) DO NOTHING",
                tableName,
                string.Join(", ", columns),
                string.Join(", ", paramNames));

            using var connection = new NpgsqlConnection(PostgresConnectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddRange(parameters.ToArray());
            await command.ExecuteNonQueryAsync();
        }

        // =================================================================
        // Public Database Helper Methods
        // =================================================================

        /// <summary>
        /// Creates and opens a new <see cref="NpgsqlConnection"/> to the test
        /// database. The caller is responsible for disposing the connection.
        ///
        /// <para>
        /// This method is useful for test classes that need to perform direct
        /// SQL operations for setup, verification, or assertion purposes
        /// outside the EF Core DbContext.
        /// </para>
        /// </summary>
        /// <returns>
        /// A task that resolves to an open <see cref="NpgsqlConnection"/>
        /// connected to the test database.
        /// </returns>
        /// <example>
        /// <code>
        /// using var connection = await _fixture.CreateConnectionAsync();
        /// using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM rec_task", connection);
        /// var count = (long)await cmd.ExecuteScalarAsync();
        /// Assert.True(count > 0);
        /// </code>
        /// </example>
        public async Task<NpgsqlConnection> CreateConnectionAsync()
        {
            var connection = new NpgsqlConnection(PostgresConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        /// <summary>
        /// Executes an arbitrary SQL statement against the test database with
        /// optional parameterized inputs. Useful for custom test setup,
        /// data manipulation, or assertion queries.
        ///
        /// <para>
        /// The method opens a new connection, executes the SQL, and closes
        /// the connection. For multiple operations, prefer
        /// <see cref="CreateConnectionAsync"/> to reuse a single connection.
        /// </para>
        /// </summary>
        /// <param name="sql">
        /// The SQL statement to execute. May contain <c>@paramName</c>
        /// placeholders matching the provided <paramref name="parameters"/>.
        /// </param>
        /// <param name="parameters">
        /// Optional array of <see cref="NpgsqlParameter"/> instances for
        /// parameterized queries. Pass an empty array or omit for
        /// non-parameterized SQL.
        /// </param>
        /// <returns>A task representing the async execution operation.</returns>
        /// <example>
        /// <code>
        /// await _fixture.ExecuteSqlAsync(
        ///     "UPDATE rec_task SET subject = @subject WHERE id = @id",
        ///     new NpgsqlParameter("@subject", "Updated Subject"),
        ///     new NpgsqlParameter("@id", ProjectDatabaseFixture.TestTaskId));
        /// </code>
        /// </example>
        public async Task ExecuteSqlAsync(string sql, params NpgsqlParameter[] parameters)
        {
            using var connection = new NpgsqlConnection(PostgresConnectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(sql, connection);
            if (parameters != null && parameters.Length > 0)
            {
                command.Parameters.AddRange(parameters);
            }
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Truncates all rows from the specified table using
        /// <c>TRUNCATE TABLE ... CASCADE</c>. This provides test isolation
        /// by allowing individual test classes to clean specific tables
        /// before or after their tests without affecting the container
        /// or schema.
        ///
        /// <para>
        /// The <c>CASCADE</c> option ensures that rows in related tables
        /// (via foreign key constraints) are also removed, preventing
        /// constraint violation errors during cleanup.
        /// </para>
        ///
        /// <para>
        /// <strong>Important:</strong> The <paramref name="tableName"/> must
        /// include the full table name with prefix (e.g., <c>"rec_task"</c>,
        /// <c>"rel_project_nn_task"</c>). This method sanitizes the table
        /// name by removing any characters that are not alphanumeric or
        /// underscore to prevent SQL injection.
        /// </para>
        /// </summary>
        /// <param name="tableName">
        /// The full table name to truncate (e.g., <c>"rec_task"</c>,
        /// <c>"rec_timelog"</c>, <c>"rel_project_nn_task"</c>).
        /// </param>
        /// <returns>A task representing the async truncation operation.</returns>
        /// <example>
        /// <code>
        /// await _fixture.CleanTableAsync("rec_task");
        /// await _fixture.CleanTableAsync("rel_project_nn_task");
        /// </code>
        /// </example>
        public async Task CleanTableAsync(string tableName)
        {
            // Sanitize the table name to prevent SQL injection.
            // Only allow alphanumeric characters and underscores.
            var sanitized = new string(tableName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

            if (string.IsNullOrEmpty(sanitized))
            {
                throw new ArgumentException(
                    "Table name must contain at least one alphanumeric character.",
                    nameof(tableName));
            }

            string sql = string.Format("TRUNCATE TABLE \"{0}\" CASCADE", sanitized);

            using var connection = new NpgsqlConnection(PostgresConnectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
