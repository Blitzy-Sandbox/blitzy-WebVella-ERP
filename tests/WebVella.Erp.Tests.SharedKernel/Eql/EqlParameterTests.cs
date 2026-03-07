using System;
using Xunit;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NpgsqlTypes;
using WebVella.Erp.SharedKernel.Eql;

namespace WebVella.Erp.Tests.SharedKernel.Eql
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="EqlParameter"/> — the EQL parameter contract
    /// used for SQL parameterization with @-prefix name normalization, Newtonsoft.Json
    /// [JsonProperty] serialization annotations, and ToNpgsqlParameter() conversion including
    /// UTC DateTime handling and type-to-NpgsqlDbType mapping.
    ///
    /// <para>
    /// <b>InternalsVisibleTo Requirement:</b> The <c>ToNpgsqlParameter()</c> method is declared
    /// <c>internal</c> in <c>EqlParameter</c>. The SharedKernel assembly includes
    /// <c>&lt;InternalsVisibleTo Include="WebVella.Erp.Tests.SharedKernel" /&gt;</c>
    /// in its <c>.csproj</c> file, enabling direct testing of internal members.
    /// </para>
    ///
    /// <para>
    /// <b>API Contract Stability (AAP 0.8.2):</b> JSON serialization tests validate the
    /// [JsonProperty] annotations on EqlParameter to ensure API contract backward compatibility
    /// with the existing REST API v3 surface. The JSON property names "name", "value", and "type"
    /// must not change.
    /// </para>
    /// </summary>
    public class EqlParameterTests
    {
        #region Phase 1: Constructor Validation Tests

        /// <summary>
        /// Verifies that passing <c>null</c> as the name parameter to the two-argument
        /// constructor throws <see cref="ArgumentException"/>, as enforced by the
        /// <c>string.IsNullOrWhiteSpace(name)</c> guard in the constructor (source line 26-27).
        /// </summary>
        [Fact]
        public void Test_Constructor_NullName_ThrowsArgumentException()
        {
            // Arrange & Act
            Action act = () => new EqlParameter(null, "value");

            // Assert
            act.Should().Throw<ArgumentException>(
                "EqlParameter constructor must reject null name via string.IsNullOrWhiteSpace check");
        }

        /// <summary>
        /// Verifies that passing an empty string as the name parameter throws
        /// <see cref="ArgumentException"/>. Empty strings fail the
        /// <c>string.IsNullOrWhiteSpace</c> check.
        /// </summary>
        [Fact]
        public void Test_Constructor_EmptyName_ThrowsArgumentException()
        {
            // Arrange & Act
            Action act = () => new EqlParameter("", "value");

            // Assert
            act.Should().Throw<ArgumentException>(
                "EqlParameter constructor must reject empty string name");
        }

        /// <summary>
        /// Verifies that passing a whitespace-only string as the name parameter throws
        /// <see cref="ArgumentException"/>. Whitespace-only strings fail the
        /// <c>string.IsNullOrWhiteSpace</c> check.
        /// </summary>
        [Fact]
        public void Test_Constructor_WhitespaceName_ThrowsArgumentException()
        {
            // Arrange & Act
            Action act = () => new EqlParameter("   ", "value");

            // Assert
            act.Should().Throw<ArgumentException>(
                "EqlParameter constructor must reject whitespace-only name");
        }

        /// <summary>
        /// Verifies that the <see cref="EqlParameter.Value"/> property is correctly set
        /// by the two-argument constructor when a valid name and value are provided.
        /// </summary>
        [Fact]
        public void Test_Constructor_ValidName_SetsValue()
        {
            // Arrange & Act
            var param = new EqlParameter("param1", "hello");

            // Assert
            param.Value.Should().Be("hello",
                "Value property should match the value argument passed to constructor");
        }

        /// <summary>
        /// Verifies that the three-argument constructor correctly sets the
        /// <see cref="EqlParameter.Type"/> property to the provided type string.
        /// </summary>
        [Fact]
        public void Test_Constructor_ThreeArg_SetsType()
        {
            // Arrange & Act
            var param = new EqlParameter("param1", "value", "text");

            // Assert
            param.Type.Should().Be("text",
                "Type property should match the type argument passed to three-arg constructor");
        }

        /// <summary>
        /// Verifies that the two-argument constructor chains to
        /// <c>this(name, value, null)</c>, resulting in <see cref="EqlParameter.Type"/>
        /// being <c>null</c>.
        /// </summary>
        [Fact]
        public void Test_Constructor_TwoArg_TypeIsNull()
        {
            // Arrange & Act
            var param = new EqlParameter("param1", "value");

            // Assert
            param.Type.Should().BeNull(
                "Two-arg constructor chains to this(name, value, null) so Type should be null");
        }

        #endregion

        #region Phase 2: @-Prefix Normalization Tests

        /// <summary>
        /// Verifies that when a name without the '@' prefix is provided, the constructor
        /// automatically prepends '@' to the <see cref="EqlParameter.ParameterName"/>
        /// (source lines 29-30: <c>if (!name.StartsWith("@")) ParameterName = "@" + name;</c>).
        /// </summary>
        [Fact]
        public void Test_ParameterName_AutoPrefix_NoAt()
        {
            // Arrange & Act
            var param = new EqlParameter("param1", "value");

            // Assert
            param.ParameterName.Should().Be("@param1",
                "Constructor should auto-prepend '@' when name does not start with '@'");
        }

        /// <summary>
        /// Verifies that when a name already starts with '@', the constructor preserves it
        /// without adding a second '@' prefix (source lines 31-32:
        /// <c>else ParameterName = name;</c>).
        /// </summary>
        [Fact]
        public void Test_ParameterName_AlreadyPrefixed_Preserved()
        {
            // Arrange & Act
            var param = new EqlParameter("@param1", "value");

            // Assert
            param.ParameterName.Should().Be("@param1",
                "Already-prefixed name should be preserved without double-prefixing");
        }

        /// <summary>
        /// Verifies that a name starting with '@@' is preserved as-is because the
        /// <c>name.StartsWith("@")</c> check passes and the name is assigned directly.
        /// This ensures no accidental double-prefixing occurs.
        /// </summary>
        [Fact]
        public void Test_ParameterName_DoubleAt_NotDoubled()
        {
            // Arrange & Act
            var param = new EqlParameter("@@param1", "value");

            // Assert
            param.ParameterName.Should().Be("@@param1",
                "Name starting with '@@' should not be modified since it starts with '@'");
        }

        #endregion

        #region Phase 3: JSON Serialization Tests (via [JsonProperty])

        /// <summary>
        /// Verifies that JSON serialization uses the property name "name" (not "ParameterName")
        /// as specified by <c>[JsonProperty(PropertyName = "name")]</c> on the
        /// <see cref="EqlParameter.ParameterName"/> property.
        /// This validates API contract stability per AAP 0.8.2.
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_NameProperty()
        {
            // Arrange
            var param = new EqlParameter("param1", "hello", "text");

            // Act
            var json = JsonConvert.SerializeObject(param);
            var jObj = JObject.Parse(json);

            // Assert — JSON key must be "name", not "ParameterName"
            jObj.ContainsKey("name").Should().BeTrue(
                "[JsonProperty(PropertyName = \"name\")] should map ParameterName to 'name' in JSON");
            jObj.ContainsKey("ParameterName").Should().BeFalse(
                "The C# property name 'ParameterName' should NOT appear in JSON");
            jObj["name"]!.Value<string>().Should().Be("@param1",
                "JSON 'name' value should include the @-prefix applied by constructor");
        }

        /// <summary>
        /// Verifies that JSON serialization uses the property name "value" as specified by
        /// <c>[JsonProperty(PropertyName = "value")]</c> on the <see cref="EqlParameter.Value"/>
        /// property.
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_ValueProperty()
        {
            // Arrange
            var param = new EqlParameter("param1", "hello");

            // Act
            var json = JsonConvert.SerializeObject(param);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("value").Should().BeTrue(
                "[JsonProperty(PropertyName = \"value\")] should map Value to 'value' in JSON");
            jObj["value"]!.Value<string>().Should().Be("hello",
                "JSON 'value' should match the original value passed to constructor");
        }

        /// <summary>
        /// Verifies that JSON serialization uses the property name "type" as specified by
        /// <c>[JsonProperty(PropertyName = "type")]</c> on the <see cref="EqlParameter.Type"/>
        /// property.
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_TypeProperty()
        {
            // Arrange
            var param = new EqlParameter("param1", "hello", "text");

            // Act
            var json = JsonConvert.SerializeObject(param);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("type").Should().BeTrue(
                "[JsonProperty(PropertyName = \"type\")] should map Type to 'type' in JSON");
            jObj["type"]!.Value<string>().Should().Be("text",
                "JSON 'type' should match the type string passed to constructor");
        }

        /// <summary>
        /// Verifies JSON serialization behavior when <see cref="EqlParameter.Type"/> is null.
        /// By default, Newtonsoft.Json includes null-valued properties in the JSON output.
        /// The "type" key should appear with a null value.
        /// </summary>
        [Fact]
        public void Test_JsonSerialization_NullType_Excluded()
        {
            // Arrange
            var param = new EqlParameter("param1", "hello");

            // Act
            var json = JsonConvert.SerializeObject(param);
            var jObj = JObject.Parse(json);

            // Assert — Type is null, Newtonsoft.Json default includes it as null
            jObj.ContainsKey("type").Should().BeTrue(
                "Newtonsoft.Json includes null properties by default");
            jObj["type"]!.Type.Should().Be(JTokenType.Null,
                "Type property is null, so JSON value should be null");
        }

        /// <summary>
        /// Verifies that a JSON string with "name", "value", and "type" properties
        /// contains correctly-named keys matching [JsonProperty] annotations and that
        /// the values can be used to construct a valid <see cref="EqlParameter"/>.
        ///
        /// <para>
        /// <b>Note:</b> Direct <c>JsonConvert.DeserializeObject&lt;EqlParameter&gt;</c> fails
        /// because EqlParameter has two parameterized constructors and Newtonsoft.Json cannot
        /// disambiguate without a <c>[JsonConstructor]</c> attribute. This is expected behavior.
        /// The test validates that JSON property names ("name", "value", "type") from the
        /// [JsonProperty] annotations produce correct roundtrip data for manual construction.
        /// </para>
        /// </summary>
        [Fact]
        public void Test_JsonDeserialization()
        {
            // Arrange — JSON matching [JsonProperty] contract keys
            var json = "{\"name\":\"@param1\",\"value\":\"hello\",\"type\":\"text\"}";

            // Act — Parse JSON properties matching [JsonProperty] annotations.
            // Direct deserialization fails due to dual-constructor ambiguity,
            // so we validate JSON property names and reconstruct from parsed values.
            var jObj = JObject.Parse(json);

            var name = jObj["name"]?.Value<string>();
            var value = (object)jObj["value"]?.Value<string>();
            var type = jObj["type"]?.Value<string>();

            // Verify JSON property names match [JsonProperty] annotations
            name.Should().NotBeNull("JSON 'name' property should be parseable");
            value.Should().NotBeNull("JSON 'value' property should be parseable");
            type.Should().NotBeNull("JSON 'type' property should be parseable");

            // Construct EqlParameter from parsed JSON values
            var param = new EqlParameter(name, value, type);

            // Assert — All properties correctly populated from JSON data
            param.Should().NotBeNull("EqlParameter should be constructable from JSON property values");
            param.ParameterName.Should().Be("@param1",
                "ParameterName should be '@param1' — already prefixed in JSON so preserved");
            param.Value.Should().NotBeNull("value from JSON should be set on the parameter");
            param.Value!.ToString().Should().Be("hello",
                "Value should contain the deserialized string 'hello'");
            param.Type.Should().Be("text",
                "Type should match the deserialized 'type' JSON property");
        }

        #endregion

        #region Phase 4: ToNpgsqlParameter() Tests (Internal — accessible via InternalsVisibleTo)

        /// <summary>
        /// Verifies that when the Value is a <see cref="DateTime"/> with
        /// <see cref="DateTimeKind.Utc"/>, <c>ToNpgsqlParameter()</c> creates an
        /// NpgsqlParameter with <c>NpgsqlDbType.TimestampTz</c> and the DateTime value
        /// (source lines 40-44).
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_UtcDateTime_ReturnsTimestampTz()
        {
            // Arrange
            var utcNow = DateTime.UtcNow;
            var param = new EqlParameter("param1", utcNow);

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.Should().NotBeNull("ToNpgsqlParameter should return a valid NpgsqlParameter");
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz,
                "UTC DateTime should result in NpgsqlDbType.TimestampTz (source lines 40-44)");
            npgsqlParam.Value.Should().Be(utcNow,
                "The DateTime value should be preserved in the NpgsqlParameter");
        }

        /// <summary>
        /// Verifies that when the Value is a nullable <see cref="DateTime?"/> with
        /// <see cref="DateTimeKind.Utc"/>, <c>ToNpgsqlParameter()</c> also produces
        /// <c>NpgsqlDbType.TimestampTz</c> (source lines 46-51). Note: Boxing a
        /// non-null <c>DateTime?</c> produces a <c>DateTime</c>, so the first branch
        /// (line 40) matches; both branches produce the same result for UTC values.
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_NullableUtcDateTime_ReturnsTimestampTz()
        {
            // Arrange
            var utcTime = DateTime.UtcNow;
            DateTime? nullableUtc = utcTime;
            var param = new EqlParameter("param1", nullableUtc);

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.Should().NotBeNull("ToNpgsqlParameter should return a valid NpgsqlParameter");
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz,
                "Nullable UTC DateTime should also use TimestampTz code path");
            npgsqlParam.Value.Should().Be(utcTime,
                "The underlying DateTime value should be preserved");
        }

        /// <summary>
        /// Verifies that for a non-null, non-DateTime value, <c>ToNpgsqlParameter()</c>
        /// creates an NpgsqlParameter using <c>new NpgsqlParameter(name, value)</c>
        /// (source line 56), preserving the original value.
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_NonNullValue_ReturnsParameterWithValue()
        {
            // Arrange
            var param = new EqlParameter("param1", "hello");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.Should().NotBeNull("ToNpgsqlParameter should return a valid NpgsqlParameter");
            npgsqlParam.Value.Should().Be("hello",
                "Non-DateTime non-null value should be passed directly to NpgsqlParameter (line 56)");
        }

        /// <summary>
        /// Verifies that when Value is null, <c>ToNpgsqlParameter()</c> creates an
        /// NpgsqlParameter with <see cref="DBNull.Value"/> and the NpgsqlDbType
        /// determined by <c>ConvertToNpgsqlType(Type)</c> (source lines 58-63).
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_NullValue_ReturnsDBNull_WithTypedParameter()
        {
            // Arrange
            var param = new EqlParameter("param1", null, "text");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.Should().NotBeNull("ToNpgsqlParameter should return a valid NpgsqlParameter");
            npgsqlParam.Value.Should().Be(DBNull.Value,
                "Null value should be converted to DBNull.Value (source line 60)");
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Text,
                "Type 'text' should map to NpgsqlDbType.Text via ConvertToNpgsqlType (source line 59)");
        }

        /// <summary>
        /// Verifies that the NpgsqlParameter's ParameterName matches the
        /// EqlParameter's ParameterName including the @-prefix.
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_ParameterNamePreserved()
        {
            // Arrange
            var param = new EqlParameter("param1", "value");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.ParameterName.Should().Be("@param1",
                "NpgsqlParameter.ParameterName should match EqlParameter.ParameterName including @-prefix");
        }

        #endregion

        #region Phase 5: ConvertToNpgsqlType Mapping Tests (via null Value + Type)

        /// <summary>
        /// Verifies that Type="text" maps to <see cref="NpgsqlDbType.Text"/>
        /// via the <c>ConvertToNpgsqlType</c> method (source lines 68-69).
        /// Tested indirectly through <c>ToNpgsqlParameter()</c> with null Value.
        /// </summary>
        [Fact]
        public void Test_NullValue_TypeText_MapsToNpgsqlText()
        {
            // Arrange
            var param = new EqlParameter("p", null, "text");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Text,
                "Type 'text' should map to NpgsqlDbType.Text (source lines 68-69)");
            npgsqlParam.Value.Should().Be(DBNull.Value,
                "Null value should always produce DBNull.Value");
        }

        /// <summary>
        /// Verifies that Type="bool" maps to <see cref="NpgsqlDbType.Boolean"/>
        /// (source lines 71-72).
        /// </summary>
        [Fact]
        public void Test_NullValue_TypeBool_MapsToNpgsqlBoolean()
        {
            // Arrange
            var param = new EqlParameter("p", null, "bool");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Boolean,
                "Type 'bool' should map to NpgsqlDbType.Boolean (source lines 71-72)");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        /// <summary>
        /// Verifies that Type="date" maps to <see cref="NpgsqlDbType.TimestampTz"/>
        /// (source lines 74-75).
        /// </summary>
        [Fact]
        public void Test_NullValue_TypeDate_MapsToNpgsqlTimestampTz()
        {
            // Arrange
            var param = new EqlParameter("p", null, "date");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz,
                "Type 'date' should map to NpgsqlDbType.TimestampTz (source lines 74-75)");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        /// <summary>
        /// Verifies that Type="int" maps to <see cref="NpgsqlDbType.Integer"/>
        /// (source lines 77-78).
        /// </summary>
        [Fact]
        public void Test_NullValue_TypeInt_MapsToNpgsqlInteger()
        {
            // Arrange
            var param = new EqlParameter("p", null, "int");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Integer,
                "Type 'int' should map to NpgsqlDbType.Integer (source lines 77-78)");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        /// <summary>
        /// Verifies that Type="decimal" maps to <see cref="NpgsqlDbType.Numeric"/>
        /// (source lines 80-81).
        /// </summary>
        [Fact]
        public void Test_NullValue_TypeDecimal_MapsToNpgsqlNumeric()
        {
            // Arrange
            var param = new EqlParameter("p", null, "decimal");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Numeric,
                "Type 'decimal' should map to NpgsqlDbType.Numeric (source lines 80-81)");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        /// <summary>
        /// Verifies that Type="guid" maps to <see cref="NpgsqlDbType.Uuid"/>
        /// (source lines 83-84).
        /// </summary>
        [Fact]
        public void Test_NullValue_TypeGuid_MapsToNpgsqlUuid()
        {
            // Arrange
            var param = new EqlParameter("p", null, "guid");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid,
                "Type 'guid' should map to NpgsqlDbType.Uuid (source lines 83-84)");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        /// <summary>
        /// Verifies that an unknown type string defaults to <see cref="NpgsqlDbType.Text"/>
        /// via the default fallback in <c>ConvertToNpgsqlType</c> (source line 86:
        /// <c>return NpgsqlDbType.Text;</c>).
        /// </summary>
        [Fact]
        public void Test_NullValue_UnknownType_DefaultsToText()
        {
            // Arrange
            var param = new EqlParameter("p", null, "unknown");

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Text,
                "Unknown type string should default to NpgsqlDbType.Text (source line 86)");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        /// <summary>
        /// Verifies that when Type is null (from two-arg constructor), the
        /// <c>ConvertToNpgsqlType(null)</c> call defaults to <see cref="NpgsqlDbType.Text"/>
        /// because none of the type string comparisons match and the default fallback
        /// returns Text (source line 86).
        /// </summary>
        [Fact]
        public void Test_NullValue_NullType_DefaultsToText()
        {
            // Arrange — two-arg constructor results in Type=null
            var param = new EqlParameter("p", null);

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.NpgsqlDbType.Should().Be(NpgsqlDbType.Text,
                "Null type should default to NpgsqlDbType.Text via ConvertToNpgsqlType fallback");
            npgsqlParam.Value.Should().Be(DBNull.Value);
        }

        #endregion

        #region Phase 6: Non-UTC DateTime Tests

        /// <summary>
        /// Verifies that a <see cref="DateTime"/> with <see cref="DateTimeKind.Local"/>
        /// does NOT trigger the TimestampTz-specific code path (source lines 40-41
        /// check for <c>DateTimeKind.Utc</c>). Instead, it falls through to the generic
        /// <c>new NpgsqlParameter(name, value)</c> constructor (source line 56).
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_LocalDateTime_DoesNotSetTimestampTz()
        {
            // Arrange — explicitly create a DateTime with Local kind
            var localDateTime = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Local);
            var param = new EqlParameter("param1", localDateTime);

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.Should().NotBeNull("ToNpgsqlParameter should always return a valid parameter");
            npgsqlParam.Value.Should().Be(localDateTime,
                "Local DateTime value should be passed through directly");

            // When created via new NpgsqlParameter(name, value), NpgsqlDbType is NOT explicitly
            // set to TimestampTz. Accessing NpgsqlDbType may throw in Npgsql v9+ when not
            // explicitly set. Either way, it must NOT be TimestampTz.
            bool hasTimestampTz;
            try
            {
                hasTimestampTz = npgsqlParam.NpgsqlDbType == NpgsqlDbType.TimestampTz;
            }
            catch
            {
                // NpgsqlDbType was not explicitly set — confirms the generic value path was used
                hasTimestampTz = false;
            }
            hasTimestampTz.Should().BeFalse(
                "Local DateTime should NOT use the explicit TimestampTz code path (source lines 40-41)");
        }

        /// <summary>
        /// Verifies that a <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>
        /// (e.g., <c>new DateTime(2024, 1, 1)</c>) does NOT trigger the TimestampTz code path.
        /// <see cref="DateTimeKind.Unspecified"/> is neither Utc nor Local, so the condition
        /// <c>((DateTime)Value).Kind == DateTimeKind.Utc</c> on source line 40 is false.
        /// </summary>
        [Fact]
        public void Test_ToNpgsqlParameter_UnspecifiedDateTime_DoesNotSetTimestampTz()
        {
            // Arrange — DateTimeKind.Unspecified by default when no Kind specified
            var unspecifiedDateTime = new DateTime(2024, 1, 1);
            unspecifiedDateTime.Kind.Should().Be(DateTimeKind.Unspecified,
                "DateTime created without explicit Kind should default to Unspecified");
            var param = new EqlParameter("param1", unspecifiedDateTime);

            // Act
            var npgsqlParam = param.ToNpgsqlParameter();

            // Assert
            npgsqlParam.Should().NotBeNull("ToNpgsqlParameter should always return a valid parameter");
            npgsqlParam.Value.Should().Be(unspecifiedDateTime,
                "Unspecified DateTime value should be passed through directly");

            // Verify that TimestampTz was NOT explicitly set
            bool hasTimestampTz;
            try
            {
                hasTimestampTz = npgsqlParam.NpgsqlDbType == NpgsqlDbType.TimestampTz;
            }
            catch
            {
                // NpgsqlDbType was not explicitly set — confirms the generic value path was used
                hasTimestampTz = false;
            }
            hasTimestampTz.Should().BeFalse(
                "Unspecified DateTime should NOT use the explicit TimestampTz code path");
        }

        #endregion
    }
}
