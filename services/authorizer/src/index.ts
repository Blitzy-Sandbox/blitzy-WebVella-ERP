/**
 * Custom Lambda JWT Authorizer Handler
 *
 * Node.js 22 Lambda function serving as a custom authorizer for HTTP API
 * Gateway v2. Replaces the monolith's JwtMiddleware.cs token extraction and
 * validation pipeline with an AWS Lambda authorizer that returns IAM policy
 * documents (Allow / Deny).
 *
 * Behavioral mapping from the monolith:
 *  - JwtMiddleware.Invoke()       → handler(event, context)
 *  - Authorization header parsing → event.authorizationToken
 *  - AuthService.GetValidSecurityTokenAsync() → validateToken() from jwt-validator.ts
 *  - ClaimTypes extraction        → API Gateway authorizer context object
 *  - Error-swallowing try/catch   → Return Deny policy (never throw)
 *
 * Source references:
 *  - WebVella.Erp.Web/Middleware/JwtMiddleware.cs (lines 21-65)
 *  - WebVella.Erp.Web/Services/AuthService.cs (lines 29-55, 120-143)
 *  - WebVella.Erp.Site/Config.json (JWT configuration)
 *
 * @module services/authorizer/src/index
 */

import type {
  APIGatewayTokenAuthorizerEvent,
  APIGatewayAuthorizerResult,
  Context,
} from 'aws-lambda';

import { validateToken } from './jwt-validator';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Decoded JWT payload with expected Cognito-compatible claims.
 *
 * Maps to the monolith's ClaimTypes consumed in JwtMiddleware.cs (lines 43-52)
 * and AuthService.BuildTokenAsync (lines 145-160):
 *  - sub             → ClaimTypes.NameIdentifier (user GUID)
 *  - email           → ClaimTypes.Email
 *  - cognito:groups  → ClaimTypes.Role (user roles)
 *  - iss             → Token issuer
 *  - aud             → Token audience
 *  - exp             → Expiration timestamp (seconds since epoch)
 *  - iat             → Issued-at timestamp (seconds since epoch)
 *  - token_use       → Cognito-specific: 'access' or 'id'
 */
interface JwtPayload {
  /** User unique identifier — maps to ClaimTypes.NameIdentifier in the monolith */
  sub: string;
  /** User email address — maps to ClaimTypes.Email in the monolith */
  email: string;
  /** Cognito user pool groups — maps to ClaimTypes.Role (user roles) */
  'cognito:groups'?: string[];
  /** Token issuer URI */
  iss: string;
  /** Token audience (string or array) */
  aud: string | string[];
  /** Token expiration (seconds since Unix epoch) */
  exp: number;
  /** Token issued-at (seconds since Unix epoch) */
  iat: number;
  /** Cognito token type: 'access' or 'id' */
  token_use?: string;
}

// ---------------------------------------------------------------------------
// Structured Logging
// ---------------------------------------------------------------------------

/**
 * Emits a structured JSON log entry to stdout for CloudWatch ingestion.
 * Per AAP §0.8.5: Structured JSON logging with correlation-ID propagation.
 *
 * Logs are formatted as single-line JSON for CloudWatch Insights compatibility.
 * Sensitive data (tokens, secrets) is NEVER logged.
 *
 * @param level       - Log severity: 'info', 'warn', or 'error'
 * @param message     - Human-readable log description
 * @param correlationId - Request correlation ID for distributed tracing
 * @param extra       - Additional structured context fields
 */
function structuredLog(
  level: 'info' | 'warn' | 'error',
  message: string,
  correlationId: string,
  extra: Record<string, unknown> = {}
): void {
  console.log(
    JSON.stringify({
      level,
      message,
      timestamp: new Date().toISOString(),
      service: 'authorizer',
      correlationId,
      ...extra,
    })
  );
}

// ---------------------------------------------------------------------------
// IAM Policy Document Generator
// ---------------------------------------------------------------------------

/**
 * Generates an API Gateway authorizer IAM policy document.
 *
 * Builds the standard IAM policy structure that API Gateway expects from a
 * custom Lambda authorizer. The `context` field passes claim data to downstream
 * Lambda integrations, equivalent to `context.Items["User"]` in the monolith's
 * JwtMiddleware (line 49).
 *
 * API Gateway authorizer context only supports primitive values (string, number,
 * boolean), so complex types like role arrays are serialised as comma-separated
 * strings.
 *
 * @param principalId - The principal identifier (user sub / GUID)
 * @param effect      - 'Allow' or 'Deny' — the policy effect
 * @param resource    - The API Gateway method ARN (from event.methodArn)
 * @param context     - Optional context data passed to downstream integrations
 * @returns A fully formed APIGatewayAuthorizerResult
 */
function generatePolicy(
  principalId: string,
  effect: 'Allow' | 'Deny',
  resource: string,
  context?: Record<string, string | number | boolean>
): APIGatewayAuthorizerResult {
  const authResponse: APIGatewayAuthorizerResult = {
    principalId,
    policyDocument: {
      Version: '2012-10-17',
      Statement: [
        {
          Action: 'execute-api:Invoke',
          Effect: effect,
          Resource: resource,
        },
      ],
    },
  };

  // Attach claim context for downstream Lambda consumption if provided.
  // API Gateway serialises these into $context.authorizer.{key} for
  // integration mapping.
  if (context) {
    authResponse.context = context;
  }

  return authResponse;
}

// ---------------------------------------------------------------------------
// Token Extraction
// ---------------------------------------------------------------------------

/**
 * Extracts the raw JWT token string from the authorizer event's
 * authorizationToken field.
 *
 * Mirrors JwtMiddleware.cs lines 23-36:
 *  - Checks for the "Bearer " prefix (case-insensitive) and strips it
 *  - Falls back to using the raw value if no prefix detected
 *  - Returns null for empty, whitespace-only, or too-short tokens
 *
 * @param authorizationToken - The raw authorizationToken from the event
 * @returns The bare JWT string, or null if extraction fails
 */
function extractBearerToken(authorizationToken: string | undefined | null): string | null {
  if (!authorizationToken || typeof authorizationToken !== 'string') {
    return null;
  }

  const trimmed = authorizationToken.trim();
  if (trimmed.length === 0) {
    return null;
  }

  // Mirror JwtMiddleware.cs lines 27-32:
  // The monolith checks `token.Length <= 7` (meaning just "Bearer " with nothing
  // after) and returns null, otherwise strips the first 7 characters ("Bearer ").
  // We use a case-insensitive check for robustness.
  const bearerPrefix = 'bearer ';
  if (trimmed.toLowerCase().startsWith(bearerPrefix)) {
    const tokenPart = trimmed.substring(bearerPrefix.length).trim();
    // Equivalent to the monolith's `if (token.Length <= 7) token = null`
    if (tokenPart.length === 0) {
      return null;
    }
    return tokenPart;
  }

  // If no Bearer prefix, treat the entire value as the raw token.
  // This allows direct token passing without the "Bearer " wrapper.
  return trimmed;
}

// ---------------------------------------------------------------------------
// Main Lambda Handler
// ---------------------------------------------------------------------------

/**
 * Custom Lambda JWT Authorizer handler.
 *
 * This is the entry point invoked by API Gateway for every incoming request
 * that requires authorization. It mirrors the monolith's JwtMiddleware.Invoke()
 * pipeline (JwtMiddleware.cs lines 21-65):
 *
 * 1. Extract the Bearer token from event.authorizationToken
 * 2. Validate the token via jwt-validator.ts (dual-mode: Cognito RS256 / LocalStack HS256)
 * 3. Map validated claims to the API Gateway authorizer context
 * 4. Return an Allow policy (valid token) or Deny policy (invalid/missing token)
 *
 * CRITICAL: This function NEVER throws. The error-swallowing pattern from
 * JwtMiddleware.cs (lines 56-60, bare `catch {}`) is preserved — any failure
 * returns a Deny policy. The caller (API Gateway) translates Deny into a 403.
 *
 * Per AAP §0.8.2: Keep handler lightweight for Node.js Lambda cold start < 3 seconds.
 * Per AAP §0.8.5: Structured JSON logging with correlation-ID.
 * Per AAP §0.8.6: Environment variables: COGNITO_USER_POOL_ID, AWS_REGION, IS_LOCAL.
 *
 * @param event        - The API Gateway token authorizer event
 * @param lambdaContext - The Lambda execution context (provides awsRequestId)
 * @returns An APIGatewayAuthorizerResult with Allow or Deny policy
 */
export const handler = async (
  event: APIGatewayTokenAuthorizerEvent,
  lambdaContext: Context
): Promise<APIGatewayAuthorizerResult> => {
  // Derive correlation ID: prefer an explicit header if the event somehow
  // carries one (via stage variables or custom integration), otherwise fall
  // back to the Lambda request ID. This is the primary tracing identifier
  // across the distributed system (AAP §0.8.5).
  const correlationId = lambdaContext.awsRequestId;

  // Log entry — structured JSON for CloudWatch Insights
  structuredLog('info', 'Authorizer invoked', correlationId, {
    requestId: lambdaContext.awsRequestId,
    methodArn: event.methodArn,
  });

  try {
    // -----------------------------------------------------------------------
    // Step 1: Token Extraction
    // Mirrors JwtMiddleware.cs lines 23-36
    // -----------------------------------------------------------------------
    const token = extractBearerToken(event.authorizationToken);

    if (!token) {
      structuredLog('warn', 'No valid token found in authorization header', correlationId, {
        requestId: lambdaContext.awsRequestId,
        hasAuthorizationToken: !!event.authorizationToken,
        tokenLength: event.authorizationToken ? event.authorizationToken.length : 0,
      });
      return generatePolicy('unauthorized', 'Deny', event.methodArn);
    }

    // -----------------------------------------------------------------------
    // Step 2: Token Validation
    // Mirrors JwtMiddleware.cs lines 40-54, delegating to
    // AuthService.GetValidSecurityTokenAsync() → validateToken()
    // -----------------------------------------------------------------------
    const payload = await validateToken(token);

    if (!payload) {
      structuredLog('warn', 'Token validation returned null — invalid or expired token', correlationId, {
        requestId: lambdaContext.awsRequestId,
      });
      return generatePolicy('unauthorized', 'Deny', event.methodArn);
    }

    // -----------------------------------------------------------------------
    // Step 3: Claims Mapping
    // Mirrors JwtMiddleware.cs lines 43-52
    //
    // In the monolith:
    //   ClaimTypes.NameIdentifier → nameIdentifier → user lookup
    //   ClaimTypes.Email          → user.Email
    //   ClaimTypes.Role           → user.Roles
    //   context.Items["User"]     → downstream context
    //
    // In the Lambda authorizer the claims are passed via the policy context
    // so downstream Lambda integrations can access them via
    // $context.authorizer.{key}
    // -----------------------------------------------------------------------
    const principalId = (payload as JwtPayload).sub || '';
    if (!principalId) {
      structuredLog('warn', 'Token missing sub (subject) claim', correlationId, {
        requestId: lambdaContext.awsRequestId,
      });
      return generatePolicy('unauthorized', 'Deny', event.methodArn);
    }

    // Extract email — map from Cognito 'email' claim
    const email: string = (payload as JwtPayload).email || '';

    // Extract roles — Cognito groups map to the monolith's user roles
    // (ClaimTypes.Role). API Gateway context only supports primitives, so
    // we serialise the groups array as a comma-separated string.
    const cognitoGroups: string[] = (payload as JwtPayload)['cognito:groups'] || [];
    const roles: string = cognitoGroups.join(',');

    // Extract token_use for downstream identification
    const tokenUse: string = (payload as JwtPayload).token_use || '';

    // Determine admin status — check if user belongs to "admin" or "administrator" group.
    // Downstream Lambda handlers check this flag via request.RequestContext.Authorizer.Lambda["isAdmin"].
    const isAdmin: boolean = cognitoGroups.some(
      (g) => g.toLowerCase() === 'admin' || g.toLowerCase() === 'administrator'
    );

    // Build the context object passed to downstream Lambda integrations.
    // This replaces the monolith's `context.Items["User"]` assignment
    // (JwtMiddleware.cs line 49) and `context.User` principal attachment
    // (line 52).
    const authorizerContext: Record<string, string | number | boolean> = {
      userId: principalId,
      email,
      roles,
      tokenUse,
      correlationId,
      isAdmin,
    };

    // -----------------------------------------------------------------------
    // Step 4: Return Allow Policy
    // -----------------------------------------------------------------------
    structuredLog('info', 'Token validated successfully', correlationId, {
      requestId: lambdaContext.awsRequestId,
      principalId,
      roles,
    });

    return generatePolicy(principalId, 'Allow', event.methodArn, authorizerContext);
  } catch (error: unknown) {
    // -----------------------------------------------------------------------
    // Error Handler — Swallow-all pattern
    // Mirrors JwtMiddleware.cs lines 56-60:
    //   catch { /* do nothing if jwt validation fails */ }
    //
    // CRITICAL: The Lambda authorizer MUST NOT throw. Any unhandled exception
    // would cause API Gateway to return a 500 instead of a clean 403. The
    // monolith's design philosophy is "fail closed" — on any error, the user
    // simply doesn't get authenticated.
    // -----------------------------------------------------------------------
    const errorMessage = error instanceof Error ? error.message : 'Unknown authorizer error';
    const errorStack = error instanceof Error ? error.stack : undefined;

    structuredLog('error', 'Authorizer encountered an unexpected error', correlationId, {
      requestId: lambdaContext.awsRequestId,
      error: errorMessage,
      stack: errorStack,
    });

    return generatePolicy('unauthorized', 'Deny', event.methodArn);
  }
};
