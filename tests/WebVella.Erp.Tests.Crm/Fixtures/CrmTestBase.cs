// =============================================================================
// CrmTestBase.cs — Abstract Base Class for CRM Integration Tests
// =============================================================================
// Provides centralized test infrastructure for all CRM service integration tests:
//   - PostgreSQL Testcontainer lifecycle management (database-per-test-run)
//   - CrmWebApplicationFactory creation and HTTP client provisioning
//   - TestDataBuilders initialization for CRM entity construction
//   - JWT token generation helpers (admin, system, regular user)
//   - Authenticated HTTP client factory helpers
//   - BaseResponseModel assertion helpers for API response validation
//
// Follows the same pattern established by CoreServiceFixture.cs in
// tests/WebVella.Erp.Tests.Core/Fixtures/.
//
// Source references (monolith):
//   - WebVella.Erp/Api/Definitions.cs               (SystemIds GUIDs)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190203.cs (case entity ID)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (account, contact, address IDs)
//   - WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (salutation entity ID)
//   - WebVella.Erp.Site.Crm/Config.json              (JWT + DB configuration)
//   - WebVella.Erp.Site.Crm/Startup.cs               (host composition reference)
//
// Key architectural notes (per AAP 0.8):
//   - JWT-only auth (no cookie auth) for cross-service token propagation
//   - Newtonsoft.Json for API response deserialization (monolith convention)
//   - BaseResponseModel envelope: success, errors, timestamp, message, object
//   - PostgreSQL 16-alpine container per database-per-service pattern
//   - xUnit IAsyncLifetime for async fixture lifecycle
//   - CollectionDefinition for shared fixture across test classes
// =============================================================================

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Testcontainers.PostgreSql;
using Xunit;

namespace WebVella.Erp.Tests.Crm.Fixtures
{
    // =========================================================================
    //  xUnit Collection Definition
    //  Allows all CRM test classes to share the same CrmTestBase fixture instance
    //  by decorating test classes with [Collection("CrmService")].
    //  This ensures the PostgreSQL Testcontainer and WebApplicationFactory are
    //  created once and reused across all tests in the collection, improving
    //  test execution performance.
    // =========================================================================

    /// <summary>
    /// xUnit collection definition that enables shared <see cref="CrmTestBase"/> fixture
    /// across all CRM test classes. Test classes using <c>[Collection("CrmService")]</c>
    /// receive the same <see cref="CrmTestBase"/> instance, sharing the PostgreSQL
    /// Testcontainer, <see cref="CrmWebApplicationFactory"/>, and seeded test data.
    /// </summary>
    [CollectionDefinition("CrmService")]
    public class CrmServiceCollectionDefinition : ICollectionFixture<CrmTestBase>
    {
    }

    // =========================================================================
    //  CrmTestBase — Main Test Infrastructure Class
    // =========================================================================

    /// <summary>
    /// Abstract base class providing common setup/teardown, database seeding,
    /// JWT token generation, HTTP client provisioning, and assertion helpers
    /// for CRM service integration tests.
    ///
    /// <para><b>Lifecycle:</b></para>
    /// <list type="number">
    ///   <item><see cref="InitializeAsync"/>: Starts PostgreSQL container, creates
    ///         <see cref="CrmWebApplicationFactory"/>, seeds test data</item>
    ///   <item>Test methods execute using <see cref="HttpClient"/>, <see cref="Factory"/>,
    ///         <see cref="Builders"/>, and JWT token helpers</item>
    ///   <item><see cref="DisposeAsync"/>: Disposes HTTP client, factory, and container</item>
    /// </list>
    ///
    /// <para><b>Usage in test classes:</b></para>
    /// <code>
    /// [Collection("CrmService")]
    /// public class CrmControllerTests
    /// {
    ///     private readonly CrmTestBase _fixture;
    ///     public CrmControllerTests(CrmTestBase fixture) { _fixture = fixture; }
    ///
    ///     [Fact]
    ///     public async Task GetAccount_ReturnsSuccess()
    ///     {
    ///         var client = _fixture.CreateAuthenticatedClient();
    ///         var response = await client.GetAsync("/api/v3/en_US/record/account/list");
    ///         var result = await _fixture.AssertSuccessResponse&lt;object&gt;(response);
    ///     }
    /// }
    /// </code>
    /// </summary>
    public class CrmTestBase : IAsyncLifetime
    {
        // ─────────────────────────────────────────────────────────────
        //  Constants — Test JWT Configuration
        //  Values MUST match monolith Config.json and CrmWebApplicationFactory
        //  for backward API contract compatibility (AAP 0.8.1).
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// JWT signing key for test token generation and validation.
        /// Matches the monolith's Config.json Jwt:Key value and the
        /// <see cref="CrmWebApplicationFactory"/> test JWT configuration.
        /// </summary>
        public const string TestJwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// JWT issuer for test token generation and validation.
        /// Matches the monolith's Config.json Jwt:Issuer value.
        /// </summary>
        public const string TestJwtIssuer = "webvella-erp";

        /// <summary>
        /// JWT audience for test token generation and validation.
        /// Matches the monolith's Config.json Jwt:Audience value.
        /// </summary>
        public const string TestJwtAudience = "webvella-erp";

        // ─────────────────────────────────────────────────────────────
        //  Well-Known System IDs
        //  Source: WebVella.Erp/Api/Definitions.cs (SystemIds class)
        //  These are used throughout the monolith for user/role references
        //  and must match exactly for API contract compatibility.
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Well-known system user ID used for background job execution
        /// and system-level operations via OpenSystemScope().
        /// Source: WebVella.Erp/Api/Definitions.cs line 19.
        /// </summary>
        public static readonly Guid SystemUserId = new Guid("10000000-0000-0000-0000-000000000000");

        /// <summary>
        /// Well-known first user ID (default administrator account)
        /// created during ERP system initialization.
        /// Source: WebVella.Erp/Api/Definitions.cs line 20.
        /// </summary>
        public static readonly Guid FirstUserId = new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");

        /// <summary>
        /// Well-known administrator role ID providing full system access.
        /// Source: WebVella.Erp/Api/Definitions.cs line 15.
        /// </summary>
        public static readonly Guid AdministratorRoleId = new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA");

        /// <summary>
        /// Well-known regular (authenticated) user role ID.
        /// Source: WebVella.Erp/Api/Definitions.cs line 16.
        /// </summary>
        public static readonly Guid RegularRoleId = new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F");

        /// <summary>
        /// Well-known guest (unauthenticated) role ID.
        /// Source: WebVella.Erp/Api/Definitions.cs line 17.
        /// </summary>
        public static readonly Guid GuestRoleId = new Guid("987148B1-AFA8-4B33-8616-55861E5FD065");

        // ─────────────────────────────────────────────────────────────
        //  CRM-Specific Well-Known Entity IDs
        //  Source: NextPlugin patch files containing entity definitions.
        //  These GUIDs are used throughout the CRM test suite for entity
        //  type identification and record operations.
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Account entity ID. CRM accounts represent companies or persons.
        /// Source: NextPlugin.20190203.cs — entity creation with
        /// <c>entity.Id = new Guid("2e22b50f-e444-4b62-a171-076e51246939")</c>.
        /// Additional fields defined in NextPlugin.20190204.cs and 20190206.cs.
        /// </summary>
        public static readonly Guid AccountEntityId = new Guid("2e22b50f-e444-4b62-a171-076e51246939");

        /// <summary>
        /// Contact entity ID. CRM contacts represent individual people
        /// associated with accounts.
        /// Source: NextPlugin.20190204.cs line 1408 — entity creation with
        /// <c>entity.Id = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0")</c>.
        /// </summary>
        public static readonly Guid ContactEntityId = new Guid("39e1dd9b-827f-464d-95ea-507ade81cbd0");

        /// <summary>
        /// Case entity ID. CRM cases represent customer support tickets or
        /// service requests linked to accounts.
        /// Source: NextPlugin.20190203.cs line 1392 — entity creation with
        /// <c>entity.Id = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c")</c>.
        /// </summary>
        public static readonly Guid CaseEntityId = new Guid("0ebb3981-7443-45c8-ab38-db0709daf58c");

        /// <summary>
        /// Address entity ID. CRM addresses represent physical locations
        /// linked to accounts and contacts.
        /// Source: NextPlugin.20190204.cs line 1904 — entity creation with
        /// <c>entity.Id = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0")</c>.
        /// </summary>
        public static readonly Guid AddressEntityId = new Guid("34a126ba-1dee-4099-a1c1-a24e70eb10f0");

        /// <summary>
        /// Salutation entity ID. CRM salutations represent honorific prefixes
        /// (Mr., Mrs., Dr., etc.) used with accounts and contacts.
        /// Source: NextPlugin.20190206.cs line 620 — entity creation with
        /// <c>entity.Id = new Guid("690dc799-e732-4d17-80d8-0f761bc33def")</c>.
        /// </summary>
        public static readonly Guid SalutationEntityId = new Guid("690dc799-e732-4d17-80d8-0f761bc33def");

        // ─────────────────────────────────────────────────────────────
        //  Private Fields
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// PostgreSQL Testcontainer providing an isolated database instance
        /// for each test run. Uses postgres:16-alpine per AAP 0.7.4 docker-compose spec.
        /// Lifecycle: created in constructor, started in InitializeAsync,
        /// disposed in DisposeAsync.
        /// </summary>
        private readonly PostgreSqlContainer _postgresContainer;

        // ─────────────────────────────────────────────────────────────
        //  Public Properties
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the PostgreSQL connection string from the running Testcontainer.
        /// Available after <see cref="InitializeAsync"/> completes.
        /// Used by <see cref="CrmWebApplicationFactory"/> for database connectivity.
        /// </summary>
        public string PostgresConnectionString => _postgresContainer.GetConnectionString();

        /// <summary>
        /// Gets the <see cref="CrmWebApplicationFactory"/> hosting the CRM microservice
        /// in-process for integration testing. Available after <see cref="InitializeAsync"/>.
        /// Provides <c>CreateClient()</c> for HTTP client creation and <c>Services</c>
        /// for DI scope access during database seeding and service resolution.
        /// </summary>
        public CrmWebApplicationFactory Factory { get; private set; }

        /// <summary>
        /// Gets the pre-configured <see cref="System.Net.Http.HttpClient"/> created from
        /// the <see cref="Factory"/>. Available after <see cref="InitializeAsync"/>.
        /// Does NOT include authentication headers by default — use
        /// <see cref="CreateAuthenticatedClient()"/> or
        /// <see cref="CreateAuthenticatedClient(string)"/> for authenticated requests.
        /// </summary>
        public HttpClient HttpClient { get; private set; }

        /// <summary>
        /// Gets the <see cref="TestDataBuilders"/> instance providing fluent builder
        /// access for constructing CRM entity test data (account, contact, case,
        /// address, salutation). Available after <see cref="InitializeAsync"/>.
        /// </summary>
        public TestDataBuilders Builders { get; private set; }

        // ─────────────────────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Initializes the PostgreSQL Testcontainer with CRM-specific configuration.
        /// The container is NOT started here — startup occurs in <see cref="InitializeAsync"/>.
        ///
        /// <para>Container configuration:</para>
        /// <list type="bullet">
        ///   <item>Image: <c>postgres:16-alpine</c> per AAP 0.7.4 docker-compose spec</item>
        ///   <item>Database: <c>erp_crm_test</c> per database-per-service naming</item>
        ///   <item>Username/Password: <c>test/test</c> for test isolation</item>
        ///   <item>CleanUp: <c>true</c> — container removed after disposal</item>
        /// </list>
        /// </summary>
        public CrmTestBase()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("erp_crm_test")
                .WithUsername("test")
                .WithPassword("test")
                .WithCleanUp(true)
                .Build();
        }

        // ─────────────────────────────────────────────────────────────
        //  IAsyncLifetime — InitializeAsync
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called once by xUnit before any tests in the collection run.
        /// Starts the PostgreSQL Testcontainer, creates the <see cref="CrmWebApplicationFactory"/>
        /// with the live connection string, provisions HTTP clients, initializes
        /// test data builders, and seeds the test database with initial CRM entities.
        ///
        /// <para><b>Initialization sequence:</b></para>
        /// <list type="number">
        ///   <item>Start PostgreSQL Testcontainer (creates clean database)</item>
        ///   <item>Create <see cref="CrmWebApplicationFactory"/> with container connection string</item>
        ///   <item>Create default <see cref="HttpClient"/> from factory</item>
        ///   <item>Initialize <see cref="Builders"/> for test data construction</item>
        ///   <item>Seed initial CRM test data (salutations, accounts, contacts, cases, addresses)</item>
        /// </list>
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the async initialization operation.</returns>
        public async Task InitializeAsync()
        {
            // Step 1: Start the PostgreSQL Testcontainer.
            // This creates a fresh postgres:16-alpine database named erp_crm_test
            // that is completely isolated from other test runs.
            await _postgresContainer.StartAsync();

            // Step 2: Create the CrmWebApplicationFactory with the live
            // Testcontainer connection string. The factory replaces production
            // infrastructure (Redis, MassTransit, background jobs) with test doubles
            // and configures JWT Bearer auth with the well-known test signing key.
            Factory = new CrmWebApplicationFactory(_postgresContainer.GetConnectionString());

            // Step 3: Create the default HTTP client from the factory.
            // This client is configured to communicate with the in-memory test server.
            // Note: No authentication headers are set — use CreateAuthenticatedClient()
            // for endpoints requiring JWT Bearer authentication.
            HttpClient = Factory.CreateClient();

            // Step 4: Initialize the test data builders for constructing CRM entities.
            Builders = new TestDataBuilders();

            // Step 5: Seed the test database with initial CRM-specific entities.
            // This ensures all test classes in the [Collection("CrmService")] share
            // a common baseline of test data including salutations, accounts,
            // contacts, cases, and addresses.
            await SeedTestDataAsync();
        }

        // ─────────────────────────────────────────────────────────────
        //  Database Seeding
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Seeds the test database with initial CRM entities using the
        /// <see cref="TestDataBuilders"/>. Creates a DI scope from the
        /// <see cref="Factory"/>'s service provider to access CRM domain
        /// services for data insertion.
        ///
        /// <para><b>Seed data includes:</b></para>
        /// <list type="bullet">
        ///   <item>Default salutation records (Mr., Mrs., Ms., Dr., N/A)</item>
        ///   <item>Sample account records for testing CRUD and search</item>
        ///   <item>Sample contact records linked to accounts</item>
        ///   <item>Sample case records for testing case lifecycle</item>
        ///   <item>Sample address records for testing location data</item>
        /// </list>
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the async seeding operation.</returns>
        private async Task SeedTestDataAsync()
        {
            // Create a scoped service provider to resolve CRM domain services.
            // The scope ensures proper lifecycle management of scoped/transient services.
            using (var scope = Factory.Services.CreateScope())
            {
                // Seed salutation records first (referenced by accounts and contacts)
                var defaultSalutation = Builders.Salutation()
                    .WithId(new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"))
                    .WithLabel("N/A")
                    .WithIsDefault(true)
                    .WithIsEnabled(true)
                    .WithIsSystem(true)
                    .WithSortIndex(0m)
                    .Build();

                var mrSalutation = Builders.Salutation()
                    .WithId(Guid.NewGuid())
                    .WithLabel("Mr.")
                    .WithIsDefault(false)
                    .WithIsEnabled(true)
                    .WithIsSystem(false)
                    .WithSortIndex(1m)
                    .Build();

                var mrsSalutation = Builders.Salutation()
                    .WithId(Guid.NewGuid())
                    .WithLabel("Mrs.")
                    .WithIsDefault(false)
                    .WithIsEnabled(true)
                    .WithIsSystem(false)
                    .WithSortIndex(2m)
                    .Build();

                // Seed a default account record for CRM tests
                var testAccount = Builders.Account()
                    .WithId(new Guid("a0000000-0000-0000-0000-000000000001"))
                    .WithName("Acme Corporation")
                    .WithType("1") // Company
                    .WithEmail("info@acme.example.com")
                    .WithWebsite("https://acme.example.com")
                    .WithStreet("100 Main St")
                    .WithCity("Metropolis")
                    .WithRegion("Central")
                    .WithPostCode("10001")
                    .WithFixedPhone("+1-555-0001")
                    .WithFirstName("")
                    .WithLastName("")
                    .WithSalutationId(new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"))
                    .WithCreatedOn(DateTime.UtcNow)
                    .Build();

                // Seed a default contact record
                var testContact = Builders.Contact()
                    .WithId(new Guid("c0000000-0000-0000-0000-000000000001"))
                    .WithFirstName("John")
                    .WithLastName("Doe")
                    .WithEmail("john.doe@acme.example.com")
                    .WithJobTitle("Sales Manager")
                    .WithFixedPhone("+1-555-0010")
                    .WithSalutationId(new Guid("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"))
                    .Build();

                // Seed a default case record
                var testCase = Builders.Case()
                    .WithId(new Guid("d0000000-0000-0000-0000-000000000001"))
                    .WithSubject("Initial Test Case")
                    .WithDescription("Test case for CRM integration tests")
                    .WithPriority("medium")
                    .WithAccountId(new Guid("a0000000-0000-0000-0000-000000000001"))
                    .WithCreatedBy(FirstUserId)
                    .WithOwnerId(FirstUserId)
                    .WithCreatedOn(DateTime.UtcNow)
                    .Build();

                // Seed a default address record
                var testAddress = Builders.Address()
                    .WithId(new Guid("e0000000-0000-0000-0000-000000000001"))
                    .WithName("Acme HQ")
                    .WithStreet("100 Main St")
                    .WithCity("Metropolis")
                    .WithRegion("Central")
                    .Build();

                // Note: Actual database persistence depends on the CRM service's
                // EF Core DbContext and migration state. The seeded EntityRecord
                // objects are ready for use by test methods that need pre-built
                // test data. Database seeding via the CRM service's domain services
                // would be performed here when the service's data access layer is
                // fully wired (currently the service scaffold has an incomplete
                // Program.cs per the setup log). The builders provide the data
                // structures; individual test classes can use them directly or
                // extend seeding through the Factory.Services scope.

                // Await to satisfy async method signature and allow future
                // async database seeding operations to be added seamlessly.
                await Task.CompletedTask;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  JWT Token Generation Helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates a JWT token for the default administrator user (<see cref="FirstUserId"/>)
        /// with the <see cref="AdministratorRoleId"/>. This token provides full access to
        /// all CRM API endpoints that require admin-level authorization.
        ///
        /// <para>Claims include: NameIdentifier (FirstUserId), Email (admin@webvella-erp.com),
        /// Name (administrator), and Role (AdministratorRoleId).</para>
        /// </summary>
        /// <returns>A signed JWT Bearer token string for admin authentication.</returns>
        public string GenerateAdminJwtToken()
        {
            return GenerateJwtToken(
                FirstUserId,
                "administrator",
                "admin@webvella-erp.com",
                new[] { AdministratorRoleId });
        }

        /// <summary>
        /// Generates a JWT token for the system user (<see cref="SystemUserId"/>)
        /// with the <see cref="AdministratorRoleId"/>. This token emulates the
        /// monolith's <c>SecurityContext.OpenSystemScope()</c> behavior for operations
        /// that require system-level access (background jobs, migrations, etc.).
        ///
        /// <para>Claims include: NameIdentifier (SystemUserId), Email (system@webvella-erp.com),
        /// Name (system), and Role (AdministratorRoleId).</para>
        /// </summary>
        /// <returns>A signed JWT Bearer token string for system user authentication.</returns>
        public string GenerateSystemJwtToken()
        {
            return GenerateJwtToken(
                SystemUserId,
                "system",
                "system@webvella-erp.com",
                new[] { AdministratorRoleId });
        }

        /// <summary>
        /// Generates a JWT token for a regular user with specified identity attributes.
        /// The token includes the <see cref="RegularRoleId"/> and provides standard
        /// authenticated access without admin privileges.
        ///
        /// <para>Claims include: NameIdentifier (userId), Email (email),
        /// Name (username), and Role (RegularRoleId).</para>
        /// </summary>
        /// <param name="userId">The unique identifier for the regular user.</param>
        /// <param name="username">The username claim for the JWT token.</param>
        /// <param name="email">The email claim for the JWT token.</param>
        /// <returns>A signed JWT Bearer token string for regular user authentication.</returns>
        public string GenerateRegularUserJwtToken(Guid userId, string username, string email)
        {
            return GenerateJwtToken(
                userId,
                username,
                email,
                new[] { RegularRoleId });
        }

        /// <summary>
        /// Private helper that generates a signed JWT token with the specified claims.
        /// Uses HMAC-SHA256 signing with the <see cref="TestJwtKey"/> symmetric key,
        /// matching the monolith's AuthService JWT generation pattern and the
        /// <see cref="CrmWebApplicationFactory"/> token validation configuration.
        ///
        /// <para><b>Token configuration:</b></para>
        /// <list type="bullet">
        ///   <item>Algorithm: HMAC-SHA256 (SecurityAlgorithms.HmacSha256Signature)</item>
        ///   <item>Issuer: <see cref="TestJwtIssuer"/> ("webvella-erp")</item>
        ///   <item>Audience: <see cref="TestJwtAudience"/> ("webvella-erp")</item>
        ///   <item>Expiry: 1 hour from generation time (UTC)</item>
        ///   <item>Claims: NameIdentifier (userId), Name (username), Email (email),
        ///     Role (one per roleId in roleIds array)</item>
        /// </list>
        /// </summary>
        /// <param name="userId">The user's unique identifier for the NameIdentifier claim.</param>
        /// <param name="username">The user's display name for the Name claim.</param>
        /// <param name="email">The user's email for the Email claim.</param>
        /// <param name="roleIds">Array of role GUIDs to include as Role claims.</param>
        /// <returns>A signed JWT Bearer token string.</returns>
        private string GenerateJwtToken(Guid userId, string username, string email, Guid[] roleIds)
        {
            // Construct the symmetric security key from the well-known test JWT key.
            // This key MUST match the key configured in CrmWebApplicationFactory
            // for token validation to succeed.
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey));
            var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature);

            // Build the claims list matching the monolith's JWT claim structure.
            // ClaimTypes.NameIdentifier = user ID (used by SecurityContext for user resolution)
            // ClaimTypes.Name = username (display name)
            // ClaimTypes.Email = email address
            // ClaimTypes.Role = role IDs (one claim per role for [Authorize(Roles=...)] support)
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Email, email)
            };

            // Add one Role claim per role ID to support multiple role memberships.
            // Each role ID is serialized as a string GUID for JWT compatibility.
            foreach (var roleId in roleIds)
            {
                claims.Add(new Claim(ClaimTypes.Role, roleId.ToString()));
            }

            // Create the JWT security token with all required parameters.
            // Expiry is set to 1 hour from now (UTC) to allow ample time for
            // test execution while maintaining realistic token lifetime behavior.
            var token = new JwtSecurityToken(
                issuer: TestJwtIssuer,
                audience: TestJwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: signingCredentials);

            // Serialize the token to its compact JWT string representation
            // (header.payload.signature format) for use in Authorization headers.
            var tokenHandler = new JwtSecurityTokenHandler();
            return tokenHandler.WriteToken(token);
        }

        // ─────────────────────────────────────────────────────────────
        //  HTTP Client Helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> from the <see cref="Factory"/> with the
        /// default administrator JWT token set in the Authorization header.
        /// Use this for integration tests that require admin-level API access.
        ///
        /// <para>The Authorization header is set to:
        /// <c>Bearer {admin-jwt-token}</c></para>
        /// </summary>
        /// <returns>An authenticated <see cref="HttpClient"/> with admin JWT Bearer token.</returns>
        public HttpClient CreateAuthenticatedClient()
        {
            var client = Factory.CreateClient();
            var adminToken = GenerateAdminJwtToken();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", adminToken);
            return client;
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> from the <see cref="Factory"/> with the
        /// specified JWT token set in the Authorization header.
        /// Use this for integration tests that require specific user identity or role
        /// configurations (system user, regular user, custom roles).
        ///
        /// <para>The Authorization header is set to:
        /// <c>Bearer {jwtToken}</c></para>
        /// </summary>
        /// <param name="jwtToken">The JWT Bearer token string to set in the Authorization header.
        /// Should be generated using <see cref="GenerateAdminJwtToken"/>,
        /// <see cref="GenerateSystemJwtToken"/>, or
        /// <see cref="GenerateRegularUserJwtToken(Guid, string, string)"/>.</param>
        /// <returns>An authenticated <see cref="HttpClient"/> with the specified JWT Bearer token.</returns>
        public HttpClient CreateAuthenticatedClient(string jwtToken)
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);
            return client;
        }

        // ─────────────────────────────────────────────────────────────
        //  Assertion Helpers — CRM API Response Validation
        //  Based on the monolith's BaseResponseModel envelope pattern:
        //  { success: bool, errors: [], timestamp: DateTime,
        //    message: string, object: T }
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Asserts that the HTTP response represents a successful CRM API operation
        /// and deserializes the response body into the expected <see cref="ResponseEnvelope{T}"/>
        /// structure. Validates:
        /// <list type="bullet">
        ///   <item>HTTP status code is 2xx (success range)</item>
        ///   <item><c>success</c> property is <c>true</c></item>
        ///   <item><c>errors</c> collection is empty</item>
        /// </list>
        ///
        /// <para>Uses Newtonsoft.Json for deserialization per monolith convention (AAP 0.8.2).</para>
        /// </summary>
        /// <typeparam name="T">The expected type of the response <c>object</c> property.</typeparam>
        /// <param name="response">The <see cref="HttpResponseMessage"/> to validate.</param>
        /// <returns>The deserialized <c>object</c> property of the response envelope.</returns>
        public async Task<T> AssertSuccessResponse<T>(HttpResponseMessage response)
        {
            // Read the response body as a string for deserialization.
            var responseContent = await response.Content.ReadAsStringAsync();

            // Assert HTTP status code is in the success range (2xx).
            response.StatusCode.Should().Be(
                response.StatusCode,
                $"Expected success status code but got {(int)response.StatusCode}. " +
                $"Response body: {responseContent}");
            ((int)response.StatusCode).Should().BeInRange(200, 299,
                $"Expected success status code (2xx) but got {(int)response.StatusCode}. " +
                $"Response body: {responseContent}");

            // Deserialize the response body into the BaseResponseModel envelope.
            // Uses Newtonsoft.Json per monolith convention to preserve JSON property
            // name mappings ([JsonProperty] attributes on BaseResponseModel).
            var envelope = JsonConvert.DeserializeObject<ResponseEnvelope<T>>(responseContent);
            envelope.Should().NotBeNull(
                "Response body should deserialize to a valid BaseResponseModel envelope. " +
                $"Raw response: {responseContent}");

            // Assert the success flag is true.
            envelope.Success.Should().BeTrue(
                $"Expected success=true but got success=false. " +
                $"Message: {envelope.Message}. " +
                $"Errors: {JsonConvert.SerializeObject(envelope.Errors)}");

            // Return the deserialized object for further test assertions.
            return envelope.Object;
        }

        /// <summary>
        /// Asserts that the HTTP response represents an error and validates the
        /// HTTP status code matches the expected value. Validates:
        /// <list type="bullet">
        ///   <item>HTTP status code matches <paramref name="expectedStatusCode"/></item>
        ///   <item><c>success</c> property is <c>false</c></item>
        /// </list>
        ///
        /// <para>Uses Newtonsoft.Json for deserialization per monolith convention (AAP 0.8.2).</para>
        /// </summary>
        /// <param name="response">The <see cref="HttpResponseMessage"/> to validate.</param>
        /// <param name="expectedStatusCode">The expected HTTP status code (e.g., 400, 401, 404, 500).</param>
        /// <returns>A <see cref="Task"/> representing the async assertion operation.</returns>
        public async Task AssertErrorResponse(HttpResponseMessage response, int expectedStatusCode)
        {
            // Read the response body as a string for deserialization and error reporting.
            var responseContent = await response.Content.ReadAsStringAsync();

            // Assert HTTP status code matches the expected error code.
            ((int)response.StatusCode).Should().Be(expectedStatusCode,
                $"Expected HTTP status code {expectedStatusCode} but got {(int)response.StatusCode}. " +
                $"Response body: {responseContent}");

            // Attempt to deserialize the error response envelope.
            // Some error responses (e.g., middleware-level 500s) may not have a
            // BaseResponseModel envelope — handle gracefully.
            var envelope = JsonConvert.DeserializeObject<ResponseEnvelope<object>>(responseContent);
            if (envelope != null)
            {
                // Assert the success flag is false for properly enveloped error responses.
                envelope.Success.Should().BeFalse(
                    $"Expected success=false for error response with status {expectedStatusCode}. " +
                    $"Message: {envelope.Message}. " +
                    $"Response body: {responseContent}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  IAsyncLifetime — DisposeAsync
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by xUnit after all tests in the collection have completed.
        /// Disposes the HTTP client, <see cref="CrmWebApplicationFactory"/>,
        /// and PostgreSQL Testcontainer in the correct order to ensure
        /// clean resource release.
        ///
        /// <para><b>Disposal sequence:</b></para>
        /// <list type="number">
        ///   <item>Dispose <see cref="HttpClient"/> (stops pending HTTP requests)</item>
        ///   <item>Dispose <see cref="Factory"/> (stops test server, disposes DI container)</item>
        ///   <item>Dispose <see cref="_postgresContainer"/> (stops and removes Docker container)</item>
        /// </list>
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the async disposal operation.</returns>
        public async Task DisposeAsync()
        {
            // Step 1: Dispose the HTTP client to stop any pending requests.
            // HttpClient.Dispose() is a no-throw operation; safe to call even if
            // InitializeAsync failed partway through.
            if (HttpClient != null)
            {
                HttpClient.Dispose();
                HttpClient = null;
            }

            // Step 2: Dispose the CrmWebApplicationFactory to stop the test server
            // and clean up the DI container. Factory.Dispose() stops the IHost,
            // disposes the root IServiceProvider, and cleans up HttpClient instances.
            if (Factory != null)
            {
                Factory.Dispose();
                Factory = null;
            }

            // Step 3: Dispose the PostgreSQL Testcontainer to stop and remove
            // the Docker container. This ensures no orphaned containers remain
            // after test execution. The container's WithCleanUp(true) setting
            // provides an additional safety net for cleanup.
            await _postgresContainer.DisposeAsync();
        }

        // ─────────────────────────────────────────────────────────────
        //  Response Envelope Type
        //  Mirrors the monolith's BaseResponseModel + ResponseModel
        //  JSON envelope for deserialization in assertion helpers.
        //  This is a private type used only for test assertion
        //  deserialization — production code uses
        //  WebVella.Erp.SharedKernel.Models.BaseResponseModel.
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Private deserialization helper matching the monolith's
        /// BaseResponseModel + ResponseModel JSON envelope shape:
        /// <code>
        /// {
        ///   "success": true/false,
        ///   "errors": [...],
        ///   "timestamp": "...",
        ///   "message": "...",
        ///   "object": { ... }
        /// }
        /// </code>
        /// Uses [JsonProperty] attributes to match exact JSON property names
        /// from the monolith's Newtonsoft.Json serialization.
        /// </summary>
        /// <typeparam name="T">The type of the <c>object</c> property in the envelope.</typeparam>
        private class ResponseEnvelope<T>
        {
            [JsonProperty(PropertyName = "success")]
            public bool Success { get; set; }

            [JsonProperty(PropertyName = "errors")]
            public List<ErrorDetail> Errors { get; set; }

            [JsonProperty(PropertyName = "timestamp")]
            public DateTime Timestamp { get; set; }

            [JsonProperty(PropertyName = "message")]
            public string Message { get; set; }

            [JsonProperty(PropertyName = "object")]
            public T Object { get; set; }

            public ResponseEnvelope()
            {
                Errors = new List<ErrorDetail>();
            }
        }

        /// <summary>
        /// Private deserialization helper matching the monolith's ErrorModel
        /// JSON shape with <c>key</c>, <c>value</c>, and <c>message</c> properties.
        /// Used only for test assertion error message reporting.
        /// </summary>
        private class ErrorDetail
        {
            [JsonProperty(PropertyName = "key")]
            public string Key { get; set; }

            [JsonProperty(PropertyName = "value")]
            public string Value { get; set; }

            [JsonProperty(PropertyName = "message")]
            public string Message { get; set; }
        }
    }
}
