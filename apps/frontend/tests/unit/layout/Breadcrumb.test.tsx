/**
 * Vitest Component Tests for `<Breadcrumb />`
 *
 * Validates the React Breadcrumb component that generates a navigation
 * breadcrumb trail from React Router route parameters, replacing the
 * monolith's implicit breadcrumb context derived from `ErpRequestContext`
 * properties (`CurrentApp`, `SitemapArea`, `SitemapNode`) that were set
 * in `NavViewComponent.cs` (lines 25-28) and consumed by
 * `ApplicationNode.cshtml.cs` / `ApplicationHome.cshtml.cs` page models.
 *
 * Test coverage includes:
 *  - Route-based breadcrumb generation from useParams / useLocation
 *  - Explicit `items` prop override
 *  - Home link rendering and `showHome` toggle
 *  - Slug-to-label formatting (underscores / hyphens → title case)
 *  - Separator (chevron-right) rendering between items
 *  - Tailwind CSS class application (not Bootstrap)
 *  - Semantic HTML accessibility (nav, aria-label, ol, li)
 *  - All monolith URL patterns: /l/, /c/, /r/:id, /m/:id, /a/:pageName
 *
 * @see apps/frontend/src/components/layout/Breadcrumb.tsx
 * @see WebVella.Erp.Web/Components/Nav/NavViewComponent.cs
 * @see WebVella.Erp.Web/Pages/ApplicationNode.cshtml.cs
 * @see WebVella.Erp.Web/Pages/ApplicationHome.cshtml.cs
 */

import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ReactElement } from 'react';
import Breadcrumb from '../../../src/components/layout/Breadcrumb';

// ---------------------------------------------------------------------------
// Test helper: renderWithRouter
// ---------------------------------------------------------------------------

/**
 * Renders a React element within a MemoryRouter context that provides the
 * route parameters the Breadcrumb component extracts via `useParams()` and
 * `useLocation()`.
 *
 * @param ui             - The React element to render (typically `<Breadcrumb />`)
 * @param initialEntries - Array of URL strings for the MemoryRouter history;
 *                         the last entry becomes the current location.
 * @param routePath      - A React Router path pattern with named segments
 *                         (e.g. `/:appName/:areaName/:nodeName/*`) so that
 *                         `useParams()` inside the component receives the
 *                         expected parameter values.
 *
 * @returns The render result from `@testing-library/react`, including the
 *          `container` DOM element for direct querySelector access.
 */
function renderWithRouter(
  ui: ReactElement,
  {
    initialEntries = ['/'],
    routePath = '/*',
  }: {
    initialEntries?: string[];
    routePath?: string;
  } = {},
) {
  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <Routes>
        <Route path={routePath} element={ui} />
      </Routes>
    </MemoryRouter>,
  );
}

// ---------------------------------------------------------------------------
// Test suite
// ---------------------------------------------------------------------------

describe('Breadcrumb', () => {
  // -----------------------------------------------------------------------
  // 1. Home breadcrumb link
  // -----------------------------------------------------------------------

  it('renders home breadcrumb link', () => {
    // Render at a deeper route so Home is NOT the last (active) item and
    // therefore IS rendered as a clickable <Link> with href="/".
    const { container } = renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Home text is present in the document
    const homeElement = screen.getByText('Home');
    expect(homeElement).toBeTruthy();

    // Home is rendered as a link (<a>) with href "/"
    const homeLink = homeElement.closest('a');
    expect(homeLink).not.toBeNull();
    expect(homeLink!.getAttribute('href')).toBe('/');

    // Proper <nav> wrapper with aria-label for accessibility
    const nav = container.querySelector('nav');
    expect(nav).not.toBeNull();
    expect(nav!.getAttribute('aria-label')).toBe('Breadcrumb');
  });

  it('renders home as current page at root path', () => {
    // At the root path with no route params, Home is the only breadcrumb
    // item and is therefore marked as the active (current) page — rendered
    // as a <span> rather than a <Link>.
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/'],
      routePath: '/*',
    });

    const homeElement = screen.getByText('Home');
    expect(homeElement).toBeTruthy();
    expect(homeElement.tagName.toLowerCase()).toBe('span');
    expect(homeElement.getAttribute('aria-current')).toBe('page');
    // No <a> wrapper for the active item
    expect(homeElement.closest('a')).toBeNull();
  });

  // -----------------------------------------------------------------------
  // 2. Route-based breadcrumb trail from params
  // -----------------------------------------------------------------------

  it('renders breadcrumb trail from route params (appName/areaName/nodeName)', () => {
    // Simulates the monolith URL /{AppName}/{AreaName}/{NodeName}/l/
    // which corresponds to an Entity List page.
    // In the monolith, NavViewComponent.cs (lines 25-28) set:
    //   ViewBag.CurrentApp  = ErpRequestContext.App
    //   ViewBag.CurrentArea = ErpRequestContext.SitemapArea
    //   ViewBag.CurrentNode = ErpRequestContext.SitemapNode
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Home breadcrumb is rendered with href "/"
    const homeElement = screen.getByText('Home');
    expect(homeElement).toBeTruthy();
    const homeLink = homeElement.closest('a');
    expect(homeLink).not.toBeNull();
    expect(homeLink!.getAttribute('href')).toBe('/');

    // App-level breadcrumb: slug "my_app" → display "My App"
    const appElement = screen.getByText('My App');
    expect(appElement).toBeTruthy();

    // Area-level breadcrumb: slug "contacts" → display "Contacts"
    const areaElement = screen.getByText('Contacts');
    expect(areaElement).toBeTruthy();

    // Node-level breadcrumb: slug "all_contacts" → display "All Contacts"
    const nodeElement = screen.getByText('All Contacts');
    expect(nodeElement).toBeTruthy();

    // Page context label "List" is auto-resolved from the /l/ URL tail
    const listElement = screen.getByText('List');
    expect(listElement).toBeTruthy();

    // The last breadcrumb item ("List") does NOT have an <a> link —
    // it is the current/active page rendered as a <span>.
    expect(listElement.tagName.toLowerCase()).toBe('span');
    expect(listElement.closest('a')).toBeNull();
    expect(listElement.getAttribute('aria-current')).toBe('page');
  });

  // -----------------------------------------------------------------------
  // 3. Navigation links point to correct routes
  // -----------------------------------------------------------------------

  it('navigation links point to correct routes', () => {
    // Uses different slugs to ensure each href is independently correct.
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/sales/pipeline/leads/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Home link → /
    const homeLink = screen.getByText('Home').closest('a');
    expect(homeLink).not.toBeNull();
    expect(homeLink!.getAttribute('href')).toBe('/');

    // App breadcrumb link → /{appName}/a/
    const appLink = screen.getByText('Sales').closest('a');
    expect(appLink).not.toBeNull();
    expect(appLink!.getAttribute('href')).toBe('/sales/a/');

    // Area breadcrumb link → /{appName}/{areaName}/{nodeName}/l/
    // (when nodeName exists the area links to the node's list view,
    //  matching the monolith's NavViewComponent.cs line 54-57 convention)
    const areaLink = screen.getByText('Pipeline').closest('a');
    expect(areaLink).not.toBeNull();
    expect(areaLink!.getAttribute('href')).toBe('/sales/pipeline/leads/l/');

    // Node breadcrumb link → /{appName}/{areaName}/{nodeName}/l/
    const nodeLink = screen.getByText('Leads').closest('a');
    expect(nodeLink).not.toBeNull();
    expect(nodeLink!.getAttribute('href')).toBe('/sales/pipeline/leads/l/');

    // Page context "List" is the last (active) item — NO link
    const listItem = screen.getByText('List');
    expect(listItem.closest('a')).toBeNull();
  });

  // -----------------------------------------------------------------------
  // 4. Slug-to-label formatting
  // -----------------------------------------------------------------------

  it('formats route param slugs to display labels', () => {
    // Underscores → spaces → title case
    // Matches monolith identifiers: currentApp.Name, area.Name, currentNode.Name
    // which use underscore_separated naming (e.g. "account_management").
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/account_management/task_area/all-tasks/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // "account_management" → "Account Management"
    expect(screen.getByText('Account Management')).toBeTruthy();

    // "task_area" → "Task Area"
    expect(screen.getByText('Task Area')).toBeTruthy();

    // "all-tasks" → "All Tasks" (hyphens also replaced)
    expect(screen.getByText('All Tasks')).toBeTruthy();
  });

  it('formats single-word slugs correctly', () => {
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/crm/contacts/leads/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // "crm" → "Crm" (single word, capitalised first letter)
    expect(screen.getByText('Crm')).toBeTruthy();

    // "contacts" → "Contacts"
    expect(screen.getByText('Contacts')).toBeTruthy();

    // "leads" → "Leads"
    expect(screen.getByText('Leads')).toBeTruthy();
  });

  // -----------------------------------------------------------------------
  // 5. Explicit items prop override
  // -----------------------------------------------------------------------

  it('supports explicit items prop override', () => {
    const customItems = [
      { label: 'Custom', href: '/custom' },
      { label: 'Page', isActive: true },
    ];

    renderWithRouter(<Breadcrumb items={customItems} />, {
      initialEntries: ['/'],
      routePath: '/*',
    });

    // "Custom" is rendered as a link with href "/custom"
    const customElement = screen.getByText('Custom');
    expect(customElement).toBeTruthy();
    const customLink = customElement.closest('a');
    expect(customLink).not.toBeNull();
    expect(customLink!.getAttribute('href')).toBe('/custom');

    // "Page" is rendered as an active item without a link
    const pageElement = screen.getByText('Page');
    expect(pageElement).toBeTruthy();
    expect(pageElement.tagName.toLowerCase()).toBe('span');
    expect(pageElement.closest('a')).toBeNull();
    expect(pageElement.getAttribute('aria-current')).toBe('page');

    // In explicit mode the component does NOT auto-prepend "Home"
    // (the showHome prop only affects route-based generation)
    expect(screen.queryByText('Home')).toBeNull();
  });

  it('marks last explicit item active when isActive is not set', () => {
    const customItems = [
      { label: 'Section', href: '/section' },
      { label: 'SubSection', href: '/section/sub' },
      { label: 'Current' }, // No isActive flag — component auto-marks last
    ];

    renderWithRouter(<Breadcrumb items={customItems} />, {
      initialEntries: ['/'],
      routePath: '/*',
    });

    // The last item "Current" should be active even without explicit flag
    const currentElement = screen.getByText('Current');
    expect(currentElement.tagName.toLowerCase()).toBe('span');
    expect(currentElement.getAttribute('aria-current')).toBe('page');
  });

  // -----------------------------------------------------------------------
  // 6. Separators between breadcrumb items
  // -----------------------------------------------------------------------

  it('shows separators between breadcrumb items', () => {
    const { container } = renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Expected items: Home, My App, Contacts, All Contacts, List = 5 items
    // Separators appear between consecutive items = 4 separators
    // Each separator is an <svg> with aria-hidden="true"
    const separators = container.querySelectorAll('svg[aria-hidden="true"]');
    expect(separators.length).toBe(4);
  });

  it('renders no separators when only one item is present', () => {
    // At root with only Home, no separator needed
    const { container } = renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/'],
      routePath: '/*',
    });

    const separators = container.querySelectorAll('svg[aria-hidden="true"]');
    expect(separators.length).toBe(0);
  });

  // -----------------------------------------------------------------------
  // 7. showHome prop
  // -----------------------------------------------------------------------

  it('hides home breadcrumb when showHome is false', () => {
    renderWithRouter(<Breadcrumb showHome={false} />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // "Home" must NOT appear in the document
    expect(screen.queryByText('Home')).toBeNull();

    // Other route-derived items should still render
    expect(screen.getByText('My App')).toBeTruthy();
    expect(screen.getByText('Contacts')).toBeTruthy();
    expect(screen.getByText('All Contacts')).toBeTruthy();
  });

  it('shows home breadcrumb by default (showHome defaults to true)', () => {
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    expect(screen.getByText('Home')).toBeTruthy();
  });

  // -----------------------------------------------------------------------
  // 8. Graceful handling of missing route params
  // -----------------------------------------------------------------------

  it('handles missing route params gracefully (only appName)', () => {
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/'],
      routePath: '/:appName/*',
    });

    // Home is present
    expect(screen.getByText('Home')).toBeTruthy();

    // App breadcrumb is present
    expect(screen.getByText('My App')).toBeTruthy();

    // No area or node items
    expect(screen.queryByText('Contacts')).toBeNull();
    expect(screen.queryByText('All Contacts')).toBeNull();
  });

  it('handles missing route params gracefully (appName and areaName only)', () => {
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/'],
      routePath: '/:appName/:areaName/*',
    });

    expect(screen.getByText('Home')).toBeTruthy();
    expect(screen.getByText('My App')).toBeTruthy();
    expect(screen.getByText('Contacts')).toBeTruthy();

    // No node item
    expect(screen.queryByText('All Contacts')).toBeNull();
  });

  it('renders empty nav when showHome is false and no params', () => {
    const { container } = renderWithRouter(<Breadcrumb showHome={false} />, {
      initialEntries: ['/'],
      routePath: '/*',
    });

    // Nav exists but has no list items
    const nav = container.querySelector('nav[aria-label="Breadcrumb"]');
    expect(nav).not.toBeNull();
    const listItems = container.querySelectorAll('li');
    expect(listItems.length).toBe(0);
  });

  // -----------------------------------------------------------------------
  // 9. Tailwind CSS class application
  // -----------------------------------------------------------------------

  it('applies proper Tailwind CSS classes', () => {
    const { container } = renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Nav element has text-sm and text-gray-600 Tailwind classes
    const nav = container.querySelector('nav');
    expect(nav).not.toBeNull();
    const navClasses = nav!.className;
    expect(navClasses).toContain('text-sm');
    expect(navClasses).toContain('text-gray-600');
    expect(navClasses).toContain('flex');
    expect(navClasses).toContain('items-center');

    // Active (last) item has font-semibold and text-gray-900
    const activeSpan = container.querySelector('span[aria-current="page"]');
    expect(activeSpan).not.toBeNull();
    const activeClasses = activeSpan!.className;
    expect(activeClasses).toContain('font-semibold');
    expect(activeClasses).toContain('text-gray-900');

    // Link items have hover styling classes
    const links = container.querySelectorAll('a');
    expect(links.length).toBeGreaterThan(0);
    const firstLinkClasses = links[0].className;
    expect(firstLinkClasses).toContain('hover:text-blue-600');
    expect(firstLinkClasses).toContain('hover:underline');
    expect(firstLinkClasses).toContain('transition-colors');
  });

  it('appends custom className to nav element', () => {
    const { container } = renderWithRouter(
      <Breadcrumb className="mt-4 mb-2" />,
      {
        initialEntries: ['/my_app/contacts/all_contacts/l/'],
        routePath: '/:appName/:areaName/:nodeName/*',
      },
    );

    const nav = container.querySelector('nav');
    expect(nav).not.toBeNull();
    expect(nav!.className).toContain('mt-4');
    expect(nav!.className).toContain('mb-2');
    // Default classes should also still be present
    expect(nav!.className).toContain('text-sm');
  });

  // -----------------------------------------------------------------------
  // 10. Semantic HTML
  // -----------------------------------------------------------------------

  it('uses semantic HTML', () => {
    const { container } = renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Component renders a <nav> with aria-label
    const nav = container.querySelector('nav');
    expect(nav).not.toBeNull();
    expect(nav!.getAttribute('aria-label')).toBe('Breadcrumb');

    // Breadcrumb items are inside an <ol> list with role="list"
    const ol = nav!.querySelector('ol');
    expect(ol).not.toBeNull();
    expect(ol!.getAttribute('role')).toBe('list');

    // Each item is an <li> element
    const liItems = ol!.querySelectorAll('li');
    expect(liItems.length).toBeGreaterThan(0);

    // All breadcrumb content (text + links) is inside <li> elements
    // Verify the number of <li> elements matches the expected item count
    // (Home + My App + Contacts + All Contacts + List = 5)
    expect(liItems.length).toBe(5);
  });

  it('renders nav role accessible via getByRole', () => {
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/all_contacts/l/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    // Verify the navigation landmark is discoverable by role
    const navElement = screen.getByRole('navigation');
    expect(navElement).toBeTruthy();
    expect(navElement.getAttribute('aria-label')).toBe('Breadcrumb');
  });

  // -----------------------------------------------------------------------
  // 11. Additional monolith URL pattern handling
  // -----------------------------------------------------------------------

  it('handles record create route (/c/)', () => {
    // Maps to monolith pattern: /{AppName}/{AreaName}/{NodeName}/c/
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/sales/pipeline/leads/c/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    expect(screen.getByText('Home')).toBeTruthy();
    expect(screen.getByText('Sales')).toBeTruthy();
    expect(screen.getByText('Pipeline')).toBeTruthy();
    expect(screen.getByText('Leads')).toBeTruthy();

    // Page context resolves "c" → "Create"
    const createItem = screen.getByText('Create');
    expect(createItem).toBeTruthy();
    expect(createItem.tagName.toLowerCase()).toBe('span');
    expect(createItem.getAttribute('aria-current')).toBe('page');
  });

  it('handles record detail route (/r/:recordId)', () => {
    // Maps to monolith pattern: /{AppName}/{AreaName}/{NodeName}/r/{RecordId}/
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/sales/pipeline/leads/r/abc-123/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    expect(screen.getByText('Home')).toBeTruthy();
    expect(screen.getByText('Sales')).toBeTruthy();
    expect(screen.getByText('Pipeline')).toBeTruthy();
    expect(screen.getByText('Leads')).toBeTruthy();

    // Page context resolves "r" + "abc-123" → "Record abc-123"
    const recordItem = screen.getByText('Record abc-123');
    expect(recordItem).toBeTruthy();
    expect(recordItem.tagName.toLowerCase()).toBe('span');
    expect(recordItem.getAttribute('aria-current')).toBe('page');
  });

  it('handles record manage/edit route (/m/:recordId)', () => {
    // Maps to monolith pattern: /{AppName}/{AreaName}/{NodeName}/m/{RecordId}/
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/sales/pipeline/leads/m/xyz-789/'],
      routePath: '/:appName/:areaName/:nodeName/*',
    });

    expect(screen.getByText('Home')).toBeTruthy();
    expect(screen.getByText('Sales')).toBeTruthy();
    expect(screen.getByText('Pipeline')).toBeTruthy();
    expect(screen.getByText('Leads')).toBeTruthy();

    // Page context resolves "m" + "xyz-789" → "Edit xyz-789"
    const editItem = screen.getByText('Edit xyz-789');
    expect(editItem).toBeTruthy();
    expect(editItem.tagName.toLowerCase()).toBe('span');
    expect(editItem.getAttribute('aria-current')).toBe('page');
  });

  it('handles application page route (/a/) without extra breadcrumb', () => {
    // Maps to monolith pattern: /{AppName}/a/{PageName}
    // For /a/ tail the component returns null (no extra page context label)
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/a/'],
      routePath: '/:appName/*',
    });

    expect(screen.getByText('Home')).toBeTruthy();
    expect(screen.getByText('My App')).toBeTruthy();

    // No "List", "Create", or extra page-context breadcrumb
    expect(screen.queryByText('List')).toBeNull();
    expect(screen.queryByText('Create')).toBeNull();
  });

  // -----------------------------------------------------------------------
  // 12. Edge cases
  // -----------------------------------------------------------------------

  it('does not crash with no props', () => {
    // Default props: showHome=true, no items, no className
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/'],
      routePath: '/*',
    });

    // At minimum, Home is rendered
    expect(screen.getByText('Home')).toBeTruthy();
  });

  it('handles area href correctly when nodeName is absent', () => {
    // When nodeName is not in the route, area href falls back to
    // /{appName}/{areaName}/ instead of /{appName}/{areaName}/{nodeName}/l/
    renderWithRouter(<Breadcrumb />, {
      initialEntries: ['/my_app/contacts/'],
      routePath: '/:appName/:areaName/*',
    });

    const areaElement = screen.getByText('Contacts');
    expect(areaElement).toBeTruthy();
    // Area is the last item → becomes active (no link)
    expect(areaElement.tagName.toLowerCase()).toBe('span');
    expect(areaElement.closest('a')).toBeNull();
  });
});
