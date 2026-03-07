// =============================================================================
// WebVella ERP — Core Platform Service Test Infrastructure
// CoreServiceFixture.cs
// =============================================================================
// xUnit IAsyncLifetime class fixture that provides the shared test
// infrastructure for ALL Core Platform service test classes. Manages:
//   - PostgreSQL Testcontainer for isolated database tests
//   - CoreServiceWebApplicationFactory for controller/gRPC integration tests
//   - Shared configuration (test JWT tokens, connection strings)
//   - Well-known system IDs matching monolith Definitions.cs exactly
//
// Key source references:
//   - WebVella.Erp/Api/Definitions.cs (lines 6-21): SystemIds GUIDs
//   - WebVella.Erp.Site/Config.json (lines 24-28): JWT Key, Issuer, Audience
//   - WebVella.Erp.Site/Startup.cs (lines 88-125): JWT Bearer auth config
//   - WebVella.Erp/ERPService.cs (lines 444-527): System user/role seeding
//   - WebVella.Erp/Api/SecurityContext.cs (lines 17-27): System user definition
// =============================================================================

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace WebVella.Erp.Tests.Core.Fixtures
{
	/// <summary>
	/// xUnit collection definition that allows all test classes decorated with
	/// <c>[Collection("CoreService")]</c> to share the same
	/// <see cref="CoreServiceFixture"/> instance. This is the standard xUnit
	/// pattern for sharing expensive resources (Docker containers, web
	/// application factories) across multiple test classes while ensuring
	/// a single initialization and teardown lifecycle.
	/// </summary>
	[CollectionDefinition("CoreService")]
	public class CoreServiceCollectionDefinition : ICollectionFixture<CoreServiceFixture>
	{
	}

	/// <summary>
	/// Primary xUnit <see cref="IAsyncLifetime"/> class fixture for all Core
	/// Platform service test classes. Manages the complete lifecycle of:
	///
	/// <list type="bullet">
	///   <item>
	///     <term>PostgreSQL Testcontainer</term>
	///     <description>
	///       An isolated <c>postgres:16-alpine</c> Docker container providing a
	///       clean database (<c>erp_core_test</c>) for each test collection run.
	///       Connection string is exposed via <see cref="PostgresConnectionString"/>.
	///     </description>
	///   </item>
	///   <item>
	///     <term>CoreServiceWebApplicationFactory</term>
	///     <description>
	///       Custom <c>WebApplicationFactory&lt;Program&gt;</c> hosting the Core
	///       Platform service in-memory with test-safe configuration overrides
	///       (Testcontainer PostgreSQL, in-memory cache, MassTransit test harness).
	///     </description>
	///   </item>
	///   <item>
	///     <term>Test Data Seeding</term>
	///     <description>
	///       Seeds the database with system entities, users, roles, and relations
	///       matching <c>ERPService.InitializeSystemEntities()</c> exactly.
	///     </description>
	///   </item>
	///   <item>
	///     <term>JWT Token Generation</term>
	///     <description>
	///       Provides helper methods for generating valid JWT tokens matching
	///       the monolith's JWT configuration for authenticated integration tests.
	///     </description>
	///   </item>
	/// </list>
	///
	/// <para>
	/// Usage: Decorate test classes with <c>[Collection("CoreService")]</c> and
	/// inject <see cref="CoreServiceFixture"/> via constructor injection.
	/// </para>
	/// </summary>
	/// <example>
	/// <code>
	/// [Collection("CoreService")]
	/// public class EntityManagerTests
	/// {
	///     private readonly CoreServiceFixture _fixture;
	///
	///     public EntityManagerTests(CoreServiceFixture fixture)
	///     {
	///         _fixture = fixture;
	///     }
	///
	///     [Fact]
	///     public async Task GetEntity_ReturnsSystemUser()
	///     {
	///         var client = _fixture.CreateAuthenticatedClient();
	///         var response = await client.GetAsync("/api/v3/en_US/entity/user");
	///         response.EnsureSuccessStatusCode();
	///     }
	/// }
	/// </code>
	/// </example>
	public class CoreServiceFixture : IAsyncLifetime
	{
		// =================================================================
		// Testcontainer — PostgreSQL
		// =================================================================

		/// <summary>
		/// PostgreSQL Testcontainer instance providing an isolated database for
		/// all Core service tests. Configured with <c>postgres:16-alpine</c>
		/// matching the docker-compose.localstack.yml specification (AAP 0.7.4).
		/// </summary>
		private readonly PostgreSqlContainer _postgresContainer;

		/// <summary>
		/// Live PostgreSQL connection string from the running Testcontainer.
		/// Passed to <see cref="CoreServiceWebApplicationFactory"/> during
		/// initialization, which injects it as
		/// <c>ConnectionStrings:Default</c> in the Core service configuration.
		/// </summary>
		public string PostgresConnectionString => _postgresContainer.GetConnectionString();

		// =================================================================
		// Web Application Factory and HTTP Client
		// =================================================================

		/// <summary>
		/// Custom <see cref="CoreServiceWebApplicationFactory"/> that hosts the
		/// Core Platform service in-memory with test-safe configuration
		/// (Testcontainer PostgreSQL, in-memory distributed cache, MassTransit
		/// test harness, test JWT authentication, disabled background jobs).
		/// </summary>
		public CoreServiceWebApplicationFactory Factory { get; private set; }

		/// <summary>
		/// Pre-configured <see cref="HttpClient"/> created from the factory's
		/// in-memory test server. This client is unauthenticated — use
		/// <see cref="CreateAuthenticatedClient()"/> or
		/// <see cref="CreateAuthenticatedClient(string)"/> for authenticated
		/// requests.
		/// </summary>
		public HttpClient HttpClient { get; private set; }

		// =================================================================
		// Test JWT Configuration
		// Derived from monolith Config.json lines 24-28:
		//   "Jwt": {
		//     "Key": "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey",
		//     "Issuer": "webvella-erp",
		//     "Audience": "webvella-erp"
		//   }
		// These values MUST match exactly for JWT token backward compatibility
		// with the monolith's Startup.cs JWT Bearer authentication (lines 102-114).
		// =================================================================

		/// <summary>
		/// JWT signing key matching the monolith's <c>Config.json</c> line 25
		/// (<c>Settings:Jwt:Key</c>). Used by <see cref="GenerateJwtToken"/>
		/// to create HMAC-SHA256 signed tokens that validate against the Core
		/// service's JWT Bearer authentication configuration.
		/// </summary>
		public const string TestJwtKey =
			"ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

		/// <summary>
		/// JWT issuer matching the monolith's <c>Config.json</c> line 26
		/// (<c>Settings:Jwt:Issuer</c>). Must match <c>ValidIssuer</c> in
		/// the Core service's <c>TokenValidationParameters</c>.
		/// </summary>
		public const string TestJwtIssuer = "webvella-erp";

		/// <summary>
		/// JWT audience matching the monolith's <c>Config.json</c> line 27
		/// (<c>Settings:Jwt:Audience</c>). Must match <c>ValidAudience</c>
		/// in the Core service's <c>TokenValidationParameters</c>.
		/// </summary>
		public const string TestJwtAudience = "webvella-erp";

		// =================================================================
		// Well-Known System IDs
		// Source: WebVella.Erp/Api/Definitions.cs lines 6-21 (SystemIds class)
		// These GUIDs MUST match the source exactly — they are used by
		// TestDataSeeder to seed the database and by test assertions to
		// verify system entity/user/role identity.
		// =================================================================

		/// <summary>
		/// System user ID matching <c>SystemIds.SystemUserId</c> from
		/// <c>Definitions.cs</c> line 19. The system user (email:
		/// <c>system@webvella.com</c>, username: <c>system</c>) has the
		/// administrator role only (<see cref="AdministratorRoleId"/>).
		/// Source: <c>SecurityContext.cs</c> lines 17-27.
		/// </summary>
		public static readonly Guid SystemUserId =
			new Guid("10000000-0000-0000-0000-000000000000");

		/// <summary>
		/// First/admin user ID matching <c>SystemIds.FirstUserId</c> from
		/// <c>Definitions.cs</c> line 20. The first user (email:
		/// <c>erp@webvella.com</c>, username: <c>administrator</c>) has both
		/// administrator and regular roles.
		/// Source: <c>ERPService.cs</c> lines 462-476, 517-526.
		/// </summary>
		public static readonly Guid FirstUserId =
			new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");

		/// <summary>
		/// Administrator role ID matching <c>SystemIds.AdministratorRoleId</c>
		/// from <c>Definitions.cs</c> line 15.
		/// Source: <c>ERPService.cs</c> lines 478-487.
		/// </summary>
		public static readonly Guid AdministratorRoleId =
			new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA");

		/// <summary>
		/// Regular role ID matching <c>SystemIds.RegularRoleId</c> from
		/// <c>Definitions.cs</c> line 16.
		/// Source: <c>ERPService.cs</c> lines 489-498.
		/// </summary>
		public static readonly Guid RegularRoleId =
			new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F");

		/// <summary>
		/// Guest role ID matching <c>SystemIds.GuestRoleId</c> from
		/// <c>Definitions.cs</c> line 17.
		/// Source: <c>ERPService.cs</c> lines 500-509.
		/// </summary>
		public static readonly Guid GuestRoleId =
			new Guid("987148B1-AFA8-4B33-8616-55861E5FD065");

		// =================================================================
		// Constructor
		// =================================================================

		/// <summary>
		/// Initializes a new <see cref="CoreServiceFixture"/> instance.
		/// Configures the PostgreSQL Testcontainer with:
		/// <list type="bullet">
		///   <item><c>postgres:16-alpine</c> — matching docker-compose.localstack.yml (AAP 0.7.4)</item>
		///   <item>Database: <c>erp_core_test</c> — isolated test database</item>
		///   <item>Username/Password: <c>test/test</c> — test credentials</item>
		///   <item><c>WithCleanUp(true)</c> — automatic container removal after tests</item>
		/// </list>
		/// The container is NOT started here — that happens in <see cref="InitializeAsync"/>.
		/// </summary>
		public CoreServiceFixture()
		{
			_postgresContainer = new PostgreSqlBuilder()
				.WithImage("postgres:16-alpine")
				.WithDatabase("erp_core_test")
				.WithUsername("test")
				.WithPassword("test")
				.WithCleanUp(true)
				.Build();
		}

		// =================================================================
		// IAsyncLifetime — InitializeAsync
		// =================================================================

		/// <summary>
		/// Called once by xUnit before any tests in the collection run.
		/// Performs the complete test infrastructure initialization:
		///
		/// <list type="number">
		///   <item>Starts the PostgreSQL Testcontainer Docker container</item>
		///   <item>Creates the <see cref="CoreServiceWebApplicationFactory"/>
		///         with the live container connection string</item>
		///   <item>Creates the default <see cref="HttpClient"/> from the factory</item>
		///   <item>Seeds the test database using <see cref="TestDataSeeder"/>
		///         within a scoped DI container, replicating
		///         <c>ERPService.InitializeSystemEntities()</c></item>
		/// </list>
		///
		/// After this method completes, the test database contains:
		/// - System entities (user, role, user_file) with all fields
		/// - System user (<c>system@webvella.com</c>) and first admin user (<c>erp@webvella.com</c>)
		/// - Three roles (administrator, regular, guest)
		/// - User-role relation links (system→admin, first→admin+regular)
		/// - Sample test entities and relations for comprehensive testing
		/// </summary>
		public async Task InitializeAsync()
		{
			// Step 1: Start the PostgreSQL Testcontainer.
			// This downloads the postgres:16-alpine image (if not cached),
			// starts a new container, and waits until PostgreSQL is ready
			// to accept connections.
			await _postgresContainer.StartAsync();

			// Step 2: Create the CoreServiceWebApplicationFactory with the
			// live PostgreSQL connection string from the running container.
			// The factory overrides the Core service's configuration to use
			// this connection string, in-memory cache, MassTransit test
			// harness, and test JWT authentication parameters.
			Factory = new CoreServiceWebApplicationFactory(PostgresConnectionString);

			// Step 3: Create the default HTTP client from the factory.
			// This client is connected to the in-memory test server and can
			// be used for unauthenticated HTTP requests.
			HttpClient = Factory.CreateClient();

			// Step 4: Seed the test database with system entities, users,
			// roles, and relations matching ERPService.InitializeSystemEntities().
			// The seeder is resolved within a DI scope to ensure proper
			// scoped service lifetimes.
			using (var scope = Factory.Services.CreateScope())
			{
				var seeder = new TestDataSeeder(scope.ServiceProvider);
				await seeder.SeedAsync();
			}
		}

		// =================================================================
		// IAsyncLifetime — DisposeAsync
		// =================================================================

		/// <summary>
		/// Called once by xUnit after all tests in the collection have completed.
		/// Performs orderly resource cleanup:
		///
		/// <list type="number">
		///   <item>Disposes the <see cref="HttpClient"/> (stops HTTP communication)</item>
		///   <item>Disposes the <see cref="Factory"/> (stops the in-memory test server)</item>
		///   <item>Disposes the PostgreSQL Testcontainer (stops and removes the Docker container)</item>
		/// </list>
		///
		/// Disposal is performed in reverse initialization order to prevent
		/// access to disposed resources. Each disposal step is guarded with
		/// null checks to handle partial initialization scenarios (e.g., if
		/// <see cref="InitializeAsync"/> fails midway).
		/// </summary>
		public async Task DisposeAsync()
		{
			// Dispose HTTP client first — it depends on the factory's test server.
			if (HttpClient != null)
			{
				HttpClient.Dispose();
				HttpClient = null;
			}

			// Dispose the factory — this stops the in-memory test server
			// and disposes all scoped services registered in the DI container.
			if (Factory != null)
			{
				Factory.Dispose();
				Factory = null;
			}

			// Dispose the PostgreSQL Testcontainer — this stops the container,
			// removes it (WithCleanUp=true), and releases Docker resources.
			await _postgresContainer.DisposeAsync();
		}

		// =================================================================
		// JWT Token Generation Helpers
		// =================================================================

		/// <summary>
		/// Generates a JWT token for the admin user (first user) with both
		/// administrator and regular roles, matching the monolith's first
		/// user seeding in <c>ERPService.cs</c> lines 462-476 and role
		/// assignments in lines 517-526.
		///
		/// User details:
		/// <list type="bullet">
		///   <item>ID: <see cref="FirstUserId"/> (<c>EABD66FD-...</c>)</item>
		///   <item>Username: <c>administrator</c></item>
		///   <item>Email: <c>erp@webvella.com</c></item>
		///   <item>Roles: administrator + regular</item>
		/// </list>
		/// </summary>
		/// <returns>
		/// A compact JWT token string suitable for use in HTTP Authorization
		/// headers as <c>Bearer {token}</c>.
		/// </returns>
		public string GenerateAdminJwtToken()
		{
			return GenerateJwtToken(
				FirstUserId,
				"administrator",
				"erp@webvella.com",
				new[] { AdministratorRoleId, RegularRoleId });
		}

		/// <summary>
		/// Generates a JWT token for the system user with the administrator
		/// role, matching the monolith's <c>SecurityContext.cs</c> static
		/// constructor (lines 17-27) where the system user is created with
		/// only the administrator role.
		///
		/// User details:
		/// <list type="bullet">
		///   <item>ID: <see cref="SystemUserId"/> (<c>10000000-...</c>)</item>
		///   <item>Username: <c>system</c></item>
		///   <item>Email: <c>system@webvella.com</c></item>
		///   <item>Roles: administrator only</item>
		/// </list>
		/// </summary>
		/// <returns>
		/// A compact JWT token string suitable for use in HTTP Authorization
		/// headers as <c>Bearer {token}</c>.
		/// </returns>
		public string GenerateSystemJwtToken()
		{
			return GenerateJwtToken(
				SystemUserId,
				"system",
				"system@webvella.com",
				new[] { AdministratorRoleId });
		}

		/// <summary>
		/// Generates a JWT token for a regular (non-admin) user with only
		/// the regular role. Useful for testing permission-restricted
		/// endpoints that should deny non-admin access.
		/// </summary>
		/// <param name="userId">
		/// The unique identifier of the regular user. Use a known seeded
		/// user ID or generate a new GUID for ad-hoc test users.
		/// </param>
		/// <param name="username">
		/// The username claim for the token. Must match the seeded user's
		/// username if testing against existing records.
		/// </param>
		/// <param name="email">
		/// The email claim for the token. Must match the seeded user's
		/// email if testing against existing records.
		/// </param>
		/// <returns>
		/// A compact JWT token string suitable for use in HTTP Authorization
		/// headers as <c>Bearer {token}</c>.
		/// </returns>
		public string GenerateRegularUserJwtToken(Guid userId, string username, string email)
		{
			return GenerateJwtToken(
				userId,
				username,
				email,
				new[] { RegularRoleId });
		}

		/// <summary>
		/// Private helper that constructs a signed JWT token with the
		/// specified user identity and role claims. The token is signed
		/// with HMAC-SHA256 using <see cref="TestJwtKey"/> and is valid
		/// for 1 hour from the time of creation.
		///
		/// Token structure:
		/// <list type="bullet">
		///   <item><c>sub</c>: User ID (GUID string)</item>
		///   <item><c>email</c>: User email address</item>
		///   <item><c>username</c>: Custom claim with username</item>
		///   <item><c>jti</c>: Unique token ID (random GUID)</item>
		///   <item><c>role</c>: One claim per role ID (GUID string)</item>
		/// </list>
		///
		/// The token validates against the Core service's JWT Bearer
		/// authentication configured in <c>Startup.cs</c> lines 102-114
		/// with matching issuer, audience, and signing key.
		/// </summary>
		/// <param name="userId">The user's unique identifier.</param>
		/// <param name="username">The user's username.</param>
		/// <param name="email">The user's email address.</param>
		/// <param name="roleIds">
		/// Array of role GUIDs to include as role claims in the token.
		/// </param>
		/// <returns>
		/// A compact JWT token string (header.payload.signature).
		/// </returns>
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

		// =================================================================
		// HTTP Client Helpers
		// =================================================================

		/// <summary>
		/// Creates an <see cref="HttpClient"/> pre-configured with an admin
		/// JWT token in the <c>Authorization</c> header. The token is
		/// generated using <see cref="GenerateAdminJwtToken"/> and represents
		/// the first admin user (<c>erp@webvella.com</c>) with both
		/// administrator and regular roles.
		///
		/// This is the most commonly used authenticated client for
		/// integration tests that require admin-level access.
		/// </summary>
		/// <returns>
		/// An <see cref="HttpClient"/> connected to the in-memory test
		/// server with <c>Authorization: Bearer {adminToken}</c> header set.
		/// </returns>
		public HttpClient CreateAuthenticatedClient()
		{
			var token = GenerateAdminJwtToken();
			return CreateAuthenticatedClient(token);
		}

		/// <summary>
		/// Creates an <see cref="HttpClient"/> pre-configured with the
		/// specified JWT token in the <c>Authorization</c> header. Use
		/// this overload when testing with non-admin tokens (e.g., regular
		/// user, system user, or custom tokens).
		/// </summary>
		/// <param name="jwtToken">
		/// A valid JWT token string to include in the <c>Authorization</c>
		/// header. Typically generated by <see cref="GenerateAdminJwtToken"/>,
		/// <see cref="GenerateSystemJwtToken"/>, or
		/// <see cref="GenerateRegularUserJwtToken"/>.
		/// </param>
		/// <returns>
		/// An <see cref="HttpClient"/> connected to the in-memory test
		/// server with <c>Authorization: Bearer {jwtToken}</c> header set.
		/// </returns>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="jwtToken"/> is null.
		/// </exception>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="jwtToken"/> is empty or whitespace.
		/// </exception>
		public HttpClient CreateAuthenticatedClient(string jwtToken)
		{
			if (jwtToken == null)
			{
				throw new ArgumentNullException(nameof(jwtToken));
			}

			if (string.IsNullOrWhiteSpace(jwtToken))
			{
				throw new ArgumentException(
					"JWT token must not be empty or whitespace.",
					nameof(jwtToken));
			}

			var client = Factory.CreateClient();
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("Bearer", jwtToken);
			return client;
		}
	}
}
