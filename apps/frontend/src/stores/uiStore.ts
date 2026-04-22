/**
 * UI Preferences & Ephemeral State Zustand Store
 * `apps/frontend/src/stores/uiStore.ts`
 *
 * Zustand 5 store for UI preferences and transient UI state. This is the
 * central client-side store for all non-authentication UI concerns: sidebar
 * layout, theme selection, toast notifications, section collapse states,
 * component usage analytics, and route-loading indicators.
 *
 * Replaces the following monolith modules:
 *
 *  - **`UserPreferencies.cs`** — Server-side preference persistence
 *    (sidebar size via `SetSidebarSize`, SDK component usage counters via
 *    `SdkUseComponent`, per-component data via `Get/Set/RemoveComponentData`).
 *    Preferences were stored as a JSON-serialised `preferences` field on the
 *    `user` entity record.
 *
 *  - **`ScreenMessage.cs`** — Toast notification model with `Type` (enum:
 *    Success=0, Info=1, Warning=2, Error=3), `Title`, and `Message` fields.
 *    Used throughout the monolith for user feedback (success confirmations,
 *    validation errors, warnings).
 *
 *  - **`ErpAppContext.cs`** — Singleton application context holding:
 *    active `Theme` object (from `ThemeService.Get()`),
 *    generated CSS content + SHA256 hash, core web settings, script includes,
 *    and a process-wide memory cache. Only the theme selection aspect is
 *    captured here; CSS generation moves to Tailwind CSS.
 *
 *  - **`Theme.cs` / `ThemeService.cs`** — Theme model with 80+ colour/font
 *    properties and the service that resolves the active theme by ID. In the
 *    React SPA, only the `themeId` preference is stored; actual theme data
 *    is fetched/resolved via TanStack Query.
 *
 * Persistence strategy:
 *  - Uses Zustand `persist` middleware with `localStorage` to remember user
 *    preferences (sidebar, theme, section states, component usage) across
 *    browser sessions — matching the monolith's "remember preferences" UX.
 *  - Ephemeral state (toast messages, route loading) is intentionally NOT
 *    persisted.
 *  - Server-side sync of preferences is NOT this store's responsibility —
 *    it is handled by a separate TanStack Query mutation hook if needed.
 */

import { create } from 'zustand';
import { persist, createJSONStorage } from 'zustand/middleware';
import { ScreenMessageType } from '../types/common';

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * A transient UI notification displayed as a toast or banner.
 *
 * Extends the monolith's `ScreenMessage.cs` model with:
 *  - `id`       — UUID for programmatic dismissal (not in C# model)
 *  - `duration` — Auto-dismiss timer in milliseconds (not in C# model)
 *
 * The `type` property uses `ScreenMessageType` enum values that match the
 * C# `ScreenMessageType` exactly: Success=0, Info=1, Warning=2, Error=3.
 */
export interface ScreenMessage {
  /** Unique identifier for toast dismissal. Generated via `crypto.randomUUID()`. */
  id: string;

  /** Severity / visual style of the message. */
  type: ScreenMessageType;

  /** Short headline (may be empty string). */
  title: string;

  /** Detailed message body (may be empty string). */
  message: string;

  /**
   * Auto-dismiss duration in milliseconds. Default is 5000ms (5 seconds).
   * Set to `0` or `undefined` to disable auto-dismiss (sticky toast).
   */
  duration?: number;
}

/**
 * Per-section collapse state map.
 *
 * Replaces the monolith's `UserPreferencies.GetComponentData` /
 * `SetComponentData` / `RemoveComponentData` methods that stored
 * per-component data as `EntityRecord` objects in a `JObject` dictionary
 * keyed by lowercase component name.
 *
 * In the React SPA, section collapse is tracked as a simple
 * `{ [sectionId]: boolean }` map where `true` means collapsed.
 */
export interface SectionState {
  [sectionId: string]: boolean;
}

/**
 * Complete UI store shape — state properties **and** actions.
 *
 * Design notes:
 *  - State is split into "persistent" (survives page reload via localStorage)
 *    and "ephemeral" (reset on each page load).
 *  - No API calls are made from this store. Theme data fetching, preference
 *    syncing, etc. are the responsibility of TanStack Query hooks.
 *  - Actions that return values (e.g. `getSectionCollapsed`) use `get()` to
 *    access current state — standard Zustand pattern.
 */
export interface UiState {
  // ── Persistent state (survives page reload) ─────────────────────────────

  /**
   * Whether the sidebar navigation is collapsed.
   * Replaces part of `UserPreferencies.SetSidebarSize` behaviour.
   */
  sidebarCollapsed: boolean;

  /**
   * Sidebar size preference: `'sm'` | `'md'` | `'lg'`.
   * Matches the monolith's `UserPreferencies.SidebarSize` values that were
   * persisted via `SetSidebarSize(userId, size)`.
   */
  sidebarSize: string;

  /**
   * Active theme identifier (GUID string or empty for default).
   * Replaces `ErpAppContext.Theme` selection. An empty string means
   * "use default theme" (mirrors `Theme.cs` default where `Id = Guid.Empty`).
   */
  themeId: string;

  /**
   * Per-section collapse tracking.
   * Replaces `UserPreferencies.GetComponentData/SetComponentData` for storing
   * collapse state per UI section.
   */
  sectionStates: SectionState;

  /**
   * SDK component usage counters.
   * Replaces `UserPreferencies.SdkUseComponent` which incremented
   * `preferences.ComponentUsage[name].SdkUsed` and set `SdkUsedOn` timestamp.
   *
   * Each entry tracks how many times a component was used (`count`) and the
   * ISO 8601 timestamp of the last usage (`lastUsedOn`).
   */
  componentUsage: Record<string, { count: number; lastUsedOn: string }>;

  // ── Ephemeral state (reset on page reload) ──────────────────────────────

  /**
   * Active toast notification messages.
   * Replaces the `ScreenMessage` model used throughout the monolith for
   * in-page user feedback. Messages are automatically dismissed after their
   * configured `duration`.
   */
  messages: ScreenMessage[];

  /**
   * Global route-transition loading indicator.
   * Set to `true` during React Router navigation transitions and cleared
   * when the target route finishes loading.
   */
  isRouteLoading: boolean;

  // ── Sidebar actions ─────────────────────────────────────────────────────

  /** Toggle sidebar between collapsed and expanded. */
  toggleSidebar: () => void;

  /**
   * Set the sidebar size preference.
   * @param size - One of `'sm'`, `'md'`, `'lg'`.
   */
  setSidebarSize: (size: string) => void;

  /**
   * Explicitly set the sidebar collapsed state.
   * @param collapsed - `true` to collapse, `false` to expand.
   */
  setSidebarCollapsed: (collapsed: boolean) => void;

  // ── Theme actions ───────────────────────────────────────────────────────

  /**
   * Set the active theme identifier.
   * @param themeId - Theme GUID string, or empty string for default.
   */
  setThemeId: (themeId: string) => void;

  // ── Toast notification actions ──────────────────────────────────────────

  /**
   * Add a toast notification message.
   * Generates a unique ID and starts an auto-dismiss timer when `duration > 0`.
   * @param message - Message payload (without `id`, which is auto-generated).
   */
  addMessage: (message: Omit<ScreenMessage, 'id'>) => void;

  /**
   * Remove a specific toast notification by ID.
   * @param id - The UUID of the message to remove.
   */
  removeMessage: (id: string) => void;

  /** Remove all active toast notifications. */
  clearMessages: () => void;

  // ── Section state actions ───────────────────────────────────────────────

  /**
   * Set the collapse state for a specific UI section.
   * Replaces `UserPreferencies.SetComponentData`.
   * @param sectionId - Unique section identifier.
   * @param collapsed - `true` to collapse, `false` to expand.
   */
  setSectionCollapsed: (sectionId: string, collapsed: boolean) => void;

  /**
   * Get the collapse state for a specific UI section.
   * Returns `false` (expanded) if no state has been recorded.
   * Replaces `UserPreferencies.GetComponentData`.
   * @param sectionId - Unique section identifier.
   */
  getSectionCollapsed: (sectionId: string) => boolean;

  /**
   * Remove the collapse state for a specific UI section.
   * Replaces `UserPreferencies.RemoveComponentData`.
   * @param sectionId - Unique section identifier.
   */
  removeSectionState: (sectionId: string) => void;

  // ── Component usage actions ─────────────────────────────────────────────

  /**
   * Increment the usage counter for a named component.
   * Replaces `UserPreferencies.SdkUseComponent` which incremented
   * `SdkUsed` counter and set `SdkUsedOn` to `DateTime.UtcNow`.
   * @param componentName - Fully qualified component name string.
   */
  trackComponentUsage: (componentName: string) => void;

  // ── Route loading ───────────────────────────────────────────────────────

  /**
   * Set the global route-loading indicator.
   * @param loading - `true` when a route transition is in progress.
   */
  setRouteLoading: (loading: boolean) => void;

  // ── Reset ───────────────────────────────────────────────────────────────

  /**
   * Hard-reset the store to its initial default state.
   * Clears all preferences and ephemeral state. Useful during logout flows,
   * error recovery, or test teardown.
   */
  resetUiState: () => void;
}

// ---------------------------------------------------------------------------
// Default state
// ---------------------------------------------------------------------------

/**
 * Initial UI state values.
 *
 * - `sidebarCollapsed: false` — sidebar starts expanded (matches monolith
 *   default where no preference record means "show full sidebar").
 * - `sidebarSize: 'md'` — medium sidebar width (matches monolith default).
 * - `themeId: ''` — empty string means use the default theme
 *   (mirrors `Theme.cs` where `Id = Guid.Empty` means default).
 * - `messages: []` — no active toasts on load.
 * - `sectionStates: {}` — no sections have recorded collapse state.
 * - `componentUsage: {}` — no component usage recorded.
 * - `isRouteLoading: false` — no route transition in progress.
 */
const defaultUiState: Pick<
  UiState,
  | 'sidebarCollapsed'
  | 'sidebarSize'
  | 'themeId'
  | 'messages'
  | 'sectionStates'
  | 'componentUsage'
  | 'isRouteLoading'
> = {
  sidebarCollapsed: false,
  sidebarSize: 'md',
  themeId: '',
  messages: [],
  sectionStates: {},
  componentUsage: {},
  isRouteLoading: false,
};

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

/**
 * Primary UI preferences and ephemeral state store.
 *
 * Usage — full destructuring:
 * ```tsx
 * const { sidebarCollapsed, toggleSidebar, addMessage } = useUiStore();
 * ```
 *
 * Usage — with selector for minimal re-renders:
 * ```tsx
 * const collapsed = useUiStore(s => s.sidebarCollapsed);
 * ```
 *
 * Persistence: sidebar, theme, section states, and component usage are
 * automatically saved to `localStorage` under the key
 * `'webvella-ui-preferences'`. Toast messages and loading flags are
 * ephemeral and are NOT persisted.
 */
export const useUiStore = create<UiState>()(
  persist(
    (set, get) => ({
      // ── State ────────────────────────────────────────────────────────────
      ...defaultUiState,

      // ── Sidebar actions ──────────────────────────────────────────────────

      toggleSidebar: (): void => {
        set((state) => ({ sidebarCollapsed: !state.sidebarCollapsed }));
      },

      setSidebarSize: (size: string): void => {
        set({ sidebarSize: size });
      },

      setSidebarCollapsed: (collapsed: boolean): void => {
        set({ sidebarCollapsed: collapsed });
      },

      // ── Theme actions ────────────────────────────────────────────────────

      setThemeId: (themeId: string): void => {
        set({ themeId });
      },

      // ── Toast notification actions ───────────────────────────────────────

      addMessage: (message: Omit<ScreenMessage, 'id'>): void => {
        const id = crypto.randomUUID();
        const duration = message.duration ?? 5000;
        const newMessage: ScreenMessage = { ...message, id, duration };

        set((state) => ({
          messages: [...state.messages, newMessage],
        }));

        // Auto-dismiss after the configured duration.
        // A duration of 0 or undefined means the toast is "sticky" and must
        // be dismissed manually by the user.
        if (duration > 0) {
          setTimeout(() => {
            get().removeMessage(id);
          }, duration);
        }
      },

      removeMessage: (id: string): void => {
        set((state) => ({
          messages: state.messages.filter((m) => m.id !== id),
        }));
      },

      clearMessages: (): void => {
        set({ messages: [] });
      },

      // ── Section state actions ────────────────────────────────────────────

      setSectionCollapsed: (sectionId: string, collapsed: boolean): void => {
        set((state) => ({
          sectionStates: { ...state.sectionStates, [sectionId]: collapsed },
        }));
      },

      getSectionCollapsed: (sectionId: string): boolean => {
        const state = get();
        return state.sectionStates[sectionId] ?? false;
      },

      removeSectionState: (sectionId: string): void => {
        set((state) => {
          const { [sectionId]: _, ...rest } = state.sectionStates;
          // Suppress unused variable lint for the destructured key
          void _;
          return { sectionStates: rest };
        });
      },

      // ── Component usage actions ──────────────────────────────────────────

      trackComponentUsage: (componentName: string): void => {
        set((state) => ({
          componentUsage: {
            ...state.componentUsage,
            [componentName]: {
              count: (state.componentUsage[componentName]?.count ?? 0) + 1,
              lastUsedOn: new Date().toISOString(),
            },
          },
        }));
      },

      // ── Route loading ────────────────────────────────────────────────────

      setRouteLoading: (loading: boolean): void => {
        set({ isRouteLoading: loading });
      },

      // ── Reset ────────────────────────────────────────────────────────────

      resetUiState: (): void => {
        set({ ...defaultUiState });
      },
    }),
    {
      name: 'webvella-ui-preferences',
      storage: createJSONStorage(() => localStorage),
      /**
       * Only persist user-preference fields to localStorage.
       * Ephemeral state (messages, isRouteLoading) is intentionally excluded
       * so it resets on each page load — toast messages are transient, and
       * route-loading state is only relevant during navigation.
       */
      partialize: (state) => ({
        sidebarCollapsed: state.sidebarCollapsed,
        sidebarSize: state.sidebarSize,
        themeId: state.themeId,
        sectionStates: state.sectionStates,
        componentUsage: state.componentUsage,
      }),
    }
  )
);

// ---------------------------------------------------------------------------
// Typed selector hooks
// ---------------------------------------------------------------------------
// These thin wrappers select a single slice of state and cause the consuming
// component to re-render only when that specific slice changes. They are the
// recommended way to consume the store in leaf components.

/** Select `sidebarCollapsed` from the UI store. */
export const useSidebarCollapsed = (): boolean =>
  useUiStore((state) => state.sidebarCollapsed);

/** Select `sidebarSize` from the UI store. */
export const useSidebarSize = (): string =>
  useUiStore((state) => state.sidebarSize);

/** Select the active toast `messages` array from the UI store. */
export const useMessages = (): ScreenMessage[] =>
  useUiStore((state) => state.messages);

/** Select `themeId` from the UI store. */
export const useThemeId = (): string =>
  useUiStore((state) => state.themeId);

/** Select `isRouteLoading` from the UI store. */
export const useIsRouteLoading = (): boolean =>
  useUiStore((state) => state.isRouteLoading);

// ---------------------------------------------------------------------------
// Re-exports for consumer convenience
// ---------------------------------------------------------------------------

export { ScreenMessageType };
