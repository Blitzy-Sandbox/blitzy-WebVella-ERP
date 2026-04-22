// ──────────────────────────────────────────────────────────────────────────────
// UserHandlerTests.cs — xUnit Unit Tests for UserHandler Lambda Handler
//
// Namespace: WebVellaErp.Identity.Tests.Unit
// Tests the UserHandler Lambda handler class that replaces SecurityManager
// user CRUD operations. All AWS dependencies are mocked via Moq — zero real
// AWS SDK calls.
//
// Coverage target: >80% per AAP Section 0.8.4
// Validation logic source: SecurityManager.SaveUser (lines 191-293)
// ──────────────────────────────────────────────────────────────────────────────

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
    /// Comprehensive unit tests for <see cref="UserHandler"/> Lambda handler.
    /// Tests user CRUD operations at the Lambda handler level using mocked dependencies.
    /// Validates all business rules originally in SecurityManager.SaveUser are preserved:
    ///   - Username: required, unique (create); conditional on change (update)
    ///   - Email: required, unique, valid format (create); conditional on change (update)
    ///   - Password: required (create only)
    ///   - Idempotency key support on all write operations
    ///   - ValidationException-style error accumulation pattern
    /// </summary>
    public class UserHandlerTests
    {
        // ─── Mock Dependencies ───────────────────────────────────────────
        private readonly Mock<ICognitoService> _mockCognitoService;
        private readonly Mock<IUserRepository> _mockUserRepository;
        private readonly Mock<IPermissionService> _mockPermissionService;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<UserHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;

        // ─── System Under Test ───────────────────────────────────────────
        private readonly UserHandler _handler;

        // ─── JSON Serializer Options (matches UserHandler.JsonOptions) ───
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Initializes all mocks and builds UserHandler via the test-friendly
        /// IServiceProvider constructor (UserHandler.cs line 167).
        /// </summary>
        public UserHandlerTests()
        {
            _mockCognitoService = new Mock<ICognitoService>();
            _mockUserRepository = new Mock<IUserRepository>();
            _mockPermissionService = new Mock<IPermissionService>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<UserHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default: permission checks pass (admin caller)
            _mockPermissionService
                .Setup(p => p.HasMetaPermission(It.IsAny<User?>()))
                .Returns(true);

            // Default: SNS publish succeeds
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Default: GetUserRolesAsync returns empty list (populated by specific tests)
            _mockUserRepository
                .Setup(r => r.GetUserRolesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Role>());

            // Build DI container with mocked dependencies
            var services = new ServiceCollection();
            services.AddSingleton(_mockCognitoService.Object);
            services.AddSingleton<ICognitoService>(_mockCognitoService.Object);
            services.AddSingleton(_mockUserRepository.Object);
            services.AddSingleton<IUserRepository>(_mockUserRepository.Object);
            services.AddSingleton(_mockPermissionService.Object);
            services.AddSingleton<IPermissionService>(_mockPermissionService.Object);
            services.AddSingleton(_mockSnsClient.Object);
            services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);
            services.AddSingleton(_mockLogger.Object);
            services.AddSingleton<ILogger<UserHandler>>(_mockLogger.Object);

            var serviceProvider = services.BuildServiceProvider();
            _handler = new UserHandler(serviceProvider);
        }

        // ═══════════════════════════════════════════════════════════════════
        // Helper Methods
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds an <see cref="APIGatewayHttpApiV2ProxyRequest"/> with configurable
        /// HTTP method, body, path parameters, headers, and JWT authorizer claims.
        /// Simulates the API Gateway HTTP API v2 proxy integration event format.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateApiGatewayRequest(
            string httpMethod,
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? headers = null,
            Dictionary<string, string>? jwtClaims = null)
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParameters ?? new Dictionary<string, string>(),
                Headers = headers ?? new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = httpMethod
                    }
                }
            };

            // Set up JWT authorizer claims if provided (for caller extraction)
            if (jwtClaims != null)
            {
                request.RequestContext.Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                {
                    Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                    {
                        Claims = jwtClaims
                    }
                };
            }

            return request;
        }

        /// <summary>
        /// Creates a fully populated test user with roles for mocking repository returns.
        /// Uses well-known system IDs from Role class for admin role assignment.
        /// </summary>
        private static User CreateTestUser(Guid? userId = null, string? email = null, string? username = null)
        {
            var id = userId ?? Guid.NewGuid();
            return new User
            {
                Id = id,
                Email = email ?? $"test-{id:N}@example.com",
                Username = username ?? $"testuser-{id:N}",
                Password = "hashed-password",
                FirstName = "Test",
                LastName = "User",
                Image = "/images/test-avatar.png",
                Enabled = true,
                Verified = true,
                CreatedOn = DateTime.UtcNow.AddDays(-30),
                LastLoggedIn = DateTime.UtcNow.AddHours(-1),
                Roles = new List<Role>
                {
                    new Role
                    {
                        Id = Role.AdministratorRoleId,
                        Name = "administrator",
                        Description = "Full system access"
                    }
                }
            };
        }

        /// <summary>
        /// Creates a <see cref="SaveUserRequest"/> JSON body for create operations.
        /// Matches the SaveUserRequest DTO property names (camelCase serialization).
        /// </summary>
        private static string CreateUserRequestBody(
            string? email = "newuser@example.com",
            string? username = "newuser",
            string? password = "SecurePass123!",
            string? firstName = "New",
            string? lastName = "User",
            bool enabled = true,
            bool verified = true,
            List<Guid>? roleIds = null)
        {
            var requestObj = new Dictionary<string, object?>();

            if (email != null) requestObj["email"] = email;
            if (username != null) requestObj["username"] = username;
            if (password != null) requestObj["password"] = password;
            if (firstName != null) requestObj["first_name"] = firstName;
            if (lastName != null) requestObj["last_name"] = lastName;
            requestObj["enabled"] = enabled;
            requestObj["verified"] = verified;
            if (roleIds != null) requestObj["role_ids"] = roleIds;

            return JsonSerializer.Serialize(requestObj, JsonOptions);
        }

        /// <summary>
        /// Sets up mock JWT claims for an admin caller so that ExtractCallerFromContext
        /// returns a valid admin user. The caller is used for permission checks.
        /// </summary>
        private User SetupAdminCaller()
        {
            var adminUser = CreateTestUser();
            var adminUserId = adminUser.Id;

            _mockUserRepository
                .Setup(r => r.GetUserByIdAsync(adminUserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(adminUser);

            _mockUserRepository
                .Setup(r => r.GetUserRolesAsync(adminUserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(adminUser.Roles);

            return adminUser;
        }

        /// <summary>
        /// Creates JWT claims dictionary for API Gateway authorizer context.
        /// </summary>
        private static Dictionary<string, string> CreateAdminJwtClaims(Guid userId)
        {
            return new Dictionary<string, string>
            {
                { "user_id", userId.ToString() },
                { "email", $"admin-{userId:N}@example.com" },
                { "sub", Guid.NewGuid().ToString() }
            };
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 2: HandleGetUser Tests
        // Source: SecurityManager.GetUser(Guid userId) — lines 36-47
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Happy path: GET /v1/users/{userId} with a valid userId that exists.
        /// Source logic: EQL SELECT *, $user_role.* FROM user WHERE id = @id → result[0]
        /// Verifies 200 status code and response body contains user data with roles.
        /// </summary>
        [Fact]
        public async Task HandleGetUser_ExistingUser_Returns200WithUser()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var testUser = CreateTestUser(userId: userId, email: "existing@example.com", username: "existinguser");
            var adminRole = new Role
            {
                Id = Role.AdministratorRoleId,
                Name = "administrator",
                Description = "Full system access"
            };

            _mockUserRepository
                .Setup(r => r.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(testUser);

            _mockUserRepository
                .Setup(r => r.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Role> { adminRole });

            var request = CreateApiGatewayRequest(
                httpMethod: "GET",
                pathParameters: new Dictionary<string, string> { { "userId", userId.ToString() } });

            // Act
            var response = await _handler.HandleGetUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();
            responseBody.GetProperty("object").GetProperty("id")
                .GetString().Should().Be(userId.ToString());
            responseBody.GetProperty("object").GetProperty("email")
                .GetString().Should().Be("existing@example.com");

            _mockUserRepository.Verify(
                r => r.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()),
                Times.Once());
            _mockUserRepository.Verify(
                r => r.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Non-existent user: GET /v1/users/{userId} where userId does not match any record.
        /// Replaces source lines 42-43: if (result.Count != 1) return null → handler returns 404.
        /// </summary>
        [Fact]
        public async Task HandleGetUser_NonExistentUser_Returns404()
        {
            // Arrange
            var userId = Guid.NewGuid();
            _mockUserRepository
                .Setup(r => r.GetUserByIdAsync(userId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var request = CreateApiGatewayRequest(
                httpMethod: "GET",
                pathParameters: new Dictionary<string, string> { { "userId", userId.ToString() } });

            // Act
            var response = await _handler.HandleGetUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(404);
            response.Body.Should().Contain("not found");
        }

        /// <summary>
        /// Invalid GUID format: GET /v1/users/{userId} with a non-GUID string.
        /// UserHandler validates Guid.TryParse and returns 400 for invalid formats.
        /// </summary>
        [Fact]
        public async Task HandleGetUser_InvalidGuidFormat_Returns400()
        {
            // Arrange
            var request = CreateApiGatewayRequest(
                httpMethod: "GET",
                pathParameters: new Dictionary<string, string> { { "userId", "not-a-guid" } });

            // Act
            var response = await _handler.HandleGetUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);

            _mockUserRepository.Verify(
                r => r.GetUserByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 3: HandleSaveUser (Create) Tests
        // Source: SecurityManager.SaveUser() CREATE path — lines 255-292
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Happy path create: POST /v1/users with valid user data.
        /// Verifies user is created via CognitoService and 200 is returned.
        /// Source: full CREATE path lines 255-292 with all validations passing.
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_ValidUser_Returns200()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            // No existing users with same username/email
            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            // CognitoService.CreateUserAsync returns the created user
            _mockCognitoService
                .Setup(c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User u, string p, CancellationToken ct) =>
                {
                    u.Id = u.Id == Guid.Empty ? Guid.NewGuid() : u.Id;
                    return u;
                });

            var body = CreateUserRequestBody(
                email: "valid@example.com",
                username: "validuser",
                password: "SecurePass123!",
                firstName: "Valid",
                lastName: "User");

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(
                    It.Is<User>(u => u.Email == "valid@example.com" && u.Username == "validuser"),
                    It.Is<string>(p => p == "SecurePass123!"), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Missing username: POST /v1/users with empty username.
        /// Source line 267-268: if (string.IsNullOrWhiteSpace(user.Username))
        ///   → error "Username is required."
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_MissingUsername_Returns400()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var body = CreateUserRequestBody(
                email: "test@example.com",
                username: "",
                password: "SecurePass123!");

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Username is required.");

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Duplicate username: POST /v1/users with a username already taken.
        /// Source line 269-270: if (GetUserByUsername(user.Username) != null)
        ///   → error "Username is already registered to another user. It must be unique."
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_DuplicateUsername_Returns400()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var existingUser = CreateTestUser(username: "takenuser");
            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync("takenuser", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingUser);
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var body = CreateUserRequestBody(
                email: "unique@example.com",
                username: "takenuser",
                password: "SecurePass123!");

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Username is already registered to another user. It must be unique.");

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Missing email: POST /v1/users with empty email field.
        /// Source line 272-273: if (string.IsNullOrWhiteSpace(user.Email))
        ///   → error "Email is required."
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_MissingEmail_Returns400()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var body = CreateUserRequestBody(
                email: "",
                username: "validuser",
                password: "SecurePass123!");

            // No existing username
            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Email is required.");

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Duplicate email: POST /v1/users with an email already registered.
        /// Source line 274-275: if (GetUser(user.Email) != null)
        ///   → error "Email is already registered to another user. It must be unique."
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_DuplicateEmail_Returns400()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var existingUser = CreateTestUser(email: "taken@example.com");
            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync("taken@example.com", It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingUser);

            var body = CreateUserRequestBody(
                email: "taken@example.com",
                username: "newuser",
                password: "SecurePass123!");

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Email is already registered to another user. It must be unique.");

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Invalid email format: POST /v1/users with a non-valid email string.
        /// Source lines 276-277 using IsValidEmail which calls System.Net.Mail.MailAddress
        ///   → error "Email is not valid."
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_InvalidEmailFormat_Returns400()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            // No existing user with this email (so uniqueness check passes, format check runs)
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync("not-an-email", It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var body = CreateUserRequestBody(
                email: "not-an-email",
                username: "validuser",
                password: "SecurePass123!");

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Email is not valid.");

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Missing password: POST /v1/users with no password field.
        /// Source lines 279-280: if (string.IsNullOrWhiteSpace(user.Password))
        ///   → error "Password is required."
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_MissingPassword_Returns400()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var body = CreateUserRequestBody(
                email: "test@example.com",
                username: "validuser",
                password: null);

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("Password is required.");

            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 4: HandleSaveUser (Update) Tests
        // Source: SecurityManager.SaveUser() UPDATE path — lines 202-253
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Happy path update: PUT /v1/users/{userId} with valid changed fields.
        /// Source: UPDATE path lines 202-253 with all validations passing.
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Update_ValidChanges_Returns200()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var existingUserId = Guid.NewGuid();
            var existingUser = CreateTestUser(userId: existingUserId, email: "existing@example.com", username: "existinguser");

            _mockUserRepository
                .Setup(r => r.GetUserByIdAsync(existingUserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingUser);

            // New username/email are unique
            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync("updateduser", It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync("updated@example.com", It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            // CognitoService.UpdateUserAsync returns the updated user
            _mockCognitoService
                .Setup(c => c.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User u, CancellationToken ct) => u);

            var body = CreateUserRequestBody(
                email: "updated@example.com",
                username: "updateduser",
                password: null,
                firstName: "Updated",
                lastName: "Name");

            var request = CreateApiGatewayRequest(
                httpMethod: "PUT",
                body: body,
                pathParameters: new Dictionary<string, string> { { "userId", existingUserId.ToString() } },
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);

            var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();

            _mockCognitoService.Verify(
                c => c.UpdateUserAsync(It.Is<User>(u =>
                    u.Id == existingUserId &&
                    u.Email == "updated@example.com" &&
                    u.Username == "updateduser"), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Conditional username validation on update: only validates username uniqueness
        /// when the username has actually changed from the existing value.
        /// Source line 206: if (existingUser.Username != user.Username)
        ///   - SAME username as existing → no validation error
        ///   - DIFFERENT username → validates uniqueness
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Update_ConditionalUsernameValidation_OnlyWhenChanged()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var existingUserId = Guid.NewGuid();
            var existingUser = CreateTestUser(
                userId: existingUserId,
                email: "user@example.com",
                username: "existingname");

            _mockUserRepository
                .Setup(r => r.GetUserByIdAsync(existingUserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingUser);

            // CognitoService.UpdateUserAsync returns the updated user
            _mockCognitoService
                .Setup(c => c.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User u, CancellationToken ct) => u);

            // Test 1: Update with SAME username → should NOT trigger username validation
            var bodyWithSameUsername = CreateUserRequestBody(
                email: "user@example.com",
                username: "existingname",
                password: null);

            var requestSameUsername = CreateApiGatewayRequest(
                httpMethod: "PUT",
                body: bodyWithSameUsername,
                pathParameters: new Dictionary<string, string> { { "userId", existingUserId.ToString() } },
                jwtClaims: claims);

            // Act 1
            var responseSameUsername = await _handler.HandleSaveUser(requestSameUsername, _mockLambdaContext.Object);

            // Assert 1: Should succeed — no username uniqueness check needed
            responseSameUsername.StatusCode.Should().Be(200);
            _mockUserRepository.Verify(
                r => r.GetUserByUsernameAsync("existingname", It.IsAny<CancellationToken>()),
                Times.Never(),
                "Username uniqueness should NOT be checked when username hasn't changed");

            // Test 2: Update with DIFFERENT username that is already taken
            var duplicateUser = CreateTestUser(username: "takenname");
            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync("takenname", It.IsAny<CancellationToken>()))
                .ReturnsAsync(duplicateUser);

            var bodyWithNewUsername = CreateUserRequestBody(
                email: "user@example.com",
                username: "takenname",
                password: null);

            var requestNewUsername = CreateApiGatewayRequest(
                httpMethod: "PUT",
                body: bodyWithNewUsername,
                pathParameters: new Dictionary<string, string> { { "userId", existingUserId.ToString() } },
                jwtClaims: claims);

            // Act 2
            var responseNewUsername = await _handler.HandleSaveUser(requestNewUsername, _mockLambdaContext.Object);

            // Assert 2: Should fail — different username triggers uniqueness validation
            responseNewUsername.StatusCode.Should().Be(400);
            responseNewUsername.Body.Should().Contain("Username is already registered to another user. It must be unique.");
        }

        /// <summary>
        /// Conditional email validation on update: only validates email uniqueness and format
        /// when the email has actually changed from the existing value.
        /// Source line 216: if (existingUser.Email != user.Email)
        ///   - SAME email as existing → no validation error
        ///   - DIFFERENT email → validates uniqueness + format
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Update_ConditionalEmailValidation_OnlyWhenChanged()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            var existingUserId = Guid.NewGuid();
            var existingUser = CreateTestUser(
                userId: existingUserId,
                email: "existing@example.com",
                username: "existinguser");

            _mockUserRepository
                .Setup(r => r.GetUserByIdAsync(existingUserId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingUser);

            // CognitoService.UpdateUserAsync returns the updated user
            _mockCognitoService
                .Setup(c => c.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User u, CancellationToken ct) => u);

            // Test 1: Update with SAME email → should NOT trigger email validation
            var bodyWithSameEmail = CreateUserRequestBody(
                email: "existing@example.com",
                username: "existinguser",
                password: null);

            var requestSameEmail = CreateApiGatewayRequest(
                httpMethod: "PUT",
                body: bodyWithSameEmail,
                pathParameters: new Dictionary<string, string> { { "userId", existingUserId.ToString() } },
                jwtClaims: claims);

            // Act 1
            var responseSameEmail = await _handler.HandleSaveUser(requestSameEmail, _mockLambdaContext.Object);

            // Assert 1: Should succeed — no email uniqueness check needed
            responseSameEmail.StatusCode.Should().Be(200);
            _mockUserRepository.Verify(
                r => r.GetUserByEmailAsync("existing@example.com", It.IsAny<CancellationToken>()),
                Times.Never(),
                "Email uniqueness should NOT be checked when email hasn't changed");

            // Test 2: Update with DIFFERENT email that is already taken
            var duplicateUser = CreateTestUser(email: "taken@example.com");
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync("taken@example.com", It.IsAny<CancellationToken>()))
                .ReturnsAsync(duplicateUser);

            var bodyWithNewEmail = CreateUserRequestBody(
                email: "taken@example.com",
                username: "existinguser",
                password: null);

            var requestNewEmail = CreateApiGatewayRequest(
                httpMethod: "PUT",
                body: bodyWithNewEmail,
                pathParameters: new Dictionary<string, string> { { "userId", existingUserId.ToString() } },
                jwtClaims: claims);

            // Act 2
            var responseNewEmail = await _handler.HandleSaveUser(requestNewEmail, _mockLambdaContext.Object);

            // Assert 2: Should fail — different email triggers uniqueness validation
            responseNewEmail.StatusCode.Should().Be(400);
            responseNewEmail.Body.Should().Contain("Email is already registered to another user. It must be unique.");
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 5: HandleUpdateLastLogin Tests
        // Source: SecurityManager.UpdateUserLastLoginTime() — lines 350-356
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Happy path: PATCH /v1/users/{userId}/last-login with a valid userId.
        /// Source: storageRecordData.Add("last_logged_in", DateTime.UtcNow)
        ///         CurrentContext.RecordRepository.Update("user", storageRecordData)
        /// Verifies UpdateLastLoginTimeAsync is called and returns 200.
        /// </summary>
        [Fact]
        public async Task HandleUpdateLastLogin_ValidUserId_Returns200()
        {
            // Arrange
            var userId = Guid.NewGuid();

            _mockUserRepository
                .Setup(r => r.UpdateLastLoginTimeAsync(userId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var request = CreateApiGatewayRequest(
                httpMethod: "PATCH",
                pathParameters: new Dictionary<string, string> { { "userId", userId.ToString() } });

            // Act
            var response = await _handler.HandleUpdateLastLogin(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("Last login time updated.");

            var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();

            _mockUserRepository.Verify(
                r => r.UpdateLastLoginTimeAsync(userId, It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 6: Idempotency Key Tests
        // Per AAP Section 0.8.5: "Idempotency keys on all write endpoints"
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that the x-idempotency-key header is accepted and
        /// processed for create operations. The idempotency key is used to
        /// prevent duplicate operations on retried requests.
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_Create_SupportsIdempotencyKey()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);
            var idempotencyKey = Guid.NewGuid().ToString();

            _mockUserRepository
                .Setup(r => r.GetUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);
            _mockUserRepository
                .Setup(r => r.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            _mockCognitoService
                .Setup(c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User u, string p, CancellationToken ct) =>
                {
                    u.Id = u.Id == Guid.Empty ? Guid.NewGuid() : u.Id;
                    return u;
                });

            var body = CreateUserRequestBody(
                email: "idempotent@example.com",
                username: "idempotentuser",
                password: "SecurePass123!");

            var headers = new Dictionary<string, string>
            {
                { "x-idempotency-key", idempotencyKey }
            };

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                headers: headers,
                jwtClaims: claims);

            // Act — First invocation with idempotency key
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert: Request succeeds; idempotency key is accepted in headers
            response.StatusCode.Should().Be(200);

            var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();

            // Verify the user was created (CognitoService called)
            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once());
        }

        // ═══════════════════════════════════════════════════════════════════
        // Phase 7: Error Response Format Tests
        // Verifies error format matches source ValidationException pattern
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that multiple validation errors are accumulated and returned
        /// together in a single response, matching the source pattern of
        /// <c>valEx.AddError()</c> calls before <c>valEx.CheckAndThrow()</c>
        /// (SecurityManager.SaveUser lines 267-280).
        /// The response includes: success=false, message="Validation failed.", errors dictionary.
        /// </summary>
        [Fact]
        public async Task HandleSaveUser_ValidationErrors_MatchValidationExceptionPattern()
        {
            // Arrange
            var adminUser = SetupAdminCaller();
            var claims = CreateAdminJwtClaims(adminUser.Id);

            // Create request with MULTIPLE missing fields to verify error accumulation
            // Empty username → "Username is required."
            // Empty email → "Email is required."
            // Null password → "Password is required."
            var body = CreateUserRequestBody(
                email: "",
                username: "",
                password: null);

            var request = CreateApiGatewayRequest(
                httpMethod: "POST",
                body: body,
                jwtClaims: claims);

            // Act
            var response = await _handler.HandleSaveUser(request, _mockLambdaContext.Object);

            // Assert: StatusCode is 400 (validation failure)
            response.StatusCode.Should().Be(400);
            response.Body.Should().NotBeNullOrEmpty();

            // Parse response to verify ValidationException-style error structure
            var responseBody = JsonSerializer.Deserialize<JsonElement>(response.Body);

            // success should be false
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();

            // message should indicate validation failure
            responseBody.GetProperty("message").GetString().Should().Be("Validation failed.");

            // errors dictionary should contain field-level errors
            var errors = responseBody.GetProperty("errors");
            errors.GetProperty("username").GetString().Should().Be("Username is required.");
            errors.GetProperty("email").GetString().Should().Be("Email is required.");
            errors.GetProperty("password").GetString().Should().Be("Password is required.");

            // Verify no creation was attempted
            _mockCognitoService.Verify(
                c => c.CreateUserAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }
    }
}
