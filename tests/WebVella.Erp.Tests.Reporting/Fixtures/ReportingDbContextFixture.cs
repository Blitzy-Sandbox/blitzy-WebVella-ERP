using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.Tests.Reporting.Fixtures;

namespace WebVella.Erp.Tests.Reporting.Fixtures
{
    #region xUnit Collection Definition

    /// <summary>
    /// Defines the "ReportingService" xUnit test collection that shares a single
    /// <see cref="ReportingDbContextFixture"/> instance across all test classes
    /// annotated with <c>[Collection("ReportingService")]</c>.
    ///
    /// This pattern ensures the expensive PostgreSQL Testcontainer startup/teardown
    /// happens only once per test collection, not per test class, significantly
    /// reducing total test execution time for the Reporting service test suite.
    ///
    /// <para><b>Usage in test classes:</b></para>
    /// <code>
    /// [Collection("ReportingService")]
    /// public class ReportAggregationServiceTests
    /// {
    ///     private readonly ReportingDbContextFixture _fixture;
    ///     public ReportAggregationServiceTests(ReportingDbContextFixture fixture) => _fixture = fixture;
    /// }
    /// </code>
    /// </summary>
    [CollectionDefinition("ReportingService")]
    public class ReportingServiceCollectionDefinition : ICollectionFixture<ReportingDbContextFixture>
    {
    }

    #endregion

    #region ReportingDbContextFixture

    /// <summary>
    /// Shared xUnit class fixture providing pre-configured <see cref="ReportingDbContext"/>
    /// instances for all Reporting service test categories (Domain, Controllers, Events, Database).
    ///
    /// <para><b>Test Infrastructure Responsibilities:</b></para>
    /// <list type="bullet">
    ///   <item>Manages a PostgreSQL Testcontainer (postgres:16-alpine) for integration tests</item>
    ///   <item>Provides EF Core InMemory database contexts for fast isolated unit tests</item>
    ///   <item>Creates a <see cref="ReportingWebApplicationFactory"/> for controller integration tests</item>
    ///   <item>Seeds test data exercising ALL business rules from the monolith's
    ///         ReportService.GetTimelogData() method via <see cref="TestDataScenarios"/></item>
    ///   <item>Exposes well-known test IDs matching monolith SystemIds (Definitions.cs)</item>
    /// </list>
    ///
    /// <para><b>Business Rules Covered by Seed Data:</b></para>
    /// <list type="number">
    ///   <item>Month range filtering — Timelogs 1-5 in Jan 2024, Timelog 6 in Feb 2024</item>
    ///   <item>Billable/non-billable aggregation — Task 1: billable=120, non-billable=60 min</item>
    ///   <item>Multi-project task splitting — Task 2 under both Project Alpha and Project Beta</item>
    ///   <item>Account filtering — Filter by AccountAlphaId returns only Alpha project timelogs</item>
    ///   <item>Missing account error — Project Gamma has null AccountId triggering exception</item>
    ///   <item>Result shape — 7 fields: task_id, project_id, task_subject, project_name, task_type,
    ///         billable_minutes, non_billable_minutes</item>
    /// </list>
    ///
    /// <para><b>Sibling Patterns:</b></para>
    /// Follows the same fixture pattern as <c>CoreServiceFixture</c> in
    /// <c>Tests.Core/Fixtures</c> and <c>MailServiceWebApplicationFactory</c> in
    /// <c>Tests.Mail/Fixtures</c>.
    ///
    /// <para><b>Source References:</b></para>
    /// <list type="bullet">
    ///   <item><c>WebVella.Erp.Plugins.Project/Services/ReportService.cs</c> — 137-line GetTimelogData() method</item>
    ///   <item><c>WebVella.Erp/Api/Definitions.cs</c> — SystemIds: SystemUserId, FirstUserId, AdministratorRoleId</item>
    ///   <item><c>WebVella.Erp/Database/DbContext.cs</c> — Original ambient context pattern (replaced by EF Core DI)</item>
    /// </list>
    /// </summary>
    public class ReportingDbContextFixture : IAsyncLifetime
    {
        #region Testcontainer Infrastructure

        /// <summary>
        /// PostgreSQL Testcontainer instance for integration tests.
        /// Uses postgres:16-alpine matching the AAP 0.7.4 docker-compose.localstack.yml specification.
        /// Configured with <c>WithCleanUp(true)</c> for reliable container cleanup.
        /// </summary>
        private readonly PostgreSqlContainer _postgresContainer;

        /// <summary>
        /// Live PostgreSQL connection string from the running Testcontainer.
        /// Passed to <see cref="ReportingWebApplicationFactory"/> for database-per-service integration tests.
        /// </summary>
        public string PostgresConnectionString => _postgresContainer.GetConnectionString();

        #endregion

        #region WebApplicationFactory

        /// <summary>
        /// Custom <see cref="ReportingWebApplicationFactory"/> providing the configured test server
        /// for Reporting service integration tests. Initialized in <see cref="InitializeAsync"/>
        /// with the live PostgreSQL Testcontainer connection string.
        ///
        /// Provides JWT authentication, InMemory cache (replacing Redis), MassTransit test harness,
        /// and the Testcontainer PostgreSQL connection string injection.
        /// </summary>
        public ReportingWebApplicationFactory Factory { get; private set; } = null!;

        /// <summary>
        /// Pre-configured <see cref="HttpClient"/> created from <see cref="Factory"/> for
        /// controller integration tests. Makes HTTP requests to the in-process test server.
        /// </summary>
        public HttpClient HttpClient { get; private set; } = null!;

        #endregion

        #region InMemory Database Configuration

        /// <summary>
        /// Base name for InMemory EF Core databases. Each call to <see cref="CreateInMemoryDbContext"/>
        /// appends a unique GUID suffix to prevent cross-contamination between parallel tests.
        /// </summary>
        private readonly string _inMemoryDatabaseName;

        #endregion

        #region Well-Known Test IDs — Match monolith SystemIds (Definitions.cs)

        /// <summary>
        /// System user UUID matching <c>SystemIds.SystemUserId</c> from monolith <c>Definitions.cs</c> line 6.
        /// The internal system user created during ERP initialization (<c>ERPService.cs</c> lines 447-459).
        /// </summary>
        public static readonly Guid SystemUserId = new Guid("10000000-0000-0000-0000-000000000000");

        /// <summary>
        /// First admin user UUID matching <c>SystemIds.FirstUserId</c> from monolith <c>Definitions.cs</c> line 7.
        /// The default administrator user created during ERP initialization (<c>ERPService.cs</c> lines 462-476).
        /// Username: "administrator", Email: "erp@webvella.com".
        /// </summary>
        public static readonly Guid FirstUserId = new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");

        /// <summary>
        /// Administrator role UUID matching <c>SystemIds.AdministratorRoleId</c> from monolith <c>Definitions.cs</c> line 14.
        /// The built-in administrator role with full CRUD permissions on all entities.
        /// </summary>
        public static readonly Guid AdministratorRoleId = new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA");

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the fixture with a uniquely-named InMemory database base name
        /// and a pre-configured PostgreSQL Testcontainer.
        ///
        /// <para><b>Container Configuration:</b></para>
        /// <list type="bullet">
        ///   <item>Image: <c>postgres:16-alpine</c> — matching AAP 0.7.4 docker-compose.localstack.yml</item>
        ///   <item>Database: <c>erp_reporting_test</c> — database-per-service pattern with test suffix</item>
        ///   <item>Username/Password: <c>test/test</c> — simple credentials for test environment</item>
        ///   <item>CleanUp: <c>true</c> — ensures container is removed after tests complete</item>
        /// </list>
        /// </summary>
        public ReportingDbContextFixture()
        {
            _inMemoryDatabaseName = $"ReportingTestDb_{Guid.NewGuid():N}";

            _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("erp_reporting_test")
                .WithUsername("test")
                .WithPassword("test")
                .WithCleanUp(true)
                .Build();
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Called once before any tests in the "ReportingService" collection run.
        /// Starts the PostgreSQL Testcontainer, creates the test server via
        /// <see cref="ReportingWebApplicationFactory"/>, and seeds the database
        /// with business-rule-covering test data.
        ///
        /// <para><b>Initialization Sequence:</b></para>
        /// <list type="number">
        ///   <item>Start PostgreSQL Testcontainer (provides live connection string)</item>
        ///   <item>Create <see cref="ReportingWebApplicationFactory"/> with the connection string</item>
        ///   <item>Create pre-configured <see cref="HttpClient"/> from the factory</item>
        ///   <item>Apply EF Core schema creation via <c>Database.EnsureCreatedAsync()</c></item>
        ///   <item>Seed test data via <see cref="TestDataScenarios.BuildStandardTestData()"/>
        ///         covering all ReportService.GetTimelogData() business rules</item>
        /// </list>
        /// </summary>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public async Task InitializeAsync()
        {
            // 1. Start the PostgreSQL Testcontainer — this provides a live connection string
            await _postgresContainer.StartAsync();

            // 2. Create the custom WebApplicationFactory with the live connection string
            //    This configures the test server with InMemory cache, MassTransit test harness,
            //    JWT authentication, and the Testcontainer PostgreSQL connection
            Factory = new ReportingWebApplicationFactory(PostgresConnectionString);

            // 3. Create the pre-configured HTTP client for controller integration tests
            HttpClient = Factory.CreateClient();

            // 4. Apply EF Core schema creation and seed test data
            //    Use a scoped DbContext from the test server's DI container to ensure
            //    the database schema matches the Reporting service's model configuration
            using (var scope = Factory.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();

                // Apply EF Core model to the PostgreSQL database (creates all tables,
                // indexes, and constraints defined in ReportingDbContext.OnModelCreating)
                await dbContext.Database.EnsureCreatedAsync();

                // Seed test data covering all business rules from ReportService.GetTimelogData()
                await SeedTestDataAsync(dbContext);
            }
        }

        /// <summary>
        /// Called once after all tests in the "ReportingService" collection complete.
        /// Disposes resources in reverse order of creation: HttpClient → Factory → container.
        ///
        /// <para><b>Disposal Sequence:</b></para>
        /// <list type="number">
        ///   <item>Dispose <see cref="HttpClient"/> — releases HTTP connection resources</item>
        ///   <item>Dispose <see cref="Factory"/> — shuts down the in-process test server</item>
        ///   <item>Dispose <see cref="_postgresContainer"/> — stops and removes the Docker container</item>
        /// </list>
        /// </summary>
        /// <returns>A task representing the asynchronous disposal operation.</returns>
        public async Task DisposeAsync()
        {
            // Dispose in reverse order of creation for proper resource cleanup
            HttpClient?.Dispose();

            if (Factory != null)
            {
                await Factory.DisposeAsync();
            }

            await _postgresContainer.DisposeAsync();
        }

        #endregion

        #region InMemory DbContext Factory Methods — For Unit Tests

        /// <summary>
        /// Creates a fresh EF Core InMemory <see cref="ReportingDbContext"/> for fast unit tests
        /// that don't require PostgreSQL. Each call produces a unique InMemory database instance
        /// to prevent test cross-contamination between parallel test executions.
        ///
        /// <para><b>Usage:</b></para>
        /// <code>
        /// var context = fixture.CreateInMemoryDbContext();
        /// context.TimelogProjections.Add(new TimelogProjection { ... });
        /// await context.SaveChangesAsync();
        /// </code>
        ///
        /// <para><b>Isolation:</b> Each call appends a new GUID to the database name,
        /// ensuring complete isolation between tests even when running in parallel.</para>
        /// </summary>
        /// <returns>
        /// A new <see cref="ReportingDbContext"/> backed by a unique EF Core InMemory database.
        /// </returns>
        public ReportingDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ReportingDbContext>()
                .UseInMemoryDatabase(databaseName: $"{_inMemoryDatabaseName}_{Guid.NewGuid():N}")
                .Options;

            return new ReportingDbContext(options);
        }

        /// <summary>
        /// Creates a fresh EF Core InMemory <see cref="ReportingDbContext"/> pre-seeded with
        /// the standard test data that exercises all business rules from
        /// <c>ReportService.GetTimelogData()</c>.
        ///
        /// <para><b>Convenience method</b> combining <see cref="CreateInMemoryDbContext"/>
        /// with <see cref="SeedTestDataAsync"/>.</para>
        ///
        /// <para><b>Pre-seeded Data:</b></para>
        /// <list type="bullet">
        ///   <item>3 projects (2 with accounts, 1 without — tests missing account error)</item>
        ///   <item>3 tasks (regular, multi-project, under accountless project)</item>
        ///   <item>6 timelogs (billable/non-billable, Jan/Feb, multiple projects)</item>
        ///   <item>2 report definitions (monthly and account-filtered)</item>
        /// </list>
        /// </summary>
        /// <returns>
        /// A new <see cref="ReportingDbContext"/> backed by a unique EF Core InMemory database,
        /// populated with standard test data.
        /// </returns>
        public async Task<ReportingDbContext> CreateInMemoryDbContextWithSeedDataAsync()
        {
            var context = CreateInMemoryDbContext();
            await SeedTestDataAsync(context);
            return context;
        }

        #endregion

        #region Scoped DbContext Helper — For Integration Tests

        /// <summary>
        /// Creates a new scoped <see cref="ReportingDbContext"/> from the running test server's
        /// DI container. The context is connected to the live PostgreSQL Testcontainer database
        /// and shares the same connection configuration as the test server's controllers.
        ///
        /// <para><b>Usage:</b> Integration tests use this to verify database state after
        /// making API calls through <see cref="HttpClient"/>:</para>
        /// <code>
        /// // Make API call
        /// var response = await fixture.HttpClient.PostAsync("/api/reports", content);
        /// 
        /// // Verify database state
        /// using var dbContext = fixture.CreateScopedDbContext();
        /// var report = await dbContext.ReportDefinitions.FirstOrDefaultAsync(r => r.Name == "Test Report");
        /// report.Should().NotBeNull();
        /// </code>
        ///
        /// <para><b>Note:</b> The caller is responsible for disposing the returned context.
        /// The underlying DI scope is created internally; the DbContext's disposal will
        /// handle scope cleanup.</para>
        /// </summary>
        /// <returns>
        /// A scoped <see cref="ReportingDbContext"/> connected to the live PostgreSQL Testcontainer.
        /// </returns>
        public ReportingDbContext CreateScopedDbContext()
        {
            var scope = Factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        }

        #endregion

        #region Private Seed Data Method

        /// <summary>
        /// Seeds the provided <see cref="ReportingDbContext"/> with business-rule-covering test data
        /// using <see cref="TestDataScenarios.BuildStandardTestData()"/>.
        ///
        /// <para><b>Delegates all data construction to <see cref="TestDataScenarios"/>.</b></para>
        ///
        /// The standard test data set includes:
        /// <list type="bullet">
        ///   <item><b>3 ProjectProjections:</b> Alpha (account), Beta (different account), Gamma (no account)</item>
        ///   <item><b>3 TaskProjections:</b> Development task, Bug Fix (multi-project), Review (accountless project)</item>
        ///   <item><b>6 TimelogProjections:</b> Billable/non-billable, Jan/Feb, multi-project, accountless</item>
        ///   <item><b>2 ReportDefinitions:</b> Monthly timelog report, account-filtered report</item>
        /// </list>
        ///
        /// <para><b>Business Rules Exercised (from ReportService.cs, 137 lines):</b></para>
        /// <list type="number">
        ///   <item>Month filtering: Timelogs 1-5 Jan, Timelog 6 Feb (source lines 27-30)</item>
        ///   <item>Billable/non-billable: Task 1 has both types (source lines 97-108)</item>
        ///   <item>Multi-project tasks: Task 2 in Alpha and Beta (source lines 75-95)</item>
        ///   <item>Account filtering: AccountAlphaId filter (source lines 110-125)</item>
        ///   <item>Missing account error: Gamma has null AccountId (source lines 115-120)</item>
        /// </list>
        /// </summary>
        /// <param name="dbContext">The <see cref="ReportingDbContext"/> to seed with test data.</param>
        /// <returns>A task representing the asynchronous seed operation.</returns>
        private async Task SeedTestDataAsync(ReportingDbContext dbContext)
        {
            // Use TestDataScenarios for all test data construction (AAP requirement:
            // "Use TestDataBuilder for all test data construction, not raw object creation")
            var (projects, tasks, timelogs, reports) = TestDataScenarios.BuildStandardTestData();

            // Seed ProjectProjections (3 projects: Alpha with account, Beta with different account, Gamma without account)
            await dbContext.ProjectProjections.AddRangeAsync(projects);

            // Seed TaskProjections (3 tasks: regular, multi-project, under accountless project)
            await dbContext.TaskProjections.AddRangeAsync(tasks);

            // Seed TimelogProjections (6 entries: billable/non-billable, different months, different projects)
            await dbContext.TimelogProjections.AddRangeAsync(timelogs);

            // Seed ReportDefinitions (2 definitions: monthly and account-filtered)
            await dbContext.ReportDefinitions.AddRangeAsync(reports);

            // Persist all seeded entities to the database
            await dbContext.SaveChangesAsync();
        }

        #endregion
    }

    #endregion
}
