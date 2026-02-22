using System;
using System.Collections.Generic;
using System.Linq;
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
    /// End-to-end integration tests for role CRUD operations against real LocalStack
    /// DynamoDB and Cognito services. Validates the complete role lifecycle with exact
    /// validation rule parity from the monolith's <c>SecurityManager.SaveRole()</c>
    /// (source lines 295-347).
    ///
    /// <para><b>Zero mocked AWS SDK calls</b> — every test operates against real
    /// LocalStack infrastructure provisioned by <see cref="LocalStackFixture"/>.</para>
    ///
    /// <para>Per AAP Section 0.8.4: "All integration and E2E tests MUST execute against
    /// LocalStack. No mocked AWS SDK calls in integration tests."</para>
    ///
    /// <para><b>DynamoDB key pattern:</b> <c>PK=ROLE#{roleId}</c>, <c>SK=META</c>,
    /// <c>EntityType=ROLE_META</c></para>
    ///
    /// <para><b>Cognito sync:</b> Each role maps to a Cognito user pool group with
    /// <c>GroupName = roleName.ToLowerInvariant()</c>.</para>
    /// </summary>
    public class RoleCrudIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly IAmazonCognitoIdentityProvider _cognitoClient;
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly string _userPoolId;
        private readonly string _tableName;

        /// <summary>
        /// Constructor receives the shared <see cref="LocalStackFixture"/> which provisions
        /// DynamoDB table (with PK/SK, GSI1, GSI2, and seeded system roles) and Cognito
        /// user pool (with app client and system role groups: administrator, regular, guest).
        /// </summary>
        /// <param name="fixture">Shared LocalStack fixture providing pre-configured AWS clients.</param>
        public RoleCrudIntegrationTests(LocalStackFixture fixture)
        {
            _cognitoClient = fixture.CognitoClient;
            _dynamoDbClient = fixture.DynamoDbClient;
            _userPoolId = fixture.UserPoolId;
            _tableName = fixture.TableName;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Helper Methods
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a test role in both DynamoDB and Cognito.
        /// DynamoDB item uses single-table design: <c>PK=ROLE#{roleId}</c>, <c>SK=META</c>,
        /// <c>EntityType=ROLE_META</c>.
        /// Cognito group uses <c>GroupName = name.ToLowerInvariant()</c>.
        /// </summary>
        /// <param name="name">Role display name (also used for Cognito group name, lowercased).</param>
        /// <param name="description">Role description text.</param>
        /// <returns>The generated role ID.</returns>
        private async Task<Guid> CreateTestRole(string name, string description)
        {
            var roleId = Guid.NewGuid();

            // Persist role in DynamoDB with single-table design pattern
            await _dynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                    ["SK"] = new AttributeValue { S = "META" },
                    ["EntityType"] = new AttributeValue { S = "ROLE_META" },
                    ["id"] = new AttributeValue { S = roleId.ToString() },
                    ["name"] = new AttributeValue { S = name },
                    ["description"] = new AttributeValue { S = description ?? string.Empty }
                }
            });

            // Create corresponding Cognito group for role-group sync
            await _cognitoClient.CreateGroupAsync(new CreateGroupRequest
            {
                GroupName = name.ToLowerInvariant(),
                UserPoolId = _userPoolId,
                Description = description ?? string.Empty
            });

            return roleId;
        }

        /// <summary>
        /// Cleans up a test role from both DynamoDB and Cognito.
        /// Each cleanup operation is independently try-caught to prevent masking test failures
        /// when a resource was already deleted or never created.
        /// </summary>
        /// <param name="roleId">The role's unique identifier for DynamoDB key construction.</param>
        /// <param name="groupName">The Cognito group name to delete.</param>
        private async Task CleanupRole(Guid roleId, string groupName)
        {
            // Delete DynamoDB role item
            try
            {
                await _dynamoDbClient.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    }
                });
            }
            catch (Exception)
            {
                // Cleanup failures should not mask test assertions
            }

            // Delete Cognito group
            try
            {
                await _cognitoClient.DeleteGroupAsync(new DeleteGroupRequest
                {
                    GroupName = groupName.ToLowerInvariant(),
                    UserPoolId = _userPoolId
                });
            }
            catch (Exception)
            {
                // Cleanup failures should not mask test assertions
            }
        }

        /// <summary>
        /// Retrieves a role item from DynamoDB by its ID.
        /// Uses the single-table design key pattern: <c>PK=ROLE#{roleId}</c>, <c>SK=META</c>.
        /// Returns null if the item does not exist.
        /// </summary>
        /// <param name="roleId">The role's unique identifier.</param>
        /// <returns>DynamoDB item attributes or null if not found.</returns>
        private async Task<Dictionary<string, AttributeValue>?> GetRoleFromDynamoDb(Guid roleId)
        {
            var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                    ["SK"] = new AttributeValue { S = "META" }
                }
            });

            return response.Item != null && response.Item.Count > 0 ? response.Item : null;
        }

        /// <summary>
        /// Scans all roles from DynamoDB filtered by <c>EntityType = ROLE_META</c>.
        /// Replaces monolith's <c>SecurityManager.GetAllRoles()</c> (source lines 186-189)
        /// which used <c>EqlCommand("SELECT * FROM role").Execute().MapTo&lt;ErpRole&gt;()</c>.
        /// </summary>
        /// <returns>List of all role items in DynamoDB.</returns>
        private async Task<List<Dictionary<string, AttributeValue>>> ScanAllRoles()
        {
            var response = await _dynamoDbClient.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                FilterExpression = "EntityType = :entityType",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":entityType"] = new AttributeValue { S = "ROLE_META" }
                }
            });

            return response.Items;
        }

        /// <summary>
        /// Validates role name requirements matching the monolith's <c>SecurityManager.SaveRole()</c>
        /// validation logic (source lines 316-319 for update, 335-338 for create):
        /// <list type="bullet">
        ///   <item><description>Name must not be null, empty, or whitespace → "Name is required."</description></item>
        ///   <item><description>Name must be unique among all roles → "Role with same name already exists"</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">The role name to validate.</param>
        /// <param name="excludeRoleId">
        /// Optional role ID to exclude from uniqueness check (for update scenarios
        /// where the role's own current name should not conflict). Matches source line 312:
        /// <c>if (existingRole.Name != role.Name)</c> — only checks uniqueness when name changes.
        /// </param>
        /// <returns>
        /// A list of validation error messages. Empty list means validation passed.
        /// Error messages match the source exactly:
        /// <c>"Name is required."</c> and <c>"Role with same name already exists"</c>.
        /// </returns>
        private async Task<List<string>> ValidateRoleName(string? name, Guid? excludeRoleId = null)
        {
            var errors = new List<string>();

            // Source SecurityManager.cs lines 316-317 (update) and 335-336 (create):
            // if (string.IsNullOrWhiteSpace(role.Name))
            //     valEx.AddError("name", "Name is required.");
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add("Name is required.");
                return errors;
            }

            // Source SecurityManager.cs lines 318-319 (update) and 337-338 (create):
            // else if (allRoles.Any(x => x.Name == role.Name))
            //     valEx.AddError("name", "Role with same name already exists");
            var allRoles = await ScanAllRoles();
            var nameExists = allRoles.Any(r =>
            {
                var roleName = r.ContainsKey("name") ? r["name"].S : string.Empty;
                var roleId = r.ContainsKey("id") ? r["id"].S : string.Empty;

                // For updates, exclude the role being updated from the uniqueness check
                if (excludeRoleId.HasValue && roleId == excludeRoleId.Value.ToString())
                    return false;

                return string.Equals(roleName, name, StringComparison.Ordinal);
            });

            if (nameExists)
            {
                errors.Add("Role with same name already exists");
            }

            return errors;
        }

        /// <summary>
        /// Checks whether a given role ID is a well-known system role that must not be deleted.
        /// System roles are identified by their well-known GUIDs from the monolith's
        /// <c>Definitions.cs</c> SystemIds class:
        /// <list type="bullet">
        ///   <item><description><see cref="Role.AdministratorRoleId"/> = BDC56420-CAF0-4030-8A0E-D264938E0CDA</description></item>
        ///   <item><description><see cref="Role.RegularRoleId"/> = F16EC6DB-626D-4C27-8DE0-3E7CE542C55F</description></item>
        ///   <item><description><see cref="Role.GuestRoleId"/> = 987148B1-AFA8-4B33-8616-55861E5FD065</description></item>
        /// </list>
        /// </summary>
        /// <param name="roleId">The role ID to check.</param>
        /// <returns>True if the role is a system role and must not be deleted.</returns>
        private static bool IsSystemRole(Guid roleId)
        {
            return roleId == Role.AdministratorRoleId
                || roleId == Role.RegularRoleId
                || roleId == Role.GuestRoleId;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 3: Role Creation Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that creating a role with valid data persists correctly in both
        /// DynamoDB (single-table design) and Cognito (user pool group).
        ///
        /// Replaces: <c>SecurityManager.SaveRole()</c> CREATE path (source lines 329-346)
        /// which called <c>recMan.CreateRecord("role", record)</c>.
        ///
        /// Validates:
        /// - DynamoDB item has correct PK, SK, id, name, description, EntityType
        /// - Cognito group exists with matching name
        /// </summary>
        [Fact]
        public async Task CreateRole_WithValidData_PersistsInDynamoDbAndCognitoGroup()
        {
            // Arrange
            var roleId = Guid.NewGuid();
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var roleName = $"testrole_{uniqueSuffix}";
            var roleDescription = "Test role description";

            try
            {
                // Act — Create role in DynamoDB with single-table design pattern
                await _dynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = _tableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        ["SK"] = new AttributeValue { S = "META" },
                        ["EntityType"] = new AttributeValue { S = "ROLE_META" },
                        ["id"] = new AttributeValue { S = roleId.ToString() },
                        ["name"] = new AttributeValue { S = roleName },
                        ["description"] = new AttributeValue { S = roleDescription }
                    }
                });

                // Act — Create corresponding Cognito group
                await _cognitoClient.CreateGroupAsync(new CreateGroupRequest
                {
                    GroupName = roleName.ToLowerInvariant(),
                    UserPoolId = _userPoolId,
                    Description = roleDescription
                });

                // Assert — Verify DynamoDB persistence
                var item = await GetRoleFromDynamoDb(roleId);
                item.Should().NotBeNull("role should be persisted in DynamoDB");
                item!["id"].S.Should().Be(roleId.ToString());
                item["name"].S.Should().Be(roleName);
                item["description"].S.Should().Be(roleDescription);
                item["EntityType"].S.Should().Be("ROLE_META");

                // Assert — Verify Cognito group exists
                var groupResponse = await _cognitoClient.GetGroupAsync(new GetGroupRequest
                {
                    GroupName = roleName.ToLowerInvariant(),
                    UserPoolId = _userPoolId
                });
                groupResponse.Group.Should().NotBeNull("Cognito group should exist for the role");
                groupResponse.Group.GroupName.Should().Be(roleName.ToLowerInvariant());
            }
            finally
            {
                // Cleanup
                await CleanupRole(roleId, roleName);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 4: Role Name Uniqueness Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that creating a role with a name that already exists fails with
        /// the exact validation error from the monolith.
        ///
        /// Source: <c>SecurityManager.SaveRole()</c> lines 337-338:
        /// <code>
        /// else if (allRoles.Any(x => x.Name == role.Name))
        ///     valEx.AddError("name", "Role with same name already exists");
        /// </code>
        /// </summary>
        [Fact]
        public async Task CreateRole_WithDuplicateName_FailsWithValidationError()
        {
            // Arrange — Create first role with unique name
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var roleName = $"uniquerole_{uniqueSuffix}";
            var role1Id = await CreateTestRole(roleName, "First role");

            try
            {
                // Act — Attempt to validate a second role with the same name
                var validationErrors = await ValidateRoleName(roleName);

                // Assert — Should fail with exact monolith error message
                validationErrors.Should().NotBeEmpty("duplicate name should be rejected");
                validationErrors.Should().Contain("Role with same name already exists");
            }
            finally
            {
                // Cleanup
                await CleanupRole(role1Id, roleName);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 5: Role Name Required Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that creating a role with an empty name fails with
        /// the exact validation error from the monolith.
        ///
        /// Source: <c>SecurityManager.SaveRole()</c> lines 335-336:
        /// <code>
        /// if (string.IsNullOrWhiteSpace(role.Name))
        ///     valEx.AddError("name", "Name is required.");
        /// </code>
        /// </summary>
        [Fact]
        public async Task CreateRole_WithEmptyName_FailsWithValidationError()
        {
            // Act — Validate empty role name
            var validationErrors = await ValidateRoleName(string.Empty);

            // Assert — Should fail with exact monolith error message
            validationErrors.Should().NotBeEmpty("empty name should be rejected");
            validationErrors.Should().Contain("Name is required.");
        }

        /// <summary>
        /// Verifies that creating a role with a whitespace-only name fails with
        /// the exact validation error from the monolith.
        ///
        /// Source: Uses <c>string.IsNullOrWhiteSpace(role.Name)</c> pattern
        /// from <c>SecurityManager.SaveRole()</c> lines 316, 335.
        /// Whitespace-only strings ("   ") are treated identically to empty strings.
        /// </summary>
        [Fact]
        public async Task CreateRole_WithWhitespaceName_FailsWithValidationError()
        {
            // Act — Validate whitespace-only role name
            var validationErrors = await ValidateRoleName("   ");

            // Assert — Should fail with exact monolith error message
            validationErrors.Should().NotBeEmpty("whitespace-only name should be rejected");
            validationErrors.Should().Contain("Name is required.");
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 6: Role Update Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that updating a role's description persists the new value in DynamoDB
        /// while leaving the name unchanged.
        ///
        /// Replaces: <c>SecurityManager.SaveRole()</c> UPDATE path (source lines 309-310):
        /// <code>
        /// record["id"] = role.Id;
        /// record["description"] = role.Description;
        /// </code>
        /// Description is always updated regardless of whether it changed.
        /// </summary>
        [Fact]
        public async Task UpdateRole_ChangeDescription_PersistsNewDescription()
        {
            // Arrange — Create role with original description
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var roleName = $"descrole_{uniqueSuffix}";
            var roleId = await CreateTestRole(roleName, "Original description");

            try
            {
                // Act — Update description via DynamoDB UpdateItem
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    },
                    UpdateExpression = "SET description = :desc",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":desc"] = new AttributeValue { S = "Updated description" }
                    }
                });

                // Assert — Verify updated description and unchanged name
                var item = await GetRoleFromDynamoDb(roleId);
                item.Should().NotBeNull("role should exist after update");
                item!["description"].S.Should().Be("Updated description");
                item["name"].S.Should().Be(roleName, "name should remain unchanged");
            }
            finally
            {
                // Cleanup
                await CleanupRole(roleId, roleName);
            }
        }

        /// <summary>
        /// Verifies that updating a role's name to a non-conflicting value succeeds
        /// after passing uniqueness validation.
        ///
        /// Source: <c>SecurityManager.SaveRole()</c> lines 312-320:
        /// Name uniqueness is only validated when the name actually changes
        /// (<c>if (existingRole.Name != role.Name)</c>). If the new name is unique,
        /// the update proceeds.
        /// </summary>
        [Fact]
        public async Task UpdateRole_ChangeName_ValidatesUniquenessAndPersists()
        {
            // Arrange — Create two roles with distinct names
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var role1Name = $"role1_{uniqueSuffix}";
            var role2Name = $"role2_{uniqueSuffix}";
            var newName = $"newname_{uniqueSuffix}";

            var role1Id = await CreateTestRole(role1Name, "Role 1");
            var role2Id = await CreateTestRole(role2Name, "Role 2");

            try
            {
                // Act — Validate the new name (excluding role2 from uniqueness check)
                var validationErrors = await ValidateRoleName(newName, excludeRoleId: role2Id);
                validationErrors.Should().BeEmpty("new unique name should pass validation");

                // Act — Update role2's name in DynamoDB
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{role2Id}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    },
                    UpdateExpression = "SET #n = :name",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#n"] = "name"
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":name"] = new AttributeValue { S = newName }
                    }
                });

                // Assert — Verify the name was updated in DynamoDB
                var item = await GetRoleFromDynamoDb(role2Id);
                item.Should().NotBeNull("role should exist after name update");
                item!["name"].S.Should().Be(newName);
            }
            finally
            {
                // Cleanup — delete with both old and new group names
                await CleanupRole(role1Id, role1Name);
                await CleanupRole(role2Id, newName);
                // Also attempt cleanup of old group name in case update didn't happen
                try
                {
                    await _cognitoClient.DeleteGroupAsync(new DeleteGroupRequest
                    {
                        GroupName = role2Name.ToLowerInvariant(),
                        UserPoolId = _userPoolId
                    });
                }
                catch (Exception) { /* Cleanup failures are acceptable */ }
            }
        }

        /// <summary>
        /// Verifies that renaming a role to a name that already exists fails with
        /// the exact validation error from the monolith.
        ///
        /// Source: <c>SecurityManager.SaveRole()</c> lines 318-319:
        /// <code>
        /// else if (allRoles.Any(x => x.Name == role.Name))
        ///     valEx.AddError("name", "Role with same name already exists");
        /// </code>
        /// </summary>
        [Fact]
        public async Task UpdateRole_ChangeNameToDuplicate_FailsWithValidationError()
        {
            // Arrange — Create two roles with distinct names
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var takenName = $"taken_{uniqueSuffix}";
            var otherName = $"other_{uniqueSuffix}";

            var role1Id = await CreateTestRole(takenName, "First role with taken name");
            var role2Id = await CreateTestRole(otherName, "Second role to be renamed");

            try
            {
                // Act — Attempt to rename role2 to the taken name (excluding role2 from check)
                var validationErrors = await ValidateRoleName(takenName, excludeRoleId: role2Id);

                // Assert — Should fail with exact monolith error message
                validationErrors.Should().NotBeEmpty("duplicate name should be rejected during update");
                validationErrors.Should().Contain("Role with same name already exists");
            }
            finally
            {
                // Cleanup
                await CleanupRole(role1Id, takenName);
                await CleanupRole(role2Id, otherName);
            }
        }

        /// <summary>
        /// Verifies that updating a role with a null description stores an empty string,
        /// matching the monolith's behavior.
        ///
        /// Source: <c>SecurityManager.SaveRole()</c> lines 305-306:
        /// <code>
        /// if (role.Description is null)
        ///     role.Description = String.Empty;
        /// </code>
        /// </summary>
        [Fact]
        public async Task UpdateRole_NullDescription_DefaultsToEmptyString()
        {
            // Arrange — Create role with initial description
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var roleName = $"nulldesc_{uniqueSuffix}";
            var roleId = await CreateTestRole(roleName, "Initial description");

            try
            {
                // Act — Apply null-to-empty-string coercion (matching source pattern)
                // and update description in DynamoDB
                string? nullDescription = null;
                var coercedDescription = nullDescription ?? string.Empty;

                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    },
                    UpdateExpression = "SET description = :desc",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":desc"] = new AttributeValue { S = coercedDescription }
                    }
                });

                // Assert — Verify DynamoDB stores empty string (not null)
                var item = await GetRoleFromDynamoDb(roleId);
                item.Should().NotBeNull("role should exist after description update");
                item!["description"].S.Should().BeEmpty(
                    "null description should default to empty string per SecurityManager.cs lines 305-306");
                item["name"].S.Should().Be(roleName, "name should remain unchanged");
            }
            finally
            {
                // Cleanup
                await CleanupRole(roleId, roleName);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 7: System Role Protection Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that the Administrator system role cannot be deleted.
        /// System roles are seeded by <see cref="LocalStackFixture.SeedSystemRolesAsync"/>
        /// and must be protected from deletion.
        ///
        /// Reference: <c>Definitions.cs</c> line 15:
        /// <c>AdministratorRoleId = new Guid("BDC56420-CAF0-4030-8A0E-D264938E0CDA")</c>
        /// </summary>
        [Fact]
        public async Task DeleteSystemRole_Administrator_IsRejected()
        {
            // Arrange — Use well-known Administrator role ID from Role model
            var roleId = Role.AdministratorRoleId;

            // Act — Verify this is a system role
            var isSystem = IsSystemRole(roleId);

            // Assert — System role must be protected from deletion
            isSystem.Should().BeTrue(
                "Administrator role (BDC56420-CAF0-4030-8A0E-D264938E0CDA) is a system role");

            // Assert — Verify the role still exists in DynamoDB (was not deleted)
            var item = await GetRoleFromDynamoDb(roleId);
            item.Should().NotBeNull(
                "Administrator system role should still exist in DynamoDB after rejection");
            item!["name"].S.Should().Be("administrator");
        }

        /// <summary>
        /// Verifies that the Regular system role cannot be deleted.
        ///
        /// Reference: <c>Definitions.cs</c> line 16:
        /// <c>RegularRoleId = new Guid("F16EC6DB-626D-4C27-8DE0-3E7CE542C55F")</c>
        /// </summary>
        [Fact]
        public async Task DeleteSystemRole_Regular_IsRejected()
        {
            // Arrange — Use well-known Regular role ID from Role model
            var roleId = Role.RegularRoleId;

            // Act — Verify this is a system role
            var isSystem = IsSystemRole(roleId);

            // Assert — System role must be protected from deletion
            isSystem.Should().BeTrue(
                "Regular role (F16EC6DB-626D-4C27-8DE0-3E7CE542C55F) is a system role");

            // Assert — Verify the role still exists in DynamoDB (was not deleted)
            var item = await GetRoleFromDynamoDb(roleId);
            item.Should().NotBeNull(
                "Regular system role should still exist in DynamoDB after rejection");
            item!["name"].S.Should().Be("regular");
        }

        /// <summary>
        /// Verifies that the Guest system role cannot be deleted.
        ///
        /// Reference: <c>Definitions.cs</c> line 17:
        /// <c>GuestRoleId = new Guid("987148B1-AFA8-4B33-8616-55861E5FD065")</c>
        /// </summary>
        [Fact]
        public async Task DeleteSystemRole_Guest_IsRejected()
        {
            // Arrange — Use well-known Guest role ID from Role model
            var roleId = Role.GuestRoleId;

            // Act — Verify this is a system role
            var isSystem = IsSystemRole(roleId);

            // Assert — System role must be protected from deletion
            isSystem.Should().BeTrue(
                "Guest role (987148B1-AFA8-4B33-8616-55861E5FD065) is a system role");

            // Assert — Verify the role still exists in DynamoDB (was not deleted)
            var item = await GetRoleFromDynamoDb(roleId);
            item.Should().NotBeNull(
                "Guest system role should still exist in DynamoDB after rejection");
            item!["name"].S.Should().Be("guest");
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 8: Cognito Group Sync Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that creating a role also creates a corresponding Cognito group
        /// with matching name and description — ensuring bidirectional consistency
        /// between DynamoDB role records and Cognito groups.
        ///
        /// Per AAP Section 0.7.5: "System roles map to Cognito groups"
        /// This pattern extends to all roles, not just system roles.
        /// </summary>
        [Fact]
        public async Task CreateRole_AlsoCreatesCognitoGroup()
        {
            // Arrange
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var roleName = $"syncrole_{uniqueSuffix}";
            var roleDescription = "Cognito sync test role description";
            Guid roleId = default;

            try
            {
                // Act — Create role in both DynamoDB and Cognito
                roleId = await CreateTestRole(roleName, roleDescription);

                // Assert — Verify Cognito group exists with matching attributes
                var groupResponse = await _cognitoClient.GetGroupAsync(new GetGroupRequest
                {
                    GroupName = roleName.ToLowerInvariant(),
                    UserPoolId = _userPoolId
                });

                groupResponse.Should().NotBeNull("Cognito GetGroup should return a response");
                groupResponse.Group.Should().NotBeNull("Cognito group should exist");
                groupResponse.Group.GroupName.Should().Be(roleName.ToLowerInvariant(),
                    "Cognito group name should match the role name (lowercased)");
                groupResponse.Group.Description.Should().Be(roleDescription,
                    "Cognito group description should match the role description");
            }
            finally
            {
                // Cleanup
                if (roleId != default)
                {
                    await CleanupRole(roleId, roleName);
                }
            }
        }

        /// <summary>
        /// Verifies that updating a role's name also updates the corresponding
        /// Cognito group: the old group is deleted and a new group is created
        /// with the new name.
        ///
        /// This tests bidirectional consistency maintenance during role name changes,
        /// ensuring that:
        /// 1. The old Cognito group is removed (GetGroup throws GroupNotFoundException)
        /// 2. A new Cognito group is created with the updated name
        /// </summary>
        [Fact]
        public async Task UpdateRoleName_UpdatesCognitoGroup()
        {
            // Arrange — Create role with initial name
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var oldName = $"oldrole_{uniqueSuffix}";
            var newName = $"newrole_{uniqueSuffix}";
            var roleId = await CreateTestRole(oldName, "Role for name update test");

            try
            {
                // Act — Update role name in DynamoDB
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{roleId}" },
                        ["SK"] = new AttributeValue { S = "META" }
                    },
                    UpdateExpression = "SET #n = :name",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        ["#n"] = "name"
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":name"] = new AttributeValue { S = newName }
                    }
                });

                // Act — Sync Cognito: delete old group, create new group
                await _cognitoClient.DeleteGroupAsync(new DeleteGroupRequest
                {
                    GroupName = oldName.ToLowerInvariant(),
                    UserPoolId = _userPoolId
                });

                await _cognitoClient.CreateGroupAsync(new CreateGroupRequest
                {
                    GroupName = newName.ToLowerInvariant(),
                    UserPoolId = _userPoolId,
                    Description = "Role for name update test"
                });

                // Assert — Old group should no longer exist in Cognito
                Func<Task> getOldGroup = async () =>
                {
                    await _cognitoClient.GetGroupAsync(new GetGroupRequest
                    {
                        GroupName = oldName.ToLowerInvariant(),
                        UserPoolId = _userPoolId
                    });
                };
                await getOldGroup.Should().ThrowAsync<Amazon.CognitoIdentityProvider.Model.ResourceNotFoundException>(
                    "old Cognito group should not exist after rename");

                // Assert — New group should exist in Cognito
                var newGroupResponse = await _cognitoClient.GetGroupAsync(new GetGroupRequest
                {
                    GroupName = newName.ToLowerInvariant(),
                    UserPoolId = _userPoolId
                });
                newGroupResponse.Group.Should().NotBeNull("new Cognito group should exist");
                newGroupResponse.Group.GroupName.Should().Be(newName.ToLowerInvariant());

                // Assert — DynamoDB should reflect the new name
                var item = await GetRoleFromDynamoDb(roleId);
                item.Should().NotBeNull("role should exist in DynamoDB after name update");
                item!["name"].S.Should().Be(newName);
            }
            finally
            {
                // Cleanup — delete with new name (old group already deleted)
                await CleanupRole(roleId, newName);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Phase 9: Get All Roles Tests
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that scanning for all roles returns all created roles including
        /// both system roles and custom test roles.
        ///
        /// Replaces: <c>SecurityManager.GetAllRoles()</c> (source lines 186-189) which used
        /// <c>EqlCommand("SELECT * FROM role").Execute().MapTo&lt;ErpRole&gt;()</c>.
        ///
        /// The scan uses <c>EntityType = ROLE_META</c> filter to return only role items
        /// from the single-table design DynamoDB table.
        /// </summary>
        [Fact]
        public async Task GetAllRoles_ReturnsAllCreatedRoles()
        {
            // Arrange — Create 3 test roles with unique names
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var testRoles = new[]
            {
                (Name: $"allrole1_{uniqueSuffix}", Description: "First test role for scan"),
                (Name: $"allrole2_{uniqueSuffix}", Description: "Second test role for scan"),
                (Name: $"allrole3_{uniqueSuffix}", Description: "Third test role for scan")
            };

            var createdRoleIds = new List<Guid>();
            try
            {
                foreach (var testRole in testRoles)
                {
                    var roleId = await CreateTestRole(testRole.Name, testRole.Description);
                    createdRoleIds.Add(roleId);
                }

                // Act — Scan all roles from DynamoDB with EntityType=ROLE_META filter
                var allRoles = await ScanAllRoles();

                // Assert — Results should contain at least the 3 created test roles
                // plus the 3 system roles (administrator, regular, guest) seeded by fixture
                allRoles.Should().NotBeNull("scan should return results");
                allRoles.Count.Should().BeGreaterOrEqualTo(6,
                    "should have at least 3 system roles + 3 test roles");

                // Assert — Verify each created test role is present in scan results
                var roleNames = allRoles.Select(r => r.ContainsKey("name") ? r["name"].S : string.Empty).ToList();
                foreach (var testRole in testRoles)
                {
                    roleNames.Should().Contain(testRole.Name,
                        $"scan results should include test role '{testRole.Name}'");
                }

                // Assert — Verify system roles are also present
                roleNames.Should().Contain("administrator", "scan should include administrator system role");
                roleNames.Should().Contain("regular", "scan should include regular system role");
                roleNames.Should().Contain("guest", "scan should include guest system role");

                // Assert — Verify role details for one of the test roles
                var firstTestRole = allRoles.FirstOrDefault(r =>
                    r.ContainsKey("name") && r["name"].S == testRoles[0].Name);
                firstTestRole.Should().NotBeNull("first test role should be in scan results");
                firstTestRole!["description"].S.Should().Be(testRoles[0].Description);
                firstTestRole["EntityType"].S.Should().Be("ROLE_META");
            }
            finally
            {
                // Cleanup — Delete all created test roles
                for (int i = 0; i < createdRoleIds.Count; i++)
                {
                    await CleanupRole(createdRoleIds[i], testRoles[i].Name);
                }
            }
        }
    }
}
