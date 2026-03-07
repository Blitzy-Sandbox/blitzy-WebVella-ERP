using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MassTransit;
using MassTransit.Testing;
using WebVella.Erp.Service.Admin;

namespace WebVella.Erp.Tests.Admin.Fixtures
{
    /// <summary>
    /// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that overrides the Admin/SDK
    /// microservice's production configuration for integration test isolation.
    ///
    /// <para><b>Key responsibilities:</b></para>
    /// <list type="bullet">
    ///   <item>Replaces the production PostgreSQL connection string with one from a
    ///         Testcontainers PostgreSQL container (injected via constructor)</item>
    ///   <item>Replaces the Redis distributed cache with an in-memory implementation
    ///         to eliminate external Redis dependency during tests</item>
    ///   <item>Replaces MassTransit's RabbitMQ/SQS transport with the in-memory test
    ///         harness for isolated event publisher/subscriber testing</item>
    ///   <item>Registers <see cref="TestAuthHandler"/> as the "Test" authentication
    ///         scheme, bypassing production JWT Bearer validation</item>
    ///   <item>Disables all <see cref="IHostedService"/> background workers to prevent
    ///         background job processing during test execution</item>
    ///   <item>Overrides configuration values (JWT, encryption key, locale) to match
    ///         the monolith's <c>Config.json</c> for backward compatibility</item>
    /// </list>
    ///
    /// <para>
    /// This factory is instantiated by <c>PostgreSqlContainerFixture</c> after the
    /// PostgreSQL Testcontainer is started and ready. It is shared across all test
    /// classes in the "AdminService" xUnit collection via <c>ICollectionFixture</c>.
    /// </para>
    ///
    /// <para><b>Source patterns preserved from monolith:</b></para>
    /// <list type="bullet">
    ///   <item><c>Startup.cs</c> line 40: <c>AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)</c></item>
    ///   <item><c>Config.json</c> lines 24-28: JWT key, issuer, audience values</item>
    ///   <item><c>Config.json</c> line 5: EncryptionKey for password hash compatibility</item>
    ///   <item><c>Config.json</c> lines 10-12: DevelopmentMode, background jobs disabled, file system storage disabled</item>
    /// </list>
    /// </summary>
    public class AdminWebApplicationFactory : WebApplicationFactory<Program>
    {
        /// <summary>
        /// The PostgreSQL connection string from the Testcontainers PostgreSQL container.
        /// Injected via constructor and used to override the production
        /// <c>ConnectionStrings:Default</c> configuration value.
        /// </summary>
        private readonly string _postgresConnectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdminWebApplicationFactory"/> class
        /// with the specified PostgreSQL connection string from a Testcontainer.
        /// </summary>
        /// <param name="postgresConnectionString">
        /// The connection string from a running PostgreSQL Testcontainer instance.
        /// This MUST point to a real PostgreSQL database (never a production database).
        /// Obtained from <c>PostgreSqlContainer.GetConnectionString()</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="postgresConnectionString"/> is <c>null</c>.
        /// </exception>
        public AdminWebApplicationFactory(string postgresConnectionString)
        {
            _postgresConnectionString = postgresConnectionString
                ?? throw new ArgumentNullException(nameof(postgresConnectionString));
        }

        /// <summary>
        /// Overrides the Admin service's web host configuration for test isolation.
        /// This method is called by the <see cref="WebApplicationFactory{TEntryPoint}"/>
        /// infrastructure AFTER the Admin service's <c>Program.Main()</c> has configured
        /// the <c>WebApplicationBuilder</c> but BEFORE the host is built and started.
        ///
        /// <para>The override performs five critical substitutions:</para>
        /// <list type="number">
        ///   <item>Sets the hosting environment to "Testing" to enable test-specific code paths</item>
        ///   <item>Overrides <c>appsettings.json</c> with in-memory configuration matching
        ///         the monolith's <c>Config.json</c> values for JWT, encryption, and locale</item>
        ///   <item>Replaces Redis distributed cache with in-memory cache</item>
        ///   <item>Replaces MassTransit RabbitMQ/SQS transport with in-memory test harness</item>
        ///   <item>Replaces JWT Bearer authentication with <see cref="TestAuthHandler"/></item>
        /// </list>
        /// </summary>
        /// <param name="builder">The web host builder to configure for testing.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // ================================================================
            // 1. Set hosting environment to "Testing"
            // Enables test-specific configuration and code paths in the Admin
            // service (e.g., conditional middleware, Swagger availability).
            // ================================================================
            builder.UseEnvironment("Testing");

            // ================================================================
            // 2. Override appsettings.json with test-specific configuration
            // AddInMemoryCollection runs AFTER appsettings.json is loaded,
            // so these values take precedence over any file-based config.
            //
            // CRITICAL: JWT and EncryptionKey values MUST match the monolith's
            // Config.json exactly for backward compatibility:
            //   - JWT Key: Config.json line 25
            //   - JWT Issuer: Config.json line 26
            //   - JWT Audience: Config.json line 27
            //   - EncryptionKey: Config.json line 5 (password hash compatibility)
            // ================================================================
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    // Database — points to the Testcontainer PostgreSQL instance
                    ["ConnectionStrings:Default"] = _postgresConnectionString,

                    // JWT configuration — MUST match monolith Config.json lines 24-28
                    // These values are used by the Admin service's JWT Bearer validation
                    // (Program.cs lines 154-178) and by TestAuthHandler for claim generation
                    ["Jwt:Key"] = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey",
                    ["Jwt:Issuer"] = "webvella-erp",
                    ["Jwt:Audience"] = "webvella-erp",

                    // ERP settings — matching monolith Config.json lines 5-12
                    ["Settings:DevelopmentMode"] = "true",
                    ["Settings:EnableBackgroundJobs"] = "false",
                    ["Settings:EnableFileSystemStorage"] = "false",
                    ["Settings:EncryptionKey"] = "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658",
                    ["Settings:Lang"] = "en",
                    ["Settings:Locale"] = "en-US",

                    // Background jobs — disabled for test isolation
                    ["Jobs:Enabled"] = "false",

                    // Redis — not used in tests (replaced with in-memory cache below)
                    ["Redis:ConnectionString"] = "not-used-in-tests",

                    // Messaging — signal to use in-memory transport instead of RabbitMQ/SQS
                    ["Messaging:Transport"] = "InMemory"
                });
            });

            // ================================================================
            // 3. Override DI service registrations for test isolation
            // ConfigureTestServices runs AFTER the Admin service's
            // Program.ConfigureServices() has completed, so these
            // registrations override/supplement the production services.
            // ================================================================
            builder.ConfigureTestServices(services =>
            {
                // --------------------------------------------------------
                // 3a. Npgsql legacy timestamp behavior
                // CRITICAL: Must be set before any Npgsql connections are
                // created. Preserves the monolith's timestamp handling from
                // Startup.cs line 40. Also set in Admin service Program.cs
                // line 59, but we ensure it here for test safety.
                // --------------------------------------------------------
                AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

                // --------------------------------------------------------
                // 3b. Replace Redis distributed cache with in-memory
                // The Admin service registers StackExchange.Redis cache
                // (Program.cs lines 218-222). For tests, we remove all
                // IDistributedCache registrations and add an in-memory
                // implementation to avoid requiring a running Redis instance.
                // --------------------------------------------------------
                services.RemoveAll<IDistributedCache>();
                services.AddDistributedMemoryCache();

                // --------------------------------------------------------
                // 3c. Replace MassTransit RabbitMQ/SQS with in-memory harness
                // The Admin service registers MassTransit with RabbitMQ
                // transport (Program.cs lines 194-210). The test harness
                // replaces this with an in-memory transport that:
                //   - Captures all published/sent messages for assertion
                //   - Delivers messages to consumers synchronously
                //   - Requires no external RabbitMQ or SQS infrastructure
                // AddConsumers scans the Admin service assembly to register
                // all consumer classes with the test harness.
                // --------------------------------------------------------
                services.AddMassTransitTestHarness(cfg =>
                {
                    cfg.AddConsumers(typeof(Program).Assembly);
                });

                // --------------------------------------------------------
                // 3d. Replace JWT Bearer authentication with TestAuthHandler
                // The Admin service uses JWT Bearer as the default scheme
                // (Program.cs lines 159-178). For tests, we replace this
                // with the TestAuthHandler that:
                //   - Always returns AuthenticateResult.Success
                //   - Provides configurable claims via request headers
                //   - Defaults to admin user identity (SystemIds.FirstUserId)
                //   - Supports role-based authorization testing
                // The "Test" scheme name matches TestAuthHandler.TestScheme.
                // --------------------------------------------------------
                services.AddAuthentication(TestAuthHandler.TestScheme)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.TestScheme, options => { });

                // --------------------------------------------------------
                // 3e. Remove all hosted background services
                // The Admin service registers ClearJobAndErrorLogsJob as a
                // hosted service (Program.cs line 283). During tests, we
                // remove ALL IHostedService registrations to prevent:
                //   - Background job processing interfering with test state
                //   - Schedule-based triggers firing during test execution
                //   - Uncontrolled database writes from background workers
                // The job class itself can still be tested by direct
                // instantiation when needed.
                // --------------------------------------------------------
                var hostedServiceDescriptors = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var descriptor in hostedServiceDescriptors)
                {
                    services.Remove(descriptor);
                }
            });
        }

        /// <summary>
        /// Disposes of the test server and all associated resources.
        /// The base class implementation stops the in-memory test server,
        /// disposes the <see cref="IHost"/>, and releases the HTTP client.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
