// =============================================================================
// CrmWebApplicationFactory.cs — Custom WebApplicationFactory for CRM Service
//                                Integration Tests
// =============================================================================
// Provides a fully configured in-memory test server hosting the CRM microservice
// (WebVella.Erp.Service.Crm) with production infrastructure replaced by test
// doubles:
//   - PostgreSQL connection string injected from Testcontainers.PostgreSql
//   - Redis distributed cache replaced with in-memory MemoryDistributedCache
//   - MassTransit RabbitMQ/SQS transport replaced with InMemory test harness
//   - JWT Bearer authentication configured with well-known test signing key
//   - Background job hosted services disabled for test isolation
//   - SearchService replaced with MockSearchService for controlled behavior
//
// Follows the same pattern as CoreServiceWebApplicationFactory.cs from
// tests/WebVella.Erp.Tests.Core/Fixtures/.
//
// Source references (monolith):
//   - WebVella.Erp.Site.Crm/Startup.cs      (original host composition)
//   - WebVella.Erp.Site.Crm/Config.json      (original configuration values)
//   - WebVella.Erp.Site.Crm/Program.cs        (original WebHost builder)
//   - WebVella.Erp.Plugins.Crm/CrmPlugin.cs   (domain bootstrap)
//   - WebVella.Erp.Plugins.Next/Services/SearchService.cs (search indexing)
//
// Key architectural changes from monolith (per AAP 0.8):
//   - JWT-only auth (no cookie auth "erp_auth_crm" from Startup.cs lines 62-71)
//   - No plugin pipeline (UseErpPlugin removed — Startup.cs lines 120-124)
//   - MassTransit replaces PostgreSQL LISTEN/NOTIFY for events
//   - Redis replaces IMemoryCache for distributed caching
//   - EF Core replaces ambient static DbContext.Current
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using WebVella.Erp.Service.Crm;
using WebVella.Erp.Service.Crm.Domain.Services;
using Xunit;

namespace WebVella.Erp.Tests.Crm.Fixtures
{
    /// <summary>
    /// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that creates an in-memory
    /// test server hosting the CRM microservice with all production infrastructure replaced
    /// by test doubles. This is the central test host for CRM integration tests.
    ///
    /// <para><b>Infrastructure Replacements:</b></para>
    /// <list type="bullet">
    ///   <item>PostgreSQL connection → Testcontainer connection string (injected via constructor)</item>
    ///   <item>Redis distributed cache → In-memory MemoryDistributedCache</item>
    ///   <item>MassTransit RabbitMQ/SQS → InMemory test harness with consumer auto-discovery</item>
    ///   <item>SearchService → MockSearchService for controlled test behavior</item>
    ///   <item>Background job hosted services → Removed for test isolation</item>
    ///   <item>JWT Bearer auth → Configured with well-known test signing key/issuer/audience</item>
    /// </list>
    ///
    /// <para><b>Usage:</b></para>
    /// <code>
    /// var factory = new CrmWebApplicationFactory(postgresConnectionString);
    /// var client = factory.CreateClient();
    /// // Use client for HTTP-based integration tests
    /// // Access factory.MockSearchService for search indexing assertions
    /// // Access factory.Services.CreateScope() for DI service resolution
    /// </code>
    /// </summary>
    public class CrmWebApplicationFactory : WebApplicationFactory<Program>
    {
        // ──────────────────────────────────────────────────────────────
        //  Constants — Test JWT Configuration
        //  Values MUST match monolith Config.json for backward compatibility
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// JWT signing key for test token generation and validation.
        /// Must match the key used in test JWT token generation helpers
        /// and the monolith's Config.json Jwt:Key value.
        /// </summary>
        private const string TestJwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// JWT issuer for test token validation.
        /// Must match the monolith's Config.json Jwt:Issuer value.
        /// </summary>
        private const string TestJwtIssuer = "webvella-erp";

        /// <summary>
        /// JWT audience for test token validation.
        /// Must match the monolith's Config.json Jwt:Audience value.
        /// </summary>
        private const string TestJwtAudience = "webvella-erp";

        /// <summary>
        /// Encryption key matching monolith Config.json line 4.
        /// Required for password hash compatibility in integration tests
        /// that verify user authentication flows.
        /// </summary>
        private const string TestEncryptionKey = "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658";

        // ──────────────────────────────────────────────────────────────
        //  Fields
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// PostgreSQL connection string from a Testcontainers.PostgreSql container.
        /// Injected via constructor and used to override the CRM service's
        /// ConnectionStrings:Default configuration value.
        /// </summary>
        private readonly string _postgresConnectionString;

        /// <summary>
        /// Configurable mock replacement for the CRM service's SearchService.
        /// Prevents heavy infrastructure operations (EntityManager, EQL, RecordManager)
        /// during controller and event handler integration tests while recording
        /// calls for test assertions.
        /// </summary>
        private readonly MockSearchService _mockSearchService;

        // ──────────────────────────────────────────────────────────────
        //  Properties
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Exposes the <see cref="MockSearchService"/> instance for test assertions.
        /// Tests can verify search indexing calls via <c>MockSearchService.Calls</c>,
        /// <c>MockSearchService.CallCount</c>, <c>MockSearchService.VerifyCalledOnce()</c>,
        /// and <c>MockSearchService.VerifyNeverCalled()</c>.
        /// </summary>
        public MockSearchService MockSearchService => _mockSearchService;

        // ──────────────────────────────────────────────────────────────
        //  Constructor
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new <see cref="CrmWebApplicationFactory"/> with the specified
        /// PostgreSQL connection string and optional mock search service.
        /// </summary>
        /// <param name="postgresConnectionString">
        /// PostgreSQL connection string from a Testcontainers.PostgreSql container.
        /// This replaces the production database connection for test isolation.
        /// Must not be null or empty.
        /// </param>
        /// <param name="mockSearchService">
        /// Optional <see cref="Fixtures.MockSearchService"/> instance for replacing the
        /// CRM service's <see cref="SearchService"/>. When null, a new default instance
        /// is created (succeeds silently, records all calls).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="postgresConnectionString"/> is null.
        /// </exception>
        public CrmWebApplicationFactory(
            string postgresConnectionString,
            MockSearchService mockSearchService = null)
        {
            _postgresConnectionString = postgresConnectionString
                ?? throw new ArgumentNullException(nameof(postgresConnectionString),
                    "PostgreSQL connection string must be provided. " +
                    "Use a Testcontainers.PostgreSql container to obtain a connection string.");
            _mockSearchService = mockSearchService ?? new MockSearchService();
        }

        // ──────────────────────────────────────────────────────────────
        //  ConfigureWebHost Override — Core Test Infrastructure
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Overrides the CRM service's web host configuration for test isolation.
        /// Replaces production infrastructure (PostgreSQL connection, Redis, MassTransit,
        /// background jobs) with test doubles while preserving business logic behavior.
        ///
        /// <para><b>Configuration overrides applied:</b></para>
        /// <list type="number">
        ///   <item>Environment set to "Testing" for test-specific configuration loading</item>
        ///   <item>In-memory configuration replaces appsettings.json values</item>
        ///   <item>Npgsql legacy timestamp behavior preserved (Startup.cs line 27)</item>
        ///   <item>Redis → in-memory distributed cache</item>
        ///   <item>MassTransit → InMemory test harness with consumer auto-discovery</item>
        ///   <item>SearchService → MockSearchService for controlled test behavior</item>
        ///   <item>JWT Bearer → test signing key/issuer/audience</item>
        ///   <item>Background job hosted services → removed</item>
        /// </list>
        /// </summary>
        /// <param name="builder">The web host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // ── 1. Set hosting environment to "Testing" ──────────────
            // Ensures test-specific configuration and middleware behavior.
            builder.UseEnvironment("Testing");

            // ── 2. Override application configuration ─────────────────
            // Replaces appsettings.json values with test-specific configuration.
            // All values match the monolith's Config.json for backward compatibility
            // except ConnectionStrings:Default which uses the Testcontainer string.
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    // Database: Testcontainer connection replaces Config.json line 3
                    ["ConnectionStrings:Default"] = _postgresConnectionString,

                    // JWT: Must match Config.json for token compatibility
                    ["Jwt:Key"] = TestJwtKey,
                    ["Jwt:Issuer"] = TestJwtIssuer,
                    ["Jwt:Audience"] = TestJwtAudience,

                    // CRM service settings: Match Config.json lines 4-8
                    ["CrmService:DevelopmentMode"] = "true",
                    ["CrmService:EncryptionKey"] = TestEncryptionKey,
                    ["CrmService:Lang"] = "en",
                    ["CrmService:Locale"] = "en-US",

                    // Background jobs: Disabled for test isolation
                    // Maps Config.json line 9 "EnableBackgroungJobs" (sic)
                    ["Jobs:Enabled"] = "false",

                    // Redis: Placeholder — replaced with in-memory cache in services
                    ["Redis:ConnectionString"] = "not-used-in-tests",

                    // Messaging: InMemory transport for MassTransit test harness
                    ["Messaging:Transport"] = "InMemory"
                });
            });

            // ── 3. Override service registrations ─────────────────────
            // Replaces production services with test doubles.
            builder.ConfigureTestServices(services =>
            {
                // ── 3a. Npgsql legacy timestamp behavior ──────────────
                // CRITICAL: Preserved from monolith Startup.cs line 27.
                // Must be set before ANY Npgsql connections are created.
                // The CRM service's Program.cs also sets this, but we set it
                // again here for defense-in-depth in test configuration.
                AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

                // ── 3b. Replace Redis with in-memory distributed cache ─
                // Removes StackExchange.Redis-backed IDistributedCache and
                // replaces with MemoryDistributedCache for test isolation.
                // No external Redis instance required during tests.
                services.RemoveAll<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
                services.AddDistributedMemoryCache();

                // ── 3c. Replace MassTransit with InMemory test harness ─
                // Replaces production RabbitMQ/SQS transport with in-memory
                // transport for event publisher/subscriber testing without
                // external message broker dependencies.
                // AddConsumers auto-discovers CRM event consumers from the
                // service assembly for in-process event verification.
                services.AddMassTransitTestHarness(cfg =>
                {
                    cfg.AddConsumers(typeof(Program).Assembly);
                });

                // ── 3d. Replace SearchService with MockSearchService ───
                // Removes the real SearchService (which depends on
                // ICrmEntityRelationManager, ICrmEntityManager, ICrmRecordManager,
                // and executes EQL queries) and registers the configurable mock.
                // This isolates controller and event handler tests from search
                // indexing infrastructure. Tests access the mock via the
                // MockSearchService property for call verification.
                services.RemoveAll<SearchService>();
                services.AddSingleton<MockSearchService>(_mockSearchService);

                // ── 3e. Configure JWT Bearer authentication ────────────
                // PostConfigure runs AFTER the CRM service's JWT configuration,
                // ensuring test values override any production defaults.
                // Token validation parameters match monolith Config.json:
                //   Key:      "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"
                //   Issuer:   "webvella-erp"
                //   Audience: "webvella-erp"
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidateAudience = true,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            ValidIssuer = TestJwtIssuer,
                            ValidAudience = TestJwtAudience,
                            IssuerSigningKey = new SymmetricSecurityKey(
                                Encoding.UTF8.GetBytes(TestJwtKey))
                        };
                    });

                // ── 3f. Remove background job hosted services ──────────
                // Selectively removes application-level hosted services
                // (job processors, schedule managers) while preserving
                // MassTransit bus control and Microsoft framework services.
                // This prevents background processing during integration tests
                // that could cause non-deterministic behavior.
                RemoveNonEssentialHostedServices(services);
            });
        }

        // ──────────────────────────────────────────────────────────────
        //  Private Helpers
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Removes application-level <see cref="IHostedService"/> registrations
        /// (background job processors, schedule managers) from the service collection
        /// while preserving MassTransit bus control and Microsoft framework hosted services.
        ///
        /// <para>
        /// This prevents background processing during integration tests that could cause
        /// non-deterministic behavior, race conditions, or unintended side effects.
        /// MassTransit's hosted services are kept because the InMemory test harness
        /// relies on them for bus lifecycle management.
        /// </para>
        /// </summary>
        /// <param name="services">The service collection to filter.</param>
        private static void RemoveNonEssentialHostedServices(IServiceCollection services)
        {
            var hostedServiceDescriptors = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();

            foreach (var descriptor in hostedServiceDescriptors)
            {
                // Determine the implementation type from the service descriptor.
                // Registrations can use ImplementationType (concrete type),
                // ImplementationInstance (singleton instance), or
                // ImplementationFactory (factory delegate).
                var implType = descriptor.ImplementationType
                    ?? descriptor.ImplementationInstance?.GetType();

                if (implType == null)
                {
                    // Factory-registered hosted services cannot be inspected.
                    // Keep them as a safe default — they may be MassTransit's
                    // bus control or other framework-essential services.
                    continue;
                }

                var fullName = implType.FullName ?? implType.Name;

                // Keep MassTransit hosted services (bus control, outbox delivery,
                // health checks, etc.) — required for InMemory test harness operation.
                if (fullName.Contains("MassTransit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Keep Microsoft framework hosted services (generic host internals,
                // health check publisher, etc.).
                if (fullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Remove application-level hosted services:
                // - JobManager/JobPool background workers
                // - ScheduleManager polling services
                // - Any CRM-specific background processors
                services.Remove(descriptor);
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Dispose Override
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Releases resources used by the <see cref="CrmWebApplicationFactory"/>.
        /// Calls <see cref="WebApplicationFactory{TEntryPoint}.Dispose(bool)"/>
        /// to stop the test server and dispose the underlying service provider.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            // Base disposal handles:
            // - Stopping the TestServer
            // - Disposing the IHost
            // - Disposing the root IServiceProvider
            // - Cleaning up HttpClient instances
            base.Dispose(disposing);
        }
    }
}
