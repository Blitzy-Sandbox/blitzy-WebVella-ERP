/**
 * Vitest Component Tests for `<Drawer />`
 *
 * Validates the React Drawer component that replaces the monolith's
 * `PcDrawer` ViewComponent (`WebVella.Erp.Web/Components/PcDrawer/PcDrawer.cs`,
 * `Display.cshtml`) and the `WvDrawer` TagHelper + `drawer.js` jQuery behaviour.
 *
 * The monolith's PcDrawerOptions define six configuration properties:
 *  - is_visible (string → boolean): controls drawer visibility
 *  - width (string, default "550px"): CSS width of the drawer panel
 *  - title (string): optional header title (resolved via data source)
 *  - title_action_html (string): raw HTML action area in the header
 *  - class (string): additional CSS classes on the outer wrapper
 *  - body_class (string): CSS classes on the content body wrapper
 *
 * The `drawer.js` jQuery behaviour provided three close triggers:
 *  1. Backdrop click (lines 74-78, delegated click on `.drawer-backdrop`)
 *  2. Close button in header (`<button class="drawer-close">`)
 *  3. ErpEvent bus dispatch for programmatic close
 *
 * In the React rewrite, these map to:
 *  1. Backdrop overlay `onClick` → calls `onClose` prop callback
 *  2. Header close button `onClick` → calls `onClose` prop callback
 *  3. `Escape` key via `document.addEventListener('keydown')` → calls `onClose`
 *
 * Test coverage includes:
 *  - Basic rendering (visibility, default/custom width, children, id)
 *  - Slide-in/slide-out visibility toggle (replaces jQuery d-block toggling)
 *  - Position default right-side rendering
 *  - Overlay/backdrop rendering and click-to-close
 *  - Close button rendering and callback invocation
 *  - Title/header conditional rendering and title action area
 *  - Content area with bodyClassName and scrollable overflow
 *  - Escape key close trigger with listener cleanup
 *  - Portal rendering via createPortal to document.body
 *  - ARIA accessibility (role=dialog, aria-modal, aria-label)
 *  - Multiple drawers isolation
 *
 * @see apps/frontend/src/components/common/Drawer.tsx
 * @see WebVella.Erp.Web/Components/PcDrawer/PcDrawer.cs (monolith source)
 */
import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import Drawer, { type DrawerProps } from '../../../src/components/common/Drawer';

/* -------------------------------------------------------------------------- */
/*  Test-wide lifecycle                                                       */
/* -------------------------------------------------------------------------- */

beforeEach(() => {
  // Ensure clean body state before each test for complete isolation.
  // Resets any overflow style that a previous test may have left behind.
  document.body.style.overflow = '';
});

afterEach(() => {
  // Reset body scroll lock that the Drawer component may have set.
  // React Testing Library's auto-cleanup unmounts components and removes
  // portal-rendered DOM nodes. We only need to reset the body overflow
  // style that the component's scroll-lock useEffect applies.
  document.body.style.overflow = '';
});

/* -------------------------------------------------------------------------- */
/*  Helpers                                                                   */
/* -------------------------------------------------------------------------- */

/**
 * Renders the Drawer with sensible defaults for props that most tests
 * need but don't specifically test.  Individual tests override as needed.
 */
function renderDrawer(overrides: Partial<DrawerProps> = {}) {
  const defaultProps: DrawerProps = {
    isVisible: true,
    onClose: vi.fn(),
    ...overrides,
  };
  return render(<Drawer {...defaultProps} />);
}

/* ========================================================================== */
/*  2.1 — Basic Rendering Tests                                               */
/* ========================================================================== */

describe('Drawer — Basic Rendering', () => {
  it('renders nothing when isVisible is false', () => {
    // PcDrawerOptions.IsVisible default empty string resolves to true in monolith,
    // but React component defaults isVisible to false for controlled behaviour.
    renderDrawer({ isVisible: false });

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByTestId('drawer-panel')).toBeNull();
    expect(screen.queryByTestId('drawer-backdrop')).toBeNull();
  });

  it('renders drawer when isVisible is true', () => {
    renderDrawer({ isVisible: true });

    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.getByTestId('drawer-panel')).toBeTruthy();
  });

  it('renders with default width of 550px', () => {
    // PcDrawerOptions.Width default is "550px" (PcDrawer.cs line 30).
    renderDrawer();

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.style.width).toBe('550px');
  });

  it('renders with custom width', () => {
    renderDrawer({ width: '400px' });

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.style.width).toBe('400px');
  });

  it('renders drawer content (children)', () => {
    render(
      <Drawer isVisible onClose={vi.fn()}>
        <p>Drawer Content</p>
      </Drawer>,
    );

    expect(screen.getByText('Drawer Content')).toBeTruthy();
  });

  it('renders id attribute on the drawer panel', () => {
    // WvDrawer.cs builds the id as "wv-{node.Id}".
    renderDrawer({ id: 'my-drawer' });

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.id).toBe('my-drawer');
  });
});

/* ========================================================================== */
/*  2.2 — Slide-in / Slide-out Behaviour Tests                               */
/* ========================================================================== */

describe('Drawer — Slide-in / Slide-out', () => {
  it('drawer becomes visible when isVisible changes from false to true', () => {
    // Simulates jQuery OpenDrawer() adding .d-block → React isVisible toggle.
    const onClose = vi.fn();
    const { rerender } = render(<Drawer isVisible={false} onClose={onClose} />);

    expect(screen.queryByRole('dialog')).toBeNull();

    rerender(<Drawer isVisible onClose={onClose} />);

    expect(screen.getByRole('dialog')).toBeTruthy();
  });

  it('drawer disappears when isVisible changes from true to false', () => {
    // Simulates jQuery CloseDrawer() removing .d-block → React isVisible toggle.
    const onClose = vi.fn();
    const { rerender } = render(<Drawer isVisible onClose={onClose} />);

    expect(screen.getByRole('dialog')).toBeTruthy();

    rerender(<Drawer isVisible={false} onClose={onClose} />);

    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('drawer has animation class when visible', () => {
    // The monolith's drawer.js used jQuery DOM manipulation for open/close.
    // The React replacement uses CSS keyframe animation via the
    // `wv-drawer-panel` class selector targeted by @keyframes wv-drawer-slide-in.
    renderDrawer();

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.classList.contains('wv-drawer-panel')).toBe(true);
  });
});

/* ========================================================================== */
/*  2.3 — Position Tests                                                      */
/* ========================================================================== */

describe('Drawer — Position', () => {
  it('defaults to right position', () => {
    // WvDrawer in the monolith defaults to right-side rendering.
    // The React component uses Tailwind `right-0` for right-side positioning.
    renderDrawer();

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.classList.contains('right-0')).toBe(true);
  });

  it('renders with top-0 for full-height positioning', () => {
    renderDrawer();

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.classList.contains('top-0')).toBe(true);
    expect(panel.classList.contains('h-full')).toBe(true);
  });

  it('renders with fixed positioning for viewport-relative placement', () => {
    renderDrawer();

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.classList.contains('fixed')).toBe(true);
  });
});

/* ========================================================================== */
/*  2.4 — Overlay / Backdrop Tests                                            */
/* ========================================================================== */

describe('Drawer — Overlay / Backdrop', () => {
  it('renders backdrop overlay when visible', () => {
    // Monolith's drawer.js (lines 8-9) creates `.drawer-backdrop` div.
    // React replacement renders a fixed overlay with semi-transparent bg.
    renderDrawer();

    const backdrop = screen.getByTestId('drawer-backdrop');
    expect(backdrop).toBeTruthy();
    expect(backdrop.classList.contains('fixed')).toBe(true);
    expect(backdrop.classList.contains('wv-drawer-backdrop')).toBe(true);
  });

  it('backdrop click calls onClose', async () => {
    // Replaces drawer.js lines 74-78 backdrop click handler.
    const onClose = vi.fn();
    renderDrawer({ onClose });

    const backdrop = screen.getByTestId('drawer-backdrop');
    const user = userEvent.setup();
    await user.click(backdrop);

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('backdrop is not rendered when drawer is hidden', () => {
    renderDrawer({ isVisible: false });

    expect(screen.queryByTestId('drawer-backdrop')).toBeNull();
  });
});

/* ========================================================================== */
/*  2.5 — Close Button Tests                                                  */
/* ========================================================================== */

describe('Drawer — Close Button', () => {
  it('renders close button in header when title is provided', () => {
    // Monolith's Display.cshtml renders `<button class="drawer-close">` in header.
    renderDrawer({ title: 'My Drawer' });

    const closeButton = screen.getByTestId('drawer-close-button');
    expect(closeButton).toBeTruthy();
  });

  it('close button calls onClose when clicked', async () => {
    const onClose = vi.fn();
    renderDrawer({ title: 'My Drawer', onClose });

    const closeButton = screen.getByTestId('drawer-close-button');
    const user = userEvent.setup();
    await user.click(closeButton);

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('close button has accessible label', () => {
    renderDrawer({ title: 'My Drawer' });

    const closeButton = screen.getByTestId('drawer-close-button');
    expect(closeButton.getAttribute('aria-label')).toBe('Close drawer');
  });

  it('close button is not rendered when title is absent', () => {
    // Without a title, the header (and therefore close button) should not render.
    renderDrawer({ title: undefined });

    expect(screen.queryByTestId('drawer-close-button')).toBeNull();
  });
});

/* ========================================================================== */
/*  2.6 — Title / Header Tests                                                */
/* ========================================================================== */

describe('Drawer — Title / Header', () => {
  it('renders title text in header', () => {
    // PcDrawer.cs resolves title via data source (line 106).
    // React component receives title as a direct string prop.
    renderDrawer({ title: 'Settings' });

    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('does not render header when title is empty string', () => {
    // Matches monolith's conditional header rendering when title is empty.
    // In the React component: `title != null && title !== ''`
    renderDrawer({ title: '' });

    // No close button means no header was rendered.
    expect(screen.queryByTestId('drawer-close-button')).toBeNull();

    // Verify the header border-b div does not exist in the panel.
    const panel = screen.getByTestId('drawer-panel');
    const headerDiv = panel.querySelector('.border-b');
    expect(headerDiv).toBeNull();
  });

  it('does not render header when title is undefined', () => {
    renderDrawer({ title: undefined });

    expect(screen.queryByTestId('drawer-close-button')).toBeNull();
    // Also verify no unexpected title text appears in the DOM.
    expect(screen.queryByText('Settings')).toBeNull();
  });

  it('renders title action content alongside title', () => {
    // PcDrawerOptions.TitleActionHtml → React titleAction prop (ReactNode).
    render(
      <Drawer isVisible onClose={vi.fn()} title="Settings" titleAction={<button>Action</button>}>
        <p>Body</p>
      </Drawer>,
    );

    expect(screen.getByText('Action')).toBeTruthy();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('title action area is within the header section', () => {
    render(
      <Drawer isVisible onClose={vi.fn()} title="Settings" titleAction={<button>Save</button>}>
        <p>Body</p>
      </Drawer>,
    );

    // The title and action should both be in the same border-b header div.
    // We find the panel and check the first child (header) contains both.
    const panel = screen.getByTestId('drawer-panel');
    const headerDiv = panel.querySelector('.border-b');
    expect(headerDiv).toBeTruthy();

    const headerScope = within(headerDiv as HTMLElement);
    expect(headerScope.getByText('Settings')).toBeTruthy();
    expect(headerScope.getByText('Save')).toBeTruthy();
  });

  it('does not render title action when titleAction is null', () => {
    renderDrawer({ title: 'Settings', titleAction: undefined });

    // The ml-auto shrink-0 wrapper for title actions should not be present.
    const panel = screen.getByTestId('drawer-panel');
    const actionWrapper = panel.querySelector('.ml-auto.shrink-0');
    expect(actionWrapper).toBeNull();
  });
});

/* ========================================================================== */
/*  2.7 — Content Area Tests                                                  */
/* ========================================================================== */

describe('Drawer — Content Area', () => {
  it('renders children in content area', () => {
    render(
      <Drawer isVisible onClose={vi.fn()}>
        <span>Inner Content</span>
      </Drawer>,
    );

    const contentArea = screen.getByTestId('drawer-content');
    const childScope = within(contentArea);
    expect(childScope.getByText('Inner Content')).toBeTruthy();
  });

  it('applies bodyClassName to content wrapper', () => {
    // PcDrawerOptions.BodyClass → React bodyClassName prop.
    renderDrawer({ bodyClassName: 'custom-body' });

    const contentArea = screen.getByTestId('drawer-content');
    expect(contentArea.classList.contains('custom-body')).toBe(true);
  });

  it('applies className to outer drawer panel', () => {
    // PcDrawerOptions.Class → React className prop.
    renderDrawer({ className: 'custom-drawer' });

    const panel = screen.getByTestId('drawer-panel');
    expect(panel.classList.contains('custom-drawer')).toBe(true);
  });

  it('content area is scrollable with overflow-y-auto', () => {
    renderDrawer();

    const contentArea = screen.getByTestId('drawer-content');
    expect(contentArea.classList.contains('overflow-y-auto')).toBe(true);
  });

  it('content area has padding applied', () => {
    renderDrawer();

    const contentArea = screen.getByTestId('drawer-content');
    expect(contentArea.classList.contains('p-4')).toBe(true);
  });
});

/* ========================================================================== */
/*  2.8 — Escape Key Close Tests                                              */
/* ========================================================================== */

describe('Drawer — Escape Key Close', () => {
  it('pressing Escape key calls onClose', () => {
    // Replaces ErpEvent.ON('WebVella.Erp.Web.Components.PcDrawer') bus close trigger.
    const onClose = vi.fn();
    renderDrawer({ onClose });

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('Escape key does nothing when drawer is hidden', () => {
    const onClose = vi.fn();
    renderDrawer({ isVisible: false, onClose });

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onClose).not.toHaveBeenCalled();
  });

  it('Escape key listener is cleaned up on unmount', () => {
    const onClose = vi.fn();
    const { unmount } = renderDrawer({ onClose });

    unmount();

    fireEvent.keyDown(document, { key: 'Escape' });

    // After unmount, the keydown listener should have been removed,
    // so onClose should NOT be called.
    expect(onClose).not.toHaveBeenCalled();
  });

  it('Escape key listener is cleaned up when isVisible changes to false', () => {
    const onClose = vi.fn();
    const { rerender } = render(<Drawer isVisible onClose={onClose} />);

    // Verify the listener is active.
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);

    // Re-render with isVisible=false — component returns null,
    // useEffect cleanup should remove the listener.
    rerender(<Drawer isVisible={false} onClose={onClose} />);
    onClose.mockClear();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('does not react to non-Escape keys', () => {
    const onClose = vi.fn();
    renderDrawer({ onClose });

    fireEvent.keyDown(document, { key: 'Enter' });
    fireEvent.keyDown(document, { key: 'Tab' });
    fireEvent.keyDown(document, { key: 'a' });

    expect(onClose).not.toHaveBeenCalled();
  });
});

/* ========================================================================== */
/*  2.9 — Portal Rendering Tests                                              */
/* ========================================================================== */

describe('Drawer — Portal Rendering', () => {
  it('drawer renders via portal to document.body', () => {
    const { container } = renderDrawer();

    // The component's own container should be empty because the drawer
    // is portal-rendered directly into document.body.
    expect(container.querySelector('[role="dialog"]')).toBeNull();

    // But the panel should be accessible at the document.body level.
    const panel = document.body.querySelector('[data-testid="drawer-panel"]');
    expect(panel).toBeTruthy();
    expect(panel?.parentElement).toBe(document.body);
  });

  it('backdrop renders via portal to document.body', () => {
    renderDrawer();

    const backdrop = document.body.querySelector('[data-testid="drawer-backdrop"]');
    expect(backdrop).toBeTruthy();
    expect(backdrop?.parentElement).toBe(document.body);
  });

  it('portal content is cleaned up on unmount', () => {
    const { unmount } = renderDrawer();

    // Before unmount, panel should exist.
    expect(document.body.querySelector('[data-testid="drawer-panel"]')).toBeTruthy();

    unmount();

    // After unmount, portal elements should be removed.
    expect(document.body.querySelector('[data-testid="drawer-panel"]')).toBeNull();
    expect(document.body.querySelector('[data-testid="drawer-backdrop"]')).toBeNull();
  });
});

/* ========================================================================== */
/*  2.10 — Accessibility Tests                                                */
/* ========================================================================== */

describe('Drawer — Accessibility', () => {
  it('drawer has role="dialog"', () => {
    renderDrawer();

    const dialog = screen.getByRole('dialog');
    expect(dialog).toBeTruthy();
  });

  it('drawer has aria-modal="true"', () => {
    renderDrawer();

    const dialog = screen.getByRole('dialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
  });

  it('drawer has aria-label matching the title', () => {
    renderDrawer({ title: 'User Settings' });

    const dialog = screen.getByRole('dialog');
    expect(dialog.getAttribute('aria-label')).toBe('User Settings');
  });

  it('drawer has default aria-label when no title is provided', () => {
    renderDrawer({ title: undefined });

    const dialog = screen.getByRole('dialog');
    expect(dialog.getAttribute('aria-label')).toBe('Drawer');
  });

  it('backdrop has aria-hidden="true"', () => {
    renderDrawer();

    const backdrop = screen.getByTestId('drawer-backdrop');
    expect(backdrop.getAttribute('aria-hidden')).toBe('true');
  });

  it('close button is a <button> element with type="button"', () => {
    renderDrawer({ title: 'Settings' });

    const closeButton = screen.getByTestId('drawer-close-button');
    expect(closeButton.tagName).toBe('BUTTON');
    expect(closeButton.getAttribute('type')).toBe('button');
  });
});

/* ========================================================================== */
/*  2.11 — Multiple Drawers Isolation                                         */
/* ========================================================================== */

describe('Drawer — Multiple Drawers Isolation', () => {
  it('two drawers with different IDs render independently', () => {
    const onClose1 = vi.fn();
    const onClose2 = vi.fn();

    render(
      <>
        <Drawer isVisible id="drawer-1" onClose={onClose1} title="Drawer One">
          <p>Content One</p>
        </Drawer>
        <Drawer isVisible id="drawer-2" onClose={onClose2} title="Drawer Two">
          <p>Content Two</p>
        </Drawer>
      </>,
    );

    // Both drawers should be present.
    const dialogs = screen.getAllByRole('dialog');
    expect(dialogs.length).toBe(2);

    expect(screen.getByText('Content One')).toBeTruthy();
    expect(screen.getByText('Content Two')).toBeTruthy();
  });

  it('closing one drawer does not affect the other', async () => {
    const onClose1 = vi.fn();
    const onClose2 = vi.fn();

    render(
      <>
        <Drawer isVisible id="drawer-1" onClose={onClose1} title="Drawer One">
          <p>Content One</p>
        </Drawer>
        <Drawer isVisible id="drawer-2" onClose={onClose2} title="Drawer Two">
          <p>Content Two</p>
        </Drawer>
      </>,
    );

    // Click the close button of the first drawer.
    const panel1 = document.getElementById('drawer-1') as HTMLElement;
    expect(panel1).toBeTruthy();

    const closeButton1 = within(panel1).getByTestId('drawer-close-button');
    const user = userEvent.setup();
    await user.click(closeButton1);

    // Only the first drawer's onClose should have been called.
    expect(onClose1).toHaveBeenCalledTimes(1);
    expect(onClose2).not.toHaveBeenCalled();
  });
});

/* ========================================================================== */
/*  2.12 — Body Scroll Lock (additional behavioural coverage)                 */
/* ========================================================================== */

describe('Drawer — Body Scroll Lock', () => {
  it('sets body overflow to hidden when visible', () => {
    renderDrawer({ isVisible: true });

    // The component's useEffect sets document.body.style.overflow = 'hidden'.
    expect(document.body.style.overflow).toBe('hidden');
  });

  it('restores body overflow when drawer closes', () => {
    document.body.style.overflow = 'auto';

    const { rerender } = render(<Drawer isVisible onClose={vi.fn()} />);
    expect(document.body.style.overflow).toBe('hidden');

    rerender(<Drawer isVisible={false} onClose={vi.fn()} />);
    expect(document.body.style.overflow).toBe('auto');
  });

  it('restores body overflow on unmount', () => {
    document.body.style.overflow = 'scroll';

    const { unmount } = render(<Drawer isVisible onClose={vi.fn()} />);
    expect(document.body.style.overflow).toBe('hidden');

    unmount();
    expect(document.body.style.overflow).toBe('scroll');
  });
});

/* ========================================================================== */
/*  2.13 — onClose Callback Robustness                                        */
/* ========================================================================== */

describe('Drawer — onClose Callback Robustness', () => {
  it('does not throw when onClose is undefined and backdrop is clicked', () => {
    // onClose is optional — the component uses optional chaining `onClose?.()`.
    render(
      <Drawer isVisible>
        <p>Content</p>
      </Drawer>,
    );

    const backdrop = screen.getByTestId('drawer-backdrop');
    expect(() => fireEvent.click(backdrop)).not.toThrow();
  });

  it('does not throw when onClose is undefined and Escape is pressed', () => {
    render(
      <Drawer isVisible>
        <p>Content</p>
      </Drawer>,
    );

    expect(() => fireEvent.keyDown(document, { key: 'Escape' })).not.toThrow();
  });

  it('does not throw when onClose is undefined and close button is clicked', () => {
    render(
      <Drawer isVisible title="Test">
        <p>Content</p>
      </Drawer>,
    );

    const closeButton = screen.getByTestId('drawer-close-button');
    expect(() => fireEvent.click(closeButton)).not.toThrow();
  });
});
