using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        #region << Cross-Service EQL Query Composition (AAP Section 0.7.3) >>

        /// <summary>
        /// Entity-to-service ownership map per AAP Section 0.7.1.
        /// Maps each entity system name to the backend service property key that owns it.
        /// Used to determine which service should handle an EQL query's primary entity and
        /// to detect cross-service relation references that require API composition.
        /// </summary>
        private static readonly Dictionary<string, string> EntityServiceOwnershipMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Core Platform service entities (erp_core database)
                { "user",         nameof(RouteConfiguration.CoreServiceUrl) },
                { "role",         nameof(RouteConfiguration.CoreServiceUrl) },
                { "user_file",    nameof(RouteConfiguration.CoreServiceUrl) },
                { "language",     nameof(RouteConfiguration.CoreServiceUrl) },
                { "currency",     nameof(RouteConfiguration.CoreServiceUrl) },
                { "country",      nameof(RouteConfiguration.CoreServiceUrl) },

                // CRM service entities (erp_crm database)
                { "account",      nameof(RouteConfiguration.CrmServiceUrl) },
                { "contact",      nameof(RouteConfiguration.CrmServiceUrl) },
                { "case",         nameof(RouteConfiguration.CrmServiceUrl) },
                { "address",      nameof(RouteConfiguration.CrmServiceUrl) },
                { "salutation",   nameof(RouteConfiguration.CrmServiceUrl) },

                // Project/Task service entities (erp_project database)
                { "task",         nameof(RouteConfiguration.ProjectServiceUrl) },
                { "timelog",      nameof(RouteConfiguration.ProjectServiceUrl) },
                { "comment",      nameof(RouteConfiguration.ProjectServiceUrl) },
                { "task_type",    nameof(RouteConfiguration.ProjectServiceUrl) },

                // Mail/Notification service entities (erp_mail database)
                { "email",        nameof(RouteConfiguration.MailServiceUrl) },
                { "smtp_service", nameof(RouteConfiguration.MailServiceUrl) },
            };

        /// <summary>
        /// Compiled regex matching the EQL endpoint URL patterns from the Gateway route configuration.
        /// Matches: <c>/api/v3/{locale}/eql</c>, <c>/api/v3/{locale}/eql-ds</c>,
        /// <c>/api/v3/{locale}/eql-ds-select2</c>.
        /// </summary>
        private static readonly Regex EqlEndpointPattern =
            new Regex(@"^/api/v3/[^/]+/eql(-ds(-select2)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Compiled regex extracting the primary entity name from an EQL FROM clause.
        /// Matches: <c>FROM entityname</c> (case-insensitive, word boundaries).
        /// Captures the entity system name (group 1).
        /// </summary>
        private static readonly Regex EqlFromClausePattern =
            new Regex(@"\bFROM\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Compiled regex detecting EQL relation field references (<c>$relation_name.field</c>
        /// or <c>$$relation_name.field</c>). Used to identify cross-service relation traversals.
        /// Captures the relation name (group 1) and optionally the field name (group 2).
        /// </summary>
        private static readonly Regex EqlRelationReferencePattern =
            new Regex(@"\$\$?(\w+)\.(\w+)", RegexOptions.Compiled);

        /// <summary>
        /// Implements cross-service EQL query composition per AAP Section 0.7.3.
        ///
        /// Composition strategy:
        /// <list type="number">
        ///   <item>Detect EQL query endpoints (<c>/api/v3/{locale}/eql*</c>).</item>
        ///   <item>Parse the EQL text from the POST body to extract the primary entity name.</item>
        ///   <item>Determine the owning service via <see cref="EntityServiceOwnershipMap"/>.</item>
        ///   <item>If the owning service differs from the default route target (Core), forward
        ///         the request to the correct service's base URL.</item>
        ///   <item>Detect cross-service <c>$relation</c> references by mapping relation target
        ///         entities to their owning services.</item>
        ///   <item>For cross-service relations: execute the primary query against the owning
        ///         service (local fields only), then resolve cross-service relation data via
        ///         secondary HTTP calls to the related services, and compose the unified response.</item>
        /// </list>
        ///
        /// Returns <c>false</c> for non-EQL requests or when no composition is needed
        /// (all entities belong to the same service).
        /// </summary>
        /// <param name="context">The current HTTP context.</param>
        /// <param name="requestPath">The incoming request path.</param>
        /// <returns>
        /// <c>true</c> if the request was handled by API composition; <c>false</c> to proceed
        /// with standard single-service forwarding.
        /// </returns>
        private async Task<bool> TryHandleApiComposition(HttpContext context, string requestPath)
        {
            // Step 1: Only intercept EQL query endpoints
            if (!EqlEndpointPattern.IsMatch(requestPath))
            {
                return false;
            }

            // Step 2: Read and buffer the request body to extract EQL text.
            // EnableBuffering allows the body to be re-read by ForwardRequestAsync if
            // composition is not needed.
            context.Request.EnableBuffering();
            string requestBody;
            using (var reader = new StreamReader(
                context.Request.Body,
                leaveOpen: true))
            {
                requestBody = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }

            if (string.IsNullOrWhiteSpace(requestBody))
            {
                return false;
            }

            // Step 3: Extract the EQL text from the request body.
            // The EQL endpoint accepts: { "eql": "SELECT ... FROM entity ..." }
            // or form-encoded text where the EQL is in a "queryText" or "eql" field.
            var eqlText = ExtractEqlTextFromBody(requestBody);
            if (string.IsNullOrWhiteSpace(eqlText))
            {
                return false;
            }

            // Step 4: Parse the primary entity name from the EQL FROM clause.
            var entityName = ExtractEntityNameFromEql(eqlText);
            if (string.IsNullOrWhiteSpace(entityName))
            {
                return false;
            }

            // Step 5: Determine which service owns the primary entity.
            var owningServiceKey = GetOwningServiceKey(entityName);

            // Step 6: Detect cross-service $relation references in the EQL.
            var crossServiceRelations = DetectCrossServiceRelations(eqlText, owningServiceKey);

            // Step 7: Determine if routing or composition is needed.
            var isDefaultRoute = string.Equals(
                owningServiceKey,
                nameof(RouteConfiguration.CoreServiceUrl),
                StringComparison.Ordinal);

            if (isDefaultRoute && crossServiceRelations.Count == 0)
            {
                // Primary entity is Core-owned and no cross-service relations detected.
                // Standard forwarding to Core service handles this correctly.
                return false;
            }

            // Step 8: Entity is owned by a non-Core service or has cross-service relations.
            // Forward the primary query to the owning service.
            var owningServiceUrl = _routeConfig.GetServiceUrl(owningServiceKey);

            _logger.LogInformation(
                "EQL API composition: entity '{EntityName}' owned by {OwningService}, " +
                "cross-service relations detected: {RelationCount}",
                entityName,
                owningServiceKey,
                crossServiceRelations.Count);

            if (crossServiceRelations.Count == 0)
            {
                // No cross-service relations — forward the EQL request to the owning service.
                // The owning service's RecordManager handles EQL against its own database.
                await ForwardRequestAsync(context, owningServiceUrl);
                return true;
            }

            // Step 9: Cross-service composition needed. Execute primary query against the
            // owning service, then resolve cross-service relation data.
            return await ExecuteComposedEqlQuery(
                context, owningServiceUrl, requestBody,
                entityName, owningServiceKey, crossServiceRelations);
        }

        /// <summary>
        /// Extracts the EQL query text from the request body. Supports both JSON format
        /// (<c>{ "queryText": "..." }</c> or <c>{ "eql": "..." }</c>) and plain-text EQL.
        /// </summary>
        private static string ExtractEqlTextFromBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;

            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    var json = JObject.Parse(body);
                    // Core's RecordController expects "queryText" field
                    var eqlText = json["queryText"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(eqlText))
                        return eqlText;

                    // Alternative field names
                    eqlText = json["eql"]?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(eqlText))
                        return eqlText;

                    eqlText = json["eqlText"]?.Value<string>();
                    return eqlText;
                }
                catch (JsonReaderException)
                {
                    return null;
                }
            }

            // Plain-text EQL (unlikely but defensive)
            return body;
        }

        /// <summary>
        /// Extracts the primary entity system name from the EQL FROM clause.
        /// Example: <c>"SELECT *, $user_created_by.username FROM account WHERE ..."</c> → <c>"account"</c>.
        /// </summary>
        private static string ExtractEntityNameFromEql(string eqlText)
        {
            var match = EqlFromClausePattern.Match(eqlText);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Returns the service property key that owns the given entity.
        /// Unknown entities default to <see cref="RouteConfiguration.CoreServiceUrl"/>
        /// since Core manages entity metadata definitions for all services.
        /// </summary>
        private static string GetOwningServiceKey(string entityName)
        {
            return EntityServiceOwnershipMap.TryGetValue(entityName, out var serviceKey)
                ? serviceKey
                : nameof(RouteConfiguration.CoreServiceUrl);
        }

        /// <summary>
        /// Detects EQL <c>$relation</c> and <c>$$relation</c> field references whose target
        /// entities belong to a different service than <paramref name="primaryServiceKey"/>.
        ///
        /// Relation names follow the convention: <c>{targetEntity}_{sourceEntity}_{fieldName}</c>
        /// (e.g., <c>user_account_created_by</c>) or <c>{entity}_{relation}</c>. The first segment
        /// before the first underscore is extracted as the potential target entity name.
        /// </summary>
        /// <returns>
        /// A list of tuples containing the relation name, detected target entity, and the
        /// owning service key for each cross-service relation.
        /// </returns>
        private static List<(string relationName, string targetEntity, string targetServiceKey)>
            DetectCrossServiceRelations(string eqlText, string primaryServiceKey)
        {
            var crossServiceRelations = new List<(string relationName, string targetEntity, string targetServiceKey)>();
            var matches = EqlRelationReferencePattern.Matches(eqlText);

            foreach (Match match in matches)
            {
                var relationName = match.Groups[1].Value;

                // Extract the target entity from the relation name convention.
                // Common patterns: "user_account_created_by" → target entity "user",
                // "account_task" → target entity "account".
                // The first segment (before first underscore) is typically the target entity.
                var targetEntity = relationName.Contains('_')
                    ? relationName.Substring(0, relationName.IndexOf('_'))
                    : relationName;

                // Check if the target entity's owning service differs from the primary entity's service
                var targetServiceKey = GetOwningServiceKey(targetEntity);
                if (!string.Equals(targetServiceKey, primaryServiceKey, StringComparison.Ordinal)
                    && EntityServiceOwnershipMap.ContainsKey(targetEntity))
                {
                    crossServiceRelations.Add((relationName, targetEntity, targetServiceKey));
                }
            }

            return crossServiceRelations;
        }

        /// <summary>
        /// Executes a composed cross-service EQL query by:
        /// <list type="number">
        ///   <item>Forwarding the primary query to the owning service and reading the response.</item>
        ///   <item>Extracting cross-service foreign key values from the primary results.</item>
        ///   <item>Making secondary HTTP requests to related services to resolve referenced data.</item>
        ///   <item>Merging the resolved cross-service data into the primary response.</item>
        ///   <item>Returning the composed response to the client.</item>
        /// </list>
        ///
        /// If the primary query fails, the error response is returned directly without composition.
        /// If secondary resolution calls fail, the primary results are returned with null values
        /// for cross-service fields (graceful degradation per AAP 0.7.3).
        /// </summary>
        private async Task<bool> ExecuteComposedEqlQuery(
            HttpContext context,
            string owningServiceUrl,
            string requestBody,
            string entityName,
            string owningServiceKey,
            List<(string relationName, string targetEntity, string targetServiceKey)> crossServiceRelations)
        {
            try
            {
                // Execute the primary query against the owning service
                using var httpClient = _httpClientFactory.CreateClient("default");

                var targetUri = BuildTargetUri(owningServiceUrl, context.Request);
                using var primaryRequest = new HttpRequestMessage(
                    new HttpMethod(context.Request.Method), targetUri);
                CopyRequestHeaders(context.Request, primaryRequest);

                // Set the request body on the primary request
                primaryRequest.Content = new StringContent(
                    requestBody,
                    System.Text.Encoding.UTF8,
                    context.Request.ContentType ?? "application/json");

                using var primaryResponse = await httpClient.SendAsync(
                    primaryRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                var primaryBody = await primaryResponse.Content.ReadAsStringAsync(context.RequestAborted);

                // If the primary query failed, return the error directly
                if (!primaryResponse.IsSuccessStatusCode)
                {
                    context.Response.StatusCode = (int)primaryResponse.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(primaryBody, context.RequestAborted);
                    return true;
                }

                // Attempt to compose cross-service relation data into the primary results
                var composedBody = await ComposeRelationData(
                    httpClient, context, primaryBody, crossServiceRelations);

                // Return the composed response
                context.Response.StatusCode = (int)primaryResponse.StatusCode;
                foreach (var header in primaryResponse.Headers)
                {
                    if (!ExcludedResponseHeaders.Contains(header.Key))
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                }
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(composedBody, context.RequestAborted);
                return true;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex,
                    "Cross-service EQL composition failed for entity '{Entity}': backend unreachable",
                    entityName);
                await WriteErrorResponseAsync(context.Response, 502,
                    $"Backend service for entity '{entityName}' is unavailable",
                    "Cross-service EQL composition error");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error during cross-service EQL composition for entity '{Entity}'",
                    entityName);
                await WriteErrorResponseAsync(context.Response, 500,
                    "An error occurred during cross-service query composition",
                    "EQL composition error");
                return true;
            }
        }

        /// <summary>
        /// Enriches the primary query response with cross-service relation data.
        /// For each cross-service relation reference, extracts the foreign key IDs from the
        /// primary results and makes HTTP requests to the owning service to resolve the
        /// related entity records. Resolved data is merged into the primary response under
        /// the <c>$relation_name</c> field of each record.
        ///
        /// Graceful degradation: if resolution fails for any relation, the primary data is
        /// returned with null values for that relation field. No exception is thrown.
        /// </summary>
        private async Task<string> ComposeRelationData(
            HttpClient httpClient,
            HttpContext context,
            string primaryBody,
            List<(string relationName, string targetEntity, string targetServiceKey)> crossServiceRelations)
        {
            JObject primaryJson;
            try
            {
                primaryJson = JObject.Parse(primaryBody);
            }
            catch (JsonReaderException)
            {
                return primaryBody; // Not JSON — return as-is
            }

            // Extract records from the response object
            var objectToken = primaryJson["object"];
            if (objectToken == null || objectToken.Type == JTokenType.Null)
            {
                return primaryBody; // No data to compose
            }

            // The response object may be a single record or an array of records
            JArray records;
            if (objectToken is JArray arr)
            {
                records = arr;
            }
            else if (objectToken is JObject obj && obj["data"] is JArray dataArr)
            {
                records = dataArr;
            }
            else
            {
                return primaryBody; // Unrecognized structure — return as-is
            }

            if (records.Count == 0)
            {
                return primaryBody; // No records to enrich
            }

            // For each cross-service relation, resolve related entity data
            foreach (var (relationName, targetEntity, targetServiceKey) in crossServiceRelations)
            {
                try
                {
                    // Extract foreign key values from the primary results.
                    // Relation fields typically use the pattern: "{relation_name}_id" or "created_by" / "last_modified_by"
                    var fkFieldName = relationName.Contains("created_by") ? "created_by"
                        : relationName.Contains("modified_by") ? "last_modified_by"
                        : $"{relationName}_id";

                    var foreignKeyIds = new HashSet<string>();
                    foreach (var record in records)
                    {
                        var fkValue = record[fkFieldName]?.ToString();
                        if (!string.IsNullOrEmpty(fkValue))
                        {
                            foreignKeyIds.Add(fkValue);
                        }
                    }

                    if (foreignKeyIds.Count == 0)
                    {
                        continue; // No FK values to resolve
                    }

                    // Resolve the related records from the owning service via HTTP
                    var resolvedEntities = await ResolveRelatedEntities(
                        httpClient, context, targetEntity, targetServiceKey, foreignKeyIds);

                    if (resolvedEntities == null || resolvedEntities.Count == 0)
                    {
                        continue; // Resolution failed or no data — graceful degradation
                    }

                    // Merge resolved data into each primary record under $relation_name
                    foreach (var record in records)
                    {
                        if (record is JObject recordObj)
                        {
                            var fkValue = recordObj[fkFieldName]?.ToString();
                            if (!string.IsNullOrEmpty(fkValue) &&
                                resolvedEntities.TryGetValue(fkValue, out var relatedData))
                            {
                                recordObj[$"${relationName}"] = relatedData;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Graceful degradation: log the error but continue with other relations
                    _logger.LogWarning(ex,
                        "Failed to resolve cross-service relation '{Relation}' to entity '{Entity}' " +
                        "during EQL composition. Primary results returned without this relation data.",
                        relationName, targetEntity);
                }
            }

            return primaryJson.ToString(Formatting.None);
        }

        /// <summary>
        /// Resolves related entity records from a target service by their IDs.
        /// Makes an HTTP GET request to the target service to fetch the records.
        /// Returns a dictionary mapping entity record IDs to their JSON data.
        ///
        /// For <c>user</c> entity resolution (the most common cross-service relation via
        /// audit fields <c>created_by</c>/<c>last_modified_by</c>), this calls the Core
        /// service's user endpoint.
        /// </summary>
        private async Task<Dictionary<string, JToken>> ResolveRelatedEntities(
            HttpClient httpClient,
            HttpContext context,
            string entityName,
            string serviceKey,
            HashSet<string> entityIds)
        {
            var result = new Dictionary<string, JToken>();

            try
            {
                var serviceUrl = _routeConfig.GetServiceUrl(serviceKey);

                // Resolve each entity by ID. For production scale, this could be batched
                // into a single bulk query endpoint. Current implementation resolves individually
                // for correctness and simplicity.
                foreach (var entityId in entityIds.Take(100)) // Cap at 100 to prevent runaway queries
                {
                    if (!Guid.TryParse(entityId, out _))
                    {
                        continue; // Skip non-GUID values
                    }

                    try
                    {
                        // Build the entity lookup URL using the standard record API pattern:
                        // GET /api/v3/en_US/record/{entityName}/{id}
                        var lookupUri = new Uri($"{serviceUrl.TrimEnd('/')}/api/v3/en_US/record/{entityName}/{entityId}");

                        using var lookupRequest = new HttpRequestMessage(HttpMethod.Get, lookupUri);

                        // Propagate authorization header for JWT token forwarding
                        if (context.Request.Headers.ContainsKey("Authorization"))
                        {
                            lookupRequest.Headers.TryAddWithoutValidation(
                                "Authorization",
                                context.Request.Headers["Authorization"].ToString());
                        }

                        using var lookupResponse = await httpClient.SendAsync(
                            lookupRequest, context.RequestAborted);

                        if (lookupResponse.IsSuccessStatusCode)
                        {
                            var responseBody = await lookupResponse.Content.ReadAsStringAsync(context.RequestAborted);
                            var responseJson = JObject.Parse(responseBody);
                            var entityData = responseJson["object"];
                            if (entityData != null && entityData.Type != JTokenType.Null)
                            {
                                result[entityId] = entityData;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "Failed to resolve {Entity} record {Id} from service {Service}",
                            entityName, entityId, serviceKey);
                        // Continue with other IDs — graceful degradation
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to resolve related {Entity} entities from service {Service}",
                    entityName, serviceKey);
            }

            return result;
        }

        #endregion

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
