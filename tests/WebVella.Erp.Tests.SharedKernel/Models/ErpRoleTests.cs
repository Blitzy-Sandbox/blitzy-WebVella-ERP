using System;
using System.Reflection;
using Xunit;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Models
{
    /// <summary>
    /// Unit tests for the <see cref="ErpRole"/> shared kernel model.
    /// Validates property behavior, default values, JSON serialization contract
    /// (lowercase property names via [JsonProperty]), and [Serializable] attribute metadata.
    /// </summary>
    public class ErpRoleTests
    {
        #region Type and Attribute Tests

        /// <summary>
        /// Verifies that the ErpRole class is decorated with the [Serializable] attribute,
        /// which is required for binary serialization compatibility and was present in the
        /// original monolith ErpRole model.
        /// </summary>
        [Fact]
        public void IsSerializableAttribute()
        {
            // Act & Assert
            typeof(ErpRole).Should().BeDecoratedWith<SerializableAttribute>();
        }

        /// <summary>
        /// Verifies that the ErpRole class exposes exactly three public instance properties:
        /// Id (Guid), Name (string), and Description (string). This ensures the DTO contract
        /// has not been accidentally expanded or reduced during migration to SharedKernel.
        /// </summary>
        [Fact]
        public void HasThreePublicProperties()
        {
            // Act
            var properties = typeof(ErpRole).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // Assert
            properties.Should().HaveCount(3);
        }

        #endregion

        #region Default Value Tests

        /// <summary>
        /// Verifies that a newly constructed ErpRole has its Id property defaulting to
        /// Guid.Empty, which is the CLR default for the Guid value type. The monolith
        /// source sets no explicit default for Id.
        /// </summary>
        [Fact]
        public void Constructor_Id_DefaultsToGuidEmpty()
        {
            // Act
            var role = new ErpRole();

            // Assert
            role.Id.Should().Be(Guid.Empty);
        }

        /// <summary>
        /// Verifies that a newly constructed ErpRole has its Name property defaulting to null.
        /// The monolith source does not assign a default value to the Name property, so the
        /// CLR default for string (null) should be used.
        /// </summary>
        [Fact]
        public void Constructor_Name_DefaultsToNull()
        {
            // Act
            var role = new ErpRole();

            // Assert
            role.Name.Should().BeNull();
        }

        /// <summary>
        /// Verifies that a newly constructed ErpRole has its Description property defaulting
        /// to null. The monolith source does not assign a default value to the Description
        /// property, so the CLR default for string (null) should be used.
        /// </summary>
        [Fact]
        public void Constructor_Description_DefaultsToNull()
        {
            // Act
            var role = new ErpRole();

            // Assert
            role.Description.Should().BeNull();
        }

        #endregion

        #region Property Get/Set Tests

        /// <summary>
        /// Verifies that the Id property correctly stores and returns a known GUID value.
        /// </summary>
        [Fact]
        public void Id_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var role = new ErpRole();
            var expectedId = Guid.NewGuid();

            // Act
            role.Id = expectedId;

            // Assert
            role.Id.Should().Be(expectedId);
        }

        /// <summary>
        /// Verifies that the Name property correctly stores and returns a known string value.
        /// </summary>
        [Fact]
        public void Name_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var role = new ErpRole();

            // Act
            role.Name = "Administrator";

            // Assert
            role.Name.Should().Be("Administrator");
        }

        /// <summary>
        /// Verifies that the Description property correctly stores and returns a known string value.
        /// </summary>
        [Fact]
        public void Description_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var role = new ErpRole();

            // Act
            role.Description = "Admin role description";

            // Assert
            role.Description.Should().Be("Admin role description");
        }

        /// <summary>
        /// Edge case: verifies that setting Id to Guid.Empty explicitly returns Guid.Empty.
        /// This tests that the property setter does not reject or transform empty GUIDs.
        /// </summary>
        [Fact]
        public void Id_SetToGuidEmpty_ReturnsGuidEmpty()
        {
            // Arrange
            var role = new ErpRole();
            role.Id = Guid.NewGuid(); // Set to non-empty first

            // Act
            role.Id = Guid.Empty;

            // Assert
            role.Id.Should().Be(Guid.Empty);
        }

        /// <summary>
        /// Edge case: verifies that setting Name to an empty string returns an empty string
        /// (not null). This distinguishes between empty and null string values.
        /// </summary>
        [Fact]
        public void Name_SetToEmptyString_ReturnsEmptyString()
        {
            // Arrange
            var role = new ErpRole();

            // Act
            role.Name = string.Empty;

            // Assert
            role.Name.Should().Be(string.Empty);
        }

        /// <summary>
        /// Edge case: verifies that setting Name to null explicitly returns null.
        /// This tests that the property setter accepts null values without throwing.
        /// </summary>
        [Fact]
        public void Name_SetToNull_ReturnsNull()
        {
            // Arrange
            var role = new ErpRole();
            role.Name = "SomeValue"; // Set to non-null first

            // Act
            role.Name = null;

            // Assert
            role.Name.Should().BeNull();
        }

        #endregion

        #region JSON Serialization Tests

        /// <summary>
        /// Verifies that when serialized to JSON, the Id property uses the lowercase key "id"
        /// as specified by the [JsonProperty(PropertyName = "id")] attribute, not the PascalCase "Id".
        /// </summary>
        [Fact]
        public void Serialize_Id_UsesLowercasePropertyName()
        {
            // Arrange
            var role = new ErpRole { Id = Guid.NewGuid() };

            // Act
            var json = JsonConvert.SerializeObject(role);
            var jObject = JObject.Parse(json);

            // Assert
            jObject.ContainsKey("id").Should().Be(true);
        }

        /// <summary>
        /// Verifies that when serialized to JSON, the Name property uses the lowercase key "name"
        /// as specified by the [JsonProperty(PropertyName = "name")] attribute, not the PascalCase "Name".
        /// </summary>
        [Fact]
        public void Serialize_Name_UsesLowercasePropertyName()
        {
            // Arrange
            var role = new ErpRole { Name = "TestRole" };

            // Act
            var json = JsonConvert.SerializeObject(role);
            var jObject = JObject.Parse(json);

            // Assert
            jObject.ContainsKey("name").Should().Be(true);
        }

        /// <summary>
        /// Verifies that when serialized to JSON, the Description property uses the lowercase key
        /// "description" as specified by the [JsonProperty(PropertyName = "description")] attribute.
        /// </summary>
        [Fact]
        public void Serialize_Description_UsesLowercasePropertyName()
        {
            // Arrange
            var role = new ErpRole { Description = "Test description" };

            // Act
            var json = JsonConvert.SerializeObject(role);
            var jObject = JObject.Parse(json);

            // Assert
            jObject.ContainsKey("description").Should().Be(true);
        }

        /// <summary>
        /// Verifies that a fully populated ErpRole serializes to JSON containing all three
        /// expected keys: "id", "name", and "description".
        /// </summary>
        [Fact]
        public void Serialize_AllProperties_Present()
        {
            // Arrange
            var role = new ErpRole
            {
                Id = Guid.NewGuid(),
                Name = "Admin",
                Description = "Administrator role"
            };

            // Act
            var json = JsonConvert.SerializeObject(role);
            var jObject = JObject.Parse(json);

            // Assert
            jObject.ContainsKey("id").Should().Be(true);
            jObject.ContainsKey("name").Should().Be(true);
            jObject.ContainsKey("description").Should().Be(true);
        }

        /// <summary>
        /// Verifies that when all properties are set, the serialized JSON contains the correct
        /// values for each property: the GUID string for id, the role name, and the description.
        /// </summary>
        [Fact]
        public void Serialize_WithValues_ProducesCorrectJson()
        {
            // Arrange
            var expectedId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            var role = new ErpRole
            {
                Id = expectedId,
                Name = "Editor",
                Description = "Can edit content"
            };

            // Act
            var json = JsonConvert.SerializeObject(role);
            var jObject = JObject.Parse(json);

            // Assert
            jObject["id"]!.ToString().Should().Be(expectedId.ToString());
            jObject["name"]!.ToString().Should().Be("Editor");
            jObject["description"]!.ToString().Should().Be("Can edit content");
        }

        /// <summary>
        /// Verifies that deserializing a JSON string with all three properties correctly sets
        /// the Id, Name, and Description properties on the resulting ErpRole instance.
        /// </summary>
        [Fact]
        public void Deserialize_SetsAllProperties()
        {
            // Arrange
            var json = "{\"id\":\"d3b07384-d113-4ec4-8a3e-0166aec184ee\",\"name\":\"Admin\",\"description\":\"desc\"}";

            // Act
            var role = JsonConvert.DeserializeObject<ErpRole>(json);

            // Assert
            role.Should().NotBeNull();
            role!.Id.Should().Be(Guid.Parse("d3b07384-d113-4ec4-8a3e-0166aec184ee"));
            role.Name.Should().Be("Admin");
            role.Description.Should().Be("desc");
        }

        /// <summary>
        /// Verifies that a GUID string in JSON is correctly parsed into a System.Guid when
        /// deserializing the "id" property of an ErpRole.
        /// </summary>
        [Fact]
        public void Deserialize_WithGuidString_ParsesCorrectly()
        {
            // Arrange
            var expectedGuid = Guid.Parse("12345678-1234-1234-1234-123456789012");
            var json = $"{{\"id\":\"{expectedGuid}\",\"name\":\"TestRole\",\"description\":\"TestDesc\"}}";

            // Act
            var role = JsonConvert.DeserializeObject<ErpRole>(json);

            // Assert
            role.Should().NotBeNull();
            role!.Id.Should().Be(expectedGuid);
        }

        /// <summary>
        /// Verifies that serializing an ErpRole and then deserializing the result produces
        /// an instance with identical property values, confirming roundtrip fidelity of the
        /// Newtonsoft.Json serialization contract.
        /// </summary>
        [Fact]
        public void Serialize_Roundtrip_PreservesAllProperties()
        {
            // Arrange
            var original = new ErpRole
            {
                Id = Guid.NewGuid(),
                Name = "Reviewer",
                Description = "Reviews submitted content"
            };

            // Act
            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<ErpRole>(json);

            // Assert
            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(original.Id);
            deserialized.Name.Should().Be(original.Name);
            deserialized.Description.Should().Be(original.Description);
        }

        /// <summary>
        /// Verifies that when Name and Description are null, the serialized JSON still includes
        /// those keys with null values (Newtonsoft.Json default behavior). This ensures the
        /// API contract always returns all properties regardless of their value.
        /// </summary>
        [Fact]
        public void Serialize_NullProperties_IncludedAsNull()
        {
            // Arrange
            var role = new ErpRole
            {
                Id = Guid.Empty,
                Name = null,
                Description = null
            };

            // Act
            var json = JsonConvert.SerializeObject(role);
            var jObject = JObject.Parse(json);

            // Assert — null properties should still be present as keys with null values
            jObject.ContainsKey("name").Should().Be(true);
            jObject["name"]!.Type.Should().Be(JTokenType.Null);
            jObject.ContainsKey("description").Should().Be(true);
            jObject["description"]!.Type.Should().Be(JTokenType.Null);
        }

        #endregion
    }
}
