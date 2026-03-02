using System;
using System.Collections.Generic;
using System.Net;
using Xunit;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.SharedKernel.Models;

namespace WebVella.Erp.Tests.SharedKernel.Models
{
    /// <summary>
    /// Comprehensive unit tests for the API response envelope model types:
    /// BaseResponseModel, ResponseModel, ErrorModel, and AccessWarningModel.
    /// Validates constructor defaults, property get/set, JSON serialization
    /// with correct [JsonProperty] names, [JsonIgnore] behavior on StatusCode,
    /// and the full response envelope contract shape.
    /// </summary>
    public class BaseResponseModelTests
    {
        // =====================================================================
        // BaseResponseModel — Constructor / Default Value Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the constructor explicitly sets Hash to null.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Constructor_SetsHashToNull()
        {
            // Arrange & Act
            var model = new BaseResponseModel();

            // Assert
            model.Hash.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the constructor initializes Errors as a non-null empty list.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Constructor_CreatesEmptyErrorsList()
        {
            // Arrange & Act
            var model = new BaseResponseModel();

            // Assert
            model.Errors.Should().NotBeNull();
            model.Errors.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the constructor initializes AccessWarnings as a non-null empty list.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Constructor_CreatesEmptyAccessWarningsList()
        {
            // Arrange & Act
            var model = new BaseResponseModel();

            // Assert
            model.AccessWarnings.Should().NotBeNull();
            model.AccessWarnings.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that the constructor sets StatusCode to HttpStatusCode.OK (200).
        /// </summary>
        [Fact]
        public void BaseResponseModel_Constructor_SetsStatusCodeToOK()
        {
            // Arrange & Act
            var model = new BaseResponseModel();

            // Assert
            model.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        /// <summary>
        /// Verifies that Success defaults to false (C# bool default).
        /// The constructor does not explicitly set Success, so it remains the default bool value.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Constructor_SuccessDefaultsFalse()
        {
            // Arrange & Act
            var model = new BaseResponseModel();

            // Assert
            model.Success.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that Timestamp defaults to DateTime.MinValue (C# DateTime default).
        /// The constructor does not explicitly set Timestamp.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Constructor_TimestampDefaultsMinValue()
        {
            // Arrange & Act
            var model = new BaseResponseModel();

            // Assert
            model.Timestamp.Should().Be(default(DateTime));
        }

        // =====================================================================
        // BaseResponseModel — Property Get/Set Tests
        // =====================================================================

        /// <summary>
        /// Verifies that Timestamp can be set and retrieved correctly.
        /// </summary>
        [Fact]
        public void Timestamp_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var model = new BaseResponseModel();
            var expected = DateTime.UtcNow;

            // Act
            model.Timestamp = expected;

            // Assert
            model.Timestamp.Should().Be(expected);
        }

        /// <summary>
        /// Verifies that Success can be toggled and read back.
        /// </summary>
        [Fact]
        public void Success_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            model.Success = true;

            // Assert
            model.Success.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that Message can be assigned and read back.
        /// </summary>
        [Fact]
        public void Message_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var model = new BaseResponseModel();
            var expected = "Operation completed successfully";

            // Act
            model.Message = expected;

            // Assert
            model.Message.Should().Be(expected);
        }

        /// <summary>
        /// Verifies that Hash can be assigned and read back.
        /// </summary>
        [Fact]
        public void Hash_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var model = new BaseResponseModel();
            var expected = "abc123hash";

            // Act
            model.Hash = expected;

            // Assert
            model.Hash.Should().Be(expected);
        }

        /// <summary>
        /// Verifies that StatusCode can be set to different values and read back.
        /// </summary>
        [Fact]
        public void StatusCode_SetAndGet_ReturnsCorrectValue()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act — set to BadRequest
            model.StatusCode = HttpStatusCode.BadRequest;

            // Assert
            model.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Act — set to InternalServerError
            model.StatusCode = HttpStatusCode.InternalServerError;

            // Assert
            model.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }

        // =====================================================================
        // BaseResponseModel — JSON Serialization Tests
        // =====================================================================

        /// <summary>
        /// Verifies that serialized JSON includes the "timestamp" key
        /// matching the [JsonProperty(PropertyName = "timestamp")] attribute.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_IncludesTimestampKey()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("timestamp").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that serialized JSON includes the "success" key
        /// matching the [JsonProperty(PropertyName = "success")] attribute.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_IncludesSuccessKey()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("success").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that serialized JSON includes the "message" key
        /// matching the [JsonProperty(PropertyName = "message")] attribute.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_IncludesMessageKey()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("message").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that serialized JSON includes the "hash" key
        /// matching the [JsonProperty(PropertyName = "hash")] attribute.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_IncludesHashKey()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("hash").Should().BeTrue();
        }

        /// <summary>
        /// Verifies that serialized JSON includes the "errors" key as an array
        /// matching the [JsonProperty(PropertyName = "errors")] attribute.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_IncludesErrorsKey()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("errors").Should().BeTrue();
            jObj["errors"].Should().NotBeNull();
            jObj["errors"].Type.Should().Be(JTokenType.Array);
        }

        /// <summary>
        /// Verifies that serialized JSON includes the "accessWarnings" key as an array
        /// matching the [JsonProperty(PropertyName = "accessWarnings")] attribute.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_IncludesAccessWarningsKey()
        {
            // Arrange
            var model = new BaseResponseModel();

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("accessWarnings").Should().BeTrue();
            jObj["accessWarnings"].Should().NotBeNull();
            jObj["accessWarnings"].Type.Should().Be(JTokenType.Array);
        }

        /// <summary>
        /// Verifies that StatusCode is excluded from serialized JSON output
        /// due to the [JsonIgnore] attribute. Neither "StatusCode", "statusCode",
        /// nor "status_code" keys should appear.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_ExcludesStatusCode()
        {
            // Arrange
            var model = new BaseResponseModel
            {
                StatusCode = HttpStatusCode.BadRequest
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert — no StatusCode-related key should exist
            jObj.ContainsKey("StatusCode").Should().BeFalse();
            jObj.ContainsKey("statusCode").Should().BeFalse();
            jObj.ContainsKey("status_code").Should().BeFalse();
        }

        /// <summary>
        /// Verifies that deserializing a JSON string with all visible properties
        /// correctly maps them onto a BaseResponseModel instance.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Deserialize_SetsProperties()
        {
            // Arrange
            var json = @"{
                ""timestamp"": ""2025-06-15T12:30:00Z"",
                ""success"": true,
                ""message"": ""All good"",
                ""hash"": ""xyz789"",
                ""errors"": [{ ""key"": ""field1"", ""value"": ""bad"", ""message"": ""Invalid"" }],
                ""accessWarnings"": [{ ""key"": ""w1"", ""code"": ""403"", ""message"": ""Forbidden"" }]
            }";

            // Act
            var model = JsonConvert.DeserializeObject<BaseResponseModel>(json);

            // Assert
            model.Should().NotBeNull();
            model.Timestamp.Should().Be(new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc));
            model.Success.Should().BeTrue();
            model.Message.Should().Be("All good");
            model.Hash.Should().Be("xyz789");
            model.Errors.Should().NotBeNull();
            model.Errors.Should().HaveCount(1);
            model.Errors[0].Key.Should().Be("field1");
            model.Errors[0].Value.Should().Be("bad");
            model.Errors[0].Message.Should().Be("Invalid");
            model.AccessWarnings.Should().NotBeNull();
            model.AccessWarnings.Should().HaveCount(1);
            model.AccessWarnings[0].Key.Should().Be("w1");
            model.AccessWarnings[0].Code.Should().Be("403");
            model.AccessWarnings[0].Message.Should().Be("Forbidden");
        }

        /// <summary>
        /// Verifies that serializing and then deserializing a BaseResponseModel
        /// preserves all visible property values through a full roundtrip.
        /// StatusCode is excluded from JSON and thus resets to default on deserialization.
        /// </summary>
        [Fact]
        public void BaseResponseModel_Serialize_Roundtrip()
        {
            // Arrange
            var original = new BaseResponseModel
            {
                Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Success = true,
                Message = "Roundtrip test",
                Hash = "roundhash",
                StatusCode = HttpStatusCode.InternalServerError
            };
            original.Errors.Add(new ErrorModel("k1", "v1", "m1"));
            original.AccessWarnings.Add(new AccessWarningModel { Key = "wk", Code = "wc", Message = "wm" });

            // Act
            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<BaseResponseModel>(json);

            // Assert — visible properties should survive roundtrip
            deserialized.Should().NotBeNull();
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.Success.Should().Be(original.Success);
            deserialized.Message.Should().Be(original.Message);
            deserialized.Hash.Should().Be(original.Hash);
            deserialized.Errors.Should().HaveCount(1);
            deserialized.Errors[0].Key.Should().Be("k1");
            deserialized.AccessWarnings.Should().HaveCount(1);
            deserialized.AccessWarnings[0].Key.Should().Be("wk");

            // StatusCode is [JsonIgnore], so it resets to constructor default (OK)
            deserialized.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // =====================================================================
        // ResponseModel Tests
        // =====================================================================

        /// <summary>
        /// Verifies that ResponseModel inherits from BaseResponseModel.
        /// </summary>
        [Fact]
        public void ResponseModel_InheritsBaseResponseModel()
        {
            // Assert
            typeof(ResponseModel).Should().BeAssignableTo<BaseResponseModel>();
        }

        /// <summary>
        /// Verifies that ResponseModel's constructor invokes the base constructor,
        /// inheriting all default property values.
        /// </summary>
        [Fact]
        public void ResponseModel_Constructor_InheritsBaseDefaults()
        {
            // Arrange & Act
            var model = new ResponseModel();

            // Assert — base constructor defaults
            model.Hash.Should().BeNull();
            model.Errors.Should().NotBeNull();
            model.Errors.Should().BeEmpty();
            model.AccessWarnings.Should().NotBeNull();
            model.AccessWarnings.Should().BeEmpty();
            model.StatusCode.Should().Be(HttpStatusCode.OK);
            model.Success.Should().BeFalse();
            model.Timestamp.Should().Be(default(DateTime));
        }

        /// <summary>
        /// Verifies that the Object property can be set and read back correctly.
        /// </summary>
        [Fact]
        public void ResponseModel_Object_SetAndGet()
        {
            // Arrange
            var model = new ResponseModel();
            var payload = "test-payload-string";

            // Act
            model.Object = payload;

            // Assert
            model.Object.Should().Be(payload);
        }

        /// <summary>
        /// Verifies that serialized JSON includes the "object" key
        /// matching the [JsonProperty(PropertyName = "object")] attribute.
        /// </summary>
        [Fact]
        public void ResponseModel_Serialize_IncludesObjectKey()
        {
            // Arrange
            var model = new ResponseModel
            {
                Object = "test-value"
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("object").Should().BeTrue();
            jObj["object"].ToString().Should().Be("test-value");
        }

        /// <summary>
        /// Verifies that setting Object to a complex nested type results
        /// in correct JSON representation.
        /// </summary>
        [Fact]
        public void ResponseModel_Serialize_WithNestedObject()
        {
            // Arrange
            var nestedObj = new { Name = "TestEntity", Id = 42, Active = true };
            var model = new ResponseModel
            {
                Object = nestedObj
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("object").Should().BeTrue();
            var objectToken = jObj["object"];
            objectToken.Should().NotBeNull();
            objectToken["Name"].ToString().Should().Be("TestEntity");
            ((int)objectToken["Id"]).Should().Be(42);
            ((bool)objectToken["Active"]).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that when Object is null, serialization handles it gracefully
        /// and the "object" key is present with a null value.
        /// </summary>
        [Fact]
        public void ResponseModel_Serialize_WithNullObject()
        {
            // Arrange
            var model = new ResponseModel
            {
                Object = null
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("object").Should().BeTrue();
            jObj["object"].Type.Should().Be(JTokenType.Null);
        }

        /// <summary>
        /// Verifies that ResponseModel serialization includes all BaseResponseModel
        /// JSON keys plus the "object" key.
        /// </summary>
        [Fact]
        public void ResponseModel_Serialize_IncludesAllBaseProperties()
        {
            // Arrange
            var model = new ResponseModel
            {
                Timestamp = DateTime.UtcNow,
                Success = true,
                Message = "OK",
                Hash = "h1",
                Object = "data"
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert — all base keys must be present
            jObj.ContainsKey("timestamp").Should().BeTrue();
            jObj.ContainsKey("success").Should().BeTrue();
            jObj.ContainsKey("message").Should().BeTrue();
            jObj.ContainsKey("hash").Should().BeTrue();
            jObj.ContainsKey("errors").Should().BeTrue();
            jObj.ContainsKey("accessWarnings").Should().BeTrue();
            // ResponseModel's own key
            jObj.ContainsKey("object").Should().BeTrue();
            // StatusCode must NOT be present
            jObj.ContainsKey("StatusCode").Should().BeFalse();
            jObj.ContainsKey("statusCode").Should().BeFalse();
        }

        // =====================================================================
        // ErrorModel Tests
        // =====================================================================

        /// <summary>
        /// Verifies that the default (parameterless) constructor leaves all
        /// properties as null.
        /// </summary>
        [Fact]
        public void ErrorModel_DefaultConstructor_PropertiesAreNull()
        {
            // Arrange & Act
            var model = new ErrorModel();

            // Assert
            model.Key.Should().BeNull();
            model.Value.Should().BeNull();
            model.Message.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the parameterized constructor sets Key, Value, and Message
        /// to the provided arguments.
        /// </summary>
        [Fact]
        public void ErrorModel_ParameterizedConstructor_SetsAllProperties()
        {
            // Arrange & Act
            var model = new ErrorModel("field_name", "bad_value", "Validation failed");

            // Assert
            model.Key.Should().Be("field_name");
            model.Value.Should().Be("bad_value");
            model.Message.Should().Be("Validation failed");
        }

        /// <summary>
        /// Verifies that the Key property can be set and read back.
        /// </summary>
        [Fact]
        public void ErrorModel_Key_SetAndGet()
        {
            // Arrange
            var model = new ErrorModel();

            // Act
            model.Key = "error_key";

            // Assert
            model.Key.Should().Be("error_key");
        }

        /// <summary>
        /// Verifies that the Value property can be set and read back.
        /// </summary>
        [Fact]
        public void ErrorModel_Value_SetAndGet()
        {
            // Arrange
            var model = new ErrorModel();

            // Act
            model.Value = "error_value";

            // Assert
            model.Value.Should().Be("error_value");
        }

        /// <summary>
        /// Verifies that the Message property can be set and read back.
        /// </summary>
        [Fact]
        public void ErrorModel_Message_SetAndGet()
        {
            // Arrange
            var model = new ErrorModel();

            // Act
            model.Message = "Something went wrong";

            // Assert
            model.Message.Should().Be("Something went wrong");
        }

        /// <summary>
        /// Verifies that ErrorModel serializes with the correct JSON property names:
        /// "key", "value", "message" as defined by [JsonProperty] attributes.
        /// </summary>
        [Fact]
        public void ErrorModel_Serialize_UsesCorrectPropertyNames()
        {
            // Arrange
            var model = new ErrorModel("k", "v", "m");

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("key").Should().BeTrue();
            jObj.ContainsKey("value").Should().BeTrue();
            jObj.ContainsKey("message").Should().BeTrue();
            jObj["key"].ToString().Should().Be("k");
            jObj["value"].ToString().Should().Be("v");
            jObj["message"].ToString().Should().Be("m");
        }

        /// <summary>
        /// Verifies that deserializing JSON with key/value/message correctly
        /// populates an ErrorModel instance.
        /// </summary>
        [Fact]
        public void ErrorModel_Deserialize_SetsProperties()
        {
            // Arrange
            var json = @"{ ""key"": ""email"", ""value"": ""bad@"", ""message"": ""Invalid email"" }";

            // Act
            var model = JsonConvert.DeserializeObject<ErrorModel>(json);

            // Assert
            model.Should().NotBeNull();
            model.Key.Should().Be("email");
            model.Value.Should().Be("bad@");
            model.Message.Should().Be("Invalid email");
        }

        /// <summary>
        /// Verifies that ErrorModel survives a full serialize → deserialize roundtrip
        /// with all properties preserved.
        /// </summary>
        [Fact]
        public void ErrorModel_Serialize_Roundtrip()
        {
            // Arrange
            var original = new ErrorModel("field", "val", "err msg");

            // Act
            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<ErrorModel>(json);

            // Assert
            deserialized.Should().NotBeNull();
            deserialized.Key.Should().Be(original.Key);
            deserialized.Value.Should().Be(original.Value);
            deserialized.Message.Should().Be(original.Message);
        }

        // =====================================================================
        // AccessWarningModel Tests
        // =====================================================================

        /// <summary>
        /// Verifies that all AccessWarningModel properties default to null.
        /// </summary>
        [Fact]
        public void AccessWarningModel_DefaultProperties_AreNull()
        {
            // Arrange & Act
            var model = new AccessWarningModel();

            // Assert
            model.Key.Should().BeNull();
            model.Code.Should().BeNull();
            model.Message.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the Key property can be set and read back.
        /// </summary>
        [Fact]
        public void AccessWarningModel_Key_SetAndGet()
        {
            // Arrange
            var model = new AccessWarningModel();

            // Act
            model.Key = "warning_key";

            // Assert
            model.Key.Should().Be("warning_key");
        }

        /// <summary>
        /// Verifies that the Code property can be set and read back.
        /// </summary>
        [Fact]
        public void AccessWarningModel_Code_SetAndGet()
        {
            // Arrange
            var model = new AccessWarningModel();

            // Act
            model.Code = "W001";

            // Assert
            model.Code.Should().Be("W001");
        }

        /// <summary>
        /// Verifies that the Message property can be set and read back.
        /// </summary>
        [Fact]
        public void AccessWarningModel_Message_SetAndGet()
        {
            // Arrange
            var model = new AccessWarningModel();

            // Act
            model.Message = "Access restricted";

            // Assert
            model.Message.Should().Be("Access restricted");
        }

        /// <summary>
        /// Verifies that AccessWarningModel serializes with the correct JSON property names:
        /// "key", "code", "message" as defined by [JsonProperty] attributes.
        /// </summary>
        [Fact]
        public void AccessWarningModel_Serialize_UsesCorrectPropertyNames()
        {
            // Arrange
            var model = new AccessWarningModel
            {
                Key = "access_key",
                Code = "403",
                Message = "Insufficient permissions"
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert
            jObj.ContainsKey("key").Should().BeTrue();
            jObj.ContainsKey("code").Should().BeTrue();
            jObj.ContainsKey("message").Should().BeTrue();
            jObj["key"].ToString().Should().Be("access_key");
            jObj["code"].ToString().Should().Be("403");
            jObj["message"].ToString().Should().Be("Insufficient permissions");
        }

        /// <summary>
        /// Verifies that deserializing JSON with key/code/message correctly
        /// populates an AccessWarningModel instance.
        /// </summary>
        [Fact]
        public void AccessWarningModel_Deserialize_SetsProperties()
        {
            // Arrange
            var json = @"{ ""key"": ""resource"", ""code"": ""401"", ""message"": ""Not authorized"" }";

            // Act
            var model = JsonConvert.DeserializeObject<AccessWarningModel>(json);

            // Assert
            model.Should().NotBeNull();
            model.Key.Should().Be("resource");
            model.Code.Should().Be("401");
            model.Message.Should().Be("Not authorized");
        }

        /// <summary>
        /// Verifies that AccessWarningModel survives a full serialize → deserialize roundtrip
        /// with all properties preserved.
        /// </summary>
        [Fact]
        public void AccessWarningModel_Serialize_Roundtrip()
        {
            // Arrange
            var original = new AccessWarningModel
            {
                Key = "perm",
                Code = "W403",
                Message = "Limited access"
            };

            // Act
            var json = JsonConvert.SerializeObject(original);
            var deserialized = JsonConvert.DeserializeObject<AccessWarningModel>(json);

            // Assert
            deserialized.Should().NotBeNull();
            deserialized.Key.Should().Be(original.Key);
            deserialized.Code.Should().Be(original.Code);
            deserialized.Message.Should().Be(original.Message);
        }

        // =====================================================================
        // Response Envelope Structure Tests (End-to-End)
        // =====================================================================

        /// <summary>
        /// Verifies the full success response envelope shape:
        /// { "timestamp":..., "success":true, "message":..., "hash":..., "errors":[], "accessWarnings":[], "object":... }
        /// This validates the complete API contract per AAP 0.8.1.
        /// </summary>
        [Fact]
        public void ResponseEnvelope_FullSuccess_Shape()
        {
            // Arrange
            var model = new ResponseModel
            {
                Timestamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
                Success = true,
                Message = "Entity created",
                Hash = "sha256hash",
                Object = new { Id = 1, Name = "Record" }
            };

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert — verify full envelope shape
            jObj.ContainsKey("timestamp").Should().BeTrue();
            jObj.ContainsKey("success").Should().BeTrue();
            jObj.ContainsKey("message").Should().BeTrue();
            jObj.ContainsKey("hash").Should().BeTrue();
            jObj.ContainsKey("errors").Should().BeTrue();
            jObj.ContainsKey("accessWarnings").Should().BeTrue();
            jObj.ContainsKey("object").Should().BeTrue();

            // Verify values
            ((bool)jObj["success"]).Should().BeTrue();
            jObj["message"].ToString().Should().Be("Entity created");
            jObj["hash"].ToString().Should().Be("sha256hash");
            jObj["errors"].Type.Should().Be(JTokenType.Array);
            ((JArray)jObj["errors"]).Count.Should().Be(0);
            jObj["accessWarnings"].Type.Should().Be(JTokenType.Array);
            ((JArray)jObj["accessWarnings"]).Count.Should().Be(0);
            jObj["object"].Should().NotBeNull();
            jObj["object"]["Id"].ToString().Should().Be("1");
            jObj["object"]["Name"].ToString().Should().Be("Record");
        }

        /// <summary>
        /// Verifies the error response envelope shape with populated errors list.
        /// The API contract dictates that error responses include the errors array
        /// with structured ErrorModel entries.
        /// </summary>
        [Fact]
        public void ResponseEnvelope_Error_Shape()
        {
            // Arrange
            var model = new ResponseModel
            {
                Timestamp = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc),
                Success = false,
                Message = "Validation failed",
                Hash = null,
                Object = null
            };
            model.Errors.Add(new ErrorModel("name", "", "Name is required"));
            model.Errors.Add(new ErrorModel("email", "invalid", "Invalid email format"));
            model.AccessWarnings.Add(new AccessWarningModel
            {
                Key = "admin_panel",
                Code = "403",
                Message = "Admin access required"
            });

            // Act
            var json = JsonConvert.SerializeObject(model);
            var jObj = JObject.Parse(json);

            // Assert — verify error envelope shape
            ((bool)jObj["success"]).Should().BeFalse();
            jObj["message"].ToString().Should().Be("Validation failed");
            jObj["hash"].Type.Should().Be(JTokenType.Null);
            jObj["object"].Type.Should().Be(JTokenType.Null);

            // Verify errors array
            var errorsArray = (JArray)jObj["errors"];
            errorsArray.Count.Should().Be(2);
            errorsArray[0]["key"].ToString().Should().Be("name");
            errorsArray[0]["message"].ToString().Should().Be("Name is required");
            errorsArray[1]["key"].ToString().Should().Be("email");
            errorsArray[1]["value"].ToString().Should().Be("invalid");

            // Verify access warnings array
            var warningsArray = (JArray)jObj["accessWarnings"];
            warningsArray.Count.Should().Be(1);
            warningsArray[0]["key"].ToString().Should().Be("admin_panel");
            warningsArray[0]["code"].ToString().Should().Be("403");
            warningsArray[0]["message"].ToString().Should().Be("Admin access required");
        }

        /// <summary>
        /// Verifies that StatusCode is completely absent from the serialized JSON
        /// of a fully populated ResponseModel, confirming [JsonIgnore] behavior
        /// end-to-end in the response envelope.
        /// </summary>
        [Fact]
        public void ResponseEnvelope_StatusCodeNotInJson()
        {
            // Arrange
            var model = new ResponseModel
            {
                Timestamp = DateTime.UtcNow,
                Success = true,
                Message = "OK",
                Hash = "h",
                StatusCode = HttpStatusCode.InternalServerError,
                Object = "data"
            };
            model.Errors.Add(new ErrorModel("e", "v", "m"));
            model.AccessWarnings.Add(new AccessWarningModel { Key = "w", Code = "c", Message = "m" });

            // Act
            var json = JsonConvert.SerializeObject(model);

            // Assert — StatusCode must not appear in any form
            json.Should().NotContain("\"StatusCode\"");
            json.Should().NotContain("\"statusCode\"");
            json.Should().NotContain("\"status_code\"");

            // Additionally verify via JObject
            var jObj = JObject.Parse(json);
            jObj.ContainsKey("StatusCode").Should().BeFalse();
            jObj.ContainsKey("statusCode").Should().BeFalse();
            jObj.ContainsKey("status_code").Should().BeFalse();
        }
    }
}
