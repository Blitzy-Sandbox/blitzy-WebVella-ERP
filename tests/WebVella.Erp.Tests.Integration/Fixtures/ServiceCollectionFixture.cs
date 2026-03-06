using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

// Type aliases for each service's Program class — each microservice has its own
// Program class in its own namespace. These aliases allow WebApplicationFactory<T>
// to reference the correct entry point for each service without ambiguity.
using CoreProgram = WebVella.Erp.Service.Core.Program;
using CrmProgram = WebVella.Erp.Service.Crm.Program;
using ProjectProgram = WebVella.Erp.Service.Project.Program;
using MailProgram = WebVella.Erp.Service.Mail.Program;
using AdminProgram = WebVella.Erp.Service.Admin.Program;
using ReportingProgram = WebVella.Erp.Service.Reporting.Program;

namespace WebVella.Erp.Tests.Integration.Fixtures
{
    /// <summary>
    /// Central xUnit <see cref="IAsyncLifetime"/> fixture that creates and manages
    /// <see cref="WebApplicationFactory{T}"/> instances for each microservice
    /// (Core, CRM, Project, Mail, Admin, Reporting), configuring each to use:
    ///
    /// <list type="bullet">
    ///   <item>Test PostgreSQL databases from <see cref="PostgreSqlFixture"/> (database-per-service)</item>
    ///   <item>MassTransit in-memory test harness (replacing production RabbitMQ/SQS transport)</item>
    ///   <item>LocalStack AWS endpoints from <see cref="LocalStackFixture"/> (SQS, SNS, S3)</item>
    ///   <item>Redis test instance from <see cref="RedisFixture"/> (distributed cache)</item>
    ///   <item>JWT test configuration matching monolith Config.json lines 24-28</item>
    /// </list>
    ///
    /// <para>
    /// This fixture is the central orchestrator enabling in-process cross-service
    /// integration testing. It creates in-memory test servers for all six domain
    /// services, each isolated with its own database, but sharing the same
    /// LocalStack and Redis infrastructure for realistic inter-service testing.
    /// </para>
    ///
    /// <para><b>Usage:</b></para>
    /// <para>
    /// Register as an <c>ICollectionFixture&lt;ServiceCollectionFixture&gt;</c> in the
    /// integration test collection definition. xUnit will inject the three
    /// infrastructure fixtures (PostgreSql, LocalStack, Redis) via the constructor,
    /// and each test class receives the configured service factories and client
    /// creation helpers through the collection fixture.
    /// </para>
    ///
    /// <para><b>Key AAP References:</b></para>
    /// <list type="bullet">
    ///   <item>AAP 0.7.4: Docker Compose topology — database names (erp_core, erp_crm, erp_project, erp_mail)</item>
    ///   <item>AAP 0.8.1: JWT authentication backward compatibility with existing tokens</item>
    ///   <item>AAP 0.8.2: Every REST endpoint requires happy-path and error-path tests</item>
    ///   <item>AAP 0.8.3: LocalStack endpoint configuration injectable via environment variables</item>
    /// </list>
    /// </summary>
    public class ServiceCollectionFixture : IAsyncLifetime
    {
        #region Constants — JWT Test Configuration

        /// <summary>
        /// Test JWT signing key matching monolith Config.json line 25.
        /// Per AAP 0.8.1: "JWT authentication must remain compatible with existing tokens."
        /// This key is used to sign test JWT tokens and is configured as the signing key
        /// in each service's JWT Bearer authentication middleware.
        /// </summary>
        private const string TestJwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// Test JWT issuer matching monolith Config.json line 26.
        /// Used as the ValidIssuer in each service's TokenValidationParameters.
        /// </summary>
        private const string TestJwtIssuer = "webvella-erp";

        /// <summary>
        /// Test JWT audience matching monolith Config.json line 27.
        /// Used as the ValidAudience in each service's TokenValidationParameters.
        /// </summary>
        private const string TestJwtAudience = "webvella-erp";

        /// <summary>
        /// Default encryption key for the test environment.
        /// Matches monolith Config.json line 5 — used by SharedKernel's ErpSettings.EncryptionKey
        /// for any encryption operations during integration tests.
        /// </summary>
        private const string TestEncryptionKey =
            "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658";

        #endregion

        #region Private Fields — Infrastructure Fixtures

        /// <summary>
        /// PostgreSQL fixture providing per-service test database connection strings.
        /// Injected by xUnit collection fixture infrastructure.
        /// </summary>
        private readonly PostgreSqlFixture _postgreSqlFixture;

        /// <summary>
        /// LocalStack fixture providing the AWS-compatible endpoint URL for SQS/SNS/S3.
        /// Injected by xUnit collection fixture infrastructure.
        /// </summary>
        private readonly LocalStackFixture _localStackFixture;

        /// <summary>
        /// Redis fixture providing the distributed cache connection string.
        /// Injected by xUnit collection fixture infrastructure.
        /// </summary>
        private readonly RedisFixture _redisFixture;

        #endregion

        #region Public Properties — Service Factories

        /// <summary>
        /// WebApplicationFactory for the Core Platform service.
        /// Configured with <see cref="PostgreSqlFixture.CoreConnectionString"/> (erp_core database).
        /// Provides the in-memory test server for entity, record, security, search,
        /// file, and datasource management endpoints.
        /// </summary>
        public WebApplicationFactory<CoreProgram> CoreServiceFactory { get; private set; }

        /// <summary>
        /// WebApplicationFactory for the CRM service.
        /// Configured with <see cref="PostgreSqlFixture.CrmConnectionString"/> (erp_crm database).
        /// Provides the in-memory test server for account, contact, case management,
        /// and CRM search indexing endpoints.
        /// </summary>
        public WebApplicationFactory<CrmProgram> CrmServiceFactory { get; private set; }

        /// <summary>
        /// WebApplicationFactory for the Project/Task service.
        /// Configured with <see cref="PostgreSqlFixture.ProjectConnectionString"/> (erp_project database).
        /// Provides the in-memory test server for task, timelog, comment, feed,
        /// and project reporting endpoints.
        /// </summary>
        public WebApplicationFactory<ProjectProgram> ProjectServiceFactory { get; private set; }

        /// <summary>
        /// WebApplicationFactory for the Mail/Notification service.
        /// Configured with <see cref="PostgreSqlFixture.MailConnectionString"/> (erp_mail database).
        /// Provides the in-memory test server for email CRUD, SMTP queue processing,
        /// and mail notification endpoints.
        /// </summary>
        public WebApplicationFactory<MailProgram> MailServiceFactory { get; private set; }

        /// <summary>
        /// WebApplicationFactory for the Admin/SDK service.
        /// Configured with <see cref="PostgreSqlFixture.CoreConnectionString"/> — the Admin
        /// service shares the Core database per AAP architecture (admin operations read/write
        /// the same entity metadata, system logs, and user data as the Core service).
        /// </summary>
        public WebApplicationFactory<AdminProgram> AdminServiceFactory { get; private set; }

        /// <summary>
        /// WebApplicationFactory for the Reporting service.
        /// Configured with a dedicated connection for report aggregation queries.
        /// The Reporting service consumes event-sourced projections from other services
        /// per AAP 0.4.3 CQRS (light) pattern.
        /// </summary>
        public WebApplicationFactory<ReportingProgram> ReportingServiceFactory { get; private set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="ServiceCollectionFixture"/>.
        /// All three infrastructure fixtures are injected by xUnit's collection fixture
        /// dependency injection mechanism. The fixtures must have completed their own
        /// InitializeAsync before this fixture's InitializeAsync is called.
        /// </summary>
        /// <param name="postgreSqlFixture">
        /// Provides per-service PostgreSQL connection strings for database-per-service isolation.
        /// Must not be null.
        /// </param>
        /// <param name="localStackFixture">
        /// Provides LocalStack endpoint URL for AWS SQS/SNS/S3 service emulation.
        /// Must not be null.
        /// </param>
        /// <param name="redisFixture">
        /// Provides Redis connection string for distributed cache testing.
        /// Must not be null.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown if any of the three infrastructure fixtures is null.
        /// </exception>
        public ServiceCollectionFixture(
            PostgreSqlFixture postgreSqlFixture,
            LocalStackFixture localStackFixture,
            RedisFixture redisFixture)
        {
            _postgreSqlFixture = postgreSqlFixture
                ?? throw new ArgumentNullException(nameof(postgreSqlFixture));
            _localStackFixture = localStackFixture
                ?? throw new ArgumentNullException(nameof(localStackFixture));
            _redisFixture = redisFixture
                ?? throw new ArgumentNullException(nameof(redisFixture));
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Creates and configures all six WebApplicationFactory instances, one per microservice.
        ///
        /// Each factory is configured with:
        /// <list type="number">
        ///   <item>Test PostgreSQL connection string (database-per-service)</item>
        ///   <item>MassTransit in-memory test harness (replacing RabbitMQ/SQS)</item>
        ///   <item>LocalStack AWS endpoints (SQS, SNS, S3)</item>
        ///   <item>Redis test container connection string (distributed cache)</item>
        ///   <item>JWT test configuration (key, issuer, audience from Config.json)</item>
        ///   <item>Disabled background jobs for deterministic test execution</item>
        ///   <item>ErpSettings-compatible configuration keys for SharedKernel initialization</item>
        /// </list>
        ///
        /// Factories are created synchronously since WebApplicationFactory construction
        /// is lightweight — the actual test server starts lazily when CreateClient() or
        /// Services is first accessed.
        /// </summary>
        /// <returns>A completed task.</returns>
        public Task InitializeAsync()
        {
            Console.WriteLine(
                "[ServiceCollectionFixture] Initializing service factories...");

            // Core Platform service — owns erp_core database for entities, records,
            // security, search, files, and datasource management.
            CoreServiceFactory = CreateServiceFactory<CoreProgram>(
                _postgreSqlFixture.CoreConnectionString,
                "erp_core_");
            Console.WriteLine(
                "[ServiceCollectionFixture] Core service factory created.");

            // CRM service — owns erp_crm database for accounts, contacts, cases,
            // addresses, and salutations. SearchService handles x_search regeneration.
            CrmServiceFactory = CreateServiceFactory<CrmProgram>(
                _postgreSqlFixture.CrmConnectionString,
                "erp_crm_");
            Console.WriteLine(
                "[ServiceCollectionFixture] CRM service factory created.");

            // Project/Task service — owns erp_project database for tasks, timelogs,
            // comments, feed, and project reporting. StartTasksOnStartDateJob runs as
            // a background hosted service (disabled in tests via Jobs:Enabled=false).
            ProjectServiceFactory = CreateServiceFactory<ProjectProgram>(
                _postgreSqlFixture.ProjectConnectionString,
                "erp_project_");
            Console.WriteLine(
                "[ServiceCollectionFixture] Project service factory created.");

            // Mail/Notification service — owns erp_mail database for emails and
            // SMTP services. ProcessMailQueueJob is disabled in tests.
            MailServiceFactory = CreateServiceFactory<MailProgram>(
                _postgreSqlFixture.MailConnectionString,
                "erp_mail_");
            Console.WriteLine(
                "[ServiceCollectionFixture] Mail service factory created.");

            // Admin/SDK service — shares Core database per AAP architecture.
            // Admin operations (code gen, log management, entity designer) operate
            // on the same entity metadata as the Core service.
            AdminServiceFactory = CreateServiceFactory<AdminProgram>(
                _postgreSqlFixture.CoreConnectionString,
                "erp_admin_");
            Console.WriteLine(
                "[ServiceCollectionFixture] Admin service factory created.");

            // Reporting service — uses Core connection for event-sourced projections.
            // Per AAP 0.4.3 CQRS pattern: reads from projections populated by
            // MassTransit event consumers from Project and CRM services.
            ReportingServiceFactory = CreateServiceFactory<ReportingProgram>(
                _postgreSqlFixture.CoreConnectionString,
                "erp_reporting_");
            Console.WriteLine(
                "[ServiceCollectionFixture] Reporting service factory created.");

            Console.WriteLine(
                "[ServiceCollectionFixture] All 6 service factories initialized.");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes all WebApplicationFactory instances in reverse order of creation.
        /// Exceptions during disposal are swallowed and logged to prevent masking
        /// actual test assertion failures.
        ///
        /// Disposal order (reverse of creation):
        /// 1. Reporting → 2. Admin → 3. Mail → 4. Project → 5. CRM → 6. Core
        ///
        /// Core is disposed last because other services may hold gRPC channel
        /// references to the Core service's test server.
        /// </summary>
        /// <returns>A completed task.</returns>
        public Task DisposeAsync()
        {
            Console.WriteLine(
                "[ServiceCollectionFixture] Disposing service factories (reverse order)...");

            // Dispose in reverse order of creation to respect potential
            // inter-service dependencies (e.g., CRM → Core gRPC channels).
            SafeDispose(ReportingServiceFactory, "Reporting");
            SafeDispose(AdminServiceFactory, "Admin");
            SafeDispose(MailServiceFactory, "Mail");
            SafeDispose(ProjectServiceFactory, "Project");
            SafeDispose(CrmServiceFactory, "CRM");
            SafeDispose(CoreServiceFactory, "Core");

            Console.WriteLine(
                "[ServiceCollectionFixture] All service factories disposed.");

            return Task.CompletedTask;
        }

        #endregion

        #region Public Methods — HttpClient Creation

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> connected to the Core service's test server.
        /// The client is pre-configured to route requests to the in-memory test server.
        /// </summary>
        /// <returns>An HttpClient targeting the Core Platform service.</returns>
        public HttpClient CreateCoreClient()
        {
            return CoreServiceFactory.CreateClient();
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> connected to the CRM service's test server.
        /// The client is pre-configured to route requests to the in-memory test server.
        /// </summary>
        /// <returns>An HttpClient targeting the CRM service.</returns>
        public HttpClient CreateCrmClient()
        {
            return CrmServiceFactory.CreateClient();
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> connected to the Project service's test server.
        /// The client is pre-configured to route requests to the in-memory test server.
        /// </summary>
        /// <returns>An HttpClient targeting the Project/Task service.</returns>
        public HttpClient CreateProjectClient()
        {
            return ProjectServiceFactory.CreateClient();
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> connected to the Mail service's test server.
        /// The client is pre-configured to route requests to the in-memory test server.
        /// </summary>
        /// <returns>An HttpClient targeting the Mail/Notification service.</returns>
        public HttpClient CreateMailClient()
        {
            return MailServiceFactory.CreateClient();
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> connected to the Admin service's test server.
        /// The client is pre-configured to route requests to the in-memory test server.
        /// </summary>
        /// <returns>An HttpClient targeting the Admin/SDK service.</returns>
        public HttpClient CreateAdminClient()
        {
            return AdminServiceFactory.CreateClient();
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> connected to the Reporting service's test server.
        /// The client is pre-configured to route requests to the in-memory test server.
        /// </summary>
        /// <returns>An HttpClient targeting the Reporting service.</returns>
        public HttpClient CreateReportingClient()
        {
            return ReportingServiceFactory.CreateClient();
        }

        /// <summary>
        /// Adds a JWT Bearer authentication header to the given <see cref="HttpClient"/>.
        /// Sets the <c>Authorization: Bearer {jwtToken}</c> header for authenticated
        /// API requests across all microservices.
        ///
        /// Per AAP 0.8.1: "JWT authentication must remain compatible with existing tokens."
        /// Per AAP 0.8.3: "JWT tokens issued by the Core service must contain all necessary
        /// claims (user ID, roles, permissions) for downstream services to authorize requests
        /// without callback to the Core service."
        ///
        /// Usage:
        /// <code>
        /// var client = fixture.CreateCoreClient();
        /// fixture.CreateAuthenticatedClient(client, jwtToken);
        /// var response = await client.GetAsync("/api/v3/en_US/entity/user/list");
        /// </code>
        /// </summary>
        /// <param name="client">
        /// The HttpClient to add the Bearer token to. Must not be null.
        /// The same client instance is returned for fluent chaining.
        /// </param>
        /// <param name="jwtToken">
        /// The JWT Bearer token string (without the "Bearer " prefix — the prefix is
        /// added automatically). Must not be null or whitespace.
        /// </param>
        /// <returns>
        /// The same <paramref name="client"/> with the Authorization header set,
        /// enabling fluent method chaining.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="client"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="jwtToken"/> is null or whitespace.
        /// </exception>
        public HttpClient CreateAuthenticatedClient(HttpClient client, string jwtToken)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(jwtToken))
                throw new ArgumentException(
                    "JWT token cannot be null or whitespace.", nameof(jwtToken));

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);

            return client;
        }

        #endregion

        #region Private Methods — Factory Construction

        /// <summary>
        /// Creates a <see cref="WebApplicationFactory{T}"/> for a specific microservice,
        /// configured with test infrastructure overrides for the database, messaging,
        /// caching, authentication, and AWS endpoint configuration.
        ///
        /// The factory uses <c>WithWebHostBuilder</c> to override the service's
        /// <c>appsettings.json</c> configuration with test-specific values and to
        /// replace the production MassTransit bus with an in-memory test harness.
        ///
        /// The test server is created lazily when <c>CreateClient()</c> or
        /// <c>Services</c> is first accessed on the returned factory.
        /// </summary>
        /// <typeparam name="T">
        /// The Program entry point class of the target microservice.
        /// Must be a public class (or have InternalsVisibleTo configured).
        /// </typeparam>
        /// <param name="connectionString">
        /// The PostgreSQL connection string for the service's test database.
        /// Obtained from <see cref="PostgreSqlFixture"/>.
        /// </param>
        /// <param name="redisInstancePrefix">
        /// The Redis instance name prefix for cache key isolation between services.
        /// Each service uses a unique prefix (e.g., "erp_core_", "erp_crm_") to
        /// prevent cache key collisions when multiple services share the same
        /// Redis instance.
        /// </param>
        /// <returns>
        /// A configured <see cref="WebApplicationFactory{T}"/> ready for client creation.
        /// </returns>
        private WebApplicationFactory<T> CreateServiceFactory<T>(
            string connectionString,
            string redisInstancePrefix) where T : class
        {
            var testConfigValues = BuildTestConfiguration(
                connectionString, redisInstancePrefix);

            return new WebApplicationFactory<T>().WithWebHostBuilder(builder =>
            {
                // ============================================================
                // Configuration Override
                // AddInMemoryCollection runs AFTER the service's default
                // configuration sources (appsettings.json, environment vars),
                // so test values take precedence over production defaults.
                // ============================================================
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.AddInMemoryCollection(testConfigValues);
                });

                // ============================================================
                // Service Registration Override
                // ConfigureServices on IWebHostBuilder runs AFTER the app's
                // service registrations in Program.cs (minimal hosting pattern),
                // allowing us to replace production services with test doubles.
                // ============================================================
                builder.ConfigureServices(services =>
                {
                    // Remove any existing MassTransit hosted services to prevent
                    // the production bus from attempting to connect to RabbitMQ
                    // or SQS during test execution.
                    services.RemoveAll<IHostedService>();

                    // Add MassTransit in-memory test harness — replaces the
                    // production RabbitMQ/SQS transport with an in-memory bus
                    // that captures published messages for test assertions.
                    // Per AAP 0.5.2: Hook-based communication replaced by
                    // asynchronous event-driven messaging between services.
                    services.AddMassTransitTestHarness();

                    // Register a singleton marker indicating test environment.
                    // Services can check for this to skip operations that are
                    // inappropriate during testing (e.g., external API calls).
                    services.AddSingleton<TestEnvironmentMarker>(
                        new TestEnvironmentMarker());
                });
            });
        }

        /// <summary>
        /// Builds the complete test configuration dictionary for a single microservice.
        /// This dictionary overrides all infrastructure-related settings in the service's
        /// <c>appsettings.json</c> to point at test containers instead of production resources.
        ///
        /// Configuration categories:
        /// <list type="number">
        ///   <item>Database — ConnectionStrings:Default + legacy Settings:ConnectionString</item>
        ///   <item>JWT — Key, Issuer, Audience matching Config.json lines 24-28</item>
        ///   <item>Redis — ConnectionString, Configuration, InstanceName</item>
        ///   <item>AWS/LocalStack — Service URLs for SQS, SNS, S3 with ForcePathStyle</item>
        ///   <item>Background jobs — Disabled for deterministic test execution</item>
        ///   <item>ErpSettings — Encryption key, language, locale, timezone</item>
        ///   <item>Messaging — Transport set to InMemory (overridden by test harness)</item>
        /// </list>
        /// </summary>
        /// <param name="connectionString">PostgreSQL connection string for the service's DB.</param>
        /// <param name="redisInstancePrefix">Redis key prefix for service isolation.</param>
        /// <returns>
        /// A dictionary of configuration key-value pairs for in-memory configuration override.
        /// </returns>
        private Dictionary<string, string> BuildTestConfiguration(
            string connectionString,
            string redisInstancePrefix)
        {
            return new Dictionary<string, string>
            {
                // =============================================================
                // Database Configuration
                // Both modern (ConnectionStrings:Default) and legacy
                // (Settings:ConnectionString) keys are set to ensure
                // compatibility with services using either pattern.
                // Format per Config.json line 4.
                // =============================================================
                ["ConnectionStrings:Default"] = connectionString,
                ["Settings:ConnectionString"] = connectionString,

                // =============================================================
                // JWT Configuration
                // Values match monolith Config.json lines 24-28.
                // Per AAP 0.8.1: JWT authentication must remain compatible
                // with existing tokens.
                // =============================================================
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = TestJwtIssuer,
                ["Jwt:Audience"] = TestJwtAudience,
                ["Jwt:RequireHttpsMetadata"] = "false",
                ["Jwt:ExpirationMinutes"] = "60",

                // =============================================================
                // Redis Configuration
                // Multiple keys are set to cover different configuration
                // patterns across services:
                //   - Redis:ConnectionString — used by Core service
                //   - Redis:Configuration — used by some StackExchangeRedis configs
                //   - ConnectionStrings:Redis — standard connection string pattern
                //   - Redis:InstanceName — per-service cache key prefix
                // Per AAP 0.7.4: Redis for distributed cache testing.
                // =============================================================
                ["Redis:ConnectionString"] = _redisFixture.ConnectionString,
                ["Redis:Configuration"] = _redisFixture.ConnectionString,
                ["ConnectionStrings:Redis"] = _redisFixture.ConnectionString,
                ["Redis:InstanceName"] = redisInstancePrefix,

                // =============================================================
                // AWS / LocalStack Configuration
                // Per AAP 0.8.3: "LocalStack endpoint configuration must be
                // injectable via environment variables."
                // Sets service URLs for SQS, SNS, and S3 to point at the
                // LocalStack container. ForcePathStyle=true is required for
                // S3 with LocalStack (virtual-hosted style not supported).
                // =============================================================
                ["AWS:ServiceURL"] = _localStackFixture.Endpoint,
                ["AWS:Region"] = "us-east-1",
                ["AWS:SQS:ServiceURL"] = _localStackFixture.Endpoint,
                ["AWS:SNS:ServiceURL"] = _localStackFixture.Endpoint,
                ["AWS:S3:ServiceURL"] = _localStackFixture.Endpoint,
                ["AWS:S3:ForcePathStyle"] = "true",
                ["LOCALSTACK_ENDPOINT"] = _localStackFixture.Endpoint,

                // =============================================================
                // Background Jobs — Disabled
                // Per test isolation requirements: background jobs must not
                // run during integration tests. Test scenarios explicitly
                // trigger job logic when needed.
                // =============================================================
                ["Jobs:Enabled"] = "false",
                ["Settings:EnableBackgroundJobs"] = "false",

                // =============================================================
                // ErpSettings Configuration
                // SharedKernel's ErpSettings.Initialize() reads from Settings:*
                // keys. These values match the monolith's Config.json defaults.
                // =============================================================
                ["Settings:EncryptionKey"] = TestEncryptionKey,
                ["Settings:Lang"] = "en",
                ["Settings:Locale"] = "en-US",
                ["Settings:TimeZoneName"] = "FLE Standard Time",
                ["Settings:DevelopmentMode"] = "true",
                ["Settings:CacheKey"] = "integration-test",
                ["Settings:EnableFileSystemStorage"] = "false",
                ["Settings:EmailEnabled"] = "false",

                // =============================================================
                // Messaging Transport
                // Set to InMemory — the MassTransit test harness overrides
                // this with its own in-memory transport regardless, but
                // setting it explicitly prevents any production transport
                // initialization code from executing.
                // =============================================================
                ["Messaging:Transport"] = "InMemory",
            };
        }

        /// <summary>
        /// Safely disposes a <see cref="WebApplicationFactory{T}"/> instance, swallowing
        /// any exceptions to prevent disposal failures from masking test assertion errors.
        /// </summary>
        /// <param name="factory">The factory to dispose. May be null.</param>
        /// <param name="serviceName">
        /// Human-readable service name for diagnostic logging (e.g., "Core", "CRM").
        /// </param>
        private static void SafeDispose(IDisposable factory, string serviceName)
        {
            if (factory == null)
            {
                Console.WriteLine(
                    $"[ServiceCollectionFixture] {serviceName} factory was null — skipping dispose.");
                return;
            }

            try
            {
                factory.Dispose();
                Console.WriteLine(
                    $"[ServiceCollectionFixture] {serviceName} service factory disposed.");
            }
            catch (Exception ex)
            {
                // Swallow disposal exceptions to prevent masking test failures.
                // Disposal failures are logged for diagnostics but do not propagate.
                Console.WriteLine(
                    $"[ServiceCollectionFixture] Warning: Error disposing {serviceName} " +
                    $"service factory: {ex.Message}");
            }
        }

        #endregion
    }

    /// <summary>
    /// Marker class registered as a singleton in each service's DI container during
    /// integration tests. Services can inject this marker to detect test environment
    /// execution and conditionally skip operations that are inappropriate during testing
    /// (e.g., external API calls to third-party services, cloud resource provisioning).
    ///
    /// Usage in service code:
    /// <code>
    /// public class SomeService
    /// {
    ///     private readonly bool _isTestEnvironment;
    ///     public SomeService(TestEnvironmentMarker marker = null)
    ///     {
    ///         _isTestEnvironment = marker != null;
    ///     }
    /// }
    /// </code>
    /// </summary>
    public class TestEnvironmentMarker
    {
        /// <summary>
        /// Timestamp when the test environment was initialized.
        /// Useful for correlating test execution with container lifecycle events.
        /// </summary>
        public DateTime InitializedAt { get; } = DateTime.UtcNow;

        /// <summary>
        /// Indicates this is a test environment marker.
        /// Always returns true for the test environment.
        /// </summary>
        public bool IsTestEnvironment => true;
    }
}
