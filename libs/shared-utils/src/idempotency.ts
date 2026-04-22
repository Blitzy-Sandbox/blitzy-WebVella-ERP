/**
 * @module idempotency
 *
 * Idempotency key generation and DynamoDB-based duplicate detection for the
 * WebVella ERP serverless microservices architecture.
 *
 * **Why this exists:**
 * The original monolith (`RecordManager.cs`) executed CRUD operations directly
 * against a single PostgreSQL database with implicit transactional guarantees
 * and no idempotency protection.  In the target architecture every write
 * endpoint and event consumer is exposed to at-least-once delivery (SQS) and
 * HTTP retries (API Gateway).  This module provides:
 *
 * 1. Deterministic key generation from arbitrary string parts.
 * 2. A convenience helper for SQS/SNS event-level idempotency keys.
 * 3. A DynamoDB-backed checker that claims a key via conditional `PutItem`,
 *    detects duplicates through `ConditionalCheckFailedException`, and
 *    transitions records from PROCESSING → COMPLETED on success.
 *
 * Per AAP §0.8.5:
 * - "Idempotency keys on all write endpoints and event handlers"
 * - "All event consumers MUST be idempotent"
 * - "At-least-once delivery guarantee via SQS"
 *
 * Per AAP §0.8.6:
 * - `AWS_ENDPOINT_URL` = `http://localhost:4566` for LocalStack
 * - `AWS_REGION` defaults to `us-east-1`
 *
 * @packageDocumentation
 */

import { createHash } from 'node:crypto';

import {
  DynamoDBClient,
  ConditionalCheckFailedException,
} from '@aws-sdk/client-dynamodb';

import {
  DynamoDBDocumentClient,
  PutCommand,
  GetCommand,
  UpdateCommand,
} from '@aws-sdk/lib-dynamodb';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default TTL for idempotency records: 24 hours in seconds. */
const DEFAULT_TTL_SECONDS = 86_400;

/** Default AWS region when neither config nor env var is set. */
const DEFAULT_REGION = 'us-east-1';

/** Delimiter used to join key parts before hashing. */
const KEY_DELIMITER = '::';

// ---------------------------------------------------------------------------
// Type Definitions
// ---------------------------------------------------------------------------

/**
 * Configuration for the {@link IdempotencyChecker}.
 *
 * All optional fields fall back to well-known environment variables so that
 * the same code works transparently against LocalStack and production AWS.
 */
export interface IdempotencyConfig {
  /** DynamoDB table name storing idempotency records. */
  tableName: string;

  /**
   * Time-to-live for idempotency records in seconds.
   *
   * DynamoDB TTL automatically purges expired items asynchronously, so the
   * actual deletion may happen up to 48 hours after the TTL epoch.
   *
   * @defaultValue 86400 (24 hours)
   */
  ttlSeconds?: number;

  /**
   * AWS region override.
   *
   * @defaultValue `process.env.AWS_REGION ?? 'us-east-1'`
   */
  region?: string;

  /**
   * Custom AWS endpoint URL.  Set to `http://localhost:4566` for LocalStack.
   *
   * @defaultValue `process.env.AWS_ENDPOINT_URL` (undefined in production)
   */
  endpoint?: string;
}

/**
 * Represents a persisted idempotency record in DynamoDB.
 *
 * The table schema is:
 * - `idempotencyKey` (String) — Partition key
 * - `createdAt`      (String) — ISO 8601 timestamp
 * - `ttl`            (Number) — DynamoDB TTL attribute (epoch seconds)
 * - `status`         (String) — Processing lifecycle state
 * - `response`       (String) — Optional cached response payload
 */
export interface IdempotencyRecord {
  /** The unique idempotency key (partition key). */
  idempotencyKey: string;

  /** ISO 8601 UTC timestamp of when the record was created. */
  createdAt: string;

  /** DynamoDB TTL — epoch seconds after which the record may be purged. */
  ttl: number;

  /**
   * Cached JSON-serialised response from the original execution.
   *
   * Present only after `complete()` has been called with a response payload.
   */
  response?: string;

  /**
   * Processing lifecycle state.
   *
   * - `PROCESSING` — The key has been claimed; the handler is executing.
   * - `COMPLETED`  — Execution finished successfully; `response` is set.
   * - `EXPIRED`    — Logically expired (DynamoDB TTL handles physical deletion).
   */
  status: 'PROCESSING' | 'COMPLETED' | 'EXPIRED';
}

/**
 * Discriminated result of an idempotency check.
 *
 * When `isNew` is `true` the caller should proceed with the write operation
 * and invoke `complete()` afterwards.
 *
 * When `isNew` is `false` the operation has already been seen.  If the
 * original execution completed, `cachedResponse` contains the serialised
 * response; otherwise the original execution is still in-flight.
 */
export type IdempotencyCheckResult =
  | { isNew: true }
  | { isNew: false; cachedResponse?: string };

// ---------------------------------------------------------------------------
// Idempotency Key Generation
// ---------------------------------------------------------------------------

/**
 * Generate a deterministic idempotency key from an arbitrary number of
 * string parts.
 *
 * The parts are joined with `::` and hashed via SHA-256 to produce a
 * fixed-length, URL-safe key suitable as a DynamoDB partition key.  Using
 * a cryptographic hash ensures uniform key distribution across DynamoDB
 * partitions regardless of input patterns.
 *
 * Inspired by the monolith's `CryptoUtility.cs` hashing patterns (MD5 /
 * AES), upgraded to SHA-256 for collision resistance.
 *
 * @example
 * ```ts
 * const key = generateIdempotencyKey('crm', 'create', recordId, userId);
 * ```
 *
 * @param parts - Variable number of string components that together
 *                uniquely identify the operation.
 * @returns A lowercase hexadecimal SHA-256 digest string.
 */
export function generateIdempotencyKey(...parts: string[]): string {
  if (parts.length === 0) {
    throw new Error(
      'generateIdempotencyKey requires at least one string part to produce a deterministic key.',
    );
  }

  const payload = parts.join(KEY_DELIMITER);
  return createHash('sha256').update(payload, 'utf8').digest('hex');
}

/**
 * Generate an idempotency key specifically for SQS/SNS event consumers.
 *
 * This is a convenience wrapper around {@link generateIdempotencyKey} that
 * combines the event source ARN (or topic name) with the unique message ID
 * assigned by AWS.  Every SQS message and SNS notification carries a
 * globally unique `messageId`, making the combination inherently unique
 * across all deliveries.
 *
 * Per AAP §0.8.5: "All event consumers MUST be idempotent."
 *
 * @example
 * ```ts
 * // In an SQS-triggered Lambda handler:
 * for (const record of event.Records) {
 *   const key = generateEventIdempotencyKey(record.eventSourceARN, record.messageId);
 *   const result = await checker.check(key);
 *   if (!result.isNew) continue;          // skip duplicate
 *   // ... process record ...
 *   await checker.complete(key);
 * }
 * ```
 *
 * @param eventSource - The source identifier (e.g. SQS queue ARN, SNS
 *                      topic ARN, or a descriptive string like
 *                      `'crm.account.created'`).
 * @param messageId   - The unique message ID assigned by SQS or SNS.
 * @returns A deterministic SHA-256 hex key.
 */
export function generateEventIdempotencyKey(
  eventSource: string,
  messageId: string,
): string {
  if (!eventSource || !messageId) {
    throw new Error(
      'generateEventIdempotencyKey requires both eventSource and messageId.',
    );
  }
  return generateIdempotencyKey(eventSource, messageId);
}

// ---------------------------------------------------------------------------
// IdempotencyChecker — DynamoDB-based duplicate detection
// ---------------------------------------------------------------------------

/**
 * DynamoDB-backed idempotency checker that enforces exactly-once semantics
 * for write operations and event processing.
 *
 * **Lifecycle:**
 * 1. Call {@link IdempotencyChecker.check | check(key)} before executing
 *    the write.  If `isNew` is `true`, proceed.
 * 2. Execute the business logic.
 * 3. Call {@link IdempotencyChecker.complete | complete(key, response?)} to
 *    mark the operation as done and optionally cache the response.
 *
 * **DynamoDB Table Requirements:**
 * - Partition key: `idempotencyKey` (String)
 * - TTL attribute: `ttl` (Number, epoch seconds)
 * - No sort key required.
 *
 * The checker is safe for concurrent invocations: DynamoDB's conditional
 * write guarantees that only one caller wins the race; all others receive
 * `ConditionalCheckFailedException` and are correctly identified as
 * duplicates.
 */
export class IdempotencyChecker {
  private readonly docClient: DynamoDBDocumentClient;
  private readonly tableName: string;
  private readonly ttlSeconds: number;

  /**
   * Create a new IdempotencyChecker.
   *
   * The underlying DynamoDB client is configured to work seamlessly against
   * both LocalStack (via `AWS_ENDPOINT_URL`) and production AWS.
   *
   * @param config - Idempotency configuration.
   */
  constructor(config: IdempotencyConfig) {
    if (!config.tableName) {
      throw new Error('IdempotencyConfig.tableName is required.');
    }

    this.tableName = config.tableName;
    this.ttlSeconds = config.ttlSeconds ?? DEFAULT_TTL_SECONDS;

    const region = config.region ?? process.env.AWS_REGION ?? DEFAULT_REGION;
    const endpoint =
      config.endpoint ?? process.env.AWS_ENDPOINT_URL ?? undefined;

    const clientConfig: ConstructorParameters<typeof DynamoDBClient>[0] = {
      region,
    };

    if (endpoint) {
      clientConfig.endpoint = endpoint;
    }

    const baseClient = new DynamoDBClient(clientConfig);

    this.docClient = DynamoDBDocumentClient.from(baseClient, {
      marshallOptions: {
        removeUndefinedValues: true,
        convertClassInstanceToMap: true,
      },
      unmarshallOptions: {
        wrapNumbers: false,
      },
    });
  }

  /**
   * Attempt to claim an idempotency key.
   *
   * Internally performs a conditional `PutItem` with
   * `attribute_not_exists(idempotencyKey)`.  If the write succeeds the key
   * is new and the caller should proceed with the operation.  If the write
   * fails due to a duplicate key the method reads the existing record and
   * returns the cached response (if the original execution has completed).
   *
   * @param idempotencyKey - The key to check, typically produced by
   *                         {@link generateIdempotencyKey} or
   *                         {@link generateEventIdempotencyKey}.
   * @returns An {@link IdempotencyCheckResult} indicating whether the
   *          operation should proceed (`isNew: true`) or has already been
   *          processed (`isNew: false`).
   * @throws Re-throws unexpected DynamoDB errors (network failures,
   *         throttling, permission issues).
   */
  async check(idempotencyKey: string): Promise<IdempotencyCheckResult> {
    if (!idempotencyKey) {
      throw new Error('idempotencyKey must be a non-empty string.');
    }

    const now = new Date();
    const record: IdempotencyRecord = {
      idempotencyKey,
      createdAt: now.toISOString(),
      ttl: Math.floor(now.getTime() / 1_000) + this.ttlSeconds,
      status: 'PROCESSING',
    };

    try {
      // Attempt to claim the key atomically.
      await this.docClient.send(
        new PutCommand({
          TableName: this.tableName,
          Item: record,
          ConditionExpression: 'attribute_not_exists(idempotencyKey)',
        }),
      );

      // Conditional write succeeded — first time seeing this key.
      return { isNew: true };
    } catch (error: unknown) {
      // If the key already exists DynamoDB throws ConditionalCheckFailedException.
      if (error instanceof ConditionalCheckFailedException) {
        return this.handleDuplicate(idempotencyKey);
      }

      // Unexpected error — propagate to caller for retry / dead-letter.
      throw error;
    }
  }

  /**
   * Mark an idempotency key as successfully completed and optionally cache
   * the response payload.
   *
   * This should be called after the business logic has executed successfully.
   * Subsequent duplicate checks for the same key will return the cached
   * response, enabling callers to replay the original result without
   * re-executing side effects.
   *
   * @param idempotencyKey - The key that was previously claimed via
   *                         {@link check}.
   * @param response       - Optional JSON-serialised response to cache.
   * @throws Throws if the DynamoDB update fails (network errors, throttling,
   *         or missing record).
   */
  async complete(idempotencyKey: string, response?: string): Promise<void> {
    if (!idempotencyKey) {
      throw new Error('idempotencyKey must be a non-empty string.');
    }

    const updateExpression = response !== undefined
      ? 'SET #status = :s, #response = :r'
      : 'SET #status = :s';

    const expressionAttributeNames: Record<string, string> = {
      '#status': 'status',
    };

    const expressionAttributeValues: Record<string, string> = {
      ':s': 'COMPLETED',
    };

    if (response !== undefined) {
      expressionAttributeNames['#response'] = 'response';
      expressionAttributeValues[':r'] = response;
    }

    await this.docClient.send(
      new UpdateCommand({
        TableName: this.tableName,
        Key: { idempotencyKey },
        UpdateExpression: updateExpression,
        ExpressionAttributeNames: expressionAttributeNames,
        ExpressionAttributeValues: expressionAttributeValues,
      }),
    );
  }

  // -------------------------------------------------------------------------
  // Private helpers
  // -------------------------------------------------------------------------

  /**
   * Handle a duplicate idempotency key by reading the existing record and
   * returning the appropriate check result.
   *
   * @param idempotencyKey - The duplicate key.
   * @returns `{ isNew: false, cachedResponse }` when the original execution
   *          has completed, or `{ isNew: false }` when it is still
   *          in-flight.
   */
  private async handleDuplicate(
    idempotencyKey: string,
  ): Promise<IdempotencyCheckResult> {
    try {
      const { Item } = await this.docClient.send(
        new GetCommand({
          TableName: this.tableName,
          Key: { idempotencyKey },
          ConsistentRead: true,
        }),
      );

      if (Item) {
        const existingRecord = Item as IdempotencyRecord;

        if (existingRecord.status === 'COMPLETED' && existingRecord.response) {
          return { isNew: false, cachedResponse: existingRecord.response };
        }

        // Status is PROCESSING (or EXPIRED without a response) — the
        // original handler is still running or failed without completing.
        return { isNew: false };
      }

      // Record vanished between the conditional write failure and the read
      // (possible if TTL deleted it).  Treat as new.
      return { isNew: true };
    } catch {
      // If the GetItem fails we conservatively report a duplicate to avoid
      // double-processing.  The caller can retry on the next delivery.
      return { isNew: false };
    }
  }
}
