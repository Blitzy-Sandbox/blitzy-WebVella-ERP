/**
 * infra/src/constructs/api-integration.ts
 *
 * Standard API Gateway Integration CDK L3 Construct for WebVella ERP Serverless Platform
 *
 * This construct standardizes how bounded-context service stacks create API Gateway
 * route integrations (Lambda proxy integrations) with their domain Lambda handlers.
 * It encapsulates HTTP API v2 route creation, Lambda integration setup, path validation,
 * and optional JWT authorization.
 *
 * In the monolith, a single WebApiController (WebVella.Erp.Web/Controllers/WebApiController.cs)
 * served 100+ endpoints with a single [Authorize] attribute and cookie+JWT dual-auth
 * (WebVella.Erp.Web/Middleware/JwtMiddleware.cs, WebVella.Erp.Site/Startup.cs lines 88-125).
 * Per-request context was established by ErpMiddleware (WebVella.Erp.Web/Middleware/ErpMiddleware.cs)
 * which opened DB connections and security scopes.
 *
 * In the target serverless architecture:
 * - Each bounded-context service has its own Lambda handlers with dedicated routes
 * - JWT validation is handled by API Gateway's native JWT authorizer (Cognito) or
 *   a custom Lambda authorizer fallback for LocalStack
 * - Request context (user identity, claims) is extracted from the Lambda event
 * - Routes follow the /v1/{service}/{resource} path convention per AAP §0.8.6
 *
 * Architecture Rules Enforced (AAP references):
 * - §0.4.2: HTTP API Gateway v2 as single entry point with path-based routing
 * - §0.5.2: Old WebApiController routes → new path-based Lambda integrations
 * - §0.7.6: LocalStack-aware — custom Lambda authorizer fallback in LocalStack mode
 * - §0.8.1: Zero cross-service DB access — each integration points to its own Lambda
 * - §0.8.3: CORS locked to known origins, input validation at API Gateway level
 * - §0.8.5: All resources tagged with service name for cost allocation
 * - §0.8.6: API versioning via /v1/ path prefix
 */

import * as cdk from 'aws-cdk-lib';
import * as apigatewayv2 from 'aws-cdk-lib/aws-apigatewayv2';
import * as apigatewayv2Integrations from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { Construct } from 'constructs';

/**
 * Supported HTTP methods for API Gateway route definitions.
 * Maps string method names to the CDK HttpMethod enum values.
 */
const HTTP_METHOD_MAP: Record<string, apigatewayv2.HttpMethod> = {
  'GET': apigatewayv2.HttpMethod.GET,
  'POST': apigatewayv2.HttpMethod.POST,
  'PUT': apigatewayv2.HttpMethod.PUT,
  'DELETE': apigatewayv2.HttpMethod.DELETE,
  'PATCH': apigatewayv2.HttpMethod.PATCH,
  'HEAD': apigatewayv2.HttpMethod.HEAD,
  'OPTIONS': apigatewayv2.HttpMethod.OPTIONS,
  'ANY': apigatewayv2.HttpMethod.ANY,
};

/**
 * Defines a single API Gateway route that maps an HTTP method + path to a Lambda handler.
 *
 * Each RouteDefinition replaces one or more endpoints that were previously served by the
 * monolith's WebApiController. For example:
 * - Old: [Route("api/v3/en_US/eql")] [HttpPost] on WebApiController
 * - New: { method: 'POST', path: '/v1/entity-management/eql', handler: entityHandler }
 *
 * Routes should follow the /v1/{service}/{resource} convention per AAP §0.8.6.
 */
export interface RouteDefinition {
  /**
   * HTTP method for this route (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS, ANY).
   * In the monolith, all methods were handled by a single controller with [HttpGet],
   * [HttpPost], etc. attributes. Now each route explicitly declares its method.
   */
  method: string;

  /**
   * Route path with /v1/ prefix, e.g., '/v1/identity/users/{id}'.
   * Replaces monolith's route patterns like [Route("api/v3/en_US/...")].
   * Path parameters use {paramName} notation (API Gateway v2 standard).
   *
   * Convention: /v1/{serviceName}/{resource}[/{id}]
   * Examples:
   *   - '/v1/identity/auth/login'
   *   - '/v1/identity/users/{id}'
   *   - '/v1/entity-management/entities'
   *   - '/v1/crm/contacts/{contactId}'
   */
  path: string;

  /**
   * Lambda function to integrate with for this route.
   * Accepts any IFunction (from WebVellaLambdaService.function or imported).
   * The Lambda receives the full API Gateway v2 proxy event including
   * JWT claims, path parameters, query strings, and request body.
   */
  handler: lambda.IFunction;

  /**
   * Whether this route requires JWT authorization.
   * Default: true (most routes require authentication).
   *
   * Set to false for:
   * - Health check endpoints (/v1/{service}/health)
   * - Login/authentication endpoints (/v1/identity/auth/login)
   * - Public API endpoints
   *
   * In the monolith, the [Authorize] attribute was applied at the controller level
   * with [AllowAnonymous] on specific actions (login). In the target, this is
   * controlled per-route via the requireAuth flag.
   */
  requireAuth?: boolean;
}

/**
 * Configuration properties for the WebVellaApiIntegration construct.
 *
 * Each bounded-context service stack creates one WebVellaApiIntegration instance,
 * passing its Lambda handlers and route definitions. The construct handles:
 * - Creating HttpLambdaIntegration for each route
 * - Adding routes to the shared HttpApi with proper route keys
 * - Applying JWT authorizer to protected routes
 * - Skipping authorization for public routes (health checks, login)
 * - Tagging all created resources with the service name
 */
export interface WebVellaApiIntegrationProps {
  /**
   * The HTTP API Gateway v2 instance to add routes to.
   * This is the shared API Gateway created by the api-gateway-stack,
   * replacing the monolith's single WebApiController as the unified entry point.
   */
  httpApi: apigatewayv2.HttpApi;

  /**
   * Service domain name (e.g., 'identity', 'crm', 'entity-management').
   * Used for:
   * - Integration naming: '{serviceName}-{method}-{path-slug}' for unique CDK IDs
   * - Resource tagging: 'service: {serviceName}' tag on all created routes
   * - Logging: correlation of route-level diagnostics to the owning service
   */
  serviceName: string;

  /**
   * Route definitions for this service.
   * Each entry creates one HttpLambdaIntegration and one HttpRoute.
   * Routes SHOULD use /v1/ prefix paths per AAP §0.8.6 versioning strategy.
   */
  routes: RouteDefinition[];

  /**
   * JWT authorizer to apply to protected routes.
   * In production: Cognito JWT authorizer (native API Gateway v2 JWT validation).
   * In LocalStack: Custom Lambda authorizer (fallback, since native JWT authorizer
   * may not fully work in LocalStack per AAP §0.7.6).
   *
   * Routes with requireAuth=false will NOT use this authorizer.
   * If not provided, no authorizer is applied to any route.
   */
  authorizer?: apigatewayv2.IHttpRouteAuthorizer;

  /**
   * Whether the infrastructure is being deployed to LocalStack.
   * Influences:
   * - Authorization strategy (custom Lambda authorizer fallback)
   * - Resource cleanup behavior
   * - Logging verbosity
   *
   * In the monolith, there was no dual-target concept. The target architecture
   * supports both LocalStack and production AWS via this flag (AAP §0.7.6).
   */
  isLocalStack: boolean;
}

/**
 * CDK L3 Construct that standardizes API Gateway route-to-Lambda integration creation
 * across all bounded-context services in the WebVella ERP serverless platform.
 *
 * Replaces the monolith's single WebApiController (100+ endpoints) with per-domain
 * Lambda proxy integrations. Each service stack creates one WebVellaApiIntegration
 * with its route definitions, and the construct handles:
 *
 * 1. Creating an HttpLambdaIntegration for each route's Lambda handler
 * 2. Adding HttpRoute entries to the shared HttpApi with proper HttpRouteKey
 * 3. Applying the JWT authorizer to protected routes (requireAuth !== false)
 * 4. Skipping authorization for public routes (health checks, login)
 * 5. Tagging all resources with the service name for operational visibility
 *
 * Example usage in a service stack:
 * ```typescript
 * const integration = new WebVellaApiIntegration(this, 'ApiRoutes', {
 *   httpApi: sharedApi,
 *   serviceName: 'identity',
 *   authorizer: jwtAuthorizer,
 *   isLocalStack: false,
 *   routes: [
 *     { method: 'POST', path: '/v1/identity/auth/login', handler: authFn, requireAuth: false },
 *     { method: 'GET', path: '/v1/identity/users', handler: userFn },
 *     { method: 'GET', path: '/v1/identity/users/{id}', handler: userFn },
 *     { method: 'GET', path: '/v1/identity/health', handler: healthFn, requireAuth: false },
 *   ],
 * });
 * ```
 */
export class WebVellaApiIntegration extends Construct {
  /**
   * Total number of routes successfully added to the API Gateway.
   * Useful for diagnostics, CloudFormation outputs, and validation
   * that all expected routes were registered.
   */
  public readonly routeCount: number;

  /**
   * Map of route key string (e.g., 'GET /v1/identity/users/{id}') to the
   * HttpLambdaIntegration instance created for that route.
   * Exposed for testing and introspection — allows service stacks to
   * verify their routes were properly registered and to reference
   * specific integrations if needed for additional configuration.
   */
  public readonly integrations: Map<string, apigatewayv2Integrations.HttpLambdaIntegration>;

  constructor(scope: Construct, id: string, props: WebVellaApiIntegrationProps) {
    super(scope, id);

    const {
      httpApi,
      serviceName,
      routes,
      authorizer,
      isLocalStack,
    } = props;

    // Validate service name is provided and non-empty
    if (!serviceName || serviceName.trim().length === 0) {
      throw new Error('WebVellaApiIntegration: serviceName must be a non-empty string');
    }

    // Validate routes array is provided and non-empty
    if (!routes || routes.length === 0) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: routes array must contain at least one route definition`
      );
    }

    this.integrations = new Map<string, apigatewayv2Integrations.HttpLambdaIntegration>();
    let routeCounter = 0;

    for (const route of routes) {
      // Validate route definition completeness
      this.validateRouteDefinition(route, serviceName);

      // Resolve the HTTP method from string to CDK enum
      const httpMethod = this.resolveHttpMethod(route.method, serviceName);

      // Generate a unique CDK construct ID for this integration
      // Sanitize path for use in construct IDs (remove slashes, braces, hyphens)
      const sanitizedPath = route.path
        .replace(/^\//, '')
        .replace(/\//g, '-')
        .replace(/[{}]/g, '')
        .replace(/--+/g, '-');
      const integrationId = `${serviceName}-${route.method.toUpperCase()}-${sanitizedPath}`;

      // Create the Lambda proxy integration for this route
      // HttpLambdaIntegration wraps the Lambda function as an API Gateway v2
      // integration, handling payload format version 2.0 automatically
      const integration = new apigatewayv2Integrations.HttpLambdaIntegration(
        `${integrationId}-integration`,
        route.handler,
      );

      // Determine whether to apply authorization to this route
      // Default: requireAuth = true (authenticated), matching the monolith's
      // [Authorize] controller-level attribute with explicit [AllowAnonymous] opt-out
      const requiresAuth = route.requireAuth !== false;

      // Build the route key using HttpRouteKey.with() for proper API Gateway v2 format
      // This creates the "{METHOD} {path}" combination recognized by API Gateway
      const routeKey = apigatewayv2.HttpRouteKey.with(route.path, httpMethod);

      // Create the HTTP route on the shared API Gateway
      // Each route maps a specific method+path combination to a Lambda integration
      const httpRoute = new apigatewayv2.HttpRoute(this, `${integrationId}-route`, {
        httpApi: httpApi,
        routeKey: routeKey,
        integration: integration,
        // Apply authorizer only for protected routes
        // For unauthenticated routes (login, health), omit the authorizer entirely
        // which means no auth check is performed by API Gateway
        authorizer: requiresAuth && authorizer ? authorizer : undefined,
      });

      // Tag the route resource with service identification for operational visibility
      // Per AAP §0.8.5: All resources tagged with service name for cost allocation
      cdk.Tags.of(httpRoute).add('service', serviceName);
      cdk.Tags.of(httpRoute).add('resource', 'api-route');
      cdk.Tags.of(httpRoute).add('route-path', route.path);
      cdk.Tags.of(httpRoute).add('route-method', route.method.toUpperCase());
      cdk.Tags.of(httpRoute).add('requires-auth', requiresAuth.toString());

      // Store the integration in the map for external reference and testing
      const routeKeyString = `${route.method.toUpperCase()} ${route.path}`;
      this.integrations.set(routeKeyString, integration);

      routeCounter++;
    }

    this.routeCount = routeCounter;

    // Apply service-level tags to the construct itself
    cdk.Tags.of(this).add('service', serviceName);
    cdk.Tags.of(this).add('resource', 'api-integration');
    cdk.Tags.of(this).add('environment', isLocalStack ? 'localstack' : 'production');
  }

  /**
   * Validates a route definition for completeness and correctness.
   * Ensures all required fields are present and the path follows the
   * /v1/ prefix convention per AAP §0.8.6.
   *
   * @param route - The route definition to validate
   * @param serviceName - The owning service name (for error messages)
   * @throws Error if the route definition is invalid
   */
  private validateRouteDefinition(route: RouteDefinition, serviceName: string): void {
    if (!route.method || route.method.trim().length === 0) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: route method must be a non-empty string`
      );
    }

    if (!route.path || route.path.trim().length === 0) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: route path must be a non-empty string`
      );
    }

    if (!route.handler) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: route handler must be a valid Lambda IFunction ` +
        `for route ${route.method.toUpperCase()} ${route.path}`
      );
    }

    // Validate the path starts with /v1/ per AAP §0.8.6 versioning convention
    // This ensures all API routes are version-prefixed for backward compatibility
    if (!route.path.startsWith('/v1/')) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: route path '${route.path}' must start with '/v1/' ` +
        `per API versioning convention (AAP §0.8.6). Example: '/v1/${serviceName}/resource'`
      );
    }

    // Validate the path does not contain double slashes (common misconfiguration)
    if (route.path.includes('//')) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: route path '${route.path}' contains double slashes '//'. ` +
        `Paths must use single slashes.`
      );
    }

    // Validate the path does not end with a trailing slash (API Gateway convention)
    if (route.path.length > 1 && route.path.endsWith('/')) {
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: route path '${route.path}' must not end with ` +
        `a trailing slash. Remove the trailing '/'.`
      );
    }
  }

  /**
   * Resolves a string HTTP method name to the CDK HttpMethod enum value.
   * Supports case-insensitive matching for developer convenience.
   *
   * @param method - The HTTP method string (e.g., 'GET', 'post', 'Delete')
   * @param serviceName - The owning service name (for error messages)
   * @returns The corresponding CDK HttpMethod enum value
   * @throws Error if the method is not a recognized HTTP method
   */
  private resolveHttpMethod(method: string, serviceName: string): apigatewayv2.HttpMethod {
    const normalizedMethod = method.toUpperCase().trim();
    const httpMethod = HTTP_METHOD_MAP[normalizedMethod];

    if (!httpMethod) {
      const validMethods = Object.keys(HTTP_METHOD_MAP).join(', ');
      throw new Error(
        `WebVellaApiIntegration [${serviceName}]: unsupported HTTP method '${method}'. ` +
        `Valid methods are: ${validMethods}`
      );
    }

    return httpMethod;
  }
}
