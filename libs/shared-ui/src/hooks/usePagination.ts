/**
 * usePagination — Pagination State Management Hook
 *
 * Replaces the server-side PcGrid component's paging logic from the WebVella ERP monolith.
 * Manages pagination parameters via URL query strings with configurable parameter names
 * and a prefix system for multi-grid pages, synchronized via React Router 7's useSearchParams.
 *
 * Source: WebVella.Erp.Web/Components/PcGrid/PcGrid.cs (lines 537–596)
 *
 * Key behaviors preserved from the monolith:
 * - Configurable query string parameter names (PcGridOptions lines 77–87)
 * - Prefix-based key namespacing for multiple grids on a single page (line 66, 578)
 * - Default values: page=1, pageSize=10, pageSize=0 means unlimited (lines 537–547)
 * - Int16.TryParse-equivalent safe integer parsing (lines 582–583)
 * - TotalCount driven by data source, not URL (line 571)
 */

import { useSearchParams } from 'react-router';
import { useState, useCallback, useMemo } from 'react';

// ─── Type Definitions ────────────────────────────────────────────────────────

/**
 * Configuration options for the usePagination hook.
 * All properties are optional with sensible defaults matching PcGridOptions.
 */
export interface PaginationConfig {
  /** Query string key prefix for multi-grid pages. Default: "" */
  prefix?: string;
  /** Query string param name for page number. Default: "page" */
  pageKey?: string;
  /** Query string param name for page size. Default: "pageSize" */
  pageSizeKey?: string;
  /** Query string param name for sort field. Default: "sortBy" */
  sortByKey?: string;
  /** Query string param name for sort direction. Default: "sortOrder" */
  sortOrderKey?: string;
  /** Default page number (1-indexed). Default: 1 */
  defaultPage?: number;
  /** Default items per page (0 = unlimited). Default: 10 */
  defaultPageSize?: number;
  /** Default sort field name. Default: "" (no sort) */
  defaultSortBy?: string;
  /** Default sort direction. Default: "asc" */
  defaultSortOrder?: 'asc' | 'desc';
}

/**
 * Read-only pagination state derived from URL search params and data source.
 */
export interface PaginationState {
  /** Current page number (1-indexed, matching PcGrid.cs ViewBag.Page) */
  page: number;
  /** Items per page (0 = unlimited, matching PcGrid.cs ViewBag.PageSize = 0 behavior) */
  pageSize: number;
  /** Field name to sort by */
  sortBy: string;
  /** Sort direction */
  sortOrder: 'asc' | 'desc';
  /** Total number of records from data source (not URL-synced) */
  totalCount: number;
  /** Computed: Math.ceil(totalCount / pageSize) or 1 if pageSize=0 */
  totalPages: number;
}

/**
 * Memoized action functions for controlling pagination state.
 */
export interface PaginationActions {
  /** Navigate to a specific page (clamped between 1 and totalPages) */
  setPage: (page: number) => void;
  /** Change page size and reset to page 1 */
  setPageSize: (pageSize: number) => void;
  /** Change sort field and reset to page 1 */
  setSortBy: (field: string) => void;
  /** Change sort direction */
  setSortOrder: (order: 'asc' | 'desc') => void;
  /** Toggle between ascending and descending sort */
  toggleSortOrder: () => void;
  /** Update total count from data source (not URL-synced) */
  setTotalCount: (count: number) => void;
  /** Go to next page (bounded by totalPages) */
  nextPage: () => void;
  /** Go to previous page (bounded by 1) */
  prevPage: () => void;
  /** Reset all pagination params to defaults and clear totalCount */
  resetPagination: () => void;
}

// ─── Default Configuration ───────────────────────────────────────────────────

/**
 * Default configuration values matching PcGridOptions defaults from PcGrid.cs:
 * - prefix: "" (line 66)
 * - pageKey: "page" (line 84)
 * - pageSizeKey: "pageSize" (line 87)
 * - sortByKey: "sortBy" (line 78)
 * - sortOrderKey: "sortOrder" (line 81)
 * - defaultPage: 1 (line 537)
 * - defaultPageSize: 10 (line 39)
 * - defaultSortBy: "" (no sort)
 * - defaultSortOrder: "asc"
 */
const DEFAULT_CONFIG: Required<PaginationConfig> = {
  prefix: '',
  pageKey: 'page',
  pageSizeKey: 'pageSize',
  sortByKey: 'sortBy',
  sortOrderKey: 'sortOrder',
  defaultPage: 1,
  defaultPageSize: 10,
  defaultSortBy: '',
  defaultSortOrder: 'asc',
};

// ─── Utility Functions ───────────────────────────────────────────────────────

/**
 * Build full query string key by concatenating prefix and key name.
 * Matches PcGrid.cs line 578: `options.Prefix + options.QueryStringPage`
 * and line 588: `options.Prefix + options.QueryStringPageSize`
 *
 * @param prefix - The query string key prefix (e.g., "grid1_")
 * @param key - The base parameter name (e.g., "page")
 * @returns The concatenated key (e.g., "grid1_page")
 */
function getFullKey(prefix: string, key: string): string {
  return `${prefix}${key}`;
}

/**
 * Safely parse a string to integer with fallback value.
 * Mirrors the PcGrid.cs Int16.TryParse pattern (lines 582–583, 592–593):
 * - If the value is null, undefined, or empty string, returns the fallback
 * - If parsing fails (NaN), returns the fallback
 * - Otherwise returns the parsed integer
 *
 * @param value - The string value to parse (from URLSearchParams.get())
 * @param fallback - The default value when parsing fails
 * @returns The parsed integer or fallback value
 */
function parseIntSafe(value: string | null, fallback: number): number {
  if (value === null || value === '') {
    return fallback;
  }
  const parsed = parseInt(value, 10);
  return Number.isNaN(parsed) ? fallback : parsed;
}

/**
 * Type guard to validate a string as a valid sort order value.
 *
 * @param value - The string value to check
 * @returns True if the value is 'asc' or 'desc'
 */
function isValidSortOrder(value: string | null): value is 'asc' | 'desc' {
  return value === 'asc' || value === 'desc';
}

// ─── Hook Implementation ─────────────────────────────────────────────────────

/**
 * Pagination state management hook with URL query parameter synchronization.
 *
 * Replaces PcGrid.cs server-side pagination logic with a client-side React hook.
 * Reads/writes pagination parameters to URL search params via React Router 7's
 * useSearchParams, supporting configurable parameter names and prefix-based
 * key namespacing for multiple grids on the same page.
 *
 * @param config - Optional configuration overriding default parameter names and values
 * @returns Combined pagination state and action functions
 *
 * @example
 * ```tsx
 * // Basic usage
 * const { page, pageSize, totalPages, setPage, setPageSize, setTotalCount } = usePagination();
 *
 * // Multi-grid with prefix namespacing
 * const grid1 = usePagination({ prefix: 'tasks_' });
 * const grid2 = usePagination({ prefix: 'logs_' });
 * // URL: ?tasks_page=2&logs_page=1&logs_pageSize=25
 *
 * // Custom parameter names
 * const { page, sortBy } = usePagination({
 *   pageKey: 'p',
 *   sortByKey: 'sort',
 *   defaultPageSize: 25,
 * });
 * ```
 */
export function usePagination(
  config?: PaginationConfig
): PaginationState & PaginationActions {
  // Merge provided config with defaults, memoized on individual property values
  // to avoid recomputation when the config object reference changes but values stay the same
  const mergedConfig = useMemo<Required<PaginationConfig>>(
    () => ({ ...DEFAULT_CONFIG, ...config }),
    [
      config?.prefix,
      config?.pageKey,
      config?.pageSizeKey,
      config?.sortByKey,
      config?.sortOrderKey,
      config?.defaultPage,
      config?.defaultPageSize,
      config?.defaultSortBy,
      config?.defaultSortOrder,
    ]
  );

  // React Router 7 URL search params for bidirectional state synchronization
  const [searchParams, setSearchParams] = useSearchParams();

  // TotalCount is data-source-driven, not URL-synced
  // Matches PcGrid.cs line 571: ViewBag.TotalCount = ((EntityRecordList)ViewBag.Records).TotalCount
  const [totalCount, setTotalCountState] = useState<number>(0);

  // Build full query string keys with prefix (PcGrid.cs line 578 pattern)
  const fullPageKey = getFullKey(mergedConfig.prefix, mergedConfig.pageKey);
  const fullPageSizeKey = getFullKey(mergedConfig.prefix, mergedConfig.pageSizeKey);
  const fullSortByKey = getFullKey(mergedConfig.prefix, mergedConfig.sortByKey);
  const fullSortOrderKey = getFullKey(mergedConfig.prefix, mergedConfig.sortOrderKey);

  // ─── Read current values from URL search params ────────────────────────────
  // Mirrors PcGrid.cs lines 578–596 query string parsing

  const page = parseIntSafe(
    searchParams.get(fullPageKey),
    mergedConfig.defaultPage
  );

  const pageSize = parseIntSafe(
    searchParams.get(fullPageSizeKey),
    mergedConfig.defaultPageSize
  );

  const sortBy = searchParams.get(fullSortByKey) ?? mergedConfig.defaultSortBy;

  const sortOrderRaw = searchParams.get(fullSortOrderKey);
  const sortOrder: 'asc' | 'desc' = isValidSortOrder(sortOrderRaw)
    ? sortOrderRaw
    : mergedConfig.defaultSortOrder;

  // ─── Computed values ───────────────────────────────────────────────────────

  // When pageSize is 0 (unlimited), totalPages is 1 (matching source behavior)
  const totalPages = useMemo(
    () => (pageSize > 0 ? Math.ceil(totalCount / pageSize) : 1),
    [totalCount, pageSize]
  );

  // ─── URL update helper ─────────────────────────────────────────────────────

  /**
   * Update URL search params while preserving existing query parameters.
   * Removes a param from the URL if its value is null (clean URL pattern).
   * Uses replace mode to avoid polluting browser history with every pagination change.
   */
  const updateSearchParams = useCallback(
    (updates: Record<string, string | null>) => {
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          for (const [key, value] of Object.entries(updates)) {
            if (value === null) {
              next.delete(key);
            } else {
              next.set(key, value);
            }
          }
          return next;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  // ─── Action implementations ────────────────────────────────────────────────

  /**
   * Navigate to a specific page. Clamped between 1 and totalPages.
   * Removes the page param from URL when the value equals the default (clean URL).
   */
  const setPage = useCallback(
    (newPage: number) => {
      const maxPages = pageSize > 0 ? Math.ceil(totalCount / pageSize) : 1;
      const clamped = Math.max(1, Math.min(newPage, Math.max(1, maxPages)));
      updateSearchParams({
        [fullPageKey]: clamped === mergedConfig.defaultPage ? null : String(clamped),
      });
    },
    [fullPageKey, mergedConfig.defaultPage, pageSize, totalCount, updateSearchParams]
  );

  /**
   * Change page size and reset page to 1 (standard UX pattern).
   * Removes param from URL when value equals the default.
   */
  const setPageSize = useCallback(
    (newPageSize: number) => {
      updateSearchParams({
        [fullPageSizeKey]:
          newPageSize === mergedConfig.defaultPageSize ? null : String(newPageSize),
        [fullPageKey]: null, // Reset page to default when page size changes
      });
    },
    [fullPageKey, fullPageSizeKey, mergedConfig.defaultPageSize, updateSearchParams]
  );

  /**
   * Change sort field and reset page to 1.
   * Removes param from URL when value equals the default.
   */
  const setSortBy = useCallback(
    (field: string) => {
      updateSearchParams({
        [fullSortByKey]: field === mergedConfig.defaultSortBy ? null : field,
        [fullPageKey]: null, // Reset page to default when sort changes
      });
    },
    [fullPageKey, fullSortByKey, mergedConfig.defaultSortBy, updateSearchParams]
  );

  /**
   * Change sort direction.
   * Removes param from URL when value equals the default.
   */
  const setSortOrder = useCallback(
    (order: 'asc' | 'desc') => {
      updateSearchParams({
        [fullSortOrderKey]:
          order === mergedConfig.defaultSortOrder ? null : order,
      });
    },
    [fullSortOrderKey, mergedConfig.defaultSortOrder, updateSearchParams]
  );

  /**
   * Toggle between ascending and descending sort order.
   * Reads current sortOrder from URL and flips it.
   */
  const toggleSortOrder = useCallback(() => {
    const newOrder: 'asc' | 'desc' = sortOrder === 'asc' ? 'desc' : 'asc';
    updateSearchParams({
      [fullSortOrderKey]:
        newOrder === mergedConfig.defaultSortOrder ? null : newOrder,
    });
  }, [sortOrder, fullSortOrderKey, mergedConfig.defaultSortOrder, updateSearchParams]);

  /**
   * Update total count from data source. Not URL-synced.
   * Ensures non-negative value.
   */
  const setTotalCount = useCallback((count: number) => {
    setTotalCountState(Math.max(0, count));
  }, []);

  /**
   * Go to next page, bounded by totalPages.
   * No-op if already on the last page.
   */
  const nextPage = useCallback(() => {
    const maxPages = pageSize > 0 ? Math.ceil(totalCount / pageSize) : 1;
    if (page < maxPages) {
      const newPage = page + 1;
      updateSearchParams({
        [fullPageKey]: newPage === mergedConfig.defaultPage ? null : String(newPage),
      });
    }
  }, [page, pageSize, totalCount, fullPageKey, mergedConfig.defaultPage, updateSearchParams]);

  /**
   * Go to previous page, bounded by 1.
   * No-op if already on the first page.
   */
  const prevPage = useCallback(() => {
    if (page > 1) {
      const newPage = page - 1;
      updateSearchParams({
        [fullPageKey]: newPage === mergedConfig.defaultPage ? null : String(newPage),
      });
    }
  }, [page, fullPageKey, mergedConfig.defaultPage, updateSearchParams]);

  /**
   * Reset all pagination params to defaults and clear totalCount.
   * Removes all pagination-related query parameters from the URL.
   */
  const resetPagination = useCallback(() => {
    updateSearchParams({
      [fullPageKey]: null,
      [fullPageSizeKey]: null,
      [fullSortByKey]: null,
      [fullSortOrderKey]: null,
    });
    setTotalCountState(0);
  }, [fullPageKey, fullPageSizeKey, fullSortByKey, fullSortOrderKey, updateSearchParams]);

  // ─── Return combined state and actions ─────────────────────────────────────

  return {
    // State (read from URL search params + data source)
    page,
    pageSize,
    sortBy,
    sortOrder,
    totalCount,
    totalPages,

    // Actions (memoized via useCallback)
    setPage,
    setPageSize,
    setSortBy,
    setSortOrder,
    toggleSortOrder,
    setTotalCount,
    nextPage,
    prevPage,
    resetPagination,
  };
}
