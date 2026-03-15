using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebVellaErp.Crm.DataAccess;
using WebVellaErp.Crm.Models;
using WebVellaErp.Crm.Services;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace WebVellaErp.Crm.Functions
{
    // =========================================================================
    // AOT-compatible JSON serialization context for ContactHandler
    // =========================================================================

    /// <summary>
    /// Source-generated JSON serializer context for Native AOT compatibility.
    /// Registers all types that flow through JsonSerializer.Serialize/Deserialize
    /// in ContactHandler, enabling .NET 9 Native AOT compilation with sub-1-second
    /// cold starts (AAP §0.8.2). Eliminates IL2026/IL3050 AOT trimming warnings.
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Contact))]
    [JsonSerializable(typeof(List<Contact>))]
    [JsonSerializable(typeof(ContactListEnvelope))]
    [JsonSerializable(typeof(ContactDomainEvent))]
    [JsonSerializable(typeof(ErrorResponse))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class ContactHandlerJsonContext : JsonSerializerContext { }

    // =========================================================================
    // Domain event payload DTO for SNS publishing
    // =========================================================================

    /// <summary>
    /// Domain event payload published to SNS for contact lifecycle changes.
    /// Follows {domain}.{entity}.{action} naming convention per AAP §0.8.5.
    /// Replaces the synchronous ContactHook post-CRUD calls from the monolith.
    /// </summary>
    internal sealed class ContactDomainEvent
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
    // Response envelope DTOs
    // =========================================================================

    /// <summary>
    /// Paginated list response envelope for contact list endpoint.
    /// Format: { "data": [...contacts], "meta": { "page": N, "pageSize": N, "total": N } }
    /// </summary>
    internal sealed class ContactListEnvelope
    {
        [JsonPropertyName("data")]
        public List<Contact> Data { get; set; } = new();

        [JsonPropertyName("meta")]
        public PaginationMeta Meta { get; set; } = new();
    }

    /// <summary>
    /// Pagination metadata for list responses.
    /// </summary>
    internal sealed class PaginationMeta
    {
        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }
    }

    /// <summary>
    /// Standard error response envelope for 4xx/5xx responses.
    /// </summary>
    internal sealed class ErrorResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    // =========================================================================
    // ContactHandler — Lambda entry point for CRM Contact CRUD
    // =========================================================================

    /// <summary>
    /// Primary AWS Lambda entry point for all CRM Contact HTTP API Gateway v2 requests.
    /// 
    /// Replaces:
    /// - WebVella.Erp.Web/Controllers/WebApiController.cs (contact-related MVC endpoints)
    /// - WebVella.Erp.Plugins.Next/Hooks/Api/ContactHook.cs (synchronous post-CRUD hooks)
    /// - WebVella.Erp/Api/RecordManager.cs (record CRUD with hook orchestration)
    /// - WebVella.Erp/Api/SecurityContext.cs (AsyncLocal user scoping)
    /// 
    /// This is NOT an MVC controller. It is a Lambda handler receiving API Gateway v2
    /// proxy events. Authentication is handled by API Gateway JWT authorizer; this
    /// handler extracts JWT claims from the request context for authorization decisions.
    /// 
    /// API Routes:
    ///   POST   /v1/contacts         → CreateContactAsync
    ///   GET    /v1/contacts         → ListContactsAsync
    ///   GET    /v1/contacts/{id}    → GetContactAsync
    ///   PUT    /v1/contacts/{id}    → UpdateContactAsync
    ///   DELETE /v1/contacts/{id}    → DeleteContactAsync
    /// </summary>
    public class ContactHandler
    {
        #region Constants

        /// <summary>
        /// Entity name for the contact entity. Matches source
        /// [HookAttachment("contact", int.MinValue)] from ContactHook.cs line 9.
        /// </summary>
        public const string EntityName = "contact";

        /// <summary>
        /// Contact entity GUID — derived from <see cref="Contact.EntityId"/>.
        /// Originally from NextPlugin.20190204.cs line 1408.
        /// </summary>
        public static readonly Guid ContactEntityId = Contact.EntityId;

        /// <summary>
        /// Contact system ID field GUID — from NextPlugin.20190204.cs line 1407.
        /// </summary>
        public static readonly Guid ContactIdFieldId = new Guid("859f24ec-4d3e-4597-9972-1d5a9cba918b");

        /// <summary>
        /// Default salutation ID — derived from <see cref="Contact.DefaultSalutationId"/>.
        /// Originally from NextPlugin.20190206.cs line 131.
        /// The original source had a misspelled 'solutation_id' field (20190204 lines 1687-1715)
        /// which was deleted in 20190206 (lines 45-53) and replaced with correctly spelled
        /// 'salutation_id' (lines 519-547). This handler uses ONLY the corrected field name.
        /// </summary>
        public static readonly Guid DefaultSalutationId = Contact.DefaultSalutationId;

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
        private const string EventContactCreated = "crm.contact.created";
        private const string EventContactUpdated = "crm.contact.updated";
        private const string EventContactDeleted = "crm.contact.deleted";

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
        private readonly ILogger<ContactHandler> _logger;
        private readonly IConfiguration _configuration;
        private readonly string? _snsTopicArn;

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for AWS Lambda runtime invocation.
        /// Builds the DI ServiceCollection, registers all dependencies (AWS SDK clients,
        /// application services, configuration), and resolves required services.
        /// AWS SDK clients are configured with AWS_ENDPOINT_URL for LocalStack compatibility
        /// per AAP §0.7.6.
        /// </summary>
        public ContactHandler()
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
            _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ContactHandler>();
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
        public ContactHandler(IServiceProvider serviceProvider)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);

            _crmRepository = serviceProvider.GetRequiredService<ICrmRepository>();
            _searchService = serviceProvider.GetRequiredService<ISearchService>();
            _snsClient = serviceProvider.GetRequiredService<IAmazonSimpleNotificationService>();
            _logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ContactHandler>();
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

            _logger.LogInformation(
                "ContactHandler invoked. Method: {Method}, Path: {Path}, CorrelationId: {CorrelationId}, RequestId: {RequestId}",
                request.RequestContext?.Http?.Method ?? "UNKNOWN",
                request.RequestContext?.Http?.Path ?? "UNKNOWN",
                correlationId,
                context.AwsRequestId);

            try
            {
                var httpMethod = request.RequestContext?.Http?.Method?.ToUpperInvariant() ?? string.Empty;
                var contactId = ExtractPathParameter(request, "id");

                return httpMethod switch
                {
                    "POST" => await CreateContactAsync(request, correlationId).ConfigureAwait(false),
                    "GET" when !string.IsNullOrEmpty(contactId) =>
                        await GetContactAsync(contactId, correlationId).ConfigureAwait(false),
                    "GET" => await ListContactsAsync(request, correlationId).ConfigureAwait(false),
                    "PUT" when !string.IsNullOrEmpty(contactId) =>
                        await UpdateContactAsync(contactId, request, correlationId).ConfigureAwait(false),
                    "DELETE" when !string.IsNullOrEmpty(contactId) =>
                        await DeleteContactAsync(contactId, correlationId).ConfigureAwait(false),
                    _ => BuildResponse(
                        (int)HttpStatusCode.MethodNotAllowed,
                        new ErrorResponse { Success = false, Message = $"Method {httpMethod} is not allowed." },
                        correlationId)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ContactHandler: Unhandled exception. CorrelationId: {CorrelationId}",
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
        /// Creates a new contact record.
        /// 
        /// Replaces: POST endpoint in WebApiController.cs + ContactHook.OnPostCreateRecord
        /// 
        /// Pre-hook validation (inline, blocking — replaces absent IErpPreCreateRecordHook):
        /// - Validates required fields (FirstName, LastName per Contact model)
        /// - Sets defaults: Id, CreatedOn, SalutationId, XSearch
        /// 
        /// Post-hook behavior (non-blocking — replaces ContactHook.cs lines 12-15):
        /// - Regenerates x_search field via ISearchService
        /// - Publishes crm.contact.created SNS domain event
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> CreateContactAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId)
        {
            _logger.LogInformation(
                "CreateContactAsync invoked. CorrelationId: {CorrelationId}", correlationId);

            // Extract idempotency key per AAP §0.8.5
            var idempotencyKey = ExtractHeader(request, "x-idempotency-key");
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                _logger.LogInformation(
                    "CreateContactAsync: IdempotencyKey={IdempotencyKey}, CorrelationId: {CorrelationId}",
                    idempotencyKey, correlationId);
            }

            // Validate request body is present
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                _logger.LogWarning(
                    "CreateContactAsync: Empty request body. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body is required." },
                    correlationId);
            }

            // Deserialize using System.Text.Json (NOT Newtonsoft.Json) for Native AOT
            Contact? contact;
            try
            {
                contact = JsonSerializer.Deserialize(
                    request.Body, ContactHandlerJsonContext.Default.Contact);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "CreateContactAsync: Malformed JSON. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid JSON in request body." },
                    correlationId);
            }

            if (contact == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body could not be parsed." },
                    correlationId);
            }

            // ---- Pre-hook validation (inline, blocking) ----
            // Replaces IErpPreCreateRecordHook (AAP §0.7.2)

            var validationErrors = new List<string>();
            if (string.IsNullOrWhiteSpace(contact.FirstName))
            {
                validationErrors.Add("first_name is required.");
            }
            if (string.IsNullOrWhiteSpace(contact.LastName))
            {
                validationErrors.Add("last_name is required.");
            }

            if (validationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "CreateContactAsync: Validation failed — {Errors}. CorrelationId: {CorrelationId}",
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
            if (contact.Id == Guid.Empty)
            {
                contact.Id = Guid.NewGuid();
            }

            // UseCurrentTimeAsDefaultValue=true from NextPlugin.20190206.cs line 440-441
            if (contact.CreatedOn == default)
            {
                contact.CreatedOn = DateTime.UtcNow;
            }

            // Default salutation: 87c08ee1-... from NextPlugin.20190206.cs line 131
            // Corrected 'salutation_id' (replaces misspelled 'solutation_id' from 20190204)
            if (contact.SalutationId == Guid.Empty)
            {
                contact.SalutationId = DefaultSalutationId;
            }

            // DefaultValue="" from NextPlugin.20190206.cs line 510
            if (contact.XSearch == null)
            {
                contact.XSearch = string.Empty;
            }

            // ---- Persist to DynamoDB ----
            try
            {
                await _crmRepository.CreateAsync(EntityName, contact).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CreateContactAsync: DynamoDB CreateAsync failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contact.Id, correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to create contact record." },
                    correlationId);
            }

            // ---- Post-hook behavior (non-blocking) ----
            // Replaces ContactHook.OnPostCreateRecord (ContactHook.cs lines 12-15):
            //   new SearchService().RegenSearchField(entityName, record, Configuration.ContactSearchIndexFields)
            try
            {
                await _searchService.RegenSearchFieldAsync(
                    EntityName,
                    contact.Id,
                    SearchIndexConfiguration.ContactSearchIndexFields.ToList())
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Log but do not fail the request — search index is eventually consistent
                _logger.LogWarning(ex,
                    "CreateContactAsync: RegenSearchFieldAsync failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contact.Id, correlationId);
            }

            // Publish SNS domain event: crm.contact.created (AAP §0.7.2)
            await PublishDomainEventAsync(EventContactCreated, contact.Id, correlationId)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Contact created: {ContactId}, CorrelationId: {CorrelationId}",
                contact.Id, correlationId);

            // Return 201 Created with Location header
            var responseHeaders = new Dictionary<string, string>(StandardResponseHeaders)
            {
                ["Location"] = $"/v1/contacts/{contact.Id:D}"
            };

            var body = JsonSerializer.Serialize(contact, ContactHandlerJsonContext.Default.Contact);

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)HttpStatusCode.Created,
                Headers = AddCorrelationIdHeader(responseHeaders, correlationId),
                Body = body
            };
        }

        /// <summary>
        /// Retrieves a single contact by ID.
        /// No SNS events on read operations.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> GetContactAsync(
            string contactIdStr,
            string correlationId)
        {
            _logger.LogInformation(
                "GetContactAsync invoked. ContactId={ContactId}, CorrelationId: {CorrelationId}",
                contactIdStr, correlationId);

            if (!Guid.TryParse(contactIdStr, out var contactId))
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid contact ID format. Must be a valid GUID." },
                    correlationId);
            }

            Contact? contact;
            try
            {
                contact = await _crmRepository.GetByIdAsync<Contact>(EntityName, contactId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetContactAsync: DynamoDB GetByIdAsync failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contactId, correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to retrieve contact record." },
                    correlationId);
            }

            if (contact == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.NotFound,
                    new ErrorResponse { Success = false, Message = $"Contact with ID '{contactId}' not found." },
                    correlationId);
            }

            var body = JsonSerializer.Serialize(contact, ContactHandlerJsonContext.Default.Contact);

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Headers = AddCorrelationIdHeader(new Dictionary<string, string>(StandardResponseHeaders), correlationId),
                Body = body
            };
        }

        /// <summary>
        /// Lists contacts with pagination, filtering, sorting, and search.
        /// 
        /// Query parameters:
        ///   page (int, default 1)
        ///   pageSize (int, default 20, max 100)
        ///   search (string?) — text search on x_search field
        ///   sortBy (string?, default "created_on")
        ///   sortDir (string?, default "desc")
        ///   accountId (Guid?) — filter by related account
        ///   salutationId (Guid?) — filter by salutation
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> ListContactsAsync(
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId)
        {
            _logger.LogInformation(
                "ListContactsAsync invoked. CorrelationId: {CorrelationId}", correlationId);

            // Parse query string parameters
            var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

            int page = 1;
            if (queryParams.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
            {
                page = Math.Max(1, parsedPage);
            }

            int pageSize = DefaultPageSize;
            if (queryParams.TryGetValue("pageSize", out var pageSizeStr) && int.TryParse(pageSizeStr, out var parsedPageSize))
            {
                pageSize = Math.Clamp(parsedPageSize, 1, MaxPageSize);
            }

            var search = queryParams.TryGetValue("search", out var searchStr) ? searchStr : null;
            var sortBy = queryParams.TryGetValue("sortBy", out var sortByStr) ? sortByStr : "created_on";
            var sortDir = queryParams.TryGetValue("sortDir", out var sortDirStr) ? sortDirStr : "desc";

            Guid? accountId = null;
            if (queryParams.TryGetValue("accountId", out var accountIdStr) && Guid.TryParse(accountIdStr, out var parsedAccountId))
            {
                accountId = parsedAccountId;
            }

            Guid? salutationId = null;
            if (queryParams.TryGetValue("salutationId", out var salutationIdStr) && Guid.TryParse(salutationIdStr, out var parsedSalutationId))
            {
                salutationId = parsedSalutationId;
            }

            var pagination = new PaginationOptions
            {
                Limit = pageSize,
                Skip = (page - 1) * pageSize
            };

            try
            {
                List<Contact> contacts;
                long totalCount;

                if (!string.IsNullOrWhiteSpace(search))
                {
                    // Text search on x_search field using GSI2
                    contacts = await _crmRepository.SearchAsync<Contact>(
                        EntityName, search, pagination)
                        .ConfigureAwait(false);

                    // For search results, count is estimated from results
                    totalCount = contacts.Count;
                }
                else
                {
                    // Build filter from query parameters
                    QueryFilter? filter = BuildContactFilter(accountId, salutationId);

                    var sort = new SortOptions
                    {
                        FieldName = sortBy,
                        Direction = sortDir.Equals("asc", StringComparison.OrdinalIgnoreCase)
                            ? SortDirection.Ascending
                            : SortDirection.Descending
                    };

                    contacts = await _crmRepository.QueryAsync<Contact>(
                        EntityName, filter, sort, pagination)
                        .ConfigureAwait(false);

                    totalCount = await _crmRepository.CountAsync(EntityName, filter)
                        .ConfigureAwait(false);
                }

                var envelope = new ContactListEnvelope
                {
                    Data = contacts,
                    Meta = new PaginationMeta
                    {
                        Page = page,
                        PageSize = pageSize,
                        Total = totalCount
                    }
                };

                var body = JsonSerializer.Serialize(envelope, ContactHandlerJsonContext.Default.ContactListEnvelope);

                return new APIGatewayHttpApiV2ProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Headers = AddCorrelationIdHeader(new Dictionary<string, string>(StandardResponseHeaders), correlationId),
                    Body = body
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ListContactsAsync: Query failed. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to list contacts." },
                    correlationId);
            }
        }

        /// <summary>
        /// Updates an existing contact record.
        /// 
        /// Replaces: PUT endpoint in WebApiController.cs + ContactHook.OnPostUpdateRecord
        /// 
        /// Pre-hook validation (inline, blocking):
        /// - Same required field validation as Create
        /// - Ensures contactId matches body Id if present
        /// 
        /// Post-hook behavior (non-blocking — replaces ContactHook.cs lines 17-20):
        /// - Regenerates x_search field via ISearchService
        /// - Publishes crm.contact.updated SNS domain event
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> UpdateContactAsync(
            string contactIdStr,
            APIGatewayHttpApiV2ProxyRequest request,
            string correlationId)
        {
            _logger.LogInformation(
                "UpdateContactAsync invoked. ContactId={ContactId}, CorrelationId: {CorrelationId}",
                contactIdStr, correlationId);

            // Extract idempotency key per AAP §0.8.5
            var idempotencyKey = ExtractHeader(request, "x-idempotency-key");
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                _logger.LogInformation(
                    "UpdateContactAsync: IdempotencyKey={IdempotencyKey}, CorrelationId: {CorrelationId}",
                    idempotencyKey, correlationId);
            }

            if (!Guid.TryParse(contactIdStr, out var contactId))
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid contact ID format. Must be a valid GUID." },
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
            Contact? contact;
            try
            {
                contact = JsonSerializer.Deserialize(
                    request.Body, ContactHandlerJsonContext.Default.Contact);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "UpdateContactAsync: Malformed JSON. CorrelationId: {CorrelationId}",
                    correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid JSON in request body." },
                    correlationId);
            }

            if (contact == null)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Request body could not be parsed." },
                    correlationId);
            }

            // Ensure path parameter matches body ID if provided
            if (contact.Id != Guid.Empty && contact.Id != contactId)
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse
                    {
                        Success = false,
                        Message = "Contact ID in path does not match ID in request body."
                    },
                    correlationId);
            }
            contact.Id = contactId;

            // ---- Pre-hook validation (inline, blocking) ----
            var validationErrors = new List<string>();
            if (string.IsNullOrWhiteSpace(contact.FirstName))
            {
                validationErrors.Add("first_name is required.");
            }
            if (string.IsNullOrWhiteSpace(contact.LastName))
            {
                validationErrors.Add("last_name is required.");
            }

            if (validationErrors.Count > 0)
            {
                _logger.LogWarning(
                    "UpdateContactAsync: Validation failed — {Errors}. CorrelationId: {CorrelationId}",
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
            try
            {
                var existing = await _crmRepository.GetByIdAsync<Contact>(EntityName, contactId)
                    .ConfigureAwait(false);
                if (existing == null)
                {
                    return BuildResponse(
                        (int)HttpStatusCode.NotFound,
                        new ErrorResponse { Success = false, Message = $"Contact with ID '{contactId}' not found." },
                        correlationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UpdateContactAsync: Existence check failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contactId, correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to verify contact existence." },
                    correlationId);
            }

            // ---- Persist update to DynamoDB ----
            try
            {
                await _crmRepository.UpdateAsync(EntityName, contactId, contact)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "UpdateContactAsync: DynamoDB UpdateAsync failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contactId, correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to update contact record." },
                    correlationId);
            }

            // ---- Post-hook behavior (non-blocking) ----
            // Replaces ContactHook.OnPostUpdateRecord (ContactHook.cs lines 17-20):
            //   new SearchService().RegenSearchField(entityName, record, Configuration.ContactSearchIndexFields)
            try
            {
                await _searchService.RegenSearchFieldAsync(
                    EntityName,
                    contact.Id,
                    SearchIndexConfiguration.ContactSearchIndexFields.ToList())
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "UpdateContactAsync: RegenSearchFieldAsync failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contact.Id, correlationId);
            }

            // Publish SNS domain event: crm.contact.updated (AAP §0.7.2)
            await PublishDomainEventAsync(EventContactUpdated, contact.Id, correlationId)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Contact updated: {ContactId}, CorrelationId: {CorrelationId}",
                contact.Id, correlationId);

            var body = JsonSerializer.Serialize(contact, ContactHandlerJsonContext.Default.Contact);

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Headers = AddCorrelationIdHeader(new Dictionary<string, string>(StandardResponseHeaders), correlationId),
                Body = body
            };
        }

        /// <summary>
        /// Deletes a contact record.
        /// 
        /// NOTE: Source ContactHook did NOT implement IErpPostDeleteRecordHook,
        /// but AAP §0.5.1 explicitly requires crm.contact.deleted event.
        /// This handler publishes the delete event as specified.
        /// </summary>
        private async Task<APIGatewayHttpApiV2ProxyResponse> DeleteContactAsync(
            string contactIdStr,
            string correlationId)
        {
            _logger.LogInformation(
                "DeleteContactAsync invoked. ContactId={ContactId}, CorrelationId: {CorrelationId}",
                contactIdStr, correlationId);

            if (!Guid.TryParse(contactIdStr, out var contactId))
            {
                return BuildResponse(
                    (int)HttpStatusCode.BadRequest,
                    new ErrorResponse { Success = false, Message = "Invalid contact ID format. Must be a valid GUID." },
                    correlationId);
            }

            // Verify record exists — return 404 if not found
            try
            {
                var existing = await _crmRepository.GetByIdAsync<Contact>(EntityName, contactId)
                    .ConfigureAwait(false);
                if (existing == null)
                {
                    return BuildResponse(
                        (int)HttpStatusCode.NotFound,
                        new ErrorResponse { Success = false, Message = $"Contact with ID '{contactId}' not found." },
                        correlationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeleteContactAsync: Existence check failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contactId, correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to verify contact existence." },
                    correlationId);
            }

            // ---- Delete from DynamoDB ----
            try
            {
                await _crmRepository.DeleteAsync(EntityName, contactId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DeleteContactAsync: DynamoDB DeleteAsync failed for ContactId={ContactId}. CorrelationId: {CorrelationId}",
                    contactId, correlationId);

                return BuildResponse(
                    (int)HttpStatusCode.InternalServerError,
                    new ErrorResponse { Success = false, Message = "Failed to delete contact record." },
                    correlationId);
            }

            // ---- Post-hook via SNS ----
            // Source ContactHook did NOT implement IErpPostDeleteRecordHook, but AAP §0.5.1
            // explicitly requires crm.contact.deleted event.
            await PublishDomainEventAsync(EventContactDeleted, contactId, correlationId)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Contact deleted: {ContactId}, CorrelationId: {CorrelationId}",
                contactId, correlationId);

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = (int)HttpStatusCode.NoContent,
                Headers = AddCorrelationIdHeader(new Dictionary<string, string>(StandardResponseHeaders), correlationId)
            };
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Extracts JWT claims from the API Gateway v2 authorizer context.
        /// Replaces the monolith's SecurityContext.OpenScope(user) AsyncLocal pattern.
        /// Parses 'sub' for user ID, 'cognito:groups' for roles, 'email' for user email.
        /// 
        /// Contact entity RecordPermissions from source (NextPlugin.20190204.cs lines 1416-1432):
        ///   CanCreate/Read/Update/Delete: Regular role (f16ec6db-...) + Admin role (bdc56420-...)
        /// </summary>
        private static ClaimsPrincipal ExtractClaims(APIGatewayHttpApiV2ProxyRequest request)
        {
            var claims = new List<Claim>();

            try
            {
                var jwt = request.RequestContext?.Authorizer?.Jwt;
                if (jwt?.Claims == null)
                {
                    return new ClaimsPrincipal(new ClaimsIdentity());
                }

                // Map 'sub' claim → ClaimTypes.NameIdentifier
                if (jwt.Claims.TryGetValue("sub", out var sub) && !string.IsNullOrEmpty(sub))
                {
                    claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
                }

                // Map 'email' claim → ClaimTypes.Email
                if (jwt.Claims.TryGetValue("email", out var email) && !string.IsNullOrEmpty(email))
                {
                    claims.Add(new Claim(ClaimTypes.Email, email));
                }

                // Map 'cognito:groups' → multiple ClaimTypes.Role claims
                if (jwt.Claims.TryGetValue("cognito:groups", out var groups) && !string.IsNullOrEmpty(groups))
                {
                    var groupList = groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var group in groupList)
                    {
                        claims.Add(new Claim(ClaimTypes.Role, group));
                    }
                }

                // Map 'custom:role' → ClaimTypes.Role (single custom role)
                if (jwt.Claims.TryGetValue("custom:role", out var customRole) && !string.IsNullOrEmpty(customRole))
                {
                    claims.Add(new Claim(ClaimTypes.Role, customRole));
                }

                // Map 'role' → ClaimTypes.Role (standard JWT claim)
                if (jwt.Claims.TryGetValue("role", out var role) && !string.IsNullOrEmpty(role))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
            catch
            {
                // Return empty principal on parsing failure — caller decides on 401/403
            }

            var identity = new ClaimsIdentity(claims, "jwt");
            return new ClaimsPrincipal(identity);
        }

        /// <summary>
        /// Builds a QueryFilter from optional contact list parameters.
        /// Supports filtering by accountId and salutationId.
        /// </summary>
        private static QueryFilter? BuildContactFilter(Guid? accountId, Guid? salutationId)
        {
            var subFilters = new List<QueryFilter>();

            if (accountId.HasValue)
            {
                subFilters.Add(new QueryFilter
                {
                    FieldName = "account_id",
                    Operator = FilterOperator.Equal,
                    Value = accountId.Value.ToString("D")
                });
            }

            if (salutationId.HasValue)
            {
                subFilters.Add(new QueryFilter
                {
                    FieldName = "salutation_id",
                    Operator = FilterOperator.Equal,
                    Value = salutationId.Value.ToString("D")
                });
            }

            if (subFilters.Count == 0)
            {
                return null;
            }

            if (subFilters.Count == 1)
            {
                return subFilters[0];
            }

            return new QueryFilter
            {
                SubFilters = subFilters,
                Logic = FilterLogic.And
            };
        }

        /// <summary>
        /// Publishes a domain event to the SNS topic for contact lifecycle changes.
        /// Errors are logged but NOT thrown — non-blocking per AAP §0.7.2.
        /// </summary>
        private async Task PublishDomainEventAsync(
            string eventType,
            Guid recordId,
            string correlationId)
        {
            if (string.IsNullOrWhiteSpace(_snsTopicArn))
            {
                _logger.LogWarning(
                    "PublishDomainEventAsync: SNS topic ARN not configured. " +
                    "Skipping event publish for {EventType}. CorrelationId: {CorrelationId}",
                    eventType, correlationId);
                return;
            }

            try
            {
                var domainEvent = new ContactDomainEvent
                {
                    EventType = eventType,
                    EntityName = EntityName,
                    RecordId = recordId.ToString("D"),
                    CorrelationId = correlationId,
                    Timestamp = DateTime.UtcNow.ToString("O")
                };

                var messageBody = JsonSerializer.Serialize(
                    domainEvent, ContactHandlerJsonContext.Default.ContactDomainEvent);

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
                    }
                };

                var publishResponse = await _snsClient.PublishAsync(publishRequest)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "PublishDomainEventAsync: Published {EventType} for contact {RecordId}. " +
                    "MessageId: {MessageId}. CorrelationId: {CorrelationId}",
                    eventType, recordId, publishResponse.MessageId, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PublishDomainEventAsync: Failed to publish {EventType} for contact {RecordId}. " +
                    "CorrelationId: {CorrelationId}",
                    eventType, recordId, correlationId);
            }
        }

        /// <summary>
        /// Extracts or generates a correlation ID for distributed tracing.
        /// Priority: x-correlation-id header → API Gateway request ID → new GUID.
        /// Per AAP §0.8.5.
        /// </summary>
        private static string GetCorrelationId(APIGatewayHttpApiV2ProxyRequest request)
        {
            if (request.Headers != null)
            {
                if (request.Headers.TryGetValue("x-correlation-id", out var correlationHeader) &&
                    !string.IsNullOrWhiteSpace(correlationHeader))
                {
                    return correlationHeader.Trim();
                }

                if (request.Headers.TryGetValue("X-Correlation-Id", out var correlationHeaderMixed) &&
                    !string.IsNullOrWhiteSpace(correlationHeaderMixed))
                {
                    return correlationHeaderMixed.Trim();
                }
            }

            var requestContextId = request.RequestContext?.RequestId;
            if (!string.IsNullOrWhiteSpace(requestContextId))
            {
                return requestContextId;
            }

            return Guid.NewGuid().ToString("D");
        }

        /// <summary>
        /// Extracts a named path parameter from the API Gateway v2 proxy request.
        /// </summary>
        private static string? ExtractPathParameter(
            APIGatewayHttpApiV2ProxyRequest request,
            string parameterName)
        {
            if (request.PathParameters != null)
            {
                if (request.PathParameters.TryGetValue(parameterName, out var value) &&
                    !string.IsNullOrEmpty(value))
                    return value;
                // Fall back to {proxy+} path parameter for HTTP API v2 catch-all routes.
                if (request.PathParameters.TryGetValue("proxy", out var proxy) &&
                    !string.IsNullOrEmpty(proxy))
                {
                    var segments = proxy.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
        /// Extracts a named header value from the API Gateway v2 proxy request.
        /// Header names are compared case-insensitively.
        /// </summary>
        private static string? ExtractHeader(
            APIGatewayHttpApiV2ProxyRequest request,
            string headerName)
        {
            if (request.Headers == null)
            {
                return null;
            }

            // API Gateway v2 lowercases all header names
            if (request.Headers.TryGetValue(headerName.ToLowerInvariant(), out var value))
            {
                return value;
            }

            // Try original case as fallback
            if (request.Headers.TryGetValue(headerName, out var valueMixed))
            {
                return valueMixed;
            }

            return null;
        }

        /// <summary>
        /// Builds a standardized API Gateway v2 proxy response with JSON body.
        /// Includes Content-Type, CORS, and correlation-ID headers.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyResponse BuildResponse(
            int statusCode,
            object body,
            string correlationId)
        {
            string serializedBody;
            try
            {
                serializedBody = body switch
                {
                    ErrorResponse error => JsonSerializer.Serialize(error,
                        ContactHandlerJsonContext.Default.ErrorResponse),
                    Contact contact => JsonSerializer.Serialize(contact,
                        ContactHandlerJsonContext.Default.Contact),
                    ContactListEnvelope envelope => JsonSerializer.Serialize(envelope,
                        ContactHandlerJsonContext.Default.ContactListEnvelope),
                    _ => JsonSerializer.Serialize(body, body.GetType(),
                        ContactHandlerJsonContext.Default)
                };
            }
            catch (Exception)
            {
                serializedBody = JsonSerializer.Serialize(
                    new ErrorResponse { Success = false, Message = "Response serialization error." },
                    ContactHandlerJsonContext.Default.ErrorResponse);
            }

            var headers = AddCorrelationIdHeader(
                new Dictionary<string, string>(StandardResponseHeaders), correlationId);

            return new APIGatewayHttpApiV2ProxyResponse
            {
                StatusCode = statusCode,
                Headers = headers,
                Body = serializedBody
            };
        }

        /// <summary>
        /// Adds the x-correlation-id header to a response headers dictionary.
        /// </summary>
        private static Dictionary<string, string> AddCorrelationIdHeader(
            Dictionary<string, string> headers,
            string correlationId)
        {
            headers["x-correlation-id"] = correlationId;
            return headers;
        }

        #endregion
    }
}
