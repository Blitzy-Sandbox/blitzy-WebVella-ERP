import { useState, useMemo, useCallback, type ReactNode, type ReactElement } from 'react';
import {
  useReactTable,
  getCoreRowModel,
  getSortedRowModel,
  flexRender,
  type ColumnDef,
  type SortingState,
} from '@tanstack/react-table';
import { useSearchParams } from 'react-router';

// ---------------------------------------------------------------------------
// DataTableColumn – per-column configuration interface
// Maps to PcGrid container options (PcGrid.cs lines 104-489)
// ---------------------------------------------------------------------------

/** Column configuration for the DataTable component. */
export interface DataTableColumn<T = Record<string, unknown>> {
  /** Column identifier — maps to PcGrid ContainerNId */
  id: string;
  /** Column header label — maps to PcGrid ContainerNLabel */
  label: string;
  /** Accessor key to extract cell value from row data */
  accessorKey?: string;
  /** Accessor function to extract cell value from row data */
  accessorFn?: (row: T) => unknown;
  /** Column width (CSS value) — maps to PcGrid ContainerNWidth */
  width?: string;
  /** Enable column sorting — maps to PcGrid ContainerNSortable (default false) */
  sortable?: boolean;
  /** Enable column search/filter — maps to PcGrid ContainerNSearchable (default false) */
  searchable?: boolean;
  /** Prevent text wrapping — maps to PcGrid ContainerNNoWrap (default false) */
  noWrap?: boolean;
  /** Additional CSS class for column cells — maps to PcGrid ContainerNClass */
  className?: string;
  /** Vertical alignment — maps to PcGrid ContainerNVerticalAlign */
  verticalAlign?: 'none' | 'top' | 'middle' | 'bottom';
  /** Horizontal alignment — maps to PcGrid ContainerNHorizontalAlign */
  horizontalAlign?: 'none' | 'left' | 'center' | 'right';
  /** Custom cell renderer function */
  cell?: (value: unknown, row: T) => ReactNode;
  /** Custom header renderer function */
  header?: () => ReactNode;
}

// ---------------------------------------------------------------------------
// DataTableProps – component props interface
// Maps to PcGridOptions class (PcGrid.cs lines 27-489)
// ---------------------------------------------------------------------------

/** Props for the DataTable component. */
export interface DataTableProps<T = Record<string, unknown>> {
  /** Column definitions — maps to PcGrid visible_columns + container configs */
  columns: DataTableColumn<T>[];
  /** Data rows — maps to PcGrid 'records' datasource */
  data: T[];
  /** Total record count for pagination — maps to PcGrid TotalCount */
  totalCount?: number;
  /** Current page (1-indexed) — maps to PcGrid query string page param (default 1) */
  page?: number;
  /** Page size — maps to PcGrid page_size option (default 10, undefined = no paging) */
  pageSize?: number;
  /** Table striped rows — maps to PcGrid 'striped' option (default false) */
  striped?: boolean;
  /** Compact/small table — maps to PcGrid 'small' option (default false) */
  small?: boolean;
  /** Bordered table — maps to PcGrid 'bordered' option (default false) */
  bordered?: boolean;
  /** Borderless table — maps to PcGrid 'borderless' option (default false) */
  borderless?: boolean;
  /** Enable row hover highlight — maps to PcGrid 'hover' option (default false) */
  hover?: boolean;
  /** Responsive breakpoint — maps to PcGrid responsive_breakpoint */
  responsiveBreakpoint?: 'none' | 'sm' | 'md' | 'lg' | 'xl';
  /** Table HTML id — maps to PcGrid 'id' option */
  id?: string;
  /** CSS class — maps to PcGrid 'class' option */
  className?: string;
  /** Show table header — maps to PcGrid has_thead (default true) */
  showHeader?: boolean;
  /** Show table footer — maps to PcGrid has_tfoot (default true) */
  showFooter?: boolean;
  /** Hide total count in footer — maps to PcGrid no_total (default false) */
  hideTotal?: boolean;
  /** Empty state text — maps to PcGrid empty_text (default 'No records') */
  emptyText?: string;
  /** Visibility flag — maps to PcGrid is_visible datasource */
  isVisible?: boolean;
  /** Query string key for sort field — maps to PcGrid query_string_sortby */
  sortByParam?: string;
  /** Query string key for sort order — maps to PcGrid query_string_sort_order */
  sortOrderParam?: string;
  /** Query string key for page — maps to PcGrid query_string_page */
  pageParam?: string;
  /** Query string key for page size — maps to PcGrid query_string_page_size */
  pageSizeParam?: string;
  /** Query string prefix — maps to PcGrid 'prefix' option */
  prefix?: string;
  /** Callback when page changes */
  onPageChange?: (page: number) => void;
  /** Callback when page size changes */
  onPageSizeChange?: (pageSize: number) => void;
  /** Callback when sort changes */
  onSortChange?: (sortBy: string, sortOrder: 'asc' | 'desc') => void;
  /** Callback when filter changes */
  onFilterChange?: (columnId: string, filterValue: string) => void;
  /** Loading state */
  isLoading?: boolean;
}

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/** Build a prefixed query-param key. */
function buildParamKey(prefix: string | undefined, param: string): string {
  return prefix ? `${prefix}${param}` : param;
}

/** Map vertical align prop to a Tailwind utility class. */
function verticalAlignClass(
  align: DataTableColumn['verticalAlign'],
): string {
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

/** Map horizontal align prop to a Tailwind utility class. */
function horizontalAlignClass(
  align: DataTableColumn['horizontalAlign'],
): string {
  switch (align) {
    case 'left':
      return 'text-left';
    case 'center':
      return 'text-center';
    case 'right':
      return 'text-right';
    default:
      return '';
  }
}

/**
 * Map responsive breakpoint prop to a Tailwind responsive `overflow-x-auto`
 * wrapper class. When `none` or undefined the table stretches to its container
 * without horizontal scrolling.
 */
function responsiveWrapperClass(
  breakpoint: DataTableProps['responsiveBreakpoint'],
): string {
  switch (breakpoint) {
    case 'sm':
      return 'sm:overflow-x-auto';
    case 'md':
      return 'md:overflow-x-auto';
    case 'lg':
      return 'lg:overflow-x-auto';
    case 'xl':
      return 'xl:overflow-x-auto';
    default:
      return '';
  }
}

/** Concatenate truthy class fragments, filtering out blanks. */
function cx(...parts: (string | false | undefined | null)[]): string {
  return parts.filter(Boolean).join(' ');
}

/** Available page-size options for the page-size selector dropdown. */
const PAGE_SIZE_OPTIONS = [10, 25, 50, 100] as const;

// ---------------------------------------------------------------------------
// DataTable component
// ---------------------------------------------------------------------------

/**
 * Reusable data-grid component backed by TanStack Table v8.
 *
 * Replaces the monolith PcGrid ViewComponent with a headless table
 * implementation, Tailwind CSS 4 styling, and URL query-string
 * synchronisation for pagination / sort state.
 */
export function DataTable<T = Record<string, unknown>>(
  props: DataTableProps<T>,
): ReactElement | null {
  const {
    columns,
    data,
    totalCount,
    page: pageProp,
    pageSize: pageSizeProp,
    striped = false,
    small = false,
    bordered = false,
    borderless = false,
    hover = false,
    responsiveBreakpoint = 'none',
    id,
    className,
    showHeader = true,
    showFooter = true,
    hideTotal = false,
    emptyText = 'No records',
    isVisible = true,
    sortByParam = 'sortBy',
    sortOrderParam = 'sortOrder',
    pageParam = 'page',
    pageSizeParam = 'pageSize',
    prefix,
    onPageChange,
    onPageSizeChange,
    onSortChange,
    onFilterChange,
    isLoading = false,
  } = props;

  // -- URL query-param keys ------------------------------------------------
  const sortByKey = buildParamKey(prefix, sortByParam);
  const sortOrderKey = buildParamKey(prefix, sortOrderParam);
  const pageKey = buildParamKey(prefix, pageParam);
  const pageSizeKey = buildParamKey(prefix, pageSizeParam);

  // -- Search-params sync --------------------------------------------------
  const [searchParams, setSearchParams] = useSearchParams();

  // Derive current page (1-indexed) from URL or prop with fallback of 1
  const currentPage = Number(searchParams.get(pageKey)) || pageProp || 1;
  // Derive current page size from URL or prop with fallback of 10
  const currentPageSize =
    Number(searchParams.get(pageSizeKey)) || pageSizeProp || 10;

  // -- Sorting state -------------------------------------------------------
  const urlSortBy = searchParams.get(sortByKey) ?? '';
  const urlSortOrder = searchParams.get(sortOrderKey) ?? '';

  const initialSorting: SortingState = useMemo(() => {
    if (urlSortBy) {
      return [{ id: urlSortBy, desc: urlSortOrder === 'desc' }];
    }
    return [];
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [urlSortBy, urlSortOrder]);

  const [sorting, setSorting] = useState<SortingState>(initialSorting);

  // -- Filter state (per-column) -------------------------------------------
  const [filterValues, setFilterValues] = useState<Record<string, string>>({});

  // -- Pagination helpers --------------------------------------------------
  const totalPages = useMemo(
    () =>
      totalCount !== undefined && currentPageSize > 0
        ? Math.max(1, Math.ceil(totalCount / currentPageSize))
        : 1,
    [totalCount, currentPageSize],
  );

  // -- Callbacks -----------------------------------------------------------

  /** Write a set of key-value pairs into URL search params. */
  const updateSearchParams = useCallback(
    (updates: Record<string, string>) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        for (const [k, v] of Object.entries(updates)) {
          if (v) {
            next.set(k, v);
          } else {
            next.delete(k);
          }
        }
        return next;
      });
    },
    [setSearchParams],
  );

  const handlePageChange = useCallback(
    (newPage: number) => {
      updateSearchParams({ [pageKey]: String(newPage) });
      onPageChange?.(newPage);
    },
    [updateSearchParams, pageKey, onPageChange],
  );

  const handlePageSizeChange = useCallback(
    (newSize: number) => {
      // Reset to page 1 when page size changes
      updateSearchParams({
        [pageSizeKey]: String(newSize),
        [pageKey]: '1',
      });
      onPageSizeChange?.(newSize);
    },
    [updateSearchParams, pageSizeKey, pageKey, onPageSizeChange],
  );

  const handleSortToggle = useCallback(
    (columnId: string) => {
      setSorting((prev) => {
        const existing = prev.find((s) => s.id === columnId);

        if (!existing) {
          // First click → ascending
          const nextState: SortingState = [{ id: columnId, desc: false }];
          updateSearchParams({
            [sortByKey]: columnId,
            [sortOrderKey]: 'asc',
          });
          onSortChange?.(columnId, 'asc');
          return nextState;
        }

        if (!existing.desc) {
          // Second click → descending
          const nextState: SortingState = [{ id: columnId, desc: true }];
          updateSearchParams({
            [sortByKey]: columnId,
            [sortOrderKey]: 'desc',
          });
          onSortChange?.(columnId, 'desc');
          return nextState;
        }

        // Third click → clear sort
        updateSearchParams({ [sortByKey]: '', [sortOrderKey]: '' });
        onSortChange?.(columnId, 'asc');
        return [];
      });
    },
    [updateSearchParams, sortByKey, sortOrderKey, onSortChange],
  );

  const handleFilterChange = useCallback(
    (columnId: string, value: string) => {
      setFilterValues((prev) => ({ ...prev, [columnId]: value }));
      onFilterChange?.(columnId, value);
    },
    [onFilterChange],
  );

  // -- Map DataTableColumn[] → TanStack ColumnDef[] ------------------------

  const tanstackColumns: ColumnDef<T, unknown>[] = useMemo(
    () =>
      columns.map((col): ColumnDef<T, unknown> => {
        const colDef: ColumnDef<T, unknown> = {
          id: col.id,
          enableSorting: col.sortable ?? false,
        };

        // Accessor — key or function
        if (col.accessorKey) {
          (colDef as ColumnDef<T, unknown> & { accessorKey: string }).accessorKey =
            col.accessorKey;
        } else if (col.accessorFn) {
          (colDef as ColumnDef<T, unknown> & { accessorFn: (row: T) => unknown }).accessorFn =
            col.accessorFn;
        }

        // Header renderer
        colDef.header = col.header
          ? () => col.header!()
          : () => col.label;

        // Cell renderer
        if (col.cell) {
          const customCell = col.cell;
          colDef.cell = (info) =>
            customCell(info.getValue(), info.row.original);
        }

        // Column size for width hints
        if (col.width) {
          colDef.size = parseInt(col.width, 10) || undefined;
        }

        return colDef;
      }),
    [columns],
  );

  // -- TanStack Table instance ---------------------------------------------

  const hasSortable = columns.some((c) => c.sortable);

  const table = useReactTable<T>({
    data,
    columns: tanstackColumns,
    state: {
      sorting,
    },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    ...(hasSortable ? { getSortedRowModel: getSortedRowModel() } : {}),
    manualPagination: true,
    manualSorting: true,
    pageCount: totalPages,
  });

  // -- Render helpers: class strings ---------------------------------------

  const tableClasses = cx(
    'w-full',
    bordered && 'border border-gray-200',
    borderless && 'border-0',
    small ? 'text-sm' : 'text-base',
    className,
  );

  const wrapperClasses = cx(
    'overflow-x-auto',
    responsiveWrapperClass(responsiveBreakpoint),
  );

  // -- Column metadata lookup for styling ----------------------------------

  /** Retrieve the original DataTableColumn config by column id. */
  const getColMeta = useCallback(
    (colId: string): DataTableColumn<T> | undefined =>
      columns.find((c) => c.id === colId),
    [columns],
  );

  // -- Visibility gate (mirrors PcGrid is_visible check) -------------------
  // Placed after all hooks to satisfy React Rules of Hooks (no conditional
  // hook invocation). The hooks above execute unconditionally.
  if (!isVisible) {
    return null;
  }

  // -- Render --------------------------------------------------------------

  return (
    <div className={wrapperClasses}>
      <table
        id={id}
        className={tableClasses}
        role="grid"
        aria-busy={isLoading}
      >
        {/* ----- THEAD ----- */}
        {showHeader && (
          <thead className="border-b border-gray-200 bg-gray-50">
            {table.getHeaderGroups().map((headerGroup) => (
              <tr key={headerGroup.id}>
                {headerGroup.headers.map((headerCell) => {
                  const meta = getColMeta(headerCell.column.id);
                  const isSortable = meta?.sortable ?? false;
                  const sortState = sorting.find(
                    (s) => s.id === headerCell.column.id,
                  );

                  const thClasses = cx(
                    small ? 'px-2 py-1' : 'px-4 py-3',
                    'font-semibold',
                    bordered && 'border border-gray-200',
                    horizontalAlignClass(meta?.horizontalAlign),
                    verticalAlignClass(meta?.verticalAlign),
                    meta?.noWrap && 'whitespace-nowrap',
                    meta?.className,
                  );

                  return (
                    <th
                      key={headerCell.id}
                      className={thClasses}
                      style={
                        meta?.width ? { width: meta.width } : undefined
                      }
                      scope="col"
                      aria-sort={
                        sortState
                          ? sortState.desc
                            ? 'descending'
                            : 'ascending'
                          : undefined
                      }
                    >
                      {isSortable ? (
                        <button
                          type="button"
                          className="inline-flex items-center gap-1 bg-transparent border-0 p-0 font-semibold cursor-pointer focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                          onClick={() =>
                            handleSortToggle(headerCell.column.id)
                          }
                          aria-label={`Sort by ${meta?.label ?? headerCell.column.id}`}
                        >
                          {flexRender(
                            headerCell.column.columnDef.header,
                            headerCell.getContext(),
                          )}
                          <span
                            className="inline-flex flex-col text-xs leading-none"
                            aria-hidden="true"
                          >
                            <span
                              className={cx(
                                sortState && !sortState.desc
                                  ? 'text-gray-900'
                                  : 'text-gray-400',
                              )}
                            >
                              ▲
                            </span>
                            <span
                              className={cx(
                                sortState?.desc
                                  ? 'text-gray-900'
                                  : 'text-gray-400',
                              )}
                            >
                              ▼
                            </span>
                          </span>
                        </button>
                      ) : (
                        flexRender(
                          headerCell.column.columnDef.header,
                          headerCell.getContext(),
                        )
                      )}

                      {/* Inline column filter input */}
                      {meta?.searchable && (
                        <div className="mt-1">
                          <input
                            type="text"
                            className="w-full rounded border border-gray-300 px-2 py-0.5 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600"
                            placeholder={`Filter ${meta.label}…`}
                            value={
                              filterValues[headerCell.column.id] ?? ''
                            }
                            onChange={(e) =>
                              handleFilterChange(
                                headerCell.column.id,
                                e.target.value,
                              )
                            }
                            aria-label={`Filter ${meta.label}`}
                          />
                        </div>
                      )}
                    </th>
                  );
                })}
              </tr>
            ))}
          </thead>
        )}

        {/* ----- TBODY ----- */}
        <tbody>
          {isLoading ? (
            /* Loading skeleton rows */
            Array.from({ length: Math.min(currentPageSize, 5) }).map(
              (_, rowIdx) => (
                <tr key={`skeleton-${rowIdx}`}>
                  {columns.map((col) => (
                    <td
                      key={`skeleton-${rowIdx}-${col.id}`}
                      className={cx(
                        small ? 'px-2 py-1' : 'px-4 py-3',
                        bordered && 'border border-gray-200',
                      )}
                    >
                      <div className="h-4 animate-pulse rounded bg-gray-200" />
                    </td>
                  ))}
                </tr>
              ),
            )
          ) : data.length === 0 ? (
            /* Empty state row — mirrors Display.cshtml empty alert */
            <tr>
              <td
                colSpan={columns.length || 1}
                className={cx(
                  small ? 'px-2 py-4' : 'px-4 py-8',
                  'text-center text-gray-500',
                )}
              >
                {emptyText}
              </td>
            </tr>
          ) : (
            /* Data rows */
            table.getRowModel().rows.map((row, rowIdx) => {
              const rowClasses = cx(
                striped && rowIdx % 2 === 1 && 'bg-gray-50',
                hover && 'hover:bg-gray-100',
                bordered && 'border-b border-gray-200',
              );

              return (
                <tr key={row.id} className={rowClasses || undefined}>
                  {row.getVisibleCells().map((cell) => {
                    const meta = getColMeta(cell.column.id);

                    const tdClasses = cx(
                      small ? 'px-2 py-1' : 'px-4 py-3',
                      bordered && 'border border-gray-200',
                      meta?.noWrap && 'whitespace-nowrap',
                      horizontalAlignClass(meta?.horizontalAlign),
                      verticalAlignClass(meta?.verticalAlign),
                      meta?.className,
                    );

                    return (
                      <td
                        key={cell.id}
                        className={tdClasses}
                        style={
                          meta?.width ? { width: meta.width } : undefined
                        }
                      >
                        {flexRender(
                          cell.column.columnDef.cell,
                          cell.getContext(),
                        )}
                      </td>
                    );
                  })}
                </tr>
              );
            })
          )}
        </tbody>

        {/* ----- TFOOT — pagination & summary ----- */}
        {showFooter && totalCount !== undefined && (
          <tfoot>
            <tr>
              <td
                colSpan={columns.length || 1}
                className={cx(
                  small ? 'px-2 py-2' : 'px-4 py-3',
                  'border-t border-gray-200',
                )}
              >
                <nav
                  className="flex flex-wrap items-center justify-between gap-4"
                  aria-label="Table pagination"
                >
                  {/* Total count */}
                  {!hideTotal && (
                    <span className="text-sm text-gray-600">
                      {totalCount === 1
                        ? '1 record'
                        : `${totalCount} records`}
                    </span>
                  )}

                  <div className="flex items-center gap-4">
                    {/* Page size selector */}
                    <label className="inline-flex items-center gap-2 text-sm text-gray-600">
                      <span>Rows</span>
                      <select
                        className="rounded border border-gray-300 bg-white px-2 py-1 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-blue-600"
                        value={currentPageSize}
                        onChange={(e) =>
                          handlePageSizeChange(Number(e.target.value))
                        }
                        aria-label="Rows per page"
                      >
                        {PAGE_SIZE_OPTIONS.map((opt) => (
                          <option key={opt} value={opt}>
                            {opt}
                          </option>
                        ))}
                      </select>
                    </label>

                    {/* Page info */}
                    <span className="text-sm text-gray-600">
                      Page {currentPage} of {totalPages}
                    </span>

                    {/* Prev / Next buttons */}
                    <div className="inline-flex gap-1" role="group">
                      <button
                        type="button"
                        disabled={currentPage <= 1}
                        className="rounded border border-gray-300 bg-white px-3 py-1 text-sm enabled:hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                        onClick={() => handlePageChange(currentPage - 1)}
                        aria-label="Previous page"
                      >
                        ← Prev
                      </button>
                      <button
                        type="button"
                        disabled={currentPage >= totalPages}
                        className="rounded border border-gray-300 bg-white px-3 py-1 text-sm enabled:hover:bg-gray-100 disabled:cursor-not-allowed disabled:opacity-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
                        onClick={() => handlePageChange(currentPage + 1)}
                        aria-label="Next page"
                      >
                        Next →
                      </button>
                    </div>
                  </div>
                </nav>
              </td>
            </tr>
          </tfoot>
        )}
      </table>
    </div>
  );
}
