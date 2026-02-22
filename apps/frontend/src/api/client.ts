/**
 * Centralized API Client Wrapper
 *
 * This is the single HTTP API client that ALL frontend API calls go through.
 * It replaces the direct server-side invocation patterns from the monolith:
 *
 * 1. **ApiControllerBase.cs** — response envelope (`BaseResponseModel` with
 *    Success, Errors, StatusCode, Timestamp, Message, Object, Hash).
 * 2. **WebApiController.cs** — constructor-injected managers (`RecordManager`,
 *    `EntityManager`, `SecurityManager`) are now HTTP calls to per-service
 *    Lambda handlers routed through API Gateway.
 * 3. **JwtMiddleware.cs** — server-side JWT extraction from the Authorization
 *    header (`token.Substring(7)` to strip "Bearer ") is flipped: this module
 *    ATTACHES the Bearer token on every outgoing request.
 * 4. **ErpMiddleware.cs** — per-request DB context + security scope binding is
 *    replaced by JWT claims extracted in Lambda event context; the sign-out on
 *    stale-auth pattern maps to the 401 auto-refresh interceptor here.
 * 5. **ErpErrorHandlingMiddleware.cs** — global exception capture is replaced by
 *    the response interceptor extracting structured ApiError from the envelope.
 *
 * AAP compliance:
 * - §0.8.1 — No hardcoded URLs; VITE_API_URL env var
 * - §0.8.3 — No secrets in bundle; only JWT access token attached
 * - §0.8.5 — X-Correlation-ID on every request for distributed tracing
 * - §0.8.6 — Path-based API versioning via `/v1/` prefix
 *
 * @module api/client
 */

import axios from 'axios';
import type {
  AxiosError,
  AxiosInstance,
  AxiosResponse,
  InternalAxiosRequestConfig,
} from 'axios';
import { v4 as uuidv4 } from 'uuid';
import { getAccessToken } from './auth';

// ---------------------------------------------------------------------------
// Type Definitions
// ---------------------------------------------------------------------------

/**
 * Mirrors the monolith's `BaseResponseModel` / `ResponseModel<T>` envelope.
 *
 * Source: `WebVella.Erp.Api.Models.BaseResponseModel`
 *   - Success: bool
 *   - Errors: List<ErrorModel>
 *   - StatusCode: HttpStatusCode
 *   - Timestamp: DateTime (UTC)
 *   - Message: string
 *
 * Extended with ResponseModel<T>.Object and ResponseModel.Hash for typed
 * payloads and cache validation.
 */
export interface ApiResponse<T = unknown> {
  /** Whether the operation completed successfully */
  success: boolean;
  /** Structured error details — mirrors List<ErrorModel> */
  errors: ApiErrorItem[];
  /** HTTP status code echoed in the body (from BaseResponseModel.StatusCode) */
  statusCode: number;
  /** ISO 8601 UTC timestamp set in DoBadRequestResponse / DoResponse */
  timestamp: string;
  /** Human-readable message (dev mode may include stack trace) */
  message: string;
  /** Typed payload — maps to ResponseModel<T>.Object */
  object?: T;
  /** ETag-like hash for cache validation — maps to ResponseModel.Hash */
  hash?: string;
}

/**
 * Individual error entry within the response envelope.
 * Mirrors `WebVella.Erp.Api.Models.ErrorModel(key, value, message)`.
 */
export interface ApiErrorItem {
  /** Field or domain key identifying the error source (e.g. "eql", "email") */
  key: string;
  /** Contextual value associated with the error */
  value: string;
  /** Human-readable error description */
  message: string;
}

/**
 * Client-side error representation extracted from failed API responses.
 * Provides a uniform structure for error handling across the SPA regardless
 * of whether the error originated from HTTP status codes or envelope-level
 * `success: false` responses.
 */
export interface ApiError {
  /** Primary error message */
  message: string;
  /** Structured error details from the response envelope */
  errors: ApiErrorItem[];
  /** HTTP status code (0 if the request never reached the server) */
  status: number;
  /** ISO 8601 UTC timestamp of when the error was captured */
  timestamp: string;
}

// ---------------------------------------------------------------------------
// Axios Module Augmentation — retry flag for 401 token refresh
// ---------------------------------------------------------------------------

declare module 'axios' {
  export interface InternalAxiosRequestConfig {
    /** Internal flag to prevent infinite 401 retry loops */
    _retry?: boolean;
  }
}

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

/**
 * API base URL constructed from the `VITE_API_URL` environment variable.
 *
 * - LocalStack:  http://localhost:4566/v1
 * - Production:  https://api.example.com/v1
 *
 * The `/v1/` suffix implements path-based API versioning at the HTTP API
 * Gateway level (AAP §0.8.6). The VITE_ prefix ensures Vite exposes the
 * value at build time via `import.meta.env`.
 */
const BASE_URL = `${
  (import.meta.env.VITE_API_URL as string | undefined) || 'http://localhost:4566'
}/v1`;

/**
 * Default request timeout in milliseconds.
 * 30 seconds accommodates Lambda cold starts (< 1s for .NET AOT, < 3s for
 * Node.js per AAP §0.8.2) plus actual processing time.
 */
const REQUEST_TIMEOUT_MS = 30_000;

// ---------------------------------------------------------------------------
// Axios Instance
// ---------------------------------------------------------------------------

/**
 * Pre-configured Axios instance serving as the single HTTP client for the
 * entire SPA. Replaces the monolith's in-process invocation of managers
 * (`RecordManager`, `EntityManager`, etc.) with HTTP calls routed through
 * API Gateway to per-domain Lambda handlers.
 */
const apiClient: AxiosInstance = axios.create({
  baseURL: BASE_URL,
  timeout: REQUEST_TIMEOUT_MS,
  headers: {
    'Content-Type': 'application/json',
    Accept: 'application/json',
  },
});

// ---------------------------------------------------------------------------
// Request Interceptor — JWT Token Attachment + Correlation-ID
// ---------------------------------------------------------------------------

/**
 * Attaches JWT Bearer token and a unique correlation-ID to every request.
 *
 * Replaces:
 * - JwtMiddleware.cs: Server-side extraction of `Authorization: Bearer <token>`
 *   and validation via `AuthService.GetValidSecurityTokenAsync`. Here the
 *   frontend PRODUCES the header instead of consuming it.
 * - Monolith's single-process request tracing: replaced by the
 *   `X-Correlation-ID` header propagated to all downstream Lambda functions
 *   for structured JSON logging (AAP §0.8.5).
 */
apiClient.interceptors.request.use(
  async (config: InternalAxiosRequestConfig): Promise<InternalAxiosRequestConfig> => {
    // 1. Attach JWT access token from Cognito (via auth module)
    //    getAccessToken() handles proactive refresh when near-expiry
    const token = await getAccessToken();
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }

    // 2. Inject correlation-ID for distributed tracing (AAP §0.8.5)
    config.headers['X-Correlation-ID'] = uuidv4();

    return config;
  },
  (error: unknown) => Promise.reject(error),
);

// ---------------------------------------------------------------------------
// Response Interceptor — Envelope Validation + 401 Auto-Refresh
// ---------------------------------------------------------------------------

/**
 * Handles two categories of API errors:
 *
 * 1. **Envelope-level errors** (HTTP 200 but `success: false`):
 *    Mirrors `ApiControllerBase.DoResponse` where errors in the body cause
 *    the status code to change to 400. The Lambda handlers may return 200
 *    with `success: false` for backward compatibility — this interceptor
 *    normalises those into rejections.
 *
 * 2. **HTTP-level errors** (4xx / 5xx):
 *    - 401: Token expired → attempt refresh via Cognito REFRESH_TOKEN_AUTH
 *      flow and retry the original request. On refresh failure, redirect to
 *      login (mirrors ErpMiddleware.cs SignOutAsync on stale auth).
 *    - 400: Maps to `DoBadRequestResponse` envelope extraction.
 *    - 404: Maps to `DoPageNotFoundResponse` / `DoItemNotFoundResponse`.
 *    - 500: Extracts error details or falls back to generic message
 *      (mirrors prod-mode "An internal error occurred!" from
 *      ApiControllerBase.DoBadRequestResponse).
 */
apiClient.interceptors.response.use(
  (response: AxiosResponse): AxiosResponse => {
    // Check for business-logic errors in the envelope even on HTTP 200
    // The monolith could return success=false with HTTP 200 in DoResponse
    if (
      response.data != null &&
      typeof response.data === 'object' &&
      'success' in response.data &&
      response.data.success === false
    ) {
      const envelopeData = response.data as ApiResponse;
      const envelopeError: ApiError = {
        message: envelopeData.message || 'Operation failed',
        errors: Array.isArray(envelopeData.errors) ? envelopeData.errors : [],
        status: envelopeData.statusCode || response.status,
        timestamp: envelopeData.timestamp || new Date().toISOString(),
      };
      // eslint-disable-next-line prefer-promise-reject-errors
      return Promise.reject(envelopeError) as never;
    }

    return response;
  },

  async (error: AxiosError<ApiResponse>): Promise<AxiosResponse> => {
    const originalRequest = error.config;

    // --- 401 Unauthorized: Token expired, attempt refresh -----------------
    // Replaces ErpMiddleware.cs pattern:
    //   if (context.User.Identity.IsAuthenticated && user == null)
    //     → SignOutAsync()
    // Here we attempt a transparent token refresh first.
    if (
      error.response?.status === 401 &&
      originalRequest != null &&
      !originalRequest._retry
    ) {
      originalRequest._retry = true;

      try {
        // Dynamic import prevents circular dependency and allows tree-shaking
        // when the 401 code path is never triggered.
        const { refreshAccessToken } = await import('./auth');
        const newToken = await refreshAccessToken();

        if (newToken && originalRequest.headers) {
          originalRequest.headers.Authorization = `Bearer ${newToken}`;
          return apiClient(originalRequest);
        }
      } catch {
        // Refresh token expired or revoked — force re-login
        // Mirrors ErpMiddleware.cs SignOutAsync + redirect
        const { logout } = await import('./auth');
        await logout();
        window.location.href = '/login';
      }
    }

    // --- Extract structured error from response envelope ------------------
    const responseData = error.response?.data;

    const apiError: ApiError = {
      message:
        responseData?.message ||
        error.message ||
        'An unexpected error occurred',
      errors: Array.isArray(responseData?.errors) ? responseData.errors : [],
      status: error.response?.status ?? 0,
      timestamp: responseData?.timestamp || new Date().toISOString(),
    };

    return Promise.reject(apiError);
  },
);

// ---------------------------------------------------------------------------
// Typed Convenience Methods
// ---------------------------------------------------------------------------

/**
 * Performs a typed GET request and unwraps the `ApiResponse<T>` envelope.
 *
 * @param url    - Relative URL path (e.g. `/entities`, `/records/123`)
 * @param params - Optional query parameters
 * @returns The unwrapped ApiResponse envelope with typed `.object`
 */
export async function get<T = unknown>(
  url: string,
  params?: Record<string, unknown>,
): Promise<ApiResponse<T>> {
  const response = await apiClient.get<ApiResponse<T>>(url, { params });
  return response.data;
}

/**
 * Performs a typed POST request and unwraps the `ApiResponse<T>` envelope.
 *
 * @param url  - Relative URL path
 * @param data - Request body payload
 * @returns The unwrapped ApiResponse envelope with typed `.object`
 */
export async function post<T = unknown>(
  url: string,
  data?: unknown,
): Promise<ApiResponse<T>> {
  const response = await apiClient.post<ApiResponse<T>>(url, data);
  return response.data;
}

/**
 * Performs a typed PUT request and unwraps the `ApiResponse<T>` envelope.
 *
 * @param url  - Relative URL path
 * @param data - Request body payload
 * @returns The unwrapped ApiResponse envelope with typed `.object`
 */
export async function put<T = unknown>(
  url: string,
  data?: unknown,
): Promise<ApiResponse<T>> {
  const response = await apiClient.put<ApiResponse<T>>(url, data);
  return response.data;
}

/**
 * Performs a typed PATCH request and unwraps the `ApiResponse<T>` envelope.
 *
 * @param url  - Relative URL path
 * @param data - Partial update payload
 * @returns The unwrapped ApiResponse envelope with typed `.object`
 */
export async function patch<T = unknown>(
  url: string,
  data?: unknown,
): Promise<ApiResponse<T>> {
  const response = await apiClient.patch<ApiResponse<T>>(url, data);
  return response.data;
}

/**
 * Performs a typed DELETE request and unwraps the `ApiResponse<T>` envelope.
 * Named `del` to avoid collision with JavaScript's `delete` reserved word.
 *
 * @param url - Relative URL path (e.g. `/entities/abc-123`)
 * @returns The unwrapped ApiResponse envelope with typed `.object`
 */
export async function del<T = unknown>(
  url: string,
): Promise<ApiResponse<T>> {
  const response = await apiClient.delete<ApiResponse<T>>(url);
  return response.data;
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default apiClient;
