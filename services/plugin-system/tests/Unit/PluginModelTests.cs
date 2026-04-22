using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Newtonsoft.Json;
using WebVellaErp.PluginSystem.Models;
using Xunit;

namespace WebVellaErp.PluginSystem.Tests.Unit
{
    /// <summary>
    /// Comprehensive unit tests for the Plugin domain model and related DTOs.
    /// Verifies JSON serialization/deserialization with dual-attribute pattern
    /// (System.Text.Json + Newtonsoft.Json), default constructor initializations,
    /// PluginStatus enum behavior, and DTO structure validation.
    /// 
    /// All 13 metadata properties from the source ErpPlugin.cs (lines 14-51) are
    /// verified to serialize with their exact JSON property names for backward
    /// compatibility with the existing plugin manifest format.
    /// 
    /// Testing Framework: xUnit [Fact]/[Theory] + FluentAssertions
    /// Coverage Target: >80% per AAP Section 0.8.4
    /// Dependencies: ZERO AWS SDK calls (pure model tests)
    /// </summary>
    public class PluginModelTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates a fully populated Plugin instance with ALL properties set to non-default values.
        /// Uses values derived from the original SdkPlugin in the source monolith:
        /// - Id: SDK app GUID from source (56a8548a-19d0-497f-8e5b-242abfdc4082)
        /// - Name/Description: SDK plugin metadata
        /// - Version: 20210429 from SdkPlugin._.cs last patch version
        /// - Timestamps: Fixed UTC dates for deterministic testing
        /// </summary>
        /// <returns>A Plugin instance with all 17 properties set to non-default, verifiable values.</returns>
        public static Plugin CreateFullyPopulatedPlugin()
        {
            return new Plugin
            {
                Id = Guid.Parse("56a8548a-19d0-497f-8e5b-242abfdc4082"),
                Name = "sdk",
                Prefix = "sdk-prefix",
                Url = "https://sdk.webvella.com",
                Description = "Software Development Kit admin console",
                Version = 20210429,
                Company = "WebVella",
                CompanyUrl = "https://webvella.com",
                Author = "WebVella Team",
                Repository = "https://github.com/WebVella/WebVella-ERP",
                License = "Apache-2.0",
                SettingsUrl = "/sdk/settings",
                PluginPageUrl = "/sdk/plugin-page",
                IconUrl = "/sdk/icon.png",
                Status = PluginStatus.Active,
                CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc)
            };
        }

        #endregion

        #region Phase 2: System.Text.Json Serialization Tests (Primary — AOT-compatible)

        /// <summary>
        /// Verifies that ALL 17 JSON property names are present when serializing a fully populated
        /// Plugin using System.Text.Json. The 13 original property names from ErpPlugin.cs lines 14-51
        /// plus 4 new properties (id, status, created_at, updated_at) must all be serialized.
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_SerializesAllProperties()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Assert — all 17 JSON property names must exist
            var expectedPropertyNames = new[]
            {
                "id", "name", "prefix", "url", "description", "version",
                "company", "company_url", "author", "repository", "license",
                "settings_url", "plugin_page_url", "icon_url",
                "status", "created_at", "updated_at"
            };

            foreach (var propName in expectedPropertyNames)
            {
                root.TryGetProperty(propName, out _).Should().BeTrue(
                    $"JSON output should contain property '{propName}'");
            }
        }

        /// <summary>
        /// Verifies that Plugin.Name serializes as "name" in JSON.
        /// Source: ErpPlugin.cs line 14 — [JsonProperty(PropertyName = "name")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Name_SerializedAs_name()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("name").GetString().Should().Be("sdk");
        }

        /// <summary>
        /// Verifies that Plugin.Prefix serializes as "prefix" in JSON.
        /// Source: ErpPlugin.cs line 17 — [JsonProperty(PropertyName = "prefix")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Prefix_SerializedAs_prefix()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("prefix").GetString().Should().Be("sdk-prefix");
        }

        /// <summary>
        /// Verifies that Plugin.Url serializes as "url" in JSON.
        /// Source: ErpPlugin.cs line 20 — [JsonProperty(PropertyName = "url")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Url_SerializedAs_url()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("url").GetString().Should().Be("https://sdk.webvella.com");
        }

        /// <summary>
        /// Verifies that Plugin.Description serializes as "description" in JSON.
        /// Source: ErpPlugin.cs line 23 — [JsonProperty(PropertyName = "description")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Description_SerializedAs_description()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("description").GetString()
                .Should().Be("Software Development Kit admin console");
        }

        /// <summary>
        /// Verifies that Plugin.Version serializes as "version" in JSON.
        /// Source: ErpPlugin.cs line 26 — [JsonProperty(PropertyName = "version")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Version_SerializedAs_version()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("version").GetInt32().Should().Be(20210429);
        }

        /// <summary>
        /// Verifies that Plugin.Company serializes as "company" in JSON.
        /// Source: ErpPlugin.cs line 29 — [JsonProperty(PropertyName = "company")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Company_SerializedAs_company()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("company").GetString().Should().Be("WebVella");
        }

        /// <summary>
        /// Verifies that Plugin.CompanyUrl serializes as "company_url" (snake_case) in JSON.
        /// Source: ErpPlugin.cs line 32 — [JsonProperty(PropertyName = "company_url")]
        /// NOTE: snake_case "company_url" NOT camelCase "companyUrl" — backward compatibility.
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_CompanyUrl_SerializedAs_company_url()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert — must be snake_case for backward compatibility
            doc.RootElement.GetProperty("company_url").GetString()
                .Should().Be("https://webvella.com");
        }

        /// <summary>
        /// Verifies that Plugin.Author serializes as "author" in JSON.
        /// Source: ErpPlugin.cs line 35 — [JsonProperty(PropertyName = "author")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Author_SerializedAs_author()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("author").GetString().Should().Be("WebVella Team");
        }

        /// <summary>
        /// Verifies that Plugin.Repository serializes as "repository" in JSON.
        /// Source: ErpPlugin.cs line 38 — [JsonProperty(PropertyName = "repository")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_Repository_SerializedAs_repository()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("repository").GetString()
                .Should().Be("https://github.com/WebVella/WebVella-ERP");
        }

        /// <summary>
        /// Verifies that Plugin.License serializes as "license" in JSON.
        /// Source: ErpPlugin.cs line 41 — [JsonProperty(PropertyName = "license")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_License_SerializedAs_license()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert
            doc.RootElement.GetProperty("license").GetString().Should().Be("Apache-2.0");
        }

        /// <summary>
        /// Verifies that Plugin.SettingsUrl serializes as "settings_url" (snake_case) in JSON.
        /// Source: ErpPlugin.cs line 44 — [JsonProperty(PropertyName = "settings_url")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_SettingsUrl_SerializedAs_settings_url()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert — must be snake_case for backward compatibility
            doc.RootElement.GetProperty("settings_url").GetString()
                .Should().Be("/sdk/settings");
        }

        /// <summary>
        /// Verifies that Plugin.PluginPageUrl serializes as "plugin_page_url" (snake_case) in JSON.
        /// Source: ErpPlugin.cs line 47 — [JsonProperty(PropertyName = "plugin_page_url")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_PluginPageUrl_SerializedAs_plugin_page_url()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert — must be snake_case for backward compatibility
            doc.RootElement.GetProperty("plugin_page_url").GetString()
                .Should().Be("/sdk/plugin-page");
        }

        /// <summary>
        /// Verifies that Plugin.IconUrl serializes as "icon_url" (snake_case) in JSON.
        /// Source: ErpPlugin.cs line 50 — [JsonProperty(PropertyName = "icon_url")]
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_IconUrl_SerializedAs_icon_url()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert — must be snake_case for backward compatibility
            doc.RootElement.GetProperty("icon_url").GetString()
                .Should().Be("/sdk/icon.png");
        }

        /// <summary>
        /// Verifies complete round-trip fidelity: Serialize → Deserialize → all properties equal.
        /// This is the most critical System.Text.Json test — ensures no data loss during JSON
        /// serialization/deserialization of the Plugin model.
        /// </summary>
        [Fact]
        public void Plugin_SystemTextJson_RoundTrip_PreservesAllProperties()
        {
            // Arrange
            var original = CreateFullyPopulatedPlugin();

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(original);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<Plugin>(json);

            // Assert — all 17 properties must survive the round-trip
            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(original.Id);
            deserialized.Name.Should().Be(original.Name);
            deserialized.Prefix.Should().Be(original.Prefix);
            deserialized.Url.Should().Be(original.Url);
            deserialized.Description.Should().Be(original.Description);
            deserialized.Version.Should().Be(original.Version);
            deserialized.Company.Should().Be(original.Company);
            deserialized.CompanyUrl.Should().Be(original.CompanyUrl);
            deserialized.Author.Should().Be(original.Author);
            deserialized.Repository.Should().Be(original.Repository);
            deserialized.License.Should().Be(original.License);
            deserialized.SettingsUrl.Should().Be(original.SettingsUrl);
            deserialized.PluginPageUrl.Should().Be(original.PluginPageUrl);
            deserialized.IconUrl.Should().Be(original.IconUrl);
            deserialized.Status.Should().Be(original.Status);
            deserialized.CreatedAt.Should().Be(original.CreatedAt);
            deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
        }

        #endregion

        #region Phase 3: Newtonsoft.Json Serialization Tests (Backward Compatibility)

        /// <summary>
        /// Verifies that Newtonsoft.Json [JsonProperty] attributes produce the SAME JSON property names
        /// as System.Text.Json [JsonPropertyName] attributes. This ensures backward compatibility
        /// with the existing plugin manifest format from source ErpPlugin.cs.
        /// All 13 original property names must match EXACTLY.
        /// </summary>
        [Fact]
        public void Plugin_NewtonsoftJson_SerializesWithSamePropertyNames()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act
            var json = JsonConvert.SerializeObject(plugin);
            var jObj = Newtonsoft.Json.Linq.JObject.Parse(json);

            // Assert — all 13 original property names from ErpPlugin.cs must be present
            var expectedOriginalPropertyNames = new[]
            {
                "name", "prefix", "url", "description", "version",
                "company", "company_url", "author", "repository", "license",
                "settings_url", "plugin_page_url", "icon_url"
            };

            foreach (var propName in expectedOriginalPropertyNames)
            {
                jObj.ContainsKey(propName).Should().BeTrue(
                    $"Newtonsoft.Json output should contain property '{propName}' for backward compatibility");
            }

            // Also verify the 4 new property names
            jObj.ContainsKey("id").Should().BeTrue();
            jObj.ContainsKey("status").Should().BeTrue();
            jObj.ContainsKey("created_at").Should().BeTrue();
            jObj.ContainsKey("updated_at").Should().BeTrue();
        }

        /// <summary>
        /// Verifies complete round-trip fidelity using Newtonsoft.Json.
        /// Same as System.Text.Json round-trip but using the backward-compatible serializer.
        /// </summary>
        [Fact]
        public void Plugin_NewtonsoftJson_RoundTrip_PreservesAllProperties()
        {
            // Arrange
            var original = CreateFullyPopulatedPlugin();

            // Act
            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<Plugin>(json);

            // Assert — all 17 properties must survive the round-trip
            deserialized.Should().NotBeNull();
            deserialized!.Id.Should().Be(original.Id);
            deserialized.Name.Should().Be(original.Name);
            deserialized.Prefix.Should().Be(original.Prefix);
            deserialized.Url.Should().Be(original.Url);
            deserialized.Description.Should().Be(original.Description);
            deserialized.Version.Should().Be(original.Version);
            deserialized.Company.Should().Be(original.Company);
            deserialized.CompanyUrl.Should().Be(original.CompanyUrl);
            deserialized.Author.Should().Be(original.Author);
            deserialized.Repository.Should().Be(original.Repository);
            deserialized.License.Should().Be(original.License);
            deserialized.SettingsUrl.Should().Be(original.SettingsUrl);
            deserialized.PluginPageUrl.Should().Be(original.PluginPageUrl);
            deserialized.IconUrl.Should().Be(original.IconUrl);
            deserialized.Status.Should().Be(original.Status);
            deserialized.CreatedAt.Should().Be(original.CreatedAt);
            deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
        }

        /// <summary>
        /// Verifies that both System.Text.Json and Newtonsoft.Json produce the SAME JSON property
        /// names and values for key properties — especially the snake_case properties that
        /// preserve backward compatibility with the existing plugin manifest format.
        /// </summary>
        [Theory]
        [InlineData("name", "sdk")]
        [InlineData("company_url", "https://webvella.com")]
        [InlineData("settings_url", "/sdk/settings")]
        [InlineData("plugin_page_url", "/sdk/plugin-page")]
        [InlineData("icon_url", "/sdk/icon.png")]
        public void Plugin_BothSerializers_ProduceSamePropertyName(string expectedJsonKey, string expectedValue)
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();

            // Act — serialize with both serializers
            var stjJson = System.Text.Json.JsonSerializer.Serialize(plugin);
            var newtJson = JsonConvert.SerializeObject(plugin);

            // Parse System.Text.Json output
            using var stjDoc = JsonDocument.Parse(stjJson);
            var stjValue = stjDoc.RootElement.GetProperty(expectedJsonKey).GetString();

            // Parse Newtonsoft.Json output
            var newtObj = Newtonsoft.Json.Linq.JObject.Parse(newtJson);
            var newtValue = newtObj[expectedJsonKey]?.ToString();

            // Assert — both serializers must produce the same property name and value
            stjValue.Should().Be(expectedValue,
                $"System.Text.Json should serialize '{expectedJsonKey}' as '{expectedValue}'");
            newtValue.Should().Be(expectedValue,
                $"Newtonsoft.Json should serialize '{expectedJsonKey}' as '{expectedValue}'");
            stjValue.Should().Be(newtValue,
                $"Both serializers must produce the same value for '{expectedJsonKey}'");
        }

        #endregion

        #region Phase 4: PluginStatus Enum Tests

        /// <summary>
        /// Verifies that PluginStatus.Active serializes as the string "Active" (not integer 0)
        /// when using System.Text.Json. This validates the [JsonConverter(typeof(JsonStringEnumConverter))]
        /// attribute on the PluginStatus enum.
        /// </summary>
        [Fact]
        public void PluginStatus_Active_SerializesAsString_SystemTextJson()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();
            plugin.Status = PluginStatus.Active;

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert — status should be the string "Active", NOT integer 0
            var statusValue = doc.RootElement.GetProperty("status").GetString();
            statusValue.Should().Be("Active");
        }

        /// <summary>
        /// Verifies that PluginStatus.Inactive serializes as the string "Inactive" (not integer 1)
        /// when using System.Text.Json.
        /// </summary>
        [Fact]
        public void PluginStatus_Inactive_SerializesAsString_SystemTextJson()
        {
            // Arrange
            var plugin = CreateFullyPopulatedPlugin();
            plugin.Status = PluginStatus.Inactive;

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(plugin);
            using var doc = JsonDocument.Parse(json);

            // Assert — status should be the string "Inactive", NOT integer 1
            var statusValue = doc.RootElement.GetProperty("status").GetString();
            statusValue.Should().Be("Inactive");
        }

        /// <summary>
        /// Verifies that PluginStatus deserializes correctly from a JSON string value.
        /// Ensures the JsonStringEnumConverter handles deserialization from string "Active".
        /// </summary>
        [Fact]
        public void PluginStatus_DeserializesFromString_SystemTextJson()
        {
            // Arrange — JSON with status as string "Active"
            var json = @"{""id"":""00000000-0000-0000-0000-000000000000"",""name"":"""",""prefix"":"""",""url"":"""",""description"":"""",""version"":0,""company"":"""",""company_url"":"""",""author"":"""",""repository"":"""",""license"":"""",""settings_url"":"""",""plugin_page_url"":"""",""icon_url"":"""",""status"":""Active"",""created_at"":""0001-01-01T00:00:00"",""updated_at"":""0001-01-01T00:00:00""}";

            // Act
            var plugin = System.Text.Json.JsonSerializer.Deserialize<Plugin>(json);

            // Assert
            plugin.Should().NotBeNull();
            plugin!.Status.Should().Be(PluginStatus.Active);
        }

        /// <summary>
        /// Verifies that PluginStatus enum values map to the correct integer values.
        /// Active = 0, Inactive = 1 — used for database storage and enum ordering.
        /// </summary>
        [Fact]
        public void PluginStatus_EnumValues_CorrectIntegerMapping()
        {
            // Assert
            ((int)PluginStatus.Active).Should().Be(0);
            ((int)PluginStatus.Inactive).Should().Be(1);
        }

        #endregion

        #region Phase 5: Default Constructor Tests

        /// <summary>
        /// Verifies that the Plugin default constructor initializes all 12 string properties
        /// to string.Empty. This ensures no null reference exceptions when accessing properties
        /// on a newly created Plugin instance.
        /// </summary>
        [Fact]
        public void Plugin_DefaultConstructor_InitializesStringPropertiesToEmpty()
        {
            // Act
            var plugin = new Plugin();

            // Assert — all 12 string properties must be string.Empty
            plugin.Name.Should().Be(string.Empty);
            plugin.Prefix.Should().Be(string.Empty);
            plugin.Url.Should().Be(string.Empty);
            plugin.Description.Should().Be(string.Empty);
            plugin.Company.Should().Be(string.Empty);
            plugin.CompanyUrl.Should().Be(string.Empty);
            plugin.Author.Should().Be(string.Empty);
            plugin.Repository.Should().Be(string.Empty);
            plugin.License.Should().Be(string.Empty);
            plugin.SettingsUrl.Should().Be(string.Empty);
            plugin.PluginPageUrl.Should().Be(string.Empty);
            plugin.IconUrl.Should().Be(string.Empty);
        }

        /// <summary>
        /// Verifies that Plugin.Id defaults to Guid.Empty in the default constructor.
        /// </summary>
        [Fact]
        public void Plugin_DefaultConstructor_IdIsGuidEmpty()
        {
            // Act
            var plugin = new Plugin();

            // Assert
            plugin.Id.Should().Be(Guid.Empty);
        }

        /// <summary>
        /// Verifies that Plugin.Version defaults to 0 in the default constructor.
        /// Matches the source ErpPlugin.cs where Version is an int with default value 0.
        /// </summary>
        [Fact]
        public void Plugin_DefaultConstructor_VersionIsZero()
        {
            // Act
            var plugin = new Plugin();

            // Assert
            plugin.Version.Should().Be(0);
        }

        /// <summary>
        /// Verifies that Plugin.Status defaults to PluginStatus.Active in the default constructor.
        /// Newly registered plugins should be active by default.
        /// </summary>
        [Fact]
        public void Plugin_DefaultConstructor_StatusIsActive()
        {
            // Act
            var plugin = new Plugin();

            // Assert
            plugin.Status.Should().Be(PluginStatus.Active);
        }

        /// <summary>
        /// Verifies that Plugin.CreatedAt and Plugin.UpdatedAt are initialized to approximately
        /// DateTime.UtcNow in the default constructor. Uses a 5-second tolerance to account
        /// for test execution time.
        /// </summary>
        [Fact]
        public void Plugin_DefaultConstructor_CreatedAtAndUpdatedAtSet()
        {
            // Arrange
            var beforeCreation = DateTime.UtcNow;

            // Act
            var plugin = new Plugin();

            // Assert — timestamps should be close to UtcNow (within 5 seconds tolerance)
            plugin.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
            plugin.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

            // Both timestamps should be in UTC
            plugin.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
            plugin.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        }

        #endregion

        #region Phase 6: RegisterPluginRequest DTO Tests

        /// <summary>
        /// Verifies that RegisterPluginRequest can be created with all required fields set.
        /// Required fields: Name, Prefix, Version (per DTO class definition).
        /// </summary>
        [Fact]
        public void RegisterPluginRequest_RequiredFieldsPresent()
        {
            // Act
            var request = new RegisterPluginRequest
            {
                Name = "test",
                Prefix = "t",
                Version = 1
            };

            // Assert — required fields set correctly
            request.Name.Should().Be("test");
            request.Prefix.Should().Be("t");
            request.Version.Should().Be(1);
        }

        /// <summary>
        /// Verifies that optional fields on RegisterPluginRequest default to null
        /// when only required fields are provided. This supports partial registration
        /// where only Name, Prefix, and Version are mandatory.
        /// </summary>
        [Fact]
        public void RegisterPluginRequest_OptionalFieldsCanBeNull()
        {
            // Act — create with only required fields
            var request = new RegisterPluginRequest
            {
                Name = "test",
                Prefix = "t",
                Version = 1
            };

            // Assert — all optional fields should be null
            request.Url.Should().BeNull();
            request.Description.Should().BeNull();
            request.Company.Should().BeNull();
            request.CompanyUrl.Should().BeNull();
            request.Author.Should().BeNull();
            request.Repository.Should().BeNull();
            request.License.Should().BeNull();
            request.SettingsUrl.Should().BeNull();
            request.PluginPageUrl.Should().BeNull();
            request.IconUrl.Should().BeNull();
        }

        /// <summary>
        /// Verifies that RegisterPluginRequest serializes and deserializes correctly
        /// using System.Text.Json, including proper JSON property naming.
        /// </summary>
        [Fact]
        public void RegisterPluginRequest_SystemTextJson_Serialization()
        {
            // Arrange
            var request = new RegisterPluginRequest
            {
                Name = "test-plugin",
                Prefix = "tp",
                Version = 20240101,
                Url = "https://example.com",
                Description = "A test plugin",
                Company = "Test Corp",
                CompanyUrl = "https://testcorp.com",
                Author = "Tester",
                Repository = "https://github.com/test/repo",
                License = "MIT",
                SettingsUrl = "/test/settings",
                PluginPageUrl = "/test/page",
                IconUrl = "/test/icon.png"
            };

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<RegisterPluginRequest>(json);

            // Assert — round-trip preserves all fields
            deserialized.Should().NotBeNull();
            deserialized!.Name.Should().Be("test-plugin");
            deserialized.Prefix.Should().Be("tp");
            deserialized.Version.Should().Be(20240101);
            deserialized.Url.Should().Be("https://example.com");
            deserialized.Description.Should().Be("A test plugin");
            deserialized.Company.Should().Be("Test Corp");
            deserialized.CompanyUrl.Should().Be("https://testcorp.com");
            deserialized.Author.Should().Be("Tester");
            deserialized.Repository.Should().Be("https://github.com/test/repo");
            deserialized.License.Should().Be("MIT");
            deserialized.SettingsUrl.Should().Be("/test/settings");
            deserialized.PluginPageUrl.Should().Be("/test/page");
            deserialized.IconUrl.Should().Be("/test/icon.png");

            // Verify JSON property names use snake_case for multi-word properties
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.TryGetProperty("company_url", out _).Should().BeTrue();
            doc.RootElement.TryGetProperty("settings_url", out _).Should().BeTrue();
            doc.RootElement.TryGetProperty("plugin_page_url", out _).Should().BeTrue();
            doc.RootElement.TryGetProperty("icon_url", out _).Should().BeTrue();
        }

        #endregion

        #region Phase 7: UpdatePluginRequest DTO Tests

        /// <summary>
        /// Verifies that all fields on UpdatePluginRequest are optional (nullable)
        /// and default to null when no properties are set. This supports partial
        /// updates where only specified fields are modified.
        /// </summary>
        [Fact]
        public void UpdatePluginRequest_AllFieldsOptional()
        {
            // Act — create with no properties set
            var request = new UpdatePluginRequest();

            // Assert — all nullable properties should be null
            request.Name.Should().BeNull();
            request.Prefix.Should().BeNull();
            request.Url.Should().BeNull();
            request.Description.Should().BeNull();
            request.Version.Should().BeNull();
            request.Company.Should().BeNull();
            request.CompanyUrl.Should().BeNull();
            request.Author.Should().BeNull();
            request.Repository.Should().BeNull();
            request.License.Should().BeNull();
            request.SettingsUrl.Should().BeNull();
            request.PluginPageUrl.Should().BeNull();
            request.IconUrl.Should().BeNull();
            request.Status.Should().BeNull();
        }

        /// <summary>
        /// Verifies that UpdatePluginRequest.Status can be set to PluginStatus.Inactive
        /// for plugin deactivation via PATCH semantics.
        /// </summary>
        [Fact]
        public void UpdatePluginRequest_StatusCanBeSet()
        {
            // Act
            var request = new UpdatePluginRequest
            {
                Status = PluginStatus.Inactive
            };

            // Assert
            request.Status.Should().Be(PluginStatus.Inactive);
        }

        #endregion

        #region Phase 8: PluginResponse DTO Tests

        /// <summary>
        /// Verifies that PluginResponse can represent a successful operation with a Plugin.
        /// </summary>
        [Fact]
        public void PluginResponse_SuccessWithPlugin()
        {
            // Arrange
            var testPlugin = CreateFullyPopulatedPlugin();

            // Act
            var response = new PluginResponse
            {
                Success = true,
                Plugin = testPlugin,
                Message = "OK"
            };

            // Assert
            response.Success.Should().BeTrue();
            response.Plugin.Should().NotBeNull();
            response.Plugin!.Id.Should().Be(testPlugin.Id);
            response.Plugin.Name.Should().Be("sdk");
            response.Message.Should().Be("OK");
        }

        /// <summary>
        /// Verifies that PluginResponse can represent a failed operation with error message
        /// and null Plugin.
        /// </summary>
        [Fact]
        public void PluginResponse_FailureWithMessage()
        {
            // Act
            var response = new PluginResponse
            {
                Success = false,
                Message = "Not found",
                Plugin = null
            };

            // Assert
            response.Success.Should().BeFalse();
            response.Message.Should().Be("Not found");
            response.Plugin.Should().BeNull();
        }

        /// <summary>
        /// Verifies that PluginResponse survives a System.Text.Json round-trip with
        /// structure preserved including the nested Plugin object.
        /// </summary>
        [Fact]
        public void PluginResponse_SystemTextJson_RoundTrip()
        {
            // Arrange
            var original = new PluginResponse
            {
                Success = true,
                Plugin = CreateFullyPopulatedPlugin(),
                Message = "Plugin retrieved successfully"
            };

            // Act
            var json = System.Text.Json.JsonSerializer.Serialize(original);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<PluginResponse>(json);

            // Assert
            deserialized.Should().NotBeNull();
            deserialized!.Success.Should().Be(original.Success);
            deserialized.Message.Should().Be(original.Message);
            deserialized.Plugin.Should().NotBeNull();
            deserialized.Plugin!.Id.Should().Be(original.Plugin!.Id);
            deserialized.Plugin.Name.Should().Be(original.Plugin.Name);
            deserialized.Plugin.Version.Should().Be(original.Plugin.Version);
            deserialized.Plugin.Status.Should().Be(original.Plugin.Status);

            // Verify JSON property names
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.TryGetProperty("success", out _).Should().BeTrue();
            doc.RootElement.TryGetProperty("plugin", out _).Should().BeTrue();
            doc.RootElement.TryGetProperty("message", out _).Should().BeTrue();
        }

        #endregion

        #region Phase 9: PluginListResponse DTO Tests

        /// <summary>
        /// Verifies that PluginListResponse correctly represents a list of plugins
        /// with accurate count and success status.
        /// </summary>
        [Fact]
        public void PluginListResponse_WithPlugins()
        {
            // Arrange — create a list of 3 plugins
            var plugins = new List<Plugin>
            {
                new Plugin { Id = Guid.NewGuid(), Name = "plugin-1", Version = 1 },
                new Plugin { Id = Guid.NewGuid(), Name = "plugin-2", Version = 2 },
                new Plugin { Id = Guid.NewGuid(), Name = "plugin-3", Version = 3 }
            };

            // Act
            var response = new PluginListResponse
            {
                Plugins = plugins,
                TotalCount = 3,
                Success = true
            };

            // Assert
            response.Plugins.Should().HaveCount(3);
            response.TotalCount.Should().Be(3);
            response.Success.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that PluginListResponse correctly represents an empty result set
        /// with zero count and empty plugin list.
        /// </summary>
        [Fact]
        public void PluginListResponse_EmptyList()
        {
            // Act
            var response = new PluginListResponse
            {
                Plugins = new List<Plugin>(),
                TotalCount = 0,
                Success = true
            };

            // Assert
            response.Plugins.Should().BeEmpty();
            response.TotalCount.Should().Be(0);
            response.Success.Should().BeTrue();
        }

        #endregion

        #region Phase 10: Property Count Verification

        /// <summary>
        /// Verifies that the Plugin class has exactly 17 public instance properties:
        /// 13 from source ErpPlugin.cs (Name, Prefix, Url, Description, Version, Company,
        /// CompanyUrl, Author, Repository, License, SettingsUrl, PluginPageUrl, IconUrl)
        /// + 4 new properties for microservices architecture (Id, Status, CreatedAt, UpdatedAt).
        /// 
        /// This guard test ensures no properties are accidentally added or removed,
        /// which would break JSON serialization contracts with API consumers.
        /// </summary>
        [Fact]
        public void Plugin_HasExactly17Properties()
        {
            // Act — get all public instance properties via reflection
            var properties = typeof(Plugin)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // Assert — exactly 17 properties expected
            properties.Should().HaveCount(17,
                "Plugin class must have exactly 17 public properties: " +
                "13 from source ErpPlugin.cs + 4 new (Id, Status, CreatedAt, UpdatedAt)");

            // Verify the expected property names are all present
            var propertyNames = properties.Select(p => p.Name).ToList();
            var expectedNames = new[]
            {
                "Id", "Name", "Prefix", "Url", "Description", "Version",
                "Company", "CompanyUrl", "Author", "Repository", "License",
                "SettingsUrl", "PluginPageUrl", "IconUrl",
                "Status", "CreatedAt", "UpdatedAt"
            };

            foreach (var name in expectedNames)
            {
                propertyNames.Should().Contain(name,
                    $"Plugin class should have property '{name}'");
            }
        }

        #endregion
    }
}
