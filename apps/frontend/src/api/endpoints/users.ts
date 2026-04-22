/**
 * User/Role Management API Module
 *
 * Typed API functions for user preference management, user CRUD, and role
 * operations. Routes all requests through the centralized API client to the
 * Identity bounded-context service Lambda handlers via API Gateway.
 *
 * Replaces:
 * - WebApiController.cs lines 340–493 (user preference endpoints)
 * - SecurityManager.cs user/role CRUD operations
 * - UserPreferencies.cs server-side preference management
 * - AuthService.cs user retrieval from claims
 *
 * Route prefix mapping:
 * - Preferences: /identity/users/preferences/*
 * - User CRUD:   /identity/users/*
 * - Role CRUD:   /identity/roles/*
 *
 * @module api/endpoints/users
 */

import { get, post, put, del } from '../client';
import type { ApiResponse } from '../client';
import type { ErpUser, ErpRole } from '../../types/user';
import type { BaseResponseModel } from '../../types/common';

// ---------------------------------------------------------------------------
// Type Definitions
// ---------------------------------------------------------------------------

/**
 * Query parameters for user listing with pagination, search, and role filtering.
 *
 * Used by {@link listUsers} to construct query-string parameters sent to
 * `GET /identity/users`. All fields are optional — omitting all produces
 * an unfiltered, first-page result set using server defaults.
 */
export interface UserListParams {
  /** Free-text search filter applied to username, email, firstName, lastName */
  search?: string;

  /** Page number for pagination (1-based). Defaults to 1 on the server. */
  page?: number;

  /** Number of results per page. Defaults to the server's standard page size. */
  pageSize?: number;

  /** Filter users by role membership (UUID string). */
  roleId?: string;
}

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * Maps an {@link ApiResponse} envelope (returned by the API client) to the
 * {@link BaseResponseModel} shape expected by preference toggle callers.
 *
 * The monolith's `ToggleSidebarSize` and `ToggleSection` endpoints returned
 * `BaseResponseModel` directly. In the serverless architecture the Identity
 * Lambda returns an `ApiResponse`-shaped envelope; this mapper bridges the
 * two representations so callers retain the same contract.
 *
 * @internal
 */
function toBaseResponse(response: ApiResponse<void>): BaseResponseModel {
  return {
    timestamp: response.timestamp,
    success: response.success,
    message: response.message,
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
// User Preference Functions
// ---------------------------------------------------------------------------

/**
 * Toggles the authenticated user's sidebar size preference between "sm" and
 * "lg".
 *
 * Replaces `WebApiController.ToggleSidebarSize` (line 340):
 * - Default → "lg", "sm" → "lg", "lg" → "sm"
 * - Server-side: calls `UserPreferencies.SetSidebarSize()` which updates
 *   the user entity record's preferences JSON field
 *
 * In the serverless architecture the Identity Lambda handler performs the
 * same toggle logic and persists to DynamoDB.
 *
 * @returns Promise resolving to a {@link BaseResponseModel} with success /
 *          error status (no typed object payload).
 *
 * @example
 * ```ts
 * const result = await toggleSidebarSize();
 * if (result.success) {
 *   console.log(result.message); // e.g. "Sidebar toggled successfully"
 * }
 * ```
 */
export async function toggleSidebarSize(): Promise<BaseResponseModel> {
  const response = await post<void>(
    '/identity/users/preferences/toggle-sidebar-size',
  );
  return toBaseResponse(response);
}

/**
 * Toggles a PcSection component's collapsed / uncollapsed state in user
 * preferences.
 *
 * Replaces `WebApiController.ToggleSection` (line 377):
 * - Manages `collapsed_node_ids` and `uncollapsed_node_ids` lists
 * - Handles multiple stored formats defensively (string, `List<Guid>`,
 *   `JArray`) — lines 404–454
 * - When `isCollapsed === true`: removes nodeId from uncollapsed list, adds
 *   to collapsed
 * - When `isCollapsed === false`: removes nodeId from collapsed list, adds
 *   to uncollapsed
 * - Persists via `UserPreferencies.SetComponentData()` for the PcSection
 *   component
 *
 * In the serverless architecture the Identity Lambda handler replicates this
 * toggle logic and persists to DynamoDB.
 *
 * @param nodeId      - UUID string of the section node to toggle.
 * @param isCollapsed - Target collapsed state (`true` = collapse,
 *                      `false` = expand).
 * @returns Promise resolving to a {@link BaseResponseModel} with success /
 *          error status.
 *
 * @example
 * ```ts
 * const result = await toggleSectionCollapse(
 *   '3fa85f64-5717-4562-b3fc-2c963f66afa6',
 *   true,
 * );
 * if (!result.success) {
 *   console.error(result.errors);
 * }
 * ```
 */
export async function toggleSectionCollapse(
  nodeId: string,
  isCollapsed: boolean,
): Promise<BaseResponseModel> {
  const response = await post<void>(
    '/identity/users/preferences/toggle-section-collapse',
    { nodeId, isCollapsed },
  );
  return toBaseResponse(response);
}

// ---------------------------------------------------------------------------
// User CRUD Functions
// ---------------------------------------------------------------------------

/**
 * Retrieves the currently authenticated user's profile.
 *
 * Replaces `AuthService.GetUser(ClaimsPrincipal)` which extracted the user
 * ID from cookie / JWT claims and fetched the full {@link ErpUser} via
 * `SecurityManager`. In the serverless architecture the Identity Lambda
 * extracts the user ID from the Cognito JWT claims in the API Gateway event
 * context.
 *
 * @returns Promise resolving to an {@link ApiResponse} containing the
 *          authenticated {@link ErpUser}.
 *
 * @example
 * ```ts
 * const { object: user, success } = await getCurrentUser();
 * if (success && user) {
 *   console.log(user.username, user.email, user.isAdmin);
 * }
 * ```
 */
export async function getCurrentUser(): Promise<ApiResponse<ErpUser>> {
  return get<ErpUser>('/identity/users/me');
}

/**
 * Updates the currently authenticated user's profile fields.
 *
 * Replaces `SecurityManager` user update operations that accepted an
 * `EntityRecord` with selective field updates. Only fields present in the
 * partial data object are updated; omitted fields retain their current
 * values.
 *
 * @param data - Partial user profile data to update (e.g. `firstName`,
 *               `lastName`, `email`, `image`, `preferences`).
 * @returns Promise resolving to an {@link ApiResponse} containing the
 *          updated {@link ErpUser}.
 *
 * @example
 * ```ts
 * const { object: updated } = await updateUserProfile({
 *   firstName: 'Jane',
 *   lastName: 'Doe',
 * });
 * ```
 */
export async function updateUserProfile(
  data: Partial<ErpUser>,
): Promise<ApiResponse<ErpUser>> {
  return put<ErpUser>('/identity/users/me', data);
}

/**
 * Retrieves a specific user by their unique identifier.
 *
 * Replaces `SecurityManager.GetUser(Guid userId)` which queried the user
 * entity by ID from the monolith's single PostgreSQL database. In the
 * serverless architecture the Identity Lambda queries DynamoDB.
 *
 * @param userId - UUID string of the user to retrieve.
 * @returns Promise resolving to an {@link ApiResponse} containing the
 *          requested {@link ErpUser}.
 *
 * @example
 * ```ts
 * const { object: user } = await getUserById(
 *   '3fa85f64-5717-4562-b3fc-2c963f66afa6',
 * );
 * ```
 */
export async function getUserById(
  userId: string,
): Promise<ApiResponse<ErpUser>> {
  return get<ErpUser>(`/identity/users/${encodeURIComponent(userId)}`);
}

/**
 * Lists users with optional pagination, search filtering, and role
 * filtering.
 *
 * Replaces `SecurityManager.GetUsers()` and related list operations that
 * queried the user entity with EQL. In the serverless architecture the
 * Identity Lambda queries DynamoDB with filter expressions derived from
 * the query parameters.
 *
 * @param params - Optional pagination, search, and role filter parameters.
 *                 See {@link UserListParams} for details.
 * @returns Promise resolving to an {@link ApiResponse} containing an array
 *          of {@link ErpUser} objects.
 *
 * @example
 * ```ts
 * const { object: users } = await listUsers({
 *   search: 'admin',
 *   page: 1,
 *   pageSize: 25,
 * });
 * ```
 */
export async function listUsers(
  params?: UserListParams,
): Promise<ApiResponse<ErpUser[]>> {
  const queryParams: Record<string, unknown> = {};

  if (params?.search !== undefined && params.search !== '') {
    queryParams.search = params.search;
  }
  if (params?.page !== undefined) {
    queryParams.page = params.page;
  }
  if (params?.pageSize !== undefined) {
    queryParams.pageSize = params.pageSize;
  }
  if (params?.roleId !== undefined && params.roleId !== '') {
    queryParams.roleId = params.roleId;
  }

  return get<ErpUser[]>('/identity/users', queryParams);
}

// ---------------------------------------------------------------------------
// Role CRUD Functions
// ---------------------------------------------------------------------------

/**
 * Lists all system roles.
 *
 * Replaces `SecurityManager` role listing that queried the role entity
 * table. Returns all roles including the three system roles (Administrator,
 * Regular, Guest) identified by `SystemIds` constants in the monolith's
 * `Definitions.cs`.
 *
 * @returns Promise resolving to an {@link ApiResponse} containing an array
 *          of {@link ErpRole} objects.
 *
 * @example
 * ```ts
 * const { object: roles } = await listRoles();
 * roles?.forEach((r) => console.log(r.id, r.name, r.description));
 * ```
 */
export async function listRoles(): Promise<ApiResponse<ErpRole[]>> {
  return get<ErpRole[]>('/identity/roles');
}

/**
 * Creates a new role in the system.
 *
 * Replaces `SecurityManager.SaveRole()` which created a new role entity
 * record. In the serverless architecture the Identity Lambda creates the
 * role in DynamoDB and publishes an `identity.role.created` domain event
 * via SNS.
 *
 * @param role - Partial role data containing at minimum the role `name`.
 * @returns Promise resolving to an {@link ApiResponse} containing the
 *          newly created {@link ErpRole}.
 *
 * @example
 * ```ts
 * const { object: newRole } = await createRole({
 *   name: 'Editor',
 *   description: 'Can edit all records',
 * });
 * ```
 */
export async function createRole(
  role: Partial<ErpRole>,
): Promise<ApiResponse<ErpRole>> {
  return post<ErpRole>('/identity/roles', role);
}

/**
 * Updates an existing role.
 *
 * Replaces `SecurityManager.SaveRole()` for existing roles. Only fields
 * present in the partial role object are updated; omitted fields retain
 * their values. System roles (Administrator, Regular, Guest) may have
 * restrictions on which fields can be modified.
 *
 * @param roleId - UUID string of the role to update.
 * @param role   - Partial role data with fields to update.
 * @returns Promise resolving to an {@link ApiResponse} containing the
 *          updated {@link ErpRole}.
 *
 * @example
 * ```ts
 * const { object: updated } = await updateRole(
 *   '3fa85f64-5717-4562-b3fc-2c963f66afa6',
 *   { description: 'Updated description' },
 * );
 * ```
 */
export async function updateRole(
  roleId: string,
  role: Partial<ErpRole>,
): Promise<ApiResponse<ErpRole>> {
  return put<ErpRole>(
    `/identity/roles/${encodeURIComponent(roleId)}`,
    role,
  );
}

/**
 * Deletes a role from the system.
 *
 * Replaces `SecurityManager.DeleteRole()` which removed the role entity
 * record and cleaned up user-role associations. In the serverless
 * architecture the Identity Lambda removes the role from DynamoDB and
 * publishes an `identity.role.deleted` domain event via SNS for downstream
 * cleanup.
 *
 * System roles (Administrator, Regular, Guest) cannot be deleted — the
 * server returns an error response.
 *
 * @param roleId - UUID string of the role to delete.
 * @returns Promise resolving to an {@link ApiResponse} with a `void`
 *          payload (success / error status only).
 *
 * @example
 * ```ts
 * const { success, message } = await deleteRole(
 *   '3fa85f64-5717-4562-b3fc-2c963f66afa6',
 * );
 * ```
 */
export async function deleteRole(
  roleId: string,
): Promise<ApiResponse<void>> {
  return del<void>(`/identity/roles/${encodeURIComponent(roleId)}`);
}
