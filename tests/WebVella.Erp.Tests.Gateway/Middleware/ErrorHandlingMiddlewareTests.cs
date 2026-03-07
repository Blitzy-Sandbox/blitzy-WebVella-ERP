using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.Gateway.Middleware;
using Xunit;

namespace WebVella.Erp.Tests.Gateway.Middleware
{
    /// <summary>
    /// Comprehensive xUnit unit tests for <see cref="ErrorHandlingMiddleware"/>.
    /// Validates exception-to-status-code mapping (502/504/401/400/500),
    /// BaseResponseModel JSON envelope format, file access error filtering,
    /// development vs production mode error detail, response-already-started safety,
    /// and nested try-catch resilience.
    ///
    /// Derived from monolith sources:
    /// - ErpErrorHandlingMiddleware.cs (70 lines) — error filtering, nested catch
    /// - ApiControllerBase.cs — BaseResponseModel envelope, dev/prod mode
    /// </summary>
    public class ErrorHandlingMiddlewareTests : IDisposable
    {
        /// <summary>
        /// Mock logger used as a required constructor dependency for the middleware.
        /// Replaces the monolith's database-backed LogService.Create().
        /// </summary>
        private readonly Mock<ILogger<ErrorHandlingMiddleware>> _mockLogger;

        /// <summary>
        /// Saved original ASPNETCORE_ENVIRONMENT value, restored in Dispose()
        /// to prevent cross-test pollution.
        /// </summary>
        private readonly string? _originalEnvironment;

        public ErrorHandlingMiddlewareTests()
        {
            _mockLogger = new Mock<ILogger<ErrorHandlingMiddleware>>();
            _originalEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        }

        /// <summary>
        /// Restores the original ASPNETCORE_ENVIRONMENT after each test.
        /// </summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalEnvironment);
        }

        #region Helper Methods

        /// <summary>
        /// Creates an ErrorHandlingMiddleware instance with the shared mock logger.
        /// Mirrors the middleware constructor: ErrorHandlingMiddleware(RequestDelegate, ILogger).
        /// </summary>
        /// <param name="next">The next middleware delegate to pass into the constructor.</param>
        /// <returns>A fully-configured ErrorHandlingMiddleware instance.</returns>
        private ErrorHandlingMiddleware CreateMiddleware(RequestDelegate next)
        {
            return new ErrorHandlingMiddleware(next, _mockLogger.Object);
        }

        /// <summary>
        /// Creates a DefaultHttpContext with a MemoryStream response body for capturing
        /// middleware-written output. This is essential because the default response body
        /// (Stream.Null) discards all written data.
        /// </summary>
        /// <returns>A DefaultHttpContext with a capturable MemoryStream response body.</returns>
        private static DefaultHttpContext CreateHttpContext()
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            return context;
        }

        /// <summary>
        /// Reads the response body from the HttpContext's MemoryStream.
        /// Seeks to the beginning of the stream before reading, and uses leaveOpen: true
        /// to prevent the StreamReader from disposing the underlying MemoryStream.
        /// </summary>
        /// <param name="context">The HttpContext whose response body to read.</param>
        /// <returns>The response body content as a string.</returns>
        private static async Task<string> ReadResponseBody(HttpContext context)
        {
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }

        #endregion

        #region Inner Types

        /// <summary>
        /// A logger implementation that throws on every Log call.
        /// Used in <see cref="Invoke_LoggingFailure_DoesNotMaskOriginalError"/>
        /// to test the middleware's nested defensive try-catch pattern
        /// (ErpErrorHandlingMiddleware.cs lines 56-66).
        /// </summary>
        private class ThrowingLogger : ILogger<ErrorHandlingMiddleware>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                throw new InvalidOperationException("Logger failed catastrophically");
            }
        }

        #endregion

        #region Phase 2: Pass-Through Tests (Happy Path)

        /// <summary>
        /// Verifies that successful requests pass through the middleware without modification.
        /// The middleware should not alter the status code, response body, or headers
        /// when no exception is thrown by the downstream pipeline.
        /// </summary>
        [Fact]
        public async Task Invoke_SuccessfulRequest_PassesThroughWithoutModification()
        {
            // Arrange
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(async ctx =>
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                await ctx.Response.WriteAsync("OK");
            });

            // Act
            await middleware.Invoke(context);

            // Assert — response status is 200 OK
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            // Assert — response body is "OK" (not wrapped in error envelope)
            var body = await ReadResponseBody(context);
            body.Should().Be("OK");

            // Assert — no error logging occurred (verify via Invocations collection)
            _mockLogger.Invocations
                .Where(i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Error)
                .Should().BeEmpty("no error logging should occur for successful requests");
        }

        #endregion

        #region Phase 3: Exception Type → HTTP Status Code Mapping Tests

        /// <summary>
        /// HttpRequestException (backend service unreachable) must map to HTTP 502 Bad Gateway.
        /// This mapping reflects the Gateway's role as a reverse proxy per AAP Section 0.4.3.
        /// </summary>
        [Fact]
        public async Task Invoke_HttpRequestException_Returns502BadGateway()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new HttpRequestException("Backend unreachable"));

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadGateway);
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            json["success"]!.Value<bool>().Should().BeFalse();
        }

        /// <summary>
        /// TaskCanceledException (request timeout) must map to HTTP 504 Gateway Timeout.
        /// Gateway-specific: indicates the backend service did not respond in time.
        /// </summary>
        [Fact]
        public async Task Invoke_TaskCanceledException_Returns504GatewayTimeout()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new TaskCanceledException("Request timed out"));

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.GatewayTimeout);
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            json["success"]!.Value<bool>().Should().BeFalse();
        }

        /// <summary>
        /// UnauthorizedAccessException must map to HTTP 401 Unauthorized.
        /// </summary>
        [Fact]
        public async Task Invoke_UnauthorizedAccessException_Returns401Unauthorized()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new UnauthorizedAccessException("Access denied"));

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            json["success"]!.Value<bool>().Should().BeFalse();
        }

        /// <summary>
        /// ArgumentException (invalid parameter) must map to HTTP 400 Bad Request.
        /// Matches monolith behavior from ApiControllerBase.DoBadRequestResponse().
        /// </summary>
        [Fact]
        public async Task Invoke_ArgumentException_Returns400BadRequest()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new ArgumentException("Invalid parameter"));

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            json["success"]!.Value<bool>().Should().BeFalse();
        }

        /// <summary>
        /// Generic Exception must map to HTTP 500 Internal Server Error.
        /// This is the catch-all default for unrecognized exception types.
        /// </summary>
        [Fact]
        public async Task Invoke_GenericException_Returns500InternalServerError()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new Exception("Something broke"));

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            json["success"]!.Value<bool>().Should().BeFalse();
        }

        #endregion

        #region Phase 4: Response Envelope Format Tests

        /// <summary>
        /// Validates that the error response matches the BaseResponseModel envelope shape
        /// used throughout the monolith: { success, errors, timestamp, message, object }.
        /// This is the REST API v3 response contract per AAP Section 0.8.1.
        ///
        /// Envelope fields validated:
        /// - success: must be false
        /// - errors: must be a non-empty array with objects containing "message"
        /// - timestamp: must be a valid ISO 8601 datetime
        /// - message: must be a non-empty descriptive string
        /// - object: must be null
        /// </summary>
        [Fact]
        public async Task Invoke_Exception_ReturnsBaseResponseModelEnvelope()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new Exception("Envelope test error"));

            // Act
            await middleware.Invoke(context);

            // Assert — parse response body as JSON
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);

            // success — must be false
            json["success"].Should().NotBeNull();
            json["success"]!.Value<bool>().Should().BeFalse();

            // errors — must be an array with at least one error object containing "message"
            json["errors"].Should().NotBeNull();
            var errors = json["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);
            errors[0]!["message"].Should().NotBeNull();
            errors[0]!["message"]!.Value<string>().Should().NotBeNullOrEmpty();

            // errors array objects should have key, value, message properties
            // matching ErrorModel from BaseResponseModel.cs
            errors[0]!["key"].Should().NotBeNull("ErrorModel should include 'key' property");
            errors[0]!["value"].Should().NotBeNull("ErrorModel should include 'value' property");

            // timestamp — must be present and parseable as DateTime (ISO 8601)
            json["timestamp"].Should().NotBeNull();
            var timestampValue = json["timestamp"]!.Value<DateTime>();
            timestampValue.Should().BeAfter(DateTime.MinValue);

            // message — must contain a descriptive error message string
            json["message"].Should().NotBeNull();
            json["message"]!.Value<string>().Should().NotBeNullOrEmpty();

            // object — must be null
            json["object"]!.Type.Should().Be(JTokenType.Null);
        }

        /// <summary>
        /// Verifies the response body is valid JSON parseable by Newtonsoft.Json.
        /// This is critical because the monolith uses Newtonsoft.Json exclusively
        /// for API responses (AAP Section 0.8.2). System.Text.Json may serialize
        /// differently (e.g., camelCase vs PascalCase, null handling).
        /// </summary>
        [Fact]
        public async Task Invoke_Exception_UsesNewtonsoftJsonSerialization()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new Exception("Newtonsoft test error"));

            // Act
            await middleware.Invoke(context);

            // Assert — response body is non-empty valid JSON
            var body = await ReadResponseBody(context);
            body.Should().NotBeNullOrEmpty();

            // Must be parseable by Newtonsoft.Json.JsonConvert.DeserializeObject
            var deserialized = JsonConvert.DeserializeObject(body);
            deserialized.Should().NotBeNull();

            // Must also be parseable by JObject.Parse (LINQ-to-JSON API)
            var jobj = JObject.Parse(body);
            jobj.Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that the error response sets Content-Type to "application/json".
        /// This ensures clients can reliably parse error responses as JSON.
        /// </summary>
        [Fact]
        public async Task Invoke_Exception_SetsContentTypeToApplicationJson()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new Exception("Content type test"));

            // Act
            await middleware.Invoke(context);

            // Assert
            context.Response.ContentType.Should().Be("application/json");
        }

        #endregion

        #region Phase 5: File Access Error Filtering Tests

        /// <summary>
        /// Validates that IOException containing "The process cannot access the file"
        /// is silently filtered — the middleware returns without writing an error response.
        /// This preserves the exact behavior from ErpErrorHandlingMiddleware.cs line 50:
        ///   if (ex != null &amp;&amp; ex.Message.Contains("The process cannot access the file")) return;
        ///
        /// CRITICAL: This exact string match MUST be preserved for backward compatibility.
        /// The monolith treats file access errors as non-critical transient conditions.
        /// </summary>
        [Fact]
        public async Task Invoke_FileAccessError_SilentlyFiltered()
        {
            // Arrange
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new IOException(
                    "The process cannot access the file because it is being used by another process."));

            // Act
            await middleware.Invoke(context);

            // Assert — middleware returns without writing an error response
            var body = await ReadResponseBody(context);
            body.Should().BeEmpty("file access errors should be silently filtered without response body");

            // Assert — status code remains default (200) since no error response was written
            context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);

            // Assert — logged as warning (not error)
            _mockLogger.Invocations
                .Where(i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Warning)
                .Should().NotBeEmpty("file access errors should be logged as warnings");

            // Assert — NOT logged as error (filtered before LogError call)
            _mockLogger.Invocations
                .Where(i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Error)
                .Should().BeEmpty("file access errors should not be logged at Error level");
        }

        #endregion

        #region Phase 6: Development vs Production Mode Tests

        /// <summary>
        /// In Development mode, the error response must include the full stack trace
        /// for debugging. The middleware uses ASPNETCORE_ENVIRONMENT == "Development"
        /// to determine this, adapted from ApiControllerBase.cs lines 51-52:
        ///   response.Message = ex.Message + ex.StackTrace
        ///
        /// In the Gateway middleware, development mode uses ex.ToString() which includes
        /// the exception type, message, AND full stack trace.
        /// </summary>
        [Fact]
        public async Task Invoke_DevelopmentMode_IncludesFullStackTrace()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new InvalidOperationException("Dev mode test error"));

            // Act
            await middleware.Invoke(context);

            // Assert — parse response and check the errors array
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            var errors = json["errors"] as JArray;
            errors.Should().NotBeNull();
            var errorMessage = errors![0]!["message"]!.Value<string>();

            // In development mode, errorMessage = ex.ToString() which includes type + stack trace
            errorMessage.Should().Contain("InvalidOperationException",
                "development mode should include the exception type name");
            errorMessage.Should().Contain("Dev mode test error",
                "development mode should include the exception message");
            // ex.ToString() includes " at " lines from the stack trace
            errorMessage.Should().Contain(" at ",
                "development mode should include the stack trace");
        }

        /// <summary>
        /// In Production mode, the error response must include only the exception message
        /// (no stack trace) for security. Adapted from ApiControllerBase.cs lines 56-57:
        ///   response.Message = "An internal error occurred!"
        ///
        /// In the Gateway middleware, production mode uses ex.Message which contains
        /// only the error message string without type information or stack trace.
        /// </summary>
        [Fact]
        public async Task Invoke_ProductionMode_IncludesMessageOnly()
        {
            // Arrange
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            var context = CreateHttpContext();
            var middleware = CreateMiddleware(_ =>
                throw new InvalidOperationException("Prod mode test error"));

            // Act
            await middleware.Invoke(context);

            // Assert — parse response and check the errors array
            var body = await ReadResponseBody(context);
            var json = JObject.Parse(body);
            var errors = json["errors"] as JArray;
            errors.Should().NotBeNull();
            var errorMessage = errors![0]!["message"]!.Value<string>();

            // In production mode, errorMessage = ex.Message (no stack trace)
            errorMessage.Should().Be("Prod mode test error",
                "production mode should only include the exception message");
            // Must NOT contain stack trace lines
            errorMessage.Should().NotContain(" at ",
                "production mode must not leak stack trace information");
        }

        #endregion

        #region Phase 7: Response Already Started Tests

        /// <summary>
        /// When the HTTP response has already started being sent to the client
        /// (e.g., headers flushed), the middleware cannot modify status code or headers.
        /// It must log a warning and return without attempting to write.
        /// This is a standard ASP.NET Core middleware safety pattern.
        /// </summary>
        [Fact]
        public async Task Invoke_ResponseAlreadyStarted_LogsWarningDoesNotWrite()
        {
            // Arrange — use Moq to create an HttpContext where Response.HasStarted = true
            var responseBody = new MemoryStream();

            var mockResponse = new Mock<HttpResponse>();
            mockResponse.SetupGet(r => r.HasStarted).Returns(true);
            mockResponse.SetupGet(r => r.Body).Returns(responseBody);
            mockResponse.SetupProperty(r => r.StatusCode);
            mockResponse.SetupProperty(r => r.ContentType);

            var mockRequest = new Mock<HttpRequest>();
            mockRequest.SetupGet(r => r.Method).Returns("GET");
            mockRequest.SetupGet(r => r.Path).Returns(new PathString("/test"));
            mockRequest.SetupGet(r => r.QueryString).Returns(new QueryString(""));

            var mockContext = new Mock<HttpContext>();
            mockContext.SetupGet(c => c.Response).Returns(mockResponse.Object);
            mockContext.SetupGet(c => c.Request).Returns(mockRequest.Object);

            var middleware = CreateMiddleware(_ =>
                throw new Exception("Response started test"));

            // Act — middleware should NOT throw even though response has started
            Func<Task> act = () => middleware.Invoke(mockContext.Object);
            await act.Should().NotThrowAsync(
                "middleware must not crash when response has already started");

            // Assert — response body remains empty (nothing written)
            responseBody.Length.Should().Be(0,
                "no data should be written to response when HasStarted is true");

            // Assert — a warning was logged about the response already being started
            _mockLogger.Invocations
                .Where(i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == LogLevel.Warning)
                .Should().NotBeEmpty(
                    "a warning should be logged when response has already started");
        }

        #endregion

        #region Phase 8: Nested Exception Safety Tests

        /// <summary>
        /// Validates the monolith's defensive nested try-catch pattern
        /// (ErpErrorHandlingMiddleware.cs lines 56-66) is preserved.
        ///
        /// When the logger itself throws (e.g., logging infrastructure failure),
        /// the middleware must NOT propagate any exception — it silently swallows
        /// the logging failure to prevent masking the original application error.
        ///
        /// Uses a custom ThrowingLogger that throws on every Log call to simulate
        /// complete logging infrastructure failure.
        /// </summary>
        [Fact]
        public async Task Invoke_LoggingFailure_DoesNotMaskOriginalError()
        {
            // Arrange — use a logger that throws on every Log call
            var throwingLogger = new ThrowingLogger();
            var context = CreateHttpContext();
            var middleware = new ErrorHandlingMiddleware(
                _ => throw new Exception("Original error"),
                throwingLogger);

            // Act — middleware must NOT throw even though logger is completely broken
            Func<Task> act = () => middleware.Invoke(context);

            // Assert — no exception propagates to the caller
            // This validates the nested try-catch safety pattern from the monolith:
            //   catch (Exception logEx) {
            //     try { _logger.LogCritical(...); }
            //     catch { /* final safety net: silently swallow */ }
            //   }
            await act.Should().NotThrowAsync(
                "logging failures must never mask the original error or propagate to callers");
        }

        #endregion
    }
}
