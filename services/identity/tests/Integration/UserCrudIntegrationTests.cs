using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Xunit;
using WebVellaErp.Identity.Models;

namespace WebVellaErp.Identity.Tests.Integration
{
    /// <summary>
    /// End-to-end integration tests for user CRUD operations against real LocalStack
    /// Cognito and DynamoDB services. Validates the complete user lifecycle (create,
    /// read, update, delete) with exact validation rule parity from the monolith's
    /// <c>SecurityManager.SaveUser()</c> (source lines 191-293).
    ///
    /// <para><b>Zero mocked AWS SDK calls</b> — every test operates against real
    /// LocalStack infrastructure provisioned by <see cref="LocalStackFixture"/>.</para>
    ///
    /// <para>Per AAP Section 0.8.4: "All integration and E2E tests MUST execute against
    /// LocalStack. No mocked AWS SDK calls in integration tests."</para>
    ///
    /// <para><b>DynamoDB key patterns validated:</b></para>
    /// <list type="bullet">
    ///   <item><c>PK=USER#{userId}, SK=PROFILE</c> — user profile item</item>
    ///   <item><c>GSI1PK=EMAIL#{email}, GSI1SK=USER#{userId}</c> — email lookup index</item>
    ///   <item><c>GSI2PK=USERNAME#{username}, GSI2SK=USER#{userId}</c> — username lookup index</item>
    ///   <item><c>PK=USER#{userId}, SK=ROLE#{roleId}</c> — user-role assignment</item>
    ///   <item><c>PK=ROLE#{roleId}, SK=MEMBER#{userId}</c> — role membership query</item>
    /// </list>
    ///
    /// <para><b>Cognito attribute mapping validated:</b>
    /// email, email_verified, given_name, family_name, preferred_username</para>
    ///
    /// <para><b>Source mapping:</b></para>
    /// <list type="bullet">
    ///   <item>SecurityManager.SaveUser() lines 191-293 → CreateUser/UpdateUser tests</item>
    ///   <item>SecurityManager.GetUsers(roleIds) lines 167-184 → GetUsersByRole test</item>
    ///   <item>SecurityManager.UpdateUserLastLoginTime() lines 350-356 → LastLogin test</item>
    ///   <item>SecurityManager.IsValidEmail() lines 358-368 → InvalidEmail test</item>
    ///   <item>Definitions.cs SystemIds → well-known GUIDs for system roles</item>
    /// </list>
    /// </summary>
    public class UserCrudIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly IAmazonCognitoIdentityProvider _cognitoClient;
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly string _userPoolId;
        private readonly string _clientId;
        private readonly string _tableName;

        /// <summary>
        /// Constructor receives the shared <see cref="LocalStackFixture"/> which provisions
        /// DynamoDB table (with PK/SK, GSI1 for email lookups, GSI2 for username lookups,
        /// and seeded system roles) and Cognito user pool (with app client and system role
        /// groups: administrator, regular, guest).
        /// </summary>
        /// <param name="fixture">Shared LocalStack fixture providing pre-configured AWS clients
        /// and resource identifiers.</param>
        public UserCrudIntegrationTests(LocalStackFixture fixture)
        {
            _cognitoClient = fixture.CognitoClient;
            _dynamoDbClient = fixture.DynamoDbClient;
            _userPoolId = fixture.UserPoolId;
            _clientId = fixture.ClientId;
            _tableName = fixture.TableName;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Helper Methods
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a user in both Cognito (AdminCreateUser + AdminSetUserPassword for
        /// permanent password) and DynamoDB (PutItem with single-table design pattern).
        ///
        /// Replaces: <c>SecurityManager.SaveUser()</c> CREATE path (source lines 256-292)
        /// which called <c>recMan.CreateRecord("user", record)</c>.
        /// </summary>
        /// <param name="email">Email address (also used as Cognito username).</param>
        /// <param name="password">Password to set permanently for the user.</param>
        /// <param name="username">Preferred username attribute.</param>
        /// <param name="firstName">First (given) name.</param>
        /// <param name="lastName">Last (family) name.</param>
        /// <returns>The generated user ID (Guid).</returns>
        private async Task<Guid> CreateCognitoUser(
            string email,
            string password,
            string username,
            string firstName,
            string lastName)
        {
            var userId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;

            // Step 1: Create user in Cognito with required attributes
            await _cognitoClient.AdminCreateUserAsync(new AdminCreateUserRequest
            {
                UserPoolId = _userPoolId,
                Username = email,
                TemporaryPassword = password,
                MessageAction = MessageActionType.SUPPRESS,
                UserAttributes = new List<AttributeType>
                {
                    new AttributeType { Name = "email", Value = email },
                    new AttributeType { Name = "email_verified", Value = "true" },
                    new AttributeType { Name = "given_name", Value = firstName },
                    new AttributeType { Name = "family_name", Value = lastName },
                    new AttributeType { Name = "preferred_username", Value = username }
                }
            });

            // Step 2: Set permanent password (bypasses FORCE_CHANGE_PASSWORD challenge)
            await _cognitoClient.AdminSetUserPasswordAsync(new AdminSetUserPasswordRequest
            {
                UserPoolId = _userPoolId,
                Username = email,
                Password = password,
                Permanent = true
            });

            // Step 3: Store user profile in DynamoDB with single-table design
            // PK=USER#{userId}, SK=PROFILE — main user profile item
            // GSI1PK=EMAIL#{email}, GSI1SK=USER#{userId} — email lookup
            // GSI2PK=USERNAME#{username}, GSI2SK=USER#{userId} — username lookup
            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["SK"] = new AttributeValue { S = "PROFILE" },
                    ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                    ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" },
                    ["GSI1SK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" },
                    ["GSI2SK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["id"] = new AttributeValue { S = userId.ToString() },
                    ["email"] = new AttributeValue { S = email },
                    ["username"] = new AttributeValue { S = username },
                    ["first_name"] = new AttributeValue { S = firstName },
                    ["last_name"] = new AttributeValue { S = lastName },
                    ["image"] = new AttributeValue { S = string.Empty },
                    ["enabled"] = new AttributeValue { BOOL = true },
                    ["verified"] = new AttributeValue { BOOL = true },
                    ["created_on"] = new AttributeValue { S = createdOn.ToString("o") }
                }
            });

            return userId;
        }

        /// <summary>
        /// Removes a user from Cognito and all related DynamoDB items (profile + role assignments).
        /// Cleanup is best-effort; exceptions are swallowed to prevent masking test failures.
        /// </summary>
        /// <param name="email">Cognito username (email) to delete.</param>
        /// <param name="userId">User ID for DynamoDB item cleanup.</param>
        private async Task CleanupUser(string email, Guid userId)
        {
            // Delete from Cognito
            try
            {
                await _cognitoClient.AdminDeleteUserAsync(new AdminDeleteUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email
                });
            }
            catch (Exception)
            {
                // Best-effort cleanup; user may not exist in Cognito
            }

            // Delete profile item from DynamoDB
            try
            {
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" }
                    }
                });
            }
            catch (Exception)
            {
                // Best-effort cleanup
            }

            // Delete any role assignment items from DynamoDB
            try
            {
                var roleItems = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"USER#{userId}" },
                        [":skPrefix"] = new AttributeValue { S = "ROLE#" }
                    }
                });

                foreach (var item in roleItems.Items)
                {
                    await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                    {
                        TableName = _tableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = item["PK"],
                            ["SK"] = item["SK"]
                        }
                    });
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup
            }
        }

        /// <summary>
        /// Retrieves a user profile item from DynamoDB by user ID.
        /// Uses the single-table design pattern: PK=USER#{userId}, SK=PROFILE.
        /// </summary>
        /// <param name="userId">The user ID to look up.</param>
        /// <returns>The DynamoDB item attributes, or null if not found.</returns>
        private async Task<Dictionary<string, AttributeValue>?> GetUserFromDynamoDb(Guid userId)
        {
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["SK"] = new AttributeValue { S = "PROFILE" }
                }
            });

            return response.Item != null && response.Item.Count > 0 ? response.Item : null;
        }

        /// <summary>
        /// Queries DynamoDB GSI1 (email lookup index) to find a user by email.
        /// Key pattern: GSI1PK=EMAIL#{email}, GSI1SK begins with USER#.
        ///
        /// Replaces: <c>SecurityManager.GetUser(string email)</c> (source lines 49-61)
        /// which used EQL: <c>"SELECT *, $user_role.* FROM user WHERE email = @email"</c>
        /// </summary>
        /// <param name="email">Email to search for.</param>
        /// <returns>The query response containing matching items.</returns>
        private async Task<QueryResponse> QueryByEmail(string email)
        {
            return await _dynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = "GSI1",
                KeyConditionExpression = "GSI1PK = :gsi1pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":gsi1pk"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" }
                }
            });
        }

        /// <summary>
        /// Queries DynamoDB GSI2 (username lookup index) to find a user by username.
        /// Key pattern: GSI2PK=USERNAME#{username}, GSI2SK begins with USER#.
        ///
        /// Replaces: <c>SecurityManager.GetUserByUsername(string username)</c> (source lines 63-75)
        /// which used EQL: <c>"SELECT *, $user_role.* FROM user WHERE username = @username"</c>
        /// </summary>
        /// <param name="username">Username to search for.</param>
        /// <returns>The query response containing matching items.</returns>
        private async Task<QueryResponse> QueryByUsername(string username)
        {
            return await _dynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = "GSI2",
                KeyConditionExpression = "GSI2PK = :gsi2pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":gsi2pk"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" }
                }
            });
        }

        /// <summary>
        /// Validates an email address using the same pattern as the monolith's
        /// <c>SecurityManager.IsValidEmail()</c> (source lines 358-368):
        /// <code>
        /// try { var addr = new MailAddress(email); return addr.Address == email; }
        /// catch { return false; }
        /// </code>
        /// </summary>
        /// <param name="email">Email string to validate.</param>
        /// <returns><c>true</c> if valid; <c>false</c> otherwise.</returns>
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
        /// Validates the user creation payload, returning a list of validation errors
        /// matching the exact error messages from <c>SecurityManager.SaveUser()</c>
        /// CREATE path (source lines 267-282).
        /// </summary>
        /// <param name="username">Username to validate.</param>
        /// <param name="email">Email to validate.</param>
        /// <param name="password">Password to validate.</param>
        /// <param name="existingUsernameQuery">GSI2 query result for username uniqueness check.</param>
        /// <param name="existingEmailQuery">GSI1 query result for email uniqueness check.</param>
        /// <returns>List of (field, message) validation errors.</returns>
        private static List<(string Field, string Message)> ValidateCreateUser(
            string? username,
            string? email,
            string? password,
            QueryResponse? existingUsernameQuery,
            QueryResponse? existingEmailQuery)
        {
            var errors = new List<(string Field, string Message)>();

            // Username validation (source lines 267-270)
            if (string.IsNullOrWhiteSpace(username))
                errors.Add(("username", "Username is required."));
            else if (existingUsernameQuery != null && existingUsernameQuery.Items.Count > 0)
                errors.Add(("username", "Username is already registered to another user. It must be unique."));

            // Email validation (source lines 272-277)
            if (string.IsNullOrWhiteSpace(email))
                errors.Add(("email", "Email is required."));
            else if (existingEmailQuery != null && existingEmailQuery.Items.Count > 0)
                errors.Add(("email", "Email is already registered to another user. It must be unique."));
            else if (!IsValidEmail(email!))
                errors.Add(("email", "Email is not valid."));

            // Password validation (source lines 279-280)
            if (string.IsNullOrWhiteSpace(password))
                errors.Add(("password", "Password is required."));

            return errors;
        }

        /// <summary>
        /// Validates the user update payload for username uniqueness,
        /// matching the exact error messages from <c>SecurityManager.SaveUser()</c>
        /// UPDATE path (source lines 206-226).
        /// </summary>
        /// <param name="newUsername">New username to validate.</param>
        /// <param name="currentUserId">The ID of the user being updated.</param>
        /// <param name="existingUsernameQuery">GSI2 query result for username uniqueness check.</param>
        /// <returns>List of (field, message) validation errors.</returns>
        private static List<(string Field, string Message)> ValidateUpdateUsername(
            string newUsername,
            Guid currentUserId,
            QueryResponse existingUsernameQuery)
        {
            var errors = new List<(string Field, string Message)>();

            // Username uniqueness (source lines 210-213)
            if (string.IsNullOrWhiteSpace(newUsername))
            {
                errors.Add(("username", "Username is required."));
            }
            else if (existingUsernameQuery.Items.Count > 0)
            {
                // Check that the found user is not the same user being updated
                var existingUserId = existingUsernameQuery.Items[0].ContainsKey("id")
                    ? existingUsernameQuery.Items[0]["id"].S
                    : null;

                if (existingUserId != currentUserId.ToString())
                {
                    errors.Add(("username", "Username is already registered to another user. It must be unique."));
                }
            }

            return errors;
        }

        /// <summary>
        /// Adds role assignment items for a user in both DynamoDB and Cognito.
        /// DynamoDB: PK=USER#{userId}/SK=ROLE#{roleId} and PK=ROLE#{roleId}/SK=MEMBER#{userId}
        /// Cognito: AdminAddUserToGroup for each role's Cognito group name.
        ///
        /// Replaces: <c>record["$user_role.id"] = user.Roles.Select(x => x.Id).ToList()</c>
        /// (source SecurityManager.cs lines 246, 284).
        /// </summary>
        /// <param name="email">Cognito username (email) for group assignment.</param>
        /// <param name="userId">User ID for DynamoDB item keys.</param>
        /// <param name="roleId">Role ID for DynamoDB item keys.</param>
        /// <param name="groupName">Cognito group name to assign user to.</param>
        private async Task AssignRoleToUser(string email, Guid userId, Guid roleId, string groupName)
        {
            // Add user to Cognito group
            await _cognitoClient.AdminAddUserToGroupAsync(new AdminAddUserToGroupRequest
            {
                UserPoolId = _userPoolId,
                Username = email,
                GroupName = groupName
            });

            // Add user-role assignment item in DynamoDB (user → role direction)
            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["SK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                    ["EntityType"] = new AttributeValue { S = "USER_ROLE" },
                    ["user_id"] = new AttributeValue { S = userId.ToString() },
                    ["role_id"] = new AttributeValue { S = roleId.ToString() }
                }
            });

            // Add role membership item in DynamoDB (role → user direction for reverse queries)
            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                    ["SK"] = new AttributeValue { S = $"MEMBER#{userId}" },
                    ["EntityType"] = new AttributeValue { S = "ROLE_MEMBER" },
                    ["user_id"] = new AttributeValue { S = userId.ToString() },
                    ["role_id"] = new AttributeValue { S = roleId.ToString() }
                }
            });
        }

        /// <summary>
        /// Cleans up role membership items from DynamoDB for a given role.
        /// Best-effort; exceptions are swallowed.
        /// </summary>
        /// <param name="roleId">Role ID to clean up membership items for.</param>
        private async Task CleanupRoleMemberships(Guid roleId)
        {
            try
            {
                var memberItems = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        [":skPrefix"] = new AttributeValue { S = "MEMBER#" }
                    }
                });

                foreach (var item in memberItems.Items)
                {
                    await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                    {
                        TableName = _tableName,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["PK"] = item["PK"],
                            ["SK"] = item["SK"]
                        }
                    });
                }
            }
            catch (Exception)
            {
                // Best-effort cleanup
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: User Creation — Basic CRUD
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that creating a user with valid data persists correctly in both
        /// Cognito (user attributes) and DynamoDB (single-table design profile item + GSI entries).
        ///
        /// Replaces: <c>SecurityManager.SaveUser()</c> CREATE path (source lines 256-292)
        /// which called <c>recMan.CreateRecord("user", record)</c> against PostgreSQL.
        ///
        /// Verifies:
        /// - Cognito AdminGetUser returns matching email, given_name, family_name
        /// - DynamoDB GetItem(PK=USER#{userId}, SK=PROFILE) returns all user attributes
        /// - GSI1 query (EMAIL#{email}) finds the user (email lookup index)
        /// - GSI2 query (USERNAME#{username}) finds the user (username lookup index)
        /// </summary>
        [Fact]
        public async Task CreateUser_WithValidData_PersistsInCognitoAndDynamoDb()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email = $"create_{guid}@test.com";
            var password = "TestP@ss123!";
            var username = $"testuser_{guid}";
            var firstName = "Test";
            var lastName = "User";

            Guid userId = Guid.Empty;
            try
            {
                // Act
                userId = await CreateCognitoUser(email, password, username, firstName, lastName);

                // Assert — Cognito: verify user attributes
                var cognitoUser = await _cognitoClient.AdminGetUserAsync(new AdminGetUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email
                });

                cognitoUser.Should().NotBeNull();
                cognitoUser.UserAttributes
                    .FirstOrDefault(a => a.Name == "email")?.Value
                    .Should().Be(email);
                cognitoUser.UserAttributes
                    .FirstOrDefault(a => a.Name == "given_name")?.Value
                    .Should().Be(firstName);
                cognitoUser.UserAttributes
                    .FirstOrDefault(a => a.Name == "family_name")?.Value
                    .Should().Be(lastName);
                cognitoUser.UserAttributes
                    .FirstOrDefault(a => a.Name == "preferred_username")?.Value
                    .Should().Be(username);

                // Assert — DynamoDB: verify profile item
                var dynamoItem = await GetUserFromDynamoDb(userId);
                dynamoItem.Should().NotBeNull();
                dynamoItem!["email"].S.Should().Be(email);
                dynamoItem["username"].S.Should().Be(username);
                dynamoItem["first_name"].S.Should().Be(firstName);
                dynamoItem["last_name"].S.Should().Be(lastName);
                dynamoItem["enabled"].BOOL.Should().BeTrue();
                dynamoItem["verified"].BOOL.Should().BeTrue();
                dynamoItem["EntityType"].S.Should().Be("USER_PROFILE");

                // Assert — GSI1: email lookup index
                var emailQuery = await QueryByEmail(email);
                emailQuery.Items.Should().HaveCount(1);
                emailQuery.Items[0]["id"].S.Should().Be(userId.ToString());

                // Assert — GSI2: username lookup index
                var usernameQuery = await QueryByUsername(username);
                usernameQuery.Items.Should().HaveCount(1);
                usernameQuery.Items[0]["id"].S.Should().Be(userId.ToString());
            }
            finally
            {
                // Cleanup
                if (userId != Guid.Empty)
                    await CleanupUser(email, userId);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: User Creation — All Attributes Preserved
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that ALL user fields from the ErpUser model are correctly
        /// persisted in DynamoDB when creating a user with every attribute populated.
        ///
        /// Fields validated: id, username, email, first_name, last_name, image,
        /// enabled, verified, created_on, preferences.
        ///
        /// This ensures that no field is lost during the migration from PostgreSQL
        /// <c>rec_user</c> table to DynamoDB single-table design.
        /// </summary>
        [Fact]
        public async Task CreateUser_WithAllAttributes_PreservesAllFields()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email = $"allfields_{guid}@test.com";
            var username = $"alluser_{guid}";
            var firstName = "AllFieldsFirst";
            var lastName = "AllFieldsLast";
            var image = "/img/test.jpg";
            var createdOn = DateTime.UtcNow;
            var preferencesJson = "{\"sidebar_size\":\"sm\",\"component_usage\":[],\"component_data_dictionary\":{}}";

            try
            {
                // Act — PutItem in DynamoDB with ALL attributes
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" },
                        ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                        ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" },
                        ["GSI1SK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" },
                        ["GSI2SK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["id"] = new AttributeValue { S = userId.ToString() },
                        ["email"] = new AttributeValue { S = email },
                        ["username"] = new AttributeValue { S = username },
                        ["first_name"] = new AttributeValue { S = firstName },
                        ["last_name"] = new AttributeValue { S = lastName },
                        ["image"] = new AttributeValue { S = image },
                        ["enabled"] = new AttributeValue { BOOL = true },
                        ["verified"] = new AttributeValue { BOOL = true },
                        ["created_on"] = new AttributeValue { S = createdOn.ToString("o") },
                        ["preferences"] = new AttributeValue { S = preferencesJson }
                    }
                });

                // Assert — GetItem returns ALL attributes intact
                var dynamoItem = await GetUserFromDynamoDb(userId);
                dynamoItem.Should().NotBeNull();
                dynamoItem!["id"].S.Should().Be(userId.ToString());
                dynamoItem["email"].S.Should().Be(email);
                dynamoItem["username"].S.Should().Be(username);
                dynamoItem["first_name"].S.Should().Be(firstName);
                dynamoItem["last_name"].S.Should().Be(lastName);
                dynamoItem["image"].S.Should().Be(image);
                dynamoItem["enabled"].BOOL.Should().BeTrue();
                dynamoItem["verified"].BOOL.Should().BeTrue();
                dynamoItem["created_on"].S.Should().NotBeNullOrEmpty();
                dynamoItem["preferences"].S.Should().Be(preferencesJson);
                dynamoItem["EntityType"].S.Should().Be("USER_PROFILE");

                // Verify created_on is a valid ISO 8601 timestamp close to now
                var parsedCreatedOn = DateTime.Parse(dynamoItem["created_on"].S);
                parsedCreatedOn.Should().BeCloseTo(createdOn, TimeSpan.FromMinutes(1));
            }
            finally
            {
                // Cleanup
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" }
                    }
                });
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: User Update — Change Attributes
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that updating a user's attributes (firstName, lastName) persists
        /// changes in both Cognito and DynamoDB.
        ///
        /// Replaces: <c>SecurityManager.SaveUser()</c> UPDATE path (source lines 202-252)
        /// which called <c>recMan.UpdateRecord("user", record)</c>.
        /// </summary>
        [Fact]
        public async Task UpdateUser_ChangeAttributes_PersistsChanges()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email = $"update_{guid}@test.com";
            var password = "TestP@ss123!";
            var username = $"updateuser_{guid}";
            var firstName = "Original";
            var lastName = "Name";

            Guid userId = Guid.Empty;
            try
            {
                userId = await CreateCognitoUser(email, password, username, firstName, lastName);

                var newFirstName = "Updated";
                var newLastName = "LastName";

                // Act — Update in Cognito
                await _cognitoClient.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email,
                    UserAttributes = new List<AttributeType>
                    {
                        new AttributeType { Name = "given_name", Value = newFirstName },
                        new AttributeType { Name = "family_name", Value = newLastName }
                    }
                });

                // Act — Update in DynamoDB
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" }
                    },
                    UpdateExpression = "SET first_name = :fn, last_name = :ln",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":fn"] = new AttributeValue { S = newFirstName },
                        [":ln"] = new AttributeValue { S = newLastName }
                    }
                });

                // Assert — Cognito: verify updated attributes
                var cognitoUser = await _cognitoClient.AdminGetUserAsync(new AdminGetUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email
                });

                cognitoUser.UserAttributes
                    .FirstOrDefault(a => a.Name == "given_name")?.Value
                    .Should().Be(newFirstName);
                cognitoUser.UserAttributes
                    .FirstOrDefault(a => a.Name == "family_name")?.Value
                    .Should().Be(newLastName);

                // Assert — DynamoDB: verify updated attributes
                var dynamoItem = await GetUserFromDynamoDb(userId);
                dynamoItem.Should().NotBeNull();
                dynamoItem!["first_name"].S.Should().Be(newFirstName);
                dynamoItem["last_name"].S.Should().Be(newLastName);
            }
            finally
            {
                // Cleanup
                if (userId != Guid.Empty)
                    await CleanupUser(email, userId);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Username Uniqueness — Create Duplicate
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that attempting to create a user with a duplicate username is
        /// detected via GSI2 lookup and produces the exact error message from the
        /// monolith's <c>SecurityManager.SaveUser()</c> (source lines 269-270):
        /// <c>"Username is already registered to another user. It must be unique."</c>
        /// </summary>
        [Fact]
        public async Task CreateUser_WithDuplicateUsername_FailsWithValidationError()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email1 = $"dupusr1_{guid}@test.com";
            var email2 = $"dupusr2_{guid}@test.com";
            var duplicateUsername = $"uniqueuser_{guid}";
            var password = "TestP@ss123!";

            Guid userId1 = Guid.Empty;
            try
            {
                // Create first user with the username
                userId1 = await CreateCognitoUser(email1, password, duplicateUsername, "First", "User");

                // Act — Query GSI2 to check username uniqueness before creating user2
                var existingUsernameQuery = await QueryByUsername(duplicateUsername);

                // Validate using the same logic as SecurityManager.SaveUser() CREATE path
                var errors = ValidateCreateUser(
                    duplicateUsername, email2, password,
                    existingUsernameQuery, null);

                // Assert — GSI2 query should find existing user
                existingUsernameQuery.Items.Should().HaveCount(1);
                existingUsernameQuery.Items[0]["id"].S.Should().Be(userId1.ToString());

                // Assert — Validation error matches exact message from SecurityManager.cs line 270
                errors.Should().Contain(e =>
                    e.Field == "username" &&
                    e.Message == "Username is already registered to another user. It must be unique.");
            }
            finally
            {
                // Cleanup
                if (userId1 != Guid.Empty)
                    await CleanupUser(email1, userId1);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Username Uniqueness — Update Duplicate
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that attempting to update a user's username to one that already
        /// belongs to another user is detected via GSI2 lookup and produces the exact
        /// error message from the monolith's <c>SecurityManager.SaveUser()</c>
        /// UPDATE path (source lines 212-213):
        /// <c>"Username is already registered to another user. It must be unique."</c>
        /// </summary>
        [Fact]
        public async Task UpdateUser_WithDuplicateUsername_FailsWithValidationError()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email1 = $"user1_{guid}@test.com";
            var email2 = $"user2_{guid}@test.com";
            var username1 = $"user1_{guid}";
            var username2 = $"user2_{guid}";
            var password = "TestP@ss123!";

            Guid userId1 = Guid.Empty;
            Guid userId2 = Guid.Empty;
            try
            {
                // Create two users with distinct usernames
                userId1 = await CreateCognitoUser(email1, password, username1, "User", "One");
                userId2 = await CreateCognitoUser(email2, password, username2, "User", "Two");

                // Act — Attempt to update user2's username to user1's username
                // First check GSI2 for the target username
                var existingUsernameQuery = await QueryByUsername(username1);

                // Validate using update logic matching SecurityManager.SaveUser() UPDATE path
                var errors = ValidateUpdateUsername(username1, userId2, existingUsernameQuery);

                // Assert — GSI2 query should find user1
                existingUsernameQuery.Items.Should().HaveCount(1);

                // Assert — Validation error matches exact message from SecurityManager.cs line 213
                errors.Should().Contain(e =>
                    e.Field == "username" &&
                    e.Message == "Username is already registered to another user. It must be unique.");
            }
            finally
            {
                // Cleanup
                if (userId1 != Guid.Empty)
                    await CleanupUser(email1, userId1);
                if (userId2 != Guid.Empty)
                    await CleanupUser(email2, userId2);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Email Uniqueness — Create Duplicate
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that attempting to create a user with a duplicate email is
        /// detected via GSI1 lookup and produces the exact error message from the
        /// monolith's <c>SecurityManager.SaveUser()</c> (source lines 274-275):
        /// <c>"Email is already registered to another user. It must be unique."</c>
        /// </summary>
        [Fact]
        public async Task CreateUser_WithDuplicateEmail_FailsWithValidationError()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var duplicateEmail = $"dup_{guid}@test.com";
            var username1 = $"emaildup1_{guid}";
            var username2 = $"emaildup2_{guid}";
            var password = "TestP@ss123!";

            Guid userId1 = Guid.Empty;
            try
            {
                // Create first user with the email
                userId1 = await CreateCognitoUser(duplicateEmail, password, username1, "First", "User");

                // Act — Query GSI1 to check email uniqueness before creating user2
                var existingEmailQuery = await QueryByEmail(duplicateEmail);

                // Validate using the same logic as SecurityManager.SaveUser() CREATE path
                var errors = ValidateCreateUser(
                    username2, duplicateEmail, password,
                    null, existingEmailQuery);

                // Assert — GSI1 query should find existing user
                existingEmailQuery.Items.Should().HaveCount(1);
                existingEmailQuery.Items[0]["id"].S.Should().Be(userId1.ToString());

                // Assert — Validation error matches exact message from SecurityManager.cs line 275
                errors.Should().Contain(e =>
                    e.Field == "email" &&
                    e.Message == "Email is already registered to another user. It must be unique.");
            }
            finally
            {
                // Cleanup
                if (userId1 != Guid.Empty)
                    await CleanupUser(duplicateEmail, userId1);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Email Format Validation
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that an invalid email address is rejected using the same
        /// <c>System.Net.Mail.MailAddress</c> validation pattern as the monolith's
        /// <c>SecurityManager.IsValidEmail()</c> (source lines 358-368).
        ///
        /// Error message matches exactly: <c>"Email is not valid."</c>
        /// (source SecurityManager.cs lines 276-277).
        /// </summary>
        [Fact]
        public async Task CreateUser_WithInvalidEmail_FailsWithValidationError()
        {
            // Arrange
            var invalidEmail = "not-an-email";
            var username = $"invalidemail_{Guid.NewGuid():N}"[..20];
            var password = "TestP@ss123!";

            // Act — Validate email format using the monolith's exact pattern
            var isValid = IsValidEmail(invalidEmail);

            // Assert — Email validation should fail
            isValid.Should().BeFalse();

            // Act — Run full validation to get the error message
            var errors = ValidateCreateUser(username, invalidEmail, password, null, null);

            // Assert — Error message matches exactly from SecurityManager.cs lines 276-277
            errors.Should().Contain(e =>
                e.Field == "email" &&
                e.Message == "Email is not valid.");

            await Task.CompletedTask; // Ensure async signature consistency
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Password Validation
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that creating a user without a password produces the exact error
        /// message from the monolith's <c>SecurityManager.SaveUser()</c> CREATE path
        /// (source lines 279-280):
        /// <c>"Password is required."</c>
        /// </summary>
        [Fact]
        public async Task CreateUser_WithoutPassword_FailsWithValidationError()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email = $"nopass_{guid}@test.com";
            var username = $"nopassuser_{guid}";

            // Act — Validate with empty password
            var errorsEmpty = ValidateCreateUser(username, email, "", null, null);

            // Assert — Error matches exactly from SecurityManager.cs line 280
            errorsEmpty.Should().Contain(e =>
                e.Field == "password" &&
                e.Message == "Password is required.");

            // Act — Validate with null password
            var errorsNull = ValidateCreateUser(username, email, null, null, null);

            // Assert — Error matches for null password as well
            errorsNull.Should().Contain(e =>
                e.Field == "password" &&
                e.Message == "Password is required.");

            // Act — Validate with whitespace-only password
            var errorsWhitespace = ValidateCreateUser(username, email, "   ", null, null);

            // Assert — Error matches for whitespace password
            errorsWhitespace.Should().Contain(e =>
                e.Field == "password" &&
                e.Message == "Password is required.");

            await Task.CompletedTask; // Ensure async signature consistency
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Role Assignment — Create With Roles
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that assigning roles to a user correctly persists in both Cognito
        /// (group membership) and DynamoDB (role assignment items).
        ///
        /// Replaces: <c>record["$user_role.id"] = user.Roles.Select(x => x.Id).ToList()</c>
        /// (source SecurityManager.cs lines 246, 284) which used PostgreSQL many-to-many
        /// <c>rel_user_role</c> join table.
        /// </summary>
        [Fact]
        public async Task CreateUser_WithRoles_AssignsToCorrectCognitoGroups()
        {
            // Arrange
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email = $"withroles_{guid}@test.com";
            var password = "TestP@ss123!";
            var username = $"roleuser_{guid}";

            Guid userId = Guid.Empty;
            try
            {
                // Create user
                userId = await CreateCognitoUser(email, password, username, "Role", "User");

                // Act — Assign administrator and regular roles
                await AssignRoleToUser(email, userId, Role.AdministratorRoleId, "administrator");
                await AssignRoleToUser(email, userId, Role.RegularRoleId, "regular");

                // Assert — Cognito: user should be in both groups
                var groupsResponse = await _cognitoClient.AdminListGroupsForUserAsync(
                    new AdminListGroupsForUserRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = email
                    });

                var groupNames = groupsResponse.Groups.Select(g => g.GroupName).ToList();
                groupNames.Should().Contain("administrator");
                groupNames.Should().Contain("regular");
                groupNames.Should().NotContain("guest");

                // Assert — DynamoDB: user-role assignment items
                var roleItems = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"USER#{userId}" },
                        [":skPrefix"] = new AttributeValue { S = "ROLE#" }
                    }
                });

                roleItems.Items.Should().HaveCount(2);
                var roleIds = roleItems.Items.Select(i => i["role_id"].S).ToList();
                roleIds.Should().Contain(Role.AdministratorRoleId.ToString());
                roleIds.Should().Contain(Role.RegularRoleId.ToString());
            }
            finally
            {
                // Cleanup
                if (userId != Guid.Empty)
                {
                    await CleanupUser(email, userId);
                    await CleanupRoleMemberships(Role.AdministratorRoleId);
                    await CleanupRoleMemberships(Role.RegularRoleId);
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Role Membership Query — Get Users By Role
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that querying for users by role correctly returns only
        /// users assigned to that specific role.
        ///
        /// Replaces: <c>SecurityManager.GetUsers(params Guid[] roleIds)</c>
        /// (source lines 167-184) which built dynamic EQL with <c>$user_role.id</c> filters.
        /// </summary>
        [Fact]
        public async Task GetUsersByRole_ReturnsCorrectUsers()
        {
            // Arrange — Create 3 users
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email1 = $"byrole1_{guid}@test.com";
            var email2 = $"byrole2_{guid}@test.com";
            var email3 = $"byrole3_{guid}@test.com";
            var password = "TestP@ss123!";

            Guid userId1 = Guid.Empty;
            Guid userId2 = Guid.Empty;
            Guid userId3 = Guid.Empty;

            try
            {
                userId1 = await CreateCognitoUser(email1, password, $"byrole1_{guid}", "User", "One");
                userId2 = await CreateCognitoUser(email2, password, $"byrole2_{guid}", "User", "Two");
                userId3 = await CreateCognitoUser(email3, password, $"byrole3_{guid}", "User", "Three");

                // Assign user1 and user2 to administrator, user3 to regular
                await AssignRoleToUser(email1, userId1, Role.AdministratorRoleId, "administrator");
                await AssignRoleToUser(email2, userId2, Role.AdministratorRoleId, "administrator");
                await AssignRoleToUser(email3, userId3, Role.RegularRoleId, "regular");

                // Act — Query for all administrator role members
                var adminMembers = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"ROLE#{Role.AdministratorRoleId}" },
                        [":skPrefix"] = new AttributeValue { S = "MEMBER#" }
                    }
                });

                // Assert — Should find user1 and user2 (not user3)
                var adminUserIds = adminMembers.Items.Select(i => i["user_id"].S).ToList();
                adminUserIds.Should().Contain(userId1.ToString());
                adminUserIds.Should().Contain(userId2.ToString());
                adminUserIds.Should().NotContain(userId3.ToString());

                // Act — Query for all regular role members
                var regularMembers = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"ROLE#{Role.RegularRoleId}" },
                        [":skPrefix"] = new AttributeValue { S = "MEMBER#" }
                    }
                });

                // Assert — Should find user3 (not user1 or user2)
                var regularUserIds = regularMembers.Items.Select(i => i["user_id"].S).ToList();
                regularUserIds.Should().Contain(userId3.ToString());
                regularUserIds.Should().NotContain(userId1.ToString());
                regularUserIds.Should().NotContain(userId2.ToString());

                // Also verify guest role has no test user members
                // Uses Role.GuestRoleId constant (replacing SystemIds.GuestRoleId from monolith)
                var guestMembers = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"ROLE#{Role.GuestRoleId}" },
                        [":skPrefix"] = new AttributeValue { S = "MEMBER#" }
                    }
                });

                var guestUserIds = guestMembers.Items.Select(i => i["user_id"].S).ToList();
                guestUserIds.Should().NotContain(userId1.ToString());
                guestUserIds.Should().NotContain(userId2.ToString());
                guestUserIds.Should().NotContain(userId3.ToString());
            }
            finally
            {
                // Cleanup
                if (userId1 != Guid.Empty) await CleanupUser(email1, userId1);
                if (userId2 != Guid.Empty) await CleanupUser(email2, userId2);
                if (userId3 != Guid.Empty) await CleanupUser(email3, userId3);
                await CleanupRoleMemberships(Role.AdministratorRoleId);
                await CleanupRoleMemberships(Role.RegularRoleId);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: Last Login Timestamp Update
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that updating a user's last login timestamp persists correctly
        /// in DynamoDB using UpdateItem with SET expression.
        ///
        /// Replaces: <c>SecurityManager.UpdateUserLastLoginTime(Guid userId)</c>
        /// (source lines 350-356) which set <c>DateTime.UtcNow</c> via
        /// <c>CurrentContext.RecordRepository.Update("user", storageRecordData)</c>.
        /// </summary>
        [Fact]
        public async Task UpdateUserLastLoginTime_PersistsTimestamp()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var guid = Guid.NewGuid().ToString("N")[..8];
            var email = $"lastlogin_{guid}@test.com";
            var username = $"lastloginuser_{guid}";
            var createdOn = DateTime.UtcNow.AddDays(-7);

            try
            {
                // Create user profile in DynamoDB
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" },
                        ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                        ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" },
                        ["GSI1SK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" },
                        ["GSI2SK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["id"] = new AttributeValue { S = userId.ToString() },
                        ["email"] = new AttributeValue { S = email },
                        ["username"] = new AttributeValue { S = username },
                        ["first_name"] = new AttributeValue { S = "Login" },
                        ["last_name"] = new AttributeValue { S = "Test" },
                        ["enabled"] = new AttributeValue { BOOL = true },
                        ["verified"] = new AttributeValue { BOOL = true },
                        ["created_on"] = new AttributeValue { S = createdOn.ToString("o") }
                    }
                });

                // Act — Update last_logged_in timestamp (matching source pattern lines 350-356)
                var loginTime = DateTime.UtcNow;
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" }
                    },
                    UpdateExpression = "SET last_logged_in = :ts",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":ts"] = new AttributeValue { S = loginTime.ToString("o") }
                    }
                });

                // Assert — Verify timestamp was persisted
                var dynamoItem = await GetUserFromDynamoDb(userId);
                dynamoItem.Should().NotBeNull();
                dynamoItem!.ContainsKey("last_logged_in").Should().BeTrue();

                var persistedTimestamp = DateTime.Parse(dynamoItem["last_logged_in"].S);
                persistedTimestamp.Should().BeCloseTo(loginTime, TimeSpan.FromMinutes(1));
            }
            finally
            {
                // Cleanup
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" }
                    }
                });
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Test: User Listing — Get All Users
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Validates that a DynamoDB Scan with EntityType filter returns all created
        /// user profiles. This tests the listing pattern that replaces the monolith's
        /// EQL-based user listing.
        /// </summary>
        [Fact]
        public async Task GetAllUsers_ReturnsAllCreatedUsers()
        {
            // Arrange — Create 3 test users in DynamoDB
            var guid = Guid.NewGuid().ToString("N")[..8];
            var testUsers = new List<(Guid Id, string Email, string Username)>();

            for (int i = 1; i <= 3; i++)
            {
                var userId = Guid.NewGuid();
                var email = $"listuser{i}_{guid}@test.com";
                var username = $"listuser{i}_{guid}";

                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" },
                        ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                        ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" },
                        ["GSI1SK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" },
                        ["GSI2SK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["id"] = new AttributeValue { S = userId.ToString() },
                        ["email"] = new AttributeValue { S = email },
                        ["username"] = new AttributeValue { S = username },
                        ["first_name"] = new AttributeValue { S = $"User{i}" },
                        ["last_name"] = new AttributeValue { S = "Test" },
                        ["enabled"] = new AttributeValue { BOOL = true },
                        ["verified"] = new AttributeValue { BOOL = true },
                        ["created_on"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") }
                    }
                });

                testUsers.Add((userId, email, username));
            }

            try
            {
                // Act — Scan DynamoDB with EntityType filter for USER_PROFILE
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :entityType",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":entityType"] = new AttributeValue { S = "USER_PROFILE" }
                    }
                });

                // Assert — All 3 test users should be in the scan results
                var scannedUserIds = scanResponse.Items
                    .Where(i => i.ContainsKey("id"))
                    .Select(i => i["id"].S)
                    .ToList();

                foreach (var testUser in testUsers)
                {
                    scannedUserIds.Should().Contain(testUser.Id.ToString());
                }

                // Assert — Scan returned at least 3 items (there may be system seeded items)
                scanResponse.Items.Count.Should().BeGreaterThanOrEqualTo(3);

                // Verify none of the test users are the system user
                // Uses User.SystemUserId constant (replacing SystemIds.SystemUserId from monolith)
                foreach (var testUser in testUsers)
                {
                    testUser.Id.Should().NotBe(User.SystemUserId);
                }
            }
            finally
            {
                // Cleanup — Delete all test users
                foreach (var testUser in testUsers)
                {
                    try
                    {
                        await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                        {
                            TableName = _tableName,
                            Key = new Dictionary<string, AttributeValue>
                            {
                                ["PK"] = new AttributeValue { S = $"USER#{testUser.Id}" },
                                ["SK"] = new AttributeValue { S = "PROFILE" }
                            }
                        });
                    }
                    catch (Exception)
                    {
                        // Best-effort cleanup
                    }
                }
            }
        }
    }
}
