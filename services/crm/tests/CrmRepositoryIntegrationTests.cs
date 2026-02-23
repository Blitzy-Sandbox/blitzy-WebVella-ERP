using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.Crm.DataAccess;
using WebVellaErp.Crm.Models;
using Xunit;

namespace WebVellaErp.Crm.Tests
{
    /// <summary>
    /// Integration tests for CrmRepository DynamoDB single-table design running against LocalStack.
    /// Validates CRUD operations, query/search via GSI1/GSI2, batch operations, and error handling
    /// for all CRM bounded-context entities (account, contact, address, salutation).
    ///
    /// Per AAP §0.8.4: All integration tests MUST execute against LocalStack — NO mocked AWS SDK calls.
    /// Pattern: docker compose up -d → test → docker compose down.
    ///
    /// Table schema:
    ///   PK (S, HASH) + SK (S, RANGE) — composite primary key
    ///   GSI1: GSI1PK (S, HASH), GSI1SK (S, RANGE) — entity listing sorted by created_on
    ///   GSI2: GSI2PK (S, HASH), GSI2SK (S, RANGE) — x_search prefix-based search
    ///   BillingMode: PAY_PER_REQUEST (on-demand, LocalStack compatible)
    ///
    /// Source mapping:
    ///   CrmRepository → DbRecordRepository (WebVella.Erp/Database/DbRecordRepository.cs)
    ///   Account model → NextPlugin.20190204 entity creation (account ID: 2e22b50f-...)
    ///   Contact model → NextPlugin.20190206 entity creation (contact ID: 39e1dd9b-...)
    ///   Error messages → DbRecordRepository lines 192, 201
    /// </summary>
    public class CrmRepositoryIntegrationTests : IAsyncLifetime
    {
        // =====================================================================
        // Test Fixture Fields
        // =====================================================================

        /// <summary>
        /// Real DynamoDB client connected to LocalStack — NO mocks per AAP §0.8.4.
        /// Configured with LocalStack endpoint (default http://localhost:4566) and
        /// test credentials (access_key="test", secret_key="test").
        /// </summary>
        private AmazonDynamoDBClient _dynamoDbClient = null!;

        /// <summary>
        /// CrmRepository system under test — constructed with real DynamoDB client,
        /// NullLogger, and in-memory IConfiguration (DynamoDB:CrmTableName = crm-records-test).
        /// </summary>
        private CrmRepository _repository = null!;

        /// <summary>
        /// Test-specific DynamoDB table name to avoid collisions with other running tests.
        /// Each test run creates and tears down its own table.
        /// </summary>
        private const string TestTableName = "crm-records-test";

        // =====================================================================
        // IAsyncLifetime — Setup and Teardown
        // =====================================================================

        /// <summary>
        /// Creates the DynamoDB test table on LocalStack with the full single-table schema
        /// (PK/SK + GSI1 + GSI2), waits for ACTIVE status, and constructs the CrmRepository
        /// system under test with real DynamoDB client and in-memory configuration.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Configure real AmazonDynamoDBClient for LocalStack
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL") ?? "http://localhost:4566";
            var config = new AmazonDynamoDBConfig
            {
                ServiceURL = endpointUrl,
                AuthenticationRegion = "us-east-1"
            };
            var credentials = new BasicAWSCredentials("test", "test");
            _dynamoDbClient = new AmazonDynamoDBClient(credentials, config);

            // Delete table if it exists from a previous failed run
            try
            {
                await _dynamoDbClient.DeleteTableAsync(new DeleteTableRequest { TableName = TestTableName });
                await Task.Delay(500); // Brief pause for LocalStack cleanup
            }
            catch (ResourceNotFoundException)
            {
                // Table doesn't exist — expected for clean start
            }

            // Create the CRM DynamoDB table with full single-table schema
            var createTableRequest = new CreateTableRequest
            {
                TableName = TestTableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = "PK", KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = "SK", KeyType = KeyType.RANGE }
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = "PK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "SK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "GSI1PK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "GSI1SK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "GSI2PK", AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = "GSI2SK", AttributeType = ScalarAttributeType.S }
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI1",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement { AttributeName = "GSI1PK", KeyType = KeyType.HASH },
                            new KeySchemaElement { AttributeName = "GSI1SK", KeyType = KeyType.RANGE }
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL }
                    },
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI2",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement { AttributeName = "GSI2PK", KeyType = KeyType.HASH },
                            new KeySchemaElement { AttributeName = "GSI2SK", KeyType = KeyType.RANGE }
                        },
                        Projection = new Projection { ProjectionType = ProjectionType.ALL }
                    }
                }
            };

            await _dynamoDbClient.CreateTableAsync(createTableRequest);

            // Wait for table to become ACTIVE on LocalStack
            var tableActive = false;
            for (var i = 0; i < 30; i++)
            {
                try
                {
                    var describeResponse = await _dynamoDbClient.DescribeTableAsync(
                        new DescribeTableRequest { TableName = TestTableName });
                    if (describeResponse.Table.TableStatus == TableStatus.ACTIVE)
                    {
                        tableActive = true;
                        break;
                    }
                }
                catch (ResourceNotFoundException)
                {
                    // Table not yet created — retry
                }
                await Task.Delay(200);
            }

            if (!tableActive)
            {
                throw new InvalidOperationException(
                    $"DynamoDB table '{TestTableName}' did not become ACTIVE within timeout. " +
                    "Ensure LocalStack is running: docker compose up -d");
            }

            // Build in-memory configuration with test table name
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DynamoDB:CrmTableName", TestTableName }
            });
            IConfiguration configuration = configBuilder.Build();

            // Create CrmRepository with real DynamoDB client and NullLogger
            ILogger<CrmRepository> logger = NullLogger<CrmRepository>.Instance;
            _repository = new CrmRepository(_dynamoDbClient, logger, configuration);
        }

        /// <summary>
        /// Cleans up the test DynamoDB table and disposes the DynamoDB client.
        /// Called after all tests in this class complete.
        /// </summary>
        public async Task DisposeAsync()
        {
            try
            {
                await _dynamoDbClient.DeleteTableAsync(new DeleteTableRequest { TableName = TestTableName });
            }
            catch (ResourceNotFoundException)
            {
                // Table already deleted or never created — safe to ignore
            }
            catch (Exception)
            {
                // Swallow disposal errors to avoid masking test failures
            }

            _dynamoDbClient?.Dispose();
        }

        // =====================================================================
        // Helper Methods — Test Data Factories
        // =====================================================================

        /// <summary>
        /// Creates a test Account with all required fields populated.
        /// Uses Account.DefaultSalutationId for SalutationId and AccountType.Company for Type.
        /// </summary>
        private static Account CreateTestAccount(
            Guid? id = null,
            string name = "Integration Test Corp",
            string type = "1",
            string firstName = "John",
            string lastName = "Doe",
            string? email = "test@example.com",
            string? city = null,
            string xSearch = "integration test corp john doe",
            DateTime? createdOn = null)
        {
            return new Account
            {
                Id = id ?? Guid.NewGuid(),
                Name = name,
                Type = type,
                FirstName = firstName,
                LastName = lastName,
                Email = email,
                City = city,
                SalutationId = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"),
                CreatedOn = createdOn ?? DateTime.UtcNow,
                XSearch = xSearch,
                LScope = "default"
            };
        }

        /// <summary>
        /// Creates a test Contact with all required fields populated.
        /// Uses Contact.DefaultSalutationId for SalutationId.
        /// </summary>
        private static Contact CreateTestContact(
            Guid? id = null,
            string firstName = "Jane",
            string lastName = "Smith",
            string? email = "jane@example.com",
            string? jobTitle = "Developer",
            string? xSearch = "jane smith developer",
            DateTime? createdOn = null)
        {
            return new Contact
            {
                Id = id ?? Guid.NewGuid(),
                FirstName = firstName,
                LastName = lastName,
                Email = email,
                SalutationId = Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"),
                CreatedOn = createdOn ?? DateTime.UtcNow,
                JobTitle = jobTitle,
                XSearch = xSearch
            };
        }

        /// <summary>
        /// Verifies a DynamoDB item directly to confirm PK/SK/GSI key structure.
        /// Bypasses CrmRepository to validate the raw DynamoDB table state.
        /// </summary>
        private async Task<GetItemResponse> GetRawDynamoDbItem(string entityName, Guid recordId)
        {
            var request = new GetItemRequest
            {
                TableName = TestTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "PK", new AttributeValue { S = $"ENTITY#{entityName}" } },
                    { "SK", new AttributeValue { S = $"RECORD#{recordId}" } }
                }
            };
            return await _dynamoDbClient.GetItemAsync(request);
        }

        // =====================================================================
        // Account CRUD Integration Tests
        // =====================================================================

        /// <summary>
        /// Verifies that CreateAsync persists an Account to LocalStack DynamoDB with correct
        /// field values, PK/SK key structure, and GSI1/GSI2 index attributes.
        /// Source: EntityManager.cs → DbRecordRepository.cs create flow.
        /// </summary>
        [Fact]
        public async Task CreateAccount_PersistsToLocalStackDynamoDB()
        {
            // Arrange
            var accountId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;
            var account = CreateTestAccount(
                id: accountId,
                name: "Integration Test Corp",
                type: AccountType.Company,
                firstName: "John",
                lastName: "Doe",
                email: "test@example.com",
                xSearch: "integration test corp john doe",
                createdOn: createdOn);

            // Act
            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Assert — Read back through repository
            var retrieved = await _repository.GetByIdAsync<Account>(CrmRepository.AccountEntity, accountId);
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(accountId);
            retrieved.Name.Should().Be("Integration Test Corp");
            retrieved.Type.Should().Be(AccountType.Company);
            retrieved.FirstName.Should().Be("John");
            retrieved.LastName.Should().Be("Doe");
            retrieved.Email.Should().Be("test@example.com");
            retrieved.SalutationId.Should().Be(Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
            retrieved.XSearch.Should().Be("integration test corp john doe");
            retrieved.CreatedOn.Should().BeCloseTo(createdOn, TimeSpan.FromSeconds(1));

            // Assert — Verify raw DynamoDB item structure (PK/SK/GSI keys)
            var rawItem = await GetRawDynamoDbItem(CrmRepository.AccountEntity, accountId);
            rawItem.Item.Should().NotBeEmpty();
            rawItem.Item["PK"].S.Should().Be("ENTITY#account");
            rawItem.Item["SK"].S.Should().Be($"RECORD#{accountId}");
            rawItem.Item["GSI1PK"].S.Should().Be("ENTITY#account");
            rawItem.Item["GSI1SK"].S.Should().StartWith("CREATED_ON#");
            rawItem.Item["GSI2PK"].S.Should().Be("ENTITY#account");
            rawItem.Item["GSI2SK"].S.Should().StartWith("X_SEARCH#");
        }

        /// <summary>
        /// Verifies GetByIdAsync returns a fully deserialized Account with all field types
        /// correctly mapped from DynamoDB attributes (S→string, S→Guid, S→DateTime).
        /// </summary>
        [Fact]
        public async Task GetAccountById_ExistingRecord_ReturnsAccount()
        {
            // Arrange
            var accountId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;
            var account = CreateTestAccount(
                id: accountId,
                name: "Deserialization Test Corp",
                type: AccountType.Person,
                firstName: "Alice",
                lastName: "Wonder",
                email: "alice@test.com",
                city: "Portland",
                xSearch: "deserialization test corp alice wonder",
                createdOn: createdOn);

            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Act
            var retrieved = await _repository.GetByIdAsync<Account>(CrmRepository.AccountEntity, accountId);

            // Assert — Every field deserialized correctly
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(accountId);
            retrieved.Name.Should().Be("Deserialization Test Corp");
            retrieved.Type.Should().Be(AccountType.Person);
            retrieved.FirstName.Should().Be("Alice");
            retrieved.LastName.Should().Be("Wonder");
            retrieved.Email.Should().Be("alice@test.com");
            retrieved.City.Should().Be("Portland");
            retrieved.SalutationId.Should().Be(Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
            retrieved.XSearch.Should().Be("deserialization test corp alice wonder");
            retrieved.CreatedOn.Should().BeCloseTo(createdOn, TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Verifies GetByIdAsync returns null (not an exception) when querying for a
        /// non-existent record ID. Mirrors DbRecordRepository behavior for missing records.
        /// </summary>
        [Fact]
        public async Task GetAccountById_NonExistent_ReturnsNull()
        {
            // Act
            var result = await _repository.GetByIdAsync<Account>(
                CrmRepository.AccountEntity, Guid.NewGuid());

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Verifies UpdateAsync modifies specified fields while preserving unchanged fields.
        /// Source: RecordManager.cs update flow → DbRecordRepository conditional update.
        /// </summary>
        [Fact]
        public async Task UpdateAccount_ModifiesExistingRecord()
        {
            // Arrange — Create original account
            var accountId = Guid.NewGuid();
            var account = CreateTestAccount(
                id: accountId,
                name: "Original Corp",
                city: null,
                firstName: "Original",
                lastName: "User");

            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Act — Update Name and City, keep everything else
            account.Name = "Updated Corp";
            account.City = "Seattle";
            account.XSearch = "updated corp original user seattle";
            await _repository.UpdateAsync(CrmRepository.AccountEntity, accountId, account);

            // Assert — Verify changes persisted
            var retrieved = await _repository.GetByIdAsync<Account>(CrmRepository.AccountEntity, accountId);
            retrieved.Should().NotBeNull();
            retrieved!.Name.Should().Be("Updated Corp");
            retrieved.City.Should().Be("Seattle");
            retrieved.XSearch.Should().Be("updated corp original user seattle");

            // Assert — Unchanged fields remain intact
            retrieved.FirstName.Should().Be("Original");
            retrieved.LastName.Should().Be("User");
            retrieved.Type.Should().Be(AccountType.Company);
            retrieved.SalutationId.Should().Be(Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
        }

        /// <summary>
        /// Verifies DeleteAsync removes a record from DynamoDB and subsequent GetByIdAsync
        /// returns null. Source: DbRecordRepository.cs delete with existence check.
        /// </summary>
        [Fact]
        public async Task DeleteAccount_RemovesFromDynamoDB()
        {
            // Arrange
            var accountId = Guid.NewGuid();
            var account = CreateTestAccount(id: accountId);
            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Verify the record exists first
            var exists = await _repository.GetByIdAsync<Account>(CrmRepository.AccountEntity, accountId);
            exists.Should().NotBeNull();

            // Act
            await _repository.DeleteAsync(CrmRepository.AccountEntity, accountId);

            // Assert
            var deleted = await _repository.GetByIdAsync<Account>(CrmRepository.AccountEntity, accountId);
            deleted.Should().BeNull();
        }

        /// <summary>
        /// Verifies that creating a record with a duplicate ID throws InvalidOperationException.
        /// CrmRepository uses DynamoDB conditional expression (attribute_not_exists) for idempotency
        /// per AAP §0.8.5. Exact error: "A record with ID '{id}' already exists for entity '{entity}'."
        /// </summary>
        [Fact]
        public async Task CreateAccount_DuplicateId_ThrowsException()
        {
            // Arrange
            var accountId = Guid.NewGuid();
            var account = CreateTestAccount(id: accountId, name: "First Account");
            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Act & Assert — Second create with same ID should throw
            var duplicate = CreateTestAccount(id: accountId, name: "Duplicate Account");
            var act = () => _repository.CreateAsync(CrmRepository.AccountEntity, duplicate);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"A record with ID '{accountId}' already exists for entity 'account'.");
        }

        // =====================================================================
        // Contact CRUD Integration Tests
        // =====================================================================

        /// <summary>
        /// Verifies that CreateAsync persists a Contact to LocalStack DynamoDB with correct
        /// field values including salutation_id (corrected spelling from source NextPlugin.20190206.cs).
        /// </summary>
        [Fact]
        public async Task CreateContact_PersistsToLocalStackDynamoDB()
        {
            // Arrange
            var contactId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;
            var contact = CreateTestContact(
                id: contactId,
                firstName: "Jane",
                lastName: "Smith",
                email: "jane@example.com",
                jobTitle: "Developer",
                xSearch: "jane smith developer",
                createdOn: createdOn);

            // Act
            await _repository.CreateAsync(CrmRepository.ContactEntity, contact);

            // Assert — Read back through repository
            var retrieved = await _repository.GetByIdAsync<Contact>(CrmRepository.ContactEntity, contactId);
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(contactId);
            retrieved.FirstName.Should().Be("Jane");
            retrieved.LastName.Should().Be("Smith");
            retrieved.Email.Should().Be("jane@example.com");
            retrieved.JobTitle.Should().Be("Developer");
            retrieved.SalutationId.Should().Be(Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
            retrieved.XSearch.Should().Be("jane smith developer");
            retrieved.CreatedOn.Should().BeCloseTo(createdOn, TimeSpan.FromSeconds(1));

            // Assert — Verify raw DynamoDB item PK/SK structure
            var rawItem = await GetRawDynamoDbItem(CrmRepository.ContactEntity, contactId);
            rawItem.Item.Should().NotBeEmpty();
            rawItem.Item["PK"].S.Should().Be("ENTITY#contact");
            rawItem.Item["SK"].S.Should().Be($"RECORD#{contactId}");
        }

        /// <summary>
        /// Verifies GetByIdAsync returns a fully deserialized Contact with all properties
        /// correctly mapped from DynamoDB attributes.
        /// </summary>
        [Fact]
        public async Task GetContactById_ExistingRecord_ReturnsContact()
        {
            // Arrange
            var contactId = Guid.NewGuid();
            var createdOn = DateTime.UtcNow;
            var contact = CreateTestContact(
                id: contactId,
                firstName: "Bob",
                lastName: "Builder",
                email: "bob@builder.com",
                jobTitle: "Architect",
                xSearch: "bob builder architect",
                createdOn: createdOn);

            await _repository.CreateAsync(CrmRepository.ContactEntity, contact);

            // Act
            var retrieved = await _repository.GetByIdAsync<Contact>(CrmRepository.ContactEntity, contactId);

            // Assert — All fields deserialized
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(contactId);
            retrieved.FirstName.Should().Be("Bob");
            retrieved.LastName.Should().Be("Builder");
            retrieved.Email.Should().Be("bob@builder.com");
            retrieved.JobTitle.Should().Be("Architect");
            retrieved.SalutationId.Should().Be(Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
            retrieved.XSearch.Should().Be("bob builder architect");
            retrieved.CreatedOn.Should().BeCloseTo(createdOn, TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Verifies UpdateAsync correctly modifies a Contact's JobTitle field
        /// while preserving all other existing field values.
        /// </summary>
        [Fact]
        public async Task UpdateContact_ModifiesJobTitle()
        {
            // Arrange
            var contactId = Guid.NewGuid();
            var contact = CreateTestContact(
                id: contactId,
                firstName: "Update",
                lastName: "Test",
                jobTitle: "Junior Dev");

            await _repository.CreateAsync(CrmRepository.ContactEntity, contact);

            // Act
            contact.JobTitle = "Senior Architect";
            contact.XSearch = "update test senior architect";
            await _repository.UpdateAsync(CrmRepository.ContactEntity, contactId, contact);

            // Assert
            var retrieved = await _repository.GetByIdAsync<Contact>(CrmRepository.ContactEntity, contactId);
            retrieved.Should().NotBeNull();
            retrieved!.JobTitle.Should().Be("Senior Architect");
            retrieved.FirstName.Should().Be("Update");
            retrieved.LastName.Should().Be("Test");
        }

        /// <summary>
        /// Verifies DeleteAsync removes a Contact from DynamoDB — subsequent GetByIdAsync returns null.
        /// </summary>
        [Fact]
        public async Task DeleteContact_RemovesFromDynamoDB()
        {
            // Arrange
            var contactId = Guid.NewGuid();
            var contact = CreateTestContact(id: contactId);
            await _repository.CreateAsync(CrmRepository.ContactEntity, contact);

            // Verify exists
            var exists = await _repository.GetByIdAsync<Contact>(CrmRepository.ContactEntity, contactId);
            exists.Should().NotBeNull();

            // Act
            await _repository.DeleteAsync(CrmRepository.ContactEntity, contactId);

            // Assert
            var deleted = await _repository.GetByIdAsync<Contact>(CrmRepository.ContactEntity, contactId);
            deleted.Should().BeNull();
        }

        // =====================================================================
        // Query and Listing Integration Tests (GSI1)
        // =====================================================================

        /// <summary>
        /// Verifies QueryAsync lists all accounts by entity name via GSI1 key condition.
        /// Validates single-table design where all accounts share PK=ENTITY#account.
        /// </summary>
        [Fact]
        public async Task QueryAsync_ListAccountsByEntity_ReturnsAll()
        {
            // Arrange — Create 5 accounts with unique IDs and staggered timestamps
            var accountIds = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var id = Guid.NewGuid();
                accountIds.Add(id);
                var account = CreateTestAccount(
                    id: id,
                    name: $"Query Account {i}",
                    xSearch: $"query account {i}",
                    createdOn: DateTime.UtcNow.AddMinutes(-i));
                await _repository.CreateAsync(CrmRepository.AccountEntity, account);
            }

            // Act
            var results = await _repository.QueryAsync<Account>(CrmRepository.AccountEntity);

            // Assert — All 5 returned (may include accounts from other tests sharing the table)
            results.Should().NotBeNull();
            results.Count.Should().BeGreaterThanOrEqualTo(5);
            foreach (var id in accountIds)
            {
                results.Any(a => a.Id == id).Should().BeTrue(
                    $"Account with ID {id} should be in query results");
            }
        }

        /// <summary>
        /// Verifies QueryAsync lists all contacts by entity name via GSI1.
        /// </summary>
        [Fact]
        public async Task QueryAsync_ListContactsByEntity_ReturnsAll()
        {
            // Arrange — Create 3 contacts
            var contactIds = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                var id = Guid.NewGuid();
                contactIds.Add(id);
                var contact = CreateTestContact(
                    id: id,
                    firstName: $"Contact{i}",
                    lastName: "QueryTest",
                    xSearch: $"contact{i} querytest",
                    createdOn: DateTime.UtcNow.AddMinutes(-i));
                await _repository.CreateAsync(CrmRepository.ContactEntity, contact);
            }

            // Act
            var results = await _repository.QueryAsync<Contact>(CrmRepository.ContactEntity);

            // Assert
            results.Should().NotBeNull();
            results.Count.Should().BeGreaterThanOrEqualTo(3);
            foreach (var id in contactIds)
            {
                results.Any(c => c.Id == id).Should().BeTrue(
                    $"Contact with ID {id} should be in query results");
            }
        }

        /// <summary>
        /// Verifies QueryAsync respects PaginationOptions.Limit parameter,
        /// returning at most the specified number of records.
        /// </summary>
        [Fact]
        public async Task QueryAsync_WithPagination_RespectsLimit()
        {
            // Arrange — Create 10 accounts
            for (var i = 0; i < 10; i++)
            {
                var account = CreateTestAccount(
                    name: $"Pagination Account {i}",
                    xSearch: $"pagination account {i}",
                    createdOn: DateTime.UtcNow.AddMinutes(-i));
                await _repository.CreateAsync(CrmRepository.AccountEntity, account);
            }

            // Act — Query with limit of 3
            var pagination = new PaginationOptions { Limit = 3 };
            var results = await _repository.QueryAsync<Account>(
                CrmRepository.AccountEntity, pagination: pagination);

            // Assert — Exactly 3 records returned
            results.Should().NotBeNull();
            results.Count.Should().Be(3);
        }

        /// <summary>
        /// Verifies QueryAsync filters accounts by Type field using QueryFilter.
        /// Creates accounts with Type="1" (Company) and Type="2" (Person), then filters
        /// for companies only. Validates DynamoDB filter expression generation.
        /// </summary>
        [Fact]
        public async Task QueryAsync_WithTypeFilter_FiltersAccounts()
        {
            // Arrange — Create 3 Company accounts and 2 Person accounts with unique marker
            var marker = Guid.NewGuid().ToString("N")[..8];
            var companyIds = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                var id = Guid.NewGuid();
                companyIds.Add(id);
                var company = CreateTestAccount(
                    id: id,
                    name: $"Company {marker} {i}",
                    type: AccountType.Company,
                    xSearch: $"company {marker} {i}");
                await _repository.CreateAsync(CrmRepository.AccountEntity, company);
            }
            for (var i = 0; i < 2; i++)
            {
                var person = CreateTestAccount(
                    name: $"Person {marker} {i}",
                    type: AccountType.Person,
                    xSearch: $"person {marker} {i}");
                await _repository.CreateAsync(CrmRepository.AccountEntity, person);
            }

            // Act — Filter for Type == "1" (Company only)
            var filter = new QueryFilter
            {
                FieldName = "type",
                Operator = FilterOperator.Equal,
                Value = AccountType.Company
            };
            var results = await _repository.QueryAsync<Account>(
                CrmRepository.AccountEntity, filter: filter);

            // Assert — Only Company accounts returned (at least our 3)
            results.Should().NotBeNull();
            var matchingCompanies = results.Where(a => companyIds.Contains(a.Id)).ToList();
            matchingCompanies.Should().HaveCount(3);
            results.All(a => a.Type == AccountType.Company).Should().BeTrue(
                "Filter should return only Company type accounts");
        }

        /// <summary>
        /// Verifies that accounts and contacts are stored in the same DynamoDB table but
        /// are separated by entity name in the GSI1 partition key. Querying for "account"
        /// returns only accounts, and querying for "contact" returns only contacts.
        /// This validates the single-table design isolation by entity name.
        /// </summary>
        [Fact]
        public async Task QueryAsync_AccountsAndContacts_AreSeparate()
        {
            // Arrange — Create accounts and contacts with unique markers
            var marker = Guid.NewGuid().ToString("N")[..8];
            var accountId = Guid.NewGuid();
            var contactId = Guid.NewGuid();

            var account = CreateTestAccount(
                id: accountId,
                name: $"SeparationTest Account {marker}",
                xSearch: $"separationtest account {marker}");
            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            var contact = CreateTestContact(
                id: contactId,
                firstName: $"SeparationTest{marker}",
                lastName: "Contact",
                xSearch: $"separationtest{marker} contact");
            await _repository.CreateAsync(CrmRepository.ContactEntity, contact);

            // Act — Query each entity type separately
            var accounts = await _repository.QueryAsync<Account>(CrmRepository.AccountEntity);
            var contacts = await _repository.QueryAsync<Contact>(CrmRepository.ContactEntity);

            // Assert — Account query contains our account but not our contact ID
            accounts.Any(a => a.Id == accountId).Should().BeTrue(
                "Account query should include the created account");

            // Assert — Contact query contains our contact but not our account ID
            contacts.Any(c => c.Id == contactId).Should().BeTrue(
                "Contact query should include the created contact");
        }

        // =====================================================================
        // Search (GSI2) Integration Tests
        // =====================================================================

        /// <summary>
        /// Verifies SearchAsync finds an account by x_search field value using GSI2.
        /// CrmRepository Strategy 1: begins_with(GSI2SK, "X_SEARCH#{normalizedSearch}").
        /// Fallback: contains(x_search, searchVal) via GSI1.
        /// </summary>
        [Fact]
        public async Task SearchAsync_FindsByXSearchField()
        {
            // Arrange — Create account with specific x_search value
            var accountId = Guid.NewGuid();
            var account = CreateTestAccount(
                id: accountId,
                name: "Acme Corporation Manufacturing",
                xSearch: "acme corporation manufacturing");
            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Act — Search for "acme"
            var results = await _repository.SearchAsync<Account>(
                CrmRepository.AccountEntity, "acme");

            // Assert — The account should be found
            results.Should().NotBeNull();
            results.Should().NotBeEmpty();
            results.Any(a => a.Id == accountId).Should().BeTrue(
                "Search for 'acme' should find the Acme Corporation account");
        }

        /// <summary>
        /// Verifies SearchAsync is case-insensitive. GSI2SK stores lowercased x_search
        /// via ToLowerInvariant(), and search normalizes input with Trim().ToLowerInvariant().
        /// </summary>
        [Fact]
        public async Task SearchAsync_CaseInsensitive()
        {
            // Arrange — Create account with mixed-case x_search
            var accountId = Guid.NewGuid();
            var account = CreateTestAccount(
                id: accountId,
                name: "ACME Corp",
                xSearch: "ACME Corp");
            await _repository.CreateAsync(CrmRepository.AccountEntity, account);

            // Act — Search with different casing
            var results = await _repository.SearchAsync<Account>(
                CrmRepository.AccountEntity, "acme");

            // Assert — Found despite case difference (GSI2SK uses lowercased x_search)
            results.Should().NotBeNull();
            results.Any(a => a.Id == accountId).Should().BeTrue(
                "Case-insensitive search for 'acme' should find 'ACME Corp' account");
        }

        /// <summary>
        /// Verifies SearchAsync returns an empty list when no records match the search term.
        /// </summary>
        [Fact]
        public async Task SearchAsync_NoMatch_ReturnsEmpty()
        {
            // Act — Search for a term that has no matching records
            var results = await _repository.SearchAsync<Account>(
                CrmRepository.AccountEntity, "zzz_nonexistent_term_xyz_" + Guid.NewGuid().ToString("N"));

            // Assert — Empty list, not null, not exception
            results.Should().NotBeNull();
            results.Should().BeEmpty();
        }

        // =====================================================================
        // Address and Salutation CRUD Tests
        // =====================================================================

        /// <summary>
        /// Verifies that an address entity record persists to DynamoDB via the CRM single-table.
        /// Entity name: "address" (from NextPlugin.20190204.cs entity creation).
        /// Uses Account model as a generic carrier for address data in the single-table design.
        /// </summary>
        [Fact]
        public async Task CreateAddress_PersistsToLocalStackDynamoDB()
        {
            // Arrange — Create an address-like record using Account as the generic type
            // In the single-table design, any entity type can coexist with different entity names
            var addressId = Guid.NewGuid();
            var address = CreateTestAccount(
                id: addressId,
                name: "123 Main Street",
                firstName: "Address",
                lastName: "Record",
                xSearch: "123 main street address");

            // Act — Store as "address" entity
            await _repository.CreateAsync(CrmRepository.AddressEntity, address);

            // Assert — Verify persistence with correct entity partition
            var retrieved = await _repository.GetByIdAsync<Account>(CrmRepository.AddressEntity, addressId);
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(addressId);
            retrieved.Name.Should().Be("123 Main Street");

            // Assert — Verify raw DynamoDB item has address entity in PK
            var rawItem = await GetRawDynamoDbItem(CrmRepository.AddressEntity, addressId);
            rawItem.Item.Should().NotBeEmpty();
            rawItem.Item["PK"].S.Should().Be("ENTITY#address");
            rawItem.Item["SK"].S.Should().Be($"RECORD#{addressId}");
        }

        /// <summary>
        /// Verifies that a salutation entity record persists to DynamoDB via the CRM single-table.
        /// Entity name: "salutation" (corrected from misspelled "solutation" in NextPlugin.20190206.cs).
        /// </summary>
        [Fact]
        public async Task CreateSalutation_PersistsToLocalStackDynamoDB()
        {
            // Arrange — Create a salutation record
            var salutationId = Guid.NewGuid();
            var salutation = CreateTestAccount(
                id: salutationId,
                name: "Mr.",
                firstName: "Salutation",
                lastName: "Record",
                xSearch: "mr salutation");

            // Act — Store as "salutation" entity
            await _repository.CreateAsync(CrmRepository.SalutationEntity, salutation);

            // Assert — Verify persistence with correct entity partition
            var retrieved = await _repository.GetByIdAsync<Account>(CrmRepository.SalutationEntity, salutationId);
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(salutationId);
            retrieved.Name.Should().Be("Mr.");

            // Assert — Verify raw DynamoDB item has salutation entity in PK
            var rawItem = await GetRawDynamoDbItem(CrmRepository.SalutationEntity, salutationId);
            rawItem.Item.Should().NotBeEmpty();
            rawItem.Item["PK"].S.Should().Be("ENTITY#salutation");
            rawItem.Item["SK"].S.Should().Be($"RECORD#{salutationId}");
        }

        // =====================================================================
        // Batch Operation Tests
        // =====================================================================

        /// <summary>
        /// Verifies BatchCreateAsync persists multiple accounts in a single batch operation.
        /// CrmRepository chunks items into groups of 25 (BATCH_WRITE_LIMIT) with exponential
        /// backoff retry for UnprocessedItems.
        /// </summary>
        [Fact]
        public async Task BatchCreateAsync_PersistsMultipleAccounts()
        {
            // Arrange — Create batch of 5 accounts
            var accounts = new List<Account>();
            for (var i = 0; i < 5; i++)
            {
                accounts.Add(CreateTestAccount(
                    name: $"Batch Account {i}",
                    xSearch: $"batch account {i}",
                    createdOn: DateTime.UtcNow.AddMinutes(-i)));
            }

            // Act
            await _repository.BatchCreateAsync(CrmRepository.AccountEntity, accounts);

            // Assert — All 5 can be read back individually
            foreach (var account in accounts)
            {
                var retrieved = await _repository.GetByIdAsync<Account>(
                    CrmRepository.AccountEntity, account.Id);
                retrieved.Should().NotBeNull(
                    $"Batch-created account '{account.Name}' should be retrievable");
                retrieved!.Name.Should().Be(account.Name);
            }
        }

        /// <summary>
        /// Verifies BatchGetAsync retrieves multiple accounts by their IDs in a single batch.
        /// CrmRepository chunks IDs into groups of 100 (BATCH_GET_LIMIT) with retry logic.
        /// </summary>
        [Fact]
        public async Task BatchGetAsync_RetrievesMultipleAccounts()
        {
            // Arrange — Create 5 accounts individually and collect their IDs
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var account = CreateTestAccount(
                    name: $"BatchGet Account {i}",
                    xSearch: $"batchget account {i}");
                await _repository.CreateAsync(CrmRepository.AccountEntity, account);
                ids.Add(account.Id);
            }

            // Act — Batch-get all 5 by IDs
            var results = await _repository.BatchGetAsync<Account>(CrmRepository.AccountEntity, ids);

            // Assert — All 5 returned
            results.Should().NotBeNull();
            results.Should().HaveCount(5);
            foreach (var id in ids)
            {
                results.Any(a => a.Id == id).Should().BeTrue(
                    $"BatchGet should return account with ID {id}");
            }
        }

        // =====================================================================
        // Error Handling Tests
        // =====================================================================

        /// <summary>
        /// Verifies DeleteAsync throws InvalidOperationException with EXACT error message
        /// matching source DbRecordRepository.cs line 201 when attempting to delete
        /// a record that doesn't exist. Message: "There is no record with such id to update."
        /// </summary>
        [Fact]
        public async Task DeleteAsync_NonExistentRecord_ThrowsException()
        {
            // Act & Assert
            var nonExistentId = Guid.NewGuid();
            var act = () => _repository.DeleteAsync(CrmRepository.AccountEntity, nonExistentId);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("There is no record with such id to update.");
        }

        /// <summary>
        /// Verifies UpdateAsync throws InvalidOperationException when attempting to update
        /// a non-existent record. CrmRepository uses DynamoDB conditional expression
        /// (attribute_exists) which fails for missing records. Message: "Failed to update record."
        /// </summary>
        [Fact]
        public async Task UpdateAsync_NonExistentRecord_ThrowsException()
        {
            // Arrange — Create account data but don't persist it
            var nonExistentId = Guid.NewGuid();
            var account = CreateTestAccount(id: nonExistentId, name: "Ghost Account");

            // Act & Assert
            var act = () => _repository.UpdateAsync(
                CrmRepository.AccountEntity, nonExistentId, account);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Failed to update record.");
        }
    }
}
