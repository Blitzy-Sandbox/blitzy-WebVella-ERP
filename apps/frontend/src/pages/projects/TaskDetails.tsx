/**
 * TaskDetails.tsx — Task Detail View Page Component
 *
 * Replaces the monolith's RecordDetails Razor Page for task entities, integrating
 * data from PcPostList (comments), PcTimelogList (timelogs), and PcFeedList
 * (activity feed) ViewComponents into a single React page with tabbed sections.
 *
 * Source: RecordDetails.cshtml[.cs] — overall page shell and action buttons
 * Source: TaskService.cs — task key generation, status/type resolution, timer logic
 * Source: ProjectController.cs — StartTimeLog, TaskSetStatus, TaskSetWatch endpoints
 * Source: PcPostList.cs — comment list embedded in tab
 * Source: PcTimelogList.cs — timelog list embedded in tab
 * Source: PcFeedList.cs — activity feed grouped by date, embedded in tab
 *
 * Route: /projects/tasks/:taskId
 *
 * @module pages/projects/TaskDetails
 */
import React, { useState, useCallback, useMemo, useEffect } from 'react';
import { useParams, Link, useNavigate } from 'react-router-dom';
import {
  useTask,
  useTimelogs,
  useComments,
  useActivityFeed,
  useUpdateTask,
  useDeleteTask,
} from '../../hooks/useProjects';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';
import type { EntityRecord } from '../../types/record';
import CommentList from './CommentList';
import TimelogList from './TimelogList';
import FeedList from './FeedList';
import { useAuthStore } from '../../stores/authStore';

/* ═══════════════════════════════════════════════════════════════
   Constants
   ═══════════════════════════════════════════════════════════════ */

/** Tab identifiers matching the three content sections */
const TAB_COMMENTS = 'comments';
const TAB_TIMELOGS = 'timelogs';
const TAB_ACTIVITY = 'activity';

/**
 * Priority configuration map — maps numeric priority values to display
 * properties. Replicates the monolith's priority icon rendering from
 * the RecordList/RecordDetails Razor Pages where priority was displayed
 * as colored badges with directional arrow icons.
 */
const PRIORITY_CONFIG: Record<
  string,
  { label: string; color: string; bgColor: string; icon: string }
> = {
  '1': {
    label: 'Critical',
    color: 'text-red-700',
    bgColor: 'bg-red-100',
    icon: '⬆',
  },
  '2': {
    label: 'High',
    color: 'text-orange-700',
    bgColor: 'bg-orange-100',
    icon: '↑',
  },
  '3': {
    label: 'Medium',
    color: 'text-yellow-700',
    bgColor: 'bg-yellow-100',
    icon: '→',
  },
  '4': {
    label: 'Low',
    color: 'text-blue-700',
    bgColor: 'bg-blue-100',
    icon: '↓',
  },
  '5': {
    label: 'Lowest',
    color: 'text-gray-600',
    bgColor: 'bg-gray-100',
    icon: '⬇',
  },
};

/** Default priority entry for unknown values */
const DEFAULT_PRIORITY = {
  label: 'None',
  color: 'text-gray-500',
  bgColor: 'bg-gray-50',
  icon: '–',
};

/**
 * Status color map — maps common task status labels to Tailwind badge colors.
 * Replicates the monolith's `$task_status_1n_task` relation resolution
 * from TaskService.SetCalculationFields which resolved the status entity
 * record to display the label with colored styling.
 */
const STATUS_COLORS: Record<string, { text: string; bg: string }> = {
  not_started: { text: 'text-gray-700', bg: 'bg-gray-100' },
  in_progress: { text: 'text-blue-700', bg: 'bg-blue-100' },
  completed: { text: 'text-green-700', bg: 'bg-green-100' },
  cancelled: { text: 'text-red-700', bg: 'bg-red-100' },
  on_hold: { text: 'text-yellow-700', bg: 'bg-yellow-100' },
  reopened: { text: 'text-purple-700', bg: 'bg-purple-100' },
};

/** Default status colors for unknown values */
const DEFAULT_STATUS_COLOR = { text: 'text-gray-700', bg: 'bg-gray-100' };

/**
 * Available task statuses for the status change dropdown.
 * Replicates the monolith's TaskService.GetTaskStatuses() which queried
 * the task_status entity for all active status records.
 */
const TASK_STATUSES = [
  { id: 'not_started', label: 'Not Started' },
  { id: 'in_progress', label: 'In Progress' },
  { id: 'completed', label: 'Completed' },
  { id: 'cancelled', label: 'Cancelled' },
  { id: 'on_hold', label: 'On Hold' },
  { id: 'reopened', label: 'Reopened' },
];

/* ═══════════════════════════════════════════════════════════════
   Utility Functions
   ═══════════════════════════════════════════════════════════════ */

/**
 * Safely extracts a string value from a dynamic EntityRecord field.
 * Returns the fallback value when the field is null, undefined, or not a string.
 */
function safeString(value: unknown, fallback = ''): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'number') return String(value);
  return fallback;
}

/**
 * Formats a date string (ISO 8601) into a human-readable display.
 * Returns the fallback string for invalid or missing values.
 */
function formatDate(value: unknown, fallback = '—'): string {
  if (!value || typeof value !== 'string') return fallback;
  try {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return fallback;
    return date.toLocaleDateString('en-US', {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });
  } catch {
    return fallback;
  }
}

/**
 * Formats elapsed time in minutes into a human-readable duration string.
 * Replicates the monolith's elapsed time display for active timelogs.
 */
function formatDuration(totalMinutes: number): string {
  if (totalMinutes <= 0) return '0m';
  const hours = Math.floor(totalMinutes / 60);
  const minutes = Math.round(totalMinutes % 60);
  if (hours > 0 && minutes > 0) return `${hours}h ${minutes}m`;
  if (hours > 0) return `${hours}h`;
  return `${minutes}m`;
}

/**
 * Calculates elapsed minutes between a start time and now.
 * Used for the active timer display.
 */
function getElapsedMinutes(startTimeStr: string): number {
  try {
    const start = new Date(startTimeStr);
    if (Number.isNaN(start.getTime())) return 0;
    const now = new Date();
    return Math.max(0, (now.getTime() - start.getTime()) / 60000);
  } catch {
    return 0;
  }
}

/**
 * Resolves the status label from a task record, checking both the resolved
 * status entity name and the raw status field. Mirrors the monolith's
 * `$task_status_1n_task` relation resolution in TaskService.SetCalculationFields.
 */
function resolveStatusLabel(task: EntityRecord): string {
  /* The server may return a nested status record from the relation */
  const statusRecord = task['$task_status_1n_task'] as
    | EntityRecord
    | EntityRecord[]
    | undefined;
  if (Array.isArray(statusRecord) && statusRecord.length > 0) {
    return safeString(statusRecord[0]?.['label'], safeString(task['status'] as string, 'Unknown'));
  }
  if (statusRecord && typeof statusRecord === 'object' && !Array.isArray(statusRecord)) {
    return safeString((statusRecord as EntityRecord)['label'], safeString(task['status'] as string, 'Unknown'));
  }
  return safeString(task['status'] as string, 'Unknown');
}

/**
 * Resolves the status ID from a task record for the status change dropdown.
 */
function resolveStatusId(task: EntityRecord): string {
  const statusRecord = task['$task_status_1n_task'] as
    | EntityRecord
    | EntityRecord[]
    | undefined;
  if (Array.isArray(statusRecord) && statusRecord.length > 0) {
    return safeString(statusRecord[0]?.['id']);
  }
  if (statusRecord && typeof statusRecord === 'object' && !Array.isArray(statusRecord)) {
    return safeString((statusRecord as EntityRecord)['id']);
  }
  return safeString(task['status_id'] as string);
}

/**
 * Resolves the type label from a task record. Mirrors `$task_type_1n_task`
 * relation resolution.
 */
function resolveTypeLabel(task: EntityRecord): string {
  const typeRecord = task['$task_type_1n_task'] as
    | EntityRecord
    | EntityRecord[]
    | undefined;
  if (Array.isArray(typeRecord) && typeRecord.length > 0) {
    return safeString(typeRecord[0]?.['label'], 'General');
  }
  if (typeRecord && typeof typeRecord === 'object' && !Array.isArray(typeRecord)) {
    return safeString((typeRecord as EntityRecord)['label'], 'General');
  }
  return safeString(task['type'] as string, 'General');
}

/**
 * Resolves the project name from a task record via `$project_nn_task` relation.
 * Mirrors TaskService.SetCalculationFields which resolved the project abbreviation.
 */
function resolveProjectName(task: EntityRecord): string {
  const projectRecords = task['$project_nn_task'] as
    | EntityRecord
    | EntityRecord[]
    | undefined;
  if (Array.isArray(projectRecords) && projectRecords.length > 0) {
    return safeString(projectRecords[0]?.['name'], 'Unassigned');
  }
  if (projectRecords && typeof projectRecords === 'object' && !Array.isArray(projectRecords)) {
    return safeString((projectRecords as EntityRecord)['name'], 'Unassigned');
  }
  return 'Unassigned';
}

/**
 * Resolves the owner display name from a task record. The owner may be
 * a nested user record or a simple string field depending on API shape.
 */
function resolveOwnerName(task: EntityRecord): string {
  const owner = task['owner'] as EntityRecord | string | undefined;
  if (typeof owner === 'string') return owner;
  if (owner && typeof owner === 'object') {
    const first = safeString(owner['firstName'] ?? owner['first_name']);
    const last = safeString(owner['lastName'] ?? owner['last_name']);
    if (first || last) return `${first} ${last}`.trim();
    return safeString(owner['email'] as string, 'Unknown');
  }
  return safeString(task['owner_id'] as string, 'Unassigned');
}

/**
 * Determines whether the current user is watching this task by checking
 * the `user_nn_task_watchers` many-to-many relation. Replicates the
 * monolith's ProjectController.TaskSetWatch which toggled watcher relations
 * based on current user identity.
 */
function isUserWatching(task: EntityRecord, userId: string | undefined): boolean {
  if (!userId) return false;
  const watchers = task['user_nn_task_watchers'] as
    | EntityRecord[]
    | string[]
    | undefined;
  if (!Array.isArray(watchers)) return false;
  return watchers.some((watcher) => {
    if (typeof watcher === 'string') return watcher === userId;
    if (watcher && typeof watcher === 'object') {
      return safeString(watcher['id']) === userId;
    }
    return false;
  });
}

/* ═══════════════════════════════════════════════════════════════
   TaskDetails Component
   ═══════════════════════════════════════════════════════════════ */

/**
 * Task detail page component.
 *
 * Renders the full task detail view with:
 * - Header: task key, subject, priority badge, status badge
 * - Action buttons: Edit, Start/Stop Timer, Watch/Unwatch, Delete
 * - Metadata grid: owner, project, type, dates, estimated minutes
 * - Tabbed content: Comments, Timelogs, Activity Feed
 *
 * Route: /projects/tasks/:taskId
 *
 * Replaces:
 * - RecordDetails.cshtml[.cs] — page shell and route handling
 * - TaskService.cs — task data resolution and calculation fields
 * - ProjectController.cs — timer/status/watch action endpoints
 * - PcPostList.cs, PcTimelogList.cs, PcFeedList.cs — embedded tab content
 */
export default function TaskDetails(): React.ReactElement {
  /* ─── Route Parameters ──────────────────────────────────────── */
  const { taskId = '' } = useParams<{ taskId: string }>();
  const navigate = useNavigate();

  /* ─── Data Fetching Hooks ───────────────────────────────────── */
  const {
    data: task,
    isLoading: isTaskLoading,
    isError: isTaskError,
    error: taskError,
  } = useTask(taskId);

  const { data: timelogsData } = useTimelogs({ taskId });
  const { data: commentsData } = useComments(taskId);
  const { data: _activityData } = useActivityFeed({ taskId });

  /* ─── Mutations ─────────────────────────────────────────────── */
  const updateTaskMutation = useUpdateTask();
  const deleteTaskMutation = useDeleteTask();

  /* ─── Auth Store ────────────────────────────────────────────── */
  const currentUser = useAuthStore((state) => state.currentUser);

  /* ─── Local UI State ────────────────────────────────────────── */
  const [activeTab, setActiveTab] = useState<string>(TAB_COMMENTS);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [showStatusDropdown, setShowStatusDropdown] = useState(false);
  const [timerElapsed, setTimerElapsed] = useState<string>('');

  /* ─── Computed Values ───────────────────────────────────────── */

  /** Priority badge configuration derived from task record */
  const priorityConfig = useMemo(() => {
    if (!task) return DEFAULT_PRIORITY;
    const priorityValue = safeString(task['priority'] as string);
    return PRIORITY_CONFIG[priorityValue] ?? DEFAULT_PRIORITY;
  }, [task]);

  /** Resolved status label from task status relation */
  const statusLabel = useMemo(() => {
    if (!task) return 'Unknown';
    return resolveStatusLabel(task);
  }, [task]);

  /** Status ID for dropdown comparison */
  const currentStatusId = useMemo(() => {
    if (!task) return '';
    return resolveStatusId(task);
  }, [task]);

  /** Status color configuration */
  const statusColor = useMemo(() => {
    const key = currentStatusId || statusLabel.toLowerCase().replace(/\s+/g, '_');
    return STATUS_COLORS[key] ?? DEFAULT_STATUS_COLOR;
  }, [currentStatusId, statusLabel]);

  /** Type label from task type relation */
  const typeLabel = useMemo(() => {
    if (!task) return 'General';
    return resolveTypeLabel(task);
  }, [task]);

  /** Project name from project relation */
  const projectName = useMemo(() => {
    if (!task) return 'Unassigned';
    return resolveProjectName(task);
  }, [task]);

  /** Owner display name */
  const ownerName = useMemo(() => {
    if (!task) return 'Unassigned';
    return resolveOwnerName(task);
  }, [task]);

  /** Whether the current user is watching the task */
  const isWatching = useMemo(() => {
    if (!task) return false;
    return isUserWatching(task, currentUser?.id);
  }, [task, currentUser]);

  /** Timer active state — when timelog_started_on is set, the timer is running */
  const isTimerActive = useMemo(() => {
    if (!task) return false;
    const startedOn = task['timelog_started_on'];
    return startedOn !== null && startedOn !== undefined && startedOn !== '';
  }, [task]);

  /** Tab count badges */
  const commentCount = useMemo(
    () => commentsData?.totalCount ?? 0,
    [commentsData],
  );
  const timelogCount = useMemo(
    () => timelogsData?.totalCount ?? 0,
    [timelogsData],
  );

  /** Task key display (e.g., "PRJ-42") */
  const taskKey = useMemo(() => {
    if (!task) return '';
    return safeString(task['key'] as string);
  }, [task]);

  /** Task subject */
  const taskSubject = useMemo(() => {
    if (!task) return '';
    return safeString(task['subject'] as string);
  }, [task]);

  /** Estimated minutes display */
  const estimatedMinutes = useMemo(() => {
    if (!task) return 0;
    const val = task['estimated_minutes'];
    if (typeof val === 'number') return val;
    if (typeof val === 'string') {
      const parsed = parseInt(val, 10);
      return Number.isNaN(parsed) ? 0 : parsed;
    }
    return 0;
  }, [task]);

  /* ─── Timer Elapsed Calculation ─────────────────────────────── */
  /**
   * Update timer display every 30 seconds when active. Uses useEffect
   * to set up a recurring interval that recalculates elapsed time from
   * the timelog_started_on timestamp.
   */
  useEffect(() => {
    if (!isTimerActive || !task) {
      setTimerElapsed('');
      return;
    }
    const startedOn = safeString(task['timelog_started_on'] as string);
    if (!startedOn) {
      setTimerElapsed('');
      return;
    }
    /* Calculate immediately on mount / dependency change */
    const elapsed = getElapsedMinutes(startedOn);
    setTimerElapsed(formatDuration(elapsed));

    /* Refresh every 30 seconds while the timer is running */
    const intervalId = window.setInterval(() => {
      const updated = getElapsedMinutes(startedOn);
      setTimerElapsed(formatDuration(updated));
    }, 30_000);

    return () => {
      window.clearInterval(intervalId);
    };
  }, [isTimerActive, task]);

  /* ─── Action Handlers ───────────────────────────────────────── */

  /**
   * Handles task status change. Replicates ProjectController.TaskSetStatus
   * which validated that the new status differs from current before updating.
   */
  const handleStatusChange = useCallback(
    (newStatusId: string) => {
      if (!taskId || newStatusId === currentStatusId) return;
      setShowStatusDropdown(false);
      updateTaskMutation.mutate({
        id: taskId,
        data: { status_id: newStatusId } as EntityRecord,
      });
    },
    [taskId, currentStatusId, updateTaskMutation],
  );

  /**
   * Toggles the timer (start/stop timelog tracking). Replicates
   * ProjectController.StartTimeLog which set timelog_started_on to
   * DateTime.UtcNow when starting, and cleared it when stopping.
   */
  const handleTimerToggle = useCallback(() => {
    if (!taskId) return;
    if (isTimerActive) {
      /* Stop timer — clear timelog_started_on */
      updateTaskMutation.mutate({
        id: taskId,
        data: { timelog_started_on: null } as unknown as EntityRecord,
      });
    } else {
      /* Start timer — set timelog_started_on to current UTC time */
      updateTaskMutation.mutate({
        id: taskId,
        data: {
          timelog_started_on: new Date().toISOString(),
        } as EntityRecord,
      });
    }
  }, [taskId, isTimerActive, updateTaskMutation]);

  /**
   * Toggles watch/unwatch for the current user. Replicates
   * ProjectController.TaskSetWatch which toggled the user_nn_task_watchers
   * many-to-many relation.
   */
  const handleWatchToggle = useCallback(() => {
    if (!taskId || !currentUser?.id) return;
    updateTaskMutation.mutate({
      id: taskId,
      data: {
        watcher_action: isWatching ? 'unwatch' : 'watch',
        watcher_user_id: currentUser.id,
      } as EntityRecord,
    });
  }, [taskId, currentUser, isWatching, updateTaskMutation]);

  /**
   * Handles task deletion with navigation to task list after success.
   * Replicates the monolith's RecordDetails page delete action which
   * redirected to the record list after successful deletion.
   */
  const handleDelete = useCallback(() => {
    if (!taskId) return;
    deleteTaskMutation.mutate(taskId, {
      onSuccess: () => {
        navigate('/projects/tasks', { replace: true });
      },
    });
  }, [taskId, deleteTaskMutation, navigate]);

  /** Opens delete confirmation dialog */
  const handleDeleteClick = useCallback(() => {
    setShowDeleteConfirm(true);
  }, []);

  /** Dismisses delete confirmation dialog */
  const handleDeleteCancel = useCallback(() => {
    setShowDeleteConfirm(false);
  }, []);

  /* ─── Tab Configuration ─────────────────────────────────────── */
  const tabs: TabConfig[] = useMemo(
    () => [
      {
        id: TAB_COMMENTS,
        label: `Comments${commentCount > 0 ? ` (${commentCount})` : ''}`,
        content: (
          <CommentList
            relatedRecordId={taskId}
            inline={true}
          />
        ),
      },
      {
        id: TAB_TIMELOGS,
        label: `Timelogs${timelogCount > 0 ? ` (${timelogCount})` : ''}`,
        content: (
          <TimelogList
            taskId={taskId}
            inline={true}
          />
        ),
      },
      {
        id: TAB_ACTIVITY,
        label: 'Activity Feed',
        content: (
          <FeedList
            taskId={taskId}
            inline={true}
          />
        ),
      },
    ],
    [taskId, commentCount, timelogCount],
  );

  /* ─── Loading State ─────────────────────────────────────────── */
  if (isTaskLoading) {
    return (
      <div
        className="flex items-center justify-center min-h-[50vh]"
        role="status"
        aria-label="Loading task details"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-e-transparent"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading task details…</span>
        </div>
      </div>
    );
  }

  /* ─── Error State ───────────────────────────────────────────── */
  if (isTaskError || !task) {
    return (
      <div
        className="flex items-center justify-center min-h-[50vh]"
        role="alert"
      >
        <div className="flex flex-col items-center gap-4 max-w-md text-center">
          <div
            className="flex items-center justify-center w-12 h-12 rounded-full bg-red-100"
            aria-hidden="true"
          >
            <span className="text-red-600 text-xl">!</span>
          </div>
          <h2 className="text-lg font-semibold text-gray-900">
            Failed to load task
          </h2>
          <p className="text-sm text-gray-500">
            {taskError?.message ?? 'The requested task could not be found.'}
          </p>
          <Link
            to="/projects/tasks"
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            ← Back to Tasks
          </Link>
        </div>
      </div>
    );
  }

  /* ─── Main Render ───────────────────────────────────────────── */
  return (
    <article className="max-w-5xl mx-auto px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Header Section ─────────────────────────────────────── */}
      <header className="mb-6">
        {/* Breadcrumb */}
        <nav aria-label="Breadcrumb" className="mb-4">
          <ol className="flex items-center gap-2 text-sm text-gray-500">
            <li>
              <Link
                to="/projects/tasks"
                className="hover:text-gray-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 rounded"
              >
                Tasks
              </Link>
            </li>
            <li aria-hidden="true" className="select-none">
              /
            </li>
            <li className="text-gray-900 font-medium" aria-current="page">
              {taskKey || taskId}
            </li>
          </ol>
        </nav>

        {/* Title Row */}
        <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-3 flex-wrap">
              {/* Task Key Badge */}
              {taskKey && (
                <span className="inline-flex items-center rounded-md bg-gray-100 px-2.5 py-1 text-xs font-mono font-semibold text-gray-700">
                  {taskKey}
                </span>
              )}
              {/* Priority Badge */}
              <span
                className={`inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-xs font-medium ${priorityConfig.bgColor} ${priorityConfig.color}`}
                title={`Priority: ${priorityConfig.label}`}
              >
                <span aria-hidden="true">{priorityConfig.icon}</span>
                {priorityConfig.label}
              </span>
              {/* Status Badge */}
              <span
                className={`inline-flex items-center rounded-md px-2.5 py-0.5 text-xs font-medium ${statusColor.bg} ${statusColor.text}`}
              >
                {statusLabel}
              </span>
            </div>
            <h1 className="mt-2 text-2xl font-bold text-gray-900 break-words">
              {taskSubject || 'Untitled Task'}
            </h1>
          </div>

          {/* Action Buttons */}
          <div className="flex items-center gap-2 flex-shrink-0 flex-wrap">
            {/* Edit Button */}
            <Link
              to={`/projects/tasks/${encodeURIComponent(taskId)}/edit`}
              className="inline-flex items-center gap-1.5 rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="m16.862 4.487 1.687-1.688a1.875 1.875 0 1 1 2.652 2.652L10.582 16.07a4.5 4.5 0 0 1-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 0 1 1.13-1.897l8.932-8.931Zm0 0L19.5 7.125M18 14v4.75A2.25 2.25 0 0 1 15.75 21H5.25A2.25 2.25 0 0 1 3 18.75V8.25A2.25 2.25 0 0 1 5.25 6H10"
                />
              </svg>
              Edit
            </Link>

            {/* Timer Toggle Button */}
            <button
              type="button"
              onClick={handleTimerToggle}
              disabled={updateTaskMutation.isPending}
              className={`inline-flex items-center gap-1.5 rounded-md px-3 py-2 text-sm font-medium shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                isTimerActive
                  ? 'bg-red-600 text-white hover:bg-red-700'
                  : 'bg-green-600 text-white hover:bg-green-700'
              } disabled:opacity-50 disabled:cursor-not-allowed`}
              aria-label={isTimerActive ? 'Stop timer' : 'Start timer'}
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                {isTimerActive ? (
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M5.25 7.5A2.25 2.25 0 0 1 7.5 5.25h9a2.25 2.25 0 0 1 2.25 2.25v9a2.25 2.25 0 0 1-2.25 2.25h-9a2.25 2.25 0 0 1-2.25-2.25v-9Z"
                  />
                ) : (
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="M5.25 5.653c0-.856.917-1.398 1.667-.986l11.54 6.347a1.125 1.125 0 0 1 0 1.972l-11.54 6.347a1.125 1.125 0 0 1-1.667-.986V5.653Z"
                  />
                )}
              </svg>
              {isTimerActive ? 'Stop' : 'Start'}
              {isTimerActive && timerElapsed && (
                <span className="ml-1 tabular-nums text-xs opacity-90">
                  {timerElapsed}
                </span>
              )}
            </button>

            {/* Watch/Unwatch Toggle */}
            <button
              type="button"
              onClick={handleWatchToggle}
              disabled={updateTaskMutation.isPending || !currentUser}
              className={`inline-flex items-center gap-1.5 rounded-md px-3 py-2 text-sm font-medium shadow-sm ring-1 ring-inset focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ${
                isWatching
                  ? 'bg-yellow-50 text-yellow-700 ring-yellow-300 hover:bg-yellow-100'
                  : 'bg-white text-gray-700 ring-gray-300 hover:bg-gray-50'
              } disabled:opacity-50 disabled:cursor-not-allowed`}
              aria-label={isWatching ? 'Unwatch task' : 'Watch task'}
              aria-pressed={isWatching}
            >
              <svg
                className="h-4 w-4"
                fill={isWatching ? 'currentColor' : 'none'}
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M2.036 12.322a1.012 1.012 0 0 1 0-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178Z"
                />
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"
                />
              </svg>
              {isWatching ? 'Unwatch' : 'Watch'}
            </button>

            {/* Status Dropdown */}
            <div className="relative">
              <button
                type="button"
                onClick={() => setShowStatusDropdown((prev) => !prev)}
                disabled={updateTaskMutation.isPending}
                className="inline-flex items-center gap-1.5 rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
                aria-haspopup="listbox"
                aria-expanded={showStatusDropdown}
                aria-label="Change task status"
              >
                Status
                <svg
                  className="h-4 w-4 text-gray-400"
                  fill="none"
                  viewBox="0 0 24 24"
                  strokeWidth={1.5}
                  stroke="currentColor"
                  aria-hidden="true"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    d="m19.5 8.25-7.5 7.5-7.5-7.5"
                  />
                </svg>
              </button>
              {showStatusDropdown && (
                <ul
                  role="listbox"
                  aria-label="Task statuses"
                  className="absolute right-0 z-10 mt-1 w-48 rounded-md bg-white py-1 shadow-lg ring-1 ring-black/5 focus:outline-none"
                >
                  {TASK_STATUSES.map((status) => {
                    const isActive = status.id === currentStatusId;
                    const colors =
                      STATUS_COLORS[status.id] ?? DEFAULT_STATUS_COLOR;
                    return (
                      <li key={status.id} role="option" aria-selected={isActive}>
                        <button
                          type="button"
                          onClick={() => handleStatusChange(status.id)}
                          disabled={isActive}
                          className={`flex w-full items-center gap-2 px-3 py-2 text-sm ${
                            isActive
                              ? 'bg-gray-50 font-semibold text-gray-900 cursor-default'
                              : 'text-gray-700 hover:bg-gray-50'
                          }`}
                        >
                          <span
                            className={`inline-block h-2 w-2 rounded-full ${colors.bg} ring-1 ring-inset ${colors.text.replace('text-', 'ring-')}`}
                            aria-hidden="true"
                          />
                          {status.label}
                          {isActive && (
                            <span className="ml-auto text-blue-600" aria-hidden="true">
                              ✓
                            </span>
                          )}
                        </button>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>

            {/* Delete Button */}
            <button
              type="button"
              onClick={handleDeleteClick}
              disabled={deleteTaskMutation.isPending}
              className="inline-flex items-center gap-1.5 rounded-md bg-white px-3 py-2 text-sm font-medium text-red-600 shadow-sm ring-1 ring-inset ring-red-300 hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
              aria-label="Delete task"
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                strokeWidth={1.5}
                stroke="currentColor"
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="m14.74 9-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 0 1-2.244 2.077H8.084a2.25 2.25 0 0 1-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 0 0-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 0 1 3.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 0 0-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 0 0-7.5 0"
                />
              </svg>
              Delete
            </button>
          </div>
        </div>

        {/* Mutation Error Alert */}
        {(updateTaskMutation.isError || deleteTaskMutation.isError) && (
          <div
            className="mt-4 rounded-md bg-red-50 p-3"
            role="alert"
          >
            <p className="text-sm text-red-700">
              {updateTaskMutation.error?.message ??
                deleteTaskMutation.error?.message ??
                'An error occurred while updating the task.'}
            </p>
          </div>
        )}
      </header>

      {/* ── Description Section ────────────────────────────────── */}
      {safeString(task['description'] as string) && (
        <section className="mb-6" aria-label="Task description">
          <div className="rounded-lg bg-gray-50 p-4">
            <p className="text-sm text-gray-700 whitespace-pre-wrap break-words">
              {safeString(task['description'] as string)}
            </p>
          </div>
        </section>
      )}

      {/* ── Metadata Grid ──────────────────────────────────────── */}
      <section className="mb-8" aria-label="Task metadata">
        <dl className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {/* Owner */}
          <div className="rounded-lg bg-white p-4 ring-1 ring-gray-200">
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Owner
            </dt>
            <dd className="mt-1 text-sm font-medium text-gray-900">
              {ownerName}
            </dd>
          </div>

          {/* Project */}
          <div className="rounded-lg bg-white p-4 ring-1 ring-gray-200">
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Project
            </dt>
            <dd className="mt-1 text-sm font-medium text-gray-900">
              {projectName}
            </dd>
          </div>

          {/* Type */}
          <div className="rounded-lg bg-white p-4 ring-1 ring-gray-200">
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Type
            </dt>
            <dd className="mt-1 text-sm font-medium text-gray-900">
              {typeLabel}
            </dd>
          </div>

          {/* Start Date */}
          <div className="rounded-lg bg-white p-4 ring-1 ring-gray-200">
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Start Date
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {formatDate(task['start_time'])}
            </dd>
          </div>

          {/* End Date */}
          <div className="rounded-lg bg-white p-4 ring-1 ring-gray-200">
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              End Date
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {formatDate(task['end_time'])}
            </dd>
          </div>

          {/* Estimated Time */}
          <div className="rounded-lg bg-white p-4 ring-1 ring-gray-200">
            <dt className="text-xs font-medium uppercase tracking-wider text-gray-500">
              Estimated Time
            </dt>
            <dd className="mt-1 text-sm text-gray-900">
              {estimatedMinutes > 0 ? formatDuration(estimatedMinutes) : '—'}
            </dd>
          </div>
        </dl>
      </section>

      {/* ── Tabbed Content Section ─────────────────────────────── */}
      <section aria-label="Task related content">
        <TabNav
          tabs={tabs}
          activeTabId={activeTab}
          onTabChange={setActiveTab}
          visibleTabs={3}
        />
      </section>

      {/* ── Delete Confirmation Dialog ─────────────────────────── */}
      {showDeleteConfirm && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
          role="dialog"
          aria-modal="true"
          aria-labelledby="delete-dialog-title"
        >
          <div className="mx-4 w-full max-w-sm rounded-lg bg-white p-6 shadow-xl">
            <h2
              id="delete-dialog-title"
              className="text-lg font-semibold text-gray-900"
            >
              Delete Task
            </h2>
            <p className="mt-2 text-sm text-gray-500">
              Are you sure you want to delete{' '}
              <strong className="font-medium text-gray-700">
                {taskKey ? `${taskKey} — ${taskSubject}` : taskSubject || 'this task'}
              </strong>
              ? This action cannot be undone. Related comments, timelogs, and
              feed items will also be removed.
            </p>
            <div className="mt-5 flex justify-end gap-3">
              <button
                type="button"
                onClick={handleDeleteCancel}
                disabled={deleteTaskMutation.isPending}
                className="rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleDelete}
                disabled={deleteTaskMutation.isPending}
                className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {deleteTaskMutation.isPending ? (
                  <>
                    <span
                      className="inline-block h-3.5 w-3.5 animate-spin rounded-full border-2 border-solid border-white border-e-transparent"
                      aria-hidden="true"
                    />
                    Deleting…
                  </>
                ) : (
                  'Delete'
                )}
              </button>
            </div>
            {deleteTaskMutation.isError && (
              <p className="mt-3 text-sm text-red-600" role="alert">
                {deleteTaskMutation.error?.message ?? 'Failed to delete task.'}
              </p>
            )}
          </div>
        </div>
      )}
    </article>
  );
}
