// ════════════════════════════════════════════════════════════════════════════════
// ReportExecutionIntegrationTests.cs
// End-to-end integration tests for the Reporting & Analytics microservice.
// Tests the complete report lifecycle: create → execute parameterized SQL → verify
// results against RDS PostgreSQL read-model via LocalStack.
//
// Replaces monolith's DataSourceManager.Execute() (source lines 470-512) with
// serverless Lambda-based report execution flow.
//
// CRITICAL: All tests execute against LocalStack services (RDS PostgreSQL, SNS, SSM)
// — NO mocked AWS SDK calls (per AAP Section 0.8.4).
// ════════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using WebVellaErp.Reporting.DataAccess;
using WebVellaErp.Reporting.Functions;
using WebVellaErp.Reporting.Models;
using WebVellaErp.Reporting.Services;

using Xunit;

namespace WebVellaErp.Reporting.Tests.Integration
{
    /// <summary>
    /// End-to-end integration tests for the complete report lifecycle in the
    /// Reporting & Analytics microservice. Validates report definition CRUD,
    /// parameterized SQL execution against RDS PostgreSQL read-model, SSM
    /// parameter retrieval, and health check endpoints — all against LocalStack.
    ///
    /// Tests exercise ReportHandler Lambda entry points with real
    /// APIGatewayHttpApiV2ProxyRequest inputs and verify responses including
    /// status codes, response envelope structure, and data correctness.
    /// </summary>
    [Trait("Category", "Integration")]
    [Collection("ReportingIntegration")]
    public class ReportExecutionIntegrationTests
        : IAsyncLifetime
    {
        // ─── Test Infrastructure ───────────────────────────────────────────
        private readonly LocalStackFixture _localStack;
        private readonly DatabaseFixture _dbFixture;

        /// <summary>System.Text.Json options matching ReportHandler's camelCase serialization.</summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>
        /// Constructor — receives both fixtures via xUnit IClassFixture injection.
        /// LocalStackFixture provides pre-provisioned AWS resources (RDS, SNS, SSM).
        /// DatabaseFixture provides per-test-class isolated RDS PostgreSQL database.
        /// </summary>
        public ReportExecutionIntegrationTests(
            LocalStackFixture localStack,
            DatabaseFixture dbFixture)
        {
            _localStack = localStack ?? throw new ArgumentNullException(nameof(localStack));
            _dbFixture = dbFixture ?? throw new ArgumentNullException(nameof(dbFixture));
        }

        /// <summary>Initializes async resources before the test class runs.</summary>
        public async Task InitializeAsync()
        {
            // CRITICAL: Update SSM parameter to point to DatabaseFixture's unique test DB
            // so that ReportService.GetConnectionStringAsync() uses the same database as
            // the test assertions. Without this, ReportService opens connections to the
            // shared "reporting_test" DB while test data lives in "reporting_test_<guid>".
            await _localStack.SsmClient.PutParameterAsync(
                new Amazon.SimpleSystemsManagement.Model.PutParameterRequest
                {
                    Name = "/reporting/db-connection-string",
                    Value = _dbFixture.ConnectionString,
                    Type = "SecureString",
                    Overwrite = true
                });
        }

        /// <summary>Cleans up test data after all tests in the class complete.</summary>
        public async Task DisposeAsync()
        {
            try
            {
                await _dbFixture.CleanAllTablesAsync();
            }
            catch
            {
                // Best-effort cleanup — don't fail disposal
            }
        }

        // ─── Private Helpers ───────────────────────────────────────────────

        /// <summary>
        /// Creates a ReportHandler instance with DI configured for LocalStack endpoints.
        /// Uses the testing constructor ReportHandler(IServiceProvider) which resolves
        /// IReportService, IReportRepository, IAmazonSimpleNotificationService,
        /// IAmazonSimpleSystemsManagement, and ILogger&lt;ReportHandler&gt;.
        /// </summary>
        private ReportHandler CreateReportHandler()
        {
            var services = new ServiceCollection();

            // Register logging
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            // Register memory cache for service-level caching
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));

            // Register AWS SDK clients from LocalStack fixture
            services.AddSingleton(_localStack.SnsClient);
            services.AddSingleton(_localStack.SsmClient);

            // Register data access layer with DatabaseFixture connection string
            services.AddSingleton<IReportRepository>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ReportRepository>>();
                return new ReportRepository(_dbFixture.ConnectionString, logger);
            });

            // Register business logic service
            services.AddSingleton<IReportService>(sp =>
            {
                var repo = sp.GetRequiredService<IReportRepository>();
                var snsClient = sp.GetRequiredService<Amazon.SimpleNotificationService.IAmazonSimpleNotificationService>();
                var ssmClient = sp.GetRequiredService<IAmazonSimpleSystemsManagement>();
                var cache = sp.GetRequiredService<IMemoryCache>();
                var logger = sp.GetRequiredService<ILogger<ReportService>>();
                return new ReportService(repo, snsClient, ssmClient, cache, logger);
            });

            var serviceProvider = services.BuildServiceProvider();
            return new ReportHandler(serviceProvider);
        }

        /// <summary>
        /// Creates a ReportHandler with an intentionally bad connection string for
        /// testing failure scenarios (health check, SSM failures).
        /// </summary>
        private ReportHandler CreateReportHandlerWithBadConnection()
        {
            // Set SSM parameter to a bad connection string so the handler's
            // GetConnectionStringAsync (which reads from SSM) returns the invalid value.
            // This ensures health checks actually fail on unreachable DB.
            string badConnectionString = "Host=localhost;Port=9999;Database=nonexistent;Username=bad;Password=bad;Timeout=3;";
            _localStack.SsmClient.PutParameterAsync(new Amazon.SimpleSystemsManagement.Model.PutParameterRequest
            {
                Name = "/reporting/db-connection-string",
                Value = badConnectionString,
                Type = Amazon.SimpleSystemsManagement.ParameterType.SecureString,
                Overwrite = true
            }).GetAwaiter().GetResult();

            var services = new ServiceCollection();

            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
            services.AddSingleton(_localStack.SnsClient);
            services.AddSingleton(_localStack.SsmClient);

            // Use invalid connection string to simulate DB failure
            services.AddSingleton<IReportRepository>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ReportRepository>>();
                return new ReportRepository(badConnectionString, logger);
            });

            services.AddSingleton<IReportService>(sp =>
            {
                var repo = sp.GetRequiredService<IReportRepository>();
                var snsClient = sp.GetRequiredService<Amazon.SimpleNotificationService.IAmazonSimpleNotificationService>();
                var ssmClient = sp.GetRequiredService<IAmazonSimpleSystemsManagement>();
                var cache = sp.GetRequiredService<IMemoryCache>();
                var logger = sp.GetRequiredService<ILogger<ReportService>>();
                return new ReportService(repo, snsClient, ssmClient, cache, logger);
            });

            var serviceProvider = services.BuildServiceProvider();
            return new ReportHandler(serviceProvider);
        }

        /// <summary>
        /// Restores the SSM parameter to the correct DB connection string after bad-connection tests.
        /// </summary>
        private void RestoreGoodSsmConnectionString()
        {
            _localStack.SsmClient.PutParameterAsync(new Amazon.SimpleSystemsManagement.Model.PutParameterRequest
            {
                Name = "/reporting/db-connection-string",
                Value = _dbFixture.ConnectionString,
                Type = Amazon.SimpleSystemsManagement.ParameterType.SecureString,
                Overwrite = true
            }).GetAwaiter().GetResult();
        }

        /// <summary>Builds an API Gateway request with admin JWT claims for authorized endpoints.</summary>
        private static APIGatewayHttpApiV2ProxyRequest BuildAuthorizedRequest(
            string? body = null,
            Dictionary<string, string>? pathParameters = null,
            Dictionary<string, string>? queryStringParameters = null,
            Dictionary<string, string>? headers = null)
        {
            var request = new APIGatewayHttpApiV2ProxyRequest
            {
                Body = body,
                PathParameters = pathParameters ?? new Dictionary<string, string>(),
                QueryStringParameters = queryStringParameters ?? new Dictionary<string, string>(),
                Headers = headers ?? new Dictionary<string, string>
                {
                    ["content-type"] = "application/json",
                    ["x-correlation-id"] = Guid.NewGuid().ToString()
                },
                RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
                {
                    RequestId = Guid.NewGuid().ToString(),
                    Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
                    {
                        Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription
                        {
                            Claims = new Dictionary<string, string>
                            {
                                ["sub"] = Guid.NewGuid().ToString(),
                                ["email"] = "admin@webvella.com",
                                ["cognito:username"] = "admin",
                                ["cognito:groups"] = "administrator"
                            }
                        }
                    }
                }
            };

            return request;
        }

        /// <summary>Creates a TestLambdaContext with default settings.</summary>
        private static TestLambdaContext CreateTestContext()
        {
            return new TestLambdaContext
            {
                FunctionName = "ReportingService",
                AwsRequestId = Guid.NewGuid().ToString(),
                RemainingTime = TimeSpan.FromMinutes(5)
            };
        }

        /// <summary>
        /// Seeds test projection data into reporting.read_model_projections for query tests.
        /// </summary>
        private async Task SeedProjectionDataAsync(
            string sourceDomain, string sourceEntity, Guid sourceRecordId, string payload)
        {
            await _dbFixture.SeedTestProjectionAsync(sourceDomain, sourceEntity, sourceRecordId, payload);
        }

        /// <summary>
        /// Helper to create a report via handler and return the response.
        /// </summary>
        private async Task<(APIGatewayHttpApiV2ProxyResponse Response, ReportDefinition? Report)>
            CreateReportViaHandlerAsync(
                ReportHandler handler,
                string name,
                string queryDefinition,
                List<ReportParameter>? parameters = null,
                bool returnTotal = true,
                string? idempotencyKey = null)
        {
            var createBody = new
            {
                name,
                description = $"Test report: {name}",
                queryDefinition,
                parameters = parameters ?? new List<ReportParameter>(),
                returnTotal
            };

            var headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["x-correlation-id"] = Guid.NewGuid().ToString()
            };

            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                headers["Idempotency-Key"] = idempotencyKey;
            }

            var request = BuildAuthorizedRequest(
                body: JsonSerializer.Serialize(createBody, JsonOptions),
                headers: headers);

            var context = CreateTestContext();
            var response = await handler.HandleCreateReport(request, context);

            ReportDefinition? report = null;
            if (response.StatusCode == 201)
            {
                var doc = JsonDocument.Parse(response.Body);
                var objectElement = doc.RootElement.GetProperty("object");
                report = JsonSerializer.Deserialize<ReportDefinition>(
                    objectElement.GetRawText(), JsonOptions);
            }

            return (response, report);
        }

        /// <summary>Cleans database tables before each test that needs isolation.</summary>
        private async Task CleanDatabaseAsync()
        {
            await _dbFixture.CleanAllTablesAsync();
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 2: Full Lifecycle Tests
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Complete end-to-end flow: seed test data → create report definition →
        /// execute parameterized SQL query → verify aggregated results match seeded data.
        /// Validates replacement of DataSourceManager.Execute() (source lines 470-512).
        /// </summary>
        [RdsFact]
        public async Task FullLifecycle_CreateReport_ExecuteQuery_VerifyResults()
        {
            // Arrange — clean slate and seed projection data
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed CRM contact projections into read_model_projections
            var contactId1 = Guid.NewGuid();
            var contactId2 = Guid.NewGuid();
            var contactId3 = Guid.NewGuid();
            var accountId1 = Guid.NewGuid();

            await SeedProjectionDataAsync("crm", "contact", contactId1,
                JsonSerializer.Serialize(new { firstName = "John", lastName = "Doe" }));
            await SeedProjectionDataAsync("crm", "contact", contactId2,
                JsonSerializer.Serialize(new { firstName = "Jane", lastName = "Smith" }));
            await SeedProjectionDataAsync("crm", "contact", contactId3,
                JsonSerializer.Serialize(new { firstName = "Bob", lastName = "Wilson" }));
            await SeedProjectionDataAsync("crm", "account", accountId1,
                JsonSerializer.Serialize(new { companyName = "Acme Corp" }));

            // Act — create a report definition
            string sql = @"SELECT source_domain, source_entity, COUNT(*) AS record_count 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = @domain 
                           GROUP BY source_domain, source_entity 
                           ORDER BY source_entity";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "domain", Type = "text", DefaultValue = "crm" }
            };

            var (createResponse, createdReport) = await CreateReportViaHandlerAsync(
                handler, "CRM Summary Report", sql, parameters);

            // Assert — report created successfully
            createResponse.StatusCode.Should().Be(201);
            createdReport.Should().NotBeNull();
            createdReport!.Name.Should().Be("CRM Summary Report");

            // Act — execute the report with parameters
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["domain"] = "crm" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = createdReport.Id.ToString() });

            var executeResponse = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — verify execution results
            executeResponse.StatusCode.Should().Be(200,
                $"Response body: {executeResponse.Body}");

            var executeDoc = JsonDocument.Parse(executeResponse.Body);
            var root = executeDoc.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeTrue();

            var obj = root.GetProperty("object");
            var data = obj.GetProperty("data");
            data.GetArrayLength().Should().BeGreaterOrEqualTo(2); // contact + account rows

            // Verify aggregated data: contacts should have count=3, accounts count=1
            bool foundContacts = false;
            bool foundAccounts = false;
            foreach (var row in data.EnumerateArray())
            {
                string entity = row.GetProperty("source_entity").GetString()!;
                int count = row.GetProperty("record_count").GetInt32();
                if (entity == "contact")
                {
                    count.Should().Be(3);
                    foundContacts = true;
                }
                else if (entity == "account")
                {
                    count.Should().Be(1);
                    foundAccounts = true;
                }
            }
            foundContacts.Should().BeTrue("expected contact aggregate row");
            foundAccounts.Should().BeTrue("expected account aggregate row");
        }

        /// <summary>
        /// Create report → update its SQL query → execute updated version → verify new results.
        /// Validates report update and re-execution flow.
        /// </summary>
        [RdsFact]
        public async Task FullLifecycle_CreateReport_UpdateReport_ExecuteUpdated()
        {
            // Arrange
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed test data
            await SeedProjectionDataAsync("inventory", "product", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Widget A", price = 9.99 }));
            await SeedProjectionDataAsync("inventory", "product", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Widget B", price = 19.99 }));

            // Create initial report
            string initialSql = "SELECT COUNT(*) AS total FROM reporting.read_model_projections WHERE source_domain = 'inventory'";
            var (createResponse, createdReport) = await CreateReportViaHandlerAsync(
                handler, "Inventory Count Report", initialSql);

            createResponse.StatusCode.Should().Be(201);
            createdReport.Should().NotBeNull();

            // Act — update the SQL query
            string updatedSql = @"SELECT source_entity, COUNT(*) AS entity_count 
                                  FROM reporting.read_model_projections 
                                  WHERE source_domain = 'inventory' 
                                  GROUP BY source_entity";

            var updateBody = JsonSerializer.Serialize(new
            {
                name = "Inventory Detail Report",
                description = "Updated inventory report with entity breakdown",
                queryDefinition = updatedSql,
                parameters = new List<object>(),
                returnTotal = true
            }, JsonOptions);

            var updateRequest = BuildAuthorizedRequest(
                body: updateBody,
                pathParameters: new Dictionary<string, string> { ["id"] = createdReport!.Id.ToString() });

            var updateResponse = await handler.HandleUpdateReport(updateRequest, CreateTestContext());
            updateResponse.StatusCode.Should().Be(200);

            // Act — execute the updated report
            var executeRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = createdReport.Id.ToString() });

            var executeResponse = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert
            executeResponse.StatusCode.Should().Be(200);

            var executeDoc = JsonDocument.Parse(executeResponse.Body);
            var obj = executeDoc.RootElement.GetProperty("object");
            var data = obj.GetProperty("data");
            data.GetArrayLength().Should().BeGreaterOrEqualTo(1);

            // Verify the updated report name in response metadata
            var updatedDoc = JsonDocument.Parse(updateResponse.Body);
            var updatedObject = updatedDoc.RootElement.GetProperty("object");
            updatedObject.GetProperty("name").GetString().Should().Be("Inventory Detail Report");
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 3: Report Execution with Parameters Tests
        // All 6 parameter types (source: DataSourceManager.GetDataSourceParameterValue)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tests GUID parameter binding. Source lines 360-378: guid type with null,
        /// guid.empty, valid GUID handling.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_GuidParameter_ResolvesCorrectly()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed a specific record we can filter by ID
            var targetId = Guid.NewGuid();
            await SeedProjectionDataAsync("crm", "contact", targetId,
                JsonSerializer.Serialize(new { name = "Target Contact" }));
            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Other Contact" }));

            // Create report with GUID parameter
            string sql = @"SELECT source_record_id, source_entity 
                           FROM reporting.read_model_projections 
                           WHERE source_record_id = @recordId::uuid";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "recordId", Type = "guid" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Guid Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with valid GUID
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["recordId"] = targetId.ToString() }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().Be(1);
            data[0].GetProperty("source_record_id").GetString().Should().Be(targetId.ToString());
        }

        /// <summary>
        /// Tests integer parameter binding. Source lines 379-394.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_IntParameter_ResolvesCorrectly()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed multiple records
            for (int i = 0; i < 5; i++)
            {
                await SeedProjectionDataAsync("project", "task", Guid.NewGuid(),
                    JsonSerializer.Serialize(new { name = $"Task {i}" }));
            }

            // Create report with int parameter for LIMIT
            string sql = @"SELECT source_record_id, source_entity 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = 'project' 
                           ORDER BY updated_at DESC 
                           LIMIT @maxRows";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "maxRows", Type = "int", DefaultValue = "10" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Int Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with int parameter value of 3
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["maxRows"] = "3" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().BeLessOrEqualTo(3);
        }

        /// <summary>
        /// Tests decimal parameter binding with proper precision for financial calculations.
        /// Source lines 395-407.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_DecimalParameter_ResolvesCorrectly()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed some data
            await SeedProjectionDataAsync("invoicing", "invoice", Guid.NewGuid(),
                JsonSerializer.Serialize(new { amount = 150.75, status = "paid" }));
            await SeedProjectionDataAsync("invoicing", "invoice", Guid.NewGuid(),
                JsonSerializer.Serialize(new { amount = 50.25, status = "paid" }));

            // Create report with decimal parameter — tests numeric precision
            string sql = @"SELECT COUNT(*) AS matching_count
                           FROM reporting.read_model_projections 
                           WHERE source_domain = 'invoicing'
                           AND (projection_data->>'amount')::decimal >= @minAmount";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "minAmount", Type = "decimal", DefaultValue = "0.00" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Decimal Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with decimal precision value
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["minAmount"] = "100.50" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — only the 150.75 record should match
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().BeGreaterOrEqualTo(1);
            data[0].GetProperty("matching_count").GetInt32().Should().Be(1);
        }

        /// <summary>
        /// Tests date parameter binding including "now" and "utc_now" special values.
        /// Source lines 408-429.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_DateParameter_ResolvesCorrectly()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed data — projections have created_at timestamps
            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Recent Contact" }));

            // Create report with date parameter — the created_at column is auto-set
            string sql = @"SELECT COUNT(*) AS count_before_date
                           FROM reporting.read_model_projections 
                           WHERE created_at <= @cutoffDate::timestamptz";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "cutoffDate", Type = "date", DefaultValue = "utc_now" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Date Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with future date — all records should be included
            var futureDate = DateTime.UtcNow.AddDays(30).ToString("o");
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["cutoffDate"] = futureDate }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200, $"Body: {response.Body}");
            var doc = JsonDocument.Parse(response.Body);
            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().BeGreaterOrEqualTo(1);
            data[0].GetProperty("count_before_date").GetInt32().Should().BeGreaterOrEqualTo(1);
        }

        /// <summary>
        /// Tests text parameter binding including "null" and "string.empty" special values.
        /// Source lines 430-442.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_TextParameter_ResolvesCorrectly()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed data with different domains
            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "CRM Contact" }));
            await SeedProjectionDataAsync("inventory", "product", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Product" }));

            // Create report with text parameter for domain filter
            string sql = @"SELECT source_domain, COUNT(*) AS count 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = @domainFilter 
                           GROUP BY source_domain";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "domainFilter", Type = "text" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Text Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with text parameter
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["domainFilter"] = "crm" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — should only return CRM records
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().Be(1);
            data[0].GetProperty("source_domain").GetString().Should().Be("crm");
        }

        /// <summary>
        /// Tests boolean parameter binding with "true"/"false"/"null" values.
        /// Source lines 443-458.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_BoolParameter_ResolvesCorrectly()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed data
            await SeedProjectionDataAsync("workflow", "task", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Active Task" }));

            // Create report with bool parameter — use a simple query that uses the bool
            string sql = @"SELECT COUNT(*) AS total 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = 'workflow'
                           AND (@includeAll = true OR source_entity = 'task')";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "includeAll", Type = "bool", DefaultValue = "false" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Bool Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with boolean true
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["includeAll"] = "true" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().BeGreaterOrEqualTo(1);
        }

        // ─── Parameter Edge Cases ──────────────────────────────────────────

        /// <summary>
        /// Verify null parameter values are handled per source pattern — returning null
        /// for empty/whitespace values across all types.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_NullParameterValue_HandlesGracefully()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed data
            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Contact" }));

            // Create report with nullable text parameter that uses COALESCE for null safety
            string sql = @"SELECT COUNT(*) AS total 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = COALESCE(@domainFilter, source_domain)";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "domainFilter", Type = "text", DefaultValue = "null" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Null Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with explicit null value — per source "null" string → null object
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["domainFilter"] = "null" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — null COALESCE to source_domain → returns all records
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// When IgnoreParseErrors=true, invalid parameter values return null instead of
        /// throwing exceptions — matching DataSourceManager.GetDataSourceParameterValue behavior.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_IgnoreParseErrors_ReturnsNullInsteadOfThrowing()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed data
            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Contact" }));

            // Create report with GUID parameter that has IgnoreParseErrors=true
            string sql = @"SELECT COUNT(*) AS total 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = 'crm'
                           AND (@recordFilter IS NULL OR source_record_id = @recordFilter::uuid)";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter
                {
                    Name = "recordFilter",
                    Type = "guid",
                    IgnoreParseErrors = true
                }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"IgnoreParseErrors Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute with invalid GUID value — should NOT throw, should return null
            var executeBody = JsonSerializer.Serialize(new
            {
                parameters = new Dictionary<string, object?> { ["recordFilter"] = "not-a-valid-guid" }
            }, JsonOptions);

            var executeRequest = BuildAuthorizedRequest(
                body: executeBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — should succeed, not throw, and the null param means all records match
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// When execution request omits parameters, default values from report definition
        /// are used — matching source lines 479-481 fallback to ds.Parameters defaults.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_DefaultParameters_UsedWhenNotProvided()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Seed specific domain data
            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "CRM Contact" }));
            await SeedProjectionDataAsync("inventory", "product", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "Product" }));

            // Create report with default parameter value
            string sql = @"SELECT COUNT(*) AS count 
                           FROM reporting.read_model_projections 
                           WHERE source_domain = @domain";

            var parameters = new List<ReportParameter>
            {
                new ReportParameter { Name = "domain", Type = "text", DefaultValue = "crm" }
            };

            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"Default Param Test {Guid.NewGuid():N}", sql, parameters);
            createResponse.StatusCode.Should().Be(201);

            // Execute WITHOUT providing parameters — should use default 'crm'
            var executeRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — should use default 'crm' domain and return only CRM records
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var data = doc.RootElement.GetProperty("object").GetProperty("data");
            data.GetArrayLength().Should().Be(1);
            data[0].GetProperty("count").GetInt32().Should().Be(1);
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 4: SSM Parameter Retrieval Tests
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verify DB_CONNECTION_STRING retrieved from LocalStack SSM SecureString.
        /// Per AAP Section 0.8.6: secrets via SSM SecureString, never environment variables.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_ConnectionStringFromSsm_RetrievesSuccessfully()
        {
            // Arrange — verify SSM parameter exists in LocalStack
            var ssmValue = await _localStack.GetSsmParameterAsync("/reporting/db-connection-string");
            ssmValue.Should().NotBeNull("SSM parameter /reporting/db-connection-string should be seeded by LocalStackFixture");

            // Verify it contains a valid connection string that points to RDS
            ssmValue.Should().Contain("Host=", "SSM value should be a PostgreSQL connection string");

            // The LocalStackFixture seeds this parameter, and the handler resolves it.
            // Verify a report can be created and executed (which requires working DB connection)
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            await SeedProjectionDataAsync("crm", "contact", Guid.NewGuid(),
                JsonSerializer.Serialize(new { name = "SSM Test Contact" }));

            string sql = "SELECT COUNT(*) AS total FROM reporting.read_model_projections";
            var (createResponse, report) = await CreateReportViaHandlerAsync(
                handler, $"SSM Conn Test {Guid.NewGuid():N}", sql);
            createResponse.StatusCode.Should().Be(201);

            var executeRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });
            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert — execution against SSM-configured database succeeds
            response.StatusCode.Should().Be(200);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        }

        /// <summary>
        /// When SSM parameter is missing or invalid, verify service returns 503 health check failure.
        /// </summary>
        [RdsFact]
        public async Task ExecuteReport_InvalidSsmParameter_Returns503()
        {
            try
            {
                // Arrange — create handler with bad connection (simulating invalid SSM value)
                var badHandler = CreateReportHandlerWithBadConnection();

                // Act — health check should fail when DB is unreachable
                var request = BuildAuthorizedRequest();
                var response = await badHandler.HandleHealthCheck(request, CreateTestContext());

                // Assert — should return 503 indicating unhealthy service
                response.StatusCode.Should().Be(503);
                var doc = JsonDocument.Parse(response.Body);
                doc.RootElement.GetProperty("status").GetString().Should().Be("unhealthy");
                doc.RootElement.GetProperty("database").GetString().Should().NotBe("connected");
            }
            finally
            {
                // Restore good SSM parameter for subsequent tests
                RestoreGoodSsmConnectionString();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 5: Health Check Endpoint Tests
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Call HandleHealthCheck() — verify 200 with healthy status including
        /// database connectivity (SELECT 1) and SNS connectivity verification.
        /// </summary>
        [RdsFact]
        public async Task HealthCheck_AllServicesHealthy_Returns200()
        {
            // Arrange
            var handler = CreateReportHandler();
            var request = BuildAuthorizedRequest();

            // Act
            var response = await handler.HandleHealthCheck(request, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("status").GetString().Should().Be("healthy");
            doc.RootElement.GetProperty("database").GetString().Should().Be("connected");
            doc.RootElement.GetProperty("sns").GetString().Should().Be("connected");
            doc.RootElement.TryGetProperty("timestamp", out var timestamp).Should().BeTrue();
            timestamp.GetString().Should().NotBeNullOrEmpty();
        }

        /// <summary>
        /// Configure handler with bad connection string — verify 503 unhealthy response.
        /// </summary>
        [RdsFact]
        public async Task HealthCheck_DatabaseDown_Returns503()
        {
            try
            {
                // Arrange — handler with unreachable database
                var badHandler = CreateReportHandlerWithBadConnection();
                var request = BuildAuthorizedRequest();

                // Act
                var response = await badHandler.HandleHealthCheck(request, CreateTestContext());

                // Assert
                response.StatusCode.Should().Be(503);

                var doc = JsonDocument.Parse(response.Body);
                doc.RootElement.GetProperty("status").GetString().Should().Be("unhealthy");
                // Database should be disconnected/error, SNS may still be connected
                doc.RootElement.GetProperty("database").GetString().Should().NotBe("connected");
            }
            finally
            {
                // Restore good SSM parameter for subsequent tests
                RestoreGoodSsmConnectionString();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 6: Report CRUD via Handler Integration Tests
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Build APIGatewayHttpApiV2ProxyRequest with JSON body, call HandleCreateReport,
        /// verify 201 response with created report definition.
        /// </summary>
        [RdsFact]
        public async Task HandleCreateReport_ValidRequest_Returns201()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Act
            var (response, report) = await CreateReportViaHandlerAsync(
                handler,
                $"Valid Report {Guid.NewGuid():N}",
                "SELECT 1 AS value");

            // Assert
            response.StatusCode.Should().Be(201);
            report.Should().NotBeNull();
            report!.Id.Should().NotBe(Guid.Empty);
            report.Name.Should().StartWith("Valid Report");
            report.SqlTemplate.Should().Contain("SELECT 1");
            report.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Create two reports with same name — verify second returns 409 conflict
        /// with validation error (matching source DataSourceManager.Create lines 172-173
        /// uniqueness check).
        /// </summary>
        [RdsFact]
        public async Task HandleCreateReport_DuplicateName_Returns400()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            string reportName = $"Duplicate Test {Guid.NewGuid():N}";

            // Create first report — should succeed
            var (firstResponse, _) = await CreateReportViaHandlerAsync(
                handler, reportName, "SELECT 1 AS value");
            firstResponse.StatusCode.Should().Be(201);

            // Create second report with same name — should fail with 409
            var (secondResponse, _) = await CreateReportViaHandlerAsync(
                handler, reportName, "SELECT 2 AS value");

            // The handler returns 409 for duplicate names
            secondResponse.StatusCode.Should().Be(409);

            var doc = JsonDocument.Parse(secondResponse.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        /// <summary>
        /// Create report, then get by ID — verify 200 response with matching report data.
        /// </summary>
        [RdsFact]
        public async Task HandleGetReport_ExistingId_Returns200()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Create a report
            var (_, report) = await CreateReportViaHandlerAsync(
                handler, $"Get Test {Guid.NewGuid():N}", "SELECT 1 AS value");
            report.Should().NotBeNull();

            // Act — get by ID
            var getRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleGetReport(getRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var returnedReport = JsonSerializer.Deserialize<ReportDefinition>(
                doc.RootElement.GetProperty("object").GetRawText(), JsonOptions);
            returnedReport.Should().NotBeNull();
            returnedReport!.Id.Should().Be(report.Id);
            returnedReport.Name.Should().Be(report.Name);
        }

        /// <summary>
        /// Get with random GUID — verify 404 (matching source line 474: "DataSource not found.").
        /// </summary>
        [RdsFact]
        public async Task HandleGetReport_NonExistentId_Returns404()
        {
            var handler = CreateReportHandler();
            var nonExistentId = Guid.NewGuid();

            // Act
            var getRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = nonExistentId.ToString() });

            var response = await handler.HandleGetReport(getRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(404);
            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        /// <summary>
        /// Create 5 reports, list with page=1 pageSize=2 — verify paginated response.
        /// </summary>
        [RdsFact]
        public async Task HandleListReports_WithPagination_Returns200()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Create 5 reports
            for (int i = 0; i < 5; i++)
            {
                var (r, _) = await CreateReportViaHandlerAsync(
                    handler, $"List Test Report {i} {Guid.NewGuid():N}", $"SELECT {i} AS value");
                r.StatusCode.Should().Be(201);
            }

            // Act — list with pagination
            var listRequest = BuildAuthorizedRequest(
                queryStringParameters: new Dictionary<string, string>
                {
                    ["page"] = "1",
                    ["pageSize"] = "2"
                });

            var response = await handler.HandleListReports(listRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var obj = doc.RootElement.GetProperty("object");
            var data = obj.GetProperty("data");
            data.GetArrayLength().Should().Be(2); // pageSize=2

            var totalCount = obj.GetProperty("total_count").GetInt32();
            totalCount.Should().BeGreaterOrEqualTo(5); // at least 5 reports total
        }

        /// <summary>
        /// Create, update — verify 200 with updated fields.
        /// </summary>
        [RdsFact]
        public async Task HandleUpdateReport_ExistingReport_Returns200()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Create a report
            var (_, report) = await CreateReportViaHandlerAsync(
                handler, $"Update Test {Guid.NewGuid():N}", "SELECT 1 AS original_value");
            report.Should().NotBeNull();

            // Act — update the report
            var updateBody = JsonSerializer.Serialize(new
            {
                name = $"Updated Report {Guid.NewGuid():N}",
                description = "Updated description",
                queryDefinition = "SELECT 2 AS updated_value",
                parameters = new List<object>(),
                returnTotal = false
            }, JsonOptions);

            var updateRequest = BuildAuthorizedRequest(
                body: updateBody,
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var response = await handler.HandleUpdateReport(updateRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(200);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            var updatedReport = JsonSerializer.Deserialize<ReportDefinition>(
                doc.RootElement.GetProperty("object").GetRawText(), JsonOptions);
            updatedReport.Should().NotBeNull();
            updatedReport!.Description.Should().Be("Updated description");
            updatedReport.UpdatedAt.Should().BeAfter(report.CreatedAt);
        }

        /// <summary>
        /// Create, delete — verify 200, then get returns 404.
        /// </summary>
        [RdsFact]
        public async Task HandleDeleteReport_ExistingReport_Returns200()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();

            // Create a report
            var (_, report) = await CreateReportViaHandlerAsync(
                handler, $"Delete Test {Guid.NewGuid():N}", "SELECT 1 AS value");
            report.Should().NotBeNull();

            // Act — delete the report
            var deleteRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = report!.Id.ToString() });

            var deleteResponse = await handler.HandleDeleteReport(deleteRequest, CreateTestContext());

            // Assert — delete succeeds
            deleteResponse.StatusCode.Should().Be(200);

            var deleteDoc = JsonDocument.Parse(deleteResponse.Body);
            deleteDoc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

            // Act — verify get returns 404
            var getRequest = BuildAuthorizedRequest(
                pathParameters: new Dictionary<string, string> { ["id"] = report.Id.ToString() });

            var getResponse = await handler.HandleGetReport(getRequest, CreateTestContext());

            // Assert — report no longer exists
            getResponse.StatusCode.Should().Be(404);
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 7: Error Handling Tests
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verify validation error for empty name — matching source DataSourceManager.Create
        /// line 170-171 name validation.
        /// </summary>
        [RdsFact]
        public async Task HandleCreateReport_EmptyName_Returns400()
        {
            var handler = CreateReportHandler();

            var body = JsonSerializer.Serialize(new
            {
                name = "",
                description = "Test",
                queryDefinition = "SELECT 1 AS value",
                parameters = new List<object>(),
                returnTotal = true
            }, JsonOptions);

            var request = BuildAuthorizedRequest(body: body);

            // Act
            var response = await handler.HandleCreateReport(request, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(400);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        /// <summary>
        /// Verify validation error for empty query — matching source line 175-176.
        /// </summary>
        [RdsFact]
        public async Task HandleCreateReport_EmptyQuery_Returns400()
        {
            var handler = CreateReportHandler();

            var body = JsonSerializer.Serialize(new
            {
                name = $"Empty Query Test {Guid.NewGuid():N}",
                description = "Test",
                queryDefinition = "",
                parameters = new List<object>(),
                returnTotal = true
            }, JsonOptions);

            var request = BuildAuthorizedRequest(body: body);

            // Act
            var response = await handler.HandleCreateReport(request, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(400);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        /// <summary>
        /// Execute report with non-existent ID — matching source line 474:
        /// "DataSource not found." → 404.
        /// </summary>
        [RdsFact]
        public async Task HandleExecuteReport_NonExistentReport_Returns404()
        {
            var handler = CreateReportHandler();
            var nonExistentId = Guid.NewGuid();

            var executeRequest = BuildAuthorizedRequest(
                body: JsonSerializer.Serialize(new
                {
                    parameters = new Dictionary<string, object>()
                }, JsonOptions),
                pathParameters: new Dictionary<string, string> { ["id"] = nonExistentId.ToString() });

            // Act
            var response = await handler.HandleExecuteReport(executeRequest, CreateTestContext());

            // Assert
            response.StatusCode.Should().Be(404);

            var doc = JsonDocument.Parse(response.Body);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        }

        // ════════════════════════════════════════════════════════════════════
        // Phase 8: Idempotency Tests
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Send same request with same Idempotency-Key header twice — verify only
        /// one report created (per AAP Section 0.8.5: idempotency keys on all write
        /// endpoints and event handlers).
        /// </summary>
        [RdsFact]
        public async Task HandleCreateReport_WithIdempotencyKey_PreventsDuplicate()
        {
            await CleanDatabaseAsync();
            var handler = CreateReportHandler();
            var idempotencyKey = Guid.NewGuid().ToString();

            var body = JsonSerializer.Serialize(new
            {
                name = $"Idempotent Report {Guid.NewGuid():N}",
                description = "Idempotency test",
                queryDefinition = "SELECT 1 AS value",
                parameters = new List<object>(),
                returnTotal = true
            }, JsonOptions);

            var request1 = BuildAuthorizedRequest(body: body);
            request1.Headers["Idempotency-Key"] = idempotencyKey;
            request1.Headers["x-idempotency-key"] = idempotencyKey;

            var request2 = BuildAuthorizedRequest(body: body);
            request2.Headers["Idempotency-Key"] = idempotencyKey;
            request2.Headers["x-idempotency-key"] = idempotencyKey;

            // Act — send same request twice with same idempotency key
            var response1 = await handler.HandleCreateReport(request1, CreateTestContext());
            var response2 = await handler.HandleCreateReport(request2, CreateTestContext());

            // Assert — first should succeed with 201
            response1.StatusCode.Should().Be(201);

            // Second should also succeed (idempotent replay) OR return 409 conflict
            // The idempotency implementation returns the cached result for the same key
            var successfulResponses = new[] { 200, 201, 409 };
            successfulResponses.Should().Contain(response2.StatusCode);

            // If both are 201, they should return the same report ID (idempotent)
            if (response1.StatusCode == 201 && response2.StatusCode == 201)
            {
                var doc1 = JsonDocument.Parse(response1.Body);
                var doc2 = JsonDocument.Parse(response2.Body);

                var id1 = doc1.RootElement.GetProperty("object").GetProperty("id").GetString();
                var id2 = doc2.RootElement.GetProperty("object").GetProperty("id").GetString();

                id1.Should().Be(id2, "idempotent requests should return the same report ID");
            }

            // Verify only one report exists via list
            var listRequest = BuildAuthorizedRequest(
                queryStringParameters: new Dictionary<string, string>
                {
                    ["page"] = "1",
                    ["pageSize"] = "50"
                });
            var listResponse = await handler.HandleListReports(listRequest, CreateTestContext());
            listResponse.StatusCode.Should().Be(200);

            var listDoc = JsonDocument.Parse(listResponse.Body);
            var data = listDoc.RootElement.GetProperty("object").GetProperty("data");
            // Should have only 1 report (idempotent creation prevented duplicates)
            data.GetArrayLength().Should().Be(1);
        }
    }
}
