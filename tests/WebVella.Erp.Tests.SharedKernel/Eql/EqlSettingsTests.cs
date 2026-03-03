using Xunit;
using FluentAssertions;
using Newtonsoft.Json;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Unit tests for <see cref="EqlSettings"/> — validates init-only property defaults,
    /// init setters, JSON serialization/deserialization via [JsonProperty] snake_case naming,
    /// and roundtrip consistency. Critical for API contract stability (AAP 0.8.2).
    /// </summary>
    public class EqlSettingsTests
    {
        // ──────────────────────────────────────────────────────────────
        // Phase 1: Default Value Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that IncludeTotal defaults to true when a new EqlSettings is created.
        /// Source: EqlSettings.cs line 8 — <c>public bool IncludeTotal { get; init; } = true;</c>
        /// </summary>
        [Fact]
        public void Test_IncludeTotal_DefaultTrue()
        {
            // Arrange & Act
            var settings = new EqlSettings();

            // Assert
            settings.IncludeTotal.Should().BeTrue(
                "IncludeTotal defaults to true per EqlSettings definition");
        }

        /// <summary>
        /// Verifies that Distinct defaults to false when a new EqlSettings is created.
        /// Source: EqlSettings.cs line 11 — <c>public bool Distinct { get; init; } = false;</c>
        /// </summary>
        [Fact]
        public void Test_Distinct_DefaultFalse()
        {
            // Arrange & Act
            var settings = new EqlSettings();

            // Assert
            settings.Distinct.Should().BeFalse(
                "Distinct defaults to false per EqlSettings definition");
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 2: Init-Only Property Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that IncludeTotal can be set to false using C# 9+ init-only setter
        /// via object initializer syntax.
        /// </summary>
        [Fact]
        public void Test_IncludeTotal_InitSetter_Works()
        {
            // Arrange & Act
            var settings = new EqlSettings { IncludeTotal = false };

            // Assert
            settings.IncludeTotal.Should().BeFalse(
                "IncludeTotal should accept false via init setter");
        }

        /// <summary>
        /// Verifies that Distinct can be set to true using C# 9+ init-only setter
        /// via object initializer syntax.
        /// </summary>
        [Fact]
        public void Test_Distinct_InitSetter_Works()
        {
            // Arrange & Act
            var settings = new EqlSettings { Distinct = true };

            // Assert
            settings.Distinct.Should().BeTrue(
                "Distinct should accept true via init setter");
        }

        /// <summary>
        /// Verifies that both IncludeTotal and Distinct can be set simultaneously
        /// via object initializer syntax.
        /// </summary>
        [Fact]
        public void Test_BothProperties_CanBeSetViaInit()
        {
            // Arrange & Act
            var settings = new EqlSettings { IncludeTotal = false, Distinct = true };

            // Assert
            settings.IncludeTotal.Should().BeFalse(
                "IncludeTotal should be false when set via init");
            settings.Distinct.Should().BeTrue(
                "Distinct should be true when set via init");
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 3: JSON Deserialization Tests (via [JsonProperty])
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that JSON deserialization with the snake_case property name "include_total"
        /// correctly sets the IncludeTotal property.
        /// [JsonProperty(PropertyName = "include_total")] on IncludeTotal.
        /// </summary>
        [Fact]
        public void Test_JsonDeserialization_IncludeTotal()
        {
            // Arrange
            var json = "{\"include_total\":false}";

            // Act
            var settings = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert
            settings.Should().NotBeNull();
            settings!.IncludeTotal.Should().BeFalse(
                "JSON 'include_total':false should set IncludeTotal to false");
        }

        /// <summary>
        /// Verifies that JSON deserialization with the snake_case property name "distinct"
        /// correctly sets the Distinct property.
        /// [JsonProperty(PropertyName = "distinct")] on Distinct.
        /// </summary>
        [Fact]
        public void Test_JsonDeserialization_Distinct()
        {
            // Arrange
            var json = "{\"distinct\":true}";

            // Act
            var settings = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert
            settings.Should().NotBeNull();
            settings!.Distinct.Should().BeTrue(
                "JSON 'distinct':true should set Distinct to true");
        }

        /// <summary>
        /// Verifies that JSON deserialization correctly sets both properties
        /// when both snake_case keys are present in the JSON payload.
        /// </summary>
        [Fact]
        public void Test_JsonDeserialization_BothProperties()
        {
            // Arrange
            var json = "{\"include_total\":false,\"distinct\":true}";

            // Act
            var settings = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert
            settings.Should().NotBeNull();
            settings!.IncludeTotal.Should().BeFalse(
                "JSON 'include_total':false should override default true");
            settings.Distinct.Should().BeTrue(
                "JSON 'distinct':true should override default false");
        }

        /// <summary>
        /// Verifies that deserializing an empty JSON object uses the property defaults
        /// (IncludeTotal=true, Distinct=false) since no values are provided.
        /// </summary>
        [Fact]
        public void Test_JsonDeserialization_EmptyObject_UsesDefaults()
        {
            // Arrange
            var json = "{}";

            // Act
            var settings = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert
            settings.Should().NotBeNull();
            settings!.IncludeTotal.Should().BeTrue(
                "Empty JSON object should preserve IncludeTotal default of true");
            settings.Distinct.Should().BeFalse(
                "Empty JSON object should preserve Distinct default of false");
        }

        /// <summary>
        /// Verifies behavior when null or missing JSON properties are encountered.
        /// Newtonsoft.Json throws ArgumentNullException for a null string input,
        /// so this test validates that the caller can safely fall back to a default
        /// EqlSettings instance when null JSON is received. Also verifies that JSON
        /// with only partial properties still uses defaults for the missing ones.
        /// </summary>
        [Fact]
        public void Test_JsonDeserialization_NullJson_UsesDefaults()
        {
            // Arrange & Act — Newtonsoft.Json throws ArgumentNullException for null string input;
            // verify this exception is thrown, which callers should handle by using defaults.
            Action deserializeNull = () => JsonConvert.DeserializeObject<EqlSettings>(null!);
            deserializeNull.Should().Throw<System.ArgumentNullException>(
                "Newtonsoft.Json throws ArgumentNullException for null string input");

            // Verify the fallback pattern: a fresh EqlSettings instance preserves defaults
            var fallback = new EqlSettings();
            fallback.IncludeTotal.Should().BeTrue(
                "Fallback instance should have IncludeTotal = true");
            fallback.Distinct.Should().BeFalse(
                "Fallback instance should have Distinct = false");

            // Also verify that JSON with only one property uses defaults for missing ones
            var partialJson = "{\"include_total\":false}";
            var partial = JsonConvert.DeserializeObject<EqlSettings>(partialJson);
            partial.Should().NotBeNull();
            partial!.IncludeTotal.Should().BeFalse(
                "Provided property should be set");
            partial.Distinct.Should().BeFalse(
                "Missing 'distinct' property should use default false");
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 4: JSON Serialization Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that serializing EqlSettings produces JSON with snake_case property names
        /// ("include_total" and "distinct") — NOT PascalCase ("IncludeTotal" and "Distinct").
        /// This ensures [JsonProperty] annotations are respected, which is critical
        /// for API contract stability (AAP 0.8.2).
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_PropertyNames()
        {
            // Arrange
            var settings = new EqlSettings();

            // Act
            var json = JsonConvert.SerializeObject(settings);

            // Assert — snake_case names must be present
            json.Should().Contain("\"include_total\"",
                "Serialized JSON must use snake_case 'include_total' per [JsonProperty]");
            json.Should().Contain("\"distinct\"",
                "Serialized JSON must use snake_case 'distinct' per [JsonProperty]");

            // Assert — PascalCase names must NOT be present
            json.Should().NotContain("\"IncludeTotal\"",
                "Serialized JSON must NOT use PascalCase 'IncludeTotal'");
            json.Should().NotContain("\"Distinct\"",
                "Serialized JSON must NOT use PascalCase 'Distinct'");
        }

        /// <summary>
        /// Verifies that serializing a default EqlSettings instance produces the expected
        /// JSON output with default values (IncludeTotal=true, Distinct=false).
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_DefaultValues()
        {
            // Arrange
            var settings = new EqlSettings();

            // Act
            var json = JsonConvert.SerializeObject(settings);

            // Assert — verify exact JSON structure with default values
            var expected = "{\"include_total\":true,\"distinct\":false}";
            json.Should().Be(expected,
                "Default EqlSettings should serialize to include_total:true and distinct:false");
        }

        /// <summary>
        /// Verifies that serializing EqlSettings with custom values produces correct JSON
        /// with the overridden values.
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_CustomValues()
        {
            // Arrange
            var settings = new EqlSettings { IncludeTotal = false, Distinct = true };

            // Act
            var json = JsonConvert.SerializeObject(settings);

            // Assert — verify exact JSON structure with custom values
            var expected = "{\"include_total\":false,\"distinct\":true}";
            json.Should().Be(expected,
                "Custom EqlSettings should serialize with overridden values");
        }

        // ──────────────────────────────────────────────────────────────
        // Phase 5: Roundtrip Tests
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifies that serialize-then-deserialize (roundtrip) preserves all property values.
        /// This is the core contract stability test ensuring EqlSettings can survive
        /// JSON transmission across service boundaries without data loss.
        /// </summary>
        [Fact]
        public void Test_JsonRoundtrip_PreservesValues()
        {
            // Arrange — create settings with non-default values
            var original = new EqlSettings { IncludeTotal = false, Distinct = true };

            // Act — serialize then deserialize
            var json = JsonConvert.SerializeObject(original);
            var restored = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert
            restored.Should().NotBeNull();
            restored!.IncludeTotal.Should().Be(original.IncludeTotal,
                "Roundtrip should preserve IncludeTotal value");
            restored.Distinct.Should().Be(original.Distinct,
                "Roundtrip should preserve Distinct value");
        }

        /// <summary>
        /// Verifies roundtrip with default values — defaults survive serialization
        /// and deserialization intact.
        /// </summary>
        [Fact]
        public void Test_JsonRoundtrip_DefaultValues()
        {
            // Arrange — use default settings
            var original = new EqlSettings();

            // Act — serialize then deserialize
            var json = JsonConvert.SerializeObject(original);
            var restored = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert
            restored.Should().NotBeNull();
            restored!.IncludeTotal.Should().BeTrue(
                "Roundtrip should preserve IncludeTotal default of true");
            restored.Distinct.Should().BeFalse(
                "Roundtrip should preserve Distinct default of false");
        }

        /// <summary>
        /// Verifies roundtrip with explicitly set custom values, confirming that
        /// the [JsonProperty] annotations and init-only setters work correctly
        /// through the full serialize/deserialize cycle.
        /// </summary>
        [Fact]
        public void Test_JsonRoundtrip_CustomValues()
        {
            // Arrange — custom values opposite to defaults
            var original = new EqlSettings { IncludeTotal = false, Distinct = true };

            // Act — full roundtrip
            var json = JsonConvert.SerializeObject(original);
            var restored = JsonConvert.DeserializeObject<EqlSettings>(json);

            // Assert — verify each property individually
            restored.Should().NotBeNull();
            restored!.IncludeTotal.Should().BeFalse(
                "Custom IncludeTotal=false should survive roundtrip");
            restored.Distinct.Should().BeTrue(
                "Custom Distinct=true should survive roundtrip");

            // Assert — also verify the intermediate JSON has correct structure
            json.Should().Contain("\"include_total\":false",
                "Intermediate JSON should contain include_total:false");
            json.Should().Contain("\"distinct\":true",
                "Intermediate JSON should contain distinct:true");
        }
    }
}
