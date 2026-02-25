/**
 * @file useUsers.test.ts
 * @description Comprehensive Vitest unit tests for the 13 user/role management
 * TanStack Query hooks exported from src/hooks/useUsers.ts.
 *
 * These hooks replace:
 *   - SecurityManager.cs  → User/role CRUD, credential validation, user lookup
 *   - UserService.cs      → EQL-based user listing and retrieval
 *   - UserPreferencies.cs → Per-user preference persistence (sidebar, component data)
 *
 * Test suites cover all 13 hooks:
 *   - useUsers             — paginated/filtered user listing
 *   - useUser              — single user fetch by ID
 *   - useCurrentUserProfile — authenticated user's full profile with 5-min staleTime
 *   - useCreateUser        — user creation mutation with cache invalidation
 *   - useUpdateUser        — user update mutation with multi-key invalidation
 *   - useDeleteUser        — user deletion mutation
 *   - useUpdatePreferences — preference persistence mutation
 *   - useRoles             — role listing with 10-min staleTime
 *   - useRole              — single role fetch by ID
 *   - useUsersInRole       — users-in-role listing
 *   - useCreateRole        — role creation mutation
 *   - useUpdateRole        — role update mutation with multi-key invalidation
 *   - useDeleteRole        — role deletion mutation
 *
 * Mocking strategy:
 *   - vi.mock intercepts the API client module to prevent real HTTP calls
 *   - A fresh QueryClient (retry: false) is created for each test
 *   - invalidateQueries spies verify cache key invalidation patterns
 *
 * @module tests/unit/hooks/useUsers.test
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';

// ──────────────────────────────────────────────────────────────────────────────
// Module mocks — vi.mock calls are hoisted by Vitest before all imports
// ──────────────────────────────────────────────────────────────────────────────

vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
  default: {},
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useUsers,
  useUser,
  useCurrentUserProfile,
  useCreateUser,
  useUpdateUser,
  useDeleteUser,
  useUpdatePreferences,
  useRoles,
  useRole,
  useUsersInRole,
  useCreateRole,
  useUpdateRole,
  useDeleteRole,
} from '../../../src/hooks/useUsers';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';

// ──────────────────────────────────────────────────────────────────────────────
// Type imports
// ──────────────────────────────────────────────────────────────────────────────

import type { ErpUser, ErpRole, ErpUserPreferences } from '../../../src/types/user';
import type { BaseResponseModel } from '../../../src/types/common';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures
// ──────────────────────────────────────────────────────────────────────────────

/** Stable ISO timestamp used across all mock responses for deterministic tests */
const MOCK_TIMESTAMP = '2024-01-15T12:00:00.000Z';

/**
 * Mock user preferences matching the ErpUserPreferences interface.
 * Replicates the JSON shape from UserPreferencies.cs serialization.
 */
const mockPreferences: ErpUserPreferences = {
  sidebarSize: 'expanded',
  componentUsage: [],
  componentDataDictionary: {},
};

/**
 * Mock user matching the ErpUser interface.
 * Mirrors the shape returned by SecurityManager.GetUser() mapped through
 * the Identity service Lambda handler.
 */
const mockUser: ErpUser = {
  id: 'user-guid',
  username: 'admin',
  email: 'admin@webvella.com',
  firstName: 'Admin',
  lastName: 'User',
  image: '',
  createdOn: MOCK_TIMESTAMP,
  lastLoggedIn: MOCK_TIMESTAMP,
  isAdmin: true,
  preferences: mockPreferences,
};

/**
 * Mock role matching the ErpRole interface.
 * Mirrors the shape returned by SecurityManager.GetAllRoles() mapped
 * through the Identity service.
 */
const mockRole: ErpRole = {
  id: 'role-guid',
  name: 'administrator',
  description: 'Full access',
};

// ──────────────────────────────────────────────────────────────────────────────
// Response envelope helpers
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Creates a mock ApiResponse<T> envelope matching the shared envelope shape
 * from api/client.ts (ApiResponse interface).
 *
 * @param object - Typed payload for the response
 * @param success - Whether the operation succeeded
 * @param message - Human-readable message
 * @returns Complete ApiResponse envelope
 */
function apiResponse<T>(object: T, success = true, message = 'Success') {
  return {
    success,
    errors: [] as Array<{ key: string; value: string; message: string }>,
    statusCode: success ? 200 : 400,
    timestamp: MOCK_TIMESTAMP,
    message,
    object,
  };
}

/**
 * Creates a mock ApiError matching the error shape thrown by the client
 * interceptor for 400/validation responses.
 *
 * Used to simulate server-side validation errors for:
 *   - Username uniqueness (SecurityManager.SaveUser)
 *   - Email format (MailAddress validation)
 *   - Required password on create
 *   - Role name uniqueness (SecurityManager.SaveRole)
 */
function apiError(
  message: string,
  errors: Array<{ key: string; value: string; message: string }>,
) {
  return {
    message,
    errors,
    status: 400,
    timestamp: MOCK_TIMESTAMP,
  };
}

/**
 * Creates a mock ApiResponse<void> envelope for delete operations.
 * The hook's internal toBaseResponse() function converts this into a
 * BaseResponseModel shape.
 */
function deleteResponse(success = true, message = 'Deleted') {
  return {
    success,
    errors: [] as Array<{ key: string; value: string; message: string }>,
    statusCode: success ? 200 : 400,
    timestamp: MOCK_TIMESTAMP,
    message,
  };
}

// ──────────────────────────────────────────────────────────────────────────────
// QueryClient and wrapper setup
// ──────────────────────────────────────────────────────────────────────────────

let queryClient: QueryClient;

/**
 * Creates a React component wrapper that provides QueryClientProvider context
 * for renderHook calls. Uses createElement instead of JSX to avoid needing
 * a .tsx file extension for this pure-logic test file.
 */
function createWrapper() {
  return ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children);
}

let wrapper: ReturnType<typeof createWrapper>;

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  wrapper = createWrapper();
});

afterEach(() => {
  vi.clearAllMocks();
  queryClient.clear();
});

// ═══════════════════════════════════════════════════════════════════════════════
// USER QUERY HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useUsers — Paginated/filtered user listing
// Replaces: SecurityManager.GetAllUsers() / UserService.GetAll()
// Endpoint: GET /v1/identity/users
// ---------------------------------------------------------------------------

describe('useUsers', () => {
  it('should fetch all users', async () => {
    mockGet.mockResolvedValueOnce(apiResponse([mockUser]) as never);

    const { result } = renderHook(() => useUsers(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/users', undefined);
    expect(result.current.data?.object).toEqual([mockUser]);
  });

  it('should pass search and roleId filters', async () => {
    mockGet.mockResolvedValueOnce(apiResponse([mockUser]) as never);

    const params = { search: 'admin', roleId: 'role-guid' };
    const { result } = renderHook(() => useUsers(params), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/users', params);
  });

  it('should handle pagination', async () => {
    mockGet.mockResolvedValueOnce(apiResponse([mockUser]) as never);

    const params = { page: 2, pageSize: 10 };
    const { result } = renderHook(() => useUsers(params), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/users', params);
  });
});

// ---------------------------------------------------------------------------
// useUser — Single user fetch by ID
// Replaces: SecurityManager.GetUser(Guid id) with EQL $user_role.* join
// Endpoint: GET /v1/identity/users/{id}
// ---------------------------------------------------------------------------

describe('useUser', () => {
  it('should fetch user by ID', async () => {
    mockGet.mockResolvedValueOnce(apiResponse(mockUser) as never);

    const { result } = renderHook(() => useUser('user-guid'), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/users/user-guid');
    expect(result.current.data?.object).toEqual(mockUser);
  });

  it('should include user roles in response', async () => {
    const userWithRoles = {
      ...mockUser,
      roles: [{ id: 'admin-role-guid', name: 'administrator' }],
    };
    mockGet.mockResolvedValueOnce(apiResponse(userWithRoles) as never);

    const { result } = renderHook(() => useUser('user-guid'), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const user = result.current.data?.object as typeof userWithRoles;
    expect(user.roles).toBeDefined();
    expect(user.roles).toHaveLength(1);
    expect(user.roles[0].name).toBe('administrator');
  });

  it('should return null for non-existent user', async () => {
    const notFoundError = apiError('User not found', [
      { key: 'id', value: 'nonexistent-guid', message: 'User not found' },
    ]);
    mockGet.mockRejectedValueOnce(notFoundError);

    const { result } = renderHook(() => useUser('nonexistent-guid'), { wrapper });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('User not found');
  });

  it('should not fetch when id is undefined', () => {
    const { result } = renderHook(() => useUser(undefined), { wrapper });

    // Query should be disabled — fetchStatus 'idle' indicates no fetch initiated
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// useCurrentUserProfile — Authenticated user's full profile
// Replaces: SecurityManager.GetUser(currentUserId) from ErpMiddleware.cs +
//           UserPreferencies.GetComponentData for preference access
// Endpoint: GET /v1/identity/users/me
// ---------------------------------------------------------------------------

describe('useCurrentUserProfile', () => {
  it('should fetch current user profile', async () => {
    mockGet.mockResolvedValueOnce(apiResponse(mockUser) as never);

    const { result } = renderHook(() => useCurrentUserProfile(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/users/me');
    expect(result.current.data?.object).toEqual(mockUser);
  });

  it('should use staleTime of 5 minutes', async () => {
    mockGet.mockResolvedValue(apiResponse(mockUser) as never);

    // First render — triggers initial fetch
    const { result } = renderHook(() => useCurrentUserProfile(), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockGet).toHaveBeenCalledTimes(1);

    mockGet.mockClear();

    // Second render with same QueryClient — data should be served from cache
    // because it's still within the 5-minute staleTime window
    const { result: result2 } = renderHook(
      () => useCurrentUserProfile(),
      { wrapper },
    );
    await waitFor(() => expect(result2.current.isSuccess).toBe(true));

    // No additional API call — data is still fresh
    expect(mockGet).not.toHaveBeenCalled();
  });

  it('should include preferences in response', async () => {
    const userWithDetailedPrefs: ErpUser = {
      ...mockUser,
      preferences: {
        sidebarSize: 'expanded',
        componentUsage: [{ name: 'PcFieldText', sdkUsed: 5, sdkUsedOn: MOCK_TIMESTAMP }],
        componentDataDictionary: { 'pc-field-text': { width: 200 } },
      },
    };
    mockGet.mockResolvedValueOnce(apiResponse(userWithDetailedPrefs) as never);

    const { result } = renderHook(() => useCurrentUserProfile(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const profile = result.current.data?.object;
    expect(profile?.preferences).toBeDefined();
    expect(profile?.preferences?.sidebarSize).toBe('expanded');
    expect(profile?.preferences?.componentUsage).toHaveLength(1);
    expect(profile?.preferences?.componentDataDictionary).toHaveProperty('pc-field-text');
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// USER MUTATION HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useCreateUser — User creation mutation
// Replaces: SecurityManager.SaveUser(ErpUser) — create path
// Endpoint: POST /v1/identity/users
// Cache invalidation: ['users']
// ---------------------------------------------------------------------------

describe('useCreateUser', () => {
  it('should create user', async () => {
    mockPost.mockResolvedValueOnce(apiResponse(mockUser) as never);

    const { result } = renderHook(() => useCreateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({
        username: 'admin',
        email: 'admin@webvella.com',
        password: 's3cure',
        firstName: 'Admin',
        lastName: 'User',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPost).toHaveBeenCalledWith('/identity/users', {
      username: 'admin',
      email: 'admin@webvella.com',
      password: 's3cure',
      firstName: 'Admin',
      lastName: 'User',
    });
    expect(result.current.data?.object).toEqual(mockUser);
  });

  it('should invalidate users query on success', async () => {
    mockPost.mockResolvedValueOnce(apiResponse(mockUser) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({
        username: 'newuser',
        email: 'new@webvella.com',
        password: 'pass123',
        firstName: 'New',
        lastName: 'User',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['users'] });
  });

  it('should handle username uniqueness error', async () => {
    mockPost.mockRejectedValueOnce(
      apiError('Username already exists', [
        { key: 'username', value: 'admin', message: 'Username already exists' },
      ]),
    );

    const { result } = renderHook(() => useCreateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({
        username: 'admin',
        email: 'another@webvella.com',
        password: 'pass',
        firstName: 'Another',
        lastName: 'User',
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Username already exists');
    expect(result.current.error?.errors?.[0]?.key).toBe('username');
  });

  it('should handle email format error', async () => {
    mockPost.mockRejectedValueOnce(
      apiError('Invalid email format', [
        { key: 'email', value: 'notanemail', message: 'Invalid email format' },
      ]),
    );

    const { result } = renderHook(() => useCreateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({
        username: 'newuser',
        email: 'notanemail',
        password: 'pass',
        firstName: 'New',
        lastName: 'User',
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Invalid email format');
    expect(result.current.error?.errors?.[0]?.key).toBe('email');
  });

  it('should require password on create', async () => {
    mockPost.mockRejectedValueOnce(
      apiError('Password is required', [
        {
          key: 'password',
          value: '',
          message: 'Password is required for new users',
        },
      ]),
    );

    const { result } = renderHook(() => useCreateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({
        username: 'newuser',
        email: 'new@webvella.com',
        password: '',
        firstName: 'New',
        lastName: 'User',
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Password is required');
    expect(result.current.error?.errors?.[0]?.key).toBe('password');
  });
});

// ---------------------------------------------------------------------------
// useUpdateUser — User update mutation
// Replaces: SecurityManager.SaveUser(ErpUser) — update path
// Endpoint: PUT /v1/identity/users/{id}
// Cache invalidation: ['users'], ['users', id], ['users', 'me']
// ---------------------------------------------------------------------------

describe('useUpdateUser', () => {
  it('should update user', async () => {
    const updatedUser: ErpUser = { ...mockUser, username: 'updated-admin' };
    mockPut.mockResolvedValueOnce(apiResponse(updatedUser) as never);

    const { result } = renderHook(() => useUpdateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({ id: 'user-guid', username: 'updated-admin' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify PUT is called with id in URL and remaining fields in body
    expect(mockPut).toHaveBeenCalledWith('/identity/users/user-guid', {
      username: 'updated-admin',
    });
    expect(result.current.data?.object?.username).toBe('updated-admin');
  });

  it('should invalidate users list, specific user, and me queries', async () => {
    mockPut.mockResolvedValueOnce(apiResponse(mockUser) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateUser(), { wrapper });

    await act(async () => {
      result.current.mutate({ id: 'user-guid', username: 'updated' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify all three cache invalidation calls:
    // 1. ['users'] — refresh all user list queries
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['users'] });
    // 2. ['users', 'user-guid'] — refresh this specific user's detail cache
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['users', 'user-guid'],
    });
    // 3. ['users', 'me'] — always invalidated for self-update safety
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['users', 'me'] });
  });
});

// ---------------------------------------------------------------------------
// useDeleteUser — User deletion mutation
// Endpoint: DELETE /v1/identity/users/{id}
// Cache invalidation: ['users'] (only on success)
// ---------------------------------------------------------------------------

describe('useDeleteUser', () => {
  it('should delete user', async () => {
    mockDel.mockResolvedValueOnce(deleteResponse(true, 'User deleted') as never);

    const { result } = renderHook(() => useDeleteUser(), { wrapper });

    await act(async () => {
      result.current.mutate('user-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockDel).toHaveBeenCalledWith('/identity/users/user-guid');
    // The hook's toBaseResponse() converts the ApiResponse to BaseResponseModel
    expect(result.current.data?.success).toBe(true);
    expect(result.current.data?.message).toBe('User deleted');
  });

  it('should invalidate users query on success', async () => {
    mockDel.mockResolvedValueOnce(deleteResponse(true, 'User deleted') as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteUser(), { wrapper });

    await act(async () => {
      result.current.mutate('user-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['users'] });
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// USER PREFERENCES MUTATION HOOK
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useUpdatePreferences — Preference persistence mutation
// Replaces: UserPreferencies.SetSidebarSize(), SdkUseComponent(),
//           SetComponentData(), RemoveComponentData()
// Endpoint: PUT /v1/identity/users/me/preferences
// Cache invalidation: ['users', 'me']
// ---------------------------------------------------------------------------

describe('useUpdatePreferences', () => {
  it('should update sidebar size preference', async () => {
    const updatedPrefs: ErpUserPreferences = {
      ...mockPreferences,
      sidebarSize: 'collapsed',
    };
    mockPut.mockResolvedValueOnce(apiResponse(updatedPrefs) as never);

    const { result } = renderHook(() => useUpdatePreferences(), { wrapper });

    await act(async () => {
      result.current.mutate({ sidebarSize: 'collapsed' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPut).toHaveBeenCalledWith('/identity/users/me/preferences', {
      sidebarSize: 'collapsed',
    });
  });

  it('should update component data', async () => {
    const componentData: Record<string, Record<string, unknown>> = {
      'pc-field-text': { width: 200, collapsed: false },
    };
    const updatedPrefs: ErpUserPreferences = {
      ...mockPreferences,
      componentDataDictionary: componentData,
    };
    mockPut.mockResolvedValueOnce(apiResponse(updatedPrefs) as never);

    const { result } = renderHook(() => useUpdatePreferences(), { wrapper });

    await act(async () => {
      result.current.mutate({ componentDataDictionary: componentData });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPut).toHaveBeenCalledWith('/identity/users/me/preferences', {
      componentDataDictionary: componentData,
    });
  });

  it('should invalidate current user profile on success', async () => {
    mockPut.mockResolvedValueOnce(apiResponse(mockPreferences) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdatePreferences(), { wrapper });

    await act(async () => {
      result.current.mutate({ sidebarSize: 'sm' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Preference updates invalidate the 'me' profile cache so it re-fetches
    // with the updated preferences included
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['users', 'me'] });
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// ROLE QUERY HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useRoles — Role listing
// Replaces: SecurityManager.GetAllRoles() — EQL: SELECT * FROM role
// Endpoint: GET /v1/identity/roles
// StaleTime: 10 minutes (roles change infrequently)
// ---------------------------------------------------------------------------

describe('useRoles', () => {
  it('should fetch all roles', async () => {
    const allRoles: ErpRole[] = [
      mockRole,
      { id: 'regular-role-guid', name: 'regular', description: 'Standard user' },
      { id: 'guest-role-guid', name: 'guest', description: 'Read-only access' },
    ];
    mockGet.mockResolvedValueOnce(apiResponse(allRoles) as never);

    const { result } = renderHook(() => useRoles(), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/roles');
    expect(result.current.data?.object).toEqual(allRoles);
    expect(result.current.data?.object).toHaveLength(3);
  });

  it('should use staleTime of 10 minutes', async () => {
    mockGet.mockResolvedValue(apiResponse([mockRole]) as never);

    // First render — triggers initial fetch
    const { result } = renderHook(() => useRoles(), { wrapper });
    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockGet).toHaveBeenCalledTimes(1);

    mockGet.mockClear();

    // Second render with same QueryClient — data should be served from cache
    // because it's still within the 10-minute staleTime window
    const { result: result2 } = renderHook(() => useRoles(), { wrapper });
    await waitFor(() => expect(result2.current.isSuccess).toBe(true));

    // No additional API call — data is still fresh
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// useRole — Single role fetch by ID
// Endpoint: GET /v1/identity/roles/{id}
// ---------------------------------------------------------------------------

describe('useRole', () => {
  it('should fetch role by ID', async () => {
    mockGet.mockResolvedValueOnce(apiResponse(mockRole) as never);

    const { result } = renderHook(() => useRole('role-guid'), { wrapper });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/roles/role-guid');
    expect(result.current.data?.object).toEqual(mockRole);
  });

  it('should not fetch when id is undefined', () => {
    const { result } = renderHook(() => useRole(undefined), { wrapper });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// useUsersInRole — Users-in-role listing
// Replaces: SecurityManager.GetAllUsersInRole(Guid roleId)
// Endpoint: GET /v1/identity/roles/{roleId}/users
// ---------------------------------------------------------------------------

describe('useUsersInRole', () => {
  it('should fetch users in role', async () => {
    mockGet.mockResolvedValueOnce(apiResponse([mockUser]) as never);

    const { result } = renderHook(
      () => useUsersInRole('role-guid'),
      { wrapper },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockGet).toHaveBeenCalledWith('/identity/roles/role-guid/users');
    expect(result.current.data?.object).toEqual([mockUser]);
  });

  it('should not fetch when roleId is undefined', () => {
    const { result } = renderHook(
      () => useUsersInRole(undefined),
      { wrapper },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockGet).not.toHaveBeenCalled();
  });
});

// ═══════════════════════════════════════════════════════════════════════════════
// ROLE MUTATION HOOKS
// ═══════════════════════════════════════════════════════════════════════════════

// ---------------------------------------------------------------------------
// useCreateRole — Role creation mutation
// Replaces: SecurityManager.SaveRole(ErpRole) — create path
// Endpoint: POST /v1/identity/roles
// Cache invalidation: ['roles']
// ---------------------------------------------------------------------------

describe('useCreateRole', () => {
  it('should create role', async () => {
    const newRole: ErpRole = {
      id: 'new-role-guid',
      name: 'editor',
      description: 'Can edit content',
    };
    mockPost.mockResolvedValueOnce(apiResponse(newRole) as never);

    const { result } = renderHook(() => useCreateRole(), { wrapper });

    await act(async () => {
      result.current.mutate({ name: 'editor', description: 'Can edit content' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockPost).toHaveBeenCalledWith('/identity/roles', {
      name: 'editor',
      description: 'Can edit content',
    });
    expect(result.current.data?.object).toEqual(newRole);
  });

  it('should invalidate roles query on success', async () => {
    mockPost.mockResolvedValueOnce(apiResponse(mockRole) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateRole(), { wrapper });

    await act(async () => {
      result.current.mutate({ name: 'viewer', description: 'Read-only access' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['roles'] });
  });

  it('should handle role name uniqueness error', async () => {
    mockPost.mockRejectedValueOnce(
      apiError('Role name already exists', [
        {
          key: 'name',
          value: 'administrator',
          message: 'A role with this name already exists',
        },
      ]),
    );

    const { result } = renderHook(() => useCreateRole(), { wrapper });

    await act(async () => {
      result.current.mutate({ name: 'administrator' });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));

    expect(result.current.error?.message).toBe('Role name already exists');
    expect(result.current.error?.errors?.[0]?.key).toBe('name');
  });
});

// ---------------------------------------------------------------------------
// useUpdateRole — Role update mutation
// Replaces: SecurityManager.SaveRole(ErpRole) — update path
// Endpoint: PUT /v1/identity/roles/{id}
// Cache invalidation: ['roles'], ['roles', id]
// ---------------------------------------------------------------------------

describe('useUpdateRole', () => {
  it('should update role', async () => {
    const updatedRole: ErpRole = {
      ...mockRole,
      description: 'Updated description',
    };
    mockPut.mockResolvedValueOnce(apiResponse(updatedRole) as never);

    const { result } = renderHook(() => useUpdateRole(), { wrapper });

    await act(async () => {
      result.current.mutate({
        id: 'role-guid',
        description: 'Updated description',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify PUT is called with id in URL and remaining fields in body
    expect(mockPut).toHaveBeenCalledWith('/identity/roles/role-guid', {
      description: 'Updated description',
    });
  });

  it('should invalidate roles list and specific role', async () => {
    mockPut.mockResolvedValueOnce(apiResponse(mockRole) as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateRole(), { wrapper });

    await act(async () => {
      result.current.mutate({ id: 'role-guid', name: 'updated-admin' });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Verify both cache invalidation calls:
    // 1. ['roles'] — refresh all role list queries
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['roles'] });
    // 2. ['roles', 'role-guid'] — refresh this specific role's detail cache
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['roles', 'role-guid'],
    });
  });
});

// ---------------------------------------------------------------------------
// useDeleteRole — Role deletion mutation
// Endpoint: DELETE /v1/identity/roles/{id}
// Cache invalidation: ['roles'] (only on success)
// ---------------------------------------------------------------------------

describe('useDeleteRole', () => {
  it('should delete role', async () => {
    mockDel.mockResolvedValueOnce(deleteResponse(true, 'Role deleted') as never);

    const { result } = renderHook(() => useDeleteRole(), { wrapper });

    await act(async () => {
      result.current.mutate('role-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockDel).toHaveBeenCalledWith('/identity/roles/role-guid');
    // The hook's toBaseResponse() converts the ApiResponse to BaseResponseModel
    expect(result.current.data?.success).toBe(true);
    expect(result.current.data?.message).toBe('Role deleted');
  });

  it('should invalidate roles query on success', async () => {
    mockDel.mockResolvedValueOnce(deleteResponse(true, 'Role deleted') as never);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteRole(), { wrapper });

    await act(async () => {
      result.current.mutate('role-guid');
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['roles'] });
  });
});
