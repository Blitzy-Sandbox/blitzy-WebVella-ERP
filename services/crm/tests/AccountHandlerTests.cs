// ---------------------------------------------------------------------------
// AccountHandlerTests.cs — CRM AccountHandler Lambda Unit Tests
// Bounded context: CRM / Contacts
// Source: WebVella.Erp.Plugins.Next/Hooks/Api/AccountHook.cs (post-CRUD hooks)
//         WebVella.Erp.Plugins.Next/NextPlugin.20190204.cs (entity definition)
//         WebVella.Erp.Plugins.Next/NextPlugin.20190206.cs (field defaults)
//         WebVella.Erp.Plugins.Next/Configuration.cs (search index fields)
//
// Tests verify all Account CRUD Lambda handler behavior: create, read, update,
// delete, list, and search — including request/response mapping, SNS domain
// event publishing, search index regeneration, input validation, correlation-ID
// propagation, idempotency key handling, and HTTP method routing.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Crm.DataAccess;
using WebVellaErp.Crm.Functions;
using WebVellaErp.Crm.Models;
using WebVellaErp.Crm.Services;
using Xunit;

namespace WebVellaErp.Crm.Tests;

/// <summary>
/// Comprehensive unit tests for <see cref="AccountHandler"/> Lambda handler.
/// Covers all Account CRUD operations, SNS domain event publishing, search
/// index regeneration (replacing monolith AccountHook.OnPostCreateRecord /
/// OnPostUpdateRecord), validation rules, default value assignment,
/// correlation-ID propagation, idempotency key handling, and HTTP method routing.
/// </summary>
public class AccountHandlerTests : IDisposable
{
    #region Fields & Constants

    // Mocked dependencies — injected via IServiceProvider constructor
    private readonly Mock<ICrmRepository> _mockCrmRepository;
    private readonly Mock<ISearchService> _mockSearchService;
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<ILogger<AccountHandler>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;

    // System under test
    private readonly AccountHandler _handler;

    // Lambda execution context (Amazon.Lambda.TestUtilities)
    private readonly ILambdaContext _lambdaContext;

    // ServiceProvider kept for disposal
    private readonly ServiceProvider _serviceProvider;

    /// <summary>Test SNS topic ARN matching AccountHandler.SnsTopicArnKey config key.</summary>
    private const string TestSnsTopicArn = "arn:aws:sns:us-east-1:000000000000:crm-events";

    /// <summary>Stable correlation ID for request tracing assertions.</summary>
    private const string TestCorrelationId = "test-correlation-id-12345";

    /// <summary>Stable idempotency key for write-side duplicate detection assertions.</summary>
    private const string TestIdempotencyKey = "idem-key-98765";

    /// <summary>
    /// Account entity GUID constant from source NextPlugin.20190204.cs line 43.
    /// Matches <see cref="Account.EntityId"/>.
    /// </summary>
    private static readonly Guid AccountEntityGuid =
        Guid.Parse("2e22b50f-e444-4b62-a171-076e51246939");

    /// <summary>
    /// Default salutation GUID from NextPlugin.20190206.cs line 131.
    /// Matches <see cref="Account.DefaultSalutationId"/>.
    /// </summary>
    private static readonly Guid DefaultSalutationGuid =
        Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698");

    #endregion

    #region Constructor (Test Fixture Setup)

    /// <summary>
    /// Initializes all mocked dependencies, builds <see cref="IServiceProvider"/>
    /// with registered mocks, and constructs <see cref="AccountHandler"/> via the
    /// IServiceProvider constructor (matching production DI wiring).
    /// </summary>
    public AccountHandlerTests()
    {
        _mockCrmRepository = new Mock<ICrmRepository>();
        _mockSearchService = new Mock<ISearchService>();
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockLogger = new Mock<ILogger<AccountHandler>>();
        _mockConfiguration = new Mock<IConfiguration>();

        // Configure IConfiguration mock: SNS topic ARN
        _mockConfiguration
            .Setup(c => c["SNS:CrmEventTopicArn"])
            .Returns(TestSnsTopicArn);

        // Default SNS publish success — all CRUD tests expect this unless overridden
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { MessageId = "test-message-id" });

        // Default search service success — non-blocking post-hook behavior
        _mockSearchService
            .Setup(x => x.RegenSearchFieldAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<List<string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Build ILoggerFactory mock that returns our mock logger
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory
            .Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(_mockLogger.Object);

        // Build ServiceCollection with all mocked dependencies
        var services = new ServiceCollection();
        services.AddSingleton<ICrmRepository>(_mockCrmRepository.Object);
        services.AddSingleton<ISearchService>(_mockSearchService.Object);
        services.AddSingleton<IAmazonSimpleNotificationService>(_mockSnsClient.Object);
        services.AddSingleton<ILoggerFactory>(mockLoggerFactory.Object);
        services.AddSingleton<IConfiguration>(_mockConfiguration.Object);

        // Build service provider and construct handler via IServiceProvider constructor
        _serviceProvider = services.BuildServiceProvider();
        _handler = new AccountHandler(_serviceProvider);

        // Create TestLambdaContext (from Amazon.Lambda.TestUtilities)
        _lambdaContext = new TestLambdaContext
        {
            FunctionName = "AccountHandlerTest",
            AwsRequestId = Guid.NewGuid().ToString("D")
        };
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Constructs an <see cref="APIGatewayHttpApiV2ProxyRequest"/> for testing.
    /// Sets up RequestContext.Http for method routing and default correlation-ID header.
    /// </summary>
    private static APIGatewayHttpApiV2ProxyRequest BuildRequest(
        string httpMethod,
        string? body = null,
        Dictionary<string, string>? pathParameters = null,
        Dictionary<string, string>? queryStringParameters = null,
        Dictionary<string, string>? headers = null)
    {
        var effectiveHeaders = headers ?? new Dictionary<string, string>
        {
            ["x-correlation-id"] = TestCorrelationId
        };

        return new APIGatewayHttpApiV2ProxyRequest
        {
            RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                {
                    Method = httpMethod,
                    Path = pathParameters?.ContainsKey("id") == true
                        ? $"/v1/accounts/{pathParameters["id"]}"
                        : "/v1/accounts"
                },
                RequestId = Guid.NewGuid().ToString("D")
            },
            Body = body,
            PathParameters = pathParameters ?? new Dictionary<string, string>(),
            QueryStringParameters = queryStringParameters ?? new Dictionary<string, string>(),
            Headers = effectiveHeaders
        };
    }

    /// <summary>
    /// Creates a fully populated test <see cref="Account"/> with known values.
    /// Uses AccountType.Company ("1") by default with all required fields populated.
    /// </summary>
    private static Account CreateTestAccount(Guid? id = null)
    {
        return new Account
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Test Corp",
            Type = AccountType.Company,
            FirstName = "John",
            LastName = "Doe",
            SalutationId = Account.DefaultSalutationId,
            CreatedOn = DateTime.UtcNow,
            XSearch = "test corp john doe"
        };
    }

    /// <summary>
    /// Serializes an <see cref="Account"/> to JSON using System.Text.Json.
    /// The Account model's [JsonPropertyName] attributes ensure correct snake_case keys.
    /// </summary>
    private static string SerializeAccount(Account account)
    {
        return JsonSerializer.Serialize(account);
    }

    #endregion

    // =========================================================================
    // Phase 2: CreateAccount Tests (POST /v1/accounts)
    // Replaces: POST endpoint in WebApiController.cs + AccountHook.OnPostCreateRecord
    // =========================================================================

    /// <summary>
    /// Verifies that a valid POST request creates an account, returns 201 Created
    /// with Location header, invokes repository CreateAsync, triggers search index
    /// regeneration, and publishes crm.account.created SNS event.
    /// </summary>
    [Fact]
    public async Task CreateAccount_ValidRequest_Returns201Created()
    {
        // Arrange
        Account? capturedAccount = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Account, CancellationToken>((_, a, _) => capturedAccount = a)
            .Returns(Task.CompletedTask);

        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 201 Created with Location header
        response.StatusCode.Should().Be((int)HttpStatusCode.Created);
        response.Headers.Should().ContainKey("Location");
        response.Headers["Location"].Should().StartWith("/v1/accounts/");
        response.Headers.Should().ContainKey("x-correlation-id");

        // Assert — response body contains created Account with non-empty Id
        response.Body.Should().NotBeNullOrEmpty();
        var returnedAccount = JsonSerializer.Deserialize<Account>(response.Body);
        returnedAccount.Should().NotBeNull();
        returnedAccount!.Id.Should().NotBe(Guid.Empty);
        returnedAccount.Name.Should().Be("Test Corp");
        returnedAccount.Type.Should().Be(AccountType.Company);

        // Verify — repository CreateAsync called once with entity "account"
        _mockCrmRepository.Verify(
            x => x.CreateAsync(
                "account",
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — search service called (replaces AccountHook.OnPostCreateRecord)
        // AccountSearchIndexFields has 17 fields (from Configuration.cs lines 9-11)
        _mockSearchService.Verify(
            x => x.RegenSearchFieldAsync(
                "account",
                It.IsAny<Guid>(),
                It.Is<List<string>>(l => l.Count == 17),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — SNS event crm.account.created published
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestSnsTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify captured account had non-empty Id assigned
        capturedAccount.Should().NotBeNull();
        capturedAccount!.Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// Verifies that POST with missing required Name field returns 400 Bad Request
    /// and the repository CreateAsync is never called.
    /// Validation rule: "name is required." (AccountHandler.ValidateAccountFields)
    /// </summary>
    [Fact]
    public async Task CreateAccount_MissingName_Returns400BadRequest()
    {
        // Arrange — valid type/first_name/last_name but missing name
        var requestBody = """{"type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 400 Bad Request with error message about name
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        response.Body.Should().NotBeNullOrEmpty();
        response.Body.Should().Contain("name is required");

        // Verify — repository CreateAsync should NOT have been called
        _mockCrmRepository.Verify(
            x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that POST with invalid account Type value returns 400 Bad Request.
    /// Only "1" (Company) and "2" (Person) are valid per NextPlugin.20190204.cs lines 31-35.
    /// Validation rule: "type must be '1' (Company) or '2' (Person)."
    /// </summary>
    [Fact]
    public async Task CreateAccount_InvalidType_Returns400BadRequest()
    {
        // Arrange — Type="3" is invalid
        var requestBody = """{"name":"Test Corp","type":"3","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 400 Bad Request with error message about type
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        response.Body.Should().NotBeNullOrEmpty();
        response.Body.Should().Contain("type must be");

        // Verify — repository NOT called
        _mockCrmRepository.Verify(
            x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that POST with missing required FirstName field returns 400 Bad Request.
    /// Required=true per source NextPlugin.20190204.cs line 334.
    /// Validation rule: "first_name is required."
    /// </summary>
    [Fact]
    public async Task CreateAccount_MissingFirstName_Returns400BadRequest()
    {
        // Arrange — valid name/type/last_name but missing first_name
        var requestBody = """{"name":"Test Corp","type":"1","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 400 Bad Request
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        response.Body.Should().NotBeNullOrEmpty();
        response.Body.Should().Contain("first_name is required");

        // Verify — repository NOT called
        _mockCrmRepository.Verify(
            x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that POST with missing required LastName field returns 400 Bad Request.
    /// Required=true per source NextPlugin.20190204.cs line 304.
    /// Validation rule: "last_name is required."
    /// </summary>
    [Fact]
    public async Task CreateAccount_MissingLastName_Returns400BadRequest()
    {
        // Arrange — valid name/type/first_name but missing last_name
        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 400 Bad Request
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        response.Body.Should().NotBeNullOrEmpty();
        response.Body.Should().Contain("last_name is required");

        // Verify — repository NOT called
        _mockCrmRepository.Verify(
            x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that when SalutationId is Guid.Empty (not provided), the handler
    /// sets it to the default value 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698.
    /// Source: NextPlugin.20190206.cs line 131 — DefaultValue.
    /// </summary>
    [Fact]
    public async Task CreateAccount_SetsDefaultSalutationId_WhenNotProvided()
    {
        // Arrange — send JSON with salutation_id explicitly set to Guid.Empty
        Account? capturedAccount = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Account, CancellationToken>((_, a, _) => capturedAccount = a)
            .Returns(Task.CompletedTask);

        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe","salutation_id":"00000000-0000-0000-0000-000000000000"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — handler should set default salutation 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698
        capturedAccount.Should().NotBeNull();
        capturedAccount!.SalutationId.Should().Be(Account.DefaultSalutationId);
        capturedAccount.SalutationId.Should().Be(DefaultSalutationGuid);
    }

    /// <summary>
    /// Verifies that CreatedOn is set to approximately DateTime.UtcNow when the
    /// client does not provide it in the request body.
    /// Source: NextPlugin.20190206.cs line 102 — UseCurrentTimeAsDefaultValue = true.
    /// </summary>
    [Fact]
    public async Task CreateAccount_SetsCreatedOn_WhenNotProvided()
    {
        // Arrange
        var beforeTest = DateTime.UtcNow;
        Account? capturedAccount = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Account, CancellationToken>((_, a, _) => capturedAccount = a)
            .Returns(Task.CompletedTask);

        // JSON without created_on — Account constructor sets DateTime.UtcNow
        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — CreatedOn should be approximately DateTime.UtcNow
        capturedAccount.Should().NotBeNull();
        capturedAccount!.CreatedOn.Should().BeCloseTo(beforeTest, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Verifies that a new non-empty GUID is generated when the account's Id is
    /// Guid.Empty (default/not provided). AccountHandler lines 398-402.
    /// </summary>
    [Fact]
    public async Task CreateAccount_GeneratesNewId_WhenIdIsDefault()
    {
        // Arrange
        Account? capturedAccount = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Account, CancellationToken>((_, a, _) => capturedAccount = a)
            .Returns(Task.CompletedTask);

        // JSON without id — Account constructor sets Guid.Empty, handler generates new GUID
        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — Id should be a new non-empty GUID
        capturedAccount.Should().NotBeNull();
        capturedAccount!.Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// Verifies that the response echoes the x-correlation-id header from the request
    /// for distributed tracing per AAP §0.8.5.
    /// </summary>
    [Fact]
    public async Task CreateAccount_IncludesCorrelationId_InResponse()
    {
        // Arrange
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var customCorrelationId = "my-unique-correlation-id-67890";
        var headers = new Dictionary<string, string>
        {
            ["x-correlation-id"] = customCorrelationId
        };
        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody, headers: headers);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — response should echo the correlation-ID
        response.Headers.Should().ContainKey("x-correlation-id");
        response.Headers["x-correlation-id"].Should().Be(customCorrelationId);
    }

    /// <summary>
    /// Verifies that a request with the x-idempotency-key header is accepted and
    /// processed successfully per AAP §0.8.5.
    /// </summary>
    [Fact]
    public async Task CreateAccount_IncludesIdempotencyKey_Check()
    {
        // Arrange
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var headers = new Dictionary<string, string>
        {
            ["x-correlation-id"] = TestCorrelationId,
            ["x-idempotency-key"] = TestIdempotencyKey
        };
        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody, headers: headers);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — request with idempotency key should succeed normally (201)
        response.StatusCode.Should().Be((int)HttpStatusCode.Created);
    }

    // =========================================================================
    // Phase 3: GetAccount Tests (GET /v1/accounts/{id})
    // =========================================================================

    /// <summary>
    /// Verifies that GET with an existing account ID returns 200 OK with the
    /// full account data including SalutationId, and NO SNS events are published.
    /// </summary>
    [Fact]
    public async Task GetAccount_ExistingId_Returns200WithAccount()
    {
        // Arrange
        var testAccount = CreateTestAccount();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                testAccount.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(testAccount);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testAccount.Id.ToString("D")
        };
        var request = BuildRequest("GET", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK with account data
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        response.Body.Should().NotBeNullOrEmpty();

        var returnedAccount = JsonSerializer.Deserialize<Account>(response.Body);
        returnedAccount.Should().NotBeNull();
        returnedAccount!.Id.Should().Be(testAccount.Id);
        returnedAccount.Name.Should().Be(testAccount.Name);
        returnedAccount.Type.Should().Be(testAccount.Type);
        returnedAccount.SalutationId.Should().Be(testAccount.SalutationId);

        // Verify NO SNS events published on read operations
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that GET with a non-existing account ID returns 404 Not Found.
    /// </summary>
    [Fact]
    public async Task GetAccount_NonExistingId_Returns404NotFound()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                nonExistingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = nonExistingId.ToString("D")
        };
        var request = BuildRequest("GET", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies that GET with an invalid GUID path parameter returns 400 Bad Request.
    /// </summary>
    [Fact]
    public async Task GetAccount_InvalidGuid_Returns400BadRequest()
    {
        // Arrange — "not-a-guid" cannot be parsed by Guid.TryParse
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = "not-a-guid"
        };
        var request = BuildRequest("GET", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // Phase 4: UpdateAccount Tests (PUT /v1/accounts/{id})
    // Replaces: PUT endpoint + AccountHook.OnPostUpdateRecord
    // =========================================================================

    /// <summary>
    /// Verifies that a valid PUT request updates an existing account, returns 200 OK,
    /// invokes repository UpdateAsync, triggers search index regeneration, and
    /// publishes crm.account.updated SNS event.
    /// Source: AccountHook.OnPostUpdateRecord (AccountHook.cs line 19) — same search
    /// regeneration as create.
    /// </summary>
    [Fact]
    public async Task UpdateAccount_ValidRequest_Returns200WithUpdatedAccount()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var existingAccount = CreateTestAccount(existingId);

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                existingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingAccount);

        _mockCrmRepository
            .Setup(x => x.UpdateAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var updatedBody = $@"{{""name"":""Updated Corp"",""type"":""1"",""first_name"":""John"",""last_name"":""Doe""}}";
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = existingId.ToString("D")
        };
        var request = BuildRequest("PUT", body: updatedBody, pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        response.Body.Should().NotBeNullOrEmpty();

        // Verify — repository UpdateAsync called exactly once
        _mockCrmRepository.Verify(
            x => x.UpdateAsync(
                "account",
                existingId,
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — search service called (replaces AccountHook.OnPostUpdateRecord)
        _mockSearchService.Verify(
            x => x.RegenSearchFieldAsync(
                "account",
                It.IsAny<Guid>(),
                It.Is<List<string>>(l => l.Count == 17),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — SNS event crm.account.updated published
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestSnsTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that PUT with a non-existing account ID returns 404 Not Found
    /// and UpdateAsync is never called.
    /// </summary>
    [Fact]
    public async Task UpdateAccount_NonExistingId_Returns404NotFound()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                nonExistingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var requestBody = """{"name":"Updated Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = nonExistingId.ToString("D")
        };
        var request = BuildRequest("PUT", body: requestBody, pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);

        // Verify UpdateAsync was NOT called
        _mockCrmRepository.Verify(
            x => x.UpdateAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that PUT with an invalid/empty body returns 400 Bad Request.
    /// </summary>
    [Fact]
    public async Task UpdateAccount_InvalidBody_Returns400BadRequest()
    {
        // Arrange — empty body triggers 400 before any repository call
        var existingId = Guid.NewGuid();
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = existingId.ToString("D")
        };
        var request = BuildRequest("PUT", body: "", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // Phase 5: DeleteAccount Tests (DELETE /v1/accounts/{id})
    // =========================================================================

    /// <summary>
    /// Verifies that DELETE with an existing account ID returns 204 No Content,
    /// invokes repository DeleteAsync, and publishes crm.account.deleted SNS event.
    /// NOTE: Source AccountHook did NOT implement IErpPostDeleteRecordHook, but the
    /// AAP requires domain events on all CRUD operations.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_ExistingId_Returns204NoContent()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var existingAccount = CreateTestAccount(existingId);

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                existingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingAccount);

        _mockCrmRepository
            .Setup(x => x.DeleteAsync(
                "account",
                existingId,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = existingId.ToString("D")
        };
        var request = BuildRequest("DELETE", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 204 No Content
        response.StatusCode.Should().Be((int)HttpStatusCode.NoContent);

        // Verify — repository DeleteAsync called exactly once
        _mockCrmRepository.Verify(
            x => x.DeleteAsync(
                "account",
                existingId,
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — SNS event crm.account.deleted published
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestSnsTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that DELETE with a non-existing account ID returns 404 Not Found
    /// and DeleteAsync is never called.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_NonExistingId_Returns404NotFound()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                nonExistingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = nonExistingId.ToString("D")
        };
        var request = BuildRequest("DELETE", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);

        // Verify DeleteAsync was NOT called
        _mockCrmRepository.Verify(
            x => x.DeleteAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    // =========================================================================
    // Phase 6: ListAccounts Tests (GET /v1/accounts)
    // =========================================================================

    /// <summary>
    /// Verifies that GET /v1/accounts with default pagination returns 200 OK with
    /// paginated response containing data array and meta object (page/pageSize/total).
    /// </summary>
    [Fact]
    public async Task ListAccounts_DefaultPagination_Returns200WithList()
    {
        // Arrange — return 2 test accounts
        var accounts = new List<Account>
        {
            CreateTestAccount(),
            CreateTestAccount()
        };

        _mockCrmRepository
            .Setup(x => x.QueryAsync<Account>(
                "account",
                It.IsAny<QueryFilter?>(),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(accounts);

        _mockCrmRepository
            .Setup(x => x.CountAsync(
                "account",
                It.IsAny<QueryFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2L);

        // GET request with NO query parameters (defaults: page=1, pageSize=20)
        var request = BuildRequest("GET");

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        response.Body.Should().NotBeNullOrEmpty();

        // Parse the paginated envelope response
        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        root.TryGetProperty("data", out var dataElement).Should().BeTrue();
        dataElement.GetArrayLength().Should().Be(2);

        root.TryGetProperty("meta", out var metaElement).Should().BeTrue();
        metaElement.GetProperty("page").GetInt32().Should().Be(1);
        metaElement.GetProperty("pageSize").GetInt32().Should().Be(20);
        metaElement.GetProperty("total").GetInt64().Should().Be(2);
    }

    /// <summary>
    /// Verifies that GET /v1/accounts?search=test uses SearchAsync (not QueryAsync)
    /// for full-text search. Replaces SearchService.RegenSearchField search index usage.
    /// </summary>
    [Fact]
    public async Task ListAccounts_WithSearch_UsesSearchAsync()
    {
        // Arrange
        var accounts = new List<Account> { CreateTestAccount() };

        _mockCrmRepository
            .Setup(x => x.SearchAsync<Account>(
                "account",
                "test",
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(accounts);

        var queryParams = new Dictionary<string, string>
        {
            ["search"] = "test"
        };
        var request = BuildRequest("GET", queryStringParameters: queryParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);

        // Verify SearchAsync was called (NOT QueryAsync)
        _mockCrmRepository.Verify(
            x => x.SearchAsync<Account>(
                "account",
                "test",
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that GET /v1/accounts?type=1 filters results by account type.
    /// Type "1" = Company per NextPlugin.20190204.cs lines 31-35.
    /// </summary>
    [Fact]
    public async Task ListAccounts_WithTypeFilter_FiltersResults()
    {
        // Arrange
        var accounts = new List<Account> { CreateTestAccount() };

        _mockCrmRepository
            .Setup(x => x.QueryAsync<Account>(
                "account",
                It.IsAny<QueryFilter?>(),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(accounts);

        _mockCrmRepository
            .Setup(x => x.CountAsync(
                "account",
                It.IsAny<QueryFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var queryParams = new Dictionary<string, string>
        {
            ["type"] = "1"
        };
        var request = BuildRequest("GET", queryStringParameters: queryParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);

        // Verify QueryAsync was called with a filter (not SearchAsync)
        _mockCrmRepository.Verify(
            x => x.QueryAsync<Account>(
                "account",
                It.Is<QueryFilter?>(f => f != null),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    // =========================================================================
    // Phase 7: SNS Event Schema Validation
    // Event naming convention: crm.account.{created|updated|deleted} (AAP §0.8.5)
    // =========================================================================

    /// <summary>
    /// Verifies that the SNS PublishAsync is called with correct JSON structure
    /// containing eventType, entityName, recordId, correlationId, and timestamp.
    /// MessageAttributes include eventType and entityName for SNS filtering.
    /// </summary>
    [Fact]
    public async Task CreateAccount_PublishesSnsEvent_WithCorrectFormat()
    {
        // Arrange
        PublishRequest? capturedRequest = null;
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — SNS publish request structure
        capturedRequest.Should().NotBeNull();
        capturedRequest!.TopicArn.Should().Be(TestSnsTopicArn);

        // Parse SNS message body as JSON
        capturedRequest.Message.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(capturedRequest.Message);
        var root = doc.RootElement;

        root.TryGetProperty("eventType", out var eventType).Should().BeTrue();
        eventType.GetString().Should().Be("crm.account.created");

        root.TryGetProperty("entityName", out var entityName).Should().BeTrue();
        entityName.GetString().Should().Be("account");

        root.TryGetProperty("recordId", out var recordId).Should().BeTrue();
        recordId.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("correlationId", out var correlationId).Should().BeTrue();
        correlationId.GetString().Should().NotBeNullOrEmpty();

        root.TryGetProperty("timestamp", out var timestamp).Should().BeTrue();
        timestamp.GetString().Should().NotBeNullOrEmpty();

        // Verify message attributes for SNS topic filtering
        capturedRequest.MessageAttributes.Should().ContainKey("eventType");
        capturedRequest.MessageAttributes["eventType"].StringValue.Should().Be("crm.account.created");
        capturedRequest.MessageAttributes.Should().ContainKey("entityName");
        capturedRequest.MessageAttributes["entityName"].StringValue.Should().Be("account");
    }

    /// <summary>
    /// Verifies that the SNS event for account update has eventType = crm.account.updated.
    /// Same JSON structure as create, different eventType value.
    /// </summary>
    [Fact]
    public async Task UpdateAccount_PublishesSnsEvent_WithCorrectFormat()
    {
        // Arrange
        PublishRequest? capturedRequest = null;
        var existingId = Guid.NewGuid();
        var existingAccount = CreateTestAccount(existingId);

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                existingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingAccount);

        _mockCrmRepository
            .Setup(x => x.UpdateAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

        var requestBody = """{"name":"Updated Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = existingId.ToString("D")
        };
        var request = BuildRequest("PUT", body: requestBody, pathParameters: pathParams);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — SNS message eventType is crm.account.updated
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Message.Should().NotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(capturedRequest.Message);
        var root = doc.RootElement;
        root.GetProperty("eventType").GetString().Should().Be("crm.account.updated");
        root.GetProperty("entityName").GetString().Should().Be("account");

        capturedRequest.MessageAttributes["eventType"].StringValue.Should().Be("crm.account.updated");
    }

    /// <summary>
    /// Verifies that the SNS event for account deletion has eventType = crm.account.deleted.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_PublishesSnsEvent_WithCorrectFormat()
    {
        // Arrange
        PublishRequest? capturedRequest = null;
        var existingId = Guid.NewGuid();
        var existingAccount = CreateTestAccount(existingId);

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Account>(
                "account",
                existingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingAccount);

        _mockCrmRepository
            .Setup(x => x.DeleteAsync(
                "account",
                existingId,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = existingId.ToString("D")
        };
        var request = BuildRequest("DELETE", pathParameters: pathParams);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — SNS message eventType is crm.account.deleted
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Message.Should().NotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(capturedRequest.Message);
        var root = doc.RootElement;
        root.GetProperty("eventType").GetString().Should().Be("crm.account.deleted");
        root.GetProperty("entityName").GetString().Should().Be("account");

        capturedRequest.MessageAttributes["eventType"].StringValue.Should().Be("crm.account.deleted");
    }

    /// <summary>
    /// Verifies that if the SNS client throws an exception during PublishAsync,
    /// the API response is still successful (fire-and-forget pattern per AAP §0.7.2).
    /// Post-hooks must never fail the primary API operation.
    /// </summary>
    [Fact]
    public async Task SnsPublishFailure_DoesNotFailApiResponse()
    {
        // Arrange — SNS throws on publish
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleNotificationServiceException("SNS failure"));

        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Account>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var requestBody = """{"name":"Test Corp","type":"1","first_name":"John","last_name":"Doe"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — API should still return 201 Created despite SNS failure
        response.StatusCode.Should().Be((int)HttpStatusCode.Created);
    }

    // =========================================================================
    // Phase 8: HTTP Method Routing Tests
    // =========================================================================

    /// <summary>
    /// Verifies that an unsupported HTTP method (PATCH) returns 405 Method Not Allowed.
    /// AccountHandler only supports GET, POST, PUT, DELETE.
    /// </summary>
    [Fact]
    public async Task Handle_UnsupportedMethod_Returns405MethodNotAllowed()
    {
        // Arrange — PATCH is not supported by AccountHandler
        var request = BuildRequest("PATCH", body: """{"name":"Test"}""");

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 405 Method Not Allowed
        response.StatusCode.Should().Be((int)HttpStatusCode.MethodNotAllowed);
    }

    // =========================================================================
    // IDisposable Implementation
    // =========================================================================

    /// <summary>
    /// Disposes the <see cref="ServiceProvider"/> created during test setup to
    /// release any held resources and prevent memory leaks across test runs.
    /// </summary>
    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }
}
