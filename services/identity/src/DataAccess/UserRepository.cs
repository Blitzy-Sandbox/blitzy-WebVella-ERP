using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using WebVellaErp.Identity.Models;

namespace WebVellaErp.Identity.DataAccess
{
    /// <summary>
    /// Contract for user and role persistence operations backed by DynamoDB.
    /// Replaces all PostgreSQL-based user/role data access patterns from the monolith:
    /// <list type="bullet">
    ///   <item><description><c>DbRecordRepository</c> — Dynamic record CRUD over <c>rec_user</c> / <c>rec_role</c> tables</description></item>
    ///   <item><description><c>SecurityManager</c> — EQL queries (<c>SELECT *, $user_role.* FROM user WHERE …</c>)</description></item>
    ///   <item><description><c>DbContext</c> — Ambient singleton connection/transaction management</description></item>
    /// </list>
    /// All methods are async with <see cref="CancellationToken"/> support for cooperative cancellation.
    /// </summary>
    public interface IUserRepository
    {
        // ── User Operations ─────────────────────────────────────────────

        /// <summary>
        /// Retrieves a user by their unique identifier, including role assignments.
        /// Replaces <c>SecurityManager.GetUser(Guid)</c> which used
        /// <c>EqlCommand("SELECT *, $user_role.* FROM user WHERE id = @id")</c>.
        /// </summary>
        Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a user by email address (case-insensitive), including role assignments.
        /// Uses GSI1 (<c>EMAIL#{email}</c>) for efficient lookup.
        /// Replaces <c>SecurityManager.GetUser(string email)</c>.
        /// </summary>
        Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a user by username, including role assignments.
        /// Uses GSI2 (<c>USERNAME#{username}</c>) for efficient lookup.
        /// Replaces <c>SecurityManager.GetUserByUsername(string)</c>.
        /// </summary>
        Task<User?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves all users that have at least one of the specified roles.
        /// Replaces <c>SecurityManager.GetUsers(params Guid[] roleIds)</c> which
        /// dynamically built EQL with <c>$user_role.id</c> filters.
        /// </summary>
        Task<List<User>> GetUsersByRoleAsync(CancellationToken cancellationToken = default, params Guid[] roleIds);

        /// <summary>
        /// Retrieves all users in the system with their role assignments.
        /// Uses a filtered Scan on <c>EntityType = USER_PROFILE</c>.
        /// </summary>
        Task<List<User>> GetAllUsersAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates or overwrites a user record and synchronises role assignments.
        /// Replaces <c>SecurityManager.SaveUser(ErpUser)</c> (both create and update paths).
        /// </summary>
        Task SaveUserAsync(User user, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a user record and all associated role relationship items.
        /// Replaces <c>RecordManager.DeleteRecord("user", …)</c>.
        /// </summary>
        Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically updates the <c>last_logged_in</c> timestamp on a user profile.
        /// Replaces <c>SecurityManager.UpdateUserLastLoginTime</c>.
        /// </summary>
        Task UpdateLastLoginTimeAsync(Guid userId, CancellationToken cancellationToken = default);

        // ── Role Operations ─────────────────────────────────────────────

        /// <summary>
        /// Retrieves all roles in the system.
        /// Replaces <c>SecurityManager.GetAllRoles()</c> which used
        /// <c>EqlCommand("SELECT * FROM role")</c>.
        /// </summary>
        Task<List<Role>> GetAllRolesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a single role by its unique identifier.
        /// </summary>
        Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates or overwrites a role record.
        /// Replaces <c>SecurityManager.SaveRole(ErpRole)</c>.
        /// </summary>
        Task SaveRoleAsync(Role role, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a role record and cleans up all user-role relationship items.
        /// </summary>
        Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

        // ── User-Role Relationship Operations ───────────────────────────

        /// <summary>
        /// Retrieves all roles assigned to a user.
        /// Replaces the <c>$user_role.*</c> relational projection from EQL queries and the
        /// manual PostgreSQL JOIN in <c>GetSystemUserWithNoSecurityCheck</c>.
        /// </summary>
        Task<List<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Assigns the specified roles to a user, creating bidirectional relationship items.
        /// Replaces the <c>record["$user_role.id"] = user.Roles.Select(…)</c> pattern.
        /// </summary>
        Task AssignRolesToUserAsync(Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes the specified roles from a user, deleting bidirectional relationship items.
        /// </summary>
        Task RemoveRolesFromUserAsync(Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// DynamoDB single-table design repository for Identity service data access.
    /// Replaces all PostgreSQL-based user/role persistence from the WebVella ERP monolith.
    /// <para>
    /// <strong>Key Patterns:</strong>
    /// <list type="bullet">
    ///   <item><description><c>PK=USER#{userId} / SK=PROFILE</c> — User profile item</description></item>
    ///   <item><description><c>PK=ROLE#{roleId} / SK=META</c> — Role metadata item</description></item>
    ///   <item><description><c>PK=USER#{userId} / SK=ROLE#{roleId}</c> — User-to-role link</description></item>
    ///   <item><description><c>PK=ROLE#{roleId} / SK=MEMBER#{userId}</c> — Role-to-user reverse link</description></item>
    ///   <item><description>GSI1: <c>EMAIL#{email} / USER</c> — Email-based lookup</description></item>
    ///   <item><description>GSI2: <c>USERNAME#{username} / USER</c> — Username-based lookup</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public class UserRepository : IUserRepository
    {
        // ────────────────────────────────────────────────────────────────
        //  DynamoDB Single-Table Design Constants
        // ────────────────────────────────────────────────────────────────

        /// <summary>Environment variable name that holds the DynamoDB table name.</summary>
        private const string TableNameEnv = "IDENTITY_TABLE_NAME";

        /// <summary>Fallback table name when the environment variable is not set.</summary>
        private const string DefaultTableName = "identity";

        // Attribute names
        private const string PK = "PK";
        private const string SK = "SK";
        private const string Gsi1Pk = "GSI1PK";
        private const string Gsi1Sk = "GSI1SK";
        private const string Gsi2Pk = "GSI2PK";
        private const string Gsi2Sk = "GSI2SK";
        private const string Gsi1Name = "GSI1";
        private const string Gsi2Name = "GSI2";
        private const string EntityTypeAttr = "EntityType";

        // Entity type discriminators
        private const string UserProfileType = "USER_PROFILE";
        private const string RoleMetaType = "ROLE_META";
        private const string UserRoleLinkType = "USER_ROLE_LINK";
        private const string RoleMemberLinkType = "ROLE_MEMBER_LINK";

        // Key prefixes
        private const string UserPrefix = "USER#";
        private const string RolePrefix = "ROLE#";
        private const string MemberPrefix = "MEMBER#";
        private const string EmailPrefix = "EMAIL#";
        private const string UsernamePrefix = "USERNAME#";
        private const string ProfileSk = "PROFILE";
        private const string MetaSk = "META";
        private const string UserGsi1Sk = "USER";

        // DynamoDB BatchWriteItem limit
        private const int BatchWriteMaxItems = 25;

        // ────────────────────────────────────────────────────────────────
        //  Dependencies
        // ────────────────────────────────────────────────────────────────

        private readonly IAmazonDynamoDB _dynamoDb;
        private readonly ILogger<UserRepository> _logger;
        private readonly string _tableName;

        /// <summary>
        /// Initialises a new <see cref="UserRepository"/> instance.
        /// The <paramref name="dynamoDbClient"/> respects <c>AWS_ENDPOINT_URL</c> for LocalStack
        /// compatibility — no endpoint hardcoding occurs in this repository.
        /// </summary>
        /// <param name="dynamoDbClient">Injected DynamoDB client (replaces <c>DbContext</c>/<c>DbRecordRepository</c>).</param>
        /// <param name="logger">Structured logger for correlation-ID propagation.</param>
        public UserRepository(IAmazonDynamoDB dynamoDbClient, ILogger<UserRepository> logger)
        {
            _dynamoDb = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tableName = Environment.GetEnvironmentVariable(TableNameEnv) ?? DefaultTableName;
        }

        // ────────────────────────────────────────────────────────────────
        //  User Operations
        // ────────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"{UserPrefix}{userId}" },
                        [SK] = new AttributeValue { S = ProfileSk }
                    },
                    ConsistentRead = true
                }, cancellationToken).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                {
                    return null;
                }

                var user = MapToUser(response.Item);

                // Hydrate role assignments (replaces $user_role.* EQL join)
                user.Roles = await GetUserRolesAsync(userId, cancellationToken).ConfigureAwait(false);

                return user;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving user by ID {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            try
            {
                // Use GSI1 for email lookup — replaces EQL: SELECT *, $user_role.* FROM user WHERE email = @email
                var response = await _dynamoDb.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = Gsi1Name,
                    KeyConditionExpression = $"{Gsi1Pk} = :pk AND {Gsi1Sk} = :sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"{EmailPrefix}{email.ToLowerInvariant()}" },
                        [":sk"] = new AttributeValue { S = UserGsi1Sk }
                    },
                    Limit = 1
                }, cancellationToken).ConfigureAwait(false);

                if (response.Items == null || response.Items.Count == 0)
                {
                    return null;
                }

                var user = MapToUser(response.Items[0]);
                user.Roles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                return user;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving user by email {Email}", email);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<User?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            try
            {
                // Use GSI2 for username lookup — replaces EQL: SELECT *, $user_role.* FROM user WHERE username = @username
                var response = await _dynamoDb.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = Gsi2Name,
                    KeyConditionExpression = $"{Gsi2Pk} = :pk AND {Gsi2Sk} = :sk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"{UsernamePrefix}{username}" },
                        [":sk"] = new AttributeValue { S = UserGsi1Sk }
                    },
                    Limit = 1
                }, cancellationToken).ConfigureAwait(false);

                if (response.Items == null || response.Items.Count == 0)
                {
                    return null;
                }

                var user = MapToUser(response.Items[0]);
                user.Roles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                return user;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving user by username {Username}", username);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<User>> GetUsersByRoleAsync(
            CancellationToken cancellationToken = default,
            params Guid[] roleIds)
        {
            if (roleIds == null || roleIds.Length == 0)
            {
                return new List<User>();
            }

            try
            {
                // Collect unique user IDs from role membership items
                var userIdSet = new HashSet<Guid>();

                foreach (var roleId in roleIds.Distinct())
                {
                    string? exclusiveStartKey = null;
                    bool hasMore = true;

                    while (hasMore)
                    {
                        var request = new QueryRequest
                        {
                            TableName = _tableName,
                            KeyConditionExpression = $"{PK} = :pk AND begins_with({SK}, :skPrefix)",
                            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                            {
                                [":pk"] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                                [":skPrefix"] = new AttributeValue { S = MemberPrefix }
                            }
                        };

                        if (exclusiveStartKey != null)
                        {
                            request.ExclusiveStartKey = new Dictionary<string, AttributeValue>
                            {
                                [PK] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                                [SK] = new AttributeValue { S = exclusiveStartKey }
                            };
                        }

                        var response = await _dynamoDb.QueryAsync(request, cancellationToken).ConfigureAwait(false);

                        foreach (var item in response.Items)
                        {
                            // SK is MEMBER#{userId} — extract the user ID
                            var sk = GetStringOrDefault(item, SK);
                            if (sk.StartsWith(MemberPrefix, StringComparison.Ordinal))
                            {
                                var userIdStr = sk.Substring(MemberPrefix.Length);
                                if (Guid.TryParse(userIdStr, out var uid))
                                {
                                    userIdSet.Add(uid);
                                }
                            }
                        }

                        var lastEvaluatedKey = response.LastEvaluatedKey;
                        hasMore = lastEvaluatedKey != null && lastEvaluatedKey.Count > 0;
                        if (hasMore && lastEvaluatedKey != null && lastEvaluatedKey.TryGetValue(SK, out var lastKey))
                        {
                            exclusiveStartKey = lastKey.S ?? string.Empty;
                        }
                    }
                }

                if (userIdSet.Count == 0)
                {
                    return new List<User>();
                }

                // Fetch user profiles for all collected IDs via BatchGetItem
                var users = new List<User>();
                var idList = userIdSet.ToList();

                // BatchGetItem supports up to 100 keys per table per call
                const int batchGetMaxKeys = 100;
                for (int i = 0; i < idList.Count; i += batchGetMaxKeys)
                {
                    var batch = idList.Skip(i).Take(batchGetMaxKeys).ToList();
                    var keysAndAttributes = new KeysAndAttributes
                    {
                        Keys = batch.Select(uid => new Dictionary<string, AttributeValue>
                        {
                            [PK] = new AttributeValue { S = $"{UserPrefix}{uid}" },
                            [SK] = new AttributeValue { S = ProfileSk }
                        }).ToList(),
                        ConsistentRead = true
                    };

                    var batchResponse = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest
                    {
                        RequestItems = new Dictionary<string, KeysAndAttributes>
                        {
                            [_tableName] = keysAndAttributes
                        }
                    }, cancellationToken).ConfigureAwait(false);

                    if (batchResponse.Responses.TryGetValue(_tableName, out var items))
                    {
                        foreach (var item in items)
                        {
                            var user = MapToUser(item);
                            user.Roles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                            users.Add(user);
                        }
                    }

                    // Handle unprocessed keys with retry
                    while (batchResponse.UnprocessedKeys != null
                           && batchResponse.UnprocessedKeys.Count > 0
                           && batchResponse.UnprocessedKeys.ContainsKey(_tableName))
                    {
                        batchResponse = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest
                        {
                            RequestItems = batchResponse.UnprocessedKeys
                        }, cancellationToken).ConfigureAwait(false);

                        if (batchResponse.Responses.TryGetValue(_tableName, out var retryItems))
                        {
                            foreach (var item in retryItems)
                            {
                                var user = MapToUser(item);
                                user.Roles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                                users.Add(user);
                            }
                        }
                    }
                }

                return users;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving users by role IDs {RoleIds}", string.Join(", ", roleIds));
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<List<User>> GetAllUsersAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var users = new List<User>();
                Dictionary<string, AttributeValue>? exclusiveStartKey = null;

                // Paginated Scan with EntityType filter
                do
                {
                    var request = new ScanRequest
                    {
                        TableName = _tableName,
                        FilterExpression = $"{EntityTypeAttr} = :entityType",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":entityType"] = new AttributeValue { S = UserProfileType }
                        }
                    };

                    if (exclusiveStartKey != null)
                    {
                        request.ExclusiveStartKey = exclusiveStartKey;
                    }

                    var response = await _dynamoDb.ScanAsync(request, cancellationToken).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        var user = MapToUser(item);
                        user.Roles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                        users.Add(user);
                    }

                    exclusiveStartKey = response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0
                        ? response.LastEvaluatedKey
                        : null;
                } while (exclusiveStartKey != null);

                return users;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving all users");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task SaveUserAsync(User user, CancellationToken cancellationToken = default)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            try
            {
                var item = MapFromUser(user);

                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Saved user {UserId} ({Email})", user.Id, user.Email);

                // Synchronise role assignments if roles are present
                if (user.Roles != null && user.Roles.Count > 0)
                {
                    // Get current roles to compute delta
                    var currentRoles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                    var currentRoleIds = new HashSet<Guid>(currentRoles.Select(r => r.Id));
                    var desiredRoleIds = new HashSet<Guid>(user.Roles.Select(r => r.Id));

                    // Roles to add
                    var toAdd = desiredRoleIds.Except(currentRoleIds).ToList();
                    if (toAdd.Count > 0)
                    {
                        await AssignRolesToUserAsync(user.Id, toAdd, cancellationToken).ConfigureAwait(false);
                    }

                    // Roles to remove
                    var toRemove = currentRoleIds.Except(desiredRoleIds).ToList();
                    if (toRemove.Count > 0)
                    {
                        await RemoveRolesFromUserAsync(user.Id, toRemove, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    // If roles list is empty or null, remove all existing role assignments
                    var currentRoles = await GetUserRolesAsync(user.Id, cancellationToken).ConfigureAwait(false);
                    if (currentRoles.Count > 0)
                    {
                        await RemoveRolesFromUserAsync(
                            user.Id,
                            currentRoles.Select(r => r.Id).ToList(),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error saving user {UserId}", user.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            try
            {
                // 1. Query all items belonging to this user (profile + role links)
                var itemsToDelete = new List<Dictionary<string, AttributeValue>>();
                Dictionary<string, AttributeValue>? exclusiveStartKey = null;

                do
                {
                    var request = new QueryRequest
                    {
                        TableName = _tableName,
                        KeyConditionExpression = $"{PK} = :pk",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":pk"] = new AttributeValue { S = $"{UserPrefix}{userId}" }
                        },
                        ProjectionExpression = $"{PK}, {SK}"
                    };

                    if (exclusiveStartKey != null)
                    {
                        request.ExclusiveStartKey = exclusiveStartKey;
                    }

                    var response = await _dynamoDb.QueryAsync(request, cancellationToken).ConfigureAwait(false);
                    itemsToDelete.AddRange(response.Items);

                    exclusiveStartKey = response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0
                        ? response.LastEvaluatedKey
                        : null;
                } while (exclusiveStartKey != null);

                // 2. Also find and delete reverse membership items (ROLE#x / MEMBER#userId)
                var roleLinks = itemsToDelete
                    .Where(item => GetStringOrDefault(item, SK).StartsWith(RolePrefix, StringComparison.Ordinal))
                    .ToList();

                foreach (var link in roleLinks)
                {
                    var roleIdStr = GetStringOrDefault(link, SK).Substring(RolePrefix.Length);
                    itemsToDelete.Add(new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"{RolePrefix}{roleIdStr}" },
                        [SK] = new AttributeValue { S = $"{MemberPrefix}{userId}" }
                    });
                }

                // 3. BatchWriteItem delete (25 per batch)
                await BatchDeleteItemsAsync(itemsToDelete, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Deleted user {UserId} and {Count} associated items", userId, itemsToDelete.Count);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error deleting user {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task UpdateLastLoginTimeAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"{UserPrefix}{userId}" },
                        [SK] = new AttributeValue { S = ProfileSk }
                    },
                    UpdateExpression = "SET last_logged_in = :ts",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":ts"] = new AttributeValue { S = ToIso8601(DateTime.UtcNow) }
                    },
                    ConditionExpression = $"attribute_exists({PK})"
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Updated last login time for user {UserId}", userId);
            }
            catch (ConditionalCheckFailedException)
            {
                _logger.LogWarning("Attempted to update last login for non-existent user {UserId}", userId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error updating last login time for user {UserId}", userId);
                throw;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Role Operations
        // ────────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<List<Role>> GetAllRolesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var roles = new List<Role>();
                Dictionary<string, AttributeValue>? exclusiveStartKey = null;

                do
                {
                    var request = new ScanRequest
                    {
                        TableName = _tableName,
                        FilterExpression = $"{EntityTypeAttr} = :entityType",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":entityType"] = new AttributeValue { S = RoleMetaType }
                        }
                    };

                    if (exclusiveStartKey != null)
                    {
                        request.ExclusiveStartKey = exclusiveStartKey;
                    }

                    var response = await _dynamoDb.ScanAsync(request, cancellationToken).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        roles.Add(MapToRole(item));
                    }

                    exclusiveStartKey = response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0
                        ? response.LastEvaluatedKey
                        : null;
                } while (exclusiveStartKey != null);

                return roles;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving all roles");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<Role?> GetRoleByIdAsync(Guid roleId, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _dynamoDb.GetItemAsync(new GetItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                        [SK] = new AttributeValue { S = MetaSk }
                    },
                    ConsistentRead = true
                }, cancellationToken).ConfigureAwait(false);

                if (response.Item == null || response.Item.Count == 0)
                {
                    return null;
                }

                return MapToRole(response.Item);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving role by ID {RoleId}", roleId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task SaveRoleAsync(Role role, CancellationToken cancellationToken = default)
        {
            if (role == null)
            {
                throw new ArgumentNullException(nameof(role));
            }

            try
            {
                var item = MapFromRole(role);

                await _dynamoDb.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Saved role {RoleId} ({RoleName})", role.Id, role.Name);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error saving role {RoleId}", role.Id);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
        {
            try
            {
                // 1. Delete the role metadata item directly (single-item operation)
                await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        [PK] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                        [SK] = new AttributeValue { S = MetaSk }
                    }
                }, cancellationToken).ConfigureAwait(false);

                var itemsToDelete = new List<Dictionary<string, AttributeValue>>();

                // 2. Find all MEMBER# items under this role (role-to-user reverse links)
                Dictionary<string, AttributeValue>? exclusiveStartKey = null;
                do
                {
                    var request = new QueryRequest
                    {
                        TableName = _tableName,
                        KeyConditionExpression = $"{PK} = :pk AND begins_with({SK}, :skPrefix)",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":pk"] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                            [":skPrefix"] = new AttributeValue { S = MemberPrefix }
                        },
                        ProjectionExpression = $"{PK}, {SK}"
                    };

                    if (exclusiveStartKey != null)
                    {
                        request.ExclusiveStartKey = exclusiveStartKey;
                    }

                    var response = await _dynamoDb.QueryAsync(request, cancellationToken).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        // Delete the reverse link (ROLE#x / MEMBER#userId)
                        itemsToDelete.Add(new Dictionary<string, AttributeValue>
                        {
                            [PK] = item[PK],
                            [SK] = item[SK]
                        });

                        // Also delete the forward link (USER#userId / ROLE#roleId)
                        var userIdStr = GetStringOrDefault(item, SK).Substring(MemberPrefix.Length);
                        itemsToDelete.Add(new Dictionary<string, AttributeValue>
                        {
                            [PK] = new AttributeValue { S = $"{UserPrefix}{userIdStr}" },
                            [SK] = new AttributeValue { S = $"{RolePrefix}{roleId}" }
                        });
                    }

                    exclusiveStartKey = response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0
                        ? response.LastEvaluatedKey
                        : null;
                } while (exclusiveStartKey != null);

                // 3. BatchWriteItem delete
                await BatchDeleteItemsAsync(itemsToDelete, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Deleted role {RoleId} and {Count} associated items", roleId, itemsToDelete.Count);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error deleting role {RoleId}", roleId);
                throw;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  User-Role Relationship Operations
        // ────────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public async Task<List<Role>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            try
            {
                var roles = new List<Role>();
                Dictionary<string, AttributeValue>? exclusiveStartKey = null;

                do
                {
                    var request = new QueryRequest
                    {
                        TableName = _tableName,
                        KeyConditionExpression = $"{PK} = :pk AND begins_with({SK}, :skPrefix)",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":pk"] = new AttributeValue { S = $"{UserPrefix}{userId}" },
                            [":skPrefix"] = new AttributeValue { S = RolePrefix }
                        }
                    };

                    if (exclusiveStartKey != null)
                    {
                        request.ExclusiveStartKey = exclusiveStartKey;
                    }

                    var response = await _dynamoDb.QueryAsync(request, cancellationToken).ConfigureAwait(false);

                    foreach (var item in response.Items)
                    {
                        // Each user-role link item stores denormalised role data
                        var role = new Role
                        {
                            Id = Guid.TryParse(GetStringOrDefault(item, "role_id"), out var rid) ? rid : Guid.Empty,
                            Name = GetStringOrDefault(item, "role_name"),
                            Description = GetStringOrDefault(item, "role_description"),
                            CognitoGroupName = GetStringOrNull(item, "cognito_group_name")
                        };

                        if (role.Id != Guid.Empty)
                        {
                            roles.Add(role);
                        }
                    }

                    exclusiveStartKey = response.LastEvaluatedKey != null && response.LastEvaluatedKey.Count > 0
                        ? response.LastEvaluatedKey
                        : null;
                } while (exclusiveStartKey != null);

                return roles;
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error retrieving roles for user {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task AssignRolesToUserAsync(
            Guid userId,
            List<Guid> roleIds,
            CancellationToken cancellationToken = default)
        {
            if (roleIds == null || roleIds.Count == 0)
            {
                return;
            }

            try
            {
                var writeRequests = new List<WriteRequest>();

                foreach (var roleId in roleIds.Distinct())
                {
                    // Fetch role metadata for denormalisation in the link item
                    var role = await GetRoleByIdAsync(roleId, cancellationToken).ConfigureAwait(false);
                    var roleName = role?.Name ?? string.Empty;
                    var roleDescription = role?.Description ?? string.Empty;
                    var cognitoGroupName = role?.CognitoGroupName ?? string.Empty;

                    // Forward link: USER#userId / ROLE#roleId
                    writeRequests.Add(new WriteRequest
                    {
                        PutRequest = new PutRequest
                        {
                            Item = new Dictionary<string, AttributeValue>
                            {
                                [PK] = new AttributeValue { S = $"{UserPrefix}{userId}" },
                                [SK] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                                [EntityTypeAttr] = new AttributeValue { S = UserRoleLinkType },
                                ["role_id"] = new AttributeValue { S = roleId.ToString() },
                                ["role_name"] = new AttributeValue { S = roleName },
                                ["role_description"] = new AttributeValue { S = roleDescription },
                                ["cognito_group_name"] = new AttributeValue { S = cognitoGroupName },
                                ["user_id"] = new AttributeValue { S = userId.ToString() }
                            }
                        }
                    });

                    // Reverse link: ROLE#roleId / MEMBER#userId
                    writeRequests.Add(new WriteRequest
                    {
                        PutRequest = new PutRequest
                        {
                            Item = new Dictionary<string, AttributeValue>
                            {
                                [PK] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                                [SK] = new AttributeValue { S = $"{MemberPrefix}{userId}" },
                                [EntityTypeAttr] = new AttributeValue { S = RoleMemberLinkType },
                                ["user_id"] = new AttributeValue { S = userId.ToString() },
                                ["role_id"] = new AttributeValue { S = roleId.ToString() }
                            }
                        }
                    });
                }

                // Execute in batches of 25
                await BatchWriteRequestsAsync(writeRequests, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Assigned {Count} role(s) to user {UserId}", roleIds.Count, userId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error assigning roles to user {UserId}", userId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task RemoveRolesFromUserAsync(
            Guid userId,
            List<Guid> roleIds,
            CancellationToken cancellationToken = default)
        {
            if (roleIds == null || roleIds.Count == 0)
            {
                return;
            }

            try
            {
                var writeRequests = new List<WriteRequest>();

                foreach (var roleId in roleIds.Distinct())
                {
                    // Delete forward link: USER#userId / ROLE#roleId
                    writeRequests.Add(new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest
                        {
                            Key = new Dictionary<string, AttributeValue>
                            {
                                [PK] = new AttributeValue { S = $"{UserPrefix}{userId}" },
                                [SK] = new AttributeValue { S = $"{RolePrefix}{roleId}" }
                            }
                        }
                    });

                    // Delete reverse link: ROLE#roleId / MEMBER#userId
                    writeRequests.Add(new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest
                        {
                            Key = new Dictionary<string, AttributeValue>
                            {
                                [PK] = new AttributeValue { S = $"{RolePrefix}{roleId}" },
                                [SK] = new AttributeValue { S = $"{MemberPrefix}{userId}" }
                            }
                        }
                    });
                }

                await BatchWriteRequestsAsync(writeRequests, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Removed {Count} role(s) from user {UserId}", roleIds.Count, userId);
            }
            catch (AmazonDynamoDBException ex)
            {
                _logger.LogError(ex, "DynamoDB error removing roles from user {UserId}", userId);
                throw;
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  Private Helper Methods
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps a DynamoDB item to a <see cref="User"/> domain model.
        /// Replaces the deserialization logic from monolith's EQL result-set to ErpUser mapping.
        /// </summary>
        private User MapToUser(Dictionary<string, AttributeValue> item)
        {
            var user = new User
            {
                Id = Guid.TryParse(GetStringOrDefault(item, "id"), out var id) ? id : Guid.Empty,
                Username = GetStringOrDefault(item, "username"),
                Email = GetStringOrDefault(item, "email"),
                Password = GetStringOrDefault(item, "password"),
                FirstName = GetStringOrDefault(item, "first_name"),
                LastName = GetStringOrDefault(item, "last_name"),
                Image = GetStringOrNull(item, "image"),
                Enabled = GetBoolOrDefault(item, "enabled", false),
                Verified = GetBoolOrDefault(item, "verified", false),
                EmailVerified = GetBoolOrDefault(item, "email_verified", false),
                CreatedOn = ParseDateTime(item.GetValueOrDefault("created_on")) ?? DateTime.UtcNow,
                LastLoggedIn = ParseDateTime(item.GetValueOrDefault("last_logged_in")),
                CognitoSub = GetStringOrNull(item, "cognito_sub"),
                Roles = new List<Role>() // Hydrated separately via GetUserRolesAsync
            };

            // Deserialize preferences JSON (replaces JsonConvert.DeserializeObject<ErpUserPreferences>)
            var preferencesJson = GetStringOrNull(item, "preferences");
            if (!string.IsNullOrEmpty(preferencesJson))
            {
                try
                {
                    user.Preferences = JsonSerializer.Deserialize(
                        preferencesJson,
                        UserRepositorySerializerContext.Default.UserPreferences);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize preferences for user {UserId}", user.Id);
                    user.Preferences = new UserPreferences();
                }
            }
            else
            {
                user.Preferences = new UserPreferences();
            }

            return user;
        }

        /// <summary>
        /// Maps a DynamoDB item to a <see cref="Role"/> domain model.
        /// </summary>
        private static Role MapToRole(Dictionary<string, AttributeValue> item)
        {
            return new Role
            {
                Id = Guid.TryParse(GetStringOrDefault(item, "id"), out var id) ? id : Guid.Empty,
                Name = GetStringOrDefault(item, "name"),
                Description = GetStringOrDefault(item, "description"),
                CognitoGroupName = GetStringOrNull(item, "cognito_group_name")
            };
        }

        /// <summary>
        /// Converts a <see cref="User"/> model to a DynamoDB item dictionary.
        /// Mirrors the record-building logic from SecurityManager.SaveUser (create path).
        /// </summary>
        private Dictionary<string, AttributeValue> MapFromUser(User user)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                [PK] = new AttributeValue { S = $"{UserPrefix}{user.Id}" },
                [SK] = new AttributeValue { S = ProfileSk },
                [Gsi1Pk] = new AttributeValue { S = $"{EmailPrefix}{user.Email.ToLowerInvariant()}" },
                [Gsi1Sk] = new AttributeValue { S = UserGsi1Sk },
                [Gsi2Pk] = new AttributeValue { S = $"{UsernamePrefix}{user.Username}" },
                [Gsi2Sk] = new AttributeValue { S = UserGsi1Sk },
                [EntityTypeAttr] = new AttributeValue { S = UserProfileType },
                ["id"] = new AttributeValue { S = user.Id.ToString() },
                ["username"] = new AttributeValue { S = user.Username ?? string.Empty },
                ["email"] = new AttributeValue { S = user.Email ?? string.Empty },
                ["first_name"] = new AttributeValue { S = user.FirstName ?? string.Empty },
                ["last_name"] = new AttributeValue { S = user.LastName ?? string.Empty },
                ["enabled"] = new AttributeValue { BOOL = user.Enabled },
                ["verified"] = new AttributeValue { BOOL = user.Verified },
                ["email_verified"] = new AttributeValue { BOOL = user.EmailVerified },
                ["created_on"] = new AttributeValue { S = ToIso8601(user.CreatedOn) }
            };

            // Optional string attributes — store empty string rather than NULL for DynamoDB compatibility
            item["image"] = new AttributeValue { S = user.Image ?? string.Empty };
            item["cognito_sub"] = new AttributeValue { S = user.CognitoSub ?? string.Empty };
            item["password"] = new AttributeValue { S = user.Password ?? string.Empty };

            // LastLoggedIn — nullable DateTime
            if (user.LastLoggedIn.HasValue)
            {
                item["last_logged_in"] = new AttributeValue { S = ToIso8601(user.LastLoggedIn.Value) };
            }
            else
            {
                item["last_logged_in"] = new AttributeValue { NULL = true };
            }

            // Preferences — serialized as JSON string (replaces JsonConvert.SerializeObject from SecurityManager)
            if (user.Preferences != null)
            {
                try
                {
                    item["preferences"] = new AttributeValue
                    {
                        S = JsonSerializer.Serialize(
                            user.Preferences,
                            UserRepositorySerializerContext.Default.UserPreferences)
                    };
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to serialize preferences for user {UserId}", user.Id);
                    item["preferences"] = new AttributeValue { S = "{}" };
                }
            }
            else
            {
                item["preferences"] = new AttributeValue { S = "{}" };
            }

            return item;
        }

        /// <summary>
        /// Converts a <see cref="Role"/> model to a DynamoDB item dictionary.
        /// </summary>
        private static Dictionary<string, AttributeValue> MapFromRole(Role role)
        {
            return new Dictionary<string, AttributeValue>
            {
                [PK] = new AttributeValue { S = $"{RolePrefix}{role.Id}" },
                [SK] = new AttributeValue { S = MetaSk },
                [EntityTypeAttr] = new AttributeValue { S = RoleMetaType },
                ["id"] = new AttributeValue { S = role.Id.ToString() },
                ["name"] = new AttributeValue { S = role.Name ?? string.Empty },
                ["description"] = new AttributeValue { S = role.Description ?? string.Empty },
                ["cognito_group_name"] = new AttributeValue { S = role.CognitoGroupName ?? string.Empty }
            };
        }

        /// <summary>
        /// Formats a <see cref="DateTime"/> as an ISO 8601 string for DynamoDB storage.
        /// Uses <see cref="CultureInfo.InvariantCulture"/> for consistent formatting across locales.
        /// </summary>
        private static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses an ISO 8601 formatted <see cref="AttributeValue"/> string back to a nullable <see cref="DateTime"/>.
        /// </summary>
        private static DateTime? ParseDateTime(AttributeValue? value)
        {
            if (value == null || value.NULL || string.IsNullOrEmpty(value.S))
            {
                return null;
            }

            if (DateTime.TryParse(value.S, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt.ToUniversalTime();
            }

            return null;
        }

        /// <summary>
        /// Safely extracts a string attribute from a DynamoDB item.
        /// Returns <paramref name="defaultValue"/> if the key is missing, null, or a DynamoDB NULL.
        /// </summary>
        private static string GetStringOrDefault(Dictionary<string, AttributeValue> item, string key, string defaultValue = "")
        {
            if (item.TryGetValue(key, out var attr) && !attr.NULL && attr.S != null)
            {
                return attr.S;
            }
            return defaultValue;
        }

        /// <summary>
        /// Safely extracts a nullable string attribute from a DynamoDB item.
        /// Returns <c>null</c> if the key is missing, the attribute is DynamoDB NULL, or the string is empty.
        /// </summary>
        private static string? GetStringOrNull(Dictionary<string, AttributeValue> item, string key)
        {
            if (item.TryGetValue(key, out var attr) && !attr.NULL && !string.IsNullOrEmpty(attr.S))
            {
                return attr.S;
            }
            return null;
        }

        /// <summary>
        /// Safely extracts a boolean attribute from a DynamoDB item.
        /// Returns <paramref name="defaultValue"/> if the key is missing.
        /// </summary>
        private static bool GetBoolOrDefault(Dictionary<string, AttributeValue> item, string key, bool defaultValue = false)
        {
            if (item.TryGetValue(key, out var attr) && !attr.NULL)
            {
                return attr.BOOL;
            }
            return defaultValue;
        }

        /// <summary>
        /// Executes a batch of delete operations in chunks of 25 (DynamoDB BatchWriteItem limit).
        /// Handles <c>UnprocessedItems</c> with automatic retry.
        /// </summary>
        private async Task BatchDeleteItemsAsync(
            List<Dictionary<string, AttributeValue>> keys,
            CancellationToken cancellationToken)
        {
            if (keys.Count == 0)
            {
                return;
            }

            var writeRequests = keys.Select(key => new WriteRequest
            {
                DeleteRequest = new DeleteRequest { Key = key }
            }).ToList();

            await BatchWriteRequestsAsync(writeRequests, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes a batch of write requests (puts and/or deletes) in chunks of 25
        /// with automatic retry for unprocessed items.
        /// </summary>
        private async Task BatchWriteRequestsAsync(
            List<WriteRequest> writeRequests,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < writeRequests.Count; i += BatchWriteMaxItems)
            {
                var batch = writeRequests.Skip(i).Take(BatchWriteMaxItems).ToList();

                var request = new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [_tableName] = batch
                    }
                };

                var response = await _dynamoDb.BatchWriteItemAsync(request, cancellationToken).ConfigureAwait(false);

                // Retry unprocessed items
                while (response.UnprocessedItems != null
                       && response.UnprocessedItems.Count > 0
                       && response.UnprocessedItems.ContainsKey(_tableName)
                       && response.UnprocessedItems[_tableName].Count > 0)
                {
                    _logger.LogWarning(
                        "Retrying {Count} unprocessed items in batch write",
                        response.UnprocessedItems[_tableName].Count);

                    response = await _dynamoDb.BatchWriteItemAsync(new BatchWriteItemRequest
                    {
                        RequestItems = response.UnprocessedItems
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// AOT-compatible source-generated JSON serializer context for <see cref="UserPreferences"/>
    /// used in DynamoDB attribute serialization/deserialization. Eliminates IL2026/IL3050
    /// trimming warnings when <c>PublishAot=true</c>.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(UserPreferences))]
    internal partial class UserRepositorySerializerContext : JsonSerializerContext
    {
    }
}