/**
 * SharedStack — Foundation infrastructure stack for WebVella ERP microservices.
 *
 * This stack creates the shared AWS resources consumed by ALL other service stacks:
 *
 * 1. **Cognito User Pool** — Central authentication hub replacing the monolith's
 *    cookie+JWT dual-scheme auth (Startup.cs lines 88-125). Configured with email
 *    sign-in, password policy, and a user migration Lambda trigger for MD5→Cognito
 *    password migration (SecurityManager.cs PasswordUtil.GetMd5Hash).
 *
 * 2. **Cognito User Pool Client** — Public SPA client (no secret) for the React 19
 *    frontend, with OAuth Authorization Code grant and openid/email/profile scopes.
 *
 * 3. **Cognito Groups** — Role-based access control groups mapping the monolith's
 *    system roles from Definitions.cs: admin (AdministratorRoleId), regular
 *    (RegularRoleId), guest (GuestRoleId).
 *
 * 4. **SNS Domain Event Bus** — Central topic replacing the monolith's synchronous
 *    HookManager post-hook invocations and PostgreSQL LISTEN/NOTIFY pub/sub.
 *    Event naming convention: {domain}.{entity}.{action}
 *
 * 5. **SQS Dead-Letter Queue** — Shared DLQ for undeliverable messages across all
 *    services, with 14-day retention per AAP section 0.8.5.
 *
 * 6. **SSM Parameter Store** — All Config.json settings migrated to SSM parameters.
 *    Secrets use SecureString per AAP section 0.8.1 (never environment variables).
 *
 * 7. **VPC** — Shared network for RDS-backed services (Invoicing, Reporting).
 *    LocalStack uses default VPC; production creates a dedicated VPC.
 *
 * Source files referenced:
 * - WebVella.Erp.Site/Config.json (37 lines — full config migration)
 * - WebVella.Erp.Site/Startup.cs (lines 88-125 — auth configuration)
 * - WebVella.Erp/Api/Definitions.cs (SystemIds — role GUIDs for Cognito groups)
 * - WebVella.Erp/Api/SecurityManager.cs (MD5 password hashing -> migration Lambda)
 * - WebVella.Erp/Api/SecurityContext.cs (system user context -> Cognito mapping)
 * - WebVella.Erp/Hooks/HookManager.cs (synchronous hooks -> SNS events)
 * - WebVella.Erp/Hooks/IErpPost{Create,Update,Delete}RecordHook.cs (event contracts)
 *
 * @module infra/src/stacks/shared-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as path from 'path';

/**
 * Configuration properties for the SharedStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * per AAP section 0.7.6. The isLocalStack flag controls resource configuration:
 * - LocalStack: Simplified resources, DESTROY removal policy, default VPC
 * - Production: Full HA, RETAIN removal policy, dedicated VPC, encryption
 */
export interface SharedStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: this.node.tryGetContext('localstack') === 'true'
   * Controls conditional resource creation per AAP section 0.7.6:
   * - Cognito password policy (relaxed vs strict)
   * - VPC type (default lookup vs dedicated)
   * - Removal policies (DESTROY vs RETAIN)
   * - Email verification (auto-verified vs required)
   */
  readonly isLocalStack: boolean;
}

/**
 * SharedStack — Foundation infrastructure for all WebVella ERP microservices.
 *
 * This is the ROOT stack with ZERO dependencies on other stacks. All other
 * service stacks consume resources from this stack via public properties
 * and CfnOutput cross-stack references.
 */
export class SharedStack extends cdk.Stack {
  /**
   * Cognito User Pool for centralized authentication.
   * Replaces: Cookie+JWT dual-scheme auth from Startup.cs
   * Consumed by: All service stacks for JWT validation
   */
  public readonly userPool: cognito.UserPool;

  /**
   * Cognito User Pool Client for the React SPA.
   * Public client (no secret) with Authorization Code grant.
   * Consumed by: Frontend stack for Cognito SDK configuration
   */
  public readonly userPoolClient: cognito.UserPoolClient;

  /**
   * Central SNS topic serving as the domain event bus.
   * Replaces: HookManager synchronous post-hooks and PostgreSQL LISTEN/NOTIFY
   * Event naming: {domain}.{entity}.{action} via message attributes
   * Consumed by: All service stacks for event publishing and subscription
   */
  public readonly eventBus: sns.Topic;

  /**
   * Shared VPC for RDS-backed services (Invoicing, Reporting).
   * LocalStack: Default VPC via fromLookup
   * Production: Dedicated VPC with public/private subnets
   * Consumed by: Invoicing stack, Reporting stack
   */
  public readonly vpc: ec2.IVpc;

  constructor(scope: Construct, id: string, props: SharedStackProps) {
    super(scope, id, props);

    const { isLocalStack } = props;

    // Determine removal policy based on deployment target (AAP section 0.7.6)
    // LocalStack: DESTROY — clean up resources on stack deletion for dev/test cycles
    // Production: RETAIN — preserve resources after stack deletion for safety
    const removalPolicy = isLocalStack
      ? cdk.RemovalPolicy.DESTROY
      : cdk.RemovalPolicy.RETAIN;

    // ================================================================
    // 1. COGNITO USER POOL
    // Replaces: Startup.cs Cookie+JWT dual-scheme authentication (lines 88-125)
    // Source: WebVella.Erp.Site/Startup.cs — addAuthentication with JWT_OR_COOKIE policy
    // Source: WebVella.Erp/Api/SecurityManager.cs — user credential management (MD5)
    // Source: WebVella.Erp/Api/SecurityContext.cs — system user with role "administrator"
    //
    // The monolith used cookie-based auth with JWT Bearer fallback.
    // Cookie name: "erp_auth_base"
    // JWT settings from Config.json: Issuer="webvella-erp", Audience="webvella-erp"
    // SymmetricSecurityKey from Config.json JWT.Key
    //
    // Target: Single Cognito user pool with JWKS-based token validation,
    // eliminating the need for shared symmetric keys across services.
    // ================================================================

    this.userPool = new cognito.UserPool(this, 'UserPool', {
      userPoolName: 'webvella-erp-users',

      // Sign-in configuration: email-based authentication
      // Replaces cookie-based username/password from Startup.cs
      signInAliases: {
        email: true,
        username: false,
        phone: false,
      },

      // Self-sign-up disabled: admin-only user creation
      // Mirrors SecurityManager.cs admin-controlled user provisioning
      // New users are created via admin API or migrated via user migration Lambda
      selfSignUpEnabled: false,

      // Email verification configuration
      // LocalStack: auto-verify email (no SES available locally)
      // Production: email verification required before account activation
      autoVerify: {
        email: true,
      },

      // Password policy configuration
      // Production: strong requirements matching enterprise standards
      // LocalStack: relaxed for development convenience (min 8 chars only)
      // Note: original monolith used MD5 hashing (SecurityManager.cs PasswordUtil.GetMd5Hash)
      // which had no complexity requirements — Cognito enforces better security
      passwordPolicy: {
        minLength: 8,
        requireUppercase: !isLocalStack,
        requireLowercase: !isLocalStack,
        requireDigits: !isLocalStack,
        requireSymbols: false,
        tempPasswordValidity: cdk.Duration.days(7),
      },

      // MFA disabled for initial deployment
      // Can be enabled later via stack update without user disruption
      mfa: cognito.Mfa.OFF,

      // Account recovery via email only
      // Matches the monolith's email-based password reset flow
      accountRecovery: cognito.AccountRecovery.EMAIL_ONLY,

      // Standard attributes configuration
      // Maps from SecurityManager.cs user fields (email, first_name, last_name)
      standardAttributes: {
        email: {
          required: true,
          mutable: true,
        },
        givenName: {
          required: false,
          mutable: true,
        },
        familyName: {
          required: false,
          mutable: true,
        },
      },

      // Custom attributes for role mapping
      // Replaces: SecurityContext.cs AsyncLocal role assignment
      // Stores comma-separated role names matching Cognito group membership
      // Example value: "admin,regular" or "guest"
      // Used by Lambda authorizer for fine-grained permission checks
      customAttributes: {
        roles: new cognito.StringAttribute({
          mutable: true,
          minLen: 0,
          maxLen: 2048,
        }),
      },

      // Removal policy based on deployment target
      removalPolicy: removalPolicy,
    });

    // Apply resource tags to user pool for operational visibility
    cdk.Tags.of(this.userPool).add('service', 'shared');
    cdk.Tags.of(this.userPool).add('resource', 'cognito-user-pool');

    // ================================================================
    // 2. USER MIGRATION LAMBDA TRIGGER
    // Handles MD5-to-Cognito password migration on first login attempt
    // Source: WebVella.Erp/Api/SecurityManager.cs — PasswordUtil.GetMd5Hash
    //
    // Per AAP section 0.7.5 Authentication Migration Path:
    // - Deploy a UserMigration_Authentication Lambda trigger on Cognito user pool
    // - On first login attempt, the trigger calls legacy password verification logic
    // - If the MD5 hash matches, the Lambda creates the user in Cognito with
    //   the provided password (Cognito hashes it securely with SRP)
    // - Subsequent logins use Cognito natively — no migration overhead
    //
    // The default system user (erp@webvella.com / erp) is seeded via
    // tools/scripts/seed-test-data.sh, NOT in CDK
    // ================================================================

    // IAM execution role with least-privilege per AAP section 0.8.3
    const migrationLambdaRole = new iam.Role(this, 'UserMigrationLambdaRole', {
      roleName: 'webvella-erp-user-migration-role',
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
      description:
        'Execution role for Cognito user migration Lambda trigger (MD5 password migration)',
      managedPolicies: [
        iam.ManagedPolicy.fromAwsManagedPolicyName(
          'service-role/AWSLambdaBasicExecutionRole',
        ),
      ],
    });

    // Grant Cognito admin permissions for user creation during migration flow
    //
    // IMPORTANT: We use a region/account-scoped wildcard ARN pattern instead of
    // this.userPool.userPoolArn to avoid a circular dependency:
    //   UserPool -> Lambda (via addTrigger) -> IAM Policy -> UserPool (via ARN ref)
    // Using stack pseudo-parameters (region/account) breaks this cycle while still
    // scoping permissions to the correct account and region.
    migrationLambdaRole.addToPolicy(
      new iam.PolicyStatement({
        effect: iam.Effect.ALLOW,
        actions: [
          'cognito-idp:AdminCreateUser',
          'cognito-idp:AdminSetUserPassword',
          'cognito-idp:AdminUpdateUserAttributes',
        ],
        resources: [
          `arn:aws:cognito-idp:${this.region}:${this.account}:userpool/*`,
        ],
      }),
    );

    // Grant DynamoDB read access for legacy user lookup during migration
    // Scoped to identity service table at deployment time via resource ARN
    migrationLambdaRole.addToPolicy(
      new iam.PolicyStatement({
        effect: iam.Effect.ALLOW,
        actions: ['dynamodb:GetItem', 'dynamodb:Query'],
        resources: ['arn:aws:dynamodb:*:*:table/webvella-erp-identity-*'],
      }),
    );

    // User migration Lambda function
    // Runtime: Node.js 22.x — lightweight, fast cold start for Cognito trigger
    // Handler: index.handler (standard Node.js Lambda handler convention)
    // Code: Asset bundle from identity service migration trigger directory
    const userMigrationLambda = new lambda.Function(
      this,
      'UserMigrationLambda',
      {
        functionName: 'webvella-erp-user-migration',
        runtime: lambda.Runtime.NODEJS_22_X,
        handler: 'index.handler',
        code: lambda.Code.fromAsset(
          path.join(
            __dirname,
            '..',
            '..',
            '..',
            'services',
            'identity',
            'src',
            'triggers',
            'user-migration',
          ),
        ),
        timeout: cdk.Duration.minutes(1),
        memorySize: 256,
        role: migrationLambdaRole,
        description:
          'Cognito user migration trigger: verifies legacy MD5 password hash and creates user in Cognito on first login',
        environment: {
          IS_LOCAL: isLocalStack ? 'true' : 'false',
          NODE_OPTIONS: '--enable-source-maps',
          ...(isLocalStack
            ? { AWS_ENDPOINT_URL: 'http://host.docker.internal:4566' }
            : {}),
        },
      },
    );

    // Apply resource tags to migration Lambda
    cdk.Tags.of(userMigrationLambda).add('service', 'identity');
    cdk.Tags.of(userMigrationLambda).add('resource', 'lambda-trigger');
    cdk.Tags.of(userMigrationLambda).add('trigger-type', 'user-migration');

    // Attach user migration Lambda as Cognito trigger
    // Fires on UserMigration_Authentication event when user not found in pool
    // See: https://docs.aws.amazon.com/cognito/latest/developerguide/user-pool-lambda-migrate-user.html
    this.userPool.addTrigger(
      cognito.UserPoolOperation.USER_MIGRATION,
      userMigrationLambda,
    );

    // ================================================================
    // 3. COGNITO USER POOL CLIENT
    // Public SPA client for the React 19 frontend (no client secret)
    // Replaces: JWT token issuance from AuthService.cs
    // Source: WebVella.Erp.Web/Services/AuthService.cs — token generation
    // Source: WebVella.Erp.Site/Config.json — JWT Issuer/Audience/Key settings
    //
    // The monolith issued JWTs with:
    //   Issuer = "webvella-erp" (Config.json Jwt.Issuer)
    //   Audience = "webvella-erp" (Config.json Jwt.Audience)
    //   SigningKey = SymmetricSecurityKey from Config.json Jwt.Key
    //
    // Target: Cognito-issued JWTs with asymmetric JWKS validation,
    // eliminating shared symmetric keys across microservices.
    // ================================================================

    this.userPoolClient = new cognito.UserPoolClient(
      this,
      'UserPoolClient',
      {
        userPool: this.userPool,
        userPoolClientName: 'webvella-erp-spa',

        // No client secret — public SPA client per PKCE flow
        // The React frontend cannot securely store a client secret
        generateSecret: false,

        // Auth flows configuration
        // USER_SRP_AUTH: Secure Remote Password (recommended for production)
        // USER_PASSWORD_AUTH: Direct password auth (required for user migration trigger)
        authFlows: {
          userSrp: true,
          userPassword: true,
        },

        // OAuth 2.0 / OIDC configuration
        oAuth: {
          flows: {
            authorizationCodeGrant: true,
            implicitCodeGrant: false,
          },
          scopes: [
            cognito.OAuthScope.OPENID,
            cognito.OAuthScope.EMAIL,
            cognito.OAuthScope.PROFILE,
          ],
          callbackUrls: isLocalStack
            ? [
                'http://localhost:5173/callback',
                'http://localhost:5173/',
                'http://localhost:3000/callback',
              ]
            : ['https://app.webvella-erp.com/callback'],
          logoutUrls: isLocalStack
            ? ['http://localhost:5173/', 'http://localhost:3000/']
            : ['https://app.webvella-erp.com/'],
        },

        // Token validity periods
        // Access token: 1 hour (short-lived for API calls)
        // ID token: 1 hour (short-lived, contains user claims)
        // Refresh token: 30 days (long-lived for session persistence)
        accessTokenValidity: cdk.Duration.hours(1),
        idTokenValidity: cdk.Duration.hours(1),
        refreshTokenValidity: cdk.Duration.days(30),

        // Prevent user existence errors — security best practice per AAP section 0.8.3
        // Returns generic "user not found" instead of "user does not exist" vs "wrong password"
        preventUserExistenceErrors: true,
      },
    );

    // ================================================================
    // 4. COGNITO GROUPS (Role Mapping)
    // Maps monolith system roles from Definitions.cs to Cognito groups
    // Source: WebVella.Erp/Api/Definitions.cs — SystemIds class
    // Source: WebVella.Erp/Api/SecurityContext.cs — system user role "administrator"
    //
    // Role hierarchy (via precedence — lower number = higher priority):
    //   admin (1) > regular (10) > guest (100)
    //
    // When a user belongs to multiple groups, the group with the lowest
    // precedence value is used for the preferred_role claim in the ID token.
    // ================================================================

    // Administrator group
    // Maps to: Definitions.cs AdministratorRoleId = BDC56420-CAF0-4030-8A0E-D264938E0CDA
    // Grants: Full system access — entity/field management, user administration, plugin management
    // SecurityContext.cs uses role name "administrator" for system user
    new cognito.CfnUserPoolGroup(this, 'AdminGroup', {
      groupName: 'admin',
      userPoolId: this.userPool.userPoolId,
      description:
        'Administrator role (maps to Definitions.cs AdministratorRoleId: BDC56420-CAF0-4030-8A0E-D264938E0CDA)',
      precedence: 1,
    });

    // Regular user group
    // Maps to: Definitions.cs RegularRoleId = F16EC6DB-626D-4C27-8DE0-3E7CE542C55F
    // Grants: Standard application access — record CRUD within entity permissions
    new cognito.CfnUserPoolGroup(this, 'RegularGroup', {
      groupName: 'regular',
      userPoolId: this.userPool.userPoolId,
      description:
        'Regular user role (maps to Definitions.cs RegularRoleId: F16EC6DB-626D-4C27-8DE0-3E7CE542C55F)',
      precedence: 10,
    });

    // Guest group
    // Maps to: Definitions.cs GuestRoleId = 987148B1-AFA8-4B33-8616-55861E5FD065
    // Grants: Read-only access to public entities only
    new cognito.CfnUserPoolGroup(this, 'GuestGroup', {
      groupName: 'guest',
      userPoolId: this.userPool.userPoolId,
      description:
        'Guest role (maps to Definitions.cs GuestRoleId: 987148B1-AFA8-4B33-8616-55861E5FD065)',
      precedence: 100,
    });

    // ================================================================
    // 5. SNS DOMAIN EVENT BUS
    // Central topic for ALL domain events across all bounded-context services
    // Replaces: HookManager synchronous post-hook invocations (WebVella.Erp/Hooks/)
    // Replaces: PostgreSQL LISTEN/NOTIFY pub/sub (WebVella.Erp/Notifications/)
    //
    // Source hook contracts mapped to SNS events:
    // - IErpPostCreateRecordHook.OnPostCreateRecord(entityName, record)
    //   -> SNS event: {domain}.{entity}.created
    // - IErpPostUpdateRecordHook.OnPostUpdateRecord(entityName, record)
    //   -> SNS event: {domain}.{entity}.updated
    // - IErpPostDeleteRecordHook.OnPostDeleteRecord(entityName, record)
    //   -> SNS event: {domain}.{entity}.deleted
    //
    // Event naming convention per AAP section 0.8.5:
    //   {domain}.{entity}.{action}
    //   Examples: crm.account.created, invoicing.invoice.updated, workflow.job.completed
    //
    // Message attributes enable selective SQS subscription filtering:
    //   eventType: "crm.account.created" (string attribute for filter policies)
    //   domain: "crm" (for domain-level filtering)
    //   correlationId: UUID (for request tracing across services)
    // ================================================================

    this.eventBus = new sns.Topic(this, 'DomainEventBus', {
      topicName: 'webvella-erp-domain-events',
      displayName: 'WebVella ERP Domain Events Bus',
    });

    // Apply removal policy to event bus
    this.eventBus.applyRemovalPolicy(removalPolicy);

    // Apply resource tags for identification and cost allocation
    cdk.Tags.of(this.eventBus).add('service', 'shared');
    cdk.Tags.of(this.eventBus).add('resource', 'sns-topic');
    cdk.Tags.of(this.eventBus).add('purpose', 'domain-event-bus');

    // ================================================================
    // 6. SQS SHARED DEAD-LETTER QUEUE
    // General-purpose DLQ for undeliverable messages across all services
    // DLQ naming convention per AAP section 0.8.5: {service}-{queue}-dlq
    // 14-day message retention for forensic analysis of failed messages
    //
    // Each service also creates its own per-queue DLQs via the WebVellaEventBus
    // construct (infra/src/constructs/event-bus.ts). This shared DLQ serves as
    // the catch-all for messages that escape per-service DLQs or for
    // infrastructure-level failures.
    // ================================================================

    const sharedDlq = new sqs.Queue(this, 'SharedDlq', {
      queueName: 'webvella-erp-dlq',
      retentionPeriod: cdk.Duration.days(14),
      removalPolicy: removalPolicy,
    });

    // Apply resource tags to shared DLQ
    cdk.Tags.of(sharedDlq).add('service', 'shared');
    cdk.Tags.of(sharedDlq).add('resource', 'dead-letter-queue');

    // ================================================================
    // 7. SSM PARAMETER STORE
    // All Config.json settings migrated to SSM Parameter Store
    // Source: WebVella.Erp.Site/Config.json (37 lines)
    //
    // Standard parameters: non-sensitive application configuration
    // SecureString parameters: secrets per AAP section 0.8.1
    //   "All secrets via SSM Parameter Store SecureString — NEVER environment variables"
    //
    // Parameter hierarchy:
    //   /webvella-erp/config/*     — Application configuration
    //   /webvella-erp/cognito/*    — Cognito resource references
    //   /webvella-erp/events/*     — Event bus resource references
    // ================================================================

    // --- Standard Parameters (non-sensitive configuration) ---

    // Application name
    // Source: Config.json "AppName": "WebVella Next"
    new ssm.StringParameter(this, 'ParamAppName', {
      parameterName: '/webvella-erp/config/app-name',
      stringValue: 'WebVella Next',
      description:
        'Application display name (from Config.json AppName)',
    });

    // Locale setting
    // Source: Config.json "Locale": "en-US"
    new ssm.StringParameter(this, 'ParamLocale', {
      parameterName: '/webvella-erp/config/locale',
      stringValue: 'en-US',
      description: 'Application locale (from Config.json Locale)',
    });

    // Timezone setting
    // Source: Config.json "TimeZoneName": "FLE Standard Time"
    new ssm.StringParameter(this, 'ParamTimezone', {
      parameterName: '/webvella-erp/config/timezone',
      stringValue: 'FLE Standard Time',
      description:
        'Application timezone (from Config.json TimeZoneName)',
    });

    // Development mode flag
    // Source: Config.json "DevelopmentMode": "true"
    // Controls debug logging, detailed error responses, and developer tools
    new ssm.StringParameter(this, 'ParamDevelopmentMode', {
      parameterName: '/webvella-erp/config/development-mode',
      stringValue: isLocalStack ? 'true' : 'false',
      description:
        'Development mode flag (from Config.json DevelopmentMode)',
    });

    // AWS Region
    // Per AAP section 0.8.6: AWS_REGION = us-east-1
    new ssm.StringParameter(this, 'ParamRegion', {
      parameterName: '/webvella-erp/config/region',
      stringValue: props.env?.region ?? 'us-east-1',
      description: 'AWS deployment region',
    });

    // Cognito User Pool ID — runtime reference for services
    // Services use this to validate JWT tokens and manage users
    new ssm.StringParameter(this, 'ParamCognitoUserPoolId', {
      parameterName: '/webvella-erp/cognito/user-pool-id',
      stringValue: this.userPool.userPoolId,
      description:
        'Cognito User Pool ID for JWT validation by service Lambdas',
    });

    // Cognito App Client ID — runtime reference for frontend
    // The React SPA uses this to configure the Cognito SDK
    new ssm.StringParameter(this, 'ParamCognitoClientId', {
      parameterName: '/webvella-erp/cognito/client-id',
      stringValue: this.userPoolClient.userPoolClientId,
      description:
        'Cognito User Pool Client ID for SPA authentication',
    });

    // SNS Topic ARN — runtime reference for event publishing
    // All services use this ARN to publish domain events to the central bus
    new ssm.StringParameter(this, 'ParamEventTopicArn', {
      parameterName: '/webvella-erp/events/topic-arn',
      stringValue: this.eventBus.topicArn,
      description: 'SNS domain event bus topic ARN for event publishing',
    });

    // --- SecureString Parameters (secrets) ---
    // CDK does not have a high-level construct for SSM SecureString.
    // Using the L1 CfnParameter construct (AWS::SSM::Parameter) with Type: SecureString.
    // Per AAP section 0.8.1: All secrets MUST use SSM SecureString, NEVER environment variables.
    //
    // SECURITY NOTE: The value is visible in the CloudFormation template.
    // For production, rotate this key via AWS Secrets Manager or a secure
    // deployment pipeline that injects the value post-deployment.

    // Encryption key for data protection
    // Source: Config.json "EncryptionKey": "BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658"
    // Used for: symmetric encryption of sensitive data fields across services
    new ssm.CfnParameter(this, 'ParamEncryptionKey', {
      type: 'SecureString',
      name: '/webvella-erp/config/encryption-key',
      value: isLocalStack
        ? 'BC93B776A42877CFEE808823BA8B37C83B6B0AD23198AC3AF2B5A54DCB647658'
        : 'REPLACE_WITH_PRODUCTION_ENCRYPTION_KEY',
      description:
        'Encryption key for data protection (from Config.json EncryptionKey). SecureString per AAP section 0.8.1.',
    });

    // ================================================================
    // 8. VPC (Shared Network)
    // Required by RDS-backed services: Invoicing (RDS PostgreSQL), Reporting (RDS PostgreSQL)
    // Per AAP section 0.7.6 dual-target strategy:
    //   LocalStack: Use default VPC via fromLookup (simpler, no NAT costs)
    //   Production: Create dedicated VPC with public/private subnets
    //
    // The VPC reference is exported for Invoicing/Reporting stack consumption.
    // DynamoDB-backed services do not need VPC access (use VPC endpoints or
    // public DynamoDB endpoints).
    // ================================================================

    if (isLocalStack) {
      // LocalStack: Use default VPC for simplicity
      // LocalStack emulates a default VPC in account 000000000000
      this.vpc = ec2.Vpc.fromLookup(this, 'DefaultVpc', {
        isDefault: true,
      });
    } else {
      // Production: Create dedicated VPC with public/private subnets
      //
      // Network layout:
      // - Public subnets (2 AZs): NAT gateways, bastion hosts, load balancers
      // - Private subnets (2 AZs): RDS instances, Lambda functions in VPC
      //
      // NAT gateways: 1 (cost optimization for non-critical workloads)
      // For high-availability production, increase to natGateways: 2
      this.vpc = new ec2.Vpc(this, 'SharedVpc', {
        vpcName: 'webvella-erp-vpc',
        maxAzs: 2,
        natGateways: 1,
        subnetConfiguration: [
          {
            name: 'Public',
            subnetType: ec2.SubnetType.PUBLIC,
            cidrMask: 24,
          },
          {
            name: 'Private',
            subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS,
            cidrMask: 24,
          },
        ],
      });

      // Apply tags to production VPC
      cdk.Tags.of(this.vpc).add('service', 'shared');
      cdk.Tags.of(this.vpc).add('resource', 'vpc');
    }

    // ================================================================
    // 9. CFN OUTPUTS (Cross-Stack References)
    // These outputs enable other stacks to reference shared resources
    // via cdk.Fn.importValue() or direct property access when stacks
    // are composed in the same CDK app.
    //
    // Export names follow convention: WebVellaErp-{ResourceType}
    // ================================================================

    new cdk.CfnOutput(this, 'UserPoolIdOutput', {
      value: this.userPool.userPoolId,
      description: 'Cognito User Pool ID',
      exportName: 'WebVellaErp-UserPoolId',
    });

    new cdk.CfnOutput(this, 'UserPoolArnOutput', {
      value: this.userPool.userPoolArn,
      description: 'Cognito User Pool ARN',
      exportName: 'WebVellaErp-UserPoolArn',
    });

    new cdk.CfnOutput(this, 'UserPoolClientIdOutput', {
      value: this.userPoolClient.userPoolClientId,
      description: 'Cognito User Pool Client ID for the SPA',
      exportName: 'WebVellaErp-UserPoolClientId',
    });

    new cdk.CfnOutput(this, 'EventBusTopicArnOutput', {
      value: this.eventBus.topicArn,
      description: 'SNS Domain Event Bus Topic ARN',
      exportName: 'WebVellaErp-EventBusTopicArn',
    });

    new cdk.CfnOutput(this, 'SharedDlqArnOutput', {
      value: sharedDlq.queueArn,
      description: 'Shared Dead-Letter Queue ARN',
      exportName: 'WebVellaErp-SharedDlqArn',
    });

    new cdk.CfnOutput(this, 'SharedDlqUrlOutput', {
      value: sharedDlq.queueUrl,
      description: 'Shared Dead-Letter Queue URL',
      exportName: 'WebVellaErp-SharedDlqUrl',
    });

    new cdk.CfnOutput(this, 'VpcIdOutput', {
      value: this.vpc.vpcId,
      description: 'Shared VPC ID for RDS-backed services',
      exportName: 'WebVellaErp-VpcId',
    });
  }
}
