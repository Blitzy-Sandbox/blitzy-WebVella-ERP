using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Crm.DataAccess;
using WebVellaErp.Crm.Models;
using WebVellaErp.Crm.Services;

namespace WebVellaErp.Crm.Functions
{
    // =========================================================================
    // AOT-compatible JSON serialization context for AccountHandler
    // =========================================================================

    /// <summary>
    /// Source-generated JSON serializer context for Native AOT compatibility.
    /// Registers all types that flow through JsonSerializer.Serialize/Deserialize
    /// in AccountHandler, enabling .NET 9 Native AOT compilation with sub-1-second
    /// cold starts (AAP §0.8.2). Eliminates IL2026/IL3050 AOT trimming warnings.
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Account))]
    [JsonSerializable(typeof(List<Account>))]
    [JsonSerializable(typeof(AccountListEnvelope))]
    [JsonSerializable(typeof(AccountDomainEvent))]
    [JsonSerializable(typeof(ErrorResponse))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(string))]
    internal partial class AccountHandlerJsonContext : JsonSerializerContext { }

    // =========================================================================
    // Domain event payload DTO for SNS publishing
    // =========================================================================

    /// <summary>
    /// Domain event payload published to SNS for account lifecycle changes.
    /// Follows {domain}.{entity}.{action} naming convention per AAP §0.8.5.
    /// Replaces the synchronous AccountHook post-CRUD calls from the monolith.
    /// </summary>
    internal sealed class AccountDomainEvent
    {
        [JsonPropertyName("eventType")]
        public string EventType { get; set; } = string.Empty;

        [JsonPropertyName("entityName")]
        public string EntityName { get; set; } = string.Empty;

        [JsonPropertyName("recordId")]
        public string RecordId { get; set; } = string.Empty;

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;
    }

    // =========================================================================
    // Response envelope DTO for account list endpoint
    // =========================================================================

    /// <summary>
    /// Paginated list response envelope for account list endpoint.
    /// Format: { "data": [...accounts], "meta": { "page": N, "pageSize": N, "total": N } }
    /// </summary>
    internal sealed class AccountListEnvelope
    {
        [JsonPropertyName("data")]
        public List<Account> Data { get; set; } = new();

        [JsonPropertyName("meta")]
        public PaginationMeta Meta { get; set; } = new();
    }

    // NOTE: PaginationMeta and ErrorResponse are defined in ContactHandler.cs
    // within the same namespace (WebVellaErp.Crm.Functions). We reuse them
    // here to avoid duplicate type definitions within the assembly.

    // =========================================================================
    // AccountHandler — Lambda entry point for CRM Account CRUD
    // =========================================================================

    /// <summary>
    /// Primary AWS Lambda entry point for all CRM Account HTTP API Gateway v2 requests.
    /// 
    /// Replaces:
    /// - WebVella.Erp.Web/Controllers/WebApiController.cs (account-related MVC endpoints)
    /// - WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs (synchronous post-CRUD hooks)
    /// - WebVella.Erp/Api/RecordManager.cs (record CRUD with hook orchestration)
    /// - WebVella.Erp/Api/SecurityContext.cs (AsyncLocal user scoping)
    /// 
    /// This is NOT an MVC controller. It is a Lambda handler receiving API Gateway v2
    /// proxy events. Authentication is handled by API Gateway JWT authorizer; this
    /// handler extracts JWT claims from the request context for authorization decisions.
    /// 
    /// API Routes:
    ///   POST   /v1/accounts         → CreateAccountAsync
    ///   GET    /v1/accounts         → ListAccountsAsync
    ///   GET    /v1/accounts/{id}    → GetAccountAsync
    ///   PUT    /v1/accounts/{id}    → UpdateAccountAsync
    ///   DELETE /v1/accounts/{id}    → DeleteAccountAsync
    /// </summary>
    public class AccountHandler
    {
        #region Constants

        /// <summary>
        /// Entity name for the account entity. Matches source
        /// [HookAttachment("account", int.MinValue)] from AccountHook.cs line 9.
        /// </summary>
        public const string EntityName = "account";

        /// <summary>
        /// Account entity GUID — derived from <see cref="Account.EntityId"/>.
        /// Originally from NextPlugin.20190204.cs line 43.
        /// </summary>
        public static readonly Guid AccountEntityId = Account.EntityId;

        /// <summary>
        /// Default salutation ID — derived from <see cref="Account.DefaultSalutationId"/>.
        /// Originally from NextPlugin.20190206.cs line 131.
        /// </summary>
        public static readonly Guid DefaultSalutationId = Account.DefaultSalutationId;

        /// <summary>
        /// SNS topic ARN configuration key for CRM domain events.
        /// Retrieved from IConfiguration (backed by SSM Parameter Store per AAP §0.8.6).
        /// </summary>
        private const string SnsTopicArnKey = "SNS:CrmEventTopicArn";

        /// <summary>
        /// Environment variable fallback for SNS topic ARN.
        /// </summary>
        private const string SnsTopicArnEnvVar = "CRM_EVENTS_TOPIC_ARN";

        /// <summary>
        /// SNS event type constants following {domain}.{entity}.{action} naming convention
        /// per AAP §0.8.5.
        /// </summary>
        private const string EventAccountCreated = "crm.account.created";
        private const string EventAccountUpdated = "crm.account.updated";
        private const string EventAccountDeleted = "crm.account.deleted";

        /// <summary>
        /// Maximum allowed page size for list queries.
        /// </summary>
        private const int MaxPageSize = 100;

        /// <summary>
        /// Default page size when not specified by the caller.
        /// </summary>
        private const int DefaultPageSize = 20;

        /// <summary>
        /// Standard response headers for all API Gateway v2 responses.
        /// Includes Content-Type and CORS headers per AAP requirements.
        /// </summary>
        private static readonly Dictionary<string, string> StandardResponseHeaders = new()
        {
            { "Content-Type", "application/json" },
            { "Access-Control-Allow-Origin", "*" },
            { "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,OPTIONS" },
            { "Access-Control-Allow-Headers", "Content-Type,Authorization,x-correlation-id,x-idempotency-key" }
        };

        #endregion

        #region Fields

        private readonly ICrmRepository _crmRepository;
        private readonly ISearchService _searchService;
        private readonly IAmazonSimpleNotificationService _snsClient;
        private readonly ILogger<AccountHandler> _logger;
        private readonly IConfiguration _configuration;
        private readonly string? _snsTopicArn;

        /// <summary>
        /// Lazily-initialised ContactHandler for delegating contact-specific routes.
        /// All CRM routes arrive via a single API Gateway {proxy+} route targeting
        /// this AccountHandler Lambda. Contact-path requests (/contacts/*) are
        /// forwarded to the ContactHandler, which shares the same DynamoDB table
        /// and DI setup but uses its own model/validation logic.
        /// </summary>
        private ContactHandler? _contactHandler;

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for AWS Lambda runtime invocation.
        /// Builds the DI ServiceCollection, registers all dependencies (AWS SDK clients,
        /// application services, configuration), and resolves required services.
        /// AWS SDK clients are configured with AWS_ENDPOINT_URL for LocalStack compatibility
        /// per AAP §0.7.6.
        /// </summary>
        public AccountHandler()
        {
            var services = new ServiceCollection();

            // Configure structured JSON logging per AAP §0.8.5
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            });

            // Build IConfiguration from environment variables and optional SSM
            var configBuilder = new ConfigurationBuilder()
                .AddEnvironmentVariables();
            var configuration = configBuilder.Build();
            services.AddSingleton<IConfiguration>(configuration);

            // Read AWS_ENDPOINT_URL for LocalStack dual-target compatibility (AAP §0.7.6)
            var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");

            // Register AWS SDK clients with endpoint URL override for LocalStack
            if (!string.IsNullOrEmpty(endpointUrl))
            {
                services.AddSingleton<IAmazonDynamoDB>(_ =>
                    new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = endpointUrl }));
                services.AddSingleton<IAmazonSimpleNotificationService>(_ =>
                    new AmazonSimpleNotificationServiceClient(
                        new AmazonSimpleNotificationServiceConfig { ServiceURL = endpointUrl }));
            }
            else
            {
                // Production AWS: use default credential and region resolution chain
                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonSimpleNotificationService, AmazonSimpleNotificationServiceClient>();
            }

            // Register application services with transient lifetime
            services.AddTransient<ICrmRepository, CrmRepository>();
            services.AddTransient<ISearchService, SearchService>();

            // Build DI container and resolve all handler dependencies
            var serviceProvider = services.BuildServiceProvider();
            _crmRepository = serviceProvider.GetRequiredService<ICrmRepository>();
            _searchService = serviceProvider.GetRequiredService<ISearchService>();
            _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<AccountHandler>();
            _configuration = serviceProvider.GetRequiredService<IConfiguration>();

            // Resolve SNS topic ARN: prefer IConfiguration, fall back to environment variable
            _snsTopicArn = _configuration[SnsTopicArnKey]
                ?? Environment.GetEnvironmentVariable(SnsTopicArnEnvVar);
        }

        /// <summary>
        /// Secondary constructor accepting an IServiceProvider for unit testing.
        /// Allows test code to inject mock or stubbed services without Lambda runtime dependencies.
        /// </summary>
        /// <param name="serviceProvider">Pre-configured service provider with all required dependencies.</param>
        public AccountHandler(IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);

            _crmRepository = serviceProvider.GetRequiredService<ICrmRepository>();
            _searchService = serviceProvider.GetRequiredService<ISearchService>();
            _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<AccountHandler>();
            _configuration = serviceProvider.GetRequiredService<IConfiguration>();

            _snsTopicArn = _configuration[SnsTopicArnKey]
                ?? Environment.GetEnvironmentVariable(SnsTopicArnEnvVar);
        }

        #endregion

        #region Lambda Entry Point

        /// <summary>
        /// Primary Lambda handler entry point. Routes HTTP API Gateway v2 proxy events
        /// to the appropriate CRUD method based on HTTP method and path.
        /// Extracts correlation-ID for distributed tracing per AAP §0.8.5.
        /// </summary>
        /// <param name="request">API Gateway v2 HTTP proxy request.</param>
        /// <param name="context">Lambda execution context with function metadata.</param>
        /// <returns>API Gateway v2 HTTP proxy response.</returns>
        public async Task<APIGatewayHttpApiV2ProxyResponse> HandleAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            ILambdaContext context)
        {
            var correlationId = GetCorrelationId(request);
            var rawPath = request.RequestContext?.Http?.Path ?? request.RawPath ?? string.Empty;

            _logger.LogInformation(
                "AccountHandler invoked. Method: {Method}, Path: {Path}, CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                request.RequestContext?.Http?.Method ?? "UNKNOWN",
                rawPath,
                correlationId,
                context.AwsRequestId);

            // ── Contact-path delegation ────────────────────────────────────
            // All CRM routes arrive at this Lambda via a single API Gateway
            // {proxy+} catch-all route.  Contact-specific paths (/contacts/*)
            // are forwarded to the ContactHandler which owns its own model,
            // validation, and domain-event logic.
            if (rawPath.Contains("/contacts", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "AccountHandler: delegating to ContactHandler for path {Path}. CorrelationId: {CorrelationId}",
                    rawPath, correlationId);
                _contactHandler ??= new ContactHandler();
                return await _contactHandler.HandleAsync(request, context).ConfigureAwait(false);
            }

            try
            {
                var httpMethod = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? string.Empty;
                var accountId = ExtractPathParameter(request, "id");

                return httpMethod switch
                {
                    "POST" => await CreateAccountAsync(request, correlationId).ConfigureAwait(false),
                    "GET" when !string.IsNullOrEmpty(accountId) =>
                        await GetAccountAsync(accountId, correlationId).ConfigureAwait(false),
                    "GET" => await ListAccountsAsync(request, correlationId).ConfigureAwait(false),
                    "PUT" when !string.IsNullOrEmpty(accountId) =>
                        await UpdateAccountAsync(accountId, request, correlationId).ConfigureAwait(false),
                    "DELETE" when !string.IsNullOrEmpty(accountId) =>
                        await DeleteAccountAsync(accountId, correlationId).ConfigureAwait(false),
                    _ => BuildResponse(
                        (int)HttpStatusCode.MethodNotAllowed,
                        new ErrorResponse { Success = false, Message = $"Method {httpMethod} is not allowed." },
                        correlationId)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AccountHandler: Unhandled exception. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "An internal error occurred." },
                    correlationId);
            }
        }

        #endregion

        #region CRUD Operations

        /// <summary>
        /// Creates a new account record.
        ///
        /// Replaces: POST endpoint in WebApiController.cs + AccountHook.OnPostCreateRecord
        ///
        /// Pre-hook validation (inline, blocking — replaces absent IErpPreCreateRecordHook):
        /// - Validates required fields: Name, Type, LastName, FirstName
        /// - Validates Type is "1" (Company) or "2" (Person)
        /// - Sets defaults: Id, CreatedOn, SalutationId, XSearch
        ///
        /// Post-hook behavior (non-blocking — replaces AccountHook.cs lines 12-15):
        /// - Regenerates x_search field via ISearchService
        /// - Publishes crm.account.created SNS domain event
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> CreateAccountAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId)
        {
            _logger.LogInformation(
                "CreateAccountAsync invoked. CorrelationId: {CorrelationId}", correlationId);

            // Extract idempotency key per AAP §0.8.5
            var idempotencyKey = ExtractHeader(request, "x-idempotency-key");
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                _logger.LogInformation(
                    "CreateAccountAsync: IdempotencyKey={IdempotencyKey}, CorrelationId: {CorrelationId}",
                    idempotencyKey, correlationId);
            }

            // Validate request body is present
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                _logger.LogWarning(
                    "CreateAccountAsync: Empty request body. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body is required." },
                    correlationId);
            }

            // Deserialize using System.Text.Json (NOT Newtonsoft.Json) for Native AOT
            Account? account;
            try
            {
                account = JsonSerializer.Deserialize(
                    request.Body, AccountHandlerJsonContext.Default.Account);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "CreateAccountAsync: Malformed JSON. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid JSON in request body." },
                    correlationId);
            }

            if (account == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body could not be parsed." },
                    correlationId);
            }

            // ---- Pre-hook validation (inline, blocking) ----
            // Replaces IErpPreCreateRecordHook (AAP §0.7.2)
            var validationErrors = ValidateAccountFields(account);
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "CreateAccountAsync: Validation failed — {Errors}. CorrelationId: {CorrelationId}",
                    string.Join("; ", validationErrors), correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse
                    {
                        Success = false,
                        Message = string.Join(" ", validationErrors)
                    },
                    correlationId);
            }

            // Set defaults for system-managed fields
            if (account.Id == Guid.Empty)
            {
                account.Id = Guid.NewGuid();
            }

            // UseCurrentTimeAsDefaultValue=true from NextPlugin.20190206.cs line 102
            if (account.CreatedOn == default)
            {
                account.CreatedOn = DateTime.UtcNow;
            }

            // Default salutation: 87c08ee1-... from NextPlugin.20190206.cs line 131
            if (account.SalutationId == Guid.Empty)
            {
                account.SalutationId = DefaultSalutationId;
            }

            // Ensure x_search starts with empty string if not set (default="" from 20190206)
            if (string.IsNullOrEmpty(account.XSearch))
            {
                account.XSearch = string.Empty;
            }

            // ---- DynamoDB persistence ----
            await _crmRepository.CreateAsync(EntityName, account).ConfigureAwait(false);

            _logger.LogInformation(
                "Account created: {AccountId}, CorrelationId: {CorrelationId}",
                account.Id, correlationId);

            // ---- Post-hook behavior (non-blocking) ----
            // Replaces AccountHook.OnPostCreateRecord (AccountHook.cs lines 12-15):
            // new SearchService().RegenSearchField(entityName, record, Configuration.AccountSearchIndexFields)
            try
            {
                await _searchService.RegenSearchFieldAsync(
                    EntityName,
                    account.Id,
                    SearchIndexConfiguration.AccountSearchIndexFields.ToList())
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Search regen failure is non-critical — log but do not fail the request
                _logger.LogWarning(ex,
                    "CreateAccountAsync: Search index regen failed for {AccountId}. CorrelationId: {CorrelationId}",
                    account.Id, correlationId);
            }

            // Publish SNS domain event: crm.account.created (AAP §0.7.2)
            await PublishDomainEventAsync(EventAccountCreated, account.Id, correlationId).ConfigureAwait(false);

            // Return 201 Created with Location header
            var response = BuildResponse((int)HttpStatusCode.Created, account, correlationId);
            response.Headers["Location"] = $"/v1/accounts/{account.Id}";
            return response;
        }

        /// <summary>
        /// Retrieves a single account by its unique ID.
        /// Replaces GET /v1/accounts/{id} endpoint from WebApiController.cs.
        /// No SNS events on read operations.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> GetAccountAsync(
            string accountIdStr,
            string correlationId)
        {
            _logger.LogInformation(
                "GetAccountAsync invoked. AccountId: {AccountId}, CorrelationId: {CorrelationId}",
                accountIdStr, correlationId);

            if (!Guid.TryParse(accountIdStr, out var accountId) || accountId == Guid.Empty)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid account ID format." },
                    correlationId);
            }

            var account = await _crmRepository.GetByIdAsync<Account>(EntityName, accountId)
                .ConfigureAwait(false);

            if (account == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.NotFound,
                    new ErrorResponse { Success = false, Message = $"Account {accountId} not found." },
                    correlationId);
            }

            return BuildResponse((int)HttpStatusCode.OK, account, correlationId);
        }

        /// <summary>
        /// Lists accounts with pagination, filtering, sorting, and search.
        ///
        /// Query parameters:
        ///   page (int, default 1) — pagination page number
        ///   pageSize (int, default 20, max 100) — page size
        ///   search (string?) — text search on x_search field
        ///   sortBy (string?, default "created_on") — sort field
        ///   sortDir (string?, default "desc") — sort direction (asc/desc)
        ///   type (string?) — filter by account type ("1"=Company, "2"=Person)
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ListAccountsAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId)
        {
            _logger.LogInformation(
                "ListAccountsAsync invoked. CorrelationId: {CorrelationId}", correlationId);

            // Parse query string parameters with safe defaults
            var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

            if (!queryParams.TryGetValue("page", out var pageStr) ||
                !int.TryParse(pageStr, out var page) || page < 1)
            {
                page = 1;
            }

            if (!queryParams.TryGetValue("pageSize", out var pageSizeStr) ||
                !int.TryParse(pageSizeStr, out var pageSize) || pageSize < 1)
            {
                pageSize = DefaultPageSize;
            }
            pageSize = Math.Min(pageSize, MaxPageSize);

            queryParams.TryGetValue("search", out var searchTerm);
            queryParams.TryGetValue("sortBy", out var sortBy);
            queryParams.TryGetValue("sortDir", out var sortDir);
            queryParams.TryGetValue("type", out var accountType);

            if (string.IsNullOrEmpty(sortBy)) sortBy = "created_on";
            if (string.IsNullOrEmpty(sortDir)) sortDir = "desc";

            var pagination = new PaginationOptions
            {
                Limit = pageSize,
                Skip = (page - 1) * pageSize
            };

            List<Account> accounts;
            long total;

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                // Search branch: uses SearchAsync which searches the x_search field
                accounts = await _crmRepository.SearchAsync<Account>(
                    EntityName, searchTerm, pagination).ConfigureAwait(false);

                // For search results we need a separate count — apply same search via count
                // SearchAsync does not return total count, so count all search-matching records
                total = accounts.Count < pageSize ? (page - 1) * pageSize + accounts.Count : (page - 1) * pageSize + accounts.Count + 1;
            }
            else
            {
                // Query branch: filter + sort + paginate via QueryAsync + CountAsync
                var filter = BuildAccountFilter(accountType);
                var sort = new SortOptions
                {
                    FieldName = sortBy,
                    Direction = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase)
                        ? SortDirection.Ascending : SortDirection.Descending
                };

                accounts = await _crmRepository.QueryAsync<Account>(
                    EntityName, filter, sort, pagination).ConfigureAwait(false);

                total = await _crmRepository.CountAsync(EntityName, filter).ConfigureAwait(false);
            }

            var envelope = new AccountListEnvelope
            {
                Data = accounts,
                Meta = new PaginationMeta
                {
                    Page = page,
                    PageSize = pageSize,
                    Total = total
                }
            };

            return BuildResponse((int)HttpStatusCode.OK, envelope, correlationId);
        }

        /// <summary>
        /// Updates an existing account record.
        ///
        /// Replaces: PUT endpoint in WebApiController.cs + AccountHook.OnPostUpdateRecord
        ///
        /// Pre-hook validation (inline, blocking):
        /// - Same required field validation as Create
        /// - Ensures path ID matches body ID if present
        ///
        /// Post-hook behavior (non-blocking — replaces AccountHook.cs lines 17-20):
        /// - Regenerates x_search field via ISearchService
        /// - Publishes crm.account.updated SNS domain event
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> UpdateAccountAsync(
            string accountIdStr,
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId)
        {
            _logger.LogInformation(
                "UpdateAccountAsync invoked. AccountId: {AccountId}, CorrelationId: {CorrelationId}",
                accountIdStr, correlationId);

            // Extract idempotency key per AAP §0.8.5
            var idempotencyKey = ExtractHeader(request, "x-idempotency-key");
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                _logger.LogInformation(
                    "UpdateAccountAsync: IdempotencyKey={IdempotencyKey}, CorrelationId: {CorrelationId}",
                    idempotencyKey, correlationId);
            }

            // Validate path parameter GUID
            if (!Guid.TryParse(accountIdStr, out var accountId) || accountId == Guid.Empty)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid account ID format." },
                    correlationId);
            }

            // Validate request body is present
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body is required." },
                    correlationId);
            }

            // Deserialize using System.Text.Json for Native AOT
            Account? account;
            try
            {
                account = JsonSerializer.Deserialize(
                    request.Body, AccountHandlerJsonContext.Default.Account);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "UpdateAccountAsync: Malformed JSON. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid JSON in request body." },
                    correlationId);
            }

            if (account == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body could not be parsed." },
                    correlationId);
            }

            // Ensure path ID matches body ID if body contains a non-empty ID
            if (account.Id != Guid.Empty && account.Id != accountId)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse
                    {
                        Success = false,
                        Message = "Account ID in path does not match ID in request body."
                    },
                    correlationId);
            }
            account.Id = accountId;

            // ---- Pre-hook validation (inline, blocking) ----
            var validationErrors = ValidateAccountFields(account);
            if (validationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "UpdateAccountAsync: Validation failed — {Errors}. CorrelationId: {CorrelationId}",
                    string.Join("; ", validationErrors), correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse
                    {
                        Success = false,
                        Message = string.Join(" ", validationErrors)
                    },
                    correlationId);
            }

            // Verify record exists — return 404 if not found
            var existing = await _crmRepository.GetByIdAsync<Account>(EntityName, accountId)
                .ConfigureAwait(false);
            if (existing == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.NotFound,
                    new ErrorResponse { Success = false, Message = $"Account {accountId} not found." },
                    correlationId);
            }

            // ---- DynamoDB persistence ----
            await _crmRepository.UpdateAsync(EntityName, accountId, account).ConfigureAwait(false);

            _logger.LogInformation(
                "Account updated: {AccountId}, CorrelationId: {CorrelationId}",
                accountId, correlationId);

            // ---- Post-hook behavior (non-blocking) ----
            // Replaces AccountHook.OnPostUpdateRecord (AccountHook.cs lines 17-20):
            // new SearchService().RegenSearchField(entityName, record, Configuration.AccountSearchIndexFields)
            try
            {
                await _searchService.RegenSearchFieldAsync(
                    EntityName,
                    account.Id,
                    SearchIndexConfiguration.AccountSearchIndexFields.ToList())
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "UpdateAccountAsync: Search index regen failed for {AccountId}. CorrelationId: {CorrelationId}",
                    account.Id, correlationId);
            }

            // Publish SNS domain event: crm.account.updated (AAP §0.7.2)
            await PublishDomainEventAsync(EventAccountUpdated, account.Id, correlationId).ConfigureAwait(false);

            return BuildResponse((int)HttpStatusCode.OK, account, correlationId);
        }

        /// <summary>
        /// Deletes an account record by its unique ID.
        ///
        /// NOTE: Source AccountHook.cs did NOT implement IErpPostDeleteRecordHook,
        /// but AAP §0.5.1 explicitly requires 'crm.account.deleted' event.
        ///
        /// Returns 204 No Content on success.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> DeleteAccountAsync(
            string accountIdStr,
            string correlationId)
        {
            _logger.LogInformation(
                "DeleteAccountAsync invoked. AccountId: {AccountId}, CorrelationId: {CorrelationId}",
                accountIdStr, correlationId);

            // Validate path parameter GUID
            if (!Guid.TryParse(accountIdStr, out var accountId) || accountId == Guid.Empty)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid account ID format." },
                    correlationId);
            }

            // Verify record exists — return 404 if not found
            var existing = await _crmRepository.GetByIdAsync<Account>(EntityName, accountId)
                .ConfigureAwait(false);
            if (existing == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.NotFound,
                    new ErrorResponse { Success = false, Message = $"Account {accountId} not found." },
                    correlationId);
            }

            // ---- DynamoDB persistence ----
            await _crmRepository.DeleteAsync(EntityName, accountId).ConfigureAwait(false);

            _logger.LogInformation(
                "Account deleted: {AccountId}, CorrelationId: {CorrelationId}",
                accountId, correlationId);

            // ---- Post-hook behavior via SNS ----
            // AAP §0.5.1 explicitly requires crm.account.deleted event
            await PublishDomainEventAsync(EventAccountDeleted, accountId, correlationId).ConfigureAwait(false);

            // 204 No Content — no body for DELETE responses
            var response = new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)HttpStatusCode.NoContent,
                Headers = new Dictionary<string, string>(StandardResponseHeaders)
            };
            AddCorrelationIdHeader(response, correlationId);
            return response;
        }

        #endregion

        #region Validation Helpers

        /// <summary>
        /// Validates required Account fields for create and update operations.
        /// Replaces inline pre-hook validation that would have been IErpPreCreateRecordHook
        /// and IErpPreUpdateRecordHook. Validates:
        /// - Name (Required=true from NextPlugin.20190203)
        /// - Type (Required=true, must be "1" or "2" from NextPlugin.20190204 lines 16-48)
        /// - LastName (Required=true from NextPlugin.20190204 line 304)
        /// - FirstName (Required=true from NextPlugin.20190204 line 334)
        /// </summary>
        /// <param name="account">Account model to validate.</param>
        /// <returns>List of validation error messages. Empty list means validation passed.</returns>
        private static List<string> ValidateAccountFields(Account account)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(account.Name))
            {
                errors.Add("name is required.");
            }

            if (string.IsNullOrWhiteSpace(account.Type))
            {
                errors.Add("type is required.");
            }
            else if (account.Type != AccountType.Company && account.Type != AccountType.Person)
            {
                errors.Add("type must be '1' (Company) or '2' (Person).");
            }

            if (string.IsNullOrWhiteSpace(account.LastName))
            {
                errors.Add("last_name is required.");
            }

            if (string.IsNullOrWhiteSpace(account.FirstName))
            {
                errors.Add("first_name is required.");
            }

            return errors;
        }

        /// <summary>
        /// Builds a composable QueryFilter for the account list endpoint.
        /// Supports filtering by account type (Company="1", Person="2").
        /// </summary>
        /// <param name="accountType">Optional account type filter value.</param>
        /// <returns>QueryFilter or null if no filters apply.</returns>
        private static QueryFilter? BuildAccountFilter(string? accountType)
        {
            if (string.IsNullOrWhiteSpace(accountType))
            {
                return null;
            }

            return new QueryFilter
            {
                FieldName = "type",
                Operator = FilterOperator.Equal,
                Value = accountType
            };
        }

        #endregion

        #region Event Publishing

        /// <summary>
        /// Publishes a domain event to SNS for account lifecycle changes.
        /// Follows {domain}.{entity}.{action} naming convention per AAP §0.8.5.
        ///
        /// Post-hook events MUST be non-blocking per AAP §0.7.2:
        /// "Post-hooks that notify other systems become SNS-published domain events."
        /// If SNS publish fails, the error is logged but does NOT fail the API response.
        ///
        /// Replaces the synchronous AccountHook post-CRUD calls from the monolith.
        /// </summary>
        /// <param name="eventType">Event type string (e.g., "crm.account.created").</param>
        /// <param name="recordId">Account record GUID.</param>
        /// <param name="correlationId">Distributed tracing correlation ID.</param>
        private async Task PublishDomainEventAsync(string eventType, Guid recordId, string correlationId)
        {
            if (string.IsNullOrEmpty(_snsTopicArn))
            {
                _logger.LogWarning(
                    "PublishDomainEventAsync: SNS topic ARN not configured. Event {EventType} not published. CorrelationId: {CorrelationId}",
                    eventType, correlationId);
                return;
            }

            try
            {
                var domainEvent = new AccountDomainEvent
                {
                    EventType = eventType,
                    EntityName = EntityName,
                    RecordId = recordId.ToString(),
                    CorrelationId = correlationId,
                    Timestamp = DateTime.UtcNow.ToString("O")
                };

                var messageBody = JsonSerializer.Serialize(
                    domainEvent, AccountHandlerJsonContext.Default.AccountDomainEvent);

                var publishRequest = new PublishRequest
                {
                    TopicArn = _snsTopicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>
                    {
                        ["eventType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["correlationId"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = correlationId
                        },
                        ["entityName"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = EntityName
                        }
                    },
                    // Use MessageGroupId for FIFO topic ordering (if applicable)
                    MessageGroupId = $"account-{recordId}"
                };

                await _snsClient.PublishAsync(publishRequest).ConfigureAwait(false);

                _logger.LogInformation(
                    "Domain event published: {EventType}, RecordId: {RecordId}, CorrelationId: {CorrelationId}",
                    eventType, recordId, correlationId);
            }
            catch (Exception ex)
            {
                // Non-blocking: log but do NOT fail the API response on SNS publish failure
                _logger.LogError(ex,
                    "PublishDomainEventAsync: Failed to publish {EventType} for {RecordId}. CorrelationId: {CorrelationId}",
                    eventType, recordId, correlationId);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Extracts JWT claims from API Gateway v2 authorizer context.
        /// Replaces the monolith's SecurityContext.OpenScope(user) AsyncLocal pattern.
        /// JWT claims are the sole identity source in Lambda (AAP §0.5.1).
        /// </summary>
        /// <param name="request">API Gateway v2 proxy request.</param>
        /// <returns>ClaimsPrincipal with extracted JWT claims, or anonymous principal if no claims.</returns>
        private static ClaimsPrincipal ExtractClaims(APIGatewayHttpApiV2ProxyRequest request)
        {
            var claims = new List<Claim>();

            var jwtClaims = request.RequestContext?.Authorizer?.Jwt?.Claims;
            if (jwtClaims == null || jwtClaims.Count == 0)
            {
                // Return anonymous principal for unauthenticated endpoints (e.g., health check)
                return new ClaimsPrincipal(new ClaimsIdentity());
            }

            // Extract standard Cognito JWT claims
            if (jwtClaims.TryGetValue("sub", out var sub))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
            }

            if (jwtClaims.TryGetValue("email", out var email))
            {
                claims.Add(new Claim(ClaimTypes.Email, email));
            }

            // Extract Cognito group memberships as role claims
            if (jwtClaims.TryGetValue("cognito:groups", out var groups))
            {
                foreach (var group in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    claims.Add(new Claim(ClaimTypes.Role, group));
                }
            }

            // Fallback: custom:role attribute
            if (jwtClaims.TryGetValue("custom:role", out var customRole))
            {
                claims.Add(new Claim(ClaimTypes.Role, customRole));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt"));
        }

        /// <summary>
        /// Extracts or generates a correlation ID for distributed tracing.
        /// Prefers x-correlation-id header, falls back to API Gateway requestId, then new GUID.
        /// Per AAP §0.8.5: "Structured JSON logging with correlation-ID propagation."
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            // Try x-correlation-id header (case-insensitive search)
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))
                    {
                        return header.Value;
                    }
                }
            }

            // Fall back to API Gateway request ID
            var requestId = request.RequestContext?.RequestId;
            if (!string.IsNullOrEmpty(requestId))
            {
                return requestId;
            }

            // Last resort: generate a new correlation ID
            return Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Safely extracts a path parameter from the API Gateway v2 proxy request.
        /// Returns null if the parameter is not present.
        /// </summary>
        private static string? ExtractPathParameter(APIGatewayHttpApiV2ProxyRequest request, string paramName)
        {
            if (request.PathParameters != null)
            {
                // 1. Try named parameter first (works when API GW uses named path vars)
                if (request.PathParameters.TryGetValue(paramName, out var value) &&
                    !string.IsNullOrEmpty(value))
                    return value;
                // 2. Fall back to {proxy+} path parameter used by HTTP API v2 catch-all routes.
                // Proxy path is "accounts/{id}" or "contacts/{id}" — ID is the last GUID segment.
                if (request.PathParameters.TryGetValue("proxy", out var proxy) &&
                    !string.IsNullOrEmpty(proxy))
                {
                    var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // Walk segments looking for a GUID (skip resource names like "accounts")
                    for (var i = segments.Length - 1; i >= 0; i--)
                    {
                        if (Guid.TryParse(segments[i], out _))
                            return segments[i];
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Safely extracts a header value from the API Gateway v2 proxy request.
        /// Performs case-insensitive header name lookup to handle varied HTTP client behavior.
        /// </summary>
        private static string? ExtractHeader(APIGatewayHttpApiV2ProxyRequest request, string headerName)
        {
            if (request.Headers == null) return null;

            // API Gateway v2 lowercases all header names
            if (request.Headers.TryGetValue(headerName.ToLowerInvariant(), out var lowerValue))
            {
                return lowerValue;
            }

            // Fallback: original case
            if (request.Headers.TryGetValue(headerName, out var originalValue))
            {
                return originalValue;
            }

            return null;
        }

        /// <summary>
        /// Builds an APIGatewayHttpApiV2ProxyResponse with proper JSON serialization,
        /// standard headers, and correlation-ID.
        /// Uses AccountHandlerJsonContext for AOT-compatible serialization, with type-specific
        /// switch to route each body type through the correct JsonTypeInfo.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(int statusCode, object? body, string correlationId)
        {
            string? jsonBody = null;
            if (body != null)
            {
                // AOT-safe serialization: route each known type through its JsonTypeInfo
                jsonBody = body switch
                {
                    Account a => JsonSerializer.Serialize(a, AccountHandlerJsonContext.Default.Account),
                    AccountListEnvelope e => JsonSerializer.Serialize(e, AccountHandlerJsonContext.Default.AccountListEnvelope),
                    ErrorResponse err => JsonSerializer.Serialize(err, AccountHandlerJsonContext.Default.ErrorResponse),
                    AccountDomainEvent evt => JsonSerializer.Serialize(evt, AccountHandlerJsonContext.Default.AccountDomainEvent),
                    // Fallback: serialize as string representation for unknown types
                    // This avoids AOT-unsafe JsonSerializer.Serialize<object> calls
                    _ => JsonSerializer.Serialize(body.ToString() ?? string.Empty, AccountHandlerJsonContext.Default.String)
                };
            }

            var response = new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Body = jsonBody,
                Headers = new Dictionary<string, string>(StandardResponseHeaders)
            };

            AddCorrelationIdHeader(response, correlationId);
            return response;
        }

        /// <summary>
        /// Adds the x-correlation-id header to the response for distributed tracing continuity.
        /// </summary>
        private static void AddCorrelationIdHeader(APIGatewayHttpApiV2ProxyResponse response, string correlationId)
        {
            response.Headers ??= new Dictionary<string, string>();
            response.Headers["x-correlation-id"] = correlationId;
        }

        #endregion
    }
}
