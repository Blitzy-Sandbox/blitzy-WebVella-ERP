using System;
using System.Collections.Generic;
using System.Dynamic;
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
    /// Comprehensive unit tests for the <see cref="EntityRecord"/> class from SharedKernel.
    /// EntityRecord is the foundational data carrier for all record operations across every
    /// microservice. It inherits from Expando (a DynamicObject-based property bag) and provides
    /// dynamic property access via a Properties dictionary, enabling the dynamic entity-field
    /// model where field names are determined at runtime from entity metadata.
    ///
    /// Tests validate:
    /// - Construction and type metadata ([Serializable], DynamicObject inheritance)
    /// - Dynamic property get/set via string indexer and dynamic dispatch
    /// - PropertyBag dictionary-like access (Properties, Contains, GetProperties)
    /// - JSON serialization/deserialization fidelity with Newtonsoft.Json
    /// - Property enumeration via GetProperties() and GetDynamicMemberNames()
    /// </summary>
    public class EntityRecordTests
    {
        // =====================================================================
        // Construction and Type Metadata Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the default constructor creates an EntityRecord with zero
        /// dynamic properties in the underlying Properties dictionary.
        /// </summary>
        [Fact]
        public void Constructor_CreatesEmptyRecord()
        {
            // Arrange & Act
            var record = new EntityRecord();

            // Assert
            record.Properties.Count.Should().Be(0);
        }

        /// <summary>
        /// Verifies that EntityRecord is decorated with the [Serializable] attribute,
        /// preserving backward compatibility with the monolith's serialization contract.
        /// </summary>
        [Fact]
        public void IsSerializableAttribute()
        {
            // Assert
            typeof(EntityRecord).Should().BeDecoratedWith<SerializableAttribute>();
        }

        /// <summary>
        /// Verifies that EntityRecord inherits from DynamicObject through the Expando
        /// base class, enabling dynamic member dispatch for property access.
        /// </summary>
        [Fact]
        public void InheritsFromExpando()
        {
            // Assert
            typeof(EntityRecord).Should().BeAssignableTo<DynamicObject>();
        }

        /// <summary>
        /// Verifies that an EntityRecord can be cast to dynamic and used for
        /// dynamic property get/set at runtime without compilation errors.
        /// </summary>
        [Fact]
        public void CanBeCastToDynamic()
        {
            // Arrange
            var record = new EntityRecord();

            // Act — cast to dynamic and perform a round-trip property set/get
            dynamic dynRecord = record;
            dynRecord.testField = "hello";
            string value = dynRecord.testField;

            // Assert
            value.Should().Be("hello");
        }

        // =====================================================================
        // String Indexer Tests
        // =====================================================================

        /// <summary>
        /// Verifies that a property set via the string indexer can be retrieved
        /// using the same key.
        /// </summary>
        [Fact]
        public void Indexer_SetProperty_CanBeRetrieved()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            record["name"] = "test";

            // Assert
            record["name"].Should().Be("test");
        }

        /// <summary>
        /// Verifies that multiple properties set via the string indexer are all
        /// independently retrievable with correct values.
        /// </summary>
        [Fact]
        public void Indexer_SetMultipleProperties_AllRetrievable()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            record["first"] = "John";
            record["last"] = "Doe";
            record["age"] = 30;

            // Assert
            record["first"].Should().Be("John");
            record["last"].Should().Be("Doe");
            record["age"].Should().Be(30);
        }

        /// <summary>
        /// Verifies that null can be stored as a property value via the indexer
        /// and retrieved as null without throwing.
        /// </summary>
        [Fact]
        public void Indexer_SetNullValue_StoresNull()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            record["field"] = null;

            // Assert
            record["field"].Should().BeNull();
        }

        /// <summary>
        /// Verifies that a Guid value stored via the indexer is preserved exactly
        /// (reference-type equality for boxed Guids).
        /// </summary>
        [Fact]
        public void Indexer_SetGuidValue_StoresCorrectly()
        {
            // Arrange
            var record = new EntityRecord();
            var guid = Guid.NewGuid();

            // Act
            record["id"] = guid;

            // Assert
            record["id"].Should().Be(guid);
        }

        /// <summary>
        /// Verifies that a DateTime value stored via the indexer is preserved exactly.
        /// </summary>
        [Fact]
        public void Indexer_SetDateTimeValue_StoresCorrectly()
        {
            // Arrange
            var record = new EntityRecord();
            var now = DateTime.UtcNow;

            // Act
            record["created_on"] = now;

            // Assert
            record["created_on"].Should().Be(now);
        }

        /// <summary>
        /// Verifies that an integer value stored via the indexer is preserved exactly.
        /// </summary>
        [Fact]
        public void Indexer_SetIntValue_StoresCorrectly()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            record["count"] = 42;

            // Assert
            record["count"].Should().Be(42);
        }

        /// <summary>
        /// Verifies that a List&lt;object&gt; stored via the indexer is preserved
        /// by reference (same list instance retrieved).
        /// </summary>
        [Fact]
        public void Indexer_SetListValue_StoresCorrectly()
        {
            // Arrange
            var record = new EntityRecord();
            var list = new List<object> { "alpha", "beta", "gamma" };

            // Act
            record["items"] = list;

            // Assert
            record["items"].Should().BeSameAs(list);
            var retrieved = (List<object>)record["items"];
            retrieved.Should().HaveCount(3);
            retrieved[0].Should().Be("alpha");
            retrieved[1].Should().Be("beta");
            retrieved[2].Should().Be("gamma");
        }

        /// <summary>
        /// Verifies that a nested EntityRecord stored via the indexer is preserved
        /// by reference and its inner properties remain accessible.
        /// </summary>
        [Fact]
        public void Indexer_SetNestedEntityRecord_StoresCorrectly()
        {
            // Arrange
            var record = new EntityRecord();
            var nested = new EntityRecord();
            nested["key"] = "inner_value";

            // Act
            record["nested"] = nested;

            // Assert
            var retrieved = record["nested"] as EntityRecord;
            retrieved.Should().NotBeNull();
            retrieved.Should().BeSameAs(nested);
            retrieved["key"].Should().Be("inner_value");
        }

        /// <summary>
        /// Verifies that setting the same key twice via the indexer overwrites
        /// the previous value, and the latest value is returned.
        /// </summary>
        [Fact]
        public void Indexer_OverwriteExistingProperty()
        {
            // Arrange
            var record = new EntityRecord();
            record["status"] = "draft";

            // Act
            record["status"] = "published";

            // Assert
            record["status"].Should().Be("published");
        }

        /// <summary>
        /// Verifies that accessing a non-existent key via the string indexer throws
        /// a KeyNotFoundException, since the Expando first checks Properties dictionary
        /// (which throws) and then falls back to reflection (which fails for EntityRecord).
        /// </summary>
        [Fact]
        public void Indexer_NonExistentKey_ThrowsKeyNotFoundException()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            Action act = () => { var _ = record["nonexistent"]; };

            // Assert
            act.Should().Throw<KeyNotFoundException>();
        }

        // =====================================================================
        // Dynamic Dispatch Tests
        // =====================================================================

        /// <summary>
        /// Verifies that a property set via dynamic dispatch can be retrieved
        /// via dynamic dispatch.
        /// </summary>
        [Fact]
        public void Dynamic_SetProperty_CanBeRetrieved()
        {
            // Arrange
            dynamic record = new EntityRecord();

            // Act
            record.name = "test";

            // Assert
            ((string)record.name).Should().Be("test");
        }

        /// <summary>
        /// Verifies that multiple properties set via dynamic dispatch are all
        /// independently retrievable with correct values.
        /// </summary>
        [Fact]
        public void Dynamic_SetMultipleProperties()
        {
            // Arrange
            dynamic record = new EntityRecord();

            // Act
            record.first = "John";
            record.last = "Doe";
            record.age = 30;

            // Assert
            ((string)record.first).Should().Be("John");
            ((string)record.last).Should().Be("Doe");
            ((int)record.age).Should().Be(30);
        }

        /// <summary>
        /// Verifies that a property set via the string indexer is accessible
        /// via dynamic dispatch. This tests the bidirectional bridge between
        /// the indexer and TryGetMember in Expando.
        /// </summary>
        [Fact]
        public void Dynamic_PropertySetViaIndexer_AccessibleViaDynamic()
        {
            // Arrange
            var record = new EntityRecord();
            record["name"] = "indexer_value";

            // Act
            dynamic dyn = record;
            string retrieved = dyn.name;

            // Assert
            retrieved.Should().Be("indexer_value");
        }

        /// <summary>
        /// Verifies that a property set via dynamic dispatch is accessible
        /// via the string indexer. This tests the bidirectional bridge between
        /// TrySetMember and the indexer getter in Expando.
        /// </summary>
        [Fact]
        public void Dynamic_PropertySetViaDynamic_AccessibleViaIndexer()
        {
            // Arrange
            dynamic dynRecord = new EntityRecord();
            dynRecord.name = "dynamic_value";

            // Act
            EntityRecord record = dynRecord;
            var retrieved = record["name"];

            // Assert
            retrieved.Should().Be("dynamic_value");
        }

        // =====================================================================
        // Properties Dictionary / PropertyBag Tests
        // =====================================================================

        /// <summary>
        /// Verifies that a newly constructed EntityRecord has an empty Properties
        /// dictionary (PropertyBag).
        /// </summary>
        [Fact]
        public void Properties_EmptyOnConstruction()
        {
            // Arrange & Act
            var record = new EntityRecord();

            // Assert
            record.Properties.Count.Should().Be(0);
        }

        /// <summary>
        /// Verifies that setting a value via the string indexer is reflected
        /// in the underlying Properties dictionary.
        /// </summary>
        [Fact]
        public void Properties_ReflectsIndexerSets()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            record["x"] = 1;

            // Assert
            record.Properties.ContainsKey("x").Should().BeTrue();
            record.Properties["x"].Should().Be(1);
        }

        /// <summary>
        /// Verifies that GetProperties(false) returns all dynamic properties
        /// that were added to the EntityRecord, with correct keys and values.
        /// The includeInstanceProperties=false parameter means only Properties
        /// dictionary entries are enumerated.
        /// </summary>
        [Fact]
        public void GetProperties_ReturnsAllDynamicProperties()
        {
            // Arrange
            var record = new EntityRecord();
            record["a"] = 1;
            record["b"] = "two";
            record["c"] = true;

            // Act
            var props = record.GetProperties(false).ToList();

            // Assert
            props.Should().HaveCount(3);
            props.Select(p => p.Key).Should().Contain("a")
                .And.Contain("b")
                .And.Contain("c");
            props.First(p => p.Key == "a").Value.Should().Be(1);
            props.First(p => p.Key == "b").Value.Should().Be("two");
            props.First(p => p.Key == "c").Value.Should().Be(true);
        }

        /// <summary>
        /// Verifies that GetProperties(true) includes all properties from the
        /// Properties dictionary plus any instance properties discovered via
        /// reflection. For EntityRecord (which declares no instance properties),
        /// the result is equivalent to GetProperties(false), but the method
        /// correctly exercises the includeInstanceProperties code path.
        /// </summary>
        [Fact]
        public void GetProperties_IncludeInstanceProperties_ReturnsExpandoProperties()
        {
            // Arrange
            var record = new EntityRecord();
            record["x"] = 10;
            record["y"] = 20;

            // Act
            var withInstance = record.GetProperties(true).ToList();
            var withoutInstance = record.GetProperties(false).ToList();

            // Assert — EntityRecord has no declared C# properties, so both calls
            // return the same set of dynamic properties from the Properties dictionary
            withInstance.Should().HaveCount(withoutInstance.Count);
            withInstance.Select(p => p.Key).Should().Contain("x").And.Contain("y");
            withInstance.First(p => p.Key == "x").Value.Should().Be(10);
            withInstance.First(p => p.Key == "y").Value.Should().Be(20);
        }

        /// <summary>
        /// Verifies that Contains returns true for a key that exists in the
        /// Properties dictionary.
        /// </summary>
        [Fact]
        public void Contains_ExistingKey_ReturnsTrue()
        {
            // Arrange
            var record = new EntityRecord();
            record["key"] = "value";

            // Act
            var result = record.Contains(new KeyValuePair<string, object>("key", null));

            // Assert
            result.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that Contains returns false for a key that does not exist
        /// in the Properties dictionary.
        /// </summary>
        [Fact]
        public void Contains_NonExistingKey_ReturnsFalse()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            var result = record.Contains(new KeyValuePair<string, object>("missing", null));

            // Assert
            result.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that GetDynamicMemberNames() returns all property names stored
        /// in the EntityRecord. This method is critical for Newtonsoft.Json serialization
        /// as Json.NET calls it to discover which dynamic members to serialize.
        /// </summary>
        [Fact]
        public void GetDynamicMemberNames_ReturnsAllPropertyNames()
        {
            // Arrange
            var record = new EntityRecord();
            record["alpha"] = 1;
            record["beta"] = 2;
            record["gamma"] = 3;

            // Act
            var names = record.GetDynamicMemberNames().ToList();

            // Assert
            names.Should().HaveCount(3);
            names.Should().Contain("alpha")
                .And.Contain("beta")
                .And.Contain("gamma");
        }

        // =====================================================================
        // JSON Serialization / Deserialization Tests (Newtonsoft.Json)
        // =====================================================================

        /// <summary>
        /// Verifies that an empty EntityRecord serializes to an empty JSON object "{}".
        /// This is critical because EntityRecord has no declared C# properties, and
        /// the Properties field (a PropertyBag) is NOT included in Newtonsoft.Json
        /// serialization of DynamicObject subclasses — only dynamic members from
        /// GetDynamicMemberNames() are serialized.
        /// </summary>
        [Fact]
        public void Serialize_EmptyRecord_ProducesEmptyJson()
        {
            // Arrange
            var record = new EntityRecord();

            // Act
            var json = JsonConvert.SerializeObject(record);

            // Assert
            json.Should().Be("{}");
        }

        /// <summary>
        /// Verifies that all dynamic properties set on an EntityRecord appear
        /// in the serialized JSON output with correct keys and values.
        /// </summary>
        [Fact]
        public void Serialize_WithDynamicProperties_IncludesAll()
        {
            // Arrange
            var record = new EntityRecord();
            var guid = Guid.NewGuid();
            record["id"] = guid;
            record["name"] = "test";

            // Act
            var json = JsonConvert.SerializeObject(record);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("id").Should().BeTrue();
            jObj.ContainsKey("name").Should().BeTrue();
            jObj["id"].ToString().Should().Be(guid.ToString());
            jObj["name"].ToString().Should().Be("test");
        }

        /// <summary>
        /// Verifies that various .NET types (Guid, DateTime, int, bool, null) are
        /// serialized correctly in the JSON output, preserving their JSON representations.
        /// </summary>
        [Fact]
        public void Serialize_DynamicPropertyTypes_PreservedCorrectly()
        {
            // Arrange
            var record = new EntityRecord();
            var guid = Guid.NewGuid();
            var now = DateTime.UtcNow;
            record["guid_field"] = guid;
            record["date_field"] = now;
            record["int_field"] = 42;
            record["bool_field"] = true;
            record["null_field"] = null;

            // Act
            var json = JsonConvert.SerializeObject(record);
            var jObj = JObject.Parse(json);

            // Assert — Guid serializes as string
            jObj["guid_field"].Type.Should().Be(JTokenType.String);
            jObj["guid_field"].ToString().Should().Be(guid.ToString());

            // Assert — DateTime serializes as ISO 8601 string, which JObject.Parse
            // interprets as JTokenType.Date due to default date parsing behavior
            jObj["date_field"].Type.Should().Be(JTokenType.Date);

            // Assert — int serializes as JSON integer
            jObj["int_field"].Type.Should().Be(JTokenType.Integer);
            jObj["int_field"].Value<int>().Should().Be(42);

            // Assert — bool serializes as JSON boolean
            jObj["bool_field"].Type.Should().Be(JTokenType.Boolean);
            jObj["bool_field"].Value<bool>().Should().BeTrue();

            // Assert — null serializes as JSON null
            jObj["null_field"].Type.Should().Be(JTokenType.Null);
        }

        /// <summary>
        /// Verifies that deserializing a JSON string into an EntityRecord correctly
        /// populates properties accessible via the string indexer. Note that JSON
        /// integer values are deserialized as System.Int64 (long) and Guid strings
        /// remain as System.String when targeting a DynamicObject.
        /// </summary>
        [Fact]
        public void Deserialize_JsonToEntityRecord_SetsProperties()
        {
            // Arrange
            var guid = Guid.NewGuid();
            var json = $"{{\"name\":\"test\",\"id\":\"{guid}\",\"count\":42}}";

            // Act
            var record = JsonConvert.DeserializeObject<EntityRecord>(json);

            // Assert
            record.Should().NotBeNull();
            record["name"].Should().Be("test");
            record["id"].Should().Be(guid.ToString()); // Guid string remains string
            record["count"].Should().BeOfType<long>(); // JSON integers deserialize as Int64
            ((long)record["count"]).Should().Be(42L);
        }

        /// <summary>
        /// Verifies that serializing an EntityRecord and then deserializing the
        /// result produces an EntityRecord with equivalent property values.
        /// Note: round-trip type conversions apply (int→long, Guid→string).
        /// String and bool values are preserved exactly.
        /// </summary>
        [Fact]
        public void Serialize_Roundtrip_PreservesAllProperties()
        {
            // Arrange
            var original = new EntityRecord();
            original["name"] = "Acme Corp";
            original["active"] = true;
            original["description"] = "A test corporation";

            // Act
            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<EntityRecord>(json);

            // Assert
            deserialized.Should().NotBeNull();
            deserialized["name"].Should().Be("Acme Corp");
            deserialized["active"].Should().Be(true);
            deserialized["description"].Should().Be("A test corporation");

            // Verify all original properties exist in the deserialized record
            deserialized.Properties.Count.Should().Be(original.Properties.Count);
        }

        /// <summary>
        /// Verifies that a nested EntityRecord serializes correctly as a nested
        /// JSON object. Note: when deserialized, the nested object becomes a
        /// Newtonsoft.Json.Linq.JObject rather than an EntityRecord, because
        /// the dynamic property type information is not preserved in JSON.
        /// </summary>
        [Fact]
        public void Serialize_NestedEntityRecord_HandledCorrectly()
        {
            // Arrange
            var record = new EntityRecord();
            var nested = new EntityRecord();
            nested["key"] = "inner_value";
            nested["num"] = 123;
            record["child"] = nested;
            record["label"] = "parent";

            // Act
            var json = JsonConvert.SerializeObject(record);
            var jObj = JObject.Parse(json);

            // Assert — nested record serializes as a JSON object
            jObj.ContainsKey("child").Should().BeTrue();
            jObj["child"].Type.Should().Be(JTokenType.Object);
            jObj["child"]["key"].ToString().Should().Be("inner_value");
            jObj["child"]["num"].Value<int>().Should().Be(123);
            jObj["label"].ToString().Should().Be("parent");
        }

        /// <summary>
        /// Verifies that a list property serializes correctly as a JSON array.
        /// </summary>
        [Fact]
        public void Serialize_WithListProperty_Serialized()
        {
            // Arrange
            var record = new EntityRecord();
            record["tags"] = new List<string> { "crm", "enterprise", "active" };
            record["name"] = "tagged_record";

            // Act
            var json = JsonConvert.SerializeObject(record);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("tags").Should().BeTrue();
            jObj["tags"].Type.Should().Be(JTokenType.Array);
            jObj["tags"].Count().Should().Be(3);
            jObj["tags"][0].ToString().Should().Be("crm");
            jObj["tags"][1].ToString().Should().Be("enterprise");
            jObj["tags"][2].ToString().Should().Be("active");
            jObj["name"].ToString().Should().Be("tagged_record");
        }

        /// <summary>
        /// Verifies that null property values are included in the serialized JSON
        /// output (not omitted). This is important for API contract fidelity —
        /// callers may distinguish between a missing field and a field set to null.
        /// </summary>
        [Fact]
        public void Serialize_NullPropertyValues_IncludedInJson()
        {
            // Arrange
            var record = new EntityRecord();
            record["present"] = "value";
            record["absent"] = null;

            // Act
            var json = JsonConvert.SerializeObject(record);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("present").Should().BeTrue();
            jObj.ContainsKey("absent").Should().BeTrue();
            jObj["present"].ToString().Should().Be("value");
            jObj["absent"].Type.Should().Be(JTokenType.Null);
        }

        // =====================================================================
        // Property Enumeration Tests
        // =====================================================================

        /// <summary>
        /// Verifies that iterating over GetProperties(false) with foreach yields
        /// all five properties that were added, with correct key-value pairs.
        /// </summary>
        [Fact]
        public void PropertyEnumeration_ForEach_YieldsAllProperties()
        {
            // Arrange
            var record = new EntityRecord();
            record["one"] = 1;
            record["two"] = 2;
            record["three"] = 3;
            record["four"] = 4;
            record["five"] = 5;

            // Act
            var collected = new List<KeyValuePair<string, object>>();
            foreach (var kvp in record.GetProperties(false))
            {
                collected.Add(kvp);
            }

            // Assert
            collected.Should().HaveCount(5);
            collected.Select(c => c.Key).Should()
                .Contain("one")
                .And.Contain("two")
                .And.Contain("three")
                .And.Contain("four")
                .And.Contain("five");
        }

        /// <summary>
        /// Verifies that the LINQ Count() on GetProperties(false) matches the
        /// number of properties that were set on the EntityRecord.
        /// </summary>
        [Fact]
        public void PropertyEnumeration_LinqCount_MatchesPropertyCount()
        {
            // Arrange
            var record = new EntityRecord();
            record["a"] = "alpha";
            record["b"] = "beta";
            record["c"] = "gamma";

            // Act
            var count = record.GetProperties(false).Count();

            // Assert
            count.Should().Be(3);
            count.Should().Be(record.Properties.Count);
        }

        /// <summary>
        /// Verifies that all keys yielded by property enumeration can be used
        /// with the string indexer to retrieve the correct values.
        /// </summary>
        [Fact]
        public void PropertyEnumeration_KeysAreAccessible()
        {
            // Arrange
            var record = new EntityRecord();
            record["id"] = Guid.NewGuid();
            record["name"] = "Accessible";
            record["status"] = "active";
            record["count"] = 7;

            // Act
            var keys = record.GetProperties(false).Select(p => p.Key).ToList();

            // Assert — every enumerated key can be used with the indexer
            foreach (var key in keys)
            {
                Action act = () => { var _ = record[key]; };
                act.Should().NotThrow();
            }

            // Also verify specific values are accessible via enumerated keys
            keys.Should().HaveCount(4);
            record[keys.First(k => k == "name")].Should().Be("Accessible");
            record[keys.First(k => k == "status")].Should().Be("active");
            record[keys.First(k => k == "count")].Should().Be(7);
        }
    }
}
