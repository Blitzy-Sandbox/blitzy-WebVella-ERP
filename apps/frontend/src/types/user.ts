/**
 * User, Role, and Authentication TypeScript Interfaces
 *
 * Converts C# DTOs from the WebVella ERP monolith into TypeScript interfaces
 * for the React SPA frontend. Source models:
 *   - WebVella.Erp/Api/Models/ErpRole.cs
 *   - WebVella.Erp/Api/Models/UserComponentUsage.cs
 *   - WebVella.Erp/Api/Models/ErpUserPreferences.cs
 *   - WebVella.Erp/Api/Models/ErpUser.cs
 *   - WebVella.Erp.Web/Models/JwtTokenModels.cs
 *   - WebVella.Erp/Api/Definitions.cs (SystemIds reference)
 *
 * Mapping rules applied:
 *   - C# Guid → TypeScript string (UUID format)
 *   - C# DateTime → TypeScript string (ISO 8601 format)
 *   - C# [JsonIgnore] properties are excluded from frontend types
 *   - C# snake_case JSON property names → TypeScript camelCase
 *   - C# computed property is_admin → TypeScript isAdmin
 */

// ---------------------------------------------------------------------------
// Role Model
// ---------------------------------------------------------------------------

/**
 * Represents a system role for authorization and access control.
 *
 * Mapped from C# ErpRole (WebVella.Erp/Api/Models/ErpRole.cs).
 * JSON property names: id, name, description.
 */
export interface ErpRole {
  /** Unique identifier for the role (UUID string) */
  id: string;

  /** Display name of the role (e.g., "Administrator", "Regular", "Guest") */
  name: string;

  /** Human-readable description of the role's purpose and permissions */
  description: string;
}

// ---------------------------------------------------------------------------
// User Component Usage
// ---------------------------------------------------------------------------

/**
 * Tracks SDK component usage statistics within user preferences.
 *
 * Mapped from C# UserComponentUsage (WebVella.Erp/Api/Models/UserComponentUsage.cs).
 * JSON property names: name, sdk_used, sdk_used_on.
 */
export interface UserComponentUsage {
  /** Fully qualified component name identifier */
  name: string;

  /** Count of times this component was used via the SDK */
  sdkUsed: number;

  /** ISO 8601 timestamp of when the SDK was last used for this component */
  sdkUsedOn: string;
}

// ---------------------------------------------------------------------------
// User Preferences
// ---------------------------------------------------------------------------

/**
 * User preference settings including sidebar configuration, component usage
 * tracking, and per-component data storage.
 *
 * Mapped from C# ErpUserPreferences (WebVella.Erp/Api/Models/ErpUserPreferences.cs).
 * JSON property names: sidebar_size, component_usage, component_data_dictionary.
 */
export interface ErpUserPreferences {
  /** Sidebar display size preference (e.g., "sm", "md", "lg") */
  sidebarSize: string;

  /** Array of component usage tracking records */
  componentUsage: UserComponentUsage[];

  /**
   * Dictionary of component data keyed by full component name.
   * Each value is a generic record representing an EntityRecord from the
   * monolith's dynamic entity system.
   */
  componentDataDictionary: Record<string, Record<string, unknown>>;
}

// ---------------------------------------------------------------------------
// User Model
// ---------------------------------------------------------------------------

/**
 * Represents an authenticated ERP user as sent to the frontend client.
 *
 * Mapped from C# ErpUser (WebVella.Erp/Api/Models/ErpUser.cs).
 *
 * CRITICAL — The following C# properties are marked [JsonIgnore] and are
 * intentionally excluded from this frontend type:
 *   - Password  — Security-sensitive credential, never sent to the client
 *   - Enabled   — Server-only account state flag
 *   - Verified  — Server-only verification state flag
 *   - Roles     — Server-only role list (isAdmin is computed from Roles
 *                  server-side and IS serialized to the client)
 *
 * JSON property names: id, username, email, firstName, lastName, image,
 *   createdOn, lastLoggedIn, is_admin, preferences.
 */
export interface ErpUser {
  /** Unique identifier for the user (UUID string) */
  id: string;

  /** Username for display purposes */
  username: string;

  /** User's email address, also used as the login identifier */
  email: string;

  /** User's first name */
  firstName: string;

  /** User's last name */
  lastName: string;

  /** URL or path to the user's profile image */
  image: string;

  /** ISO 8601 timestamp of when the user account was created */
  createdOn: string;

  /**
   * ISO 8601 timestamp of the user's most recent login.
   * Nullable — will be null if the user has never logged in.
   */
  lastLoggedIn?: string | null;

  /**
   * Whether the user holds the Administrator role.
   * Computed server-side from the user's Roles collection
   * (checks for SystemIds.AdministratorRoleId membership).
   */
  isAdmin: boolean;

  /** User preference settings */
  preferences: ErpUserPreferences;
}

// ---------------------------------------------------------------------------
// Authentication Request / Response Types
// ---------------------------------------------------------------------------

/**
 * Request payload for user authentication (login).
 *
 * Replaces C# JwtTokenLoginModel (WebVella.Erp.Web/Models/JwtTokenModels.cs)
 * with Cognito-compatible semantics for the serverless auth flow.
 */
export interface LoginRequest {
  /** User's email address used as the login identifier */
  email: string;

  /** User's password for credential verification */
  password: string;
}

/**
 * Legacy token model maintained for backward compatibility with existing
 * API consumers during the migration period.
 *
 * Mapped from C# JwtTokenModel (WebVella.Erp.Web/Models/JwtTokenModels.cs).
 */
export interface TokenModel {
  /** JWT access token string */
  token: string;
}

/**
 * Client-side authentication state for the Zustand auth store.
 *
 * New type introduced for the Cognito-based authentication flow,
 * replacing the monolith's cookie + JWT middleware approach.
 */
export interface AuthState {
  /** Whether the user is currently authenticated with a valid session */
  isAuthenticated: boolean;

  /** Authenticated user's profile data, null when not authenticated */
  user: ErpUser | null;

  /** Current JWT access token for API authorization, null when not authenticated */
  accessToken: string | null;

  /** Current refresh token for obtaining new access tokens, null when not authenticated */
  refreshToken: string | null;

  /**
   * Unix timestamp (seconds since epoch) when the access token expires.
   * Null when the user is not authenticated.
   */
  expiresAt: number | null;
}

/**
 * Response payload returned by authentication endpoints upon successful login
 * or token refresh.
 *
 * New type introduced for the Cognito-based authentication response,
 * containing both tokens and the authenticated user's profile.
 */
export interface AuthResponse {
  /** JWT access token for API authorization (Bearer token) */
  accessToken: string;

  /** Refresh token for obtaining new access tokens without re-authentication */
  refreshToken: string;

  /** Token validity duration in seconds */
  expiresIn: number;

  /** Authenticated user's full profile data */
  user: ErpUser;
}
