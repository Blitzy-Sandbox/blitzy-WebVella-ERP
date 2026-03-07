using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Net.Http.Headers;
using Moq;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using Xunit;

// Alias to disambiguate from Microsoft.AspNetCore.Authentication.AuthenticationMiddleware
using AuthenticationMiddleware = WebVella.Erp.Gateway.Middleware.AuthenticationMiddleware;

namespace WebVella.Erp.Tests.Gateway.Middleware
{
    /// <summary>
    /// Comprehensive unit tests for the AuthenticationMiddleware class that validates
    /// JWT + Cookie dual-mode authentication behavior in the API Gateway.
    ///
    /// Tests cover all behavior preserved from the monolith's ErpMiddleware.cs and
    /// JwtMiddleware.cs, including:
    /// - JWT token extraction from Authorization header and auth ticket
    /// - Bearer prefix stripping with edge cases (token.Length &lt;= 7)
    /// - JWT validation and user extraction from claims (no database access)
    /// - Cookie-based authentication fallback
    /// - Stale cookie sign-out via SignOutAsync
    /// - IHttpBodyControlFeature.AllowSynchronousIO management
    /// - SecurityContext scope lifecycle (open/dispose in finally block)
    /// - Gateway-specific no-database-access constraint validation
    ///
    /// All tests use Moq for mocking dependencies and FluentAssertions for readable assertions.
    /// Each test follows the Arrange/Act/Assert pattern with independent state (no shared mutable state).
    /// </summary>
    public class AuthenticationMiddlewareTests
    {
        private readonly Mock<JwtTokenHandler> _mockJwtTokenHandler;
        private readonly Mock<ILogger<AuthenticationMiddleware>> _mockLogger;
        private readonly Mock<IAuthenticationService> _mockAuthService;

        /// <summary>
        /// Initializes shared mock objects for all tests.
        /// xUnit creates a new instance per test method, ensuring isolation.
        /// </summary>
        public AuthenticationMiddlewareTests()
        {
            // JwtTokenHandler requires JwtTokenOptions in its constructor;
            // Moq passes constructor args to create the proxy instance.
            _mockJwtTokenHandler = new Mock<JwtTokenHandler>(new JwtTokenOptions(), (IDistributedCache)null) { CallBase = true };
            _mockLogger = new Mock<ILogger<AuthenticationMiddleware>>();
            _mockAuthService = new Mock<IAuthenticationService>();

            // Default: AuthenticateAsync returns NoResult (no access_token from auth ticket).
            // This ensures GetTokenAsync("access_token") returns null by default.
            _mockAuthService
                .Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()))
                .ReturnsAsync(AuthenticateResult.NoResult());

            // Default: SignOutAsync completes successfully to prevent NRE on await.
            _mockAuthService
                .Setup(x => x.SignOutAsync(
                    It.IsAny<HttpContext>(),
                    It.IsAny<string>(),
                    It.IsAny<AuthenticationProperties>()))
                .Returns(Task.CompletedTask);
        }

        /// <summary>
        /// Creates an AuthenticationMiddleware instance with mocked dependencies.
        /// </summary>
        /// <param name="next">The next middleware delegate in the pipeline.</param>
        /// <returns>A configured AuthenticationMiddleware instance.</returns>
        private AuthenticationMiddleware CreateMiddleware(RequestDelegate next)
        {
            return new AuthenticationMiddleware(next, _mockJwtTokenHandler.Object, _mockLogger.Object);
        }

        /// <summary>
        /// Creates a DefaultHttpContext with IAuthenticationService registered in DI,
        /// enabling GetTokenAsync and SignOutAsync extension methods to function correctly.
        /// </summary>
        /// <returns>A configured DefaultHttpContext with request services.</returns>
        private DefaultHttpContext CreateHttpContext()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAuthenticationService>(_mockAuthService.Object);
            var serviceProvider = services.BuildServiceProvider();

            var context = new DefaultHttpContext();
            context.RequestServices = serviceProvider;
            return context;
        }

        /// <summary>
        /// Creates a JwtSecurityToken with the specified claims for mock return values.
        /// Uses standard issuer/audience values matching the SharedKernel defaults.
        /// </summary>
        /// <param name="claims">Claims to embed in the token.</param>
        /// <returns>A JwtSecurityToken instance with the specified claims.</returns>
        private static JwtSecurityToken CreateJwtSecurityToken(params Claim[] claims)
        {
            return new JwtSecurityToken(
                issuer: "webvella-erp",
                audience: "webvella-erp",
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1));
        }

        /// <summary>
        /// No-op RequestDelegate that completes immediately.
        /// Used when the test does not need to verify next middleware behavior.
        /// </summary>
        private static RequestDelegate NoOpNext => _ => Task.CompletedTask;

        // ================================================================
        // Phase 2: JWT Token Extraction Tests
        // Validates: JwtMiddleware.cs lines 23-36
        // ================================================================

        /// <summary>
        /// Validates JWT token extraction from the Authorization: Bearer header
        /// and successful user extraction from JWT claims.
        /// Source: JwtMiddleware.cs line 26 (header read) + line 32 (Substring(7) strip).
        /// </summary>
        [Fact]
        public async Task Invoke_ValidBearerToken_ExtractsUserFromClaims()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "test@example.com")
            };
            var jwtToken = CreateJwtSecurityToken(claims);

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Returns(new ValueTask<JwtSecurityToken>(jwtToken));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer valid-jwt-token-here";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Items["User"].Should().NotBeNull();
            context.Items["User"].Should().BeOfType<ErpUser>();
            var user = (ErpUser)context.Items["User"];
            user.Id.Should().Be(userId);
        }

        /// <summary>
        /// Validates JWT token extraction from the authentication ticket's access_token property.
        /// Source: JwtMiddleware.cs line 23: var token = await context.GetTokenAsync("access_token").
        /// The middleware first tries GetTokenAsync before falling back to Authorization header.
        /// </summary>
        [Fact]
        public async Task Invoke_JwtFromGetTokenAsync_ExtractsUserFromToken()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var tokenClaims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "ticket@example.com")
            };
            var jwtToken = CreateJwtSecurityToken(tokenClaims);

            // Set up IAuthenticationService to return an access_token from the auth ticket
            var props = new AuthenticationProperties();
            props.StoreTokens(new[]
            {
                new AuthenticationToken { Name = "access_token", Value = "jwt-from-ticket" }
            });
            var ticket = new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity()),
                props,
                "TestScheme");

            _mockAuthService
                .Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), It.IsAny<string>()))
                .ReturnsAsync(AuthenticateResult.Success(ticket));

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync("jwt-from-ticket"))
                .Returns(new ValueTask<JwtSecurityToken>(jwtToken));

            var context = CreateHttpContext();
            // No Authorization header — token comes solely from GetTokenAsync
            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Items["User"].Should().NotBeNull();
            var user = (ErpUser)context.Items["User"];
            user.Id.Should().Be(userId);
            _mockJwtTokenHandler.Verify(
                x => x.GetValidSecurityTokenAsync("jwt-from-ticket"),
                Times.Once());
        }

        /// <summary>
        /// Validates that "Bearer" (exactly 7 chars, no value after prefix) is treated as no token.
        /// Source: JwtMiddleware.cs lines 29-30: if (token.Length &lt;= 7) token = null.
        /// </summary>
        [Fact]
        public async Task Invoke_BearerPrefixLengthLessOrEqual7_TreatedAsNoToken()
        {
            // Arrange
            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — no JWT validation should be attempted
            _mockJwtTokenHandler.Verify(
                x => x.GetValidSecurityTokenAsync(It.IsAny<string>()),
                Times.Never());
            context.Items.ContainsKey("User").Should().BeFalse();
        }

        /// <summary>
        /// Validates that "Bearer abc123" strips the 7-character "Bearer " prefix and passes
        /// "abc123" to GetValidSecurityTokenAsync for validation.
        /// Source: JwtMiddleware.cs line 32: token = token.Substring(7).
        /// </summary>
        [Fact]
        public async Task Invoke_BearerPrefixWithValue_StripsPrefix()
        {
            // Arrange
            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync("abc123"))
                .Returns(new ValueTask<JwtSecurityToken>((JwtSecurityToken)null));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer abc123";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — verify stripped token "abc123" was passed to validation
            _mockJwtTokenHandler.Verify(
                x => x.GetValidSecurityTokenAsync("abc123"),
                Times.Once());
        }

        /// <summary>
        /// Validates that empty or whitespace-only Authorization header values
        /// are treated as no token, and no JWT validation is attempted.
        /// Source: JwtMiddleware.cs lines 27-35 (string.IsNullOrWhiteSpace checks).
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Invoke_EmptyAuthorizationHeader_NoJwtValidation(string headerValue)
        {
            // Arrange
            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = headerValue;

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — no JWT validation attempted
            _mockJwtTokenHandler.Verify(
                x => x.GetValidSecurityTokenAsync(It.IsAny<string>()),
                Times.Never());
        }

        // ================================================================
        // Phase 3: JWT Validation and User Extraction Tests
        // Validates: JwtMiddleware.cs lines 38-62
        // ================================================================

        /// <summary>
        /// Validates that a valid JWT token results in context.Items["User"] being set
        /// to an ErpUser extracted from the JWT claims via SecurityContext.ExtractUserFromClaims.
        /// Source: JwtMiddleware.cs lines 42-52 (adapted for claims-only extraction without DB).
        /// </summary>
        [Fact]
        public async Task Invoke_ValidJwt_SetsContextItemsUser()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "user@example.com")
            };
            var jwtToken = CreateJwtSecurityToken(claims);

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Returns(new ValueTask<JwtSecurityToken>(jwtToken));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer test-token";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — context.Items["User"] should be populated with an ErpUser
            context.Items.Should().ContainKey("User");
            context.Items["User"].Should().BeOfType<ErpUser>();
            var user = (ErpUser)context.Items["User"];
            user.Id.Should().Be(userId);
        }

        /// <summary>
        /// Validates that JWT validation exceptions are silently swallowed
        /// (swallow-all catch block) and the next middleware is still invoked.
        /// Source: JwtMiddleware.cs lines 56-59: catch block that swallows all exceptions.
        /// </summary>
        [Fact]
        public async Task Invoke_InvalidJwt_SwallowsExceptionContinues()
        {
            // Arrange — mock throws exception during JWT validation
            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Throws(new Exception("JWT validation failed"));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer bad-token";

            bool nextCalled = false;
            RequestDelegate next = _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };
            var middleware = CreateMiddleware(next);

            // Act — should NOT throw despite JWT validation failure
            await middleware.Invoke(context);

            // Assert — next middleware was called, no user attached
            nextCalled.Should().BeTrue();
            context.Items.ContainsKey("User").Should().BeFalse();
        }

        /// <summary>
        /// Validates that when JWT validation returns null (invalid/expired token),
        /// no user is attached to the context.
        /// </summary>
        [Fact]
        public async Task Invoke_NullTokenFromValidation_NoUserAttached()
        {
            // Arrange — mock returns null (invalid token)
            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Returns(new ValueTask<JwtSecurityToken>((JwtSecurityToken)null));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer some-token";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — no user attached
            context.Items.ContainsKey("User").Should().BeFalse();
        }

        /// <summary>
        /// Validates that a JWT token with no meaningful claims (no NameIdentifier)
        /// results in no user being attached. The middleware checks jwtToken.Claims.Any()
        /// and SecurityContext.ExtractUserFromClaims returns null for missing NameIdentifier.
        /// </summary>
        [Fact]
        public async Task Invoke_JwtWithNoClaims_NoUserAttached()
        {
            // Arrange — create a minimal JWT token with no user-identifying claims.
            // Even if the token has default claims (iss, aud, exp), it lacks NameIdentifier,
            // so ExtractUserFromClaims returns null (user.Id == Guid.Empty check).
            var emptyToken = new JwtSecurityToken();

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Returns(new ValueTask<JwtSecurityToken>(emptyToken));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer some-token";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — no user should be attached regardless of default claims
            context.Items.ContainsKey("User").Should().BeFalse();
        }

        /// <summary>
        /// Validates that the validated JWT token string is stored in context.Items["JwtToken"]
        /// for downstream RequestRoutingMiddleware to propagate to backend microservices.
        /// This is NEW Gateway behavior not present in the monolith.
        /// </summary>
        [Fact]
        public async Task Invoke_ValidJwt_SetsContextItemsJwtToken()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
            var jwtToken = CreateJwtSecurityToken(claims);

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync("test-token"))
                .Returns(new ValueTask<JwtSecurityToken>(jwtToken));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer test-token";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — token string (without "Bearer " prefix) stored for downstream propagation
            context.Items.Should().ContainKey("JwtToken");
            context.Items["JwtToken"].Should().Be("test-token");
        }

        /// <summary>
        /// Validates that context.User is replaced with a ClaimsPrincipal containing
        /// a ClaimsIdentity with AuthenticationType "jwt" after successful JWT validation.
        /// Source: JwtMiddleware.cs lines 51-52: new ClaimsIdentity(jwtToken.Claims, "jwt").
        /// </summary>
        [Fact]
        public async Task Invoke_ValidJwt_ReplacesContextUserWithJwtIdentity()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "jwt@example.com")
            };
            var jwtToken = CreateJwtSecurityToken(claims);

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Returns(new ValueTask<JwtSecurityToken>(jwtToken));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer valid-token";

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — context.User should be replaced with JWT-based identity
            context.User.Should().NotBeNull();
            context.User.Identity.Should().NotBeNull();
            context.User.Identity.IsAuthenticated.Should().BeTrue();
            context.User.Identity.AuthenticationType.Should().Be("jwt");
        }

        // ================================================================
        // Phase 4: Cookie-Based Auth Fallback Tests
        // Validates: ErpMiddleware.cs lines 32-43
        // ================================================================

        /// <summary>
        /// Validates that when no JWT token is available, the middleware falls back
        /// to extracting the user from the cookie-based ClaimsPrincipal.
        /// Source: ErpMiddleware.cs line 32: ErpUser user = AuthService.GetUser(context.User)
        /// (adapted to use SecurityContext.ExtractUserFromClaims instead of DB lookup).
        /// </summary>
        [Fact]
        public async Task Invoke_NoJwt_CookieFallback_ExtractsUser()
        {
            // Arrange — no JWT token, but authenticated cookie user with valid claims
            var userId = Guid.NewGuid();
            var cookieClaims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, "cookie@example.com")
            };
            var cookieIdentity = new ClaimsIdentity(
                cookieClaims,
                CookieAuthenticationDefaults.AuthenticationScheme);

            var context = CreateHttpContext();
            context.User = new ClaimsPrincipal(cookieIdentity);
            // No Authorization header — triggers cookie fallback path

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — user extracted from cookie claims
            context.Items["User"].Should().NotBeNull();
            context.Items["User"].Should().BeOfType<ErpUser>();
            var user = (ErpUser)context.Items["User"];
            user.Id.Should().Be(userId);
        }

        /// <summary>
        /// Validates that when a cookie-authenticated user has stale/invalid claims
        /// (no valid NameIdentifier), SignOutAsync is called to clear the stale cookie.
        /// Source: ErpMiddleware.cs lines 39-42: stale cookie sign-out behavior.
        /// </summary>
        [Fact]
        public async Task Invoke_StaleCookie_CallsSignOutAsync()
        {
            // Arrange — authenticated user without a valid NameIdentifier claim.
            // SecurityContext.ExtractUserFromClaims returns null when Id == Guid.Empty.
            var staleClaims = new[]
            {
                new Claim(ClaimTypes.Email, "stale@example.com")
                // No NameIdentifier claim — ExtractUserFromClaims returns null
            };
            var staleIdentity = new ClaimsIdentity(
                staleClaims,
                CookieAuthenticationDefaults.AuthenticationScheme);

            var context = CreateHttpContext();
            context.User = new ClaimsPrincipal(staleIdentity);
            // No Authorization header — enters cookie fallback path

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — SignOutAsync called with cookie auth scheme
            _mockAuthService.Verify(
                x => x.SignOutAsync(
                    It.IsAny<HttpContext>(),
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    It.IsAny<AuthenticationProperties>()),
                Times.Once());
            context.Items.ContainsKey("User").Should().BeFalse();
        }

        // ================================================================
        // Phase 5: IHttpBodyControlFeature Tests
        // Validates: ErpMiddleware.cs lines 25-27
        // ================================================================

        /// <summary>
        /// Validates that the middleware sets IHttpBodyControlFeature.AllowSynchronousIO = true
        /// when the feature is available on the HttpContext.
        /// Source: ErpMiddleware.cs lines 25-27.
        /// </summary>
        [Fact]
        public async Task Invoke_HttpBodyControlFeature_SetsAllowSynchronousIO()
        {
            // Arrange — add a mock IHttpBodyControlFeature with tracking
            var mockFeature = new Mock<IHttpBodyControlFeature>();
            mockFeature.SetupProperty(f => f.AllowSynchronousIO, false);

            var context = CreateHttpContext();
            context.Features.Set<IHttpBodyControlFeature>(mockFeature.Object);

            var middleware = CreateMiddleware(NoOpNext);

            // Act
            await middleware.Invoke(context);

            // Assert — AllowSynchronousIO should be set to true
            mockFeature.Object.AllowSynchronousIO.Should().BeTrue();
        }

        /// <summary>
        /// Validates that the middleware completes without error when
        /// IHttpBodyControlFeature is not present in the HttpContext features.
        /// Source: ErpMiddleware.cs line 26: null check before setting.
        /// </summary>
        [Fact]
        public async Task Invoke_NoHttpBodyControlFeature_NoError()
        {
            // Arrange — no IHttpBodyControlFeature in features (default)
            var context = CreateHttpContext();

            var middleware = CreateMiddleware(NoOpNext);

            // Act — should complete without throwing
            var act = () => middleware.Invoke(context);

            // Assert — no exception thrown
            await act.Should().NotThrowAsync();
        }

        // ================================================================
        // Phase 6: Security Scope Lifecycle Tests
        // Validates: ErpMiddleware.cs lines 46-52 (dispose in finally block)
        // ================================================================

        /// <summary>
        /// Validates that the SecurityContext scope is properly disposed in the
        /// finally block, even when the next middleware delegate throws an exception.
        /// Source: ErpMiddleware.cs lines 46-52 (try/finally pattern for scope disposal).
        /// </summary>
        [Fact]
        public async Task Invoke_ValidAuth_DisposesSecurityScope()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
            var jwtToken = CreateJwtSecurityToken(claims);

            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Returns(new ValueTask<JwtSecurityToken>(jwtToken));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer test-token";

            // Capture the SecurityContext.CurrentUser during pipeline execution
            ErpUser capturedUserDuringPipeline = null;
            RequestDelegate next = _ =>
            {
                capturedUserDuringPipeline = SecurityContext.CurrentUser;
                throw new InvalidOperationException("Simulated pipeline error");
            };

            var middleware = CreateMiddleware(next);

            // Act — expect exception from next delegate to propagate
            Exception thrownException = null;
            try
            {
                await middleware.Invoke(context);
            }
            catch (InvalidOperationException ex)
            {
                thrownException = ex;
            }

            // Assert — user was available during pipeline execution
            thrownException.Should().NotBeNull("next delegate should have thrown");
            capturedUserDuringPipeline.Should().NotBeNull(
                "SecurityContext should have an active user scope during pipeline execution");
            capturedUserDuringPipeline.Id.Should().Be(userId);

            // After finally block executes, security scope should be disposed
            SecurityContext.CurrentUser.Should().BeNull(
                "SecurityContext scope must be disposed in the finally block");
        }

        /// <summary>
        /// Validates that no security scope disposal occurs when no user is authenticated
        /// (no scope was opened, so nothing needs to be disposed).
        /// </summary>
        [Fact]
        public async Task Invoke_NoAuth_NoScopeDisposed()
        {
            // Arrange — no JWT, no cookie auth (default unauthenticated context)
            var context = CreateHttpContext();

            bool nextCalled = false;
            RequestDelegate next = _ =>
            {
                nextCalled = true;
                // Verify no security scope is active during pipeline
                SecurityContext.CurrentUser.Should().BeNull(
                    "no security scope should be open when no user is authenticated");
                return Task.CompletedTask;
            };

            var middleware = CreateMiddleware(next);

            // Act
            await middleware.Invoke(context);

            // Assert — next was called, no security context state leaked
            nextCalled.Should().BeTrue();
            SecurityContext.CurrentUser.Should().BeNull();
        }

        // ================================================================
        // Phase 7: Gateway-Specific Constraint Tests
        // Validates: AAP Section 0.8.3 (no database access in Gateway)
        // ================================================================

        /// <summary>
        /// Validates that the AuthenticationMiddleware constructor does NOT accept any
        /// database-related parameters, enforcing the Gateway no-database-access constraint.
        /// Source: AAP Section 0.8.3 — Gateway middleware MUST NOT access the database.
        /// </summary>
        [Fact]
        public void Invoke_NoDatabaseAccess_ConstructorValidation()
        {
            // Arrange — inspect all constructor parameter types via reflection
            var constructors = typeof(AuthenticationMiddleware).GetConstructors();
            var dbRelatedTypeNames = new[]
            {
                "DbContext", "DbConnection", "SecurityManager",
                "DbRepository", "IDbContext", "DbRecordRepository",
                "DbEntityRepository", "DbRelationRepository"
            };

            // Assert — no constructor should accept database-related parameter types
            foreach (var ctor in constructors)
            {
                foreach (var param in ctor.GetParameters())
                {
                    var typeName = param.ParameterType.Name;
                    foreach (var dbType in dbRelatedTypeNames)
                    {
                        typeName.Should().NotContain(dbType,
                            because: $"constructor parameter '{param.Name}' of type '{typeName}' " +
                                     "would indicate database access, violating Gateway architecture constraint " +
                                     "(AAP Section 0.8.3)");
                    }
                }
            }

            // Additionally verify only expected parameter types are present
            var mainCtor = constructors.First();
            var paramTypes = mainCtor.GetParameters().Select(p => p.ParameterType).ToList();
            paramTypes.Should().HaveCount(3, "middleware should have exactly 3 constructor parameters");
            paramTypes.Should().Contain(typeof(RequestDelegate));
            paramTypes.Should().Contain(typeof(JwtTokenHandler));
            paramTypes.Should().Contain(typeof(ILogger<AuthenticationMiddleware>));
        }

        /// <summary>
        /// Validates that the next middleware delegate is always called, even when
        /// authentication fails completely (invalid JWT, no cookie, no user).
        /// Source: JwtMiddleware.cs line 64: await _next(context) (always called).
        /// </summary>
        [Fact]
        public async Task Invoke_AuthFailure_NextMiddlewareStillCalled()
        {
            // Arrange — invalid JWT that throws during validation, no cookie auth
            _mockJwtTokenHandler
                .Setup(x => x.GetValidSecurityTokenAsync(It.IsAny<string>()))
                .Throws(new Exception("Invalid JWT"));

            var context = CreateHttpContext();
            context.Request.Headers[HeaderNames.Authorization] = "Bearer invalid-token";

            bool nextCalled = false;
            RequestDelegate next = _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            };
            var middleware = CreateMiddleware(next);

            // Act
            await middleware.Invoke(context);

            // Assert — next middleware was invoked despite auth failure
            nextCalled.Should().BeTrue(
                "the next middleware delegate must always be called regardless of authentication outcome");
            context.Items.ContainsKey("User").Should().BeFalse(
                "no user should be attached after authentication failure");
        }
    }
}
