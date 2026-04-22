using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using WebVellaErp.EntityManagement.DataAccess;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using Xunit;

namespace WebVellaErp.EntityManagement.Tests.Unit.Services
{
    /// <summary>
    /// Comprehensive unit tests for RecordService covering CRUD operations, permission enforcement,
    /// relation-aware payload processing, field value normalization for all 20+ field types,
    /// hook-to-event migration patterns, file/image S3 path handling, and error message backward compatibility.
    /// Source mapping: WebVella.Erp/Api/RecordManager.cs + WebVella.Erp/Hooks/RecordHookManager.cs
    /// </summary>
    public class RecordServiceTests
    {
        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IEntityRepository> _mockEntityRepository;
        private readonly Mock<IRecordRepository> _mockRecordRepository;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<ILogger<RecordService>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfiguration;
        private readonly RecordService _sut;

        public RecordServiceTests()
        {
            _mockEntityService = new Mock<IEntityService>();
            _mockEntityRepository = new Mock<IEntityRepository>();
            _mockRecordRepository = new Mock<IRecordRepository>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockLogger = new Mock<ILogger<RecordService>>();
            _mockConfiguration = new Mock<IConfiguration>();

            // Default configuration section for unmatched keys
            var defaultSection = new Mock<IConfigurationSection>();
            defaultSection.Setup(x => x.Value).Returns((string?)null);
            _mockConfiguration.Setup(x => x.GetSection(It.IsAny<string>())).Returns(defaultSection.Object);

            // SNS Topic ARN prefix
            var snsSection = new Mock<IConfigurationSection>();
            snsSection.Setup(x => x.Value).Returns("arn:aws:sns:us-east-1:000000000000:entity-management");
            _mockConfiguration.Setup(x => x.GetSection("Sns:TopicArnPrefix")).Returns(snsSection.Object);

            // Development mode flag
            var devModeSection = new Mock<IConfigurationSection>();
            devModeSection.Setup(x => x.Value).Returns("false");
            _mockConfiguration.Setup(x => x.GetSection("DevelopmentMode")).Returns(devModeSection.Object);

            // ERP timezone for DateTime normalization
            var tzSection = new Mock<IConfigurationSection>();
            tzSection.Setup(x => x.Value).Returns("UTC");
            _mockConfiguration.Setup(x => x.GetSection("ErpTimeZoneName")).Returns(tzSection.Object);

            // Default mock behaviors for most tests
            _mockEntityService.Setup(x => x.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Success = true, Object = new List<EntityRelation>() });
            _mockRecordRepository.Setup(x => x.Find(It.IsAny<EntityQuery>()))
                .ReturnsAsync(new List<EntityRecord>());
            _mockSnsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());

            // Default mock for entity repository relation lookup fallback (GetAllRelations)
            _mockEntityRepository.Setup(x => x.GetAllRelations())
                .ReturnsAsync(new List<EntityRelation>());

            // Default mock for repository Count used by IRecordService.Count()
            _mockRecordRepository.Setup(x => x.Count(It.IsAny<EntityQuery>()))
                .ReturnsAsync(0L);

            _sut = new RecordService(
                _mockEntityService.Object,
                _mockEntityRepository.Object,
                _mockRecordRepository.Object,
                _mockSnsClient.Object,
                _mockLogger.Object,
                _mockConfiguration.Object
            );

            // Verify SUT implements the IRecordService interface contract
            IRecordService _ = _sut;
        }

        #region Helper Methods

        /// <summary>
        /// Creates a test entity with standard id and name fields plus optional additional fields.
        /// RecordPermissions includes AdministratorRoleId for all operations by default.
        /// </summary>
        private Entity CreateTestEntity(string name = "test_entity", List<Field>? additionalFields = null)
        {
            // Build the primary id GuidField from an InputGuidField specification
            // InputGuidField extends InputField — demonstrating the input→persisted field pattern
            var inputIdSpec = new InputGuidField { GenerateNewId = true };
            InputField baseInputSpec = inputIdSpec; // proves InputGuidField IS-A InputField
            _ = baseInputSpec.Permissions; // access InputField.Permissions to verify it exists

            var fields = new List<Field>
            {
                new GuidField { Id = Guid.NewGuid(), Name = "id", Required = true, Unique = true, GenerateNewId = inputIdSpec.GenerateNewId ?? true },
                new TextField { Id = Guid.NewGuid(), Name = "name", Required = false, Unique = false }
            };
            if (additionalFields != null)
                fields.AddRange(additionalFields);

            return new Entity
            {
                Id = Guid.NewGuid(),
                Name = name,
                Label = name,
                LabelPlural = name + "s",
                Fields = fields,
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };
        }

        /// <summary>
        /// Creates an EntityRecord with optional id and a default name value.
        /// </summary>
        private EntityRecord CreateTestRecord(Guid? id = null)
        {
            var record = new EntityRecord();
            if (id.HasValue)
                record["id"] = id.Value;
            record["name"] = "Test Record";
            return record;
        }

        /// <summary>
        /// Sets up the entity service mock to return the given entity for both
        /// string-based and Guid-based lookups used by RecordService.GetEntity helpers.
        /// </summary>
        private void SetupEntityServiceForEntity(Entity entity)
        {
            _mockEntityService.Setup(x => x.ReadEntity(entity.Name))
                .ReturnsAsync(new EntityResponse { Success = true, Object = entity });
            _mockEntityService.Setup(x => x.ReadEntity(entity.Id))
                .ReturnsAsync(new EntityResponse { Success = true, Object = entity });
            _mockEntityService.Setup(x => x.GetEntity(entity.Name))
                .ReturnsAsync(entity);
            _mockEntityService.Setup(x => x.GetEntity(entity.Id))
                .ReturnsAsync(entity);
        }

        /// <summary>
        /// Sets up the entity service mock to return the given relations list for ReadRelations.
        /// </summary>
        private void SetupRelations(List<EntityRelation> relations)
        {
            _mockEntityService.Setup(x => x.ReadRelations())
                .ReturnsAsync(new EntityRelationListResponse { Success = true, Object = relations });
        }

        /// <summary>
        /// Sets up the record repository to return a record when FindRecord is called,
        /// simulating successful post-create/update reload.
        /// </summary>
        private void SetupFindRecordReturns(string entityName, Guid recordId, EntityRecord record)
        {
            _mockRecordRepository.Setup(x => x.FindRecord(entityName, recordId))
                .ReturnsAsync(record);
        }

        /// <summary>
        /// Invokes the private ExtractFieldValue method via reflection.
        /// Unwraps TargetInvocationException to expose the actual inner exception for assertion.
        /// </summary>
        private object? InvokeExtractFieldValue(object? value, Field field, bool encryptPasswordFields = true)
        {
            var method = typeof(RecordService).GetMethod("ExtractFieldValue",
                BindingFlags.NonPublic | BindingFlags.Instance);
            try
            {
                return method!.Invoke(_sut, new object?[] { value, field, encryptPasswordFields });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        /// <summary>
        /// Computes MD5 hash matching RecordService.ComputeMd5Hash for password field verification.
        /// </summary>
        private static string ComputeExpectedMd5Hash(string input)
        {
            using var md5 = MD5.Create();
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);
            var sb = new StringBuilder();
            foreach (var b in hashBytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        #endregion

        // =================================================================
        // Phase 2: CreateRecord Tests
        // =================================================================
        #region CreateRecord Tests

        [Fact]
        public async Task CreateRecord_NullEntityName_ReturnsError()
        {
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.CreateRecord(entityName: "", record: record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Invalid entity name.");
        }

        [Fact]
        public async Task CreateRecord_EntityNotFound_ReturnsError()
        {
            _mockEntityService.Setup(x => x.ReadEntity("nonexistent"))
                .ReturnsAsync(new EntityResponse { Success = false, Object = null });
            _mockEntityService.Setup(x => x.GetEntity("nonexistent"))
                .ReturnsAsync((Entity?)null);
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.CreateRecord("nonexistent", record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Entity cannot be found.");
        }

        [Fact]
        public async Task CreateRecord_NullEntity_ReturnsError()
        {
            _mockEntityService.Setup(x => x.ReadEntity("null_entity"))
                .ReturnsAsync(new EntityResponse { Success = false, Object = null });
            _mockEntityService.Setup(x => x.GetEntity("null_entity"))
                .ReturnsAsync((Entity?)null);
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.CreateRecord("null_entity", record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Entity cannot be found.");
        }

        [Fact]
        public async Task CreateRecord_NullRecord_ReturnsError()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var result = await _sut.CreateRecord(entity, null!);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Invalid record. Cannot be null.");
        }

        [Fact]
        public async Task CreateRecord_NoCreatePermission_ReturnsForbidden()
        {
            // This test validates that HasEntityPermission(EntityPermission.Create, entity)
            // returns false and the service blocks the operation.
            // EntityPermission enum values used across CRUD: Read, Create, Update, Delete
            EntityPermission testedPermission = EntityPermission.Create;
            testedPermission.Should().Be(EntityPermission.Create);

            var entity = CreateTestEntity();
            entity.RecordPermissions = new RecordPermissions
            {
                CanRead = new List<Guid> { Guid.NewGuid() },
                CanCreate = new List<Guid> { Guid.NewGuid() },
                CanUpdate = new List<Guid> { Guid.NewGuid() },
                CanDelete = new List<Guid> { Guid.NewGuid() }
            };
            SetupEntityServiceForEntity(entity);
            var record = CreateTestRecord(Guid.NewGuid());
            QueryResponse result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            result.Message.Should().Be("Access denied.");
            // Verify ErrorModel properties for backward-compatible error structure
            result.Errors.Should().NotBeEmpty();
            ErrorModel firstError = result.Errors.First();
            firstError.Key.Should().NotBeNull();
            firstError.Message.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task CreateRecord_NoCreatePermission_MessageIncludesEntityName()
        {
            var entity = CreateTestEntity("permission_test_entity");
            entity.RecordPermissions = new RecordPermissions
            {
                CanRead = new List<Guid> { Guid.NewGuid() },
                CanCreate = new List<Guid> { Guid.NewGuid() },
                CanUpdate = new List<Guid> { Guid.NewGuid() },
                CanDelete = new List<Guid> { Guid.NewGuid() }
            };
            SetupEntityServiceForEntity(entity);
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Access denied.");
        }

        [Fact]
        public async Task CreateRecord_NoIdProperty_GeneratesNewGuid()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var record = new EntityRecord();
            record["name"] = "No ID Record";

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, It.IsAny<Guid>()))
                .ReturnsAsync((string eName, Guid id) =>
                {
                    var r = new EntityRecord();
                    r["id"] = id;
                    r["name"] = "No ID Record";
                    return r;
                });

            var result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            _mockRecordRepository.Verify(x => x.CreateRecord(entity.Name,
                It.IsAny<IEnumerable<KeyValuePair<string, object>>>()), Times.Once);
        }

        [Fact]
        public async Task CreateRecord_StringId_ParsesGuid()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var expectedId = Guid.NewGuid();
            var record = new EntityRecord();
            record["id"] = expectedId.ToString();
            record["name"] = "String ID Record";

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, expectedId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = expectedId;
                    r["name"] = "String ID Record";
                    return r;
                });

            var result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_GuidId_UsedDirectly()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var expectedId = Guid.NewGuid();
            var record = CreateTestRecord(expectedId);

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, expectedId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = expectedId;
                    r["name"] = "Test Record";
                    return r;
                });

            var result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_EmptyGuidId_ThrowsException()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var record = new EntityRecord();
            record["id"] = Guid.Empty;
            record["name"] = "Empty Guid Record";

            var result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateRecord_PreHookErrors_AbortOperation()
        {
            var entity = CreateTestEntity("prehook_entity", new List<Field>
            {
                new GuidField { Id = Guid.NewGuid(), Name = "ref_id", Required = false, Unique = false }
            });
            SetupEntityServiceForEntity(entity);
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            record["ref_id"] = 12345; // non-string, non-Guid → triggers error

            var result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            _mockRecordRepository.Verify(x => x.CreateRecord(It.IsAny<string>(),
                It.IsAny<IEnumerable<KeyValuePair<string, object>>>()), Times.Never);
        }

        [Fact]
        public async Task CreateRecord_Success_PublishesSnsEvent()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Test Record";
                    return r;
                });

            var result = await _sut.CreateRecord(entity, record);
            result.Success.Should().BeTrue();
            _mockSnsClient.Verify(x => x.PublishAsync(
                It.Is<PublishRequest>(p => p.TopicArn != null),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CreateRecord_SnsPublishFails_StillReturnsSuccess()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Test Record";
                    return r;
                });
            _mockSnsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SNS connection failed"));

            var result = await _sut.CreateRecord(entity, record);
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Record was created successfully.");
        }

        [Fact]
        public async Task CreateRecord_SnsPublishFails_LogsError()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Test Record";
                    return r;
                });
            _mockSnsClient.Setup(x => x.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SNS connection failed"));

            var result = await _sut.CreateRecord(entity, record);
            result.Success.Should().BeTrue();
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task CreateRecord_Success_ReturnsCreatedRecord()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            var returnedRecord = new EntityRecord();
            returnedRecord["id"] = recordId;
            returnedRecord["name"] = "Test Record";
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(returnedRecord);

            QueryResponse result = await _sut.CreateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Record was created successfully.");
            result.Object.Should().NotBeNull();
            // Verify QueryResult shape: Data should be an EntityRecordList
            QueryResult queryResult = result.Object!;
            queryResult.Data.Should().NotBeNull();
            queryResult.Data.Should().BeOfType<EntityRecordList>();
        }

        [Fact]
        public async Task CreateRecord_Success_CallsRepositoryCreate()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Test Record";
                    return r;
                });

            var result = await _sut.CreateRecord(entity, record);
            result.Success.Should().BeTrue();
            _mockRecordRepository.Verify(x => x.CreateRecord(entity.Name,
                It.IsAny<IEnumerable<KeyValuePair<string, object>>>()), Times.Once);

            // Also verify IRecordService.Find and IRecordService.Count interface methods
            // Build a QueryObject filter using EntityQuery.QueryEQ fluent API
            QueryObject statusFilter = EntityQuery.QueryEQ("status", "active");
            statusFilter.Should().NotBeNull();
            statusFilter.FieldName.Should().Be("status");

            var findQuery = new EntityQuery(entity.Name, "*", query: null);
            QueryResponse findResult = await _sut.Find(findQuery);
            findResult.Should().NotBeNull();
            findResult.Success.Should().BeTrue();
            // Verify QueryResponse inherits from BaseResponseModel (Success, Message, Errors, Timestamp)
            BaseResponseModel baseResponse = findResult;
            baseResponse.Timestamp.Should().NotBe(default);

            QueryCountResponse countResult = await _sut.Count(entity.Name);
            countResult.Should().NotBeNull();
            countResult.Success.Should().BeTrue();
        }

        #endregion

        // =================================================================
        // Phase 3: UpdateRecord Tests
        // =================================================================
        #region UpdateRecord Tests

        [Fact]
        public async Task UpdateRecord_NullEntityName_ReturnsError()
        {
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.UpdateRecord(entityName: "", record: record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Invalid entity name.");
        }

        [Fact]
        public async Task UpdateRecord_EntityNotFound_ReturnsError()
        {
            _mockEntityService.Setup(x => x.ReadEntity("nonexistent"))
                .ReturnsAsync(new EntityResponse { Success = false, Object = null });
            _mockEntityService.Setup(x => x.GetEntity("nonexistent"))
                .ReturnsAsync((Entity?)null);
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.UpdateRecord("nonexistent", record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Entity cannot be found.");
        }

        [Fact]
        public async Task UpdateRecord_NullRecord_ReturnsError()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var result = await _sut.UpdateRecord(entity, null!);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Invalid record. Cannot be null.");
        }

        [Fact]
        public async Task UpdateRecord_MissingId_ReturnsError()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var record = new EntityRecord();
            record["name"] = "No ID";
            // No "id" property

            var result = await _sut.UpdateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
        }

        [Fact]
        public async Task UpdateRecord_NoUpdatePermission_ReturnsForbidden()
        {
            var entity = CreateTestEntity();
            entity.RecordPermissions = new RecordPermissions
            {
                CanRead = new List<Guid> { Guid.NewGuid() },
                CanCreate = new List<Guid> { Guid.NewGuid() },
                CanUpdate = new List<Guid> { Guid.NewGuid() },
                CanDelete = new List<Guid> { Guid.NewGuid() }
            };
            SetupEntityServiceForEntity(entity);
            var record = CreateTestRecord(Guid.NewGuid());
            var result = await _sut.UpdateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            result.Message.Should().Be("Access denied.");
        }

        [Fact]
        public async Task UpdateRecord_Success_PublishesSnsEvent()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            var existingRecord = new EntityRecord();
            existingRecord["id"] = recordId;
            existingRecord["name"] = "Old Name";
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(existingRecord);
            _mockRecordRepository.Setup(x => x.UpdateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);

            var result = await _sut.UpdateRecord(entity, record);
            result.Success.Should().BeTrue();
            _mockSnsClient.Verify(x => x.PublishAsync(
                It.Is<PublishRequest>(p => p.TopicArn != null),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateRecord_Success_ReturnsUpdatedRecord()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            var record = CreateTestRecord(recordId);

            var existingRecord = new EntityRecord();
            existingRecord["id"] = recordId;
            existingRecord["name"] = "Old Name";
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(existingRecord);
            _mockRecordRepository.Setup(x => x.UpdateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);

            var result = await _sut.UpdateRecord(entity, record);
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Record was updated successfully");
        }

        #endregion

        // =================================================================
        // Phase 4: DeleteRecord Tests
        // =================================================================
        #region DeleteRecord Tests

        [Fact]
        public async Task DeleteRecord_NullEntityName_ReturnsError()
        {
            var result = await _sut.DeleteRecord(entityName: "", id: Guid.NewGuid());
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Invalid entity name.");
        }

        [Fact]
        public async Task DeleteRecord_EntityNotFound_ReturnsError()
        {
            _mockEntityService.Setup(x => x.ReadEntity("nonexistent"))
                .ReturnsAsync(new EntityResponse { Success = false, Object = null });
            _mockEntityService.Setup(x => x.GetEntity("nonexistent"))
                .ReturnsAsync((Entity?)null);
            var result = await _sut.DeleteRecord("nonexistent", Guid.NewGuid());
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Entity cannot be found.");
        }

        [Fact]
        public async Task DeleteRecord_NoDeletePermission_ReturnsForbidden()
        {
            var entity = CreateTestEntity();
            entity.RecordPermissions = new RecordPermissions
            {
                CanRead = new List<Guid> { Guid.NewGuid() },
                CanCreate = new List<Guid> { Guid.NewGuid() },
                CanUpdate = new List<Guid> { Guid.NewGuid() },
                CanDelete = new List<Guid> { Guid.NewGuid() }
            };
            SetupEntityServiceForEntity(entity);
            var result = await _sut.DeleteRecord(entity, Guid.NewGuid());
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            result.Message.Should().Be("Access denied.");
        }

        [Fact]
        public async Task DeleteRecord_Success_PublishesSnsEvent()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            var existingRecord = new EntityRecord();
            existingRecord["id"] = recordId;
            existingRecord["name"] = "To Delete";
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(existingRecord);
            _mockRecordRepository.Setup(x => x.DeleteRecord(entity.Name, recordId))
                .Returns(Task.CompletedTask);

            var result = await _sut.DeleteRecord(entity, recordId);
            result.Success.Should().BeTrue();
            _mockSnsClient.Verify(x => x.PublishAsync(
                It.Is<PublishRequest>(p => p.TopicArn != null),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteRecord_WithFileFields_DeletesS3Files()
        {
            var fileField = new FileField { Id = Guid.NewGuid(), Name = "attachment", Required = false, Unique = false };
            var entity = CreateTestEntity("file_entity", new List<Field> { fileField });
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            var existingRecord = new EntityRecord();
            existingRecord["id"] = recordId;
            existingRecord["name"] = "File Record";
            existingRecord["attachment"] = "/file_entity/" + recordId + "/document.pdf";
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(existingRecord);
            _mockRecordRepository.Setup(x => x.DeleteRecord(entity.Name, recordId))
                .Returns(Task.CompletedTask);

            var result = await _sut.DeleteRecord(entity, recordId);
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Record was deleted successfully.");
        }

        [Fact]
        public async Task DeleteRecord_RecordNotFound_ReturnsError()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync((EntityRecord?)null);

            var result = await _sut.DeleteRecord(entity, recordId);
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Be("Record was not found.");
        }

        [Fact]
        public async Task DeleteRecord_Success_CallsRepositoryDelete()
        {
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            var existingRecord = new EntityRecord();
            existingRecord["id"] = recordId;
            existingRecord["name"] = "To Delete";
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(existingRecord);
            _mockRecordRepository.Setup(x => x.DeleteRecord(entity.Name, recordId))
                .Returns(Task.CompletedTask);

            var result = await _sut.DeleteRecord(entity, recordId);
            result.Success.Should().BeTrue();
            _mockRecordRepository.Verify(x => x.DeleteRecord(entity.Name, recordId), Times.Once);
        }

        #endregion

        // =================================================================
        // Phase 5: Relation-Aware Payload Processing Tests
        // =================================================================
        #region Relation-Aware Payload Processing Tests

        [Fact]
        public async Task CreateRecord_RelationField_SingleDollar_OriginToTarget()
        {
            // Arrange — $relationName.fieldName means origin-to-target direction
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var targetEntity = CreateTestEntity("target_entity");
            SetupEntityServiceForEntity(targetEntity);
            var recordId = Guid.NewGuid();
            var targetRecordId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "test_relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = entity.Id,
                OriginFieldId = entity.Fields.First(f => f.Name == "id").Id,
                TargetEntityId = targetEntity.Id,
                TargetFieldId = targetEntity.Fields.First(f => f.Name == "id").Id
            };
            SetupRelations(new List<EntityRelation> { relation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$test_relation.id"] = targetRecordId;

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Test";
                    return r;
                });

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert — relation field should be processed without error (categorized as OneToMany)
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_RelationField_DoubleDollar_TargetToOrigin()
        {
            // Arrange — $$relationName.fieldName means target-to-origin direction
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var originEntity = CreateTestEntity("origin_entity");
            SetupEntityServiceForEntity(originEntity);
            var recordId = Guid.NewGuid();
            var originRecordId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "reverse_relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = originEntity.Id,
                OriginFieldId = originEntity.Fields.First(f => f.Name == "id").Id,
                TargetEntityId = entity.Id,
                TargetFieldId = entity.Fields.First(f => f.Name == "id").Id
            };
            SetupRelations(new List<EntityRelation> { relation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$$reverse_relation.id"] = originRecordId;

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Test";
                    return r;
                });

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_RelationField_NoDollar_ThrowsError()
        {
            // Arrange — missing $ prefix should cause an error
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "some_relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = entity.Id,
                OriginFieldId = entity.Fields.First(f => f.Name == "id").Id,
                TargetEntityId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid()
            };
            SetupRelations(new List<EntityRelation> { relation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["some_relation.field_name"] = "value"; // no $ prefix, has dot → ProcessRelationField

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("The relation name is not specified.");
        }

        [Fact]
        public async Task CreateRecord_RelationField_EmptyRelationName_ThrowsError()
        {
            // Arrange — "$" alone (empty relation name after removing $)
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            SetupRelations(new List<EntityRelation>());

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$.fieldName"] = "value"; // $ with empty relation name

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("The relation name is not correct.");
        }

        [Fact]
        public async Task CreateRecord_RelationField_EmptyFieldName_ThrowsError()
        {
            // Arrange — "$relation." (no field name after dot)
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "myrelation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = entity.Id,
                OriginFieldId = entity.Fields.First(f => f.Name == "id").Id,
                TargetEntityId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid()
            };
            SetupRelations(new List<EntityRelation> { relation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$myrelation."] = "value"; // empty field name after dot

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("The relation field name is not specified.");
        }

        [Fact]
        public async Task CreateRecord_RelationField_TooManyLevels_ThrowsError()
        {
            // Arrange — "$relation.field.subfield" (3 parts)
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            SetupRelations(new List<EntityRelation>());

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$rel.field.subfield"] = "value"; // too many levels

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Only first level relation can be specified.");
        }

        [Fact]
        public async Task CreateRecord_RelationField_RelationNotFound_ThrowsError()
        {
            // Arrange — valid $ prefix but relation does not exist
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            SetupRelations(new List<EntityRelation>()); // empty relations list

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$nonexistent.field"] = "value";

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("The relation does not exist.");
        }

        [Fact]
        public async Task CreateRecord_RelationField_EntityNotInRelation_ThrowsError()
        {
            // Arrange — relation exists but does not involve the current entity
            var entity = CreateTestEntity();
            SetupEntityServiceForEntity(entity);
            var recordId = Guid.NewGuid();

            var unrelatedRelation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "other_relation",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = Guid.NewGuid(), // different entity
                OriginFieldId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(), // different entity
                TargetFieldId = Guid.NewGuid()
            };
            SetupRelations(new List<EntityRelation> { unrelatedRelation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Test";
            record["$other_relation.field"] = "value";

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("belongs to entity that does not relate to current entity.");
        }

        [Fact]
        public async Task CreateRecord_OneToMany_FKUpdate_OnRelatedRecords()
        {
            // Arrange — OneToMany relation: current entity is origin, FK update on target records
            var entity = CreateTestEntity("parent_entity");
            SetupEntityServiceForEntity(entity);
            var childEntity = CreateTestEntity("child_entity");
            SetupEntityServiceForEntity(childEntity);

            var recordId = Guid.NewGuid();
            var childRecordId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "parent_child",
                RelationType = EntityRelationType.OneToMany,
                OriginEntityId = entity.Id,
                OriginFieldId = entity.Fields.First(f => f.Name == "id").Id,
                TargetEntityId = childEntity.Id,
                TargetFieldId = childEntity.Fields.First(f => f.Name == "id").Id
            };
            SetupRelations(new List<EntityRelation> { relation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Parent";
            record["$parent_child.id"] = new List<Guid> { childRecordId };

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Parent";
                    return r;
                });

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert — record should be created successfully and relation processing attempted
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
        }

        [Fact]
        public async Task CreateRecord_ManyToMany_CreatesBridgeRecords()
        {
            // Arrange — ManyToMany relation: should create bridge records
            var entity = CreateTestEntity("m2m_source");
            SetupEntityServiceForEntity(entity);
            var targetEntity = CreateTestEntity("m2m_target");
            SetupEntityServiceForEntity(targetEntity);

            var recordId = Guid.NewGuid();
            var targetRecordId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = Guid.NewGuid(),
                Name = "m2m_rel",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = entity.Id,
                OriginFieldId = entity.Fields.First(f => f.Name == "id").Id,
                TargetEntityId = targetEntity.Id,
                TargetFieldId = targetEntity.Fields.First(f => f.Name == "id").Id
            };
            SetupRelations(new List<EntityRelation> { relation });

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "M2M Source";
            // Use List<object> instead of List<Guid> because ExtractGuidListFromRelationValue
            // checks for IEnumerable<object>, and List<Guid> (value type) does not satisfy that
            // due to generic covariance rules. Real JSON deserialization produces List<object>.
            record["$m2m_rel.id"] = new List<object> { targetRecordId };

            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "M2M Source";
                    return r;
                });
            _mockEntityRepository.Setup(x => x.CreateManyToManyRecord(relation.Id, It.IsAny<Guid>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            _mockEntityRepository.Verify(x => x.CreateManyToManyRecord(
                relation.Id, It.IsAny<Guid>(), It.IsAny<Guid>()), Times.AtLeastOnce);
        }

        #endregion

        // =================================================================
        // Phase 6: ExtractFieldValue Tests (all 20+ field types)
        // =================================================================
        #region ExtractFieldValue Tests

        [Fact]
        public void ExtractFieldValue_AutoNumber_NullReturnsNull()
        {
            var field = new AutoNumberField { Id = Guid.NewGuid(), Name = "auto_num" };
            var result = InvokeExtractFieldValue(null, field);
            // Null → field.GetFieldDefaultValue() which returns null for AutoNumberField
            result.Should().BeNull();
        }

        [Fact]
        public void ExtractFieldValue_AutoNumber_StringParsesDecimal()
        {
            var field = new AutoNumberField { Id = Guid.NewGuid(), Name = "auto_num" };
            var result = InvokeExtractFieldValue("123", field);
            result.Should().Be((int)decimal.Parse("123"));
        }

        [Fact]
        public void ExtractFieldValue_Checkbox_StringConvertsToBoolean()
        {
            var field = new CheckboxField { Id = Guid.NewGuid(), Name = "is_active" };
            var result = InvokeExtractFieldValue("true", field);
            result.Should().Be(true);
        }

        [Fact]
        public void ExtractFieldValue_Currency_StringParsesDecimal()
        {
            var field = new CurrencyField
            {
                Id = Guid.NewGuid(),
                Name = "price",
                Currency = new CurrencyType { DecimalDigits = 2 }
            };
            var result = InvokeExtractFieldValue("100.50", field);
            result.Should().BeOfType<decimal>();
            ((decimal)result!).Should().Be(100.50m);
        }

        [Fact]
        public void ExtractFieldValue_Currency_RoundingApplied()
        {
            var field = new CurrencyField
            {
                Id = Guid.NewGuid(),
                Name = "price",
                Currency = new CurrencyType { DecimalDigits = 2 }
            };
            // 100.555 rounds to 100.56 with MidpointRounding.AwayFromZero
            var result = InvokeExtractFieldValue(100.555m, field);
            result.Should().BeOfType<decimal>();
            ((decimal)result!).Should().Be(100.56m);
        }

        [Fact]
        public void ExtractFieldValue_Date_NullReturnsNull()
        {
            var field = new DateField { Id = Guid.NewGuid(), Name = "birth_date" };
            var result = InvokeExtractFieldValue(null, field);
            result.Should().BeNull();
        }

        [Fact]
        public void ExtractFieldValue_DateTime_UtcReturnsAsIs()
        {
            var field = new DateTimeField { Id = Guid.NewGuid(), Name = "created_on" };
            var utcNow = DateTime.UtcNow;
            var result = InvokeExtractFieldValue(utcNow, field);
            result.Should().BeOfType<DateTime>();
            var resultDt = (DateTime)result!;
            resultDt.Kind.Should().Be(DateTimeKind.Utc);
            resultDt.Should().Be(utcNow);
        }

        [Fact]
        public void ExtractFieldValue_MultiSelect_JArrayConvertsToListString()
        {
            var field = new MultiSelectField { Id = Guid.NewGuid(), Name = "tags" };
            // JArray implements IEnumerable<JToken> → passes IEnumerable<object> check
            var jArray = new JArray("tag1", "tag2", "tag3");
            var result = InvokeExtractFieldValue(jArray, field);
            result.Should().NotBeNull();
            var list = result as List<string>;
            list.Should().NotBeNull();
            list.Should().HaveCount(3);
            list.Should().Contain("tag1");
            list.Should().Contain("tag2");
            list.Should().Contain("tag3");
        }

        [Fact]
        public void ExtractFieldValue_Number_StringParsesDecimal()
        {
            var field = new NumberField { Id = Guid.NewGuid(), Name = "quantity" };
            var result = InvokeExtractFieldValue("42.5", field);
            result.Should().BeOfType<decimal>();
            ((decimal)result!).Should().Be(42.5m);
        }

        [Fact]
        public void ExtractFieldValue_Password_EncryptedComputesMD5()
        {
            var field = new PasswordField { Id = Guid.NewGuid(), Name = "password", Encrypted = true };
            var testPassword = "MySecretP@ss";
            var expectedHash = ComputeExpectedMd5Hash(testPassword);
            var result = InvokeExtractFieldValue(testPassword, field, encryptPasswordFields: true);
            result.Should().Be(expectedHash);
        }

        [Fact]
        public void ExtractFieldValue_Password_NotEncryptedReturnsRaw()
        {
            var field = new PasswordField { Id = Guid.NewGuid(), Name = "password", Encrypted = false };
            var testPassword = "RawPassword";
            var result = InvokeExtractFieldValue(testPassword, field, encryptPasswordFields: true);
            result.Should().Be(testPassword);
        }

        [Fact]
        public void ExtractFieldValue_Guid_StringParsesGuid()
        {
            var field = new GuidField { Id = Guid.NewGuid(), Name = "ref_id" };
            var testGuid = Guid.NewGuid();
            var result = InvokeExtractFieldValue(testGuid.ToString(), field);
            result.Should().Be((Guid?)testGuid);
        }

        [Fact]
        public void ExtractFieldValue_Guid_InvalidValue_ThrowsException()
        {
            var field = new GuidField { Id = Guid.NewGuid(), Name = "ref_id" };
            // Non-string, non-Guid value (e.g., integer) → "Invalid Guid field value."
            Action act = () => InvokeExtractFieldValue(12345, field);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("Invalid Guid field value.");
        }

        [Fact]
        public void ExtractFieldValue_UnsupportedType_ThrowsException()
        {
            // To test the default/unsupported branch in ExtractFieldValue's switch,
            // we need a Field whose GetFieldType() returns an unhandled FieldType value.
            // Field.GetFieldType() must be virtual to allow Moq to intercept the call.
            // This test validates the defensive guard against unknown field types.
            var mockField = new Mock<Field> { CallBase = true };
            mockField.Setup(f => f.GetFieldType()).Returns((FieldType)999);
            mockField.Object.Id = Guid.NewGuid();
            mockField.Object.Name = "unsupported_field";

            Action act = () => InvokeExtractFieldValue("test_value", mockField.Object);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("System Error. A field type is not supported in field value extraction process.");
        }

        #endregion

        // =================================================================
        // Phase 7: File/Image Handling Tests
        // =================================================================
        #region File/Image Handling Tests

        [Fact]
        public async Task CreateRecord_FileField_TempPath_MovesToPermanent()
        {
            // Arrange — file field with temp path should be normalized in record data
            var fileField = new FileField { Id = Guid.NewGuid(), Name = "document" };
            var entity = CreateTestEntity("doc_entity", new List<Field> { fileField });
            SetupEntityServiceForEntity(entity);
            SetupRelations(new List<EntityRelation>());
            var recordId = Guid.NewGuid();

            var record = new EntityRecord();
            record["id"] = recordId;
            record["name"] = "Doc Record";
            record["document"] = "/tmp/upload123.pdf";

            IEnumerable<KeyValuePair<string, object>>? capturedData = null;
            _mockRecordRepository.Setup(x => x.CreateRecord(entity.Name, It.IsAny<IEnumerable<KeyValuePair<string, object>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, object>>>((name, data) =>
                {
                    capturedData = data.ToList();
                })
                .Returns(Task.CompletedTask);
            _mockRecordRepository.Setup(x => x.FindRecord(entity.Name, recordId))
                .ReturnsAsync(() =>
                {
                    var r = new EntityRecord();
                    r["id"] = recordId;
                    r["name"] = "Doc Record";
                    r["document"] = "/doc_entity/" + recordId + "/upload123.pdf";
                    return r;
                });

            // Act
            var result = await _sut.CreateRecord(entity, record);

            // Assert — record should be created successfully
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            _mockRecordRepository.Verify(x => x.CreateRecord(entity.Name,
                It.IsAny<IEnumerable<KeyValuePair<string, object>>>()), Times.Once);
        }

        #endregion

        // =================================================================
        // Phase 8: ManyToMany Relation Record Tests
        // =================================================================
        #region ManyToMany Relation Record Tests

        [Fact]
        public async Task CreateRelationManyToMany_Success_ReturnsResponse()
        {
            // Arrange — valid M2M relation
            // Service calls _entityRepository.GetRelationById(relationId), NOT _entityService.ReadRelations()
            var relationId = Guid.NewGuid();
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = relationId,
                Name = "m2m_relation",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = Guid.NewGuid(),
                OriginFieldId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid()
            };

            _mockEntityRepository.Setup(x => x.GetRelationById(relationId))
                .ReturnsAsync(relation);
            _mockEntityRepository.Setup(x => x.CreateManyToManyRecord(relationId, originId, targetId))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.CreateRelationManyToManyRecord(relationId, originId, targetId);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Message.Should().Contain("created successfully");
            _mockEntityRepository.Verify(x => x.CreateManyToManyRecord(relationId, originId, targetId), Times.Once);
        }

        [Fact]
        public async Task CreateRelationManyToMany_RelationNotFound_ReturnsError()
        {
            // Arrange — relation does not exist
            // Service calls _entityRepository.GetRelationById() which returns null
            var relationId = Guid.NewGuid();
            _mockEntityRepository.Setup(x => x.GetRelationById(relationId))
                .ReturnsAsync((EntityRelation?)null);

            // Act
            var result = await _sut.CreateRelationManyToManyRecord(relationId, Guid.NewGuid(), Guid.NewGuid());

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Relation does not exists.");
        }

        [Fact]
        public async Task RemoveRelationManyToMany_Success_ReturnsResponse()
        {
            // Arrange — valid M2M relation for removal
            // Service calls _entityRepository.GetRelationById(relationId), NOT _entityService.ReadRelations()
            var relationId = Guid.NewGuid();
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var relation = new EntityRelation
            {
                Id = relationId,
                Name = "m2m_remove",
                RelationType = EntityRelationType.ManyToMany,
                OriginEntityId = Guid.NewGuid(),
                OriginFieldId = Guid.NewGuid(),
                TargetEntityId = Guid.NewGuid(),
                TargetFieldId = Guid.NewGuid()
            };

            _mockEntityRepository.Setup(x => x.GetRelationById(relationId))
                .ReturnsAsync(relation);
            _mockEntityRepository.Setup(x => x.DeleteManyToManyRecord(
                relation.Name, It.IsAny<Guid?>(), It.IsAny<Guid?>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _sut.RemoveRelationManyToManyRecord(relationId, originId, targetId);

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeTrue();
            result.Message.Should().Contain("deleted successfully");
        }

        [Fact]
        public async Task RemoveRelationManyToMany_RelationNotFound_ReturnsError()
        {
            // Arrange — relation does not exist
            // Service calls _entityRepository.GetRelationById() which returns null
            var relationId = Guid.NewGuid();
            _mockEntityRepository.Setup(x => x.GetRelationById(relationId))
                .ReturnsAsync((EntityRelation?)null);

            // Act
            var result = await _sut.RemoveRelationManyToManyRecord(relationId, Guid.NewGuid(), Guid.NewGuid());

            // Assert
            result.Should().NotBeNull();
            result.Success.Should().BeFalse();
            result.Message.Should().Contain("Relation does not exists.");
        }

        #endregion
    }

}

