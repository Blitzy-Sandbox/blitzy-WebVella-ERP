/**
 * Vitest Unit Tests — `uiStore` (Zustand 5 UI Preferences Store)
 *
 * Comprehensive test suite for the UI preferences Zustand store that replaces
 * the monolith's server-side state management:
 *
 *  - **`UserPreferencies.cs`** — Sidebar size via `SetSidebarSize`, SDK
 *    component usage counters via `SdkUseComponent`, per-component collapse
 *    state via `Get/Set/RemoveComponentData`.
 *
 *  - **`ScreenMessage.cs`** — Toast notification model with `Type` (enum:
 *    Success=0, Info=1, Warning=2, Error=3), `Title`, and `Message` fields,
 *    rendered by the Toastr jQuery plugin in the monolith.
 *
 *  - **`ErpAppContext.cs`** — Theme selection (`Theme` object resolved by
 *    `ThemeService.Get()`). Only `themeId` preference is stored client-side.
 *
 *  - **`Theme.cs`** — Theme model with 80+ colour/font properties; the React
 *    SPA stores only the `themeId` in the UI store.
 *
 * All tests use the `useUiStore.getState()` / `.setState()` pattern for
 * direct state manipulation — no React rendering is required.
 *
 * @see apps/frontend/src/stores/uiStore.ts
 * @see WebVella.Erp.Web/Services/UserPreferencies.cs
 * @see WebVella.Erp.Web/Models/ScreenMessage.cs
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useUiStore } from '../../../src/stores/uiStore';
import { ScreenMessageType } from '../../../src/types/common';

// ---------------------------------------------------------------------------
// Test Suite
// ---------------------------------------------------------------------------

describe('uiStore', () => {
  /**
   * CRITICAL: Zustand stores are singletons. To prevent cross-test state
   * leakage, reset the store to its default values before every test.
   *
   * The default state mirrors `defaultUiState` in the store:
   *   sidebarCollapsed: false
   *   sidebarSize:      'md'
   *   themeId:          ''
   *   messages:         []
   *   sectionStates:    {}
   *   componentUsage:   {}
   *   isRouteLoading:   false
   */
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();

    useUiStore.setState({
      sidebarCollapsed: false,
      sidebarSize: 'md',
      themeId: '',
      messages: [],
      sectionStates: {},
      componentUsage: {},
      isRouteLoading: false,
    });
  });

  // =========================================================================
  // Sidebar Size State
  // =========================================================================

  describe('sidebarSize', () => {
    /**
     * Test 1 — Default sidebar size.
     * In the monolith, `UserPreferencies.SidebarSize` defaults to 'md'
     * when no preference record exists.
     */
    it('initial sidebarSize is "md"', () => {
      const state = useUiStore.getState();
      expect(state.sidebarSize).toBe('md');
    });

    /**
     * Test 2 — `setSidebarSize` updates the sidebar size.
     * Replaces `UserPreferencies.SetSidebarSize(userId, 'sm')`.
     */
    it('setSidebarSize updates sidebar size', () => {
      useUiStore.getState().setSidebarSize('sm');
      expect(useUiStore.getState().sidebarSize).toBe('sm');
    });

    /**
     * Test 3 — `setSidebarSize` to 'lg'.
     * Validates that the monolith's 'erp-sidebar-lg' body class equivalent
     * (large sidebar) is supported.
     */
    it('setSidebarSize to "lg"', () => {
      useUiStore.getState().setSidebarSize('lg');
      expect(useUiStore.getState().sidebarSize).toBe('lg');
    });
  });

  // =========================================================================
  // Sidebar Collapsed Toggle
  // =========================================================================

  describe('sidebarCollapsed', () => {
    /**
     * Test 4 — Default collapsed state.
     * Sidebar starts expanded (not collapsed) matching the monolith's default
     * where no preference means "show full sidebar".
     */
    it('initial sidebarCollapsed is false', () => {
      const state = useUiStore.getState();
      expect(state.sidebarCollapsed).toBe(false);
    });

    /**
     * Test 5 — `toggleSidebar` flips the collapsed state.
     * Verifies that calling toggle twice returns to the original state.
     */
    it('toggleSidebar toggles collapsed state', () => {
      useUiStore.getState().toggleSidebar();
      expect(useUiStore.getState().sidebarCollapsed).toBe(true);

      useUiStore.getState().toggleSidebar();
      expect(useUiStore.getState().sidebarCollapsed).toBe(false);
    });

    /**
     * Test 6 — `setSidebarCollapsed` sets an explicit value.
     * Ensures that the explicit setter overrides any previous toggle state.
     */
    it('setSidebarCollapsed sets explicit value', () => {
      useUiStore.getState().setSidebarCollapsed(true);
      expect(useUiStore.getState().sidebarCollapsed).toBe(true);

      useUiStore.getState().setSidebarCollapsed(false);
      expect(useUiStore.getState().sidebarCollapsed).toBe(false);
    });
  });

  // =========================================================================
  // Toast Notifications (ScreenMessage replacement)
  // =========================================================================

  describe('toast notifications', () => {
    /**
     * Test 7 — `addMessage` adds a toast notification.
     * Replaces `ScreenMessage` objects added to `TempData["ScreenMessage"]`
     * and rendered by the Toastr jQuery plugin in _AppMaster.cshtml.
     *
     * ScreenMessageType.Success = 0 (matches C# enum).
     */
    it('addMessage adds a toast notification', () => {
      const mockUuid = 'test-uuid-add-msg';
      vi.stubGlobal('crypto', { randomUUID: () => mockUuid });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Success,
        title: 'Success',
        message: 'Record created',
        duration: 5000,
      });

      const messages = useUiStore.getState().messages;
      expect(messages.length).toBe(1);
      expect(messages[0].type).toBe(0); // ScreenMessageType.Success = 0
      expect(messages[0].title).toBe('Success');
      expect(messages[0].message).toBe('Record created');
      expect(messages[0].id).toBe(mockUuid);
    });

    /**
     * Test 8 — `addMessage` generates unique ID for each toast.
     * Verifies that each message receives a distinct `id` from
     * `crypto.randomUUID()`.
     */
    it('addMessage generates unique id for each toast', () => {
      let callCount = 0;
      vi.stubGlobal('crypto', {
        randomUUID: () => {
          callCount++;
          return `mock-uuid-${callCount}`;
        },
      });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Success,
        title: 'First',
        message: 'first message',
        duration: 5000,
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Info,
        title: 'Second',
        message: 'second message',
        duration: 5000,
      });

      const messages = useUiStore.getState().messages;
      expect(messages.length).toBe(2);
      expect(messages[0].id).toBe('mock-uuid-1');
      expect(messages[1].id).toBe('mock-uuid-2');
      expect(messages[0].id).not.toBe(messages[1].id);
    });

    /**
     * Test 9 — `addMessage` supports all ScreenMessageType values.
     * Validates that all four severity levels from the C# enum are
     * correctly stored: Success=0, Info=1, Warning=2, Error=3.
     */
    it('addMessage supports all ScreenMessageType values', () => {
      let callCount = 0;
      vi.stubGlobal('crypto', {
        randomUUID: () => {
          callCount++;
          return `msg-${callCount}`;
        },
      });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Success,
        title: 'OK',
        message: 'success msg',
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Info,
        title: 'Info',
        message: 'info msg',
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Warning,
        title: 'Warning',
        message: 'warning msg',
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Error,
        title: 'Error',
        message: 'error msg',
      });

      const messages = useUiStore.getState().messages;
      expect(messages.length).toBe(4);
      expect(messages[0].type).toBe(0); // Success
      expect(messages[1].type).toBe(1); // Info
      expect(messages[2].type).toBe(2); // Warning
      expect(messages[3].type).toBe(3); // Error
    });

    /**
     * Test 10 — `addMessage` with default duration.
     * When `duration` is not specified in the message payload, the store
     * defaults to 5000ms (5 seconds). This matches a reasonable UX default
     * for transient toast notifications.
     */
    it('addMessage with default duration', () => {
      vi.stubGlobal('crypto', { randomUUID: () => 'uuid-default-dur' });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Success,
        title: 'Default',
        message: 'uses default duration',
      });

      const messages = useUiStore.getState().messages;
      expect(messages.length).toBe(1);
      // The store sets duration to `message.duration ?? 5000`
      expect(messages[0].duration).toBe(5000);
    });
  });

  // =========================================================================
  // Remove / Clear Messages
  // =========================================================================

  describe('removeMessage / clearMessages', () => {
    /**
     * Test 11 — `removeMessage` removes a specific toast by ID.
     * Simulates user dismissing a single notification while others remain.
     */
    it('removeMessage removes toast by id', () => {
      let callCount = 0;
      vi.stubGlobal('crypto', {
        randomUUID: () => {
          callCount++;
          return `rm-uuid-${callCount}`;
        },
      });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Success,
        title: 'First',
        message: 'msg 1',
        duration: 0, // Sticky — no auto-dismiss
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Info,
        title: 'Second',
        message: 'msg 2',
        duration: 0,
      });

      expect(useUiStore.getState().messages.length).toBe(2);

      // Remove the first message
      useUiStore.getState().removeMessage('rm-uuid-1');

      const remaining = useUiStore.getState().messages;
      expect(remaining.length).toBe(1);
      expect(remaining[0].id).toBe('rm-uuid-2');
      expect(remaining[0].title).toBe('Second');
    });

    /**
     * Test 12 — `removeMessage` is safe for a non-existent ID.
     * Ensures that calling removeMessage with an ID that doesn't exist
     * in the messages array does not alter the store state.
     */
    it('removeMessage is safe for non-existent id', () => {
      vi.stubGlobal('crypto', { randomUUID: () => 'safe-uuid' });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Warning,
        title: 'Existing',
        message: 'existing msg',
        duration: 0,
      });

      expect(useUiStore.getState().messages.length).toBe(1);

      // Remove with a non-existent ID
      useUiStore.getState().removeMessage('non-existent-uuid');

      expect(useUiStore.getState().messages.length).toBe(1);
      expect(useUiStore.getState().messages[0].id).toBe('safe-uuid');
    });

    /**
     * Test 13 — `clearMessages` removes all toasts at once.
     * Useful during route transitions or error recovery to clear
     * all stale notifications.
     */
    it('clearMessages removes all toasts', () => {
      let callCount = 0;
      vi.stubGlobal('crypto', {
        randomUUID: () => {
          callCount++;
          return `clear-uuid-${callCount}`;
        },
      });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Success,
        title: 'A',
        message: 'msg a',
        duration: 0,
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Info,
        title: 'B',
        message: 'msg b',
        duration: 0,
      });
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Error,
        title: 'C',
        message: 'msg c',
        duration: 0,
      });

      expect(useUiStore.getState().messages.length).toBe(3);

      useUiStore.getState().clearMessages();

      expect(useUiStore.getState().messages.length).toBe(0);
    });
  });

  // =========================================================================
  // Toast Auto-Dismiss Timer
  // =========================================================================

  describe('toast auto-dismiss', () => {
    /**
     * Validates that `addMessage` sets a `setTimeout` for auto-dismiss
     * when `duration > 0`. After advancing timers by the configured
     * duration, the message should be automatically removed from state.
     */
    it('auto-dismisses toast after configured duration', () => {
      vi.useFakeTimers();
      vi.stubGlobal('crypto', { randomUUID: () => 'auto-dismiss-uuid' });

      useUiStore.getState().addMessage({
        type: ScreenMessageType.Info,
        title: 'Auto',
        message: 'will auto-dismiss',
        duration: 3000,
      });

      expect(useUiStore.getState().messages.length).toBe(1);

      // Advance timers by 3000ms (the configured duration)
      vi.advanceTimersByTime(3000);

      expect(useUiStore.getState().messages.length).toBe(0);
    });
  });

  // =========================================================================
  // Section Collapse State
  // =========================================================================

  describe('section collapse state', () => {
    /**
     * Test 14 — Initial sectionStates is an empty object.
     * Mirrors the monolith's `UserPreferencies.ComponentDataDictionary`
     * being empty for new users.
     */
    it('initial sectionStates is empty object', () => {
      const state = useUiStore.getState();
      expect(state.sectionStates).toEqual({});
    });

    /**
     * Test 15 — `setSectionCollapsed` sets section collapse state.
     * Replaces `UserPreferencies.SetComponentData(userId, componentName, data)`.
     */
    it('setSectionCollapsed sets section collapse state', () => {
      useUiStore.getState().setSectionCollapsed('section-1', true);
      expect(useUiStore.getState().sectionStates['section-1']).toBe(true);
    });

    /**
     * Test 16 — `setSectionCollapsed` can expand a section.
     * Verifies toggling collapse off after it has been set to true.
     */
    it('setSectionCollapsed can expand a section', () => {
      useUiStore.getState().setSectionCollapsed('section-1', true);
      expect(useUiStore.getState().sectionStates['section-1']).toBe(true);

      useUiStore.getState().setSectionCollapsed('section-1', false);
      expect(useUiStore.getState().sectionStates['section-1']).toBe(false);
    });

    /**
     * Test 17 — Section collapse can be toggled via successive calls.
     * The store does not have a dedicated `toggleSection` action, so
     * toggling is achieved by calling `setSectionCollapsed` with the
     * opposite of the current state (retrieved via `getSectionCollapsed`).
     */
    it('section collapse can be toggled via successive setSectionCollapsed calls', () => {
      // Initially undefined → getSectionCollapsed returns false
      expect(useUiStore.getState().getSectionCollapsed('section-1')).toBe(false);

      // Collapse (false → true)
      const currentState1 = useUiStore.getState().getSectionCollapsed('section-1');
      useUiStore.getState().setSectionCollapsed('section-1', !currentState1);
      expect(useUiStore.getState().getSectionCollapsed('section-1')).toBe(true);

      // Expand (true → false)
      const currentState2 = useUiStore.getState().getSectionCollapsed('section-1');
      useUiStore.getState().setSectionCollapsed('section-1', !currentState2);
      expect(useUiStore.getState().getSectionCollapsed('section-1')).toBe(false);
    });

    /**
     * Test 18 — `getSectionCollapsed` returns collapse state for a known section.
     * Mirrors `UserPreferencies.GetComponentData` which returns stored data
     * for a known component key (lowercased via `ToLowerInvariant()`).
     */
    it('getSectionCollapsed returns collapse state for known section', () => {
      useUiStore.getState().setSectionCollapsed('section-1', true);
      expect(useUiStore.getState().getSectionCollapsed('section-1')).toBe(true);
    });

    /**
     * Test 19 — `getSectionCollapsed` returns false for an unknown section.
     * Mirrors the monolith's `GetComponentData` returning `null` for unknown
     * keys — in the React SPA, this defaults to `false` (expanded).
     */
    it('getSectionCollapsed returns false for unknown section', () => {
      expect(useUiStore.getState().getSectionCollapsed('unknown-section')).toBe(false);
    });

    /**
     * Test 20 — `removeSectionState` deletes a section from the map.
     * Mirrors `UserPreferencies.RemoveComponentData(userId, componentName)`.
     */
    it('removeSectionState deletes section from map', () => {
      useUiStore.getState().setSectionCollapsed('section-1', true);
      expect(useUiStore.getState().sectionStates['section-1']).toBe(true);

      useUiStore.getState().removeSectionState('section-1');

      expect(useUiStore.getState().sectionStates['section-1']).toBeUndefined();
      expect('section-1' in useUiStore.getState().sectionStates).toBe(false);
    });
  });

  // =========================================================================
  // Component Usage Tracking
  // =========================================================================

  describe('component usage tracking', () => {
    /**
     * Test 21 — `trackComponentUsage` records first usage.
     * Replaces `UserPreferencies.SdkUseComponent(userId, componentFullName)`:
     *  - First-time use creates `{ Name, SdkUsed: 1, SdkUsedOn: DateTime.UtcNow }`
     * In the React SPA, this becomes `{ count: 1, lastUsedOn: ISOString }`.
     */
    it('trackComponentUsage records first usage', () => {
      useUiStore.getState().trackComponentUsage('PcFieldText');

      const usage = useUiStore.getState().componentUsage['PcFieldText'];
      expect(usage).toBeDefined();
      expect(usage.count).toBe(1);
      // Verify lastUsedOn is a valid ISO date string
      expect(new Date(usage.lastUsedOn).toISOString()).toBe(usage.lastUsedOn);
    });

    /**
     * Test 22 — `trackComponentUsage` increments existing counter.
     * Replaces `currentComponentUsage.SdkUsed++` in the monolith's
     * `SdkUseComponent` method.
     */
    it('trackComponentUsage increments existing counter', () => {
      useUiStore.getState().trackComponentUsage('PcFieldText');
      useUiStore.getState().trackComponentUsage('PcFieldText');

      const usage = useUiStore.getState().componentUsage['PcFieldText'];
      expect(usage.count).toBe(2);
    });
  });

  // =========================================================================
  // Theme and Route Loading
  // =========================================================================

  describe('theme and route loading', () => {
    /**
     * Test 23 — `setThemeId` updates the active theme.
     * Replaces `ErpAppContext.Theme` selection. The `themeId` is stored
     * as a string (GUID or empty for default), matching the monolith's
     * `Theme.cs` where `Id = Guid.Empty` means "use default theme".
     */
    it('setThemeId updates theme', () => {
      useUiStore.getState().setThemeId('dark-theme');
      expect(useUiStore.getState().themeId).toBe('dark-theme');
    });

    /**
     * Test 24 — `setRouteLoading` toggles the global loading indicator.
     * Used during React Router navigation transitions to show a loading
     * state in the application shell.
     */
    it('setRouteLoading toggles loading state', () => {
      useUiStore.getState().setRouteLoading(true);
      expect(useUiStore.getState().isRouteLoading).toBe(true);

      useUiStore.getState().setRouteLoading(false);
      expect(useUiStore.getState().isRouteLoading).toBe(false);
    });
  });

  // =========================================================================
  // Reset
  // =========================================================================

  describe('resetUiState', () => {
    /**
     * Test 25 — `resetUiState` restores all defaults.
     * Useful during logout flows or error recovery. All persistent and
     * ephemeral state should return to the `defaultUiState` values.
     */
    it('resetUiState restores all defaults', () => {
      // Modify every state property to a non-default value
      vi.stubGlobal('crypto', { randomUUID: () => 'reset-test-uuid' });

      useUiStore.getState().setSidebarCollapsed(true);
      useUiStore.getState().setSidebarSize('lg');
      useUiStore.getState().setThemeId('some-theme');
      useUiStore.getState().addMessage({
        type: ScreenMessageType.Error,
        title: 'Test',
        message: 'test msg',
        duration: 0,
      });
      useUiStore.getState().setSectionCollapsed('sec-a', true);
      useUiStore.getState().trackComponentUsage('PcButton');
      useUiStore.getState().setRouteLoading(true);

      // Verify state is modified
      expect(useUiStore.getState().sidebarCollapsed).toBe(true);
      expect(useUiStore.getState().sidebarSize).toBe('lg');
      expect(useUiStore.getState().themeId).toBe('some-theme');
      expect(useUiStore.getState().messages.length).toBe(1);
      expect(Object.keys(useUiStore.getState().sectionStates).length).toBe(1);
      expect(Object.keys(useUiStore.getState().componentUsage).length).toBe(1);
      expect(useUiStore.getState().isRouteLoading).toBe(true);

      // Reset
      useUiStore.getState().resetUiState();

      // Verify all properties restored to defaults
      expect(useUiStore.getState().sidebarCollapsed).toBe(false);
      expect(useUiStore.getState().sidebarSize).toBe('md');
      expect(useUiStore.getState().themeId).toBe('');
      expect(useUiStore.getState().messages).toEqual([]);
      expect(useUiStore.getState().sectionStates).toEqual({});
      expect(useUiStore.getState().componentUsage).toEqual({});
      expect(useUiStore.getState().isRouteLoading).toBe(false);
    });
  });

  // =========================================================================
  // State Isolation
  // =========================================================================

  describe('state isolation', () => {
    /**
     * Test 26 — State does not leak between tests.
     * Because `beforeEach` resets the store, this test verifies that
     * modifications from previous test cases do not persist.
     */
    it('state does not leak between tests', () => {
      // All properties should be at their default values (set by beforeEach)
      const state = useUiStore.getState();
      expect(state.sidebarCollapsed).toBe(false);
      expect(state.sidebarSize).toBe('md');
      expect(state.themeId).toBe('');
      expect(state.messages).toEqual([]);
      expect(state.sectionStates).toEqual({});
      expect(state.componentUsage).toEqual({});
      expect(state.isRouteLoading).toBe(false);
    });
  });
});
