/**
 * Application & Sitemap TanStack Query 5 Hooks
 *
 * Replaces the monolith's `WebVella.Erp.Web/Services/AppService.cs` — the
 * web-layer orchestrator for App CRUD, sitemap area/group/node CRUD, cache
 * management, and cascaded deletion — with declarative React hooks backed
 * by HTTP calls to the Plugin System Lambda handlers via API Gateway.
 *
 * Source mapping:
 *   - AppService.GetAllApplications()           → useApps()
 *   - AppService.GetApplication(id/name)         → useApp(idOrName)
 *   - AppService.CreateApplication(...)          → useCreateApp()
 *   - AppService.UpdateApplication(...)          → useUpdateApp()
 *   - AppService.DeleteApplication(id)           → useDeleteApp()
 *   - AppService.CreateArea(...)                 → useCreateArea()
 *   - AppService.UpdateArea(...)                 → useUpdateArea()
 *   - AppService.DeleteArea(id)                  → useDeleteArea()
 *   - AppService.CreateAreaGroup(...)            → useCreateAreaGroup()
 *   - AppService.UpdateAreaGroup(...)            → useUpdateAreaGroup()
 *   - AppService.DeleteAreaGroup(id)             → useDeleteAreaGroup()
 *   - AppService.CreateAreaNode(...)             → useCreateAreaNode()
 *   - AppService.UpdateAreaNode(...)             → useUpdateAreaNode()
 *   - AppService.DeleteAreaNode(id)              → useDeleteAreaNode()
 *   - AppService.OrderSitemap(sitemap)           → useOrderSitemap()
 *
 * Cache strategy:
 *   - staleTime 5 min for app queries (apps change infrequently, matching
 *     the monolith's ErpAppContext.Current.Cache duration).
 *   - All sitemap mutations invalidate the parent app query key since
 *     areas/groups/nodes are nested within the App payload.
 *   - App deletion also invalidates the 'pages' query key because the
 *     server performs cascaded deletion of all bound pages.
 *
 * @module hooks/useApps
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type { ApiResponse } from '../api/client';
import type { App, SitemapArea, SitemapGroup, SitemapNode, Sitemap } from '../types/app';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Constants — Query Keys
// ---------------------------------------------------------------------------

/**
 * Centralised query key factory for apps domain.
 * Using a factory pattern avoids key typos and enables targeted invalidation.
 */
const APP_KEYS = {
  /** Root key for all app-related queries. */
  all: ['apps'] as const,
  /** Key for a single app by ID or name. */
  detail: (idOrName: string) => ['apps', idOrName] as const,
  /** Related: pages query key invalidated on app deletion. */
  pages: ['pages'] as const,
} as const;

/**
 * Stale time for app list and detail queries.
 * Apps change infrequently — 5 minutes matches the monolith's
 * ErpAppContext.Current.Cache TTL.
 */
const APPS_STALE_TIME_MS = 5 * 60 * 1000;

// ---------------------------------------------------------------------------
// Mutation Variable Types
// ---------------------------------------------------------------------------

/**
 * Payload for creating a new application.
 * Maps to `AppService.CreateApplication(id, name, label, description,
 * iconClass, author, color, weight, access)`.
 */
export interface CreateAppVariables {
  /** Application fields to create. */
  app: Partial<App> & Pick<App, 'name' | 'label'>;
}

/**
 * Payload for updating an existing application.
 * Maps to `AppService.UpdateApplication(id, ...)`.
 */
export interface UpdateAppVariables {
  /** Application ID. */
  id: string;
  /** Updated application fields. */
  app: Partial<App> & Pick<App, 'id' | 'name' | 'label'>;
}

/**
 * Payload for deleting an application.
 * Maps to `AppService.DeleteApplication(id)`.
 * Server performs cascaded deletion of pages + sitemap.
 */
export interface DeleteAppVariables {
  /** Application ID to delete. */
  id: string;
}

/**
 * Payload for creating a sitemap area within an app.
 * Maps to `AppService.CreateArea(...)`.
 */
export interface CreateAreaVariables {
  /** Parent application ID. */
  appId: string;
  /** Area fields to create. */
  area: Partial<SitemapArea> & Pick<SitemapArea, 'name' | 'label'>;
}

/**
 * Payload for updating a sitemap area.
 * Maps to `AppService.UpdateArea(...)`.
 */
export interface UpdateAreaVariables {
  /** Parent application ID. */
  appId: string;
  /** Area ID to update. */
  areaId: string;
  /** Updated area fields. */
  area: Partial<SitemapArea> & Pick<SitemapArea, 'id' | 'name' | 'label'>;
}

/**
 * Payload for deleting a sitemap area.
 * Maps to `AppService.DeleteArea(id)`.
 * Server performs cascaded deletion of groups + nodes.
 */
export interface DeleteAreaVariables {
  /** Parent application ID. */
  appId: string;
  /** Area ID to delete. */
  areaId: string;
}

/**
 * Payload for creating a sitemap area group.
 * Maps to `AppService.CreateAreaGroup(...)`.
 */
export interface CreateAreaGroupVariables {
  /** Parent application ID. */
  appId: string;
  /** Parent area ID. */
  areaId: string;
  /** Group fields to create. */
  group: Partial<SitemapGroup> & Pick<SitemapGroup, 'name' | 'label'>;
}

/**
 * Payload for updating a sitemap area group.
 * Maps to `AppService.UpdateAreaGroup(...)`.
 */
export interface UpdateAreaGroupVariables {
  /** Parent application ID. */
  appId: string;
  /** Parent area ID. */
  areaId: string;
  /** Group ID to update. */
  groupId: string;
  /** Updated group fields. */
  group: Partial<SitemapGroup> & Pick<SitemapGroup, 'id' | 'name' | 'label'>;
}

/**
 * Payload for deleting a sitemap area group.
 * Maps to `AppService.DeleteAreaGroup(id)`.
 */
export interface DeleteAreaGroupVariables {
  /** Parent application ID. */
  appId: string;
  /** Parent area ID. */
  areaId: string;
  /** Group ID to delete. */
  groupId: string;
}

/**
 * Payload for creating a sitemap area node.
 * Maps to `AppService.CreateAreaNode(...)`.
 */
export interface CreateAreaNodeVariables {
  /** Parent application ID. */
  appId: string;
  /** Parent area ID. */
  areaId: string;
  /** Node fields to create. */
  node: Partial<SitemapNode> & Pick<SitemapNode, 'name' | 'label'>;
}

/**
 * Payload for updating a sitemap area node.
 * Maps to `AppService.UpdateAreaNode(...)`.
 */
export interface UpdateAreaNodeVariables {
  /** Parent application ID. */
  appId: string;
  /** Parent area ID. */
  areaId: string;
  /** Node ID to update. */
  nodeId: string;
  /** Updated node fields. */
  node: Partial<SitemapNode> & Pick<SitemapNode, 'id' | 'name' | 'label'>;
}

/**
 * Payload for deleting a sitemap area node.
 * Maps to `AppService.DeleteAreaNode(id)`.
 * Server performs cascaded unbind of pages.
 */
export interface DeleteAreaNodeVariables {
  /** Parent application ID. */
  appId: string;
  /** Parent area ID. */
  areaId: string;
  /** Node ID to delete. */
  nodeId: string;
}

/**
 * Payload for reordering a sitemap.
 * Maps to `AppService.OrderSitemap(sitemap)` — deterministic
 * sorting by Weight then Name.
 */
export interface OrderSitemapVariables {
  /** Parent application ID. */
  appId: string;
  /** Sitemap with desired ordering applied. */
  sitemap: Sitemap;
}

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches all applications.
 *
 * Replaces `AppService.GetAllApplications()` with cache-first retrieval
 * from `ErpAppContext.Current.Cache`. The 5-minute staleTime mirrors the
 * monolith's in-memory cache behaviour.
 *
 * @returns TanStack Query result with `data` as `ApiResponse<App[]>`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } = useApps();
 * const apps = data?.object ?? [];
 * ```
 */
export function useApps() {
  return useQuery<ApiResponse<App[]>, Error>({
    queryKey: APP_KEYS.all,
    queryFn: () => get<App[]>('/apps'),
    staleTime: APPS_STALE_TIME_MS,
  });
}

/**
 * Fetches a single application by ID or name.
 *
 * Replaces both `AppService.GetApplication(Guid id)` and
 * `AppService.GetApplication(string name)` — the server accepts either
 * format in the path parameter.
 *
 * The query is automatically disabled when `idOrName` is falsy, preventing
 * unnecessary requests during route transitions.
 *
 * @param idOrName - Application GUID or URL-safe name
 * @returns TanStack Query result with `data` as `ApiResponse<App>`
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } = useApp('my-app');
 * const app = data?.object;
 * ```
 */
export function useApp(idOrName: string | undefined | null) {
  return useQuery<ApiResponse<App>, Error>({
    queryKey: APP_KEYS.detail(idOrName ?? ''),
    queryFn: () => get<App>(`/apps/${encodeURIComponent(idOrName!)}`),
    enabled: Boolean(idOrName),
    staleTime: APPS_STALE_TIME_MS,
  });
}

// ---------------------------------------------------------------------------
// App Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new application.
 *
 * Replaces `AppService.CreateApplication(id, name, label, description,
 * iconClass, author, color, weight, access)`. The server validates:
 * - Unique name (AddError "name" if duplicate)
 * - Required label (AddError "label" if missing)
 *
 * On success, invalidates the `['apps']` query key to refresh the app list.
 *
 * @returns TanStack Mutation result with `mutate`, `mutateAsync`, `isPending`,
 *   `isError`, `error`, `isSuccess`, `data`, `reset`
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending } = useCreateApp();
 * await mutateAsync({ app: { name: 'crm', label: 'CRM' } });
 * ```
 */
export function useCreateApp() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<App>, Error, CreateAppVariables>({
    mutationFn: (variables: CreateAppVariables) =>
      post<App>('/apps', variables.app),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Updates an existing application.
 *
 * Replaces `AppService.UpdateApplication(id, name, label, ...)`.
 * Server validates unique name and required label, then persists and
 * clears the monolith cache (`ClearAppCache(id)`).
 *
 * On success, invalidates both the app list and the specific app detail
 * query to ensure UI consistency.
 *
 * @returns TanStack Mutation result
 *
 * @example
 * ```tsx
 * const { mutateAsync } = useUpdateApp();
 * await mutateAsync({ id: appId, app: { id: appId, name: 'crm', label: 'CRM v2' } });
 * ```
 */
export function useUpdateApp() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<App>, Error, UpdateAppVariables>({
    mutationFn: (variables: UpdateAppVariables) =>
      put<App>(`/apps/${encodeURIComponent(variables.id)}`, variables.app),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.id),
      });
    },
  });
}

/**
 * Deletes an application with full server-side cascade.
 *
 * Replaces `AppService.DeleteApplication(id)` which performs:
 * 1. Delete all application pages (via PageService)
 * 2. Delete all sitemap areas (and their groups/nodes)
 * 3. Delete the application record
 *
 * On success, invalidates the app list and the pages query key since
 * the cascaded deletion may have removed pages.
 *
 * @returns TanStack Mutation result (no `data` — delete returns base response)
 *
 * @example
 * ```tsx
 * const { mutate } = useDeleteApp();
 * mutate({ id: appId });
 * ```
 */
export function useDeleteApp() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<BaseResponseModel>, Error, DeleteAppVariables>({
    mutationFn: (variables: DeleteAppVariables) =>
      del<BaseResponseModel>(`/apps/${encodeURIComponent(variables.id)}`),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.id),
      });
      // Cascaded deletion on the server removes pages bound to this app.
      // Invalidate pages queries so any active page list refreshes.
      queryClient.invalidateQueries({ queryKey: APP_KEYS.pages });
    },
  });
}

// ---------------------------------------------------------------------------
// Sitemap Area Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new sitemap area within an application.
 *
 * Replaces `AppService.CreateArea(id, appId, name, label,
 * labelTranslations, description, descriptionTranslations, iconClass,
 * color, weight, showGroupNames, accessRoles)`.
 *
 * Localization resources (TranslationResource[]) are sent as part of
 * the area payload. On success, invalidates the parent app query key
 * since areas are nested within the App.sitemap payload.
 *
 * @returns TanStack Mutation result
 *
 * @example
 * ```tsx
 * const { mutateAsync } = useCreateArea();
 * await mutateAsync({ appId, area: { name: 'contacts', label: 'Contacts' } });
 * ```
 */
export function useCreateArea() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<SitemapArea>, Error, CreateAreaVariables>({
    mutationFn: (variables: CreateAreaVariables) =>
      post<SitemapArea>(
        `/apps/${encodeURIComponent(variables.appId)}/areas`,
        variables.area,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Updates an existing sitemap area.
 *
 * Replaces `AppService.UpdateArea(id, appId, name, label, ...)`.
 * On success, invalidates the parent app to refresh the sitemap tree.
 *
 * @returns TanStack Mutation result
 */
export function useUpdateArea() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<SitemapArea>, Error, UpdateAreaVariables>({
    mutationFn: (variables: UpdateAreaVariables) =>
      put<SitemapArea>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}`,
        variables.area,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Deletes a sitemap area with server-side cascade.
 *
 * Replaces `AppService.DeleteArea(id)` which:
 * 1. Deletes all groups within the area
 * 2. Deletes all nodes within the area (cascading page unbind)
 * 3. Unbinds pages from the area via PageService
 * 4. Deletes the area record
 *
 * On success, invalidates the parent app query key.
 *
 * @returns TanStack Mutation result (no typed `data`)
 */
export function useDeleteArea() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<BaseResponseModel>, Error, DeleteAreaVariables>({
    mutationFn: (variables: DeleteAreaVariables) =>
      del<BaseResponseModel>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}`,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

// ---------------------------------------------------------------------------
// Sitemap Area Group Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new sitemap area group.
 *
 * Replaces `AppService.CreateAreaGroup(id, areaId, name, label,
 * labelTranslations, weight, renderRoles)`.
 *
 * On success, invalidates the parent app to refresh the sitemap tree.
 *
 * @returns TanStack Mutation result
 */
export function useCreateAreaGroup() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<SitemapGroup>, Error, CreateAreaGroupVariables>({
    mutationFn: (variables: CreateAreaGroupVariables) =>
      post<SitemapGroup>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}/groups`,
        variables.group,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Updates an existing sitemap area group.
 *
 * Replaces `AppService.UpdateAreaGroup(id, areaId, name, label,
 * labelTranslations, weight, renderRoles)`.
 *
 * On success, invalidates the parent app.
 *
 * @returns TanStack Mutation result
 */
export function useUpdateAreaGroup() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<SitemapGroup>, Error, UpdateAreaGroupVariables>({
    mutationFn: (variables: UpdateAreaGroupVariables) =>
      put<SitemapGroup>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}/groups/${encodeURIComponent(variables.groupId)}`,
        variables.group,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Deletes a sitemap area group.
 *
 * Replaces `AppService.DeleteAreaGroup(id)`.
 * On success, invalidates the parent app.
 *
 * @returns TanStack Mutation result (no typed `data`)
 */
export function useDeleteAreaGroup() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<BaseResponseModel>, Error, DeleteAreaGroupVariables>({
    mutationFn: (variables: DeleteAreaGroupVariables) =>
      del<BaseResponseModel>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}/groups/${encodeURIComponent(variables.groupId)}`,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

// ---------------------------------------------------------------------------
// Sitemap Area Node Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Creates a new sitemap area node.
 *
 * Replaces `AppService.CreateAreaNode(id, areaId, name, label,
 * labelTranslations, iconClass, url, type, entityId, weight, accessRoles,
 * entityListPages, entityCreatePages, entityDetailsPages,
 * entityManagePages, parentId)`.
 *
 * The node payload includes all entity page binding arrays and
 * localization resources as-is — the server handles serialization.
 *
 * On success, invalidates the parent app.
 *
 * @returns TanStack Mutation result
 */
export function useCreateAreaNode() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<SitemapNode>, Error, CreateAreaNodeVariables>({
    mutationFn: (variables: CreateAreaNodeVariables) =>
      post<SitemapNode>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}/nodes`,
        variables.node,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Updates an existing sitemap area node.
 *
 * Replaces `AppService.UpdateAreaNode(id, areaId, name, label,
 * labelTranslations, iconClass, url, type, entityId, weight, accessRoles,
 * entityListPages, entityCreatePages, entityDetailsPages,
 * entityManagePages, parentId)`.
 *
 * On success, invalidates the parent app.
 *
 * @returns TanStack Mutation result
 */
export function useUpdateAreaNode() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<SitemapNode>, Error, UpdateAreaNodeVariables>({
    mutationFn: (variables: UpdateAreaNodeVariables) =>
      put<SitemapNode>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}/nodes/${encodeURIComponent(variables.nodeId)}`,
        variables.node,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

/**
 * Deletes a sitemap area node with server-side cascade.
 *
 * Replaces `AppService.DeleteAreaNode(id)` which:
 * 1. Unbinds pages from the node via PageService
 * 2. Deletes the node record
 *
 * On success, invalidates the parent app.
 *
 * @returns TanStack Mutation result (no typed `data`)
 */
export function useDeleteAreaNode() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<BaseResponseModel>, Error, DeleteAreaNodeVariables>({
    mutationFn: (variables: DeleteAreaNodeVariables) =>
      del<BaseResponseModel>(
        `/apps/${encodeURIComponent(variables.appId)}/areas/${encodeURIComponent(variables.areaId)}/nodes/${encodeURIComponent(variables.nodeId)}`,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}

// ---------------------------------------------------------------------------
// Sitemap Ordering Hook
// ---------------------------------------------------------------------------

/**
 * Reorders a sitemap (areas and nodes sorted by weight, then name).
 *
 * Replaces `AppService.OrderSitemap(sitemap)` which performs:
 * ```csharp
 * sitemap.Areas.ForEach(x => {
 *   x.Nodes = x.Nodes.OrderBy(t => t.Weight).ThenBy(y => y.Name).ToList();
 * });
 * sitemap.Areas = sitemap.Areas.OrderBy(x => x.Weight).ThenBy(x => x.Name).ToList();
 * ```
 *
 * The server persists the new ordering. On success, invalidates the parent
 * app so the sitemap tree in the UI reflects the updated order.
 *
 * @returns TanStack Mutation result
 *
 * @example
 * ```tsx
 * const { mutateAsync } = useOrderSitemap();
 * await mutateAsync({ appId, sitemap: reorderedSitemap });
 * ```
 */
export function useOrderSitemap() {
  const queryClient = useQueryClient();

  return useMutation<ApiResponse<Sitemap>, Error, OrderSitemapVariables>({
    mutationFn: (variables: OrderSitemapVariables) =>
      put<Sitemap>(
        `/apps/${encodeURIComponent(variables.appId)}/sitemap/order`,
        variables.sitemap,
      ),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({
        queryKey: APP_KEYS.detail(variables.appId),
      });
      queryClient.invalidateQueries({ queryKey: APP_KEYS.all });
    },
  });
}
