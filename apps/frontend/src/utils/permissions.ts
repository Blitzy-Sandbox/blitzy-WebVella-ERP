/**
 * Permission Checking Utilities — WebVella ERP Frontend
 *
 * Client-side permission evaluation for UI rendering decisions. These are
 * advisory helpers that control component visibility and field access modes.
 * Actual security enforcement happens at the API Gateway / Lambda level.
 *
 * Sources:
 *   - WebVella.Erp/Api/SecurityContext.cs         (HasEntityPermission, IsUserInRole, HasMetaPermission)
 *   - WebVella.Erp/Api/Definitions.cs             (SystemIds, EntityPermission enum)
 *   - WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs (WvFieldAccess mapping, lines 599-620)
 *   - WebVella.Erp.Plugins.SDK/Pages/entity/data-create.cshtml.cs (GetFieldAccess, lines 127-159)
 *   - WebVella.Erp.Web/Services/RenderService.cs  (erp-allow-roles/erp-block-roles, lines 211-268)
 *
 * Rules:
 *   - All functions are pure — no side effects, no API calls, no state mutations
 *   - All parameters and return types are fully typed (TypeScript strict mode)
 *   - Role IDs are lowercase GUID strings (matching Cognito group mapping)
 *   - Named exports only — no default export
 */

import {
  SYSTEM_USER_ID,
  ADMINISTRATOR_ROLE_ID,
  GUEST_ROLE_ID,
  REGULAR_ROLE_ID,
} from './constants';

// =============================================================================
// Phase 1: FieldAccess Enum (maps WvFieldAccess from WebVella.TagHelpers)
// =============================================================================

/**
 * Field access levels controlling how a field renders in the UI.
 *
 * Maps to the C# `WvFieldAccess` enum used throughout PcFieldBase.cs and SDK
 * pages. The access level is determined per-field by combining entity-level
 * record permissions with field-level security permissions.
 *
 * - Full        — Read/write access (field is editable)
 * - ReadOnly    — Read-only access (field is visible but not editable)
 * - Forbidden   — No access (field is hidden)
 * - FullAndCreate — Full access plus ability to create new related records
 */
export enum FieldAccess {
  Full = 'full',
  ReadOnly = 'readonly',
  Forbidden = 'forbidden',
  FullAndCreate = 'full_and_create',
}

// =============================================================================
// Phase 2: Entity Permission Types (from SecurityContext.cs lines 63-107)
// =============================================================================

/**
 * Entity-level record permissions. Each array contains the role IDs
 * (lowercase GUID strings) that are granted the corresponding permission.
 *
 * Maps to the C# `RecordPermissions` class on `Entity` objects:
 *   entity.RecordPermissions.CanRead   → canRead
 *   entity.RecordPermissions.CanCreate → canCreate
 *   entity.RecordPermissions.CanUpdate → canUpdate
 *   entity.RecordPermissions.CanDelete → canDelete
 */
export interface EntityPermissions {
  /** Role IDs that can read records of this entity */
  canRead: string[];
  /** Role IDs that can create records of this entity */
  canCreate: string[];
  /** Role IDs that can update records of this entity */
  canUpdate: string[];
  /** Role IDs that can delete records of this entity */
  canDelete: string[];
}

/**
 * Permission type discriminator matching the C# `EntityPermission` enum.
 * Used as the `permission` parameter in `hasEntityPermission`.
 */
export type EntityPermissionType = 'read' | 'create' | 'update' | 'delete';

// =============================================================================
// Phase 3: Role Checking (from SecurityContext.cs lines 45-61)
// =============================================================================

/**
 * Check whether a user holds any of the required roles.
 *
 * Replaces `SecurityContext.IsUserInRole(params Guid[] roles)` (lines 54-61):
 * ```csharp
 * return currentUser.Roles.Any(x => roles.Any(z => z == x.Id));
 * ```
 *
 * @param userRoles  - Array of role IDs from the current user's JWT claims
 * @param requiredRoles - Array of role IDs to check against
 * @returns `true` if any user role matches any required role
 */
export function isUserInRole(
  userRoles: string[],
  requiredRoles: string[],
): boolean {
  if (userRoles.length === 0 || requiredRoles.length === 0) {
    return false;
  }

  // Normalize to lowercase for case-insensitive GUID comparison
  const normalizedUserRoles = userRoles.map((r) => r.toLowerCase());
  const normalizedRequired = requiredRoles.map((r) => r.toLowerCase());

  return normalizedUserRoles.some((userRole) =>
    normalizedRequired.includes(userRole),
  );
}

/**
 * Check whether the user has the Administrator role.
 *
 * Derived from `SecurityContext.HasMetaPermission` (line 117):
 * ```csharp
 * return user.Roles.Any(x => x.Id == SystemIds.AdministratorRoleId);
 * ```
 *
 * @param userRoles - Array of role IDs from the current user's JWT claims
 * @returns `true` if the user holds the administrator role
 */
export function isAdministrator(userRoles: string[]): boolean {
  return userRoles.some(
    (role) => role.toLowerCase() === ADMINISTRATOR_ROLE_ID,
  );
}

/**
 * Check whether the user is a guest (holds only the Guest role).
 *
 * A guest is defined as a user whose sole role is the Guest role.
 * This matches the monolith's behavior where unauthenticated users
 * are assigned only the guest role for fallback permission checks
 * (SecurityContext.cs lines 93-101).
 *
 * @param userRoles - Array of role IDs from the current user's JWT claims
 * @returns `true` if the user holds only the guest role
 */
export function isGuest(userRoles: string[]): boolean {
  if (userRoles.length === 0) {
    return true; // No roles at all implies guest
  }

  return (
    userRoles.length === 1 &&
    userRoles[0].toLowerCase() === GUEST_ROLE_ID
  );
}

// =============================================================================
// Phase 4: Entity Permission Checking (from SecurityContext.cs lines 63-107)
// =============================================================================

/**
 * Map an `EntityPermissionType` to the corresponding `EntityPermissions` array.
 * Internal helper to avoid repetitive switch/if-else in permission checks.
 */
function getPermissionList(
  permission: EntityPermissionType,
  entityPermissions: EntityPermissions,
): string[] {
  switch (permission) {
    case 'read':
      return entityPermissions.canRead;
    case 'create':
      return entityPermissions.canCreate;
    case 'update':
      return entityPermissions.canUpdate;
    case 'delete':
      return entityPermissions.canDelete;
  }
}

/**
 * Check whether a user (or guest) has a specific entity-level permission.
 *
 * Replicates `SecurityContext.HasEntityPermission` (lines 63-107):
 * 1. System user (SYSTEM_USER_ID) always returns `true` (unlimited — line 74)
 * 2. Authenticated user: checks if any user role is present in the entity's
 *    permission list for the requested permission type (lines 77-89)
 * 3. Guest fallback: if no `userId` is provided, checks whether the Guest
 *    role is present in the entity's permission list (lines 93-105)
 *
 * @param permission        - The type of permission to check
 * @param entityPermissions - The entity's record-level permission arrays
 * @param userRoles         - Array of role IDs from the user's JWT claims
 * @param userId            - Optional user ID; `null`/`undefined` triggers guest check
 * @returns `true` if the user (or guest) has the requested permission
 */
export function hasEntityPermission(
  permission: EntityPermissionType,
  entityPermissions: EntityPermissions,
  userRoles: string[],
  userId?: string | null,
): boolean {
  // System user has unlimited permissions (SecurityContext.cs line 74-75)
  if (userId != null && userId.toLowerCase() === SYSTEM_USER_ID) {
    return true;
  }

  const permissionList = getPermissionList(permission, entityPermissions);
  const normalizedPermissions = permissionList.map((p) => p.toLowerCase());

  // Authenticated user: check role intersection (lines 77-89)
  if (userId != null && userRoles.length > 0) {
    const normalizedUserRoles = userRoles.map((r) => r.toLowerCase());
    return normalizedUserRoles.some((role) =>
      normalizedPermissions.includes(role),
    );
  }

  // Guest fallback: check if GUEST_ROLE_ID is in the permission list (lines 93-105)
  return normalizedPermissions.includes(GUEST_ROLE_ID);
}

// =============================================================================
// Phase 5: Field Access Determination (from PcFieldBase.cs + data-create.cshtml.cs)
// =============================================================================

/**
 * Parameters for field access determination.
 */
interface GetFieldAccessParams {
  /** The field descriptor. `system` fields are always read-only. */
  field: { system?: boolean; name: string };
  /** Entity-level record permissions containing role ID arrays */
  entityPermissions: EntityPermissions;
  /** Role IDs from the current user's JWT claims */
  userRoles: string[];
}

/**
 * Determine the access level for a specific field based on entity permissions
 * and the user's roles.
 *
 * Combines logic from two source files:
 *
 * **data-create.cshtml.cs** `GetFieldAccess` (lines 127-159):
 * - If `field.system === true` → ReadOnly (system fields can't be edited by users)
 * - Security-enabled fields check canUpdate/canRead against user roles
 *
 * **PcFieldBase.cs** (lines 599-620):
 * ```csharp
 * if (canUpdate) model.Access = WvFieldAccess.Full;
 * else if (canRead) model.Access = WvFieldAccess.ReadOnly;
 * else model.Access = WvFieldAccess.Forbidden;
 * ```
 *
 * Additional rule from the AAP: administrators always get Full access.
 *
 * @param params - Field descriptor, entity permissions, and user roles
 * @returns The computed `FieldAccess` level for this field
 */
export function getFieldAccess(params: GetFieldAccessParams): FieldAccess {
  const { field, entityPermissions, userRoles } = params;

  // System fields are always read-only (data-create.cshtml.cs concept:
  // system fields like "id" cannot be edited by any user)
  if (field.system === true) {
    return FieldAccess.ReadOnly;
  }

  // Administrators always get full access (SecurityContext.cs line 117 pattern)
  if (isAdministrator(userRoles)) {
    return FieldAccess.Full;
  }

  // Check entity-level update permission for this user's roles
  const normalizedUserRoles = userRoles.map((r) => r.toLowerCase());
  const canUpdate = entityPermissions.canUpdate
    .map((p) => p.toLowerCase())
    .some((perm) => normalizedUserRoles.includes(perm));

  if (canUpdate) {
    return FieldAccess.Full;
  }

  // Check entity-level read permission for this user's roles
  const canRead = entityPermissions.canRead
    .map((p) => p.toLowerCase())
    .some((perm) => normalizedUserRoles.includes(perm));

  if (canRead) {
    return FieldAccess.ReadOnly;
  }

  // No matching permissions → forbidden
  return FieldAccess.Forbidden;
}

// =============================================================================
// Phase 6: Template Role-Based Content Gating (from RenderService.cs lines 211-268)
// =============================================================================

/**
 * Well-known role name → ID mapping for resolving template role names
 * (e.g., "admin", "regular", "guest") to their GUID identifiers.
 *
 * Used by `shouldShowForRoles` to support the monolith's template-based
 * content gating pattern where role names are used:
 * `{{erp-allow-roles="admin,regular"}}` or `{{erp-block-roles="guest"}}`
 */
const WELL_KNOWN_ROLE_MAP: Record<string, string> = {
  administrator: ADMINISTRATOR_ROLE_ID,
  admin: ADMINISTRATOR_ROLE_ID,
  regular: REGULAR_ROLE_ID,
  guest: GUEST_ROLE_ID,
};

/**
 * Resolve a role reference (name or ID) to a normalized lowercase GUID.
 * Well-known role names ("admin", "regular", "guest") are mapped to their
 * canonical GUIDs. Unknown values are returned as-is (lowercased).
 */
function resolveRoleRef(ref: string): string {
  const normalized = ref.toLowerCase();
  return WELL_KNOWN_ROLE_MAP[normalized] ?? normalized;
}

/**
 * Determine whether content should be shown based on role-based content
 * gating rules.
 *
 * Replaces the monolith's template directives:
 * - `{{erp-allow-roles="admin,regular"}}` → Show only for allowed roles
 * - `{{erp-block-roles="guest"}}`         → Hide from blocked roles
 *
 * Logic from RenderService.cs lines 211-268:
 * 1. If `allowedRoles` is non-empty, user must have at least one matching
 *    role (by ID or by name) to see the content
 * 2. If `blockedRoles` is non-empty, user must NOT have any blocked role
 * 3. Both lists can be applied simultaneously (allow takes precedence:
 *    if user is in allowed, they see content even if also in blocked)
 *
 * Role matching supports both role IDs (GUIDs) and role names for
 * flexibility with the template-based content gating pattern. Well-known
 * role names ("admin", "administrator", "regular", "guest") are resolved
 * to their canonical GUID identifiers before matching.
 *
 * @param allowedRoles - Role IDs or names that are allowed to see the content
 * @param blockedRoles - Role IDs or names that are blocked from seeing the content
 * @param userRoles    - Current user's roles with both ID and name
 * @returns `true` if the content should be rendered for this user
 */
export function shouldShowForRoles(
  allowedRoles: string[],
  blockedRoles: string[],
  userRoles: Array<{ id: string; name: string }>,
): boolean {
  // No gating rules → always show
  if (allowedRoles.length === 0 && blockedRoles.length === 0) {
    return true;
  }

  // Build normalized sets for efficient lookup
  const userRoleIds = userRoles.map((r) => r.id.toLowerCase());
  const userRoleNames = userRoles.map((r) => r.name.toLowerCase());

  // Guest check: if no user roles (unauthenticated), treat as guest
  const isGuestUser = userRoles.length === 0;

  // Allow-list check: user must have at least one allowed role
  if (allowedRoles.length > 0) {
    const resolvedAllowed = allowedRoles.map(resolveRoleRef);

    if (isGuestUser) {
      // Guest user: check if guest role ID is in resolved allowedRoles
      return resolvedAllowed.includes(GUEST_ROLE_ID);
    }

    const hasAllowedRole = resolvedAllowed.some(
      (allowed) =>
        userRoleIds.includes(allowed) || userRoleNames.includes(allowed),
    );

    return hasAllowedRole;
  }

  // Block-list check: user must NOT have any blocked role
  if (blockedRoles.length > 0) {
    const resolvedBlocked = blockedRoles.map(resolveRoleRef);

    if (isGuestUser) {
      // Guest user: check if guest role ID is in resolved blockedRoles
      return !resolvedBlocked.includes(GUEST_ROLE_ID);
    }

    const hasBlockedRole = resolvedBlocked.some(
      (blocked) =>
        userRoleIds.includes(blocked) || userRoleNames.includes(blocked),
    );

    return !hasBlockedRole;
  }

  return true;
}

// =============================================================================
// Phase 7: Component Visibility Helpers
// =============================================================================

/**
 * Check whether a field should be visible to the user at all.
 *
 * A field is visible if its access level is anything other than `Forbidden`.
 * This maps to the monolith's behavior where `WvFieldAccess.Forbidden`
 * hides the field completely from the rendered page.
 *
 * @param fieldAccess - The computed field access level
 * @returns `true` if the field should be rendered (visible)
 */
export function canViewField(fieldAccess: FieldAccess): boolean {
  return (
    fieldAccess === FieldAccess.Full ||
    fieldAccess === FieldAccess.FullAndCreate ||
    fieldAccess === FieldAccess.ReadOnly
  );
}

/**
 * Check whether a field should be editable by the user.
 *
 * A field is editable if its access level grants write permissions.
 * This controls whether form inputs are enabled or disabled.
 *
 * @param fieldAccess - The computed field access level
 * @returns `true` if the field should be editable
 */
export function canEditField(fieldAccess: FieldAccess): boolean {
  return (
    fieldAccess === FieldAccess.Full ||
    fieldAccess === FieldAccess.FullAndCreate
  );
}

/**
 * Check whether the user can create related records through this field.
 *
 * Only `FullAndCreate` access level grants the ability to create new
 * related records. This is used for relation-type fields where a "create new"
 * button may be shown alongside the relation lookup.
 *
 * @param fieldAccess - The computed field access level
 * @returns `true` only if the field has `FullAndCreate` access
 */
export function canCreateRelated(fieldAccess: FieldAccess): boolean {
  return fieldAccess === FieldAccess.FullAndCreate;
}

// =============================================================================
// Phase 8: Meta Permission Checking (from SecurityContext.cs line 109-118)
// =============================================================================

/**
 * Check whether the user has meta-level permissions (entity/field/relation CRUD).
 *
 * Replaces `SecurityContext.HasMetaPermission` (lines 109-118):
 * ```csharp
 * return user.Roles.Any(x => x.Id == SystemIds.AdministratorRoleId);
 * ```
 *
 * Only administrators can modify the entity schema (create/update/delete
 * entities, fields, and relations). This is a UI gate — actual enforcement
 * is at the Entity Management service Lambda level.
 *
 * @param userRoles - Array of role IDs from the current user's JWT claims
 * @returns `true` if the user has administrator-level meta permissions
 */
export function hasMetaPermission(userRoles: string[]): boolean {
  return isAdministrator(userRoles);
}
