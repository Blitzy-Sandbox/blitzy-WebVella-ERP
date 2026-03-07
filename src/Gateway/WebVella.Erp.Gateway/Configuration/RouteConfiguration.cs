using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WebVella.Erp.Gateway.Configuration
{
    /// <summary>
    /// Central configuration POCO that binds to the Gateway's appsettings.json configuration sections
    /// (ServiceRoutes, GrpcEndpoints, RouteMappings). This class is the single source of truth consumed
    /// by <see cref="WebVella.Erp.Gateway.Middleware.RequestRoutingMiddleware"/> to determine which backend
    /// microservice handles each incoming HTTP request.
    ///
    /// Implements the Strangler Fig pattern by allowing gradual route migration from monolith to microservices.
    /// Each URL pattern prefix in <see cref="RouteMappings"/> maps to a service identifier key, which resolves
    /// to an actual backend service URL via <see cref="GetServiceUrl(string)"/>.
    ///
    /// Configuration binding:
    ///   builder.Services.Configure&lt;RouteConfiguration&gt;(builder.Configuration.GetSection("ServiceRoutes"));
    ///
    /// All default URLs use Docker Compose service names from docker-compose.yml with standard container ports
    /// (8080 for HTTP, 5001 for gRPC/HTTP2).
    /// </summary>
    public class RouteConfiguration
    {
        #region Backend Service URL Properties

        /// <summary>
        /// Base URL for the Core Platform service. Handles entity metadata CRUD, record CRUD, security/auth,
        /// datasource management, EQL queries, search, file storage, page builder, and core UI resources.
        /// Routes: /api/v3/{locale}/eql, /api/v3/{locale}/eql-ds, /api/v3/{locale}/eql-ds-select2,
        ///         /api/v3.0/datasource/*, /api/v3.0/p/core/*, /api/v3.0/pc/*, /api/v3.0/page/*,
        ///         /api/v3/{locale}/auth/jwt/*, /api/v3.0/user/preferences/*, /fs/*, entity/record/relation CRUD
        /// Docker Compose service: core-service
        /// </summary>
        public string CoreServiceUrl { get; set; } = "http://localhost:8084";

        /// <summary>
        /// Base URL for the CRM service. Handles account, contact, case, address, and salutation entities.
        /// Routes: /api/v3/{locale}/crm/*
        /// Docker Compose service: crm-service
        /// </summary>
        public string CrmServiceUrl { get; set; } = "http://localhost:8082";

        /// <summary>
        /// Base URL for the Project/Task service. Handles task CRUD, timelogs, comments, feeds,
        /// project user endpoints, and project-specific UI resources.
        /// Routes: /api/v3.0/p/project/* (from ProjectController.cs)
        /// Docker Compose service: project-service
        /// </summary>
        public string ProjectServiceUrl { get; set; } = "http://localhost:8092";

        /// <summary>
        /// Base URL for the Mail/Notification service. Handles email entities, SMTP services,
        /// mail queue processing, and notification delivery.
        /// Routes: /api/v3/{locale}/mail/*
        /// Docker Compose service: mail-service
        /// </summary>
        public string MailServiceUrl { get; set; } = "http://localhost:8090";

        /// <summary>
        /// Base URL for the Reporting service. Handles report aggregation endpoints
        /// and cross-service data projections for dashboards and analytics.
        /// Routes: /api/v3/{locale}/report/*
        /// Docker Compose service: reporting-service
        /// </summary>
        public string ReportingServiceUrl { get; set; } = "http://localhost:8088";

        /// <summary>
        /// Base URL for the Admin/SDK service. Handles SDK admin console endpoints including
        /// datasource list, sitemap CRUD, code generation, and system log management.
        /// Routes: /api/v3.0/p/sdk/* (from AdminController.cs)
        /// Docker Compose service: admin-service
        /// </summary>
        public string AdminServiceUrl { get; set; } = "http://localhost:8086";

        #endregion

        #region gRPC Endpoint Properties

        /// <summary>
        /// gRPC endpoint for the Core Platform service. Used by the Gateway for inter-service
        /// communication when performing API composition (cross-service EQL queries, entity resolution).
        /// Port 5001 is the standard gRPC HTTP/2 port by convention.
        /// </summary>
        public string CoreServiceGrpc { get; set; } = "http://localhost:8085";

        /// <summary>
        /// gRPC endpoint for the CRM service. Used for cross-service entity resolution
        /// (e.g., resolving account/contact references from Project or Mail services).
        /// </summary>
        public string CrmServiceGrpc { get; set; } = "http://localhost:8083";

        /// <summary>
        /// gRPC endpoint for the Project/Task service. Used for cross-service entity resolution
        /// (e.g., resolving task references from CRM case-task links).
        /// </summary>
        public string ProjectServiceGrpc { get; set; } = "http://localhost:8093";

        /// <summary>
        /// gRPC endpoint for the Mail/Notification service. Used for cross-service communication
        /// (e.g., triggering email notifications from other services via the Gateway).
        /// </summary>
        public string MailServiceGrpc { get; set; } = "http://localhost:8091";

        #endregion

        #region Route Mapping Configuration

        /// <summary>
        /// Dictionary mapping URL path prefixes to backend service identifier keys.
        /// Each key is a URL pattern prefix (e.g., "/api/v3.0/p/project") and each value
        /// is a service property name (e.g., "ProjectServiceUrl") that can be resolved
        /// via <see cref="GetServiceUrl(string)"/>.
        ///
        /// This dictionary is populated from the "RouteMappings" section in appsettings.json.
        /// The middleware performs longest-prefix-first matching to ensure more specific routes
        /// (e.g., "/api/v3.0/p/sdk/datasource") take priority over shorter prefixes
        /// (e.g., "/api/v3.0/p/sdk").
        ///
        /// Route patterns derived from monolith controllers:
        ///   WebApiController.cs  → Core service routes (EQL, datasource, page, auth, file, etc.)
        ///   AdminController.cs   → Admin/SDK service routes (datasource list, sitemap CRUD)
        ///   ProjectController.cs → Project service routes (tasks, timelogs, comments, feeds)
        /// </summary>
        public Dictionary<string, string> RouteMappings { get; set; } = new Dictionary<string, string>();

        #endregion

        #region Helper Methods

        /// <summary>
        /// Resolves a backend service URL from a service identifier key string.
        /// The key must match one of the service URL property names defined in this class
        /// (e.g., "CoreServiceUrl", "CrmServiceUrl", "AdminServiceUrl").
        ///
        /// Used by <see cref="WebVella.Erp.Gateway.Middleware.RequestRoutingMiddleware"/> after
        /// <see cref="FindMatchingRoute(string)"/> identifies which service should handle a request.
        /// </summary>
        /// <param name="serviceKey">
        /// The service identifier key matching a property name
        /// (e.g., nameof(CoreServiceUrl), nameof(AdminServiceUrl)).
        /// </param>
        /// <returns>The backend service base URL for the specified service key.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="serviceKey"/> does not match any known service property name.
        /// </exception>
        public string GetServiceUrl(string serviceKey)
        {
            return serviceKey switch
            {
                nameof(CoreServiceUrl) => CoreServiceUrl,
                nameof(CrmServiceUrl) => CrmServiceUrl,
                nameof(ProjectServiceUrl) => ProjectServiceUrl,
                nameof(MailServiceUrl) => MailServiceUrl,
                nameof(ReportingServiceUrl) => ReportingServiceUrl,
                nameof(AdminServiceUrl) => AdminServiceUrl,
                _ => throw new ArgumentException(
                    $"Unknown service key: '{serviceKey}'. Valid keys are: " +
                    $"{nameof(CoreServiceUrl)}, {nameof(CrmServiceUrl)}, {nameof(ProjectServiceUrl)}, " +
                    $"{nameof(MailServiceUrl)}, {nameof(ReportingServiceUrl)}, {nameof(AdminServiceUrl)}.",
                    nameof(serviceKey))
            };
        }

        /// <summary>
        /// Finds the backend service that should handle a given request path by performing
        /// longest-prefix matching against the <see cref="RouteMappings"/> dictionary.
        ///
        /// Route mappings are sorted by key length descending before matching to ensure that
        /// more specific routes take priority over shorter, more general prefixes.
        /// For example, "/api/v3.0/p/sdk/datasource" will match before "/api/v3.0/p/sdk".
        ///
        /// The matching is case-insensitive using <see cref="StringComparison.OrdinalIgnoreCase"/>.
        /// </summary>
        /// <param name="requestPath">
        /// The incoming HTTP request path (e.g., "/api/v3.0/p/project/pc-post-list/create").
        /// </param>
        /// <returns>
        /// A tuple containing the service key and resolved service URL if a matching route is found;
        /// null if no route mapping matches the request path.
        /// </returns>
        /// <summary>
        /// Cached compiled regex patterns for route mapping keys containing template parameters.
        /// Lazily built on first call to FindMatchingRoute to avoid repeated regex compilation.
        /// </summary>
        private List<(Regex pattern, string serviceKey, int keyLength)> _compiledRoutes;

        /// <summary>
        /// Builds and caches compiled regex patterns from the RouteMappings dictionary.
        /// Template parameters like <c>{locale}</c> are replaced with wildcard capture groups
        /// (e.g., <c>[^/]+</c>) to match concrete path segments like <c>en_US</c>.
        /// Static route keys (without template parameters) are matched as literal prefixes.
        /// Results are sorted by key length descending for longest-prefix-first matching.
        /// </summary>
        private List<(Regex pattern, string serviceKey, int keyLength)> GetCompiledRoutes()
        {
            if (_compiledRoutes != null)
                return _compiledRoutes;

            var routes = new List<(Regex pattern, string serviceKey, int keyLength)>();
            if (RouteMappings != null)
            {
                foreach (var kvp in RouteMappings)
                {
                    // Replace {parameterName} tokens with a wildcard pattern matching any
                    // non-slash path segment (e.g., {locale} matches "en_US", "bg_BG", etc.)
                    // Note: Regex.Escape escapes '{' to '\{' but leaves '}' unescaped,
                    // so the replacement pattern matches '\{...}' (not '\{...\}').
                    var regexPattern = "^" + Regex.Replace(
                        Regex.Escape(kvp.Key),
                        @"\\{[^}]+}",
                        "[^/]+");
                    routes.Add((
                        new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled),
                        kvp.Value,
                        kvp.Key.Length));
                }
            }
            // Sort by key length descending for longest-prefix-first matching
            _compiledRoutes = routes.OrderByDescending(r => r.keyLength).ToList();
            return _compiledRoutes;
        }

        public (string serviceKey, string serviceUrl)? FindMatchingRoute(string requestPath)
        {
            if (string.IsNullOrEmpty(requestPath) || RouteMappings == null || RouteMappings.Count == 0)
                return null;

            // Use compiled regex patterns that handle {locale} and other template parameters.
            // Sort by key length descending ensures more specific routes take priority.
            foreach (var (pattern, serviceKey, _) in GetCompiledRoutes())
            {
                if (pattern.IsMatch(requestPath))
                {
                    var serviceUrl = GetServiceUrl(serviceKey);
                    return (serviceKey, serviceUrl);
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a gRPC endpoint URL for a given service identifier key.
        /// Used when the Gateway needs to make gRPC calls for API composition
        /// (e.g., cross-service EQL queries, entity resolution across service boundaries).
        ///
        /// Accepts both the full property name format (e.g., "CoreServiceUrl") and the
        /// short service name format (e.g., "Core") for convenience.
        /// </summary>
        /// <param name="serviceKey">
        /// The service identifier key. Accepts property name format (e.g., nameof(CoreServiceUrl))
        /// or short name format (e.g., "Core", "Crm", "Project", "Mail").
        /// </param>
        /// <returns>The gRPC endpoint URL for the specified service.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="serviceKey"/> does not match any known service with a gRPC endpoint.
        /// Note: ReportingServiceUrl and AdminServiceUrl do not have dedicated gRPC endpoints.
        /// </exception>
        public string GetGrpcEndpoint(string serviceKey)
        {
            return serviceKey switch
            {
                nameof(CoreServiceUrl) or "Core" => CoreServiceGrpc,
                nameof(CrmServiceUrl) or "Crm" => CrmServiceGrpc,
                nameof(ProjectServiceUrl) or "Project" => ProjectServiceGrpc,
                nameof(MailServiceUrl) or "Mail" => MailServiceGrpc,
                _ => throw new ArgumentException(
                    $"No gRPC endpoint configured for service key: '{serviceKey}'. " +
                    $"Valid keys are: {nameof(CoreServiceUrl)}/Core, {nameof(CrmServiceUrl)}/Crm, " +
                    $"{nameof(ProjectServiceUrl)}/Project, {nameof(MailServiceUrl)}/Mail.",
                    nameof(serviceKey))
            };
        }

        #endregion
    }
}
