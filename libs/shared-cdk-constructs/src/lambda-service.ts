/**
 * @fileoverview Standard Lambda + API Gateway L3 CDK Construct
 *
 * Reusable AWS CDK L3 construct that encapsulates the standard Lambda function +
 * IAM role + CloudWatch log group pattern used by ALL 10 bounded-context
 * microservices and the custom Lambda authorizer in the WebVella ERP platform.
 *
 * This construct replaces the monolith's Startup.cs service wiring and
 * ErpMvcExtensions.cs middleware pipeline with standardized per-service
 * Lambda deployments. Authentication is handled at the API Gateway level
 * (JWT authorizer), NOT within Lambda functions.
 *
 * Key architectural decisions:
 * - Dual-target support: LocalStack (cdklocal) and production AWS (cdk deploy)
 *   via the isLocalStack context flag per AAP §0.7.6
 * - Secrets via SSM Parameter Store SecureString, NEVER environment variables
 *   per AAP §0.8.3 and §0.8.6
 * - IAM least-privilege: only permissions for resources explicitly provided
 *   per AAP §0.8.3
 * - Performance: .NET 9 Native AOT on PROVIDED_AL2023 for < 1s cold start,
 *   Node.js 22 on NODEJS_22_X for < 3s cold start per AAP §0.8.2
 * - Structured JSON logging with correlation-ID via CloudWatch log groups
 *   per AAP §0.8.5
 *
 * Consumed by all 13 CDK stacks in infra/src/stacks/.
 *
 * @module @webvella-erp/shared-cdk-constructs/lambda-service
 */

import { Construct } from 'constructs';
import * as cdk from 'aws-cdk-lib';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as ec2 from 'aws-cdk-lib/aws-ec2';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default memory allocation in MB for Lambda functions (per AAP §0.8.2) */
const DEFAULT_MEMORY_SIZE_MB = 512;

/** Default timeout in seconds for Lambda functions (per AAP §0.8.2) */
const DEFAULT_TIMEOUT_SECONDS = 30;

/**
 * Note on AWS_REGION (per AAP §0.8.6):
 * AWS_REGION is a reserved Lambda runtime environment variable set
 * automatically based on the CDK stack's deployment region (us-east-1).
 * It cannot and should not be set manually via Lambda environment variables.
 */

/** LocalStack endpoint URL for AWS SDK overrides (per AAP §0.8.6) */
const LOCALSTACK_ENDPOINT_URL = 'http://localhost:4566';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Supported Lambda runtime types for WebVella ERP services.
 *
 * - `'dotnet9-aot'`: .NET 9 Native AOT on PROVIDED_AL2023 custom runtime.
 *   Used by all 10 bounded-context microservices (identity, entity-management,
 *   crm, inventory, invoicing, reporting, notifications, file-management,
 *   workflow, plugin-system). Achieves < 1s cold start per AAP §0.8.2.
 *
 * - `'nodejs22'`: Node.js 22 runtime on NODEJS_22_X managed runtime.
 *   Used by the custom Lambda authorizer (services/authorizer/).
 *   Achieves < 3s cold start per AAP §0.8.2.
 */
export type LambdaServiceRuntime = 'dotnet9-aot' | 'nodejs22';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the LambdaServiceConstruct.
 *
 * Defines the complete set of inputs needed to provision a Lambda function
 * with its IAM execution role and CloudWatch log group. Properties follow
 * the least-privilege IAM principle: only resources explicitly provided
 * via ARN arrays receive IAM permissions.
 */
export interface LambdaServiceProps {
  /**
   * Bounded context service name used for structured logging, resource
   * naming, and tagging. Examples: 'identity', 'crm', 'entity-management'.
   */
  readonly serviceName: string;

  /**
   * Lambda function name. Should follow the naming convention:
   * `webvella-erp-{serviceName}-{function}`.
   * Example: 'webvella-erp-identity-auth'.
   */
  readonly functionName: string;

  /**
   * Optional human-readable description of the Lambda function's purpose.
   */
  readonly description?: string;

  /**
   * Runtime type selection that determines the Lambda runtime and architecture.
   * - `'dotnet9-aot'` → PROVIDED_AL2023 custom runtime for .NET 9 Native AOT
   * - `'nodejs22'` → NODEJS_22_X managed runtime for Node.js 22
   */
  readonly runtime: LambdaServiceRuntime;

  /**
   * Lambda handler entry point string.
   * - .NET format: 'AssemblyName::Namespace.ClassName::MethodName'
   *   Example: 'Identity::WebVellaErp.Identity.Functions.AuthHandler::FunctionHandler'
   * - Node.js format: 'index.handler'
   */
  readonly handler: string;

  /**
   * Path to the Lambda deployment package or code directory.
   * Used with lambda.Code.fromAsset() for packaging Lambda artifacts.
   */
  readonly codePath: string;

  /**
   * Memory allocation in MB. Higher memory also increases CPU allocation.
   * Influences warm response latency (P95 < 500ms target per AAP §0.8.2).
   * @default 512
   */
  readonly memorySize?: number;

  /**
   * Maximum function execution time in seconds.
   * @default 30
   */
  readonly timeout?: number;

  /**
   * Additional service-specific environment variables.
   *
   * CRITICAL: Per AAP §0.8.3 and §0.8.6, secrets such as DB_CONNECTION_STRING
   * and COGNITO_CLIENT_SECRET must NEVER be passed as environment variables.
   * Use ssmParameterArns for secrets accessed via SSM Parameter Store at runtime.
   */
  readonly environment?: Record<string, string>;

  /**
   * Dual-target deployment flag per AAP §0.7.6.
   * When true, configures the Lambda for LocalStack:
   * - Sets AWS_ENDPOINT_URL to http://localhost:4566
   * - Sets IS_LOCAL to 'true'
   * - Disables X-Ray tracing
   * - Uses TWO_WEEKS log retention with DESTROY removal policy
   */
  readonly isLocalStack: boolean;

  /**
   * SSM Parameter Store ARNs the function needs to read at runtime.
   * Grants ssm:GetParameter and ssm:GetParametersByPath permissions.
   * Used for secrets like DB_CONNECTION_STRING and COGNITO_CLIENT_SECRET
   * per AAP §0.8.3 and §0.8.6.
   */
  readonly ssmParameterArns?: string[];

  /**
   * DynamoDB table ARNs for read/write access.
   * Grants dynamodb:GetItem, PutItem, UpdateItem, DeleteItem, Query, Scan
   * and index access on these tables.
   */
  readonly dynamoDbTableArns?: string[];

  /**
   * SNS topic ARNs for publish permissions.
   * Grants sns:Publish for domain event publishing
   * per AAP §0.4.2 Event-Driven Architecture.
   */
  readonly snsTopicArns?: string[];

  /**
   * SQS queue ARNs for send/receive permissions.
   * Grants sqs:SendMessage, ReceiveMessage, DeleteMessage, GetQueueAttributes
   * for async message processing per AAP §0.8.6 communication patterns.
   */
  readonly sqsQueueArns?: string[];

  /**
   * S3 bucket ARNs for read/write access.
   * Grants s3:GetObject, PutObject, DeleteObject, ListBucket
   * for file management operations.
   */
  readonly s3BucketArns?: string[];

  /**
   * Additional IAM policy statements for service-specific needs.
   * Examples: Cognito admin operations (identity service),
   * RDS Data API access (invoicing/reporting), Step Functions
   * execution (workflow service).
   */
  readonly additionalPolicyStatements?: iam.PolicyStatement[];

  /**
   * Optional VPC for Lambda functions that need to access RDS PostgreSQL.
   * Only required for Invoicing and Reporting stacks that connect to
   * RDS instances on port 5432 per AAP §0.7.6 dual-target strategy.
   * When provided, AWSLambdaVPCAccessExecutionRole is added to the role.
   */
  readonly vpc?: ec2.IVpc;

  /**
   * Optional security groups for VPC-connected Lambda functions.
   * Only relevant when vpc is provided.
   */
  readonly securityGroups?: ec2.ISecurityGroup[];

  /**
   * Enable health check endpoint support in the Lambda function.
   * When true, the SERVICE_HEALTH_CHECK environment variable is set to 'enabled',
   * allowing the Lambda handler to expose a /health endpoint
   * per AAP §0.8.5.
   * @default true
   */
  readonly enableHealthCheck?: boolean;

  /**
   * Optional reserved concurrent executions to limit a function's
   * maximum concurrency. Useful for protecting downstream resources
   * (e.g., RDS connection limits for Invoicing/Reporting).
   */
  readonly reservedConcurrency?: number;
}

// ---------------------------------------------------------------------------
// Construct Implementation
// ---------------------------------------------------------------------------

/**
 * Reusable L3 CDK construct that provisions a standardized Lambda function
 * with its IAM execution role and CloudWatch log group.
 *
 * This construct encapsulates the infrastructure pattern shared across all
 * 10 bounded-context microservices and the custom Lambda authorizer. It
 * replaces the monolith's Startup.cs DI/middleware composition with
 * individual, independently deployable Lambda function configurations.
 *
 * Resources created:
 * - 1 IAM Role with least-privilege policies
 * - 1 CloudWatch Log Group with configurable retention
 * - 1 Lambda Function with appropriate runtime and configuration
 *
 * @example
 * ```typescript
 * // .NET 9 AOT service (e.g., Identity service)
 * const authLambda = new LambdaServiceConstruct(this, 'AuthHandler', {
 *   serviceName: 'identity',
 *   functionName: 'webvella-erp-identity-auth',
 *   runtime: 'dotnet9-aot',
 *   handler: 'Identity::WebVellaErp.Identity.Functions.AuthHandler::FunctionHandler',
 *   codePath: '../services/identity/src',
 *   isLocalStack: true,
 *   dynamoDbTableArns: [identityTable.tableArn],
 *   ssmParameterArns: [cognitoSecretArn],
 * });
 *
 * // Node.js 22 authorizer
 * const authorizer = new LambdaServiceConstruct(this, 'JwtAuthorizer', {
 *   serviceName: 'authorizer',
 *   functionName: 'webvella-erp-authorizer',
 *   runtime: 'nodejs22',
 *   handler: 'index.handler',
 *   codePath: '../services/authorizer/dist',
 *   isLocalStack: true,
 *   ssmParameterArns: [cognitoPoolArn],
 * });
 * ```
 */
export class LambdaServiceConstruct extends Construct {
  /**
   * The underlying Lambda function resource. Used by consuming stacks to:
   * - Wire API Gateway integrations (api-gateway-stack.ts)
   * - Create SQS event source mappings
   * - Add additional permissions post-construction
   * - Create CloudWatch alarms or metric filters
   */
  public readonly function: lambda.Function;

  /**
   * The CloudWatch log group for this Lambda function. Captures structured
   * JSON logging with correlation-ID propagation from the service's logger
   * utility (libs/shared-utils/src/logger.ts) per AAP §0.8.5.
   */
  public readonly logGroup: logs.LogGroup;

  /**
   * The IAM execution role for this Lambda function. Exposed to allow
   * consuming stacks to add additional permissions after construction,
   * such as cross-stack resource access grants.
   */
  public readonly role: iam.Role;

  constructor(scope: Construct, id: string, props: LambdaServiceProps) {
    super(scope, id);

    // -------------------------------------------------------------------
    // Phase 1: IAM Execution Role (Least-Privilege per AAP §0.8.3)
    // -------------------------------------------------------------------
    this.role = this.createExecutionRole(props);

    // -------------------------------------------------------------------
    // Phase 2: CloudWatch Log Group (Structured Logging per AAP §0.8.5)
    // -------------------------------------------------------------------
    this.logGroup = this.createLogGroup(props);

    // -------------------------------------------------------------------
    // Phase 3: Lambda Function (Performance per AAP §0.8.2)
    // -------------------------------------------------------------------
    this.function = this.createLambdaFunction(props);

    // -------------------------------------------------------------------
    // Phase 4: Resource Tagging
    // -------------------------------------------------------------------
    this.applyTags(props);
  }

  /**
   * Creates the IAM execution role with least-privilege policies.
   *
   * Base policy: AWSLambdaBasicExecutionRole (CloudWatch Logs).
   * Additional policies are conditionally added based on which
   * resource ARNs are provided in props, ensuring zero unnecessary
   * permissions per AAP §0.8.3.
   */
  private createExecutionRole(props: LambdaServiceProps): iam.Role {
    const role = new iam.Role(this, 'ExecutionRole', {
      roleName: `${props.functionName}-role`,
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
      description: `Execution role for ${props.functionName} Lambda function`,
    });

    // Base policy: CloudWatch Logs access (required for all Lambda functions)
    role.addManagedPolicy(
      iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AWSLambdaBasicExecutionRole')
    );

    // VPC access policy: Required for RDS-connected Lambdas (Invoicing/Reporting)
    if (props.vpc) {
      role.addManagedPolicy(
        iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AWSLambdaVPCAccessExecutionRole')
      );
    }

    // DynamoDB read/write access: conditional on dynamoDbTableArns
    if (props.dynamoDbTableArns && props.dynamoDbTableArns.length > 0) {
      role.addToPolicy(
        new iam.PolicyStatement({
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
            'dynamodb:ConditionCheckItem',
          ],
          resources: [
            ...props.dynamoDbTableArns,
            // Include GSI access (table/index/*) for Query operations on secondary indexes
            ...props.dynamoDbTableArns.map((arn) => `${arn}/index/*`),
          ],
        })
      );
    }

    // SNS publish access: conditional on snsTopicArns
    if (props.snsTopicArns && props.snsTopicArns.length > 0) {
      role.addToPolicy(
        new iam.PolicyStatement({
          effect: iam.Effect.ALLOW,
          actions: ['sns:Publish'],
          resources: props.snsTopicArns,
        })
      );
    }

    // SQS send/receive access: conditional on sqsQueueArns
    if (props.sqsQueueArns && props.sqsQueueArns.length > 0) {
      role.addToPolicy(
        new iam.PolicyStatement({
          effect: iam.Effect.ALLOW,
          actions: [
            'sqs:SendMessage',
            'sqs:ReceiveMessage',
            'sqs:DeleteMessage',
            'sqs:GetQueueAttributes',
            'sqs:GetQueueUrl',
            'sqs:ChangeMessageVisibility',
          ],
          resources: props.sqsQueueArns,
        })
      );
    }

    // S3 read/write access: conditional on s3BucketArns
    if (props.s3BucketArns && props.s3BucketArns.length > 0) {
      role.addToPolicy(
        new iam.PolicyStatement({
          effect: iam.Effect.ALLOW,
          actions: [
            's3:GetObject',
            's3:PutObject',
            's3:DeleteObject',
            's3:ListBucket',
            's3:GetBucketLocation',
          ],
          resources: [
            ...props.s3BucketArns,
            // Include object-level access (bucket/*)
            ...props.s3BucketArns.map((arn) => `${arn}/*`),
          ],
        })
      );
    }

    // SSM Parameter Store access: conditional on ssmParameterArns
    // Used for secrets like DB_CONNECTION_STRING, COGNITO_CLIENT_SECRET
    // per AAP §0.8.3 and §0.8.6 — secrets via SSM, NEVER env vars
    if (props.ssmParameterArns && props.ssmParameterArns.length > 0) {
      role.addToPolicy(
        new iam.PolicyStatement({
          effect: iam.Effect.ALLOW,
          actions: [
            'ssm:GetParameter',
            'ssm:GetParameters',
            'ssm:GetParametersByPath',
          ],
          resources: props.ssmParameterArns,
        })
      );
    }

    // Additional custom policy statements for service-specific needs
    // Examples: Cognito admin (identity), Step Functions (workflow)
    if (props.additionalPolicyStatements && props.additionalPolicyStatements.length > 0) {
      for (const statement of props.additionalPolicyStatements) {
        role.addToPolicy(statement);
      }
    }

    return role;
  }

  /**
   * Creates the CloudWatch log group for structured JSON logging.
   *
   * Log retention and removal policy are conditional on the deployment
   * target (LocalStack vs production) per AAP §0.7.6:
   * - LocalStack: TWO_WEEKS retention, DESTROY on stack deletion
   * - Production: THREE_MONTHS retention, RETAIN on stack deletion
   *
   * Log group captures structured JSON output from Lambda stdout,
   * including correlation-ID propagation per AAP §0.8.5.
   */
  private createLogGroup(props: LambdaServiceProps): logs.LogGroup {
    return new logs.LogGroup(this, 'LogGroup', {
      logGroupName: `/aws/lambda/${props.functionName}`,
      retention: props.isLocalStack
        ? logs.RetentionDays.TWO_WEEKS
        : logs.RetentionDays.THREE_MONTHS,
      removalPolicy: props.isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });
  }

  /**
   * Creates the Lambda function with the appropriate runtime, memory,
   * timeout, and environment configuration.
   *
   * Runtime mapping:
   * - 'dotnet9-aot' → PROVIDED_AL2023 (.NET 9 Native AOT, < 1s cold start)
   * - 'nodejs22' → NODEJS_22_X (Node.js 22, < 3s cold start)
   *
   * Environment variables always include SERVICE_NAME and AWS_REGION.
   * LocalStack-specific variables (AWS_ENDPOINT_URL, IS_LOCAL) are only
   * set when isLocalStack is true per AAP §0.8.6.
   *
   * X-Ray tracing is ACTIVE in production and DISABLED in LocalStack
   * per AAP §0.7.6 (X-Ray replaced by correlation-ID structured logging locally).
   */
  private createLambdaFunction(props: LambdaServiceProps): lambda.Function {
    // Resolve runtime from the LambdaServiceRuntime type union
    const resolvedRuntime = this.resolveRuntime(props.runtime);

    // Build environment variables map
    const environmentVariables = this.buildEnvironmentVariables(props);

    // Create the Lambda function
    const fn = new lambda.Function(this, 'Function', {
      functionName: props.functionName,
      description: props.description ?? `${props.serviceName} service Lambda function`,
      runtime: resolvedRuntime,
      handler: props.handler,
      code: lambda.Code.fromAsset(props.codePath),
      memorySize: props.memorySize ?? DEFAULT_MEMORY_SIZE_MB,
      timeout: cdk.Duration.seconds(props.timeout ?? DEFAULT_TIMEOUT_SECONDS),
      role: this.role,
      architecture: lambda.Architecture.X86_64,
      environment: environmentVariables,
      // X-Ray tracing: disabled in LocalStack per AAP §0.7.6,
      // active in production for distributed tracing
      tracing: props.isLocalStack ? lambda.Tracing.DISABLED : lambda.Tracing.ACTIVE,
      // VPC configuration: only for RDS-connected Lambdas (Invoicing/Reporting)
      ...(props.vpc && {
        vpc: props.vpc,
        ...(props.securityGroups && props.securityGroups.length > 0 && {
          securityGroups: props.securityGroups,
        }),
      }),
      // Reserved concurrency: protect downstream resources (e.g., RDS connections)
      ...(props.reservedConcurrency !== undefined && {
        reservedConcurrentExecutions: props.reservedConcurrency,
      }),
      // Associate with the pre-created log group
      logGroup: this.logGroup,
    });

    return fn;
  }

  /**
   * Resolves the LambdaServiceRuntime string to the appropriate CDK
   * Lambda runtime construct.
   *
   * - 'dotnet9-aot': PROVIDED_AL2023 custom runtime for .NET 9 Native AOT.
   *   This runtime enables < 1s cold starts (AAP §0.8.2) by running
   *   ahead-of-time compiled native binaries without the .NET CLR.
   *
   * - 'nodejs22': NODEJS_22_X managed runtime for Node.js 22 LTS.
   *   Used by the custom Lambda authorizer. Achieves < 3s cold starts
   *   (AAP §0.8.2).
   */
  private resolveRuntime(runtime: LambdaServiceRuntime): lambda.Runtime {
    switch (runtime) {
      case 'dotnet9-aot':
        return lambda.Runtime.PROVIDED_AL2023;
      case 'nodejs22':
        return lambda.Runtime.NODEJS_22_X;
      default:
        // Exhaustive check: TypeScript will error if a new runtime is added
        // to LambdaServiceRuntime without updating this switch
        const _exhaustiveCheck: never = runtime;
        throw new Error(`Unsupported Lambda runtime: ${_exhaustiveCheck}`);
    }
  }

  /**
   * Builds the complete environment variables map for the Lambda function.
   *
   * Always includes:
   * - SERVICE_NAME: bounded context name for structured logging
   * Note: AWS_REGION (us-east-1 per AAP §0.8.6) is provided automatically
   * by the Lambda runtime based on the stack's deployment region and cannot
   * be set manually as it is a reserved environment variable.
   *
   * Conditionally includes (LocalStack only, per AAP §0.8.6):
   * - AWS_ENDPOINT_URL: 'http://localhost:4566'
   * - IS_LOCAL: 'true'
   *
   * Conditionally includes (health check, per AAP §0.8.5):
   * - SERVICE_HEALTH_CHECK: 'enabled'
   *
   * Service-specific variables from props.environment are spread last,
   * allowing overrides when needed.
   *
   * CRITICAL: No secrets are ever set as environment variables per
   * AAP §0.8.3 and §0.8.6. Secrets like DB_CONNECTION_STRING and
   * COGNITO_CLIENT_SECRET must be read from SSM Parameter Store at
   * runtime by the Lambda handler.
   */
  private buildEnvironmentVariables(props: LambdaServiceProps): Record<string, string> {
    // NOTE: AWS_REGION is a reserved Lambda runtime variable and cannot be
    // set manually. It is automatically provided by the Lambda execution
    // environment based on the deployment region (us-east-1 per AAP §0.8.6).
    // The stack's env.region property controls this value.
    const env: Record<string, string> = {
      // Always-present variables
      SERVICE_NAME: props.serviceName,
    };

    // LocalStack-specific variables per AAP §0.8.6
    // Only set when targeting LocalStack to enable AWS SDK endpoint override
    if (props.isLocalStack) {
      env.AWS_ENDPOINT_URL = LOCALSTACK_ENDPOINT_URL;
      env.IS_LOCAL = 'true';
    }

    // Health check support per AAP §0.8.5
    // Default to enabled unless explicitly disabled
    const healthCheckEnabled = props.enableHealthCheck !== false;
    if (healthCheckEnabled) {
      env.SERVICE_HEALTH_CHECK = 'enabled';
    }

    // Spread service-specific environment variables last
    // This allows service stacks to pass custom variables like
    // COGNITO_USER_POOL_ID, TABLE_NAME, SNS_TOPIC_ARN, etc.
    if (props.environment) {
      Object.assign(env, props.environment);
    }

    return env;
  }

  /**
   * Applies resource tags to all construct resources for identification,
   * cost allocation, and operational management.
   *
   * Tags applied:
   * - Service: bounded context service name
   * - Environment: 'localstack' or 'production'
   * - ManagedBy: 'cdk' for IaC identification
   * - Project: 'webvella-erp' for cost allocation
   */
  private applyTags(props: LambdaServiceProps): void {
    const tags = cdk.Tags.of(this);
    tags.add('Service', props.serviceName);
    tags.add('Environment', props.isLocalStack ? 'localstack' : 'production');
    tags.add('ManagedBy', 'cdk');
    tags.add('Project', 'webvella-erp');
  }
}
