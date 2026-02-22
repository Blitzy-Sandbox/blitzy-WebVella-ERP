import { useEffect } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';

// ---------------------------------------------------------------------------
// Enums — ModalPosition and ModalSize
// ---------------------------------------------------------------------------

/**
 * Vertical position of the modal dialog within the viewport.
 * Maps from C# WvModalPosition enum used by PcModalOptions.Position.
 */
export enum ModalPosition {
  /** Dialog appears near the top of the viewport (default). */
  Top = 'top',
  /** Dialog is vertically centered in the viewport. */
  Center = 'center',
}

/**
 * Width variant for the modal dialog.
 * Maps from C# WvModalSize enum used by PcModalOptions.Size.
 */
export enum ModalSize {
  /** Default width — approx 500px (Bootstrap default). */
  Normal = 'normal',
  /** Narrow width — approx 300px. */
  Small = 'sm',
  /** Wide width — approx 896px. */
  Large = 'lg',
  /** Extra-wide width — approx 1152px. */
  ExtraLarge = 'xl',
  /** Near-full viewport width with a small margin. */
  Full = 'full',
}

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the Modal component.
 *
 * Maps every property from the monolith's PcModalOptions (PcModal.cs lines 25-45)
 * plus React-specific callback and content props.
 */
export interface ModalProps {
  /**
   * Controls whether the modal is visible.
   * Source: PcModalOptions.IsVisible (resolved from data-source string to boolean).
   * @default false
   */
  isVisible?: boolean;

  /**
   * HTML `id` attribute for the dialog container.
   * Source: PcModalOptions.Id (defaults to `"wv-" + node.Id` in PcModal.cs).
   */
  id?: string;

  /**
   * Title rendered in the modal header.
   * Source: PcModalOptions.Title (resolved via ViewBag.ProcessedTitle).
   * When omitted or empty the header section (including close button) is hidden.
   */
  title?: string;

  /**
   * Vertical position of the dialog within the viewport.
   * Source: PcModalOptions.Position (WvModalPosition, default Top).
   * @default ModalPosition.Top
   */
  position?: ModalPosition;

  /**
   * Size variant controlling the dialog's max-width.
   * Source: PcModalOptions.Size (WvModalSize, default Normal).
   * @default ModalSize.Normal
   */
  size?: ModalSize;

  /**
   * Backdrop behaviour.
   *
   * - `true` (default): A semi-transparent backdrop is shown; clicking it calls `onClose`.
   * - `false`: No backdrop is rendered.
   * - `'static'`: Backdrop is shown but clicking it does **not** close the modal
   *    (matches Bootstrap `data-backdrop="static"` behaviour).
   *
   * Source: PcModalOptions.Backdrop (string, default `"true"`).
   * @default true
   */
  backdrop?: boolean | 'static';

  /**
   * Callback invoked when the modal should close.
   * Triggered by: Escape key, backdrop click (non-static), or close button click.
   * Replaces Bootstrap modal JS `data-dismiss="modal"` behaviour.
   */
  onClose?: () => void;

  /**
   * Modal body content.
   *
   * In the monolith, body nodes are rendered from child nodes whose
   * `ContainerId === "body"` via `Component.InvokeAsync`.
   * In React the content is passed directly as children.
   */
  children?: ReactNode;

  /**
   * Optional footer content rendered below the body, separated by a border.
   *
   * In the monolith, footer nodes are rendered from child nodes whose
   * `ContainerId === "footer"` via `Component.InvokeAsync`.
   */
  footer?: ReactNode;
}

// ---------------------------------------------------------------------------
// Size class mapping — Tailwind max-width utilities per ModalSize variant
// ---------------------------------------------------------------------------

const SIZE_CLASSES: Record<ModalSize, string> = {
  [ModalSize.Normal]: 'max-w-lg',
  [ModalSize.Small]: 'max-w-sm',
  [ModalSize.Large]: 'max-w-4xl',
  [ModalSize.ExtraLarge]: 'max-w-6xl',
  [ModalSize.Full]: 'max-w-full mx-4',
};

// ---------------------------------------------------------------------------
// Modal Component
// ---------------------------------------------------------------------------

/**
 * Portal-rendered modal dialog with configurable position, size, title,
 * backdrop behaviour and body / footer content.
 *
 * Replaces:
 * - `PcModal/` ViewComponent (PcModal.cs + Display.cshtml)
 * - `<wv-modal>` TagHelper (WvModal.cs)
 *
 * Key behaviours:
 * - Rendered into `document.body` via `createPortal` to prevent CSS overflow
 *   clipping and ensure correct z-index stacking.
 * - Escape key closes the modal (keyboard accessible).
 * - Body scroll is locked while the modal is visible.
 * - Backdrop click-to-close respects `backdrop` prop (`true` / `false` / `'static'`).
 *
 * @example
 * ```tsx
 * <Modal
 *   isVisible={open}
 *   title="Confirm Action"
 *   size={ModalSize.Normal}
 *   onClose={() => setOpen(false)}
 *   footer={<button onClick={() => setOpen(false)}>OK</button>}
 * >
 *   <p>Are you sure you want to proceed?</p>
 * </Modal>
 * ```
 */
export default function Modal({
  isVisible = false,
  id,
  title,
  position = ModalPosition.Top,
  size = ModalSize.Normal,
  backdrop = true,
  onClose,
  children,
  footer,
}: ModalProps) {
  // ---------------------------------------------------------------------------
  // Effect: Keyboard — Escape to close
  // Replaces Bootstrap `data-dismiss="modal"` keyboard plugin.
  // ---------------------------------------------------------------------------
  useEffect(() => {
    if (!isVisible) return;

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose?.();
      }
    };

    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [isVisible, onClose]);

  // ---------------------------------------------------------------------------
  // Effect: Body scroll lock
  // Prevents page scrolling while the modal overlay is visible.
  // ---------------------------------------------------------------------------
  useEffect(() => {
    if (!isVisible) return;

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [isVisible]);

  // ---------------------------------------------------------------------------
  // Early exit — do not render anything when the modal is not visible
  // ---------------------------------------------------------------------------
  if (!isVisible) {
    return null;
  }

  // ---------------------------------------------------------------------------
  // Backdrop click handler
  // ---------------------------------------------------------------------------
  const handleBackdropClick = (): void => {
    if (backdrop !== 'static') {
      onClose?.();
    }
  };

  // ---------------------------------------------------------------------------
  // Position classes
  // ---------------------------------------------------------------------------
  const positionClasses =
    position === ModalPosition.Center
      ? 'items-center'
      : 'items-start pt-12';

  // Derive the `aria-labelledby` value only when a title and id are present
  const titleId = id ? `${id}-title` : undefined;
  const ariaLabelledBy = title && titleId ? titleId : undefined;

  // ---------------------------------------------------------------------------
  // Render — portal to document.body
  // ---------------------------------------------------------------------------
  return createPortal(
    <div className="fixed inset-0 z-50 overflow-y-auto">
      {/* Backdrop overlay */}
      {backdrop !== false && (
        <div
          className="fixed inset-0 bg-black/50"
          aria-hidden="true"
          onClick={handleBackdropClick}
        />
      )}

      {/* Dialog positioning container */}
      <div
        className={`flex min-h-full justify-center p-4 ${positionClasses}`}
      >
        {/* Dialog panel */}
        <div
          id={id}
          role="dialog"
          aria-modal="true"
          aria-labelledby={ariaLabelledBy}
          className={`relative w-full rounded-lg bg-white shadow-xl ${SIZE_CLASSES[size]}`}
        >
          {/* Header — rendered only when a title is provided */}
          {title && (
            <div className="flex items-center justify-between border-b border-gray-200 px-6 py-4">
              <h5
                id={titleId}
                className="text-lg font-semibold text-gray-900"
              >
                {title}
              </h5>
              <button
                type="button"
                onClick={onClose}
                aria-label="Close modal"
                className="text-2xl leading-none text-gray-400 transition-colors hover:text-gray-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
              >
                &times;
              </button>
            </div>
          )}

          {/* Body */}
          <div className="px-6 py-4">{children}</div>

          {/* Footer — rendered only when footer content is provided */}
          {footer && (
            <div className="flex justify-end gap-2 border-t border-gray-200 px-6 py-4">
              {footer}
            </div>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}
