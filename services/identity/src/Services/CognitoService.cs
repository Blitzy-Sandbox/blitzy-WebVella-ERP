using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.Identity.DataAccess;
using WebVellaErp.Identity.Models;

namespace WebVellaErp.Identity.Services
{
    /// <summary>
    /// Result type returned from token-based authentication operations.
    /// Replaces the raw JWT string from <c>AuthService.BuildTokenAsync()</c> with structured Cognito tokens.
    /// </summary>
    public class AuthTokenResult
    {
        /// <summary>Cognito ID token containing user claims (name, email, groups).</summary>
        public string IdToken { get; set; } = string.Empty;

        /// <summary>Cognito access token for authorizing API calls.</summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>Cognito refresh token for obtaining new tokens without re-authentication.</summary>
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>Token expiry in seconds (typically 3600 for Cognito).</summary>
        public int ExpiresIn { get; set; }

        /// <summary>Token type — always "Bearer".</summary>
        public string TokenType { get; set; } = "Bearer";
    }

    /// <summary>
    /// Defines the contract for AWS Cognito user pool operations for the Identity and Access Management microservice.
    /// Replaces:
    /// <list type="bullet">
    ///   <item><c>AuthService</c> — Cookie + JWT authentication (login/logout/token)</item>
    ///   <item><c>SecurityManager</c> — User credential validation, user/role CRUD</item>
    ///   <item><c>JwtMiddleware</c> — JWT token validation middleware</item>
    ///   <item><c>PasswordUtil</c> — MD5 password hashing for legacy migration</item>
    /// </list>
    /// </summary>
    public interface ICognitoService
    {
        /// <summary>
        /// Authenticates a user via email and password against the Cognito user pool.
        /// Replaces <c>AuthService.Authenticate(email, password)</c>.
        /// Returns the authenticated <see cref="User"/> or null on failure.
        /// </summary>
        Task<User?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken = default);

        /// <summary>
        /// Signs out a user globally from all Cognito sessions.
        /// Replaces <c>AuthService.Logout()</c> which called <c>HttpContext.SignOutAsync(CookieAuthenticationDefaults)</c>.
        /// </summary>
        Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Authenticates and returns the full token result (IdToken, AccessToken, RefreshToken).
        /// Replaces <c>AuthService.GetTokenAsync(email, password)</c>.
        /// </summary>
        Task<AuthTokenResult> GetTokenAsync(string email, string password, CancellationToken cancellationToken = default);

        /// <summary>
        /// Refreshes tokens using a Cognito refresh token.
        /// Replaces <c>AuthService.GetNewTokenAsync(tokenString)</c>.
        /// </summary>
        Task<AuthTokenResult?> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves the authenticated user from a Cognito access token.
        /// Replaces <c>AuthService.GetUser(ClaimsPrincipal)</c> and <c>JwtMiddleware.Invoke()</c>.
        /// </summary>
        Task<User?> GetUserFromTokenAsync(string accessToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new user in Cognito and DynamoDB.
        /// Replaces the CREATE path of <c>SecurityManager.SaveUser(ErpUser)</c>.
        /// </summary>
        Task<User> CreateUserAsync(User user, string password, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates an existing user in Cognito and DynamoDB.
        /// Replaces the UPDATE path of <c>SecurityManager.SaveUser(ErpUser)</c>.
        /// </summary>
        Task<User> UpdateUserAsync(User user, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets or resets a user's password in Cognito via AdminSetUserPassword.
        /// </summary>
        Task SetUserPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

        /// <summary>
        /// Enables a user account in Cognito.
        /// Replaces user enable logic in <c>SecurityManager.SaveUser()</c>.
        /// </summary>
        Task EnableUserAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disables a user account in Cognito.
        /// </summary>
        Task DisableUserAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists users from the Cognito user pool with pagination.
        /// Replaces <c>SecurityManager.GetUsers()</c>.
        /// </summary>
        Task<List<User>> ListUsersAsync(int limit = 50, string? paginationToken = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds a user to a Cognito group (role assignment).
        /// Maps to AdminAddUserToGroup — well-known role groups: administrator, regular, guest.
        /// </summary>
        Task AddUserToGroupAsync(string username, string groupName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes a user from a Cognito group (role removal).
        /// Maps to AdminRemoveUserFromGroup.
        /// </summary>
        Task RemoveUserFromGroupAsync(string username, string groupName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Seeds default system user and role groups in Cognito.
        /// Creates erp@webvella.com (FirstUserId), system@webvella.com (SystemUserId),
        /// and administrator/regular/guest groups.
        /// </summary>
        Task SeedDefaultSystemUserAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Migrates a user from legacy MD5 password authentication to Cognito.
        /// Implements User Migration Lambda Trigger logic per AAP Section 0.7.5.
        /// </summary>
        Task<bool> MigrateUserPasswordAsync(string email, string password, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Production implementation of <see cref="ICognitoService"/> backed by AWS Cognito user pools.
    /// <para>
    /// Replaces the following monolith components:
    /// <list type="bullet">
    ///   <item><c>AuthService.cs</c> (lines 1-169) — Cookie + JWT authentication</item>
    ///   <item><c>SecurityManager.cs</c> (lines 1-371) — User credential validation, CRUD</item>
    ///   <item><c>JwtMiddleware.cs</c> (lines 1-67) — JWT token validation middleware</item>
    ///   <item><c>PasswordUtil.cs</c> (lines 1-35) — MD5 password hashing for migration</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Configuration:</strong>
    /// COGNITO_USER_POOL_ID — environment variable (required).
    /// COGNITO_CLIENT_ID — environment variable (required).
    /// COGNITO_CLIENT_SECRET — SSM Parameter Store SecureString (NEVER environment variable).
    /// AWS_REGION — environment variable (default us-east-1).
    /// IS_LOCAL — environment variable (true for LocalStack).
    /// </para>
    /// </summary>
    public class CognitoService : ICognitoService
    {
        // ────────────────────────────────────────────────────────────────
        //  Dependencies and Configuration
        // ────────────────────────────────────────────────────────────────

        private readonly IAmazonCognitoIdentityProvider _cognitoClient;
        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly IUserRepository _userRepository;
        private readonly ILogger<CognitoService> _logger;

        private readonly string _userPoolId;
        private readonly string _clientId;
        private readonly string _region;
        private readonly bool _isLocal;

        /// <summary>
        /// Cached Cognito client secret retrieved from SSM Parameter Store.
        /// Thread-safe access via <see cref="_secretLock"/>.
        /// </summary>
        private string? _cachedClientSecret;
        private readonly SemaphoreSlim _secretLock = new SemaphoreSlim(1, 1);

        /// <summary>SSM parameter path for the Cognito app client secret.</summary>
        private const string CognitoClientSecretSsmPath = "/identity/cognito-client-secret";

        /// <summary>
        /// Initialises a new <see cref="CognitoService"/> instance.
        /// All dependencies are injected via DI — zero direct instantiation patterns.
        /// </summary>
        /// <param name="cognitoClient">AWS Cognito Identity Provider client.</param>
        /// <param name="ssmClient">AWS Systems Manager client for secret retrieval.</param>
        /// <param name="userRepository">DynamoDB-backed user/role persistence.</param>
        /// <param name="logger">Structured logger for audit trail and diagnostics.</param>
        public CognitoService(
            IAmazonCognitoIdentityProvider cognitoClient,
            IAmazonSimpleSystemsManagement ssmClient,
            IUserRepository userRepository,
            ILogger<CognitoService> logger)
        {
            _cognitoClient = cognitoClient ?? throw new ArgumentNullException(nameof(cognitoClient));
            _ssmClient = ssmClient ?? throw new ArgumentNullException(nameof(ssmClient));
            _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")
                ?? throw new InvalidOperationException("COGNITO_USER_POOL_ID environment variable is required.");
            _clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                ?? throw new InvalidOperationException("COGNITO_CLIENT_ID environment variable is required.");
            _region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "us-east-1";
            _isLocal = string.Equals(
                Environment.GetEnvironmentVariable("IS_LOCAL"), "true", StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────────────────────────────────────────────
        //  Authentication Methods
        // ────────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<User?> AuthenticateAsync(
            string email, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Authentication attempted with empty email or password");
                return null;
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            _logger.LogInformation("Authentication attempt for user {Email}", normalizedEmail);

            try
            {
                var authParams = new Dictionary<string, string>
                {
                    ["USERNAME"] = normalizedEmail,
                    ["PASSWORD"] = password
                };

                // Include SECRET_HASH if client secret is configured (from SSM, NEVER env var)
                var clientSecret = await GetCognitoClientSecretAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(clientSecret))
                {
                    authParams["SECRET_HASH"] = ComputeSecretHash(normalizedEmail, _clientId, clientSecret);
                }

                var authRequest = new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                    AuthParameters = authParams
                };

                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(authRequest, cancellationToken)
                    .ConfigureAwait(false);

                // Extract the final authentication result, handling NEW_PASSWORD_REQUIRED challenge
                AuthenticationResultType? authResult = authResponse.AuthenticationResult;

                if (authResponse.ChallengeName == ChallengeNameType.NEW_PASSWORD_REQUIRED)
                {
                    _logger.LogInformation(
                        "User {Email} requires password change (NEW_PASSWORD_REQUIRED challenge)",
                        normalizedEmail);
                    authResult = await RespondToNewPasswordChallengeAsync(
                        authResponse.Session, normalizedEmail, password, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (authResult == null)
                {
                    _logger.LogWarning(
                        "Authentication failed for {Email} — no AuthenticationResult returned", normalizedEmail);
                    return null;
                }

                // Retrieve user details from the access token
                var user = await GetUserFromTokenAsync(authResult.AccessToken, cancellationToken)
                    .ConfigureAwait(false);

                if (user != null)
                {
                    // Update last login time in DynamoDB (replaces SecurityManager.UpdateUserLastLoginTime)
                    await _userRepository.UpdateLastLoginTimeAsync(user.Id, cancellationToken)
                        .ConfigureAwait(false);
                    user.LastLoggedIn = DateTime.UtcNow;
                    _logger.LogInformation("User {Email} authenticated successfully", normalizedEmail);
                }

                return user;
            }
            catch (NotAuthorizedException ex)
            {
                _logger.LogWarning(ex, "Authentication failed for {Email} — invalid credentials", normalizedEmail);
                return null;
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "Authentication failed for {Email} — user not found", normalizedEmail);
                return null;
            }
            catch (UserNotConfirmedException ex)
            {
                _logger.LogWarning(ex, "Authentication failed for {Email} — user not confirmed", normalizedEmail);
                return null;
            }
            catch (TooManyRequestsException ex)
            {
                _logger.LogError(ex, "Rate limit exceeded during authentication for {Email}", normalizedEmail);
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during authentication for {Email}", normalizedEmail);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ArgumentException("Access token is required for logout.", nameof(accessToken));
            }

            try
            {
                // Get the username from the access token to identify the user for global sign-out
                var getUserRequest = new GetUserRequest { AccessToken = accessToken };
                var getUserResponse = await _cognitoClient.GetUserAsync(getUserRequest, cancellationToken)
                    .ConfigureAwait(false);

                var username = getUserResponse.Username;

                // Global sign-out — invalidates all tokens for the user
                var signOutRequest = new AdminUserGlobalSignOutRequest
                {
                    UserPoolId = _userPoolId,
                    Username = username
                };

                await _cognitoClient.AdminUserGlobalSignOutAsync(signOutRequest, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("User {Username} signed out globally", username);
            }
            catch (NotAuthorizedException ex)
            {
                _logger.LogWarning(ex, "Logout failed — access token invalid or expired");
                throw;
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "Logout failed — user not found");
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during logout");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AuthTokenResult> GetTokenAsync(
            string email, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("Email and password are required for token retrieval.");
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            _logger.LogInformation("Token request for user {Email}", normalizedEmail);

            try
            {
                var authParams = new Dictionary<string, string>
                {
                    ["USERNAME"] = normalizedEmail,
                    ["PASSWORD"] = password.Trim()
                };

                var clientSecret = await GetCognitoClientSecretAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(clientSecret))
                {
                    authParams["SECRET_HASH"] = ComputeSecretHash(normalizedEmail, _clientId, clientSecret);
                }

                var authRequest = new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                    AuthParameters = authParams
                };

                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(authRequest, cancellationToken)
                    .ConfigureAwait(false);

                // Handle NEW_PASSWORD_REQUIRED challenge for migrated users
                AuthenticationResultType? authResult = authResponse.AuthenticationResult;

                if (authResponse.ChallengeName == ChallengeNameType.NEW_PASSWORD_REQUIRED)
                {
                    authResult = await RespondToNewPasswordChallengeAsync(
                        authResponse.Session, normalizedEmail, password.Trim(), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (authResult == null)
                {
                    throw new InvalidOperationException(
                        $"Cognito did not return an AuthenticationResult for user {normalizedEmail}.");
                }

                // Update last login time in DynamoDB
                var user = await GetUserFromTokenAsync(authResult.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                if (user != null)
                {
                    await _userRepository.UpdateLastLoginTimeAsync(user.Id, cancellationToken)
                        .ConfigureAwait(false);
                }

                _logger.LogInformation("Token issued successfully for user {Email}", normalizedEmail);
                return MapToAuthTokenResult(authResult);
            }
            catch (NotAuthorizedException ex)
            {
                _logger.LogWarning(ex, "Token request failed for {Email} — invalid credentials", normalizedEmail);
                throw;
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "Token request failed for {Email} — user not found", normalizedEmail);
                throw;
            }
            catch (UserNotConfirmedException ex)
            {
                _logger.LogWarning(ex, "Token request failed for {Email} — user not confirmed", normalizedEmail);
                throw;
            }
            catch (TooManyRequestsException ex)
            {
                _logger.LogError(ex, "Rate limit exceeded during token request for {Email}", normalizedEmail);
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during token request for {Email}", normalizedEmail);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AuthTokenResult?> RefreshTokenAsync(
            string refreshToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogWarning("Refresh token attempt with empty token");
                return null;
            }

            _logger.LogInformation("Token refresh attempt");

            try
            {
                var authParams = new Dictionary<string, string>
                {
                    ["REFRESH_TOKEN"] = refreshToken
                };

                var authRequest = new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.REFRESH_TOKEN_AUTH,
                    AuthParameters = authParams
                };

                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(authRequest, cancellationToken)
                    .ConfigureAwait(false);

                if (authResponse.AuthenticationResult == null)
                {
                    _logger.LogWarning("Token refresh failed — no AuthenticationResult returned");
                    return null;
                }

                _logger.LogInformation("Token refreshed successfully");

                var result = MapToAuthTokenResult(authResponse.AuthenticationResult);
                // Cognito does not return a new refresh token on refresh — preserve the original
                if (string.IsNullOrEmpty(result.RefreshToken))
                {
                    result.RefreshToken = refreshToken;
                }

                return result;
            }
            catch (NotAuthorizedException ex)
            {
                _logger.LogWarning(ex, "Token refresh failed — token invalid or expired");
                return null;
            }
            catch (TooManyRequestsException ex)
            {
                _logger.LogError(ex, "Rate limit exceeded during token refresh");
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during token refresh");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<User?> GetUserFromTokenAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            try
            {
                var request = new GetUserRequest { AccessToken = accessToken };
                var response = await _cognitoClient.GetUserAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                return await MapCognitoUserToUserAsync(
                    response, cancellationToken).ConfigureAwait(false);
            }
            catch (NotAuthorizedException ex)
            {
                _logger.LogWarning(ex, "GetUserFromToken failed — access token invalid or expired");
                return null;
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "GetUserFromToken failed — user not found");
                return null;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during GetUserFromToken");
                throw;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  User Management Methods
        // ────────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<User> CreateUserAsync(
            User user, string password, CancellationToken cancellationToken = default)
        {
            // Preserve validation logic from SecurityManager.SaveUser() create path (lines 267-286)
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            if (string.IsNullOrWhiteSpace(user.Username))
            {
                throw new ArgumentException("Username is required.", nameof(user));
            }

            // Username uniqueness check (SecurityManager line 269-270)
            var existingByUsername = await _userRepository.GetUserByUsernameAsync(
                user.Username, cancellationToken).ConfigureAwait(false);
            if (existingByUsername != null)
            {
                throw new ArgumentException(
                    $"Username '{user.Username}' is already taken.", nameof(user));
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                throw new ArgumentException("Email is required.", nameof(user));
            }

            // Email uniqueness check (SecurityManager line 274-275)
            var existingByEmail = await _userRepository.GetUserByEmailAsync(
                user.Email.Trim().ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
            if (existingByEmail != null)
            {
                throw new ArgumentException(
                    $"Email '{user.Email}' is already in use.", nameof(user));
            }

            // Email format validation (SecurityManager line 276-277)
            if (!IsValidEmail(user.Email))
            {
                throw new ArgumentException(
                    $"Email '{user.Email}' is not a valid email address.", nameof(user));
            }

            // Password required (SecurityManager line 279-280)
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("Password is required.", nameof(password));
            }

            var normalizedEmail = user.Email.Trim().ToLowerInvariant();
            _logger.LogInformation("Creating user {Username} ({Email})", user.Username, normalizedEmail);

            try
            {
                // Assign ID if not set
                if (user.Id == Guid.Empty)
                {
                    user.Id = Guid.NewGuid();
                }

                // Create user in Cognito with suppressed invitation message
                var createRequest = new AdminCreateUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = normalizedEmail,
                    MessageAction = "SUPPRESS",
                    UserAttributes = new List<AttributeType>
                    {
                        new AttributeType { Name = "email", Value = normalizedEmail },
                        new AttributeType { Name = "email_verified", Value = "true" },
                        new AttributeType { Name = "custom:erp_user_id", Value = user.Id.ToString() },
                        new AttributeType { Name = "given_name", Value = user.FirstName ?? string.Empty },
                        new AttributeType { Name = "family_name", Value = user.LastName ?? string.Empty },
                        new AttributeType { Name = "preferred_username", Value = user.Username }
                    }
                };

                var createResponse = await _cognitoClient.AdminCreateUserAsync(
                    createRequest, cancellationToken).ConfigureAwait(false);

                // Set permanent password (avoids FORCE_CHANGE_PASSWORD state)
                await _cognitoClient.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
                {
                    UserPoolId = _userPoolId,
                    Username = normalizedEmail,
                    Password = password,
                    Permanent = true
                }, cancellationToken).ConfigureAwait(false);

                // Store Cognito sub attribute
                user.CognitoSub = createResponse.User?.Attributes?
                    .FirstOrDefault(a => a.Name == "sub")?.Value;

                // Disable user in Cognito if Enabled is false
                if (!user.Enabled)
                {
                    await _cognitoClient.AdminDisableUserAsync(new AdminDisableUserRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = normalizedEmail
                    }, cancellationToken).ConfigureAwait(false);
                }

                // Assign roles via Cognito groups (replaces $user_role.id from SecurityManager line 284)
                if (user.Roles.Any())
                {
                    foreach (var role in user.Roles)
                    {
                        var groupName = role.CognitoGroupName ?? role.Name.ToLowerInvariant();
                        await AddUserToGroupSafeAsync(normalizedEmail, groupName, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                // Set metadata timestamps and normalize fields
                user.CreatedOn = DateTime.UtcNow;
                user.Email = normalizedEmail;
                user.Verified = true;
                user.EmailVerified = true;

                // Persist extended user data to DynamoDB
                await _userRepository.SaveUserAsync(user, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "User {Username} ({Email}) created successfully with ID {UserId}",
                    user.Username, normalizedEmail, user.Id);

                return user;
            }
            catch (UsernameExistsException ex)
            {
                _logger.LogWarning(ex,
                    "User creation failed — username already exists in Cognito: {Email}", normalizedEmail);
                throw new ArgumentException(
                    $"User with email '{normalizedEmail}' already exists in Cognito.", ex);
            }
            catch (InvalidPasswordException ex)
            {
                _logger.LogWarning(ex,
                    "User creation failed — password does not meet Cognito policy for {Email}", normalizedEmail);
                throw new ArgumentException(
                    "Password does not meet the password policy requirements.", ex);
            }
            catch (TooManyRequestsException ex)
            {
                _logger.LogError(ex, "Rate limit exceeded during user creation for {Email}", normalizedEmail);
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during user creation for {Email}", normalizedEmail);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<User> UpdateUserAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            _logger.LogInformation("Updating user {UserId}", user.Id);

            try
            {
                // Retrieve existing user to detect changes (SecurityManager.SaveUser update path)
                var existingUser = await _userRepository.GetUserByIdAsync(user.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (existingUser == null)
                {
                    throw new ArgumentException($"User with ID '{user.Id}' not found.", nameof(user));
                }

                var cognitoUsername = existingUser.Email.Trim().ToLowerInvariant();

                // Validate username change (SecurityManager.SaveUser lines 206-213)
                if (!string.Equals(user.Username, existingUser.Username, StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(user.Username))
                    {
                        throw new ArgumentException("Username is required.", nameof(user));
                    }

                    var usernameCheck = await _userRepository.GetUserByUsernameAsync(
                        user.Username, cancellationToken).ConfigureAwait(false);
                    if (usernameCheck != null && usernameCheck.Id != user.Id)
                    {
                        throw new ArgumentException(
                            $"Username '{user.Username}' is already taken.", nameof(user));
                    }
                }

                // Validate email change (SecurityManager.SaveUser lines 216-225)
                var normalizedNewEmail = user.Email?.Trim().ToLowerInvariant() ?? string.Empty;
                var emailChanged = !string.Equals(
                    normalizedNewEmail, existingUser.Email.Trim().ToLowerInvariant(),
                    StringComparison.Ordinal);

                if (emailChanged)
                {
                    if (string.IsNullOrWhiteSpace(normalizedNewEmail))
                    {
                        throw new ArgumentException("Email is required.", nameof(user));
                    }

                    var emailCheck = await _userRepository.GetUserByEmailAsync(
                        normalizedNewEmail, cancellationToken).ConfigureAwait(false);
                    if (emailCheck != null && emailCheck.Id != user.Id)
                    {
                        throw new ArgumentException(
                            $"Email '{normalizedNewEmail}' is already in use.", nameof(user));
                    }

                    if (!IsValidEmail(normalizedNewEmail))
                    {
                        throw new ArgumentException(
                            $"Email '{normalizedNewEmail}' is not a valid email address.", nameof(user));
                    }
                }

                // Build Cognito attribute update list
                var attributesToUpdate = new List<AttributeType>
                {
                    new AttributeType { Name = "given_name", Value = user.FirstName ?? string.Empty },
                    new AttributeType { Name = "family_name", Value = user.LastName ?? string.Empty },
                    new AttributeType { Name = "preferred_username", Value = user.Username ?? string.Empty }
                };

                if (emailChanged)
                {
                    attributesToUpdate.Add(new AttributeType { Name = "email", Value = normalizedNewEmail });
                    attributesToUpdate.Add(new AttributeType { Name = "email_verified", Value = "true" });
                }

                await _cognitoClient.AdminUpdateUserAttributesAsync(
                    new AdminUpdateUserAttributesRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = cognitoUsername,
                        UserAttributes = attributesToUpdate
                    }, cancellationToken).ConfigureAwait(false);

                // Handle password change (SecurityManager.SaveUser line 228)
                if (!string.IsNullOrWhiteSpace(user.Password))
                {
                    await _cognitoClient.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = cognitoUsername,
                        Password = user.Password,
                        Permanent = true
                    }, cancellationToken).ConfigureAwait(false);
                }

                // Handle enabled/disabled state change (SecurityManager.SaveUser lines 232-234)
                if (user.Enabled != existingUser.Enabled)
                {
                    if (user.Enabled)
                    {
                        await _cognitoClient.AdminEnableUserAsync(new AdminEnableUserRequest
                        {
                            UserPoolId = _userPoolId,
                            Username = cognitoUsername
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _cognitoClient.AdminDisableUserAsync(new AdminDisableUserRequest
                        {
                            UserPoolId = _userPoolId,
                            Username = cognitoUsername
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Sync role assignments — reconcile Cognito groups (replaces $user_role.id update)
                var existingRoleIds = existingUser.Roles.Select(r => r.Id).ToList();
                var newRoleIds = user.Roles.Select(r => r.Id).ToList();

                // Remove old roles no longer assigned
                var rolesToRemove = existingUser.Roles
                    .Where(r => !newRoleIds.Contains(r.Id)).ToList();
                foreach (var role in rolesToRemove)
                {
                    var groupName = role.CognitoGroupName ?? role.Name.ToLowerInvariant();
                    try
                    {
                        await RemoveUserFromGroupAsync(cognitoUsername, groupName, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (AmazonCognitoIdentityProviderException ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to remove user {Username} from group {GroupName}",
                            cognitoUsername, groupName);
                    }
                }

                // Add newly assigned roles
                var rolesToAdd = user.Roles
                    .Where(r => !existingRoleIds.Contains(r.Id)).ToList();
                foreach (var role in rolesToAdd)
                {
                    var groupName = role.CognitoGroupName ?? role.Name.ToLowerInvariant();
                    await AddUserToGroupSafeAsync(cognitoUsername, groupName, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Normalize email for storage
                user.Email = emailChanged ? normalizedNewEmail : existingUser.Email;

                // Preserve creation timestamp and Cognito sub from existing user
                user.CreatedOn = existingUser.CreatedOn;
                user.CognitoSub = existingUser.CognitoSub;

                // Persist to DynamoDB
                await _userRepository.SaveUserAsync(user, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("User {UserId} updated successfully", user.Id);
                return user;
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex,
                    "User update failed — user not found in Cognito for {UserId}", user.Id);
                throw new ArgumentException($"User with ID '{user.Id}' not found in Cognito.", ex);
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during user update for {UserId}", user.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task SetUserPasswordAsync(
            Guid userId, string newPassword, CancellationToken cancellationToken = default)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentException("User ID cannot be empty.", nameof(userId));
            }

            if (string.IsNullOrWhiteSpace(newPassword))
            {
                throw new ArgumentException("Password cannot be empty.", nameof(newPassword));
            }

            _logger.LogInformation("Setting password for user {UserId}", userId);

            var existingUser = await _userRepository.GetUserByIdAsync(userId, cancellationToken)
                .ConfigureAwait(false);
            if (existingUser == null)
            {
                throw new ArgumentException($"User with ID '{userId}' not found.", nameof(userId));
            }

            var cognitoUsername = existingUser.Email.Trim().ToLowerInvariant();

            try
            {
                await _cognitoClient.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
                {
                    UserPoolId = _userPoolId,
                    Username = cognitoUsername,
                    Password = newPassword,
                    Permanent = true
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Password set successfully for user {UserId}", userId);
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "SetUserPassword failed — user not found in Cognito for {UserId}", userId);
                throw new ArgumentException($"User with ID '{userId}' not found in Cognito.", ex);
            }
            catch (InvalidPasswordException ex)
            {
                _logger.LogWarning(ex, "SetUserPassword failed — invalid password for {UserId}", userId);
                throw new ArgumentException("The provided password does not meet the password policy.", ex);
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during SetUserPassword for {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task EnableUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentException("User ID cannot be empty.", nameof(userId));
            }

            _logger.LogInformation("Enabling user {UserId}", userId);

            var existingUser = await _userRepository.GetUserByIdAsync(userId, cancellationToken)
                .ConfigureAwait(false);
            if (existingUser == null)
            {
                throw new ArgumentException($"User with ID '{userId}' not found.", nameof(userId));
            }

            var cognitoUsername = existingUser.Email.Trim().ToLowerInvariant();

            try
            {
                await _cognitoClient.AdminEnableUserAsync(new AdminEnableUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = cognitoUsername
                }, cancellationToken).ConfigureAwait(false);

                existingUser.Enabled = true;
                await _userRepository.SaveUserAsync(existingUser, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("User {UserId} enabled successfully", userId);
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "EnableUser failed — user not found in Cognito for {UserId}", userId);
                throw new ArgumentException($"User with ID '{userId}' not found in Cognito.", ex);
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during EnableUser for {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DisableUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentException("User ID cannot be empty.", nameof(userId));
            }

            _logger.LogInformation("Disabling user {UserId}", userId);

            var existingUser = await _userRepository.GetUserByIdAsync(userId, cancellationToken)
                .ConfigureAwait(false);
            if (existingUser == null)
            {
                throw new ArgumentException($"User with ID '{userId}' not found.", nameof(userId));
            }

            var cognitoUsername = existingUser.Email.Trim().ToLowerInvariant();

            try
            {
                await _cognitoClient.AdminDisableUserAsync(new AdminDisableUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = cognitoUsername
                }, cancellationToken).ConfigureAwait(false);

                existingUser.Enabled = false;
                await _userRepository.SaveUserAsync(existingUser, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("User {UserId} disabled successfully", userId);
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "DisableUser failed — user not found in Cognito for {UserId}", userId);
                throw new ArgumentException($"User with ID '{userId}' not found in Cognito.", ex);
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during DisableUser for {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<User>> ListUsersAsync(
            int limit = 50, string? paginationToken = null, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Listing users (limit={Limit}, hasPaginationToken={HasToken})",
                limit, paginationToken != null);

            try
            {
                var request = new ListUsersRequest
                {
                    UserPoolId = _userPoolId,
                    Limit = limit
                };

                if (!string.IsNullOrWhiteSpace(paginationToken))
                {
                    request.PaginationToken = paginationToken;
                }

                var response = await _cognitoClient.ListUsersAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                var users = new List<User>();
                foreach (var cognitoUser in response.Users)
                {
                    var user = MapCognitoUserTypeToUser(cognitoUser);
                    if (user != null)
                    {
                        // Enrich with DynamoDB data (roles, preferences, extended attributes)
                        var dbUser = await _userRepository.GetUserByIdAsync(
                            user.Id, cancellationToken).ConfigureAwait(false);
                        if (dbUser != null)
                        {
                            user.Roles = dbUser.Roles;
                            user.Image = dbUser.Image;
                            user.CreatedOn = dbUser.CreatedOn;
                            user.LastLoggedIn = dbUser.LastLoggedIn;
                        }
                        else
                        {
                            // User in Cognito but not yet persisted to DynamoDB — load roles from repo
                            var roles = await _userRepository.GetUserRolesAsync(
                                user.Id, cancellationToken).ConfigureAwait(false);
                            user.Roles = roles;
                        }

                        users.Add(user);
                    }
                }

                _logger.LogInformation("Listed {Count} users", users.Count);
                return users;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during ListUsers");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task AddUserToGroupAsync(
            string username, string groupName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("Username cannot be empty.", nameof(username));
            }

            if (string.IsNullOrWhiteSpace(groupName))
            {
                throw new ArgumentException("Group name cannot be empty.", nameof(groupName));
            }

            _logger.LogInformation("Adding user {Username} to group {GroupName}", username, groupName);

            try
            {
                await _cognitoClient.AdminAddUserToGroupAsync(
                    new AdminAddUserToGroupRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = username,
                        GroupName = groupName
                    }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("User {Username} added to group {GroupName}", username, groupName);
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "AddUserToGroup failed — user {Username} not found", username);
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex,
                    "Cognito error adding user {Username} to group {GroupName}", username, groupName);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task RemoveUserFromGroupAsync(
            string username, string groupName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("Username cannot be empty.", nameof(username));
            }

            if (string.IsNullOrWhiteSpace(groupName))
            {
                throw new ArgumentException("Group name cannot be empty.", nameof(groupName));
            }

            _logger.LogInformation("Removing user {Username} from group {GroupName}", username, groupName);

            try
            {
                await _cognitoClient.AdminRemoveUserFromGroupAsync(
                    new AdminRemoveUserFromGroupRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = username,
                        GroupName = groupName
                    }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("User {Username} removed from group {GroupName}", username, groupName);
            }
            catch (UserNotFoundException ex)
            {
                _logger.LogWarning(ex, "RemoveUserFromGroup failed — user {Username} not found", username);
                throw;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex,
                    "Cognito error removing user {Username} from group {GroupName}", username, groupName);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task SeedDefaultSystemUserAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Seeding default system user and role groups in Cognito");

            // Step 1: Create well-known Cognito groups for the three system roles
            // (AdministratorRoleId → "administrator", RegularRoleId → "regular", GuestRoleId → "guest")
            var systemGroups = new[]
            {
                new { Name = "administrator", Description = "Administrator role group" },
                new { Name = "regular", Description = "Regular user role group" },
                new { Name = "guest", Description = "Guest user role group" }
            };

            foreach (var group in systemGroups)
            {
                try
                {
                    await _cognitoClient.CreateGroupAsync(new CreateGroupRequest
                    {
                        UserPoolId = _userPoolId,
                        GroupName = group.Name,
                        Description = group.Description
                    }, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("Created Cognito group '{GroupName}'", group.Name);
                }
                catch (AmazonCognitoIdentityProviderException ex) when (
                    ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    // Idempotent — group already exists, safe to continue
                    _logger.LogInformation("Cognito group '{GroupName}' already exists", group.Name);
                }
            }

            // Step 2: Seed the default first user: erp@webvella.com / erp
            // (per AAP Section 0.7.5 and SecurityContext.cs system user definition)
            var firstUser = new User
            {
                Id = User.FirstUserId,
                Username = "erp",
                Email = "erp@webvella.com",
                FirstName = "ERP",
                LastName = "Admin",
                Enabled = true,
                Verified = true,
                CreatedOn = DateTime.UtcNow,
                Roles = new List<Role>
                {
                    new Role
                    {
                        Id = Role.AdministratorRoleId,
                        Name = "administrator",
                        CognitoGroupName = "administrator"
                    }
                }
            };

            try
            {
                await CreateUserAsync(firstUser, "erp", cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Default ERP admin user (erp@webvella.com) seeded successfully");
            }
            catch (UsernameExistsException)
            {
                // Idempotent — user already exists, safe to continue
                _logger.LogInformation("Default ERP admin user (erp@webvella.com) already exists");
            }
            catch (ArgumentException ex) when (
                ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                // Caught from our own validation (email/username already taken)
                _logger.LogInformation("Default ERP admin user (erp@webvella.com) already exists");
            }

            // Step 3: Seed system user record (system@webvella.com) in DynamoDB only
            // This user represents the system context (SecurityContext.cs SystemUser) and does NOT
            // authenticate via Cognito — it is used only for internal service-to-service operations.
            var systemUser = new User
            {
                Id = User.SystemUserId,
                Username = "system",
                Email = "system@webvella.com",
                FirstName = "Local",
                LastName = "System",
                Enabled = true,
                Verified = true,
                CreatedOn = DateTime.UtcNow,
                Roles = new List<Role>
                {
                    new Role
                    {
                        Id = Role.AdministratorRoleId,
                        Name = "administrator",
                        CognitoGroupName = "administrator"
                    }
                }
            };

            var existingSystemUser = await _userRepository.GetUserByIdAsync(
                User.SystemUserId, cancellationToken).ConfigureAwait(false);
            if (existingSystemUser == null)
            {
                await _userRepository.SaveUserAsync(systemUser, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("System user record (system@webvella.com) saved to DynamoDB");
            }
            else
            {
                _logger.LogInformation("System user record (system@webvella.com) already exists in DynamoDB");
            }

            _logger.LogInformation("Default system user and role group seeding complete");
        }

        /// <inheritdoc />
        public async Task<bool> MigrateUserPasswordAsync(
            string email, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email cannot be empty.", nameof(email));
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new ArgumentException("Password cannot be empty.", nameof(password));
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            _logger.LogInformation("Attempting MD5 password migration for {Email}", normalizedEmail);

            try
            {
                // Step 1: Look up the user in DynamoDB to find the stored MD5 hash
                var dbUser = await _userRepository.GetUserByEmailAsync(normalizedEmail, cancellationToken)
                    .ConfigureAwait(false);
                if (dbUser == null)
                {
                    _logger.LogWarning("Migration failed — user {Email} not found in DynamoDB", normalizedEmail);
                    return false;
                }

                // Step 2: Compute MD5 hash of the provided plaintext password
                // CRITICAL: Reproduces EXACT logic from PasswordUtil.GetMd5Hash (UTF8 encoding, "x2" hex format)
                var computedHash = ComputeMd5Hash(password);

                // Step 3: Compare hashes — case-insensitive per PasswordUtil.VerifyMd5Hash
                // (source line 28-29: StringComparer.OrdinalIgnoreCase)
                if (string.IsNullOrEmpty(dbUser.Password) ||
                    !string.Equals(computedHash, dbUser.Password, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("MD5 password migration failed — hash mismatch for {Email}", normalizedEmail);
                    return false;
                }

                // Step 4: Create user in Cognito with the plaintext password
                // Cognito will store it securely using SRP-based hashing
                try
                {
                    await _cognitoClient.AdminCreateUserAsync(new AdminCreateUserRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = normalizedEmail,
                        MessageAction = "SUPPRESS",
                        UserAttributes = new List<AttributeType>
                        {
                            new AttributeType { Name = "email", Value = normalizedEmail },
                            new AttributeType { Name = "email_verified", Value = "true" },
                            new AttributeType
                            {
                                Name = "custom:erp_user_id",
                                Value = dbUser.Id.ToString()
                            },
                            new AttributeType { Name = "given_name", Value = dbUser.FirstName ?? string.Empty },
                            new AttributeType { Name = "family_name", Value = dbUser.LastName ?? string.Empty },
                            new AttributeType
                            {
                                Name = "preferred_username",
                                Value = dbUser.Username ?? string.Empty
                            }
                        }
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (UsernameExistsException)
                {
                    // User already exists in Cognito — just set the password
                    _logger.LogInformation(
                        "User {Email} already exists in Cognito during migration; setting password", normalizedEmail);
                }

                // Step 5: Set permanent password so user does not face FORCE_CHANGE_PASSWORD
                await _cognitoClient.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
                {
                    UserPoolId = _userPoolId,
                    Username = normalizedEmail,
                    Password = password,
                    Permanent = true
                }, cancellationToken).ConfigureAwait(false);

                // Step 6: Assign roles as Cognito groups
                if (dbUser.Roles != null && dbUser.Roles.Any())
                {
                    foreach (var role in dbUser.Roles)
                    {
                        var groupName = role.CognitoGroupName ?? role.Name.ToLowerInvariant();
                        await AddUserToGroupSafeAsync(normalizedEmail, groupName, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                // Step 7: Clear the MD5 hash from DynamoDB (no longer needed, Cognito is source of truth)
                dbUser.Password = string.Empty;
                await _userRepository.SaveUserAsync(dbUser, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("MD5 password migration successful for {Email}", normalizedEmail);
                return true;
            }
            catch (AmazonCognitoIdentityProviderException ex)
            {
                _logger.LogError(ex, "Cognito error during MD5 password migration for {Email}", normalizedEmail);
                throw;
            }
        }

        // =====================================================================
        // Private Helper Methods
        // =====================================================================

        /// <summary>
        /// Handles the NEW_PASSWORD_REQUIRED challenge that Cognito returns for users created via
        /// AdminCreateUser who have not yet set a permanent password.
        /// </summary>
        private async Task<AuthenticationResultType?> RespondToNewPasswordChallengeAsync(
            string session,
            string username,
            string newPassword,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Responding to NEW_PASSWORD_REQUIRED challenge for {Username}", username);

            var challengeResponses = new Dictionary<string, string>
            {
                { "USERNAME", username },
                { "NEW_PASSWORD", newPassword }
            };

            // Include SECRET_HASH if the app client has a secret
            var clientSecret = await GetCognitoClientSecretAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(clientSecret))
            {
                challengeResponses["SECRET_HASH"] = ComputeSecretHash(username, _clientId, clientSecret);
            }

            var challengeResponse = await _cognitoClient.AdminRespondToAuthChallengeAsync(
                new AdminRespondToAuthChallengeRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    ChallengeName = ChallengeNameType.NEW_PASSWORD_REQUIRED,
                    Session = session,
                    ChallengeResponses = challengeResponses
                }, cancellationToken).ConfigureAwait(false);

            return challengeResponse.AuthenticationResult;
        }

        /// <summary>
        /// Retrieves the Cognito client secret from AWS SSM Parameter Store SecureString.
        /// Per AAP Section 0.8.6: secrets via SSM, NEVER environment variables.
        /// Caches the result to avoid repeated SSM calls.
        /// </summary>
        private async Task<string> GetCognitoClientSecretAsync(CancellationToken cancellationToken)
        {
            if (_cachedClientSecret != null)
            {
                return _cachedClientSecret;
            }

            await _secretLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring lock
                if (_cachedClientSecret != null)
                {
                    return _cachedClientSecret;
                }

                try
                {
                    var response = await _ssmClient.GetParameterAsync(new GetParameterRequest
                    {
                        Name = CognitoClientSecretSsmPath,
                        WithDecryption = true
                    }, cancellationToken).ConfigureAwait(false);

                    _cachedClientSecret = response.Parameter?.Value ?? string.Empty;
                    _logger.LogInformation(
                        "Retrieved Cognito client secret from SSM Parameter Store");
                }
                catch (Amazon.SimpleSystemsManagement.Model.ParameterNotFoundException)
                {
                    // No client secret configured — this is valid for app clients without a secret
                    _logger.LogInformation(
                        "No Cognito client secret found in SSM at '{Path}'; proceeding without secret",
                        CognitoClientSecretSsmPath);
                    _cachedClientSecret = string.Empty;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to retrieve Cognito client secret from SSM; proceeding without secret");
                    _cachedClientSecret = string.Empty;
                }

                return _cachedClientSecret;
            }
            finally
            {
                _secretLock.Release();
            }
        }

        /// <summary>
        /// Computes the SECRET_HASH required by Cognito app clients that have a configured secret.
        /// SECRET_HASH = Base64(HMAC-SHA256(clientSecret, username + clientId))
        /// </summary>
        private static string ComputeSecretHash(string username, string clientId, string clientSecret)
        {
            var message = username + clientId;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(clientSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Computes the MD5 hash of a string using the EXACT same algorithm as
        /// <c>PasswordUtil.GetMd5Hash</c> from the monolith source.
        /// CRITICAL: Uses <c>Encoding.UTF8</c> (NOT Unicode) and lowercase hex "x2" format.
        /// </summary>
        private static string ComputeMd5Hash(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            byte[] data = MD5.HashData(Encoding.UTF8.GetBytes(input));
            var sBuilder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                sBuilder.Append(data[i].ToString("x2"));
            }
            return sBuilder.ToString();
        }

        /// <summary>
        /// Validates email format using System.Net.Mail.MailAddress.
        /// Preserves EXACT behavioral parity with SecurityManager.IsValidEmail() (source lines 358-368).
        /// </summary>
        private static bool IsValidEmail(string email)
        {
            try
            {
                var addr = new MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Maps Cognito user attributes from a GetUser response to the internal User domain model.
        /// Retrieves supplementary data (roles, extended attributes) from DynamoDB.
        /// </summary>
        private async Task<User?> MapCognitoUserToUserAsync(
            GetUserResponse cognitoResponse, CancellationToken cancellationToken)
        {
            if (cognitoResponse == null || cognitoResponse.UserAttributes == null)
            {
                return null;
            }

            var attributes = cognitoResponse.UserAttributes;

            var erpUserIdStr = attributes
                .FirstOrDefault(a => a.Name == "custom:erp_user_id")?.Value;

            if (!Guid.TryParse(erpUserIdStr, out var erpUserId))
            {
                _logger.LogWarning(
                    "Cognito user {Username} has no valid custom:erp_user_id attribute",
                    cognitoResponse.Username);
                return null;
            }

            // Load the full user record from DynamoDB (includes roles, preferences, image, timestamps)
            var dbUser = await _userRepository.GetUserByIdAsync(erpUserId, cancellationToken)
                .ConfigureAwait(false);

            var user = new User
            {
                Id = erpUserId,
                Username = attributes.FirstOrDefault(a => a.Name == "preferred_username")?.Value
                    ?? cognitoResponse.Username,
                Email = attributes.FirstOrDefault(a => a.Name == "email")?.Value ?? string.Empty,
                FirstName = attributes.FirstOrDefault(a => a.Name == "given_name")?.Value ?? string.Empty,
                LastName = attributes.FirstOrDefault(a => a.Name == "family_name")?.Value ?? string.Empty,
                CognitoSub = attributes.FirstOrDefault(a => a.Name == "sub")?.Value,
                EmailVerified = string.Equals(
                    attributes.FirstOrDefault(a => a.Name == "email_verified")?.Value,
                    "true", StringComparison.OrdinalIgnoreCase),
                Enabled = true,
                Verified = true
            };

            if (dbUser != null)
            {
                user.Roles = dbUser.Roles;
                user.Image = dbUser.Image;
                user.CreatedOn = dbUser.CreatedOn;
                user.LastLoggedIn = dbUser.LastLoggedIn;
            }
            else
            {
                // User in Cognito but not yet in DynamoDB — attempt to load roles
                var roles = await _userRepository.GetUserRolesAsync(erpUserId, cancellationToken)
                    .ConfigureAwait(false);
                user.Roles = roles;
                user.CreatedOn = DateTime.UtcNow;
            }

            return user;
        }

        /// <summary>
        /// Maps a Cognito ListUsers UserType entry to the internal User model.
        /// Used during ListUsersAsync for batch user listing.
        /// </summary>
        private static User? MapCognitoUserTypeToUser(UserType cognitoUser)
        {
            if (cognitoUser == null || cognitoUser.Attributes == null)
            {
                return null;
            }

            var attributes = cognitoUser.Attributes;

            var erpUserIdStr = attributes
                .FirstOrDefault(a => a.Name == "custom:erp_user_id")?.Value;

            if (!Guid.TryParse(erpUserIdStr, out var erpUserId))
            {
                return null;
            }

            return new User
            {
                Id = erpUserId,
                Username = attributes.FirstOrDefault(a => a.Name == "preferred_username")?.Value
                    ?? cognitoUser.Username,
                Email = attributes.FirstOrDefault(a => a.Name == "email")?.Value ?? string.Empty,
                FirstName = attributes.FirstOrDefault(a => a.Name == "given_name")?.Value ?? string.Empty,
                LastName = attributes.FirstOrDefault(a => a.Name == "family_name")?.Value ?? string.Empty,
                CognitoSub = attributes.FirstOrDefault(a => a.Name == "sub")?.Value,
                EmailVerified = string.Equals(
                    attributes.FirstOrDefault(a => a.Name == "email_verified")?.Value,
                    "true", StringComparison.OrdinalIgnoreCase),
                Enabled = cognitoUser.Enabled,
                Verified = true,
                CreatedOn = cognitoUser.UserCreateDate
            };
        }

        /// <summary>
        /// Maps Cognito AuthenticationResultType to the public AuthTokenResult DTO.
        /// </summary>
        private static AuthTokenResult MapToAuthTokenResult(
            AuthenticationResultType authResult, string? originalRefreshToken = null)
        {
            return new AuthTokenResult
            {
                IdToken = authResult.IdToken ?? string.Empty,
                AccessToken = authResult.AccessToken ?? string.Empty,
                // Cognito does not return a new refresh token on REFRESH_TOKEN_AUTH flow;
                // preserve the original if available
                RefreshToken = authResult.RefreshToken ?? originalRefreshToken ?? string.Empty,
                ExpiresIn = authResult.ExpiresIn,
                TokenType = authResult.TokenType ?? "Bearer"
            };
        }

        /// <summary>
        /// Adds a user to a Cognito group, creating the group first if it does not exist.
        /// Ensures idempotent group creation to support seed and migration scenarios.
        /// </summary>
        private async Task AddUserToGroupSafeAsync(
            string username, string groupName, CancellationToken cancellationToken)
        {
            // Ensure the group exists first
            try
            {
                await _cognitoClient.CreateGroupAsync(new CreateGroupRequest
                {
                    UserPoolId = _userPoolId,
                    GroupName = groupName,
                    Description = $"Auto-created group for role '{groupName}'"
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonCognitoIdentityProviderException ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Group already exists — safe to proceed
            }

            // Add user to group
            await _cognitoClient.AdminAddUserToGroupAsync(
                new AdminAddUserToGroupRequest
                {
                    UserPoolId = _userPoolId,
                    Username = username,
                    GroupName = groupName
                }, cancellationToken).ConfigureAwait(false);
        }
    }
}
