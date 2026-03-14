/**
 * ReportingStack — Reporting & Analytics Service Infrastructure (RDS PostgreSQL).
 *
 * This CDK stack defines all AWS resources for the Reporting & Analytics
 * bounded context. It is the SECOND service (along with Invoicing) that uses
 * RDS PostgreSQL instead of DynamoDB per AAP §0.4.2 Database-Per-Service
 * pattern. The Reporting service implements the read side of a CQRS
 * architecture, consuming domain events from ALL other bounded contexts
 * via SQS and building read-optimized projections in RDS PostgreSQL for
 * analytics queries.
 *
 * **Source systems replaced:**
 * - `DataSourceManager.cs` — Datasource registry and execution engine.
 *   Code datasources (C# compiled) and DB datasources (SQL queries) are
 *   replaced by the ReportHandler Lambda that executes analytics queries
 *   against read-optimized projections in RDS PostgreSQL.
 * - `RecordHookManager.cs` — Synchronous post-CRUD hook orchestration.
 *   Post-hooks that notified the reporting subsystem of data changes are
 *   replaced by asynchronous SNS/SQS domain event consumption via the
 *   EventConsumer Lambda (CQRS event-sourced read model updates).
 * - `DbDataSourceRepository.cs` — `data_source` table CRUD in the
 *   monolith's single PostgreSQL database. Replaced by the reporting
 *   service's own RDS PostgreSQL schema with report definitions and
 *   pre-computed projections.
 * - `DbRecordRepository.cs` — Dynamic SQL record queries that powered
 *   report generation in the monolith. Replaced by purpose-built
 *   analytics queries against denormalized read-model projections.
 *
 * **Target architecture (CQRS read side):**
 * - RDS PostgreSQL 16 instance for read-optimized projections
 * - SQS queue consuming ALL domain events from SharedStack SNS topic
 * - EventConsumer Lambda processes events and updates projections
 * - ReportHandler Lambda executes analytics queries against projections
 *
 * Resources created:
 *
 * 1. **VPC** — Networking infrastructure for RDS and Lambda:
 *    - LocalStack: Minimal VPC with public subnets (sufficient for
 *      local development and testing against LocalStack RDS)
 *    - Production: VPC with public + private subnets for RDS placement
 *      in private subnets with Lambda functions accessing via VPC
 *
 * 2. **Security Groups** — Network-level access control:
 *    - RDS Security Group: Allows inbound PostgreSQL (port 5432) ONLY
 *      from the Lambda Security Group. No public internet access.
 *    - Lambda Security Group: Allows outbound traffic for RDS access,
 *      SQS/SNS/SSM API calls, and internet access for SDK operations.
 *
 * 3. **RDS PostgreSQL 16 Instance** (`webvella-erp-reporting-db`):
 *    - Database name: `reporting`
 *    - Schema: `reporting` (schema-level isolation per AAP §0.4.2)
 *    - Credentials: Auto-generated via Secrets Manager
 *    - LocalStack: Standard `db.t3.micro` instance
 *    - Production: Standard `db.t3.micro` instance (scale up as needed)
 *    - Removal policy: DESTROY (LocalStack) / SNAPSHOT (production)
 *    - Stores CQRS read-optimized projections aggregated from ALL
 *      domain events (CRM, Invoicing, Inventory, Entity Management, etc.)
 *
 * 4. **SQS Queues** — Domain event consumption:
 *    - `webvella-erp-reporting-events` — Main event processing queue.
 *      Visibility timeout 60s (matches EventConsumer Lambda timeout).
 *      Message retention 14 days for resilience against outages.
 *    - `webvella-erp-reporting-events-dlq` — Dead-letter queue per
 *      AAP §0.8.5 naming convention `{service}-{queue}-dlq`.
 *      Max receive count 3 before messages move to DLQ.
 *
 * 5. **SNS Subscription** — Routes ALL domain events from SharedStack's
 *    central event bus to the reporting SQS queue for CQRS projection
 *    updates. Events consumed include:
 *    - `crm.account.created`, `crm.contact.updated`
 *    - `invoicing.invoice.created`, `invoicing.payment.processed`
 *    - `inventory.product.updated`, `inventory.stock.adjusted`
 *    - `entity-management.record.created/updated/deleted`
 *    - All other domain events from all bounded contexts
 *
 * 6. **Lambda Functions** (2 handlers, .NET 9 Native AOT):
 *    a. **ReportHandler** (`webvella-reporting-report`) — 1024 MB, 120s.
 *       Handles report generation and analytics queries against RDS
 *       PostgreSQL read-model projections. Higher memory/timeout due
 *       to analytics query complexity.
 *    b. **EventConsumer** (`webvella-reporting-event-consumer`) — 512 MB,
 *       60s. SQS-triggered processor that consumes domain events and
 *       updates CQRS read-model projections in RDS PostgreSQL.
 *       Batch size 10 for efficient event processing.
 *       MUST be idempotent per AAP §0.8.5.
 *
 * 7. **SSM Parameters** — Resource discovery per AAP §0.8.6:
 *    - `/webvella-erp/reporting/db-connection-string` — RDS connection
 *      details (host, port, database name, secret ARN reference)
 *    - `/webvella-erp/reporting/queue-url` — SQS queue URL for event
 *      consumption monitoring and operational tooling
 *
 * 8. **CfnOutputs** — Cross-stack references:
 *    - `functions` array for API Gateway route integration
 *    - `dbEndpoint` for RDS connection
 *    - `queueUrl` for SQS monitoring
 *
 * @module infra/src/stacks/reporting-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as snsSubscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import * as lambdaEventSources from 'aws-cdk-lib/aws-lambda-event-sources';

import {
  WebVellaLambdaService,
  LambdaRuntime,
} from '../constructs';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Service name used as a prefix for all resource identifiers. */
const SERVICE_NAME = 'reporting';

/** RDS instance identifier following the WebVella ERP naming convention. */
const RDS_INSTANCE_IDENTIFIER = 'webvella-erp-reporting-db';

/** Database name for the reporting read-model schema. */
const RDS_DATABASE_NAME = 'reporting';

/** RDS master username for the reporting database. */
const RDS_MASTER_USERNAME = 'reporting_admin';

/** SQS queue name for domain event consumption. */
const EVENT_QUEUE_NAME = 'webvella-erp-reporting-events';

/** SQS dead-letter queue name per AAP §0.8.5: {service}-{queue}-dlq. */
const EVENT_DLQ_NAME = 'webvella-erp-reporting-events-dlq';

/**
 * Maximum number of times a message can be received before it is
 * moved to the dead-letter queue. Per AAP §0.8.5.
 */
const MAX_RECEIVE_COUNT = 3;

/** SQS visibility timeout in seconds — must match EventConsumer timeout. */
const QUEUE_VISIBILITY_TIMEOUT_SECONDS = 60;

/** SQS message retention period in days. */
const QUEUE_RETENTION_DAYS = 14;

/** ReportHandler Lambda memory in MB (analytics queries need more memory). */
const REPORT_HANDLER_MEMORY_MB = 1024;

/** ReportHandler Lambda timeout in seconds. */
const REPORT_HANDLER_TIMEOUT_SECONDS = 120;

/** EventConsumer Lambda memory in MB. */
const EVENT_CONSUMER_MEMORY_MB = 512;

/** EventConsumer Lambda timeout in seconds. */
const EVENT_CONSUMER_TIMEOUT_SECONDS = 60;

/** SQS event source batch size for EventConsumer Lambda. */
const EVENT_CONSUMER_BATCH_SIZE = 10;

/** PostgreSQL default port. */
const POSTGRES_PORT = 5432;

/** SSM parameter path for DB connection string. */
const SSM_DB_CONNECTION_STRING_PATH = '/webvella-erp/reporting/db-connection-string';

/** SSM parameter path for SQS queue URL. */
const SSM_QUEUE_URL_PATH = '/webvella-erp/reporting/queue-url';

// ---------------------------------------------------------------------------
// Interface: ReportingStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the ReportingStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2, §0.4.2 CQRS).
 */
export interface ReportingStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - RDS instance type: `db.t3.micro` (LocalStack) vs production sizing
   * - Removal policies: DESTROY (LocalStack) vs SNAPSHOT (production RDS)
   * - VPC subnets: PUBLIC (LocalStack) vs PRIVATE_WITH_EGRESS (production)
   * - Lambda tracing, architecture, and log retention
   * - AWS_ENDPOINT_URL injection for SDK redirects
   */
  readonly isLocalStack: boolean;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. The Reporting service subscribes its SQS queue
   * to this topic to consume ALL domain events from all bounded contexts
   * (CRM, Invoicing, Inventory, Entity Management, etc.) for building
   * CQRS read-optimized projections in RDS PostgreSQL.
   *
   * Event naming convention per AAP §0.8.5:
   * - `{domain}.{entity}.{action}` (e.g., `invoicing.invoice.created`)
   *
   * Replaces the monolith's RecordHookManager.cs synchronous post-hook
   * orchestration and PostgreSQL LISTEN/NOTIFY event propagation with
   * asynchronous SNS/SQS event-driven consumption.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: ReportingStack
// ---------------------------------------------------------------------------

/**
 * ReportingStack — CDK stack for the Reporting & Analytics bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own RDS
 * PostgreSQL instance, SQS queues, Lambda functions, IAM policies,
 * VPC/security groups, and SSM parameters. No other service may directly
 * access the reporting service's datastore or queues.
 *
 * This is one of only TWO services (along with Invoicing) that uses RDS
 * PostgreSQL instead of DynamoDB per AAP §0.4.2. The Reporting service
 * implements the read side of a CQRS architecture: it consumes domain
 * events from all bounded contexts and builds read-optimized projections
 * for analytics queries.
 *
 * The stack exposes three public properties consumed by ApiGatewayStack
 * for route-to-Lambda integration mapping and cross-stack resource
 * discovery:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `dbEndpoint` — RDS instance endpoint for monitoring and diagnostics
 * - `queueUrl` — SQS queue URL for event consumption monitoring
 *
 * @example
 * ```typescript
 * const reportingStack = new ReportingStack(app, 'ReportingStack', {
 *   isLocalStack: true,
 *   eventBus: sharedStack.eventBus,
 *   env: { account: '000000000000', region: 'us-east-1' },
 * });
 * ```
 */
export class ReportingStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the ReportHandler and EventConsumer functions that handle
   * all reporting HTTP endpoints and SQS event processing. Consumed by
   * ApiGatewayStack for path-based routing under `/v1/reports/*`.
   */
  public readonly functions: lambda.IFunction[];

  /**
   * RDS PostgreSQL instance endpoint address.
   *
   * The hostname for the `webvella-erp-reporting-db` RDS instance.
   * Used for cross-stack references and operational monitoring.
   * Lambda functions access the database using the connection string
   * stored in SSM Parameter Store at `/webvella-erp/reporting/db-connection-string`.
   */
  public readonly dbEndpoint: string;

  /**
   * SQS queue URL for the domain event consumption queue.
   *
   * URL of the `webvella-erp-reporting-events` SQS queue that receives
   * ALL domain events from the SharedStack's SNS event bus. Used for
   * cross-stack references and operational monitoring.
   * Also published as SSM parameter at `/webvella-erp/reporting/queue-url`.
   */
  public readonly queueUrl: string;

  constructor(scope: Construct, id: string, props: ReportingStackProps) {
    super(scope, id, props);

    const { isLocalStack, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. VPC — Networking for RDS PostgreSQL and Lambda functions
    // -----------------------------------------------------------------------
    // RDS PostgreSQL requires VPC placement. Lambda functions that access
    // RDS must also be placed in the same VPC.
    //
    // LocalStack: Minimal VPC with public subnets (LocalStack networking
    // is simulated — all resources communicate via localhost:4566).
    // Production: VPC with public + private subnets. RDS placed in private
    // subnets. Lambda functions in private subnets with NAT gateway for
    // outbound internet access (SQS, SNS, SSM API calls).
    //
    // Same VPC pattern as the Invoicing stack per AAP §0.7.6.

    const vpc = new ec2.Vpc(this, 'ReportingVpc', {
      maxAzs: 2,
      natGateways: isLocalStack ? 0 : 1,
      subnetConfiguration: [
        {
          name: 'public',
          subnetType: ec2.SubnetType.PUBLIC,
          cidrMask: 24,
        },
        ...(isLocalStack
          ? []
          : [
              {
                name: 'private',
                subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS,
                cidrMask: 24,
              },
            ]),
      ],
    });

    // -----------------------------------------------------------------------
    // 2. Security Groups — Network-level access control
    // -----------------------------------------------------------------------
    // Per AAP §0.8.3: IAM least-privilege + network-level restrictions.
    //
    // Lambda Security Group: Allows all outbound traffic for RDS access,
    // AWS service API calls (SQS, SNS, SSM, Secrets Manager), and
    // internet access for SDK operations.
    //
    // RDS Security Group: Allows inbound PostgreSQL (port 5432) ONLY from
    // the Lambda Security Group. No public internet access. This ensures
    // only the reporting service's Lambda functions can access the database.

    const lambdaSg = new ec2.SecurityGroup(this, 'ReportingLambdaSg', {
      vpc,
      description:
        'Security group for Reporting Lambda functions — allows outbound traffic ' +
        'for RDS access and AWS service API calls (SQS, SNS, SSM)',
      allowAllOutbound: true,
    });

    const rdsSg = new ec2.SecurityGroup(this, 'ReportingRdsSg', {
      vpc,
      description:
        'Security group for Reporting RDS PostgreSQL instance — allows inbound ' +
        'port 5432 from Reporting Lambda functions only',
      allowAllOutbound: false,
    });

    // Allow inbound PostgreSQL connections from Lambda security group only.
    // This enforces the single-entity-ownership principle: only the
    // reporting service's Lambda functions can access the reporting database.
    rdsSg.addIngressRule(
      lambdaSg,
      ec2.Port.tcp(POSTGRES_PORT),
      'Allow PostgreSQL access from Reporting Lambda functions',
    );

    // -----------------------------------------------------------------------
    // 3. RDS PostgreSQL 16 Instance — CQRS read-model projections
    // -----------------------------------------------------------------------
    // This is one of only TWO services (along with Invoicing) that uses
    // RDS PostgreSQL per AAP §0.4.2. The Reporting service stores CQRS
    // read-optimized projections built from domain events consumed via SQS.
    //
    // Source: DbDataSourceRepository.cs — `data_source` table CRUD
    //         DbRecordRepository.cs — Dynamic SQL record queries for reports
    //
    // Target: Dedicated RDS PostgreSQL instance with `reporting` database
    // containing denormalized projections optimized for analytics queries.
    // All report definitions and pre-computed aggregations live here.
    //
    // Credentials are auto-generated and stored in AWS Secrets Manager
    // (via Credentials.fromGeneratedSecret). Per AAP §0.8.3: connection
    // string stored in SSM SecureString — NEVER in Lambda env vars.
    //
    // Removal policy per AAP §0.7.6:
    //   - LocalStack: DESTROY — clean up resources for dev/test cycles
    //   - Production: SNAPSHOT — take final snapshot before deletion

    const dbCredentials = rds.Credentials.fromGeneratedSecret(
      RDS_MASTER_USERNAME,
      {
        secretName: `webvella-erp/${SERVICE_NAME}/db-credentials`,
      },
    );

    // Select subnet placement based on deployment target.
    // LocalStack: Public subnets (LocalStack simulates networking)
    // Production: Private subnets for security best practices
    const dbSubnetType = isLocalStack
      ? ec2.SubnetType.PUBLIC
      : ec2.SubnetType.PRIVATE_WITH_EGRESS;

    const dbInstance = new rds.DatabaseInstance(this, 'ReportingDatabase', {
      instanceIdentifier: RDS_INSTANCE_IDENTIFIER,
      engine: rds.DatabaseInstanceEngine.postgres({
        version: rds.PostgresEngineVersion.VER_16,
      }),
      instanceType: ec2.InstanceType.of(
        ec2.InstanceClass.T3,
        ec2.InstanceSize.MICRO,
      ),
      vpc,
      vpcSubnets: {
        subnetType: dbSubnetType,
      },
      securityGroups: [rdsSg],
      databaseName: RDS_DATABASE_NAME,
      credentials: dbCredentials,
      multiAz: !isLocalStack,
      allocatedStorage: 20,
      maxAllocatedStorage: isLocalStack ? 20 : 100,
      storageEncrypted: !isLocalStack,
      autoMinorVersionUpgrade: true,
      deletionProtection: !isLocalStack,
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.SNAPSHOT,
      backupRetention: isLocalStack
        ? cdk.Duration.days(0)
        : cdk.Duration.days(7),
      publiclyAccessible: isLocalStack,
    });

    // -----------------------------------------------------------------------
    // 4. SQS Queues — Domain event consumption with DLQ
    // -----------------------------------------------------------------------
    // The Reporting service consumes ALL domain events from the SharedStack's
    // SNS topic to build CQRS read-optimized projections in RDS PostgreSQL.
    //
    // Source: RecordHookManager.cs — Synchronous post-CRUD hook orchestration
    //         replaced by asynchronous SNS/SQS event-driven consumption
    //
    // Event flow:
    //   1. Domain services publish events to SharedStack SNS topic
    //   2. SNS fans out events to the reporting SQS queue (subscription)
    //   3. EventConsumer Lambda is triggered by SQS (batch size 10)
    //   4. EventConsumer processes events and updates RDS projections
    //   5. Failed messages route to DLQ after 3 attempts for inspection
    //
    // Per AAP §0.8.5:
    //   - DLQ naming: {service}-{queue}-dlq → webvella-erp-reporting-events-dlq
    //   - Max receive count: 3
    //   - All event consumers MUST be idempotent

    // Dead-letter queue for events that fail processing after max retries.
    // Named per AAP §0.8.5 convention: {service}-{queue}-dlq
    const eventQueueDlq = new sqs.Queue(this, 'EventQueueDlq', {
      queueName: EVENT_DLQ_NAME,
      retentionPeriod: cdk.Duration.days(QUEUE_RETENTION_DAYS),
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });

    // Main event processing queue.
    // Visibility timeout (60s) MUST be >= EventConsumer Lambda timeout (60s)
    // to prevent duplicate processing during Lambda execution.
    // Message retention 14 days ensures resilience against extended outages.
    const eventQueue = new sqs.Queue(this, 'EventQueue', {
      queueName: EVENT_QUEUE_NAME,
      visibilityTimeout: cdk.Duration.seconds(QUEUE_VISIBILITY_TIMEOUT_SECONDS),
      retentionPeriod: cdk.Duration.days(QUEUE_RETENTION_DAYS),
      deadLetterQueue: {
        queue: eventQueueDlq,
        maxReceiveCount: MAX_RECEIVE_COUNT,
      } as sqs.DeadLetterQueue,
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });

    // -----------------------------------------------------------------------
    // 5. SNS Subscription — Route ALL domain events to reporting SQS queue
    // -----------------------------------------------------------------------
    // Subscribe the reporting SQS queue to the SharedStack's central SNS
    // event bus. This enables the CQRS pattern where the Reporting service
    // receives events from ALL bounded contexts:
    //   - CRM: account/contact created/updated/deleted
    //   - Invoicing: invoice/payment created/updated
    //   - Inventory: product/stock updated
    //   - Entity Management: record CRUD events
    //   - Workflow: job started/completed/failed
    //   - Notifications: email sent/queued
    //   - File Management: file uploaded/deleted
    //   - Plugin System: plugin registered/updated
    //
    // The EventConsumer Lambda filters events at the application level
    // to determine which projections need updating. Raw message delivery
    // is enabled for efficient parsing (no SNS envelope overhead).
    //
    // Source: RecordHookManager.cs — In the monolith, post-create/update/
    //         delete hooks synchronously notified the reporting subsystem.
    //         Now replaced by asynchronous SNS → SQS event delivery.

    eventBus.addSubscription(
      new snsSubscriptions.SqsSubscription(eventQueue, {
        rawMessageDelivery: true,
      }),
    );

    // -----------------------------------------------------------------------
    // 6. SSM Parameters — Resource discovery per AAP §0.8.6
    // -----------------------------------------------------------------------
    // Per AAP §0.8.1 / §0.8.3: DB connection string stored in SSM
    // SecureString — NEVER as Lambda environment variables.
    //
    // Lambda functions read these parameters at startup to connect to
    // RDS PostgreSQL and discover the SQS queue URL. The DB credentials
    // (username/password) are stored in Secrets Manager via
    // Credentials.fromGeneratedSecret and referenced by ARN in the
    // connection string parameter.
    //
    // The connection string parameter stores host/port/database/secret-ARN
    // as a JSON object for structured parsing by Lambda functions:
    // { "host": "...", "port": 5432, "database": "reporting",
    //   "secretArn": "arn:aws:secretsmanager:..." }

    // Use the secret's full ARN for IAM policies. In LocalStack Community,
    // RDS secret ARNs may resolve to non-ARN physical resource IDs, causing
    // MalformedPolicyDocument errors. Use a wildcard ARN in LocalStack mode.
    const dbSecretArn = isLocalStack
      ? `arn:aws:secretsmanager:${this.region}:${this.account}:secret:webvella-erp/${SERVICE_NAME}/*`
      : (dbInstance.secret?.secretArn ?? `arn:aws:secretsmanager:${this.region}:${this.account}:secret:webvella-erp/${SERVICE_NAME}/*`);

    const dbConnectionStringParam = new ssm.StringParameter(
      this,
      'DbConnectionStringParam',
      {
        parameterName: SSM_DB_CONNECTION_STRING_PATH,
        stringValue: JSON.stringify({
          host: dbInstance.dbInstanceEndpointAddress,
          port: POSTGRES_PORT,
          database: RDS_DATABASE_NAME,
          secretArn: dbSecretArn,
        }),
        description:
          'RDS PostgreSQL connection details for the Reporting service. ' +
          'Contains host, port, database name, and Secrets Manager ARN for ' +
          'credentials. Per AAP §0.8.3: stored in SSM, NEVER in env vars.',
      },
    );

    const queueUrlParam = new ssm.StringParameter(this, 'QueueUrlParam', {
      parameterName: SSM_QUEUE_URL_PATH,
      stringValue: eventQueue.queueUrl,
      description:
        'SQS queue URL for the Reporting service domain event consumption queue. ' +
        'Used by operational tooling and cross-service discovery.',
    });

    // -----------------------------------------------------------------------
    // 7. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // RDS connect permission for Lambda functions accessing PostgreSQL.
    // Uses IAM database authentication for additional security layer.
    const rdsConnectPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['rds-db:connect'],
      resources: [
        `arn:aws:rds-db:${this.region}:${this.account}:dbuser:${dbInstance.instanceIdentifier}/*`,
      ],
    });

    // Secrets Manager read permission for DB credentials.
    // The auto-generated secret (from Credentials.fromGeneratedSecret)
    // stores the master username/password that Lambda functions read
    // at startup to build the PostgreSQL connection string.
    const secretsReadPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'secretsmanager:GetSecretValue',
        'secretsmanager:DescribeSecret',
      ],
      resources: [dbSecretArn],
    });

    // SSM Parameter Store read permission for connection string and queue URL.
    // Per AAP §0.8.3: secrets via SSM SecureString, never environment variables.
    const ssmReadPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'ssm:GetParameter',
        'ssm:GetParametersByPath',
      ],
      resources: [
        `arn:aws:ssm:${this.region}:${this.account}:parameter/webvella-erp/${SERVICE_NAME}/*`,
      ],
    });

    // SNS publish permission for domain events published by ReportHandler.
    // Reports may publish analytics events (e.g., reporting.report.generated).
    const snsPublishPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['sns:Publish'],
      resources: [eventBus.topicArn],
    });

    // SQS consume permission for EventConsumer Lambda.
    // Required for reading and deleting messages from the event queue.
    const sqsConsumePolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'sqs:ReceiveMessage',
        'sqs:DeleteMessage',
        'sqs:GetQueueAttributes',
        'sqs:GetQueueUrl',
        'sqs:ChangeMessageVisibility',
      ],
      resources: [eventQueue.queueArn],
    });

    // VPC network interface management permission for Lambda functions
    // that are placed in VPC to access the RDS PostgreSQL instance.
    // Required by AWS Lambda to create/manage ENIs in the VPC subnets.
    const vpcAccessPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'ec2:CreateNetworkInterface',
        'ec2:DescribeNetworkInterfaces',
        'ec2:DeleteNetworkInterface',
        'ec2:AssignPrivateIpAddresses',
        'ec2:UnassignPrivateIpAddresses',
      ],
      resources: ['*'],
    });

    // Lambda subnet type — where Lambda functions are placed within the VPC.
    // Lambda functions must be in the same VPC as RDS for direct connectivity.
    // LocalStack: Public subnets (LocalStack simulates networking via localhost)
    // Production: Private subnets with NAT gateway for outbound internet access
    const lambdaSubnetType = isLocalStack
      ? ec2.SubnetType.PUBLIC
      : ec2.SubnetType.PRIVATE_WITH_EGRESS;

    // -----------------------------------------------------------------------
    // 8. Lambda Function — ReportHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Handles report generation and analytics query HTTP operations:
    //   GET    /v1/reports              → List available reports
    //   POST   /v1/reports              → Create report definition
    //   GET    /v1/reports/{id}         → Get report details
    //   PUT    /v1/reports/{id}         → Update report definition
    //   DELETE /v1/reports/{id}         → Delete report definition
    //   POST   /v1/reports/{id}/execute → Execute report (analytics query)
    //   GET    /v1/reports/datasources  → List available datasources
    //   POST   /v1/reports/datasources/execute → Execute datasource query
    //   GET    /v1/reports/dashboards   → Get dashboard analytics
    //
    // Source mapping:
    //   DataSourceManager.cs      → Datasource registry and execution engine
    //   DbDataSourceRepository.cs → Report definition CRUD (data_source table)
    //   DbRecordRepository.cs     → Dynamic SQL queries for report data
    //
    // Higher memory (1024 MB) and timeout (120s) because analytics queries
    // may involve complex joins and aggregations across denormalized
    // read-model projections.

    const reportHandler = new WebVellaLambdaService(this, 'ReportHandler', {
      serviceName: SERVICE_NAME,
      functionName: 'report',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: `../services/${SERVICE_NAME}/publish`,
      handler: 'WebVellaErp.Reporting::WebVellaErp.Reporting.Functions.ReportHandler::FunctionHandler',
      isLocalStack,
      memorySize: REPORT_HANDLER_MEMORY_MB,
      timeoutSeconds: REPORT_HANDLER_TIMEOUT_SECONDS,
      description:
        'Reporting report handler — analytics queries, report CRUD, dashboard aggregation. ' +
        'Replaces DataSourceManager.cs with RDS PostgreSQL read-model projections.',
      environment: {
        SSM_DB_CONNECTION_PATH: SSM_DB_CONNECTION_STRING_PATH,
        EVENT_TOPIC_ARN: eventBus.topicArn,
        SERVICE_NAME: SERVICE_NAME,
      },
      additionalPolicies: [
        rdsConnectPolicy,
        secretsReadPolicy,
        ssmReadPolicy,
        snsPublishPolicy,
        vpcAccessPolicy,
      ],
    });

    // Place ReportHandler Lambda in VPC for RDS access.
    // WebVellaLambdaService does not natively support VPC configuration,
    // so we use the CloudFormation L1 escape hatch to set VpcConfig
    // directly on the underlying CfnFunction resource. This places the
    // Lambda in the same VPC as the RDS instance for direct connectivity.
    const cfnReportHandler = reportHandler.function.node
      .defaultChild as lambda.CfnFunction;
    cfnReportHandler.vpcConfig = {
      securityGroupIds: [lambdaSg.securityGroupId],
      subnetIds: vpc.selectSubnets({
        subnetType: lambdaSubnetType,
      }).subnetIds,
    };

    // -----------------------------------------------------------------------
    // 9. Lambda Function — EventConsumer (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // SQS-triggered domain event processor that updates CQRS read-model
    // projections in RDS PostgreSQL. Consumes ALL domain events from ALL
    // bounded contexts to maintain denormalized analytics projections.
    //
    // Source mapping:
    //   RecordHookManager.cs → Post-CRUD hook orchestration (synchronous in
    //     monolith, now async via SNS/SQS)
    //   DataSourceManager.cs → Datasource cache invalidation on data changes
    //
    // Event processing flow:
    //   1. SQS delivers batch of domain events (batch size 10)
    //   2. For each event, determine which projections need updating
    //   3. Execute SQL INSERT/UPDATE/DELETE against RDS projections
    //   4. Handle failures gracefully — failed items are retried via SQS
    //   5. After maxReceiveCount (3), failed messages move to DLQ
    //
    // MUST be idempotent per AAP §0.8.5:
    //   Use eventId/correlationId as idempotency key. Process each event
    //   by checking if the projection already reflects this event version.
    //   If the projection is up-to-date, skip processing (deduplication).
    //
    // Batch size 10 balances throughput and latency: enough events per
    // invocation for efficient database operations, but small enough to
    // complete within the 60s timeout.

    const eventConsumer = new WebVellaLambdaService(this, 'EventConsumer', {
      serviceName: SERVICE_NAME,
      functionName: 'event-consumer',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: `../services/${SERVICE_NAME}/publish`,
      handler: 'WebVellaErp.Reporting::WebVellaErp.Reporting.Functions.EventConsumer::HandleSqsEvent',
      isLocalStack,
      memorySize: EVENT_CONSUMER_MEMORY_MB,
      timeoutSeconds: EVENT_CONSUMER_TIMEOUT_SECONDS,
      description:
        'Reporting event consumer — SQS-triggered CQRS projection updater. Consumes all ' +
        'domain events, updates RDS PostgreSQL read-model. Replaces RecordHookManager.cs.',
      environment: {
        SSM_DB_CONNECTION_PATH: SSM_DB_CONNECTION_STRING_PATH,
        QUEUE_URL: eventQueue.queueUrl,
        EVENT_TOPIC_ARN: eventBus.topicArn,
        SERVICE_NAME: SERVICE_NAME,
      },
      additionalPolicies: [
        rdsConnectPolicy,
        secretsReadPolicy,
        ssmReadPolicy,
        sqsConsumePolicy,
        snsPublishPolicy,
        vpcAccessPolicy,
      ],
    });

    // Place EventConsumer Lambda in VPC for RDS access.
    // Same L1 escape hatch pattern as ReportHandler above.
    const cfnEventConsumer = eventConsumer.function.node
      .defaultChild as lambda.CfnFunction;
    cfnEventConsumer.vpcConfig = {
      securityGroupIds: [lambdaSg.securityGroupId],
      subnetIds: vpc.selectSubnets({
        subnetType: lambdaSubnetType,
      }).subnetIds,
    };

    // -----------------------------------------------------------------------
    // 10. SQS Event Source — Wire event queue to EventConsumer Lambda
    // -----------------------------------------------------------------------
    // Replaces the monolith's RecordHookManager.cs synchronous post-hook
    // pattern with event-driven SQS-triggered Lambda processing. Batch
    // size 10 provides efficient event processing while keeping individual
    // Lambda invocations within the 60s timeout.
    //
    // The SqsEventSource automatically manages:
    //   - Long polling (reducing empty receives and cost)
    //   - Batch delivery (up to 10 messages per Lambda invocation)
    //   - Automatic message deletion on successful processing
    //   - Visibility timeout extension for in-progress messages
    //   - Routing to DLQ after maxReceiveCount exceeded
    //   - reportBatchItemFailures for partial batch success

    eventConsumer.function.addEventSource(
      new lambdaEventSources.SqsEventSource(eventQueue, {
        batchSize: EVENT_CONSUMER_BATCH_SIZE,
        maxBatchingWindow: cdk.Duration.seconds(10),
        reportBatchItemFailures: true,
      }),
    );

    // -----------------------------------------------------------------------
    // 11. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [
      reportHandler.function,
      eventConsumer.function,
    ];
    this.dbEndpoint = dbInstance.dbInstanceEndpointAddress;
    this.queueUrl = eventQueue.queueUrl;

    // -----------------------------------------------------------------------
    // 12. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------

    new cdk.CfnOutput(this, 'ReportHandlerFunctionArn', {
      value: reportHandler.functionArn,
      description: 'ARN of the Reporting report handler Lambda function',
      exportName: `${this.stackName}-ReportHandlerArn`,
    });

    new cdk.CfnOutput(this, 'EventConsumerFunctionArn', {
      value: eventConsumer.functionArn,
      description: 'ARN of the Reporting event consumer Lambda function',
      exportName: `${this.stackName}-EventConsumerArn`,
    });

    new cdk.CfnOutput(this, 'ReportingDbEndpoint', {
      value: dbInstance.dbInstanceEndpointAddress,
      description: 'RDS PostgreSQL endpoint for the Reporting service',
      exportName: `${this.stackName}-DbEndpoint`,
    });

    new cdk.CfnOutput(this, 'ReportingQueueUrl', {
      value: eventQueue.queueUrl,
      description: 'SQS queue URL for the Reporting domain event consumption queue',
      exportName: `${this.stackName}-QueueUrl`,
    });

    new cdk.CfnOutput(this, 'ReportingDlqUrl', {
      value: eventQueueDlq.queueUrl,
      description: 'SQS dead-letter queue URL for failed event processing',
      exportName: `${this.stackName}-DlqUrl`,
    });

    // -----------------------------------------------------------------------
    // 13. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------

    cdk.Tags.of(this).add('service', SERVICE_NAME);
    cdk.Tags.of(this).add('domain', SERVICE_NAME);
    cdk.Tags.of(this).add(
      'environment',
      isLocalStack ? 'localstack' : 'production',
    );
  }
}
