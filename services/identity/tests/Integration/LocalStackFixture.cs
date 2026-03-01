using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Xunit;

namespace WebVellaErp.Identity.Tests.Integration
{
    /// <summary>
    /// Shared xunit IAsyncLifetime test fixture for LocalStack integration tests.
    /// Provisions real AWS resources (DynamoDB table with single-table design,
    /// Cognito user pool with app client and system role groups, and SNS topics
    /// for domain events) in LocalStack before tests run and cleans them up after.
    ///
    /// Used via IClassFixture&lt;LocalStackFixture&gt; by all integration test classes
    /// in the Identity service. Ensures all tests run against real LocalStack
    /// infrastructure with zero mocked AWS SDK calls.
    ///
    /// Per AAP Section 0.8.4: "All integration and E2E tests MUST execute against
    /// LocalStack. No mocked AWS SDK calls in integration tests. Pattern:
    /// docker compose up -d → test → docker compose down."
    /// </summary>
    public class LocalStackFixture : IAsyncLifetime
    {
        // ──────────────────────────────────────────────────────────────────────
        // Well-known system role GUIDs from source WebVella.Erp/Api/Definitions.cs
        // These are the exact GUIDs used by the legacy monolith for system roles.
        // ──────────────────────────────────────────────────────────────────────
        private const string AdministratorRoleId = "BDC56420-CAF0-4030-8A0E-D264938E0CDA";
        private const string RegularRoleId = "F16EC6DB-626D-4C27-8DE0-3E7CE542C55F";
        private const string GuestRoleId = "987148B1-AFA8-4B33-8616-55861E5FD065";

        /// <summary>
        /// Pre-configured Cognito Identity Provider client pointing to LocalStack.
        /// Used by integration tests for all Cognito user pool operations including
        /// user CRUD, authentication flows, and group management.
        /// </summary>
        public IAmazonCognitoIdentityProvider CognitoClient { get; private set; }

        /// <summary>
        /// Pre-configured DynamoDB client pointing to LocalStack.
        /// Used by integration tests for all DynamoDB table operations including
        /// single-table design CRUD, GSI queries, and user-role relationship management.
        /// </summary>
        public IAmazonDynamoDB DynamoDbClient { get; private set; }

        /// <summary>
        /// Pre-configured SNS client pointing to LocalStack.
        /// Used by integration tests for verifying domain event publishing
        /// on identity.user.* and identity.role.* topics.
        /// </summary>
        public IAmazonSimpleNotificationService SnsClient { get; private set; }

        /// <summary>
        /// The ID of the Cognito user pool created for this test run.
        /// Unique per test run to avoid collisions with parallel test executions.
        /// </summary>
        public string UserPoolId { get; private set; } = string.Empty;

        /// <summary>
        /// The ID of the Cognito app client created for this test run.
        /// Configured with ADMIN_USER_PASSWORD_AUTH, REFRESH_TOKEN_AUTH,
        /// and USER_PASSWORD_AUTH flows enabled.
        /// </summary>
        public string ClientId { get; private set; } = string.Empty;

        /// <summary>
        /// The name of the DynamoDB identity table created for this test run.
        /// Uses a unique name per run (identity-{guid}) to avoid collisions.
        /// Table uses single-table design with PK/SK + GSI1 (email) + GSI2 (username).
        /// </summary>
        public string TableName { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the SNS topic for user domain events.
        /// Publishes: identity.user.created, identity.user.updated, identity.user.deleted
        /// Per AAP Section 0.8.5 event naming convention: {domain}.{entity}.{action}
        /// </summary>
        public string UserEventsTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// ARN of the SNS topic for role domain events.
        /// Publishes: identity.role.created, identity.role.updated, identity.role.deleted
        /// Per AAP Section 0.8.5 event naming convention: {domain}.{entity}.{action}
        /// </summary>
        public string RoleEventsTopicArn { get; private set; } = string.Empty;

        /// <summary>
        /// Indicates whether the Cognito Identity Provider service is available in the
        /// current LocalStack environment. LocalStack Community Edition does not include
        /// Cognito — it requires LocalStack Pro. When Cognito is unavailable, tests that
        /// depend on Cognito operations should be dynamically skipped via
        /// <see cref="SkipIfCognitoUnavailable"/>.
        /// </summary>
        public bool CognitoAvailable { get; private set; } = true;

        /// <summary>
        /// Human-readable reason why Cognito is not available. Empty when Cognito IS available.
        /// Used as the skip reason in dynamically skipped tests.
        /// </summary>
        public string CognitoSkipReason { get; private set; } = string.Empty;

        /// <summary>
        /// Helper method for integration test classes to call at the start of any test
        /// that requires Cognito. Uses the xUnit 2.x dynamic skip mechanism: throwing
        /// an exception whose message begins with <c>$XunitDynamicSkip$</c> causes the
        /// test runner to report the test as Skipped rather than Failed.
        /// See xUnit.Sdk.DynamicSkipToken (internal) — the contract is: any exception
        /// whose message starts with "$XunitDynamicSkip$" will be treated as a skip.
        /// </summary>
        public void SkipIfCognitoUnavailable()
        {
            if (!CognitoAvailable)
            {
                throw new Exception($"$XunitDynamicSkip${CognitoSkipReason}");
            }
        }

        /// <summary>
        /// Constructor configures all AWS SDK clients for LocalStack.
        /// Reads endpoint from AWS_ENDPOINT_URL environment variable
        /// (falls back to http://localhost:4566 which is the standard LocalStack port).
        /// Uses BasicAWSCredentials("test", "test") since LocalStack accepts any credentials.
        /// Region is us-east-1 per AAP Section 0.8.6.
        /// </summary>
        public LocalStackFixture()
        {
            var localStackEndpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL")
                ?? "http://localhost:4566";

            var credentials = new BasicAWSCredentials("test", "test");

            // Configure DynamoDB client for LocalStack
            var dynamoConfig = new AmazonDynamoDBConfig
            {
                ServiceURL = localStackEndpoint,
                AuthenticationRegion = "us-east-1"
            };
            DynamoDbClient = new AmazonDynamoDBClient(credentials, dynamoConfig);

            // Configure Cognito Identity Provider client for LocalStack
            var cognitoConfig = new AmazonCognitoIdentityProviderConfig
            {
                ServiceURL = localStackEndpoint,
                AuthenticationRegion = "us-east-1"
            };
            CognitoClient = new AmazonCognitoIdentityProviderClient(credentials, cognitoConfig);

            // Configure SNS client for LocalStack
            var snsConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = localStackEndpoint,
                AuthenticationRegion = "us-east-1"
            };
            SnsClient = new AmazonSimpleNotificationServiceClient(credentials, snsConfig);

            // Generate unique resource names for this test run to avoid collisions
            var runId = Guid.NewGuid().ToString("N");
            TableName = $"identity-{runId}";
        }

        /// <summary>
        /// Provisions all AWS resources in LocalStack before integration tests run.
        /// Executes sequentially to ensure proper dependency ordering:
        /// 1. DynamoDB table with GSIs (must exist before seeding)
        /// 2. Cognito user pool (must exist before app client and groups)
        /// 3. Cognito app client (depends on user pool)
        /// 4. Cognito groups for system roles (depends on user pool)
        /// 5. SNS topics for domain events
        /// 6. Seed system roles in DynamoDB (depends on table)
        /// </summary>
        public async Task InitializeAsync()
        {
            // Step 1: Create DynamoDB Identity Table with single-table design
            // Table has PK/SK primary key + GSI1 for email lookups + GSI2 for username lookups
            await CreateDynamoDbTableAsync();

            // Steps 2-4: Cognito provisioning — wrapped in try-catch because Cognito
            // is a LocalStack Pro feature and may not be available in the current environment.
            // When Cognito is unavailable, CognitoAvailable is set to false and tests
            // that depend on Cognito will be dynamically skipped via SkipIfCognitoUnavailable().
            try
            {
                // Step 2: Create Cognito User Pool with relaxed password policy for testing
                // Allows simple passwords like "erp" (the system default user password)
                await CreateCognitoUserPoolAsync();

                // Step 3: Create Cognito App Client with required auth flows
                // Enables ADMIN_USER_PASSWORD_AUTH, REFRESH_TOKEN_AUTH, USER_PASSWORD_AUTH
                await CreateCognitoAppClientAsync();

                // Step 4: Create Default Cognito Groups matching system roles
                // Maps to: administrator, regular, guest (from Definitions.cs)
                await CreateCognitoGroupsAsync();
            }
            catch (Amazon.CognitoIdentityProvider.AmazonCognitoIdentityProviderException ex)
            {
                // Cognito is not available — likely running against LocalStack Community Edition
                // which does not include the cognito-idp service. Mark Cognito as unavailable
                // so tests can be dynamically skipped instead of failing.
                CognitoAvailable = false;
                CognitoSkipReason = $"Cognito is not available in the current LocalStack environment: {ex.Message}";
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                // Network-level failure connecting to LocalStack Cognito endpoint
                CognitoAvailable = false;
                CognitoSkipReason = $"Cannot connect to LocalStack Cognito endpoint: {ex.Message}";
            }

            // Step 5: Create SNS Topics for identity domain events
            // Topics: identity-user-events, identity-role-events
            await CreateSnsTopicsAsync();

            // Step 6: Seed System Roles in DynamoDB with well-known GUIDs
            // PK=ROLE#{roleId}, SK=META for each system role
            await SeedSystemRolesAsync();
        }

        /// <summary>
        /// Cleans up all provisioned AWS resources after tests complete.
        /// Each cleanup operation is wrapped in try-catch to handle failures gracefully,
        /// since tests may have already cleaned up individual resources, or resources
        /// may not have been created due to earlier failures.
        /// Disposes all SDK clients to release underlying HTTP connections.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Delete the DynamoDB identity table
            await SafeDeleteDynamoDbTableAsync();

            // Delete the Cognito user pool (cascades to app clients and groups)
            await SafeDeleteCognitoUserPoolAsync();

            // Delete SNS topics
            await SafeDeleteSnsTopicsAsync();

            // Dispose all SDK clients to release HTTP connections and resources
            DisposeSdkClients();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Private helper methods for InitializeAsync
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the DynamoDB identity table with single-table design:
        /// - Primary key: PK (String, HASH) + SK (String, RANGE)
        /// - GSI1: GSI1PK (HASH) + GSI1SK (RANGE) for email-based user lookups
        ///   Replaces: EQL "SELECT * FROM user WHERE email = @email" from SecurityManager.cs
        /// - GSI2: GSI2PK (HASH) + GSI2SK (RANGE) for username-based user lookups
        ///   Replaces: EQL "SELECT * FROM user WHERE username = @username" from SecurityManager.cs
        ///
        /// Both GSIs use ProjectionType.ALL to include all attributes in query results.
        /// Waits for table to become ACTIVE before returning.
        /// </summary>
        private async Task CreateDynamoDbTableAsync()
        {
            var createTableRequest = new CreateTableRequest
            {
                TableName = TableName,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement("PK", KeyType.HASH),
                    new KeySchemaElement("SK", KeyType.RANGE)
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition("PK", ScalarAttributeType.S),
                    new AttributeDefinition("SK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI1PK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI1SK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI2PK", ScalarAttributeType.S),
                    new AttributeDefinition("GSI2SK", ScalarAttributeType.S)
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI1",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("GSI1PK", KeyType.HASH),
                            new KeySchemaElement("GSI1SK", KeyType.RANGE)
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI2",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("GSI2PK", KeyType.HASH),
                            new KeySchemaElement("GSI2SK", KeyType.RANGE)
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                        ProvisionedThroughput = new ProvisionedThroughput(5, 5)
                    }
                },
                ProvisionedThroughput = new ProvisionedThroughput(5, 5),
                BillingMode = BillingMode.PROVISIONED
            };

            await DynamoDbClient.CreateTableAsync(createTableRequest);

            // Poll DescribeTable until the table status transitions to ACTIVE
            await WaitForTableActiveAsync();
        }

        /// <summary>
        /// Polls DescribeTable until the table status is ACTIVE.
        /// LocalStack typically creates tables instantly, but this polling loop
        /// ensures correctness for any environment and prevents race conditions.
        /// Throws TimeoutException if the table does not become ACTIVE within 30 seconds.
        /// </summary>
        private async Task WaitForTableActiveAsync()
        {
            const int maxRetries = 30;
            const int delayMilliseconds = 1000;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var response = await DynamoDbClient.DescribeTableAsync(new DescribeTableRequest
                {
                    TableName = TableName
                });

                if (response.Table.TableStatus == TableStatus.ACTIVE)
                {
                    return;
                }

                await Task.Delay(delayMilliseconds);
            }

            throw new TimeoutException(
                $"DynamoDB table '{TableName}' did not become ACTIVE within {maxRetries} seconds.");
        }

        /// <summary>
        /// Creates a Cognito user pool with relaxed password policy for testing.
        /// Password policy is intentionally relaxed (MinimumLength=6, no complexity
        /// requirements) to allow simple test passwords like "erp" which is the
        /// default system user password per AAP Section 0.7.5.
        ///
        /// Schema attributes:
        /// - email (required, mutable) — primary login identifier
        /// - given_name (mutable) — user's first name
        /// - family_name (mutable) — user's last name
        /// - preferred_username (mutable) — display username
        ///
        /// Configuration:
        /// - AutoVerifiedAttributes: email (auto-verified on creation)
        /// - UsernameAttributes: email (users sign in with email address)
        /// </summary>
        private async Task CreateCognitoUserPoolAsync()
        {
            var createPoolResponse = await CognitoClient.CreateUserPoolAsync(new CreateUserPoolRequest
            {
                PoolName = $"identity-test-pool-{Guid.NewGuid():N}",
                Policies = new UserPoolPolicyType
                {
                    PasswordPolicy = new PasswordPolicyType
                    {
                        MinimumLength = 6,
                        RequireUppercase = false,
                        RequireLowercase = false,
                        RequireNumbers = false,
                        RequireSymbols = false
                    }
                },
                Schema = new List<SchemaAttributeType>
                {
                    new SchemaAttributeType
                    {
                        Name = "email",
                        AttributeDataType = AttributeDataType.String,
                        Required = true,
                        Mutable = true
                    },
                    new SchemaAttributeType
                    {
                        Name = "given_name",
                        AttributeDataType = AttributeDataType.String,
                        Mutable = true
                    },
                    new SchemaAttributeType
                    {
                        Name = "family_name",
                        AttributeDataType = AttributeDataType.String,
                        Mutable = true
                    },
                    new SchemaAttributeType
                    {
                        Name = "preferred_username",
                        AttributeDataType = AttributeDataType.String,
                        Mutable = true
                    }
                },
                AutoVerifiedAttributes = new List<string> { "email" },
                UsernameAttributes = new List<string> { "email" }
            });

            UserPoolId = createPoolResponse.UserPool.Id;
        }

        /// <summary>
        /// Creates a Cognito app client for the user pool.
        /// Enables the following auth flows required by integration tests:
        /// - ALLOW_ADMIN_USER_PASSWORD_AUTH: Required for AdminInitiateAuth calls
        ///   used in authentication flow tests
        /// - ALLOW_REFRESH_TOKEN_AUTH: Required for token refresh tests
        /// - ALLOW_USER_PASSWORD_AUTH: Required for standard user password auth
        ///
        /// GenerateSecret is false to simplify test setup by avoiding SECRET_HASH
        /// computation in every authentication request.
        /// </summary>
        private async Task CreateCognitoAppClientAsync()
        {
            var createClientResponse = await CognitoClient.CreateUserPoolClientAsync(
                new CreateUserPoolClientRequest
                {
                    UserPoolId = UserPoolId,
                    ClientName = "identity-test-client",
                    ExplicitAuthFlows = new List<string>
                    {
                        "ALLOW_ADMIN_USER_PASSWORD_AUTH",
                        "ALLOW_REFRESH_TOKEN_AUTH",
                        "ALLOW_USER_PASSWORD_AUTH"
                    },
                    GenerateSecret = false
                });

            ClientId = createClientResponse.UserPoolClient.ClientId;
        }

        /// <summary>
        /// Creates Cognito groups matching the well-known system roles from
        /// WebVella.Erp/Api/Definitions.cs SystemIds class:
        ///
        /// - "administrator" → AdministratorRoleId = BDC56420-CAF0-4030-8A0E-D264938E0CDA
        /// - "regular"       → RegularRoleId       = F16EC6DB-626D-4C27-8DE0-3E7CE542C55F
        /// - "guest"         → GuestRoleId         = 987148B1-AFA8-4B33-8616-55861E5FD065
        ///
        /// Per AAP Section 0.7.5: "System roles (SystemIds.AdministratorRoleId,
        /// SystemIds.RegularRoleId, SystemIds.GuestRoleId) map to Cognito groups"
        /// </summary>
        private async Task CreateCognitoGroupsAsync()
        {
            var systemRoles = new[]
            {
                (Name: "administrator", RoleId: AdministratorRoleId,
                    Description: "System administrator role with full access"),
                (Name: "regular", RoleId: RegularRoleId,
                    Description: "Regular user role with standard permissions"),
                (Name: "guest", RoleId: GuestRoleId,
                    Description: "Guest role with read-only access")
            };

            foreach (var role in systemRoles)
            {
                await CognitoClient.CreateGroupAsync(new CreateGroupRequest
                {
                    GroupName = role.Name,
                    UserPoolId = UserPoolId,
                    Description = $"{role.Description} (RoleId: {role.RoleId})"
                });
            }
        }

        /// <summary>
        /// Creates SNS topics for identity domain events:
        /// - identity-user-events: Publishes identity.user.created, identity.user.updated,
        ///   identity.user.deleted events when user lifecycle operations occur
        /// - identity-role-events: Publishes identity.role.created, identity.role.updated,
        ///   identity.role.deleted events when role lifecycle operations occur
        ///
        /// Topic names include a unique suffix per test run to avoid collisions.
        /// ARNs are stored in UserEventsTopicArn and RoleEventsTopicArn for
        /// test assertions.
        ///
        /// Per AAP Section 0.8.5: Event naming convention is {domain}.{entity}.{action}
        /// </summary>
        private async Task CreateSnsTopicsAsync()
        {
            var runSuffix = Guid.NewGuid().ToString("N");

            var userEventsResponse = await SnsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = $"identity-user-events-{runSuffix}"
            });
            UserEventsTopicArn = userEventsResponse.TopicArn;

            var roleEventsResponse = await SnsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = $"identity-role-events-{runSuffix}"
            });
            RoleEventsTopicArn = roleEventsResponse.TopicArn;
        }

        /// <summary>
        /// Seeds the three system roles in DynamoDB with well-known GUIDs from
        /// the monolith's Definitions.cs SystemIds class. Each role is stored
        /// following the DynamoDB single-table design pattern:
        ///
        /// - PK = ROLE#{roleGuid}  (partition key identifying the role entity)
        /// - SK = META             (sort key indicating this is role metadata)
        /// - EntityType = ROLE_META (discriminator for scan filtering)
        /// - id = roleGuid         (the role's unique identifier)
        /// - name = role name      (human-readable role name)
        /// - description = text    (role description)
        ///
        /// System roles seeded:
        /// - Administrator: BDC56420-CAF0-4030-8A0E-D264938E0CDA
        /// - Regular:       F16EC6DB-626D-4C27-8DE0-3E7CE542C55F
        /// - Guest:         987148B1-AFA8-4B33-8616-55861E5FD065
        /// </summary>
        private async Task SeedSystemRolesAsync()
        {
            var systemRoles = new[]
            {
                (RoleId: AdministratorRoleId, Name: "administrator",
                    Description: "System administrator role with full access"),
                (RoleId: RegularRoleId, Name: "regular",
                    Description: "Regular user role with standard permissions"),
                (RoleId: GuestRoleId, Name: "guest",
                    Description: "Guest role with read-only access")
            };

            foreach (var role in systemRoles)
            {
                await DynamoDbClient.PutItemAsync(new PutItemRequest
                {
                    TableName = TableName,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["PK"] = new AttributeValue { S = $"ROLE#{role.RoleId}" },
                        ["SK"] = new AttributeValue { S = "META" },
                        ["EntityType"] = new AttributeValue { S = "ROLE_META" },
                        ["id"] = new AttributeValue { S = role.RoleId },
                        ["name"] = new AttributeValue { S = role.Name },
                        ["description"] = new AttributeValue { S = role.Description }
                    }
                });
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Private helper methods for DisposeAsync (safe cleanup)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Safely deletes the DynamoDB identity table.
        /// Catches and ignores all exceptions since the table may have already
        /// been deleted by a test or may not have been created due to earlier failures.
        /// </summary>
        private async Task SafeDeleteDynamoDbTableAsync()
        {
            if (string.IsNullOrEmpty(TableName))
            {
                return;
            }

            try
            {
                await DynamoDbClient.DeleteTableAsync(new DeleteTableRequest
                {
                    TableName = TableName
                });
            }
            catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException)
            {
                // Table was already deleted or never created; this is expected and safe to ignore
            }
            catch (Exception)
            {
                // Catch all other exceptions during cleanup to prevent masking test failures
            }
        }

        /// <summary>
        /// Safely deletes the Cognito user pool, which cascades to delete
        /// all app clients and groups within the pool.
        /// Catches and ignores all exceptions since the pool may have already
        /// been deleted or may not have been created.
        /// </summary>
        private async Task SafeDeleteCognitoUserPoolAsync()
        {
            if (string.IsNullOrEmpty(UserPoolId))
            {
                return;
            }

            try
            {
                await CognitoClient.DeleteUserPoolAsync(new DeleteUserPoolRequest
                {
                    UserPoolId = UserPoolId
                });
            }
            catch (Amazon.CognitoIdentityProvider.Model.ResourceNotFoundException)
            {
                // User pool was already deleted or never created; safe to ignore
            }
            catch (Exception)
            {
                // Catch all other exceptions during cleanup to prevent masking test failures
            }
        }

        /// <summary>
        /// Safely deletes both SNS topics (user events and role events).
        /// Each deletion is independently try-caught so failure of one
        /// does not prevent cleanup of the other.
        /// </summary>
        private async Task SafeDeleteSnsTopicsAsync()
        {
            if (!string.IsNullOrEmpty(UserEventsTopicArn))
            {
                try
                {
                    await SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = UserEventsTopicArn
                    });
                }
                catch (Exception)
                {
                    // Topic may have already been deleted; safe to ignore
                }
            }

            if (!string.IsNullOrEmpty(RoleEventsTopicArn))
            {
                try
                {
                    await SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = RoleEventsTopicArn
                    });
                }
                catch (Exception)
                {
                    // Topic may have already been deleted; safe to ignore
                }
            }
        }

        /// <summary>
        /// Disposes all AWS SDK clients to release underlying HTTP connections
        /// and other unmanaged resources. Each client is checked for IDisposable
        /// implementation before disposal.
        /// </summary>
        private void DisposeSdkClients()
        {
            if (DynamoDbClient is IDisposable dynamoDisposable)
            {
                dynamoDisposable.Dispose();
            }

            if (CognitoClient is IDisposable cognitoDisposable)
            {
                cognitoDisposable.Dispose();
            }

            if (SnsClient is IDisposable snsDisposable)
            {
                snsDisposable.Dispose();
            }
        }
    }
}
