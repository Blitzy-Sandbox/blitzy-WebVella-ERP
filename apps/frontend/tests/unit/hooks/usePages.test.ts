/**
 * Vitest unit tests for 16 page management TanStack Query hooks.
 *
 * Hooks under test (from usePages.ts):
 *   usePages, usePage, usePageByUrl, useCreatePage, useUpdatePage, useDeletePage,
 *   useClonePage, usePageBody, useCreateBodyNode, useUpdateBodyNode, useDeleteBodyNode,
 *   useMoveBodyNode, usePageDataSources, useCreatePageDataSource, useDeletePageDataSource,
 *   useComponentCatalog
 *
 * Replaces monolith subsystems:
 *   - PageService.cs           — page CRUD, body node tree management, data
 *                                 sources, cloning, URL resolution, caching
 *   - PageComponentLibraryService.cs — reflection-based component catalog discovery
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React, { type ReactNode } from 'react';

// ── Module under test ────────────────────────────────────────────────
import {
  usePages,
  usePage,
  usePageByUrl,
  useCreatePage,
  useUpdatePage,
  useDeletePage,
  useClonePage,
  usePageBody,
  useCreateBodyNode,
  useUpdateBodyNode,
  useDeleteBodyNode,
  useMoveBodyNode,
  usePageDataSources,
  useCreatePageDataSource,
  useDeletePageDataSource,
  useComponentCatalog,
} from '../../../src/hooks/usePages';

// ── Type imports (type-only) ─────────────────────────────────────────
import type {
  ErpPage,
  PageBodyNode,
  PageDataSource,
  PageType,
} from '../../../src/types/page';
import type { PageComponentMeta } from '../../../src/types/component';
import type { BaseResponseModel, UrlInfo } from '../../../src/types/common';

// ── Mock API client ──────────────────────────────────────────────────
// vi.mock is auto-hoisted before imports so the mock is established
// before the hook module loads and binds to the real client functions.
vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
}));

import { get, post, put, patch, del } from '../../../src/api/client';

const mockedGet = vi.mocked(get);
const mockedPost = vi.mocked(post);
const mockedPut = vi.mocked(put);
const mockedPatch = vi.mocked(patch);
const mockedDel = vi.mocked(del);

// =====================================================================
// Test Fixtures
// =====================================================================

/** PageType enum values matching the source enum */
const PAGE_TYPE_HOME = 0;
const PAGE_TYPE_SITE = 1;
const PAGE_TYPE_APPLICATION = 2;
const PAGE_TYPE_RECORD_LIST = 3;
const PAGE_TYPE_RECORD_CREATE = 4;
const PAGE_TYPE_RECORD_DETAILS = 5;
const PAGE_TYPE_RECORD_MANAGE = 6;

const mockPage: ErpPage = {
  id: 'a1000000-0000-0000-0000-000000000001',
  weight: 1,
  label: 'Contacts',
  labelTranslations: [],
  name: 'contact-list',
  iconClass: 'fa fa-address-book',
  system: false,
  type: PAGE_TYPE_RECORD_LIST as unknown as PageType,
  appId: 'b1000000-0000-0000-0000-000000000001',
  entityId: 'c1000000-0000-0000-0000-000000000001',
  areaId: 'd1000000-0000-0000-0000-000000000001',
  nodeId: 'e1000000-0000-0000-0000-000000000001',
  isRazorBody: false,
  razorBody: '',
  layout: '',
  body: [],
};

const mockSecondPage: ErpPage = {
  id: 'a2000000-0000-0000-0000-000000000002',
  weight: 2,
  label: 'Accounts',
  labelTranslations: [],
  name: 'account-list',
  iconClass: 'fa fa-building',
  system: false,
  type: PAGE_TYPE_RECORD_LIST as unknown as PageType,
  appId: 'b1000000-0000-0000-0000-000000000001',
  entityId: 'c2000000-0000-0000-0000-000000000002',
  areaId: 'd1000000-0000-0000-0000-000000000001',
  nodeId: 'e2000000-0000-0000-0000-000000000002',
  isRazorBody: false,
  razorBody: '',
  layout: '',
  body: [],
};

const mockHomePage: ErpPage = {
  id: 'a3000000-0000-0000-0000-000000000003',
  weight: 0,
  label: 'Home',
  labelTranslations: [],
  name: 'home',
  iconClass: 'fa fa-home',
  system: true,
  type: PAGE_TYPE_HOME as unknown as PageType,
  appId: null,
  entityId: null,
  areaId: null,
  nodeId: null,
  isRazorBody: false,
  razorBody: '',
  layout: '',
  body: [],
};

/** Body node fixture — root-level grid component */
const mockBodyNode: PageBodyNode = {
  id: 'n1000000-0000-0000-0000-000000000001',
  parentId: null,
  pageId: mockPage.id,
  nodeId: 'n1000000-0000-0000-0000-000000000001',
  containerId: 'content',
  weight: 1,
  componentName: 'PcGrid',
  options: JSON.stringify({ entityName: 'contact' }),
  nodes: [],
};

/** Child body node fixture — text field inside the grid */
const mockChildNode: PageBodyNode = {
  id: 'n2000000-0000-0000-0000-000000000002',
  parentId: 'n1000000-0000-0000-0000-000000000001',
  pageId: mockPage.id,
  nodeId: 'n2000000-0000-0000-0000-000000000002',
  containerId: 'column1',
  weight: 1,
  componentName: 'PcFieldText',
  options: JSON.stringify({ fieldName: 'name' }),
  nodes: [],
};

/** Body tree with parent–child hierarchy */
const mockBodyTree: PageBodyNode[] = [
  {
    ...mockBodyNode,
    nodes: [{ ...mockChildNode }],
  },
];

/** Data source binding fixture */
const mockDataSource: PageDataSource = {
  id: 'ds100000-0000-0000-0000-000000000001',
  pageId: mockPage.id,
  dataSourceId: 'dsr10000-0000-0000-0000-000000000001',
  name: 'contact_list_ds',
  parameters: [
    {
      name: 'entityName',
      type: 'text',
      value: 'contact',
      ignoreParseErrors: false,
    },
  ],
};

const mockSecondDataSource: PageDataSource = {
  id: 'ds200000-0000-0000-0000-000000000002',
  pageId: mockPage.id,
  dataSourceId: 'dsr20000-0000-0000-0000-000000000002',
  name: 'contact_detail_ds',
  parameters: [],
};

/** Component catalog entry fixture */
const mockComponentMeta: PageComponentMeta = {
  name: 'PcGrid',
  label: 'Data Grid',
  description: 'Sortable, filterable data grid component',
  iconClass: 'fa fa-table',
  color: '#2196F3',
  category: 'Layout',
  library: 'WebVella.Erp.Web',
  designViewUrl: '',
  optionsViewUrl: '',
  helpViewUrl: '',
  serviceJsUrl: '',
  version: '1.0.0',
  isInline: false,
  usageCounter: 42,
  lastUsedOn: '2024-01-15T10:30:00Z',
};

const mockFieldComponentMeta: PageComponentMeta = {
  name: 'PcFieldText',
  label: 'Text Field',
  description: 'Single-line text input component',
  iconClass: 'fa fa-font',
  color: '#4CAF50',
  category: 'Fields',
  library: 'WebVella.Erp.Web',
  designViewUrl: '',
  optionsViewUrl: '',
  helpViewUrl: '',
  serviceJsUrl: '',
  version: '1.0.0',
  isInline: true,
  usageCounter: 120,
  lastUsedOn: '2024-01-16T09:00:00Z',
};

// =====================================================================
// Response Helpers
// =====================================================================

/**
 * Build a success response envelope.
 * Covers BaseResponseModel fields: success, errors, message, timestamp, hash.
 */
function createSuccessResponse<T>(object: T): {
  success: BaseResponseModel['success'];
  errors: BaseResponseModel['errors'];
  message: BaseResponseModel['message'];
  timestamp: BaseResponseModel['timestamp'];
  hash: string | undefined;
  object: T;
  statusCode: number;
} {
  return {
    success: true,
    object,
    errors: [],
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: '',
    hash: 'response-hash',
  };
}

/**
 * Build an error response envelope (400/403/500).
 * Hooks check `response.success` and throw with error details.
 */
function createErrorResponse(
  statusCode: number,
  errors: Array<{ key: string; value: string; message: string }>,
  message?: string,
) {
  return {
    success: false as const,
    object: undefined,
    errors,
    statusCode,
    timestamp: new Date().toISOString(),
    message: message || errors[0]?.message || 'Error',
    hash: undefined,
  };
}

// =====================================================================
// QueryClient Wrapper
// =====================================================================

let queryClient: QueryClient;

/**
 * Creates a React wrapper that provides QueryClientProvider context.
 * Uses React.createElement (not JSX) because the file is .ts not .tsx.
 */
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

// #####################################################################
//  1. usePages — List all pages (with optional filters)
//     Replaces PageService.GetAll(), GetIndexPages(), GetSitePages(),
//     GetAppControlledPages(), GetEntityPages()
// #####################################################################

describe('usePages', () => {
  it('should fetch all pages', async () => {
    const pages: ErpPage[] = [mockPage, mockSecondPage];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(pages));

    const { result } = renderHook(() => usePages(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/pages', undefined);
    expect(result.current.data?.object).toHaveLength(2);
    expect(result.current.data?.object?.[0].name).toBe('contact-list');
    expect(result.current.data?.object?.[1].name).toBe('account-list');
  });

  it('should filter by type', async () => {
    const recordListPages = [mockPage, mockSecondPage];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(recordListPages));

    const params = { type: PAGE_TYPE_RECORD_LIST as unknown as PageType };
    const { result } = renderHook(() => usePages(params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/pages',
      expect.objectContaining({ type: PAGE_TYPE_RECORD_LIST }),
    );
    expect(result.current.data?.object).toHaveLength(2);
  });

  it('should filter by appId', async () => {
    const appPages = [mockPage];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(appPages));

    const appId = 'b1000000-0000-0000-0000-000000000001';
    const { result } = renderHook(() => usePages({ appId }), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/pages',
      expect.objectContaining({ appId }),
    );
  });

  it('should filter by entityId', async () => {
    const entityPages = [mockPage];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(entityPages));

    const entityId = 'c1000000-0000-0000-0000-000000000001';
    const { result } = renderHook(() => usePages({ entityId }), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      '/pages',
      expect.objectContaining({ entityId }),
    );
  });

  it('should handle empty page list', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse([] as ErpPage[]));

    const { result } = renderHook(() => usePages(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.object).toEqual([]);
  });
});

// #####################################################################
//  2. usePage — Fetch single page by ID
//     Replaces PageService.GetPage(Guid id)
// #####################################################################

describe('usePage', () => {
  it('should fetch page by ID', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockPage));

    const { result } = renderHook(() => usePage(mockPage.id), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(`/pages/${mockPage.id}`);
    expect(result.current.data?.object?.name).toBe('contact-list');
    expect(result.current.data?.object?.label).toBe('Contacts');
  });

  it('should not fetch when id is undefined', async () => {
    const { result } = renderHook(() => usePage(undefined), {
      wrapper: createWrapper(),
    });

    // enabled = !!id evaluates to false for undefined
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should not fetch when id is empty string', async () => {
    const { result } = renderHook(() => usePage(''), {
      wrapper: createWrapper(),
    });

    // enabled = !!id evaluates to false for empty string
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });
});

// #####################################################################
//  3. usePageByUrl — URL-to-page resolution
//     Replaces PageService.GetCurrentPage() complex URL resolution
//     across home, site, app, and record routes
// #####################################################################

describe('usePageByUrl', () => {
  it('should resolve page from URL info', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockPage));

    const urlInfo: UrlInfo = {
      hasRelation: false,
      pageType: PAGE_TYPE_APPLICATION,
      appName: 'crm',
      areaName: 'contacts',
      nodeName: 'list',
      pageName: 'contact-list',
    };

    const { result } = renderHook(() => usePageByUrl(urlInfo), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/pages/resolve', urlInfo);
    expect(result.current.data?.object?.name).toBe('contact-list');
  });

  it('should handle home page resolution', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockHomePage));

    const urlInfo: UrlInfo = {
      hasRelation: false,
      pageType: PAGE_TYPE_HOME,
      appName: 'home',
      areaName: '',
      nodeName: '',
      pageName: '',
    };

    const { result } = renderHook(() => usePageByUrl(urlInfo), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/pages/resolve', urlInfo);
    expect(result.current.data?.object?.type).toBe(PAGE_TYPE_HOME);
    expect(result.current.data?.object?.name).toBe('home');
  });

  it('should handle record page resolution with recordId', async () => {
    const recordPage: ErpPage = {
      ...mockPage,
      type: PAGE_TYPE_RECORD_DETAILS as unknown as PageType,
      name: 'contact-details',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(recordPage));

    const urlInfo: UrlInfo = {
      hasRelation: false,
      pageType: PAGE_TYPE_RECORD_DETAILS,
      appName: 'crm',
      areaName: 'contacts',
      nodeName: 'details',
      pageName: 'contact-details',
      recordId: 'rec10000-0000-0000-0000-000000000001',
    };

    const { result } = renderHook(() => usePageByUrl(urlInfo), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/pages/resolve', urlInfo);
    expect(result.current.data?.object?.type).toBe(PAGE_TYPE_RECORD_DETAILS);
  });

  it('should not fetch when urlInfo is undefined', async () => {
    const { result } = renderHook(() => usePageByUrl(undefined), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedPost).not.toHaveBeenCalled();
  });

  it('should not fetch when urlInfo.appName is empty', async () => {
    const urlInfo: UrlInfo = {
      hasRelation: false,
      pageType: PAGE_TYPE_HOME,
      appName: '',
      areaName: '',
      nodeName: '',
      pageName: '',
    };

    const { result } = renderHook(() => usePageByUrl(urlInfo), {
      wrapper: createWrapper(),
    });

    // enabled defaults to !!urlInfo && !!urlInfo.appName → false
    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedPost).not.toHaveBeenCalled();
  });

  it('should respect explicit enabled override', async () => {
    const urlInfo: UrlInfo = {
      hasRelation: false,
      pageType: PAGE_TYPE_HOME,
      appName: 'crm',
      areaName: '',
      nodeName: '',
      pageName: '',
    };

    const { result } = renderHook(
      () => usePageByUrl(urlInfo, { enabled: false }),
      { wrapper: createWrapper() },
    );

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedPost).not.toHaveBeenCalled();
  });
});

// #####################################################################
//  4. useCreatePage — Create a new ERP page
//     Replaces PageService.CreatePage() with validation rules
// #####################################################################

describe('useCreatePage', () => {
  it('should create page successfully', async () => {
    const newPage: ErpPage = {
      ...mockPage,
      id: 'a4000000-0000-0000-0000-000000000004',
      name: 'new-contacts',
      label: 'New Contacts',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(newPage));

    const { result } = renderHook(() => useCreatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        name: 'new-contacts',
        label: 'New Contacts',
        type: PAGE_TYPE_RECORD_LIST as unknown as PageType,
        appId: mockPage.appId!,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith('/pages', {
      name: 'new-contacts',
      label: 'New Contacts',
      type: PAGE_TYPE_RECORD_LIST,
      appId: mockPage.appId,
    });
    expect(result.current.data?.object?.name).toBe('new-contacts');
  });

  it('should invalidate pages query on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockPage));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        name: 'contact-list',
        label: 'Contacts',
        type: PAGE_TYPE_RECORD_LIST as unknown as PageType,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['pages'] }),
    );
  });

  it('should handle single Home page rule — server returns 400 for duplicate', async () => {
    // The API client interceptor rejects for both HTTP 400 and success:false
    // responses, so we simulate a rejection to match real client behavior.
    mockedPost.mockRejectedValueOnce({
      message: 'Only one Home page is allowed',
      errors: [
        {
          key: 'type',
          value: '',
          message: 'Only one Home page is allowed',
        },
      ],
      status: 400,
      timestamp: new Date().toISOString(),
    });

    const { result } = renderHook(() => useCreatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        name: 'home-duplicate',
        label: 'Second Home',
        type: PAGE_TYPE_HOME as unknown as PageType,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });

  it('should handle validation errors — name and label required', async () => {
    // The API client interceptor rejects for both HTTP 400 and success:false
    // responses, so we simulate a rejection to match real client behavior.
    mockedPost.mockRejectedValueOnce({
      message: 'Validation failed',
      errors: [
        { key: 'name', value: '', message: 'Page name is required' },
        { key: 'label', value: '', message: 'Page label is required' },
      ],
      status: 400,
      timestamp: new Date().toISOString(),
    });

    const { result } = renderHook(() => useCreatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        name: '',
        label: '',
        type: PAGE_TYPE_RECORD_LIST as unknown as PageType,
      });
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
  });
});

// #####################################################################
//  5. useUpdatePage — Update an existing ERP page
//     Replaces PageService.UpdatePage() with Razor body sync
// #####################################################################

describe('useUpdatePage', () => {
  it('should update page successfully', async () => {
    const updatedPage: ErpPage = {
      ...mockPage,
      label: 'Updated Contacts',
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedPage));

    const { result } = renderHook(() => useUpdatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: mockPage.id,
        label: 'Updated Contacts',
        name: mockPage.name,
        type: mockPage.type,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/pages/${mockPage.id}`,
      expect.objectContaining({ label: 'Updated Contacts' }),
    );
    expect(result.current.data?.object?.label).toBe('Updated Contacts');
  });

  it('should invalidate pages list and specific page on success', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockPage));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: mockPage.id,
        label: 'Contacts',
        name: 'contact-list',
        type: mockPage.type,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Should invalidate the pages list
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['pages'] }),
    );
    // Should also invalidate the specific page detail cache
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['pages', mockPage.id] }),
    );
  });

  it('should send only data (not id) in the request body', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockPage));

    const { result } = renderHook(() => useUpdatePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: mockPage.id,
        label: 'Contacts',
        name: 'contact-list',
        type: mockPage.type,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The id should be in the URL, not in the request body
    const callArgs = mockedPut.mock.calls[0];
    expect(callArgs[0]).toBe(`/pages/${mockPage.id}`);
    expect(callArgs[1]).not.toHaveProperty('id');
  });
});

// #####################################################################
//  6. useDeletePage — Delete page with cascaded cleanup
//     Replaces PageService.DeletePage() (body nodes, data sources, Razor file)
// #####################################################################

describe('useDeletePage', () => {
  it('should delete page successfully', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        message: 'Page deleted successfully',
        timestamp: new Date().toISOString(),
        hash: '',
        errors: [],
        accessWarnings: [],
      } as BaseResponseModel),
    );

    const { result } = renderHook(() => useDeletePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockPage.id);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(`/pages/${mockPage.id}`);
  });

  it('should invalidate pages query on success', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        message: '',
        timestamp: new Date().toISOString(),
        hash: '',
        errors: [],
        accessWarnings: [],
      } as BaseResponseModel),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeletePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockPage.id);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['pages'] }),
    );
  });
});

// #####################################################################
//  7. useClonePage — Deep-clone page (body nodes + data sources)
//     Replaces PageService.ClonePage()
// #####################################################################

describe('useClonePage', () => {
  it('should clone page successfully', async () => {
    const clonedPage: ErpPage = {
      ...mockPage,
      id: 'a5000000-0000-0000-0000-000000000005',
      name: 'contact-list-copy',
      label: 'Contacts (Copy)',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(clonedPage));

    const { result } = renderHook(() => useClonePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: mockPage.id,
        name: 'contact-list-copy',
        label: 'Contacts (Copy)',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/clone`,
      expect.objectContaining({
        name: 'contact-list-copy',
        label: 'Contacts (Copy)',
      }),
    );
    expect(result.current.data?.object?.name).toBe('contact-list-copy');
  });

  it('should invalidate pages query on success', async () => {
    const clonedPage: ErpPage = {
      ...mockPage,
      id: 'a5000000-0000-0000-0000-000000000005',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(clonedPage));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useClonePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: mockPage.id,
        name: 'clone',
        label: 'Clone',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['pages'] }),
    );
  });

  it('should send only clone data (not id) in the request body', async () => {
    const clonedPage: ErpPage = {
      ...mockPage,
      id: 'a5000000-0000-0000-0000-000000000005',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(clonedPage));

    const { result } = renderHook(() => useClonePage(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        id: mockPage.id,
        name: 'clone',
        label: 'Clone',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The id should be in the URL, not in the request body
    const callArgs = mockedPost.mock.calls[0];
    expect(callArgs[0]).toBe(`/pages/${mockPage.id}/clone`);
    expect(callArgs[1]).not.toHaveProperty('id');
  });
});

// #####################################################################
//  8. usePageBody — Fetch page body tree
//     Replaces PageService.GetPageBody(pageId) tree reconstruction
// #####################################################################

describe('usePageBody', () => {
  it('should fetch page body tree', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockBodyTree));

    const { result } = renderHook(() => usePageBody(mockPage.id), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(`/pages/${mockPage.id}/body`);
    expect(result.current.data?.object).toHaveLength(1);
  });

  it('should return hierarchical tree with parent-child structure', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockBodyTree));

    const { result } = renderHook(() => usePageBody(mockPage.id), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const tree = result.current.data?.object;
    expect(tree).toBeDefined();
    expect(tree![0].componentName).toBe('PcGrid');
    expect(tree![0].parentId).toBeNull();
    expect(tree![0].nodes).toHaveLength(1);
    expect(tree![0].nodes[0].componentName).toBe('PcFieldText');
    expect(tree![0].nodes[0].parentId).toBe(mockBodyNode.id);
  });

  it('should not fetch when pageId is undefined', async () => {
    const { result } = renderHook(() => usePageBody(undefined), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should handle empty body tree', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse([] as PageBodyNode[]),
    );

    const { result } = renderHook(() => usePageBody(mockPage.id), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.object).toEqual([]);
  });
});

// #####################################################################
//  9. useCreateBodyNode — Add component to page body tree
//     Replaces PageService.CreatePageBodyNode()
//     Granular invalidation: only ['pages', pageId, 'body'], NOT all pages
// #####################################################################

describe('useCreateBodyNode', () => {
  it('should create body node successfully', async () => {
    const newNode: PageBodyNode = {
      ...mockChildNode,
      id: 'n3000000-0000-0000-0000-000000000003',
      componentName: 'PcFieldDate',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(newNode));

    const { result } = renderHook(() => useCreateBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        parentId: mockBodyNode.id,
        containerId: 'column2',
        componentName: 'PcFieldDate',
        weight: 2,
        options: JSON.stringify({ fieldName: 'created_on' }),
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/body`,
      expect.objectContaining({
        parentId: mockBodyNode.id,
        containerId: 'column2',
        componentName: 'PcFieldDate',
      }),
    );
    // pageId should be in the URL, not the request body
    expect(mockedPost.mock.calls[0][1]).not.toHaveProperty('pageId');
  });

  it('should invalidate ONLY the specific page body — NOT all pages', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockChildNode));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        parentId: undefined,
        containerId: 'content',
        componentName: 'PcGrid',
        weight: 1,
        options: '{}',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Granular invalidation: specific page body
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['pages', mockPage.id, 'body'],
      }),
    );
  });
});

// #####################################################################
//  10. useUpdateBodyNode — Update body node options/component
//      Replaces PageService.UpdatePageBodyNode()
// #####################################################################

describe('useUpdateBodyNode', () => {
  it('should update body node options successfully', async () => {
    const updatedNode: PageBodyNode = {
      ...mockBodyNode,
      options: JSON.stringify({ entityName: 'contact', pageSize: 20 }),
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedNode));

    const { result } = renderHook(() => useUpdateBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        nodeId: mockBodyNode.id,
        options: JSON.stringify({ entityName: 'contact', pageSize: 20 }),
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/body/${mockBodyNode.id}`,
      expect.objectContaining({
        options: expect.stringContaining('pageSize'),
      }),
    );
    // pageId and nodeId should be in the URL, not the body
    expect(mockedPut.mock.calls[0][1]).not.toHaveProperty('pageId');
    expect(mockedPut.mock.calls[0][1]).not.toHaveProperty('nodeId');
  });

  it('should invalidate specific page body on success', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockBodyNode));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        nodeId: mockBodyNode.id,
        options: '{}',
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['pages', mockPage.id, 'body'],
      }),
    );
  });
});

// #####################################################################
//  11. useDeleteBodyNode — Delete body node and all descendants
//      Replaces PageService.DeletePageBodyNodeInternal() queue/stack cascade
// #####################################################################

describe('useDeleteBodyNode', () => {
  it('should delete body node and descendants', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        message: 'Body node deleted',
        timestamp: new Date().toISOString(),
        hash: '',
        errors: [],
        accessWarnings: [],
      } as BaseResponseModel),
    );

    const { result } = renderHook(() => useDeleteBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        nodeId: mockBodyNode.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/body/${mockBodyNode.id}`,
    );
  });

  it('should invalidate specific page body on success', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        message: '',
        timestamp: new Date().toISOString(),
        hash: '',
        errors: [],
        accessWarnings: [],
      } as BaseResponseModel),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        nodeId: mockBodyNode.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['pages', mockPage.id, 'body'],
      }),
    );
  });
});

// #####################################################################
//  12. useMoveBodyNode — Reorder / re-parent body node
//      Replaces weight/parent update logic in PageService
//      Uses PATCH semantics for partial update
// #####################################################################

describe('useMoveBodyNode', () => {
  it('should move body node to new parent', async () => {
    const movedNode: PageBodyNode = {
      ...mockChildNode,
      parentId: 'n9000000-0000-0000-0000-000000000009',
      containerId: 'new-container',
      weight: 3,
    };
    mockedPatch.mockResolvedValueOnce(createSuccessResponse(movedNode));

    const { result } = renderHook(() => useMoveBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        nodeId: mockChildNode.id,
        newParentId: 'n9000000-0000-0000-0000-000000000009',
        newContainerId: 'new-container',
        newWeight: 3,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPatch).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/body/${mockChildNode.id}/move`,
      expect.objectContaining({
        newParentId: 'n9000000-0000-0000-0000-000000000009',
        newContainerId: 'new-container',
        newWeight: 3,
      }),
    );
    // pageId and nodeId should be in the URL, not the body
    expect(mockedPatch.mock.calls[0][1]).not.toHaveProperty('pageId');
    expect(mockedPatch.mock.calls[0][1]).not.toHaveProperty('nodeId');
  });

  it('should invalidate specific page body on success', async () => {
    mockedPatch.mockResolvedValueOnce(createSuccessResponse(mockChildNode));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useMoveBodyNode(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        nodeId: mockChildNode.id,
        newParentId: undefined,
        newContainerId: 'content',
        newWeight: 1,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['pages', mockPage.id, 'body'],
      }),
    );
  });
});

// #####################################################################
//  13. usePageDataSources — Fetch page data source bindings
//      Replaces PageService.GetPageDataSources(pageId)
// #####################################################################

describe('usePageDataSources', () => {
  it('should fetch page data sources', async () => {
    const dataSources = [mockDataSource, mockSecondDataSource];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(dataSources));

    const { result } = renderHook(
      () => usePageDataSources(mockPage.id),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/datasources`,
    );
    expect(result.current.data?.object).toHaveLength(2);
    expect(result.current.data?.object?.[0].name).toBe('contact_list_ds');
    expect(result.current.data?.object?.[1].name).toBe('contact_detail_ds');
  });

  it('should not fetch when pageId is undefined', async () => {
    const { result } = renderHook(() => usePageDataSources(undefined), {
      wrapper: createWrapper(),
    });

    expect(result.current.fetchStatus).toBe('idle');
    expect(mockedGet).not.toHaveBeenCalled();
  });

  it('should handle empty data sources list', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse([] as PageDataSource[]),
    );

    const { result } = renderHook(
      () => usePageDataSources(mockPage.id),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.object).toEqual([]);
  });
});

// #####################################################################
//  14. useCreatePageDataSource — Create data source binding
//      Replaces PageService.CreatePageDataSource()
// #####################################################################

describe('useCreatePageDataSource', () => {
  it('should create page data source successfully', async () => {
    const newDs: PageDataSource = {
      ...mockDataSource,
      id: 'ds300000-0000-0000-0000-000000000003',
      name: 'new_datasource',
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(newDs));

    const { result } = renderHook(() => useCreatePageDataSource(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        dataSourceId: 'dsr30000-0000-0000-0000-000000000003',
        name: 'new_datasource',
        parameters: [],
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/datasources`,
      expect.objectContaining({
        dataSourceId: 'dsr30000-0000-0000-0000-000000000003',
        name: 'new_datasource',
      }),
    );
    // pageId should be in the URL, not the request body
    expect(mockedPost.mock.calls[0][1]).not.toHaveProperty('pageId');
  });

  it('should invalidate page datasources on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockDataSource));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreatePageDataSource(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        dataSourceId: mockDataSource.dataSourceId,
        name: mockDataSource.name,
        parameters: [],
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['pages', mockPage.id, 'datasources'],
      }),
    );
  });
});

// #####################################################################
//  15. useDeletePageDataSource — Delete data source binding
//      Replaces PageService.DeletePageDataSource()
// #####################################################################

describe('useDeletePageDataSource', () => {
  it('should delete page data source successfully', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        message: 'Data source deleted',
        timestamp: new Date().toISOString(),
        hash: '',
        errors: [],
        accessWarnings: [],
      } as BaseResponseModel),
    );

    const { result } = renderHook(() => useDeletePageDataSource(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        dsId: mockDataSource.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/pages/${mockPage.id}/datasources/${mockDataSource.id}`,
    );
  });

  it('should invalidate page datasources on success', async () => {
    mockedDel.mockResolvedValueOnce(
      createSuccessResponse({
        success: true,
        message: '',
        timestamp: new Date().toISOString(),
        hash: '',
        errors: [],
        accessWarnings: [],
      } as BaseResponseModel),
    );
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeletePageDataSource(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({
        pageId: mockPage.id,
        dsId: mockDataSource.id,
      });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        queryKey: ['pages', mockPage.id, 'datasources'],
      }),
    );
  });
});

// #####################################################################
//  16. useComponentCatalog — Component catalog discovery
//      Replaces PageComponentLibraryService reflection-based static
//      catalog with 30-minute staleTime caching
// #####################################################################

describe('useComponentCatalog', () => {
  it('should fetch component catalog', async () => {
    const catalog: PageComponentMeta[] = [
      mockComponentMeta,
      mockFieldComponentMeta,
    ];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(catalog));

    const { result } = renderHook(() => useComponentCatalog(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/pages/components');
    expect(result.current.data?.object).toHaveLength(2);
  });

  it('should use staleTime of 30 minutes for heavy caching', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse([mockComponentMeta]),
    );

    const { result } = renderHook(() => useComponentCatalog(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // Second render within same QueryClient reuses cached data
    // because staleTime = 30 * 60 * 1000 = 1,800,000 ms
    renderHook(() => useComponentCatalog(), { wrapper: createWrapper() });
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('should include component categories and icons', async () => {
    const catalog: PageComponentMeta[] = [
      mockComponentMeta,
      mockFieldComponentMeta,
    ];
    mockedGet.mockResolvedValueOnce(createSuccessResponse(catalog));

    const { result } = renderHook(() => useComponentCatalog(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const data = result.current.data?.object;
    expect(data).toBeDefined();

    // Verify layout component
    const gridComponent = data!.find(
      (c: PageComponentMeta) => c.name === 'PcGrid',
    );
    expect(gridComponent).toBeDefined();
    expect(gridComponent!.category).toBe('Layout');
    expect(gridComponent!.iconClass).toBe('fa fa-table');
    expect(gridComponent!.label).toBe('Data Grid');

    // Verify field component
    const textFieldComponent = data!.find(
      (c: PageComponentMeta) => c.name === 'PcFieldText',
    );
    expect(textFieldComponent).toBeDefined();
    expect(textFieldComponent!.category).toBe('Fields');
    expect(textFieldComponent!.iconClass).toBe('fa fa-font');
    expect(textFieldComponent!.label).toBe('Text Field');
    expect(textFieldComponent!.isInline).toBe(true);
  });

  it('should handle empty component catalog', async () => {
    mockedGet.mockResolvedValueOnce(
      createSuccessResponse([] as PageComponentMeta[]),
    );

    const { result } = renderHook(() => useComponentCatalog(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(result.current.data?.object).toEqual([]);
  });
});
