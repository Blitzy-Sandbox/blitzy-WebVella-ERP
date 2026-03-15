/**
 * @file useProjects.test.ts
 * @description Comprehensive Vitest unit tests for the 15 project management TanStack Query hooks
 * exported from src/hooks/useProjects.ts. These hooks replace the monolith's:
 *
 *  - TaskService.cs        — Task CRUD, status transitions (Open → InProgress → Completed/Cancelled),
 *                             filtering by project/assignee/status/priority, search-key computation
 *  - TimelogService.cs     — Timelog CRUD for per-task time tracking, aggregation per user/task/date,
 *                             monthly/weekly summaries
 *  - CommentService.cs     — Comment CRUD attached to tasks (threaded via parentId), author ownership
 *                             validation, cascade delete for child comments
 *  - FeedService.cs        — Activity feed generation from task/project CRUD events, feed item creation
 *                             for status changes and assignments
 *  - ReportingService.cs   — Project dashboards with task counts by status, timelog reports with
 *                             date-range filtering grouped by user/task/date
 *
 * Test suites cover all 15 hooks:
 *   1.  useTasks           — Paginated task list with filters (project, assignee, status, priority, dates, search)
 *   2.  useTask            — Single task by ID
 *   3.  useCreateTask      — Task creation mutation with cache invalidation
 *   4.  useUpdateTask      — Task update with optimistic updates and cache invalidation
 *   5.  useDeleteTask      — Task deletion with cross-entity cache invalidation
 *   6.  useTimelogs        — Paginated timelog list with filters (task, user, dates)
 *   7.  useCreateTimelog   — Timelog creation with cross-query invalidation (timelogs + tasks + dashboard)
 *   8.  useUpdateTimelog   — Timelog update with summary invalidation
 *   9.  useDeleteTimelog   — Timelog deletion with cross-query invalidation
 *   10. useComments        — Task-scoped comment list
 *   11. useCreateComment   — Comment creation with task-scoped invalidation
 *   12. useDeleteComment   — Comment deletion with broad invalidation
 *   13. useActivityFeed    — Near-real-time activity feed with refetchInterval polling
 *   14. useProjectDashboard — Project dashboard aggregate statistics
 *   15. useTimelogSummary  — Timelog aggregation grouped by user/task/date
 *
 * Monolith parity:
 *   - Task status values match Project plugin entity definition: Open, InProgress, Completed, Cancelled
 *   - Task priority values: High, Medium, Low (from ProjectPlugin patch files)
 *   - Timelog hours tracked as decimal (from TimelogService)
 *   - Comment threading via parentId (from CommentService cascade logic)
 *   - Activity feed types match FeedService event patterns (TaskCreated, TimelogCreated, etc.)
 *   - Dashboard aggregates mirror ReportingService calculations (task counts by status, total hours)
 *   - All API endpoints follow /v1/inventory/* convention (Inventory service Lambda handlers)
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createElement, type ReactNode } from 'react';

// ──────────────────────────────────────────────────────────────────────────────
// Module mocks — vi.mock calls are hoisted by Vitest before all imports
// ──────────────────────────────────────────────────────────────────────────────

vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
}));

// ──────────────────────────────────────────────────────────────────────────────
// Module-under-test imports (uses mocked API client)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useTasks,
  useTask,
  useCreateTask,
  useUpdateTask,
  useDeleteTask,
  useTimelogs,
  useCreateTimelog,
  useUpdateTimelog,
  useDeleteTimelog,
  useComments,
  useCreateComment,
  useDeleteComment,
  useActivityFeed,
  useProjectDashboard,
  useTimelogSummary,
} from '../../../src/hooks/useProjects';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';
import type { ApiResponse } from '../../../src/api/client';

// Type-only imports for mock data typing — ensures mock data matches the actual
// API response structures that the project hooks expect from Inventory service
// Lambda handlers.
import type {
  EntityRecord,
  EntityRecordList,
  RecordResponse,
  RecordListResponse,
} from '../../../src/types/record';
import type { BaseResponseModel } from '../../../src/types/common';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures — modelled after Project plugin entity definitions
//
// Tasks match the entity created in ProjectPlugin patch files with fields:
//   id, subject, status (Open/InProgress/Completed/Cancelled), priority
//   (High/Medium/Low), assigneeId, projectId, startDate, endDate, description
//
// Timelogs match TimelogService fields: taskId, userId, hours, date, description
//
// Comments match CommentService fields: taskId, userId, body, parentId, createdOn
//
// Feed items match FeedService event patterns: type, data, createdOn
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Validates that a mock response envelope conforms to the shared
 * BaseResponseModel contract (success, errors, message). This type alias
 * ensures compile-time safety between mock data and the actual API shape.
 */
type MockEnvelopeFields = Pick<BaseResponseModel, 'success' | 'errors' | 'message'>;

/** Mock task fixture — InProgress status, High priority (from TaskService task entity). */
const mockTask: EntityRecord = {
  id: 'task-guid',
  subject: 'Fix login bug',
  status: 'InProgress',
  priority: 'High',
  assigneeId: 'user-guid',
  projectId: 'project-guid',
  startDate: '2024-01-15',
  endDate: '2024-01-31',
  description: 'Login page returns 500',
};

/** Second task fixture for list testing — Open status, Medium priority. */
const mockTaskTwo: EntityRecord = {
  id: 'task-guid-2',
  subject: 'Add dark mode',
  status: 'Open',
  priority: 'Medium',
  assigneeId: 'user-guid-2',
  projectId: 'project-guid',
  startDate: '2024-02-01',
  endDate: '2024-02-15',
  description: 'Implement dark theme support',
};

/** Mock timelog fixture — 2.5 hours (from TimelogService time tracking). */
const mockTimelog: EntityRecord = {
  id: 'timelog-guid',
  taskId: 'task-guid',
  userId: 'user-guid',
  hours: 2.5,
  date: '2024-01-20',
  description: 'Debugging',
};

/** Second timelog fixture for list testing — different user. */
const mockTimelogTwo: EntityRecord = {
  id: 'timelog-guid-2',
  taskId: 'task-guid',
  userId: 'user-guid-2',
  hours: 1.5,
  date: '2024-01-21',
  description: 'Code review',
};

/**
 * Mock comment fixture — top-level comment (parentId: null).
 * Matches CommentService fields: id, taskId, userId, body, createdOn, parentId.
 */
const mockComment: EntityRecord = {
  id: 'comment-guid',
  taskId: 'task-guid',
  userId: 'user-guid',
  body: 'Found the root cause',
  createdOn: '2024-01-20T10:00:00Z',
  parentId: null,
};

/**
 * Mock threaded reply — parentId references the top-level comment.
 * Tests the CommentService's threading model where child comments
 * reference parent_id for nested display.
 */
const mockCommentReply: EntityRecord = {
  id: 'comment-reply-guid',
  taskId: 'task-guid',
  userId: 'user-guid-2',
  body: 'Good find!',
  createdOn: '2024-01-20T11:00:00Z',
  parentId: 'comment-guid',
};

/**
 * Mock feed item — TaskCreated event (from FeedService event patterns).
 * The monolith generated feed items for every CRUD event on task/project entities.
 */
const mockFeedItem: EntityRecord = {
  id: 'feed-guid',
  type: 'TaskCreated',
  data: { taskId: 'task-guid', subject: 'Fix login bug' },
  createdOn: '2024-01-15T08:00:00Z',
};

/** Second feed item — TimelogCreated event for feed list testing. */
const mockFeedItemTwo: EntityRecord = {
  id: 'feed-guid-2',
  type: 'TimelogCreated',
  data: { taskId: 'task-guid', hours: 2.5 },
  createdOn: '2024-01-20T09:00:00Z',
};

/**
 * Mock dashboard data — aggregate statistics matching ReportingService
 * dashboard computation: task counts by status, total hours, overdue count.
 */
const mockDashboardData: EntityRecord = {
  id: 'dashboard-guid',
  totalTasks: 25,
  openTasks: 10,
  inProgressTasks: 8,
  completedTasks: 5,
  cancelledTasks: 2,
  totalHoursLogged: 120.5,
  overdueTasks: 3,
};

/**
 * Mock timelog summary — aggregated hours grouped by user, matching
 * ReportingService timelog aggregation computation.
 */
const mockTimelogSummaryData: EntityRecord = {
  id: 'summary-guid',
  groupBy: 'user',
  totalHours: 40,
  entries: [
    { userId: 'user-guid', userName: 'Alice', totalHours: 25 },
    { userId: 'user-guid-2', userName: 'Bob', totalHours: 15 },
  ],
};

// ──────────────────────────────────────────────────────────────────────────────
// Mock response helpers
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Creates a successful mock ApiResponse envelope with fields consistent with
 * BaseResponseModel (success, errors, message). Uses the shared envelope
 * contract to ensure type safety across test boundaries.
 */
function mockApiResponse<T>(object: T): ApiResponse<T> {
  const envelope: MockEnvelopeFields = {
    success: true,
    errors: [],
    message: '',
  };
  return {
    ...envelope,
    statusCode: 200,
    timestamp: new Date().toISOString(),
    object,
  } as ApiResponse<T>;
}

/**
 * Creates an error ApiResponse envelope using BaseResponseModel's
 * success=false, errors array, and message fields. Used to test
 * assertApiSuccess error propagation through hooks.
 */
function mockErrorResponse(errorMessage: string): ApiResponse<never> {
  const envelope: MockEnvelopeFields = {
    success: false,
    errors: [{ key: 'error', value: '', message: errorMessage }],
    message: errorMessage,
  };
  return {
    ...envelope,
    statusCode: 400,
    timestamp: new Date().toISOString(),
  } as ApiResponse<never>;
}

// ──────────────────────────────────────────────────────────────────────────────
// Test utilities
// ──────────────────────────────────────────────────────────────────────────────

/** Creates a fresh QueryClient with retries disabled for deterministic tests. */
function createTestQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
}

/**
 * Creates a React wrapper component that provides QueryClientProvider context.
 * Uses React.createElement instead of JSX since this is a .ts (not .tsx) file.
 */
function createWrapper(queryClient?: QueryClient) {
  const client = queryClient ?? createTestQueryClient();
  return function TestQueryClientWrapper({ children }: { children: ReactNode }) {
    return createElement(QueryClientProvider, { client }, children);
  };
}

// ──────────────────────────────────────────────────────────────────────────────
// Global test lifecycle
// ──────────────────────────────────────────────────────────────────────────────

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ══════════════════════════════════════════════════════════════════════════════
// Test Suites — 15 hooks organized by domain
// ══════════════════════════════════════════════════════════════════════════════

// --------------------------------------------------------------------------
// 1. useTasks — Paginated task list with filters
//    Replaces TaskService filtered/sorted task queues by project/assignee/status
//    API: GET /v1/inventory/tasks
// --------------------------------------------------------------------------

describe('useTasks', () => {
  it('should fetch tasks', async () => {
    const taskList: EntityRecordList = {
      records: [mockTask, mockTaskTwo],
      totalCount: 2,
    };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(() => useTasks(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify correct Inventory service endpoint (replaces api/v3.0/p/project/task/*)
    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', undefined);
    expect(mockGet).toHaveBeenCalledTimes(1);

    // Verify response data — EntityRecordList with records array and totalCount
    expect(result.current.data).toEqual(taskList);
    expect(result.current.data?.records).toHaveLength(2);
    expect(result.current.data?.totalCount).toBe(2);
  });

  it('should filter by projectId', async () => {
    const taskList: EntityRecordList = { records: [mockTask], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ projectId: 'project-guid' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // buildTaskQueryParams extracts projectId into query params
    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      projectId: 'project-guid',
    });
  });

  it('should filter by assigneeId', async () => {
    const taskList: EntityRecordList = { records: [mockTask], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ assigneeId: 'user-guid' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      assigneeId: 'user-guid',
    });
  });

  it('should filter by status', async () => {
    // Status values match Project plugin entity definition:
    // Open, InProgress, Completed, Cancelled
    const taskList: EntityRecordList = { records: [mockTask], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ status: 'InProgress' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      status: 'InProgress',
    });
  });

  it('should filter by priority', async () => {
    // Priority values: High, Medium, Low
    const taskList: EntityRecordList = { records: [mockTask], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ priority: 'High' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      priority: 'High',
    });
  });

  it('should filter by date range', async () => {
    const taskList: EntityRecordList = { records: [mockTask], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ startDate: '2024-01-01', endDate: '2024-01-31' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      startDate: '2024-01-01',
      endDate: '2024-01-31',
    });
  });

  it('should support search text', async () => {
    // Search replaces the monolith's x_search computed field (search key
    // concatenation from SearchService.RegenerateSearchField)
    const taskList: EntityRecordList = { records: [mockTask], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ search: 'login bug' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      search: 'login bug',
    });
  });

  it('should handle pagination', async () => {
    const taskList: EntityRecordList = { records: [mockTaskTwo], totalCount: 25 };
    mockGet.mockResolvedValue(mockApiResponse(taskList));

    const { result } = renderHook(
      () => useTasks({ page: 2, pageSize: 10 }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks', {
      page: 2,
      pageSize: 10,
    });
    expect(result.current.data?.totalCount).toBe(25);
  });

  it('should handle empty task list', async () => {
    const emptyList: EntityRecordList = { records: [], totalCount: 0 };
    mockGet.mockResolvedValue(mockApiResponse(emptyList));

    const { result } = renderHook(() => useTasks(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.records).toHaveLength(0);
    expect(result.current.data?.totalCount).toBe(0);
  });

  it('should handle API error', async () => {
    mockGet.mockResolvedValue(mockErrorResponse('Failed to fetch tasks'));

    const { result } = renderHook(() => useTasks(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error).toBeDefined();
    expect(result.current.error?.message).toContain('Failed to fetch tasks');
  });
});

// --------------------------------------------------------------------------
// 2. useTask — Single task by ID
//    Replaces TaskService.GetTaskById via RecordManager.Find
//    API: GET /v1/inventory/tasks/{id}
// --------------------------------------------------------------------------

describe('useTask', () => {
  it('should fetch task by ID', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockTask));

    const { result } = renderHook(() => useTask('task-guid'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify endpoint includes task ID (URL-encoded)
    expect(mockGet).toHaveBeenCalledWith('/inventory/tasks/task-guid');
    expect(mockGet).toHaveBeenCalledTimes(1);

    // Verify full task record returned
    expect(result.current.data).toEqual(mockTask);
    expect(result.current.data?.id).toBe('task-guid');
    expect(result.current.data?.subject).toBe('Fix login bug');
  });

  it('should include all task properties from Project plugin entity definition', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockTask));

    const { result } = renderHook(() => useTask('task-guid'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify ALL fields matching ProjectPlugin task entity definition
    const task = result.current.data;
    expect(task).toBeDefined();
    expect(task?.id).toBe('task-guid');
    expect(task?.subject).toBe('Fix login bug');
    expect(task?.status).toBe('InProgress');
    expect(task?.priority).toBe('High');
    expect(task?.assigneeId).toBe('user-guid');
    expect(task?.projectId).toBe('project-guid');
    expect(task?.startDate).toBe('2024-01-15');
    expect(task?.endDate).toBe('2024-01-31');
    expect(task?.description).toBe('Login page returns 500');
  });

  it('should not fetch when id is empty', async () => {
    // The hook's enabled flag is `id.length > 0`, so empty string disables the query.
    // This replicates the pattern where no task ID has been selected yet.
    const { result } = renderHook(() => useTask(''), {
      wrapper: createWrapper(),
    });

    expect(mockGet).not.toHaveBeenCalled();
    expect(result.current.fetchStatus).toBe('idle');
  });

  it('should handle API error for invalid task ID', async () => {
    mockGet.mockResolvedValue(mockErrorResponse('Task not found'));

    const { result } = renderHook(() => useTask('nonexistent-id'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error?.message).toContain('Task not found');
  });
});

// --------------------------------------------------------------------------
// 3. useCreateTask — Task creation mutation
//    Replaces TaskService.Create via RecordManager.CreateRecord("task", ...)
//    API: POST /v1/inventory/tasks
// --------------------------------------------------------------------------

describe('useCreateTask', () => {
  it('should create task', async () => {
    const newTaskData: EntityRecord = {
      subject: 'New feature request',
      status: 'Open',
      priority: 'Medium',
      projectId: 'project-guid',
      description: 'Implement export to CSV',
    };
    const createdTask: EntityRecord = { ...newTaskData, id: 'new-task-guid' };
    mockPost.mockResolvedValue(mockApiResponse(createdTask));

    const { result } = renderHook(() => useCreateTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(newTaskData);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/inventory/tasks', newTaskData);
    expect(mockPost).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual(createdTask);
    expect(result.current.data?.id).toBe('new-task-guid');
  });

  it('should invalidate tasks, activity-feed, and project-dashboard queries', async () => {
    const newTask: EntityRecord = { subject: 'Test task', status: 'Open' };
    mockPost.mockResolvedValue(mockApiResponse({ ...newTask, id: 'guid' }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateTask(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(newTask);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Task creation invalidates: tasks.all, activityFeed.all, projectDashboard.all
    // This mirrors the monolith's post-create hook pattern via RecordHookManager
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['tasks'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['activity-feed'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['project-dashboard'] }),
    );
  });

  it('should handle validation errors for missing required fields', async () => {
    // Replaces the monolith's pre-create validation where RecordManager validated
    // required fields before persistence.
    mockPost.mockResolvedValue(mockErrorResponse('Subject is required'));

    const { result } = renderHook(() => useCreateTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({});
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error).toBeDefined();
    expect(result.current.error?.message).toContain('Subject is required');
  });
});

// --------------------------------------------------------------------------
// 4. useUpdateTask — Task update with optimistic updates
//    Replaces TaskService.Update via RecordManager.UpdateRecord("task", ...)
//    API: PUT /v1/inventory/tasks/{id}
// --------------------------------------------------------------------------

describe('useUpdateTask', () => {
  it('should update task', async () => {
    const updateData: EntityRecord = { description: 'Updated description' };
    const updatedTask: EntityRecord = { ...mockTask, ...updateData };
    mockPut.mockResolvedValue(mockApiResponse(updatedTask));

    const { result } = renderHook(() => useUpdateTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: 'task-guid', data: updateData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPut).toHaveBeenCalledWith('/inventory/tasks/task-guid', updateData);
    expect(mockPut).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual(updatedTask);
  });

  it('should invalidate tasks list, specific task, activity-feed, and project-dashboard', async () => {
    const updateData: EntityRecord = { subject: 'Updated subject' };
    mockPut.mockResolvedValue(mockApiResponse({ ...mockTask, ...updateData }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateTask(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate({ id: 'task-guid', data: updateData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // useUpdateTask uses onSettled (fires after success or error) for invalidation:
    // tasks.all, tasks.detail(id), activityFeed.all, projectDashboard.all
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['tasks'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['tasks', 'task-guid'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['activity-feed'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['project-dashboard'] }),
    );
  });

  it('should handle status transition from Open to InProgress', async () => {
    // Status transitions: Open → InProgress → Completed/Cancelled
    // Matches the Project plugin's TaskService status validation
    const statusUpdate: EntityRecord = { status: 'InProgress' };
    const transitionedTask: EntityRecord = {
      ...mockTaskTwo,
      status: 'InProgress',
    };
    mockPut.mockResolvedValue(mockApiResponse(transitionedTask));

    const { result } = renderHook(() => useUpdateTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: 'task-guid-2', data: statusUpdate });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPut).toHaveBeenCalledWith('/inventory/tasks/task-guid-2', statusUpdate);
    expect(result.current.data?.status).toBe('InProgress');
  });

  it('should handle API errors during update', async () => {
    mockPut.mockResolvedValue(mockErrorResponse('Concurrent modification detected'));

    const { result } = renderHook(() => useUpdateTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: 'task-guid', data: { subject: 'Updated' } });
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error?.message).toContain('Concurrent modification detected');
  });
});

// --------------------------------------------------------------------------
// 5. useDeleteTask — Task deletion with cross-entity invalidation
//    Replaces TaskService.Delete via RecordManager.DeleteRecord("task", ...)
//    API: DELETE /v1/inventory/tasks/{id}
// --------------------------------------------------------------------------

describe('useDeleteTask', () => {
  it('should delete task', async () => {
    mockDel.mockResolvedValue(mockApiResponse(undefined));

    const { result } = renderHook(() => useDeleteTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('task-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockDel).toHaveBeenCalledWith('/inventory/tasks/task-guid');
    expect(mockDel).toHaveBeenCalledTimes(1);
  });

  it('should invalidate tasks, comments, timelogs, project-dashboard, and activity-feed queries', async () => {
    // Deleting a task cascades to related entities — the monolith's
    // RecordManager cascade-deleted related timelogs and comments.
    // In the microservices architecture, cache invalidation ensures
    // stale related data is refetched.
    mockDel.mockResolvedValue(mockApiResponse(undefined));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteTask(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('task-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Task deletion invalidates the broadest set of queries:
    // tasks.all, comments.all, timelogs.all, projectDashboard.all, activityFeed.all
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['tasks'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['comments'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelogs'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['project-dashboard'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['activity-feed'] }),
    );
  });

  it('should handle delete API error', async () => {
    mockDel.mockResolvedValue(mockErrorResponse('Insufficient permissions'));

    const { result } = renderHook(() => useDeleteTask(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('task-guid');
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error?.message).toContain('Insufficient permissions');
  });
});

// --------------------------------------------------------------------------
// 6. useTimelogs — Paginated timelog list with filters
//    Replaces TimelogService filtered timelog queries
//    API: GET /v1/inventory/timelogs
// --------------------------------------------------------------------------

describe('useTimelogs', () => {
  it('should fetch timelogs', async () => {
    const timelogList: EntityRecordList = {
      records: [mockTimelog, mockTimelogTwo],
      totalCount: 2,
    };
    mockGet.mockResolvedValue(mockApiResponse(timelogList));

    const { result } = renderHook(() => useTimelogs(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs', undefined);
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.records).toHaveLength(2);
    expect(result.current.data?.totalCount).toBe(2);
  });

  it('should filter by taskId', async () => {
    const timelogList: EntityRecordList = { records: [mockTimelog], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(timelogList));

    const { result } = renderHook(
      () => useTimelogs({ taskId: 'task-guid' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs', {
      taskId: 'task-guid',
    });
  });

  it('should filter by userId', async () => {
    const timelogList: EntityRecordList = { records: [mockTimelog], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(timelogList));

    const { result } = renderHook(
      () => useTimelogs({ userId: 'user-guid' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs', {
      userId: 'user-guid',
    });
  });

  it('should filter by date range', async () => {
    const timelogList: EntityRecordList = { records: [mockTimelog], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(timelogList));

    const { result } = renderHook(
      () => useTimelogs({ startDate: '2024-01-01', endDate: '2024-01-31' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs', {
      startDate: '2024-01-01',
      endDate: '2024-01-31',
    });
  });

  it('should handle empty timelog list', async () => {
    const emptyList: EntityRecordList = { records: [], totalCount: 0 };
    mockGet.mockResolvedValue(mockApiResponse(emptyList));

    const { result } = renderHook(() => useTimelogs(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.records).toHaveLength(0);
    expect(result.current.data?.totalCount).toBe(0);
  });
});

// --------------------------------------------------------------------------
// 7. useCreateTimelog — Timelog creation with cross-query invalidation
//    Replaces TimelogService.Create (timelog affects task time totals)
//    API: POST /v1/inventory/timelogs
// --------------------------------------------------------------------------

describe('useCreateTimelog', () => {
  it('should create timelog', async () => {
    const newTimelogData: EntityRecord = {
      taskId: 'task-guid',
      hours: 3.0,
      date: '2024-01-22',
      description: 'Feature implementation',
    };
    const createdTimelog: EntityRecord = {
      ...newTimelogData,
      id: 'new-timelog-guid',
      userId: 'user-guid',
    };
    mockPost.mockResolvedValue(mockApiResponse(createdTimelog));

    const { result } = renderHook(() => useCreateTimelog(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(newTimelogData);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/inventory/timelogs', newTimelogData);
    expect(mockPost).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual(createdTimelog);
  });

  it('should invalidate timelogs, tasks, project-dashboard, timelog-summary, and activity-feed', async () => {
    // Creating a timelog affects:
    //   - timelogs list (new entry)
    //   - tasks list (task time totals changed — the monolith's TimelogService
    //     updated task.x_billable_hours and task.x_nonbillable_hours)
    //   - project dashboard (hours aggregate changed)
    //   - timelog summary (aggregation changed)
    //   - activity feed (new TimelogCreated event)
    const newTimelog: EntityRecord = { taskId: 'task-guid', hours: 1.0 };
    mockPost.mockResolvedValue(mockApiResponse({ ...newTimelog, id: 'guid' }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateTimelog(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(newTimelog);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelogs'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['tasks'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['project-dashboard'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelog-summary'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['activity-feed'] }),
    );
  });

  it('should handle validation error for missing hours', async () => {
    mockPost.mockResolvedValue(mockErrorResponse('Hours is required'));

    const { result } = renderHook(() => useCreateTimelog(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ taskId: 'task-guid' });
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error?.message).toContain('Hours is required');
  });
});

// --------------------------------------------------------------------------
// 8. useUpdateTimelog — Timelog update with summary invalidation
//    Replaces TimelogService.Update
//    API: PUT /v1/inventory/timelogs/{id}
// --------------------------------------------------------------------------

describe('useUpdateTimelog', () => {
  it('should update timelog', async () => {
    const updateData: EntityRecord = { hours: 4.0, description: 'Extended session' };
    const updatedTimelog: EntityRecord = { ...mockTimelog, ...updateData };
    mockPut.mockResolvedValue(mockApiResponse(updatedTimelog));

    const { result } = renderHook(() => useUpdateTimelog(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: 'timelog-guid', data: updateData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPut).toHaveBeenCalledWith('/inventory/timelogs/timelog-guid', updateData);
    expect(mockPut).toHaveBeenCalledTimes(1);
    expect(result.current.data?.hours).toBe(4.0);
  });

  it('should invalidate timelogs, timelog-summary, and project-dashboard queries', async () => {
    const updateData: EntityRecord = { hours: 5.0 };
    mockPut.mockResolvedValue(mockApiResponse({ ...mockTimelog, ...updateData }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateTimelog(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate({ id: 'timelog-guid', data: updateData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Timelog update invalidates: timelogs.all, timelogSummary.all, projectDashboard.all
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelogs'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelog-summary'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['project-dashboard'] }),
    );
  });
});

// --------------------------------------------------------------------------
// 9. useDeleteTimelog — Timelog deletion with cross-query invalidation
//    Replaces TimelogService.Delete
//    API: DELETE /v1/inventory/timelogs/{id}
// --------------------------------------------------------------------------

describe('useDeleteTimelog', () => {
  it('should delete timelog', async () => {
    mockDel.mockResolvedValue(mockApiResponse(undefined));

    const { result } = renderHook(() => useDeleteTimelog(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('timelog-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockDel).toHaveBeenCalledWith('/inventory/timelogs/timelog-guid');
    expect(mockDel).toHaveBeenCalledTimes(1);
  });

  it('should invalidate timelogs, tasks, project-dashboard, and timelog-summary queries', async () => {
    // Deleting a timelog affects task time totals (same cross-invalidation
    // as create), plus dashboard and summary aggregates
    mockDel.mockResolvedValue(mockApiResponse(undefined));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteTimelog(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('timelog-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelogs'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['tasks'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['project-dashboard'] }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['timelog-summary'] }),
    );
  });
});

// --------------------------------------------------------------------------
// 10. useComments — Task-scoped comment list
//     Replaces CommentService comment listing (threaded)
//     API: GET /v1/inventory/tasks/{taskId}/comments
// --------------------------------------------------------------------------

describe('useComments', () => {
  it('should fetch comments for task', async () => {
    const commentList: EntityRecordList = {
      records: [mockComment, mockCommentReply],
      totalCount: 2,
    };
    mockGet.mockResolvedValue(mockApiResponse(commentList));

    const { result } = renderHook(() => useComments('task-guid'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify task-scoped comments endpoint
    expect(mockGet).toHaveBeenCalledWith(
      '/inventory/tasks/task-guid/comments',
      undefined,
    );
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.records).toHaveLength(2);
    expect(result.current.data?.totalCount).toBe(2);
  });

  it('should return threaded comments with parentId', async () => {
    // The monolith's CommentService supported threaded comments via parent_id.
    // In the target, parentId enables nested comment display in the React SPA.
    const commentList: EntityRecordList = {
      records: [mockComment, mockCommentReply],
      totalCount: 2,
    };
    mockGet.mockResolvedValue(mockApiResponse(commentList));

    const { result } = renderHook(() => useComments('task-guid'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const comments = result.current.data?.records;
    expect(comments).toBeDefined();

    // First comment is a top-level comment (parentId: null)
    const topLevel = comments?.find((c) => c.id === 'comment-guid');
    expect(topLevel?.parentId).toBeNull();
    expect(topLevel?.body).toBe('Found the root cause');

    // Second comment is a reply (parentId references the first)
    const reply = comments?.find((c) => c.id === 'comment-reply-guid');
    expect(reply?.parentId).toBe('comment-guid');
    expect(reply?.body).toBe('Good find!');
  });

  it('should not fetch when taskId is empty', async () => {
    // The hook's enabled flag is `taskId.length > 0`
    const { result } = renderHook(() => useComments(''), {
      wrapper: createWrapper(),
    });

    expect(mockGet).not.toHaveBeenCalled();
    expect(result.current.fetchStatus).toBe('idle');
  });

  it('should support pagination parameters', async () => {
    const commentList: EntityRecordList = { records: [mockComment], totalCount: 10 };
    mockGet.mockResolvedValue(mockApiResponse(commentList));

    const { result } = renderHook(
      () => useComments('task-guid', { page: 1, pageSize: 5 }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Pagination params are passed as second argument to get()
    expect(mockGet).toHaveBeenCalledWith(
      '/inventory/tasks/task-guid/comments',
      { page: 1, pageSize: 5 },
    );
  });
});

// --------------------------------------------------------------------------
// 11. useCreateComment — Comment creation with task-scoped invalidation
//     Replaces CommentService.Create
//     API: POST /v1/inventory/tasks/{taskId}/comments
// --------------------------------------------------------------------------

describe('useCreateComment', () => {
  it('should create comment on task', async () => {
    const commentData: EntityRecord = {
      body: 'This is a new comment',
      parentId: null,
    };
    const createdComment: EntityRecord = {
      ...commentData,
      id: 'new-comment-guid',
      taskId: 'task-guid',
      userId: 'user-guid',
      createdOn: '2024-01-21T12:00:00Z',
    };
    mockPost.mockResolvedValue(mockApiResponse(createdComment));

    const { result } = renderHook(() => useCreateComment(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ taskId: 'task-guid', data: commentData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Verify task-scoped comment creation endpoint
    expect(mockPost).toHaveBeenCalledWith(
      '/inventory/tasks/task-guid/comments',
      commentData,
    );
    expect(mockPost).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual(createdComment);
  });

  it('should invalidate task comments and activity-feed', async () => {
    // Creating a comment invalidates the specific task's comments
    // (comments.byTask(taskId)) and the activity feed (new CommentCreated event)
    const commentData: EntityRecord = { body: 'Test comment' };
    mockPost.mockResolvedValue(
      mockApiResponse({ ...commentData, id: 'guid', taskId: 'task-guid' }),
    );

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateComment(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate({ taskId: 'task-guid', data: commentData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Scoped invalidation: comments.byTask('task-guid') = ['comments', 'task-guid']
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['comments', 'task-guid'] }),
    );
    // Broad invalidation: activityFeed.all = ['activity-feed']
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['activity-feed'] }),
    );
  });

  it('should create threaded reply comment', async () => {
    // Test creating a reply (parentId set to existing comment)
    const replyData: EntityRecord = {
      body: 'This is a reply',
      parentId: 'comment-guid',
    };
    const createdReply: EntityRecord = {
      ...replyData,
      id: 'reply-guid',
      taskId: 'task-guid',
      userId: 'user-guid',
      createdOn: '2024-01-21T14:00:00Z',
    };
    mockPost.mockResolvedValue(mockApiResponse(createdReply));

    const { result } = renderHook(() => useCreateComment(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ taskId: 'task-guid', data: replyData });
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.parentId).toBe('comment-guid');
  });
});

// --------------------------------------------------------------------------
// 12. useDeleteComment — Comment deletion with broad invalidation
//     Replaces CommentService.Delete (cascade delete for child comments)
//     API: DELETE /v1/inventory/comments/{id}
// --------------------------------------------------------------------------

describe('useDeleteComment', () => {
  it('should delete comment', async () => {
    mockDel.mockResolvedValue(mockApiResponse(undefined));

    const { result } = renderHook(() => useDeleteComment(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('comment-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Note: delete endpoint uses /inventory/comments/{id} (not task-scoped)
    expect(mockDel).toHaveBeenCalledWith('/inventory/comments/comment-guid');
    expect(mockDel).toHaveBeenCalledTimes(1);
  });

  it('should invalidate all comments and activity-feed queries', async () => {
    // The delete hook uses broad comments.all invalidation (not task-scoped)
    // because the comment ID alone doesn't tell which task it belonged to.
    // This mirrors the monolith's CommentService.Delete which also cascade-deleted
    // child comments (threaded replies).
    mockDel.mockResolvedValue(mockApiResponse(undefined));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteComment(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('comment-guid');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Broad invalidation: comments.all = ['comments']
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['comments'] }),
    );
    // Activity feed: activityFeed.all = ['activity-feed']
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['activity-feed'] }),
    );
  });
});

// --------------------------------------------------------------------------
// 13. useActivityFeed — Near-real-time activity feed with refetchInterval
//     Replaces FeedService activity feed generation
//     API: GET /v1/inventory/feed
// --------------------------------------------------------------------------

describe('useActivityFeed', () => {
  it('should fetch activity feed', async () => {
    const feedList: EntityRecordList = {
      records: [mockFeedItem, mockFeedItemTwo],
      totalCount: 2,
    };
    mockGet.mockResolvedValue(mockApiResponse(feedList));

    const { result } = renderHook(() => useActivityFeed(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/feed', undefined);
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.records).toHaveLength(2);
    expect(result.current.data?.totalCount).toBe(2);
  });

  it('should support pagination and filtering parameters', async () => {
    const feedList: EntityRecordList = { records: [mockFeedItem], totalCount: 50 };
    mockGet.mockResolvedValue(mockApiResponse(feedList));

    const { result } = renderHook(
      () =>
        useActivityFeed({
          projectId: 'project-guid',
          page: 1,
          pageSize: 20,
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // buildActivityFeedQueryParams extracts projectId, page, pageSize
    expect(mockGet).toHaveBeenCalledWith('/inventory/feed', {
      projectId: 'project-guid',
      page: 1,
      pageSize: 20,
    });
  });

  it('should filter by event type', async () => {
    const feedList: EntityRecordList = { records: [mockFeedItem], totalCount: 1 };
    mockGet.mockResolvedValue(mockApiResponse(feedList));

    const { result } = renderHook(
      () => useActivityFeed({ type: 'TaskCreated' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/feed', {
      type: 'TaskCreated',
    });
  });

  it('should include feed item type and data', async () => {
    const feedList: EntityRecordList = {
      records: [mockFeedItem, mockFeedItemTwo],
      totalCount: 2,
    };
    mockGet.mockResolvedValue(mockApiResponse(feedList));

    const { result } = renderHook(() => useActivityFeed(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const items = result.current.data?.records;
    expect(items).toBeDefined();

    // Verify feed item structure matches FeedService event patterns
    const taskCreated = items?.find((i) => i.id === 'feed-guid');
    expect(taskCreated?.type).toBe('TaskCreated');
    expect(taskCreated?.createdOn).toBe('2024-01-15T08:00:00Z');

    const timelogCreated = items?.find((i) => i.id === 'feed-guid-2');
    expect(timelogCreated?.type).toBe('TimelogCreated');
  });

  it('should handle empty activity feed', async () => {
    const emptyFeed: EntityRecordList = { records: [], totalCount: 0 };
    mockGet.mockResolvedValue(mockApiResponse(emptyFeed));

    const { result } = renderHook(() => useActivityFeed(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.records).toHaveLength(0);
  });
});

// --------------------------------------------------------------------------
// 14. useProjectDashboard — Project dashboard aggregate statistics
//     Replaces ReportingService dashboard computation
//     API: GET /v1/inventory/dashboard
// --------------------------------------------------------------------------

describe('useProjectDashboard', () => {
  it('should fetch dashboard data', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockDashboardData));

    const { result } = renderHook(() => useProjectDashboard(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Global dashboard (no projectId) — fetches across all projects
    expect(mockGet).toHaveBeenCalledWith('/inventory/dashboard', undefined);
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual(mockDashboardData);
  });

  it('should include task counts by status', async () => {
    // Mirrors ReportingService dashboard aggregation: task counts by status,
    // total hours, and overdue task count
    mockGet.mockResolvedValue(mockApiResponse(mockDashboardData));

    const { result } = renderHook(() => useProjectDashboard(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const dashboard = result.current.data;
    expect(dashboard).toBeDefined();
    expect(dashboard?.totalTasks).toBe(25);
    expect(dashboard?.openTasks).toBe(10);
    expect(dashboard?.inProgressTasks).toBe(8);
    expect(dashboard?.completedTasks).toBe(5);
    expect(dashboard?.cancelledTasks).toBe(2);
    expect(dashboard?.totalHoursLogged).toBe(120.5);
    expect(dashboard?.overdueTasks).toBe(3);
  });

  it('should filter by projectId', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockDashboardData));

    const { result } = renderHook(
      () => useProjectDashboard('project-guid'),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Project-scoped dashboard passes projectId as query param
    expect(mockGet).toHaveBeenCalledWith('/inventory/dashboard', {
      projectId: 'project-guid',
    });
  });

  it('should fetch global dashboard when projectId is empty string', async () => {
    // Empty string is treated the same as undefined — global dashboard
    mockGet.mockResolvedValue(mockApiResponse(mockDashboardData));

    const { result } = renderHook(() => useProjectDashboard(''), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/dashboard', undefined);
  });

  it('should handle API error', async () => {
    mockGet.mockResolvedValue(mockErrorResponse('Dashboard unavailable'));

    const { result } = renderHook(() => useProjectDashboard(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error?.message).toContain('Dashboard unavailable');
  });
});

// --------------------------------------------------------------------------
// 15. useTimelogSummary — Aggregated timelog reports
//     Replaces ReportingService timelog aggregation (grouped by user/task/date)
//     API: GET /v1/inventory/timelogs/summary
// --------------------------------------------------------------------------

describe('useTimelogSummary', () => {
  it('should fetch timelog summary', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockTimelogSummaryData));

    const { result } = renderHook(() => useTimelogSummary(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs/summary', undefined);
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data).toEqual(mockTimelogSummaryData);
  });

  it('should support groupBy user parameter', async () => {
    // The monolith's ReportingService supported grouping by user, task, or date
    mockGet.mockResolvedValue(mockApiResponse(mockTimelogSummaryData));

    const { result } = renderHook(
      () => useTimelogSummary({ groupBy: 'user' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs/summary', {
      groupBy: 'user',
    });
  });

  it('should support groupBy task parameter', async () => {
    const taskSummary: EntityRecord = {
      id: 'summary-by-task',
      groupBy: 'task',
      totalHours: 40,
      entries: [
        { taskId: 'task-guid', taskSubject: 'Fix login bug', totalHours: 25 },
        { taskId: 'task-guid-2', taskSubject: 'Add dark mode', totalHours: 15 },
      ],
    };
    mockGet.mockResolvedValue(mockApiResponse(taskSummary));

    const { result } = renderHook(
      () => useTimelogSummary({ groupBy: 'task' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs/summary', {
      groupBy: 'task',
    });
  });

  it('should support groupBy date parameter', async () => {
    const dateSummary: EntityRecord = {
      id: 'summary-by-date',
      groupBy: 'date',
      totalHours: 12,
      entries: [
        { date: '2024-01-20', totalHours: 8 },
        { date: '2024-01-21', totalHours: 4 },
      ],
    };
    mockGet.mockResolvedValue(mockApiResponse(dateSummary));

    const { result } = renderHook(
      () => useTimelogSummary({ groupBy: 'date' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs/summary', {
      groupBy: 'date',
    });
  });

  it('should filter by date range and projectId', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockTimelogSummaryData));

    const { result } = renderHook(
      () =>
        useTimelogSummary({
          groupBy: 'user',
          projectId: 'project-guid',
          startDate: '2024-01-01',
          endDate: '2024-01-31',
        }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/inventory/timelogs/summary', {
      groupBy: 'user',
      projectId: 'project-guid',
      startDate: '2024-01-01',
      endDate: '2024-01-31',
    });
  });

  it('should include summary aggregate data', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockTimelogSummaryData));

    const { result } = renderHook(
      () => useTimelogSummary({ groupBy: 'user' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const summary = result.current.data;
    expect(summary).toBeDefined();
    expect(summary?.groupBy).toBe('user');
    expect(summary?.totalHours).toBe(40);
    expect(summary?.entries).toHaveLength(2);
  });

  it('should handle API error', async () => {
    mockGet.mockResolvedValue(mockErrorResponse('Summary computation failed'));

    const { result } = renderHook(() => useTimelogSummary(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(result.current.error?.message).toContain('Summary computation failed');
  });
});
