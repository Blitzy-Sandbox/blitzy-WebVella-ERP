/**
 * Vitest Component Tests for `<Sidebar />`
 *
 * Validates the React Sidebar component that replaces the monolith's
 * SidebarMenu ViewComponent (`SidebarMenu.cs` + `SidebarMenu.cshtml`).
 *
 * Test coverage includes:
 *  - Hierarchical menu item rendering from Zustand appStore sidebarMenu
 *    (replaces @foreach iteration in SidebarMenu.cshtml lines 9-13)
 *  - Recursive child menu item rendering (replaces NavMenu partial)
 *  - Collapse/expand toggle via onToggle callback
 *    (replaces .sidebar-switch button from SidebarMenu.cshtml lines 16-18)
 *  - Active link highlighting via React Router NavLink
 *    (replaces server-side isActive flag on MenuItem model)
 *  - Tailwind CSS styling classes (NOT Bootstrap)
 *  - Responsive behaviour and sidebar width transitions
 *  - Accessibility: role="navigation", aria-label, aria-expanded
 *
 * @see apps/frontend/src/components/layout/Sidebar.tsx
 * @see WebVella.Erp.Web/Components/SidebarMenu/SidebarMenu.cs
 * @see WebVella.Erp.Web/Components/SidebarMenu/SidebarMenu.cshtml
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ReactElement } from 'react';
import Sidebar from '../../../src/components/layout/Sidebar';
import type { MenuItem } from '../../../src/types/app';

// ---------------------------------------------------------------------------
// Mock: useAppStore — provides controlled sidebarMenu data
// The Sidebar component imports useAppStore to get sidebarMenu items.
// We mock the store to isolate test behaviour and control menu data,
// replacing the monolith's server-side pageModel.SidebarMenu from
// BaseErpPageModel.
// ---------------------------------------------------------------------------

/**
 * Holds the mock sidebarMenu value that the mocked useAppStore returns.
 * Updated in beforeEach / individual tests to control Sidebar input data.
 */
let mockSidebarMenuItems: MenuItem[] = [];

vi.mock('../../../src/stores/appStore', () => ({
  useAppStore: (selector: (state: { sidebarMenu: MenuItem[] }) => unknown) =>
    selector({ sidebarMenu: mockSidebarMenuItems }),
}));

// ---------------------------------------------------------------------------
// Test data — mirrors monolith's List<MenuItem> from BaseErpPageModel
// ---------------------------------------------------------------------------

/**
 * Creates a default MenuItem matching the MenuItem interface in types/app.ts.
 * Fields mirror the C# MenuItem.cs properties.
 */
function createMenuItem(overrides: Partial<MenuItem> & { id: string }): MenuItem {
  return {
    id: overrides.id,
    parentId: overrides.parentId ?? null,
    content: overrides.content ?? '',
    class: overrides.class ?? '',
    isHtml: overrides.isHtml ?? false,
    renderWrapper: overrides.renderWrapper ?? true,
    nodes: overrides.nodes ?? [],
    isDropdownRight: overrides.isDropdownRight ?? false,
    sortOrder: overrides.sortOrder ?? 10,
  };
}

/**
 * Creates mock menu items resembling the monolith's SidebarMenu structure.
 * - Dashboard: top-level item with icon, no children
 * - Contacts: top-level item with two child nodes (All Contacts, Accounts)
 * - Projects: top-level item with icon, no children
 *
 * Uses isHtml=true with HTML content containing <a> tags, matching how the
 * monolith's BaseErpPageModel.Init() builds MenuItem.content as HTML strings.
 */
function createMockMenuItems(): MenuItem[] {
  return [
    createMenuItem({
      id: '1',
      content: '<a href="/dashboard"><span class="icon fa fa-home"></span> Dashboard</a>',
      class: '',
      isHtml: true,
      nodes: [],
      sortOrder: 1,
    }),
    createMenuItem({
      id: '2',
      content: '<a href="/crm/contacts"><span class="icon fa fa-users"></span> Contacts</a>',
      class: '',
      isHtml: true,
      nodes: [
        createMenuItem({
          id: '2-1',
          parentId: '2',
          content: '<a href="/crm/contacts/all"><span class="icon"></span> All Contacts</a>',
          class: '',
          isHtml: true,
          nodes: [],
          sortOrder: 1,
        }),
        createMenuItem({
          id: '2-2',
          parentId: '2',
          content: '<a href="/crm/contacts/accounts"><span class="icon"></span> Accounts</a>',
          class: '',
          isHtml: true,
          nodes: [],
          sortOrder: 2,
        }),
      ],
      sortOrder: 2,
    }),
    createMenuItem({
      id: '3',
      content: '<a href="/projects"><span class="icon fa fa-tasks"></span> Projects</a>',
      class: '',
      isHtml: true,
      nodes: [],
      sortOrder: 3,
    }),
  ];
}

// ---------------------------------------------------------------------------
// Test helper: renderSidebar
// ---------------------------------------------------------------------------

interface RenderSidebarOptions {
  /** Whether the sidebar is in collapsed mode. Default: false. */
  collapsed?: boolean;
  /** Callback for the toggle button. Default: vi.fn(). */
  onToggle?: () => void;
  /** Initial URL entries for the MemoryRouter. Default: ['/'] */
  initialEntries?: string[];
  /** Custom menu items to inject via the mock store. */
  menuItems?: MenuItem[];
}

/**
 * Renders the Sidebar component within a MemoryRouter context.
 * NavLink requires router context, so every Sidebar render must be wrapped.
 *
 * Optionally configures the mocked appStore sidebarMenu before rendering,
 * and wraps the Sidebar in Routes/Route for NavLink active state testing.
 */
function renderSidebar(options: RenderSidebarOptions = {}) {
  const {
    collapsed = false,
    onToggle = vi.fn(),
    initialEntries = ['/'],
    menuItems,
  } = options;

  // Update the mock store data before render
  if (menuItems !== undefined) {
    mockSidebarMenuItems = menuItems;
  }

  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <Routes>
        <Route
          path="*"
          element={<Sidebar collapsed={collapsed} onToggle={onToggle} />}
        />
      </Routes>
    </MemoryRouter>,
  );
}

// ---------------------------------------------------------------------------
// Lifecycle hooks
// ---------------------------------------------------------------------------

beforeEach(() => {
  // Reset mock store data to default menu items before each test
  mockSidebarMenuItems = createMockMenuItems();
});

afterEach(() => {
  // Reset mocks and clear any leftover state
  vi.restoreAllMocks();
  mockSidebarMenuItems = [];
});

// ===========================================================================
// Suite 1: Sidebar rendering
// ===========================================================================

describe('Sidebar rendering', () => {
  it('renders sidebar container with navigation role', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    expect(nav).toBeDefined();
    expect(nav.getAttribute('aria-label')).toBe('Sidebar navigation');
  });

  it('renders menu items from data', () => {
    // Context: In monolith SidebarMenu.cshtml lines 9-13, items are iterated
    // via @foreach (var menuItem in sidebarMenu) and rendered via
    // <partial name="NavMenu" for="@menuItem"/>
    renderSidebar();

    expect(screen.getByText('Dashboard')).toBeDefined();
    expect(screen.getByText('Contacts')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
  });

  it('renders nested child menu items when parent is expanded', () => {
    // Context: Monolith uses recursive NavMenu partial; React uses
    // recursive SidebarMenuItem sub-component.
    // Child items are only visible when parent is expanded.
    renderSidebar();

    // The "Contacts" item has child nodes. We need to expand it first.
    // Find the expand button for the Contacts menu item.
    const contactsLink = screen.getByText('Contacts');
    expect(contactsLink).toBeDefined();

    // Look for the expand/collapse button near the Contacts item
    const expandButtons = screen.getAllByRole('button', { name: /expand|collapse/i });
    // Click the expand button for Contacts (the one associated with Contacts item)
    const contactsExpandBtn = expandButtons.find((btn) => {
      const label = btn.getAttribute('aria-label') || '';
      return label.toLowerCase().includes('contacts');
    });

    if (contactsExpandBtn) {
      fireEvent.click(contactsExpandBtn);
    }

    // After expansion, child items should appear
    expect(screen.getByText('All Contacts')).toBeDefined();
    expect(screen.getByText('Accounts')).toBeDefined();
  });

  it('renders menu item icons from iconClass in HTML content', () => {
    renderSidebar();

    // The Sidebar component extracts icon classes from HTML content.
    // For items with isHtml=true, icons are rendered as <i> elements.
    const iconElements = document.querySelectorAll('i[aria-hidden="true"]');
    expect(iconElements.length).toBeGreaterThan(0);

    // Verify that icons have the extracted CSS classes
    const iconClassesFound = Array.from(iconElements).some(
      (el) => el.className.includes('fa') || el.className.includes('fa-home'),
    );
    expect(iconClassesFound).toBe(true);
  });

  it('renders empty state when no menu items are provided', () => {
    renderSidebar({ menuItems: [] });

    expect(screen.getByText('No navigation items')).toBeDefined();
  });

  it('does not render empty state text when collapsed with no items', () => {
    renderSidebar({ menuItems: [], collapsed: true });

    expect(screen.queryByText('No navigation items')).toBeNull();
  });

  it('sorts menu items by sortOrder', () => {
    const unorderedItems: MenuItem[] = [
      createMenuItem({
        id: 'z',
        content: '<a href="/z"><span class="icon fa fa-file"></span> Zeta</a>',
        isHtml: true,
        sortOrder: 99,
      }),
      createMenuItem({
        id: 'a',
        content: '<a href="/a"><span class="icon fa fa-star"></span> Alpha</a>',
        isHtml: true,
        sortOrder: 1,
      }),
    ];

    renderSidebar({ menuItems: unorderedItems });

    const items = screen.getAllByRole('none');
    const textContents = items.map((item) => item.textContent || '');
    const alphaIndex = textContents.findIndex((t) => t.includes('Alpha'));
    const zetaIndex = textContents.findIndex((t) => t.includes('Zeta'));

    expect(alphaIndex).toBeLessThan(zetaIndex);
  });
});

// ===========================================================================
// Suite 2: Sidebar collapse/expand
// ===========================================================================

describe('Sidebar collapse/expand', () => {
  it('renders expanded sidebar by default (when collapsed=false)', () => {
    renderSidebar({ collapsed: false });

    const nav = screen.getByRole('navigation');
    expect(nav.className).toContain('w-64');

    // Menu item labels should be visible in expanded mode
    expect(screen.getByText('Dashboard')).toBeDefined();
    expect(screen.getByText('Contacts')).toBeDefined();
    expect(screen.getByText('Projects')).toBeDefined();
  });

  it('renders collapsed sidebar when collapsed=true', () => {
    // Context: Monolith's .sidebar-switch button (SidebarMenu.cshtml line 16-18)
    // triggers sidebar resize; state was persisted via UserPreferencies.SetSidebarSize.
    // React replaces with controlled props + localStorage persistence in AppShell.
    renderSidebar({ collapsed: true });

    const nav = screen.getByRole('navigation');
    expect(nav.className).toContain('w-16');

    // In collapsed mode, text labels (inside <span class="truncate">) should
    // not be rendered. Icons should still be present.
    // The text "Dashboard" etc. should NOT appear as visible spans
    // (they may appear only as title attributes on the link elements).
    const iconElements = document.querySelectorAll('i[aria-hidden="true"]');
    expect(iconElements.length).toBeGreaterThan(0);
  });

  it('toggle button triggers onToggle callback', () => {
    // Context: Monolith's toggle was <button class="btn btn-sm btn-dark sidebar-switch">
    // with fa-angle-double-right icon
    const onToggle = vi.fn();
    renderSidebar({ onToggle });

    // Find the toggle button by its aria-label
    const toggleButton = screen.getByRole('button', {
      name: /collapse sidebar|expand sidebar/i,
    });
    expect(toggleButton).toBeDefined();

    fireEvent.click(toggleButton);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it('toggle button triggers onToggle callback with userEvent', async () => {
    const user = userEvent.setup();
    const onToggle = vi.fn();
    renderSidebar({ onToggle });

    const toggleButton = screen.getByRole('button', {
      name: /collapse sidebar|expand sidebar/i,
    });

    await user.click(toggleButton);
    expect(onToggle).toHaveBeenCalledTimes(1);
  });

  it('toggle button shows "Collapse sidebar" label when expanded', () => {
    // Context: Monolith always showed fa-angle-double-right;
    // React toggles icon direction based on collapsed state.
    renderSidebar({ collapsed: false });

    const toggleButton = screen.getByRole('button', { name: 'Collapse sidebar' });
    expect(toggleButton).toBeDefined();
    expect(toggleButton.getAttribute('title')).toBe('Collapse sidebar');
  });

  it('toggle button shows "Expand sidebar" label when collapsed', () => {
    renderSidebar({ collapsed: true });

    const toggleButton = screen.getByRole('button', { name: 'Expand sidebar' });
    expect(toggleButton).toBeDefined();
    expect(toggleButton.getAttribute('title')).toBe('Expand sidebar');
  });

  it('toggle button icon rotates when expanded (rotate-180 class)', () => {
    renderSidebar({ collapsed: false });

    const toggleButton = screen.getByRole('button', { name: 'Collapse sidebar' });
    const svg = toggleButton.querySelector('svg');
    expect(svg).not.toBeNull();
    expect(svg!.className.baseVal || svg!.getAttribute('class') || '').toContain(
      'rotate-180',
    );
  });

  it('toggle button icon does NOT rotate when collapsed', () => {
    renderSidebar({ collapsed: true });

    const toggleButton = screen.getByRole('button', { name: 'Expand sidebar' });
    const svg = toggleButton.querySelector('svg');
    expect(svg).not.toBeNull();
    // When collapsed, the SVG should NOT have rotate-180
    const svgClasses = svg!.className.baseVal || svg!.getAttribute('class') || '';
    expect(svgClasses).not.toContain('rotate-180');
  });

  it('applies smooth transition classes on sidebar container', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    expect(nav.className).toContain('transition-all');
    expect(nav.className).toContain('duration-300');
  });
});

// ===========================================================================
// Suite 3: Sidebar active link highlighting
// ===========================================================================

describe('Sidebar active link highlighting', () => {
  it('highlights active menu item via React Router NavLink', () => {
    // Context: Monolith used server-side isActive flag on MenuItem;
    // React uses NavLink's className callback for active detection.
    renderSidebar({ initialEntries: ['/crm/contacts'] });

    // The "Contacts" item links to /crm/contacts. When the router location
    // matches, NavLink applies the active styling classes.
    const contactsLink = screen.getByText('Contacts').closest('a');
    expect(contactsLink).not.toBeNull();

    // NavLink should apply active state classes
    const classes = contactsLink!.className;
    expect(classes).toContain('bg-gray-900');
    expect(classes).toContain('text-white');
    expect(classes).toContain('font-medium');
  });

  it('does NOT highlight non-active menu items', () => {
    renderSidebar({ initialEntries: ['/crm/contacts'] });

    // Dashboard links to /dashboard, which does not match /crm/contacts
    const dashboardLink = screen.getByText('Dashboard').closest('a');
    expect(dashboardLink).not.toBeNull();

    const classes = dashboardLink!.className;
    // Non-active items should have the inactive styling
    expect(classes).toContain('text-gray-300');
    expect(classes).not.toContain('bg-gray-900');
    expect(classes).not.toContain('font-medium');
  });

  it('does NOT highlight any items for non-matching routes', () => {
    renderSidebar({ initialEntries: ['/settings'] });

    // None of our mock items match /settings, so no item should have active styling
    const allLinks = document.querySelectorAll('a');
    allLinks.forEach((link) => {
      const classes = link.className;
      // Active class should not be present on any link for this route
      if (
        link.textContent?.includes('Dashboard') ||
        link.textContent?.includes('Contacts') ||
        link.textContent?.includes('Projects')
      ) {
        expect(classes).not.toContain('font-medium');
      }
    });
  });

  it('highlights active menu item via prefix matching for nested routes', () => {
    // /crm/contacts/all should also make the parent "Contacts" link active
    // because NavLink path matching uses startsWith for non-root paths
    renderSidebar({ initialEntries: ['/crm/contacts/all'] });

    const contactsLink = screen.getByText('Contacts').closest('a');
    expect(contactsLink).not.toBeNull();

    const classes = contactsLink!.className;
    expect(classes).toContain('bg-gray-900');
    expect(classes).toContain('text-white');
  });
});

// ===========================================================================
// Suite 4: Sidebar footer
// ===========================================================================

describe('Sidebar footer', () => {
  it('renders sidebar footer with toggle button', () => {
    // Context: Monolith's SidebarMenu.cshtml line 15 had .sidebar-footer div
    // containing the .sidebar-switch button
    renderSidebar();

    // The footer is a div with border-t class containing the toggle button
    const toggleButton = screen.getByRole('button', {
      name: /collapse sidebar|expand sidebar/i,
    });
    expect(toggleButton).toBeDefined();

    // Verify the button is within a footer-like container with border-t
    const footerContainer = toggleButton.closest('div');
    expect(footerContainer).not.toBeNull();
    expect(footerContainer!.className).toContain('border-t');
    expect(footerContainer!.className).toContain('border-gray-700');
  });

  it('toggle button contains an SVG icon (double-chevron)', () => {
    // Context: Monolith used <i class="fas fa-fw fa-angle-double-right icon">
    // React replaces with an SVG double-chevron
    renderSidebar();

    const toggleButton = screen.getByRole('button', {
      name: /collapse sidebar|expand sidebar/i,
    });
    const svg = toggleButton.querySelector('svg');
    expect(svg).not.toBeNull();

    // SVG should contain polyline elements for the double-chevron
    const polylines = svg!.querySelectorAll('polyline');
    expect(polylines.length).toBe(2); // Two chevron prongs
  });

  it('toggle button has proper focus styling classes', () => {
    renderSidebar();

    const toggleButton = screen.getByRole('button', {
      name: /collapse sidebar|expand sidebar/i,
    });
    expect(toggleButton.className).toContain('focus-visible:outline-none');
    expect(toggleButton.className).toContain('focus-visible:ring-2');
  });
});

// ===========================================================================
// Suite 5: Sidebar responsive behavior and Tailwind styling
// ===========================================================================

describe('Sidebar responsive behavior', () => {
  it('sidebar container uses correct Tailwind dark background styling', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    expect(nav.className).toContain('bg-gray-800');
    expect(nav.className).toContain('text-white');
  });

  it('sidebar body section has overflow-y for scrolling', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    // The sidebar body is the first child div with overflow-y-auto
    const bodyDiv = nav.querySelector('.overflow-y-auto');
    expect(bodyDiv).not.toBeNull();
  });

  it('sidebar uses flexbox column layout', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    expect(nav.className).toContain('flex');
    expect(nav.className).toContain('flex-col');
  });

  it('sidebar has full height class', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    expect(nav.className).toContain('h-full');
  });

  it('collapsed mode hides text labels but keeps icons', () => {
    renderSidebar({ collapsed: true });

    // In collapsed mode, the Sidebar component conditionally renders text
    // labels: {!collapsed && <span className="truncate ...">label</span>}
    // So text spans with labels should not appear.
    // However, icons (<i> elements with aria-hidden) should still be present.
    const iconElements = document.querySelectorAll('i[aria-hidden="true"]');
    expect(iconElements.length).toBeGreaterThan(0);

    // Text labels should be rendered as title attributes on links (for tooltips)
    // but not as visible span elements.
    const truncateSpans = document.querySelectorAll('span.truncate');
    expect(truncateSpans.length).toBe(0);
  });

  it('expanded mode shows text labels alongside icons', () => {
    renderSidebar({ collapsed: false });

    // In expanded mode, text labels are visible as span.truncate elements
    const truncateSpans = document.querySelectorAll('span.truncate');
    expect(truncateSpans.length).toBeGreaterThan(0);

    // Icons should also be present
    const iconElements = document.querySelectorAll('i[aria-hidden="true"]');
    expect(iconElements.length).toBeGreaterThan(0);
  });

  it('collapsed mode adds title attributes for tooltip display', () => {
    renderSidebar({ collapsed: true });

    // When collapsed, links should have title attributes with the label text
    // for accessibility/tooltip display
    const links = document.querySelectorAll('a[title]');
    expect(links.length).toBeGreaterThan(0);

    const titles = Array.from(links).map((link) => link.getAttribute('title'));
    expect(titles.some((t) => t && t.includes('Dashboard'))).toBe(true);
  });

  it('expanded mode does NOT add title attributes on links', () => {
    renderSidebar({ collapsed: false });

    // When expanded, labels are visible so title tooltips are not needed
    const navLinks = document.querySelectorAll('a[title]');
    // None of the NavLink anchor elements should have title when expanded
    expect(navLinks.length).toBe(0);
  });
});

// ===========================================================================
// Suite 6: Sidebar with plain text menu items (non-HTML)
// ===========================================================================

describe('Sidebar with plain text menu items', () => {
  it('renders plain text items (isHtml=false) correctly', () => {
    const plainItems: MenuItem[] = [
      createMenuItem({
        id: 'p1',
        content: 'Settings',
        isHtml: false,
        sortOrder: 1,
      }),
      createMenuItem({
        id: 'p2',
        content: 'Help',
        isHtml: false,
        sortOrder: 2,
      }),
    ];

    renderSidebar({ menuItems: plainItems });

    expect(screen.getByText('Settings')).toBeDefined();
    expect(screen.getByText('Help')).toBeDefined();
  });

  it('renders plain text items with children as expandable buttons', () => {
    const parentItem: MenuItem[] = [
      createMenuItem({
        id: 'parent',
        content: 'Administration',
        isHtml: false,
        sortOrder: 1,
        nodes: [
          createMenuItem({
            id: 'child-1',
            content: 'Users',
            isHtml: false,
            parentId: 'parent',
            sortOrder: 1,
          }),
          createMenuItem({
            id: 'child-2',
            content: 'Roles',
            isHtml: false,
            parentId: 'parent',
            sortOrder: 2,
          }),
        ],
      }),
    ];

    renderSidebar({ menuItems: parentItem });

    // Parent should be a button since it has children and is plain text
    const adminButton = screen.getByRole('button', { name: /administration/i });
    expect(adminButton).toBeDefined();
    expect(adminButton.getAttribute('aria-expanded')).toBe('false');

    // Click to expand
    fireEvent.click(adminButton);

    // After expanding, child items should be visible
    expect(screen.getByText('Users')).toBeDefined();
    expect(screen.getByText('Roles')).toBeDefined();

    // aria-expanded should now be true
    expect(adminButton.getAttribute('aria-expanded')).toBe('true');
  });

  it('collapses expanded submenu when parent button is clicked again', () => {
    const parentItem: MenuItem[] = [
      createMenuItem({
        id: 'parent',
        content: 'Administration',
        isHtml: false,
        sortOrder: 1,
        nodes: [
          createMenuItem({
            id: 'child-1',
            content: 'Users',
            isHtml: false,
            parentId: 'parent',
            sortOrder: 1,
          }),
        ],
      }),
    ];

    renderSidebar({ menuItems: parentItem });

    const adminButton = screen.getByRole('button', { name: /administration/i });

    // Expand
    fireEvent.click(adminButton);
    expect(screen.getByText('Users')).toBeDefined();

    // Collapse
    fireEvent.click(adminButton);
    expect(screen.queryByText('Users')).toBeNull();
  });
});

// ===========================================================================
// Suite 7: Sidebar menu item with active class from server
// ===========================================================================

describe('Sidebar menu item with server-side active class', () => {
  it('applies active styling when MenuItem.class includes "active"', () => {
    const activeItems: MenuItem[] = [
      createMenuItem({
        id: 'act-1',
        content: '<a href="/reports"><span class="icon fa fa-chart-bar"></span> Reports</a>',
        class: 'active',
        isHtml: true,
        sortOrder: 1,
      }),
    ];

    renderSidebar({ menuItems: activeItems, initialEntries: ['/other'] });

    // Even though the route doesn't match, the "active" class on the MenuItem
    // should trigger active styling
    const reportsLink = screen.getByText('Reports').closest('a');
    expect(reportsLink).not.toBeNull();
    expect(reportsLink!.className).toContain('bg-gray-900');
    expect(reportsLink!.className).toContain('font-medium');
  });
});

// ===========================================================================
// Suite 8: Sidebar accessibility
// ===========================================================================

describe('Sidebar accessibility', () => {
  it('has correct ARIA landmarks and labels', () => {
    renderSidebar();

    const nav = screen.getByRole('navigation');
    expect(nav.getAttribute('aria-label')).toBe('Sidebar navigation');
  });

  it('renders menu list with menubar role', () => {
    renderSidebar();

    const menubar = screen.getByRole('menubar');
    expect(menubar).toBeDefined();
    expect(menubar.getAttribute('aria-label')).toBe('Navigation menu');
  });

  it('renders each menu item in a list item with role="none"', () => {
    renderSidebar();

    const listItems = screen.getAllByRole('none');
    expect(listItems.length).toBeGreaterThan(0);
  });

  it('toggle button has correct aria-label for expanded state', () => {
    renderSidebar({ collapsed: false });

    const toggleButton = screen.getByRole('button', { name: 'Collapse sidebar' });
    expect(toggleButton.getAttribute('aria-label')).toBe('Collapse sidebar');
  });

  it('toggle button has correct aria-label for collapsed state', () => {
    renderSidebar({ collapsed: true });

    const toggleButton = screen.getByRole('button', { name: 'Expand sidebar' });
    expect(toggleButton.getAttribute('aria-label')).toBe('Expand sidebar');
  });
});
