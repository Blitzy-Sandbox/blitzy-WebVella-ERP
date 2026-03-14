/**
 * @file usePlugins.test.ts
 * @description Comprehensive Vitest unit tests for the 7 plugin management TanStack Query hooks
 * exported from src/hooks/usePlugins.ts. These hooks replace the monolith's ErpPlugin.cs
 * (abstract plugin base with metadata persistence to the `plugin_data` table), IErpService.cs
 * (plugin initialization contract with reflection-based discovery), and SdkPlugin.cs (SDK admin
 * console plugin) with API calls to the Plugin System microservice at `/plugins/*`.
 *
 * Test suites cover:
 *   - usePlugins        — list all registered plugins
 *   - usePlugin         — fetch a single plugin by ID
 *   - usePluginData     — fetch plugin-specific configuration data
 *   - useRegisterPlugin — register a new plugin
 *   - useUpdatePlugin   — update plugin metadata
 *   - useDeletePlugin   — unregister (delete) a plugin
 *   - useSavePluginData — persist plugin configuration data
 *
 * Monolith parity:
 *   - Plugin metadata matches ErpPlugin.cs [JsonProperty] fields exactly
 *   - Plugin data stored as opaque JSON string (from `plugin_data.data` column)
 *   - Plugin discovery via API replaces reflection-based `ErpPlugin` assembly scanning
 *   - SDK plugin fixture modelled after SdkPlugin.cs
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
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  usePlugins,
  usePlugin,
  usePluginData,
  useRegisterPlugin,
  useUpdatePlugin,
  useDeletePlugin,
  useSavePluginData,
} from '../../../src/hooks/usePlugins';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';
import type { ApiResponse } from '../../../src/api/client';

// Type-only import to validate mock response shape aligns with the shared
// server envelope contract. The success / errors / message members from
// BaseResponseModel ensure mock data mirrors the real API response structure.
import type { BaseResponseModel } from '../../../src/types/common';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures — modelled after ErpPlugin.cs [JsonProperty] attributes and
// SdkPlugin.cs (the primary admin console plugin from the monolith)
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Validates that a mock response envelope conforms to the shared
 * BaseResponseModel contract (success, errors, message). This type alias
 * ensures compile-time safety between mock data and the actual API shape.
 */
type MockEnvelopeFields = Pick<BaseResponseModel, 'success' | 'errors' | 'message'>;

/**
 * Mock plugin fixture matching ErpPlugin.cs [JsonProperty] attributes:
 *   name, prefix, url, description, version, company, companyUrl (company_url),
 *   author, repository, license, settingsUrl (settings_url),
 *   pluginPageUrl (plugin_page_url), iconUrl (icon_url).
 *
 * Modelled after SdkPlugin.cs — the SDK Admin Console plugin.
 */
const mockPlugin = {
  id: 'plugin-guid',
  name: 'WebVella.Erp.Plugins.SDK',
  prefix: 'sdk',
  url: '/sdk',
  description: 'SDK Admin Console',
  version: '1.0.0',
  company: 'WebVella',
  companyUrl: 'https://webvella.com',
  author: 'WebVella Team',
  repository: 'https://github.com/WebVella/WebVella-ERP',
  license: 'Apache-2.0',
  settingsUrl: '/sdk/settings',
  pluginPageUrl: '/sdk',
  iconUrl: '/assets/sdk-icon.png',
};

/** Second plugin fixture for list testing (CRM plugin skeleton). */
const mockPluginCrm = {
  id: 'crm-plugin-guid',
  name: 'WebVella.Erp.Plugins.Crm',
  prefix: 'crm',
  url: '/crm',
  description: 'CRM / Contacts Management',
  version: '1.0.0',
  company: 'WebVella',
  companyUrl: 'https://webvella.com',
  author: 'WebVella Team',
  repository: 'https://github.com/WebVella/WebVella-ERP',
  license: 'Apache-2.0',
  settingsUrl: '/crm/settings',
  pluginPageUrl: '/crm',
  iconUrl: '/assets/crm-icon.png',
};

/**
 * Mock plugin data — opaque JSON string matching the monolith's
 * `plugin_data.data` column storage pattern. ErpPlugin.GetPluginData()
 * returned this as a raw string; ErpPlugin.SavePluginData(data) persisted
 * it verbatim.
 */
const mockPluginData = '{"key":"value","config":{"feature1":true}}';

/**
 * Creates a mock ApiResponse envelope with fields consistent with
 * BaseResponseModel (success, errors, message). Uses the shared envelope
 * contract to ensure type safety across test boundaries.
 */
function mockApiResponse<T>(object: T): ApiResponse<T> {
  // Validate envelope fields conform to BaseResponseModel contract
  const envelope: MockEnvelopeFields = {
    success: true,
    errors: [],
    message: '',
  };
  return {
    ...envelope,
    statusCode: 200,
    timestamp: new Date().toISOString(),
    object,
  } as ApiResponse<T>;
}

/**
 * Creates an error ApiResponse envelope using BaseResponseModel's
 * success=false, errors array, and message fields.
 */
function mockErrorResponse(errorMessage: string): ApiResponse<never> {
  const envelope: MockEnvelopeFields = {
    success: false,
    errors: [{ key: 'error', value: '', message: errorMessage }],
    message: errorMessage,
  };
  return {
    ...envelope,
    statusCode: 400,
    timestamp: new Date().toISOString(),
  } as ApiResponse<never>;
}

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
// Global test lifecycle
// ──────────────────────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ══════════════════════════════════════════════════════════════════════════════
// Test Suites
// ══════════════════════════════════════════════════════════════════════════════

// --------------------------------------------------------------------------
// 1. usePlugins — List all registered plugins
// --------------------------------------------------------------------------

describe('usePlugins', () => {
  it('should fetch all plugins', async () => {
    const pluginList = [mockPlugin, mockPluginCrm];
    mockGet.mockResolvedValue(mockApiResponse(pluginList));

    const { result } = renderHook(() => usePlugins(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify the correct API endpoint was called (replaces reflection-based discovery)
    expect(mockGet).toHaveBeenCalledWith('/plugins');
    expect(mockGet).toHaveBeenCalledTimes(1);

    // Verify response data — should contain the full plugin list
    expect(result.current.data?.object).toEqual(pluginList);
    expect(result.current.data?.success).toBe(true);
    expect(result.current.data?.object).toHaveLength(2);
  });

  it('should use staleTime of 10 minutes', async () => {
    mockGet.mockResolvedValue(mockApiResponse([mockPlugin]));

    const queryClient = createTestQueryClient();
    const { result } = renderHook(() => usePlugins(), {
      wrapper: createWrapper(queryClient),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // After successful fetch, data should NOT be stale immediately — staleTime
    // is 10 minutes (600_000 ms), matching the monolith's static plugin list
    // that only changed at application startup via reflection-based discovery.
    expect(result.current.isStale).toBe(false);

    // Verify no refetch occurs when re-rendering with the same QueryClient
    const initialCallCount = mockGet.mock.calls.length;
    const { result: result2 } = renderHook(() => usePlugins(), {
      wrapper: createWrapper(queryClient),
    });

    await waitFor(() => {
      expect(result2.current.isSuccess).toBe(true);
    });

    // Should serve from cache — no additional API call
    expect(mockGet.mock.calls.length).toBe(initialCallCount);
  });

  it('should handle empty plugin list', async () => {
    mockGet.mockResolvedValue(mockApiResponse([]));

    const { result } = renderHook(() => usePlugins(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.object).toEqual([]);
    expect(result.current.data?.success).toBe(true);
  });
});

// --------------------------------------------------------------------------
// 2. usePlugin — Fetch a single plugin by ID
// --------------------------------------------------------------------------

describe('usePlugin', () => {
  it('should fetch plugin by ID', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockPlugin));

    const { result } = renderHook(() => usePlugin('plugin-guid'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/plugins/plugin-guid');
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toEqual(mockPlugin);
    expect(result.current.data?.success).toBe(true);
  });

  it('should include all metadata properties from ErpPlugin.cs JsonProperty attributes', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockPlugin));

    const { result } = renderHook(() => usePlugin('plugin-guid'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const plugin = result.current.data?.object;

    // Verify ALL 14 fields matching ErpPlugin.cs [JsonProperty] attributes:
    //   name, prefix, url, description, version, company, companyUrl (company_url),
    //   author, repository, license, settingsUrl (settings_url),
    //   pluginPageUrl (plugin_page_url), iconUrl (icon_url), plus id.
    expect(plugin).toBeDefined();
    expect(plugin?.id).toBe('plugin-guid');
    expect(plugin?.name).toBe('WebVella.Erp.Plugins.SDK');
    expect(plugin?.prefix).toBe('sdk');
    expect(plugin?.url).toBe('/sdk');
    expect(plugin?.description).toBe('SDK Admin Console');
    expect(plugin?.version).toBe('1.0.0');
    expect(plugin?.company).toBe('WebVella');
    expect(plugin?.companyUrl).toBe('https://webvella.com');
    expect(plugin?.author).toBe('WebVella Team');
    expect(plugin?.repository).toBe('https://github.com/WebVella/WebVella-ERP');
    expect(plugin?.license).toBe('Apache-2.0');
    expect(plugin?.settingsUrl).toBe('/sdk/settings');
    expect(plugin?.pluginPageUrl).toBe('/sdk');
    expect(plugin?.iconUrl).toBe('/assets/sdk-icon.png');
  });

  it('should not fetch when id is falsy', async () => {
    // With empty string, the enabled flag becomes Boolean('') === false,
    // so the query should not execute — replicating the pattern where a
    // plugin ID hasn't been selected yet in the UI.
    const { result } = renderHook(() => usePlugin(''), {
      wrapper: createWrapper(),
    });

    // The hook should remain in idle/pending state without making any API call
    expect(mockGet).not.toHaveBeenCalled();
    expect(result.current.fetchStatus).toBe('idle');
  });
});

// --------------------------------------------------------------------------
// 3. usePluginData — Fetch plugin-specific configuration data
// --------------------------------------------------------------------------

describe('usePluginData', () => {
  it('should fetch plugin data by plugin name', async () => {
    // Replaces ErpPlugin.GetPluginData() which executed:
    //   SELECT * FROM plugin_data WHERE name = @name
    mockGet.mockResolvedValue(mockApiResponse(mockPluginData));

    const { result } = renderHook(() => usePluginData('sdk'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/plugins/sdk/data');
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toBe(mockPluginData);
    expect(result.current.data?.success).toBe(true);
  });

  it('should return opaque JSON string as-is', async () => {
    // Plugin data is stored as raw JSON string in plugin_data.data column (now DynamoDB).
    // The Plugin System service persists it verbatim without interpreting its contents.
    const complexData = '{"nested":{"deep":{"value":42}},"array":[1,2,3],"bool":true}';
    mockGet.mockResolvedValue(mockApiResponse(complexData));

    const { result } = renderHook(() => usePluginData('sdk'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // The returned data should be the exact raw string — not parsed JSON
    expect(typeof result.current.data?.object).toBe('string');
    expect(result.current.data?.object).toBe(complexData);

    // Verify the string is valid JSON that can be parsed by consumers
    const parsed = JSON.parse(result.current.data?.object as string);
    expect(parsed.nested.deep.value).toBe(42);
    expect(parsed.array).toEqual([1, 2, 3]);
  });

  it('should handle plugin with no data', async () => {
    // ErpPlugin.GetPluginData() returned null when no rows existed in plugin_data.
    // The API returns a successful response with null/undefined object.
    mockGet.mockResolvedValue(mockApiResponse(null));

    const { result } = renderHook(() => usePluginData('new-plugin'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.object).toBeNull();
    expect(result.current.data?.success).toBe(true);
  });
});

// --------------------------------------------------------------------------
// 4. useRegisterPlugin — Register a new plugin
// --------------------------------------------------------------------------

describe('useRegisterPlugin', () => {
  it('should register a new plugin via POST', async () => {
    // Replaces the monolith's reflection-based discovery where plugins
    // were found via assembly scanning at startup. In the microservices
    // architecture, plugins self-register via this API endpoint.
    const { id: _id, ...registrationPayload } = mockPlugin;
    const registeredPlugin = { ...mockPlugin, id: 'newly-assigned-guid' };

    mockPost.mockResolvedValue(mockApiResponse(registeredPlugin));

    const queryClient = createTestQueryClient();
    const { result } = renderHook(() => useRegisterPlugin(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(registrationPayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/plugins', registrationPayload);
    expect(mockPost).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toEqual(registeredPlugin);
  });

  it('should invalidate plugins query on success', async () => {
    const { id: _id, ...registrationPayload } = mockPlugin;
    mockPost.mockResolvedValue(mockApiResponse({ ...mockPlugin, id: 'new-guid' }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useRegisterPlugin(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(registrationPayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // The ['plugins'] query key should be invalidated so the plugin list
    // refetches and includes the newly registered plugin.
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['plugins'] }),
    );
  });
});

// --------------------------------------------------------------------------
// 5. useUpdatePlugin — Update plugin metadata
// --------------------------------------------------------------------------

describe('useUpdatePlugin', () => {
  it('should update plugin metadata via PUT', async () => {
    const updatedPlugin = {
      ...mockPlugin,
      description: 'Updated SDK Admin Console',
      version: '2.0.0',
    };

    mockPut.mockResolvedValue(mockApiResponse(updatedPlugin));

    const queryClient = createTestQueryClient();
    const { result } = renderHook(() => useUpdatePlugin(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(updatedPlugin);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPut).toHaveBeenCalledWith(
      '/plugins/plugin-guid',
      updatedPlugin,
    );
    expect(mockPut).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toEqual(updatedPlugin);
  });

  it('should invalidate plugins list and specific plugin queries on success', async () => {
    mockPut.mockResolvedValue(mockApiResponse(mockPlugin));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdatePlugin(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(mockPlugin);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Both the list query ['plugins'] AND the specific plugin query
    // ['plugins', id] should be invalidated to ensure all consumers
    // see the updated metadata.
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['plugins'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['plugins', 'plugin-guid'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledTimes(2);
  });
});

// --------------------------------------------------------------------------
// 6. useDeletePlugin — Unregister (delete) a plugin
// --------------------------------------------------------------------------

describe('useDeletePlugin', () => {
  it('should unregister a plugin via DELETE', async () => {
    mockDel.mockResolvedValue(mockApiResponse(undefined as unknown as void));

    const queryClient = createTestQueryClient();
    const { result } = renderHook(() => useDeletePlugin(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('plugin-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockDel).toHaveBeenCalledWith('/plugins/plugin-guid');
    expect(mockDel).toHaveBeenCalledTimes(1);
  });

  it('should invalidate plugins query on success', async () => {
    mockDel.mockResolvedValue(mockApiResponse(undefined as unknown as void));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeletePlugin(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('plugin-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // The ['plugins'] query key should be invalidated so the plugin list
    // refetches without the deleted plugin.
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['plugins'] }),
    );
  });
});

// --------------------------------------------------------------------------
// 7. useSavePluginData — Persist plugin configuration data
// --------------------------------------------------------------------------

describe('useSavePluginData', () => {
  it('should save plugin data via PUT', async () => {
    // Replaces ErpPlugin.SavePluginData(string data) which performed:
    //   INSERT INTO plugin_data(id, name, data) VALUES(@id, @name, @data)
    //   or UPDATE plugin_data SET data = @data WHERE name = @name
    mockPut.mockResolvedValue(mockApiResponse(undefined as unknown as void));

    const queryClient = createTestQueryClient();
    const { result } = renderHook(() => useSavePluginData(), {
      wrapper: createWrapper(queryClient),
    });

    const payload = {
      pluginName: 'sdk',
      data: mockPluginData,
    };

    await act(async () => {
      result.current.mutate(payload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify the correct endpoint and body — the data is sent as { data: string }
    expect(mockPut).toHaveBeenCalledWith('/plugins/sdk/data', {
      data: mockPluginData,
    });
    expect(mockPut).toHaveBeenCalledTimes(1);
  });

  it('should invalidate plugin data query on success', async () => {
    mockPut.mockResolvedValue(mockApiResponse(undefined as unknown as void));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useSavePluginData(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate({ pluginName: 'sdk', data: '{"updated":true}' });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // The specific plugin data query ['plugins', pluginName, 'data'] should be
    // invalidated so consumers see the updated configuration.
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['plugins', 'sdk', 'data'] }),
    );
  });

  it('should handle large JSON payloads', async () => {
    // Plugin data can be arbitrarily complex — the Plugin System service
    // stores it verbatim as an opaque JSON string in DynamoDB, replacing
    // the monolith's `plugin_data.data` text column.
    const largeConfig = JSON.stringify({
      settings: {
        feature1: true,
        feature2: false,
        feature3: { nested: { deep: { value: 'complex' } } },
      },
      uiPreferences: {
        theme: 'dark',
        language: 'en',
        sidebar: { collapsed: false, width: 280 },
      },
      dataSourceConfigs: Array.from({ length: 50 }, (_, i) => ({
        id: `ds-${i}`,
        name: `DataSource ${i}`,
        type: i % 2 === 0 ? 'code' : 'database',
        enabled: true,
        parameters: { timeout: 30000, retryCount: 3 },
      })),
      metadata: {
        lastUpdated: new Date().toISOString(),
        version: 42,
        checksum: 'abc123def456',
      },
    });

    mockPut.mockResolvedValue(mockApiResponse(undefined as unknown as void));

    const queryClient = createTestQueryClient();
    const { result } = renderHook(() => useSavePluginData(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate({ pluginName: 'sdk', data: largeConfig });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify the large JSON payload was passed through correctly
    expect(mockPut).toHaveBeenCalledWith('/plugins/sdk/data', {
      data: largeConfig,
    });

    // Verify the payload is valid JSON that can be round-tripped
    const sentData = (mockPut.mock.calls[0] as unknown[])[1] as { data: string };
    const parsed = JSON.parse(sentData.data);
    expect(parsed.dataSourceConfigs).toHaveLength(50);
    expect(parsed.settings.feature3.nested.deep.value).toBe('complex');
    expect(parsed.metadata.version).toBe(42);
  });
});
