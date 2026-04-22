/**
 * @fileoverview NotificationCenter page component.
 *
 * Replaces the PostgreSQL LISTEN/NOTIFY pub/sub notification system from
 * WebVella.Erp/Notifications/ (NotificationContext.cs, Notification.cs,
 * ErpRecordChangeNotification.cs, NotificationHandlerAttribute.cs, Listener.cs)
 * with an SNS/SQS-driven notification UI using periodic API polling.
 *
 * Key behavioral mappings from the monolith:
 * - NotificationContext singleton dispatch → TanStack Query polling (30s interval)
 * - Channel-based case-insensitive listener filtering → TabNav tab-based filtering
 * - ErpRecordChangeNotification → RecordChangeNotification with entity links
 * - Assembly-scanning handler dispatch → React query cache invalidation
 * - PostgreSQL LISTEN/NOTIFY → HTTP polling via Notifications service Lambda
 *
 * Route: /notifications/center (lazy-loaded in router.tsx)
 * Auth: Protected route — accessible to authenticated users only.
 */

import React, { useState, useEffect, useCallback } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { get, put, del } from '../../api/client';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import {
  useNotifications,
  useMarkNotificationRead,
} from '../../hooks/useNotifications';
import type {
  Notification,
  NotificationType,
  NotificationListResponse,
  NotificationListParams,
} from '../../hooks/useNotifications';

/* ------------------------------------------------------------------ */
/*  Local Types                                                        */
/* ------------------------------------------------------------------ */

/**
 * Extended notification carrying record-change context.
 * Maps from the monolith's ErpRecordChangeNotification.cs which carried
 * EntityId (Guid), EntityName (string), and RecordId (Guid).
 */
interface RecordChangeNotification extends Notification {
  entityId: string;
  entityName: string;
  recordId: string;
}

/**
 * Tab filter categories replacing the monolith's channel-based Listener
 * filtering from NotificationContext.cs where listeners were matched by
 * Channel string (case-insensitive).
 */
type NotificationTab = 'all' | 'unread' | 'record-changes';

/* ------------------------------------------------------------------ */
/*  Constants                                                           */
/* ------------------------------------------------------------------ */

/** Polling interval in milliseconds — replaces PostgreSQL LISTEN/NOTIFY push. */
const POLL_INTERVAL_MS = 30_000;

/** Default page size for notification list queries. */
const DEFAULT_PAGE_SIZE = 50;

/** Query keys for cache management of center-specific queries. */
const CENTER_QUERY_KEYS = {
  unreadCount: ['notifications', 'center', 'unread-count'] as const,
} as const;

/* ------------------------------------------------------------------ */
/*  Utility Functions                                                   */
/* ------------------------------------------------------------------ */

/**
 * Type guard for RecordChangeNotification.
 * Checks for the presence of entityId, entityName, and recordId fields
 * that distinguish record-change notifications from regular notifications.
 */
function isRecordChangeNotification(
  notification: Notification,
): notification is RecordChangeNotification {
  const candidate = notification as unknown as Record<string, unknown>;
  return (
    typeof candidate.entityId === 'string' &&
    candidate.entityId.length > 0 &&
    typeof candidate.entityName === 'string' &&
    candidate.entityName.length > 0 &&
    typeof candidate.recordId === 'string' &&
    candidate.recordId.length > 0
  );
}

/**
 * Derives NotificationListParams from the active tab selection.
 * Replaces the monolith's channel-based case-insensitive listener filtering
 * with REST API query parameters.
 */
function getFilterParams(tab: NotificationTab): NotificationListParams {
  const base: NotificationListParams = { pageSize: DEFAULT_PAGE_SIZE };
  switch (tab) {
    case 'unread':
      return { ...base, status: 'sent' };
    case 'record-changes':
      return { ...base, type: 'in_app' };
    case 'all':
    default:
      return base;
  }
}

/**
 * Returns display label and Tailwind CSS classes for a notification type badge.
 * Maps the monolith's Notification.Channel string to colored type badges.
 */
function getTypeBadge(type: NotificationType): {
  label: string;
  className: string;
} {
  switch (type) {
    case 'email':
      return {
        label: 'Email',
        className:
          'inline-flex items-center rounded-full bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-800 dark:bg-blue-900 dark:text-blue-200',
      };
    case 'in_app':
      return {
        label: 'In-App',
        className:
          'inline-flex items-center rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900 dark:text-green-200',
      };
    case 'webhook':
      return {
        label: 'Webhook',
        className:
          'inline-flex items-center rounded-full bg-purple-100 px-2 py-0.5 text-xs font-medium text-purple-800 dark:bg-purple-900 dark:text-purple-200',
      };
    default:
      return {
        label: String(type),
        className:
          'inline-flex items-center rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium text-gray-800 dark:bg-gray-700 dark:text-gray-200',
      };
  }
}

/**
 * Formats an ISO timestamp into a human-readable relative time string.
 */
function formatRelativeTime(isoTimestamp: string): string {
  const date = new Date(isoTimestamp);
  if (Number.isNaN(date.getTime())) {
    return '';
  }

  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffSeconds = Math.floor(diffMs / 1000);
  const diffMinutes = Math.floor(diffSeconds / 60);
  const diffHours = Math.floor(diffMinutes / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffSeconds < 60) return 'Just now';
  if (diffMinutes < 60) return `${diffMinutes}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 7) return `${diffDays}d ago`;
  return date.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    year: diffDays > 365 ? 'numeric' : undefined,
  });
}

/**
 * Checks whether a notification is unread based on status and readAt.
 */
function isNotificationUnread(notification: Notification): boolean {
  return notification.status !== 'read' && !notification.readAt;
}

/* ------------------------------------------------------------------ */
/*  NotificationActions Sub-component                                   */
/* ------------------------------------------------------------------ */

/** Props for the bulk actions toolbar. */
interface NotificationActionsProps {
  unreadCount: number;
  totalCount: number;
  onMarkAllAsRead: () => void;
  isMarkingAll: boolean;
}

/**
 * Toolbar rendering notification summary counts and bulk action buttons.
 * The "Mark all as read" action maps to PUT /v1/notifications/read-all.
 */
function NotificationActions({
  unreadCount,
  totalCount,
  onMarkAllAsRead,
  isMarkingAll,
}: NotificationActionsProps) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-200 px-4 py-3 dark:border-gray-700">
      <p className="text-sm text-gray-600 dark:text-gray-400">
        <span className="font-semibold text-gray-900 dark:text-gray-100">
          {totalCount}
        </span>{' '}
        notification{totalCount !== 1 ? 's' : ''}
        {unreadCount > 0 && (
          <>
            {' · '}
            <span className="font-semibold text-blue-600 dark:text-blue-400">
              {unreadCount}
            </span>{' '}
            unread
          </>
        )}
      </p>

      {unreadCount > 0 && (
        <button
          type="button"
          onClick={onMarkAllAsRead}
          disabled={isMarkingAll}
          className="inline-flex items-center rounded-md bg-white px-3 py-1.5 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:pointer-events-none disabled:opacity-50 dark:bg-gray-800 dark:text-gray-300 dark:ring-gray-600 dark:hover:bg-gray-700"
          aria-label="Mark all notifications as read"
        >
          {isMarkingAll ? (
            <>
              <svg
                className="me-1.5 size-4 animate-spin"
                xmlns="http://www.w3.org/2000/svg"
                fill="none"
                viewBox="0 0 24 24"
                aria-hidden="true"
              >
                <circle
                  className="opacity-25"
                  cx="12"
                  cy="12"
                  r="10"
                  stroke="currentColor"
                  strokeWidth="4"
                />
                <path
                  className="opacity-75"
                  fill="currentColor"
                  d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                />
              </svg>
              Marking…
            </>
          ) : (
            'Mark all as read'
          )}
        </button>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  NotificationItem Sub-component                                      */
/* ------------------------------------------------------------------ */

/** Props for an individual notification card. */
interface NotificationItemProps {
  notification: Notification;
  isSelected: boolean;
  onSelect: (id: string) => void;
  onMarkAsRead: (id: string) => void;
  onDelete: (id: string) => void;
}

/**
 * Individual notification card with type badge, content preview,
 * read/unread indicator, and action buttons.
 *
 * For RecordChangeNotification items (from the monolith's
 * ErpRecordChangeNotification.cs), renders a Link component to the
 * record detail page using entityName and recordId.
 */
function NotificationItem({
  notification,
  isSelected,
  onSelect,
  onMarkAsRead,
  onDelete,
}: NotificationItemProps) {
  const unread = isNotificationUnread(notification);
  const badge = getTypeBadge(notification.type);
  const recordChange = isRecordChangeNotification(notification);

  return (
    <article
      role="listitem"
      aria-current={isSelected ? 'true' : undefined}
      className={[
        'group relative cursor-pointer border-b border-gray-100 px-4 py-3 transition-colors last:border-b-0 dark:border-gray-700',
        unread
          ? 'border-l-4 border-l-blue-500 bg-blue-50/50 dark:bg-blue-950/20'
          : 'border-l-4 border-l-transparent',
        isSelected
          ? 'bg-gray-100 dark:bg-gray-700/50'
          : 'hover:bg-gray-50 dark:hover:bg-gray-800/50',
      ]
        .filter(Boolean)
        .join(' ')}
    >
      {/* Accessible clickable overlay for selection */}
      <button
        type="button"
        className="absolute inset-0 z-0"
        onClick={() => onSelect(notification.id)}
        aria-label={`${unread ? 'Unread: ' : ''}${notification.subject || 'Notification'}`}
      >
        <span className="sr-only">Select notification</span>
      </button>

      <div className="relative z-10 flex flex-col gap-2">
        {/* Header row: badge + unread dot + timestamp */}
        <div className="flex items-center justify-between gap-2">
          <div className="flex items-center gap-2">
            <span className={badge.className}>{badge.label}</span>
            {unread && (
              <span
                className="size-2 shrink-0 rounded-full bg-blue-500"
                aria-label="Unread"
              />
            )}
          </div>
          <time
            dateTime={notification.createdOn}
            className="shrink-0 text-xs text-gray-500 dark:text-gray-400"
          >
            {formatRelativeTime(notification.createdOn)}
          </time>
        </div>

        {/* Subject line */}
        <h3
          className={[
            'text-sm leading-snug',
            unread
              ? 'font-semibold text-gray-900 dark:text-gray-100'
              : 'font-medium text-gray-700 dark:text-gray-300',
          ].join(' ')}
        >
          {notification.subject || 'No subject'}
        </h3>

        {/* Content preview — truncated to 2 lines */}
        {notification.content && (
          <p className="line-clamp-2 text-sm text-gray-600 dark:text-gray-400">
            {notification.content}
          </p>
        )}

        {/* Record change entity link — maps to ErpRecordChangeNotification */}
        {recordChange && (
          <Link
            to={`/records/${(notification as RecordChangeNotification).entityName}/${(notification as RecordChangeNotification).recordId}`}
            className="relative z-20 inline-flex items-center gap-1 text-sm font-medium text-blue-600 hover:text-blue-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 dark:text-blue-400 dark:hover:text-blue-300"
            onClick={(e) => e.stopPropagation()}
          >
            <svg
              className="size-4"
              xmlns="http://www.w3.org/2000/svg"
              fill="none"
              viewBox="0 0 24 24"
              strokeWidth={1.5}
              stroke="currentColor"
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25"
              />
            </svg>
            View {(notification as RecordChangeNotification).entityName} record
          </Link>
        )}

        {/* Expanded details shown when selected */}
        {isSelected && (
          <div className="mt-1 space-y-1.5 border-t border-gray-200 pt-2 dark:border-gray-600">
            {notification.sender && (
              <p className="text-xs text-gray-500 dark:text-gray-400">
                <span className="font-medium">From:</span>{' '}
                {notification.sender}
              </p>
            )}
            {notification.sentAt && (
              <p className="text-xs text-gray-500 dark:text-gray-400">
                <span className="font-medium">Sent:</span>{' '}
                {formatRelativeTime(notification.sentAt)}
              </p>
            )}
            {notification.lastError && (
              <p className="text-xs text-red-600 dark:text-red-400">
                <span className="font-medium">Error:</span>{' '}
                {notification.lastError}
              </p>
            )}
            {recordChange && (
              <p className="text-xs text-gray-500 dark:text-gray-400">
                <span className="font-medium">Entity:</span>{' '}
                {(notification as RecordChangeNotification).entityName}
                {' · '}
                <span className="font-medium">Record:</span>{' '}
                {(notification as RecordChangeNotification).recordId}
              </p>
            )}
          </div>
        )}

        {/* Action buttons — visible on hover/focus/selection */}
        <div
          className={[
            'flex items-center gap-2 pt-0.5',
            isSelected
              ? 'opacity-100'
              : 'opacity-0 transition-opacity group-hover:opacity-100 group-focus-within:opacity-100',
          ].join(' ')}
        >
          {unread && (
            <button
              type="button"
              className="relative z-20 rounded px-2 py-1 text-xs font-medium text-blue-600 hover:bg-blue-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-blue-600 dark:text-blue-400 dark:hover:bg-blue-900/40"
              onClick={(e) => {
                e.stopPropagation();
                onMarkAsRead(notification.id);
              }}
              aria-label={`Mark "${notification.subject || 'notification'}" as read`}
            >
              Mark as read
            </button>
          )}
          <button
            type="button"
            className="relative z-20 rounded px-2 py-1 text-xs font-medium text-red-600 hover:bg-red-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-red-600 dark:text-red-400 dark:hover:bg-red-900/40"
            onClick={(e) => {
              e.stopPropagation();
              onDelete(notification.id);
            }}
            aria-label={`Delete "${notification.subject || 'notification'}"`}
          >
            Delete
          </button>
        </div>
      </div>
    </article>
  );
}

/* ------------------------------------------------------------------ */
/*  NotificationList Sub-component                                      */
/* ------------------------------------------------------------------ */

/** Props for the scrollable notification list. */
interface NotificationListProps {
  notifications: Notification[];
  selectedNotification: string | null;
  onSelect: (id: string) => void;
  onMarkAsRead: (id: string) => void;
  onDelete: (id: string) => void;
  isLoading: boolean;
}

/**
 * Scrollable list of NotificationItem cards with loading and empty states.
 * Handles 0, 1, and N items correctly with no trailing divider.
 */
function NotificationList({
  notifications,
  selectedNotification,
  onSelect,
  onMarkAsRead,
  onDelete,
  isLoading,
}: NotificationListProps) {
  /* Loading skeleton */
  if (isLoading) {
    return (
      <div
        className="space-y-1 p-4"
        role="status"
        aria-label="Loading notifications"
      >
        {Array.from({ length: 5 }).map((_, idx) => (
          <div
            key={`skeleton-${String(idx)}`}
            className="animate-pulse rounded-lg bg-gray-100 dark:bg-gray-800"
          >
            <div className="space-y-2 p-4">
              <div className="flex items-center gap-2">
                <div className="h-5 w-16 rounded-full bg-gray-200 dark:bg-gray-700" />
                <div className="size-2 rounded-full bg-gray-200 dark:bg-gray-700" />
              </div>
              <div className="h-4 w-3/4 rounded bg-gray-200 dark:bg-gray-700" />
              <div className="h-3 w-full rounded bg-gray-200 dark:bg-gray-700" />
            </div>
          </div>
        ))}
        <span className="sr-only">Loading notifications…</span>
      </div>
    );
  }

  /* Empty state */
  if (notifications.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center px-4 py-16 text-center">
        <svg
          className="mb-4 size-16 text-gray-300 dark:text-gray-600"
          xmlns="http://www.w3.org/2000/svg"
          fill="none"
          viewBox="0 0 24 24"
          strokeWidth={1}
          stroke="currentColor"
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M14.857 17.082a23.848 23.848 0 005.454-1.31A8.967 8.967 0 0118 9.75v-.7V9A6 6 0 006 9v.75a8.967 8.967 0 01-2.312 6.022c1.733.64 3.56 1.085 5.455 1.31m5.714 0a24.255 24.255 0 01-5.714 0m5.714 0a3 3 0 11-5.714 0"
          />
        </svg>
        <h3 className="text-base font-medium text-gray-900 dark:text-gray-100">
          No notifications
        </h3>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
          You're all caught up. New notifications will appear here.
        </p>
      </div>
    );
  }

  /* Notification items list */
  return (
    <div
      role="list"
      aria-label="Notifications"
      className="max-h-[calc(100vh-16rem)] overflow-y-auto"
    >
      {notifications.map((notification) => (
        <NotificationItem
          key={notification.id}
          notification={notification}
          isSelected={selectedNotification === notification.id}
          onSelect={onSelect}
          onMarkAsRead={onMarkAsRead}
          onDelete={onDelete}
        />
      ))}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  Main NotificationCenter Component                                   */
/* ------------------------------------------------------------------ */

/**
 * NotificationCenter — In-app notification center page.
 *
 * Replaces the monolith's NotificationContext.cs PostgreSQL LISTEN/NOTIFY
 * pattern with TanStack Query polling (30-second interval). Provides
 * tab-based filtering (all / unread / record-changes) via TabNav,
 * individual mark-as-read via useMarkNotificationRead, bulk mark-all-read
 * via direct API mutation, and notification deletion.
 */
function NotificationCenter(): React.JSX.Element {
  /* ---- State ---- */
  const [activeTab, setActiveTab] = useState<NotificationTab>('all');
  const [selectedNotification, setSelectedNotification] = useState<
    string | null
  >(null);

  const queryClient = useQueryClient();

  /* ---- Main list query via shared hook ---- */
  const filterParams = getFilterParams(activeTab);
  const {
    data: notificationsData,
    isLoading,
    isError,
    error,
    refetch,
  } = useNotifications(filterParams);

  /* ---- Unread count query with built-in polling interval ---- */
  const { data: unreadCountData } = useQuery({
    queryKey: CENTER_QUERY_KEYS.unreadCount,
    queryFn: () =>
      get<NotificationListResponse>('/notifications', {
        status: 'sent',
        pageSize: 1,
      }),
    refetchInterval: POLL_INTERVAL_MS,
    staleTime: POLL_INTERVAL_MS / 2,
  });

  /* ---- Mutations ---- */

  /** Mark single notification as read — uses shared hook. */
  const markAsReadMutation = useMarkNotificationRead();

  /** Bulk mark all as read — PUT /v1/notifications/read-all. */
  const markAllAsReadMutation = useMutation({
    mutationFn: () => put('/notifications/read-all'),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['notifications'] });
    },
  });

  /** Delete notification — DELETE /v1/notifications/:id. */
  const deleteNotificationMutation = useMutation({
    mutationFn: (id: string) => del(`/v1/notifications/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['notifications'] });
    },
  });

  /* ---- Polling via useEffect for main list (30s) ---- */
  useEffect(() => {
    const interval = setInterval(() => {
      refetch();
    }, POLL_INTERVAL_MS);
    return () => clearInterval(interval);
  }, [refetch]);

  /* ---- Update document title with unread count ---- */
  useEffect(() => {
    const unread = unreadCountData?.object?.totalCount ?? 0;
    if (unread > 0) {
      document.title = `(${String(unread)}) Notifications — WebVella ERP`;
    } else {
      document.title = 'Notifications — WebVella ERP';
    }
    return () => {
      document.title = 'WebVella ERP';
    };
  }, [unreadCountData]);

  /* ---- Memoized handlers ---- */

  const handleTabChange = useCallback((tabId: string) => {
    setActiveTab(tabId as NotificationTab);
    setSelectedNotification(null);
  }, []);

  const handleSelect = useCallback((id: string) => {
    setSelectedNotification((prev) => (prev === id ? null : id));
  }, []);

  const handleMarkAsRead = useCallback(
    (id: string) => {
      markAsReadMutation.mutate(id);
    },
    [markAsReadMutation],
  );

  const handleMarkAllAsRead = useCallback(() => {
    markAllAsReadMutation.mutate();
  }, [markAllAsReadMutation]);

  const handleDelete = useCallback(
    (id: string) => {
      if (selectedNotification === id) {
        setSelectedNotification(null);
      }
      deleteNotificationMutation.mutate(id);
    },
    [deleteNotificationMutation, selectedNotification],
  );

  /* ---- Derived data ---- */
  const notifications: Notification[] =
    notificationsData?.object?.notifications ?? [];
  const totalCount = notificationsData?.object?.totalCount ?? 0;
  const unreadCount = unreadCountData?.object?.totalCount ?? 0;

  /* ---- Tab configuration ---- */
  const tabs: TabConfig[] = [
    { id: 'all', label: `All (${String(totalCount)})` },
    { id: 'unread', label: `Unread (${String(unreadCount)})` },
    { id: 'record-changes', label: 'Record Changes' },
  ];

  /* ---- Render ---- */
  return (
    <main className="mx-auto w-full max-w-4xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold tracking-tight text-gray-900 dark:text-gray-100">
          Notification Center
        </h1>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
          Stay informed about record changes, emails, and system events.
        </p>
      </div>

      {/* Tab navigation — replaces channel-based listener filtering */}
      <TabNav
        tabs={tabs}
        visibleTabs={3}
        activeTabId={activeTab}
        onTabChange={handleTabChange}
        className="mb-0"
      />

      {/* Content area */}
      <div className="overflow-hidden rounded-b-lg border border-t-0 border-gray-200 bg-white shadow-sm dark:border-gray-700 dark:bg-gray-800">
        {/* Error state */}
        {isError && (
          <div
            role="alert"
            className="flex items-center gap-3 bg-red-50 px-4 py-3 text-sm text-red-700 dark:bg-red-950/30 dark:text-red-400"
          >
            <svg
              className="size-5 shrink-0"
              xmlns="http://www.w3.org/2000/svg"
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
            <span>
              Failed to load notifications.{' '}
              {error instanceof Error ? error.message : 'Please try again.'}
            </span>
            <button
              type="button"
              onClick={() => refetch()}
              className="ms-auto shrink-0 font-medium underline hover:no-underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
            >
              Retry
            </button>
          </div>
        )}

        {/* Bulk actions toolbar */}
        {!isError && (
          <NotificationActions
            unreadCount={unreadCount}
            totalCount={totalCount}
            onMarkAllAsRead={handleMarkAllAsRead}
            isMarkingAll={markAllAsReadMutation.isPending}
          />
        )}

        {/* Notification list */}
        <NotificationList
          notifications={notifications}
          selectedNotification={selectedNotification}
          onSelect={handleSelect}
          onMarkAsRead={handleMarkAsRead}
          onDelete={handleDelete}
          isLoading={isLoading}
        />
      </div>
    </main>
  );
}

export default NotificationCenter;
