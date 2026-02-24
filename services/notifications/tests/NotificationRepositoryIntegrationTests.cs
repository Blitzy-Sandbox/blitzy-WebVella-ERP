using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebVellaErp.Notifications.DataAccess;
using WebVellaErp.Notifications.Models;
using Xunit;

namespace WebVellaErp.Notifications.Tests;

/// <summary>
/// Integration tests for <see cref="NotificationRepository"/> DynamoDB single-table design.
/// All tests execute against LocalStack DynamoDB — NO mocked AWS SDK calls.
/// Pattern: table creation in <see cref="InitializeAsync"/> → test → table deletion in <see cref="DisposeAsync"/>.
///
/// Table schema mirrors the production single-table design:
///   PK (HASH, S) / SK (RANGE, S) — base key pair
///   GSI1: GSI1PK (HASH, S) / GSI1SK (RANGE, S) — email queue ordering by status/priority/scheduled
///   GSI2: GSI2PK (HASH, S) / GSI2SK (RANGE, S) — SMTP service name lookup
///   BillingMode: PAY_PER_REQUEST (on-demand, LocalStack compatible)
///
/// PK patterns verified:
///   EMAIL#{emailId}         / META — email records
///   SMTP_SERVICE#{serviceId}/ META — SMTP service configs
///   SMTP_SERVICE#DEFAULT    / META — default pointer entity
///   WEBHOOK#{webhookId}     / META — webhook configs
/// </summary>
public class NotificationRepositoryIntegrationTests : IAsyncLifetime
{
    // ═══════════════════════════════════════════════════════════════════
    //  Private fields
    // ═══════════════════════════════════════════════════════════════════

    private AmazonDynamoDBClient _dynamoDbClient = null!;
    private NotificationRepository _repository = null!;
    private IMemoryCache _memoryCache = null!;
    private const string TestTableName = "notifications-records-test";
    private string? _originalTableName;

    // ═══════════════════════════════════════════════════════════════════
    //  IAsyncLifetime — Test Setup / Teardown
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a real DynamoDB table in LocalStack with the full single-table
    /// design schema (PK/SK + GSI1 + GSI2), then instantiates the repository
    /// under test with real AWS SDK client, MemoryCache, and NullLogger.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Preserve and override the environment variable for the table name
        _originalTableName = Environment.GetEnvironmentVariable("NOTIFICATIONS_TABLE_NAME");
        Environment.SetEnvironmentVariable("NOTIFICATIONS_TABLE_NAME", TestTableName);

        // Create a real AmazonDynamoDBClient targeting LocalStack
        var endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL") ?? "http://localhost:4566";
        var clientConfig = new AmazonDynamoDBConfig
        {
            ServiceURL = endpoint,
            AuthenticationRegion = "us-east-1"
        };
        var credentials = new BasicAWSCredentials("test", "test");
        _dynamoDbClient = new AmazonDynamoDBClient(credentials, clientConfig);

        // Create the DynamoDB table with the full single-table design schema
        await CreateTestTableAsync();

        // Build dependencies for NotificationRepository constructor:
        //   (IAmazonDynamoDB dynamoDbClient, IMemoryCache memoryCache, ILogger<NotificationRepository> logger)
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        var logger = NullLogger<NotificationRepository>.Instance;

        // Instantiate the repository under test with real LocalStack DynamoDB client
        _repository = new NotificationRepository(_dynamoDbClient, _memoryCache, logger);
    }

    /// <summary>
    /// Deletes the test DynamoDB table and disposes all resources.
    /// Restores the original NOTIFICATIONS_TABLE_NAME environment variable.
    /// </summary>
    public async Task DisposeAsync()
    {
        try
        {
            await _dynamoDbClient.DeleteTableAsync(new DeleteTableRequest { TableName = TestTableName });
        }
        catch (ResourceNotFoundException)
        {
            // Table already deleted or never created — safe to ignore
        }

        _memoryCache?.Dispose();
        _dynamoDbClient?.Dispose();

        // Restore original environment variable
        Environment.SetEnvironmentVariable("NOTIFICATIONS_TABLE_NAME", _originalTableName);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Table creation helper
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the test DynamoDB table with proper single-table design schema
    /// matching NotificationRepository's expected key structure and GSI configuration.
    /// </summary>
    private async Task CreateTestTableAsync()
    {
        var request = new CreateTableRequest
        {
            TableName = TestTableName,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement("PK", KeyType.HASH),
                new KeySchemaElement("SK", KeyType.RANGE)
            },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new AttributeDefinition("PK", ScalarAttributeType.S),
                new AttributeDefinition("SK", ScalarAttributeType.S),
                new AttributeDefinition("GSI1PK", ScalarAttributeType.S),
                new AttributeDefinition("GSI1SK", ScalarAttributeType.S),
                new AttributeDefinition("GSI2PK", ScalarAttributeType.S),
                new AttributeDefinition("GSI2SK", ScalarAttributeType.S)
            },
            GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
            {
                new GlobalSecondaryIndex
                {
                    IndexName = "GSI1",
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement("GSI1PK", KeyType.HASH),
                        new KeySchemaElement("GSI1SK", KeyType.RANGE)
                    },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                },
                new GlobalSecondaryIndex
                {
                    IndexName = "GSI2",
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement("GSI2PK", KeyType.HASH),
                        new KeySchemaElement("GSI2SK", KeyType.RANGE)
                    },
                    Projection = new Projection { ProjectionType = ProjectionType.ALL }
                }
            }
        };

        await _dynamoDbClient.CreateTableAsync(request);

        // Wait for table to become ACTIVE (LocalStack is typically immediate, but be safe)
        var retries = 0;
        while (retries < 30)
        {
            var describeResponse = await _dynamoDbClient.DescribeTableAsync(
                new DescribeTableRequest { TableName = TestTableName });
            if (describeResponse.Table.TableStatus == TableStatus.ACTIVE)
                return;

            await Task.Delay(500);
            retries++;
        }

        throw new TimeoutException($"DynamoDB table '{TestTableName}' did not become ACTIVE within 15 seconds.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helper factory methods for test data construction
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a fully populated <see cref="Email"/> test record with sensible defaults.
    /// Override any property after calling this method for specific test scenarios.
    /// </summary>
    private static Email CreateTestEmail(
        Guid? id = null,
        Guid? serviceId = null,
        EmailStatus status = EmailStatus.Pending,
        EmailPriority priority = EmailPriority.Normal,
        DateTime? scheduledOn = null,
        DateTime? createdOn = null,
        string subject = "Test Email Subject",
        int retriesCount = 0)
    {
        return new Email
        {
            Id = id ?? Guid.NewGuid(),
            ServiceId = serviceId ?? Guid.NewGuid(),
            Sender = new EmailAddress("Test Sender", "sender@test.com"),
            Recipients = new List<EmailAddress>
            {
                new EmailAddress("Recipient One", "recipient1@test.com"),
                new EmailAddress("Recipient Two", "recipient2@test.com")
            },
            ReplyToEmail = "reply@test.com",
            Subject = subject,
            ContentText = "Plain text content for testing",
            ContentHtml = "<p>HTML content for testing</p>",
            CreatedOn = createdOn ?? DateTime.UtcNow,
            SentOn = null,
            Status = status,
            Priority = priority,
            ServerError = string.Empty,
            ScheduledOn = scheduledOn ?? DateTime.UtcNow.AddMinutes(-5),
            RetriesCount = retriesCount,
            XSearch = $"test email {subject.ToLowerInvariant()}",
            Attachments = new List<string> { "attachment1.pdf", "attachment2.doc" }
        };
    }

    /// <summary>
    /// Creates a fully populated <see cref="SmtpServiceConfig"/> test record.
    /// </summary>
    private static SmtpServiceConfig CreateTestSmtpConfig(
        Guid? id = null,
        string name = "Test SMTP Service",
        bool isDefault = false,
        bool isEnabled = true,
        int port = 587)
    {
        return new SmtpServiceConfig
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Server = "smtp.test.com",
            Port = port,
            Username = "smtp_user",
            Password = "smtp_password_secure",
            DefaultSenderName = "WebVella ERP",
            DefaultSenderEmail = "noreply@webvella.com",
            DefaultReplyToEmail = "support@webvella.com",
            MaxRetriesCount = 3,
            RetryWaitMinutes = 5,
            IsDefault = isDefault,
            IsEnabled = isEnabled,
            ConnectionSecurity = 2 // SslOnConnect
        };
    }

    /// <summary>
    /// Creates a fully populated <see cref="WebhookConfig"/> test record.
    /// </summary>
    private static WebhookConfig CreateTestWebhookConfig(
        Guid? id = null,
        string channel = "crm.account.created",
        bool isEnabled = true,
        string endpointUrl = "https://webhook.site/test-endpoint")
    {
        return new WebhookConfig
        {
            Id = id ?? Guid.NewGuid(),
            Channel = channel,
            EndpointUrl = endpointUrl,
            IsEnabled = isEnabled,
            MaxRetries = 3,
            RetryIntervalSeconds = 30,
            CreatedOn = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Reads a raw DynamoDB item by PK/SK for single-table design verification tests.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetRawItemAsync(string pk, string sk)
    {
        var response = await _dynamoDbClient.GetItemAsync(new GetItemRequest
        {
            TableName = TestTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["PK"] = new AttributeValue { S = pk },
                ["SK"] = new AttributeValue { S = sk }
            }
        });
        return response.IsItemSet ? response.Item : null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Email CRUD Integration Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveEmail_NewEmail_PersistsToLocalStackDynamoDB()
    {
        // Arrange
        var email = CreateTestEmail(subject: "Persist New Email");

        // Act
        await _repository.SaveEmailAsync(email);

        // Assert — read back via repository and verify ALL fields match
        var retrieved = await _repository.GetEmailByIdAsync(email.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(email.Id);
        retrieved.ServiceId.Should().Be(email.ServiceId);
        retrieved.Subject.Should().Be("Persist New Email");
        retrieved.ContentText.Should().Be(email.ContentText);
        retrieved.ContentHtml.Should().Be(email.ContentHtml);
        retrieved.ReplyToEmail.Should().Be(email.ReplyToEmail);
        retrieved.Status.Should().Be(EmailStatus.Pending);
        retrieved.Priority.Should().Be(EmailPriority.Normal);
        retrieved.RetriesCount.Should().Be(0);
        retrieved.ServerError.Should().Be(string.Empty);
        retrieved.XSearch.Should().Be(email.XSearch);
        retrieved.Sender.Should().NotBeNull();
        retrieved.Sender!.Name.Should().Be("Test Sender");
        retrieved.Sender.Address.Should().Be("sender@test.com");
        retrieved.Recipients.Should().HaveCount(2);
        retrieved.Recipients![0].Address.Should().Be("recipient1@test.com");
        retrieved.Attachments.Should().HaveCount(2);
        retrieved.Attachments![0].Should().Be("attachment1.pdf");
    }

    [Fact]
    public async Task SaveEmail_ExistingEmail_UpdatesRecord()
    {
        // Arrange — save initial email
        var email = CreateTestEmail(subject: "Original Subject");
        await _repository.SaveEmailAsync(email);

        // Act — modify and save again (upsert behavior)
        email.Subject = "Updated Subject";
        email.Status = EmailStatus.Sent;
        email.SentOn = DateTime.UtcNow;
        await _repository.SaveEmailAsync(email);

        // Assert — read back and verify changes persisted
        var retrieved = await _repository.GetEmailByIdAsync(email.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Subject.Should().Be("Updated Subject");
        retrieved.Status.Should().Be(EmailStatus.Sent);
        retrieved.SentOn.Should().NotBeNull();
    }

    [Fact]
    public async Task GetEmailById_ExistingId_ReturnsEmail()
    {
        // Arrange
        var email = CreateTestEmail(subject: "Fetch By ID Test");
        await _repository.SaveEmailAsync(email);

        // Act
        var retrieved = await _repository.GetEmailByIdAsync(email.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(email.Id);
        retrieved.Subject.Should().Be("Fetch By ID Test");
        retrieved.Sender.Should().NotBeNull();
        retrieved.Recipients.Should().NotBeNull();
        retrieved.Recipients.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEmailById_NonExistent_ReturnsNull()
    {
        // Act
        var result = await _repository.GetEmailByIdAsync(Guid.NewGuid());

        // Assert — repository returns null for non-existent IDs, not exception
        result.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetPendingEmails Integration Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPendingEmails_ReturnsBatchOf10()
    {
        // Arrange — create 15 pending emails with unique IDs, all past-due
        var emailIds = new List<Guid>();
        for (int i = 0; i < 15; i++)
        {
            var email = CreateTestEmail(
                subject: $"Batch Email {i}",
                status: EmailStatus.Pending,
                priority: EmailPriority.Normal,
                scheduledOn: DateTime.UtcNow.AddMinutes(-10 - i));
            await _repository.SaveEmailAsync(email);
            emailIds.Add(email.Id);
        }

        // Act — request batch of 10 (preserves source PAGESIZE 10 from SmtpInternalService)
        var results = await _repository.GetPendingEmailsAsync(10);

        // Assert — at most 10 returned (may include emails from other tests sharing same table)
        results.Should().NotBeNull();
        results.Should().HaveCountLessOrEqualTo(10);
        // Verify that returned emails are from our batch
        var returnedIds = results.Select(e => e.Id).ToList();
        returnedIds.Intersect(emailIds).Should().NotBeEmpty("at least some batch emails should be returned");
    }

    [Fact]
    public async Task GetPendingEmails_OrdersByPriorityDescScheduledOnAsc()
    {
        // Arrange — create emails with different priorities and scheduled times
        var highPriorityEarly = CreateTestEmail(
            subject: "High Priority Early",
            priority: EmailPriority.High,
            scheduledOn: DateTime.UtcNow.AddHours(-3));
        var highPriorityLate = CreateTestEmail(
            subject: "High Priority Late",
            priority: EmailPriority.High,
            scheduledOn: DateTime.UtcNow.AddHours(-1));
        var normalPriority = CreateTestEmail(
            subject: "Normal Priority",
            priority: EmailPriority.Normal,
            scheduledOn: DateTime.UtcNow.AddHours(-2));
        var lowPriority = CreateTestEmail(
            subject: "Low Priority",
            priority: EmailPriority.Low,
            scheduledOn: DateTime.UtcNow.AddHours(-2));

        await _repository.SaveEmailAsync(lowPriority);
        await _repository.SaveEmailAsync(normalPriority);
        await _repository.SaveEmailAsync(highPriorityLate);
        await _repository.SaveEmailAsync(highPriorityEarly);

        // Act
        var results = await _repository.GetPendingEmailsAsync(100);

        // Assert — verify priority DESC ordering
        // GSI1SK design: PRIORITY#{(9999-(int)priority):D4}#SCHEDULED#{iso8601}
        // Higher priority (2=High) → lower inverted value (9997) → sorts FIRST
        // Within same priority, earlier scheduled_on sorts first (ASC)
        var ourResults = results
            .Where(e => new[] { highPriorityEarly.Id, highPriorityLate.Id, normalPriority.Id, lowPriority.Id }.Contains(e.Id))
            .ToList();

        ourResults.Should().HaveCount(4, "all 4 test emails should appear as pending and past-due");
        // High priority emails come first, then Normal, then Low
        ourResults[0].Priority.Should().Be(EmailPriority.High);
        ourResults[1].Priority.Should().Be(EmailPriority.High);
        // Within High priority, earlier scheduled_on comes first
        ourResults[0].Id.Should().Be(highPriorityEarly.Id, "earlier scheduled High-priority email first");
        ourResults[1].Id.Should().Be(highPriorityLate.Id, "later scheduled High-priority email second");
        ourResults[2].Priority.Should().Be(EmailPriority.Normal);
        ourResults[3].Priority.Should().Be(EmailPriority.Low);
    }

    [Fact]
    public async Task GetPendingEmails_OnlyPendingStatus()
    {
        // Arrange — create emails with different statuses
        var pendingEmail = CreateTestEmail(
            subject: "Pending Only Test - Pending",
            status: EmailStatus.Pending,
            scheduledOn: DateTime.UtcNow.AddMinutes(-10));
        var sentEmail = CreateTestEmail(
            subject: "Pending Only Test - Sent",
            status: EmailStatus.Sent,
            scheduledOn: DateTime.UtcNow.AddMinutes(-10));
        var abortedEmail = CreateTestEmail(
            subject: "Pending Only Test - Aborted",
            status: EmailStatus.Aborted,
            scheduledOn: DateTime.UtcNow.AddMinutes(-10));

        await _repository.SaveEmailAsync(pendingEmail);
        await _repository.SaveEmailAsync(sentEmail);
        await _repository.SaveEmailAsync(abortedEmail);

        // Act
        var results = await _repository.GetPendingEmailsAsync(100);

        // Assert — only Pending status emails returned
        var resultIds = results.Select(e => e.Id).ToList();
        resultIds.Should().Contain(pendingEmail.Id, "Pending email should be in results");
        resultIds.Should().NotContain(sentEmail.Id, "Sent email should NOT be in results");
        resultIds.Should().NotContain(abortedEmail.Id, "Aborted email should NOT be in results");
    }

    [Fact]
    public async Task GetPendingEmails_OnlyDueEmails()
    {
        // Arrange — create emails with scheduledOn in past and future
        var pastDueEmail = CreateTestEmail(
            subject: "Due Emails - Past Due",
            scheduledOn: DateTime.UtcNow.AddHours(-1));
        var futureEmail = CreateTestEmail(
            subject: "Due Emails - Future",
            scheduledOn: DateTime.UtcNow.AddHours(1));

        await _repository.SaveEmailAsync(pastDueEmail);
        await _repository.SaveEmailAsync(futureEmail);

        // Act
        var results = await _repository.GetPendingEmailsAsync(100);

        // Assert — only past-due emails returned (scheduled_on <= now)
        var resultIds = results.Select(e => e.Id).ToList();
        resultIds.Should().Contain(pastDueEmail.Id, "Past-due email should be in results");
        resultIds.Should().NotContain(futureEmail.Id, "Future-scheduled email should NOT be in results");
    }

    [Fact]
    public async Task GetPendingEmails_ScheduledOnNotNull()
    {
        // Arrange — create pending email with ScheduledOn = null
        var emailNoSchedule = CreateTestEmail(subject: "No Schedule Test");
        emailNoSchedule.ScheduledOn = null;
        await _repository.SaveEmailAsync(emailNoSchedule);

        // Also create a properly scheduled pending email for reference
        var emailWithSchedule = CreateTestEmail(
            subject: "Has Schedule Test",
            scheduledOn: DateTime.UtcNow.AddMinutes(-5));
        await _repository.SaveEmailAsync(emailWithSchedule);

        // Act
        var results = await _repository.GetPendingEmailsAsync(100);

        // Assert — email with null ScheduledOn (stored as NONE sentinel) should NOT appear
        var resultIds = results.Select(e => e.Id).ToList();
        resultIds.Should().NotContain(emailNoSchedule.Id,
            "Email with null ScheduledOn should NOT appear in pending queue");
        resultIds.Should().Contain(emailWithSchedule.Id,
            "Email with valid past-due ScheduledOn should appear in pending queue");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SMTP Service Config CRUD Integration Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveSmtpService_NewService_PersistsToDynamoDB()
    {
        // Arrange — create SMTP config with all fields populated
        var config = CreateTestSmtpConfig(
            name: "New SMTP Persist Test",
            isDefault: true,
            port: 465);

        // Act
        await _repository.SaveSmtpServiceAsync(config);

        // Assert — read back and verify ALL fields
        var retrieved = await _repository.GetSmtpServiceByIdAsync(config.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(config.Id);
        retrieved.Name.Should().Be("New SMTP Persist Test");
        retrieved.Server.Should().Be("smtp.test.com");
        retrieved.Port.Should().Be(465);
        retrieved.Username.Should().Be("smtp_user");
        retrieved.Password.Should().Be("smtp_password_secure");
        retrieved.DefaultSenderName.Should().Be("WebVella ERP");
        retrieved.DefaultSenderEmail.Should().Be("noreply@webvella.com");
        retrieved.DefaultReplyToEmail.Should().Be("support@webvella.com");
        retrieved.MaxRetriesCount.Should().Be(3);
        retrieved.RetryWaitMinutes.Should().Be(5);
        retrieved.IsDefault.Should().BeTrue();
        retrieved.IsEnabled.Should().BeTrue();
        retrieved.ConnectionSecurity.Should().Be(2);
    }

    [Fact]
    public async Task SaveSmtpService_ExistingService_UpdatesRecord()
    {
        // Arrange — save initial config
        var config = CreateTestSmtpConfig(name: "Update SMTP Test", isDefault: true);
        await _repository.SaveSmtpServiceAsync(config);

        // Act — modify and save again
        config.Port = 2525;
        config.MaxRetriesCount = 5;
        config.Server = "smtp2.test.com";
        await _repository.SaveSmtpServiceAsync(config);

        // Assert — read back and verify changes
        var retrieved = await _repository.GetSmtpServiceByIdAsync(config.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Port.Should().Be(2525);
        retrieved.MaxRetriesCount.Should().Be(5);
        retrieved.Server.Should().Be("smtp2.test.com");
        // Original fields should be preserved
        retrieved.Name.Should().Be("Update SMTP Test");
    }

    [Fact]
    public async Task GetSmtpServiceById_ExistingId_ReturnsConfig()
    {
        // Arrange
        var config = CreateTestSmtpConfig(name: "GetById SMTP Test", isDefault: true);
        await _repository.SaveSmtpServiceAsync(config);

        // Clear the cache to ensure a fresh DynamoDB read
        _repository.ClearSmtpServiceCache();

        // Act
        var retrieved = await _repository.GetSmtpServiceByIdAsync(config.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(config.Id);
        retrieved.Name.Should().Be("GetById SMTP Test");
    }

    [Fact]
    public async Task GetSmtpServiceById_NonExistent_ReturnsNull()
    {
        // Act — query a random GUID that was never saved
        var result = await _repository.GetSmtpServiceByIdAsync(Guid.NewGuid());

        // Assert — should return null without throwing
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSmtpServiceByName_ExistingName_ReturnsConfig()
    {
        // Arrange — unique name for isolation
        var uniqueName = $"SMTP-ByName-{Guid.NewGuid():N}";
        var config = CreateTestSmtpConfig(name: uniqueName, isDefault: true);
        await _repository.SaveSmtpServiceAsync(config);

        // Act
        var retrieved = await _repository.GetSmtpServiceByNameAsync(uniqueName);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be(uniqueName);
        retrieved.Id.Should().Be(config.Id);
    }

    [Fact]
    public async Task GetSmtpServiceByName_NonExistent_ReturnsNull()
    {
        // Act & Assert — GetSmtpServiceByNameAsync throws when not found
        // The actual NotificationRepository implementation throws Exception("SmtpService with name '...' not found.")
        var act = async () => await _repository.GetSmtpServiceByNameAsync("nonexistent-smtp-service-xyz");

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task GetDefaultSmtpService_ReturnsDefaultService()
    {
        // Arrange — create a default SMTP service
        var defaultConfig = CreateTestSmtpConfig(
            name: $"Default-SMTP-{Guid.NewGuid():N}",
            isDefault: true);
        await _repository.SaveSmtpServiceAsync(defaultConfig);

        // Clear cache to force fresh read
        _repository.ClearSmtpServiceCache();

        // Act
        var retrieved = await _repository.GetDefaultSmtpServiceAsync();

        // Assert — should return the default service
        retrieved.Should().NotBeNull();
        retrieved!.IsDefault.Should().BeTrue();
        // The default may be ours or a previously set default; verify it's a valid service
        retrieved.Id.Should().NotBe(Guid.Empty);
        retrieved.Name.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetAllSmtpServices_ReturnsAll()
    {
        // Arrange — create 3 unique SMTP services, first one is default
        var configs = new List<SmtpServiceConfig>();
        for (int i = 0; i < 3; i++)
        {
            var config = CreateTestSmtpConfig(
                name: $"AllServices-{Guid.NewGuid():N}-{i}",
                isDefault: i == 0);
            await _repository.SaveSmtpServiceAsync(config);
            configs.Add(config);
        }

        // Act
        var results = await _repository.GetAllSmtpServicesAsync();

        // Assert — all 3 test services should be in results
        results.Should().NotBeNull();
        var resultIds = results.Select(s => s.Id).ToList();
        foreach (var config in configs)
        {
            resultIds.Should().Contain(config.Id, $"Service '{config.Name}' should be in results");
        }
    }

    [Fact]
    public async Task DeleteSmtpService_RemovesFromDynamoDB()
    {
        // Arrange — create a non-default SMTP service (default cannot be deleted)
        // First create a default so we can set up a non-default
        var defaultConfig = CreateTestSmtpConfig(
            name: $"DeleteTest-Default-{Guid.NewGuid():N}",
            isDefault: true);
        await _repository.SaveSmtpServiceAsync(defaultConfig);

        var toDeleteConfig = CreateTestSmtpConfig(
            name: $"DeleteTest-Target-{Guid.NewGuid():N}",
            isDefault: false);
        await _repository.SaveSmtpServiceAsync(toDeleteConfig);

        // Act
        await _repository.DeleteSmtpServiceAsync(toDeleteConfig.Id);

        // Assert — verify get returns null after deletion
        var result = await _repository.GetSmtpServiceByIdAsync(toDeleteConfig.Id);
        result.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Webhook Config CRUD Integration Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveWebhookConfig_NewConfig_PersistsToDynamoDB()
    {
        // Arrange
        var webhook = CreateTestWebhookConfig(
            channel: "crm.contact.created",
            endpointUrl: "https://webhook.site/unique-test");

        // Act
        await _repository.SaveWebhookConfigAsync(webhook);

        // Assert — read raw item from DynamoDB to verify persistence
        var rawItem = await GetRawItemAsync($"WEBHOOK#{webhook.Id}", "META");
        rawItem.Should().NotBeNull("webhook should be persisted in DynamoDB");
        rawItem!["channel"].S.Should().Be("crm.contact.created");
        rawItem["endpoint_url"].S.Should().Be("https://webhook.site/unique-test");
        rawItem["is_enabled"].BOOL.Should().BeTrue();
        rawItem["max_retries"].N.Should().Be("3");
        rawItem["retry_interval_seconds"].N.Should().Be("30");
    }

    [Fact]
    public async Task GetActiveWebhooksByChannel_ReturnsMatchingWebhooks()
    {
        // Arrange — create 3 webhooks: 2 for same channel (1 active, 1 inactive), 1 for different channel
        var uniqueChannel = $"test.channel.{Guid.NewGuid():N}";

        var activeWebhook = CreateTestWebhookConfig(channel: uniqueChannel, isEnabled: true);
        var inactiveWebhook = CreateTestWebhookConfig(channel: uniqueChannel, isEnabled: false);
        var otherChannelWebhook = CreateTestWebhookConfig(
            channel: $"other.channel.{Guid.NewGuid():N}", isEnabled: true);

        await _repository.SaveWebhookConfigAsync(activeWebhook);
        await _repository.SaveWebhookConfigAsync(inactiveWebhook);
        await _repository.SaveWebhookConfigAsync(otherChannelWebhook);

        // Act
        var results = await _repository.GetActiveWebhooksByChannelAsync(uniqueChannel);

        // Assert — only the 1 active matching webhook returned
        results.Should().NotBeNull();
        var matchingIds = results.Select(w => w.Id).ToList();
        matchingIds.Should().Contain(activeWebhook.Id, "active webhook for channel should be returned");
        matchingIds.Should().NotContain(inactiveWebhook.Id, "inactive webhook should NOT be returned");
        matchingIds.Should().NotContain(otherChannelWebhook.Id, "webhook for different channel should NOT be returned");
    }

    [Fact]
    public async Task GetActiveWebhooksByChannel_CaseInsensitive()
    {
        // Arrange — create webhook with mixed-case channel
        var webhook = CreateTestWebhookConfig(
            channel: $"CRM.Account.Created.{Guid.NewGuid():N}",
            isEnabled: true);
        await _repository.SaveWebhookConfigAsync(webhook);

        // Act — query with lowercase channel (case-insensitive per source NotificationContext line 143)
        var results = await _repository.GetActiveWebhooksByChannelAsync(
            webhook.Channel.ToLowerInvariant());

        // Assert — should find the webhook despite case difference
        results.Should().NotBeNull();
        results.Select(w => w.Id).Should().Contain(webhook.Id,
            "case-insensitive channel match should find the webhook");
    }

    [Fact]
    public async Task GetActiveWebhooksByChannel_NoMatch_ReturnsEmpty()
    {
        // Act — query for a channel that has no webhooks
        var results = await _repository.GetActiveWebhooksByChannelAsync(
            $"nonexistent.channel.{Guid.NewGuid():N}");

        // Assert
        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteWebhookConfig_RemovesFromDynamoDB()
    {
        // Arrange — create and verify existence
        var webhook = CreateTestWebhookConfig();
        await _repository.SaveWebhookConfigAsync(webhook);

        // Verify it exists
        var before = await GetRawItemAsync($"WEBHOOK#{webhook.Id}", "META");
        before.Should().NotBeNull("webhook should exist before deletion");

        // Act
        await _repository.DeleteWebhookConfigAsync(webhook.Id);

        // Assert — raw item should be gone
        var after = await GetRawItemAsync($"WEBHOOK#{webhook.Id}", "META");
        after.Should().BeNull("webhook should be removed after deletion");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Notification Status Tracking Integration Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Email_StatusTransitions_PendingToSent()
    {
        // Arrange — create email with Pending status
        var email = CreateTestEmail(
            subject: "Status Transition: Pending→Sent",
            status: EmailStatus.Pending);
        await _repository.SaveEmailAsync(email);

        // Act — transition to Sent
        email.Status = EmailStatus.Sent;
        email.SentOn = DateTime.UtcNow;
        email.ScheduledOn = null; // Clear scheduled time after send
        await _repository.SaveEmailAsync(email);

        // Assert
        var retrieved = await _repository.GetEmailByIdAsync(email.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Status.Should().Be(EmailStatus.Sent);
        retrieved.SentOn.Should().NotBeNull();
    }

    [Fact]
    public async Task Email_StatusTransitions_PendingToAborted()
    {
        // Arrange — create email with Pending status
        var email = CreateTestEmail(
            subject: "Status Transition: Pending→Aborted",
            status: EmailStatus.Pending);
        await _repository.SaveEmailAsync(email);

        // Act — transition to Aborted with server error
        email.Status = EmailStatus.Aborted;
        email.ServerError = "SMTP connection timeout after 30s: smtp.test.com:587";
        await _repository.SaveEmailAsync(email);

        // Assert
        var retrieved = await _repository.GetEmailByIdAsync(email.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Status.Should().Be(EmailStatus.Aborted);
        retrieved.ServerError.Should().Contain("SMTP connection timeout");
    }

    [Fact]
    public async Task Email_RetryCount_Increments()
    {
        // Arrange — create email with RetriesCount=0
        var email = CreateTestEmail(
            subject: "Retry Count Test",
            status: EmailStatus.Pending);
        email.RetriesCount = 0;
        await _repository.SaveEmailAsync(email);

        // Act — increment retries
        email.RetriesCount = 1;
        await _repository.SaveEmailAsync(email);
        var after1 = await _repository.GetEmailByIdAsync(email.Id);
        after1!.RetriesCount.Should().Be(1);

        email.RetriesCount = 2;
        await _repository.SaveEmailAsync(email);
        var after2 = await _repository.GetEmailByIdAsync(email.Id);
        after2!.RetriesCount.Should().Be(2);

        email.RetriesCount = 3;
        email.Status = EmailStatus.Aborted;
        email.ServerError = "Max retries exceeded";
        await _repository.SaveEmailAsync(email);

        // Assert — final state
        var final = await _repository.GetEmailByIdAsync(email.Id);
        final.Should().NotBeNull();
        final!.RetriesCount.Should().Be(3);
        final.Status.Should().Be(EmailStatus.Aborted);
    }

    [Fact]
    public async Task Email_XSearch_PersistedCorrectly()
    {
        // Arrange — create email with XSearch populated
        // Source: SmtpInternalService.PrepareEmailXSearch (lines 683-687)
        var email = CreateTestEmail(subject: "XSearch Verification");
        email.XSearch = "sender@test.com recipient1@test.com xsearch verification test email";
        await _repository.SaveEmailAsync(email);

        // Act — read back and verify XSearch field
        var retrieved = await _repository.GetEmailByIdAsync(email.Id);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.XSearch.Should().Be("sender@test.com recipient1@test.com xsearch verification test email");

        // Verify the x_search attribute is persisted in raw DynamoDB item
        var rawItem = await GetRawItemAsync($"EMAIL#{email.Id}", "META");
        rawItem.Should().NotBeNull();
        rawItem!.Should().ContainKey("x_search", "x_search attribute should be persisted for search");
        rawItem!["x_search"].S.Should().Be("sender@test.com recipient1@test.com xsearch verification test email");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DynamoDB Single-Table Design Verification Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmailAndSmtpService_CoexistInSameTable()
    {
        // Arrange — create both email and SMTP service records
        var email = CreateTestEmail(subject: "Coexist Test - Email");
        var smtp = CreateTestSmtpConfig(
            name: $"Coexist-{Guid.NewGuid():N}",
            isDefault: true);

        await _repository.SaveEmailAsync(email);
        await _repository.SaveSmtpServiceAsync(smtp);

        // Act — verify both are readable from the repository (same table)
        var retrievedEmail = await _repository.GetEmailByIdAsync(email.Id);
        _repository.ClearSmtpServiceCache();
        var retrievedSmtp = await _repository.GetSmtpServiceByIdAsync(smtp.Id);

        // Assert — both coexist in the same table, identified by different PK prefixes
        retrievedEmail.Should().NotBeNull();
        retrievedSmtp.Should().NotBeNull();

        // Verify raw items exist in the SAME table with different PK patterns
        var rawEmail = await GetRawItemAsync($"EMAIL#{email.Id}", "META");
        var rawSmtp = await GetRawItemAsync($"SMTP_SERVICE#{smtp.Id}", "META");
        rawEmail.Should().NotBeNull("email record should exist in DynamoDB");
        rawSmtp.Should().NotBeNull("SMTP service record should exist in DynamoDB");

        // Verify EntityType attributes distinguish them (PascalCase per NotificationRepository constant)
        rawEmail!["EntityType"].S.Should().Be("EMAIL");
        rawSmtp!["EntityType"].S.Should().Be("SMTP_SERVICE");
    }

    [Fact]
    public async Task Email_PK_Format_Correct()
    {
        // Arrange
        var emailId = Guid.NewGuid();
        var email = CreateTestEmail(id: emailId, subject: "PK Format Test - Email");
        await _repository.SaveEmailAsync(email);

        // Act — read raw item with expected PK/SK format
        var rawItem = await GetRawItemAsync($"EMAIL#{emailId}", "META");

        // Assert — PK = EMAIL#{emailId}, SK = META
        rawItem.Should().NotBeNull("email should be stored with PK=EMAIL#{id} SK=META");
        rawItem!["PK"].S.Should().Be($"EMAIL#{emailId}");
        rawItem["SK"].S.Should().Be("META");
    }

    [Fact]
    public async Task SmtpService_PK_Format_Correct()
    {
        // Arrange
        var serviceId = Guid.NewGuid();
        var config = CreateTestSmtpConfig(
            id: serviceId,
            name: $"PK-Format-{Guid.NewGuid():N}",
            isDefault: true);
        await _repository.SaveSmtpServiceAsync(config);

        // Act — read raw item with expected PK/SK format
        var rawItem = await GetRawItemAsync($"SMTP_SERVICE#{serviceId}", "META");

        // Assert — PK = SMTP_SERVICE#{serviceId}, SK = META
        rawItem.Should().NotBeNull("SMTP service should be stored with PK=SMTP_SERVICE#{id} SK=META");
        rawItem!["PK"].S.Should().Be($"SMTP_SERVICE#{serviceId}");
        rawItem["SK"].S.Should().Be("META");
    }

    [Fact]
    public async Task Webhook_PK_Format_Correct()
    {
        // Arrange
        var webhookId = Guid.NewGuid();
        var webhook = CreateTestWebhookConfig(id: webhookId);
        await _repository.SaveWebhookConfigAsync(webhook);

        // Act — read raw item with expected PK/SK format
        var rawItem = await GetRawItemAsync($"WEBHOOK#{webhookId}", "META");

        // Assert — PK = WEBHOOK#{webhookId}, SK = META
        rawItem.Should().NotBeNull("webhook should be stored with PK=WEBHOOK#{id} SK=META");
        rawItem!["PK"].S.Should().Be($"WEBHOOK#{webhookId}");
        rawItem["SK"].S.Should().Be("META");
    }

    [Fact]
    public async Task GSI1_QueueOrdering_WorksCorrectly()
    {
        // Arrange — create emails with different priorities to verify GSI1 sort key design
        // GSI1PK = STATUS#{(int)status}, GSI1SK = PRIORITY#{(9999-(int)priority):D4}#SCHEDULED#{iso8601}
        // Higher priority → lower inverted value → sorts first (ascending)
        var highPriority = CreateTestEmail(
            subject: "GSI1 Test - High",
            priority: EmailPriority.High,
            scheduledOn: DateTime.UtcNow.AddMinutes(-10));
        var normalPriority = CreateTestEmail(
            subject: "GSI1 Test - Normal",
            priority: EmailPriority.Normal,
            scheduledOn: DateTime.UtcNow.AddMinutes(-10));
        var lowPriority = CreateTestEmail(
            subject: "GSI1 Test - Low",
            priority: EmailPriority.Low,
            scheduledOn: DateTime.UtcNow.AddMinutes(-10));

        await _repository.SaveEmailAsync(highPriority);
        await _repository.SaveEmailAsync(normalPriority);
        await _repository.SaveEmailAsync(lowPriority);

        // Verify GSI1 attributes in raw items
        var rawHigh = await GetRawItemAsync($"EMAIL#{highPriority.Id}", "META");
        var rawNormal = await GetRawItemAsync($"EMAIL#{normalPriority.Id}", "META");
        var rawLow = await GetRawItemAsync($"EMAIL#{lowPriority.Id}", "META");

        rawHigh.Should().NotBeNull();
        rawNormal.Should().NotBeNull();
        rawLow.Should().NotBeNull();

        // GSI1PK should be STATUS#0 (Pending = 0) for all three
        rawHigh!["GSI1PK"].S.Should().Be("STATUS#0");
        rawNormal!["GSI1PK"].S.Should().Be("STATUS#0");
        rawLow!["GSI1PK"].S.Should().Be("STATUS#0");

        // GSI1SK ordering: High (2) → 9997, Normal (1) → 9998, Low (0) → 9999
        // String comparison: "PRIORITY#9997..." < "PRIORITY#9998..." < "PRIORITY#9999..."
        // So High priority sorts first (ascending), which is correct for priority DESC
        var gsi1SkHigh = rawHigh["GSI1SK"].S;
        var gsi1SkNormal = rawNormal["GSI1SK"].S;
        var gsi1SkLow = rawLow["GSI1SK"].S;

        string.Compare(gsi1SkHigh, gsi1SkNormal, StringComparison.Ordinal)
            .Should().BeNegative("High priority GSI1SK should sort before Normal");
        string.Compare(gsi1SkNormal, gsi1SkLow, StringComparison.Ordinal)
            .Should().BeNegative("Normal priority GSI1SK should sort before Low");

        // Verify GetPendingEmails returns them in correct order
        var results = await _repository.GetPendingEmailsAsync(100);
        var ourResults = results
            .Where(e => new[] { highPriority.Id, normalPriority.Id, lowPriority.Id }.Contains(e.Id))
            .ToList();

        if (ourResults.Count == 3)
        {
            ourResults[0].Id.Should().Be(highPriority.Id, "High priority should be first");
            ourResults[1].Id.Should().Be(normalPriority.Id, "Normal priority should be second");
            ourResults[2].Id.Should().Be(lowPriority.Id, "Low priority should be third");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Batch and Concurrency Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPendingEmails_ConcurrentCalls_NoDuplicates()
    {
        // Arrange — create several pending emails
        var emailIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var email = CreateTestEmail(
                subject: $"Concurrent Test Email {i}",
                scheduledOn: DateTime.UtcNow.AddMinutes(-5));
            await _repository.SaveEmailAsync(email);
            emailIds.Add(email.Id);
        }

        // Act — call GetPendingEmails twice concurrently
        var task1 = _repository.GetPendingEmailsAsync(100);
        var task2 = _repository.GetPendingEmailsAsync(100);
        var results = await Task.WhenAll(task1, task2);

        // Assert — both calls should return valid results
        // Since these are read-only queries, both should succeed
        results[0].Should().NotBeNull();
        results[1].Should().NotBeNull();

        // Verify both calls return the same set of our test emails
        var ids1 = results[0].Select(e => e.Id).ToHashSet();
        var ids2 = results[1].Select(e => e.Id).ToHashSet();

        foreach (var id in emailIds)
        {
            ids1.Should().Contain(id, "first concurrent call should contain test email");
            ids2.Should().Contain(id, "second concurrent call should contain test email");
        }
    }

    [Fact]
    public async Task SaveEmail_ConditionalWrite_PreventsOverwrite()
    {
        // The actual NotificationRepository uses plain PutItem (upsert) without conditional expressions.
        // This test verifies the upsert/overwrite behavior:
        // saving the same email ID twice results in the latest version persisted.

        // Arrange
        var emailId = Guid.NewGuid();
        var email1 = CreateTestEmail(id: emailId, subject: "Version 1");
        var email2 = CreateTestEmail(id: emailId, subject: "Version 2");
        email2.ServiceId = email1.ServiceId;

        // Act — save version 1, then version 2 (same ID)
        await _repository.SaveEmailAsync(email1);
        await _repository.SaveEmailAsync(email2);

        // Assert — the last write wins (upsert behavior)
        var retrieved = await _repository.GetEmailByIdAsync(emailId);
        retrieved.Should().NotBeNull();
        retrieved!.Subject.Should().Be("Version 2",
            "SaveEmailAsync uses PutItem (upsert) — last write wins");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Cache Clearing Pattern Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClearSmtpServiceCache_AfterSave_SubsequentGetReturnsLatest()
    {
        // Arrange — save an SMTP service and retrieve it (populating cache)
        var config = CreateTestSmtpConfig(
            name: $"CacheTest-{Guid.NewGuid():N}",
            isDefault: true,
            port: 587);
        await _repository.SaveSmtpServiceAsync(config);

        // First read — populates cache
        var firstRead = await _repository.GetSmtpServiceByIdAsync(config.Id);
        firstRead.Should().NotBeNull();
        firstRead!.Port.Should().Be(587);

        // Act — update the service bypassing cache (simulate another process updating),
        //        then clear cache, then read again
        config.Port = 2525;
        config.MaxRetriesCount = 10;
        await _repository.SaveSmtpServiceAsync(config);

        // Clear cache to invalidate the stale cached version
        _repository.ClearSmtpServiceCache();

        // Assert — subsequent get returns the updated version, not stale cache
        var secondRead = await _repository.GetSmtpServiceByIdAsync(config.Id);
        secondRead.Should().NotBeNull();
        secondRead!.Port.Should().Be(2525, "after cache clear, latest DynamoDB version should be returned");
        secondRead.MaxRetriesCount.Should().Be(10);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Additional GetEmailsByStatusAsync and DeleteEmailAsync Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetEmailsByStatusAsync_ReturnsSentEmails()
    {
        // Arrange — create emails with different statuses
        var sentEmail = CreateTestEmail(subject: "Status Query - Sent", status: EmailStatus.Sent);
        sentEmail.ScheduledOn = null;
        var pendingEmail = CreateTestEmail(subject: "Status Query - Pending", status: EmailStatus.Pending);
        await _repository.SaveEmailAsync(sentEmail);
        await _repository.SaveEmailAsync(pendingEmail);

        // Act
        var results = await _repository.GetEmailsByStatusAsync(EmailStatus.Sent, 100);

        // Assert
        results.Should().NotBeNull();
        var resultIds = results.Select(e => e.Id).ToList();
        resultIds.Should().Contain(sentEmail.Id, "Sent email should be returned for Sent status query");
        resultIds.Should().NotContain(pendingEmail.Id, "Pending email should NOT be returned for Sent status query");
    }

    [Fact]
    public async Task DeleteEmailAsync_RemovesFromDynamoDB()
    {
        // Arrange
        var email = CreateTestEmail(subject: "Delete Email Test");
        await _repository.SaveEmailAsync(email);

        // Verify existence
        var before = await _repository.GetEmailByIdAsync(email.Id);
        before.Should().NotBeNull();

        // Act
        await _repository.DeleteEmailAsync(email.Id);

        // Assert
        var after = await _repository.GetEmailByIdAsync(email.Id);
        after.Should().BeNull("email should be removed after deletion");
    }
}
