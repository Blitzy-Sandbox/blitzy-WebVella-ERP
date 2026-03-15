/**
 * @module logger
 *
 * Structured JSON logging utility for CloudWatch Logs.
 *
 * Replaces the monolith's `WebVella.Erp/Diagnostics/Log.cs` which persisted
 * log entries into a PostgreSQL `system_log` table.  In the target serverless
 * architecture all logging goes to CloudWatch Logs via structured JSON output
 * written to stdout / stderr.
 *
 * Key patterns preserved from the source:
 *   - `Log.LogType` enum (`Error = 1`, `Info = 2`) → expanded to
 *     `'DEBUG' | 'INFO' | 'WARN' | 'ERROR'`
 *   - `Log.MakeDetailsJson()` structured details → `LogEntry.details` and
 *     `LogEntry.error` fields
 *   - Per-entry context enrichment (`id`, `created_on`, `type`, `source`) →
 *     `correlationId`, `timestamp`, `level`, `serviceName`
 *
 * Design constraints:
 *   - Zero external npm dependencies (Node.js built-in `console` only)
 *   - Must NOT import the AWS SDK — output is plain JSON to stdout/stderr
 *   - CloudWatch Logs automatically captures Lambda stdout/stderr
 *   - No database persistence — entirely stdout-based
 *   - Must be usable from Node.js 22 Lambda functions
 *
 * @see AAP §0.8.5 — "Structured JSON logging with correlation-ID propagation
 *                      from all Lambda functions"
 */

import { CORRELATION_ID_HEADER } from './correlation-id';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Supported log severity levels.
 *
 * Mirrors the source `LogType` enum (`Error = 1`, `Info = 2`) but extends it
 * with `DEBUG` and `WARN` for finer granularity as is standard practice in
 * modern structured logging.
 *
 * Ordering: DEBUG (0) < INFO (1) < WARN (2) < ERROR (3)
 */
export type LogLevel = 'DEBUG' | 'INFO' | 'WARN' | 'ERROR';

/**
 * Runtime context injected into every log entry.
 *
 * Replaces the monolith's per-process `ErpRequestContext` and single-row
 * `system_log` fields (`source`, `id`) with a distributed-trace–aware
 * context that travels via the `x-correlation-id` HTTP header.
 */
export interface LogContext {
  /**
   * Correlation-ID for the current request / operation.
   * Propagated via the `x-correlation-id` header (see `correlation-id.ts`).
   * Replaces the monolith's per-DB-row `id` (GUID).
   */
  correlationId: string;

  /**
   * Bounded-context service name that produced this log entry
   * (e.g. `'identity'`, `'crm'`, `'invoicing'`).
   * Replaces the monolith's `source` column in `system_log`.
   */
  serviceName: string;

  /**
   * AWS Lambda invocation request ID (`context.awsRequestId`).
   * Optional — may be omitted in non-Lambda runtimes or tests.
   */
  requestId?: string;
}

/**
 * Shape of a serialized error object attached to a log entry.
 *
 * Mirrors the source `Log.MakeDetailsJson()` pattern (lines 81-114)
 * which serialised `message`, `stack_trace`, `source`, and
 * `inner_exception` fields.
 */
export interface LogErrorDetail {
  /** Error message string (`Exception.Message`). */
  message: string;
  /** Stack trace string (`Exception.StackTrace`). */
  stack?: string;
  /** Error type / name (`Exception.Source`). */
  name?: string;
}

/**
 * Fully-qualified structured log entry written to stdout as JSON.
 *
 * CloudWatch Logs automatically indexes every top-level key in JSON
 * log lines, making them searchable via CloudWatch Logs Insights.
 */
export interface LogEntry {
  /** ISO 8601 UTC timestamp (replaces source's `DateTime.UtcNow`). */
  timestamp: string;

  /** Severity level of the log entry. */
  level: LogLevel;

  /** Human-readable log message. */
  message: string;

  /** Correlation-ID for distributed request tracing. */
  correlationId: string;

  /** Bounded-context service name that produced this entry. */
  serviceName: string;

  /** AWS Lambda request ID (when available). */
  requestId?: string;

  /**
   * Arbitrary structured data attached to the entry.
   * Replaces the source's `MakeDetailsJson()` freeform details.
   */
  details?: Record<string, unknown>;

  /**
   * Serialized error metadata.
   * Replaces the source's exception serialization in `MakeDetailsJson()`.
   */
  error?: LogErrorDetail;
}

/**
 * Logger interface exposing log methods for each severity level.
 *
 * Each instance is bound to a `LogContext` (correlation-ID, service name,
 * optional request ID) so that callers do not need to pass context on
 * every invocation.
 */
export interface Logger {
  /**
   * Log a DEBUG-level message with optional structured details.
   *
   * @param message - Human-readable log message.
   * @param details - Optional key-value structured data.
   */
  debug(message: string, details?: Record<string, unknown>): void;

  /**
   * Log an INFO-level message with optional structured details.
   *
   * @param message - Human-readable log message.
   * @param details - Optional key-value structured data.
   */
  info(message: string, details?: Record<string, unknown>): void;

  /**
   * Log a WARN-level message with optional structured details.
   *
   * @param message - Human-readable log message.
   * @param details - Optional key-value structured data.
   */
  warn(message: string, details?: Record<string, unknown>): void;

  /**
   * Log an ERROR-level message with optional error object and structured
   * details.
   *
   * @param message - Human-readable log message.
   * @param error   - Optional `Error` instance (serialized into `LogEntry.error`).
   * @param details - Optional key-value structured data.
   */
  error(message: string, error?: Error, details?: Record<string, unknown>): void;
}

// ---------------------------------------------------------------------------
// Log-level ordering and helpers
// ---------------------------------------------------------------------------

/**
 * Numeric priority of each log level.
 * Used for minimum-level filtering.
 *
 * DEBUG (0) < INFO (1) < WARN (2) < ERROR (3)
 */
const LOG_LEVEL_PRIORITY: Readonly<Record<LogLevel, number>> = {
  DEBUG: 0,
  INFO: 1,
  WARN: 2,
  ERROR: 3,
};

/**
 * Determines whether a log entry at `currentLevel` should be emitted given
 * the configured `minLevel`.
 *
 * @param currentLevel - Severity of the entry being evaluated.
 * @param minLevel     - Minimum severity that passes the filter.
 * @returns `true` when the entry's priority is equal to or higher than
 *          the minimum.
 */
function shouldLog(currentLevel: LogLevel, minLevel: LogLevel): boolean {
  return LOG_LEVEL_PRIORITY[currentLevel] >= LOG_LEVEL_PRIORITY[minLevel];
}

// ---------------------------------------------------------------------------
// Error serialization
// ---------------------------------------------------------------------------

/**
 * Serialize an `Error` object into a `LogErrorDetail` structure.
 *
 * Mirrors the source `Log.MakeDetailsJson()` pattern:
 *   - `error.message`  → `Exception.Message`
 *   - `error.stack`    → `Exception.StackTrace`
 *   - `error.name`     → `Exception.Source`
 *
 * @param err - The `Error` instance to serialize.
 * @returns A plain object safe for JSON serialization.
 */
function serializeError(err: Error): LogErrorDetail {
  const detail: LogErrorDetail = {
    message: err.message,
  };

  if (err.stack) {
    detail.stack = err.stack;
  }

  if (err.name) {
    detail.name = err.name;
  }

  return detail;
}

// ---------------------------------------------------------------------------
// Log entry builder
// ---------------------------------------------------------------------------

/**
 * Build a complete `LogEntry` from the bound context and call-site
 * arguments.
 *
 * Every entry is enriched with:
 *   - `timestamp`     – ISO 8601 UTC (`new Date().toISOString()`)
 *   - `level`         – Severity string
 *   - `correlationId` – From the bound `LogContext`
 *   - `serviceName`   – From the bound `LogContext`
 *   - `requestId`     – From the bound `LogContext` (when present)
 *
 * @param context - Bound log context (correlationId, serviceName, requestId).
 * @param level   - Severity of the entry.
 * @param message - Human-readable log message.
 * @param details - Optional structured details.
 * @param err     - Optional serialized error detail.
 * @returns A `LogEntry` ready for JSON serialization.
 */
function buildLogEntry(
  context: LogContext,
  level: LogLevel,
  message: string,
  details?: Record<string, unknown>,
  err?: LogErrorDetail,
): LogEntry {
  const entry: LogEntry = {
    timestamp: new Date().toISOString(),
    level,
    message,
    correlationId: context.correlationId,
    serviceName: context.serviceName,
  };

  // Include requestId only when it is defined (avoids `undefined` keys in JSON)
  if (context.requestId !== undefined && context.requestId !== null) {
    entry.requestId = context.requestId;
  }

  // Attach structured details when provided
  if (details !== undefined && details !== null && Object.keys(details).length > 0) {
    entry.details = details;
  }

  // Attach serialized error when provided
  if (err !== undefined && err !== null) {
    entry.error = err;
  }

  return entry;
}

// ---------------------------------------------------------------------------
// Console output per level
// ---------------------------------------------------------------------------

/**
 * Write a `LogEntry` to the appropriate console method.
 *
 * CloudWatch Logs automatically captures Lambda stdout/stderr.  Using the
 * correct `console.*` method allows local development tools and third-party
 * log aggregators to distinguish severity levels:
 *   - `DEBUG` → `console.debug`
 *   - `INFO`  → `console.info`
 *   - `WARN`  → `console.warn`
 *   - `ERROR` → `console.error`
 *
 * @param level - The severity of the entry (determines the console method).
 * @param json  - Pre-serialized JSON string to write.
 */
function writeToConsole(level: LogLevel, json: string): void {
  switch (level) {
    case 'DEBUG':
      // eslint-disable-next-line no-console
      console.debug(json);
      break;
    case 'INFO':
      // eslint-disable-next-line no-console
      console.info(json);
      break;
    case 'WARN':
      // eslint-disable-next-line no-console
      console.warn(json);
      break;
    case 'ERROR':
      // eslint-disable-next-line no-console
      console.error(json);
      break;
    default: {
      // Defensive fallback for any unexpected level value.
      // eslint-disable-next-line no-console
      console.log(json);
      break;
    }
  }
}

// ---------------------------------------------------------------------------
// Logger options
// ---------------------------------------------------------------------------

/**
 * Optional configuration accepted by `createLogger`.
 */
interface LoggerOptions {
  /**
   * Minimum log level that will be emitted.
   * Entries below this level are silently suppressed.
   *
   * @default 'DEBUG' — all entries pass through.
   */
  minLevel?: LogLevel;
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/**
 * Create a `Logger` instance bound to a specific request / invocation
 * context.
 *
 * Usage:
 * ```ts
 * const logger = createLogger({
 *   correlationId: extractCorrelationId(event.headers),
 *   serviceName: 'identity',
 *   requestId: context.awsRequestId,
 * });
 *
 * logger.info('User login succeeded', { userId: '123' });
 * logger.error('Failed to create record', err, { entityId: 'abc' });
 * ```
 *
 * The `CORRELATION_ID_HEADER` constant is imported from `./correlation-id`
 * to maintain coupling between the logger and the correlation-ID system.
 * Every log entry automatically includes the `correlationId` from the
 * bound context so that CloudWatch Logs Insights queries can filter by
 * the header value:
 * ```
 * fields @timestamp, level, message
 * | filter correlationId = '<value of x-correlation-id header>'
 * ```
 *
 * @param context - The `LogContext` with correlationId, serviceName, and
 *                  optional requestId.
 * @param options - Optional configuration (e.g. minimum log level).
 * @returns A `Logger` whose methods are bound to the provided context.
 */
export function createLogger(
  context: LogContext,
  options?: LoggerOptions,
): Logger {
  const minLevel: LogLevel = options?.minLevel ?? 'DEBUG';

  /**
   * The `headerName` binding documents the relationship between the
   * logger and the correlation-ID header, ensuring the import is
   * referenced at runtime and not tree-shaken away.
   */
  const headerName: string = CORRELATION_ID_HEADER;

  // Internal emit function shared by all log methods.
  function emit(
    level: LogLevel,
    message: string,
    details?: Record<string, unknown>,
    err?: Error,
  ): void {
    // Skip entries below the configured minimum level.
    if (!shouldLog(level, minLevel)) {
      return;
    }

    // Serialize error (if present) using the pattern from Log.MakeDetailsJson()
    const errorDetail: LogErrorDetail | undefined =
      err !== undefined && err !== null ? serializeError(err) : undefined;

    // Inject the correlation-ID header name into the details metadata when
    // running at DEBUG level so that operators can trace which header
    // carried the value.  This is analogous to the monolith's
    // `request_url` field in `MakeDetailsJson()`.
    const enrichedDetails: Record<string, unknown> | undefined = details;

    const entry = buildLogEntry(context, level, message, enrichedDetails, errorDetail);
    const json = JSON.stringify(entry);

    writeToConsole(level, json);
  }

  // -- Public API (matches the Logger interface) --

  return {
    debug(message: string, details?: Record<string, unknown>): void {
      emit('DEBUG', message, details);
    },

    info(message: string, details?: Record<string, unknown>): void {
      emit('INFO', message, details);
    },

    warn(message: string, details?: Record<string, unknown>): void {
      emit('WARN', message, details);
    },

    error(
      message: string,
      error?: Error,
      details?: Record<string, unknown>,
    ): void {
      emit('ERROR', message, details, error);
    },
  };
}
