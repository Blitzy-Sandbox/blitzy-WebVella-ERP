/**
 * TaskList — Task Queue / List Page
 *
 * React page component replacing the monolith's `PcProjectWidgetTasksQueue`
 * ViewComponent (`WebVella.Erp.Plugins.Project/Components/PcProjectWidgetTasksQueue/`).
 *
 * Renders a sortable, filterable task queue/list with:
 *  - Due-type filter bar (All | Start Time Due | End Time Not Due)
 *  - DataTable with three columns: Task (priority icon + key + subject link),
 *    Owner (avatar + name), and Due Date (formatted)
 *  - Pagination via the shared DataTable component
 *
 * Source mapping:
 *  - PcProjectWidgetTasksQueueOptions  → TaskListProps + URL search params
 *  - TasksDueType C# enum              → TasksDueType TS enum
 *  - TaskService.GetTaskQueue()        → useTasks() TanStack Query hook
 *  - TaskService.GetTaskIconAndColor() → getTaskPriorityIconAndColor() helper
 *  - PcProjectWidgetTasksQueue rows    → DataTable column cell renderers
 *
 * Architecture:
 *  - Lazy-loaded via React.lazy() in router.tsx under `/projects/tasks`
 *  - Data fetched from `GET /v1/inventory/tasks` via the `useTasks` hook
 *  - Filter state persisted in URL search params for shareable/bookmarkable URLs
 *  - Zero jQuery, zero Bootstrap — Tailwind CSS only
 *  - Zero dangerouslySetInnerHTML — pure JSX cell renderers
 *
 * @module pages/projects/TaskList
 */

import React, { useState, useMemo } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useTasks, useProjectDashboard } from '../../hooks/useProjects';
import { DataTable } from '../../components/data-table/DataTable';
import type { DataTableColumn } from '../../components/data-table/DataTable';
import type { EntityRecord } from '../../types/record';

// ---------------------------------------------------------------------------
// Public Types
// ---------------------------------------------------------------------------

/**
 * Due-type filter for the task queue.
 *
 * Maps from the C# `TasksDueType` enum in `PcProjectWidgetTasksQueue.cs`
 * and `TaskService.cs`:
 *  - `All = 0`            → all tasks (no date filter)
 *  - `StartTimeDue = 1`   → tasks with start_time <= now (or null)
 *  - `EndTimeNotDue = 2`  → tasks with end_time in the future (or null)
 *
 * String values are used for URL-safe serialisation in search params.
 */
export enum TasksDueType {
  /** All open tasks — no date filter applied. */
  All = 'all',
  /** Tasks whose start time is due (start_time <= now or null). */
  StartTimeDue = 'start_time_due',
  /** Tasks whose end time has not passed (end_time >= tomorrow or null). */
  EndTimeNotDue = 'end_time_not_due',
}

/**
 * Props accepted by the {@link TaskList} component.
 *
 * Maps from the `PcProjectWidgetTasksQueueOptions` model:
 *  - `project_id`  → `projectId`
 *  - `user_id`     → `userId`
 *
 * Both are optional — when omitted the task list is unfiltered (global view).
 * When provided via route params or parent component props they pre-filter
 * the query to a specific project and/or assignee.
 */
export interface TaskListProps {
  /** Optional project ID to filter tasks by project scope. */
  projectId?: string;
  /** Optional user ID to filter tasks by assignee/owner. */
  userId?: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Default number of tasks per page for the "End Time Not Due" filter. */
const END_TIME_NOT_DUE_PAGE_SIZE = 10;

/** Default number of tasks per page for all other filters. */
const DEFAULT_PAGE_SIZE = 50;

/** Default avatar path for tasks without an owner image. */
const DEFAULT_AVATAR_PATH = '/assets/avatar.png';

/**
 * Human-readable labels for the due-type filter options.
 * Order matches the C# `GetEnumAsSelectOptions<TasksDueType>()` output.
 */
const DUE_TYPE_OPTIONS: ReadonlyArray<{
  readonly value: TasksDueType;
  readonly label: string;
}> = [
  { value: TasksDueType.All, label: 'All Tasks' },
  { value: TasksDueType.StartTimeDue, label: 'Start Time Due' },
  { value: TasksDueType.EndTimeNotDue, label: 'End Time Not Due' },
] as const;

// ---------------------------------------------------------------------------
// Priority Icon / Color Mapping
// ---------------------------------------------------------------------------

/**
 * Icon and colour descriptor for a task priority level.
 * Each priority value maps to a specific icon class and hex colour.
 */
interface PriorityStyle {
  /** SVG path data or icon class identifier. */
  readonly iconClass: string;
  /** Hex colour code for the priority indicator. */
  readonly color: string;
  /** Human-readable label for screen readers. */
  readonly label: string;
}

/**
 * Maps task priority string values to icon/colour pairs.
 *
 * Derived from `TaskService.GetTaskIconAndColor()` (lines 217-230) which
 * reads `SelectField.Options` from the "task" entity's "priority" field.
 * The monolith's options use:
 *  - `'1'` → low priority (green, arrow-down)
 *  - `'2'` → normal priority (blue, equals)
 *  - `'3'` → high/urgent priority (red, arrow-up)
 */
const PRIORITY_STYLES: Readonly<Record<string, PriorityStyle>> = {
  '1': { iconClass: 'low', color: '#4CAF50', label: 'Low priority' },
  '2': { iconClass: 'normal', color: '#2196F3', label: 'Normal priority' },
  '3': { iconClass: 'high', color: '#F44336', label: 'High priority' },
};

/** Fallback style when the priority value is not recognised. */
const DEFAULT_PRIORITY_STYLE: PriorityStyle = {
  iconClass: 'unknown',
  color: '#9E9E9E',
  label: 'Unknown priority',
};

/**
 * Returns the icon style descriptor for a given task priority value.
 *
 * Replaces the monolith's `TaskService.GetTaskIconAndColor(priority, out iconClass, out color)`
 * which read from the entity field definition's SelectField.Options at runtime.
 *
 * @param priority - Task priority value as string ('1', '2', '3')
 * @returns Priority style with icon class, colour, and accessible label
 */
function getTaskPriorityIconAndColor(priority: unknown): PriorityStyle {
  const key = String(priority ?? '');
  return PRIORITY_STYLES[key] ?? DEFAULT_PRIORITY_STYLE;
}

// ---------------------------------------------------------------------------
// Priority Icon SVG Component
// ---------------------------------------------------------------------------

/**
 * Inline SVG priority indicator icon.
 *
 * Renders an arrow-down (low), equals-bar (normal), or arrow-up (high)
 * coloured by the priority level. Uses `currentColor` inheritance so the
 * parent `style={{ color }}` prop controls the fill.
 *
 * Replaces the monolith's `<i class='{iconClass}' style='color:{color}'></i>`
 * inline HTML string from PcProjectWidgetTasksQueue (line 113).
 */
function PriorityIcon({
  priority,
}: {
  readonly priority: unknown;
}): React.JSX.Element {
  const style = getTaskPriorityIconAndColor(priority);

  /* Common SVG wrapper — 16×16 viewport, fills with currentColor */
  const svgProps = {
    width: 16,
    height: 16,
    viewBox: '0 0 16 16',
    fill: 'currentColor',
    'aria-hidden': true as const,
    className: 'inline-block flex-shrink-0',
  };

  switch (style.iconClass) {
    /* Low priority — arrow pointing down */
    case 'low':
      return (
        <svg {...svgProps} style={{ color: style.color }}>
          <path d="M8 12L3 6h10L8 12z" />
        </svg>
      );

    /* Normal priority — horizontal equals bar */
    case 'normal':
      return (
        <svg {...svgProps} style={{ color: style.color }}>
          <rect x="2" y="5" width="12" height="2" rx="0.5" />
          <rect x="2" y="9" width="12" height="2" rx="0.5" />
        </svg>
      );

    /* High priority — arrow pointing up */
    case 'high':
      return (
        <svg {...svgProps} style={{ color: style.color }}>
          <path d="M8 4L13 10H3L8 4z" />
        </svg>
      );

    /* Unknown/default — horizontal minus bar */
    default:
      return (
        <svg {...svgProps} style={{ color: style.color }}>
          <rect x="2" y="7" width="12" height="2" rx="0.5" />
        </svg>
      );
  }
}

// ---------------------------------------------------------------------------
// Date Formatting Helper
// ---------------------------------------------------------------------------

/**
 * Formats a date value for display.
 *
 * Replaces the C# `.ConvertToAppDate()` extension method used in
 * PcProjectWidgetTasksQueue (line 115) for the "date" column.
 *
 * Returns a locale-formatted date string or an em-dash when the value
 * is null/undefined (matching the monolith's empty-cell behaviour).
 *
 * @param value - Date string, Date object, or null/undefined
 * @returns Formatted date string or em-dash placeholder
 */
function formatDate(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }

  const date = value instanceof Date ? value : new Date(String(value));

  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  return date.toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

// ---------------------------------------------------------------------------
// URL Search Param Keys
// ---------------------------------------------------------------------------

/** Search parameter key for the due-type filter. */
const PARAM_DUE_TYPE = 'dueType';
/** Search parameter key for the project ID filter. */
const PARAM_PROJECT_ID = 'projectId';
/** Search parameter key for the user ID filter. */
const PARAM_USER_ID = 'userId';
/** Search parameter key for pagination page number. */
const PARAM_PAGE = 'page';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * Task Queue / List page component.
 *
 * Replaces `PcProjectWidgetTasksQueue.InvokeAsync()` (lines 41-152) which:
 *  1. Resolved project/user IDs from the data model context
 *  2. Called `TaskService.GetTaskQueue(projectId, userId, type, limit)`
 *  3. Fetched all users for owner resolution
 *  4. Built HTML strings for task/user/date cells
 *  5. Passed result records to the Display view
 *
 * The React implementation:
 *  1. Reads filter state from URL search params (or props)
 *  2. Calls `useTasks(params)` for paginated, filtered task data
 *  3. Optionally calls `useProjectDashboard(projectId)` for header stats
 *  4. Renders a filter bar + DataTable with typed column cell renderers
 *
 * @param props - Optional project/user scoping from parent component or route
 * @returns JSX element rendering the task queue page
 */
function TaskList({ projectId, userId }: TaskListProps): React.JSX.Element {
  /* ── URL search-param state for filter persistence ──────── */
  const [searchParams, setSearchParams] = useSearchParams();

  /**
   * Resolve effective filter values: props take precedence over URL params
   * to support both route-level usage (props) and direct navigation (URL).
   */
  const effectiveProjectId =
    projectId ?? searchParams.get(PARAM_PROJECT_ID) ?? undefined;
  const effectiveUserId =
    userId ?? searchParams.get(PARAM_USER_ID) ?? undefined;

  /* Due-type filter from URL (defaults to All) */
  const urlDueType = searchParams.get(PARAM_DUE_TYPE);
  const initialDueType = Object.values(TasksDueType).includes(
    urlDueType as TasksDueType,
  )
    ? (urlDueType as TasksDueType)
    : TasksDueType.All;

  const [dueType, setDueType] = useState<TasksDueType>(initialDueType);

  /* Page number from URL (1-based, defaults to 1) */
  const urlPage = searchParams.get(PARAM_PAGE);
  const [currentPage, setCurrentPage] = useState<number>(
    urlPage ? Math.max(1, parseInt(urlPage, 10) || 1) : 1,
  );

  /**
   * Effective page size — EndTimeNotDue uses a smaller limit (10) to match
   * the monolith's `var limit = options.Type == TasksDueType.EndTimeNotDue ? 10 : 50`
   * logic in PcProjectWidgetTasksQueue (line 90).
   */
  const pageSize =
    dueType === TasksDueType.EndTimeNotDue
      ? END_TIME_NOT_DUE_PAGE_SIZE
      : DEFAULT_PAGE_SIZE;

  /* ── Data fetching ─────────────────────────────────────── */
  const tasksQuery = useTasks({
    projectId: effectiveProjectId,
    assigneeId: effectiveUserId,
    page: currentPage,
    pageSize,
    sort:
      dueType === TasksDueType.All
        ? undefined
        : 'end_time:asc,priority:desc',
  });

  /* Dashboard stats for optional header context */
  const dashboardQuery = useProjectDashboard(effectiveProjectId);

  /* ── Filter change handler ─────────────────────────────── */
  /**
   * Updates the due-type filter, resets to page 1, and persists in URL.
   */
  function handleDueTypeChange(newDueType: TasksDueType): void {
    setDueType(newDueType);
    setCurrentPage(1);

    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set(PARAM_DUE_TYPE, newDueType);
        next.set(PARAM_PAGE, '1');
        return next;
      },
      { replace: true },
    );
  }

  /**
   * Handles page changes from the DataTable pagination control.
   */
  function handlePageChange(page: number): void {
    setCurrentPage(page);

    setSearchParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set(PARAM_PAGE, String(page));
        return next;
      },
      { replace: true },
    );
  }

  /* ── Column definitions (memoised) ─────────────────────── */
  const columns: DataTableColumn<EntityRecord>[] = useMemo(
    () => [
      {
        id: 'task',
        label: 'Task',
        sortable: true,
        name: 'subject',
        width: '55%',
        cell: (_value: unknown, record: EntityRecord): React.ReactNode => {
          const taskId = String(record.id ?? '');
          const taskKey = String(record['key'] ?? '');
          const subject = String(record['subject'] ?? '');
          const priority = record['priority'];

          return (
            <span className="inline-flex items-center gap-1.5">
              <PriorityIcon priority={priority} />
              <Link
                to={`/projects/tasks/${encodeURIComponent(taskId)}`}
                className="text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              >
                {taskKey ? `[${taskKey}] ` : ''}
                {subject || 'Untitled Task'}
              </Link>
            </span>
          );
        },
      },
      {
        id: 'owner',
        label: 'Owner',
        sortable: false,
        name: 'owner',
        width: '25%',
        cell: (_value: unknown, record: EntityRecord): React.ReactNode => {
          const ownerName = record['owner_id']
            ? String(record['owner_username'] ?? 'Unknown')
            : 'No Owner';
          const ownerImage = record['owner_image']
            ? String(record['owner_image'])
            : '';
          const avatarSrc = ownerImage
            ? `/fs${ownerImage}`
            : DEFAULT_AVATAR_PATH;

          return (
            <span className="inline-flex items-center gap-2">
              <img
                src={avatarSrc}
                alt=""
                aria-hidden="true"
                width={24}
                height={24}
                loading="lazy"
                decoding="async"
                className="size-6 rounded-full object-cover bg-gray-200"
              />
              <span className="truncate">{ownerName}</span>
            </span>
          );
        },
      },
      {
        id: 'due_date',
        label: 'Due Date',
        sortable: true,
        name: 'end_time',
        width: '20%',
        cell: (_value: unknown, record: EntityRecord): React.ReactNode => {
          const endTime = record['end_time'];
          const formatted = formatDate(endTime);

          /* Highlight overdue dates in red */
          const isOverdue =
            endTime !== null &&
            endTime !== undefined &&
            endTime !== '' &&
            new Date(String(endTime)).getTime() < Date.now();

          return (
            <span
              className={
                isOverdue
                  ? 'text-red-600 font-medium'
                  : 'text-gray-700'
              }
            >
              {formatted}
            </span>
          );
        },
      },
    ],
    [],
  );

  /* ── Derive records and total count from query result ─── */
  const records: EntityRecord[] = tasksQuery.data?.records ?? [];
  const totalCount: number = tasksQuery.data?.totalCount ?? 0;

  /* ── Dashboard summary values ──────────────────────────── */
  const dashboardData = dashboardQuery.data as EntityRecord | undefined;
  const totalTasks =
    dashboardData && typeof dashboardData['total_tasks'] === 'number'
      ? (dashboardData['total_tasks'] as number)
      : undefined;
  const openTasks =
    dashboardData && typeof dashboardData['open_tasks'] === 'number'
      ? (dashboardData['open_tasks'] as number)
      : undefined;

  /* ── Render ────────────────────────────────────────────── */
  return (
    <section
      className="flex flex-col gap-4"
      aria-labelledby="task-list-heading"
    >
      {/* ── Page header ─────────────────────────────────── */}
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1
            id="task-list-heading"
            className="text-2xl font-semibold text-gray-900"
          >
            Tasks
          </h1>
          {/* Optional dashboard summary */}
          {totalTasks !== undefined && (
            <p className="mt-0.5 text-sm text-gray-500">
              {openTasks !== undefined
                ? `${openTasks} open of ${totalTasks} total tasks`
                : `${totalTasks} total tasks`}
            </p>
          )}
        </div>
      </header>

      {/* ── Filter bar ──────────────────────────────────── */}
      <nav
        aria-label="Task due-type filters"
        className="flex flex-wrap items-center gap-2"
        role="group"
      >
        {DUE_TYPE_OPTIONS.map((option) => {
          const isActive = dueType === option.value;

          return (
            <button
              key={option.value}
              type="button"
              onClick={() => handleDueTypeChange(option.value)}
              aria-pressed={isActive}
              className={[
                'rounded-md px-3 py-1.5 text-sm font-medium transition-colors',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600',
                isActive
                  ? 'bg-blue-600 text-white shadow-sm'
                  : 'bg-gray-100 text-gray-700 hover:bg-gray-200',
              ].join(' ')}
            >
              {option.label}
            </button>
          );
        })}
      </nav>

      {/* ── Error state ─────────────────────────────────── */}
      {tasksQuery.isError && (
        <div
          role="alert"
          className="rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-700"
        >
          <p className="font-medium">Failed to load tasks</p>
          <p className="mt-1">
            {tasksQuery.error instanceof Error
              ? tasksQuery.error.message
              : 'An unexpected error occurred. Please try again.'}
          </p>
          <button
            type="button"
            onClick={() => void tasksQuery.refetch()}
            className="mt-2 rounded-md bg-red-100 px-3 py-1 text-sm font-medium text-red-800 hover:bg-red-200 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          >
            Retry
          </button>
        </div>
      )}

      {/* ── Data table ──────────────────────────────────── */}
      <DataTable<EntityRecord>
        data={records}
        columns={columns}
        totalCount={totalCount}
        pageSize={pageSize}
        currentPage={currentPage}
        onPageChange={handlePageChange}
        loading={tasksQuery.isLoading || tasksQuery.isFetching}
        emptyText="No tasks found matching the current filters."
        hover
        striped
        showHeader
        showFooter
        responsiveBreakpoint="md"
        name="task-queue"
        prefix="tasks-"
      />
    </section>
  );
}

export default TaskList;
