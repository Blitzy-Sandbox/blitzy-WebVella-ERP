/**
 * useApi — Configured API Client Hook
 *
 * Replaces patterns from the monolith's primary API surface:
 *   - WebApiController.cs (100+ endpoints, all [Authorize] by default)
 *   - JwtMiddleware.cs    (Bearer token extraction from Authorization header)
 *   - BaseModels.cs       (BaseResponseModel / ResponseModel envelope pattern)
 *
 * Provides a memoized API client with:
 *   - Base URL resolution via VITE_API_URL (LocalStack) or explicit override
 *   - JWT bearer token injection from the useAuth hook (Cognito tokens)
 *   - Response normalization to the ApiResponse<T> envelope pattern
 *   - Retry logic for transient 5xx / network failures
 *   - AbortController-based timeout and component-unmount cleanup
 *
 * Per AAP §0.8.1: Pure static SPA — API URL configurable at build time.
 * Per AAP §0.8.3: No secrets in bundle — JWT from auth hook, URL from env var.
 * Per AAP §0.8.6: VITE_API_URL points to API Gateway (LocalStack or production).
 *
 * @module libs/shared-ui/src/hooks/useApi
 */

import { useCallback, useMemo, useRef, useEffect } from 'react';
import { useAuth } from './useAuth';
import type { BaseResponseModel, ErrorModel, ApiResponse } from '../types';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default request timeout in milliseconds (30 seconds) */
const DEFAULT_TIMEOUT_MS = 30_000;

/** Default retry delay in milliseconds */
const DEFAULT_RETRY_DELAY_MS = 1_000;

/** Default number of retries (0 = no retry) */
const DEFAULT_RETRIES = 0;

/** HTTP status code threshold — 5xx codes are considered transient/retryable */
const SERVER_ERROR_THRESHOLD = 500;

// ---------------------------------------------------------------------------
// Exported Interfaces
// ---------------------------------------------------------------------------

/**
 * Configuration options for the useApi hook.
 *
 * Controls base URL, timeout, retry behaviour, and default headers
 * for all requests made through the returned ApiClient.
 */
export interface UseApiOptions {
  /**
   * Override the base URL for API requests.
   * Resolution priority:
   *   1. This parameter (explicit override)
   *   2. import.meta.env.VITE_API_URL (Vite build-time env var)
   *   3. '' (empty string — relative URL for same-origin API)
   */
  baseUrl?: string;

  /** Request timeout in milliseconds. Default: 30 000 (30 s). */
  timeout?: number;

  /**
   * Number of retry attempts for transient failures (5xx, network errors).
   * 4xx client errors are never retried. Default: 0 (no retry).
   */
  retries?: number;

  /** Delay between retry attempts in milliseconds. Default: 1 000 (1 s). */
  retryDelay?: number;

  /** Additional default headers merged into every request. */
  headers?: Record<string, string>;
}

/**
 * Per-request configuration passed to individual API calls.
 *
 * Allows overriding HTTP method, headers, body, query params, timeout,
 * and authentication behaviour on a per-call basis.
 */
export interface RequestConfig {
  /** HTTP method. Default: 'GET'. */
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

  /** Per-request headers (merged on top of defaults + auth). */
  headers?: Record<string, string>;

  /** Request body — serialized to JSON for non-GET requests. */
  body?: unknown;

  /**
   * Query parameters appended to the URL.
   * Undefined values are filtered out before serialization.
   */
  params?: Record<string, string | number | boolean | undefined>;

  /** Per-request timeout override in milliseconds. */
  timeout?: number;

  /**
   * When true, skip JWT token injection for this request.
   * Useful for public endpoints like login.
   */
  skipAuth?: boolean;
}

/**
 * Typed API client interface returned by the useApi hook.
 *
 * All methods are generic over <T> — the payload type inside
 * the ApiResponse<T> envelope's `object` field.
 */
export interface ApiClient {
  /** HTTP GET request. */
  get: <T = unknown>(
    url: string,
    config?: RequestConfig,
  ) => Promise<ApiResponse<T>>;

  /** HTTP POST request with optional JSON body. */
  post: <T = unknown>(
    url: string,
    body?: unknown,
    config?: RequestConfig,
  ) => Promise<ApiResponse<T>>;

  /** HTTP PUT request with optional JSON body. */
  put: <T = unknown>(
    url: string,
    body?: unknown,
    config?: RequestConfig,
  ) => Promise<ApiResponse<T>>;

  /** HTTP PATCH request with optional JSON body. */
  patch: <T = unknown>(
    url: string,
    body?: unknown,
    config?: RequestConfig,
  ) => Promise<ApiResponse<T>>;

  /** HTTP DELETE request. */
  del: <T = unknown>(
    url: string,
    config?: RequestConfig,
  ) => Promise<ApiResponse<T>>;

  /**
   * Low-level request method — all convenience methods delegate here.
   * Callers must supply a full RequestConfig including `method`.
   */
  request: <T = unknown>(
    url: string,
    config: RequestConfig,
  ) => Promise<ApiResponse<T>>;
}

// ---------------------------------------------------------------------------
// ApiError Class
// ---------------------------------------------------------------------------

/**
 * Structured API error with envelope metadata.
 *
 * Thrown when:
 *   - The API response envelope has `success === false`
 *   - The HTTP response status is 4xx or 5xx
 *   - A network/fetch error occurs
 *
 * Maps to the monolith's error handling pattern where
 * `response.Success = false` and `response.Errors` contain detail items
 * (ErrorModel: { key, value, message }).
 */
export class ApiError extends Error {
  /** HTTP status code (0 for network errors) */
  public readonly status: number;

  /** Structured error details from the response envelope */
  public readonly errors: ErrorModel[];

  /** Always false — mirrors BaseResponseModel.success */
  public readonly success: boolean;

  /** ISO 8601 timestamp from the response envelope (or generated) */
  public readonly timestamp: string;

  constructor(
    message: string,
    status: number,
    errors: ErrorModel[] = [],
    timestamp?: string,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.errors = errors;
    this.success = false;
    this.timestamp = timestamp ?? new Date().toISOString();

    // Maintain proper prototype chain for instanceof checks
    Object.setPrototypeOf(this, ApiError.prototype);
  }
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Safely read a Vite-style environment variable (`import.meta.env.VITE_*`).
 *
 * Handles non-Vite environments (SSR, test, Node.js) where
 * `import.meta.env` may not exist.
 *
 * @param name - The environment variable name
 * @returns The value or undefined
 */
function getEnvVar(name: string): string | undefined {
  try {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const meta = import.meta as any;
    if (meta && typeof meta === 'object' && meta.env) {
      const value = meta.env[name];
      if (typeof value === 'string' && value.length > 0) {
        return value;
      }
    }
    /* eslint-enable @typescript-eslint/no-explicit-any */
  } catch {
    // import.meta may not be available in CommonJS or older Node.js
  }
  try {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const proc = (globalThis as any).process;
    if (proc && typeof proc === 'object' && proc.env) {
      const value = proc.env[name];
      if (typeof value === 'string' && value.length > 0) {
        return value;
      }
    }
    /* eslint-enable @typescript-eslint/no-explicit-any */
  } catch {
    // process may not be available in browser or Deno
  }
  return undefined;
}

/**
 * Build a full URL from base + path + query params.
 *
 * - Strips trailing slash from baseUrl to avoid double slashes
 * - Filters out undefined param values
 * - URL-encodes param keys and values
 *
 * @param baseUrl - API base URL (may be empty for same-origin)
 * @param path    - Request path (e.g., '/v1/entities')
 * @param params  - Optional query parameters
 * @returns Fully constructed URL string
 */
function buildUrl(
  baseUrl: string,
  path: string,
  params?: Record<string, string | number | boolean | undefined>,
): string {
  const normalizedBase = baseUrl.endsWith('/')
    ? baseUrl.slice(0, -1)
    : baseUrl;
  let url = `${normalizedBase}${path}`;

  if (params) {
    const searchParts: string[] = [];
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined) {
        searchParts.push(
          `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`,
        );
      }
    }
    if (searchParts.length > 0) {
      url += `?${searchParts.join('&')}`;
    }
  }

  return url;
}

/**
 * Check whether a response body looks like a BaseResponseModel envelope.
 *
 * The monolith always returns { success, message, errors, ... } so we
 * detect the envelope by the presence of the `success` boolean field.
 */
function isEnvelopeResponse(data: unknown): data is BaseResponseModel {
  return (
    typeof data === 'object' &&
    data !== null &&
    'success' in data &&
    typeof (data as Record<string, unknown>)['success'] === 'boolean'
  );
}

/**
 * Determine whether an HTTP status code is retryable (server/transient error).
 * Only 5xx status codes are retried — 4xx (client errors) are never retried.
 */
function isRetryableStatus(status: number): boolean {
  return status >= SERVER_ERROR_THRESHOLD;
}

/**
 * Delay execution for a specified number of milliseconds.
 * Used between retry attempts.
 */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

// ---------------------------------------------------------------------------
// useApi Hook
// ---------------------------------------------------------------------------

/**
 * React hook that provides a fully configured, memoized API client.
 *
 * Features:
 *   - Automatic JWT bearer token injection from useAuth (Cognito)
 *   - Base URL from VITE_API_URL or explicit override
 *   - Response normalization to ApiResponse<T> envelope
 *   - Retry logic for transient 5xx / network failures
 *   - AbortController timeout and unmount cleanup
 *   - Typed convenience methods: get, post, put, patch, del
 *
 * @example
 * ```tsx
 * const api = useApi({ timeout: 10_000 });
 *
 * // GET with typed response
 * const { object: entities } = await api.get<Entity[]>('/v1/entities');
 *
 * // POST with body
 * const { object: created } = await api.post<Entity>('/v1/entities', { name: 'task' });
 *
 * // Skip auth for login
 * const { object: tokens } = await api.post<LoginResponse>(
 *   '/v1/auth/login',
 *   credentials,
 *   { skipAuth: true },
 * );
 * ```
 *
 * @param options - Optional hook-level configuration
 * @returns Memoized ApiClient with typed convenience methods
 */
export function useApi(options?: UseApiOptions): ApiClient {
  const { token } = useAuth();

  // Resolve configuration with defaults
  const resolvedBaseUrl =
    options?.baseUrl ?? getEnvVar('VITE_API_URL') ?? '';
  const resolvedTimeout = options?.timeout ?? DEFAULT_TIMEOUT_MS;
  const resolvedRetries = options?.retries ?? DEFAULT_RETRIES;
  const resolvedRetryDelay = options?.retryDelay ?? DEFAULT_RETRY_DELAY_MS;
  const defaultHeaders = options?.headers;

  // Track active AbortControllers for cleanup on unmount
  const activeControllersRef = useRef<Set<AbortController>>(new Set());

  // Cleanup: abort all in-flight requests when the component unmounts
  useEffect(() => {
    const controllers = activeControllersRef.current;
    return () => {
      for (const controller of controllers) {
        controller.abort();
      }
      controllers.clear();
    };
  }, []);

  /**
   * Core request method — handles the complete request lifecycle:
   *   1. Build URL with query params
   *   2. Merge headers (defaults → options → per-request → auth)
   *   3. Execute fetch with AbortController timeout
   *   4. Parse and normalize response to ApiResponse<T>
   *   5. Retry on transient failures
   */
  const request = useCallback(
    async <T = unknown>(
      url: string,
      config: RequestConfig,
    ): Promise<ApiResponse<T>> => {
      const method = config.method ?? 'GET';
      const requestTimeout = config.timeout ?? resolvedTimeout;
      const fullUrl = buildUrl(resolvedBaseUrl, url, config.params);

      // Merge headers: Content-Type → default headers → per-request → auth
      const mergedHeaders: Record<string, string> = {
        'Content-Type': 'application/json',
        Accept: 'application/json',
        ...(defaultHeaders ?? {}),
        ...(config.headers ?? {}),
      };

      // JWT Bearer token injection (mirrors JwtMiddleware.cs Authorization header pattern)
      if (config.skipAuth !== true && token) {
        mergedHeaders['Authorization'] = `Bearer ${token}`;
      }

      // Serialize body for non-GET methods
      const fetchBody =
        config.body !== undefined && method !== 'GET'
          ? JSON.stringify(config.body)
          : undefined;

      // Retry loop
      let lastError: ApiError | null = null;
      const maxAttempts = 1 + resolvedRetries;

      for (let attempt = 0; attempt < maxAttempts; attempt++) {
        // Wait before retry (skip delay on first attempt)
        if (attempt > 0) {
          await delay(resolvedRetryDelay * attempt);
        }

        // Create AbortController for timeout management
        const controller = new AbortController();
        activeControllersRef.current.add(controller);

        const timeoutId = setTimeout(() => {
          controller.abort();
        }, requestTimeout);

        try {
          const response = await fetch(fullUrl, {
            method,
            headers: mergedHeaders,
            body: fetchBody,
            signal: controller.signal,
          });

          clearTimeout(timeoutId);
          activeControllersRef.current.delete(controller);

          // Attempt to parse JSON response body
          let responseData: unknown;
          const contentType = response.headers.get('content-type') ?? '';
          if (contentType.includes('application/json')) {
            try {
              responseData = await response.json();
            } catch {
              responseData = null;
            }
          } else {
            // Non-JSON response — read as text for potential error messages
            const textBody = await response.text();
            responseData = textBody.length > 0 ? textBody : null;
          }

          // Handle successful HTTP status with envelope response
          if (response.ok) {
            if (isEnvelopeResponse(responseData)) {
              const envelope = responseData as ApiResponse<T>;
              if (envelope.success) {
                return envelope;
              }
              // Envelope indicates logical failure (success === false)
              throw new ApiError(
                envelope.message || 'Request failed',
                response.status,
                envelope.errors ?? [],
                envelope.timestamp,
              );
            }

            // Non-envelope successful response — wrap in synthetic envelope
            return {
              timestamp: new Date().toISOString(),
              success: true,
              message: '',
              hash: null,
              errors: [],
              accessWarnings: [],
              object: responseData as T,
            };
          }

          // Handle HTTP error status codes (4xx, 5xx)
          if (isEnvelopeResponse(responseData)) {
            const envelope = responseData as BaseResponseModel;
            const apiError = new ApiError(
              envelope.message || response.statusText || 'Request failed',
              response.status,
              envelope.errors ?? [],
              envelope.timestamp,
            );

            // Only retry on 5xx (server/transient) errors
            if (isRetryableStatus(response.status) && attempt < maxAttempts - 1) {
              lastError = apiError;
              continue;
            }
            throw apiError;
          }

          // Non-envelope HTTP error
          const errorMessage =
            typeof responseData === 'string' && responseData.length > 0
              ? responseData
              : response.statusText || `HTTP ${response.status}`;

          const httpError = new ApiError(errorMessage, response.status);

          if (isRetryableStatus(response.status) && attempt < maxAttempts - 1) {
            lastError = httpError;
            continue;
          }
          throw httpError;
        } catch (error: unknown) {
          clearTimeout(timeoutId);
          activeControllersRef.current.delete(controller);

          // Re-throw ApiError instances directly (already structured)
          if (error instanceof ApiError) {
            // Check if this is a retryable error we haven't exhausted retries for
            if (isRetryableStatus(error.status) && attempt < maxAttempts - 1) {
              lastError = error;
              continue;
            }
            throw error;
          }

          // Handle abort (timeout or unmount)
          if (error instanceof DOMException && error.name === 'AbortError') {
            const abortError = new ApiError(
              'Request timeout — the server did not respond in time',
              0,
            );
            if (attempt < maxAttempts - 1) {
              lastError = abortError;
              continue;
            }
            throw abortError;
          }

          // Handle network errors (fetch failures, DNS, CORS, etc.)
          const networkMessage =
            error instanceof Error
              ? error.message
              : 'An unexpected network error occurred';
          const networkError = new ApiError(networkMessage, 0);

          if (attempt < maxAttempts - 1) {
            lastError = networkError;
            continue;
          }
          throw networkError;
        }
      }

      // All retry attempts exhausted — throw the last captured error
      throw lastError ?? new ApiError('Request failed after retries', 0);
    },
    [token, resolvedBaseUrl, resolvedTimeout, resolvedRetries, resolvedRetryDelay, defaultHeaders],
  );

  // ---------------------------------------------------------------------------
  // Convenience Methods
  // ---------------------------------------------------------------------------

  const get = useCallback(
    <T = unknown>(url: string, config?: RequestConfig): Promise<ApiResponse<T>> =>
      request<T>(url, { ...config, method: 'GET' }),
    [request],
  );

  const post = useCallback(
    <T = unknown>(url: string, body?: unknown, config?: RequestConfig): Promise<ApiResponse<T>> =>
      request<T>(url, { ...config, method: 'POST', body }),
    [request],
  );

  const put = useCallback(
    <T = unknown>(url: string, body?: unknown, config?: RequestConfig): Promise<ApiResponse<T>> =>
      request<T>(url, { ...config, method: 'PUT', body }),
    [request],
  );

  const patch = useCallback(
    <T = unknown>(url: string, body?: unknown, config?: RequestConfig): Promise<ApiResponse<T>> =>
      request<T>(url, { ...config, method: 'PATCH', body }),
    [request],
  );

  const del = useCallback(
    <T = unknown>(url: string, config?: RequestConfig): Promise<ApiResponse<T>> =>
      request<T>(url, { ...config, method: 'DELETE' }),
    [request],
  );

  // ---------------------------------------------------------------------------
  // Memoized ApiClient Object
  // ---------------------------------------------------------------------------

  const client: ApiClient = useMemo(
    () => ({ get, post, put, patch, del, request }),
    [get, post, put, patch, del, request],
  );

  return client;
}
