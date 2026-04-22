using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WebVellaErp.Inventory.Functions;
using WebVellaErp.Inventory.Services;
using Models = WebVellaErp.Inventory.Models;
using Xunit;

namespace WebVellaErp.Inventory.Tests.Unit
{
    /// <summary>
    /// Comprehensive xUnit unit tests for TimelogHandler Lambda handler.
    /// Tests verify all 5 handler methods (CreateTimelog, DeleteTimelog, GetTimelogs,
    /// TrackTime, HealthCheck), JWT claims extraction, request parsing, error handling,
    /// and response formatting.
    /// All ITaskService calls are mocked via Moq — no actual AWS SDK calls.
    /// </summary>
    public class TimelogHandlerTests
    {
        private readonly Mock<ITaskService> _taskServiceMock;
        private readonly Mock<ILogger<TimelogHandler>> _loggerMock;
        private readonly TimelogHandler _sut;
        private readonly TestLambdaContext _lambdaContext;

        // Standard user ID for JWT claims across tests
        private static readonly Guid TestUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid TestTimelogId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid TestTaskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private static readonly Guid TestProjectId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        public TimelogHandlerTests()
        {
            _taskServiceMock = new Mock<ITaskService>();
            _loggerMock = new Mock<ILogger<TimelogHandler>>();
            _sut = new TimelogHandler(_taskServiceMock.Object, _loggerMock.Object);
            _lambdaContext = new TestLambdaContext
            {
                AwsRequestId = "test-correlation-id-12345"
            };
        }

        #region Helper Methods

        /// <summary>
        /// Creates an API Gateway v2 proxy request with JWT claims containing the specified user ID.
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateAuthenticatedRequest(
            Guid userId,
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            string rawPath = "/v1/inventory/timelogs")
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = rawPath,
                Body = body,
                PathParameters = pathParameters,
                QueryStringParameters = queryStringParameters,
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                { "sub", userId.ToString() }
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates an API Gateway v2 proxy request without JWT claims (unauthenticated).
        /// </summary>
        private static APIGatewayHttpApiV2ProxyRequest CreateUnauthenticatedRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            string rawPath = "/v1/inventory/timelogs")
        {
            return new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = rawPath,
                Body = body,
                PathParameters = pathParameters,
                QueryStringParameters = queryStringParameters,
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext()
            };
        }

        /// <summary>
        /// Deserializes the response body to extract the ResponseModel fields.
        /// </summary>
        private static Models.ResponseModel? DeserializeResponse(string responseBody)
        {
            return JsonSerializer.Deserialize<Models.ResponseModel>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        /// <summary>
        /// Creates a standard CreateTimelog request body JSON string.
        /// </summary>
        private static string CreateTimelogRequestBody(
            int minutes = 30,
            bool isBillable = true,
            string loggedOn = "2024-01-15T10:00:00Z",
            string body = "Test work",
            List<Guid>? relatedRecords = null)
        {
            var bodyObj = new Dictionary<string, object>
            {
                { "minutes", minutes },
                { "isBillable", isBillable },
                { "loggedOn", loggedOn },
                { "body", body }
            };
            if (relatedRecords != null)
            {
                bodyObj["relatedRecords"] = relatedRecords;
            }
            return JsonSerializer.Serialize(bodyObj);
        }

        /// <summary>
        /// Creates a standard TrackTime request body JSON string.
        /// </summary>
        private static string CreateTrackTimeRequestBody(
            Guid taskId,
            int minutes = 30,
            string loggedOn = "2024-01-15T10:00:00Z",
            string body = "Test tracking",
            bool isBillable = true)
        {
            var bodyObj = new Dictionary<string, object>
            {
                { "taskId", taskId.ToString() },
                { "minutes", minutes },
                { "loggedOn", loggedOn },
                { "body", body },
                { "isBillable", isBillable }
            };
            return JsonSerializer.Serialize(bodyObj);
        }

        #endregion

        #region Constructor Tests

        [Fact]
        public void Constructor_WithValidDependencies_ShouldCreateInstance()
        {
            var handler = new TimelogHandler(_taskServiceMock.Object, _loggerMock.Object);
            handler.Should().NotBeNull();
        }

        [Fact]
        public void Constructor_WithNullTaskService_ShouldThrowArgumentNullException()
        {
            Action act = () => new TimelogHandler(null!, _loggerMock.Object);
            act.Should().Throw<ArgumentNullException>().WithParameterName("taskService");
        }

        [Fact]
        public void Constructor_WithNullLogger_ShouldThrowArgumentNullException()
        {
            Action act = () => new TimelogHandler(_taskServiceMock.Object, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
        }

        #endregion

        #region CreateTimelog Tests

        [Fact]
        public async Task CreateTimelog_WithValidRequest_ShouldReturn201()
        {
            var requestBody = CreateTimelogRequestBody(minutes: 60, isBillable: true, body: "Implemented feature");
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(201);
            var responseModel = DeserializeResponse(response.Body);
            responseModel.Should().NotBeNull();
            responseModel!.Success.Should().BeTrue();
            responseModel.Message.Should().Be("Timelog successfully created");
        }

        [Fact]
        public async Task CreateTimelog_ShouldCallCreateTimelogAsyncWithCorrectParameters()
        {
            var requestBody = CreateTimelogRequestBody(minutes: 45, isBillable: false, body: "Code review");
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            await _sut.CreateTimelog(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(),
                TestUserId,
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                45,
                false,
                "Code review",
                It.Is<List<string>>(l => l.Contains("projects")),
                It.IsAny<List<Guid>>()), Times.Once());
        }

        [Fact]
        public async Task CreateTimelog_ShouldCallHandleTimelogCreationHookAsync()
        {
            var requestBody = CreateTimelogRequestBody();
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            await _sut.CreateTimelog(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.HandleTimelogCreationHookAsync(
                It.Is<Models.Timelog>(t => t.Minutes == 30 && t.IsBillable == true),
                TestUserId), Times.Once());
        }

        [Fact]
        public async Task CreateTimelog_WithEmptyBody_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId, body: "");

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeFalse();
            responseModel.Message.Should().Contain("Request body is required");
        }

        [Fact]
        public async Task CreateTimelog_WithNullBody_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId, body: null);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateTimelog_WithInvalidJsonBody_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId, body: "not-valid-json{{{}}}");

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("Invalid JSON");
        }

        [Fact]
        public async Task CreateTimelog_WithMissingMinutes_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new { loggedOn = "2024-01-15T10:00:00Z" });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("minutes");
        }

        [Fact]
        public async Task CreateTimelog_WithInvalidMinutes_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new { minutes = "not-a-number", loggedOn = "2024-01-15T10:00:00Z" });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("minutes");
        }

        [Fact]
        public async Task CreateTimelog_WithMissingLoggedOn_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new { minutes = 30 });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("loggedOn");
        }

        [Fact]
        public async Task CreateTimelog_WithInvalidLoggedOn_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new { minutes = 30, loggedOn = "not-a-date" });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("loggedOn");
        }

        [Fact]
        public async Task CreateTimelog_WithRelatedRecords_ShouldPassThemToService()
        {
            var relatedGuids = new List<Guid> { TestTaskId, TestProjectId };
            var requestBody = CreateTimelogRequestBody(relatedRecords: relatedGuids);
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            await _sut.CreateTimelog(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(),
                It.Is<List<Guid>>(l => l.Count == 2 && l.Contains(TestTaskId) && l.Contains(TestProjectId))),
                Times.Once());
        }

        [Fact]
        public async Task CreateTimelog_WhenUnauthenticated_ShouldReturn401()
        {
            var requestBody = CreateTimelogRequestBody();
            var request = CreateUnauthenticatedRequest(body: requestBody);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(401);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task CreateTimelog_WhenServiceThrowsInvalidOperation_ShouldReturn400()
        {
            var requestBody = CreateTimelogRequestBody();
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .ThrowsAsync(new InvalidOperationException("Validation failed"));

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task CreateTimelog_WhenServiceThrowsException_ShouldReturn500()
        {
            var requestBody = CreateTimelogRequestBody();
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .ThrowsAsync(new Exception("DynamoDB error"));

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(500);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().NotContain("DynamoDB"); // Should not leak internal details
        }

        [Fact]
        public async Task CreateTimelog_ResponseShouldHaveJsonContentTypeHeader()
        {
            var requestBody = CreateTimelogRequestBody();
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Be("application/json");
        }

        [Fact]
        public async Task CreateTimelog_ResponseShouldHaveCorsHeaders()
        {
            var requestBody = CreateTimelogRequestBody();
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.Headers.Should().ContainKey("Access-Control-Allow-Origin");
            response.Headers["Access-Control-Allow-Origin"].Should().Be("*");
        }

        [Fact]
        public async Task CreateTimelog_WithSnakeCaseBody_ShouldParseCorrectly()
        {
            // Test snake_case property names (backward compatibility)
            var body = JsonSerializer.Serialize(new
            {
                minutes = 30,
                is_billable = true,
                logged_on = "2024-01-15T10:00:00Z",
                body = "Work done",
                related_records = new[] { TestTaskId.ToString() }
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(201);
        }

        [Fact]
        public async Task CreateTimelog_DefaultIsBillable_ShouldBeFalse()
        {
            // When isBillable is not provided, default should be false
            var body = JsonSerializer.Serialize(new
            {
                minutes = 30,
                loggedOn = "2024-01-15T10:00:00Z"
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            await _sut.CreateTimelog(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(),
                false, // default isBillable
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()), Times.Once());
        }

        #endregion

        #region DeleteTimelog Tests

        [Fact]
        public async Task DeleteTimelog_WithValidId_ShouldReturn200()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } },
                rawPath: $"/v1/inventory/timelogs/{TestTimelogId}");

            _taskServiceMock.Setup(s => s.HandleTimelogDeletionHookAsync(TestTimelogId))
                .Returns(Task.CompletedTask);
            _taskServiceMock.Setup(s => s.DeleteTimelogAsync(TestTimelogId, TestUserId))
                .Returns(Task.CompletedTask);

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(200);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeTrue();
            responseModel.Message.Should().Be("Timelog successfully deleted");
        }

        [Fact]
        public async Task DeleteTimelog_ShouldCallDeletionHookBeforeDelete()
        {
            var callOrder = new List<string>();

            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } });

            _taskServiceMock.Setup(s => s.HandleTimelogDeletionHookAsync(TestTimelogId))
                .Callback(() => callOrder.Add("hook"))
                .Returns(Task.CompletedTask);
            _taskServiceMock.Setup(s => s.DeleteTimelogAsync(TestTimelogId, TestUserId))
                .Callback(() => callOrder.Add("delete"))
                .Returns(Task.CompletedTask);

            await _sut.DeleteTimelog(request, _lambdaContext);

            callOrder.Should().ContainInOrder("hook", "delete");
        }

        [Fact]
        public async Task DeleteTimelog_WithMissingId_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string>());

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("id");
        }

        [Fact]
        public async Task DeleteTimelog_WithInvalidGuidId_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", "not-a-guid" } });

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task DeleteTimelog_WhenUnauthenticated_ShouldReturn401()
        {
            var request = CreateUnauthenticatedRequest(
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } });

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task DeleteTimelog_WhenNotAuthor_ShouldReturn403()
        {
            // Source line 72: "Only the author can delete its comment" → thrown as InvalidOperationException
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } });

            _taskServiceMock.Setup(s => s.HandleTimelogDeletionHookAsync(TestTimelogId))
                .Returns(Task.CompletedTask);
            _taskServiceMock.Setup(s => s.DeleteTimelogAsync(TestTimelogId, TestUserId))
                .ThrowsAsync(new InvalidOperationException("Only the author can delete this timelog."));

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(403);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("Only the author");
        }

        [Fact]
        public async Task DeleteTimelog_WhenServiceThrowsException_ShouldReturn500()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } });

            _taskServiceMock.Setup(s => s.HandleTimelogDeletionHookAsync(TestTimelogId))
                .ThrowsAsync(new Exception("Database error"));

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(500);
        }

        [Fact]
        public async Task DeleteTimelog_MessageShouldSayTimelogNotComment()
        {
            // Bug fix verification: source line 289 had "Comment successfully deleted"
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } });

            _taskServiceMock.Setup(s => s.HandleTimelogDeletionHookAsync(TestTimelogId))
                .Returns(Task.CompletedTask);
            _taskServiceMock.Setup(s => s.DeleteTimelogAsync(TestTimelogId, TestUserId))
                .Returns(Task.CompletedTask);

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("Timelog");
            responseModel.Message.Should().NotContain("Comment");
        }

        #endregion

        #region GetTimelogs Tests

        [Fact]
        public async Task GetTimelogs_WithValidDateRange_ShouldReturn200()
        {
            var timelogs = new List<Models.Timelog>
            {
                new Models.Timelog { Id = Guid.NewGuid(), Minutes = 30, LoggedOn = DateTime.UtcNow },
                new Models.Timelog { Id = Guid.NewGuid(), Minutes = 60, LoggedOn = DateTime.UtcNow }
            };

            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" }
                });

            _taskServiceMock.Setup(s => s.GetTimelogsForPeriodAsync(null, null,
                It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(timelogs);

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(200);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task GetTimelogs_WithProjectFilter_ShouldPassProjectIdToService()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" },
                    { "projectId", TestProjectId.ToString() }
                });

            _taskServiceMock.Setup(s => s.GetTimelogsForPeriodAsync(
                TestProjectId, null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new List<Models.Timelog>());

            await _sut.GetTimelogs(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.GetTimelogsForPeriodAsync(
                TestProjectId, null, It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once());
        }

        [Fact]
        public async Task GetTimelogs_WithUserFilter_ShouldPassUserIdToService()
        {
            var filterUserId = Guid.NewGuid();
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" },
                    { "userId", filterUserId.ToString() }
                });

            _taskServiceMock.Setup(s => s.GetTimelogsForPeriodAsync(
                null, filterUserId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new List<Models.Timelog>());

            await _sut.GetTimelogs(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.GetTimelogsForPeriodAsync(
                null, filterUserId, It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once());
        }

        [Fact]
        public async Task GetTimelogs_WithBothFilters_ShouldPassBothToService()
        {
            var filterUserId = Guid.NewGuid();
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" },
                    { "projectId", TestProjectId.ToString() },
                    { "userId", filterUserId.ToString() }
                });

            _taskServiceMock.Setup(s => s.GetTimelogsForPeriodAsync(
                TestProjectId, filterUserId, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ReturnsAsync(new List<Models.Timelog>());

            await _sut.GetTimelogs(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.GetTimelogsForPeriodAsync(
                TestProjectId, filterUserId, It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once());
        }

        [Fact]
        public async Task GetTimelogs_WithMissingStartDate_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "endDate", "2024-01-31T23:59:59Z" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("startDate");
        }

        [Fact]
        public async Task GetTimelogs_WithMissingEndDate_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("endDate");
        }

        [Fact]
        public async Task GetTimelogs_WithEndDateBeforeStartDate_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-31T00:00:00Z" },
                    { "endDate", "2024-01-01T00:00:00Z" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("endDate must be after startDate");
        }

        [Fact]
        public async Task GetTimelogs_WithEqualDates_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-15T00:00:00Z" },
                    { "endDate", "2024-01-15T00:00:00Z" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task GetTimelogs_WithInvalidStartDate_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "not-a-date" },
                    { "endDate", "2024-01-31T23:59:59Z" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("startDate");
        }

        [Fact]
        public async Task GetTimelogs_WithInvalidProjectId_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" },
                    { "projectId", "not-a-guid" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("projectId");
        }

        [Fact]
        public async Task GetTimelogs_WithInvalidUserId_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" },
                    { "userId", "not-a-guid" }
                });

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("userId");
        }

        [Fact]
        public async Task GetTimelogs_WithNullQueryParameters_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: null);

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task GetTimelogs_WhenServiceThrowsException_ShouldReturn500()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                queryStringParameters: new Dictionary<string, string>
                {
                    { "startDate", "2024-01-01T00:00:00Z" },
                    { "endDate", "2024-01-31T23:59:59Z" }
                });

            _taskServiceMock.Setup(s => s.GetTimelogsForPeriodAsync(
                null, null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
                .ThrowsAsync(new Exception("DynamoDB failure"));

            var response = await _sut.GetTimelogs(request, _lambdaContext);

            response.StatusCode.Should().Be(500);
        }

        #endregion

        #region TrackTime Tests

        [Fact]
        public async Task TrackTime_WithValidRequest_ShouldReturn200()
        {
            var requestBody = CreateTrackTimeRequestBody(TestTaskId, minutes: 30);
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody,
                rawPath: "/v1/inventory/timelogs/track");

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ReturnsAsync(new Models.Task { Id = TestTaskId, Subject = "Test Task" });

            _taskServiceMock.Setup(s => s.HandleTrackTimePagePostAsync(
                TestTaskId, 30, It.IsAny<DateTime>(), true, "Test tracking", TestUserId))
                .Returns(Task.CompletedTask);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(200);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeTrue();
            responseModel.Message.Should().Be("Time tracked successfully");
        }

        [Fact]
        public async Task TrackTime_ShouldCallGetTaskAsyncBeforeTrackTimeOperation()
        {
            var requestBody = CreateTrackTimeRequestBody(TestTaskId);
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ReturnsAsync(new Models.Task { Id = TestTaskId });

            _taskServiceMock.Setup(s => s.HandleTrackTimePagePostAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            await _sut.TrackTime(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.GetTaskAsync(TestTaskId), Times.Once());
        }

        [Fact]
        public async Task TrackTime_WithNonExistentTask_ShouldReturn404()
        {
            var requestBody = CreateTrackTimeRequestBody(TestTaskId);
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ReturnsAsync((Models.Task?)null);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(404);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain(TestTaskId.ToString());
        }

        [Fact]
        public async Task TrackTime_DefaultIsBillable_ShouldBeTrue()
        {
            // isBillable default is true for TrackTime (different from CreateTimelog where it's false)
            var body = JsonSerializer.Serialize(new
            {
                taskId = TestTaskId.ToString(),
                minutes = 30,
                loggedOn = "2024-01-15T10:00:00Z"
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ReturnsAsync(new Models.Task { Id = TestTaskId });

            _taskServiceMock.Setup(s => s.HandleTrackTimePagePostAsync(
                TestTaskId, 30, It.IsAny<DateTime>(), true, It.IsAny<string>(), TestUserId))
                .Returns(Task.CompletedTask);

            await _sut.TrackTime(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.HandleTrackTimePagePostAsync(
                TestTaskId, 30, It.IsAny<DateTime>(),
                true, // default isBillable for TrackTime
                It.IsAny<string>(), TestUserId), Times.Once());
        }

        [Fact]
        public async Task TrackTime_WithEmptyBody_ShouldReturn400()
        {
            var request = CreateAuthenticatedRequest(TestUserId, body: "");

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task TrackTime_WithMissingTaskId_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new
            {
                minutes = 30,
                loggedOn = "2024-01-15T10:00:00Z"
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("taskId");
        }

        [Fact]
        public async Task TrackTime_WithInvalidTaskIdFormat_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new
            {
                taskId = "not-a-guid",
                minutes = 30,
                loggedOn = "2024-01-15T10:00:00Z"
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Message.Should().Contain("taskId");
        }

        [Fact]
        public async Task TrackTime_WithMissingMinutes_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new
            {
                taskId = TestTaskId.ToString(),
                loggedOn = "2024-01-15T10:00:00Z"
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task TrackTime_WithMissingLoggedOn_ShouldReturn400()
        {
            var body = JsonSerializer.Serialize(new
            {
                taskId = TestTaskId.ToString(),
                minutes = 30
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task TrackTime_WhenUnauthenticated_ShouldReturn401()
        {
            var requestBody = CreateTrackTimeRequestBody(TestTaskId);
            var request = CreateUnauthenticatedRequest(body: requestBody);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task TrackTime_WhenServiceThrowsInvalidOperation_ShouldReturn400()
        {
            var requestBody = CreateTrackTimeRequestBody(TestTaskId);
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ReturnsAsync(new Models.Task { Id = TestTaskId });

            _taskServiceMock.Setup(s => s.HandleTrackTimePagePostAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DateTime>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<Guid>()))
                .ThrowsAsync(new InvalidOperationException("Task is not in progress"));

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(400);
        }

        [Fact]
        public async Task TrackTime_WhenServiceThrowsException_ShouldReturn500()
        {
            var requestBody = CreateTrackTimeRequestBody(TestTaskId);
            var request = CreateAuthenticatedRequest(TestUserId, body: requestBody);

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ThrowsAsync(new Exception("DynamoDB failure"));

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(500);
        }

        [Fact]
        public async Task TrackTime_WithSnakeCaseTaskId_ShouldParseCorrectly()
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                { "task_id", TestTaskId.ToString() },
                { "minutes", 30 },
                { "logged_on", "2024-01-15T10:00:00Z" },
                { "is_billable", false }
            });
            var request = CreateAuthenticatedRequest(TestUserId, body: body);

            _taskServiceMock.Setup(s => s.GetTaskAsync(TestTaskId))
                .ReturnsAsync(new Models.Task { Id = TestTaskId });

            _taskServiceMock.Setup(s => s.HandleTrackTimePagePostAsync(
                TestTaskId, 30, It.IsAny<DateTime>(), false, It.IsAny<string>(), TestUserId))
                .Returns(Task.CompletedTask);

            var response = await _sut.TrackTime(request, _lambdaContext);

            response.StatusCode.Should().Be(200);
        }

        #endregion

        #region HealthCheck Tests

        [Fact]
        public async Task HealthCheck_ShouldReturn200()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                rawPath: "/v1/inventory/timelogs/health");

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.StatusCode.Should().Be(200);
        }

        [Fact]
        public async Task HealthCheck_ShouldReturnHealthyStatus()
        {
            var request = CreateAuthenticatedRequest(TestUserId);

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.Body.Should().Contain("healthy");
        }

        [Fact]
        public async Task HealthCheck_ShouldReturnServiceName()
        {
            var request = CreateAuthenticatedRequest(TestUserId);

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.Body.Should().Contain("inventory-timelog");
        }

        [Fact]
        public async Task HealthCheck_ShouldIncludeCorrelationId()
        {
            var request = CreateAuthenticatedRequest(TestUserId);

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.Body.Should().Contain("test-correlation-id-12345");
        }

        [Fact]
        public async Task HealthCheck_ShouldIncludeTimestamp()
        {
            var request = CreateAuthenticatedRequest(TestUserId);

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.Body.Should().Contain("timestamp");
        }

        [Fact]
        public async Task HealthCheck_ShouldHaveJsonContentType()
        {
            var request = CreateAuthenticatedRequest(TestUserId);

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.Headers.Should().ContainKey("Content-Type");
            response.Headers["Content-Type"].Should().Be("application/json");
        }

        [Fact]
        public async Task HealthCheck_WithUnauthenticatedRequest_ShouldStillReturn200()
        {
            // Health check should not require authentication
            var request = CreateUnauthenticatedRequest(rawPath: "/v1/inventory/timelogs/health");

            var response = await _sut.HealthCheck(request, _lambdaContext);

            response.StatusCode.Should().Be(200);
        }

        #endregion

        #region JWT Claims Extraction Tests

        [Fact]
        public async Task Handler_WithValidJwtSubClaim_ShouldExtractUserId()
        {
            var userId = Guid.NewGuid();
            var requestBody = CreateTimelogRequestBody();
            var request = CreateAuthenticatedRequest(userId, body: requestBody);

            _taskServiceMock.Setup(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), userId, It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()))
                .Returns(Task.CompletedTask);

            _taskServiceMock.Setup(s => s.HandleTimelogCreationHookAsync(
                It.IsAny<Models.Timelog>(), It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);

            await _sut.CreateTimelog(request, _lambdaContext);

            _taskServiceMock.Verify(s => s.CreateTimelogAsync(
                It.IsAny<Guid?>(), userId, It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<List<Guid>>()), Times.Once());
        }

        [Fact]
        public async Task Handler_WithInvalidGuidInSubClaim_ShouldReturn401()
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = "/v1/inventory/timelogs",
                Body = CreateTimelogRequestBody(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                { "sub", "not-a-valid-guid" }
                            }
                        }
                    }
                }
            };

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Handler_WithNullRequestContext_ShouldReturn401()
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = "/v1/inventory/timelogs",
                Body = CreateTimelogRequestBody(),
                RequestContext = null!
            };

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(401);
        }

        [Fact]
        public async Task Handler_WithNullAuthorizer_ShouldReturn401()
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = "/v1/inventory/timelogs",
                Body = CreateTimelogRequestBody(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    Authorizer = null
                }
            };

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.StatusCode.Should().Be(401);
        }

        #endregion

        #region Response Format Tests

        [Fact]
        public async Task ErrorResponse_ShouldHaveSuccessFalse()
        {
            var request = CreateAuthenticatedRequest(TestUserId, body: "");

            var response = await _sut.CreateTimelog(request, _lambdaContext);

            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeFalse();
        }

        [Fact]
        public async Task SuccessResponse_ShouldHaveSuccessTrue()
        {
            var request = CreateAuthenticatedRequest(TestUserId,
                pathParameters: new Dictionary<string, string> { { "id", TestTimelogId.ToString() } });

            _taskServiceMock.Setup(s => s.HandleTimelogDeletionHookAsync(TestTimelogId))
                .Returns(Task.CompletedTask);
            _taskServiceMock.Setup(s => s.DeleteTimelogAsync(TestTimelogId, TestUserId))
                .Returns(Task.CompletedTask);

            var response = await _sut.DeleteTimelog(request, _lambdaContext);

            var responseModel = DeserializeResponse(response.Body);
            responseModel!.Success.Should().BeTrue();
        }

        [Fact]
        public async Task AllResponses_ShouldHaveAccessControlHeaders()
        {
            // Test that even error responses include CORS headers
            var request = CreateAuthenticatedRequest(TestUserId, body: "");
            var response = await _sut.CreateTimelog(request, _lambdaContext);

            response.Headers.Should().ContainKey("Access-Control-Allow-Origin");
            response.Headers.Should().ContainKey("Access-Control-Allow-Methods");
            response.Headers.Should().ContainKey("Access-Control-Allow-Headers");
        }

        #endregion
    }
}
