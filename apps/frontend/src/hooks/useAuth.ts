/**
 * Authentication TanStack Query Hooks
 *
 * TanStack Query 5 hooks for authentication operations (login, logout, token
 * refresh, session validation, user profile fetch, password change). This is
 * the most foundational hook file — it bridges the Cognito auth module
 * (`../api/auth.ts`) and the Zustand auth store (`../stores/authStore.ts`)
 * with TanStack Query's declarative mutation/query pattern for consistent
 * loading/error/success state management.
 *
 * Replaces:
 *  - `AuthService.cs`       — Cookie + JWT dual auth (Authenticate, Logout,
 *                              GetTokenAsync, GetNewTokenAsync, GetUser)
 *  - `JwtMiddleware.cs`     — Bearer token extraction & validation pipeline
 *  - `SecurityContext.cs`   — AsyncLocal user scoping (CurrentUser, IsUserInRole)
 *
 * Auth flow overview:
 *  1. App mounts  → `useAuthSession()` checks for existing Cognito tokens
 *  2. Login       → `useLogin().mutate()` → Cognito USER_PASSWORD_AUTH
 *  3. Profile     → `useAuthUser()` fetches full ErpUser from API
 *  4. Refresh     → `useRefreshToken().mutate()` → Cognito REFRESH_TOKEN_AUTH
 *  5. Logout      → `useLogout().mutate()` → Cognito GlobalSignOut + cache clear
 *  6. Password    → `useChangePassword().mutate()` → Identity API POST
 *
 * Security constraints (AAP §0.8.1, §0.8.3):
 *  - No secrets in hooks — tokens managed by `../api/auth.ts` module closure
 *  - `queryClient.clear()` on logout — prevents stale data exposure
 *  - No localStorage — auth state in Zustand memory store only
 *  - All auth operations via `../api/auth.ts`, not direct API/Cognito calls
 *
 * @module hooks/useAuth
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  login,
  logout,
  refreshAccessToken,
  getAccessToken,
  getCurrentUser,
  isAuthenticated,
} from '../api/auth';
import type { AuthResult, CognitoUser } from '../api/auth';
import { useAuthStore } from '../stores/authStore';
import type { AuthUser } from '../stores/authStore';
import { get, post } from '../api/client';
import type { ErpUser, LoginRequest } from '../types/user';

// ---------------------------------------------------------------------------
// Query Keys
// ---------------------------------------------------------------------------

/**
 * Centralised query-key factory for all auth-related TanStack Query caches.
 * Prevents key collisions and enables targeted invalidation.
 */
const AUTH_QUERY_KEYS = {
  /** Session restoration query (runs once on mount) */
  session: ['auth', 'session'] as const,
  /** Full user profile query (fetches from Identity service API) */
  user: ['auth', 'user'] as const,
} as const;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Default token lifetime in milliseconds used as a fallback when JWT `exp`
 * claim cannot be decoded. Matches Cognito default access-token TTL (1 hour).
 */
const DEFAULT_TOKEN_LIFETIME_MS = 3_600_000;

/**
 * staleTime for user profile queries — 5 minutes.
 * Prevents unnecessary refetches while allowing periodic profile updates.
 */
const USER_PROFILE_STALE_TIME_MS = 5 * 60 * 1000;

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Extracts the `exp` claim from a JWT access token and returns the expiry
 * as a Unix timestamp in milliseconds.
 *
 * Falls back to `Date.now() + DEFAULT_TOKEN_LIFETIME_MS` when decoding fails.
 * No signature verification is performed — that is the responsibility of
 * API Gateway / Lambda authorizer.
 *
 * @param accessToken - Cognito JWT access token
 * @returns Expiry timestamp in milliseconds
 */
function extractTokenExpiry(accessToken: string): number {
  try {
    const parts = accessToken.split('.');
    if (parts.length !== 3) {
      return Date.now() + DEFAULT_TOKEN_LIFETIME_MS;
    }
    const base64Url = parts[1];
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4);
    const payload = JSON.parse(atob(padded)) as Record<string, unknown>;

    if (typeof payload['exp'] === 'number') {
      // JWT `exp` is in seconds; convert to milliseconds for consistency
      // with `Date.now()` and the Zustand store's `tokenExpiresAt`
      return (payload['exp'] as number) * 1000;
    }
    return Date.now() + DEFAULT_TOKEN_LIFETIME_MS;
  } catch {
    return Date.now() + DEFAULT_TOKEN_LIFETIME_MS;
  }
}

/**
 * Maps a minimal `CognitoUser` (built from ID-token claims) to the
 * `AuthUser` shape expected by the Zustand auth store.
 *
 * Missing profile fields (`firstName`, `lastName`, `image`) default to
 * empty strings until the full user profile is fetched via `useAuthUser()`.
 *
 * Replaces the monolith's pattern of building `ClaimsPrincipal` from JWT
 * claims in `AuthService.Authenticate()` and attaching a full `ErpUser`
 * via `SecurityManager.GetUser(userId)` in `JwtMiddleware.cs`.
 *
 * @param cognitoUser - Minimal user from Cognito ID-token claims
 * @returns AuthUser shape for the Zustand store
 */
function mapCognitoUserToAuthUser(cognitoUser: CognitoUser): AuthUser {
  return {
    id: cognitoUser.id,
    email: cognitoUser.email,
    firstName: '',
    lastName: '',
    image: '',
    roles: cognitoUser.roles,
    isAdmin: cognitoUser.roles.includes('administrator'),
  };
}

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Session restoration hook — checks for an existing valid Cognito session
 * on application mount.
 *
 * Replaces:
 *  - Cookie auto-send: browser automatically included auth cookie on every
 *    request in the monolith; in the SPA we must explicitly check for tokens.
 *  - `JwtMiddleware.Invoke()`: extracted token from header/store, validated
 *    via `AuthService.GetValidSecurityTokenAsync`, attached user to context.
 *  - `SecurityContext.CurrentUser`: populated per-request via `ErpMiddleware`.
 *
 * Runs ONCE on mount (`staleTime: Infinity`, `retry: false`). If a valid
 * access token is found (possibly after an auto-refresh), the auth store
 * is transitioned to the authenticated state.
 *
 * @returns TanStack Query result with `CognitoUser | null` data
 *
 * @example
 * ```tsx
 * function AuthProvider({ children }) {
 *   const { isLoading, isSuccess, data } = useAuthSession();
 *   if (isLoading) return <LoadingSpinner />;
 *   return <>{children}</>;
 * }
 * ```
 */
export function useAuthSession() {
  return useQuery({
    queryKey: AUTH_QUERY_KEYS.session,

    queryFn: async (): Promise<CognitoUser | null> => {
      const store = useAuthStore.getState();

      try {
        // Attempt to obtain a valid access token.
        // `getAccessToken()` handles the full token lifecycle:
        //   1. Returns stored token if valid and not near expiry
        //   2. Proactively refreshes if within the 5-min buffer window
        //   3. Force-refreshes if fully expired (using stored refresh token)
        //   4. Returns null if no session exists at all
        //
        // This replaces JwtMiddleware.cs token extraction + validation.
        const accessToken = await getAccessToken();

        // Post-condition check: confirm token state is consistent.
        // `isAuthenticated()` is a synchronous guard that validates the
        // in-memory token is non-null and non-expired. This double-check
        // catches edge cases where `getAccessToken()` succeeded in refresh
        // but the stored token was immediately invalidated.
        if (!accessToken || !isAuthenticated()) {
          // No valid session — mark loading complete without authenticating
          store.setLoading(false);
          return null;
        }

        // Extract user profile from the cached ID token.
        // `getCurrentUser()` returns the `CognitoUser` built during login
        // (via Cognito `GetUserCommand`) or from ID-token decode.
        // Replaces `AuthService.GetUser(ClaimsPrincipal)` which extracted
        // the NameIdentifier claim and loaded the full user from the DB.
        const cognitoUser = getCurrentUser();

        if (!cognitoUser) {
          // Tokens exist but user cannot be extracted — inconsistent state
          store.setLoading(false);
          return null;
        }

        // Compute token expiry from the access token's `exp` JWT claim
        const tokenExpiresAt = extractTokenExpiry(accessToken);

        // Transition auth store to the authenticated state.
        // `loginSuccess` atomically sets: isAuthenticated=true, isLoading=false,
        // currentUser=authUser, tokenExpiresAt, authError=null.
        const authUser = mapCognitoUserToAuthUser(cognitoUser);
        store.loginSuccess(authUser, tokenExpiresAt);

        return cognitoUser;
      } catch {
        // Session restoration failed — user needs to re-login.
        // `logoutSuccess` atomically clears all auth state.
        store.logoutSuccess();
        return null;
      }
    },

    // Session check is a one-time operation — never refetch automatically
    staleTime: Infinity,

    // Don't retry session checks — failure means re-login required
    retry: false,

    // Prevent automatic refetches — session state is managed by mutations
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
}

/**
 * Full user profile query — fetches the authenticated user's complete
 * profile from the Identity service API.
 *
 * Replaces:
 *  - `AuthService.GetUser(ClaimsPrincipal)` → `SecurityManager.GetUser(userId)`
 *    which loaded the full `ErpUser` including `Preferences`, `Roles`,
 *    `Image`, etc. from the database.
 *  - `BaseErpPageModel.CurrentUser` — lazy-loaded property that called
 *    `AuthService.GetUser(User)` on first access in Razor page models.
 *
 * Returns the full `ErpUser` model (more data than JWT claims: preferences,
 * profile image, full name, admin status, timestamps).
 *
 * Only enabled when the user is authenticated in the Zustand store — this
 * prevents unnecessary API calls when no session exists.
 *
 * @returns TanStack Query result with `ErpUser` data and `refetch` function
 *
 * @example
 * ```tsx
 * function UserProfile() {
 *   const { data: user, isLoading, refetch } = useAuthUser();
 *   if (isLoading) return <Skeleton />;
 *   return <span>{user?.firstName} {user?.lastName}</span>;
 * }
 * ```
 */
export function useAuthUser() {
  const isAuthed = useAuthStore((state) => state.isAuthenticated);

  return useQuery({
    queryKey: AUTH_QUERY_KEYS.user,

    queryFn: async (): Promise<ErpUser> => {
      // Calls GET /v1/identity/users/me through the centralized API client.
      // The client's request interceptor automatically attaches the Bearer
      // JWT token and X-Correlation-ID header.
      const response = await get<ErpUser>('/identity/users/me');

      if (!response.success || !response.object) {
        throw new Error(
          response.message || 'Failed to fetch user profile',
        );
      }

      return response.object;
    },

    // Only fetch when authenticated — prevents 401 errors before login
    enabled: isAuthed,

    // Revalidate every 5 minutes to pick up profile changes
    staleTime: USER_PROFILE_STALE_TIME_MS,

    // Single retry for transient network failures
    retry: 1,
  });
}

// ---------------------------------------------------------------------------
// Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Login mutation — authenticates the user via Cognito USER_PASSWORD_AUTH.
 *
 * Replaces:
 *  - `AuthService.Authenticate(email, password)` — validated credentials,
 *    built `ClaimsIdentity`, signed cookie via `HttpContext.SignInAsync`.
 *  - `AuthService.GetTokenAsync(email, password)` — validated credentials,
 *    built JWT with `BuildTokenAsync` (1440-min expiry, 120-min refresh).
 *  - `login.cshtml.cs OnPost` — called Authenticate, redirected on success.
 *
 * Flow:
 *  1. `onMutate`: sets loading + clears error in auth store
 *  2. `mutationFn`: calls `api/auth.login()` → Cognito InitiateAuth
 *  3. `onSuccess`: maps CognitoUser → AuthUser, calls `loginSuccess()`,
 *     invalidates session + user queries for fresh data
 *  4. `onError`: stores error message in auth store + stops loading
 *
 * @returns TanStack Query mutation result with `AuthResult` data
 *
 * @example
 * ```tsx
 * function LoginForm() {
 *   const { mutate, isPending, isError, error } = useLogin();
 *   const handleSubmit = (e: FormEvent) => {
 *     e.preventDefault();
 *     mutate({ email, password });
 *   };
 * }
 * ```
 */
export function useLogin() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (variables: LoginRequest): Promise<AuthResult> => {
      return login(variables.email, variables.password);
    },

    onMutate: () => {
      // Set loading state and clear previous errors before the auth attempt.
      // Uses getState() for non-reactive access from mutation callbacks.
      const store = useAuthStore.getState();
      store.setLoading(true);
      store.setAuthError(null);
    },

    onSuccess: (data: AuthResult) => {
      const store = useAuthStore.getState();

      // Map the minimal CognitoUser (id, email, roles) from the login
      // response to the full AuthUser shape expected by the store.
      // Missing fields (firstName, lastName, image) default to empty
      // strings until useAuthUser() fetches the full profile.
      const authUser = mapCognitoUserToAuthUser(data.user);

      // Compute token expiry from the returned access token's JWT `exp` claim
      const tokenExpiresAt = extractTokenExpiry(data.accessToken);

      // Atomically transition the store to the authenticated state.
      // Sets: isAuthenticated=true, isLoading=false, currentUser, tokenExpiresAt
      store.loginSuccess(authUser, tokenExpiresAt);

      // Invalidate session and user queries so they refetch with new credentials.
      // The session query will see the new token; the user query will fetch
      // the full ErpUser profile from GET /v1/identity/users/me.
      void queryClient.invalidateQueries({ queryKey: AUTH_QUERY_KEYS.session });
      void queryClient.invalidateQueries({ queryKey: AUTH_QUERY_KEYS.user });
    },

    onError: (error: Error) => {
      const store = useAuthStore.getState();

      // Dual error reporting: error is both stored in the Zustand auth store
      // (for components that read authError) AND returned from the mutation
      // result (for components that read mutation.error).
      store.setAuthError(error.message || 'Login failed');
      store.setLoading(false);
    },
  });
}

/**
 * Logout mutation — signs out the user via Cognito GlobalSignOut and
 * clears all client-side state.
 *
 * Replaces:
 *  - `AuthService.Logout()` → `HttpContext.SignOutAsync(CookieAuthScheme)`
 *  - `logout.cshtml.cs OnGet/OnPost` → called Logout, redirected to "/"
 *
 * CRITICAL security (AAP §0.8.3): After logout, `queryClient.clear()` wipes
 * ALL TanStack Query cache entries to prevent stale data exposure to the
 * next user on a shared device/browser.
 *
 * @returns TanStack Query mutation result
 *
 * @example
 * ```tsx
 * function LogoutButton() {
 *   const { mutate: doLogout, isPending } = useLogout();
 *   return <button onClick={() => doLogout()} disabled={isPending}>Logout</button>;
 * }
 * ```
 */
export function useLogout() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (): Promise<void> => {
      return logout();
    },

    onSuccess: () => {
      const store = useAuthStore.getState();

      // Atomically transition the store to the unauthenticated state.
      // Clears: isAuthenticated=false, currentUser=null, tokenExpiresAt=null
      store.logoutSuccess();

      // CRITICAL: Clear ALL cached query data to prevent data leak to the
      // next user session (AAP §0.8.3 — no secrets in bundle).
      // This includes entity data, record lists, user preferences, etc.
      queryClient.clear();
    },

    onError: () => {
      // Even on logout error (e.g. network failure), clear local state
      // for security. The server-side token may still be valid, but the
      // client must not retain access.
      const store = useAuthStore.getState();
      store.logoutSuccess();
      queryClient.clear();
    },
  });
}

/**
 * Token refresh mutation — refreshes the access token using the stored
 * Cognito refresh token via REFRESH_TOKEN_AUTH flow.
 *
 * Replaces:
 *  - `AuthService.GetNewTokenAsync(tokenString)` — validated existing JWT,
 *    extracted `NameIdentifier` claim, verified user was enabled, built a
 *    new JWT with `BuildTokenAsync`.
 *
 * Called in two contexts:
 *  1. Automatically by the API client interceptor (`client.ts`) on 401
 *     responses — transparent retry with new token.
 *  2. Manually by components or timers that detect near-expiry tokens.
 *
 * On refresh failure (expired/revoked refresh token), the auth store
 * transitions to the logged-out state, forcing re-login.
 *
 * @returns TanStack Query mutation result with `string | null` (new token)
 *
 * @example
 * ```tsx
 * const { mutateAsync: refresh } = useRefreshToken();
 * const newToken = await refresh();
 * ```
 */
export function useRefreshToken() {
  return useMutation({
    mutationFn: async (): Promise<string | null> => {
      return refreshAccessToken();
    },

    onSuccess: (newAccessToken: string | null) => {
      const store = useAuthStore.getState();

      if (newAccessToken) {
        // Compute the new expiry timestamp from the refreshed access token
        const newExpiry = extractTokenExpiry(newAccessToken);
        store.setTokenExpiry(newExpiry);
      } else {
        // Null token means the refresh token itself was expired/revoked.
        // Mirrors `AuthService.GetNewTokenAsync` returning null when
        // validation fails → session is definitively over.
        store.logoutSuccess();
      }
    },

    onError: () => {
      // Refresh failure = session expired. Force re-login.
      // Mirrors `ErpMiddleware.cs` which called `SignOutAsync` when the
      // authenticated user could not be resolved after JWT validation failed.
      const store = useAuthStore.getState();
      store.logoutSuccess();
    },
  });
}

/**
 * Request payload for the change-password mutation.
 */
interface ChangePasswordRequest {
  /** User's current password for verification */
  currentPassword: string;
  /** New password to set */
  newPassword: string;
}

/**
 * Change password mutation — updates the authenticated user's password
 * via the Identity service API.
 *
 * Replaces the monolith's `SecurityManager` user-update operations for
 * password changes, which validated the old password hash and computed
 * a new `CryptoUtility.ComputeOddMD5Hash`.
 *
 * Calls `POST /v1/identity/users/me/change-password` through the
 * centralized API client, which attaches the Bearer JWT token.
 *
 * Auth tokens remain valid after a successful password change — no cache
 * invalidation or session state update is needed (Cognito does not revoke
 * existing tokens on password change unless explicitly configured).
 *
 * @returns TanStack Query mutation result
 *
 * @example
 * ```tsx
 * function PasswordForm() {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useChangePassword();
 *   const handleSubmit = () => {
 *     mutate({ currentPassword: 'old', newPassword: 'new' });
 *   };
 * }
 * ```
 */
export function useChangePassword() {
  return useMutation({
    mutationFn: async (variables: ChangePasswordRequest): Promise<void> => {
      const response = await post('/identity/users/me/change-password', {
        currentPassword: variables.currentPassword,
        newPassword: variables.newPassword,
      });

      if (!response.success) {
        throw new Error(
          response.message || 'Failed to change password',
        );
      }
    },
  });
}
