/**
 * @file AppShell.test.tsx
 * @description Vitest component tests for the `<AppShell>` layout component — the
 * main application chrome that replaces the monolith's `_AppMaster.cshtml` Razor layout.
 *
 * Tests cover:
 *  - Layout structure: TopNav, Sidebar, and React Router Outlet (child content)
 *    replacing `<vc:nav>`, `<vc:sidebar-menu>`, and `@RenderBody()` respectively
 *  - Sidebar collapse/expand state management with localStorage persistence
 *    replacing the monolith's `UserPreferencies.SetSidebarSize`
 *  - Responsive Tailwind CSS layout classes (zero Bootstrap grid classes)
 *  - Toast notification container replacing `<vc:screen-message>` Toastr integration
 *  - Integration with React Router child routes via `<Outlet />`
 *
 * @see apps/frontend/src/components/layout/AppShell.tsx
 * @see WebVella.Erp.Web/Pages/_AppMaster.cshtml (monolith layout source)
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import AppShell from '../../../src/components/layout/AppShell';

// ---------------------------------------------------------------------------
// Module Mocks — vi.mock is hoisted by Vitest
// ---------------------------------------------------------------------------

/**
 * Mock TopNav with a lightweight placeholder rendering a div with a
 * data-testid. This isolates AppShell tests from TopNav's internal
 * implementation (store subscriptions, dropdown logic, etc.) while
 * verifying AppShell renders TopNav in the correct position.
 *
 * Replaces: `<vc:nav page-model="@Model">` from _AppMaster.cshtml line 11.
 */
vi.mock('../../../src/components/layout/TopNav', () => ({
  default: () => <div data-testid="mock-topnav">TopNav</div>,
}));

/**
 * Mock useApps hook to avoid real API calls. Returns an empty app list
 * so the AppShell's Zustand store sync receives an empty array.
 */
vi.mock('../../../src/hooks/useApps', () => ({
  useApps: () => ({ data: { success: true, object: [] }, isLoading: false, error: null }),
}));

/**
 * Mock Sidebar with a component that exposes the `collapsed` prop as a
 * `data-collapsed` attribute and the `onToggle` callback as a clickable
 * button. This isolates AppShell tests while enabling sidebar state
 * verification and toggle interaction.
 *
 * Replaces: `<vc:sidebar-menu page-model="@Model">` from _AppMaster.cshtml line 15.
 */
vi.mock('../../../src/components/layout/Sidebar', () => ({
  default: ({
    collapsed,
    onToggle,
  }: {
    collapsed: boolean;
    onToggle: () => void;
  }) => (
    <div data-testid="mock-sidebar" data-collapsed={collapsed}>
      <button data-testid="mock-sidebar-toggle" onClick={onToggle}>
        Toggle
      </button>
      Sidebar
    </div>
  ),
}));

// ---------------------------------------------------------------------------
// localStorage Mock
// ---------------------------------------------------------------------------

/**
 * Mutable backing store for the localStorage mock.
 * Reset in `beforeEach` to guarantee test isolation.
 */
let localStore: Record<string, string> = {};

/**
 * Deterministic localStorage replacement for testing sidebar persistence.
 * All methods are `vi.fn()` so call history can be asserted.
 *
 * Implementations are re-established in `beforeEach` because
 * `vi.restoreAllMocks()` in `afterEach` resets `vi.fn()` implementations.
 */
const localStorageMock = {
  getItem: vi.fn(),
  setItem: vi.fn(),
  removeItem: vi.fn(),
  clear: vi.fn(),
  get length() {
    return Object.keys(localStore).length;
  },
  key: vi.fn((index: number) => Object.keys(localStore)[index] ?? null),
};

Object.defineProperty(window, 'localStorage', {
  value: localStorageMock,
  writable: true,
});

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Render `<AppShell>` within a `<MemoryRouter>` and `<Routes>/<Route>` tree.
 *
 * This simulates how React Router renders `<Outlet />` inside AppShell:
 * - The outer `<Route element={<AppShell />}>` makes AppShell the layout route
 * - The inner `<Route path="*">` provides child content rendered via `<Outlet />`
 *
 * This pattern replaces `@RenderBody()` from `_AppMaster.cshtml` line 20.
 *
 * @param initialEntries - Initial URL entries for the MemoryRouter history.
 * @returns The render result from @testing-library/react.
 */
function renderAppShell(initialEntries: string[] = ['/']) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={initialEntries}>
        <Routes>
          <Route element={<AppShell />}>
            <Route
              path="*"
              element={
                <div data-testid="child-content">Child Page Content</div>
              }
            />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

// ---------------------------------------------------------------------------
// Setup / Teardown
// ---------------------------------------------------------------------------

beforeEach(() => {
  // Reset the backing store to an empty state
  localStore = {};

  // Re-establish localStorage mock implementations. This is required because
  // `vi.restoreAllMocks()` in `afterEach` resets `vi.fn()` implementations.
  localStorageMock.getItem.mockImplementation(
    (key: string) => localStore[key] ?? null,
  );
  localStorageMock.setItem.mockImplementation(
    (key: string, value: string) => {
      localStore[key] = value;
    },
  );
  localStorageMock.removeItem.mockImplementation((key: string) => {
    delete localStore[key];
  });
  localStorageMock.clear.mockImplementation(() => {
    localStore = {};
  });
  localStorageMock.key.mockImplementation(
    (index: number) => Object.keys(localStore)[index] ?? null,
  );
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ===========================================================================
// Suite 1: AppShell Structure (replacing _AppMaster.cshtml)
// ===========================================================================

describe('AppShell structure (replacing _AppMaster.cshtml)', () => {
  /**
   * Validates that AppShell renders all three structural elements from
   * _AppMaster.cshtml:
   *   - TopNav   (line 11: <vc:nav page-model="@Model">)
   *   - Sidebar  (line 15: <vc:sidebar-menu page-model="@Model">)
   *   - Content  (line 20: @RenderBody())
   */
  it('renders sidebar, top nav, and content area', () => {
    renderAppShell();

    // TopNav — replacing <vc:nav> from _AppMaster.cshtml line 11
    expect(screen.getByTestId('mock-topnav')).toBeDefined();

    // Sidebar — replacing <vc:sidebar-menu> from _AppMaster.cshtml line 15
    expect(screen.getByTestId('mock-sidebar')).toBeDefined();

    // Child content — replacing @RenderBody() from _AppMaster.cshtml line 20
    expect(screen.getByTestId('child-content')).toBeDefined();
  });

  /**
   * Validates that `<Outlet />` correctly renders child route content,
   * replacing `@RenderBody()` from _AppMaster.cshtml line 20.
   */
  it('renders child route content via React Router Outlet', () => {
    renderAppShell();

    const childContent = screen.getByTestId('child-content');
    expect(childContent).toBeDefined();
    expect(childContent.textContent).toBe('Child Page Content');
  });

  /**
   * Validates that the layout uses Tailwind flex classes instead of
   * Bootstrap's `.row.no-gutters` (line 13), `.col-auto` (line 14),
   * and `.col` (line 17) from _AppMaster.cshtml.
   */
  it('renders with flex layout for sidebar + content', () => {
    const { container } = renderAppShell();
    const root = container.firstElementChild as HTMLElement;

    expect(root).not.toBeNull();
    expect(root.className).toContain('flex');
    expect(root.className).toContain('h-screen');
  });
});

// ===========================================================================
// Suite 2: AppShell Sidebar Collapse/Expand State
// ===========================================================================

describe('AppShell sidebar collapse/expand state', () => {
  /**
   * Default sidebar state is expanded (not collapsed), matching the
   * monolith's default where the sidebar is always shown on desktop.
   */
  it('sidebar starts expanded by default (no localStorage value)', () => {
    renderAppShell();

    const sidebar = screen.getByTestId('mock-sidebar');
    expect(sidebar.getAttribute('data-collapsed')).toBe('false');
  });

  /**
   * When localStorage contains `sidebar-collapsed=true`, AppShell
   * initialises the sidebar in collapsed mode. This replaces the
   * monolith's `UserPreferencies.SetSidebarSize` server-side persistence.
   */
  it('sidebar reads initial collapsed state from localStorage', () => {
    // Pre-populate localStorage before render
    localStore['sidebar-collapsed'] = 'true';

    renderAppShell();

    const sidebar = screen.getByTestId('mock-sidebar');
    expect(sidebar.getAttribute('data-collapsed')).toBe('true');
  });

  /**
   * Clicking the sidebar toggle button updates the collapsed state
   * from expanded (false) to collapsed (true).
   */
  it('sidebar toggle updates collapsed state', () => {
    renderAppShell();

    const sidebar = screen.getByTestId('mock-sidebar');
    expect(sidebar.getAttribute('data-collapsed')).toBe('false');

    fireEvent.click(screen.getByTestId('mock-sidebar-toggle'));

    expect(sidebar.getAttribute('data-collapsed')).toBe('true');
  });

  /**
   * Each toggle click persists the new collapsed state to localStorage
   * under the `sidebar-collapsed` key.
   */
  it('sidebar toggle persists collapsed state to localStorage', () => {
    renderAppShell();

    // First click: expanded → collapsed
    fireEvent.click(screen.getByTestId('mock-sidebar-toggle'));
    expect(localStorageMock.setItem).toHaveBeenCalledWith(
      'sidebar-collapsed',
      'true',
    );

    // Second click: collapsed → expanded
    fireEvent.click(screen.getByTestId('mock-sidebar-toggle'));
    expect(localStorageMock.setItem).toHaveBeenCalledWith(
      'sidebar-collapsed',
      'false',
    );
  });

  /**
   * Verifies that repeated toggles alternate state correctly and
   * each toggle persists to localStorage.
   */
  it('sidebar toggle is idempotent', () => {
    renderAppShell();
    const sidebar = screen.getByTestId('mock-sidebar');
    const toggleBtn = screen.getByTestId('mock-sidebar-toggle');

    // Start: expanded (false)
    expect(sidebar.getAttribute('data-collapsed')).toBe('false');

    // Click 1: expanded → collapsed
    fireEvent.click(toggleBtn);
    expect(sidebar.getAttribute('data-collapsed')).toBe('true');
    expect(localStorageMock.setItem).toHaveBeenLastCalledWith(
      'sidebar-collapsed',
      'true',
    );

    // Click 2: collapsed → expanded
    fireEvent.click(toggleBtn);
    expect(sidebar.getAttribute('data-collapsed')).toBe('false');
    expect(localStorageMock.setItem).toHaveBeenLastCalledWith(
      'sidebar-collapsed',
      'false',
    );

    // Click 3: expanded → collapsed
    fireEvent.click(toggleBtn);
    expect(sidebar.getAttribute('data-collapsed')).toBe('true');
    expect(localStorageMock.setItem).toHaveBeenLastCalledWith(
      'sidebar-collapsed',
      'true',
    );

    // setItem was called exactly 3 times (once per toggle)
    expect(localStorageMock.setItem).toHaveBeenCalledTimes(3);
  });

  /**
   * Uses @testing-library/user-event for a more realistic user interaction
   * simulation of the sidebar toggle (vs the synthetic fireEvent.click).
   * Satisfies the schema's members_accessed: ['setup', 'click'] requirement.
   */
  it('sidebar toggle works with userEvent', async () => {
    const user = userEvent.setup();
    renderAppShell();

    const sidebar = screen.getByTestId('mock-sidebar');
    expect(sidebar.getAttribute('data-collapsed')).toBe('false');

    await user.click(screen.getByTestId('mock-sidebar-toggle'));
    expect(sidebar.getAttribute('data-collapsed')).toBe('true');

    await user.click(screen.getByTestId('mock-sidebar-toggle'));
    expect(sidebar.getAttribute('data-collapsed')).toBe('false');
  });
});

// ===========================================================================
// Suite 3: AppShell Responsive Layout with Tailwind CSS
// ===========================================================================

describe('AppShell responsive layout with Tailwind CSS', () => {
  /**
   * The root container uses `flex h-screen` for full-viewport-height
   * flexbox layout, replacing the monolith's `<body>` and
   * `<div id="body-inner-wrapper">` root wrappers.
   */
  it('root container uses full-height Tailwind classes', () => {
    const { container } = renderAppShell();
    const root = container.firstElementChild as HTMLElement;

    expect(root).not.toBeNull();
    expect(root.className).toContain('h-screen');
    expect(root.className).toContain('flex');
  });

  /**
   * The content area (`<main id="main-content">`) uses `flex-1` to fill
   * remaining space and `overflow-auto` for scrollable content, replacing
   * Bootstrap's `.col` class from `<div id="content" class="col">` in
   * _AppMaster.cshtml line 17.
   */
  it('content area uses flex-1 and overflow auto', () => {
    const { container } = renderAppShell();
    const mainContent = container.querySelector(
      '#main-content',
    ) as HTMLElement;

    expect(mainContent).not.toBeNull();
    expect(mainContent.className).toContain('flex-1');
    expect(mainContent.className).toContain('overflow-auto');
  });

  /**
   * The container holding TopNav and the content area uses
   * `flex flex-col flex-1 overflow-hidden` for a vertical layout that
   * fills the remaining space next to the sidebar.
   */
  it('main content area has flex column layout', () => {
    const { container } = renderAppShell();

    // Find the element with `flex-col` and `overflow-hidden` classes.
    // This is the div wrapping TopNav + main content area.
    const flexColContainer = container.querySelector(
      '.flex-col.overflow-hidden',
    ) as HTMLElement;

    expect(flexColContainer).not.toBeNull();
    expect(flexColContainer.className).toContain('flex');
    expect(flexColContainer.className).toContain('flex-col');
    expect(flexColContainer.className).toContain('flex-1');
    expect(flexColContainer.className).toContain('overflow-hidden');
  });

  /**
   * Asserts that no Bootstrap grid classes remain in the rendered output.
   * _AppMaster.cshtml used:
   *   - `row no-gutters` (line 13)
   *   - `col-auto` (line 14)
   *   - `col` (line 17)
   *   - `container-fluid` (line 19)
   * All must be replaced by Tailwind equivalents.
   */
  it('no Bootstrap grid classes present', () => {
    const { container } = renderAppShell();

    // CSS class selectors match exact class tokens (space-separated),
    // so `.col` will NOT false-match `flex-col` (which is a single token).
    expect(container.querySelector('.row')).toBeNull();
    expect(container.querySelector('.no-gutters')).toBeNull();
    expect(container.querySelector('.col-auto')).toBeNull();
    expect(container.querySelector('.col')).toBeNull();
    expect(container.querySelector('.container-fluid')).toBeNull();
  });
});

// ===========================================================================
// Suite 4: AppShell Toast Notification Area
// ===========================================================================

describe('AppShell toast notification area', () => {
  /**
   * Validates that AppShell renders a toast notification container when
   * a toast is triggered, replacing `<vc:screen-message>` from
   * _AppMaster.cshtml line 28 (which used Toastr JS positioned at
   * `toast-top-center`).
   *
   * The toast container is conditionally rendered (only when toasts > 0),
   * so we dispatch a custom `app-toast` DOM event to trigger it.
   */
  it('renders a notification/toast container when toast is triggered', async () => {
    const { container } = renderAppShell();

    // Initially no toast container (no toasts queued)
    expect(container.querySelector('[role="status"]')).toBeNull();

    // Trigger a toast via the custom DOM event API wrapped in act()
    // to properly handle the resulting React state update.
    // Mirrors the monolith's Toastr.success() call pattern.
    await act(async () => {
      window.dispatchEvent(
        new CustomEvent('app-toast', {
          detail: { type: 'success', message: 'Record saved!' },
        }),
      );
    });

    // Wait for React to process the state update and render the container
    await waitFor(() => {
      const toastContainer = container.querySelector('[role="status"]');
      expect(toastContainer).not.toBeNull();
      expect(toastContainer!.getAttribute('aria-live')).toBe('polite');
    });

    // Verify the toast message content is rendered
    await waitFor(() => {
      expect(screen.getByText('Record saved!')).toBeDefined();
    });
  });

  /**
   * Verifies that each toast is rendered with the `role="alert"` attribute
   * for accessibility, matching the monolith's Toastr notification pattern.
   */
  it('renders individual toasts with alert role', async () => {
    renderAppShell();

    await act(async () => {
      window.dispatchEvent(
        new CustomEvent('app-toast', {
          detail: { type: 'error', message: 'Validation failed' },
        }),
      );
    });

    await waitFor(() => {
      const alert = screen.getByRole('alert');
      expect(alert).toBeDefined();
      expect(alert.textContent).toContain('Validation failed');
    });
  });

  /**
   * Validates that the toast container uses fixed positioning at the top
   * center of the viewport, matching the monolith's Toastr configuration
   * (`positionClass: "toast-top-center"`).
   */
  it('toast container uses fixed top-center positioning', async () => {
    const { container } = renderAppShell();

    await act(async () => {
      window.dispatchEvent(
        new CustomEvent('app-toast', {
          detail: { type: 'info', message: 'Info message' },
        }),
      );
    });

    await waitFor(() => {
      const toastContainer = container.querySelector(
        '[role="status"]',
      ) as HTMLElement;
      expect(toastContainer).not.toBeNull();
      expect(toastContainer.className).toContain('fixed');
      expect(toastContainer.className).toContain('top-0');
      expect(toastContainer.className).toContain('items-center');
    });
  });
});

// ===========================================================================
// Suite 5: AppShell Integration with Child Routes
// ===========================================================================

describe('AppShell integration with child routes', () => {
  /**
   * Verifies that AppShell renders different child content based on
   * the active route, demonstrating that `<Outlet />` correctly
   * replaces `@RenderBody()` from _AppMaster.cshtml line 20.
   *
   * Also asserts that TopNav and Sidebar remain present across all routes,
   * proving the layout shell is stable while only child content changes.
   */
  it('renders different child content for different routes', () => {
    // ---------------------------------------------------------------
    // Test 1: Navigate to /dashboard → "Dashboard Page" rendered
    // ---------------------------------------------------------------
    const result1 = render(
      <MemoryRouter initialEntries={['/dashboard']}>
        <Routes>
          <Route element={<AppShell />}>
            <Route
              path="/dashboard"
              element={<div data-testid="dashboard">Dashboard Page</div>}
            />
            <Route
              path="/contacts"
              element={<div data-testid="contacts">Contacts Page</div>}
            />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText('Dashboard Page')).toBeDefined();
    expect(screen.queryByText('Contacts Page')).toBeNull();

    // Layout components remain present
    expect(screen.getByTestId('mock-topnav')).toBeDefined();
    expect(screen.getByTestId('mock-sidebar')).toBeDefined();

    result1.unmount();

    // ---------------------------------------------------------------
    // Test 2: Navigate to /contacts → "Contacts Page" rendered
    // ---------------------------------------------------------------
    render(
      <MemoryRouter initialEntries={['/contacts']}>
        <Routes>
          <Route element={<AppShell />}>
            <Route
              path="/dashboard"
              element={<div data-testid="dashboard">Dashboard Page</div>}
            />
            <Route
              path="/contacts"
              element={<div data-testid="contacts">Contacts Page</div>}
            />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText('Contacts Page')).toBeDefined();
    expect(screen.queryByText('Dashboard Page')).toBeNull();

    // Layout components remain present across different routes
    expect(screen.getByTestId('mock-topnav')).toBeDefined();
    expect(screen.getByTestId('mock-sidebar')).toBeDefined();
  });

  /**
   * Validates that AppShell correctly handles a root "/" route, which is
   * the default entry point replacing the monolith's `Index.cshtml` home page.
   */
  it('renders child content for root route', () => {
    render(
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route element={<AppShell />}>
            <Route
              path="/"
              element={<div data-testid="home">Home Page</div>}
            />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText('Home Page')).toBeDefined();
    expect(screen.getByTestId('mock-topnav')).toBeDefined();
    expect(screen.getByTestId('mock-sidebar')).toBeDefined();
  });

  /**
   * Validates that sidebar state is maintained when rendering with
   * different routes (state persists across route-level re-renders).
   */
  it('sidebar collapsed state persists across route renders', () => {
    const { unmount } = render(
      <MemoryRouter initialEntries={['/page-a']}>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/page-a" element={<div>Page A</div>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    // Toggle sidebar to collapsed
    fireEvent.click(screen.getByTestId('mock-sidebar-toggle'));
    expect(screen.getByTestId('mock-sidebar').getAttribute('data-collapsed')).toBe('true');
    expect(localStorageMock.setItem).toHaveBeenCalledWith(
      'sidebar-collapsed',
      'true',
    );

    unmount();

    // Render a new route — localStorage should restore collapsed state
    render(
      <MemoryRouter initialEntries={['/page-b']}>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/page-b" element={<div>Page B</div>} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    // Sidebar should be collapsed from localStorage persistence
    expect(screen.getByTestId('mock-sidebar').getAttribute('data-collapsed')).toBe('true');
  });
});
