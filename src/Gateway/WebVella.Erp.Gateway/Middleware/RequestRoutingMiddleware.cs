using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using WebVella.Erp.Gateway.Configuration;

namespace WebVella.Erp.Gateway.Middleware
{
    /// <summary>
    /// Domain-based request routing middleware implementing the Strangler Fig pattern
    /// (AAP Section 0.4.3). Routes incoming <c>/api/v3/{locale}/...</c> requests to
    /// the appropriate backend microservice based on URL pattern matching using
    /// <see cref="RouteConfiguration.FindMatchingRoute(string)"/>.
    ///
    /// This is the core routing engine for the API Gateway that preserves backward
    /// compatibility with the existing REST API v3 contract (AAP Section 0.8.1).
    ///
    /// Non-API requests (Razor Pages, static files, local controllers) are passed
    /// through to the next middleware in the pipeline.
    ///
    /// Error responses from backend services or routing failures are wrapped in the
    /// <c>BaseResponseModel</c> envelope format <c>{ success, errors, timestamp, message, object }</c>
    /// for backward compatibility.
    ///
    /// The middleware does NOT access any database directly — it is a pure routing proxy.
    /// </summary>
    public class RequestRoutingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly RouteConfiguration _routeConfig;
        private readonly ILogger<RequestRoutingMiddleware> _logger;

        /// <summary>
        /// Request headers that should not be copied from the incoming request to the forwarded
        /// <see cref="HttpRequestMessage"/>. Host is excluded because the HttpClient sets it
        /// from the target URI. Content-Length and Content-Type are set on the content object.
        /// Transfer-Encoding is a hop-by-hop header.
        /// </summary>
        private static readonly HashSet<string> ExcludedRequestHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Host",
                "Content-Length",
                "Content-Type",
                "Transfer-Encoding"
            };

        /// <summary>
        /// Response headers that should not be copied from the backend response to the gateway
        /// response. Transfer-Encoding and Content-Length are excluded to let Kestrel handle
        /// chunked transfer encoding and content length computation.
        /// </summary>
        private static readonly HashSet<string> ExcludedResponseHeaders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Transfer-Encoding",
                "Content-Length"
            };

        /// <summary>
        /// Initializes a new instance of the <see cref="RequestRoutingMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next middleware delegate in the pipeline.</param>
        /// <param name="httpClientFactory">
        /// Factory for creating managed <see cref="HttpClient"/> instances for backend service calls.
        /// Avoids socket exhaustion by reusing underlying <see cref="HttpMessageHandler"/> instances.
        /// </param>
        /// <param name="routeConfig">
        /// Route configuration bound from the <c>ServiceRoutes</c> section of <c>appsettings.json</c>.
        /// Provides <see cref="RouteConfiguration.FindMatchingRoute(string)"/> for URL-to-service resolution.
        /// </param>
        /// <param name="logger">Structured logger for recording routing decisions and error conditions.</param>
        public RequestRoutingMiddleware(
            RequestDelegate next,
            IHttpClientFactory httpClientFactory,
            IOptions<RouteConfiguration> routeConfig,
            ILogger<RequestRoutingMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _routeConfig = (routeConfig ?? throw new ArgumentNullException(nameof(routeConfig))).Value;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes an incoming HTTP request by determining whether it should be forwarded
        /// to a backend microservice or passed through to the next middleware.
        ///
        /// Routing logic:
        /// 1. Extract the request path.
        /// 2. Use <see cref="RouteConfiguration.FindMatchingRoute(string)"/> for longest-prefix matching.
        /// 3. If no match: pass through to next middleware (Razor Pages, static files).
        /// 4. If a match is found: attempt API composition, then forward to the matched backend service.
        /// </summary>
        /// <param name="context">The current HTTP context.</param>
        public async Task Invoke(HttpContext context)
        {
            var requestPath = context.Request.Path.Value;

            // Attempt to find a matching route for the incoming request path
            var routeMatch = _routeConfig.FindMatchingRoute(requestPath);

            if (routeMatch == null)
            {
                // No matching route found — pass through to the next middleware.
                // This allows Razor Pages, static files, and local controllers to be
                // served directly by the Gateway.
                await _next(context);
                return;
            }

            var (serviceKey, serviceUrl) = routeMatch.Value;

            _logger.LogInformation(
                "Routing request {Method} {Path} to backend service {ServiceKey} at {ServiceUrl}",
                context.Request.Method,
                requestPath,
                serviceKey,
                serviceUrl);

            // Check if this request requires cross-service API composition
            // (e.g., cross-service EQL queries spanning CRM + Project entities)
            if (await TryHandleApiComposition(context, requestPath))
            {
                return;
            }

            // Forward the request to the matched backend microservice
            await ForwardRequestAsync(context, serviceUrl);
        }

        /// <summary>
        /// Forwards the incoming HTTP request to the specified backend microservice URL.
        /// Copies the HTTP method, headers (including Authorization for JWT propagation per
        /// AAP Section 0.8.3), request body, and returns the backend response to the client.
        ///
        /// Error handling:
        /// - <see cref="HttpRequestException"/>: 502 Bad Gateway (backend unreachable)
        /// - <see cref="TaskCanceledException"/> (non-client): 504 Gateway Timeout
        /// - <see cref="TaskCanceledException"/> (client abort): logged, no response written
        /// - Any other <see cref="Exception"/>: 500 Internal Server Error
        ///
        /// All error responses use the <c>BaseResponseModel</c> envelope format.
        /// </summary>
        private async Task ForwardRequestAsync(HttpContext context, string serviceUrl)
        {
            try
            {
                // Enable request body buffering so the body stream can be read and forwarded.
                // This is critical for POST/PUT/PATCH requests where the body must be copied
                // to the outgoing HttpRequestMessage.
                context.Request.EnableBuffering();

                // Build the target URI combining the backend service base URL with the
                // original request path and query string
                var targetUri = BuildTargetUri(serviceUrl, context.Request);

                // Create the forwarded request with the same HTTP method
                using var forwardedRequest = new HttpRequestMessage(
                    new HttpMethod(context.Request.Method),
                    targetUri);

                // Copy all applicable request headers (Authorization, Accept, etc.)
                CopyRequestHeaders(context.Request, forwardedRequest);

                // Copy request body for methods that typically carry a payload.
                // The body is fully buffered into a MemoryStream before creating the content
                // to avoid Content-Length mismatches with FileBufferingReadStream, which may
                // report Length=0 if the body has not yet been consumed by earlier middleware.
                if (HasRequestBody(context.Request.Method))
                {
                    context.Request.Body.Position = 0;
                    var bodyStream = new MemoryStream();
                    await context.Request.Body.CopyToAsync(bodyStream);
                    bodyStream.Position = 0;
                    forwardedRequest.Content = new StreamContent(bodyStream);

                    // Set Content-Type on the content object (not on request headers, since
                    // it was excluded from header copying to avoid duplication)
                    if (!string.IsNullOrEmpty(context.Request.ContentType))
                    {
                        forwardedRequest.Content.Headers.TryAddWithoutValidation(
                            "Content-Type", context.Request.ContentType);
                    }
                }

                // Send the request to the backend service using a managed HttpClient
                // from the IHttpClientFactory to avoid socket exhaustion
                using var httpClient = _httpClientFactory.CreateClient("default");
                using var backendResponse = await httpClient.SendAsync(
                    forwardedRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted);

                // Copy the backend response (status, headers, body) back to the gateway response
                await CopyResponseAsync(backendResponse, context.Response);
            }
            catch (HttpRequestException ex)
            {
                // Backend service is unreachable or returned a connection-level error
                _logger.LogError(
                    ex,
                    "Backend service unreachable: {Method} {Path} → {ServiceUrl}",
                    context.Request.Method,
                    context.Request.Path,
                    serviceUrl);

                await WriteErrorResponseAsync(
                    context.Response,
                    statusCode: 502,
                    errorMessage: "Backend service unavailable",
                    responseMessage: "Service routing error");
            }
            catch (TaskCanceledException ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                // The request to the backend service timed out (not a client cancellation)
                _logger.LogWarning(
                    ex,
                    "Request to backend service timed out: {Method} {Path} → {ServiceUrl}",
                    context.Request.Method,
                    context.Request.Path,
                    serviceUrl);

                await WriteErrorResponseAsync(
                    context.Response,
                    statusCode: 504,
                    errorMessage: "Backend service request timed out",
                    responseMessage: "Gateway timeout");
            }
            catch (TaskCanceledException)
            {
                // Client cancelled the request — no response needed.
                // This occurs when the caller aborts the HTTP connection.
                _logger.LogInformation(
                    "Client cancelled request: {Method} {Path}",
                    context.Request.Method,
                    context.Request.Path);
            }
            catch (Exception ex)
            {
                // Unexpected error during request forwarding
                _logger.LogError(
                    ex,
                    "Unexpected error forwarding request: {Method} {Path} → {ServiceUrl}",
                    context.Request.Method,
                    context.Request.Path,
                    serviceUrl);

                await WriteErrorResponseAsync(
                    context.Response,
                    statusCode: 500,
                    errorMessage: "An unexpected error occurred while processing the request",
                    responseMessage: "Internal gateway error");
            }
        }

        /// <summary>
        /// Builds the target URI by combining the backend service base URL with the original
        /// request path and query string.
        /// Example: <c>"http://core-service:8080"</c> + <c>"/api/v3/en_US/eql"</c> + <c>"?query=..."</c>
        ///   → <c>"http://core-service:8080/api/v3/en_US/eql?query=..."</c>
        /// </summary>
        /// <param name="serviceUrl">The backend service base URL.</param>
        /// <param name="request">The incoming HTTP request.</param>
        /// <returns>The fully-qualified target URI.</returns>
        private static Uri BuildTargetUri(string serviceUrl, HttpRequest request)
        {
            var baseUri = serviceUrl.TrimEnd('/');
            var path = request.Path.Value ?? string.Empty;
            var query = request.QueryString.Value ?? string.Empty;
            return new Uri($"{baseUri}{path}{query}");
        }

        /// <summary>
        /// Copies request headers from the incoming <see cref="HttpRequest"/> to the forwarded
        /// <see cref="HttpRequestMessage"/>. Skips hop-by-hop headers (Host, Content-Length,
        /// Content-Type, Transfer-Encoding) and adds X-Forwarded-* headers for proxy chain
        /// transparency.
        ///
        /// The Authorization header is explicitly preserved for JWT token propagation across
        /// service boundaries (AAP Section 0.8.3).
        /// </summary>
        /// <param name="source">The incoming gateway HTTP request.</param>
        /// <param name="destination">The outgoing HTTP request message to the backend service.</param>
        private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
        {
            foreach (var header in source.Headers)
            {
                // Skip headers that are set elsewhere or are hop-by-hop
                if (ExcludedRequestHeaders.Contains(header.Key))
                {
                    continue;
                }

                // Copy all other headers including Authorization (critical for JWT propagation)
                destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            // Add X-Forwarded-For with the remote client IP address
            var remoteIpAddress = source.HttpContext.Connection.RemoteIpAddress;
            if (remoteIpAddress != null)
            {
                destination.Headers.TryAddWithoutValidation(
                    "X-Forwarded-For", remoteIpAddress.ToString());
            }

            // Add X-Forwarded-Proto with the original request scheme (http/https)
            destination.Headers.TryAddWithoutValidation(
                "X-Forwarded-Proto", source.Scheme);

            // Add X-Forwarded-Host with the original Host header value
            destination.Headers.TryAddWithoutValidation(
                "X-Forwarded-Host", source.Host.Value);
        }

        /// <summary>
        /// Copies the backend service response (status code, headers, and body) back to the
        /// gateway's outgoing <see cref="HttpResponse"/>.
        ///
        /// Transfer-Encoding and Content-Length headers are excluded from copying to allow
        /// Kestrel to handle chunked transfer encoding and content length recomputation.
        /// </summary>
        /// <param name="sourceResponse">The response received from the backend service.</param>
        /// <param name="destinationResponse">The gateway's outgoing HTTP response to the client.</param>
        private static async Task CopyResponseAsync(
            HttpResponseMessage sourceResponse,
            HttpResponse destinationResponse)
        {
            // Set the status code from the backend response
            destinationResponse.StatusCode = (int)sourceResponse.StatusCode;

            // Copy response headers from the backend (skip hop-by-hop headers)
            foreach (var header in sourceResponse.Headers)
            {
                if (ExcludedResponseHeaders.Contains(header.Key))
                {
                    continue;
                }

                destinationResponse.Headers[header.Key] = header.Value.ToArray();
            }

            // Copy content headers (especially Content-Type) from the backend response
            foreach (var header in sourceResponse.Content.Headers)
            {
                if (ExcludedResponseHeaders.Contains(header.Key))
                {
                    continue;
                }

                destinationResponse.Headers[header.Key] = header.Value.ToArray();
            }

            // Stream the response body from the backend to the client
            await sourceResponse.Content.CopyToAsync(destinationResponse.Body);
        }

        /// <summary>
        /// Writes a standardized error response in the <c>BaseResponseModel</c> envelope format.
        /// Preserves backward compatibility with the existing REST API v3 response shape
        /// (AAP Section 0.8.1):
        /// <code>
        /// {
        ///   "success": false,
        ///   "timestamp": "2024-01-01T00:00:00Z",
        ///   "errors": [{ "key": null, "value": null, "message": "..." }],
        ///   "message": "...",
        ///   "object": null
        /// }
        /// </code>
        /// Uses Newtonsoft.Json for serialization to maintain consistency with the monolith
        /// (AAP Section 0.8.2).
        /// </summary>
        /// <param name="response">The gateway HTTP response to write to.</param>
        /// <param name="statusCode">The HTTP status code (e.g., 502, 504, 500).</param>
        /// <param name="errorMessage">The error message to include in the errors array.</param>
        /// <param name="responseMessage">The top-level message for the response envelope.</param>
        private static async Task WriteErrorResponseAsync(
            HttpResponse response,
            int statusCode,
            string errorMessage,
            string responseMessage)
        {
            // Guard against writing to a response that has already started streaming
            if (response.HasStarted)
            {
                return;
            }

            response.StatusCode = statusCode;
            response.ContentType = "application/json";

            // Construct the error response matching the BaseResponseModel envelope shape:
            // { success, errors, timestamp, message, object }
            // ErrorModel shape: { key, value, message }
            var errorResponseBody = new
            {
                success = false,
                timestamp = DateTime.UtcNow,
                errors = new[]
                {
                    new
                    {
                        key = (string)null,
                        value = (string)null,
                        message = errorMessage
                    }
                },
                message = responseMessage,
                @object = (object)null
            };

            var jsonResponse = JsonConvert.SerializeObject(errorResponseBody);
            await response.WriteAsync(jsonResponse);
        }

        /// <summary>
        /// Extension point for cross-service EQL query composition (AAP Section 0.7.3).
        ///
        /// In the current implementation, all requests are forwarded directly to a single
        /// backend service. When cross-service EQL query composition is needed, this method
        /// will be enhanced to:
        /// 1. Detect cross-service EQL queries (queries joining entities across CRM + Project, etc.)
        /// 2. Split the query into per-service sub-queries
        /// 3. Execute sub-queries via gRPC to individual services
        /// 4. Compose the results and return a unified response
        ///
        /// Returns <c>false</c> to indicate that composition was not handled and the request
        /// should proceed with standard single-service forwarding.
        /// </summary>
        /// <param name="context">The current HTTP context.</param>
        /// <param name="requestPath">The incoming request path.</param>
        /// <returns>
        /// <c>true</c> if the request was handled by API composition; <c>false</c> to proceed
        /// with standard single-service forwarding.
        /// </returns>
        private Task<bool> TryHandleApiComposition(HttpContext context, string requestPath)
        {
            // TODO: Implement cross-service EQL query composition per AAP Section 0.7.3
            // Currently all requests are forwarded directly to a single backend service.
            return Task.FromResult(false);
        }

        /// <summary>
        /// Determines whether the given HTTP method typically carries a request body
        /// that needs to be forwarded to the backend service.
        /// </summary>
        /// <param name="method">The HTTP method string (e.g., "POST", "PUT", "PATCH", "DELETE").</param>
        /// <returns><c>true</c> if the method may carry a body; otherwise <c>false</c>.</returns>
        private static bool HasRequestBody(string method)
        {
            return string.Equals(method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, HttpMethod.Put.Method, StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, HttpMethod.Patch.Method, StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);
        }
    }
}
