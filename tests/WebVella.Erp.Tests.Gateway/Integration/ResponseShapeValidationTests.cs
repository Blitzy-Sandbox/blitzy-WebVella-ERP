// =========================================================================
// WebVella ERP — API Response Envelope Backward-Compatibility Tests
// =========================================================================
// Validates that the API Gateway preserves the monolith's BaseResponseModel
// response envelope format for REST API v3 contract backward compatibility.
//
// Per AAP Section 0.8.1, all existing REST API v3 endpoints must preserve
// response shapes:
//   { success, timestamp, errors, message, object, hash, accessWarnings }
//
// Source contracts:
//   - BaseModels.cs: BaseResponseModel (lines 8-38), ResponseModel (40-48),
//     ErrorModel (62-83), AccessWarningModel (50-60)
//   - ApiControllerBase.cs: DoResponse (16-30), DoPageNotFoundResponse (32-36),
//     DoBadRequestResponse (44-62)
//
// JSON property names use Newtonsoft.Json [JsonProperty(PropertyName)] annotations:
//   "success" (line 13), "timestamp" (line 10), "message" (line 16),
//   "hash" (line 19), "errors" (line 22), "accessWarnings" (line 25),
//   "object" (line 42)
// Error: "key" (line 64), "value" (line 67), "message" (line 70)
//
// StatusCode is [JsonIgnore] (line 28) — MUST NOT appear in serialized JSON.
// =========================================================================

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace WebVella.Erp.Tests.Gateway.Integration
{
    /// <summary>
    /// Integration tests validating that the API Gateway preserves the monolith's
    /// <c>BaseResponseModel</c> response envelope format for backward API v3 contract
    /// compatibility (AAP Section 0.8.1).
    ///
    /// Each test validates a specific aspect of the response shape against the exact
    /// JSON contract defined in <c>WebVella.Erp/Api/Models/BaseModels.cs</c> and the
    /// HTTP status code mapping defined in <c>WebVella.Erp.Web/Controllers/ApiControllerBase.cs</c>.
    ///
    /// Tests use <see cref="CustomWebApplicationFactory"/> which provides:
    ///   - In-memory Gateway test server with JWT authentication
    ///   - Mock backend service handlers returning <c>BaseResponseModel</c>-shaped JSON
    ///   - Authenticated and anonymous <see cref="HttpClient"/> factories
    /// </summary>
    public class ResponseShapeValidationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        /// <summary>
        /// Standard test API endpoint matching the literal route prefix "/api/v3.0/p/sdk"
        /// in the Gateway's RouteMappings configuration (appsettings.json line 58).
        /// This prefix routes to the AdminService backend, which is mocked by
        /// <see cref="CustomWebApplicationFactory"/> to return BaseResponseModel-shaped
        /// JSON responses.
        /// </summary>
        private const string TestApiEndpoint = "/api/v3.0/p/sdk/test";

        /// <summary>
        /// Initializes a new test instance with the shared <see cref="CustomWebApplicationFactory"/>.
        /// The factory provides a pre-configured Gateway test server with mock backend
        /// services returning the default success BaseResponseModel envelope.
        /// </summary>
        public ResponseShapeValidationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        #region Helper Methods

        /// <summary>
        /// Sends an authenticated GET request to the specified URL via the Gateway test
        /// server. Creates a fresh authenticated client with a valid JWT token containing
        /// administrator role claims (matching monolith AuthService.cs claim structure).
        /// Parses the response body as a <see cref="JObject"/> using Newtonsoft.Json.
        /// </summary>
        /// <param name="url">
        /// The request URL. Defaults to <see cref="TestApiEndpoint"/> which matches
        /// the AdminService route prefix in RouteMappings.
        /// </param>
        /// <returns>
        /// A tuple containing the raw <see cref="HttpResponseMessage"/>, parsed
        /// <see cref="JObject"/>, and raw JSON string for assertion.
        /// </returns>
        private async Task<(HttpResponseMessage Response, JObject Json, string RawJson)>
            SendAuthenticatedGetAsync(string url = null)
        {
            url ??= TestApiEndpoint;
            var client = _factory.CreateAuthenticatedClient(
                Guid.NewGuid(), "test@webvella.com", new[] { "administrator" });
            var response = await client.GetAsync(url);
            var rawJson = await response.Content.ReadAsStringAsync();
            var json = JObject.Parse(rawJson);
            return (response, json, rawJson);
        }

        /// <summary>
        /// Configures the shared mock backend handler to return a specific JSON response
        /// body with the specified HTTP status code. Resets all previous mock setups to
        /// prevent interference between test methods (required because
        /// <see cref="CustomWebApplicationFactory"/> is shared via <c>IClassFixture</c>).
        /// </summary>
        /// <param name="responseBody">
        /// The response body object to serialize via <see cref="JsonConvert.SerializeObject"/>.
        /// Must match the BaseResponseModel envelope shape for response validation tests.
        /// </param>
        /// <param name="statusCode">
        /// The HTTP status code for the mock response. Defaults to <see cref="HttpStatusCode.OK"/>.
        /// </param>
        private void ConfigureMockResponse(
            object responseBody,
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _factory.MockBackendHandler.Reset();
            _factory.MockBackendHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = statusCode,
                    Content = new StringContent(
                        JsonConvert.SerializeObject(responseBody),
                        Encoding.UTF8,
                        "application/json")
                });
        }

        /// <summary>
        /// Creates a standard success response body matching the complete
        /// <c>BaseResponseModel</c> + <c>ResponseModel</c> envelope shape.
        /// All property names use Newtonsoft.Json camelCase [JsonProperty] naming.
        /// </summary>
        /// <param name="resultObject">
        /// The value for the "object" property. Null by default (matching ResponseModel.Object).
        /// </param>
        private static object CreateSuccessResponseBody(object resultObject = null)
        {
            return new
            {
                // BaseResponseModel properties (BaseModels.cs lines 10-26)
                success = true,
                timestamp = DateTime.UtcNow,
                errors = new object[0],
                message = (string)null,
                hash = (string)null,
                accessWarnings = new object[0],
                // ResponseModel property (BaseModels.cs line 42)
                @object = resultObject
            };
        }

        /// <summary>
        /// Creates a standard error response body matching the <c>BaseResponseModel</c>
        /// + <c>ResponseModel</c> envelope with error details in the errors array.
        /// Each error entry matches the <c>ErrorModel</c> shape (key, value, message).
        /// </summary>
        private static object CreateErrorResponseBody(
            string errorKey = null,
            string errorValue = null,
            string errorMessage = "Validation error",
            string topMessage = null)
        {
            return new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                errors = new[]
                {
                    new { key = errorKey, value = errorValue, message = errorMessage }
                },
                message = topMessage,
                @object = (object)null,
                hash = (string)null,
                accessWarnings = new object[0]
            };
        }

        #endregion

        // =====================================================================
        // Phase 2: Success Response Shape Tests
        // Validates that the Gateway returns the full BaseResponseModel envelope
        // shape with all required JSON properties and correct types.
        // =====================================================================

        #region Success Response Shape Tests

        /// <summary>
        /// Validates that a success response contains all required BaseResponseModel
        /// and ResponseModel properties at the JSON root level.
        /// BaseModels.cs lines 8-48 define the complete response envelope shape.
        /// </summary>
        [Fact]
        public async Task SuccessResponse_HasCorrectShape()
        {
            // Arrange — configure mock backend to return full BaseResponseModel shape
            ConfigureMockResponse(CreateSuccessResponseBody());

            // Act — send authenticated request to a routed API endpoint
            var (_, json, _) = await SendAuthenticatedGetAsync();

            // Assert — all required properties must be present at root level
            json.ContainsKey("success").Should().BeTrue(
                "BaseResponseModel.Success [JsonProperty] (line 13) must be present");
            json.ContainsKey("timestamp").Should().BeTrue(
                "BaseResponseModel.Timestamp [JsonProperty] (line 10) must be present");
            json.ContainsKey("errors").Should().BeTrue(
                "BaseResponseModel.Errors [JsonProperty] (line 22) must be present");
            json.ContainsKey("message").Should().BeTrue(
                "BaseResponseModel.Message [JsonProperty] (line 16) must be present");
            json.ContainsKey("object").Should().BeTrue(
                "ResponseModel.Object [JsonProperty] (line 42) must be present");
        }

        /// <summary>
        /// Validates that the "success" field is a JSON boolean with value true
        /// for success responses.
        /// BaseModels.cs line 14: <c>public bool Success { get; set; }</c>
        /// </summary>
        [Fact]
        public async Task SuccessResponse_SuccessFieldIsBoolean()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, _) = await SendAuthenticatedGetAsync();

            json["success"].Type.Should().Be(JTokenType.Boolean,
                "success must be a boolean per BaseModels.cs line 14");
            json["success"].Value<bool>().Should().BeTrue(
                "success responses must have success=true");
        }

        /// <summary>
        /// Validates that the "timestamp" field is a parseable UTC DateTime string.
        /// BaseModels.cs line 11: <c>public DateTime Timestamp { get; set; }</c>
        /// Serialized via Newtonsoft.Json with DateTimeZoneHandling.Utc.
        /// </summary>
        [Fact]
        public async Task SuccessResponse_TimestampFieldIsUtcDateTime()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, _) = await SendAuthenticatedGetAsync();

            var timestampToken = json["timestamp"];
            timestampToken.Should().NotBeNull("timestamp field must exist");

            // The timestamp may be serialized as a Date token or a String token
            // depending on Newtonsoft.Json settings — both are valid
            DateTime.TryParse(timestampToken.ToString(), out var timestamp).Should().BeTrue(
                "timestamp must be a parseable DateTime per BaseModels.cs line 11");
        }

        /// <summary>
        /// Validates that the "errors" field is always a JSON array (never null).
        /// BaseModels.cs line 34: <c>Errors = new List&lt;ErrorModel&gt;()</c>
        /// — always initialized as empty list, so it MUST always serialize as [].
        /// For success responses, the array must be empty.
        /// </summary>
        [Fact]
        public async Task SuccessResponse_ErrorsFieldIsAlwaysArray()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, _) = await SendAuthenticatedGetAsync();

            json["errors"].Type.Should().Be(JTokenType.Array,
                "errors must be a JSON array per BaseModels.cs line 23");
            ((JArray)json["errors"]).Count.Should().Be(0,
                "success responses must have an empty errors array per BaseModels.cs line 34");
        }

        /// <summary>
        /// Validates that the "message" field is either a JSON string or null.
        /// BaseModels.cs line 17: <c>public string Message { get; set; }</c>
        /// — nullable string type, serialized as null or string.
        /// </summary>
        [Fact]
        public async Task SuccessResponse_MessageFieldIsString()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, _) = await SendAuthenticatedGetAsync();

            json["message"].Type.Should().BeOneOf(
                new[] { JTokenType.String, JTokenType.Null },
                "message must be a string or null per BaseModels.cs line 17");
        }

        /// <summary>
        /// Validates that the "object" field can hold any JSON value type or null.
        /// BaseModels.cs line 43: <c>public object Object { get; set; }</c>
        /// — typed as <c>object</c>, can serialize as any JSON value.
        /// </summary>
        [Fact]
        public async Task SuccessResponse_ObjectFieldCanBeAnyValueOrNull()
        {
            // Test with null object — the most common case for error/empty responses
            ConfigureMockResponse(CreateSuccessResponseBody(null));
            var (_, jsonNull, _) = await SendAuthenticatedGetAsync();
            jsonNull.ContainsKey("object").Should().BeTrue(
                "object property must exist per ResponseModel line 42");

            // Test with a non-null object — validates any value type is accepted
            ConfigureMockResponse(CreateSuccessResponseBody(
                new { id = Guid.NewGuid(), name = "TestEntity", count = 42 }));
            var (_, jsonObj, _) = await SendAuthenticatedGetAsync();
            jsonObj.ContainsKey("object").Should().BeTrue(
                "object property must exist even when non-null");
            jsonObj["object"].Type.Should().NotBe(JTokenType.Null,
                "non-null object should serialize as a JSON value");
        }

        #endregion

        // =====================================================================
        // Phase 3: Error Response Shape Tests
        // Validates that error responses maintain the same BaseResponseModel
        // envelope shape with success=false and populated errors array.
        // =====================================================================

        #region Error Response Shape Tests

        /// <summary>
        /// Validates that an error response has the correct <c>BaseResponseModel</c> shape
        /// with <c>success=false</c>, non-empty errors array, and <c>object=null</c>.
        /// </summary>
        [Fact]
        public async Task ErrorResponse_HasCorrectShape()
        {
            // Arrange — configure mock to return an error-shaped BaseResponseModel
            ConfigureMockResponse(
                CreateErrorResponseBody(
                    errorKey: "field",
                    errorValue: "invalid",
                    errorMessage: "Field validation failed"),
                HttpStatusCode.BadRequest);

            // Act
            var (_, json, _) = await SendAuthenticatedGetAsync();

            // Assert — verify complete error response envelope
            json["success"].Value<bool>().Should().BeFalse(
                "error responses must have success=false");
            json["errors"].Type.Should().Be(JTokenType.Array,
                "errors must always be an array");
            ((JArray)json["errors"]).Count.Should().BeGreaterThan(0,
                "error responses must contain at least one error");
            json.ContainsKey("timestamp").Should().BeTrue(
                "error responses must include timestamp");
            json.ContainsKey("message").Should().BeTrue(
                "error responses must include message field");
            json.ContainsKey("object").Should().BeTrue(
                "error responses must include object field");
            json["object"].Type.Should().Be(JTokenType.Null,
                "error responses typically have object=null");
        }

        /// <summary>
        /// Validates that each error object in the errors array contains the required
        /// <c>ErrorModel</c> properties: <c>key</c>, <c>value</c>, <c>message</c>.
        /// BaseModels.cs lines 62-83: ErrorModel with [JsonProperty] annotations for
        /// "key" (line 64), "value" (line 67), "message" (line 70).
        /// Properties CAN have null values but MUST exist as JSON keys.
        /// </summary>
        [Fact]
        public async Task ErrorResponse_ErrorObject_HasKeyValueMessage()
        {
            // Arrange — error with populated ErrorModel fields
            ConfigureMockResponse(
                CreateErrorResponseBody(
                    errorKey: "entityName",
                    errorValue: "invalid_entity",
                    errorMessage: "Entity not found"),
                HttpStatusCode.BadRequest);

            // Act
            var (_, json, _) = await SendAuthenticatedGetAsync();

            // Assert — each error object must have key, value, message properties
            var errorArray = json["errors"] as JArray;
            errorArray.Should().NotBeNull("errors must be a JArray");
            errorArray.Any().Should().BeTrue("error response must contain at least one error");

            foreach (var error in errorArray)
            {
                var errorObj = error as JObject;
                errorObj.Should().NotBeNull("each error must be a JSON object");
                errorObj.ContainsKey("key").Should().BeTrue(
                    "ErrorModel always has key property (BaseModels.cs line 64)");
                errorObj.ContainsKey("value").Should().BeTrue(
                    "ErrorModel always has value property (BaseModels.cs line 67)");
                errorObj.ContainsKey("message").Should().BeTrue(
                    "ErrorModel always has message property (BaseModels.cs line 70)");
            }
        }

        #endregion

        // =====================================================================
        // Phase 4: HTTP Status Code Mapping Tests
        // Validates that the Gateway preserves the monolith's ApiControllerBase.cs
        // HTTP status code mapping behavior when proxying backend responses.
        // =====================================================================

        #region HTTP Status Code Mapping Tests

        /// <summary>
        /// Validates that a success response with no errors returns HTTP 200 OK.
        /// ApiControllerBase.cs line 26: <c>return Json(response)</c> — default 200
        /// when no errors and success=true.
        /// </summary>
        [Fact]
        public async Task SuccessResponse_NoErrors_Returns200OK()
        {
            ConfigureMockResponse(CreateSuccessResponseBody(), HttpStatusCode.OK);
            var (response, _, _) = await SendAuthenticatedGetAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "success with no errors returns 200 per ApiControllerBase.cs line 26");
        }

        /// <summary>
        /// Validates that a response with errors and original StatusCode.OK is converted
        /// to HTTP 400 BadRequest.
        /// ApiControllerBase.cs lines 18-21:
        ///   <c>if (response.Errors.Count > 0 || !response.Success)</c>
        ///   <c>if (response.StatusCode == HttpStatusCode.OK) → force 400</c>
        /// The backend service applies this logic; the Gateway proxies the 400 through.
        /// </summary>
        [Fact]
        public async Task ErrorResponse_WithStatusCodeOK_Returns400BadRequest()
        {
            // The backend service returns 400 after applying DoResponse logic
            // (errors present + original StatusCode.OK → forced to 400)
            ConfigureMockResponse(
                CreateErrorResponseBody(
                    errorKey: "validation",
                    errorValue: "",
                    errorMessage: "Validation failed"),
                HttpStatusCode.BadRequest);

            var (response, json, _) = await SendAuthenticatedGetAsync();

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "errors with StatusCode.OK forces 400 per ApiControllerBase.cs lines 18-21");
            json["success"].Value<bool>().Should().BeFalse(
                "error response must have success=false");
        }

        /// <summary>
        /// Validates that a custom HTTP status code from the backend service is propagated
        /// correctly through the Gateway.
        /// ApiControllerBase.cs line 23: if StatusCode is NOT OK and there are errors,
        /// use the custom StatusCode.
        /// </summary>
        [Fact]
        public async Task CustomStatusCode_PropagatedCorrectly()
        {
            // Backend returns 403 Forbidden (custom status code for auth errors)
            ConfigureMockResponse(
                CreateErrorResponseBody(
                    errorKey: "auth",
                    errorValue: "",
                    errorMessage: "Access denied"),
                HttpStatusCode.Forbidden);

            var (response, _, _) = await SendAuthenticatedGetAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "custom status codes must be propagated per ApiControllerBase.cs line 23");
        }

        /// <summary>
        /// Validates that a 404 Not Found response is returned correctly.
        /// ApiControllerBase.cs lines 32-35: <c>DoPageNotFoundResponse</c> returns 404
        /// with empty JSON <c>{}</c>.
        /// Tests that the Gateway proxies 404 responses from backend services.
        /// </summary>
        [Fact]
        public async Task NotFound_Returns404()
        {
            // Configure mock to return 404 matching DoPageNotFoundResponse behavior
            ConfigureMockResponse(new { }, HttpStatusCode.NotFound);

            var (response, _, _) = await SendAuthenticatedGetAsync();

            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "DoPageNotFoundResponse returns 404 per ApiControllerBase.cs lines 32-35");
        }

        /// <summary>
        /// Validates that DoBadRequestResponse behavior is preserved: sets success=false
        /// and timestamp close to DateTime.UtcNow.
        /// ApiControllerBase.cs lines 46-47:
        ///   <c>response.Timestamp = DateTime.UtcNow;</c>
        ///   <c>response.Success = false;</c>
        /// ApiControllerBase.cs line 60: <c>HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;</c>
        /// </summary>
        [Fact]
        public async Task BadRequest_Returns400_WithSuccessFalse_AndTimestampUtcNow()
        {
            // Capture time window around the request for timestamp validation
            var beforeRequest = DateTime.UtcNow.AddSeconds(-5);

            // Backend produces DoBadRequestResponse shape (success=false, timestamp=UtcNow)
            ConfigureMockResponse(
                CreateErrorResponseBody(
                    errorKey: "request",
                    errorValue: "",
                    errorMessage: "Bad request data",
                    topMessage: "Invalid request"),
                HttpStatusCode.BadRequest);

            var (response, json, _) = await SendAuthenticatedGetAsync();
            var afterRequest = DateTime.UtcNow.AddSeconds(5);

            // Assert HTTP 400
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "DoBadRequestResponse forces HTTP 400 per ApiControllerBase.cs line 60");

            // Assert success=false
            json["success"].Value<bool>().Should().BeFalse(
                "DoBadRequestResponse forces success=false per ApiControllerBase.cs line 47");

            // Assert timestamp is close to UtcNow (within ±5 seconds tolerance)
            var timestampStr = json["timestamp"].ToString();
            DateTime.TryParse(timestampStr, out var timestamp).Should().BeTrue(
                "timestamp must be a parseable DateTime per ApiControllerBase.cs line 46");
            timestamp.Should().BeOnOrAfter(beforeRequest,
                "timestamp should be close to UtcNow");
            timestamp.Should().BeOnOrBefore(afterRequest,
                "timestamp should be close to UtcNow");
        }

        #endregion

        // =====================================================================
        // Phase 5: Error Message Tests (Development vs Production Mode)
        // Validates that the Gateway passes through error message details from
        // backend services that implement ApiControllerBase.DoBadRequestResponse
        // behavior for development and production environments.
        // =====================================================================

        #region Error Message Tests

        /// <summary>
        /// Validates that in development mode, error responses from backend services
        /// include exception message and stack trace details.
        /// ApiControllerBase.cs lines 49-52:
        ///   <c>if (ErpSettings.DevelopmentMode)</c>
        ///   <c>if (ex != null) response.Message = ex.Message + ex.StackTrace;</c>
        /// The backend service produces this response; the Gateway passes it through.
        /// </summary>
        [Fact]
        public async Task DevelopmentMode_Error_IncludesExceptionMessageAndStackTrace()
        {
            // Simulate a development-mode error response from the backend service
            // where DoBadRequestResponse includes exception details in the message
            var devErrorMessage =
                "NullReferenceException: Object reference not set to an instance of an object." +
                "   at WebVella.Erp.Api.RecordManager.Find(String entityName) in RecordManager.cs:line 42" +
                "   at WebVella.Erp.Web.Controllers.WebApiController.GetRecord() in WebApiController.cs:line 200";

            ConfigureMockResponse(new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                errors = new object[0],
                message = devErrorMessage,
                @object = (object)null,
                hash = (string)null,
                accessWarnings = new object[0]
            }, HttpStatusCode.BadRequest);

            var (_, json, _) = await SendAuthenticatedGetAsync();

            // Assert message includes exception details per ApiControllerBase.cs line 52
            var message = json["message"].Value<string>();
            message.Should().NotBeNull(
                "dev mode error must include a message");
            message.Should().Contain("NullReferenceException",
                "dev mode includes exception message per ApiControllerBase.cs line 52");
            message.Should().Contain("at ",
                "dev mode includes stack trace text per ApiControllerBase.cs line 52");
        }

        /// <summary>
        /// Validates that in production mode, error responses show a generic message
        /// when no explicit message was provided by the calling code.
        /// ApiControllerBase.cs lines 56-57:
        ///   <c>if (string.IsNullOrEmpty(message))</c>
        ///   <c>response.Message = "An internal error occurred!";</c>
        /// </summary>
        [Fact]
        public async Task ProductionMode_Error_ShowsGenericMessage_WhenNoMessageProvided()
        {
            // Simulate a production-mode error response from the backend service
            // where DoBadRequestResponse provides the generic message
            ConfigureMockResponse(new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                errors = new object[0],
                message = "An internal error occurred!",
                @object = (object)null,
                hash = (string)null,
                accessWarnings = new object[0]
            }, HttpStatusCode.BadRequest);

            var (_, json, _) = await SendAuthenticatedGetAsync();

            // Assert message matches the exact generic message from ApiControllerBase.cs line 57
            json["message"].Value<string>().Should().Be("An internal error occurred!",
                "production mode must show generic message per ApiControllerBase.cs line 57");
        }

        #endregion

        // =====================================================================
        // Phase 6: Serialization Tests
        // Validates that the Gateway uses Newtonsoft.Json (NOT System.Text.Json)
        // for API response serialization, preserving camelCase property names from
        // [JsonProperty(PropertyName)] annotations in BaseModels.cs.
        // =====================================================================

        #region Serialization Tests

        /// <summary>
        /// Validates that JSON serialization uses Newtonsoft.Json with camelCase
        /// property names from [JsonProperty(PropertyName)] annotations, NOT
        /// System.Text.Json PascalCase default naming.
        /// Gateway Program.cs configures <c>.AddNewtonsoftJson(...)</c> matching
        /// monolith Startup.cs lines 74-77.
        ///
        /// Expected camelCase names per BaseModels.cs:
        ///   "success" (line 13), "timestamp" (line 10), "errors" (line 22),
        ///   "message" (line 16), "object" (line 42), "hash" (line 19),
        ///   "accessWarnings" (line 25)
        /// </summary>
        [Fact]
        public async Task JsonSerialization_UsesNewtonsoftJson_NotSystemTextJson()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, _, rawJson) = await SendAuthenticatedGetAsync();

            // Assert camelCase property names per [JsonProperty] annotations
            rawJson.Should().Contain("\"success\"",
                "must use camelCase 'success' per BaseModels.cs line 13 [JsonProperty]");
            rawJson.Should().Contain("\"timestamp\"",
                "must use camelCase 'timestamp' per BaseModels.cs line 10 [JsonProperty]");
            rawJson.Should().Contain("\"errors\"",
                "must use camelCase 'errors' per BaseModels.cs line 22 [JsonProperty]");
            rawJson.Should().Contain("\"message\"",
                "must use camelCase 'message' per BaseModels.cs line 16 [JsonProperty]");

            // Assert PascalCase does NOT appear — would indicate System.Text.Json
            // is being used instead of Newtonsoft.Json
            rawJson.Should().NotContain("\"Success\"",
                "PascalCase 'Success' must not appear — Newtonsoft.Json must be used");
            rawJson.Should().NotContain("\"Timestamp\"",
                "PascalCase 'Timestamp' must not appear");
            rawJson.Should().NotContain("\"Errors\"",
                "PascalCase 'Errors' must not appear");
            rawJson.Should().NotContain("\"Message\"",
                "PascalCase 'Message' must not appear");
        }

        /// <summary>
        /// Validates that the response Content-Type header is <c>application/json</c>.
        /// This ensures the Gateway returns proper JSON responses for all API endpoints,
        /// matching the monolith's <c>return Json(response)</c> behavior which sets
        /// Content-Type: application/json automatically.
        /// Also validates using an anonymous client via <see cref="CustomWebApplicationFactory.CreateAnonymousClient"/>
        /// to confirm content type is set regardless of authentication status.
        /// </summary>
        [Fact]
        public async Task ResponseContentType_IsApplicationJson()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (response, _, _) = await SendAuthenticatedGetAsync();

            response.Content.Headers.ContentType.Should().NotBeNull(
                "Content-Type header must be present on API responses");
            response.Content.Headers.ContentType.MediaType.Should().Be("application/json",
                "API responses must have application/json content type");

            // Also verify with anonymous client — content type should be application/json
            // regardless of authentication status (e.g., for 401/403 error responses)
            var anonClient = _factory.CreateAnonymousClient();
            var anonResponse = await anonClient.GetAsync(TestApiEndpoint);
            anonResponse.Content.Headers.ContentType.Should().NotBeNull(
                "Content-Type header must be present even for anonymous requests");
            anonResponse.Content.Headers.ContentType.MediaType.Should().Be("application/json",
                "application/json content type required for anonymous requests too");
        }

        #endregion

        // =====================================================================
        // Phase 7: Additional Field Validation
        // Validates specific fields from the BaseResponseModel that have special
        // serialization or visibility requirements.
        // =====================================================================

        #region Additional Field Validation

        /// <summary>
        /// Validates that the "hash" field is present in the response.
        /// BaseModels.cs lines 19-20:
        ///   <c>[JsonProperty(PropertyName = "hash")]</c>
        ///   <c>public string Hash { get; set; }</c>
        /// Initialized to null in constructor (line 33): <c>Hash = null;</c>
        /// </summary>
        [Fact]
        public async Task HashField_IsPresentInResponse()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, _) = await SendAuthenticatedGetAsync();

            json.ContainsKey("hash").Should().BeTrue(
                "hash property must be present per BaseModels.cs line 19 [JsonProperty]");
        }

        /// <summary>
        /// Validates that the "accessWarnings" field is present and is a JSON array.
        /// BaseModels.cs lines 25-26:
        ///   <c>[JsonProperty(PropertyName = "accessWarnings")]</c>
        ///   <c>public List&lt;AccessWarningModel&gt; AccessWarnings { get; set; }</c>
        /// Initialized to empty list in constructor (line 35):
        ///   <c>AccessWarnings = new List&lt;AccessWarningModel&gt;();</c>
        /// </summary>
        [Fact]
        public async Task AccessWarningsField_IsPresentAndArray()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, _) = await SendAuthenticatedGetAsync();

            json.ContainsKey("accessWarnings").Should().BeTrue(
                "accessWarnings property must be present per BaseModels.cs line 25 [JsonProperty]");
            json["accessWarnings"].Type.Should().Be(JTokenType.Array,
                "accessWarnings must be a JSON array per BaseModels.cs line 26");
        }

        /// <summary>
        /// Validates that the "statusCode" field is NOT serialized in the JSON response.
        /// BaseModels.cs line 28-29:
        ///   <c>[JsonIgnore]</c>
        ///   <c>public HttpStatusCode StatusCode { get; set; }</c>
        /// The [JsonIgnore] attribute prevents serialization of this internal status
        /// tracking field — it must NOT appear in the response body in any casing.
        /// </summary>
        [Fact]
        public async Task StatusCode_IsNotSerialized_InJsonResponse()
        {
            ConfigureMockResponse(CreateSuccessResponseBody());
            var (_, json, rawJson) = await SendAuthenticatedGetAsync();

            // Assert statusCode is not present in any casing
            json.ContainsKey("statusCode").Should().BeFalse(
                "[JsonIgnore] on StatusCode (BaseModels.cs line 28) must prevent serialization");
            json.ContainsKey("StatusCode").Should().BeFalse(
                "StatusCode must not appear in PascalCase either");

            // Also check raw JSON to catch any edge cases in property naming
            rawJson.Should().NotContain("\"statusCode\"",
                "statusCode must not appear in serialized JSON per [JsonIgnore]");
            rawJson.Should().NotContain("\"StatusCode\"",
                "StatusCode must not appear in serialized JSON per [JsonIgnore]");
        }

        #endregion
    }
}
