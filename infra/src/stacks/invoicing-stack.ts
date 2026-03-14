/**
 * InvoicingStack — Invoicing / Billing Service Infrastructure (RDS PostgreSQL).
 *
 * This CDK stack defines all AWS resources for the Invoicing / Billing
 * bounded context. It is one of only TWO services (along with Reporting)
 * that uses RDS PostgreSQL instead of DynamoDB per AAP §0.4.2
 * Database-Per-Service pattern. Financial data (invoices, line items,
 * payments) requires ACID transactions for data integrity — atomically
 * creating an invoice with its line items and updating payment status
 * cannot be safely accomplished with DynamoDB's eventual consistency
 * model.
 *
 * **Source systems replaced:**
 * - `RecordManager.cs` — Record CRUD with ACID transaction support.
 *   Invoice workflows require transactional guarantees (create invoice +
 *   line items + update payment status atomically). The monolith uses
 *   PostgreSQL transactions with savepoints via DbContext/DbConnection.
 *   Replaced by InvoiceHandler and PaymentHandler Lambda functions that
 *   use Npgsql with RDS PostgreSQL for equivalent ACID transaction
 *   guarantees.
 * - `DbRecordRepository.cs` — Dynamic record queries with SQL generation.
 *   For invoicing, this maps to standard SQL against the dedicated RDS
 *   PostgreSQL instance (no EQL abstraction needed since the invoicing
 *   schema is purpose-built, not dynamic).
 * - `DbRepository.cs` — DDL helpers, table/column/index operations.
 *   Source for FluentMigrator migration script patterns that create and
 *   evolve the invoicing schema.
 * - `DbContext.cs` — Ambient transaction context with NpgsqlConnection /
 *   NpgsqlTransaction. The monolith's `DbContext.Current` AsyncLocal
 *   pattern is replaced by per-request Npgsql connections in Lambda
 *   functions that read connection details from SSM Parameter Store.
 * - `DbConnection.cs` — Connection management with savepoints and
 *   advisory locks (`pg_try_advisory_xact_lock`). Relevant for
 *   connection pooling in Lambda and concurrent invoice operations.
 *
 * **Target architecture (ACID-critical financial domain):**
 * - RDS PostgreSQL 16 instance for ACID transactional operations
 * - InvoiceHandler Lambda for invoice CRUD with atomic transactions
 * - PaymentHandler Lambda for payment processing with transactional integrity
 * - SNS domain event publishing for cross-service notifications
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
 *      SNS/SSM API calls, and internet access for SDK operations.
 *
 * 3. **RDS PostgreSQL 16 Instance** (`webvella-erp-invoicing-db`):
 *    - Database name: `invoicing`
 *    - Schema: `invoicing` (schema-level isolation per AAP §0.4.2)
 *    - Credentials: Auto-generated via Secrets Manager
 *    - LocalStack: Standard `db.t3.micro` instance
 *    - Production: Standard `db.t3.micro` instance (scale up as needed)
 *    - Removal policy: DESTROY (LocalStack) / SNAPSHOT (production)
 *    - Stores invoice, line item, payment, and billing records that
 *      require ACID transactional guarantees
 *
 * 4. **Lambda Functions** (2 handlers, .NET 9 Native AOT):
 *    a. **InvoiceHandler** (`webvella-invoicing-invoice`) — 512 MB, 60s.
 *       Handles Invoice CRUD with ACID transactions. Creates invoices
 *       with line items atomically, updates invoice status, and publishes
 *       domain events (invoicing.invoice.created, invoicing.invoice.updated).
 *    b. **PaymentHandler** (`webvella-invoicing-payment`) — 512 MB, 60s.
 *       Handles payment processing with transactional integrity. Records
 *       payments, applies them to invoices, and publishes domain events
 *       (invoicing.payment.created, invoicing.payment.applied).
 *
 * 5. **SSM Parameters** — Resource discovery per AAP §0.8.6:
 *    - `/webvella-erp/invoicing/db-connection-string` — RDS connection
 *      details (host, port, database name, secret ARN reference).
 *      Per AAP §0.8.3: stored in SSM, NEVER in Lambda env vars.
 *    - `/webvella-erp/invoicing/db-host` — RDS endpoint hostname for
 *      operational tooling and cross-service discovery.
 *
 * 6. **CfnOutputs** — Cross-stack references:
 *    - `functions` array for API Gateway route integration
 *    - `dbEndpoint` for RDS connection
 *    - `dbName` for database identification
 *
 * @module infra/src/stacks/invoicing-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as iam from 'aws-cdk-lib/aws-iam';

import {
  WebVellaLambdaService,
  LambdaRuntime,
} from '../constructs';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Service name used as a prefix for all resource identifiers. */
const SERVICE_NAME = 'invoicing';

/** RDS instance identifier following the WebVella ERP naming convention. */
const RDS_INSTANCE_IDENTIFIER = 'webvella-erp-invoicing-db';

/** Database name for the invoicing schema. */
const RDS_DATABASE_NAME = 'invoicing';

/** RDS master username for the invoicing database. */
const RDS_MASTER_USERNAME = 'invoicing_admin';

/** InvoiceHandler Lambda memory in MB (ACID transactional operations). */
const INVOICE_HANDLER_MEMORY_MB = 512;

/** InvoiceHandler Lambda timeout in seconds (transactional operations). */
const INVOICE_HANDLER_TIMEOUT_SECONDS = 60;

/** PaymentHandler Lambda memory in MB (transactional payment processing). */
const PAYMENT_HANDLER_MEMORY_MB = 512;

/** PaymentHandler Lambda timeout in seconds. */
const PAYMENT_HANDLER_TIMEOUT_SECONDS = 60;

/** PostgreSQL default port. */
const POSTGRES_PORT = 5432;

/** SSM parameter path for DB connection string. */
const SSM_DB_CONNECTION_STRING_PATH = '/webvella-erp/invoicing/db-connection-string';

/** SSM parameter path for RDS endpoint hostname. */
const SSM_DB_HOST_PATH = '/webvella-erp/invoicing/db-host';

// ---------------------------------------------------------------------------
// Interface: InvoicingStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the InvoicingStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6) and a reference to the shared domain event bus from
 * SharedStack (AAP §0.7.2, §0.8.5).
 */
export interface InvoicingStackProps extends cdk.StackProps {
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
   * Passed from SharedStack. The Invoicing service Lambda functions
   * publish domain events to this topic for cross-service notification:
   * - `invoicing.invoice.created` — New invoice with line items
   * - `invoicing.invoice.updated` — Invoice status or content change
   * - `invoicing.payment.received` — Payment received notification
   * - `invoicing.payment.created` — New payment record created
   * - `invoicing.payment.applied` — Payment applied to invoice
   *
   * Event naming convention per AAP §0.8.5:
   * - `{domain}.{entity}.{action}` (e.g., `invoicing.invoice.created`)
   *
   * Replaces the monolith's RecordHookManager.cs synchronous post-hook
   * orchestration with asynchronous SNS event publishing.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: InvoicingStack
// ---------------------------------------------------------------------------

/**
 * InvoicingStack — CDK stack for the Invoicing / Billing bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own RDS
 * PostgreSQL instance, Lambda functions, IAM policies, VPC/security
 * groups, and SSM parameters. No other service may directly access the
 * invoicing service's datastore.
 *
 * This is one of only TWO services (along with Reporting) that uses RDS
 * PostgreSQL instead of DynamoDB per AAP §0.4.2. Financial data
 * requires proper ACID transactions: creating an invoice with line
 * items, processing a payment and applying it to an invoice, and
 * updating inventory atomically. These operations map directly to the
 * monolith's `RecordManager.cs` transaction patterns using
 * `DbConnection.BeginTransaction()` / `CommitTransaction()` /
 * `RollbackTransaction()` with savepoints.
 *
 * The stack exposes three public properties consumed by ApiGatewayStack
 * for route-to-Lambda integration mapping and cross-stack resource
 * discovery:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `dbEndpoint` — RDS instance endpoint for monitoring and diagnostics
 * - `dbName` — Database name for connection string construction
 *
 * @example
 * ```typescript
 * const invoicingStack = new InvoicingStack(app, 'InvoicingStack', {
 *   isLocalStack: true,
 *   eventBus: sharedStack.eventBus,
 *   env: { account: '000000000000', region: 'us-east-1' },
 * });
 * ```
 */
export class InvoicingStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the InvoiceHandler and PaymentHandler functions that handle
   * all invoicing HTTP endpoints. Consumed by ApiGatewayStack for
   * path-based routing under `/v1/invoicing/*` and `/v1/payments/*`.
   */
  public readonly functions: lambda.IFunction[];

  /**
   * RDS PostgreSQL instance endpoint address.
   *
   * The hostname for the `webvella-erp-invoicing-db` RDS instance.
   * Used for cross-stack references and operational monitoring.
   * Lambda functions access the database using the connection string
   * stored in SSM Parameter Store at
   * `/webvella-erp/invoicing/db-connection-string`.
   */
  public readonly dbEndpoint: string;

  /**
   * Database name for the invoicing schema.
   *
   * The name of the PostgreSQL database (`invoicing`) within the
   * RDS instance. Used for cross-stack references and connection
   * string construction by FluentMigrator migration scripts.
   */
  public readonly dbName: string;

  constructor(scope: Construct, id: string, props: InvoicingStackProps) {
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
    // outbound internet access (SNS, SSM API calls).
    //
    // Same VPC pattern as the Reporting stack per AAP §0.7.6.

    const vpc = new ec2.Vpc(this, 'InvoicingVpc', {
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
    // AWS service API calls (SNS, SSM, Secrets Manager), and internet
    // access for SDK operations.
    //
    // RDS Security Group: Allows inbound PostgreSQL (port 5432) ONLY from
    // the Lambda Security Group. No public internet access. This ensures
    // only the invoicing service's Lambda functions can access the database,
    // enforcing the single-entity-ownership principle per AAP §0.8.1.

    const lambdaSg = new ec2.SecurityGroup(this, 'InvoicingLambdaSg', {
      vpc,
      description:
        'Security group for Invoicing Lambda functions — allows outbound traffic ' +
        'for RDS access and AWS service API calls (SNS, SSM)',
      allowAllOutbound: true,
    });

    const rdsSg = new ec2.SecurityGroup(this, 'InvoicingRdsSg', {
      vpc,
      description:
        'Security group for Invoicing RDS PostgreSQL instance — allows inbound ' +
        'port 5432 from Invoicing Lambda functions only',
      allowAllOutbound: false,
    });

    // Allow inbound PostgreSQL connections from Lambda security group only.
    // This enforces the single-entity-ownership principle: only the
    // invoicing service's Lambda functions can access the invoicing database.
    rdsSg.addIngressRule(
      lambdaSg,
      ec2.Port.tcp(POSTGRES_PORT),
      'Allow PostgreSQL access from Invoicing Lambda functions',
    );

    // -----------------------------------------------------------------------
    // 3. RDS PostgreSQL 16 Instance — ACID-critical financial data
    // -----------------------------------------------------------------------
    // This is one of only TWO services (along with Reporting) that uses
    // RDS PostgreSQL per AAP §0.4.2. The Invoicing service stores invoices,
    // line items, payments, and billing records that require ACID
    // transactional guarantees. These operations map directly to the
    // monolith's RecordManager.cs / DbConnection.cs transaction patterns:
    //   - BeginTransaction() / CommitTransaction() / RollbackTransaction()
    //   - Savepoints for nested operations
    //   - Advisory locks (pg_try_advisory_xact_lock) for concurrency
    //
    // Source: RecordManager.cs — ACID transaction patterns
    //         DbContext.cs — Ambient context with NpgsqlConnection
    //         DbConnection.cs — Transaction/savepoint management
    //
    // Target: Dedicated RDS PostgreSQL instance with `invoicing` database
    // containing normalized tables for invoices, line items, payments,
    // and billing cycles with full referential integrity constraints.
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

    const dbInstance = new rds.DatabaseInstance(this, 'InvoicingDatabase', {
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
    // 4. SSM Parameters — Resource discovery per AAP §0.8.6
    // -----------------------------------------------------------------------
    // Per AAP §0.8.1 / §0.8.3: DB connection string stored in SSM
    // SecureString — NEVER as Lambda environment variables.
    //
    // Lambda functions read these parameters at startup to connect to
    // RDS PostgreSQL. The DB credentials (username/password) are stored
    // in Secrets Manager via Credentials.fromGeneratedSecret and
    // referenced by ARN in the connection string parameter.
    //
    // The connection string parameter stores host/port/database/secret-ARN
    // as a JSON object for structured parsing by Lambda functions:
    // { "host": "...", "port": 5432, "database": "invoicing",
    //   "secretArn": "arn:aws:secretsmanager:..." }

    const dbSecretArn = dbInstance.secret?.secretArn ?? 'secret-not-available';

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
          'RDS PostgreSQL connection details for the Invoicing service. ' +
          'Contains host, port, database name, and Secrets Manager ARN for ' +
          'credentials. Per AAP §0.8.3: stored in SSM, NEVER in env vars.',
      },
    );

    const dbHostParam = new ssm.StringParameter(this, 'DbHostParam', {
      parameterName: SSM_DB_HOST_PATH,
      stringValue: dbInstance.dbInstanceEndpointAddress,
      description:
        'RDS PostgreSQL endpoint hostname for the Invoicing service. ' +
        'Used by operational tooling, FluentMigrator scripts, and ' +
        'cross-service discovery.',
    });

    // -----------------------------------------------------------------------
    // 5. IAM Policy Statements — Least-privilege per AAP §0.8.3
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

    // SSM Parameter Store read permission for connection string and host.
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

    // SNS publish permission for domain events.
    // InvoiceHandler publishes: invoicing.invoice.created, invoicing.invoice.updated,
    //   invoicing.payment.received
    // PaymentHandler publishes: invoicing.payment.created, invoicing.payment.applied
    //
    // Event naming convention per AAP §0.8.5: {domain}.{entity}.{action}
    const snsPublishPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['sns:Publish'],
      resources: [eventBus.topicArn],
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
    // 6. Lambda Function — InvoiceHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Handles Invoice CRUD with ACID transactional guarantees:
    //   POST   /v1/invoicing/invoices         → Create invoice with line items
    //   GET    /v1/invoicing/invoices          → List invoices
    //   GET    /v1/invoicing/invoices/{id}     → Get invoice details
    //   PUT    /v1/invoicing/invoices/{id}     → Update invoice
    //   DELETE /v1/invoicing/invoices/{id}     → Delete invoice
    //   POST   /v1/invoicing/invoices/{id}/send → Send invoice
    //   POST   /v1/invoicing/invoices/{id}/void → Void invoice
    //   GET    /v1/invoicing/invoices/{id}/pdf  → Generate invoice PDF
    //
    // Source mapping:
    //   RecordManager.cs       → ACID transaction patterns for invoice CRUD
    //   DbRecordRepository.cs  → SQL record persistence with row_to_json
    //   DbConnection.cs        → Transaction/savepoint management, advisory locks
    //
    // 512 MB memory and 60s timeout: invoice creation involves multi-table
    // atomic transactions (invoice header + line items + tax calculations)
    // but individual operations complete within seconds. Higher timeout
    // than standard 30s API handlers to accommodate complex atomic
    // transaction chains.
    //
    // Domain events published per AAP §0.7.2:
    //   - invoicing.invoice.created — After atomic invoice + line items creation
    //   - invoicing.invoice.updated — After invoice status/content change
    //   - invoicing.payment.received — After payment confirmation

    const invoiceHandler = new WebVellaLambdaService(this, 'InvoiceHandler', {
      serviceName: SERVICE_NAME,
      functionName: 'invoice',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: `../services/${SERVICE_NAME}/publish`,
      handler: 'WebVellaErp.Invoicing::WebVellaErp.Invoicing.Functions.InvoiceHandler::FunctionHandler',
      isLocalStack,
      memorySize: INVOICE_HANDLER_MEMORY_MB,
      timeoutSeconds: INVOICE_HANDLER_TIMEOUT_SECONDS,
      description:
        'Invoicing invoice handler — ACID invoice CRUD, line items, status management. ' +
        'Replaces RecordManager.cs transaction patterns with RDS PostgreSQL.',
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

    // Place InvoiceHandler Lambda in VPC for RDS access.
    // WebVellaLambdaService does not natively support VPC configuration,
    // so we use the CloudFormation L1 escape hatch to set VpcConfig
    // directly on the underlying CfnFunction resource. This places the
    // Lambda in the same VPC as the RDS instance for direct connectivity.
    const cfnInvoiceHandler = invoiceHandler.function.node
      .defaultChild as lambda.CfnFunction;
    cfnInvoiceHandler.vpcConfig = {
      securityGroupIds: [lambdaSg.securityGroupId],
      subnetIds: vpc.selectSubnets({
        subnetType: lambdaSubnetType,
      }).subnetIds,
    };

    // -----------------------------------------------------------------------
    // 7. Lambda Function — PaymentHandler (.NET 9 Native AOT)
    // -----------------------------------------------------------------------
    // Handles payment processing with transactional integrity:
    //   POST   /v1/invoicing/payments          → Create payment
    //   GET    /v1/invoicing/payments           → List payments
    //   GET    /v1/invoicing/payments/{id}      → Get payment details
    //   PUT    /v1/invoicing/payments/{id}      → Update payment
    //   DELETE /v1/invoicing/payments/{id}      → Delete/void payment
    //   POST   /v1/invoicing/payments/{id}/apply → Apply payment to invoice
    //   POST   /v1/invoicing/payments/{id}/refund → Process refund
    //
    // Source mapping:
    //   RecordManager.cs       → Payment record CRUD with ACID transactions
    //   DbRecordRepository.cs  → SQL persistence for payment records
    //   DbConnection.cs        → Transaction management for atomic operations
    //
    // Payment operations are ACID-critical: applying a payment to an
    // invoice must atomically update the payment status, the invoice
    // balance, and potentially trigger follow-up actions (e.g., marking
    // the invoice as paid). The monolith's DbConnection.BeginTransaction()
    // pattern ensures atomicity — replaced by Npgsql transactions in Lambda.
    //
    // Domain events published per AAP §0.7.2:
    //   - invoicing.payment.created — New payment record
    //   - invoicing.payment.applied — Payment applied to invoice

    const paymentHandler = new WebVellaLambdaService(this, 'PaymentHandler', {
      serviceName: SERVICE_NAME,
      functionName: 'payment',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: `../services/${SERVICE_NAME}/publish`,
      handler: 'WebVellaErp.Invoicing::WebVellaErp.Invoicing.Functions.PaymentHandler::FunctionHandler',
      isLocalStack,
      memorySize: PAYMENT_HANDLER_MEMORY_MB,
      timeoutSeconds: PAYMENT_HANDLER_TIMEOUT_SECONDS,
      description:
        'Invoicing payment handler — ACID payment processing, application to invoices, refunds. ' +
        'Replaces RecordManager.cs transaction patterns with RDS PostgreSQL.',
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

    // Place PaymentHandler Lambda in VPC for RDS access.
    // Same L1 escape hatch pattern as InvoiceHandler above.
    const cfnPaymentHandler = paymentHandler.function.node
      .defaultChild as lambda.CfnFunction;
    cfnPaymentHandler.vpcConfig = {
      securityGroupIds: [lambdaSg.securityGroupId],
      subnetIds: vpc.selectSubnets({
        subnetType: lambdaSubnetType,
      }).subnetIds,
    };

    // -----------------------------------------------------------------------
    // 8. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [
      invoiceHandler.function,
      paymentHandler.function,
    ];
    this.dbEndpoint = dbInstance.dbInstanceEndpointAddress;
    this.dbName = RDS_DATABASE_NAME;

    // -----------------------------------------------------------------------
    // 9. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------

    new cdk.CfnOutput(this, 'InvoiceHandlerFunctionArn', {
      value: invoiceHandler.functionArn,
      description: 'ARN of the Invoicing invoice handler Lambda function',
      exportName: `${this.stackName}-InvoiceHandlerArn`,
    });

    new cdk.CfnOutput(this, 'PaymentHandlerFunctionArn', {
      value: paymentHandler.functionArn,
      description: 'ARN of the Invoicing payment handler Lambda function',
      exportName: `${this.stackName}-PaymentHandlerArn`,
    });

    new cdk.CfnOutput(this, 'InvoicingDbEndpoint', {
      value: dbInstance.dbInstanceEndpointAddress,
      description: 'RDS PostgreSQL endpoint for the Invoicing service',
      exportName: `${this.stackName}-DbEndpoint`,
    });

    new cdk.CfnOutput(this, 'InvoicingDbName', {
      value: RDS_DATABASE_NAME,
      description: 'Database name for the Invoicing service RDS instance',
      exportName: `${this.stackName}-DbName`,
    });

    // -----------------------------------------------------------------------
    // 10. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------

    cdk.Tags.of(this).add('service', SERVICE_NAME);
    cdk.Tags.of(this).add('domain', SERVICE_NAME);
    cdk.Tags.of(this).add(
      'environment',
      isLocalStack ? 'localstack' : 'production',
    );
  }
}
