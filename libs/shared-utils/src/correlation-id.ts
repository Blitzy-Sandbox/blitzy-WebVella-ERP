/**
 * @module correlation-id
 *
 * Correlation-ID generation, extraction, and propagation utilities for
 * distributed request tracing across the WebVella ERP serverless
 * microservices architecture.
 *
 * Replaces the monolith's single-process request-context patterns:
 *   - ErpRequestContext.cs  – per-request App/Area/Node/Entity context
 *   - SecurityContext.cs    – AsyncLocal<SecurityContext> user propagation
 *   - ErpMiddleware.cs      – per-request DbContext + SecurityContext lifecycle
 *
 * In the target architecture every Lambda invocation is independent.
 * The correlation-ID flows through:
 *   API Gateway → Lambda handler → SNS/SQS messages → downstream consumers
 *
 * Design constraints:
 *   - Zero external npm dependencies (Node.js built-in `crypto` only)
 *   - Must work in Node.js 22 Lambda AND modern browsers (uses `crypto.randomUUID()`)
 *   - Header name is lowercase `x-correlation-id` (HTTP/2 / API Gateway v2 convention)
 */

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Canonical HTTP header name for correlation-ID propagation.
 * API Gateway v2 lowercases all header keys, so the constant is
 * already in the canonical form.
 */
export const CORRELATION_ID_HEADER = 'x-correlation-id' as const;

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Captures the distributed trace context for a single request / operation.
 *
 * `correlationId` links all operations triggered by one user request across
 * multiple bounded-context services (replaces monolith's ErpRequestContext).
 *
 * `requestId` is the optional AWS Lambda invocation ID
 * (`context.awsRequestId`) — useful for debugging a specific execution
 * within a distributed trace.
 */
export interface CorrelationIdContext {
  /** The correlation ID for the current request / operation. */
  correlationId: string;

  /** Optional AWS Lambda request ID (from `context.awsRequestId`). */
  requestId?: string;
}

// ---------------------------------------------------------------------------
// Correlation-ID Generation
// ---------------------------------------------------------------------------

/**
 * Generate a new UUID v4 correlation-ID.
 *
 * Uses `crypto.randomUUID()` which is available in:
 *   - Node.js ≥ 19 (global `crypto`)
 *   - All modern browsers (Web Crypto API)
 *
 * This replaces the monolith's `Guid.NewGuid()` pattern used throughout
 * SecurityManager, RecordManager, and Log.cs.
 *
 * @returns A lowercase UUID v4 string, e.g. `'a1b2c3d4-e5f6-4890-abcd-ef1234567890'`
 */
export function generateCorrelationId(): string {
  // `crypto.randomUUID()` is available on the global `crypto` object in
  // both Node.js 19+ and modern browsers. We access it via globalThis so
  // that the same code path works in both runtimes.
  return globalThis.crypto.randomUUID();
}

// ---------------------------------------------------------------------------
// Correlation-ID Extraction (API Gateway v2 events)
// ---------------------------------------------------------------------------

/**
 * Extract the correlation-ID from incoming HTTP headers (typically from an
 * API Gateway v2 event).
 *
 * If the `x-correlation-id` header is present and contains a non-empty value
 * the function returns that value.  Otherwise it generates a fresh
 * correlation-ID so that **every** request is guaranteed to carry one.
 *
 * API Gateway v2 lowercases all header keys, so a simple property access on
 * the canonical key is sufficient.  As an extra safety-net we also perform a
 * case-insensitive search across all keys to handle edge-cases such as
 * direct invocations or test harnesses that pass mixed-case headers.
 *
 * @param headers - The `headers` object from an API Gateway v2 event
 *                  (`Record<string, string | undefined>`).
 * @returns The existing or newly generated correlation-ID.
 */
export function extractCorrelationId(
  headers: Record<string, string | undefined>,
): string {
  if (!headers || typeof headers !== 'object') {
    return generateCorrelationId();
  }

  // Fast-path: direct lookup on the canonical (lowercase) key.
  const directValue = headers[CORRELATION_ID_HEADER];
  if (directValue && directValue.trim().length > 0) {
    return directValue.trim();
  }

  // Slow-path: case-insensitive scan in case the caller supplies
  // mixed-case headers (e.g. unit tests, direct Lambda invocations).
  const lowerKey = CORRELATION_ID_HEADER; // already lowercase
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() === lowerKey) {
      const value = headers[key];
      if (value && value.trim().length > 0) {
        return value.trim();
      }
    }
  }

  // No correlation-ID found — generate a fresh one.
  return generateCorrelationId();
}

// ---------------------------------------------------------------------------
// Propagation Helpers — HTTP
// ---------------------------------------------------------------------------

/**
 * Build a headers object containing the correlation-ID for outgoing HTTP
 * requests to other bounded-context services.
 *
 * @param correlationId - The correlation-ID to propagate.
 * @returns A `Record<string, string>` with the `x-correlation-id` header.
 */
export function createCorrelationHeaders(
  correlationId: string,
): Record<string, string> {
  return {
    [CORRELATION_ID_HEADER]: correlationId,
  };
}

// ---------------------------------------------------------------------------
// Propagation Helpers — SNS
// ---------------------------------------------------------------------------

/**
 * AWS SNS MessageAttributes shape used by the SNS Publish API.
 *
 * @see https://docs.aws.amazon.com/sns/latest/api/API_MessageAttributeValue.html
 */
interface SnsMessageAttributeValue {
  DataType: string;
  StringValue: string;
}

/**
 * Create SNS `MessageAttributes` containing the correlation-ID.
 *
 * Used when publishing domain events to SNS topics.
 * (AAP §0.5.2: HookManager → SNS topic publish for post-CRUD events.)
 *
 * @param correlationId - The correlation-ID to embed in the message.
 * @returns An object compatible with the AWS SNS `PublishCommand` `MessageAttributes` parameter.
 */
export function createSnsMessageAttributes(
  correlationId: string,
): Record<string, SnsMessageAttributeValue> {
  return {
    [CORRELATION_ID_HEADER]: {
      DataType: 'String',
      StringValue: correlationId,
    },
  };
}

// ---------------------------------------------------------------------------
// Propagation Helpers — SQS
// ---------------------------------------------------------------------------

/**
 * AWS SQS MessageAttributes shape used by the SQS SendMessage API.
 *
 * @see https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_MessageAttributeValue.html
 */
interface SqsMessageAttributeValue {
  DataType: string;
  StringValue: string;
}

/**
 * Create SQS `MessageAttributes` containing the correlation-ID.
 *
 * Used when sending messages directly to SQS queues.
 * (AAP §0.8.1: All cross-service communication must go through SNS/SQS.)
 *
 * @param correlationId - The correlation-ID to embed in the message.
 * @returns An object compatible with the AWS SQS `SendMessageCommand` `MessageAttributes` parameter.
 */
export function createSqsMessageAttributes(
  correlationId: string,
): Record<string, SqsMessageAttributeValue> {
  return {
    [CORRELATION_ID_HEADER]: {
      DataType: 'String',
      StringValue: correlationId,
    },
  };
}

// ---------------------------------------------------------------------------
// Extraction Helpers — SNS Message (consumed via SQS subscription)
// ---------------------------------------------------------------------------

/**
 * Shape of a single SNS message attribute value as delivered inside an
 * SQS record body (SNS → SQS fan-out).  The SNS notification JSON uses
 * `Value` (capital V) for the string payload, while raw SNS SDK responses
 * may use `StringValue`.  We check both for maximum compatibility.
 */
interface SnsDeliveredAttributeValue {
  Value?: string;
  StringValue?: string;
}

/**
 * Extract the correlation-ID from an SNS message's `MessageAttributes`.
 *
 * When an SNS notification is delivered to an SQS queue the message body
 * is a JSON object containing a `MessageAttributes` map.  Each attribute
 * value has a `Value` (or `StringValue`) field with the actual string.
 *
 * Falls back to generating a new correlation-ID if the attribute is missing
 * or empty — this ensures downstream processing always has a trace ID.
 *
 * @param messageAttributes - The `MessageAttributes` map from the parsed
 *                            SNS notification JSON.
 * @returns The existing or newly generated correlation-ID.
 */
export function extractCorrelationIdFromSnsMessage(
  messageAttributes: Record<string, SnsDeliveredAttributeValue>,
): string {
  if (!messageAttributes || typeof messageAttributes !== 'object') {
    return generateCorrelationId();
  }

  const attr = messageAttributes[CORRELATION_ID_HEADER];
  if (attr) {
    // Prefer `Value` (SNS notification JSON format) then fall back to
    // `StringValue` (raw SDK response format).
    const value = attr.Value ?? attr.StringValue;
    if (value && value.trim().length > 0) {
      return value.trim();
    }
  }

  // Case-insensitive fallback scan.
  const lowerKey = CORRELATION_ID_HEADER;
  for (const key of Object.keys(messageAttributes)) {
    if (key.toLowerCase() === lowerKey) {
      const a = messageAttributes[key];
      const v = a?.Value ?? a?.StringValue;
      if (v && v.trim().length > 0) {
        return v.trim();
      }
    }
  }

  return generateCorrelationId();
}

// ---------------------------------------------------------------------------
// Extraction Helpers — SQS Record
// ---------------------------------------------------------------------------

/**
 * Shape of a single SQS message attribute value as delivered in the Lambda
 * event record.  SQS uses camelCase `stringValue` in the Lambda event
 * payload.
 */
interface SqsRecordAttributeValue {
  stringValue?: string;
}

/**
 * Extract the correlation-ID from an SQS record's `messageAttributes`.
 *
 * SQS-triggered Lambda functions receive records with `messageAttributes`
 * where each attribute has a lowercase `stringValue` field.
 *
 * Falls back to generating a new correlation-ID if the attribute is missing
 * or empty.
 *
 * @param messageAttributes - The `messageAttributes` map from an SQS event record.
 * @returns The existing or newly generated correlation-ID.
 */
export function extractCorrelationIdFromSqsRecord(
  messageAttributes: Record<string, SqsRecordAttributeValue>,
): string {
  if (!messageAttributes || typeof messageAttributes !== 'object') {
    return generateCorrelationId();
  }

  const attr = messageAttributes[CORRELATION_ID_HEADER];
  if (attr) {
    const value = attr.stringValue;
    if (value && value.trim().length > 0) {
      return value.trim();
    }
  }

  // Case-insensitive fallback scan.
  const lowerKey = CORRELATION_ID_HEADER;
  for (const key of Object.keys(messageAttributes)) {
    if (key.toLowerCase() === lowerKey) {
      const v = messageAttributes[key]?.stringValue;
      if (v && v.trim().length > 0) {
        return v.trim();
      }
    }
  }

  return generateCorrelationId();
}
