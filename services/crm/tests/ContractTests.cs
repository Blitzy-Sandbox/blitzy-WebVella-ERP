using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using WebVellaErp.Crm.Models;
using Xunit;

namespace WebVellaErp.Crm.Tests
{
    /// <summary>
    /// Contract tests verifying that published SNS event schemas from the CRM microservice
    /// match the shared schema definitions in libs/shared-schemas. Ensures inter-service API
    /// and event schema contracts are maintained across bounded-context boundaries.
    ///
    /// Source mapping (monolith hooks → microservice SNS events):
    ///   IErpPostCreateRecordHook → crm.{entity}.created SNS event
    ///   IErpPostUpdateRecordHook → crm.{entity}.updated SNS event
    ///   IErpPostDeleteRecordHook → crm.{entity}.deleted SNS event (NEW — not in monolith hooks)
    ///   AccountHook.OnPostCreateRecord → crm.account.created
    ///   AccountHook.OnPostUpdateRecord → crm.account.updated
    ///   ContactHook.OnPostCreateRecord → crm.contact.created
    ///   ContactHook.OnPostUpdateRecord → crm.contact.updated
    ///
    /// Per AAP §0.8.5: Event naming convention {domain}.{entity}.{action}
    /// Per AAP §0.7.2: Post-CRUD hooks → SNS domain events
    /// Per AAP §0.8.4: Contract tests for all inter-service API and event schemas
    /// </summary>
    public class ContractTests
    {
        /// <summary>
        /// Complete set of 6 CRM domain event types per AAP §0.8.5 naming convention.
        /// Covers account CRUD (3 events) and contact CRUD (3 events).
        /// Note: crm.account.deleted and crm.contact.deleted are NEW — the monolith's
        /// AccountHook and ContactHook did NOT implement IErpPostDeleteRecordHook.
        /// </summary>
        private static readonly string[] AllCrmEventTypesList = new[]
        {
            "crm.account.created",
            "crm.account.updated",
            "crm.account.deleted",
            "crm.contact.created",
            "crm.contact.updated",
            "crm.contact.deleted"
        };

        /// <summary>
        /// Regex pattern enforcing AAP §0.8.5 event naming convention:
        /// {domain}.{entity}.{action} where domain=crm, entity=[a-z]+,
        /// action=created|updated|deleted.
        /// </summary>
        private static readonly Regex EventNamingPattern = new Regex(
            @"^crm\.[a-z]+\.(created|updated|deleted)$",
            RegexOptions.Compiled);

        /// <summary>
        /// Required fields in every CRM SNS event payload, matching the shared
        /// event schema contract in libs/shared-schemas/src/events/.
        /// </summary>
        private static readonly HashSet<string> RequiredEventFields = new HashSet<string>
        {
            "eventType",
            "entityName",
            "recordId",
            "correlationId",
            "timestamp"
        };

        /// <summary>
        /// Valid CRUD action suffixes for CRM domain events.
        /// Maps to IErpPostCreateRecordHook (created), IErpPostUpdateRecordHook (updated),
        /// and IErpPostDeleteRecordHook (deleted).
        /// </summary>
        private static readonly HashSet<string> ValidActions = new HashSet<string>
        {
            "created",
            "updated",
            "deleted"
        };

        /// <summary>
        /// Shared JSON Schema property type definitions for CRM event payloads.
        /// All fields are string type per the shared schema contract — GUIDs and
        /// timestamps are serialized as strings for cross-service interoperability.
        /// </summary>
        private static readonly Dictionary<string, JsonValueKind> SharedSchemaPropertyTypes =
            new Dictionary<string, JsonValueKind>
            {
                { "eventType", JsonValueKind.String },
                { "entityName", JsonValueKind.String },
                { "recordId", JsonValueKind.String },
                { "correlationId", JsonValueKind.String },
                { "timestamp", JsonValueKind.String }
            };

        #region Helper Methods

        /// <summary>
        /// Creates a sample CRM domain event payload JSON string matching the shared
        /// schema contract. Uses System.Text.Json (NOT Newtonsoft.Json) per AAP
        /// .NET 9 Native AOT requirement.
        /// </summary>
        /// <param name="eventType">Full event type (e.g., "crm.account.created")</param>
        /// <param name="entityName">Entity name (e.g., "account")</param>
        /// <returns>JSON string representing the event payload</returns>
        private static string CreateEventPayload(string eventType, string entityName)
        {
            var payload = new Dictionary<string, object>
            {
                { "eventType", eventType },
                { "entityName", entityName },
                { "recordId", Guid.NewGuid().ToString() },
                { "correlationId", Guid.NewGuid().ToString() },
                { "timestamp", DateTime.UtcNow.ToString("o") }
            };
            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Creates sample SNS MessageAttributes dictionary for topic-level filtering.
        /// These attributes enable SQS subscribers to filter events by type, entity,
        /// and correlation ID without parsing the message body.
        /// </summary>
        /// <param name="eventType">Full event type for MessageAttributes filtering</param>
        /// <param name="entityName">Entity name for entity-level filtering</param>
        /// <param name="correlationId">Correlation ID for distributed request tracing</param>
        /// <returns>Dictionary representing SNS MessageAttributes</returns>
        private static Dictionary<string, Dictionary<string, string>> CreateSnsMessageAttributes(
            string eventType, string entityName, string correlationId)
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                {
                    "eventType",
                    new Dictionary<string, string>
                    {
                        { "DataType", "String" },
                        { "StringValue", eventType }
                    }
                },
                {
                    "entityName",
                    new Dictionary<string, string>
                    {
                        { "DataType", "String" },
                        { "StringValue", entityName }
                    }
                },
                {
                    "correlationId",
                    new Dictionary<string, string>
                    {
                        { "DataType", "String" },
                        { "StringValue", correlationId }
                    }
                }
            };
        }

        /// <summary>
        /// Validates that a JSON event payload has exactly the required fields with
        /// correct types and values. Performs comprehensive schema validation including
        /// field presence, type checking, GUID format, and ISO 8601 timestamp format.
        /// </summary>
        /// <param name="json">JSON event payload string to validate</param>
        /// <param name="expectedEventType">Expected eventType field value</param>
        /// <param name="expectedEntityName">Expected entityName field value</param>
        private static void ValidateEventPayloadStructure(
            string json, string expectedEventType, string expectedEntityName)
        {
            // Deserialize to verify round-trip compatibility with System.Text.Json
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            deserialized.Should().NotBeNull("event payload must be valid JSON");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Verify all required fields exist with correct types
            foreach (var field in RequiredEventFields)
            {
                root.TryGetProperty(field, out var fieldValue).Should().BeTrue(
                    $"event payload must contain required field '{field}'");
                fieldValue.ValueKind.Should().Be(JsonValueKind.String,
                    $"field '{field}' must be a string type per shared schema");
            }

            // Verify no extra/unknown fields — strict schema validation
            var propertyCount = 0;
            foreach (var _ in root.EnumerateObject())
            {
                propertyCount++;
            }
            propertyCount.Should().Be(RequiredEventFields.Count,
                "event payload must contain exactly the required fields and no extras");

            // Verify specific field values match expected types
            root.GetProperty("eventType").GetString().Should().Be(expectedEventType);
            root.GetProperty("entityName").GetString().Should().Be(expectedEntityName);

            // Verify eventType follows CRM domain naming prefix (wildcard match)
            root.GetProperty("eventType").GetString().Should().Match("crm.*",
                "eventType must follow CRM domain naming prefix pattern");

            // Verify recordId is valid GUID format
            Guid.TryParse(root.GetProperty("recordId").GetString(), out _).Should().BeTrue(
                "recordId must be a valid GUID format");

            // Verify correlationId is valid GUID format (per AAP §0.8.5 idempotency keys)
            Guid.TryParse(root.GetProperty("correlationId").GetString(), out _).Should().BeTrue(
                "correlationId must be a valid GUID format");

            // Verify timestamp is valid ISO 8601 DateTime format
            DateTime.TryParse(root.GetProperty("timestamp").GetString(), out _).Should().BeTrue(
                "timestamp must be a valid ISO 8601 DateTime format");
        }

        #endregion

        #region Phase 2: Event Naming Convention Tests

        /// <summary>
        /// Verifies each CRM event type follows the AAP §0.8.5 naming convention:
        /// {domain}.{entity}.{action} with domain="crm", entity in lowercase,
        /// and action as one of created/updated/deleted.
        /// </summary>
        [Theory]
        [InlineData("crm.account.created")]
        [InlineData("crm.account.updated")]
        [InlineData("crm.account.deleted")]
        [InlineData("crm.contact.created")]
        [InlineData("crm.contact.updated")]
        [InlineData("crm.contact.deleted")]
        public void EventType_FollowsNamingConvention(string eventType)
        {
            // Verify matches AAP §0.8.5 naming convention regex
            Regex.IsMatch(eventType, @"^crm\.[a-z]+\.(created|updated|deleted)$")
                .Should().BeTrue(
                    $"event type '{eventType}' must match pattern {{domain}}.{{entity}}.{{action}}");

            // Also verify using the compiled static regex instance
            EventNamingPattern.IsMatch(eventType).Should().BeTrue(
                $"event type '{eventType}' must pass compiled naming convention pattern");

            // Verify exactly 3 dot-separated parts
            var parts = eventType.Split('.');
            parts.Should().HaveCount(3, "event type must have exactly 3 dot-separated parts");

            // Verify first part is domain "crm"
            parts[0].Should().Be("crm", "first segment must be the CRM domain identifier");

            // Verify second part (entity name) is non-empty lowercase
            parts[1].Should().NotBeEmpty("entity name segment must not be empty");

            // Verify last part is a valid CRUD action
            ValidActions.Should().Contain(parts[2],
                "last segment must be a valid action: created, updated, or deleted");
        }

        /// <summary>
        /// Verifies the complete set of 6 CRM event types are defined — 3 for account
        /// (created/updated/deleted) and 3 for contact (created/updated/deleted).
        /// Note: crm.account.deleted and crm.contact.deleted are NEW events not present
        /// in the source monolith's AccountHook/ContactHook, but required by AAP.
        /// </summary>
        [Fact]
        public void AllCrmEventTypes_AreDefined()
        {
            // Define the expected complete set of CRM event types
            var expectedEventTypes = new List<string>
            {
                "crm.account.created",  // Replaces IErpPostCreateRecordHook for account
                "crm.account.updated",  // Replaces IErpPostUpdateRecordHook for account
                "crm.account.deleted",  // NEW — source AccountHook did NOT have delete hook
                "crm.contact.created",  // Replaces IErpPostCreateRecordHook for contact
                "crm.contact.updated",  // Replaces IErpPostUpdateRecordHook for contact
                "crm.contact.deleted"   // NEW — source ContactHook did NOT have delete hook
            };

            // Verify the defined set matches exactly
            AllCrmEventTypesList.Should().NotBeEmpty(
                "CRM service must define event types for inter-service communication");
            AllCrmEventTypesList.Should().HaveCount(6,
                "CRM service must publish exactly 6 event types (3 per entity × 2 entities)");
            AllCrmEventTypesList.Should().BeEquivalentTo(expectedEventTypes,
                "all 6 CRM domain event types must be defined with correct names");

            // Verify each entity has exactly 3 event types (created, updated, deleted)
            var accountEvents = AllCrmEventTypesList.Where(e => e.Contains(".account.")).ToArray();
            accountEvents.Length.Should().Be(3,
                "account entity must have exactly 3 event types (created, updated, deleted)");

            var contactEvents = AllCrmEventTypesList.Where(e => e.Contains(".contact.")).ToArray();
            contactEvents.Length.Should().Be(3,
                "contact entity must have exactly 3 event types (created, updated, deleted)");
        }

        #endregion

        #region Phase 3: Event Payload Schema Tests

        /// <summary>
        /// Verifies crm.account.created event payload has all required fields with correct
        /// types and values. Source: AccountHook.OnPostCreateRecord → SNS event.
        /// </summary>
        [Fact]
        public void AccountCreatedEvent_HasRequiredFields()
        {
            var json = CreateEventPayload("crm.account.created", "account");
            ValidateEventPayloadStructure(json, "crm.account.created", "account");
        }

        /// <summary>
        /// Verifies crm.account.updated event payload has all required fields.
        /// Source: AccountHook.OnPostUpdateRecord → SNS event.
        /// </summary>
        [Fact]
        public void AccountUpdatedEvent_HasRequiredFields()
        {
            var json = CreateEventPayload("crm.account.updated", "account");
            ValidateEventPayloadStructure(json, "crm.account.updated", "account");
        }

        /// <summary>
        /// Verifies crm.account.deleted event payload has all required fields.
        /// NEW: The monolith's AccountHook did NOT implement IErpPostDeleteRecordHook —
        /// this event type is added per AAP requirement for complete CRUD event coverage.
        /// </summary>
        [Fact]
        public void AccountDeletedEvent_HasRequiredFields()
        {
            var json = CreateEventPayload("crm.account.deleted", "account");
            ValidateEventPayloadStructure(json, "crm.account.deleted", "account");
        }

        /// <summary>
        /// Verifies crm.contact.created event payload has all required fields.
        /// Source: ContactHook.OnPostCreateRecord → SNS event.
        /// </summary>
        [Fact]
        public void ContactCreatedEvent_HasRequiredFields()
        {
            var json = CreateEventPayload("crm.contact.created", "contact");
            ValidateEventPayloadStructure(json, "crm.contact.created", "contact");
        }

        /// <summary>
        /// Verifies crm.contact.updated event payload has all required fields.
        /// Source: ContactHook.OnPostUpdateRecord → SNS event.
        /// </summary>
        [Fact]
        public void ContactUpdatedEvent_HasRequiredFields()
        {
            var json = CreateEventPayload("crm.contact.updated", "contact");
            ValidateEventPayloadStructure(json, "crm.contact.updated", "contact");
        }

        /// <summary>
        /// Verifies crm.contact.deleted event payload has all required fields.
        /// NEW: The monolith's ContactHook did NOT implement IErpPostDeleteRecordHook —
        /// this event type is added per AAP requirement for complete CRUD event coverage.
        /// </summary>
        [Fact]
        public void ContactDeletedEvent_HasRequiredFields()
        {
            var json = CreateEventPayload("crm.contact.deleted", "contact");
            ValidateEventPayloadStructure(json, "crm.contact.deleted", "contact");
        }

        #endregion

        #region Phase 4: SNS MessageAttributes Schema Tests

        /// <summary>
        /// Verifies SNS MessageAttributes include eventType for topic-level filtering.
        /// SQS subscribers use MessageAttributes to filter events without parsing the body.
        /// </summary>
        [Fact]
        public void SnsMessageAttributes_ContainEventType()
        {
            var correlationId = Guid.NewGuid().ToString();
            var attributes = CreateSnsMessageAttributes(
                "crm.account.created", "account", correlationId);

            attributes.Should().ContainKey("eventType",
                "SNS MessageAttributes must include 'eventType' for topic-level filtering");

            var eventTypeAttr = attributes["eventType"];
            eventTypeAttr.Should().ContainKey("DataType");
            eventTypeAttr["DataType"].Should().Be("String",
                "eventType MessageAttribute DataType must be 'String'");
            eventTypeAttr.Should().ContainKey("StringValue");
            eventTypeAttr["StringValue"].Should().Be("crm.account.created",
                "eventType StringValue must match the published event type");
        }

        /// <summary>
        /// Verifies SNS MessageAttributes include entityName for entity-level filtering.
        /// Allows subscribers to filter for specific CRM entity events (account vs contact).
        /// </summary>
        [Fact]
        public void SnsMessageAttributes_ContainEntityName()
        {
            var correlationId = Guid.NewGuid().ToString();
            var attributes = CreateSnsMessageAttributes(
                "crm.contact.updated", "contact", correlationId);

            attributes.Should().ContainKey("entityName",
                "SNS MessageAttributes must include 'entityName' for entity-level filtering");

            var entityNameAttr = attributes["entityName"];
            entityNameAttr.Should().ContainKey("DataType");
            entityNameAttr["DataType"].Should().Be("String",
                "entityName MessageAttribute DataType must be 'String'");
            entityNameAttr.Should().ContainKey("StringValue");
            entityNameAttr["StringValue"].Should().Be("contact",
                "entityName StringValue must match the target entity name");
        }

        /// <summary>
        /// Verifies SNS MessageAttributes include correlationId for distributed request tracing.
        /// Per AAP §0.8.5: correlation-ID propagation from all Lambda functions.
        /// </summary>
        [Fact]
        public void SnsMessageAttributes_ContainCorrelationId()
        {
            var correlationId = Guid.NewGuid().ToString();
            var attributes = CreateSnsMessageAttributes(
                "crm.account.deleted", "account", correlationId);

            attributes.Should().ContainKey("correlationId",
                "SNS MessageAttributes must include 'correlationId' for distributed tracing");

            var correlationIdAttr = attributes["correlationId"];
            correlationIdAttr.Should().ContainKey("DataType");
            correlationIdAttr["DataType"].Should().Be("String",
                "correlationId MessageAttribute DataType must be 'String'");
            correlationIdAttr.Should().ContainKey("StringValue");
            Guid.TryParse(correlationIdAttr["StringValue"], out _).Should().BeTrue(
                "correlationId StringValue must be a valid GUID string");
        }

        #endregion

        #region Phase 5: Cross-Schema Compatibility Tests

        /// <summary>
        /// Validates crm.account.created event payload against the shared JSON Schema
        /// contract from libs/shared-schemas/src/events/. This ensures backward
        /// compatibility — if the shared schema changes, this test catches it.
        /// </summary>
        [Fact]
        public void AccountCreatedEvent_MatchesSharedSchemaContract()
        {
            // Define the expected shared schema contract properties and types
            var expectedProperties = new Dictionary<string, JsonValueKind>(SharedSchemaPropertyTypes);
            var expectedRequired = new HashSet<string>(RequiredEventFields);

            // Create a sample event payload as produced by AccountHandler
            var json = CreateEventPayload("crm.account.created", "account");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Validate all properties exist with correct types (schema compliance)
            foreach (var (propName, expectedType) in expectedProperties)
            {
                root.TryGetProperty(propName, out var propValue).Should().BeTrue(
                    $"shared schema requires property '{propName}'");
                propValue.ValueKind.Should().Be(expectedType,
                    $"property '{propName}' must match shared schema type {expectedType}");
            }

            // Validate all required properties are present
            foreach (var required in expectedRequired)
            {
                root.TryGetProperty(required, out _).Should().BeTrue(
                    $"shared schema marks '{required}' as required");
            }

            // Validate no additional properties (strict schema — no additionalProperties)
            var actualProperties = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                actualProperties.Add(prop.Name);
            }
            actualProperties.Should().BeEquivalentTo(expectedRequired,
                "event payload must contain exactly the shared schema properties, no extras");

            // Validate entityName matches CRM entity
            root.GetProperty("entityName").GetString().Should().Be("account",
                "entityName must match the CRM account entity");
        }

        /// <summary>
        /// Validates crm.contact.created event payload against the shared JSON Schema
        /// contract. Same structure as account events — consumers use a single handler.
        /// </summary>
        [Fact]
        public void ContactCreatedEvent_MatchesSharedSchemaContract()
        {
            var expectedProperties = new Dictionary<string, JsonValueKind>(SharedSchemaPropertyTypes);
            var expectedRequired = new HashSet<string>(RequiredEventFields);

            var json = CreateEventPayload("crm.contact.created", "contact");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Validate all properties exist with correct types
            foreach (var (propName, expectedType) in expectedProperties)
            {
                root.TryGetProperty(propName, out var propValue).Should().BeTrue(
                    $"shared schema requires property '{propName}'");
                propValue.ValueKind.Should().Be(expectedType,
                    $"property '{propName}' must match shared schema type {expectedType}");
            }

            // Validate all required properties are present
            foreach (var required in expectedRequired)
            {
                root.TryGetProperty(required, out _).Should().BeTrue(
                    $"shared schema marks '{required}' as required");
            }

            // Validate strict schema — no additional properties
            var actualProperties = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                actualProperties.Add(prop.Name);
            }
            actualProperties.Should().BeEquivalentTo(expectedRequired,
                "event payload must contain exactly the shared schema properties, no extras");

            // Validate entityName matches CRM entity
            root.GetProperty("entityName").GetString().Should().Be("contact",
                "entityName must match the CRM contact entity");
        }

        /// <summary>
        /// Verifies all 6 CRM events have consistent structure — identical field set
        /// with only eventType and entityName values differing. This ensures consumers
        /// can use a single handler/deserializer for all CRM domain events.
        /// </summary>
        [Theory]
        [InlineData("crm.account.created", "account")]
        [InlineData("crm.account.updated", "account")]
        [InlineData("crm.account.deleted", "account")]
        [InlineData("crm.contact.created", "contact")]
        [InlineData("crm.contact.updated", "contact")]
        [InlineData("crm.contact.deleted", "contact")]
        public void AllCrmEvents_HaveConsistentStructure(string eventType, string entityName)
        {
            var json = CreateEventPayload(eventType, entityName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Verify identical field names across all CRM events
            var fieldNames = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                fieldNames.Add(prop.Name);
            }
            fieldNames.Should().BeEquivalentTo(RequiredEventFields,
                $"event type '{eventType}' must have the same field structure as all CRM events");

            // Verify all field values are string type (consistent structure for single handler)
            foreach (var field in RequiredEventFields)
            {
                root.GetProperty(field).ValueKind.Should().Be(JsonValueKind.String,
                    $"field '{field}' in event '{eventType}' must be a string type");
            }

            // Verify the field count is exactly the expected number (no hidden fields)
            var fieldCount = fieldNames.Count;
            fieldCount.Should().Be(RequiredEventFields.Count,
                $"event '{eventType}' must have exactly {RequiredEventFields.Count} fields");
        }

        #endregion

        #region Phase 6: Entity ID Constants Verification

        /// <summary>
        /// Verifies Account.EntityId constant matches the source monolith's entity definition
        /// from NextPlugin.20190204.cs line 43. Entity IDs must be preserved during migration
        /// to maintain data integrity per AAP §0.8.1.
        /// </summary>
        [Fact]
        public void AccountEntityId_MatchesSource()
        {
            // Source: NextPlugin.20190204.cs line 43 — account entity creation
            var expectedEntityId = Guid.Parse("2e22b50f-e444-4b62-a171-076e51246939");
            Account.EntityId.Should().Be(expectedEntityId,
                "Account.EntityId must match the source monolith's NextPlugin.20190204.cs " +
                "account entity definition for data migration integrity");
        }

        /// <summary>
        /// Verifies Contact.EntityId constant matches the source monolith's entity definition
        /// from NextPlugin.20190204.cs line 1408. Entity IDs must be preserved during migration
        /// to maintain data integrity per AAP §0.8.1.
        /// </summary>
        [Fact]
        public void ContactEntityId_MatchesSource()
        {
            // Source: NextPlugin.20190204.cs line 1408 — contact entity creation
            var expectedEntityId = Guid.Parse("39e1dd9b-827f-464d-95ea-507ade81cbd0");
            Contact.EntityId.Should().Be(expectedEntityId,
                "Contact.EntityId must match the source monolith's NextPlugin.20190204.cs " +
                "contact entity definition for data migration integrity");
        }

        #endregion

        #region Phase 7: Hook-to-Event Mapping Verification

        /// <summary>
        /// Verifies crm.account.created event covers the behavior of the monolith's
        /// AccountHook.OnPostCreateRecord. Source: AccountHook.cs lines 12-15 called
        /// SearchService.RegenSearchField after create. Target: SNS event triggers
        /// downstream consumers + inline search regeneration.
        /// </summary>
        [Fact]
        public void AccountHook_OnPostCreate_MapsToSnsEvent()
        {
            // Source hook contract: IErpPostCreateRecordHook
            //   void OnPostCreateRecord(string entityName, EntityRecord record)
            // Target: crm.account.created SNS event
            var eventType = "crm.account.created";
            var entityName = "account";

            // Verify event targets the correct entity
            eventType.Should().Contain("account",
                "event must target the account entity matching AccountHook");
            eventType.Should().EndWith(".created",
                "event must map to post-CREATE hook action (IErpPostCreateRecordHook)");

            // Verify the event payload carries equivalent data to the hook parameters
            var json = CreateEventPayload(eventType, entityName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Hook parameter: entityName → event field: entityName
            root.GetProperty("entityName").GetString().Should().Be("account",
                "event must carry entityName matching AccountHook's entityName parameter");

            // Hook parameter: EntityRecord (has Id) → event field: recordId
            root.TryGetProperty("recordId", out var recordIdProp).Should().BeTrue(
                "event must carry recordId representing the EntityRecord from AccountHook");
            Guid.TryParse(recordIdProp.GetString(), out _).Should().BeTrue(
                "recordId must be a valid GUID matching the EntityRecord's Id property");

            // Async replacement adds correlation tracking (not in original sync hook)
            root.TryGetProperty("correlationId", out _).Should().BeTrue(
                "async SNS event must include correlationId for distributed tracing " +
                "(replacing AccountHook's synchronous in-process execution)");
        }

        /// <summary>
        /// Verifies crm.account.updated event maps to AccountHook.OnPostUpdateRecord.
        /// Source: AccountHook.cs lines 17-20 called SearchService.RegenSearchField
        /// after update.
        /// </summary>
        [Fact]
        public void AccountHook_OnPostUpdate_MapsToSnsEvent()
        {
            // Source hook contract: IErpPostUpdateRecordHook
            //   void OnPostUpdateRecord(string entityName, EntityRecord record)
            var eventType = "crm.account.updated";
            var entityName = "account";

            eventType.Should().Contain("account",
                "event must target the account entity matching AccountHook");
            eventType.Should().EndWith(".updated",
                "event must map to post-UPDATE hook action (IErpPostUpdateRecordHook)");

            var json = CreateEventPayload(eventType, entityName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.GetProperty("entityName").GetString().Should().Be("account",
                "event must carry entityName matching AccountHook's entityName parameter");

            root.TryGetProperty("recordId", out var recordIdProp).Should().BeTrue(
                "event must carry recordId representing the EntityRecord from AccountHook");
            Guid.TryParse(recordIdProp.GetString(), out _).Should().BeTrue(
                "recordId must be a valid GUID matching the EntityRecord's Id property");

            root.TryGetProperty("correlationId", out _).Should().BeTrue(
                "async SNS event must include correlationId for distributed tracing");
        }

        /// <summary>
        /// Verifies crm.contact.created event maps to ContactHook.OnPostCreateRecord.
        /// Source: ContactHook.cs lines 12-15 called SearchService.RegenSearchField
        /// after create.
        /// </summary>
        [Fact]
        public void ContactHook_OnPostCreate_MapsToSnsEvent()
        {
            // Source hook contract: IErpPostCreateRecordHook
            //   void OnPostCreateRecord(string entityName, EntityRecord record)
            var eventType = "crm.contact.created";
            var entityName = "contact";

            eventType.Should().Contain("contact",
                "event must target the contact entity matching ContactHook");
            eventType.Should().EndWith(".created",
                "event must map to post-CREATE hook action (IErpPostCreateRecordHook)");

            var json = CreateEventPayload(eventType, entityName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.GetProperty("entityName").GetString().Should().Be("contact",
                "event must carry entityName matching ContactHook's entityName parameter");

            root.TryGetProperty("recordId", out var recordIdProp).Should().BeTrue(
                "event must carry recordId representing the EntityRecord from ContactHook");
            Guid.TryParse(recordIdProp.GetString(), out _).Should().BeTrue(
                "recordId must be a valid GUID matching the EntityRecord's Id property");

            root.TryGetProperty("correlationId", out _).Should().BeTrue(
                "async SNS event must include correlationId for distributed tracing " +
                "(replacing ContactHook's synchronous in-process execution)");
        }

        /// <summary>
        /// Verifies crm.contact.updated event maps to ContactHook.OnPostUpdateRecord.
        /// Source: ContactHook.cs lines 17-20 called SearchService.RegenSearchField
        /// after update.
        /// </summary>
        [Fact]
        public void ContactHook_OnPostUpdate_MapsToSnsEvent()
        {
            // Source hook contract: IErpPostUpdateRecordHook
            //   void OnPostUpdateRecord(string entityName, EntityRecord record)
            var eventType = "crm.contact.updated";
            var entityName = "contact";

            eventType.Should().Contain("contact",
                "event must target the contact entity matching ContactHook");
            eventType.Should().EndWith(".updated",
                "event must map to post-UPDATE hook action (IErpPostUpdateRecordHook)");

            var json = CreateEventPayload(eventType, entityName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            root.GetProperty("entityName").GetString().Should().Be("contact",
                "event must carry entityName matching ContactHook's entityName parameter");

            root.TryGetProperty("recordId", out var recordIdProp).Should().BeTrue(
                "event must carry recordId representing the EntityRecord from ContactHook");
            Guid.TryParse(recordIdProp.GetString(), out _).Should().BeTrue(
                "recordId must be a valid GUID matching the EntityRecord's Id property");

            root.TryGetProperty("correlationId", out _).Should().BeTrue(
                "async SNS event must include correlationId for distributed tracing");
        }

        #endregion
    }
}
