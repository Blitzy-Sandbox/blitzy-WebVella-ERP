/**
 * AppShell — Main Application Layout Component
 *
 * Replaces the monolith's `_AppMaster.cshtml` layout structure (lines 1–30).
 * This is the top-level layout wrapper for all authenticated routes in the
 * React SPA. It renders the complete application chrome:
 *
 *   _AppMaster.cshtml mapping → React equivalent:
 *   ─────────────────────────────────────────────
 *   <vc:nav>              → <TopNav />
 *   <vc:sidebar-menu>     → <Sidebar collapsed onToggle />
 *   @RenderBody()         → <Outlet />
 *   <vc:screen-message>   → Toast notification container
 *
 * Sidebar collapsed state is persisted to `localStorage` so the preference
 * survives page refreshes. Mobile devices (below the `md` breakpoint) show
 * the sidebar as a toggleable overlay with a backdrop.
 *
 * Toast notifications replace the Toastr-based `ScreenMessage` ViewComponent.
 * Any component can trigger a toast by dispatching a custom DOM event:
 *
 *   window.dispatchEvent(new CustomEvent('app-toast', {
 *     detail: { type: 'success', message: 'Record saved!' }
 *   }));
 *
 * @module components/layout/AppShell
 * @source WebVella.Erp.Web/Pages/_AppMaster.cshtml
 * @source WebVella.Erp.Web/Components/ScreenMessage/ScreenMessageViewComponent.cs
 * @source WebVella.Erp.Web/Components/ScreenMessage/Default.cshtml
 */

import { useState, useCallback, useEffect, type ReactElement } from 'react';
import { Outlet } from 'react-router-dom';
import TopNav from './TopNav';
import Sidebar from './Sidebar';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** localStorage key for persisting the sidebar collapsed/expanded preference. */
const SIDEBAR_COLLAPSED_KEY = 'sidebar-collapsed';

/**
 * Custom DOM event name for triggering toast notifications from any component.
 * Decouples toast dispatch from the AppShell component tree without prop drilling.
 */
const TOAST_EVENT_NAME = 'app-toast';

/**
 * Auto-dismiss durations (ms) per toast severity.
 *
 * Matches the monolith's Toastr configuration from
 * `ScreenMessage/Default.cshtml`:
 *   - error:   timeOut 7000, extendedTimeOut 7000
 *   - others:  timeOut 3000, extendedTimeOut 3000
 */
const TOAST_TIMEOUTS: Record<ToastType, number> = {
  error: 7000,
  success: 3000,
  info: 3000,
  warning: 3000,
};

// ---------------------------------------------------------------------------
// Toast Type Definitions
// ---------------------------------------------------------------------------

/**
 * Toast notification severity levels.
 * Maps 1:1 to the monolith's `ScreenMessageType` enum values:
 *   Error, Success, Info, Warning.
 */
type ToastType = 'error' | 'success' | 'info' | 'warning';

/** Individual toast notification data. */
interface Toast {
  /** Unique identifier used as React key and for targeted removal. */
  id: string;
  /** Severity level controlling background color and auto-dismiss duration. */
  type: ToastType;
  /** Optional bold title text rendered above the message body. */
  title: string;
  /** Primary notification message text. */
  message: string;
}

/**
 * Shape of the `detail` payload on the custom `app-toast` DOM event.
 * Consumers dispatch:
 *   `new CustomEvent('app-toast', { detail: { type, title?, message } })`
 */
interface ToastEventDetail {
  type: ToastType;
  title?: string;
  message: string;
}

// ---------------------------------------------------------------------------
// Toast Visual Configuration
// ---------------------------------------------------------------------------

/** Tailwind background + text color classes per toast severity. */
const TOAST_STYLE_CLASSES: Record<ToastType, string> = {
  error: 'bg-red-600 text-white',
  success: 'bg-green-600 text-white',
  info: 'bg-blue-600 text-white',
  warning: 'bg-amber-600 text-white',
};

/** Close-button hover color classes per toast severity. */
const TOAST_CLOSE_CLASSES: Record<ToastType, string> = {
  error: 'text-white/80 hover:text-white',
  success: 'text-white/80 hover:text-white',
  info: 'text-white/80 hover:text-white',
  warning: 'text-white/80 hover:text-white',
};

// ---------------------------------------------------------------------------
// Toast SVG Icons
// ---------------------------------------------------------------------------

/** Inline SVG icon for error toasts (X inside circle). */
function ErrorIcon() {
  return (
    <svg
      className="h-5 w-5 shrink-0"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="10" />
      <line x1="15" y1="9" x2="9" y2="15" />
      <line x1="9" y1="9" x2="15" y2="15" />
    </svg>
  );
}

/** Inline SVG icon for success toasts (checkmark inside circle). */
function SuccessIcon() {
  return (
    <svg
      className="h-5 w-5 shrink-0"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14" />
      <polyline points="22 4 12 14.01 9 11.01" />
    </svg>
  );
}

/** Inline SVG icon for info toasts (i inside circle). */
function InfoIcon() {
  return (
    <svg
      className="h-5 w-5 shrink-0"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <circle cx="12" cy="12" r="10" />
      <line x1="12" y1="16" x2="12" y2="12" />
      <line x1="12" y1="8" x2="12.01" y2="8" />
    </svg>
  );
}

/** Inline SVG icon for warning toasts (exclamation inside triangle). */
function WarningIcon() {
  return (
    <svg
      className="h-5 w-5 shrink-0"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
      <line x1="12" y1="9" x2="12" y2="13" />
      <line x1="12" y1="17" x2="12.01" y2="17" />
    </svg>
  );
}

/** Map of toast type → icon component for rendering. */
const TOAST_ICONS: Record<ToastType, () => ReactElement> = {
  error: ErrorIcon,
  success: SuccessIcon,
  info: InfoIcon,
  warning: WarningIcon,
};

// ---------------------------------------------------------------------------
// AppShell Component
// ---------------------------------------------------------------------------

/**
 * Main application layout component.
 *
 * Renders the complete application chrome:
 * - **Sidebar** (left column): Collapsible navigation, w-64 expanded / w-16
 *   collapsed, hidden below `md` breakpoint (shown as overlay on toggle).
 * - **TopNav** (top of content column): Top navigation bar.
 * - **Main content** (`<Outlet />`): Child route rendering area replacing
 *   `@RenderBody()` from `_AppMaster.cshtml` line 20.
 * - **Toast container** (fixed top-center): Notification messages replacing
 *   `<vc:screen-message>` from `_AppMaster.cshtml` line 28.
 *
 * @returns The application shell layout JSX.
 */
function AppShell() {
  // -------------------------------------------------------------------------
  // Sidebar collapsed / expanded state
  // -------------------------------------------------------------------------

  /**
   * Whether the sidebar is in collapsed (icon-only, w-16) mode.
   * Initialised from localStorage if available, defaulting to expanded (false).
   */
  const [sidebarCollapsed, setSidebarCollapsed] = useState<boolean>(() => {
    try {
      const stored = localStorage.getItem(SIDEBAR_COLLAPSED_KEY);
      return stored === 'true';
    } catch {
      /* localStorage may be unavailable in restricted browsing contexts. */
      return false;
    }
  });

  /** Whether the mobile sidebar overlay is visible (below md breakpoint). */
  const [mobileMenuOpen, setMobileMenuOpen] = useState<boolean>(false);

  /**
   * Toggle desktop sidebar between collapsed and expanded modes.
   * Persists the new state to localStorage for cross-session persistence.
   */
  const toggleSidebar = useCallback(() => {
    setSidebarCollapsed((prev) => {
      const next = !prev;
      try {
        localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(next));
      } catch {
        /* Silently ignore if localStorage is unavailable. */
      }
      return next;
    });
  }, []);

  /** Toggle the mobile sidebar overlay open/closed. */
  const toggleMobileMenu = useCallback(() => {
    setMobileMenuOpen((prev) => !prev);
  }, []);

  /** Close the mobile sidebar overlay (backdrop click / Escape key). */
  const closeMobileMenu = useCallback(() => {
    setMobileMenuOpen(false);
  }, []);

  /**
   * Sync sidebar collapsed state from localStorage on mount.
   * Handles the edge case where another tab modifies the value via
   * the Storage event (though we also initialise in useState above,
   * this effect ensures consistency if the component is remounted).
   */
  useEffect(() => {
    const handleStorageChange = (e: StorageEvent) => {
      if (e.key === SIDEBAR_COLLAPSED_KEY && e.newValue !== null) {
        setSidebarCollapsed(e.newValue === 'true');
      }
    };
    window.addEventListener('storage', handleStorageChange);
    return () => window.removeEventListener('storage', handleStorageChange);
  }, []);

  /** Close mobile overlay on Escape key press. */
  useEffect(() => {
    if (!mobileMenuOpen) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        closeMobileMenu();
      }
    };
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [mobileMenuOpen, closeMobileMenu]);

  // -------------------------------------------------------------------------
  // Toast notification state
  // -------------------------------------------------------------------------

  /** Active toast notifications rendered in the toast container. */
  const [toasts, setToasts] = useState<Toast[]>([]);

  /**
   * Add a new toast notification with auto-dismiss scheduling.
   * Toast ordering follows `newestOnTop: false` — oldest toast stays at
   * the top, newest appends at the bottom (matching the monolith's
   * Toastr configuration from Default.cshtml).
   */
  const addToast = useCallback((detail: ToastEventDetail) => {
    const id = `toast-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
    const type: ToastType = detail.type || 'info';
    const toast: Toast = {
      id,
      type,
      title: detail.title ?? '',
      message: detail.message ?? '',
    };

    /* Append new toast at the end (oldest on top). */
    setToasts((prev) => [...prev, toast]);

    /* Schedule auto-dismiss using the type-specific timeout. */
    const timeout = TOAST_TIMEOUTS[type];
    setTimeout(() => {
      setToasts((prev) => prev.filter((t) => t.id !== id));
    }, timeout);
  }, []);

  /** Remove a toast by its unique ID (manual close button). */
  const removeToast = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id));
  }, []);

  /**
   * Listen for custom `app-toast` events dispatched from any component.
   * This decoupled event-based API allows any part of the application to
   * trigger toast notifications without prop drilling or context coupling.
   */
  useEffect(() => {
    const handler = (e: Event) => {
      const customEvent = e as CustomEvent<ToastEventDetail>;
      if (customEvent.detail) {
        addToast(customEvent.detail);
      }
    };
    window.addEventListener(TOAST_EVENT_NAME, handler);
    return () => window.removeEventListener(TOAST_EVENT_NAME, handler);
  }, [addToast]);

  // -------------------------------------------------------------------------
  // Render
  // -------------------------------------------------------------------------

  return (
    <div className="flex h-screen bg-gray-100">
      {/* ================================================================= */}
      {/* Desktop Sidebar — always visible on md+ screens                   */}
      {/* Replaces <div id="sidebar" class="col-auto"> from _AppMaster     */}
      {/* ================================================================= */}
      <div
        className="hidden md:flex md:shrink-0"
        role="complementary"
        aria-label="Desktop sidebar"
      >
        <Sidebar collapsed={sidebarCollapsed} onToggle={toggleSidebar} />
      </div>

      {/* ================================================================= */}
      {/* Mobile Sidebar Overlay — rendered below md breakpoint on toggle   */}
      {/* ================================================================= */}
      {mobileMenuOpen && (
        <div
          className="fixed inset-0 z-40 flex md:hidden"
          role="dialog"
          aria-modal="true"
          aria-label="Mobile navigation"
        >
          {/* Semi-transparent backdrop — click to close */}
          <button
            type="button"
            className="fixed inset-0 bg-black/50 focus:outline-none"
            onClick={closeMobileMenu}
            aria-label="Close navigation overlay"
            tabIndex={-1}
          />

          {/* Sidebar panel — renders at full expanded width on mobile */}
          <div className="relative z-50 w-64 shrink-0">
            <Sidebar collapsed={false} onToggle={closeMobileMenu} />
          </div>
        </div>
      )}

      {/* ================================================================= */}
      {/* Main Content Column                                               */}
      {/* Replaces <div id="content" class="col"> from _AppMaster          */}
      {/* ================================================================= */}
      <div className="flex flex-1 flex-col overflow-hidden">
        {/* TopNav — replaces <vc:nav page-model="@Model"> */}
        <TopNav />

        {/* Content area — replaces @RenderBody() from _AppMaster line 20 */}
        <main className="flex-1 overflow-auto p-4" id="main-content">
          <Outlet />
        </main>
      </div>

      {/* ================================================================= */}
      {/* Mobile Sidebar Toggle FAB                                         */}
      {/* Visible only below md breakpoint — bottom-right floating button   */}
      {/* ================================================================= */}
      <button
        type="button"
        className={[
          'fixed bottom-4 end-4 z-30 flex h-12 w-12 items-center justify-center',
          'rounded-full bg-gray-800 text-white shadow-lg',
          'md:hidden',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:ring-offset-2',
        ].join(' ')}
        onClick={toggleMobileMenu}
        aria-label={mobileMenuOpen ? 'Close navigation menu' : 'Open navigation menu'}
      >
        {mobileMenuOpen ? (
          /* X icon — close */
          <svg
            className="h-6 w-6"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <line x1="18" y1="6" x2="6" y2="18" />
            <line x1="6" y1="6" x2="18" y2="18" />
          </svg>
        ) : (
          /* Hamburger icon — open */
          <svg
            className="h-6 w-6"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            <line x1="3" y1="12" x2="21" y2="12" />
            <line x1="3" y1="6" x2="21" y2="6" />
            <line x1="3" y1="18" x2="21" y2="18" />
          </svg>
        )}
      </button>

      {/* ================================================================= */}
      {/* Toast Notification Container                                      */}
      {/* Replaces <vc:screen-message> from _AppMaster.cshtml line 28      */}
      {/*                                                                   */}
      {/* Position: toast-top-center (matching monolith Toastr config)      */}
      {/* newestOnTop: false — oldest toast stays at top, new ones append   */}
      {/* ================================================================= */}
      {toasts.length > 0 && (
        <div
          className="pointer-events-none fixed inset-x-0 top-0 z-50 flex flex-col items-center gap-2 p-4"
          aria-live="polite"
          aria-atomic="false"
          role="status"
        >
          {toasts.map((toast) => {
            const ToastIcon = TOAST_ICONS[toast.type];
            return (
              <div
                key={toast.id}
                className={[
                  'pointer-events-auto flex w-full max-w-sm items-start gap-3',
                  'rounded-lg px-4 py-3 shadow-lg',
                  TOAST_STYLE_CLASSES[toast.type],
                ].join(' ')}
                role="alert"
              >
                {/* Severity icon */}
                <span className="mt-0.5">
                  <ToastIcon />
                </span>

                {/* Text content */}
                <div className="min-w-0 flex-1">
                  {toast.title && (
                    <p className="text-sm font-semibold leading-5">
                      {toast.title}
                    </p>
                  )}
                  <p className="text-sm leading-5">{toast.message}</p>
                </div>

                {/* Manual close button */}
                <button
                  type="button"
                  onClick={() => removeToast(toast.id)}
                  className={[
                    'shrink-0 rounded-md p-1.5',
                    TOAST_CLOSE_CLASSES[toast.type],
                    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-white/60',
                  ].join(' ')}
                  aria-label="Dismiss notification"
                >
                  <svg
                    className="h-4 w-4"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                  >
                    <line x1="18" y1="6" x2="6" y2="18" />
                    <line x1="6" y1="6" x2="18" y2="18" />
                  </svg>
                </button>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Default Export
// ---------------------------------------------------------------------------

export default AppShell;
