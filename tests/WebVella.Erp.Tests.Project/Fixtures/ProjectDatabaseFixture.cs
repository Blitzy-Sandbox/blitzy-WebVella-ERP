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

            // Step 2: Apply EF Core migrations DIRECTLY against the Testcontainer.
            // CRITICAL: We must run migrations using a standalone DbContext with the
            // Testcontainer connection string rather than through the factory's DI
            // container. This is because WebApplicationFactory's ConfigureAppConfiguration
            // overrides are applied after Program.cs captures the connection string
            // variable (minimal hosting model timing issue), so the factory-resolved
            // ProjectDbContext may connect to the wrong database. By creating the
            // DbContext directly with the Testcontainer connection string, we ensure
            // migrations run against the correct database. The ConfigureWarnings
            // suppression for PendingModelChangesWarning is required because the
            // initial migration was generated from the monolith schema and minor
            // model snapshot drift is expected during decomposition.
            var migrationOptions = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseNpgsql(PostgresConnectionString)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;

            using (var migrationContext = new ProjectDbContext(migrationOptions))
            {
                await migrationContext.Database.MigrateAsync();
            }

            // Step 2b: Seed entity metadata system tables.
            // The EQL engine and RecordManager depend on entity metadata stored
            // in the "entities" and "entity_relations" tables. These are Core
            // service infrastructure tables not created by the Project EF Core
            // migration. Without them, all EQL queries fail with
            // "One or more Eql errors occurred" because the engine cannot resolve
            // entity names to table definitions.
            await SeedEntityMetadataAsync();

            // Step 3: Create the WebApplicationFactory with the live connection string.
            // This configures the Project service test host to use the Testcontainer
            // database instead of a production PostgreSQL instance.
            Factory = new ProjectWebApplicationFactory(PostgresConnectionString);

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
            // status_id and type_id are NOT NULL in the EF Core migration, so
            // we must use valid seeded IDs from the migration seed data.
            // "Not Started" status: f3fdd750-0c16-4215-93b3-5373bd528d1f
            // "General" type: da9bf72d-3655-4c51-9f99-047ef9297bf2
            // Priority "1" matches the monolith's default.
            // -----------------------------------------------------------------
            var notStartedStatusId = new Guid("f3fdd750-0c16-4215-93b3-5373bd528d1f");
            var generalTypeId = new Guid("da9bf72d-3655-4c51-9f99-047ef9297bf2");
            var task = new TaskRecordBuilder()
                .WithId(TestTaskId)
                .WithSubject("Test Task Subject")
                .WithNumber(1)
                .WithStatusId(notStartedStatusId)
                .WithTypeId(generalTypeId)
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

            // Seed the rel_project_nn_task join table row linking project ↔ task
            using var relConn = new NpgsqlConnection(PostgresConnectionString);
            await relConn.OpenAsync();
            using var relCmd = new NpgsqlCommand(
                "INSERT INTO rel_project_nn_task (origin_id, target_id) VALUES (@oid, @tid) ON CONFLICT DO NOTHING",
                relConn);
            relCmd.Parameters.AddWithValue("oid", TestProjectId);
            relCmd.Parameters.AddWithValue("tid", TestTaskId);
            await relCmd.ExecuteNonQueryAsync();
        }

        // =================================================================
        // SeedEntityMetadataAsync — Entity Metadata for EQL Engine
        // =================================================================

        /// <summary>
        /// Seeds the Core infrastructure system tables (entities, entity_relations)
        /// required by the EQL engine and RecordManager. These tables are normally
        /// managed by the Core service but must exist in each service's database
        /// for the shared EntityManager/RecordManager to resolve entity metadata.
        /// </summary>
        private async Task SeedEntityMetadataAsync()
        {
            using var connection = new NpgsqlConnection(PostgresConnectionString);
            await connection.OpenAsync();

            // Create system tables
            using (var cmd = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS public.entities (
                    id uuid NOT NULL,
                    json json NOT NULL,
                    CONSTRAINT entities_pkey PRIMARY KEY (id)
                );
                CREATE TABLE IF NOT EXISTS public.entity_relations (
                    id uuid NOT NULL,
                    json json NOT NULL,
                    CONSTRAINT entity_relations_pkey PRIMARY KEY (id)
                );", connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            var adminRoleId = AdministratorRoleId.ToString();
            var regularRoleId = RegularRoleId.ToString();
            var guestRoleId = GuestRoleId.ToString();
            var asmName = "WebVella.Erp.SharedKernel";
            var ns = "WebVella.Erp.SharedKernel.Database";

            // Helper: build a minimal DbEntity JSON blob.
            // The JSON must use Newtonsoft.Json TypeNameHandling.Auto format
            // with $type discriminators for polymorphic DbBaseField subclasses.
            string BuildEntityJson(Guid entityId, string name, string label, List<(string fieldType, Guid fieldId, string fieldName, string fieldLabel, bool required)> fields)
            {
                var fieldJsons = new List<string>();
                foreach (var f in fields)
                {
                    // Each field needs a $type discriminator for Newtonsoft.Json deserialization
                    var fieldJson = $@"{{
                        ""$type"":""{ns}.{f.fieldType}, {asmName}"",
                        ""id"":""{f.fieldId}"",
                        ""name"":""{f.fieldName}"",
                        ""label"":""{f.fieldLabel}"",
                        ""placeholder_text"":"""",
                        ""description"":"""",
                        ""help_text"":"""",
                        ""required"":{(f.required ? "true" : "false")},
                        ""unique"":false,
                        ""searchable"":false,
                        ""auditable"":false,
                        ""system"":false,
                        ""permissions"":{{""can_read"":[],""can_update"":[]}}
                    }}";
                    fieldJsons.Add(fieldJson);
                }

                return $@"{{
                    ""id"":""{entityId}"",
                    ""name"":""{name}"",
                    ""label"":""{label}"",
                    ""label_plural"":""{label}s"",
                    ""system"":false,
                    ""icon_name"":"""",
                    ""color"":"""",
                    ""record_permissions"":{{
                        ""can_read"":[""{adminRoleId}"",""{regularRoleId}"",""{guestRoleId}""],
                        ""can_create"":[""{adminRoleId}"",""{regularRoleId}""],
                        ""can_update"":[""{adminRoleId}"",""{regularRoleId}""],
                        ""can_delete"":[""{adminRoleId}""]
                    }},
                    ""fields"":[{string.Join(",", fieldJsons)}]
                }}";
            }

            // Deterministic entity IDs matching the migration comments
            var projectEntityId = new Guid("ab1fb3e4-508c-48a3-b576-bfdd395f69d5");
            var taskEntityId = new Guid("9386226e-381e-4522-b27b-fb5514d77902");
            var taskStatusEntityId = new Guid("541ccc20-e86b-4b78-8570-0745b4a17497");
            var taskTypeEntityId = new Guid("12244aea-878f-4a33-b205-26e53f9ed25b");
            var timelogEntityId = new Guid("750153c5-1df9-408f-b856-727078a525bc");
            var commentEntityId = new Guid("b1d218d5-68c2-41a5-bea5-1b4a78cbf91d");
            var feedItemEntityId = new Guid("2ac9a907-1bdf-4700-8874-6e06a8d22c97");

            // Fixed field IDs for id fields used in relation metadata
            var projectIdFieldId = new Guid("e1a1a5c0-0001-0000-0000-000000000001");
            var taskIdFieldId = new Guid("e1a1a5c0-0002-0000-0000-000000000001");

            // ----- project entity metadata -----
            var projectFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", projectIdFieldId, "id", "Id", true),
                ("DbTextField", Guid.NewGuid(), "name", "Name", true),
                ("DbTextField", Guid.NewGuid(), "abbr", "Abbreviation", true),
                ("DbTextField", Guid.NewGuid(), "description", "Description", false),
                ("DbGuidField", Guid.NewGuid(), "owner_id", "Owner", false),
                ("DbTextField", Guid.NewGuid(), "color", "Color", false),
                ("DbTextField", Guid.NewGuid(), "icon", "Icon", false),
                ("DbDateTimeField", Guid.NewGuid(), "start_date", "Start Date", false),
                ("DbDateTimeField", Guid.NewGuid(), "end_date", "End Date", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_billable", "Is Billable", false),
                ("DbTextField", Guid.NewGuid(), "scope_key", "Scope Key", false),
                ("DbNumberField", Guid.NewGuid(), "x_billable_hours", "Billable Hours", false),
                ("DbNumberField", Guid.NewGuid(), "x_nonbillable_hours", "Non-billable Hours", false),
                ("DbNumberField", Guid.NewGuid(), "x_tasks_not_started", "Tasks Not Started", false),
                ("DbNumberField", Guid.NewGuid(), "x_tasks_in_progress", "Tasks In Progress", false),
                ("DbNumberField", Guid.NewGuid(), "x_tasks_completed", "Tasks Completed", false),
                ("DbNumberField", Guid.NewGuid(), "x_overdue_tasks", "Overdue Tasks", false),
                ("DbNumberField", Guid.NewGuid(), "x_milestones_on_track", "Milestones On Track", false),
                ("DbNumberField", Guid.NewGuid(), "x_milestones_missed", "Milestones Missed", false),
                ("DbNumberField", Guid.NewGuid(), "x_budget", "Budget", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
                ("DbTextField", Guid.NewGuid(), "x_search", "Search Index", false),
                ("DbDateTimeField", Guid.NewGuid(), "created_on", "Created On", false),
                ("DbGuidField", Guid.NewGuid(), "created_by", "Created By", false),
            };
            await InsertEntityMetadataAsync(connection, projectEntityId,
                BuildEntityJson(projectEntityId, "project", "Project", projectFields));

            // ----- task entity metadata -----
            var taskFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", taskIdFieldId, "id", "Id", true),
                ("DbTextField", Guid.NewGuid(), "subject", "Subject", true),
                ("DbTextField", Guid.NewGuid(), "body", "Body", false),
                ("DbGuidField", Guid.NewGuid(), "owner_id", "Owner", false),
                ("DbDateTimeField", Guid.NewGuid(), "start_time", "Start Time", false),
                ("DbDateTimeField", Guid.NewGuid(), "end_time", "End Time", false),
                ("DbDateTimeField", Guid.NewGuid(), "created_on", "Created On", false),
                ("DbGuidField", Guid.NewGuid(), "created_by", "Created By", true),
                ("DbDateTimeField", Guid.NewGuid(), "completed_on", "Completed On", false),
                ("DbNumberField", Guid.NewGuid(), "number", "Number", true),
                ("DbGuidField", Guid.NewGuid(), "parent_id", "Parent Task", false),
                ("DbGuidField", Guid.NewGuid(), "status_id", "Status", true),
                ("DbGuidField", Guid.NewGuid(), "type_id", "Type", true),
                ("DbTextField", Guid.NewGuid(), "priority", "Priority", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
                ("DbTextField", Guid.NewGuid(), "l_related_records", "Related Records", false),
                ("DbTextField", Guid.NewGuid(), "x_search", "Search Index", false),
                ("DbGuidField", Guid.NewGuid(), "recurrence_id", "Recurrence", false),
                ("DbTextField", Guid.NewGuid(), "recurrence_template", "Recurrence Template", false),
                ("DbTextField", Guid.NewGuid(), "key", "Key", true),
                ("DbNumberField", Guid.NewGuid(), "estimated_minutes", "Estimated Minutes", false),
                ("DbNumberField", Guid.NewGuid(), "x_billable_minutes", "Billable Minutes", false),
                ("DbNumberField", Guid.NewGuid(), "x_nonbillable_minutes", "Non-billable Minutes", false),
                ("DbDateTimeField", Guid.NewGuid(), "timelog_started_on", "Timelog Started On", false),
                ("DbCheckboxField", Guid.NewGuid(), "reserve_time", "Reserve Time", false),
            };
            await InsertEntityMetadataAsync(connection, taskEntityId,
                BuildEntityJson(taskEntityId, "task", "Task", taskFields));

            // ----- task_status entity metadata -----
            var taskStatusFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", Guid.NewGuid(), "id", "Id", true),
                ("DbTextField", Guid.NewGuid(), "label", "Label", true),
                ("DbNumberField", Guid.NewGuid(), "sort_index", "Sort Index", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_closed", "Is Closed", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_default", "Is Default", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_enabled", "Is Enabled", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_system", "Is System", false),
                ("DbTextField", Guid.NewGuid(), "icon_class", "Icon Class", false),
                ("DbTextField", Guid.NewGuid(), "color", "Color", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
            };
            await InsertEntityMetadataAsync(connection, taskStatusEntityId,
                BuildEntityJson(taskStatusEntityId, "task_status", "Task Status", taskStatusFields));

            // ----- task_type entity metadata -----
            var taskTypeFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", Guid.NewGuid(), "id", "Id", true),
                ("DbTextField", Guid.NewGuid(), "label", "Label", true),
                ("DbNumberField", Guid.NewGuid(), "sort_index", "Sort Index", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_default", "Is Default", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_enabled", "Is Enabled", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_system", "Is System", false),
                ("DbTextField", Guid.NewGuid(), "icon_class", "Icon Class", false),
                ("DbTextField", Guid.NewGuid(), "color", "Color", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
            };
            await InsertEntityMetadataAsync(connection, taskTypeEntityId,
                BuildEntityJson(taskTypeEntityId, "task_type", "Task Type", taskTypeFields));

            // ----- timelog entity metadata -----
            var timelogFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", Guid.NewGuid(), "id", "Id", true),
                ("DbNumberField", Guid.NewGuid(), "minutes", "Minutes", false),
                ("DbCheckboxField", Guid.NewGuid(), "is_billable", "Is Billable", false),
                ("DbTextField", Guid.NewGuid(), "body", "Body", false),
                ("DbDateTimeField", Guid.NewGuid(), "logged_on", "Logged On", false),
                ("DbDateTimeField", Guid.NewGuid(), "created_on", "Created On", false),
                ("DbGuidField", Guid.NewGuid(), "created_by", "Created By", false),
                ("DbTextField", Guid.NewGuid(), "l_related_records", "Related Records", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
            };
            await InsertEntityMetadataAsync(connection, timelogEntityId,
                BuildEntityJson(timelogEntityId, "timelog", "Timelog", timelogFields));

            // ----- comment entity metadata -----
            var commentFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", Guid.NewGuid(), "id", "Id", true),
                ("DbTextField", Guid.NewGuid(), "body", "Body", false),
                ("DbGuidField", Guid.NewGuid(), "parent_id", "Parent Comment", false),
                ("DbDateTimeField", Guid.NewGuid(), "created_on", "Created On", false),
                ("DbGuidField", Guid.NewGuid(), "created_by", "Created By", false),
                ("DbTextField", Guid.NewGuid(), "l_related_records", "Related Records", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
            };
            await InsertEntityMetadataAsync(connection, commentEntityId,
                BuildEntityJson(commentEntityId, "comment", "Comment", commentFields));

            // ----- feed_item entity metadata -----
            var feedItemFields = new List<(string, Guid, string, string, bool)>
            {
                ("DbGuidField", Guid.NewGuid(), "id", "Id", true),
                ("DbTextField", Guid.NewGuid(), "subject", "Subject", false),
                ("DbTextField", Guid.NewGuid(), "body", "Body", false),
                ("DbTextField", Guid.NewGuid(), "type", "Type", false),
                ("DbDateTimeField", Guid.NewGuid(), "created_on", "Created On", false),
                ("DbGuidField", Guid.NewGuid(), "created_by", "Created By", false),
                ("DbTextField", Guid.NewGuid(), "l_related_records", "Related Records", false),
                ("DbTextField", Guid.NewGuid(), "l_scope", "Scope", false),
            };
            await InsertEntityMetadataAsync(connection, feedItemEntityId,
                BuildEntityJson(feedItemEntityId, "feed_item", "Feed Item", feedItemFields));

            // ----- Seed relation metadata for project_nn_task (M:N) -----
            // Required by EQL queries that use $project_nn_task.xxx syntax
            var projectNnTaskRelationId = new Guid("d7a285c0-0001-0000-0000-000000000001");
            var relationJson = $@"{{
                ""id"":""{projectNnTaskRelationId}"",
                ""name"":""project_nn_task"",
                ""label"":""Project - Task"",
                ""description"":""M:N relation between project and task entities"",
                ""system"":false,
                ""relation_type"":3,
                ""origin_entity_id"":""{projectEntityId}"",
                ""origin_field_id"":""{projectIdFieldId}"",
                ""target_entity_id"":""{taskEntityId}"",
                ""target_field_id"":""{taskIdFieldId}""
            }}";
            using (var relCmd = new NpgsqlCommand(
                "INSERT INTO entity_relations (id, json) VALUES (@id, @json::json) ON CONFLICT (id) DO UPDATE SET json = @json::json",
                connection))
            {
                relCmd.Parameters.AddWithValue("id", projectNnTaskRelationId);
                relCmd.Parameters.AddWithValue("json", relationJson);
                await relCmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Inserts a single entity metadata JSON row into the entities table.
        /// </summary>
        private static async Task InsertEntityMetadataAsync(NpgsqlConnection connection, Guid entityId, string json)
        {
            using var cmd = new NpgsqlCommand(
                "INSERT INTO entities (id, json) VALUES (@id, @json::json) ON CONFLICT (id) DO UPDATE SET json = @json::json",
                connection);
            cmd.Parameters.AddWithValue("id", entityId);
            cmd.Parameters.AddWithValue("json", json);
            await cmd.ExecuteNonQueryAsync();
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
