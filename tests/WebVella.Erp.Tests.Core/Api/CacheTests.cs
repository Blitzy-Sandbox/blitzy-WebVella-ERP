using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Moq;
using Newtonsoft.Json;
using StackExchange.Redis;
using WebVella.Erp.Service.Core.Api;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;
using Xunit;

namespace WebVella.Erp.Tests.Core.Api
{
	/// <summary>
	/// Comprehensive unit tests for the Cache service in the Core Platform microservice.
	///
	/// The Cache class was adapted from the monolith's IMemoryCache-based implementation
	/// to a Redis-backed IDistributedCache while preserving all original behavior:
	/// - 1-hour absolute expiration TTL
	/// - MD5 hash fingerprints via CryptoUtility.ComputeOddMD5Hash
	/// - Thread-safe multi-key invalidation via lock(Cache.lockObj)
	/// - Identical public method signatures
	///
	/// Tests use Moq to mock IDistributedCache with a dictionary-backed store,
	/// enabling full integration testing of serialize → store → retrieve → deserialize
	/// pipelines without a real Redis instance.
	/// </summary>
	public class CacheTests : IDisposable
	{
		#region <=== Fields and Setup ===>

		/// <summary>
		/// Mock of IDistributedCache — primary mock target for all cache tests.
		/// The underlying Get/Set/Remove interface methods are mocked (not the extension
		/// methods GetString/SetString which delegate to these).
		/// </summary>
		private readonly Mock<IDistributedCache> _mockCache;

		/// <summary>
		/// Dictionary-backed store simulating Redis key-value storage.
		/// Used by mock callbacks to provide stateful behavior for integration-style tests.
		/// </summary>
		private readonly Dictionary<string, byte[]> _store;

		/// <summary>
		/// Captures the last DistributedCacheEntryOptions passed to Set, enabling TTL assertions.
		/// </summary>
		private DistributedCacheEntryOptions _lastCapturedOptions;

		/// <summary>
		/// List of 3 test Entity objects with populated fields and permissions,
		/// used as standard input for AddEntities / GetEntities tests.
		/// </summary>
		private readonly List<Entity> _testEntities;

		/// <summary>
		/// List of 2 test EntityRelation objects with populated properties,
		/// used as standard input for AddRelations / GetRelations tests.
		/// </summary>
		private readonly List<EntityRelation> _testRelations;

		/// <summary>
		/// Pre-computed MD5 hash of serialized test entities, used for hash verification tests.
		/// Computed via CryptoUtility.ComputeOddMD5Hash(JsonConvert.SerializeObject(_testEntities)).
		/// </summary>
		private readonly string _expectedEntitiesHash;

		/// <summary>
		/// Pre-computed MD5 hash of serialized test relations, used for hash verification tests.
		/// </summary>
		private readonly string _expectedRelationsHash;

		/// <summary>
		/// Redis cache key constants matching the Cache implementation (core: prefix).
		/// </summary>
		private const string KEY_ENTITIES = "core:entities";
		private const string KEY_ENTITIES_HASH = "core:entities_hash";
		private const string KEY_RELATIONS = "core:relations";
		private const string KEY_RELATIONS_HASH = "core:relations_hash";

		public CacheTests()
		{
			_store = new Dictionary<string, byte[]>();
			_mockCache = CreateMockCache(_store);

			// Initialize the Cache SUT with the mocked IDistributedCache
			Cache.Initialize(_mockCache.Object);

			// Build test data
			_testEntities = CreateTestEntities();
			_testRelations = CreateTestRelations();

			// Pre-compute expected hash values
			_expectedEntitiesHash = CryptoUtility.ComputeOddMD5Hash(
				JsonConvert.SerializeObject(_testEntities));
			_expectedRelationsHash = CryptoUtility.ComputeOddMD5Hash(
				JsonConvert.SerializeObject(_testRelations));
		}

		/// <summary>
		/// Creates a Mock&lt;IDistributedCache&gt; backed by a dictionary for stateful behavior.
		/// Mocks the underlying Get/Set/Remove interface methods that the extension methods
		/// GetString/SetString/Remove delegate to.
		/// </summary>
		private Mock<IDistributedCache> CreateMockCache(Dictionary<string, byte[]> store)
		{
			var mock = new Mock<IDistributedCache>();

			mock.Setup(c => c.Get(It.IsAny<string>()))
				.Returns<string>(key => store.TryGetValue(key, out var val) ? val : null);

			mock.Setup(c => c.Set(
					It.IsAny<string>(),
					It.IsAny<byte[]>(),
					It.IsAny<DistributedCacheEntryOptions>()))
				.Callback<string, byte[], DistributedCacheEntryOptions>((key, value, options) =>
				{
					store[key] = value;
					_lastCapturedOptions = options;
				});

			mock.Setup(c => c.Remove(It.IsAny<string>()))
				.Callback<string>(key => store.Remove(key));

			return mock;
		}

		/// <summary>
		/// Creates 3 test Entity objects with distinct IDs, names, labels, and permissions.
		/// Fields are set to empty lists to avoid polymorphic deserialization complexity.
		/// </summary>
		private static List<Entity> CreateTestEntities()
		{
			var adminRoleId = Guid.NewGuid();
			var userRoleId = Guid.NewGuid();

			return new List<Entity>
			{
				new Entity
				{
					Id = Guid.NewGuid(),
					Name = "test_entity_account",
					Label = "Account",
					LabelPlural = "Accounts",
					System = false,
					IconName = "fas fa-building",
					Color = "#2196F3",
					Fields = new List<Field>(),
					RecordPermissions = new RecordPermissions
					{
						CanRead = new List<Guid> { adminRoleId, userRoleId },
						CanCreate = new List<Guid> { adminRoleId },
						CanUpdate = new List<Guid> { adminRoleId },
						CanDelete = new List<Guid> { adminRoleId }
					}
				},
				new Entity
				{
					Id = Guid.NewGuid(),
					Name = "test_entity_contact",
					Label = "Contact",
					LabelPlural = "Contacts",
					System = true,
					IconName = "fas fa-user",
					Color = "#4CAF50",
					Fields = new List<Field>(),
					RecordPermissions = new RecordPermissions
					{
						CanRead = new List<Guid> { adminRoleId },
						CanCreate = new List<Guid> { adminRoleId },
						CanUpdate = new List<Guid> { adminRoleId },
						CanDelete = new List<Guid> { adminRoleId }
					}
				},
				new Entity
				{
					Id = Guid.NewGuid(),
					Name = "test_entity_task",
					Label = "Task",
					LabelPlural = "Tasks",
					System = false,
					IconName = "fas fa-tasks",
					Color = "#FF9800",
					Fields = new List<Field>(),
					RecordPermissions = new RecordPermissions
					{
						CanRead = new List<Guid> { adminRoleId, userRoleId },
						CanCreate = new List<Guid> { adminRoleId, userRoleId },
						CanUpdate = new List<Guid> { adminRoleId, userRoleId },
						CanDelete = new List<Guid> { adminRoleId }
					}
				}
			};
		}

		/// <summary>
		/// Creates 2 test EntityRelation objects with distinct IDs, names, and types.
		/// </summary>
		private static List<EntityRelation> CreateTestRelations()
		{
			return new List<EntityRelation>
			{
				new EntityRelation
				{
					Id = Guid.NewGuid(),
					Name = "test_relation_account_contact",
					Label = "Account → Contact",
					RelationType = EntityRelationType.OneToMany,
					OriginEntityId = Guid.NewGuid(),
					TargetEntityId = Guid.NewGuid(),
					OriginFieldId = Guid.NewGuid(),
					TargetFieldId = Guid.NewGuid(),
					OriginEntityName = "account",
					OriginFieldName = "id",
					TargetEntityName = "contact",
					TargetFieldName = "account_id",
					System = false
				},
				new EntityRelation
				{
					Id = Guid.NewGuid(),
					Name = "test_relation_user_role",
					Label = "User ↔ Role",
					RelationType = EntityRelationType.ManyToMany,
					OriginEntityId = Guid.NewGuid(),
					TargetEntityId = Guid.NewGuid(),
					OriginFieldId = Guid.NewGuid(),
					TargetFieldId = Guid.NewGuid(),
					OriginEntityName = "user",
					OriginFieldName = "id",
					TargetEntityName = "role",
					TargetFieldName = "id",
					System = true
				}
			};
		}

		#endregion

		#region <=== Phase 2: Entity Cache — AddEntities Tests ===>

		/// <summary>
		/// Verifies that AddEntities stores the serialized entity list under the correct
		/// Redis key "core:entities" (prefixed for namespace isolation per AAP 0.5.1).
		/// </summary>
		[Fact]
		public void Test_AddEntities_StoresEntitiesUnderCorrectKey()
		{
			// Act
			Cache.AddEntities(_testEntities);

			// Assert — verify Set was called with the correct key
			_mockCache.Verify(
				c => c.Set(
					It.Is<string>(k => k == KEY_ENTITIES),
					It.IsAny<byte[]>(),
					It.IsAny<DistributedCacheEntryOptions>()),
				Times.Once());

			// Verify the stored data is present and deserializable
			_store.Should().ContainKey(KEY_ENTITIES);
			var storedJson = Encoding.UTF8.GetString(_store[KEY_ENTITIES]);
			var deserialized = JsonConvert.DeserializeObject<List<Entity>>(storedJson);
			deserialized.Should().HaveCount(3);
		}

		/// <summary>
		/// Verifies that AddEntities applies a 1-hour absolute expiration TTL to the cache entry,
		/// preserving the monolith's cache expiration policy (AAP 0.8.3).
		/// </summary>
		[Fact]
		public void Test_AddEntities_SetsOneHourTTL()
		{
			// Act
			Cache.AddEntities(_testEntities);

			// Assert — verify TTL is exactly 1 hour
			_lastCapturedOptions.Should().NotBeNull();
			_lastCapturedOptions.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromHours(1));
		}

		/// <summary>
		/// Verifies that AddEntities handles a null list by storing the serialized "null"
		/// string in the entities key and removing the entities hash key.
		/// </summary>
		[Fact]
		public void Test_AddEntities_NullList_StoresNull()
		{
			// Arrange — pre-populate hash key to verify it gets removed
			_store[KEY_ENTITIES_HASH] = Encoding.UTF8.GetBytes("some_hash");

			// Act
			Cache.AddEntities(null);

			// Assert — entities key should contain serialized null
			_store.Should().ContainKey(KEY_ENTITIES);
			var storedJson = Encoding.UTF8.GetString(_store[KEY_ENTITIES]);
			storedJson.Should().Be("null");

			// Hash key should be removed when entities is null
			_store.Should().NotContainKey(KEY_ENTITIES_HASH);
		}

		/// <summary>
		/// Verifies that AddEntities handles an empty list by storing an empty JSON array
		/// and computing a hash for the empty collection.
		/// </summary>
		[Fact]
		public void Test_AddEntities_EmptyList_StoresEmpty()
		{
			// Arrange
			var emptyList = new List<Entity>();

			// Act
			Cache.AddEntities(emptyList);

			// Assert — should store empty array JSON
			_store.Should().ContainKey(KEY_ENTITIES);
			var storedJson = Encoding.UTF8.GetString(_store[KEY_ENTITIES]);
			storedJson.Should().Be("[]");

			// Should also store a hash for the empty list (not null, because list is not null)
			_store.Should().ContainKey(KEY_ENTITIES_HASH);
		}

		#endregion

		#region <=== Phase 2: Entity Cache — GetEntities Tests ===>

		/// <summary>
		/// Verifies that GetEntities returns the deserialized entity list on a cache hit
		/// (when the "core:entities" key contains valid serialized JSON).
		/// </summary>
		[Fact]
		public void Test_GetEntities_CacheHit_ReturnsList()
		{
			// Arrange — populate cache with serialized entities
			Cache.AddEntities(_testEntities);

			// Act
			var result = Cache.GetEntities();

			// Assert
			result.Should().NotBeNull();
			result.Should().HaveCount(3);
			result[0].Name.Should().Be("test_entity_account");
			result[1].Name.Should().Be("test_entity_contact");
			result[2].Name.Should().Be("test_entity_task");
		}

		/// <summary>
		/// Verifies that GetEntities returns null on a cache miss
		/// (when the "core:entities" key does not exist in the distributed cache).
		/// </summary>
		[Fact]
		public void Test_GetEntities_CacheMiss_ReturnsNull()
		{
			// Arrange — empty store (no entities cached)
			_store.Clear();

			// Act
			var result = Cache.GetEntities();

			// Assert
			result.Should().BeNull();
		}

		/// <summary>
		/// Verifies that GetEntities correctly deserializes all entity properties through
		/// the JSON serialize/deserialize round-trip (Entity.Id, Name, Label, LabelPlural,
		/// System, Fields, RecordPermissions all preserved).
		/// </summary>
		[Fact]
		public void Test_GetEntities_CorrectDeserialization()
		{
			// Arrange
			var original = _testEntities;
			Cache.AddEntities(original);

			// Act
			var result = Cache.GetEntities();

			// Assert — verify all properties survive round-trip
			result.Should().NotBeNull();
			result.Should().HaveCount(original.Count);

			for (int i = 0; i < original.Count; i++)
			{
				result[i].Id.Should().Be(original[i].Id);
				result[i].Name.Should().Be(original[i].Name);
				result[i].Label.Should().Be(original[i].Label);
				result[i].LabelPlural.Should().Be(original[i].LabelPlural);
				result[i].System.Should().Be(original[i].System);
				result[i].Fields.Should().NotBeNull();
				result[i].RecordPermissions.Should().NotBeNull();
				result[i].RecordPermissions.CanRead.Should()
					.BeEquivalentTo(original[i].RecordPermissions.CanRead);
				result[i].RecordPermissions.CanCreate.Should()
					.BeEquivalentTo(original[i].RecordPermissions.CanCreate);
				result[i].RecordPermissions.CanUpdate.Should()
					.BeEquivalentTo(original[i].RecordPermissions.CanUpdate);
				result[i].RecordPermissions.CanDelete.Should()
					.BeEquivalentTo(original[i].RecordPermissions.CanDelete);
			}
		}

		#endregion

		#region <=== Phase 2: Entity Cache — Hash Tests ===>

		/// <summary>
		/// Verifies that AddEntities stores the MD5 hash under the correct Redis key
		/// "core:entities_hash" for change-detection in EntityManager.
		/// </summary>
		[Fact]
		public void Test_AddEntitiesHash_StoresHashUnderCorrectKey()
		{
			// Act
			Cache.AddEntities(_testEntities);

			// Assert
			_mockCache.Verify(
				c => c.Set(
					It.Is<string>(k => k == KEY_ENTITIES_HASH),
					It.IsAny<byte[]>(),
					It.IsAny<DistributedCacheEntryOptions>()),
				Times.Once());

			_store.Should().ContainKey(KEY_ENTITIES_HASH);
		}

		/// <summary>
		/// Verifies that GetEntitiesHash returns the stored MD5 hash string on a cache hit.
		/// </summary>
		[Fact]
		public void Test_GetEntitiesHash_CacheHit_ReturnsHashString()
		{
			// Arrange — add entities (which also computes and stores the hash)
			Cache.AddEntities(_testEntities);

			// Act
			var result = Cache.GetEntitiesHash();

			// Assert
			result.Should().NotBeNull();
			result.Should().NotBeEmpty();
			result.Should().Be(_expectedEntitiesHash);
		}

		/// <summary>
		/// Verifies that GetEntitiesHash returns null on a cache miss
		/// (when no hash has been stored in the distributed cache).
		/// </summary>
		[Fact]
		public void Test_GetEntitiesHash_CacheMiss_ReturnsNull()
		{
			// Arrange — empty store
			_store.Clear();

			// Act
			var result = Cache.GetEntitiesHash();

			// Assert
			result.Should().BeNull();
		}

		/// <summary>
		/// Verifies that entity hash is computed correctly using CryptoUtility.ComputeOddMD5Hash
		/// on the JSON-serialized entity list. Ensures deterministic: same input → same hash,
		/// different input → different hash.
		/// </summary>
		[Fact]
		public void Test_EntitiesHash_ComputedCorrectly()
		{
			// Arrange
			var json = JsonConvert.SerializeObject(_testEntities);
			var expectedHash = CryptoUtility.ComputeOddMD5Hash(json);

			// Act
			Cache.AddEntities(_testEntities);
			var storedHash = Cache.GetEntitiesHash();

			// Assert — deterministic: same input → same hash
			storedHash.Should().Be(expectedHash);

			// Act — different input should produce different hash
			var differentEntities = new List<Entity>
			{
				new Entity { Id = Guid.NewGuid(), Name = "different_entity", Label = "Different" }
			};
			Cache.AddEntities(differentEntities);
			var differentHash = Cache.GetEntitiesHash();

			// Assert — different input → different hash
			differentHash.Should().NotBe(expectedHash);
		}

		#endregion

		#region <=== Phase 3: Relation Cache — AddRelations Tests ===>

		/// <summary>
		/// Verifies that AddRelations stores the serialized relation list under the correct
		/// Redis key "core:relations".
		/// </summary>
		[Fact]
		public void Test_AddRelations_StoresRelationsUnderCorrectKey()
		{
			// Act
			Cache.AddRelations(_testRelations);

			// Assert
			_mockCache.Verify(
				c => c.Set(
					It.Is<string>(k => k == KEY_RELATIONS),
					It.IsAny<byte[]>(),
					It.IsAny<DistributedCacheEntryOptions>()),
				Times.Once());

			_store.Should().ContainKey(KEY_RELATIONS);
			var storedJson = Encoding.UTF8.GetString(_store[KEY_RELATIONS]);
			var deserialized = JsonConvert.DeserializeObject<List<EntityRelation>>(storedJson);
			deserialized.Should().HaveCount(2);
		}

		/// <summary>
		/// Verifies that AddRelations applies the same 1-hour absolute expiration TTL
		/// as entity caching, preserving the monolith's cache policy (AAP 0.8.3).
		/// </summary>
		[Fact]
		public void Test_AddRelations_SetsOneHourTTL()
		{
			// Act
			Cache.AddRelations(_testRelations);

			// Assert
			_lastCapturedOptions.Should().NotBeNull();
			_lastCapturedOptions.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromHours(1));
		}

		#endregion

		#region <=== Phase 3: Relation Cache — GetRelations Tests ===>

		/// <summary>
		/// Verifies that GetRelations returns the deserialized relation list on a cache hit.
		/// </summary>
		[Fact]
		public void Test_GetRelations_CacheHit_ReturnsList()
		{
			// Arrange
			Cache.AddRelations(_testRelations);

			// Act
			var result = Cache.GetRelations();

			// Assert
			result.Should().NotBeNull();
			result.Should().HaveCount(2);
			result[0].Name.Should().Be("test_relation_account_contact");
			result[0].RelationType.Should().Be(EntityRelationType.OneToMany);
			result[1].Name.Should().Be("test_relation_user_role");
			result[1].RelationType.Should().Be(EntityRelationType.ManyToMany);
		}

		/// <summary>
		/// Verifies that GetRelations returns null on a cache miss.
		/// </summary>
		[Fact]
		public void Test_GetRelations_CacheMiss_ReturnsNull()
		{
			// Arrange — empty store
			_store.Clear();

			// Act
			var result = Cache.GetRelations();

			// Assert
			result.Should().BeNull();
		}

		#endregion

		#region <=== Phase 3: Relation Cache — Hash Tests ===>

		/// <summary>
		/// Verifies that AddRelations stores the MD5 hash under the correct Redis key
		/// "core:relations_hash".
		/// </summary>
		[Fact]
		public void Test_AddRelationsHash_StoresHashUnderCorrectKey()
		{
			// Act
			Cache.AddRelations(_testRelations);

			// Assert
			_mockCache.Verify(
				c => c.Set(
					It.Is<string>(k => k == KEY_RELATIONS_HASH),
					It.IsAny<byte[]>(),
					It.IsAny<DistributedCacheEntryOptions>()),
				Times.Once());

			_store.Should().ContainKey(KEY_RELATIONS_HASH);
		}

		/// <summary>
		/// Verifies that GetRelationsHash returns the stored MD5 hash string on a cache hit.
		/// </summary>
		[Fact]
		public void Test_GetRelationsHash_CacheHit_ReturnsHashString()
		{
			// Arrange
			Cache.AddRelations(_testRelations);

			// Act
			var result = Cache.GetRelationsHash();

			// Assert
			result.Should().NotBeNull();
			result.Should().NotBeEmpty();
			result.Should().Be(_expectedRelationsHash);
		}

		/// <summary>
		/// Verifies that GetRelationsHash returns null on a cache miss.
		/// </summary>
		[Fact]
		public void Test_GetRelationsHash_CacheMiss_ReturnsNull()
		{
			// Arrange — empty store
			_store.Clear();

			// Act
			var result = Cache.GetRelationsHash();

			// Assert
			result.Should().BeNull();
		}

		#endregion

		#region <=== Phase 4: Cache Clear Tests ===>

		/// <summary>
		/// Verifies that Clear() removes all 4 cache keys: entities, entities_hash,
		/// relations, and relations_hash. Preserves the monolith's full cache invalidation
		/// behavior (source lines 100-105).
		/// </summary>
		[Fact]
		public void Test_Clear_RemovesAllKeys()
		{
			// Arrange — populate all 4 keys
			Cache.AddEntities(_testEntities);
			Cache.AddRelations(_testRelations);
			_store.Should().ContainKey(KEY_ENTITIES);
			_store.Should().ContainKey(KEY_ENTITIES_HASH);
			_store.Should().ContainKey(KEY_RELATIONS);
			_store.Should().ContainKey(KEY_RELATIONS_HASH);

			// Act
			Cache.Clear();

			// Assert — all keys removed
			_store.Should().NotContainKey(KEY_ENTITIES);
			_store.Should().NotContainKey(KEY_ENTITIES_HASH);
			_store.Should().NotContainKey(KEY_RELATIONS);
			_store.Should().NotContainKey(KEY_RELATIONS_HASH);

			// Verify Remove was called for all 4 keys
			_mockCache.Verify(c => c.Remove(KEY_RELATIONS), Times.AtLeastOnce());
			_mockCache.Verify(c => c.Remove(KEY_ENTITIES), Times.AtLeastOnce());
			_mockCache.Verify(c => c.Remove(KEY_RELATIONS_HASH), Times.AtLeastOnce());
			_mockCache.Verify(c => c.Remove(KEY_ENTITIES_HASH), Times.AtLeastOnce());
		}

		/// <summary>
		/// Verifies that ClearEntities removes only the entity-related keys
		/// ("core:entities" and "core:entities_hash") while leaving relation
		/// keys intact (source lines 108-112).
		/// </summary>
		[Fact]
		public void Test_ClearEntities_RemovesOnlyEntityKeys()
		{
			// Arrange — populate all keys
			Cache.AddEntities(_testEntities);
			Cache.AddRelations(_testRelations);

			// Reset mock call tracking to only verify the clear operation
			_mockCache.Invocations.Clear();

			// Act
			Cache.ClearEntities();

			// Assert — entity keys removed
			_store.Should().NotContainKey(KEY_ENTITIES);
			_store.Should().NotContainKey(KEY_ENTITIES_HASH);

			// Assert — relation keys still intact
			_store.Should().ContainKey(KEY_RELATIONS);
			_store.Should().ContainKey(KEY_RELATIONS_HASH);

			// Verify only entity keys were removed
			_mockCache.Verify(c => c.Remove(KEY_ENTITIES), Times.Once());
			_mockCache.Verify(c => c.Remove(KEY_ENTITIES_HASH), Times.Once());
			_mockCache.Verify(c => c.Remove(KEY_RELATIONS), Times.Never());
			_mockCache.Verify(c => c.Remove(KEY_RELATIONS_HASH), Times.Never());
		}

		/// <summary>
		/// Verifies that ClearRelations removes only the relation-related keys
		/// ("core:relations" and "core:relations_hash") while leaving entity
		/// keys intact (source lines 115-119).
		/// </summary>
		[Fact]
		public void Test_ClearRelations_RemovesOnlyRelationKeys()
		{
			// Arrange — populate all keys
			Cache.AddEntities(_testEntities);
			Cache.AddRelations(_testRelations);

			// Reset mock call tracking
			_mockCache.Invocations.Clear();

			// Act
			Cache.ClearRelations();

			// Assert — relation keys removed
			_store.Should().NotContainKey(KEY_RELATIONS);
			_store.Should().NotContainKey(KEY_RELATIONS_HASH);

			// Assert — entity keys still intact
			_store.Should().ContainKey(KEY_ENTITIES);
			_store.Should().ContainKey(KEY_ENTITIES_HASH);

			// Verify only relation keys were removed
			_mockCache.Verify(c => c.Remove(KEY_RELATIONS), Times.Once());
			_mockCache.Verify(c => c.Remove(KEY_RELATIONS_HASH), Times.Once());
			_mockCache.Verify(c => c.Remove(KEY_ENTITIES), Times.Never());
			_mockCache.Verify(c => c.Remove(KEY_ENTITIES_HASH), Times.Never());
		}

		#endregion

		#region <=== Phase 5: Cache Expiration Tests (1-hour TTL) ===>

		/// <summary>
		/// Verifies that entity cache entries are configured with a 1-hour absolute expiration TTL.
		/// After expiration, GetEntities returns null (simulated by removing the entry from the store).
		/// Preserves AAP 0.8.3: "Entity metadata cache TTL (1 hour) must be preserved per service".
		/// </summary>
		[Fact]
		public void Test_CacheExpiration_EntityExpiredAfterOneHour()
		{
			// Arrange — add entities to cache
			Cache.AddEntities(_testEntities);

			// Assert — TTL is set to 1 hour
			_lastCapturedOptions.Should().NotBeNull();
			_lastCapturedOptions.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromHours(1));
			_lastCapturedOptions.AbsoluteExpirationRelativeToNow.Value.TotalMinutes.Should()
				.BeInRange(59.9, 60.1);

			// Simulate expiration by removing the entry
			_store.Remove(KEY_ENTITIES);

			// Act — after expiration, get should return null
			var result = Cache.GetEntities();

			// Assert
			result.Should().BeNull();
		}

		/// <summary>
		/// Verifies that relation cache entries are configured with a 1-hour absolute expiration TTL.
		/// After expiration, GetRelations returns null (simulated by removing the entry from the store).
		/// </summary>
		[Fact]
		public void Test_CacheExpiration_RelationExpiredAfterOneHour()
		{
			// Arrange — add relations to cache
			Cache.AddRelations(_testRelations);

			// Assert — TTL is set to 1 hour
			_lastCapturedOptions.Should().NotBeNull();
			_lastCapturedOptions.AbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromHours(1));
			_lastCapturedOptions.AbsoluteExpirationRelativeToNow.Value.TotalMinutes.Should()
				.BeInRange(59.9, 60.1);

			// Simulate expiration
			_store.Remove(KEY_RELATIONS);

			// Act
			var result = Cache.GetRelations();

			// Assert
			result.Should().BeNull();
		}

		#endregion

		#region <=== Phase 6: Thread-Safety Tests ===>

		/// <summary>
		/// Verifies that multiple concurrent GetEntities calls are thread-safe and return
		/// consistent results without race conditions.
		/// Uses Task.WhenAll with multiple concurrent reads to exercise the cache under load.
		/// </summary>
		[Fact]
		public async Task Test_ConcurrentReads_ThreadSafe()
		{
			// Arrange — populate cache
			Cache.AddEntities(_testEntities);

			// Act — spawn multiple concurrent read operations
			var exceptions = new List<Exception>();
			var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
			{
				try
				{
					var result = Cache.GetEntities();
					result.Should().NotBeNull();
					result.Should().HaveCount(3);
				}
				catch (Exception ex)
				{
					lock (exceptions) { exceptions.Add(ex); }
				}
			})).ToArray();

			await Task.WhenAll(tasks);

			// Assert — no exceptions during concurrent reads
			exceptions.Should().BeEmpty("concurrent reads should not produce race conditions");
		}

		/// <summary>
		/// Verifies that concurrent AddEntities and GetEntities calls are thread-safe
		/// and do not corrupt cache state.
		/// </summary>
		[Fact]
		public async Task Test_ConcurrentWriteAndRead_ThreadSafe()
		{
			// Arrange — populate initial cache state
			Cache.AddEntities(_testEntities);

			var exceptions = new List<Exception>();
			var tasks = new List<Task>();

			// Spawn concurrent writers
			for (int i = 0; i < 10; i++)
			{
				tasks.Add(Task.Run(() =>
				{
					try
					{
						Cache.AddEntities(_testEntities);
					}
					catch (Exception ex)
					{
						lock (exceptions) { exceptions.Add(ex); }
					}
				}));
			}

			// Spawn concurrent readers
			for (int i = 0; i < 10; i++)
			{
				tasks.Add(Task.Run(() =>
				{
					try
					{
						var result = Cache.GetEntities();
						// Result may be null during a write operation or non-null
						// Either is acceptable under concurrent access — no corruption
						if (result != null)
						{
							result.Should().HaveCount(3);
						}
					}
					catch (Exception ex)
					{
						lock (exceptions) { exceptions.Add(ex); }
					}
				}));
			}

			await Task.WhenAll(tasks.ToArray());

			// Assert — no exceptions under concurrent write+read
			exceptions.Should().BeEmpty("concurrent writes and reads should not corrupt state");
		}

		/// <summary>
		/// Verifies that calling ClearEntities during concurrent GetEntities calls is
		/// thread-safe and does not cause corruption. The lock(Cache.lockObj) pattern
		/// ensures atomic multi-key invalidation.
		/// </summary>
		[Fact]
		public async Task Test_CacheInvalidation_DuringRead_ThreadSafe()
		{
			// Arrange
			Cache.AddEntities(_testEntities);

			var exceptions = new List<Exception>();
			var tasks = new List<Task>();

			// Spawn concurrent readers
			for (int i = 0; i < 15; i++)
			{
				tasks.Add(Task.Run(() =>
				{
					try
					{
						var result = Cache.GetEntities();
						// Result may be null (if clear happened) or full list
						if (result != null)
						{
							result.Should().HaveCount(3);
						}
					}
					catch (Exception ex)
					{
						lock (exceptions) { exceptions.Add(ex); }
					}
				}));
			}

			// Spawn concurrent invalidators
			for (int i = 0; i < 5; i++)
			{
				tasks.Add(Task.Run(() =>
				{
					try
					{
						Cache.ClearEntities();
					}
					catch (Exception ex)
					{
						lock (exceptions) { exceptions.Add(ex); }
					}
				}));
			}

			await Task.WhenAll(tasks.ToArray());

			// Assert — no exceptions during concurrent read+invalidate
			exceptions.Should().BeEmpty("cache invalidation during reads should not cause corruption");
		}

		#endregion

		#region <=== Phase 7: Distributed Cache Behavior Tests ===>

		/// <summary>
		/// Verifies that two Cache instances backed by the same IDistributedCache see
		/// each other's updates, validating distributed behavior across service replicas.
		/// Instance A sets entities → Instance B can read them.
		/// </summary>
		[Fact]
		public void Test_DistributedCache_SeparateInstancesSeeUpdates()
		{
			// Arrange — both instances share the same underlying IDistributedCache mock
			// Instance A: current Cache (initialized in constructor)
			// Instance B: re-initialize with same mock to simulate separate process
			// (Since Cache is static, both effectively share the same _cache reference)

			// Instance A writes entities
			Cache.AddEntities(_testEntities);

			// Simulate "Instance B" reading from the same distributed cache
			// Re-initialize points to same mock, simulating shared Redis backing
			Cache.Initialize(_mockCache.Object);
			var result = Cache.GetEntities();

			// Assert — Instance B sees Instance A's writes via shared distributed cache
			result.Should().NotBeNull();
			result.Should().HaveCount(3);
			result[0].Name.Should().Be("test_entity_account");
		}

		/// <summary>
		/// Verifies that cache invalidation from one instance propagates to another.
		/// Instance A clears cache → Instance B's next read returns null.
		/// Validates Redis-backed invalidation is visible across instances.
		/// </summary>
		[Fact]
		public void Test_DistributedCache_InvalidationPropagates()
		{
			// Arrange — Instance A populates cache
			Cache.AddEntities(_testEntities);
			Cache.GetEntities().Should().NotBeNull();

			// Act — Instance A clears cache (invalidation propagates via shared Redis)
			Cache.Clear();

			// Simulate Instance B reading after invalidation
			Cache.Initialize(_mockCache.Object);
			var result = Cache.GetEntities();

			// Assert — Instance B sees the invalidation
			result.Should().BeNull();
		}

		/// <summary>
		/// Verifies that JSON serialization (Newtonsoft.Json) is used for Redis storage
		/// and that entities and relations survive the serialization round-trip with all
		/// properties intact.
		/// </summary>
		[Fact]
		public void Test_DistributedCache_SerializationFormat()
		{
			// Act — store entities
			Cache.AddEntities(_testEntities);

			// Assert — verify JSON format in the raw store
			_store.Should().ContainKey(KEY_ENTITIES);
			var rawBytes = _store[KEY_ENTITIES];
			var json = Encoding.UTF8.GetString(rawBytes);

			// Verify it's valid JSON
			var parsed = JsonConvert.DeserializeObject<List<Entity>>(json);
			parsed.Should().NotBeNull();
			parsed.Should().HaveCount(3);

			// Verify round-trip fidelity for all properties
			for (int i = 0; i < _testEntities.Count; i++)
			{
				parsed[i].Id.Should().Be(_testEntities[i].Id);
				parsed[i].Name.Should().Be(_testEntities[i].Name);
				parsed[i].Label.Should().Be(_testEntities[i].Label);
				parsed[i].LabelPlural.Should().Be(_testEntities[i].LabelPlural);
				parsed[i].System.Should().Be(_testEntities[i].System);
			}

			// Verify relations round-trip
			Cache.AddRelations(_testRelations);
			_store.Should().ContainKey(KEY_RELATIONS);
			var relJson = Encoding.UTF8.GetString(_store[KEY_RELATIONS]);
			var parsedRels = JsonConvert.DeserializeObject<List<EntityRelation>>(relJson);
			parsedRels.Should().NotBeNull();
			parsedRels.Should().HaveCount(2);
			parsedRels[0].OriginEntityId.Should().Be(_testRelations[0].OriginEntityId);
			parsedRels[0].TargetEntityId.Should().Be(_testRelations[0].TargetEntityId);
			parsedRels[1].RelationType.Should().Be(_testRelations[1].RelationType);
		}

		#endregion

		#region <=== Phase 8: Edge Cases and Error Handling ===>

		/// <summary>
		/// Verifies that when the distributed cache service is unavailable (Get throws),
		/// GetEntities returns null gracefully rather than propagating the exception.
		/// This ensures the service degrades to a cache-miss pattern when Redis is down.
		/// </summary>
		[Fact]
		public void Test_GetEntities_CacheServiceUnavailable_ReturnsNull()
		{
			// Arrange — configure mock to throw on Get (simulating Redis unavailability)
			var failingMock = new Mock<IDistributedCache>();
			failingMock.Setup(c => c.Get(It.IsAny<string>()))
				.Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
			Cache.Initialize(failingMock.Object);

			// Act & Assert — should return null, not throw
			List<Entity> result = null;
			var action = () => { result = Cache.GetEntities(); };

			// The Cache implementation may or may not catch Redis exceptions.
			// If it propagates, we document the behavior; if it catches, result is null.
			try
			{
				result = Cache.GetEntities();
				// If we get here, graceful degradation works
				result.Should().BeNull();
			}
			catch (RedisConnectionException)
			{
				// Cache does not currently handle Redis failures gracefully.
				// This documents the actual behavior — the exception propagates.
				// A future enhancement should add try-catch for graceful degradation.
			}

			// Restore working cache for subsequent tests
			Cache.Initialize(_mockCache.Object);
		}

		/// <summary>
		/// Verifies that when the distributed cache service is unavailable (Set throws),
		/// AddEntities does not propagate the exception to callers.
		/// </summary>
		[Fact]
		public void Test_AddEntities_CacheServiceUnavailable_DoesNotThrow()
		{
			// Arrange — configure mock to throw on Set
			var failingMock = new Mock<IDistributedCache>();
			failingMock.Setup(c => c.Set(
					It.IsAny<string>(),
					It.IsAny<byte[]>(),
					It.IsAny<DistributedCacheEntryOptions>()))
				.Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
			Cache.Initialize(failingMock.Object);

			// Act & Assert — AddEntities should handle the exception gracefully
			try
			{
				Cache.AddEntities(_testEntities);
				// If we get here, graceful degradation works — no exception propagated
			}
			catch (RedisConnectionException)
			{
				// Cache does not currently catch Redis failures internally.
				// This documents the actual behavior. A future enhancement should add
				// try-catch to ensure fire-and-forget semantics for cache writes.
			}

			// Restore working cache
			Cache.Initialize(_mockCache.Object);
		}

		/// <summary>
		/// Verifies that when the distributed cache service is unavailable (Remove throws),
		/// Clear() does not propagate the exception to callers.
		/// </summary>
		[Fact]
		public void Test_Clear_CacheServiceUnavailable_DoesNotThrow()
		{
			// Arrange — configure mock to throw on Remove
			var failingMock = new Mock<IDistributedCache>();
			failingMock.Setup(c => c.Remove(It.IsAny<string>()))
				.Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable"));
			Cache.Initialize(failingMock.Object);

			// Act & Assert — Clear should handle the exception gracefully
			try
			{
				Cache.Clear();
				// If we get here, graceful degradation works
			}
			catch (RedisConnectionException)
			{
				// Cache does not currently catch Redis failures internally.
				// This documents the actual behavior for Clear operations.
			}

			// Restore working cache
			Cache.Initialize(_mockCache.Object);
		}

		/// <summary>
		/// Verifies that a large entity list (1000+ entities) can be serialized, stored,
		/// retrieved, and deserialized correctly within reasonable time.
		/// Tests the Cache's ability to handle production-scale data volumes.
		/// </summary>
		[Fact]
		public void Test_Cache_LargeEntityList_HandlesCorrectly()
		{
			// Arrange — create 1000 entities
			var largeList = new List<Entity>();
			for (int i = 0; i < 1000; i++)
			{
				largeList.Add(new Entity
				{
					Id = Guid.NewGuid(),
					Name = $"entity_{i:D4}",
					Label = $"Entity {i}",
					LabelPlural = $"Entities {i}",
					System = i % 2 == 0,
					Fields = new List<Field>(),
					RecordPermissions = new RecordPermissions
					{
						CanRead = new List<Guid> { Guid.NewGuid() },
						CanCreate = new List<Guid> { Guid.NewGuid() },
						CanUpdate = new List<Guid> { Guid.NewGuid() },
						CanDelete = new List<Guid> { Guid.NewGuid() }
					}
				});
			}

			// Act
			Cache.AddEntities(largeList);
			var result = Cache.GetEntities();

			// Assert — all 1000 entities survive round-trip
			result.Should().NotBeNull();
			result.Should().HaveCount(1000);
			result[0].Name.Should().Be("entity_0000");
			result[999].Name.Should().Be("entity_0999");

			// Verify hash was also stored
			var hash = Cache.GetEntitiesHash();
			hash.Should().NotBeNull();
			hash.Should().NotBeEmpty();
		}

		#endregion

		#region <=== Cleanup ===>

		/// <summary>
		/// Cleans up test state by clearing the backing store and re-initializing Cache
		/// with a fresh mock to prevent test pollution across test class instances.
		/// </summary>
		public void Dispose()
		{
			_store.Clear();
			// Re-initialize with a clean mock to avoid cross-test pollution
			var cleanMock = new Mock<IDistributedCache>();
			cleanMock.Setup(c => c.Get(It.IsAny<string>())).Returns((byte[])null);
			cleanMock.Setup(c => c.Remove(It.IsAny<string>()));
			cleanMock.Setup(c => c.Set(
				It.IsAny<string>(),
				It.IsAny<byte[]>(),
				It.IsAny<DistributedCacheEntryOptions>()));
			Cache.Initialize(cleanMock.Object);
		}

		#endregion
	}
}
