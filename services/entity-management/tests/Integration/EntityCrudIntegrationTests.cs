// =============================================================================
// EntityCrudIntegrationTests.cs — Full Entity Lifecycle Integration Tests
// =============================================================================
// Tests entity metadata CRUD (create/read/update/delete), field CRUD (all 21
// field types), relation CRUD (OneToMany, ManyToMany, immutability), and cache
// invalidation against real LocalStack DynamoDB — NO mocked AWS SDK calls.
//
// DynamoDB single-table design keys:
//   PK = ENTITY#{entityId}  SK = META                      → Entity metadata
//   PK = ENTITY#{entityId}  SK = FIELD#{fieldId}            → Field metadata
//   PK = RELATION#{relId}   SK = META                      → Relation metadata
//   PK = RELATION#{relId}   SK = M2M#{originId}#{targetId} → M2M association
//   GSI1PK = ENTITY_NAME#{name}                            → Name-based lookups
//   GSI2PK = RELATION#{relId}                              → Relation lookups
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using WebVellaErp.EntityManagement.Tests.Fixtures;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Integration
{
    /// <summary>
    /// Integration test suite validating the full entity metadata lifecycle
    /// (create/read/update/delete) plus field CRUD, relation CRUD, and cache
    /// invalidation against real LocalStack DynamoDB tables.
    /// </summary>
    [Collection("Integration")]
    public class EntityCrudIntegrationTests : IClassFixture<LocalStackFixture>
    {
        private readonly LocalStackFixture _fixture;
        private readonly IEntityService _entityService;
        private readonly IEntityRepository _entityRepository;
        private readonly IConfiguration _config;

        /// <summary>
        /// Constructor wired by xUnit IClassFixture injection.
        /// Builds an in-memory IConfiguration with DynamoDB table names and
        /// SNS topic ARN prefix pointing at LocalStack, then constructs
        /// EntityRepository → EntityService with a fresh MemoryCache.
        /// </summary>
        public EntityCrudIntegrationTests(LocalStackFixture fixture)
        {
            _fixture = fixture;
            _config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "DynamoDB:MetadataTableName", LocalStackFixture.EntityMetadataTableName },
                    { "DynamoDB:RecordTableName", LocalStackFixture.RecordStorageTableName },
                    { "Sns:TopicArnPrefix", "arn:aws:sns:us-east-1:000000000000:" },
                    { "DevelopmentMode", "true" }
                })
                .Build();

            _entityRepository = new EntityRepository(
                _fixture.DynamoDbClient,
                NullLogger<EntityRepository>.Instance,
                _config);

            _entityService = new EntityService(
                _entityRepository,
                NullLogger<EntityService>.Instance,
                _config,
                new MemoryCache(new MemoryCacheOptions()));
        }

        // =====================================================================
        // Helper: build a fresh EntityService with its own MemoryCache
        // =====================================================================
        private IEntityService CreateFreshEntityService()
        {
            return new EntityService(
                new EntityRepository(
                    _fixture.DynamoDbClient,
                    NullLogger<EntityRepository>.Instance,
                    _config),
                NullLogger<EntityService>.Instance,
                _config,
                new MemoryCache(new MemoryCacheOptions()));
        }

        // =====================================================================
        // Helper: read a single DynamoDB item by PK/SK
        // =====================================================================
        private async Task<GetItemResponse> GetDynamoDbItemAsync(string pk, string sk)
        {
            var response = await _fixture.DynamoDbClient.GetItemAsync(new GetItemRequest
            {
                TableName = LocalStackFixture.EntityMetadataTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    { "PK", new AttributeValue { S = pk } },
                    { "SK", new AttributeValue { S = sk } }
                },
                ConsistentRead = true
            });
            return response;
        }

        // =====================================================================
        // Helper: query DynamoDB items by PK with SK prefix
        // =====================================================================
        private async Task<Amazon.DynamoDBv2.Model.QueryResponse> QueryDynamoDbByPkAndSkPrefixAsync(
            string pk, string skPrefix)
        {
            var response = await _fixture.DynamoDbClient.QueryAsync(new QueryRequest
            {
                TableName = LocalStackFixture.EntityMetadataTableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :skPrefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":pk", new AttributeValue { S = pk } },
                    { ":skPrefix", new AttributeValue { S = skPrefix } }
                },
                ConsistentRead = true
            });
            return response;
        }

        // =====================================================================
        //  PHASE 2 — Entity Create Tests (7 methods)
        // =====================================================================

        [Fact]
        public async Task CreateEntity_WithValidInput_PersistsToLocalStackDynamoDB()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "integration_test_entity",
                Label = "Integration Test Entity",
                LabelPlural = "Integration Test Entities"
            };

            // Act
            var response = await svc.CreateEntity(input);

            // Assert — service-level response
            response.Should().NotBeNull();
            response.Success.Should().BeTrue();
            response.Object.Should().NotBeNull();
            response.Object!.Name.Should().Be("integration_test_entity");
            response.Object.Label.Should().Be("Integration Test Entity");
            response.Object.LabelPlural.Should().Be("Integration Test Entities");
            response.Object.Id.Should().NotBe(Guid.Empty);

            // Assert — direct DynamoDB verification
            var entityId = response.Object.Id;
            var dbItem = await GetDynamoDbItemAsync($"ENTITY#{entityId}", "META");
            dbItem.Item.Should().NotBeNull();
            dbItem.Item.Should().NotBeEmpty();
            dbItem.Item.Should().ContainKey("PK");
            dbItem.Item["PK"].S.Should().Be($"ENTITY#{entityId}");

            // Verify GSI1PK for name-based lookups
            dbItem.Item.Should().ContainKey("GSI1PK");
            dbItem.Item["GSI1PK"].S.Should().Be($"ENTITY_NAME#integration_test_entity");
        }

        [Fact]
        public async Task CreateEntity_AutoGeneratesIdField()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "auto_id_entity",
                Label = "Auto ID Entity",
                LabelPlural = "Auto ID Entities"
            };

            // Act — createOnlyIdField=true (default)
            var response = await svc.CreateEntity(input);

            // Assert
            response.Success.Should().BeTrue();
            var entity = response.Object;
            entity.Should().NotBeNull();
            entity!.Fields.Should().NotBeEmpty();

            // Verify the "id" GuidField exists with correct attributes
            var idField = entity.Fields.FirstOrDefault(f =>
                f.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
            idField.Should().NotBeNull();
            idField!.Required.Should().BeTrue();
            idField.Unique.Should().BeTrue();
            idField.System.Should().BeTrue();
            idField.Should().BeOfType<GuidField>();
            var guidField = (GuidField)idField;
            guidField.GenerateNewId.Should().BeTrue();

            // Verify via DynamoDB that FIELD# items exist
            var fieldItems = await QueryDynamoDbByPkAndSkPrefixAsync(
                $"ENTITY#{entity.Id}", "FIELD#");
            fieldItems.Items.Should().NotBeEmpty();
            fieldItems.Items.Any(item =>
                item.ContainsKey("SK") && item["SK"].S.Contains("FIELD#")).Should().BeTrue();
        }

        [Fact]
        public async Task CreateEntity_WithAuditFields_CreatesStandardRelations()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();

            // First, seed a "user" entity so that audit relations can target it
            var userInput = new InputEntity
            {
                Id = SystemIds.UserEntityId,
                Name = "user",
                Label = "User",
                LabelPlural = "Users"
            };
            await svc.CreateEntity(userInput);

            var input = new InputEntity
            {
                Name = "audit_test_entity",
                Label = "Audit Test",
                LabelPlural = "Audit Tests"
            };

            // Act — createOnlyIdField=false to include audit fields
            var response = await svc.CreateEntity(input, createOnlyIdField: false);

            // Assert
            response.Success.Should().BeTrue();
            var entity = response.Object;
            entity.Should().NotBeNull();

            // Verify audit fields exist
            var fieldNames = entity!.Fields.Select(f => f.Name).ToList();
            fieldNames.Should().Contain("created_by");
            fieldNames.Should().Contain("last_modified_by");
            fieldNames.Should().Contain("created_on");
            fieldNames.Should().Contain("last_modified_on");

            // Verify relations for created_by / modified_by exist
            var relationsResponse = await svc.ReadRelations();
            relationsResponse.Success.Should().BeTrue();
            var relations = relationsResponse.Object;
            relations.Should().NotBeNull();

            var createdByRelation = relations!.FirstOrDefault(r =>
                r.Name.Contains("audit_test_entity") && r.Name.Contains("created_by"));
            var modifiedByRelation = relations.FirstOrDefault(r =>
                r.Name.Contains("audit_test_entity") && r.Name.Contains("modified_by"));

            // At least one of the user-entity relations should exist
            var auditRelations = relations.Where(r =>
                r.TargetEntityId == entity.Id || r.OriginEntityId == entity.Id).ToList();
            auditRelations.Should().NotBeEmpty("audit relations should be created for created_by/modified_by fields");
        }

        [Fact]
        public async Task CreateEntity_WithDuplicateName_ReturnsValidationError()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input1 = new InputEntity
            {
                Name = "duplicate_test",
                Label = "Dup Test 1",
                LabelPlural = "Dup Tests 1"
            };
            var response1 = await svc.CreateEntity(input1);
            response1.Success.Should().BeTrue();

            // Act — attempt duplicate
            var input2 = new InputEntity
            {
                Name = "duplicate_test",
                Label = "Dup Test 2",
                LabelPlural = "Dup Tests 2"
            };
            var response2 = await svc.CreateEntity(input2);

            // Assert
            response2.Success.Should().BeFalse();
            response2.Errors.Should().NotBeEmpty();
            response2.Errors.Any(e => e.Message.Contains("Entity with such Name exists already!"))
                .Should().BeTrue();
        }

        [Fact]
        public async Task CreateEntity_WithEmptyName_ReturnsValidationError()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "",
                Label = "Empty Name",
                LabelPlural = "Empty Names"
            };

            // Act
            var response = await svc.CreateEntity(input);

            // Assert
            response.Success.Should().BeFalse();
            response.Errors.Should().NotBeEmpty();
        }

        [Fact]
        public async Task CreateEntity_WithNameExceeding63Chars_ReturnsValidationError()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = new string('a', 64),
                Label = "Long Name",
                LabelPlural = "Long Names"
            };

            // Act
            var response = await svc.CreateEntity(input);

            // Assert
            response.Success.Should().BeFalse();
            response.Errors.Should().NotBeEmpty();
            response.Errors.Any(e => e.Message.Contains("Entity name length exceeded"))
                .Should().BeTrue();
        }

        [Fact]
        public async Task CreateEntity_WithNullRecordPermissions_InitializesEmptyLists()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "null_perms_entity",
                Label = "Null Perms",
                LabelPlural = "Null Perms"
            };
            // RecordPermissions defaults to new RecordPermissions(), but set to null explicitly
            input.RecordPermissions = null!;

            // Act
            var response = await svc.CreateEntity(input);

            // Assert
            response.Success.Should().BeTrue();
            var entity = response.Object;
            entity.Should().NotBeNull();
            entity!.RecordPermissions.Should().NotBeNull();
            entity.RecordPermissions.CanRead.Should().NotBeNull();
            entity.RecordPermissions.CanCreate.Should().NotBeNull();
            entity.RecordPermissions.CanUpdate.Should().NotBeNull();
            entity.RecordPermissions.CanDelete.Should().NotBeNull();
        }

        // =====================================================================
        //  PHASE 3 — Entity Read Tests (4 methods)
        // =====================================================================

        [Fact]
        public async Task ReadEntity_ById_ReturnsCachedOrFreshEntity()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "read_by_id_entity",
                Label = "Read By Id",
                LabelPlural = "Read By Ids"
            };
            var createResponse = await svc.CreateEntity(input);
            createResponse.Success.Should().BeTrue();
            var entityId = createResponse.Object!.Id;

            // Act
            var readResponse = await svc.ReadEntity(entityId);

            // Assert
            readResponse.Should().NotBeNull();
            readResponse.Success.Should().BeTrue();
            readResponse.Object.Should().NotBeNull();
            readResponse.Object!.Id.Should().Be(entityId);
            readResponse.Object.Name.Should().Be("read_by_id_entity");
            readResponse.Object.Label.Should().Be("Read By Id");
            readResponse.Object.Fields.Should().NotBeEmpty();
        }

        [Fact]
        public async Task ReadEntity_ByName_ReturnsCachedOrFreshEntity()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "read_by_name_ent",
                Label = "Read By Name",
                LabelPlural = "Read By Names"
            };
            var createResponse = await svc.CreateEntity(input);
            createResponse.Success.Should().BeTrue();

            // Act — read by name string
            var readResponse = await svc.ReadEntity("read_by_name_ent");

            // Assert
            readResponse.Should().NotBeNull();
            readResponse.Success.Should().BeTrue();
            readResponse.Object.Should().NotBeNull();
            readResponse.Object!.Name.Should().Be("read_by_name_ent");
            readResponse.Object.Id.Should().Be(createResponse.Object!.Id);
        }

        [Fact]
        public async Task ReadEntities_ReturnsAllSeededEntities()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();

            var names = new[] { "entity_one", "entity_two", "entity_three" };
            var createdIds = new List<Guid>();
            foreach (var name in names)
            {
                var resp = await svc.CreateEntity(new InputEntity
                {
                    Name = name,
                    Label = name.Replace("_", " "),
                    LabelPlural = name.Replace("_", " ") + "s"
                });
                resp.Success.Should().BeTrue();
                createdIds.Add(resp.Object!.Id);
            }

            // Act
            var listResponse = await svc.ReadEntities();

            // Assert
            listResponse.Should().NotBeNull();
            listResponse.Success.Should().BeTrue();
            listResponse.Object.Should().NotBeNull();
            listResponse.Object!.Count.Should().BeGreaterThanOrEqualTo(3);

            foreach (var id in createdIds)
            {
                listResponse.Object.Any(e => e.Id == id).Should().BeTrue();
            }

            // Verify hashes are computed
            foreach (var entity in listResponse.Object)
            {
                entity.Hash.Should().NotBeNullOrEmpty();
            }
        }

        [Fact]
        public async Task ReadEntity_NonExistentId_ReturnsNullObject()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();

            // Act
            var readResponse = await svc.ReadEntity(Guid.NewGuid());

            // Assert
            readResponse.Should().NotBeNull();
            readResponse.Object.Should().BeNull();
        }

        // =====================================================================
        //  PHASE 4 — Entity Update Tests (2 methods)
        // =====================================================================

        [Fact]
        public async Task UpdateEntity_ChangesLabelAndPersists()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "update_label_ent",
                Label = "Original Label",
                LabelPlural = "Original Labels"
            };
            var createResp = await svc.CreateEntity(input);
            createResp.Success.Should().BeTrue();
            var entityId = createResp.Object!.Id;

            // Act — update label
            var updateInput = new InputEntity
            {
                Id = entityId,
                Name = "update_label_ent",
                Label = "Updated Label",
                LabelPlural = "Updated Labels"
            };
            var updateResp = await svc.UpdateEntity(updateInput);

            // Assert — service-level
            updateResp.Success.Should().BeTrue();
            updateResp.Object.Should().NotBeNull();
            updateResp.Object!.Label.Should().Be("Updated Label");
            updateResp.Object.LabelPlural.Should().Be("Updated Labels");
            updateResp.Object.Name.Should().Be("update_label_ent");
            updateResp.Object.Id.Should().Be(entityId);

            // Assert — direct DynamoDB verification
            var dbItem = await GetDynamoDbItemAsync($"ENTITY#{entityId}", "META");
            dbItem.Item.Should().NotBeEmpty();
            dbItem.Item.Should().ContainKey("entityData");
        }

        [Fact]
        public async Task UpdateEntity_NonExistentId_ReturnsError()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var updateInput = new InputEntity
            {
                Id = Guid.NewGuid(),
                Name = "nonexistent_ent",
                Label = "Ghost",
                LabelPlural = "Ghosts"
            };

            // Act
            var updateResp = await svc.UpdateEntity(updateInput);

            // Assert
            updateResp.Success.Should().BeFalse();
            (updateResp.Errors.Any(e => e.Message.Contains("Entity with such Id does not exist!"))
             || updateResp.Message.Contains("Entity not found"))
                .Should().BeTrue("expected entity-not-found error");
        }

        // =====================================================================
        //  PHASE 5 — Entity Delete Tests (2 methods)
        // =====================================================================

        [Fact]
        public async Task DeleteEntity_RemovesMetadataAndFieldItems()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var input = new InputEntity
            {
                Name = "delete_meta_ent",
                Label = "Delete Meta",
                LabelPlural = "Delete Metas"
            };
            var createResp = await svc.CreateEntity(input);
            createResp.Success.Should().BeTrue();
            var entityId = createResp.Object!.Id;

            // Verify items exist before delete
            var preDeleteMeta = await GetDynamoDbItemAsync($"ENTITY#{entityId}", "META");
            preDeleteMeta.Item.Should().NotBeEmpty();
            var preDeleteFields = await QueryDynamoDbByPkAndSkPrefixAsync(
                $"ENTITY#{entityId}", "FIELD#");
            preDeleteFields.Items.Should().NotBeEmpty();

            // Act
            var deleteResp = await svc.DeleteEntity(entityId);

            // Assert
            deleteResp.Success.Should().BeTrue();
            deleteResp.Message.Should().Contain("successfully deleted");

            // Verify META item removed
            var postDeleteMeta = await GetDynamoDbItemAsync($"ENTITY#{entityId}", "META");
            postDeleteMeta.Item.Should().BeNullOrEmpty();

            // Verify FIELD# items removed
            var postDeleteFields = await QueryDynamoDbByPkAndSkPrefixAsync(
                $"ENTITY#{entityId}", "FIELD#");
            postDeleteFields.Items.Should().BeEmpty();
        }

        [Fact]
        public async Task DeleteEntity_RemovesAssociatedRelations()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();

            // Create two entities
            var resp1 = await svc.CreateEntity(new InputEntity
            {
                Name = "origin_del_ent",
                Label = "Origin",
                LabelPlural = "Origins"
            });
            resp1.Success.Should().BeTrue();
            var originEntity = resp1.Object!;

            var resp2 = await svc.CreateEntity(new InputEntity
            {
                Name = "target_del_ent",
                Label = "Target",
                LabelPlural = "Targets"
            });
            resp2.Success.Should().BeTrue();
            var targetEntity = resp2.Object!;

            // Add a GuidField to target for relation target field
            var targetGuidField = new InputGuidField
            {
                Name = "origin_id",
                Label = "Origin ID",
                Required = false,
                Unique = false,
                GenerateNewId = false
            };
            var fieldResp = await svc.CreateField(targetEntity.Id, targetGuidField);
            fieldResp.Success.Should().BeTrue();
            var targetFieldId = fieldResp.Object!.Id;

            // Get origin entity "id" field
            var originIdField = originEntity.Fields.First(f => f.Name == "id");

            // Create a relation
            var relation = new EntityRelation
            {
                Name = "origin_target_del_rel",
                Label = "Origin Target Delete Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = originEntity.Id,
                OriginFieldId = originIdField.Id,
                TargetEntityId = targetEntity.Id,
                TargetFieldId = targetFieldId
            };
            var relResp = await svc.CreateRelation(relation);
            relResp.Success.Should().BeTrue();
            var relationId = relResp.Object!.Id;

            // Verify relation exists pre-delete
            var preDelRelation = await svc.ReadRelation(relationId);
            preDelRelation.Object.Should().NotBeNull();

            // Act — delete the origin entity (should cascade remove relations)
            var deleteResp = await svc.DeleteEntity(originEntity.Id);
            deleteResp.Success.Should().BeTrue();

            // Assert — relation should no longer be found
            // Force cache clear and re-read
            svc.ClearCache();
            var postDelRelations = await svc.ReadRelations();
            postDelRelations.Object.Should().NotBeNull();
            postDelRelations.Object!.Any(r => r.Id == relationId).Should().BeFalse(
                "relation should be removed when origin entity is deleted");
        }

        // =====================================================================
        //  PHASE 6 — Field CRUD Integration Tests (4 methods)
        // =====================================================================

        [Fact]
        public async Task CreateField_AddsFieldItemToDynamoDB()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var entityResp = await svc.CreateEntity(new InputEntity
            {
                Name = "field_add_entity",
                Label = "Field Add",
                LabelPlural = "Field Adds"
            });
            entityResp.Success.Should().BeTrue();
            var entityId = entityResp.Object!.Id;

            var textField = new InputTextField
            {
                Name = "test_text_field",
                Label = "Test Text Field",
                Required = false,
                Unique = false,
                Searchable = true,
                DefaultValue = "hello",
                MaxLength = 200
            };

            // Act
            var fieldResp = await svc.CreateField(entityId, textField);

            // Assert — service-level
            fieldResp.Success.Should().BeTrue();
            fieldResp.Object.Should().NotBeNull();
            fieldResp.Object!.Name.Should().Be("test_text_field");
            fieldResp.Object.Id.Should().NotBe(Guid.Empty);

            // Assert — direct DynamoDB verification
            var fieldId = fieldResp.Object.Id;
            var dbItem = await GetDynamoDbItemAsync(
                $"ENTITY#{entityId}", $"FIELD#{fieldId}");
            dbItem.Item.Should().NotBeEmpty();
            dbItem.Item.Should().ContainKey("PK");
            dbItem.Item["PK"].S.Should().Be($"ENTITY#{entityId}");
            dbItem.Item["SK"].S.Should().Be($"FIELD#{fieldId}");
        }

        [Fact]
        public async Task CreateField_AllFieldTypes_PersistCorrectly()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var entityResp = await svc.CreateEntity(new InputEntity
            {
                Name = "all_fields_entity",
                Label = "All Fields",
                LabelPlural = "All Fields Entities"
            });
            entityResp.Success.Should().BeTrue();
            var entityId = entityResp.Object!.Id;

            // Build one InputField of each of the 20 concrete types (excluding RelationField)
            var inputFields = new List<InputField>
            {
                new InputAutoNumberField
                {
                    Name = "auto_number_f", Label = "Auto Number",
                    DefaultValue = 1, DisplayFormat = "{0}"
                },
                new InputCheckboxField
                {
                    Name = "checkbox_f", Label = "Checkbox",
                    DefaultValue = false
                },
                new InputCurrencyField
                {
                    Name = "currency_f", Label = "Currency",
                    DefaultValue = 0m, MinValue = 0m, MaxValue = 999999m,
                    Currency = new CurrencyType { Symbol = "$", SymbolNative = "$", Code = "USD", SymbolPlacement = CurrencySymbolPlacement.Before, DecimalDigits = 2, NamePlural = "US dollars", Name = "US Dollar" }
                },
                new InputDateField
                {
                    Name = "date_f", Label = "Date",
                    Format = "dd/MM/yyyy"
                },
                new InputDateTimeField
                {
                    Name = "datetime_f", Label = "DateTime",
                    Format = "dd/MM/yyyy HH:mm"
                },
                new InputEmailField
                {
                    Name = "email_f", Label = "Email",
                    DefaultValue = "", MaxLength = 255
                },
                new InputFileField
                {
                    Name = "file_f", Label = "File"
                },
                new InputGuidField
                {
                    Name = "guid_f", Label = "Guid",
                    GenerateNewId = false
                },
                new InputHtmlField
                {
                    Name = "html_f", Label = "Html",
                    DefaultValue = ""
                },
                new InputImageField
                {
                    Name = "image_f", Label = "Image"
                },
                new InputMultiLineTextField
                {
                    Name = "multiline_f", Label = "MultiLine",
                    DefaultValue = "", MaxLength = 5000
                },
                new InputMultiSelectField
                {
                    Name = "multiselect_f", Label = "MultiSelect",
                    Options = new List<SelectOption>
                    {
                        new SelectOption { Value = "opt1", Label = "Option 1" },
                        new SelectOption { Value = "opt2", Label = "Option 2" }
                    }
                },
                new InputNumberField
                {
                    Name = "number_f", Label = "Number",
                    DefaultValue = 0m, MinValue = 0m, MaxValue = 999999m, DecimalPlaces = 2
                },
                new InputPasswordField
                {
                    Name = "password_f", Label = "Password",
                    MaxLength = 128
                },
                new InputPercentField
                {
                    Name = "percent_f", Label = "Percent",
                    DefaultValue = 0m, MinValue = 0m, MaxValue = 100m, DecimalPlaces = 2
                },
                new InputPhoneField
                {
                    Name = "phone_f", Label = "Phone",
                    DefaultValue = "", MaxLength = 30
                },
                new InputSelectField
                {
                    Name = "select_f", Label = "Select",
                    Options = new List<SelectOption>
                    {
                        new SelectOption { Value = "a", Label = "A" },
                        new SelectOption { Value = "b", Label = "B" }
                    }
                },
                new InputTextField
                {
                    Name = "text_f", Label = "Text",
                    DefaultValue = "", MaxLength = 500
                },
                new InputUrlField
                {
                    Name = "url_f", Label = "Url",
                    DefaultValue = "", MaxLength = 2048, OpenTargetInNewWindow = true
                },
                new InputGeographyField
                {
                    Name = "geography_f", Label = "Geography",
                    MaxLength = 10000, Format = GeographyFieldFormat.GeoJSON
                }
            };

            // Act — create all fields
            var createdFieldIds = new List<Guid>();
            foreach (var inputField in inputFields)
            {
                var resp = await svc.CreateField(entityId, inputField);
                resp.Success.Should().BeTrue(
                    $"field '{inputField.Name}' should be created successfully, errors: " +
                    string.Join(", ", resp.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));
                resp.Object.Should().NotBeNull();
                createdFieldIds.Add(resp.Object!.Id);
            }

            // Assert — read all fields back from DynamoDB
            var fieldQuery = await QueryDynamoDbByPkAndSkPrefixAsync(
                $"ENTITY#{entityId}", "FIELD#");
            // At least 20 custom fields + 1 auto-generated "id" field = 21+
            fieldQuery.Items.Count.Should().BeGreaterThanOrEqualTo(inputFields.Count);

            // Assert — read via service and verify round-trip types
            svc.ClearCache();
            var entityReadResp = await svc.ReadEntity(entityId);
            entityReadResp.Success.Should().BeTrue();
            var fields = entityReadResp.Object!.Fields;

            // Verify each created field type round-trips correctly
            fields.Any(f => f.Name == "auto_number_f" && f is AutoNumberField).Should().BeTrue();
            fields.Any(f => f.Name == "checkbox_f" && f is CheckboxField).Should().BeTrue();
            fields.Any(f => f.Name == "currency_f" && f is CurrencyField).Should().BeTrue();
            fields.Any(f => f.Name == "date_f" && f is DateField).Should().BeTrue();
            fields.Any(f => f.Name == "datetime_f" && f is DateTimeField).Should().BeTrue();
            fields.Any(f => f.Name == "email_f" && f is EmailField).Should().BeTrue();
            fields.Any(f => f.Name == "file_f" && f is FileField).Should().BeTrue();
            fields.Any(f => f.Name == "guid_f" && f is GuidField).Should().BeTrue();
            fields.Any(f => f.Name == "html_f" && f is HtmlField).Should().BeTrue();
            fields.Any(f => f.Name == "image_f" && f is ImageField).Should().BeTrue();
            fields.Any(f => f.Name == "multiline_f" && f is MultiLineTextField).Should().BeTrue();
            fields.Any(f => f.Name == "multiselect_f" && f is MultiSelectField).Should().BeTrue();
            fields.Any(f => f.Name == "number_f" && f is NumberField).Should().BeTrue();
            fields.Any(f => f.Name == "password_f" && f is PasswordField).Should().BeTrue();
            fields.Any(f => f.Name == "percent_f" && f is PercentField).Should().BeTrue();
            fields.Any(f => f.Name == "phone_f" && f is PhoneField).Should().BeTrue();
            fields.Any(f => f.Name == "select_f" && f is SelectField).Should().BeTrue();
            fields.Any(f => f.Name == "text_f" && f is TextField).Should().BeTrue();
            fields.Any(f => f.Name == "url_f" && f is UrlField).Should().BeTrue();
            fields.Any(f => f.Name == "geography_f" && f is GeographyField).Should().BeTrue();
        }

        [Fact]
        public async Task UpdateField_ModifiesFieldInDynamoDB()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var entityResp = await svc.CreateEntity(new InputEntity
            {
                Name = "field_update_ent",
                Label = "Field Update",
                LabelPlural = "Field Updates"
            });
            entityResp.Success.Should().BeTrue();
            var entityId = entityResp.Object!.Id;

            var createFieldResp = await svc.CreateField(entityId, new InputTextField
            {
                Name = "updatable_text",
                Label = "Original Label",
                MaxLength = 100
            });
            createFieldResp.Success.Should().BeTrue();
            var fieldId = createFieldResp.Object!.Id;

            // Act — update label and MaxLength
            var updateField = new InputTextField
            {
                Id = fieldId,
                Name = "updatable_text",
                Label = "Updated Field Label",
                MaxLength = 500
            };
            var updateResp = await svc.UpdateField(entityId, updateField);

            // Assert — service-level
            updateResp.Success.Should().BeTrue();
            updateResp.Object.Should().NotBeNull();
            updateResp.Object!.Label.Should().Be("Updated Field Label");
            updateResp.Object.Should().BeOfType<TextField>();
            ((TextField)updateResp.Object).MaxLength.Should().Be(500);

            // Assert — direct DynamoDB
            var dbItem = await GetDynamoDbItemAsync(
                $"ENTITY#{entityId}", $"FIELD#{fieldId}");
            dbItem.Item.Should().NotBeEmpty();
        }

        [Fact]
        public async Task DeleteField_RemovesFieldItemFromDynamoDB()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var entityResp = await svc.CreateEntity(new InputEntity
            {
                Name = "field_delete_ent",
                Label = "Field Delete",
                LabelPlural = "Field Deletes"
            });
            entityResp.Success.Should().BeTrue();
            var entityId = entityResp.Object!.Id;

            var createFieldResp = await svc.CreateField(entityId, new InputTextField
            {
                Name = "removable_text",
                Label = "Removable",
                MaxLength = 100
            });
            createFieldResp.Success.Should().BeTrue();
            var fieldId = createFieldResp.Object!.Id;

            // Verify field item exists before deletion
            var preDelete = await GetDynamoDbItemAsync(
                $"ENTITY#{entityId}", $"FIELD#{fieldId}");
            preDelete.Item.Should().NotBeEmpty();

            // Act
            var deleteResp = await svc.DeleteField(entityId, fieldId);

            // Assert
            deleteResp.Success.Should().BeTrue();
            deleteResp.Message.Should().Contain("successfully deleted");

            // Verify field item removed from DynamoDB
            var postDelete = await GetDynamoDbItemAsync(
                $"ENTITY#{entityId}", $"FIELD#{fieldId}");
            postDelete.Item.Should().BeNullOrEmpty();
        }

        // =====================================================================
        //  PHASE 7 — Relation CRUD Integration Tests (4 methods)
        // =====================================================================

        /// <summary>
        /// Helper: creates two entities (origin + target), each with at least one GuidField, and
        /// returns (originEntity, originIdFieldId, targetEntity, targetGuidFieldId).
        /// When <paramref name="requiredAndUnique"/> is true, the target GuidField is
        /// created with Required=true and Unique=true (needed for ManyToMany relations).
        /// </summary>
        private async Task<(Entity origin, Guid originFieldId, Entity target, Guid targetFieldId)>
            SetupTwoEntitiesWithGuidFieldsAsync(IEntityService svc, string prefix, bool requiredAndUnique = false)
        {
            var resp1 = await svc.CreateEntity(new InputEntity
            {
                Name = $"{prefix}_origin",
                Label = $"{prefix} Origin",
                LabelPlural = $"{prefix} Origins"
            });
            resp1.Success.Should().BeTrue();
            var origin = resp1.Object!;
            var originIdField = origin.Fields.First(f => f.Name == "id");

            var resp2 = await svc.CreateEntity(new InputEntity
            {
                Name = $"{prefix}_target",
                Label = $"{prefix} Target",
                LabelPlural = $"{prefix} Targets"
            });
            resp2.Success.Should().BeTrue();
            var target = resp2.Object!;

            // Add a GuidField to target
            var targetGuid = new InputGuidField
            {
                Name = $"{prefix}_origin_id",
                Label = $"{prefix} Origin Id",
                Required = requiredAndUnique,
                Unique = requiredAndUnique,
                GenerateNewId = false
            };
            var fieldResp = await svc.CreateField(target.Id, targetGuid);
            fieldResp.Success.Should().BeTrue();

            return (origin, originIdField.Id, target, fieldResp.Object!.Id);
        }

        [Fact]
        public async Task CreateRelation_OneToMany_PersistsCorrectly()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var (origin, originFieldId, target, targetFieldId) =
                await SetupTwoEntitiesWithGuidFieldsAsync(svc, "o2m");

            var relation = new EntityRelation
            {
                Name = "o2m_test_relation",
                Label = "O2M Test Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = origin.Id,
                OriginFieldId = originFieldId,
                TargetEntityId = target.Id,
                TargetFieldId = targetFieldId
            };

            // Act
            var relResp = await svc.CreateRelation(relation);

            // Assert — service-level
            relResp.Success.Should().BeTrue();
            relResp.Object.Should().NotBeNull();
            relResp.Object!.Name.Should().Be("o2m_test_relation");
            relResp.Object.RelationType.Should().Be(EntityRelationType.OneToMany);
            relResp.Object.OriginEntityId.Should().Be(origin.Id);
            relResp.Object.TargetEntityId.Should().Be(target.Id);
            relResp.Object.Id.Should().NotBe(Guid.Empty);

            // Assert — direct DynamoDB verification
            // Relation items are stored with PK=ENTITY#{originEntityId}, SK=RELATION#{relationId}
            var relationId = relResp.Object.Id;
            var dbItem = await GetDynamoDbItemAsync($"ENTITY#{origin.Id}", $"RELATION#{relationId}");
            dbItem.Item.Should().NotBeEmpty();
            dbItem.Item.Should().ContainKey("PK");
            dbItem.Item["PK"].S.Should().Be($"ENTITY#{origin.Id}");
            dbItem.Item["SK"].S.Should().Be($"RELATION#{relationId}");
            dbItem.Item.Should().ContainKey("relationData");
        }

        [Fact]
        public async Task CreateRelation_ManyToMany_PersistsCorrectly()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            // ManyToMany requires both origin and target fields to be Required + Unique
            var (origin, originFieldId, target, targetFieldId) =
                await SetupTwoEntitiesWithGuidFieldsAsync(svc, "m2m", requiredAndUnique: true);

            var relation = new EntityRelation
            {
                Name = "m2m_test_relation",
                Label = "M2M Test Relation",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = origin.Id,
                OriginFieldId = originFieldId,
                TargetEntityId = target.Id,
                TargetFieldId = targetFieldId
            };

            // Act
            var relResp = await svc.CreateRelation(relation);

            // Assert — service-level
            relResp.Success.Should().BeTrue();
            relResp.Object.Should().NotBeNull();
            relResp.Object!.RelationType.Should().Be(EntityRelationType.ManyToMany);

            // Assert — DynamoDB relation metadata (PK=ENTITY#{originEntityId}, SK=RELATION#{relationId})
            var relationId = relResp.Object.Id;
            var dbItem = await GetDynamoDbItemAsync($"ENTITY#{origin.Id}", $"RELATION#{relationId}");
            dbItem.Item.Should().NotBeEmpty();
            dbItem.Item["PK"].S.Should().Be($"ENTITY#{origin.Id}");
        }

        [Fact]
        public async Task UpdateRelation_ImmutabilityEnforced()
        {
            // Arrange — create 3 entities so we can swap origin/target to REAL entities
            // (ValidateRelation checks entity existence BEFORE immutability, so we need valid entities)
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var (origin, originFieldId, target, targetFieldId) =
                await SetupTwoEntitiesWithGuidFieldsAsync(svc, "immut");

            // Create a third entity (alternate) for immutability swap tests
            var resp3 = await svc.CreateEntity(new InputEntity
            {
                Name = "immut_alt",
                Label = "Immut Alt",
                LabelPlural = "Immut Alts"
            });
            resp3.Success.Should().BeTrue();
            var alt = resp3.Object!;
            var altIdField = alt.Fields.First(f => f.Name == "id");

            var relation = new EntityRelation
            {
                Name = "immut_relation",
                Label = "Immutable Relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = origin.Id,
                OriginFieldId = originFieldId,
                TargetEntityId = target.Id,
                TargetFieldId = targetFieldId
            };
            var createResp = await svc.CreateRelation(relation);
            createResp.Success.Should().BeTrue();
            var createdRelation = createResp.Object!;

            // Act 1 — attempt to change RelationType (origin/target remain valid → immutability check fires)
            var updateRelationType = new EntityRelation
            {
                Id = createdRelation.Id,
                Name = createdRelation.Name,
                Label = createdRelation.Label,
                RelationType = EntityRelationType.ManyToMany, // changed!
                OriginEntityId = createdRelation.OriginEntityId,
                OriginFieldId = createdRelation.OriginFieldId,
                TargetEntityId = createdRelation.TargetEntityId,
                TargetFieldId = createdRelation.TargetFieldId
            };
            var typeResp = await svc.UpdateRelation(updateRelationType);
            typeResp.Success.Should().BeFalse();
            typeResp.Errors.Should().NotBeEmpty();
            typeResp.Errors.Any(e => e.Message.Contains("Relation type cannot be changed."))
                .Should().BeTrue();

            // Act 2 — attempt to change OriginEntityId (swap to alt entity + alt field so validation passes entity/field checks)
            var updateOriginEntity = new EntityRelation
            {
                Id = createdRelation.Id,
                Name = createdRelation.Name,
                Label = createdRelation.Label,
                RelationType = createdRelation.RelationType,
                OriginEntityId = alt.Id, // changed to existing alt entity
                OriginFieldId = altIdField.Id, // must match a GuidField in the alt entity
                TargetEntityId = createdRelation.TargetEntityId,
                TargetFieldId = createdRelation.TargetFieldId
            };
            var originResp = await svc.UpdateRelation(updateOriginEntity);
            originResp.Success.Should().BeFalse();
            originResp.Errors.Should().NotBeEmpty();
            originResp.Errors.Any(e => e.Message.Contains("Origin entity cannot be changed."))
                .Should().BeTrue();

            // Act 3 — attempt to change TargetEntityId (swap to alt entity + alt field)
            var updateTargetEntity = new EntityRelation
            {
                Id = createdRelation.Id,
                Name = createdRelation.Name,
                Label = createdRelation.Label,
                RelationType = createdRelation.RelationType,
                OriginEntityId = createdRelation.OriginEntityId,
                OriginFieldId = createdRelation.OriginFieldId,
                TargetEntityId = alt.Id, // changed to existing alt entity
                TargetFieldId = altIdField.Id // must match a GuidField in the alt entity
            };
            var targetResp = await svc.UpdateRelation(updateTargetEntity);
            targetResp.Success.Should().BeFalse();
            targetResp.Errors.Should().NotBeEmpty();
            targetResp.Errors.Any(e => e.Message.Contains("Target entity cannot be changed."))
                .Should().BeTrue();

            // Act 4 — verify that updating Name/Label succeeds
            var updateLabel = new EntityRelation
            {
                Id = createdRelation.Id,
                Name = createdRelation.Name, // keep same
                Label = "Updated Immutable Label", // only label changes
                RelationType = createdRelation.RelationType,
                OriginEntityId = createdRelation.OriginEntityId,
                OriginFieldId = createdRelation.OriginFieldId,
                TargetEntityId = createdRelation.TargetEntityId,
                TargetFieldId = createdRelation.TargetFieldId
            };
            var labelResp = await svc.UpdateRelation(updateLabel);
            labelResp.Success.Should().BeTrue();
            labelResp.Object.Should().NotBeNull();
            labelResp.Object!.Label.Should().Be("Updated Immutable Label");
        }

        [Fact]
        public async Task DeleteRelation_RemovesRelationAndM2MItems()
        {
            // Arrange — ManyToMany requires Required+Unique fields
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var (origin, originFieldId, target, targetFieldId) =
                await SetupTwoEntitiesWithGuidFieldsAsync(svc, "delrel", requiredAndUnique: true);

            var relation = new EntityRelation
            {
                Name = "delrel_m2m_relation",
                Label = "Delete M2M Rel",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = origin.Id,
                OriginFieldId = originFieldId,
                TargetEntityId = target.Id,
                TargetFieldId = targetFieldId
            };
            var createResp = await svc.CreateRelation(relation);
            createResp.Success.Should().BeTrue();
            var relationId = createResp.Object!.Id;

            // Verify relation metadata item exists (stored under origin entity PK)
            var preDeleteItem = await GetDynamoDbItemAsync($"ENTITY#{origin.Id}", $"RELATION#{relationId}");
            preDeleteItem.Item.Should().NotBeEmpty();

            // Act
            var deleteResp = await svc.DeleteRelation(relationId);

            // Assert
            deleteResp.Success.Should().BeTrue();
            deleteResp.Message.Should().Contain("successfully deleted");

            // Verify relation metadata removed from DynamoDB
            var postDeleteItem = await GetDynamoDbItemAsync($"ENTITY#{origin.Id}", $"RELATION#{relationId}");
            postDeleteItem.Item.Should().BeNullOrEmpty();

            // Verify via service that relation is gone
            svc.ClearCache();
            var readResp = await svc.ReadRelation(relationId);
            readResp.Object.Should().BeNull();
        }

        // =====================================================================
        //  PHASE 8 — Cache Behavior Tests (3 methods)
        // =====================================================================

        [Fact]
        public async Task EntityCache_InvalidatedAfterCreate()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();

            // Seed one entity and populate cache
            var resp1 = await svc.CreateEntity(new InputEntity
            {
                Name = "cache_pre_entity",
                Label = "Cache Pre",
                LabelPlural = "Cache Pres"
            });
            resp1.Success.Should().BeTrue();

            // Read entities (populates cache)
            var listBefore = await svc.ReadEntities();
            listBefore.Success.Should().BeTrue();
            var countBefore = listBefore.Object!.Count;

            // Act — create new entity (should invalidate cache)
            var resp2 = await svc.CreateEntity(new InputEntity
            {
                Name = "cache_post_entity",
                Label = "Cache Post",
                LabelPlural = "Cache Posts"
            });
            resp2.Success.Should().BeTrue();

            // Assert — reading again shows the new entity
            var listAfter = await svc.ReadEntities();
            listAfter.Success.Should().BeTrue();
            listAfter.Object!.Count.Should().BeGreaterThan(countBefore);
            listAfter.Object.Any(e => e.Name == "cache_post_entity").Should().BeTrue();
        }

        [Fact]
        public async Task EntityCache_InvalidatedAfterUpdate()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var createResp = await svc.CreateEntity(new InputEntity
            {
                Name = "cache_update_ent",
                Label = "Original Cache Label",
                LabelPlural = "Originals"
            });
            createResp.Success.Should().BeTrue();
            var entityId = createResp.Object!.Id;

            // Populate cache by reading
            var readBefore = await svc.ReadEntity(entityId);
            readBefore.Object!.Label.Should().Be("Original Cache Label");

            // Act — update label
            var updateResp = await svc.UpdateEntity(new InputEntity
            {
                Id = entityId,
                Name = "cache_update_ent",
                Label = "Updated Cache Label",
                LabelPlural = "Updated"
            });
            updateResp.Success.Should().BeTrue();

            // Assert — reading returns updated label (cache invalidated)
            var readAfter = await svc.ReadEntity(entityId);
            readAfter.Object.Should().NotBeNull();
            readAfter.Object!.Label.Should().Be("Updated Cache Label");
        }

        [Fact]
        public async Task EntityCache_InvalidatedAfterDelete()
        {
            // Arrange
            await _fixture.ResetAsync();
            var svc = CreateFreshEntityService();
            var createResp = await svc.CreateEntity(new InputEntity
            {
                Name = "cache_delete_ent",
                Label = "Cache Delete",
                LabelPlural = "Cache Deletes"
            });
            createResp.Success.Should().BeTrue();
            var entityId = createResp.Object!.Id;

            // Populate cache by reading list
            var listBefore = await svc.ReadEntities();
            listBefore.Object!.Any(e => e.Id == entityId).Should().BeTrue();

            // Act — delete entity
            var deleteResp = await svc.DeleteEntity(entityId);
            deleteResp.Success.Should().BeTrue();

            // Assert — entity no longer in list (cache invalidated)
            var listAfter = await svc.ReadEntities();
            listAfter.Object!.Any(e => e.Id == entityId).Should().BeFalse();
        }
    }
}
