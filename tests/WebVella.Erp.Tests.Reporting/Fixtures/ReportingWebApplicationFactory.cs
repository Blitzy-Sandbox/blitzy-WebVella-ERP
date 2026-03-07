using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MassTransit;
using MassTransit.Testing;
using WebVella.Erp.Service.Reporting;
using WebVella.Erp.Service.Reporting.Database;
using Xunit;

namespace WebVella.Erp.Tests.Reporting.Fixtures
{
    /// <summary>
    /// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> specifically configured for the
    /// Reporting microservice integration tests. Hosts the Reporting service in-process with
    /// test-specific overrides for database, authentication, caching, and messaging.
    ///
    /// <para><b>Test Infrastructure Responsibilities:</b></para>
    /// <list type="bullet">
    ///   <item>Replaces the production PostgreSQL connection with a Testcontainer-provided connection string</item>
    ///   <item>Replaces Redis distributed cache with in-memory implementation</item>
    ///   <item>Replaces MassTransit (RabbitMQ/AmazonSQS) with InMemory test harness</item>
    ///   <item>Configures JWT Bearer authentication with test credentials matching monolith Config.json</item>
    ///   <item>Disables background jobs for test isolation</item>
    ///   <item>Provides authenticated HTTP client creation and MassTransit test harness access</item>
    /// </list>
    ///
    /// <para><b>Testcontainer Management:</b></para>
    /// This factory does NOT create or manage Docker containers. The PostgreSQL Testcontainer
    /// lifecycle is owned by <c>ReportingDbContextFixture</c>, which passes the live connection
    /// string to this factory's constructor.
    ///
    /// <para><b>JWT Configuration:</b></para>
    /// JWT Key, Issuer, and Audience values MUST exactly match the monolith's Config.json
    /// (lines 24-28) and Startup.cs (lines 102-114) for backward-compatible token validation:
    /// <code>
    /// Key:      "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"
    /// Issuer:   "webvella-erp"
    /// Audience: "webvella-erp"
    /// </code>
    ///
    /// <para><b>Sibling Pattern:</b></para>
    /// Follows the same pattern as <c>CoreServiceWebApplicationFactory</c> in
    /// <c>Tests.Core/Fixtures</c> and <c>MailServiceWebApplicationFactory</c> in
    /// <c>Tests.Mail/Fixtures</c>.
    ///
    /// <para><b>Source References:</b></para>
    /// <list type="bullet">
    ///   <item><c>WebVella.Erp.Site/Startup.cs</c> — JWT Bearer setup (lines 102-114), Npgsql legacy timestamp (line 40)</item>
    ///   <item><c>WebVella.Erp.Site/Config.json</c> — JWT configuration values (lines 24-28)</item>
    ///   <item><c>WebVella.Erp/ERPService.cs</c> — SystemIds: FirstUserId (line 462), AdministratorRoleId</item>
    ///   <item><c>WebVella.Erp/Api/Definitions.cs</c> — Well-known GUIDs for system users and roles</item>
    /// </list>
    /// </summary>
    public class ReportingWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
    {
        #region JWT Constants — MUST match monolith Config.json lines 24-28 exactly

        /// <summary>
        /// HMAC-SHA256 signing key for test JWT tokens.
        /// Source: <c>Config.json</c> line 25 — <c>"Key": "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"</c>
        /// </summary>
        public const string TestJwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// JWT token issuer for test tokens.
        /// Source: <c>Config.json</c> line 26 — <c>"Issuer": "webvella-erp"</c>
        /// </summary>
        public const string TestJwtIssuer = "webvella-erp";

        /// <summary>
        /// JWT token audience for test tokens.
        /// Source: <c>Config.json</c> line 27 — <c>"Audience": "webvella-erp"</c>
        /// </summary>
        public const string TestJwtAudience = "webvella-erp";

        #endregion

        #region Fields

        /// <summary>
        /// PostgreSQL connection string pointing to the Testcontainer database instance.
        /// Injected via constructor from <c>ReportingDbContextFixture</c> which manages
        /// the Testcontainer lifecycle.
        /// </summary>
        private readonly string _connectionString;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new <see cref="ReportingWebApplicationFactory"/> with the specified
        /// PostgreSQL connection string from a Testcontainer-managed database instance.
        /// </summary>
        /// <param name="connectionString">
        /// The PostgreSQL connection string for the test database, provided by
        /// <c>ReportingDbContextFixture</c> via <c>PostgreSqlContainer.GetConnectionString()</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connectionString"/> is null or empty.
        /// </exception>
        public ReportingWebApplicationFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString),
                    "A valid PostgreSQL connection string is required for the Reporting service test factory.");
            }

            _connectionString = connectionString;
        }

        #endregion

        #region WebApplicationFactory Override — Test Host Configuration

        /// <summary>
        /// Configures the web host for testing by overriding:
        /// <list type="number">
        ///   <item>Environment to "Testing"</item>
        ///   <item>Configuration with in-memory test values (database, JWT, Redis, messaging, jobs)</item>
        ///   <item>Service registrations: ReportingDbContext, IDistributedCache, MassTransit, JWT, logging</item>
        /// </list>
        ///
        /// The override order ensures that test configuration takes precedence over
        /// <c>appsettings.json</c> values set in the Reporting service's <c>Program.cs</c>.
        /// </summary>
        /// <param name="builder">The web host builder to configure for testing.</param>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // ================================================================
            // 5.1: SET TEST ENVIRONMENT
            // Ensures any environment-specific configuration or behavior
            // (e.g., developer exception pages, HTTPS redirection) is
            // appropriate for the test context.
            // ================================================================
            builder.UseEnvironment("Testing");

            // ================================================================
            // 5.2: CONFIGURATION OVERRIDES
            // Replace production configuration values with test-specific settings.
            // These override the Reporting service's appsettings.json values loaded
            // in Program.cs. The in-memory collection takes highest precedence.
            //
            // - ConnectionStrings:Default → Testcontainer PostgreSQL
            // - Jwt:Key/Issuer/Audience → MUST match monolith Config.json lines 24-28
            // - Redis:ConnectionString → Not used (replaced by in-memory cache)
            // - Messaging:Transport → InMemory (MassTransit test harness)
            // - Jobs:Enabled → false (disabled for test isolation)
            // ================================================================
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["ConnectionStrings:Default"] = _connectionString,
                    ["Jwt:Key"] = TestJwtKey,
                    ["Jwt:Issuer"] = TestJwtIssuer,
                    ["Jwt:Audience"] = TestJwtAudience,
                    ["Redis:ConnectionString"] = "not-used-in-tests",
                    ["Messaging:Transport"] = "InMemory",
                    ["Jobs:Enabled"] = "false"
                });
            });

            // ================================================================
            // 5.3 + 5.4: SERVICE REGISTRATION OVERRIDES
            // Replace production service registrations with test-appropriate
            // implementations using ConfigureTestServices (executes after the
            // service's own DI configuration in Program.cs).
            // ================================================================
            builder.ConfigureTestServices(services =>
            {
                // -----------------------------------------------------------
                // 1. RE-REGISTER ReportingDbContext WITH TEST CONNECTION STRING
                // Remove the existing DbContextOptions<ReportingDbContext>
                // registration (added by Program.cs line 178:
                //   builder.Services.AddDbContext<ReportingDbContext>(options =>
                //       options.UseNpgsql(...)))
                // and re-register with the Testcontainer connection string.
                // -----------------------------------------------------------
                var dbContextDescriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<ReportingDbContext>));
                if (dbContextDescriptor != null)
                {
                    services.Remove(dbContextDescriptor);
                }

                services.AddDbContext<ReportingDbContext>(options =>
                    options.UseNpgsql(_connectionString));

                // -----------------------------------------------------------
                // 2. REPLACE DISTRIBUTED CACHE WITH IN-MEMORY IMPLEMENTATION
                // The Reporting service registers Redis via
                // AddStackExchangeRedisCache() in Program.cs (line 187).
                // For tests, replace all IDistributedCache registrations with
                // a local in-memory implementation to avoid requiring a Redis
                // container during test execution.
                // -----------------------------------------------------------
                services.RemoveAll<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
                services.AddDistributedMemoryCache();

                // -----------------------------------------------------------
                // 3. REPLACE MASSTRANSIT WITH IN-MEMORY TEST HARNESS
                // The Reporting service registers MassTransit with
                // RabbitMQ/AmazonSQS transport in Program.cs (lines 207-237).
                // For tests, replace with the in-memory test harness which
                // allows event publish/consume verification without external
                // message broker dependencies.
                //
                // AddConsumers(typeof(Program).Assembly) auto-discovers all
                // consumers in the Reporting service assembly:
                //   - TimelogCreatedConsumer
                //   - TimelogDeletedConsumer
                //   - TaskUpdatedConsumer
                //   - ProjectUpdatedConsumer
                // -----------------------------------------------------------
                services.AddMassTransitTestHarness(cfg =>
                {
                    cfg.AddConsumers(typeof(Program).Assembly);
                });

                // -----------------------------------------------------------
                // 4. NPGSQL LEGACY TIMESTAMP BEHAVIOR
                // Preserved from monolith Startup.cs line 40:
                //   AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
                // Required for backward-compatible DateTime handling with
                // PostgreSQL timestamp columns. The Reporting service's
                // Program.cs also sets this (line 60), but we set it here
                // as well to ensure it takes effect in the test process.
                // -----------------------------------------------------------
                AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

                // -----------------------------------------------------------
                // 5. CONFIGURE TEST LOGGING
                // Replace default logging configuration with minimal test
                // output: clear all providers, add console for visibility,
                // and set minimum level to Warning to suppress verbose logs
                // during test execution.
                // -----------------------------------------------------------
                services.AddLogging(loggingBuilder =>
                {
                    loggingBuilder.ClearProviders();
                    loggingBuilder.AddConsole();
                    loggingBuilder.SetMinimumLevel(LogLevel.Warning);
                });

                // -----------------------------------------------------------
                // 5.4: JWT AUTHENTICATION OVERRIDE
                // Override the JWT Bearer token validation parameters to use
                // the test JWT credentials. This PostConfigure runs after
                // the Reporting service's own AddJwtBearer() configuration
                // in Program.cs (lines 145-163), ensuring test tokens signed
                // with TestJwtKey are accepted.
                //
                // TokenValidationParameters MUST match the monolith's
                // Startup.cs lines 104-113 pattern exactly:
                //   ValidateIssuer = true
                //   ValidateAudience = true
                //   ValidateLifetime = true
                //   ValidateIssuerSigningKey = true
                //   ValidIssuer = "webvella-erp"
                //   ValidAudience = "webvella-erp"
                //   IssuerSigningKey = HMAC-SHA256 key from TestJwtKey
                // -----------------------------------------------------------
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme, options =>
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
            });
        }

        #endregion

        #region JWT Token Generation — Test Authentication Helpers

        /// <summary>
        /// Generates a JWT token for the default administrator user, matching the
        /// monolith's first/admin user created in <c>ERPService.cs</c> lines 462-476.
        ///
        /// <para>Admin User Details (from <c>ERPService.cs</c> and <c>Definitions.cs</c>):</para>
        /// <list type="bullet">
        ///   <item>UserId: <c>EABD66FD-8DE1-4D79-9674-447EE89921C2</c> (SystemIds.FirstUserId)</item>
        ///   <item>Username: "administrator"</item>
        ///   <item>Email: "erp@webvella.com"</item>
        ///   <item>Roles: Administrator (<c>BDC56420-CAF0-4030-8A0E-D264938E0CDA</c>)</item>
        /// </list>
        /// </summary>
        /// <returns>A compact JWT string valid for 1 hour, signed with <see cref="TestJwtKey"/>.</returns>
        public string GenerateAdminJwtToken()
        {
            return GenerateJwtToken(
                new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2"),  // SystemIds.FirstUserId
                "administrator",
                "erp@webvella.com",
                new[] { new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA") }  // AdministratorRoleId
            );
        }

        /// <summary>
        /// Generates a JWT token with the specified user identity and role claims.
        /// The token structure matches the monolith's JWT token format for cross-service
        /// token propagation (AAP 0.8.3).
        ///
        /// <para><b>Claims included:</b></para>
        /// <list type="bullet">
        ///   <item><c>sub</c> — User UUID (JwtRegisteredClaimNames.Sub)</item>
        ///   <item><c>email</c> — User email address (JwtRegisteredClaimNames.Email)</item>
        ///   <item><c>username</c> — Custom claim for user display name</item>
        ///   <item><c>jti</c> — Unique token identifier (JwtRegisteredClaimNames.Jti)</item>
        ///   <item><c>role</c> — One claim per role GUID (ClaimTypes.Role)</item>
        /// </list>
        ///
        /// <para><b>Token signing:</b> HMAC-SHA256 with <see cref="TestJwtKey"/>.</para>
        /// <para><b>Token lifetime:</b> 1 hour from UTC now.</para>
        /// </summary>
        /// <param name="userId">The user's unique identifier (UUID).</param>
        /// <param name="username">The user's login name.</param>
        /// <param name="email">The user's email address.</param>
        /// <param name="roleIds">Array of role UUIDs to include as role claims.</param>
        /// <returns>A compact JWT string signed with the test HMAC key.</returns>
        private string GenerateJwtToken(Guid userId, string username, string email, Guid[] roleIds)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email),
                new Claim("username", username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            foreach (var roleId in roleIds)
            {
                claims.Add(new Claim(ClaimTypes.Role, roleId.ToString()));
            }

            var token = new JwtSecurityToken(
                issuer: TestJwtIssuer,
                audience: TestJwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        #endregion

        #region HTTP Client Helpers — Authenticated Test Clients

        /// <summary>
        /// Creates an <see cref="HttpClient"/> pre-configured with an administrator JWT token
        /// in the <c>Authorization: Bearer {token}</c> header. This is the primary entry point
        /// for integration tests that require authenticated access to the Reporting service's
        /// <c>[Authorize]</c>-protected controller endpoints.
        ///
        /// <para>Uses <see cref="GenerateAdminJwtToken"/> to create the token with
        /// SystemIds.FirstUserId and AdministratorRoleId claims.</para>
        /// </summary>
        /// <returns>
        /// An <see cref="HttpClient"/> with the admin JWT Bearer authorization header set.
        /// </returns>
        public HttpClient CreateAuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", GenerateAdminJwtToken());
            return client;
        }

        /// <summary>
        /// Creates an <see cref="HttpClient"/> with the specified JWT token in the
        /// <c>Authorization: Bearer {token}</c> header. Use this method for testing
        /// with custom user identities, specific role combinations, or expired/invalid tokens.
        /// </summary>
        /// <param name="jwtToken">The JWT token string to include in the authorization header.</param>
        /// <returns>
        /// An <see cref="HttpClient"/> with the specified JWT Bearer authorization header set.
        /// </returns>
        public HttpClient CreateAuthenticatedClient(string jwtToken)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);
            return client;
        }

        #endregion

        #region MassTransit Test Harness Access

        /// <summary>
        /// Resolves the MassTransit <see cref="ITestHarness"/> from the test server's DI container.
        /// The test harness provides methods for verifying event publish/consume behavior
        /// in integration tests without requiring external message broker infrastructure.
        ///
        /// <para><b>Usage example:</b></para>
        /// <code>
        /// var harness = factory.GetTestHarness();
        /// await harness.Bus.Publish(new TimelogCreatedEvent { ... });
        /// Assert.True(await harness.Consumed.Any&lt;TimelogCreatedEvent&gt;());
        /// </code>
        ///
        /// <para><b>Available consumers in Reporting service assembly:</b></para>
        /// <list type="bullet">
        ///   <item>TimelogCreatedConsumer — processes new timelog projection entries</item>
        ///   <item>TimelogDeletedConsumer — removes timelog projection entries</item>
        ///   <item>TaskUpdatedConsumer — updates task projection metadata</item>
        ///   <item>ProjectUpdatedConsumer — updates project projection metadata</item>
        /// </list>
        /// </summary>
        /// <returns>
        /// The <see cref="ITestHarness"/> instance for verifying message publish and consume operations.
        /// </returns>
        public ITestHarness GetTestHarness()
        {
            return Services.GetRequiredService<ITestHarness>();
        }

        #endregion

        #region IAsyncLifetime Implementation — Async Lifecycle Management

        /// <summary>
        /// Initializes the factory asynchronously. The <see cref="WebApplicationFactory{TEntryPoint}"/>
        /// lazily creates the test server on first use (e.g., <see cref="CreateClient()"/> or
        /// <see cref="WebApplicationFactory{TEntryPoint}.Services"/> access), so no explicit
        /// initialization is needed here.
        ///
        /// <para>The PostgreSQL Testcontainer is managed by <c>ReportingDbContextFixture</c>,
        /// which ensures the container is started and the connection string is available
        /// before this factory is constructed.</para>
        /// </summary>
        /// <returns><see cref="Task.CompletedTask"/> — initialization is lazy.</returns>
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the factory asynchronously, shutting down the in-process test server
        /// and releasing all DI-managed resources (ReportingDbContext connections, MassTransit
        /// test harness, logging providers).
        ///
        /// <para>The PostgreSQL Testcontainer is NOT disposed here — it is owned by
        /// <c>ReportingDbContextFixture</c> which handles container lifecycle independently.</para>
        /// </summary>
        /// <returns>A task representing the asynchronous disposal operation.</returns>
        public new async Task DisposeAsync()
        {
            await base.DisposeAsync();
        }

        #endregion
    }
}
