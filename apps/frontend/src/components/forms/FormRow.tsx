import { useMemo, Children, type ReactNode, type JSX } from 'react';

// ---------------------------------------------------------------------------
// Type Definitions
// ---------------------------------------------------------------------------

/**
 * Vertical alignment of items within the row container.
 * Maps to Bootstrap 4's `align-items-*` → Tailwind `items-*`.
 * Source: PcRowOptions.FlexVerticalAlignment (WvFlexVerticalAlignmentType enum).
 */
export type FlexVerticalAlignment = 'none' | 'start' | 'center' | 'end' | 'stretch';

/**
 * Horizontal alignment of items within the row container.
 * Maps to Bootstrap 4's `justify-content-*` → Tailwind `justify-items-*`.
 * Source: PcRowOptions.FlexHorizontalAlignment (WvFlexHorizontalAlignmentType enum).
 */
export type FlexHorizontalAlignment = 'none' | 'start' | 'center' | 'end' | 'between' | 'around';

/**
 * Per-column self-alignment along the cross axis.
 * Maps to Bootstrap 4's `align-self-*` → Tailwind `self-*`.
 * Source: PcRowOptions.Container{N}FlexSelfAlign (WvFlexSelfAlignType enum).
 */
export type FlexSelfAlign = 'none' | 'start' | 'center' | 'end' | 'stretch';

/**
 * Configuration for a single column container within the row.
 * Each column supports responsive breakpoint spans, offsets, self-alignment,
 * and ordering. Mirrors the per-container properties from PcRowOptions
 * (Container1–Container12, each with 13 identical properties).
 */
export interface ColumnConfig {
  /** Unique container identifier. Default: `column${index+1}`. */
  id?: string;
  /**
   * Base column span (1–12). `0` = auto equal distribution (Bootstrap `col`).
   * `null`/`undefined` = no span class (inherits grid flow).
   * Default: 0 (matching Container{N}Span default in PcRowOptions).
   */
  span?: number | null;
  /** Column span at the `sm` breakpoint. `null` = no responsive override. */
  spanSm?: number | null;
  /** Column span at the `md` breakpoint. `null` = no responsive override. */
  spanMd?: number | null;
  /** Column span at the `lg` breakpoint. `null` = no responsive override. */
  spanLg?: number | null;
  /** Column span at the `xl` breakpoint. `null` = no responsive override. */
  spanXl?: number | null;
  /**
   * Base column offset (0-indexed). Translates to CSS Grid `col-start-{N+1}`.
   * `null`/`undefined` = no offset. Default: null.
   */
  offset?: number | null;
  /** Column offset at the `sm` breakpoint. */
  offsetSm?: number | null;
  /** Column offset at the `md` breakpoint. */
  offsetMd?: number | null;
  /** Column offset at the `lg` breakpoint. */
  offsetLg?: number | null;
  /** Column offset at the `xl` breakpoint. */
  offsetXl?: number | null;
  /** Per-column self-alignment. Default: 'none'. */
  flexSelfAlign?: FlexSelfAlign;
  /** Per-column ordering. `null` = no explicit order. */
  flexOrder?: number | null;
}

/**
 * Props for the FormRow component.
 * Replaces PcRowOptions from the monolith's PcRow ViewComponent.
 */
export interface FormRowProps {
  /**
   * Number of visible columns to render (1–12).
   * Default: 2 (matching PcRowOptions.VisibleColumns).
   */
  visibleColumns?: number;
  /**
   * Per-column configuration array. Up to 12 entries.
   * Columns beyond `visibleColumns` count are ignored.
   * Missing entries use default configuration (auto span).
   */
  columns?: ColumnConfig[];
  /** Row-level vertical alignment. Default: 'none'. */
  flexVerticalAlignment?: FlexVerticalAlignment;
  /** Row-level horizontal alignment. Default: 'none'. */
  flexHorizontalAlignment?: FlexHorizontalAlignment;
  /** Remove gap between columns. Default: false. */
  noGutters?: boolean;
  /** Custom CSS class applied to the row container. */
  className?: string;
  /**
   * Flat children distributed across visible columns round-robin.
   * Child at index `i` goes to column `i % visibleColumns`.
   * Replaces server-side ContainerId filtering from Display.cshtml.
   */
  children?: ReactNode;
  /**
   * Explicit per-column children. `columnChildren[0]` renders in column 1,
   * `columnChildren[1]` in column 2, etc. Takes precedence over `children`.
   */
  columnChildren?: ReactNode[];
  /** DOM id for the row container element. */
  id?: string;
}

// ---------------------------------------------------------------------------
// Alignment Lookup Maps
// ---------------------------------------------------------------------------

/**
 * Maps FlexVerticalAlignment values to Tailwind CSS `items-*` classes.
 * Equivalent to Bootstrap 4 `align-items-*` on the row flex container.
 */
const VERTICAL_ALIGNMENT_MAP: Record<FlexVerticalAlignment, string> = {
  none: '',
  start: 'items-start',
  center: 'items-center',
  end: 'items-end',
  stretch: 'items-stretch',
};

/**
 * Maps FlexHorizontalAlignment values to Tailwind CSS classes.
 * - start/center/end → `justify-items-*` (item alignment within grid cells)
 * - between/around → `justify-between`/`justify-around` (justify-content,
 *   limited visual effect with 1fr grid tracks but CSS property is applied)
 */
const HORIZONTAL_ALIGNMENT_MAP: Record<FlexHorizontalAlignment, string> = {
  none: '',
  start: 'justify-items-start',
  center: 'justify-items-center',
  end: 'justify-items-end',
  between: 'justify-between',
  around: 'justify-around',
};

/**
 * Maps FlexSelfAlign values to Tailwind CSS `self-*` classes.
 * Equivalent to Bootstrap 4 `align-self-*` on individual columns.
 */
const SELF_ALIGN_MAP: Record<FlexSelfAlign, string> = {
  none: '',
  start: 'self-start',
  center: 'self-center',
  end: 'self-end',
  stretch: 'self-stretch',
};

// ---------------------------------------------------------------------------
// Helper Functions
// ---------------------------------------------------------------------------

/**
 * Converts a span value to the corresponding Tailwind CSS Grid `col-span-*` class.
 *
 * - `span === 0`: auto-width — computes equal distribution `col-span-{12/visibleColumns}`,
 *   replicating Bootstrap's auto `col` class that divides equally.
 * - `span === null || span === undefined`: no span class (inherits grid flow).
 * - `span >= 1 && span <= 12`: explicit `col-span-{span}`.
 *
 * @param span - Column span value (0 = auto, null/undefined = none, 1–12 = explicit).
 * @param visibleColumns - Total visible columns for auto-calculation context.
 * @param breakpoint - Optional responsive breakpoint prefix (e.g., 'sm', 'md').
 * @returns Tailwind class string or empty string.
 */
function getSpanClass(
  span: number | null | undefined,
  visibleColumns: number,
  breakpoint?: string,
): string {
  if (span === null || span === undefined) {
    return '';
  }

  const prefix = breakpoint ? `${breakpoint}:` : '';

  if (span === 0) {
    // Auto: compute equal distribution across 12-column grid
    const autoSpan = Math.max(1, Math.floor(12 / visibleColumns));
    return `${prefix}col-span-${autoSpan}`;
  }

  if (span >= 1 && span <= 12) {
    return `${prefix}col-span-${span}`;
  }

  return '';
}

/**
 * Converts an offset value to a Tailwind CSS Grid `col-start-*` class.
 *
 * Bootstrap offsets are 0-indexed (offset-0 = no offset, offset-1 = skip 1 column).
 * CSS Grid `col-start` is 1-indexed, so we add 1 to convert.
 *
 * @param offset - Column offset (0-indexed). `null`/`undefined` = no offset.
 * @param breakpoint - Optional responsive breakpoint prefix.
 * @returns Tailwind class string or empty string.
 */
function getOffsetClass(
  offset: number | null | undefined,
  breakpoint?: string,
): string {
  if (offset === null || offset === undefined) {
    return '';
  }

  const colStart = offset + 1;

  if (colStart < 1 || colStart > 13) {
    return '';
  }

  const prefix = breakpoint ? `${breakpoint}:` : '';
  return `${prefix}col-start-${colStart}`;
}

/**
 * Converts a FlexSelfAlign value to the Tailwind CSS `self-*` class.
 *
 * @param align - Self-alignment value. `'none'`/`undefined` = no class.
 * @returns Tailwind class string or empty string.
 */
function getSelfAlignClass(align: FlexSelfAlign | undefined): string {
  if (!align || align === 'none') {
    return '';
  }
  return SELF_ALIGN_MAP[align] || '';
}

/**
 * Converts a flex order value to a Tailwind CSS `order-*` class.
 *
 * @param order - Numeric order. `null`/`undefined` = no explicit order.
 * @returns Tailwind class string or empty string.
 */
function getOrderClass(order: number | null | undefined): string {
  if (order === null || order === undefined) {
    return '';
  }
  return `order-${order}`;
}

/**
 * Builds the complete Tailwind class string for a single column container.
 *
 * Applies base span, responsive spans, base offset, responsive offsets,
 * self-alignment, and flex order. When span defaults to 0 (auto), computes
 * equal distribution across the 12-column grid based on `visibleColumns`.
 *
 * @param config - Column configuration object.
 * @param visibleColumns - Total visible columns for auto-span calculation.
 * @param _index - Column index (0-based), reserved for future extensions.
 * @returns Space-separated Tailwind class string.
 */
function buildColumnClasses(
  config: ColumnConfig,
  visibleColumns: number,
  _index: number,
): string {
  const classes: string[] = [];

  // Base and responsive span classes
  // Default span is 0 (auto equal distribution) matching PcRowOptions.Container{N}Span
  const baseSpan = getSpanClass(config.span ?? 0, visibleColumns);
  if (baseSpan) classes.push(baseSpan);

  const smSpan = getSpanClass(config.spanSm, visibleColumns, 'sm');
  if (smSpan) classes.push(smSpan);

  const mdSpan = getSpanClass(config.spanMd, visibleColumns, 'md');
  if (mdSpan) classes.push(mdSpan);

  const lgSpan = getSpanClass(config.spanLg, visibleColumns, 'lg');
  if (lgSpan) classes.push(lgSpan);

  const xlSpan = getSpanClass(config.spanXl, visibleColumns, 'xl');
  if (xlSpan) classes.push(xlSpan);

  // Base and responsive offset classes
  const baseOffset = getOffsetClass(config.offset);
  if (baseOffset) classes.push(baseOffset);

  const smOffset = getOffsetClass(config.offsetSm, 'sm');
  if (smOffset) classes.push(smOffset);

  const mdOffset = getOffsetClass(config.offsetMd, 'md');
  if (mdOffset) classes.push(mdOffset);

  const lgOffset = getOffsetClass(config.offsetLg, 'lg');
  if (lgOffset) classes.push(lgOffset);

  const xlOffset = getOffsetClass(config.offsetXl, 'xl');
  if (xlOffset) classes.push(xlOffset);

  // Self-alignment
  const selfAlignClass = getSelfAlignClass(config.flexSelfAlign);
  if (selfAlignClass) classes.push(selfAlignClass);

  // Flex order
  const orderClass = getOrderClass(config.flexOrder);
  if (orderClass) classes.push(orderClass);

  // Defensive: prevent grid child overflow
  classes.push('min-w-0');

  return classes.join(' ');
}

// ---------------------------------------------------------------------------
// FormRow Component
// ---------------------------------------------------------------------------

/**
 * 12-column CSS Grid row component replacing the monolith's `PcRow` ViewComponent.
 *
 * Renders a `grid grid-cols-12` container with `visibleColumns` column slots.
 * Each column supports responsive breakpoint spans, offsets, self-alignment,
 * and ordering — all via Tailwind CSS utility classes (zero Bootstrap).
 *
 * Children are distributed across columns either explicitly via `columnChildren`
 * (per-column ReactNode array) or automatically via round-robin distribution
 * of the flat `children` prop (replacing the server-side ContainerId filtering
 * pattern from the monolith's Display.cshtml).
 *
 * @example
 * ```tsx
 * <FormRow visibleColumns={3} noGutters={false}>
 *   <TextField label="First Name" />
 *   <TextField label="Last Name" />
 *   <TextField label="Email" />
 * </FormRow>
 * ```
 *
 * @example
 * ```tsx
 * <FormRow
 *   visibleColumns={2}
 *   columns={[
 *     { span: 8, spanMd: 6 },
 *     { span: 4, spanMd: 6 },
 *   ]}
 *   columnChildren={[
 *     <TextField label="Description" />,
 *     <TextField label="Status" />,
 *   ]}
 * />
 * ```
 */
function FormRow({
  visibleColumns = 2,
  columns = [],
  flexVerticalAlignment = 'none',
  flexHorizontalAlignment = 'none',
  noGutters = false,
  className,
  children,
  columnChildren,
  id,
}: FormRowProps): JSX.Element {
  // Clamp visible columns to the valid 1–12 range (matching 12 containers in PcRowOptions)
  const colCount = Math.max(1, Math.min(12, visibleColumns));

  // Memoize the row container class string to avoid recalculation on every render
  const rowClasses = useMemo(() => {
    const parts: string[] = ['grid', 'grid-cols-12'];

    // Gap between columns: removed when noGutters is true (Bootstrap `no-gutters`)
    parts.push(noGutters ? 'gap-0' : 'gap-4');

    // Vertical alignment of items within the row
    const vAlign = VERTICAL_ALIGNMENT_MAP[flexVerticalAlignment];
    if (vAlign) {
      parts.push(vAlign);
    }

    // Horizontal alignment / justification of items
    const hAlign = HORIZONTAL_ALIGNMENT_MAP[flexHorizontalAlignment];
    if (hAlign) {
      parts.push(hAlign);
    }

    // User-provided custom class
    if (className) {
      parts.push(className);
    }

    return parts.join(' ');
  }, [noGutters, flexVerticalAlignment, flexHorizontalAlignment, className]);

  // Convert flat children to an array for round-robin distribution
  const childArray = useMemo(() => {
    if (columnChildren && columnChildren.length > 0) {
      // When explicit columnChildren are provided, skip flat children processing
      return null;
    }
    return Children.toArray(children);
  }, [children, columnChildren]);

  // Distribute children into column buckets using round-robin assignment.
  // This replaces the server-side `ContainerId` filtering from Display.cshtml
  // where each column filters `node.Nodes.FindAll(x => x.ContainerId == ...)`.
  const columnBuckets = useMemo((): ReactNode[][] => {
    // Initialize empty buckets for each visible column
    const buckets: ReactNode[][] = Array.from({ length: colCount }, () => []);

    if (columnChildren && columnChildren.length > 0) {
      // Explicit per-column children: place each directly into its column
      for (let i = 0; i < colCount; i++) {
        if (i < columnChildren.length && columnChildren[i] != null) {
          buckets[i].push(columnChildren[i]);
        }
      }
      return buckets;
    }

    if (childArray && childArray.length > 0) {
      // Round-robin distribution: child[i] → column[i % colCount]
      childArray.forEach((child, i) => {
        buckets[i % colCount].push(child);
      });
    }

    return buckets;
  }, [childArray, columnChildren, colCount]);

  // Memoize the rendered column elements to avoid unnecessary re-renders
  const renderedColumns = useMemo(() => {
    return Array.from({ length: colCount }, (_, index) => {
      // Retrieve column config or use an empty object for defaults
      const config: ColumnConfig = columns[index] || {};

      // Build the complete Tailwind class string for this column
      const colClasses = buildColumnClasses(config, colCount, index);

      // Column id: from config or generated default matching PcRowOptions.Container{N}Id
      const colId = config.id || `column${index + 1}`;

      // Get content for this column from the distributed buckets
      const content = columnBuckets[index];

      return (
        <div
          key={colId}
          id={colId}
          className={colClasses}
        >
          {content && content.length > 0 ? content : null}
        </div>
      );
    });
  }, [colCount, columns, columnBuckets]);

  return (
    <div id={id} className={rowClasses}>
      {renderedColumns}
    </div>
  );
}

export default FormRow;
