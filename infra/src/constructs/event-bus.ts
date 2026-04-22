/**
 * infra/src/constructs/event-bus.ts
 *
 * Standard SNS/SQS Event Bus Construct with DLQ for WebVella ERP
 *
 * This L3 CDK construct standardizes the creation of SNS topics and SQS queues
 * with mandatory dead-letter queues (DLQs) across all bounded-context microservices.
 * It replaces the monolith's three in-process messaging/event systems:
 *
 * 1. HookManager (WebVella.Erp/Hooks/HookManager.cs) — Reflection-based synchronous
 *    post-hook invocation → replaced by SNS domain event publishing.
 *
 * 2. NotificationContext (WebVella.Erp/Notifications/) — PostgreSQL LISTEN/NOTIFY
 *    pub/sub → replaced by SNS topics + SQS queue subscriptions.
 *
 * 3. JobManager (WebVella.Erp/Jobs/JobManager.cs) — 20-thread bounded polling loop
 *    → replaced by SQS-triggered Lambda consumers.
 *
 * Architecture Rules Enforced (AAP references):
 * - §0.4.2: SNS topics for domain events, SQS queues for consumer decoupling, DLQs for all consumers
 * - §0.7.2: Post-hooks → SNS events; pre-hooks → API-level validation
 * - §0.8.5: DLQ naming convention: {service}-{queue}-dlq
 * - §0.8.5: Event naming convention: {domain}.{entity}.{action}
 * - §0.8.5: At-least-once delivery guarantee via SQS
 * - §0.8.5: All event consumers MUST be idempotent (enforced by architecture, not code)
 * - §0.8.6: SQS for async point-to-point; SNS fan-out for multi-consumer events
 * - §0.8.1: Zero cross-service database access — events are the ONLY cross-boundary communication
 */

import * as cdk from 'aws-cdk-lib';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as snsSubscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as lambdaEventSources from 'aws-cdk-lib/aws-lambda-event-sources';
import { Construct } from 'constructs';

/**
 * Configuration for an SQS queue that subscribes to the SNS topic.
 *
 * Each queue subscription creates:
 * - A dead-letter queue (DLQ) with naming convention {service}-{queue}-dlq
 * - A main queue with configurable retry/retention settings
 * - An SNS→SQS subscription with optional filter policy
 * - An optional SQS→Lambda event source mapping for consumer Lambdas
 */
export interface QueueSubscription {
  /**
   * Queue name suffix (e.g., 'record-created', 'email-send').
   * Full queue name will be: {serviceName}-{queueName}
   * Full DLQ name will be: {serviceName}-{queueName}-dlq
   */
  queueName: string;

  /**
   * SNS filter policy for this subscription.
   * Enables selective event consumption from the domain event bus
   * by filtering on message attributes.
   * Example: { eventType: sns.SubscriptionFilter.stringFilter({ allowlist: ['entity.created'] }) }
   */
  filterPolicy?: { [key: string]: sns.SubscriptionFilter };

  /**
   * Maximum number of receives before a message is sent to the DLQ.
   * After this many failed processing attempts, the message moves to the DLQ.
   * @default 3
   */
  maxReceiveCount?: number;

  /**
   * Visibility timeout in seconds. Must be >= 6x the Lambda function timeout
   * to prevent duplicate processing during retries.
   * @default 30
   */
  visibilityTimeoutSeconds?: number;

  /**
   * Message retention period in days. Messages older than this are automatically deleted.
   * @default 14
   */
  retentionPeriodDays?: number;

  /**
   * Lambda function to trigger from this queue. When provided, an SQS event source
   * mapping is created to wire the queue as a Lambda trigger, replacing the monolith's
   * JobPool 20-thread bounded polling executor with event-driven invocation.
   * May be omitted and set later via direct SQS event source configuration.
   */
  consumer?: lambda.IFunction;
}

/**
 * Properties for the WebVellaEventBus construct.
 *
 * Configures the SNS topic, queue subscriptions, and environment-specific
 * behavior (LocalStack vs production).
 */
export interface WebVellaEventBusProps {
  /**
   * Service domain name (e.g., 'identity', 'crm', 'entity-management').
   * Used as prefix for all resource names to enforce per-service isolation.
   */
  serviceName: string;

  /**
   * SNS topic name override. If not provided, defaults to '{serviceName}-events'.
   * Topics follow the naming convention per AAP §0.8.5.
   */
  topicName?: string;

  /**
   * Queue subscriptions to create. Each subscription creates a main queue,
   * a DLQ, an SNS→SQS subscription, and optionally an SQS→Lambda event source.
   * @default [] (topic only, no queues)
   */
  queues?: QueueSubscription[];

  /**
   * Whether this construct is being deployed to LocalStack.
   * Controls removal policy (DESTROY for LocalStack, RETAIN for production),
   * and other environment-specific settings.
   */
  isLocalStack: boolean;

  /**
   * Explicit removal policy override. If not provided, defaults to:
   * - DESTROY for LocalStack (clean teardown in dev)
   * - RETAIN for production (protect data)
   */
  removalPolicy?: cdk.RemovalPolicy;
}

/**
 * Default values for queue subscription configuration.
 * These defaults provide sensible out-of-the-box behavior aligned with
 * the AAP's operational requirements (§0.8.5).
 */
const QUEUE_DEFAULTS = {
  /** Default maximum receive count before DLQ routing */
  MAX_RECEIVE_COUNT: 3,
  /** Default visibility timeout in seconds — must be >= 6x Lambda timeout */
  VISIBILITY_TIMEOUT_SECONDS: 30,
  /** Default message retention period in days */
  RETENTION_PERIOD_DAYS: 14,
  /** Default DLQ retention period in days (always 14 for forensic analysis) */
  DLQ_RETENTION_PERIOD_DAYS: 14,
  /** Default batch size for SQS→Lambda event source mapping */
  LAMBDA_BATCH_SIZE: 10,
} as const;

/**
 * Standard SNS/SQS Event Bus construct for WebVella ERP microservices.
 *
 * Creates an SNS topic with optional SQS queue subscriptions, each backed
 * by a mandatory dead-letter queue (DLQ). This construct is used by all
 * bounded-context service stacks to implement the event-driven architecture
 * that replaces the monolith's HookManager, NotificationContext, and JobManager.
 *
 * Usage example:
 * ```typescript
 * const eventBus = new WebVellaEventBus(this, 'EventBus', {
 *   serviceName: 'crm',
 *   isLocalStack: true,
 *   queues: [
 *     {
 *       queueName: 'account-events',
 *       filterPolicy: {
 *         eventType: sns.SubscriptionFilter.stringFilter({
 *           allowlist: ['crm.account.created', 'crm.account.updated'],
 *         }),
 *       },
 *       consumer: accountEventHandler.function,
 *     },
 *   ],
 * });
 * ```
 */
export class WebVellaEventBus extends Construct {
  /**
   * The SNS topic serving as the domain event bus.
   * Other services can subscribe to this topic for cross-boundary events.
   * Replaces the monolith's HookManager synchronous post-hook invocation
   * and PostgreSQL LISTEN/NOTIFY pub/sub.
   */
  public readonly topic: sns.Topic;

  /**
   * Map of queue name suffix to SQS main queue instance.
   * Key is the queueName from QueueSubscription, value is the created SQS queue.
   * Used by stacks to reference queues for additional configuration or IAM grants.
   */
  public readonly queues: Map<string, sqs.Queue>;

  /**
   * Map of queue name suffix to SQS dead-letter queue (DLQ) instance.
   * Key is the queueName from QueueSubscription, value is the created DLQ.
   * DLQs capture messages that fail processing after maxReceiveCount attempts.
   * Naming follows AAP §0.8.5 convention: {service}-{queue}-dlq
   */
  public readonly deadLetterQueues: Map<string, sqs.Queue>;

  /**
   * The ARN of the SNS topic. Useful for cross-stack references
   * and SSM parameter storage for runtime service discovery.
   */
  public readonly topicArn: string;

  constructor(scope: Construct, id: string, props: WebVellaEventBusProps) {
    super(scope, id);

    // Determine the removal policy based on deployment target
    const effectiveRemovalPolicy = props.removalPolicy
      ?? (props.isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN);

    // Initialize queue maps
    this.queues = new Map<string, sqs.Queue>();
    this.deadLetterQueues = new Map<string, sqs.Queue>();

    // ---------------------------------------------------------------
    // 1. Create SNS Topic
    // Replaces: HookManager synchronous post-hook invocation
    // Replaces: PostgreSQL LISTEN/NOTIFY NotificationContext pub/sub
    // ---------------------------------------------------------------
    const topicName = props.topicName ?? `${props.serviceName}-events`;
    const displayServiceName = props.serviceName
      .split('-')
      .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
      .join(' ');

    this.topic = new sns.Topic(this, 'Topic', {
      topicName: topicName,
      displayName: `WebVella ERP ${displayServiceName} Events`,
    });

    // Apply removal policy to the SNS topic
    this.topic.applyRemovalPolicy(effectiveRemovalPolicy);

    // Apply resource tags for identification and cost allocation
    cdk.Tags.of(this.topic).add('service', props.serviceName);
    cdk.Tags.of(this.topic).add('resource', 'event-bus');

    // Store the topic ARN for cross-stack references and SSM parameter storage
    this.topicArn = this.topic.topicArn;

    // ---------------------------------------------------------------
    // 2. Create Queue Subscriptions
    // For each QueueSubscription, create: DLQ → Main Queue → SNS Subscription → Lambda Event Source
    // Replaces: JobManager 20-thread bounded polling loop with SQS-triggered Lambdas
    // ---------------------------------------------------------------
    const queueSubscriptions = props.queues ?? [];

    for (const queueSub of queueSubscriptions) {
      // Resolve configuration with defaults
      const maxReceiveCount = queueSub.maxReceiveCount ?? QUEUE_DEFAULTS.MAX_RECEIVE_COUNT;
      const visibilityTimeoutSeconds = queueSub.visibilityTimeoutSeconds ?? QUEUE_DEFAULTS.VISIBILITY_TIMEOUT_SECONDS;
      const retentionPeriodDays = queueSub.retentionPeriodDays ?? QUEUE_DEFAULTS.RETENTION_PERIOD_DAYS;

      // Construct a PascalCase CDK logical ID from the queue name
      const logicalIdSuffix = queueSub.queueName
        .split('-')
        .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
        .join('');

      // -------------------------------------------------------
      // 2a. Create Dead-Letter Queue (DLQ)
      // Naming convention per AAP §0.8.5: {service}-{queue}-dlq
      // -------------------------------------------------------
      const dlqName = `${props.serviceName}-${queueSub.queueName}-dlq`;

      const dlq = new sqs.Queue(this, `${logicalIdSuffix}Dlq`, {
        queueName: dlqName,
        retentionPeriod: cdk.Duration.days(QUEUE_DEFAULTS.DLQ_RETENTION_PERIOD_DAYS),
        removalPolicy: effectiveRemovalPolicy,
      });

      // Tag the DLQ for identification
      cdk.Tags.of(dlq).add('service', props.serviceName);
      cdk.Tags.of(dlq).add('resource', 'dead-letter-queue');
      cdk.Tags.of(dlq).add('parent-queue', queueSub.queueName);

      this.deadLetterQueues.set(queueSub.queueName, dlq);

      // -------------------------------------------------------
      // 2b. Create Main Queue
      // At-least-once delivery guarantee via SQS (AAP §0.8.5)
      // Messages failing maxReceiveCount attempts route to DLQ
      // -------------------------------------------------------
      const mainQueueName = `${props.serviceName}-${queueSub.queueName}`;

      const mainQueue = new sqs.Queue(this, `${logicalIdSuffix}Queue`, {
        queueName: mainQueueName,
        visibilityTimeout: cdk.Duration.seconds(visibilityTimeoutSeconds),
        retentionPeriod: cdk.Duration.days(retentionPeriodDays),
        deadLetterQueue: {
          queue: dlq,
          maxReceiveCount: maxReceiveCount,
        },
        removalPolicy: effectiveRemovalPolicy,
      });

      // Tag the main queue for identification
      cdk.Tags.of(mainQueue).add('service', props.serviceName);
      cdk.Tags.of(mainQueue).add('resource', 'event-queue');

      this.queues.set(queueSub.queueName, mainQueue);

      // -------------------------------------------------------
      // 2c. Create SNS → SQS Subscription
      // Enables selective event consumption via filter policies
      // Raw message delivery avoids SNS envelope wrapping (AAP §0.8.6)
      // -------------------------------------------------------
      const subscriptionProps: snsSubscriptions.SqsSubscriptionProps = {
        rawMessageDelivery: true,
        ...(queueSub.filterPolicy ? { filterPolicy: queueSub.filterPolicy } : {}),
      };

      this.topic.addSubscription(
        new snsSubscriptions.SqsSubscription(mainQueue, subscriptionProps),
      );

      // -------------------------------------------------------
      // 2d. Create SQS → Lambda Event Source (if consumer provided)
      // Replaces: JobPool 20-thread bounded polling executor
      // -------------------------------------------------------
      if (queueSub.consumer) {
        const eventSource = new lambdaEventSources.SqsEventSource(mainQueue, {
          batchSize: QUEUE_DEFAULTS.LAMBDA_BATCH_SIZE,
          enabled: true,
        });

        queueSub.consumer.addEventSource(eventSource);

        // Grant the consumer Lambda permissions to read from the queue
        // This includes sqs:ReceiveMessage, sqs:DeleteMessage, sqs:GetQueueAttributes
        mainQueue.grantConsumeMessages(queueSub.consumer);
      }
    }
  }
}
