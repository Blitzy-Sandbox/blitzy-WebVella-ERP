using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Security;
using Xunit;

namespace WebVella.Erp.Tests.SharedKernel.Security
{
    /// <summary>
    /// Comprehensive unit tests for JwtTokenHandler validating JWT token creation,
    /// validation, refresh, claim extraction, and configuration defaults.
    /// Tests verify backward compatibility with the monolith's AuthService JWT behavior
    /// (WebVella.Erp.Web/Services/AuthService.cs, JWT region lines 81-165).
    /// </summary>
    public class JwtTokenHandlerTests
    {
        // Test constants matching monolith defaults from ErpSettings and AuthService
        private const string TestKey = "ThisIsMySecretKeyForTestingPurposes123!"; // 38 chars >= 32 min for HMAC-SHA256
        private const string TestIssuer = "webvella-erp";
        private const string TestAudience = "webvella-erp";
        private const double TestExpiryMinutes = 1440;   // 24 hours — AuthService.JWT_TOKEN_EXPIRY_DURATION_MINUTES
        private const double TestRefreshMinutes = 120;    // 2 hours — AuthService.JWT_TOKEN_FORCE_REFRESH_MINUTES

        private readonly JwtTokenHandler _handler;

        /// <summary>
        /// Initializes a shared handler instance with default test options for
        /// tests that use the standard configuration.
        /// </summary>
        public JwtTokenHandlerTests()
        {
            _handler = new JwtTokenHandler(CreateDefaultOptions());
        }

        #region Helper Methods

        /// <summary>
        /// Creates default JWT options matching the monolith's ErpSettings defaults
        /// with a test-appropriate key length (>= 32 bytes for HMAC-SHA256).
        /// Key is longer than the monolith default ("ThisIsMySecretKey") to avoid
        /// relying on the key padding logic in standard tests.
        /// </summary>
        private static JwtTokenOptions CreateDefaultOptions()
        {
            return new JwtTokenOptions
            {
                Key = TestKey,
                Issuer = TestIssuer,
                Audience = TestAudience,
                TokenExpiryMinutes = TestExpiryMinutes,
                TokenRefreshMinutes = TestRefreshMinutes
            };
        }

        /// <summary>
        /// Creates a test ErpUser with configurable properties and roles.
        /// Each role gets a unique Guid and the specified name.
        /// ErpUser.Roles has a private setter but is initialized as an empty list,
        /// so roles are added via Roles.Add().
        /// </summary>
        private static ErpUser CreateTestUser(
            Guid? id = null,
            string username = "testuser",
            string email = "test@test.com",
            params string[] roleNames)
        {
            var user = new ErpUser
            {
                Id = id ?? Guid.NewGuid(),
                Username = username,
                Email = email,
                FirstName = "Test",
                LastName = "User"
            };
            foreach (var roleName in roleNames)
            {
                user.Roles.Add(new ErpRole { Id = Guid.NewGuid(), Name = roleName });
            }
            return user;
        }

        /// <summary>
        /// Finds a claim in a JwtSecurityToken by checking both the primary type
        /// and an optional alternate type. This accommodates JWT claim type mapping
        /// differences between System.IdentityModel.Tokens.Jwt versions where standard
        /// claims may use either short JWT names ("sub", "email", "role") or full
        /// CLR URI names (ClaimTypes.NameIdentifier, ClaimTypes.Email, ClaimTypes.Role).
        /// </summary>
        private static Claim FindClaim(JwtSecurityToken token, string primaryType, string alternateType = null)
        {
            var claim = token.Claims.FirstOrDefault(c => c.Type == primaryType);
            if (claim == null && alternateType != null)
                claim = token.Claims.FirstOrDefault(c => c.Type == alternateType);
            return claim;
        }

        /// <summary>
        /// Finds all claims in a JwtSecurityToken matching either the primary type
        /// or an optional alternate type.
        /// </summary>
        private static List<Claim> FindAllClaims(JwtSecurityToken token, string primaryType, string alternateType = null)
        {
            var claims = token.Claims.Where(c => c.Type == primaryType).ToList();
            if (claims.Count == 0 && alternateType != null)
                claims = token.Claims.Where(c => c.Type == alternateType).ToList();
            return claims;
        }

        #endregion

        #region Phase 2: Token Creation Tests (BuildTokenAsync)

        /// <summary>
        /// Verifies BuildTokenAsync returns a non-null, non-empty JWT token string
        /// for a valid user input.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_WithValidUser_ReturnsNonEmptyTokenString()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Assert
            tokenString.Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Verifies BuildTokenAsync returns a non-null JwtSecurityToken object.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_WithValidUser_ReturnsJwtSecurityToken()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert
            token.Should().NotBeNull();
            token.Should().BeOfType<JwtSecurityToken>();
        }

        /// <summary>
        /// Verifies the token contains a NameIdentifier claim matching user.Id.
        /// Source: AuthService.cs line 148: new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        /// In JWT, this is stored as the "sub" claim.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenContainsNameIdentifierClaim()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var user = CreateTestUser(id: userId);

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — When using the constructor directly, Subject may be null
            // because ClaimTypes.NameIdentifier has the long URI form. Use FindClaim
            // to check both short ("sub") and long (ClaimTypes.NameIdentifier) forms.
            var subClaim = FindClaim(token, "sub", ClaimTypes.NameIdentifier);
            subClaim.Should().NotBeNull();
            subClaim!.Value.Should().Be(userId.ToString());
        }

        /// <summary>
        /// Verifies the token contains an Email claim matching user.Email.
        /// Source: AuthService.cs line 149: new Claim(ClaimTypes.Email, user.Email)
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenContainsEmailClaim()
        {
            // Arrange
            var user = CreateTestUser(email: "jwt@test.com");

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — Email may be stored as "email" (JWT) or ClaimTypes.Email (CLR URI)
            var emailClaim = FindClaim(token, "email", ClaimTypes.Email);
            emailClaim.Should().NotBeNull();
            emailClaim!.Value.Should().Be("jwt@test.com");
        }

        /// <summary>
        /// Verifies the token contains Username (ClaimTypes.Name), FirstName (ClaimTypes.GivenName),
        /// LastName (ClaimTypes.Surname), and Image ("image") claims for complete cross-service
        /// identity propagation per AAP 0.8.3.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenContainsAllIdentityClaims()
        {
            // Arrange
            var user = CreateTestUser(username: "jwtuser", email: "jwt@test.com");
            user.FirstName = "John";
            user.LastName = "Doe";
            user.Image = "/avatar/john.png";

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — Username claim
            var nameClaim = FindClaim(token, ClaimTypes.Name, "unique_name");
            nameClaim.Should().NotBeNull("Username claim (ClaimTypes.Name) is required");
            nameClaim!.Value.Should().Be("jwtuser");

            // Assert — FirstName claim
            var givenNameClaim = FindClaim(token, ClaimTypes.GivenName, "given_name");
            givenNameClaim.Should().NotBeNull("FirstName claim (ClaimTypes.GivenName) is required");
            givenNameClaim!.Value.Should().Be("John");

            // Assert — LastName claim
            var surnameClaim = FindClaim(token, ClaimTypes.Surname, "family_name");
            surnameClaim.Should().NotBeNull("LastName claim (ClaimTypes.Surname) is required");
            surnameClaim!.Value.Should().Be("Doe");

            // Assert — Image claim
            var imageClaim = FindClaim(token, "image");
            imageClaim.Should().NotBeNull("Image claim is required for complete identity propagation");
            imageClaim!.Value.Should().Be("/avatar/john.png");
        }

        /// <summary>
        /// Verifies the token contains Role claims for each user role using role.Id (Guid)
        /// as the claim value — NOT role.Name (string). This is critical for
        /// SecurityContext.HasEntityPermission() which looks up RecordPermissions by role Guid.
        /// Companion "role_name" claims carry the human-readable name for [Authorize(Roles)] checks.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenContainsRoleClaims()
        {
            // Arrange
            var user = CreateTestUser(roleNames: new[] { "admin", "editor" });
            var expectedRoleIds = user.Roles.Select(r => r.Id.ToString()).ToList();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — Role claims should contain role.Id (Guid), not role.Name (string)
            var roleClaims = FindAllClaims(token, "role", ClaimTypes.Role);
            roleClaims.Should().HaveCount(2);
            // Each role claim value must be a valid Guid (role.Id)
            foreach (var claim in roleClaims)
            {
                Guid.TryParse(claim.Value, out _).Should().BeTrue(
                    "Role claim values must be Guids (role.Id) for SecurityContext.HasEntityPermission()");
            }
            roleClaims.Select(c => c.Value).Should().BeEquivalentTo(expectedRoleIds);

            // Verify companion role_name claims carry the human-readable names
            var roleNameClaims = FindAllClaims(token, "role_name");
            roleNameClaims.Should().HaveCount(2);
            roleNameClaims.Select(c => c.Value).Should().Contain("admin");
            roleNameClaims.Select(c => c.Value).Should().Contain("editor");
        }

        /// <summary>
        /// Verifies the token contains a custom "token_refresh_after" claim with
        /// a value that is a DateTime.ToBinary().ToString() approximately 120 minutes
        /// from now (TestRefreshMinutes).
        /// Source: AuthService.cs lines 152-153.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenContainsTokenRefreshAfterClaim()
        {
            // Arrange
            var beforeBuild = DateTime.UtcNow;
            var user = CreateTestUser();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — "token_refresh_after" is a custom claim type, stored as-is
            var refreshClaim = FindClaim(token, "token_refresh_after");
            refreshClaim.Should().NotBeNull();

            // Value must be parseable as a binary DateTime
            long binaryDate = long.Parse(refreshClaim!.Value);
            DateTime refreshAfter = DateTime.FromBinary(binaryDate);

            // Should be approximately UtcNow + 120 minutes
            var expectedRefresh = beforeBuild.AddMinutes(TestRefreshMinutes);
            refreshAfter.Should().BeCloseTo(expectedRefresh, TimeSpan.FromMinutes(2));
        }

        /// <summary>
        /// Verifies the token expiration is approximately 24 hours (1440 minutes) from now.
        /// Source: AuthService.cs line 158 uses DateTime.Now (local time) for expiry.
        /// JwtSecurityToken.ValidTo returns the expiry in UTC.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenHasCorrectExpiration()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — ValidTo is UTC; compare with UTC + expiry minutes
            var expectedExpiry = DateTime.UtcNow.AddMinutes(TestExpiryMinutes);
            token.ValidTo.Should().BeCloseTo(expectedExpiry, TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Verifies the token uses HMAC SHA-256 signing algorithm.
        /// Source: AuthService.cs line 156: SecurityAlgorithms.HmacSha256Signature
        /// In JWT header, this is represented as "HS256".
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenUsesHmacSha256()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — The implementation uses SecurityAlgorithms.HmacSha256Signature
            // which is the full URI form. The header stores the algorithm as provided
            // to the SigningCredentials constructor. Both HS256 and the URI are valid.
            token.Header.Alg.Should().BeOneOf(
                "HS256",
                SecurityAlgorithms.HmacSha256Signature);
        }

        /// <summary>
        /// Verifies the token issuer matches the configured value "webvella-erp".
        /// Source: ErpSettings.cs line 119 default.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenHasCorrectIssuer()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert
            token.Issuer.Should().Be(TestIssuer);
        }

        /// <summary>
        /// Verifies the token audience includes the configured value "webvella-erp".
        /// Source: ErpSettings.cs line 120 default.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_TokenHasCorrectAudience()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert
            token.Audiences.Should().Contain(TestAudience);
        }

        #endregion

        #region Phase 3: Token Validation Tests (GetValidSecurityTokenAsync)

        /// <summary>
        /// Verifies that a valid token (just created) can be successfully validated.
        /// Source: AuthService.cs lines 120-143 (GetValidSecurityTokenAsync).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithValidToken_ReturnsJwtSecurityToken()
        {
            // Arrange
            var user = CreateTestUser();
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Act
            var result = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<JwtSecurityToken>();
        }

        /// <summary>
        /// Verifies that a token signed with a different key fails validation
        /// (ValidateIssuerSigningKey = true is enforced).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithInvalidSignature_ReturnsNull()
        {
            // Arrange — Build token with the standard handler (TestKey)
            var user = CreateTestUser();
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Create a different handler with a different signing key
            var differentOptions = CreateDefaultOptions();
            differentOptions.Key = "ACompletelyDifferentSecretKey12345!!";
            var differentHandler = new JwtTokenHandler(differentOptions);

            // Act — Validate with the different key handler
            var result = await differentHandler.GetValidSecurityTokenAsync(tokenString);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that an expired token fails validation.
        /// Manually constructs an expired JWT to ensure the token is clearly past
        /// the default 5-minute clock skew threshold.
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithExpiredToken_ReturnsNull()
        {
            // Arrange — Manually create an expired token using the same key
            var keyBytes = Encoding.UTF8.GetBytes(TestKey);
            var securityKey = new SymmetricSecurityKey(keyBytes);
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature);

            var expiredToken = new JwtSecurityToken(
                TestIssuer,
                TestAudience,
                new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) },
                expires: DateTime.UtcNow.AddMinutes(-30), // Clearly expired, well beyond clock skew
                signingCredentials: credentials);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(expiredToken);

            // Act
            var result = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a token with a different issuer fails validation
        /// (ValidateIssuer = true is enforced).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithInvalidIssuer_ReturnsNull()
        {
            // Arrange — Build a token with a different issuer
            var differentIssuerOptions = CreateDefaultOptions();
            differentIssuerOptions.Issuer = "different-issuer";
            var differentHandler = new JwtTokenHandler(differentIssuerOptions);

            var user = CreateTestUser();
            var (tokenString, _) = await differentHandler.BuildTokenAsync(user);

            // Act — Validate with the standard handler expecting "webvella-erp" issuer
            var result = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a token with a different audience fails validation
        /// (ValidateAudience = true is enforced).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithInvalidAudience_ReturnsNull()
        {
            // Arrange — Build a token with a different audience
            var differentAudienceOptions = CreateDefaultOptions();
            differentAudienceOptions.Audience = "different-audience";
            var differentHandler = new JwtTokenHandler(differentAudienceOptions);

            var user = CreateTestUser();
            var (tokenString, _) = await differentHandler.BuildTokenAsync(user);

            // Act — Validate with the standard handler expecting "webvella-erp" audience
            var result = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that an empty string token returns null (handled by the
        /// string.IsNullOrWhiteSpace guard in the implementation).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithEmptyString_ReturnsNull()
        {
            // Act
            var result = await _handler.GetValidSecurityTokenAsync(string.Empty);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a garbage (non-JWT) string returns null
        /// (caught by the try/catch in the implementation).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithGarbageString_ReturnsNull()
        {
            // Act
            var result = await _handler.GetValidSecurityTokenAsync("not-a-valid-jwt-token-at-all");

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a null token returns null
        /// (handled by string.IsNullOrWhiteSpace guard, source line 241).
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithNullToken_ReturnsNull()
        {
            // Act
            var result = await _handler.GetValidSecurityTokenAsync(null);

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Phase 4: Token Round-Trip Tests

        /// <summary>
        /// Verifies that building a token and then validating it preserves all claims:
        /// NameIdentifier (user Id), Email, Role claims, and token_refresh_after.
        /// This is the critical end-to-end test for JWT token integrity.
        /// </summary>
        [Fact]
        public async Task RoundTrip_BuildAndValidate_PreservesAllClaims()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var user = CreateTestUser(
                id: userId,
                email: "roundtrip@test.com",
                roleNames: new[] { "admin", "user" });

            // Act — Build and then validate (round-trip through serialization)
            var (tokenString, _) = await _handler.BuildTokenAsync(user);
            var validatedToken = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Assert — All claims should survive the round-trip
            validatedToken.Should().NotBeNull();

            // NameIdentifier via claim lookup (Subject may be null depending on JWT library version)
            var subClaim = FindClaim(validatedToken!, "sub", ClaimTypes.NameIdentifier);
            subClaim.Should().NotBeNull();
            subClaim!.Value.Should().Be(userId.ToString());

            // Email claim
            var emailClaim = FindClaim(validatedToken, "email", ClaimTypes.Email);
            emailClaim.Should().NotBeNull();
            emailClaim!.Value.Should().Be("roundtrip@test.com");

            // Role claims — values are role.Id (Guid), not role.Name (string)
            var roleClaims = FindAllClaims(validatedToken, "role", ClaimTypes.Role);
            roleClaims.Should().HaveCount(2);
            foreach (var claim in roleClaims)
            {
                Guid.TryParse(claim.Value, out _).Should().BeTrue(
                    "Role claim values must be Guids (role.Id) for SecurityContext permission checks");
            }
            var expectedRoleIds = user.Roles.Select(r => r.Id.ToString()).ToList();
            roleClaims.Select(c => c.Value).Should().BeEquivalentTo(expectedRoleIds);

            // token_refresh_after custom claim
            var refreshClaim = FindClaim(validatedToken, "token_refresh_after");
            refreshClaim.Should().NotBeNull();

            // Verify refresh claim value round-trips as a valid binary DateTime
            long binaryDate = long.Parse(refreshClaim!.Value);
            DateTime refreshAfter = DateTime.FromBinary(binaryDate);
            refreshAfter.Should().BeCloseTo(
                DateTime.UtcNow.AddMinutes(TestRefreshMinutes),
                TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Verifies that tokens with multiple roles (3) preserve all role claims
        /// (as role.Id Guids) through the build → validate round-trip.
        /// </summary>
        [Fact]
        public async Task RoundTrip_MultipleRoles_AllPreserved()
        {
            // Arrange
            var user = CreateTestUser(roleNames: new[] { "admin", "editor", "viewer" });
            var expectedRoleIds = user.Roles.Select(r => r.Id.ToString()).ToList();

            // Act
            var (tokenString, _) = await _handler.BuildTokenAsync(user);
            var validatedToken = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Assert — Role claim values are role.Id (Guid), not role.Name (string)
            validatedToken.Should().NotBeNull();
            var roleClaims = FindAllClaims(validatedToken!, "role", ClaimTypes.Role);
            roleClaims.Should().HaveCount(3);
            roleClaims.Select(c => c.Value).Should().BeEquivalentTo(expectedRoleIds);

            // Companion role_name claims carry the human-readable role names
            var roleNameClaims = FindAllClaims(validatedToken!, "role_name");
            roleNameClaims.Should().HaveCount(3);
            roleNameClaims.Select(c => c.Value).Should().Contain("admin");
            roleNameClaims.Select(c => c.Value).Should().Contain("editor");
            roleNameClaims.Select(c => c.Value).Should().Contain("viewer");
        }

        #endregion

        #region Phase 5: Claim Extraction Helper Tests

        /// <summary>
        /// Verifies ExtractUserIdFromToken returns the correct Guid when the token
        /// contains a valid NameIdentifier claim. Uses build → validate → extract
        /// workflow to ensure the claim survives serialization.
        /// </summary>
        [Fact]
        public async Task ExtractUserIdFromToken_WithValidToken_ReturnsCorrectGuid()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var user = CreateTestUser(id: userId);
            var (tokenString, _) = await _handler.BuildTokenAsync(user);
            var validatedToken = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Act
            var extractedId = JwtTokenHandler.ExtractUserIdFromToken(validatedToken);

            // Assert
            extractedId.Should().NotBeNull();
            extractedId!.Value.Should().Be(userId);
        }

        /// <summary>
        /// Verifies ExtractUserIdFromToken returns null when the token has no
        /// NameIdentifier claim.
        /// </summary>
        [Fact]
        public async Task ExtractUserIdFromToken_WithNoNameIdentifier_ReturnsNull()
        {
            // Arrange — Build a token with only an email claim (no NameIdentifier)
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Email, "noident@test.com")
            };
            var (tokenString, _) = await _handler.BuildTokenAsync(claims);
            var validatedToken = await _handler.GetValidSecurityTokenAsync(tokenString);

            // Act
            var extractedId = JwtTokenHandler.ExtractUserIdFromToken(validatedToken);

            // Assert
            extractedId.Should().BeNull();
        }

        /// <summary>
        /// Verifies IsTokenRefreshRequired returns false for a freshly created token
        /// whose token_refresh_after is 120 minutes in the future.
        /// </summary>
        [Fact]
        public async Task IsTokenRefreshRequired_WithFreshToken_ReturnsFalse()
        {
            // Arrange — Fresh token has token_refresh_after set to UtcNow + 120 min
            var user = CreateTestUser();
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Act
            var result = JwtTokenHandler.IsTokenRefreshRequired(token);

            // Assert — Token was just created, refresh is not required yet
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies IsTokenRefreshRequired returns true when the token_refresh_after
        /// claim is set to a past DateTime, indicating the refresh window has expired.
        /// Uses BuildTokenAsync(IEnumerable&lt;Claim&gt;) to inject a custom past-dated claim.
        /// </summary>
        [Fact]
        public async Task IsTokenRefreshRequired_WithExpiredRefresh_ReturnsTrue()
        {
            // Arrange — Create a token with token_refresh_after set to 60 minutes ago
            var pastTime = DateTime.UtcNow.AddMinutes(-60);
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("token_refresh_after", pastTime.ToBinary().ToString())
            };
            var (_, token) = await _handler.BuildTokenAsync(claims);

            // Act
            var result = JwtTokenHandler.IsTokenRefreshRequired(token);

            // Assert — Refresh window has passed
            result.Should().BeTrue();
        }

        #endregion

        #region Phase 6: Configuration Tests (JwtTokenOptions)

        /// <summary>
        /// Verifies that JwtTokenOptions default values use the standardized development
        /// key constant and match the monolith's ErpSettings and AuthService constant
        /// values for backward compatibility.
        /// AuthService.cs lines 19-20: Expiry=1440, Refresh=120
        /// </summary>
        [Fact]
        public void JwtTokenOptions_DefaultValues_MatchMonolithDefaults()
        {
            // Arrange & Act
            var options = new JwtTokenOptions();

            // Assert — Key defaults to the shared development constant (>= 32 chars)
            options.Key.Should().Be(JwtTokenOptions.DefaultDevelopmentKey);
            options.Key.Length.Should().BeGreaterOrEqualTo(32,
                "Default key must be at least 32 chars for HMAC-SHA256 without padding");
            options.Issuer.Should().Be("webvella-erp");
            options.Audience.Should().Be("webvella-erp");
            options.TokenExpiryMinutes.Should().Be(1440);
            options.TokenRefreshMinutes.Should().Be(120);
        }

        /// <summary>
        /// Verifies the JwtTokenHandler(string, string, string) constructor overload
        /// creates a functional handler that can build and validate tokens.
        /// </summary>
        [Fact]
        public async Task Constructor_WithStringParameters_CreatesHandler()
        {
            // Arrange
            var handler = new JwtTokenHandler(TestKey, TestIssuer, TestAudience);
            var user = CreateTestUser();

            // Act — Build a token
            var (tokenString, token) = await handler.BuildTokenAsync(user);

            // Assert — Token was created successfully
            tokenString.Should().NotBeNullOrEmpty();
            token.Should().NotBeNull();

            // And can be validated by the same handler
            var validatedToken = await handler.GetValidSecurityTokenAsync(tokenString);
            validatedToken.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies the JwtTokenHandler(JwtTokenOptions) constructor creates
        /// a functional handler.
        /// </summary>
        [Fact]
        public async Task Constructor_WithOptions_CreatesHandler()
        {
            // Arrange
            var options = CreateDefaultOptions();
            var handler = new JwtTokenHandler(options);
            var user = CreateTestUser();

            // Act
            var (tokenString, token) = await handler.BuildTokenAsync(user);

            // Assert
            tokenString.Should().NotBeNullOrEmpty();
            token.Should().NotBeNull();
        }

        #endregion

        #region Phase 7: Edge Cases

        /// <summary>
        /// Verifies that passing a null user to BuildTokenAsync throws
        /// ArgumentNullException (source line 154-155).
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_WithNullUser_ThrowsArgumentNullException()
        {
            // Arrange & Act
            Func<Task> act = async () => await _handler.BuildTokenAsync((ErpUser)null!);

            // Assert
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        /// <summary>
        /// Verifies that a user with no roles produces a token containing
        /// NameIdentifier and Email claims but no Role claims.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_WithEmptyRoles_ProducesTokenWithNoRoleClaims()
        {
            // Arrange — User with explicitly no roles
            var user = CreateTestUser(); // No roleNames specified

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — No role claims present
            var roleClaims = FindAllClaims(token, "role", ClaimTypes.Role);
            roleClaims.Should().BeEmpty();

            // But identity claims should still be present
            var subClaim = FindClaim(token, "sub", ClaimTypes.NameIdentifier);
            subClaim.Should().NotBeNull();
            var emailClaim = FindClaim(token, "email", ClaimTypes.Email);
            emailClaim.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that a user with an empty email string produces a token
        /// with an empty email claim value (not missing, but empty).
        /// ErpUser constructor default: Email = String.Empty.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_WithEmptyEmail_ProducesTokenWithEmptyEmailClaim()
        {
            // Arrange
            var user = CreateTestUser(email: "");

            // Act
            var (_, token) = await _handler.BuildTokenAsync(user);

            // Assert — Email claim exists but has empty value
            var emailClaim = FindClaim(token, "email", ClaimTypes.Email);
            emailClaim.Should().NotBeNull();
            emailClaim!.Value.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a user with unicode/special characters in the username
        /// can still produce a valid token that survives the build → validate round-trip.
        /// </summary>
        [Fact]
        public async Task BuildTokenAsync_WithSpecialCharactersInUsername_TokenStillValid()
        {
            // Arrange — Unicode and special characters in username
            var user = CreateTestUser(
                username: "тест-user@special!#$%&()",
                email: "special@test.com");

            // Act
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Assert — Token should be valid through round-trip
            tokenString.Should().NotBeNullOrEmpty();
            var validatedToken = await _handler.GetValidSecurityTokenAsync(tokenString);
            validatedToken.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that tampering with a token's signature portion causes validation
        /// to fail (returns null), confirming cryptographic integrity checking.
        /// </summary>
        [Fact]
        public async Task GetValidSecurityTokenAsync_WithTamperedToken_ReturnsNull()
        {
            // Arrange — Create a valid token
            var user = CreateTestUser();
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Tamper with the signature section (third part of JWT: header.payload.signature)
            var parts = tokenString.Split('.');
            parts.Length.Should().Be(3, "JWT tokens have exactly 3 parts separated by dots");

            var tamperedSignature = parts[2].Length > 5
                ? "XXXXX" + parts[2].Substring(5)
                : "XXXXX";
            var tamperedToken = $"{parts[0]}.{parts[1]}.{tamperedSignature}";

            // Act
            var result = await _handler.GetValidSecurityTokenAsync(tamperedToken);

            // Assert
            result.Should().BeNull();
        }

        #endregion

        #region Phase 8: Token Refresh Tests

        /// <summary>
        /// Verifies that RefreshTokenAsync returns a new valid token string when
        /// given a valid existing token and an enabled user.
        /// Source: Adapted from AuthService.GetNewTokenAsync (lines 94-117).
        /// </summary>
        [Fact]
        public async Task RefreshTokenAsync_WithValidTokenAndUser_ReturnsNewToken()
        {
            // Arrange
            var user = CreateTestUser();
            var (originalToken, _) = await _handler.BuildTokenAsync(user);

            // Act
            var newToken = await _handler.RefreshTokenAsync(originalToken, user);

            // Assert — New token should be returned (non-null/empty)
            newToken.Should().NotBeNullOrEmpty();

            // The new token should also be valid
            var validatedNewToken = await _handler.GetValidSecurityTokenAsync(newToken);
            validatedNewToken.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that RefreshTokenAsync returns null when given an invalid
        /// (non-JWT) token string.
        /// </summary>
        [Fact]
        public async Task RefreshTokenAsync_WithInvalidToken_ReturnsNull()
        {
            // Arrange
            var user = CreateTestUser();

            // Act
            var result = await _handler.RefreshTokenAsync("invalid-token-string", user);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that RefreshTokenAsync returns null when the user is null,
        /// even if the token is valid.
        /// </summary>
        [Fact]
        public async Task RefreshTokenAsync_WithNullUser_ReturnsNull()
        {
            // Arrange
            var user = CreateTestUser();
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Act
            var result = await _handler.RefreshTokenAsync(tokenString, null);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies that RefreshTokenAsync returns null when the user is disabled
        /// (Enabled = false), even if the token itself is valid.
        /// This preserves the monolith's security check from AuthService.GetNewTokenAsync
        /// (source line 109: user.Enabled check).
        /// </summary>
        [Fact]
        public async Task RefreshTokenAsync_WithDisabledUser_ReturnsNull()
        {
            // Arrange
            var user = CreateTestUser();
            var (tokenString, _) = await _handler.BuildTokenAsync(user);

            // Create a disabled user for the refresh attempt
            var disabledUser = CreateTestUser();
            disabledUser.Enabled = false;

            // Act
            var result = await _handler.RefreshTokenAsync(tokenString, disabledUser);

            // Assert
            result.Should().BeNull();
        }

        #endregion
    }
}
