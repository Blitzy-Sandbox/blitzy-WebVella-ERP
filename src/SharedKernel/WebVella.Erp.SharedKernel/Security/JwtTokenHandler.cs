using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.SharedKernel.Security
{
	/// <summary>
	/// Configuration POCO for JWT token generation and validation settings.
	/// Default values are preserved from the monolith's ErpSettings and AuthService constants
	/// to ensure backward compatibility with existing tokens.
	/// </summary>
	public class JwtTokenOptions
	{
		/// <summary>
		/// Shared default development signing key used across ALL microservices as a
		/// consistent fallback when no explicit JWT key is configured. This constant
		/// ensures that in development environments without explicit config, all
		/// services use the same signing key and can validate each other's tokens.
		///
		/// The key is 50 characters (400 bits) — well above the 256-bit HMAC-SHA256
		/// minimum — so no padding is required by JwtTokenHandler.GetSigningKeyBytes().
		///
		/// Based on the monolith's Config.json key pattern (repeated base key).
		/// MUST be overridden in production via configuration.
		/// </summary>
		public const string DefaultDevelopmentKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKe";

		/// <summary>
		/// HMAC SHA-256 signing key for JWT tokens.
		/// Default: <see cref="DefaultDevelopmentKey"/> — a 50-character development-only key.
		/// The monolith's ErpSettings default was "ThisIsMySecretKey" (17 chars), but
		/// production Config.json used a 50-character key. The longer default prevents
		/// key padding discrepancies across services.
		/// MUST be overridden in production environments.
		/// </summary>
		public string Key { get; set; } = DefaultDevelopmentKey;

		/// <summary>
		/// JWT token issuer claim value.
		/// Default: "webvella-erp" (from ErpSettings.cs Initialize method).
		/// </summary>
		public string Issuer { get; set; } = "webvella-erp";

		/// <summary>
		/// JWT token audience claim value.
		/// Default: "webvella-erp" (from ErpSettings.cs Initialize method).
		/// </summary>
		public string Audience { get; set; } = "webvella-erp";

		/// <summary>
		/// Token expiration duration in minutes. After this time the token is invalid.
		/// Default: 1440 minutes (24 hours) — from AuthService.JWT_TOKEN_EXPIRY_DURATION_MINUTES.
		/// </summary>
		public double TokenExpiryMinutes { get; set; } = 1440;

		/// <summary>
		/// Duration in minutes after which the token should be proactively refreshed
		/// to avoid expiration during an active session.
		/// Default: 120 minutes (2 hours) — from AuthService.JWT_TOKEN_FORCE_REFRESH_MINUTES.
		/// </summary>
		public double TokenRefreshMinutes { get; set; } = 120;
	}

	/// <summary>
	/// Shared JWT token handler providing creation, validation, and refresh capabilities
	/// for all microservices. Extracted from WebVella.Erp.Web.Services.AuthService (JWT region,
	/// lines 81-165) with the following adaptations:
	/// 
	/// - Replaced static ErpSettings references with injected JwtTokenOptions configuration.
	/// - Removed all HttpContext, cookie authentication, and SecurityManager/database dependencies.
	/// - Made methods instance-based (non-static) to use injected configuration.
	/// - Added RefreshTokenAsync that accepts a pre-resolved ErpUser (caller handles user lookup).
	/// - Added static helpers ExtractUserIdFromToken and IsTokenRefreshRequired.
	/// - Added BuildTokenAsync(IEnumerable&lt;Claim&gt;) overload for raw claim-based token creation.
	///
	/// Signing: HMAC SHA-256 (SecurityAlgorithms.HmacSha256Signature).
	/// Token format: JWT with NameIdentifier, Name, Email, GivenName, Surname, image, Role,
	/// role_name, and token_refresh_after claims — matching ErpUser.ToClaims() for complete
	/// cross-service identity propagation.
	/// Refresh format: token_refresh_after uses DateTime.ToBinary().ToString() for backward compatibility.
	/// </summary>
	public class JwtTokenHandler
	{
		/// <summary>
		/// Custom claim type for the token refresh-after timestamp.
		/// Stored as a binary DateTime string (DateTime.ToBinary().ToString()).
		/// </summary>
		private const string TokenRefreshAfterClaimType = "token_refresh_after";

		/// <summary>
		/// Minimum key size in bytes required by HMAC-SHA256 (256 bits = 32 bytes).
		/// </summary>
		private const int MinKeyLengthBytes = 32;

		/// <summary>
		/// JWT configuration options injected via constructor.
		/// </summary>
		private readonly JwtTokenOptions _options;

		/// <summary>
		/// Creates a new JwtTokenHandler with the specified JWT options.
		/// </summary>
		/// <param name="options">JWT configuration options containing signing key, issuer, audience, and expiry settings.</param>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
		public JwtTokenHandler(JwtTokenOptions options)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));
		}

		/// <summary>
		/// Creates a new JwtTokenHandler with explicit key, issuer, and audience values.
		/// Other settings (TokenExpiryMinutes, TokenRefreshMinutes) use their defaults.
		/// </summary>
		/// <param name="key">HMAC SHA-256 signing key string.</param>
		/// <param name="issuer">JWT issuer claim value.</param>
		/// <param name="audience">JWT audience claim value.</param>
		public JwtTokenHandler(string key, string issuer, string audience)
		{
			_options = new JwtTokenOptions
			{
				Key = key ?? throw new ArgumentNullException(nameof(key)),
				Issuer = issuer ?? throw new ArgumentNullException(nameof(issuer)),
				Audience = audience ?? throw new ArgumentNullException(nameof(audience))
			};
		}

		/// <summary>
		/// Returns signing key bytes that meet the minimum 256-bit (32-byte) requirement
		/// for HMAC-SHA256. If the configured key is shorter than 32 bytes (e.g., the
		/// monolith default "ThisIsMySecretKey" at 17 bytes), the key material is cycled
		/// to reach the minimum length. Production deployments should always configure
		/// keys of at least 32 characters; the monolith's Config.json uses a 50-character
		/// key ("ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey").
		/// </summary>
		/// <returns>Byte array suitable for SymmetricSecurityKey construction.</returns>
		private byte[] GetSigningKeyBytes()
		{
			byte[] keyBytes = Encoding.UTF8.GetBytes(_options.Key);
			if (keyBytes.Length >= MinKeyLengthBytes)
				return keyBytes;

			// Pad short keys by cycling the key material to meet the 256-bit minimum
			byte[] paddedKey = new byte[MinKeyLengthBytes];
			for (int i = 0; i < MinKeyLengthBytes; i++)
				paddedKey[i] = keyBytes[i % keyBytes.Length];
			return paddedKey;
		}

		/// <summary>
		/// Builds a JWT token for the given ErpUser. Delegates to <see cref="ErpUser.ToClaims()"/>
		/// which produces the complete claim set required for cross-service identity propagation:
		/// NameIdentifier (user.Id), Name (Username), Email, GivenName (FirstName),
		/// Surname (LastName), image, Role (role.Id as Guid), and role_name (role.Name).
		///
		/// Using ErpUser.ToClaims() ensures round-trip fidelity with ErpUser.FromClaims():
		/// - Role claims use role.Id (Guid) as the value, enabling SecurityContext.HasEntityPermission()
		///   which looks up RecordPermissions by role Guid.
		/// - role_name companion claims carry the human-readable name for ASP.NET [Authorize(Roles)] checks.
		/// - All identity fields (Username, FirstName, LastName, Image) are included so downstream
		///   services can reconstruct a complete ErpUser from JWT claims without callback to Core.
		///
		/// Adapted from AuthService.BuildTokenAsync(ErpUser) — source lines 145-160.
		///
		/// NOTE: Token expiration uses DateTime.Now (local time), NOT DateTime.UtcNow — this
		/// matches the original monolith behavior and must be preserved for backward compatibility.
		/// The token_refresh_after claim uses DateTime.UtcNow as in the original source.
		/// </summary>
		/// <param name="user">The ErpUser to create the token for. Must not be null.</param>
		/// <returns>A tuple containing the serialized token string and the JwtSecurityToken object.</returns>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="user"/> is null.</exception>
#pragma warning disable 1998
		public async ValueTask<(string TokenString, JwtSecurityToken Token)> BuildTokenAsync(ErpUser user)
		{
			if (user == null)
				throw new ArgumentNullException(nameof(user));

			// Delegate to ErpUser.ToClaims() for the complete, round-trip-safe claim set.
			// ToClaims() produces: NameIdentifier(Id), Name(Username), Email, GivenName(FirstName),
			// Surname(LastName), image, Role(role.Id), role_name(role.Name), plus custom Claims dict.
			var claims = user.ToClaims();

			// token_refresh_after claim: DateTime.UtcNow + configured refresh minutes, stored as binary
			DateTime tokenRefreshAfterDateTime = DateTime.UtcNow.AddMinutes(_options.TokenRefreshMinutes);
			claims.Add(new Claim(type: TokenRefreshAfterClaimType, value: tokenRefreshAfterDateTime.ToBinary().ToString()));

			// Sign with HMAC SHA-256 using standard "HS256" algorithm identifier.
			// CRITICAL: Must use SecurityAlgorithms.HmacSha256 (= "HS256"), NOT
			// SecurityAlgorithms.HmacSha256Signature (= XML URI), because downstream
			// services validate with ValidAlgorithms = { "HS256" } and will reject
			// tokens signed with the XML URI algorithm identifier.
			var securityKey = new SymmetricSecurityKey(GetSigningKeyBytes());
			var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

			// Create token with configured issuer, audience, and expiry
			// NOTE: expires uses DateTime.Now (local time) — preserved from source line 158
			var tokenDescriptor = new JwtSecurityToken(
				_options.Issuer,
				_options.Audience,
				claims,
				expires: DateTime.Now.AddMinutes(_options.TokenExpiryMinutes),
				signingCredentials: credentials);

			return (new JwtSecurityTokenHandler().WriteToken(tokenDescriptor), tokenDescriptor);
		}
#pragma warning restore 1998

		/// <summary>
		/// Builds a JWT token from a pre-built set of claims. If the claims do not already
		/// contain a token_refresh_after claim, one is automatically added using the configured
		/// TokenRefreshMinutes value. Uses the same signing and expiry logic as the ErpUser overload.
		/// </summary>
		/// <param name="claims">The claims to include in the token. Must not be null.</param>
		/// <returns>A tuple containing the serialized token string and the JwtSecurityToken object.</returns>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="claims"/> is null.</exception>
#pragma warning disable 1998
		public async ValueTask<(string TokenString, JwtSecurityToken Token)> BuildTokenAsync(IEnumerable<Claim> claims)
		{
			if (claims == null)
				throw new ArgumentNullException(nameof(claims));

			var claimsList = claims.ToList();

			// Add token_refresh_after claim if not already present
			if (!claimsList.Any(c => c.Type == TokenRefreshAfterClaimType))
			{
				DateTime tokenRefreshAfterDateTime = DateTime.UtcNow.AddMinutes(_options.TokenRefreshMinutes);
				claimsList.Add(new Claim(type: TokenRefreshAfterClaimType, value: tokenRefreshAfterDateTime.ToBinary().ToString()));
			}

			// Sign with HMAC SHA-256 using standard "HS256" algorithm identifier.
			// Must match BuildTokenAsync(ErpUser) above for consistency.
			var securityKey = new SymmetricSecurityKey(GetSigningKeyBytes());
			var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

			// Create token with configured issuer, audience, and expiry
			// NOTE: expires uses DateTime.Now (local time) — consistent with ErpUser overload
			var tokenDescriptor = new JwtSecurityToken(
				_options.Issuer,
				_options.Audience,
				claimsList,
				expires: DateTime.Now.AddMinutes(_options.TokenExpiryMinutes),
				signingCredentials: credentials);

			return (new JwtSecurityTokenHandler().WriteToken(tokenDescriptor), tokenDescriptor);
		}
#pragma warning restore 1998

		/// <summary>
		/// Validates a JWT token string and returns the parsed JwtSecurityToken if valid.
		/// Returns null if the token is invalid, expired, or any exception occurs during validation.
		///
		/// Extracted from AuthService.GetValidSecurityTokenAsync — source lines 120-143.
		/// Validation parameters are preserved exactly from the original source:
		/// - ValidateIssuerSigningKey = true
		/// - ValidateIssuer = true
		/// - ValidateAudience = true
		/// - IssuerSigningKey = SymmetricSecurityKey from configured Key
		/// </summary>
		/// <param name="token">The JWT token string to validate.</param>
		/// <returns>The validated JwtSecurityToken, or null if validation fails.</returns>
#pragma warning disable 1998
		public virtual async ValueTask<JwtSecurityToken> GetValidSecurityTokenAsync(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return null;

			var mySecret = GetSigningKeyBytes();
			var mySecurityKey = new SymmetricSecurityKey(mySecret);
			var tokenHandler = new JwtSecurityTokenHandler();
			try
			{
				tokenHandler.ValidateToken(token,
				new TokenValidationParameters
				{
					ValidateIssuerSigningKey = true,
					ValidateIssuer = true,
					ValidateAudience = true,
					ValidIssuer = _options.Issuer,
					ValidAudience = _options.Audience,
					IssuerSigningKey = mySecurityKey,
				}, out SecurityToken validatedToken);
				return validatedToken as JwtSecurityToken;
			}
			catch (Exception)
			{
				return null;
			}
		}
#pragma warning restore 1998

		/// <summary>
		/// Refreshes an existing JWT token by validating it and issuing a new token for the
		/// provided user. Unlike the monolith's AuthService.GetNewTokenAsync (lines 94-117),
		/// this method does NOT perform SecurityManager.GetUser() database lookups — the caller
		/// must provide the already-resolved ErpUser to maintain the separation between the
		/// shared kernel (no database access) and the service layer (handles user resolution).
		///
		/// Returns the new token string if the existing token is valid and the user is enabled,
		/// or null if validation fails or the user is not eligible for token refresh.
		/// </summary>
		/// <param name="existingTokenString">The current JWT token string to refresh.</param>
		/// <param name="currentUser">The resolved ErpUser for the token's subject. May be null.</param>
		/// <returns>The new JWT token string, or null if refresh is not possible.</returns>
#pragma warning disable 1998
		public async ValueTask<string> RefreshTokenAsync(string existingTokenString, ErpUser currentUser)
		{
			// Validate the existing token
			JwtSecurityToken jwtToken = await GetValidSecurityTokenAsync(existingTokenString);
			if (jwtToken == null)
				return null;

			// Verify the user is provided and enabled
			if (currentUser == null || !currentUser.Enabled)
				return null;

			// Build a fresh token for the current user
			var (newTokenString, _) = await BuildTokenAsync(currentUser);
			return newTokenString;
		}
#pragma warning restore 1998

		/// <summary>
		/// Extracts the user ID (Guid) from a validated JwtSecurityToken by reading
		/// the ClaimTypes.NameIdentifier claim. Returns null if the claim is missing
		/// or cannot be parsed as a Guid.
		///
		/// Useful for services that need to identify the user from a validated token
		/// without performing a full user resolution (database lookup).
		/// </summary>
		/// <param name="token">The validated JwtSecurityToken to extract the user ID from.</param>
		/// <returns>The user's Guid, or null if extraction fails.</returns>
		public static Guid? ExtractUserIdFromToken(JwtSecurityToken token)
		{
			if (token == null)
				return null;

			var nameIdentifierClaim = token.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier);
			if (nameIdentifierClaim == null)
				return null;

			if (Guid.TryParse(nameIdentifierClaim.Value, out Guid userId))
				return userId;

			return null;
		}

		/// <summary>
		/// Determines whether a validated JWT token requires proactive refresh based on
		/// the token_refresh_after claim. The claim stores a DateTime as a binary string
		/// (DateTime.ToBinary().ToString()) and this method parses it back to compare
		/// against the current UTC time.
		///
		/// Returns true if the current time exceeds the refresh-after threshold, indicating
		/// that the middleware or service layer should issue a new token to prevent
		/// expiration during an active session.
		///
		/// Preserves the refresh-after logic from the monolith's JwtMiddleware pattern.
		/// </summary>
		/// <param name="token">The validated JwtSecurityToken to check.</param>
		/// <returns>True if the token should be refreshed; false if it is still within the refresh window or if the claim is missing/unparseable.</returns>
		public static bool IsTokenRefreshRequired(JwtSecurityToken token)
		{
			if (token == null)
				return false;

			var refreshClaim = token.Claims.FirstOrDefault(x => x.Type == TokenRefreshAfterClaimType);
			if (refreshClaim == null)
				return false;

			try
			{
				long binaryDate = long.Parse(refreshClaim.Value);
				DateTime refreshAfterDateTime = DateTime.FromBinary(binaryDate);
				return DateTime.UtcNow > refreshAfterDateTime;
			}
			catch (Exception)
			{
				// If the claim value cannot be parsed, do not require refresh
				return false;
			}
		}
	}
}
