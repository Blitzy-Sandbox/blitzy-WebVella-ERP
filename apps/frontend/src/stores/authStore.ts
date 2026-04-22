/**
 * Authentication Zustand Store — `apps/frontend/src/stores/authStore.ts`
 *
 * Zustand 5 store for authentication state management in the WebVella ERP
 * React SPA. This is the most foundational client-side store — depended on
 * by `App.tsx`, `ProtectedRoute`, and every component that checks auth state.
 *
 * Replaces:
 *  - `SecurityContext.cs`        — AsyncLocal-based user scope (CurrentUser,
 *                                  IsUserInRole, HasMetaPermission)
 *  - `BaseErpPageModel.cs`      — Lazy-loaded CurrentUser property and
 *                                  role-based access gating (App.Access ∩ userRoles)
 *  - `AuthService.cs`           — Cookie/JWT auth state tracking (Authenticate,
 *                                  Logout, GetUser, token expiry constants)
 *  - `Definitions.cs` SystemIds — Well-known role/user GUIDs mapped to names
 *
 * Security constraints:
 *  - No JWT tokens are stored in this store — tokens live in the `api/auth.ts`
 *    module closure (AAP §0.8.3: no secrets in bundle).
 *  - No localStorage/sessionStorage persistence — state is ephemeral and
 *    re-hydrated from the Cognito session on each page load.
 *  - `tokenExpiresAt` is metadata-only (unix timestamp) for UI decisions;
 *    actual token handling is in `api/auth.ts`.
 */

import { create } from 'zustand';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Authenticated user profile. Mirrors the essential fields of the monolith's
 * `ErpUser` model, with Cognito-specific attribute mapping.
 *
 * Source mapping:
 *  - `id`        → `ErpUser.Id` (Guid serialised as string / Cognito `sub`)
 *  - `email`     → `ErpUser.Email`
 *  - `firstName` → `ErpUser.FirstName`
 *  - `lastName`  → `ErpUser.LastName`
 *  - `image`     → `ErpUser.Image`
 *  - `roles`     → `ErpUser.Roles.Select(r => r.Name)` (name-based, not GUID)
 *  - `isAdmin`   → Derived: `roles` includes `'administrator'`
 *                   (replaces `SecurityContext.HasMetaPermission()` which
 *                    checks for `SystemIds.AdministratorRoleId`)
 */
export interface AuthUser {
  /** Cognito `sub` attribute or legacy ErpUser.Id (GUID string). */
  id: string;

  /** User email address — used as the primary login identifier. */
  email: string;

  /** User first name. */
  firstName: string;

  /** User last name. */
  lastName: string;

  /** URL to the user's profile image. Empty string when not set. */
  image: string;

  /**
   * Role names the user belongs to.
   * In the monolith these were `ErpRole` objects keyed by GUID; in the React
   * SPA we use name strings (e.g. `'administrator'`, `'regular'`, `'guest'`).
   */
  roles: string[];

  /**
   * Pre-computed admin flag — `true` when `roles` contains `'administrator'`.
   * Replaces `SecurityContext.HasMetaPermission()` which checks
   * `user.Roles.Any(x => x.Id == SystemIds.AdministratorRoleId)`.
   */
  isAdmin: boolean;
}

/**
 * Complete auth store shape — state properties **and** actions.
 *
 * Design notes:
 *  - State is kept minimal and UI-focused.
 *  - All API interactions (login, logout, token refresh) live in `api/auth.ts`.
 *  - Role-checking utilities (`isUserInRole`, `hasAnyRole`, `isAdmin`) use
 *    `get()` to access current state — standard Zustand pattern for derived
 *    actions.
 */
export interface AuthState {
  // ── State properties ────────────────────────────────────────────────────

  /**
   * `true` after a successful login; `false` on logout or session expiry.
   * Drives `ProtectedRoute` rendering decisions.
   */
  isAuthenticated: boolean;

  /**
   * `true` while the app is checking an existing Cognito session on mount,
   * or during an active login/logout/refresh operation.
   *
   * Defaults to `true` so the app renders a loading indicator instead of
   * flashing the login page while the initial session check completes
   * (replaces the monolith's automatic cookie-based persistent auth).
   */
  isLoading: boolean;

  /**
   * Profile of the currently authenticated user, or `null` when logged out.
   * Replaces `BaseErpPageModel.CurrentUser` and `SecurityContext.CurrentUser`.
   */
  currentUser: AuthUser | null;

  /**
   * Unix timestamp (ms) when the current access token expires.
   * Used for UI hints only — actual token lifecycle is managed in
   * `api/auth.ts`.  `null` when no active session exists.
   */
  tokenExpiresAt: number | null;

  /**
   * Human-readable authentication error message, or `null` when there is no
   * error. Cleared automatically on `loginSuccess`.
   */
  authError: string | null;

  // ── Setter actions ──────────────────────────────────────────────────────

  /** Replace the current user profile (or set to `null` on logout). */
  setUser: (user: AuthUser | null) => void;

  /** Explicitly flip the authenticated flag. */
  setAuthenticated: (authenticated: boolean) => void;

  /** Toggle the global loading indicator for auth operations. */
  setLoading: (loading: boolean) => void;

  /** Update token-expiry metadata after a token refresh. */
  setTokenExpiry: (expiresAt: number | null) => void;

  /** Set (or clear) an authentication error message. */
  setAuthError: (error: string | null) => void;

  // ── Composite actions ───────────────────────────────────────────────────

  /**
   * Atomically transition to the "logged-in" state.
   *
   * Called by the login page after `api/auth.login()` resolves successfully.
   * Clears any prior error, sets the user, marks authenticated, and records
   * the token expiry timestamp.
   *
   * Replaces the monolith's `AuthService.Authenticate()` post-success path
   * (cookie sign-in + claims principal creation).
   */
  loginSuccess: (user: AuthUser, tokenExpiresAt: number) => void;

  /**
   * Atomically transition to the "logged-out" state.
   *
   * Called after `api/auth.logout()` completes, or when a token refresh
   * fails (401 from Cognito). Clears all auth state.
   *
   * Replaces `AuthService.Logout()` (cookie sign-out).
   */
  logoutSuccess: () => void;

  // ── Role-checking utilities ─────────────────────────────────────────────

  /**
   * Check whether the current user belongs to the given role (by name).
   *
   * Replaces `SecurityContext.IsUserInRole(Guid[])` — the monolith matched
   * by role GUID; in the React SPA we match by role name string.
   *
   * @param roleName - Case-sensitive role name (e.g. `'administrator'`).
   * @returns `true` if the user's `roles` array includes `roleName`.
   */
  isUserInRole: (roleName: string) => boolean;

  /**
   * Check whether the current user has **at least one** of the given roles.
   *
   * Replaces the role-intersection logic in `BaseErpPageModel.Init()`
   * (lines 151-167) which computed
   * `ErpRequestContext.App.Access.Intersect(currentUserRoles)` and
   * redirected to `/error?401` when the intersection was empty.
   *
   * @param roleNames - Array of role name strings to check.
   * @returns `true` if any role in `roleNames` appears in the user's roles.
   */
  hasAnyRole: (roleNames: string[]) => boolean;

  /**
   * Convenience check — equivalent to `isUserInRole('administrator')`.
   *
   * Replaces `SecurityContext.HasMetaPermission()` which returned `true`
   * only when the user had `SystemIds.AdministratorRoleId`.
   *
   * @returns `true` if the current user is an administrator.
   */
  isAdmin: () => boolean;

  // ── Reset ───────────────────────────────────────────────────────────────

  /**
   * Hard-reset the store to its initial (unauthenticated, loading) state.
   * Useful during error-recovery flows or test teardown.
   */
  resetAuthState: () => void;
}

// ---------------------------------------------------------------------------
// Default state
// ---------------------------------------------------------------------------

/**
 * Initial auth state values.
 *
 * `isLoading` starts as `true` so the application shell renders a loading
 * indicator while it checks for an existing Cognito session on mount —
 * preventing a flash of the login page for users who already have a valid
 * session. This replaces the monolith's cookie-based persistent auth where
 * the browser automatically sends the authentication cookie on every request.
 */
const defaultAuthState: Pick<
  AuthState,
  'isAuthenticated' | 'isLoading' | 'currentUser' | 'tokenExpiresAt' | 'authError'
> = {
  isAuthenticated: false,
  isLoading: true,
  currentUser: null,
  tokenExpiresAt: null,
  authError: null,
};

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

/**
 * Primary authentication store hook.
 *
 * Usage:
 * ```tsx
 * const { isAuthenticated, currentUser, loginSuccess } = useAuthStore();
 * ```
 *
 * Or with a selector for minimal re-renders:
 * ```tsx
 * const isAuthenticated = useAuthStore(s => s.isAuthenticated);
 * ```
 */
export const useAuthStore = create<AuthState>()((set, get) => ({
  // ── State ──────────────────────────────────────────────────────────────
  ...defaultAuthState,

  // ── Setter actions ─────────────────────────────────────────────────────

  setUser: (user: AuthUser | null): void => {
    set({ currentUser: user });
  },

  setAuthenticated: (authenticated: boolean): void => {
    set({ isAuthenticated: authenticated });
  },

  setLoading: (loading: boolean): void => {
    set({ isLoading: loading });
  },

  setTokenExpiry: (expiresAt: number | null): void => {
    set({ tokenExpiresAt: expiresAt });
  },

  setAuthError: (error: string | null): void => {
    set({ authError: error });
  },

  // ── Composite actions ──────────────────────────────────────────────────

  loginSuccess: (user: AuthUser, tokenExpiresAt: number): void => {
    set({
      isAuthenticated: true,
      isLoading: false,
      currentUser: user,
      tokenExpiresAt,
      authError: null,
    });
  },

  logoutSuccess: (): void => {
    set({
      isAuthenticated: false,
      isLoading: false,
      currentUser: null,
      tokenExpiresAt: null,
      authError: null,
    });
  },

  // ── Role-checking utilities ────────────────────────────────────────────

  isUserInRole: (roleName: string): boolean => {
    const user = get().currentUser;
    if (!user) {
      return false;
    }
    return user.roles.includes(roleName);
  },

  hasAnyRole: (roleNames: string[]): boolean => {
    const user = get().currentUser;
    if (!user) {
      return false;
    }
    return roleNames.some((role) => user.roles.includes(role));
  },

  isAdmin: (): boolean => {
    const user = get().currentUser;
    return user?.isAdmin ?? false;
  },

  // ── Reset ──────────────────────────────────────────────────────────────

  resetAuthState: (): void => {
    set({ ...defaultAuthState });
  },
}));

// ---------------------------------------------------------------------------
// Typed selector hooks
// ---------------------------------------------------------------------------
// These thin wrappers select a single slice of state and cause the consuming
// component to re-render only when that specific slice changes.  They are the
// recommended way to consume the store in leaf components.

/** Select `isAuthenticated` from the auth store. */
export const useIsAuthenticated = (): boolean =>
  useAuthStore((state) => state.isAuthenticated);

/** Select `currentUser` from the auth store. */
export const useCurrentUser = (): AuthUser | null =>
  useAuthStore((state) => state.currentUser);

/** Select `isLoading` from the auth store. */
export const useIsAuthLoading = (): boolean =>
  useAuthStore((state) => state.isLoading);

/** Select `authError` from the auth store. */
export const useAuthError = (): string | null =>
  useAuthStore((state) => state.authError);

/**
 * Select whether the current user is an administrator.
 *
 * Returns `false` when no user is authenticated.
 */
export const useIsAdmin = (): boolean =>
  useAuthStore((state) => state.currentUser?.isAdmin ?? false);
