// =============================================================================
// ContactHandlerTests.cs — CRM ContactHandler Lambda Unit Tests
//
// Comprehensive unit tests for the CRM ContactHandler Lambda handler covering
// all CRUD operations (Create, Read, Update, Delete, List, Search), SNS domain
// event publishing, correlation-ID propagation, idempotency key handling,
// salutation association with corrected field naming, and HTTP method routing.
//
// Source mapping:
//   ContactHook.cs       → Verifies post-create/update search regen + SNS events
//   NextPlugin.20190204  → Contact entity ID (39e1dd9b-...), entity creation
//   NextPlugin.20190206  → Default salutation (87c08ee1-...), corrected field name
//   Configuration.cs     → ContactSearchIndexFields (15 fields)
//   SearchService.cs     → RegenSearchFieldAsync verification
//
// Test framework: xUnit (AAP §0.6.1)
// Assertions: FluentAssertions (AAP §0.6.1)
// Mocking: Moq (AAP §0.6.1)
// JSON: System.Text.Json only (NOT Newtonsoft.Json, per AAP .NET 9 Native AOT)
// =============================================================================

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
/// Unit tests for <see cref="ContactHandler"/> Lambda handler in the CRM
/// bounded-context microservice. Validates contact CRUD Lambda handler behavior
/// including salutation association, SNS event publishing, search index
/// regeneration, correlation-ID propagation, and HTTP method routing.
///
/// Each test constructs an <see cref="APIGatewayHttpApiV2ProxyRequest"/> with
/// appropriate HTTP method, path parameters, query parameters, headers, and body,
/// then invokes <see cref="ContactHandler.HandleAsync"/> and asserts the
/// <see cref="APIGatewayHttpApiV2ProxyResponse"/> status code, headers, and body.
///
/// All AWS SDK interactions (DynamoDB via ICrmRepository, SNS) are mocked with Moq.
/// No real AWS SDK calls are made in these unit tests.
/// </summary>
public class ContactHandlerTests : IDisposable
{
    #region Test Fixtures and Constants

    // Mocked dependencies injected into ContactHandler via IServiceProvider constructor
    private readonly Mock<ICrmRepository> _mockCrmRepository;
    private readonly Mock<ISearchService> _mockSearchService;
    private readonly Mock<IAmazonSimpleNotificationService> _mockSnsClient;
    private readonly Mock<ILogger<ContactHandler>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;

    // System Under Test
    private readonly ContactHandler _handler;

    // Lambda execution context for all test invocations
    private readonly ILambdaContext _lambdaContext;

    // Service provider used for handler construction (kept for disposal)
    private readonly ServiceProvider _serviceProvider;

    /// <summary>Test SNS topic ARN matching LocalStack format.</summary>
    private const string TestSnsTopicArn = "arn:aws:sns:us-east-1:000000000000:crm-events";

    /// <summary>Stable correlation ID for tests that verify propagation.</summary>
    private const string TestCorrelationId = "test-correlation-id-12345";

    /// <summary>Stable idempotency key for idempotency header tests.</summary>
    private const string TestIdempotencyKey = "idem-key-98765";

    #endregion

    #region Constructor (Test Setup)

    /// <summary>
    /// Initializes all mocked dependencies and constructs the ContactHandler
    /// via its <c>ContactHandler(IServiceProvider)</c> constructor for unit testing.
    /// </summary>
    public ContactHandlerTests()
    {
        // Create mock instances for all ContactHandler constructor dependencies
        _mockCrmRepository = new Mock<ICrmRepository>();
        _mockSearchService = new Mock<ISearchService>();
        _mockSnsClient = new Mock<IAmazonSimpleNotificationService>();
        _mockLogger = new Mock<ILogger<ContactHandler>>();
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

        // Build service provider and construct handler
        _serviceProvider = services.BuildServiceProvider();
        _handler = new ContactHandler(_serviceProvider);

        // Create TestLambdaContext (from Amazon.Lambda.TestUtilities)
        _lambdaContext = new TestLambdaContext
        {
            FunctionName = "ContactHandlerTest",
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
                        ? $"/v1/contacts/{pathParameters["id"]}"
                        : "/v1/contacts"
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
    /// Creates a fully populated test <see cref="Contact"/> with known values.
    /// </summary>
    private static Contact CreateTestContact(Guid? id = null)
    {
        return new Contact
        {
            Id = id ?? Guid.NewGuid(),
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane@example.com",
            JobTitle = "Engineer",
            SalutationId = Contact.DefaultSalutationId,
            CreatedOn = DateTime.UtcNow,
            XSearch = "jane smith engineer"
        };
    }

    /// <summary>
    /// Serializes a <see cref="Contact"/> to JSON using System.Text.Json.
    /// The Contact model's [JsonPropertyName] attributes ensure correct snake_case keys.
    /// </summary>
    private static string SerializeContact(Contact contact)
    {
        return JsonSerializer.Serialize(contact);
    }

    #endregion

    // =========================================================================
    // Phase 2: CreateContact Tests (POST /v1/contacts)
    // Replaces: POST endpoint in WebApiController.cs + ContactHook.OnPostCreateRecord
    // =========================================================================

    /// <summary>
    /// Verifies that a valid POST request creates a contact, returns 201 Created
    /// with Location header, invokes repository CreateAsync, triggers search index
    /// regeneration, and publishes crm.contact.created SNS event.
    /// </summary>
    [Fact]
    public async Task CreateContact_ValidRequest_Returns201Created()
    {
        // Arrange
        Contact? capturedContact = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Contact, CancellationToken>((_, c, _) => capturedContact = c)
            .Returns(Task.CompletedTask);

        var requestBody = """{"first_name":"Jane","last_name":"Smith","email":"jane@example.com"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 201 Created with Location header
        response.StatusCode.Should().Be((int)HttpStatusCode.Created);
        response.Headers.Should().ContainKey("Location");
        response.Headers["Location"].Should().StartWith("/v1/contacts/");
        response.Headers.Should().ContainKey("x-correlation-id");

        // Assert — response body contains created Contact with non-empty Id
        response.Body.Should().NotBeNullOrEmpty();
        var returnedContact = JsonSerializer.Deserialize<Contact>(response.Body);
        returnedContact.Should().NotBeNull();
        returnedContact!.Id.Should().NotBe(Guid.Empty);
        returnedContact.FirstName.Should().Be("Jane");
        returnedContact.LastName.Should().Be("Smith");

        // Verify — repository CreateAsync called once with entity "contact"
        _mockCrmRepository.Verify(
            x => x.CreateAsync(
                "contact",
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — search service called (replaces ContactHook.OnPostCreateRecord)
        // ContactSearchIndexFields has 15 fields (from Configuration.cs lines 17-19)
        _mockSearchService.Verify(
            x => x.RegenSearchFieldAsync(
                "contact",
                It.IsAny<Guid>(),
                It.Is<List<string>>(l => l.Count == 15),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — SNS event crm.contact.created published
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestSnsTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify captured contact had non-empty Id assigned
        capturedContact.Should().NotBeNull();
        capturedContact!.Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// Verifies that when SalutationId is Guid.Empty (not provided), the handler
    /// sets it to the default value 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698.
    /// Source: NextPlugin.20190206.cs line 131 — guidField.DefaultValue.
    /// CRITICAL: Uses corrected field name 'salutation_id' (NOT misspelled 'solutation_id').
    /// </summary>
    [Fact]
    public async Task CreateContact_SetsDefaultSalutationId_WhenNotProvided()
    {
        // Arrange — send JSON with salutation_id explicitly set to Guid.Empty
        Contact? capturedContact = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Contact, CancellationToken>((_, c, _) => capturedContact = c)
            .Returns(Task.CompletedTask);

        var requestBody = """{"first_name":"Jane","last_name":"Smith","salutation_id":"00000000-0000-0000-0000-000000000000"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — handler should set default salutation 87c08ee1-8d4d-4c89-9b37-4e3cc3f98698
        capturedContact.Should().NotBeNull();
        capturedContact!.SalutationId.Should().Be(Contact.DefaultSalutationId);
        capturedContact.SalutationId.Should().Be(
            Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
    }

    /// <summary>
    /// Verifies that CreatedOn is set to approximately DateTime.UtcNow when the
    /// client does not provide it in the request body.
    /// Source: NextPlugin.20190206.cs lines 440-441 — UseCurrentTimeAsDefaultValue = true.
    /// </summary>
    [Fact]
    public async Task CreateContact_SetsCreatedOn_WhenNotProvided()
    {
        // Arrange
        var beforeTest = DateTime.UtcNow;
        Contact? capturedContact = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Contact, CancellationToken>((_, c, _) => capturedContact = c)
            .Returns(Task.CompletedTask);

        // JSON without created_on — Contact constructor sets DateTime.UtcNow
        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — CreatedOn should be approximately DateTime.UtcNow
        capturedContact.Should().NotBeNull();
        capturedContact!.CreatedOn.Should().BeCloseTo(beforeTest, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Verifies that a new non-empty GUID is generated when the contact's Id is
    /// Guid.Empty (default/not provided).
    /// </summary>
    [Fact]
    public async Task CreateContact_GeneratesNewId_WhenIdIsDefault()
    {
        // Arrange
        Contact? capturedContact = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Contact, CancellationToken>((_, c, _) => capturedContact = c)
            .Returns(Task.CompletedTask);

        // JSON without id — Contact constructor sets Guid.Empty, handler generates new GUID
        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — Id should be a new non-empty GUID
        capturedContact.Should().NotBeNull();
        capturedContact!.Id.Should().NotBe(Guid.Empty);
    }

    /// <summary>
    /// Verifies that XSearch is initialized to empty string when not provided.
    /// Source: NextPlugin.20190206.cs line 510 — DefaultValue = "".
    /// </summary>
    [Fact]
    public async Task CreateContact_SetsXSearchDefault_WhenNotProvided()
    {
        // Arrange
        Contact? capturedContact = null;
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Contact, CancellationToken>((_, c, _) => capturedContact = c)
            .Returns(Task.CompletedTask);

        // JSON without x_search — Contact constructor sets string.Empty
        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — XSearch should be empty string, not null
        capturedContact.Should().NotBeNull();
        capturedContact!.XSearch.Should().NotBeNull();
        capturedContact.XSearch.Should().Be(string.Empty);
    }

    /// <summary>
    /// Verifies that the response echoes the x-correlation-id header from the request
    /// for distributed tracing per AAP §0.8.5.
    /// </summary>
    [Fact]
    public async Task CreateContact_IncludesCorrelationId_InResponse()
    {
        // Arrange
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var customCorrelationId = "my-unique-correlation-id-67890";
        var headers = new Dictionary<string, string>
        {
            ["x-correlation-id"] = customCorrelationId
        };
        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
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
    public async Task CreateContact_IncludesIdempotencyKey_Check()
    {
        // Arrange
        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var headers = new Dictionary<string, string>
        {
            ["x-correlation-id"] = TestCorrelationId,
            ["x-idempotency-key"] = TestIdempotencyKey
        };
        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
        var request = BuildRequest("POST", body: requestBody, headers: headers);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — request with idempotency key should succeed normally (201)
        response.StatusCode.Should().Be((int)HttpStatusCode.Created);
    }

    // =========================================================================
    // Phase 3: GetContact Tests (GET /v1/contacts/{id})
    // =========================================================================

    /// <summary>
    /// Verifies that GET with an existing contact ID returns 200 OK with the
    /// full contact data including SalutationId, and NO SNS events are published.
    /// </summary>
    [Fact]
    public async Task GetContact_ExistingId_Returns200WithContact()
    {
        // Arrange
        var testContact = CreateTestContact();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                testContact.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(testContact);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testContact.Id.ToString("D")
        };
        var request = BuildRequest("GET", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK with contact data
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        response.Body.Should().NotBeNullOrEmpty();

        var returnedContact = JsonSerializer.Deserialize<Contact>(response.Body);
        returnedContact.Should().NotBeNull();
        returnedContact!.Id.Should().Be(testContact.Id);
        returnedContact.FirstName.Should().Be(testContact.FirstName);
        returnedContact.LastName.Should().Be(testContact.LastName);
        returnedContact.SalutationId.Should().Be(testContact.SalutationId);

        // Verify NO SNS events published on read operations
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that GET with a non-existing contact ID returns 404 Not Found.
    /// </summary>
    [Fact]
    public async Task GetContact_NonExistingId_Returns404NotFound()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                nonExistingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Contact?)null);

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
    public async Task GetContact_InvalidGuid_Returns400BadRequest()
    {
        // Arrange — "not-a-valid-guid" cannot be parsed as Guid
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = "not-a-valid-guid"
        };
        var request = BuildRequest("GET", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // Phase 4: UpdateContact Tests (PUT /v1/contacts/{id})
    // Replaces: PUT endpoint in WebApiController.cs + ContactHook.OnPostUpdateRecord
    // =========================================================================

    /// <summary>
    /// Verifies that a valid PUT request updates an existing contact, returns
    /// 200 OK, invokes repository UpdateAsync, triggers search index regeneration,
    /// and publishes crm.contact.updated SNS event.
    /// </summary>
    [Fact]
    public async Task UpdateContact_ValidRequest_Returns200WithUpdatedContact()
    {
        // Arrange
        var testContact = CreateTestContact();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                testContact.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(testContact);
        _mockCrmRepository
            .Setup(x => x.UpdateAsync(
                "contact",
                testContact.Id,
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var updatedContact = new Contact
        {
            Id = testContact.Id,
            FirstName = "Janet",
            LastName = "Doe",
            Email = "janet@example.com",
            SalutationId = testContact.SalutationId,
            CreatedOn = testContact.CreatedOn
        };
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testContact.Id.ToString("D")
        };
        var request = BuildRequest("PUT",
            body: SerializeContact(updatedContact),
            pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK with updated contact data
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        response.Body.Should().NotBeNullOrEmpty();

        // Verify — repository UpdateAsync called once
        _mockCrmRepository.Verify(
            x => x.UpdateAsync(
                "contact",
                testContact.Id,
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — search service called (replaces ContactHook.OnPostUpdateRecord)
        _mockSearchService.Verify(
            x => x.RegenSearchFieldAsync(
                "contact",
                testContact.Id,
                It.Is<List<string>>(l => l.Count == 15),
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify — SNS event crm.contact.updated published
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestSnsTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that PUT with a non-existing contact ID returns 404 Not Found
    /// and does NOT call UpdateAsync.
    /// </summary>
    [Fact]
    public async Task UpdateContact_NonExistingId_Returns404NotFound()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                nonExistingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Contact?)null);

        var contact = new Contact
        {
            Id = nonExistingId,
            FirstName = "Jane",
            LastName = "Smith"
        };
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = nonExistingId.ToString("D")
        };
        var request = BuildRequest("PUT",
            body: SerializeContact(contact),
            pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.NotFound);

        // Verify UpdateAsync was NOT called
        _mockCrmRepository.Verify(
            x => x.UpdateAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>
    /// Verifies that when an explicit (non-default) SalutationId is provided in the
    /// update body, it is preserved as-is and NOT overwritten with the default.
    /// </summary>
    [Fact]
    public async Task UpdateContact_PreservsSalutationId_WhenProvided()
    {
        // Arrange — use a custom SalutationId that differs from the default
        var testContact = CreateTestContact();
        var customSalutationId = Guid.NewGuid();

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                testContact.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(testContact);

        Contact? capturedUpdate = null;
        _mockCrmRepository
            .Setup(x => x.UpdateAsync(
                "contact",
                testContact.Id,
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Guid, Contact, CancellationToken>(
                (_, _, c, _) => capturedUpdate = c)
            .Returns(Task.CompletedTask);

        var updateBody = new Contact
        {
            Id = testContact.Id,
            FirstName = "Jane",
            LastName = "Smith",
            SalutationId = customSalutationId,
            CreatedOn = testContact.CreatedOn
        };
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testContact.Id.ToString("D")
        };
        var request = BuildRequest("PUT",
            body: SerializeContact(updateBody),
            pathParameters: pathParams);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — SalutationId should be preserved as-is, not overwritten with default
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.SalutationId.Should().Be(customSalutationId);
    }

    // =========================================================================
    // Phase 5: DeleteContact Tests (DELETE /v1/contacts/{id})
    // NOTE: Source ContactHook did NOT implement IErpPostDeleteRecordHook,
    // but AAP §0.5.1 explicitly requires crm.contact.deleted event.
    // =========================================================================

    /// <summary>
    /// Verifies that DELETE with an existing contact ID returns 204 No Content,
    /// invokes repository DeleteAsync, and publishes crm.contact.deleted SNS event.
    /// </summary>
    [Fact]
    public async Task DeleteContact_ExistingId_Returns204NoContent()
    {
        // Arrange
        var testContact = CreateTestContact();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                testContact.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(testContact);
        _mockCrmRepository
            .Setup(x => x.DeleteAsync(
                "contact",
                testContact.Id,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testContact.Id.ToString("D")
        };
        var request = BuildRequest("DELETE", pathParameters: pathParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 204 No Content
        response.StatusCode.Should().Be((int)HttpStatusCode.NoContent);

        // Verify DeleteAsync was called once
        _mockCrmRepository.Verify(
            x => x.DeleteAsync(
                "contact",
                testContact.Id,
                It.IsAny<CancellationToken>()),
            Times.Once());

        // Verify SNS event crm.contact.deleted published
        _mockSnsClient.Verify(
            x => x.PublishAsync(
                It.Is<PublishRequest>(r => r.TopicArn == TestSnsTopicArn),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that DELETE with a non-existing contact ID returns 404 Not Found
    /// and does NOT call DeleteAsync.
    /// </summary>
    [Fact]
    public async Task DeleteContact_NonExistingId_Returns404NotFound()
    {
        // Arrange
        var nonExistingId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact",
                nonExistingId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Contact?)null);

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
    // Phase 6: ListContacts Tests (GET /v1/contacts)
    // =========================================================================

    /// <summary>
    /// Verifies that GET /v1/contacts with no query params returns 200 OK with
    /// paginated response envelope: { "data": [...], "meta": { "page": 1, "page_size": 20, "total": N } }.
    /// </summary>
    [Fact]
    public async Task ListContacts_DefaultPagination_Returns200WithList()
    {
        // Arrange
        var contacts = new List<Contact>
        {
            CreateTestContact(),
            CreateTestContact()
        };
        _mockCrmRepository
            .Setup(x => x.QueryAsync<Contact>(
                "contact",
                It.IsAny<QueryFilter?>(),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(contacts);
        _mockCrmRepository
            .Setup(x => x.CountAsync(
                "contact",
                It.IsAny<QueryFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        // GET with no path params = list endpoint
        var request = BuildRequest("GET");

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — HTTP 200 OK with paginated envelope
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        response.Body.Should().NotBeNullOrEmpty();

        // Parse the envelope response using JsonDocument (AOT-safe)
        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        root.TryGetProperty("data", out var dataElement).Should().BeTrue();
        dataElement.GetArrayLength().Should().Be(2);

        root.TryGetProperty("meta", out var metaElement).Should().BeTrue();
        metaElement.GetProperty("page").GetInt32().Should().Be(1);
        metaElement.GetProperty("total").GetInt64().Should().Be(2);
    }

    /// <summary>
    /// Verifies that GET /v1/contacts?search=jane uses SearchAsync instead of QueryAsync,
    /// matching the DynamoDB GSI2 x_search field lookup pattern.
    /// </summary>
    [Fact]
    public async Task ListContacts_WithSearch_UsesSearchAsync()
    {
        // Arrange
        var searchResults = new List<Contact> { CreateTestContact() };
        _mockCrmRepository
            .Setup(x => x.SearchAsync<Contact>(
                "contact",
                "jane",
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(searchResults);

        var queryParams = new Dictionary<string, string> { ["search"] = "jane" };
        var request = BuildRequest("GET", queryStringParameters: queryParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — SearchAsync was called, not QueryAsync
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        _mockCrmRepository.Verify(
            x => x.SearchAsync<Contact>(
                "contact",
                "jane",
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that GET /v1/contacts?accountId={guid} passes a filter for
    /// the account_nn_contact relation (account_id field) to QueryAsync.
    /// </summary>
    [Fact]
    public async Task ListContacts_FilterByAccountId_FiltersResults()
    {
        // Arrange
        var accountId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.QueryAsync<Contact>(
                "contact",
                It.IsAny<QueryFilter?>(),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Contact>());
        _mockCrmRepository
            .Setup(x => x.CountAsync(
                "contact",
                It.IsAny<QueryFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var queryParams = new Dictionary<string, string>
        {
            ["accountId"] = accountId.ToString("D")
        };
        var request = BuildRequest("GET", queryStringParameters: queryParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);

        // Verify QueryAsync was called with a non-null filter containing account_id
        _mockCrmRepository.Verify(
            x => x.QueryAsync<Contact>(
                "contact",
                It.Is<QueryFilter?>(f => f != null && f.FieldName == "account_id"),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    /// <summary>
    /// Verifies that GET /v1/contacts?salutationId={guid} passes a filter for
    /// the salutation_id field to QueryAsync.
    /// </summary>
    [Fact]
    public async Task ListContacts_FilterBySalutationId_FiltersResults()
    {
        // Arrange
        var salutationId = Guid.NewGuid();
        _mockCrmRepository
            .Setup(x => x.QueryAsync<Contact>(
                "contact",
                It.IsAny<QueryFilter?>(),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Contact>());
        _mockCrmRepository
            .Setup(x => x.CountAsync(
                "contact",
                It.IsAny<QueryFilter?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var queryParams = new Dictionary<string, string>
        {
            ["salutationId"] = salutationId.ToString("D")
        };
        var request = BuildRequest("GET", queryStringParameters: queryParams);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.OK);

        // Verify QueryAsync was called with a filter for salutation_id
        _mockCrmRepository.Verify(
            x => x.QueryAsync<Contact>(
                "contact",
                It.Is<QueryFilter?>(f => f != null && f.FieldName == "salutation_id"),
                It.IsAny<SortOptions?>(),
                It.IsAny<PaginationOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once());
    }

    // =========================================================================
    // Phase 7: SNS Event Schema Validation
    // Event naming convention: {domain}.{entity}.{action} (AAP §0.8.5)
    // =========================================================================

    /// <summary>
    /// Verifies that crm.contact.created SNS event has the correct message format
    /// with eventType, entityName, recordId, correlationId, timestamp fields in the
    /// JSON message body, and correct MessageAttributes for SNS filtering.
    /// </summary>
    [Fact]
    public async Task CreateContact_PublishesSnsEvent_WithCorrectFormat()
    {
        // Arrange
        PublishRequest? capturedRequest = null;
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>(
                (r, _) => capturedRequest = r)
            .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — SNS message format validation
        capturedRequest.Should().NotBeNull();
        capturedRequest!.TopicArn.Should().Be(TestSnsTopicArn);

        // Parse message body JSON
        using var doc = JsonDocument.Parse(capturedRequest.Message);
        var root = doc.RootElement;
        root.GetProperty("eventType").GetString().Should().Be("crm.contact.created");
        root.GetProperty("entityName").GetString().Should().Be("contact");
        root.GetProperty("recordId").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("timestamp").GetString().Should().NotBeNullOrEmpty();

        // Verify MessageAttributes for SNS subscription filtering
        capturedRequest.MessageAttributes.Should().ContainKey("eventType");
        capturedRequest.MessageAttributes["eventType"].StringValue
            .Should().Be("crm.contact.created");
        capturedRequest.MessageAttributes["eventType"].DataType
            .Should().Be("String");
        capturedRequest.MessageAttributes.Should().ContainKey("entityName");
        capturedRequest.MessageAttributes["entityName"].StringValue
            .Should().Be("contact");
        capturedRequest.MessageAttributes.Should().ContainKey("correlationId");
    }

    /// <summary>
    /// Verifies crm.contact.updated SNS event format on PUT operations.
    /// </summary>
    [Fact]
    public async Task UpdateContact_PublishesSnsEvent_WithCorrectFormat()
    {
        // Arrange
        var testContact = CreateTestContact();
        PublishRequest? capturedRequest = null;
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>(
                (r, _) => capturedRequest = r)
            .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact", testContact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(testContact);
        _mockCrmRepository
            .Setup(x => x.UpdateAsync(
                "contact", testContact.Id, It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var updateBody = new Contact
        {
            Id = testContact.Id,
            FirstName = "Janet",
            LastName = "Doe",
            SalutationId = testContact.SalutationId,
            CreatedOn = testContact.CreatedOn
        };
        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testContact.Id.ToString("D")
        };
        var request = BuildRequest("PUT",
            body: SerializeContact(updateBody),
            pathParameters: pathParams);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — eventType = crm.contact.updated
        capturedRequest.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedRequest!.Message);
        doc.RootElement.GetProperty("eventType").GetString()
            .Should().Be("crm.contact.updated");
        doc.RootElement.GetProperty("entityName").GetString()
            .Should().Be("contact");
        capturedRequest.MessageAttributes["eventType"].StringValue
            .Should().Be("crm.contact.updated");
    }

    /// <summary>
    /// Verifies crm.contact.deleted SNS event format on DELETE operations.
    /// NOTE: Source ContactHook did NOT implement IErpPostDeleteRecordHook, but
    /// AAP §0.5.1 explicitly requires this event.
    /// </summary>
    [Fact]
    public async Task DeleteContact_PublishesSnsEvent_WithCorrectFormat()
    {
        // Arrange
        var testContact = CreateTestContact();
        PublishRequest? capturedRequest = null;
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<PublishRequest, CancellationToken>(
                (r, _) => capturedRequest = r)
            .ReturnsAsync(new PublishResponse { MessageId = "test-msg-id" });

        _mockCrmRepository
            .Setup(x => x.GetByIdAsync<Contact>(
                "contact", testContact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(testContact);
        _mockCrmRepository
            .Setup(x => x.DeleteAsync(
                "contact", testContact.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pathParams = new Dictionary<string, string>
        {
            ["id"] = testContact.Id.ToString("D")
        };
        var request = BuildRequest("DELETE", pathParameters: pathParams);

        // Act
        await _handler.HandleAsync(request, _lambdaContext);

        // Assert — eventType = crm.contact.deleted
        capturedRequest.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedRequest!.Message);
        doc.RootElement.GetProperty("eventType").GetString()
            .Should().Be("crm.contact.deleted");
        doc.RootElement.GetProperty("entityName").GetString()
            .Should().Be("contact");
        capturedRequest.MessageAttributes["eventType"].StringValue
            .Should().Be("crm.contact.deleted");
    }

    /// <summary>
    /// Verifies that SNS publish failures do NOT fail the API response — the handler
    /// catches exceptions from PublishDomainEventAsync and logs them as warnings,
    /// implementing fire-and-forget behavior per AAP §0.7.2.
    /// </summary>
    [Fact]
    public async Task SnsPublishFailure_DoesNotFailApiResponse()
    {
        // Arrange — SNS client throws on PublishAsync
        _mockSnsClient
            .Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SNS publish failed"));

        _mockCrmRepository
            .Setup(x => x.CreateAsync(
                It.IsAny<string>(),
                It.IsAny<Contact>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var requestBody = """{"first_name":"Jane","last_name":"Smith"}""";
        var request = BuildRequest("POST", body: requestBody);

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert — response should STILL succeed despite SNS failure
        response.StatusCode.Should().Be((int)HttpStatusCode.Created);
    }

    // =========================================================================
    // Phase 8: HTTP Method Routing
    // =========================================================================

    /// <summary>
    /// Verifies that an unsupported HTTP method (PATCH) returns 405 Method Not Allowed.
    /// Handler routing uses pattern matching on HTTP method string.
    /// </summary>
    [Fact]
    public async Task Handle_UnsupportedMethod_Returns405()
    {
        // Arrange — PATCH is not a supported method for ContactHandler
        var request = BuildRequest("PATCH");

        // Act
        var response = await _handler.HandleAsync(request, _lambdaContext);

        // Assert
        response.StatusCode.Should().Be((int)HttpStatusCode.MethodNotAllowed);
    }

    // =========================================================================
    // Phase 9: Salutation-Specific Tests
    // Source: NextPlugin.20190206.cs deleted misspelled 'solutation_id'
    //         (lines 45-53) and created 'salutation_id' (lines 519-547).
    // =========================================================================

    /// <summary>
    /// Verifies that the Contact model uses the corrected field name 'salutation_id'
    /// and NOT the misspelled 'solutation_id' from NextPlugin.20190204.cs.
    /// The field was renamed in NextPlugin.20190206.cs.
    /// </summary>
    [Fact]
    public void CreateContact_UsesCorrectSalutationFieldName()
    {
        // Arrange — serialize a Contact to inspect JSON property names
        var contact = new Contact
        {
            FirstName = "Test",
            LastName = "User",
            SalutationId = Contact.DefaultSalutationId
        };
        var json = JsonSerializer.Serialize(contact);

        // Assert — JSON contains corrected spelling, not misspelled version
        json.Should().Contain("\"salutation_id\"");
        json.Should().NotContain("\"solutation_id\"");
    }

    /// <summary>
    /// Verifies Contact.EntityId matches the canonical GUID from
    /// NextPlugin.20190204.cs line 1408: 39e1dd9b-827f-464d-95ea-507ade81cbd0.
    /// Also verifies ContactHandler.ContactEntityId and DefaultSalutationId constants.
    /// </summary>
    [Fact]
    public void ContactEntityId_IsCorrectGuid()
    {
        // Assert — Contact entity GUID from NextPlugin.20190204.cs
        Contact.EntityId.Should().Be(
            Guid.Parse("39e1dd9b-827f-464d-95ea-507ade81cbd0"));

        // Assert — ContactHandler constants derive from Contact model
        ContactHandler.ContactEntityId.Should().Be(Contact.EntityId);
        ContactHandler.DefaultSalutationId.Should().Be(Contact.DefaultSalutationId);
        ContactHandler.DefaultSalutationId.Should().Be(
            Guid.Parse("87c08ee1-8d4d-4c89-9b37-4e3cc3f98698"));
    }

    // =========================================================================
    // Dispose — IDisposable implementation
    // =========================================================================

    /// <summary>
    /// Disposes the service provider created for handler construction.
    /// </summary>
    public void Dispose()
    {
        _serviceProvider.Dispose();
    }
}
