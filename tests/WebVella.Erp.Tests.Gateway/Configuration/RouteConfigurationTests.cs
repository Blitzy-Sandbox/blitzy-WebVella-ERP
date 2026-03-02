using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using WebVella.Erp.Gateway.Configuration;

namespace WebVella.Erp.Tests.Gateway.Configuration
{
    /// <summary>
    /// Comprehensive unit tests for <see cref="RouteConfiguration"/>.
    /// Validates service URL resolution, route-to-service matching, gRPC endpoint resolution,
    /// configuration binding via IConfiguration, and backward compatibility with all legacy
    /// monolith API route patterns from WebApiController.cs, AdminController.cs, and ProjectController.cs.
    /// </summary>
    public class RouteConfigurationTests
    {
        #region Helper Methods

        /// <summary>
        /// Creates a fully-populated <see cref="RouteConfiguration"/> instance with all known
        /// API route prefix mappings from the monolith source controllers. Used by multiple tests
        /// to avoid duplication and ensure consistent route coverage.
        /// </summary>
        /// <returns>A RouteConfiguration with RouteMappings populated for all known API prefixes.</returns>
        private RouteConfiguration CreateDefaultRouteConfiguration()
        {
            var config = new RouteConfiguration();
            config.RouteMappings = new Dictionary<string, string>
            {
                // Admin/SDK service routes (from AdminController.cs)
                { "/api/v3.0/p/sdk", "AdminServiceUrl" },

                // Project service routes (from ProjectController.cs)
                { "/api/v3.0/p/project", "ProjectServiceUrl" },

                // Core service routes — versioned path prefix routes (from WebApiController.cs)
                { "/api/v3.0/p/core", "CoreServiceUrl" },
                { "/api/v3.0/pc", "CoreServiceUrl" },
                { "/api/v3.0/page", "CoreServiceUrl" },
                { "/api/v3.0/datasource", "CoreServiceUrl" },
                { "/api/v3.0/user/preferences", "CoreServiceUrl" },

                // Core service routes — locale-prefixed API v3 routes (from WebApiController.cs)
                { "/api/v3/en_US/eql", "CoreServiceUrl" },
                { "/api/v3/en_US/auth/jwt", "CoreServiceUrl" },
                { "/api/v3/en_US/meta", "CoreServiceUrl" },
                { "/api/v3/en_US/record", "CoreServiceUrl" },
                { "/api/v3/en_US/quick-search", "CoreServiceUrl" },
                { "/api/v3/en_US/plugin", "CoreServiceUrl" },
                { "/api/v3/en_US/jobs", "CoreServiceUrl" },
                { "/api/v3/en_US/scheduleplan", "CoreServiceUrl" },
                { "/api/v3/en_US/system-log", "CoreServiceUrl" },
                { "/api/v3/en_US/user_file", "CoreServiceUrl" },
                { "/api/v3/en_US/snippets", "CoreServiceUrl" },
                { "/api/v3/en_US/snippet", "CoreServiceUrl" },
                { "/api/v3/en_US/entity", "CoreServiceUrl" },
                { "/api/v3/en_US/search", "CoreServiceUrl" },
                { "/api/v3/en_US/file", "CoreServiceUrl" },

                // CRM service routes
                { "/api/v3/en_US/crm", "CrmServiceUrl" },

                // Mail service routes
                { "/api/v3/en_US/mail", "MailServiceUrl" },

                // Reporting service routes
                { "/api/v3/en_US/report", "ReportingServiceUrl" },

                // File serving route (from WebApiController.cs line 3253)
                { "/fs", "CoreServiceUrl" }
            };
            return config;
        }

        #endregion

        #region Default Property Value Tests

        /// <summary>
        /// Verifies that all backend service URL properties default to Docker Compose service names
        /// with port 8080, matching the docker-compose.yml configuration (AAP Section 0.7.4).
        /// </summary>
        [Fact]
        public void DefaultValues_ShouldMatchDockerComposeServiceNames()
        {
            // Arrange & Act
            var config = new RouteConfiguration();

            // Assert — each default URL must match Docker Compose service naming convention
            config.CoreServiceUrl.Should().Be("http://core-service:8080");
            config.CrmServiceUrl.Should().Be("http://crm-service:8080");
            config.ProjectServiceUrl.Should().Be("http://project-service:8080");
            config.MailServiceUrl.Should().Be("http://mail-service:8080");
            config.ReportingServiceUrl.Should().Be("http://reporting-service:8080");
            config.AdminServiceUrl.Should().Be("http://admin-service:8080");
        }

        /// <summary>
        /// Verifies that gRPC endpoint properties default to Docker Compose service names
        /// with port 5001 (gRPC HTTP/2 convention).
        /// </summary>
        [Fact]
        public void DefaultGrpcEndpoints_ShouldMatchDockerComposeServiceNames()
        {
            // Arrange & Act
            var config = new RouteConfiguration();

            // Assert — gRPC endpoints use port 5001 by convention
            config.CoreServiceGrpc.Should().Be("http://core-service:5001");
            config.CrmServiceGrpc.Should().Be("http://crm-service:5001");
            config.ProjectServiceGrpc.Should().Be("http://project-service:5001");
            config.MailServiceGrpc.Should().Be("http://mail-service:5001");
        }

        /// <summary>
        /// Verifies that RouteMappings dictionary is initialized as empty (not null) by default,
        /// allowing safe iteration without null checks.
        /// </summary>
        [Fact]
        public void RouteMappings_DefaultsToEmptyDictionary()
        {
            // Arrange & Act & Assert
            new RouteConfiguration().RouteMappings.Should().NotBeNull().And.BeEmpty();
        }

        #endregion

        #region GetServiceUrl Tests

        /// <summary>
        /// Verifies that GetServiceUrl resolves the correct backend URL for all valid service keys
        /// using the default Docker Compose URLs.
        /// </summary>
        [Theory]
        [InlineData("CoreServiceUrl", "http://core-service:8080")]
        [InlineData("CrmServiceUrl", "http://crm-service:8080")]
        [InlineData("ProjectServiceUrl", "http://project-service:8080")]
        [InlineData("MailServiceUrl", "http://mail-service:8080")]
        [InlineData("ReportingServiceUrl", "http://reporting-service:8080")]
        [InlineData("AdminServiceUrl", "http://admin-service:8080")]
        public void GetServiceUrl_ReturnsCorrectUrl_ForAllValidServiceKeys(string serviceKey, string expectedUrl)
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var result = config.GetServiceUrl(serviceKey);

            // Assert
            result.Should().Be(expectedUrl);
        }

        /// <summary>
        /// Verifies that GetServiceUrl throws ArgumentException with correct parameter name
        /// when an unknown service key is provided.
        /// </summary>
        [Fact]
        public void GetServiceUrl_ThrowsArgumentException_ForUnknownServiceKey()
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var act = () => config.GetServiceUrl("UnknownService");

            // Assert
            act.Should().Throw<ArgumentException>().WithParameterName("serviceKey");
        }

        /// <summary>
        /// Verifies that GetServiceUrl throws ArgumentException when null is passed as service key.
        /// Null does not match any named service pattern in the switch expression.
        /// </summary>
        [Fact]
        public void GetServiceUrl_ThrowsArgumentException_ForNullServiceKey()
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var act = () => config.GetServiceUrl(null!);

            // Assert
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Verifies that GetServiceUrl throws ArgumentException when an empty string is passed.
        /// Empty string does not match any named service pattern in the switch expression.
        /// </summary>
        [Fact]
        public void GetServiceUrl_ThrowsArgumentException_ForEmptyServiceKey()
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var act = () => config.GetServiceUrl("");

            // Assert
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Verifies that GetServiceUrl returns the custom URL when a service URL property
        /// has been overridden from its default value.
        /// </summary>
        [Fact]
        public void GetServiceUrl_ReturnsCustomUrl_WhenPropertyOverridden()
        {
            // Arrange
            var config = new RouteConfiguration();
            config.CoreServiceUrl = "http://custom-core:9090";

            // Act
            var result = config.GetServiceUrl("CoreServiceUrl");

            // Assert
            result.Should().Be("http://custom-core:9090");
        }

        #endregion

        #region FindMatchingRoute Tests — Known Route Matching

        /// <summary>
        /// Validates backward compatibility with all known API v3 route patterns from the monolith.
        /// Each route prefix must correctly resolve to its target microservice key.
        /// Route patterns sourced from WebApiController.cs, AdminController.cs, and ProjectController.cs.
        /// </summary>
        [Theory]
        [InlineData("/api/v3/en_US/eql", "CoreServiceUrl")]                                    // EQL query endpoint (WebApiController line 63)
        [InlineData("/api/v3.0/p/sdk/datasource/list", "AdminServiceUrl")]                      // SDK admin (AdminController line 39)
        [InlineData("/api/v3.0/p/project/pc-post-list/create", "ProjectServiceUrl")]            // Project (ProjectController line 56)
        [InlineData("/api/v3/en_US/entity/list", "CoreServiceUrl")]                             // Entity metadata
        [InlineData("/api/v3/en_US/record/account/some-id", "CoreServiceUrl")]                  // Record CRUD
        [InlineData("/api/v3/en_US/search/quick", "CoreServiceUrl")]                            // Quick search
        [InlineData("/api/v3/en_US/file/download", "CoreServiceUrl")]                           // File operations
        [InlineData("/api/v3/en_US/crm/accounts", "CrmServiceUrl")]                             // CRM endpoints
        [InlineData("/api/v3/en_US/mail/queue", "MailServiceUrl")]                              // Mail endpoints
        [InlineData("/api/v3/en_US/report/summary", "ReportingServiceUrl")]                     // Reporting
        [InlineData("/api/v3/en_US/auth/jwt/token", "CoreServiceUrl")]                          // JWT issuance (line 4274)
        [InlineData("/api/v3/en_US/auth/jwt/token/refresh", "CoreServiceUrl")]                  // JWT refresh (line 4293)
        [InlineData("/api/v3.0/p/core/styles.css", "CoreServiceUrl")]                           // Core styles (line 1039)
        [InlineData("/api/v3.0/pc/some-component/view/display", "CoreServiceUrl")]              // Page component (line 823)
        [InlineData("/api/v3.0/page/some-id/node/create", "CoreServiceUrl")]                    // Page builder (line 603)
        [InlineData("/api/v3.0/datasource/test", "CoreServiceUrl")]                             // Datasource (line 511)
        [InlineData("/api/v3.0/user/preferences/toggle-sidebar-size", "CoreServiceUrl")]        // User prefs (line 340)
        [InlineData("/api/v3/en_US/meta/entity/list", "CoreServiceUrl")]                        // Meta entity (line 1437)
        [InlineData("/api/v3/en_US/meta/relation/list", "CoreServiceUrl")]                      // Meta relation (line 2009)
        [InlineData("/api/v3/en_US/plugin/list", "CoreServiceUrl")]                             // Plugin list (line 3403)
        [InlineData("/api/v3/en_US/jobs", "CoreServiceUrl")]                                    // Jobs (line 3420)
        [InlineData("/api/v3/en_US/scheduleplan/list", "CoreServiceUrl")]                       // Schedule (line 3705)
        [InlineData("/api/v3/en_US/system-log", "CoreServiceUrl")]                              // System log (line 3817)
        [InlineData("/api/v3/en_US/user_file", "CoreServiceUrl")]                               // User file (line 3886)
        [InlineData("/api/v3/en_US/snippets", "CoreServiceUrl")]                                // Snippets (line 4234)
        [InlineData("/fs/some-file.pdf", "CoreServiceUrl")]                                     // File serving (line 3253)
        public void FindMatchingRoute_MatchesKnownRoutes(string requestPath, string expectedServiceKey)
        {
            // Arrange
            var config = CreateDefaultRouteConfiguration();

            // Act
            var result = config.FindMatchingRoute(requestPath);

            // Assert
            result.Should().NotBeNull();
            result!.Value.serviceKey.Should().Be(expectedServiceKey);
        }

        #endregion

        #region FindMatchingRoute Tests — Non-Matching and Edge Cases

        /// <summary>
        /// Verifies that paths outside known API route prefixes return null,
        /// indicating the Gateway should handle them locally (e.g., Razor Pages, static files).
        /// </summary>
        [Theory]
        [InlineData("/")]
        [InlineData("/login")]
        [InlineData("/static/file.js")]
        [InlineData("/home/index")]
        [InlineData("/swagger/v1/swagger.json")]
        public void FindMatchingRoute_ReturnsNull_ForNonMatchingPaths(string requestPath)
        {
            // Arrange
            var config = CreateDefaultRouteConfiguration();

            // Act & Assert
            config.FindMatchingRoute(requestPath).Should().BeNull();
        }

        /// <summary>
        /// Verifies that FindMatchingRoute safely returns null for null input
        /// without throwing an exception.
        /// </summary>
        [Fact]
        public void FindMatchingRoute_ReturnsNull_ForNullInput()
        {
            // Arrange
            var config = CreateDefaultRouteConfiguration();

            // Act & Assert
            config.FindMatchingRoute(null!).Should().BeNull();
        }

        /// <summary>
        /// Verifies that FindMatchingRoute returns null for empty string input
        /// without throwing an exception.
        /// </summary>
        [Fact]
        public void FindMatchingRoute_ReturnsNull_ForEmptyInput()
        {
            // Arrange
            var config = CreateDefaultRouteConfiguration();

            // Act & Assert
            config.FindMatchingRoute("").Should().BeNull();
        }

        /// <summary>
        /// Verifies that FindMatchingRoute returns null when the RouteMappings dictionary is empty,
        /// even if the request path matches a known API pattern.
        /// </summary>
        [Fact]
        public void FindMatchingRoute_ReturnsNull_WhenRouteMappingsIsEmpty()
        {
            // Arrange
            var config = new RouteConfiguration();
            config.RouteMappings = new Dictionary<string, string>();

            // Act & Assert
            config.FindMatchingRoute("/api/v3/en_US/eql").Should().BeNull();
        }

        /// <summary>
        /// Verifies that FindMatchingRoute performs case-insensitive matching using
        /// StringComparison.OrdinalIgnoreCase, supporting clients that may send
        /// mixed-case or uppercase URL paths.
        /// </summary>
        [Fact]
        public void FindMatchingRoute_IsCaseInsensitive()
        {
            // Arrange
            var config = CreateDefaultRouteConfiguration();

            // Act — test uppercase path
            var resultUpper = config.FindMatchingRoute("/API/V3/EN_US/EQL");

            // Assert — should match the /api/v3/en_US/eql prefix
            resultUpper.Should().NotBeNull();
            resultUpper!.Value.serviceKey.Should().Be("CoreServiceUrl");

            // Act — test mixed case path
            var resultMixed = config.FindMatchingRoute("/Api/V3/En_US/Eql");

            // Assert — should also match
            resultMixed.Should().NotBeNull();
            resultMixed!.Value.serviceKey.Should().Be("CoreServiceUrl");
        }

        /// <summary>
        /// Validates longest-prefix matching: when multiple route prefixes could match,
        /// the most specific (longest) prefix wins. This ensures "/api/v3.0/p/project/tasks"
        /// routes to ProjectServiceUrl via the "/api/v3.0/p/project" prefix rather than
        /// a shorter generic "/api/v3" prefix.
        /// </summary>
        [Fact]
        public void FindMatchingRoute_LongestPrefixMatch()
        {
            // Arrange — create config with overlapping prefixes of different lengths
            var config = new RouteConfiguration();
            config.RouteMappings = new Dictionary<string, string>
            {
                { "/api/v3", "CoreServiceUrl" },
                { "/api/v3.0/p/project", "ProjectServiceUrl" }
            };

            // Act
            var result = config.FindMatchingRoute("/api/v3.0/p/project/tasks");

            // Assert — longest prefix "/api/v3.0/p/project" should match, not "/api/v3"
            result.Should().NotBeNull();
            result!.Value.serviceKey.Should().Be("ProjectServiceUrl");
        }

        /// <summary>
        /// Validates that SDK-specific routes ("/api/v3.0/p/sdk/datasource/list") resolve to
        /// AdminServiceUrl rather than a shorter Core prefix. This ensures the Admin/SDK service
        /// correctly handles its own endpoints even when more generic prefixes overlap.
        /// </summary>
        [Fact]
        public void FindMatchingRoute_LongestPrefixMatch_SdkOverGeneric()
        {
            // Arrange — create config with overlapping SDK and generic prefixes
            var config = new RouteConfiguration();
            config.RouteMappings = new Dictionary<string, string>
            {
                { "/api/v3.0/p", "CoreServiceUrl" },
                { "/api/v3.0/p/sdk", "AdminServiceUrl" }
            };

            // Act
            var result = config.FindMatchingRoute("/api/v3.0/p/sdk/datasource/list");

            // Assert — longer "/api/v3.0/p/sdk" prefix should win over "/api/v3.0/p"
            result.Should().NotBeNull();
            result!.Value.serviceKey.Should().Be("AdminServiceUrl");
        }

        #endregion

        #region GetGrpcEndpoint Tests

        /// <summary>
        /// Verifies that GetGrpcEndpoint resolves the correct gRPC endpoint for all valid keys,
        /// accepting both short service name format ("Core") and full property name format ("CoreServiceUrl").
        /// </summary>
        [Theory]
        [InlineData("Core", "http://core-service:5001")]
        [InlineData("Crm", "http://crm-service:5001")]
        [InlineData("Project", "http://project-service:5001")]
        [InlineData("Mail", "http://mail-service:5001")]
        [InlineData("CoreServiceUrl", "http://core-service:5001")]
        [InlineData("CrmServiceUrl", "http://crm-service:5001")]
        [InlineData("ProjectServiceUrl", "http://project-service:5001")]
        [InlineData("MailServiceUrl", "http://mail-service:5001")]
        public void GetGrpcEndpoint_ReturnsCorrectEndpoint_ForAllValidKeys(string serviceKey, string expectedEndpoint)
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var result = config.GetGrpcEndpoint(serviceKey);

            // Assert
            result.Should().Be(expectedEndpoint);
        }

        /// <summary>
        /// Verifies that GetGrpcEndpoint throws ArgumentException for an unknown service key.
        /// </summary>
        [Fact]
        public void GetGrpcEndpoint_ThrowsArgumentException_ForUnknownServiceKey()
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var act = () => config.GetGrpcEndpoint("UnknownService");

            // Assert
            act.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Verifies that GetGrpcEndpoint throws ArgumentException for the Reporting service key,
        /// because the Reporting service does NOT have a dedicated gRPC endpoint per the architecture spec.
        /// </summary>
        [Fact]
        public void GetGrpcEndpoint_ThrowsArgumentException_ForReportingService()
        {
            // Arrange
            var config = new RouteConfiguration();

            // Act
            var act = () => config.GetGrpcEndpoint("Reporting");

            // Assert
            act.Should().Throw<ArgumentException>();
        }

        #endregion

        #region Configuration Binding Tests

        /// <summary>
        /// Validates that the RouteConfiguration POCO is properly bindable from IConfiguration
        /// using the ASP.NET Core options pattern. This ensures that appsettings.json values
        /// correctly populate the RouteConfiguration when bound via
        /// builder.Services.Configure&lt;RouteConfiguration&gt;(builder.Configuration.GetSection("ServiceRoutes")).
        /// </summary>
        [Fact]
        public void ConfigurationBinding_PopulatesRouteMappingsFromIConfiguration()
        {
            // Arrange — build in-memory configuration matching appsettings.json structure
            var inMemorySettings = new Dictionary<string, string?>
            {
                { "ServiceRoutes:CoreServiceUrl", "http://localhost:5001" },
                { "ServiceRoutes:CrmServiceUrl", "http://localhost:5002" },
                { "ServiceRoutes:RouteMappings:/api/v3/en_US/eql", "CoreServiceUrl" },
                { "ServiceRoutes:RouteMappings:/api/v3/en_US/crm", "CrmServiceUrl" }
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            // Act — bind configuration section to POCO
            var routeConfig = new RouteConfiguration();
            configuration.GetSection("ServiceRoutes").Bind(routeConfig);

            // Assert — service URLs should be populated from configuration
            routeConfig.CoreServiceUrl.Should().Be("http://localhost:5001");
            routeConfig.CrmServiceUrl.Should().Be("http://localhost:5002");

            // Assert — route mappings should be populated from configuration
            routeConfig.RouteMappings.Should().ContainKey("/api/v3/en_US/eql");
            routeConfig.RouteMappings["/api/v3/en_US/eql"].Should().Be("CoreServiceUrl");
            routeConfig.RouteMappings.Should().ContainKey("/api/v3/en_US/crm");
            routeConfig.RouteMappings["/api/v3/en_US/crm"].Should().Be("CrmServiceUrl");
        }

        #endregion

        #region Backward Compatibility Coverage Tests

        /// <summary>
        /// Comprehensive backward compatibility validation ensuring ALL legacy route patterns
        /// from the monolith source controllers are covered by the route configuration.
        /// This is the final safety-net test per AAP Section 0.8.1: every existing REST API v3
        /// endpoint must remain accessible through the API Gateway.
        /// </summary>
        [Fact]
        public void BackwardCompatibility_AllLegacyRoutePatterns_AreCovered()
        {
            // Arrange — full route configuration with all known prefixes
            var config = CreateDefaultRouteConfiguration();

            // Define all legacy route patterns from monolith source controllers
            var legacyRoutes = new[]
            {
                "/api/v3/en_US/eql",                                // WebApiController line 63
                "/api/v3/en_US/eql-ds",                             // WebApiController line 97 (matches /api/v3/en_US/eql prefix)
                "/api/v3/en_US/eql-ds-select2",                     // WebApiController line 190 (matches /api/v3/en_US/eql prefix)
                "/api/v3.0/user/preferences/toggle-sidebar-size",   // WebApiController line 340
                "/api/v3.0/datasource/code-compile",                // WebApiController line 494
                "/api/v3.0/datasource/test",                        // WebApiController line 511
                "/api/v3.0/page/some-id/node/create",               // WebApiController line 603
                "/api/v3.0/pc/component/view/display",              // WebApiController line 823
                "/api/v3.0/p/core/styles.css",                      // WebApiController line 1039
                "/api/v3.0/p/core/related-field-multiselect",       // WebApiController line 1138
                "/api/v3/en_US/meta/entity/list",                   // WebApiController line 1437
                "/api/v3/en_US/meta/relation/list",                 // WebApiController line 2009
                "/api/v3/en_US/record/relation",                    // WebApiController line 2106
                "/api/v3/en_US/record/account/some-id",             // WebApiController line 2504
                "/api/v3/en_US/quick-search",                       // WebApiController line 3020
                "/fs/some-file.pdf",                                // WebApiController line 3253
                "/fs/upload/",                                      // WebApiController line 3327
                "/api/v3/en_US/plugin/list",                        // WebApiController line 3403
                "/api/v3/en_US/jobs",                               // WebApiController line 3420
                "/api/v3/en_US/scheduleplan/list",                  // WebApiController line 3705
                "/api/v3/en_US/system-log",                         // WebApiController line 3817
                "/api/v3/en_US/user_file",                          // WebApiController line 3886
                "/api/v3/en_US/snippets",                           // WebApiController line 4234
                "/api/v3/en_US/auth/jwt/token",                     // WebApiController line 4274
                "/api/v3/en_US/auth/jwt/token/refresh",             // WebApiController line 4293
                "/api/v3.0/p/sdk/datasource/list",                  // AdminController line 39
                "/api/v3.0/p/sdk/sitemap/area",                     // AdminController line 54
                "/api/v3.0/p/project/pc-post-list/create",          // ProjectController line 56
                "/api/v3.0/p/project/timelog/start",                // ProjectController line 295
                "/api/v3.0/p/project/task/status",                  // ProjectController line 362
            };

            // Act & Assert — every legacy route must be resolvable through the Gateway
            foreach (var route in legacyRoutes)
            {
                config.FindMatchingRoute(route).Should().NotBeNull(
                    $"Route '{route}' should be matched by the gateway route configuration");
            }
        }

        #endregion
    }
}
