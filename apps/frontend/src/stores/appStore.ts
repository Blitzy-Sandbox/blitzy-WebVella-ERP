/**
 * Application Navigation Zustand Store
 *
 * Replaces the monolith's per-request context pipeline:
 * - ErpRequestContext.cs — Scoped per-request container (App, SitemapArea, SitemapNode, Page, RecordId, etc.)
 * - BaseErpPageModel.cs — Route-bound properties, menu building (Init lines 183-350), URL state
 * - UserPreferencies.cs — Navigation-level sidebar state (active app/area/node)
 *
 * This store is thin — it holds resolved navigation state only.
 * Route resolution, data fetching, and menu building happen in hooks/components
 * via React Router (useParams) and TanStack Query (API calls).
 * The store receives the resolved results and exposes them to the component tree.
 *
 * No localStorage persistence — navigation state is URL-driven and transient.
 * No server data fetching — that is TanStack Query's responsibility.
 */

import { create } from 'zustand';
import type { App, SitemapArea, SitemapNode, MenuItem } from '../types/app';
import type { ErpPage } from '../types/page';

// ---------------------------------------------------------------------------
// BreadcrumbItem — React-specific navigation helper (not from source DTOs)
// Computed from the app/area/node/page hierarchy during route resolution.
// ---------------------------------------------------------------------------
export interface BreadcrumbItem {
  /** Display label for the breadcrumb segment */
  label: string;
  /** Navigation URL for this breadcrumb segment */
  url: string;
  /** Whether this breadcrumb is the currently active (last) segment */
  isActive: boolean;
}

// ---------------------------------------------------------------------------
// AppState — Full navigation state interface
// Maps directly to ErpRequestContext + BaseErpPageModel properties and actions.
// ---------------------------------------------------------------------------
export interface AppState {
  // -- Current navigation context (replaces ErpRequestContext properties) --

  /**
   * Currently active application resolved from the URL appName parameter.
   * Replaces ErpRequestContext.App, resolved via AppService.GetApplication(appName).
   * Provides access to App.id, App.name, App.label, App.sitemap, App.access.
   */
  currentApp: App | null;

  /**
   * Currently active sitemap area resolved from the URL areaName parameter.
   * Replaces ErpRequestContext.SitemapArea, resolved via
   * App.Sitemap.Areas.FirstOrDefault(x => x.Name == areaName).
   * Provides access to SitemapArea.id, SitemapArea.name, SitemapArea.label,
   * SitemapArea.nodes, SitemapArea.access.
   */
  currentArea: SitemapArea | null;

  /**
   * Currently active sitemap node resolved from the URL nodeName parameter.
   * Replaces ErpRequestContext.SitemapNode, resolved via
   * SitemapArea.Nodes.FirstOrDefault(x => x.Name == nodeName).
   * Provides access to SitemapNode.id, SitemapNode.name, SitemapNode.label,
   * SitemapNode.type, SitemapNode.entityId, SitemapNode.url.
   */
  currentNode: SitemapNode | null;

  /**
   * Currently active ERP page resolved via PageService.GetCurrentPage().
   * Replaces ErpRequestContext.Page.
   * Provides access to ErpPage.id, ErpPage.name, ErpPage.label, ErpPage.type.
   */
  currentPage: ErpPage | null;

  // -- Route parameters (replaces BaseErpPageModel bound properties) --

  /** Application name from route segment. Replaces BaseErpPageModel.AppName. */
  appName: string;
  /** Area name from route segment. Replaces BaseErpPageModel.AreaName. */
  areaName: string;
  /** Node name from route segment. Replaces BaseErpPageModel.NodeName. */
  nodeName: string;
  /** Page name from route segment. Replaces BaseErpPageModel.PageName. */
  pageName: string;
  /** Record ID from route. Replaces BaseErpPageModel.RecordId (Guid?). */
  recordId: string | null;
  /** Relation ID from route. Replaces BaseErpPageModel.RelationId (Guid?). */
  relationId: string | null;
  /** Parent record ID from route. Replaces BaseErpPageModel.ParentRecordId (Guid?). */
  parentRecordId: string | null;

  // -- Navigation menus (replaces BaseErpPageModel menu lists) --
  // Built from sitemap in Init() lines 183-350.
  // MenuItem arrays use MenuItem.id, MenuItem.parentId, MenuItem.content,
  // MenuItem.class, MenuItem.nodes, MenuItem.sortOrder properties.

  /** Sidebar menu items for the current area's nodes. */
  sidebarMenu: MenuItem[];
  /** Application menu items — top-level areas with nested node links. */
  applicationMenu: MenuItem[];
  /** Site-wide menu items from PageService.GetSitePages(). */
  siteMenu: MenuItem[];
  /** Toolbar action buttons for the current page context. */
  toolbarMenu: MenuItem[];
  /** User-specific action menu items. */
  userMenu: MenuItem[];

  // -- Breadcrumbs (computed from navigation context) --

  /** Breadcrumb trail computed from app → area → node → page hierarchy. */
  breadcrumbs: BreadcrumbItem[];

  // -- URL state (replaces BaseErpPageModel URL properties) --

  /** Return URL for back-navigation. Replaces BaseErpPageModel.ReturnUrl. */
  returnUrl: string;
  /** Current canonical URL. Replaces BaseErpPageModel.CurrentUrl. */
  currentUrl: string;

  // -- Individual setter actions --

  /** Set the current application. */
  setCurrentApp: (app: App | null) => void;
  /** Set the current sitemap area. */
  setCurrentArea: (area: SitemapArea | null) => void;
  /** Set the current sitemap node. */
  setCurrentNode: (node: SitemapNode | null) => void;
  /** Set the current ERP page. */
  setCurrentPage: (page: ErpPage | null) => void;

  // -- Route parameter actions --

  /**
   * Batch update route parameters. Accepts a partial object and merges
   * with current route state. Replaces the multi-step route binding from
   * BaseErpPageModel's [BindProperty] attributes.
   */
  setRouteParams: (params: {
    appName?: string;
    areaName?: string;
    nodeName?: string;
    pageName?: string;
    recordId?: string | null;
    relationId?: string | null;
    parentRecordId?: string | null;
  }) => void;

  // -- Navigation menu actions --

  /** Set sidebar menu items for the current area's nodes. */
  setSidebarMenu: (items: MenuItem[]) => void;
  /** Set application menu items (areas with nested nodes). */
  setApplicationMenu: (items: MenuItem[]) => void;
  /** Set site-wide menu items. */
  setSiteMenu: (items: MenuItem[]) => void;
  /** Set toolbar action items for the current page. */
  setToolbarMenu: (items: MenuItem[]) => void;
  /** Set user-specific action menu items. */
  setUserMenu: (items: MenuItem[]) => void;

  // -- Breadcrumb actions --

  /** Set the breadcrumb trail for the current navigation context. */
  setBreadcrumbs: (items: BreadcrumbItem[]) => void;

  // -- URL state actions --

  /** Set the return URL for back-navigation. */
  setReturnUrl: (url: string) => void;
  /** Set the current canonical URL. */
  setCurrentUrl: (url: string) => void;

  // -- Composite navigation update --

  /**
   * Batch update the entire navigation context at once.
   * Replaces the multi-step BaseErpPageModel.Init() flow:
   *   1. SetCurrentApp(appName, areaName, nodeName)
   *   2. SetCurrentPage(pageContext, pageName, ...)
   *   3. Build navigation menus from sitemap
   *   4. Build breadcrumbs from hierarchy
   *
   * All provided fields are merged into the current state in a single
   * Zustand update, preventing unnecessary intermediate re-renders.
   */
  updateNavigationContext: (context: {
    app?: App | null;
    area?: SitemapArea | null;
    node?: SitemapNode | null;
    page?: ErpPage | null;
    breadcrumbs?: BreadcrumbItem[];
    menus?: {
      sidebar?: MenuItem[];
      application?: MenuItem[];
      site?: MenuItem[];
      toolbar?: MenuItem[];
      user?: MenuItem[];
    };
  }) => void;

  // -- Reset --

  /** Reset all navigation state to defaults. Used on logout or full navigation reset. */
  resetAppState: () => void;
}

// ---------------------------------------------------------------------------
// Default state values
// All navigation state starts empty/null and is populated on route resolution.
// No localStorage persistence — state is URL-driven and re-resolved per navigation.
// ---------------------------------------------------------------------------
const defaultAppState: Omit<
  AppState,
  | 'setCurrentApp'
  | 'setCurrentArea'
  | 'setCurrentNode'
  | 'setCurrentPage'
  | 'setRouteParams'
  | 'setSidebarMenu'
  | 'setApplicationMenu'
  | 'setSiteMenu'
  | 'setToolbarMenu'
  | 'setUserMenu'
  | 'setBreadcrumbs'
  | 'setReturnUrl'
  | 'setCurrentUrl'
  | 'updateNavigationContext'
  | 'resetAppState'
> = {
  currentApp: null,
  currentArea: null,
  currentNode: null,
  currentPage: null,
  appName: '',
  areaName: '',
  nodeName: '',
  pageName: '',
  recordId: null,
  relationId: null,
  parentRecordId: null,
  sidebarMenu: [],
  applicationMenu: [],
  siteMenu: [],
  toolbarMenu: [],
  userMenu: [],
  breadcrumbs: [],
  returnUrl: '',
  currentUrl: '',
};

// ---------------------------------------------------------------------------
// Store creation — Zustand 5 with TypeScript
// ---------------------------------------------------------------------------
export const useAppStore = create<AppState>()((set) => ({
  // Spread all default state values
  ...defaultAppState,

  // -- Individual setter actions --

  setCurrentApp: (app: App | null) => {
    set({ currentApp: app });
  },

  setCurrentArea: (area: SitemapArea | null) => {
    set({ currentArea: area });
  },

  setCurrentNode: (node: SitemapNode | null) => {
    set({ currentNode: node });
  },

  setCurrentPage: (page: ErpPage | null) => {
    set({ currentPage: page });
  },

  // -- Route parameter batch action --

  setRouteParams: (params) => {
    set((state) => ({
      appName: params.appName !== undefined ? params.appName : state.appName,
      areaName: params.areaName !== undefined ? params.areaName : state.areaName,
      nodeName: params.nodeName !== undefined ? params.nodeName : state.nodeName,
      pageName: params.pageName !== undefined ? params.pageName : state.pageName,
      recordId: params.recordId !== undefined ? params.recordId : state.recordId,
      relationId: params.relationId !== undefined ? params.relationId : state.relationId,
      parentRecordId:
        params.parentRecordId !== undefined
          ? params.parentRecordId
          : state.parentRecordId,
    }));
  },

  // -- Navigation menu actions --

  setSidebarMenu: (items: MenuItem[]) => {
    set({ sidebarMenu: items });
  },

  setApplicationMenu: (items: MenuItem[]) => {
    set({ applicationMenu: items });
  },

  setSiteMenu: (items: MenuItem[]) => {
    set({ siteMenu: items });
  },

  setToolbarMenu: (items: MenuItem[]) => {
    set({ toolbarMenu: items });
  },

  setUserMenu: (items: MenuItem[]) => {
    set({ userMenu: items });
  },

  // -- Breadcrumb actions --

  setBreadcrumbs: (items: BreadcrumbItem[]) => {
    set({ breadcrumbs: items });
  },

  // -- URL state actions --

  setReturnUrl: (url: string) => {
    set({ returnUrl: url });
  },

  setCurrentUrl: (url: string) => {
    set({ currentUrl: url });
  },

  // -- Composite navigation update --
  // Merges all provided context fields in a single Zustand update.
  // Replaces the sequential Init() flow from BaseErpPageModel.

  updateNavigationContext: (context) => {
    set((state) => {
      const nextState: Partial<AppState> = {};

      // Navigation context entities
      if (context.app !== undefined) {
        nextState.currentApp = context.app;
      }
      if (context.area !== undefined) {
        nextState.currentArea = context.area;
      }
      if (context.node !== undefined) {
        nextState.currentNode = context.node;
      }
      if (context.page !== undefined) {
        nextState.currentPage = context.page;
      }

      // Breadcrumbs
      if (context.breadcrumbs !== undefined) {
        nextState.breadcrumbs = context.breadcrumbs;
      }

      // Navigation menus — only update those explicitly provided
      if (context.menus) {
        if (context.menus.sidebar !== undefined) {
          nextState.sidebarMenu = context.menus.sidebar;
        }
        if (context.menus.application !== undefined) {
          nextState.applicationMenu = context.menus.application;
        }
        if (context.menus.site !== undefined) {
          nextState.siteMenu = context.menus.site;
        }
        if (context.menus.toolbar !== undefined) {
          nextState.toolbarMenu = context.menus.toolbar;
        }
        if (context.menus.user !== undefined) {
          nextState.userMenu = context.menus.user;
        }
      }

      return nextState;
    });
  },

  // -- Reset --
  // Returns all state to defaults. Called on logout or full navigation reset.

  resetAppState: () => {
    set({ ...defaultAppState });
  },
}));

// ---------------------------------------------------------------------------
// Typed selector hooks
// Provide granular subscriptions to prevent unnecessary re-renders.
// Components subscribe only to the slice of state they need.
// ---------------------------------------------------------------------------

/** Select the current application. */
export const useCurrentApp = () => useAppStore((state) => state.currentApp);

/** Select the current sitemap area. */
export const useCurrentArea = () => useAppStore((state) => state.currentArea);

/** Select the current sitemap node. */
export const useCurrentNode = () => useAppStore((state) => state.currentNode);

/** Select the current ERP page. */
export const useCurrentPage = () => useAppStore((state) => state.currentPage);

/** Select the breadcrumb trail. */
export const useBreadcrumbs = () => useAppStore((state) => state.breadcrumbs);

/** Select sidebar menu items. */
export const useSidebarMenu = () => useAppStore((state) => state.sidebarMenu);

/** Select application menu items. */
export const useApplicationMenu = () =>
  useAppStore((state) => state.applicationMenu);

/**
 * Select all route parameters as a single object.
 * Returns a new object reference on every call — consumers should use
 * shallow comparison or memoize if needed.
 */
export const useRouteParams = () =>
  useAppStore((state) => ({
    appName: state.appName,
    areaName: state.areaName,
    nodeName: state.nodeName,
    pageName: state.pageName,
    recordId: state.recordId,
    relationId: state.relationId,
    parentRecordId: state.parentRecordId,
  }));
