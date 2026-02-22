/**
 * NotificationsStack — Notifications Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the Notifications bounded
 * context, replacing the monolith's SMTP email queue processing, PostgreSQL
 * LISTEN/NOTIFY pub/sub, and in-process webhook dispatch with a serverless
 * architecture using SQS, SES, SNS, and Lambda.
 *
 * **Source systems replaced:**
 * - `SmtpService.cs` / `SmtpInternalService.cs` — Email send/queue API with
 *   MailKit/MimeKit SMTP client, validation hooks, queue processing locks
 * - `ProcessSmtpQueueJob.cs` — Scheduled 10-minute interval job polling
 *   PostgreSQL for queued emails → SQS-triggered Lambda consumer
 * - `NotificationService.cs` — PostgreSQL LISTEN/NOTIFY pub/sub →
 *   SNS/SQS event-driven notifications
 * - `MailPlugin.cs` + 7 patch files — `email` and `smtp_service` entity
 *   definitions with queue status fields (to/from/subject/body/status/retry)
 *
 * **Target architecture:**
 * - DynamoDB table for notification/email/webhook metadata
 * - SQS queue for email send processing (replacing PostgreSQL polling)
 * - SES for email delivery (replacing direct SMTP via MailKit)
 * - Lambda functions for email operations, webhook dispatch, queue processing
 * - SNS domain event publishing for cross-service notification triggers
 *
 * Resources created:
 *
 * 1. **DynamoDB Table** (`notifications-notifications`) — Single-table design
 *    storing all notification metadata. Partition key patterns:
 *    - `EMAIL#{emailId}` — Email message records (to, from, subject, body,
 *      status, retry count — migrated from `email` entity rec_email table)
 *    - `NOTIFICATION#{notifId}` — In-app notification records
 *    - `WEBHOOK#{hookId}` — Webhook subscription and dispatch records
 *      (replaces PostgreSQL LISTEN/NOTIFY from NotificationService.cs)
 *    - `SMTP_CONFIG#{configId}` — SMTP service configuration records
 *      (migrated from `smtp_service` entity rec_smtp_service table)
 *    Sort key: `META` for main records, timestamps for queue ordering.
 *    GSI1: `GSI1PK`/`GSI1SK` for status-based lookups enabling queued
 *    email retrieval and pending notification queries.
 *
 * 2. **SQS Queues**:
 *    - `webvella-erp-notifications-email-queue` — Email send queue replacing
 *      ProcessSmtpQueueJob.cs PostgreSQL polling. Visibility timeout 60s,
 *      message retention 14 days.
 *    - `webvella-erp-notifications-email-queue-dlq` — Dead-letter queue per
 *      AAP §0.8.5 naming convention `{service}-{queue}-dlq`. Max receive
 *      count 3 before messages move to DLQ for manual inspection.
 *
 * 3. **SES Email Identity** — Conditional on deployment target:
 *    - LocalStack: SES stub available (all operations succeed)
 *    - Production: Real SES with verified domain identity
 *    Note: Third-party SMTP integrations are stubbed per AAP §0.3.2.
 *
 * 4. **Lambda Functions** (3 handlers, .NET 9 Native AOT):
 *    a. **EmailHandler** (`webvella-notifications-email`) — 512 MB, 30s.
 *       Email send/queue operations replacing SmtpService.cs public API.
 *    b. **WebhookHandler** (`webvella-notifications-webhook`) — 512 MB, 30s.
 *       Webhook dispatch replacing PostgreSQL LISTEN/NOTIFY from
 *       NotificationService.cs.
 *    c. **QueueProcessor** (`webvella-notifications-queue-processor`) —
 *       512 MB, 60s. SQS-triggered email queue processor replacing
 *       ProcessSmtpQueueJob.cs scheduled polling. Batch size 5 for
 *       throttled email sending. MUST be idempotent per AAP §0.8.5.
 *
 * 5. **SNS Subscription** — Routes domain events from the SharedStack's
 *    central event bus to the notifications SQS queue for event-driven
 *    notifications (e.g., crm.account.created → welcome email).
 *
 * 6. **SSM Parameters** — Resource discovery per AAP §0.8.6:
 *    - `/webvella-erp/notifications/table-name` → DynamoDB table name
 *    - `/webvella-erp/notifications/queue-url` → SQS queue URL
 *
 * Domain events published to the shared SNS event bus:
 * - `notifications.email.sent` — Email successfully sent via SES
 * - `notifications.email.queued` — Email queued for async processing
 * - `notifications.webhook.dispatched` — Webhook notification dispatched
 *
 * Source files referenced:
 * - WebVella.Erp.Plugins.Mail/MailPlugin.cs — email/smtp_service entity defs
 * - WebVella.Erp.Plugins.Mail/Services/SmtpInternalService.cs — SMTP engine
 * - WebVella.Erp.Plugins.Mail/Services/SmtpService.cs — Public email API
 * - WebVella.Erp.Plugins.Mail/Jobs/ProcessSmtpQueueJob.cs — Queue processor
 * - WebVella.Erp/Notifications/NotificationService.cs — LISTEN/NOTIFY pub/sub
 *
 * @module infra/src/stacks/notifications-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as ses from 'aws-cdk-lib/aws-ses';
import * as snsSubscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import * as lambdaEventSources from 'aws-cdk-lib/aws-lambda-event-sources';

import {
  WebVellaLambdaService,
  LambdaRuntime,
  WebVellaDynamoDBTable,
  GsiDefinition,
} from '../constructs';

// ---------------------------------------------------------------------------
// Interface: NotificationsStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the NotificationsStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2).
 */
export interface NotificationsStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - Removal policies: DESTROY (LocalStack) vs RETAIN (production)
   * - SES identity: stub (LocalStack) vs verified domain (production)
   * - Lambda tracing, architecture, and log retention
   * - AWS_ENDPOINT_URL injection for SDK redirects
   */
  readonly isLocalStack: boolean;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. The notification Lambda functions publish domain
   * events to this topic using the naming convention from AAP §0.8.5:
   * - `notifications.email.sent`
   * - `notifications.email.queued`
   * - `notifications.webhook.dispatched`
   *
   * Also subscribed to for receiving domain events from other services
   * that trigger notifications (e.g., crm.account.created → welcome email).
   * Replaces the monolith's PostgreSQL LISTEN/NOTIFY from
   * NotificationService.cs and synchronous HookManager post-hook invocations.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: NotificationsStack
// ---------------------------------------------------------------------------

/**
 * NotificationsStack — CDK stack for the Notifications bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own DynamoDB
 * table, SQS queues, SES identity, Lambda functions, IAM policies, and
 * SSM parameters. No other service may directly access the notification
 * service's datastore or queues.
 *
 * The stack exposes three public properties consumed by ApiGatewayStack for
 * route-to-Lambda integration mapping and cross-stack resource discovery:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `tableName` — DynamoDB table name (also published as SSM parameter)
 * - `queueUrl` — SQS email queue URL (also published as SSM parameter)
 *
 * @example
 * ```typescript
 * const notificationsStack = new NotificationsStack(app, 'NotificationsStack', {
 *   isLocalStack: true,
 *   eventBus: sharedStack.eventBus,
 *   env: { account: '000000000000', region: 'us-east-1' },
 * });
 * ```
 */
export class NotificationsStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the EmailHandler, WebhookHandler, and QueueProcessor functions
   * that handle all notification HTTP endpoints and SQS processing. Consumed
   * by ApiGatewayStack for path-based routing under `/v1/notifications/*`.
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB table name for the notifications service datastore.
   *
   * Follows the naming pattern: `notifications-notifications` (generated by
   * WebVellaDynamoDBTable as `{serviceName}-{tableName}`).
   * Also published as SSM parameter at `/webvella-erp/notifications/table-name`.
   */
  public readonly tableName: string;

  /**
   * SQS queue URL for the email processing queue.
   *
   * Used by the EmailHandler Lambda to enqueue emails for async processing
   * and by external services to submit email send requests.
   * Also published as SSM parameter at `/webvella-erp/notifications/queue-url`.
   */
  public readonly queueUrl: string;

  constructor(scope: Construct, id: string, props: NotificationsStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Table — Single-table design for notifications
    // -----------------------------------------------------------------------
    // Replaces PostgreSQL tables from MailPlugin.cs entity definitions:
    //   - `rec_email` (email entity records with status/retry fields)
    //   - `rec_smtp_service` (SMTP configuration records)
    // And from NotificationService.cs:
    //   - PostgreSQL LISTEN/NOTIFY channel subscriptions
    //
    // Access patterns:
    //   PK=EMAIL#{emailId},         SK=META          → Email message record
    //   PK=NOTIFICATION#{notifId},  SK=META          → In-app notification
    //   PK=WEBHOOK#{hookId},        SK=META          → Webhook subscription
    //   PK=SMTP_CONFIG#{configId},  SK=META          → SMTP service config
    //
    // GSI1 enables status-based lookups for queue processing:
    //   GSI1PK=STATUS#queued,       GSI1SK=CREATED#{ts} → Queued emails
    //   GSI1PK=STATUS#pending,      GSI1SK=CREATED#{ts} → Pending notifications
    //   GSI1PK=STATUS#failed,       GSI1SK=RETRY#{count} → Failed for retry
    //
    // This replaces SmtpInternalService.cs queue processing lock + SQL:
    //   WHERE status = 'queued' ORDER BY created_on ASC LIMIT batch_size

    const gsiDefinitions: GsiDefinition[] = [
      {
        indexName: 'GSI1',
        partitionKey: {
          name: 'GSI1PK',
          type: dynamodb.AttributeType.STRING,
        },
        sortKey: {
          name: 'GSI1SK',
          type: dynamodb.AttributeType.STRING,
        },
      },
    ];

    const notificationsTable = new WebVellaDynamoDBTable(
      this,
      'NotificationsTable',
      {
        serviceName: 'notifications',
        tableName: 'notifications',
        isLocalStack,
        globalSecondaryIndexes: gsiDefinitions,
      },
    );

    // -----------------------------------------------------------------------
    // 2. SQS Queues — Email processing queue with DLQ
    // -----------------------------------------------------------------------
    // Replaces the monolith's ProcessSmtpQueueJob.cs scheduled polling pattern
    // (10-minute interval via SchedulePlan in MailPlugin.cs) with an event-
    // driven SQS queue. Emails are enqueued by the EmailHandler Lambda and
    // consumed by the QueueProcessor Lambda.
    //
    // SmtpInternalService.cs queue processing logic:
    //   1. Lock via ProcessSmtpQueueLock (Semaphore)
    //   2. Query queued emails from PostgreSQL
    //   3. Send each email via MailKit SmtpClient
    //   4. Update status to sent/failed in PostgreSQL
    //
    // In the target architecture:
    //   1. EmailHandler enqueues message to SQS (no lock needed)
    //   2. QueueProcessor Lambda triggered by SQS event source
    //   3. Send email via AWS SES (replacing MailKit SMTP)
    //   4. Update status in DynamoDB
    //   5. Failed messages automatically routed to DLQ after 3 attempts

    // Dead-letter queue for emails that fail processing after max retries.
    // Named per AAP §0.8.5 convention: {service}-{queue}-dlq
    const emailQueueDlq = new sqs.Queue(this, 'EmailQueueDlq', {
      queueName: 'webvella-erp-notifications-email-queue-dlq',
      retentionPeriod: cdk.Duration.days(14),
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });

    // Main email processing queue.
    // Visibility timeout (60s) must be >= QueueProcessor Lambda timeout (60s)
    // to prevent duplicate processing. Message retention 14 days ensures
    // resilience against extended outages.
    const emailQueue = new sqs.Queue(this, 'EmailQueue', {
      queueName: 'webvella-erp-notifications-email-queue',
      visibilityTimeout: cdk.Duration.seconds(60),
      retentionPeriod: cdk.Duration.days(14),
      deadLetterQueue: {
        queue: emailQueueDlq,
        maxReceiveCount: 3,
      } as sqs.DeadLetterQueue,
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });

    // -----------------------------------------------------------------------
    // 3. SES Email Identity — Email sending capability
    // -----------------------------------------------------------------------
    // Replaces SmtpInternalService.cs direct SMTP connection via MailKit:
    //   new SmtpClient() → Connect(host, port, SecureSocketOptions)
    //   → Authenticate(username, password) → Send(mimeMessage)
    //
    // In LocalStack mode, SES is available as a stub — all operations succeed
    // without actual email delivery. In production, SES requires a verified
    // domain identity for sending.
    //
    // Per AAP §0.3.2: Third-party SaaS integrations (external SMTP providers)
    // are stubbed interfaces only. SES serves as the AWS-native replacement.

    if (!isLocalStack) {
      // Production: Create verified domain identity for email sending.
      // The domain should be configured via Route53 or external DNS.
      new ses.EmailIdentity(this, 'NotificationEmailIdentity', {
        identity: ses.Identity.domain('notifications.webvella-erp.local'),
      });
    }
    // LocalStack: SES is available as a stub without explicit identity setup.
    // All SES API calls succeed — emails are captured in LocalStack's SES
    // mock backend for testing verification.

    // -----------------------------------------------------------------------
    // 4. SNS Subscription — Domain events triggering notifications
    // -----------------------------------------------------------------------
    // Subscribe the email queue to the shared SNS event bus to receive
    // domain events from other services that should trigger notifications.
    // Examples:
    //   - crm.account.created → Send welcome email to new account contact
    //   - invoicing.invoice.created → Send invoice notification email
    //   - workflow.job.failed → Send failure alert notification
    //
    // This replaces the monolith's PostgreSQL LISTEN/NOTIFY pattern from
    // NotificationService.cs where services published notifications via
    // pg_notify() and the notification service subscribed to channels.
    //
    // Filter policy can be refined per domain event type when consumers
    // are implemented. For now, all events are routed to allow the
    // QueueProcessor to inspect and filter at the application level.

    eventBus.addSubscription(
      new snsSubscriptions.SqsSubscription(emailQueue, {
        rawMessageDelivery: true,
      }),
    );

    // -----------------------------------------------------------------------
    // 5. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the notifications table and GSIs.
    // Replaces direct PostgreSQL access from SmtpInternalService.cs and
    // MailPlugin.cs entity record operations.
    const dynamoDbPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'dynamodb:GetItem',
        'dynamodb:PutItem',
        'dynamodb:UpdateItem',
        'dynamodb:DeleteItem',
        'dynamodb:Query',
        'dynamodb:Scan',
        'dynamodb:BatchGetItem',
        'dynamodb:BatchWriteItem',
      ],
      resources: [
        notificationsTable.tableArn,
        `${notificationsTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // Replaces in-process HookManager post-hook invocations for notification
    // lifecycle events (email sent, queued, webhook dispatched).
    const snsPublishPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['sns:Publish'],
      resources: [eventBus.topicArn],
    });

    // SQS permissions for email queue operations.
    // EmailHandler needs SendMessage to enqueue emails.
    // QueueProcessor needs ReceiveMessage/DeleteMessage (via event source).
    const sqsSendPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'sqs:SendMessage',
        'sqs:GetQueueAttributes',
        'sqs:GetQueueUrl',
      ],
      resources: [emailQueue.queueArn],
    });

    const sqsConsumePolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'sqs:ReceiveMessage',
        'sqs:DeleteMessage',
        'sqs:GetQueueAttributes',
        'sqs:GetQueueUrl',
        'sqs:ChangeMessageVisibility',
      ],
      resources: [emailQueue.queueArn],
    });

    // SES email sending permission for the QueueProcessor and EmailHandler.
    // Replaces SmtpInternalService.cs MailKit SmtpClient.Send() calls.
    const sesSendPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'ses:SendEmail',
        'ses:SendRawEmail',
      ],
      resources: ['*'],
    });

    // -----------------------------------------------------------------------
    // 6. Lambda Function — EmailHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Handles email send/queue HTTP operations:
    //   POST   /v1/notifications/emails          → Queue email for sending
    //   POST   /v1/notifications/emails/send     → Send email immediately
    //   GET    /v1/notifications/emails           → List email records
    //   GET    /v1/notifications/emails/{id}      → Get email details
    //   PUT    /v1/notifications/emails/{id}      → Update email status
    //   DELETE /v1/notifications/emails/{id}      → Delete email record
    //   GET    /v1/notifications/smtp-configs     → List SMTP configurations
    //   POST   /v1/notifications/smtp-configs     → Create SMTP config
    //   PUT    /v1/notifications/smtp-configs/{id} → Update SMTP config
    //
    // Source mapping:
    //   SmtpService.cs        → Public email send/queue API
    //   SmtpInternalService.cs → Email validation, queueing logic
    //   MailPlugin.cs          → Email/smtp_service entity schema definitions

    const emailHandler = new WebVellaLambdaService(this, 'EmailHandler', {
      serviceName: 'notifications',
      functionName: 'email',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/notifications/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Notifications email handler — email send/queue operations, SMTP config CRUD. ' +
        'Replaces SmtpService.cs public API and MailPlugin.cs email entity management. ' +
        'Enqueues emails to SQS for async processing by QueueProcessor.',
      environment: {
        TABLE_NAME: notificationsTable.tableName,
        QUEUE_URL: emailQueue.queueUrl,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [
        dynamoDbPolicy,
        snsPublishPolicy,
        sqsSendPolicy,
        sesSendPolicy,
      ],
    });

    // -----------------------------------------------------------------------
    // 7. Lambda Function — WebhookHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Handles webhook subscription and dispatch HTTP operations:
    //   POST   /v1/notifications/webhooks         → Register webhook
    //   GET    /v1/notifications/webhooks          → List webhooks
    //   GET    /v1/notifications/webhooks/{id}     → Get webhook details
    //   PUT    /v1/notifications/webhooks/{id}     → Update webhook
    //   DELETE /v1/notifications/webhooks/{id}     → Delete webhook
    //   POST   /v1/notifications/webhooks/{id}/test → Test webhook dispatch
    //   GET    /v1/notifications/in-app            → List in-app notifications
    //   PUT    /v1/notifications/in-app/{id}/read  → Mark as read
    //
    // Source mapping:
    //   NotificationService.cs → PostgreSQL LISTEN/NOTIFY pub/sub replacement
    //   HookManager.cs         → Post-hook event dispatch to webhook endpoints
    //
    // The WebhookHandler manages webhook subscriptions stored in DynamoDB
    // (PK=WEBHOOK#{hookId}) and dispatches HTTP callbacks when domain events
    // are received. Also manages in-app notifications (PK=NOTIFICATION#{id}).

    const webhookHandler = new WebVellaLambdaService(this, 'WebhookHandler', {
      serviceName: 'notifications',
      functionName: 'webhook',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/notifications/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Notifications webhook handler — webhook subscription CRUD, dispatch, and ' +
        'in-app notification management. Replaces PostgreSQL LISTEN/NOTIFY from ' +
        'NotificationService.cs with SNS/SQS event-driven webhook dispatch.',
      environment: {
        TABLE_NAME: notificationsTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [dynamoDbPolicy, snsPublishPolicy],
    });

    // -----------------------------------------------------------------------
    // 8. Lambda Function — QueueProcessor (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // SQS-triggered email queue processor replacing ProcessSmtpQueueJob.cs
    // scheduled polling pattern. The monolith's queue processing flow was:
    //
    //   1. SchedulePlan triggers every 10 minutes (MailPlugin.cs line 69)
    //   2. ProcessSmtpQueueJob.Execute() calls SmtpInternalService.ProcessSmtpQueue()
    //   3. SmtpInternalService acquires ProcessSmtpQueueLock (Semaphore)
    //   4. Queries queued emails: WHERE status='queued' ORDER BY created_on
    //   5. For each email: validate → construct MimeMessage → SmtpClient.Send()
    //   6. Update status to 'sent' or 'failed' with retry count increment
    //
    // In the target architecture, this becomes:
    //   1. SQS delivers batch of messages (batch size 5 for throttled sending)
    //   2. Lambda processes each message: validate → SES.SendEmail()
    //   3. Update status in DynamoDB (PK=EMAIL#{id}, SK=META)
    //   4. Failed messages: SQS visibility timeout → retry up to 3 times → DLQ
    //
    // MUST be idempotent per AAP §0.8.5:
    //   Use emailId as idempotency key — check DynamoDB status before sending.
    //   If status is already 'sent', skip processing (deduplication).

    const queueProcessor = new WebVellaLambdaService(this, 'QueueProcessor', {
      serviceName: 'notifications',
      functionName: 'queue-processor',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/notifications/src',
      handler: 'bootstrap',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 60,
      description:
        'Notifications queue processor — SQS-triggered email send processor replacing ' +
        'ProcessSmtpQueueJob.cs 10-minute polling interval. Processes queued emails via ' +
        'AWS SES with idempotency via DynamoDB status check. Batch size 5.',
      environment: {
        TABLE_NAME: notificationsTable.tableName,
        EVENT_TOPIC_ARN: eventBus.topicArn,
      },
      additionalPolicies: [
        dynamoDbPolicy,
        sqsConsumePolicy,
        sesSendPolicy,
        snsPublishPolicy,
      ],
    });

    // -----------------------------------------------------------------------
    // 9. SQS Event Source — Wire email queue to QueueProcessor Lambda
    // -----------------------------------------------------------------------
    // Replaces the monolith's SchedulePlan-based polling with event-driven
    // processing. Batch size 5 provides throttled email sending to avoid
    // SES rate limits and comply with provider quotas.
    //
    // The SqsEventSource automatically manages:
    //   - Long polling (reducing empty receives and cost)
    //   - Batch delivery (up to 5 messages per Lambda invocation)
    //   - Automatic message deletion on successful processing
    //   - Visibility timeout extension for in-progress messages
    //   - Routing to DLQ after maxReceiveCount exceeded

    queueProcessor.function.addEventSource(
      new lambdaEventSources.SqsEventSource(emailQueue, {
        batchSize: 5,
        maxBatchingWindow: cdk.Duration.seconds(10),
        reportBatchItemFailures: true,
      }),
    );

    // -----------------------------------------------------------------------
    // 10. SSM Parameters — Cross-service resource discovery
    // -----------------------------------------------------------------------
    // Per AAP §0.8.6: service configuration stored in SSM Parameter Store.
    // Other services and bootstrap scripts use these parameters to locate
    // the notification service's resources without hardcoded names.

    new ssm.StringParameter(this, 'NotificationsTableNameParam', {
      parameterName: '/webvella-erp/notifications/table-name',
      stringValue: notificationsTable.tableName,
      description:
        'DynamoDB table name for the Notifications service datastore. ' +
        'Used by bootstrap scripts and cross-service discovery.',
    });

    new ssm.StringParameter(this, 'NotificationsQueueUrlParam', {
      parameterName: '/webvella-erp/notifications/queue-url',
      stringValue: emailQueue.queueUrl,
      description:
        'SQS queue URL for the Notifications email processing queue. ' +
        'Used by other services to submit email send requests.',
    });

    // -----------------------------------------------------------------------
    // 11. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [
      emailHandler.function,
      webhookHandler.function,
      queueProcessor.function,
    ];
    this.tableName = notificationsTable.tableName;
    this.queueUrl = emailQueue.queueUrl;

    // -----------------------------------------------------------------------
    // 12. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------

    new cdk.CfnOutput(this, 'NotificationsTableName', {
      value: notificationsTable.tableName,
      description: 'DynamoDB table name for the Notifications service',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'NotificationsQueueUrl', {
      value: emailQueue.queueUrl,
      description: 'SQS queue URL for the Notifications email processing queue',
      exportName: `${this.stackName}-QueueUrl`,
    });

    new cdk.CfnOutput(this, 'EmailHandlerFunctionArn', {
      value: emailHandler.functionArn,
      description: 'ARN of the Notifications email handler Lambda function',
      exportName: `${this.stackName}-EmailHandlerArn`,
    });

    new cdk.CfnOutput(this, 'WebhookHandlerFunctionArn', {
      value: webhookHandler.functionArn,
      description: 'ARN of the Notifications webhook handler Lambda function',
      exportName: `${this.stackName}-WebhookHandlerArn`,
    });

    new cdk.CfnOutput(this, 'QueueProcessorFunctionArn', {
      value: queueProcessor.functionArn,
      description: 'ARN of the Notifications queue processor Lambda function',
      exportName: `${this.stackName}-QueueProcessorArn`,
    });

    new cdk.CfnOutput(this, 'EmailQueueDlqUrl', {
      value: emailQueueDlq.queueUrl,
      description: 'SQS dead-letter queue URL for failed email processing',
      exportName: `${this.stackName}-EmailQueueDlqUrl`,
    });

    // -----------------------------------------------------------------------
    // 13. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------

    cdk.Tags.of(this).add('service', 'notifications');
    cdk.Tags.of(this).add('domain', 'notifications');
    cdk.Tags.of(this).add(
      'environment',
      isLocalStack ? 'localstack' : 'production',
    );
  }
}
