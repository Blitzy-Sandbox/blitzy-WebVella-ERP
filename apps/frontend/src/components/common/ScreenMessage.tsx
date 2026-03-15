/**
 * Toast Notification Component — ScreenMessage
 *
 * Replaces the monolith's `ScreenMessage/` ViewComponent + Toastr JavaScript
 * library integration. The original system passes `ScreenMessage` objects via
 * `TempData`, then renders inline `<script>` blocks that call the Toastr API.
 *
 * In the React SPA this becomes a purely state-driven toast notification
 * system built with React hooks and Tailwind CSS utility classes.
 *
 * Key behaviours preserved from the monolith's `Default.cshtml`:
 *   - Position: top-center of the viewport (`toast-top-center`)
 *   - Stacking: oldest on top, newest at bottom (`newestOnTop: false`)
 *   - Error timeout: 7 000 ms (`timeOut: 7000, extendedTimeOut: 7000`)
 *   - Other types timeout: 3 000 ms (`timeOut: 3000, extendedTimeOut: 3000`)
 *   - Type-to-colour mapping: success=green, info=blue, warning=yellow, error=red
 *
 * Source files:
 *   - WebVella.Erp.Web/Components/ScreenMessage/ScreenMessageViewComponent.cs
 *   - WebVella.Erp.Web/Components/ScreenMessage/Default.cshtml
 *   - WebVella.Erp.Web/Models/ScreenMessage.cs
 */

import { useState, useCallback, useEffect } from 'react';
import { ScreenMessageType } from '../../types/common';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Represents a single toast notification message.
 *
 * Mirrors the C# `ScreenMessage` class from ScreenMessage.cs with the
 * addition of `id` (for keying / dismissal) and `duration` (auto-dismiss
 * timing derived from `ScreenMessageType`).
 */
export interface ToastMessage {
  /** Unique identifier used as React key and for programmatic dismissal. */
  id: string;
  /** Severity level controlling colour and auto-dismiss timing. */
  type: ScreenMessageType;
  /** Optional bold heading rendered above the body text. */
  title: string;
  /** Body text of the notification. */
  message: string;
  /** Auto-dismiss delay in milliseconds (Error = 7 000, others = 3 000). */
  duration: number;
}

/**
 * Props for the `ScreenMessage` container component.
 */
export interface ScreenMessageProps {
  /** Active toast messages displayed in order (oldest first). */
  messages: ToastMessage[];
  /** Callback invoked to remove a toast by its `id`. */
  onDismiss: (id: string) => void;
}

/**
 * Props for an individual `ToastItem` rendered inside the container.
 */
export interface ToastItemProps {
  /** The toast data to render. */
  toast: ToastMessage;
  /** Callback invoked when the toast should be removed. */
  onDismiss: (id: string) => void;
}

// ---------------------------------------------------------------------------
// Helper — Duration
// ---------------------------------------------------------------------------

/**
 * Returns the auto-dismiss duration in milliseconds for a given message type.
 *
 * Matches the monolith's `Default.cshtml`:
 *   - Error → 7 000 ms (line 14: `timeOut: 7000, extendedTimeOut: 7000`)
 *   - All other types → 3 000 ms (line 17: `timeOut: 3000, extendedTimeOut: 3000`)
 */
export function getToastDuration(type: ScreenMessageType): number {
  return type === ScreenMessageType.Error ? 7000 : 3000;
}

// ---------------------------------------------------------------------------
// Helper — Type → Tailwind colour classes
// ---------------------------------------------------------------------------

/**
 * Maps a `ScreenMessageType` to Tailwind CSS background / text classes.
 *
 * Colour semantics follow the Toastr theme colours:
 *   - Success → green
 *   - Info    → blue
 *   - Warning → yellow
 *   - Error   → red
 */
function getToastClasses(type: ScreenMessageType): string {
  switch (type) {
    case ScreenMessageType.Success:
      return 'bg-green-500 text-white';
    case ScreenMessageType.Info:
      return 'bg-blue-500 text-white';
    case ScreenMessageType.Warning:
      return 'bg-yellow-500 text-white';
    case ScreenMessageType.Error:
      return 'bg-red-600 text-white';
    default:
      return 'bg-gray-700 text-white';
  }
}

// ---------------------------------------------------------------------------
// Helper — Type → Icon
// ---------------------------------------------------------------------------

/**
 * Returns an accessible icon character for each toast type.
 * Uses Unicode symbols so no icon library dependency is needed.
 */
function getToastIcon(type: ScreenMessageType): string {
  switch (type) {
    case ScreenMessageType.Success:
      return '✓';
    case ScreenMessageType.Info:
      return 'ℹ';
    case ScreenMessageType.Warning:
      return '⚠';
    case ScreenMessageType.Error:
      return '✕';
    default:
      return 'ℹ';
  }
}

// ---------------------------------------------------------------------------
// ToastItem — Individual notification
// ---------------------------------------------------------------------------

/**
 * Renders a single toast notification with auto-dismiss behaviour.
 *
 * The `useEffect` hook sets a timeout matching the toast's `duration`. When
 * the timer fires (or the user clicks the close button), `onDismiss` removes
 * the toast from the parent container's message list.
 */
function ToastItem({ toast, onDismiss }: ToastItemProps) {
  useEffect(() => {
    const timer = setTimeout(() => {
      onDismiss(toast.id);
    }, toast.duration);

    return () => {
      clearTimeout(timer);
    };
  }, [toast.id, toast.duration, onDismiss]);

  return (
    <div
      className={`pointer-events-auto rounded-lg shadow-lg px-4 py-3 min-w-[300px] max-w-[400px] flex items-start gap-3 ${getToastClasses(toast.type)}`}
      role="alert"
      aria-live="polite"
    >
      {/* Severity icon */}
      <span className="text-xl leading-none flex-shrink-0" aria-hidden="true">
        {getToastIcon(toast.type)}
      </span>

      {/* Content */}
      <div className="flex-1 min-w-0">
        {toast.title && (
          <div className="font-semibold text-sm leading-snug">
            {toast.title}
          </div>
        )}
        <div className="text-sm leading-snug break-words">{toast.message}</div>
      </div>

      {/* Dismiss button */}
      <button
        type="button"
        onClick={() => onDismiss(toast.id)}
        className="flex-shrink-0 text-current opacity-70 hover:opacity-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-white rounded"
        aria-label="Dismiss notification"
      >
        <span aria-hidden="true" className="text-lg leading-none">
          ×
        </span>
      </button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// ScreenMessage — Toast container (default export)
// ---------------------------------------------------------------------------

/**
 * Fixed-position container that renders active toast notifications at the
 * top-center of the viewport.
 *
 * Replaces Toastr's `positionClass: "toast-top-center"` and
 * `newestOnTop: false` (messages are rendered in array order — oldest first,
 * newest appended at the bottom).
 *
 * The container itself is `pointer-events-none` so clicks pass through to the
 * page beneath. Each individual `ToastItem` re-enables pointer events.
 *
 * Usage:
 * ```tsx
 * const { messages, showToast, dismissToast } = useToast();
 * <ScreenMessage messages={messages} onDismiss={dismissToast} />
 * ```
 */
export default function ScreenMessage({
  messages,
  onDismiss,
}: ScreenMessageProps) {
  if (messages.length === 0) {
    return null;
  }

  return (
    <div
      className="fixed top-0 inset-inline-start-0 inline-size-full z-[9999] flex flex-col items-center gap-2 p-4 pointer-events-none"
      aria-label="Notifications"
    >
      {messages.map((msg) => (
        <ToastItem key={msg.id} toast={msg} onDismiss={onDismiss} />
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// useToast hook — app-wide toast management
// ---------------------------------------------------------------------------

/**
 * Custom hook that provides a self-contained toast notification state manager.
 *
 * Replaces the monolith's `TempData.Set<ScreenMessage>("ScreenMessage", ...)`
 * pattern. Consumers call `showToast()` to enqueue a notification and the
 * component automatically handles auto-dismiss via `getToastDuration()`.
 *
 * Returns:
 *   - `messages`     — the current list of active toasts
 *   - `showToast`    — enqueue a new toast (type, title, message)
 *   - `dismissToast` — remove a toast by id
 *
 * Example:
 * ```tsx
 * const { messages, showToast, dismissToast } = useToast();
 *
 * showToast(ScreenMessageType.Success, 'Saved', 'Record updated.');
 *
 * <ScreenMessage messages={messages} onDismiss={dismissToast} />
 * ```
 */
export function useToast(): {
  messages: ToastMessage[];
  showToast: (
    type: ScreenMessageType,
    title: string,
    message: string,
  ) => void;
  dismissToast: (id: string) => void;
} {
  const [messages, setMessages] = useState<ToastMessage[]>([]);

  const showToast = useCallback(
    (type: ScreenMessageType, title: string, message: string) => {
      const id = crypto.randomUUID();
      const duration = getToastDuration(type);
      setMessages((prev) => [...prev, { id, type, title, message, duration }]);
    },
    [],
  );

  const dismissToast = useCallback((id: string) => {
    setMessages((prev) => prev.filter((m) => m.id !== id));
  }, []);

  return { messages, showToast, dismissToast };
}
