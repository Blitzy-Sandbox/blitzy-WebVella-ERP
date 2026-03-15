/**
 * Plugin System TanStack Query Hooks
 *
 * TanStack Query 5 hooks for plugin/extension system management.
 * Replaces the monolith's `ErpPlugin` abstract base discovery/registration
 * mechanism and `SdkPlugin` admin operations with API calls to the Plugin
 * System microservice at `/v1/plugins/*`.
 *
 * Source mapping:
 *  - ErpPlugin.cs metadata properties → Plugin interface
 *  - ErpPlugin.GetPluginData() → usePluginData query
 *  - ErpPlugin.SavePluginData() → useSavePluginData mutation
 *  - SdkPlugin / AdminController CRUD → useRegisterPlugin, useUpdatePlugin,
 *    useDeletePlugin mutations
 *  - Reflection-based ErpPlugin discovery → usePlugins query
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type { ApiResponse } from '../api/client';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/**
 * Plugin metadata interface.
 *
 * Mirrors the `[JsonProperty]` attributes from the monolith's `ErpPlugin.cs`:
 *   Name, Prefix, Url, Description, Version, Company, CompanyUrl, Author,
 *   Repository, License, SettingsUrl, PluginPageUrl, IconUrl.
 *
 * The `id` field is the primary key assigned by the Plugin System service
 * (replaces the in-memory GUID generated during monolith reflection discovery).
 */
export interface Plugin {
  /** Unique identifier for the plugin registration. */
  id: string;
  /** Human-readable plugin name (e.g. "sdk", "crm", "mail"). */
  name: string;
  /** Short prefix used for namespacing (e.g. route prefixes, entity prefixes). */
  prefix: string;
  /** Plugin homepage or documentation URL. */
  url: string;
  /** Brief description of the plugin's functionality. */
  description: string;
  /** Semantic version string (e.g. "1.0.0"). */
  version: string;
  /** Company / organization that owns the plugin. */
  company: string;
  /** Company website URL. */
  companyUrl: string;
  /** Primary author / maintainer name. */
  author: string;
  /** Source code repository URL. */
  repository: string;
  /** License identifier (e.g. "MIT", "Apache-2.0"). */
  license: string;
  /** URL to the plugin-specific settings page. */
  settingsUrl: string;
  /** URL to the plugin's dedicated admin page. */
  pluginPageUrl: string;
  /** URL or path for the plugin's icon / logo. */
  iconUrl: string;
}

/**
 * Payload for registering a new plugin.
 * Omits `id` because the server assigns it.
 */
interface RegisterPluginPayload extends Omit<Plugin, 'id'> {}

/**
 * Payload for updating an existing plugin's metadata.
 * Requires `id` to identify the target plugin; all metadata fields
 * are updatable.
 */
interface UpdatePluginPayload extends Plugin {}

/**
 * Payload for saving plugin-specific configuration data.
 * The `data` field is an opaque JSON string — the Plugin System service
 * persists it as-is, matching the monolith's `plugin_data.data` column.
 */
interface SavePluginDataPayload {
  /** Plugin name identifying which plugin's data to save. */
  pluginName: string;
  /** Opaque JSON configuration string. */
  data: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Base API path for plugin endpoints. */
const PLUGINS_BASE = '/plugins';

/**
 * Stale time for plugin list/detail queries (10 minutes).
 * Plugins change very rarely after initial registration, so aggressive
 * caching is appropriate — mirrors the monolith's static reflection-based
 * plugin discovery which only ran at startup.
 */
const PLUGINS_STALE_TIME = 10 * 60 * 1000;

// ---------------------------------------------------------------------------
// Query key factory
// ---------------------------------------------------------------------------

/**
 * Centralized query key factory for consistent cache management.
 * Follows the TanStack Query recommended pattern of hierarchical keys
 * so that `invalidateQueries({ queryKey: pluginKeys.all })` cascades
 * to plugin detail and data queries when needed.
 */
const pluginKeys = {
  /** Root key for all plugin queries. */
  all: ['plugins'] as const,
  /** Key for a single plugin by ID. */
  detail: (id: string) => ['plugins', id] as const,
  /** Key for a plugin's configuration data by name. */
  data: (pluginName: string) => ['plugins', pluginName, 'data'] as const,
};

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches all registered plugins.
 *
 * Replaces the monolith's reflection-based `ErpPlugin` discovery listing
 * that enumerated all `ErpPlugin` subclasses at startup and exposed them
 * via `IErpService.Plugins`.
 *
 * @returns TanStack Query result with `data.object` containing `Plugin[]`.
 *
 * @example
 * ```tsx
 * const { data, isLoading } = usePlugins();
 * const plugins = data?.object ?? [];
 * ```
 */
export function usePlugins() {
  return useQuery<ApiResponse<Plugin[]>>({
    queryKey: pluginKeys.all,
    queryFn: () => get<Plugin[]>(PLUGINS_BASE),
    staleTime: PLUGINS_STALE_TIME,
  });
}

/**
 * Fetches a single plugin by its ID.
 *
 * The query is automatically disabled when `id` is falsy, preventing
 * unnecessary network requests before the caller has a valid ID.
 *
 * @param id - The plugin's unique identifier.
 * @returns TanStack Query result with `data.object` containing a `Plugin`.
 *
 * @example
 * ```tsx
 * const { data, isLoading } = usePlugin(selectedPluginId);
 * const plugin = data?.object;
 * ```
 */
export function usePlugin(id: string) {
  return useQuery<ApiResponse<Plugin>>({
    queryKey: pluginKeys.detail(id),
    queryFn: () => get<Plugin>(`${PLUGINS_BASE}/${encodeURIComponent(id)}`),
    enabled: Boolean(id),
  });
}

/**
 * Fetches plugin-specific configuration data by plugin name.
 *
 * Replaces `ErpPlugin.GetPluginData()` which read from the `plugin_data`
 * table via raw SQL: `SELECT data FROM plugin_data WHERE plugin_name = @name`.
 *
 * The returned `data` is an opaque JSON string — the Plugin System service
 * stores it verbatim without interpreting its contents.
 *
 * @param pluginName - The plugin's name (e.g. "sdk", "crm").
 * @returns TanStack Query result with `data.object` containing the
 *          plugin's configuration data as a string.
 *
 * @example
 * ```tsx
 * const { data } = usePluginData('sdk');
 * const config = data?.object ? JSON.parse(data.object) : {};
 * ```
 */
export function usePluginData(pluginName: string) {
  return useQuery<ApiResponse<string>>({
    queryKey: pluginKeys.data(pluginName),
    queryFn: () =>
      get<string>(`${PLUGINS_BASE}/${encodeURIComponent(pluginName)}/data`),
    enabled: Boolean(pluginName),
  });
}

// ---------------------------------------------------------------------------
// Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Registers a new plugin with the Plugin System service.
 *
 * Replaces the monolith's reflection-based plugin discovery — in the
 * microservices architecture, plugins self-register via this API endpoint
 * rather than being discovered at startup via assembly scanning.
 *
 * On success, invalidates the `['plugins']` query so the plugin list
 * refetches and includes the newly registered plugin.
 *
 * @returns TanStack Mutation result with `mutate` / `mutateAsync`.
 *
 * @example
 * ```tsx
 * const { mutate, isPending } = useRegisterPlugin();
 * mutate({ name: 'my-plugin', prefix: 'mp', version: '1.0.0', ... });
 * ```
 */
export function useRegisterPlugin() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<Plugin>, Error, RegisterPluginPayload>({
    mutationFn: (payload: RegisterPluginPayload) =>
      post<Plugin>(PLUGINS_BASE, payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: pluginKeys.all });
    },
  });
}

/**
 * Updates an existing plugin's metadata.
 *
 * On success, invalidates both the plugin list query (`['plugins']`) and
 * the specific plugin detail query (`['plugins', id]`) to ensure all
 * consumers see the updated metadata.
 *
 * @returns TanStack Mutation result with `mutate` / `mutateAsync`.
 *
 * @example
 * ```tsx
 * const { mutate } = useUpdatePlugin();
 * mutate({ id: 'abc-123', name: 'updated-name', ... });
 * ```
 */
export function useUpdatePlugin() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<Plugin>, Error, UpdatePluginPayload>({
    mutationFn: (plugin: UpdatePluginPayload) =>
      put<Plugin>(
        `${PLUGINS_BASE}/${encodeURIComponent(plugin.id)}`,
        plugin,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: pluginKeys.all });
      queryClient.invalidateQueries({
        queryKey: pluginKeys.detail(variables.id),
      });
    },
  });
}

/**
 * Unregisters (deletes) a plugin from the Plugin System service.
 *
 * On success, invalidates the `['plugins']` query so the plugin list
 * refetches without the deleted plugin.
 *
 * @returns TanStack Mutation result with `mutate` / `mutateAsync`.
 *          The mutation variable is the plugin ID string.
 *
 * @example
 * ```tsx
 * const { mutate } = useDeletePlugin();
 * mutate('abc-123');
 * ```
 */
export function useDeletePlugin() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<void>, Error, string>({
    mutationFn: (id: string) =>
      del<void>(`${PLUGINS_BASE}/${encodeURIComponent(id)}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: pluginKeys.all });
    },
  });
}

/**
 * Saves plugin-specific configuration data.
 *
 * Replaces `ErpPlugin.SavePluginData(string data)` which performed an
 * INSERT or UPDATE on the `plugin_data` table:
 * ```sql
 * INSERT INTO plugin_data(plugin_name, data) VALUES(@name, @data)
 * ON CONFLICT(plugin_name) DO UPDATE SET data = @data;
 * ```
 *
 * The `data` field is an opaque JSON string — the Plugin System service
 * persists it verbatim.
 *
 * On success, invalidates the specific plugin data query
 * (`['plugins', pluginName, 'data']`) so consumers see the updated data.
 *
 * @returns TanStack Mutation result with `mutate` / `mutateAsync`.
 *
 * @example
 * ```tsx
 * const { mutate } = useSavePluginData();
 * mutate({ pluginName: 'sdk', data: JSON.stringify({ key: 'value' }) });
 * ```
 */
export function useSavePluginData() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<void>, Error, SavePluginDataPayload>({
    mutationFn: ({ pluginName, data }: SavePluginDataPayload) =>
      put<void>(
        `${PLUGINS_BASE}/${encodeURIComponent(pluginName)}/data`,
        { data },
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: pluginKeys.data(variables.pluginName),
      });
    },
  });
}
