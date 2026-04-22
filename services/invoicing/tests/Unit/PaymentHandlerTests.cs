// ============================================================================
// File: services/invoicing/tests/Unit/PaymentHandlerTests.cs
//
// Unit tests for PaymentHandler.FunctionHandler path-based dispatch logic.
//
// Phase 4 QA/Test Integrity context
// ---------------------------------
// These tests lock in the routing rewrite performed in the earlier Phase 4
// re-review.  Before the rewrite, the handler dispatched strictly on HTTP
// method and silently fell through to HandleRecordPayment for unrecognized
// methods — OPTIONS, HEAD, PATCH, PUT, and DELETE would therefore create
// payments.  The rewritten FunctionHandler dispatches on (method, hasPaymentId)
// and returns HTTP 404 "Route not found." for any unrecognized combination.
// The tests in this file assert the full dispatch matrix, including the
// path-based health-check short-circuit that must run BEFORE the payment-id
// detection.
//
// Semantic differences from InvoiceHandlerTests
// ---------------------------------------------
// PaymentHandler has three dispatch-visible differences vs InvoiceHandler:
//   1. NO PUT / DELETE support — both must return 404 (regression guard).
//   2. ExtractCallerIdFromContext FALLS BACK to the system user GUID when the
//      authorizer is missing — it never returns 403.  (Auth is enforced at
//      API Gateway level via the JWT authorizer, not in the handler.)
//   3. GET list requires the invoiceId query parameter; without it the handler
//      returns 400 AFTER dispatching to HandleListPayments, but still routes
//      there (the 400 proves we reached HandleListPayments, not HandleGetPayment).
//
// Testing strategy
// ----------------
// The handler is instantiated via its testing constructor that accepts an
// IServiceProvider; we build the provider with Moq-backed services.  Dispatch
// correctness is verified primarily by calling Moq's `.Verify(..., Times.Once)`
// on the injected repository/service methods (one per expected route), and
// secondarily by inspecting the response status code and body.  No real AWS
// SDK calls are made; no real database is touched.
//
// The test constructor registers ALL THREE lazy-init services on the provider
// (IInvoiceService, IInvoiceRepository, IInvoiceEventPublisher) so that
// PaymentHandler's testing constructor sets `_initialized = true` and the
// lazy EnsureInitializedAsync path (which would hit SSM) is never triggered.
// ============================================================================

using System;
using System.Collections.Generic;
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
    /// Unit tests for <see cref="PaymentHandler.FunctionHandler"/> covering the
    /// complete route dispatch matrix introduced by the Phase 4 routing rewrite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each test constructs a Lambda <see cref="APIGatewayHttpApiV2ProxyRequest"/>
    /// that mirrors what API Gateway emits for a given (method, path) combination,
    /// sends it through the handler, and asserts both the HTTP response shape AND
    /// the underlying repository/service method that was actually called.
    /// </para>
    /// <para>
    /// Verifying the downstream method call (rather than just the status code) is
    /// critical for dispatch testing: a handler could return 201 for any method
    /// via the old default-fallback path without ever being correctly routed.
    /// The <c>Times.Once</c> assertions on each method prove that the correct
    /// handler was chosen.
    /// </para>
    /// </remarks>
    public class PaymentHandlerTests
    {
        // ───────────────────────────── Fixtures ─────────────────────────────

        private readonly Mock<IInvoiceService> _invoiceServiceMock;
        private readonly Mock<IInvoiceRepository> _invoiceRepositoryMock;
        private readonly Mock<IInvoiceEventPublisher> _eventPublisherMock;
        private readonly Mock<IAmazonSimpleNotificationService> _snsMock;
        private readonly Mock<IAmazonSimpleSystemsManagement> _ssmMock;
        private readonly PaymentHandler _handler;
        private readonly Mock<ILambdaContext> _lambdaContextMock;

        /// <summary>
        /// Test user id surfaced from the JWT <c>sub</c> claim.
        /// </summary>
        private static readonly Guid TestUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        /// <summary>
        /// Test payment id used for every "has id" route (detail).
        /// </summary>
        private static readonly Guid TestPaymentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        /// <summary>
        /// Test invoice id used when a payment refers to an invoice and for list queries.
        /// </summary>
        private static readonly Guid TestInvoiceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        /// <summary>
        /// JSON options that match <c>PaymentHandler.DeserializeOptions</c> for request
        /// body round-trip — needed so deserialization succeeds inside the handler
        /// and we reach the service-invocation branch we want to verify.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public PaymentHandlerTests()
        {
            // Strict on the business-logic dependencies so unconfigured calls fail loudly
            // (forces us to assert the exact dispatch surface). Loose on AWS SDK mocks.
            _invoiceServiceMock = new Mock<IInvoiceService>(MockBehavior.Strict);
            _invoiceRepositoryMock = new Mock<IInvoiceRepository>(MockBehavior.Strict);
            _eventPublisherMock = new Mock<IInvoiceEventPublisher>(MockBehavior.Loose);
            _snsMock = new Mock<IAmazonSimpleNotificationService>(MockBehavior.Loose);
            _ssmMock = new Mock<IAmazonSimpleSystemsManagement>(MockBehavior.Loose);

            _lambdaContextMock = new Mock<ILambdaContext>();
            _lambdaContextMock.SetupGet(c => c.AwsRequestId).Returns(Guid.NewGuid().ToString());
            _lambdaContextMock.SetupGet(c => c.FunctionName).Returns("PaymentHandler-Test");

            // Build an IServiceProvider around the mocks.  The handler's testing
            // constructor calls GetRequiredService<T>() for the AWS clients/logger and
            // GetService<T>() for IInvoiceService/IInvoiceRepository/IInvoiceEventPublisher.
            // Registering all three triggers `_initialized = true` which bypasses the
            // lazy SSM-backed initialization path entirely.
            var services = new ServiceCollection();
            services.AddSingleton(_invoiceServiceMock.Object);
            services.AddSingleton(_invoiceRepositoryMock.Object);
            services.AddSingleton(_eventPublisherMock.Object);
            services.AddSingleton(_snsMock.Object);
            services.AddSingleton(_ssmMock.Object);
            services.AddSingleton<ILogger<PaymentHandler>>(NullLogger<PaymentHandler>.Instance);

            _handler = new PaymentHandler(services.BuildServiceProvider());
        }

        // ─────────────────────────── Test Helpers ───────────────────────────

        /// <summary>
        /// Constructs an API Gateway HTTP API v2 request populated with the JWT
        /// authorizer context.  Claims contain a <c>sub</c> GUID so
        /// <c>ExtractCallerIdFromContext</c> returns a non-empty caller id.
        /// Unlike <see cref="InvoiceHandler"/>, <see cref="PaymentHandler"/> does
        /// NOT enforce role membership in the handler — JWT auth is enforced by
        /// API Gateway — so no <c>cognito:groups</c> claim is needed.
        /// </summary>
        /// <param name="method">HTTP method (e.g., "GET", "POST", "PUT", "DELETE").</param>
        /// <param name="path">Request path such as <c>/v1/invoicing/payments</c>.</param>
        /// <param name="body">Optional JSON body for POST requests.</param>
        /// <param name="pathParameters">Optional path parameters (e.g., <c>paymentId</c> or <c>proxy</c>).</param>
        /// <param name="queryParameters">Optional query string parameters.</param>
        /// <param name="omitAuthorizer">
        /// When true, the returned request has no <c>Authorizer</c> at all — used
        /// by tests that verify the handler still dispatches (falling back to the
        /// system user GUID) rather than short-circuiting.
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
                            ["sub"] = TestUserId.ToString()
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
        /// Produces a minimal <c>RecordPaymentRequest</c> JSON body that passes
        /// the in-handler validation (non-empty invoice id, positive amount).
        /// The JSON keys match the snake_case property names on the PRIVATE
        /// <c>RecordPaymentRequest</c> record nested in <see cref="PaymentHandler"/>.
        /// </summary>
        /// <param name="invoiceId">Target invoice id. Defaults to <see cref="TestInvoiceId"/>.</param>
        /// <param name="amount">Payment amount. Must be positive to pass validation.</param>
        /// <returns>Serialized JSON payload.</returns>
        private static string BuildRecordPaymentBody(Guid? invoiceId = null, decimal amount = 100m)
        {
            // The handler deserializes into its private RecordPaymentRequest record
            // (line 1081 of PaymentHandler.cs).  We build the same shape as a raw
            // dictionary so we don't depend on the private type.
            var payload = new Dictionary<string, object?>
            {
                ["invoice_id"] = (invoiceId ?? TestInvoiceId).ToString(),
                ["amount"] = amount,
                ["payment_date"] = DateTime.UtcNow,
                ["payment_method"] = "BankTransfer",
                ["reference_number"] = "REF-TEST-001",
                ["notes"] = "Dispatch test payment"
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        /// <summary>
        /// Builds a canonical <see cref="Invoice"/> entity used as the return value
        /// for <see cref="IInvoiceService.GetInvoiceAsync"/> when the handler needs
        /// to validate that the target invoice exists and is in an accepting state.
        /// Status is <see cref="InvoiceStatus.Issued"/> — the only status for which
        /// payments are accepted.
        /// </summary>
        private static InvoiceResponse BuildIssuedInvoiceResponse()
        {
            var invoice = new Invoice
            {
                Id = TestInvoiceId,
                InvoiceNumber = "INV-2024-0001",
                CustomerId = Guid.NewGuid(),
                Status = InvoiceStatus.Issued,
                IssueDate = DateTime.UtcNow.AddDays(-30),
                DueDate = DateTime.UtcNow.AddDays(7),
                SubTotal = 1000m,
                TaxAmount = 0m,
                TotalAmount = 1000m,
                CreatedOn = DateTime.UtcNow.AddDays(-30),
                LastModifiedOn = DateTime.UtcNow,
                CreatedBy = TestUserId,
                LastModifiedBy = TestUserId,
                LineItems = new List<LineItem>(),
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
        /// Builds a canonical <see cref="Payment"/> entity used as the return value
        /// for <see cref="IInvoiceRepository.GetPaymentAsync"/> and
        /// <see cref="IInvoiceRepository.CreatePaymentAsync"/> in dispatch tests.
        /// </summary>
        private static Payment BuildTestPayment() => new()
        {
            Id = TestPaymentId,
            InvoiceId = TestInvoiceId,
            Amount = 100m,
            PaymentDate = DateTime.UtcNow,
            PaymentMethod = PaymentMethod.BankTransfer,
            ReferenceNumber = "REF-TEST-001",
            Notes = "Dispatch test payment",
            CreatedBy = TestUserId,
            CreatedOn = DateTime.UtcNow
        };

        /// <summary>
        /// Deserializes a <see cref="BaseResponseModel"/> (or subclass) from an
        /// API Gateway response body using the canonical JSON options.
        /// </summary>
        private static T? DeserializeBody<T>(APIGatewayHttpApiV2ProxyResponse response)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(response.Body)) return null;
            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions);
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — Health check short-circuit
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The health-check path (<c>/v1/invoicing/payments/health</c>) must be
        /// routed to <see cref="PaymentHandler.HandleHealthCheck"/> BEFORE any
        /// payment-id extraction or business dispatch happens.  Neither the
        /// invoice service nor the invoice repository may be invoked.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_HealthPath_ShortCircuitsBeforeDispatch()
        {
            var request = BuildRequest("GET", "/v1/invoicing/payments/health");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.Should().NotBeNull();
            // Health-check returns 200 when all deps are healthy, 503 otherwise.
            // Since our mocks don't simulate healthy DB/SNS dependencies we expect
            // 503 — but either is a valid outcome: the critical assertion is
            // "no service/repository call was ever made".
            response.StatusCode.Should().BeOneOf(new[] { 200, 503 });
            response.Body.Should().NotBeNullOrWhiteSpace();

            // No business dispatch must have occurred on the health path.
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// The health path check is case-insensitive — the handler uses
        /// <see cref="StringComparison.OrdinalIgnoreCase"/>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_HealthPath_CaseInsensitive()
        {
            var request = BuildRequest("GET", "/v1/invoicing/payments/HEALTH");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.Should().NotBeNull();
            response.StatusCode.Should().BeOneOf(new[] { 200, 503 });

            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — POST routes to HandleRecordPayment
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>POST /v1/invoicing/payments</c> with a valid <c>RecordPaymentRequest</c>
        /// body must be dispatched to <see cref="PaymentHandler.HandleRecordPayment"/>.
        /// We verify the dispatch by asserting that the service's GetInvoiceAsync
        /// (the first dependency call inside HandleRecordPayment) was invoked
        /// exactly once with the invoice id extracted from the body.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PostNoId_RoutesToHandleRecordPayment()
        {
            // Arrange — set up the full happy path so HandleRecordPayment can
            // proceed through its dependency chain without throwing.
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildIssuedInvoiceResponse());

            _invoiceRepositoryMock
                .Setup(r => r.ListPaymentsForInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Payment>());

            _invoiceRepositoryMock
                .Setup(r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Payment p, CancellationToken _) => { p.Id = TestPaymentId; return p; });

            // Not fully paid -> MarkInvoicePaidAsync is NOT invoked for 100 of 1000.

            var request = BuildRequest(
                "POST",
                "/v1/invoicing/payments",
                BuildRecordPaymentBody(amount: 100m));

            // Act
            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            // Assert — 201 Created indicates HandleRecordPayment succeeded.
            response.StatusCode.Should().Be(201);

            // The definitive dispatch assertion: HandleRecordPayment's first
            // service call must have fired exactly once.
            _invoiceServiceMock.Verify(
                s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once,
                "HandleRecordPayment must call IInvoiceService.GetInvoiceAsync exactly once.");

            _invoiceRepositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "HandleRecordPayment must persist the payment via CreatePaymentAsync.");
        }

        /// <summary>
        /// <c>POST</c> with an empty body must be routed to
        /// <see cref="PaymentHandler.HandleRecordPayment"/> and rejected with 400
        /// — proving dispatch occurred even when validation fails afterward.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PostEmptyBody_Returns400FromRecordHandler()
        {
            var request = BuildRequest("POST", "/v1/invoicing/payments", body: string.Empty);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(400);
            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("body", Exactly.Once());

            // HandleRecordPayment rejected before calling any dependencies.
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// <c>POST</c> with invoice_id = empty GUID must route to HandleRecordPayment
        /// and be rejected by its pre-hook validation with 400 — not silently routed
        /// elsewhere.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PostInvalidInvoiceId_Returns400FromRecordHandler()
        {
            var body = BuildRecordPaymentBody(invoiceId: Guid.Empty);
            var request = BuildRequest("POST", "/v1/invoicing/payments", body);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(400);

            // No service calls yet — validation runs before GetInvoiceAsync.
            _invoiceServiceMock.VerifyNoOtherCalls();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — GET (list) routes to HandleListPayments
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>GET /v1/invoicing/payments?invoiceId={guid}</c> (no path id) must
        /// route to <see cref="PaymentHandler.HandleListPayments"/>.  Verified
        /// by asserting that <see cref="IInvoiceRepository.ListPaymentsForInvoiceAsync"/>
        /// fires exactly once with the query-string invoice id.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetNoIdWithInvoiceIdQuery_RoutesToListPayments()
        {
            _invoiceRepositoryMock
                .Setup(r => r.ListPaymentsForInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Payment> { BuildTestPayment() });

            var request = BuildRequest(
                "GET",
                "/v1/invoicing/payments",
                queryParameters: new Dictionary<string, string>
                {
                    ["invoiceId"] = TestInvoiceId.ToString()
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);

            _invoiceRepositoryMock.Verify(
                r => r.ListPaymentsForInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once,
                "HandleListPayments must call IInvoiceRepository.ListPaymentsForInvoiceAsync exactly once.");

            // Detail fetch must never happen for list route.
            _invoiceRepositoryMock.Verify(
                r => r.GetPaymentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// <c>GET /v1/invoicing/payments</c> WITHOUT the invoiceId query parameter
        /// must route to HandleListPayments (not HandleGetPayment, not HandleRecordPayment)
        /// and return 400 — proving the list dispatch branch was taken.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetNoIdNoQuery_RoutesToListPaymentsWithValidationError()
        {
            var request = BuildRequest("GET", "/v1/invoicing/payments");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            // HandleListPayments rejects with 400 when invoiceId query param is missing.
            response.StatusCode.Should().Be(400);
            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("invoiceId", Exactly.Once());

            // Critical regression guards: ListPayments validation rejected BEFORE
            // any repo call, AND GetPayment/CreatePayment were never touched.
            _invoiceRepositoryMock.Verify(
                r => r.ListPaymentsForInvoiceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _invoiceRepositoryMock.Verify(
                r => r.GetPaymentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _invoiceRepositoryMock.Verify(
                r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// <c>GET /v1/invoicing/payments?invoiceId=...&amp;page=2&amp;pageSize=5</c>
        /// must route to HandleListPayments which honors pagination query params.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetListWithPagination_RoutesToListPayments()
        {
            var payments = Enumerable.Range(0, 15)
                .Select(i => new Payment
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = TestInvoiceId,
                    Amount = 10m + i,
                    PaymentDate = DateTime.UtcNow.AddDays(-i),
                    PaymentMethod = PaymentMethod.BankTransfer,
                    ReferenceNumber = $"REF-{i}",
                    Notes = string.Empty,
                    CreatedBy = TestUserId,
                    CreatedOn = DateTime.UtcNow
                })
                .ToList();

            _invoiceRepositoryMock
                .Setup(r => r.ListPaymentsForInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(payments);

            var request = BuildRequest(
                "GET",
                "/v1/invoicing/payments",
                queryParameters: new Dictionary<string, string>
                {
                    ["invoiceId"] = TestInvoiceId.ToString(),
                    ["page"] = "2",
                    ["pageSize"] = "5"
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceRepositoryMock.Verify(
                r => r.ListPaymentsForInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — GET with id routes to HandleGetPayment
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>GET /v1/invoicing/payments/{paymentId}</c> with a named
        /// <c>paymentId</c> path parameter must route to
        /// <see cref="PaymentHandler.HandleGetPayment"/>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetWithNamedPathId_RoutesToGetPayment()
        {
            _invoiceRepositoryMock
                .Setup(r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildTestPayment());

            var request = BuildRequest(
                "GET",
                $"/v1/invoicing/payments/{TestPaymentId}",
                pathParameters: new Dictionary<string, string>
                {
                    ["paymentId"] = TestPaymentId.ToString()
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);

            _invoiceRepositoryMock.Verify(
                r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()),
                Times.Once,
                "HandleGetPayment must call IInvoiceRepository.GetPaymentAsync exactly once.");

            // List route must never be dispatched.
            _invoiceRepositoryMock.Verify(
                r => r.ListPaymentsForInvoiceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// <c>GET</c> with a <c>{proxy+}</c> catch-all path must also dispatch
        /// to <see cref="PaymentHandler.HandleGetPayment"/> via the segment-scan
        /// fallback in <c>TryExtractPaymentId</c>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetWithProxyPathId_RoutesToGetPayment()
        {
            _invoiceRepositoryMock
                .Setup(r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildTestPayment());

            var request = BuildRequest(
                "GET",
                $"/v1/invoicing/payments/{TestPaymentId}",
                pathParameters: new Dictionary<string, string>
                {
                    ["proxy"] = TestPaymentId.ToString()
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceRepositoryMock.Verify(
                r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// <c>GET</c> with a <c>{proxy+}</c> catch-all carrying <c>payments/{id}</c>
        /// (multi-segment proxy) must still extract the GUID by scanning segments
        /// right-to-left and reach <see cref="PaymentHandler.HandleGetPayment"/>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_GetWithMultiSegmentProxyPath_RoutesToGetPayment()
        {
            _invoiceRepositoryMock
                .Setup(r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildTestPayment());

            var request = BuildRequest(
                "GET",
                $"/v1/invoicing/payments/{TestPaymentId}",
                pathParameters: new Dictionary<string, string>
                {
                    ["proxy"] = $"payments/{TestPaymentId}"
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceRepositoryMock.Verify(
                r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — Unsupported methods return 404 (regression guards)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>DELETE /v1/invoicing/payments/{paymentId}</c> must return 404 —
        /// PaymentHandler does NOT support payment deletion.  This is the primary
        /// regression guard: the prior dispatch logic silently fell through to
        /// HandleRecordPayment for any method it didn't recognize.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_DeleteWithId_Returns404_NeverSilentlyRecords()
        {
            var request = BuildRequest(
                "DELETE",
                $"/v1/invoicing/payments/{TestPaymentId}",
                pathParameters: new Dictionary<string, string>
                {
                    ["paymentId"] = TestPaymentId.ToString()
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404);
            var body = DeserializeBody<BaseResponseModel>(response);
            body.Should().NotBeNull();
            body!.Success.Should().BeFalse();
            body.Message.Should().Contain("Route", Exactly.Once());

            // CRITICAL regression guard — no business methods must ever be invoked.
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// <c>PUT /v1/invoicing/payments/{paymentId}</c> must return 404 —
        /// PaymentHandler does NOT support payment updates per the OpenAPI spec.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PutWithId_Returns404_NeverSilentlyRecords()
        {
            var request = BuildRequest(
                "PUT",
                $"/v1/invoicing/payments/{TestPaymentId}",
                body: BuildRecordPaymentBody(),
                pathParameters: new Dictionary<string, string>
                {
                    ["paymentId"] = TestPaymentId.ToString()
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404);

            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// Exhaustive check that every unsupported HTTP verb returns 404 rather
        /// than silently routing to HandleRecordPayment.  Covers OPTIONS, HEAD,
        /// PATCH, TRACE, and CONNECT — the prior dispatch logic would have
        /// routed all of these to the payment creation branch.
        /// </summary>
        [Theory]
        [InlineData("OPTIONS")]
        [InlineData("HEAD")]
        [InlineData("PATCH")]
        [InlineData("TRACE")]
        [InlineData("CONNECT")]
        public async Task FunctionHandler_UnsupportedMethod_Returns404_NeverSilentlyRecords(string method)
        {
            var request = BuildRequest(method, "/v1/invoicing/payments");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404);

            // No business method should be called for any unsupported method.
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// <c>DELETE</c> without a path id must also return 404 (not silently
        /// create a payment).  Critical regression guard complementing
        /// <see cref="FunctionHandler_DeleteWithId_Returns404_NeverSilentlyRecords"/>.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_DeleteNoId_Returns404_NeverSilentlyRecords()
        {
            var request = BuildRequest("DELETE", "/v1/invoicing/payments");

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404);
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        /// <summary>
        /// <c>PUT</c> without a path id must also return 404.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_PutNoId_Returns404_NeverSilentlyRecords()
        {
            var request = BuildRequest("PUT", "/v1/invoicing/payments", body: BuildRecordPaymentBody());

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(404);
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
            _eventPublisherMock.VerifyNoOtherCalls();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — Authorization behavior (differs from InvoiceHandler)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Unlike InvoiceHandler, PaymentHandler's <c>ExtractCallerIdFromContext</c>
        /// falls back to the system administrator GUID when the authorizer is
        /// missing rather than returning 403.  (JWT enforcement happens at the
        /// API Gateway level, not in the handler.)  This test confirms dispatch
        /// still proceeds to HandleRecordPayment even without an authorizer.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_MissingAuthorizer_StillDispatches()
        {
            _invoiceServiceMock
                .Setup(s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildIssuedInvoiceResponse());

            _invoiceRepositoryMock
                .Setup(r => r.ListPaymentsForInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<Payment>());

            _invoiceRepositoryMock
                .Setup(r => r.CreatePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Payment p, CancellationToken _) => { p.Id = TestPaymentId; return p; });

            var request = BuildRequest(
                "POST",
                "/v1/invoicing/payments",
                BuildRecordPaymentBody(amount: 100m),
                omitAuthorizer: true);

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            // 201 proves HandleRecordPayment ran.  PaymentHandler falls back to
            // the system user GUID when the authorizer is absent.
            response.StatusCode.Should().Be(201);
            _invoiceServiceMock.Verify(
                s => s.GetInvoiceAsync(TestInvoiceId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ════════════════════════════════════════════════════════════════════
        //  Tests — Edge cases and defensive behavior
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>null</c> service provider passed to the testing constructor must
        /// throw <see cref="ArgumentNullException"/> immediately.
        /// </summary>
        [Fact]
        public void Constructor_NullServiceProvider_Throws()
        {
            Action act = () => new PaymentHandler((IServiceProvider)null!);

            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("serviceProvider");
        }

        /// <summary>
        /// HTTP method is normalized to upper-case before the switch statement.
        /// A lowercase <c>get</c> must dispatch to the GET branch exactly as
        /// the canonical <c>GET</c> does.
        /// </summary>
        [Fact]
        public async Task FunctionHandler_LowercaseMethod_StillDispatches()
        {
            _invoiceRepositoryMock
                .Setup(r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(BuildTestPayment());

            var request = BuildRequest(
                "get",
                $"/v1/invoicing/payments/{TestPaymentId}",
                pathParameters: new Dictionary<string, string>
                {
                    ["paymentId"] = TestPaymentId.ToString()
                });

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            response.StatusCode.Should().Be(200);
            _invoiceRepositoryMock.Verify(
                r => r.GetPaymentAsync(TestPaymentId, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// A null HTTP method defaults to <c>GET</c> which without a path id
        /// routes to HandleListPayments (and fails its invoiceId validation
        /// since no query param was provided — returning 400).
        /// </summary>
        [Fact]
        public async Task FunctionHandler_NullMethod_DefaultsToGetList()
        {
            // Build a request with a null HTTP method
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                RawPath = "/v1/invoicing/payments",
                Body = null,
                PathParameters = new Dictionary<string, string>(),
                QueryStringParameters = new Dictionary<string, string>(),
                Headers = new Dictionary<string, string>(),
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription
                    {
                        Method = null!,
                        Path = "/v1/invoicing/payments"
                    }
                }
            };

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            // HandleListPayments rejects 400 when invoiceId query param is missing,
            // proving the list dispatch branch was taken (not a 404 "route not found").
            response.StatusCode.Should().Be(400);
            _invoiceRepositoryMock.Verify(
                r => r.ListPaymentsForInvoiceAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// When <c>RawPath</c> is null, the handler must fall back to
        /// <c>request.RequestContext.Http.Path</c> — both the health-check
        /// short-circuit and the dispatch logic must continue to work.
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
                        Path = "/v1/invoicing/payments/health"
                    }
                }
            };

            var response = await _handler.FunctionHandler(request, _lambdaContextMock.Object);

            // Health check short-circuit activated via fallback path.
            response.StatusCode.Should().BeOneOf(new[] { 200, 503 });
            _invoiceServiceMock.VerifyNoOtherCalls();
            _invoiceRepositoryMock.VerifyNoOtherCalls();
        }
    }
}
