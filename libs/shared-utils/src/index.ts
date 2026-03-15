/**
 * @module @webvella-erp/shared-utils
 *
 * Public API barrel export for the shared-utils library.
 *
 * This file is the single entry point for ALL consumers of the
 * `@webvella-erp/shared-utils` package (path alias defined in
 * `tsconfig.base.json`).  It re-exports every public symbol from
 * the three sibling utility modules:
 *
 * 1. **correlation-id** — Distributed request tracing utilities that
 *    replace the monolith's `ErpRequestContext`, `SecurityContext`, and
 *    `ErpMiddleware` per-request context patterns.
 *    Exports: `CORRELATION_ID_HEADER`, `CorrelationIdContext`,
 *    `generateCorrelationId`, `extractCorrelationId`,
 *    `createCorrelationHeaders`, `createSnsMessageAttributes`,
 *    `createSqsMessageAttributes`, `extractCorrelationIdFromSnsMessage`,
 *    `extractCorrelationIdFromSqsRecord`
 *
 * 2. **logger** — Structured JSON logging for CloudWatch Logs that
 *    replaces the monolith's `WebVella.Erp/Diagnostics/Log.cs`
 *    PostgreSQL `system_log` persistence.
 *    Exports: `LogLevel`, `LogContext`, `LogEntry`, `Logger`,
 *    `createLogger`
 *
 * 3. **idempotency** — DynamoDB-based duplicate detection that replaces
 *    the monolith's implicit single-database transactional guarantees
 *    with explicit at-least-once delivery protection.
 *    Exports: `IdempotencyConfig`, `IdempotencyRecord`,
 *    `IdempotencyCheckResult`, `generateIdempotencyKey`,
 *    `generateEventIdempotencyKey`, `IdempotencyChecker`
 *
 * Consumers:
 *   - All 10 bounded-context Lambda services (.NET 9 / Node.js 22)
 *   - Custom Lambda JWT Authorizer (`services/authorizer/`)
 *   - React SPA frontend (`apps/frontend/`)
 *
 * @see AAP §0.4.1 — `libs/shared-utils/src/index.ts`
 * @see AAP §0.8.5 — Structured JSON logging with correlation-ID propagation
 * @see AAP §0.8.5 — Idempotency keys on all write endpoints and event handlers
 * @packageDocumentation
 */

// ---------------------------------------------------------------------------
// Correlation-ID utilities — distributed request tracing
// ---------------------------------------------------------------------------
export * from './correlation-id';

// ---------------------------------------------------------------------------
// Structured JSON logger — CloudWatch Logs output
// ---------------------------------------------------------------------------
export * from './logger';

// ---------------------------------------------------------------------------
// Idempotency utilities — DynamoDB-based duplicate detection
// ---------------------------------------------------------------------------
export * from './idempotency';
