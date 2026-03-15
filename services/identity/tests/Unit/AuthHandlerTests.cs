using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.CognitoIdentityProvider.Model;
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
    /// Unit tests for <see cref="AuthHandler"/> Lambda handler class that replaces
    /// AuthService.cs (cookie/JWT auth) and WebApiController.cs (JWT token endpoints).
    /// All AWS dependencies (Cognito, SNS) are mocked via Moq — zero real AWS SDK calls.
    ///
    /// <para>
    /// Tests cover HandleLogin (POST /v1/auth/login), HandleLogout (POST /v1/auth/logout),
    /// and HandleRefreshToken (POST /v1/auth/token/refresh) including input validation,
    /// token extraction from Authorization header (Bearer prefix stripping per JwtMiddleware
    /// line 32: token.Substring(7)), correlation-ID propagation, and ResponseModel pattern.
    /// </para>
    ///
    /// <para>Coverage target: &gt;80% per AAP Section 0.8.4.</para>
    /// </summary>
    public class AuthHandlerTests
    {
        #region Fields and Constructor

        private readonly Mock<ICognitoService> _mockCognitoService;
        private readonly Mock<ILogger<AuthHandler>> _mockLogger;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly Mock<IUserRepository> _mockUserRepository;
        private readonly Mock<IPermissionService> _mockPermissionService;
        private readonly AuthHandler _handler;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Constructor — initializes all mocks and builds <see cref="AuthHandler"/> via the
        /// test-friendly <c>IServiceProvider</c> constructor (AuthHandler.cs line 180).
        /// Mocked dependencies:
        /// <list type="bullet">
        ///   <item><see cref="ICognitoService"/> — Cognito auth operations (login, logout, refresh)</item>
        ///   <item><see cref="IAmazonSimpleNotificationService"/> — SNS domain event publishing</item>
        ///   <item><see cref="ILogger{AuthHandler}"/> — structured logger</item>
        ///   <item><see cref="IUserRepository"/> — DynamoDB user data access (transitive dependency)</item>
        ///   <item><see cref="IPermissionService"/> — authorization checks (transitive dependency)</item>
        /// </list>
        /// </summary>
        public AuthHandlerTests()
        {
            _mockCognitoService = new Mock<ICognitoService>();
            _mockLogger = new Mock<ILogger<AuthHandler>>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLambdaContext = new Mock<ILambdaContext>();
            _mockUserRepository = new Mock<IUserRepository>();
            _mockPermissionService = new Mock<IPermissionService>();

            // Lambda context returns a request ID for structured logging correlation
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());

            // Default SNS mock — return empty response to prevent null-ref in non-fatal publish path
            _mockSnsClient
                .Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Build ServiceCollection with mocked dependencies matching AuthHandler(IServiceProvider)
            // constructor which calls GetRequiredService<ICognitoService>(),
            // GetRequiredService<IAmazonSimpleNotificationService>(),
            // and GetRequiredService<ILogger<AuthHandler>>().
            var services = new ServiceCollection();
            services.AddSingleton<ICognitoService>(_mockCognitoService.Object);
            services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);
            services.AddSingleton<ILogger<AuthHandler>>(_mockLogger.Object);
            services.AddSingleton<IUserRepository>(_mockUserRepository.Object);
            services.AddSingleton<IPermissionService>(_mockPermissionService.Object);

            var serviceProvider = services.BuildServiceProvider();
            _handler = new AuthHandler(serviceProvider);

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates an <see cref="APIGatewayHttpApiV2ProxyRequest"/> with configurable body and headers.
        /// Follows the exact structure used by HTTP API Gateway v2 integration.
        /// Headers use lowercase keys (HTTP API v2 normalizes header names to lowercase).
        /// </summary>
        /// <param name="body">Optional JSON body string for POST requests.</param>
        /// <param name="headers">Optional headers dictionary. If null, default x-correlation-id is included.</param>
        /// <returns>Fully constructed API Gateway v2 proxy request for handler invocation.</returns>
        private static APIGatewayHttpApiV2ProxyRequest CreateApiGatewayRequest(
            string? body = null,
            Dictionary<string, string>? headers = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                Headers = headers ?? new Dictionary<string, string>
                {
                    { "x-correlation-id", Guid.NewGuid().ToString() }
                },
                PathParameters = new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString()
                }
            };
        }

        /// <summary>
        /// Deserializes a JSON response body string into the specified type using
        /// case-insensitive property name matching (matching AuthHandler.JsonOptions behavior).
        /// </summary>
        /// <typeparam name="T">Target deserialization type.</typeparam>
        /// <param name="body">JSON string from API Gateway response body.</param>
        /// <returns>Deserialized object of type T.</returns>
        private T? DeserializeResponseBody<T>(string body)
        {
            return JsonSerializer.Deserialize<T>(body, _jsonOptions);
        }

        /// <summary>
        /// Creates a test <see cref="User"/> instance with all properties populated.
        /// Used for mock <c>AuthenticateAsync</c> return values in login tests.
        /// </summary>
        /// <param name="userId">Optional user ID; defaults to a new GUID.</param>
        /// <returns>Fully populated User test instance.</returns>
        private static User CreateTestUser(Guid? userId = null)
        {
            var id = userId ?? Guid.NewGuid();
            return new User
            {
                Id = id,
                Email = "test@test.com",
                Username = "testuser",
                FirstName = "Test",
                LastName = "User",
                Enabled = true,
                Roles = new List<Role>
                {
                    new Role { Id = Role.RegularRoleId, Name = "regular" }
                }
            };
        }

        /// <summary>
        /// Creates a test <see cref="AuthTokenResult"/> with all token fields populated.
        /// Used for mock <c>GetTokenAsync</c> and <c>RefreshTokenAsync</c> return values.
        /// </summary>
        /// <returns>Fully populated AuthTokenResult test instance.</returns>
        private static AuthTokenResult CreateTestTokenResult()
        {
            return new AuthTokenResult
            {
                IdToken = "test-id-token-" + Guid.NewGuid().ToString("N"),
                AccessToken = "test-access-token-" + Guid.NewGuid().ToString("N"),
                RefreshToken = "test-refresh-token-" + Guid.NewGuid().ToString("N"),
                ExpiresIn = 3600,
                TokenType = "Bearer"
            };
        }

        #endregion

        #region HandleLogin Tests

        /// <summary>
        /// Happy path: valid credentials return HTTP 200 with JWT tokens and user profile.
        /// Validates the complete login flow:
        ///   1. CognitoService.AuthenticateAsync returns a valid User (replacing SecurityManager.GetUser)
        ///   2. CognitoService.GetTokenAsync returns AuthTokenResult (replacing AuthService.BuildTokenAsync)
        ///   3. Response contains success=true, token object (idToken, accessToken, refreshToken), user data
        ///   4. CognitoService methods called exactly once each
        /// </summary>
        [Fact]
        public async Task HandleLogin_ValidCredentials_Returns200WithTokens()
        {
            // Arrange
            var testUser = CreateTestUser();
            var testTokens = CreateTestTokenResult();

            _mockCognitoService
                .Setup(x => x.AuthenticateAsync("test@test.com", "password", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testUser);

            _mockCognitoService
                .Setup(x => x.GetTokenAsync("test@test.com", "password", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testTokens);

            var loginJson = JsonSerializer.Serialize(new { email = "test@test.com", password = "password" });
            var request = CreateApiGatewayRequest(body: loginJson);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — HTTP 200 status code
            response.StatusCode.Should().Be(200);

            // Assert — response body contains success + token + user data
            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.Should().NotBeNull();
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();
            responseBody.GetProperty("message").GetString().Should().Contain("Login successful");
            responseBody.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();

            // Assert — token object present with expected fields
            var tokenObj = responseBody.GetProperty("object").GetProperty("token");
            tokenObj.GetProperty("idToken").GetString().Should().NotBeNullOrEmpty();
            tokenObj.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
            tokenObj.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
            tokenObj.GetProperty("expiresIn").GetInt32().Should().Be(3600);
            tokenObj.GetProperty("tokenType").GetString().Should().Be("Bearer");

            // Assert — user object present with expected fields
            var userObj = responseBody.GetProperty("object").GetProperty("user");
            userObj.GetProperty("id").GetString().Should().Be(testUser.Id.ToString());
            userObj.GetProperty("email").GetString().Should().Be("test@test.com");
            userObj.GetProperty("username").GetString().Should().Be("testuser");
            userObj.GetProperty("firstName").GetString().Should().Be("Test");
            userObj.GetProperty("lastName").GetString().Should().Be("User");

            // Verify — CognitoService.AuthenticateAsync called exactly once
            _mockCognitoService.Verify(
                x => x.AuthenticateAsync("test@test.com", "password", It.IsAny<CancellationToken>()),
                Times.Once());

            // Verify — CognitoService.GetTokenAsync called exactly once
            _mockCognitoService.Verify(
                x => x.GetTokenAsync("test@test.com", "password", It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Invalid credentials: CognitoService.AuthenticateAsync returns null → HTTP 401.
        /// Replaces source AuthService.GetTokenAsync line 91: throw new Exception("Invalid email or password").
        /// Validates the handler returns proper error response without leaking credential details.
        /// </summary>
        [Fact]
        public async Task HandleLogin_InvalidCredentials_Returns401()
        {
            // Arrange
            _mockCognitoService
                .Setup(x => x.AuthenticateAsync("wrong@test.com", "wrong", It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var loginJson = JsonSerializer.Serialize(new { email = "wrong@test.com", password = "wrong" });
            var request = CreateApiGatewayRequest(body: loginJson);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — HTTP 401 Unauthorized
            response.StatusCode.Should().Be(401);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Invalid email or password");

            // Verify — AuthenticateAsync was called but GetTokenAsync was NOT called
            _mockCognitoService.Verify(
                x => x.AuthenticateAsync("wrong@test.com", "wrong", It.IsAny<CancellationToken>()),
                Times.Once());
            _mockCognitoService.Verify(
                x => x.GetTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Missing email: POST body contains password but no email → HTTP 400.
        /// Input validation at handler level — checks email is not empty after trim.
        /// </summary>
        [Fact]
        public async Task HandleLogin_MissingEmail_Returns400()
        {
            // Arrange — body with password only, no email field
            var loginJson = JsonSerializer.Serialize(new { password = "password" });
            var request = CreateApiGatewayRequest(body: loginJson);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — HTTP 400 Bad Request with email validation error
            response.StatusCode.Should().Be(400);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Email is required");

            // Verify — CognitoService was never called (validation short-circuit)
            _mockCognitoService.Verify(
                x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Missing password: POST body contains email but no password → HTTP 400.
        /// Input validation at handler level — checks password is not empty after trim.
        /// </summary>
        [Fact]
        public async Task HandleLogin_MissingPassword_Returns400()
        {
            // Arrange — body with email only, no password field
            var loginJson = JsonSerializer.Serialize(new { email = "test@test.com" });
            var request = CreateApiGatewayRequest(body: loginJson);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — HTTP 400 Bad Request with password validation error
            response.StatusCode.Should().Be(400);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Password is required");

            // Verify — CognitoService was never called (validation short-circuit)
            _mockCognitoService.Verify(
                x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Empty body: POST with null/whitespace body → HTTP 400.
        /// First validation gate in HandleLogin — checks request.Body is not null/empty.
        /// </summary>
        [Fact]
        public async Task HandleLogin_EmptyBody_Returns400()
        {
            // Arrange — request with empty body
            var request = CreateApiGatewayRequest(body: null);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — HTTP 400 Bad Request
            response.StatusCode.Should().Be(400);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Request body is required");

            // Verify — CognitoService was never called
            _mockCognitoService.Verify(
                x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Cognito exception: AuthenticateAsync throws a generic exception → HTTP 500.
        /// Tests the catch-all exception handler that logs the error and returns a generic message
        /// without leaking internal exception details.
        /// </summary>
        [Fact]
        public async Task HandleLogin_CognitoException_Returns500()
        {
            // Arrange — mock throws unhandled exception (e.g., Cognito service unavailable)
            _mockCognitoService
                .Setup(x => x.AuthenticateAsync("test@test.com", "password", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Cognito service unavailable"));

            var loginJson = JsonSerializer.Serialize(new { email = "test@test.com", password = "password" });
            var request = CreateApiGatewayRequest(body: loginJson);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — HTTP 500 Internal Server Error
            response.StatusCode.Should().Be(500);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("internal error");

            // Verify — AuthenticateAsync was called (before it threw)
            _mockCognitoService.Verify(
                x => x.AuthenticateAsync("test@test.com", "password", It.IsAny<CancellationToken>()),
                Times.Once());
        }

        #endregion

        #region HandleLogout Tests

        /// <summary>
        /// Happy path: valid access token in Authorization header → HTTP 200.
        /// Validates the complete logout flow:
        ///   1. Bearer token extracted from Authorization header
        ///   2. CognitoService.LogoutAsync called with extracted token (no "Bearer " prefix)
        ///   3. Response contains success=true
        /// Replaces AuthService.Logout() lines 57-61 (cookie-based sign-out).
        /// </summary>
        [Fact]
        public async Task HandleLogout_ValidAccessToken_Returns200()
        {
            // Arrange
            _mockCognitoService
                .Setup(x => x.LogoutAsync("valid-access-token", It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var headers = new Dictionary<string, string>
            {
                { "authorization", "Bearer valid-access-token" },
                { "x-correlation-id", Guid.NewGuid().ToString() }
            };
            var request = CreateApiGatewayRequest(headers: headers);

            // Act
            var response = await _handler.HandleLogout(request, _mockLambdaContext.Object);

            // Assert — HTTP 200 OK
            response.StatusCode.Should().Be(200);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();
            responseBody.GetProperty("message").GetString().Should().Contain("Logout successful");

            // Verify — LogoutAsync called once with the raw token (Bearer prefix stripped)
            _mockCognitoService.Verify(
                x => x.LogoutAsync("valid-access-token", It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// CRITICAL: Verifies Bearer prefix stripping matches JwtMiddleware.cs line 32: token.Substring(7).
        /// The Authorization header value "Bearer my-token-here" must result in the string
        /// "my-token-here" being passed to LogoutAsync (7 characters "Bearer " stripped).
        /// Uses Moq Callback to capture the exact token string passed to the mock.
        /// </summary>
        [Fact]
        public async Task HandleLogout_TokenExtractionFromAuthHeader_StripsBearerPrefix()
        {
            // Arrange
            string? capturedToken = null;
            _mockCognitoService
                .Setup(x => x.LogoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((token, _) => capturedToken = token)
                .Returns(Task.CompletedTask);

            var headers = new Dictionary<string, string>
            {
                { "authorization", "Bearer my-token-here" },
                { "x-correlation-id", Guid.NewGuid().ToString() }
            };
            var request = CreateApiGatewayRequest(headers: headers);

            // Act
            var response = await _handler.HandleLogout(request, _mockLambdaContext.Object);

            // Assert — Token passed to LogoutAsync is "my-token-here" (not "Bearer my-token-here")
            response.StatusCode.Should().Be(200);
            capturedToken.Should().NotBeNull();
            capturedToken.Should().Be("my-token-here");
            capturedToken.Should().NotContain("Bearer");

            // Verify — LogoutAsync called with the stripped token
            _mockCognitoService.Verify(
                x => x.LogoutAsync(
                    It.Is<string>(t => t == "my-token-here"),
                    It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Missing Authorization header: POST without Authorization header → HTTP 401.
        /// The ExtractBearerToken method returns null when no "authorization" header is present,
        /// causing HandleLogout to return 401 with "Authorization token is required."
        /// </summary>
        [Fact]
        public async Task HandleLogout_MissingAuthorizationHeader_Returns401()
        {
            // Arrange — request with no authorization header
            var headers = new Dictionary<string, string>
            {
                { "x-correlation-id", Guid.NewGuid().ToString() }
            };
            var request = CreateApiGatewayRequest(headers: headers);

            // Act
            var response = await _handler.HandleLogout(request, _mockLambdaContext.Object);

            // Assert — HTTP 401 Unauthorized
            response.StatusCode.Should().Be(401);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Authorization token is required");

            // Verify — LogoutAsync was never called
            _mockCognitoService.Verify(
                x => x.LogoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        /// <summary>
        /// Short token: Authorization header "Bearer " (exactly 7 chars or less after trim) → HTTP 401.
        /// Matches JwtMiddleware.cs lines 29-30: if (token.Length &lt;= 7) token = null.
        /// ExtractBearerToken returns null for tokens that are just "Bearer " with nothing after.
        /// </summary>
        [Fact]
        public async Task HandleLogout_ShortToken_Returns401()
        {
            // Arrange — "Bearer " is exactly 7 chars, so token.Length <= 7 returns null
            var headers = new Dictionary<string, string>
            {
                { "authorization", "Bearer " },
                { "x-correlation-id", Guid.NewGuid().ToString() }
            };
            var request = CreateApiGatewayRequest(headers: headers);

            // Act
            var response = await _handler.HandleLogout(request, _mockLambdaContext.Object);

            // Assert — HTTP 401 Unauthorized (token too short)
            response.StatusCode.Should().Be(401);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Authorization token is required");

            // Verify — LogoutAsync was never called
            _mockCognitoService.Verify(
                x => x.LogoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        #endregion

        #region HandleRefreshToken Tests

        /// <summary>
        /// Happy path: valid refresh token returns HTTP 200 with new tokens.
        /// Replaces AuthService.GetNewTokenAsync() lines 94-117 which validated JWT,
        /// extracted claims, loaded user by GUID, checked enabled status, and built new JWT.
        /// Cognito REFRESH_TOKEN_AUTH flow handles all of this natively.
        /// </summary>
        [Fact]
        public async Task HandleRefreshToken_ValidRefreshToken_Returns200WithNewTokens()
        {
            // Arrange
            var newTokens = CreateTestTokenResult();
            _mockCognitoService
                .Setup(x => x.RefreshTokenAsync("valid-refresh-token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(newTokens);

            var refreshJson = JsonSerializer.Serialize(new { refreshToken = "valid-refresh-token" });
            var request = CreateApiGatewayRequest(body: refreshJson);

            // Act
            var response = await _handler.HandleRefreshToken(request, _mockLambdaContext.Object);

            // Assert — HTTP 200 OK
            response.StatusCode.Should().Be(200);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeTrue();
            responseBody.GetProperty("message").GetString().Should().Contain("Token refreshed successfully");
            responseBody.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();

            // Assert — new tokens present in response object
            var tokenObj = responseBody.GetProperty("object");
            tokenObj.GetProperty("idToken").GetString().Should().NotBeNullOrEmpty();
            tokenObj.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
            tokenObj.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
            tokenObj.GetProperty("expiresIn").GetInt32().Should().Be(3600);
            tokenObj.GetProperty("tokenType").GetString().Should().Be("Bearer");

            // Verify — RefreshTokenAsync called exactly once
            _mockCognitoService.Verify(
                x => x.RefreshTokenAsync("valid-refresh-token", It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Invalid refresh token: CognitoService.RefreshTokenAsync returns null → HTTP 401.
        /// Tests the handler properly returns unauthorized when Cognito rejects the refresh token.
        /// </summary>
        [Fact]
        public async Task HandleRefreshToken_InvalidRefreshToken_Returns401()
        {
            // Arrange
            _mockCognitoService
                .Setup(x => x.RefreshTokenAsync("expired-refresh-token", It.IsAny<CancellationToken>()))
                .ReturnsAsync((AuthTokenResult?)null);

            var refreshJson = JsonSerializer.Serialize(new { refreshToken = "expired-refresh-token" });
            var request = CreateApiGatewayRequest(body: refreshJson);

            // Act
            var response = await _handler.HandleRefreshToken(request, _mockLambdaContext.Object);

            // Assert — HTTP 401 Unauthorized
            response.StatusCode.Should().Be(401);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Invalid or expired refresh token");

            // Verify — RefreshTokenAsync called once
            _mockCognitoService.Verify(
                x => x.RefreshTokenAsync("expired-refresh-token", It.IsAny<CancellationToken>()),
                Times.Once());
        }

        /// <summary>
        /// Missing refresh token: POST with empty body or missing refreshToken → HTTP 400.
        /// First validation gate — checks request.Body presence and refreshToken field non-empty.
        /// </summary>
        [Fact]
        public async Task HandleRefreshToken_MissingRefreshToken_Returns400()
        {
            // Arrange — empty body (no refresh token)
            var request = CreateApiGatewayRequest(body: null);

            // Act
            var response = await _handler.HandleRefreshToken(request, _mockLambdaContext.Object);

            // Assert — HTTP 400 Bad Request
            response.StatusCode.Should().Be(400);

            var responseBody = DeserializeResponseBody<JsonElement>(response.Body);
            responseBody.GetProperty("success").GetBoolean().Should().BeFalse();
            responseBody.GetProperty("message").GetString().Should().Contain("Request body is required");

            // Verify — RefreshTokenAsync was never called
            _mockCognitoService.Verify(
                x => x.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never());
        }

        #endregion

        #region Correlation-ID Tests

        /// <summary>
        /// Correlation-ID extraction: verifies x-correlation-id header is extracted from request.
        /// Per AAP Section 0.8.5: "Structured JSON logging with correlation-ID propagation."
        /// The handler calls GetCorrelationId(request) which checks x-correlation-id header first.
        /// This test verifies the correlation-ID flows through the handler processing pipeline
        /// and that the logger is invoked with the correct correlation-ID value.
        /// </summary>
        [Fact]
        public async Task HandleLogin_ExtractsCorrelationIdFromHeaders()
        {
            // Arrange — set specific correlation-ID in request headers
            var expectedCorrelationId = "test-corr-id-" + Guid.NewGuid().ToString("N");

            _mockCognitoService
                .Setup(x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((User?)null);

            var loginJson = JsonSerializer.Serialize(new { email = "test@test.com", password = "wrong" });
            var headers = new Dictionary<string, string>
            {
                { "x-correlation-id", expectedCorrelationId }
            };
            var request = CreateApiGatewayRequest(body: loginJson, headers: headers);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — handler processes request (we verify indirectly via logging)
            response.Should().NotBeNull();
            response.StatusCode.Should().BeGreaterOrEqualTo(200);

            // Verify — logger was invoked with the correlation-ID.
            // The handler calls _logger.LogInformation with CorrelationId={CorrelationId}.
            // We verify the logger was called at least once (proving the handler ran through
            // the correlation-ID extraction path).
            _mockLogger.Verify(
                x => x.Log(
                    It.IsAny<Microsoft.Extensions.Logging.LogLevel>(),
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains(expectedCorrelationId)),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce());
        }

        #endregion

        #region Response Format Tests

        /// <summary>
        /// Response format validation: verifies the response body matches the ResponseModel pattern
        /// from WebApiController.cs. Every response (success or error) must contain:
        /// - <c>success</c> (bool): indicates operation outcome
        /// - <c>timestamp</c> (DateTime as string): when the response was generated
        /// - <c>message</c> (string or null): human-readable message
        /// - <c>object</c> (any or null): data payload for successful operations
        /// - <c>errors</c> (array or null): error details for validation/processing failures
        /// This ensures backward compatibility with the monolith's response format.
        /// </summary>
        [Fact]
        public async Task HandleLogin_ResponseMatchesResponseModelPattern()
        {
            // Arrange — use a valid login to get a success response with all fields populated
            var testUser = CreateTestUser();
            var testTokens = CreateTestTokenResult();

            _mockCognitoService
                .Setup(x => x.AuthenticateAsync("test@test.com", "password", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testUser);
            _mockCognitoService
                .Setup(x => x.GetTokenAsync("test@test.com", "password", It.IsAny<CancellationToken>()))
                .ReturnsAsync(testTokens);

            var loginJson = JsonSerializer.Serialize(new { email = "test@test.com", password = "password" });
            var request = CreateApiGatewayRequest(body: loginJson);

            // Act
            var response = await _handler.HandleLogin(request, _mockLambdaContext.Object);

            // Assert — raw JSON has all ResponseModel fields
            var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;

            // success (bool) — MUST be present
            root.TryGetProperty("success", out var successProp).Should().BeTrue("response must have 'success' field");
            successProp.ValueKind.Should().Be(JsonValueKind.True);

            // timestamp (string, DateTime format) — MUST be present
            root.TryGetProperty("timestamp", out var timestampProp).Should().BeTrue("response must have 'timestamp' field");
            timestampProp.ValueKind.Should().Be(JsonValueKind.String);
            DateTime.TryParse(timestampProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp).Should().BeTrue("timestamp must be a valid DateTime");
            parsedTimestamp.Kind.Should().Be(DateTimeKind.Utc, "response timestamp must be UTC");
            parsedTimestamp.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5), "timestamp should be recent");

            // message (string) — MUST be present
            root.TryGetProperty("message", out var messageProp).Should().BeTrue("response must have 'message' field");
            messageProp.ValueKind.Should().Be(JsonValueKind.String);
            messageProp.GetString().Should().NotBeNullOrEmpty();

            // object (any) — present for success responses (contains token + user data)
            root.TryGetProperty("object", out var objectProp).Should().BeTrue("success response must have 'object' field");
            objectProp.ValueKind.Should().Be(JsonValueKind.Object);

            // Also verify an error response has the same pattern (errors array present)
            var errorRequest = CreateApiGatewayRequest(body: null);
            var errorResponse = await _handler.HandleLogin(errorRequest, _mockLambdaContext.Object);
            var errorDoc = JsonDocument.Parse(errorResponse.Body);
            var errorRoot = errorDoc.RootElement;

            // Error response must have success = false
            errorRoot.TryGetProperty("success", out var errSuccess).Should().BeTrue();
            errSuccess.GetBoolean().Should().BeFalse();

            // Error response must have timestamp
            errorRoot.TryGetProperty("timestamp", out var errTimestamp).Should().BeTrue();
            errTimestamp.ValueKind.Should().Be(JsonValueKind.String);

            // Error response must have message
            errorRoot.TryGetProperty("message", out var errMessage).Should().BeTrue();
            errMessage.GetString().Should().NotBeNullOrEmpty();
        }

        #endregion
    }
}
