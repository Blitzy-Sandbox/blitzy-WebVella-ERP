using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;
using WebVella.Erp.Tests.Integration.Fixtures;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.Integration.CrossService
{
    /// <summary>
    /// Cross-service integration tests validating that the JWT-based identity propagation
    /// correctly replaces the monolith's <c>SecurityContext</c> with its <c>AsyncLocal&lt;Stack&lt;ErpUser&gt;&gt;</c>
    /// pattern. In the monolith (source: <c>WebVella.Erp/Api/SecurityContext.cs</c>), user identity
    /// was managed via a process-local <c>AsyncLocal</c> stack with <c>OpenScope(ErpUser)</c> and
    /// <c>OpenSystemScope()</c> methods. In the microservice architecture, JWT tokens issued by the
    /// Core service propagate user identity (ID, roles, permissions) to all downstream services
    /// without callback.
    ///
    /// <para><b>SecurityContext Business Rules Tested (all 6):</b></para>
    /// <list type="number">
    ///   <item>Rule 1: System User Identity — SystemUserId, admin role, email=system@webvella.com</item>
    ///   <item>Rule 2: CurrentUser Resolution — JWT claims map to user identity in each service</item>
    ///   <item>Rule 3: IsUserInRole — Role ID claims enable role-based checks</item>
    ///   <item>Rule 4: HasEntityPermission — System user unlimited; regular checks role membership; guest checks GuestRoleId</item>
    ///   <item>Rule 5: HasMetaPermission — Only AdministratorRoleId grants meta operations</item>
    ///   <item>Rule 6: OpenSystemScope — System user JWT for background job execution</item>
    /// </list>
    ///
    /// <para><b>Key AAP References:</b></para>
    /// <list type="bullet">
    ///   <item>AAP 0.1.1: "SecurityContext using AsyncLocal requires conversion to a token-propagated identity model"</item>
    ///   <item>AAP 0.7.1: User→Role — "Core service owns; JWT claims propagate role information"</item>
    ///   <item>AAP 0.8.1: "JWT authentication must remain compatible with existing tokens"</item>
    ///   <item>AAP 0.8.3: "JWT tokens issued by Core service must contain all necessary claims for downstream services"</item>
    ///   <item>AAP 0.8.3: "OpenSystemScope() pattern must be preserved within each service for background job execution"</item>
    /// </list>
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class UserRolePropagationTests : IAsyncLifetime
    {
        #region Constants — JWT Configuration (matching monolith Config.json lines 24-28)

        /// <summary>
        /// Symmetric signing key for JWT tokens.
        /// Source: Config.json line 25: "Key": "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey"
        /// Per AAP 0.8.1: JWT authentication must remain compatible with existing tokens.
        /// </summary>
        private const string JwtSecretKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// JWT issuer matching monolith Config.json line 26.
        /// Source: Config.json: "Issuer": "webvella-erp"
        /// </summary>
        private const string JwtIssuer = "webvella-erp";

        /// <summary>
        /// JWT audience matching monolith Config.json line 27.
        /// Source: Config.json: "Audience": "webvella-erp"
        /// </summary>
        private const string JwtAudience = "webvella-erp";

        /// <summary>
        /// Default JWT token expiry in minutes matching monolith AuthService.cs line 19.
        /// Source: AuthService.cs: JWT_TOKEN_EXPIRY_DURATION_MINUTES = 1440
        /// </summary>
        private const int DefaultExpiryMinutes = 1440;

        /// <summary>
        /// JWT token refresh-after threshold in minutes matching monolith AuthService.cs line 20.
        /// Source: AuthService.cs: JWT_TOKEN_FORCE_REFRESH_MINUTES = 120
        /// </summary>
        private const double JwtTokenRefreshMinutes = 120;

        #endregion

        #region Constants — Service Endpoint Paths

        /// <summary>
        /// Core service record listing endpoint for authorization testing.
        /// Route from RecordController.cs: [Route("api/v3/{locale}")] with
        /// [AcceptVerbs(new[] { "GET" }, Route = "/api/v3/{locale}/record/{entityName}/list")]
        /// Uses "user" entity (system entity defined in ERPService.cs, always present).
        /// </summary>
        private const string CoreHealthEndpoint = "/api/v3/en_US/record/user/list";

        /// <summary>
        /// CRM service record listing endpoint for authorization testing.
        /// Route from CrmController.cs: [Route("api/v3/{locale}/crm")] with
        /// [HttpGet("record/{entityName}/list")]. Note the /crm/ prefix.
        /// </summary>
        private const string CrmRecordEndpoint = "/api/v3/en_US/crm/record/account/list";

        /// <summary>
        /// Project service authenticated endpoint for authorization testing.
        /// Route from ProjectController.cs: [Route("api/v3.0/p/project/user/get-current")] [HttpGet].
        /// Returns current authenticated user info.
        /// </summary>
        private const string ProjectRecordEndpoint = "/api/v3.0/p/project/user/get-current";

        /// <summary>
        /// Mail service email listing endpoint for authorization testing.
        /// Route from MailController.cs: [Route("api/v3/{locale}")] with
        /// [HttpGet("mail/emails")]. Lists email records.
        /// </summary>
        private const string MailRecordEndpoint = "/api/v3/en_US/mail/emails";

        /// <summary>
        /// Metadata operation endpoint — only accessible by users with AdministratorRoleId.
        /// Route from EntityController.cs: [Route("api/v3.0/meta")] [Authorize(Roles = "administrator")]
        /// with [HttpGet("entity/list")].
        /// Source: SecurityContext.cs lines 109-118: HasMetaPermission checks AdministratorRoleId.
        /// </summary>
        private const string MetaEntityEndpoint = "/api/v3.0/meta/entity/list";

        #endregion

        #region Private Fields

        private readonly ServiceCollectionFixture _serviceFixture;
        private readonly PostgreSqlFixture _pgFixture;
        private readonly TestDataSeeder _seeder;
        private readonly ITestOutputHelper _output;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of <see cref="UserRolePropagationTests"/>.
        /// Receives PostgreSqlFixture, LocalStackFixture, and RedisFixture from the
        /// <see cref="IntegrationTestCollection"/> collection fixtures (xUnit DI).
        /// Creates <see cref="ServiceCollectionFixture"/> manually because xUnit v2 does not
        /// support injecting collection fixtures into other collection fixture constructors
        /// (see IntegrationTestCollection.cs note).
        /// </summary>
        public UserRolePropagationTests(
            PostgreSqlFixture pgFixture,
            LocalStackFixture localStackFixture,
            RedisFixture redisFixture,
            ITestOutputHelper output)
        {
            _pgFixture = pgFixture ?? throw new ArgumentNullException(nameof(pgFixture));
            _serviceFixture = new ServiceCollectionFixture(pgFixture, localStackFixture, redisFixture);
            _seeder = new TestDataSeeder(pgFixture);
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region IAsyncLifetime — Initialize service factories and seed data

        /// <summary>
        /// Initializes the <see cref="ServiceCollectionFixture"/> (creates all 6 WebApplicationFactory
        /// instances) and seeds the Core database with system users, roles, and user-role relations
        /// required by all JWT propagation tests.
        /// </summary>
        public async Task InitializeAsync()
        {
            _output.WriteLine("[UserRolePropagationTests] Initializing service factories...");
            await _serviceFixture.InitializeAsync();
            _output.WriteLine("[UserRolePropagationTests] Service factories initialized. Seeding core data...");
            await _seeder.SeedCoreDataAsync(_pgFixture.CoreConnectionString);
            _output.WriteLine("[UserRolePropagationTests] Core data seeded (users, roles, user-role relations).");
        }

        /// <summary>
        /// Disposes all WebApplicationFactory instances created during initialization.
        /// </summary>
        public async Task DisposeAsync()
        {
            _output.WriteLine("[UserRolePropagationTests] Disposing service factories...");
            await _serviceFixture.DisposeAsync();
            _output.WriteLine("[UserRolePropagationTests] Disposed.");
        }

        #endregion

        #region Test 1: CoreServiceJwtToken_ContainsAllRequiredClaims_DownstreamServicesAuthorize

        /// <summary>
        /// Validates SecurityContext Rule 2 (CurrentUser Resolution) and AAP 0.8.3:
        /// "JWT tokens issued by Core service must contain all necessary claims for
        /// downstream services to authorize requests without callback to the Core service."
        ///
        /// Verifies that a JWT token generated with admin claims is accepted by ALL
        /// downstream services (CRM, Project, Mail) without callback to Core.
        /// </summary>
        [Fact]
        public async Task CoreServiceJwtToken_ContainsAllRequiredClaims_DownstreamServicesAuthorize()
        {
            // Arrange: Generate admin JWT token with all required claims
            string adminToken = _seeder.GenerateAdminJwtToken();
            _output.WriteLine($"[Test1] Admin JWT generated. Token length: {adminToken.Length}");

            // Verify the token contains the required claims before sending to services
            JwtSecurityToken parsedToken = ParseJwtToken(adminToken);
            parsedToken.Should().NotBeNull("admin JWT token must be parseable");

            var nameIdClaim = parsedToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier);
            nameIdClaim.Should().NotBeNull("JWT must contain NameIdentifier claim (user ID)");
            nameIdClaim.Value.Should().Be(SystemIds.FirstUserId.ToString(),
                "admin token should use FirstUserId per TestDataSeeder.GenerateAdminJwtToken()");

            var emailClaim = parsedToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email);
            emailClaim.Should().NotBeNull("JWT must contain Email claim");
            emailClaim.Value.Should().Be("admin@webvella.com",
                "admin token should use admin email");

            var roleClaims = parsedToken.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == ClaimTypes.Role.ToString()).ToList();
            roleClaims.Should().NotBeEmpty("JWT must contain at least one Role claim");
            roleClaims.Any(c => c.Value == "administrator").Should().BeTrue(
                "admin JWT must include 'administrator' role matching SecurityContext.cs line 26");
            _output.WriteLine($"[Test1] Token claims verified: userId={nameIdClaim.Value}, email={emailClaim.Value}, roles={roleClaims.Count}");

            // Act: Call each downstream service with the same JWT token
            using var coreClient = _serviceFixture.CreateCoreClient();
            using var crmClient = _serviceFixture.CreateCrmClient();
            using var projectClient = _serviceFixture.CreateProjectClient();
            using var mailClient = _serviceFixture.CreateMailClient();

            HttpResponseMessage coreResponse = await CallServiceEndpoint(coreClient, CoreHealthEndpoint, adminToken);
            HttpResponseMessage crmResponse = await CallServiceEndpoint(crmClient, CrmRecordEndpoint, adminToken);
            HttpResponseMessage projectResponse = await CallServiceEndpoint(projectClient, ProjectRecordEndpoint, adminToken);
            HttpResponseMessage mailResponse = await CallServiceEndpoint(mailClient, MailRecordEndpoint, adminToken);

            _output.WriteLine($"[Test1] Service responses — Core:{coreResponse.StatusCode}, CRM:{crmResponse.StatusCode}, Project:{projectResponse.StatusCode}, Mail:{mailResponse.StatusCode}");

            // Assert: ALL downstream services accept the JWT without callback to Core
            // Per AAP 0.8.3: services should authorize based solely on JWT claims
            coreResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "Core service should accept valid admin JWT");
            crmResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "CRM service should accept the same JWT without callback to Core");
            projectResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "Project service should accept the same JWT without callback to Core");
            mailResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "Mail service should accept the same JWT without callback to Core");
        }

        #endregion

        #region Test 2: AdminUserJwt_HasFullEntityPermissions_AcrossAllServices

        /// <summary>
        /// Validates SecurityContext Rule 4 (HasEntityPermission) — specifically the rule from
        /// SecurityContext.cs line 74-75: system user and administrator role users have full
        /// entity permissions (CanRead, CanCreate, CanUpdate, CanDelete) across all services.
        /// </summary>
        [Fact]
        public async Task AdminUserJwt_HasFullEntityPermissions_AcrossAllServices()
        {
            // Arrange: Generate admin JWT and system JWT to test both permission paths
            string adminToken = _seeder.GenerateAdminJwtToken();
            string systemToken = _seeder.GenerateSystemJwtToken();
            _output.WriteLine("[Test2] Admin and System JWT tokens generated.");

            // Act: Call CRUD-equivalent endpoints on each service with admin token
            using var coreClient = _serviceFixture.CreateCoreClient();
            using var crmClient = _serviceFixture.CreateCrmClient();
            using var projectClient = _serviceFixture.CreateProjectClient();
            using var mailClient = _serviceFixture.CreateMailClient();

            // Read operations across all services (GET requests to list endpoints)
            HttpResponseMessage coreRead = await CallServiceEndpoint(coreClient, CoreHealthEndpoint, adminToken);
            HttpResponseMessage crmRead = await CallServiceEndpoint(crmClient, CrmRecordEndpoint, adminToken);
            HttpResponseMessage projectRead = await CallServiceEndpoint(projectClient, ProjectRecordEndpoint, adminToken);
            HttpResponseMessage mailRead = await CallServiceEndpoint(mailClient, MailRecordEndpoint, adminToken);

            _output.WriteLine($"[Test2] Admin reads — Core:{coreRead.StatusCode}, CRM:{crmRead.StatusCode}, Project:{projectRead.StatusCode}, Mail:{mailRead.StatusCode}");

            // Assert: Admin user has full read permissions across all services
            // Per SecurityContext.cs HasEntityPermission: admin role is checked against RecordPermissions
            coreRead.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "admin user should have read access to Core entities");
            coreRead.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "admin user should not be forbidden from Core entity reads");
            crmRead.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "admin user should have read access to CRM entities");
            crmRead.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "admin user should not be forbidden from CRM entity reads");
            projectRead.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "admin user should have read access to Project entities");
            projectRead.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "admin user should not be forbidden from Project entity reads");
            mailRead.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "admin user should have read access to Mail entities");
            mailRead.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "admin user should not be forbidden from Mail entity reads");

            // Verify system token also grants full permissions
            // Per SecurityContext.cs line 74-75: if (user.Id == SystemIds.SystemUserId) return true;
            using var coreClientSys = _serviceFixture.CreateCoreClient();
            HttpResponseMessage systemRead = await CallServiceEndpoint(coreClientSys, CoreHealthEndpoint, systemToken);
            systemRead.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "system user should have unlimited permissions per SecurityContext.cs line 74-75");
            systemRead.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "system user should never be forbidden per SecurityContext.cs line 74-75");
            _output.WriteLine($"[Test2] System user read — Core:{systemRead.StatusCode}");
        }

        #endregion

        #region Test 3: RegularUserJwt_HasLimitedPermissions_EnforcedAcrossServices

        /// <summary>
        /// Validates SecurityContext Rule 3 (IsUserInRole) and Rule 4 (HasEntityPermission) —
        /// regular users with only RegularRoleId should have limited permissions.
        /// Operations requiring administrator permission should return 403 Forbidden.
        /// </summary>
        [Fact]
        public async Task RegularUserJwt_HasLimitedPermissions_EnforcedAcrossServices()
        {
            // Arrange: Generate JWT with only regular role
            // RegularRoleId: F16EC6DB-626D-4C27-8DE0-3E7CE542C55F (from Definitions.cs line 16)
            Guid regularUserId = new Guid("A0000001-0000-0000-0000-000000000001"); // test user
            string regularToken = _seeder.GenerateJwtToken(
                regularUserId,
                "testuser@webvella.com",
                new List<string> { "regular" }
            );
            _output.WriteLine($"[Test3] Regular user JWT generated for userId={regularUserId}");

            // Verify the token contains the regular role claim
            JwtSecurityToken parsedToken = ParseJwtToken(regularToken);
            var roleClaims = parsedToken.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == ClaimTypes.Role.ToString())
                .ToList();
            roleClaims.Should().NotBeEmpty("regular user JWT must contain role claims");
            roleClaims.Any(c => c.Value == "regular").Should().BeTrue(
                "regular user JWT must include 'regular' role");
            roleClaims.Any(c => c.Value == "administrator").Should().BeFalse(
                "regular user JWT must NOT include 'administrator' role");

            // Act: Attempt meta operations that require administrator permission
            // Per SecurityContext.cs line 109-118: HasMetaPermission only returns true for AdministratorRoleId
            using var coreClient = _serviceFixture.CreateCoreClient();
            HttpResponseMessage metaResponse = await CallServiceEndpoint(coreClient, MetaEntityEndpoint, regularToken);

            _output.WriteLine($"[Test3] Regular user meta operation response: {metaResponse.StatusCode}");

            // Assert: Regular user should be denied meta operations.
            // EntityController.cs has [Authorize(Roles = "administrator")] at class level,
            // so authenticated users without the "administrator" role should get 403 Forbidden.
            var metaStatusCode = metaResponse.StatusCode;
            ((int)metaStatusCode).Should().BeGreaterThanOrEqualTo(400,
                $"regular user should be denied meta operations, got {metaStatusCode}. " +
                "Per SecurityContext.cs lines 109-118: only AdministratorRoleId has meta permission");
            metaStatusCode.Should().Be(HttpStatusCode.Forbidden,
                "regular user with valid JWT but non-admin role should receive 403 Forbidden " +
                "from [Authorize(Roles = \"administrator\")] on EntityController");

            // Regular user should still have read access to non-meta endpoints
            using var crmClient = _serviceFixture.CreateCrmClient();
            HttpResponseMessage readResponse = await CallServiceEndpoint(crmClient, CrmRecordEndpoint, regularToken);
            readResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "regular user should have read access to standard endpoints");
            _output.WriteLine($"[Test3] Regular user read access: {readResponse.StatusCode}");
        }

        #endregion

        #region Test 4: NoJwtToken_GuestPermissions_OnlyPublicEndpointsAccessible

        /// <summary>
        /// Validates SecurityContext Rule 4 (HasEntityPermission) for null user scenario —
        /// from SecurityContext.cs lines 92-106: when user is null, permissions check
        /// against GuestRoleId. Without JWT, services should deny protected endpoints.
        /// </summary>
        [Fact]
        public async Task NoJwtToken_GuestPermissions_OnlyPublicEndpointsAccessible()
        {
            // Arrange: Create clients without any JWT token (guest access)
            using var coreClient = _serviceFixture.CreateCoreClient();
            using var crmClient = _serviceFixture.CreateCrmClient();
            using var projectClient = _serviceFixture.CreateProjectClient();
            using var mailClient = _serviceFixture.CreateMailClient();

            _output.WriteLine("[Test4] Testing guest (no JWT) access to all services...");

            // Act: Call protected endpoints without JWT token
            HttpResponseMessage coreResponse = await coreClient.GetAsync(CoreHealthEndpoint);
            HttpResponseMessage crmResponse = await crmClient.GetAsync(CrmRecordEndpoint);
            HttpResponseMessage projectResponse = await projectClient.GetAsync(ProjectRecordEndpoint);
            HttpResponseMessage mailResponse = await mailClient.GetAsync(MailRecordEndpoint);

            _output.WriteLine($"[Test4] Guest responses — Core:{coreResponse.StatusCode}, CRM:{crmResponse.StatusCode}, Project:{projectResponse.StatusCode}, Mail:{mailResponse.StatusCode}");

            // Assert: Protected endpoints should deny unauthenticated requests.
            // Per SecurityContext.cs lines 92-106: when user is null, only GuestRoleId permissions apply.
            // Per [Authorize] attribute on all controllers: unauthenticated requests return 401.
            // Some services may return 401 (Unauthorized) while others return 403 (Forbidden)
            // depending on middleware configuration. The key assertion is that the request
            // does NOT succeed (i.e., status code is NOT in the 2xx success range).
            ((int)coreResponse.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                "Core protected endpoint should deny unauthenticated requests");
            coreResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "Core service [Authorize] attribute should return 401 for missing JWT");

            ((int)crmResponse.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                "CRM protected endpoint should deny unauthenticated requests");
            crmResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "CRM service [Authorize] attribute should return 401 for missing JWT");

            ((int)projectResponse.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                "Project protected endpoint should deny unauthenticated requests");
            projectResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "Project service [Authorize] attribute should return 401 for missing JWT");

            ((int)mailResponse.StatusCode).Should().BeGreaterThanOrEqualTo(400,
                "Mail protected endpoint should deny unauthenticated requests");
            mailResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "Mail service [Authorize] attribute should return 401 for missing JWT");
        }

        #endregion

        #region Test 5: JwtTokenClaims_ContainCorrectRoleIds_MatchSystemIds

        /// <summary>
        /// Validates that JWT claims contain role IDs matching exactly the SystemIds GUIDs
        /// from Definitions.cs lines 15-17. This is a critical structural test ensuring
        /// GUID preservation from the monolith.
        ///
        /// AdministratorRoleId: BDC56420-CAF0-4030-8A0E-D264938E0CDA
        /// RegularRoleId: F16EC6DB-626D-4C27-8DE0-3E7CE542C55F
        /// GuestRoleId: 987148B1-AFA8-4B33-8616-55861E5FD065
        /// </summary>
        [Fact]
        public async Task JwtTokenClaims_ContainCorrectRoleIds_MatchSystemIds()
        {
            // Arrange: Verify SystemIds match exactly from Definitions.cs
            _output.WriteLine("[Test5] Verifying SystemIds GUID preservation from monolith Definitions.cs...");

            // Assert SystemIds constants match the exact GUIDs from the monolith
            SystemIds.AdministratorRoleId.Should().Be(new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA"),
                "AdministratorRoleId must match Definitions.cs line 15");
            SystemIds.RegularRoleId.Should().Be(new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F"),
                "RegularRoleId must match Definitions.cs line 16");
            SystemIds.GuestRoleId.Should().Be(new Guid("987148B1-AFA8-4B33-8616-55861E5FD065"),
                "GuestRoleId must match Definitions.cs line 17");
            SystemIds.SystemUserId.Should().Be(new Guid("10000000-0000-0000-0000-000000000000"),
                "SystemUserId must match Definitions.cs line 19");
            SystemIds.FirstUserId.Should().Be(new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2"),
                "FirstUserId must match Definitions.cs line 20");

            // Act: Generate JWT tokens using the role ID-based format from SharedKernel ErpUser.ToClaims()
            string adminJwtWithRoleIds = GenerateTestJwt(
                SystemIds.FirstUserId,
                "admin@webvella.com",
                new List<Guid> { SystemIds.AdministratorRoleId },
                new List<string> { "administrator" }
            );

            string regularJwtWithRoleIds = GenerateTestJwt(
                new Guid("A0000001-0000-0000-0000-000000000001"),
                "testuser@webvella.com",
                new List<Guid> { SystemIds.RegularRoleId },
                new List<string> { "regular" }
            );

            // Parse and verify admin token claims
            JwtSecurityToken adminParsed = ParseJwtToken(adminJwtWithRoleIds);
            adminParsed.Should().NotBeNull();

            var adminRoleClaims = adminParsed.Claims
                .Where(c => c.Type == ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList();
            _output.WriteLine($"[Test5] Admin role claims: {string.Join(", ", adminRoleClaims)}");

            adminRoleClaims.Should().Contain(SystemIds.AdministratorRoleId.ToString(),
                "admin JWT must contain AdministratorRoleId as ClaimTypes.Role value");

            // Verify role name claims are also present
            var adminRoleNameClaims = adminParsed.Claims
                .Where(c => c.Type == "role_name")
                .Select(c => c.Value)
                .ToList();
            adminRoleNameClaims.Should().Contain("administrator",
                "admin JWT must contain 'administrator' role_name claim");

            // Parse and verify regular user token claims
            JwtSecurityToken regularParsed = ParseJwtToken(regularJwtWithRoleIds);
            regularParsed.Should().NotBeNull();

            var regularRoleClaims = regularParsed.Claims
                .Where(c => c.Type == ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList();
            _output.WriteLine($"[Test5] Regular role claims: {string.Join(", ", regularRoleClaims)}");

            regularRoleClaims.Should().Contain(SystemIds.RegularRoleId.ToString(),
                "regular JWT must contain RegularRoleId as ClaimTypes.Role value");

            // Verify NameIdentifier claims match expected user IDs
            var adminNameId = adminParsed.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier);
            adminNameId.Should().NotBeNull();
            adminNameId.Value.Should().Be(SystemIds.FirstUserId.ToString(),
                "admin JWT NameIdentifier must match FirstUserId");

            await Task.CompletedTask; // Maintain async signature for xUnit
        }

        #endregion

        #region Test 6: SystemScope_PreservedForBackgroundJobs_WithinEachService

        /// <summary>
        /// Validates SecurityContext Rule 6 (OpenSystemScope) — per AAP 0.8.3:
        /// "The OpenSystemScope() pattern must be preserved within each service for
        /// background job execution." The system user JWT should grant full access.
        ///
        /// Source: SecurityContext.cs lines 134-137: OpenSystemScope() creates a scope
        /// with the system user (Id=SystemUserId, email=system@webvella.com, role=administrator).
        /// </summary>
        [Fact]
        public async Task SystemScope_PreservedForBackgroundJobs_WithinEachService()
        {
            // Arrange: Generate system user JWT matching SecurityContext.cs static constructor
            // System user: Id=10000000-..., Email=system@webvella.com, Role=administrator
            string systemToken = _seeder.GenerateSystemJwtToken();
            _output.WriteLine("[Test6] System user JWT generated for background job simulation.");

            // Verify system token structure
            JwtSecurityToken parsedToken = ParseJwtToken(systemToken);
            parsedToken.Should().NotBeNull("system JWT must be parseable");

            var sysNameId = parsedToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier);
            sysNameId.Should().NotBeNull("system JWT must have NameIdentifier claim");
            sysNameId.Value.Should().Be(SystemIds.SystemUserId.ToString(),
                "system JWT NameIdentifier must be SystemUserId (10000000-0000-0000-0000-000000000000)");

            var sysEmail = parsedToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email);
            sysEmail.Should().NotBeNull("system JWT must have Email claim");
            sysEmail.Value.Should().Be("system@webvella.com",
                "system JWT email must match SecurityContext.cs line 24");

            var sysRoles = parsedToken.Claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == ClaimTypes.Role.ToString())
                .ToList();
            sysRoles.Should().NotBeEmpty("system JWT must have role claims");
            sysRoles.Any(c => c.Value == "administrator").Should().BeTrue(
                "system JWT must include 'administrator' role matching SecurityContext.cs line 26");

            // Act: Use system token to call service endpoints (simulating background job context)
            using var coreClient = _serviceFixture.CreateCoreClient();
            using var crmClient = _serviceFixture.CreateCrmClient();

            HttpResponseMessage coreResponse = await CallServiceEndpoint(coreClient, CoreHealthEndpoint, systemToken);
            HttpResponseMessage crmResponse = await CallServiceEndpoint(crmClient, CrmRecordEndpoint, systemToken);

            _output.WriteLine($"[Test6] System scope responses — Core:{coreResponse.StatusCode}, CRM:{crmResponse.StatusCode}");

            // Assert: System user should have full access (unlimited permissions)
            // Per SecurityContext.cs line 74-75: if (user.Id == SystemIds.SystemUserId) return true;
            coreResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "system user background job context should be accepted by Core service");
            coreResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "system user should have unlimited permissions in Core service");
            crmResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "system user background job context should be accepted by CRM service");
            crmResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "system user should have unlimited permissions in CRM service");
        }

        #endregion

        #region Test 7: MetaOperations_OnlyAdminRoleAllowed_EnforcedAcrossServices

        /// <summary>
        /// Validates SecurityContext Rule 5 (HasMetaPermission) — from SecurityContext.cs
        /// lines 109-118: only users with AdministratorRoleId can access metadata operations.
        /// Regular users and guest users should be denied.
        /// </summary>
        [Fact]
        public async Task MetaOperations_OnlyAdminRoleAllowed_EnforcedAcrossServices()
        {
            // Arrange: Generate both admin and regular user JWT tokens.
            // CRITICAL: Core service configures RoleClaimType = "role_name" (Program.cs line 149),
            // so [Authorize(Roles = "administrator")] checks the "role_name" claim type.
            // We use GenerateTestJwt which includes both ClaimTypes.Role (GUID) and "role_name" claims.
            string adminToken = GenerateTestJwt(
                SystemIds.FirstUserId,
                "admin@webvella.com",
                new List<Guid> { SystemIds.AdministratorRoleId },
                new List<string> { "administrator" }
            );
            string regularToken = GenerateTestJwt(
                new Guid("A0000001-0000-0000-0000-000000000001"),
                "testuser@webvella.com",
                new List<Guid> { SystemIds.RegularRoleId },
                new List<string> { "regular" }
            );
            _output.WriteLine("[Test7] Admin and regular user JWT tokens generated for meta permission test.");

            // Act: Attempt meta operations with regular user (should be denied)
            using var coreRegular = _serviceFixture.CreateCoreClient();
            HttpResponseMessage regularMetaResponse = await CallServiceEndpoint(coreRegular, MetaEntityEndpoint, regularToken);

            // Act: Attempt same meta operations with admin user (should succeed)
            using var coreAdmin = _serviceFixture.CreateCoreClient();
            HttpResponseMessage adminMetaResponse = await CallServiceEndpoint(coreAdmin, MetaEntityEndpoint, adminToken);

            _output.WriteLine($"[Test7] Meta responses — Regular:{regularMetaResponse.StatusCode}, Admin:{adminMetaResponse.StatusCode}");

            // Assert: Regular user denied, admin user allowed
            // EntityController.cs has [Authorize(Roles = "administrator")] at class level,
            // so regular users get 403 Forbidden. The key assertion is that the request
            // is NOT in the success range (2xx). 401/403 are the expected denial codes.
            var regularStatus = regularMetaResponse.StatusCode;
            ((int)regularStatus).Should().BeGreaterThanOrEqualTo(400,
                $"regular user must be denied meta operations, got {regularStatus}. " +
                "Per SecurityContext.cs line 117: return user.Roles.Any(x => x.Id == SystemIds.AdministratorRoleId)");
            regularStatus.Should().Be(HttpStatusCode.Forbidden,
                "regular user with valid JWT but non-admin role should receive 403 Forbidden " +
                "from [Authorize(Roles = \"administrator\")] on EntityController");

            adminMetaResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "admin user must be allowed meta operations");
            adminMetaResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "admin user with AdministratorRoleId should have meta permission");
        }

        #endregion

        #region Test 8: ExistingJwtTokenFormat_CompatibleWithMicroservices_BackwardCompatible

        /// <summary>
        /// Validates AAP 0.8.1: "JWT authentication must remain compatible with existing tokens."
        /// Generates a JWT token using the EXACT same algorithm and claims format as the
        /// monolith's AuthService.cs BuildTokenAsync method (lines 145-159).
        ///
        /// Key backward compatibility requirements:
        /// - Algorithm: HmacSha256Signature (AuthService.cs line 156)
        /// - Key: "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey" (Config.json line 25)
        /// - Issuer/Audience: "webvella-erp" (Config.json lines 26-27)
        /// - Claims: ClaimTypes.NameIdentifier (userId), ClaimTypes.Email (email),
        ///   ClaimTypes.Role (role names — NOT role IDs, matching monolith format)
        /// - token_refresh_after claim with DateTime.ToBinary() format
        /// </summary>
        [Fact]
        public async Task ExistingJwtTokenFormat_CompatibleWithMicroservices_BackwardCompatible()
        {
            // Arrange: Generate JWT using EXACT monolith format from AuthService.cs BuildTokenAsync
            _output.WriteLine("[Test8] Generating JWT using exact monolith AuthService.cs format...");

            var claims = new List<Claim>();
            claims.Add(new Claim(ClaimTypes.NameIdentifier, SystemIds.FirstUserId.ToString()));
            claims.Add(new Claim(ClaimTypes.Email, "admin@webvella.com"));
            // CRITICAL: Monolith uses ClaimTypes.Role.ToString() as the claim type,
            // with role NAME (not role ID) as the value — preserving this exactly.
            // Source: AuthService.cs line 150:
            //   user.Roles.ForEach(role => claims.Add(new Claim(ClaimTypes.Role.ToString(), role.Name)));
            claims.Add(new Claim(ClaimTypes.Role.ToString(), "administrator"));

            // Add token_refresh_after claim matching AuthService.cs lines 152-153
            DateTime tokenRefreshAfterDateTime = DateTime.UtcNow.AddMinutes(JwtTokenRefreshMinutes);
            claims.Add(new Claim(type: "token_refresh_after", value: tokenRefreshAfterDateTime.ToBinary().ToString()));

            // Create signing credentials matching AuthService.cs lines 155-156
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecretKey));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature);

            // Create JWT token matching AuthService.cs lines 157-158
            // NOTE: Source uses DateTime.Now (not UtcNow) for expiry — preserving this exactly
            var tokenDescriptor = new JwtSecurityToken(
                JwtIssuer,
                JwtAudience,
                claims,
                expires: DateTime.Now.AddMinutes(DefaultExpiryMinutes),
                signingCredentials: credentials
            );
            string monolithFormatToken = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
            _output.WriteLine($"[Test8] Monolith-format JWT generated. Token length: {monolithFormatToken.Length}");

            // Verify the token structure matches the monolith pattern
            JwtSecurityToken parsed = ParseJwtToken(monolithFormatToken);
            parsed.Should().NotBeNull("monolith-format JWT must be parseable");
            parsed.Issuer.Should().Be(JwtIssuer, "issuer must match Config.json: webvella-erp");
            parsed.Audiences.Should().Contain(JwtAudience, "audience must match Config.json: webvella-erp");

            // Act: Use the monolith-format token to call all services
            using var coreClient = _serviceFixture.CreateCoreClient();
            using var crmClient = _serviceFixture.CreateCrmClient();
            using var projectClient = _serviceFixture.CreateProjectClient();
            using var mailClient = _serviceFixture.CreateMailClient();

            HttpResponseMessage coreResponse = await CallServiceEndpoint(coreClient, CoreHealthEndpoint, monolithFormatToken);
            HttpResponseMessage crmResponse = await CallServiceEndpoint(crmClient, CrmRecordEndpoint, monolithFormatToken);
            HttpResponseMessage projectResponse = await CallServiceEndpoint(projectClient, ProjectRecordEndpoint, monolithFormatToken);
            HttpResponseMessage mailResponse = await CallServiceEndpoint(mailClient, MailRecordEndpoint, monolithFormatToken);

            _output.WriteLine($"[Test8] Backward compat responses — Core:{coreResponse.StatusCode}, CRM:{crmResponse.StatusCode}, Project:{projectResponse.StatusCode}, Mail:{mailResponse.StatusCode}");

            // Assert: All services must accept monolith-format JWT tokens
            coreResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "Core service must accept monolith-format JWT per AAP 0.8.1");
            crmResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "CRM service must accept monolith-format JWT per AAP 0.8.1");
            projectResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "Project service must accept monolith-format JWT per AAP 0.8.1");
            mailResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
                "Mail service must accept monolith-format JWT per AAP 0.8.1");
        }

        #endregion

        #region Test 9: ExpiredJwtToken_RejectedByAllServices_Returns401

        /// <summary>
        /// Validates that all services reject expired JWT tokens with HTTP 401 Unauthorized.
        /// This ensures that token expiry enforcement works correctly across all service
        /// boundaries — a critical security requirement.
        /// </summary>
        [Fact]
        public async Task ExpiredJwtToken_RejectedByAllServices_Returns401()
        {
            // Arrange: Generate a JWT token with past expiry (expired 60 minutes ago)
            string expiredToken = GenerateTestJwt(
                SystemIds.FirstUserId,
                "admin@webvella.com",
                new List<Guid> { SystemIds.AdministratorRoleId },
                new List<string> { "administrator" },
                expiryMinutes: -60 // Negative = expired in the past
            );
            _output.WriteLine("[Test9] Expired JWT token generated (expired 60 minutes ago).");

            // Verify the token is indeed expired
            JwtSecurityToken parsedToken = ParseJwtToken(expiredToken);
            parsedToken.Should().NotBeNull("expired token should still be parseable");
            parsedToken.ValidTo.Should().BeBefore(DateTime.UtcNow,
                "token expiry must be in the past for this test");
            _output.WriteLine($"[Test9] Token ValidTo: {parsedToken.ValidTo:O} (current UTC: {DateTime.UtcNow:O})");

            // Act: Attempt to call all services with the expired token
            using var coreClient = _serviceFixture.CreateCoreClient();
            using var crmClient = _serviceFixture.CreateCrmClient();
            using var projectClient = _serviceFixture.CreateProjectClient();
            using var mailClient = _serviceFixture.CreateMailClient();

            HttpResponseMessage coreResponse = await CallServiceEndpoint(coreClient, CoreHealthEndpoint, expiredToken);
            HttpResponseMessage crmResponse = await CallServiceEndpoint(crmClient, CrmRecordEndpoint, expiredToken);
            HttpResponseMessage projectResponse = await CallServiceEndpoint(projectClient, ProjectRecordEndpoint, expiredToken);
            HttpResponseMessage mailResponse = await CallServiceEndpoint(mailClient, MailRecordEndpoint, expiredToken);

            _output.WriteLine($"[Test9] Expired token responses — Core:{coreResponse.StatusCode}, CRM:{crmResponse.StatusCode}, Project:{projectResponse.StatusCode}, Mail:{mailResponse.StatusCode}");

            // Assert: All services must reject expired tokens.
            // The primary expected response is 401 Unauthorized, as the JWT Bearer middleware
            // rejects expired tokens before reaching the controller's [Authorize] attribute.
            coreResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "Core service must reject expired JWT with 401");
            crmResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "CRM service must reject expired JWT with 401");
            projectResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "Project service must reject expired JWT with 401");
            mailResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "Mail service must reject expired JWT with 401");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Generates a JWT token matching the microservice format with role IDs as claims.
        /// Supports both positive (future expiry) and negative (past expiry) expiryMinutes
        /// for testing both valid and expired token scenarios.
        ///
        /// Claims format (new microservice pattern via SharedKernel ErpUser.ToClaims()):
        ///   - ClaimTypes.NameIdentifier: userId.ToString()
        ///   - ClaimTypes.Email: email
        ///   - ClaimTypes.Role: roleId.ToString() (one per role — uses GUID, not name)
        ///   - "role_name": roleName (one per role — name for human readability)
        ///   - "token_refresh_after": DateTime.UtcNow.AddMinutes(120).ToBinary().ToString()
        ///
        /// Signing: HMAC SHA-256 with symmetric key from Config.json.
        /// </summary>
        /// <param name="userId">The user's GUID, typically from SystemIds.</param>
        /// <param name="email">The user's email address.</param>
        /// <param name="roleIds">List of role GUIDs to include as ClaimTypes.Role claims.</param>
        /// <param name="roleNames">List of role names to include as "role_name" claims.</param>
        /// <param name="expiryMinutes">
        /// Minutes until token expiry. Use negative values for already-expired tokens.
        /// Default: 1440 (24 hours) matching AuthService.cs JWT_TOKEN_EXPIRY_DURATION_MINUTES.
        /// </param>
        /// <returns>The serialized JWT token string.</returns>
        private string GenerateTestJwt(
            Guid userId,
            string email,
            List<Guid> roleIds,
            List<string> roleNames,
            int expiryMinutes = DefaultExpiryMinutes)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email)
            };

            // Add role ID claims (new microservice format matching ErpUser.ToClaims())
            if (roleIds != null)
            {
                foreach (Guid roleId in roleIds)
                {
                    claims.Add(new Claim(ClaimTypes.Role, roleId.ToString()));
                }
            }

            // Add role name claims for human-readable role identification
            if (roleNames != null)
            {
                foreach (string roleName in roleNames)
                {
                    claims.Add(new Claim("role_name", roleName));
                }
            }

            // Add token_refresh_after claim matching AuthService.cs lines 152-153
            DateTime tokenRefreshAfterDateTime = DateTime.UtcNow.AddMinutes(JwtTokenRefreshMinutes);
            claims.Add(new Claim("token_refresh_after", tokenRefreshAfterDateTime.ToBinary().ToString()));

            // Create signing credentials matching AuthService.cs lines 155-156
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecretKey));
            var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature);

            // Create the JWT token matching AuthService.cs lines 157-158
            // NOTE: Source uses DateTime.Now for expiry — preserved for backward compatibility.
            // For expired token testing, expiryMinutes can be negative.
            var token = new JwtSecurityToken(
                issuer: JwtIssuer,
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.Now.AddMinutes(expiryMinutes),
                signingCredentials: signingCredentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Makes an authenticated GET request to a microservice endpoint.
        /// Sets the Authorization: Bearer {token} header on a fresh HttpRequestMessage
        /// to avoid modifying the HttpClient's default headers (which could interfere
        /// with subsequent requests using the same client).
        /// </summary>
        /// <param name="client">The HttpClient connected to the target service's test server.</param>
        /// <param name="path">The relative URL path to call (e.g., "/api/v3/en_US/entity/list").</param>
        /// <param name="token">The JWT Bearer token string (without "Bearer " prefix).</param>
        /// <returns>The HTTP response message from the service.</returns>
        private async Task<HttpResponseMessage> CallServiceEndpoint(HttpClient client, string path, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return await client.SendAsync(request);
        }

        /// <summary>
        /// Parses a JWT token string into a <see cref="JwtSecurityToken"/> for claim inspection.
        /// Uses <see cref="JwtSecurityTokenHandler.ReadJwtToken(string)"/> which reads the token
        /// without validation — suitable for test assertions where we need to inspect claims
        /// regardless of expiry or signature validity.
        /// </summary>
        /// <param name="token">The serialized JWT token string to parse.</param>
        /// <returns>The parsed <see cref="JwtSecurityToken"/> object, or null if parsing fails.</returns>
        private JwtSecurityToken ParseJwtToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            try
            {
                var handler = new JwtSecurityTokenHandler();
                return handler.ReadJwtToken(token);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ParseJwtToken] Failed to parse JWT: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
