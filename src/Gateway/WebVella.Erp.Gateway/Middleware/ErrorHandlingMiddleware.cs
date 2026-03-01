using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace WebVella.Erp.Gateway.Middleware
{
    /// <summary>
    /// API Gateway error handling middleware derived from the monolith's
    /// <c>WebVella.Erp.Web.Middleware.ErpErrorHandlingMiddleware</c>.
    /// 
    /// Captures unhandled exceptions in the Gateway pipeline and wraps them in the
    /// <c>BaseResponseModel</c> envelope format (<c>{ success, errors, timestamp, message, object }</c>)
    /// to preserve backward compatibility with the existing REST API v3 contract.
    /// 
    /// Key differences from the monolith:
    /// <list type="bullet">
    ///   <item>Replaces database-based <c>LogService.Create()</c> with structured <c>ILogger</c> logging</item>
    ///   <item>Does NOT rethrow exceptions — writes a JSON error response instead (Gateway is the outermost handler)</item>
    ///   <item>Maps exception types to appropriate HTTP status codes (502, 504, 401, 400, 500)</item>
    ///   <item>Controls error detail level based on ASPNETCORE_ENVIRONMENT (development vs production)</item>
    /// </list>
    /// 
    /// Preserves from the monolith:
    /// <list type="bullet">
    ///   <item>File access error filtering ("The process cannot access the file") — source line 50</item>
    ///   <item>Nested defensive try-catch pattern — source lines 56-66</item>
    ///   <item>Request context inclusion in log entries — source lines 45-47</item>
    /// </list>
    /// </summary>
    public class ErrorHandlingMiddleware
    {
        /// <summary>
        /// The next middleware delegate in the pipeline.
        /// Derived from <c>ErpErrorHandlingMiddleware.next</c> (source line 12).
        /// </summary>
        private readonly RequestDelegate _next;

        /// <summary>
        /// Structured logger replacing the monolith's database-backed <c>LogService.Create()</c>.
        /// Gateway has no direct database access, so all diagnostics flow through ILogger.
        /// </summary>
        private readonly ILogger<ErrorHandlingMiddleware> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ErrorHandlingMiddleware"/> class.
        /// Replaces the monolith constructor (source lines 14-16) which only took <c>RequestDelegate</c>.
        /// </summary>
        /// <param name="next">The next middleware delegate in the pipeline.</param>
        /// <param name="logger">Structured logger injected via DI, replacing database-based LogService.</param>
        public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Invokes the middleware, wrapping the downstream pipeline in error handling.
        /// Adapted from source lines 19-30 of <c>ErpErrorHandlingMiddleware.Invoke()</c>.
        /// 
        /// KEY DIFFERENCE: The monolith rethrows after logging (<c>throw;</c> at source line 29).
        /// The Gateway MUST NOT rethrow — it writes a JSON error response instead, because the
        /// Gateway is the outermost handler and must return a proper HTTP response to the client.
        /// </summary>
        /// <param name="context">The HTTP context for the current request.</param>
        public async Task Invoke(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex);
            }
        }

        /// <summary>
        /// Handles an unhandled exception by writing a JSON error response in the
        /// <c>BaseResponseModel</c> envelope format.
        /// 
        /// Replaces the monolith's <c>LogError()</c> method (source lines 33-67) which created
        /// a database context, opened a system security scope, and logged via <c>LogService.Create()</c>.
        /// 
        /// Preserves:
        /// - File access error filtering (source line 50)
        /// - Nested defensive try-catch pattern (source lines 56-66)
        /// - Request context in log entries (source lines 45-47)
        /// </summary>
        /// <param name="context">The HTTP context for the current request.</param>
        /// <param name="ex">The unhandled exception.</param>
        private async Task HandleExceptionAsync(HttpContext context, Exception ex)
        {
            try
            {
                // ------------------------------------------------------------------
                // Preserve file access error filtering from monolith
                // (ErpErrorHandlingMiddleware.cs line 50):
                //   if (ex != null && ex.Message.Contains("The process cannot access the file")) return;
                // ------------------------------------------------------------------
                if (ex != null && ex.Message.Contains("The process cannot access the file"))
                {
                    _logger.LogWarning(ex, "File access error filtered (non-critical)");
                    return;
                }

                // ------------------------------------------------------------------
                // Log the full exception with request context.
                // Replaces monolith's LogService.Create(Diagnostics.LogType.Error, "Global", ex, request)
                // (source line 53) with structured logging including request method, path, and query.
                // Always log the full exception regardless of environment.
                // ------------------------------------------------------------------
                _logger.LogError(ex, "Unhandled exception: {Method} {Path} {Query}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString);

                // ------------------------------------------------------------------
                // If the response has already started being sent to the client,
                // we cannot modify the response headers or status code.
                // Log the warning and return — the connection will be terminated
                // by the server when the middleware pipeline exits.
                // ------------------------------------------------------------------
                if (context.Response.HasStarted)
                {
                    _logger.LogWarning(
                        "Response has already started. Cannot write error response for exception: {ExceptionMessage}",
                        ex?.Message ?? "Unknown error");
                    return;
                }

                // ------------------------------------------------------------------
                // Set response content type to JSON for the error envelope.
                // ------------------------------------------------------------------
                context.Response.ContentType = "application/json";

                // ------------------------------------------------------------------
                // Map exception type to appropriate HTTP status code.
                // These mappings are specific to the Gateway's role as a reverse proxy:
                //   - HttpRequestException → 502 Bad Gateway (backend service call failure)
                //   - TaskCanceledException/OperationCanceledException → 504 Gateway Timeout
                //   - UnauthorizedAccessException → 401 Unauthorized
                //   - ArgumentException/ArgumentNullException → 400 Bad Request
                //   - All others → 500 Internal Server Error
                // ------------------------------------------------------------------
                var statusCode = DetermineStatusCode(ex!);
                context.Response.StatusCode = (int)statusCode;

                // ------------------------------------------------------------------
                // Determine error detail level based on environment.
                // In development: include full exception details (stack trace) for debugging.
                // In production: only include the exception message for security.
                // ------------------------------------------------------------------
                var isDevelopment = string.Equals(
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                    "Development",
                    StringComparison.OrdinalIgnoreCase);
                var errorMessage = isDevelopment
                    ? (ex?.ToString() ?? "Unknown error")
                    : (ex?.Message ?? "An unexpected error occurred.");

                // ------------------------------------------------------------------
                // Write JSON response body using the BaseResponseModel envelope format.
                // CRITICAL: The response shape MUST match: { success, errors, timestamp, message, object }
                // This is the BaseResponseModel envelope used throughout the monolith's API responses.
                // 
                // errors array contains objects with { key, value, message } properties,
                // matching ErrorModel from WebVella.Erp/Api/Models/BaseModels.cs.
                //
                // @object uses the @ prefix because "object" is a C# reserved word.
                // ------------------------------------------------------------------
                var response = new
                {
                    success = false,
                    timestamp = DateTime.UtcNow,
                    errors = new[]
                    {
                        new
                        {
                            key = (string?)null,
                            value = (string?)null,
                            message = errorMessage
                        }
                    },
                    message = "An error occurred processing the request.",
                    @object = (object?)null
                };

                // ------------------------------------------------------------------
                // Serialize using Newtonsoft.Json (NOT System.Text.Json) to match
                // monolith serialization behavior and preserve backward-compatible
                // REST API v3 error response format.
                // ------------------------------------------------------------------
                var jsonResponse = JsonConvert.SerializeObject(response);
                await context.Response.WriteAsync(jsonResponse);
            }
            catch (Exception logEx)
            {
                // ------------------------------------------------------------------
                // Preserve monolith's nested defensive try-catch pattern.
                // Source: ErpErrorHandlingMiddleware.cs lines 57-60 (outer catch)
                // and lines 63-65 (inner catch).
                //
                // The monolith swallows all logging/error-handling failures to prevent
                // them from masking the original error. We log at Critical level
                // as a last-resort diagnostic, but do NOT rethrow.
                // ------------------------------------------------------------------
                try
                {
                    _logger.LogCritical(logEx, "Failed to handle exception properly. Original exception: {OriginalException}", ex?.Message);
                }
                catch
                {
                    // Final safety net: if even LogCritical fails, silently swallow.
                    // This matches the monolith's innermost empty catch (source lines 63-65).
                }
            }
        }

        /// <summary>
        /// Determines the appropriate HTTP status code based on the exception type.
        /// This mapping reflects the Gateway's role as a reverse proxy to backend microservices.
        /// </summary>
        /// <param name="ex">The exception to classify.</param>
        /// <returns>The HTTP status code to return to the client.</returns>
        private static HttpStatusCode DetermineStatusCode(Exception ex)
        {
            return ex switch
            {
                HttpRequestException => HttpStatusCode.BadGateway,               // 502 — backend service call failure
                TaskCanceledException => HttpStatusCode.GatewayTimeout,          // 504 — request timeout
                OperationCanceledException => HttpStatusCode.GatewayTimeout,     // 504 — request cancellation
                UnauthorizedAccessException => HttpStatusCode.Unauthorized,      // 401 — authorization failure
                ArgumentNullException => HttpStatusCode.BadRequest,              // 400 — null argument (checked before ArgumentException)
                ArgumentException => HttpStatusCode.BadRequest,                  // 400 — invalid argument
                _ => HttpStatusCode.InternalServerError                          // 500 — all other unhandled exceptions
            };
        }
    }
}
