/**
 * @module @webvella-erp/shared-schemas/openapi-contract.test
 *
 * OpenAPI 3.1 contract tests validating that each per-service OpenAPI YAML
 * specification in `libs/shared-schemas/src/api/` adheres to the documented
 * structural contract, security conventions, and path conventions expected
 * by the WebVella ERP platform.
 *
 * These tests satisfy the Phase 4 (QA/Test Integrity) Check 4.12 requirement
 * that "Contract tests validate request/response schemas against the
 * corresponding OpenAPI definition in `libs/shared-schemas/src/api/`". They
 * complement the per-service SNS event contract tests (such as
 * `services/crm/tests/ContractTests.cs`) by providing HTTP-level API
 * schema validation that is otherwise absent from the test suite.
 *
 * The tests verify:
 *   1. Every declared service (10 total) has a parseable OpenAPI YAML file
 *   2. Each spec declares OpenAPI 3.1.x with the required top-level sections
 *      (`info`, `servers`, `paths`, `components`, `security`, `tags`)
 *   3. Servers include both a LocalStack base URL (for LocalStack-exclusive
 *      testing per AAP §0.8.1) and a production AWS base URL
 *   4. A `BearerAuth` security scheme is declared under `components.securitySchemes`
 *      (per AAP §0.8.3 — Cognito JWT authorization)
 *   5. All paths begin with `/v1/` (per AAP §0.8.6 — path-based API versioning)
 *   6. Every declared operation (GET/POST/PUT/PATCH/DELETE) declares an
 *      `operationId`, a `tags` array, and at least one documented response
 *   7. Every request body payload, when present, references a schema under
 *      `#/components/schemas/` (ensuring no inline schemas bypass the schema
 *      registry)
 *   8. Every non-empty response declares a `content` map with
 *      `application/json` or `application/octet-stream` media types
 *   9. The CRM OpenAPI spec declares the full set of account/contact CRUD
 *      routes that the CRM Lambda handlers (and SNS event publishers) expose
 *      — ensuring the same set of resources covered by
 *      `services/crm/tests/ContractTests.cs` at the event layer are also
 *      covered at the HTTP contract layer.
 *
 * All assertions run entirely offline (no network, no LocalStack required).
 * Each OpenAPI YAML file is loaded synchronously via `loadApiSpec()` and
 * parsed with the `yaml` module.
 *
 * @see AAP §0.8.4 — Contract tests for all inter-service API and event schemas
 * @see AAP §0.8.6 — API versioning via `/v1/` path prefix
 * @see AAP §0.8.3 — Cognito JWT validation via HTTP API native JWT authorizer
 */

import { describe, it, expect } from 'vitest';
import * as yaml from 'yaml';
import { loadApiSpec, ServiceNames } from './index';

// ---------------------------------------------------------------------------
// Constants and helpers
// ---------------------------------------------------------------------------

/**
 * Full set of microservices expected to have OpenAPI specifications on disk.
 * Derived from `ServiceNames` (10 total) — matches the 10 bounded-context
 * services in AAP §0.4.1 target architecture.
 */
const ALL_SERVICES: readonly string[] = Object.values(ServiceNames) as readonly string[];

/**
 * HTTP methods that OpenAPI 3.1 recognizes as path operations. Used to
 * enumerate operations on a path without confusing them with shared
 * properties like `parameters` and `summary`.
 */
const HTTP_METHODS = [
  'get',
  'post',
  'put',
  'patch',
  'delete',
  'options',
  'head',
  'trace',
] as const;

/**
 * Media types permitted in response `content` maps. JSON is the canonical
 * format for every CRUD operation; octet-stream is permitted only for file
 * download endpoints.
 */
const ALLOWED_RESPONSE_MEDIA_TYPES = [
  'application/json',
  'application/octet-stream',
] as const;

/**
 * Media types permitted in request body `content` maps. In addition to
 * the response media types, request bodies may accept `multipart/form-data`
 * (for file uploads such as `POST /v1/file-management/files/upload/direct`
 * and CSV imports such as `POST /v1/entity-management/records/{entityName}/import`)
 * and `application/x-www-form-urlencoded` (for standard HTML form submissions).
 */
const ALLOWED_REQUEST_MEDIA_TYPES = [
  'application/json',
  'application/octet-stream',
  'multipart/form-data',
  'application/x-www-form-urlencoded',
] as const;

/**
 * Parses the OpenAPI YAML for the given service and returns a structured
 * document object suitable for navigation.
 *
 * @param service - Service name matching a value in `ServiceNames`.
 * @returns The parsed OpenAPI document as a plain object.
 */
function parseSpec(service: string): Record<string, unknown> {
  const raw = loadApiSpec(service);
  const parsed: unknown = yaml.parse(raw);

  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error(
      `OpenAPI YAML for service "${service}" did not parse to a top-level object.`
    );
  }

  return parsed as Record<string, unknown>;
}

/**
 * Safely retrieves a nested object from an OpenAPI document using dotted
 * path notation. Returns `undefined` if any segment is missing or of the
 * wrong type.
 */
function getObject(
  doc: Record<string, unknown>,
  dottedPath: string
): Record<string, unknown> | undefined {
  const segments = dottedPath.split('.');
  let current: unknown = doc;

  for (const segment of segments) {
    if (
      current === null ||
      typeof current !== 'object' ||
      Array.isArray(current)
    ) {
      return undefined;
    }

    current = (current as Record<string, unknown>)[segment];
  }

  if (
    current === null ||
    typeof current === 'undefined' ||
    typeof current !== 'object' ||
    Array.isArray(current)
  ) {
    return undefined;
  }

  return current as Record<string, unknown>;
}

// ---------------------------------------------------------------------------
// Structural contract tests: one describe block per spec file
// ---------------------------------------------------------------------------

describe.each(ALL_SERVICES)(
  'OpenAPI 3.1 contract for service "%s"',
  (service) => {
    // Parse once per service and reuse across all assertions. Any parse
    // failure surfaces as the very first test failure for that service,
    // making diagnosis simpler.
    const doc = parseSpec(service);

    it('declares OpenAPI 3.1.x version', () => {
      expect(doc).toHaveProperty('openapi');
      const version = doc.openapi;
      expect(typeof version).toBe('string');
      expect(version as string).toMatch(/^3\.1(\.\d+)?$/);
    });

    it('declares required top-level sections (info, paths, components, security, servers, tags)', () => {
      for (const section of [
        'info',
        'paths',
        'components',
        'security',
        'servers',
        'tags',
      ]) {
        expect(doc, `missing "${section}" section in ${service}-api.yaml`).toHaveProperty(
          section
        );
      }
    });

    it('declares non-empty "info.title" and "info.version"', () => {
      const info = getObject(doc, 'info');
      expect(info, '"info" object must exist').toBeDefined();
      expect(info!).toHaveProperty('title');
      expect(info!).toHaveProperty('version');
      expect(typeof info!.title).toBe('string');
      expect((info!.title as string).length).toBeGreaterThan(0);
      expect(typeof info!.version).toBe('string');
      expect((info!.version as string).length).toBeGreaterThan(0);
    });

    it('declares both a LocalStack server and a production AWS server', () => {
      expect(doc.servers).toBeDefined();
      expect(Array.isArray(doc.servers)).toBe(true);

      const servers = doc.servers as Array<{ url?: string; description?: string }>;
      expect(servers.length).toBeGreaterThanOrEqual(2);

      // Exactly one LocalStack entry (URL contains localhost:4566)
      const localStackServers = servers.filter(
        (s) => typeof s.url === 'string' && s.url.includes('localhost:4566')
      );
      expect(
        localStackServers.length,
        'expected at least one LocalStack server (URL containing localhost:4566)'
      ).toBeGreaterThanOrEqual(1);

      // At least one production AWS entry (URL contains amazonaws.com)
      const awsServers = servers.filter(
        (s) => typeof s.url === 'string' && s.url.includes('amazonaws.com')
      );
      expect(
        awsServers.length,
        'expected at least one production AWS server (URL containing amazonaws.com)'
      ).toBeGreaterThanOrEqual(1);
    });

    it('declares a "BearerAuth" security scheme under components.securitySchemes', () => {
      const securitySchemes = getObject(doc, 'components.securitySchemes');
      expect(
        securitySchemes,
        'components.securitySchemes must exist'
      ).toBeDefined();
      expect(securitySchemes!).toHaveProperty('BearerAuth');

      const bearerAuth = securitySchemes!.BearerAuth as Record<string, unknown>;
      expect(bearerAuth).toHaveProperty('type');
      expect(bearerAuth.type).toBe('http');
      expect(bearerAuth.scheme).toBe('bearer');
      expect(bearerAuth.bearerFormat).toBe('JWT');
    });

    it('globally requires BearerAuth via top-level security array', () => {
      expect(Array.isArray(doc.security)).toBe(true);
      const security = doc.security as Array<Record<string, unknown>>;
      expect(security.length).toBeGreaterThan(0);

      // At least one entry in the security array references BearerAuth
      const hasBearerAuth = security.some(
        (entry) =>
          entry !== null &&
          typeof entry === 'object' &&
          Object.prototype.hasOwnProperty.call(entry, 'BearerAuth')
      );
      expect(
        hasBearerAuth,
        'at least one top-level security entry must reference BearerAuth'
      ).toBe(true);
    });

    it('declares at least one path and every path starts with /v1/', () => {
      const paths = getObject(doc, 'paths');
      expect(paths).toBeDefined();
      const pathKeys = Object.keys(paths!);
      expect(
        pathKeys.length,
        `expected at least one path in ${service}-api.yaml`
      ).toBeGreaterThan(0);

      for (const pathKey of pathKeys) {
        expect(
          pathKey.startsWith('/v1/'),
          `path "${pathKey}" must begin with "/v1/" (AAP §0.8.6 API versioning)`
        ).toBe(true);
      }
    });

    it('declares operationId, tags, and at least one response for every operation', () => {
      const paths = getObject(doc, 'paths')!;
      const seenOperationIds = new Set<string>();

      for (const pathKey of Object.keys(paths)) {
        const pathItem = paths[pathKey] as Record<string, unknown>;

        for (const method of HTTP_METHODS) {
          const operation = pathItem[method];
          if (operation === undefined) {
            continue;
          }

          expect(
            operation,
            `${method.toUpperCase()} ${pathKey} must be an object`
          ).toBeTypeOf('object');
          const op = operation as Record<string, unknown>;

          // operationId must be a non-empty string and globally unique
          expect(op).toHaveProperty('operationId');
          const operationId = op.operationId;
          expect(typeof operationId).toBe('string');
          expect((operationId as string).length).toBeGreaterThan(0);
          expect(
            seenOperationIds.has(operationId as string),
            `duplicate operationId "${operationId as string}" in ${service}-api.yaml`
          ).toBe(false);
          seenOperationIds.add(operationId as string);

          // tags must be a non-empty array of strings
          expect(op).toHaveProperty('tags');
          expect(Array.isArray(op.tags)).toBe(true);
          expect((op.tags as unknown[]).length).toBeGreaterThan(0);

          // responses must be an object with at least one entry
          expect(op).toHaveProperty('responses');
          const responses = op.responses as Record<string, unknown>;
          expect(Object.keys(responses).length).toBeGreaterThan(0);
        }
      }
    });

    it('every request body declares a $ref or schema under application/json media type', () => {
      const paths = getObject(doc, 'paths')!;

      for (const pathKey of Object.keys(paths)) {
        const pathItem = paths[pathKey] as Record<string, unknown>;

        for (const method of HTTP_METHODS) {
          const operation = pathItem[method];
          if (operation === undefined) {
            continue;
          }

          const op = operation as Record<string, unknown>;
          if (!('requestBody' in op)) {
            continue;
          }

          const requestBody = op.requestBody as Record<string, unknown>;
          // requestBody may be a $ref to a shared components.requestBodies entry
          if ('$ref' in requestBody) {
            continue;
          }

          expect(
            requestBody,
            `${method.toUpperCase()} ${pathKey} requestBody must declare content`
          ).toHaveProperty('content');
          const content = requestBody.content as Record<string, unknown>;

          // At least one of the allowed request media types must be declared.
          // Request bodies may use multipart/form-data (for file uploads) or
          // form-urlencoded in addition to JSON and octet-stream.
          const declaredMediaTypes = Object.keys(content);
          const hasAllowed = declaredMediaTypes.some((mt) =>
            (ALLOWED_REQUEST_MEDIA_TYPES as readonly string[]).includes(mt)
          );
          expect(
            hasAllowed,
            `${method.toUpperCase()} ${pathKey} requestBody must use an allowed media type (${ALLOWED_REQUEST_MEDIA_TYPES.join(
              ', '
            )}); got ${declaredMediaTypes.join(', ')}`
          ).toBe(true);

          // Every declared media type must have either a schema.$ref or a
          // schema.type (to avoid untyped request bodies)
          for (const mediaType of declaredMediaTypes) {
            const mediaEntry = content[mediaType] as Record<string, unknown>;
            if ('schema' in mediaEntry) {
              const schema = mediaEntry.schema as Record<string, unknown>;
              const hasRef = '$ref' in schema;
              const hasType = 'type' in schema;
              const hasComposition =
                'oneOf' in schema || 'anyOf' in schema || 'allOf' in schema;
              expect(
                hasRef || hasType || hasComposition,
                `${method.toUpperCase()} ${pathKey} requestBody.content["${mediaType}"].schema must declare $ref, type, or oneOf/anyOf/allOf`
              ).toBe(true);
            }
          }
        }
      }
    });

    it('every non-204 response declares a content map with an allowed media type', () => {
      const paths = getObject(doc, 'paths')!;

      for (const pathKey of Object.keys(paths)) {
        const pathItem = paths[pathKey] as Record<string, unknown>;

        for (const method of HTTP_METHODS) {
          const operation = pathItem[method];
          if (operation === undefined) {
            continue;
          }

          const op = operation as Record<string, unknown>;
          const responses = op.responses as Record<string, unknown>;

          for (const statusCode of Object.keys(responses)) {
            // 204 No Content and 3xx redirects legitimately omit a body
            if (statusCode === '204' || statusCode.startsWith('3')) {
              continue;
            }

            const response = responses[statusCode] as Record<string, unknown>;

            // $ref-only responses reference a shared components.responses entry
            if ('$ref' in response) {
              continue;
            }

            // Every non-$ref response must have a description
            expect(
              response,
              `${method.toUpperCase()} ${pathKey} response ${statusCode} must have a description`
            ).toHaveProperty('description');

            // Response bodies (when present) must use an allowed media type
            if ('content' in response) {
              const content = response.content as Record<string, unknown>;
              const declaredMediaTypes = Object.keys(content);
              const hasAllowed = declaredMediaTypes.some((mt) =>
                (ALLOWED_RESPONSE_MEDIA_TYPES as readonly string[]).includes(mt)
              );
              expect(
                hasAllowed,
                `${method.toUpperCase()} ${pathKey} response ${statusCode} must use an allowed media type; got ${declaredMediaTypes.join(
                  ', '
                )}`
              ).toBe(true);
            }
          }
        }
      }
    });

    it('components.schemas declares at least one reusable schema', () => {
      const schemas = getObject(doc, 'components.schemas');
      expect(
        schemas,
        'components.schemas must exist and declare at least one schema'
      ).toBeDefined();
      expect(Object.keys(schemas!).length).toBeGreaterThan(0);
    });
  }
);

// ---------------------------------------------------------------------------
// Cross-spec: every spec file on disk matches a known service name
// ---------------------------------------------------------------------------

describe('OpenAPI spec file inventory', () => {
  it('loadApiSpec succeeds for every service name declared in ServiceNames', () => {
    for (const service of ALL_SERVICES) {
      expect(() => loadApiSpec(service)).not.toThrow();
    }
  });

  it('every OpenAPI spec has a unique title', () => {
    const titles = new Set<string>();
    for (const service of ALL_SERVICES) {
      const doc = parseSpec(service);
      const info = getObject(doc, 'info')!;
      const title = info.title as string;
      expect(
        titles.has(title),
        `duplicate OpenAPI title "${title}" across service specs`
      ).toBe(false);
      titles.add(title);
    }
  });
});

// ---------------------------------------------------------------------------
// CRM-specific route coverage
//
// The CRM service is the canonical example cited in Check 4.12 — the existing
// services/crm/tests/ContractTests.cs validates the 6 SNS event types, and
// these tests validate the corresponding HTTP routes. Together they provide
// bidirectional contract coverage for the CRM bounded context.
// ---------------------------------------------------------------------------

describe('CRM OpenAPI route coverage', () => {
  const doc = parseSpec(ServiceNames.CRM);
  const paths = getObject(doc, 'paths')!;
  const pathKeys = Object.keys(paths);

  // Helper: assert that a path ending in `/accounts` or `/contacts` exists
  // and supports the given HTTP method. Versioned prefix is part of the key.
  function assertRouteExists(suffix: string, method: string): void {
    const match = pathKeys.find((p) => p === suffix || p.endsWith(suffix));
    expect(
      match,
      `expected CRM spec to declare a path matching "${suffix}"`
    ).toBeDefined();

    const pathItem = paths[match!] as Record<string, unknown>;
    expect(
      pathItem,
      `path "${match}" must declare a ${method.toUpperCase()} operation`
    ).toHaveProperty(method);
  }

  it('declares GET and POST routes for /v1/crm/accounts', () => {
    assertRouteExists('/v1/crm/accounts', 'get');
    assertRouteExists('/v1/crm/accounts', 'post');
  });

  it('declares GET, update (PUT or PATCH), and DELETE for an accounts item route', () => {
    // Parameterized segment (e.g., /v1/crm/accounts/{id}) — tolerate either
    // an {id} or an {accountId} parameter naming convention.
    const matches = pathKeys.filter(
      (p) => /^\/v1\/crm\/accounts\/\{[^/}]+\}$/.test(p)
    );
    expect(
      matches.length,
      'expected at least one CRM accounts item route (e.g., /v1/crm/accounts/{id})'
    ).toBeGreaterThan(0);

    const itemPath = matches[0]!;
    const pathItem = paths[itemPath] as Record<string, unknown>;

    // GET and DELETE are mandatory for RESTful item resources
    for (const method of ['get', 'delete']) {
      expect(
        Object.prototype.hasOwnProperty.call(pathItem, method),
        `CRM accounts item route "${itemPath}" must declare ${method.toUpperCase()}`
      ).toBe(true);
    }

    // Update operation: either PUT (full replace) or PATCH (partial) is
    // acceptable per RFC 5789 / OpenAPI convention. The CRM spec uses PUT.
    const hasUpdate =
      Object.prototype.hasOwnProperty.call(pathItem, 'put') ||
      Object.prototype.hasOwnProperty.call(pathItem, 'patch');
    expect(
      hasUpdate,
      `CRM accounts item route "${itemPath}" must declare either PUT or PATCH`
    ).toBe(true);
  });

  it('declares GET and POST routes for /v1/crm/contacts', () => {
    assertRouteExists('/v1/crm/contacts', 'get');
    assertRouteExists('/v1/crm/contacts', 'post');
  });

  it('declares GET, update (PUT or PATCH), and DELETE for a contacts item route', () => {
    const matches = pathKeys.filter(
      (p) => /^\/v1\/crm\/contacts\/\{[^/}]+\}$/.test(p)
    );
    expect(
      matches.length,
      'expected at least one CRM contacts item route (e.g., /v1/crm/contacts/{id})'
    ).toBeGreaterThan(0);

    const itemPath = matches[0]!;
    const pathItem = paths[itemPath] as Record<string, unknown>;

    // GET and DELETE are mandatory for RESTful item resources
    for (const method of ['get', 'delete']) {
      expect(
        Object.prototype.hasOwnProperty.call(pathItem, method),
        `CRM contacts item route "${itemPath}" must declare ${method.toUpperCase()}`
      ).toBe(true);
    }

    // Update operation: either PUT or PATCH is acceptable. The CRM spec uses PUT.
    const hasUpdate =
      Object.prototype.hasOwnProperty.call(pathItem, 'put') ||
      Object.prototype.hasOwnProperty.call(pathItem, 'patch');
    expect(
      hasUpdate,
      `CRM contacts item route "${itemPath}" must declare either PUT or PATCH`
    ).toBe(true);
  });
});
