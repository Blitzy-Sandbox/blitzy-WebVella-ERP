/**
 * Lambda Handler Unit Tests — Custom JWT Authorizer
 *
 * Comprehensive Vitest unit tests for services/authorizer/src/index.ts.
 * Validates the Lambda handler's token extraction from Bearer authorization
 * headers, IAM Allow/Deny policy generation, claims mapping to API Gateway
 * context (userId, email, roles), error-swallowing behavior (never throws),
 * and structured JSON logging.
 *
 * Source pattern: WebVella.Erp.Web/Middleware/JwtMiddleware.cs (lines 21-65)
 *  - Token extraction from Authorization header (lines 23-36)
 *  - Claims mapping to context (lines 43-52)
 *  - Error-swallowing catch-all (lines 56-60)
 *
 * Per AAP §0.8.4: Tests use Vitest with > 80% coverage target.
 * Per AAP §0.8.1: Full behavioral parity with the monolith's auth middleware.
 * Per AAP §0.8.5: Structured JSON logging — verify log output format.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type {
  APIGatewayTokenAuthorizerEvent,
  APIGatewayAuthorizerResult,
  Context,
} from 'aws-lambda';

// ---------------------------------------------------------------------------
// Module-level mock for jwt-validator
// ---------------------------------------------------------------------------
// vi.mock is hoisted by Vitest, so this runs before any import.
// We mock the jwt-validator module to prevent real JWT validation.
// Default behaviour: validateToken returns null (deny by default).
// Each test overrides with vi.mocked(validateToken).mockResolvedValue(...)

vi.mock('../src/jwt-validator', () => ({
  validateToken: vi.fn().mockResolvedValue(null),
}));

// Import AFTER the vi.mock call — Vitest hoists the mock regardless but this
// makes intent clear. The handler is the real implementation; validateToken
// is the mocked dependency.
import { handler } from '../src/index';
import { validateToken } from '../src/jwt-validator';

// ---------------------------------------------------------------------------
// Test Constants
// ---------------------------------------------------------------------------

/** Standard method ARN used in most tests */
const TEST_METHOD_ARN =
  'arn:aws:execute-api:us-east-1:123456789012:abc123def/prod/GET/v1/entities';

/** Alternative method ARN for resource-matching tests */
const ALT_METHOD_ARN =
  'arn:aws:execute-api:us-east-1:123456789012:xyz789/staging/POST/v1/records';

// ---------------------------------------------------------------------------
// Test Helper Functions
// ---------------------------------------------------------------------------

/**
 * Creates a mock API Gateway TOKEN authorizer event.
 * Mirrors the event shape API Gateway sends to custom Lambda authorizers.
 *
 * @param token - The authorization token string (e.g. 'Bearer eyJ...')
 * @param methodArn - The method ARN for the target resource
 * @returns A valid APIGatewayTokenAuthorizerEvent
 */
function createMockEvent(
  token?: string,
  methodArn: string = TEST_METHOD_ARN
): APIGatewayTokenAuthorizerEvent {
  return {
    type: 'TOKEN',
    authorizationToken: token as string,
    methodArn,
  };
}

/**
 * Creates a minimal mock Lambda execution context.
 * Provides the awsRequestId used as correlation-ID in the handler.
 *
 * @returns A Context object with essential fields populated
 */
function createMockContext(): Context {
  return {
    awsRequestId: 'test-request-id-12345',
    functionName: 'webvella-authorizer',
    functionVersion: '$LATEST',
    invokedFunctionArn: 'arn:aws:lambda:us-east-1:123456789012:function:webvella-authorizer',
    memoryLimitInMB: '128',
    logGroupName: '/aws/lambda/webvella-authorizer',
    logStreamName: '2024/01/01/[$LATEST]abcdef123456',
    callbackWaitsForEmptyEventLoop: true,
    getRemainingTimeInMillis: () => 30000,
    done: () => {},
    fail: () => {},
    succeed: () => {},
  };
}

/**
 * Helper type to access the IAM policy statement's Action and Resource
 * properties without hitting the discriminated union constraints in
 * @types/aws-lambda. The actual runtime values are always plain strings.
 */
interface FlatStatement {
  Effect: string;
  Action: string | string[];
  Resource: string | string[];
}

/**
 * Asserts that the given result is a valid Deny policy.
 */
function expectDenyPolicy(result: APIGatewayAuthorizerResult): void {
  expect(result).toBeDefined();
  expect(result.policyDocument).toBeDefined();
  expect(result.policyDocument.Version).toBe('2012-10-17');
  expect(result.policyDocument.Statement).toHaveLength(1);
  const stmt = result.policyDocument.Statement[0] as unknown as FlatStatement;
  expect(stmt.Effect).toBe('Deny');
  expect(stmt.Action).toBe('execute-api:Invoke');
}

/**
 * Asserts that the given result is a valid Allow policy.
 */
function expectAllowPolicy(
  result: APIGatewayAuthorizerResult,
  expectedPrincipalId: string,
  expectedResource: string = TEST_METHOD_ARN
): void {
  expect(result).toBeDefined();
  expect(result.principalId).toBe(expectedPrincipalId);
  expect(result.policyDocument).toBeDefined();
  expect(result.policyDocument.Version).toBe('2012-10-17');
  expect(result.policyDocument.Statement).toHaveLength(1);
  const stmt = result.policyDocument.Statement[0] as unknown as FlatStatement;
  expect(stmt.Effect).toBe('Allow');
  expect(stmt.Action).toBe('execute-api:Invoke');
  expect(stmt.Resource).toBe(expectedResource);
}

// ---------------------------------------------------------------------------
// Test Setup / Teardown
// ---------------------------------------------------------------------------

let consoleSpy: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  // resetAllMocks clears call history AND resets implementations set by
  // per-test overrides (e.g. mockResolvedValue). This ensures test isolation
  // even when tests run in sequence and earlier tests override the mock.
  vi.resetAllMocks();
  // Re-apply the default mock implementation after reset since resetAllMocks
  // clears the factory-provided mockResolvedValue(null). Without this,
  // validateToken would return undefined (default vi.fn() behaviour).
  vi.mocked(validateToken).mockResolvedValue(null);
  // Suppress console.log output during tests while still allowing spy assertions
  consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
});

afterEach(() => {
  consoleSpy.mockRestore();
});

// ===========================================================================
// Test Suite: Token Extraction
// ===========================================================================

describe('Token Extraction', () => {
  it('extracts token from Bearer authorization header', async () => {
    // Arrange: mock validateToken to return a valid payload when called
    // with the bare token (prefix stripped)
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-uuid-123',
      email: 'test@example.com',
    } as any);

    const event = createMockEvent('Bearer valid.jwt.token');
    const ctx = createMockContext();

    // Act
    const result = await handler(event, ctx);

    // Assert: validateToken was called with the bare token, not the full header
    expect(validateToken).toHaveBeenCalledWith('valid.jwt.token');
    expectAllowPolicy(result, 'user-uuid-123');
  });

  it('returns Deny policy when Authorization header is missing', async () => {
    // Create event with undefined authorizationToken
    const event = createMockEvent(undefined);
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // validateToken should NOT be called when there is no token
    expect(validateToken).not.toHaveBeenCalled();
    expectDenyPolicy(result);
  });

  it('returns Deny policy when Authorization header is empty string', async () => {
    const event = createMockEvent('');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('returns Deny policy when token is only "Bearer " without actual token', async () => {
    // Edge case from JwtMiddleware.cs lines 29-30:
    // When `token.Length <= 7`, token is set to null.
    // "Bearer " is 7 chars; after trim() the trailing space is removed leaving
    // "Bearer" (6 chars) which does NOT match the "bearer " prefix (7 chars).
    // The handler falls through to treating it as a raw token and passes it to
    // validateToken, which returns null (our default mock) → Deny.
    const event = createMockEvent('Bearer ');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
    // validateToken IS called with the trimmed raw value "Bearer"
    expect(validateToken).toHaveBeenCalledWith('Bearer');
  });

  it('returns Deny policy when token is "Bearer" without space', async () => {
    // "Bearer" is exactly 6 characters — no space, no token after it.
    // The handler's extractBearerToken checks for "bearer " prefix (7 chars).
    // "bearer" (6 chars) does NOT start with "bearer " (7 chars), so it
    // falls through to treating the raw string "Bearer" as the token.
    // validateToken is called with "Bearer", returns null → Deny.
    const event = createMockEvent('Bearer');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
    // validateToken IS called with the raw value since no Bearer prefix match
    expect(validateToken).toHaveBeenCalledWith('Bearer');
  });

  it('handles token without Bearer prefix gracefully', async () => {
    // Original JwtMiddleware.cs (line 26) reads the full header value, then
    // strips "Bearer " prefix. If no "Bearer " prefix, the handler's
    // extractBearerToken() passes the raw value to validateToken.
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'raw-user-id',
      email: 'raw@test.com',
    } as any);

    const event = createMockEvent('raw.jwt.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // The handler passes the raw token through; validateToken determines validity
    expect(validateToken).toHaveBeenCalledWith('raw.jwt.token');
    expectAllowPolicy(result, 'raw-user-id');
  });

  it('handles Bearer prefix with mixed casing', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'case-user-id',
      email: 'case@test.com',
    } as any);

    const event = createMockEvent('BEARER my.token.value');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // The handler performs case-insensitive prefix checking
    expect(validateToken).toHaveBeenCalledWith('my.token.value');
    expectAllowPolicy(result, 'case-user-id');
  });
});

// ===========================================================================
// Test Suite: IAM Policy Generation
// ===========================================================================

describe('IAM Policy Generation', () => {
  it('returns Allow policy with correct structure on valid token', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-uuid-123',
      email: 'test@example.com',
      'cognito:groups': ['admin', 'regular'],
    } as any);

    const event = createMockEvent('Bearer test.token', TEST_METHOD_ARN);
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // Verify full policy structure per API Gateway authorizer contract
    expect(result.principalId).toBe('user-uuid-123');
    expect(result.policyDocument.Version).toBe('2012-10-17');
    expect(result.policyDocument.Statement).toHaveLength(1);
    const stmt = result.policyDocument.Statement[0] as unknown as FlatStatement;
    expect(stmt.Action).toBe('execute-api:Invoke');
    expect(stmt.Effect).toBe('Allow');
    expect(stmt.Resource).toBe(TEST_METHOD_ARN);
  });

  it('returns Deny policy with correct structure on invalid token', async () => {
    // Default mock returns null — token invalid
    vi.mocked(validateToken).mockResolvedValue(null);

    const event = createMockEvent('Bearer invalid.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    const denyStmt = result.policyDocument.Statement[0] as unknown as FlatStatement;
    expect(denyStmt.Effect).toBe('Deny');
    expect(denyStmt.Action).toBe('execute-api:Invoke');
    expect(result.policyDocument.Version).toBe('2012-10-17');
  });

  it('includes user context in Allow policy', async () => {
    // Maps the monolith's context.Items["User"] (JwtMiddleware.cs line 49)
    // to API Gateway authorizer context
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-123',
      email: 'admin@webvella.com',
      'cognito:groups': ['administrator', 'regular'],
    } as any);

    const event = createMockEvent('Bearer context.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'user-123');
    expect(result.context).toBeDefined();
    expect(result.context!.userId).toBe('user-123');
    expect(result.context!.email).toBe('admin@webvella.com');
    expect(result.context!.roles).toBe('administrator,regular');
  });

  it('uses the methodArn from event as the policy Resource', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'resource-user',
      email: 'res@test.com',
    } as any);

    const event = createMockEvent('Bearer resource.token', ALT_METHOD_ARN);
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    const resStmt = result.policyDocument.Statement[0] as unknown as FlatStatement;
    expect(resStmt.Resource).toBe(ALT_METHOD_ARN);
  });

  it('does not include context object in Deny policy', async () => {
    vi.mocked(validateToken).mockResolvedValue(null);

    const event = createMockEvent('Bearer deny.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
    // Deny policies should not include user context
    expect(result.context).toBeUndefined();
  });
});

// ===========================================================================
// Test Suite: Claims Extraction
// ===========================================================================

describe('Claims Extraction', () => {
  it('extracts sub claim as principalId', async () => {
    // Maps to ClaimTypes.NameIdentifier from JwtMiddleware.cs line 45
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'abc-def-ghi-jkl',
    } as any);

    const event = createMockEvent('Bearer claims.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expect(result.principalId).toBe('abc-def-ghi-jkl');
  });

  it('extracts email claim into context', async () => {
    // Maps to ClaimTypes.Email from AuthService.cs line 149
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-1',
      email: 'user@test.com',
    } as any);

    const event = createMockEvent('Bearer email.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'user-1');
    expect(result.context!.email).toBe('user@test.com');
  });

  it('extracts cognito:groups claim as roles', async () => {
    // Maps to ClaimTypes.Role from AuthService.cs line 150
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-1',
      'cognito:groups': ['admin', 'editor'],
    } as any);

    const event = createMockEvent('Bearer groups.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'user-1');
    expect(result.context!.roles).toBe('admin,editor');
  });

  it('handles missing email claim gracefully', async () => {
    // Token has sub but no email — handler should still return Allow
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-1',
    } as any);

    const event = createMockEvent('Bearer no-email.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'user-1');
    // Email should be empty string when not present
    expect(result.context!.email).toBe('');
  });

  it('handles missing cognito:groups claim gracefully', async () => {
    // Token has sub and email but no groups — should return Allow with empty roles
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'user-1',
      email: 'user@test.com',
    } as any);

    const event = createMockEvent('Bearer no-groups.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'user-1');
    // Roles should be empty string when no groups present
    expect(result.context!.roles).toBe('');
  });

  it('handles missing sub claim by returning Deny', async () => {
    // Sub is required for principalId — without it the handler should deny.
    // The handler checks: `if (!principalId)` and returns Deny.
    vi.mocked(validateToken).mockResolvedValue({
      email: 'user@test.com',
    } as any);

    const event = createMockEvent('Bearer no-sub.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('includes correlationId in context', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'corr-user',
      email: 'corr@test.com',
    } as any);

    const event = createMockEvent('Bearer corr.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'corr-user');
    expect(result.context!.correlationId).toBe('test-request-id-12345');
  });

  it('includes tokenUse in context when present', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'use-user',
      email: 'use@test.com',
      token_use: 'access',
    } as any);

    const event = createMockEvent('Bearer use.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'use-user');
    expect(result.context!.tokenUse).toBe('access');
  });

  it('handles single cognito:group correctly', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'single-group-user',
      'cognito:groups': ['admin'],
    } as any);

    const event = createMockEvent('Bearer single-group.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectAllowPolicy(result, 'single-group-user');
    expect(result.context!.roles).toBe('admin');
  });
});

// ===========================================================================
// Test Suite: Error Handling
// ===========================================================================

describe('Error Handling', () => {
  it('returns Deny policy when validateToken throws an error', async () => {
    // Mirrors JwtMiddleware.cs lines 56-60: catch-all that swallows errors.
    // The handler wraps everything in try/catch and returns Deny on any throw.
    vi.mocked(validateToken).mockRejectedValue(new Error('Validation error'));

    const event = createMockEvent('Bearer throw.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('returns Deny policy when validateToken throws unexpected error types', async () => {
    // Non-Error throwable (a string)
    vi.mocked(validateToken).mockRejectedValue('unexpected string error');

    const event = createMockEvent('Bearer string-throw.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('throws when event is null because methodArn access is pre-try-catch', async () => {
    // The handler accesses event.methodArn in the structuredLog call on line 241,
    // which is BEFORE the try/catch block. A null event causes a TypeError.
    // This is consistent with API Gateway always providing a valid event — null
    // events don't occur in production. The handler's catch-all covers validation
    // errors, not infrastructure-level contract violations.
    const ctx = createMockContext();

    await expect(handler(null as any, ctx)).rejects.toThrow();
  });

  it('never throws from the handler function — undefined authorizationToken', async () => {
    const event: any = { type: 'TOKEN', methodArn: TEST_METHOD_ARN };
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expect(result).toBeDefined();
    expectDenyPolicy(result);
  });

  it('never throws from the handler function — validateToken rejects with TypeError', async () => {
    vi.mocked(validateToken).mockRejectedValue(new TypeError('Cannot read properties'));

    const event = createMockEvent('Bearer type-error.token');
    const ctx = createMockContext();

    // Verify the promise resolves (never rejects)
    await expect(handler(event, ctx)).resolves.toBeDefined();
    const result = await handler(event, ctx);
    expectDenyPolicy(result);
  });

  it('handles null token from validation gracefully', async () => {
    vi.mocked(validateToken).mockResolvedValue(null);

    const event = createMockEvent('Bearer null-result.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('handles undefined return from validateToken', async () => {
    vi.mocked(validateToken).mockResolvedValue(undefined as any);

    const event = createMockEvent('Bearer undefined-result.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('returns Deny when validateToken throws object without message', async () => {
    vi.mocked(validateToken).mockRejectedValue({ code: 'UNKNOWN', detail: 'some error' });

    const event = createMockEvent('Bearer obj-throw.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });
});

// ===========================================================================
// Test Suite: Structured Logging
// ===========================================================================

describe('Structured Logging', () => {
  it('logs authorizer invocation on each request', async () => {
    // Per AAP §0.8.5: Structured JSON logging with correlation-ID propagation
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'log-user',
      email: 'log@test.com',
    } as any);

    const event = createMockEvent('Bearer log.token');
    const ctx = createMockContext();

    await handler(event, ctx);

    // Find the invocation log entry
    const calls = consoleSpy.mock.calls;
    const invocationLog = calls.find((call) => {
      try {
        const parsed = JSON.parse(call[0] as string);
        return parsed.level === 'info' && parsed.message.includes('invoked');
      } catch {
        return false;
      }
    });

    expect(invocationLog).toBeDefined();
    const parsed = JSON.parse(invocationLog![0] as string);
    expect(parsed.level).toBe('info');
    expect(parsed.service).toBe('authorizer');
    expect(parsed.correlationId).toBe('test-request-id-12345');
    expect(parsed.timestamp).toBeDefined();
  });

  it('logs validation failure on denied requests', async () => {
    vi.mocked(validateToken).mockResolvedValue(null);

    const event = createMockEvent('Bearer denied.token');
    const ctx = createMockContext();

    await handler(event, ctx);

    // Find the warn-level log entry for validation failure
    const calls = consoleSpy.mock.calls;
    const warnLog = calls.find((call) => {
      try {
        const parsed = JSON.parse(call[0] as string);
        return parsed.level === 'warn';
      } catch {
        return false;
      }
    });

    expect(warnLog).toBeDefined();
    const parsed = JSON.parse(warnLog![0] as string);
    expect(parsed.level).toBe('warn');
  });

  it('does not log token content for security', async () => {
    const sensitiveToken = 'secret.jwt.token.with.sensitive.data';
    const event = createMockEvent(`Bearer ${sensitiveToken}`);
    const ctx = createMockContext();

    await handler(event, ctx);

    // Verify no log entry contains the literal token string
    const calls = consoleSpy.mock.calls;
    for (const call of calls) {
      const logStr = String(call[0]);
      expect(logStr).not.toContain(sensitiveToken);
    }
  });

  it('logs error details when handler catches an exception', async () => {
    vi.mocked(validateToken).mockRejectedValue(new Error('Unexpected failure'));

    const event = createMockEvent('Bearer error.token');
    const ctx = createMockContext();

    await handler(event, ctx);

    // Find the error-level log entry
    const calls = consoleSpy.mock.calls;
    const errorLog = calls.find((call) => {
      try {
        const parsed = JSON.parse(call[0] as string);
        return parsed.level === 'error';
      } catch {
        return false;
      }
    });

    expect(errorLog).toBeDefined();
    const parsed = JSON.parse(errorLog![0] as string);
    expect(parsed.level).toBe('error');
    expect(parsed.error).toBe('Unexpected failure');
  });

  it('logs success details when token is validated', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'success-user',
      email: 'success@test.com',
      'cognito:groups': ['admin'],
    } as any);

    const event = createMockEvent('Bearer success.token');
    const ctx = createMockContext();

    await handler(event, ctx);

    // Find the info-level log entry for success
    const calls = consoleSpy.mock.calls;
    const successLog = calls.find((call) => {
      try {
        const parsed = JSON.parse(call[0] as string);
        return parsed.level === 'info' && parsed.message.includes('validated successfully');
      } catch {
        return false;
      }
    });

    expect(successLog).toBeDefined();
    const parsed = JSON.parse(successLog![0] as string);
    expect(parsed.principalId).toBe('success-user');
    expect(parsed.roles).toBe('admin');
  });
});

// ===========================================================================
// Test Suite: Edge Cases
// ===========================================================================

describe('Edge Cases', () => {
  it('handles very long tokens', async () => {
    // Create a token > 10KB
    const longToken = 'Bearer ' + 'a'.repeat(12000);
    vi.mocked(validateToken).mockResolvedValue(null);

    const event = createMockEvent(longToken);
    const ctx = createMockContext();

    // Should complete without error
    const result = await handler(event, ctx);
    expect(result).toBeDefined();
    expect(result.policyDocument).toBeDefined();
  });

  it('handles tokens with special characters', async () => {
    // Base64 characters commonly found in JWTs: =, +, /
    const specialToken = 'Bearer eyJhbGciOi+IkpXVCJ9.eyJzdWIi/OiIxMjM0NTY3ODkwIn0=.signature==';
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'special-user',
    } as any);

    const event = createMockEvent(specialToken);
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // Handler should pass the token to validateToken without mangling special chars
    expect(validateToken).toHaveBeenCalledWith(
      'eyJhbGciOi+IkpXVCJ9.eyJzdWIi/OiIxMjM0NTY3ODkwIn0=.signature=='
    );
    expectAllowPolicy(result, 'special-user');
  });

  it('handles concurrent invocations independently', async () => {
    // Each invocation should be independent — no shared state leakage
    const mockValidateToken = vi.mocked(validateToken);

    // Set up sequential return values for concurrent calls
    mockValidateToken
      .mockResolvedValueOnce({ sub: 'user-A', email: 'a@test.com' } as any)
      .mockResolvedValueOnce(null)
      .mockResolvedValueOnce({ sub: 'user-C', email: 'c@test.com' } as any);

    const eventA = createMockEvent('Bearer token-A');
    const eventB = createMockEvent('Bearer token-B');
    const eventC = createMockEvent('Bearer token-C');
    const ctx = createMockContext();

    // Run all three in parallel
    const [resultA, resultB, resultC] = await Promise.all([
      handler(eventA, ctx),
      handler(eventB, ctx),
      handler(eventC, ctx),
    ]);

    // A should be Allow
    expectAllowPolicy(resultA, 'user-A');

    // B should be Deny (validateToken returned null)
    expectDenyPolicy(resultB);

    // C should be Allow
    expectAllowPolicy(resultC, 'user-C');
  });

  it('handles whitespace-only authorization token', async () => {
    const event = createMockEvent('   ');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expectDenyPolicy(result);
  });

  it('handles "bearer" prefix in lowercase', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'lower-user',
      email: 'lower@test.com',
    } as any);

    const event = createMockEvent('bearer lower.case.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    expect(validateToken).toHaveBeenCalledWith('lower.case.token');
    expectAllowPolicy(result, 'lower-user');
  });

  it('handles "Bearer" with extra spaces before token', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'space-user',
    } as any);

    const event = createMockEvent('Bearer   spaced.token.value');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // The handler trims the token part after stripping "Bearer "
    expect(validateToken).toHaveBeenCalled();
    expectAllowPolicy(result, 'space-user');
  });

  it('handles event with missing methodArn gracefully', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: 'arn-user',
    } as any);

    const event: any = {
      type: 'TOKEN',
      authorizationToken: 'Bearer arn.token',
      methodArn: undefined,
    };
    const ctx = createMockContext();

    // Should not throw even with undefined methodArn
    const result = await handler(event, ctx);
    expect(result).toBeDefined();
    expect(result.policyDocument).toBeDefined();
  });

  it('handles payload with empty sub claim by returning Deny', async () => {
    vi.mocked(validateToken).mockResolvedValue({
      sub: '',
      email: 'empty-sub@test.com',
    } as any);

    const event = createMockEvent('Bearer empty-sub.token');
    const ctx = createMockContext();

    const result = await handler(event, ctx);

    // Empty sub is equivalent to missing sub — should Deny
    expectDenyPolicy(result);
  });
});
