/**
 * @file TopNav.test.tsx
 * @description Vitest component tests for the <TopNav> component that replaces the
 * monolith's Nav ViewComponent (NavViewComponent.cs + Nav.Default.cshtml + script.js).
 *
 * Covers: navigation bar rendering, home link, brand logo with app label,
 * dev-mode banner, site-menu / area dropdown toggle behaviour (replacing
 * jQuery's data-navclick-handler), outside-click closing, mutual-exclusion
 * of dropdowns, UserMenu presence, search placeholder, Tailwind styling,
 * and computeAppDefaultLink route computation.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import TopNav from '../../../src/components/layout/TopNav';

// ----------------------------------------------------------------
// Mutable mock state — reset in beforeEach
// ----------------------------------------------------------------
let mockCurrentApp: any = null;
let mockSiteMenu: any[] = [];
let mockApplicationMenu: any[] = [];

/** Auth store mock state (schema-required for UserMenu transitive dep). */
let mockCurrentUser: any = {
  id: 'usr-001',
  email: 'test@webvella.com',
  firstName: 'Test',
  lastName: 'User',
  image: null,
  roles: ['administrator'],
  isAdmin: true,
};
let mockIsAuthenticated = true;
const mockLogoutSuccess = vi.fn();

// ----------------------------------------------------------------
// Module mocks — vi.mock is hoisted by vitest
// ----------------------------------------------------------------

/**
 * Mock useAppStore with the selector pattern established in Sidebar.test.tsx.
 * TopNav calls useAppStore three times with different selectors for
 * currentApp, siteMenu, and applicationMenu.
 */
vi.mock('../../../src/stores/appStore', () => ({
  useAppStore: (selector: any) =>
    selector({
      currentApp: mockCurrentApp,
      siteMenu: mockSiteMenu,
      applicationMenu: mockApplicationMenu,
    }),
}));

/**
 * Mock useAuthStore (required by schema for UserMenu transitive dependency).
 * Even though TopNav does not directly import useAuthStore, this mock
 * satisfies the schema's internal_imports contract and covers the child
 * UserMenu component's auth dependency path.
 */
vi.mock('../../../src/stores/authStore', () => ({
  useAuthStore: (selector: any) =>
    selector({
      currentUser: mockCurrentUser,
      isAuthenticated: mockIsAuthenticated,
      logoutSuccess: mockLogoutSuccess,
    }),
}));

/**
 * Mock apiClient to prevent actual HTTP calls during component tests.
 * Provides stub get() method as required by schema members_accessed.
 */
vi.mock('../../../src/api/client', () => ({
  default: { get: vi.fn().mockResolvedValue({ data: {} }) },
  apiClient: { get: vi.fn().mockResolvedValue({ data: {} }) },
}));

/**
 * Mock UserMenu for test isolation — TopNav renders <UserMenu />.
 * Replaces the monolith's <vc:user-menu> and <vc:user-nav> ViewComponent
 * invocations from Nav.Default.cshtml lines 42-44.
 */
vi.mock('../../../src/components/layout/UserMenu', () => ({
  default: function MockUserMenu() {
    return <div data-testid="user-menu-mock">UserMenu</div>;
  },
}));

// ----------------------------------------------------------------
// Test helpers
// ----------------------------------------------------------------

/**
 * Render TopNav wrapped in MemoryRouter (required for Link / useLocation).
 * Accepts optional props and router initial entries.
 */
function renderTopNav(
  props: { brandLogo?: string; devMode?: boolean } = {},
  options: { initialEntries?: string[] } = {},
) {
  const { initialEntries = ['/'] } = options;
  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <TopNav {...props} />
    </MemoryRouter>,
  );
}

/** Factory for a minimal App object with sensible defaults. */
function createMockApp(overrides: Record<string, any> = {}): any {
  return {
    id: 'app-001',
    name: 'crm',
    label: 'Sales CRM',
    color: '#2196F3',
    weight: 1,
    access: [],
    entities: [],
    homePages: [],
    sitemap: null,
    ...overrides,
  };
}

/** Factory for a SitemapNode with defaults. */
function createMockSitemapNode(overrides: Record<string, any> = {}): any {
  return {
    id: 'node-001',
    parentId: null,
    weight: 1,
    groupName: null,
    label: 'Dashboard',
    name: 'dashboard',
    iconClass: 'fa fa-tachometer',
    url: '',
    type: 1, // SitemapNodeType.EntityList
    entityId: null,
    ...overrides,
  };
}

/** Factory for a SitemapArea with defaults. */
function createMockSitemapArea(overrides: Record<string, any> = {}): any {
  return {
    id: 'area-001',
    appId: 'app-001',
    weight: 1,
    label: 'Main',
    name: 'main',
    nodes: [],
    groups: [],
    ...overrides,
  };
}

/** Factory for a MenuItem (following Sidebar.test.tsx conventions). */
function createMockMenuItem(overrides: Record<string, any> = {}): any {
  return {
    id: `menu-${Math.random().toString(36).substring(2, 9)}`,
    parentId: null,
    content: 'Menu Item',
    class: '',
    isHtml: false,
    renderWrapper: true,
    nodes: [],
    isDropdownRight: false,
    sortOrder: 0,
    ...overrides,
  };
}

// ----------------------------------------------------------------
// Suite setup / teardown
// ----------------------------------------------------------------

beforeEach(() => {
  mockCurrentApp = null;
  mockSiteMenu = [];
  mockApplicationMenu = [];
  mockCurrentUser = {
    id: 'usr-001',
    email: 'test@webvella.com',
    firstName: 'Test',
    lastName: 'User',
    image: null,
    roles: ['administrator'],
    isAdmin: true,
  };
  mockIsAuthenticated = true;
  mockLogoutSuccess.mockClear();
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ================================================================
// Suite 1: TopNav rendering
// Validates the React replacement of NavViewComponent.cs +
// Nav.Default.cshtml core structure.
// ================================================================
describe('TopNav rendering', () => {
  it('renders the navigation bar', () => {
    renderTopNav();

    // Nav.Default.cshtml line 15: <div id="nav" ...> replaced by <nav>
    const nav = screen.getByRole('navigation', { name: /primary navigation/i });
    expect(nav).toBeDefined();
    expect(nav.id).toBe('nav');
  });

  it('renders home link pointing to root', () => {
    renderTopNav();

    // Nav.Default.cshtml lines 16-22: <a href="/"><span class="fas fa-home icon"></span></a>
    const homeLink = screen.getByRole('link', { name: /home/i });
    expect(homeLink).toBeDefined();
    expect(homeLink.getAttribute('href')).toBe('/');
  });

  it('renders brand logo with app label', () => {
    mockCurrentApp = createMockApp({ label: 'Sales CRM' });
    const { container } = renderTopNav({ brandLogo: '/assets/logo.png' });

    // Nav.Default.cshtml lines 24-37: brand section with <img> and app label
    // TopNav renders <img src={brandLogo} alt="" aria-hidden="true"> when brandLogo is provided
    const logo = container.querySelector('img') as HTMLImageElement | null;
    expect(logo).not.toBeNull();
    expect(logo!.src).toContain('/assets/logo.png');
    expect(logo!.getAttribute('aria-hidden')).toBe('true');

    expect(screen.getByText('Sales CRM')).toBeDefined();
  });

  it('does not render brand logo img when brandLogo prop is absent', () => {
    mockCurrentApp = createMockApp({ label: 'My App' });
    const { container } = renderTopNav();

    // No <img> should be rendered when brandLogo is not passed
    const logo = container.querySelector('img');
    expect(logo).toBeNull();

    // But the label should still appear
    expect(screen.getByText('My App')).toBeDefined();
  });

  it('renders default app name when no current app', () => {
    // NavViewComponent.cs line 90: DefaultAppName = "WebVella" (fallback)
    mockCurrentApp = null;
    renderTopNav();

    expect(screen.getByText('WebVella')).toBeDefined();
  });

  it('renders navigation links from application menu', () => {
    // applicationMenu is MenuItem[] from the appStore — NOT sitemap areas
    // Nav.Default.cshtml line 39: <vc:application-menu> replaced by AreaDropdown buttons
    const childNode1 = createMockMenuItem({ id: 'cn-1', content: 'Contacts', sortOrder: 1 });
    const childNode2 = createMockMenuItem({ id: 'cn-2', content: 'Accounts', sortOrder: 2 });
    const areaItem = createMockMenuItem({
      id: 'area-1',
      content: 'CRM Area',
      sortOrder: 1,
      nodes: [childNode1, childNode2],
    });
    mockApplicationMenu = [areaItem];
    mockCurrentApp = createMockApp({ name: 'crm', label: 'CRM' });

    renderTopNav();

    // Area label should be rendered as a dropdown trigger button
    expect(screen.getByText('CRM Area')).toBeDefined();
  });

  it('renders search area placeholder', () => {
    // Nav.Default.cshtml line 43: <vc:search-nav> replaced by search toggle button
    renderTopNav();

    const searchBtn = screen.getByRole('button', { name: /search/i });
    expect(searchBtn).toBeDefined();
  });

  it('renders fallback label when app label is empty', () => {
    // TopNav line 590: {appLabel || 'WebVella'} — empty label falls back to 'WebVella'
    mockCurrentApp = createMockApp({ label: '', name: 'my_app' });
    renderTopNav();

    // When label is empty string, TopNav renders the 'WebVella' fallback
    expect(screen.getByText('WebVella')).toBeDefined();
  });
});

// ================================================================
// Suite 2: TopNav dev mode banner
// Validates the React replacement of Nav.Default.cshtml lines 11-13:
//   @if (devMode) { <div style="...;background:red;">DEV MODE</div> }
// Now uses Tailwind bg-red-600 + text-white instead of inline styles.
// ================================================================
describe('TopNav dev mode banner', () => {
  it('shows DEV MODE banner when devMode is true', () => {
    renderTopNav({ devMode: true });

    const banner = screen.getByText('DEV MODE');
    expect(banner).toBeDefined();
    // Accessibility: banner has role="alert"
    expect(banner.getAttribute('role')).toBe('alert');
    // Tailwind styling replaces inline style="background:red; color:white"
    expect(banner.className).toContain('bg-red-600');
    expect(banner.className).toContain('text-white');
  });

  it('does NOT show DEV MODE banner when devMode is false', () => {
    renderTopNav({ devMode: false });

    expect(screen.queryByText('DEV MODE')).toBeNull();
  });

  it('does NOT show DEV MODE banner when devMode prop is omitted', () => {
    renderTopNav();

    expect(screen.queryByText('DEV MODE')).toBeNull();
  });
});

// ================================================================
// Suite 3: TopNav user menu dropdown
// Validates the React replacement of Nav.Default.cshtml lines 42-44:
//   <vc:user-menu> and <vc:user-nav> ViewComponent invocations.
// Also validates dropdown toggle behaviour from script.js lines 9-33.
// ================================================================
describe('TopNav user menu dropdown', () => {
  it('renders user menu component', () => {
    renderTopNav();

    // Mocked UserMenu renders a div with data-testid="user-menu-mock"
    expect(screen.getByTestId('user-menu-mock')).toBeDefined();
    expect(screen.getByText('UserMenu')).toBeDefined();
  });

  it('site menu dropdown toggles on click', () => {
    // Provide site menu items to create a dropdown trigger in TopNav
    // (script.js lines 9-33: [data-navclick-handler] toggle d-block)
    mockSiteMenu = [
      createMockMenuItem({ id: 'sm-1', content: 'Home' }),
      createMockMenuItem({ id: 'sm-2', content: 'Settings' }),
    ];
    renderTopNav();

    const siteMenuTrigger = screen.getByRole('button', { name: /site menu/i });

    // Initially closed
    expect(siteMenuTrigger.getAttribute('aria-expanded')).toBe('false');

    // Click to open
    fireEvent.click(siteMenuTrigger);
    expect(siteMenuTrigger.getAttribute('aria-expanded')).toBe('true');

    // Click again to close (toggle off)
    fireEvent.click(siteMenuTrigger);
    expect(siteMenuTrigger.getAttribute('aria-expanded')).toBe('false');
  });

  it('site menu dropdown shows menu items when open', () => {
    mockSiteMenu = [
      createMockMenuItem({ id: 'sm-1', content: 'Home Link', isHtml: false }),
    ];
    renderTopNav();

    // Initially the menu items are not visible
    expect(screen.queryByText('Home Link')).toBeNull();

    // Open the site menu
    const trigger = screen.getByRole('button', { name: /site menu/i });
    fireEvent.click(trigger);

    // Now the menu item should be visible
    expect(screen.getByText('Home Link')).toBeDefined();
  });

  it('search toggle opens and closes search input', () => {
    renderTopNav();

    // Initially, search input is not visible
    expect(screen.queryByPlaceholderText(/search/i)).toBeNull();

    // Click search button to open
    const searchBtn = screen.getByRole('button', { name: /search/i });
    fireEvent.click(searchBtn);

    // Search input should appear
    const searchInput = screen.getByPlaceholderText(/search/i);
    expect(searchInput).toBeDefined();

    // Click close button to hide
    const closeBtn = screen.getByRole('button', { name: /close search/i });
    fireEvent.click(closeBtn);

    expect(screen.queryByPlaceholderText(/search/i)).toBeNull();
  });
});

// ================================================================
// Suite 4: TopNav dropdown behaviour (replacing jQuery script.js)
// Validates the React state-based replacement of jQuery's
// data-navclick-handler event delegation from script.js lines 1-54.
// ================================================================
describe('TopNav dropdown behavior (replacing jQuery script.js)', () => {
  it('clicking outside closes open dropdown', async () => {
    // script.js lines 35-53: document.addEventListener("click", ...)
    // closed all menus when clicking outside .menu-nav-wrapper
    mockSiteMenu = [
      createMockMenuItem({ id: 'sm-1', content: 'Home' }),
    ];
    renderTopNav();

    // Open site menu dropdown
    const trigger = screen.getByRole('button', { name: /site menu/i });
    fireEvent.click(trigger);
    expect(trigger.getAttribute('aria-expanded')).toBe('true');

    // Click outside the nav — simulate clicking document body
    // TopNav registers capture-phase click listener (script.js replacement)
    fireEvent.click(document.body);

    // Dropdown should be closed
    await waitFor(() => {
      expect(trigger.getAttribute('aria-expanded')).toBe('false');
    });
  });

  it('opening one dropdown closes others (mutual exclusion)', async () => {
    // script.js lines 26-27: Before opening new menu, closes all existing:
    // $(".menu-nav-wrapper .dropdown-menu").removeClass("d-block")
    const user = userEvent.setup();

    mockSiteMenu = [
      createMockMenuItem({ id: 'sm-1', content: 'Site Home' }),
    ];
    // applicationMenu is MenuItem[] — areas are top-level items with child nodes
    const childNode = createMockMenuItem({ id: 'cn-1', content: 'Dashboard', sortOrder: 1 });
    const areaItem = createMockMenuItem({
      id: 'area-1',
      content: 'Main Area',
      sortOrder: 1,
      nodes: [childNode],
    });
    mockApplicationMenu = [areaItem];

    renderTopNav();

    // Open site menu first
    const siteMenuTrigger = screen.getByRole('button', { name: /site menu/i });
    await user.click(siteMenuTrigger);
    expect(siteMenuTrigger.getAttribute('aria-expanded')).toBe('true');

    // Now click the area dropdown button
    const areaButton = screen.getByText('Main Area');
    await user.click(areaButton);

    // Site menu should now be closed (mutual exclusion) and area should be open
    await waitFor(() => {
      expect(siteMenuTrigger.getAttribute('aria-expanded')).toBe('false');
    });
  });

  it('pressing Escape closes all dropdowns', async () => {
    mockSiteMenu = [
      createMockMenuItem({ id: 'sm-1', content: 'Home' }),
    ];
    renderTopNav();

    // Open site menu dropdown
    const trigger = screen.getByRole('button', { name: /site menu/i });
    fireEvent.click(trigger);
    expect(trigger.getAttribute('aria-expanded')).toBe('true');

    // Press Escape key
    fireEvent.keyDown(document, { key: 'Escape' });

    await waitFor(() => {
      expect(trigger.getAttribute('aria-expanded')).toBe('false');
    });
  });

  it('area dropdown reveals node links when opened', async () => {
    const user = userEvent.setup();

    // applicationMenu is MenuItem[] — areas contain child nodes as MenuItem[]
    const childNode = createMockMenuItem({ id: 'cn-1', content: 'Contacts', sortOrder: 1 });
    const areaItem = createMockMenuItem({
      id: 'area-1',
      content: 'People',
      sortOrder: 1,
      nodes: [childNode],
    });
    mockApplicationMenu = [areaItem];

    renderTopNav();

    // Node links should not be visible initially (dropdown closed)
    expect(screen.queryByText('Contacts')).toBeNull();

    // Open the area dropdown
    const areaBtn = screen.getByText('People');
    await user.click(areaBtn);

    // Node link should now be visible
    expect(screen.getByText('Contacts')).toBeDefined();
  });

  it('multiple area dropdowns exhibit mutual exclusion', async () => {
    const user = userEvent.setup();

    const childNode1 = createMockMenuItem({ id: 'cn-1', content: 'Contacts', sortOrder: 1 });
    const childNode2 = createMockMenuItem({ id: 'cn-2', content: 'Products', sortOrder: 1 });
    const area1 = createMockMenuItem({
      id: 'area-1',
      content: 'People',
      sortOrder: 1,
      nodes: [childNode1],
    });
    const area2 = createMockMenuItem({
      id: 'area-2',
      content: 'Inventory',
      sortOrder: 2,
      nodes: [childNode2],
    });
    mockApplicationMenu = [area1, area2];

    renderTopNav();

    // Open first area
    await user.click(screen.getByText('People'));
    expect(screen.getByText('Contacts')).toBeDefined();

    // Open second area — first should close
    await user.click(screen.getByText('Inventory'));
    expect(screen.getByText('Products')).toBeDefined();

    // First area's items should no longer be visible
    await waitFor(() => {
      expect(screen.queryByText('Contacts')).toBeNull();
    });
  });
});

// ================================================================
// Suite 5: TopNav responsive behaviour
// Validates Tailwind styling (not Bootstrap), and brand-logo link
// computation from computeAppDefaultLink replicating
// NavViewComponent.cs lines 38-68.
// ================================================================
describe('TopNav responsive behavior', () => {
  it('navigation bar uses proper Tailwind styling', () => {
    renderTopNav();

    const nav = screen.getByRole('navigation', { name: /primary navigation/i });

    // Must use Tailwind flex layout, NOT Bootstrap grid
    expect(nav.className).toContain('flex');
    expect(nav.className).toContain('items-center');

    // Tailwind background — dark nav bar
    expect(nav.className).toContain('bg-gray-800');

    // Tailwind padding — NOT Bootstrap pl-2 / pr-2
    expect(nav.className).toContain('px-2');
    expect(nav.className).not.toContain('pl-2');
    expect(nav.className).not.toContain('pr-2');
  });

  it('brand logo link computes correct href from homePages', () => {
    // NavViewComponent.cs lines 38-44: homePages ordered by weight → first page
    mockCurrentApp = createMockApp({
      name: 'crm',
      homePages: [
        { name: 'analytics', weight: 5 },
        { name: 'dashboard', weight: 1 },
      ],
    });
    renderTopNav();

    // computeAppDefaultLink should pick the homepage with lowest weight: dashboard
    // Expected href: /crm/a/dashboard
    const brandLink = screen.getByText('Sales CRM').closest('a');
    expect(brandLink).not.toBeNull();
    expect(brandLink!.getAttribute('href')).toBe('/crm/a/dashboard');
  });

  it('brand logo link computes correct href from sitemap areas/nodes (EntityList)', () => {
    // computeAppDefaultLink: sitemap areas → first node by SitemapNodeType
    const node = createMockSitemapNode({
      name: 'contacts',
      type: 1, // EntityList
      weight: 1,
    });
    const area = createMockSitemapArea({
      name: 'main',
      weight: 1,
      nodes: [node],
    });
    mockCurrentApp = createMockApp({
      name: 'crm',
      homePages: [], // No homePages — falls through to sitemap
      sitemap: { areas: [area] },
    });
    renderTopNav();

    // computeAppDefaultLink EntityList (type 1): /${app.name}/${area.name}/${node.name}/l/
    const brandLink = screen.getByText('Sales CRM').closest('a');
    expect(brandLink).not.toBeNull();
    expect(brandLink!.getAttribute('href')).toBe('/crm/main/contacts/l/');
  });

  it('brand logo link computes correct href for ApplicationPage node type', () => {
    // computeAppDefaultLink: SitemapNodeType.ApplicationPage (type 2) → /a/ suffix
    const node = createMockSitemapNode({
      name: 'settings',
      type: 2, // ApplicationPage
      weight: 1,
    });
    const area = createMockSitemapArea({
      name: 'admin',
      weight: 1,
      nodes: [node],
    });
    mockCurrentApp = createMockApp({
      name: 'sdk',
      homePages: [],
      sitemap: { areas: [area] },
    });
    renderTopNav();

    // computeAppDefaultLink ApplicationPage (type 2): /${app.name}/${area.name}/${node.name}/a/
    const brandLink = screen.getByText('Sales CRM').closest('a');
    expect(brandLink).not.toBeNull();
    expect(brandLink!.getAttribute('href')).toBe('/sdk/admin/settings/a/');
  });

  it('brand logo link falls back to app root when no homePages or sitemap', () => {
    mockCurrentApp = createMockApp({
      name: 'crm',
      homePages: [],
      sitemap: null,
    });
    renderTopNav();

    // computeAppDefaultLink with no homePages and no sitemap → falls through to '/'
    const brandLink = screen.getByText('Sales CRM').closest('a');
    expect(brandLink).not.toBeNull();
    expect(brandLink!.getAttribute('href')).toBe('/');
  });

  it('brand logo link is root when no current app', () => {
    mockCurrentApp = null;
    renderTopNav();

    const brandLink = screen.getByText('WebVella').closest('a');
    expect(brandLink).not.toBeNull();
    expect(brandLink!.getAttribute('href')).toBe('/');
  });

  it('site menu trigger is hidden when siteMenu array is empty', () => {
    mockSiteMenu = [];
    renderTopNav();

    expect(screen.queryByRole('button', { name: /site menu/i })).toBeNull();
  });

  it('application menu areas are hidden when applicationMenu is empty', () => {
    // Areas come from useAppStore applicationMenu, not currentApp.sitemap
    mockApplicationMenu = [];
    mockCurrentApp = createMockApp();
    renderTopNav();

    // The application navigation menubar should exist but have no area items
    const menubar = screen.getByRole('menubar', { name: /application navigation/i });
    // No area dropdown buttons should exist within the menubar
    const areaButtons = menubar.querySelectorAll('button');
    expect(areaButtons.length).toBe(0);
  });
});
