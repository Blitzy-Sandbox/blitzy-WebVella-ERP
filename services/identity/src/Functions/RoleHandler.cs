using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.DynamoDBv2;
using Amazon.CognitoIdentityProvider;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Identity.DataAccess;
using WebVellaErp.Identity.Models;
using WebVellaErp.Identity.Services;

// Assembly-level JSON serializer for Lambda runtime (AOT-compatible System.Text.Json).
// Only ONE file per assembly should declare this attribute; other handlers must not duplicate it.
[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Identity.Functions;

/// <summary>
/// Request DTO for creating or updating a role.
/// Maps to the monolith's EntityRecord keys used in SecurityManager.SaveRole().
/// </summary>
public class SaveRoleRequest
{
    /// <summary>
    /// Role identifier. Optional for create (auto-generated if null/empty), required for update.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Role name. Required for create, validated for uniqueness. Maps to source record["name"].
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Role description. Defaults to empty string when null (source line 305-306).
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// AWS Lambda handler for role management in the Identity &amp; Access Management bounded context.
/// Replaces SecurityManager.SaveRole() (lines 295-347) and SecurityManager.GetAllRoles() (lines 186-189).
/// Roles map to Cognito user pool groups and are persisted in DynamoDB for extended metadata.
/// All write operations publish domain events to SNS for cross-service notification.
/// </summary>
public class RoleHandler
{
    private readonly IUserRepository _userRepository;
    private readonly IPermissionService _permissionService;
    private readonly ICognitoService _cognitoService;
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly ILogger<RoleHandler> _logger;

    /// <summary>
    /// AOT-compatible JSON serializer options with camelCase naming.
    /// Replaces Newtonsoft.Json per import transformation rules.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
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
    /// Well-known system role IDs that cannot be deleted or renamed.
    /// From Definitions.cs lines 15-17.
    /// </summary>
    private static readonly HashSet<Guid> SystemRoleIds = new()
    {
        Role.AdministratorRoleId,
        Role.RegularRoleId,
        Role.GuestRoleId
    };

    /// <summary>
    /// Parameterless constructor for Lambda runtime instantiation.
    /// Builds a DI container with all required services and AWS SDK clients.
    /// </summary>
    public RoleHandler()
    {
        var services = new ServiceCollection();

        // Structured logging for CloudWatch capture
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

        // Domain services
        services.AddSingleton<IPermissionService, PermissionService>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<ICognitoService, CognitoService>();

        var serviceProvider = services.BuildServiceProvider();
        _userRepository = serviceProvider.GetRequiredService<IUserRepository>();
        _permissionService = serviceProvider.GetRequiredService<IPermissionService>();
        _cognitoService = serviceProvider.GetRequiredService<ICognitoService>();
        _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
        _logger = serviceProvider.GetRequiredService<ILogger<RoleHandler>>();
    }

    /// <summary>
    /// Constructor for unit testing — accepts a pre-configured IServiceProvider.
    /// </summary>
    /// <param name="serviceProvider">Pre-configured service provider with all dependencies.</param>
    public RoleHandler(IServiceProvider serviceProvider)
    {
        _userRepository = serviceProvider.GetRequiredService<IUserRepository>();
        _permissionService = serviceProvider.GetRequiredService<IPermissionService>();
        _cognitoService = serviceProvider.GetRequiredService<ICognitoService>();
        _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
        _logger = serviceProvider.GetRequiredService<ILogger<RoleHandler>>();
    }

    /// <summary>
    /// GET /v1/roles — Retrieves all roles.
    /// Replaces SecurityManager.GetAllRoles() which executed EQL: "SELECT * FROM role".
    /// </summary>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetAllRoles(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        try
        {
            _logger.LogInformation(
                "GetAllRoles invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
                correlationId, context.AwsRequestId);

            var roles = await _userRepository.GetAllRolesAsync(CancellationToken.None);

            _logger.LogInformation(
                "GetAllRoles completed. RoleCount={RoleCount}, CorrelationId={CorrelationId}",
                roles.Count, correlationId);

            return BuildResponse(200, new
            {
                success = true,
                timestamp = DateTime.UtcNow,
                @object = roles,
                correlationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GetAllRoles failed. CorrelationId={CorrelationId}, Error={Error}",
                correlationId, ex.Message);

            return BuildResponse(500, new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                message = "An internal error occurred while retrieving roles.",
                correlationId
            });
        }
    }

    /// <summary>
    /// GET /v1/roles/{roleId} — Retrieves a single role by ID.
    /// Added for RESTful completeness; the monolith queried roles by iterating GetAllRoles.
    /// </summary>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleGetRole(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        try
        {
            _logger.LogInformation(
                "GetRole invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
                correlationId, context.AwsRequestId);

            if (request.PathParameters == null ||
                !request.PathParameters.TryGetValue("roleId", out var roleIdStr) ||
                !Guid.TryParse(roleIdStr, out var roleId))
            {
                _logger.LogWarning(
                    "GetRole — invalid or missing roleId. CorrelationId={CorrelationId}",
                    correlationId);

                return BuildResponse(400, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = "Invalid or missing roleId path parameter.",
                    correlationId
                });
            }

            var role = await _userRepository.GetRoleByIdAsync(roleId, CancellationToken.None);

            if (role == null)
            {
                _logger.LogWarning(
                    "GetRole — not found. RoleId={RoleId}, CorrelationId={CorrelationId}",
                    roleId, correlationId);

                return BuildResponse(404, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = $"Role with ID '{roleId}' was not found.",
                    correlationId
                });
            }

            _logger.LogInformation(
                "GetRole completed. RoleId={RoleId}, Name={Name}, CorrelationId={CorrelationId}",
                role.Id, role.Name, correlationId);

            return BuildResponse(200, new
            {
                success = true,
                timestamp = DateTime.UtcNow,
                @object = role,
                correlationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GetRole failed. CorrelationId={CorrelationId}, Error={Error}",
                correlationId, ex.Message);

            return BuildResponse(500, new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                message = "An internal error occurred while retrieving the role.",
                correlationId
            });
        }
    }

    /// <summary>
    /// POST /v1/roles (create) and PUT /v1/roles/{roleId} (update).
    /// Replaces SecurityManager.SaveRole(ErpRole role) — source lines 295-347.
    /// CREATE path: source lines 329-346; UPDATE path: source lines 307-327.
    /// System roles (Admin/Regular/Guest) cannot be renamed (Phase 11).
    /// Description defaults to empty string when null (source line 305-306).
    /// Name validated only when changed on update (source line 312).
    /// </summary>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleSaveRole(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        try
        {
            _logger.LogInformation(
                "SaveRole invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
                correlationId, context.AwsRequestId);

            // Step 1: Authenticate caller from JWT claims
            var caller = ExtractCallerFromContext(request);
            if (caller == null)
            {
                _logger.LogWarning(
                    "SaveRole — unauthorized, no valid caller. CorrelationId={CorrelationId}",
                    correlationId);

                return BuildResponse(401, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = "Authentication required.",
                    correlationId
                });
            }

            // Step 2: Admin-only permission check
            // Replicates SecurityContext.HasMetaPermission() — checks user.Roles.Any(x => x.Id == AdministratorRoleId)
            if (!_permissionService.HasMetaPermission(caller))
            {
                _logger.LogWarning(
                    "SaveRole — forbidden, caller lacks meta permission. UserId={UserId}, CorrelationId={CorrelationId}",
                    caller.Id, correlationId);

                return BuildResponse(403, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = "Only administrators can manage roles.",
                    correlationId
                });
            }

            // Step 3: Parse request body
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BuildValidationErrorResponse(
                    new Dictionary<string, string> { { "body", "Request body is required." } },
                    correlationId);
            }

            SaveRoleRequest? saveRequest;
            try
            {
                saveRequest = JsonSerializer.Deserialize<SaveRoleRequest>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                return BuildValidationErrorResponse(
                    new Dictionary<string, string> { { "body", "Invalid JSON in request body." } },
                    correlationId);
            }

            if (saveRequest == null)
            {
                return BuildValidationErrorResponse(
                    new Dictionary<string, string> { { "body", "Request body cannot be null." } },
                    correlationId);
            }

            // Step 4: Resolve role ID — path parameter takes precedence for PUT requests
            Guid roleId;
            if (request.PathParameters != null &&
                request.PathParameters.TryGetValue("roleId", out var pathRoleIdStr) &&
                Guid.TryParse(pathRoleIdStr, out var pathRoleId))
            {
                roleId = pathRoleId;
            }
            else if (saveRequest.Id.HasValue && saveRequest.Id.Value != Guid.Empty)
            {
                roleId = saveRequest.Id.Value;
            }
            else
            {
                roleId = Guid.NewGuid();
            }

            // Step 5: Load all roles for uniqueness validation
            // Matches source line 302: var allRoles = GetAllRoles();
            var allRoles = await _userRepository.GetAllRolesAsync(CancellationToken.None);

            // Step 6: Determine create vs update — source line 303:
            // ErpRole existingRole = allRoles.SingleOrDefault(x => x.Id == role.Id);
            var existingRole = allRoles.SingleOrDefault(x => x.Id == roleId);

            // Step 7: Default description — source lines 305-306:
            // if (role.Description is null) role.Description = String.Empty;
            var description = saveRequest.Description ?? string.Empty;

            var validationErrors = new Dictionary<string, string>();

            if (existingRole != null)
            {
                // ===== UPDATE PATH (source lines 307-327) =====
                _logger.LogInformation(
                    "SaveRole UPDATE path. RoleId={RoleId}, ExistingName={ExistingName}, CorrelationId={CorrelationId}",
                    roleId, existingRole.Name, correlationId);

                // Phase 11: System role name protection
                if (SystemRoleIds.Contains(roleId) && existingRole.Name != saveRequest.Name)
                {
                    _logger.LogWarning(
                        "SaveRole — system role rename blocked. RoleId={RoleId}, CorrelationId={CorrelationId}",
                        roleId, correlationId);

                    return BuildValidationErrorResponse(
                        new Dictionary<string, string>
                        {
                            { "name", "Cannot rename a system role (Administrator, Regular, or Guest)." }
                        },
                        correlationId);
                }

                // Name validation — only when name changed (source line 312: if (existingRole.Name != role.Name))
                if (existingRole.Name != saveRequest.Name)
                {
                    // Source lines 316-317
                    if (string.IsNullOrWhiteSpace(saveRequest.Name))
                    {
                        validationErrors["name"] = "Name is required.";
                    }
                    // Source lines 318-319
                    else if (allRoles.Any(x => x.Name == saveRequest.Name))
                    {
                        validationErrors["name"] = "Role with same name already exists";
                    }
                }

                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning(
                        "SaveRole UPDATE validation failed. RoleId={RoleId}, Errors={ErrorCount}, CorrelationId={CorrelationId}",
                        roleId, validationErrors.Count, correlationId);

                    return BuildValidationErrorResponse(validationErrors, correlationId);
                }

                // Capture old values before mutation
                var oldName = existingRole.Name;
                var oldGroupName = existingRole.CognitoGroupName;
                var nameChanged = existingRole.Name != saveRequest.Name;

                // Apply changes to existing role object
                existingRole.Description = description;
                if (nameChanged)
                {
                    existingRole.Name = saveRequest.Name;
                    existingRole.CognitoGroupName = saveRequest.Name.ToLowerInvariant().Replace(" ", "-");
                }

                // Persist to DynamoDB — replaces recMan.UpdateRecord("role", record) source line 324
                await _userRepository.SaveRoleAsync(existingRole, CancellationToken.None);

                // Cognito group sync for renamed roles — migrate caller's membership as immediate consistency measure
                if (nameChanged && !string.IsNullOrEmpty(oldGroupName) && !string.IsNullOrEmpty(caller.Username))
                {
                    await AttemptCognitoGroupSyncAsync(
                        caller.Username, oldGroupName, existingRole.CognitoGroupName!, correlationId);
                }

                // Publish domain event: identity.role.updated
                await PublishDomainEventAsync("identity.role.updated", new
                {
                    eventType = "identity.role.updated",
                    roleId = existingRole.Id,
                    name = existingRole.Name,
                    description = existingRole.Description,
                    nameChanged,
                    oldName = nameChanged ? oldName : (string?)null,
                    oldGroupName = nameChanged ? oldGroupName : (string?)null,
                    newGroupName = nameChanged ? existingRole.CognitoGroupName : (string?)null,
                    timestamp = DateTime.UtcNow,
                    correlationId
                }, correlationId);

                _logger.LogInformation(
                    "SaveRole UPDATE completed. RoleId={RoleId}, Name={Name}, NameChanged={NameChanged}, CorrelationId={CorrelationId}",
                    existingRole.Id, existingRole.Name, nameChanged, correlationId);

                return BuildResponse(200, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    message = "Role updated successfully.",
                    @object = existingRole,
                    correlationId
                });
            }
            else
            {
                // ===== CREATE PATH (source lines 329-346) =====
                _logger.LogInformation(
                    "SaveRole CREATE path. RoleId={RoleId}, Name={Name}, CorrelationId={CorrelationId}",
                    roleId, saveRequest.Name, correlationId);

                // Source lines 335-336
                if (string.IsNullOrWhiteSpace(saveRequest.Name))
                {
                    validationErrors["name"] = "Name is required.";
                }
                // Source lines 337-338
                else if (allRoles.Any(x => x.Name == saveRequest.Name))
                {
                    validationErrors["name"] = "Role with same name already exists";
                }

                if (validationErrors.Count > 0)
                {
                    _logger.LogWarning(
                        "SaveRole CREATE validation failed. Errors={ErrorCount}, CorrelationId={CorrelationId}",
                        validationErrors.Count, correlationId);

                    return BuildValidationErrorResponse(validationErrors, correlationId);
                }

                var newRole = new Role
                {
                    Id = roleId,
                    Name = saveRequest.Name,
                    Description = description,
                    CognitoGroupName = saveRequest.Name.ToLowerInvariant().Replace(" ", "-")
                };

                // Persist to DynamoDB — replaces recMan.CreateRecord("role", record) source line 342
                await _userRepository.SaveRoleAsync(newRole, CancellationToken.None);

                // Publish domain event: identity.role.created
                await PublishDomainEventAsync("identity.role.created", new
                {
                    eventType = "identity.role.created",
                    roleId = newRole.Id,
                    name = newRole.Name,
                    description = newRole.Description,
                    cognitoGroupName = newRole.CognitoGroupName,
                    timestamp = DateTime.UtcNow,
                    correlationId
                }, correlationId);

                _logger.LogInformation(
                    "SaveRole CREATE completed. RoleId={RoleId}, Name={Name}, CorrelationId={CorrelationId}",
                    newRole.Id, newRole.Name, correlationId);

                return BuildResponse(201, new
                {
                    success = true,
                    timestamp = DateTime.UtcNow,
                    message = "Role created successfully.",
                    @object = newRole,
                    correlationId
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SaveRole failed. CorrelationId={CorrelationId}, Error={Error}",
                correlationId, ex.Message);

            return BuildResponse(500, new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                message = "An internal error occurred while saving the role.",
                correlationId
            });
        }
    }

    /// <summary>
    /// DELETE /v1/roles/{roleId} — Deletes an existing role.
    /// System roles (Administrator, Regular, Guest) cannot be deleted.
    /// Replaces the monolith's SecurityManager role deletion logic with system-role protection.
    /// </summary>
    public async Task<APIGatewayHttpApiV2ProxyResponse> HandleDeleteRole(
        APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var correlationId = GetCorrelationId(request);
        try
        {
            _logger.LogInformation(
                "DeleteRole invoked. CorrelationId={CorrelationId}, RequestId={RequestId}",
                correlationId, context.AwsRequestId);

            // ── Authentication ──────────────────────────────────────────
            var caller = ExtractCallerFromContext(request);
            if (caller == null)
            {
                return BuildResponse(401, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = "Authentication required.",
                    correlationId
                });
            }

            // ── Authorization — admin-only (mirrors SecurityContext permission check) ──
            if (!_permissionService.HasMetaPermission(caller))
            {
                _logger.LogWarning(
                    "DeleteRole forbidden. UserId={UserId}, CorrelationId={CorrelationId}",
                    caller.Id, correlationId);

                return BuildResponse(403, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = "Only administrators can manage roles.",
                    correlationId
                });
            }

            // ── Validate roleId path parameter ──────────────────────────
            string? roleIdStr = null;
            request.PathParameters?.TryGetValue("roleId", out roleIdStr);

            if (string.IsNullOrWhiteSpace(roleIdStr) || !Guid.TryParse(roleIdStr, out var roleId))
            {
                return BuildResponse(400, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = "Invalid or missing roleId path parameter.",
                    correlationId
                });
            }

            // ── System role protection — per Definitions.cs lines 15-17 ─
            if (SystemRoleIds.Contains(roleId))
            {
                _logger.LogWarning(
                    "DeleteRole blocked — system role. RoleId={RoleId}, CorrelationId={CorrelationId}",
                    roleId, correlationId);

                var errors = new Dictionary<string, string>
                {
                    { "roleId", "Cannot delete system role" }
                };
                return BuildValidationErrorResponse(errors, correlationId);
            }

            // ── Verify role exists ──────────────────────────────────────
            var existingRole = await _userRepository.GetRoleByIdAsync(roleId, CancellationToken.None);
            if (existingRole == null)
            {
                return BuildResponse(404, new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    message = $"Role with ID '{roleId}' was not found.",
                    correlationId
                });
            }

            // ── Delete the role ─────────────────────────────────────────
            await _userRepository.DeleteRoleAsync(roleId, CancellationToken.None);

            // ── Publish domain event: identity.role.deleted ─────────────
            await PublishDomainEventAsync("identity.role.deleted", new
            {
                eventType = "identity.role.deleted",
                roleId = existingRole.Id,
                name = existingRole.Name,
                timestamp = DateTime.UtcNow,
                correlationId
            }, correlationId);

            _logger.LogInformation(
                "DeleteRole completed. RoleId={RoleId}, Name={Name}, CorrelationId={CorrelationId}",
                existingRole.Id, existingRole.Name, correlationId);

            return BuildResponse(200, new
            {
                success = true,
                timestamp = DateTime.UtcNow,
                message = "Role deleted successfully.",
                correlationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "DeleteRole failed. CorrelationId={CorrelationId}, Error={Error}",
                correlationId, ex.Message);

            return BuildResponse(500, new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                message = "An internal error occurred while deleting the role.",
                correlationId
            });
        }
    }

    #region Private Helpers

    /// <summary>
    /// Builds a consistent API Gateway v2 HTTP response with JSON body and CORS headers.
    /// </summary>
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
    /// Builds a 400 Bad Request response with validation errors matching source ValidationException.Errors pattern.
    /// </summary>
    private static APIGatewayHttpApiV2ProxyResponse BuildValidationErrorResponse(
        Dictionary<string, string> errors, string correlationId)
    {
        return BuildResponse(400, new
        {
            success = false,
            timestamp = DateTime.UtcNow,
            message = "Validation failed.",
            errors,
            correlationId
        });
    }

    /// <summary>
    /// Extracts correlation-ID from request headers or generates a new one.
    /// Per AAP Section 0.8.5: structured JSON logging with correlation-ID propagation.
    /// </summary>
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

        return Guid.NewGuid().ToString("D");
    }

    /// <summary>
    /// Extracts the authenticated user from API Gateway JWT authorizer context.
    /// Replaces the monolith's SecurityContext/ErpMiddleware per-request user scoping.
    /// Maps Cognito user pool claims to the User domain model.
    /// </summary>
    private User? ExtractCallerFromContext(APIGatewayHttpApiV2ProxyRequest request)
    {
        var claims = request.RequestContext?.Authorizer?.Jwt?.Claims;
        if (claims == null || claims.Count == 0)
        {
            return null;
        }

        // Extract user ID from Cognito 'sub' claim (UUID format)
        if (!claims.TryGetValue("sub", out var subClaim) ||
            !Guid.TryParse(subClaim, out var userId))
        {
            return null;
        }

        var user = new User
        {
            Id = userId,
            Email = claims.TryGetValue("email", out var email) ? email : string.Empty,
            Username = claims.TryGetValue("cognito:username", out var username) ? username : string.Empty,
            FirstName = claims.TryGetValue("given_name", out var firstName) ? firstName : string.Empty,
            LastName = claims.TryGetValue("family_name", out var lastName) ? lastName : string.Empty
        };

        // Map Cognito groups to Role objects for permission evaluation
        if (claims.TryGetValue("cognito:groups", out var groupsClaim) &&
            !string.IsNullOrWhiteSpace(groupsClaim))
        {
            user.Roles = MapGroupsToRoles(ParseCognitoGroups(groupsClaim));
        }

        return user;
    }

    /// <summary>
    /// Parses the cognito:groups claim which may be a JSON array or comma/space-separated string.
    /// Cognito access tokens encode groups differently depending on configuration.
    /// </summary>
    private static List<string> ParseCognitoGroups(string groupsClaim)
    {
        var trimmed = groupsClaim.Trim();

        // Try JSON array format: ["administrator", "regular"]
        if (trimmed.StartsWith("["))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(trimmed, JsonOptions);
                if (parsed != null && parsed.Count > 0)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Fall through to delimiter-based parsing
            }
        }

        // Comma, space, or semicolon-separated format
        return trimmed
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Trim().Trim('[', ']', '"'))
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .ToList();
    }

    /// <summary>
    /// Maps Cognito group names to Role domain objects with well-known system role IDs.
    /// System role mapping: administrator → AdministratorRoleId, regular → RegularRoleId, guest → GuestRoleId.
    /// </summary>
    private static List<Role> MapGroupsToRoles(List<string> groupNames)
    {
        var roles = new List<Role>();

        foreach (var groupName in groupNames)
        {
            var normalized = groupName.ToLowerInvariant();

            var role = normalized switch
            {
                "administrator" or "admin" => new Role
                {
                    Id = Role.AdministratorRoleId,
                    Name = "Administrator",
                    CognitoGroupName = groupName
                },
                "regular" => new Role
                {
                    Id = Role.RegularRoleId,
                    Name = "Regular",
                    CognitoGroupName = groupName
                },
                "guest" => new Role
                {
                    Id = Role.GuestRoleId,
                    Name = "Guest",
                    CognitoGroupName = groupName
                },
                _ => new Role
                {
                    Id = Guid.Empty,
                    Name = groupName,
                    CognitoGroupName = groupName
                }
            };

            roles.Add(role);
        }

        return roles;
    }

    /// <summary>
    /// Attempts to sync Cognito group membership when a role is renamed.
    /// Moves the caller from the old group to the new group as an immediate consistency measure.
    /// Full user migration for all affected members is handled by the identity.role.updated
    /// SNS event consumer for eventual consistency.
    /// </summary>
    private async Task AttemptCognitoGroupSyncAsync(
        string callerUsername, string oldGroupName, string newGroupName, string correlationId)
    {
        try
        {
            _logger.LogInformation(
                "Cognito group sync — migrating caller. Caller={Caller}, OldGroup={OldGroup}, NewGroup={NewGroup}, CorrelationId={CorrelationId}",
                callerUsername, oldGroupName, newGroupName, correlationId);

            // Remove from old Cognito group (may fail if user is not in group — non-fatal)
            await _cognitoService.RemoveUserFromGroupAsync(
                callerUsername, oldGroupName, CancellationToken.None);

            // Add to new Cognito group (may fail if group does not exist yet — non-fatal)
            await _cognitoService.AddUserToGroupAsync(
                callerUsername, newGroupName, CancellationToken.None);

            _logger.LogInformation(
                "Cognito group sync completed for caller. Caller={Caller}, CorrelationId={CorrelationId}",
                callerUsername, correlationId);
        }
        catch (Exception ex)
        {
            // Non-fatal: full migration is event-driven via SNS consumer
            _logger.LogWarning(ex,
                "Cognito group sync failed (non-fatal, event-driven migration will handle). " +
                "Caller={Caller}, OldGroup={OldGroup}, NewGroup={NewGroup}, CorrelationId={CorrelationId}",
                callerUsername, oldGroupName, newGroupName, correlationId);
        }
    }

    /// <summary>
    /// Publishes a domain event to the SNS topic for role changes.
    /// Event naming: {domain}.{entity}.{action} per AAP Section 0.8.5.
    /// Replaces the monolith's synchronous HookManager/RecordHookManager post-CRUD hooks.
    /// SNS topic ARN from ROLE_EVENTS_TOPIC_ARN environment variable.
    /// </summary>
    private async Task PublishDomainEventAsync(string eventType, object payload, string correlationId)
    {
        var topicArn = Environment.GetEnvironmentVariable("ROLE_EVENTS_TOPIC_ARN");
        if (string.IsNullOrEmpty(topicArn))
        {
            _logger.LogWarning(
                "ROLE_EVENTS_TOPIC_ARN not configured — skipping event publish. EventType={EventType}, CorrelationId={CorrelationId}",
                eventType, correlationId);
            return;
        }

        try
        {
            var publishRequest = new PublishRequest
            {
                TopicArn = topicArn,
                Message = JsonSerializer.Serialize(payload, JsonOptions),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["eventType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = eventType
                    },
                    ["correlationId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = correlationId
                    }
                }
            };

            await _snsClient.PublishAsync(publishRequest, CancellationToken.None);

            _logger.LogInformation(
                "Domain event published. EventType={EventType}, TopicArn={TopicArn}, CorrelationId={CorrelationId}",
                eventType, topicArn, correlationId);
        }
        catch (Exception ex)
        {
            // Non-fatal: events are best-effort with DLQ for retries on the consumer side
            _logger.LogError(ex,
                "Failed to publish domain event. EventType={EventType}, CorrelationId={CorrelationId}, Error={Error}",
                eventType, correlationId, ex.Message);
        }
    }

    #endregion
}
