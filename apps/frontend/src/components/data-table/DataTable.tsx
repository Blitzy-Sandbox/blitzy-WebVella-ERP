/**
 * DataTable — Main Data Grid Component
 *
 * Replaces the monolith's PcGrid ViewComponent
 * (`WebVella.Erp.Web/Components/PcGrid/`) with a React component built
 * on TanStack Table v8. This is the primary data-display component for
 * tabular / grid layouts across the entire ERP frontend.
 *
 * Source mapping:
 *  - PcGridOptions model         → DataTableProps interface
 *  - Container1…Container12 cols → DataTableColumn[] array prop
 *  - Display.cshtml <wv-grid>    → Tailwind-styled <table>
 *  - service.js column mgmt      → useMemo column mapping
 *  - URL query-string paging     → useSearchParams from React Router 7
 *
 * Key design decisions:
 *  - Generic `<T>` lets callers get type-safe cell accessors.
 *  - URL search-params are the default paging/sort source-of-truth,
 *    matching the monolith's HttpContext.Request.Query[pageKey] pattern.
 *  - When explicit `currentPage` / `onPageChange` props are provided
 *    they take precedence over URL state.
 *  - All styling uses Tailwind CSS 4 utility classes — zero Bootstrap.
 *
 * @module DataTable
 */

import {
  useMemo,
  useCallback,
  useState,
  type ReactElement,
  type ReactNode,
} from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  useReactTable,
  getCoreRowModel,
  getSortedRowModel,
  flexRender,
  type ColumnDef,
  type SortingState,
} from '@tanstack/react-table';

/* ════════════════════════════════════════════════════════════════
 * Internal helpers & constants
 * ════════════════════════════════════════════════════════════════ */

/** Metadata transported through TanStack column definitions for rendering. */
interface DataTableColumnMeta {
  width?: string;
  noWrap?: boolean;
  className?: string;
  verticalAlign?: 'top' | 'middle' | 'bottom';
  horizontalAlign?: 'left' | 'center' | 'right';
  searchable?: boolean;
  name?: string;
}

/** Resolves a vertical-alignment value to a Tailwind class. */
function verticalAlignClass(align?: 'top' | 'middle' | 'bottom'): string {
  switch (align) {
    case 'top':
      return 'align-top';
    case 'middle':
      return 'align-middle';
    case 'bottom':
      return 'align-bottom';
    default:
      return '';
  }
}

/** Resolves a horizontal-alignment value to a Tailwind logical-property class. */
function horizontalAlignClass(align?: 'left' | 'center' | 'right'): string {
  switch (align) {
    case 'left':
      return 'text-start';
    case 'center':
      return 'text-center';
    case 'right':
      return 'text-end';
    default:
      return '';
  }
}

/**
 * Builds responsive wrapper classes from the breakpoint enum.
 *
 * Bootstrap `table-responsive-{bp}` → Tailwind equivalent:
 *  • `'none'` → no overflow handling
 *  • `'sm'`   → scrollable below `sm`, visible above
 *  • `'md'`   → scrollable below `md`, visible above
 *  • `'lg'`   → scrollable below `lg`, visible above
 *  • `'xl'`   → scrollable below `xl`, visible above
 */
function responsiveWrapperClass(
  bp?: 'none' | 'sm' | 'md' | 'lg' | 'xl',
): string {
  switch (bp) {
    case 'sm':
      return 'overflow-x-auto sm:overflow-visible';
    case 'md':
      return 'overflow-x-auto md:overflow-visible';
    case 'lg':
      return 'overflow-x-auto lg:overflow-visible';
    case 'xl':
      return 'overflow-x-auto xl:overflow-visible';
    case 'none':
    default:
      return '';
  }
}

/** Available page-size options presented in the page-size selector. */
const PAGE_SIZE_OPTIONS = [5, 10, 15, 20, 25, 50, 100] as const;

/**
 * Generates an array of page indicators for the pagination bar.
 * Returns page numbers interspersed with `'ellipsis'` sentinels when
 * the total number of pages exceeds the visible-window threshold.
 */
function buildPageNumbers(
  current: number,
  total: number,
): (number | 'ellipsis')[] {
  if (total <= 7) {
    return Array.from({ length: total }, (_, i) => i + 1);
  }

  const pages: (number | 'ellipsis')[] = [1];

  if (current > 3) {
    pages.push('ellipsis');
  }

  const start = Math.max(2, current - 1);
  const end = Math.min(total - 1, current + 1);
  for (let i = start; i <= end; i++) {
    pages.push(i);
  }

  if (current < total - 2) {
    pages.push('ellipsis');
  }

  if (total > 1) {
    pages.push(total);
  }

  return pages;
}

/** Joins non-empty class-name fragments. */
function cn(...parts: (string | false | undefined | null)[]): string {
  return parts.filter(Boolean).join(' ');
}

/* ════════════════════════════════════════════════════════════════
 * Public interfaces
 * ════════════════════════════════════════════════════════════════ */

/**
 * Column definition for the DataTable — replaces the monolith's
 * `Container1…Container12` option pattern from PcGridOptions.
 *
 * @typeParam T — Row-data type.  Defaults to `unknown` for untyped usage.
 */
export interface DataTableColumn<T = unknown> {
  /** Unique column identifier. */
  id: string;

  /** Column name used in URL-based sorting (maps to `container{N}_name`). */
  name?: string;

  /** Column header text (maps to `container{N}_label`). */
  label: string;

  /** CSS width — e.g. `'200px'`, `'25%'` (maps to `container{N}_width`). */
  width?: string;

  /** Whether the column supports sorting (maps to `container{N}_sortable`). */
  sortable?: boolean;

  /** Whether the column is searchable (maps to `container{N}_searchable`). */
  searchable?: boolean;

  /** Prevent text wrapping — applies `whitespace-nowrap` (maps to `container{N}_nowrap`). */
  noWrap?: boolean;

  /** Additional CSS class for the column (maps to `container{N}_class`). */
  className?: string;

  /**
   * Vertical alignment applied to every cell in this column.
   * Maps to `container{N}_vertical_align` (WvVerticalAlignmentType).
   */
  verticalAlign?: 'top' | 'middle' | 'bottom';

  /**
   * Horizontal alignment applied to every cell in this column.
   * Maps to `container{N}_horizontal_align` (WvHorizontalAlignmentType).
   */
  horizontalAlign?: 'left' | 'center' | 'right';

  /**
   * Custom cell renderer.
   * Receives the extracted cell value and the full row record.
   */
  cell?: (value: unknown, record: T) => ReactNode;

  /** Data-object key used to extract the cell value for this column. */
  accessorKey?: string;

  /** Accessor function that extracts the cell value from a row record. */
  accessorFn?: (record: T) => unknown;
}

/**
 * Props for the `DataTable` component — replaces PcGridOptions
 * from `PcGrid.cs`.  Every option documented in the monolith's grid
 * component has a direct counterpart here.
 *
 * @typeParam T — Row-data type.  Defaults to `Record<string, unknown>`.
 */
export interface DataTableProps<T = Record<string, unknown>> {
  /** Data records to display (parent passes resolved data). */
  data: T[];

  /**
   * Column definitions — replaces the `Container1…Container12` pattern.
   * The array length determines the number of visible columns.
   */
  columns: DataTableColumn<T>[];

  /**
   * Total record count for server-side pagination.
   * Maps to `EntityRecordList.TotalCount` / `ViewBag.TotalCount`.
   */
  totalCount?: number;

  /** Page size (default `10`).  `0` disables pagination.  Maps to `page_size`. */
  pageSize?: number;

  /** Current page (1-indexed).  When provided, overrides URL param. */
  currentPage?: number;

  /** Callback fired when the active page changes. */
  onPageChange?: (page: number) => void;

  /** Callback fired when the page-size selection changes. */
  onPageSizeChange?: (size: number) => void;

  /** Callback fired when sorting changes. */
  onSortChange?: (sortBy: string, sortOrder: 'asc' | 'desc') => void;

  /** Show table header row (default `true`).  Maps to `has_thead`. */
  showHeader?: boolean;

  /** Show table footer with pagination (default `true`).  Maps to `has_tfoot`. */
  showFooter?: boolean;

  /** Hide the total-count label in the footer (default `false`).  Maps to `no_total`. */
  hideTotal?: boolean;

  /** Text displayed when `data` is empty (default `"No records"`).  Maps to `empty_text`. */
  emptyText?: string;

  /** Striped rows (default `false`).  Maps to `striped`. */
  striped?: boolean;

  /** Compact / small table (default `false`).  Maps to `small`. */
  small?: boolean;

  /** Bordered table (default `false`).  Maps to `bordered`. */
  bordered?: boolean;

  /** Borderless table (default `false`).  Maps to `borderless`. */
  borderless?: boolean;

  /** Hover highlight on rows (default `false`).  Maps to `hover`. */
  hover?: boolean;

  /** Responsive overflow breakpoint.  Maps to `responsive_breakpoint`. */
  responsiveBreakpoint?: 'none' | 'sm' | 'md' | 'lg' | 'xl';

  /** HTML `id` attribute applied to the `<table>` element. */
  id?: string;

  /**
   * Query-param namespace prefix for multi-grid pages.
   * Maps to `prefix` in PcGridOptions.
   */
  prefix?: string;

  /** Optional table name. */
  name?: string;

  /** Additional CSS class applied to the `<table>` element. */
  className?: string;

  /** When `true`, a translucent loading overlay is shown over the table. */
  loading?: boolean;

  /** URL search-param key for sort column (default `"sortBy"`). */
  sortByParam?: string;

  /** URL search-param key for sort direction (default `"sortOrder"`). */
  sortOrderParam?: string;

  /** URL search-param key for current page (default `"page"`). */
  pageParam?: string;

  /** URL search-param key for page size (default `"pageSize"`). */
  pageSizeParam?: string;

  /**
   * Optional `data-testid` value applied to each `<tr>` in the body.
   * Enables targeted E2E test selectors without coupling tests to
   * internal class names.
   */
  rowTestId?: string;

  /**
   * Callback fired when a body row is clicked.
   * Receives the original row data and the row index.
   * Adds `cursor-pointer` styling to body rows when provided.
   */
  onRowClick?: (record: T, index: number) => void;
}

/* ════════════════════════════════════════════════════════════════
 * Component implementation
 * ════════════════════════════════════════════════════════════════ */

/**
 * Generic data-table component powered by TanStack Table v8.
 *
 * Supports server-side pagination and sorting via URL search-params
 * (matching the monolith's query-string pattern) as well as explicit
 * controlled-mode through callback props.
 *
 * @typeParam T — Row-data type.  Defaults to `Record<string, unknown>`.
 */
export function DataTable<
  T extends Record<string, unknown> = Record<string, unknown>,
>(props: DataTableProps<T>): ReactElement {
  /* ── Destructure props with sensible defaults ─────────────── */
  const {
    data,
    columns,
    totalCount: totalCountProp,
    pageSize: pageSizeProp = 10,
    currentPage: currentPageProp,
    onPageChange,
    onPageSizeChange,
    onSortChange,
    showHeader = true,
    showFooter = true,
    hideTotal = false,
    emptyText = 'No records',
    striped = false,
    small = false,
    bordered = false,
    borderless = false,
    hover = false,
    responsiveBreakpoint = 'none',
    id,
    prefix = '',
    name,
    className = '',
    loading = false,
    sortByParam = 'sortBy',
    sortOrderParam = 'sortOrder',
    pageParam = 'page',
    pageSizeParam = 'pageSize',
    rowTestId,
    onRowClick,
  } = props;

  /* ── URL search-param state (matches monolith pattern) ───── */
  const [searchParams, setSearchParams] = useSearchParams();

  const urlPage = searchParams.get(prefix + pageParam);
  const urlPageSize = searchParams.get(prefix + pageSizeParam);
  const urlSortBy = searchParams.get(prefix + sortByParam);
  const urlSortOrder = searchParams.get(prefix + sortOrderParam);

  /* ── Derived pagination values ───────────────────────────── */
  const effectivePage = currentPageProp ?? (urlPage ? parseInt(urlPage, 10) || 1 : 1);
  const effectivePageSize = urlPageSize
    ? parseInt(urlPageSize, 10) || pageSizeProp
    : pageSizeProp;
  const effectiveTotalCount = totalCountProp ?? data.length;
  const paginationEnabled = effectivePageSize > 0;
  const pageCount = paginationEnabled
    ? Math.max(1, Math.ceil(effectiveTotalCount / effectivePageSize))
    : 1;

  /* ── Sorting state ───────────────────────────────────────── */
  const [sorting, setSorting] = useState<SortingState>(() => {
    if (urlSortBy) {
      return [{ id: urlSortBy, desc: urlSortOrder === 'desc' }];
    }
    return [];
  });

  /* ── Map DataTableColumn[] → TanStack ColumnDef[] ────────── */
  const tanstackColumns = useMemo((): ColumnDef<T, unknown>[] => {
    return columns.map((col): ColumnDef<T, unknown> => {
      /* Build common properties shared by all column types. */
      const common: Partial<ColumnDef<T, unknown>> & {
        id: string;
        header: string;
        enableSorting: boolean;
        meta: DataTableColumnMeta;
      } = {
        id: col.id,
        header: col.label,
        enableSorting: col.sortable ?? false,
        meta: {
          width: col.width,
          noWrap: col.noWrap,
          className: col.className,
          verticalAlign: col.verticalAlign,
          horizontalAlign: col.horizontalAlign,
          searchable: col.searchable,
          name: col.name,
        },
      };

      /* Custom cell renderer bridge: our (value, record) → TanStack info. */
      const cellRenderer = col.cell
        ? (info: { getValue: () => unknown; row: { original: T } }) =>
            col.cell!(info.getValue(), info.row.original)
        : undefined;

      /* Branch on accessor type to satisfy TanStack's union ColumnDef. */
      if (col.accessorFn) {
        return {
          ...common,
          accessorFn: col.accessorFn,
          ...(cellRenderer ? { cell: cellRenderer } : {}),
        } as ColumnDef<T, unknown>;
      }

      if (col.accessorKey) {
        return {
          ...common,
          accessorKey: col.accessorKey as string & keyof T,
          ...(cellRenderer ? { cell: cellRenderer } : {}),
        } as ColumnDef<T, unknown>;
      }

      /* Display-only column (no data accessor). */
      return {
        ...common,
        ...(cellRenderer ? { cell: cellRenderer } : {}),
      } as ColumnDef<T, unknown>;
    });
  }, [columns]);

  /* ── Sorting-change handler ──────────────────────────────── */
  const handleSortingChange = useCallback(
    (updaterOrValue: SortingState | ((prev: SortingState) => SortingState)) => {
      const newSorting =
        typeof updaterOrValue === 'function'
          ? updaterOrValue(sorting)
          : updaterOrValue;

      setSorting(newSorting);

      /* Sync sort state → URL params & callback. */
      if (newSorting.length > 0) {
        const { id: sortColId, desc } = newSorting[0];
        const col = columns.find((c) => c.id === sortColId);
        const sortName = col?.name || sortColId;
        const sortDirection: 'asc' | 'desc' = desc ? 'desc' : 'asc';

        setSearchParams((prev) => {
          const params = new URLSearchParams(prev);
          params.set(prefix + sortByParam, sortName);
          params.set(prefix + sortOrderParam, sortDirection);
          return params;
        });

        onSortChange?.(sortName, sortDirection);
      } else {
        setSearchParams((prev) => {
          const params = new URLSearchParams(prev);
          params.delete(prefix + sortByParam);
          params.delete(prefix + sortOrderParam);
          return params;
        });
      }
    },
    [sorting, columns, prefix, sortByParam, sortOrderParam, onSortChange, setSearchParams],
  );

  /* ── TanStack Table instance ─────────────────────────────── */
  const table = useReactTable({
    data,
    columns: tanstackColumns,
    state: { sorting },
    onSortingChange: handleSortingChange,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    manualPagination: true,
    manualSorting: true,
    pageCount,
  });

  /* ── Page-change handler ─────────────────────────────────── */
  const handlePageChange = useCallback(
    (newPage: number) => {
      const clamped = Math.max(1, Math.min(newPage, pageCount));

      setSearchParams((prev) => {
        const params = new URLSearchParams(prev);
        params.set(prefix + pageParam, String(clamped));
        return params;
      });

      onPageChange?.(clamped);
    },
    [pageCount, prefix, pageParam, onPageChange, setSearchParams],
  );

  /* ── Page-size change handler ────────────────────────────── */
  const handlePageSizeChange = useCallback(
    (newSize: number) => {
      setSearchParams((prev) => {
        const params = new URLSearchParams(prev);
        params.set(prefix + pageSizeParam, String(newSize));
        /* Reset to page 1 when page size changes. */
        params.set(prefix + pageParam, '1');
        return params;
      });

      onPageSizeChange?.(newSize);
      onPageChange?.(1);
    },
    [prefix, pageSizeParam, pageParam, onPageSizeChange, onPageChange, setSearchParams],
  );

  /* ── Computed CSS classes ─────────────────────────────────── */
  const wrapperClass = cn('relative', responsiveWrapperClass(responsiveBreakpoint));

  const cellPadding = small ? 'px-2 py-1' : 'px-4 py-3';

  const tableClass = cn(
    'w-full border-collapse text-start',
    small && 'text-sm',
    bordered && 'border border-gray-200',
    className,
  );

  /* ── Pagination range text ───────────────────────────────── */
  const rangeStart = paginationEnabled
    ? (effectivePage - 1) * effectivePageSize + 1
    : 1;
  const rangeEnd = paginationEnabled
    ? Math.min(effectivePage * effectivePageSize, effectiveTotalCount)
    : effectiveTotalCount;

  const pageNumbers = buildPageNumbers(effectivePage, pageCount);

  /* ── Shared button styles for pagination ─────────────────── */
  const paginationBtnBase =
    'inline-flex items-center justify-center rounded border border-gray-300 bg-white text-sm font-medium text-gray-700 motion-safe:transition-colors motion-safe:duration-150';
  const paginationBtnSize = small ? 'min-h-7 min-w-7 px-2 py-0.5' : 'min-h-9 min-w-9 px-3 py-1.5';
  const paginationBtnEnabled = 'hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600';
  const paginationBtnDisabled = 'cursor-not-allowed opacity-50';
  const paginationBtnActive = 'border-blue-600 bg-blue-600 text-white hover:bg-blue-700';

  /* ══════════════════════════════════════════════════════════════
   * RENDER
   * ══════════════════════════════════════════════════════════════ */
  return (
    <div className={wrapperClass}>
      {/* Loading overlay */}
      {loading && (
        <div
          className="absolute inset-0 z-10 flex items-center justify-center bg-white/70"
          role="status"
          aria-live="polite"
        >
          <span className="text-sm text-gray-500">Loading…</span>
        </div>
      )}

      <table
        id={id}
        data-name={name || undefined}
        className={tableClass}
        role="table"
      >
        {/* ─── Header ──────────────────────────────────────── */}
        {showHeader && (
          <thead className="border-b border-gray-200 bg-gray-50 text-start text-xs font-semibold uppercase tracking-wider text-gray-600">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
                {headerGroup.headers.map((header) => {
                  const meta = header.column.columnDef.meta as
                    | DataTableColumnMeta
                    | undefined;
                  const isSortable = header.column.getCanSort();
                  const sortDir = header.column.getIsSorted(); // 'asc' | 'desc' | false

                  return (
                    <th
                      key={header.id}
                      scope="col"
                      className={cn(
                        cellPadding,
                        meta?.noWrap && 'whitespace-nowrap',
                        horizontalAlignClass(meta?.horizontalAlign),
                        verticalAlignClass(meta?.verticalAlign),
                        meta?.className,
                        bordered && 'border border-gray-200',
                        borderless && 'border-0',
                        isSortable && 'cursor-pointer select-none',
                      )}
                      style={meta?.width ? { width: meta.width } : undefined}
                      onClick={isSortable ? header.column.getToggleSortingHandler() : undefined}
                      aria-sort={
                        sortDir === 'asc'
                          ? 'ascending'
                          : sortDir === 'desc'
                            ? 'descending'
                            : isSortable
                              ? 'none'
                              : undefined
                      }
                    >
                      <span className="inline-flex items-center gap-1">
                        {header.isPlaceholder
                          ? null
                          : flexRender(header.column.columnDef.header, header.getContext())}

                        {/* Sort indicator */}
                        {isSortable && (
                          <span className="inline-flex flex-col text-[0.5rem] leading-none" aria-hidden="true">
                            <span className={sortDir === 'asc' ? 'text-gray-900' : 'text-gray-300'}>
                              ▲
                            </span>
                            <span className={sortDir === 'desc' ? 'text-gray-900' : 'text-gray-300'}>
                              ▼
                            </span>
                          </span>
                        )}
                      </span>
                    </th>
                  );
                })}
              </tr>
            ))}
          </thead>
        )}

        {/* ─── Body ────────────────────────────────────────── */}
        {data.length > 0 ? (
          <tbody>
            {table.getRowModel().rows.map((row) => (
              <tr
                key={row.id}
                {...(rowTestId ? { 'data-testid': rowTestId } : {})}
                {...(onRowClick
                  ? {
                      onClick: () =>
                        onRowClick(row.original, row.index),
                      role: 'button',
                      tabIndex: 0,
                      onKeyDown: (e: React.KeyboardEvent) => {
                        if (e.key === 'Enter' || e.key === ' ') {
                          e.preventDefault();
                          onRowClick(row.original, row.index);
                        }
                      },
                    }
                  : {})}
                className={cn(
                  'border-b border-gray-100',
                  striped && 'even:bg-gray-50',
                  hover &&
                    'hover:bg-gray-100 motion-safe:transition-colors motion-safe:duration-150',
                  onRowClick && 'cursor-pointer',
                )}
              >
                {row.getVisibleCells().map((cell) => {
                  const meta = cell.column.columnDef.meta as
                    | DataTableColumnMeta
                    | undefined;

                  return (
                    <td
                      key={cell.id}
                      className={cn(
                        cellPadding,
                        'text-sm text-gray-900',
                        meta?.noWrap && 'whitespace-nowrap',
                        horizontalAlignClass(meta?.horizontalAlign),
                        verticalAlignClass(meta?.verticalAlign),
                        meta?.className,
                        bordered && 'border border-gray-200',
                        borderless && 'border-0',
                      )}
                      style={meta?.width ? { width: meta.width } : undefined}
                    >
                      {flexRender(cell.column.columnDef.cell, cell.getContext())}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        ) : (
          /* Empty state — matches Display.cshtml lines 61-67 */
          !loading && (
            <tbody>
              <tr>
                <td colSpan={columns.length || 1}>
                  <div
                    className="m-0 rounded bg-blue-50 p-4 text-sm text-blue-700"
                    role="status"
                  >
                    {emptyText}
                  </div>
                </td>
              </tr>
            </tbody>
          )
        )}

        {/* ─── Footer / Pagination ─────────────────────────── */}
        {showFooter && paginationEnabled && data.length > 0 && (
          <tfoot>
            <tr>
              <td colSpan={columns.length || 1} className="px-2 py-3">
                <div className="flex flex-wrap items-center justify-between gap-4">
                  {/* Total count label */}
                  {!hideTotal && (
                    <span className="text-sm text-gray-700">
                      Showing{' '}
                      <span className="font-medium">{rangeStart}</span>
                      {' '}to{' '}
                      <span className="font-medium">{rangeEnd}</span>
                      {' '}of{' '}
                      <span className="font-medium">{effectiveTotalCount}</span>
                      {' '}records
                    </span>
                  )}

                  {/* Navigation controls */}
                  <nav
                    className="flex items-center gap-1"
                    aria-label={`${name ? name + ' ' : ''}Pagination`}
                  >
                    {/* Previous button */}
                    <button
                      type="button"
                      onClick={() => handlePageChange(effectivePage - 1)}
                      disabled={effectivePage <= 1}
                      className={cn(
                        paginationBtnBase,
                        paginationBtnSize,
                        effectivePage <= 1 ? paginationBtnDisabled : paginationBtnEnabled,
                      )}
                      aria-label="Previous page"
                    >
                      ‹
                    </button>

                    {/* Page numbers */}
                    {pageNumbers.map((pageItem, idx) =>
                      pageItem === 'ellipsis' ? (
                        <span
                          key={`ellipsis-${idx}`}
                          className="px-1 text-sm text-gray-400"
                          aria-hidden="true"
                        >
                          …
                        </span>
                      ) : (
                        <button
                          key={pageItem}
                          type="button"
                          onClick={() => handlePageChange(pageItem)}
                          className={cn(
                            paginationBtnBase,
                            paginationBtnSize,
                            pageItem === effectivePage
                              ? paginationBtnActive
                              : paginationBtnEnabled,
                          )}
                          aria-label={`Page ${pageItem}`}
                          aria-current={pageItem === effectivePage ? 'page' : undefined}
                        >
                          {pageItem}
                        </button>
                      ),
                    )}

                    {/* Next button */}
                    <button
                      type="button"
                      onClick={() => handlePageChange(effectivePage + 1)}
                      disabled={effectivePage >= pageCount}
                      className={cn(
                        paginationBtnBase,
                        paginationBtnSize,
                        effectivePage >= pageCount
                          ? paginationBtnDisabled
                          : paginationBtnEnabled,
                      )}
                      aria-label="Next page"
                    >
                      ›
                    </button>

                    {/* Page-size selector */}
                    <label className="ms-3 flex items-center gap-1.5 text-sm text-gray-600">
                      <span className="sr-only">Rows per page</span>
                      <select
                        value={effectivePageSize}
                        onChange={(e) =>
                          handlePageSizeChange(parseInt(e.target.value, 10))
                        }
                        className={cn(
                          'rounded border border-gray-300 bg-white text-sm text-gray-700',
                          'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
                          small ? 'px-1 py-0.5' : 'px-2 py-1',
                        )}
                        aria-label="Rows per page"
                      >
                        {PAGE_SIZE_OPTIONS.map((size) => (
                          <option key={size} value={size}>
                            {size} / page
                          </option>
                        ))}
                      </select>
                    </label>
                  </nav>
                </div>
              </td>
            </tr>
          </tfoot>
        )}
      </table>
    </div>
  );
}
