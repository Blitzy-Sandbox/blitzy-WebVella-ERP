/**
 * Page Management TanStack Query Hooks
 *
 * Provides 16 hooks (6 query + 10 mutation) for ERP page management,
 * replacing the monolith's PageService.cs (page CRUD, body node tree management,
 * page data sources, cloning, URL-to-page resolution, cache invalidation) and
 * PageComponentLibraryService.cs (reflection-based component catalog discovery)
 * with API calls to the Entity Management service via /v1/pages/* endpoints.
 *
 * Query hooks:  usePages, usePage, usePageByUrl, usePageBody,
 *               usePageDataSources, useComponentCatalog
 * Mutation hooks: useCreatePage, useUpdatePage, useDeletePage, useClonePage,
 *                 useCreateBodyNode, useUpdateBodyNode, useDeleteBodyNode,
 *                 useMoveBodyNode, useCreatePageDataSource,
 *                 useDeletePageDataSource
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, patch, del } from '../api/client';
import type { ErpPage, PageBodyNode, PageDataSource, PageType } from '../types/page';
import type { PageComponentMeta } from '../types/component';
import type { BaseResponseModel, UrlInfo } from '../types/common';

/* ────────────────────────────────────────────────────────────
   Constants
   ──────────────────────────────────────────────────────────── */

/**
 * 30 minutes in milliseconds.
 * Component catalog changes very rarely — heavy caching is safe.
 * Matches the monolith's static PageComponentLibraryService catalog
 * which was rebuilt only at application startup.
 */
const COMPONENT_CATALOG_STALE_TIME = 30 * 60 * 1000;

/* ────────────────────────────────────────────────────────────
   Parameter & Payload Types
   ──────────────────────────────────────────────────────────── */

/** Query parameters for the page listing endpoint (GET /v1/pages). */
export interface PagesQueryParams {
  /** Filter by PageType enum value */
  type?: PageType;
  /** Filter pages belonging to a specific application */
  appId?: string;
  /** Filter pages associated with a specific entity */
  entityId?: string;
  /** 1-based page number for pagination */
  page?: number;
  /** Number of items per page */
  pageSize?: number;
}

/** Payload for creating a new page (POST /v1/pages). */
export interface CreatePagePayload {
  /** Unique machine-readable name (required) */
  name: string;
  /** Human-readable label (required) */
  label: string;
  /** Sort weight for ordering */
  weight?: number;
  /** Page type classification */
  type?: PageType;
  /** Owning application ID */
  appId?: string;
  /** Associated entity ID */
  entityId?: string;
  /** Sitemap area ID */
  areaId?: string;
  /** Sitemap node ID */
  nodeId?: string;
  /** CSS icon class */
  iconClass?: string;
  /** Whether this is a system page */
  system?: boolean;
  /** Whether the page uses Razor body rendering */
  isRazorBody?: boolean;
  /** Layout template identifier */
  layout?: string;
}

/** Payload for updating an existing page (PUT /v1/pages/{id}). */
export interface UpdatePagePayload {
  /** Page ID to update */
  id: string;
  /** Updated machine-readable name */
  name?: string;
  /** Updated human-readable label */
  label?: string;
  /** Updated sort weight */
  weight?: number;
  /** Updated page type */
  type?: PageType;
  /** Updated owning application ID */
  appId?: string;
  /** Updated associated entity ID */
  entityId?: string;
  /** Updated sitemap area ID */
  areaId?: string;
  /** Updated sitemap node ID */
  nodeId?: string;
  /** Updated CSS icon class */
  iconClass?: string;
  /** Whether this is a system page */
  system?: boolean;
  /** Whether the page uses Razor body rendering */
  isRazorBody?: boolean;
  /** Razor body content (server validates for `<script>` blocking) */
  razorBody?: string;
  /** Layout template identifier */
  layout?: string;
  /** Localized label translations */
  labelTranslations?: Array<{ locale: string; key: string; value: string }>;
}

/**
 * Payload for deep-cloning a page (POST /v1/pages/{id}/clone).
 * Server generates a unique name if none is provided.
 */
export interface ClonePagePayload {
  /** Source page ID to clone */
  id: string;
  /** Optional new name for the cloned page */
  name?: string;
  /** Optional new label for the cloned page */
  label?: string;
  /** Optional target application ID for the clone */
  appId?: string;
}

/** Payload for creating a body node (POST /v1/pages/{pageId}/body). */
export interface CreateBodyNodePayload {
  /** Page that owns the body tree */
  pageId: string;
  /** Parent node ID (null for root-level) */
  parentId?: string;
  /** Container slot within the parent component */
  containerId?: string;
  /** Component type name (e.g. "PcRow", "PcFieldText") */
  componentName: string;
  /** Sort weight within the parent container */
  weight?: number;
  /** JSON-serialized component options */
  options?: string;
}

/** Payload for updating a body node (PUT /v1/pages/{pageId}/body/{nodeId}). */
export interface UpdateBodyNodePayload {
  /** Page that owns the body tree */
  pageId: string;
  /** Node ID to update */
  nodeId: string;
  /** Updated component type name */
  componentName?: string;
  /** Updated sort weight */
  weight?: number;
  /** Updated JSON-serialized component options */
  options?: string;
}

/** Payload for deleting a body node (DELETE /v1/pages/{pageId}/body/{nodeId}). */
export interface DeleteBodyNodePayload {
  /** Page that owns the body tree */
  pageId: string;
  /** Node ID to delete (server cascades descendants) */
  nodeId: string;
}

/**
 * Payload for moving/re-parenting a body node
 * (PATCH /v1/pages/{pageId}/body/{nodeId}/move).
 */
export interface MoveBodyNodePayload {
  /** Page that owns the body tree */
  pageId: string;
  /** Node ID to move */
  nodeId: string;
  /** New parent node ID */
  newParentId?: string;
  /** New container slot within the new parent */
  newContainerId?: string;
  /** New sort weight in the target container */
  newWeight?: number;
}

/** Payload for creating a page data source (POST /v1/pages/{pageId}/datasources). */
export interface CreatePageDataSourcePayload {
  /** Page to bind the data source to */
  pageId: string;
  /** ID of the data source definition */
  dataSourceId: string;
  /** Unique name for this binding (server enforces uniqueness) */
  name: string;
  /** Parameter overrides for this page-level binding */
  parameters?: Array<{
    name: string;
    type: string;
    value: string;
    ignoreParseErrors?: boolean;
  }>;
}

/** Payload for deleting a page data source (DELETE /v1/pages/{pageId}/datasources/{dsId}). */
export interface DeletePageDataSourcePayload {
  /** Page that owns the data source binding */
  pageId: string;
  /** Data source binding ID to remove */
  dsId: string;
}

/* ────────────────────────────────────────────────────────────
   Query Hooks (6)
   ──────────────────────────────────────────────────────────── */

/**
 * Fetches a paginated, filterable list of ERP pages.
 *
 * Replaces PageService.GetAll() (all pages ordered by Weight),
 * GetIndexPages() (type=Home filter), GetSitePages() (type=Site),
 * GetAppControlledPages(appId), and GetEntityPages(entityId, type).
 *
 * @param params - Optional filter and pagination parameters
 * @returns TanStack Query result with `data.object` as `ErpPage[]`
 *
 * @example
 * ```tsx
 * const { data, isLoading } = usePages({ type: PageType.Application, appId });
 * const pages = data?.object ?? [];
 * ```
 */
export function usePages(params?: PagesQueryParams) {
  return useQuery({
    queryKey: ['pages', params] as const,
    queryFn: () =>
      get<ErpPage[]>('/pages', params as Record<string, unknown> | undefined),
  });
}

/**
 * Fetches a single ERP page by its GUID identifier.
 *
 * Replaces PageService.GetPage(Guid id).
 * The query is disabled when `id` is falsy.
 *
 * @param id - Page GUID identifier (or undefined to disable the query)
 * @returns TanStack Query result with `data.object` as `ErpPage`
 */
export function usePage(id: string | undefined) {
  return useQuery({
    queryKey: ['pages', id] as const,
    queryFn: () => get<ErpPage>(`/pages/${id}`),
    enabled: !!id,
  });
}

/**
 * Resolves a UrlInfo object to the corresponding ERP page.
 *
 * Uses POST because the UrlInfo payload is complex (multiple fields).
 * This is a **read** operation despite using POST — it does not modify state.
 * Replaces PageService.GetCurrentPage() which performed complex URL-to-page
 * resolution across home, site, app, and record routes.
 *
 * @param urlInfo - URL resolution parameters (appName, areaName, nodeName, etc.)
 * @param options - Optional configuration; `enabled` defaults to true when
 *                  urlInfo has a non-empty appName
 * @returns TanStack Query result with `data.object` as `ErpPage`
 */
export function usePageByUrl(
  urlInfo: UrlInfo | undefined,
  options?: { enabled?: boolean },
) {
  const isEnabled =
    options?.enabled !== undefined
      ? options.enabled
      : !!urlInfo && !!urlInfo.appName;

  return useQuery({
    queryKey: ['pages', 'resolve', urlInfo] as const,
    queryFn: () => post<ErpPage>('/pages/resolve', urlInfo),
    enabled: isEnabled,
  });
}

/**
 * Fetches the reconstructed page body tree for a given page.
 *
 * Returns the full PageBodyNode hierarchy with nested `nodes` arrays.
 * Replaces PageService.GetPageBody(pageId) which reconstructed the tree
 * from a flat list using parent-wiring logic.
 *
 * @param pageId - Page GUID identifier (or undefined to disable)
 * @returns TanStack Query result with `data.object` as `PageBodyNode[]`
 */
export function usePageBody(pageId: string | undefined) {
  return useQuery({
    queryKey: ['pages', pageId, 'body'] as const,
    queryFn: () => get<PageBodyNode[]>(`/pages/${pageId}/body`),
    enabled: !!pageId,
  });
}

/**
 * Fetches all data source bindings for a given page.
 *
 * Replaces PageService.GetPageDataSources(pageId).
 *
 * @param pageId - Page GUID identifier (or undefined to disable)
 * @returns TanStack Query result with `data.object` as `PageDataSource[]`
 */
export function usePageDataSources(pageId: string | undefined) {
  return useQuery({
    queryKey: ['pages', pageId, 'datasources'] as const,
    queryFn: () => get<PageDataSource[]>(`/pages/${pageId}/datasources`),
    enabled: !!pageId,
  });
}

/**
 * Fetches the component catalog listing all available page-builder components.
 *
 * Replaces PageComponentLibraryService which used static reflection-based
 * discovery of all PageComponent subclasses at startup. The catalog includes
 * component metadata (name, label, description, category, icon, color, version,
 * isInline flag, usage counter, and last-used timestamp).
 *
 * Cached aggressively with a 30-minute staleTime because the catalog
 * changes only when new components are deployed.
 *
 * @returns TanStack Query result with `data.object` as `PageComponentMeta[]`
 */
export function useComponentCatalog() {
  return useQuery({
    queryKey: ['component-catalog'] as const,
    queryFn: () => get<PageComponentMeta[]>('/pages/components'),
    staleTime: COMPONENT_CATALOG_STALE_TIME,
  });
}

/* ────────────────────────────────────────────────────────────
   Mutation Hooks — Page CRUD (4)
   ──────────────────────────────────────────────────────────── */

/**
 * Creates a new ERP page.
 *
 * Server-side validation enforces:
 * - Name and label are required
 * - Only one Home-type page is allowed
 * - Name uniqueness within the application
 *
 * Replaces PageService.CreatePage().
 * On success, invalidates the `['pages']` query cache.
 */
export function useCreatePage() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: CreatePagePayload) =>
      post<ErpPage>('/pages', payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['pages'] });
    },
  });
}

/**
 * Updates an existing ERP page.
 *
 * Server-side handles Razor body file synchronization and `<script>` tag
 * blocking in body content. Replaces PageService.UpdatePage().
 *
 * On success, invalidates both the pages list `['pages']` and
 * the specific page detail `['pages', id]`.
 */
export function useUpdatePage() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: UpdatePagePayload) => {
      const { id, ...data } = payload;
      return put<ErpPage>(`/pages/${id}`, data);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['pages'] });
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.id],
      });
    },
  });
}

/**
 * Deletes an ERP page with full server-side cascade.
 *
 * The server handles cascaded deletion of body nodes, data sources,
 * and the Razor body file. Replaces PageService.DeletePage().
 *
 * On success, invalidates the `['pages']` query cache.
 */
export function useDeletePage() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: string) =>
      del<BaseResponseModel>(`/pages/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['pages'] });
    },
  });
}

/**
 * Deep-clones an existing ERP page including its body nodes and data sources.
 *
 * The server generates a unique name if one is not provided in the payload.
 * Replaces PageService.ClonePage().
 *
 * On success, invalidates the `['pages']` query cache.
 */
export function useClonePage() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: ClonePagePayload) => {
      const { id, ...data } = payload;
      return post<ErpPage>(`/pages/${id}/clone`, data);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['pages'] });
    },
  });
}

/* ────────────────────────────────────────────────────────────
   Mutation Hooks — Body Node Management (4)
   ──────────────────────────────────────────────────────────── */

/**
 * Creates a new body node (adds a component to a page's body tree).
 *
 * Server-side validation includes `<script>` blocking for HTML components.
 * Replaces PageService.CreatePageBodyNode().
 *
 * On success, invalidates the specific page body tree
 * `['pages', pageId, 'body']` — NOT all pages.
 */
export function useCreateBodyNode() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: CreateBodyNodePayload) => {
      const { pageId, ...data } = payload;
      return post<PageBodyNode>(`/pages/${pageId}/body`, data);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.pageId, 'body'],
      });
    },
  });
}

/**
 * Updates a body node's options, component name, or weight.
 *
 * Replaces PageService.UpdatePageBodyNode() with option validation.
 *
 * On success, invalidates the specific page body tree
 * `['pages', pageId, 'body']`.
 */
export function useUpdateBodyNode() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: UpdateBodyNodePayload) => {
      const { pageId, nodeId, ...data } = payload;
      return put<PageBodyNode>(`/pages/${pageId}/body/${nodeId}`, data);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.pageId, 'body'],
      });
    },
  });
}

/**
 * Deletes a body node and all its descendants (cascade deletion).
 *
 * Server uses a queue/stack-based approach to recursively delete all
 * descendant nodes. Replaces PageService.DeletePageBodyNodeInternal().
 *
 * On success, invalidates the specific page body tree
 * `['pages', pageId, 'body']`.
 */
export function useDeleteBodyNode() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: DeleteBodyNodePayload) =>
      del<BaseResponseModel>(
        `/pages/${payload.pageId}/body/${payload.nodeId}`,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.pageId, 'body'],
      });
    },
  });
}

/**
 * Moves or re-parents a body node within the page tree.
 *
 * Uses PATCH semantics for partial update — can change parent node,
 * container slot, and/or weight position independently.
 *
 * On success, invalidates the specific page body tree
 * `['pages', pageId, 'body']`.
 */
export function useMoveBodyNode() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: MoveBodyNodePayload) => {
      const { pageId, nodeId, ...data } = payload;
      return patch<PageBodyNode>(
        `/pages/${pageId}/body/${nodeId}/move`,
        data,
      );
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.pageId, 'body'],
      });
    },
  });
}

/* ────────────────────────────────────────────────────────────
   Mutation Hooks — Page Data Source Management (2)
   ──────────────────────────────────────────────────────────── */

/**
 * Creates a new data source binding for a page.
 *
 * Server-side enforces unique name within the page.
 * Replaces PageService.CreatePageDataSource().
 *
 * On success, invalidates the specific page data sources
 * `['pages', pageId, 'datasources']`.
 */
export function useCreatePageDataSource() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: CreatePageDataSourcePayload) => {
      const { pageId, ...data } = payload;
      return post<PageDataSource>(`/pages/${pageId}/datasources`, data);
    },
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.pageId, 'datasources'],
      });
    },
  });
}

/**
 * Deletes a data source binding from a page.
 *
 * Replaces PageService.DeletePageDataSource().
 *
 * On success, invalidates the specific page data sources
 * `['pages', pageId, 'datasources']`.
 */
export function useDeletePageDataSource() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (payload: DeletePageDataSourcePayload) =>
      del<BaseResponseModel>(
        `/pages/${payload.pageId}/datasources/${payload.dsId}`,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: ['pages', variables.pageId, 'datasources'],
      });
    },
  });
}
