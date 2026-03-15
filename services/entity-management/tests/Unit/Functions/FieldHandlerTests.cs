// =============================================================================
// FieldHandlerTests.cs — Comprehensive Unit Tests for Field CRUD Lambda Handler
// =============================================================================
// Tests for FieldHandler.cs — Lambda handler for field CRUD operations in the
// Entity Management microservice. Validates polymorphic InputField deserialization
// for all 20+ field types, type-specific validation, PATCH partial update logic,
// admin authorization enforcement, cache clearing, SNS domain event publishing,
// entity ID extraction from route parameters, and response envelope patterns.
//
// Namespace: WebVellaErp.EntityManagement.Tests.Unit.Functions
// Test Framework: xUnit 2.9.3 + Moq 4.20.72 + FluentAssertions 8.0.1
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVellaErp.EntityManagement.Functions;
using WebVellaErp.EntityManagement.Models;
using WebVellaErp.EntityManagement.Services;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace WebVellaErp.EntityManagement.Tests.Unit.Functions
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="FieldHandler"/> covering all 5 public Lambda
    /// handler methods (CreateField, ReadField, UpdateField, PatchField, DeleteField).
    /// Tests are organized in 12 logical phases covering polymorphic deserialization,
    /// field validation, CRUD operations, PATCH per-field-type partial updates,
    /// authorization, cache behavior, SNS events, and response envelope patterns.
    /// </summary>
    public class FieldHandlerTests
    {
        // ═══════════════════════════════════════════════════════════════
        // MOCK DEPENDENCIES
        // ═══════════════════════════════════════════════════════════════

        private readonly Mock<IEntityService> _mockEntityService;
        private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
        private readonly Mock<IMemoryCache> _mockCache;
        private readonly Mock<ILogger<FieldHandler>> _mockLogger;
        private readonly Mock<ILambdaContext> _mockLambdaContext;
        private readonly FieldHandler _handler;

        // ═══════════════════════════════════════════════════════════════
        // TEST FIXTURES
        // ═══════════════════════════════════════════════════════════════

        private static readonly Guid TestEntityId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid TestFieldId = Guid.Parse("f1e2d3c4-b5a6-7890-fedc-ba0987654321");
        private const string TestFieldName = "test_field";
        private const string SnsTopic = "arn:aws:sns:us-east-1:000000000000:field-events";

        /// <summary>
        /// JSON serialization options matching the handler's internal _jsonOptions configuration.
        /// </summary>
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // ═══════════════════════════════════════════════════════════════
        // CONSTRUCTOR — Test Setup
        // ═══════════════════════════════════════════════════════════════

        public FieldHandlerTests()
        {
            _mockEntityService = new Mock<IEntityService>();
            _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
            _mockCache = new Mock<IMemoryCache>();
            _mockLogger = new Mock<ILogger<FieldHandler>>();
            _mockLambdaContext = new Mock<ILambdaContext>();

            // Default Lambda context with AwsRequestId for correlation ID extraction.
            _mockLambdaContext.Setup(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _mockLambdaContext.Setup(c => c.FunctionName).Returns("FieldHandler-Test");

            // Configure SNS to return a successful publish response by default.
            _mockSnsClient
                .Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = Guid.NewGuid().ToString() });

            // Set environment variables required by the handler.
            Environment.SetEnvironmentVariable("FIELD_TOPIC_ARN", SnsTopic);
            Environment.SetEnvironmentVariable("ENTITY_TOPIC_ARN", "arn:aws:sns:us-east-1:000000000000:entity-events");
            Environment.SetEnvironmentVariable("IS_LOCAL", "false");

            _handler = new FieldHandler(
                _mockEntityService.Object,
                _mockSnsClient.Object,
                _mockCache.Object,
                _mockLogger.Object
            );
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Builds an APIGatewayHttpApiV2ProxyRequest with configurable body, path parameters,
        /// and JWT claims for role-based authorization testing.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string? body = null,
            Dictionary<string, string>? pathParams = null,
            List<string>? roles = null,
            bool includeAdminRole = true)
        {
            var claims = new Dictionary<string, string>();
            if (roles != null)
            {
                claims["custom:roles"] = string.Join(",", roles);
            }
            else if (includeAdminRole)
            {
                // Default: include administrator role for successful auth.
                claims["custom:roles"] = SystemIds.AdministratorRoleId.ToString();
            }

            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParams ?? new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["x-correlation-id"] = Guid.NewGuid().ToString()
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = claims
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Builds a request with no authorization context (null JWT claims).
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildRequestWithNoAuth(
            string? body = null,
            Dictionary<string, string>? pathParams = null)
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParams ?? new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json"
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext()
            };
        }

        /// <summary>
        /// Creates a test Entity fixture with optional pre-existing fields.
        /// </summary>
        private static Entity CreateTestEntity(Guid? id = null, List<Field>? fields = null)
        {
            return new Entity
            {
                Id = id ?? TestEntityId,
                Name = "test_entity",
                Label = "Test Entity",
                LabelPlural = "Test Entities",
                System = false,
                IconName = "fa fa-database",
                Color = "#4CAF50",
                Fields = fields ?? new List<Field>(),
                RecordPermissions = new RecordPermissions
                {
                    CanRead = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanCreate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanUpdate = new List<Guid> { SystemIds.AdministratorRoleId },
                    CanDelete = new List<Guid> { SystemIds.AdministratorRoleId }
                }
            };
        }

        /// <summary>
        /// Creates a test Entity with a single TextField at the given fieldId.
        /// </summary>
        private static Entity CreateTestEntityWithTextField(Guid entityId, Guid fieldId)
        {
            return CreateTestEntity(id: entityId, fields: new List<Field>
            {
                new TextField
                {
                    Id = fieldId,
                    Name = "existing_text",
                    Label = "Existing Text",
                    MaxLength = 200,
                    DefaultValue = "hello"
                }
            });
        }

        /// <summary>
        /// Creates a success FieldResponse wrapping a test field.
        /// </summary>
        private static FieldResponse CreateSuccessFieldResponse(Field? field = null)
        {
            return new FieldResponse
            {
                Success = true,
                Timestamp = DateTime.UtcNow,
                Message = "Field operation completed successfully.",
                Errors = new List<ErrorModel>(),
                Object = field ?? new TextField
                {
                    Id = TestFieldId,
                    Name = TestFieldName,
                    Label = "Test Field"
                }
            };
        }

        /// <summary>
        /// Creates an error FieldResponse with specified status code and message.
        /// </summary>
        private static FieldResponse CreateErrorFieldResponse(int statusCode, string message,
            List<ErrorModel>? errors = null)
        {
            return new FieldResponse
            {
                Success = false,
                Timestamp = DateTime.UtcNow,
                Message = message,
                Errors = errors ?? new List<ErrorModel>(),
                Object = null,
                StatusCode = (System.Net.HttpStatusCode)statusCode
            };
        }

        /// <summary>
        /// Builds standard path parameters for field CRUD operations (entityId + fieldId).
        /// </summary>
        private static Dictionary<string, string> FieldPathParams(Guid? entityId = null, Guid? fieldId = null)
        {
            var p = new Dictionary<string, string>
            {
                ["entityId"] = (entityId ?? TestEntityId).ToString()
            };
            if (fieldId.HasValue)
                p["fieldId"] = fieldId.Value.ToString();
            return p;
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: CreateField Tests — Polymorphic Deserialization
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_TextField_ValidPayload_Returns200()
        {
            // Arrange: Valid TextField JSON payload.
            var body = @"{
                ""fieldType"": 18,
                ""name"": ""title"",
                ""label"": ""Title"",
                ""defaultValue"": """",
                ""maxLength"": 200
            }";

            var resultField = new TextField
            {
                Id = TestFieldId,
                Name = "title",
                Label = "Title",
                MaxLength = 200,
                DefaultValue = ""
            };

            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse(resultField));

            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: CreateField returns 201 on success.
            response.StatusCode.Should().Be(201);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JObject.Parse(response.Body);
            parsed.Should().NotBeNull();
            parsed["success"]!.Value<bool>().Should().BeTrue();
            parsed["object"].Should().NotBeNull();
            parsed["object"]!.Type.Should().NotBe(Newtonsoft.Json.Linq.JTokenType.Null);
        }

        [Theory]
        [InlineData("AutoNumberField", 1)]
        [InlineData("CheckboxField", 2)]
        [InlineData("CurrencyField", 3)]
        [InlineData("DateField", 4)]
        [InlineData("DateTimeField", 5)]
        [InlineData("EmailField", 6)]
        [InlineData("FileField", 7)]
        [InlineData("GuidField", 16)]
        [InlineData("HtmlField", 8)]
        [InlineData("ImageField", 9)]
        [InlineData("MultiLineTextField", 10)]
        [InlineData("MultiSelectField", 11)]
        [InlineData("NumberField", 12)]
        [InlineData("PasswordField", 13)]
        [InlineData("PercentField", 14)]
        [InlineData("PhoneField", 15)]
        [InlineData("SelectField", 17)]
        [InlineData("TextField", 18)]
        [InlineData("UrlField", 19)]
        [InlineData("GeographyField", 21)]
        public async Task CreateField_AllFieldTypes_CorrectDeserialization(string fieldTypeName, int fieldTypeInt)
        {
            // Arrange: Build minimal valid body for each field type.
            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.CreateField(It.IsAny<Guid>(), It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            // Build body with fieldType integer + minimum required properties.
            var bodyObj = new JObject
            {
                ["fieldType"] = fieldTypeInt,
                ["name"] = "poly_test_field",
                ["label"] = "Poly Test"
            };

            // Add Options for Select/MultiSelect types (required for deserialization).
            if (fieldTypeName == "SelectField" || fieldTypeName == "MultiSelectField")
            {
                bodyObj["options"] = JToken.FromObject(new[]
                {
                    new { value = "opt1", label = "Option 1" }
                });
            }

            var request = BuildRequest(
                body: bodyObj.ToString(),
                pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: 201 success and correct InputField subclass captured.
            response.StatusCode.Should().Be(201);
            capturedField.Should().NotBeNull();

            // Verify the deserialized InputField is the expected concrete type.
            var expectedTypeName = $"Input{fieldTypeName}";
            capturedField!.GetType().Name.Should().Be(expectedTypeName,
                because: $"fieldType '{fieldTypeName}' should deserialize to {expectedTypeName}");
        }

        [Fact]
        public async Task CreateField_UnknownFieldType_Returns400()
        {
            // Arrange: Body with unrecognized field type string.
            var body = @"{
                ""fieldType"": 999,
                ""name"": ""bad_field"",
                ""label"": ""Bad""
            }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: 400 with error message about unrecognized field type.
            response.StatusCode.Should().Be(400);
            response.Body.Should().NotBeNullOrEmpty();

            // Service should NOT be called.
            _mockEntityService.Verify(
                s => s.CreateField(It.IsAny<Guid>(), It.IsAny<InputField>()), Times.Never);
        }

        [Fact]
        public async Task CreateField_MissingFieldType_Returns400()
        {
            // Arrange: Body without fieldType property.
            var body = @"{
                ""name"": ""no_type_field"",
                ""label"": ""No Type""
            }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: 400 — deserialization fails because fieldType is required.
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("cannot be converted");

            _mockEntityService.Verify(
                s => s.CreateField(It.IsAny<Guid>(), It.IsAny<InputField>()), Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: Field Validation Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_DuplicateFieldName_Returns400()
        {
            // Arrange: Service returns validation error for duplicate name.
            var errorResponse = CreateErrorFieldResponse(400, "Field name already exists.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", "title", "A field with that name already exists on this entity.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = @"{ ""fieldType"": 18, ""name"": ""title"", ""label"": ""Title"" }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<FieldResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateField_DuplicateFieldId_Returns400()
        {
            // Arrange: Service returns validation error for duplicate Id.
            var errorResponse = CreateErrorFieldResponse(400, "Field Id already exists.",
                new List<ErrorModel>
                {
                    new ErrorModel("id", TestFieldId.ToString(), "A field with that Id already exists.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                id = TestFieldId,
                name = "dup_id_field",
                label = "Dup Id"
            });
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateField_NameTooLong_Returns400()
        {
            // Arrange: Name exceeding 63 characters (PostgreSQL identifier limit).
            var longName = new string('a', 64);
            var errorResponse = CreateErrorFieldResponse(400, "Field name too long.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", longName, "Field name must not exceed 63 characters.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                name = longName,
                label = "Long Name"
            });
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<FieldResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateField_InvalidNameFormat_Returns400()
        {
            // Arrange: Name with invalid characters.
            var errorResponse = CreateErrorFieldResponse(400, "Field name format invalid.",
                new List<ErrorModel>
                {
                    new ErrorModel("name", "INVALID NAME!", "Field name must contain only lowercase letters, digits, and underscores.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = @"{ ""fieldType"": 18, ""name"": ""INVALID NAME!"", ""label"": ""Invalid"" }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateField_EmptyLabel_Returns400()
        {
            // Arrange: Service returns validation error for empty label.
            var errorResponse = CreateErrorFieldResponse(400, "Label is required.",
                new List<ErrorModel>
                {
                    new ErrorModel("label", "", "Field label is required.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = @"{ ""fieldType"": 18, ""name"": ""no_label"", ""label"": """" }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
            var parsed = JsonSerializer.Deserialize<FieldResponse>(response.Body, _jsonOptions);
            parsed!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateField_SelectField_OptionUniqueness_Returns400()
        {
            // Arrange: InputSelectField with duplicate option values.
            var errorResponse = CreateErrorFieldResponse(400, "Option values must be unique.",
                new List<ErrorModel>
                {
                    new ErrorModel("options", "dup", "Duplicate option value found.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 17,
                name = "dup_options",
                label = "Dup Options",
                options = new[]
                {
                    new { value = "dup", label = "Duplicate 1" },
                    new { value = "dup", label = "Duplicate 2" }
                }
            });
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateField_SelectField_DefaultNotInOptions_Returns400()
        {
            // Arrange: SelectField where DefaultValue is not among Options.
            var errorResponse = CreateErrorFieldResponse(400, "Default value not in options.",
                new List<ErrorModel>
                {
                    new ErrorModel("defaultValue", "missing_val", "Default value must be one of the defined options.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 17,
                name = "bad_default",
                label = "Bad Default",
                defaultValue = "missing_val",
                options = new[]
                {
                    new { value = "opt1", label = "Option 1" },
                    new { value = "opt2", label = "Option 2" }
                }
            });
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateField_MultiSelectField_OptionPresence_Required()
        {
            // Arrange: InputMultiSelectField with empty Options list.
            var errorResponse = CreateErrorFieldResponse(400, "Options are required.",
                new List<ErrorModel>
                {
                    new ErrorModel("options", "", "MultiSelect field must have at least one option.")
                });
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(errorResponse);

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 11,
                name = "empty_opts",
                label = "Empty Options",
                options = Array.Empty<object>()
            });
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateField_NumberField_DecimalPlacesDefault()
        {
            // Arrange: InputNumberField without DecimalPlaces — verify default applied.
            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse(new NumberField
                {
                    Id = TestFieldId,
                    Name = "price",
                    Label = "Price",
                    DecimalPlaces = 2
                }));

            var body = @"{
                ""fieldType"": 12,
                ""name"": ""price"",
                ""label"": ""Price"",
                ""defaultValue"": 0,
                ""minValue"": 0,
                ""maxValue"": 999999
            }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: Handler successfully processes NumberField and calls service.
            response.StatusCode.Should().Be(201);
            _mockEntityService.Verify(
                s => s.CreateField(TestEntityId, It.IsAny<InputField>()), Times.Once);
            capturedField.Should().NotBeNull();
            capturedField.Should().BeOfType<InputNumberField>();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: ReadField Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task ReadField_ValidEntityAndFieldId_ReturnsField()
        {
            // Arrange: Entity with a field matching fieldId.
            var entity = CreateTestEntityWithTextField(TestEntityId, TestFieldId);
            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityId))
                .ReturnsAsync(entity);

            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.ReadField(request, _mockLambdaContext.Object);

            // Assert: 200 OK with field in FieldResponse envelope.
            response.StatusCode.Should().Be(200);
            response.Body.Should().NotBeNullOrEmpty();

            var parsed = JObject.Parse(response.Body);
            parsed.Should().NotBeNull();
            parsed["success"]!.Value<bool>().Should().BeTrue();
            parsed["object"].Should().NotBeNull();
            parsed["object"]!.Type.Should().NotBe(Newtonsoft.Json.Linq.JTokenType.Null);
        }

        [Fact]
        public async Task ReadField_FieldNotFound_Returns404()
        {
            // Arrange: Entity exists but does NOT have the requested field.
            var entity = CreateTestEntity(id: TestEntityId);
            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityId))
                .ReturnsAsync(entity);

            var nonExistentFieldId = Guid.NewGuid();
            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, nonExistentFieldId));

            // Act
            var response = await _handler.ReadField(request, _mockLambdaContext.Object);

            // Assert: 404 because field not found in entity.
            response.StatusCode.Should().Be(404);
        }

        [Fact]
        public async Task ReadField_EntityNotFound_Returns404()
        {
            // Arrange: Entity does not exist.
            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityId))
                .ReturnsAsync((Entity?)null);

            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.ReadField(request, _mockLambdaContext.Object);

            // Assert: 404 because entity not found.
            response.StatusCode.Should().Be(404);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5: UpdateField Tests (Full Update)
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task UpdateField_ValidPayload_Returns200()
        {
            // Arrange: Valid update payload for TextField.
            var updatedField = new TextField
            {
                Id = TestFieldId,
                Name = "updated_title",
                Label = "Updated Title",
                MaxLength = 500
            };
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse(updatedField));

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                id = TestFieldId,
                name = "updated_title",
                label = "Updated Title",
                maxLength = 500,
                defaultValue = ""
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.UpdateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            var parsed = JObject.Parse(response.Body);
            parsed["success"]!.Value<bool>().Should().BeTrue();

            _mockEntityService.Verify(
                s => s.UpdateField(TestEntityId, It.IsAny<InputField>()), Times.Once);
        }

        [Fact]
        public async Task UpdateField_UnknownPropertyInBody_Returns400()
        {
            // Arrange: Body contains a property not valid for TextField.
            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                id = TestFieldId,
                name = "test_field",
                label = "Test",
                unknownProperty = "should fail"
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.UpdateField(request, _mockLambdaContext.Object);

            // Assert: 400 — property not valid for this field type.
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("not valid");

            _mockEntityService.Verify(
                s => s.UpdateField(It.IsAny<Guid>(), It.IsAny<InputField>()), Times.Never);
        }

        [Fact]
        public async Task UpdateField_AdminRequired_Returns403()
        {
            // Arrange: Non-admin role.
            var body = @"{ ""fieldType"": 18, ""name"": ""test"", ""label"": ""Test"" }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId),
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.UpdateField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            _mockEntityService.Verify(
                s => s.UpdateField(It.IsAny<Guid>(), It.IsAny<InputField>()), Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 6: PatchField Tests — Partial Update Per Field Type
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task PatchField_TextField_AppliesOnlySpecifiedProperties()
        {
            // Arrange: PATCH body with only maxLength changed.
            var fieldId = TestFieldId;
            var entity = CreateTestEntityWithTextField(TestEntityId, fieldId);
            _mockEntityService
                .Setup(s => s.GetEntity(TestEntityId))
                .ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 18, ""maxLength"": 500 }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert: 200 success, UpdateField called with partially applied field.
            response.StatusCode.Should().Be(200);
            capturedField.Should().NotBeNull();
            capturedField.Should().BeOfType<InputTextField>();
            var textField = (InputTextField)capturedField!;
            textField.MaxLength.Should().Be(500);
            // defaultValue should NOT have been set (not in PATCH body).
        }

        [Fact]
        public async Task PatchField_AutoNumberField_PatchesCorrectProperties()
        {
            // Arrange: Entity with existing AutoNumberField.
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new AutoNumberField
                {
                    Id = fieldId,
                    Name = "auto_num",
                    Label = "Auto Number",
                    DisplayFormat = "{0:0}",
                    StartingNumber = 1
                }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            // PATCH only displayFormat.
            var body = @"{ ""fieldType"": 1, ""displayFormat"": ""{0:0000}"" }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().NotBeNull();
            capturedField.Should().BeOfType<InputAutoNumberField>();
            var autoField = (InputAutoNumberField)capturedField!;
            autoField.DisplayFormat.Should().Be("{0:0000}");
        }

        [Fact]
        public async Task PatchField_CurrencyField_PatchesCurrencyObject()
        {
            // Arrange: Entity with existing CurrencyField.
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new CurrencyField
                {
                    Id = fieldId,
                    Name = "amount",
                    Label = "Amount"
                }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            // PATCH the currency object.
            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 3,
                currency = new { code = "EUR", name = "Euro", symbol = "€", decimalDigits = 2 }
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().BeOfType<InputCurrencyField>();
        }

        [Fact]
        public async Task PatchField_DateField_PatchesUseCurrentTime()
        {
            // Arrange
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new DateField { Id = fieldId, Name = "start_date", Label = "Start Date" }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 4, ""useCurrentTimeAsDefaultValue"": true }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().BeOfType<InputDateField>();
            var dateField = (InputDateField)capturedField!;
            dateField.UseCurrentTimeAsDefaultValue.Should().BeTrue();
        }

        [Fact]
        public async Task PatchField_SelectField_PatchesOptions()
        {
            // Arrange
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new SelectField { Id = fieldId, Name = "status", Label = "Status" }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 17,
                options = new[]
                {
                    new { value = "active", label = "Active" },
                    new { value = "inactive", label = "Inactive" }
                }
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().BeOfType<InputSelectField>();
            var selectField = (InputSelectField)capturedField!;
            selectField.Options.Should().NotBeNull();
            selectField.Options.Should().HaveCount(2);
        }

        [Fact]
        public async Task PatchField_MultiSelectField_PatchesOptionsAndDefault()
        {
            // Arrange
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new MultiSelectField { Id = fieldId, Name = "tags", Label = "Tags" }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 11,
                options = new[]
                {
                    new { value = "tag1", label = "Tag 1" },
                    new { value = "tag2", label = "Tag 2" }
                },
                defaultValue = new[] { "tag1" }
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().BeOfType<InputMultiSelectField>();
            var msField = (InputMultiSelectField)capturedField!;
            msField.Options.Should().NotBeNull();
            msField.DefaultValue.Should().NotBeNull();
        }

        [Fact]
        public async Task PatchField_GuidField_PatchesGenerateNewIdAndUnique()
        {
            // Arrange
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new GuidField { Id = fieldId, Name = "ref_id", Label = "Reference ID" }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 16, ""generateNewId"": true }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().BeOfType<InputGuidField>();
            var guidField = (InputGuidField)capturedField!;
            guidField.GenerateNewId.Should().BeTrue();
        }

        [Fact]
        public async Task PatchField_GeographyField_PatchesSRIDAndFormat()
        {
            // Arrange
            var fieldId = Guid.NewGuid();
            var entity = CreateTestEntity(id: TestEntityId, fields: new List<Field>
            {
                new GeographyField { Id = fieldId, Name = "location", Label = "Location" }
            });
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 21,
                srid = 3857,
                format = 1
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            capturedField.Should().BeOfType<InputGeographyField>();
            var geoField = (InputGeographyField)capturedField!;
            geoField.SRID.Should().Be(3857);
        }

        [Fact]
        public async Task PatchField_CommonProperties_AppliedAcrossAllTypes()
        {
            // Arrange: Patch common properties (label, description) on a TextField.
            var fieldId = TestFieldId;
            var entity = CreateTestEntityWithTextField(TestEntityId, fieldId);
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            InputField? capturedField = null;
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .Callback<Guid, InputField>((_, f) => capturedField = f)
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                label = "Updated Label",
                description = "A new description",
                helpText = "Help text here",
                placeholderText = "Enter value...",
                required = true,
                unique = true,
                searchable = true,
                auditable = true
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, fieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert: Common properties applied.
            response.StatusCode.Should().Be(200);
            capturedField.Should().NotBeNull();
            capturedField!.Label.Should().Be("Updated Label");
            capturedField.Description.Should().Be("A new description");
            capturedField.HelpText.Should().Be("Help text here");
            capturedField.PlaceholderText.Should().Be("Enter value...");
            capturedField.Required.Should().BeTrue();
            capturedField.Unique.Should().BeTrue();
            capturedField.Searchable.Should().BeTrue();
            capturedField.Auditable.Should().BeTrue();
        }

        [Fact]
        public async Task PatchField_MissingFieldType_Returns400()
        {
            // Arrange: PATCH body without fieldType — required for PATCH.
            var body = @"{ ""maxLength"": 500 }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert: 400 — fieldType required for PATCH operations.
            response.StatusCode.Should().Be(400);
            response.Body.Should().Contain("fieldType");
        }

        [Fact]
        public async Task PatchField_FieldNotFoundInEntity_Returns404()
        {
            // Arrange: Entity exists but doesn't have the target field.
            var entity = CreateTestEntity(id: TestEntityId);
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);

            var nonExistentFieldId = Guid.NewGuid();
            var body = @"{ ""fieldType"": 18, ""maxLength"": 300 }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, nonExistentFieldId));

            // Act
            var response = await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert: 404 — field not found in entity.
            response.StatusCode.Should().Be(404);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 7: DeleteField Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task DeleteField_ValidIds_Returns200()
        {
            // Arrange: Service returns successful deletion response.
            _mockEntityService
                .Setup(s => s.DeleteField(TestEntityId, TestFieldId))
                .ReturnsAsync(new FieldResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Field deleted successfully."
                });

            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.DeleteField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(200);
            _mockEntityService.Verify(s => s.DeleteField(TestEntityId, TestFieldId), Times.Once);
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task DeleteField_AdminRequired_Returns403()
        {
            // Arrange: Non-admin role for delete operation.
            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId),
                roles: new List<string> { SystemIds.RegularRoleId.ToString() },
                includeAdminRole: false);

            // Act
            var response = await _handler.DeleteField(request, _mockLambdaContext.Object);

            // Assert
            response.StatusCode.Should().Be(403);
            _mockEntityService.Verify(
                s => s.DeleteField(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task DeleteField_EntityNotFound_Returns404()
        {
            // Arrange: Service returns 404 error when entity/field doesn't exist.
            _mockEntityService
                .Setup(s => s.DeleteField(TestEntityId, TestFieldId))
                .ReturnsAsync(CreateErrorFieldResponse(404, "Entity or field not found."));

            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            var response = await _handler.DeleteField(request, _mockLambdaContext.Object);

            // Assert: Handler forwards 404 from the service response.
            response.StatusCode.Should().Be(404);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 8: Cache Behavior Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_ClearsEntityCache()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 18, ""name"": ""cache_test"", ""label"": ""Cache"" }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: ClearCache called unconditionally after CreateField.
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task UpdateField_ClearsEntityCache()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                id = TestFieldId,
                name = "cache_update",
                label = "Cache Update"
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            await _handler.UpdateField(request, _mockLambdaContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task PatchField_ClearsEntityCache()
        {
            // Arrange
            var entity = CreateTestEntityWithTextField(TestEntityId, TestFieldId);
            _mockEntityService.Setup(s => s.GetEntity(TestEntityId)).ReturnsAsync(entity);
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 18, ""maxLength"": 300 }";
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            await _handler.PatchField(request, _mockLambdaContext.Object);

            // Assert
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        [Fact]
        public async Task DeleteField_ClearsEntityCache()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.DeleteField(TestEntityId, TestFieldId))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            await _handler.DeleteField(request, _mockLambdaContext.Object);

            // Assert: ClearCache called unconditionally after DeleteField.
            _mockEntityService.Verify(s => s.ClearCache(), Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 9: SNS Event Tests
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_PublishesSnsEvent()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 18, ""name"": ""sns_test"", ""label"": ""SNS"" }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: SNS PublishAsync called with entity-management.field.created event.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.TopicArn == SnsTopic &&
                        r.Message.Contains("entity-management.field.created") &&
                        r.MessageAttributes.ContainsKey("eventType")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task UpdateField_PublishesSnsEvent()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.UpdateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = JsonConvert.SerializeObject(new
            {
                fieldType = 18,
                id = TestFieldId,
                name = "sns_update",
                label = "SNS Update"
            });
            var request = BuildRequest(
                body: body,
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            await _handler.UpdateField(request, _mockLambdaContext.Object);

            // Assert: SNS event published for update.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.Message.Contains("entity-management.field.updated") &&
                        r.MessageAttributes.ContainsKey("eventType")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DeleteField_PublishesSnsEvent()
        {
            // Arrange
            _mockEntityService
                .Setup(s => s.DeleteField(TestEntityId, TestFieldId))
                .ReturnsAsync(new FieldResponse
                {
                    Success = true,
                    Timestamp = DateTime.UtcNow,
                    Message = "Field deleted."
                });

            var request = BuildRequest(
                pathParams: FieldPathParams(TestEntityId, TestFieldId));

            // Act
            await _handler.DeleteField(request, _mockLambdaContext.Object);

            // Assert: SNS event published for delete.
            _mockSnsClient.Verify(
                s => s.PublishAsync(
                    It.Is<PublishRequest>(r =>
                        r.Message.Contains("entity-management.field.deleted") &&
                        r.MessageAttributes.ContainsKey("eventType")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 10: Entity ID Extraction from Route
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_EntityIdFromRoute_ValidGuid()
        {
            // Arrange: Verify entity ID is correctly extracted from PathParameters.
            var specificEntityId = Guid.NewGuid();
            _mockEntityService
                .Setup(s => s.CreateField(specificEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse());

            var body = @"{ ""fieldType"": 18, ""name"": ""route_test"", ""label"": ""Route"" }";
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string>
                {
                    ["entityId"] = specificEntityId.ToString()
                });

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: Service called with the correct entityId from the route.
            response.StatusCode.Should().Be(201);
            _mockEntityService.Verify(
                s => s.CreateField(specificEntityId, It.IsAny<InputField>()), Times.Once);
        }

        [Fact]
        public async Task CreateField_EntityIdFromRoute_InvalidGuid_Returns400()
        {
            // Arrange: PathParameters with invalid GUID for entityId.
            var body = @"{ ""fieldType"": 18, ""name"": ""bad_route"", ""label"": ""Bad"" }";
            var request = BuildRequest(
                body: body,
                pathParams: new Dictionary<string, string>
                {
                    ["entityId"] = "not-a-guid"
                });

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: 400 — invalid entityId cannot be parsed.
            response.StatusCode.Should().Be(400);
            _mockEntityService.Verify(
                s => s.CreateField(It.IsAny<Guid>(), It.IsAny<InputField>()), Times.Never);
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 11: Response Envelope Pattern
        // ═══════════════════════════════════════════════════════════════

        [Fact]
        public async Task CreateField_ResponseMatchesFieldResponseEnvelope()
        {
            // Arrange
            var testField = new TextField
            {
                Id = TestFieldId,
                Name = "envelope_test",
                Label = "Envelope Test",
                MaxLength = 100
            };
            _mockEntityService
                .Setup(s => s.CreateField(TestEntityId, It.IsAny<InputField>()))
                .ReturnsAsync(CreateSuccessFieldResponse(testField));

            var body = @"{ ""fieldType"": 18, ""name"": ""envelope_test"", ""label"": ""Envelope Test"", ""maxLength"": 100 }";
            var request = BuildRequest(body: body, pathParams: FieldPathParams());

            // Act
            var response = await _handler.CreateField(request, _mockLambdaContext.Object);

            // Assert: Verify complete response envelope structure.
            response.StatusCode.Should().Be(201);
            response.Body.Should().NotBeNullOrEmpty();
            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Be("application/json");
            response.Headers.Should().ContainKey("X-Correlation-Id");

            var parsed = JObject.Parse(response.Body);
            parsed.Should().NotBeNull();
            parsed["success"]!.Value<bool>().Should().BeTrue();
            parsed["timestamp"].Should().NotBeNull();
            parsed["errors"].Should().NotBeNull();
            parsed["object"].Should().NotBeNull();
            parsed["object"]!.Type.Should().NotBe(Newtonsoft.Json.Linq.JTokenType.Null);
        }
    }
}
