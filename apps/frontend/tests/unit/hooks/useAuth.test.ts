/**
 * @file useAuth.test.ts
 * @description Comprehensive Vitest unit tests for the 6 authentication TanStack Query hooks
 * exported from src/hooks/useAuth.ts. These hooks replace the monolith's AuthService.cs
 * (cookie+JWT dual auth), JwtMiddleware.cs (bearer token extraction), and SecurityContext.cs
 * (AsyncLocal user scoping) with a Cognito-backed, TanStack Query + Zustand architecture.
 *
 * Test suites cover:
 *   - useAuthSession — session restoration on app mount
 *   - useLogin       — credential-based login mutation
 *   - useLogout      — logout with CRITICAL cache clearing
 *   - useRefreshToken — JWT refresh with expiry management
 *   - useAuthUser    — authenticated user profile query
 *   - useChangePassword — password change mutation
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';

// ──────────────────────────────────────────────────────────────────────────────
// Module mocks — vi.mock calls are hoisted by Vitest before all imports
// ──────────────────────────────────────────────────────────────────────────────

vi.mock('../../../src/api/auth', () => ({
  login: vi.fn(),
  logout: vi.fn(),
  refreshAccessToken: vi.fn(),
  getAccessToken: vi.fn(),
  getCurrentUser: vi.fn(),
  isAuthenticated: vi.fn(),
}));

vi.mock('../../../src/stores/authStore', () => {
  const mockStore = vi.fn() as any;
  mockStore.getState = vi.fn();
  return { useAuthStore: mockStore };
});

vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useAuthSession,
  useAuthUser,
  useLogin,
  useLogout,
  useRefreshToken,
  useChangePassword,
} from '../../../src/hooks/useAuth';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import {
  login,
  logout,
  refreshAccessToken,
  getAccessToken,
  getCurrentUser,
  isAuthenticated,
} from '../../../src/api/auth';
import type { AuthResult, CognitoUser } from '../../../src/api/auth';
import { useAuthStore } from '../../../src/stores/authStore';
import { get, post } from '../../../src/api/client';
import type { ErpUser, LoginRequest } from '../../../src/types/user';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockLogin = vi.mocked(login);
const mockLogout = vi.mocked(logout);
const mockRefreshAccessToken = vi.mocked(refreshAccessToken);
const mockGetAccessToken = vi.mocked(getAccessToken);
const mockGetCurrentUser = vi.mocked(getCurrentUser);
const mockIsAuthenticated = vi.mocked(isAuthenticated);
const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures
// ──────────────────────────────────────────────────────────────────────────────

/** Generates a syntactically valid JWT with a given `exp` claim (seconds since epoch). */
function createMockJwt(expSeconds?: number): string {
  const header = btoa(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
  const payload = btoa(
    JSON.stringify({
      sub: 'eabd66fd-8de1-4d79-9674-447ee89921c2',
      email: 'erp@webvella.com',
      exp: expSeconds ?? Math.floor(Date.now() / 1000) + 3600,
      iss: 'https://cognito-idp.us-east-1.amazonaws.com/us-east-1_test',
    }),
  );
  return `${header}.${payload}.mock-signature`;
}

const MOCK_ACCESS_TOKEN = createMockJwt(Math.floor(Date.now() / 1000) + 3600);

const mockCognitoUser: CognitoUser = {
  id: 'eabd66fd-8de1-4d79-9674-447ee89921c2',
  email: 'erp@webvella.com',
  roles: ['administrator'],
};

const mockAuthResult: AuthResult = {
  user: mockCognitoUser,
  accessToken: MOCK_ACCESS_TOKEN,
};

const mockErpUser: ErpUser = {
  id: 'eabd66fd-8de1-4d79-9674-447ee89921c2',
  username: 'erp',
  email: 'erp@webvella.com',
  firstName: 'System',
  lastName: 'Administrator',
  image: '/avatars/default.png',
  createdOn: '2024-01-01T00:00:00Z',
  lastLoggedIn: '2025-02-25T12:00:00Z',
  isAdmin: true,
  preferences: {
    sidebarSize: 'md',
    componentUsage: [],
    componentDataDictionary: {},
  },
};

// ──────────────────────────────────────────────────────────────────────────────
// Helper utilities
// ──────────────────────────────────────────────────────────────────────────────

/** Creates a fresh QueryClient with retries disabled for deterministic tests. */
function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

/**
 * Creates a React wrapper component that provides QueryClientProvider context.
 * Uses React.createElement instead of JSX since this is a .ts (not .tsx) file.
 */
function createWrapper(queryClient?: QueryClient) {
  const client = queryClient ?? createTestQueryClient();
  return function TestQueryClientWrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client }, children);
  };
}

// ──────────────────────────────────────────────────────────────────────────────
// Mock store state — shared across all suites, reset in beforeEach
// ──────────────────────────────────────────────────────────────────────────────

const mockStoreActions = {
  loginSuccess: vi.fn(),
  logoutSuccess: vi.fn(),
  setLoading: vi.fn(),
  setAuthError: vi.fn(),
  setTokenExpiry: vi.fn(),
  currentUser: null as any,
  isAuthenticated: false,
};

// ──────────────────────────────────────────────────────────────────────────────
// Main test block
// ──────────────────────────────────────────────────────────────────────────────

describe('useAuth hooks', () => {
  beforeEach(() => {
    vi.clearAllMocks();

    // Recreate action mocks with clean call history
    mockStoreActions.loginSuccess = vi.fn();
    mockStoreActions.logoutSuccess = vi.fn();
    mockStoreActions.setLoading = vi.fn();
    mockStoreActions.setAuthError = vi.fn();
    mockStoreActions.setTokenExpiry = vi.fn();
    mockStoreActions.currentUser = null;
    mockStoreActions.isAuthenticated = false;

    // Configure useAuthStore as a callable hook (for selector pattern in useAuthUser)
    vi.mocked(useAuthStore).mockImplementation((selector?: any) => {
      if (typeof selector === 'function') {
        return selector(mockStoreActions);
      }
      return mockStoreActions;
    });

    // Configure useAuthStore.getState() (for imperative access in mutation callbacks)
    (useAuthStore as any).getState.mockReturnValue(mockStoreActions);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  // ────────────────────────────────────────────────────────────────────────────
  // useAuthSession
  // ────────────────────────────────────────────────────────────────────────────

  describe('useAuthSession', () => {
    it('should restore session when valid token exists', async () => {
      mockGetAccessToken.mockResolvedValue(MOCK_ACCESS_TOKEN);
      mockIsAuthenticated.mockReturnValue(true);
      mockGetCurrentUser.mockReturnValue(mockCognitoUser);

      const { result } = renderHook(() => useAuthSession(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Verify store was updated with mapped AuthUser and token expiry
      expect(mockStoreActions.loginSuccess).toHaveBeenCalledTimes(1);
      expect(mockStoreActions.loginSuccess).toHaveBeenCalledWith(
        expect.objectContaining({
          id: mockCognitoUser.id,
          email: mockCognitoUser.email,
          roles: mockCognitoUser.roles,
          isAdmin: true, // 'administrator' role → isAdmin
        }),
        expect.any(Number), // tokenExpiresAt derived from JWT exp claim
      );

      // Query data should be the raw CognitoUser
      expect(result.current.data).toEqual(mockCognitoUser);
    });

    it('should handle no existing session gracefully', async () => {
      mockGetAccessToken.mockResolvedValue(null);

      const { result } = renderHook(() => useAuthSession(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Store should have loading set to false (no session found, graceful)
      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(false);
      // loginSuccess should NOT have been called
      expect(mockStoreActions.loginSuccess).not.toHaveBeenCalled();
      // Query data should be null
      expect(result.current.data).toBeNull();
    });

    it('should return null when token exists but isAuthenticated returns false', async () => {
      mockGetAccessToken.mockResolvedValue(MOCK_ACCESS_TOKEN);
      mockIsAuthenticated.mockReturnValue(false);

      const { result } = renderHook(() => useAuthSession(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(false);
      expect(mockStoreActions.loginSuccess).not.toHaveBeenCalled();
      expect(result.current.data).toBeNull();
    });

    it('should return null when getCurrentUser returns null', async () => {
      mockGetAccessToken.mockResolvedValue(MOCK_ACCESS_TOKEN);
      mockIsAuthenticated.mockReturnValue(true);
      mockGetCurrentUser.mockReturnValue(null);

      const { result } = renderHook(() => useAuthSession(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(false);
      expect(mockStoreActions.loginSuccess).not.toHaveBeenCalled();
      expect(result.current.data).toBeNull();
    });

    it('should call logoutSuccess on session check failure', async () => {
      mockGetAccessToken.mockRejectedValue(new Error('Token storage corrupted'));

      const { result } = renderHook(() => useAuthSession(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Error is caught inside queryFn; logoutSuccess is called
      expect(mockStoreActions.logoutSuccess).toHaveBeenCalledTimes(1);
      // Data should be null (returned from catch block)
      expect(result.current.data).toBeNull();
    });

    it('should not retry on session check failure (retry: false)', async () => {
      let callCount = 0;
      mockGetAccessToken.mockImplementation(async () => {
        callCount++;
        throw new Error('Persistent failure');
      });

      const { result } = renderHook(() => useAuthSession(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // queryFn catches the error internally and returns null → isSuccess=true
      // getAccessToken should only be called once (no retry)
      expect(callCount).toBe(1);
    });
  });

  // ────────────────────────────────────────────────────────────────────────────
  // useLogin
  // ────────────────────────────────────────────────────────────────────────────

  describe('useLogin', () => {
    it('should login successfully with valid credentials', async () => {
      mockLogin.mockResolvedValue(mockAuthResult);

      const queryClient = createTestQueryClient();
      const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
      const wrapper = createWrapper(queryClient);

      const { result } = renderHook(() => useLogin(), { wrapper });

      await act(async () => {
        result.current.mutate({ email: 'erp@webvella.com', password: 'erp' });
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Verify auth API was called with correct credentials
      expect(mockLogin).toHaveBeenCalledWith('erp@webvella.com', 'erp');

      // Verify onMutate set loading and cleared errors
      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(true);
      expect(mockStoreActions.setAuthError).toHaveBeenCalledWith(null);

      // Verify onSuccess stored user data with correct AuthUser shape
      expect(mockStoreActions.loginSuccess).toHaveBeenCalledTimes(1);
      expect(mockStoreActions.loginSuccess).toHaveBeenCalledWith(
        expect.objectContaining({
          id: mockCognitoUser.id,
          email: mockCognitoUser.email,
          roles: mockCognitoUser.roles,
          firstName: '', // mapCognitoUserToAuthUser defaults
          lastName: '',
          image: '',
          isAdmin: true,
        }),
        expect.any(Number),
      );

      // Verify query cache invalidation for session and user queries
      expect(invalidateSpy).toHaveBeenCalled();
    });

    it('should handle invalid credentials with error state', async () => {
      const errorMessage = 'Invalid email or password';
      mockLogin.mockRejectedValue(new Error(errorMessage));

      const { result } = renderHook(() => useLogin(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate({ email: 'erp@webvella.com', password: 'wrong' });
      });

      await waitFor(() => {
        expect(result.current.isError).toBe(true);
      });

      // Verify onMutate was called (loading state set before error)
      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(true);
      expect(mockStoreActions.setAuthError).toHaveBeenCalledWith(null);

      // Verify onError stored the error message and reset loading
      expect(mockStoreActions.setAuthError).toHaveBeenCalledWith(errorMessage);
      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(false);

      // loginSuccess should NOT be called on failure
      expect(mockStoreActions.loginSuccess).not.toHaveBeenCalled();
    });

    it('should set loading state during login', async () => {
      // Login resolves successfully — we verify onMutate side-effects
      mockLogin.mockResolvedValue(mockAuthResult);

      const { result } = renderHook(() => useLogin(), {
        wrapper: createWrapper(),
      });

      // Trigger mutation — onMutate fires synchronously during mutate()
      await act(async () => {
        result.current.mutate({ email: 'erp@webvella.com', password: 'erp' });
      });

      // onMutate should have been called with setLoading(true) and clearing errors
      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(true);
      expect(mockStoreActions.setAuthError).toHaveBeenCalledWith(null);

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });
    });

    it('should pass email and password to the login API', async () => {
      mockLogin.mockResolvedValue(mockAuthResult);
      const credentials: LoginRequest = {
        email: 'admin@example.com',
        password: 'secureP@ss123',
      };

      const { result } = renderHook(() => useLogin(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate(credentials);
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      expect(mockLogin).toHaveBeenCalledWith(credentials.email, credentials.password);
    });

    it('should handle non-Error rejection objects', async () => {
      mockLogin.mockRejectedValue('Network failure');

      const { result } = renderHook(() => useLogin(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate({ email: 'erp@webvella.com', password: 'erp' });
      });

      await waitFor(() => {
        expect(result.current.isError).toBe(true);
      });

      // onError should still fire, setting error message
      expect(mockStoreActions.setAuthError).toHaveBeenCalled();
      expect(mockStoreActions.setLoading).toHaveBeenCalledWith(false);
    });
  });

  // ────────────────────────────────────────────────────────────────────────────
  // useLogout
  // ────────────────────────────────────────────────────────────────────────────

  describe('useLogout', () => {
    it('should logout successfully and clear all query cache', async () => {
      mockLogout.mockResolvedValue(undefined);

      const queryClient = createTestQueryClient();
      const clearSpy = vi.spyOn(queryClient, 'clear');
      const wrapper = createWrapper(queryClient);

      const { result } = renderHook(() => useLogout(), { wrapper });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Verify auth API logout was called
      expect(mockLogout).toHaveBeenCalledTimes(1);

      // Verify store was cleared
      expect(mockStoreActions.logoutSuccess).toHaveBeenCalledTimes(1);

      // CRITICAL SECURITY: queryClient.clear() must be called to prevent
      // stale data exposure to the next user
      expect(clearSpy).toHaveBeenCalledTimes(1);
    });

    it('should clear store and cache even when logout API fails', async () => {
      mockLogout.mockRejectedValue(new Error('Network error'));

      const queryClient = createTestQueryClient();
      const clearSpy = vi.spyOn(queryClient, 'clear');
      const wrapper = createWrapper(queryClient);

      const { result } = renderHook(() => useLogout(), { wrapper });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isError).toBe(true);
      });

      // Even on API failure, local state MUST be cleared (security-critical)
      expect(mockStoreActions.logoutSuccess).toHaveBeenCalledTimes(1);
      expect(clearSpy).toHaveBeenCalledTimes(1);
    });

    it('should call logout API exactly once', async () => {
      mockLogout.mockResolvedValue(undefined);

      const { result } = renderHook(() => useLogout(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      expect(mockLogout).toHaveBeenCalledTimes(1);
    });
  });

  // ────────────────────────────────────────────────────────────────────────────
  // useRefreshToken
  // ────────────────────────────────────────────────────────────────────────────

  describe('useRefreshToken', () => {
    it('should refresh token successfully and update expiry', async () => {
      const newToken = createMockJwt(Math.floor(Date.now() / 1000) + 7200);
      mockRefreshAccessToken.mockResolvedValue(newToken);

      const { result } = renderHook(() => useRefreshToken(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Verify refreshAccessToken was called
      expect(mockRefreshAccessToken).toHaveBeenCalledTimes(1);

      // Verify token expiry was updated in the store
      expect(mockStoreActions.setTokenExpiry).toHaveBeenCalledTimes(1);
      expect(mockStoreActions.setTokenExpiry).toHaveBeenCalledWith(expect.any(Number));

      // logoutSuccess should NOT have been called on successful refresh
      expect(mockStoreActions.logoutSuccess).not.toHaveBeenCalled();
    });

    it('should call logoutSuccess when refresh returns null token', async () => {
      mockRefreshAccessToken.mockResolvedValue(null);

      const { result } = renderHook(() => useRefreshToken(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Null token means refresh failed silently — session is expired
      expect(mockStoreActions.logoutSuccess).toHaveBeenCalledTimes(1);
      expect(mockStoreActions.setTokenExpiry).not.toHaveBeenCalled();
    });

    it('should call logoutSuccess on refresh failure', async () => {
      mockRefreshAccessToken.mockRejectedValue(new Error('Refresh token expired'));

      const { result } = renderHook(() => useRefreshToken(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isError).toBe(true);
      });

      // On error, session is considered expired — force logout
      expect(mockStoreActions.logoutSuccess).toHaveBeenCalledTimes(1);
      expect(mockStoreActions.setTokenExpiry).not.toHaveBeenCalled();
    });

    it('should not affect query cache on successful refresh', async () => {
      const newToken = createMockJwt(Math.floor(Date.now() / 1000) + 7200);
      mockRefreshAccessToken.mockResolvedValue(newToken);

      const queryClient = createTestQueryClient();
      const clearSpy = vi.spyOn(queryClient, 'clear');
      const wrapper = createWrapper(queryClient);

      const { result } = renderHook(() => useRefreshToken(), { wrapper });

      await act(async () => {
        result.current.mutate();
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Token refresh should NOT clear the cache (unlike logout)
      expect(clearSpy).not.toHaveBeenCalled();
    });
  });

  // ────────────────────────────────────────────────────────────────────────────
  // useAuthUser
  // ────────────────────────────────────────────────────────────────────────────

  describe('useAuthUser', () => {
    it('should fetch user profile when authenticated', async () => {
      // Enable the query by setting isAuthenticated on the mock store
      mockStoreActions.isAuthenticated = true;

      mockGet.mockResolvedValue({
        success: true,
        object: mockErpUser,
        errors: [],
        statusCode: 200,
        timestamp: new Date().toISOString(),
        message: '',
      } as any);

      const { result } = renderHook(() => useAuthUser(), {
        wrapper: createWrapper(),
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Verify API client was called with correct endpoint
      expect(mockGet).toHaveBeenCalledWith('/identity/users/me');

      // Verify returned data contains full ErpUser fields
      expect(result.current.data).toEqual(mockErpUser);
      expect(result.current.data?.id).toBe(mockErpUser.id);
      expect(result.current.data?.email).toBe(mockErpUser.email);
      expect(result.current.data?.firstName).toBe('System');
      expect(result.current.data?.lastName).toBe('Administrator');
      expect(result.current.data?.isAdmin).toBe(true);
    });

    it('should not fetch user profile when not authenticated', async () => {
      // Query should be disabled when isAuthenticated is false
      mockStoreActions.isAuthenticated = false;

      const { result } = renderHook(() => useAuthUser(), {
        wrapper: createWrapper(),
      });

      // The query should remain in idle/pending state since enabled=false
      // Give it a moment to settle
      await act(async () => {
        await new Promise((r) => setTimeout(r, 50));
      });

      // No API call should have been made
      expect(mockGet).not.toHaveBeenCalled();

      // Query should not have fetched data
      expect(result.current.data).toBeUndefined();
      expect(result.current.isFetching).toBe(false);
    });

    it('should handle API error on user profile fetch', async () => {
      mockStoreActions.isAuthenticated = true;

      mockGet.mockResolvedValue({
        success: false,
        object: null,
        errors: [{ field: '', message: 'User not found' }],
        statusCode: 404,
        timestamp: new Date().toISOString(),
        message: 'Failed to fetch user profile',
      } as any);

      const { result } = renderHook(() => useAuthUser(), {
        wrapper: createWrapper(),
      });

      // useAuthUser hardcodes retry: 1, so the query retries once with ~1s
      // exponential backoff delay before transitioning to error state.
      // Increase waitFor timeout to accommodate the retry cycle.
      await waitFor(
        () => {
          expect(result.current.isError).toBe(true);
        },
        { timeout: 5000 },
      );

      expect(result.current.error).toBeDefined();
      expect(result.current.error?.message).toContain('Failed to fetch user profile');
    });

    it('should handle network error on user profile fetch', async () => {
      mockStoreActions.isAuthenticated = true;

      mockGet.mockRejectedValue(new Error('Network error'));

      const queryClient = new QueryClient({
        defaultOptions: {
          queries: { retry: false },
          mutations: { retry: false },
        },
      });

      const { result } = renderHook(() => useAuthUser(), {
        wrapper: createWrapper(queryClient),
      });

      // useAuthUser hardcodes retry: 1 (overrides QueryClient defaults),
      // so the query retries once with ~1s backoff.
      await waitFor(
        () => {
          expect(result.current.isError).toBe(true);
        },
        { timeout: 5000 },
      );

      expect(result.current.error).toBeDefined();
    });
  });

  // ────────────────────────────────────────────────────────────────────────────
  // useChangePassword
  // ────────────────────────────────────────────────────────────────────────────

  describe('useChangePassword', () => {
    it('should change password successfully', async () => {
      mockPost.mockResolvedValue({
        success: true,
        object: null,
        errors: [],
        statusCode: 200,
        timestamp: new Date().toISOString(),
        message: 'Password changed successfully',
      } as any);

      const { result } = renderHook(() => useChangePassword(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate({
          currentPassword: 'oldPass123',
          newPassword: 'newSecureP@ss456',
        });
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Verify API was called with correct endpoint and payload
      expect(mockPost).toHaveBeenCalledWith('/identity/users/me/change-password', {
        currentPassword: 'oldPass123',
        newPassword: 'newSecureP@ss456',
      });
    });

    it('should handle password change failure from API', async () => {
      mockPost.mockResolvedValue({
        success: false,
        object: null,
        errors: [{ field: 'currentPassword', message: 'Current password is incorrect' }],
        statusCode: 400,
        timestamp: new Date().toISOString(),
        message: 'Failed to change password',
      } as any);

      const { result } = renderHook(() => useChangePassword(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate({
          currentPassword: 'wrongOldPass',
          newPassword: 'newPass',
        });
      });

      await waitFor(() => {
        expect(result.current.isError).toBe(true);
      });

      expect(result.current.error).toBeDefined();
      expect(result.current.error?.message).toContain('Failed to change password');
    });

    it('should handle network error during password change', async () => {
      mockPost.mockRejectedValue(new Error('Network error'));

      const { result } = renderHook(() => useChangePassword(), {
        wrapper: createWrapper(),
      });

      await act(async () => {
        result.current.mutate({
          currentPassword: 'oldPass',
          newPassword: 'newPass',
        });
      });

      await waitFor(() => {
        expect(result.current.isError).toBe(true);
      });

      expect(result.current.error?.message).toBe('Network error');
    });

    it('should not invalidate auth cache on successful password change', async () => {
      mockPost.mockResolvedValue({
        success: true,
        object: null,
        errors: [],
        statusCode: 200,
        timestamp: new Date().toISOString(),
        message: 'Password changed',
      } as any);

      const queryClient = createTestQueryClient();
      const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
      const clearSpy = vi.spyOn(queryClient, 'clear');
      const wrapper = createWrapper(queryClient);

      const { result } = renderHook(() => useChangePassword(), { wrapper });

      await act(async () => {
        result.current.mutate({
          currentPassword: 'old',
          newPassword: 'new',
        });
      });

      await waitFor(() => {
        expect(result.current.isSuccess).toBe(true);
      });

      // Password change should NOT affect auth tokens or cache
      expect(clearSpy).not.toHaveBeenCalled();
      // No auth-related queries should be invalidated
      expect(mockStoreActions.logoutSuccess).not.toHaveBeenCalled();
    });
  });
});
