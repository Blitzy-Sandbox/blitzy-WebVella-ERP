// =============================================================================
// WebVella ERP — Core Platform Microservice
// SecurityGrpcServiceImpl: gRPC service for security/identity operations
// =============================================================================
// Server-side gRPC service exposing Core Platform security and identity
// operations to other microservices. Wraps the adapted SecurityManager from
// WebVella.Erp.Service.Core.Api and the SharedKernel JwtTokenHandler for
// cross-service user resolution, credential validation, and JWT token
// management.
//
// Other microservices (CRM, Project, Mail, etc.) call this service to:
//   - Resolve user information for cross-service references (e.g., resolving
//     created_by/modified_by user UUIDs per AAP 0.7.1 strategy)
//   - Validate JWT tokens and retrieve decoded user identity with roles
//   - Look up role definitions for permission checks
//   - Authenticate users by email/password credentials
//   - Persist user and role records (admin operations)
//
// Design decisions:
//   - Named SecurityGrpcServiceImpl to avoid naming collision with the
//     proto-generated static class SecurityGrpcService (option csharp_namespace
//     in core.proto places both in WebVella.Erp.Service.Core.Grpc).
//   - Implements ALL proto-defined RPCs: GetUser, GetUserByCredentials,
//     GetUsers, GetAllRoles, SaveUser, SaveRole, UpdateUserLastLogin,
//     ValidateToken.
//   - User/role models are mapped to structured proto messages (ErpUserProto,
//     ErpRoleProto) rather than JSON strings, leveraging the proto schema for
//     type-safe cross-service communication.
//   - Every method opens a SecurityContext scope from JWT claims to maintain
//     the audit trail (except credential/token validation which use system
//     scope or AllowAnonymous).
//   - Every method is wrapped in try/catch with structured logging and
//     RpcException with appropriate StatusCode.
//   - Passwords are NEVER logged or included in any serialized response.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Google.Protobuf.WellKnownTypes;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.SharedKernel.Security;
// Namespace alias: distinguishes SharedKernel domain models from proto-generated
// types that share the same names (e.g., Models.ErpUser vs proto ErpUserProto).
using Models = WebVella.Erp.SharedKernel.Models;
// Proto common types from common.proto (ErrorModel).
using ProtoCommon = WebVella.Erp.SharedKernel.Grpc;

namespace WebVella.Erp.Service.Core.Grpc
{
    /// <summary>
    /// gRPC service implementation for security and identity operations.
    /// Inherits from the proto-generated <see cref="SecurityGrpcService.SecurityGrpcServiceBase"/>
    /// and overrides all RPCs defined in proto/core.proto's SecurityGrpcService.
    ///
    /// All methods require JWT authentication via the [Authorize] attribute applied at
    /// the class level (AAP 0.8.1 requirement), with [AllowAnonymous] exceptions for
    /// <see cref="GetUserByCredentials"/> and <see cref="ValidateToken"/> which are used
    /// in pre-authentication flows.
    ///
    /// Per AAP 0.7.1: "Audit fields (created_by, modified_by) — Store user UUID; resolve
    /// via Core gRPC call on read" — this is the service handling those resolution calls.
    ///
    /// Per AAP 0.8.3: "JWT tokens issued by the Core service must contain all necessary
    /// claims (user ID, roles, permissions) for downstream services to authorize requests
    /// without callback to the Core service."
    /// </summary>
    [Authorize]
    public class SecurityGrpcServiceImpl : SecurityGrpcService.SecurityGrpcServiceBase
    {
        private readonly SecurityManager _securityManager;
        private readonly JwtTokenHandler _jwtTokenHandler;
        private readonly ILogger<SecurityGrpcServiceImpl> _logger;

        /// <summary>
        /// JSON serializer settings for user/role serialization in debug logging.
        /// Uses NullValueHandling.Ignore for consistent output matching the monolith's
        /// Newtonsoft.Json contract stability requirement (AAP 0.8.2).
        /// </summary>
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Constructs a new SecurityGrpcServiceImpl with required service dependencies.
        /// All parameters are injected via ASP.NET Core DI.
        /// </summary>
        /// <param name="securityManager">Core user/role CRUD manager wrapping the adapted
        /// SecurityManager from the monolith. Provides GetUser, GetUsers, GetAllRoles,
        /// SaveUser, SaveRole, and UpdateLastLoginAndModifiedDate operations.</param>
        /// <param name="jwtTokenHandler">Shared JWT token handler from SharedKernel.Security
        /// providing token validation, user ID extraction, and refresh-required checks
        /// for cross-service authentication.</param>
        /// <param name="logger">Structured logger for error-level exception logging and
        /// debug-level user lookup operation tracing.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public SecurityGrpcServiceImpl(
            SecurityManager securityManager,
            JwtTokenHandler jwtTokenHandler,
            ILogger<SecurityGrpcServiceImpl> logger)
        {
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _jwtTokenHandler = jwtTokenHandler ?? throw new ArgumentNullException(nameof(jwtTokenHandler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region User Operations

        /// <summary>
        /// Retrieves a user by ID, email, or username. Only one lookup field should be
        /// populated in the request; priority is user_id > email > username.
        ///
        /// This is the CRITICAL identity resolution endpoint called by ALL other
        /// microservices to resolve user UUIDs stored in created_by/modified_by audit
        /// fields (per AAP 0.7.1 cross-service relation resolution strategy).
        ///
        /// Source: SecurityManager.GetUser(Guid), GetUser(string email), GetUserByUsername(string)
        /// </summary>
        /// <param name="request">Request containing one of: user_id (GUID string), email, or username.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>GetUserResponse with the user proto if found, or success=true with no user if not found.</returns>
        /// <exception cref="RpcException">
        /// InvalidArgument if no lookup field is provided or ID format is invalid.
        /// Internal on unexpected failures.
        /// </exception>
        public override Task<GetUserResponse> GetUser(
            GetUserRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    Models.ErpUser user = null;

                    // Priority: user_id > email > username (per proto documentation)
                    if (!string.IsNullOrWhiteSpace(request.UserId))
                    {
                        if (!Guid.TryParse(request.UserId, out Guid userId))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid user ID format: '{request.UserId}'. Expected a valid GUID."));
                        }
                        _logger.LogDebug(
                            "gRPC SecurityGrpcService.GetUser looking up user by ID: {UserId}",
                            userId);
                        user = _securityManager.GetUser(userId);
                    }
                    else if (!string.IsNullOrWhiteSpace(request.Email))
                    {
                        _logger.LogDebug(
                            "gRPC SecurityGrpcService.GetUser looking up user by email");
                        user = _securityManager.GetUser(request.Email);
                    }
                    else if (!string.IsNullOrWhiteSpace(request.Username))
                    {
                        _logger.LogDebug(
                            "gRPC SecurityGrpcService.GetUser looking up user by username: {Username}",
                            request.Username);
                        user = _securityManager.GetUserByUsername(request.Username);
                    }
                    else
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "At least one of 'user_id', 'email', or 'username' must be provided in GetUserRequest."));
                    }

                    var response = new GetUserResponse { Success = true };
                    if (user != null)
                    {
                        response.User = MapUserToProto(user);
                    }

                    return Task.FromResult(response);
                }
            }
            catch (RpcException)
            {
                // Re-throw RpcExceptions as-is (already have proper status codes)
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(GetUser), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing user lookup request."));
            }
        }

        /// <summary>
        /// Authenticates a user by email and password credentials.
        /// Password is validated against the MD5-hashed stored value server-side by
        /// SecurityManager.GetUser(email, password).
        ///
        /// Marked [AllowAnonymous] because this is called during authentication flow
        /// before a JWT token exists. Service-to-service communication should use
        /// mTLS or a shared API key for transport security.
        ///
        /// Source: SecurityManager.GetUser(string email, string password)
        ///
        /// SECURITY: Passwords are NEVER logged. Response never includes password data
        /// (ErpUser.Password has [JsonIgnore] and ErpUserProto has no password field).
        /// </summary>
        /// <param name="request">Request containing email and plain-text password.</param>
        /// <param name="context">gRPC server call context.</param>
        /// <returns>GetUserResponse with the user proto if credentials valid, or success=true with no user.</returns>
        [AllowAnonymous]
        public override Task<GetUserResponse> GetUserByCredentials(
            GetUserByCredentialsRequest request,
            ServerCallContext context)
        {
            try
            {
                // Use system scope for credential validation — no caller JWT needed
                using (SecurityContext.OpenSystemScope())
                {
                    if (string.IsNullOrWhiteSpace(request.Email))
                    {
                        // Source: SecurityManager.GetUser(email, password) line 79:
                        // "if (string.IsNullOrWhiteSpace(email)) return null;"
                        // Preserving original monolith behavior — return success with null user
                        return Task.FromResult(new GetUserResponse { Success = true });
                    }

                    // Do NOT log the password — security requirement per AAP 0.8.3
                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.GetUserByCredentials validating credentials for email");

                    // SecurityManager.GetUser(email, password) performs MD5 hashing
                    // and case-insensitive email matching internally
                    var user = _securityManager.GetUser(request.Email, request.Password);

                    var response = new GetUserResponse { Success = true };
                    if (user != null)
                    {
                        response.User = MapUserToProto(user);
                    }

                    return Task.FromResult(response);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(GetUserByCredentials), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing credential validation request."));
            }
        }

        /// <summary>
        /// Retrieves a list of users, optionally filtered by role IDs.
        /// If no role IDs are provided, returns all users.
        ///
        /// Source: SecurityManager.GetUsers(params Guid[] roleIds)
        /// </summary>
        /// <param name="request">Request containing optional repeated role_ids (GUID strings).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>GetUsersResponse with the list of user protos.</returns>
        public override Task<GetUsersResponse> GetUsers(
            GetUsersRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    // Parse role IDs from string to Guid
                    var roleIds = new List<Guid>();
                    foreach (var roleIdStr in request.RoleIds)
                    {
                        if (!Guid.TryParse(roleIdStr, out Guid roleId))
                        {
                            throw new RpcException(new Status(
                                StatusCode.InvalidArgument,
                                $"Invalid role ID format: '{roleIdStr}'. Expected a valid GUID."));
                        }
                        roleIds.Add(roleId);
                    }

                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.GetUsers retrieving users with {RoleFilterCount} role filters",
                        roleIds.Count);

                    var users = _securityManager.GetUsers(roleIds.ToArray());

                    var response = new GetUsersResponse { Success = true };
                    foreach (var user in users)
                    {
                        response.Users.Add(MapUserToProto(user));
                    }

                    return Task.FromResult(response);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(GetUsers), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing users list request."));
            }
        }

        #endregion

        #region Role Operations

        /// <summary>
        /// Retrieves all roles defined in the system.
        ///
        /// Source: SecurityManager.GetAllRoles()
        /// </summary>
        /// <param name="request">Empty request (no parameters required).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>GetAllRolesResponse with the complete list of role protos.</returns>
        public override Task<GetAllRolesResponse> GetAllRoles(
            GetAllRolesRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.GetAllRoles retrieving all roles");

                    var roles = _securityManager.GetAllRoles();

                    var response = new GetAllRolesResponse { Success = true };
                    foreach (var role in roles)
                    {
                        response.Roles.Add(MapRoleToProto(role));
                    }

                    return Task.FromResult(response);
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(GetAllRoles), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing roles list request."));
            }
        }

        #endregion

        #region Save Operations

        /// <summary>
        /// Creates or updates a user record with optional password.
        /// Validates username/email uniqueness and email format.
        /// Password field is separate from ErpUserProto for security — it is
        /// never returned in responses and only provided on save when needed.
        ///
        /// Source: SecurityManager.SaveUser(ErpUser)
        /// </summary>
        /// <param name="request">Request containing user proto and optional password string.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>SaveUserResponse with success/failure and validation errors.</returns>
        public override Task<SaveUserResponse> SaveUser(
            SaveUserRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    if (request.User == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "User data is required for save operation."));
                    }

                    var user = MapProtoToUser(request.User);

                    // Password is a separate field in the proto message (not part of ErpUserProto)
                    // for security — only set when creating or changing password
                    if (!string.IsNullOrEmpty(request.Password))
                    {
                        user.Password = request.Password;
                    }

                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.SaveUser saving user: {UserId}",
                        user.Id);

                    _securityManager.SaveUser(user);

                    return Task.FromResult(new SaveUserResponse
                    {
                        Success = true,
                        Message = "User saved successfully."
                    });
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (ValidationException valEx)
            {
                // Map validation errors from SecurityManager to proto error response
                var response = new SaveUserResponse
                {
                    Success = false,
                    Message = valEx.Message ?? "Validation failed."
                };
                MapValidationErrors(valEx.Errors, response.Errors);
                return Task.FromResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(SaveUser), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing user save request."));
            }
        }

        /// <summary>
        /// Creates or updates a role record.
        /// Validates role name uniqueness.
        ///
        /// Source: SecurityManager.SaveRole(ErpRole)
        /// </summary>
        /// <param name="request">Request containing role proto data.</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>SaveRoleResponse with success/failure and validation errors.</returns>
        public override Task<SaveRoleResponse> SaveRole(
            SaveRoleRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    if (request.Role == null)
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "Role data is required for save operation."));
                    }

                    var role = MapProtoToRole(request.Role);

                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.SaveRole saving role: {RoleId}",
                        role.Id);

                    _securityManager.SaveRole(role);

                    return Task.FromResult(new SaveRoleResponse
                    {
                        Success = true,
                        Message = "Role saved successfully."
                    });
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (ValidationException valEx)
            {
                // Map validation errors from SecurityManager to proto error response
                var response = new SaveRoleResponse
                {
                    Success = false,
                    Message = valEx.Message ?? "Validation failed."
                };
                MapValidationErrors(valEx.Errors, response.Errors);
                return Task.FromResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(SaveRole), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing role save request."));
            }
        }

        /// <summary>
        /// Updates the last login timestamp for a user to the current UTC time.
        /// Uses direct repository access for efficiency (bypasses full RecordManager
        /// pipeline and associated event publishing since this is a high-frequency
        /// operation during authentication flows).
        ///
        /// Source: SecurityManager.UpdateLastLoginAndModifiedDate(Guid userId)
        /// (renamed from monolith's UpdateUserLastLoginTime)
        /// </summary>
        /// <param name="request">Request containing user_id (GUID string).</param>
        /// <param name="context">gRPC server call context with JWT authentication metadata.</param>
        /// <returns>UpdateUserLastLoginResponse with success flag.</returns>
        public override Task<UpdateUserLastLoginResponse> UpdateUserLastLogin(
            UpdateUserLastLoginRequest request,
            ServerCallContext context)
        {
            try
            {
                using (SecurityContext.OpenScope(ExtractUserFromContext(context)))
                {
                    if (string.IsNullOrWhiteSpace(request.UserId))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            "User ID is required for last login update."));
                    }

                    if (!Guid.TryParse(request.UserId, out Guid userId))
                    {
                        throw new RpcException(new Status(
                            StatusCode.InvalidArgument,
                            $"Invalid user ID format: '{request.UserId}'. Expected a valid GUID."));
                    }

                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.UpdateUserLastLogin updating for user: {UserId}",
                        userId);

                    _securityManager.UpdateLastLoginAndModifiedDate(userId);

                    return Task.FromResult(new UpdateUserLastLoginResponse
                    {
                        Success = true
                    });
                }
            }
            catch (RpcException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(UpdateUserLastLogin), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing last login update request."));
            }
        }

        #endregion

        #region Token Validation

        /// <summary>
        /// Validates a JWT token for cross-service authentication and returns the
        /// decoded user identity and role information.
        ///
        /// Uses JwtTokenHandler from SharedKernel.Security to:
        /// 1. Validate the token signature, issuer, and audience
        /// 2. Extract the user ID from the NameIdentifier claim
        /// 3. Extract role IDs from the Role claims
        /// 4. Optionally indicate if the token needs proactive refresh
        ///
        /// Marked [AllowAnonymous] because this validates tokens for other services
        /// that may not yet have established their own authentication context.
        ///
        /// SECURITY: The token string is NEVER logged to prevent token leakage.
        /// Only the validation result (valid/invalid) and extracted user ID are logged.
        ///
        /// Per AAP 0.8.3: Enables downstream services to verify identity without
        /// direct DB access when the standard JWT middleware validation is insufficient
        /// (e.g., for token refresh decisions or cross-service token forwarding).
        /// </summary>
        /// <param name="request">Request containing the JWT token string to validate.</param>
        /// <param name="context">gRPC server call context.</param>
        /// <returns>ValidateTokenResponse with validity flag, user ID, role IDs, and status message.</returns>
        [AllowAnonymous]
        public override async Task<ValidateTokenResponse> ValidateToken(
            ValidateTokenRequest request,
            ServerCallContext context)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.JwtToken))
                {
                    return new ValidateTokenResponse
                    {
                        Valid = false,
                        Message = "Token is required for validation."
                    };
                }

                // Validate the JWT token using the shared handler
                var jwtToken = await _jwtTokenHandler.GetValidSecurityTokenAsync(request.JwtToken);

                if (jwtToken == null)
                {
                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.ValidateToken: token validation failed (invalid/expired)");

                    return new ValidateTokenResponse
                    {
                        Valid = false,
                        Message = "Token is invalid or expired."
                    };
                }

                // Extract user ID from the validated token
                Guid? userId = JwtTokenHandler.ExtractUserIdFromToken(jwtToken);

                if (!userId.HasValue)
                {
                    _logger.LogDebug(
                        "gRPC SecurityGrpcService.ValidateToken: token valid but no user ID claim found");

                    return new ValidateTokenResponse
                    {
                        Valid = false,
                        Message = "Token does not contain a valid user identifier claim."
                    };
                }

                // Extract role IDs from token claims
                var roleIds = jwtToken.Claims
                    .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
                    .Select(c => c.Value)
                    .Where(v => Guid.TryParse(v, out _))
                    .ToList();

                // Check if token needs refresh
                bool needsRefresh = JwtTokenHandler.IsTokenRefreshRequired(jwtToken);

                _logger.LogDebug(
                    "gRPC SecurityGrpcService.ValidateToken: token valid for user {UserId}, " +
                    "{RoleCount} roles, needsRefresh={NeedsRefresh}",
                    userId.Value, roleIds.Count, needsRefresh);

                var response = new ValidateTokenResponse
                {
                    Valid = true,
                    UserId = userId.Value.ToString(),
                    Message = needsRefresh ? "Token is valid but should be refreshed." : "Token is valid."
                };
                response.RoleIds.AddRange(roleIds);

                return response;
            }
            catch (Exception ex)
            {
                // Do NOT log the token string itself — security requirement
                _logger.LogError(ex,
                    "gRPC SecurityGrpcService.{MethodName} failed: {Message}",
                    nameof(ValidateToken), ex.Message);
                throw new RpcException(new Status(
                    StatusCode.Internal,
                    "Internal error processing token validation request."));
            }
        }

        #endregion

        #region Helper Methods — User/Context Extraction

        /// <summary>
        /// Extracts the authenticated user from the gRPC ServerCallContext's underlying
        /// HttpContext JWT claims. Uses SecurityContext.ExtractUserFromClaims to reconstruct
        /// an ErpUser from the JWT token claims populated by ASP.NET Core's JWT Bearer
        /// authentication middleware.
        ///
        /// Returns null if the user is not authenticated. The [Authorize] attribute on the
        /// class should prevent unauthenticated requests from reaching this point, but the
        /// null return provides defense-in-depth.
        /// </summary>
        /// <param name="context">The gRPC server call context containing the HttpContext.</param>
        /// <returns>An ErpUser reconstructed from JWT claims, or null if not authenticated.</returns>
        private static Models.ErpUser ExtractUserFromContext(ServerCallContext context)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                return SecurityContext.ExtractUserFromClaims(httpContext.User.Claims);
            }
            return null;
        }

        #endregion

        #region Helper Methods — Proto Mapping

        /// <summary>
        /// Maps a domain ErpUser model to the proto-generated ErpUserProto message.
        /// Includes all user properties AND role IDs for cross-service authorization.
        ///
        /// This replaces the JSON-based SerializeUserWithRoles approach described in the
        /// initial design — the structured proto message provides the same data (including
        /// roles that are [JsonIgnore] in ErpUser) in a type-safe format.
        ///
        /// Note: ErpUser.IsAdmin is a computed property (checks if any role matches
        /// AdministratorRoleId) and is not explicitly mapped — the receiver can compute
        /// it from the role_ids list.
        /// </summary>
        /// <param name="user">The domain ErpUser to map. Must not be null.</param>
        /// <returns>A fully populated ErpUserProto message.</returns>
        private static ErpUserProto MapUserToProto(Models.ErpUser user)
        {
            var proto = new ErpUserProto
            {
                Id = user.Id.ToString(),
                Username = user.Username ?? string.Empty,
                Email = user.Email ?? string.Empty,
                FirstName = user.FirstName ?? string.Empty,
                LastName = user.LastName ?? string.Empty,
                Image = user.Image ?? string.Empty,
                Enabled = user.Enabled,
                Verified = user.Verified
            };

            // Map CreatedOn to proto Timestamp (requires UTC)
            if (user.CreatedOn != default)
            {
                proto.CreatedOn = SafeToTimestamp(user.CreatedOn);
            }

            // Map nullable LastLoggedIn to proto Timestamp
            if (user.LastLoggedIn.HasValue)
            {
                proto.LastLoggedIn = SafeToTimestamp(user.LastLoggedIn.Value);
            }

            // Map roles as GUID strings — includes ALL roles, bypassing ErpUser's
            // [JsonIgnore] on the Roles property. Downstream services receive complete
            // role information for authorization decisions.
            if (user.Roles != null)
            {
                foreach (var role in user.Roles)
                {
                    proto.RoleIds.Add(role.Id.ToString());
                }
            }

            return proto;
        }

        /// <summary>
        /// Maps a domain ErpRole model to the proto-generated ErpRoleProto message.
        /// </summary>
        /// <param name="role">The domain ErpRole to map. Must not be null.</param>
        /// <returns>A fully populated ErpRoleProto message.</returns>
        private static ErpRoleProto MapRoleToProto(Models.ErpRole role)
        {
            return new ErpRoleProto
            {
                Id = role.Id.ToString(),
                Name = role.Name ?? string.Empty,
                Description = role.Description ?? string.Empty
            };
        }

        /// <summary>
        /// Maps a proto-generated ErpUserProto message back to a domain ErpUser model.
        /// Used for SaveUser operations where the client sends user data via proto message.
        ///
        /// Note: Password is NOT included in ErpUserProto — it is a separate field in
        /// SaveUserRequest for security. The caller must set user.Password from
        /// request.Password after calling this method.
        ///
        /// Role reconstruction: Only role IDs are available in the proto message (as strings).
        /// Roles are added with ID only; SecurityManager.SaveUser handles the $user_role
        /// relation assignment using only role IDs.
        /// </summary>
        /// <param name="proto">The proto user message to map. Must not be null.</param>
        /// <returns>A domain ErpUser populated from the proto message.</returns>
        private static Models.ErpUser MapProtoToUser(ErpUserProto proto)
        {
            var user = new Models.ErpUser
            {
                Username = proto.Username ?? string.Empty,
                Email = proto.Email ?? string.Empty,
                FirstName = proto.FirstName ?? string.Empty,
                LastName = proto.LastName ?? string.Empty,
                Image = proto.Image ?? string.Empty,
                Enabled = proto.Enabled,
                Verified = proto.Verified
            };

            // Parse user ID
            if (!string.IsNullOrWhiteSpace(proto.Id) && Guid.TryParse(proto.Id, out Guid userId))
            {
                user.Id = userId;
            }

            // Parse CreatedOn from proto Timestamp
            if (proto.CreatedOn != null)
            {
                user.CreatedOn = proto.CreatedOn.ToDateTime();
            }

            // Parse LastLoggedIn from proto Timestamp
            if (proto.LastLoggedIn != null)
            {
                user.LastLoggedIn = proto.LastLoggedIn.ToDateTime();
            }

            // Reconstruct roles from role ID strings
            // Roles are added with ID only — SecurityManager.SaveUser uses only role IDs
            // from the $user_role relation field assignment
            foreach (var roleIdStr in proto.RoleIds)
            {
                if (Guid.TryParse(roleIdStr, out Guid roleId))
                {
                    user.Roles.Add(new Models.ErpRole { Id = roleId });
                }
            }

            return user;
        }

        /// <summary>
        /// Maps a proto-generated ErpRoleProto message back to a domain ErpRole model.
        /// Used for SaveRole operations.
        /// </summary>
        /// <param name="proto">The proto role message to map. Must not be null.</param>
        /// <returns>A domain ErpRole populated from the proto message.</returns>
        private static Models.ErpRole MapProtoToRole(ErpRoleProto proto)
        {
            var role = new Models.ErpRole
            {
                Name = proto.Name ?? string.Empty,
                Description = proto.Description ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(proto.Id) && Guid.TryParse(proto.Id, out Guid roleId))
            {
                role.Id = roleId;
            }

            return role;
        }

        #endregion

        #region Helper Methods — Error Mapping

        /// <summary>
        /// Maps ValidationException errors from the SharedKernel to proto ErrorModel messages.
        /// Each ValidationError's PropertyName and Message are mapped to the proto ErrorModel's
        /// key and message fields respectively.
        /// </summary>
        /// <param name="validationErrors">Source validation errors from SecurityManager.</param>
        /// <param name="protoErrors">Target proto error collection to populate.</param>
        private static void MapValidationErrors(
            List<ValidationError> validationErrors,
            Google.Protobuf.Collections.RepeatedField<ProtoCommon.ErrorModel> protoErrors)
        {
            if (validationErrors == null)
                return;

            foreach (var error in validationErrors)
            {
                protoErrors.Add(new ProtoCommon.ErrorModel
                {
                    Key = error.PropertyName ?? string.Empty,
                    Message = error.Message ?? string.Empty
                });
            }
        }

        #endregion

        #region Helper Methods — Timestamp Conversion

        /// <summary>
        /// Safely converts a DateTime to a Google.Protobuf.WellKnownTypes.Timestamp.
        /// Handles both UTC and non-UTC DateTime values by converting to UTC first.
        /// Protobuf Timestamp requires UTC and will throw if given a non-UTC DateTime.
        /// </summary>
        /// <param name="dateTime">The DateTime to convert.</param>
        /// <returns>A Timestamp representing the same point in time.</returns>
        private static Timestamp SafeToTimestamp(DateTime dateTime)
        {
            // Ensure UTC — Timestamp.FromDateTime requires DateTimeKind.Utc
            DateTime utcDateTime = dateTime.Kind == DateTimeKind.Utc
                ? dateTime
                : dateTime.ToUniversalTime();

            return Timestamp.FromDateTime(utcDateTime);
        }

        #endregion

        #region Helper Methods — Serialization (for debug logging)

        /// <summary>
        /// Serializes a user object to JSON for debug logging purposes.
        /// Uses Newtonsoft.Json with NullValueHandling.Ignore for consistent output
        /// matching the monolith's contract stability requirement (AAP 0.8.2).
        ///
        /// This method respects ErpUser's [JsonIgnore] attributes — Password, Enabled,
        /// Verified, and Roles are excluded from the default serialization.
        /// For complete serialization including roles (bypassing [JsonIgnore]),
        /// use <see cref="SerializeUserWithRoles"/>.
        ///
        /// SECURITY: Password is always excluded via [JsonIgnore] on ErpUser.Password.
        /// </summary>
        /// <param name="user">The user to serialize. May be null.</param>
        /// <returns>JSON string representation of the user, or null if user is null.</returns>
        private static string SerializeUser(Models.ErpUser user)
        {
            if (user == null)
                return null;

            return JsonConvert.SerializeObject(user, JsonSettings);
        }

        /// <summary>
        /// Serializes a user object including roles to JSON, bypassing the [JsonIgnore]
        /// attribute on ErpUser.Roles. Creates an anonymous DTO that includes all user
        /// properties needed by downstream services for authorization decisions:
        /// Id, Username, Email, FirstName, LastName, Image, Enabled, Verified, CreatedOn,
        /// LastLoggedIn, IsAdmin (computed), and Roles (with Id, Name, Description).
        ///
        /// Uses camelCase property names matching the [JsonProperty] annotations on ErpUser.
        ///
        /// SECURITY: Password is explicitly excluded from the DTO.
        /// </summary>
        /// <param name="user">The user to serialize. May be null.</param>
        /// <returns>JSON string with complete user profile including roles, or null.</returns>
        private static string SerializeUserWithRoles(Models.ErpUser user)
        {
            if (user == null)
                return null;

            // Create an anonymous DTO that includes roles since [JsonIgnore] hides
            // them in the default Newtonsoft.Json serialization of ErpUser
            var dto = new
            {
                id = user.Id,
                username = user.Username,
                email = user.Email,
                firstName = user.FirstName,
                lastName = user.LastName,
                image = user.Image,
                enabled = user.Enabled,
                verified = user.Verified,
                createdOn = user.CreatedOn,
                lastLoggedIn = user.LastLoggedIn,
                is_admin = user.IsAdmin,
                roles = user.Roles?.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    description = r.Description
                })
            };

            return JsonConvert.SerializeObject(dto, JsonSettings);
        }

        #endregion
    }
}
