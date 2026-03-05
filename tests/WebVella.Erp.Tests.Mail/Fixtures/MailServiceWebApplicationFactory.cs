// ============================================================================
// MailServiceWebApplicationFactory.cs — Custom WebApplicationFactory for
// Mail/Notification Microservice Integration Tests
// ============================================================================
// Provides an isolated, containerized test environment that bootstraps the
// entire Mail microservice in an in-memory test server with:
//   - Testcontainers PostgreSQL (postgres:16-alpine) for real DB integration
//   - MassTransit in-memory test harness replacing RabbitMQ/SQS transport
//   - JWT Bearer auth override with a fixed test signing key
//   - In-memory distributed cache replacing Redis
//   - Database seeding with SMTP service and email records in various states
//
// Source references:
//   - WebVella.Erp.Site.Mail/Startup.cs lines 24-73 (DI patterns)
//   - WebVella.Erp.Site.Mail/Startup.cs line 27 (Npgsql legacy timestamp)
//   - WebVella.Erp.Plugins.Mail/MailPlugin.cs (schedule plan ID, interval)
//   - WebVella.Erp.Plugins.Mail/Api/SmtpService.cs (14 model properties)
//   - WebVella.Erp.Plugins.Mail/Api/Email.cs (17 model properties)
//
// AAP References:
//   - AAP 0.7.4: Docker Compose stack (postgres:16-alpine image)
//   - AAP 0.8.1: JWT authentication backward compatibility
//   - AAP 0.8.2: xunit 2.9.3 + Testcontainers.PostgreSql 4.10.0
//   - AAP 0.6.2: Import transformation rules (no old monolith namespaces)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using WebVella.Erp.Service.Mail;
using WebVella.Erp.Service.Mail.Database;
using Xunit;

namespace WebVella.Erp.Tests.Mail.Fixtures
{
    /// <summary>
    /// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> for Mail Service integration tests.
    /// Bootstraps the entire Mail/Notification microservice in an in-memory test server backed by
    /// an isolated Testcontainers PostgreSQL database (postgres:16-alpine), an in-memory MassTransit
    /// test harness, and JWT Bearer authentication with a fixed test signing key.
    ///
    /// Usage:
    /// <code>
    /// public class MailControllerTests : IClassFixture&lt;MailServiceWebApplicationFactory&gt;
    /// {
    ///     private readonly MailServiceWebApplicationFactory _factory;
    ///     public MailControllerTests(MailServiceWebApplicationFactory factory) => _factory = factory;
    ///
    ///     [Fact]
    ///     public async Task GetEmails_ReturnsOk()
    ///     {
    ///         var client = _factory.CreateAuthenticatedClient();
    ///         var response = await client.GetAsync("/api/v3/en_US/mail/emails");
    ///         response.EnsureSuccessStatusCode();
    ///     }
    /// }
    /// </code>
    ///
    /// Implements <see cref="IAsyncLifetime"/> for async setup/teardown of Docker containers:
    ///   - <see cref="InitializeAsync"/>: Builds and starts PostgreSQL container, seeds test data
    ///   - <see cref="IAsyncLifetime.DisposeAsync"/>: Stops and disposes the PostgreSQL container
    /// </summary>
    public class MailServiceWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        #region Constants

        /// <summary>
        /// Fixed test signing key for JWT token generation and validation.
        /// Must be at least 32 bytes (256 bits) for HMAC-SHA256 algorithm.
        /// Used in both <see cref="ConfigureWebHost"/> (JWT Bearer override) and
        /// <see cref="GenerateTestJwtToken"/> (token creation) to ensure consistency.
        /// </summary>
        private const string TestSigningKey =
            "TestSecretKeyForMailServiceIntegrationTests_MustBe32Bytes!";

        /// <summary>
        /// Default test encryption key matching the format from the monolith's Config.json
        /// (Settings:EncryptionKey). 64 hex characters = 32-byte key. Used to satisfy
        /// SharedKernel's ErpSettings.Initialize() during test server startup.
        /// </summary>
        private const string TestEncryptionKey =
            "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658";

        #endregion

        #region Fields

        /// <summary>
        /// Testcontainers PostgreSQL container providing an isolated database instance
        /// for each test run. Uses postgres:16-alpine image matching the AAP 0.7.4
        /// Docker Compose stack specification. Container is started in
        /// <see cref="InitializeAsync"/> and disposed in <see cref="IAsyncLifetime.DisposeAsync"/>.
        /// </summary>
        private PostgreSqlContainer _postgresContainer;

        /// <summary>
        /// Tracks whether the PostgreSQL container has been initialized to prevent
        /// double-start scenarios and ensure proper cleanup ordering.
        /// </summary>
        private bool _containerInitialized;

        #endregion

        #region WebApplicationFactory Override

        /// <summary>
        /// Configures the test web host with service overrides that replace production
        /// infrastructure (PostgreSQL connection, RabbitMQ/SQS transport, Redis cache,
        /// JWT authentication) with test-appropriate implementations.
        ///
        /// Override sequence:
        ///   1. App configuration — inject test connection string, encryption key, JWT settings
        ///   2. Database — replace MailDbContext with Testcontainers PostgreSQL connection
        ///   3. MassTransit — replace real transport with in-memory test harness
        ///   4. JWT Bearer — override token validation with relaxed test parameters
        ///   5. Redis Cache — replace with in-memory distributed cache
        ///   6. Background Jobs — disable via configuration to prevent interference
        /// </summary>
        /// <param name="builder">The web host builder to configure.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // ---------------------------------------------------------------
            // 1. Override application configuration with test-specific values.
            //    These settings satisfy ErpSettings.Initialize() and bridge
            //    configuration for SharedKernel utilities during test startup.
            // ---------------------------------------------------------------
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var testConnectionString = _postgresContainer.GetConnectionString();

                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    // Database connection — points to Testcontainers PostgreSQL
                    ["ConnectionStrings:Default"] = testConnectionString,

                    // Legacy Settings:* keys for ErpSettings.Initialize() compatibility
                    ["Settings:ConnectionString"] = testConnectionString,
                    ["Settings:EncryptionKey"] = TestEncryptionKey,
                    ["Settings:Lang"] = "en",
                    ["Settings:Locale"] = "en-US",
                    ["Settings:TimeZoneName"] = "UTC",
                    ["Settings:DevelopmentMode"] = "true",
                    ["Settings:EnableBackgroungJobs"] = "false",
                    ["Settings:EnableFileSystemStorage"] = "false",

                    // New microservice configuration paths (bridged by Program.cs)
                    ["Security:EncryptionKey"] = TestEncryptionKey,
                    ["Localization:Lang"] = "en",
                    ["Localization:Locale"] = "en-US",
                    ["Localization:TimeZoneName"] = "UTC",
                    ["Storage:EnableFileSystemStorage"] = "false",

                    // Disable background jobs during integration tests to prevent
                    // mail queue processing from modifying seeded test data
                    ["Jobs:Enabled"] = "false",

                    // JWT configuration matching the test signing key
                    ["Jwt:Key"] = TestSigningKey,
                    ["Jwt:Issuer"] = "test-issuer",
                    ["Jwt:Audience"] = "test-audience",
                    ["Jwt:RequireHttpsMetadata"] = "false",

                    // Redis — config value provided for key resolution; actual cache is replaced by in-memory implementation
                    ["Redis:ConnectionString"] = "localhost:6379",
                    ["Redis:InstanceName"] = "MailServiceTest_",

                    // Messaging — set transport hint for any config-based branching
                    ["Messaging:Transport"] = "InMemory"
                });
            });

            // ---------------------------------------------------------------
            // 2. Override DI service registrations for test isolation.
            //    ConfigureTestServices runs AFTER the main Program.cs service
            //    registration, allowing targeted replacement of production
            //    infrastructure with test-appropriate implementations.
            // ---------------------------------------------------------------
            builder.ConfigureTestServices(services =>
            {
                OverrideDatabaseServices(services);
                OverrideMassTransitServices(services);
                OverrideJwtBearerAuthentication(services);
                OverrideDistributedCache(services);
                DisableHostedServices(services);
            });
        }

        #endregion

        #region Service Override Methods

        /// <summary>
        /// Replaces the production MailDbContext registration (pointing to the real
        /// erp_mail database) with one that uses the Testcontainers PostgreSQL instance.
        /// Preserves the Npgsql configuration options from the real service:
        ///   - MinBatchSize(1) — from Program.cs line 181
        ///   - CommandTimeout(120) — from Program.cs line 182
        /// </summary>
        private void OverrideDatabaseServices(IServiceCollection services)
        {
            // Remove existing EF Core DbContext registrations
            services.RemoveAll<MailDbContext>();
            services.RemoveAll(typeof(DbContextOptions<MailDbContext>));

            // Remove all DbContextOptions registrations that might reference the
            // production connection string to prevent EF Core from using stale config.
            var dbContextOptionDescriptors = services
                .Where(d => d.ServiceType.IsGenericType &&
                            d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
                .ToList();
            foreach (var descriptor in dbContextOptionDescriptors)
            {
                services.Remove(descriptor);
            }

            // Re-register MailDbContext with the Testcontainers connection string
            var connectionString = _postgresContainer.GetConnectionString();
            services.AddDbContext<MailDbContext>(options =>
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.MinBatchSize(1);
                    npgsqlOptions.CommandTimeout(120);
                }));
        }

        /// <summary>
        /// Replaces the production MassTransit configuration (RabbitMQ or Amazon SQS/SNS
        /// transport) with the MassTransit in-memory test harness. The test harness:
        ///   - Provides ITestHarness for asserting published/consumed events
        ///   - Preserves all IConsumer&lt;T&gt; registrations from the real service
        ///   - Uses in-memory transport for isolated, fast test execution
        ///
        /// This follows MassTransit 8.x testing best practices where
        /// AddMassTransitTestHarness() is called in ConfigureTestServices to
        /// automatically replace the real transport.
        /// </summary>
        private static void OverrideMassTransitServices(IServiceCollection services)
        {
            services.AddMassTransitTestHarness();
        }

        /// <summary>
        /// Overrides the JWT Bearer authentication configuration with relaxed
        /// token validation parameters using a fixed test signing key. This allows
        /// test-generated JWT tokens to be accepted without requiring a real
        /// identity provider or Core service token issuance.
        ///
        /// Validation overrides:
        ///   - ValidateIssuer=false — no issuer check needed for tests
        ///   - ValidateAudience=false — no audience check needed for tests
        ///   - ValidateLifetime=false — tests don't need token expiry enforcement
        ///   - IssuerSigningKey matches <see cref="TestSigningKey"/> for consistency
        ///     with <see cref="GenerateTestJwtToken"/>
        ///
        /// Preserves AAP 0.8.1 requirement: JWT authentication pattern remains
        /// compatible; only the validation strictness is relaxed for testing.
        /// </summary>
        private static void OverrideJwtBearerAuthentication(IServiceCollection services)
        {
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    var signingKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(TestSigningKey));

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = false,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = signingKey
                    };
                });
        }

        /// <summary>
        /// Replaces the production Redis distributed cache (StackExchange.Redis)
        /// with an in-memory distributed cache implementation. This avoids the need
        /// for a Redis container during integration tests while preserving the
        /// IDistributedCache interface contract used by SmtpService for SMTP config
        /// caching (1-hour TTL per AAP 0.8.3).
        /// </summary>
        private static void OverrideDistributedCache(IServiceCollection services)
        {
            services.RemoveAll<IDistributedCache>();
            services.AddDistributedMemoryCache();
        }

        /// <summary>
        /// Removes hosted background services (ProcessMailQueueJob) to prevent them
        /// from interfering with test execution by modifying seeded email records.
        /// The Jobs:Enabled=false configuration override provides a primary guard,
        /// and this removal provides a secondary safety net.
        /// </summary>
        private static void DisableHostedServices(IServiceCollection services)
        {
            // Remove all IHostedService registrations that are background jobs.
            // This prevents ProcessMailQueueJob from processing the mail queue
            // during integration tests, which would modify seeded Pending emails.
            var hostedServiceDescriptors = services
                .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                            d.ImplementationType != null &&
                            d.ImplementationType.Name.Contains("ProcessMailQueue"))
                .ToList();

            foreach (var descriptor in hostedServiceDescriptors)
            {
                services.Remove(descriptor);
            }
        }

        #endregion

        #region IAsyncLifetime Implementation

        /// <summary>
        /// Asynchronously initializes the test environment:
        ///   1. Sets Npgsql legacy timestamp behavior (preserving monolith pattern)
        ///   2. Builds and starts a PostgreSQL 16 container via Testcontainers
        ///   3. Initializes the database schema (rec_smtp_service, rec_email tables)
        ///   4. Seeds test data via DatabaseSeeder (SMTP services + emails in various states)
        ///
        /// Called by xUnit before any tests in the fixture class execute.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Preserve Npgsql legacy timestamp behavior from Startup.cs line 27.
            // Must be set before any Npgsql connection is opened.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            // Build and start the PostgreSQL Testcontainer.
            // Image: postgres:16-alpine (matching AAP 0.7.4 Docker Compose stack)
            // Database: erp_mail_test (isolated test database)
            _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("erp_mail_test")
                .WithUsername("test_user")
                .WithPassword("test_password")
                .Build();

            await _postgresContainer.StartAsync();
            _containerInitialized = true;

            // Verify database connectivity before proceeding with schema setup
            var connectionString = _postgresContainer.GetConnectionString();
            await VerifyDatabaseConnectivityAsync(connectionString);

            // Initialize schema and seed test data.
            // DatabaseSeeder creates rec_smtp_service and rec_email tables with
            // all columns matching the monolith's entity definitions (14 + 17 columns),
            // then inserts test SMTP services and emails in Pending/Sent/Aborted states.
            await DatabaseSeeder.InitializeSchemaAsync(connectionString);
            await DatabaseSeeder.SeedAsync(connectionString);
        }

        /// <summary>
        /// Asynchronously disposes the test environment by stopping and disposing
        /// the PostgreSQL Testcontainer. Called by xUnit after all tests in the
        /// fixture class have completed.
        ///
        /// Note: The base <see cref="WebApplicationFactory{TEntryPoint}"/> disposal
        /// is handled separately by xUnit through the IDisposable/IAsyncDisposable
        /// interface chain.
        /// </summary>
        async Task IAsyncLifetime.DisposeAsync()
        {
            if (_containerInitialized && _postgresContainer != null)
            {
                await _postgresContainer.DisposeAsync();
                _containerInitialized = false;
            }
        }

        #endregion

        #region Public Helper Methods

        /// <summary>
        /// Generates a valid JWT token for test authentication against the Mail service.
        /// The token uses the same fixed signing key configured in
        /// <see cref="OverrideJwtBearerAuthentication"/>, ensuring tokens generated
        /// by this method are accepted by the test server's JWT Bearer middleware.
        ///
        /// Token structure:
        ///   - Algorithm: HMAC-SHA256 (symmetric key)
        ///   - Claims: sub (user ID), name (username), role (one per role)
        ///   - Expiry: 1 hour from now (ample time for test execution)
        ///
        /// Maps to AAP 0.7.2 SecurityContext JWT propagation requirement:
        /// cross-service identity is carried via JWT claims instead of AsyncLocal.
        /// </summary>
        /// <param name="userId">
        /// The user's unique identifier, set as the 'sub' (NameIdentifier) claim.
        /// Corresponds to the ErpUser.Id GUID in the monolith's SecurityContext.
        /// </param>
        /// <param name="username">
        /// The user's display name, set as the 'name' claim.
        /// Corresponds to ErpUser.Username in the monolith.
        /// </param>
        /// <param name="roles">
        /// Array of role names to include as 'role' claims. Each role becomes a
        /// separate ClaimTypes.Role claim for ASP.NET Core authorization checks.
        /// Common test values: "administrator", "regular", "guest".
        /// </param>
        /// <returns>
        /// A compact JWT string (header.payload.signature) suitable for use in
        /// HTTP Authorization headers: <c>Authorization: Bearer {token}</c>
        /// </returns>
        public string GenerateTestJwtToken(
            Guid userId,
            string username = "test-admin",
            string[] roles = null)
        {
            var resolvedRoles = roles ?? new[] { "administrator" };

            var signingKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(TestSigningKey));
            var credentials = new SigningCredentials(
                signingKey, SecurityAlgorithms.HmacSha256Signature);

            // Build claims matching the JWT structure expected by the Mail service's
            // authorization middleware and any SharedKernel SecurityContext integration.
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, username)
            };

            foreach (var role in resolvedRoles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = credentials
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var securityToken = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(securityToken);
        }

        /// <summary>
        /// Returns the PostgreSQL connection string for the running Testcontainer.
        /// Useful for direct database assertions in integration tests (e.g., verifying
        /// that a controller endpoint correctly persisted an email record).
        /// </summary>
        /// <returns>
        /// A Npgsql-compatible connection string pointing to the test PostgreSQL container.
        /// Format: <c>Host=localhost;Port={dynamic};Database=erp_mail_test;Username=test_user;Password=test_password</c>
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if called before <see cref="InitializeAsync"/> has completed.
        /// </exception>
        public string GetConnectionString()
        {
            if (_postgresContainer == null || !_containerInitialized)
            {
                throw new InvalidOperationException(
                    "PostgreSQL container has not been initialized. " +
                    "Ensure InitializeAsync() has completed before calling GetConnectionString().");
            }

            return _postgresContainer.GetConnectionString();
        }

        /// <summary>
        /// Resolves the MassTransit <see cref="ITestHarness"/> from the test server's
        /// DI container. The test harness provides methods for asserting that domain
        /// events were published or consumed during integration test execution.
        ///
        /// Usage:
        /// <code>
        /// var harness = _factory.GetTestHarness();
        /// Assert.True(await harness.Published.Any&lt;EmailSentEvent&gt;());
        /// </code>
        /// </summary>
        /// <returns>
        /// The MassTransit test harness instance configured for the Mail service.
        /// </returns>
        public ITestHarness GetTestHarness()
        {
            return Services.GetRequiredService<ITestHarness>();
        }

        /// <summary>
        /// Creates an <see cref="HttpClient"/> pre-configured with a valid JWT Bearer
        /// authorization header for authenticated test requests against Mail service
        /// controller endpoints.
        ///
        /// The generated JWT token includes:
        ///   - sub (NameIdentifier): userId or a new random GUID
        ///   - name: specified username (default "test-admin")
        ///   - role: specified roles (default ["administrator"])
        ///
        /// Usage:
        /// <code>
        /// // Default admin client
        /// var client = _factory.CreateAuthenticatedClient();
        ///
        /// // Custom user with specific roles
        /// var client = _factory.CreateAuthenticatedClient(
        ///     userId: someGuid,
        ///     username: "regular-user",
        ///     roles: new[] { "regular" });
        /// </code>
        /// </summary>
        /// <param name="userId">
        /// Optional user ID for the JWT token. Defaults to a new random GUID.
        /// </param>
        /// <param name="username">
        /// Username for the JWT 'name' claim. Defaults to "test-admin".
        /// </param>
        /// <param name="roles">
        /// Role names for the JWT 'role' claims. Defaults to ["administrator"].
        /// </param>
        /// <returns>
        /// An HttpClient with the Authorization header set to <c>Bearer {token}</c>.
        /// </returns>
        public HttpClient CreateAuthenticatedClient(
            Guid? userId = null,
            string username = "test-admin",
            string[] roles = null)
        {
            var resolvedUserId = userId ?? Guid.NewGuid();
            var resolvedRoles = roles ?? new[] { "administrator" };

            var token = GenerateTestJwtToken(resolvedUserId, username, resolvedRoles);
            var client = CreateClient();

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Verifies that the PostgreSQL Testcontainer is accepting connections
        /// by opening and immediately closing a connection. This catch-early
        /// approach provides a clear error message if the container failed to
        /// start properly, rather than letting the first test timeout with
        /// an opaque connection failure.
        /// </summary>
        /// <param name="connectionString">
        /// The Npgsql connection string to verify.
        /// </param>
        private static async Task VerifyDatabaseConnectivityAsync(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Execute a simple query to verify the database is fully ready
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync();
        }

        #endregion
    }
}
