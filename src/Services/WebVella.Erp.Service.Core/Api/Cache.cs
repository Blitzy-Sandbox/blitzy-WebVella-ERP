using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using WebVella.Erp.SharedKernel.Models;
using WebVella.Erp.SharedKernel.Utilities;

namespace WebVella.Erp.Service.Core.Api
{
	/// <summary>
	/// Redis-backed distributed cache wrapper for entity metadata and entity relations.
	///
	/// Adapted from the monolith's <c>WebVella.Erp.Api.Cache</c> (IMemoryCache-based)
	/// to use <see cref="IDistributedCache"/> (StackExchange.Redis) for cross-instance
	/// cache coherence in the microservice architecture.
	///
	/// <para><b>Key changes from monolith:</b></para>
	/// <list type="bullet">
	///   <item>IMemoryCache replaced with IDistributedCache (Redis-backed)</item>
	///   <item>CLR object storage replaced with JSON serialization via Newtonsoft.Json
	///         with TypeNameHandling.Auto for polymorphic Field/InputField support</item>
	///   <item>Cache keys prefixed with "core:" for Redis namespace isolation across services</item>
	///   <item>Static <see cref="Initialize"/> method for DI injection at startup</item>
	/// </list>
	///
	/// <para><b>Preserved from monolith:</b></para>
	/// <list type="bullet">
	///   <item>1-hour absolute expiration TTL</item>
	///   <item>MD5 hash fingerprints via <see cref="CryptoUtility.ComputeOddMD5Hash"/></item>
	///   <item>Atomic multi-key invalidation via <c>lock(EntityManager.lockObj)</c></item>
	///   <item>All public method signatures</item>
	/// </list>
	/// </summary>
	internal class Cache
	{
		#region <=== Constants and Fields ===>

		/// <summary>
		/// Redis cache key for the serialized list of <see cref="Entity"/> metadata.
		/// Prefixed with "core:" for namespace isolation in a shared Redis instance.
		/// </summary>
		private const string KEY_ENTITIES = "core:entities";

		/// <summary>
		/// Redis cache key for the MD5 hash fingerprint of the serialized entity list.
		/// Used for change-detection and ETag-like client caching by
		/// <see cref="EntityManager.ReadEntities"/>.
		/// </summary>
		private const string KEY_ENTITIES_HASH = "core:entities_hash";

		/// <summary>
		/// Redis cache key for the serialized list of <see cref="EntityRelation"/> metadata.
		/// </summary>
		private const string KEY_RELATIONS = "core:relations";

		/// <summary>
		/// Redis cache key for the MD5 hash fingerprint of the serialized relation list.
		/// Used for change-detection by <see cref="EntityRelationManager"/>.
		/// </summary>
		private const string KEY_RELATIONS_HASH = "core:relations_hash";

		/// <summary>
		/// Shared lock object for thread-safe atomic invalidation of multi-key cache entries.
		/// Used by Cache.Clear/ClearEntities/ClearRelations and by EntityManager.ReadEntities
		/// double-checked locking pattern to prevent race conditions between cache population
		/// and cache invalidation.
		///
		/// <para>In the monolith this was defined as <c>EntityManager.lockObj</c>. In the
		/// microservice architecture it is owned by Cache since it primarily guards cache
		/// state transitions. EntityManager references <c>Cache.lockObj</c> for its
		/// cache-population critical sections.</para>
		/// </summary>
		internal static readonly object lockObj = new object();

		/// <summary>
		/// The underlying distributed cache instance (Redis via StackExchange.Redis).
		/// Initialized at startup via <see cref="Initialize"/> before any cache operations.
		/// </summary>
		private static IDistributedCache _cache;

		/// <summary>
		/// Shared cache entry options preserving the monolith's 1-hour absolute expiration TTL.
		/// Applied to all SetString calls to ensure entries expire after 1 hour regardless of access.
		/// </summary>
		/// <summary>
		/// JSON serialization settings using TypeNameHandling.Auto for polymorphic
		/// collections such as List&lt;Field&gt; (abstract) containing concrete field
		/// subclasses (GuidField, TextField, etc.). Required because the Entity model
		/// stores fields as <c>List&lt;Field&gt;</c> and the cache must round-trip
		/// through JSON while preserving concrete type identity.
		/// Mirrors DbEntityRepository's TypeNameHandling.Auto usage.
		/// </summary>
		private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
		{
			TypeNameHandling = TypeNameHandling.Auto,
			NullValueHandling = NullValueHandling.Ignore
		};

		private static readonly DistributedCacheEntryOptions _cacheOptions = new DistributedCacheEntryOptions
		{
			AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1)
		};

		#endregion

		#region <=== Initialization ===>

		/// <summary>
		/// Initializes the cache with a distributed cache implementation.
		/// Must be called once at service startup (typically in Program.cs / DI configuration)
		/// before any entity or relation caching operations are performed.
		///
		/// <para>This replaces the monolith's static constructor that created a new
		/// <c>MemoryCache</c> instance. The static-method API is preserved so that
		/// callers (EntityManager, EntityRelationManager, RecordManager, ImportExportManager)
		/// continue to call <c>Cache.GetEntities()</c>, <c>Cache.Clear()</c>, etc. without
		/// requiring instance injection at every call site.</para>
		/// </summary>
		/// <param name="cache">
		/// The <see cref="IDistributedCache"/> instance from DI (typically Redis via
		/// <c>AddStackExchangeRedisCache</c>). Must not be null.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown if <paramref name="cache"/> is null.
		/// </exception>
		public static void Initialize(IDistributedCache cache)
		{
			_cache = cache ?? throw new ArgumentNullException(nameof(cache));
		}

		#endregion

		#region <=== General Methods ===>

		/// <summary>
		/// Serializes an object to JSON and stores it in the distributed cache with 1-hour TTL.
		/// Replaces the monolith's <c>cache.Set(key, obj, options)</c> with
		/// <c>IDistributedCache.SetString(key, json, options)</c>.
		///
		/// <para>If <paramref name="obj"/> is null, the key is removed from the cache
		/// rather than storing a null JSON string, preserving the monolith's behavior
		/// where <c>GetObjectFromCache</c> returns null for missing keys.</para>
		/// </summary>
		/// <param name="key">The cache key (already prefixed with "core:").</param>
		/// <param name="obj">The object to serialize and cache, or null to remove the key.</param>
		private static void AddObjectToCache(string key, object obj)
		{
			if (obj == null)
			{
				_cache.Remove(key);
				return;
			}
			var json = JsonConvert.SerializeObject(obj);
			_cache.SetString(key, json, _cacheOptions);
		}

		/// <summary>
		/// Retrieves a JSON string from the distributed cache for the given key.
		/// Replaces the monolith's <c>cache.TryGetValue(key, out result)</c> pattern.
		///
		/// <para>Returns null if the key does not exist or has expired, matching the
		/// monolith's behavior where <c>TryGetValue</c> returned null for missing entries.</para>
		/// </summary>
		/// <param name="key">The cache key (already prefixed with "core:").</param>
		/// <returns>The cached JSON string, or null if not found.</returns>
		internal static string GetStringFromCache(string key)
		{
			return _cache.GetString(key);
		}

		/// <summary>
		/// Removes a cache entry by key from the distributed cache.
		/// Replaces the monolith's <c>cache.Remove(key)</c>.
		/// </summary>
		/// <param name="key">The cache key to remove.</param>
		internal static void RemoveObjectFromCache(string key)
		{
			_cache.Remove(key);
		}

		#endregion

		#region <=== Entities ===>

		/// <summary>
		/// Serializes and caches a list of <see cref="Entity"/> metadata objects.
		/// Also computes and stores an MD5 hash fingerprint of the serialized JSON
		/// for change-detection in <see cref="EntityManager.ReadEntities"/>.
		///
		/// <para>Preserves the monolith's pattern: entities are stored at one key,
		/// their MD5 hash at another. If <paramref name="entities"/> is null, the
		/// entity data is still cached (as JSON "null") but the hash key is removed.</para>
		/// </summary>
		/// <param name="entities">
		/// The list of entity definitions to cache, or null to clear the entities hash.
		/// </param>
		public static void AddEntities(List<Entity> entities)
		{
			var json = JsonConvert.SerializeObject(entities, _jsonSettings);
			_cache.SetString(KEY_ENTITIES, json, _cacheOptions);
			if (entities != null)
			{
				var hash = CryptoUtility.ComputeOddMD5Hash(json);
				_cache.SetString(KEY_ENTITIES_HASH, hash, _cacheOptions);
			}
			else
			{
				_cache.Remove(KEY_ENTITIES_HASH);
			}
		}

		/// <summary>
		/// Retrieves the cached list of <see cref="Entity"/> metadata objects.
		/// Deserializes from JSON stored in the distributed cache.
		///
		/// <para>Returns null if the cache key does not exist or has expired,
		/// triggering a database reload in <see cref="EntityManager.ReadEntities"/>.</para>
		/// </summary>
		/// <returns>
		/// The cached list of entities, or null if the cache is empty/expired.
		/// </returns>
		public static List<Entity> GetEntities()
		{
			var json = _cache.GetString(KEY_ENTITIES);
			if (string.IsNullOrEmpty(json))
				return null;
			return JsonConvert.DeserializeObject<List<Entity>>(json, _jsonSettings);
		}

		/// <summary>
		/// Retrieves the cached MD5 hash fingerprint of the entity list.
		/// Used by <see cref="EntityManager.ReadEntities"/> for change-detection
		/// and ETag-like client caching of entity metadata.
		/// </summary>
		/// <returns>
		/// The cached MD5 hash string, or null if not cached.
		/// </returns>
		public static string GetEntitiesHash()
		{
			return _cache.GetString(KEY_ENTITIES_HASH);
		}

		#endregion

		#region <=== Relations ===>

		/// <summary>
		/// Serializes and caches a list of <see cref="EntityRelation"/> metadata objects.
		/// Also computes and stores an MD5 hash fingerprint for change-detection.
		///
		/// <para>Follows the same pattern as <see cref="AddEntities"/> for relations.</para>
		/// </summary>
		/// <param name="relations">
		/// The list of entity relation definitions to cache, or null to clear the hash.
		/// </param>
		public static void AddRelations(List<EntityRelation> relations)
		{
			var json = JsonConvert.SerializeObject(relations);
			_cache.SetString(KEY_RELATIONS, json, _cacheOptions);
			if (relations != null)
			{
				var hash = CryptoUtility.ComputeOddMD5Hash(json);
				_cache.SetString(KEY_RELATIONS_HASH, hash, _cacheOptions);
			}
			else
			{
				_cache.Remove(KEY_RELATIONS_HASH);
			}
		}

		/// <summary>
		/// Retrieves the cached list of <see cref="EntityRelation"/> metadata objects.
		/// Deserializes from JSON stored in the distributed cache.
		/// </summary>
		/// <returns>
		/// The cached list of entity relations, or null if the cache is empty/expired.
		/// </returns>
		public static List<EntityRelation> GetRelations()
		{
			var json = _cache.GetString(KEY_RELATIONS);
			if (string.IsNullOrEmpty(json))
				return null;
			return JsonConvert.DeserializeObject<List<EntityRelation>>(json);
		}

		/// <summary>
		/// Retrieves the cached MD5 hash fingerprint of the relation list.
		/// Used by <see cref="EntityRelationManager"/> for change-detection.
		/// </summary>
		/// <returns>
		/// The cached MD5 hash string, or null if not cached.
		/// </returns>
		public static string GetRelationsHash()
		{
			return _cache.GetString(KEY_RELATIONS_HASH);
		}

		#endregion

		#region <=== Clear Methods ===>

		/// <summary>
		/// Atomically removes all cached entity and relation metadata from the distributed cache.
		///
		/// <para>Uses <c>lock(lockObj)</c> to ensure process-level atomicity of multi-key
		/// invalidation, preventing race conditions where one thread reads partially-invalidated
		/// state. In the distributed (multi-instance) model, Redis provides cross-process
		/// visibility while the lock provides intra-process safety.</para>
		///
		/// <para>Preserves the monolith's <c>lock(EntityManager.lockObj)</c> pattern; the
		/// lock object is now owned by Cache as <see cref="lockObj"/>. Invalidation order
		/// is preserved: relations first, then entities, then their respective hashes.</para>
		/// </summary>
		public static void Clear()
		{
			lock (lockObj)
			{
				_cache.Remove(KEY_RELATIONS);
				_cache.Remove(KEY_ENTITIES);
				_cache.Remove(KEY_RELATIONS_HASH);
				_cache.Remove(KEY_ENTITIES_HASH);
			}
		}

		/// <summary>
		/// Atomically removes all cached entity metadata (entities and their hash)
		/// from the distributed cache.
		///
		/// <para>Uses <c>lock(lockObj)</c> for process-level atomicity, same as
		/// <see cref="Clear"/>. Called by entity CRUD operations in
		/// <see cref="EntityManager"/> after entity/field modifications.</para>
		/// </summary>
		public static void ClearEntities()
		{
			lock (lockObj)
			{
				_cache.Remove(KEY_ENTITIES);
				_cache.Remove(KEY_ENTITIES_HASH);
			}
		}

		/// <summary>
		/// Atomically removes all cached relation metadata (relations and their hash)
		/// from the distributed cache.
		///
		/// <para>Uses <c>lock(lockObj)</c> for process-level atomicity. Called by
		/// relation CRUD operations in <see cref="EntityRelationManager"/>
		/// after relation modifications.</para>
		/// </summary>
		public static void ClearRelations()
		{
			lock (lockObj)
			{
				_cache.Remove(KEY_RELATIONS);
				_cache.Remove(KEY_RELATIONS_HASH);
			}
		}

		#endregion
	}
}
