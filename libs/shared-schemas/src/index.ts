/**
 * @module @webvella-erp/shared-schemas
 *
 * Barrel export and schema loading utilities for the shared-schemas library.
 * This is the main entry point for the shared-schemas package, consumed via
 * the `@webvella-erp/shared-schemas` path alias defined in `tsconfig.base.json`.
 *
 * Provides:
 *   - Domain event envelope interface (`DomainEvent<T>`)
 *   - API response envelope interfaces (`ApiResponseEnvelope<T>`, `PaginatedResponse<T>`)
 *   - API error model interface (`ApiErrorModel`) matching the monolith's `ErrorModel`
 *   - Event type naming constants following `{domain}.{entity}.{action}` convention
 *   - Service name and event domain constants for all 10 bounded-context services
 *   - Schema loading utilities for JSON Schema event definitions and OpenAPI YAML specs
 *
 * Source mappings (monolith → microservices):
 *   - `IErpPostCreateRecordHook.cs`  → `entity-management.record.created` event
 *   - `IErpPostUpdateRecordHook.cs`  → `entity-management.record.updated` event
 *   - `IErpPostDeleteRecordHook.cs`  → `entity-management.record.deleted` event
 *   - `IErpPostCreateManyToManyRelationHook.cs` → `entity-management.relation.created` event
 *   - `IErpPostDeleteManyToManyRelationHook.cs` → `entity-management.relation.deleted` event
 *   - `RecordHookManager.cs` post-hook execution → SNS domain event publishing
 *   - `BaseModels.cs` ErrorModel → `ApiErrorModel` interface
 *   - `BaseModels.cs` BaseResponseModel + ResponseModel → `ApiResponseEnvelope<T>` interface
 *   - `WebApiController.cs` api/v3/en_US/* routes → `/v1/*` API Gateway routes
 *
 * Design constraints:
 *   - Zero external npm dependencies — only Node.js built-ins (`path`, `fs`)
 *   - Tree-shakeable named exports (no default export)
 *   - All constants frozen with `as const` for literal type inference
 *   - Generic interfaces for type-safe event and response payloads
 *
 * @see AAP §0.7.2 — Hook System to Event-Driven Architecture Migration
 * @see AAP §0.8.5 — Event naming convention: `{domain}.{entity}.{action}`
 * @see AAP §0.8.6 — API versioning with `/v1/` path prefix
 */

import * as path from 'path';
import * as fs from 'fs';

// ---------------------------------------------------------------------------
// Domain Event Envelope Interface
// ---------------------------------------------------------------------------

/**
 * Standard domain event envelope used across all microservices for
 * asynchronous communication via SNS/SQS.
 *
 * Replaces the monolith's synchronous post-hook invocation pattern where
 * `RecordHookManager.ExecutePostCreateRecordHooks()` invoked registered
 * `IErpPostCreateRecordHook` instances in-process. In the target architecture,
 * post-hooks become domain events published to SNS topics and consumed by
 * SQS queues, enabling loose coupling between bounded contexts.
 *
 * Per AAP §0.8.5:
 *   - At-least-once delivery guarantee via SQS
 *   - All event consumers MUST be idempotent
 *   - Idempotency keys on all event handlers
 *
 * @template T - Domain-specific payload type. Defaults to a generic record
 *   for flexibility when the exact payload schema is not yet known.
 *
 * @example
 * ```typescript
 * const event: DomainEvent<{ entityId: string; entityName: string }> = {
 *   eventType: 'entity-management.entity.created',
 *   correlationId: '550e8400-e29b-41d4-a716-446655440000',
 *   timestamp: '2025-01-15T10:30:00.000Z',
 *   source: 'entity-management',
 *   version: '1.0',
 *   data: { entityId: 'abc-123', entityName: 'account' },
 * };
 * ```
 */
export interface DomainEvent<T = Record<string, unknown>> {
  /** Fully-qualified event type following `{domain}.{entity}.{action}` convention. */
  eventType: string;
  /** UUID v4 correlation identifier for distributed request tracing. */
  correlationId: string;
  /** ISO 8601 timestamp of when the event was produced. */
  timestamp: string;
  /** Service name that emitted the event (matches a value from `ServiceNames`). */
  source: string;
  /** Schema version string (e.g., "1.0") for consumer version negotiation. */
  version: string;
  /** Domain-specific event payload. */
  data: T;
}

// ---------------------------------------------------------------------------
// API Error Model Interface
// ---------------------------------------------------------------------------

/**
 * Error detail model matching the monolith's `ErrorModel` class from
 * `WebVella.Erp/Api/Models/BaseModels.cs` (lines 62–83).
 *
 * Original C# definition:
 * ```csharp
 * public class ErrorModel {
 *     [JsonProperty(PropertyName = "key")]   public string Key { get; set; }
 *     [JsonProperty(PropertyName = "value")] public string Value { get; set; }
 *     [JsonProperty(PropertyName = "message")] public string Message { get; set; }
 * }
 * ```
 *
 * Used within `ApiResponseEnvelope.errors` to provide structured error
 * information to API consumers, preserving backward compatibility with
 * existing frontend clients.
 */
export interface ApiErrorModel {
  /** Machine-readable error key identifying the field or subsystem (e.g., "email", "eql"). */
  key: string;
  /** The invalid value that caused the error, or an empty string if not applicable. */
  value: string;
  /** Human-readable error description. */
  message: string;
}

// ---------------------------------------------------------------------------
// API Response Envelope Interface
// ---------------------------------------------------------------------------

/**
 * Standard API response envelope for all microservice endpoints, matching
 * the structure derived from the monolith's `BaseResponseModel` and
 * `ResponseModel` classes (`BaseModels.cs` lines 8–48).
 *
 * Key transformation from monolith:
 *   - `ResponseModel.Object` (C# `object` type) → `ApiResponseEnvelope.data` (generic `T`)
 *   - `BaseResponseModel.Timestamp` (C# `DateTime`) → `timestamp` (ISO 8601 string)
 *   - `BaseResponseModel.Errors` (C# `List<ErrorModel>`) → `errors` (`ApiErrorModel[]`)
 *   - `BaseResponseModel.Hash` and `BaseResponseModel.AccessWarnings` are omitted
 *     (hash replaced by ETag headers; access warnings folded into errors)
 *
 * @template T - Type of the response payload. Defaults to `unknown` for
 *   maximum flexibility when the exact type is not constrained.
 *
 * @example
 * ```typescript
 * const response: ApiResponseEnvelope<{ id: string; name: string }> = {
 *   timestamp: new Date().toISOString(),
 *   success: true,
 *   message: 'Entity created successfully',
 *   errors: [],
 *   data: { id: 'abc-123', name: 'account' },
 * };
 * ```
 */
export interface ApiResponseEnvelope<T = unknown> {
  /** ISO 8601 timestamp of the response generation. */
  timestamp: string;
  /** Indicates whether the request was processed successfully. */
  success: boolean;
  /** Human-readable summary message (empty string on success, error summary on failure). */
  message: string;
  /** Array of structured error details; empty array on success. */
  errors: ApiErrorModel[];
  /** Response payload. Replaces the monolith's `ResponseModel.Object` property. */
  data: T;
}

// ---------------------------------------------------------------------------
// Pagination Metadata Interface
// ---------------------------------------------------------------------------

/**
 * Pagination metadata for list/search endpoints. Derived from the monolith's
 * EQL `PAGE` and `PAGESIZE` clauses (`EqlGrammar.cs`) and record listing
 * page models (`RecordList.cshtml.cs`).
 *
 * @example
 * ```typescript
 * const meta: PaginationMeta = {
 *   page: 1,
 *   pageSize: 25,
 *   totalCount: 142,
 *   totalPages: 6,
 * };
 * ```
 */
export interface PaginationMeta {
  /** Current page number (1-based). */
  page: number;
  /** Number of items per page. */
  pageSize: number;
  /** Total number of items across all pages. */
  totalCount: number;
  /** Total number of pages computed as `Math.ceil(totalCount / pageSize)`. */
  totalPages: number;
}

// ---------------------------------------------------------------------------
// Paginated Response Interface
// ---------------------------------------------------------------------------

/**
 * Paginated response envelope extending `ApiResponseEnvelope` with pagination
 * metadata. Used for all list/search endpoints returning multiple items.
 *
 * The `data` property is constrained to `T[]` (array of items) and
 * the `meta` property provides pagination state for client-side navigation.
 *
 * @template T - Type of individual items in the paginated result set.
 *
 * @example
 * ```typescript
 * const response: PaginatedResponse<{ id: string; name: string }> = {
 *   timestamp: new Date().toISOString(),
 *   success: true,
 *   message: '',
 *   errors: [],
 *   data: [{ id: '1', name: 'Account A' }, { id: '2', name: 'Account B' }],
 *   meta: { page: 1, pageSize: 25, totalCount: 2, totalPages: 1 },
 * };
 * ```
 */
export interface PaginatedResponse<T> extends ApiResponseEnvelope<T[]> {
  /** Pagination metadata describing the current page and total result set. */
  meta: PaginationMeta;
}

// ---------------------------------------------------------------------------
// Event Type Constants
// ---------------------------------------------------------------------------

/**
 * Frozen constant object containing all domain event type strings across
 * all bounded-context services. Event types follow the naming convention
 * `{domain}.{entity}.{action}` per AAP §0.8.5.
 *
 * Organized by domain with nested objects for each entity within the domain.
 * Uses `as const` to enable literal type inference for type-safe event
 * publishing and consumption.
 *
 * Source mapping (monolith hooks → domain events):
 *   - `IErpPostCreateRecordHook.OnPostCreateRecord()` → `ENTITY_MANAGEMENT.RECORD.CREATED`
 *   - `IErpPostUpdateRecordHook.OnPostUpdateRecord()` → `ENTITY_MANAGEMENT.RECORD.UPDATED`
 *   - `IErpPostDeleteRecordHook.OnPostDeleteRecord()` → `ENTITY_MANAGEMENT.RECORD.DELETED`
 *   - `IErpPostCreateManyToManyRelationHook.OnPostCreate()` → `ENTITY_MANAGEMENT.RELATION.CREATED`
 *   - `IErpPostDeleteManyToManyRelationHook.OnPostDelete()` → `ENTITY_MANAGEMENT.RELATION.DELETED`
 *   - Pre-hooks remain synchronous validation within Lambda handlers
 *
 * @see AAP §0.7.2 — Hook System to Event-Driven Architecture Migration
 */
export const EventTypes = {
  /**
   * Entity Management domain events.
   * Covers entity metadata CRUD, record CRUD, and relation lifecycle.
   * Source: EntityManager.cs, RecordManager.cs, EntityRelationManager.cs
   */
  ENTITY_MANAGEMENT: {
    /** Entity metadata lifecycle events. */
    ENTITY: {
      CREATED: 'entity-management.entity.created',
      UPDATED: 'entity-management.entity.updated',
      DELETED: 'entity-management.entity.deleted',
    },
    /** Record data lifecycle events (replaces IErpPost*RecordHook). */
    RECORD: {
      CREATED: 'entity-management.record.created',
      UPDATED: 'entity-management.record.updated',
      DELETED: 'entity-management.record.deleted',
    },
    /** Many-to-many relation lifecycle events (replaces IErpPost*ManyToManyRelationHook). */
    RELATION: {
      CREATED: 'entity-management.relation.created',
      DELETED: 'entity-management.relation.deleted',
    },
  },

  /**
   * Identity & Access Management domain events.
   * Source: SecurityManager.cs user/role CRUD operations
   */
  IDENTITY: {
    USER: {
      CREATED: 'identity.user.created',
      UPDATED: 'identity.user.updated',
      DELETED: 'identity.user.deleted',
    },
    ROLE: {
      CREATED: 'identity.role.created',
      UPDATED: 'identity.role.updated',
      DELETED: 'identity.role.deleted',
    },
  },

  /**
   * CRM / Contacts domain events.
   * Source: NextPlugin entity patches (account, contact entities)
   *         and post-CRUD hooks in Hooks/Api/
   */
  CRM: {
    ACCOUNT: {
      CREATED: 'crm.account.created',
      UPDATED: 'crm.account.updated',
      DELETED: 'crm.account.deleted',
    },
    CONTACT: {
      CREATED: 'crm.contact.created',
      UPDATED: 'crm.contact.updated',
      DELETED: 'crm.contact.deleted',
    },
  },

  /**
   * Invoicing / Billing domain events.
   * Source: RecordManager CRUD operations for invoice/payment entities
   */
  INVOICING: {
    INVOICE: {
      CREATED: 'invoicing.invoice.created',
      UPDATED: 'invoicing.invoice.updated',
      DELETED: 'invoicing.invoice.deleted',
    },
    PAYMENT: {
      CREATED: 'invoicing.payment.created',
      UPDATED: 'invoicing.payment.updated',
    },
  },

  /**
   * Notifications domain events.
   * Source: Mail plugin SMTP engine (Services/, Jobs/)
   */
  NOTIFICATIONS: {
    EMAIL: {
      QUEUED: 'notifications.email.queued',
      SENT: 'notifications.email.sent',
      FAILED: 'notifications.email.failed',
    },
    WEBHOOK: {
      DISPATCHED: 'notifications.webhook.dispatched',
    },
  },

  /**
   * File Management domain events.
   * Source: DbFileRepository.cs file lifecycle operations
   */
  FILE_MANAGEMENT: {
    FILE: {
      UPLOADED: 'file-management.file.uploaded',
      DELETED: 'file-management.file.deleted',
    },
  },

  /**
   * Workflow Engine domain events.
   * Source: JobManager.cs, JobPool.cs, SheduleManager.cs
   */
  WORKFLOW: {
    WORKFLOW: {
      STARTED: 'workflow.workflow.started',
      COMPLETED: 'workflow.workflow.completed',
      FAILED: 'workflow.workflow.failed',
    },
    STEP: {
      COMPLETED: 'workflow.step.completed',
    },
  },

  /**
   * Plugin / Extension System domain events.
   * Source: ErpPlugin.cs plugin registration lifecycle
   */
  PLUGIN_SYSTEM: {
    PLUGIN: {
      REGISTERED: 'plugin-system.plugin.registered',
      DEREGISTERED: 'plugin-system.plugin.deregistered',
    },
  },
} as const;

// ---------------------------------------------------------------------------
// Service Name Constants
// ---------------------------------------------------------------------------

/**
 * Frozen constant object containing all 10 bounded-context service names.
 * These names are used as:
 *   - `DomainEvent.source` field values
 *   - CDK stack identifiers
 *   - API Gateway route prefixes
 *   - SNS topic name components
 *   - SQS queue name components
 *   - DLQ naming: `{service}-{queue}-dlq`
 *
 * Maps 1:1 to the monolith decomposition (AAP §0.4.1):
 *   - `SecurityManager.cs` + `AuthService.cs` → IDENTITY
 *   - `EntityManager.cs` + `RecordManager.cs` → ENTITY_MANAGEMENT
 *   - `CrmPlugin.cs` + `NextPlugin.cs` → CRM
 *   - `ProjectPlugin.cs` → INVENTORY
 *   - Invoice entities (new) → INVOICING
 *   - `DataSourceManager.cs` (reporting) → REPORTING
 *   - `MailPlugin.cs` + Notifications → NOTIFICATIONS
 *   - `DbFileRepository.cs` → FILE_MANAGEMENT
 *   - `JobManager.cs` + `JobPool.cs` → WORKFLOW
 *   - `ErpPlugin.cs` + `SdkPlugin.cs` → PLUGIN_SYSTEM
 */
export const ServiceNames = {
  IDENTITY: 'identity',
  ENTITY_MANAGEMENT: 'entity-management',
  CRM: 'crm',
  INVENTORY: 'inventory',
  INVOICING: 'invoicing',
  REPORTING: 'reporting',
  NOTIFICATIONS: 'notifications',
  FILE_MANAGEMENT: 'file-management',
  WORKFLOW: 'workflow',
  PLUGIN_SYSTEM: 'plugin-system',
} as const;

// ---------------------------------------------------------------------------
// Event Domain Constants
// ---------------------------------------------------------------------------

/**
 * Frozen constant mapping service identifiers to their event domain prefixes.
 * The event domain is the first segment of the `{domain}.{entity}.{action}`
 * event type naming convention. In most cases the event domain matches the
 * service name, but this mapping provides an explicit binding for clarity.
 *
 * Used for:
 *   - SNS topic naming: `{domain}-events`
 *   - Event type prefix validation
 *   - Domain event routing and filtering
 */
export const EventDomains = {
  identity: 'identity',
  'entity-management': 'entity-management',
  crm: 'crm',
  inventory: 'inventory',
  invoicing: 'invoicing',
  reporting: 'reporting',
  notifications: 'notifications',
  'file-management': 'file-management',
  workflow: 'workflow',
  'plugin-system': 'plugin-system',
} as const;

// ---------------------------------------------------------------------------
// API Version Prefix Constant
// ---------------------------------------------------------------------------

/**
 * API path version prefix for HTTP API Gateway v2 routes.
 *
 * Replaces the monolith's `api/v3/en_US/` route prefix from
 * `WebApiController.cs` with a simplified `/v1/` prefix.
 * All service endpoints are mounted under this prefix at the
 * API Gateway level.
 *
 * Per AAP §0.8.6: Path-based versioning at HTTP API Gateway level.
 */
export const ApiVersionPrefix: string = '/v1/';

// ---------------------------------------------------------------------------
// Schema Loading Utilities
// ---------------------------------------------------------------------------

/**
 * Returns the absolute file path for a JSON Schema event definition file.
 *
 * Event schema files are stored in the `events/` directory adjacent to this
 * module's source, following the naming convention `{domain}.events.json`.
 *
 * @param domain - Event domain name (e.g., 'entity', 'record', 'identity').
 *   This corresponds to the first segment of the event type or a
 *   logical grouping of related events.
 * @returns Absolute file path to the event schema JSON file.
 *
 * @example
 * ```typescript
 * const schemaPath = getEventSchemaPath('entity');
 * // → '/abs/path/to/libs/shared-schemas/src/events/entity.events.json'
 * ```
 */
export function getEventSchemaPath(domain: string): string {
  if (!domain || typeof domain !== 'string') {
    throw new Error(
      `Invalid domain parameter: expected a non-empty string, received "${String(domain)}"`
    );
  }

  const sanitized = domain.replace(/[^a-zA-Z0-9_-]/g, '');
  if (sanitized !== domain) {
    throw new Error(
      `Invalid domain parameter: "${domain}" contains disallowed characters. ` +
        'Only alphanumeric characters, hyphens, and underscores are permitted.'
    );
  }

  return path.resolve(path.join(__dirname, 'events', `${domain}.events.json`));
}

/**
 * Returns the absolute file path for an OpenAPI YAML specification file.
 *
 * API spec files are stored in the `api/` directory adjacent to this
 * module's source, following the naming convention `{service}-api.yaml`.
 *
 * @param service - Service name (e.g., 'identity', 'crm', 'entity-management').
 *   Must match one of the values in `ServiceNames`.
 * @returns Absolute file path to the OpenAPI YAML spec file.
 *
 * @example
 * ```typescript
 * const specPath = getApiSpecPath('identity');
 * // → '/abs/path/to/libs/shared-schemas/src/api/identity-api.yaml'
 * ```
 */
export function getApiSpecPath(service: string): string {
  if (!service || typeof service !== 'string') {
    throw new Error(
      `Invalid service parameter: expected a non-empty string, received "${String(service)}"`
    );
  }

  const sanitized = service.replace(/[^a-zA-Z0-9_-]/g, '');
  if (sanitized !== service) {
    throw new Error(
      `Invalid service parameter: "${service}" contains disallowed characters. ` +
        'Only alphanumeric characters, hyphens, and underscores are permitted.'
    );
  }

  return path.resolve(path.join(__dirname, 'api', `${service}-api.yaml`));
}

/**
 * Loads and parses a JSON Schema event definition file from disk.
 *
 * Reads the file synchronously using `fs.readFileSync` and parses the
 * content as JSON. This is intended for use during application startup
 * or in build/test tooling — not in hot request paths.
 *
 * @param domain - Event domain name (e.g., 'entity', 'record', 'identity').
 *   Corresponds to the filename prefix: `{domain}.events.json`.
 * @returns Parsed JSON Schema object.
 * @throws {Error} If the domain parameter is invalid.
 * @throws {Error} If the schema file does not exist or cannot be read.
 * @throws {SyntaxError} If the file content is not valid JSON.
 *
 * @example
 * ```typescript
 * const entitySchema = loadEventSchema('entity');
 * console.log(entitySchema.$schema); // 'http://json-schema.org/draft-07/schema#'
 * ```
 */
export function loadEventSchema(domain: string): Record<string, unknown> {
  const schemaPath = getEventSchemaPath(domain);

  try {
    const content = fs.readFileSync(schemaPath, 'utf-8');
    const parsed: unknown = JSON.parse(content);

    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error(
        `Event schema file "${schemaPath}" does not contain a valid JSON object. ` +
          'Expected a JSON Schema object at the top level.'
      );
    }

    return parsed as Record<string, unknown>;
  } catch (error: unknown) {
    if (error instanceof SyntaxError) {
      throw new SyntaxError(
        `Failed to parse event schema file "${schemaPath}": ${error.message}`
      );
    }

    const errorWithCode = error as { code?: string; message?: string };
    if (errorWithCode.code === 'ENOENT') {
      throw new Error(
        `Event schema file not found: "${schemaPath}". ` +
          `Ensure the "${domain}.events.json" file exists in the events/ directory.`
      );
    }

    if (errorWithCode.code === 'EACCES') {
      throw new Error(
        `Permission denied reading event schema file: "${schemaPath}". ` +
          'Check filesystem permissions.'
      );
    }

    throw error;
  }
}

/**
 * Loads an OpenAPI YAML specification file from disk and returns its
 * raw content as a string. Consumers are responsible for parsing the
 * YAML content (e.g., using `js-yaml` or another YAML parser).
 *
 * Reads the file synchronously using `fs.readFileSync`. This is intended
 * for use during application startup, testing, or tooling — not in hot
 * request paths.
 *
 * @param service - Service name (e.g., 'identity', 'crm', 'entity-management').
 *   Corresponds to the filename prefix: `{service}-api.yaml`.
 * @returns Raw YAML string content of the OpenAPI spec file.
 * @throws {Error} If the service parameter is invalid.
 * @throws {Error} If the spec file does not exist or cannot be read.
 *
 * @example
 * ```typescript
 * const yamlString = loadApiSpec('identity');
 * // Parse with your preferred YAML library:
 * // const spec = yaml.load(yamlString);
 * ```
 */
export function loadApiSpec(service: string): string {
  const specPath = getApiSpecPath(service);

  try {
    return fs.readFileSync(specPath, 'utf-8');
  } catch (error: unknown) {
    const errorWithCode = error as { code?: string; message?: string };

    if (errorWithCode.code === 'ENOENT') {
      throw new Error(
        `API spec file not found: "${specPath}". ` +
          `Ensure the "${service}-api.yaml" file exists in the api/ directory.`
      );
    }

    if (errorWithCode.code === 'EACCES') {
      throw new Error(
        `Permission denied reading API spec file: "${specPath}". ` +
          'Check filesystem permissions.'
      );
    }

    throw error;
  }
}
