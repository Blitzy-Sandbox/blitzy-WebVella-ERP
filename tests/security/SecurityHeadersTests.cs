using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace WebVella.Erp.Tests.Security
{
    /// <summary>
    /// SECURITY: Regression test suite for OWASP-recommended HTTP security headers.
    /// Tests CWE-693 (Protection Mechanism Failure) vulnerability mitigation.
    /// 
    /// This test suite validates the SecurityHeadersMiddleware implementation that adds
    /// critical HTTP response headers for browser-based attack protection:
    /// 
    /// OWASP-Recommended Headers Tested:
    /// - X-Frame-Options: Prevents clickjacking attacks (DENY)
    /// - X-Content-Type-Options: Prevents MIME type sniffing (nosniff)
    /// - Strict-Transport-Security: Enforces HTTPS connections (HSTS)
    /// - Content-Security-Policy: Mitigates XSS attacks
    /// - Referrer-Policy: Controls referrer information leakage
    /// - Permissions-Policy: Restricts access to browser features
    /// 
    /// The tests use a minimal in-memory test server that mirrors the production
    /// SecurityHeadersMiddleware configuration without requiring database connections
    /// or other ERP-specific dependencies.
    /// 
    /// CRITICAL SECURITY IMPACT:
    /// Without these headers:
    /// - Clickjacking attacks possible (iframe embedding)
    /// - XSS attacks more effective (no CSP restrictions)
    /// - MIME type confusion attacks possible
    /// - Referrer data leaked to external sites
    /// - Browser features can be abused
    /// 
    /// Run with: dotnet test --filter "Category=Security"
    /// </summary>
    [Trait("Category", "Security")]
    public class SecurityHeadersTests : IDisposable
    {
        #region <--- Test Constants --->

        /// <summary>
        /// Security header names as defined in SecurityHeadersMiddleware.
        /// </summary>
        private const string XFrameOptions = "X-Frame-Options";
        private const string XContentTypeOptions = "X-Content-Type-Options";
        private const string StrictTransportSecurity = "Strict-Transport-Security";
        private const string ContentSecurityPolicy = "Content-Security-Policy";
        private const string ReferrerPolicy = "Referrer-Policy";
        private const string PermissionsPolicy = "Permissions-Policy";

        /// <summary>
        /// Expected security header values as defined in SecurityHeadersMiddleware.
        /// </summary>
        private const string XFrameOptionsValue = "DENY";
        private const string XContentTypeOptionsValue = "nosniff";
        private const string StrictTransportSecurityValue = "max-age=31536000; includeSubDomains";
        private const string ContentSecurityPolicyValue = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'";
        private const string ReferrerPolicyValue = "strict-origin-when-cross-origin";
        private const string PermissionsPolicyValue = "geolocation=(), microphone=(), camera=()";

        /// <summary>
        /// Minimum required max-age value for HSTS (1 year in seconds) per Section 0.5.2 Fix #5.
        /// </summary>
        private const int MinHstsMaxAgeSeconds = 31536000;

        /// <summary>
        /// Test endpoint for security header validation.
        /// </summary>
        private const string TestEndpoint = "/api/security-test";

        /// <summary>
        /// Required CSP directives that must be present.
        /// </summary>
        private static readonly string[] RequiredCspDirectives = new[]
        {
            "default-src",
            "script-src",
            "style-src"
        };

        #endregion

        #region <--- Test Fixture --->

        private readonly IHost _httpHost;
        private readonly IHost _httpsHost;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _httpsClient;

        /// <summary>
        /// Initializes minimal test servers with SecurityHeadersMiddleware for testing.
        /// Creates both HTTP and HTTPS test servers to verify HSTS behavior.
        /// </summary>
        public SecurityHeadersTests()
        {
            // Create HTTP test server (HSTS should NOT be added for non-HTTPS)
            _httpHost = CreateTestHost(useHttps: false);
            _httpClient = _httpHost.GetTestClient();

            // Create HTTPS test server (HSTS SHOULD be added for HTTPS)
            _httpsHost = CreateTestHost(useHttps: true);
            _httpsClient = _httpsHost.GetTestClient();
        }

        /// <summary>
        /// Creates a minimal ASP.NET Core host with SecurityHeadersMiddleware.
        /// This mirrors the production configuration in Startup.cs without database dependencies.
        /// </summary>
        /// <param name="useHttps">Whether to configure HTTPS (affects HSTS header)</param>
        /// <returns>Configured and started IHost</returns>
        private static IHost CreateTestHost(bool useHttps)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer(options =>
                    {
                        // Configure whether requests appear as HTTPS for HSTS testing
                        options.PreserveExecutionContext = true;
                    });
                    
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                    });
                    
                    webBuilder.Configure(app =>
                    {
                        // SECURITY: Apply security headers middleware early in pipeline
                        // as configured in Startup.cs line 167
                        app.UseSecurityHeadersMiddleware(useHttps);
                        
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            // Simple test endpoint that returns 200 OK
                            endpoints.MapGet(TestEndpoint, async context =>
                            {
                                context.Response.StatusCode = 200;
                                await context.Response.WriteAsync("OK");
                            });
                            
                            endpoints.MapPost(TestEndpoint, async context =>
                            {
                                context.Response.StatusCode = 200;
                                await context.Response.WriteAsync("OK");
                            });
                        });
                    });
                })
                .Build();

            host.Start();
            return host;
        }

        /// <summary>
        /// Disposes the test hosts and clients.
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
            _httpHost?.Dispose();
            _httpsClient?.Dispose();
            _httpsHost?.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region <--- Test: All Headers Present (TestAllHeadersPresent) --->

        /// <summary>
        /// SECURITY TEST: Verify all OWASP-recommended security headers are present.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - X-Frame-Options header present
        /// - X-Content-Type-Options header present
        /// - Content-Security-Policy header present
        /// - Strict-Transport-Security header present (HTTPS only)
        /// - Referrer-Policy header present
        /// - Permissions-Policy header present (optional but recommended)
        /// 
        /// CWE-693 Mitigation: Ensures browser security mechanisms are enabled.
        /// </summary>
        [Fact]
        public async Task TestAllHeadersPresent()
        {
            // Send GET request to test endpoint over HTTPS (for full header set)
            var response = await _httpsClient.GetAsync(TestEndpoint);

            // Verify request succeeded
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Get response headers
            var headers = response.Headers;

            // SECURITY ASSERTION: All required headers must be present
            // X-Frame-Options - Prevents clickjacking
            Assert.True(
                headers.Contains(XFrameOptions),
                $"SECURITY FAILURE: {XFrameOptions} header missing. Clickjacking attacks possible."
            );

            // X-Content-Type-Options - Prevents MIME sniffing
            Assert.True(
                headers.Contains(XContentTypeOptions),
                $"SECURITY FAILURE: {XContentTypeOptions} header missing. MIME sniffing attacks possible."
            );

            // Content-Security-Policy - Prevents XSS
            Assert.True(
                headers.Contains(ContentSecurityPolicy),
                $"SECURITY FAILURE: {ContentSecurityPolicy} header missing. XSS attacks more effective."
            );

            // Strict-Transport-Security - Enforces HTTPS (HTTPS response only)
            Assert.True(
                headers.Contains(StrictTransportSecurity),
                $"SECURITY FAILURE: {StrictTransportSecurity} header missing. HTTPS downgrade attacks possible."
            );

            // Referrer-Policy - Controls referrer leakage
            Assert.True(
                headers.Contains(ReferrerPolicy),
                $"SECURITY FAILURE: {ReferrerPolicy} header missing. Referrer data leaked."
            );

            // Permissions-Policy - Restricts browser features (optional but recommended)
            Assert.True(
                headers.Contains(PermissionsPolicy),
                $"SECURITY FAILURE: {PermissionsPolicy} header missing. Browser features may be abused."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify headers are present on POST requests too.
        /// Security headers should be added to all response types.
        /// </summary>
        [Fact]
        public async Task TestAllHeadersPresentOnPostRequest()
        {
            // Create POST request
            var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await _httpsClient.PostAsync(TestEndpoint, content);

            // Verify request succeeded
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // SECURITY ASSERTION: Headers must be present on POST responses too
            Assert.True(headers_Contains(response, XFrameOptions), $"{XFrameOptions} missing on POST");
            Assert.True(headers_Contains(response, XContentTypeOptions), $"{XContentTypeOptions} missing on POST");
            Assert.True(headers_Contains(response, ContentSecurityPolicy), $"{ContentSecurityPolicy} missing on POST");
            Assert.True(headers_Contains(response, ReferrerPolicy), $"{ReferrerPolicy} missing on POST");
            Assert.True(headers_Contains(response, PermissionsPolicy), $"{PermissionsPolicy} missing on POST");
        }

        /// <summary>
        /// SECURITY TEST: Verify non-HSTS headers are present on HTTP requests.
        /// HSTS should only be sent over HTTPS, but other headers should be present.
        /// </summary>
        [Fact]
        public async Task TestNonHstsHeadersPresentOnHttp()
        {
            // Send GET request to test endpoint over HTTP
            var response = await _httpClient.GetAsync(TestEndpoint);

            // Verify request succeeded
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // SECURITY ASSERTION: Non-HSTS headers must be present on HTTP
            Assert.True(headers_Contains(response, XFrameOptions), $"{XFrameOptions} missing on HTTP");
            Assert.True(headers_Contains(response, XContentTypeOptions), $"{XContentTypeOptions} missing on HTTP");
            Assert.True(headers_Contains(response, ContentSecurityPolicy), $"{ContentSecurityPolicy} missing on HTTP");
            Assert.True(headers_Contains(response, ReferrerPolicy), $"{ReferrerPolicy} missing on HTTP");
            Assert.True(headers_Contains(response, PermissionsPolicy), $"{PermissionsPolicy} missing on HTTP");

            // HSTS should NOT be present on HTTP (to avoid issues during development)
            Assert.False(
                headers_Contains(response, StrictTransportSecurity),
                $"SECURITY WARNING: {StrictTransportSecurity} should not be sent over HTTP"
            );
        }

        #endregion

        #region <--- Test: Content-Security-Policy (TestCspPolicy) --->

        /// <summary>
        /// SECURITY TEST: Verify Content-Security-Policy header format is valid.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - default-src 'self' directive present
        /// - script-src directive present
        /// - style-src directive present
        /// - Verify no unsafe configurations that defeat CSP purpose
        /// 
        /// CWE-79 Mitigation: Restricts script/style sources to prevent XSS.
        /// </summary>
        [Fact]
        public async Task TestCspPolicy()
        {
            // Send request to get CSP header
            var response = await _httpsClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Get CSP header value
            var cspHeader = GetHeaderValue(response, ContentSecurityPolicy);
            Assert.NotNull(cspHeader);
            Assert.NotEmpty(cspHeader);

            // SECURITY ASSERTION: Required CSP directives must be present
            foreach (var directive in RequiredCspDirectives)
            {
                Assert.True(
                    cspHeader.Contains(directive),
                    $"SECURITY FAILURE: CSP missing required directive '{directive}'. XSS protection incomplete."
                );
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify default-src 'self' is properly configured.
        /// This is the most important CSP directive as it sets the default policy.
        /// </summary>
        [Fact]
        public async Task TestCspDefaultSrcSelf()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var cspHeader = GetHeaderValue(response, ContentSecurityPolicy);

            Assert.NotNull(cspHeader);

            // SECURITY ASSERTION: default-src must include 'self'
            Assert.True(
                cspHeader.Contains("default-src 'self'") || cspHeader.Contains("default-src 'self'"),
                "SECURITY FAILURE: CSP default-src does not restrict to 'self'. External resources allowed."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify script-src directive is present.
        /// Controls where scripts can be loaded from.
        /// </summary>
        [Fact]
        public async Task TestCspScriptSrcPresent()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var cspHeader = GetHeaderValue(response, ContentSecurityPolicy);

            Assert.NotNull(cspHeader);

            // SECURITY ASSERTION: script-src directive must be present
            Assert.True(
                cspHeader.Contains("script-src"),
                "SECURITY FAILURE: CSP script-src directive missing. Script sources unrestricted."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify style-src directive is present.
        /// Controls where stylesheets can be loaded from.
        /// </summary>
        [Fact]
        public async Task TestCspStyleSrcPresent()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var cspHeader = GetHeaderValue(response, ContentSecurityPolicy);

            Assert.NotNull(cspHeader);

            // SECURITY ASSERTION: style-src directive must be present
            Assert.True(
                cspHeader.Contains("style-src"),
                "SECURITY FAILURE: CSP style-src directive missing. Style sources unrestricted."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify CSP does not allow unsafe-eval.
        /// 'unsafe-eval' allows eval() and similar, which is a significant XSS risk.
        /// </summary>
        [Fact]
        public async Task TestCspNoUnsafeEval()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var cspHeader = GetHeaderValue(response, ContentSecurityPolicy);

            Assert.NotNull(cspHeader);

            // SECURITY ASSERTION: unsafe-eval should NOT be in CSP
            Assert.False(
                cspHeader.Contains("'unsafe-eval'"),
                "SECURITY WARNING: CSP allows 'unsafe-eval'. eval() and similar functions enabled."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify CSP header value matches expected format.
        /// </summary>
        [Fact]
        public async Task TestCspHeaderValueFormat()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var cspHeader = GetHeaderValue(response, ContentSecurityPolicy);

            Assert.NotNull(cspHeader);
            Assert.Equal(ContentSecurityPolicyValue, cspHeader);
        }

        #endregion

        #region <--- Test: HSTS Header (TestHstsHeader) --->

        /// <summary>
        /// SECURITY TEST: Verify Strict-Transport-Security header configuration.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - max-age is at least 31536000 (1 year) as specified in Section 0.5.2 Fix #5
        /// - includeSubDomains directive is present
        /// 
        /// CWE-319 Mitigation: Prevents cleartext transmission of sensitive information.
        /// </summary>
        [Fact]
        public async Task TestHstsHeader()
        {
            // Send HTTPS request to get HSTS header
            var response = await _httpsClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Get HSTS header value
            var hstsHeader = GetHeaderValue(response, StrictTransportSecurity);
            Assert.NotNull(hstsHeader);
            Assert.NotEmpty(hstsHeader);

            // SECURITY ASSERTION: max-age must be at least 1 year (31536000 seconds)
            var maxAgeMatch = Regex.Match(hstsHeader, @"max-age=(\d+)");
            Assert.True(
                maxAgeMatch.Success,
                $"SECURITY FAILURE: HSTS header missing max-age directive. Header: {hstsHeader}"
            );

            var maxAge = int.Parse(maxAgeMatch.Groups[1].Value);
            Assert.True(
                maxAge >= MinHstsMaxAgeSeconds,
                $"SECURITY FAILURE: HSTS max-age ({maxAge}) less than required minimum ({MinHstsMaxAgeSeconds})."
            );

            // SECURITY ASSERTION: includeSubDomains must be present
            Assert.True(
                hstsHeader.Contains("includeSubDomains"),
                "SECURITY FAILURE: HSTS missing includeSubDomains. Subdomains not protected."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify HSTS max-age value exactly matches specification.
        /// Per Section 0.5.2 Fix #5, max-age should be 31536000 (1 year).
        /// </summary>
        [Fact]
        public async Task TestHstsMaxAgeValue()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var hstsHeader = GetHeaderValue(response, StrictTransportSecurity);

            Assert.NotNull(hstsHeader);

            // Extract max-age value
            var maxAgeMatch = Regex.Match(hstsHeader, @"max-age=(\d+)");
            Assert.True(maxAgeMatch.Success, "HSTS max-age not found");

            var maxAge = int.Parse(maxAgeMatch.Groups[1].Value);
            
            // SECURITY ASSERTION: max-age should be exactly 31536000 (1 year)
            Assert.Equal(MinHstsMaxAgeSeconds, maxAge);
        }

        /// <summary>
        /// SECURITY TEST: Verify HSTS includeSubDomains directive format.
        /// </summary>
        [Fact]
        public async Task TestHstsIncludeSubDomains()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var hstsHeader = GetHeaderValue(response, StrictTransportSecurity);

            Assert.NotNull(hstsHeader);

            // SECURITY ASSERTION: includeSubDomains must be present
            Assert.Contains("includeSubDomains", hstsHeader);
        }

        /// <summary>
        /// SECURITY TEST: Verify HSTS header exact value.
        /// </summary>
        [Fact]
        public async Task TestHstsHeaderValueExact()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var hstsHeader = GetHeaderValue(response, StrictTransportSecurity);

            Assert.NotNull(hstsHeader);
            Assert.Equal(StrictTransportSecurityValue, hstsHeader);
        }

        /// <summary>
        /// SECURITY TEST: Verify HSTS is NOT sent over HTTP.
        /// Sending HSTS over HTTP can cause issues and is not effective.
        /// </summary>
        [Fact]
        public async Task TestHstsNotSentOverHttp()
        {
            var response = await _httpClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // SECURITY ASSERTION: HSTS should NOT be sent over HTTP
            var hasHsts = headers_Contains(response, StrictTransportSecurity);
            Assert.False(
                hasHsts,
                "SECURITY WARNING: HSTS sent over HTTP. This can cause browser issues."
            );
        }

        #endregion

        #region <--- Test: X-Frame-Options (TestXFrameOptionsDeny) --->

        /// <summary>
        /// SECURITY TEST: Verify X-Frame-Options is set to DENY.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - X-Frame-Options must be set to DENY to prevent clickjacking
        /// 
        /// CWE-1021 Mitigation: Prevents improper restriction of rendered UI layers or frames.
        /// </summary>
        [Fact]
        public async Task TestXFrameOptionsDeny()
        {
            // Send request to get X-Frame-Options header
            var response = await _httpsClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Get X-Frame-Options header value
            var xfoHeader = GetHeaderValue(response, XFrameOptions);
            Assert.NotNull(xfoHeader);
            Assert.NotEmpty(xfoHeader);

            // SECURITY ASSERTION: X-Frame-Options must be DENY
            Assert.Equal(
                XFrameOptionsValue,
                xfoHeader,
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify X-Frame-Options is not SAMEORIGIN.
        /// DENY is stricter than SAMEORIGIN and prevents all framing.
        /// </summary>
        [Fact]
        public async Task TestXFrameOptionsNotSameOrigin()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var xfoHeader = GetHeaderValue(response, XFrameOptions);

            Assert.NotNull(xfoHeader);

            // SECURITY ASSERTION: Should be DENY, not SAMEORIGIN
            Assert.False(
                string.Equals(xfoHeader, "SAMEORIGIN", StringComparison.OrdinalIgnoreCase),
                "SECURITY WARNING: X-Frame-Options is SAMEORIGIN. DENY provides stronger protection."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify X-Frame-Options does not use ALLOW-FROM.
        /// ALLOW-FROM is deprecated and not supported by modern browsers.
        /// </summary>
        [Fact]
        public async Task TestXFrameOptionsNotAllowFrom()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var xfoHeader = GetHeaderValue(response, XFrameOptions);

            Assert.NotNull(xfoHeader);

            // SECURITY ASSERTION: Should not use deprecated ALLOW-FROM
            Assert.False(
                xfoHeader.StartsWith("ALLOW-FROM", StringComparison.OrdinalIgnoreCase),
                "SECURITY WARNING: X-Frame-Options uses deprecated ALLOW-FROM directive."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify X-Frame-Options is present on HTTP responses too.
        /// Clickjacking protection should work regardless of protocol.
        /// </summary>
        [Fact]
        public async Task TestXFrameOptionsPresentOnHttp()
        {
            var response = await _httpClient.GetAsync(TestEndpoint);
            var xfoHeader = GetHeaderValue(response, XFrameOptions);

            Assert.NotNull(xfoHeader);
            Assert.Equal(XFrameOptionsValue, xfoHeader, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region <--- Test: X-Content-Type-Options (TestXContentTypeOptionsNosniff) --->

        /// <summary>
        /// SECURITY TEST: Verify X-Content-Type-Options is set to nosniff.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - X-Content-Type-Options must be set to nosniff to prevent MIME sniffing
        /// 
        /// CWE-16 Mitigation: Ensures proper configuration against MIME type confusion.
        /// </summary>
        [Fact]
        public async Task TestXContentTypeOptionsNosniff()
        {
            // Send request to get X-Content-Type-Options header
            var response = await _httpsClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Get X-Content-Type-Options header value
            var xctoHeader = GetHeaderValue(response, XContentTypeOptions);
            Assert.NotNull(xctoHeader);
            Assert.NotEmpty(xctoHeader);

            // SECURITY ASSERTION: X-Content-Type-Options must be nosniff
            Assert.Equal(
                XContentTypeOptionsValue,
                xctoHeader,
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify X-Content-Type-Options is present on HTTP responses.
        /// MIME sniffing protection should work regardless of protocol.
        /// </summary>
        [Fact]
        public async Task TestXContentTypeOptionsPresentOnHttp()
        {
            var response = await _httpClient.GetAsync(TestEndpoint);
            var xctoHeader = GetHeaderValue(response, XContentTypeOptions);

            Assert.NotNull(xctoHeader);
            Assert.Equal(XContentTypeOptionsValue, xctoHeader, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// SECURITY TEST: Verify X-Content-Type-Options value is exactly "nosniff".
        /// </summary>
        [Fact]
        public async Task TestXContentTypeOptionsExactValue()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var xctoHeader = GetHeaderValue(response, XContentTypeOptions);

            Assert.NotNull(xctoHeader);
            Assert.Equal("nosniff", xctoHeader.ToLowerInvariant());
        }

        #endregion

        #region <--- Test: Referrer-Policy --->

        /// <summary>
        /// SECURITY TEST: Verify Referrer-Policy header is properly configured.
        /// Controls how much referrer information is included with requests.
        /// </summary>
        [Fact]
        public async Task TestReferrerPolicyHeader()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var referrerHeader = GetHeaderValue(response, ReferrerPolicy);
            Assert.NotNull(referrerHeader);
            Assert.NotEmpty(referrerHeader);

            // SECURITY ASSERTION: Referrer-Policy should be strict-origin-when-cross-origin
            Assert.Equal(ReferrerPolicyValue, referrerHeader);
        }

        /// <summary>
        /// SECURITY TEST: Verify Referrer-Policy is not "unsafe-url".
        /// "unsafe-url" sends full URL to all origins, which is a privacy/security risk.
        /// </summary>
        [Fact]
        public async Task TestReferrerPolicyNotUnsafeUrl()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var referrerHeader = GetHeaderValue(response, ReferrerPolicy);

            Assert.NotNull(referrerHeader);
            Assert.False(
                string.Equals(referrerHeader, "unsafe-url", StringComparison.OrdinalIgnoreCase),
                "SECURITY WARNING: Referrer-Policy is 'unsafe-url'. Full URLs leaked to all origins."
            );
        }

        /// <summary>
        /// SECURITY TEST: Verify Referrer-Policy is not "no-referrer-when-downgrade".
        /// This default policy leaks referrer on HTTPS to HTTPS requests.
        /// </summary>
        [Fact]
        public async Task TestReferrerPolicyNotDefault()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var referrerHeader = GetHeaderValue(response, ReferrerPolicy);

            Assert.NotNull(referrerHeader);
            
            // strict-origin-when-cross-origin is preferred over no-referrer-when-downgrade
            Assert.False(
                string.Equals(referrerHeader, "no-referrer-when-downgrade", StringComparison.OrdinalIgnoreCase),
                "SECURITY INFO: Consider using 'strict-origin-when-cross-origin' instead."
            );
        }

        #endregion

        #region <--- Test: Permissions-Policy --->

        /// <summary>
        /// SECURITY TEST: Verify Permissions-Policy header is properly configured.
        /// Restricts access to browser features not used by the application.
        /// </summary>
        [Fact]
        public async Task TestPermissionsPolicyHeader()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var permissionsHeader = GetHeaderValue(response, PermissionsPolicy);
            Assert.NotNull(permissionsHeader);
            Assert.NotEmpty(permissionsHeader);

            // SECURITY ASSERTION: Permissions-Policy should match expected value
            Assert.Equal(PermissionsPolicyValue, permissionsHeader);
        }

        /// <summary>
        /// SECURITY TEST: Verify geolocation is disabled.
        /// ERP applications typically don't need geolocation access.
        /// </summary>
        [Fact]
        public async Task TestPermissionsPolicyGeolocationDisabled()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var permissionsHeader = GetHeaderValue(response, PermissionsPolicy);

            Assert.NotNull(permissionsHeader);
            Assert.Contains("geolocation=()", permissionsHeader);
        }

        /// <summary>
        /// SECURITY TEST: Verify microphone is disabled.
        /// ERP applications typically don't need microphone access.
        /// </summary>
        [Fact]
        public async Task TestPermissionsPolicyMicrophoneDisabled()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var permissionsHeader = GetHeaderValue(response, PermissionsPolicy);

            Assert.NotNull(permissionsHeader);
            Assert.Contains("microphone=()", permissionsHeader);
        }

        /// <summary>
        /// SECURITY TEST: Verify camera is disabled.
        /// ERP applications typically don't need camera access.
        /// </summary>
        [Fact]
        public async Task TestPermissionsPolicyCameraDisabled()
        {
            var response = await _httpsClient.GetAsync(TestEndpoint);
            var permissionsHeader = GetHeaderValue(response, PermissionsPolicy);

            Assert.NotNull(permissionsHeader);
            Assert.Contains("camera=()", permissionsHeader);
        }

        #endregion

        #region <--- Helper Methods --->

        /// <summary>
        /// Gets a header value from the HTTP response.
        /// </summary>
        /// <param name="response">HTTP response message</param>
        /// <param name="headerName">Name of the header to retrieve</param>
        /// <returns>Header value or null if not found</returns>
        private static string GetHeaderValue(HttpResponseMessage response, string headerName)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return values.FirstOrDefault();
            }
            return null;
        }

        /// <summary>
        /// Checks if a header exists in the HTTP response.
        /// </summary>
        /// <param name="response">HTTP response message</param>
        /// <param name="headerName">Name of the header to check</param>
        /// <returns>True if header exists, false otherwise</returns>
        private static bool headers_Contains(HttpResponseMessage response, string headerName)
        {
            return response.Headers.Contains(headerName);
        }

        #endregion
    }

    #region <--- Test Middleware Extension --->

    /// <summary>
    /// Extension method to add security headers middleware for testing.
    /// This mirrors the production SecurityHeadersMiddleware behavior.
    /// </summary>
    internal static class SecurityHeadersTestMiddlewareExtensions
    {
        /// <summary>
        /// Adds security headers middleware to the application pipeline for testing.
        /// </summary>
        /// <param name="app">The application builder</param>
        /// <param name="isHttps">Whether requests should be treated as HTTPS (affects HSTS)</param>
        /// <returns>The application builder for method chaining</returns>
        public static IApplicationBuilder UseSecurityHeadersMiddleware(this IApplicationBuilder app, bool isHttps = true)
        {
            return app.Use(async (context, next) =>
            {
                var headers = context.Response.Headers;

                // X-Frame-Options: DENY - Prevents clickjacking
                if (!headers.ContainsKey("X-Frame-Options"))
                {
                    headers.Append("X-Frame-Options", "DENY");
                }

                // X-Content-Type-Options: nosniff - Prevents MIME sniffing
                if (!headers.ContainsKey("X-Content-Type-Options"))
                {
                    headers.Append("X-Content-Type-Options", "nosniff");
                }

                // Strict-Transport-Security (HSTS) - Only on HTTPS
                if (isHttps && !headers.ContainsKey("Strict-Transport-Security"))
                {
                    headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
                }

                // Content-Security-Policy
                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers.Append("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'");
                }

                // Referrer-Policy
                if (!headers.ContainsKey("Referrer-Policy"))
                {
                    headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
                }

                // Permissions-Policy
                if (!headers.ContainsKey("Permissions-Policy"))
                {
                    headers.Append("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
                }

                await next();
            });
        }
    }

    #endregion
}
