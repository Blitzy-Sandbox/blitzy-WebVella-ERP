using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Xunit;
using WebVellaErp.Identity.Models;

namespace WebVellaErp.Identity.Tests.Integration
{
    /// <summary>
    /// Integration tests verifying the DynamoDB single-table design for the Identity service.
    /// Tests validate table structure, GSI queries, user-role relationship modeling,
    /// and CRUD operations against real LocalStack DynamoDB.
    ///
    /// Per AAP Section 0.8.4: "All integration and E2E tests MUST execute against
    /// LocalStack. No mocked AWS SDK calls in integration tests."
    ///
    /// DynamoDB key patterns validated:
    /// <list type="bullet">
    ///   <item><description>PK=USER#{userId}, SK=PROFILE — user profile items</description></item>
    ///   <item><description>PK=ROLE#{roleId}, SK=META — role metadata items</description></item>
    ///   <item><description>PK=USER#{userId}, SK=ROLE#{roleId} — user→role forward link</description></item>
    ///   <item><description>PK=ROLE#{roleId}, SK=MEMBER#{userId} — role→user reverse link</description></item>
    ///   <item><description>GSI1PK=EMAIL#{email}, GSI1SK=USER — email lookup index</description></item>
    ///   <item><description>GSI2PK=USERNAME#{username}, GSI2SK=USER — username lookup index</description></item>
    /// </list>
    ///
    /// Source mapping:
    /// <list type="bullet">
    ///   <item><description>Table schema → replaces PostgreSQL rec_user / rec_role tables</description></item>
    ///   <item><description>GSI1 email lookup → replaces EQL "SELECT * FROM user WHERE email = @email"
    ///     (SecurityManager.cs GetUser)</description></item>
    ///   <item><description>GSI2 username lookup → replaces EQL "SELECT * FROM user WHERE username = @username"
    ///     (SecurityManager.cs GetUserByUsername)</description></item>
    ///   <item><description>User-role bidirectional items → replaces rel_user_role join table
    ///     (SecurityManager.cs GetSystemUserWithNoSecurityCheck JOIN)</description></item>
    ///   <item><description>EntityType discriminator → replaces separate PostgreSQL tables</description></item>
    /// </list>
    /// </summary>
    public class DynamoDbPersistenceIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly string _tableName;

        /// <summary>
        /// Constructor receives the shared <see cref="LocalStackFixture"/> which provisions
        /// a real DynamoDB table with PK/SK primary key + GSI1 (email) + GSI2 (username)
        /// in LocalStack before any tests execute. Also seeds system roles.
        /// </summary>
        public DynamoDbPersistenceIntegrationTests(LocalStackFixture fixture)
        {
            _dynamoDbClient = fixture.DynamoDbClient;
            _tableName = fixture.TableName;
        }

        #region Private Helper Methods

        /// <summary>
        /// Builds a complete user profile DynamoDB item with all attributes from the
        /// monolith's ErpUser model (source: ErpUser.cs), following the single-table design.
        /// Attributes stored: id, email, username, first_name, last_name, image, enabled,
        /// verified, created_on, preferences, plus key and GSI attributes.
        /// </summary>
        private Dictionary<string, AttributeValue> BuildUserProfileItem(
            string userId,
            string email,
            string username,
            string firstName = "Test",
            string lastName = "User",
            string? image = "/img/test.png",
            bool enabled = true,
            bool verified = true,
            DateTime? createdOn = null,
            string preferences = "{}")
        {
            var now = createdOn ?? DateTime.UtcNow;
            var item = new Dictionary<string, AttributeValue>
            {
                // Primary key
                ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                ["SK"] = new AttributeValue { S = "PROFILE" },
                // GSI1: email-based lookup (lowercased for case-insensitive matching)
                ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" },
                ["GSI1SK"] = new AttributeValue { S = "USER" },
                // GSI2: username-based lookup (lowercased for case-insensitive matching)
                ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" },
                ["GSI2SK"] = new AttributeValue { S = "USER" },
                // Entity type discriminator for scan filtering
                ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                // User attributes (all from ErpUser model)
                ["id"] = new AttributeValue { S = userId },
                ["email"] = new AttributeValue { S = email },
                ["username"] = new AttributeValue { S = username },
                ["first_name"] = new AttributeValue { S = firstName },
                ["last_name"] = new AttributeValue { S = lastName },
                ["enabled"] = new AttributeValue { BOOL = enabled },
                ["verified"] = new AttributeValue { BOOL = verified },
                ["created_on"] = new AttributeValue { S = now.ToString("o") },
                ["preferences"] = new AttributeValue { S = preferences }
            };

            if (image != null)
            {
                item["image"] = new AttributeValue { S = image };
            }

            return item;
        }

        /// <summary>
        /// Builds a role metadata DynamoDB item following the single-table design.
        /// PK=ROLE#{roleId}, SK=META, EntityType=ROLE_META.
        /// </summary>
        private Dictionary<string, AttributeValue> BuildRoleMetaItem(
            string roleId,
            string name,
            string description)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                ["SK"] = new AttributeValue { S = "META" },
                ["EntityType"] = new AttributeValue { S = "ROLE_META" },
                ["id"] = new AttributeValue { S = roleId },
                ["name"] = new AttributeValue { S = name },
                ["description"] = new AttributeValue { S = description }
            };
        }

        /// <summary>
        /// Builds a user-role forward link item (user has role).
        /// PK=USER#{userId}, SK=ROLE#{roleId}, EntityType=USER_ROLE.
        /// </summary>
        private Dictionary<string, AttributeValue> BuildUserRoleForwardItem(
            string userId, string roleId, string roleName)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                ["SK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                ["EntityType"] = new AttributeValue { S = "USER_ROLE" },
                ["user_id"] = new AttributeValue { S = userId },
                ["role_id"] = new AttributeValue { S = roleId },
                ["role_name"] = new AttributeValue { S = roleName }
            };
        }

        /// <summary>
        /// Builds a role-user reverse link item (role has member).
        /// PK=ROLE#{roleId}, SK=MEMBER#{userId}, EntityType=ROLE_MEMBER.
        /// </summary>
        private Dictionary<string, AttributeValue> BuildRoleMemberReverseItem(
            string roleId, string userId, string username)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                ["SK"] = new AttributeValue { S = $"MEMBER#{userId}" },
                ["EntityType"] = new AttributeValue { S = "ROLE_MEMBER" },
                ["role_id"] = new AttributeValue { S = roleId },
                ["user_id"] = new AttributeValue { S = userId },
                ["username"] = new AttributeValue { S = username }
            };
        }

        /// <summary>
        /// Puts an item into the identity table.
        /// </summary>
        private async Task PutItemAsync(Dictionary<string, AttributeValue> item)
        {
            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item
            });
        }

        /// <summary>
        /// Gets an item by PK and SK from the identity table.
        /// Returns empty Item dictionary if item does not exist.
        /// </summary>
        private async Task<GetItemResponse> GetItemAsync(string pk, string sk)
        {
            return await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = pk },
                    ["SK"] = new AttributeValue { S = sk }
                }
            });
        }

        /// <summary>
        /// Deletes an item by PK and SK from the identity table.
        /// </summary>
        private async Task DeleteItemAsync(string pk, string sk)
        {
            await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = pk },
                    ["SK"] = new AttributeValue { S = sk }
                }
            });
        }

        /// <summary>
        /// Safely deletes an item, ignoring errors if the item does not exist.
        /// Used in test cleanup to prevent masking test assertion failures.
        /// </summary>
        private async Task SafeDeleteItemAsync(string pk, string sk)
        {
            try
            {
                await DeleteItemAsync(pk, sk);
            }
            catch (Exception)
            {
                // Ignored during cleanup
            }
        }

        #endregion

        // ──────────────────────────────────────────────────────────────────────
        // Table Schema Verification Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies the DynamoDB identity table exists with the correct single-table design
        /// schema: PK (HASH, String) + SK (RANGE, String). Table status must be ACTIVE
        /// after provisioning by <see cref="LocalStackFixture"/>.
        /// </summary>
        [Fact]
        public async Task IdentityTable_ExistsWithCorrectSchema()
        {
            // Arrange: Table is already created by LocalStackFixture.InitializeAsync

            // Act: Describe the table to inspect its schema
            var response = await _dynamoDbClient.DescribeTableAsync(new DescribeTableRequest
            {
                TableName = _tableName
            });

            // Assert: Table status is ACTIVE
            response.Table.TableStatus.Value.Should().Be("ACTIVE");

            // Assert: Key schema has PK (HASH) and SK (RANGE) — exactly 2 key elements
            var keySchema = response.Table.KeySchema;
            keySchema.Should().HaveCount(2);

            var pk = keySchema.First(k => k.AttributeName == "PK");
            pk.KeyType.Value.Should().Be("HASH");

            var sk = keySchema.First(k => k.AttributeName == "SK");
            sk.KeyType.Value.Should().Be("RANGE");

            // Assert: Both keys are String type (S) per DynamoDB single-table design
            var pkAttr = response.Table.AttributeDefinitions.First(a => a.AttributeName == "PK");
            pkAttr.AttributeType.Value.Should().Be("S");

            var skAttr = response.Table.AttributeDefinitions.First(a => a.AttributeName == "SK");
            skAttr.AttributeType.Value.Should().Be("S");
        }

        /// <summary>
        /// Verifies GSI1 exists for email-based user lookups with correct key schema.
        /// GSI1PK (HASH, String) + GSI1SK (RANGE, String) with ALL projection.
        /// Replaces: <c>EqlCommand("SELECT * FROM user WHERE email = @email")</c>
        /// from SecurityManager.cs line 54.
        /// </summary>
        [Fact]
        public async Task IdentityTable_HasGSI1ForEmailLookup()
        {
            // Act: Describe the table to inspect GSI configuration
            var response = await _dynamoDbClient.DescribeTableAsync(new DescribeTableRequest
            {
                TableName = _tableName
            });

            // Assert: GSI1 exists in the list of global secondary indexes
            response.Table.GlobalSecondaryIndexes
                .Select(g => g.IndexName)
                .Should().Contain("GSI1");

            var gsi1 = response.Table.GlobalSecondaryIndexes.First(g => g.IndexName == "GSI1");
            gsi1.Should().NotBeNull();

            // Assert: GSI1 key schema — GSI1PK as HASH, GSI1SK as RANGE
            var gsi1Pk = gsi1.KeySchema.First(k => k.AttributeName == "GSI1PK");
            gsi1Pk.KeyType.Value.Should().Be("HASH");

            var gsi1Sk = gsi1.KeySchema.First(k => k.AttributeName == "GSI1SK");
            gsi1Sk.KeyType.Value.Should().Be("RANGE");

            // Assert: GSI1 projection includes ALL attributes for complete user retrieval
            gsi1.Projection.ProjectionType.Value.Should().Be("ALL");
        }

        /// <summary>
        /// Verifies GSI2 exists for username-based user lookups with correct key schema.
        /// GSI2PK (HASH, String) + GSI2SK (RANGE, String) with ALL projection.
        /// Replaces: <c>EqlCommand("SELECT * FROM user WHERE username = @username")</c>
        /// from SecurityManager.cs line 68.
        /// </summary>
        [Fact]
        public async Task IdentityTable_HasGSI2ForUsernameLookup()
        {
            // Act: Describe the table to inspect GSI configuration
            var response = await _dynamoDbClient.DescribeTableAsync(new DescribeTableRequest
            {
                TableName = _tableName
            });

            // Assert: GSI2 exists in the list of global secondary indexes
            response.Table.GlobalSecondaryIndexes
                .Select(g => g.IndexName)
                .Should().Contain("GSI2");

            var gsi2 = response.Table.GlobalSecondaryIndexes.First(g => g.IndexName == "GSI2");
            gsi2.Should().NotBeNull();

            // Assert: GSI2 key schema — GSI2PK as HASH, GSI2SK as RANGE
            var gsi2Pk = gsi2.KeySchema.First(k => k.AttributeName == "GSI2PK");
            gsi2Pk.KeyType.Value.Should().Be("HASH");

            var gsi2Sk = gsi2.KeySchema.First(k => k.AttributeName == "GSI2SK");
            gsi2Sk.KeyType.Value.Should().Be("RANGE");

            // Assert: GSI2 projection includes ALL attributes
            gsi2.Projection.ProjectionType.Value.Should().Be("ALL");
        }

        // ──────────────────────────────────────────────────────────────────────
        // User Profile CRUD Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies complete round-trip persistence of a user profile with ALL attributes
        /// from the monolith's ErpUser model (source: ErpUser.cs). Every attribute is stored
        /// in DynamoDB and retrieved exactly as written.
        ///
        /// Attributes validated: id, email, username, first_name, last_name, image,
        /// enabled (BOOL), verified (BOOL), created_on (ISO 8601), preferences (JSON string),
        /// EntityType discriminator. last_logged_in is omitted (null behavior).
        /// </summary>
        [Fact]
        public async Task UserProfile_PutItem_GetItem_RoundTrip()
        {
            // Arrange: Build a complete user profile item with unique identifiers
            var userId = Guid.NewGuid().ToString();
            var email = $"roundtrip_{userId}@test.com";
            var username = $"rtuser_{userId}";
            var createdOn = DateTime.UtcNow;

            var item = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                ["SK"] = new AttributeValue { S = "PROFILE" },
                ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email}" },
                ["GSI1SK"] = new AttributeValue { S = "USER" },
                ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username}" },
                ["GSI2SK"] = new AttributeValue { S = "USER" },
                ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                ["id"] = new AttributeValue { S = userId },
                ["email"] = new AttributeValue { S = email },
                ["username"] = new AttributeValue { S = username },
                ["first_name"] = new AttributeValue { S = "Test" },
                ["last_name"] = new AttributeValue { S = "User" },
                ["image"] = new AttributeValue { S = "/img/test.png" },
                ["enabled"] = new AttributeValue { BOOL = true },
                ["verified"] = new AttributeValue { BOOL = true },
                ["created_on"] = new AttributeValue { S = createdOn.ToString("o") },
                // last_logged_in intentionally omitted — models null from ErpUser.LastLoggedIn
                ["preferences"] = new AttributeValue { S = "{}" }
            };

            try
            {
                // Act: PutItem to store the user profile
                await PutItemAsync(item);

                // Act: GetItem to retrieve it by primary key
                var getResponse = await GetItemAsync($"USER#{userId}", "PROFILE");

                // Assert: Item exists and is not empty
                getResponse.Item.Should().NotBeNull();
                getResponse.Item.Should().NotBeEmpty();

                // Assert: Each user attribute individually (mirrors ErpUser properties)
                getResponse.Item["id"].S.Should().Be(userId);
                getResponse.Item["email"].S.Should().Be(email);
                getResponse.Item["username"].S.Should().Be(username);
                getResponse.Item["first_name"].S.Should().Be("Test");
                getResponse.Item["last_name"].S.Should().Be("User");
                getResponse.Item["image"].S.Should().Be("/img/test.png");
                getResponse.Item["enabled"].BOOL.Should().BeTrue();
                getResponse.Item["verified"].BOOL.Should().BeTrue();
                getResponse.Item["created_on"].S.Should().NotBeEmpty();
                getResponse.Item["EntityType"].S.Should().Be("USER_PROFILE");
                getResponse.Item["preferences"].S.Should().Be("{}");

                // Assert: created_on is a valid ISO 8601 datetime string
                DateTime.TryParse(getResponse.Item["created_on"].S, out var parsedDate)
                    .Should().BeTrue();

                // Assert: last_logged_in should not exist (omitted = null behavior)
                getResponse.Item.ContainsKey("last_logged_in").Should().BeFalse();
            }
            finally
            {
                // Cleanup: Remove the test user profile
                await SafeDeleteItemAsync($"USER#{userId}", "PROFILE");
            }
        }

        /// <summary>
        /// Verifies that UpdateItem correctly persists changes to specific user attributes
        /// without affecting other attributes. Tests the DynamoDB UpdateExpression SET operation.
        /// </summary>
        [Fact]
        public async Task UserProfile_UpdateItem_PersistsChanges()
        {
            // Arrange: Create a user profile to update
            var userId = Guid.NewGuid().ToString();
            var email = $"update_{userId}@test.com";
            var username = $"updateuser_{userId}";
            var item = BuildUserProfileItem(userId, email, username,
                firstName: "Original", lastName: "Name", image: "/img/original.png");

            try
            {
                await PutItemAsync(item);

                // Act: Update specific attributes using SET expression
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                        ["SK"] = new AttributeValue { S = "PROFILE" }
                    },
                    UpdateExpression = "SET first_name = :fn, last_name = :ln, image = :img",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":fn"] = new AttributeValue { S = "Updated" },
                        [":ln"] = new AttributeValue { S = "Person" },
                        [":img"] = new AttributeValue { S = "/img/updated.png" }
                    }
                });

                // Assert: Updated fields have new values
                var getResponse = await GetItemAsync($"USER#{userId}", "PROFILE");
                getResponse.Item["first_name"].S.Should().Be("Updated");
                getResponse.Item["last_name"].S.Should().Be("Person");
                getResponse.Item["image"].S.Should().Be("/img/updated.png");

                // Assert: Non-updated fields remain unchanged
                getResponse.Item["email"].S.Should().Be(email);
                getResponse.Item["username"].S.Should().Be(username);
                getResponse.Item["enabled"].BOOL.Should().BeTrue();
                getResponse.Item["verified"].BOOL.Should().BeTrue();
                getResponse.Item["EntityType"].S.Should().Be("USER_PROFILE");
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", "PROFILE");
            }
        }

        /// <summary>
        /// Verifies that DeleteItem completely removes a user profile entry from DynamoDB.
        /// After deletion, GetItem should return an empty item dictionary.
        /// </summary>
        [Fact]
        public async Task UserProfile_DeleteItem_RemovesEntry()
        {
            // Arrange: Create a user profile to delete
            var userId = Guid.NewGuid().ToString();
            var email = $"delete_{userId}@test.com";
            var username = $"deluser_{userId}";
            var item = BuildUserProfileItem(userId, email, username);

            await PutItemAsync(item);

            // Act: Delete the user profile
            await DeleteItemAsync($"USER#{userId}", "PROFILE");

            // Assert: GetItem returns an empty item (item no longer exists)
            var getResponse = await GetItemAsync($"USER#{userId}", "PROFILE");
            getResponse.Item.Should().BeEmpty();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Email GSI Query Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that querying GSI1 by email finds the correct user.
        /// Replaces: <c>SecurityManager.GetUser(string email)</c> (source lines 49-61)
        /// which used <c>EqlCommand("SELECT * FROM user WHERE email = @email")</c>.
        /// </summary>
        [Fact]
        public async Task EmailGSI_QueryByEmail_FindsCorrectUser()
        {
            // Arrange: Create a user with a unique email for GSI1 lookup
            var userId = Guid.NewGuid().ToString();
            var email = $"gsi1test_{userId}@test.com";
            var username = $"gsi1user_{userId}";
            var item = BuildUserProfileItem(userId, email, username);

            try
            {
                await PutItemAsync(item);

                // Act: Query GSI1 by email
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" }
                    }
                });

                // Assert: Exactly 1 item returned with correct userId and email
                queryResponse.Items.Should().HaveCount(1);
                var foundItem = queryResponse.Items.First();
                foundItem["id"].S.Should().Be(userId);
                foundItem["email"].S.Should().Be(email);
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", "PROFILE");
            }
        }

        /// <summary>
        /// Verifies that querying GSI1 with a non-existent email returns an empty result set.
        /// Equivalent to source SecurityManager.GetUser returning null (lines 57-58).
        /// </summary>
        [Fact]
        public async Task EmailGSI_QueryByNonExistentEmail_ReturnsEmpty()
        {
            // Act: Query GSI1 with an email that does not exist in the table
            var nonExistentEmail = $"doesnotexist_{Guid.NewGuid()}@test.com";
            var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                IndexName = "GSI1",
                KeyConditionExpression = "GSI1PK = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"EMAIL#{nonExistentEmail}" }
                }
            });

            // Assert: 0 items returned
            queryResponse.Items.Should().BeEmpty();
            queryResponse.Count.Should().Be(0);
        }

        /// <summary>
        /// Verifies case-insensitive email storage and lookup via GSI1.
        /// Emails are lowercased before storage in GSI1PK, matching source
        /// SecurityManager.cs line 85 behavior: <c>email.ToLowerInvariant()</c>.
        /// </summary>
        [Fact]
        public async Task EmailGSI_CaseInsensitiveStorage_FindsUser()
        {
            // Arrange: Store user with mixed-case email, but GSI1PK stores lowercased version
            var userId = Guid.NewGuid().ToString();
            var mixedCaseEmail = $"Mixed.Case_{userId}@Test.Com";
            var username = $"caseuser_{userId}";
            // BuildUserProfileItem lowercases the email for GSI1PK
            var item = BuildUserProfileItem(userId, mixedCaseEmail, username);

            try
            {
                await PutItemAsync(item);

                // Act: Query with the lowercased version (how the application should query)
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI1",
                    KeyConditionExpression = "GSI1PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"EMAIL#{mixedCaseEmail.ToLowerInvariant()}" }
                    }
                });

                // Assert: User is found via case-insensitive GSI1 lookup
                queryResponse.Items.Should().HaveCount(1);
                var foundItem = queryResponse.Items.First();
                foundItem["id"].S.Should().Be(userId);
                // Original mixed-case email is preserved in the email attribute
                foundItem["email"].S.Should().Be(mixedCaseEmail);
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", "PROFILE");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Username GSI Query Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that querying GSI2 by username finds the correct user.
        /// Replaces: <c>SecurityManager.GetUserByUsername(string username)</c>
        /// (source lines 63-75) which used EQL <c>WHERE username = @username</c>.
        /// </summary>
        [Fact]
        public async Task UsernameGSI_QueryByUsername_FindsCorrectUser()
        {
            // Arrange: Create a user with a unique username for GSI2 lookup
            var userId = Guid.NewGuid().ToString();
            var email = $"gsi2test_{userId}@test.com";
            var username = $"testgsi2user_{userId}";
            var item = BuildUserProfileItem(userId, email, username);

            try
            {
                await PutItemAsync(item);

                // Act: Query GSI2 by username
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    IndexName = "GSI2",
                    KeyConditionExpression = "GSI2PK = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" }
                    }
                });

                // Assert: Exactly 1 item returned with correct userId
                queryResponse.Items.Should().HaveCount(1);
                var foundItem = queryResponse.Items.First();
                foundItem["id"].S.Should().Be(userId);
                foundItem["username"].S.Should().Be(username);
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", "PROFILE");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // User-Role Relationship Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that bidirectional user-role relationship items are correctly created.
        /// Forward link: PK=USER#{userId}, SK=ROLE#{roleId} (user has this role).
        /// Reverse link: PK=ROLE#{roleId}, SK=MEMBER#{userId} (role has this member).
        /// Replaces: the <c>rel_user_role</c> many-to-many join table in PostgreSQL.
        /// Uses <see cref="Role.AdministratorRoleId"/> for the well-known system role.
        /// </summary>
        [Fact]
        public async Task UserRoleRelationship_BidirectionalItems_Created()
        {
            // Arrange: Use well-known administrator role ID from the Role model constants
            var userId = Guid.NewGuid().ToString();
            var roleId = Role.AdministratorRoleId.ToString();
            var username = $"bidir_{userId}";

            var forwardItem = BuildUserRoleForwardItem(userId, roleId, "administrator");
            var reverseItem = BuildRoleMemberReverseItem(roleId, userId, username);

            try
            {
                // Act: Create both directional relationship items
                await PutItemAsync(forwardItem);
                await PutItemAsync(reverseItem);

                // Assert: Forward link exists — user has the role
                var forwardResponse = await GetItemAsync($"USER#{userId}", $"ROLE#{roleId}");
                forwardResponse.Item.Should().NotBeEmpty();
                forwardResponse.Item["role_id"].S.Should().Be(roleId);
                forwardResponse.Item["user_id"].S.Should().Be(userId);

                // Assert: Reverse link exists — role has the member
                var reverseResponse = await GetItemAsync($"ROLE#{roleId}", $"MEMBER#{userId}");
                reverseResponse.Item.Should().NotBeEmpty();
                reverseResponse.Item["user_id"].S.Should().Be(userId);
                reverseResponse.Item["role_id"].S.Should().Be(roleId);
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", $"ROLE#{roleId}");
                await SafeDeleteItemAsync($"ROLE#{roleId}", $"MEMBER#{userId}");
            }
        }

        /// <summary>
        /// Verifies querying a user's assigned roles using begins_with on the sort key.
        /// Query: PK=USER#{userId}, SK begins_with "ROLE#"
        /// Replaces: PostgreSQL JOIN from GetSystemUserWithNoSecurityCheck (SecurityManager.cs
        /// lines 140-146):
        /// <c>SELECT r.* FROM rec_role r LEFT OUTER JOIN rel_user_role ur
        /// ON ur.origin_id = r.id WHERE ur.target_id = @user_id</c>
        ///
        /// Uses <see cref="Role.AdministratorRoleId"/> and <see cref="Role.RegularRoleId"/>
        /// as the assigned roles.
        /// </summary>
        [Fact]
        public async Task GetUserRoles_QueryByUserId_ReturnsRoles()
        {
            // Arrange: Create user and assign 2 system roles (admin + regular)
            var userId = Guid.NewGuid().ToString();
            var username = $"multirole_{userId}";
            var adminRoleId = Role.AdministratorRoleId.ToString();
            var regularRoleId = Role.RegularRoleId.ToString();

            // Create forward + reverse items for each role (4 items total)
            await PutItemAsync(BuildUserRoleForwardItem(userId, adminRoleId, "administrator"));
            await PutItemAsync(BuildRoleMemberReverseItem(adminRoleId, userId, username));
            await PutItemAsync(BuildUserRoleForwardItem(userId, regularRoleId, "regular"));
            await PutItemAsync(BuildRoleMemberReverseItem(regularRoleId, userId, username));

            try
            {
                // Act: Query user's roles using begins_with on SK
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"USER#{userId}" },
                        [":skPrefix"] = new AttributeValue { S = "ROLE#" }
                    }
                });

                // Assert: Exactly 2 role items returned
                queryResponse.Items.Should().HaveCount(2);

                // Assert: Both role IDs are present in the results
                var roleIds = queryResponse.Items.Select(i => i["role_id"].S).ToList();
                roleIds.Should().Contain(adminRoleId);
                roleIds.Should().Contain(regularRoleId);
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", $"ROLE#{adminRoleId}");
                await SafeDeleteItemAsync($"USER#{userId}", $"ROLE#{regularRoleId}");
                await SafeDeleteItemAsync($"ROLE#{adminRoleId}", $"MEMBER#{userId}");
                await SafeDeleteItemAsync($"ROLE#{regularRoleId}", $"MEMBER#{userId}");
            }
        }

        /// <summary>
        /// Verifies querying a role's members using begins_with on the sort key.
        /// Query: PK=ROLE#{roleId}, SK begins_with "MEMBER#"
        /// Tests the reverse direction of the user-role relationship.
        /// Uses <see cref="Role.GuestRoleId"/> as the test role.
        /// </summary>
        [Fact]
        public async Task GetRoleMembers_QueryByRoleId_ReturnsUsers()
        {
            // Arrange: Create a unique custom role and assign 3 users to it
            var roleId = Guid.NewGuid().ToString();
            var userIds = new[]
            {
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString(),
                Guid.NewGuid().ToString()
            };

            // Create forward + reverse items for each user (6 items total)
            foreach (var uid in userIds)
            {
                var uname = $"member_{uid}";
                await PutItemAsync(BuildUserRoleForwardItem(uid, roleId, "custom_test_role"));
                await PutItemAsync(BuildRoleMemberReverseItem(roleId, uid, uname));
            }

            try
            {
                // Act: Query role's members using begins_with on SK
                var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
                {
                    TableName = _tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":pk"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        [":skPrefix"] = new AttributeValue { S = "MEMBER#" }
                    }
                });

                // Assert: Results are non-empty and contain exactly 3 member items
                queryResponse.Items.Any().Should().BeTrue();
                queryResponse.Items.Should().HaveCount(3);

                // Assert: All 3 user IDs are present
                var foundUserIds = queryResponse.Items.Select(i => i["user_id"].S).ToList();
                foreach (var uid in userIds)
                {
                    foundUserIds.Should().Contain(uid);
                }

                // Also verify the well-known GuestRoleId constant is accessible
                // (confirming the Role model constants compile and are valid GUIDs)
                Role.GuestRoleId.Should().NotBe(Guid.Empty);
            }
            finally
            {
                foreach (var uid in userIds)
                {
                    await SafeDeleteItemAsync($"USER#{uid}", $"ROLE#{roleId}");
                    await SafeDeleteItemAsync($"ROLE#{roleId}", $"MEMBER#{uid}");
                }
            }
        }

        /// <summary>
        /// Verifies that removing a user-role relationship deletes both directional items.
        /// After deletion, neither the forward link (USER→ROLE) nor the reverse link
        /// (ROLE→MEMBER) should exist. Queries in both directions should return 0 items.
        /// </summary>
        [Fact]
        public async Task RemoveUserRole_DeletesBothDirections()
        {
            // Arrange: Create a user-role relationship with both forward and reverse items
            var userId = Guid.NewGuid().ToString();
            var roleId = Guid.NewGuid().ToString();
            var username = $"remove_{userId}";

            await PutItemAsync(BuildUserRoleForwardItem(userId, roleId, "removable_role"));
            await PutItemAsync(BuildRoleMemberReverseItem(roleId, userId, username));

            // Act: Delete both directional items
            await DeleteItemAsync($"USER#{userId}", $"ROLE#{roleId}");
            await DeleteItemAsync($"ROLE#{roleId}", $"MEMBER#{userId}");

            // Assert: Forward link is gone — GetItem returns empty
            var forwardResponse = await GetItemAsync($"USER#{userId}", $"ROLE#{roleId}");
            forwardResponse.Item.Should().BeEmpty();

            // Assert: Reverse link is gone — GetItem returns empty
            var reverseResponse = await GetItemAsync($"ROLE#{roleId}", $"MEMBER#{userId}");
            reverseResponse.Item.Should().BeEmpty();

            // Assert: Query by user returns 0 roles
            var userRolesQuery = await _dynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"USER#{userId}" },
                    [":skPrefix"] = new AttributeValue { S = "ROLE#" }
                }
            });
            userRolesQuery.Items.Should().BeEmpty();

            // Assert: Query by role returns 0 members
            var roleMembersQuery = await _dynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"ROLE#{roleId}" },
                    [":skPrefix"] = new AttributeValue { S = "MEMBER#" }
                }
            });
            roleMembersQuery.Items.Should().BeEmpty();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Role CRUD Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies complete round-trip persistence of a role metadata item.
        /// PK=ROLE#{roleId}, SK=META, EntityType=ROLE_META with id, name, description attributes.
        /// Also verifies the well-known system role constants from <see cref="Role"/>
        /// (<see cref="Role.AdministratorRoleId"/>, <see cref="Role.RegularRoleId"/>,
        /// <see cref="Role.GuestRoleId"/>) match expected GUID values.
        /// </summary>
        [Fact]
        public async Task RoleMeta_PutItem_GetItem_RoundTrip()
        {
            // Arrange: Build a new custom role item (distinct from seeded system roles)
            var roleId = Guid.NewGuid().ToString();
            var roleName = $"testrole_{roleId}";
            var roleDescription = "Test role description for round-trip verification";
            var item = BuildRoleMetaItem(roleId, roleName, roleDescription);

            try
            {
                // Act: PutItem and then GetItem
                await PutItemAsync(item);
                var getResponse = await GetItemAsync($"ROLE#{roleId}", "META");

                // Assert: Item exists and all attributes match
                getResponse.Item.Should().NotBeEmpty();
                getResponse.Item["id"].S.Should().Be(roleId);
                getResponse.Item["name"].S.Should().Be(roleName);
                getResponse.Item["description"].S.Should().Be(roleDescription);
                getResponse.Item["EntityType"].S.Should().Be("ROLE_META");

                // Verify well-known system role constants from the Role model
                // (source: Definitions.cs SystemIds)
                Role.AdministratorRoleId.Should().Be(new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA"));
                Role.RegularRoleId.Should().Be(new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F"));
                Role.GuestRoleId.Should().Be(new Guid("987148B1-AFA8-4B33-8616-55861E5FD065"));
            }
            finally
            {
                await SafeDeleteItemAsync($"ROLE#{roleId}", "META");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Entity Type Filtering Tests (Scan with EntityType discriminator)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that scanning with EntityType=USER_PROFILE filter returns only user items
        /// and no role items. Tests the EntityType discriminator that replaces separate
        /// PostgreSQL tables (rec_user vs rec_role) in the single-table DynamoDB design.
        /// Also validates <see cref="User.SystemUserId"/> and <see cref="User.FirstUserId"/>
        /// are well-known constants from the source Definitions.cs.
        /// </summary>
        [Fact]
        public async Task ScanByEntityType_USER_PROFILE_ReturnsOnlyUsers()
        {
            // Arrange: Create 2 unique test users and 2 unique test roles
            var user1Id = Guid.NewGuid().ToString();
            var user2Id = Guid.NewGuid().ToString();
            var role1Id = Guid.NewGuid().ToString();
            var role2Id = Guid.NewGuid().ToString();

            await PutItemAsync(BuildUserProfileItem(user1Id,
                $"scanuser1_{user1Id}@test.com", $"scanuser1_{user1Id}"));
            await PutItemAsync(BuildUserProfileItem(user2Id,
                $"scanuser2_{user2Id}@test.com", $"scanuser2_{user2Id}"));
            await PutItemAsync(BuildRoleMetaItem(role1Id, $"scanrole1_{role1Id}", "Scan test role 1"));
            await PutItemAsync(BuildRoleMetaItem(role2Id, $"scanrole2_{role2Id}", "Scan test role 2"));

            try
            {
                // Act: Scan with EntityType filter for USER_PROFILE only
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :type",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":type"] = new AttributeValue { S = "USER_PROFILE" }
                    }
                });

                // Assert: All returned items are USER_PROFILE (no ROLE_META)
                scanResponse.Items.Should().NotBeEmpty();
                scanResponse.Items
                    .Select(i => i["EntityType"].S)
                    .Should().OnlyContain(et => et == "USER_PROFILE");

                // Assert: Our 2 test users are in the results
                var userIds = scanResponse.Items.Select(i => i["id"].S).ToList();
                userIds.Should().Contain(user1Id);
                userIds.Should().Contain(user2Id);

                // Verify User model well-known constants are accessible
                // (source: Definitions.cs lines 19-20)
                User.SystemUserId.Should().Be(new Guid("10000000-0000-0000-0000-000000000000"));
                User.FirstUserId.Should().Be(new Guid("EABD66FD-8DE1-4D79-9674-447EE89921C2"));
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{user1Id}", "PROFILE");
                await SafeDeleteItemAsync($"USER#{user2Id}", "PROFILE");
                await SafeDeleteItemAsync($"ROLE#{role1Id}", "META");
                await SafeDeleteItemAsync($"ROLE#{role2Id}", "META");
            }
        }

        /// <summary>
        /// Verifies that scanning with EntityType=ROLE_META filter returns only role items
        /// and no user items. The fixture seeds 3 system roles, so results include both
        /// seeded system roles and any test roles created during this test.
        /// </summary>
        [Fact]
        public async Task ScanByEntityType_ROLE_META_ReturnsOnlyRoles()
        {
            // Arrange: Create 2 unique test users and 2 unique test roles
            var user1Id = Guid.NewGuid().ToString();
            var user2Id = Guid.NewGuid().ToString();
            var role1Id = Guid.NewGuid().ToString();
            var role2Id = Guid.NewGuid().ToString();

            await PutItemAsync(BuildUserProfileItem(user1Id,
                $"scanu1_{user1Id}@test.com", $"scanu1_{user1Id}"));
            await PutItemAsync(BuildUserProfileItem(user2Id,
                $"scanu2_{user2Id}@test.com", $"scanu2_{user2Id}"));
            await PutItemAsync(BuildRoleMetaItem(role1Id, $"scanr1_{role1Id}", "Scan role test 1"));
            await PutItemAsync(BuildRoleMetaItem(role2Id, $"scanr2_{role2Id}", "Scan role test 2"));

            try
            {
                // Act: Scan with EntityType filter for ROLE_META only
                var scanResponse = await _dynamoDbClient.ScanAsync(new ScanRequest
                {
                    TableName = _tableName,
                    FilterExpression = "EntityType = :type",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":type"] = new AttributeValue { S = "ROLE_META" }
                    }
                });

                // Assert: All returned items are ROLE_META (no USER_PROFILE)
                scanResponse.Items.Should().NotBeEmpty();
                scanResponse.Items
                    .Select(i => i["EntityType"].S)
                    .Should().OnlyContain(et => et == "ROLE_META");

                // Assert: Our 2 test roles are in the results
                var roleIds = scanResponse.Items.Select(i => i["id"].S).ToList();
                roleIds.Should().Contain(role1Id);
                roleIds.Should().Contain(role2Id);

                // Assert: At least 5 roles total (3 system-seeded + 2 test)
                // System roles seeded by fixture: administrator, regular, guest
                scanResponse.Items.Count.Should().BeGreaterThanOrEqualTo(5);
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{user1Id}", "PROFILE");
                await SafeDeleteItemAsync($"USER#{user2Id}", "PROFILE");
                await SafeDeleteItemAsync($"ROLE#{role1Id}", "META");
                await SafeDeleteItemAsync($"ROLE#{role2Id}", "META");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Data Integrity Tests (Conditional Expressions)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that a conditional PutItem with <c>attribute_not_exists(PK)</c> prevents
        /// overwriting an existing item. This models idempotent create operations with
        /// optimistic concurrency — ensuring that duplicate user creation requests do not
        /// silently overwrite existing data.
        /// Per AAP Section 0.8.5: "Idempotency keys on all write endpoints."
        /// </summary>
        [Fact]
        public async Task ConditionalPut_PreventsOverwrite_WhenItemExists()
        {
            // Arrange: Create a user profile that we will attempt to overwrite
            var userId = Guid.NewGuid().ToString();
            var email = $"condput_{userId}@test.com";
            var username = $"condput_{userId}";
            var item = BuildUserProfileItem(userId, email, username);

            try
            {
                // First put succeeds (no existing item)
                await PutItemAsync(item);

                // Act: Attempt a second PutItem with condition that PK must not exist
                Func<Task> act = async () => await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = item,
                    ConditionExpression = "attribute_not_exists(PK)"
                });

                // Assert: ConditionalCheckFailedException is thrown — item already exists
                await act.Should().ThrowAsync<ConditionalCheckFailedException>();
            }
            finally
            {
                await SafeDeleteItemAsync($"USER#{userId}", "PROFILE");
            }
        }

        /// <summary>
        /// Verifies that an UpdateItem with <c>attribute_exists(PK)</c> condition fails
        /// when the target item does not exist. This prevents phantom updates to
        /// non-existent records and ensures data integrity for update operations.
        /// </summary>
        [Fact]
        public async Task UpdateItem_ConditionCheck_OnlyUpdatesExisting()
        {
            // Arrange: Use a non-existent key — no item is created for this test
            var nonExistentUserId = Guid.NewGuid().ToString();

            // Act: Attempt an UpdateItem with condition that PK must exist
            Func<Task> act = async () => await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{nonExistentUserId}" },
                    ["SK"] = new AttributeValue { S = "PROFILE" }
                },
                UpdateExpression = "SET first_name = :fn",
                ConditionExpression = "attribute_exists(PK)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":fn"] = new AttributeValue { S = "Ghost" }
                }
            });

            // Assert: ConditionalCheckFailedException is thrown — item does not exist
            await act.Should().ThrowAsync<ConditionalCheckFailedException>();
        }
    }
}
