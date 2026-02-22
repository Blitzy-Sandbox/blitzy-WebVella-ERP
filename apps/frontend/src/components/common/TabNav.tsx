import { useState, useCallback, useId } from 'react';

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/**
 * Render type for the TabNav component.
 *
 * Replaces the C# `TabNavRenderType` enum from
 * `WebVella.Erp.Web/Models/TabNavRenderType.cs`:
 *   TABS  = 1  →  border-bottom tab strip (Bootstrap `.nav-tabs`)
 *   PILLS = 2  →  rounded pill buttons   (Bootstrap `.nav-pills`)
 *
 * Default in the monolith's `PcTabNavOptions.RenderType` was PILLS.
 */
export enum TabNavRenderType {
  /** Border-bottom style tabs (replaces Bootstrap nav-tabs). */
  TABS = 'tabs',
  /** Rounded pill-style buttons (replaces Bootstrap nav-pills). */
  PILLS = 'pills',
}

// ---------------------------------------------------------------------------
// Interfaces
// ---------------------------------------------------------------------------

/**
 * Configuration for a single tab.
 *
 * Replaces the monolith's hardcoded `tab{N}_id` / `tab{N}_label` pairs
 * (tab1 through tab7) from `PcTabNavOptions`. Each `TabConfig` entry
 * corresponds to one of those seven slots.
 */
export interface TabConfig {
  /**
   * Unique tab identifier.
   * In the monolith: `tab1_id` through `tab7_id` with defaults "tab1"–"tab7".
   */
  id: string;

  /**
   * Display label shown on the tab button.
   * In the monolith: `tab1_label` through `tab7_label` with defaults "Tab 1"–"Tab 7".
   */
  label: string;

  /**
   * Tab panel content rendered when this tab is active.
   * Replaces child node rendering from Display-Tabs/Display-Pills `.cshtml`
   * where children were matched by `ContainerId == options.TabNId`.
   */
  content?: React.ReactNode;
}

/**
 * Props for the `TabNav` component.
 *
 * Maps every configurable property from the C# `PcTabNavOptions` class
 * (`PcTabNav.cs` lines 23–98), plus React-specific controlled/uncontrolled
 * mode props.
 */
export interface TabNavProps {
  /**
   * Conditional rendering toggle.
   * Maps to `@jsonProperty is_visible` (PcTabNavOptions.IsVisible).
   * @default true
   */
  isVisible?: boolean;

  /**
   * Number of visible tabs (1–7).
   * Maps to `@jsonProperty visible_tabs` (PcTabNavOptions.VisibleTabs).
   * @default 2
   */
  visibleTabs?: number;

  /**
   * Render style — tabs or pills.
   * Maps to `@jsonProperty render_type` (PcTabNavOptions.RenderType).
   * @default TabNavRenderType.PILLS
   */
  renderType?: TabNavRenderType;

  /**
   * Additional CSS classes applied to the `<ul>` tab list wrapper.
   * Maps to `@jsonProperty css_class` (PcTabNavOptions.CssClass).
   */
  className?: string;

  /**
   * CSS classes applied to the tab content body wrapper.
   * Maps to `@jsonProperty body_css_class` (PcTabNavOptions.BodyCssClass).
   */
  bodyClassName?: string;

  /**
   * Tab configuration array. Each entry maps to one of the monolith's
   * seven tab slots (`tab1_id`/`tab1_label` through `tab7_id`/`tab7_label`).
   * Only the first `visibleTabs` entries are rendered.
   */
  tabs: TabConfig[];

  /**
   * Controlled mode: the currently active tab ID.
   * When provided, the component becomes controlled and delegates state
   * management to the parent. When absent, the first visible tab is
   * selected by default (uncontrolled mode).
   */
  activeTabId?: string;

  /**
   * Callback fired when the user clicks a different tab.
   * Receives the tab `id` of the newly selected tab.
   * Replaces the monolith's jQuery `$(this).tab('show')` from `service.js`.
   */
  onTabChange?: (tabId: string) => void;

  /**
   * Alternative children content.
   * Can be used as a fallback or supplement to `tabs[].content`.
   */
  children?: React.ReactNode;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Maximum number of tabs supported, matching the monolith's tab1–tab7 slots. */
const MAX_TABS = 7;

/** Minimum number of visible tabs. */
const MIN_TABS = 1;

// ---------------------------------------------------------------------------
// Style helper functions — Tailwind CSS (replacing Bootstrap)
// ---------------------------------------------------------------------------

/**
 * Returns the Tailwind CSS classes for the `<ul>` tab list container.
 *
 * Replaces:
 * - Bootstrap `.nav .nav-tabs`  → bottom border strip
 * - Bootstrap `.nav .nav-pills` → pill gap spacing
 */
function getNavClasses(renderType: TabNavRenderType): string {
  if (renderType === TabNavRenderType.TABS) {
    // Tabs: a bottom border on the wrapper — active tab's border overlaps it
    return 'border-b border-gray-200';
  }
  // Pills: horizontal gap between pill buttons
  return 'gap-1';
}

/**
 * Returns the Tailwind CSS classes for an individual tab `<button>`.
 *
 * Replaces Bootstrap `.nav-link`, `.active`, and pill/tab variants.
 *
 * **Tabs active:**   blue bottom border, blue text, transparent background
 * **Tabs inactive:** transparent border, gray text → hover: darker gray
 * **Pills active:**  blue background, white text, rounded-full
 * **Pills inactive:** gray text, transparent bg → hover: light gray bg
 *
 * Focus-visible outline follows UI guideline UI3
 * (:focus-visible not :focus — keyboard users only).
 */
function getTabButtonClasses(
  renderType: TabNavRenderType,
  isActive: boolean,
): string {
  // Shared base: sizing, font, cursor, accessible focus ring
  const base = [
    'inline-block px-4 py-2 text-sm font-medium',
    'cursor-pointer select-none',
    'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
  ].join(' ');

  if (renderType === TabNavRenderType.TABS) {
    if (isActive) {
      return [
        base,
        'text-blue-600 border-b-2 border-blue-600 -mb-px bg-transparent',
      ].join(' ');
    }
    return [
      base,
      'text-gray-500 border-b-2 border-transparent -mb-px',
      'hover:text-gray-700 hover:border-gray-300',
    ].join(' ');
  }

  // Pills variant
  if (isActive) {
    return [base, 'text-white bg-blue-600 rounded-full'].join(' ');
  }
  return [
    base,
    'text-gray-600 rounded-full',
    'hover:text-gray-800 hover:bg-gray-100',
  ].join(' ');
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * **TabNav** — React tab navigation component.
 *
 * Full replacement for the monolith's `PcTabNav` ViewComponent:
 * - `PcTabNav.cs` (component logic + PcTabNavOptions)
 * - `Display-Tabs.cshtml` (Bootstrap nav-tabs rendering)
 * - `Display-Pills.cshtml` (Bootstrap nav-pills rendering)
 * - `service.js` (jQuery tab-switching via `$(this).tab('show')`)
 * - `TabNavRenderType.cs` (TABS / PILLS enum)
 *
 * ## Features
 * - Supports both **Tabs** and **Pills** render types
 * - Renders up to **7** tab containers (matching the monolith's slots)
 * - **Controlled** (`activeTabId` + `onTabChange`) and **uncontrolled** modes
 * - **Unique DOM IDs** via `useId()` (replaces `wv-tab-@node.Id` prefix)
 * - Inactive panels are **hidden** (`display: none`), NOT unmounted,
 *   preserving child component state (matching Bootstrap tab behavior)
 * - Full **WAI-ARIA** tablist / tab / tabpanel pattern
 * - **Keyboard accessible**: `<button>` elements are natively focusable
 *   and activatable via Enter / Space
 * - **Tailwind CSS** only — zero Bootstrap, zero jQuery
 */
export default function TabNav({
  isVisible = true,
  visibleTabs = 2,
  renderType = TabNavRenderType.PILLS,
  className,
  bodyClassName,
  tabs,
  activeTabId,
  onTabChange,
  children,
}: TabNavProps): React.ReactNode {
  // ── Unique ID prefix ────────────────────────────────────────────────
  // Replaces the monolith's "wv-tab-@node.Id" pattern to prevent
  // collisions when multiple TabNav instances coexist on the same page.
  const instanceId = useId();

  // ── Clamp visible tab count ─────────────────────────────────────────
  // PcTabNavOptions.VisibleTabs was an int (1–7). Clamp to safe range.
  const clampedCount = Math.max(MIN_TABS, Math.min(visibleTabs, MAX_TABS));
  const visibleTabConfigs = tabs.slice(0, clampedCount);

  // ── Internal active-tab state (uncontrolled mode) ───────────────────
  // When no `activeTabId` prop is provided the component manages its own
  // state, defaulting to the first visible tab — matching the monolith's
  // first-tab-active behavior from Display-Tabs/Pills.cshtml.
  const [internalActiveId, setInternalActiveId] = useState<string>(
    () => visibleTabConfigs[0]?.id ?? '',
  );

  // Resolve the effective active tab ID.
  // Controlled mode: `activeTabId` prop takes precedence.
  // Uncontrolled mode: use internal state.
  const resolvedActiveId = activeTabId ?? internalActiveId;

  // If the resolved ID does not match any visible tab, fall back to
  // the first visible tab so at least one panel is always shown.
  const effectiveActiveId =
    visibleTabConfigs.some((t) => t.id === resolvedActiveId)
      ? resolvedActiveId
      : visibleTabConfigs[0]?.id ?? '';

  // ── Memoised click handler ──────────────────────────────────────────
  // Prevents unnecessary child re-renders when the parent doesn't change.
  const handleTabClick = useCallback(
    (tabId: string) => {
      setInternalActiveId(tabId);
      onTabChange?.(tabId);
    },
    [onTabChange],
  );

  // ── Early returns ───────────────────────────────────────────────────
  // Invisible component returns nothing (matching monolith's behavior
  // where `PcTabNav.InvokeAsync` returned `Content(string.Empty)` for
  // invisible components in Display mode).
  if (!isVisible) {
    return null;
  }

  // No tabs to render — bail out gracefully.
  if (visibleTabConfigs.length === 0) {
    return null;
  }

  // ── Render ──────────────────────────────────────────────────────────
  return (
    <div>
      {/* ───── Tab list ───── */}
      <ul
        className={[
          'flex list-none m-0 p-0',
          getNavClasses(renderType),
          className ?? '',
        ]
          .filter(Boolean)
          .join(' ')
          .trim()}
        role="tablist"
      >
        {visibleTabConfigs.map((tab) => {
          const isActive = effectiveActiveId === tab.id;
          return (
            <li key={tab.id} role="presentation">
              <button
                type="button"
                role="tab"
                id={`${instanceId}-${tab.id}-tab`}
                aria-controls={`${instanceId}-${tab.id}`}
                aria-selected={isActive}
                tabIndex={isActive ? 0 : -1}
                className={getTabButtonClasses(renderType, isActive)}
                onClick={() => handleTabClick(tab.id)}
              >
                {tab.label}
              </button>
            </li>
          );
        })}
      </ul>

      {/* ───── Tab panels ───── */}
      <div
        className={['mt-2', bodyClassName ?? '']
          .filter(Boolean)
          .join(' ')
          .trim()}
      >
        {visibleTabConfigs.map((tab) => {
          const isActive = effectiveActiveId === tab.id;
          return (
            <div
              key={tab.id}
              id={`${instanceId}-${tab.id}`}
              role="tabpanel"
              aria-labelledby={`${instanceId}-${tab.id}-tab`}
              // Hidden panels use display:none rather than unmounting,
              // preserving child component state (matches Bootstrap behaviour).
              className={isActive ? 'block' : 'hidden'}
              tabIndex={0}
            >
              {tab.content}
            </div>
          );
        })}

        {/* Optional children rendered alongside tab panels */}
        {children}
      </div>
    </div>
  );
}
