/**
 * @fileoverview File Browser / Listing Page
 *
 * React page component for browsing and listing files. Replaces the
 * monolith's UserFileService.GetFilesList() — paginated file list with
 * type/search/sort filters returning List<UserFile> — and
 * DbFileRepository.FindAll() — PostgreSQL ILIKE path filtering, temp
 * file exclusion, LIMIT/OFFSET pagination.
 *
 * Route: /files (lazy-loaded via React.lazy())
 *
 * Key behavioral mappings from the monolith:
 * - UserFileService.GetFilesList(type, search, sort, page, pageSize)
 *   → useFiles() TanStack Query 5 hook with FileListParams
 * - Type filter (image/document/audio/video/other) → <select> dropdown
 * - OR-based search across name, alt, caption → debounced text input (300ms)
 * - Sort: 1 = created_on DESC, 2 = name ASC → <select> dropdown
 * - Default pageSize 30 → state-managed pagination
 * - DbFileRepository.FindAll with LIMIT/OFFSET → server-side pagination
 * - PcGrid ViewComponent → DataTable component (TanStack Table)
 *
 * @module FileList
 */

import { useState, useCallback, useRef } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useFiles } from '../../hooks/useFiles';
import type {
  FileListParams,
  FileMetadata,
  FileType,
  FileSortOption,
} from '../../hooks/useFiles';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';

/**
 * Intersection type that satisfies DataTable's `T extends Record<string, unknown>`
 * constraint while preserving FileMetadata's typed fields.
 */
type FileRecord = FileMetadata & Record<string, unknown>;

/* ════════════════════════════════════════════════════════════════
 * Constants
 * ════════════════════════════════════════════════════════════════ */

/** Debounce delay for the search input (milliseconds). */
const SEARCH_DEBOUNCE_MS = 300;

/**
 * Default page size matching the monolith's UserFileService default (30).
 * UserFileService.GetFilesList used pageSize=30 as the default when
 * invoked from the file browser page.
 */
const DEFAULT_PAGE_SIZE = 30;

/**
 * File type filter options matching the monolith's UserFileService
 * classification: image, document, audio, video, other.
 * An empty string selects all types (no filter applied).
 */
const FILE_TYPE_OPTIONS: ReadonlyArray<{ value: FileType | ''; label: string }> = [
  { value: '', label: 'All Types' },
  { value: 'image', label: 'Images' },
  { value: 'document', label: 'Documents' },
  { value: 'audio', label: 'Audio' },
  { value: 'video', label: 'Video' },
  { value: 'other', label: 'Other' },
];

/**
 * Sort options matching UserFileService.GetFilesList sort parameter:
 *  - 1 = created_on DESC (newest first, default)
 *  - 2 = name ASC (alphabetical A-Z)
 */
const SORT_OPTIONS: ReadonlyArray<{ value: FileSortOption; label: string }> = [
  { value: 1, label: 'Newest First' },
  { value: 2, label: 'Name (A-Z)' },
];

/* ════════════════════════════════════════════════════════════════
 * Helper functions
 * ════════════════════════════════════════════════════════════════ */

/**
 * Formats a byte count into a human-readable file size string.
 *
 * Matches the monolith's approach:
 *   fileKilobytes = Math.Round((decimal)tempFile.GetBytes().Length / 1024, 2)
 * but extends to MB/GB for larger files.
 */
function formatFileSize(bytes: number): string {
  if (bytes === 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  const kb = bytes / 1024;
  if (kb < 1024) return `${kb.toFixed(2)} KB`;
  const mb = kb / 1024;
  if (mb < 1024) return `${mb.toFixed(2)} MB`;
  const gb = mb / 1024;
  return `${gb.toFixed(2)} GB`;
}

/**
 * Returns a display label and Tailwind badge colour classes for a given
 * file type classification.  Mirrors the monolith's
 * UserFileService.CreateUserFile MIME classification buckets.
 */
function getFileTypeBadge(type: FileType | string): {
  label: string;
  className: string;
} {
  switch (type) {
    case 'image':
      return { label: 'Image', className: 'bg-blue-100 text-blue-800' };
    case 'document':
      return { label: 'Document', className: 'bg-green-100 text-green-800' };
    case 'audio':
      return { label: 'Audio', className: 'bg-purple-100 text-purple-800' };
    case 'video':
      return { label: 'Video', className: 'bg-red-100 text-red-800' };
    case 'application':
      return { label: 'Application', className: 'bg-yellow-100 text-yellow-800' };
    case 'other':
    default:
      return { label: 'Other', className: 'bg-gray-100 text-gray-800' };
  }
}

/**
 * Formats an ISO 8601 date string to a locale-friendly display string.
 * Returns an empty string for falsy inputs to avoid rendering "null" /
 * "undefined" as visible text (UI8 defensive pattern).
 */
function formatDate(isoDate: string | undefined | null): string {
  if (!isoDate) return '';
  try {
    return new Date(isoDate).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return '';
  }
}

/* ════════════════════════════════════════════════════════════════
 * Column definitions (stable reference — no component-state deps)
 * ════════════════════════════════════════════════════════════════ */

/**
 * DataTable column definitions for the file listing.
 *
 * Columns: Name (linked to detail page), Type (colour badge),
 * Size (human-readable), Created date.
 */
const FILE_COLUMNS: DataTableColumn<FileRecord>[] = [
  {
    id: 'filename',
    label: 'Name',
    accessorKey: 'filename',
    width: '40%',
    sortable: false,
    cell: (_value: unknown, record: FileRecord) => (
      <Link
        to={`/files/${record.id}`}
        className="text-blue-600 font-medium overflow-hidden text-ellipsis whitespace-nowrap hover:text-blue-800 hover:underline"
      >
        {record.filename || 'Untitled'}
      </Link>
    ),
  },
  {
    id: 'type',
    label: 'Type',
    accessorKey: 'type',
    width: '120px',
    sortable: false,
    cell: (_value: unknown, record: FileRecord) => {
      const badge = getFileTypeBadge(record.type);
      return (
        <span
          className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${badge.className}`}
        >
          {badge.label}
        </span>
      );
    },
  },
  {
    id: 'size',
    label: 'Size',
    accessorKey: 'size',
    width: '120px',
    horizontalAlign: 'right',
    sortable: false,
    cell: (_value: unknown, record: FileRecord) => (
      <span className="text-gray-600 tabular-nums">
        {formatFileSize(record.size)}
      </span>
    ),
  },
  {
    id: 'createdOn',
    label: 'Created',
    accessorKey: 'createdOn',
    width: '200px',
    sortable: false,
    cell: (_value: unknown, record: FileRecord) => (
      <span className="text-gray-600">{formatDate(record.createdOn)}</span>
    ),
  },
];

/* ════════════════════════════════════════════════════════════════
 * FileList component
 * ════════════════════════════════════════════════════════════════ */

/**
 * File browser/listing page component.
 *
 * Replaces the monolith's server-rendered file listing
 * (UserFileService.GetFilesList + DbFileRepository.FindAll) with a
 * React SPA page using TanStack Query for data fetching and DataTable
 * for presentation.
 *
 * Features:
 * - Filter by type (image, document, audio, video, other)
 * - OR-based text search across name, alt, caption (300 ms debounce)
 * - Sort: newest first (created_on DESC) or alphabetical (name ASC)
 * - Server-side pagination with configurable page size
 * - File-name links to detail page (/files/{id})
 * - Upload button links to /files/upload
 *
 * @returns JSX element rendering the file browser page.
 */
export default function FileList() {
  const navigate = useNavigate();

  /* ── Local state ─────────────────────────────────────────── */

  /**
   * Immediate search input value — drives the controlled <input>.
   * Kept separate from the debounced query param to avoid flickering.
   */
  const [searchInput, setSearchInput] = useState('');

  /**
   * Query parameters forwarded to the useFiles() hook.
   * Mirrors UserFileService.GetFilesList(type, search, sort, page, pageSize).
   */
  const [params, setParams] = useState<FileListParams>({
    type: undefined,
    search: undefined,
    sort: 1,
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
  });

  /** Timer handle for the 300 ms search debounce. */
  const searchTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  /* ── Data fetching ───────────────────────────────────────── */

  const { data, isLoading, isError, error } = useFiles(params);

  /** File records from the API response envelope (ApiResponse.object). */
  const files: FileRecord[] = (data?.object?.files ?? []) as FileRecord[];
  /** Total matching record count for pagination. */
  const totalCount: number = data?.object?.totalCount ?? 0;

  /* ── Event handlers ──────────────────────────────────────── */

  /**
   * Debounced search handler.
   * Updates the controlled input immediately but delays the query-param
   * update by 300 ms to avoid hammering the API on every keystroke.
   * Resets page to 1 when the search string changes.
   */
  const handleSearchChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const value = event.target.value;
      setSearchInput(value);

      if (searchTimerRef.current !== null) {
        clearTimeout(searchTimerRef.current);
      }

      searchTimerRef.current = setTimeout(() => {
        setParams((prev) => ({
          ...prev,
          search: value.trim() || undefined,
          page: 1,
        }));
        searchTimerRef.current = null;
      }, SEARCH_DEBOUNCE_MS);
    },
    [],
  );

  /** Type filter change — resets to page 1. */
  const handleTypeChange = useCallback(
    (event: React.ChangeEvent<HTMLSelectElement>) => {
      const value = event.target.value as FileType | '';
      setParams((prev) => ({
        ...prev,
        type: value || undefined,
        page: 1,
      }));
    },
    [],
  );

  /** Sort selection change. */
  const handleSortChange = useCallback(
    (event: React.ChangeEvent<HTMLSelectElement>) => {
      const value = Number(event.target.value) as FileSortOption;
      setParams((prev) => ({ ...prev, sort: value }));
    },
    [],
  );

  /** Pagination page-change callback for DataTable. */
  const handlePageChange = useCallback((page: number) => {
    setParams((prev) => ({ ...prev, page }));
  }, []);

  /** Pagination page-size change callback for DataTable. */
  const handlePageSizeChange = useCallback((pageSize: number) => {
    setParams((prev) => ({ ...prev, pageSize, page: 1 }));
  }, []);

  /* ── Error state ─────────────────────────────────────────── */

  if (isError) {
    const errorMessage =
      error instanceof Error
        ? error.message
        : 'An unexpected error occurred while loading files.';

    return (
      <div className="p-6">
        <div className="flex items-center justify-between mb-6">
          <h1 className="text-2xl font-semibold text-gray-900">Files</h1>
        </div>

        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 p-4"
        >
          <div className="flex items-start gap-3">
            {/* Inline error icon — monochrome SVG with fill="currentColor" */}
            <svg
              className="h-5 w-5 flex-shrink-0 text-red-500 mt-0.5"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
                clipRule="evenodd"
              />
            </svg>

            <div>
              <h2 className="text-sm font-medium text-red-800">
                Failed to load files
              </h2>
              <p className="mt-1 text-sm text-red-700">{errorMessage}</p>
              <button
                type="button"
                onClick={() => navigate(0)}
                className="mt-3 inline-flex items-center rounded-md bg-red-100 px-3 py-1.5 text-sm font-medium text-red-700 hover:bg-red-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
              >
                Retry
              </button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  /* ── Normal render ───────────────────────────────────────── */

  return (
    <div className="p-6">
      {/* Page header — title + upload action */}
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-semibold text-gray-900">Files</h1>

        <Link
          to="/files/upload"
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          {/* Upload icon — Heroicons arrow-up-tray */}
          <svg
            className="h-4 w-4"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path d="M9.25 13.25a.75.75 0 001.5 0V4.636l2.955 3.129a.75.75 0 001.09-1.03l-4.25-4.5a.75.75 0 00-1.09 0l-4.25 4.5a.75.75 0 101.09 1.03L9.25 4.636v8.614z" />
            <path d="M3.5 12.75a.75.75 0 00-1.5 0v2.5A2.75 2.75 0 004.75 18h10.5A2.75 2.75 0 0018 15.25v-2.5a.75.75 0 00-1.5 0v2.5c0 .69-.56 1.25-1.25 1.25H4.75c-.69 0-1.25-.56-1.25-1.25v-2.5z" />
          </svg>
          Upload Files
        </Link>
      </div>

      {/* Filter bar — search, type filter, sort selector */}
      <div className="flex flex-wrap items-end gap-4 mb-6">
        {/* Search input (debounced 300 ms) — searches name, alt, caption (OR) */}
        <div className="relative flex-1 min-w-[200px]">
          <label
            htmlFor="file-search"
            className="block text-sm font-medium text-gray-700 mb-1"
          >
            Search
          </label>
          <div className="relative">
            <svg
              className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3 h-full w-5 text-gray-400"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
                clipRule="evenodd"
              />
            </svg>
            <input
              id="file-search"
              type="search"
              placeholder="Search files by name, alt, or caption…"
              value={searchInput}
              onChange={handleSearchChange}
              className="block w-full rounded-md border-0 py-2 pl-10 pr-3 text-gray-900 ring-1 ring-inset ring-gray-300 placeholder:text-gray-400 focus:ring-2 focus:ring-inset focus:ring-blue-600 text-sm leading-6"
            />
          </div>
        </div>

        {/* Type filter */}
        <div>
          <label
            htmlFor="file-type-filter"
            className="block text-sm font-medium text-gray-700 mb-1"
          >
            Type
          </label>
          <select
            id="file-type-filter"
            value={params.type ?? ''}
            onChange={handleTypeChange}
            className="block rounded-md border-0 py-2 pl-3 pr-8 text-gray-900 ring-1 ring-inset ring-gray-300 focus:ring-2 focus:ring-inset focus:ring-blue-600 text-sm leading-6"
          >
            {FILE_TYPE_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>

        {/* Sort selector */}
        <div>
          <label
            htmlFor="file-sort"
            className="block text-sm font-medium text-gray-700 mb-1"
          >
            Sort
          </label>
          <select
            id="file-sort"
            value={params.sort ?? 1}
            onChange={handleSortChange}
            className="block rounded-md border-0 py-2 pl-3 pr-8 text-gray-900 ring-1 ring-inset ring-gray-300 focus:ring-2 focus:ring-inset focus:ring-blue-600 text-sm leading-6"
          >
            {SORT_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      {/* Results summary text */}
      {!isLoading && (
        <p className="mb-4 text-sm text-gray-500">
          {totalCount === 0
            ? 'No files found'
            : `Showing ${files.length} of ${totalCount} file${totalCount !== 1 ? 's' : ''}`}
        </p>
      )}

      {/* Data table — server-side pagination */}
      <DataTable<FileRecord>
        data={files}
        columns={FILE_COLUMNS}
        totalCount={totalCount}
        pageSize={params.pageSize ?? DEFAULT_PAGE_SIZE}
        currentPage={params.page ?? 1}
        onPageChange={handlePageChange}
        onPageSizeChange={handlePageSizeChange}
        loading={isLoading}
        emptyText="No files found. Try adjusting your filters or upload new files."
        hover
        striped
        responsiveBreakpoint="md"
      />
    </div>
  );
}
