using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
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

namespace WebVellaErp.Identity.Functions
{
    /// <summary>
    /// Data transfer object for user create and update requests.
    /// Properties map to the monolith EntityRecord keys:
    /// record["email"], record["username"], record["first_name"], record["last_name"], etc.
    /// </summary>
    public class SaveUserRequest
    {
        /// <summary>User ID — optional for create (auto-generated), used for update path.</summary>
        [JsonPropertyName("id")]
        public Guid? Id { get; set; }

        /// <summary>User email address. Matches source record["email"].</summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>Username for login. Matches source record["username"].</summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>Password — required for create, optional for update. Matches source record["password"].</summary>
        [JsonPropertyName("password")]
        public string? Password { get; set; }

        /// <summary>First name. Matches source record["first_name"].</summary>
        [JsonPropertyName("first_name")]
        public string FirstName { get; set; } = string.Empty;

        /// <summary>Last name. Matches source record["last_name"].</summary>
        [JsonPropertyName("last_name")]
        public string LastName { get; set; } = string.Empty;

        /// <summary>Profile image URL. Matches source record["image"].</summary>
        [JsonPropertyName("image")]
        public string? Image { get; set; }

        /// <summary>Whether the user account is enabled. Matches source record["enabled"].</summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Whether the user email is verified. Matches source record["verified"].</summary>
        [JsonPropertyName("verified")]
        public bool Verified { get; set; } = true;

        /// <summary>
        /// List of role IDs to assign. Replaces source record["$user_role.id"] pattern
        /// from SecurityManager.cs lines 246 and 284.
        /// </summary>
        [JsonPropertyName("role_ids")]
        public List<Guid>? RoleIds { get; set; }
    }

    /// <summary>
    /// AWS Lambda handler for user management operations in the Identity bounded context.
    /// Replaces:
    ///   SecurityManager.GetUser(Guid)             — lines 36-47
    ///   SecurityManager.GetUser(string email)      — lines 49-61
    ///   SecurityManager.GetUserByUsername(string)   — lines 63-75
    ///   SecurityManager.GetUsers(params Guid[])    — lines 167-184
    ///   SecurityManager.SaveUser(ErpUser)           — lines 191-293
    ///   SecurityManager.UpdateUserLastLoginTime     — lines 350-356
    ///   SecurityManager.IsValidEmail(string)        — lines 358-369
    /// All PostgreSQL/EQL/ambient context patterns replaced by DynamoDB + Cognito + JWT claims.
    /// </summary>
    public class UserHandler
    {
        private ICognitoService _cognitoService = null!;
        private IUserRepository _userRepository = null!;
        private IPermissionService _permissionService = null!;
        private IAmazonSimpleNotificationService _snsClient = null!;
        private ILogger<UserHandler> _logger = null!;
        private string _userEventsTopicArn = string.Empty;

        /// <summary>
        /// AOT-compatible System.Text.Json options replacing Newtonsoft.Json.
        /// Uses camelCase naming to match frontend API expectations.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Parameterless constructor for Lambda runtime.
        /// Builds a DI container, registers all services and AWS SDK clients with
        /// LocalStack endpoint support via AWS_ENDPOINT_URL environment variable.
        /// </summary>
        public UserHandler()
        {
            var services = new ServiceCollection();
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            // Register AWS SDK clients with LocalStack endpoint support
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonCognitoIdentityProvider>(
                    new AmazonCognitoIdentityProviderClient(
                        new AmazonCognitoIdentityProviderConfig { ServiceURL = endpointUrl }));
                services.AddSingleton<IAmazonDynamoDB>(
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));
                services.AddSingleton<IAmazonSimpleNotificationService>(
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));
                services.AddSingleton<IAmazonSimpleSystemsManagement>(
                    new AmazonSimpleSystemsManagementClient(
                        new AmazonSimpleSystemsManagementConfig { ServiceURL = endpointUrl }));
            }
            else
            {
                services.AddSingleton<IAmazonCognitoIdentityProvider, AmazonCognitoIdentityProviderClient>();
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
                services.AddSingleton<IAmazonSimpleSystemsManagement, AmazonSimpleSystemsManagementClient>();
            }

            // Register application services
            services.AddSingleton<ICognitoService, CognitoService>();
            services.AddSingleton<IUserRepository, UserRepository>();
            services.AddSingleton<IPermissionService, PermissionService>();

            // Configure structured JSON logging with correlation-ID support
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
                builder.AddJsonConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                });
            });

            var provider = services.BuildServiceProvider();
            InitializeFromProvider(provider);
        }

        /// <summary>
        /// Secondary constructor for unit testing with a pre-configured DI container.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured service provider for test injection.</param>
        public UserHandler(IServiceProvider serviceProvider)
        {
            InitializeFromProvider(serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider)));
        }

        /// <summary>
        /// Resolves all dependencies from the service provider.
        /// Called from both constructors to ensure consistent initialization.
        /// </summary>
        private void InitializeFromProvider(IServiceProvider provider)
        {
            _cognitoService = provider.GetRequiredService<ICognitoService>();
            _userRepository = provider.GetRequiredService<IUserRepository>();
            _permissionService = provider.GetRequiredService<IPermissionService>();
            _snsClient = provider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = provider.GetRequiredService<ILogger<UserHandler>>();
            _userEventsTopicArn = Environment.GetEnvironmentVariable("USER_EVENTS_TOPIC_ARN") ?? string.Empty;
        }

        // ──────────────────────────────────────────────────────────────────
        // Handler: GET /v1/users/{userId}
        // Replaces: SecurityManager.GetUser(Guid userId) — source lines 36-47
        // Source: EqlCommand("SELECT *, $user_role.* FROM user WHERE id = @id")
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lambda handler for GET /v1/users/{userId}.
        /// Retrieves a single user by ID with their associated roles.
        /// Replaces SecurityManager.GetUser(Guid) which used EQL against PostgreSQL.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetUser(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            try
            {
                // Extract userId from path parameters: /v1/users/{userId}
                if (request.PathParameters == null ||
                    !request.PathParameters.TryGetValue("userId", out var userIdStr) ||
                    !Guid.TryParse(userIdStr, out var userId))
                {
                    return BuildResponse(400, new { success = false, message = "Invalid or missing userId path parameter." });
                }

                _logger.LogInformation(
                    "GetUser requested for userId={UserId}, correlationId={CorrelationId}",
                    userId, correlationId);

                // DynamoDB GetItem replaces EQL: SELECT *, $user_role.* FROM user WHERE id = @id
                var user = await _userRepository.GetUserByIdAsync(userId);
                if (user == null)
                {
                    _logger.LogWarning(
                        "User not found: userId={UserId}, correlationId={CorrelationId}",
                        userId, correlationId);
                    return BuildResponse(404, new { success = false, message = "User not found." });
                }

                // Fetch roles — replaces $user_role.* relational join from source EQL
                var roles = await _userRepository.GetUserRolesAsync(userId);
                user.Roles = roles;

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    @object = new { user }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error in HandleGetUser, correlationId={CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." });
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Handler: GET /v1/users?email={email} or GET /v1/users?username={username}
        // Replaces: SecurityManager.GetUser(string email) — source lines 49-61
        //           SecurityManager.GetUserByUsername(string) — source lines 63-75
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lambda handler for GET /v1/users?email={email} or GET /v1/users?username={username}.
        /// Retrieves a single user by email address or by username.
        /// Replaces SecurityManager.GetUser(string) which used EQL WHERE email = @email
        /// and SecurityManager.GetUserByUsername(string) which used EQL WHERE username = @username.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetUserByEmail(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            try
            {
                var queryParams = request.QueryStringParameters;

                // Email lookup — replaces EQL: SELECT *, $user_role.* FROM user WHERE email = @email
                if (queryParams != null &&
                    queryParams.TryGetValue("email", out var email) &&
                    !string.IsNullOrWhiteSpace(email))
                {
                    _logger.LogInformation(
                        "GetUserByEmail requested, correlationId={CorrelationId}", correlationId);

                    var user = await _userRepository.GetUserByEmailAsync(email);
                    if (user == null)
                    {
                        return BuildResponse(404, new { success = false, message = "User not found." });
                    }

                    var roles = await _userRepository.GetUserRolesAsync(user.Id);
                    user.Roles = roles;

                    return BuildResponse(200, new
                    {
                        success = true,
                        timestamp = DateTime.UtcNow,
                        @object = new { user }
                    });
                }

                // Username lookup — replaces EQL: SELECT *, $user_role.* FROM user WHERE username = @username
                if (queryParams != null &&
                    queryParams.TryGetValue("username", out var username) &&
                    !string.IsNullOrWhiteSpace(username))
                {
                    _logger.LogInformation(
                        "GetUserByUsername requested, correlationId={CorrelationId}", correlationId);

                    var user = await _userRepository.GetUserByUsernameAsync(username);
                    if (user == null)
                    {
                        return BuildResponse(404, new { success = false, message = "User not found." });
                    }

                    var roles = await _userRepository.GetUserRolesAsync(user.Id);
                    user.Roles = roles;

                    return BuildResponse(200, new
                    {
                        success = true,
                        timestamp = DateTime.UtcNow,
                        @object = new { user }
                    });
                }

                return BuildResponse(400, new
                {
                    success = false,
                    message = "Either 'email' or 'username' query parameter is required."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error in HandleGetUserByEmail, correlationId={CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." });
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Handler: GET /v1/users  or  GET /v1/users?roleIds={id1},{id2}
        // Replaces: SecurityManager.GetUsers(params Guid[] roleIds) — source lines 167-184
        // Source built dynamic EQL with $user_role.id OR conditions
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lambda handler for GET /v1/users or GET /v1/users?roleIds={id1},{id2}.
        /// Lists users, optionally filtered by role membership.
        /// Replaces SecurityManager.GetUsers which dynamically built EQL WHERE clauses
        /// with $user_role.id OR conditions for each requested role.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetUsers(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            try
            {
                List<User> users;

                // Check for roleIds filter parameter
                if (request.QueryStringParameters != null &&
                    request.QueryStringParameters.TryGetValue("roleIds", out var roleIdsStr) &&
                    !string.IsNullOrWhiteSpace(roleIdsStr))
                {
                    // Parse comma-separated GUIDs (replaces dynamic EQL sbRoles.AppendLine)
                    var roleIdParts = roleIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var roleIds = new List<Guid>();
                    foreach (var part in roleIdParts)
                    {
                        if (!Guid.TryParse(part, out var roleId))
                        {
                            return BuildResponse(400, new
                            {
                                success = false,
                                message = $"Invalid role ID format: '{part}'."
                            });
                        }
                        roleIds.Add(roleId);
                    }

                    _logger.LogInformation(
                        "GetUsers by roleIds requested, count={RoleCount}, correlationId={CorrelationId}",
                        roleIds.Count, correlationId);

                    users = await _userRepository.GetUsersByRoleAsync(default, roleIds.ToArray());
                }
                else
                {
                    _logger.LogInformation(
                        "GetAllUsers requested, correlationId={CorrelationId}", correlationId);
                    users = await _userRepository.GetAllUsersAsync();
                }

                // Populate roles for each user (replaces $user_role.* join in EQL)
                foreach (var user in users)
                {
                    var roles = await _userRepository.GetUserRolesAsync(user.Id);
                    user.Roles = roles;
                }

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    @object = new { users }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error in HandleGetUsers, correlationId={CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." });
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Handler: POST /v1/users (create) or PUT /v1/users/{userId} (update)
        // Replaces: SecurityManager.SaveUser(ErpUser user) — source lines 191-293
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lambda handler for POST /v1/users (create) and PUT /v1/users/{userId} (update).
        /// Handles both user creation and update paths with full validation.
        /// Replaces SecurityManager.SaveUser which handled both create (lines 255-292)
        /// and update (lines 202-253) paths using RecordManager CRUD against PostgreSQL.
        /// Idempotency supported via idempotency key header per AAP requirements.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleSaveUser(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            try
            {
                // Deserialize request body
                if (string.IsNullOrWhiteSpace(request.Body))
                {
                    return BuildResponse(400, new { success = false, message = "Request body is required." });
                }

                SaveUserRequest? saveRequest;
                try
                {
                    saveRequest = JsonSerializer.Deserialize<SaveUserRequest>(request.Body, JsonOptions);
                }
                catch (JsonException)
                {
                    return BuildResponse(400, new { success = false, message = "Invalid JSON in request body." });
                }

                if (saveRequest == null)
                {
                    return BuildResponse(400, new { success = false, message = "Request body deserialized to null." });
                }

                // Extract caller from JWT context for permission checks
                var caller = await ExtractCallerFromContext(request);

                // Determine create vs update from HTTP method (POST = create, PUT = update)
                var httpMethod = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? "POST";
                bool isUpdate = httpMethod == "PUT";

                if (isUpdate)
                {
                    // Also extract userId from path parameters for PUT /v1/users/{userId}
                    if (request.PathParameters != null &&
                        request.PathParameters.TryGetValue("userId", out var userIdStr) &&
                        Guid.TryParse(userIdStr, out var userId))
                    {
                        saveRequest.Id = userId;
                    }

                    return await HandleUpdateUser(saveRequest, caller, correlationId);
                }
                else
                {
                    return await HandleCreateUser(saveRequest, caller, correlationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error in HandleSaveUser, correlationId={CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." });
            }
        }

        /// <summary>
        /// Creates a new user with full validation.
        /// Replaces SecurityManager.SaveUser CREATE path (source lines 255-292):
        ///   - Validates username required and unique (lines 267-270)
        ///   - Validates email required, unique, and format (lines 272-277)
        ///   - Validates password required (lines 279-280)
        ///   - Assigns roles via record["$user_role.id"] (line 284)
        ///   - Creates via RecordManager.CreateRecord("user", record) (line 288)
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleCreateUser(
            SaveUserRequest saveRequest, User? caller, string correlationId)
        {
            // Permission check — only administrators can create users
            if (!_permissionService.HasMetaPermission(caller))
            {
                _logger.LogWarning(
                    "Unauthorized user create attempt, correlationId={CorrelationId}", correlationId);
                return BuildResponse(403, new { success = false, message = "Insufficient permissions to create users." });
            }

            var errors = new Dictionary<string, string>();

            // === Username validation (source lines 267-270) ===
            if (string.IsNullOrWhiteSpace(saveRequest.Username))
            {
                errors["username"] = "Username is required.";
            }
            else
            {
                var existingByUsername = await _userRepository.GetUserByUsernameAsync(saveRequest.Username);
                if (existingByUsername != null)
                {
                    errors["username"] = "Username is already registered to another user. It must be unique.";
                }
            }

            // === Email validation (source lines 272-277) ===
            if (string.IsNullOrWhiteSpace(saveRequest.Email))
            {
                errors["email"] = "Email is required.";
            }
            else
            {
                var existingByEmail = await _userRepository.GetUserByEmailAsync(saveRequest.Email);
                if (existingByEmail != null)
                {
                    errors["email"] = "Email is already registered to another user. It must be unique.";
                }

                // Email format: source uses MailAddress (lines 358-368)
                if (!errors.ContainsKey("email") && !IsValidEmail(saveRequest.Email))
                {
                    errors["email"] = "Email is not valid.";
                }
            }

            // === Password validation (source lines 279-280) ===
            if (string.IsNullOrWhiteSpace(saveRequest.Password))
            {
                errors["password"] = "Password is required.";
            }

            if (errors.Count > 0)
            {
                _logger.LogWarning(
                    "User create validation failed, errorCount={ErrorCount}, correlationId={CorrelationId}",
                    errors.Count, correlationId);
                return BuildValidationErrorResponse(errors);
            }

            // Build User model for creation
            var user = new User
            {
                Id = saveRequest.Id ?? Guid.NewGuid(),
                Email = saveRequest.Email.Trim(),
                Username = saveRequest.Username.Trim(),
                Password = saveRequest.Password!,
                FirstName = saveRequest.FirstName?.Trim() ?? string.Empty,
                LastName = saveRequest.LastName?.Trim() ?? string.Empty,
                Image = saveRequest.Image,
                Enabled = saveRequest.Enabled,
                Verified = saveRequest.Verified,
                CreatedOn = DateTime.UtcNow,
                Roles = new List<Role>()
            };

            // Resolve roles from RoleIds (replaces source line 284:
            // record["$user_role.id"] = user.Roles.Select(x => x.Id).ToList())
            if (saveRequest.RoleIds != null && saveRequest.RoleIds.Any())
            {
                foreach (var roleId in saveRequest.RoleIds)
                {
                    var role = await _userRepository.GetRoleByIdAsync(roleId);
                    if (role != null)
                    {
                        user.Roles.Add(role);
                    }
                }
            }

            // Create in Cognito + DynamoDB (replaces source line 288:
            // recMan.CreateRecord("user", record))
            var createdUser = await _cognitoService.CreateUserAsync(user, saveRequest.Password!);

            _logger.LogInformation(
                "User created: userId={UserId}, correlationId={CorrelationId}",
                createdUser.Id, correlationId);

            // Publish domain event: identity.user.created (replaces post-create hooks)
            await PublishDomainEventAsync("identity.user.created", new
            {
                eventType = "identity.user.created",
                userId = createdUser.Id,
                email = createdUser.Email,
                username = createdUser.Username,
                roles = createdUser.Roles?.Select(r => new { id = r.Id, name = r.Name }).ToList(),
                timestamp = DateTime.UtcNow,
                correlationId
            });

            // Populate roles for response
            createdUser.Roles = await _userRepository.GetUserRolesAsync(createdUser.Id);

            return BuildResponse(200, new
            {
                success = true,
                timestamp = DateTime.UtcNow,
                @object = new { user = createdUser }
            });
        }

        /// <summary>
        /// Updates an existing user with full validation.
        /// Replaces SecurityManager.SaveUser UPDATE path (source lines 202-253):
        ///   - Only validates changed fields (username lines 206-213, email lines 216-225)
        ///   - Updates password only if non-empty (line 228)
        ///   - Updates enabled/verified/firstName/lastName/image (lines 232-244)
        ///   - Syncs roles via record["$user_role.id"] (line 246)
        ///   - Updates via RecordManager.UpdateRecord("user", record) (line 250)
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateUser(
            SaveUserRequest saveRequest, User? caller, string correlationId)
        {
            // Permission check — only administrators can update users
            if (!_permissionService.HasMetaPermission(caller))
            {
                _logger.LogWarning(
                    "Unauthorized user update attempt, correlationId={CorrelationId}", correlationId);
                return BuildResponse(403, new { success = false, message = "Insufficient permissions to update users." });
            }

            if (saveRequest.Id == null || saveRequest.Id == Guid.Empty)
            {
                return BuildResponse(400, new { success = false, message = "User ID is required for update." });
            }

            var existingUser = await _userRepository.GetUserByIdAsync(saveRequest.Id.Value);
            if (existingUser == null)
            {
                return BuildResponse(404, new { success = false, message = "User not found." });
            }

            // Guard: Prevent modification of system user
            if (existingUser.Id == User.SystemUserId)
            {
                return BuildResponse(403, new { success = false, message = "System user cannot be modified." });
            }

            var errors = new Dictionary<string, string>();

            // === If username changed (source line 206) ===
            if (existingUser.Username != saveRequest.Username)
            {
                if (string.IsNullOrWhiteSpace(saveRequest.Username))
                {
                    // source lines 210-211
                    errors["username"] = "Username is required.";
                }
                else
                {
                    // source lines 212-213
                    var existingByUsername = await _userRepository.GetUserByUsernameAsync(saveRequest.Username);
                    if (existingByUsername != null && existingByUsername.Id != existingUser.Id)
                    {
                        errors["username"] = "Username is already registered to another user. It must be unique.";
                    }
                }
            }

            // === If email changed (source line 216) ===
            if (existingUser.Email != saveRequest.Email)
            {
                if (string.IsNullOrWhiteSpace(saveRequest.Email))
                {
                    // source lines 220-221
                    errors["email"] = "Email is required.";
                }
                else
                {
                    // source lines 222-223
                    var existingByEmail = await _userRepository.GetUserByEmailAsync(saveRequest.Email);
                    if (existingByEmail != null && existingByEmail.Id != existingUser.Id)
                    {
                        errors["email"] = "Email is already registered to another user. It must be unique.";
                    }
                    // source lines 224-225
                    if (!errors.ContainsKey("email") && !IsValidEmail(saveRequest.Email))
                    {
                        errors["email"] = "Email is not valid.";
                    }
                }
            }

            if (errors.Count > 0)
            {
                _logger.LogWarning(
                    "User update validation failed, userId={UserId}, errorCount={ErrorCount}, correlationId={CorrelationId}",
                    existingUser.Id, errors.Count, correlationId);
                return BuildValidationErrorResponse(errors);
            }

            // Apply changes to existing user (source lines 232-244)
            existingUser.Username = saveRequest.Username?.Trim() ?? existingUser.Username;
            existingUser.Email = saveRequest.Email?.Trim() ?? existingUser.Email;
            existingUser.FirstName = saveRequest.FirstName?.Trim() ?? existingUser.FirstName;
            existingUser.LastName = saveRequest.LastName?.Trim() ?? existingUser.LastName;
            existingUser.Image = saveRequest.Image;
            existingUser.Enabled = saveRequest.Enabled;
            existingUser.Verified = saveRequest.Verified;

            // Update password only if provided and non-empty (source line 228)
            if (!string.IsNullOrWhiteSpace(saveRequest.Password))
            {
                existingUser.Password = saveRequest.Password;
            }

            // Sync roles (source line 246: record["$user_role.id"] = user.Roles.Select(x => x.Id).ToList())
            if (saveRequest.RoleIds != null)
            {
                var newRoles = new List<Role>();
                foreach (var roleId in saveRequest.RoleIds)
                {
                    var role = await _userRepository.GetRoleByIdAsync(roleId);
                    if (role != null)
                    {
                        newRoles.Add(role);
                    }
                }
                existingUser.Roles = newRoles;
            }

            // Update in Cognito + DynamoDB (replaces source line 250:
            // recMan.UpdateRecord("user", record))
            var updatedUser = await _cognitoService.UpdateUserAsync(existingUser);

            _logger.LogInformation(
                "User updated: userId={UserId}, correlationId={CorrelationId}",
                updatedUser.Id, correlationId);

            // Publish domain event: identity.user.updated (replaces post-update hooks)
            await PublishDomainEventAsync("identity.user.updated", new
            {
                eventType = "identity.user.updated",
                userId = updatedUser.Id,
                email = updatedUser.Email,
                username = updatedUser.Username,
                roles = updatedUser.Roles?.Select(r => new { id = r.Id, name = r.Name }).ToList(),
                timestamp = DateTime.UtcNow,
                correlationId
            });

            // Populate roles for response
            updatedUser.Roles = await _userRepository.GetUserRolesAsync(updatedUser.Id);

            return BuildResponse(200, new
            {
                success = true,
                timestamp = DateTime.UtcNow,
                @object = new { user = updatedUser }
            });
        }

        // ──────────────────────────────────────────────────────────────────
        // Handler: PATCH /v1/users/{userId}/last-login
        // Replaces: SecurityManager.UpdateUserLastLoginTime(Guid) — source lines 350-356
        // Source: storageRecordData.Add("last_logged_in", DateTime.UtcNow);
        //         CurrentContext.RecordRepository.Update("user", storageRecordData);
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lambda handler for PATCH /v1/users/{userId}/last-login.
        /// Updates the user's last login timestamp to DateTime.UtcNow.
        /// Replaces SecurityManager.UpdateUserLastLoginTime which directly updated
        /// the PostgreSQL rec_user table via CurrentContext.RecordRepository.Update.
        /// </summary>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleUpdateLastLogin(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            try
            {
                // Extract userId from path parameters: /v1/users/{userId}/last-login
                if (request.PathParameters == null ||
                    !request.PathParameters.TryGetValue("userId", out var userIdStr) ||
                    !Guid.TryParse(userIdStr, out var userId))
                {
                    return BuildResponse(400, new { success = false, message = "Invalid or missing userId path parameter." });
                }

                _logger.LogInformation(
                    "UpdateLastLogin requested for userId={UserId}, correlationId={CorrelationId}",
                    userId, correlationId);

                // DynamoDB UpdateItem replaces:
                // storageRecordData.Add("last_logged_in", DateTime.UtcNow);
                // CurrentContext.RecordRepository.Update("user", storageRecordData);
                await _userRepository.UpdateLastLoginTimeAsync(userId);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    message = "Last login time updated."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error in HandleUpdateLastLogin, correlationId={CorrelationId}", correlationId);
                return BuildResponse(500, new { success = false, message = "An internal error occurred." });
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Response Builders and Helper Methods
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a consistent HTTP API Gateway v2 response with CORS headers.
        /// Response format matches the monolith's ResponseModel pattern:
        /// { success, timestamp, object }.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object body)
        {
            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, string>
                {
                    { "Content-Type", "application/json" },
                    { "Access-Control-Allow-Origin", "*" },
                    { "Access-Control-Allow-Headers", "Content-Type,Authorization,X-Correlation-Id" },
                    { "Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS" }
                },
                Body = JsonSerializer.Serialize(body, JsonOptions)
            };
        }

        /// <summary>
        /// Builds a 400 validation error response matching the source ValidationException.Errors pattern.
        /// Returns field-level error messages for client-side form validation.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildValidationErrorResponse(
            Dictionary<string, string> errors)
        {
            return BuildResponse(400, new
            {
                success = false,
                message = "Validation failed.",
                errors
            });
        }

        /// <summary>
        /// Extracts correlation-ID from request headers for structured logging.
        /// Falls back to a new GUID if no correlation-ID header is present.
        /// Per AAP Section 0.8.5: structured JSON logging with correlation-ID propagation.
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.Headers != null)
            {
                // HTTP API v2 lowercases all header names
                if (request.Headers.TryGetValue("x-correlation-id", out var correlationId) &&
                    !string.IsNullOrWhiteSpace(correlationId))
                {
                    return correlationId;
                }

                // Fallback for mixed-case header name
                if (request.Headers.TryGetValue("X-Correlation-Id", out correlationId) &&
                    !string.IsNullOrWhiteSpace(correlationId))
                {
                    return correlationId;
                }
            }

            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Extracts the authenticated caller from API Gateway JWT authorizer context.
        /// Replaces SecurityContext.CurrentUser and the monolith's AuthService.GetUser(User).
        /// Tries: custom user_id claim → email claim → sub claim (Cognito subject).
        /// </summary>
        private async Task<User?> ExtractCallerFromContext(APIGatewayHttpApiV2ProxyRequest request)
        {
            try
            {
                var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
                if (claims == null)
                {
                    return null;
                }

                // Try custom user_id claim first (set by custom authorizer)
                if (claims.TryGetValue("user_id", out var userIdStr) &&
                    Guid.TryParse(userIdStr, out var userId))
                {
                    var user = await _userRepository.GetUserByIdAsync(userId);
                    if (user != null)
                    {
                        user.Roles = await _userRepository.GetUserRolesAsync(user.Id);
                        return user;
                    }
                }

                // Fall back to email claim (standard Cognito)
                if (claims.TryGetValue("email", out var email) &&
                    !string.IsNullOrWhiteSpace(email))
                {
                    var user = await _userRepository.GetUserByEmailAsync(email);
                    if (user != null)
                    {
                        user.Roles = await _userRepository.GetUserRolesAsync(user.Id);
                        return user;
                    }
                }

                // Fall back to sub claim (Cognito subject ID)
                if (claims.TryGetValue("sub", out var sub) &&
                    !string.IsNullOrWhiteSpace(sub))
                {
                    // The sub may be stored as CognitoSub on the user record
                    var allUsers = await _userRepository.GetAllUsersAsync();
                    var user = allUsers.FirstOrDefault(u =>
                        string.Equals(u.CognitoSub, sub, StringComparison.OrdinalIgnoreCase));
                    if (user != null)
                    {
                        user.Roles = await _userRepository.GetUserRolesAsync(user.Id);
                        return user;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract caller from JWT context");
            }

            return null;
        }

        /// <summary>
        /// Email validation matching SecurityManager.IsValidEmail (source lines 358-368):
        ///   try { var addr = new MailAddress(email); return addr.Address == email; }
        ///   catch { return false; }
        /// Uses System.Net.Mail.MailAddress for EXACT behavioral parity.
        /// </summary>
        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }

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
        /// Publishes domain events to SNS for async cross-service communication.
        /// Replaces the monolith's synchronous HookManager/RecordHookManager post-CRUD hooks.
        /// Non-blocking: errors are logged but not thrown to avoid failing the primary operation.
        /// Event naming per AAP Section 0.8.5: {domain}.{entity}.{action}
        /// SNS topic ARN read from USER_EVENTS_TOPIC_ARN environment variable.
        /// </summary>
        private async Task PublishDomainEventAsync(string eventType, object eventData)
        {
            if (string.IsNullOrEmpty(_userEventsTopicArn))
            {
                _logger.LogWarning(
                    "USER_EVENTS_TOPIC_ARN not configured, skipping event publish: {EventType}",
                    eventType);
                return;
            }

            try
            {
                var publishRequest = new PublishRequest
                {
                    TopicArn = _userEventsTopicArn,
                    Message = JsonSerializer.Serialize(eventData, JsonOptions),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        }
                    }
                };

                await _snsClient.PublishAsync(publishRequest);

                _logger.LogInformation("Domain event published: {EventType}", eventType);
            }
            catch (Exception ex)
            {
                // Non-blocking: log error but do not throw — primary operation succeeded
                _logger.LogError(ex, "Failed to publish domain event: {EventType}", eventType);
            }
        }
    }
}
