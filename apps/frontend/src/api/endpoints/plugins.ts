/**
 * Plugin Management API Module
 *
 * Typed API functions for the Plugin/Extension System bounded-context service.
 * Replaces the monolith's WebApiController.cs GetPlugins endpoint (line 3403)
 * and plugin registry management via ErpPlugin.cs / IErpService.Plugins.
 *
 * All operations route to the Plugin System Lambda through API Gateway
 * at the `/plugin-system/plugins` path prefix.
 */

import { get, post, put } from '../client';
import type { ApiResponse } from '../client';

// ---------------------------------------------------------------------------
// Route prefix — all plugin operations share this base path
// ---------------------------------------------------------------------------

const PLUGINS_BASE = '/plugin-system/plugins';

// ---------------------------------------------------------------------------
// Type definitions
// ---------------------------------------------------------------------------

/**
 * Read-only representation of a registered plugin returned by the
 * Plugin System service.
 *
 * Maps from the monolith's ErpPlugin abstract class properties
 * (Name, Description, Version, Patch, Author) extended with
 * microservice-specific lifecycle fields (id, isActive, installedOn,
 * lastUpdated).
 */
export interface PluginInfo {
  /** Unique plugin identifier (UUID) */
  id: string;

  /** Human-readable plugin name (ErpPlugin.Name) */
  name: string;

  /** Plugin description (ErpPlugin.Description) */
  description: string;

  /** Semantic version string (e.g. "1.0.0") — was int Version in monolith */
  version: string;

  /** Current patch level (ErpPlugin.Patch — abstract int in monolith) */
  patch: number;

  /** Plugin author (ErpPlugin.Author) */
  author: string;

  /** Whether the plugin is currently active in the system */
  isActive: boolean;

  /** ISO 8601 datetime when the plugin was first installed */
  installedOn: string;

  /** ISO 8601 datetime of the most recent update */
  lastUpdated: string;
}

/**
 * Payload for registering or updating a plugin in the Plugin System.
 *
 * Mirrors the subset of ErpPlugin properties that are user-provided
 * during plugin registration, plus an optional configuration map for
 * plugin-specific settings (replaces the monolith's plugin_data JSON
 * blob stored via ErpPlugin.SavePluginData).
 */
export interface PluginRegistration {
  /** Plugin name — required for registration */
  name: string;

  /** Plugin description */
  description: string;

  /** Semantic version string */
  version: string;

  /** Plugin author — optional during registration */
  author?: string;

  /** Arbitrary plugin configuration (replaces plugin_data JSON) */
  configuration?: Record<string, unknown>;
}

// ---------------------------------------------------------------------------
// API functions
// ---------------------------------------------------------------------------

/**
 * List all registered plugins.
 *
 * Replaces the monolith's `GetPlugins` endpoint
 * (WebApiController.cs line 3403, GET api/v3/en_US/plugin/list)
 * which returned `erpService.Plugins` wrapped in a ResponseModel
 * with Success=true and Timestamp=UTC.
 *
 * The source endpoint required `[Authorize(Roles = "administrator")]` —
 * authorization is enforced at the API Gateway / Lambda authorizer level
 * in the target architecture.
 *
 * @returns Promise resolving to an ApiResponse containing an array of PluginInfo
 */
export async function listPlugins(): Promise<ApiResponse<PluginInfo[]>> {
  return get<PluginInfo[]>(PLUGINS_BASE);
}

/**
 * Get detailed information about a single plugin by its identifier.
 *
 * @param pluginId - UUID of the plugin to retrieve
 * @returns Promise resolving to an ApiResponse containing the PluginInfo
 */
export async function getPlugin(
  pluginId: string,
): Promise<ApiResponse<PluginInfo>> {
  return get<PluginInfo>(`${PLUGINS_BASE}/${encodeURIComponent(pluginId)}`);
}

/**
 * Register a new plugin in the Plugin System.
 *
 * In the monolith, plugin registration was handled by the reflection-based
 * `HookManager` discovering `ErpPlugin` subclasses at startup and calling
 * `ErpPlugin.Initialize()`. In the target microservices architecture,
 * plugins are registered explicitly via this API endpoint.
 *
 * @param plugin - Plugin registration payload
 * @returns Promise resolving to an ApiResponse containing the created PluginInfo
 */
export async function registerPlugin(
  plugin: PluginRegistration,
): Promise<ApiResponse<PluginInfo>> {
  return post<PluginInfo>(PLUGINS_BASE, plugin);
}

/**
 * Update metadata for an existing plugin.
 *
 * Accepts a partial PluginRegistration — only the provided fields are
 * updated. This mirrors the monolith's `ErpPlugin.SavePluginData()` flow
 * where plugin metadata was persisted to the `plugin_data` table as JSON.
 *
 * @param pluginId - UUID of the plugin to update
 * @param data     - Partial registration payload with fields to update
 * @returns Promise resolving to an ApiResponse containing the updated PluginInfo
 */
export async function updatePlugin(
  pluginId: string,
  data: Partial<PluginRegistration>,
): Promise<ApiResponse<PluginInfo>> {
  return put<PluginInfo>(
    `${PLUGINS_BASE}/${encodeURIComponent(pluginId)}`,
    data,
  );
}

/**
 * Deactivate a currently active plugin.
 *
 * Sets `isActive = false` on the plugin record. In the monolith, there
 * was no explicit deactivation — plugins were always active once loaded.
 * The target architecture supports runtime activation/deactivation.
 *
 * @param pluginId - UUID of the plugin to deactivate
 * @returns Promise resolving to an ApiResponse containing the updated PluginInfo
 */
export async function deactivatePlugin(
  pluginId: string,
): Promise<ApiResponse<PluginInfo>> {
  return post<PluginInfo>(
    `${PLUGINS_BASE}/${encodeURIComponent(pluginId)}/deactivate`,
    {},
  );
}

/**
 * Activate a currently inactive plugin.
 *
 * Sets `isActive = true` on the plugin record and triggers the plugin's
 * initialization sequence (equivalent to the monolith's
 * `ErpPlugin.Initialize()` call during startup).
 *
 * @param pluginId - UUID of the plugin to activate
 * @returns Promise resolving to an ApiResponse containing the updated PluginInfo
 */
export async function activatePlugin(
  pluginId: string,
): Promise<ApiResponse<PluginInfo>> {
  return post<PluginInfo>(
    `${PLUGINS_BASE}/${encodeURIComponent(pluginId)}/activate`,
    {},
  );
}
