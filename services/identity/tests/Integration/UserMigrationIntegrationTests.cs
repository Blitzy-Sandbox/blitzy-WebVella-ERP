using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
    /// Integration tests for the MD5-to-Cognito password migration flow.
    ///
    /// Per AAP Section 0.7.5: "Deploy a User Migration Lambda Trigger on the Cognito user pool.
    /// On first login attempt, the trigger calls the legacy password verification logic.
    /// If the MD5 hash matches, the Lambda creates the user in Cognito with the provided password."
    ///
    /// These tests validate the complete user migration lifecycle:
    /// 1. Correctness of MD5 hash computation matching PasswordUtil.GetMd5Hash (UTF-8, lowercase hex)
    /// 2. Case-insensitive MD5 hash verification matching PasswordUtil.VerifyMd5Hash
    /// 3. End-to-end migration flow: DynamoDB legacy hash → verify → create Cognito user → auth works
    /// 4. Migration idempotency: second migration attempt does not fail
    /// 5. System default user migration: erp@webvella.com / erp migrates correctly
    ///
    /// All tests run against REAL LocalStack Cognito + DynamoDB. ZERO mocked AWS SDK calls.
    /// Pattern: docker compose up -d → test → docker compose down
    ///
    /// Source mapping:
    ///   PasswordUtil.cs lines 11-23 (GetMd5Hash)     → ComputeMd5Hash helper + MD5 correctness tests
    ///   PasswordUtil.cs lines 25-30 (VerifyMd5Hash)   → VerifyMd5Hash helper + verification tests
    ///   CryptoUtility.cs line 182 (Encoding.Unicode)  → GetMd5Hash_UsesUtf8NotUnicode contrast test
    ///   SecurityManager.cs line 84 (GetUser login)    → Migration trigger flow tests
    ///   Definitions.cs lines 19-20 (SystemIds)        → SystemDefaultUser_MigratesFromMd5 test
    /// </summary>
    public class UserMigrationIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly IAmazonCognitoIdentityProvider _cognitoClient;
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly string _userPoolId;
        private readonly string _clientId;
        private readonly string _tableName;

        /// <summary>
        /// Constructor extracts pre-configured AWS SDK clients and resource IDs from the
        /// shared LocalStackFixture. The fixture handles Cognito user pool creation (with
        /// relaxed password policy allowing simple passwords like "erp"), app client
        /// configuration (ADMIN_USER_PASSWORD_AUTH enabled), system role Cognito groups,
        /// and DynamoDB identity table provisioning with single-table design (PK/SK + GSI1/GSI2).
        /// </summary>
        /// <param name="fixture">Shared LocalStack infrastructure fixture providing pre-configured
        /// AWS SDK clients and resource identifiers.</param>
        public UserMigrationIntegrationTests(LocalStackFixture fixture)
        {
            _cognitoClient = fixture.CognitoClient;
            _dynamoDbClient = fixture.DynamoDbClient;
            _userPoolId = fixture.UserPoolId;
            _clientId = fixture.ClientId;
            _tableName = fixture.TableName;
        }

        // ──────────────────────────────────────────────────────────────────────
        // MD5 Hash Computation Helper — EXACT replica of PasswordUtil.GetMd5Hash
        // Source: WebVella.Erp/Utilities/PasswordUtil.cs lines 11-23
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// EXACT replica of PasswordUtil.GetMd5Hash from the monolith.
        /// Uses UTF-8 encoding (NOT Unicode) and lowercase hex "x2" format.
        ///
        /// CRITICAL encoding note:
        /// - PasswordUtil.GetMd5Hash (line 16): Encoding.UTF8.GetBytes — THIS is the method
        ///   used for password hashing in the monolith's SecurityManager.GetUser (line 84)
        /// - CryptoUtility.ComputeOddMD5Hash (line 182): Encoding.Unicode.GetBytes — DIFFERENT
        ///   method, used for different purposes. Must NOT be confused with password hashing.
        ///
        /// Source lines:
        ///   13-14: IsNullOrWhiteSpace check → return string.Empty
        ///   16:    Encoding.UTF8.GetBytes(input) → byte[] data
        ///   18-20: StringBuilder + ToString("x2") → lowercase hex string
        /// </summary>
        /// <param name="input">The plaintext password to hash.</param>
        /// <returns>Lowercase hex MD5 hash string, or string.Empty if input is null/whitespace.</returns>
        private static string ComputeMd5Hash(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            byte[] data = MD5.HashData(Encoding.UTF8.GetBytes(input));

            StringBuilder sBuilder = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
                sBuilder.Append(data[i].ToString("x2"));

            return sBuilder.ToString();
        }

        // ──────────────────────────────────────────────────────────────────────
        // MD5 Hash Verification Helper — EXACT replica of PasswordUtil.VerifyMd5Hash
        // Source: WebVella.Erp/Utilities/PasswordUtil.cs lines 25-30
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// EXACT replica of PasswordUtil.VerifyMd5Hash from the monolith.
        /// Uses StringComparer.OrdinalIgnoreCase for case-insensitive hash comparison.
        ///
        /// Source lines:
        ///   27: GetMd5Hash(input) → compute hash of input
        ///   28: StringComparer.OrdinalIgnoreCase → case-insensitive comparer
        ///   29: comparer.Compare(hashOfInput, hash) == 0 → equality check
        /// </summary>
        /// <param name="input">The plaintext password to verify.</param>
        /// <param name="hash">The stored MD5 hash to compare against.</param>
        /// <returns>True if the MD5 hash of input matches the stored hash (case-insensitive).</returns>
        private static bool VerifyMd5Hash(string input, string hash)
        {
            string hashOfInput = ComputeMd5Hash(input);
            return StringComparer.OrdinalIgnoreCase.Compare(hashOfInput, hash) == 0;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Migration Flow Helper Methods
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Stores a "legacy" user record in DynamoDB simulating the pre-migration state.
        /// The user has an MD5 password hash stored in the legacy_password_hash attribute,
        /// mimicking the monolith's PostgreSQL rec_user table where passwords were stored
        /// as MD5 hashes via PasswordUtil.GetMd5Hash.
        ///
        /// DynamoDB single-table design: PK=USER#{userId}, SK=PROFILE
        /// Additional attributes: email, username, legacy_password_hash, first_name, last_name, enabled
        /// </summary>
        private async Task StoreLegacyUserInDynamoDbAsync(
            Guid userId,
            string email,
            string username,
            string md5PasswordHash)
        {
            var putRequest = new PutItemRequest
            {
                TableName = _tableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["SK"] = new AttributeValue { S = "PROFILE" },
                    ["GSI1PK"] = new AttributeValue { S = $"EMAIL#{email.ToLowerInvariant()}" },
                    ["GSI1SK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["GSI2PK"] = new AttributeValue { S = $"USERNAME#{username.ToLowerInvariant()}" },
                    ["GSI2SK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["email"] = new AttributeValue { S = email },
                    ["username"] = new AttributeValue { S = username },
                    ["legacy_password_hash"] = new AttributeValue { S = md5PasswordHash },
                    ["first_name"] = new AttributeValue { S = "Test" },
                    ["last_name"] = new AttributeValue { S = "User" },
                    ["enabled"] = new AttributeValue { BOOL = true }
                }
            };
            await _dynamoDbClient.PutItemAsync(putRequest);
        }

        /// <summary>
        /// Retrieves the legacy MD5 password hash from DynamoDB for a given user ID.
        /// This simulates the migration trigger reading the stored hash to verify
        /// against the plaintext password provided during the first Cognito login attempt.
        /// </summary>
        private async Task<string> GetLegacyPasswordHashAsync(Guid userId)
        {
            var getRequest = new GetItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["SK"] = new AttributeValue { S = "PROFILE" }
                }
            };
            var response = await _dynamoDbClient.GetItemAsync(getRequest);
            if (response.Item == null || !response.Item.ContainsKey("legacy_password_hash"))
                return string.Empty;

            return response.Item["legacy_password_hash"].S;
        }

        /// <summary>
        /// Simulates the complete User Migration Lambda Trigger flow described in AAP Section 0.7.5.
        ///
        /// Migration steps:
        /// 1. Retrieve legacy MD5 password hash from DynamoDB
        /// 2. Compute MD5 of the provided plaintext password
        /// 3. Verify hash matches (case-insensitive, matching PasswordUtil.VerifyMd5Hash)
        /// 4. If match: AdminCreateUser in Cognito + AdminSetUserPassword (permanent)
        /// 5. Return true indicating successful migration
        ///
        /// If the hash does not match (wrong password), the migration is rejected (return false).
        /// If the user already exists in Cognito (UsernameExistsException), handles idempotently (return true).
        /// </summary>
        /// <param name="userId">The legacy user ID used as DynamoDB key.</param>
        /// <param name="email">The user's email (used as Cognito username).</param>
        /// <param name="username">The user's preferred username (stored as Cognito attribute).</param>
        /// <param name="plainPassword">The plaintext password provided during login attempt.</param>
        /// <returns>True if migration succeeded or user already exists; false if password verification failed.</returns>
        private async Task<bool> MigrateUserToCognitoAsync(
            Guid userId,
            string email,
            string username,
            string plainPassword)
        {
            // Step 1: Retrieve the legacy MD5 hash from DynamoDB
            string storedHash = await GetLegacyPasswordHashAsync(userId);
            if (string.IsNullOrEmpty(storedHash))
                return false;

            // Step 2-3: Verify the plaintext password against the stored MD5 hash
            // Uses the exact same algorithm as PasswordUtil.VerifyMd5Hash (UTF-8 encoding, OrdinalIgnoreCase)
            if (!VerifyMd5Hash(plainPassword, storedHash))
                return false;

            // Step 4: Create user in Cognito with AdminCreateUser + set permanent password
            try
            {
                var createUserRequest = new AdminCreateUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email,
                    MessageAction = MessageActionType.SUPPRESS,
                    UserAttributes = new List<AttributeType>
                    {
                        new AttributeType { Name = "email", Value = email },
                        new AttributeType { Name = "email_verified", Value = "true" },
                        new AttributeType { Name = "preferred_username", Value = username },
                        new AttributeType { Name = "custom:legacy_user_id", Value = userId.ToString() }
                    }
                };
                await _cognitoClient.AdminCreateUserAsync(createUserRequest);

                // Set permanent password (Cognito hashes it securely, replacing the legacy MD5 hash)
                var setPasswordRequest = new AdminSetUserPasswordRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email,
                    Password = plainPassword,
                    Permanent = true
                };
                await _cognitoClient.AdminSetUserPasswordAsync(setPasswordRequest);
            }
            catch (UsernameExistsException)
            {
                // Idempotent migration: user already migrated, not an error
                // Per AAP: "Migrate same user twice → verify no error on second attempt"
                return true;
            }

            return true;
        }

        /// <summary>
        /// Clears the legacy_password_hash attribute from DynamoDB after successful migration.
        /// This ensures the migration is a one-way operation — once migrated to Cognito,
        /// the legacy MD5 hash is no longer needed and should be removed for security.
        /// </summary>
        private async Task ClearLegacyPasswordHashAsync(Guid userId)
        {
            var updateRequest = new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["PK"] = new AttributeValue { S = $"USER#{userId}" },
                    ["SK"] = new AttributeValue { S = "PROFILE" }
                },
                UpdateExpression = "REMOVE legacy_password_hash",
            };
            await _dynamoDbClient.UpdateItemAsync(updateRequest);
        }

        /// <summary>
        /// Safely deletes a Cognito user, suppressing UserNotFoundException if the user
        /// was already deleted or never created (e.g., migration failed before creation).
        /// </summary>
        private async Task SafeDeleteCognitoUserAsync(string email)
        {
            try
            {
                await _cognitoClient.AdminDeleteUserAsync(new AdminDeleteUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email
                });
            }
            catch (UserNotFoundException)
            {
                // User was never created or already cleaned up — not an error
            }
        }

        /// <summary>
        /// Safely deletes a DynamoDB item, suppressing errors if the item doesn't exist.
        /// </summary>
        private async Task SafeDeleteDynamoDbItemAsync(Guid userId)
        {
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
                // Cleanup failure should not mask test assertions
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 4: MD5 Hash Correctness Tests
        // Validates that ComputeMd5Hash produces identical output to PasswordUtil.GetMd5Hash
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verifies that ComputeMd5Hash produces the correct MD5 hash for the known input "erp"
        /// (the default system user password from AAP Section 0.7.5).
        ///
        /// Expected: MD5("erp" in UTF-8) = "def6d90e829e50c63f98c387daecd138"
        ///
        /// This validates that the migration service will correctly compute the same hash
        /// as the monolith's PasswordUtil.GetMd5Hash, ensuring legacy users can authenticate
        /// during the MD5→Cognito migration window.
        /// </summary>
        [Fact]
        public async Task GetMd5Hash_WithKnownInput_ProducesCorrectHash()
        {
            // Arrange: Known input "erp" — the default system user password
            // MD5("erp" in UTF-8 bytes 0x65, 0x72, 0x70) = "def6d90e829e50c63f98c387daecd138"
            const string input = "erp";
            const string expectedHash = "def6d90e829e50c63f98c387daecd138";

            // Act
            string actualHash = ComputeMd5Hash(input);

            // Assert: Hash must match exactly — lowercase hex, 32 characters
            actualHash.Should().Be(expectedHash,
                because: "MD5 of 'erp' with UTF-8 encoding must produce the exact same hash as " +
                         "the monolith's PasswordUtil.GetMd5Hash to ensure migration correctness");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies that ComputeMd5Hash returns string.Empty for empty string input,
        /// matching PasswordUtil.GetMd5Hash source line 13-14:
        ///   if (string.IsNullOrWhiteSpace(input))
        ///       return string.Empty;
        /// </summary>
        [Fact]
        public async Task GetMd5Hash_WithEmptyInput_ReturnsEmptyString()
        {
            // Act
            string result = ComputeMd5Hash("");

            // Assert
            result.Should().BeEmpty(
                because: "PasswordUtil.GetMd5Hash returns string.Empty for empty input " +
                         "(source line 13-14: string.IsNullOrWhiteSpace check)");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies that ComputeMd5Hash returns string.Empty for whitespace-only input,
        /// matching the string.IsNullOrWhiteSpace check on PasswordUtil.cs line 13.
        /// </summary>
        [Fact]
        public async Task GetMd5Hash_WithWhitespaceInput_ReturnsEmptyString()
        {
            // Act
            string result = ComputeMd5Hash("   ");

            // Assert
            result.Should().BeEmpty(
                because: "PasswordUtil.GetMd5Hash returns string.Empty for whitespace input " +
                         "(source line 13: string.IsNullOrWhiteSpace('   ') returns true)");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies that VerifyMd5Hash returns true when the input matches the stored hash.
        /// Matches PasswordUtil.VerifyMd5Hash source lines 25-30:
        ///   string hashOfInput = GetMd5Hash(input);
        ///   StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        ///   return (0 == comparer.Compare(hashOfInput, hash));
        /// </summary>
        [Fact]
        public async Task VerifyMd5Hash_WithCorrectInput_ReturnsTrue()
        {
            // Arrange
            const string password = "TestPassword123";
            string computedHash = ComputeMd5Hash(password);

            // Act
            bool result = VerifyMd5Hash(password, computedHash);

            // Assert
            result.Should().BeTrue(
                because: "VerifyMd5Hash must return true when the plaintext password produces " +
                         "a hash matching the stored hash (case-insensitive per source line 29)");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verifies that VerifyMd5Hash is case-insensitive for hash comparison,
        /// matching the use of StringComparer.OrdinalIgnoreCase on PasswordUtil.cs line 28.
        ///
        /// This is critical for migration because some storage systems may normalize
        /// hash casing. The monolith's PasswordUtil.VerifyMd5Hash handles this correctly
        /// with OrdinalIgnoreCase, and our migration must preserve this behavior.
        /// </summary>
        [Fact]
        public async Task VerifyMd5Hash_IsCaseInsensitive()
        {
            // Arrange: compute hash (lowercase by default due to "x2" format)
            const string password = "CaseSensitiveTest!";
            string lowercaseHash = ComputeMd5Hash(password);
            string uppercaseHash = lowercaseHash.ToUpperInvariant();

            // Sanity: verify they differ in casing
            lowercaseHash.Should().NotBe(uppercaseHash,
                because: "lowercase and uppercase hex strings differ in casing");

            // Act: verify against uppercase hash
            bool result = VerifyMd5Hash(password, uppercaseHash);

            // Assert: OrdinalIgnoreCase comparison per source line 28
            result.Should().BeTrue(
                because: "PasswordUtil.VerifyMd5Hash uses StringComparer.OrdinalIgnoreCase (line 28), " +
                         "so hash comparison must be case-insensitive");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Proves that ComputeMd5Hash uses Encoding.UTF8 (matching PasswordUtil.cs line 16)
        /// and NOT Encoding.Unicode (which CryptoUtility.ComputeOddMD5Hash uses on line 182).
        ///
        /// This distinction is CRITICAL for migration correctness: the monolith's password
        /// hashing uses UTF-8 via PasswordUtil.GetMd5Hash, while CryptoUtility.ComputeOddMD5Hash
        /// uses Unicode encoding. If the migration service uses the wrong encoding, ALL legacy
        /// password verifications will fail.
        ///
        /// Test: hash a non-ASCII string with UTF-8 and Unicode — the results MUST differ.
        /// </summary>
        [Fact]
        public async Task GetMd5Hash_UsesUtf8NotUnicode()
        {
            // Arrange: non-ASCII input where UTF-8 and Unicode produce different byte sequences
            const string input = "pässwörd";

            // Act: compute with UTF-8 (our implementation, matching PasswordUtil.cs line 16)
            string utf8Hash = ComputeMd5Hash(input);

            // Compute with Unicode (Encoding.Unicode = UTF-16LE) as CryptoUtility.ComputeOddMD5Hash does
            byte[] unicodeData = MD5.HashData(Encoding.Unicode.GetBytes(input));
            StringBuilder unicodeSb = new StringBuilder();
            for (int i = 0; i < unicodeData.Length; i++)
                unicodeSb.Append(unicodeData[i].ToString("x2"));
            string unicodeHash = unicodeSb.ToString();

            // Assert: UTF-8 and Unicode hashes must differ for non-ASCII input
            utf8Hash.Should().NotBe(unicodeHash,
                because: "PasswordUtil.GetMd5Hash uses Encoding.UTF8 (line 16), which produces " +
                         "different byte sequences than Encoding.Unicode for non-ASCII characters. " +
                         "CryptoUtility.ComputeOddMD5Hash (line 182) uses Encoding.Unicode — " +
                         "these are DIFFERENT methods and must NOT be confused during migration.");

            await Task.CompletedTask;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 5: Migration Trigger Flow Tests (Against LocalStack Cognito)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// End-to-end test of the migration trigger flow:
        /// 1. Store a legacy user in DynamoDB with an MD5 password hash
        /// 2. Simulate migration: verify MD5 → create Cognito user → set password
        /// 3. Verify user exists in Cognito (AdminGetUser succeeds)
        /// 4. Verify standard Cognito auth works (AdminInitiateAuth succeeds)
        /// 5. Verify legacy hash can be cleared from DynamoDB post-migration
        ///
        /// Per AAP Section 0.7.5: "On first login attempt, the trigger calls the legacy
        /// password verification logic. If the MD5 hash matches, the Lambda creates the
        /// user in Cognito with the provided password."
        /// </summary>
        [Fact]
        public async Task MigrateUser_WithCorrectMd5Hash_CreatesUserInCognito()
        {
            // Arrange: unique user for test isolation
            var userId = Guid.NewGuid();
            string email = $"migrate-correct-{userId:N}@test.webvella.com";
            string username = $"migrate-correct-{userId:N}";
            const string password = "LegacyPass123";
            string md5Hash = ComputeMd5Hash(password);

            try
            {
                // Store legacy user with MD5 hash in DynamoDB
                await StoreLegacyUserInDynamoDbAsync(userId, email, username, md5Hash);

                // Act: simulate migration trigger flow
                bool migrationResult = await MigrateUserToCognitoAsync(userId, email, username, password);

                // Assert 1: migration succeeded
                migrationResult.Should().BeTrue(
                    because: "migration should succeed when the plaintext password's MD5 hash " +
                             "matches the stored legacy hash");

                // Assert 2: user now exists in Cognito
                var getUserResponse = await _cognitoClient.AdminGetUserAsync(new AdminGetUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = email
                });
                getUserResponse.Should().NotBeNull(
                    because: "after migration, the user must exist in Cognito");

                // Assert 3: standard Cognito authentication works post-migration
                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                    AuthParameters = new Dictionary<string, string>
                    {
                        ["USERNAME"] = email,
                        ["PASSWORD"] = password
                    }
                });
                authResponse.AuthenticationResult.Should().NotBeNull(
                    because: "after migration, Cognito native authentication must work");

                // Assert 4: legacy hash can be cleared from DynamoDB
                await ClearLegacyPasswordHashAsync(userId);
                string clearedHash = await GetLegacyPasswordHashAsync(userId);
                clearedHash.Should().BeEmpty(
                    because: "after successful migration, the legacy MD5 hash should be " +
                             "removed from DynamoDB (migration is complete)");
            }
            finally
            {
                // Cleanup: remove test artifacts from both Cognito and DynamoDB
                await SafeDeleteCognitoUserAsync(email);
                await SafeDeleteDynamoDbItemAsync(userId);
            }
        }

        /// <summary>
        /// Validates that after MD5→Cognito migration, users authenticate exclusively
        /// via Cognito's native password hashing — no more MD5 involved.
        ///
        /// Per AAP Section 0.7.5: "Subsequent logins use Cognito natively."
        ///
        /// Verifies:
        /// - AuthenticationResult is not null
        /// - AccessToken is returned (proves Cognito issued real JWT tokens)
        /// </summary>
        [Fact]
        public async Task MigrateUser_PostMigration_CognitoAuthWorks()
        {
            // Arrange: unique user for test isolation
            var userId = Guid.NewGuid();
            string email = $"migrate-auth-{userId:N}@test.webvella.com";
            string username = $"migrate-auth-{userId:N}";
            const string password = "Migr@te123";
            string md5Hash = ComputeMd5Hash(password);

            try
            {
                // Store legacy user and run migration
                await StoreLegacyUserInDynamoDbAsync(userId, email, username, md5Hash);
                bool migrated = await MigrateUserToCognitoAsync(userId, email, username, password);
                migrated.Should().BeTrue(because: "migration precondition must succeed");

                // Act: authenticate via standard Cognito ADMIN_USER_PASSWORD_AUTH
                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                    AuthParameters = new Dictionary<string, string>
                    {
                        ["USERNAME"] = email,
                        ["PASSWORD"] = password
                    }
                });

                // Assert: Cognito returns valid authentication tokens
                authResponse.AuthenticationResult.Should().NotBeNull(
                    because: "after migration, Cognito native authentication must return an AuthenticationResult");

                authResponse.AuthenticationResult.AccessToken.Should().NotBeNull(
                    because: "Cognito must issue a JWT AccessToken proving the user is authenticated " +
                             "natively via Cognito (no more MD5 password verification needed)");
            }
            finally
            {
                await SafeDeleteCognitoUserAsync(email);
                await SafeDeleteDynamoDbItemAsync(userId);
            }
        }

        /// <summary>
        /// Verifies that migration is rejected when the wrong password is provided.
        /// The MD5 hash of the wrong password will not match the stored legacy hash,
        /// so the user must NOT be created in Cognito.
        ///
        /// Verifies:
        /// - Migration returns false (password verification failed)
        /// - User does NOT exist in Cognito (AdminGetUser throws UserNotFoundException)
        /// </summary>
        [Fact]
        public async Task MigrateUser_WithWrongPassword_DoesNotCreateInCognito()
        {
            // Arrange: store legacy user with MD5 hash of "CorrectPass"
            var userId = Guid.NewGuid();
            string email = $"migrate-wrong-{userId:N}@test.webvella.com";
            string username = $"migrate-wrong-{userId:N}";
            const string correctPassword = "CorrectPass";
            const string wrongPassword = "WrongPass";
            string md5Hash = ComputeMd5Hash(correctPassword);

            try
            {
                await StoreLegacyUserInDynamoDbAsync(userId, email, username, md5Hash);

                // Act: attempt migration with wrong password — MD5 hashes won't match
                bool migrationResult = await MigrateUserToCognitoAsync(userId, email, username, wrongPassword);

                // Assert 1: migration was rejected
                migrationResult.Should().BeFalse(
                    because: "migration must be rejected when the MD5 hash of the provided password " +
                             "does not match the stored legacy hash");

                // Assert 2: user should NOT exist in Cognito
                Func<Task> getUserAction = async () => await _cognitoClient.AdminGetUserAsync(
                    new AdminGetUserRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = email
                    });

                await getUserAction.Should().ThrowAsync<UserNotFoundException>(
                    because: "when migration is rejected, the user must NOT be created in Cognito");
            }
            finally
            {
                await SafeDeleteCognitoUserAsync(email);
                await SafeDeleteDynamoDbItemAsync(userId);
            }
        }

        /// <summary>
        /// Verifies that migrating the same user twice does not cause an error.
        /// The second migration attempt should handle UsernameExistsException gracefully.
        ///
        /// Per AAP: "Idempotent migration: Migrate same user twice → verify no error on second attempt."
        /// Per AAP Section 0.8.5: "All event consumers MUST be idempotent."
        /// Per AAP Section 0.8.5: "Idempotency keys on all write endpoints and event handlers."
        /// </summary>
        [Fact]
        public async Task MigrateUser_IdempotentMigration_SecondAttemptDoesNotFail()
        {
            // Arrange: unique user for test isolation
            var userId = Guid.NewGuid();
            string email = $"migrate-idempotent-{userId:N}@test.webvella.com";
            string username = $"migrate-idempotent-{userId:N}";
            const string password = "Idempotent!Pass1";
            string md5Hash = ComputeMd5Hash(password);

            try
            {
                await StoreLegacyUserInDynamoDbAsync(userId, email, username, md5Hash);

                // Act: first migration — should succeed
                bool firstResult = await MigrateUserToCognitoAsync(userId, email, username, password);
                firstResult.Should().BeTrue(because: "first migration attempt must succeed");

                // Act: second migration — should not throw, handles UsernameExistsException gracefully
                bool secondResult = await MigrateUserToCognitoAsync(userId, email, username, password);

                // Assert: second attempt returns true (idempotent success)
                secondResult.Should().BeTrue(
                    because: "idempotent migration: second attempt must succeed without error, " +
                             "handling UsernameExistsException gracefully as specified in AAP");

                // Verify user still works in Cognito after idempotent migration
                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                    AuthParameters = new Dictionary<string, string>
                    {
                        ["USERNAME"] = email,
                        ["PASSWORD"] = password
                    }
                });
                authResponse.AuthenticationResult.Should().NotBeNull(
                    because: "user must still authenticate normally after idempotent migration");
            }
            finally
            {
                await SafeDeleteCognitoUserAsync(email);
                await SafeDeleteDynamoDbItemAsync(userId);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Phase 6: System Default User Migration Test
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates that the default system user (erp@webvella.com / erp) migrates
        /// correctly from MD5 to Cognito authentication.
        ///
        /// Per AAP Section 0.7.5: "The default system user (erp@webvella.com / erp)
        /// is seeded during Cognito user pool bootstrapping."
        ///
        /// Uses User.FirstUserId from the Identity models (extracted from Definitions.cs
        /// line 20: SystemIds.FirstUserId = EABD66FD-8DE1-4D79-9674-447EE89921C2).
        ///
        /// Verifies:
        /// - MD5 hash of "erp" matches the stored hash
        /// - User is created in Cognito with correct attributes
        /// - Login via Cognito succeeds with the original password
        /// </summary>
        [Fact]
        public async Task SystemDefaultUser_MigratesFromMd5()
        {
            // Arrange: use the well-known FirstUserId from User model
            // This is the default administrative user (erp@webvella.com) seeded during bootstrap
            // Note: User.SystemUserId (10000000-...) is the service account (non-human),
            //       User.FirstUserId (EABD66FD-...) is the admin user who logs in interactively
            Guid userId = User.FirstUserId;
            userId.Should().NotBe(User.SystemUserId,
                because: "the default admin user (FirstUserId) is a different identity than the " +
                         "system service account (SystemUserId) — we migrate human users, not service accounts");

            // Well-known credentials: erp@webvella.com / erp (AAP Section 0.7.5)
            const string password = "erp";

            // Compute MD5 hash matching the monolith's stored value
            string md5Hash = ComputeMd5Hash(password);

            // Verify the hash matches the known expected value
            // MD5("erp" in UTF-8) = "def6d90e829e50c63f98c387daecd138"
            md5Hash.Should().Be("def6d90e829e50c63f98c387daecd138",
                because: "the MD5 hash of the default password 'erp' must match the value " +
                         "stored in the monolith's PostgreSQL database");

            // Use a unique email to avoid fixture collision with other tests that may
            // use erp@webvella.com. The migration logic is the same regardless of email.
            string testEmail = $"erp-migration-{Guid.NewGuid():N}@webvella.com";
            string testUsername = $"erp-migration-{Guid.NewGuid():N}";

            try
            {
                // Store the default user with the known MD5 hash
                await StoreLegacyUserInDynamoDbAsync(userId, testEmail, testUsername, md5Hash);

                // Act: run migration with the default credentials
                bool migrationResult = await MigrateUserToCognitoAsync(userId, testEmail, testUsername, password);

                // Assert 1: migration succeeded
                migrationResult.Should().BeTrue(
                    because: "the default system user erp@webvella.com must migrate successfully " +
                             "from MD5 to Cognito");

                // Assert 2: user exists in Cognito
                var getUserResponse = await _cognitoClient.AdminGetUserAsync(new AdminGetUserRequest
                {
                    UserPoolId = _userPoolId,
                    Username = testEmail
                });
                getUserResponse.Should().NotBeNull(
                    because: "after migration, the default system user must exist in Cognito");

                // Assert 3: login via Cognito succeeds
                var authResponse = await _cognitoClient.AdminInitiateAuthAsync(new AdminInitiateAuthRequest
                {
                    UserPoolId = _userPoolId,
                    ClientId = _clientId,
                    AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
                    AuthParameters = new Dictionary<string, string>
                    {
                        ["USERNAME"] = testEmail,
                        ["PASSWORD"] = password
                    }
                });
                authResponse.AuthenticationResult.Should().NotBeNull(
                    because: "the default system user must be able to authenticate via Cognito " +
                             "after migration from MD5 (per AAP: 'Subsequent logins use Cognito natively')");

                authResponse.AuthenticationResult.AccessToken.Should().NotBeNull(
                    because: "Cognito must issue an AccessToken for the migrated default user");
            }
            finally
            {
                await SafeDeleteCognitoUserAsync(testEmail);
                await SafeDeleteDynamoDbItemAsync(userId);
            }
        }
    }
}
