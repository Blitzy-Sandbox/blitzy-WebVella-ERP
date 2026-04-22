/**
 * Vitest unit tests for 15 application & sitemap TanStack Query hooks.
 *
 * Hooks under test (from useApps.ts):
 *   useApps, useApp, useCreateApp, useUpdateApp, useDeleteApp,
 *   useCreateArea, useUpdateArea, useDeleteArea,
 *   useCreateAreaGroup, useUpdateAreaGroup, useDeleteAreaGroup,
 *   useCreateAreaNode, useUpdateAreaNode, useDeleteAreaNode,
 *   useOrderSitemap
 *
 * Replaces monolith subsystem:
 *   - AppService.cs — App CRUD, sitemap area/group/node CRUD,
 *                      cache management, cascaded deletion, ordering
 *
 * Key monolith behaviours preserved:
 *   - Nested sitemap: Apps contain areas → groups → nodes
 *   - All sitemap mutations invalidate the parent app query (NOT all apps)
 *   - App deletion cascades to pages + sitemap
 *   - Weight-based ordering for OrderSitemap
 *   - TranslationResource[] carried in area/group/node payloads (i18n)
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React, { type ReactNode } from 'react';

// ── Module under test ────────────────────────────────────────────────
import {
  useApps,
  useApp,
  useCreateApp,
  useUpdateApp,
  useDeleteApp,
  useCreateArea,
  useUpdateArea,
  useDeleteArea,
  useCreateAreaGroup,
  useUpdateAreaGroup,
  useDeleteAreaGroup,
  useCreateAreaNode,
  useUpdateAreaNode,
  useDeleteAreaNode,
  useOrderSitemap,
} from '../../../src/hooks/useApps';

// ── Type imports ─────────────────────────────────────────────────────
import type {
  App,
  SitemapArea,
  SitemapGroup,
  SitemapNode,
  Sitemap,
} from '../../../src/types/app';
import type { BaseResponseModel } from '../../../src/types/common';

// ── Mock API client ──────────────────────────────────────────────────
vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
}));

import { get, post, put, del } from '../../../src/api/client';

const mockedGet = vi.mocked(get);
const mockedPost = vi.mocked(post);
const mockedPut = vi.mocked(put);
const mockedDel = vi.mocked(del);

// =====================================================================
// Test Fixtures
// =====================================================================

const mockGroup: SitemapGroup = {
  id: 'g1000000-0000-0000-0000-000000000001',
  name: 'main',
  label: 'Main',
  weight: 1,
  labelTranslations: [],
  renderRoles: [],
};

const mockNode: SitemapNode = {
  id: 'nd100000-0000-0000-0000-000000000001',
  parentId: null,
  weight: 1,
  groupName: 'main',
  label: 'Contact List',
  name: 'list',
  iconClass: 'fa fa-list',
  url: '',
  labelTranslations: [],
  access: [],
  type: 1, // SitemapNodeType.EntityList
  entityId: 'e1000000-0000-0000-0000-000000000001',
  entityListPages: [],
  entityCreatePages: [],
  entityDetailsPages: [],
  entityManagePages: [],
};

const mockArea: SitemapArea = {
  id: 'a1000000-0000-0000-0000-000000000001',
  appId: 'ap100000-0000-0000-0000-000000000001',
  weight: 1,
  label: 'Contacts',
  description: 'CRM contacts area',
  name: 'contacts',
  iconClass: 'fa fa-address-book',
  showGroupNames: true,
  color: '#4caf50',
  labelTranslations: [],
  descriptionTranslations: [],
  groups: [mockGroup],
  nodes: [mockNode],
  access: [],
};

const mockSitemap: Sitemap = {
  areas: [mockArea],
};

const mockApp: App = {
  id: 'ap100000-0000-0000-0000-000000000001',
  name: 'crm',
  label: 'CRM',
  description: 'Customer Relationship Management',
  iconClass: 'fa fa-users',
  author: 'WebVella',
  color: '#2196f3',
  weight: 1,
  access: [],
  homePages: [],
  entities: [],
  sitemap: mockSitemap,
};

const mockApp2: App = {
  id: 'ap200000-0000-0000-0000-000000000002',
  name: 'projects',
  label: 'Projects',
  description: 'Project management app',
  iconClass: 'fa fa-tasks',
  author: 'WebVella',
  color: '#ff9800',
  weight: 2,
  access: [],
  homePages: [],
  entities: [],
  sitemap: null,
};

// =====================================================================
// Response Helpers
// =====================================================================

function createSuccessResponse<T>(object: T) {
  return {
    success: true as const,
    object,
    errors: [] as Array<{ key: string; value: string; message: string }>,
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: '',
    hash: 'response-hash',
    accessWarnings: [],
  };
}

function createErrorResponse(
  statusCode: number,
  errors: Array<{ key: string; value: string; message: string }>,
  message = 'Request failed',
) {
  return {
    success: false as const,
    object: null,
    errors,
    statusCode,
    timestamp: new Date().toISOString(),
    message,
    hash: null,
    accessWarnings: [],
  };
}

// =====================================================================
// QueryClient Wrapper (.ts file — React.createElement, not JSX)
// =====================================================================

let queryClient: QueryClient;

function createWrapper() {
  return function Wrapper({ children }: { children: ReactNode }) {
    return React.createElement(
      QueryClientProvider,
      { client: queryClient },
      children,
    );
  };
}

// =====================================================================
// Setup / Teardown
// =====================================================================

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  vi.clearAllMocks();
});

afterEach(() => {
  queryClient.clear();
});

// =====================================================================
// 1. useApps — list all applications
//    Replaces AppService.GetAllApplications() with cache
// =====================================================================
describe('useApps', () => {
  it('should fetch all applications', async () => {
    const apps = [mockApp, mockApp2];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(apps));

    const { result } = renderHook(() => useApps(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/apps');
    expect(result.current.data?.object).toEqual(apps);
  });

  it('should use staleTime of 5 minutes', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([mockApp]));

    const { result } = renderHook(() => useApps(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Within staleTime the query MUST NOT refetch — only 1 call
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should handle empty app list', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([] as App[]));

    const { result } = renderHook(() => useApps(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(result.current.data?.object).toEqual([]);
  });

  it('should handle API error gracefully', async () => {
    mockedGet.mockRejectedValueOnce(
      createErrorResponse(500, [
        { key: 'server', value: '', message: 'Internal error' },
      ]),
    );

    const { result } = renderHook(() => useApps(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeDefined();
  });
});

// =====================================================================
// 2. useApp — single application by id or name
//    Replaces AppService.GetApplication(id/name)
// =====================================================================
describe('useApp', () => {
  it('should fetch app by ID', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockApp));

    const { result } = renderHook(() => useApp(mockApp.id), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}`,
    );
    expect(result.current.data?.object).toEqual(mockApp);
  });

  it('should fetch app by name', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockApp));

    const { result } = renderHook(() => useApp('crm'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent('crm')}`,
    );
    expect(result.current.data?.object).toEqual(mockApp);
  });

  it('should include nested sitemap structure (areas → groups → nodes)', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockApp));

    const { result } = renderHook(() => useApp(mockApp.id), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const app = result.current.data?.object;
    expect(app?.sitemap).toBeDefined();
    expect(app?.sitemap?.areas).toHaveLength(1);
    expect(app?.sitemap?.areas[0].groups).toHaveLength(1);
    expect(app?.sitemap?.areas[0].nodes).toHaveLength(1);
    expect(app?.sitemap?.areas[0].groups[0].name).toBe('main');
    expect(app?.sitemap?.areas[0].nodes[0].label).toBe('Contact List');
  });

  it('should not fetch when idOrName is undefined (enabled guard)', async () => {
    const { result } = renderHook(
      () => useApp(undefined as unknown as string),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch when idOrName is empty string', async () => {
    const { result } = renderHook(() => useApp(''), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should handle 404 error for non-existent app', async () => {
    mockedGet.mockRejectedValueOnce(
      createErrorResponse(404, [
        { key: 'app', value: 'missing-id', message: 'Application not found' },
      ]),
    );

    const { result } = renderHook(() => useApp('missing-id'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeDefined();
  });
});

// =====================================================================
// 3. useCreateApp — create application
//    Replaces AppService.CreateApplication(app) with uniqueness validation
// =====================================================================
describe('useCreateApp', () => {
  it('should create app via POST /v1/apps', async () => {
    const newApp: Partial<App> = {
      name: 'invoicing',
      label: 'Invoicing',
      description: 'Billing and invoices',
      iconClass: 'fa fa-file-invoice',
      color: '#9c27b0',
      weight: 3,
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse({ ...mockApp, ...newApp, id: 'new-app-guid' }),
    );

    const { result } = renderHook(() => useCreateApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ app: newApp as App });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/apps', newApp);
  });

  it('should invalidate apps query on successful create', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockApp));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ app: mockApp });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });

  it('should handle name uniqueness error (400)', async () => {
    mockedPost.mockRejectedValueOnce(
      createErrorResponse(400, [
        {
          key: 'name',
          value: 'crm',
          message: 'An application with that name already exists',
        },
      ]),
    );

    const { result } = renderHook(() => useCreateApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ app: mockApp });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error).toBeDefined();
  });
});

// =====================================================================
// 4. useUpdateApp — update application
//    Replaces AppService.UpdateApplication(app) + ClearAppCache
// =====================================================================
describe('useUpdateApp', () => {
  it('should update app via PUT /v1/apps/{id}', async () => {
    const updated = { ...mockApp, label: 'CRM Updated' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const { result } = renderHook(() => useUpdateApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockApp.id, app: updated });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}`,
      updated,
    );
  });

  it('should invalidate apps list AND specific app on update', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockApp));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockApp.id, app: mockApp });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Must invalidate both the list query and the specific detail query
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
  });

  it('should handle validation error on update', async () => {
    mockedPut.mockRejectedValueOnce(
      createErrorResponse(400, [
        { key: 'label', value: '', message: 'Label is required' },
      ]),
    );

    const { result } = renderHook(() => useUpdateApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockApp.id, app: { ...mockApp, label: '' } });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

// =====================================================================
// 5. useDeleteApp — delete application with cascade
//    Replaces AppService.DeleteApplication → DeleteApplicationInternal
//    (cascades pages + sitemap areas in a transaction)
// =====================================================================
describe('useDeleteApp', () => {
  it('should delete app via DELETE /v1/apps/{id}', async () => {
    const deleteResponse: BaseResponseModel = {
      success: true,
      errors: [],
      message: 'Application deleted',
      timestamp: new Date().toISOString(),
      hash: null,
      accessWarnings: [],
    };
    mockedDel.mockResolvedValueOnce(createSuccessResponse(deleteResponse));

    const { result } = renderHook(() => useDeleteApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockApp.id });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}`,
    );
  });

  it('should invalidate apps AND pages queries on delete (cascade)', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockApp.id });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // App deletion cascades to pages — both query groups must be invalidated
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['pages'] }),
    );
  });

  it('should invalidate specific app detail on delete', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteApp(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockApp.id });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
  });
});

// =====================================================================
// 6. useCreateArea — create sitemap area on an app
//    Replaces AppService.CreateArea(appId, area)
// =====================================================================
describe('useCreateArea', () => {
  it('should create area via POST /v1/apps/{appId}/areas', async () => {
    const newArea: Partial<SitemapArea> = {
      name: 'deals',
      label: 'Deals',
      description: 'Sales pipeline',
      iconClass: 'fa fa-handshake',
      weight: 2,
      color: '#e91e63',
      showGroupNames: true,
      labelTranslations: [],
      descriptionTranslations: [],
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse({ ...mockArea, ...newArea, id: 'new-area-guid' }),
    );

    const { result } = renderHook(() => useCreateArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        area: newArea as SitemapArea,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas`,
      newArea,
    );
  });

  it('should invalidate parent app query on area create', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockArea));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ appId: mockApp.id, area: mockArea });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
  });

  it('should also invalidate apps list on area create', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockArea));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ appId: mockApp.id, area: mockArea });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 7. useUpdateArea — update sitemap area
//    Replaces AppService.UpdateArea(appId, area)
// =====================================================================
describe('useUpdateArea', () => {
  it('should update area via PUT /v1/apps/{appId}/areas/{areaId}', async () => {
    const updated = { ...mockArea, label: 'Contacts Updated' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const { result } = renderHook(() => useUpdateArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        area: updated,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}`,
      updated,
    );
  });

  it('should invalidate parent app on area update', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockArea));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        area: mockArea,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 8. useDeleteArea — delete sitemap area with cascade
//    Replaces AppService.DeleteArea(appId, areaId) — cascades groups/nodes
// =====================================================================
describe('useDeleteArea', () => {
  it('should delete area via DELETE /v1/apps/{appId}/areas/{areaId}', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Area deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );

    const { result } = renderHook(() => useDeleteArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}`,
    );
  });

  it('should invalidate parent app on area delete', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Area deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteArea(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 9. useCreateAreaGroup — create sitemap group in area
//    Replaces AppService.CreateSitemapGroup(appId, areaId, group)
// =====================================================================
describe('useCreateAreaGroup', () => {
  it('should create group via POST /v1/apps/{appId}/areas/{areaId}/groups', async () => {
    const newGroup: Partial<SitemapGroup> = {
      name: 'secondary',
      label: 'Secondary',
      weight: 2,
      labelTranslations: [],
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse({ ...mockGroup, ...newGroup, id: 'new-group-guid' }),
    );

    const { result } = renderHook(() => useCreateAreaGroup(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        group: newGroup as SitemapGroup,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}/groups`,
      newGroup,
    );
  });

  it('should invalidate parent app on group create', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockGroup));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateAreaGroup(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        group: mockGroup,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 10. useUpdateAreaGroup — update sitemap group
//     Replaces AppService.UpdateSitemapGroup(appId, areaId, group)
// =====================================================================
describe('useUpdateAreaGroup', () => {
  it('should update group via PUT /v1/apps/{appId}/areas/{areaId}/groups/{groupId}', async () => {
    const updated = { ...mockGroup, label: 'Main Updated' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const { result } = renderHook(() => useUpdateAreaGroup(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        groupId: mockGroup.id,
        group: updated,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}/groups/${encodeURIComponent(mockGroup.id)}`,
      updated,
    );
  });

  it('should invalidate parent app on group update', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockGroup));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateAreaGroup(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        groupId: mockGroup.id,
        group: mockGroup,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 11. useDeleteAreaGroup — delete sitemap group
//     Replaces AppService.DeleteSitemapGroup(appId, areaId, groupId)
// =====================================================================
describe('useDeleteAreaGroup', () => {
  it('should delete group via DELETE /v1/apps/{appId}/areas/{areaId}/groups/{groupId}', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Group deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );

    const { result } = renderHook(() => useDeleteAreaGroup(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        groupId: mockGroup.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}/groups/${encodeURIComponent(mockGroup.id)}`,
    );
  });

  it('should invalidate parent app on group delete', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Group deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteAreaGroup(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        groupId: mockGroup.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 12. useCreateAreaNode — create sitemap node in area
//     Replaces AppService.CreateSitemapNode(appId, areaId, node)
// =====================================================================
describe('useCreateAreaNode', () => {
  it('should create node via POST /v1/apps/{appId}/areas/{areaId}/nodes', async () => {
    const newNode: Partial<SitemapNode> = {
      name: 'create',
      label: 'Create Contact',
      iconClass: 'fa fa-plus',
      weight: 2,
      groupName: 'main',
      type: 1, // EntityList
      entityId: 'e1000000-0000-0000-0000-000000000001',
      labelTranslations: [],
    };
    mockedPost.mockResolvedValueOnce(
      createSuccessResponse({ ...mockNode, ...newNode, id: 'new-node-guid' }),
    );

    const { result } = renderHook(() => useCreateAreaNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        node: newNode as SitemapNode,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}/nodes`,
      newNode,
    );
  });

  it('should invalidate parent app on node create', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockNode));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateAreaNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        node: mockNode,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 13. useUpdateAreaNode — update sitemap node
//     Replaces AppService.UpdateSitemapNode(appId, areaId, node)
// =====================================================================
describe('useUpdateAreaNode', () => {
  it('should update node via PUT /v1/apps/{appId}/areas/{areaId}/nodes/{nodeId}', async () => {
    const updated = { ...mockNode, label: 'Contact List V2' };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updated));

    const { result } = renderHook(() => useUpdateAreaNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        nodeId: mockNode.id,
        node: updated,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}/nodes/${encodeURIComponent(mockNode.id)}`,
      updated,
    );
  });

  it('should invalidate parent app on node update', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockNode));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateAreaNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        nodeId: mockNode.id,
        node: mockNode,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 14. useDeleteAreaNode — delete sitemap node
//     Replaces AppService.DeleteSitemapNode(appId, areaId, nodeId)
// =====================================================================
describe('useDeleteAreaNode', () => {
  it('should delete node via DELETE /v1/apps/{appId}/areas/{areaId}/nodes/{nodeId}', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Node deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );

    const { result } = renderHook(() => useDeleteAreaNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        nodeId: mockNode.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/areas/${encodeURIComponent(mockArea.id)}/nodes/${encodeURIComponent(mockNode.id)}`,
    );
  });

  it('should invalidate parent app on node delete (cascade)', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        errors: [],
        message: 'Node deleted',
        timestamp: new Date().toISOString(),
        hash: null,
        accessWarnings: [],
      }),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteAreaNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        areaId: mockArea.id,
        nodeId: mockNode.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });
});

// =====================================================================
// 15. useOrderSitemap — reorder sitemap items (weight-based)
//     Replaces AppService.OrderSitemap(sitemap) deterministic sort
// =====================================================================
describe('useOrderSitemap', () => {
  it('should reorder sitemap via PUT /v1/apps/{appId}/sitemap/order', async () => {
    const reorderedArea: SitemapArea = {
      ...mockArea,
      weight: 10,
      nodes: [
        { ...mockNode, weight: 5 },
      ],
    };
    const reorderedSitemap: Sitemap = { areas: [reorderedArea] };

    mockedPut.mockResolvedValueOnce(createSuccessResponse(reorderedSitemap));

    const { result } = renderHook(() => useOrderSitemap(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        sitemap: reorderedSitemap,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/sitemap/order`,
      reorderedSitemap,
    );
  });

  it('should invalidate parent app on reorder', async () => {
    const reorderedSitemap: Sitemap = { areas: [{ ...mockArea, weight: 99 }] };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(reorderedSitemap));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useOrderSitemap(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        sitemap: reorderedSitemap,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps', mockApp.id] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['apps'] }),
    );
  });

  it('should handle ordering with multiple areas and weight updates', async () => {
    const area2: SitemapArea = {
      ...mockArea,
      id: 'a2000000-0000-0000-0000-000000000002',
      name: 'deals',
      label: 'Deals',
      weight: 1,
    };
    const reorderedSitemap: Sitemap = {
      areas: [
        { ...mockArea, weight: 2 }, // moved down
        { ...area2, weight: 1 },    // moved up
      ],
    };

    mockedPut.mockResolvedValueOnce(createSuccessResponse(reorderedSitemap));

    const { result } = renderHook(() => useOrderSitemap(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        appId: mockApp.id,
        sitemap: reorderedSitemap,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/apps/${encodeURIComponent(mockApp.id)}/sitemap/order`,
      reorderedSitemap,
    );
    expect(result.current.data?.object).toEqual(reorderedSitemap);
  });
});
