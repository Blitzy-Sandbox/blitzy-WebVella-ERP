/**
 * User Management TanStack Query Hooks
 *
 * Provides React Query 5 hooks for all user and role management operations,
 * replacing the monolith's server-side invocation patterns with API calls to
 * the Identity microservice Lambda handlers via API Gateway.
 *
 * Source mapping:
 *   - `SecurityManager.cs`  → User/role CRUD, lookup, credential validation
 *   - `UserService.cs`      → EQL-based user listing and retrieval
 *   - `UserPreferencies.cs` → Per-user preference persistence (sidebar, component data)
 *
 * Target API routes:
 *   - `/identity/users/*`   — User CRUD, profile, preferences
 *   - `/identity/roles/*`   — Role CRUD, users-in-role listing
 *
 * All hooks follow TanStack Query 5 patterns:
 *   - Query hooks use `useQuery` with hierarchical cache keys
 *   - Mutation hooks use `useMutation` with `onSuccess` cache invalidation
 *   - Cache invalidation is scoped to affected query key prefixes
 *
 * AAP compliance:
 *   - §0.4.3 — Full CRUD for users and roles via Identity service
 *   - §0.5.1 — SecurityManager.GetAllUsers → useUsers, GetUser → useUser, etc.
 *   - §0.7.5 — Password handling is server-side only (Cognito)
 *   - §0.8.1 — Self-contained SPA hooks with no direct DB access
 *
 * @module hooks/useUsers
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';

import { get, post, put, del } from '../api/client';
import type { ApiResponse, ApiError } from '../api/client';
import type { ErpUser, ErpRole, ErpUserPreferences } from '../types/user';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Key Factories
// ---------------------------------------------------------------------------

/**
 * Hierarchical query key factory for user-related queries.
 *
 * Keys follow a parent→child hierarchy so that invalidating a parent
 * (e.g. `['users']`) cascades to all children (detail, me, inRole).
 *
 * Pattern mirrors the monolith's SecurityManager method grouping:
 *   - GetAllUsers    → `userKeys.list(params)`
 *   - GetUser(id)    → `userKeys.detail(id)`
 *   - GetUser(email) → handled server-side, client uses `detail`
 *   - GetAllUsersInRole → `userKeys.inRole(roleId)`
 */
const userKeys = {
  /** Root key — invalidating this clears ALL user-related caches */
  all: ['users'] as const,
  /** Paginated/filtered user list query */
  list: (params: UserListParams | undefined) => [...userKeys.all, params] as const,
  /** Single user by ID */
  detail: (id: string) => [...userKeys.all, id] as const,
  /** Current authenticated user's full profile */
  me: () => [...userKeys.all, 'me'] as const,
  /** Users belonging to a specific Cognito group / role */
  inRole: (roleId: string) => [...userKeys.all, 'role', roleId] as const,
};

/**
 * Hierarchical query key factory for role-related queries.
 *
 * Mirrors SecurityManager.GetAllRoles / role-by-ID retrieval.
 */
const roleKeys = {
  /** Root key — invalidating this clears ALL role-related caches */
  all: ['roles'] as const,
  /** Single role by ID */
  detail: (id: string) => ['roles', id] as const,
};

// ---------------------------------------------------------------------------
// Parameter and Payload Types
// ---------------------------------------------------------------------------

/**
 * Query parameters for the paginated user list.
 *
 * Replaces SecurityManager.GetAllUsers() and GetUsers(roleIds) by
 * supporting server-side filtering via query parameters.
 */
export interface UserListParams {
  /** Free-text search across username, email, firstName, lastName */
  search?: string;
  /** Filter users by role membership (maps to Cognito group) */
  roleId?: string;
  /** 1-based page number for pagination */
  page?: number;
  /** Number of results per page */
  pageSize?: number;
}

/**
 * Payload for creating a new user.
 *
 * Maps to SecurityManager.SaveUser(ErpUser) — create path.
 * Validation rules preserved from source:
 *   - username: required, unique
 *   - email: required, unique, valid format
 *   - password: required on create
 *   - roleIds: optional list of role IDs (maps to `$user_role.id`)
 */
export interface CreateUserPayload {
  /** Username for display purposes — must be unique */
  username: string;
  /** User's email address — must be unique, valid format */
  email: string;
  /** Password for authentication — required on create, server-side hashing */
  password: string;
  /** User's first name */
  firstName: string;
  /** User's last name */
  lastName: string;
  /** URL or path to the user's profile image */
  image?: string;
  /** List of role IDs to assign (maps to monolith's `$user_role.id`) */
  roleIds?: string[];
}

/**
 * Payload for updating an existing user.
 *
 * Maps to SecurityManager.SaveUser(ErpUser) — update path.
 * Only changed fields need to be included (delta update pattern).
 * Preserves the monolith's per-field change detection logic.
 */
export interface UpdateUserPayload {
  /** User ID — required to identify the target user */
  id: string;
  /** Updated username — triggers uniqueness check if changed */
  username?: string;
  /** Updated email — triggers uniqueness and format validation if changed */
  email?: string;
  /** New password — only processed if non-empty (mirrors monolith logic) */
  password?: string;
  /** Updated first name */
  firstName?: string;
  /** Updated last name */
  lastName?: string;
  /** Updated profile image URL */
  image?: string;
  /** Account enabled state */
  enabled?: boolean;
  /** Account verified state */
  verified?: boolean;
  /** Updated role ID list (replaces all existing role assignments) */
  roleIds?: string[];
}

/**
 * Payload for creating a new role.
 *
 * Maps to SecurityManager.SaveRole(ErpRole) — create path.
 * Validation: name required, must be unique.
 */
export interface CreateRolePayload {
  /** Role display name — must be unique */
  name: string;
  /** Human-readable description of the role's purpose */
  description?: string;
}

/**
 * Payload for updating an existing role.
 *
 * Maps to SecurityManager.SaveRole(ErpRole) — update path.
 * Name uniqueness is re-validated server-side if changed.
 */
export interface UpdateRolePayload {
  /** Role ID — required to identify the target role */
  id: string;
  /** Updated role name — triggers uniqueness check if changed */
  name?: string;
  /** Updated description */
  description?: string;
}

// ---------------------------------------------------------------------------
// Helper — Map API envelope to BaseResponseModel for delete operations
// ---------------------------------------------------------------------------

/**
 * Converts an `ApiResponse<void>` envelope (returned by the `del()` client
 * function) into a `BaseResponseModel` shape for delete mutation consumers.
 *
 * Delete operations don't return a typed object payload — only the base
 * response fields (success, errors, message) are meaningful. This helper
 * bridges the client envelope type to the shared `BaseResponseModel`
 * interface from `types/common.ts`.
 *
 * @param response - API response envelope from a DELETE operation
 * @returns Normalized `BaseResponseModel` with success, errors, and message
 */
function toBaseResponse(response: ApiResponse<void>): BaseResponseModel {
  return {
    success: response.success,
    message: response.message,
    timestamp: response.timestamp,
    hash: response.hash ?? null,
    errors: response.errors.map((e) => ({
      key: e.key,
      value: e.value,
      message: e.message,
    })),
    accessWarnings: [],
  };
}

// ---------------------------------------------------------------------------
// User Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated and filterable list of all users.
 *
 * Replaces:
 *   - `SecurityManager.GetAllUsers()` — full user list
 *   - `UserService.GetAll()` — EQL: `SELECT * FROM user`
 *
 * @param params - Optional filtering and pagination parameters
 * @returns TanStack Query result with `data`, `isLoading`, `isError`,
 *          `error`, `isSuccess`, `refetch`, `isFetching`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isFetching } = useUsers({ page: 1, pageSize: 20 });
 * const users = data?.object ?? [];
 * ```
 */
export function useUsers(params?: UserListParams) {
  return useQuery<ApiResponse<ErpUser[]>, ApiError>({
    queryKey: userKeys.list(params),
    queryFn: () =>
      get<ErpUser[]>(
        '/identity/users',
        params as Record<string, unknown> | undefined,
      ),
  });
}

/**
 * Fetches a single user by ID, including their role assignments.
 *
 * Replaces:
 *   - `SecurityManager.GetUser(Guid id)` — loads user with roles via
 *     EQL: `SELECT *, $user_role.* FROM user WHERE id = @id`
 *   - `UserService.Get(Guid userId)` — EQL: `SELECT * FROM user WHERE id = @userId`
 *
 * @param id - User UUID string. Pass `undefined` to disable the query.
 * @returns TanStack Query result with `data`, `isLoading`, `isError`,
 *          `error`, `isSuccess`, `refetch`
 *
 * @example
 * ```tsx
 * const { data, isLoading } = useUser(userId);
 * const user = data?.object;
 * ```
 */
export function useUser(id: string | undefined) {
  return useQuery<ApiResponse<ErpUser>, ApiError>({
    queryKey: userKeys.detail(id ?? ''),
    queryFn: () => get<ErpUser>(`/identity/users/${id}`),
    enabled: !!id,
  });
}

/**
 * Fetches the current authenticated user's full profile, including preferences.
 *
 * This is SEPARATE from the auth store's JWT claims — it fetches the
 * complete `ErpUser` record with preferences from the Identity service.
 *
 * Uses a 5-minute staleTime since the profile changes infrequently and
 * is invalidated on preference/profile updates.
 *
 * Replaces:
 *   - `SecurityManager.GetUser(currentUserId)` from `ErpMiddleware.cs`
 *     where the user is loaded on every request
 *   - `UserPreferencies.GetComponentData` for preference access
 *
 * @returns TanStack Query result with `data`, `isLoading`, `isError`,
 *          `error`, `isSuccess`, `refetch`
 *
 * @example
 * ```tsx
 * const { data } = useCurrentUserProfile();
 * const sidebarSize = data?.object?.preferences?.sidebarSize ?? 'md';
 * ```
 */
export function useCurrentUserProfile() {
  return useQuery<ApiResponse<ErpUser>, ApiError>({
    queryKey: userKeys.me(),
    queryFn: () => get<ErpUser>('/identity/users/me'),
    staleTime: 5 * 60 * 1000, // 5 minutes
  });
}

// ---------------------------------------------------------------------------
// User Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new user in the Identity service.
 *
 * Replaces `SecurityManager.SaveUser(ErpUser)` — create path, which:
 *   - Validates username/email uniqueness
 *   - Validates email format via `MailAddress` parsing
 *   - Requires password on creation
 *   - Sets role relationships via `$user_role.id`
 *   - Serializes preferences as JSON
 *
 * In the target architecture, the Identity service Lambda handler performs
 * Cognito user creation + DynamoDB profile persistence.
 *
 * On success, invalidates the `['users']` cache prefix to refresh all
 * user list queries.
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, `reset`
 *
 * @example
 * ```tsx
 * const createUser = useCreateUser();
 * createUser.mutate({ username: 'jdoe', email: 'jdoe@example.com', password: 's3cure', firstName: 'John', lastName: 'Doe' });
 * ```
 */
export function useCreateUser() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<ErpUser>, ApiError, CreateUserPayload>({
    mutationFn: (payload: CreateUserPayload) =>
      post<ErpUser>('/identity/users', payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
    },
  });
}

/**
 * Updates an existing user's profile in the Identity service.
 *
 * Replaces `SecurityManager.SaveUser(ErpUser)` — update path, which:
 *   - Detects per-field changes (username, email, password, etc.)
 *   - Re-validates uniqueness for changed username/email
 *   - Skips password update if empty
 *   - Updates role relationships via `$user_role.id`
 *
 * On success, invalidates:
 *   - `['users']` — all user list queries
 *   - `['users', id]` — the updated user's detail cache
 *   - `['users', 'me']` — current user profile (in case of self-update,
 *     always invalidated for safety since detecting self-update client-side
 *     would require additional state)
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, `reset`
 */
export function useUpdateUser() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<ErpUser>, ApiError, UpdateUserPayload>({
    mutationFn: ({ id, ...data }: UpdateUserPayload) =>
      put<ErpUser>(`/identity/users/${id}`, data),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: userKeys.all });
      queryClient.invalidateQueries({ queryKey: userKeys.detail(variables.id) });
      // Always invalidate current user profile to handle self-update detection
      // without requiring client-side user ID comparison
      queryClient.invalidateQueries({ queryKey: userKeys.me() });
    },
  });
}

/**
 * Deletes a user from the Identity service.
 *
 * No direct monolith equivalent — the original codebase did not expose user
 * deletion via SecurityManager (users were soft-disabled via `enabled` flag).
 * The target architecture supports hard deletion via Cognito + DynamoDB.
 *
 * Returns a `BaseResponseModel` shape (success, errors, message) since
 * delete operations don't produce a typed object payload.
 *
 * On success, invalidates `['users']` to refresh all user list queries.
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `reset`
 */
export function useDeleteUser() {
  const queryClient = useQueryClient();

  return useMutation<BaseResponseModel, ApiError, string>({
    mutationFn: async (id: string): Promise<BaseResponseModel> =>
      toBaseResponse(await del(`/identity/users/${id}`)),
    onSuccess: (data: BaseResponseModel) => {
      if (data.success) {
        queryClient.invalidateQueries({ queryKey: userKeys.all });
      }
    },
  });
}

// ---------------------------------------------------------------------------
// User Preferences Mutation Hook
// ---------------------------------------------------------------------------

/**
 * Updates the current user's preferences (sidebar size, component usage, etc.).
 *
 * Replaces multiple `UserPreferencies` methods:
 *   - `SetSidebarSize(userId, size)` — updates preferences.SidebarSize
 *   - `SdkUseComponent(userId, componentName)` — increments usage counter
 *   - `SetComponentData(userId, componentName, data)` — writes per-component data
 *   - `RemoveComponentData(userId, componentName)` — deletes per-component data
 *
 * The target architecture consolidates all preference operations into a
 * single PUT endpoint that accepts a partial `ErpUserPreferences` object.
 * The Identity service Lambda handler merges the partial update into the
 * existing preferences JSON stored alongside the user profile.
 *
 * On success, invalidates `['users', 'me']` to refresh the current user
 * profile cache, which includes the updated preferences.
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, `reset`
 *
 * @example
 * ```tsx
 * const updatePrefs = useUpdatePreferences();
 * updatePrefs.mutate({ sidebarSize: 'sm' });
 * ```
 */
export function useUpdatePreferences() {
  const queryClient = useQueryClient();

  return useMutation<
    ApiResponse<ErpUserPreferences>,
    ApiError,
    Partial<ErpUserPreferences>
  >({
    mutationFn: (preferences: Partial<ErpUserPreferences>) =>
      put<ErpUserPreferences>('/identity/users/me/preferences', preferences),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: userKeys.me() });
    },
  });
}

// ---------------------------------------------------------------------------
// Role Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches all roles in the system.
 *
 * Replaces `SecurityManager.GetAllRoles()` which executes
 * EQL: `SELECT * FROM role`.
 *
 * Uses a 10-minute staleTime since roles change infrequently in
 * production environments (typically only during initial setup or
 * administrative reconfiguration). This reduces unnecessary API calls
 * to the Identity service.
 *
 * In the target architecture, roles map to Cognito user pool groups.
 *
 * @returns TanStack Query result with `data`, `isLoading`, `isError`,
 *          `error`, `isSuccess`, `refetch`
 *
 * @example
 * ```tsx
 * const { data } = useRoles();
 * const roles = data?.object ?? [];
 * ```
 */
export function useRoles() {
  return useQuery<ApiResponse<ErpRole[]>, ApiError>({
    queryKey: roleKeys.all,
    queryFn: () => get<ErpRole[]>('/identity/roles'),
    staleTime: 10 * 60 * 1000, // 10 minutes — roles change infrequently
  });
}

/**
 * Fetches a single role by ID.
 *
 * No direct monolith equivalent — the original SecurityManager loaded
 * all roles via `GetAllRoles()` and filtered client-side. The target
 * architecture exposes a dedicated per-role endpoint for efficiency.
 *
 * @param id - Role UUID string. Pass `undefined` to disable the query.
 * @returns TanStack Query result with `data`, `isLoading`, `isError`,
 *          `error`, `isSuccess`, `refetch`
 */
export function useRole(id: string | undefined) {
  return useQuery<ApiResponse<ErpRole>, ApiError>({
    queryKey: roleKeys.detail(id ?? ''),
    queryFn: () => get<ErpRole>(`/identity/roles/${id}`),
    enabled: !!id,
  });
}

/**
 * Fetches all users belonging to a specific role.
 *
 * Replaces `SecurityManager.GetAllUsersInRole(Guid roleId)` which used
 * `GetUsers(roleIds)` internally with EQL:
 * `SELECT *, $user_role.* FROM user WHERE $user_role.id = @role_id_...`
 *
 * In the target architecture, this maps to listing users in a Cognito
 * user pool group.
 *
 * @param roleId - Role UUID string. Pass `undefined` to disable the query.
 * @returns TanStack Query result with `data`, `isLoading`, `isError`,
 *          `error`, `isSuccess`, `refetch`, `isFetching`
 *
 * @example
 * ```tsx
 * const { data, isFetching } = useUsersInRole(selectedRoleId);
 * const roleUsers = data?.object ?? [];
 * ```
 */
export function useUsersInRole(roleId: string | undefined) {
  return useQuery<ApiResponse<ErpUser[]>, ApiError>({
    queryKey: userKeys.inRole(roleId ?? ''),
    queryFn: () => get<ErpUser[]>(`/identity/roles/${roleId}/users`),
    enabled: !!roleId,
  });
}

// ---------------------------------------------------------------------------
// Role Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new role in the Identity service.
 *
 * Replaces `SecurityManager.SaveRole(ErpRole)` — create path, which:
 *   - Validates name is required
 *   - Validates name uniqueness against all existing roles
 *   - Creates the role record via `RecordManager.CreateRecord("role", record)`
 *
 * In the target architecture, this creates a Cognito user pool group +
 * DynamoDB role metadata entry.
 *
 * On success, invalidates `['roles']` to refresh the role list.
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, `reset`
 *
 * @example
 * ```tsx
 * const createRole = useCreateRole();
 * createRole.mutate({ name: 'Editor', description: 'Can edit content' });
 * ```
 */
export function useCreateRole() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<ErpRole>, ApiError, CreateRolePayload>({
    mutationFn: (payload: CreateRolePayload) =>
      post<ErpRole>('/identity/roles', payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: roleKeys.all });
    },
  });
}

/**
 * Updates an existing role in the Identity service.
 *
 * Replaces `SecurityManager.SaveRole(ErpRole)` — update path, which:
 *   - Updates description unconditionally
 *   - Re-validates name uniqueness only if name changed
 *
 * On success, invalidates:
 *   - `['roles']` — all role list queries
 *   - `['roles', id]` — the updated role's detail cache
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, `reset`
 */
export function useUpdateRole() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<ErpRole>, ApiError, UpdateRolePayload>({
    mutationFn: ({ id, ...data }: UpdateRolePayload) =>
      put<ErpRole>(`/identity/roles/${id}`, data),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: roleKeys.all });
      queryClient.invalidateQueries({
        queryKey: roleKeys.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes a role from the Identity service.
 *
 * No direct monolith equivalent — SecurityManager.SaveRole only supported
 * create/update. The target architecture supports role deletion via
 * Cognito group removal + DynamoDB metadata cleanup.
 *
 * Returns a `BaseResponseModel` shape (success, errors, message) since
 * delete operations don't produce a typed object payload.
 *
 * On success, invalidates `['roles']` to refresh the role list.
 *
 * @returns TanStack Mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `reset`
 */
export function useDeleteRole() {
  const queryClient = useQueryClient();

  return useMutation<BaseResponseModel, ApiError, string>({
    mutationFn: async (id: string): Promise<BaseResponseModel> =>
      toBaseResponse(await del(`/identity/roles/${id}`)),
    onSuccess: (data: BaseResponseModel) => {
      if (data.success) {
        queryClient.invalidateQueries({ queryKey: roleKeys.all });
      }
    },
  });
}
