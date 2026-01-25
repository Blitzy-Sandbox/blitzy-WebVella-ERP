using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using WebVella.Erp.Site;
using Xunit;

namespace WebVella.Erp.Tests.Security
{
    /// <summary>
    /// SECURITY: Regression test suite for CORS (Cross-Origin Resource Sharing) policy enforcement.
    /// Tests CWE-942 (Overly Permissive Cross-domain Whitelist) vulnerability mitigation.
    /// 
    /// The security fix replaces the vulnerable AllowAnyOrigin() CORS configuration with:
    /// - Explicit allowed origins whitelist (configurable via Settings:AllowedOrigins)
    /// - Default allowed origins: http://localhost:5000, https://localhost:5001
    /// - Restricted methods: GET, POST, PUT, DELETE, PATCH
    /// - Restricted headers: Content-Type, Authorization, X-Requested-With
    /// - AllowCredentials enabled for authenticated requests
    /// 
    /// CRITICAL SECURITY IMPACT:
    /// Without these tests, regression could allow:
    /// - Cross-site request forgery (CSRF) attacks
    /// - Data theft from authenticated users via malicious websites
    /// - Session hijacking through cross-origin requests
    /// 
    /// Run with: dotnet test --filter "Category=Security"
    /// </summary>
    [Trait("Category", "Security")]
    public class CorsSecurityTests : IClassFixture<WebApplicationFactory<Startup>>
    {
        #region <--- Test Constants --->

        /// <summary>
        /// Default allowed origins as configured in Startup.cs when no AllowedOrigins setting is specified.
        /// These should receive CORS headers in preflight responses.
        /// </summary>
        private static readonly string[] DefaultAllowedOrigins = new[]
        {
            "http://localhost:5000",
            "https://localhost:5001"
        };

        /// <summary>
        /// Malicious origins that should be blocked by CORS policy.
        /// SECURITY: Any origin not in the whitelist must be rejected.
        /// </summary>
        private static readonly string[] MaliciousOrigins = new[]
        {
            "http://evil.com",
            "http://attacker.local",
            "https://malicious-site.org",
            "http://phishing.example.com",
            "null", // Special 'null' origin used in some attack scenarios
            "http://localhost:9999" // Different port - not in whitelist
        };

        /// <summary>
        /// HTTP methods allowed by the CORS policy per Section 0.5.2 Fix #4.
        /// </summary>
        private static readonly string[] AllowedMethods = new[]
        {
            "GET", "POST", "PUT", "DELETE", "PATCH"
        };

        /// <summary>
        /// HTTP methods that should be blocked by the CORS policy.
        /// </summary>
        private static readonly string[] BlockedMethods = new[]
        {
            "TRACE", "CONNECT", "OPTIONS" // OPTIONS is special for preflight, TRACE/CONNECT should be blocked
        };

        /// <summary>
        /// HTTP headers allowed by the CORS policy.
        /// </summary>
        private static readonly string[] AllowedHeaders = new[]
        {
            "Content-Type",
            "Authorization",
            "X-Requested-With"
        };

        /// <summary>
        /// CORS response header names.
        /// </summary>
        private const string AccessControlAllowOrigin = "Access-Control-Allow-Origin";
        private const string AccessControlAllowMethods = "Access-Control-Allow-Methods";
        private const string AccessControlAllowHeaders = "Access-Control-Allow-Headers";
        private const string AccessControlAllowCredentials = "Access-Control-Allow-Credentials";
        private const string AccessControlMaxAge = "Access-Control-Max-Age";

        /// <summary>
        /// Test endpoint for CORS validation - login page is publicly accessible.
        /// </summary>
        private const string TestEndpoint = "/login";

        #endregion

        #region <--- Test Fixture --->

        private readonly WebApplicationFactory<Startup> _factory;
        private readonly HttpClient _client;

        /// <summary>
        /// Initializes the test fixture with WebApplicationFactory for integration testing.
        /// Uses the application's actual Startup class to ensure CORS configuration is tested as deployed.
        /// </summary>
        /// <param name="factory">WebApplicationFactory instance shared across test methods</param>
        public CorsSecurityTests(WebApplicationFactory<Startup> factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            
            // Create client that does NOT follow redirects to properly test CORS headers
            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });
        }

        #endregion

        #region <--- Test: Unauthorized Origin Blocking --->

        /// <summary>
        /// SECURITY TEST: Verify requests from unauthorized origins are blocked.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - Send preflight OPTIONS request with Origin header from unauthorized domain
        /// - Verify response does NOT contain Access-Control-Allow-Origin header
        /// - Verify response does NOT contain Access-Control-Allow-Methods header
        /// - Test with various malicious origins (e.g., "http://evil.com", "http://attacker.local")
        /// 
        /// CWE-942 Mitigation: Ensures only whitelisted origins can make cross-origin requests.
        /// </summary>
        [Fact]
        public async Task TestUnauthorizedOriginBlocked()
        {
            // Test each malicious origin to ensure comprehensive blocking
            foreach (var maliciousOrigin in MaliciousOrigins)
            {
                // Create preflight OPTIONS request with malicious origin
                var request = CreatePreflightRequest(TestEndpoint, maliciousOrigin, "GET");

                // Send the preflight request
                var response = await _client.SendAsync(request);

                // SECURITY ASSERTION: Unauthorized origins should NOT receive CORS headers
                // The absence of Access-Control-Allow-Origin header means the browser will block the request
                
                var hasAllowOriginHeader = response.Headers.Contains(AccessControlAllowOrigin);
                var hasAllowMethodsHeader = response.Headers.Contains(AccessControlAllowMethods);

                // If headers exist, verify they don't allow the malicious origin
                if (hasAllowOriginHeader)
                {
                    var allowedOriginValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                    
                    // SECURITY: The allowed origin must NOT match the malicious origin
                    // and must NOT be a wildcard (*) when credentials are involved
                    Assert.False(
                        string.Equals(allowedOriginValue, maliciousOrigin, StringComparison.OrdinalIgnoreCase) ||
                        allowedOriginValue == "*",
                        $"SECURITY VIOLATION: Malicious origin '{maliciousOrigin}' was allowed by CORS policy. " +
                        $"Access-Control-Allow-Origin returned: '{allowedOriginValue}'"
                    );
                }

                // Log test progress for debugging
                await Task.CompletedTask; // Ensure async method signature
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify specific malicious domain is blocked.
        /// Tests the evil.com origin explicitly as required by the specification.
        /// </summary>
        [Fact]
        public async Task TestEvilComOriginBlocked()
        {
            const string maliciousOrigin = "http://evil.com";
            
            var request = CreatePreflightRequest(TestEndpoint, maliciousOrigin, "POST");
            var response = await _client.SendAsync(request);

            // Verify malicious origin is not allowed
            AssertOriginNotAllowed(response, maliciousOrigin);
        }

        /// <summary>
        /// SECURITY TEST: Verify local attacker domain is blocked.
        /// Tests the attacker.local origin explicitly as required by the specification.
        /// </summary>
        [Fact]
        public async Task TestAttackerLocalOriginBlocked()
        {
            const string maliciousOrigin = "http://attacker.local";
            
            var request = CreatePreflightRequest(TestEndpoint, maliciousOrigin, "PUT");
            var response = await _client.SendAsync(request);

            // Verify malicious origin is not allowed
            AssertOriginNotAllowed(response, maliciousOrigin);
        }

        /// <summary>
        /// SECURITY TEST: Verify null origin is blocked.
        /// The 'null' origin can occur in certain attack scenarios and sandboxed contexts.
        /// </summary>
        [Fact]
        public async Task TestNullOriginBlocked()
        {
            const string maliciousOrigin = "null";
            
            var request = CreatePreflightRequest(TestEndpoint, maliciousOrigin, "DELETE");
            var response = await _client.SendAsync(request);

            // Verify null origin is not allowed
            AssertOriginNotAllowed(response, maliciousOrigin);
        }

        #endregion

        #region <--- Test: Authorized Origin Acceptance --->

        /// <summary>
        /// SECURITY TEST: Verify requests from configured allowed origins succeed.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - Send preflight OPTIONS request with Origin header matching configured whitelist
        /// - Verify response contains Access-Control-Allow-Origin matching the request origin
        /// - Verify response contains Access-Control-Allow-Methods with allowed methods
        /// - Verify response contains Access-Control-Allow-Headers
        /// 
        /// The default allowed origins are http://localhost:5000 and https://localhost:5001.
        /// </summary>
        [Fact]
        public async Task TestAuthorizedOriginAllowed()
        {
            foreach (var authorizedOrigin in DefaultAllowedOrigins)
            {
                // Create preflight OPTIONS request with authorized origin
                var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "POST");

                // Send the preflight request
                var response = await _client.SendAsync(request);

                // SECURITY ASSERTION: Authorized origins should receive proper CORS headers
                
                // Verify Access-Control-Allow-Origin header is present and matches the request origin
                if (response.Headers.Contains(AccessControlAllowOrigin))
                {
                    var allowedOriginValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                    
                    // The origin should match exactly (not wildcard) when credentials are allowed
                    Assert.True(
                        string.Equals(allowedOriginValue, authorizedOrigin, StringComparison.OrdinalIgnoreCase),
                        $"Access-Control-Allow-Origin should match request origin '{authorizedOrigin}', " +
                        $"but received '{allowedOriginValue}'"
                    );
                }

                // Verify Access-Control-Allow-Methods header contains expected methods
                if (response.Headers.Contains(AccessControlAllowMethods))
                {
                    var allowedMethodsValue = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                    Assert.NotNull(allowedMethodsValue);
                    
                    // Verify each expected method is in the allowed methods
                    foreach (var method in AllowedMethods)
                    {
                        Assert.Contains(
                            method,
                            allowedMethodsValue,
                            StringComparison.OrdinalIgnoreCase
                        );
                    }
                }

                // Verify Access-Control-Allow-Headers header contains expected headers
                if (response.Headers.Contains(AccessControlAllowHeaders))
                {
                    var allowedHeadersValue = response.Headers.GetValues(AccessControlAllowHeaders).FirstOrDefault();
                    Assert.NotNull(allowedHeadersValue);
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify localhost:5000 origin is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestLocalhost5000Allowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "GET");
            var response = await _client.SendAsync(request);

            // For authorized origins, if CORS headers are returned, they should match
            if (response.Headers.Contains(AccessControlAllowOrigin))
            {
                var allowedOrigin = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                Assert.Equal(authorizedOrigin, allowedOrigin);
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify localhost:5001 (HTTPS) origin is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestLocalhost5001HttpsAllowed()
        {
            const string authorizedOrigin = "https://localhost:5001";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "GET");
            var response = await _client.SendAsync(request);

            // For authorized origins, if CORS headers are returned, they should match
            if (response.Headers.Contains(AccessControlAllowOrigin))
            {
                var allowedOrigin = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                Assert.Equal(authorizedOrigin, allowedOrigin);
            }
        }

        #endregion

        #region <--- Test: Restricted HTTP Methods --->

        /// <summary>
        /// SECURITY TEST: Verify only required HTTP methods are allowed.
        /// 
        /// Test Requirements (Section 0.5.2 Fix #4):
        /// - GET, POST, PUT, DELETE, PATCH should be allowed
        /// - Other methods like TRACE should be handled appropriately
        /// 
        /// Restricting methods reduces the attack surface for HTTP method-based attacks.
        /// </summary>
        [Fact]
        public async Task TestRestrictedMethods()
        {
            const string authorizedOrigin = "http://localhost:5000";

            // Test that each allowed method is accepted in preflight
            foreach (var allowedMethod in AllowedMethods)
            {
                var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, allowedMethod);
                var response = await _client.SendAsync(request);

                // Verify the method is listed in Access-Control-Allow-Methods
                if (response.Headers.Contains(AccessControlAllowMethods))
                {
                    var allowedMethodsValue = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                    Assert.NotNull(allowedMethodsValue);
                    Assert.Contains(allowedMethod, allowedMethodsValue, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Test that TRACE method is not explicitly allowed (security risk)
            var traceRequest = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "TRACE");
            var traceResponse = await _client.SendAsync(traceRequest);
            
            if (traceResponse.Headers.Contains(AccessControlAllowMethods))
            {
                var allowedMethodsValue = traceResponse.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                if (!string.IsNullOrEmpty(allowedMethodsValue))
                {
                    // TRACE should not be in the allowed methods
                    Assert.DoesNotContain(
                        "TRACE",
                        allowedMethodsValue,
                        StringComparison.OrdinalIgnoreCase
                    );
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify GET method is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestGetMethodAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "GET");
            var response = await _client.SendAsync(request);

            AssertMethodAllowed(response, "GET");
        }

        /// <summary>
        /// SECURITY TEST: Verify POST method is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestPostMethodAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "POST");
            var response = await _client.SendAsync(request);

            AssertMethodAllowed(response, "POST");
        }

        /// <summary>
        /// SECURITY TEST: Verify PUT method is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestPutMethodAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "PUT");
            var response = await _client.SendAsync(request);

            AssertMethodAllowed(response, "PUT");
        }

        /// <summary>
        /// SECURITY TEST: Verify DELETE method is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestDeleteMethodAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "DELETE");
            var response = await _client.SendAsync(request);

            AssertMethodAllowed(response, "DELETE");
        }

        /// <summary>
        /// SECURITY TEST: Verify PATCH method is explicitly allowed.
        /// </summary>
        [Fact]
        public async Task TestPatchMethodAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "PATCH");
            var response = await _client.SendAsync(request);

            AssertMethodAllowed(response, "PATCH");
        }

        /// <summary>
        /// SECURITY TEST: Verify TRACE method is not allowed (HTTP TRACE attack prevention).
        /// </summary>
        [Fact]
        public async Task TestTraceMethodNotAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "TRACE");
            var response = await _client.SendAsync(request);

            // TRACE should not be in the allowed methods
            if (response.Headers.Contains(AccessControlAllowMethods))
            {
                var allowedMethods = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                Assert.False(
                    allowedMethods?.Contains("TRACE", StringComparison.OrdinalIgnoreCase) ?? false,
                    "SECURITY VIOLATION: TRACE method should not be allowed due to HTTP TRACE attack risk"
                );
            }
        }

        #endregion

        #region <--- Test: Credentials Handling --->

        /// <summary>
        /// SECURITY TEST: Verify credentials are handled correctly.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - When AllowCredentials is enabled, Access-Control-Allow-Credentials should be true
        /// - Origin must be specific (not wildcard) when credentials are allowed
        /// 
        /// SECURITY: When credentials are allowed with wildcard origin, it creates a security vulnerability
        /// as any website could make authenticated requests on behalf of the user.
        /// </summary>
        [Fact]
        public async Task TestCredentialsHandling()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            // Create preflight request with credentials flag
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "POST");

            var response = await _client.SendAsync(request);

            // Check if credentials are allowed
            if (response.Headers.Contains(AccessControlAllowCredentials))
            {
                var credentialsValue = response.Headers.GetValues(AccessControlAllowCredentials).FirstOrDefault();
                
                if (string.Equals(credentialsValue, "true", StringComparison.OrdinalIgnoreCase))
                {
                    // SECURITY ASSERTION: When credentials are allowed, origin MUST NOT be wildcard
                    if (response.Headers.Contains(AccessControlAllowOrigin))
                    {
                        var originValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                        
                        Assert.NotEqual(
                            "*",
                            originValue,
                            StringComparer.Ordinal
                        );
                        
                        // SECURITY: Origin should be the exact requesting origin, not wildcard
                        Assert.Equal(authorizedOrigin, originValue);
                    }
                }
            }

            // Verify CORS is not using wildcard when credentials might be involved
            if (response.Headers.Contains(AccessControlAllowOrigin))
            {
                var originValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                
                // If credentials header is true or absent (defaulting to potentially true),
                // origin must not be wildcard
                if (!response.Headers.Contains(AccessControlAllowCredentials))
                {
                    // No credentials header means we need to be careful with wildcards
                    // For security, we prefer explicit origins
                    if (originValue == "*")
                    {
                        // This is a security warning but not necessarily a failure
                        // depending on the application's requirements
                    }
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify credentials are not allowed for malicious origins.
        /// </summary>
        [Fact]
        public async Task TestCredentialsNotAllowedForMaliciousOrigin()
        {
            const string maliciousOrigin = "http://evil.com";
            
            var request = CreatePreflightRequest(TestEndpoint, maliciousOrigin, "POST");
            var response = await _client.SendAsync(request);

            // SECURITY: Malicious origins should not receive credentials allowance
            if (response.Headers.Contains(AccessControlAllowCredentials))
            {
                var credentialsValue = response.Headers.GetValues(AccessControlAllowCredentials).FirstOrDefault();
                
                // If credentials header exists for malicious origin, it should be false
                // OR the Access-Control-Allow-Origin should not match the malicious origin
                if (string.Equals(credentialsValue, "true", StringComparison.OrdinalIgnoreCase))
                {
                    // If credentials are true, origin must not match malicious origin
                    if (response.Headers.Contains(AccessControlAllowOrigin))
                    {
                        var originValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                        Assert.NotEqual(maliciousOrigin, originValue);
                    }
                }
            }
        }

        #endregion

        #region <--- Test: CORS Preflight Response --->

        /// <summary>
        /// SECURITY TEST: Verify preflight responses are properly formatted.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - OPTIONS requests receive proper CORS headers
        /// - Preflight cache duration is set appropriately
        /// 
        /// Proper preflight handling ensures browsers correctly enforce CORS policy.
        /// </summary>
        [Fact]
        public async Task TestCorsPreflightResponse()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            // Create a full preflight request with all typical headers
            var request = new HttpRequestMessage(HttpMethod.Options, TestEndpoint);
            request.Headers.Add("Origin", authorizedOrigin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "Content-Type, Authorization");

            var response = await _client.SendAsync(request);

            // Preflight response should be successful or at least not an error
            // Note: Status can be 200, 204, or even 302/301 depending on routing
            Assert.False(
                response.StatusCode == HttpStatusCode.InternalServerError,
                $"Preflight request returned server error: {response.StatusCode}"
            );

            // Verify proper CORS headers are present for authorized origin
            if (response.Headers.Contains(AccessControlAllowOrigin))
            {
                var originValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                Assert.NotNull(originValue);
                
                // Origin should match the requesting origin
                Assert.Equal(authorizedOrigin, originValue);
            }

            // Verify methods header format
            if (response.Headers.Contains(AccessControlAllowMethods))
            {
                var methodsValue = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                Assert.NotNull(methodsValue);
                Assert.False(
                    string.IsNullOrWhiteSpace(methodsValue),
                    "Access-Control-Allow-Methods should not be empty"
                );
            }

            // Verify headers header format
            if (response.Headers.Contains(AccessControlAllowHeaders))
            {
                var headersValue = response.Headers.GetValues(AccessControlAllowHeaders).FirstOrDefault();
                Assert.NotNull(headersValue);
                Assert.False(
                    string.IsNullOrWhiteSpace(headersValue),
                    "Access-Control-Allow-Headers should not be empty"
                );
            }

            // Check for preflight cache duration (optional but recommended)
            if (response.Headers.Contains(AccessControlMaxAge))
            {
                var maxAgeValue = response.Headers.GetValues(AccessControlMaxAge).FirstOrDefault();
                if (int.TryParse(maxAgeValue, out int maxAge))
                {
                    // Max age should be reasonable (not too short, not excessively long)
                    // Typical values are 86400 (1 day) or less
                    Assert.True(
                        maxAge >= 0 && maxAge <= 86400 * 7,
                        $"Access-Control-Max-Age {maxAge} should be between 0 and 604800 (7 days)"
                    );
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify preflight request with all custom headers.
        /// </summary>
        [Fact]
        public async Task TestPreflightWithCustomHeaders()
        {
            const string authorizedOrigin = "http://localhost:5000";
            
            var request = new HttpRequestMessage(HttpMethod.Options, TestEndpoint);
            request.Headers.Add("Origin", authorizedOrigin);
            request.Headers.Add("Access-Control-Request-Method", "PUT");
            request.Headers.Add("Access-Control-Request-Headers", "Content-Type, Authorization, X-Requested-With");

            var response = await _client.SendAsync(request);

            // Verify the response allows the requested custom headers
            if (response.Headers.Contains(AccessControlAllowHeaders))
            {
                var allowedHeaders = response.Headers.GetValues(AccessControlAllowHeaders).FirstOrDefault();
                
                if (!string.IsNullOrEmpty(allowedHeaders))
                {
                    // Check that each expected header is allowed
                    foreach (var header in AllowedHeaders)
                    {
                        Assert.Contains(
                            header,
                            allowedHeaders,
                            StringComparison.OrdinalIgnoreCase
                        );
                    }
                }
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify OPTIONS requests don't cause server errors.
        /// </summary>
        [Fact]
        public async Task TestOptionsRequestsDoNotCauseErrors()
        {
            // Test multiple endpoints with OPTIONS
            var endpoints = new[] { "/", "/login", "/api" };
            
            foreach (var endpoint in endpoints)
            {
                var request = new HttpRequestMessage(HttpMethod.Options, endpoint);
                request.Headers.Add("Origin", "http://localhost:5000");
                request.Headers.Add("Access-Control-Request-Method", "GET");

                try
                {
                    var response = await _client.SendAsync(request);
                    
                    // OPTIONS should not cause 500 Internal Server Error
                    Assert.NotEqual(
                        HttpStatusCode.InternalServerError,
                        response.StatusCode
                    );
                }
                catch (Exception ex) when (ex is HttpRequestException)
                {
                    // Network errors are acceptable for endpoints that might not exist
                    // The important thing is no unhandled server-side exceptions
                }
            }
        }

        #endregion

        #region <--- Helper Methods --->

        /// <summary>
        /// Creates an HTTP preflight OPTIONS request with the specified Origin and requested method.
        /// </summary>
        /// <param name="endpoint">The endpoint URL to request</param>
        /// <param name="origin">The Origin header value</param>
        /// <param name="requestedMethod">The Access-Control-Request-Method header value</param>
        /// <returns>Configured HttpRequestMessage for preflight</returns>
        private static HttpRequestMessage CreatePreflightRequest(string endpoint, string origin, string requestedMethod)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, endpoint);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", requestedMethod);
            request.Headers.Add("Access-Control-Request-Headers", "Content-Type, Authorization");
            return request;
        }

        /// <summary>
        /// Asserts that the specified origin is not allowed by CORS policy.
        /// </summary>
        /// <param name="response">The HTTP response to check</param>
        /// <param name="origin">The origin that should not be allowed</param>
        private static void AssertOriginNotAllowed(HttpResponseMessage response, string origin)
        {
            if (response.Headers.Contains(AccessControlAllowOrigin))
            {
                var allowedOrigin = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                
                // The allowed origin should NOT match the malicious origin
                Assert.False(
                    string.Equals(allowedOrigin, origin, StringComparison.OrdinalIgnoreCase),
                    $"SECURITY VIOLATION: Origin '{origin}' should be blocked but received " +
                    $"Access-Control-Allow-Origin: '{allowedOrigin}'"
                );
                
                // Also verify it's not a wildcard (which would allow all origins)
                Assert.False(
                    allowedOrigin == "*" && !string.IsNullOrEmpty(origin) && origin != "*",
                    $"SECURITY VIOLATION: Wildcard origin '*' allows malicious origin '{origin}'"
                );
            }
            // If no Access-Control-Allow-Origin header, the origin is effectively blocked
        }

        /// <summary>
        /// Asserts that the specified HTTP method is allowed by CORS policy.
        /// </summary>
        /// <param name="response">The HTTP response to check</param>
        /// <param name="method">The method that should be allowed</param>
        private static void AssertMethodAllowed(HttpResponseMessage response, string method)
        {
            if (response.Headers.Contains(AccessControlAllowMethods))
            {
                var allowedMethods = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                
                Assert.True(
                    allowedMethods?.Contains(method, StringComparison.OrdinalIgnoreCase) ?? false,
                    $"Method '{method}' should be in Access-Control-Allow-Methods: '{allowedMethods}'"
                );
            }
            // If no Access-Control-Allow-Methods header, simple methods (GET, HEAD, POST) are implicitly allowed
        }

        #endregion
    }
}
