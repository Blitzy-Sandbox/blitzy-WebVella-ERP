using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Events
{
    /// <summary>
    /// Comprehensive xUnit tests for all 12 domain event contracts in
    /// <c>WebVella.Erp.SharedKernel.Contracts.Events</c> that replace the monolith's
    /// 12 hook interfaces from <c>WebVella.Erp/Hooks/</c>.
    ///
    /// Validates:
    ///   - IDomainEvent interface implementation (Phase 1)
    ///   - JSON round-trip serialization with [JsonProperty] camelCase annotations (Phase 2)
    ///   - Property contracts matching original hook interface parameters (Phase 3)
    ///   - Distributed tracing metadata (CorrelationId, Timestamp) (Phase 4)
    ///   - Event type discriminator uniqueness and namespace correctness (Phase 5)
    ///   - MassTransit compatibility (parameterless ctors, public get/set) (Phase 6)
    ///   - Pre-operation vs post-operation validation error semantics (Phase 7)
    ///   - Nullable Guid specificity for relation create vs delete events (Phase 8)
    /// </summary>
    public class DomainEventContractTests
    {
        #region Shared test data — event type collections

        /// <summary>
        /// All 12 domain event types that replace the monolith's hook interfaces.
        /// </summary>
        private static readonly Type[] AllEventTypes = new[]
        {
            typeof(PreRecordCreateEvent),    // replaces IErpPreCreateRecordHook
            typeof(RecordCreatedEvent),      // replaces IErpPostCreateRecordHook
            typeof(PreRecordUpdateEvent),    // replaces IErpPreUpdateRecordHook
            typeof(RecordUpdatedEvent),      // replaces IErpPostUpdateRecordHook
            typeof(PreRecordDeleteEvent),    // replaces IErpPreDeleteRecordHook
            typeof(RecordDeletedEvent),      // replaces IErpPostDeleteRecordHook
            typeof(PreRecordSearchEvent),    // replaces IErpPreSearchRecordHook
            typeof(RecordSearchEvent),       // replaces IErpPostSearchRecordHook
            typeof(PreRelationCreateEvent),  // replaces IErpPreCreateManyToManyRelationHook
            typeof(RelationCreatedEvent),    // replaces IErpPostCreateManyToManyRelationHook
            typeof(PreRelationDeleteEvent),  // replaces IErpPreDeleteManyToManyRelationHook
            typeof(RelationDeletedEvent)     // replaces IErpPostDeleteManyToManyRelationHook
        };

        /// <summary>
        /// Pre-operation event types that carry ValidationErrors or EqlErrors
        /// allowing subscribers to block the pending operation.
        /// </summary>
        private static readonly Type[] PreOperationEventTypes = new[]
        {
            typeof(PreRecordCreateEvent),
            typeof(PreRecordUpdateEvent),
            typeof(PreRecordDeleteEvent),
            typeof(PreRecordSearchEvent),
            typeof(PreRelationCreateEvent),
            typeof(PreRelationDeleteEvent)
        };

        /// <summary>
        /// Post-operation event types that carry immutable result data
        /// and do NOT include ValidationErrors.
        /// </summary>
        private static readonly Type[] PostOperationEventTypes = new[]
        {
            typeof(RecordCreatedEvent),
            typeof(RecordUpdatedEvent),
            typeof(RecordDeletedEvent),
            typeof(RecordSearchEvent),
            typeof(RelationCreatedEvent),
            typeof(RelationDeletedEvent)
        };

        #endregion

        #region Helper methods

        /// <summary>
        /// Creates a test EntityRecord with sample key-value data for use in
        /// round-trip serialization and property contract tests.
        /// </summary>
        private static EntityRecord CreateTestEntityRecord(string name = "test_record")
        {
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            record["name"] = name;
            record["created_on"] = DateTimeOffset.UtcNow;
            return record;
        }

        /// <summary>
        /// Asserts that a given type has a public instance property with the specified name and type.
        /// </summary>
        private static PropertyInfo AssertPropertyExists(Type type, string propertyName, Type expectedType)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            prop.Should().NotBeNull(because: $"{type.Name} must have a public property '{propertyName}'");
            prop.PropertyType.Should().Be(expectedType,
                because: $"{type.Name}.{propertyName} must be of type {expectedType.Name}");
            return prop;
        }

        /// <summary>
        /// Asserts that a given type does NOT have a public instance property with the specified name.
        /// </summary>
        private static void AssertPropertyDoesNotExist(Type type, string propertyName)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            prop.Should().BeNull(because: $"{type.Name} should NOT have a '{propertyName}' property (post-operation event)");
        }

        #endregion

        // =====================================================================
        // Phase 1: Event Contract Completeness Tests
        // =====================================================================

        #region Phase 1 — IDomainEvent implementation

        /// <summary>
        /// Verifies that all 12 event contract classes implement the IDomainEvent interface,
        /// and that exactly 12 event types exist (no missing, no extra).
        /// </summary>
        [Fact]
        public void AllEventTypes_ShouldImplementIDomainEvent()
        {
            // Verify exactly 12 event types are defined
            AllEventTypes.Should().HaveCount(12,
                because: "there must be exactly 12 domain event types replacing the 12 monolith hook interfaces");

            // Verify each type implements IDomainEvent
            foreach (var eventType in AllEventTypes)
            {
                eventType.Should().Implement<IDomainEvent>(
                    because: $"{eventType.Name} must implement IDomainEvent for message bus routing");
            }
        }

        #endregion

        // =====================================================================
        // Phase 2: JSON Round-Trip Serialization Tests
        // =====================================================================

        #region Phase 2 — Record CRUD event round-trip serialization

        /// <summary>
        /// Verifies PreRecordCreateEvent (replacing IErpPreCreateRecordHook) survives JSON round-trip
        /// with all properties preserved and camelCase [JsonProperty] annotations working correctly.
        /// </summary>
        [Fact]
        public void PreRecordCreateEvent_ShouldRoundTripSerialize()
        {
            var original = new PreRecordCreateEvent
            {
                EntityName = "test_entity",
                Record = CreateTestEntityRecord(),
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("key1", "val1", "msg1")
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<PreRecordCreateEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.Record.Should().NotBeNull();
            deserialized.ValidationErrors.Should().HaveCount(1);
            deserialized.ValidationErrors[0].Key.Should().Be("key1");
            deserialized.ValidationErrors[0].Value.Should().Be("val1");
            deserialized.ValidationErrors[0].Message.Should().Be("msg1");

            // Verify camelCase JSON property names from [JsonProperty] annotations
            json.Should().Contain("\"entityName\"");
            json.Should().Contain("\"correlationId\"");
            json.Should().Contain("\"timestamp\"");
            json.Should().Contain("\"validationErrors\"");
            json.Should().Contain("\"record\"");
        }

        /// <summary>
        /// Verifies RecordCreatedEvent (replacing IErpPostCreateRecordHook) round-trip serialization.
        /// Confirms NO ValidationErrors property exists on this post-operation event.
        /// </summary>
        [Fact]
        public void RecordCreatedEvent_ShouldRoundTripSerialize()
        {
            var original = new RecordCreatedEvent
            {
                EntityName = "test_entity",
                Record = CreateTestEntityRecord(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<RecordCreatedEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.Record.Should().NotBeNull();

            // Verify camelCase JSON property names
            json.Should().Contain("\"entityName\"");
            json.Should().Contain("\"record\"");

            // Post-operation event: no ValidationErrors property
            json.Should().NotContain("\"validationErrors\"");
        }

        /// <summary>
        /// Verifies PreRecordUpdateEvent (replacing IErpPreUpdateRecordHook) round-trip serialization.
        /// </summary>
        [Fact]
        public void PreRecordUpdateEvent_ShouldRoundTripSerialize()
        {
            var original = new PreRecordUpdateEvent
            {
                EntityName = "test_entity",
                Record = CreateTestEntityRecord(),
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("field_required", "name", "Name is required")
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<PreRecordUpdateEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.Record.Should().NotBeNull();
            deserialized.ValidationErrors.Should().HaveCount(1);
            deserialized.ValidationErrors[0].Key.Should().Be("field_required");

            // Verify camelCase JSON property names
            json.Should().Contain("\"entityName\"");
            json.Should().Contain("\"validationErrors\"");
        }

        /// <summary>
        /// Verifies RecordUpdatedEvent (replacing IErpPostUpdateRecordHook) round-trip serialization.
        /// ENRICHED from source hook: carries both OldRecord and NewRecord instead of single record.
        /// </summary>
        [Fact]
        public void RecordUpdatedEvent_ShouldRoundTripSerialize()
        {
            var original = new RecordUpdatedEvent
            {
                EntityName = "test_entity",
                OldRecord = CreateTestEntityRecord("old_state"),
                NewRecord = CreateTestEntityRecord("new_state"),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<RecordUpdatedEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.OldRecord.Should().NotBeNull();
            deserialized.NewRecord.Should().NotBeNull();

            // Verify camelCase JSON property names for enriched properties
            json.Should().Contain("\"oldRecord\"");
            json.Should().Contain("\"newRecord\"");

            // Post-operation event: no ValidationErrors property
            json.Should().NotContain("\"validationErrors\"");
        }

        /// <summary>
        /// Verifies PreRecordDeleteEvent (replacing IErpPreDeleteRecordHook) round-trip serialization.
        /// </summary>
        [Fact]
        public void PreRecordDeleteEvent_ShouldRoundTripSerialize()
        {
            var original = new PreRecordDeleteEvent
            {
                EntityName = "test_entity",
                Record = CreateTestEntityRecord(),
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("cannot_delete", "id", "Record is referenced by other records")
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<PreRecordDeleteEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.Record.Should().NotBeNull();
            deserialized.ValidationErrors.Should().HaveCount(1);
            deserialized.ValidationErrors[0].Message.Should().Be("Record is referenced by other records");
        }

        /// <summary>
        /// Verifies RecordDeletedEvent (replacing IErpPostDeleteRecordHook) round-trip serialization.
        /// SIMPLIFIED from source hook: carries only RecordId (Guid) instead of full EntityRecord.
        /// </summary>
        [Fact]
        public void RecordDeletedEvent_ShouldRoundTripSerialize()
        {
            var recordId = Guid.NewGuid();
            var original = new RecordDeletedEvent
            {
                EntityName = "test_entity",
                RecordId = recordId,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<RecordDeletedEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.RecordId.Should().Be(recordId);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);

            // Verify camelCase JSON property name for simplified RecordId
            json.Should().Contain("\"recordId\"");
        }

        #endregion

        #region Phase 2 — Search event round-trip serialization

        /// <summary>
        /// Verifies PreRecordSearchEvent (replacing IErpPreSearchRecordHook) round-trip serialization.
        /// EqlTree is stored as a string (serialized EqlSelectNode) and EqlErrors as List&lt;EqlError&gt;.
        /// </summary>
        [Fact]
        public void PreRecordSearchEvent_ShouldRoundTripSerialize()
        {
            var original = new PreRecordSearchEvent
            {
                EntityName = "test_entity",
                EqlTree = "* from test_entity",
                EqlErrors = new List<EqlError>
                {
                    new EqlError { Message = "Syntax error", Line = 1, Column = 5 }
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<PreRecordSearchEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.EqlTree.Should().Be("* from test_entity");
            deserialized.EqlErrors.Should().HaveCount(1);
            deserialized.EqlErrors[0].Message.Should().Be("Syntax error");
            deserialized.EqlErrors[0].Line.Should().Be(1);
            deserialized.EqlErrors[0].Column.Should().Be(5);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);

            // Verify camelCase JSON property names
            json.Should().Contain("\"eqlTree\"");
            json.Should().Contain("\"eqlErrors\"");
        }

        /// <summary>
        /// Verifies RecordSearchEvent (replacing IErpPostSearchRecordHook) round-trip serialization
        /// with Results as List&lt;EntityRecord&gt;, preserving list count.
        /// </summary>
        [Fact]
        public void RecordSearchEvent_ShouldRoundTripSerialize()
        {
            var original = new RecordSearchEvent
            {
                EntityName = "test_entity",
                Results = new List<EntityRecord>
                {
                    CreateTestEntityRecord("record_1"),
                    CreateTestEntityRecord("record_2")
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<RecordSearchEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.EntityName.Should().Be(original.EntityName);
            deserialized.Results.Should().HaveCount(2);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
            deserialized.Timestamp.Should().Be(original.Timestamp);

            // Verify camelCase JSON property name
            json.Should().Contain("\"results\"");
        }

        #endregion

        #region Phase 2 — Relation event round-trip serialization

        /// <summary>
        /// Verifies PreRelationCreateEvent (replacing IErpPreCreateManyToManyRelationHook) round-trip.
        /// OriginId and TargetId are NON-nullable Guid matching source hook signature.
        /// </summary>
        [Fact]
        public void PreRelationCreateEvent_ShouldRoundTripSerialize()
        {
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var original = new PreRelationCreateEvent
            {
                RelationName = "test_relation",
                OriginId = originId,
                TargetId = targetId,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("duplicate", "relation", "Relation already exists")
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<PreRelationCreateEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.RelationName.Should().Be("test_relation");
            deserialized.OriginId.Should().Be(originId);
            deserialized.TargetId.Should().Be(targetId);
            deserialized.ValidationErrors.Should().HaveCount(1);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);

            // Verify camelCase JSON property names
            json.Should().Contain("\"relationName\"");
            json.Should().Contain("\"originId\"");
            json.Should().Contain("\"targetId\"");
        }

        /// <summary>
        /// Verifies RelationCreatedEvent (replacing IErpPostCreateManyToManyRelationHook) round-trip.
        /// NON-nullable Guid for both IDs matching source hook: Guid originId, Guid targetId.
        /// </summary>
        [Fact]
        public void RelationCreatedEvent_ShouldRoundTripSerialize()
        {
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var original = new RelationCreatedEvent
            {
                RelationName = "test_relation",
                OriginId = originId,
                TargetId = targetId,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<RelationCreatedEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.RelationName.Should().Be("test_relation");
            deserialized.OriginId.Should().Be(originId);
            deserialized.TargetId.Should().Be(targetId);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);

            // Post-operation event: no ValidationErrors
            json.Should().NotContain("\"validationErrors\"");
        }

        /// <summary>
        /// Verifies PreRelationDeleteEvent (replacing IErpPreDeleteManyToManyRelationHook) round-trip.
        /// CRITICAL: OriginId and TargetId are NULLABLE Guid? matching source hook: Guid? originId, Guid? targetId.
        /// Tests both non-null and null values for nullable Guids.
        /// </summary>
        [Fact]
        public void PreRelationDeleteEvent_ShouldRoundTripSerialize()
        {
            var originId = Guid.NewGuid();
            var targetId = Guid.NewGuid();

            var original = new PreRelationDeleteEvent
            {
                RelationName = "test_relation",
                OriginId = originId,
                TargetId = targetId,
                ValidationErrors = new List<ErrorModel>
                {
                    new ErrorModel("in_use", "relation", "Relation is still in use")
                },
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<PreRelationDeleteEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.RelationName.Should().Be("test_relation");
            deserialized.OriginId.Should().Be(originId);
            deserialized.TargetId.Should().Be(targetId);
            deserialized.ValidationErrors.Should().HaveCount(1);
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
        }

        /// <summary>
        /// Verifies RelationDeletedEvent (replacing IErpPostDeleteManyToManyRelationHook) round-trip.
        /// CRITICAL: NULLABLE Guid? for both IDs matching source hook: Guid? originId, Guid? targetId.
        /// Tests with one null TargetId to verify nullable serialization survives round-trip.
        /// </summary>
        [Fact]
        public void RelationDeletedEvent_ShouldRoundTripSerialize()
        {
            var originId = Guid.NewGuid();

            var original = new RelationDeletedEvent
            {
                RelationName = "test_relation",
                OriginId = originId,
                TargetId = null,   // Nullable — null value to verify round-trip
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<RelationDeletedEvent>(json);

            deserialized.Should().NotBeNull();
            deserialized.RelationName.Should().Be("test_relation");
            deserialized.OriginId.Should().Be(originId);
            deserialized.TargetId.Should().BeNull("nullable Guid? null value must survive JSON round-trip");
            deserialized.CorrelationId.Should().Be(original.CorrelationId);
        }

        #endregion

        // =====================================================================
        // Phase 3: Event Properties Match Source Hook Interface Parameters
        // =====================================================================

        #region Phase 3 — Record hook property mapping

        /// <summary>
        /// Source: IErpPreCreateRecordHook.OnPreCreateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)
        /// Verifies PreRecordCreateEvent has matching properties: EntityName (string), Record (EntityRecord), ValidationErrors (List&lt;ErrorModel&gt;).
        /// </summary>
        [Fact]
        public void PreRecordCreateEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(PreRecordCreateEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "Record", typeof(EntityRecord));
            AssertPropertyExists(type, "ValidationErrors", typeof(List<ErrorModel>));
        }

        /// <summary>
        /// Source: IErpPostCreateRecordHook.OnPostCreateRecord(string entityName, EntityRecord record)
        /// Verifies RecordCreatedEvent has: EntityName (string), Record (EntityRecord).
        /// </summary>
        [Fact]
        public void RecordCreatedEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(RecordCreatedEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "Record", typeof(EntityRecord));
        }

        /// <summary>
        /// Source: IErpPreUpdateRecordHook.OnPreUpdateRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)
        /// Verifies PreRecordUpdateEvent has: EntityName, Record, ValidationErrors.
        /// </summary>
        [Fact]
        public void PreRecordUpdateEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(PreRecordUpdateEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "Record", typeof(EntityRecord));
            AssertPropertyExists(type, "ValidationErrors", typeof(List<ErrorModel>));
        }

        /// <summary>
        /// Source: IErpPostUpdateRecordHook.OnPostUpdateRecord(string entityName, EntityRecord record)
        /// ENRICHED: RecordUpdatedEvent has OldRecord and NewRecord instead of single record parameter.
        /// </summary>
        [Fact]
        public void RecordUpdatedEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(RecordUpdatedEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "OldRecord", typeof(EntityRecord));
            AssertPropertyExists(type, "NewRecord", typeof(EntityRecord));
        }

        /// <summary>
        /// Source: IErpPreDeleteRecordHook.OnPreDeleteRecord(string entityName, EntityRecord record, List&lt;ErrorModel&gt; errors)
        /// Verifies PreRecordDeleteEvent has: EntityName, Record (EntityRecord), ValidationErrors.
        /// </summary>
        [Fact]
        public void PreRecordDeleteEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(PreRecordDeleteEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "Record", typeof(EntityRecord));
            AssertPropertyExists(type, "ValidationErrors", typeof(List<ErrorModel>));
        }

        /// <summary>
        /// Source: IErpPostDeleteRecordHook.OnPostDeleteRecord(string entityName, EntityRecord record)
        /// SIMPLIFIED: RecordDeletedEvent carries RecordId (Guid) instead of full EntityRecord.
        /// </summary>
        [Fact]
        public void RecordDeletedEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(RecordDeletedEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "RecordId", typeof(Guid));
        }

        #endregion

        #region Phase 3 — Search hook property mapping

        /// <summary>
        /// Source: IErpPreSearchRecordHook.OnPreSearchRecord(string entityName, EqlSelectNode tree, List&lt;EqlError&gt; errors)
        /// EqlTree is string (serialized EqlSelectNode), EqlErrors is List&lt;EqlError&gt;.
        /// </summary>
        [Fact]
        public void PreRecordSearchEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(PreRecordSearchEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "EqlTree", typeof(string));
            AssertPropertyExists(type, "EqlErrors", typeof(List<EqlError>));
        }

        /// <summary>
        /// Source: IErpPostSearchRecordHook.OnPostSearchRecord(string entityName, List&lt;EntityRecord&gt; record)
        /// Results renamed from "record" for semantic clarity.
        /// </summary>
        [Fact]
        public void RecordSearchEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(RecordSearchEvent);
            AssertPropertyExists(type, "EntityName", typeof(string));
            AssertPropertyExists(type, "Results", typeof(List<EntityRecord>));
        }

        #endregion

        #region Phase 3 — Relation hook property mapping

        /// <summary>
        /// Source: IErpPreCreateManyToManyRelationHook.OnPreCreate(string relationName, Guid originId, Guid targetId, List&lt;ErrorModel&gt; errors)
        /// NON-nullable Guid for create events.
        /// </summary>
        [Fact]
        public void PreRelationCreateEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(PreRelationCreateEvent);
            AssertPropertyExists(type, "RelationName", typeof(string));
            AssertPropertyExists(type, "OriginId", typeof(Guid));
            AssertPropertyExists(type, "TargetId", typeof(Guid));
            AssertPropertyExists(type, "ValidationErrors", typeof(List<ErrorModel>));
        }

        /// <summary>
        /// Source: IErpPostCreateManyToManyRelationHook.OnPostCreate(string relationName, Guid originId, Guid targetId)
        /// NON-nullable Guid for create events.
        /// </summary>
        [Fact]
        public void RelationCreatedEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(RelationCreatedEvent);
            AssertPropertyExists(type, "RelationName", typeof(string));
            AssertPropertyExists(type, "OriginId", typeof(Guid));
            AssertPropertyExists(type, "TargetId", typeof(Guid));
        }

        /// <summary>
        /// Source: IErpPreDeleteManyToManyRelationHook.OnPreDelete(string relationName, Guid? originId, Guid? targetId, List&lt;ErrorModel&gt; errors)
        /// NULLABLE Guid? for delete events.
        /// </summary>
        [Fact]
        public void PreRelationDeleteEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(PreRelationDeleteEvent);
            AssertPropertyExists(type, "RelationName", typeof(string));
            AssertPropertyExists(type, "OriginId", typeof(Guid?));
            AssertPropertyExists(type, "TargetId", typeof(Guid?));
            AssertPropertyExists(type, "ValidationErrors", typeof(List<ErrorModel>));
        }

        /// <summary>
        /// Source: IErpPostDeleteManyToManyRelationHook.OnPostDelete(string relationName, Guid? originId, Guid? targetId)
        /// NULLABLE Guid? for delete events.
        /// </summary>
        [Fact]
        public void RelationDeletedEvent_ShouldHavePropertiesMatchingSourceHook()
        {
            var type = typeof(RelationDeletedEvent);
            AssertPropertyExists(type, "RelationName", typeof(string));
            AssertPropertyExists(type, "OriginId", typeof(Guid?));
            AssertPropertyExists(type, "TargetId", typeof(Guid?));
        }

        #endregion

        // =====================================================================
        // Phase 4: Distributed Tracing Properties
        // =====================================================================

        #region Phase 4 — Distributed tracing metadata

        /// <summary>
        /// Verifies all 12 event types have non-default CorrelationId and Timestamp
        /// when instantiated via the parameterless constructor, as required by AAP
        /// for distributed tracing across service boundaries.
        /// </summary>
        [Fact]
        public void AllEvents_ShouldHaveCorrelationIdAndTimestamp()
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var eventType in AllEventTypes)
            {
                var instance = (IDomainEvent)Activator.CreateInstance(eventType);

                instance.CorrelationId.Should().NotBe(Guid.Empty,
                    because: $"{eventType.Name} must auto-generate a CorrelationId for distributed tracing");

                instance.Timestamp.Should().BeCloseTo(now, TimeSpan.FromSeconds(2),
                    because: $"{eventType.Name} must auto-set Timestamp close to current UTC time");
            }
        }

        /// <summary>
        /// Verifies that two instances of the same event type get unique CorrelationIds,
        /// ensuring each event instance is independently traceable.
        /// </summary>
        [Fact]
        public void CorrelationId_ShouldBeUniquePerInstance()
        {
            var instance1 = new PreRecordCreateEvent();
            var instance2 = new PreRecordCreateEvent();

            instance1.CorrelationId.Should().NotBe(instance2.CorrelationId,
                because: "each event instance must get a unique CorrelationId for independent tracing");

            // Also verify for a different event type
            var rel1 = new RelationCreatedEvent();
            var rel2 = new RelationCreatedEvent();

            rel1.CorrelationId.Should().NotBe(rel2.CorrelationId);
        }

        /// <summary>
        /// Verifies that the Timestamp property represents UTC time (offset == TimeSpan.Zero)
        /// and is close to the current UTC time within tolerance.
        /// </summary>
        [Fact]
        public void Timestamp_ShouldBeUtcNow()
        {
            var beforeCreation = DateTimeOffset.UtcNow;
            var instance = new RecordCreatedEvent();
            var afterCreation = DateTimeOffset.UtcNow;

            // Verify the timestamp offset is zero (UTC)
            instance.Timestamp.Offset.Should().Be(TimeSpan.Zero,
                because: "Timestamp must be in UTC for consistent ordering across distributed services");

            // Verify the timestamp is between the before and after markers
            instance.Timestamp.Should().BeOnOrAfter(beforeCreation);
            instance.Timestamp.Should().BeOnOrBefore(afterCreation);
        }

        #endregion

        // =====================================================================
        // Phase 5: Event Type Discriminator Tests
        // =====================================================================

        #region Phase 5 — Type discriminators and namespace

        /// <summary>
        /// Verifies all 12 event type names are distinct, serving as unique
        /// message broker routing discriminators for MassTransit topic routing.
        /// </summary>
        [Fact]
        public void EventTypes_ShouldHaveDistinctTypeNames()
        {
            var typeNames = AllEventTypes.Select(t => t.Name).ToList();
            var distinctNames = typeNames.Distinct().ToList();

            distinctNames.Should().HaveCount(typeNames.Count,
                because: "all event type names must be unique for message broker routing discriminators");
        }

        /// <summary>
        /// Verifies all 12 event types reside in the correct namespace
        /// <c>WebVella.Erp.SharedKernel.Contracts.Events</c>.
        /// </summary>
        [Fact]
        public void EventTypes_ShouldBeInCorrectNamespace()
        {
            const string expectedNamespace = "WebVella.Erp.SharedKernel.Contracts.Events";

            foreach (var eventType in AllEventTypes)
            {
                eventType.Namespace.Should().Be(expectedNamespace,
                    because: $"{eventType.Name} must be in namespace {expectedNamespace}");
            }
        }

        #endregion

        // =====================================================================
        // Phase 6: MassTransit Interface Compatibility Tests
        // =====================================================================

        #region Phase 6 — MassTransit compatibility

        /// <summary>
        /// Verifies all 12 event types have a public parameterless constructor,
        /// which is required by MassTransit for message deserialization.
        /// </summary>
        [Fact]
        public void AllEvents_ShouldHavePublicParameterlessConstructor()
        {
            foreach (var eventType in AllEventTypes)
            {
                var instance = Activator.CreateInstance(eventType);
                instance.Should().NotBeNull(
                    because: $"{eventType.Name} must have a public parameterless constructor for MassTransit deserialization");
            }
        }

        /// <summary>
        /// Verifies all 12 event types have public get AND set accessors on all properties,
        /// which is required by MassTransit for serialization/deserialization.
        /// </summary>
        [Fact]
        public void AllEvents_ShouldHavePublicGetSetProperties()
        {
            foreach (var eventType in AllEventTypes)
            {
                var properties = eventType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                properties.Should().NotBeEmpty(
                    because: $"{eventType.Name} must have at least one public property");

                foreach (var prop in properties)
                {
                    prop.GetGetMethod().Should().NotBeNull(
                        because: $"{eventType.Name}.{prop.Name} must have a public getter for MassTransit serialization");
                    prop.GetSetMethod().Should().NotBeNull(
                        because: $"{eventType.Name}.{prop.Name} must have a public setter for MassTransit deserialization");
                }
            }
        }

        /// <summary>
        /// Verifies all 12 event types are classes (not interfaces, not structs),
        /// as required by MassTransit message contracts.
        /// </summary>
        [Fact]
        public void AllEvents_ShouldBeClasses()
        {
            foreach (var eventType in AllEventTypes)
            {
                eventType.IsClass.Should().BeTrue(
                    because: $"{eventType.Name} must be a class for MassTransit message contracts");
                eventType.IsInterface.Should().BeFalse(
                    because: $"{eventType.Name} must not be an interface");
                eventType.IsValueType.Should().BeFalse(
                    because: $"{eventType.Name} must not be a struct");
            }
        }

        #endregion

        // =====================================================================
        // Phase 7: Pre-operation vs Post-operation Validation
        // =====================================================================

        #region Phase 7 — Pre/post-operation validation errors

        /// <summary>
        /// Verifies pre-operation events have the appropriate validation error properties:
        /// - Record pre-ops (Create, Update, Delete): ValidationErrors as List&lt;ErrorModel&gt;
        /// - Search pre-op: EqlErrors as List&lt;EqlError&gt;
        /// - Relation pre-ops (Create, Delete): ValidationErrors as List&lt;ErrorModel&gt;
        /// </summary>
        [Fact]
        public void PreOperationEvents_ShouldHaveValidationErrors()
        {
            // Record pre-operation events with ValidationErrors
            AssertPropertyExists(typeof(PreRecordCreateEvent), "ValidationErrors", typeof(List<ErrorModel>));
            AssertPropertyExists(typeof(PreRecordUpdateEvent), "ValidationErrors", typeof(List<ErrorModel>));
            AssertPropertyExists(typeof(PreRecordDeleteEvent), "ValidationErrors", typeof(List<ErrorModel>));

            // Search pre-operation event with EqlErrors
            AssertPropertyExists(typeof(PreRecordSearchEvent), "EqlErrors", typeof(List<EqlError>));

            // Relation pre-operation events with ValidationErrors
            AssertPropertyExists(typeof(PreRelationCreateEvent), "ValidationErrors", typeof(List<ErrorModel>));
            AssertPropertyExists(typeof(PreRelationDeleteEvent), "ValidationErrors", typeof(List<ErrorModel>));
        }

        /// <summary>
        /// Verifies post-operation events do NOT have a ValidationErrors property,
        /// because post-operation events represent committed data that cannot be rejected.
        /// </summary>
        [Fact]
        public void PostOperationEvents_ShouldNotHaveValidationErrors()
        {
            // Record post-operation events
            AssertPropertyDoesNotExist(typeof(RecordCreatedEvent), "ValidationErrors");
            AssertPropertyDoesNotExist(typeof(RecordUpdatedEvent), "ValidationErrors");
            AssertPropertyDoesNotExist(typeof(RecordDeletedEvent), "ValidationErrors");
            AssertPropertyDoesNotExist(typeof(RecordSearchEvent), "ValidationErrors");

            // Relation post-operation events
            AssertPropertyDoesNotExist(typeof(RelationCreatedEvent), "ValidationErrors");
            AssertPropertyDoesNotExist(typeof(RelationDeletedEvent), "ValidationErrors");
        }

        /// <summary>
        /// Verifies that pre-operation events initialize their ValidationErrors/EqlErrors
        /// to empty lists in the parameterless constructor (not null), preventing
        /// NullReferenceException when subscribers add validation errors.
        /// </summary>
        [Fact]
        public void PreOperationEvents_ShouldInitializeValidationErrorsToEmptyList()
        {
            // Record pre-op events: ValidationErrors initialized to empty list
            new PreRecordCreateEvent().ValidationErrors.Should().NotBeNull().And.BeEmpty();
            new PreRecordUpdateEvent().ValidationErrors.Should().NotBeNull().And.BeEmpty();
            new PreRecordDeleteEvent().ValidationErrors.Should().NotBeNull().And.BeEmpty();

            // Search pre-op event: EqlErrors initialized to empty list
            new PreRecordSearchEvent().EqlErrors.Should().NotBeNull().And.BeEmpty();

            // Relation pre-op events: ValidationErrors initialized to empty list
            new PreRelationCreateEvent().ValidationErrors.Should().NotBeNull().And.BeEmpty();
            new PreRelationDeleteEvent().ValidationErrors.Should().NotBeNull().And.BeEmpty();
        }

        #endregion

        // =====================================================================
        // Phase 8: Nullable Guid Specificity Tests
        // =====================================================================

        #region Phase 8 — Nullable Guid specificity

        /// <summary>
        /// Verifies relation CREATE events use NON-nullable Guid for OriginId and TargetId,
        /// matching source hooks:
        /// - IErpPreCreateManyToManyRelationHook.OnPreCreate(string, Guid, Guid, List&lt;ErrorModel&gt;)
        /// - IErpPostCreateManyToManyRelationHook.OnPostCreate(string, Guid, Guid)
        /// </summary>
        [Fact]
        public void RelationCreateEvents_ShouldHaveNonNullableGuids()
        {
            // PreRelationCreateEvent: non-nullable Guid
            var preOriginProp = typeof(PreRelationCreateEvent).GetProperty("OriginId");
            preOriginProp.PropertyType.Should().Be(typeof(Guid),
                because: "create events must use non-nullable Guid matching IErpPreCreateManyToManyRelationHook");
            var preTargetProp = typeof(PreRelationCreateEvent).GetProperty("TargetId");
            preTargetProp.PropertyType.Should().Be(typeof(Guid));

            // RelationCreatedEvent: non-nullable Guid
            var postOriginProp = typeof(RelationCreatedEvent).GetProperty("OriginId");
            postOriginProp.PropertyType.Should().Be(typeof(Guid),
                because: "create events must use non-nullable Guid matching IErpPostCreateManyToManyRelationHook");
            var postTargetProp = typeof(RelationCreatedEvent).GetProperty("TargetId");
            postTargetProp.PropertyType.Should().Be(typeof(Guid));
        }

        /// <summary>
        /// Verifies relation DELETE events use NULLABLE Guid? for OriginId and TargetId,
        /// matching source hooks:
        /// - IErpPreDeleteManyToManyRelationHook.OnPreDelete(string, Guid?, Guid?, List&lt;ErrorModel&gt;)
        /// - IErpPostDeleteManyToManyRelationHook.OnPostDelete(string, Guid?, Guid?)
        /// </summary>
        [Fact]
        public void RelationDeleteEvents_ShouldHaveNullableGuids()
        {
            // PreRelationDeleteEvent: nullable Guid?
            var preOriginProp = typeof(PreRelationDeleteEvent).GetProperty("OriginId");
            preOriginProp.PropertyType.Should().Be(typeof(Guid?),
                because: "delete events must use nullable Guid? matching IErpPreDeleteManyToManyRelationHook");
            var preTargetProp = typeof(PreRelationDeleteEvent).GetProperty("TargetId");
            preTargetProp.PropertyType.Should().Be(typeof(Guid?));

            // RelationDeletedEvent: nullable Guid?
            var postOriginProp = typeof(RelationDeletedEvent).GetProperty("OriginId");
            postOriginProp.PropertyType.Should().Be(typeof(Guid?),
                because: "delete events must use nullable Guid? matching IErpPostDeleteManyToManyRelationHook");
            var postTargetProp = typeof(RelationDeletedEvent).GetProperty("TargetId");
            postTargetProp.PropertyType.Should().Be(typeof(Guid?));
        }

        /// <summary>
        /// Verifies that null Guid values in relation delete events survive JSON round-trip serialization,
        /// confirming that Newtonsoft.Json correctly handles nullable Guid? with [JsonProperty] annotations.
        /// </summary>
        [Fact]
        public void RelationDeleteEvent_ShouldSerializeNullGuids()
        {
            // Test PreRelationDeleteEvent with both null Guids
            var preDeleteOriginal = new PreRelationDeleteEvent
            {
                RelationName = "test_relation",
                OriginId = null,
                TargetId = null,
                ValidationErrors = new List<ErrorModel>(),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var preDeleteJson = JsonConvert.SerializeObject(preDeleteOriginal);
            var preDeleteDeserialized = JsonConvert.DeserializeObject<PreRelationDeleteEvent>(preDeleteJson);

            preDeleteDeserialized.Should().NotBeNull();
            preDeleteDeserialized.OriginId.Should().BeNull(
                because: "null Guid? must survive JSON round-trip for bulk delete scenarios");
            preDeleteDeserialized.TargetId.Should().BeNull(
                because: "null Guid? must survive JSON round-trip for bulk delete scenarios");
            preDeleteDeserialized.RelationName.Should().Be("test_relation");
            preDeleteDeserialized.CorrelationId.Should().Be(preDeleteOriginal.CorrelationId);

            // Test RelationDeletedEvent with both null Guids
            var deletedOriginal = new RelationDeletedEvent
            {
                RelationName = "test_relation",
                OriginId = null,
                TargetId = null,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };

            var deletedJson = JsonConvert.SerializeObject(deletedOriginal);
            var deletedDeserialized = JsonConvert.DeserializeObject<RelationDeletedEvent>(deletedJson);

            deletedDeserialized.Should().NotBeNull();
            deletedDeserialized.OriginId.Should().BeNull();
            deletedDeserialized.TargetId.Should().BeNull();
            deletedDeserialized.RelationName.Should().Be("test_relation");
        }

        #endregion
    }
}
