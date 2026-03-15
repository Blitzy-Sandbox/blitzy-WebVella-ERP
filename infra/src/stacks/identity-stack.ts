/**
 * IdentityStack — Identity & Access Management Service Infrastructure.
 *
 * This CDK stack defines all AWS resources for the Identity & Access
 * Management bounded context, replacing the monolith's authentication
 * and authorization subsystems with a DynamoDB-backed serverless service
 * handling user CRUD, role management, and Cognito-integrated authentication.
 *
 * **Source systems replaced:**
 * - `AuthService.cs` (169 lines) — Cookie-based authentication (lines 29-55)
 *   with `SecurityManager.GetUser(email, password)` + cookie sign-in, and
 *   JWT token generation/validation (lines 83-160) with HMAC-SHA256 signing,
 *   1440-min expiry, 120-min refresh window. Replaced by Cognito user pool
 *   authentication via the AuthHandler Lambda.
 * - `SecurityManager.cs` — User/role CRUD: `GetUser(Guid)`, `GetUser(email)`,
 *   `GetUser(email, password)` with MD5 hash (`CryptoUtility.ComputeOddMD5Hash`),
 *   role management, user creation/update/deletion via EQL queries against
 *   PostgreSQL. Replaced by UserHandler + RoleHandler Lambdas with DynamoDB
 *   persistence and Cognito user management.
 * - `SecurityContext.cs` — AsyncLocal-based user scoping with `OpenScope(user)`
 *   and `OpenSystemScope()`, role-based permission checks via
 *   `IsUserInRole(roles)`. Replaced by JWT claims extracted from API Gateway
 *   event context in Lambda handlers.
 * - `Definitions.cs` — `SystemIds` class with hardcoded GUIDs:
 *   `SystemUserId`, `AdministratorRoleId`, `RegularRoleId`, `GuestRoleId`,
 *   `FirstUserId`. `EntityPermission` enum (Read/Create/Update/Delete).
 *   System roles map to Cognito groups: admin, regular, guest.
 * - `JwtMiddleware.cs` — Bearer token extraction and validation from HTTP
 *   Authorization header. Replaced by API Gateway JWT authorizer with
 *   custom Lambda authorizer fallback for LocalStack.
 * - `ErpMiddleware.cs` — Per-request DB context creation + security scope
 *   binding via `SecurityContext.OpenScope(user)`. Replaced by per-invocation
 *   Lambda context with JWT claims.
 * - `JwtTokenModels.cs` — Login/token DTOs (`JwtTokenLoginModel`,
 *   `JwtTokenModel`). Replaced by Cognito token flows.
 * - `Startup.cs` (lines 88-125) — Dual auth scheme: Cookie + JWT Bearer
 *   with policy-based forwarding (`JWT_OR_COOKIE`). Replaced by Cognito
 *   user pool configuration in SharedStack.
 *
 * **Target architecture:**
 * - DynamoDB table for user/role persistence (single-table design)
 * - 3 Lambda functions for auth, user CRUD, and role management
 * - Cognito integration for authentication flows
 * - SNS domain events for cross-service communication
 * - SSM Parameter Store for resource discovery
 *
 * Resources created:
 *
 * 1. **DynamoDB Table** (`erp-identity-main`) — Single-table design storing
 *    all identity entities. Partition key patterns:
 *    - `USER#{userId}` — User records (email, firstName, lastName, username,
 *      enabled, image, createdOn, lastModifiedOn from SecurityManager.cs)
 *    - `ROLE#{roleId}` — Role records (name, description from SecurityManager.cs)
 *    - `USER_ROLE#{userId}` — User-role membership records (SK=ROLE#{roleId})
 *    - `SESSION#{sessionId}` — Active session tracking
 *    Sort key patterns: `META` for main records, `ROLE#{roleId}` for
 *    membership entries, `SESSION#{token}` for session data.
 *    GSI1: `GSI1PK`/`GSI1SK` — For email-based user lookups and role queries:
 *      GSI1PK=EMAIL#{email}, GSI1SK=USER#{userId} → Find user by email
 *      GSI1PK=ROLE#{roleName}, GSI1SK=USER#{userId} → Users in role
 *      (Replaces SecurityManager.GetUser(email) EQL queries and
 *       SecurityContext.IsUserInRole() permission checks)
 *
 * 2. **Lambda Functions** (3 handlers, .NET 9 Native AOT):
 *    - `webvella-erp-identity-auth` (512 MB, 30s) — Authentication operations:
 *      login/logout/token-refresh via Cognito. Replaces AuthService.cs cookie+JWT
 *      auth with Cognito-backed authentication flows.
 *    - `webvella-erp-identity-user` (512 MB, 30s) — User CRUD operations.
 *      Replaces SecurityManager.cs user management (GetUser, CreateUser,
 *      UpdateUser, DeleteUser) with Cognito + DynamoDB backing.
 *    - `webvella-erp-identity-role` (512 MB, 30s) — Role management operations.
 *      Replaces SecurityManager.cs role CRUD with Cognito group management
 *      + DynamoDB role metadata persistence.
 *
 * 3. **SSM Parameters**:
 *    - `/webvella-erp/identity/table-name` → DynamoDB table name
 *    - `/webvella-erp/identity/auth-function-arn` → Auth Lambda ARN
 *
 * Domain events published to the shared SNS event bus:
 * - `identity.auth.login` — User successfully authenticated
 * - `identity.auth.logout` — User session terminated
 * - `identity.user.created` — New user created
 * - `identity.user.updated` — User profile updated
 * - `identity.user.deleted` — User deleted
 * - `identity.role.created` — New role created
 * - `identity.role.updated` — Role updated
 * - `identity.role.deleted` — Role deleted
 *
 * Source files referenced:
 * - WebVella.Erp.Web/Services/AuthService.cs — Cookie+JWT authentication
 * - WebVella.Erp/Api/SecurityManager.cs — User/role CRUD with MD5 passwords
 * - WebVella.Erp/Api/SecurityContext.cs — AsyncLocal user scoping
 * - WebVella.Erp/Api/Definitions.cs — SystemIds, EntityPermission enum
 * - WebVella.Erp.Web/Middleware/JwtMiddleware.cs — JWT token validation
 * - WebVella.Erp.Web/Middleware/ErpMiddleware.cs — Per-request security scope
 * - WebVella.Erp.Web/Models/JwtTokenModels.cs — Login/token DTOs
 * - WebVella.Erp.Site/Startup.cs — Dual auth scheme configuration
 *
 * @module infra/src/stacks/identity-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';

import {
  WebVellaLambdaService,
  LambdaRuntime,
  WebVellaDynamoDBTable,
  GsiDefinition,
} from '../constructs';

// ---------------------------------------------------------------------------
// Interface: IdentityStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the IdentityStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP §0.7.6), a reference to the Cognito user pool from SharedStack
 * (AAP §0.7.5), and the shared domain event bus from SharedStack
 * (AAP §0.7.2).
 */
export interface IdentityStackProps extends cdk.StackProps {
  /**
   * Whether this stack targets LocalStack (true) or production AWS (false).
   *
   * Derived from CDK context: `this.node.tryGetContext('localstack') === 'true'`
   * Controls conditional resource creation per AAP §0.7.6:
   * - Removal policies: DESTROY (LocalStack) vs RETAIN (production)
   * - Lambda tracing, architecture, and log retention
   * - AWS_ENDPOINT_URL injection for SDK redirects
   * - DynamoDB point-in-time recovery: disabled (LocalStack) vs enabled (production)
   */
  readonly isLocalStack: boolean;

  /**
   * Cognito User Pool reference from SharedStack.
   *
   * The 3 Lambda handlers (AuthHandler, UserHandler, RoleHandler) interact
   * with this user pool for authentication, user management, and group
   * management operations. Replaces the monolith's SecurityManager.cs
   * user/role CRUD and AuthService.cs cookie+JWT authentication per
   * AAP §0.7.5 authentication migration:
   * - AuthHandler: AdminInitiateAuth for login, GlobalSignOut for logout
   * - UserHandler: AdminCreateUser, AdminGetUser, AdminUpdateUserAttributes,
   *   AdminDeleteUser for user CRUD
   * - RoleHandler: CreateGroup, DeleteGroup, AdminAddUserToGroup,
   *   AdminRemoveUserFromGroup for role management
   *
   * System roles from Definitions.cs map to Cognito groups:
   * - AdministratorRoleId → 'admin' group
   * - RegularRoleId → 'regular' group
   * - GuestRoleId → 'guest' group
   */
  readonly userPool: cognito.IUserPool;

  /**
   * Cognito User Pool Client reference from SharedStack.
   * Required by CognitoService for authentication flows (AdminInitiateAuth).
   */
  readonly userPoolClientId: string;

  /**
   * Central SNS topic serving as the domain event bus.
   *
   * Passed from SharedStack. The AuthHandler, UserHandler, and RoleHandler
   * Lambda functions publish domain events to this topic using the naming
   * convention from AAP §0.8.5: `identity.{entity}.{action}`.
   *
   * Events published:
   * - `identity.auth.login` — Successful authentication
   * - `identity.auth.logout` — Session terminated
   * - `identity.user.created` — New user created
   * - `identity.user.updated` — User profile updated
   * - `identity.user.deleted` — User deleted
   * - `identity.role.created` — New role created
   * - `identity.role.updated` — Role updated
   * - `identity.role.deleted` — Role deleted
   *
   * Replaces the monolith's synchronous IErpPostCreateRecordHook and
   * IErpPostUpdateRecordHook invocations from HookManager with
   * asynchronous SNS event publishing per AAP §0.7.2 hook-to-event
   * migration strategy.
   */
  readonly eventBus: sns.ITopic;
}

// ---------------------------------------------------------------------------
// Class: IdentityStack
// ---------------------------------------------------------------------------

/**
 * IdentityStack — CDK stack for the Identity & Access Management bounded context.
 *
 * This stack is self-contained per AAP §0.8.1: it owns its own DynamoDB
 * table, Lambda functions, IAM policies, and SSM parameters. No other
 * service may directly access the identity service's datastore.
 *
 * The stack exposes three public properties consumed by other stacks:
 * - `functions` — Array of Lambda function references for API Gateway routes
 * - `tableName` — DynamoDB table name (also published as SSM parameter)
 * - `authFunctionArn` — Auth Lambda ARN for authorizer configuration
 */
export class IdentityStack extends cdk.Stack {
  /**
   * Array of Lambda function references for API Gateway route integration.
   *
   * Contains the AuthHandler, UserHandler, and RoleHandler functions that
   * handle all identity service HTTP endpoints. Consumed by ApiGatewayStack
   * for path-based routing under `/v1/identity/*` and `/v1/auth/*`.
   *
   * Index 0: AuthHandler — login/logout/token-refresh operations
   * Index 1: UserHandler — user CRUD operations
   * Index 2: RoleHandler — role management operations
   */
  public readonly functions: lambda.IFunction[];

  /**
   * DynamoDB table name for the identity service datastore.
   *
   * Follows the naming pattern generated by WebVellaDynamoDBTable as
   * `{serviceName}-{tableName}`. Also published as SSM parameter at
   * `/webvella-erp/identity/table-name` for cross-service discovery.
   */
  public readonly tableName: string;

  /**
   * ARN of the AuthHandler Lambda function.
   *
   * Published as SSM parameter at `/webvella-erp/identity/auth-function-arn`
   * for cross-service discovery. Used by the custom Lambda authorizer
   * (services/authorizer) as a fallback for LocalStack environments where
   * the native API Gateway JWT authorizer is not available.
   */
  public readonly authFunctionArn: string;

  constructor(scope: Construct, id: string, props: IdentityStackProps) {
    super(scope, id, props);

    const { isLocalStack, userPool, userPoolClientId, eventBus } = props;

    // -----------------------------------------------------------------------
    // 1. DynamoDB Table — Single-table design for Identity & Access Management
    // -----------------------------------------------------------------------
    // Replaces the monolith's PostgreSQL user/role tables managed by
    // SecurityManager.cs and the `rec_user` / role-related tables from
    // DbRecordRepository.cs. All user and role data migrates from the
    // single PostgreSQL instance to this DynamoDB table per AAP §0.7.4.
    //
    // Single-table design access patterns:
    //
    //   PK=USER#{userId},         SK=META               → User record
    //     Fields: email, firstName, lastName, username, enabled, image,
    //     createdOn, lastModifiedOn, preferences
    //     (Source: SecurityManager.cs GetUser methods, ErpUser model)
    //
    //   PK=USER#{userId},         SK=ROLE#{roleId}      → User-role membership
    //     Enables fast role lookup per user for permission checks
    //     (Source: SecurityContext.IsUserInRole, SecurityManager role loading)
    //
    //   PK=ROLE#{roleId},         SK=META               → Role record
    //     Fields: name, description, createdOn
    //     (Source: SecurityManager.cs role management, Definitions.cs
    //      system roles: Administrator, Regular, Guest)
    //
    //   PK=SESSION#{sessionId},   SK=META               → Active session
    //     Fields: userId, token, createdAt, expiresAt, refreshedAt
    //     (Source: AuthService.cs JWT token tracking with 1440-min expiry)
    //
    // GSI1 — Email-based user lookups and role queries:
    //   GSI1PK=EMAIL#{email},     GSI1SK=USER#{userId}   → Find user by email
    //     (Replaces SecurityManager.GetUser(email) EQL query:
    //      SELECT *, $user_role.* FROM user WHERE email = @email)
    //   GSI1PK=ROLENAME#{name},   GSI1SK=USER#{userId}   → Users in a role
    //     (Replaces SecurityContext.IsUserInRole permission checks)

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
      {
        indexName: 'GSI2',
        partitionKey: {
          name: 'GSI2PK',
          type: dynamodb.AttributeType.STRING,
        },
        sortKey: {
          name: 'GSI2SK',
          type: dynamodb.AttributeType.STRING,
        },
      },
    ];

    const identityTable = new WebVellaDynamoDBTable(this, 'IdentityTable', {
      serviceName: 'erp-identity',
      tableName: 'main',
      isLocalStack,
      globalSecondaryIndexes: gsiDefinitions,
    });

    // -----------------------------------------------------------------------
    // 2. IAM Policy Statements — Least-privilege per AAP §0.8.3
    // -----------------------------------------------------------------------

    // DynamoDB CRUD permissions scoped to the identity table and its GSIs.
    // Covers all single-table access patterns for user, role, user-role
    // membership, and session entities. All 3 Lambda handlers (AuthHandler,
    // UserHandler, RoleHandler) require full CRUD access to the shared
    // identity table.
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
        identityTable.tableArn,
        `${identityTable.tableArn}/index/*`,
      ],
    });

    // SNS publish permission scoped to the shared event bus topic.
    // All 3 Lambda functions publish domain events following the naming
    // convention from AAP §0.8.5: `identity.{entity}.{action}`.
    // This replaces the monolith's synchronous HookManager post-hook
    // invocations for user/role lifecycle events (IErpPostCreateRecordHook,
    // IErpPostUpdateRecordHook, IErpPostDeleteRecordHook) with asynchronous
    // SNS event publishing per AAP §0.7.2.
    const snsPublishPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'sns:Publish',
      ],
      resources: [
        eventBus.topicArn,
      ],
    });

    // Cognito admin operations for authentication and user management.
    // Replaces SecurityManager.cs user CRUD methods that operated directly
    // against PostgreSQL via EQL queries. The Cognito operations enable:
    // - AuthHandler: AdminInitiateAuth (login with email+password replacing
    //   SecurityManager.GetUser(email, password) + MD5 hash validation),
    //   AdminRespondToAuthChallenge (MFA/password challenges),
    //   GlobalSignOut (logout replacing cookie sign-out from AuthService.cs),
    //   AdminUserGlobalSignOut (force logout)
    // - UserHandler: AdminCreateUser (replacing SecurityManager user creation),
    //   AdminGetUser (replacing SecurityManager.GetUser(email)),
    //   AdminUpdateUserAttributes (replacing user profile updates),
    //   AdminDeleteUser (replacing user deletion),
    //   AdminSetUserPassword (password management)
    // - RoleHandler: CreateGroup, DeleteGroup (replacing role CRUD from
    //   SecurityManager.cs), AdminAddUserToGroup, AdminRemoveUserFromGroup
    //   (replacing user-role association from the monolith's entity_relations
    //   join table for user↔role N:N relation), ListUsersInGroup,
    //   AdminListGroupsForUser (permission queries replacing
    //   SecurityContext.IsUserInRole checks)
    //
    // Per AAP §0.7.5: System roles from Definitions.cs map to Cognito groups:
    //   AdministratorRoleId (bdc56420-caf0-4030-8a0e-d264f6f47b04) → 'admin'
    //   RegularRoleId (f16ec6db-626d-4c27-8de0-3e7ce542c55f) → 'regular'
    //   GuestRoleId (987148b1-afa8-4b13-a840-58f0637f1684) → 'guest'
    const cognitoPolicy = new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: [
        'cognito-idp:AdminInitiateAuth',
        'cognito-idp:AdminRespondToAuthChallenge',
        'cognito-idp:AdminCreateUser',
        'cognito-idp:AdminGetUser',
        'cognito-idp:AdminUpdateUserAttributes',
        'cognito-idp:AdminDeleteUser',
        'cognito-idp:AdminSetUserPassword',
        'cognito-idp:AdminUserGlobalSignOut',
        'cognito-idp:GlobalSignOut',
        'cognito-idp:CreateGroup',
        'cognito-idp:DeleteGroup',
        'cognito-idp:AdminAddUserToGroup',
        'cognito-idp:AdminRemoveUserFromGroup',
        'cognito-idp:ListUsersInGroup',
        'cognito-idp:AdminListGroupsForUser',
      ],
      resources: [
        // In LocalStack Community (no Cognito), userPoolArn resolves to "unknown"
        // at deploy time. Use a wildcard ARN fallback in LocalStack mode to
        // prevent MalformedPolicyDocument errors from IAM.
        isLocalStack
          ? `arn:aws:cognito-idp:${this.region}:${this.account}:userpool/*`
          : userPool.userPoolArn,
      ],
    });

    // -----------------------------------------------------------------------
    // 3. Shared Lambda Environment Variables
    // -----------------------------------------------------------------------
    // All 3 Lambda handlers share the same environment configuration.
    // AWS_ENDPOINT_URL is only set when targeting LocalStack (isLocalStack)
    // to redirect AWS SDK calls to the LocalStack endpoint at
    // http://localhost:4566, per AAP §0.8.6 environment variable spec.

    const sharedEnvironment: Record<string, string> = {
      TABLE_NAME: identityTable.tableName,
      IDENTITY_TABLE_NAME: identityTable.tableName,
      COGNITO_USER_POOL_ID: userPool.userPoolId,
      COGNITO_CLIENT_ID: userPoolClientId,
      EVENT_TOPIC_ARN: eventBus.topicArn,
    };

    // Only inject AWS_ENDPOINT_URL for LocalStack environments.
    // In production, the AWS SDK uses default service endpoints.
    if (isLocalStack) {
      sharedEnvironment['AWS_ENDPOINT_URL'] = 'http://172.17.0.1:4566';
    }

    // Combined IAM policies for all identity Lambda handlers.
    // Each handler gets DynamoDB CRUD + Cognito admin + SNS publish.
    const allPolicies = [dynamoDbPolicy, snsPublishPolicy, cognitoPolicy];

    // -----------------------------------------------------------------------
    // 4. Lambda Functions — .NET 9 Native AOT handlers
    // -----------------------------------------------------------------------

    // 4a. AuthHandler — Authentication operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/auth/login                → Authenticate user (Cognito AdminInitiateAuth)
    //   POST   /v1/auth/logout               → Terminate session (Cognito GlobalSignOut)
    //   POST   /v1/auth/refresh              → Refresh access token
    //   GET    /v1/auth/me                   → Get current authenticated user profile
    //
    // Source mapping:
    //   AuthService.cs (lines 29-55) → Authenticate() method: cookie sign-in
    //     with SecurityManager.GetUser(email, password). Replaced by Cognito
    //     AdminInitiateAuth with USER_PASSWORD_AUTH flow.
    //   AuthService.cs (lines 83-160) → GetTokenAsync(), BuildTokenAsync():
    //     JWT generation with HMAC-SHA256, 1440-min expiry, 120-min refresh.
    //     Replaced by Cognito-issued JWT tokens (ID token + access token).
    //   JwtMiddleware.cs → Bearer token extraction and validation. Replaced
    //     by API Gateway JWT authorizer (native) or custom Lambda authorizer
    //     (LocalStack fallback).
    //   ErpMiddleware.cs → Per-request security scope creation via
    //     SecurityContext.OpenScope(user). Replaced by JWT claims in Lambda
    //     event context (requestContext.authorizer.jwt.claims).
    //
    // Publishes domain events:
    //   identity.auth.login — after successful authentication
    //   identity.auth.logout — after session termination
    //
    // Per AAP §0.7.5: Supports user migration from MD5-hashed passwords
    // via Cognito UserMigration_Authentication trigger on first login.

    const authHandler = new WebVellaLambdaService(this, 'AuthHandler', {
      serviceName: 'erp-identity',
      functionName: 'auth',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/identity/publish',
      handler: 'WebVellaErp.Identity::WebVellaErp.Identity.Functions.AuthHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Identity AuthHandler — login/logout/token-refresh via Cognito. ' +
        'Replaces AuthService.cs cookie+JWT auth and JwtMiddleware.cs. ' +
        'Publishes identity.auth.{login,logout} events to SNS.',
      environment: sharedEnvironment,
      additionalPolicies: allPolicies,
    });

    // 4b. UserHandler — User CRUD operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/identity/users            → Create user (Cognito AdminCreateUser + DynamoDB)
    //   GET    /v1/identity/users            → List users (DynamoDB scan/query)
    //   GET    /v1/identity/users/{userId}   → Get user details (DynamoDB + Cognito)
    //   PUT    /v1/identity/users/{userId}   → Update user (Cognito AdminUpdateUserAttributes + DynamoDB)
    //   DELETE /v1/identity/users/{userId}   → Delete user (Cognito AdminDeleteUser + DynamoDB)
    //   GET    /v1/identity/users/search     → Search users by email/name (GSI1 query)
    //
    // Source mapping:
    //   SecurityManager.cs → GetUser(Guid userId): EQL query
    //     `SELECT *, $user_role.* FROM user WHERE id = @id`
    //     Replaced by DynamoDB GetItem (PK=USER#{userId}, SK=META)
    //     followed by Query (PK=USER#{userId}, SK begins_with ROLE#)
    //   SecurityManager.cs → GetUser(string email): EQL query
    //     `SELECT *, $user_role.* FROM user WHERE email = @email`
    //     Replaced by DynamoDB GSI1 Query (GSI1PK=EMAIL#{email})
    //   SecurityManager.cs → User creation with MD5 hash:
    //     `CryptoUtility.ComputeOddMD5Hash(password)`. Replaced by
    //     Cognito AdminCreateUser (password managed by Cognito).
    //   Definitions.cs → SystemIds.SystemUserId, SystemIds.FirstUserId:
    //     System user (erp@webvella.com) seeded during bootstrap.
    //
    // Publishes domain events:
    //   identity.user.created — after successful user creation
    //   identity.user.updated — after successful user update
    //   identity.user.deleted — after successful user deletion

    const userHandler = new WebVellaLambdaService(this, 'UserHandler', {
      serviceName: 'erp-identity',
      functionName: 'user',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/identity/publish',
      handler: 'WebVellaErp.Identity::WebVellaErp.Identity.Functions.UserHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Identity UserHandler — user CRUD backed by Cognito + DynamoDB. ' +
        'Replaces SecurityManager.cs user management with MD5 migration. ' +
        'Publishes identity.user.{created,updated,deleted} events.',
      environment: sharedEnvironment,
      additionalPolicies: allPolicies,
    });

    // 4c. RoleHandler — Role management operations
    //
    // Handles HTTP endpoints:
    //   POST   /v1/identity/roles            → Create role (Cognito group + DynamoDB)
    //   GET    /v1/identity/roles            → List roles
    //   GET    /v1/identity/roles/{roleId}   → Get role details
    //   PUT    /v1/identity/roles/{roleId}   → Update role
    //   DELETE /v1/identity/roles/{roleId}   → Delete role
    //   POST   /v1/identity/roles/{roleId}/users/{userId}  → Assign user to role
    //   DELETE /v1/identity/roles/{roleId}/users/{userId}  → Remove user from role
    //   GET    /v1/identity/roles/{roleId}/users            → List users in role
    //
    // Source mapping:
    //   SecurityManager.cs → Role CRUD operations that stored role records
    //     in PostgreSQL. Roles are now dual-stored: Cognito groups for
    //     runtime authorization + DynamoDB for metadata and queries.
    //   Definitions.cs → System roles seeded as Cognito groups:
    //     AdministratorRoleId (bdc56420-caf0-4030-8a0e-d264f6f47b04) → admin
    //     RegularRoleId (f16ec6db-626d-4c27-8de0-3e7ce542c55f) → regular
    //     GuestRoleId (987148b1-afa8-4b13-a840-58f0637f1684) → guest
    //   SecurityContext.IsUserInRole() → Permission checks now performed by
    //     querying Cognito groups and DynamoDB user-role membership records.
    //
    // Publishes domain events:
    //   identity.role.created — after successful role creation
    //   identity.role.updated — after successful role update
    //   identity.role.deleted — after successful role deletion

    const roleHandler = new WebVellaLambdaService(this, 'RoleHandler', {
      serviceName: 'erp-identity',
      functionName: 'role',
      runtime: LambdaRuntime.DOTNET_9_AOT,
      codePath: '../services/identity/publish',
      handler: 'WebVellaErp.Identity::WebVellaErp.Identity.Functions.RoleHandler::FunctionHandler',
      isLocalStack,
      memorySize: 512,
      timeoutSeconds: 30,
      description:
        'Identity RoleHandler — role management with Cognito groups. ' +
        'Replaces SecurityManager.cs role CRUD and permission checks. ' +
        'Publishes identity.role.{created,updated,deleted} events.',
      environment: sharedEnvironment,
      additionalPolicies: allPolicies,
    });

    // -----------------------------------------------------------------------
    // 5. SSM Parameters — Resource discovery per AAP §0.8.6
    // -----------------------------------------------------------------------
    // Other services and bootstrap scripts use these parameters to locate
    // the identity service's datastore and auth function without hardcoded
    // names. Per AAP §0.8.3: all secrets via SSM Parameter Store SecureString
    // — never environment variables. Non-secret configuration uses standard
    // StringParameter.

    // SSM Parameter: DynamoDB table name for cross-service discovery.
    // Used by seed-test-data.sh for inserting default system user
    // (erp@webvella.com) and system roles (admin, regular, guest)
    // during LocalStack bootstrap.
    const tableNameParam = new ssm.StringParameter(this, 'IdentityTableNameParam', {
      parameterName: '/webvella-erp/identity/table-name',
      stringValue: identityTable.tableName,
      description:
        'DynamoDB table name for the Identity & Access Management service ' +
        'datastore. Used by bootstrap scripts and cross-service discovery.',
    });

    // Apply conditional removal policy for clean teardown in LocalStack
    // mode per AAP §0.7.6 dual-target strategy.
    tableNameParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN
    );

    // SSM Parameter: Auth Lambda function ARN for authorizer configuration.
    // Used by the custom Lambda authorizer (services/authorizer) as a
    // fallback authentication endpoint for LocalStack environments where
    // the native API Gateway JWT authorizer is not available.
    const authFunctionArnParam = new ssm.StringParameter(this, 'AuthFunctionArnParam', {
      parameterName: '/webvella-erp/identity/auth-function-arn',
      stringValue: authHandler.functionArn,
      description:
        'ARN of the Identity AuthHandler Lambda function. Used by the custom ' +
        'Lambda authorizer for LocalStack fallback and by CI/CD pipelines ' +
        'for deployment verification.',
    });

    // Apply conditional removal policy for clean teardown.
    authFunctionArnParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN
    );

    // -----------------------------------------------------------------------
    // 6. Public Property Assignments
    // -----------------------------------------------------------------------

    this.functions = [
      authHandler.function,
      userHandler.function,
      roleHandler.function,
    ];
    this.tableName = identityTable.tableName;
    this.authFunctionArn = authHandler.functionArn;

    // -----------------------------------------------------------------------
    // 7. Stack Outputs — Cross-stack references
    // -----------------------------------------------------------------------
    // These outputs are consumed by ApiGatewayStack for route integration,
    // by the custom Lambda authorizer for auth function discovery, and by
    // the CI/CD pipeline for deployment verification.

    new cdk.CfnOutput(this, 'IdentityTableName', {
      value: identityTable.tableName,
      description: 'DynamoDB table name for the Identity service',
      exportName: `${this.stackName}-TableName`,
    });

    new cdk.CfnOutput(this, 'IdentityTableArn', {
      value: identityTable.tableArn,
      description: 'DynamoDB table ARN for the Identity service',
      exportName: `${this.stackName}-TableArn`,
    });

    new cdk.CfnOutput(this, 'AuthHandlerFunctionArn', {
      value: authHandler.functionArn,
      description: 'ARN of the Identity AuthHandler Lambda function',
      exportName: `${this.stackName}-AuthHandlerArn`,
    });

    new cdk.CfnOutput(this, 'AuthHandlerFunctionName', {
      value: authHandler.functionName,
      description: 'Name of the Identity AuthHandler Lambda function',
      exportName: `${this.stackName}-AuthHandlerName`,
    });

    new cdk.CfnOutput(this, 'UserHandlerFunctionArn', {
      value: userHandler.functionArn,
      description: 'ARN of the Identity UserHandler Lambda function',
      exportName: `${this.stackName}-UserHandlerArn`,
    });

    new cdk.CfnOutput(this, 'UserHandlerFunctionName', {
      value: userHandler.functionName,
      description: 'Name of the Identity UserHandler Lambda function',
      exportName: `${this.stackName}-UserHandlerName`,
    });

    new cdk.CfnOutput(this, 'RoleHandlerFunctionArn', {
      value: roleHandler.functionArn,
      description: 'ARN of the Identity RoleHandler Lambda function',
      exportName: `${this.stackName}-RoleHandlerArn`,
    });

    new cdk.CfnOutput(this, 'RoleHandlerFunctionName', {
      value: roleHandler.functionName,
      description: 'Name of the Identity RoleHandler Lambda function',
      exportName: `${this.stackName}-RoleHandlerName`,
    });

    new cdk.CfnOutput(this, 'FunctionCount', {
      value: String(this.functions.length),
      description: 'Number of Lambda functions in the Identity stack',
      exportName: `${this.stackName}-FunctionCount`,
    });

    // -----------------------------------------------------------------------
    // 8. Resource Tags — Service identification per AAP §0.8.5
    // -----------------------------------------------------------------------
    // Tags applied at the stack level propagate to all child resources,
    // enabling cost allocation, operational visibility, and automated
    // discovery across the WebVella ERP microservices fleet.

    cdk.Tags.of(this).add('service', 'identity');
    cdk.Tags.of(this).add('domain', 'identity');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }
}
