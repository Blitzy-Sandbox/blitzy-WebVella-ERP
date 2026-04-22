/**
 * FeedList.tsx — Activity Feed Page Component
 *
 * Replaces the monolith's PcFeedList ViewComponent and the <wv-feed-list> Stencil
 * web component. Renders activity feed items grouped by creation date.
 *
 * Source: PcFeedList.cs (lines 79-95) — groups EntityRecord[] by
 * ((DateTime)x["created_on"]).ToString("dd MMMM"), serializes for Stencil.
 *
 * Source: FeedItemService.cs — fields: id, type, subject, body, created_by,
 * created_on, l_related_records, l_scope. Type values: "system", "comment", "timelog".
 *
 * @module pages/projects/FeedList
 */
import React, { useMemo, useState, useCallback } from 'react';
import { useActivityFeed } from '../../hooks/useProjects';
import type { EntityRecord } from '../../types/record';

/* ═══════════════════════════════════════════════════════════════
   Constants
   ═══════════════════════════════════════════════════════════════ */

/** Default number of feed items loaded per batch */
const FEED_PAGE_SIZE = 20;

/* ═══════════════════════════════════════════════════════════════
   Interfaces
   ═══════════════════════════════════════════════════════════════ */

/**
 * Typed feed item interface extracted from dynamic EntityRecord fields.
 * Maps FeedItemService.cs Create() fields: id, type, subject, body,
 * created_by, created_on, l_related_records, l_scope.
 */
interface FeedItem {
  /** Unique feed item identifier (GUID) */
  id: string;
  /** Feed item type — "system" | "comment" | "timelog" | custom string */
  type: string;
  /** Subject line (may contain server-rendered HTML) */
  subject: string;
  /** Body content (may contain server-rendered HTML) */
  body: string;
  /** Creator user identifier (GUID) */
  created_by: string;
  /** ISO 8601 date string of creation time */
  created_on: string;
  /** JSON-serialized array of related record GUIDs */
  l_related_records?: string;
  /** JSON-serialized array of scope strings (e.g., ["projects"]) */
  l_scope?: string;
}

/**
 * Props for the FeedList component.
 * Supports filter params for scoped feed queries and an inline embedding mode.
 */
interface FeedListProps {
  /** Filter feed items by task ID */
  taskId?: string;
  /** Filter feed items by project ID */
  projectId?: string;
  /** Filter feed items by scope (e.g., "projects") */
  scope?: string;
  /** When true, renders in compact mode without page wrapper (for embedding in TaskDetails) */
  inline?: boolean;
}

/**
 * Feed type icon descriptor — maps a type string to visual attributes.
 */
interface FeedTypeIconInfo {
  /** Display color (hex) */
  color: string;
  /** Human-readable label for accessibility */
  label: string;
}

/* ═══════════════════════════════════════════════════════════════
   Utility Functions
   ═══════════════════════════════════════════════════════════════ */

/**
 * Maps a feed item type to its icon color and accessible label.
 * Matches the Stencil wv-feed-list component type icon rendering:
 *  - comment → blue (#2196F3)
 *  - timelog → green (#4CAF50)
 *  - system/default → grey (#9E9E9E)
 */
function getFeedTypeIcon(type: string): FeedTypeIconInfo {
  switch (type) {
    case 'comment':
      return { color: '#2196F3', label: 'Comment' };
    case 'timelog':
      return { color: '#4CAF50', label: 'Time Log' };
    case 'system':
    default:
      return { color: '#9E9E9E', label: 'System' };
  }
}

/**
 * Formats a date string to "dd MMMM" format matching the C# PcFeedList.cs
 * grouping logic: ((DateTime)x["created_on"]).ToString("dd MMMM").
 *
 * @example formatDateKey("2024-01-15T10:30:00Z") → "15 January"
 */
function formatDateKey(dateStr: string): string {
  const date = new Date(dateStr);
  if (isNaN(date.getTime())) {
    return 'Unknown Date';
  }
  const day = date.getDate().toString().padStart(2, '0');
  const month = date.toLocaleDateString('en-US', { month: 'long' });
  return `${day} ${month}`;
}

/**
 * Groups an array of FeedItem by creation date, matching PcFeedList.cs:
 * records.GroupBy(x => ((DateTime)x["created_on"]).ToString("dd MMMM"))
 *
 * Items within each group maintain their original order (most recent first
 * if the API returns them sorted by created_on descending).
 */
function groupFeedByDate(items: FeedItem[]): Map<string, FeedItem[]> {
  const groups = new Map<string, FeedItem[]>();
  for (const item of items) {
    const dateKey = formatDateKey(item.created_on);
    const existing = groups.get(dateKey);
    if (existing) {
      existing.push(item);
    } else {
      groups.set(dateKey, [item]);
    }
  }
  return groups;
}

/**
 * Extracts a typed FeedItem from a dynamic EntityRecord.
 * Safely coerces field values with fallback defaults matching
 * FeedItemService.cs field defaults (type defaults to "system").
 */
function toFeedItem(record: EntityRecord): FeedItem {
  return {
    id: (record.id as string) ?? (record['id'] as string) ?? '',
    type: (record['type'] as string) ?? 'system',
    subject: (record['subject'] as string) ?? '',
    body: (record['body'] as string) ?? '',
    created_by: (record['created_by'] as string) ?? '',
    created_on: (record['created_on'] as string) ?? '',
    l_related_records: record['l_related_records'] as string | undefined,
    l_scope: record['l_scope'] as string | undefined,
  };
}

/**
 * Strips HTML tags from a string for safe plain-text rendering.
 * Feed item subject and body may contain server-rendered HTML.
 * Using regex strip instead of dangerouslySetInnerHTML for security.
 */
function stripHtmlTags(html: string): string {
  if (!html) {
    return '';
  }
  return html.replace(/<[^>]*>/g, '').trim();
}

/**
 * Formats a date string to a human-readable time (e.g., "10:30 AM").
 */
function formatTime(dateStr: string): string {
  const date = new Date(dateStr);
  if (isNaN(date.getTime())) {
    return '';
  }
  return date.toLocaleTimeString('en-US', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: true,
  });
}

/* ═══════════════════════════════════════════════════════════════
   Sub-Components
   ═══════════════════════════════════════════════════════════════ */

/**
 * Renders an inline SVG icon based on the feed item type.
 * Uses viewBox-based SVGs with no hardcoded width/height (CSS controls sizing).
 * Monochrome icons use explicit stroke color from getFeedTypeIcon.
 */
function TypeIcon({ type }: { type: string }): React.JSX.Element {
  const { color, label } = getFeedTypeIcon(type);

  if (type === 'comment') {
    return (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke={color}
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-label={label}
        role="img"
        className="inline-block h-5 w-5 flex-shrink-0"
      >
        <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />
      </svg>
    );
  }

  if (type === 'timelog') {
    return (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke={color}
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-label={label}
        role="img"
        className="inline-block h-5 w-5 flex-shrink-0"
      >
        <circle cx="12" cy="12" r="10" />
        <polyline points="12 6 12 12 16 14" />
      </svg>
    );
  }

  // Default: system gear icon
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke={color}
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-label={label}
      role="img"
      className="inline-block h-5 w-5 flex-shrink-0"
    >
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  );
}

/**
 * Renders a single feed item card within a date group.
 * Displays: type icon, subject, body, timestamp, and type badge.
 */
function FeedItemCard({ item }: { item: FeedItem }): React.JSX.Element {
  const { label: typeLabel, color: typeColor } = getFeedTypeIcon(item.type);
  const subjectText = stripHtmlTags(item.subject);
  const bodyText = stripHtmlTags(item.body);
  const timeStr = formatTime(item.created_on);

  return (
    <article
      className="relative flex gap-3 rounded-lg border border-gray-200 bg-white p-4 shadow-sm transition-colors hover:bg-gray-50"
      aria-label={`${typeLabel} activity: ${subjectText || 'Untitled'}`}
    >
      {/* Type icon indicator */}
      <div className="flex flex-shrink-0 items-start pt-0.5">
        <div
          className="flex h-8 w-8 items-center justify-center rounded-full"
          style={{ backgroundColor: `${typeColor}1A` }}
        >
          <TypeIcon type={item.type} />
        </div>
      </div>

      {/* Content area */}
      <div className="min-w-0 flex-1">
        {/* Subject and type badge row */}
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0 flex-1">
            {subjectText && (
              <p className="text-sm font-medium text-gray-900" style={{ overflowWrap: 'break-word' }}>
                {subjectText}
              </p>
            )}
          </div>
          <span
            className="inline-flex flex-shrink-0 items-center rounded-full px-2 py-0.5 text-xs font-medium"
            style={{ backgroundColor: `${typeColor}1A`, color: typeColor }}
          >
            {typeLabel}
          </span>
        </div>

        {/* Body */}
        {bodyText && (
          <p
            className="mt-1 text-sm text-gray-600"
            style={{ overflowWrap: 'break-word' }}
          >
            {bodyText}
          </p>
        )}

        {/* Timestamp */}
        {timeStr && (
          <p className="mt-2 text-xs text-gray-400">
            <time dateTime={item.created_on}>{timeStr}</time>
          </p>
        )}
      </div>
    </article>
  );
}

/**
 * Loading skeleton placeholder for feed items.
 */
function FeedItemSkeleton(): React.JSX.Element {
  return (
    <div className="flex animate-pulse gap-3 rounded-lg border border-gray-100 bg-white p-4">
      <div className="h-8 w-8 flex-shrink-0 rounded-full bg-gray-200" />
      <div className="min-w-0 flex-1 space-y-2">
        <div className="h-4 w-3/4 rounded bg-gray-200" />
        <div className="h-3 w-full rounded bg-gray-200" />
        <div className="h-3 w-1/4 rounded bg-gray-200" />
      </div>
    </div>
  );
}

/**
 * Empty state component when no feed items are found.
 */
function EmptyFeedState({ inline }: { inline: boolean }): React.JSX.Element {
  return (
    <div
      className={`flex flex-col items-center justify-center rounded-lg border border-dashed border-gray-300 bg-gray-50 ${
        inline ? 'px-4 py-8' : 'px-6 py-12'
      }`}
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        className="mb-3 h-10 w-10 text-gray-400"
        aria-hidden="true"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M12 9v3.75m9-.75a9 9 0 1 1-18 0 9 9 0 0 1 18 0zm-9 3.75h.008v.008H12v-.008z"
        />
      </svg>
      <p className="text-sm font-medium text-gray-500">No activity yet</p>
      <p className="mt-1 text-xs text-gray-400">
        Activity items will appear here as changes are made.
      </p>
    </div>
  );
}

/**
 * Error state component for failed feed data fetching.
 */
function ErrorState({
  onRetry,
  inline,
}: {
  onRetry: () => void;
  inline: boolean;
}): React.JSX.Element {
  return (
    <div
      className={`flex flex-col items-center justify-center rounded-lg border border-red-200 bg-red-50 ${
        inline ? 'px-4 py-8' : 'px-6 py-12'
      }`}
      role="alert"
    >
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        className="mb-3 h-10 w-10 text-red-400"
        aria-hidden="true"
      >
        <path
          strokeLinecap="round"
          strokeLinejoin="round"
          d="M12 9v3.75m-9.303 3.376c-.866 1.5.217 3.374 1.948 3.374h14.71c1.73 0 2.813-1.874 1.948-3.374L13.949 3.378c-.866-1.5-3.032-1.5-3.898 0L2.697 16.126ZM12 15.75h.007v.008H12v-.008Z"
        />
      </svg>
      <p className="text-sm font-medium text-red-600">Failed to load activity feed</p>
      <p className="mb-3 mt-1 text-xs text-red-500">
        There was a problem fetching the activity data. Please try again.
      </p>
      <button
        type="button"
        onClick={onRetry}
        className="rounded-md bg-red-100 px-3 py-1.5 text-xs font-medium text-red-700 transition-colors hover:bg-red-200 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500"
      >
        Retry
      </button>
    </div>
  );
}

/* ═══════════════════════════════════════════════════════════════
   Main Component
   ═══════════════════════════════════════════════════════════════ */

/**
 * FeedList — Activity feed page component.
 *
 * Replaces the PcFeedList ViewComponent and <wv-feed-list> Stencil web component.
 * Fetches activity feed items via the useActivityFeed TanStack Query hook,
 * groups them by creation date (matching C# "dd MMMM" format),
 * and renders a timeline-style feed with type-specific icons.
 *
 * Supports two rendering modes:
 * - Standalone page mode (default): includes page heading and full padding
 * - Inline mode (inline=true): compact layout for embedding in TaskDetails tabs
 *
 * Pagination is implemented as "Load More" — clicking the button increases
 * the page size to fetch additional items in a single query.
 */
function FeedList({
  taskId,
  projectId,
  scope,
  inline = false,
}: FeedListProps): React.JSX.Element {
  /* ─── Pagination state ─── */
  const [pageSize, setPageSize] = useState<number>(FEED_PAGE_SIZE);

  /* ─── Data fetching via TanStack Query ─── */
  const { data, isLoading, isError, refetch, isFetching } = useActivityFeed(
    {
      taskId,
      projectId,
      ...(scope ? { type: scope } : {}),
      page: 1,
      pageSize,
    },
    { refetchInterval: inline ? false : undefined },
  );

  /* ─── Extract records and pagination info ─── */
  const records: EntityRecord[] = data?.records ?? [];
  const totalCount: number = data?.totalCount ?? 0;
  const hasMore = records.length < totalCount;

  /* ─── Convert EntityRecord[] to FeedItem[] and group by date ─── */
  const feedItems = useMemo<FeedItem[]>(() => {
    return records.map(toFeedItem);
  }, [records]);

  const groupedFeed = useMemo<Map<string, FeedItem[]>>(() => {
    return groupFeedByDate(feedItems);
  }, [feedItems]);

  /* ─── Load more handler ─── */
  const handleLoadMore = useCallback(() => {
    setPageSize((prev) => prev + FEED_PAGE_SIZE);
  }, []);

  /* ─── Retry handler for error state ─── */
  const handleRetry = useCallback(() => {
    refetch();
  }, [refetch]);

  /* ─── Loading state ─── */
  if (isLoading) {
    return (
      <div className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}>
        {!inline && (
          <h1 className="mb-6 text-xl font-semibold text-gray-900">Activity Feed</h1>
        )}
        <div className="space-y-3" role="status" aria-label="Loading activity feed">
          <FeedItemSkeleton />
          <FeedItemSkeleton />
          <FeedItemSkeleton />
          <FeedItemSkeleton />
          <span className="sr-only">Loading activity feed…</span>
        </div>
      </div>
    );
  }

  /* ─── Error state ─── */
  if (isError) {
    return (
      <div className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}>
        {!inline && (
          <h1 className="mb-6 text-xl font-semibold text-gray-900">Activity Feed</h1>
        )}
        <ErrorState onRetry={handleRetry} inline={inline} />
      </div>
    );
  }

  /* ─── Empty state ─── */
  if (feedItems.length === 0) {
    return (
      <div className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}>
        {!inline && (
          <h1 className="mb-6 text-xl font-semibold text-gray-900">Activity Feed</h1>
        )}
        <EmptyFeedState inline={inline} />
      </div>
    );
  }

  /* ─── Feed content with date-grouped sections ─── */
  const dateEntries = Array.from(groupedFeed.entries());

  return (
    <div className={inline ? '' : 'mx-auto max-w-3xl px-4 py-6'}>
      {/* Page heading — standalone mode only */}
      {!inline && (
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-xl font-semibold text-gray-900">Activity Feed</h1>
          {/* Refresh button */}
          <button
            type="button"
            onClick={handleRetry}
            disabled={isFetching}
            className="inline-flex items-center gap-1.5 rounded-md bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 transition-colors hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
            aria-label="Refresh activity feed"
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              className={`h-4 w-4 ${isFetching ? 'animate-spin' : ''}`}
              aria-hidden="true"
            >
              <polyline points="23 4 23 10 17 10" />
              <polyline points="1 20 1 14 7 14" />
              <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
            </svg>
            {isFetching ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>
      )}

      {/* Feed timeline */}
      <div className="space-y-6" role="feed" aria-label="Activity feed">
        {dateEntries.map(([dateKey, items]) => (
          <section key={dateKey} aria-label={`Activities on ${dateKey}`}>
            {/* Date group header */}
            <div className="sticky top-0 z-10 mb-3 flex items-center gap-3">
              <h2 className="flex-shrink-0 text-sm font-semibold text-gray-500">
                {dateKey}
              </h2>
              <div className="h-px flex-1 bg-gray-200" aria-hidden="true" />
            </div>

            {/* Feed items within this date group */}
            <div className="space-y-3">
              {items.map((item) => (
                <FeedItemCard key={item.id || `${item.created_on}-${item.type}`} item={item} />
              ))}
            </div>
          </section>
        ))}
      </div>

      {/* Load More / Background fetch indicator */}
      <div className="mt-6">
        {hasMore && (
          <div className="flex justify-center">
            <button
              type="button"
              onClick={handleLoadMore}
              disabled={isFetching}
              className="inline-flex items-center gap-2 rounded-md bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 transition-colors hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
              aria-label={`Load more activity items. Showing ${feedItems.length} of ${totalCount}.`}
            >
              {isFetching ? (
                <>
                  <svg
                    className="h-4 w-4 animate-spin text-gray-400"
                    viewBox="0 0 24 24"
                    fill="none"
                    aria-hidden="true"
                  >
                    <circle
                      cx="12"
                      cy="12"
                      r="10"
                      stroke="currentColor"
                      strokeWidth="4"
                      className="opacity-25"
                    />
                    <path
                      fill="currentColor"
                      d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                      className="opacity-75"
                    />
                  </svg>
                  Loading…
                </>
              ) : (
                <>
                  Load More
                  <span className="text-xs text-gray-400">
                    ({feedItems.length} of {totalCount})
                  </span>
                </>
              )}
            </button>
          </div>
        )}

        {/* Background refetch indicator (non-blocking) */}
        {isFetching && !isLoading && !hasMore && (
          <p className="text-center text-xs text-gray-400" role="status">
            Updating…
          </p>
        )}
      </div>
    </div>
  );
}

/* ═══════════════════════════════════════════════════════════════
   Default Export (lazy-loadable)
   ═══════════════════════════════════════════════════════════════ */

export default FeedList;
