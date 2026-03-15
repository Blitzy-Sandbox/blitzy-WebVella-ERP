/**
 * @file infra/src/constructs/lambda-service.ts
 * @description Standard Lambda Function CDK L3 Construct for WebVella ERP Serverless Platform
 *
 * This is the MOST FOUNDATIONAL construct in the infrastructure layer. It standardizes
 * Lambda function creation across ALL 10 bounded-context services (.NET 9 Native AOT)
 * plus the custom JWT authorizer (Node.js 22).
 *
 * Encapsulates:
 * - .NET 9 Native AOT runtime configuration (PROVIDED_AL2023 custom runtime)
 * - Node.js 22 runtime configuration (NODEJS_22_X managed runtime)
 * - Common environment variables (IS_LOCAL, SERVICE_NAME, POWERTOOLS_SERVICE_NAME, LOG_LEVEL)
 * - LocalStack-aware AWS_ENDPOINT_URL injection
 * - Cognito User Pool ID injection for JWT-based auth
 * - IAM least-privilege execution role (CloudWatch Logs + SSM read)
 * - Structured JSON logging via CloudWatch Log Groups with retention policies
 * - Memory sizing, timeout, concurrency, and architecture defaults
 * - Resource tagging for cost allocation and operational visibility
 *
 * Source context (monolith references):
 * - WebVella.Erp.Site/Startup.cs — DI composition and auth setup → per-Lambda env vars
 * - WebVella.Erp.Site/Config.json — Connection strings and JWT config → SSM parameters
 * - WebVella.Erp/Api/SecurityContext.cs — AsyncLocal user scope → JWT claims in Lambda event
 * - WebVella.Erp.Web/Services/AuthService.cs — Cookie+JWT auth → Cognito
 * - WebVella.Erp.Web/Middleware/JwtMiddleware.cs — Bearer extraction → API Gateway JWT authorizer
 * - WebVella.Erp/Jobs/JobManager.cs — 20-thread executor → SQS-triggered Lambdas
 *
 * AAP compliance:
 * - §0.8.2: Performance — Native AOT < 1s cold start, Node.js < 3s, P95 < 500ms
 * - §0.8.3: Security — IAM least-privilege, secrets via SSM only (never env vars)
 * - §0.8.5: Operations — Structured JSON logging, correlation-ID propagation
 * - §0.8.6: Environment — AWS_ENDPOINT_URL for LocalStack, IS_LOCAL flag
 * - §0.7.6: Dual-target — isLocalStack flag controls X-Ray, architecture, removal policy
 * - §0.8.1: Bounded contexts — each Lambda belongs to exactly one service
 */

import * as cdk from 'aws-cdk-lib';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';

// ---------------------------------------------------------------------------
// Enum: LambdaRuntime
// ---------------------------------------------------------------------------

/**
 * Supported Lambda runtime selections for WebVella ERP services.
 *
 * Only two runtimes are permitted per AAP §0.8.1:
 * - .NET 9 Native AOT (all 10 bounded-context services)
 * - Node.js 22 (custom Lambda JWT authorizer)
 */
export enum LambdaRuntime {
  /** .NET 9 Native AOT — uses PROVIDED_AL2023 custom runtime with 'bootstrap' handler */
  DOTNET_9_AOT = 'dotnet9-aot',
  /** Node.js 22 — uses NODEJS_22_X managed runtime */
  NODEJS_22 = 'nodejs22',
}

// ---------------------------------------------------------------------------
// Interface: WebVellaLambdaServiceProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the WebVellaLambdaService construct.
 *
 * Required props define the service identity, runtime, and deployment package.
 * Optional props allow overriding defaults for memory, timeout, concurrency,
 * environment variables, IAM policies, architecture, and tracing.
 */
export interface WebVellaLambdaServiceProps {
  /**
   * Service domain name identifying the bounded context.
   * Used in function naming, IAM scoping, and log group paths.
   * Examples: 'identity', 'crm', 'entity-management', 'invoicing'
   */
  readonly serviceName: string;

  /**
   * Lambda function name suffix appended to the service name.
   * Full function name: webvella-{serviceName}-{functionName}
   * Examples: 'auth-handler', 'user-handler', 'queue-processor'
   */
  readonly functionName: string;

  /**
   * Runtime selection controlling CDK runtime, handler defaults, and memory defaults.
   * - DOTNET_9_AOT: PROVIDED_AL2023, handler='bootstrap', memory=512MB
   * - NODEJS_22: NODEJS_22_X, handler from props, memory=256MB
   */
  readonly runtime: LambdaRuntime;

  /**
   * Path to the Lambda deployment package (code directory or zip).
   * Resolved via lambda.Code.fromAsset().
   * Must keep unzipped package under 250MB per AAP §0.8.2.
   */
  readonly codePath: string;

  /**
   * Lambda handler string.
   * - For .NET 9 Native AOT: typically 'bootstrap' (compiled binary entrypoint)
   * - For Node.js 22: e.g. 'index.handler' (module.export)
   */
  readonly handler: string;

  /**
   * Memory size in MB allocated to the Lambda function.
   * Defaults: 512 MB for .NET 9 AOT (for < 1s cold start), 256 MB for Node.js.
   * Higher memory also allocates proportionally more CPU.
   */
  readonly memorySize?: number;

  /**
   * Timeout in seconds for the Lambda function execution.
   * Default: 30 seconds (suitable for API handlers).
   * Queue processors should use 300 seconds.
   */
  readonly timeoutSeconds?: number;

  /**
   * Reserved concurrent executions for this Lambda function.
   * Controls maximum scale to prevent downstream saturation.
   * When undefined, Lambda uses unreserved account concurrency.
   */
  readonly reservedConcurrency?: number;

  /**
   * Additional environment variables merged with the standard set.
   * Props values override standard defaults for same-named keys.
   *
   * SECURITY: Never include DB_CONNECTION_STRING, COGNITO_CLIENT_SECRET,
   * or any secret value here. Use SSM Parameter Store SecureString instead
   * per AAP §0.8.3.
   */
  readonly environment?: { [key: string]: string };

  /**
   * Whether the deployment targets LocalStack (true) or production AWS (false).
   * Controls: AWS_ENDPOINT_URL injection, X-Ray tracing, architecture selection,
   * log retention, and removal policies.
   */
  readonly isLocalStack: boolean;

  /**
   * Cognito User Pool ID passed to the Lambda as an environment variable.
   * Sourced from SharedStack output. Used by services that need to validate
   * or interact with Cognito (e.g., identity service, authorizer).
   */
  readonly cognitoUserPoolId?: string;

  /**
   * Additional IAM policy statements appended to the Lambda execution role.
   * Use for service-specific permissions (DynamoDB, S3, SQS, SNS, etc.).
   * The base role already includes CloudWatch Logs and SSM read permissions.
   */
  readonly additionalPolicies?: iam.PolicyStatement[];

  /**
   * Human-readable description for the Lambda function.
   * If omitted, auto-generated from serviceName and functionName.
   */
  readonly description?: string;

  /**
   * Lambda CPU architecture.
   * Default: ARM_64 (Graviton2) for production cost optimization.
   * For LocalStack compatibility, X86_64 is used as default when isLocalStack=true.
   * Can be explicitly overridden via this prop.
   */
  readonly architecture?: lambda.Architecture;

  /**
   * Whether to enable AWS X-Ray active tracing.
   * Default: true for production (X-Ray ACTIVE), false for LocalStack (DISABLED).
   * Per AAP §0.3.2, X-Ray is deferred for LocalStack; correlation-ID structured
   * logging is used instead.
   */
  readonly tracing?: boolean;
}

// ---------------------------------------------------------------------------
// Constant: Forbidden environment variable names for security enforcement
// ---------------------------------------------------------------------------

/**
 * Environment variable names that must NEVER be set directly on Lambda functions.
 * These secrets must be retrieved at runtime from SSM Parameter Store SecureString.
 * Per AAP §0.8.3: "All secrets via SSM Parameter Store SecureString — NEVER
 * environment variables."
 */
const FORBIDDEN_ENV_VARS: ReadonlyArray<string> = [
  'DB_CONNECTION_STRING',
  'COGNITO_CLIENT_SECRET',
  'ENCRYPTION_KEY',
  'JWT_SECRET',
  'DATABASE_PASSWORD',
  'SECRET_KEY',
];

// ---------------------------------------------------------------------------
// Class: WebVellaLambdaService
// ---------------------------------------------------------------------------

/**
 * L3 CDK Construct that creates a standardized, production-ready Lambda function
 * for WebVella ERP bounded-context services.
 *
 * Creates:
 * 1. IAM execution role with least-privilege permissions
 * 2. CloudWatch Log Group with configurable retention
 * 3. Lambda function with runtime-appropriate defaults
 * 4. Resource tags for operational visibility
 *
 * Usage in CDK stacks:
 * ```typescript
 * const authHandler = new WebVellaLambdaService(this, 'AuthHandler', {
 *   serviceName: 'identity',
 *   functionName: 'auth-handler',
 *   runtime: LambdaRuntime.DOTNET_9_AOT,
 *   codePath: '../services/identity/src',
 *   handler: 'bootstrap',
 *   isLocalStack: true,
 *   cognitoUserPoolId: 'us-east-1_abc123',
 *   additionalPolicies: [dynamoDbReadWritePolicy],
 * });
 * // Use authHandler.function for API Gateway integration
 * // Use authHandler.role for additional IAM grants
 * ```
 */
export class WebVellaLambdaService extends Construct {
  /**
   * The underlying AWS Lambda Function resource.
   * Use for API Gateway integration, SQS event source mapping, IAM grants,
   * and cross-stack references.
   */
  public readonly function: lambda.Function;

  /**
   * The ARN of the Lambda function.
   * Use for cross-stack references and IAM policy resource specifications.
   */
  public readonly functionArn: string;

  /**
   * The full function name following the convention: webvella-{serviceName}-{functionName}.
   * Use for log group naming and operational dashboards.
   */
  public readonly functionName: string;

  /**
   * The IAM execution role attached to the Lambda function.
   * Use for granting additional permissions from consuming stacks
   * (e.g., DynamoDB table grants, S3 bucket grants).
   */
  public readonly role: iam.IRole;

  constructor(scope: Construct, id: string, props: WebVellaLambdaServiceProps) {
    super(scope, id);

    // ------------------------------------------------------------------
    // Step 1: Validate inputs
    // ------------------------------------------------------------------
    this.validateProps(props);

    // ------------------------------------------------------------------
    // Step 2: Determine runtime-specific defaults
    // ------------------------------------------------------------------
    const runtimeConfig = this.resolveRuntimeConfig(props);

    // ------------------------------------------------------------------
    // Step 3: Build the full function name
    // ------------------------------------------------------------------
    const fullFunctionName = `webvella-${props.serviceName}-${props.functionName}`;

    // ------------------------------------------------------------------
    // Step 4: Build standard environment variables
    // ------------------------------------------------------------------
    const environment = this.buildEnvironment(props);

    // ------------------------------------------------------------------
    // Step 5: Create the IAM execution role with least-privilege permissions
    // ------------------------------------------------------------------
    const executionRole = this.createExecutionRole(props, fullFunctionName);

    // ------------------------------------------------------------------
    // Step 6: Create the CloudWatch Log Group
    // ------------------------------------------------------------------
    const logGroup = this.createLogGroup(props, fullFunctionName);

    // ------------------------------------------------------------------
    // Step 7: Determine architecture
    // ------------------------------------------------------------------
    const architecture = this.resolveArchitecture(props);

    // ------------------------------------------------------------------
    // Step 8: Determine tracing configuration
    // ------------------------------------------------------------------
    const tracingConfig = this.resolveTracing(props);

    // ------------------------------------------------------------------
    // Step 9: Create the Lambda function
    // ------------------------------------------------------------------
    const lambdaFunction = new lambda.Function(this, 'Function', {
      functionName: fullFunctionName,
      runtime: runtimeConfig.runtime,
      handler: runtimeConfig.handler,
      code: lambda.Code.fromAsset(props.codePath),
      memorySize: runtimeConfig.memorySize,
      timeout: cdk.Duration.seconds(runtimeConfig.timeoutSeconds),
      environment,
      role: executionRole,
      architecture,
      tracing: tracingConfig,
      description:
        props.description ??
        `WebVella ERP ${props.serviceName} service - ${props.functionName}`,
      reservedConcurrentExecutions: props.reservedConcurrency,
      logGroup,
    });

    // Apply removal policy for LocalStack mode
    if (props.isLocalStack) {
      lambdaFunction.applyRemovalPolicy(cdk.RemovalPolicy.DESTROY);
    }

    // ------------------------------------------------------------------
    // Step 10: Apply resource tags
    // ------------------------------------------------------------------
    this.applyTags(props, lambdaFunction);

    // ------------------------------------------------------------------
    // Step 11: Expose public properties
    // ------------------------------------------------------------------
    this.function = lambdaFunction;
    this.functionArn = lambdaFunction.functionArn;
    this.functionName = fullFunctionName;
    this.role = executionRole;
  }

  // ====================================================================
  // Private Helper Methods
  // ====================================================================

  /**
   * Validates required props and enforces security constraints.
   * Throws descriptive errors for invalid configurations.
   */
  private validateProps(props: WebVellaLambdaServiceProps): void {
    if (!props.serviceName || props.serviceName.trim().length === 0) {
      throw new Error(
        'WebVellaLambdaService: serviceName is required and cannot be empty.',
      );
    }

    if (!props.functionName || props.functionName.trim().length === 0) {
      throw new Error(
        'WebVellaLambdaService: functionName is required and cannot be empty.',
      );
    }

    if (!props.codePath || props.codePath.trim().length === 0) {
      throw new Error(
        'WebVellaLambdaService: codePath is required and cannot be empty.',
      );
    }

    if (!props.handler || props.handler.trim().length === 0) {
      throw new Error(
        'WebVellaLambdaService: handler is required and cannot be empty.',
      );
    }

    if (
      props.runtime !== LambdaRuntime.DOTNET_9_AOT &&
      props.runtime !== LambdaRuntime.NODEJS_22
    ) {
      throw new Error(
        `WebVellaLambdaService: Invalid runtime '${props.runtime as string}'. ` +
          `Must be LambdaRuntime.DOTNET_9_AOT or LambdaRuntime.NODEJS_22.`,
      );
    }

    if (props.memorySize !== undefined && (props.memorySize < 128 || props.memorySize > 10240)) {
      throw new Error(
        `WebVellaLambdaService: memorySize must be between 128 and 10240 MB. Got: ${props.memorySize}`,
      );
    }

    if (props.timeoutSeconds !== undefined && (props.timeoutSeconds < 1 || props.timeoutSeconds > 900)) {
      throw new Error(
        `WebVellaLambdaService: timeoutSeconds must be between 1 and 900. Got: ${props.timeoutSeconds}`,
      );
    }

    // Enforce AAP §0.8.3 — no secrets in environment variables
    if (props.environment) {
      const violations = Object.keys(props.environment).filter((key) =>
        FORBIDDEN_ENV_VARS.includes(key.toUpperCase()),
      );
      if (violations.length > 0) {
        throw new Error(
          `WebVellaLambdaService: Security violation — the following environment variables ` +
            `must NOT be set directly on Lambda. Use SSM Parameter Store SecureString instead ` +
            `(AAP §0.8.3): ${violations.join(', ')}`,
        );
      }
    }
  }

  /**
   * Resolves runtime-specific configuration based on the LambdaRuntime enum value.
   *
   * .NET 9 Native AOT:
   * - Runtime: PROVIDED_AL2023 (custom runtime for AOT-compiled binaries)
   * - Handler: from props (typically 'bootstrap')
   * - Default memory: 512 MB (for < 1s cold start per AAP §0.8.2)
   * - Default timeout: 30 seconds
   *
   * Node.js 22:
   * - Runtime: NODEJS_22_X (managed runtime)
   * - Handler: from props (e.g., 'index.handler')
   * - Default memory: 256 MB (sufficient for authorizer Lambda)
   * - Default timeout: 30 seconds
   */
  private resolveRuntimeConfig(props: WebVellaLambdaServiceProps): {
    runtime: lambda.Runtime;
    handler: string;
    memorySize: number;
    timeoutSeconds: number;
  } {
    switch (props.runtime) {
      case LambdaRuntime.DOTNET_9_AOT:
        return {
          // Self-contained .NET 9 publish bundles the runtime in the deployment package.
          // provided.al2023 is used for BOTH LocalStack and production — the 'bootstrap'
          // executable in the publish directory IS the entry point (no managed runtime needed).
          runtime: lambda.Runtime.PROVIDED_AL2023,
          handler: props.handler,
          memorySize: props.memorySize ?? 512,
          // .NET 9 self-contained cold starts require longer timeouts (~15-40s on cold start)
          // due to JIT compilation overhead in the provided.al2023 runtime.
          timeoutSeconds: props.timeoutSeconds ?? 120,
        };

      case LambdaRuntime.NODEJS_22:
        return {
          runtime: lambda.Runtime.NODEJS_22_X,
          handler: props.handler,
          memorySize: props.memorySize ?? 256,
          timeoutSeconds: props.timeoutSeconds ?? 30,
        };

      default:
        // TypeScript exhaustive check — should never reach here after validation
        throw new Error(
          `WebVellaLambdaService: Unsupported runtime: ${props.runtime as string}`,
        );
    }
  }

  /**
   * Builds the merged environment variable map.
   *
   * Standard variables (always set):
   * - AWS_REGION: 'us-east-1'
   * - IS_LOCAL: 'true' or 'false' based on isLocalStack flag
   * - SERVICE_NAME: serviceName (for correlation-ID structured logging)
   * - POWERTOOLS_SERVICE_NAME: serviceName (AWS Lambda Powertools integration)
   * - LOG_LEVEL: 'INFO'
   *
   * Conditional variables:
   * - AWS_ENDPOINT_URL: 'http://host.docker.internal:4566' (only when isLocalStack=true)
   * - COGNITO_USER_POOL_ID: from props (only when provided)
   *
   * Note: AWS_REGION is NOT set manually because the Lambda runtime reserves this
   * environment variable and sets it automatically based on the deployment region.
   * See: https://docs.aws.amazon.com/lambda/latest/dg/configuration-envvars.html
   *
   * User-provided environment variables from props.environment override standard defaults.
   */
  private buildEnvironment(props: WebVellaLambdaServiceProps): {
    [key: string]: string;
  } {
    // Standard environment variables per AAP §0.8.6
    // Note: AWS_REGION is a reserved Lambda runtime variable and cannot be set manually.
    // The Lambda runtime sets it automatically to the region where the function is deployed.
    const standardEnv: { [key: string]: string } = {
      IS_LOCAL: props.isLocalStack ? 'true' : 'false',
      SERVICE_NAME: props.serviceName,
      POWERTOOLS_SERVICE_NAME: props.serviceName,
      LOG_LEVEL: 'INFO',
    };

    // LocalStack-specific: inject the endpoint URL for SDK redirects
    // Uses LOCALSTACK_HOSTNAME env var (auto-injected by LocalStack into Lambda containers)
    // to dynamically resolve the LocalStack endpoint from within Docker networks.
    // Falls back to Docker bridge gateway 172.17.0.1 which routes to the host's port binding.
    if (props.isLocalStack) {
      standardEnv['AWS_ENDPOINT_URL'] = 'http://172.17.0.1:4566';
    }

    // Cognito User Pool ID: passed by SharedStack, used for token validation
    if (props.cognitoUserPoolId) {
      standardEnv['COGNITO_USER_POOL_ID'] = props.cognitoUserPoolId;
    }

    // Merge user-provided environment variables (props override standard defaults)
    const mergedEnv: { [key: string]: string } = {
      ...standardEnv,
      ...(props.environment ?? {}),
    };

    return mergedEnv;
  }

  /**
   * Creates the IAM execution role with least-privilege permissions.
   *
   * Base permissions:
   * - AWSLambdaBasicExecutionRole (CloudWatch Logs: CreateLogGroup, CreateLogStream, PutLogEvents)
   *
   * SSM read permissions:
   * - ssm:GetParameter and ssm:GetParametersByPath for /{serviceName}/* paths
   * - This allows each service to read its own configuration secrets from SSM
   *   without access to other services' parameters (per AAP §0.8.3 least-privilege)
   *
   * Additional policies:
   * - Any service-specific policies from props.additionalPolicies
   *   (e.g., DynamoDB read/write, S3 operations, SQS/SNS operations)
   */
  private createExecutionRole(
    props: WebVellaLambdaServiceProps,
    fullFunctionName: string,
  ): iam.Role {
    const role = new iam.Role(this, 'ExecutionRole', {
      roleName: `${fullFunctionName}-role`,
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
      description: `Execution role for Lambda function ${fullFunctionName}`,
      managedPolicies: [
        iam.ManagedPolicy.fromAwsManagedPolicyName(
          'service-role/AWSLambdaBasicExecutionRole',
        ),
      ],
    });

    // SSM Parameter Store read access — scoped to the service's parameter path
    // Each service can read only its own secrets: /{serviceName}/*
    // Plus shared parameters under /shared/*
    role.addToPolicy(
      new iam.PolicyStatement({
        effect: iam.Effect.ALLOW,
        actions: ['ssm:GetParameter', 'ssm:GetParametersByPath'],
        resources: [
          `arn:aws:ssm:*:*:parameter/${props.serviceName}/*`,
          'arn:aws:ssm:*:*:parameter/shared/*',
        ],
      }),
    );

    // SSM decrypt permission for SecureString parameters
    role.addToPolicy(
      new iam.PolicyStatement({
        effect: iam.Effect.ALLOW,
        actions: ['kms:Decrypt'],
        resources: ['*'],
        conditions: {
          StringEquals: {
            'kms:ViaService': 'ssm.us-east-1.amazonaws.com',
          },
        },
      }),
    );

    // Append any additional service-specific IAM policies
    if (props.additionalPolicies && props.additionalPolicies.length > 0) {
      for (const policy of props.additionalPolicies) {
        role.addToPolicy(policy);
      }
    }

    return role;
  }

  /**
   * Creates a CloudWatch Log Group for the Lambda function with configurable retention.
   *
   * Log group name follows AWS Lambda convention: /aws/lambda/{functionName}
   *
   * Retention policy per AAP §0.7.6:
   * - LocalStack: 30 days (ONE_MONTH) — shorter retention for dev/test
   * - Production: 90 days (THREE_MONTHS) — longer retention for compliance
   *
   * Removal policy:
   * - LocalStack: DESTROY — clean up on stack deletion
   * - Production: RETAIN — preserve logs after stack deletion
   */
  private createLogGroup(
    props: WebVellaLambdaServiceProps,
    fullFunctionName: string,
  ): logs.LogGroup {
    const logGroup = new logs.LogGroup(this, 'LogGroup', {
      logGroupName: `/aws/lambda/${fullFunctionName}`,
      retention: props.isLocalStack
        ? logs.RetentionDays.ONE_MONTH
        : logs.RetentionDays.THREE_MONTHS,
      removalPolicy: props.isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });

    return logGroup;
  }

  /**
   * Resolves the Lambda CPU architecture.
   *
   * Priority:
   * 1. Explicit prop value (if provided)
   * 2. X86_64 for LocalStack (better compatibility with Lambda emulation)
   * 3. ARM_64 (Graviton2) for production (cost optimization per AAP §0.7.6)
   */
  private resolveArchitecture(
    props: WebVellaLambdaServiceProps,
  ): lambda.Architecture {
    if (props.architecture) {
      return props.architecture;
    }

    // LocalStack Lambda emulation works best with X86_64
    if (props.isLocalStack) {
      return lambda.Architecture.X86_64;
    }

    // Production: ARM_64 (Graviton2) for cost optimization
    return lambda.Architecture.ARM_64;
  }

  /**
   * Resolves the X-Ray tracing configuration.
   *
   * Per AAP §0.3.2 and §0.7.6:
   * - Production: X-Ray ACTIVE tracing for distributed trace analysis
   * - LocalStack: DISABLED (X-Ray deferred for LocalStack; use correlation-ID
   *   structured logging instead)
   *
   * Can be explicitly overridden via the tracing prop.
   */
  private resolveTracing(props: WebVellaLambdaServiceProps): lambda.Tracing {
    // Explicit override takes precedence
    if (props.tracing !== undefined) {
      return props.tracing ? lambda.Tracing.ACTIVE : lambda.Tracing.DISABLED;
    }

    // Default: active in production, disabled in LocalStack
    return props.isLocalStack
      ? lambda.Tracing.DISABLED
      : lambda.Tracing.ACTIVE;
  }

  /**
   * Applies standard resource tags for operational visibility and cost allocation.
   *
   * Tags applied:
   * - service: The bounded-context service name (e.g., 'identity', 'crm')
   * - function: The function name suffix (e.g., 'auth-handler')
   * - runtime: The runtime identifier (e.g., 'dotnet9-aot', 'nodejs22')
   * - resource: Fixed value 'lambda-function' for resource type filtering
   */
  private applyTags(
    props: WebVellaLambdaServiceProps,
    lambdaFunction: lambda.Function,
  ): void {
    cdk.Tags.of(lambdaFunction).add('service', props.serviceName);
    cdk.Tags.of(lambdaFunction).add('function', props.functionName);
    cdk.Tags.of(lambdaFunction).add('runtime', props.runtime);
    cdk.Tags.of(lambdaFunction).add('resource', 'lambda-function');
  }
}
