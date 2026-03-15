using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Identity.DataAccess;
using WebVellaErp.Identity.Functions;
using WebVellaErp.Identity.Models;
using WebVellaErp.Identity.Services;
using Xunit;

namespace WebVellaErp.Identity.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="RoleHandler"/> Lambda handler class that replaces
    /// SecurityManager.SaveRole() and SecurityManager.GetAllRoles() role management operations.
    /// All AWS dependencies (DynamoDB, Cognito, SNS) are mocked via Moq — zero real AWS SDK calls.
    ///
    /// <para>
    /// Tests cover HandleGetAllRoles (GET /v1/roles), HandleSaveRole (POST/PUT /v1/roles),
    /// and HandleDeleteRole (DELETE /v1/roles/{roleId}) including system role protection,
    /// name uniqueness validation, and authorization checks.
    /// </para>
    ///
    /// <para>Coverage target: &gt;80% per AAP Section 0.8.4.</para>
    /// </summary>
    public class RoleHandlerTests
    {
        #region Fields and Constructor

        private readonly Mock<IUserRepository> _mockUserRepository;
        private readonly Mock<IPermissionService> _mockPermissionService;
        private readonly Mock<ICognitoService> _mockCognitoService;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<RoleHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly RoleHandler _handler;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Constructor — initializes all mocks and builds <see cref="RoleHandler"/> via the
        /// test-friendly <c>IServiceProvider</c> constructor (RoleHandler.cs line 150).
        /// Mocked dependencies:
        /// <list type="bullet">
        ///   <item><see cref="IUserRepository"/> — DynamoDB role data access</item>
        ///   <item><see cref="IPermissionService"/> — authorization checks</item>
        ///   <item><see cref="ICognitoService"/> — Cognito group management</item>
        ///   <item><see cref="IAmazonSimpleNotificationService"/> — SNS event publishing</item>
        ///   <item><see cref="ILogger{RoleHandler}"/> — structured logger</item>
        /// </list>
        /// </summary>
        public RoleHandlerTests()
        {
            _mockUserRepository = new Mock<IUserRepository>();
            _mockPermissionService = new Mock<IPermissionService>();
            _mockCognitoService = new Mock<ICognitoService>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<RoleHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Lambda context returns a request ID for structured logging correlation
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());

            // Default SNS mock — return empty response to prevent null-ref in non-fatal publish path
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Build ServiceCollection with mocked dependencies matching RoleHandler(IServiceProvider) constructor
            var services = new ServiceCollection();
            services.AddSingleton<IUserRepository>(_mockUserRepository.Object);
            services.AddSingleton<IPermissionService>(_mockPermissionService.Object);
            services.AddSingleton<ICognitoService>(_mockCognitoService.Object);
            services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);
            services.AddSingleton<ILogger<RoleHandler>>(_mockLogger.Object);

            var serviceProvider = services.BuildServiceProvider();
            _handler = new RoleHandler(serviceProvider);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates an <see cref="APIGatewayHttpApiV2ProxyRequest"/> with configurable body,
        /// path parameters, headers, and JWT authorizer claims. Follows the exact structure
        /// used by HTTP API Gateway v2 integration (matching RoleHandler.ExtractCallerFromContext).
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateApiGatewayRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? headers = null,
            Dictionary<string, string>? claims = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParameters ?? new Dictionary<string, string>(),
                Headers = headers ?? new Dictionary<string, string>
                {
                    { "x-correlation-id", Guid.NewGuid().ToString() }
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = claims ?? new Dictionary<string, string>()
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates JWT claims representing an administrator user. Maps to the handler's
        /// ExtractCallerFromContext method which reads sub, email, cognito:username,
        /// given_name, family_name, and cognito:groups from the JWT claims dictionary.
        /// The "administrator" group maps to Role.AdministratorRoleId via MapGroupsToRoles.
        /// </summary>
        private static Dictionary<string, string> CreateAdminClaims(Guid? userId = null)
        {
            return new Dictionary<string, string>
            {
                { "sub", (userId ?? Guid.NewGuid()).ToString() },
                { "email", "admin@webvella.com" },
                { "cognito:username", "admin" },
                { "given_name", "Admin" },
                { "family_name", "User" },
                { "cognito:groups", "[\"administrator\"]" }
            };
        }

        /// <summary>
        /// Creates JWT claims representing a non-admin (regular) user. The "regular" group
        /// maps to Role.RegularRoleId, which will cause HasMetaPermission to return false
        /// when the IPermissionService mock is configured to deny non-admin access.
        /// </summary>
        private static Dictionary<string, string> CreateRegularUserClaims(Guid? userId = null)
        {
            return new Dictionary<string, string>
            {
                { "sub", (userId ?? Guid.NewGuid()).ToString() },
                { "email", "user@webvella.com" },
                { "cognito:username", "regularuser" },
                { "given_name", "Regular" },
                { "family_name", "User" },
                { "cognito:groups", "[\"regular\"]" }
            };
        }

        /// <summary>
        /// Creates a list of the three well-known system roles (Administrator, Regular, Guest)
        /// with their canonical IDs from Definitions.cs lines 15-17, for use in mock returns
        /// and test data setup.
        /// </summary>
        private static List<Role> CreateSystemRoles()
        {
            return new List<Role>
            {
                new Role
                {
                    Id = Role.AdministratorRoleId,
                    Name = "Administrator",
                    Description = "System administrator role",
                    CognitoGroupName = "administrator"
                },
                new Role
                {
                    Id = Role.RegularRoleId,
                    Name = "Regular",
                    Description = "Regular user role",
                    CognitoGroupName = "regular"
                },
                new Role
                {
                    Id = Role.GuestRoleId,
                    Name = "Guest",
                    Description = "Guest user role",
                    CognitoGroupName = "guest"
                }
            };
        }

        #endregion

        #region Phase 2: HandleGetAllRoles Tests

        /// <summary>
        /// Happy path: GET /v1/roles returns all roles with 200 status.
        /// Source: SecurityManager.GetAllRoles() — EQL: "SELECT * FROM role".
        /// Verifies response contains success=true and an array of 3 system roles,
        /// each with Id, Name, and Description properties.
        /// </summary>
        [Fact]
        public async Task HandleGetAllRoles_ReturnsListOfRoles()
        {
            // Arrange
            var testRoles = CreateSystemRoles();
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(testRoles);

            var request = CreateApiGatewayRequest();

            // Act
            var response = await _handler.HandleGetAllRoles(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNull();

            var body = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            body.GetProperty("success").GetBoolean().Should().BeTrue();

            // Verify the response contains 3 roles with expected properties
            var rolesArray = body.GetProperty("object");
            rolesArray.GetArrayLength().Should().Be(3);
            rolesArray[0].TryGetProperty("id", out _).Should().BeTrue();
            rolesArray[0].TryGetProperty("name", out _).Should().BeTrue();
            rolesArray[0].TryGetProperty("description", out _).Should().BeTrue();

            // Verify repository was called exactly once
            _mockUserRepository.Verify(
                x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Edge case: GET /v1/roles returns 200 with empty array when no roles exist.
        /// Verifies the handler does not fail when the repository returns an empty list.
        /// </summary>
        [Fact]
        public async Task HandleGetAllRoles_EmptyList_Returns200WithEmptyArray()
        {
            // Arrange
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Role>());

            var request = CreateApiGatewayRequest();

            // Act
            var response = await _handler.HandleGetAllRoles(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNull();

            var body = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            body.GetProperty("success").GetBoolean().Should().BeTrue();
            body.GetProperty("object").GetArrayLength().Should().Be(0);
        }

        #endregion

        #region Phase 3: HandleSaveRole — Create Tests

        /// <summary>
        /// Happy path create: POST /v1/roles with valid name and description.
        /// Source: SecurityManager.SaveRole() CREATE path (lines 329-346).
        /// Handler returns 201 for created resources (correct HTTP semantics).
        /// Verifies the role is persisted via SaveRoleAsync with correct name and description.
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Create_ValidRole_Returns200()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateSystemRoles()); // No collision with "NewRole"
            _mockUserRepository
                .Setup(x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var requestBody = JsonSerializer.Serialize(
                new { name = "NewRole", description = "A new role" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert — handler returns 201 for resource creation (HTTP POST create semantics)
            response.StatusCode.Should().Be(201);
            response.Body.Should().Contain("Role created successfully.");

            var body = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            body.GetProperty("success").GetBoolean().Should().BeTrue();

            // Verify persistence was called with correct role properties
            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(
                    It.Is<Role>(r => r.Name == "NewRole" && r.Description == "A new role"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Validation: POST /v1/roles without name field returns 400.
        /// Source: SecurityManager.SaveRole() lines 335-336: if (string.IsNullOrWhiteSpace(role.Name))
        /// Error message: "Name is required."
        /// SaveRoleRequest.Name defaults to string.Empty when not provided in JSON.
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Create_MissingName_Returns400()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Role>());

            // POST without name field — Name defaults to string.Empty in SaveRoleRequest
            var requestBody = JsonSerializer.Serialize(
                new { description = "Some description" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Name is required.");

            // Verify SaveRoleAsync was NOT called — validation failed before persistence
            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Validation: POST /v1/roles with duplicate name returns 400.
        /// Source: SecurityManager.SaveRole() lines 337-338:
        /// if (allRoles.Any(x =&gt; x.Name == role.Name)) → "Role with same name already exists"
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Create_DuplicateName_Returns400()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);

            var existingRoles = new List<Role>
            {
                new Role { Id = Guid.NewGuid(), Name = "ExistingRole", Description = "Already exists" }
            };
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingRoles);

            // POST with the same name as an existing role
            var requestBody = JsonSerializer.Serialize(
                new { name = "ExistingRole", description = "Duplicate attempt" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Role with same name already exists");

            // Verify SaveRoleAsync was NOT called — validation failed before persistence
            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Edge case: POST /v1/roles without description field defaults to empty string.
        /// Source: SecurityManager.SaveRole() lines 305-306:
        /// if (role.Description is null) role.Description = String.Empty;
        /// Handler code: var description = saveRequest.Description ?? string.Empty;
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Create_NullDescription_DefaultsToEmptyString()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Role>());
            _mockUserRepository
                .Setup(x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // POST without description field — Description is nullable in SaveRoleRequest
            var requestBody = JsonSerializer.Serialize(
                new { name = "RoleWithoutDesc" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert: Description defaults to empty string (not null) per source lines 305-306
            response.StatusCode.Should().Be(201);

            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(
                    It.Is<Role>(r => r.Description == string.Empty && r.Name == "RoleWithoutDesc"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region Phase 4: HandleSaveRole — Update Tests

        /// <summary>
        /// Happy path update: PUT /v1/roles/{roleId} with valid changes returns 200.
        /// Source: SecurityManager.SaveRole() UPDATE path (lines 307-327).
        /// Tests updating description while keeping the same name.
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Update_ValidChanges_Returns200()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var existingRoles = new List<Role>
            {
                new Role
                {
                    Id = roleId,
                    Name = "Editors",
                    Description = "Editor role",
                    CognitoGroupName = "editors"
                }
            };

            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingRoles);
            _mockUserRepository
                .Setup(x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // PUT with updated description (same name — no name uniqueness check triggered)
            var requestBody = JsonSerializer.Serialize(
                new { name = "Editors", description = "Updated editor role description" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                pathParameters: new Dictionary<string, string> { { "roleId", roleId.ToString() } },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("Role updated successfully.");

            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(
                    It.Is<Role>(r => r.Description == "Updated editor role description"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// CRITICAL: Name uniqueness is ONLY validated when the name has actually changed.
        /// Source: SecurityManager.SaveRole() line 312: if (existingRole.Name != role.Name)
        ///
        /// When updating a role with the SAME name (e.g., only changing description), the name
        /// uniqueness check is completely skipped. This prevents false-positive "duplicate name"
        /// errors when a role's own name appears in allRoles.
        ///
        /// This test sends an update with the identical name — the handler should succeed
        /// even though allRoles.Any(x =&gt; x.Name == "Editors") would be true (the role itself).
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Update_NameUniquenessOnlyWhenChanged()
        {
            // Arrange — Role "Editors" exists alongside "Viewers"
            var roleId = Guid.NewGuid();
            var existingRoles = new List<Role>
            {
                new Role { Id = roleId, Name = "Editors", Description = "Editor role" },
                new Role { Id = Guid.NewGuid(), Name = "Viewers", Description = "Viewer role" }
            };

            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingRoles);
            _mockUserRepository
                .Setup(x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Update with SAME name "Editors" — name uniqueness check MUST be skipped
            // (source line 312: conditional block only entered when names differ)
            var requestBody = JsonSerializer.Serialize(
                new { name = "Editors", description = "Changed description only" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                pathParameters: new Dictionary<string, string> { { "roleId", roleId.ToString() } },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert — should succeed because name did not change, so uniqueness is never validated
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("Role updated successfully.");

            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Validation: PUT /v1/roles/{roleId} with empty name when name changed returns 400.
        /// Source: SecurityManager.SaveRole() lines 316-317.
        /// Existing role has name "OldName"; update sends empty name (name changed) triggering
        /// the name validation block → "Name is required."
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_Update_EmptyNameWhenChanged_Returns400()
        {
            // Arrange — existing role with name "OldName"
            var roleId = Guid.NewGuid();
            var existingRoles = new List<Role>
            {
                new Role { Id = roleId, Name = "OldName", Description = "Original description" }
            };

            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingRoles);

            // Update sends empty name — name changed from "OldName" to "", triggering validation
            var requestBody = JsonSerializer.Serialize(
                new { name = "", description = "Trying to clear name" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                pathParameters: new Dictionary<string, string> { { "roleId", roleId.ToString() } },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert — source lines 316-317: name required when name changes to empty
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Name is required.");

            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        #endregion

        #region Phase 5: System Role Protection Tests (HandleDeleteRole)

        /// <summary>
        /// System role protection: DELETE /v1/roles/{AdministratorRoleId} returns 400.
        /// Administrator role (BDC56420-CAF0-4030-8A0E-D264938E0CDA) from Definitions.cs line 15
        /// is a well-known system role that cannot be deleted.
        /// </summary>
        [Fact]
        public async Task HandleDeleteRole_SystemAdministratorRole_Returns400()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);

            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string>
                {
                    { "roleId", Role.AdministratorRoleId.ToString() }
                },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleDeleteRole(request, _mockLambdaContext.Object);

            // Assert — BDC56420-CAF0-4030-8A0E-D264938E0CDA cannot be deleted
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Cannot delete system role");

            // Verify DeleteRoleAsync was NEVER called
            _mockUserRepository.Verify(
                x => x.DeleteRoleAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// System role protection: DELETE /v1/roles/{RegularRoleId} returns 400.
        /// Regular role (F16EC6DB-626D-4C27-8DE0-3E7CE542C55F) from Definitions.cs line 16
        /// is a well-known system role that cannot be deleted.
        /// </summary>
        [Fact]
        public async Task HandleDeleteRole_SystemRegularRole_Returns400()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);

            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string>
                {
                    { "roleId", Role.RegularRoleId.ToString() }
                },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleDeleteRole(request, _mockLambdaContext.Object);

            // Assert — F16EC6DB-626D-4C27-8DE0-3E7CE542C55F cannot be deleted
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Cannot delete system role");

            _mockUserRepository.Verify(
                x => x.DeleteRoleAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// System role protection: DELETE /v1/roles/{GuestRoleId} returns 400.
        /// Guest role (987148B1-AFA8-4B33-8616-55861E5FD065) from Definitions.cs line 17
        /// is a well-known system role that cannot be deleted.
        /// </summary>
        [Fact]
        public async Task HandleDeleteRole_SystemGuestRole_Returns400()
        {
            // Arrange
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);

            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string>
                {
                    { "roleId", Role.GuestRoleId.ToString() }
                },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleDeleteRole(request, _mockLambdaContext.Object);

            // Assert — 987148B1-AFA8-4B33-8616-55861E5FD065 cannot be deleted
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Cannot delete system role");

            _mockUserRepository.Verify(
                x => x.DeleteRoleAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Positive case: DELETE /v1/roles/{customRoleId} returns 200 for non-system roles.
        /// Custom roles (those not in SystemRoleIds) can be freely deleted by administrators.
        /// Verifies DeleteRoleAsync is called exactly once with the correct role ID.
        /// </summary>
        [Fact]
        public async Task HandleDeleteRole_NonSystemRole_Returns200()
        {
            // Arrange
            var customRoleId = Guid.NewGuid();
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetRoleByIdAsync(customRoleId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Role
                {
                    Id = customRoleId,
                    Name = "CustomRole",
                    Description = "A custom deletable role"
                });
            _mockUserRepository
                .Setup(x => x.DeleteRoleAsync(customRoleId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateApiGatewayRequest(
                pathParameters: new Dictionary<string, string>
                {
                    { "roleId", customRoleId.ToString() }
                },
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleDeleteRole(request, _mockLambdaContext.Object);

            // Assert — custom roles can be successfully deleted
            response.StatusCode.Should().Be(200);

            var body = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            body.GetProperty("success").GetBoolean().Should().BeTrue();

            _mockUserRepository.Verify(
                x => x.DeleteRoleAsync(customRoleId, It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region Phase 6: Authorization Tests

        /// <summary>
        /// Authorization: Non-admin user receives 403 Forbidden when attempting to save a role.
        /// Source: SecurityContext.HasMetaPermission() checks user.Roles.Any(x =&gt; x.Id == AdministratorRoleId).
        /// The handler calls _permissionService.HasMetaPermission(caller) which returns false for non-admins.
        /// Verifies no persistence or data retrieval operations occur after authorization failure.
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_NonAdminUser_Returns403()
        {
            // Arrange — HasMetaPermission returns false for non-admin user
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(false);

            var requestBody = JsonSerializer.Serialize(
                new { name = "UnauthorizedRole", description = "Should not be created" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                claims: CreateRegularUserClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert — 403 Forbidden with descriptive message
            response.StatusCode.Should().Be(403);
            response.Body.Should().Contain("Only administrators can manage roles.");

            // Verify no persistence or data retrieval operations occurred
            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()),
                Times.Never());
            _mockUserRepository.Verify(
                x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Authorization: Admin user successfully creates a role (HasMetaPermission returns true).
        /// End-to-end admin authorization flow: JWT claims → ExtractCallerFromContext → User with
        /// administrator role → HasMetaPermission(true) → operation proceeds → role saved.
        /// </summary>
        [Fact]
        public async Task HandleSaveRole_AdminUser_Succeeds()
        {
            // Arrange — HasMetaPermission returns true for admin user
            _mockPermissionService
                .Setup(x => x.HasMetaPermission(It.IsAny<User>()))
                .Returns(true);
            _mockUserRepository
                .Setup(x => x.GetAllRolesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Role>());
            _mockUserRepository
                .Setup(x => x.SaveRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var requestBody = JsonSerializer.Serialize(
                new { name = "AuthorizedRole", description = "Created by admin" }, _jsonOptions);
            var request = CreateApiGatewayRequest(
                body: requestBody,
                claims: CreateAdminClaims());

            // Act
            var response = await _handler.HandleSaveRole(request, _mockLambdaContext.Object);

            // Assert — admin user is authorized, role creation proceeds successfully
            response.StatusCode.Should().Be(201);
            response.Body.Should().Contain("Role created successfully.");

            var body = JsonSerializer.Deserialize<JsonElement>(response.Body, _jsonOptions);
            body.GetProperty("success").GetBoolean().Should().BeTrue();

            _mockUserRepository.Verify(
                x => x.SaveRoleAsync(
                    It.Is<Role>(r => r.Name == "AuthorizedRole" && r.Description == "Created by admin"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion
    }
}
