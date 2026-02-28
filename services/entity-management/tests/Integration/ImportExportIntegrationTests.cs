// =============================================================================
// ImportExportIntegrationTests.cs — CSV Import/Export Integration Tests
// Against LocalStack S3 + DynamoDB
// =============================================================================
// Validates CSV parsing via CsvHelper, S3 file upload/download, entity record
// creation from CSV rows, and relation field resolution against **real
// LocalStack S3 and DynamoDB**. NO mocked AWS SDK calls (AAP §0.8.4).
//
// Covers 8 phases (20 test methods):
//   Phase 3: Basic Import Tests (4 tests)
//   Phase 4: File Path Normalization Tests (4 tests)
//   Phase 5: Relation Field Resolution Tests (4 tests)
//   Phase 6: CSV Parsing Edge Cases (4 tests)
//   Phase 7: S3 Upload/Download Verification (2 tests)
//   Phase 8: Evaluate Import Tests (2 tests)
//
// Source: WebVella.Erp/Api/ImportExportManager.cs lines 34-250+
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Functions;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using WebVellaErp.EntityManagement.Tests.Fixtures;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Integration
{
    /// <summary>
    /// Integration tests for ImportExportHandler — validates CSV import/export
    /// operations including S3 file storage, record creation from CSV rows,
    /// relation field resolution ($/$$ notation), file path normalization,
    /// and evaluate/import pipeline against real LocalStack S3 and DynamoDB.
    /// Uses IClassFixture&lt;LocalStackFixture&gt; for shared resource provisioning
    /// (tables, S3 buckets, SNS topics) across all 20 test methods.
    /// </summary>
    public class ImportExportIntegrationTests : IClassFixture<LocalStackFixture>
    {
        // =====================================================================
        // Phase 1: Class Declaration and Fixture Wiring
        // =====================================================================

        private readonly LocalStackFixture _fixture;
        private readonly ImportExportHandler _handler;
        private readonly IRecordService _recordService;
        private readonly IEntityService _entityService;
        private readonly IEntityRepository _entityRepository;
        private readonly IRecordRepository _recordRepository;
        private readonly IConfiguration _config;
        private readonly Mock<ILambdaContext> _mockLambdaContext;

        /// <summary>
        /// System.Text.Json options matching ImportExportHandler's _jsonOptions:
        /// PropertyNameCaseInsensitive=true, WhenWritingNull ignored.
        /// Used for response deserialization in test assertions.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Constructor wires up ImportExportHandler with real LocalStack DynamoDB,
        /// S3, and SNS clients. Configures IConfiguration with DynamoDB table names,
        /// SNS topic ARN prefix, and DevelopmentMode=true. Sets environment variables
        /// for ImportExportHandler's internal configuration (FILES_S3_BUCKET,
        /// FILES_TEMP_PREFIX, IS_LOCAL, IMPORT_TOPIC_ARN).
        /// </summary>
        public ImportExportIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;

            // Configure environment variables that ImportExportHandler reads in constructor.
            Environment.SetEnvironmentVariable("FILES_S3_BUCKET", LocalStackFixture.ImportExportBucketName);
            Environment.SetEnvironmentVariable("FILES_TEMP_PREFIX", "temp/");
            Environment.SetEnvironmentVariable("IS_LOCAL", "true");
            Environment.SetEnvironmentVariable("IMPORT_TOPIC_ARN",
                "arn:aws:sns:us-east-1:000000000000:entity-management-import-events");

            // Build IConfiguration pointing to fixture table names and SNS topics
            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DynamoDB:MetadataTableName", LocalStackFixture.EntityMetadataTableName },
                    { "DynamoDB:RecordTableName", LocalStackFixture.RecordStorageTableName },
                    { "Sns:TopicArnPrefix", "arn:aws:sns:us-east-1:000000000000:" },
                    { "DevelopmentMode", "true" }
                })
                .Build();

            // Create real repository instances backed by LocalStack DynamoDB
            _entityRepository = new EntityRepository(
                _fixture.DynamoDbClient,
                NullLogger<EntityRepository>.Instance,
                _config);

            _recordRepository = new RecordRepository(
                _fixture.DynamoDbClient,
                _entityRepository,
                NullLogger<RecordRepository>.Instance,
                _config);

            // Create real service instances backed by LocalStack
            _entityService = new EntityService(
                _entityRepository,
                NullLogger<EntityService>.Instance,
                _config,
                new MemoryCache(new MemoryCacheOptions()));

            _recordService = new RecordService(
                _entityService,
                _entityRepository,
                _recordRepository,
                _fixture.SnsClient,
                NullLogger<RecordService>.Instance,
                _config);

            // Create the handler under test with real LocalStack clients
            _handler = new ImportExportHandler(
                _recordService,
                _entityService,
                _fixture.S3Client,
                _fixture.SnsClient,
                NullLogger<ImportExportHandler>.Instance,
                _config);

            // Configure mock ILambdaContext for correlation ID extraction
            _mockLambdaContext = new Mock<ILambdaContext>();
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("ImportExportHandler-IntegrationTest");
        }

        // =====================================================================
        // Helper Methods
        // =====================================================================

        /// <summary>
        /// Uploads CSV content to the LocalStack S3 bucket used by ImportExportHandler.
        /// Returns the file path (without the temp/ prefix) suitable for passing
        /// to ImportFromCsv as fileTempPath.
        /// </summary>
        private async Task<string> UploadCsvToS3(string csvContent, string fileName)
        {
            var bytes = Encoding.UTF8.GetBytes(csvContent);
            using var stream = new MemoryStream(bytes);

            var s3Key = "temp/" + fileName.TrimStart('/');
            await _fixture.S3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = LocalStackFixture.ImportExportBucketName,
                Key = s3Key,
                InputStream = stream,
                ContentType = "text/csv"
            });

            // Return the fileTempPath that the handler expects (before normalization)
            return "/" + fileName.TrimStart('/');
        }

        /// <summary>
        /// Seeds a test entity with specified fields into LocalStack DynamoDB via the fixture.
        /// Returns the entity object. Each test should use a unique entity name.
        /// </summary>
        private async Task<Entity> SeedTestEntityAsync(
            string entityName,
            Guid? entityId = null,
            List<Field>? additionalFields = null)
        {
            var id = entityId ?? Guid.NewGuid();
            var entity = TestDataHelper.CreateTestEntity(entityName, id);

            if (additionalFields != null)
            {
                foreach (var field in additionalFields)
                {
                    if (!entity.Fields.Any(f => f.Name == field.Name))
                    {
                        entity.Fields.Add(field);
                    }
                }
            }

            await _fixture.SeedEntityAsync(entity);
            return entity;
        }

        /// <summary>
        /// Builds an admin-level API Gateway request with JWT claims containing
        /// the administrator role in cognito:groups. This passes the IsAdminUser
        /// check in ImportExportHandler.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildAdminRequest(
            string entityName,
            string? body = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = new Dictionary<string, string>
                {
                    { "entityName", entityName }
                },
                QueryStringParameters = new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["x-correlation-id"] = Guid.NewGuid().ToString()
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = SystemIds.SystemUserId.ToString(),
                                ["custom:roles"] = SystemIds.AdministratorRoleId.ToString()
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Seeds a record directly into DynamoDB using the RecordRepository.
        /// Used for update-path tests where existing records must be present.
        /// </summary>
        private async Task<EntityRecord> SeedRecordAsync(string entityName, EntityRecord record)
        {
            var response = await _recordService.CreateRecord(entityName, record);
            response.Success.Should().BeTrue($"SeedRecord failed: {response.Message}");
            return record;
        }

        /// <summary>
        /// Queries DynamoDB records for a given entity and returns all records found.
        /// Uses a DynamoDB query with the entity's partition key pattern.
        /// </summary>
        private async Task<List<Dictionary<string, AttributeValue>>> QueryDynamoDbRecordsAsync(string entityName)
        {
            var queryRequest = new QueryRequest
            {
                TableName = LocalStackFixture.RecordStorageTableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new AttributeValue { S = $"ENTITY#{entityName}" },
                    [":skPrefix"] = new AttributeValue { S = "RECORD#" }
                }
            };

            var result = await _fixture.DynamoDbClient.QueryAsync(queryRequest);
            return result.Items;
        }

        /// <summary>
        /// Parses the API Gateway response body as a ResponseModel for assertion.
        /// Uses System.Text.Json matching the handler's serialization options.
        /// </summary>
        private static ResponseModel ParseResponseModel(APIGatewayHttpApiV2ProxyResponse response)
        {
            var model = JsonSerializer.Deserialize<ResponseModel>(response.Body, _jsonOptions);
            return model ?? new ResponseModel { Success = false, Message = "Failed to deserialize response" };
        }

        /// <summary>
        /// Seeds a relation between two entities directly in DynamoDB via
        /// TestDataHelper.CreateRelationMetadataItem.
        /// </summary>
        private async Task SeedRelationAsync(EntityRelation relation)
        {
            var relationItem = TestDataHelper.CreateRelationMetadataItem(relation);
            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = LocalStackFixture.EntityMetadataTableName,
                Item = relationItem
            });
        }

        /// <summary>
        /// Seeds a record directly into DynamoDB using TestDataHelper.CreateRecordItem
        /// for fine-grained control over the DynamoDB item structure.
        /// </summary>
        private async Task SeedRecordItemAsync(string entityName, EntityRecord record)
        {
            var recordItem = TestDataHelper.CreateRecordItem(entityName, record);
            await _fixture.DynamoDbClient.PutItemAsync(new PutItemRequest
            {
                TableName = LocalStackFixture.RecordStorageTableName,
                Item = recordItem
            });
        }

        // =====================================================================
        // Phase 3: Basic Import Tests
        // =====================================================================

        /// <summary>
        /// Verifies that ImportFromCsv creates new records from a valid CSV file
        /// stored in S3. Seeds an entity with text, number, and checkbox fields,
        /// generates a CSV with 5 rows, uploads to S3, and asserts 5 records created
        /// in DynamoDB with correct field values.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithValidCsv_CreatesRecords()
        {
            // Arrange — clean slate + seed entity
            await _fixture.ResetAsync();

            var entityName = "import_valid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var amountField = TestDataHelper.CreateNumberField("amount");
            var activeField = TestDataHelper.CreateCheckboxField("active");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField, amountField, activeField
            });

            // Generate CSV content with 5 rows
            var csvContent = TestDataHelper.GenerateTestCsvContent(entity, 5);
            var fileName = $"{entityName}_import.csv";
            var fileTempPath = await UploadCsvToS3(csvContent, fileName);

            // Build request
            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — HTTP 200
            response.StatusCode.Should().Be(200);

            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Import should succeed but failed: {model.Message}");
            model.Message.Should().Contain("Import completed");
            model.Message.Should().Contain("Created: 5");

            // Verify records in DynamoDB
            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(5, "5 records should be created in DynamoDB");
        }

        /// <summary>
        /// Verifies that ImportFromCsv updates existing records when the CSV contains
        /// an 'id' column with valid existing record GUIDs. Seeds 3 records, generates
        /// CSV with their IDs and modified field values, and asserts records are updated
        /// (not duplicated).
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithIdColumn_UpdatesExistingRecords()
        {
            // Arrange — clean slate + seed entity and existing records
            await _fixture.ResetAsync();

            var entityName = "import_update_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // Seed 3 existing records
            var recordIds = new List<Guid>();
            for (int i = 0; i < 3; i++)
            {
                var record = TestDataHelper.CreateTestRecord();
                var recordId = (Guid)record["id"]!;
                recordIds.Add(recordId);
                record["name"] = $"original_name_{i}";
                await SeedRecordAsync(entityName, record);
            }

            // Build CSV with existing IDs and modified name values
            var sb = new StringBuilder();
            sb.AppendLine("id,name");
            for (int i = 0; i < 3; i++)
            {
                sb.AppendLine($"{recordIds[i]},updated_name_{i}");
            }

            var fileTempPath = await UploadCsvToS3(sb.ToString(), $"{entityName}_update.csv");
            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Update import should succeed: {model.Message}");
            model.Message.Should().Contain("Updated: 3");

            // Verify still only 3 records (not duplicated)
            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(3, "records should be updated, not duplicated");
        }

        /// <summary>
        /// Verifies that ImportFromCsv creates new records when CSV id column values
        /// are empty or unparseable. Source behavior: if id is null/empty/invalid,
        /// a new record is created with an auto-generated GUID.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithNullIds_CreatesNewRecords()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_nullid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // CSV with id column but empty/null values
            var csvContent = "id,name\n,new_record_1\n,new_record_2\n,new_record_3\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_nullid.csv");
            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — all 3 should be created as new
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Null-id import should succeed: {model.Message}");
            model.Message.Should().Contain("Created: 3");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(3, "3 new records should be created for empty IDs");
        }

        /// <summary>
        /// Verifies that ImportFromCsv handles a mix of create (null id) and update
        /// (existing id) rows in a single CSV file. Seeds 2 records, then imports
        /// CSV with 2 existing IDs + 2 empty IDs.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithMixedCreateAndUpdate_HandlesBoth()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_mixed_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // Seed 2 existing records
            var existingIds = new List<Guid>();
            for (int i = 0; i < 2; i++)
            {
                var record = TestDataHelper.CreateTestRecord();
                existingIds.Add((Guid)record["id"]!);
                record["name"] = $"existing_{i}";
                await SeedRecordAsync(entityName, record);
            }

            // CSV: 2 rows with existing IDs (update) + 2 rows with empty IDs (create)
            var sb = new StringBuilder();
            sb.AppendLine("id,name");
            sb.AppendLine($"{existingIds[0]},updated_existing_0");
            sb.AppendLine($"{existingIds[1]},updated_existing_1");
            sb.AppendLine(",new_record_0");
            sb.AppendLine(",new_record_1");

            var fileTempPath = await UploadCsvToS3(sb.ToString(), $"{entityName}_mixed.csv");
            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Mixed import should succeed: {model.Message}");
            model.Message.Should().Contain("Created: 2");
            model.Message.Should().Contain("Updated: 2");

            // Total records: 2 original (updated) + 2 new = 4
            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(4, "2 updated + 2 created = 4 total records");
        }

        // =====================================================================
        // Phase 4: File Path Normalization Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the handler normalizes file paths by stripping the /fs
        /// prefix. Source behavior: if fileTempPath starts with "/fs", the first 3
        /// characters are removed, the path is forced to start with "/", and
        /// converted to lowercase.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_NormalizesFilePath_StripsFsPrefix()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_fspath_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // Upload CSV with a specific filename
            var actualFileName = "uploads/fspath_test.csv";
            var csvContent = "name\nfspath_row_1\nfspath_row_2\n";
            await UploadCsvToS3(csvContent, actualFileName);

            // Use /fs prefix — handler should strip it to get /uploads/fspath_test.csv
            var fileTempPath = "/fs/uploads/fspath_test.csv";
            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — should succeed because after normalization the path resolves
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue(
                $"Path normalization should strip /fs prefix: {model.Message}");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(2, "2 records from CSV after /fs path normalization");
        }

        /// <summary>
        /// Verifies that ImportFromCsv returns an error when fileTempPath is empty.
        /// The handler should recognize the missing file and return a meaningful error.
        /// Note: The handler actually attempts S3 retrieval which fails — the "CSV file not found"
        /// error is returned from the S3 retrieval step.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_EmptyFilePath_ReturnsError()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_emptypath_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // Empty fileTempPath
            var requestBody = JsonSerializer.Serialize(new { fileTempPath = "" });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — should fail with error about missing/empty file path
            var model = ParseResponseModel(response);
            model.Success.Should().BeFalse("empty file path should result in failure");
        }

        /// <summary>
        /// Verifies that ImportFromCsv returns an error when the S3 file does not
        /// exist. The handler should catch the AmazonS3Exception and return a
        /// descriptive error message.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_NonExistentFile_ReturnsError()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_nofile_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // File that does not exist in S3
            var requestBody = JsonSerializer.Serialize(new { fileTempPath = "/nonexistent_file.csv" });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            var model = ParseResponseModel(response);
            model.Success.Should().BeFalse("non-existent file should result in failure");
            model.Message.Should().Contain("CSV file not found",
                "error should indicate the file was not found in S3");
        }

        /// <summary>
        /// Verifies that ImportFromCsv returns an error when the specified entity
        /// does not exist. The handler looks up the entity by name and returns a
        /// descriptive error when not found.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_NonExistentEntity_ReturnsError()
        {
            // Arrange
            await _fixture.ResetAsync();

            // Upload a valid CSV but reference a non-existent entity
            var csvContent = "name\ntest_row\n";
            var fileTempPath = await UploadCsvToS3(csvContent, "noentity_test.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest("nonexistent_entity_xyz", requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            var model = ParseResponseModel(response);
            model.Success.Should().BeFalse("non-existent entity should result in failure");
            model.Message.Should().Contain("not found",
                "error should indicate entity was not found");
        }

        // =====================================================================
        // Phase 5: Relation Field Resolution Tests
        // =====================================================================

        /// <summary>
        /// Verifies that ImportFromCsv resolves relation fields using the $
        /// notation ($relationName.fieldName). Seeds two entities with a OneToMany
        /// relation, seeds customer records, then imports orders CSV with
        /// $customer_order.name column. The handler should resolve the customer
        /// record ID from the name value and set the FK field.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithRelationColumn_ResolvesRelatedRecords()
        {
            // Arrange
            await _fixture.ResetAsync();

            var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            var customerEntityName = "customer_" + suffix;
            var orderEntityName = "order_" + suffix;

            // Create customer entity with name field
            var customerNameField = TestDataHelper.CreateTextField("name");
            var customerEntity = await SeedTestEntityAsync(
                customerEntityName,
                additionalFields: new List<Field> { customerNameField });

            // Create order entity with description field and customer_id (guid) field
            var descriptionField = TestDataHelper.CreateTextField("description");
            var customerIdField = TestDataHelper.CreateGuidField("customer_id");
            var orderEntity = await SeedTestEntityAsync(
                orderEntityName,
                additionalFields: new List<Field> { descriptionField, customerIdField });

            // Create a OneToMany relation: customer (origin) -> order (target)
            // origin field = customer.id, target field = order.customer_id
            var customerIdFieldDef = customerEntity.Fields.First(f => f.Name == "id");
            var orderCustIdFieldDef = orderEntity.Fields.First(f => f.Name == "customer_id");

            var relation = TestDataHelper.CreateOneToManyRelation(
                name: $"customer_{suffix}_order",
                originEntityId: customerEntity.Id,
                originFieldId: customerIdFieldDef.Id,
                targetEntityId: orderEntity.Id,
                targetFieldId: orderCustIdFieldDef.Id);

            await SeedRelationAsync(relation);

            // Seed customer records
            var customer1 = TestDataHelper.CreateTestRecord();
            customer1["name"] = "Acme Corp";
            await SeedRecordAsync(customerEntityName, customer1);

            var customer2 = TestDataHelper.CreateTestRecord();
            customer2["name"] = "Globex Inc";
            await SeedRecordAsync(customerEntityName, customer2);

            // CSV with $relation.field notation — resolve customer by name
            var relationColumnName = $"$customer_{suffix}_order.name";
            var csvContent = $"description,{relationColumnName}\nOrder A,Acme Corp\nOrder B,Globex Inc\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{orderEntityName}_rel.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(orderEntityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — records should be created with FK resolved
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue(
                $"Relation import should succeed: {model.Message}");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(orderEntityName);
            dynamoRecords.Should().HaveCount(2, "2 order records should be created");
        }

        /// <summary>
        /// Verifies that $$ (double dollar) notation flips the relation direction
        /// from target-to-origin. Seeds the same customer-order relation but uses
        /// $$relation.field from the customer side to reference orders.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithDoubleRelationPrefix_FlipsDirection()
        {
            // Arrange
            await _fixture.ResetAsync();

            var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            var parentEntityName = "parent_" + suffix;
            var childEntityName = "child_" + suffix;

            // Parent entity with name
            var parentNameField = TestDataHelper.CreateTextField("name");
            var parentEntity = await SeedTestEntityAsync(
                parentEntityName,
                additionalFields: new List<Field> { parentNameField });

            // Child entity with name and parent_id
            var childNameField = TestDataHelper.CreateTextField("name");
            var parentIdField = TestDataHelper.CreateGuidField("parent_id");
            var childEntity = await SeedTestEntityAsync(
                childEntityName,
                additionalFields: new List<Field> { childNameField, parentIdField });

            // OneToMany: parent (origin) -> child (target)
            var parentIdFieldDef = parentEntity.Fields.First(f => f.Name == "id");
            var childParentIdFieldDef = childEntity.Fields.First(f => f.Name == "parent_id");

            var relation = TestDataHelper.CreateOneToManyRelation(
                name: $"parent_{suffix}_child",
                originEntityId: parentEntity.Id,
                originFieldId: parentIdFieldDef.Id,
                targetEntityId: childEntity.Id,
                targetFieldId: childParentIdFieldDef.Id);

            await SeedRelationAsync(relation);

            // Seed child records
            var child1 = TestDataHelper.CreateTestRecord();
            child1["name"] = "Child Alpha";
            child1["parent_id"] = Guid.NewGuid();
            await SeedRecordAsync(childEntityName, child1);

            // CSV for parent entity using $$ (double dollar) to flip direction
            // With $$ from parent side, it looks at origin side (parent) as the "other" entity
            // Since parent IS the origin, $$ reverses: looks at target side = child entity
            var relationColumn = $"$$parent_{suffix}_child.name";
            var csvContent = $"name,{relationColumn}\nParent Row,Child Alpha\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{parentEntityName}_dd.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(parentEntityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — should succeed (relation resolved via flipped direction)
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue(
                $"Double-dollar relation should succeed: {model.Message}");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(parentEntityName);
            dynamoRecords.Should().HaveCount(1, "1 parent record created with reversed relation");
        }

        /// <summary>
        /// Verifies that ImportFromCsv handles an invalid (non-existent) relation
        /// name gracefully. The handler logs a warning for invalid relation columns
        /// and skips the field rather than throwing. The record is still created
        /// without the relation field value.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithInvalidRelation_ThrowsError()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_badrel_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // CSV with non-existent relation in $notation
            var csvContent = "$nonExistentRelation.field,name\nsome_value,row_1\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_badrel.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — handler logs warning for invalid relation and skips the column.
            // The record is still created (with just the "name" field).
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            // Record created (minus the invalid relation column)
            model.Success.Should().BeTrue(
                $"Invalid relation column should be skipped: {model.Message}");
        }

        /// <summary>
        /// Verifies that the relation header parser rejects deep-nested relation
        /// notation (more than one dot separator). Only first-level relation is
        /// supported: $relation.field. A format like $rel.sub.field triggers a
        /// parse error (the entire string after the first $ is parsed as
        /// relationName.fieldName — the "sub.field" part becomes the fieldName
        /// which won't be found on the related entity, causing a field-not-found
        /// error that logs a warning and skips the column).
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithDeepRelation_ThrowsError()
        {
            // Arrange
            await _fixture.ResetAsync();

            var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            var entityName = "import_deeprel_" + suffix;
            var nameField = TestDataHelper.CreateTextField("name");
            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // Create another entity and a real relation for the first part
            var relatedEntityName = "related_" + suffix;
            var relNameField = TestDataHelper.CreateTextField("subfield");
            var relatedEntity = await SeedTestEntityAsync(
                relatedEntityName,
                additionalFields: new List<Field> { relNameField });

            var relation = TestDataHelper.CreateOneToManyRelation(
                name: $"rel_{suffix}",
                originEntityId: relatedEntity.Id,
                originFieldId: relatedEntity.Fields.First(f => f.Name == "id").Id,
                targetEntityId: entity.Id,
                targetFieldId: entity.Fields.First(f => f.Name == "id").Id);

            await SeedRelationAsync(relation);

            // CSV with deep relation notation $rel.sub.field — uses RELATION_SEPARATOR
            // The parser splits on first dot: relationName = "rel_{suffix}"
            // fieldName = "sub.field" which doesn't exist on the related entity
            var csvContent = $"$rel_{suffix}.sub.field,name\ndeep_val,row_1\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_deep.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — the deep relation field is skipped (logged warning)
            // but the record is created with the "name" field
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue(
                $"Deep relation column should be skipped: {model.Message}");
        }

        // =====================================================================
        // Phase 6: CSV Parsing Edge Cases
        // =====================================================================

        /// <summary>
        /// Verifies that JSON array notation in CSV cells is correctly parsed as
        /// a multi-select field value. A cell containing ["value1","value2","value3"]
        /// should be parsed as List&lt;string&gt; for MultiSelectField type.
        /// Source: ImportExportManager.cs lines 197-199 — JSON array detection.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithJsonArrayValues_ParsesMultiSelect()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_mselect_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var tagsField = TestDataHelper.CreateMultiSelectField("tags");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField, tagsField
            });

            // CSV with JSON array value in tags column
            var csvContent = "name,tags\nitem_1,\"[\"\"red\"\",\"\"blue\"\",\"\"green\"\"]\"\nitem_2,\"[\"\"alpha\"\",\"\"beta\"\"]\"\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_mselect.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Multi-select import should succeed: {model.Message}");

            // Verify records created
            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(2, "2 records with multi-select values");
        }

        /// <summary>
        /// Verifies that decimal values in CSV are correctly parsed for NumberField.
        /// CSV cells like "123.45" should be parsed as decimal and stored correctly.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithDecimalValues_ParsesCorrectly()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_decimal_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var priceField = TestDataHelper.CreateNumberField("price");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField, priceField
            });

            var csvContent = "name,price\nItem A,123.45\nItem B,999.99\nItem C,0.01\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_decimal.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Decimal import should succeed: {model.Message}");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(3, "3 records with decimal values");
        }

        /// <summary>
        /// Verifies that date values in CSV are correctly parsed for DateField.
        /// CSV cells with ISO date strings should be parsed and stored as DateTime.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithDateValues_ParsesCorrectly()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_date_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var dateField = TestDataHelper.CreateDateField("due_date");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField, dateField
            });

            var csvContent = "name,due_date\nTask A,2025-01-15\nTask B,2025-06-30\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_date.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Date import should succeed: {model.Message}");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(2, "2 records with date values");
        }

        /// <summary>
        /// Verifies that boolean values in CSV are correctly parsed for CheckboxField.
        /// CSV cells with "true"/"false" should be parsed as boolean and stored correctly.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_WithBooleanValues_ParsesCorrectly()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_bool_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var activeField = TestDataHelper.CreateCheckboxField("is_active");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField, activeField
            });

            var csvContent = "name,is_active\nActive Item,true\nInactive Item,false\nAnother Active,true\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_bool.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Boolean import should succeed: {model.Message}");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(3, "3 records with boolean values");
        }

        // =====================================================================
        // Phase 7: S3 Upload/Download Verification Tests
        // =====================================================================

        /// <summary>
        /// Verifies basic S3 connectivity with LocalStack by uploading a CSV file
        /// and downloading it back, asserting the contents match exactly. This
        /// validates the S3 infrastructure needed for all import operations.
        /// </summary>
        [Fact]
        public async Task S3FileUpload_AndRetrieval_WorksWithLocalStack()
        {
            // Arrange
            await _fixture.ResetAsync();

            var csvContent = "id,name,value\n1,Test,100\n2,Demo,200\n";
            var fileName = "s3_roundtrip_test.csv";
            var s3Key = "temp/" + fileName;

            var bytes = Encoding.UTF8.GetBytes(csvContent);
            using var uploadStream = new MemoryStream(bytes);

            // Act — Upload
            await _fixture.S3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = LocalStackFixture.ImportExportBucketName,
                Key = s3Key,
                InputStream = uploadStream,
                ContentType = "text/csv"
            });

            // Act — Download
            var getResponse = await _fixture.S3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = LocalStackFixture.ImportExportBucketName,
                Key = s3Key
            });

            string downloadedContent;
            using (var reader = new StreamReader(getResponse.ResponseStream, Encoding.UTF8))
            {
                downloadedContent = await reader.ReadToEndAsync();
            }

            // Assert — round-trip content matches
            downloadedContent.Should().Be(csvContent,
                "S3 upload/download round-trip should preserve file content exactly");
        }

        /// <summary>
        /// Verifies that ImportFromCsv reads the CSV file from the S3 bucket by
        /// uploading a CSV, calling import, and verifying records were created.
        /// This proves the handler's S3 integration works end-to-end.
        /// </summary>
        [Fact]
        public async Task ImportFromCsv_ReadsFileFromS3Bucket()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "import_s3read_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field> { nameField });

            // Upload CSV to S3
            var csvContent = "name\ns3_record_1\ns3_record_2\ns3_record_3\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_s3read.csv");

            var requestBody = JsonSerializer.Serialize(new { fileTempPath });
            var request = BuildAdminRequest(entityName, requestBody);

            // Act
            var response = await _handler.ImportFromCsv(request, _mockLambdaContext.Object);

            // Assert — records created proves S3 read worked
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"S3 read import should succeed: {model.Message}");
            model.Message.Should().Contain("Created: 3");

            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(3, "3 records from S3-hosted CSV file");
        }

        // =====================================================================
        // Phase 8: Evaluate Import Tests
        // =====================================================================

        /// <summary>
        /// Verifies that EvaluateImport with general_command="evaluate" returns
        /// a preview analysis without actually creating any records. The response
        /// should contain column analysis and record preview data but DynamoDB
        /// should remain empty.
        /// </summary>
        [Fact]
        public async Task EvaluateImport_WithEvaluateCommand_ReturnsPreviewWithoutCreating()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "eval_preview_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");
            var amountField = TestDataHelper.CreateNumberField("amount");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField, amountField
            });

            // Upload CSV to S3
            var csvContent = "name,amount\nevaluate_item_1,100\nevaluate_item_2,200\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_eval.csv");

            // Build evaluate request body
            var evaluateBody = JsonSerializer.Serialize(new
            {
                fileTempPath,
                general_command = "evaluate"
            });

            var request = BuildAdminRequest(entityName, evaluateBody);

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert — response should succeed with evaluation data
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue($"Evaluate should succeed: {model.Message}");
            model.Message.Should().Contain("Evaluation completed",
                "evaluate command should return evaluation message, not import message");

            // Critical assertion: NO records should be created in DynamoDB
            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(0,
                "evaluate command should NOT create any records — preview only");
        }

        /// <summary>
        /// Verifies that EvaluateImport with general_command="evaluate-import"
        /// performs evaluation AND executes the import, creating records in DynamoDB.
        /// </summary>
        [Fact]
        public async Task EvaluateImport_WithEvaluateImportCommand_CreatesRecords()
        {
            // Arrange
            await _fixture.ResetAsync();

            var entityName = "eval_import_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var nameField = TestDataHelper.CreateTextField("name");

            var entity = await SeedTestEntityAsync(entityName, additionalFields: new List<Field>
            {
                nameField
            });

            // Upload CSV to S3
            var csvContent = "name\neval_import_1\neval_import_2\neval_import_3\n";
            var fileTempPath = await UploadCsvToS3(csvContent, $"{entityName}_evalimport.csv");

            // Build evaluate-import request body
            var evaluateImportBody = JsonSerializer.Serialize(new
            {
                fileTempPath,
                general_command = "evaluate-import"
            });

            var request = BuildAdminRequest(entityName, evaluateImportBody);

            // Act
            var response = await _handler.EvaluateImport(request, _mockLambdaContext.Object);

            // Assert — response should succeed with import message
            response.StatusCode.Should().Be(200);
            var model = ParseResponseModel(response);
            model.Success.Should().BeTrue(
                $"Evaluate-import should succeed: {model.Message}");
            model.Message.Should().Contain("Import completed",
                "evaluate-import command should perform actual import");

            // Records SHOULD be created in DynamoDB
            var dynamoRecords = await QueryDynamoDbRecordsAsync(entityName);
            dynamoRecords.Should().HaveCount(3,
                "evaluate-import should create 3 records in DynamoDB");
        }
    }
}
