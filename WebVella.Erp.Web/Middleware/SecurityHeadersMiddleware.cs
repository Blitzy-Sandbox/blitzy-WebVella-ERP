using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace WebVella.Erp.Web.Middleware
{
    /// <summary>
    /// OWASP-recommended security headers middleware for CWE-693 mitigation.
    /// Adds HTTP response headers to protect against clickjacking, XSS, MIME-sniffing, 
    /// and other browser-based attacks.
    /// 
    /// Security headers implemented:
    /// - X-Frame-Options: Prevents clickjacking attacks by disabling iframe embedding
    /// - X-Content-Type-Options: Prevents MIME type sniffing attacks
    /// - Strict-Transport-Security: Enforces HTTPS connections (HSTS)
    /// - Content-Security-Policy: Mitigates XSS attacks by controlling resource loading
    /// - Referrer-Policy: Controls referrer information leakage
    /// - Permissions-Policy: Restricts access to browser features not used by the application
    /// </summary>
    public class SecurityHeadersMiddleware
    {
        private readonly RequestDelegate _next;

        // Security header constants for consistent application across all responses
        private const string XFrameOptions = "X-Frame-Options";
        private const string XContentTypeOptions = "X-Content-Type-Options";
        private const string StrictTransportSecurity = "Strict-Transport-Security";
        private const string ContentSecurityPolicy = "Content-Security-Policy";
        private const string ReferrerPolicy = "Referrer-Policy";
        private const string PermissionsPolicy = "Permissions-Policy";

        // Header values following OWASP recommendations
        // X-Frame-Options: DENY - Prevents page from being loaded in any iframe
        // This is the strictest option, blocking all framing attempts
        private const string XFrameOptionsValue = "DENY";

        // X-Content-Type-Options: nosniff - Forces browser to honor declared content type
        // Prevents MIME type sniffing which could lead to XSS attacks
        private const string XContentTypeOptionsValue = "nosniff";

        // HSTS: max-age=31536000 (1 year) with includeSubDomains
        // Enforces HTTPS for the domain and all subdomains
        // Note: Only sent when served over HTTPS to avoid issues during development
        private const string StrictTransportSecurityValue = "max-age=31536000; includeSubDomains";

        // CSP: Restricts resource loading to same origin with allowances for inline scripts/styles
        // 'unsafe-inline' is required for existing ERP functionality that uses inline scripts/styles
        // This provides XSS mitigation while maintaining backward compatibility
        private const string ContentSecurityPolicyValue = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'";

        // Referrer-Policy: strict-origin-when-cross-origin
        // - Same-origin requests: Send full referrer URL
        // - Cross-origin requests: Send only the origin (no path)
        // - Downgrade (HTTPS to HTTP): Send nothing
        // Prevents sensitive URL path leakage to external sites
        private const string ReferrerPolicyValue = "strict-origin-when-cross-origin";

        // Permissions-Policy: Disables browser features not used by the ERP application
        // Reduces attack surface by preventing potential abuse of these APIs
        // - geolocation=(): Disables location tracking
        // - microphone=(): Disables microphone access
        // - camera=(): Disables camera access
        private const string PermissionsPolicyValue = "geolocation=(), microphone=(), camera=()";

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
        /// </summary>
        /// <param name="next">The next middleware delegate in the pipeline.</param>
        public SecurityHeadersMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        /// <summary>
        /// Processes the HTTP request by adding security headers to the response
        /// before passing control to the next middleware in the pipeline.
        /// </summary>
        /// <param name="context">The current HTTP context.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task Invoke(HttpContext context)
        {
            // Add security headers before processing the request
            // Headers are added to response early so they are present even if
            // subsequent middleware or the application throws an exception
            var headers = context.Response.Headers;

            // X-Frame-Options: DENY
            // Mitigates clickjacking attacks by preventing the page from being embedded in iframes
            // CWE-1021: Improper Restriction of Rendered UI Layers or Frames
            if (!headers.ContainsKey(XFrameOptions))
            {
                headers.Append(XFrameOptions, XFrameOptionsValue);
            }

            // X-Content-Type-Options: nosniff
            // Prevents browsers from MIME-sniffing a response away from the declared content-type
            // CWE-16: Configuration - ensures browser respects Content-Type header
            if (!headers.ContainsKey(XContentTypeOptions))
            {
                headers.Append(XContentTypeOptions, XContentTypeOptionsValue);
            }

            // Strict-Transport-Security (HSTS)
            // Only add HSTS header when served over HTTPS to avoid issues during HTTP development
            // CWE-319: Cleartext Transmission of Sensitive Information mitigation
            if (context.Request.IsHttps && !headers.ContainsKey(StrictTransportSecurity))
            {
                headers.Append(StrictTransportSecurity, StrictTransportSecurityValue);
            }

            // Content-Security-Policy
            // Restricts sources for content loading to mitigate XSS attacks
            // CWE-79: Improper Neutralization of Input During Web Page Generation ('Cross-site Scripting')
            if (!headers.ContainsKey(ContentSecurityPolicy))
            {
                headers.Append(ContentSecurityPolicy, ContentSecurityPolicyValue);
            }

            // Referrer-Policy
            // Controls how much referrer information is included with requests
            // CWE-200: Exposure of Sensitive Information to an Unauthorized Actor
            if (!headers.ContainsKey(ReferrerPolicy))
            {
                headers.Append(ReferrerPolicy, ReferrerPolicyValue);
            }

            // Permissions-Policy (formerly Feature-Policy)
            // Restricts which browser features can be used
            // Reduces attack surface by disabling unused capabilities
            if (!headers.ContainsKey(PermissionsPolicy))
            {
                headers.Append(PermissionsPolicy, PermissionsPolicyValue);
            }

            // Continue to the next middleware in the pipeline
            await _next(context);
        }
    }
}
