using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Exceptions;
using WebVella.Erp.Service.Reporting;
using WebVella.Erp.Service.Reporting.Database;
using WebVella.Erp.Service.Reporting.Domain.Services;

namespace WebVella.Erp.Tests.Reporting.Controllers
{
    /// <summary>
    /// Integration tests for the Reporting microservice's ReportController REST API endpoints.
    /// Validates all REST endpoints, response envelope shapes, error handling, validation behavior,
    /// and authorization using WebApplicationFactory to host the full ASP.NET Core pipeline in-memory.
    /// Business rules are derived from the source monolith ReportService.cs (lines 13-135) and
    /// API contract patterns from ApiControllerBase.cs.
    /// </summary>
    public class ReportControllerTests : IClassFixture<WebApplicationFactory<Program>>
    {
        #region Constants and Test Identifiers

        /// <summary>
        /// JWT signing key used for test token generation. Overrides the ${JWT_SIGNING_KEY}
        /// environment variable placeholder in appsettings.json.
        /// </summary>
        private const string TestJwtKey = "ThisIsMySecretKeyThisIsMySecretKeyThisIsMySecretKey";
        private const string TestJwtIssuer = "webvella-erp";
        private const string TestJwtAudience = "webvella-erp";

        /// <summary>Well-known test user identifier embedded in JWT claims.</summary>
        private static readonly Guid TestUserId = new Guid("12345678-1234-1234-1234-123456789012");

        /// <summary>Test task identifier for mock timelog report data.</summary>
        private static readonly Guid TestTaskId = new Guid("aaaaaaaa-1111-2222-3333-444444444444");

        /// <summary>Test project identifier for mock timelog report data.</summary>
        private static readonly Guid TestProjectId = new Guid("bbbbbbbb-1111-2222-3333-444444444444");

        /// <summary>Valid test account identifier that returns filtered data.</summary>
        private static readonly Guid TestAccountId = new Guid("cccccccc-1111-2222-3333-444444444444");

        /// <summary>
        /// Non-existent account identifier that triggers the "Account not found" validation error.
        /// Mirrors ReportService.cs line 29: valEx.AddError("accountId", $"Account with ID:{accountId} not found.");
        /// </summary>
        private static readonly Guid NonExistentAccountId = new Guid("dddddddd-1111-2222-3333-444444444444");

        /// <summary>
        /// Well-known monthly timelog report definition identifier from ReportController.cs.
        /// Must match the controller's static MonthlyTimelogReportId field.
        /// </summary>
        private static readonly Guid MonthlyTimelogReportId = new Guid("a0d5e2f1-b3c4-4d6e-8f7a-9b0c1d2e3f4a");

        #endregion

        #region Fields

        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the test class with a configured WebApplicationFactory.
        /// Overrides JWT settings, replaces the database context with InMemory provider,
        /// removes background hosted services (MassTransit bus), and replaces
        /// ReportAggregationService with a Moq mock that replicates source validation logic.
        /// </summary>
        public ReportControllerTests(WebApplicationFactory<Program> factory)
        {
            _factory = factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    // Override JWT configuration with known test values
                    // The real appsettings.json uses ${JWT_SIGNING_KEY} placeholder
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Jwt:Key"] = TestJwtKey,
                        ["Jwt:Issuer"] = TestJwtIssuer,
                        ["Jwt:Audience"] = TestJwtAudience,
                        // Override connection strings to prevent real DB connections
                        ["ConnectionStrings:Default"] = "Host=localhost;Port=5432;Database=test_erp_reporting;Username=test;Password=test",
                        // Override Redis to prevent connection attempts
                        ["Redis:ConnectionString"] = "localhost:59999",
                        ["Redis:InstanceName"] = "Test_",
                        // Override messaging to prevent RabbitMQ/SQS connection attempts
                        ["Messaging:Transport"] = "RabbitMQ",
                        ["Messaging:RabbitMQ:Host"] = "localhost",
                        ["Messaging:RabbitMQ:Username"] = "guest",
                        ["Messaging:RabbitMQ:Password"] = "guest"
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Remove all hosted services to prevent MassTransit bus and other
                    // background services from attempting to connect to external infrastructure
                    var hostedServiceDescriptors = services
                        .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                        .ToList();
                    foreach (var descriptor in hostedServiceDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    // Replace PostgreSQL DbContext with InMemory provider for test isolation.
                    // This prevents real database connection attempts during test execution.
                    var dbContextDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<ReportingDbContext>));
                    if (dbContextDescriptor != null)
                    {
                        services.Remove(dbContextDescriptor);
                    }
                    services.AddDbContext<ReportingDbContext>(options =>
                        options.UseInMemoryDatabase("TestReportingDb_" + Guid.NewGuid().ToString("N")));

                    // Replace ReportAggregationService with a Moq mock that replicates
                    // the validation logic from the source monolith's ReportService.cs.
                    // The mock avoids real database dependencies while preserving business rules.
                    services.RemoveAll<ReportAggregationService>();
                    services.AddScoped<ReportAggregationService>(sp =>
                    {
                        var dbContext = sp.GetRequiredService<ReportingDbContext>();
                        var logger = sp.GetRequiredService<ILogger<ReportAggregationService>>();
                        var mock = new Mock<ReportAggregationService>(dbContext, logger) { CallBase = false };
                        ConfigureMockService(mock);
                        return mock.Object;
                    });
                });
            });

            _client = _factory.CreateClient();
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", GenerateTestJwtToken());
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Configures the mock ReportAggregationService to replicate the validation logic
        /// from the source monolith's ReportService.cs (lines 15-32).
        /// The mock validates month (1-12), year (>0), and accountId existence,
        /// aggregating all errors via ValidationException.CheckAndThrow().
        /// </summary>
        private static void ConfigureMockService(Mock<ReportAggregationService> mock)
        {
            mock.Setup(x => x.GetTimelogData(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Guid?>()))
                .Returns((int year, int month, Guid? accountId) =>
                {
                    // Replicate validation logic from source ReportService.cs lines 15-32
                    var valEx = new ValidationException();

                    // ReportService.cs line 17-18: if (month > 12 || month <= 0)
                    if (month > 12 || month <= 0)
                    {
                        valEx.AddError("month", "Invalid month.");
                    }

                    // ReportService.cs line 20-21: if (year <= 0)
                    if (year <= 0)
                    {
                        valEx.AddError("year", "Invalid year.");
                    }

                    // ReportService.cs line 28-29: Account existence check
                    if (accountId.HasValue && accountId.Value == NonExistentAccountId)
                    {
                        valEx.AddError("accountId", $"Account with ID:{accountId} not found.");
                    }

                    // ReportService.cs line 32: valEx.CheckAndThrow()
                    // Aggregates ALL errors before throwing — critical for multi-error test
                    valEx.CheckAndThrow();

                    // Return test data matching ReportService.cs output format (lines 106-131)
                    return CreateTestTimelogData();
                });
        }

        /// <summary>
        /// Creates mock timelog report data matching the exact output structure
        /// from the source monolith's ReportService.cs (lines 106-131).
        /// Fields: task_id, project_id, task_subject, project_name, task_type,
        /// billable_minutes (decimal), non_billable_minutes (decimal).
        /// </summary>
        private static List<EntityRecord> CreateTestTimelogData()
        {
            var records = new List<EntityRecord>();

            var rec = new EntityRecord();
            rec["task_id"] = TestTaskId;
            rec["project_id"] = TestProjectId;
            rec["task_subject"] = "Test Task";
            rec["project_name"] = "Test Project";
            rec["task_type"] = "Bug";
            rec["billable_minutes"] = (decimal)120;
            rec["non_billable_minutes"] = (decimal)30;
            records.Add(rec);

            return records;
        }

        /// <summary>
        /// Generates a valid JWT token matching the Reporting service's JWT validation parameters.
        /// Uses the test JWT key, issuer, and audience configured in the WebApplicationFactory.
        /// Token includes ClaimTypes.NameIdentifier with the test user GUID.
        /// Expiration: 60 minutes from generation.
        /// </summary>
        private string GenerateTestJwtToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, TestUserId.ToString())
            };

            var token = new JwtSecurityToken(
                issuer: TestJwtIssuer,
                audience: TestJwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(60),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Creates an HttpClient from the factory WITHOUT authorization headers.
        /// Used to test that unauthenticated requests return HTTP 401 Unauthorized.
        /// Validates the [Authorize] attribute on ReportController.
        /// </summary>
        private HttpClient CreateUnauthenticatedClient()
        {
            return _factory.CreateClient();
        }

        /// <summary>
        /// Reads the HTTP response body and parses it as a JObject for assertion.
        /// Uses Newtonsoft.Json (v13.0.4) matching the monolith's serialization library.
        /// </summary>
        private static async Task<JObject> ReadResponseAsJObjectAsync(HttpResponseMessage response)
        {
            var content = await response.Content.ReadAsStringAsync();
            return JObject.Parse(content);
        }

        #endregion

        #region GET api/v3.0/p/reporting/timelog/monthly — Happy Path Tests

        /// <summary>
        /// Validates that a valid request with year=2024, month=6 (no accountId)
        /// returns HTTP 200 OK with success=true and report data in the response envelope.
        /// Verifies the data matches the mock timelog output structure from ReportService.cs.
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_ValidParameters_ReturnsSuccessWithData()
        {
            // Arrange — authenticated client with valid parameters
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=6";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 200 with success envelope
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeTrue();
            responseObj["timestamp"].Should().NotBeNull();
            responseObj["errors"].Should().NotBeNull();
            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().Be(0);
            responseObj["object"].Should().NotBeNull();

            // Verify data matches mock return values from ReportService.cs output format
            var data = responseObj["object"] as JArray;
            data.Should().NotBeNull();
            data!.Count.Should().BeGreaterThan(0);

            var firstItem = data[0] as JObject;
            firstItem.Should().NotBeNull();
            firstItem!["task_subject"]!.Value<string>().Should().Be("Test Task");
            firstItem["project_name"]!.Value<string>().Should().Be("Test Project");
            firstItem["task_type"]!.Value<string>().Should().Be("Bug");
            firstItem["billable_minutes"]!.Value<decimal>().Should().Be(120);
            firstItem["non_billable_minutes"]!.Value<decimal>().Should().Be(30);
        }

        /// <summary>
        /// Validates that a valid request with year=2024, month=6, and a valid accountId
        /// returns HTTP 200 OK with filtered report data.
        /// Verifies the accountId parameter is correctly passed to the service.
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_ValidParametersWithAccountId_ReturnsFilteredData()
        {
            // Arrange — authenticated client with valid parameters and accountId filter
            var url = $"/api/v3.0/p/reporting/timelog/monthly?year=2024&month=6&accountId={TestAccountId}";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 200 with filtered data
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeTrue();
            responseObj["object"].Should().NotBeNull();

            var data = responseObj["object"] as JArray;
            data.Should().NotBeNull();
            data!.Count.Should().BeGreaterThan(0);
        }

        #endregion

        #region GET api/v3.0/p/reporting/timelog/monthly — Invalid Month Tests

        /// <summary>
        /// Validates that month=0 returns HTTP 400 with validation error.
        /// Business rule from ReportService.cs line 17: if (month > 12 || month &lt;= 0)
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_InvalidMonth_Zero_ReturnsBadRequestWithValidationErrors()
        {
            // Arrange — month=0 triggers validation error
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=0";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with month validation error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            // ValidationError.PropertyName is lowercased via ToLowerInvariant()
            var monthError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "month");
            monthError.Should().NotBeNull();
            monthError!["message"]!.Value<string>().Should().Be("Invalid month.");
        }

        /// <summary>
        /// Validates that month=13 returns HTTP 400 with validation error.
        /// Business rule from ReportService.cs line 17: month > 12
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_InvalidMonth_Thirteen_ReturnsBadRequestWithValidationErrors()
        {
            // Arrange — month=13 exceeds valid range
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=13";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with month validation error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            var monthError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "month");
            monthError.Should().NotBeNull();
            monthError!["message"]!.Value<string>().Should().Be("Invalid month.");
        }

        /// <summary>
        /// Validates that month=-1 returns HTTP 400 with validation error.
        /// Business rule from ReportService.cs line 17: month &lt;= 0
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_InvalidMonth_Negative_ReturnsBadRequestWithValidationErrors()
        {
            // Arrange — negative month
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=-1";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with month validation error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            var monthError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "month");
            monthError.Should().NotBeNull();
            monthError!["message"]!.Value<string>().Should().Be("Invalid month.");
        }

        #endregion

        #region GET api/v3.0/p/reporting/timelog/monthly — Invalid Year Tests

        /// <summary>
        /// Validates that year=0 returns HTTP 400 with validation error.
        /// Business rule from ReportService.cs lines 20-21: if (year &lt;= 0)
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_InvalidYear_Zero_ReturnsBadRequestWithValidationErrors()
        {
            // Arrange — year=0 triggers validation error
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=0&month=6";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with year validation error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            var yearError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "year");
            yearError.Should().NotBeNull();
            yearError!["message"]!.Value<string>().Should().Be("Invalid year.");
        }

        /// <summary>
        /// Validates that year=-1 returns HTTP 400 with validation error.
        /// Business rule from ReportService.cs lines 20-21: if (year &lt;= 0)
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_InvalidYear_Negative_ReturnsBadRequestWithValidationErrors()
        {
            // Arrange — negative year
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=-1&month=6";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with year validation error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            var yearError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "year");
            yearError.Should().NotBeNull();
            yearError!["message"]!.Value<string>().Should().Be("Invalid year.");
        }

        #endregion

        #region GET api/v3.0/p/reporting/timelog/monthly — Account and Auth Tests

        /// <summary>
        /// Validates that a non-existent accountId returns HTTP 400 with the exact error message
        /// format from ReportService.cs line 29: "Account with ID:{accountId} not found."
        /// The error key should be "accountid" (lowercased via ValidationError.PropertyName).
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_NonExistentAccountId_ReturnsBadRequestWithAccountNotFound()
        {
            // Arrange — use the well-known non-existent account ID
            var url = $"/api/v3.0/p/reporting/timelog/monthly?year=2024&month=6&accountId={NonExistentAccountId}";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with account not found error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            // ValidationError.PropertyName is lowercased: "accountId" → "accountid"
            var accountError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "accountid");
            accountError.Should().NotBeNull();
            accountError!["message"]!.Value<string>().Should().Contain($"Account with ID:{NonExistentAccountId} not found.");
        }

        /// <summary>
        /// Validates that an unauthenticated request (no Authorization header)
        /// returns HTTP 401 Unauthorized. Verifies the [Authorize] attribute on ReportController.
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_Unauthenticated_Returns401()
        {
            // Arrange — client without authorization headers
            var unauthenticatedClient = CreateUnauthenticatedClient();

            // Act
            var response = await unauthenticatedClient.GetAsync(
                "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=6");

            // Assert — HTTP 401
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        /// <summary>
        /// Validates that multiple validation errors are aggregated and returned together.
        /// When both year=0 and month=0 are invalid, the response should contain at least 2 errors.
        /// This tests the ValidationException aggregation pattern: valEx.CheckAndThrow() at line 32,
        /// which collects ALL errors before throwing (not fail-fast behavior).
        /// </summary>
        [Fact]
        public async Task GetMonthlyTimelogReport_MultipleValidationErrors_ReturnsBadRequestWithAllErrors()
        {
            // Arrange — both year=0 and month=0 are invalid
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=0&month=0";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with multiple validation errors
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterOrEqualTo(2);

            // Verify both month and year errors are present
            var monthError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "month");
            monthError.Should().NotBeNull();
            monthError!["message"]!.Value<string>().Should().Be("Invalid month.");

            var yearError = errors.FirstOrDefault(e => e["key"]!.Value<string>() == "year");
            yearError.Should().NotBeNull();
            yearError!["message"]!.Value<string>().Should().Be("Invalid year.");
        }

        #endregion

        #region GET api/v3.0/p/reporting/definitions — Report Definitions Tests

        /// <summary>
        /// Validates that an authenticated request to the report definitions endpoint
        /// returns HTTP 200 OK with a success response containing available report definitions.
        /// The response should include at least the monthly timelog report definition.
        /// </summary>
        [Fact]
        public async Task GetReportDefinitions_Authenticated_ReturnsSuccessWithDefinitions()
        {
            // Arrange — authenticated client
            var url = "/api/v3.0/p/reporting/definitions";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 200 with definitions list
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeTrue();
            responseObj["object"].Should().NotBeNull();

            // Verify the response contains report definitions
            var definitions = responseObj["object"] as JArray;
            definitions.Should().NotBeNull();
            definitions!.Count.Should().BeGreaterThan(0);
        }

        /// <summary>
        /// Validates that an unauthenticated request to the definitions endpoint
        /// returns HTTP 401 Unauthorized.
        /// </summary>
        [Fact]
        public async Task GetReportDefinitions_Unauthenticated_Returns401()
        {
            // Arrange — client without authorization
            var unauthenticatedClient = CreateUnauthenticatedClient();

            // Act
            var response = await unauthenticatedClient.GetAsync("/api/v3.0/p/reporting/definitions");

            // Assert — HTTP 401
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        #endregion

        #region GET api/v3.0/p/reporting/results/{reportId} — Report Results Tests

        /// <summary>
        /// Validates that a valid report ID (MonthlyTimelogReportId) with valid parameters
        /// returns HTTP 200 OK with report results.
        /// The controller routes this to ReportAggregationService.GetTimelogData().
        /// </summary>
        [Fact]
        public async Task GetReportResults_ValidReportId_ReturnsSuccessWithResults()
        {
            // Arrange — use the well-known monthly timelog report ID with valid params
            var url = $"/api/v3.0/p/reporting/results/{MonthlyTimelogReportId}?year=2024&month=6";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 200 with report results
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeTrue();
            responseObj["object"].Should().NotBeNull();
        }

        /// <summary>
        /// Validates that an unknown report ID returns HTTP 404 Not Found or HTTP 400 Bad Request.
        /// The controller uses DoItemNotFoundResponse for unknown report IDs.
        /// Message: "Report definition with ID:{reportId} not found."
        /// </summary>
        [Fact]
        public async Task GetReportResults_InvalidReportId_ReturnsNotFoundOrBadRequest()
        {
            // Arrange — use a random non-existent report ID
            var unknownReportId = Guid.NewGuid();
            var url = $"/api/v3.0/p/reporting/results/{unknownReportId}?year=2024&month=6";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — should be 404 (DoItemNotFoundResponse) or 400 (DoBadRequestResponse)
            response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            responseObj["success"]!.Value<bool>().Should().BeFalse();
        }

        /// <summary>
        /// Validates that an unauthenticated request to the results endpoint
        /// returns HTTP 401 Unauthorized.
        /// </summary>
        [Fact]
        public async Task GetReportResults_Unauthenticated_Returns401()
        {
            // Arrange — client without authorization
            var unauthenticatedClient = CreateUnauthenticatedClient();
            var reportId = Guid.NewGuid();

            // Act
            var response = await unauthenticatedClient.GetAsync(
                $"/api/v3.0/p/reporting/results/{reportId}?year=2024&month=6");

            // Assert — HTTP 401
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        #endregion

        #region Response Envelope Validation Tests

        /// <summary>
        /// Validates that a successful response contains ALL required BaseResponseModel fields:
        /// timestamp (DateTime), success (bool=true), message (string), hash (string),
        /// errors (empty array), accessWarnings (empty array), object (data).
        /// This validates AAP 0.8.1: "Response shapes (BaseResponseModel envelope:
        /// success, errors, timestamp, message, object) must not change."
        /// Field names correspond to BaseModels.cs [JsonProperty] annotations.
        /// </summary>
        [Fact]
        public async Task ResponseEnvelope_SuccessResponse_ContainsAllRequiredFields()
        {
            // Arrange — any successful request
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=6";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — verify all BaseResponseModel fields are present
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var responseObj = await ReadResponseAsJObjectAsync(response);

            // BaseModels.cs line 10-11: public DateTime Timestamp [JsonProperty("timestamp")]
            responseObj["timestamp"].Should().NotBeNull();

            // BaseModels.cs line 13-14: public bool Success [JsonProperty("success")]
            responseObj["success"].Should().NotBeNull();
            responseObj["success"]!.Value<bool>().Should().BeTrue();

            // BaseModels.cs line 16-17: public string Message [JsonProperty("message")]
            responseObj.ContainsKey("message").Should().BeTrue();

            // BaseModels.cs line 19-20: public string Hash [JsonProperty("hash")]
            responseObj.ContainsKey("hash").Should().BeTrue();

            // BaseModels.cs line 22-23: public List<ErrorModel> Errors [JsonProperty("errors")]
            responseObj["errors"].Should().NotBeNull();
            var errorsArray = responseObj["errors"] as JArray;
            errorsArray.Should().NotBeNull();
            errorsArray!.Count.Should().Be(0);

            // BaseModels.cs line 25-26: public List<AccessWarningModel> AccessWarnings [JsonProperty("accessWarnings")]
            responseObj.ContainsKey("accessWarnings").Should().BeTrue();

            // BaseModels.cs line 42-43: ResponseModel inherits BaseResponseModel with Object property
            responseObj["object"].Should().NotBeNull();
        }

        /// <summary>
        /// Validates that an error response contains ALL required BaseResponseModel fields:
        /// timestamp (not null), success (false), errors (non-empty array with key/value/message).
        /// Validates AAP 0.8.1 backward compatibility for error responses.
        /// </summary>
        [Fact]
        public async Task ResponseEnvelope_ErrorResponse_ContainsAllRequiredFields()
        {
            // Arrange — trigger validation error with invalid month
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=0";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — HTTP 400 with complete error envelope
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);

            // timestamp — not null
            responseObj["timestamp"].Should().NotBeNull();

            // success — false
            responseObj["success"].Should().NotBeNull();
            responseObj["success"]!.Value<bool>().Should().BeFalse();

            // errors — non-empty array
            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            // Each error has key, value, message fields (ErrorModel from BaseModels.cs lines 64-82)
            var firstError = errors[0] as JObject;
            firstError.Should().NotBeNull();
            firstError!.ContainsKey("key").Should().BeTrue();
            firstError.ContainsKey("value").Should().BeTrue();
            firstError.ContainsKey("message").Should().BeTrue();
        }

        /// <summary>
        /// Validates that each item in the errors array matches the ErrorModel structure
        /// from BaseModels.cs (lines 62-83):
        /// - key (string): corresponds to field name (e.g., "month", "year", "accountid")
        /// - value (string): error value (empty string in controller mapping)
        /// - message (string): human-readable message (e.g., "Invalid month.", "Invalid year.")
        /// The controller maps ValidationError.PropertyName → ErrorModel.Key,
        /// and PropertyName is lowercased via ToLowerInvariant().
        /// </summary>
        [Fact]
        public async Task ResponseEnvelope_ErrorModel_MatchesMonolithStructure()
        {
            // Arrange — trigger a known validation error to inspect ErrorModel structure
            var url = "/api/v3.0/p/reporting/timelog/monthly?year=2024&month=0";

            // Act
            var response = await _client.GetAsync(url);

            // Assert — verify ErrorModel field structure
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var responseObj = await ReadResponseAsJObjectAsync(response);
            var errors = responseObj["errors"] as JArray;
            errors.Should().NotBeNull();
            errors!.Count.Should().BeGreaterThan(0);

            foreach (var error in errors)
            {
                var errorObj = error as JObject;
                errorObj.Should().NotBeNull();

                // key — string, corresponds to field name (lowercased PropertyName)
                var key = errorObj!["key"];
                key.Should().NotBeNull();
                key!.Type.Should().Be(JTokenType.String);

                // value — string, set to empty string by controller mapping:
                // new ErrorModel(e.PropertyName, "", e.Message)
                var value = errorObj["value"];
                value.Should().NotBeNull();
                value!.Type.Should().Be(JTokenType.String);
                value.Value<string>().Should().Be("");

                // message — string, human-readable error message
                var message = errorObj["message"];
                message.Should().NotBeNull();
                message!.Type.Should().Be(JTokenType.String);
                message.Value<string>().Should().NotBeNullOrEmpty();
            }
        }

        #endregion
    }
}
