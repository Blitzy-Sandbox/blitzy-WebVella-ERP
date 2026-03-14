/**
 * ApiGatewayStack — HTTP API Gateway v2 with Route-to-Lambda Mapping.
 *
 * This CDK stack defines the HTTP API Gateway v2 as the single entry point
 * for all WebVella ERP API requests, replacing the monolith's single
 * WebApiController.cs (4300+ lines, 100+ endpoints) with path-based routing
 * to per-domain Lambda functions across 10 bounded-context services.
 *
 * **Source systems replaced:**
 * - `WebApiController.cs` — Single API controller with 100+ endpoints organized
 *   by domain (EQL, entities, relations, records, files, plugins, jobs, auth).
 *   Each endpoint group maps to a bounded-context Lambda integration:
 *   - EQL query endpoints (lines 63-188) → Entity Management Lambda
 *   - Entity metadata CRUD (lines 1437-2008) → Entity Management Lambda
 *   - Relation CRUD (lines 2009-2105) → Entity Management Lambda
 *   - Record CRUD (lines 2504-3018) → Entity Management Lambda
 *   - File operations (lines 3252-3401) → File Management Lambda
 *   - Plugin list (line 3403) → Plugin System Lambda
 *   - Job/schedule operations (lines 3420-3815) → Workflow Lambda
 *   - System log (line 3817) → Reporting Lambda
 *   - User file operations (lines 3886-4133) → File Management Lambda
 *   - Auth JWT endpoints (lines 4274-4294) → Identity Lambda
 *
 * - `ApiControllerBase.cs` — [Authorize] class-level attribute providing
 *   default authentication for all endpoints. Replaced by API Gateway JWT
 *   authorizer applied to all routes except /v1/auth/login and /v1/auth/refresh.
 *
 * - `JwtMiddleware.cs` (67 lines) — Bearer token extraction from Authorization
 *   header and validation via AuthService.GetValidSecurityTokenAsync(). Replaced
 *   by native HTTP API v2 JWT authorizer (production) or custom Lambda authorizer
 *   (LocalStack fallback) per AAP section 0.7.6.
 *
 * - `AuthService.cs` (169 lines) — JWT validation with HMAC-SHA256, claims
 *   extraction (NameIdentifier, Email, Roles), 1440-min expiry, 120-min refresh.
 *   Replaced by Cognito JWT token validation at API Gateway level.
 *
 * - `Startup.cs` (lines 52-64) — CORS configuration: AllowAnyOrigin(),
 *   AllowAnyMethod(), AllowAnyHeader(). Replicated in HTTP API v2 CORS
 *   preflight configuration with explicit method and header allowlists.
 *   (lines 88-125) — Authentication scheme configuration: Cookie + JWT Bearer
 *   with JWT_OR_COOKIE policy-based forwarding. Replaced by API Gateway JWT
 *   authorizer with Cognito user pool integration.
 *
 * **Target architecture (per AAP):**
 * - HTTP API v2 (not REST API v1) for lower latency (section 0.4.2)
 * - Path-based versioning with /v1/ prefix (section 0.8.6)
 * - Dual-mode JWT authorizer (section 0.7.6):
 *   - Production: Native Cognito JWT authorizer on HTTP API v2
 *   - LocalStack: Custom Lambda authorizer (Node.js 22) fallback
 * - Per-domain Lambda integrations for all 10 bounded-context services
 * - CORS with explicit method and header allowlists
 * - Access logging to CloudWatch with structured JSON format (production)
 * - API URL stored in SSM for frontend build discovery
 *
 * Resources created:
 * 1. HTTP API v2 (webvella-erp-api) with CORS preflight
 * 2. JWT Authorizer (Cognito native or Lambda custom)
 * 3. Custom Lambda Authorizer function (LocalStack mode only)
 * 4. Route-to-Lambda integrations for all 10 services
 * 5. CloudWatch access log group
 * 6. SSM Parameter for API URL discovery
 * 7. Health check endpoint (direct HttpLambdaIntegration)
 *
 * @module infra/src/stacks/api-gateway-stack
 */

import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as apigatewayv2 from 'aws-cdk-lib/aws-apigatewayv2';
import * as apigatewayv2Integrations from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as apigatewayv2Authorizers from 'aws-cdk-lib/aws-apigatewayv2-authorizers';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as ssm from 'aws-cdk-lib/aws-ssm';
import * as logs from 'aws-cdk-lib/aws-logs';

import {
  WebVellaApiIntegration,
  RouteDefinition,
  WebVellaApiIntegrationProps,
  WebVellaLambdaService,
  LambdaRuntime,
} from '../constructs';

// ---------------------------------------------------------------------------
// Interface: ApiGatewayStackProps
// ---------------------------------------------------------------------------

/**
 * Configuration properties for the ApiGatewayStack.
 *
 * Extends standard CDK StackProps with the dual-target deployment flag
 * (AAP section 0.7.6), Cognito user pool reference for JWT authorization,
 * and Lambda function references from all 10 bounded-context service stacks.
 *
 * Property names match the app.ts wiring:
 *   new ApiGatewayStack(app, 'WebVellaErpApiGateway', {
 *     isLocalStack, userPool: sharedStack.userPool,
 *     identityFunctions: identityStack.functions, ...
 *   });
 */
export interface ApiGatewayStackProps extends cdk.StackProps {
  /** Whether this stack targets LocalStack (true) or production AWS (false). */
  readonly isLocalStack: boolean;

  /** Cognito User Pool reference from SharedStack for JWT authorization. */
  readonly userPool: cognito.IUserPool;

  /**
   * Identity service Lambda functions from IdentityStack.
   * [0] = AuthHandler (login/logout/refresh/me)
   * [1] = UserHandler (user CRUD)
   * [2] = RoleHandler (role management)
   */
  readonly identityFunctions: lambda.IFunction[];

  /**
   * Entity Management service Lambda functions from EntityManagementStack.
   * Handles entity metadata, relations, records, datasources, search, EQL,
   * and import/export — the largest set of endpoints migrated from
   * WebApiController.cs (lines 63-3018).
   */
  readonly entityManagementFunctions: lambda.IFunction[];

  /**
   * CRM service Lambda functions from CrmStack.
   * Handles account and contact CRUD, replacing NextPlugin entity patches
   * (NextPlugin.20190204.cs, NextPlugin.20190206.cs).
   */
  readonly crmFunctions: lambda.IFunction[];

  /**
   * Inventory / Products service Lambda functions from InventoryStack.
   * Handles task and timelog CRUD, replacing ProjectPlugin services.
   */
  readonly inventoryFunctions: lambda.IFunction[];

  /**
   * Invoicing / Billing service Lambda functions from InvoicingStack.
   * Handles invoice and payment operations with ACID transactions
   * backed by RDS PostgreSQL.
   */
  readonly invoicingFunctions: lambda.IFunction[];

  /**
   * Reporting & Analytics service Lambda functions from ReportingStack.
   * [0] = ReportHandler (API-facing report generation)
   * [1] = EventConsumer (SQS-triggered, not API-facing)
   */
  readonly reportingFunctions: lambda.IFunction[];

  /**
   * Notifications service Lambda functions from NotificationsStack.
   * [0] = EmailHandler (API-facing email operations)
   * [1] = WebhookHandler (webhook dispatch)
   * [2] = QueueProcessor (SQS-triggered, not API-facing)
   */
  readonly notificationsFunctions: lambda.IFunction[];

  /**
   * File Management service Lambda functions from FileManagementStack.
   * Handles S3 upload/download operations, replacing DbFileRepository
   * (LO/filesystem/Storage.Net backends) and /fs/* endpoints.
   */
  readonly fileManagementFunctions: lambda.IFunction[];

  /**
   * Workflow Engine service Lambda functions from WorkflowStack.
   * [0] = WorkflowHandler (API-facing workflow initiation)
   * [1] = StepHandler (Step Functions-triggered, not API-facing)
   */
  readonly workflowFunctions: lambda.IFunction[];

  /**
   * Plugin / Extension System service Lambda functions from PluginSystemStack.
   * Handles plugin registration and listing, replacing ErpPlugin abstract
   * base and WebApiController.cs plugin endpoints (line 3403).
   */
  readonly pluginSystemFunctions: lambda.IFunction[];
}

// ---------------------------------------------------------------------------
// Class: ApiGatewayStack
// ---------------------------------------------------------------------------

/**
 * ApiGatewayStack — CDK stack for the HTTP API Gateway v2.
 *
 * Replaces the monolith's single WebApiController.cs (100+ endpoints in one
 * controller) with an HTTP API Gateway v2 that routes requests to per-domain
 * Lambda functions across 10 bounded-context services. Uses path-based
 * versioning with /v1/ prefix and JWT authorization.
 *
 * Exposes two public properties consumed by other stacks:
 * - `apiUrl` — HTTP API endpoint URL (consumed by FrontendStack via VITE_API_URL)
 * - `apiId` — HTTP API ID for cross-stack references
 */
export class ApiGatewayStack extends cdk.Stack {
  /**
   * HTTP API endpoint URL.
   * Consumed by FrontendStack for VITE_API_URL configuration.
   * Also stored in SSM at /webvella-erp/api/url for build-time discovery.
   */
  public readonly apiUrl: string;

  /**
   * HTTP API Gateway ID.
   * Exported as stack output for cross-stack references and CI/CD verification.
   */
  public readonly apiId: string;

  constructor(scope: Construct, id: string, props: ApiGatewayStackProps) {
    super(scope, id, props);

    const { isLocalStack, userPool } = props;

    // -------------------------------------------------------------------
    // 1. CloudWatch Access Log Group
    // -------------------------------------------------------------------
    // Structured JSON request/response logging with correlation-ID
    // propagation per AAP section 0.8.5 operational requirements.
    // - LocalStack: ONE_MONTH retention for development/testing
    // - Production: THREE_MONTHS retention for audit trail

    const accessLogGroup = new logs.LogGroup(this, 'ApiAccessLogs', {
      logGroupName: '/webvella-erp/api-gateway/access-logs',
      retention: isLocalStack
        ? logs.RetentionDays.ONE_MONTH
        : logs.RetentionDays.THREE_MONTHS,
      removalPolicy: isLocalStack
        ? cdk.RemovalPolicy.DESTROY
        : cdk.RemovalPolicy.RETAIN,
    });

    // -------------------------------------------------------------------
    // 2. HTTP API v2 — Single Entry Point
    // -------------------------------------------------------------------
    // HTTP API v2 (not REST API v1) per AAP section 0.4.2 for lower latency.
    // Replaces the monolith's ASP.NET Core Kestrel server.
    //
    // CORS configuration replaces Startup.cs (lines 52-64):
    //   Original: AllowAnyOrigin(), AllowAnyMethod(), AllowAnyHeader()
    //   Target: Explicit method/header allowlists with all origins.
    //   Per AAP section 0.8.3, production deployments should restrict
    //   origins to the known frontend domain.

    const corsPreflightOptions: apigatewayv2.CorsPreflightOptions = {
      allowOrigins: ['*'],
      allowMethods: [
        apigatewayv2.CorsHttpMethod.GET,
        apigatewayv2.CorsHttpMethod.POST,
        apigatewayv2.CorsHttpMethod.PUT,
        apigatewayv2.CorsHttpMethod.PATCH,
        apigatewayv2.CorsHttpMethod.DELETE,
        apigatewayv2.CorsHttpMethod.OPTIONS,
      ],
      allowHeaders: [
        'Content-Type',
        'Authorization',
        'X-Correlation-Id',
        'X-Amz-Date',
        'X-Api-Key',
        'X-Amz-Security-Token',
      ],
      maxAge: cdk.Duration.seconds(3600),
    };

    const httpApi = new apigatewayv2.HttpApi(this, 'HttpApi', {
      apiName: 'webvella-erp-api',
      description:
        'WebVella ERP HTTP API Gateway v2 — routes requests to per-domain ' +
        'Lambda functions across 10 bounded-context microservices.',
      corsPreflight: corsPreflightOptions,
      disableExecuteApiEndpoint: false,
    });

    // Apply resource tags per AAP conventions
    cdk.Tags.of(httpApi).add('service', 'api-gateway');
    cdk.Tags.of(httpApi).add('environment', isLocalStack ? 'localstack' : 'production');
    cdk.Tags.of(httpApi).add('project', 'webvella-erp');

    // Configure structured JSON access logging on the default stage.
    // Uses L1 escape hatch because the L2 HttpApi construct does not
    // expose access logging configuration directly.
    if (!isLocalStack) {
      const defaultStage = httpApi.defaultStage?.node
        .defaultChild as apigatewayv2.CfnStage | undefined;
      if (defaultStage) {
        defaultStage.accessLogSettings = {
          destinationArn: accessLogGroup.logGroupArn,
          format: JSON.stringify({
            requestId: '$context.requestId',
            ip: '$context.identity.sourceIp',
            requestTime: '$context.requestTime',
            httpMethod: '$context.httpMethod',
            routeKey: '$context.routeKey',
            status: '$context.status',
            protocol: '$context.protocol',
            responseLength: '$context.responseLength',
            integrationError: '$context.integrationErrorMessage',
            correlationId: '$context.requestId',
          }),
        };
      }
    }

    // -------------------------------------------------------------------
    // 3. JWT Authorizer — Dual-Mode per AAP section 0.7.6
    // -------------------------------------------------------------------
    // Production: Native HTTP API v2 JWT authorizer with Cognito issuer.
    //   Replaces JwtMiddleware.cs Bearer token extraction and
    //   AuthService.cs HMAC-SHA256 validation with Cognito JWKS.
    //
    // LocalStack: Custom Lambda authorizer fallback because LocalStack
    //   may not fully support HTTP API v2 native JWT authorizer.
    //   Uses the Node.js 22 Lambda from services/authorizer/.

    let authorizer: apigatewayv2.IHttpRouteAuthorizer;

    if (isLocalStack) {
      // Create custom Lambda JWT authorizer for LocalStack environments
      const authorizerLambda = new WebVellaLambdaService(
        this,
        'CustomAuthorizer',
        {
          serviceName: 'erp-authorizer',
          functionName: 'jwt-validator',
          runtime: LambdaRuntime.NODEJS_22,
          codePath: '../services/authorizer/dist',
          handler: 'index.handler',
          isLocalStack,
          memorySize: 256,
          timeoutSeconds: 10,
          description:
            'Custom Lambda JWT authorizer for LocalStack environments. ' +
            'Validates JWT tokens from Cognito user pool as fallback for ' +
            'HTTP API v2 native JWT authorizer unsupported in LocalStack.',
          cognitoUserPoolId: userPool.userPoolId,
          environment: {
            COGNITO_USER_POOL_ID: userPool.userPoolId,
          },
        },
      );

      authorizer = new apigatewayv2Authorizers.HttpLambdaAuthorizer(
        'LambdaAuthorizer',
        authorizerLambda.function,
        {
          responseTypes: [
            apigatewayv2Authorizers.HttpLambdaResponseType.IAM,
          ],
          identitySource: ['$request.header.Authorization'],
          authorizerName: 'webvella-erp-lambda-authorizer',
        },
      );

      // Output the authorizer Lambda ARN for debugging
      new cdk.CfnOutput(this, 'AuthorizerFunctionArn', {
        value: authorizerLambda.functionArn,
        description:
          'Custom Lambda authorizer function ARN (LocalStack mode)',
        exportName: `${this.stackName}-AuthorizerArn`,
      });
    } else {
      // Production: Native Cognito JWT authorizer on HTTP API v2
      // Issuer URL derived from Cognito user pool provider URL
      const issuerUrl =
        `https://cognito-idp.${this.region}.amazonaws.com/` +
        `${userPool.userPoolId}`;

      authorizer = new apigatewayv2Authorizers.HttpJwtAuthorizer(
        'CognitoJwtAuthorizer',
        issuerUrl,
        {
          jwtAudience: [userPool.userPoolId],
          identitySource: ['$request.header.Authorization'],
          authorizerName: 'webvella-erp-jwt-authorizer',
        },
      );
    }

    // -------------------------------------------------------------------
    // 4. Helper — Create CRUD route definitions for standard HTTP methods
    // -------------------------------------------------------------------
    // HTTP API v2 uses explicit HTTP methods for route matching.
    // This helper generates RouteDefinition arrays for the five standard
    // CRUD methods (GET, POST, PUT, PATCH, DELETE) pointing to the same
    // Lambda handler, enabling catch-all service routing.

    const createCrudRoutes = (
      basePath: string,
      handler: lambda.IFunction,
      requireAuth: boolean = true,
    ): RouteDefinition[] => {
      const methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];
      const routes = methods.map((method) => ({
        method,
        path: basePath,
        handler,
        requireAuth,
      }));

      // Also create bare routes (without {proxy+}) for list endpoints.
      // API Gateway treats {proxy+} as requiring at least one path segment,
      // so bare GET/POST to the root path (e.g., /v1/roles) needs its own route.
      if (basePath.endsWith('/{proxy+}')) {
        const barePath = basePath.replace('/{proxy+}', '');
        routes.push(
          { method: 'GET', path: barePath, handler, requireAuth },
          { method: 'POST', path: barePath, handler, requireAuth },
        );
      }

      return routes;
    };

    // -------------------------------------------------------------------
    // 5. Health Check Endpoint — Direct HttpLambdaIntegration
    // -------------------------------------------------------------------
    // Lightweight health check that bypasses WebVellaApiIntegration.
    // Returns 200 OK for monitoring probes and load balancer checks.
    // Uses HttpLambdaIntegration directly for non-construct routes.

    const healthIntegration =
      new apigatewayv2Integrations.HttpLambdaIntegration(
        'HealthCheckIntegration',
        props.identityFunctions[0],
      );

    httpApi.addRoutes({
      path: '/v1/health',
      methods: [apigatewayv2.HttpMethod.GET],
      integration: healthIntegration,
    });

    // -------------------------------------------------------------------
    // 6. Route-to-Lambda Integrations per Bounded Context
    // -------------------------------------------------------------------
    // Each bounded context gets a WebVellaApiIntegration that maps route
    // patterns to Lambda handler functions from the service stacks.
    // Path-based versioning with /v1/ prefix per AAP section 0.8.6.
    // Each service's primary handler (functions[0]) handles all routes
    // with internal path-based dispatch.

    // --- 6a. Identity Service ---
    // Source: SecurityManager, AuthService JWT endpoints
    // AuthHandler (functions[0]): login/logout/refresh/me
    // UserHandler (functions[1]): user CRUD
    // RoleHandler (functions[2]): role management

    const identityRoutes: RouteDefinition[] = [
      // Auth endpoints — NO AUTH for login and refresh
      // Source: WebApiController.cs lines 4273-4309 [AllowAnonymous]
      {
        method: 'POST',
        path: '/v1/auth/login',
        handler: props.identityFunctions[0],
        requireAuth: false,
      },
      {
        method: 'POST',
        path: '/v1/auth/refresh',
        handler: props.identityFunctions[0],
        requireAuth: false,
      },
      // Auth endpoints — REQUIRE AUTH
      {
        method: 'POST',
        path: '/v1/auth/logout',
        handler: props.identityFunctions[0],
        requireAuth: true,
      },
      {
        method: 'GET',
        path: '/v1/auth/me',
        handler: props.identityFunctions[0],
        requireAuth: true,
      },
      // User CRUD routes — all standard methods
      ...createCrudRoutes('/v1/users/{proxy+}', props.identityFunctions[1]),
      // Role management routes — all standard methods
      ...createCrudRoutes('/v1/roles/{proxy+}', props.identityFunctions[2]),
    ];

    const identityIntegration = new WebVellaApiIntegration(
      this,
      'IdentityIntegration',
      {
        httpApi,
        serviceName: 'identity',
        authorizer,
        isLocalStack,
        routes: identityRoutes,
      } as WebVellaApiIntegrationProps,
    );

    // --- 6b. Entity Management Service ---
    // Source: WebApiController.cs entity meta (1437-2008), relation (2009-2105),
    //   record CRUD (2504-3018), EQL (63-188), datasource, search, import/export
    // Primary handler (functions[0]) receives all entity management routes.

    const entityManagementRoutes: RouteDefinition[] = [
      // EQL query endpoint — POST only
      // Source: api/v3/en_US/eql (lines 63-188)
      // Routed to SearchHandler [5] which owns query execution logic.
      {
        method: 'POST',
        path: '/v1/eql',
        handler: props.entityManagementFunctions[5],
        requireAuth: true,
      },
      // Entity metadata CRUD
      // Source: api/v3/en_US/meta/entity/* (lines 1437-2008)
      // EntityHandler [0] — entity/field metadata management.
      ...createCrudRoutes(
        '/v1/meta/entity/{proxy+}',
        props.entityManagementFunctions[0],
      ),
      // Relation metadata CRUD
      // Source: api/v3/en_US/meta/relation/* (lines 2009-2105)
      // RelationHandler [2] — relation metadata management.
      ...createCrudRoutes(
        '/v1/meta/relation/{proxy+}',
        props.entityManagementFunctions[2],
      ),
      // Record CRUD
      // Source: api/v3/en_US/record/* (lines 2504-3018)
      // RecordHandler [3] — record CRUD with domain event publishing.
      // RecordHandler also delegates /entities/{proxy+} entity/field
      // requests internally, but these legacy /v1/record paths must
      // reach it directly for proper record operations.
      ...createCrudRoutes(
        '/v1/record/{proxy+}',
        props.entityManagementFunctions[3],
      ),
      // DataSource operations
      // Source: api/v3.0/datasource/*
      // DataSourceHandler [4] — datasource registry and execution.
      ...createCrudRoutes(
        '/v1/datasource/{proxy+}',
        props.entityManagementFunctions[4],
      ),
      // Search endpoint — GET and POST
      // Source: api/v3/en_US/quick-search
      // SearchHandler [5] — full-text search operations.
      {
        method: 'GET',
        path: '/v1/search',
        handler: props.entityManagementFunctions[5],
        requireAuth: true,
      },
      {
        method: 'POST',
        path: '/v1/search',
        handler: props.entityManagementFunctions[5],
        requireAuth: true,
      },
      // Import/Export operations
      // Source: api/v3/en_US/record/*/import*
      // ImportExportHandler [6] — CSV import/export pipelines.
      ...createCrudRoutes(
        '/v1/import-export/{proxy+}',
        props.entityManagementFunctions[6],
      ),
    ];

    const entityManagementIntegration = new WebVellaApiIntegration(
      this,
      'EntityManagementIntegration',
      {
        httpApi,
        serviceName: 'entity-management',
        authorizer,
        isLocalStack,
        routes: entityManagementRoutes,
      } as WebVellaApiIntegrationProps,
    );

    // --- 6c. CRM / Contacts Service ---
    // Source: CrmPlugin, NextPlugin entity patches (account, contact, address)
    // Primary handler receives all CRM routes via proxy forwarding.

    const crmIntegration = new WebVellaApiIntegration(
      this,
      'CrmIntegration',
      {
        httpApi,
        serviceName: 'crm',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/crm/{proxy+}',
          props.crmFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6d. Inventory / Products Service ---
    // Source: ProjectPlugin services (task/timelog management)

    const inventoryIntegration = new WebVellaApiIntegration(
      this,
      'InventoryIntegration',
      {
        httpApi,
        serviceName: 'inventory',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/inventory/{proxy+}',
          props.inventoryFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6e. Invoicing / Billing Service ---
    // Source: RecordManager invoice workflows with ACID transactions (RDS PG)

    const invoicingIntegration = new WebVellaApiIntegration(
      this,
      'InvoicingIntegration',
      {
        httpApi,
        serviceName: 'invoicing',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/invoicing/{proxy+}',
          props.invoicingFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6f. Reporting & Analytics Service ---
    // Source: DataSourceManager + hook-based event consumption
    // Only ReportHandler (functions[0]) is API-facing.
    // EventConsumer (functions[1]) is SQS-triggered, not routed here.

    const reportingIntegration = new WebVellaApiIntegration(
      this,
      'ReportingIntegration',
      {
        httpApi,
        serviceName: 'reporting',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/reports/{proxy+}',
          props.reportingFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6g. Notifications Service ---
    // Source: MailPlugin SMTP engine, PostgreSQL LISTEN/NOTIFY replacement
    // Only EmailHandler (functions[0]) is API-facing.
    // WebhookHandler and QueueProcessor are event-triggered.

    const notificationsIntegration = new WebVellaApiIntegration(
      this,
      'NotificationsIntegration',
      {
        httpApi,
        serviceName: 'notifications',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/notifications/{proxy+}',
          props.notificationsFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6h. File Management Service ---
    // Source: DbFileRepository (LO/filesystem/blob → S3 migration)
    // Source: WebApiController.cs /fs/* endpoints (lines 3252-3401)

    const fileManagementIntegration = new WebVellaApiIntegration(
      this,
      'FileManagementIntegration',
      {
        httpApi,
        serviceName: 'file-management',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/files/{proxy+}',
          props.fileManagementFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6i. Workflow Engine Service ---
    // Source: JobManager, JobPool, SheduleManager → Step Functions
    // Only WorkflowHandler (functions[0]) is API-facing.
    // StepHandler (functions[1]) is Step Functions-triggered.

    const workflowIntegration = new WebVellaApiIntegration(
      this,
      'WorkflowIntegration',
      {
        httpApi,
        serviceName: 'workflow',
        authorizer,
        isLocalStack,
        routes: createCrudRoutes(
          '/v1/workflows/{proxy+}',
          props.workflowFunctions[0],
        ),
      } as WebVellaApiIntegrationProps,
    );

    // --- 6j. Plugin / Extension System Service ---
    // Source: ErpPlugin abstract base, WebApiController.cs plugin list (line 3403)

    const pluginSystemIntegration = new WebVellaApiIntegration(
      this,
      'PluginSystemIntegration',
      {
        httpApi,
        serviceName: 'plugin-system',
        authorizer,
        isLocalStack,
        routes: [
          ...createCrudRoutes(
            '/v1/plugins/{proxy+}',
            props.pluginSystemFunctions[0],
          ),
          // The frontend fetches the application list via GET /v1/apps.
          // PluginHandler routes /apps requests to app management logic
          // (HandleListApps, HandleGetApp, HandleCreateApp, etc.).
          // These routes point to the same plugin-system Lambda that handles
          // the /v1/plugins paths — PluginHandler dispatches based on the
          // normalized request path.
          ...createCrudRoutes(
            '/v1/apps/{proxy+}',
            props.pluginSystemFunctions[0],
          ),
        ],
      } as WebVellaApiIntegrationProps,
    );

    // -------------------------------------------------------------------
    // 7. Route Count Summary — Operational Visibility
    // -------------------------------------------------------------------
    // Track total routes across all integrations for operational monitoring
    // using routeCount and integrations from WebVellaApiIntegration.

    const allIntegrations = [
      { name: 'identity', integration: identityIntegration },
      { name: 'entity-management', integration: entityManagementIntegration },
      { name: 'crm', integration: crmIntegration },
      { name: 'inventory', integration: inventoryIntegration },
      { name: 'invoicing', integration: invoicingIntegration },
      { name: 'reporting', integration: reportingIntegration },
      { name: 'notifications', integration: notificationsIntegration },
      { name: 'file-management', integration: fileManagementIntegration },
      { name: 'workflow', integration: workflowIntegration },
      { name: 'plugin-system', integration: pluginSystemIntegration },
    ];

    // Calculate total route count across all service integrations
    const totalRouteCount = allIntegrations.reduce(
      (sum, entry) => sum + entry.integration.routeCount,
      0,
    );

    // Validate all integrations have routes — each bounded context must
    // have at least one route integration per AAP section 0.8.1
    for (const { name, integration } of allIntegrations) {
      if (integration.integrations.size === 0) {
        throw new Error(
          `ApiGatewayStack: No routes configured for service '${name}'. ` +
            'Each bounded context must have at least one route integration.',
        );
      }
    }

    // -------------------------------------------------------------------
    // 8. SSM Parameter — API URL Discovery (AAP section 0.8.6)
    // -------------------------------------------------------------------
    // Frontend build process reads this SSM parameter value via VITE_API_URL
    // environment variable to configure the React SPA API client base URL.

    const apiUrlParam = new ssm.StringParameter(this, 'ApiUrlParam', {
      parameterName: '/webvella-erp/api/url',
      stringValue: httpApi.apiEndpoint,
      description:
        'HTTP API Gateway endpoint URL for the WebVella ERP API. ' +
        'Used by frontend build via VITE_API_URL environment variable.',
    });

    apiUrlParam.applyRemovalPolicy(
      isLocalStack ? cdk.RemovalPolicy.DESTROY : cdk.RemovalPolicy.RETAIN,
    );

    // -------------------------------------------------------------------
    // 9. Public Property Assignments
    // -------------------------------------------------------------------

    this.apiUrl = httpApi.apiEndpoint;
    this.apiId = httpApi.apiId;

    // -------------------------------------------------------------------
    // 10. Stack Outputs — Cross-Stack References
    // -------------------------------------------------------------------
    // Consumed by FrontendStack (apiUrl) for configuring the React SPA,
    // and by CI/CD pipelines for deployment verification.

    new cdk.CfnOutput(this, 'ApiUrl', {
      value: httpApi.apiEndpoint,
      description: 'HTTP API Gateway endpoint URL',
      exportName: `${this.stackName}-ApiUrl`,
    });

    new cdk.CfnOutput(this, 'ApiId', {
      value: httpApi.apiId,
      description: 'HTTP API Gateway ID',
      exportName: `${this.stackName}-ApiId`,
    });

    new cdk.CfnOutput(this, 'TotalRouteCount', {
      value: totalRouteCount.toString(),
      description:
        'Total number of API routes across all service integrations',
      exportName: `${this.stackName}-TotalRouteCount`,
    });

    new cdk.CfnOutput(this, 'ApiLogGroupArn', {
      value: accessLogGroup.logGroupArn,
      description: 'CloudWatch log group ARN for API access logging',
      exportName: `${this.stackName}-ApiLogGroupArn`,
    });
  }
}
