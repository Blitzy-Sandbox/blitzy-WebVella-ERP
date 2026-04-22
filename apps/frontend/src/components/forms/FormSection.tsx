/**
 * FormSection — Collapsible Form Section Component
 *
 * Replaces the monolith's PcSection ViewComponent
 * (WebVella.Erp.Web/Components/PcSection/).
 *
 * Implements a collapsible, card-style form section with:
 *  - Configurable heading tag (h1-h6)
 *  - Card vs non-card rendering
 *  - Collapse/expand toggle with smooth CSS transitions
 *  - Persistent collapse state via localStorage (replacing UserPreferencies)
 *  - Render mode inheritance via React Context (labelMode, fieldMode)
 *  - Visibility control
 *  - Custom CSS class support (wrapper + body)
 *
 * Composition: DynamicForm > FormSection > FormRow > FieldComponents
 *
 * Source mapping:
 *  - PcSectionOptions     → FormSectionProps interface
 *  - PcSection.TitleTag   → HeadingTag type
 *  - context.Items[…]     → FormContext consume + provide
 *  - UserPreferencies     → localStorage collapse persistence
 *  - Bootstrap collapse   → Tailwind CSS transitions
 */

import {
  useState,
  useEffect,
  useCallback,
  useMemo,
  createElement,
  type ReactNode,
} from 'react';

import {
  FormContext,
  useFormContext,
  type LabelRenderMode,
  type FieldRenderMode,
  type FormContextValue,
} from './DynamicForm';

/* ────────────────────────────────────────────────────────────────
 * Type Exports
 * ──────────────────────────────────────────────────────────────── */

/**
 * Valid HTML heading tag levels.
 *
 * Maps to PcSectionOptions.TitleTag which accepts "h1"–"h6"
 * (source: PcSection.cs line 36, default "h4").
 */
export type HeadingTag = 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6';

/**
 * Props for the FormSection component.
 *
 * Maps 1:1 to PcSectionOptions properties (PcSection.cs lines 27-61).
 */
export interface FormSectionProps {
  /**
   * Section identifier used for collapse state persistence in localStorage.
   * Maps to the monolith's node.Id used for UserPreferencies tracking
   * (PcSection.cs lines 122-193).
   */
  id?: string;

  /**
   * Section title text. When provided, renders a heading element above the body.
   * Maps to PcSectionOptions.Title (source: line 33).
   * In the monolith this could be a datasource expression — in the SPA it is
   * resolved by the parent before being passed as a prop.
   */
  title?: string;

  /**
   * HTML heading tag level for the title.
   * Default: 'h4' (source: PcSectionOptions.TitleTag, line 36).
   */
  titleTag?: HeadingTag;

  /**
   * Render the section as a card with border, background, and shadow.
   * Default: false (source: PcSectionOptions.IsCard, line 45).
   */
  isCard?: boolean;

  /**
   * Enable collapse/expand toggle on the section header.
   * Default: false (source: PcSectionOptions.IsCollapsable, line 48).
   */
  isCollapsible?: boolean;

  /**
   * Initial collapsed state. Overridden by localStorage persisted state if
   * the section has an `id`.
   * Default: false (source: PcSectionOptions.IsCollapsed, line 54).
   */
  isCollapsed?: boolean;

  /**
   * Controls visibility of the entire section. When false, renders nothing.
   * Default: true (source: PcSectionOptions.IsVisible, lines 209-222).
   */
  isVisible?: boolean;

  /**
   * Custom CSS classes applied to the root wrapper element.
   * Maps to PcSectionOptions.Class (source: line 39).
   */
  className?: string;

  /**
   * Custom CSS classes applied to the body content container.
   * Maps to PcSectionOptions.BodyClass (source: line 42).
   */
  bodyClassName?: string;

  /**
   * Override label rendering mode for all descendant field components.
   * When undefined, inherits from parent FormContext (DynamicForm or parent FormSection).
   * Maps to PcSectionOptions.LabelMode (source: lines 96-106).
   */
  labelMode?: LabelRenderMode;

  /**
   * Override field interaction mode for all descendant field components.
   * When undefined, inherits from parent FormContext (DynamicForm or parent FormSection).
   * Maps to PcSectionOptions.FieldMode (source: lines 109-119).
   */
  fieldMode?: FieldRenderMode;

  /**
   * Section content: FormRow, FieldComponents, or nested FormSections.
   */
  children?: ReactNode;
}

/* ────────────────────────────────────────────────────────────────
 * Constants
 * ──────────────────────────────────────────────────────────────── */

/**
 * localStorage key for section collapse state persistence.
 *
 * Replaces the monolith's UserPreferencies.GetComponentData(userId,
 * "WebVella.Erp.Web.Components.PcSection") storage mechanism.
 * The stored value is a JSON object:
 * `{ collapsedIds: string[], uncollapsedIds: string[] }`
 */
const STORAGE_KEY = 'wv-section-collapse';

/**
 * Shape of the persisted collapse state in localStorage.
 * Mirrors the monolith's collapsed_node_ids / uncollapsed_node_ids
 * (PcSection.cs lines 129-182).
 */
interface CollapseStorage {
  collapsedIds: string[];
  uncollapsedIds: string[];
}

/**
 * Tailwind class map for heading tag sizes.
 * Each heading tag maps to appropriate text size, weight, and color utilities.
 */
const HEADING_CLASSES: Record<HeadingTag, string> = {
  h1: 'text-2xl font-bold text-gray-900',
  h2: 'text-xl font-bold text-gray-900',
  h3: 'text-lg font-bold text-gray-900',
  h4: 'text-lg font-semibold text-gray-900',
  h5: 'text-base font-semibold text-gray-900',
  h6: 'text-sm font-semibold text-gray-900',
};

/* ────────────────────────────────────────────────────────────────
 * localStorage Helpers
 *
 * Safe read/write operations for collapse state persistence.
 * Handles missing, corrupt, or unavailable localStorage gracefully.
 * ──────────────────────────────────────────────────────────────── */

/**
 * Read the current collapse storage from localStorage.
 * Returns a valid CollapseStorage object even if storage is empty or corrupt.
 */
function readCollapseStorage(): CollapseStorage {
  const defaultStorage: CollapseStorage = {
    collapsedIds: [],
    uncollapsedIds: [],
  };

  try {
    if (typeof window === 'undefined' || !window.localStorage) {
      return defaultStorage;
    }
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return defaultStorage;
    }
    const parsed = JSON.parse(raw) as Partial<CollapseStorage>;
    return {
      collapsedIds: Array.isArray(parsed.collapsedIds)
        ? parsed.collapsedIds
        : [],
      uncollapsedIds: Array.isArray(parsed.uncollapsedIds)
        ? parsed.uncollapsedIds
        : [],
    };
  } catch {
    return defaultStorage;
  }
}

/**
 * Write updated collapse storage to localStorage.
 * Silently fails if localStorage is unavailable.
 */
function writeCollapseStorage(storage: CollapseStorage): void {
  try {
    if (typeof window === 'undefined' || !window.localStorage) {
      return;
    }
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(storage));
  } catch {
    /* localStorage may be full or disabled — fail silently */
  }
}

/**
 * Read the persisted collapsed state for a specific section ID.
 *
 * Returns:
 *  - `true`  if the section ID is in collapsedIds
 *  - `false` if the section ID is in uncollapsedIds
 *  - `null`  if no persisted state exists (use prop default)
 *
 * This replicates PcSection.cs lines 183-189 logic:
 *   if (collapsedNodeIds.Contains(node.Id)) → true
 *   else if (uncollapsedNodeIds.Contains(node.Id)) → false
 */
function getPersistedCollapseState(sectionId: string): boolean | null {
  const storage = readCollapseStorage();
  if (storage.collapsedIds.includes(sectionId)) {
    return true;
  }
  if (storage.uncollapsedIds.includes(sectionId)) {
    return false;
  }
  return null;
}

/**
 * Persist the collapsed state for a specific section ID.
 *
 * Adds the ID to the appropriate list and removes it from the other,
 * mirroring the monolith's UserPreferencies mutation pattern.
 */
function persistCollapseState(sectionId: string, isCollapsed: boolean): void {
  const storage = readCollapseStorage();

  if (isCollapsed) {
    /* Add to collapsedIds, remove from uncollapsedIds */
    if (!storage.collapsedIds.includes(sectionId)) {
      storage.collapsedIds.push(sectionId);
    }
    storage.uncollapsedIds = storage.uncollapsedIds.filter(
      (id) => id !== sectionId,
    );
  } else {
    /* Add to uncollapsedIds, remove from collapsedIds */
    if (!storage.uncollapsedIds.includes(sectionId)) {
      storage.uncollapsedIds.push(sectionId);
    }
    storage.collapsedIds = storage.collapsedIds.filter(
      (id) => id !== sectionId,
    );
  }

  writeCollapseStorage(storage);
}

/* ────────────────────────────────────────────────────────────────
 * ChevronIcon — Collapse/Expand Indicator
 *
 * Inline SVG chevron that rotates to indicate collapsed/expanded state.
 * Uses currentColor for theming and CSS transitions for smooth rotation.
 * ──────────────────────────────────────────────────────────────── */

function ChevronIcon({ isExpanded }: { isExpanded: boolean }): ReactNode {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
      className={[
        'w-5 h-5 text-gray-400 transition-transform duration-200',
        isExpanded ? 'rotate-90' : 'rotate-0',
      ].join(' ')}
    >
      <path
        fillRule="evenodd"
        d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
        clipRule="evenodd"
      />
    </svg>
  );
}

/* ────────────────────────────────────────────────────────────────
 * FormSection Component
 *
 * Default export — collapsible form section wrapping child components.
 * ──────────────────────────────────────────────────────────────── */

function FormSection({
  id,
  title,
  titleTag = 'h4',
  isCard = false,
  isCollapsible = false,
  isCollapsed: isCollapsedProp = false,
  isVisible = true,
  className,
  bodyClassName,
  labelMode,
  fieldMode,
  children,
}: FormSectionProps): ReactNode {
  /* ── 1. Consume parent FormContext (PcSection.cs lines 96-119) ── */
  const parentContext = useFormContext();

  /* ── 2. Resolve effective render modes ──────────────────────── */

  /*
   * Inheritance logic from PcSection.cs lines 96-119:
   *  - If prop is explicitly set → use it
   *  - Else if parent context has a value → inherit
   *  - Else → default ('stacked' for labels, 'form' for fields)
   *
   * Since DynamicForm.tsx already defaults to 'stacked'/'form' in the
   * context, the fallback chain is: explicit prop → parent context value.
   */
  const effectiveLabelMode: LabelRenderMode =
    labelMode ?? parentContext.labelMode;
  const effectiveFieldMode: FieldRenderMode =
    fieldMode ?? parentContext.fieldMode;

  /* ── 3. Build overridden context value (PcSection.cs lines 239-240) ── */
  const contextValue = useMemo<FormContextValue>(
    () => ({
      labelMode: effectiveLabelMode,
      fieldMode: effectiveFieldMode,
      formId: parentContext.formId,
      formName: parentContext.formName,
    }),
    [
      effectiveLabelMode,
      effectiveFieldMode,
      parentContext.formId,
      parentContext.formName,
    ],
  );

  /* ── 4. Collapse state management ──────────────────────────── */

  /*
   * Initial state: use prop default. The useEffect below will override
   * this from localStorage once the component mounts (client-side only).
   */
  const [collapsed, setCollapsed] = useState<boolean>(isCollapsedProp);

  /*
   * On mount: restore collapse state from localStorage.
   * Replicates PcSection.cs lines 122-193 (UserPreferencies).
   *
   * Only runs when the section has an ID (required for tracking).
   * When no ID is provided, the section uses only the prop default
   * and collapse state is not persisted.
   */
  useEffect(() => {
    if (!id) {
      return;
    }
    const persisted = getPersistedCollapseState(id);
    if (persisted !== null) {
      setCollapsed(persisted);
    }
  }, [id]);

  /*
   * Toggle handler: flip collapsed state and persist to localStorage.
   * Memoized with useCallback to avoid unnecessary re-renders of
   * child components receiving this handler via props or context.
   */
  const toggleCollapse = useCallback(() => {
    setCollapsed((prev) => {
      const next = !prev;
      if (id) {
        persistCollapseState(id, next);
      }
      return next;
    });
  }, [id]);

  /* ── 5. Visibility gate (PcSection.cs lines 209-222) ───────── */
  if (isVisible === false) {
    return null;
  }

  /* ── 6. Compute CSS classes ─────────────────────────────────── */

  /* Root wrapper classes */
  const rootClasses = [
    isCard
      ? 'rounded-lg border border-gray-200 bg-white shadow-sm'
      : 'mb-4',
    className ?? '',
  ]
    .filter(Boolean)
    .join(' ');

  /* Header classes */
  const headerClasses = [
    isCollapsible
      ? 'cursor-pointer flex items-center justify-between w-full'
      : 'flex items-center w-full',
    isCard ? 'px-4 py-3 border-b border-gray-200' : 'py-2',
  ].join(' ');

  /* Body wrapper classes for collapse animation */
  const bodyWrapperClasses = isCollapsible
    ? [
        'overflow-hidden transition-all duration-300',
        collapsed ? 'max-h-0 opacity-0' : 'max-h-[2000px] opacity-100',
      ].join(' ')
    : '';

  /* Body content classes */
  const bodyContentClasses = [
    isCard ? 'px-4 py-4' : 'py-2',
    bodyClassName ?? '',
  ]
    .filter(Boolean)
    .join(' ');

  /* ── 7. Render ─────────────────────────────────────────────── */

  /*
   * Dynamic heading element rendering using React.createElement.
   * This supports the configurable TitleTag (h1-h6) from PcSectionOptions.
   */
  const renderTitle = (): ReactNode => {
    if (!title) {
      return null;
    }

    const headingContent = createElement(
      titleTag,
      {
        className: HEADING_CLASSES[titleTag],
      },
      title,
    );

    if (isCollapsible) {
      return (
        <button
          type="button"
          className={headerClasses}
          onClick={toggleCollapse}
          aria-expanded={!collapsed}
          aria-controls={id ? `${id}-body` : undefined}
        >
          {headingContent}
          <ChevronIcon isExpanded={!collapsed} />
        </button>
      );
    }

    return <div className={headerClasses}>{headingContent}</div>;
  };

  const bodyContent = (
    <div
      id={id ? `${id}-body` : undefined}
      role={isCollapsible ? 'region' : undefined}
      aria-labelledby={isCollapsible && title && id ? `${id}-title` : undefined}
      className={bodyContentClasses}
    >
      <FormContext.Provider value={contextValue}>
        {children}
      </FormContext.Provider>
    </div>
  );

  return (
    <section
      id={id}
      className={rootClasses || undefined}
      data-collapsed={isCollapsible ? collapsed : undefined}
    >
      {renderTitle()}

      {isCollapsible ? (
        <div className={bodyWrapperClasses}>{bodyContent}</div>
      ) : (
        bodyContent
      )}
    </section>
  );
}

export default FormSection;
