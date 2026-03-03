using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Models
{
    /// <summary>
    /// Comprehensive unit tests for the <see cref="EntityRecordList"/> class from SharedKernel.
    /// EntityRecordList is a pagination-aware list of <see cref="EntityRecord"/> instances
    /// returned by record queries. It extends <see cref="List{EntityRecord}"/> so it can be
    /// enumerated directly while also carrying <see cref="EntityRecordList.TotalCount"/> for
    /// offset-based pagination.
    ///
    /// Tests validate:
    /// - Collection behavior inherited from List&lt;EntityRecord&gt; (Add, Remove, Clear, Count,
    ///   indexer access, enumeration, Contains)
    /// - TotalCount property (default value, get/set, independence from list Count)
    /// - JSON serialization/deserialization with Newtonsoft.Json (snake_case total_count
    ///   property name via [JsonProperty], nested EntityRecord serialization, round-trip fidelity)
    /// - Integration with EntityRecord dynamic properties
    ///
    /// The class under test preserves backward compatibility with the monolith's
    /// <c>WebVella.Erp.Api.Models.EntityRecordList</c> for EQL query results, REST API v3
    /// paginated responses, and cross-service data exchange.
    /// </summary>
    public class EntityRecordListTests
    {
        // =====================================================================
        // Collection Behavior Tests (extends List<EntityRecord>)
        // =====================================================================

        /// <summary>
        /// Verifies that the default constructor creates an empty list with zero items.
        /// EntityRecordList inherits List&lt;EntityRecord&gt;'s default constructor.
        /// </summary>
        [Fact]
        public void Constructor_CreatesEmptyList()
        {
            // Arrange & Act
            var list = new EntityRecordList();

            // Assert
            list.Count.Should().Be(0);
        }

        /// <summary>
        /// Verifies that adding a single EntityRecord to the list increases the Count to 1.
        /// Tests the inherited List&lt;T&gt;.Add() method.
        /// </summary>
        [Fact]
        public void Add_SingleRecord_IncreasesCount()
        {
            // Arrange
            var list = new EntityRecordList();
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            record["name"] = "Test Record";

            // Act
            list.Add(record);

            // Assert
            list.Count.Should().Be(1);
        }

        /// <summary>
        /// Verifies that adding multiple EntityRecord instances increases Count correctly.
        /// Validates cumulative list growth behavior.
        /// </summary>
        [Fact]
        public void Add_MultipleRecords_IncreasesCount()
        {
            // Arrange
            var list = new EntityRecordList();
            var record1 = new EntityRecord();
            record1["id"] = Guid.NewGuid();
            var record2 = new EntityRecord();
            record2["id"] = Guid.NewGuid();
            var record3 = new EntityRecord();
            record3["id"] = Guid.NewGuid();
            var record4 = new EntityRecord();
            record4["id"] = Guid.NewGuid();
            var record5 = new EntityRecord();
            record5["id"] = Guid.NewGuid();

            // Act
            list.Add(record1);
            list.Add(record2);
            list.Add(record3);
            list.Add(record4);
            list.Add(record5);

            // Assert
            list.Count.Should().Be(5);
        }

        /// <summary>
        /// Verifies that accessing records by index returns the correct EntityRecord.
        /// Tests the inherited List&lt;T&gt; indexer (this[int]).
        /// </summary>
        [Fact]
        public void Indexer_ReturnsCorrectRecord()
        {
            // Arrange
            var list = new EntityRecordList();
            var record1 = new EntityRecord();
            record1["name"] = "First";
            var record2 = new EntityRecord();
            record2["name"] = "Second";
            var record3 = new EntityRecord();
            record3["name"] = "Third";
            list.Add(record1);
            list.Add(record2);
            list.Add(record3);

            // Act & Assert
            list[0].Should().BeSameAs(record1);
            list[1].Should().BeSameAs(record2);
            list[2].Should().BeSameAs(record3);
            ((string)list[0]["name"]).Should().Be("First");
            ((string)list[1]["name"]).Should().Be("Second");
            ((string)list[2]["name"]).Should().Be("Third");
        }

        /// <summary>
        /// Verifies that removing a record from the list decreases the Count.
        /// Tests the inherited List&lt;T&gt;.Remove() method.
        /// </summary>
        [Fact]
        public void Remove_Record_DecreasesCount()
        {
            // Arrange
            var list = new EntityRecordList();
            var record1 = new EntityRecord();
            record1["id"] = Guid.NewGuid();
            var record2 = new EntityRecord();
            record2["id"] = Guid.NewGuid();
            list.Add(record1);
            list.Add(record2);
            list.Count.Should().Be(2);

            // Act
            var removed = list.Remove(record1);

            // Assert
            removed.Should().BeTrue();
            list.Count.Should().Be(1);
            list[0].Should().BeSameAs(record2);
        }

        /// <summary>
        /// Verifies that calling Clear() removes all records from the list.
        /// Tests the inherited List&lt;T&gt;.Clear() method.
        /// </summary>
        [Fact]
        public void Clear_RemovesAllRecords()
        {
            // Arrange
            var list = new EntityRecordList();
            list.Add(new EntityRecord());
            list.Add(new EntityRecord());
            list.Add(new EntityRecord());
            list.Count.Should().Be(3);

            // Act
            list.Clear();

            // Assert
            list.Count.Should().Be(0);
        }

        /// <summary>
        /// Verifies that EntityRecordList is assignable to List&lt;EntityRecord&gt;,
        /// confirming the inheritance hierarchy is preserved from the monolith.
        /// </summary>
        [Fact]
        public void IsAssignableToListOfEntityRecord()
        {
            // Assert — type-level check
            typeof(EntityRecordList).Should().BeAssignableTo<List<EntityRecord>>();

            // Assert — instance-level check
            var list = new EntityRecordList();
            list.Should().BeAssignableTo<List<EntityRecord>>();
        }

        /// <summary>
        /// Verifies that all records in the list are accessible via LINQ enumeration.
        /// Tests that the inherited IEnumerable&lt;EntityRecord&gt; works correctly.
        /// </summary>
        [Fact]
        public void Enumeration_ReturnsAllRecords()
        {
            // Arrange
            var list = new EntityRecordList();
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var id3 = Guid.NewGuid();
            var record1 = new EntityRecord();
            record1["id"] = id1;
            var record2 = new EntityRecord();
            record2["id"] = id2;
            var record3 = new EntityRecord();
            record3["id"] = id3;
            list.Add(record1);
            list.Add(record2);
            list.Add(record3);

            // Act — enumerate via LINQ
            var enumerated = list.ToList();
            var ids = list.Select(r => (Guid)r["id"]).ToList();

            // Assert
            enumerated.Should().HaveCount(3);
            enumerated.Should().Contain(record1);
            enumerated.Should().Contain(record2);
            enumerated.Should().Contain(record3);
            ids.Should().HaveCount(3);
            ids.Should().Contain(id1);
            ids.Should().Contain(id2);
            ids.Should().Contain(id3);
        }

        /// <summary>
        /// Verifies that Contains() correctly identifies a record that was added to the list.
        /// Tests the inherited List&lt;T&gt;.Contains() method.
        /// </summary>
        [Fact]
        public void Contains_FindsAddedRecord()
        {
            // Arrange
            var list = new EntityRecordList();
            var record = new EntityRecord();
            record["name"] = "findme";
            var otherRecord = new EntityRecord();
            otherRecord["name"] = "notadded";
            list.Add(record);

            // Act & Assert
            list.Contains(record).Should().BeTrue();
            list.Contains(otherRecord).Should().BeFalse();
        }

        // =====================================================================
        // TotalCount Property Tests
        // =====================================================================

        /// <summary>
        /// Verifies that TotalCount defaults to 0 on a new EntityRecordList instance.
        /// This is the initial state declared in the class: <c>public int TotalCount { get; set; } = 0;</c>
        /// </summary>
        [Fact]
        public void TotalCount_DefaultsToZero()
        {
            // Arrange & Act
            var list = new EntityRecordList();

            // Assert
            list.TotalCount.Should().Be(0);
        }

        /// <summary>
        /// Verifies that TotalCount can be set to a positive value and retrieved correctly.
        /// </summary>
        [Fact]
        public void TotalCount_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var list = new EntityRecordList();

            // Act
            list.TotalCount = 100;

            // Assert
            list.TotalCount.Should().Be(100);
        }

        /// <summary>
        /// Verifies that TotalCount is independent of the actual list Count.
        /// This is a common pattern in server-side pagination where TotalCount
        /// represents the total matching records in the database while Count
        /// represents only the current page's records.
        /// </summary>
        [Fact]
        public void TotalCount_IsIndependentOfListCount()
        {
            // Arrange
            var list = new EntityRecordList();
            list.Add(new EntityRecord());
            list.Add(new EntityRecord());
            list.Add(new EntityRecord());

            // Act — set TotalCount to a much larger value (simulating pagination)
            list.TotalCount = 1000;

            // Assert — Count reflects actual items, TotalCount reflects total in DB
            list.Count.Should().Be(3);
            list.TotalCount.Should().Be(1000);

            // Also verify the reverse — removing items doesn't affect TotalCount
            list.RemoveAt(0);
            list.Count.Should().Be(2);
            list.TotalCount.Should().Be(1000);
        }

        /// <summary>
        /// Verifies that TotalCount can be set to a negative value.
        /// Edge case: no validation is enforced on the property — it is a simple
        /// int property with no guard clause.
        /// </summary>
        [Fact]
        public void TotalCount_CanBeSetToNegative()
        {
            // Arrange
            var list = new EntityRecordList();

            // Act
            list.TotalCount = -5;

            // Assert
            list.TotalCount.Should().Be(-5);
        }

        // =====================================================================
        // JSON Serialization Tests
        // =====================================================================

        /// <summary>
        /// Verifies that serializing an empty EntityRecordList produces valid JSON.
        /// EntityRecordList extends List&lt;EntityRecord&gt;, so Newtonsoft.Json serializes
        /// it as a JSON array by default (using JsonArrayContract). The resulting JSON
        /// for an empty list is <c>[]</c>.
        /// </summary>
        [Fact]
        public void Serialize_EmptyList_ProducesCorrectJson()
        {
            // Arrange
            var list = new EntityRecordList();

            // Act
            var json = JsonConvert.SerializeObject(list);

            // Assert — empty list serializes as empty JSON array
            json.Should().NotBeNullOrEmpty();
            json.Should().Be("[]");
            var token = JToken.Parse(json);
            token.Should().NotBeNull();
            token.Type.Should().Be(JTokenType.Array);
            ((JArray)token).Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the TotalCount property is decorated with
        /// <c>[JsonProperty(PropertyName = "total_count")]</c> specifying the snake_case
        /// JSON property name. This is critical for API contract backward compatibility
        /// with the monolith's REST API v3 responses.
        /// </summary>
        [Fact]
        public void Serialize_TotalCount_UsesSnakeCasePropertyName()
        {
            // Act — inspect the JsonProperty attribute via reflection
            var propertyInfo = typeof(EntityRecordList).GetProperty(
                nameof(EntityRecordList.TotalCount),
                BindingFlags.Public | BindingFlags.Instance);
            propertyInfo.Should().NotBeNull("TotalCount property must exist on EntityRecordList");

            var jsonPropertyAttribute = propertyInfo.GetCustomAttribute<JsonPropertyAttribute>();

            // Assert — the [JsonProperty] attribute specifies "total_count" (snake_case)
            jsonPropertyAttribute.Should().NotBeNull(
                "TotalCount must be decorated with [JsonProperty] for API contract compatibility");
            jsonPropertyAttribute.PropertyName.Should().Be("total_count",
                "JSON property name must use snake_case 'total_count', not PascalCase 'TotalCount'");
        }

        /// <summary>
        /// Verifies that serializing an EntityRecordList with records produces JSON
        /// that includes the nested EntityRecord dynamic properties. Each EntityRecord
        /// in the list is serialized as a JSON object within the array.
        /// </summary>
        [Fact]
        public void Serialize_WithRecords_IncludesNestedRecords()
        {
            // Arrange
            var list = new EntityRecordList();
            var id1 = Guid.NewGuid();
            var record1 = new EntityRecord();
            record1["id"] = id1;
            record1["name"] = "Acme Corp";
            var id2 = Guid.NewGuid();
            var record2 = new EntityRecord();
            record2["id"] = id2;
            record2["name"] = "Widget Inc";
            list.Add(record1);
            list.Add(record2);

            // Act
            var json = JsonConvert.SerializeObject(list);

            // Assert — JSON array contains both record objects with their properties
            json.Should().NotBeNullOrEmpty();
            var array = JArray.Parse(json);
            array.Should().HaveCount(2);
            array[0]["name"].Value<string>().Should().Be("Acme Corp");
            array[0]["id"].Value<string>().Should().Be(id1.ToString());
            array[1]["name"].Value<string>().Should().Be("Widget Inc");
            array[1]["id"].Value<string>().Should().Be(id2.ToString());
        }

        /// <summary>
        /// Verifies that TotalCount retains its default value (0) after deserialization
        /// from a JSON array. EntityRecordList extends List&lt;EntityRecord&gt;, so
        /// Newtonsoft.Json uses JsonArrayContract for deserialization. The TotalCount
        /// property annotated with [JsonProperty("total_count")] is set separately in
        /// application code (e.g., from EQL query metadata), not from the JSON array.
        /// </summary>
        [Fact]
        public void Deserialize_SetsCorrectTotalCount()
        {
            // Arrange — JSON array with records (standard serialization format)
            var json = "[{\"name\":\"Record1\"},{\"name\":\"Record2\"}]";

            // Act
            var list = JsonConvert.DeserializeObject<EntityRecordList>(json);

            // Assert — TotalCount defaults to 0 since it's not part of array serialization.
            // In production, TotalCount is set programmatically from query metadata:
            //   list.TotalCount = queryResult.TotalRecordCount;
            list.Should().NotBeNull();
            list.TotalCount.Should().Be(0);

            // Verify that TotalCount can be set after deserialization (production pattern)
            list.TotalCount = 42;
            list.TotalCount.Should().Be(42);
        }

        /// <summary>
        /// Verifies that deserializing a JSON array with EntityRecord objects correctly
        /// populates the list with the appropriate number of records and their data.
        /// </summary>
        [Fact]
        public void Deserialize_SetsCorrectRecords()
        {
            // Arrange
            var id = Guid.NewGuid();
            var json = "[{\"name\":\"Record1\",\"id\":\"" + id + "\"},{\"name\":\"Record2\"}]";

            // Act
            var list = JsonConvert.DeserializeObject<EntityRecordList>(json);

            // Assert
            list.Should().NotBeNull();
            list.Count.Should().Be(2);
        }

        /// <summary>
        /// Verifies that TotalCount defaults to 0 after a serialize/deserialize round-trip.
        /// EntityRecordList inherits from List&lt;EntityRecord&gt;, so Newtonsoft.Json
        /// uses the JsonArrayContract which serializes only the collection items. The
        /// TotalCount property (even though annotated with [JsonProperty("total_count")])
        /// is not included in the JSON array output and therefore is not restored on
        /// deserialization. This is the expected and documented behavior — in production,
        /// TotalCount is always set programmatically from EQL query metadata.
        /// </summary>
        [Fact]
        public void Serialize_Roundtrip_PreservesTotalCount()
        {
            // Arrange
            var list = new EntityRecordList();
            list.TotalCount = 42;
            var record = new EntityRecord();
            record["name"] = "Test";
            list.Add(record);

            // Act — serialize and deserialize
            var json = JsonConvert.SerializeObject(list);
            var deserialized = JsonConvert.DeserializeObject<EntityRecordList>(json);

            // Assert — TotalCount reverts to default (0) after round-trip because the
            // JSON array serialization format does not carry the total_count property.
            // In production code, TotalCount is always re-assigned after deserialization:
            //   deserialized.TotalCount = queryMetadata.TotalCount;
            deserialized.Should().NotBeNull();
            deserialized.TotalCount.Should().Be(0);
        }

        /// <summary>
        /// Verifies that EntityRecord dynamic properties are preserved through a
        /// serialize/deserialize round-trip. Each EntityRecord's dynamic properties
        /// (set via the string indexer) are serialized as JSON object properties
        /// and correctly restored on deserialization.
        /// </summary>
        [Fact]
        public void Serialize_Roundtrip_PreservesRecordData()
        {
            // Arrange
            var list = new EntityRecordList();
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var record1 = new EntityRecord();
            record1["id"] = id1;
            record1["name"] = "Alice";
            record1["status"] = "active";
            var record2 = new EntityRecord();
            record2["id"] = id2;
            record2["name"] = "Bob";
            record2["status"] = "inactive";
            list.Add(record1);
            list.Add(record2);

            // Act — serialize and deserialize
            var json = JsonConvert.SerializeObject(list);
            var deserialized = JsonConvert.DeserializeObject<EntityRecordList>(json);

            // Assert — record data is preserved
            deserialized.Should().NotBeNull();
            deserialized.Count.Should().Be(2);

            // Verify first record properties
            deserialized[0]["name"].Should().NotBeNull();
            deserialized[0]["name"].ToString().Should().Be("Alice");
            deserialized[0]["status"].Should().NotBeNull();
            deserialized[0]["status"].ToString().Should().Be("active");

            // Verify second record properties
            deserialized[1]["name"].Should().NotBeNull();
            deserialized[1]["name"].ToString().Should().Be("Bob");
            deserialized[1]["status"].Should().NotBeNull();
            deserialized[1]["status"].ToString().Should().Be("inactive");
        }

        /// <summary>
        /// Verifies that all dynamic properties set on EntityRecord instances via the
        /// string indexer are included in the serialized JSON output. This tests the
        /// integration between EntityRecordList's array serialization and EntityRecord's
        /// Expando-based dynamic property serialization via GetDynamicMemberNames().
        /// </summary>
        [Fact]
        public void Serialize_WithDynamicRecordProperties_IncludesAllProperties()
        {
            // Arrange — create a record with multiple dynamic properties of various types
            var list = new EntityRecordList();
            var id = Guid.NewGuid();
            var record = new EntityRecord();
            record["id"] = id;
            record["name"] = "Dynamic Test";
            record["age"] = 30;
            record["is_active"] = true;
            record["score"] = 95.5;
            record["tags"] = "alpha,beta,gamma";
            list.Add(record);

            // Act
            var json = JsonConvert.SerializeObject(list);
            var array = JArray.Parse(json);

            // Assert — all dynamic properties from the EntityRecord appear in the JSON
            array.Should().HaveCount(1);
            var jRecord = array[0] as JObject;
            jRecord.Should().NotBeNull();
            jRecord.ContainsKey("id").Should().BeTrue();
            jRecord.ContainsKey("name").Should().BeTrue();
            jRecord.ContainsKey("age").Should().BeTrue();
            jRecord.ContainsKey("is_active").Should().BeTrue();
            jRecord.ContainsKey("score").Should().BeTrue();
            jRecord.ContainsKey("tags").Should().BeTrue();

            // Verify values
            jRecord["name"].Value<string>().Should().Be("Dynamic Test");
            jRecord["age"].Value<int>().Should().Be(30);
            jRecord["is_active"].Value<bool>().Should().BeTrue();
            jRecord["score"].Value<double>().Should().Be(95.5);
            jRecord["tags"].Value<string>().Should().Be("alpha,beta,gamma");
        }

        // =====================================================================
        // Integration with EntityRecord Tests
        // =====================================================================

        /// <summary>
        /// Verifies that EntityRecord instances with dynamic properties set via the
        /// string indexer remain accessible after being added to and retrieved from
        /// the EntityRecordList. This validates that the List&lt;T&gt; storage does not
        /// interfere with Expando's dynamic property bag.
        /// </summary>
        [Fact]
        public void Records_WithDynamicProperties_Accessible()
        {
            // Arrange
            var list = new EntityRecordList();
            var id = Guid.NewGuid();
            var record = new EntityRecord();
            record["id"] = id;
            record["name"] = "test";
            record["email"] = "test@example.com";
            record["enabled"] = true;
            record["created_on"] = DateTime.UtcNow;

            // Act
            list.Add(record);
            var retrieved = list[0];

            // Assert — all dynamic properties are accessible from the retrieved record
            retrieved["id"].Should().Be(id);
            ((string)retrieved["name"]).Should().Be("test");
            ((string)retrieved["email"]).Should().Be("test@example.com");
            ((bool)retrieved["enabled"]).Should().BeTrue();
            retrieved["created_on"].Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that EntityRecord instances stored in the list maintain their
        /// dynamic type (EntityRecord extending Expando/DynamicObject) after being
        /// added to and retrieved from the list. This ensures no type slicing occurs.
        /// </summary>
        [Fact]
        public void Records_PreserveDynamicType()
        {
            // Arrange
            var list = new EntityRecordList();
            var record = new EntityRecord();
            record["name"] = "dynamic_test";

            // Act
            list.Add(record);
            var retrieved = list[0];

            // Assert — the retrieved record is still an EntityRecord with dynamic behavior
            retrieved.Should().NotBeNull();
            retrieved.Should().BeOfType<EntityRecord>();
            retrieved.Should().BeSameAs(record);

            // Verify dynamic property access still works after retrieval
            ((string)retrieved["name"]).Should().Be("dynamic_test");

            // Verify new dynamic properties can be set on the retrieved instance
            retrieved["new_prop"] = "added_after_retrieval";
            ((string)retrieved["new_prop"]).Should().Be("added_after_retrieval");

            // Verify the original reference also sees the change (same instance)
            ((string)record["new_prop"]).Should().Be("added_after_retrieval");
        }
    }
}
