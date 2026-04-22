/**
 * Unit tests for the Cognito User Migration Lambda trigger
 * (`services/identity/src/triggers/user-migration/index.js`).
 *
 * These tests run under Vitest 3.x at the workspace root; no AWS calls
 * are made — the DynamoDB client is stubbed via `__setDynamoDbClient`.
 *
 * Run via:   npx vitest run services/identity/src/triggers/__tests__/user-migration.test.mjs
 *
 * Per the Phase 2 Security Review (Check 2.9) these tests must prove:
 *   1. MD5 hash reproduces PasswordUtil.GetMd5Hash exactly (UTF-8 encoding,
 *      lowercase hex, `MD5("erp") === "def6d90e829e50c63f98c387daecd138"`).
 *   2. Hash comparison is CASE-INSENSITIVE (matches
 *      `StringComparer.OrdinalIgnoreCase` on source line 28).
 *   3. Hash comparison is CONSTANT-TIME (uses `crypto.timingSafeEqual`).
 *   4. Successful migration populates the Cognito response correctly.
 *   5. Wrong password, missing user, missing hash, unknown trigger source
 *      all handled safely without information leakage.
 */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import crypto from 'node:crypto';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const require = createRequire(import.meta.url);

// Load the CJS handler via createRequire so Vitest treats it as a single
// module (otherwise ESM ↔ CJS interop duplicates state).
const migrationModule = require(
  path.resolve(__dirname, '../user-migration/index.js'),
);
const { handler, __internals } = migrationModule;
const {
  computeMd5Hash,
  compareHashesConstantTime,
  findLegacyUserByEmail,
  extractLegacyPasswordHash,
  populateCognitoResponse,
  unmarshalItem,
  maskEmail,
  __setDynamoDbClient,
  __resetDynamoDbClient,
} = __internals;

// ---------------------------------------------------------------------------
// computeMd5Hash — AAP §0.7.5 correctness
// ---------------------------------------------------------------------------

describe('computeMd5Hash (MD5 reproduction of PasswordUtil.GetMd5Hash)', () => {
  it('hashes "erp" to the well-known lowercase hex digest', () => {
    // Source: WebVella.Erp/Utilities/PasswordUtil.cs line 16 (UTF-8 encoding).
    // MD5(UTF-8("erp")) = def6d90e829e50c63f98c387daecd138
    expect(computeMd5Hash('erp')).toBe('def6d90e829e50c63f98c387daecd138');
  });

  it('returns the empty string for null / undefined / empty / whitespace', () => {
    expect(computeMd5Hash('')).toBe('');
    expect(computeMd5Hash('   ')).toBe('');
    expect(computeMd5Hash(null)).toBe('');
    expect(computeMd5Hash(undefined)).toBe('');
    expect(computeMd5Hash(42)).toBe('');
  });

  it('produces lowercase hex of length 32 for arbitrary ASCII input', () => {
    const hash = computeMd5Hash('TestPassword123!');
    expect(hash).toMatch(/^[0-9a-f]{32}$/);
  });

  it('uses UTF-8 encoding (not UTF-16LE) — differs from CryptoUtility.ComputeOddMD5Hash', () => {
    const input = 'pässwörd';
    const utf8Hash = computeMd5Hash(input);
    const utf16Hash = crypto
      .createHash('md5')
      .update(Buffer.from(input, 'utf16le'))
      .digest('hex');
    expect(utf8Hash).not.toBe(utf16Hash);
  });
});

// ---------------------------------------------------------------------------
// compareHashesConstantTime — security behaviour
// ---------------------------------------------------------------------------

describe('compareHashesConstantTime (CWE-208 constant-time comparison)', () => {
  it('returns true when hashes are identical', () => {
    const h = 'def6d90e829e50c63f98c387daecd138';
    expect(compareHashesConstantTime(h, h)).toBe(true);
  });

  it('is case-insensitive (matches StringComparer.OrdinalIgnoreCase)', () => {
    const lower = 'def6d90e829e50c63f98c387daecd138';
    const upper = lower.toUpperCase();
    expect(compareHashesConstantTime(lower, upper)).toBe(true);
    expect(compareHashesConstantTime(upper, lower)).toBe(true);
  });

  it('returns false for mismatched hashes of the same length', () => {
    const a = '00000000000000000000000000000000';
    const b = 'ffffffffffffffffffffffffffffffff';
    expect(compareHashesConstantTime(a, b)).toBe(false);
  });

  it('returns false (without throwing) for length-mismatched inputs', () => {
    expect(compareHashesConstantTime('abc', 'abcdef')).toBe(false);
    expect(compareHashesConstantTime('', 'abc')).toBe(false);
  });

  it('returns false for non-string inputs (defensive typing)', () => {
    expect(compareHashesConstantTime(null, 'abc')).toBe(false);
    expect(compareHashesConstantTime('abc', undefined)).toBe(false);
    expect(compareHashesConstantTime(123, 456)).toBe(false);
  });

  it('uses crypto.timingSafeEqual under the hood', () => {
    // Delegate behavioural proof to Node's timingSafeEqual: compare
    // equal-length, different-content hashes — we should still get `false`
    // (no throw) and it should behave as documented.
    const spy = vi.spyOn(crypto, 'timingSafeEqual');
    const result = compareHashesConstantTime(
      'abcdef0123456789abcdef0123456789',
      'fedcba9876543210fedcba9876543210',
    );
    expect(spy).toHaveBeenCalledTimes(1);
    expect(result).toBe(false);
    spy.mockRestore();
  });
});

// ---------------------------------------------------------------------------
// extractLegacyPasswordHash
// ---------------------------------------------------------------------------

describe('extractLegacyPasswordHash', () => {
  it('prefers the "password" attribute (production schema)', () => {
    expect(
      extractLegacyPasswordHash({
        password: 'abc123',
        legacy_password_hash: 'ignored',
      }),
    ).toBe('abc123');
  });

  it('falls back to "legacy_password_hash" when password is empty / missing', () => {
    expect(
      extractLegacyPasswordHash({
        password: '',
        legacy_password_hash: 'fallback',
      }),
    ).toBe('fallback');
    expect(
      extractLegacyPasswordHash({ legacy_password_hash: 'fallback' }),
    ).toBe('fallback');
  });

  it('returns "" when neither attribute is usable', () => {
    expect(extractLegacyPasswordHash({})).toBe('');
    expect(extractLegacyPasswordHash(null)).toBe('');
    expect(extractLegacyPasswordHash({ password: '   ' })).toBe('');
  });
});

// ---------------------------------------------------------------------------
// unmarshalItem
// ---------------------------------------------------------------------------

describe('unmarshalItem', () => {
  it('converts AttributeValue maps to plain JS objects', () => {
    const item = {
      PK: { S: 'USER#abc' },
      SK: { S: 'PROFILE' },
      enabled: { BOOL: true },
      verified: { BOOL: false },
      attempts: { N: '5' },
      notes: { NULL: true },
    };
    expect(unmarshalItem(item)).toEqual({
      PK: 'USER#abc',
      SK: 'PROFILE',
      enabled: true,
      verified: false,
      attempts: 5,
      notes: null,
    });
  });

  it('returns null for falsy or non-object inputs', () => {
    expect(unmarshalItem(null)).toBeNull();
    expect(unmarshalItem(undefined)).toBeNull();
    expect(unmarshalItem('not-an-item')).toBeNull();
  });

  it('skips attributes with unsupported shapes', () => {
    const item = {
      PK: { S: 'USER#abc' },
      something: { L: [{ S: 'list' }] }, // lists not unmarshalled
    };
    expect(unmarshalItem(item)).toEqual({ PK: 'USER#abc' });
  });
});

// ---------------------------------------------------------------------------
// findLegacyUserByEmail — DynamoDB stub
// ---------------------------------------------------------------------------

describe('findLegacyUserByEmail', () => {
  afterEach(() => {
    __resetDynamoDbClient();
  });

  it('queries GSI1 with the lowercased email and returns the unmarshalled item', async () => {
    const sent = [];
    const stub = {
      send: async (cmd) => {
        sent.push(cmd);
        return {
          Items: [
            {
              PK: { S: 'USER#1' },
              SK: { S: 'PROFILE' },
              email: { S: 'erp@webvella.com' },
              username: { S: 'erp' },
              password: { S: 'def6d90e829e50c63f98c387daecd138' },
            },
          ],
        };
      },
    };
    __setDynamoDbClient(stub);

    const result = await findLegacyUserByEmail('ERP@WEBVELLA.COM');

    expect(result).toEqual({
      PK: 'USER#1',
      SK: 'PROFILE',
      email: 'erp@webvella.com',
      username: 'erp',
      password: 'def6d90e829e50c63f98c387daecd138',
    });
    expect(sent).toHaveLength(1);
    const cmdInput = sent[0].input;
    expect(cmdInput.IndexName).toBe('GSI1');
    expect(cmdInput.ExpressionAttributeValues[':pk'].S).toBe(
      'EMAIL#erp@webvella.com',
    );
    expect(cmdInput.ExpressionAttributeValues[':sk'].S).toBe('USER');
    expect(cmdInput.Limit).toBe(1);
  });

  it('returns null when the query yields zero items', async () => {
    __setDynamoDbClient({
      send: async () => ({ Items: [] }),
    });
    expect(await findLegacyUserByEmail('unknown@nowhere.io')).toBeNull();
  });

  it('returns null for blank / non-string email input', async () => {
    __setDynamoDbClient({
      send: async () => {
        throw new Error('should not be called');
      },
    });
    expect(await findLegacyUserByEmail('')).toBeNull();
    expect(await findLegacyUserByEmail('   ')).toBeNull();
    expect(await findLegacyUserByEmail(null)).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// populateCognitoResponse
// ---------------------------------------------------------------------------

describe('populateCognitoResponse', () => {
  it('sets userAttributes, finalUserStatus, messageAction, forceAliasCreation', () => {
    const event = {};
    populateCognitoResponse(event, {
      email: 'admin@webvella.com',
      username: 'admin',
      first_name: 'Alice',
      last_name: 'Admin',
      id: '1111-2222-3333-4444',
    });
    expect(event.response).toEqual({
      userAttributes: {
        email: 'admin@webvella.com',
        email_verified: 'true',
        preferred_username: 'admin',
        given_name: 'Alice',
        family_name: 'Admin',
        'custom:erp_user_id': '1111-2222-3333-4444',
      },
      finalUserStatus: 'CONFIRMED',
      messageAction: 'SUPPRESS',
      forceAliasCreation: false,
    });
  });

  it('omits optional attributes when not present', () => {
    const event = {};
    populateCognitoResponse(event, { email: 'only@email.com' });
    expect(event.response.userAttributes).toEqual({
      email: 'only@email.com',
      email_verified: 'true',
    });
    expect(event.response.finalUserStatus).toBe('CONFIRMED');
    expect(event.response.messageAction).toBe('SUPPRESS');
  });
});

// ---------------------------------------------------------------------------
// maskEmail
// ---------------------------------------------------------------------------

describe('maskEmail', () => {
  it('truncates the local part to 3 chars and preserves the domain', () => {
    expect(maskEmail('jdoe@example.com')).toBe('jdo***@example.com');
  });
  it('handles short local parts (no out-of-bounds)', () => {
    expect(maskEmail('a@b.io')).toBe('a***@b.io');
  });
  it('returns a sentinel for missing / invalid inputs', () => {
    expect(maskEmail(undefined)).toBe('<none>');
    expect(maskEmail('')).toBe('<none>');
    expect(maskEmail('no-at-sign')).toBe('<invalid>');
    expect(maskEmail('@leading-at')).toBe('<invalid>');
  });
});

// ---------------------------------------------------------------------------
// handler — end-to-end trigger flow
// ---------------------------------------------------------------------------

describe('handler (Cognito UserMigration trigger)', () => {
  beforeEach(() => {
    __resetDynamoDbClient();
  });
  afterEach(() => {
    __resetDynamoDbClient();
  });

  const baseEvent = (overrides = {}) => ({
    triggerSource: 'UserMigration_Authentication',
    userName: 'erp@webvella.com',
    request: { password: 'erp' },
    response: {},
    ...overrides,
  });

  const userItem = {
    PK: { S: 'USER#eabd66fd-8de1-4d79-9674-447ee89921c2' },
    SK: { S: 'PROFILE' },
    email: { S: 'erp@webvella.com' },
    username: { S: 'erp' },
    first_name: { S: 'ERP' },
    last_name: { S: 'Admin' },
    id: { S: 'eabd66fd-8de1-4d79-9674-447ee89921c2' },
    password: { S: 'def6d90e829e50c63f98c387daecd138' },
  };

  it('succeeds for the default system user (erp / erp) and populates the response', async () => {
    __setDynamoDbClient({
      send: async () => ({ Items: [userItem] }),
    });

    const event = baseEvent();
    const result = await handler(event);

    expect(result.response.userAttributes.email).toBe('erp@webvella.com');
    expect(result.response.userAttributes.email_verified).toBe('true');
    expect(result.response.userAttributes.preferred_username).toBe('erp');
    expect(result.response.userAttributes.given_name).toBe('ERP');
    expect(result.response.userAttributes.family_name).toBe('Admin');
    expect(result.response.userAttributes['custom:erp_user_id']).toBe(
      'eabd66fd-8de1-4d79-9674-447ee89921c2',
    );
    expect(result.response.finalUserStatus).toBe('CONFIRMED');
    expect(result.response.messageAction).toBe('SUPPRESS');
  });

  it('also accepts the legacy_password_hash attribute (test-fixture schema)', async () => {
    __setDynamoDbClient({
      send: async () => ({
        Items: [
          {
            ...userItem,
            password: { S: '' }, // empty password → fall back to legacy_password_hash
            legacy_password_hash: {
              S: 'def6d90e829e50c63f98c387daecd138',
            },
          },
        ],
      }),
    });

    const result = await handler(baseEvent());
    expect(result.response.finalUserStatus).toBe('CONFIRMED');
  });

  it('throws a generic error when the user is not found', async () => {
    __setDynamoDbClient({
      send: async () => ({ Items: [] }),
    });
    await expect(handler(baseEvent())).rejects.toThrow(
      'User migration failed',
    );
  });

  it('throws a generic error when the password does not match', async () => {
    __setDynamoDbClient({
      send: async () => ({ Items: [userItem] }),
    });
    await expect(
      handler(baseEvent({ request: { password: 'WRONG_PASSWORD' } })),
    ).rejects.toThrow('User migration failed');
  });

  it('throws a generic error when the user has no legacy hash', async () => {
    __setDynamoDbClient({
      send: async () => ({
        Items: [{ ...userItem, password: { S: '' } }],
      }),
    });
    await expect(handler(baseEvent())).rejects.toThrow(
      'User migration failed',
    );
  });

  it('throws a generic error when no password is supplied (no info leak)', async () => {
    __setDynamoDbClient({
      send: async () => ({ Items: [userItem] }),
    });
    await expect(
      handler(baseEvent({ request: { password: '' } })),
    ).rejects.toThrow('User migration failed');
  });

  it('masks DynamoDB errors as a generic failure (no info leak)', async () => {
    __setDynamoDbClient({
      send: async () => {
        throw new Error('DynamoDB unavailable: specific-internal-detail');
      },
    });
    await expect(handler(baseEvent())).rejects.toThrow(
      'User migration failed',
    );
  });

  it('supports UserMigration_ForgotPassword without password verification', async () => {
    __setDynamoDbClient({
      send: async () => ({ Items: [userItem] }),
    });
    const event = baseEvent({
      triggerSource: 'UserMigration_ForgotPassword',
      request: {}, // no password for forgot-password flow
    });
    const result = await handler(event);
    expect(result.response.userAttributes.email).toBe('erp@webvella.com');
    expect(result.response.messageAction).toBe('SUPPRESS');
  });

  it('returns unchanged event for non-migration trigger sources', async () => {
    const event = {
      triggerSource: 'PreSignUp_SignUp',
      userName: 'whatever@example.com',
      request: {},
      response: { originalField: 'preserved' },
    };
    const result = await handler(event);
    expect(result).toBe(event);
    expect(result.response).toEqual({ originalField: 'preserved' });
  });

  it('uses case-insensitive hash comparison (uppercase stored hash)', async () => {
    const uppercaseHashItem = {
      ...userItem,
      password: { S: 'DEF6D90E829E50C63F98C387DAECD138' },
    };
    __setDynamoDbClient({
      send: async () => ({ Items: [uppercaseHashItem] }),
    });

    const result = await handler(baseEvent());
    expect(result.response.finalUserStatus).toBe('CONFIRMED');
  });
});
