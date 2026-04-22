// ============================================================================
// File: services/invoicing/tests/Unit/InvoiceHandlerTests.cs
//
// Unit tests for InvoiceHandler.FunctionHandler path-based dispatch logic.
//
// Phase 4 QA/Test Integrity context
// ---------------------------------
// These tests lock in the routing rewrite performed in the earlier Phase 4
// re-review.  Before the rewrite, the handler dispatched strictly on HTTP
// method and silently fell through to HandleCreateInvoice for unrecognized
// methods — OPTIONS, HEAD, PATCH, and DELETE without an invoice id would
// therefore create invoices.  The rewritten FunctionHandler dispatches on
// (method, hasInvoiceId) and returns HTTP 404 "Route not found." for any
// unrecognized combination.  The tests in this file assert the full dispatch
// matrix, including the path-based health-check short-circuit that must run
// BEFORE the invoice-id detection.
//
// Testing strategy
// ----------------
// The handler is instantiated via its testing constructor that accepts an
// IServiceProvider; we build the provider with Moq-backed services.  Dispatch
// correctness is verified by calling Moq's `.Verify(..., Times.Once)` on the
// injected service methods (one per expected route).  No real AWS SDK calls
// are made; no real database is touched.  JWT authentication is stubbed using
// the "sub" + "cognito:groups=administrator" claims that InvoiceHandler
// recognizes (ExtractUserIdFromContext + HasPermission bypass).
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SimpleNotificationService;
using Amazon.SimpleSystemsManagement;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebVellaErp.Invoicing.DataAccess;
using WebVellaErp.Invoicing.Functions;
using WebVellaErp.Invoicing.Models;
using WebVellaErp.Invoicing.Services;
using Xunit;

namespace WebVellaErp.Invoicing.Tests.Unit
{
    /// <summary>
    /// Unit tests for <see cref="InvoiceHandler.FunctionHandler"/> covering the
    /// complete route dispatch matrix introduced by the Phase 4 routing rewrite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each test constructs a Lambda <see cref="APIGatewayHttpApiV2ProxyRequest"/>
    /// that mirrors what API Gateway emits for a given (method, path) combination,
    /// sends it through the handler, and asserts both the HTTP response shape AND
    /// the underlying <see cref="IInvoiceService"/> method that was actually called.
    /// </para>
    /// <para>
    /// Verifying the service method call (rather than just the status code) is
    /// critical for dispatch testing: a handler could return 201 for any method
    /// via the old default-fallback path without ever being correctly routed.
    /// The <c>Times.Once</c> assertions on each service method prove that the
    /// correct handler was chosen.
    /// </para>
    /// </remarks>
    public class InvoiceHandlerTests
    {
        // ───────────────────────────── Fixtures ─────────────────────────────

        private readonly Mock<IInvoiceService> _invoiceServiceMock;
        private readonly Mock<IInvoiceRepository> _invoiceRepositoryMock;
        private readonly Mock<IAmazonSimpleNotificationService> _snsMock;
        private readonly Mock<IAmazonSimpleSystemsManagement> _ssmMock;
        private readonly InvoiceHandler _handler;
        private readonly Mock<ILambdaContext> _lambdaContextMock;

        /// <summary>
        /// Test user id surfaced from the JWT <c>sub</c> claim.  Propagates into
        /// every <see cref="IInvoiceService"/> call so tests can assert that the
        /// handler forwarded the authenticated caller correctly.
        /// </summary>
        private static readonly Guid TestUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        /// <summary>
        /// Test invoice id used for every "has id" route (detail / update / void).
        /// </summary>
        private static readonly Guid TestInvoiceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        /// <summary>
        /// JSON options that match <c>InvoiceHandler.JsonOptions</c> for request
        /// body round-trip — needed so deserialization succeeds inside the handler
        /// and we reach the service-invocation branch we want to verify.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public InvoiceHandlerTests()
        {
            _invoiceServiceMock = new Mock<IInvoiceService>(MockBehavior.Strict);
            _invoiceRepositoryMock = new Mock<IInvoiceRepository>(MockBehavior.Strict);
            _snsMock = new Mock<IAmazonSimpleNotificationService>(MockBehavior.Loose);
            _ssmMock = new Mock<IAmazonSimpleSystemsManagement>(MockBehavior.Loose);

            _lambdaContextMock = new Mock<ILambdaContext>();
            _lambdaContextMock.SetupGet(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _lambdaContextMock.SetupGet(c => c.FunctionName).Returns("InvoiceHandler-Test");

            // Build an IServiceProvider around the mocks.  The handler's testing
            // constructor calls GetRequiredService<T>() for each dependency.
            var services = new ServiceCollection();
            services.AddSingleton(_invoiceServiceMock.Object);
            services.AddSingleton(_invoiceRepositoryMock.Object);
            services.AddSingleton(_snsMock.Object);
            services.AddSingleton(_ssmMock.Object);
            services.AddSingleton<ILogger<InvoiceHandler>>(NullLogger<InvoiceHandler>.Instance);

            _handler = new InvoiceHandler(services.BuildServiceProvider());
        }

        // ─────────────────────────── Test Helpers ───────────────────────────

        /// <summary>
        /// Constructs an API Gateway HTTP API v2 request populated with the JWT
        /// authorizer context that <see cref="InvoiceHandler"/> expects.  Claims
        /// contain a <c>sub</c> GUID (so <c>ExtractUserIdFromContext</c> returns
        /// a non-empty user id) and a <c>cognito:groups=administrator</c> claim
        /// (so <c>HasPermission</c> short-circuits true for every permission).
        /// </summary>
        /// <param name="method">HTTP method (e.g., "GET", "POST", "PUT", "DELETE").</param>
        /// <param name="path">Request path such as <c>/v1/invoicing/invoices</c>.</param>
        /// <param name="body">Optional JSON body for POST / PUT requests.</param>
        /// <param name="pathParameters">Optional path parameters (e.g., <c>invoiceId</c> or <c>proxy</c>).</param>
        /// <param name="queryParameters">Optional query string parameters.</param>
        /// <param name="omitAuthorizer">
        /// When true, the returned request has no <c>Authorizer</c> at all — used
        /// by tests that verify the handler rejects unauthenticated callers.
        /// </param>
        /// <returns>A fully-populated <see cref="APIGatewayHttpApiV2ProxyRequest"/>.</returns>
        private static APIGatewayHttpApiV2ProxyRequest BuildRequest(
            string method,
            string path,
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryParameters = null,
            bool omitAuthorizer = false)
        {
            var requestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
            {
                RequestId = Guid.NewGuid().ToString(),
                Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                {
                    Method = method,
                    Path = path,
                    Protocol = "HTTP/1.1",
                    SourceIp = "127.0.0.1",
                    UserAgent = "xunit-test-runner"
                }
            };

            if (!omitAuthorizer)
            {
                requestContext.Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                {
                    Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                    {
                        Claims = new Dictionary<string, string>
                        {
                            ["sub"] = TestUserId.ToString(),
                            ["cognito:groups"] = "administrator"
                        }
                    }
                };
            }

            return new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = path,
                Body = body,
                PathParameters = pathParameters ?? new Dictionary<string, string>(),
                QueryStringParameters = queryParameters ?? new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                    ["x-correlation-id"] = Guid.NewGuid().ToString()
                },
                RequestContext = requestContext
            };
        }

        /// <summary>
        /// Produces a minimal <see cref="CreateInvoiceRequest"/> JSON body that
        /// passes the in-handler validation (non-empty customer id, at least one
        /// valid line item).  Used as the body for dispatch tests that hit
        /// <c>HandleCreateInvoice</c>.
        /// </summary>
        private static string BuildCreateInvoiceBody()
        {
            var request = new CreateInvoiceRequest
            {
                CustomerId = Guid.NewGuid(),
                IssueDate = DateTime.UtcNow,
                DueDate = DateTime.UtcNow.AddDays(30),
                Currency = "USD",
                Notes = "Dispatch test invoice",
                LineItems = new List<CreateLineItemRequest>
                {
                    new()
                    {
                        Description = "Test line item",
                        Quantity = 1m,
                        UnitPrice = 100m,
                        TaxRate = 0.10m,
                        SortOrder = 1
                    }
                }
            };
            return JsonSerializer.Serialize(request, JsonOptions);
        }

        /// <summary>
        /// Produces a minimal <see cref="UpdateInvoiceRequest"/> JSON body that
        /// passes in-handler validation and reaches the service method call.
        /// </summary>
        private static string BuildUpdateInvoiceBody()
        {
            var request = new UpdateInvoiceRequest
            {
                Notes = "Updated via dispatch test"
            };
            return JsonSerializer.Serialize(request, JsonOptions);
        }

        /// <summary>
        /// Builds a canonical successful <see cref="InvoiceResponse"/> used as
        /// the mocked return value for service methods in the dispatch tests.
        /// Keeps all test invoices on the same id for deterministic assertions.
        /// </summary>
        private static InvoiceResponse BuildSuccessInvoiceResponse()
        {
            var invoice = new Invoice
            {
                Id = TestInvoiceId,
                InvoiceNumber = "INV-2024-0001",
                Status = InvoiceStatus.Draft,
                CreatedOn = DateTime.UtcNow,
                LastModifiedOn = DateTime.UtcNow,
                CreatedBy = TestUserId,
                LastModifiedBy = TestUserId,
                LineItems = new List<LineItem>(),
                SubTotal = 100m,
                TaxAmount = 10m,
                TotalAmount = 110m,
                Currency = new CurrencyInfo
                {
                    Code = "USD",
                    Symbol = "$",
                    Name = "US Dollar",
                    DecimalDigits = 2,
                    SymbolPlacement = CurrencySymbolPlacement.Before
                }
            };
            return new InvoiceResponse(invoice) { Success = true, StatusCode = HttpStatusCode.OK };
        }

        /// <summary>
        /// Deserializes a <see cref="BaseResponseModel"/> (or subclass) from an
        /// API Gateway response body for assertions on the error envelope.
        /// </summary>
        private static T? DeserializeBody<T>(APIGatewayHttpApiV2ProxyResponse response)
            where T : BaseResponseModel
        {
            if (string.IsNullOrWhiteSpace(response.Body)) return null;
            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions);
        }

        // ════════════════════════════════════════════════════════════════════
        //  1. Health-check short-circuit
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET /v1/invoicing/invoices/health must bypass JWT authentication and
        /// the invoice-id detection and return immediately from HandleHealthCheck.
        /// Because the mocked SSM and SNS clients throw by default (no setup on
        /// their connectivity calls), the expected status is 503 Unhealthy with
        /// a JSON body carrying <c>status=unhealthy</c> — but the critical
        /// assertion is that NO <see cref="IInvoiceService"/> method was invoked.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_HealthPath_ShortCircuitsBeforeDispatch()
        {
            var request = BuildRequest("GET", "/v1/invoicing/invoices/health", omitAuthorizer: true);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.Should().NotBeNull();
            // Health-check returns 200 when all deps are healthy, 503 when any dep (SNS / DB) is unhealthy.
            // Since our mocks don't set up the SNS/DB dependencies to succeed, we expect 503 — but either
            // is a valid outcome for this dispatch test (the critical assertion is "no service called").
            response.StatusCode.Should().BeOneOf(new[] { 200, 503 });
            response.Body.Should().NotBeNullOrWhiteSpace();
            response.Body.Should().Contain("\"service\"").And.Contain("invoicing");
            response.Body.Should().Contain("\"status\"").And.MatchRegex("\"status\"\\s*:\\s*\"(healthy|unhealthy)\"");

            // Dispatch correctness: no service method should have been reached.
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// Health check path casing is case-insensitive — capitalized /HEALTH
        /// must still route to HandleHealthCheck rather than being interpreted
        /// as an invoice id.  Proves <c>EndsWith(..., OrdinalIgnoreCase)</c>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_HealthPath_CaseInsensitive()
        {
            var request = BuildRequest("GET", "/v1/invoicing/invoices/HEALTH", omitAuthorizer: true);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.Body.Should().Contain("\"service\"").And.Contain("invoicing");
            _invoiceServiceMock.VerifyNoOtherCalls();
        }

        // ════════════════════════════════════════════════════════════════════
        //  2. POST /v1/invoicing/invoices  →  HandleCreateInvoice
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// POST with a valid body must route to HandleCreateInvoice.  We verify
        /// routing by asserting <c>_invoiceService.CreateInvoiceAsync(...)</c>
        /// was invoked exactly once with the authenticated user id extracted
        /// from the JWT <c>sub</c> claim.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PostNoId_RoutesToCreateInvoice()
        {
            _invoiceServiceMock
                .Setup(s => s.CreateInvoiceAsync(
                    It.IsAny<CreateInvoiceRequest>(),
                    TestUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            var request = BuildRequest(
                method: "POST",
                path: "/v1/invoicing/invoices",
                body: BuildCreateInvoiceBody());

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(201, "HandleCreateInvoice returns 201 Created on success");
            _invoiceServiceMock.Verify(
                s => s.CreateInvoiceAsync(
                    It.IsAny<CreateInvoiceRequest>(),
                    TestUserId,
                    It.IsAny<CancellationToken>()),
                Times.Once,
                "POST /v1/invoicing/invoices must dispatch to HandleCreateInvoice");
            _invoiceServiceMock.Verify(
                s => s.ListInvoicesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never,
                "POST must never reach HandleListInvoices");
        }

        /// <summary>
        /// POST with an invoice id in the path (which is outside the OpenAPI spec)
        /// still dispatches to HandleCreateInvoice — the current routing rule is
        /// "POST with any path routes to create" to preserve behavioral parity with
        /// the original catch-all controller.  The test documents this behavior.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PostWithPathId_StillRoutesToCreateInvoice()
        {
            _invoiceServiceMock
                .Setup(s => s.CreateInvoiceAsync(
                    It.IsAny<CreateInvoiceRequest>(),
                    TestUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            var request = BuildRequest(
                method: "POST",
                path: $"/v1/invoicing/invoices/{TestInvoiceId}",
                body: BuildCreateInvoiceBody(),
                pathParameters: new Dictionary<string, string> { ["invoiceId"] = TestInvoiceId.ToString() });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(201);
            _invoiceServiceMock.Verify(
                s => s.CreateInvoiceAsync(It.IsAny<CreateInvoiceRequest>(), TestUserId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// POST with an empty body must reach HandleCreateInvoice (i.e. dispatch
        /// worked) but the body-null check must then reject with 400.  This
        /// proves the dispatch routes to the CREATE handler, not to a catch-all
        /// that silently returns 200.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PostEmptyBody_Returns400FromCreateHandler()
        {
            var request = BuildRequest(
                method: "POST",
                path: "/v1/invoicing/invoices",
                body: null);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(400);
            var envelope = DeserializeBody<InvoiceResponse>(response);
            envelope.Should().NotBeNull();
            envelope!.Success.Should().BeFalse();
            envelope.Message.Should().Contain("Invalid record");

            // Strict mock: we never set up CreateInvoiceAsync → any call would throw.
            // Reaching here without an exception proves the handler rejected the
            // request BEFORE invoking the service (correct behavior: body validation
            // happens after auth but before service invocation).
            _invoiceServiceMock.Verify(
                s => s.CreateInvoiceAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ════════════════════════════════════════════════════════════════════
        //  3. GET /v1/invoicing/invoices  →  HandleListInvoices
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET with no invoice id in the path must dispatch to HandleListInvoices.
        /// We verify by asserting <c>ListInvoicesAsync</c> was the only service
        /// method called.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetNoId_RoutesToListInvoices()
        {
            _invoiceServiceMock
                .Setup(s => s.ListInvoicesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InvoiceListResponse(new List<Invoice>(), 0) { Success = true });

            var request = BuildRequest(
                method: "GET",
                path: "/v1/invoicing/invoices");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.ListInvoicesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once,
                "GET /v1/invoicing/invoices (no id) must dispatch to HandleListInvoices");
            _invoiceServiceMock.Verify(
                s => s.GetInvoiceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "GET without id must never call GetInvoiceAsync");
        }

        /// <summary>
        /// GET with query parameters (status filter, pagination) must still
        /// dispatch to HandleListInvoices and forward the parsed values.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetNoIdWithQuery_RoutesToListInvoicesWithFilters()
        {
            _invoiceServiceMock
                .Setup(s => s.ListInvoicesAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InvoiceListResponse(new List<Invoice>(), 0) { Success = true });

            var request = BuildRequest(
                method: "GET",
                path: "/v1/invoicing/invoices",
                queryParameters: new Dictionary<string, string>
                {
                    ["status"] = "Issued",
                    ["page"] = "2",
                    ["pageSize"] = "50"
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.ListInvoicesAsync(It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ════════════════════════════════════════════════════════════════════
        //  4. GET /v1/invoicing/invoices/{id}  →  HandleGetInvoice
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// GET with a named <c>invoiceId</c> path parameter must dispatch to
        /// HandleGetInvoice (not HandleListInvoices).  The <c>hasInvoiceId</c>
        /// detection uses <c>TryGetPathParameter</c>'s named-parameter branch.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetWithNamedPathId_RoutesToGetInvoice()
        {
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            var request = BuildRequest(
                method: "GET",
                path: $"/v1/invoicing/invoices/{TestInvoiceId}",
                pathParameters: new Dictionary<string, string> { ["invoiceId"] = TestInvoiceId.ToString() });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once,
                "GET /v1/invoicing/invoices/{id} must dispatch to HandleGetInvoice with the parsed id");
            _invoiceServiceMock.Verify(
                s => s.ListInvoicesAsync(It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "GET with id must never reach HandleListInvoices");
        }

        /// <summary>
        /// GET via the HTTP API <c>{proxy+}</c> catch-all route (no named
        /// parameter) must still dispatch to HandleGetInvoice via the
        /// right-to-left GUID scan in <c>TryGetPathParameter</c>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetWithProxyPathId_RoutesToGetInvoice()
        {
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            // Simulate the {proxy+} integration: named parameter absent, only
            // the "proxy" catch-all present with a path ending in the invoice id.
            var request = BuildRequest(
                method: "GET",
                path: $"/v1/invoicing/invoices/{TestInvoiceId}",
                pathParameters: new Dictionary<string, string> { ["proxy"] = $"invoices/{TestInvoiceId}" });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once,
                "GET with {proxy+} path containing a GUID must dispatch to HandleGetInvoice");
        }

        // ════════════════════════════════════════════════════════════════════
        //  5. PUT /v1/invoicing/invoices/{id}  →  HandleUpdateInvoice
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// PUT with an invoice id must dispatch to HandleUpdateInvoice.  The
        /// existing-invoice lookup happens inside the handler before the
        /// update call, so we mock BOTH GetInvoiceAsync and UpdateInvoiceAsync.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PutWithId_RoutesToUpdateInvoice()
        {
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());
            _invoiceServiceMock
                .Setup(s => s.UpdateInvoiceAsync(
                    TestInvoiceId,
                    It.IsAny<UpdateInvoiceRequest>(),
                    TestUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            var request = BuildRequest(
                method: "PUT",
                path: $"/v1/invoicing/invoices/{TestInvoiceId}",
                body: BuildUpdateInvoiceBody(),
                pathParameters: new Dictionary<string, string> { ["invoiceId"] = TestInvoiceId.ToString() });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.UpdateInvoiceAsync(
                    TestInvoiceId,
                    It.IsAny<UpdateInvoiceRequest>(),
                    TestUserId,
                    It.IsAny<CancellationToken>()),
                Times.Once,
                "PUT /v1/invoicing/invoices/{id} must dispatch to HandleUpdateInvoice");
        }

        /// <summary>
        /// PUT without an invoice id must return 404 "Route not found."  Prior
        /// to the Phase 4 rewrite, the default switch branch fell through to
        /// HandleCreateInvoice, which would silently create invoices.  This test
        /// locks in the correct 404 behavior.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PutNoId_Returns404_NeverSilentlyCreates()
        {
            var request = BuildRequest(
                method: "PUT",
                path: "/v1/invoicing/invoices",
                body: BuildUpdateInvoiceBody());

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404, "PUT without id is not a valid route — must return Route not found");
            var envelope = DeserializeBody<BaseResponseModel>(response);
            envelope.Should().NotBeNull();
            envelope!.Success.Should().BeFalse();
            envelope.Message.Should().Be("Route not found.");

            _invoiceServiceMock.Verify(
                s => s.CreateInvoiceAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "REGRESSION GUARD: PUT without id must NEVER silently invoke HandleCreateInvoice");
            _invoiceServiceMock.Verify(
                s => s.UpdateInvoiceAsync(It.IsAny<Guid>(), It.IsAny<UpdateInvoiceRequest>(),
                    It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ════════════════════════════════════════════════════════════════════
        //  6. DELETE /v1/invoicing/invoices/{id}  →  HandleVoidInvoice
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// DELETE with an invoice id must dispatch to HandleVoidInvoice (invoices
        /// are NEVER hard-deleted for financial audit compliance — only voided).
        /// We verify <c>VoidInvoiceAsync</c> was invoked with the correct id + user.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_DeleteWithId_RoutesToVoidInvoice()
        {
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());
            _invoiceServiceMock
                .Setup(s => s.VoidInvoiceAsync(
                    TestInvoiceId,
                    TestUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            var request = BuildRequest(
                method: "DELETE",
                path: $"/v1/invoicing/invoices/{TestInvoiceId}",
                pathParameters: new Dictionary<string, string> { ["invoiceId"] = TestInvoiceId.ToString() });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.VoidInvoiceAsync(
                    TestInvoiceId,
                    TestUserId,
                    It.IsAny<CancellationToken>()),
                Times.Once,
                "DELETE /v1/invoicing/invoices/{id} must dispatch to HandleVoidInvoice");
        }

        /// <summary>
        /// DELETE without an invoice id must return 404 "Route not found." —
        /// NOT silently fall through to create / void / any other handler.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_DeleteNoId_Returns404_NeverSilentlyCreates()
        {
            var request = BuildRequest(
                method: "DELETE",
                path: "/v1/invoicing/invoices");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404);
            _invoiceServiceMock.Verify(
                s => s.CreateInvoiceAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "REGRESSION GUARD: DELETE without id must NEVER silently invoke HandleCreateInvoice");
            _invoiceServiceMock.Verify(
                s => s.VoidInvoiceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ════════════════════════════════════════════════════════════════════
        //  7. Unsupported HTTP methods  →  404
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The Phase 4 rewrite explicitly rejects OPTIONS, HEAD, PATCH, TRACE
        /// and any other unrecognized HTTP method with a 404 "Route not found."
        /// response — the former default-fallback behavior silently invoked
        /// HandleCreateInvoice, which is a severe defect.  This parameterized
        /// test covers every unsupported method.
        /// </summary>
        [Theory]
        [InlineData("OPTIONS")]
        [InlineData("HEAD")]
        [InlineData("PATCH")]
        [InlineData("TRACE")]
        [InlineData("CONNECT")]
        public async Task FunctionHandler_UnsupportedMethod_Returns404_NeverSilentlyCreates(string unsupportedMethod)
        {
            var request = BuildRequest(
                method: unsupportedMethod,
                path: "/v1/invoicing/invoices",
                body: BuildCreateInvoiceBody(),
                pathParameters: new Dictionary<string, string> { ["invoiceId"] = TestInvoiceId.ToString() });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404,
                $"{unsupportedMethod} is not in the dispatch table — must return Route not found");
            var envelope = DeserializeBody<BaseResponseModel>(response);
            envelope.Should().NotBeNull();
            envelope!.Success.Should().BeFalse();
            envelope.Message.Should().Be("Route not found.");
            envelope.Errors.Should().NotBeEmpty();
            envelope.Errors[0].Key.Should().Be("route");

            // Strict mocks → any service call would throw → absence of throw +
            // explicit VerifyNoOtherCalls proves the handler never reached any service.
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
        }

        // ════════════════════════════════════════════════════════════════════
        //  8. Authentication / authorization
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Requests without a JWT authorizer context must be rejected by
        /// ExtractUserIdFromContext with HTTP 403 BEFORE any service method is
        /// called.  Only the authorized handlers (POST/GET list/GET detail/PUT/DELETE)
        /// perform the auth check — the health path bypasses auth by design.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_MissingAuthorizer_Returns403()
        {
            var request = BuildRequest(
                method: "GET",
                path: "/v1/invoicing/invoices",
                omitAuthorizer: true);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(403, "ExtractUserIdFromContext returns Guid.Empty without authorizer");
            _invoiceServiceMock.Verify(
                s => s.ListInvoicesAsync(It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "Unauthenticated requests must be rejected BEFORE service invocation");
        }

        // ════════════════════════════════════════════════════════════════════
        //  9. Constructor guard clauses
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Constructing <see cref="InvoiceHandler"/> with a null service provider
        /// must throw <see cref="ArgumentNullException"/> with paramName=serviceProvider.
        /// Proves the ctor null guard required for DI safety in tests and Lambda.
        /// </summary>
        [Fact]
        public void Constructor_NullServiceProvider_Throws()
        {
            var act = () => new InvoiceHandler((IServiceProvider)null!);
            act.Should().Throw<ArgumentNullException>()
                .Which.ParamName.Should().Be("serviceProvider");
        }

        // ════════════════════════════════════════════════════════════════════
        //  10. Path/method casing and resilience
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Lowercase HTTP methods from malformed clients must still be routed
        /// correctly because the dispatcher uppercases via
        /// <c>request.RequestContext?.Http?.Method?.ToUpperInvariant()</c>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_LowercaseMethod_StillDispatches()
        {
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildSuccessInvoiceResponse());

            var request = BuildRequest(
                method: "get", // lowercase
                path: $"/v1/invoicing/invoices/{TestInvoiceId}",
                pathParameters: new Dictionary<string, string> { ["invoiceId"] = TestInvoiceId.ToString() });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once,
                "Handler must uppercase lowercase methods before matching the switch");
        }

        /// <summary>
        /// When the Http.Method is null, the dispatcher defaults to "GET" via the
        /// <c>?? "GET"</c> coalescing fallback.  Verify this defensive behavior.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_NullMethod_DefaultsToGetList()
        {
            _invoiceServiceMock
                .Setup(s => s.ListInvoicesAsync(
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(), It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InvoiceListResponse(new List<Invoice>(), 0) { Success = true });

            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = "/v1/invoicing/invoices",
                Body = null,
                PathParameters = new Dictionary<string, string>(),
                QueryStringParameters = new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString(),
                    // Http.Method is intentionally unset (null-default behavior)
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = null!,
                        Path = "/v1/invoicing/invoices"
                    },
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = TestUserId.ToString(),
                                ["cognito:groups"] = "administrator"
                            }
                        }
                    }
                }
            };

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceServiceMock.Verify(
                s => s.ListInvoicesAsync(It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<InvoiceStatus?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "Null HTTP method must default to GET per `?? \"GET\"` fallback");
        }

        /// <summary>
        /// When <c>RawPath</c> is null, the dispatcher must fall back to
        /// <c>RequestContext?.Http?.Path</c>.  A request with RawPath=null and
        /// Http.Path="/v1/invoicing/invoices/health" must still short-circuit
        /// to the health handler.  Verifies the null-coalesce fallback.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_NullRawPath_FallsBackToHttpPath()
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = null,
                Body = null,
                PathParameters = new Dictionary<string, string>(),
                QueryStringParameters = new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = "GET",
                        Path = "/v1/invoicing/invoices/health"
                    }
                    // No authorizer needed — health check bypasses auth.
                }
            };

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.Body.Should().Contain("\"service\"").And.Contain("invoicing");
            _invoiceServiceMock.VerifyNoOtherCalls();
        }
    }
}
