using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using FluentAssertions;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Tests.SharedKernel.Utilities
{
    #region Test Fixtures — Polymorphic type hierarchy and concrete converter

    /// <summary>
    /// Abstract base class representing the polymorphic root type for
    /// <see cref="JsonCreationConverter{T}"/> testing. All deserialization
    /// targets are subclasses of TestAnimal.
    /// </summary>
    public abstract class TestAnimal
    {
        /// <summary>Name of the animal (common to all subtypes).</summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// Dog subtype used for polymorphic dispatch testing.
    /// Discriminated by the presence of a "breed" (or "Breed") JSON property.
    /// Includes a decimal <see cref="Weight"/> property for FloatParseHandling propagation tests.
    /// </summary>
    public class TestDog : TestAnimal
    {
        /// <summary>Breed of the dog (e.g., "Labrador", "Poodle").</summary>
        public string Breed { get; set; }

        /// <summary>
        /// Weight of the dog in kilograms. Typed as <see cref="decimal"/> to
        /// verify <see cref="FloatParseHandling.Decimal"/> propagation through
        /// the JSON replay pattern in ReadJson.
        /// </summary>
        public decimal Weight { get; set; }
    }

    /// <summary>
    /// Cat subtype used for polymorphic dispatch testing.
    /// Discriminated by the presence of an "isIndoor" (or "IsIndoor") JSON property.
    /// </summary>
    public class TestCat : TestAnimal
    {
        /// <summary>Whether the cat lives indoors.</summary>
        public bool IsIndoor { get; set; }
    }

    /// <summary>
    /// Concrete implementation of <see cref="JsonCreationConverter{T}"/> targeting
    /// <see cref="TestAnimal"/>. Inspects the <see cref="JObject"/> for discriminating
    /// properties to decide whether to instantiate <see cref="TestDog"/> or
    /// <see cref="TestCat"/>. Falls back to <see cref="TestDog"/> when no discriminator
    /// is present (empty object).
    /// </summary>
    public class TestAnimalConverter : JsonCreationConverter<TestAnimal>
    {
        /// <inheritdoc />
        protected override TestAnimal Create(Type objectType, JObject jObject)
        {
            // Check both camelCase (from hand-written JSON) and PascalCase (from
            // Newtonsoft default serialization) to support all test scenarios.
            if (jObject["breed"] != null || jObject["Breed"] != null)
                return new TestDog();
            if (jObject["isIndoor"] != null || jObject["IsIndoor"] != null)
                return new TestCat();

            // Default: return a TestDog when no discriminating property is found
            return new TestDog();
        }
    }

    #endregion

    /// <summary>
    /// Comprehensive unit tests for <see cref="JsonCreationConverter{T}"/>.
    /// Validates all behavior paths of the abstract converter including:
    /// <list type="bullet">
    ///   <item><description>CanConvert — base, derived, and unrelated type checks</description></item>
    ///   <item><description>ReadJson — polymorphic deserialization, null handling, empty objects, property population</description></item>
    ///   <item><description>ReadJson — reader settings propagation (Culture, DateParseHandling, FloatParseHandling)</description></item>
    ///   <item><description>WriteJson — delegation to serializer</description></item>
    ///   <item><description>Round-trip — serialize/deserialize fidelity for Dog, Cat, and mixed collections</description></item>
    /// </list>
    /// </summary>
    public class JsonCreationConverterTests
    {
        /// <summary>Converter instance reused across tests (xUnit creates a new class per test).</summary>
        private readonly TestAnimalConverter _converter = new TestAnimalConverter();

        /// <summary>
        /// Creates a fresh <see cref="JsonSerializerSettings"/> with the
        /// <see cref="TestAnimalConverter"/> registered in its Converters collection.
        /// </summary>
        private static JsonSerializerSettings CreateSettingsWithConverter()
        {
            return new JsonSerializerSettings
            {
                Converters = { new TestAnimalConverter() }
            };
        }

        // =====================================================================
        //  CanConvert Tests
        // =====================================================================

        /// <summary>
        /// Verifies that CanConvert returns true for the exact base type T
        /// (typeof(TestAnimal).IsAssignableFrom(typeof(TestAnimal)) is true).
        /// </summary>
        [Fact]
        public void CanConvert_BaseType_ReturnsTrue()
        {
            // Act
            var result = _converter.CanConvert(typeof(TestAnimal));

            // Assert
            result.Should().BeTrue(
                "because typeof(TestAnimal).IsAssignableFrom(typeof(TestAnimal)) is true");
        }

        /// <summary>
        /// Verifies that CanConvert returns true for derived types
        /// (typeof(TestAnimal).IsAssignableFrom(typeof(TestDog)) is true).
        /// </summary>
        [Fact]
        public void CanConvert_DerivedType_ReturnsTrue()
        {
            // Act
            var result = _converter.CanConvert(typeof(TestDog));

            // Assert
            result.Should().BeTrue(
                "because TestDog derives from TestAnimal so IsAssignableFrom is true");
        }

        /// <summary>
        /// Verifies that CanConvert returns false for types unrelated to T
        /// (typeof(TestAnimal).IsAssignableFrom(typeof(string)) is false).
        /// </summary>
        [Fact]
        public void CanConvert_UnrelatedType_ReturnsFalse()
        {
            // Act
            var result = _converter.CanConvert(typeof(string));

            // Assert
            result.Should().BeFalse(
                "because string is not assignable to TestAnimal");
        }

        // =====================================================================
        //  ReadJson — Polymorphic Deserialization Tests
        // =====================================================================

        /// <summary>
        /// Deserializes JSON containing a "breed" property and verifies the
        /// converter dispatches to <see cref="TestDog"/> with all properties populated.
        /// </summary>
        [Fact]
        public void ReadJson_WithBreedProperty_DeserializesToDog()
        {
            // Arrange
            var json = @"{""name"":""Rex"",""breed"":""Labrador""}";
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestDog>();
            var dog = (TestDog)result;
            dog.Name.Should().Be("Rex");
            dog.Breed.Should().Be("Labrador");
        }

        /// <summary>
        /// Deserializes JSON containing an "isIndoor" property and verifies the
        /// converter dispatches to <see cref="TestCat"/> with all properties populated.
        /// </summary>
        [Fact]
        public void ReadJson_WithIsIndoorProperty_DeserializesToCat()
        {
            // Arrange
            var json = @"{""name"":""Whiskers"",""isIndoor"":true}";
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestCat>();
            var cat = (TestCat)result;
            cat.Name.Should().Be("Whiskers");
            cat.IsIndoor.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that ReadJson returns null when the JSON token is
        /// <see cref="JsonToken.Null"/>, matching the early-exit at line 19 of
        /// the source implementation.
        /// </summary>
        [Fact]
        public void ReadJson_NullToken_ReturnsNull()
        {
            // Arrange
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<TestAnimal>("null", settings);

            // Assert
            result.Should().BeNull();
        }

        /// <summary>
        /// Deserializes an empty JSON object "{}" and verifies the converter
        /// falls through to the default branch of <see cref="TestAnimalConverter.Create"/>,
        /// producing a <see cref="TestDog"/> with all properties at default values.
        /// </summary>
        [Fact]
        public void ReadJson_EmptyObject_CreatesDefault()
        {
            // Arrange
            var json = "{}";
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestDog>(
                "because the converter defaults to TestDog when no discriminator is present");
            var dog = (TestDog)result;
            dog.Name.Should().BeNull();
            dog.Breed.Should().BeNull();
            dog.Weight.Should().Be(0m);
        }

        /// <summary>
        /// Verifies that ALL JSON properties (including the decimal <see cref="TestDog.Weight"/>)
        /// are fully populated on the target object after ReadJson's replay pattern.
        /// </summary>
        [Fact]
        public void ReadJson_PreservesAllProperties()
        {
            // Arrange
            var json = @"{""name"":""Buddy"",""breed"":""Golden Retriever"",""weight"":30.5}";
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestDog>();
            var dog = (TestDog)result;
            dog.Name.Should().Be("Buddy");
            dog.Breed.Should().Be("Golden Retriever");
            dog.Weight.Should().Be(30.5m);
        }

        // =====================================================================
        //  ReadJson — Reader Settings Propagation Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the <see cref="JsonReader.Culture"/> setting is propagated
        /// from the original reader to the inner replay reader (source line 33:
        /// <c>newReader.Culture = reader.Culture</c>). Sets
        /// <see cref="CultureInfo.InvariantCulture"/> on the reader and confirms
        /// deserialization succeeds with properties correctly populated.
        /// </summary>
        [Fact]
        public void ReadJson_PropagatesCulture()
        {
            // Arrange
            var json = @"{""name"":""Rex"",""breed"":""Labrador""}";
            using var stringReader = new StringReader(json);
            using var reader = new JsonTextReader(stringReader);
            reader.Culture = CultureInfo.InvariantCulture;
            reader.Read(); // Advance to the first token (StartObject)

            var serializer = JsonSerializer.Create(new JsonSerializerSettings());

            // Act
            var result = _converter.ReadJson(reader, typeof(TestAnimal), null, serializer);

            // Assert — deserialization succeeds with propagated culture
            result.Should().NotBeNull();
            result.Should().BeOfType<TestDog>();
            var dog = (TestDog)result;
            dog.Name.Should().Be("Rex");
            dog.Breed.Should().Be("Labrador");
        }

        /// <summary>
        /// Verifies that the <see cref="JsonReader.DateParseHandling"/> setting is
        /// propagated from the original reader to the inner replay reader (source line 34:
        /// <c>newReader.DateParseHandling = reader.DateParseHandling</c>). Uses
        /// <see cref="DateParseHandling.None"/> so that ISO-8601 date strings are
        /// preserved as raw strings rather than being parsed into DateTime objects.
        /// </summary>
        [Fact]
        public void ReadJson_PropagatesDateParseHandling()
        {
            // Arrange — use a date-like string in the Name property
            var dateString = "2024-01-15T10:30:00Z";
            var json = $@"{{""name"":""{dateString}"",""breed"":""Labrador""}}";

            using var stringReader = new StringReader(json);
            using var reader = new JsonTextReader(stringReader);
            reader.DateParseHandling = DateParseHandling.None;
            reader.Read(); // Advance to StartObject

            // Serializer also configured with DateParseHandling.None to ensure
            // consistent behavior during the replay serialization step
            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                DateParseHandling = DateParseHandling.None
            });

            // Act
            var result = _converter.ReadJson(reader, typeof(TestAnimal), null, serializer);

            // Assert — date string is preserved verbatim (not reformatted by DateTime parsing)
            result.Should().NotBeNull();
            var dog = result as TestDog;
            dog.Should().NotBeNull();
            dog.Name.Should().Be(dateString,
                "because DateParseHandling.None preserves the raw date string");
        }

        /// <summary>
        /// Verifies that the <see cref="JsonReader.FloatParseHandling"/> setting is
        /// propagated from the original reader to the inner replay reader (source line 36:
        /// <c>newReader.FloatParseHandling = reader.FloatParseHandling</c>). Uses
        /// <see cref="FloatParseHandling.Decimal"/> to ensure decimal precision is
        /// preserved during the JSON replay pattern.
        /// </summary>
        [Fact]
        public void ReadJson_PropagatesFloatParseHandling()
        {
            // Arrange
            var json = @"{""breed"":""Labrador"",""weight"":12.345}";

            using var stringReader = new StringReader(json);
            using var reader = new JsonTextReader(stringReader);
            reader.FloatParseHandling = FloatParseHandling.Decimal;
            reader.Read(); // Advance to StartObject

            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                FloatParseHandling = FloatParseHandling.Decimal
            });

            // Act
            var result = _converter.ReadJson(reader, typeof(TestAnimal), null, serializer);

            // Assert — decimal precision is preserved through the replay pattern
            result.Should().NotBeNull();
            var dog = result as TestDog;
            dog.Should().NotBeNull();
            dog.Weight.Should().Be(12.345m,
                "because FloatParseHandling.Decimal preserves exact decimal precision");
        }

        // =====================================================================
        //  WriteJson Tests
        // =====================================================================

        /// <summary>
        /// Verifies that WriteJson delegates to <c>serializer.Serialize(writer, value)</c>
        /// (source line 45) by calling WriteJson directly with a serializer that does
        /// NOT have the converter registered (to avoid infinite recursion) and
        /// confirming the output JSON contains all expected properties.
        /// </summary>
        [Fact]
        public void WriteJson_DelegatesToSerializer()
        {
            // Arrange
            var dog = new TestDog { Name = "Rex", Breed = "Labrador", Weight = 25.5m };
            // Create a serializer WITHOUT the converter to avoid infinite recursion
            // (WriteJson calls serializer.Serialize which would re-enter the converter)
            var serializer = JsonSerializer.Create(new JsonSerializerSettings());

            var stringWriter = new StringWriter();
            var jsonWriter = new JsonTextWriter(stringWriter);

            // Act
            _converter.WriteJson(jsonWriter, dog, serializer);
            jsonWriter.Flush();

            // Assert — verify the JSON output contains all properties from TestDog
            var json = stringWriter.ToString();
            var jObj = JObject.Parse(json);
            jObj["Name"].Value<string>().Should().Be("Rex");
            jObj["Breed"].Value<string>().Should().Be("Labrador");
            jObj["Weight"].Value<decimal>().Should().Be(25.5m);
        }

        /// <summary>
        /// Verifies a complete write-then-read round trip: serializes a TestDog using
        /// default serialization (no converter, to avoid WriteJson recursion), then
        /// deserializes with the converter and confirms all properties are preserved.
        /// </summary>
        [Fact]
        public void WriteJson_RoundTrip_SerializeDeserialize()
        {
            // Arrange
            var dog = new TestDog { Name = "Rex", Breed = "Labrador", Weight = 25.5m };

            // Serialize without converter (standard serialization produces PascalCase JSON)
            var json = JsonConvert.SerializeObject(dog);

            // Deserialize with converter (PascalCase "Breed" matched by converter)
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestDog>();
            var resultDog = (TestDog)result;
            resultDog.Name.Should().Be("Rex");
            resultDog.Breed.Should().Be("Labrador");
            resultDog.Weight.Should().Be(25.5m);
        }

        // =====================================================================
        //  Round-Trip Integration Tests
        // =====================================================================

        /// <summary>
        /// Full create → serialize → deserialize → compare cycle for TestDog,
        /// confirming polymorphic type and all property values survive the round trip.
        /// </summary>
        [Fact]
        public void RoundTrip_Dog_FullCycle()
        {
            // Arrange
            var original = new TestDog
            {
                Name = "Max",
                Breed = "German Shepherd",
                Weight = 35.2m
            };

            // Act — serialize (standard) then deserialize (with converter)
            var json = JsonConvert.SerializeObject(original);
            var settings = CreateSettingsWithConverter();
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestDog>();
            var restored = (TestDog)result;
            restored.Name.Should().Be(original.Name);
            restored.Breed.Should().Be(original.Breed);
            restored.Weight.Should().Be(original.Weight);
        }

        /// <summary>
        /// Full create → serialize → deserialize → compare cycle for TestCat,
        /// confirming polymorphic type and all property values survive the round trip.
        /// </summary>
        [Fact]
        public void RoundTrip_Cat_FullCycle()
        {
            // Arrange
            var original = new TestCat
            {
                Name = "Luna",
                IsIndoor = true
            };

            // Act — serialize (standard) then deserialize (with converter)
            var json = JsonConvert.SerializeObject(original);
            var settings = CreateSettingsWithConverter();
            var result = JsonConvert.DeserializeObject<TestAnimal>(json, settings);

            // Assert
            result.Should().NotBeNull();
            result.Should().BeOfType<TestCat>();
            var restored = (TestCat)result;
            restored.Name.Should().Be(original.Name);
            restored.IsIndoor.Should().Be(original.IsIndoor);
        }

        /// <summary>
        /// Deserializes a JSON array containing a mixed collection of Dog and Cat
        /// objects and verifies each element is instantiated as the correct concrete
        /// type with all properties populated.
        /// </summary>
        [Fact]
        public void Deserialize_CollectionOfPolymorphicTypes()
        {
            // Arrange — JSON array with two dogs and one cat, using camelCase properties
            var json = @"[
                {""name"":""Rex"",""breed"":""Labrador""},
                {""name"":""Whiskers"",""isIndoor"":true},
                {""name"":""Buddy"",""breed"":""Poodle""}
            ]";
            var settings = CreateSettingsWithConverter();

            // Act
            var result = JsonConvert.DeserializeObject<List<TestAnimal>>(json, settings);

            // Assert — correct count
            result.Should().NotBeNull();
            result.Should().HaveCount(3);

            // First element: Dog
            result[0].Should().BeOfType<TestDog>();
            result[0].Name.Should().Be("Rex");
            ((TestDog)result[0]).Breed.Should().Be("Labrador");

            // Second element: Cat
            result[1].Should().BeOfType<TestCat>();
            result[1].Name.Should().Be("Whiskers");
            ((TestCat)result[1]).IsIndoor.Should().BeTrue();

            // Third element: Dog
            result[2].Should().BeOfType<TestDog>();
            result[2].Name.Should().Be("Buddy");
            ((TestDog)result[2]).Breed.Should().Be("Poodle");
        }
    }
}
