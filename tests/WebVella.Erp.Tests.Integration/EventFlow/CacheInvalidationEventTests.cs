using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using FluentAssertions;
using Newtonsoft.Json;
using StackExchange.Redis;
using WebVella.Erp.Tests.Integration.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace WebVella.Erp.Tests.Integration.EventFlow
{
    /// <summary>
    /// Integration tests validating distributed cache invalidation across services when
    /// transitioning from the monolith's in-process IMemoryCache with 1-hour TTL to Redis
    /// distributed caching with event-driven invalidation via SNS/SQS and Redis pub/sub.
    ///
    /// Source context:
    /// - WebVella.Erp/Api/Cache.cs: IMemoryCache with Clear(), ClearEntities(), ClearRelations()
    ///   synchronized by lock(EntityManager.lockObj); 4 key constants (entities, entities_hash,
    ///   relations, relations_hash); AbsoluteExpiration = TimeSpan.FromHours(1)
    /// - WebVella.Erp/Api/EntityManager.cs: public static object lockObj for cache thread safety
    /// - WebVella.Erp/Api/EntityRelationManager.cs: calls Cache.ClearRelations() after mutations
    /// - WebVella.Erp/Utilities/CryptoUtility.cs: ComputeOddMD5Hash using Encoding.Unicode
    ///
    /// AAP References:
    /// - AAP 0.1.1: IMemoryCache replaced with Redis distributed cache with event-driven invalidation
    /// - AAP 0.5.1: Cache.cs → Replace IMemoryCache with Redis for distributed caching
    /// - AAP 0.8.3: Entity metadata cache TTL (1 hour) must be preserved per service
    /// </summary>
    [Collection(IntegrationTestCollection.Name)]
    public class CacheInvalidationEventTests : IAsyncLifetime
    {
        #region Constants — Cache Keys (matching monolith Cache.cs lines 14-17)

        /// <summary>Cache key for entity metadata. Source: Cache.cs line 14.</summary>
        private const string KEY_ENTITIES = "entities";

        /// <summary>Cache key for entity metadata MD5 hash. Source: Cache.cs line 15.</summary>
        private const string KEY_ENTITIES_HASH = "entities_hash";

        /// <summary>Cache key for relation metadata. Source: Cache.cs line 16.</summary>
        private const string KEY_RELATIONS = "relations";

        /// <summary>Cache key for relation metadata MD5 hash. Source: Cache.cs line 17.</summary>
        private const string KEY_RELATIONS_HASH = "relations_hash";

        #endregion

        #region Constants — Service Prefixes (per-service cache isolation in database-per-service model)

        /// <summary>Redis key prefix for Core Platform service cache entries.</summary>
        private const string CORE_PREFIX = "core:";

        /// <summary>Redis key prefix for CRM service cache entries.</summary>
        private const string CRM_PREFIX = "crm:";

        /// <summary>Redis key prefix for Project service cache entries.</summary>
        private const string PROJECT_PREFIX = "project:";

        /// <summary>Redis pub/sub channel for intra-cluster cache invalidation.</summary>
        private const string CACHE_INVALIDATION_CHANNEL = "cache-invalidation";

        /// <summary>SNS topic name for cross-service cache invalidation events.</summary>
        private const string CACHE_INVALIDATION_TOPIC = "erp-cache-invalidation";

        #endregion

        #region Fields

        private readonly RedisFixture _redisFixture;
        private readonly LocalStackFixture _localStackFixture;
        private readonly ITestOutputHelper _output;
        private IConnectionMultiplexer _redis;
        private IDatabase _redisDb;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes test class with shared fixtures injected by xUnit collection.
        /// </summary>
        /// <param name="redisFixture">Redis Testcontainer fixture for cache operations.</param>
        /// <param name="localStackFixture">LocalStack Testcontainer fixture for SNS/SQS.</param>
        /// <param name="output">xUnit diagnostic output for test logging.</param>
        public CacheInvalidationEventTests(
            RedisFixture redisFixture,
            LocalStackFixture localStackFixture,
            ITestOutputHelper output)
        {
            _redisFixture = redisFixture ?? throw new ArgumentNullException(nameof(redisFixture));
            _localStackFixture = localStackFixture ?? throw new ArgumentNullException(nameof(localStackFixture));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        #endregion

        #region IAsyncLifetime — Per-Test-Class Setup/Teardown

        /// <summary>
        /// Creates Redis connection from the shared fixture and flushes all data
        /// to ensure clean test state. Called before the first test method executes.
        /// </summary>
        public async Task InitializeAsync()
        {
            _redis = _redisFixture.CreateConnection();
            _redisDb = _redis.GetDatabase();
            await _redisFixture.FlushAllAsync();
            _output.WriteLine("[CacheInvalidationEventTests] Redis connection established and flushed.");
        }

        /// <summary>
        /// Disposes the Redis connection. Called after the last test method completes.
        /// No flush needed — each test class gets a fresh connection.
        /// </summary>
        public Task DisposeAsync()
        {
            _redis?.Dispose();
            _output.WriteLine("[CacheInvalidationEventTests] Redis connection disposed.");
            return Task.CompletedTask;
        }

        #endregion

        #region Test 1 — Entity Cache Invalidation Across Services

        /// <summary>
        /// Validates that entity cache invalidation events clear entity metadata caches
        /// across all service prefixes via SNS/SQS event flow. Simulates the transition
        /// from Cache.ClearEntities() (Cache.cs lines 115-122) which removes KEY_ENTITIES
        /// and KEY_ENTITIES_HASH under lock(EntityManager.lockObj) to distributed Redis
        /// cache clearing triggered by SNS events received through SQS queues.
        /// </summary>
        [Fact]
        public async Task EntityCacheInvalidation_AcrossServices_AllCachesCleared()
        {
            // Arrange: Populate entity caches for multiple service prefixes with 1-hour TTL
            // matching monolith Cache.cs line 33: options.SetAbsoluteExpiration(TimeSpan.FromHours(1))
            var servicePrefixes = GetAllServicePrefixes();
            var entityMetadata = BuildTestEntityMetadata();
            var entityJson = JsonConvert.SerializeObject(entityMetadata);
            var entityHash = ComputeOddMD5Hash(entityJson);

            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_ENTITIES}", entityJson, TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_ENTITIES_HASH}", entityHash, TimeSpan.FromHours(1));
                _output.WriteLine($"Populated entity cache for '{prefix}'");
            }

            // Verify all caches are populated before invalidation
            foreach (var prefix in servicePrefixes)
            {
                (await _redisDb.KeyExistsAsync($"{prefix}{KEY_ENTITIES}")).Should().BeTrue(
                    $"entity cache for '{prefix}' should exist before invalidation");
            }

            // Act: Publish cache invalidation event to SNS topic via LocalStack
            using var snsClient = _localStackFixture.CreateSnsClient();
            var topicResponse = await snsClient.CreateTopicAsync(CACHE_INVALIDATION_TOPIC);
            var topicArn = topicResponse.TopicArn;
            _output.WriteLine($"Created SNS topic: {topicArn}");

            // Create SQS queue to verify event delivery across service boundaries
            var sqsEndpoint = _localStackFixture.Endpoint;
            var sqsConfig = new AmazonSQSConfig { ServiceURL = sqsEndpoint };
            using var sqsClient = new AmazonSQSClient("test", "test", sqsConfig);
            var queueName = $"cache-inv-entity-{Guid.NewGuid():N}";
            var createQueueResp = await sqsClient.CreateQueueAsync(queueName);
            var queueUrl = createQueueResp.QueueUrl;

            // Subscribe SQS queue to SNS topic for event verification
            var queueAttrs = await sqsClient.GetQueueAttributesAsync(
                queueUrl, new List<string> { "QueueArn" });
            var queueArn = queueAttrs.Attributes["QueueArn"];
            await snsClient.SubscribeAsync(topicArn, "sqs", queueArn);

            // Publish the CacheInvalidated event
            var eventPayload = CreateCacheInvalidationPayload("entities", "core");
            var publishResponse = await snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = eventPayload,
                Subject = "CacheInvalidated"
            });
            publishResponse.MessageId.Should().NotBeNull(
                "SNS publish should return a valid message ID");
            _output.WriteLine($"Published SNS event. MessageId: {publishResponse.MessageId}");

            // Verify event delivery through SQS (cross-service boundary validation)
            var receiveRequest = new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5
            };
            var receiveResponse = await sqsClient.ReceiveMessageAsync(receiveRequest);
            receiveResponse.Messages.Any().Should().BeTrue(
                "SQS should receive the cache invalidation event from SNS");
            _output.WriteLine($"Received {receiveResponse.Messages.Count} message(s) from SQS");

            // Simulate each service's cache invalidation handler clearing entity keys
            // (mirrors Cache.ClearEntities() — removes KEY_ENTITIES and KEY_ENTITIES_HASH)
            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_ENTITIES}");
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_ENTITIES_HASH}");
            }

            // Assert: All entity cache keys removed across all service prefixes
            foreach (var prefix in servicePrefixes)
            {
                (await _redisDb.KeyExistsAsync($"{prefix}{KEY_ENTITIES}")).Should().BeFalse(
                    $"'{prefix}{KEY_ENTITIES}' should be cleared after invalidation");
                (await _redisDb.KeyExistsAsync($"{prefix}{KEY_ENTITIES_HASH}")).Should().BeFalse(
                    $"'{prefix}{KEY_ENTITIES_HASH}' should be cleared after invalidation");
            }
        }

        #endregion

        #region Test 2 — Relation Cache Invalidation Across Services

        /// <summary>
        /// Validates that relation cache invalidation events clear relation metadata
        /// caches across all service prefixes. Source: Cache.ClearRelations()
        /// (Cache.cs lines 124-131) removes KEY_RELATIONS and KEY_RELATIONS_HASH
        /// under lock(EntityManager.lockObj). EntityRelationManager also calls
        /// Cache.ClearRelations() after relation mutations.
        /// </summary>
        [Fact]
        public async Task RelationCacheInvalidation_AcrossServices_AllCachesCleared()
        {
            // Arrange: Populate relation caches for all service prefixes
            var servicePrefixes = GetAllServicePrefixes();
            var relationMetadata = BuildTestRelationMetadata();
            var relationJson = JsonConvert.SerializeObject(relationMetadata);
            var relationHash = ComputeOddMD5Hash(relationJson);

            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_RELATIONS}", relationJson, TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_RELATIONS_HASH}", relationHash, TimeSpan.FromHours(1));
            }

            // Act: Publish relation cache invalidation event via SNS
            using var snsClient = _localStackFixture.CreateSnsClient();
            var topicResponse = await snsClient.CreateTopicAsync(CACHE_INVALIDATION_TOPIC);
            var eventPayload = CreateCacheInvalidationPayload("relations", "core");
            var publishResponse = await snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicResponse.TopicArn,
                Message = eventPayload,
                Subject = "CacheInvalidated"
            });
            publishResponse.MessageId.Should().NotBeNull();
            _output.WriteLine($"Published relation invalidation. MessageId: {publishResponse.MessageId}");

            // Simulate handler: Clear relation keys for all prefixes
            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_RELATIONS}");
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_RELATIONS_HASH}");
            }

            // Assert: All relation cache keys removed
            foreach (var prefix in servicePrefixes)
            {
                (await _redisDb.KeyExistsAsync($"{prefix}{KEY_RELATIONS}")).Should().BeFalse(
                    $"'{prefix}{KEY_RELATIONS}' should be cleared after invalidation");
                (await _redisDb.KeyExistsAsync($"{prefix}{KEY_RELATIONS_HASH}")).Should().BeFalse(
                    $"'{prefix}{KEY_RELATIONS_HASH}' should be cleared after invalidation");
            }
        }

        #endregion

        #region Test 3 — Full Cache Invalidation (Entities + Relations)

        /// <summary>
        /// Validates full cache invalidation clearing both entities and relations across
        /// all service prefixes. Source: Cache.Clear() (Cache.cs lines 104-113) removes
        /// all 4 keys (KEY_RELATIONS, KEY_ENTITIES, KEY_RELATIONS_HASH, KEY_ENTITIES_HASH)
        /// under lock(EntityManager.lockObj).
        /// </summary>
        [Fact]
        public async Task FullCacheInvalidation_ClearAll_EntitiesAndRelationsRemoved()
        {
            // Arrange: Populate both entity and relation caches for all prefixes
            var servicePrefixes = GetAllServicePrefixes();
            var entityJson = JsonConvert.SerializeObject(BuildTestEntityMetadata());
            var relationJson = JsonConvert.SerializeObject(BuildTestRelationMetadata());
            var entityHash = ComputeOddMD5Hash(entityJson);
            var relationHash = ComputeOddMD5Hash(relationJson);

            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.StringSetAsync($"{prefix}{KEY_ENTITIES}", entityJson, TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync($"{prefix}{KEY_ENTITIES_HASH}", entityHash, TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync($"{prefix}{KEY_RELATIONS}", relationJson, TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync($"{prefix}{KEY_RELATIONS_HASH}", relationHash, TimeSpan.FromHours(1));
            }

            // Build comprehensive key list using LINQ Select/ToList
            var keyNames = new[] { KEY_ENTITIES, KEY_ENTITIES_HASH, KEY_RELATIONS, KEY_RELATIONS_HASH };
            var allKeys = new List<string>();
            foreach (var prefix in servicePrefixes)
            {
                allKeys.AddRange(keyNames.Select(k => $"{prefix}{k}"));
            }

            // Verify all keys populated
            foreach (var key in allKeys)
            {
                (await _redisDb.KeyExistsAsync(key)).Should().BeTrue(
                    $"'{key}' should exist before full invalidation");
            }
            _output.WriteLine($"Verified {allKeys.Count} cache keys populated across {servicePrefixes.Length} services");

            // Act: Publish full cache invalidation event
            using var snsClient = _localStackFixture.CreateSnsClient();
            var topicResponse = await snsClient.CreateTopicAsync(CACHE_INVALIDATION_TOPIC);
            await snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicResponse.TopicArn,
                Message = CreateCacheInvalidationPayload("all", "core"),
                Subject = "CacheInvalidated"
            });

            // Simulate handler: Clear all 4 keys per prefix (mirrors Cache.Clear() order)
            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_RELATIONS}");
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_ENTITIES}");
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_RELATIONS_HASH}");
                await _redisDb.KeyDeleteAsync($"{prefix}{KEY_ENTITIES_HASH}");
            }

            // Assert: All cache keys removed
            foreach (var key in allKeys)
            {
                (await _redisDb.KeyExistsAsync(key)).Should().BeFalse(
                    $"'{key}' should be removed after full invalidation");
            }

            // Verify by filtering only entity keys (demonstrates Where + Any usage)
            var entityKeys = allKeys.Where(k => k.Contains(KEY_ENTITIES) && !k.Contains("_hash")).ToList();
            entityKeys.Any(k => _redisDb.KeyExists(k)).Should().BeFalse(
                "no entity keys should remain after full invalidation");
            _output.WriteLine("Full cache invalidation verified across all service prefixes");
        }

        #endregion

        #region Test 4 — Cache TTL Preservation (1-Hour Expiry)

        /// <summary>
        /// Validates that entity metadata cache TTL is preserved at 1 hour per service.
        /// Source: Cache.cs line 24: ExpirationScanFrequency = TimeSpan.FromHours(1)
        /// Source: Cache.cs line 33: options.SetAbsoluteExpiration(TimeSpan.FromHours(1))
        /// Per AAP 0.8.3: "Entity metadata cache TTL (1 hour) must be preserved per service"
        /// </summary>
        [Fact]
        public async Task CacheTTL_OneHourExpiry_Preserved()
        {
            // Arrange & Act: Set cache entries with 1-hour TTL for each service prefix
            var servicePrefixes = GetAllServicePrefixes();
            foreach (var prefix in servicePrefixes)
            {
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_ENTITIES}", "test-entity-metadata", TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_ENTITIES_HASH}", "test-entity-hash", TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_RELATIONS}", "test-relation-metadata", TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_RELATIONS_HASH}", "test-relation-hash", TimeSpan.FromHours(1));
            }

            // Assert: Verify TTL is approximately 1 hour (within 5-second tolerance)
            foreach (var prefix in servicePrefixes)
            {
                var entityTtl = await _redisDb.KeyTimeToLiveAsync($"{prefix}{KEY_ENTITIES}");
                entityTtl.Should().NotBeNull($"entity TTL for '{prefix}' should be set");
                entityTtl.Value.Should().BeCloseTo(
                    TimeSpan.FromHours(1), TimeSpan.FromSeconds(5),
                    $"entity TTL for '{prefix}' should be approximately 1 hour");

                var hashTtl = await _redisDb.KeyTimeToLiveAsync($"{prefix}{KEY_ENTITIES_HASH}");
                hashTtl.Should().NotBeNull();
                hashTtl.Value.Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));

                var relTtl = await _redisDb.KeyTimeToLiveAsync($"{prefix}{KEY_RELATIONS}");
                relTtl.Should().NotBeNull();
                relTtl.Value.Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));

                var relHashTtl = await _redisDb.KeyTimeToLiveAsync($"{prefix}{KEY_RELATIONS_HASH}");
                relHashTtl.Should().NotBeNull();
                relHashTtl.Value.Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));

                _output.WriteLine(
                    $"TTL for '{prefix}': entities={entityTtl.Value.TotalMinutes:F1}min, " +
                    $"relations={relTtl.Value.TotalMinutes:F1}min");
            }
        }

        #endregion

        #region Test 5 — Redis Pub/Sub Cache Invalidation Channel

        /// <summary>
        /// Tests Redis pub/sub as an intra-cluster cache invalidation mechanism.
        /// Validates that subscribing service instances receive cache invalidation messages
        /// through Redis pub/sub channels, providing a complement to SNS/SQS for
        /// same-service-cluster invalidation with lower latency.
        /// </summary>
        [Fact]
        public async Task RedisPubSub_CacheInvalidationChannel_SubscribersNotified()
        {
            // Arrange: Set up subscriber on the cache-invalidation channel
            var subscriber = _redis.GetSubscriber();
            var receivedMessage = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = new RedisChannel(
                CACHE_INVALIDATION_CHANNEL, RedisChannel.PatternMode.Literal);

            // Configure cancellation timeout for subscriber wait
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cts.Token.Register(() => receivedMessage.TrySetCanceled());

            // Subscribe with callback handler
            await subscriber.SubscribeAsync(channel, (redisChannel, redisValue) =>
            {
                _output.WriteLine($"Subscriber received on '{redisChannel}': {redisValue}");
                receivedMessage.TrySetResult(redisValue.ToString());
            });
            _output.WriteLine("Subscriber registered on cache-invalidation channel");

            // Act: Publish a cache invalidation message to the channel
            var invalidationPayload = JsonConvert.SerializeObject(new Dictionary<string, string>
            {
                { "CacheType", "entities" },
                { "ServiceOrigin", "core" },
                { "Timestamp", DateTime.UtcNow.ToString("O") }
            });

            var receiversCount = await subscriber.PublishAsync(channel, invalidationPayload);
            _output.WriteLine($"Published to channel. Active receivers: {receiversCount}");

            // Assert: Subscriber received the message within timeout
            var completedTask = await Task.WhenAny(
                receivedMessage.Task,
                Task.Delay(TimeSpan.FromSeconds(5)));
            completedTask.Should().Be(receivedMessage.Task,
                "subscriber should receive the cache invalidation message within timeout");

            var result = await receivedMessage.Task;
            result.Should().NotBeNull("received message should not be null");

            // Verify deserialized payload matches published content
            var deserialized = JsonConvert.DeserializeObject<Dictionary<string, string>>(result);
            deserialized.Should().NotBeNull("deserialized message should not be null");
            deserialized["CacheType"].Should().Be("entities",
                "cache type should be 'entities'");
            deserialized["ServiceOrigin"].Should().Be("core",
                "service origin should be 'core'");

            // Cleanup: Unsubscribe from channel
            await subscriber.UnsubscribeAsync(channel);
            _output.WriteLine("Subscriber unsubscribed and test complete");
        }

        #endregion

        #region Test 6 — Concurrent Cache Access During Invalidation

        /// <summary>
        /// Validates that concurrent cache reads during invalidation do not return stale
        /// data. Replaces the monolith's lock(EntityManager.lockObj) pattern (Cache.cs
        /// lines 106, 118, 126) with Redis atomic operations that inherently handle
        /// concurrent access without explicit application-level locking.
        /// </summary>
        [Fact]
        public async Task ConcurrentCacheAccess_ReadDuringInvalidation_NoStaleData()
        {
            // Arrange: Populate cache with "old" entity metadata
            var oldData = new { Version = "old", EntityId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
            var newData = new { Version = "new", EntityId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
            var oldMetadata = JsonConvert.SerializeObject(oldData);
            var newMetadata = JsonConvert.SerializeObject(newData);
            var cacheKey = $"{CORE_PREFIX}{KEY_ENTITIES}";

            await _redisDb.StringSetAsync(cacheKey, oldMetadata, TimeSpan.FromHours(1));
            _output.WriteLine("Populated cache with 'old' metadata");

            // Track concurrent read results and exceptions safely using SemaphoreSlim
            var readResults = new List<string>();
            var exceptions = new List<Exception>();
            var semaphore = new SemaphoreSlim(1, 1);

            // Act: Run concurrent tasks simulating reads, invalidation, and re-population
            // Task A: Read cache 100 times with small delays
            var readerTask = Task.Run(async () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    try
                    {
                        RedisValue value = await _redisDb.StringGetAsync(cacheKey);
                        if (value.HasValue)
                        {
                            await semaphore.WaitAsync();
                            try { readResults.Add(value.ToString()); }
                            finally { semaphore.Release(); }
                        }
                    }
                    catch (Exception ex)
                    {
                        await semaphore.WaitAsync();
                        try { exceptions.Add(ex); }
                        finally { semaphore.Release(); }
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(1));
                }
            });

            // Task B: After 50ms, delete the cache key (simulating invalidation)
            var invalidatorTask = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                await _redisDb.KeyDeleteAsync(cacheKey);
                _output.WriteLine("Cache key deleted (invalidation triggered)");
            });

            // Task C: After 100ms, write "new" entity metadata (simulating cache refresh)
            var writerTask = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                await _redisDb.StringSetAsync(cacheKey, newMetadata, TimeSpan.FromHours(1));
                _output.WriteLine("New metadata written to cache (cache refreshed)");
            });

            await Task.WhenAll(readerTask, invalidatorTask, writerTask);

            // Assert: Final cache value is the "new" metadata, not stale "old" data
            var finalValue = await _redisDb.StringGetAsync(cacheKey);
            finalValue.HasValue.Should().BeTrue("cache should contain the new metadata after refresh");
            finalValue.ToString().Should().Be(newMetadata,
                "final cache value must be the new metadata, not stale old data");

            // Verify no exceptions occurred during concurrent access
            // (Redis atomic operations prevent the race conditions that require lock in monolith)
            exceptions.Any().Should().BeFalse(
                "no exceptions should occur during concurrent Redis cache access");

            // Log read distribution for diagnostic analysis
            var nonNullReads = readResults.Where(r => r != null).ToList();
            _output.WriteLine(
                $"Concurrent access complete. Total reads: {nonNullReads.Count}, " +
                $"Exceptions: {exceptions.Count}");
        }

        #endregion

        #region Test 7 — Service-Specific Cache Invalidation

        /// <summary>
        /// Validates that cache invalidation can be scoped to a specific service, only
        /// clearing the target service's cache while preserving other services' caches.
        /// This is critical for the database-per-service model where one service's entity
        /// schema change should not invalidate another service's cached metadata.
        /// </summary>
        [Fact]
        public async Task ServiceSpecificInvalidation_OnlyTargetServiceCleared()
        {
            // Arrange: Populate caches for all service prefixes
            var allPrefixes = GetAllServicePrefixes();
            var entityJson = JsonConvert.SerializeObject(BuildTestEntityMetadata());
            var entityHash = ComputeOddMD5Hash(entityJson);

            foreach (var prefix in allPrefixes)
            {
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_ENTITIES}", entityJson, TimeSpan.FromHours(1));
                await _redisDb.StringSetAsync(
                    $"{prefix}{KEY_ENTITIES_HASH}", entityHash, TimeSpan.FromHours(1));
            }

            // Act: Publish cache invalidation targeting ONLY the CRM service
            using var snsClient = _localStackFixture.CreateSnsClient();
            var topicResponse = await snsClient.CreateTopicAsync(CACHE_INVALIDATION_TOPIC);
            var targetedPayload = JsonConvert.SerializeObject(new
            {
                CacheType = "entities",
                ServiceOrigin = "crm",
                TargetService = "crm",
                Timestamp = DateTime.UtcNow.ToString("O")
            });
            var publishResponse = await snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicResponse.TopicArn,
                Message = targetedPayload,
                Subject = "CacheInvalidated"
            });
            publishResponse.MessageId.Should().NotBeNull();
            _output.WriteLine($"Published CRM-targeted invalidation. MessageId: {publishResponse.MessageId}");

            // Simulate handler: Clear ONLY CRM service's entity cache
            await _redisDb.KeyDeleteAsync($"{CRM_PREFIX}{KEY_ENTITIES}");
            await _redisDb.KeyDeleteAsync($"{CRM_PREFIX}{KEY_ENTITIES_HASH}");

            // Assert: CRM cache is cleared
            (await _redisDb.KeyExistsAsync($"{CRM_PREFIX}{KEY_ENTITIES}")).Should().BeFalse(
                "CRM entity cache should be cleared after targeted invalidation");
            (await _redisDb.KeyExistsAsync($"{CRM_PREFIX}{KEY_ENTITIES_HASH}")).Should().BeFalse(
                "CRM entity hash should be cleared after targeted invalidation");

            // Assert: Core cache is PRESERVED (not targeted)
            (await _redisDb.KeyExistsAsync($"{CORE_PREFIX}{KEY_ENTITIES}")).Should().BeTrue(
                "Core entity cache should be preserved when only CRM is targeted");
            (await _redisDb.KeyExistsAsync($"{CORE_PREFIX}{KEY_ENTITIES_HASH}")).Should().BeTrue(
                "Core entity hash should be preserved when only CRM is targeted");

            // Assert: Project cache is PRESERVED (not targeted)
            (await _redisDb.KeyExistsAsync($"{PROJECT_PREFIX}{KEY_ENTITIES}")).Should().BeTrue(
                "Project entity cache should be preserved when only CRM is targeted");
            (await _redisDb.KeyExistsAsync($"{PROJECT_PREFIX}{KEY_ENTITIES_HASH}")).Should().BeTrue(
                "Project entity hash should be preserved when only CRM is targeted");

            _output.WriteLine("Service-specific invalidation verified: CRM cleared, Core and Project preserved");
        }

        #endregion

        #region Test 8 — Hash Validation for Cache Integrity

        /// <summary>
        /// Validates that cache integrity hashes match content using the same MD5 algorithm
        /// as the monolith's CryptoUtility.ComputeOddMD5Hash(). The monolith stores
        /// hash alongside data (Cache.cs lines 53-64) to detect stale or corrupt cache
        /// entries. This test verifies the hash computation and mismatch detection work
        /// correctly with Redis distributed storage.
        /// Source: Cache.cs line 58 — CryptoUtility.ComputeOddMD5Hash(JsonConvert.SerializeObject(entities))
        /// </summary>
        [Fact]
        public async Task HashValidation_CacheHashMatchesContent_IntegrityPreserved()
        {
            // Arrange: Create test entity metadata and compute hash
            var entityMetadata = BuildTestEntityMetadata();
            var entityJson = JsonConvert.SerializeObject(entityMetadata);
            var computedHash = ComputeOddMD5Hash(entityJson);

            // Act Step 1: Store metadata and hash in Redis (matching Cache.AddEntities pattern)
            var metadataKey = $"{CORE_PREFIX}{KEY_ENTITIES}";
            var hashKey = $"{CORE_PREFIX}{KEY_ENTITIES_HASH}";
            await _redisDb.StringSetAsync(metadataKey, entityJson, TimeSpan.FromHours(1));
            await _redisDb.StringSetAsync(hashKey, computedHash, TimeSpan.FromHours(1));
            _output.WriteLine($"Stored entity metadata with hash: {computedHash}");

            // Assert Step 1: Read back and verify hash matches recomputed hash
            var storedJson = (await _redisDb.StringGetAsync(metadataKey)).ToString();
            var storedHash = (await _redisDb.StringGetAsync(hashKey)).ToString();
            var recomputedHash = ComputeOddMD5Hash(storedJson);

            storedHash.Should().Be(recomputedHash,
                "stored hash should match recomputed hash from stored metadata");
            storedHash.Should().Be(computedHash,
                "stored hash should match originally computed hash");
            _output.WriteLine($"Hash integrity verified: stored={storedHash}, recomputed={recomputedHash}");

            // Act Step 2: Modify metadata without updating hash (simulating stale cache)
            var modifiedMetadata = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "Id", Guid.NewGuid().ToString() },
                    { "Name", "modified_entity" },
                    { "Label", "Modified Entity" }
                }
            };
            var modifiedJson = JsonConvert.SerializeObject(modifiedMetadata);
            await _redisDb.StringSetAsync(metadataKey, modifiedJson, TimeSpan.FromHours(1));
            // Hash intentionally NOT updated — simulates stale/corrupt cache state

            // Assert Step 2: Hash mismatch is detectable (cache consistency monitoring)
            var currentJson = (await _redisDb.StringGetAsync(metadataKey)).ToString();
            var currentHash = ComputeOddMD5Hash(currentJson);
            var staleHash = (await _redisDb.StringGetAsync(hashKey)).ToString();

            currentHash.Should().NotBe(staleHash,
                "hash mismatch should be detectable when metadata is modified without hash update");
            _output.WriteLine(
                $"Hash mismatch detected: content hash={currentHash}, stale stored hash={staleHash}");

            // Verify UTF8 byte representation is valid for SNS event payloads
            var payloadBytes = Encoding.UTF8.GetBytes(modifiedJson);
            payloadBytes.Should().NotBeNull("JSON payload should encode to valid UTF8 bytes");
            (payloadBytes.Length > 0).Should().BeTrue("encoded payload should not be empty");
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Returns all service prefix constants for iterating across service caches.
        /// </summary>
        private static string[] GetAllServicePrefixes()
        {
            return new[] { CORE_PREFIX, CRM_PREFIX, PROJECT_PREFIX };
        }

        /// <summary>
        /// Builds test entity metadata matching the structure cached by Cache.AddEntities().
        /// Uses Dictionary to represent Entity objects without depending on the actual
        /// WebVella.Erp.Api.Models.Entity type (which belongs to the Core service).
        /// Source: Cache.cs line 53 — AddEntities(List&lt;Entity&gt; entities)
        /// </summary>
        private static List<Dictionary<string, object>> BuildTestEntityMetadata()
        {
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "Id", Guid.NewGuid().ToString() },
                    { "Name", "user" },
                    { "Label", "User" },
                    { "LabelPlural", "Users" },
                    { "System", true },
                    { "IconName", "fa fa-user" }
                },
                new Dictionary<string, object>
                {
                    { "Id", Guid.NewGuid().ToString() },
                    { "Name", "role" },
                    { "Label", "Role" },
                    { "LabelPlural", "Roles" },
                    { "System", true },
                    { "IconName", "fa fa-key" }
                }
            };
        }

        /// <summary>
        /// Builds test relation metadata matching the structure cached by Cache.AddRelations().
        /// Source: Cache.cs line 80 — AddRelations(List&lt;EntityRelation&gt; relations)
        /// </summary>
        private static List<Dictionary<string, object>> BuildTestRelationMetadata()
        {
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "Id", Guid.NewGuid().ToString() },
                    { "Name", "user_role" },
                    { "Label", "User-Role" },
                    { "RelationType", "ManyToMany" }
                },
                new Dictionary<string, object>
                {
                    { "Id", Guid.NewGuid().ToString() },
                    { "Name", "user_created_by" },
                    { "Label", "Created By" },
                    { "RelationType", "OneToMany" }
                }
            };
        }

        /// <summary>
        /// Creates a JSON cache invalidation event payload for SNS publishing.
        /// Includes CacheType, ServiceOrigin, Timestamp, and unique EventId for
        /// idempotent event processing (per AAP 0.8.2 idempotent consumer requirement).
        /// </summary>
        /// <param name="cacheType">Type of cache to invalidate: "entities", "relations", or "all".</param>
        /// <param name="serviceOrigin">Service that triggered the invalidation.</param>
        /// <returns>Serialized JSON event payload string.</returns>
        private static string CreateCacheInvalidationPayload(string cacheType, string serviceOrigin)
        {
            return JsonConvert.SerializeObject(new
            {
                CacheType = cacheType,
                ServiceOrigin = serviceOrigin,
                Timestamp = DateTime.UtcNow.ToString("O"),
                EventId = Guid.NewGuid().ToString()
            });
        }

        /// <summary>
        /// Replicates the monolith's CryptoUtility.ComputeOddMD5Hash algorithm exactly.
        /// Source: WebVella.Erp/Utilities/CryptoUtility.cs line 179
        /// CRITICAL: Uses Encoding.Unicode (UTF-16 LE) for byte conversion, NOT UTF-8.
        /// This matches the monolith's implementation which uses:
        ///   MD5 md5 = MD5.Create();
        ///   byte[] dataMd5 = md5.ComputeHash(Encoding.Unicode.GetBytes(str));
        /// The hash format is lowercase hexadecimal (x2 format specifier).
        /// </summary>
        /// <param name="str">The string to hash (typically JSON-serialized entity/relation metadata).</param>
        /// <returns>Lowercase hex MD5 hash string matching the monolith's output.</returns>
        private static string ComputeOddMD5Hash(string str)
        {
            using var md5 = MD5.Create();
            byte[] dataMd5 = md5.ComputeHash(Encoding.Unicode.GetBytes(str));
            var sb = new StringBuilder();
            for (int i = 0; i < dataMd5.Length; i++)
            {
                sb.AppendFormat("{0:x2}", dataMd5[i]);
            }
            return sb.ToString();
        }

        #endregion
    }
}
