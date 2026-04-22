/**
 * useAuth — Cognito JWT Authentication Hook
 *
 * Replaces the monolith's dual authentication system:
 *   - AuthService.cs   (cookie + JWT dual auth → pure Cognito JWT)
 *   - JwtMiddleware.cs (bearer token extraction → client-side token storage)
 *   - SecurityContext.cs (AsyncLocal user scoping → React state)
 *
 * Authentication flow:
 *   1. Login: Cognito USER_PASSWORD_AUTH → tokens stored in localStorage
 *   2. Token refresh: Cognito REFRESH_TOKEN_AUTH at 80% of token lifetime
 *   3. Logout: Clear localStorage + optional Cognito GlobalSignOut
 *   4. Initialization: Restore session from localStorage on mount
 *
 * Per AAP §0.8.3: No secrets in frontend bundle — uses public Cognito app client.
 * Per AAP §0.8.6: COGNITO_CLIENT_SECRET via SSM, never in env vars.
 *
 * @module libs/shared-ui/src/hooks/useAuth
 */

import { useState, useCallback, useEffect, useRef } from 'react';
import {
  CognitoIdentityProviderClient,
  InitiateAuthCommand,
  GlobalSignOutCommand,
} from '@aws-sdk/client-cognito-identity-provider';
import type { ErpUser } from '../types';

// ---------------------------------------------------------------------------
// Constants — localStorage keys for token persistence
// ---------------------------------------------------------------------------

/** localStorage key for the Cognito access token */
const STORAGE_KEY_ACCESS_TOKEN = 'webvella_access_token';

/** localStorage key for the Cognito refresh token */
const STORAGE_KEY_REFRESH_TOKEN = 'webvella_refresh_token';

/** localStorage key for the serialized ErpUser profile */
const STORAGE_KEY_USER = 'webvella_user';

/** localStorage key for the token expiry timestamp (ms since epoch) */
const STORAGE_KEY_TOKEN_EXPIRY = 'webvella_token_expiry';

/**
 * Fraction of token lifetime at which automatic refresh is triggered.
 * At 80% of the token lifetime the hook silently refreshes the access token.
 * Mirrors the monolith's JWT_TOKEN_FORCE_REFRESH_MINUTES = 120 concept
 * (AuthService.cs line 20) adapted to Cognito's token expiry model.
 */
const TOKEN_REFRESH_THRESHOLD = 0.8;

// ---------------------------------------------------------------------------
// Exported Interfaces
// ---------------------------------------------------------------------------

/**
 * Authentication state managed by the useAuth hook.
 *
 * Replaces:
 *   - HttpContext.Items["User"] (ErpMiddleware) → state.user
 *   - JWT string from AuthService.BuildTokenAsync → state.token
 *   - Cookie auth state → state.isAuthenticated
 *   - Synchronous blocking → state.isLoading (async in React)
 *   - Exception throwing in AuthService.GetTokenAsync → state.error
 */
export interface AuthState {
  /** Current authenticated user (null when not logged in) */
  user: ErpUser | null;
  /** Cognito access token (null when not authenticated) */
  token: string | null;
  /** Cognito refresh token for silent token renewal (null when not authenticated) */
  refreshToken: string | null;
  /** Whether the user is currently authenticated with a valid token */
  isAuthenticated: boolean;
  /** Whether an authentication operation is in progress (login, refresh, init) */
  isLoading: boolean;
  /** Last authentication error message (null when no error) */
  error: string | null;
}

/**
 * Authentication actions exposed by the useAuth hook.
 *
 * Replaces:
 *   - AuthService.Authenticate() + GetTokenAsync() → login
 *   - AuthService.Logout()                         → logout
 *   - AuthService.GetNewTokenAsync()                → refreshToken
 */
export interface AuthActions {
  /** Authenticate with email/password via Cognito USER_PASSWORD_AUTH */
  login: (email: string, password: string) => Promise<void>;
  /** Sign out: clear localStorage + optional Cognito GlobalSignOut */
  logout: () => Promise<void>;
  /** Silently refresh the access token via Cognito REFRESH_TOKEN_AUTH */
  refreshToken: () => Promise<void>;
}

/**
 * Login form credentials.
 * Matches AuthService.Authenticate(email, password) parameter signature.
 */
export interface LoginCredentials {
  /** User email address */
  email: string;
  /** User password */
  password: string;
}

// ---------------------------------------------------------------------------
// Internal Interfaces
// ---------------------------------------------------------------------------

/**
 * Parsed token set from a Cognito InitiateAuth response.
 */
interface CognitoTokens {
  /** Cognito access token (JWT) */
  accessToken: string;
  /** Cognito ID token containing user claims (JWT) */
  idToken: string;
  /** Cognito refresh token for silent renewal */
  refreshToken: string;
  /** Seconds until the access token expires */
  expiresIn: number;
}

// ---------------------------------------------------------------------------
// Environment Variable Access (safe for non-Vite environments)
// ---------------------------------------------------------------------------

/**
 * Safely read a Vite-style environment variable (`import.meta.env.VITE_*`).
 *
 * Vite injects env vars at build time onto `import.meta.env`. This helper
 * gracefully handles non-Vite environments (SSR, test, Node.js) where
 * `import.meta.env` may not exist or may not have a `VITE_*` key.
 *
 * @param name - The environment variable name (e.g. 'VITE_AWS_REGION')
 * @returns The value or undefined if not available
 */
function getEnvVar(name: string): string | undefined {
  // Try Vite's import.meta.env first (available in Vite builds and Vitest)
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
    // import.meta may not be available (CommonJS, older Node.js)
  }
  // Fallback to process.env (Node.js, SSR, and test environments)
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
    // process may not be available (browser, Deno)
  }
  return undefined;
}

// ---------------------------------------------------------------------------
// Cognito Client Configuration
// ---------------------------------------------------------------------------

/**
 * Build a CognitoIdentityProviderClient configured for the current environment.
 *
 * - Region: VITE_AWS_REGION env var or 'us-east-1' (per AAP §0.8.6)
 * - Endpoint: VITE_API_URL when targeting LocalStack (per AAP §0.8.6)
 *
 * The client is lazily created (singleton per module load) to avoid
 * instantiating AWS SDK resources during SSR or testing.
 */
function createCognitoClient(): CognitoIdentityProviderClient {
  const region = getEnvVar('VITE_AWS_REGION') ?? 'us-east-1';
  const endpoint = getEnvVar('VITE_API_URL');

  const clientConfig: ConstructorParameters<
    typeof CognitoIdentityProviderClient
  >[0] = {
    region,
  };

  if (endpoint) {
    clientConfig.endpoint = endpoint;
  }

  return new CognitoIdentityProviderClient(clientConfig);
}

/** Module-level Cognito client singleton */
let cognitoClient: CognitoIdentityProviderClient | null = null;

/**
 * Get or create the Cognito client singleton.
 * Lazy initialization avoids side-effects at module import time.
 */
function getCognitoClient(): CognitoIdentityProviderClient {
  if (!cognitoClient) {
    cognitoClient = createCognitoClient();
  }
  return cognitoClient;
}

/**
 * Read the Cognito App Client ID from environment variables.
 * Per AAP §0.8.3: This is a PUBLIC client ID (no client secret) —
 * safe to include in the frontend bundle.
 */
function getCognitoClientId(): string {
  const clientId = getEnvVar('VITE_COGNITO_CLIENT_ID');

  if (!clientId) {
    throw new Error(
      'VITE_COGNITO_CLIENT_ID environment variable is not configured. ' +
        'Set it in your .env or .env.local file.'
    );
  }
  return clientId;
}

// ---------------------------------------------------------------------------
// JWT Decoding (base64url → JSON — no external library)
// ---------------------------------------------------------------------------

/**
 * Decode a JWT payload without validation.
 *
 * This performs client-side claim extraction ONLY — it does NOT validate
 * the token signature. Server-side validation is handled by API Gateway's
 * native JWT authorizer (per AAP §0.4.2).
 *
 * Implementation: split on '.', take payload segment, base64url-decode,
 * JSON.parse. This is the standard approach for SPAs that only need to
 * read claims (e.g., user info from the ID token).
 *
 * @param token - A JWT string (header.payload.signature)
 * @returns Parsed payload as a record, or null on decode failure
 */
function decodeJwtPayload(
  token: string
): Record<string, unknown> | null {
  try {
    const segments = token.split('.');
    if (segments.length !== 3) {
      return null;
    }
    const payload = segments[1];

    // base64url → base64: replace URL-safe chars and pad
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const padded =
      base64.length % 4 === 0
        ? base64
        : base64 + '='.repeat(4 - (base64.length % 4));

    const jsonString = atob(padded);

    // Handle multi-byte UTF-8 characters correctly
    const bytes = Uint8Array.from(jsonString, (c) => c.charCodeAt(0));
    const decoded = new TextDecoder().decode(bytes);

    return JSON.parse(decoded) as Record<string, unknown>;
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------------------
// User Extraction from Cognito ID Token
// ---------------------------------------------------------------------------

/**
 * Build an ErpUser from decoded Cognito ID token claims.
 *
 * Claim mapping (Cognito → ErpUser):
 *   - sub                → id (replaces ClaimTypes.NameIdentifier)
 *   - email              → email (replaces ClaimTypes.Email)
 *   - cognito:groups     → isAdmin (admin group membership)
 *   - given_name         → firstName
 *   - family_name        → lastName
 *   - preferred_username → username (fallback to email)
 *   - picture            → image
 *
 * @param claims - Decoded ID token payload
 * @returns Fully populated ErpUser
 */
function buildUserFromClaims(claims: Record<string, unknown>): ErpUser {
  const groups = Array.isArray(claims['cognito:groups'])
    ? (claims['cognito:groups'] as string[])
    : [];

  const isAdmin = groups.some(
    (g) =>
      g.toLowerCase() === 'administrator' ||
      g.toLowerCase() === 'admin'
  );

  return {
    id: (claims['sub'] as string) ?? '',
    username:
      (claims['preferred_username'] as string) ??
      (claims['email'] as string) ??
      '',
    email: (claims['email'] as string) ?? '',
    firstName: (claims['given_name'] as string) ?? '',
    lastName: (claims['family_name'] as string) ?? '',
    image: (claims['picture'] as string) ?? '',
    createdOn: new Date().toISOString(),
    lastLoggedIn: new Date().toISOString(),
    isAdmin,
    preferences: undefined,
  };
}

// ---------------------------------------------------------------------------
// localStorage Helpers (safe for SSR / test environments)
// ---------------------------------------------------------------------------

/**
 * Safely read a value from localStorage.
 * Returns null if localStorage is unavailable (SSR, test, etc.).
 */
function storageGet(key: string): string | null {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      return window.localStorage.getItem(key);
    }
  } catch {
    // localStorage may throw in private browsing or when blocked by policy
  }
  return null;
}

/**
 * Safely write a value to localStorage.
 */
function storageSet(key: string, value: string): void {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.setItem(key, value);
    }
  } catch {
    // Silently ignore storage failures (quota exceeded, private browsing)
  }
}

/**
 * Safely remove a value from localStorage.
 */
function storageRemove(key: string): void {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.removeItem(key);
    }
  } catch {
    // Silently ignore removal failures
  }
}

/**
 * Persist the complete token set and user profile to localStorage.
 */
function persistSession(
  accessToken: string,
  refreshTokenValue: string,
  user: ErpUser,
  expiresIn: number
): void {
  const expiryMs = Date.now() + expiresIn * 1000;
  storageSet(STORAGE_KEY_ACCESS_TOKEN, accessToken);
  storageSet(STORAGE_KEY_REFRESH_TOKEN, refreshTokenValue);
  storageSet(STORAGE_KEY_USER, JSON.stringify(user));
  storageSet(STORAGE_KEY_TOKEN_EXPIRY, String(expiryMs));
}

/**
 * Clear all authentication data from localStorage.
 */
function clearSession(): void {
  storageRemove(STORAGE_KEY_ACCESS_TOKEN);
  storageRemove(STORAGE_KEY_REFRESH_TOKEN);
  storageRemove(STORAGE_KEY_USER);
  storageRemove(STORAGE_KEY_TOKEN_EXPIRY);
}

// ---------------------------------------------------------------------------
// Error Message Normalization
// ---------------------------------------------------------------------------

/**
 * Convert Cognito error responses to user-friendly messages.
 *
 * Cognito SDK throws typed errors (NotAuthorizedException,
 * UserNotFoundException, etc.). This function maps them to readable strings
 * so the UI never shows raw SDK error names.
 */
function normalizeAuthError(error: unknown): string {
  if (error instanceof Error) {
    const name = error.name ?? '';

    if (
      name === 'NotAuthorizedException' ||
      name === 'UserNotFoundException'
    ) {
      return 'Invalid email or password. Please try again.';
    }
    if (name === 'UserNotConfirmedException') {
      return 'Your account has not been confirmed. Please check your email.';
    }
    if (name === 'PasswordResetRequiredException') {
      return 'A password reset is required. Please check your email.';
    }
    if (name === 'TooManyRequestsException') {
      return 'Too many login attempts. Please wait a moment and try again.';
    }
    if (name === 'InvalidParameterException') {
      return 'Invalid login parameters. Please check your input.';
    }
    if (name === 'ResourceNotFoundException') {
      return 'Authentication service is not configured correctly. Please contact support.';
    }

    // Fallback: use the error message but strip AWS SDK boilerplate
    if (error.message) {
      return error.message;
    }
  }

  return 'An unexpected authentication error occurred. Please try again.';
}

// ---------------------------------------------------------------------------
// Initial State Factory
// ---------------------------------------------------------------------------

/** Create a fresh unauthenticated state */
function createInitialState(): AuthState {
  return {
    user: null,
    token: null,
    refreshToken: null,
    isAuthenticated: false,
    isLoading: true, // starts loading to allow initialization check
    error: null,
  };
}

// ---------------------------------------------------------------------------
// useAuth Hook
// ---------------------------------------------------------------------------

/**
 * React hook for Cognito JWT authentication.
 *
 * Provides:
 *   - AuthState: current authentication state (user, tokens, loading, errors)
 *   - AuthActions: login, logout, refreshToken actions
 *
 * Replaces the monolith's:
 *   - AuthService.cs      → Cognito SDK calls
 *   - JwtMiddleware.cs    → localStorage token persistence
 *   - SecurityContext.cs   → React state for current user
 *   - ErpMiddleware.cs     → initialization from stored session
 *
 * Usage:
 * ```tsx
 * const { user, isAuthenticated, isLoading, error, login, logout, refreshToken } = useAuth();
 * ```
 */
export function useAuth(): Omit<AuthState, 'refreshToken'> & AuthActions {
  const [state, setState] = useState<AuthState>(createInitialState);

  /**
   * Ref to the automatic token refresh timeout.
   * Cleared on logout and component unmount to prevent memory leaks.
   */
  const refreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(
    null
  );

  /**
   * Ref tracking whether the hook instance is still mounted.
   * Prevents state updates after unmount during async operations.
   */
  const mountedRef = useRef<boolean>(true);

  // -----------------------------------------------------------------------
  // Token Refresh Scheduling
  // -----------------------------------------------------------------------

  /**
   * Schedule an automatic token refresh at TOKEN_REFRESH_THRESHOLD (80%)
   * of the token's lifetime.
   *
   * Example: If token expires in 3600s (1h), refresh fires at 2880s (48min).
   * This mirrors the monolith's JWT_TOKEN_FORCE_REFRESH_MINUTES = 120 pattern
   * (AuthService.cs line 20) adapted to Cognito's configurable token lifetime.
   */
  const scheduleTokenRefresh = useCallback(
    (expiresIn: number, doRefresh: () => Promise<void>) => {
      // Clear any existing timer
      if (refreshTimerRef.current !== null) {
        clearTimeout(refreshTimerRef.current);
        refreshTimerRef.current = null;
      }

      // Schedule refresh at 80% of token lifetime (minimum 30 seconds)
      const delayMs = Math.max(
        expiresIn * TOKEN_REFRESH_THRESHOLD * 1000,
        30_000
      );

      refreshTimerRef.current = setTimeout(() => {
        if (mountedRef.current) {
          doRefresh().catch(() => {
            // Silent failure on auto-refresh — user will be prompted on next API call
          });
        }
      }, delayMs);
    },
    []
  );

  /**
   * Clear the scheduled refresh timer.
   */
  const clearRefreshTimer = useCallback(() => {
    if (refreshTimerRef.current !== null) {
      clearTimeout(refreshTimerRef.current);
      refreshTimerRef.current = null;
    }
  }, []);

  // -----------------------------------------------------------------------
  // Core Authentication Actions
  // -----------------------------------------------------------------------

  /**
   * Perform a Cognito REFRESH_TOKEN_AUTH flow to renew the access token.
   *
   * Replaces AuthService.GetNewTokenAsync() which re-validated the user
   * against the DB and rebuilt the JWT with updated claims.
   *
   * In the Cognito model, the refresh token is long-lived (configurable,
   * default 30 days) and the access token is short-lived (default 1 hour).
   */
  const doRefreshToken = useCallback(async (): Promise<void> => {
    const storedRefreshToken = storageGet(STORAGE_KEY_REFRESH_TOKEN);
    if (!storedRefreshToken) {
      if (mountedRef.current) {
        clearSession();
        setState({
          user: null,
          token: null,
          refreshToken: null,
          isAuthenticated: false,
          isLoading: false,
          error: 'Session expired. Please log in again.',
        });
      }
      clearRefreshTimer();
      return;
    }

    try {
      const client = getCognitoClient();
      const clientId = getCognitoClientId();

      const command = new InitiateAuthCommand({
        AuthFlow: 'REFRESH_TOKEN_AUTH',
        ClientId: clientId,
        AuthParameters: {
          REFRESH_TOKEN: storedRefreshToken,
        },
      });

      const response = await client.send(command);
      const authResult = response.AuthenticationResult;

      if (
        !authResult?.AccessToken ||
        !authResult.IdToken
      ) {
        throw new Error('Cognito refresh response missing required tokens.');
      }

      // Cognito REFRESH_TOKEN_AUTH does not return a new RefreshToken —
      // reuse the existing one
      const tokens: CognitoTokens = {
        accessToken: authResult.AccessToken,
        idToken: authResult.IdToken,
        refreshToken: storedRefreshToken,
        expiresIn: authResult.ExpiresIn ?? 3600,
      };

      // Decode ID token to rebuild user profile with potentially updated claims
      const claims = decodeJwtPayload(tokens.idToken);
      const user = claims
        ? buildUserFromClaims(claims)
        : JSON.parse(storageGet(STORAGE_KEY_USER) ?? 'null');

      if (!user) {
        throw new Error('Unable to decode user profile from refreshed token.');
      }

      // Persist updated tokens and user
      persistSession(
        tokens.accessToken,
        tokens.refreshToken,
        user,
        tokens.expiresIn
      );

      if (mountedRef.current) {
        setState({
          user,
          token: tokens.accessToken,
          refreshToken: tokens.refreshToken,
          isAuthenticated: true,
          isLoading: false,
          error: null,
        });
      }

      // Schedule the next automatic refresh
      scheduleTokenRefresh(tokens.expiresIn, doRefreshToken);
    } catch (err) {
      // Refresh failure — clear session and require re-login
      clearSession();
      clearRefreshTimer();

      if (mountedRef.current) {
        setState({
          user: null,
          token: null,
          refreshToken: null,
          isAuthenticated: false,
          isLoading: false,
          error: normalizeAuthError(err),
        });
      }
    }
  }, [scheduleTokenRefresh, clearRefreshTimer]);

  /**
   * Authenticate with email and password via Cognito USER_PASSWORD_AUTH.
   *
   * Replaces:
   *   - SecurityManager.GetUser(email, password) — MD5 credential validation
   *   - AuthService.Authenticate() — cookie auth + ClaimsIdentity creation
   *   - AuthService.GetTokenAsync() — JWT issuance
   *
   * Per AAP §0.7.5: A User Migration Lambda Trigger on the Cognito user pool
   * handles first-login migration from MD5-hashed passwords.
   */
  const login = useCallback(
    async (email: string, password: string): Promise<void> => {
      // Clear any previous error and set loading state
      setState((prev) => ({
        ...prev,
        isLoading: true,
        error: null,
      }));

      try {
        const client = getCognitoClient();
        const clientId = getCognitoClientId();

        const command = new InitiateAuthCommand({
          AuthFlow: 'USER_PASSWORD_AUTH',
          ClientId: clientId,
          AuthParameters: {
            USERNAME: email,
            PASSWORD: password,
          },
        });

        const response = await client.send(command);
        const authResult = response.AuthenticationResult;

        if (
          !authResult?.AccessToken ||
          !authResult.IdToken ||
          !authResult.RefreshToken
        ) {
          throw new Error(
            'Authentication succeeded but the response is missing required tokens.'
          );
        }

        const tokens: CognitoTokens = {
          accessToken: authResult.AccessToken,
          idToken: authResult.IdToken,
          refreshToken: authResult.RefreshToken,
          expiresIn: authResult.ExpiresIn ?? 3600,
        };

        // Decode ID token to extract user claims
        // Claim mapping: sub → id, email → email, cognito:groups → isAdmin,
        // given_name → firstName, family_name → lastName
        const claims = decodeJwtPayload(tokens.idToken);
        if (!claims) {
          throw new Error('Failed to decode user claims from ID token.');
        }

        const user = buildUserFromClaims(claims);

        // Persist tokens and user profile to localStorage
        persistSession(
          tokens.accessToken,
          tokens.refreshToken,
          user,
          tokens.expiresIn
        );

        if (mountedRef.current) {
          setState({
            user,
            token: tokens.accessToken,
            refreshToken: tokens.refreshToken,
            isAuthenticated: true,
            isLoading: false,
            error: null,
          });
        }

        // Schedule automatic token refresh before expiry
        scheduleTokenRefresh(tokens.expiresIn, doRefreshToken);
      } catch (err) {
        clearSession();
        clearRefreshTimer();

        if (mountedRef.current) {
          setState({
            user: null,
            token: null,
            refreshToken: null,
            isAuthenticated: false,
            isLoading: false,
            error: normalizeAuthError(err),
          });
        }
      }
    },
    [scheduleTokenRefresh, clearRefreshTimer, doRefreshToken]
  );

  /**
   * Sign out the current user.
   *
   * Replaces AuthService.Logout() which called
   * HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).
   *
   * Flow:
   *   1. Clear all tokens from localStorage
   *   2. Cancel any pending token refresh timer
   *   3. Reset state to unauthenticated
   *   4. Best-effort Cognito GlobalSignOut (server-side session invalidation)
   */
  const logout = useCallback(async (): Promise<void> => {
    const storedAccessToken = storageGet(STORAGE_KEY_ACCESS_TOKEN);

    // Clear local state first (immediate UX feedback)
    clearSession();
    clearRefreshTimer();

    if (mountedRef.current) {
      setState({
        user: null,
        token: null,
        refreshToken: null,
        isAuthenticated: false,
        isLoading: false,
        error: null,
      });
    }

    // Best-effort server-side sign-out — do not block on failure
    if (storedAccessToken) {
      try {
        const client = getCognitoClient();
        const command = new GlobalSignOutCommand({
          AccessToken: storedAccessToken,
        });
        await client.send(command);
      } catch {
        // Ignore GlobalSignOut failures — local state is already cleared.
        // The access token will naturally expire, and the refresh token
        // won't be usable after this point from the client's perspective.
      }
    }
  }, [clearRefreshTimer]);

  // -----------------------------------------------------------------------
  // Session Initialization (on mount)
  // -----------------------------------------------------------------------

  useEffect(() => {
    mountedRef.current = true;

    /**
     * Restore authentication state from localStorage.
     *
     * Replaces the monolith's ErpMiddleware.cs per-request pipeline which:
     *   1. Read the cookie/JWT from the request
     *   2. Validated the token via AuthService.GetValidSecurityTokenAsync
     *   3. Loaded the user via SecurityManager.GetUser(userId)
     *   4. Set HttpContext.Items["User"] and SecurityContext.OpenScope(user)
     *
     * In the SPA model:
     *   1. Read tokens from localStorage
     *   2. Check if access token has expired
     *   3. If valid, restore state directly from stored data
     *   4. If expired but refresh token exists, attempt silent refresh
     *   5. If no tokens, set unauthenticated state
     */
    async function initializeAuth(): Promise<void> {
      const storedToken = storageGet(STORAGE_KEY_ACCESS_TOKEN);
      const storedRefresh = storageGet(STORAGE_KEY_REFRESH_TOKEN);
      const storedUser = storageGet(STORAGE_KEY_USER);
      const storedExpiry = storageGet(STORAGE_KEY_TOKEN_EXPIRY);

      // No stored session — set unauthenticated and stop loading
      if (!storedToken || !storedRefresh || !storedUser) {
        if (mountedRef.current) {
          setState({
            user: null,
            token: null,
            refreshToken: null,
            isAuthenticated: false,
            isLoading: false,
            error: null,
          });
        }
        return;
      }

      // Parse stored user profile
      let user: ErpUser | null = null;
      try {
        user = JSON.parse(storedUser) as ErpUser;
      } catch {
        // Corrupted user data — clear and reset
        clearSession();
        if (mountedRef.current) {
          setState({
            user: null,
            token: null,
            refreshToken: null,
            isAuthenticated: false,
            isLoading: false,
            error: null,
          });
        }
        return;
      }

      const expiryMs = storedExpiry ? Number(storedExpiry) : 0;
      const now = Date.now();

      if (expiryMs > now) {
        // Token is still valid — restore session
        const remainingSeconds = Math.floor((expiryMs - now) / 1000);

        if (mountedRef.current) {
          setState({
            user,
            token: storedToken,
            refreshToken: storedRefresh,
            isAuthenticated: true,
            isLoading: false,
            error: null,
          });
        }

        // Schedule refresh for remaining lifetime
        scheduleTokenRefresh(remainingSeconds, doRefreshToken);
      } else if (storedRefresh) {
        // Token expired but refresh token exists — attempt silent refresh
        try {
          await doRefreshToken();
        } catch {
          // Refresh failed — clear session
          clearSession();
          if (mountedRef.current) {
            setState({
              user: null,
              token: null,
              refreshToken: null,
              isAuthenticated: false,
              isLoading: false,
              error: null,
            });
          }
        }
      } else {
        // No valid tokens — clear and reset
        clearSession();
        if (mountedRef.current) {
          setState({
            user: null,
            token: null,
            refreshToken: null,
            isAuthenticated: false,
            isLoading: false,
            error: null,
          });
        }
      }
    }

    initializeAuth().catch(() => {
      // Catch-all: ensure we never leave the hook in a loading state
      if (mountedRef.current) {
        setState({
          user: null,
          token: null,
          refreshToken: null,
          isAuthenticated: false,
          isLoading: false,
          error: null,
        });
      }
    });

    // Cleanup on unmount: cancel timers and mark as unmounted
    return () => {
      mountedRef.current = false;
      if (refreshTimerRef.current !== null) {
        clearTimeout(refreshTimerRef.current);
        refreshTimerRef.current = null;
      }
    };
  }, [doRefreshToken, scheduleTokenRefresh]);

  // -----------------------------------------------------------------------
  // Return combined state + actions
  // -----------------------------------------------------------------------

  return {
    // AuthState properties
    user: state.user,
    token: state.token,
    isAuthenticated: state.isAuthenticated,
    isLoading: state.isLoading,
    error: state.error,
    // AuthActions
    login,
    logout,
    /**
     * Refresh token action: triggers Cognito REFRESH_TOKEN_AUTH flow.
     * Note: `AuthState.refreshToken` (the raw token string) is managed internally
     * by the hook and persisted to localStorage. The `refreshToken` returned here
     * is the ACTION function from `AuthActions` — the most useful form for consumers.
     * The raw Cognito refresh token string is only needed within the hook's internal
     * refresh flow and is not exposed to avoid the naming collision with the action.
     */
    refreshToken: doRefreshToken,
  };
}
