/**
 * @module event-bus
 * @description SNS/SQS Event Bus L3 CDK Construct for WebVella ERP Microservices
 *
 * This construct provisions the SNS/SQS infrastructure that replaces two monolith
 * subsystems:
 *
 * 1. **Hook System** (`WebVella.Erp/Hooks/` — HookManager.cs, RecordHookManager.cs,
 *    12 hook interfaces): The monolith used reflection-based discovery via
 *    `AppDomain.CurrentDomain.GetAssemblies()` to find classes decorated with
 *    `[HookAttachment]` implementing hook interfaces with `[Hook]`. Instances were
 *    stored in `Dictionary<Type, List<HookInfo>>` and invoked sequentially by
 *    `RecordHookManager`. Post-CRUD hooks (IErpPostCreate/Update/DeleteRecordHook)
 *    become SNS domain events with pattern `{domain}.{entity}.{action}`. Pre-CRUD
 *    hooks remain synchronous API-level validation in Lambda handlers.
 *
 * 2. **PostgreSQL LISTEN/NOTIFY** (`WebVella.Erp/Notifications/` —
 *    NotificationContext.cs, Notification.cs, ErpRecordChangeNotification.cs):
 *    The monolith's singleton `NotificationContext` used Npgsql to listen on the
 *    `ERP_NOTIFICATIONS_CHANNNEL` PostgreSQL channel, routing messages to
 *    `[NotificationHandler]`-decorated methods. `Notification.Channel` maps to
 *    SNS message attributes for filtering; `Notification.Message` maps to the SNS
 *    message body. `ErpRecordChangeNotification` (EntityId, EntityName, RecordId)
 *    maps to the SNS event payload structure for domain events.
 *
 * Event naming convention (AAP §0.8.5): `{domain}.{entity}.{action}`
 *   - Examples: crm.account.created, invoicing.invoice.updated, workflow.job.completed
 *   - Message attributes for filtering: domain, entity, action, eventType
 *
 * Communication patterns (AAP §0.8.6):
 *   - SQS for async point-to-point delivery
 *   - SNS fan-out for multi-consumer domain events
 *   - At-least-once delivery guarantee via SQS
 *   - All event consumers MUST be idempotent (enforced in consumer code)
 *
 * Dual-target support (AAP §0.7.6):
 *   - LocalStack: DESTROY removal policy, no encryption
 *   - Production: RETAIN removal policy, AWS-managed encryption
 *
 * Consumed by:
 *   - shared-stack.ts: Creates central SNS topic (webvella-erp-domain-events)
 *   - reporting-stack.ts: Subscribes SQS queue for read-model projections
 *   - notifications-stack.ts: Subscribes SQS queue for email processing
 *   - Any stack needing SQS subscriptions to the central event bus
 */

import { Construct } from 'constructs';
import * as cdk from 'aws-cdk-lib';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as subscriptions from 'aws-cdk-lib/aws-sns-subscriptions';

/**
 * Defines a single SQS queue subscription to the SNS event bus topic.
 *
 * Each subscription creates a main queue (for consuming domain events) and a
 * dead-letter queue (for messages that exceed maxReceiveCount). This replaces
 * the monolith's `RecordHookManager.GetHookedInstances<T>(entityName)` pattern
 * where hooks were filtered by entity name — in the target architecture, SQS
 * filter policies on message attributes provide the equivalent selective routing.
 *
 * DLQ naming follows AAP §0.8.5: `{service}-{queue}-dlq`
 */
export interface SqsSubscriptionDefinition {
  /**
   * SQS queue name for the main consumer queue.
   * Example: 'webvella-erp-reporting-events', 'webvella-erp-notifications-email'
   */
  readonly queueName: string;

  /**
   * Dead-letter queue name. Auto-generated as `${queueName}-dlq` if not provided.
   * Per AAP §0.8.5: DLQ naming convention `{service}-{queue}-dlq`
   */
  readonly dlqName?: string;

  /**
   * SNS message attribute filter policy for selective event routing.
   * Enables per-subscription filtering by domain, entity, or action attributes.
   *
   * This replaces the monolith's `HookManager.GetHookedInstances<T>(key)` where
   * `key` filtered hooks by entity/relation name. In the target architecture,
   * SNS filter policies on message attributes (domain, entity, action, eventType)
   * provide equivalent selective routing at the infrastructure level.
   *
   * Example filter for CRM events only:
   *   { domain: sns.SubscriptionFilter.stringFilter({ allowlist: ['crm'] }) }
   *
   * Example filter for all created events:
   *   { action: sns.SubscriptionFilter.stringFilter({ allowlist: ['created'] }) }
   */
  readonly filterPolicy?: Record<string, sns.SubscriptionFilter>;

  /**
   * Visibility timeout in seconds for the SQS queue.
   * Should match or exceed the consuming Lambda function's timeout.
   * Default: 60 seconds.
   */
  readonly visibilityTimeoutSeconds?: number;

  /**
   * Maximum number of receive attempts before a message is sent to the DLQ.
   * Per AAP §0.8.5: Standard retry before DLQ routing.
   * Default: 3.
   */
  readonly maxReceiveCount?: number;

  /**
   * Message retention period in days for the main queue.
   * Default: 14 days (maximum SQS retention).
   */
  readonly messageRetentionDays?: number;

  /**
   * Lambda event source mapping batch size for consuming messages.
   * Stored on the definition for downstream consumers to reference when
   * configuring Lambda SQS event source mappings in service stacks.
   * Default: 10.
   */
  readonly batchSize?: number;
}

/**
 * Properties for the EventBusConstruct.
 *
 * Supports two operating modes:
 * 1. **Create new topic**: Provide `topicName` without `existingTopic`
 * 2. **Use existing topic**: Provide `existingTopic` (from SharedStack's central event bus)
 *
 * The `isLocalStack` flag controls dual-target behavior per AAP §0.7.6:
 * - LocalStack: DESTROY removal policies, no encryption
 * - Production: RETAIN removal policies, AWS-managed encryption at rest
 */
export interface EventBusProps {
  /**
   * SNS topic name for creating a new topic.
   * If `existingTopic` is provided, this property is ignored.
   * Example: 'webvella-erp-domain-events'
   */
  readonly topicName?: string;

  /**
   * Reference to an existing SNS topic (e.g., from SharedStack's central event bus).
   * When provided, no new topic is created — the construct subscribes queues to it.
   * This is the primary pattern: SharedStack creates the topic, other stacks subscribe.
   */
  readonly existingTopic?: sns.ITopic;

  /**
   * Dual-target flag per AAP §0.7.6.
   * - true: LocalStack mode — DESTROY removal policies, no encryption
   * - false: Production mode — RETAIN removal policies, encryption at rest
   */
  readonly isLocalStack: boolean;

  /**
   * Array of SQS queue subscriptions to create and subscribe to the topic.
   * Each subscription creates a main queue + DLQ pair, then subscribes to the topic
   * with an optional filter policy for selective event routing.
   */
  readonly subscriptions?: SqsSubscriptionDefinition[];

  /**
   * Enable encryption at rest for SNS topic and SQS queues.
   * Per AAP §0.8.3: Encryption at rest for all datastores (production only).
   * Default: true for production (when isLocalStack is false).
   * Encryption is always disabled when isLocalStack is true, regardless of this flag.
   */
  readonly enableEncryption?: boolean;
}

/**
 * EventBusConstruct — Reusable L3 CDK construct for SNS/SQS event bus infrastructure.
 *
 * This construct encapsulates the standard pattern for domain event publishing and
 * consumption across all WebVella ERP microservices. It replaces:
 *
 * - `HookManager.RegisterHooks()` — Reflection-based hook discovery scanning all
 *   assemblies for `[HookAttachment]`-decorated classes → SNS topic subscription
 * - `RecordHookManager.ExecutePost*Hooks()` — Sequential post-hook invocation
 *   → SNS event publishing from Lambda handlers
 * - `NotificationContext.SendNotification()` — PostgreSQL NOTIFY on
 *   `ERP_NOTIFICATIONS_CHANNNEL` → SNS publish
 * - `NotificationContext.ListenForNotifications()` — PostgreSQL LISTEN +
 *   `sqlConnection.Wait()` loop → SQS queue polling via Lambda event source mapping
 *
 * Hook Interface → SNS Event Mapping (AAP §0.7.2):
 * - IErpPostCreateRecordHook → {domain}.{entity}.created
 * - IErpPostUpdateRecordHook → {domain}.{entity}.updated
 * - IErpPostDeleteRecordHook → {domain}.{entity}.deleted
 * - IErpPostCreate/DeleteManyToManyRelationHook → {domain}.relation.created/deleted
 * - Pre-hooks (IErpPreCreate/Update/DeleteRecordHook) remain synchronous in Lambda handlers
 *
 * @example Create new topic with subscriptions (SharedStack):
 * ```ts
 * const eventBus = new EventBusConstruct(this, 'DomainEventBus', {
 *   topicName: 'webvella-erp-domain-events',
 *   isLocalStack: isLocalStack,
 *   subscriptions: [{
 *     queueName: 'webvella-erp-reporting-events',
 *     filterPolicy: undefined, // receive all events for read-model projections
 *   }],
 * });
 * ```
 *
 * @example Subscribe to existing topic (ReportingStack):
 * ```ts
 * const eventBus = new EventBusConstruct(this, 'ReportingEventBus', {
 *   existingTopic: sharedStack.domainEventTopic,
 *   isLocalStack: isLocalStack,
 *   subscriptions: [{
 *     queueName: 'webvella-erp-reporting-all-events',
 *   }],
 * });
 * ```
 *
 * @example Filtered subscription (NotificationsStack):
 * ```ts
 * const eventBus = new EventBusConstruct(this, 'NotificationEventBus', {
 *   existingTopic: sharedStack.domainEventTopic,
 *   isLocalStack: isLocalStack,
 *   subscriptions: [{
 *     queueName: 'webvella-erp-notifications-email',
 *     filterPolicy: {
 *       domain: sns.SubscriptionFilter.stringFilter({
 *         allowlist: ['crm', 'invoicing', 'workflow'],
 *       }),
 *     },
 *   }],
 * });
 * ```
 */
export class EventBusConstruct extends Construct {
  /**
   * The SNS topic for domain event publishing.
   * Either a newly created topic or a reference to an existing topic from SharedStack.
   * Stacks use this to:
   * - Pass topic ARN to LambdaServiceConstruct for SNS publish permissions
   * - Reference for cross-stack outputs
   */
  public readonly topic: sns.ITopic;

  /**
   * Array of all SQS main queues created by this construct.
   * Stacks use these to:
   * - Create Lambda SQS event source mappings for consuming domain events
   * - Pass queue ARNs to LambdaServiceConstruct for SQS receive permissions
   */
  public readonly queues: sqs.Queue[];

  /**
   * Array of all SQS dead-letter queues created by this construct.
   * Stacks use these to:
   * - Create CloudWatch alarms on DLQ message count (production monitoring)
   * - Reference DLQ ARNs for operational dashboards
   */
  public readonly deadLetterQueues: sqs.Queue[];

  constructor(scope: Construct, id: string, props: EventBusProps) {
    super(scope, id);

    // Initialize queue arrays
    this.queues = [];
    this.deadLetterQueues = [];

    // Determine removal policy based on dual-target flag (AAP §0.7.6)
    // LocalStack: DESTROY for clean teardown; Production: RETAIN to prevent data loss
    const removalPolicy = props.isLocalStack
      ? cdk.RemovalPolicy.DESTROY
      : cdk.RemovalPolicy.RETAIN;

    // Determine if encryption should be applied
    // Per AAP §0.8.3: Encryption at rest for production only
    // Encryption is always disabled for LocalStack regardless of enableEncryption flag
    const shouldEncrypt =
      !props.isLocalStack && (props.enableEncryption !== false);

    // =========================================================================
    // Phase 1: Topic Resolution
    // =========================================================================
    // Two modes of operation:
    // 1. Use existing topic (primary pattern — SharedStack creates, others subscribe)
    // 2. Create new topic (when this construct owns the topic lifecycle)
    // =========================================================================

    if (props.existingTopic) {
      // Mode 1: Use an existing SNS topic from another stack (e.g., SharedStack)
      // This is the primary consumption pattern — SharedStack creates the central
      // domain event topic, and other stacks (Reporting, Notifications, etc.)
      // subscribe their SQS queues to it.
      this.topic = props.existingTopic;
    } else {
      // Mode 2: Create a new SNS topic
      // Used by SharedStack to create the central domain event bus topic
      // Topic name defaults to the construct id if topicName is not provided
      const topicName = props.topicName ?? `${id}-topic`;

      this.topic = new sns.Topic(this, 'Topic', {
        topicName: topicName,
        displayName: `WebVella ERP Domain Events - ${topicName}`,
        // Per AAP §0.8.3: Encryption at rest for production
        // LocalStack does not support KMS-encrypted SNS topics reliably
        ...(shouldEncrypt
          ? { masterKey: undefined } // AWS-managed encryption (SSE-SNS) is default when no key
          : {}),
      });

      // Apply removal policy to the topic
      // LocalStack: DESTROY for clean teardown
      // Production: RETAIN to prevent accidental topic deletion
      (this.topic as sns.Topic).applyRemovalPolicy(removalPolicy);
    }

    // =========================================================================
    // Phase 2: SQS Queue + DLQ Provisioning
    // =========================================================================
    // For each subscription definition, create:
    //   a. Dead-Letter Queue (DLQ) — captures messages exceeding maxReceiveCount
    //   b. Main SQS Queue — configured with visibility timeout, retention, DLQ
    //   c. SNS → SQS Subscription — with optional filter policy
    //
    // This replaces the monolith's:
    // - RecordHookManager.ExecutePost*Hooks(): sequential in-process invocation
    //   → async message delivery via SQS with at-least-once guarantee
    // - NotificationContext.HandleNotification(): channel-filtered method invocation
    //   → SNS filter policy routing to domain-specific SQS queues
    // =========================================================================

    if (props.subscriptions && props.subscriptions.length > 0) {
      for (let i = 0; i < props.subscriptions.length; i++) {
        const subscription = props.subscriptions[i];

        // Derive DLQ name per AAP §0.8.5 naming convention: {service}-{queue}-dlq
        const dlqName = subscription.dlqName ?? `${subscription.queueName}-dlq`;

        // Sanitize queue name for use as CDK construct id (remove special chars)
        const sanitizedId = subscription.queueName
          .replace(/[^a-zA-Z0-9]/g, '')
          .substring(0, 64);

        // a. Create Dead-Letter Queue (DLQ)
        // DLQ captures messages that fail processing after maxReceiveCount attempts.
        // Retention is always 14 days (maximum) for debugging failed messages.
        const dlq = new sqs.Queue(this, `Dlq${sanitizedId}`, {
          queueName: dlqName,
          retentionPeriod: cdk.Duration.days(14),
          removalPolicy: removalPolicy,
          // Per AAP §0.8.3: Encryption at rest for production
          ...(shouldEncrypt
            ? { encryption: sqs.QueueEncryption.SQS_MANAGED }
            : {}),
        });

        this.deadLetterQueues.push(dlq);

        // b. Create Main SQS Queue
        // Main queue for consuming domain events from the SNS topic.
        // Visibility timeout should match or exceed the consuming Lambda timeout.
        // Message retention defaults to 14 days per requirements.
        const mainQueue = new sqs.Queue(this, `Queue${sanitizedId}`, {
          queueName: subscription.queueName,
          visibilityTimeout: cdk.Duration.seconds(
            subscription.visibilityTimeoutSeconds ?? 60,
          ),
          retentionPeriod: cdk.Duration.days(
            subscription.messageRetentionDays ?? 14,
          ),
          deadLetterQueue: {
            queue: dlq,
            maxReceiveCount: subscription.maxReceiveCount ?? 3,
          },
          removalPolicy: removalPolicy,
          // Per AAP §0.8.3: Encryption at rest for production
          ...(shouldEncrypt
            ? { encryption: sqs.QueueEncryption.SQS_MANAGED }
            : {}),
        });

        this.queues.push(mainQueue);

        // c. Create SNS → SQS Subscription
        // Subscribe the SQS queue to the SNS topic with optional filter policy.
        // This replaces the monolith's:
        // - HookManager.GetHookedInstances<T>(key) filtered by AttachAttribute.Key
        //   → SNS SubscriptionFilter on message attributes (domain, entity, action)
        // - NotificationContext listeners filtered by Channel
        //   → SNS SubscriptionFilter on eventType attribute
        //
        // Per AAP §0.8.5: At-least-once delivery guarantee via SQS.
        // Per AAP §0.8.6: SQS for async point-to-point; SNS fan-out for multi-consumer.
        const sqsSubscription = new subscriptions.SqsSubscription(mainQueue, {
          // Apply filter policy for selective event routing if provided
          // Examples:
          //   { domain: SubscriptionFilter.stringFilter({ allowlist: ['crm'] }) }
          //   { action: SubscriptionFilter.stringFilter({ allowlist: ['created'] }) }
          //   { eventType: SubscriptionFilter.stringFilter({ allowlist: ['crm.account.created'] }) }
          ...(subscription.filterPolicy
            ? { filterPolicy: subscription.filterPolicy }
            : {}),
          // Raw message delivery: false (use standard SNS envelope for metadata)
          rawMessageDelivery: false,
        });

        this.topic.addSubscription(sqsSubscription);
      }
    }
  }
}
