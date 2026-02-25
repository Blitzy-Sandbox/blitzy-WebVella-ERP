/**
 * Project/Task/Timelog/Comment TanStack Query Hooks
 *
 * TanStack Query 5 hooks for project management operations — tasks, timelogs,
 * comments, activity feeds, and project reporting. Replaces the monolith's:
 *
 *  - `TaskService.cs`       — Task CRUD, status transitions, priority ordering,
 *                              search-key computation (project abbr + task number),
 *                              filtered/sorted task queues by project/assignee/status
 *  - `TimelogService.cs`    — Timelog CRUD for per-task time tracking, aggregations
 *                              per user/task/date, monthly/weekly summaries
 *  - `CommentService.cs`    — Comment CRUD attached to tasks (threaded), author
 *                              ownership validation, cascade delete for child comments
 *  - `FeedService.cs`       — Activity feed generation for task/project CRUD events,
 *                              feed item creation for status changes and assignments
 *  - `ReportingService.cs`  — Project dashboards with task statistics, timelog
 *                              reports with date-range filtering grouped by user/task/date
 *  - `ProjectController.cs` — API endpoints: `api/v3.0/p/project/task/*`,
 *                              `api/v3.0/p/project/timelog/*`,
 *                              `api/v3.0/p/project/comment/*`
 *
 * Architecture:
 *  - Project entities (task, timelog, comment, feed) are dynamic entities
 *    defined via the entity management system; all use generic {@link EntityRecord}
 *    types rather than static interfaces
 *  - In the target architecture, these map to `/v1/inventory/*` API routes on
 *    the Inventory/Project microservice Lambda handlers
 *  - Post-CRUD hook logic (previously synchronous via `RecordHookManager`) is
 *    now handled server-side via SNS domain events
 *  - Activity feed near-real-time updates are achieved via optional
 *    `refetchInterval` on the `useActivityFeed` hook
 *
 * Query keys:
 *  - `['tasks', params]`                — Paginated task list with filters
 *  - `['tasks', id]`                    — Single task by ID
 *  - `['timelogs', params]`             — Paginated timelog list with filters
 *  - `['comments', taskId]`             — Comments for a specific task
 *  - `['activity-feed', params]`        — Project activity feed
 *  - `['project-dashboard', projectId]` — Project statistics / dashboard
 *  - `['timelog-summary', params]`       — Aggregated timelog reports
 *
 * @module hooks/useProjects
 */

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { get, post, put, del } from '../api/client';
import type {
  EntityRecord,
  EntityRecordList,
  RecordResponse,
  RecordListResponse,
} from '../types/record';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Keys
// ---------------------------------------------------------------------------

/**
 * Centralised query-key factory for all project-domain caches.
 * Prevents key collisions and enables targeted invalidation.
 *
 * Mirrors the monolith's per-entity cache partitioning — tasks, timelogs,
 * comments, feeds, and dashboard each have their own cache namespace so
 * that mutations on one entity do not unnecessarily invalidate another.
 */
const PROJECT_QUERY_KEYS = {
  tasks: {
    /** Root key for all task queries — used for broadest invalidation */
    all: ['tasks'] as const,
    /** Paginated task list with optional filters */
    list: (params?: TasksParams) => ['tasks', params] as const,
    /** Single task by ID */
    detail: (id: string) => ['tasks', id] as const,
  },
  timelogs: {
    /** Root key for all timelog queries */
    all: ['timelogs'] as const,
    /** Paginated timelog list with optional filters */
    list: (params?: TimelogsParams) => ['timelogs', params] as const,
  },
  comments: {
    /** Root key for all comment queries */
    all: ['comments'] as const,
    /** Comments for a specific task */
    byTask: (taskId: string) => ['comments', taskId] as const,
  },
  activityFeed: {
    /** Root key for all activity feed queries */
    all: ['activity-feed'] as const,
    /** Activity feed with optional filters */
    list: (params?: ActivityFeedParams) => ['activity-feed', params] as const,
  },
  projectDashboard: {
    /** Root key for all project dashboard queries */
    all: ['project-dashboard'] as const,
    /** Dashboard for a specific project (or global when undefined) */
    byProject: (projectId?: string) => ['project-dashboard', projectId] as const,
  },
  timelogSummary: {
    /** Root key for all timelog summary queries */
    all: ['timelog-summary'] as const,
    /** Timelog summary with aggregation parameters */
    byParams: (params?: TimelogSummaryParams) => ['timelog-summary', params] as const,
  },
} as const;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Default staleTime for project entity queries — 30 seconds (30 000 ms).
 *
 * Tasks and timelogs are moderately volatile. The monolith served fresh data
 * on every request via direct DB queries in RecordManager.Find(). The
 * 30-second staleTime balances network efficiency with data freshness.
 */
const PROJECT_DEFAULT_STALE_TIME_MS = 30 * 1000;

/**
 * Polling interval for the activity feed — 60 seconds (60 000 ms).
 *
 * The monolith's FeedService generated feed items on every CRUD event.
 * In the target architecture, feed items are created server-side via SNS
 * domain events. Polling every 60 seconds provides near-real-time updates
 * without excessive network traffic.
 */
const ACTIVITY_FEED_REFETCH_INTERVAL_MS = 60 * 1000;

/**
 * staleTime for dashboard/summary queries — 60 seconds (60 000 ms).
 *
 * Dashboard statistics and timelog summaries are aggregated data. Slightly
 * longer staleTime reduces load on the reporting endpoints.
 */
const DASHBOARD_STALE_TIME_MS = 60 * 1000;

// ---------------------------------------------------------------------------
// Parameter Interfaces
// ---------------------------------------------------------------------------

/**
 * Query parameters for the {@link useTasks} hook.
 *
 * Maps to query string parameters accepted by `GET /v1/inventory/tasks`.
 * Supports filtering by project, assignee, status, priority, date range,
 * and free-text search — fields originally defined in the project entity
 * schema by `ProjectPlugin.cs` patch files.
 *
 * Replaces the C# EQL-based query in `TaskService.GetTaskQueue()` which
 * supported projectId, userId, TasksDueType, and status filters.
 */
export interface TasksParams {
  /** Filter by project ID */
  projectId?: string;
  /** Filter by assignee user ID */
  assigneeId?: string;
  /** Filter by task status (e.g. "open", "in_progress", "completed", "cancelled") */
  status?: string;
  /** Filter by task priority (e.g. "low", "medium", "high", "urgent") */
  priority?: string;
  /** Filter tasks created/due after this date (ISO 8601 string) */
  startDate?: string;
  /** Filter tasks created/due before this date (ISO 8601 string) */
  endDate?: string;
  /** Free-text search across task name, description, search key */
  search?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Sort expression (e.g. "priority:desc", "created_on:desc") */
  sort?: string;
}

/**
 * Query parameters for the {@link useTimelogs} hook.
 *
 * Maps to query string parameters accepted by `GET /v1/inventory/timelogs`.
 * Supports filtering by task, user, and date range — fields used by
 * `TimelogService` for per-task/user timelog aggregation.
 */
export interface TimelogsParams {
  /** Filter by parent task ID */
  taskId?: string;
  /** Filter by user who logged the time */
  userId?: string;
  /** Filter timelogs on or after this date (ISO 8601 string) */
  startDate?: string;
  /** Filter timelogs on or before this date (ISO 8601 string) */
  endDate?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Sort expression (e.g. "logged_on:desc") */
  sort?: string;
}

/**
 * Query parameters for the {@link useActivityFeed} hook.
 *
 * Maps to query string parameters accepted by `GET /v1/inventory/feed`.
 * Replaces `FeedService` activity generation for task/project CRUD events.
 */
export interface ActivityFeedParams {
  /** Filter by project ID */
  projectId?: string;
  /** Filter by task ID */
  taskId?: string;
  /** Filter by feed item type (e.g. "task_created", "status_changed") */
  type?: string;
  /** Page number (1-based) */
  page?: number;
  /** Number of records per page */
  pageSize?: number;
  /** Sort expression (e.g. "created_on:desc") */
  sort?: string;
}

/**
 * Query parameters for the {@link useTimelogSummary} hook.
 *
 * Maps to query string parameters accepted by `GET /v1/inventory/timelogs/summary`.
 * Replaces `ReportingService` timelog aggregation with date-range filtering
 * and grouping by user, task, or date.
 */
export interface TimelogSummaryParams {
  /** Group results by dimension: "user", "task", or "date" */
  groupBy?: 'user' | 'task' | 'date';
  /** Filter by project ID */
  projectId?: string;
  /** Aggregate timelogs on or after this date (ISO 8601 string) */
  startDate?: string;
  /** Aggregate timelogs on or before this date (ISO 8601 string) */
  endDate?: string;
}

// ---------------------------------------------------------------------------
// Mutation Variable Interfaces
// ---------------------------------------------------------------------------

/**
 * Variables for the {@link useUpdateTask} mutation.
 *
 * Replaces in-process `RecordManager.UpdateRecord("task", record)` which
 * used merge semantics — only supplied fields are updated. The server
 * handles field normalisation and publishes SNS `inventory.task.updated`.
 */
interface UpdateTaskVariables {
  /** Task record ID (GUID string) to update */
  id: string;
  /** Partial task data — only changed fields required (merge semantics) */
  data: EntityRecord;
}

/**
 * Variables for the {@link useUpdateTimelog} mutation.
 *
 * Replaces in-process timelog update via `ProjectController.CreateTimelog`
 * pattern. Fields include minutes, isBillable, loggedOn, body.
 */
interface UpdateTimelogVariables {
  /** Timelog record ID (GUID string) to update */
  id: string;
  /** Partial timelog data — only changed fields required */
  data: EntityRecord;
}

/**
 * Variables for the {@link useCreateComment} mutation.
 *
 * Encapsulates the task scope and comment payload. Maps to
 * `CommentService.Create` which builds an EntityRecord with id,
 * created_by, created_on, body, parent_id, l_scope, l_related_records.
 */
interface CreateCommentVariables {
  /** Parent task ID the comment is attached to */
  taskId: string;
  /** Comment data (body, optional parent_id for threading) */
  data: EntityRecord;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Validates an API response envelope and throws a descriptive error when
 * the operation failed (`success === false`).
 *
 * Accepts a response shape matching the monolith's BaseResponseModel
 * members (`success`, `errors`, `message`). The `errors` array mirrors
 * `ErrorModel[]` with `{ key, value, message }` per error.
 *
 * @param response - API response with success flag and structured error details
 * @param fallbackMessage - Default error message when no specific errors returned
 * @throws Error with concatenated error messages from the response envelope
 */
function assertApiSuccess(
  response: Pick<BaseResponseModel, 'success' | 'errors' | 'message'>,
  fallbackMessage: string,
): void {
  if (!response.success) {
    const errorMessages = response.errors
      ?.map((err) => err.message)
      .filter(Boolean);
    throw new Error(
      errorMessages && errorMessages.length > 0
        ? errorMessages.join('; ')
        : response.message || fallbackMessage,
    );
  }
}

/**
 * Serialises {@link TasksParams} into query-string-ready key-value pairs
 * for the `GET /v1/inventory/tasks` endpoint.
 *
 * Only includes parameters that have been explicitly set — undefined and
 * empty-string values are omitted to keep the URL clean and allow server
 * defaults to apply.
 */
function buildTaskQueryParams(
  params?: TasksParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.projectId !== undefined && params.projectId !== '') {
    queryParams['projectId'] = params.projectId;
  }
  if (params.assigneeId !== undefined && params.assigneeId !== '') {
    queryParams['assigneeId'] = params.assigneeId;
  }
  if (params.status !== undefined && params.status !== '') {
    queryParams['status'] = params.status;
  }
  if (params.priority !== undefined && params.priority !== '') {
    queryParams['priority'] = params.priority;
  }
  if (params.startDate !== undefined && params.startDate !== '') {
    queryParams['startDate'] = params.startDate;
  }
  if (params.endDate !== undefined && params.endDate !== '') {
    queryParams['endDate'] = params.endDate;
  }
  if (params.search !== undefined && params.search !== '') {
    queryParams['search'] = params.search;
  }
  if (params.page !== undefined) {
    queryParams['page'] = params.page;
  }
  if (params.pageSize !== undefined) {
    queryParams['pageSize'] = params.pageSize;
  }
  if (params.sort !== undefined && params.sort !== '') {
    queryParams['sort'] = params.sort;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

/**
 * Serialises {@link TimelogsParams} into query-string-ready key-value pairs
 * for the `GET /v1/inventory/timelogs` endpoint.
 */
function buildTimelogQueryParams(
  params?: TimelogsParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.taskId !== undefined && params.taskId !== '') {
    queryParams['taskId'] = params.taskId;
  }
  if (params.userId !== undefined && params.userId !== '') {
    queryParams['userId'] = params.userId;
  }
  if (params.startDate !== undefined && params.startDate !== '') {
    queryParams['startDate'] = params.startDate;
  }
  if (params.endDate !== undefined && params.endDate !== '') {
    queryParams['endDate'] = params.endDate;
  }
  if (params.page !== undefined) {
    queryParams['page'] = params.page;
  }
  if (params.pageSize !== undefined) {
    queryParams['pageSize'] = params.pageSize;
  }
  if (params.sort !== undefined && params.sort !== '') {
    queryParams['sort'] = params.sort;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

/**
 * Serialises {@link ActivityFeedParams} into query-string-ready key-value pairs
 * for the `GET /v1/inventory/feed` endpoint.
 */
function buildActivityFeedQueryParams(
  params?: ActivityFeedParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.projectId !== undefined && params.projectId !== '') {
    queryParams['projectId'] = params.projectId;
  }
  if (params.taskId !== undefined && params.taskId !== '') {
    queryParams['taskId'] = params.taskId;
  }
  if (params.type !== undefined && params.type !== '') {
    queryParams['type'] = params.type;
  }
  if (params.page !== undefined) {
    queryParams['page'] = params.page;
  }
  if (params.pageSize !== undefined) {
    queryParams['pageSize'] = params.pageSize;
  }
  if (params.sort !== undefined && params.sort !== '') {
    queryParams['sort'] = params.sort;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

/**
 * Serialises {@link TimelogSummaryParams} into query-string-ready key-value
 * pairs for the `GET /v1/inventory/timelogs/summary` endpoint.
 */
function buildTimelogSummaryQueryParams(
  params?: TimelogSummaryParams,
): Record<string, unknown> | undefined {
  if (!params) return undefined;

  const queryParams: Record<string, unknown> = {};

  if (params.groupBy !== undefined) {
    queryParams['groupBy'] = params.groupBy;
  }
  if (params.projectId !== undefined && params.projectId !== '') {
    queryParams['projectId'] = params.projectId;
  }
  if (params.startDate !== undefined && params.startDate !== '') {
    queryParams['startDate'] = params.startDate;
  }
  if (params.endDate !== undefined && params.endDate !== '') {
    queryParams['endDate'] = params.endDate;
  }

  return Object.keys(queryParams).length > 0 ? queryParams : undefined;
}

// ---------------------------------------------------------------------------
// Task Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of project tasks with optional filters.
 *
 * Replaces the monolith's `TaskService.GetTaskQueue()` which executed a
 * complex EQL query against `rec_task` table with project/assignee/status/
 * priority/date filters, and `RecordManager.Find("task", query)` backed by
 * `DbRecordRepository.Find()`.
 *
 * The monolith's `TaskService.SetCalculationFields()` generated the
 * search key from project abbreviation + task number — this is now handled
 * server-side by the Inventory service.
 *
 * API: `GET /v1/inventory/tasks`
 * Query params: projectId, assigneeId, status, priority, startDate, endDate,
 *               search, page, pageSize, sort
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param params - Optional query parameters for filtering, pagination, and sorting
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function TaskList() {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useTasks({
 *     projectId: 'abc-123',
 *     status: 'open',
 *     page: 1,
 *     pageSize: 25,
 *     sort: 'priority:desc',
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <DataTable records={data?.records} totalCount={data?.totalCount} />;
 * }
 * ```
 */
export function useTasks(params?: TasksParams) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.tasks.list(params),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>(
        '/inventory/tasks',
        buildTaskQueryParams(params),
      );
      assertApiSuccess(response, 'Failed to fetch tasks');

      if (!response.object) {
        throw new Error('Task list response missing data');
      }

      return response.object;
    },

    staleTime: PROJECT_DEFAULT_STALE_TIME_MS,
  });
}

/**
 * Fetches a single task by ID.
 *
 * Replaces `TaskService.GetTask(taskId)` which executed an EQL query with
 * an ID equality filter against `rec_task`. The Inventory microservice
 * exposes a dedicated `/tasks/{id}` endpoint for single-record lookups.
 *
 * API: `GET /v1/inventory/tasks/{id}`
 * Response shape: `{ success, object: EntityRecord }`
 *
 * @param id - Task record ID (GUID string)
 * @returns TanStack Query result with `EntityRecord` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function TaskDetail({ id }: { id: string }) {
 *   const { data: task, isLoading, isError, error, refetch } = useTask(id);
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <TaskView record={task} />;
 * }
 * ```
 */
export function useTask(id: string) {
  return useQuery<RecordResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.tasks.detail(id),

    queryFn: async (): Promise<RecordResponse['object']> => {
      const response = await get<EntityRecord>(
        `/inventory/tasks/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to fetch task "${id}"`);

      if (!response.object) {
        throw new Error(`Task response missing data for "${id}"`);
      }

      return response.object;
    },

    staleTime: PROJECT_DEFAULT_STALE_TIME_MS,

    // Only fetch when id is provided — prevents unnecessary requests
    // when component mounts before route params resolve
    enabled: id.length > 0,
  });
}

/**
 * Creates a new task record.
 *
 * Replaces `RecordManager.CreateRecord("task", record)` which:
 *  1. Validated entity metadata and field types
 *  2. Normalised field values (auto-number for task number)
 *  3. Persisted via DbRecordRepository
 *  4. Triggered post-create hooks → SNS `inventory.task.created` event
 *
 * The monolith's `TaskService.SetCalculationFields()` auto-generated the
 * `x_search` key (project abbreviation + task number). This is now handled
 * server-side by the Inventory service Lambda.
 *
 * API: `POST /v1/inventory/tasks`
 * Body: {@link EntityRecord} with task field values
 * Response shape: `{ success, object: EntityRecord }` (created task)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateTaskForm() {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateTask();
 *   const handleSubmit = (formData: EntityRecord) => {
 *     mutate(formData);
 *   };
 * }
 * ```
 */
export function useCreateTask() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, EntityRecord>({
    mutationFn: async (data: EntityRecord): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>('/inventory/tasks', data);
      assertApiSuccess(response, 'Failed to create task');

      if (!response.object) {
        throw new Error('Create task response missing data');
      }

      return response.object;
    },

    onSuccess: () => {
      // Invalidate all task list queries so the new task appears
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.all,
      });
      // Task creation generates activity feed entries server-side
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.activityFeed.all,
      });
      // Dashboard task counts may have changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.projectDashboard.all,
      });
    },
  });
}

/**
 * Updates an existing task record with partial data (merge semantics).
 *
 * Replaces `RecordManager.UpdateRecord("task", record)` which used merge
 * semantics — only supplied fields are updated. Supports task status
 * transitions (Open → In Progress → Completed/Cancelled) and reassignment.
 *
 * Implements optimistic updates for status changes to improve UX:
 * the task status is immediately updated in the cache while the network
 * request is in-flight, and rolled back on failure.
 *
 * API: `PUT /v1/inventory/tasks/{id}`
 * Body: partial {@link EntityRecord}
 * Response shape: `{ success, object: EntityRecord }` (updated task)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditTaskForm({ taskId }: { taskId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateTask();
 *   const handleSubmit = (changes: EntityRecord) => {
 *     mutate({ id: taskId, data: changes });
 *   };
 * }
 * ```
 */
export function useUpdateTask() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, UpdateTaskVariables>({
    mutationFn: async ({
      id,
      data,
    }: UpdateTaskVariables): Promise<RecordResponse['object']> => {
      const response = await put<EntityRecord>(
        `/inventory/tasks/${encodeURIComponent(id)}`,
        data,
      );
      assertApiSuccess(response, `Failed to update task "${id}"`);

      if (!response.object) {
        throw new Error(`Update task response missing data for "${id}"`);
      }

      return response.object;
    },

    onMutate: async (variables: UpdateTaskVariables) => {
      // Optimistic update for the task detail cache — provides instant UI
      // feedback for status changes (Open → In Progress → Completed).
      // Cancel any in-flight refetches to prevent overwriting optimistic data.
      await queryClient.cancelQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.detail(variables.id),
      });

      // Snapshot the previous value for rollback on error
      const previousTask = queryClient.getQueryData<RecordResponse['object']>(
        PROJECT_QUERY_KEYS.tasks.detail(variables.id),
      );

      // Optimistically update the detail cache with merged data
      if (previousTask) {
        queryClient.setQueryData<RecordResponse['object']>(
          PROJECT_QUERY_KEYS.tasks.detail(variables.id),
          { ...previousTask, ...variables.data },
        );
      }

      return { previousTask };
    },

    onError: (_error, variables, context) => {
      // Rollback the optimistic update on failure
      const typedContext = context as
        | { previousTask: RecordResponse['object'] | undefined }
        | undefined;
      if (typedContext?.previousTask) {
        queryClient.setQueryData<RecordResponse['object']>(
          PROJECT_QUERY_KEYS.tasks.detail(variables.id),
          typedContext.previousTask,
        );
      }
    },

    onSettled: (_data, _error, variables) => {
      // Always refetch after settlement to ensure server state is canonical
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.all,
      });
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.detail(variables.id),
      });
      // Status changes and reassignments generate feed entries server-side
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.activityFeed.all,
      });
      // Dashboard statistics may have changed (e.g. task status counts)
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.projectDashboard.all,
      });
    },
  });
}

/**
 * Deletes a task record by ID.
 *
 * Replaces `RecordManager.DeleteRecord("task", recordId)` which:
 *  1. Enforced EntityPermission.CanDelete
 *  2. Executed pre-delete hooks
 *  3. Cleaned up file fields and related records (timelogs, comments)
 *  4. Deleted the record from `rec_task` table
 *  5. Executed post-delete hooks → SNS domain events
 *
 * API: `DELETE /v1/inventory/tasks/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteTaskButton({ taskId }: { taskId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteTask();
 *   return (
 *     <button onClick={() => mutate(taskId)} disabled={isPending}>
 *       Delete Task
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteTask() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(
        `/inventory/tasks/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to delete task "${id}"`);
    },

    onSuccess: () => {
      // Invalidate all task queries — the deleted task must
      // disappear from lists and detail caches
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.all,
      });
      // Related comments and timelogs may be cascade-deleted server-side
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.comments.all,
      });
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogs.all,
      });
      // Dashboard counts changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.projectDashboard.all,
      });
      // Feed entry for deletion
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.activityFeed.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Timelog Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of timelogs with optional filters.
 *
 * Replaces the monolith's `TimelogService` aggregation queries which
 * executed EQL against `rec_timelog` with task/user/date-range filters.
 * The `ProjectController.CreateTimelog` route pattern showed timelogs
 * have fields: minutes, isBillable, loggedOn, body, scope, relatedRecords.
 *
 * API: `GET /v1/inventory/timelogs`
 * Query params: taskId, userId, startDate, endDate, page, pageSize, sort
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param params - Optional query parameters for filtering, pagination, and sorting
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function TimelogList({ taskId }: { taskId?: string }) {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useTimelogs({
 *     taskId,
 *     startDate: '2024-01-01',
 *     endDate: '2024-12-31',
 *     page: 1,
 *     pageSize: 50,
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <TimelogTable records={data?.records} totalCount={data?.totalCount} />;
 * }
 * ```
 */
export function useTimelogs(params?: TimelogsParams) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.timelogs.list(params),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>(
        '/inventory/timelogs',
        buildTimelogQueryParams(params),
      );
      assertApiSuccess(response, 'Failed to fetch timelogs');

      if (!response.object) {
        throw new Error('Timelog list response missing data');
      }

      return response.object;
    },

    staleTime: PROJECT_DEFAULT_STALE_TIME_MS,
  });
}

/**
 * Creates a new timelog record.
 *
 * Replaces `ProjectController.CreateTimelog` which:
 *  1. Extracted minutes, isBillable, loggedOn, body from request
 *  2. Called `TimeLogService.Create(record)` with scope and relatedRecords
 *  3. Persisted via RecordManager
 *
 * API: `POST /v1/inventory/timelogs`
 * Body: {@link EntityRecord} with timelog field values
 * Response shape: `{ success, object: EntityRecord }` (created timelog)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateTimelogForm({ taskId }: { taskId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateTimelog();
 *   const handleSubmit = (formData: EntityRecord) => {
 *     mutate({ ...formData, taskId });
 *   };
 * }
 * ```
 */
export function useCreateTimelog() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, EntityRecord>({
    mutationFn: async (data: EntityRecord): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>('/inventory/timelogs', data);
      assertApiSuccess(response, 'Failed to create timelog');

      if (!response.object) {
        throw new Error('Create timelog response missing data');
      }

      return response.object;
    },

    onSuccess: () => {
      // Invalidate all timelog list queries so the new entry appears
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogs.all,
      });
      // Timelog creation affects task totals displayed in task lists/details
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.all,
      });
      // Dashboard and summary statistics changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.projectDashboard.all,
      });
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogSummary.all,
      });
      // Feed entry for timelog creation
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.activityFeed.all,
      });
    },
  });
}

/**
 * Updates an existing timelog record with partial data (merge semantics).
 *
 * Replaces in-process timelog update. Timelog fields that may change:
 * minutes, isBillable, loggedOn, body.
 *
 * API: `PUT /v1/inventory/timelogs/{id}`
 * Body: partial {@link EntityRecord}
 * Response shape: `{ success, object: EntityRecord }` (updated timelog)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function EditTimelogForm({ timelogId }: { timelogId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useUpdateTimelog();
 *   const handleSubmit = (changes: EntityRecord) => {
 *     mutate({ id: timelogId, data: changes });
 *   };
 * }
 * ```
 */
export function useUpdateTimelog() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, UpdateTimelogVariables>({
    mutationFn: async ({
      id,
      data,
    }: UpdateTimelogVariables): Promise<RecordResponse['object']> => {
      const response = await put<EntityRecord>(
        `/inventory/timelogs/${encodeURIComponent(id)}`,
        data,
      );
      assertApiSuccess(response, `Failed to update timelog "${id}"`);

      if (!response.object) {
        throw new Error(`Update timelog response missing data for "${id}"`);
      }

      return response.object;
    },

    onSuccess: () => {
      // Invalidate all timelog queries to reflect updated values
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogs.all,
      });
      // Timelog summary aggregations may have changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogSummary.all,
      });
      // Dashboard totals may have changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.projectDashboard.all,
      });
    },
  });
}

/**
 * Deletes a timelog record by ID.
 *
 * API: `DELETE /v1/inventory/timelogs/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteTimelogButton({ timelogId }: { timelogId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteTimelog();
 *   return (
 *     <button onClick={() => mutate(timelogId)} disabled={isPending}>
 *       Delete Timelog
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteTimelog() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(
        `/inventory/timelogs/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to delete timelog "${id}"`);
    },

    onSuccess: () => {
      // Invalidate all timelog queries
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogs.all,
      });
      // Task totals may have changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.tasks.all,
      });
      // Dashboard and summary aggregations changed
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.projectDashboard.all,
      });
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.timelogSummary.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Comment Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches comments for a specific task.
 *
 * Replaces the monolith's comment listing pattern from `CommentService`
 * and `ProjectController.CreateNewPcPostListItem` which populated
 * comment records with id, created_by, created_on, body, parent_id
 * (for threading), l_scope, and l_related_records.
 *
 * API: `GET /v1/inventory/tasks/{taskId}/comments`
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param taskId - Parent task ID to fetch comments for
 * @param params - Optional pagination parameters
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function CommentList({ taskId }: { taskId: string }) {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useComments(taskId);
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <Comments records={data?.records} totalCount={data?.totalCount} />;
 * }
 * ```
 */
export function useComments(taskId: string, params?: { page?: number; pageSize?: number }) {
  const queryParams = params
    ? (() => {
        const qp: Record<string, unknown> = {};
        if (params.page !== undefined) qp['page'] = params.page;
        if (params.pageSize !== undefined) qp['pageSize'] = params.pageSize;
        return Object.keys(qp).length > 0 ? qp : undefined;
      })()
    : undefined;

  return useQuery<RecordListResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.comments.byTask(taskId),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>(
        `/inventory/tasks/${encodeURIComponent(taskId)}/comments`,
        queryParams,
      );
      assertApiSuccess(response, `Failed to fetch comments for task "${taskId}"`);

      if (!response.object) {
        throw new Error(`Comment list response missing data for task "${taskId}"`);
      }

      return response.object;
    },

    staleTime: PROJECT_DEFAULT_STALE_TIME_MS,

    // Only fetch when taskId is provided
    enabled: taskId.length > 0,
  });
}

/**
 * Creates a new comment attached to a task.
 *
 * Replaces `ProjectController.CreateNewPcPostListItem` which called
 * `CommentService.Create(scopeRecord, relatedRecords, user, body, parentId)`.
 * The monolith's CommentService built an EntityRecord with:
 *  - id (new GUID)
 *  - created_by (current user ID)
 *  - created_on (DateTime.UtcNow)
 *  - body (comment text)
 *  - parent_id (for threaded replies, nullable)
 *  - l_scope (task → entity scoping)
 *  - l_related_records (related entity links)
 *
 * In the target architecture, created_by/created_on are set server-side
 * from the JWT claims and Lambda execution time.
 *
 * API: `POST /v1/inventory/tasks/{taskId}/comments`
 * Body: {@link EntityRecord} with comment field values
 * Response shape: `{ success, object: EntityRecord }` (created comment)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, `data`, and `reset`
 *
 * @example
 * ```tsx
 * function CreateCommentForm({ taskId }: { taskId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, data, reset } = useCreateComment();
 *   const handleSubmit = (body: string) => {
 *     mutate({ taskId, data: { body } });
 *   };
 * }
 * ```
 */
export function useCreateComment() {
  const queryClient = useQueryClient();

  return useMutation<RecordResponse['object'], Error, CreateCommentVariables>({
    mutationFn: async ({
      taskId,
      data,
    }: CreateCommentVariables): Promise<RecordResponse['object']> => {
      const response = await post<EntityRecord>(
        `/inventory/tasks/${encodeURIComponent(taskId)}/comments`,
        data,
      );
      assertApiSuccess(response, `Failed to create comment for task "${taskId}"`);

      if (!response.object) {
        throw new Error('Create comment response missing data');
      }

      return response.object;
    },

    onSuccess: (_data, variables) => {
      // Invalidate comments for the specific task
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.comments.byTask(variables.taskId),
      });
      // Comment creation generates feed entries server-side
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.activityFeed.all,
      });
    },
  });
}

/**
 * Deletes a comment by ID.
 *
 * Replaces `ProjectController.DeletePcPostListItem` which called
 * `CommentService.Delete(commentId, user)`. The monolith's delete
 * validated author ownership (`created_by == currentUser.Id`) and
 * cascaded to child comments (threaded replies) before deleting
 * via `RecordManager.DeleteRecord("comment", recordId)`.
 *
 * In the target architecture, author ownership validation and cascade
 * delete are handled server-side by the Inventory service Lambda.
 *
 * API: `DELETE /v1/inventory/comments/{id}`
 * Response: success envelope only (no typed object)
 *
 * @returns TanStack Query mutation with `mutate`, `mutateAsync`, `isPending`,
 *          `isError`, `error`, `isSuccess`, and `reset`
 *
 * @example
 * ```tsx
 * function DeleteCommentButton({ commentId }: { commentId: string }) {
 *   const { mutate, isPending, isError, error, isSuccess, reset } = useDeleteComment();
 *   return (
 *     <button onClick={() => mutate(commentId)} disabled={isPending}>
 *       Delete Comment
 *     </button>
 *   );
 * }
 * ```
 */
export function useDeleteComment() {
  const queryClient = useQueryClient();

  return useMutation<void, Error, string>({
    mutationFn: async (id: string): Promise<void> => {
      const response = await del(
        `/inventory/comments/${encodeURIComponent(id)}`,
      );
      assertApiSuccess(response, `Failed to delete comment "${id}"`);
    },

    onSuccess: () => {
      // Invalidate all comment queries — we don't know which task the
      // comment belonged to at this point, so invalidate broadly
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.comments.all,
      });
      // Feed entry for deletion
      void queryClient.invalidateQueries({
        queryKey: PROJECT_QUERY_KEYS.activityFeed.all,
      });
    },
  });
}

// ---------------------------------------------------------------------------
// Activity Feed Hook
// ---------------------------------------------------------------------------

/**
 * Fetches the project activity feed with optional near-real-time polling.
 *
 * Replaces the monolith's `FeedService` which generated activity feed items
 * for every task/project CRUD event (task created, status changed, timelog
 * logged, comment posted, etc.). In the target architecture, feed items
 * are created server-side via SNS domain events consumed by the Inventory
 * service.
 *
 * The hook supports an optional `refetchInterval` (default 60 seconds) to
 * provide near-real-time activity feed updates without WebSocket complexity.
 *
 * API: `GET /v1/inventory/feed`
 * Query params: projectId, taskId, type, page, pageSize, sort
 * Response shape: `{ success, object: EntityRecordList }`
 *
 * @param params - Optional query parameters for filtering and pagination
 * @param options - Optional TanStack Query overrides (e.g. refetchInterval)
 * @returns TanStack Query result with `EntityRecordList` data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, `refetch`, and `isFetching`
 *
 * @example
 * ```tsx
 * function ActivityFeed({ projectId }: { projectId?: string }) {
 *   const { data, isLoading, isError, error, refetch, isFetching } = useActivityFeed({
 *     projectId,
 *     page: 1,
 *     pageSize: 50,
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <FeedList items={data?.records} />;
 * }
 * ```
 */
export function useActivityFeed(
  params?: ActivityFeedParams,
  options?: { refetchInterval?: number | false },
) {
  return useQuery<RecordListResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.activityFeed.list(params),

    queryFn: async (): Promise<RecordListResponse['object']> => {
      const response = await get<EntityRecordList>(
        '/inventory/feed',
        buildActivityFeedQueryParams(params),
      );
      assertApiSuccess(response, 'Failed to fetch activity feed');

      if (!response.object) {
        throw new Error('Activity feed response missing data');
      }

      return response.object;
    },

    staleTime: PROJECT_DEFAULT_STALE_TIME_MS,

    // Near-real-time polling — defaults to 60 seconds, configurable via options
    refetchInterval:
      options?.refetchInterval !== undefined
        ? options.refetchInterval
        : ACTIVITY_FEED_REFETCH_INTERVAL_MS,
  });
}

// ---------------------------------------------------------------------------
// Dashboard & Reporting Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches project dashboard statistics.
 *
 * Replaces the monolith's `ReportingService` dashboard data aggregation
 * which computed task counts by status, priority distributions, recent
 * activity summaries, and overdue task counts for a given project.
 *
 * The response is a generic {@link BaseResponseModel} with an `object`
 * payload containing aggregate statistics. The shape is determined by
 * the Inventory service's dashboard endpoint.
 *
 * API: `GET /v1/inventory/dashboard`
 * Query params: projectId (optional — global dashboard when omitted)
 * Response shape: `{ success, object: EntityRecord }` (dashboard statistics)
 *
 * @param projectId - Optional project ID to scope dashboard (global when omitted)
 * @returns TanStack Query result with dashboard data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function ProjectDashboard({ projectId }: { projectId?: string }) {
 *   const { data, isLoading, isError, error, refetch } = useProjectDashboard(projectId);
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <DashboardCharts stats={data} />;
 * }
 * ```
 */
export function useProjectDashboard(projectId?: string) {
  const queryParams: Record<string, unknown> | undefined =
    projectId !== undefined && projectId !== ''
      ? { projectId }
      : undefined;

  return useQuery<RecordResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.projectDashboard.byProject(projectId),

    queryFn: async (): Promise<RecordResponse['object']> => {
      const response = await get<EntityRecord>(
        '/inventory/dashboard',
        queryParams,
      );
      assertApiSuccess(response, 'Failed to fetch project dashboard');

      if (!response.object) {
        throw new Error('Dashboard response missing data');
      }

      return response.object;
    },

    staleTime: DASHBOARD_STALE_TIME_MS,
  });
}

/**
 * Fetches aggregated timelog summary reports.
 *
 * Replaces the monolith's `ReportingService` timelog aggregation which
 * computed total hours grouped by user, task, or date within a date range.
 * Used for weekly/monthly timesheets and project time reports.
 *
 * API: `GET /v1/inventory/timelogs/summary`
 * Query params: groupBy (user|task|date), projectId, startDate, endDate
 * Response shape: `{ success, object: EntityRecord }` (summary statistics)
 *
 * @param params - Optional groupBy, projectId, and date range parameters
 * @returns TanStack Query result with summary data, plus `isLoading`,
 *          `isError`, `error`, `isSuccess`, and `refetch`
 *
 * @example
 * ```tsx
 * function TimelogReport({ projectId }: { projectId: string }) {
 *   const { data, isLoading, isError, error, refetch } = useTimelogSummary({
 *     groupBy: 'user',
 *     projectId,
 *     startDate: '2024-01-01',
 *     endDate: '2024-01-31',
 *   });
 *   if (isLoading) return <Spinner />;
 *   if (isError) return <ErrorAlert error={error} />;
 *   return <SummaryTable data={data} />;
 * }
 * ```
 */
export function useTimelogSummary(params?: TimelogSummaryParams) {
  return useQuery<RecordResponse['object'], Error>({
    queryKey: PROJECT_QUERY_KEYS.timelogSummary.byParams(params),

    queryFn: async (): Promise<RecordResponse['object']> => {
      const response = await get<EntityRecord>(
        '/inventory/timelogs/summary',
        buildTimelogSummaryQueryParams(params),
      );
      assertApiSuccess(response, 'Failed to fetch timelog summary');

      if (!response.object) {
        throw new Error('Timelog summary response missing data');
      }

      return response.object;
    },

    staleTime: DASHBOARD_STALE_TIME_MS,
  });
}
