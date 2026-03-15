using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Identity.DataAccess;
using WebVellaErp.Identity.Models;
using WebVellaErp.Identity.Services;

// Assembly-level LambdaSerializer attribute declared in RoleHandler.cs
// [assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Identity.Functions;

/// <summary>
/// Request DTO for POST /v1/auth/login.
/// Replaces the monolith's JwtTokenLoginModel (WebApiController line 4276)
/// with Email and Password fields for Cognito-based authentication.
/// </summary>
public class LoginRequest
{
    /// <summary>User email address. Replaces source JwtTokenLoginModel.Email.</summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    /// <summary>User password. Replaces source JwtTokenLoginModel.Password.</summary>
    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Request DTO for POST /v1/auth/token/refresh.
/// Replaces the monolith's JwtTokenModel (WebApiController line 4295)
/// with RefreshToken field for Cognito REFRESH_TOKEN_AUTH flow.
/// </summary>
public class RefreshTokenRequest
{
    /// <summary>Cognito refresh token. Replaces source JwtTokenModel.Token.</summary>
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Standard API response DTO matching the monolith's ResponseModel pattern.
/// Fields: Success, Timestamp, Message, Object, Errors — preserving backward
/// compatibility with existing API consumers.
/// </summary>
public class AuthResponse
{
    /// <summary>Whether the operation succeeded. Matches ResponseModel.Success.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>UTC timestamp of the response. Matches ResponseModel.Timestamp.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>Human-readable status message. Matches ResponseModel.Message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Payload data (tokens, user profile). Matches ResponseModel.Object.</summary>
    [JsonPropertyName("object")]
    public object? Object { get; set; }

    /// <summary>Validation or processing errors. Matches ResponseModel.Errors.</summary>
    [JsonPropertyName("errors")]
    public List<object>? Errors { get; set; }
}

/// <summary>
/// AWS Lambda entry point for authentication operations in the Identity bounded context.
///
/// Handles three HTTP API Gateway v2 routes:
///   POST /v1/auth/login         → HandleLogin
///   POST /v1/auth/logout        → HandleLogout
///   POST /v1/auth/token/refresh → HandleRefreshToken
///
/// Replaces:
/// - WebVella.Erp.Web/Services/AuthService.cs — Cookie + JWT authentication
///   (Authenticate, Logout, GetTokenAsync, GetNewTokenAsync, BuildTokenAsync)
/// - WebVella.Erp.Web/Controllers/WebApiController.cs lines 4270-4311 — JWT token endpoints
///   (GetJwtToken, GetNewJwtToken actions)
/// - WebVella.Erp.Web/Middleware/JwtMiddleware.cs — JWT extraction from Authorization header
///
/// All credential validation is delegated to AWS Cognito via ICognitoService.
/// Cookie authentication, custom JWT issuance (HMAC-SHA256), and MD5 password
/// hashing are completely removed in favor of Cognito-native authentication.
/// Post-login hooks are replaced by SNS domain event publishing.
/// </summary>
public class AuthHandler
{
    private readonly ICognitoService _cognitoService;
    private readonly ILogger<AuthHandler> _logger;
    private readonly IAmazonSimpleNotificationService _snsClient;

    /// <summary>AOT-compatible JSON serializer options for request/response serialization.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Default HTTP response headers including CORS and content type.</summary>
    private static readonly Dictionary<string, string> DefaultHeaders = new()
    {
        { "Content-Type", "application/json" },
        { "Access-Control-Allow-Origin", "*" },
        { "Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS" },
        { "Access-Control-Allow-Headers", "Content-Type, Authorization, X-Correlation-Id" }
    };

    /// <summary>
    /// Parameterless constructor for Lambda runtime instantiation.
    /// Builds a DI container with all required services and AWS SDK clients.
    /// Matches the DI composition pattern used by RoleHandler and UserHandler.
    /// </summary>
    public AuthHandler()
    {
        var services = new ServiceCollection();

        // Structured logging for CloudWatch JSON capture
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            builder.AddConsole();
        });

        // AWS SDK clients with optional LocalStack endpoint override
        var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            services.AddSingleton<IAmazonDynamoDB>(_ =>
                new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));
            services.AddSingleton<IAmazonCognitoIdentityProvider>(_ =>
                new AmazonCognitoIdentityProviderClient(
                    new AmazonCognitoIdentityProviderConfig { ServiceURL = endpointUrl }));
            services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                new AmazonSimpleNotificationServiceClient(
                    new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));
            services.AddSingleton<IAmazonSimpleSystemsManagement>(_ =>
                new AmazonSimpleSystemsManagementClient(
                    new AmazonSimpleSystemsManagementConfig { ServiceURL = endpointUrl }));
        }
        else
        {
            services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
            services.AddSingleton<IAmazonCognitoIdentityProvider, AmazonCognitoIdentityProviderClient>();
            services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
            services.AddSingleton<IAmazonSimpleSystemsManagement, AmazonSimpleSystemsManagementClient>();
        }

        // Domain services — registered as transient for stateless per-invocation isolation
        services.AddTransient<IPermissionService, PermissionService>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<ICognitoService, CognitoService>();

        var serviceProvider = services.BuildServiceProvider();
        _cognitoService = serviceProvider.GetRequiredService<ICognitoService>();
        _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
        _logger = serviceProvider.GetRequiredService<ILogger<AuthHandler>>();
    }

    /// <summary>
    /// Constructor for unit testing — accepts a pre-configured IServiceProvider.
    /// </summary>
    /// <param name="serviceProvider">Pre-configured service provider with all dependencies.</param>
    public AuthHandler(IServiceProvider serviceProvider)
    {
        _cognitoService = serviceProvider.GetRequiredService<ICognitoService>();
        _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
        _logger = serviceProvider.GetRequiredService<ILogger<AuthHandler>>();
    }

    /// <summary>
    /// Single entry point for managed .NET Lambda runtime (dotnet9).
    /// Routes API Gateway HTTP API v2 requests to the appropriate handler method.
    /// </summary>
    public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var path = request.RawPath ?? request.RequestContext?.Http?.Path ?? string.Empty;
        var method = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "GET";

        if (method == "POST" && path.Contains("/login"))
            return await HandleLogin(request, context);
        if (method == "POST" && path.Contains("/logout"))
            return await HandleLogout(request, context);
        if (method == "POST" && path.Contains("/refresh"))
            return await HandleRefreshToken(request, context);

        // Default: login
        return await HandleLogin(request, context);
    }


    /// <summary>
    /// POST /v1/auth/login — Authenticates user via Cognito and returns tokens with user profile.
    ///
    /// Replaces:
    /// - AuthService.Authenticate() lines 29-55: SecurityManager.GetUser(email, password) with MD5
    ///   credential validation + cookie-based SignInAsync
    /// - AuthService.GetTokenAsync() lines 83-91: credential validation + BuildTokenAsync with
    ///   HMAC-SHA256 JWT signing
    /// - WebApiController.GetJwtToken() lines 4276-4290: JwtTokenLoginModel deserialization +
    ///   ResponseModel construction
    ///
    /// New behavior: Cognito USER_PASSWORD_AUTH flow handles credential validation, token issuance,
    /// and user status checks. SNS domain event replaces post-login hooks.
    /// </summary>
    /// <param name="request">HTTP API Gateway v2 proxy request containing login credentials in body.</param>
    /// <param name="context">Lambda execution context for request ID and remaining time.</param>
    /// <returns>API Gateway v2 proxy response with tokens and user profile or error details.</returns>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleLogin(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        _logger.LogInformation(
            "HandleLogin invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
            correlationId, context.AwsRequestId);

        try
        {
            // Validate request body presence
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Request body is required.",
                    Errors = new List<object> { "Email and password are required." }
                });
            }

            // Deserialize login credentials from JSON body
            LoginRequest? loginRequest;
            try
            {
                loginRequest = JsonSerializer.Deserialize<LoginRequest>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Invalid JSON in request body.",
                    Errors = new List<object> { "Request body must be valid JSON with 'email' and 'password' fields." }
                });
            }

            if (loginRequest == null)
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Request body deserialized to null."
                });
            }

            // Pre-hook validation at handler level — replaces IErpPreCreateRecordHook pattern.
            // Source SecurityManager.GetUser used email.Trim().ToLowerInvariant(); Cognito handles case.
            var email = loginRequest.Email?.Trim();
            var password = loginRequest.Password?.Trim();

            if (string.IsNullOrEmpty(email))
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Email is required.",
                    Errors = new List<object> { "The 'email' field must not be empty." }
                });
            }

            if (string.IsNullOrEmpty(password))
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Password is required.",
                    Errors = new List<object> { "The 'password' field must not be empty." }
                });
            }

            _logger.LogInformation(
                "Login attempt for user {Email}. CorrelationId={CorrelationId}",
                ObfuscateEmail(email), correlationId);

            // Authenticate user via Cognito — replaces SecurityManager.GetUser(email, password)
            // which performed MD5 hash comparison via CryptoUtility.ComputeOddMD5Hash.
            // Cognito natively handles credential validation and user status checks.
            var user = await _cognitoService.AuthenticateAsync(email, password);
            if (user == null)
            {
                _logger.LogWarning(
                    "Authentication failed for {Email}. CorrelationId={CorrelationId}",
                    ObfuscateEmail(email), correlationId);
                return BuildResponse(401, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Invalid email or password."
                });
            }

            // Obtain Cognito tokens — replaces AuthService.BuildTokenAsync() which created
            // HMAC-SHA256 signed JWTs with claims (NameIdentifier, Email, Roles).
            // Cognito issues IdToken, AccessToken, and RefreshToken natively.
            var tokens = await _cognitoService.GetTokenAsync(email, password);
            if (tokens == null)
            {
                _logger.LogWarning(
                    "Token generation failed for {Email}. CorrelationId={CorrelationId}",
                    ObfuscateEmail(email), correlationId);
                return BuildResponse(401, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Invalid email or password."
                });
            }

            _logger.LogInformation(
                "Login successful for UserId={UserId} ({Email}). CorrelationId={CorrelationId}",
                user.Id, ObfuscateEmail(email), correlationId);

            // Publish post-login domain event — replaces synchronous IErpPostCreateRecordHook
            // invocations from HookManager. Per AAP Section 0.7.2: post-hooks become SNS events.
            await PublishLoginEventAsync(user.Id, correlationId);

            // Build response matching source ResponseModel pattern:
            // Timestamp = DateTime.UtcNow, Success = true, Object = payload
            return BuildResponse(200, new AuthResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Login successful.",
                Object = new
                {
                    token = new
                    {
                        idToken = tokens.IdToken,
                        accessToken = tokens.AccessToken,
                        refreshToken = tokens.RefreshToken,
                        expiresIn = tokens.ExpiresIn,
                        tokenType = tokens.TokenType
                    },
                    user = new
                    {
                        id = user.Id,
                        email = user.Email,
                        username = user.Username,
                        firstName = user.FirstName,
                        lastName = user.LastName,
                        roles = user.Roles
                    }
                }
            });
        }
        catch (NotAuthorizedException ex)
        {
            _logger.LogWarning(
                "Cognito NotAuthorizedException during login. CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(401, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "Invalid email or password."
            });
        }
        catch (UserNotFoundException ex)
        {
            _logger.LogWarning(
                "Cognito UserNotFoundException during login. CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(401, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "Invalid email or password."
            });
        }
        catch (UserNotConfirmedException ex)
        {
            _logger.LogWarning(
                "Cognito UserNotConfirmedException during login. CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(403, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "User account is not confirmed. Please verify your email address."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception in HandleLogin. CorrelationId={CorrelationId}",
                correlationId);
            return BuildResponse(500, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "An internal error occurred."
            });
        }
    }

    /// <summary>
    /// POST /v1/auth/logout — Revokes all tokens for the authenticated user.
    ///
    /// Replaces:
    /// - AuthService.Logout() lines 57-61: HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
    ///
    /// New behavior: Calls Cognito AdminUserGlobalSignOut to revoke all tokens for the user.
    /// Logout is naturally idempotent — revoking already-revoked tokens succeeds per AAP Section 0.8.5.
    /// </summary>
    /// <param name="request">HTTP API Gateway v2 proxy request with Authorization header.</param>
    /// <param name="context">Lambda execution context for request ID and remaining time.</param>
    /// <returns>API Gateway v2 proxy response confirming logout or error details.</returns>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleLogout(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        _logger.LogInformation(
            "HandleLogout invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
            correlationId, context.AwsRequestId);

        try
        {
            // Extract bearer token from Authorization header.
            // Replaces JwtMiddleware.cs lines 23-35: token extraction from Authorization
            // header, stripping "Bearer " prefix (7 characters per source line 32).
            var accessToken = ExtractBearerToken(request);
            if (string.IsNullOrEmpty(accessToken))
            {
                return BuildResponse(401, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Authorization token is required."
                });
            }

            // Revoke all tokens via Cognito — replaces cookie SignOutAsync.
            // Source AuthService.Logout() line 60 called:
            //   httpContextAccesor.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            // New: Cognito AdminUserGlobalSignOut revokes all active tokens.
            await _cognitoService.LogoutAsync(accessToken);

            _logger.LogInformation(
                "Logout successful. CorrelationId={CorrelationId}", correlationId);

            return BuildResponse(200, new AuthResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Logout successful."
            });
        }
        catch (NotAuthorizedException ex)
        {
            // Token already revoked or expired — logout is idempotent per AAP Section 0.8.5.
            // Return success since the end state (user logged out) is achieved regardless.
            _logger.LogWarning(
                "Cognito NotAuthorizedException during logout (token may be expired/revoked). " +
                "CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(200, new AuthResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Logout successful."
            });
        }
        catch (UserNotFoundException ex)
        {
            _logger.LogWarning(
                "Cognito UserNotFoundException during logout. CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(401, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "Invalid authorization token."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception in HandleLogout. CorrelationId={CorrelationId}",
                correlationId);
            return BuildResponse(500, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "An internal error occurred."
            });
        }
    }

    /// <summary>
    /// POST /v1/auth/token/refresh — Issues new tokens using a Cognito refresh token.
    ///
    /// Replaces:
    /// - AuthService.GetNewTokenAsync() lines 94-117: JWT validation via GetValidSecurityTokenAsync,
    ///   claim extraction (NameIdentifier), user reload by GUID, enabled check, new token build
    /// - WebApiController.GetNewJwtToken() lines 4295-4309: JwtTokenModel deserialization +
    ///   ResponseModel construction
    ///
    /// New behavior: Cognito REFRESH_TOKEN_AUTH flow handles all validation, user status checks,
    /// and new token issuance natively. No custom JWT re-signing required.
    /// </summary>
    /// <param name="request">HTTP API Gateway v2 proxy request with refresh token in body.</param>
    /// <param name="context">Lambda execution context for request ID and remaining time.</param>
    /// <returns>API Gateway v2 proxy response with new tokens or error details.</returns>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleRefreshToken(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        _logger.LogInformation(
            "HandleRefreshToken invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
            correlationId, context.AwsRequestId);

        try
        {
            // Validate request body presence
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Request body is required.",
                    Errors = new List<object> { "Refresh token is required." }
                });
            }

            // Deserialize refresh token from JSON body
            RefreshTokenRequest? refreshRequest;
            try
            {
                refreshRequest = JsonSerializer.Deserialize<RefreshTokenRequest>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Invalid JSON in request body.",
                    Errors = new List<object> { "Request body must be valid JSON with 'refreshToken' field." }
                });
            }

            if (refreshRequest == null || string.IsNullOrWhiteSpace(refreshRequest.RefreshToken))
            {
                return BuildResponse(400, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Refresh token is required."
                });
            }

            // Refresh tokens via Cognito REFRESH_TOKEN_AUTH flow.
            // Source AuthService.GetNewTokenAsync() logic was:
            //   1. GetValidSecurityTokenAsync(tokenString) — validate JWT signature
            //   2. Extract NameIdentifier claim from validated token
            //   3. Load user by GUID via SecurityManager.GetUser(Guid)
            //   4. Check user exists and is enabled
            //   5. Build new JWT with BuildTokenAsync(user)
            // Cognito handles ALL of these steps natively in the REFRESH_TOKEN_AUTH flow.
            var tokens = await _cognitoService.RefreshTokenAsync(refreshRequest.RefreshToken);
            if (tokens == null)
            {
                _logger.LogWarning(
                    "Token refresh failed — invalid or expired refresh token. CorrelationId={CorrelationId}",
                    correlationId);
                return BuildResponse(401, new AuthResponse
                {
                    Success = false,
                    Timestamp = DateTime.UtcNow,
                    Message = "Invalid or expired refresh token."
                });
            }

            _logger.LogInformation(
                "Token refresh successful. CorrelationId={CorrelationId}", correlationId);

            // Build response matching source pattern from WebApiController line 4300:
            //   response.Object = await AuthService.GetNewTokenAsync(model.Token)
            return BuildResponse(200, new AuthResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Token refreshed successfully.",
                Object = new
                {
                    idToken = tokens.IdToken,
                    accessToken = tokens.AccessToken,
                    refreshToken = tokens.RefreshToken,
                    expiresIn = tokens.ExpiresIn,
                    tokenType = tokens.TokenType
                }
            });
        }
        catch (NotAuthorizedException ex)
        {
            _logger.LogWarning(
                "Cognito NotAuthorizedException during token refresh. " +
                "CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(401, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "Invalid or expired refresh token."
            });
        }
        catch (UserNotFoundException ex)
        {
            _logger.LogWarning(
                "Cognito UserNotFoundException during token refresh. " +
                "CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(401, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "User associated with refresh token was not found."
            });
        }
        catch (UserNotConfirmedException ex)
        {
            _logger.LogWarning(
                "Cognito UserNotConfirmedException during token refresh. " +
                "CorrelationId={CorrelationId}, Message={Message}",
                correlationId, ex.Message);
            return BuildResponse(403, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "User account is not confirmed."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception in HandleRefreshToken. CorrelationId={CorrelationId}",
                correlationId);
            return BuildResponse(500, new AuthResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = "An internal error occurred."
            });
        }
    }

    #region Helper Methods

    /// <summary>
    /// Creates a consistent API Gateway HTTP API v2 response with JSON body and standard headers.
    /// Mirrors the source DoResponse(BaseResponseModel) helper from ApiControllerBase.cs which
    /// aligned HTTP status codes with response semantics.
    /// </summary>
    /// <param name="statusCode">HTTP status code (200, 400, 401, 403, 500).</param>
    /// <param name="body">Response body object to be JSON-serialized.</param>
    /// <returns>Fully constructed API Gateway v2 proxy response.</returns>
    private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object body)
    {
        return new APIGatewayHttpApiV2ProxyResponse
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string>(DefaultHeaders),
            Body = JsonSerializer.Serialize(body, JsonOptions)
        };
    }

    /// <summary>
    /// Extracts Bearer token from the Authorization header.
    ///
    /// Replaces JwtMiddleware.cs lines 23-35:
    /// - Reads from Authorization header
    /// - Strips "Bearer " prefix (7 characters, matching source line 32: token.Substring(7))
    /// - Returns null if header missing or token too short (matching source lines 29-30:
    ///   if (token.Length &lt;= 7) token = null)
    /// </summary>
    /// <param name="request">HTTP API Gateway v2 proxy request.</param>
    /// <returns>Extracted token string, or null if not present or malformed.</returns>
    private static string? ExtractBearerToken(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (request.Headers == null)
        {
            return null;
        }

        // HTTP API v2 normalizes header names to lowercase
        if (!request.Headers.TryGetValue("authorization", out var authHeader) ||
            string.IsNullOrWhiteSpace(authHeader))
        {
            return null;
        }

        // Strip "Bearer " prefix (7 chars) — matches source JwtMiddleware line 32: token.Substring(7)
        // Source line 29-30: if (token.Length <= 7) token = null
        var token = authHeader.Trim();
        if (token.Length <= 7)
        {
            return null;
        }

        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = token.Substring(7).Trim();
            return string.IsNullOrEmpty(extracted) ? null : extracted;
        }

        return null;
    }

    /// <summary>
    /// Extracts correlation-ID from request headers or generates a new one.
    /// Per AAP Section 0.8.5: "Structured JSON logging with correlation-ID propagation."
    /// Checks x-correlation-id first, then x-request-id, then API Gateway requestId,
    /// and finally falls back to a new GUID.
    /// </summary>
    /// <param name="request">HTTP API Gateway v2 proxy request.</param>
    /// <returns>Correlation ID string for structured logging.</returns>
    private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
    {
        if (request.Headers != null)
        {
            // HTTP API v2 normalizes header names to lowercase
            if (request.Headers.TryGetValue("x-correlation-id", out var correlationValue) &&
                !string.IsNullOrWhiteSpace(correlationValue))
            {
                return correlationValue;
            }

            if (request.Headers.TryGetValue("x-request-id", out var requestIdValue) &&
                !string.IsNullOrWhiteSpace(requestIdValue))
            {
                return requestIdValue;
            }
        }

        // Fall back to API Gateway request context ID if available
        if (request.RequestContext != null &&
            !string.IsNullOrWhiteSpace(request.RequestContext.RequestId))
        {
            return request.RequestContext.RequestId;
        }

        return Guid.NewGuid().ToString("D");
    }

    /// <summary>
    /// Obfuscates email address for secure logging.
    /// Per security requirements: NEVER log full email addresses or passwords.
    /// Shows first character + masked local part + full domain.
    /// Example: "user@domain.com" → "u***@domain.com"
    /// </summary>
    /// <param name="email">Email address to obfuscate.</param>
    /// <returns>Obfuscated email string safe for logging.</returns>
    private static string ObfuscateEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return "***";
        }

        var atIndex = email.IndexOf('@');
        if (atIndex <= 0)
        {
            return "***";
        }

        return email[0] + "***" + email.Substring(atIndex);
    }

    /// <summary>
    /// Publishes SNS domain event after successful login.
    /// Replaces the monolith's synchronous IErpPostCreateRecordHook invocations
    /// from HookManager with async event-driven architecture.
    /// Event pattern: identity.user.loggedIn (per AAP naming convention: {domain}.{entity}.{action})
    ///
    /// Failure to publish does NOT fail the login response — the event is best-effort.
    /// Per AAP: "At-least-once delivery guarantee via SQS" — consumers handle idempotency.
    /// </summary>
    /// <param name="userId">Authenticated user's unique identifier.</param>
    /// <param name="correlationId">Correlation ID for distributed tracing.</param>
    private async Task PublishLoginEventAsync(Guid userId, string correlationId)
    {
        try
        {
            var topicArn = Environment.GetEnvironmentVariable("AUTH_EVENTS_TOPIC_ARN");
            if (string.IsNullOrEmpty(topicArn))
            {
                _logger.LogWarning(
                    "AUTH_EVENTS_TOPIC_ARN not configured — skipping login event publish. " +
                    "CorrelationId={CorrelationId}",
                    correlationId);
                return;
            }

            var eventPayload = JsonSerializer.Serialize(new
            {
                eventType = "identity.user.loggedIn",
                userId = userId,
                timestamp = DateTime.UtcNow,
                correlationId = correlationId
            }, JsonOptions);

            await _snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = eventPayload,
                Subject = "identity.user.loggedIn"
            });

            _logger.LogInformation(
                "Published login event for UserId={UserId}. CorrelationId={CorrelationId}",
                userId, correlationId);
        }
        catch (Exception ex)
        {
            // SNS publish failure must NOT fail the login response.
            // The user's authentication is complete; the event is best-effort.
            _logger.LogError(ex,
                "Failed to publish login event for UserId={UserId}. CorrelationId={CorrelationId}",
                userId, correlationId);
        }
    }

    #endregion
}
