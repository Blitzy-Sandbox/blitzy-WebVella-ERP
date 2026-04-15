using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    /// SECURITY: Regression test suite for CORS (Cross-Origin Resource Sharing) policy enforcement.
    /// Tests CWE-942 (Overly Permissive Cross-domain Whitelist) vulnerability mitigation.
    /// 
    /// This test suite validates the CORS security configuration that was fixed in Startup.cs:
    /// - Previous vulnerable code: AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
    /// - Fixed secure code: WithOrigins(configuredOrigins).WithMethods(...).AllowCredentials()
    /// 
    /// The tests use a minimal in-memory test server that mirrors the production CORS configuration
    /// without requiring database connections or other ERP-specific dependencies. This isolation
    /// ensures focused security testing of the CORS middleware behavior.
    /// 
    /// CRITICAL SECURITY IMPACT:
    /// Without proper CORS policy:
    /// - Cross-site request forgery (CSRF) attacks possible
    /// - Data theft from authenticated users via malicious websites
    /// - Session hijacking through cross-origin requests
    /// 
    /// Run with: dotnet test --filter "Category=Security"
    /// </summary>
    [Trait("Category", "Security")]
    public class CorsSecurityTests : IDisposable
    {
        #region <--- Test Constants --->

        /// <summary>
        /// Default allowed origins as configured in Startup.cs when no AllowedOrigins setting is specified.
        /// These match the fallback values in Startup.cs line 56-57.
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
        /// HTTP methods allowed by the CORS policy per Section 0.5.2 Fix #4 and Startup.cs line 63.
        /// </summary>
        private static readonly string[] AllowedMethods = new[]
        {
            "GET", "POST", "PUT", "DELETE", "PATCH"
        };

        /// <summary>
        /// HTTP methods that should NOT be in the allowed methods list.
        /// TRACE is particularly dangerous for security (enables XST attacks).
        /// </summary>
        private static readonly string[] DisallowedMethods = new[]
        {
            "TRACE", "CONNECT", "HEAD" // HEAD might be implicitly allowed by browsers for simple requests
        };

        /// <summary>
        /// HTTP headers allowed by the CORS policy as configured in Startup.cs line 64.
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
        /// Test endpoint for CORS validation.
        /// </summary>
        private const string TestEndpoint = "/api/test";

        #endregion

        #region <--- Test Fixture --->

        private readonly IHost _host;
        private readonly HttpClient _client;

        /// <summary>
        /// Initializes a minimal test server with CORS configuration matching production Startup.cs.
        /// This approach avoids database dependencies while accurately testing CORS security behavior.
        /// </summary>
        public CorsSecurityTests()
        {
            _host = CreateTestHost(DefaultAllowedOrigins);
            _client = _host.GetTestClient();
        }

        /// <summary>
        /// Creates a minimal ASP.NET Core host with CORS configuration matching Startup.cs.
        /// </summary>
        /// <param name="allowedOrigins">Origins to allow in CORS policy</param>
        /// <returns>Configured and started IHost</returns>
        private static IHost CreateTestHost(string[] allowedOrigins)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        // Configure CORS exactly as in Startup.cs lines 59-66
                        services.AddCors(options =>
                        {
                            options.AddDefaultPolicy(policy =>
                                policy.WithOrigins(allowedOrigins)
                                    .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH")
                                    .WithHeaders("Content-Type", "Authorization", "X-Requested-With")
                                    .AllowCredentials());
                        });
                        services.AddRouting();
                    });
                    webBuilder.Configure(app =>
                    {
                        // Apply CORS middleware as in Startup.cs line 188
                        app.UseCors();
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
                            endpoints.MapPut(TestEndpoint, async context =>
                            {
                                context.Response.StatusCode = 200;
                                await context.Response.WriteAsync("OK");
                            });
                            endpoints.MapDelete(TestEndpoint, async context =>
                            {
                                context.Response.StatusCode = 200;
                                await context.Response.WriteAsync("OK");
                            });
                            endpoints.MapMethods(TestEndpoint, new[] { "PATCH" }, async context =>
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
        /// Disposes the test host and client.
        /// </summary>
        public void Dispose()
        {
            _client?.Dispose();
            _host?.Dispose();
            GC.SuppressFinalize(this);
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
                AssertOriginNotAllowed(response, maliciousOrigin);
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

        /// <summary>
        /// SECURITY TEST: Verify different port on localhost is blocked.
        /// This ensures port-specific whitelisting is enforced.
        /// </summary>
        [Fact]
        public async Task TestDifferentPortBlocked()
        {
            const string maliciousOrigin = "http://localhost:9999";
            
            var request = CreatePreflightRequest(TestEndpoint, maliciousOrigin, "GET");
            var response = await _client.SendAsync(request);

            // Verify different port is not allowed
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
                AssertOriginAllowed(response, authorizedOrigin);
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

            AssertOriginAllowed(response, authorizedOrigin);
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

            AssertOriginAllowed(response, authorizedOrigin);
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

            // Test that each allowed method is in the Access-Control-Allow-Methods response
            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "GET");
            var response = await _client.SendAsync(request);

            Assert.True(response.Headers.Contains(AccessControlAllowMethods),
                "Access-Control-Allow-Methods header should be present");

            var allowedMethodsValue = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
            Assert.NotNull(allowedMethodsValue);

            // Verify each expected method is in the allowed methods
            foreach (var method in AllowedMethods)
            {
                Assert.Contains(method, allowedMethodsValue, StringComparison.OrdinalIgnoreCase);
            }

            // Verify TRACE is NOT in the allowed methods (security risk)
            Assert.DoesNotContain("TRACE", allowedMethodsValue, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// SECURITY TEST: Verify each allowed method individually.
        /// Tests GET, POST, PUT, DELETE, PATCH are all listed in allowed methods.
        /// </summary>
        [Theory]
        [InlineData("GET")]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("PATCH")]
        public async Task TestAllowedMethodsIndividually(string method)
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, method);
            var response = await _client.SendAsync(request);

            Assert.True(response.Headers.Contains(AccessControlAllowMethods),
                $"Access-Control-Allow-Methods header should be present for {method}");

            var allowedMethodsValue = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
            Assert.Contains(method, allowedMethodsValue!, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// SECURITY TEST: Verify TRACE method is not allowed.
        /// TRACE can be used for Cross-Site Tracing (XST) attacks.
        /// </summary>
        [Fact]
        public async Task TestTraceMethodNotAllowed()
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "TRACE");
            var response = await _client.SendAsync(request);

            if (response.Headers.Contains(AccessControlAllowMethods))
            {
                var allowedMethodsValue = response.Headers.GetValues(AccessControlAllowMethods).FirstOrDefault();
                if (!string.IsNullOrEmpty(allowedMethodsValue))
                {
                    Assert.DoesNotContain("TRACE", allowedMethodsValue, StringComparison.OrdinalIgnoreCase);
                }
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
        /// CRITICAL: Allowing credentials with wildcard origin (*) is a security vulnerability.
        /// </summary>
        [Fact]
        public async Task TestCredentialsHandling()
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "POST");
            var response = await _client.SendAsync(request);

            // Verify Access-Control-Allow-Credentials is true
            Assert.True(response.Headers.Contains(AccessControlAllowCredentials),
                "Access-Control-Allow-Credentials header should be present");

            var credentialsValue = response.Headers.GetValues(AccessControlAllowCredentials).FirstOrDefault();
            Assert.Equal("true", credentialsValue, StringComparer.OrdinalIgnoreCase);

            // Verify Origin is NOT wildcard when credentials are allowed
            Assert.True(response.Headers.Contains(AccessControlAllowOrigin),
                "Access-Control-Allow-Origin header should be present");

            var originValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
            Assert.NotEqual("*", originValue);
            Assert.Equal(authorizedOrigin, originValue);
        }

        /// <summary>
        /// SECURITY TEST: Verify wildcard origin is never used with credentials.
        /// </summary>
        [Fact]
        public async Task TestNoWildcardWithCredentials()
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "GET");
            var response = await _client.SendAsync(request);

            if (response.Headers.Contains(AccessControlAllowCredentials))
            {
                var credentialsValue = response.Headers.GetValues(AccessControlAllowCredentials).FirstOrDefault();
                if (string.Equals(credentialsValue, "true", StringComparison.OrdinalIgnoreCase))
                {
                    // When credentials are allowed, origin must NOT be wildcard
                    if (response.Headers.Contains(AccessControlAllowOrigin))
                    {
                        var originValue = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
                        Assert.True(originValue != "*",
                            "SECURITY VIOLATION: Wildcard origin (*) must not be used when credentials are allowed");
                    }
                }
            }
        }

        #endregion

        #region <--- Test: Preflight Response Format --->

        /// <summary>
        /// SECURITY TEST: Verify preflight responses are properly formatted.
        /// 
        /// Test Requirements (Section 0.8.1):
        /// - OPTIONS requests receive proper CORS headers
        /// - Preflight responses include required headers
        /// </summary>
        [Fact]
        public async Task TestCorsPreflightResponse()
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = CreatePreflightRequest(TestEndpoint, authorizedOrigin, "POST");
            var response = await _client.SendAsync(request);

            // OPTIONS preflight should succeed
            Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NoContent,
                $"Preflight request should succeed, got {response.StatusCode}");

            // Verify all required CORS headers are present for authorized origin
            Assert.True(response.Headers.Contains(AccessControlAllowOrigin),
                "Access-Control-Allow-Origin header missing");
            Assert.True(response.Headers.Contains(AccessControlAllowMethods),
                "Access-Control-Allow-Methods header missing");
            Assert.True(response.Headers.Contains(AccessControlAllowHeaders),
                "Access-Control-Allow-Headers header missing");

            // Verify allowed headers include the configured headers
            var allowedHeadersValue = response.Headers.GetValues(AccessControlAllowHeaders).FirstOrDefault();
            Assert.NotNull(allowedHeadersValue);
            foreach (var header in AllowedHeaders)
            {
                Assert.Contains(header, allowedHeadersValue, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify OPTIONS requests don't cause server errors.
        /// </summary>
        [Fact]
        public async Task TestOptionsRequestsDoNotCauseErrors()
        {
            var request = new HttpRequestMessage(HttpMethod.Options, TestEndpoint);
            request.Headers.Add("Origin", "http://localhost:5000");
            request.Headers.Add("Access-Control-Request-Method", "GET");

            var response = await _client.SendAsync(request);

            // OPTIONS should not cause 500 Internal Server Error
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        /// <summary>
        /// SECURITY TEST: Verify preflight response for POST with JSON content type.
        /// This is a common scenario for API requests.
        /// </summary>
        [Fact]
        public async Task TestPreflightForJsonRequest()
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = new HttpRequestMessage(HttpMethod.Options, TestEndpoint);
            request.Headers.Add("Origin", authorizedOrigin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "Content-Type");

            var response = await _client.SendAsync(request);

            // Verify Content-Type header is allowed
            if (response.Headers.Contains(AccessControlAllowHeaders))
            {
                var allowedHeaders = response.Headers.GetValues(AccessControlAllowHeaders).FirstOrDefault();
                Assert.Contains("Content-Type", allowedHeaders!, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// SECURITY TEST: Verify preflight response for request with Authorization header.
        /// This is required for authenticated API requests.
        /// </summary>
        [Fact]
        public async Task TestPreflightForAuthenticatedRequest()
        {
            const string authorizedOrigin = "http://localhost:5000";

            var request = new HttpRequestMessage(HttpMethod.Options, TestEndpoint);
            request.Headers.Add("Origin", authorizedOrigin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "Authorization");

            var response = await _client.SendAsync(request);

            // Verify Authorization header is allowed
            if (response.Headers.Contains(AccessControlAllowHeaders))
            {
                var allowedHeaders = response.Headers.GetValues(AccessControlAllowHeaders).FirstOrDefault();
                Assert.Contains("Authorization", allowedHeaders!, StringComparison.OrdinalIgnoreCase);
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
            // If no Access-Control-Allow-Origin header, the origin is effectively blocked - this is correct
        }

        /// <summary>
        /// Asserts that the specified origin is allowed by CORS policy.
        /// </summary>
        /// <param name="response">The HTTP response to check</param>
        /// <param name="origin">The origin that should be allowed</param>
        private static void AssertOriginAllowed(HttpResponseMessage response, string origin)
        {
            Assert.True(response.Headers.Contains(AccessControlAllowOrigin),
                $"Access-Control-Allow-Origin header should be present for allowed origin '{origin}'");

            var allowedOrigin = response.Headers.GetValues(AccessControlAllowOrigin).FirstOrDefault();
            
            // The origin should match exactly (not wildcard) since credentials are allowed
            Assert.True(string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase),
                $"Access-Control-Allow-Origin should match request origin '{origin}', but received '{allowedOrigin}'");
        }

        #endregion
    }
}
