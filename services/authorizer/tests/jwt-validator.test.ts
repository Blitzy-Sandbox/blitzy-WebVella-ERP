/**
 * JWT Validation Module Unit Tests
 *
 * Comprehensive Vitest tests for services/authorizer/src/jwt-validator.ts.
 * Validates both RS256 (Cognito JWKS) and HS256 (LocalStack symmetric key)
 * token validation paths, error handling, claims extraction, and the dual-mode
 * behavior controlled by the IS_LOCAL environment variable.
 *
 * Source pattern: WebVella.Erp.Web/Services/AuthService.cs — GetValidSecurityTokenAsync()
 * The null-return-on-error pattern (lines 139-142) is the PRIMARY security guarantee
 * tested here: the validator NEVER throws, always returns null on failure.
 *
 * Per AAP §0.8.4: Tests use Vitest with > 80% coverage target.
 * Per AAP §0.8.1: Full behavioral parity with the monolith's auth validation.
 */

import { describe, it, expect, vi, beforeEach, afterEach, beforeAll } from 'vitest';
import jwt, { SignOptions } from 'jsonwebtoken';
import crypto from 'crypto';

// ---------------------------------------------------------------------------
// Module-level mock for jwks-rsa
// ---------------------------------------------------------------------------
// vi.mock is hoisted by Vitest, so this runs before any import.
// We mock jwks-rsa to avoid real JWKS HTTP calls during tests.
// The mock returns a configurable getSigningKey function controlled per-test.

const mockGetSigningKey = vi.fn();

vi.mock('jwks-rsa', () => {
  const MockJwksClient = vi.fn().mockImplementation(() => ({
    getSigningKey: mockGetSigningKey,
  }));
  // Expose both as default export and named export to cover all import styles
  return {
    default: { JwksClient: MockJwksClient },
    JwksClient: MockJwksClient,
  };
});

// ---------------------------------------------------------------------------
// Test Constants
// ---------------------------------------------------------------------------

/** HS256 symmetric key used for LocalStack mode tests */
const TEST_SECRET = 'test-localstack-jwt-secret-key-for-tests';

/** Default HS256 key matching the jwt-validator.ts fallback */
const DEFAULT_LOCAL_SECRET = 'localstack-jwt-secret';

/** Fake Cognito user pool ID for test issuer derivation */
const TEST_USER_POOL_ID = 'us-east-1_TestPool';

/** AWS region for test issuer derivation */
const TEST_REGION = 'us-east-1';

/** Production-mode Cognito issuer URL */
const TEST_COGNITO_ISSUER = `https://cognito-idp.${TEST_REGION}.amazonaws.com/${TEST_USER_POOL_ID}`;

/** LocalStack-mode issuer URL */
const TEST_LOCAL_ISSUER = `http://localhost:4566/${TEST_USER_POOL_ID}`;

// ---------------------------------------------------------------------------
// RSA Key Pair for RS256 Tests
// ---------------------------------------------------------------------------

const { publicKey: rsaPublicKey, privateKey: rsaPrivateKey } = crypto.generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
});

/** Alternative RSA key pair for signature mismatch tests */
const { privateKey: altRsaPrivateKey } = crypto.generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
});

// ---------------------------------------------------------------------------
// Test Token Key ID
// ---------------------------------------------------------------------------
const TEST_KID = 'test-key-id-001';

// ---------------------------------------------------------------------------
// Environment Variable Management
// ---------------------------------------------------------------------------

/** Saves the original process.env values to restore after each test */
let originalEnv: NodeJS.ProcessEnv;

function saveEnv(): void {
  originalEnv = { ...process.env };
}

function restoreEnv(): void {
  // Remove keys that were added during tests
  const currentKeys = Object.keys(process.env);
  for (const key of currentKeys) {
    if (!(key in originalEnv)) {
      delete process.env[key];
    }
  }
  // Restore original values
  for (const [key, value] of Object.entries(originalEnv)) {
    if (value === undefined) {
      delete process.env[key];
    } else {
      process.env[key] = value;
    }
  }
}

/**
 * Configures environment for LocalStack (HS256) mode and dynamically imports
 * the jwt-validator module so that module-level constants are re-evaluated.
 */
async function importLocalModule(
  secret?: string,
  extraEnv?: Record<string, string>
): Promise<{ validateToken: (token: string) => Promise<import('../src/jwt-validator').TokenPayload | null> }> {
  vi.resetModules();
  process.env.IS_LOCAL = 'true';
  if (secret !== undefined) {
    process.env.LOCAL_JWT_SECRET = secret;
  }
  process.env.AWS_REGION = TEST_REGION;
  process.env.COGNITO_USER_POOL_ID = TEST_USER_POOL_ID;
  if (extraEnv) {
    for (const [key, value] of Object.entries(extraEnv)) {
      process.env[key] = value;
    }
  }
  const mod = await import('../src/jwt-validator');
  return { validateToken: mod.validateToken };
}

/**
 * Configures environment for production (RS256 / Cognito) mode and dynamically
 * imports the jwt-validator module so that module-level constants are re-evaluated.
 */
async function importProductionModule(
  extraEnv?: Record<string, string>
): Promise<{ validateToken: (token: string) => Promise<import('../src/jwt-validator').TokenPayload | null> }> {
  vi.resetModules();
  process.env.IS_LOCAL = 'false';
  process.env.AWS_REGION = TEST_REGION;
  process.env.COGNITO_USER_POOL_ID = TEST_USER_POOL_ID;
  if (extraEnv) {
    for (const [key, value] of Object.entries(extraEnv)) {
      process.env[key] = value;
    }
  }
  const mod = await import('../src/jwt-validator');
  return { validateToken: mod.validateToken };
}

// ---------------------------------------------------------------------------
// Token Creation Helpers
// ---------------------------------------------------------------------------

/**
 * Creates an HS256-signed test token with configurable payload and options.
 * Uses the real jsonwebtoken library (not mocked) — mirrors the monolith's
 * AuthService.BuildTokenAsync() which used HmacSha256Signature.
 */
function createHS256Token(
  payload: Record<string, unknown>,
  secret: string = TEST_SECRET,
  options: Partial<SignOptions> = {}
): string {
  const defaultOptions: SignOptions = {
    algorithm: 'HS256',
    expiresIn: '1h',
    issuer: TEST_LOCAL_ISSUER,
    ...options,
  };
  return jwt.sign(payload, secret, defaultOptions);
}

/**
 * Creates an RS256-signed test token with a kid header for JWKS resolution.
 * Uses the test RSA private key and includes a kid header matching TEST_KID.
 */
function createRS256Token(
  payload: Record<string, unknown>,
  privateKey: string = rsaPrivateKey,
  options: Partial<SignOptions> = {}
): string {
  const defaultOptions: SignOptions = {
    algorithm: 'RS256',
    expiresIn: '1h',
    keyid: TEST_KID,
    issuer: TEST_COGNITO_ISSUER,
    ...options,
  };
  return jwt.sign(payload, privateKey, defaultOptions);
}

/**
 * Configures the mock JWKS client to return the test RSA public key
 * for the given kid, simulating Cognito's /.well-known/jwks.json response.
 */
function setupJwksMock(publicKey: string = rsaPublicKey): void {
  mockGetSigningKey.mockImplementation(async (kid: string) => {
    if (kid === TEST_KID) {
      return { getPublicKey: () => publicKey };
    }
    throw new Error(`SigningKeyNotFoundError: Unable to find a signing key that matches '${kid}'`);
  });
}

// ============================================================================
// TEST SUITES
// ============================================================================

describe('JWT Validator — jwt-validator.ts', () => {
  beforeEach(() => {
    saveEnv();
    vi.clearAllMocks();
  });

  afterEach(() => {
    restoreEnv();
  });

  // ==========================================================================
  // HS256 LocalStack Mode Validation
  // ==========================================================================
  describe('HS256 LocalStack Mode Validation', () => {
    it('validates a valid HS256 token in LocalStack mode', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'user-uuid-1234',
        email: 'test@webvella.com',
        'cognito:groups': ['admin'],
      });

      const result = await validateToken(token);

      expect(result).not.toBeNull();
      expect(result!.sub).toBe('user-uuid-1234');
      expect(result!.email).toBe('test@webvella.com');
      expect(result!['cognito:groups']).toEqual(['admin']);
    });

    it('returns null for HS256 token signed with wrong key', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      // Sign with a different key than what the validator expects
      const token = createHS256Token(
        { sub: 'user-1', email: 'user@test.com' },
        'completely-wrong-secret-key'
      );

      const result = await validateToken(token);
      expect(result).toBeNull();
    });

    it('returns null for expired HS256 token', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      // Create a token that is already expired by using a past exp claim
      const pastTimestamp = Math.floor(Date.now() / 1000) - 3600; // 1 hour ago
      // Use jwt.sign directly to set exp without expiresIn conflict
      const token = jwt.sign(
        { sub: 'user-1', email: 'expired@test.com', exp: pastTimestamp },
        TEST_SECRET,
        { algorithm: 'HS256', issuer: TEST_LOCAL_ISSUER }
      );

      const result = await validateToken(token);
      expect(result).toBeNull();
    });

    it('uses default symmetric key when LOCAL_JWT_SECRET is not set', async () => {
      // Import WITHOUT setting LOCAL_JWT_SECRET — validator falls back to 'localstack-jwt-secret'
      const { validateToken } = await importLocalModule(undefined);
      // Remove LOCAL_JWT_SECRET to force default
      delete process.env.LOCAL_JWT_SECRET;
      // We need to re-import since the constant is evaluated at load time
      vi.resetModules();
      const mod2 = await import('../src/jwt-validator');

      // Sign with the default fallback key
      const token = createHS256Token(
        { sub: 'default-user', email: 'default@test.com' },
        DEFAULT_LOCAL_SECRET
      );

      const result = await mod2.validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.sub).toBe('default-user');
    });

    it('validates issuer when configured in LocalStack mode', async () => {
      // Set a custom issuer via COGNITO_ISSUER env var
      const customIssuer = 'https://custom-issuer.example.com';
      const { validateToken } = await importLocalModule(TEST_SECRET, {
        COGNITO_ISSUER: customIssuer,
      });

      // Token with correct issuer
      const validToken = createHS256Token(
        { sub: 'user-1' },
        TEST_SECRET,
        { issuer: customIssuer }
      );
      const validResult = await validateToken(validToken);
      expect(validResult).not.toBeNull();
      expect(validResult!.sub).toBe('user-1');

      // Token with wrong issuer
      const invalidToken = createHS256Token(
        { sub: 'user-1' },
        TEST_SECRET,
        { issuer: 'https://wrong-issuer.evil.com' }
      );
      const invalidResult = await validateToken(invalidToken);
      expect(invalidResult).toBeNull();
    });

    it('validates audience when COGNITO_AUDIENCE is configured', async () => {
      const expectedAudience = 'webvella-erp';
      const { validateToken } = await importLocalModule(TEST_SECRET, {
        COGNITO_AUDIENCE: expectedAudience,
      });

      // Token with correct audience
      const validToken = createHS256Token(
        { sub: 'user-aud' },
        TEST_SECRET,
        { audience: expectedAudience }
      );
      const validResult = await validateToken(validToken);
      expect(validResult).not.toBeNull();

      // Token with wrong audience
      const invalidToken = createHS256Token(
        { sub: 'user-aud' },
        TEST_SECRET,
        { audience: 'wrong-audience' }
      );
      const invalidResult = await validateToken(invalidToken);
      expect(invalidResult).toBeNull();
    });
  });

  // ==========================================================================
  // RS256 Cognito Mode Validation
  // ==========================================================================
  describe('RS256 Cognito Mode Validation', () => {
    it('validates a valid RS256 token in production mode', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      const token = createRS256Token({
        sub: 'cognito-user-uuid',
        email: 'admin@webvella.com',
        'cognito:groups': ['administrator'],
        token_use: 'access',
      });

      const result = await validateToken(token);

      expect(result).not.toBeNull();
      expect(result!.sub).toBe('cognito-user-uuid');
      expect(result!.email).toBe('admin@webvella.com');
      expect(result!['cognito:groups']).toEqual(['administrator']);
    });

    it('returns null when JWKS key retrieval fails', async () => {
      mockGetSigningKey.mockRejectedValue(new Error('Network error: JWKS endpoint unreachable'));
      const { validateToken } = await importProductionModule();

      const token = createRS256Token({
        sub: 'user-1',
      });

      const result = await validateToken(token);
      expect(result).toBeNull();
    });

    it('returns null when kid is missing from token header', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      // Create RS256 token WITHOUT kid in header
      const tokenNoKid = jwt.sign(
        { sub: 'user-no-kid', iss: TEST_COGNITO_ISSUER },
        rsaPrivateKey,
        { algorithm: 'RS256', expiresIn: '1h' } // No keyid
      );

      const result = await validateToken(tokenNoKid);
      expect(result).toBeNull();
    });

    it('returns null when kid does not match any JWKS key', async () => {
      // Mock returns error for unknown kids
      mockGetSigningKey.mockRejectedValue(
        new Error("SigningKeyNotFoundError: Unable to find a signing key that matches 'unknown-kid'")
      );
      const { validateToken } = await importProductionModule();

      const token = createRS256Token(
        { sub: 'user-wrong-kid' },
        rsaPrivateKey,
        { keyid: 'unknown-kid' }
      );

      const result = await validateToken(token);
      expect(result).toBeNull();
    });

    it('validates issuer matches Cognito user pool URL', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      // Token with correct Cognito issuer
      const validToken = createRS256Token(
        { sub: 'user-iss-ok', token_use: 'access' },
        rsaPrivateKey,
        { issuer: TEST_COGNITO_ISSUER }
      );
      const validResult = await validateToken(validToken);
      expect(validResult).not.toBeNull();
      expect(validResult!.sub).toBe('user-iss-ok');

      // Token with wrong issuer — jwt.verify will throw
      const wrongIssuerToken = createRS256Token(
        { sub: 'user-iss-bad' },
        rsaPrivateKey,
        { issuer: 'https://evil-cognito.example.com/bad-pool' }
      );
      const invalidResult = await validateToken(wrongIssuerToken);
      expect(invalidResult).toBeNull();
    });

    it('returns null for RS256 token signed with wrong private key', async () => {
      // JWKS returns the original public key, but the token is signed with alt key
      setupJwksMock(rsaPublicKey);
      const { validateToken } = await importProductionModule();

      const token = createRS256Token(
        { sub: 'user-wrong-key' },
        altRsaPrivateKey // Signed with different key
      );

      const result = await validateToken(token);
      expect(result).toBeNull();
    });
  });

  // ==========================================================================
  // Claims Extraction Tests
  // ==========================================================================
  describe('Claims Extraction', () => {
    it('extracts sub claim from validated token', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.sub).toBe('a1b2c3d4-e5f6-7890-abcd-ef1234567890');
    });

    it('extracts email claim from validated token', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'user-email-test',
        email: 'admin@webvella.com',
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.email).toBe('admin@webvella.com');
    });

    it('extracts cognito:groups claim from validated token', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'user-groups-test',
        'cognito:groups': ['administrator', 'regular'],
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!['cognito:groups']).toEqual(['administrator', 'regular']);
    });

    it('extracts token_use claim from Cognito tokens', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'user-token-use',
        token_use: 'access',
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.token_use).toBe('access');
    });

    it('preserves all standard JWT claims (iat, exp, iss)', async () => {
      const customIssuer = 'https://custom-issuer.test.com';
      const { validateToken } = await importLocalModule(TEST_SECRET, {
        COGNITO_ISSUER: customIssuer,
      });

      const token = createHS256Token(
        { sub: 'user-standard-claims', email: 'claims@test.com' },
        TEST_SECRET,
        { issuer: customIssuer, expiresIn: '2h' }
      );

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.iat).toBeDefined();
      expect(typeof result!.iat).toBe('number');
      expect(result!.exp).toBeDefined();
      expect(typeof result!.exp).toBe('number');
      expect(result!.iss).toBe(customIssuer);
    });
  });

  // ==========================================================================
  // Error Handling and Edge Cases
  // ==========================================================================
  describe('Error Handling', () => {
    it('returns null for completely malformed token string', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const result = await validateToken('not-a-jwt-at-all');
      expect(result).toBeNull();
    });

    it('returns null for empty string token', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const result = await validateToken('');
      expect(result).toBeNull();
    });

    it('returns null for null input', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const result = await validateToken(null as any);
      expect(result).toBeNull();
    });

    it('returns null for undefined input', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const result = await validateToken(undefined as any);
      expect(result).toBeNull();
    });

    it('never throws an exception for any input', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const maliciousInputs = [
        '',
        'x',
        'not.valid.jwt',
        'aaa.bbb',
        'aaa.bbb.ccc.ddd',
        Buffer.from('random bytes data for testing').toString('base64'),
        'a'.repeat(10000), // extremely long string
        'eyJhbGciOiJIUzI1NiJ9.eyJ0ZXN0IjoiMSJ9', // header + payload, no signature
        '{}',
        '{"alg":"none"}',
        123 as any,
        true as any,
        {} as any,
        [] as any,
      ];

      for (const input of maliciousInputs) {
        let threw = false;
        try {
          const result = await validateToken(input);
          // Regardless of input, result should be null (fail-closed)
          expect(result).toBeNull();
        } catch {
          threw = true;
        }
        // The critical guarantee: validateToken NEVER throws
        expect(threw).toBe(false);
      }
    });

    it('returns null for token with only header and no payload/signature', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      // Partial JWT — just a base64-encoded header
      const result = await validateToken('eyJhbGciOiJIUzI1NiJ9.');
      expect(result).toBeNull();
    });

    it('returns null for token with tampered payload', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      // Create a valid token
      const validToken = createHS256Token(
        { sub: 'original-user', email: 'original@test.com' },
        TEST_SECRET
      );

      // Tamper with the payload segment (second part between dots)
      const parts = validToken.split('.');
      // Modify the payload by flipping a character
      const tamperedPayload = parts[1].slice(0, -1) +
        (parts[1].slice(-1) === 'A' ? 'B' : 'A');
      const tamperedToken = `${parts[0]}.${tamperedPayload}.${parts[2]}`;

      const result = await validateToken(tamperedToken);
      expect(result).toBeNull();
    });
  });

  // ==========================================================================
  // IS_LOCAL Environment Variable Toggle Tests
  // ==========================================================================
  describe('IS_LOCAL Environment Variable', () => {
    it('uses HS256 validation when IS_LOCAL is true', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({ sub: 'local-user' });
      const result = await validateToken(token);

      expect(result).not.toBeNull();
      expect(result!.sub).toBe('local-user');
      // JWKS client should NOT have been called
      expect(mockGetSigningKey).not.toHaveBeenCalled();
    });

    it('uses RS256 JWKS validation when IS_LOCAL is false', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      const token = createRS256Token(
        { sub: 'prod-user', token_use: 'access' },
        rsaPrivateKey,
        { issuer: TEST_COGNITO_ISSUER }
      );

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.sub).toBe('prod-user');
      // JWKS client SHOULD have been called for RS256 mode
      expect(mockGetSigningKey).toHaveBeenCalledWith(TEST_KID);
    });

    it('uses RS256 JWKS validation when IS_LOCAL is not set', async () => {
      setupJwksMock();

      // Explicitly remove IS_LOCAL and re-import
      vi.resetModules();
      delete process.env.IS_LOCAL;
      process.env.AWS_REGION = TEST_REGION;
      process.env.COGNITO_USER_POOL_ID = TEST_USER_POOL_ID;
      const mod = await import('../src/jwt-validator');

      const token = createRS256Token(
        { sub: 'default-mode-user', token_use: 'access' },
        rsaPrivateKey,
        { issuer: TEST_COGNITO_ISSUER }
      );

      const result = await mod.validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.sub).toBe('default-mode-user');
    });

    it('IS_LOCAL=true accepts RS256 tokens via Cognito JWKS fallback', async () => {
      // LocalStack Cognito issues RS256-signed JWTs, so local mode tries
      // RS256 JWKS validation first, then falls back to HS256.
      // When a valid JWKS key is available, RS256 tokens succeed.
      setupJwksMock();
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createRS256Token(
        { sub: 'rs256-in-local' },
        rsaPrivateKey,
        { issuer: TEST_LOCAL_ISSUER }
      );

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.sub).toBe('rs256-in-local');
    });

    it('IS_LOCAL=false rejects HS256 tokens (no kid for JWKS lookup)', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      // HS256 token has no kid header — Cognito mode requires kid for JWKS
      const token = createHS256Token({ sub: 'hs256-in-prod' });

      const result = await validateToken(token);
      expect(result).toBeNull();
    });
  });

  // ==========================================================================
  // Security Tests
  // ==========================================================================
  describe('Security', () => {
    it('rejects tokens with none algorithm', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      // Construct an unsigned token with algorithm: 'none'
      // This tests protection against the well-known JWT 'none' algorithm bypass
      const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
      const payload = Buffer.from(JSON.stringify({
        sub: 'hacker',
        email: 'attacker@evil.com',
        iat: Math.floor(Date.now() / 1000),
        exp: Math.floor(Date.now() / 1000) + 3600,
      })).toString('base64url');
      const unsignedToken = `${header}.${payload}.`;

      const result = await validateToken(unsignedToken);
      expect(result).toBeNull();
    });

    it('rejects tokens with none algorithm in production mode', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
      const payload = Buffer.from(JSON.stringify({
        sub: 'hacker',
        iat: Math.floor(Date.now() / 1000),
        exp: Math.floor(Date.now() / 1000) + 3600,
      })).toString('base64url');
      const unsignedToken = `${header}.${payload}.`;

      const result = await validateToken(unsignedToken);
      expect(result).toBeNull();
    });

    it('does not log token content in validation errors', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const consoleWarnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

      const sensitiveToken = 'secret-data-in-token-that-should-not-appear-in-logs';
      await validateToken(sensitiveToken);

      // Check that the literal token string was NOT logged
      for (const call of consoleSpy.mock.calls) {
        const logOutput = call.map(arg => String(arg)).join(' ');
        expect(logOutput).not.toContain(sensitiveToken);
      }
      for (const call of consoleWarnSpy.mock.calls) {
        const logOutput = call.map(arg => String(arg)).join(' ');
        expect(logOutput).not.toContain(sensitiveToken);
      }

      consoleSpy.mockRestore();
      consoleWarnSpy.mockRestore();
    });

    it('rejects token with algorithm confusion attack (HS256 with public key)', async () => {
      // Attacker signs an HS256 token using the RS256 public key as the secret
      // A vulnerable implementation would verify this with the public key
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const maliciousToken = jwt.sign(
        { sub: 'attacker', email: 'hack@evil.com' },
        rsaPublicKey, // Using public key as HS256 secret
        { algorithm: 'HS256', expiresIn: '1h' }
      );

      const result = await validateToken(maliciousToken);
      // Should be null because TEST_SECRET is the expected key, not the public key
      expect(result).toBeNull();
    });
  });

  // ==========================================================================
  // Additional Edge Cases for Complete Coverage
  // ==========================================================================
  describe('Additional Coverage', () => {
    it('handles token with multiple cognito:groups correctly', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const roles = ['administrator', 'regular', 'guest', 'custom-role'];
      const token = createHS256Token({
        sub: 'multi-role-user',
        'cognito:groups': roles,
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!['cognito:groups']).toEqual(roles);
      expect(result!['cognito:groups']!.length).toBe(4);
    });

    it('handles token with empty cognito:groups array', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'no-role-user',
        'cognito:groups': [],
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!['cognito:groups']).toEqual([]);
    });

    it('validates token with additional custom claims preserved', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const token = createHS256Token({
        sub: 'custom-claims-user',
        email: 'custom@test.com',
        custom_field: 'custom_value',
        numeric_claim: 42,
      });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect((result as any).custom_field).toBe('custom_value');
      expect((result as any).numeric_claim).toBe(42);
    });

    it('returns null for whitespace-only token string', async () => {
      const { validateToken } = await importLocalModule(TEST_SECRET);

      const result = await validateToken('   ');
      expect(result).toBeNull();
    });

    it('RS256 mode: validates token with full Cognito-like payload', async () => {
      setupJwksMock();
      const { validateToken } = await importProductionModule();

      // Simulate a realistic Cognito access token
      const token = createRS256Token({
        sub: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890',
        email: 'erp@webvella.com',
        'cognito:groups': ['administrator'],
        token_use: 'access',
        scope: 'openid email profile',
        auth_time: Math.floor(Date.now() / 1000),
        client_id: 'test-client-id-12345',
      }, rsaPrivateKey, { issuer: TEST_COGNITO_ISSUER });

      const result = await validateToken(token);
      expect(result).not.toBeNull();
      expect(result!.sub).toBe('a1b2c3d4-e5f6-7890-abcd-ef1234567890');
      expect(result!.email).toBe('erp@webvella.com');
      expect(result!.token_use).toBe('access');
    });
  });
});
