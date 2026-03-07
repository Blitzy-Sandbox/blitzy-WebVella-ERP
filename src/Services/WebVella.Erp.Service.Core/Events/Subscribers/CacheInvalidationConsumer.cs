using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.Extensions.Logging;
using WebVella.Erp.SharedKernel.Contracts.Events;
using WebVella.Erp.Service.Core.Api;

namespace WebVella.Erp.Service.Core.Events.Subscribers
{
	/// <summary>
	/// MassTransit consumer that invalidates the Core service's Redis-backed distributed cache
	/// whenever external services create, update, or delete entity records.
	///
	/// <para><b>Monolith replacement:</b> This consumer replaces the monolith's
	/// <c>NotificationContext.HandleNotification</c> method (lines 139-147 of
	/// <c>WebVella.Erp/Notifications/NotificationContext.cs</c>), which used PostgreSQL
	/// LISTEN/NOTIFY on the <c>ERP_NOTIFICATIONS_CHANNNEL</c> channel to receive
	/// <c>ErpRecordChangeNotification</c> payloads (carrying EntityId, EntityName, RecordId),
	/// deserialized them via <c>JsonConvert.DeserializeObject&lt;Notification&gt;(json, settings)</c>
	/// with <c>TypeNameHandling.Auto</c>, filtered by channel name, and invoked handlers
	/// via <c>Task.Run(() =&gt; { listener.Method.Invoke(listener.Instance, ...) })</c>.</para>
	///
	/// <para>In the microservice architecture, MassTransit automatically discovers and registers
	/// this consumer via assembly scanning in <c>Program.cs</c>. Message deserialization,
	/// endpoint management, and consumer lifecycle are handled by the MassTransit pipeline,
	/// replacing the monolith's reflection-based dispatch entirely.</para>
	///
	/// <para><b>Idempotency:</b> Cache invalidation is inherently idempotent — calling
	/// <see cref="Cache.Clear()"/> multiple times for the same event has no side effects
	/// beyond redundant cache misses. Duplicate event delivery (at-least-once semantics)
	/// will not cause data corruption, satisfying AAP rule 0.8.2 that all consumers must
	/// be idempotent.</para>
	///
	/// <para><b>Error handling:</b> Cache invalidation is non-critical — if it fails, the
	/// cache will self-heal via its 1-hour TTL expiration. Therefore, exceptions in
	/// <c>Cache.Clear()</c> are caught and logged but never rethrown, preventing message
	/// retries for transient Redis failures that would waste broker resources.</para>
	///
	/// <para><b>Source-to-target mapping:</b></para>
	/// <list type="table">
	///   <listheader>
	///     <term>Monolith Pattern</term>
	///     <description>Consumer Implementation</description>
	///   </listheader>
	///   <item>
	///     <term><c>NotificationContext.HandleNotification</c> — filters listeners by channel, invokes via reflection</term>
	///     <description><c>Consume()</c> methods — strongly-typed, direct <c>Cache.Clear()</c> call</description>
	///   </item>
	///   <item>
	///     <term><c>ErpRecordChangeNotification</c> DTO (EntityId, EntityName, RecordId)</term>
	///     <description><c>RecordCreatedEvent</c>, <c>RecordUpdatedEvent</c>, <c>RecordDeletedEvent</c> contracts from SharedKernel</description>
	///   </item>
	///   <item>
	///     <term>PostgreSQL <c>LISTEN ERP_NOTIFICATIONS_CHANNNEL</c></term>
	///     <description>MassTransit consumer endpoint auto-registered via assembly scanning</description>
	///   </item>
	///   <item>
	///     <term><c>Task.Run(() =&gt; { listener.Method.Invoke(...) })</c></term>
	///     <description>Direct async <c>Consume()</c> execution by MassTransit pipeline</description>
	///   </item>
	///   <item>
	///     <term><c>IMemoryCache</c> invalidation via <c>Cache.Clear()</c></term>
	///     <description><c>IDistributedCache</c> (Redis) invalidation via adapted <c>Cache.Clear()</c></description>
	///   </item>
	/// </list>
	/// </summary>
	public class CacheInvalidationConsumer :
		IConsumer<RecordCreatedEvent>,
		IConsumer<RecordUpdatedEvent>,
		IConsumer<RecordDeletedEvent>
	{
		/// <summary>
		/// Logger for structured logging of event receipt and cache invalidation operations.
		/// Uses EntityName and CorrelationId as structured parameters for distributed tracing.
		/// </summary>
		private readonly ILogger<CacheInvalidationConsumer> _logger;

		/// <summary>
		/// Initializes a new instance of the <see cref="CacheInvalidationConsumer"/> class.
		/// Dependencies are injected by the MassTransit/ASP.NET Core DI container.
		/// </summary>
		/// <param name="logger">
		/// The logger instance for structured logging. Must not be null.
		/// </param>
		/// <exception cref="ArgumentNullException">
		/// Thrown when <paramref name="logger"/> is null.
		/// </exception>
		public CacheInvalidationConsumer(ILogger<CacheInvalidationConsumer> logger)
		{
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		/// <summary>
		/// Consumes a <see cref="RecordCreatedEvent"/> and invalidates the Core service's
		/// distributed cache to ensure stale entity/relation metadata is evicted.
		///
		/// <para><b>Source mapping:</b> Replaces the monolith's
		/// <c>NotificationContext.HandleNotification</c> (lines 139-147) which dispatched
		/// <c>ErpRecordChangeNotification</c> with EntityId/EntityName/RecordId to
		/// registered listeners via reflection.</para>
		///
		/// <para>Uses a conservative invalidation strategy matching the monolith's behavior:
		/// <c>Cache.Clear()</c> invalidates all cached metadata (entities, relations, and
		/// their hashes) because the <c>ErpRecordChangeNotification</c> did not distinguish
		/// entity types for cache invalidation purposes.</para>
		/// </summary>
		/// <param name="context">
		/// The MassTransit consume context wrapping the <see cref="RecordCreatedEvent"/>
		/// message. Contains the deserialized event payload and message metadata.
		/// </param>
		/// <returns>A completed task after cache invalidation processing.</returns>
		public async Task Consume(ConsumeContext<RecordCreatedEvent> context)
		{
			var evt = context.Message;
			_logger.LogInformation(
				"CacheInvalidation: RecordCreatedEvent received for entity '{EntityName}', CorrelationId: {CorrelationId}",
				evt.EntityName, evt.CorrelationId);

			try
			{
				// Invalidate cached entity/relation metadata when external services
				// create records that may affect Core's cached state.
				// This replaces NotificationContext.HandleNotification (lines 139-147)
				// which dispatched ErpRecordChangeNotification to registered listeners.
				Cache.Clear();

				_logger.LogInformation(
					"CacheInvalidation: Cache cleared successfully for RecordCreatedEvent, entity '{EntityName}', CorrelationId: {CorrelationId}",
					evt.EntityName, evt.CorrelationId);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex,
					"CacheInvalidation: Error invalidating cache for RecordCreatedEvent, entity '{EntityName}', CorrelationId: {CorrelationId}",
					evt.EntityName, evt.CorrelationId);
				// Do NOT rethrow — idempotent consumer should not cause message retry
				// for cache invalidation failures. The cache will self-heal via its
				// 1-hour TTL expiration configured in Cache._cacheOptions.
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Consumes a <see cref="RecordUpdatedEvent"/> and invalidates the Core service's
		/// distributed cache to ensure stale entity/relation metadata is evicted.
		///
		/// <para><b>Source mapping:</b> Replaces the monolith's post-update notification
		/// pipeline where <c>ErpRecordChangeNotification</c> was dispatched through
		/// PostgreSQL LISTEN/NOTIFY to registered <c>NotificationHandler</c>-attributed
		/// methods, which could then call <c>Cache.Clear()</c> or
		/// <c>Cache.ClearEntities()</c>.</para>
		///
		/// <para>Uses the same conservative <c>Cache.Clear()</c> strategy as the create
		/// consumer — the monolith did not differentiate update vs create for cache
		/// invalidation purposes.</para>
		/// </summary>
		/// <param name="context">
		/// The MassTransit consume context wrapping the <see cref="RecordUpdatedEvent"/>
		/// message. Contains the deserialized event payload and message metadata.
		/// </param>
		/// <returns>A completed task after cache invalidation processing.</returns>
		public async Task Consume(ConsumeContext<RecordUpdatedEvent> context)
		{
			var evt = context.Message;
			_logger.LogInformation(
				"CacheInvalidation: RecordUpdatedEvent received for entity '{EntityName}', CorrelationId: {CorrelationId}",
				evt.EntityName, evt.CorrelationId);

			try
			{
				// Invalidate cached entity/relation metadata when external services
				// update records that may affect Core's cached state.
				// Same conservative approach as RecordCreatedEvent — clear all metadata.
				Cache.Clear();

				_logger.LogInformation(
					"CacheInvalidation: Cache cleared successfully for RecordUpdatedEvent, entity '{EntityName}', CorrelationId: {CorrelationId}",
					evt.EntityName, evt.CorrelationId);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex,
					"CacheInvalidation: Error invalidating cache for RecordUpdatedEvent, entity '{EntityName}', CorrelationId: {CorrelationId}",
					evt.EntityName, evt.CorrelationId);
				// Do NOT rethrow — cache will self-heal via 1-hour TTL expiration.
			}

			await Task.CompletedTask;
		}

		/// <summary>
		/// Consumes a <see cref="RecordDeletedEvent"/> and invalidates the Core service's
		/// distributed cache to ensure stale entity/relation metadata is evicted.
		///
		/// <para><b>Source mapping:</b> Replaces the monolith's post-delete notification
		/// pipeline. The <see cref="RecordDeletedEvent"/> carries <see cref="RecordDeletedEvent.RecordId"/>
		/// (a <see cref="Guid"/>) rather than the full <c>EntityRecord</c>, since the
		/// record no longer exists after deletion. The RecordId is logged for traceability
		/// but is not used for targeted cache invalidation — the conservative
		/// <c>Cache.Clear()</c> strategy is applied.</para>
		///
		/// <para>This consumer is idempotent: calling <c>Cache.Clear()</c> for an already-
		/// processed delete event simply results in a redundant cache miss on the next
		/// metadata read, which is harmless.</para>
		/// </summary>
		/// <param name="context">
		/// The MassTransit consume context wrapping the <see cref="RecordDeletedEvent"/>
		/// message. Contains the deserialized event payload and message metadata.
		/// </param>
		/// <returns>A completed task after cache invalidation processing.</returns>
		public async Task Consume(ConsumeContext<RecordDeletedEvent> context)
		{
			var evt = context.Message;
			_logger.LogInformation(
				"CacheInvalidation: RecordDeletedEvent received for entity '{EntityName}', RecordId: {RecordId}, CorrelationId: {CorrelationId}",
				evt.EntityName, evt.RecordId, evt.CorrelationId);

			try
			{
				// Invalidate cached entity/relation metadata when external services
				// delete records that may affect Core's cached state.
				// Same conservative approach — clear all metadata regardless of entity type.
				Cache.Clear();

				_logger.LogInformation(
					"CacheInvalidation: Cache cleared successfully for RecordDeletedEvent, entity '{EntityName}', RecordId: {RecordId}, CorrelationId: {CorrelationId}",
					evt.EntityName, evt.RecordId, evt.CorrelationId);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex,
					"CacheInvalidation: Error invalidating cache for RecordDeletedEvent, entity '{EntityName}', RecordId: {RecordId}, CorrelationId: {CorrelationId}",
					evt.EntityName, evt.RecordId, evt.CorrelationId);
				// Do NOT rethrow — cache will self-heal via 1-hour TTL expiration.
			}

			await Task.CompletedTask;
		}
	}
}
