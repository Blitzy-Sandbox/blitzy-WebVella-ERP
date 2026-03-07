// =========================================================================
// WebVella ERP Gateway — Service Abstractions and Registry
// =========================================================================
// Provides the DI-registered service infrastructure for the Gateway/BFF layer.
// These services are consumed by RequestRoutingMiddleware, Razor Pages, and
// controllers to proxy requests to backend microservices.
//
// The IServiceProxyRegistry acts as a centralized lookup for named HttpClient
// instances corresponding to each backend microservice (Core, CRM, Project,
// Mail, Reporting, Admin). This decouples Razor Pages and middleware from
// direct knowledge of service URLs.
//
// Referenced by: Pages/_ViewImports.cshtml (@using WebVella.Erp.Gateway.Services)
// =========================================================================

using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace WebVella.Erp.Gateway.Services
{
    /// <summary>
    /// Registry interface for resolving named <see cref="HttpClient"/> instances
    /// corresponding to backend microservices. Used by Gateway middleware and
    /// Razor Pages to proxy requests without hardcoding service URLs.
    /// </summary>
    public interface IServiceProxyRegistry
    {
        /// <summary>
        /// Retrieves an <see cref="HttpClient"/> configured for the specified
        /// backend service. The client is obtained from <see cref="IHttpClientFactory"/>
        /// using the service name as the named client key.
        /// </summary>
        /// <param name="serviceName">
        /// The logical service name matching a named HttpClient registration
        /// (e.g., "CoreService", "CrmService", "ProjectService", "MailService",
        /// "ReportingService", "AdminService").
        /// </param>
        /// <returns>
        /// A configured <see cref="HttpClient"/> for the requested service,
        /// or the default unnamed client if the service name is not recognized.
        /// </returns>
        HttpClient GetServiceClient(string serviceName);

        /// <summary>
        /// Checks whether a named service client is registered.
        /// </summary>
        /// <param name="serviceName">The logical service name to check.</param>
        /// <returns>True if the service has a named HttpClient registration.</returns>
        bool IsServiceRegistered(string serviceName);
    }

    /// <summary>
    /// Default implementation of <see cref="IServiceProxyRegistry"/> backed by
    /// <see cref="IHttpClientFactory"/>. Named clients are registered in
    /// Program.cs for each backend microservice with pre-configured base
    /// addresses and timeouts.
    ///
    /// Thread-safe: <see cref="IHttpClientFactory.CreateClient(string)"/> is
    /// thread-safe and manages HttpMessageHandler lifetimes internally.
    /// </summary>
    public class ServiceProxyRegistry : IServiceProxyRegistry
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ServiceProxyRegistry> _logger;

        /// <summary>
        /// Well-known service names that have named HttpClient registrations.
        /// Must match the names used in Program.cs AddHttpClient calls.
        /// </summary>
        private static readonly HashSet<string> RegisteredServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "CoreService",
            "CrmService",
            "ProjectService",
            "MailService",
            "ReportingService",
            "AdminService"
        };

        /// <summary>
        /// Initializes a new instance of <see cref="ServiceProxyRegistry"/>.
        /// </summary>
        /// <param name="httpClientFactory">
        /// The factory for creating named <see cref="HttpClient"/> instances.
        /// </param>
        /// <param name="logger">Logger for diagnostic output.</param>
        public ServiceProxyRegistry(
            IHttpClientFactory httpClientFactory,
            ILogger<ServiceProxyRegistry> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public HttpClient GetServiceClient(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                _logger.LogWarning("Empty service name requested; returning default HttpClient");
                return _httpClientFactory.CreateClient();
            }

            if (RegisteredServices.Contains(serviceName))
            {
                _logger.LogDebug("Creating named HttpClient for service: {ServiceName}", serviceName);
                return _httpClientFactory.CreateClient(serviceName);
            }

            _logger.LogWarning(
                "Unknown service name '{ServiceName}' requested; returning default HttpClient. " +
                "Known services: {KnownServices}",
                serviceName,
                string.Join(", ", RegisteredServices));

            return _httpClientFactory.CreateClient();
        }

        /// <inheritdoc />
        public bool IsServiceRegistered(string serviceName)
        {
            return !string.IsNullOrWhiteSpace(serviceName) && RegisteredServices.Contains(serviceName);
        }
    }
}
