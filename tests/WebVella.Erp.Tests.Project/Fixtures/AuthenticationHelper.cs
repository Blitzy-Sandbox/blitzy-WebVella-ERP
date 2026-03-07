using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WebVella.Erp.Tests.Project.Fixtures
{
    /// <summary>
    /// Provides JWT token generation for test authentication scenarios.
    /// All tokens use the same key/issuer/audience as configured in the Project service appsettings.json.
    /// 
    /// This helper enables integration tests to authenticate HTTP and gRPC requests
    /// using valid, expired, or malformed JWT tokens without depending on a running
    /// Core/Identity service. Constants are extracted from the monolith's Config.json
    /// and Definitions.cs to ensure exact parity with the production JWT configuration.
    /// </summary>
    public static class AuthenticationHelper
    {
        #region JWT Configuration Constants — MUST match Config.json EXACTLY

        /// <summary>
        /// JWT signing key — EXACT value from WebVella.Erp.Site.Project/Config.json line 20.
        /// Used with HMAC-SHA256 algorithm to sign and validate tokens.
        /// </summary>
        public const string JwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";

        /// <summary>
        /// JWT issuer — EXACT value from WebVella.Erp.Site.Project/Config.json line 21.
        /// Validated by the Project service's TokenValidationParameters.ValidIssuer.
        /// </summary>
        public const string JwtIssuer = "webvella-erp";

        /// <summary>
        /// JWT audience — EXACT value from WebVella.Erp.Site.Project/Config.json line 22.
        /// Validated by the Project service's TokenValidationParameters.ValidAudience.
        /// </summary>
        public const string JwtAudience = "webvella-erp";

        #endregion

        #region Well-Known User and Role IDs — MUST match Definitions.cs EXACTLY

        /// <summary>
        /// System user ID — from WebVella.Erp/Api/Definitions.cs line 19.
        /// The system user is an internal identity used for background jobs and system-level operations.
        /// </summary>
        public static readonly Guid SystemUserId = new Guid("10000000-0000-0000-0000-000000000000");

        /// <summary>
        /// First (admin) user ID — from WebVella.Erp/Api/Definitions.cs line 20.
        /// This is the initial administrator user created during ERP bootstrap (email: erp@webvella.com).
        /// </summary>
        public static readonly Guid FirstUserId = new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2");

        /// <summary>
        /// Administrator role ID — from WebVella.Erp/Api/Definitions.cs line 15.
        /// Users with this role have full administrative privileges across the ERP system.
        /// </summary>
        public static readonly Guid AdministratorRoleId = new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA");

        /// <summary>
        /// Regular role ID — from WebVella.Erp/Api/Definitions.cs line 16.
        /// Standard authenticated user role with normal operational permissions.
        /// </summary>
        public static readonly Guid RegularRoleId = new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F");

        /// <summary>
        /// Guest role ID — from WebVella.Erp/Api/Definitions.cs line 17.
        /// Limited-access role used for unauthenticated or guest-level access testing.
        /// </summary>
        public static readonly Guid GuestRoleId = new Guid("987148B1-AFA8-4B33-8616-55861E5FD065");

        #endregion

        #region Valid Token Generation Methods

        /// <summary>
        /// Generates a JWT token for the first/admin user with Administrator and Regular roles.
        /// This is the primary token for happy-path integration tests requiring full admin privileges.
        /// 
        /// Source: ERPService.cs lines 462-476 — First user "erp@webvella.com" with admin + regular roles.
        /// Source: SecurityContext.cs lines 17-27 — System user role assignment pattern.
        /// </summary>
        /// <returns>A valid JWT bearer token string for the administrator user.</returns>
        public static string GenerateAdminToken()
        {
            return GenerateToken(
                userId: FirstUserId,
                username: "administrator",
                email: "erp@webvella.com",
                roleIds: new[] { AdministratorRoleId, RegularRoleId },
                expiresInHours: 1
            );
        }

        /// <summary>
        /// Generates a JWT token for the system user with Administrator role.
        /// Used for testing system-level operations like background jobs and internal API calls.
        /// 
        /// Source: SecurityContext.cs lines 17-27 — System user has Id=SystemUserId,
        /// Username="system", Email="system@webvella.com", and AdministratorRole.
        /// </summary>
        /// <returns>A valid JWT bearer token string for the system user.</returns>
        public static string GenerateSystemToken()
        {
            return GenerateToken(
                userId: SystemUserId,
                username: "system",
                email: "system@webvella.com",
                roleIds: new[] { AdministratorRoleId },
                expiresInHours: 1
            );
        }

        /// <summary>
        /// Generates a JWT token for a regular (non-admin) user with only the Regular role.
        /// Used for testing standard user operations and verifying authorization boundaries.
        /// </summary>
        /// <param name="userId">The unique identifier for the user.</param>
        /// <param name="username">The username claim value.</param>
        /// <param name="email">The email claim value.</param>
        /// <returns>A valid JWT bearer token string for a regular user.</returns>
        public static string GenerateRegularUserToken(Guid userId, string username, string email)
        {
            return GenerateToken(
                userId: userId,
                username: username,
                email: email,
                roleIds: new[] { RegularRoleId },
                expiresInHours: 1
            );
        }

        /// <summary>
        /// Generates a JWT token for a guest user with only the Guest role.
        /// Used for testing authorization failure scenarios where guest-level access
        /// is insufficient for the requested operation.
        /// </summary>
        /// <returns>A valid JWT bearer token string for a guest user with a random user ID.</returns>
        public static string GenerateGuestToken()
        {
            return GenerateToken(
                userId: Guid.NewGuid(),
                username: "guest",
                email: "guest@test.com",
                roleIds: new[] { GuestRoleId },
                expiresInHours: 1
            );
        }

        /// <summary>
        /// Core token generation method that constructs a JWT with the specified claims.
        /// All other token generation methods delegate to this method.
        /// 
        /// The token is signed with HMAC-SHA256 using the JwtKey constant and includes:
        /// - Sub (subject): user ID
        /// - Email: user email address
        /// - Username: custom claim for the user's login name
        /// - Jti (JWT ID): unique token identifier for replay prevention
        /// - Role claims: one per role ID for authorization
        /// </summary>
        /// <param name="userId">The unique identifier for the user (becomes the 'sub' claim).</param>
        /// <param name="username">The username (becomes the 'username' custom claim).</param>
        /// <param name="email">The email address (becomes the 'email' claim).</param>
        /// <param name="roleIds">Array of role GUIDs (each becomes a 'role' claim).</param>
        /// <param name="expiresInHours">Number of hours until token expiration. Use negative values for expired tokens.</param>
        /// <returns>A signed JWT token string.</returns>
        public static string GenerateToken(Guid userId, string username, string email, Guid[] roleIds, double expiresInHours)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
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
                issuer: JwtIssuer,
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(expiresInHours),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        #endregion

        #region Negative Test Token Methods

        /// <summary>
        /// Generates a JWT token that has already expired (1 hour in the past).
        /// Used for testing that the Project service correctly rejects expired tokens
        /// when ValidateLifetime is enabled in TokenValidationParameters.
        /// </summary>
        /// <returns>An expired JWT bearer token string.</returns>
        public static string GenerateExpiredToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, FirstUserId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "erp@webvella.com"),
                new Claim("username", "administrator"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, AdministratorRoleId.ToString())
            };

            var token = new JwtSecurityToken(
                issuer: JwtIssuer,
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(-1), // Already expired
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Generates a JWT token with only a JTI claim, missing all required claims
        /// (sub, email, username, role). Used for testing that the Project service
        /// gracefully handles tokens that pass signature validation but lack
        /// the claims needed for identity resolution and authorization.
        /// </summary>
        /// <returns>A valid but incomplete JWT bearer token string.</returns>
        public static string GenerateTokenWithMissingClaims()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            // Only include JTI — no sub, email, username, or role claims
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: JwtIssuer,
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Generates a JWT token signed with a DIFFERENT key than the one configured
        /// in the Project service. Used for testing that the service correctly rejects
        /// tokens with invalid signatures when ValidateIssuerSigningKey is enabled.
        /// </summary>
        /// <returns>A JWT bearer token string signed with the wrong key.</returns>
        public static string GenerateTokenWithWrongKey()
        {
            var wrongKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("WrongKeyWrongKeyWrongKeyWrongKeyWrongKeyWrongKey"));
            var creds = new SigningCredentials(wrongKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, FirstUserId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "erp@webvella.com"),
                new Claim("username", "administrator"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, AdministratorRoleId.ToString())
            };

            var token = new JwtSecurityToken(
                issuer: JwtIssuer,
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Generates a JWT token with a mismatched issuer ("wrong-issuer" instead of "webvella-erp").
        /// Used for testing that the Project service correctly rejects tokens from
        /// untrusted issuers when ValidateIssuer is enabled in TokenValidationParameters.
        /// </summary>
        /// <returns>A JWT bearer token string with an incorrect issuer claim.</returns>
        public static string GenerateTokenWithWrongIssuer()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, FirstUserId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "erp@webvella.com"),
                new Claim("username", "administrator"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.Role, AdministratorRoleId.ToString()),
                new Claim(ClaimTypes.Role, RegularRoleId.ToString())
            };

            var token = new JwtSecurityToken(
                issuer: "wrong-issuer", // Mismatched issuer — should be rejected by validation
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        #endregion

        #region Custom Token Generation

        /// <summary>
        /// Public convenience method for creating tokens with custom user IDs and roles.
        /// Used in specific test scenarios such as testing author-only operations
        /// (e.g., only the comment author can delete their comment in CommentService,
        /// or only the timelog author can modify their entry in TimelogService).
        /// </summary>
        /// <param name="userId">The unique identifier for the user.</param>
        /// <param name="username">The username claim value.</param>
        /// <param name="email">The email claim value.</param>
        /// <param name="roleIds">Variable number of role GUIDs to include as role claims.</param>
        /// <returns>A valid JWT bearer token string for the specified user and roles.</returns>
        public static string GenerateTokenForUser(Guid userId, string username, string email, params Guid[] roleIds)
        {
            return GenerateToken(
                userId: userId,
                username: username,
                email: email,
                roleIds: roleIds,
                expiresInHours: 1
            );
        }

        #endregion
    }
}
