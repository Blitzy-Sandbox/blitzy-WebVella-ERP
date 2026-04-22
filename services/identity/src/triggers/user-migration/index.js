'use strict';

/**
 * Cognito User Migration Lambda Trigger — Constant-Time MD5 → Cognito Migration
 * ============================================================================
 *
 * Handles the `UserMigration_Authentication` and `UserMigration_ForgotPassword`
 * trigger events for the WebVella ERP Cognito user pool.
 *
 * Purpose (per AAP §0.7.5 — Authentication Migration Path):
 * --------------------------------------------------------
 * The legacy WebVella ERP monolith stored passwords as MD5 hashes in
 * PostgreSQL (via `SecurityManager` + `PasswordUtil.GetMd5Hash`). Cognito
 * does not natively support MD5, so a transparent migration is required:
 *
 *   1. When a legacy user logs in for the first time against Cognito, the
 *      user will not exist in the user pool and Cognito fires the
 *      `UserMigration_Authentication` trigger.
 *   2. This Lambda looks up the user by email in DynamoDB (identity table,
 *      GSI1: `EMAIL#{email}`) and retrieves the stored MD5 hash from either
 *      the `password` attribute (production schema) or the
 *      `legacy_password_hash` attribute (test / monolith-import schema).
 *   3. The supplied plaintext password is hashed with the **exact same**
 *      algorithm used by the monolith (`MD5(UTF-8(input))` → lowercase
 *      hex), reproducing `PasswordUtil.GetMd5Hash` (WebVella.Erp /
 *      Utilities / PasswordUtil.cs lines 11-23).
 *   4. The computed hash is compared to the stored hash using
 *      **`crypto.timingSafeEqual`** — a constant-time byte comparison that
 *      prevents timing side-channel attacks (CWE-208). Hashes are
 *      normalised to lowercase first to preserve the case-insensitive
 *      behaviour of `PasswordUtil.VerifyMd5Hash` (source line 28, which
 *      uses `StringComparer.OrdinalIgnoreCase`).
 *   5. On success, the trigger returns the event with
 *      `event.response.userAttributes` populated, `finalUserStatus =
 *      'CONFIRMED'` (so no `FORCE_CHANGE_PASSWORD` challenge is issued)
 *      and `messageAction = 'SUPPRESS'` (no welcome email). Cognito then
 *      stores the supplied password via its own secure SRP-based hashing,
 *      replacing the MD5 hash forever.
 *   6. On failure (user missing / password mismatch / internal error) the
 *      handler throws — causing Cognito to return `UserNotFoundException`
 *      to the caller (this is the correct behaviour: leaks nothing about
 *      whether the user exists).
 *
 * Security Properties:
 * --------------------
 * - Constant-time password comparison via `crypto.timingSafeEqual`
 *   (Node.js built-in, backed by OpenSSL's `CRYPTO_memcmp`) on equal-
 *   length Buffers — defeats timing side-channel attacks.
 * - No password or hash is ever logged, even at debug level.
 * - `AWS_ENDPOINT_URL` override is only honoured when `IS_LOCAL=true` —
 *   production Lambdas always talk to real DynamoDB.
 * - IAM permissions for this Lambda (see
 *   `infra/src/stacks/shared-stack.ts`) are scoped to
 *   `dynamodb:GetItem|Query` on the identity table only.
 *
 * AWS SDK: The handler depends only on `@aws-sdk/client-dynamodb`, which
 * is available in the Node.js 22 Lambda managed runtime — no external
 * dependencies are bundled with the asset.
 *
 * @see AAP §0.7.5 Authentication Migration Path
 * @see services/identity/src/Services/CognitoService.cs — `MigrateUserPasswordAsync`
 * @see services/identity/tests/Integration/UserMigrationIntegrationTests.cs
 */

const crypto = require('crypto');
const {
  DynamoDBClient,
  QueryCommand,
} = require('@aws-sdk/client-dynamodb');

// ---------------------------------------------------------------------------
// Configuration constants
// ---------------------------------------------------------------------------

/**
 * DynamoDB identity table name — defaults to `identity` but is overridden
 * at deploy time via the `IDENTITY_TABLE_NAME` environment variable (set
 * by the CDK `shared-stack` construct that wires up this trigger).
 * @type {string}
 */
const TABLE_NAME = process.env.IDENTITY_TABLE_NAME || 'identity';

/**
 * AWS region — inherited from the Lambda execution environment. The
 * DynamoDB SDK will fall back to `AWS_REGION` / `AWS_DEFAULT_REGION`
 * automatically if this is unset.
 * @type {string}
 */
const REGION = process.env.AWS_REGION || process.env.AWS_DEFAULT_REGION || 'us-east-1';

/**
 * When running against LocalStack the DynamoDB client must point to the
 * LocalStack endpoint. `IS_LOCAL` is provided by the CDK stack when the
 * `--context localstack=true` flag is used.
 * @type {boolean}
 */
const IS_LOCAL = process.env.IS_LOCAL === 'true';

/**
 * DynamoDB key / GSI field names — mirror the constants in
 * `services/identity/src/DataAccess/UserRepository.cs` (lines 151-176).
 * Changing these here without changing the repository will desynchronise
 * legacy user lookup.
 */
const PK_ATTR = 'PK';
const SK_ATTR = 'SK';
const GSI1_NAME = 'GSI1';
const GSI1_PK_ATTR = 'GSI1PK';
const GSI1_SK_ATTR = 'GSI1SK';
const EMAIL_PREFIX = 'EMAIL#';
const USER_GSI1_SK = 'USER';

/**
 * Cognito trigger source identifiers that this handler responds to. All
 * other trigger sources are logged and returned unchanged (no-op).
 * @see https://docs.aws.amazon.com/cognito/latest/developerguide/user-pool-lambda-migrate-user.html
 */
const TRIGGER_AUTHENTICATION = 'UserMigration_Authentication';
const TRIGGER_FORGOT_PASSWORD = 'UserMigration_ForgotPassword';

// ---------------------------------------------------------------------------
// DynamoDB client singleton (reused across warm invocations for performance)
// ---------------------------------------------------------------------------

/**
 * Constructs the DynamoDB client, layering in the LocalStack endpoint
 * override only when `IS_LOCAL` is true. The client is created lazily
 * once per warm Lambda container.
 * @returns {DynamoDBClient}
 */
function createDynamoDbClient() {
  const clientConfig = { region: REGION };
  if (IS_LOCAL && process.env.AWS_ENDPOINT_URL) {
    clientConfig.endpoint = process.env.AWS_ENDPOINT_URL;
  }
  return new DynamoDBClient(clientConfig);
}

/** @type {DynamoDBClient|null} */
let dynamoDbClient = null;

/**
 * Accessor that initialises the DynamoDB client on first use and returns
 * the cached instance on subsequent invocations. Tests override this by
 * calling `__setDynamoDbClient` to inject a mock.
 * @returns {DynamoDBClient}
 */
function getDynamoDbClient() {
  if (dynamoDbClient === null) {
    dynamoDbClient = createDynamoDbClient();
  }
  return dynamoDbClient;
}

// ---------------------------------------------------------------------------
// Cryptographic helpers
// ---------------------------------------------------------------------------

/**
 * Computes the MD5 hash of the UTF-8 encoding of `input`, returning the
 * digest as a lowercase hexadecimal string.
 *
 * **Must match exactly** the monolith's `PasswordUtil.GetMd5Hash`
 * (WebVella.Erp / Utilities / PasswordUtil.cs lines 11-23):
 *   - Uses `Encoding.UTF8` (NOT `Encoding.Unicode` — `CryptoUtility.
 *     ComputeOddMD5Hash` is a different method and must not be
 *     confused with password hashing).
 *   - Returns empty string when input is null / empty / whitespace
 *     (mirrors the `string.IsNullOrWhiteSpace` branch on line 13).
 *   - Emits lowercase hex via the `x2` format specifier equivalent.
 *
 * @param {string} input - the plaintext password to hash.
 * @returns {string} - the lowercase hex MD5 digest, or '' for empty input.
 */
function computeMd5Hash(input) {
  if (typeof input !== 'string' || input.trim().length === 0) {
    return '';
  }
  // Node's crypto module uses OpenSSL's MD5; .digest('hex') returns lowercase.
  return crypto.createHash('md5').update(input, 'utf8').digest('hex');
}

/**
 * Constant-time comparison of two hex-encoded MD5 hashes, tolerating
 * casing differences (PasswordUtil.VerifyMd5Hash uses
 * `StringComparer.OrdinalIgnoreCase`).
 *
 * The two hashes are:
 *   1. Trimmed of surrounding whitespace.
 *   2. Lowercased (this is safe — hash casing carries no entropy).
 *   3. Converted to equal-length `Buffer` instances.
 *   4. Compared byte-wise via `crypto.timingSafeEqual`, which runs in
 *      time independent of the position of the first differing byte —
 *      defeating timing attacks (CWE-208).
 *
 * If lengths differ the function returns `false` immediately *without*
 * calling `timingSafeEqual` (which requires equal-length inputs). This
 * length mismatch is not timing-sensitive because hash length is a
 * public invariant (MD5 is always 32 hex chars).
 *
 * @param {string} computedHash - hash computed from the supplied password.
 * @param {string} storedHash - hash loaded from DynamoDB for the user.
 * @returns {boolean} - `true` iff the two hashes are equal ignoring case.
 */
function compareHashesConstantTime(computedHash, storedHash) {
  if (typeof computedHash !== 'string' || typeof storedHash !== 'string') {
    return false;
  }
  const a = computedHash.trim().toLowerCase();
  const b = storedHash.trim().toLowerCase();
  if (a.length === 0 || b.length === 0 || a.length !== b.length) {
    return false;
  }
  const bufA = Buffer.from(a, 'utf8');
  const bufB = Buffer.from(b, 'utf8');
  if (bufA.length !== bufB.length) {
    return false;
  }
  try {
    return crypto.timingSafeEqual(bufA, bufB);
  } catch (_err) {
    // timingSafeEqual throws on length mismatch in older Node versions;
    // we already guarded against that above but defend against drift.
    return false;
  }
}

// ---------------------------------------------------------------------------
// DynamoDB access helpers
// ---------------------------------------------------------------------------

/**
 * Looks up a legacy user record in DynamoDB by email using GSI1.
 *
 * Matches the production query pattern implemented in
 * `UserRepository.GetUserByEmailAsync` (lines 242-268). Email lookup
 * is case-insensitive — emails are normalised to lowercase before
 * building the GSI1 partition key.
 *
 * @param {string} email - the raw email provided by Cognito (`userName`).
 * @param {DynamoDBClient} [client] - optional DynamoDB client override
 *   for tests; defaults to the module-level singleton.
 * @returns {Promise<object|null>} - the legacy user item map (attribute
 *   names are the DynamoDB keys, values are the native form), or `null`
 *   if the user does not exist.
 */
async function findLegacyUserByEmail(email, client) {
  if (typeof email !== 'string' || email.trim().length === 0) {
    return null;
  }
  const normalisedEmail = email.trim().toLowerCase();
  const dynamo = client || getDynamoDbClient();

  const response = await dynamo.send(new QueryCommand({
    TableName: TABLE_NAME,
    IndexName: GSI1_NAME,
    KeyConditionExpression: `${GSI1_PK_ATTR} = :pk AND begins_with(${GSI1_SK_ATTR}, :sk)`,
    ExpressionAttributeValues: {
      ':pk': { S: `${EMAIL_PREFIX}${normalisedEmail}` },
      ':sk': { S: USER_GSI1_SK },
    },
    Limit: 1,
  }));

  if (!response.Items || response.Items.length === 0) {
    return null;
  }
  return unmarshalItem(response.Items[0]);
}

/**
 * Converts a DynamoDB `AttributeValue` map into a plain JavaScript object.
 * Only supports the attribute types we actually read from identity items
 * (S = string, BOOL = boolean, N = number, NULL = null). This avoids a
 * dependency on `@aws-sdk/util-dynamodb` which is not always included in
 * the Lambda-provided runtime.
 *
 * @param {object} item - DynamoDB item in `AttributeValue` wire format.
 * @returns {object} - plain JS object, or `null` if `item` is falsy.
 */
function unmarshalItem(item) {
  if (!item || typeof item !== 'object') {
    return null;
  }
  const out = {};
  for (const [key, value] of Object.entries(item)) {
    if (!value || typeof value !== 'object') continue;
    if ('S' in value) out[key] = value.S;
    else if ('BOOL' in value) out[key] = value.BOOL;
    else if ('N' in value) out[key] = Number(value.N);
    else if ('NULL' in value) out[key] = null;
  }
  return out;
}

/**
 * Reads the legacy MD5 password hash from a user item, trying both
 * known attribute names:
 *   - `password` — production schema (matches `UserRepository.MapFromUser`
 *     line 1135 which writes `item["password"] = user.Password`).
 *   - `legacy_password_hash` — monolith import schema / test fixture
 *     schema (see `UserMigrationIntegrationTests.StoreLegacyUserInDynamoDbAsync`).
 *
 * Returns `''` (empty string) if neither attribute contains a non-empty
 * value. An empty-string `password` attribute is common for users who
 * have already migrated — we treat it the same as "no legacy hash
 * available" and reject migration.
 *
 * @param {object} userItem - unmarshalled user record.
 * @returns {string} - the non-empty MD5 hash, or '' if not available.
 */
function extractLegacyPasswordHash(userItem) {
  if (!userItem || typeof userItem !== 'object') {
    return '';
  }
  const primary = userItem.password;
  if (typeof primary === 'string' && primary.trim().length > 0) {
    return primary;
  }
  const secondary = userItem.legacy_password_hash;
  if (typeof secondary === 'string' && secondary.trim().length > 0) {
    return secondary;
  }
  return '';
}

// ---------------------------------------------------------------------------
// Cognito response builder
// ---------------------------------------------------------------------------

/**
 * Populates `event.response` with the Cognito user attribute map the
 * runtime expects when the migration is successful.
 *
 * Per the Cognito docs:
 *   - `userAttributes` MUST contain at minimum `email` and
 *     `email_verified`. Additional attributes may include
 *     `given_name`, `family_name`, `preferred_username`, and any
 *     custom attributes declared on the user pool.
 *   - `finalUserStatus = 'CONFIRMED'` skips the forced password-change
 *     challenge, mimicking the behaviour of
 *     `CognitoService.MigrateUserPasswordAsync` which calls
 *     `AdminSetUserPassword` with `Permanent = true`.
 *   - `messageAction = 'SUPPRESS'` suppresses the welcome-email that
 *     Cognito would otherwise send, mimicking the monolith's
 *     authentication flow which issues no such email.
 *   - `desiredDeliveryMediums` is not set — we never want Cognito to
 *     send SMS / email during a transparent migration.
 *
 * Only `UserMigration_Authentication` reaches this function; for
 * `UserMigration_ForgotPassword` the same attributes are set but
 * Cognito itself will subsequently trigger the reset-password flow
 * and does not require the `finalUserStatus` / `messageAction` fields.
 *
 * @param {object} event - the Cognito trigger event.
 * @param {object} userItem - unmarshalled DynamoDB user record.
 */
function populateCognitoResponse(event, userItem) {
  const userAttributes = {
    email: userItem.email,
    email_verified: 'true',
  };
  if (userItem.username) {
    userAttributes['preferred_username'] = userItem.username;
  }
  if (userItem.first_name) {
    userAttributes['given_name'] = userItem.first_name;
  }
  if (userItem.last_name) {
    userAttributes['family_name'] = userItem.last_name;
  }
  if (userItem.id) {
    userAttributes['custom:erp_user_id'] = userItem.id;
  }

  event.response = event.response || {};
  event.response.userAttributes = userAttributes;
  event.response.finalUserStatus = 'CONFIRMED';
  event.response.messageAction = 'SUPPRESS';
  // `forceAliasCreation` is explicitly false — Cognito must not auto-
  // merge legacy users into an existing alias account.
  event.response.forceAliasCreation = false;
}

// ---------------------------------------------------------------------------
// Main handler
// ---------------------------------------------------------------------------

/**
 * Lambda entry point for Cognito `UserMigration_*` triggers.
 *
 * Return semantics:
 *   - On success: returns the modified `event` with the Cognito-mandated
 *     `response.userAttributes` / `finalUserStatus` / `messageAction`
 *     fields populated. Cognito then creates the user in the pool with
 *     the supplied password (which Cognito hashes via SRP).
 *   - On any failure (user not found / wrong password / internal
 *     error): throws `Error('User migration failed')` — Cognito returns
 *     `UserNotFoundException` to the caller. The generic error message
 *     avoids disclosing whether the user exists or whether the password
 *     was wrong (enumeration prevention).
 *
 * Logging policy:
 *   - Info-level: trigger source + non-sensitive flow markers
 *     (masked email, outcome).
 *   - No password, hash, or full email is ever logged. Emails are
 *     truncated to `<localPart[0..2]>***@<domain>` before logging.
 *
 * @param {object} event - Cognito trigger event.
 * @returns {Promise<object>} - the (possibly updated) trigger event.
 */
async function handler(event) {
  // Defensive: Cognito always supplies these fields, but we guard.
  const triggerSource = event && event.triggerSource;
  const userName = event && event.userName;
  const password =
    event && event.request ? event.request.password : undefined;

  // Non-migration trigger sources are a no-op — return event unchanged.
  if (
    triggerSource !== TRIGGER_AUTHENTICATION &&
    triggerSource !== TRIGGER_FORGOT_PASSWORD
  ) {
    console.log(
      JSON.stringify({
        level: 'info',
        message: 'Non-migration trigger source, returning event unchanged',
        triggerSource: triggerSource || 'UNKNOWN',
      }),
    );
    return event;
  }

  const maskedEmail = maskEmail(userName);
  console.log(
    JSON.stringify({
      level: 'info',
      message: 'User migration trigger invoked',
      triggerSource,
      email: maskedEmail,
    }),
  );

  try {
    // Step 1 — Look up legacy user in DynamoDB by email.
    const userItem = await findLegacyUserByEmail(userName);
    if (!userItem) {
      console.log(
        JSON.stringify({
          level: 'warn',
          message: 'Legacy user not found',
          email: maskedEmail,
        }),
      );
      throw new Error('User migration failed');
    }

    // Step 2 — `UserMigration_ForgotPassword` does not include a password
    //   in the request payload. We skip password verification for that
    //   trigger source and rely on the subsequent `ForgotPassword` flow
    //   to re-authenticate the user via a confirmation code.
    if (triggerSource === TRIGGER_AUTHENTICATION) {
      if (typeof password !== 'string' || password.length === 0) {
        console.log(
          JSON.stringify({
            level: 'warn',
            message: 'No password supplied in UserMigration_Authentication',
            email: maskedEmail,
          }),
        );
        throw new Error('User migration failed');
      }

      // Step 3 — Extract legacy MD5 hash and verify in constant time.
      const storedHash = extractLegacyPasswordHash(userItem);
      if (!storedHash) {
        console.log(
          JSON.stringify({
            level: 'warn',
            message: 'User has no legacy MD5 hash (already migrated?)',
            email: maskedEmail,
          }),
        );
        throw new Error('User migration failed');
      }

      const computedHash = computeMd5Hash(password);
      if (!compareHashesConstantTime(computedHash, storedHash)) {
        console.log(
          JSON.stringify({
            level: 'warn',
            message: 'MD5 hash mismatch',
            email: maskedEmail,
          }),
        );
        throw new Error('User migration failed');
      }
    }

    // Step 4 — Populate Cognito response with user attributes and
    //   migration directives. Cognito will create the user and set the
    //   new password itself.
    populateCognitoResponse(event, userItem);

    console.log(
      JSON.stringify({
        level: 'info',
        message: 'User migration successful',
        triggerSource,
        email: maskedEmail,
      }),
    );
    return event;
  } catch (err) {
    // Re-throw the generic error so Cognito returns UserNotFoundException
    // (or UserLambdaValidationException on some runtimes). Never leak
    // details about *why* the migration failed.
    if (err && err.message === 'User migration failed') {
      throw err;
    }
    console.log(
      JSON.stringify({
        level: 'error',
        message: 'Unexpected error during user migration',
        email: maskedEmail,
        errorType: err && err.name ? err.name : 'Error',
      }),
    );
    throw new Error('User migration failed');
  }
}

/**
 * Truncates an email to its first three local-part characters and the
 * unchanged domain, e.g. `jdoe@example.com` → `jdo***@example.com`.
 * Used for log lines; never pass this value back to Cognito.
 * @param {unknown} email
 * @returns {string}
 */
function maskEmail(email) {
  if (typeof email !== 'string' || email.length === 0) return '<none>';
  const atIndex = email.indexOf('@');
  if (atIndex <= 0) return '<invalid>';
  const local = email.slice(0, Math.min(3, atIndex));
  const domain = email.slice(atIndex);
  return `${local}***${domain}`;
}

// ---------------------------------------------------------------------------
// Exports — public + test-only
// ---------------------------------------------------------------------------

module.exports = {
  // Public Lambda entry point (matches CDK `handler: 'index.handler'`).
  handler,

  // Exposed for unit tests (prefixed `__` to signal internal use).
  __internals: {
    computeMd5Hash,
    compareHashesConstantTime,
    findLegacyUserByEmail,
    extractLegacyPasswordHash,
    populateCognitoResponse,
    unmarshalItem,
    maskEmail,
    /**
     * Overrides the cached DynamoDB client — intended strictly for unit
     * tests that inject a stub implementing `.send()`.
     * @param {object|null} client
     */
    __setDynamoDbClient(client) {
      dynamoDbClient = client;
    },
    /**
     * Restores the default (lazy) DynamoDB client for post-test cleanup.
     */
    __resetDynamoDbClient() {
      dynamoDbClient = null;
    },
  },
};
