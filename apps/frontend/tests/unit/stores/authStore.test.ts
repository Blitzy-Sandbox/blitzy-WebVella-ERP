/**
 * Vitest Unit Tests — `authStore` (Zustand 5 Authentication Store)
 *
 * Comprehensive test suite for the authentication Zustand store that replaces
 * the monolith's server-side authentication state management:
 *
 *  - `SecurityContext.cs`   — AsyncLocal user scope, `CurrentUser`, `IsUserInRole`,
 *                             `HasMetaPermission`, `HasEntityPermission`
 *  - `BaseErpPageModel.cs`  — Lazy `CurrentUser` property, role-based access gating
 *  - `AuthService.cs`       — Cookie/JWT authentication state (Authenticate, Logout)
 *  - `Definitions.cs`       — `SystemIds` well-known role/user GUIDs
 *
 * All tests use the `useAuthStore.getState()` / `.setState()` pattern for
 * direct state manipulation — no React rendering is required.
 *
 * @see apps/frontend/src/stores/authStore.ts
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { useAuthStore } from '../../../src/stores/authStore';
import type { AuthUser } from '../../../src/stores/authStore';

// ---------------------------------------------------------------------------
// Test Data Fixtures
// ---------------------------------------------------------------------------

/**
 * Admin user fixture — maps to the monolith's first user (`SystemIds.FirstUserId`)
 * with `SystemIds.AdministratorRoleId`.
 */
const mockAdminUser: AuthUser = {
  id: 'eabd66fd-8de1-4d79-9674-447ee89921c2', // SystemIds.FirstUserId
  email: 'admin@webvella.com',
  firstName: 'Admin',
  lastName: 'User',
  image: '/img/admin.png',
  roles: ['administrator'], // Maps to SystemIds.AdministratorRoleId
  isAdmin: true,
};

/**
 * Regular user fixture — maps to a standard user with
 * `SystemIds.RegularRoleId`.
 */
const mockRegularUser: AuthUser = {
  id: 'b2e6c789-1234-5678-abcd-ef0123456789',
  email: 'user@webvella.com',
  firstName: 'Regular',
  lastName: 'User',
  image: '',
  roles: ['regular'], // Maps to SystemIds.RegularRoleId
  isAdmin: false,
};

/**
 * Guest user fixture — maps to the anonymous/guest role with
 * `SystemIds.GuestRoleId`.
 */
const mockGuestUser: AuthUser = {
  id: '00000000-0000-0000-0000-000000000000',
  email: 'guest@webvella.com',
  firstName: 'Guest',
  lastName: 'User',
  image: '',
  roles: ['guest'], // Maps to SystemIds.GuestRoleId
  isAdmin: false,
};

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

describe('authStore', () => {
  /**
   * CRITICAL: Zustand stores are singletons. To prevent cross-test state
   * leakage, reset the store to its default values before every test.
   *
   * The default state mirrors `defaultAuthState` in the store:
   *   isAuthenticated: false
   *   isLoading:       true    (matches initial boot state)
   *   currentUser:     null
   *   tokenExpiresAt:  null
   *   authError:       null
   */
  beforeEach(() => {
    useAuthStore.setState({
      isAuthenticated: false,
      isLoading: true,
      currentUser: null,
      tokenExpiresAt: null,
      authError: null,
    });
  });

  // ─── loginSuccess ──────────────────────────────────────────────────────

  describe('loginSuccess', () => {
    it('sets user, isAuthenticated=true, clears error and loading', () => {
      const tokenExpiry = Date.now() + 3600000; // 1 hour from now

      useAuthStore.getState().loginSuccess(mockAdminUser, tokenExpiry);

      const state = useAuthStore.getState();
      expect(state.currentUser).toEqual(mockAdminUser);
      expect(state.isAuthenticated).toBe(true);
      expect(state.isLoading).toBe(false);
      expect(state.authError).toBeNull();
      expect(state.tokenExpiresAt).toBe(tokenExpiry);
    });

    it('replaces previous user on re-login', () => {
      const tokenExpiry = Date.now() + 3600000;

      // First login as regular user
      useAuthStore.getState().loginSuccess(mockRegularUser, tokenExpiry);
      expect(useAuthStore.getState().currentUser).toEqual(mockRegularUser);

      // Re-login as admin user
      useAuthStore.getState().loginSuccess(mockAdminUser, tokenExpiry);
      expect(useAuthStore.getState().currentUser).toEqual(mockAdminUser);
      expect(useAuthStore.getState().currentUser?.email).toBe('admin@webvella.com');
    });

    it('stores tokenExpiresAt metadata with exact timestamp', () => {
      const specificTimestamp = 1700000000000; // Specific unix timestamp

      useAuthStore.getState().loginSuccess(mockAdminUser, specificTimestamp);

      expect(useAuthStore.getState().tokenExpiresAt).toBe(1700000000000);
    });
  });

  // ─── logoutSuccess ─────────────────────────────────────────────────────

  describe('logoutSuccess', () => {
    it('clears user, sets isAuthenticated=false, clears all session data', () => {
      // Set up: login first
      useAuthStore.getState().loginSuccess(mockAdminUser, Date.now() + 3600000);
      expect(useAuthStore.getState().isAuthenticated).toBe(true);

      // Act: logout
      useAuthStore.getState().logoutSuccess();

      // Assert: all session state cleared
      const state = useAuthStore.getState();
      expect(state.currentUser).toBeNull();
      expect(state.isAuthenticated).toBe(false);
      expect(state.isLoading).toBe(false);
      expect(state.tokenExpiresAt).toBeNull();
      expect(state.authError).toBeNull();
    });

    it('is safe to call when already logged out', () => {
      // Default state: not logged in
      expect(useAuthStore.getState().isAuthenticated).toBe(false);

      // Act: logout without prior login — should not throw
      expect(() => {
        useAuthStore.getState().logoutSuccess();
      }).not.toThrow();

      // Assert: state remains clean
      const state = useAuthStore.getState();
      expect(state.currentUser).toBeNull();
      expect(state.isAuthenticated).toBe(false);
      expect(state.tokenExpiresAt).toBeNull();
      expect(state.authError).toBeNull();
    });
  });

  // ─── isAdmin ───────────────────────────────────────────────────────────

  describe('isAdmin', () => {
    it('returns true for admin user', () => {
      useAuthStore.getState().loginSuccess(mockAdminUser, Date.now() + 3600000);

      expect(useAuthStore.getState().isAdmin()).toBe(true);
    });

    it('returns false for regular user', () => {
      useAuthStore.getState().loginSuccess(mockRegularUser, Date.now() + 3600000);

      expect(useAuthStore.getState().isAdmin()).toBe(false);
    });

    it('returns false when no user logged in', () => {
      // Default state: no user
      expect(useAuthStore.getState().isAdmin()).toBe(false);
    });
  });

  // ─── isUserInRole ──────────────────────────────────────────────────────

  describe('isUserInRole', () => {
    it('returns true for matching role', () => {
      useAuthStore.getState().loginSuccess(mockAdminUser, Date.now() + 3600000);

      expect(useAuthStore.getState().isUserInRole('administrator')).toBe(true);
    });

    it('returns false for non-matching role', () => {
      useAuthStore.getState().loginSuccess(mockRegularUser, Date.now() + 3600000);

      expect(useAuthStore.getState().isUserInRole('administrator')).toBe(false);
    });

    it('returns false when no user logged in', () => {
      expect(useAuthStore.getState().isUserInRole('administrator')).toBe(false);
    });
  });

  // ─── hasAnyRole ────────────────────────────────────────────────────────

  describe('hasAnyRole', () => {
    it('returns true when user has any of the specified roles', () => {
      useAuthStore.getState().loginSuccess(mockAdminUser, Date.now() + 3600000);

      expect(useAuthStore.getState().hasAnyRole(['administrator', 'regular'])).toBe(true);
    });

    it('returns false when user has none of the specified roles', () => {
      useAuthStore.getState().loginSuccess(mockRegularUser, Date.now() + 3600000);

      expect(useAuthStore.getState().hasAnyRole(['administrator', 'superadmin'])).toBe(false);
    });

    it('returns false when no user logged in', () => {
      expect(useAuthStore.getState().hasAnyRole(['administrator'])).toBe(false);
    });
  });

  // ─── Individual Setters ────────────────────────────────────────────────

  describe('setLoading', () => {
    it('updates loading state', () => {
      // Default is true
      expect(useAuthStore.getState().isLoading).toBe(true);

      useAuthStore.getState().setLoading(false);
      expect(useAuthStore.getState().isLoading).toBe(false);

      useAuthStore.getState().setLoading(true);
      expect(useAuthStore.getState().isLoading).toBe(true);
    });
  });

  describe('setAuthError', () => {
    it('sets error message', () => {
      useAuthStore.getState().setAuthError('Invalid credentials');

      expect(useAuthStore.getState().authError).toBe('Invalid credentials');
    });

    it('clears error with null', () => {
      // First set an error
      useAuthStore.getState().setAuthError('Session expired');
      expect(useAuthStore.getState().authError).toBe('Session expired');

      // Clear it
      useAuthStore.getState().setAuthError(null);
      expect(useAuthStore.getState().authError).toBeNull();
    });
  });

  // ─── resetAuthState ────────────────────────────────────────────────────

  describe('resetAuthState', () => {
    it('restores default state after login', () => {
      // Set up: login and modify various state
      useAuthStore.getState().loginSuccess(mockAdminUser, Date.now() + 3600000);
      expect(useAuthStore.getState().isAuthenticated).toBe(true);

      // Act: reset
      useAuthStore.getState().resetAuthState();

      // Assert: all state properties are back to defaults
      const state = useAuthStore.getState();
      expect(state.isAuthenticated).toBe(false);
      expect(state.isLoading).toBe(true); // Default is true (loading on boot)
      expect(state.currentUser).toBeNull();
      expect(state.tokenExpiresAt).toBeNull();
      expect(state.authError).toBeNull();
    });
  });

  // ─── State Isolation ───────────────────────────────────────────────────

  describe('state isolation', () => {
    it('store state does not leak between tests (first: login)', () => {
      // This test logs in — the next test should start with clean state
      useAuthStore.getState().loginSuccess(mockAdminUser, Date.now() + 3600000);
      expect(useAuthStore.getState().isAuthenticated).toBe(true);
      expect(useAuthStore.getState().currentUser).toEqual(mockAdminUser);
    });

    it('store state does not leak between tests (second: verify clean state)', () => {
      // beforeEach should have reset the store — verify clean state
      const state = useAuthStore.getState();
      expect(state.isAuthenticated).toBe(false);
      expect(state.isLoading).toBe(true);
      expect(state.currentUser).toBeNull();
      expect(state.tokenExpiresAt).toBeNull();
      expect(state.authError).toBeNull();
    });
  });
});
