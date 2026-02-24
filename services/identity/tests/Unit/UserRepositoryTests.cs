using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Identity.DataAccess;
using WebVellaErp.Identity.Models;
using Xunit;

namespace WebVellaErp.Identity.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="UserRepository"/> — the DynamoDB-backed data access layer
    /// that replaces the monolith's PostgreSQL-based DbRecordRepository and SecurityManager EQL queries.
    /// All DynamoDB interactions are mocked via Moq; zero real AWS calls are made.
    /// </summary>
    public class UserRepositoryTests
    {
        // ── Mocks and SUT ──────────────────────────────────────────────
        private readonly Mock<IAmazonDynamoDB> _mockDynamoDb;
        private readonly Mock<ILogger<UserRepository>> _mockLogger;
        private readonly UserRepository _sut;

        // ── DynamoDB key/index constants (must match UserRepository internals) ──
        private const string TableName = "test-identity-table";
        private const string PK = "PK";
        private const string SK = "SK";
        private const string GSI1PK = "GSI1PK";
        private const string GSI1SK = "GSI1SK";
        private const string GSI2PK = "GSI2PK";
        private const string GSI2SK = "GSI2SK";

        public UserRepositoryTests()
        {
            _mockDynamoDb = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
            _mockLogger = new Mock<ILogger<UserRepository>>();

            // UserRepository reads the table name from this env var (default: "identity")
            Environment.SetEnvironmentVariable("IDENTITY_TABLE_NAME", TableName);

            _sut = new UserRepository(_mockDynamoDb.Object, _mockLogger.Object);
        }

        // ═══════════════════════════════════════════════════════════════
        // Helper: create a DynamoDB item dictionary that represents a persisted User
        // ═══════════════════════════════════════════════════════════════
        private static Dictionary<string, AttributeValue> CreateDynamoDbUserItem(User user)
        {
            var item = new Dictionary<string, AttributeValue>
            {
                [PK] = new AttributeValue { S = $"USER#{user.Id}" },
                [SK] = new AttributeValue { S = "PROFILE" },
                [GSI1PK] = new AttributeValue { S = $"EMAIL#{user.Email.ToLowerInvariant()}" },
                [GSI1SK] = new AttributeValue { S = "USER" },
                [GSI2PK] = new AttributeValue { S = $"USERNAME#{user.Username}" },
                [GSI2SK] = new AttributeValue { S = "USER" },
                ["EntityType"] = new AttributeValue { S = "USER_PROFILE" },
                ["id"] = new AttributeValue { S = user.Id.ToString() },
                ["username"] = new AttributeValue { S = user.Username },
                ["email"] = new AttributeValue { S = user.Email },
                ["first_name"] = new AttributeValue { S = user.FirstName },
                ["last_name"] = new AttributeValue { S = user.LastName },
                ["image"] = new AttributeValue { S = user.Image ?? string.Empty },
                ["enabled"] = new AttributeValue { BOOL = user.Enabled },
                ["verified"] = new AttributeValue { BOOL = user.Verified },
                ["created_on"] = new AttributeValue { S = user.CreatedOn.ToString("o") },
                ["cognito_sub"] = new AttributeValue { S = user.CognitoSub ?? string.Empty },
                ["preferences"] = new AttributeValue { S = "{}" }
            };

            if (user.LastLoggedIn.HasValue)
            {
                item["last_logged_in"] = new AttributeValue { S = user.LastLoggedIn.Value.ToString("o") };
            }
            else
            {
                item["last_logged_in"] = new AttributeValue { NULL = true };
            }

            return item;
        }

        // ═══════════════════════════════════════════════════════════════
        // Helper: create a DynamoDB item dictionary that represents a persisted Role
        // ═══════════════════════════════════════════════════════════════
        private static Dictionary<string, AttributeValue> CreateDynamoDbRoleItem(Role role)
        {
            return new Dictionary<string, AttributeValue>
            {
                [PK] = new AttributeValue { S = $"ROLE#{role.Id}" },
                [SK] = new AttributeValue { S = "META" },
                ["EntityType"] = new AttributeValue { S = "ROLE_META" },
                ["id"] = new AttributeValue { S = role.Id.ToString() },
                ["name"] = new AttributeValue { S = role.Name },
                ["description"] = new AttributeValue { S = role.Description },
                ["cognito_group_name"] = new AttributeValue { S = role.CognitoGroupName ?? string.Empty }
            };
        }

        /// <summary>
        /// Helper: create a user-role link item as returned by GetUserRolesAsync query
        /// (PK=USER#{userId}, SK=ROLE#{roleId} with denormalized role fields).
        /// </summary>
        private static Dictionary<string, AttributeValue> CreateUserRoleLinkItem(
            Guid userId, Role role)
        {
            return new Dictionary<string, AttributeValue>
            {
                [PK] = new AttributeValue { S = $"USER#{userId}" },
                [SK] = new AttributeValue { S = $"ROLE#{role.Id}" },
                ["EntityType"] = new AttributeValue { S = "USER_ROLE_LINK" },
                ["role_id"] = new AttributeValue { S = role.Id.ToString() },
                ["role_name"] = new AttributeValue { S = role.Name },
                ["role_description"] = new AttributeValue { S = role.Description },
                ["cognito_group_name"] = new AttributeValue { S = role.CognitoGroupName ?? string.Empty }
            };
        }

        /// <summary>Helper: build a stock test user with predictable property values.</summary>
        private static User CreateTestUser(Guid? id = null)
        {
            var userId = id ?? Guid.NewGuid();
            return new User
            {
                Id = userId,
                Username = "testuser",
                Email = "test@example.com",
                FirstName = "Test",
                LastName = "User",
                Image = "https://example.com/avatar.png",
                Enabled = true,
                Verified = true,
                CreatedOn = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                LastLoggedIn = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc),
                CognitoSub = "cognito-sub-12345",
                Preferences = null,
                Roles = new List<Role>()
            };
        }

        /// <summary>Helper: build a stock test role.</summary>
        private static Role CreateTestRole(Guid? id = null, string name = "Administrator")
        {
            return new Role
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Description = $"{name} role",
                CognitoGroupName = name.ToLowerInvariant()
            };
        }

        // ═══════════════════════════════════════════════════════════════
        //  GetUserByIdAsync Tests
        //  Replaces SecurityManager.GetUser(Guid userId) EQL query
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetUserByIdAsync_ExistingUser_ReturnsUser()
        {
            // Arrange
            var user = CreateTestUser();
            var userItem = CreateDynamoDbUserItem(user);
            var adminRole = CreateTestRole(Role.AdministratorRoleId, "Administrator");
            var roleLinkItem = CreateUserRoleLinkItem(user.Id, adminRole);

            // Mock GetItemAsync for user profile lookup
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.TableName == TableName &&
                        r.Key[PK].S == $"USER#{user.Id}" &&
                        r.Key[SK].S == "PROFILE"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = userItem });

            // Mock QueryAsync for role hydration (SK begins_with ROLE#)
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.KeyConditionExpression.Contains("begins_with") &&
                        r.ExpressionAttributeValues.ContainsKey(":pk") &&
                        r.ExpressionAttributeValues[":pk"].S == $"USER#{user.Id}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { roleLinkItem }
                });

            // Act
            var result = await _sut.GetUserByIdAsync(user.Id);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(user.Id);
            result.Email.Should().Be(user.Email);
            result.Username.Should().Be(user.Username);
            result.FirstName.Should().Be(user.FirstName);
            result.LastName.Should().Be(user.LastName);
            result.Roles.Should().HaveCount(1);
            result.Roles[0].Id.Should().Be(adminRole.Id);
        }

        [Fact]
        public async Task GetUserByIdAsync_NonExistentUser_ReturnsNull()
        {
            // Arrange — GetItemAsync returns empty/null item
            var userId = Guid.NewGuid();

            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.TableName == TableName &&
                        r.Key[PK].S == $"USER#{userId}" &&
                        r.Key[SK].S == "PROFILE"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>() // empty item
                });

            // Act
            var result = await _sut.GetUserByIdAsync(userId);

            // Assert — matches source SecurityManager.cs: if (result.Count != 1) return null
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetUserByIdAsync_CorrectKeyPattern()
        {
            // Arrange
            var userId = Guid.NewGuid();
            GetItemRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.IsAny<GetItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<GetItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new GetItemResponse
                {
                    Item = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.GetUserByIdAsync(userId);

            // Assert — verify exact DynamoDB single-table key pattern
            capturedRequest.Should().NotBeNull();
            capturedRequest!.TableName.Should().Be(TableName);
            capturedRequest.Key.Should().ContainKey(PK);
            capturedRequest.Key[PK].S.Should().Be($"USER#{userId}");
            capturedRequest.Key.Should().ContainKey(SK);
            capturedRequest.Key[SK].S.Should().Be("PROFILE");
        }

        // ═══════════════════════════════════════════════════════════════
        //  GetUserByEmailAsync Tests
        //  Replaces SecurityManager.GetUser(string email) EQL query
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetUserByEmailAsync_ExistingUser_ReturnsUser()
        {
            // Arrange
            var user = CreateTestUser();
            var userItem = CreateDynamoDbUserItem(user);
            var role = CreateTestRole();
            var roleLinkItem = CreateUserRoleLinkItem(user.Id, role);

            // Mock GSI1 query returning user item
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.IndexName == "GSI1" &&
                        r.ExpressionAttributeValues.Any(kv =>
                            kv.Value.S == $"EMAIL#{user.Email.ToLowerInvariant()}")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { userItem }
                });

            // Mock role hydration query (SK begins_with ROLE#)
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        !string.IsNullOrEmpty(r.KeyConditionExpression) &&
                        r.KeyConditionExpression.Contains("begins_with") &&
                        r.ExpressionAttributeValues.ContainsKey(":pk") &&
                        r.ExpressionAttributeValues[":pk"].S == $"USER#{user.Id}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { roleLinkItem }
                });

            // Act
            var result = await _sut.GetUserByEmailAsync(user.Email);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(user.Id);
            result.Email.Should().Be(user.Email);
            result.Roles.Should().HaveCount(1);
        }

        [Fact]
        public async Task GetUserByEmailAsync_NonExistentUser_ReturnsNull()
        {
            // Arrange — GSI1 query returns no items
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.IndexName == "GSI1"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            var result = await _sut.GetUserByEmailAsync("nonexistent@example.com");

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetUserByEmailAsync_UsesGsi1Index()
        {
            // Arrange
            var email = "lookup@example.com";
            QueryRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.IndexName == "GSI1"),
                    It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            await _sut.GetUserByEmailAsync(email);

            // Assert — verify GSI1 index query with correct key
            capturedRequest.Should().NotBeNull();
            capturedRequest!.IndexName.Should().Be("GSI1");
            capturedRequest.TableName.Should().Be(TableName);

            // The expression attribute values should contain the email-prefixed key
            var emailKeyValue = capturedRequest.ExpressionAttributeValues
                .Values.FirstOrDefault(v => v.S != null && v.S.Contains("EMAIL#"));
            emailKeyValue.Should().NotBeNull();
            emailKeyValue!.S.Should().Be($"EMAIL#{email.ToLowerInvariant()}");
        }

        // ═══════════════════════════════════════════════════════════════
        //  GetUserByUsernameAsync Tests
        //  Replaces SecurityManager.GetUserByUsername(string username) EQL query
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetUserByUsernameAsync_ExistingUser_ReturnsUser()
        {
            // Arrange
            var user = CreateTestUser();
            var userItem = CreateDynamoDbUserItem(user);
            var role = CreateTestRole();
            var roleLinkItem = CreateUserRoleLinkItem(user.Id, role);

            // Mock GSI2 query returning user item
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.IndexName == "GSI2" &&
                        r.ExpressionAttributeValues.Any(kv =>
                            kv.Value.S == $"USERNAME#{user.Username}")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { userItem }
                });

            // Mock role hydration query
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        !string.IsNullOrEmpty(r.KeyConditionExpression) &&
                        r.KeyConditionExpression.Contains("begins_with") &&
                        r.ExpressionAttributeValues.ContainsKey(":pk") &&
                        r.ExpressionAttributeValues[":pk"].S == $"USER#{user.Id}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { roleLinkItem }
                });

            // Act
            var result = await _sut.GetUserByUsernameAsync(user.Username);

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be(user.Id);
            result.Username.Should().Be(user.Username);
            result.Roles.Should().HaveCount(1);
        }

        [Fact]
        public async Task GetUserByUsernameAsync_NonExistentUser_ReturnsNull()
        {
            // Arrange — GSI2 query returns no items
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.IndexName == "GSI2"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            var result = await _sut.GetUserByUsernameAsync("ghost_user");

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task GetUserByUsernameAsync_UsesGsi2Index()
        {
            // Arrange
            var username = "lookupuser";
            QueryRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.IndexName == "GSI2"),
                    It.IsAny<CancellationToken>()))
                .Callback<QueryRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            await _sut.GetUserByUsernameAsync(username);

            // Assert — verify GSI2 index query
            capturedRequest.Should().NotBeNull();
            capturedRequest!.IndexName.Should().Be("GSI2");
            capturedRequest.TableName.Should().Be(TableName);

            var usernameKeyValue = capturedRequest.ExpressionAttributeValues
                .Values.FirstOrDefault(v => v.S != null && v.S.Contains("USERNAME#"));
            usernameKeyValue.Should().NotBeNull();
            usernameKeyValue!.S.Should().Be($"USERNAME#{username}");
        }

        // ═══════════════════════════════════════════════════════════════
        //  SaveUserAsync Tests
        //  Replaces RecordManager.CreateRecord / UpdateRecord
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task SaveUserAsync_PutsItemWithCorrectKeyPattern()
        {
            // Arrange
            var user = CreateTestUser();
            user.Roles = new List<Role>(); // no roles — simplifies this test
            PutItemRequest? capturedPut = null;

            _mockDynamoDb
                .Setup(x => x.PutItemAsync(
                    It.Is<PutItemRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Mock query for current roles (differential sync reads existing roles)
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.KeyConditionExpression.Contains("begins_with")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            await _sut.SaveUserAsync(user);

            // Assert — verify PutItem was called with correct DynamoDB key pattern
            capturedPut.Should().NotBeNull();
            var item = capturedPut!.Item;
            item[PK].S.Should().Be($"USER#{user.Id}");
            item[SK].S.Should().Be("PROFILE");
            item[GSI1PK].S.Should().Be($"EMAIL#{user.Email.ToLowerInvariant()}");
            item[GSI1SK].S.Should().Be("USER");
            item[GSI2PK].S.Should().Be($"USERNAME#{user.Username}");
            item[GSI2SK].S.Should().Be("USER");
            item["EntityType"].S.Should().Be("USER_PROFILE");

            // Verify all user attributes are mapped
            item["id"].S.Should().Be(user.Id.ToString());
            item["username"].S.Should().Be(user.Username);
            item["email"].S.Should().Be(user.Email);
            item["first_name"].S.Should().Be(user.FirstName);
            item["last_name"].S.Should().Be(user.LastName);
            item["enabled"].BOOL.Should().Be(user.Enabled);
            item["verified"].BOOL.Should().Be(user.Verified);
            item["cognito_sub"].S.Should().Be(user.CognitoSub);
        }

        [Fact]
        public async Task SaveUserAsync_SerializesPreferencesAsJson()
        {
            // Arrange
            var user = CreateTestUser();
            user.Preferences = new UserPreferences
            {
                SidebarSize = "lg"
            };
            user.Roles = new List<Role>();
            PutItemRequest? capturedPut = null;

            _mockDynamoDb
                .Setup(x => x.PutItemAsync(
                    It.Is<PutItemRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Mock differential role sync query
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.KeyConditionExpression.Contains("begins_with")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            await _sut.SaveUserAsync(user);

            // Assert — preferences must be serialized as JSON string
            // The UserRepository uses System.Text.Json with snake_case naming policy,
            // so UserPreferences.SidebarSize serializes as "sidebar_size"
            capturedPut.Should().NotBeNull();
            var preferencesAttr = capturedPut!.Item["preferences"];
            preferencesAttr.S.Should().NotBeNullOrEmpty();
            preferencesAttr.S.Should().Contain("sidebar_size");
        }

        // ═══════════════════════════════════════════════════════════════
        //  GetAllRolesAsync Tests
        //  Replaces SecurityManager.GetAllRoles() EQL query
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetAllRolesAsync_ReturnsAllRoles()
        {
            // Arrange
            var adminRole = CreateTestRole(Role.AdministratorRoleId, "Administrator");
            var regularRole = CreateTestRole(Role.RegularRoleId, "Regular");
            var guestRole = CreateTestRole(Role.GuestRoleId, "Guest");

            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.Is<ScanRequest>(r =>
                        r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateDynamoDbRoleItem(adminRole),
                        CreateDynamoDbRoleItem(regularRole),
                        CreateDynamoDbRoleItem(guestRole)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>() // no more pages
                });

            // Act
            var roles = await _sut.GetAllRolesAsync();

            // Assert
            roles.Should().NotBeNull();
            roles.Should().HaveCount(3);
            roles.Select(r => r.Name).Should().Contain("Administrator");
            roles.Select(r => r.Name).Should().Contain("Regular");
            roles.Select(r => r.Name).Should().Contain("Guest");
        }

        [Fact]
        public async Task GetAllRolesAsync_UsesEntityTypeFilter()
        {
            // Arrange
            ScanRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.Is<ScanRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .Callback<ScanRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>(),
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            await _sut.GetAllRolesAsync();

            // Assert — verify filter expression checks EntityType = ROLE_META
            capturedRequest.Should().NotBeNull();
            capturedRequest!.FilterExpression.Should().Contain("EntityType");

            var entityTypeValue = capturedRequest.ExpressionAttributeValues
                .Values.FirstOrDefault(v => v.S == "ROLE_META");
            entityTypeValue.Should().NotBeNull();
        }

        // ═══════════════════════════════════════════════════════════════
        //  SaveRoleAsync Tests
        //  Replaces SecurityManager.SaveRole persistence
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task SaveRoleAsync_PutsItemWithCorrectKeyPattern()
        {
            // Arrange
            var role = CreateTestRole();
            PutItemRequest? capturedPut = null;

            _mockDynamoDb
                .Setup(x => x.PutItemAsync(
                    It.Is<PutItemRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .Callback<PutItemRequest, CancellationToken>((req, _) => capturedPut = req)
                .ReturnsAsync(new PutItemResponse());

            // Act
            await _sut.SaveRoleAsync(role);

            // Assert — verify correct key pattern: PK=ROLE#{roleId}, SK=META
            capturedPut.Should().NotBeNull();
            var item = capturedPut!.Item;
            item[PK].S.Should().Be($"ROLE#{role.Id}");
            item[SK].S.Should().Be("META");
            item["EntityType"].S.Should().Be("ROLE_META");

            // Verify role attributes
            item["id"].S.Should().Be(role.Id.ToString());
            item["name"].S.Should().Be(role.Name);
            item["description"].S.Should().Be(role.Description);
            item["cognito_group_name"].S.Should().Be(role.CognitoGroupName);
        }

        // ═══════════════════════════════════════════════════════════════
        //  AssignRolesToUserAsync Tests
        //  Replaces PostgreSQL rel_user_role join table writes
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task AssignRolesToUserAsync_CreatesBidirectionalRelationshipItems()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var role = CreateTestRole(Guid.NewGuid(), "TestRole");
            var roleIds = new List<Guid> { role.Id };

            // Mock GetRoleByIdAsync (fetches role metadata for denormalization)
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK].S == $"ROLE#{role.Id}" &&
                        r.Key[SK].S == "META"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateDynamoDbRoleItem(role)
                });

            // Capture BatchWriteItem calls to verify bidirectional links
            var capturedBatchRequests = new List<BatchWriteItemRequest>();

            _mockDynamoDb
                .Setup(x => x.BatchWriteItemAsync(
                    It.IsAny<BatchWriteItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<BatchWriteItemRequest, CancellationToken>((req, _) =>
                    capturedBatchRequests.Add(req))
                .ReturnsAsync(new BatchWriteItemResponse
                {
                    UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
                });

            // Act
            await _sut.AssignRolesToUserAsync(userId, roleIds);

            // Assert — verify TWO items written per role assignment:
            //   1. Forward: PK=USER#{userId}, SK=ROLE#{roleId}
            //   2. Reverse: PK=ROLE#{roleId}, SK=MEMBER#{userId}
            capturedBatchRequests.Should().NotBeEmpty();

            var allWriteRequests = capturedBatchRequests
                .SelectMany(br => br.RequestItems.Values.SelectMany(v => v))
                .Where(wr => wr.PutRequest != null)
                .Select(wr => wr.PutRequest.Item)
                .ToList();

            // Forward link (user → role)
            var forwardLink = allWriteRequests.FirstOrDefault(item =>
                item.ContainsKey(PK) && item[PK].S == $"USER#{userId}" &&
                item.ContainsKey(SK) && item[SK].S == $"ROLE#{role.Id}");
            forwardLink.Should().NotBeNull("a forward USER→ROLE link item should be written");
            forwardLink!["EntityType"].S.Should().Be("USER_ROLE_LINK");

            // Reverse link (role → member)
            var reverseLink = allWriteRequests.FirstOrDefault(item =>
                item.ContainsKey(PK) && item[PK].S == $"ROLE#{role.Id}" &&
                item.ContainsKey(SK) && item[SK].S == $"MEMBER#{userId}");
            reverseLink.Should().NotBeNull("a reverse ROLE→MEMBER link item should be written");
            reverseLink!["EntityType"].S.Should().Be("ROLE_MEMBER_LINK");
        }

        [Fact]
        public async Task AssignRolesToUserAsync_RemovesOldRoles_AddsNewRoles()
        {
            // Arrange — user currently has roleA; we assign roleB (removes A, adds B)
            var userId = Guid.NewGuid();
            var roleA = CreateTestRole(Guid.NewGuid(), "OldRole");
            var roleB = CreateTestRole(Guid.NewGuid(), "NewRole");
            var user = CreateTestUser(userId);
            user.Roles = new List<Role> { roleB }; // desired end state

            // 1. Mock PutItemAsync for user profile
            _mockDynamoDb
                .Setup(x => x.PutItemAsync(
                    It.Is<PutItemRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PutItemResponse());

            // 2. Mock QueryAsync for current roles → returns roleA link
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.TableName == TableName &&
                        r.KeyConditionExpression.Contains("begins_with") &&
                        r.ExpressionAttributeValues.ContainsKey(":pk") &&
                        r.ExpressionAttributeValues[":pk"].S == $"USER#{userId}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateUserRoleLinkItem(userId, roleA)
                    }
                });

            // 3. Mock GetItemAsync for roleB metadata (for denormalization during add)
            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK].S == $"ROLE#{roleB.Id}" &&
                        r.Key[SK].S == "META"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse
                {
                    Item = CreateDynamoDbRoleItem(roleB)
                });

            // 4. Mock BatchWriteItemAsync for both add and remove operations
            var capturedBatchRequests = new List<BatchWriteItemRequest>();
            _mockDynamoDb
                .Setup(x => x.BatchWriteItemAsync(
                    It.IsAny<BatchWriteItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<BatchWriteItemRequest, CancellationToken>((req, _) =>
                    capturedBatchRequests.Add(req))
                .ReturnsAsync(new BatchWriteItemResponse
                {
                    UnprocessedItems = new Dictionary<string, List<WriteRequest>>()
                });

            // Act
            await _sut.SaveUserAsync(user);

            // Assert — should have both add (roleB) and remove (roleA) operations
            capturedBatchRequests.Should().NotBeEmpty();

            var allWriteRequests = capturedBatchRequests
                .SelectMany(br => br.RequestItems.Values.SelectMany(v => v))
                .ToList();

            // Verify at least one Put for roleB
            var roleBPuts = allWriteRequests
                .Where(wr => wr.PutRequest?.Item != null &&
                             wr.PutRequest.Item.ContainsKey(SK) &&
                             wr.PutRequest.Item[SK].S.Contains(roleB.Id.ToString()))
                .ToList();
            roleBPuts.Should().NotBeEmpty("new roleB should be added via PutRequest");

            // Verify at least one Delete for roleA
            var roleADeletes = allWriteRequests
                .Where(wr => wr.DeleteRequest?.Key != null &&
                             wr.DeleteRequest.Key.Values.Any(v =>
                                 v.S != null && v.S.Contains(roleA.Id.ToString())))
                .ToList();
            roleADeletes.Should().NotBeEmpty("old roleA should be removed via DeleteRequest");
        }

        // ═══════════════════════════════════════════════════════════════
        //  UpdateLastLoginTimeAsync Tests
        //  Replaces SecurityManager.UpdateUserLastLoginTime(Guid userId)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateLastLoginTimeAsync_UpdatesCorrectItem()
        {
            // Arrange
            var userId = Guid.NewGuid();
            var beforeCall = DateTime.UtcNow;
            UpdateItemRequest? capturedRequest = null;

            _mockDynamoDb
                .Setup(x => x.UpdateItemAsync(
                    It.Is<UpdateItemRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new UpdateItemResponse());

            // Act
            await _sut.UpdateLastLoginTimeAsync(userId);
            var afterCall = DateTime.UtcNow;

            // Assert — verify UpdateItem targets correct key and expression
            capturedRequest.Should().NotBeNull();
            capturedRequest!.Key[PK].S.Should().Be($"USER#{userId}");
            capturedRequest.Key[SK].S.Should().Be("PROFILE");
            capturedRequest.UpdateExpression.Should().Contain("last_logged_in");

            // Verify the timestamp attribute value is close to UtcNow
            var tsValue = capturedRequest.ExpressionAttributeValues
                .Values.FirstOrDefault(v => v.S != null && v.S.Contains("20"));
            tsValue.Should().NotBeNull("a timestamp value should be provided");

            var parsedTimestamp = DateTime.Parse(tsValue!.S);
            parsedTimestamp.Should().BeOnOrAfter(beforeCall.AddSeconds(-2));
            parsedTimestamp.Should().BeOnOrBefore(afterCall.AddSeconds(2));
        }

        // ═══════════════════════════════════════════════════════════════
        //  MapToUser / MapToRole Tests (indirect via public API)
        //  Tests the internal DynamoDB-item-to-model attribute mapping
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task MapToUser_CorrectlyMapsAllAttributes()
        {
            // Arrange — build a user with all properties set
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "mapper_test",
                Email = "MAPPER@Example.COM",
                FirstName = "Map",
                LastName = "Test",
                Image = "https://cdn.example.com/img.jpg",
                Enabled = false,
                Verified = false,
                CreatedOn = new DateTime(2023, 3, 15, 14, 30, 0, DateTimeKind.Utc),
                LastLoggedIn = new DateTime(2024, 12, 25, 0, 0, 0, DateTimeKind.Utc),
                CognitoSub = "sub-abc-123",
                Preferences = null,
                Roles = new List<Role>()
            };

            var userItem = CreateDynamoDbUserItem(user);

            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.Is<GetItemRequest>(r =>
                        r.Key[PK].S == $"USER#{user.Id}"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetItemResponse { Item = userItem });

            // Mock empty role hydration
            _mockDynamoDb
                .Setup(x => x.QueryAsync(
                    It.Is<QueryRequest>(r =>
                        r.KeyConditionExpression.Contains("begins_with")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>()
                });

            // Act
            var result = await _sut.GetUserByIdAsync(user.Id);

            // Assert — every single attribute mapped correctly
            result.Should().NotBeNull();
            result!.Id.Should().Be(user.Id);
            result.Username.Should().Be(user.Username);
            result.Email.Should().Be(user.Email);
            result.FirstName.Should().Be(user.FirstName);
            result.LastName.Should().Be(user.LastName);
            result.Image.Should().Be(user.Image);
            result.Enabled.Should().Be(user.Enabled);
            result.Verified.Should().Be(user.Verified);
            result.CognitoSub.Should().Be(user.CognitoSub);
            result.Roles.Should().NotBeNull();
            result.Roles.Should().BeEmpty();

            // Date verification (round-trip through ISO 8601 "o" format)
            result.CreatedOn.Should().BeCloseTo(user.CreatedOn, TimeSpan.FromSeconds(1));
            result.LastLoggedIn.Should().NotBeNull();
            result.LastLoggedIn!.Value.Should().BeCloseTo(user.LastLoggedIn.Value, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task MapToRole_CorrectlyMapsAllAttributes()
        {
            // Arrange — use GetAllRolesAsync which maps role items via MapToRole
            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = "CustomRole",
                Description = "A custom role for testing attribute mapping",
                CognitoGroupName = "custom-role-group"
            };

            _mockDynamoDb
                .Setup(x => x.ScanAsync(
                    It.Is<ScanRequest>(r => r.TableName == TableName),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ScanResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>>
                    {
                        CreateDynamoDbRoleItem(role)
                    },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue>()
                });

            // Act
            var roles = await _sut.GetAllRolesAsync();

            // Assert — verify all Role attributes are correctly mapped
            roles.Should().HaveCount(1);
            var mappedRole = roles.First();
            mappedRole.Id.Should().Be(role.Id);
            mappedRole.Name.Should().Be(role.Name);
            mappedRole.Description.Should().Be(role.Description);
            mappedRole.CognitoGroupName.Should().Be(role.CognitoGroupName);
        }

        // ═══════════════════════════════════════════════════════════════
        //  Error Handling Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task GetUserByIdAsync_DynamoDbException_PropagatesError()
        {
            // Arrange — DynamoDB throws an exception
            var userId = Guid.NewGuid();

            _mockDynamoDb
                .Setup(x => x.GetItemAsync(
                    It.IsAny<GetItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonDynamoDBException("Service unavailable"));

            // Act & Assert — exception must propagate (after logging)
            Func<Task> act = () => _sut.GetUserByIdAsync(userId);
            await act.Should().ThrowAsync<AmazonDynamoDBException>()
                .WithMessage("*Service unavailable*");
        }
    }
}
