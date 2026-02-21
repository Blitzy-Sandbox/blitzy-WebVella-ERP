/**
 * JWT Validation Module — Dual-Mode Token Validator
 *
 * Supports two validation modes:
 * 1. Production (Cognito RS256): Uses JWKS-based asymmetric key resolution
 *    from the Cognito user pool's /.well-known/jwks.json endpoint.
 * 2. LocalStack (HS256): Uses symmetric key validation compatible with
 *    LocalStack's Cognito emulation for local development/testing.
 *
 * Derived from the monolith's AuthService.GetValidSecurityTokenAsync() pattern
 * (WebVella.Erp.Web/Services/AuthService.cs lines 120-143).
 *
 * Security design: Returns null on ANY validation failure — never throws.
 * This mirrors the original fail-closed pattern where the caller decides
 * how to handle unauthorized requests (Deny policy in the Lambda authorizer).
 */

import jwt, { JwtPayload, VerifyOptions } from 'jsonwebtoken';
import JwksRsa from 'jwks-rsa';

// ---------------------------------------------------------------------------
// Environment Configuration
// ---------------------------------------------------------------------------

/**
 * Boolean flag for LocalStack mode.
 * When true, uses HS256 symmetric key validation.
 * When false, uses RS256 JWKS-based Cognito validation.
 * Per AAP §0.8.6: IS_LOCAL=true toggles LocalStack mode.
 */
const IS_LOCAL: boolean = process.env.IS_LOCAL === 'true';

/**
 * AWS region for Cognito JWKS URI derivation.
 * Per AAP §0.8.6: AWS_REGION environment variable.
 */
const AWS_REGION: string = process.env.AWS_REGION || 'us-east-1';

/**
 * Cognito user pool ID for JWKS URI and issuer derivation.
 * Per AAP §0.8.6: COGNITO_USER_POOL_ID environment variable.
 */
const COGNITO_USER_POOL_ID: string = process.env.COGNITO_USER_POOL_ID || '';

/**
 * LocalStack symmetric key for HS256 validation.
 * Only used in LocalStack mode (IS_LOCAL=true).
 * In production, JWKS handles key resolution — no secrets in env vars.
 * For LocalStack, this is acceptable since it's a development environment.
 */
const LOCAL_JWT_SECRET: string = process.env.LOCAL_JWT_SECRET || 'localstack-jwt-secret';

/**
 * Optional audience override for validation.
 * Cognito access tokens lack the `aud` claim; ID tokens include it.
 * For LocalStack mode, mirrors the original monolith audience ("webvella-erp").
 */
const COGNITO_AUDIENCE: string = process.env.COGNITO_AUDIENCE || '';

// ---------------------------------------------------------------------------
// Derived Configuration
// ---------------------------------------------------------------------------

/**
 * AWS endpoint URL override — used for LocalStack to route JWKS requests.
 * Per AAP §0.8.6: http://localhost:4566 for LocalStack, omitted in production.
 */
const AWS_ENDPOINT_URL: string = process.env.AWS_ENDPOINT_URL || 'http://localhost:4566';

/**
 * Derive the JWKS URI based on mode:
 * - Production: https://cognito-idp.{region}.amazonaws.com/{poolId}/.well-known/jwks.json
 * - LocalStack: {endpoint}/_aws/cognito-idp/.well-known/jwks.json (uses pool ID in path)
 */
function deriveJwksUri(): string {
  if (IS_LOCAL) {
    return `${AWS_ENDPOINT_URL}/_aws/cognito-idp/${COGNITO_USER_POOL_ID}/.well-known/jwks.json`;
  }
  return `https://cognito-idp.${AWS_REGION}.amazonaws.com/${COGNITO_USER_POOL_ID}/.well-known/jwks.json`;
}

/**
 * Derive the expected token issuer based on mode:
 * - Production: https://cognito-idp.{region}.amazonaws.com/{poolId}
 * - LocalStack: Configurable via COGNITO_ISSUER env var, falls back to endpoint-based URI
 */
function deriveIssuer(): string {
  const customIssuer = process.env.COGNITO_ISSUER;
  if (customIssuer) {
    return customIssuer;
  }
  if (IS_LOCAL) {
    return `${AWS_ENDPOINT_URL}/${COGNITO_USER_POOL_ID}`;
  }
  return `https://cognito-idp.${AWS_REGION}.amazonaws.com/${COGNITO_USER_POOL_ID}`;
}

const JWKS_URI: string = deriveJwksUri();
const EXPECTED_ISSUER: string = deriveIssuer();

// ---------------------------------------------------------------------------
// JWKS Client (Production Mode Only)
// ---------------------------------------------------------------------------

/**
 * Singleton JWKS client for RS256 key resolution.
 * Only initialized when NOT in LocalStack mode to avoid unnecessary HTTP calls.
 * Per AAP §0.8.2: Caching is critical for Node.js Lambda cold start < 3 seconds.
 *
 * Configuration:
 * - cache: true — caches public keys to reduce cold start impact
 * - cacheMaxEntries: 5 — Cognito rotates keys infrequently
 * - cacheMaxAge: 600000 (10 minutes) — balance between freshness and performance
 * - rateLimit: true — prevents excessive JWKS endpoint calls
 * - jwksRequestsPerMinute: 10 — reasonable rate limit for key rotation scenarios
 */
const jwksClient: JwksRsa.JwksClient | null = IS_LOCAL
  ? null
  : new JwksRsa.JwksClient({
      jwksUri: JWKS_URI,
      cache: true,
      cacheMaxEntries: 5,
      cacheMaxAge: 600000,
      rateLimit: true,
      jwksRequestsPerMinute: 10,
    });

/**
 * Fetches the RSA public key for a given Key ID (kid) from the JWKS endpoint.
 * Used exclusively in production mode for RS256 token verification.
 *
 * @param kid - The Key ID from the JWT header
 * @returns The RSA public key string, or null if key resolution fails
 */
async function getSigningKey(kid: string): Promise<string | null> {
  if (!jwksClient) {
    return null;
  }
  try {
    const signingKey = await jwksClient.getSigningKey(kid);
    return signingKey.getPublicKey();
  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : 'Unknown JWKS error';
    console.log(
      JSON.stringify({
        level: 'warn',
        message: 'Failed to retrieve signing key from JWKS endpoint',
        error: errorMessage,
        kid,
        jwksUri: JWKS_URI,
      })
    );
    return null;
  }
}

// ---------------------------------------------------------------------------
// Exported Interface
// ---------------------------------------------------------------------------

/**
 * Decoded JWT token payload with Cognito-specific claims.
 * Extends the standard JwtPayload with fields used by the Lambda authorizer
 * to build the API Gateway context (userId, email, roles).
 *
 * Maps to the monolith's ClaimTypes:
 * - sub → ClaimTypes.NameIdentifier (user GUID)
 * - email → ClaimTypes.Email
 * - cognito:groups → ClaimTypes.Role (user roles)
 * - token_use → Cognito-specific: 'access' or 'id'
 */
export interface TokenPayload extends JwtPayload {
  /** User unique identifier (maps to ClaimTypes.NameIdentifier in the monolith) */
  sub: string;
  /** User email address (maps to ClaimTypes.Email in the monolith) */
  email?: string;
  /** Cognito user pool groups (maps to user roles in the monolith) */
  'cognito:groups'?: string[];
  /** Cognito token type: 'access' or 'id' */
  token_use?: string;
}

// ---------------------------------------------------------------------------
// Structured Logging Helper
// ---------------------------------------------------------------------------

/**
 * Emits a structured JSON log entry to stdout for CloudWatch ingestion.
 * Per AAP §0.8.5: Structured JSON logging — logs validation decisions,
 * never token content (tokens are sensitive).
 *
 * @param level - Log level (info, warn, error)
 * @param message - Human-readable log message
 * @param context - Additional structured context fields
 */
function structuredLog(
  level: 'info' | 'warn' | 'error',
  message: string,
  context: Record<string, unknown> = {}
): void {
  console.log(
    JSON.stringify({
      level,
      message,
      timestamp: new Date().toISOString(),
      service: 'authorizer',
      mode: IS_LOCAL ? 'localstack' : 'production',
      ...context,
    })
  );
}

// ---------------------------------------------------------------------------
// LocalStack Validation (HS256 Symmetric Key)
// ---------------------------------------------------------------------------

/**
 * Validates a JWT using HS256 symmetric key verification.
 * Mirrors the original AuthService.GetValidSecurityTokenAsync() pattern from
 * the monolith (WebVella.Erp.Web/Services/AuthService.cs lines 120-143)
 * which used SymmetricSecurityKey with HmacSha256Signature.
 *
 * @param token - The raw JWT string to validate
 * @returns Decoded token payload or null on any failure
 */
function validateLocalToken(token: string): TokenPayload | null {
  try {
    const verifyOptions: VerifyOptions = {
      algorithms: ['HS256'],
    };

    // Mirror the monolith's issuer validation:
    // ValidateIssuer = true, ValidIssuer = ErpSettings.JwtIssuer ("webvella-erp")
    if (EXPECTED_ISSUER) {
      verifyOptions.issuer = EXPECTED_ISSUER;
    }

    // Mirror the monolith's audience validation:
    // ValidateAudience = true, ValidAudience = ErpSettings.JwtAudience ("webvella-erp")
    if (COGNITO_AUDIENCE) {
      verifyOptions.audience = COGNITO_AUDIENCE;
    }

    // jwt.verify() automatically validates the `exp` claim (token expiration).
    // If expired, it throws TokenExpiredError which is caught below.
    const decoded = jwt.verify(token, LOCAL_JWT_SECRET, verifyOptions);

    // jwt.verify with non-complete mode returns JwtPayload | string.
    // For properly formed JWTs, it returns the payload object.
    if (typeof decoded === 'string') {
      structuredLog('warn', 'Token decoded as string instead of object in local mode');
      return null;
    }

    return decoded as TokenPayload;
  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : 'Unknown local validation error';
    structuredLog('warn', 'Local token validation failed', { error: errorMessage });
    return null;
  }
}

// ---------------------------------------------------------------------------
// Production Validation (RS256 JWKS / Cognito)
// ---------------------------------------------------------------------------

/**
 * Validates a JWT using RS256 asymmetric key verification via Cognito JWKS.
 *
 * Flow:
 * 1. Decode token without verification to extract the `kid` (Key ID) header
 * 2. Fetch the corresponding RSA public key from the Cognito JWKS endpoint
 * 3. Verify the token signature with the public key (RS256)
 * 4. Validate issuer claim against expected Cognito issuer
 * 5. For ID tokens (token_use === 'id'), also validate audience claim
 *
 * Note: Cognito access tokens do NOT include the `aud` claim, so audience
 * validation is only performed for ID tokens.
 *
 * @param token - The raw JWT string to validate
 * @returns Decoded token payload or null on any failure
 */
async function validateCognitoToken(token: string): Promise<TokenPayload | null> {
  try {
    // Step 1: Decode the token WITHOUT verification to extract the kid header.
    // This is safe because we verify the signature in the next step.
    const decodedHeader = jwt.decode(token, { complete: true });

    if (!decodedHeader || typeof decodedHeader === 'string') {
      structuredLog('warn', 'Failed to decode token header for kid extraction');
      return null;
    }

    const kid = decodedHeader.header.kid;
    if (!kid) {
      structuredLog('warn', 'Token header missing kid (Key ID) claim');
      return null;
    }

    // Step 2: Fetch the RSA public key matching the kid from the JWKS endpoint.
    const signingKey = await getSigningKey(kid);
    if (!signingKey) {
      structuredLog('warn', 'Unable to retrieve signing key for kid', { kid });
      return null;
    }

    // Step 3: Build verification options for RS256.
    const verifyOptions: VerifyOptions = {
      algorithms: ['RS256'],
    };

    // Validate issuer against the expected Cognito issuer URI.
    if (EXPECTED_ISSUER) {
      verifyOptions.issuer = EXPECTED_ISSUER;
    }

    // Step 4: Verify the token with the RSA public key.
    // jwt.verify() automatically validates `exp` (expiration).
    const decoded = jwt.verify(token, signingKey, verifyOptions);

    if (typeof decoded === 'string') {
      structuredLog('warn', 'Token decoded as string instead of object in production mode');
      return null;
    }

    const payload = decoded as TokenPayload;

    // Step 5: For ID tokens, validate the audience claim.
    // Cognito access tokens lack the `aud` claim, so we skip audience
    // validation for access tokens to avoid false rejections.
    if (payload.token_use === 'id' && COGNITO_AUDIENCE) {
      const tokenAudience = payload.aud;
      if (tokenAudience) {
        const audiences = Array.isArray(tokenAudience) ? tokenAudience : [tokenAudience];
        if (!audiences.includes(COGNITO_AUDIENCE)) {
          structuredLog('warn', 'ID token audience mismatch', {
            expected: COGNITO_AUDIENCE,
          });
          return null;
        }
      }
    }

    return payload;
  } catch (error: unknown) {
    const errorMessage = error instanceof Error ? error.message : 'Unknown Cognito validation error';
    structuredLog('warn', 'Cognito token validation failed', { error: errorMessage });
    return null;
  }
}

// ---------------------------------------------------------------------------
// Main Exported Function
// ---------------------------------------------------------------------------

/**
 * Validates a JWT token string and returns the decoded payload.
 *
 * Supports dual validation modes based on the IS_LOCAL environment flag:
 * - LocalStack (IS_LOCAL=true): HS256 symmetric key validation
 * - Production (IS_LOCAL=false): RS256 JWKS-based Cognito validation
 *
 * This function mirrors the fail-closed security pattern from the monolith's
 * AuthService.GetValidSecurityTokenAsync() (lines 139-142): returns null on
 * ANY error. The caller (Lambda authorizer handler in index.ts) decides whether
 * to return an Allow or Deny policy.
 *
 * The jsonwebtoken library automatically validates the `exp` claim during
 * jwt.verify(), so expired tokens are rejected without custom expiration logic.
 *
 * @param token - The raw JWT string to validate (without "Bearer " prefix)
 * @returns The decoded token payload as TokenPayload, or null on any validation failure
 *
 * @example
 * ```typescript
 * const payload = await validateToken(bearerToken);
 * if (!payload) {
 *   // Token is invalid, expired, or verification failed
 *   return denyPolicy;
 * }
 * // payload.sub contains the user ID
 * // payload.email contains the user email
 * // payload['cognito:groups'] contains user roles
 * ```
 */
export async function validateToken(token: string): Promise<TokenPayload | null> {
  try {
    // Guard: reject empty/falsy tokens immediately
    if (!token || typeof token !== 'string' || token.trim().length === 0) {
      structuredLog('warn', 'Empty or invalid token provided');
      return null;
    }

    // Route to the appropriate validation mode
    if (IS_LOCAL) {
      return validateLocalToken(token);
    }

    return await validateCognitoToken(token);
  } catch (error: unknown) {
    // Outermost catch-all: mirrors AuthService.GetValidSecurityTokenAsync()
    // catch (Exception) { return null; } pattern at lines 139-142.
    // This ensures the function NEVER throws, regardless of error type.
    const errorMessage = error instanceof Error ? error.message : 'Unknown validation error';
    structuredLog('error', 'Unexpected error during token validation', { error: errorMessage });
    return null;
  }
}
