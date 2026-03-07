using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebVella.Erp.Gateway.Configuration;
using WebVella.Erp.Gateway.Middleware;
using Xunit;

namespace WebVella.Erp.Tests.Gateway.Middleware
{
    /// <summary>
    /// Comprehensive xUnit unit tests for <see cref="RequestRoutingMiddleware"/>,
    /// the domain-based request routing middleware implementing the Strangler Fig pattern
    /// (AAP Section 0.4.3). Validates route matching, request forwarding, response
    /// copy-back, error handling (502/504), query string preservation, JWT token
    /// propagation, X-Forwarded-* headers, and HTTP method forwarding.
    /// </summary>
    public class RequestRoutingMiddlewareTests
    {
        #region Fields and Constructor

        private readonly Mock<ILogger<RequestRoutingMiddleware>> _mockLogger;
        private readonly RouteConfiguration _routeConfig;

        /// <summary>
        /// Initializes shared test fixtures: a mock logger and a <see cref="RouteConfiguration"/>
        /// populated with known service URLs and route mappings. Route mappings correspond to
        /// monolith WebApiController/AdminController/ProjectController patterns, splitting the
        /// single monolith API surface across domain-aligned microservices.
        /// </summary>
        public RequestRoutingMiddlewareTests()
        {
            _mockLogger = new Mock<ILogger<RequestRoutingMiddleware>>();
            _routeConfig = new RouteConfiguration
            {
                CoreServiceUrl = "http://core-service:8080",
                AdminServiceUrl = "http://admin-service:8080",
                ProjectServiceUrl = "http://project-service:8080",
                CrmServiceUrl = "http://crm-service:8080",
                MailServiceUrl = "http://mail-service:8080",
                RouteMappings = new Dictionary<string, string>
                {
                    { "/api/v3/", "CoreServiceUrl" },
                    { "/api/v3.0/p/sdk/", "AdminServiceUrl" },
                    { "/api/v3.0/p/project/", "ProjectServiceUrl" },
                    { "/api/v3.0/p/crm/", "CrmServiceUrl" },
                    { "/api/v3.0/p/mail/", "MailServiceUrl" }
                }
            };
        }

        #endregion

        #region Inner Classes

        /// <summary>
        /// Custom <see cref="HttpMessageHandler"/> that captures sent requests and returns
        /// controlled responses. Supports both fixed responses and custom async functions
        /// for simulating backend service failures (HttpRequestException, TaskCanceledException).
        /// </summary>
        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendFunc;

            /// <summary>The captured <see cref="HttpRequestMessage"/> from the last call.</summary>
            public HttpRequestMessage CapturedRequest { get; private set; }

            /// <summary>The body content of the captured request, read as a string before potential disposal.</summary>
            public string CapturedRequestBody { get; private set; }

            /// <summary>Creates a handler that always returns the specified fixed response.</summary>
            /// <param name="response">The HTTP response to return for every request.</param>
            public MockHttpMessageHandler(HttpResponseMessage response)
            {
                _sendFunc = (_, __) => Task.FromResult(response);
            }

            /// <summary>
            /// Creates a handler with a custom async send function for advanced scenarios
            /// such as throwing exceptions to simulate backend unavailability or timeouts.
            /// </summary>
            /// <param name="sendFunc">Custom function controlling the handler's behavior.</param>
            public MockHttpMessageHandler(
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc)
            {
                _sendFunc = sendFunc;
            }

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedRequest = request;
                if (request.Content != null)
                {
                    CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                }
                return await _sendFunc(request, cancellationToken);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a <see cref="RequestRoutingMiddleware"/> instance with the given
        /// <see cref="HttpMessageHandler"/> backing the <see cref="IHttpClientFactory"/>.
        /// The factory is configured to return an HttpClient wrapping the supplied handler
        /// for any client name (the middleware uses "default").
        /// </summary>
        /// <param name="next">The next middleware delegate in the pipeline.</param>
        /// <param name="handler">The mock message handler controlling backend responses.</param>
        /// <returns>The configured middleware instance.</returns>
        private RequestRoutingMiddleware CreateMiddleware(RequestDelegate next, HttpMessageHandler handler)
        {
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler));

            return new RequestRoutingMiddleware(
                next,
                mockFactory.Object,
                Options.Create(_routeConfig),
                _mockLogger.Object);
        }

        /// <summary>
        /// Creates a <see cref="RequestRoutingMiddleware"/> with a mock
        /// <see cref="IHttpClientFactory"/> that has NO client setup. Used for tests
        /// where no backend call is expected (non-API request pass-through).
        /// Returns both the middleware and the mock factory for verification.
        /// </summary>
        /// <param name="next">The next middleware delegate in the pipeline.</param>
        /// <returns>Tuple containing the middleware and the mock factory for verification.</returns>
        private (RequestRoutingMiddleware middleware, Mock<IHttpClientFactory> factory) CreateMiddlewareNoBackend(
            RequestDelegate next)
        {
            var mockFactory = new Mock<IHttpClientFactory>();
            var middleware = new RequestRoutingMiddleware(
                next,
                mockFactory.Object,
                Options.Create(_routeConfig),
                _mockLogger.Object);
            return (middleware, mockFactory);
        }

        /// <summary>
        /// Creates a <see cref="DefaultHttpContext"/> with the specified HTTP method, path,
        /// optional JSON body, and optional query string. Both request body (if provided) and
        /// response body are backed by <see cref="MemoryStream"/> instances for content capture.
        /// Default scheme is "https" and host is "gateway.example.com".
        /// </summary>
        /// <param name="method">The HTTP method (GET, POST, PUT, PATCH, DELETE).</param>
        /// <param name="path">The request path (e.g., "/api/v3/en_US/eql").</param>
        /// <param name="body">Optional JSON request body for POST/PUT/PATCH requests.</param>
        /// <param name="queryString">Optional query string including "?" prefix.</param>
        /// <returns>A configured <see cref="DefaultHttpContext"/> ready for middleware invocation.</returns>
        private static DefaultHttpContext CreateHttpContext(
            string method, string path, string body = null, string queryString = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("gateway.example.com");

            if (queryString != null)
            {
                context.Request.QueryString = new QueryString(queryString);
            }

            if (body != null)
            {
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bodyBytes);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bodyBytes.Length;
            }
            else
            {
                context.Request.Body = new MemoryStream();
            }

            // Use a MemoryStream for the response body to capture middleware output
            context.Response.Body = new MemoryStream();
            return context;
        }

        /// <summary>
        /// Reads the full response body from the context's response <see cref="MemoryStream"/>
        /// by seeking to the beginning and reading to end as UTF-8 text.
        /// </summary>
        /// <param name="context">The HTTP context whose response body to read.</param>
        /// <returns>The response body as a string.</returns>
        private static async Task<string> ReadResponseBodyAsync(HttpContext context)
        {
            context.Response.Body.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }

        /// <summary>A no-op <see cref="RequestDelegate"/> for tests that don't need next middleware behavior.</summary>
        private static RequestDelegate NoOpNext => _ => Task.CompletedTask;

        #endregion

        #region Phase 2: Route Matching Tests

        /// <summary>
        /// Verifies that a request to <c>/api/v3/en_US/eql</c> is forwarded to the Core service
        /// at <c>http://core-service:8080</c>. This is the primary EQL endpoint from the monolith's
        /// WebApiController, now routed to the Core Platform microservice.
        /// </summary>
        [Fact]
        public async Task Invoke_ApiV3EqlRoute_RoutesToCoreService()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/eql");

            // Act
            await middleware.Invoke(context);

            // Assert — request forwarded to Core service
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequest.RequestUri.ToString()
                .Should().StartWith("http://core-service:8080/api/v3/en_US/eql");
        }

        /// <summary>
        /// Verifies that SDK plugin routes (<c>/api/v3.0/p/sdk/*</c>) are forwarded
        /// to the Admin service at <c>http://admin-service:8080</c>.
        /// Extracted from the monolith's AdminController.cs endpoints.
        /// </summary>
        [Fact]
        public async Task Invoke_SdkPluginRoute_RoutesToAdminService()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3.0/p/sdk/entities");

            // Act
            await middleware.Invoke(context);

            // Assert — request forwarded to Admin service
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequest.RequestUri.ToString()
                .Should().StartWith("http://admin-service:8080/api/v3.0/p/sdk/entities");
        }

        /// <summary>
        /// Verifies that Project plugin routes (<c>/api/v3.0/p/project/*</c>) are forwarded
        /// to the Project service at <c>http://project-service:8080</c>.
        /// Extracted from the monolith's ProjectController.cs endpoints.
        /// </summary>
        [Fact]
        public async Task Invoke_ProjectPluginRoute_RoutesToProjectService()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3.0/p/project/tasks");

            // Act
            await middleware.Invoke(context);

            // Assert — request forwarded to Project service
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequest.RequestUri.ToString()
                .Should().StartWith("http://project-service:8080/api/v3.0/p/project/tasks");
        }

        /// <summary>
        /// Verifies that non-API requests (Razor pages, static files, local controllers)
        /// pass through to the next middleware without being routed to a backend service.
        /// The Gateway serves Razor Pages locally — only API-prefixed requests are forwarded.
        /// </summary>
        [Fact]
        public async Task Invoke_NonApiRequest_PassesToNextMiddleware()
        {
            // Arrange
            var nextCalled = false;
            RequestDelegate next = ctx => { nextCalled = true; return Task.CompletedTask; };
            var (middleware, mockFactory) = CreateMiddlewareNoBackend(next);
            var context = CreateHttpContext("GET", "/dashboard");

            // Act
            await middleware.Invoke(context);

            // Assert — next middleware was invoked and no HTTP client call was made
            nextCalled.Should().BeTrue("non-API requests must pass through to the next middleware");
            mockFactory.Verify(
                f => f.CreateClient(It.IsAny<string>()),
                Times.Never,
                "no HTTP client should be created for non-API requests");
        }

        /// <summary>
        /// Verifies that longest-prefix matching selects the most specific route.
        /// A request to <c>/api/v3.0/p/sdk/codegen</c> matches both <c>/api/v3/</c> (Core)
        /// and <c>/api/v3.0/p/sdk/</c> (Admin). The longer Admin prefix must win per
        /// <see cref="RouteConfiguration.FindMatchingRoute(string)"/> ordering.
        /// </summary>
        [Fact]
        public async Task Invoke_OverlappingPrefixes_LongestPrefixMatchWins()
        {
            // Arrange — request path matches both /api/v3/ and /api/v3.0/p/sdk/ prefixes
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3.0/p/sdk/codegen");

            // Act
            await middleware.Invoke(context);

            // Assert — Admin Service (longest prefix /api/v3.0/p/sdk/) wins over Core (/api/v3/)
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequest.RequestUri.ToString()
                .Should().StartWith("http://admin-service:8080/api/v3.0/p/sdk/codegen",
                    "the longer /api/v3.0/p/sdk/ prefix should match before the shorter /api/v3/ prefix");
        }

        #endregion

        #region Phase 3: Request Forwarding Tests

        /// <summary>
        /// Verifies that the Authorization header (JWT Bearer token) is forwarded to
        /// the backend service. CRITICAL for JWT propagation across service boundaries
        /// per AAP Section 0.8.3 — downstream services authorize requests using the
        /// forwarded JWT without callback to the Core service.
        /// </summary>
        [Fact]
        public async Task Invoke_AuthorizationHeader_ForwardedToBackend()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/eql");
            context.Request.Headers["Authorization"] = "Bearer my-jwt-token";

            // Act
            await middleware.Invoke(context);

            // Assert — Authorization header preserved for JWT propagation
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequest.Headers.Authorization.Should().NotBeNull();
            handler.CapturedRequest.Headers.Authorization.ToString()
                .Should().Be("Bearer my-jwt-token");
        }

        /// <summary>
        /// Verifies that the request body is forwarded for POST requests with JSON payloads.
        /// Validates that record creation/update operations (e.g., creating a user record
        /// via <c>/api/v3/{locale}/record/user</c>) have their payloads properly proxied
        /// to the backend service.
        /// </summary>
        [Fact]
        public async Task Invoke_PostWithJsonBody_BodyForwardedToBackend()
        {
            // Arrange
            var requestBody = "{\"name\":\"test\",\"value\":42}";
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("POST", "/api/v3/en_US/record/user", body: requestBody);

            // Act
            await middleware.Invoke(context);

            // Assert — request body forwarded intact to backend
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequestBody.Should().Be(requestBody);
            handler.CapturedRequest.Content.Should().NotBeNull();
            handler.CapturedRequest.Content.Headers.ContentType.MediaType
                .Should().Be("application/json");
        }

        /// <summary>
        /// Verifies that X-Forwarded-For, X-Forwarded-Proto, and X-Forwarded-Host headers
        /// are added to the forwarded request for proxy chain transparency. These headers
        /// are essential for backend services to know the original request context (client IP,
        /// scheme, and host) when behind the API Gateway.
        /// </summary>
        [Fact]
        public async Task Invoke_XForwardedHeaders_AddedToForwardedRequest()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/eql");
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("gateway.example.com");

            // Act
            await middleware.Invoke(context);

            // Assert — X-Forwarded-* headers present on forwarded request
            handler.CapturedRequest.Should().NotBeNull();
            var headers = handler.CapturedRequest.Headers;

            headers.TryGetValues("X-Forwarded-For", out var forwardedFor).Should().BeTrue();
            string.Join(",", forwardedFor).Should().Contain("192.168.1.100");

            headers.TryGetValues("X-Forwarded-Proto", out var forwardedProto).Should().BeTrue();
            string.Join(",", forwardedProto).Should().Contain("https");

            headers.TryGetValues("X-Forwarded-Host", out var forwardedHost).Should().BeTrue();
            string.Join(",", forwardedHost).Should().Contain("gateway.example.com");
        }

        #endregion

        #region Phase 4: Response Copy-Back Tests

        /// <summary>
        /// Verifies that the backend response (status code 200, Content-Type, and body) is
        /// copied back to the gateway's outgoing response. The response envelope must arrive
        /// intact to the client, preserving the BaseResponseModel format.
        /// </summary>
        [Fact]
        public async Task Invoke_BackendReturnsSuccess_ResponseCopiedBack()
        {
            // Arrange
            var backendBody = "{\"success\":true,\"object\":{\"id\":1}}";
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(backendBody, Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/record/user");

            // Act
            await middleware.Invoke(context);

            // Assert — status, content-type, and body copied back from backend
            context.Response.StatusCode.Should().Be(200);
            var responseBody = await ReadResponseBodyAsync(context);
            responseBody.Should().Contain("\"success\":true");
            responseBody.Should().Contain("\"id\":1");
            context.Response.Headers["Content-Type"].ToString()
                .Should().Contain("application/json");
        }

        /// <summary>
        /// Verifies that non-200 status codes from the backend (e.g., 404 Not Found) are
        /// preserved and forwarded unchanged to the gateway's client response.
        /// </summary>
        [Fact]
        public async Task Invoke_BackendReturnsNon200_StatusPreserved()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"success\":false}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/record/nonexistent");

            // Act
            await middleware.Invoke(context);

            // Assert — 404 status preserved from backend
            context.Response.StatusCode.Should().Be(404);
        }

        #endregion

        #region Phase 5: Backend Service Error Tests

        /// <summary>
        /// Verifies that when a backend service is unavailable (<see cref="HttpRequestException"/>),
        /// the middleware returns 502 Bad Gateway with a BaseResponseModel JSON envelope.
        /// Validates backward compatibility with the existing REST API v3 response shape
        /// per AAP Section 0.8.1: <c>{ success, errors, timestamp, message, object }</c>.
        /// </summary>
        [Fact]
        public async Task Invoke_BackendUnavailable_Returns502WithEnvelope()
        {
            // Arrange — handler throws HttpRequestException to simulate service down
            var handler = new MockHttpMessageHandler((_, __) =>
                Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Connection refused")));
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/eql");

            // Act
            await middleware.Invoke(context);

            // Assert — 502 with BaseResponseModel envelope
            context.Response.StatusCode.Should().Be(502);
            context.Response.ContentType.Should().Be("application/json");

            var body = await ReadResponseBodyAsync(context);
            body.Should().NotBeNullOrEmpty();

            var json = JObject.Parse(body);
            json["success"].Value<bool>().Should().BeFalse();
            json["message"].Value<string>().Should().NotBeNullOrEmpty();
            json["errors"].Should().NotBeNull();
            json["errors"].Type.Should().Be(JTokenType.Array);
            json["errors"][0]["message"].Value<string>().Should().Contain("unavailable");
            json["timestamp"].Should().NotBeNull();
            json["object"].Type.Should().Be(JTokenType.Null);
        }

        /// <summary>
        /// Verifies that when a backend service times out (<see cref="TaskCanceledException"/>
        /// not caused by client abort), the middleware returns 504 Gateway Timeout with a
        /// BaseResponseModel JSON envelope.
        /// </summary>
        [Fact]
        public async Task Invoke_BackendTimeout_Returns504WithEnvelope()
        {
            // Arrange — handler throws TaskCanceledException to simulate timeout.
            // DefaultHttpContext.RequestAborted is CancellationToken.None (not cancelled),
            // so the middleware's when-filter matches the timeout path, not client abort.
            var handler = new MockHttpMessageHandler((_, __) =>
                Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException("The operation was canceled due to timeout")));
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/eql");

            // Act
            await middleware.Invoke(context);

            // Assert — 504 with BaseResponseModel envelope
            context.Response.StatusCode.Should().Be(504);
            context.Response.ContentType.Should().Be("application/json");

            var body = await ReadResponseBodyAsync(context);
            body.Should().NotBeNullOrEmpty();

            var json = JObject.Parse(body);
            json["success"].Value<bool>().Should().BeFalse();
            json["message"].Value<string>().Should().NotBeNullOrEmpty();
            json["errors"].Should().NotBeNull();
            json["errors"].Type.Should().Be(JTokenType.Array);
            json["errors"][0]["message"].Value<string>().Should().Contain("timed out");
            json["timestamp"].Should().NotBeNull();
            json["object"].Type.Should().Be(JTokenType.Null);
        }

        #endregion

        #region Phase 6: Query String Preservation Tests

        /// <summary>
        /// Verifies that query string parameters are forwarded to the backend service.
        /// EQL endpoints use query strings extensively for query specification, pagination,
        /// and filtering. The full query string must be preserved when forwarding.
        /// </summary>
        [Fact]
        public async Task Invoke_QueryStringPresent_ForwardedToBackend()
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext("GET", "/api/v3/en_US/eql",
                queryString: "?query=entity&limit=10&offset=0");

            // Act
            await middleware.Invoke(context);

            // Assert — full query string forwarded to backend
            handler.CapturedRequest.Should().NotBeNull();
            var forwardedUrl = handler.CapturedRequest.RequestUri.ToString();
            forwardedUrl.Should().Contain("query=entity");
            forwardedUrl.Should().Contain("limit=10");
            forwardedUrl.Should().Contain("offset=0");
        }

        #endregion

        #region Phase 7: HTTP Method Tests

        /// <summary>
        /// Verifies that the correct HTTP method is forwarded to the backend service
        /// for each of the standard REST methods (GET, POST, PUT, PATCH, DELETE).
        /// The middleware must preserve the original method for proper CRUD operation
        /// mapping at the backend service.
        /// </summary>
        /// <param name="httpMethod">The HTTP method to test.</param>
        [Theory]
        [InlineData("GET")]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("PATCH")]
        [InlineData("DELETE")]
        public async Task Invoke_HttpMethods_CorrectMethodForwarded(string httpMethod)
        {
            // Arrange
            var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
            });
            var middleware = CreateMiddleware(NoOpNext, handler);
            var context = CreateHttpContext(httpMethod, "/api/v3/en_US/record/user");

            // Act
            await middleware.Invoke(context);

            // Assert — HTTP method preserved in forwarded request
            handler.CapturedRequest.Should().NotBeNull();
            handler.CapturedRequest.Method.ToString()
                .Should().Be(httpMethod,
                    $"the {httpMethod} method should be forwarded unchanged to the backend service");
        }

        #endregion
    }
}
