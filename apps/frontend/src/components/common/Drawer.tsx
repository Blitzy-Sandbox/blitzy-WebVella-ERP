import { useEffect, useCallback, type ReactNode, type MouseEvent } from 'react';
import { createPortal } from 'react-dom';

/**
 * Scoped keyframe styles for the drawer slide-in animation and backdrop fade.
 * Wrapped in `@media (prefers-reduced-motion: no-preference)` so users who
 * prefer reduced motion see an instant appearance instead of an animation.
 * Replaces the monolith's jQuery DOM manipulation (OpenDrawer adding .d-block).
 */
const DRAWER_STYLES = `
@media (prefers-reduced-motion: no-preference) {
  @keyframes wv-drawer-slide-in {
    from { transform: translateX(100%); }
    to   { transform: translateX(0); }
  }
  @keyframes wv-drawer-backdrop-fade {
    from { opacity: 0; }
    to   { opacity: 1; }
  }
  .wv-drawer-panel {
    animation: wv-drawer-slide-in 300ms ease-out both;
  }
  .wv-drawer-backdrop {
    animation: wv-drawer-backdrop-fade 200ms ease-out both;
  }
}
`;

/* -------------------------------------------------------------------------- */
/*  DrawerProps — maps from C# PcDrawerOptions & WvDrawer TagHelper attrs     */
/* -------------------------------------------------------------------------- */

/**
 * Props interface for the Drawer component.
 *
 * Source mapping:
 * - PcDrawer.cs  → PcDrawerOptions (lines 24-43)
 * - WvDrawer.cs  → HtmlAttributeName tag-helper attributes
 * - drawer.js    → open/close/backdrop jQuery behaviour
 */
export interface DrawerProps {
  /**
   * Controls drawer visibility. Default: `false`.
   * Source: PcDrawerOptions.IsVisible / WvDrawer.cs `is-visible` attribute.
   */
  isVisible?: boolean;

  /**
   * CSS width string applied via inline style. Default: `"550px"`.
   * Source: PcDrawerOptions.Width (default `"550px"`), WvDrawer.cs `width`.
   */
  width?: string;

  /**
   * Drawer header title. When set, the header section (close button + title
   * text + optional action area) is rendered.
   * Source: PcDrawerOptions.Title / WvDrawer.cs `title`.
   */
  title?: string;

  /**
   * React node rendered in the header action area (right-side of the title).
   * Replaces WvDrawer.cs `title-action-html` raw-HTML string attribute
   * with a type-safe ReactNode.
   */
  titleAction?: ReactNode;

  /**
   * Additional CSS classes on the outer drawer container.
   * Source: PcDrawerOptions.Class / WvDrawer.cs `class`.
   */
  className?: string;

  /**
   * CSS classes on the inner scrollable content wrapper.
   * Source: PcDrawerOptions.BodyClass / WvDrawer.cs `body-class`.
   */
  bodyClassName?: string;

  /**
   * Callback invoked when the drawer should close.
   * Triggered by: Escape key, backdrop click, or header close button.
   * Replaces the monolith's `CloseDrawer()` jQuery function (drawer.js).
   */
  onClose?: () => void;

  /**
   * Drawer body content.
   * Replaces Display.cshtml child-node iteration via `Component.InvokeAsync`.
   */
  children?: ReactNode;

  /**
   * HTML `id` attribute for the drawer panel element.
   * Source: WvDrawer.cs builds the id as `"wv-{node.Id}"`.
   */
  id?: string;
}

/* -------------------------------------------------------------------------- */
/*  Drawer component                                                          */
/* -------------------------------------------------------------------------- */

/**
 * Side drawer panel component.
 *
 * Replaces:
 * 1. `PcDrawer/` ViewComponent  (PcDrawer.cs + Display.cshtml)
 * 2. `WvDrawer` TagHelper       (WvDrawer.cs)
 * 3. `drawer.js`                 (jQuery open/close/backdrop management)
 *
 * Renders a right-sliding panel with a semi-transparent backdrop, an optional
 * header (close button + title + action area), and a scrollable content area.
 * The entire overlay is portal-rendered into `document.body` to guarantee
 * correct z-index stacking and avoid CSS overflow clipping.
 */
export default function Drawer({
  isVisible = false,
  width = '550px',
  title,
  titleAction,
  className,
  bodyClassName,
  onClose,
  children,
  id,
}: DrawerProps) {
  /* ---- Body scroll lock ------------------------------------------------- */
  // Prevents background scrolling while the drawer is open.
  // Mirrors drawer.js `OpenDrawer` setting position:relative on body wrappers.
  useEffect(() => {
    if (!isVisible) return;

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      document.body.style.overflow = previousOverflow;
    };
  }, [isVisible]);

  /* ---- Escape-key handler ----------------------------------------------- */
  // Replaces ErpEvent.ON('WebVella.Erp.Web.Components.PcDrawer') from drawer.js
  // which dispatched close/hide actions through the monolith's event bus.
  useEffect(() => {
    if (!isVisible) return;

    const handleEscapeKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        onClose?.();
      }
    };

    document.addEventListener('keydown', handleEscapeKey);
    return () => {
      document.removeEventListener('keydown', handleEscapeKey);
    };
  }, [isVisible, onClose]);

  /* ---- Backdrop click handler ------------------------------------------- */
  // Replaces jQuery delegated click on `.drawer-backdrop` (drawer.js lines 76-81).
  const handleBackdropClick = useCallback(
    (event: MouseEvent<HTMLDivElement>): void => {
      event.preventDefault();
      event.stopPropagation();
      onClose?.();
    },
    [onClose],
  );

  /* ---- Visibility gate -------------------------------------------------- */
  if (!isVisible) {
    return null;
  }

  /* ---- Portal render ---------------------------------------------------- */
  return createPortal(
    <>
      {/* Scoped animation keyframes */}
      {/* eslint-disable-next-line react/no-danger */}
      <style dangerouslySetInnerHTML={{ __html: DRAWER_STYLES }} />

      {/* Backdrop overlay — semi-transparent click-to-close */}
      <div
        className="wv-drawer-backdrop fixed inset-0 bg-black/50 z-40"
        onClick={handleBackdropClick}
        aria-hidden="true"
        data-testid="drawer-backdrop"
      />

      {/* Drawer panel — right-sliding side panel */}
      <div
        id={id}
        className={[
          'wv-drawer-panel',
          'fixed top-0 right-0 h-full bg-white shadow-xl z-50',
          'flex flex-col',
          className ?? '',
        ]
          .filter(Boolean)
          .join(' ')}
        style={{ width }}
        role="dialog"
        aria-modal="true"
        aria-label={title || 'Drawer'}
        data-testid="drawer-panel"
      >
        {/* ---- Conditional header ---------------------------------------- */}
        {/* Only rendered when `title` is provided, matching WvDrawer.cs
            ProcessAsync which conditionally builds the drawer-header div. */}
        {title != null && title !== '' && (
          <div className="flex items-center gap-3 px-4 py-3 border-b border-gray-200 shrink-0">
            {/* Close button — replaces drawer-close onclick ErpEvent.DISPATCH */}
            <button
              type="button"
              onClick={() => onClose?.()}
              className={[
                'inline-flex items-center justify-center rounded',
                'text-gray-500',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
              ].join(' ')}
              style={{ minWidth: '2.75rem', minHeight: '2.75rem' }}
              aria-label="Close drawer"
              data-testid="drawer-close-button"
            >
              {/* Inline SVG × icon — replaces FontAwesome fa fa-times fa-fw.
                  Uses fill="currentColor" so the color inherits from text-gray-500
                  and any hover/focus colour overrides (UI7 guideline). */}
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-5 w-5"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                  clipRule="evenodd"
                />
              </svg>
            </button>

            {/* Title text */}
            <span className="font-semibold text-lg flex-1 truncate">
              {title}
            </span>

            {/* Optional title action area — right-side of header */}
            {titleAction != null && (
              <div className="ml-auto shrink-0">{titleAction}</div>
            )}
          </div>
        )}

        {/* ---- Scrollable content area ----------------------------------- */}
        <div
          className={[
            'flex-1 overflow-y-auto p-4',
            bodyClassName ?? '',
          ]
            .filter(Boolean)
            .join(' ')}
          data-testid="drawer-content"
        >
          {children}
        </div>
      </div>
    </>,
    document.body,
  );
}
