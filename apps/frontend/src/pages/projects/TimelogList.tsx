/**
 * Timelog Listing Page — `apps/frontend/src/pages/projects/TimelogList.tsx`
 *
 * React page component replacing the monolith's `PcTimelogList` ViewComponent
 * and `<wv-timelog-list>` Stencil web component. Renders a paginated list of
 * timelog entries with:
 *   - User avatar + username
 *   - Minutes logged (formatted as hours:minutes)
 *   - Billable/Non-Billable badge
 *   - Associated task link (key + subject)
 *   - Logged date (relative time)
 *   - Optional body/description text
 *   - Author-only delete button with confirmation dialog
 *
 * Source analysis:
 *   - PcTimelogList.cs lines 79-155: data enrichment loop resolving
 *     $user_1n_timelog relation, task key/subject, billable status
 *   - TimeLogService.cs lines 62-83: author-only delete validation
 *   - ProjectController.cs lines 177-260: timelog create/delete endpoints
 *
 * Architecture:
 *   - Lazy-loaded via React.lazy() under `/projects/timelogs`
 *   - Also usable inline in TaskDetails via the `inline` prop
 *   - Data fetching via TanStack Query 5 `useTimelogs` hook
 *   - Author-only delete via `useDeleteTimelog` mutation
 *   - Auth state via Zustand `useAuthStore` for currentUser
 *   - Tailwind CSS styling — zero Bootstrap, zero jQuery, zero Stencil
 *
 * @module pages/projects/TimelogList
 */

import React, { useState, useMemo, useCallback } from 'react';
import { Link } from 'react-router';
import { useTimelogs, useDeleteTimelog } from '../../hooks/useProjects';
import { useAuthStore } from '../../stores/authStore';
import type { EntityRecord } from '../../types/record';
import { formatRelativeTime } from '../../utils/formatters';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default number of timelogs to display per page. */
const DEFAULT_PAGE_SIZE = 25;

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the {@link TimelogList} component.
 *
 * All filter props are optional — when omitted, the API returns all timelogs
 * the current user has access to.
 */
interface TimelogListProps {
  /** Filter by parent task ID (GUID string). */
  taskId?: string;

  /**
   * Filter by project ID (GUID string).
   *
   * Note: The current API's TimelogsParams interface does not include a direct
   * projectId filter. When set, the component will pass taskId/userId filters
   * to the API and may rely on server-side project scoping in a future release.
   */
  projectId?: string;

  /** Filter by user who logged the time (GUID string). */
  userId?: string;

  /**
   * When `true`, renders without page-level wrapper (header/padding) —
   * intended for embedding inside TaskDetails or project detail views.
   */
  inline?: boolean;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Safely extracts a string value from a dynamic EntityRecord field.
 * Returns the fallback value when the field is null, undefined, or not a string.
 */
function safeString(value: unknown, fallback = ''): string {
  if (typeof value === 'string') return value;
  if (value != null) return String(value);
  return fallback;
}

/**
 * Safely extracts a numeric value from a dynamic EntityRecord field.
 * Returns the fallback value when the field is null, undefined, or not a number.
 */
function safeNumber(value: unknown, fallback = 0): number {
  if (typeof value === 'number' && !Number.isNaN(value)) return value;
  if (typeof value === 'string') {
    const parsed = Number(value);
    if (!Number.isNaN(parsed)) return parsed;
  }
  return fallback;
}

/**
 * Safely extracts a boolean value from a dynamic EntityRecord field.
 * Returns the fallback value when the field cannot be interpreted as boolean.
 */
function safeBoolean(value: unknown, fallback = false): boolean {
  if (typeof value === 'boolean') return value;
  if (value === 'true') return true;
  if (value === 'false') return false;
  return fallback;
}

/**
 * Formats a total number of minutes into a human-readable duration string.
 *
 * Examples:
 *   - 30  → "30m"
 *   - 90  → "1h 30m"
 *   - 120 → "2h"
 *   - 0   → "0m"
 *
 * Replaces the Stencil wv-timelog-list component's inline minutes display.
 */
function formatMinutes(totalMinutes: number): string {
  if (totalMinutes <= 0) return '0m';
  const hours = Math.floor(totalMinutes / 60);
  const mins = totalMinutes % 60;
  if (hours === 0) return `${mins}m`;
  if (mins === 0) return `${hours}h`;
  return `${hours}h ${mins}m`;
}

/**
 * Extracts enriched timelog display data from a raw EntityRecord.
 *
 * The Inventory/Project microservice API returns timelog records with
 * enriched relational data (user info, task info) as flat or nested fields.
 * This function normalises the record shape for consistent rendering.
 *
 * Field mapping from PcTimelogList.cs data enrichment loop:
 *   - $user_1n_timelog → user_username, user_image
 *   - task → task_id, task_key, task_subject
 *   - Standard fields: minutes, is_billable, body, logged_on, created_by
 */
interface TimelogDisplayData {
  id: string;
  minutes: number;
  isBillable: boolean;
  body: string;
  loggedOn: string;
  createdBy: string;
  userUsername: string;
  userImage: string;
  taskId: string;
  taskKey: string;
  taskSubject: string;
}

function extractTimelogData(record: EntityRecord): TimelogDisplayData {
  // Resolve user data — may be nested under $user_1n_timelog or flattened
  let userUsername = '';
  let userImage = '';
  const userRelation = record['$user_1n_timelog'];
  if (Array.isArray(userRelation) && userRelation.length > 0) {
    const userRecord = userRelation[0] as EntityRecord;
    userUsername = safeString(userRecord['username']);
    userImage = safeString(userRecord['image']);
  } else {
    // Flattened fields from API enrichment
    userUsername = safeString(record['user_username'] ?? record['username']);
    userImage = safeString(record['user_image'] ?? record['user_avatar']);
  }

  // Resolve task data — may be nested or flattened
  let taskId = '';
  let taskKey = '';
  let taskSubject = '';
  const taskRelation = record['$task'];
  if (taskRelation && typeof taskRelation === 'object' && !Array.isArray(taskRelation)) {
    const taskRecord = taskRelation as EntityRecord;
    taskId = safeString(taskRecord['id'] ?? taskRecord['task_id']);
    taskKey = safeString(taskRecord['key'] ?? taskRecord['task_key']);
    taskSubject = safeString(taskRecord['subject'] ?? taskRecord['task_subject']);
  } else {
    taskId = safeString(record['task_id']);
    taskKey = safeString(record['task_key']);
    taskSubject = safeString(record['task_subject']);
  }

  return {
    id: safeString(record.id ?? record['id']),
    minutes: safeNumber(record['minutes']),
    isBillable: safeBoolean(record['is_billable']),
    body: safeString(record['body']),
    loggedOn: safeString(record['logged_on']),
    createdBy: safeString(record['created_by']),
    userUsername,
    userImage,
    taskId,
    taskKey,
    taskSubject,
  };
}

/**
 * Computes the total number of pages given a total record count and page size.
 */
function getTotalPages(totalCount: number, pageSize: number): number {
  return Math.max(1, Math.ceil(totalCount / pageSize));
}

// ---------------------------------------------------------------------------
// Sub-Components
// ---------------------------------------------------------------------------

/**
 * User avatar with fallback to initials.
 * Renders a 24×24px circular image or a coloured initials badge.
 */
function UserAvatar({ username, image }: { username: string; image: string }) {
  const initials = useMemo(() => {
    if (!username) return '?';
    const parts = username.trim().split(/\s+/);
    if (parts.length >= 2) {
      return (parts[0][0] + parts[1][0]).toUpperCase();
    }
    return username.slice(0, 2).toUpperCase();
  }, [username]);

  if (image) {
    return (
      <img
        src={image}
        alt={username ? `${username}'s avatar` : 'User avatar'}
        width={24}
        height={24}
        loading="lazy"
        decoding="async"
        className="inline-block size-6 shrink-0 rounded-full object-cover bg-gray-200"
      />
    );
  }

  return (
    <span
      className="inline-flex size-6 shrink-0 items-center justify-center rounded-full bg-indigo-100 text-[0.625rem] font-medium leading-none text-indigo-700"
      aria-hidden="true"
    >
      {initials}
    </span>
  );
}

/**
 * Billable status badge.
 * Green badge for billable entries, gray for non-billable.
 */
function BillableBadge({ isBillable }: { isBillable: boolean }) {
  return (
    <span
      className={
        isBillable
          ? 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-green-100 text-green-800'
          : 'inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-600'
      }
    >
      {isBillable ? 'Billable' : 'Non-Billable'}
    </span>
  );
}

/**
 * Delete confirmation dialog rendered as a modal overlay.
 *
 * Provides an accessible confirmation step before permanently deleting a
 * timelog entry — matching the monolith's confirm-before-delete pattern
 * from the wv-timelog-list Stencil component.
 */
function DeleteConfirmDialog({
  isOpen,
  isDeleting,
  onConfirm,
  onCancel,
}: {
  isOpen: boolean;
  isDeleting: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  if (!isOpen) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      role="dialog"
      aria-modal="true"
      aria-labelledby="delete-timelog-title"
    >
      <div className="mx-4 w-full max-w-sm rounded-lg bg-white p-6 shadow-xl">
        <h3
          id="delete-timelog-title"
          className="text-lg font-semibold text-gray-900"
        >
          Delete Timelog
        </h3>
        <p className="mt-2 text-sm text-gray-600">
          Are you sure you want to delete this timelog entry? This action cannot
          be undone.
        </p>
        <div className="mt-4 flex justify-end gap-3">
          <button
            type="button"
            onClick={onCancel}
            disabled={isDeleting}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={isDeleting}
            className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50"
          >
            {isDeleting ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Pagination Controls
// ---------------------------------------------------------------------------

/**
 * Pagination controls with previous/next buttons and page indicator.
 */
function PaginationControls({
  currentPage,
  totalPages,
  totalCount,
  pageSize,
  isFetching,
  onPageChange,
}: {
  currentPage: number;
  totalPages: number;
  totalCount: number;
  pageSize: number;
  isFetching: boolean;
  onPageChange: (page: number) => void;
}) {
  if (totalPages <= 1) return null;

  const startRecord = (currentPage - 1) * pageSize + 1;
  const endRecord = Math.min(currentPage * pageSize, totalCount);

  return (
    <nav
      className="flex items-center justify-between border-t border-gray-200 px-1 pt-3"
      aria-label="Timelog pagination"
    >
      <p className="text-sm text-gray-500">
        Showing{' '}
        <span className="font-medium text-gray-700">{startRecord}</span>
        {' '}to{' '}
        <span className="font-medium text-gray-700">{endRecord}</span>
        {' '}of{' '}
        <span className="font-medium text-gray-700">{totalCount}</span>
        {' '}timelogs
      </p>
      <div className="flex gap-2">
        <button
          type="button"
          onClick={() => onPageChange(currentPage - 1)}
          disabled={currentPage <= 1 || isFetching}
          aria-label="Previous page"
          className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
        >
          Previous
        </button>
        <button
          type="button"
          onClick={() => onPageChange(currentPage + 1)}
          disabled={currentPage >= totalPages || isFetching}
          aria-label="Next page"
          className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
        >
          Next
        </button>
      </div>
    </nav>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * Timelog listing page/section component.
 *
 * Renders a paginated list of timelog entries fetched via TanStack Query.
 * Each entry displays user info, time logged, billable status, associated
 * task, date, and an optional description. Authors may delete their own
 * entries via a confirmation dialog.
 *
 * Can be used as:
 *   - A standalone page at `/projects/timelogs`
 *   - An inline section within TaskDetails (set `inline={true}`)
 *
 * @example
 * ```tsx
 * // Standalone page
 * <TimelogList />
 *
 * // Inline in TaskDetails
 * <TimelogList taskId="abc-123" inline />
 * ```
 */
function TimelogList({
  taskId,
  projectId,
  userId,
  inline = false,
}: TimelogListProps) {
  // ---------------------------------------------------------------------------
  // Local State
  // ---------------------------------------------------------------------------

  /** Current page number (1-based). */
  const [page, setPage] = useState(1);

  /** ID of the timelog pending deletion — drives the confirm dialog. */
  const [deleteConfirmId, setDeleteConfirmId] = useState<string | null>(null);

  // ---------------------------------------------------------------------------
  // Auth State
  // ---------------------------------------------------------------------------

  const currentUser = useAuthStore((state) => state.currentUser);

  // ---------------------------------------------------------------------------
  // Data Fetching
  // ---------------------------------------------------------------------------

  /**
   * Fetch paginated timelog list with optional filters.
   *
   * The `projectId` prop is accepted for API forward-compatibility but is not
   * currently passed to the query parameters because `TimelogsParams` does not
   * include a `projectId` field. Task-based and user-based filtering is active.
   */
  const {
    data: timelogData,
    isLoading,
    isError,
    error,
    isFetching,
  } = useTimelogs({
    taskId,
    userId,
    page,
    pageSize: DEFAULT_PAGE_SIZE,
    sort: 'logged_on:desc',
  });

  // ---------------------------------------------------------------------------
  // Mutations
  // ---------------------------------------------------------------------------

  const {
    mutate: deleteTimelog,
    isPending: isDeleting,
  } = useDeleteTimelog();

  // ---------------------------------------------------------------------------
  // Derived Data
  // ---------------------------------------------------------------------------

  /**
   * Memoised list of enriched timelog display data, extracted from the
   * raw EntityRecord response. Prevents unnecessary re-computation on
   * each render when data has not changed.
   */
  const timelogs = useMemo<TimelogDisplayData[]>(() => {
    if (!timelogData?.records) return [];
    return timelogData.records.map(extractTimelogData);
  }, [timelogData]);

  const totalCount = timelogData?.totalCount ?? 0;
  const totalPages = getTotalPages(totalCount, DEFAULT_PAGE_SIZE);

  // ---------------------------------------------------------------------------
  // Callbacks
  // ---------------------------------------------------------------------------

  /**
   * Initiates the delete confirmation flow for the given timelog ID.
   * Only the author of a timelog may delete it — this is enforced both
   * in the UI (button visibility) and server-side (TimeLogService.Delete).
   */
  const handleDeleteRequest = useCallback((timelogId: string) => {
    setDeleteConfirmId(timelogId);
  }, []);

  /**
   * Confirms and executes the timelog deletion.
   * On success, the TanStack Query cache is automatically invalidated
   * by the `useDeleteTimelog` mutation's `onSuccess` handler.
   */
  const handleDeleteConfirm = useCallback(() => {
    if (!deleteConfirmId) return;
    deleteTimelog(deleteConfirmId, {
      onSettled: () => {
        setDeleteConfirmId(null);
      },
    });
  }, [deleteConfirmId, deleteTimelog]);

  /** Cancels the pending delete operation and closes the confirm dialog. */
  const handleDeleteCancel = useCallback(() => {
    setDeleteConfirmId(null);
  }, []);

  /** Navigates to a specific page number. */
  const handlePageChange = useCallback((newPage: number) => {
    setPage(newPage);
  }, []);

  // ---------------------------------------------------------------------------
  // Render: Loading State
  // ---------------------------------------------------------------------------

  if (isLoading) {
    return (
      <div className={inline ? '' : 'mx-auto max-w-4xl px-4 py-6'}>
        {!inline && (
          <h1 className="mb-6 text-2xl font-bold text-gray-900">Timelogs</h1>
        )}
        <div className="space-y-3" aria-busy="true" aria-label="Loading timelogs">
          {Array.from({ length: 5 }).map((_, index) => (
            <div
              key={index}
              className="animate-pulse rounded-lg border border-gray-200 bg-white p-4"
            >
              <div className="flex items-start gap-3">
                <div className="size-6 rounded-full bg-gray-200" />
                <div className="flex-1 space-y-2">
                  <div className="h-4 w-1/3 rounded bg-gray-200" />
                  <div className="h-3 w-2/3 rounded bg-gray-200" />
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Render: Error State
  // ---------------------------------------------------------------------------

  if (isError) {
    return (
      <div className={inline ? '' : 'mx-auto max-w-4xl px-4 py-6'}>
        {!inline && (
          <h1 className="mb-6 text-2xl font-bold text-gray-900">Timelogs</h1>
        )}
        <div
          role="alert"
          className="rounded-lg border border-red-200 bg-red-50 p-4"
        >
          <div className="flex items-start gap-3">
            <svg
              className="mt-0.5 size-5 shrink-0 text-red-500"
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
              <h3 className="text-sm font-semibold text-red-800">
                Failed to load timelogs
              </h3>
              <p className="mt-1 text-sm text-red-700">
                {error instanceof Error
                  ? error.message
                  : 'An unexpected error occurred while fetching timelogs.'}
              </p>
            </div>
          </div>
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Render: Empty State
  // ---------------------------------------------------------------------------

  if (timelogs.length === 0) {
    return (
      <div className={inline ? '' : 'mx-auto max-w-4xl px-4 py-6'} data-testid="timelog-list">
        {!inline && (
          <div className="mb-6 flex items-center justify-between">
            <h1 className="text-2xl font-bold text-gray-900">Timelogs</h1>
            <span className="text-sm text-gray-500" data-testid="hours-summary">Total hours: 0</span>
          </div>
        )}
        <div className="rounded-lg border border-gray-200 bg-white px-6 py-12 text-center">
          <svg
            className="mx-auto size-12 text-gray-400"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth={1.5}
            aria-hidden="true"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
          <h2 className="mt-3 text-sm font-semibold text-gray-900">
            No timelogs found
          </h2>
          <p className="mt-1 text-sm text-gray-500">
            {taskId
              ? 'No time has been logged for this task yet.'
              : 'No timelogs match the current filters.'}
          </p>
        </div>
      </div>
    );
  }

  // ---------------------------------------------------------------------------
  // Render: Timelog List
  // ---------------------------------------------------------------------------

  /** Aggregate total minutes across all displayed timelogs for the summary line. */
  const totalMinutes = timelogs.reduce(
    (acc, t) => acc + safeNumber(t.minutes, 0),
    0,
  );
  const totalHours = Math.floor(totalMinutes / 60);
  const remainingMins = Math.round(totalMinutes % 60);

  return (
    <div className={inline ? '' : 'mx-auto max-w-4xl px-4 py-6'} data-testid="timelog-list">
      {!inline && (
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-2xl font-bold text-gray-900">Timelogs</h1>
          <div className="flex items-center gap-4">
            <span className="text-sm text-gray-600" data-testid="hours-summary">
              Total hours: {totalHours}h {remainingMins > 0 ? `${remainingMins}m` : ''}
            </span>
            {isFetching && (
              <span className="text-sm text-gray-500" aria-live="polite">
                Refreshing…
              </span>
            )}
          </div>
        </div>
      )}

      {/* Timelog entries list */}
      <ul className="space-y-3" role="list" aria-label="Timelog entries">
        {timelogs.map((timelog) => {
          const isAuthor =
            currentUser != null &&
            timelog.createdBy !== '' &&
            currentUser.id === timelog.createdBy;

          return (
            <li
              key={timelog.id || `timelog-${timelog.loggedOn}-${timelog.createdBy}`}
              className="rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
            >
              {/* Row 1: User info + Duration + Billable badge */}
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <UserAvatar
                  username={timelog.userUsername}
                  image={timelog.userImage}
                />
                <span className="text-sm font-medium text-gray-900">
                  {timelog.userUsername || 'Unknown user'}
                </span>
                <span
                  className="text-sm font-semibold text-indigo-600"
                  aria-label={`Duration: ${formatMinutes(timelog.minutes)}`}
                >
                  {formatMinutes(timelog.minutes)}
                </span>
                <BillableBadge isBillable={timelog.isBillable} />

                {/* Spacer pushes delete button to the end */}
                <div className="flex-1" />

                {/* Delete button — author only */}
                {isAuthor && (
                  <button
                    type="button"
                    onClick={() => handleDeleteRequest(timelog.id)}
                    className="rounded p-1 text-gray-400 hover:bg-red-50 hover:text-red-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
                    aria-label={`Delete timelog by ${timelog.userUsername}`}
                  >
                    <svg
                      className="size-4"
                      viewBox="0 0 20 20"
                      fill="currentColor"
                      aria-hidden="true"
                    >
                      <path
                        fillRule="evenodd"
                        d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.52.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325C8.327 4.025 9.16 4 10 4zM8.58 7.72a.75.75 0 00-1.5.06l.3 7.5a.75.75 0 101.5-.06l-.3-7.5zm4.34.06a.75.75 0 10-1.5-.06l-.3 7.5a.75.75 0 101.5.06l.3-7.5z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </button>
                )}
              </div>

              {/* Row 2: Task link + Logged date */}
              <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-sm">
                {timelog.taskId && (timelog.taskKey || timelog.taskSubject) && (
                  <Link
                    to={`/projects/tasks/${timelog.taskId}`}
                    className="font-medium text-indigo-600 hover:text-indigo-800 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                  >
                    {timelog.taskKey && (
                      <span className="font-semibold">[{timelog.taskKey}]</span>
                    )}
                    {timelog.taskKey && timelog.taskSubject && ' '}
                    {timelog.taskSubject}
                  </Link>
                )}
                {timelog.loggedOn && (
                  <time
                    dateTime={timelog.loggedOn}
                    className="text-gray-500"
                    title={new Date(timelog.loggedOn).toLocaleString()}
                  >
                    {formatRelativeTime(timelog.loggedOn)}
                  </time>
                )}
              </div>

              {/* Row 3: Body text (optional) */}
              {timelog.body && (
                <p className="mt-2 text-sm text-gray-600 whitespace-pre-line">
                  {timelog.body}
                </p>
              )}
            </li>
          );
        })}
      </ul>

      {/* Pagination */}
      <PaginationControls
        currentPage={page}
        totalPages={totalPages}
        totalCount={totalCount}
        pageSize={DEFAULT_PAGE_SIZE}
        isFetching={isFetching}
        onPageChange={handlePageChange}
      />

      {/* Delete Confirmation Dialog */}
      <DeleteConfirmDialog
        isOpen={deleteConfirmId !== null}
        isDeleting={isDeleting}
        onConfirm={handleDeleteConfirm}
        onCancel={handleDeleteCancel}
      />
    </div>
  );
}

export default TimelogList;
