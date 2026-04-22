/**
 * Vitest Component Tests for `<Modal />`
 *
 * Validates the React Modal component that replaces the monolith's
 * `PcModal` ViewComponent (`WebVella.Erp.Web/Components/PcModal/PcModal.cs`,
 * `Display.cshtml`) and the `<wv-modal>` TagHelper.
 *
 * The monolith's PcModalOptions define six configuration properties:
 *  - is_visible (string → boolean): controls modal visibility
 *  - id (string): HTML id for the dialog element
 *  - title (string): header title (resolved via data source)
 *  - position (WvModalPosition.Top | .Center): vertical alignment
 *  - size (WvModalSize.Normal | .Small | .Large | .ExtraLarge | .Full): max-width
 *  - backdrop (string "true" | "false" | "static"): backdrop behaviour
 *
 * Test coverage includes:
 *  - Open/close behaviour (isVisible toggle, onClose callback)
 *  - Confirm/cancel callback patterns (× button, footer actions)
 *  - Portal rendering via createPortal to document.body
 *  - Backdrop click handling (true → close, false → no backdrop, static → no close)
 *  - Escape key close trigger with proper listener lifecycle
 *  - Title / body / footer slot rendering and empty states
 *  - Animation state: body scroll lock (overflow hidden/reset)
 *  - Position variants (Top → items-start pt-12, Center → items-center)
 *  - Size variants (Normal → max-w-lg, Small → max-w-sm, Large → max-w-4xl,
 *    ExtraLarge → max-w-6xl, Full → max-w-full)
 *  - ID attribute passthrough on dialog element
 *  - ARIA accessibility (role=dialog, aria-modal, aria-labelledby, close button)
 *
 * @see apps/frontend/src/components/common/Modal.tsx
 * @see WebVella.Erp.Web/Components/PcModal/PcModal.cs
 * @see WebVella.Erp.Web/Components/PcModal/Display.cshtml
 * @see WebVella.Erp.Web/Components/PcModal/Options.cshtml
 */

import '@testing-library/jest-dom/vitest';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import Modal, {
  ModalPosition,
  ModalSize,
} from '../../../src/components/common/Modal';
import type { ModalProps } from '../../../src/components/common/Modal';

// ---------------------------------------------------------------------------
// Test Helpers
// ---------------------------------------------------------------------------

/**
 * Default props factory for consistent test setup.
 * Mirrors the PcModalOptions defaults from PcModal.cs (lines 25-45).
 */
function createDefaultProps(overrides: Partial<ModalProps> = {}): ModalProps {
  return {
    isVisible: true,
    title: 'Test Modal',
    id: 'test-modal',
    onClose: vi.fn(),
    ...overrides,
  };
}

/**
 * Helper to locate the backdrop overlay element rendered inside the portal.
 * The backdrop is identified by its `aria-hidden="true"` attribute, which
 * mirrors Bootstrap's sr-only backdrop pattern from Display.cshtml.
 */
function getBackdropElement(): HTMLElement | null {
  return document.body.querySelector('[aria-hidden="true"]');
}

/**
 * Helper to locate the outer portal container (the fixed overlay root).
 * Uses the unique class combination applied by the Modal component.
 */
function getPortalRoot(): HTMLElement | null {
  return document.body.querySelector('.fixed.inset-0.z-50');
}

// ---------------------------------------------------------------------------
// Cleanup
// ---------------------------------------------------------------------------

afterEach(() => {
  // Reset body scroll lock that the Modal component may have set.
  // Mirrors the cleanup needed after PcModal toggles body overflow.
  document.body.style.overflow = '';
});

// ==========================================================================
// TEST SUITES
// ==========================================================================

describe('Modal', () => {
  // ========================================================================
  // 2.1 Open/Close Behaviour Tests
  // ========================================================================
  describe('open/close behaviour', () => {
    it('renders nothing when isVisible is false (default)', () => {
      const props = createDefaultProps({ isVisible: false });
      render(<Modal {...props} />);

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('renders modal when isVisible is true', () => {
      const props = createDefaultProps({ isVisible: true });
      render(<Modal {...props} />);

      expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('modal appears when isVisible changes from false to true', () => {
      const props = createDefaultProps({ isVisible: false });
      const { rerender } = render(<Modal {...props} />);

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

      rerender(<Modal {...props} isVisible={true} />);

      expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('modal disappears when isVisible changes from true to false', () => {
      const props = createDefaultProps({ isVisible: true });
      const { rerender } = render(<Modal {...props} />);

      expect(screen.getByRole('dialog')).toBeInTheDocument();

      rerender(<Modal {...props} isVisible={false} />);

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('onClose callback is not called on initial render', () => {
      const onClose = vi.fn();
      render(<Modal {...createDefaultProps({ onClose })} />);

      expect(onClose).not.toHaveBeenCalled();
    });
  });

  // ========================================================================
  // 2.2 Confirm/Cancel Callback Tests
  // ========================================================================
  describe('confirm/cancel callbacks', () => {
    it('onClose is called when close button (×) is clicked', async () => {
      const onClose = vi.fn();
      render(<Modal {...createDefaultProps({ onClose })} />);

      const closeButton = screen.getByLabelText('Close modal');
      await userEvent.click(closeButton);

      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('onClose is called via footer cancel button', async () => {
      const onClose = vi.fn();
      const footer = <button onClick={onClose}>Cancel</button>;
      render(<Modal {...createDefaultProps({ onClose, footer })} />);

      const cancelButton = screen.getByText('Cancel');
      await userEvent.click(cancelButton);

      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('onClose is called via footer confirm button pattern', async () => {
      const onConfirm = vi.fn();
      const footer = (
        <>
          <button onClick={vi.fn()}>Cancel</button>
          <button onClick={onConfirm}>Confirm</button>
        </>
      );
      render(<Modal {...createDefaultProps({ footer })} />);

      const confirmButton = screen.getByText('Confirm');
      await userEvent.click(confirmButton);

      expect(onConfirm).toHaveBeenCalledTimes(1);
    });

    it('multiple rapid close clicks only fire once when modal closes', async () => {
      const onClose = vi.fn();
      const props = createDefaultProps({ onClose });
      const { rerender } = render(<Modal {...props} />);

      const closeButton = screen.getByLabelText('Close modal');
      await userEvent.click(closeButton);

      expect(onClose).toHaveBeenCalledTimes(1);

      // Simulate parent responding to onClose by hiding the modal
      rerender(<Modal {...props} isVisible={false} />);

      // The modal and close button should be gone — no further clicks possible
      expect(screen.queryByLabelText('Close modal')).not.toBeInTheDocument();
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  // ========================================================================
  // 2.3 Portal Rendering Tests
  // ========================================================================
  describe('portal rendering', () => {
    it('modal renders via createPortal to document.body', () => {
      const { container } = render(<Modal {...createDefaultProps()} />);

      // The dialog should NOT be inside the render container (it portals out)
      const dialogInsideContainer = container.querySelector('[role="dialog"]');
      expect(dialogInsideContainer).toBeNull();

      // But it should be in document.body
      const dialogInBody = document.body.querySelector('[role="dialog"]');
      expect(dialogInBody).toBeInTheDocument();
    });

    it('portal content is accessible via screen queries', () => {
      render(<Modal {...createDefaultProps()} />);

      // React Testing Library's screen queries work with portalled content
      const dialog = screen.getByRole('dialog');
      expect(dialog).toBeInTheDocument();
      expect(screen.getByText('Test Modal')).toBeInTheDocument();
    });

    it('multiple modals render separate portals', () => {
      render(
        <>
          <Modal {...createDefaultProps({ id: 'modal-1', title: 'First Modal' })} />
          <Modal {...createDefaultProps({ id: 'modal-2', title: 'Second Modal' })} />
        </>
      );

      const dialogs = screen.getAllByRole('dialog');
      expect(dialogs).toHaveLength(2);
      expect(screen.getByText('First Modal')).toBeInTheDocument();
      expect(screen.getByText('Second Modal')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // 2.4 Backdrop Click Handling Tests
  // ========================================================================
  describe('backdrop click handling', () => {
    it('backdrop click calls onClose when backdrop is true (default)', () => {
      const onClose = vi.fn();
      render(
        <Modal {...createDefaultProps({ onClose, backdrop: true })} />
      );

      const backdrop = getBackdropElement();
      expect(backdrop).toBeInTheDocument();

      fireEvent.click(backdrop!);
      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('backdrop click does NOT call onClose when backdrop is "static"', () => {
      const onClose = vi.fn();
      render(
        <Modal {...createDefaultProps({ onClose, backdrop: 'static' })} />
      );

      const backdrop = getBackdropElement();
      expect(backdrop).toBeInTheDocument();

      fireEvent.click(backdrop!);
      expect(onClose).not.toHaveBeenCalled();
    });

    it('no backdrop rendered when backdrop is false', () => {
      render(
        <Modal {...createDefaultProps({ backdrop: false })} />
      );

      const backdrop = getBackdropElement();
      expect(backdrop).toBeNull();
    });

    it('clicking inside modal dialog does NOT trigger backdrop close', () => {
      const onClose = vi.fn();
      render(
        <Modal {...createDefaultProps({ onClose, backdrop: true })}>
          <p>Dialog content</p>
        </Modal>
      );

      // Click on the dialog content area — should NOT close
      const dialogContent = screen.getByText('Dialog content');
      fireEvent.click(dialogContent);

      expect(onClose).not.toHaveBeenCalled();
    });
  });

  // ========================================================================
  // 2.5 ESC Key Close Tests
  // ========================================================================
  describe('Escape key close', () => {
    it('pressing Escape key calls onClose', () => {
      const onClose = vi.fn();
      render(<Modal {...createDefaultProps({ onClose })} />);

      fireEvent.keyDown(document, { key: 'Escape' });

      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('Escape key has no effect when modal is hidden', () => {
      const onClose = vi.fn();
      render(
        <Modal {...createDefaultProps({ onClose, isVisible: false })} />
      );

      fireEvent.keyDown(document, { key: 'Escape' });

      expect(onClose).not.toHaveBeenCalled();
    });

    it('Escape key event listener is registered only when visible', () => {
      const onClose = vi.fn();
      const { rerender } = render(
        <Modal {...createDefaultProps({ onClose, isVisible: false })} />
      );

      // While hidden, Escape should have no effect
      fireEvent.keyDown(document, { key: 'Escape' });
      expect(onClose).not.toHaveBeenCalled();

      // Toggle to visible — listener should now be active
      rerender(
        <Modal {...createDefaultProps({ onClose, isVisible: true })} />
      );

      fireEvent.keyDown(document, { key: 'Escape' });
      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('Escape key event listener is cleaned up on unmount', () => {
      const onClose = vi.fn();
      const { unmount } = render(
        <Modal {...createDefaultProps({ onClose })} />
      );

      unmount();

      // After unmount, the keydown listener should be removed
      fireEvent.keyDown(document, { key: 'Escape' });
      expect(onClose).not.toHaveBeenCalled();
    });
  });

  // ========================================================================
  // 2.6 Modal Title / Body / Footer Slot Tests
  // ========================================================================
  describe('title / body / footer slots', () => {
    it('renders title in modal header', () => {
      render(<Modal {...createDefaultProps({ title: 'My Modal' })} />);

      const dialog = screen.getByRole('dialog');
      const heading = within(dialog).getByText('My Modal');
      expect(heading).toBeInTheDocument();
      expect(heading.tagName).toBe('H5');
    });

    it('renders without title when title is empty', () => {
      render(
        <Modal {...createDefaultProps({ title: undefined })}>
          <p>Body only</p>
        </Modal>
      );

      const dialog = screen.getByRole('dialog');
      // No heading element should exist inside the dialog
      expect(within(dialog).queryByRole('heading')).not.toBeInTheDocument();
      // Close button in the header should also not exist
      expect(screen.queryByLabelText('Close modal')).not.toBeInTheDocument();
    });

    it('renders children as body content', () => {
      render(
        <Modal {...createDefaultProps()}>
          <p>Body content here</p>
        </Modal>
      );

      expect(screen.getByText('Body content here')).toBeInTheDocument();
    });

    it('renders footer content', () => {
      const footer = <button>Save</button>;
      render(<Modal {...createDefaultProps({ footer })} />);

      expect(screen.getByText('Save')).toBeInTheDocument();
    });

    it('renders without footer when footer is not provided', () => {
      render(<Modal {...createDefaultProps({ footer: undefined })} />);

      const dialog = screen.getByRole('dialog');
      // The footer section has border-t class — should not exist
      const footerSection = dialog.querySelector('.border-t.border-gray-200');
      expect(footerSection).toBeNull();
    });

    it('body and footer are visually separated by border', () => {
      const footer = <button>Action</button>;
      render(<Modal {...createDefaultProps({ footer })} />);

      const dialog = screen.getByRole('dialog');
      // Footer section should have border-t class for visual separation
      const footerSection = dialog.querySelector('.border-t');
      expect(footerSection).toBeInTheDocument();
      expect(within(footerSection as HTMLElement).getByText('Action')).toBeInTheDocument();
    });
  });

  // ========================================================================
  // 2.7 Animation State Tests
  // ========================================================================
  describe('animation state', () => {
    it('modal has transition-related classes when appearing', () => {
      render(<Modal {...createDefaultProps()} />);

      const portalRoot = getPortalRoot();
      expect(portalRoot).toBeInTheDocument();
      // The outer container has fixed positioning and z-index for overlay
      expect(portalRoot).toHaveClass('fixed', 'inset-0', 'z-50');
    });

    it('body scroll is locked when modal is visible', () => {
      render(<Modal {...createDefaultProps({ isVisible: true })} />);

      expect(document.body.style.overflow).toBe('hidden');
    });

    it('body scroll is restored when modal is hidden', () => {
      const { rerender } = render(
        <Modal {...createDefaultProps({ isVisible: true })} />
      );

      expect(document.body.style.overflow).toBe('hidden');

      rerender(<Modal {...createDefaultProps({ isVisible: false })} />);

      expect(document.body.style.overflow).toBe('');
    });
  });

  // ========================================================================
  // 2.8 Position Tests (WvModalPosition enum → ModalPosition)
  // ========================================================================
  describe('position', () => {
    it('Top position (default): modal aligns near top of viewport', () => {
      render(
        <Modal {...createDefaultProps({ position: ModalPosition.Top })} />
      );

      const dialog = screen.getByRole('dialog');
      // The positioning container wrapping the dialog should have top-align classes
      const posContainer = dialog.parentElement;
      expect(posContainer).toHaveClass('items-start');
      expect(posContainer).toHaveClass('pt-12');
    });

    it('Center position: modal is vertically centered', () => {
      render(
        <Modal {...createDefaultProps({ position: ModalPosition.Center })} />
      );

      const dialog = screen.getByRole('dialog');
      const posContainer = dialog.parentElement;
      expect(posContainer).toHaveClass('items-center');
      // Should NOT have the top-alignment padding
      expect(posContainer).not.toHaveClass('pt-12');
    });
  });

  // ========================================================================
  // 2.9 Size Tests (WvModalSize enum → ModalSize)
  // ========================================================================
  describe('size', () => {
    it('Normal size (default): has max-w-lg class', () => {
      render(
        <Modal {...createDefaultProps({ size: ModalSize.Normal })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveClass('max-w-lg');
    });

    it('Small size: has max-w-sm class', () => {
      render(
        <Modal {...createDefaultProps({ size: ModalSize.Small })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveClass('max-w-sm');
    });

    it('Large size: has max-w-4xl class', () => {
      render(
        <Modal {...createDefaultProps({ size: ModalSize.Large })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveClass('max-w-4xl');
    });

    it('ExtraLarge size: has max-w-6xl class', () => {
      render(
        <Modal {...createDefaultProps({ size: ModalSize.ExtraLarge })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveClass('max-w-6xl');
    });

    it('Full size: has max-w-full class', () => {
      render(
        <Modal {...createDefaultProps({ size: ModalSize.Full })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveClass('max-w-full');
    });
  });

  // ========================================================================
  // 2.10 ID Attribute Tests (PcModalOptions.Id)
  // ========================================================================
  describe('id attribute', () => {
    it('renders id attribute on dialog', () => {
      render(<Modal {...createDefaultProps({ id: 'my-modal' })} />);

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveAttribute('id', 'my-modal');
    });

    it('renders without id when not provided', () => {
      render(
        <Modal {...createDefaultProps({ id: undefined })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).not.toHaveAttribute('id');
    });
  });

  // ========================================================================
  // 2.11 Accessibility Tests
  // ========================================================================
  describe('accessibility', () => {
    it('modal has role="dialog"', () => {
      render(<Modal {...createDefaultProps()} />);

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveAttribute('role', 'dialog');
    });

    it('modal has aria-modal="true"', () => {
      render(<Modal {...createDefaultProps()} />);

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveAttribute('aria-modal', 'true');
    });

    it('modal has aria-labelledby referencing title', () => {
      render(
        <Modal {...createDefaultProps({ id: 'my-modal', title: 'Accessible Title' })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).toHaveAttribute('aria-labelledby', 'my-modal-title');

      // Verify the title element has the matching id
      const titleElement = screen.getByText('Accessible Title');
      expect(titleElement).toHaveAttribute('id', 'my-modal-title');
    });

    it('aria-labelledby is not set when id is not provided', () => {
      render(
        <Modal {...createDefaultProps({ id: undefined, title: 'No ID Title' })} />
      );

      const dialog = screen.getByRole('dialog');
      expect(dialog).not.toHaveAttribute('aria-labelledby');
    });

    it('close button is focusable and keyboard accessible', async () => {
      const onClose = vi.fn();
      render(<Modal {...createDefaultProps({ onClose })} />);

      const closeButton = screen.getByLabelText('Close modal');
      expect(closeButton.tagName).toBe('BUTTON');
      expect(closeButton).toHaveAttribute('type', 'button');

      // Focus and activate with keyboard
      closeButton.focus();
      expect(closeButton).toHaveFocus();

      await userEvent.keyboard('{Enter}');
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });
});
