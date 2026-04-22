/**
 * Vitest Unit Tests — `appStore` (Zustand 5 App Navigation Store)
 *
 * Comprehensive test suite for the `appStore` Zustand store that replaces the
 * monolith's per-request navigation context pipeline:
 *
 *  - `ErpRequestContext.cs`   — Scoped per-request container holding App,
 *                               SitemapArea, SitemapNode, Page, RecordId, etc.
 *                               SetCurrentApp(appName, areaName, nodeName)
 *                               resolves the hierarchy from route parameters.
 *  - `BaseErpPageModel.cs`    — Route-bound properties ([BindProperty] AppName,
 *                               AreaName, NodeName, PageName, RecordId, etc.),
 *                               Init() lines 183-350 build menus & breadcrumbs.
 *  - `RenderService.cs`       — ConvertListToTree(list, result, parentId) converts
 *                               flat MenuItem arrays into hierarchical menu trees.
 *  - `UserPreferencies.cs`    — Navigation-level sidebar state (active app/area/node).
 *
 * All tests use the `useAppStore.getState()` / `.setState()` pattern for
 * direct state manipulation — no React rendering is required.
 *
 * @see apps/frontend/src/stores/appStore.ts
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { useAppStore } from '../../../src/stores/appStore';
import type { BreadcrumbItem } from '../../../src/stores/appStore';
import type { App, SitemapArea, SitemapNode, MenuItem } from '../../../src/types/app';
import type { ErpPage } from '../../../src/types/page';
import { PageType } from '../../../src/types/page';

// ---------------------------------------------------------------------------
// Test Data Fixtures
// ---------------------------------------------------------------------------

/**
 * Mock SitemapNode fixture.
 * Represents a navigation node for "Tasks" linked to an entity list view.
 *
 * Source reference: SitemapNode fields defined in WebVella.Erp.Web/Models/SitemapNode.cs
 * SitemapNodeType.EntityList = 1, resolving via
 * SitemapArea.Nodes.FirstOrDefault(x => x.Name == nodeName) in ErpRequestContext.
 */
const mockSitemapNode: SitemapNode = {
  id: 'node-uuid-1',
  parentId: null,
  weight: 1,
  groupName: '',
  label: 'Tasks',
  name: 'tasks',
  iconClass: 'fa fa-tasks',
  url: '',
  labelTranslations: [],
  access: [],
  type: 1, // SitemapNodeType.EntityList
  entityId: 'entity-uuid-1',
  entityListPages: [],
  entityCreatePages: [],
  entityDetailsPages: [],
  entityManagePages: [],
};

/**
 * Second mock SitemapNode fixture for multi-node scenarios.
 * Represents an "Overview" application page node.
 */
const mockSitemapNode2: SitemapNode = {
  id: 'node-uuid-2',
  parentId: null,
  weight: 2,
  groupName: 'management',
  label: 'Overview',
  name: 'overview',
  iconClass: 'fa fa-tachometer-alt',
  url: '',
  labelTranslations: [],
  access: [],
  type: 2, // SitemapNodeType.ApplicationPage
  entityId: null,
  entityListPages: [],
  entityCreatePages: [],
  entityDetailsPages: [],
  entityManagePages: [],
};

/**
 * Mock SitemapArea fixture.
 * Represents the "Projects" area within the app's sitemap.
 *
 * Source reference: SitemapArea from WebVella.Erp.Web/Models/SitemapArea.cs
 * Resolved via App.Sitemap.Areas.FirstOrDefault(x => x.Name == areaName).
 */
const mockSitemapArea: SitemapArea = {
  id: 'area-uuid-1',
  appId: 'app-uuid-1',
  weight: 1,
  label: 'Projects',
  description: 'Project management area',
  name: 'projects',
  iconClass: 'fa fa-project-diagram',
  showGroupNames: false,
  color: '#2196F3',
  labelTranslations: [],
  descriptionTranslations: [],
  groups: [],
  nodes: [mockSitemapNode],
  access: [],
};

/**
 * Mock App fixture.
 * Represents the "Project Manager" application with one sitemap area.
 *
 * Source reference: App from WebVella.Erp.Web/Models/App.cs
 * Resolved via AppService.GetApplication(appName) in ErpRequestContext.SetCurrentApp().
 */
const mockApp: App = {
  id: 'app-uuid-1',
  name: 'project-manager',
  label: 'Project Manager',
  description: 'PM tool',
  iconClass: 'fa fa-clipboard',
  author: 'WebVella',
  color: '#2196F3',
  sitemap: { areas: [mockSitemapArea] },
  homePages: [],
  entities: [],
  weight: 1,
  access: [],
};

/**
 * Mock ErpPage fixture.
 * Represents a record list page with body tree.
 *
 * Source reference: ErpPage from WebVella.Erp.Web/Models/ErpPage.cs
 * Resolved via PageService.GetCurrentPage() in ErpRequestContext.
 */
const mockErpPage: ErpPage = {
  id: 'page-uuid-1',
  weight: 10,
  label: 'Task List',
  labelTranslations: [],
  name: 'task-list',
  iconClass: 'fa fa-list',
  system: false,
  type: PageType.RecordList,
  appId: 'app-uuid-1',
  entityId: 'entity-uuid-1',
  areaId: 'area-uuid-1',
  nodeId: 'node-uuid-1',
  isRazorBody: false,
  razorBody: '',
  layout: 'default',
  body: [],
};

/**
 * Mock BreadcrumbItem fixture — "Home" breadcrumb entry.
 *
 * Breadcrumbs are computed from the app → area → node → page hierarchy
 * during BaseErpPageModel.Init().
 */
const mockBreadcrumb: BreadcrumbItem = {
  label: 'Home',
  url: '/',
  isActive: false,
};

/**
 * Mock MenuItem fixture.
 * Represents a single flat menu item for "Tasks".
 *
 * Source reference: MenuItem from WebVella.Erp.Web/Models/MenuItem.cs
 * Menu trees are built from flat lists via RenderService.ConvertListToTree().
 */
const mockMenuItem: MenuItem = {
  id: 'menu-uuid-1',
  parentId: null,
  content: 'Tasks',
  class: '',
  isHtml: false,
  renderWrapper: true,
  nodes: [],
  isDropdownRight: false,
  sortOrder: 10,
};

/**
 * Second mock MenuItem fixture for multi-item scenarios.
 */
const mockMenuItem2: MenuItem = {
  id: 'menu-uuid-2',
  parentId: null,
  content: 'Dashboard',
  class: 'active',
  isHtml: false,
  renderWrapper: true,
  nodes: [],
  isDropdownRight: false,
  sortOrder: 5,
};

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

describe('appStore', () => {
  /**
   * Reset store state before each test to prevent cross-test state leakage.
   *
   * Uses useAppStore.setState() to replace the entire state with defaults.
   * This mirrors the "fresh request context" behavior of the monolith where
   * each HTTP request creates a new ErpRequestContext instance via DI.
   */
  beforeEach(() => {
    // Use merge mode (default) to preserve action functions while resetting data.
    // Using replace (true) would wipe out action methods like setCurrentApp, etc.
    useAppStore.setState({
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
    });
  });

  // -------------------------------------------------------------------------
  // Phase 3: Navigation State — currentApp, currentArea, currentNode
  // -------------------------------------------------------------------------

  describe('navigation state', () => {
    it('initial state has all navigation properties null/empty', () => {
      const state = useAppStore.getState();

      // Navigation context entities — all null initially
      expect(state.currentApp).toBe(null);
      expect(state.currentArea).toBe(null);
      expect(state.currentNode).toBe(null);
      expect(state.currentPage).toBe(null);

      // Route parameters — all empty strings initially
      expect(state.appName).toBe('');
      expect(state.areaName).toBe('');
      expect(state.nodeName).toBe('');
      expect(state.pageName).toBe('');

      // Nullable route IDs
      expect(state.recordId).toBe(null);
      expect(state.relationId).toBe(null);
      expect(state.parentRecordId).toBe(null);

      // Menu arrays — all empty initially
      expect(state.sidebarMenu).toEqual([]);
      expect(state.applicationMenu).toEqual([]);
      expect(state.siteMenu).toEqual([]);
      expect(state.toolbarMenu).toEqual([]);
      expect(state.userMenu).toEqual([]);

      // Breadcrumbs — empty initially
      expect(state.breadcrumbs).toEqual([]);
      expect(state.breadcrumbs).toHaveLength(0);

      // URL state — empty initially
      expect(state.returnUrl).toBe('');
      expect(state.currentUrl).toBe('');
    });

    it('setCurrentApp updates currentApp', () => {
      useAppStore.getState().setCurrentApp(mockApp);

      const state = useAppStore.getState();
      expect(state.currentApp).toEqual(mockApp);
      // Verify key App properties are accessible
      expect(state.currentApp!.id).toBe('app-uuid-1');
      expect(state.currentApp!.name).toBe('project-manager');
      expect(state.currentApp!.label).toBe('Project Manager');
      expect(state.currentApp!.description).toBe('PM tool');
      expect(state.currentApp!.iconClass).toBe('fa fa-clipboard');
      expect(state.currentApp!.author).toBe('WebVella');
      expect(state.currentApp!.color).toBe('#2196F3');
      expect(state.currentApp!.sitemap).not.toBeNull();
      expect(state.currentApp!.sitemap!.areas).toHaveLength(1);
      expect(state.currentApp!.homePages).toEqual([]);
      expect(state.currentApp!.entities).toEqual([]);
      expect(state.currentApp!.weight).toBe(1);
      expect(state.currentApp!.access).toEqual([]);
    });

    it('setCurrentApp accepts null to clear current app', () => {
      // First set an app
      useAppStore.getState().setCurrentApp(mockApp);
      expect(useAppStore.getState().currentApp).not.toBeNull();

      // Then clear it (mirrors clearing when appName is empty in ErpRequestContext)
      useAppStore.getState().setCurrentApp(null);
      expect(useAppStore.getState().currentApp).toBe(null);
    });

    it('setCurrentArea updates currentArea', () => {
      useAppStore.getState().setCurrentArea(mockSitemapArea);

      const state = useAppStore.getState();
      expect(state.currentArea).toEqual(mockSitemapArea);
      // Verify key SitemapArea properties
      expect(state.currentArea!.id).toBe('area-uuid-1');
      expect(state.currentArea!.appId).toBe('app-uuid-1');
      expect(state.currentArea!.weight).toBe(1);
      expect(state.currentArea!.label).toBe('Projects');
      expect(state.currentArea!.description).toBe('Project management area');
      expect(state.currentArea!.name).toBe('projects');
      expect(state.currentArea!.iconClass).toBe('fa fa-project-diagram');
      expect(state.currentArea!.showGroupNames).toBe(false);
      expect(state.currentArea!.color).toBe('#2196F3');
      expect(state.currentArea!.labelTranslations).toEqual([]);
      expect(state.currentArea!.descriptionTranslations).toEqual([]);
      expect(state.currentArea!.groups).toEqual([]);
      expect(state.currentArea!.nodes).toHaveLength(1);
      expect(state.currentArea!.access).toEqual([]);
    });

    it('setCurrentNode updates currentNode', () => {
      useAppStore.getState().setCurrentNode(mockSitemapNode);

      const state = useAppStore.getState();
      expect(state.currentNode).toEqual(mockSitemapNode);
      // Verify key SitemapNode properties
      expect(state.currentNode!.id).toBe('node-uuid-1');
      expect(state.currentNode!.parentId).toBe(null);
      expect(state.currentNode!.weight).toBe(1);
      expect(state.currentNode!.groupName).toBe('');
      expect(state.currentNode!.label).toBe('Tasks');
      expect(state.currentNode!.name).toBe('tasks');
      expect(state.currentNode!.iconClass).toBe('fa fa-tasks');
      expect(state.currentNode!.url).toBe('');
      expect(state.currentNode!.labelTranslations).toEqual([]);
      expect(state.currentNode!.access).toEqual([]);
      expect(state.currentNode!.type).toBe(1); // EntityList
      expect(state.currentNode!.entityId).toBe('entity-uuid-1');
      expect(state.currentNode!.entityListPages).toEqual([]);
      expect(state.currentNode!.entityCreatePages).toEqual([]);
      expect(state.currentNode!.entityDetailsPages).toEqual([]);
      expect(state.currentNode!.entityManagePages).toEqual([]);
    });
  });

  // -------------------------------------------------------------------------
  // Phase 4: Route Parameters — setRouteParams
  // -------------------------------------------------------------------------

  describe('setRouteParams', () => {
    it('updates all route parameters at once', () => {
      useAppStore.getState().setRouteParams({
        appName: 'pm',
        areaName: 'projects',
        nodeName: 'tasks',
        pageName: 'list',
        recordId: 'rec-uuid-1',
        relationId: 'rel-uuid-1',
        parentRecordId: 'parent-rec-uuid-1',
      });

      const state = useAppStore.getState();
      expect(state.appName).toBe('pm');
      expect(state.areaName).toBe('projects');
      expect(state.nodeName).toBe('tasks');
      expect(state.pageName).toBe('list');
      expect(state.recordId).toBe('rec-uuid-1');
      expect(state.relationId).toBe('rel-uuid-1');
      expect(state.parentRecordId).toBe('parent-rec-uuid-1');
    });

    it('with partial params only updates provided fields', () => {
      // First set all route params
      useAppStore.getState().setRouteParams({
        appName: 'pm',
        areaName: 'projects',
        nodeName: 'tasks',
        pageName: 'list',
        recordId: 'rec-uuid-1',
        relationId: 'rel-uuid-1',
        parentRecordId: 'parent-rec-uuid-1',
      });

      // Then partially update — only change appName
      useAppStore.getState().setRouteParams({ appName: 'crm' });

      const state = useAppStore.getState();
      // Updated field
      expect(state.appName).toBe('crm');
      // Retained previous values (not overwritten)
      expect(state.areaName).toBe('projects');
      expect(state.nodeName).toBe('tasks');
      expect(state.pageName).toBe('list');
      expect(state.recordId).toBe('rec-uuid-1');
      expect(state.relationId).toBe('rel-uuid-1');
      expect(state.parentRecordId).toBe('parent-rec-uuid-1');
    });

    it('handles null recordId/relationId/parentRecordId', () => {
      // First set IDs to specific values
      useAppStore.getState().setRouteParams({
        recordId: 'some-record-id',
        relationId: 'some-relation-id',
        parentRecordId: 'some-parent-record-id',
      });

      expect(useAppStore.getState().recordId).toBe('some-record-id');
      expect(useAppStore.getState().relationId).toBe('some-relation-id');
      expect(useAppStore.getState().parentRecordId).toBe('some-parent-record-id');

      // Then explicitly set them to null
      useAppStore.getState().setRouteParams({
        recordId: null,
        relationId: null,
        parentRecordId: null,
      });

      const state = useAppStore.getState();
      expect(state.recordId).toBe(null);
      expect(state.relationId).toBe(null);
      expect(state.parentRecordId).toBe(null);
    });
  });

  // -------------------------------------------------------------------------
  // Phase 5: Breadcrumbs
  // -------------------------------------------------------------------------

  describe('breadcrumbs', () => {
    it('setBreadcrumbs sets breadcrumb array', () => {
      const breadcrumbs: BreadcrumbItem[] = [
        { label: 'Home', url: '/', isActive: false },
        { label: 'Projects', url: '/pm/projects', isActive: true },
      ];

      useAppStore.getState().setBreadcrumbs(breadcrumbs);

      const state = useAppStore.getState();
      expect(state.breadcrumbs).toHaveLength(2);
      expect(state.breadcrumbs[0].label).toBe('Home');
      expect(state.breadcrumbs[0].url).toBe('/');
      expect(state.breadcrumbs[0].isActive).toBe(false);
      expect(state.breadcrumbs[1].label).toBe('Projects');
      expect(state.breadcrumbs[1].url).toBe('/pm/projects');
      expect(state.breadcrumbs[1].isActive).toBe(true);
    });

    it('breadcrumbs default to empty array', () => {
      const state = useAppStore.getState();
      expect(state.breadcrumbs).toEqual([]);
      expect(state.breadcrumbs).toHaveLength(0);
    });

    it('setBreadcrumbs replaces existing breadcrumbs entirely', () => {
      // Set initial breadcrumbs
      useAppStore.getState().setBreadcrumbs([
        { label: 'Home', url: '/', isActive: false },
        { label: 'Projects', url: '/pm/projects', isActive: true },
      ]);
      expect(useAppStore.getState().breadcrumbs).toHaveLength(2);

      // Replace with a single breadcrumb — verifies it replaces, not appends
      useAppStore.getState().setBreadcrumbs([
        { label: 'Dashboard', url: '/dashboard', isActive: true },
      ]);

      const state = useAppStore.getState();
      expect(state.breadcrumbs).toHaveLength(1);
      expect(state.breadcrumbs[0].label).toBe('Dashboard');
    });
  });

  // -------------------------------------------------------------------------
  // Phase 6: Menu Items — Sidebar, Application, Site, Toolbar, User
  // -------------------------------------------------------------------------

  describe('menu items', () => {
    it('setSidebarMenu updates sidebar menu items', () => {
      useAppStore.getState().setSidebarMenu([mockMenuItem, mockMenuItem2]);

      const state = useAppStore.getState();
      expect(state.sidebarMenu).toHaveLength(2);
      expect(state.sidebarMenu[0].id).toBe('menu-uuid-1');
      expect(state.sidebarMenu[0].content).toBe('Tasks');
      expect(state.sidebarMenu[0].sortOrder).toBe(10);
      expect(state.sidebarMenu[1].id).toBe('menu-uuid-2');
      expect(state.sidebarMenu[1].content).toBe('Dashboard');
      expect(state.sidebarMenu[1].class).toBe('active');
      expect(state.sidebarMenu[1].sortOrder).toBe(5);
    });

    it('setApplicationMenu updates application menu items', () => {
      useAppStore.getState().setApplicationMenu([mockMenuItem]);

      const state = useAppStore.getState();
      expect(state.applicationMenu).toHaveLength(1);
      expect(state.applicationMenu[0].id).toBe('menu-uuid-1');
      expect(state.applicationMenu[0].content).toBe('Tasks');
      expect(state.applicationMenu[0].parentId).toBe(null);
      expect(state.applicationMenu[0].isHtml).toBe(false);
      expect(state.applicationMenu[0].renderWrapper).toBe(true);
      expect(state.applicationMenu[0].isDropdownRight).toBe(false);
    });

    it('menu items support nested tree structure via MenuItem.nodes', () => {
      // Build a nested tree — mirrors RenderService.ConvertListToTree()
      const childMenuItem: MenuItem = {
        id: 'child-menu-uuid-1',
        parentId: 'menu-uuid-1',
        content: 'Sub-Task List',
        class: '',
        isHtml: false,
        renderWrapper: true,
        nodes: [],
        isDropdownRight: false,
        sortOrder: 1,
      };

      const parentWithChildren: MenuItem = {
        ...mockMenuItem,
        nodes: [childMenuItem],
      };

      useAppStore.getState().setSidebarMenu([parentWithChildren]);

      const state = useAppStore.getState();
      expect(state.sidebarMenu).toHaveLength(1);
      expect(state.sidebarMenu[0].nodes).toHaveLength(1);
      expect(state.sidebarMenu[0].nodes[0].id).toBe('child-menu-uuid-1');
      expect(state.sidebarMenu[0].nodes[0].parentId).toBe('menu-uuid-1');
      expect(state.sidebarMenu[0].nodes[0].content).toBe('Sub-Task List');
    });

    it('setSiteMenu updates site-wide menu items', () => {
      useAppStore.getState().setSiteMenu([mockMenuItem]);

      const state = useAppStore.getState();
      expect(state.siteMenu).toHaveLength(1);
      expect(state.siteMenu[0].content).toBe('Tasks');
    });

    it('setToolbarMenu updates toolbar action items', () => {
      useAppStore.getState().setToolbarMenu([mockMenuItem]);

      const state = useAppStore.getState();
      expect(state.toolbarMenu).toHaveLength(1);
      expect(state.toolbarMenu[0].content).toBe('Tasks');
    });

    it('setUserMenu updates user-specific menu items', () => {
      useAppStore.getState().setUserMenu([mockMenuItem, mockMenuItem2]);

      const state = useAppStore.getState();
      expect(state.userMenu).toHaveLength(2);
      expect(state.userMenu[0].content).toBe('Tasks');
      expect(state.userMenu[1].content).toBe('Dashboard');
    });
  });

  // -------------------------------------------------------------------------
  // Phase 8: Composite Navigation Update — updateNavigationContext
  // -------------------------------------------------------------------------

  describe('updateNavigationContext', () => {
    it('sets all navigation context at once', () => {
      useAppStore.getState().updateNavigationContext({
        app: mockApp,
        area: mockSitemapArea,
        node: mockSitemapNode,
        page: mockErpPage,
        breadcrumbs: [mockBreadcrumb],
        menus: {
          sidebar: [mockMenuItem],
          application: [mockMenuItem2],
          site: [mockMenuItem],
          toolbar: [],
          user: [mockMenuItem2],
        },
      });

      const state = useAppStore.getState();

      // Navigation context entities
      expect(state.currentApp).toEqual(mockApp);
      expect(state.currentArea).toEqual(mockSitemapArea);
      expect(state.currentNode).toEqual(mockSitemapNode);
      expect(state.currentPage).toEqual(mockErpPage);

      // Breadcrumbs
      expect(state.breadcrumbs).toHaveLength(1);
      expect(state.breadcrumbs[0].label).toBe('Home');
      expect(state.breadcrumbs[0].isActive).toBe(false);

      // Menus
      expect(state.sidebarMenu).toHaveLength(1);
      expect(state.sidebarMenu[0].content).toBe('Tasks');
      expect(state.applicationMenu).toHaveLength(1);
      expect(state.applicationMenu[0].content).toBe('Dashboard');
      expect(state.siteMenu).toHaveLength(1);
      expect(state.toolbarMenu).toHaveLength(0);
      expect(state.userMenu).toHaveLength(1);
    });

    it('with partial context only updates provided fields', () => {
      // First set full context
      useAppStore.getState().updateNavigationContext({
        app: mockApp,
        area: mockSitemapArea,
        node: mockSitemapNode,
        breadcrumbs: [mockBreadcrumb],
        menus: {
          sidebar: [mockMenuItem],
        },
      });

      // Verify initial state
      expect(useAppStore.getState().currentApp).toEqual(mockApp);
      expect(useAppStore.getState().currentArea).toEqual(mockSitemapArea);
      expect(useAppStore.getState().currentNode).toEqual(mockSitemapNode);
      expect(useAppStore.getState().sidebarMenu).toHaveLength(1);

      // Partially update — only clear the app
      useAppStore.getState().updateNavigationContext({ app: null });

      const state = useAppStore.getState();
      // App was explicitly set to null
      expect(state.currentApp).toBe(null);
      // Other fields retain previous values (not provided in update)
      expect(state.currentArea).toEqual(mockSitemapArea);
      expect(state.currentNode).toEqual(mockSitemapNode);
      expect(state.breadcrumbs).toHaveLength(1);
      expect(state.sidebarMenu).toHaveLength(1);
    });

    it('updates only provided menu types in the menus object', () => {
      // Set initial menus
      useAppStore.getState().updateNavigationContext({
        menus: {
          sidebar: [mockMenuItem, mockMenuItem2],
          application: [mockMenuItem],
        },
      });

      expect(useAppStore.getState().sidebarMenu).toHaveLength(2);
      expect(useAppStore.getState().applicationMenu).toHaveLength(1);

      // Update only sidebar — application should remain unchanged
      useAppStore.getState().updateNavigationContext({
        menus: {
          sidebar: [mockMenuItem],
        },
      });

      const state = useAppStore.getState();
      expect(state.sidebarMenu).toHaveLength(1);
      // applicationMenu was not included in the menus update — retained
      expect(state.applicationMenu).toHaveLength(1);
    });
  });

  // -------------------------------------------------------------------------
  // Phase 9: URL State
  // -------------------------------------------------------------------------

  describe('URL state', () => {
    it('setReturnUrl updates returnUrl', () => {
      useAppStore.getState().setReturnUrl('/previous-page');

      const state = useAppStore.getState();
      expect(state.returnUrl).toBe('/previous-page');
    });

    it('setCurrentUrl updates currentUrl', () => {
      useAppStore.getState().setCurrentUrl('/pm/projects/tasks/l/list');

      const state = useAppStore.getState();
      expect(state.currentUrl).toBe('/pm/projects/tasks/l/list');
    });

    it('setReturnUrl with empty string clears returnUrl', () => {
      useAppStore.getState().setReturnUrl('/some-page');
      expect(useAppStore.getState().returnUrl).toBe('/some-page');

      useAppStore.getState().setReturnUrl('');
      expect(useAppStore.getState().returnUrl).toBe('');
    });
  });

  // -------------------------------------------------------------------------
  // Phase 10: Reset
  // -------------------------------------------------------------------------

  describe('resetAppState', () => {
    it('restores all defaults after full navigation context is set', () => {
      // Set up a comprehensive navigation state
      useAppStore.getState().setCurrentApp(mockApp);
      useAppStore.getState().setCurrentArea(mockSitemapArea);
      useAppStore.getState().setCurrentNode(mockSitemapNode);
      useAppStore.getState().setCurrentPage(mockErpPage);
      useAppStore.getState().setRouteParams({
        appName: 'pm',
        areaName: 'projects',
        nodeName: 'tasks',
        pageName: 'list',
        recordId: 'rec-uuid-1',
        relationId: 'rel-uuid-1',
        parentRecordId: 'parent-rec-uuid-1',
      });
      useAppStore.getState().setSidebarMenu([mockMenuItem]);
      useAppStore.getState().setApplicationMenu([mockMenuItem]);
      useAppStore.getState().setSiteMenu([mockMenuItem]);
      useAppStore.getState().setToolbarMenu([mockMenuItem]);
      useAppStore.getState().setUserMenu([mockMenuItem]);
      useAppStore.getState().setBreadcrumbs([mockBreadcrumb]);
      useAppStore.getState().setReturnUrl('/previous');
      useAppStore.getState().setCurrentUrl('/pm/projects/tasks');

      // Verify state is populated before reset
      expect(useAppStore.getState().currentApp).not.toBeNull();
      expect(useAppStore.getState().appName).toBe('pm');
      expect(useAppStore.getState().sidebarMenu).toHaveLength(1);
      expect(useAppStore.getState().breadcrumbs).toHaveLength(1);
      expect(useAppStore.getState().returnUrl).toBe('/previous');
      expect(useAppStore.getState().currentUrl).toBe('/pm/projects/tasks');

      // Execute reset
      useAppStore.getState().resetAppState();

      // Verify all properties are back to defaults
      const state = useAppStore.getState();
      expect(state.currentApp).toBe(null);
      expect(state.currentArea).toBe(null);
      expect(state.currentNode).toBe(null);
      expect(state.currentPage).toBe(null);
      expect(state.appName).toBe('');
      expect(state.areaName).toBe('');
      expect(state.nodeName).toBe('');
      expect(state.pageName).toBe('');
      expect(state.recordId).toBe(null);
      expect(state.relationId).toBe(null);
      expect(state.parentRecordId).toBe(null);
      expect(state.sidebarMenu).toEqual([]);
      expect(state.applicationMenu).toEqual([]);
      expect(state.siteMenu).toEqual([]);
      expect(state.toolbarMenu).toEqual([]);
      expect(state.userMenu).toEqual([]);
      expect(state.breadcrumbs).toEqual([]);
      expect(state.returnUrl).toBe('');
      expect(state.currentUrl).toBe('');
    });
  });

  // -------------------------------------------------------------------------
  // Phase 11: State Isolation
  // -------------------------------------------------------------------------

  describe('state isolation', () => {
    it('state does not leak between tests — verified by beforeEach reset', () => {
      // This test verifies that the beforeEach() reset is effective.
      // If any previous test leaked state, these assertions would fail.
      const state = useAppStore.getState();
      expect(state.currentApp).toBe(null);
      expect(state.currentArea).toBe(null);
      expect(state.currentNode).toBe(null);
      expect(state.currentPage).toBe(null);
      expect(state.appName).toBe('');
      expect(state.areaName).toBe('');
      expect(state.nodeName).toBe('');
      expect(state.pageName).toBe('');
      expect(state.recordId).toBe(null);
      expect(state.relationId).toBe(null);
      expect(state.parentRecordId).toBe(null);
      expect(state.sidebarMenu).toEqual([]);
      expect(state.applicationMenu).toEqual([]);
      expect(state.siteMenu).toEqual([]);
      expect(state.toolbarMenu).toEqual([]);
      expect(state.userMenu).toEqual([]);
      expect(state.breadcrumbs).toEqual([]);
      expect(state.returnUrl).toBe('');
      expect(state.currentUrl).toBe('');
    });

    it('modifying state in one scope does not affect subsequent reads after reset', () => {
      // Set complex state
      useAppStore.getState().setCurrentApp(mockApp);
      useAppStore.getState().setSidebarMenu([mockMenuItem, mockMenuItem2]);
      useAppStore.getState().setRouteParams({ appName: 'test-app', recordId: 'rec-id' });

      // Verify modifications are visible
      expect(useAppStore.getState().currentApp).toEqual(mockApp);
      expect(useAppStore.getState().sidebarMenu).toHaveLength(2);
      expect(useAppStore.getState().appName).toBe('test-app');
      expect(useAppStore.getState().recordId).toBe('rec-id');

      // Simulate the beforeEach reset (merge mode preserves actions)
      useAppStore.setState({
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
      });

      // Verify clean state
      expect(useAppStore.getState().currentApp).toBe(null);
      expect(useAppStore.getState().sidebarMenu).toEqual([]);
      expect(useAppStore.getState().appName).toBe('');
      expect(useAppStore.getState().recordId).toBe(null);
    });
  });
});
