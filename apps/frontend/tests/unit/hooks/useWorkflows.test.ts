/**
 * @file useWorkflows.test.ts
 * @description Comprehensive Vitest unit tests for the 12 workflow/job TanStack Query hooks
 * exported from src/hooks/useWorkflows.ts. These hooks replace the monolith's:
 *   - JobManager.cs — singleton coordinator: job type registry, creation, querying, dispatch
 *   - JobPool.cs — bounded 20-thread executor
 *   - JobDataService.cs — PostgreSQL persistence for jobs and schedules
 *   - SheduleManager.cs — schedule plan CRUD + trigger
 *
 * Test suites cover:
 *   Query hooks (5): useWorkflows, useWorkflow, useWorkflowTypes, useSchedulePlans, useSchedulePlan
 *   Mutation hooks (7): useCreateWorkflow, useUpdateWorkflow, useCancelWorkflow,
 *     useCreateSchedulePlan, useUpdateSchedulePlan, useDeleteSchedulePlan, useTriggerSchedulePlan
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
// Module-under-test import (uses mocked dependencies)
// ──────────────────────────────────────────────────────────────────────────────

import {
  useWorkflows,
  useWorkflow,
  useWorkflowTypes,
  useSchedulePlans,
  useSchedulePlan,
  useCreateWorkflow,
  useUpdateWorkflow,
  useCancelWorkflow,
  useCreateSchedulePlan,
  useUpdateSchedulePlan,
  useDeleteSchedulePlan,
  useTriggerSchedulePlan,
  WorkflowStatus,
  SchedulePlanType,
} from '../../../src/hooks/useWorkflows';

// ──────────────────────────────────────────────────────────────────────────────
// Mocked module imports (for typed access to mocks)
// ──────────────────────────────────────────────────────────────────────────────

import { get, post, put, del } from '../../../src/api/client';
import type { ApiResponse } from '../../../src/api/client';

// Type-only import to validate mock response shape aligns with the shared
// server envelope contract (success, errors, message).
import type { BaseResponseModel } from '../../../src/types/common';

// Import types from the hook module for type-safe fixtures
import type {
  Workflow,
  WorkflowType,
  SchedulePlan,
  WorkflowListResponse,
  SchedulePlanListResponse,
  WorkflowListParams,
  CreateWorkflowPayload,
  UpdateWorkflowPayload,
  CreateSchedulePlanPayload,
  UpdateSchedulePlanPayload,
} from '../../../src/hooks/useWorkflows';

// ──────────────────────────────────────────────────────────────────────────────
// Typed mock references
// ──────────────────────────────────────────────────────────────────────────────

const mockGet = vi.mocked(get);
const mockPost = vi.mocked(post);
const mockPut = vi.mocked(put);
const mockDel = vi.mocked(del);

// ──────────────────────────────────────────────────────────────────────────────
// Test fixtures — modelled after JobManager.cs (jobs), JobDataService.cs
// (persistence), and SheduleManager.cs (schedule plans)
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Validates that a mock response envelope conforms to the shared
 * BaseResponseModel contract (success, errors, message).
 */
type MockEnvelopeFields = Pick<BaseResponseModel, 'success' | 'errors' | 'message'>;

/**
 * Mock workflow fixture matching the Workflow interface.
 * Maps from JobDataService.cs columns: id, type_id, type_name, status, priority,
 * started_on, finished_on, aborted_by, error_message, schedule_plan_id, etc.
 */
const mockWorkflow: Workflow = {
  id: 'job-guid-001',
  typeId: 'job-type-guid-001',
  typeName: 'SendEmailJob',
  completeClassName: 'WebVella.Erp.Plugins.Mail.Jobs.SendEmailJob',
  attributes: { recipient: 'test@example.com', subject: 'Test' },
  status: WorkflowStatus.Pending,
  priority: 1,
  startedOn: null,
  finishedOn: null,
  abortedBy: null,
  canceledBy: null,
  errorMessage: null,
  result: null,
  schedulePlanId: null,
  createdOn: '2024-01-20T10:00:00Z',
  lastModifiedOn: '2024-01-20T10:00:00Z',
  createdBy: 'user-guid-001',
  lastModifiedBy: 'user-guid-001',
};

/** Second workflow fixture for list testing — Running status. */
const mockWorkflowRunning: Workflow = {
  ...mockWorkflow,
  id: 'job-guid-002',
  status: WorkflowStatus.Running,
  startedOn: '2024-01-20T10:01:00Z',
};

/**
 * Mock workflow type fixture matching WorkflowType interface.
 * Maps from JobManager.JobTypes registry populated by RegisterJobTypes()
 * via reflection at startup (monolith pattern).
 */
const mockWorkflowType: WorkflowType = {
  id: 'job-type-guid-001',
  name: 'SendEmailJob',
  label: 'Send Email Job',
  description: 'Processes and sends queued emails via SMTP',
  assemblyName: 'WebVella.Erp.Plugins.Mail',
  className: 'WebVella.Erp.Plugins.Mail.Jobs.SendEmailJob',
  defaultPriority: 1,
  allowMultipleInstances: false,
};

/** Second workflow type fixture for list testing. */
const mockWorkflowTypeTask: WorkflowType = {
  id: 'job-type-guid-002',
  name: 'StartTasksOnStartDate',
  label: 'Start Tasks On Start Date',
  description: 'Activates project tasks when their start date arrives',
  assemblyName: 'WebVella.Erp.Plugins.Project',
  className: 'WebVella.Erp.Plugins.Project.Jobs.StartTasksOnStartDate',
  defaultPriority: 2,
  allowMultipleInstances: true,
};

/**
 * Mock schedule plan fixture matching SchedulePlan interface.
 * Maps from SheduleManager.cs schedule plan entity: name, type,
 * intervalInMinutes, jobTypeId, enabled, nextTriggerTime, etc.
 */
const mockSchedulePlan: SchedulePlan = {
  id: 'schedule-guid-001',
  name: 'hourly-email-check',
  type: SchedulePlanType.Interval,
  startTimespan: null,
  endTimespan: null,
  intervalInMinutes: 60,
  scheduledDays: null,
  jobTypeId: 'job-type-guid-001',
  jobTypeName: 'SendEmailJob',
  jobAttributes: { queue: 'default' },
  enabled: true,
  startDate: null,
  endDate: null,
  lastTriggerTime: '2024-01-20T09:00:00Z',
  nextTriggerTime: '2024-01-20T10:00:00Z',
  lastStartedJobId: 'job-guid-prev',
  createdOn: '2024-01-01T00:00:00Z',
  lastModifiedOn: '2024-01-20T09:00:00Z',
  createdBy: 'user-guid-001',
  lastModifiedBy: 'user-guid-001',
};

/** Mock workflow list response for pagination testing. */
const mockWorkflowListResponse: WorkflowListResponse = {
  items: [mockWorkflow, mockWorkflowRunning],
  totalCount: 42,
  page: 1,
  pageSize: 10,
};

/** Mock schedule plan list response. */
const mockSchedulePlanListResponse: SchedulePlanListResponse = {
  items: [mockSchedulePlan],
  totalCount: 1,
};

/**
 * Base envelope fields for mutation success responses.
 * Conforms to the BaseResponseModel contract (success, errors, message).
 */
const mockBaseEnvelope: MockEnvelopeFields = {
  success: true,
  errors: [],
  message: '',
};

// ──────────────────────────────────────────────────────────────────────────────
// Helper utilities
// ──────────────────────────────────────────────────────────────────────────────

/**
 * Creates a mock ApiResponse envelope with fields consistent with
 * BaseResponseModel (success, errors, message).
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
 * success=false, errors array, and message fields.
 */
function mockErrorResponse(errorMessage: string, statusCode = 400): ApiResponse<never> {
  const envelope: MockEnvelopeFields = {
    success: false,
    errors: [{ key: 'error', value: '', message: errorMessage }],
    message: errorMessage,
  };
  return {
    ...envelope,
    statusCode,
    timestamp: new Date().toISOString(),
  } as ApiResponse<never>;
}

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
// QUERY HOOK TEST SUITES
// ══════════════════════════════════════════════════════════════════════════════

// --------------------------------------------------------------------------
// 1. useWorkflows — Paginated job listing with status/type/date/priority filters
//    Replaces: JobManager.GetJobs(...) + JobDataService.GetJobs(...)
// --------------------------------------------------------------------------

describe('useWorkflows', () => {
  it('should fetch workflows', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockWorkflowListResponse));

    const { result } = renderHook(() => useWorkflows(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/workflows', undefined);
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object?.items).toHaveLength(2);
    expect(result.current.data?.success).toBe(true);
  });

  it('should filter by status', async () => {
    const filtered: WorkflowListResponse = {
      items: [mockWorkflow],
      totalCount: 1,
      page: 1,
      pageSize: 10,
    };
    mockGet.mockResolvedValue(mockApiResponse(filtered));

    const params: WorkflowListParams = { status: WorkflowStatus.Pending };

    const { result } = renderHook(() => useWorkflows(params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      '/workflows',
      expect.objectContaining({ status: WorkflowStatus.Pending }),
    );
  });

  it('should filter by typeId', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockWorkflowListResponse));

    const params: WorkflowListParams = { typeId: 'job-type-guid-001' };

    const { result } = renderHook(() => useWorkflows(params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      '/workflows',
      expect.objectContaining({ typeId: 'job-type-guid-001' }),
    );
  });

  it('should filter by date range', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockWorkflowListResponse));

    const params: WorkflowListParams = {
      dateFrom: '2024-01-01T00:00:00Z',
      dateTo: '2024-01-31T23:59:59Z',
    };

    const { result } = renderHook(() => useWorkflows(params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      '/workflows',
      expect.objectContaining({
        dateFrom: '2024-01-01T00:00:00Z',
        dateTo: '2024-01-31T23:59:59Z',
      }),
    );
  });

  it('should filter by priority', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockWorkflowListResponse));

    const params: WorkflowListParams = { priority: 1 };

    const { result } = renderHook(() => useWorkflows(params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      '/workflows',
      expect.objectContaining({ priority: 1 }),
    );
  });

  it('should handle pagination', async () => {
    const pagedResponse: WorkflowListResponse = {
      items: [mockWorkflow],
      totalCount: 42,
      page: 3,
      pageSize: 5,
    };
    mockGet.mockResolvedValue(mockApiResponse(pagedResponse));

    const params: WorkflowListParams = { page: 3, pageSize: 5 };

    const { result } = renderHook(() => useWorkflows(params), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith(
      '/workflows',
      expect.objectContaining({ page: 3, pageSize: 5 }),
    );
    expect(result.current.data?.object?.page).toBe(3);
    expect(result.current.data?.object?.pageSize).toBe(5);
  });

  it('should return totalCount', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockWorkflowListResponse));

    const { result } = renderHook(() => useWorkflows(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(result.current.data?.object?.totalCount).toBe(42);
  });
});

// --------------------------------------------------------------------------
// 2. useWorkflow — Single job fetch by ID
//    Replaces: JobManager.GetJob(id) + JobDataService.GetJob(id)
// --------------------------------------------------------------------------

describe('useWorkflow', () => {
  it('should fetch workflow by ID', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockWorkflow));

    const { result } = renderHook(() => useWorkflow('job-guid-001'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/workflows/job-guid-001');
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toEqual(mockWorkflow);
    expect(result.current.data?.success).toBe(true);
  });

  it('should not fetch when id is falsy', () => {
    const { result } = renderHook(() => useWorkflow(null), {
      wrapper: createWrapper(),
    });

    // When enabled: Boolean(id) is false, the query stays idle
    expect(mockGet).not.toHaveBeenCalled();
    expect(result.current.fetchStatus).toBe('idle');
  });

  it('should include status timeline (startedOn, finishedOn)', async () => {
    const completedWorkflow: Workflow = {
      ...mockWorkflow,
      id: 'job-guid-003',
      status: WorkflowStatus.Completed,
      startedOn: '2024-01-20T10:01:00Z',
      finishedOn: '2024-01-20T10:05:30Z',
    };
    mockGet.mockResolvedValue(mockApiResponse(completedWorkflow));

    const { result } = renderHook(() => useWorkflow('job-guid-003'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const data = result.current.data?.object;
    expect(data?.startedOn).toBe('2024-01-20T10:01:00Z');
    expect(data?.finishedOn).toBe('2024-01-20T10:05:30Z');
    expect(data?.status).toBe(WorkflowStatus.Completed);
  });
});

// --------------------------------------------------------------------------
// 3. useWorkflowTypes — Job type registry listing
//    Replaces: JobManager.JobTypes (static ConcurrentDictionary populated
//    by RegisterJobTypes() via reflection at startup)
//    Uses 10-minute staleTime since types change rarely.
// --------------------------------------------------------------------------

describe('useWorkflowTypes', () => {
  it('should fetch workflow types', async () => {
    const types = [mockWorkflowType, mockWorkflowTypeTask];
    mockGet.mockResolvedValue(mockApiResponse(types));

    const { result } = renderHook(() => useWorkflowTypes(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/workflows/types');
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toHaveLength(2);
    expect(result.current.data?.success).toBe(true);
  });

  it('should use staleTime of 10 minutes', async () => {
    // Workflow types are registered at startup via reflection and rarely change.
    // The hook uses TEN_MINUTES_MS (600000) as staleTime to avoid unnecessary refetches.
    const types = [mockWorkflowType];
    mockGet.mockResolvedValue(mockApiResponse(types));

    const queryClient = createTestQueryClient();

    const { result, unmount } = renderHook(() => useWorkflowTypes(), {
      wrapper: createWrapper(queryClient),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledTimes(1);

    // Unmount and remount — data should still be fresh (within 10-min staleTime)
    unmount();

    const { result: result2 } = renderHook(() => useWorkflowTypes(), {
      wrapper: createWrapper(queryClient),
    });

    await waitFor(() => {
      expect(result2.current.isSuccess).toBe(true);
    });

    // Should NOT have made a second API call because staleTime is 10 minutes
    expect(mockGet).toHaveBeenCalledTimes(1);
  });

  it('should include type metadata (defaultPriority, allowMultipleInstances)', async () => {
    mockGet.mockResolvedValue(mockApiResponse([mockWorkflowType]));

    const { result } = renderHook(() => useWorkflowTypes(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const type = result.current.data?.object?.[0];
    expect(type).toBeDefined();
    expect(type?.id).toBe('job-type-guid-001');
    expect(type?.name).toBe('SendEmailJob');
    expect(type?.defaultPriority).toBe(1);
    expect(type?.allowMultipleInstances).toBe(false);
    expect(type?.className).toBe('WebVella.Erp.Plugins.Mail.Jobs.SendEmailJob');
  });
});

// --------------------------------------------------------------------------
// 4. useSchedulePlans — Schedule plan listing
//    Replaces: SheduleManager.GetSchedulePlans()
// --------------------------------------------------------------------------

describe('useSchedulePlans', () => {
  it('should fetch schedule plans', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockSchedulePlanListResponse));

    const { result } = renderHook(() => useSchedulePlans(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/workflows/schedules');
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object?.items).toHaveLength(1);
    expect(result.current.data?.success).toBe(true);
  });

  it('should include next trigger time', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockSchedulePlanListResponse));

    const { result } = renderHook(() => useSchedulePlans(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    const plan = result.current.data?.object?.items[0];
    expect(plan?.nextTriggerTime).toBe('2024-01-20T10:00:00Z');
    expect(plan?.lastTriggerTime).toBe('2024-01-20T09:00:00Z');
  });
});

// --------------------------------------------------------------------------
// 5. useSchedulePlan — Single schedule plan fetch
//    Replaces: SheduleManager.GetSchedulePlan(id)
// --------------------------------------------------------------------------

describe('useSchedulePlan', () => {
  it('should fetch schedule plan by ID', async () => {
    mockGet.mockResolvedValue(mockApiResponse(mockSchedulePlan));

    const { result } = renderHook(() => useSchedulePlan('schedule-guid-001'), {
      wrapper: createWrapper(),
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockGet).toHaveBeenCalledWith('/workflows/schedules/schedule-guid-001');
    expect(mockGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object).toEqual(mockSchedulePlan);
    expect(result.current.data?.success).toBe(true);
  });

  it('should not fetch when id is falsy', () => {
    const { result } = renderHook(() => useSchedulePlan(undefined), {
      wrapper: createWrapper(),
    });

    // When enabled: Boolean(id) is false, the query stays idle
    expect(mockGet).not.toHaveBeenCalled();
    expect(result.current.fetchStatus).toBe('idle');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// MUTATION HOOK TEST SUITES — WORKFLOW MUTATIONS
// ══════════════════════════════════════════════════════════════════════════════

// --------------------------------------------------------------------------
// 6. useCreateWorkflow — Job creation
//    Replaces: JobManager.CreateJob(typeId, creatorId, priority, attributes)
//    Sends POST /workflows and invalidates ['workflows']
// --------------------------------------------------------------------------

describe('useCreateWorkflow', () => {
  it('should create workflow', async () => {
    const createPayload: CreateWorkflowPayload = {
      typeId: 'job-type-guid-001',
      priority: 2,
      attributes: { recipient: 'user@example.com' },
    };
    mockPost.mockResolvedValue(mockApiResponse({
      ...mockBaseEnvelope,
      id: 'new-job-guid',
    }));

    const { result } = renderHook(() => useCreateWorkflow(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(createPayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/workflows', createPayload);
    expect(mockPost).toHaveBeenCalledTimes(1);
  });

  it('should invalidate workflows query', async () => {
    const createPayload: CreateWorkflowPayload = {
      typeId: 'job-type-guid-001',
    };
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateWorkflow(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(createPayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['workflows'],
    });
  });

  it('should handle invalid typeId', async () => {
    const badPayload: CreateWorkflowPayload = {
      typeId: 'non-existent-type-guid',
    };
    // The API client rejects on failure (response interceptor throws for
    // success:false envelopes), so TanStack Query sees a rejected Promise.
    mockPost.mockRejectedValue(mockErrorResponse('Workflow type not found'));

    const { result } = renderHook(() => useCreateWorkflow(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(badPayload);
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/workflows', badPayload);
  });

  it('should normalize invalid priority to default (server-side)', async () => {
    // Per JobManager behavior, invalid priorities are normalized server-side
    // to the job type's DefaultPriority. The frontend sends whatever is provided.
    const payloadWithHighPriority: CreateWorkflowPayload = {
      typeId: 'job-type-guid-001',
      priority: 999,
    };
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useCreateWorkflow(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(payloadWithHighPriority);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // The frontend passes priority as-is; server normalizes
    expect(mockPost).toHaveBeenCalledWith('/workflows', payloadWithHighPriority);
  });
});

// --------------------------------------------------------------------------
// 7. useUpdateWorkflow — Job update
//    Replaces: JobManager.UpdateJob(job) via PUT /workflows/{id}
//    Destructures {id, ...body} from payload for URL + body separation.
//    Invalidates ['workflows'] (list) + ['workflows', 'detail', id] (detail).
// --------------------------------------------------------------------------

describe('useUpdateWorkflow', () => {
  it('should update workflow', async () => {
    const updatePayload: UpdateWorkflowPayload = {
      id: 'job-guid-001',
      status: WorkflowStatus.Aborted,
      abortedBy: 'admin-user-guid',
    };
    mockPut.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useUpdateWorkflow(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(updatePayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Hook destructures { id, ...body }, calls put('/workflows/{id}', body)
    expect(mockPut).toHaveBeenCalledWith(
      '/workflows/job-guid-001',
      {
        status: WorkflowStatus.Aborted,
        abortedBy: 'admin-user-guid',
      },
    );
    expect(mockPut).toHaveBeenCalledTimes(1);
  });

  it('should invalidate workflows list and specific workflow', async () => {
    const updatePayload: UpdateWorkflowPayload = {
      id: 'job-guid-001',
      priority: 5,
    };
    mockPut.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateWorkflow(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(updatePayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Should invalidate the workflows list
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['workflows'],
    });
    // Should also invalidate the specific workflow detail
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['workflows', 'detail', 'job-guid-001'],
    });
  });
});

// --------------------------------------------------------------------------
// 8. useCancelWorkflow — Job cancellation (status → Aborted)
//    Replaces: Setting job.Status = Aborted in JobManager
//    Sends POST /workflows/{id}/cancel (no body), invalidates list + detail.
// --------------------------------------------------------------------------

describe('useCancelWorkflow', () => {
  it('should cancel workflow', async () => {
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useCancelWorkflow(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('job-guid-002');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // useCancelWorkflow takes id: string, calls post('/workflows/{id}/cancel')
    expect(mockPost).toHaveBeenCalledWith('/workflows/job-guid-002/cancel');
    expect(mockPost).toHaveBeenCalledTimes(1);
  });

  it('should invalidate workflows query', async () => {
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCancelWorkflow(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('job-guid-002');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Should invalidate the workflows list
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['workflows'],
    });
    // Should also invalidate the specific workflow detail
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['workflows', 'detail', 'job-guid-002'],
    });
  });

  it('should handle cancel of completed job', async () => {
    // Can't cancel a job that's already Completed — server returns 400.
    // The API client rejects on failure (interceptor throws for success:false).
    mockPost.mockRejectedValue(
      mockErrorResponse('Cannot cancel a completed workflow'),
    );

    const { result } = renderHook(() => useCancelWorkflow(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('job-guid-003');
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/workflows/job-guid-003/cancel');
  });
});

// ══════════════════════════════════════════════════════════════════════════════
// MUTATION HOOK TEST SUITES — SCHEDULE PLAN MUTATIONS
// ══════════════════════════════════════════════════════════════════════════════

// --------------------------------------------------------------------------
// 9. useCreateSchedulePlan — Schedule plan creation
//    Replaces: SheduleManager.Create(schedulePlan) + JobDataService.CreateSchedulePlan()
//    Sends POST /workflows/schedules, invalidates ['schedule-plans'].
// --------------------------------------------------------------------------

describe('useCreateSchedulePlan', () => {
  it('should create schedule plan', async () => {
    const createPayload: CreateSchedulePlanPayload = {
      name: 'daily-report-gen',
      type: SchedulePlanType.Daily,
      jobTypeId: 'job-type-guid-002',
      startTimespan: '08:00:00',
      enabled: true,
    };
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useCreateSchedulePlan(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(createPayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith('/workflows/schedules', createPayload);
    expect(mockPost).toHaveBeenCalledTimes(1);
  });

  it('should invalidate schedule-plans query', async () => {
    const createPayload: CreateSchedulePlanPayload = {
      name: 'weekly-cleanup',
      type: SchedulePlanType.Weekly,
      jobTypeId: 'job-type-guid-001',
      scheduledDays: { Mon: true, Wed: true, Fri: true },
      intervalInMinutes: 0,
      enabled: true,
    };
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateSchedulePlan(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(createPayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['schedule-plans'],
    });
  });
});

// --------------------------------------------------------------------------
// 10. useUpdateSchedulePlan — Schedule plan update
//     Replaces: SheduleManager.Update(schedulePlan)
//     Destructures {id, ...body}, sends PUT /workflows/schedules/{id}.
//     Invalidates ['schedule-plans'] (list) + ['schedule-plans', 'detail', id].
// --------------------------------------------------------------------------

describe('useUpdateSchedulePlan', () => {
  it('should update schedule plan', async () => {
    const updatePayload: UpdateSchedulePlanPayload = {
      id: 'schedule-guid-001',
      name: 'hourly-email-check-updated',
      intervalInMinutes: 30,
      enabled: false,
    };
    mockPut.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useUpdateSchedulePlan(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(updatePayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Hook destructures { id, ...body }, calls put('/workflows/schedules/{id}', body)
    expect(mockPut).toHaveBeenCalledWith(
      '/workflows/schedules/schedule-guid-001',
      {
        name: 'hourly-email-check-updated',
        intervalInMinutes: 30,
        enabled: false,
      },
    );
    expect(mockPut).toHaveBeenCalledTimes(1);
  });

  it('should invalidate schedule-plans list and specific schedule plan', async () => {
    const updatePayload: UpdateSchedulePlanPayload = {
      id: 'schedule-guid-001',
      enabled: true,
    };
    mockPut.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateSchedulePlan(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate(updatePayload);
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Should invalidate the schedule plans list
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['schedule-plans'],
    });
    // Should also invalidate the specific schedule plan detail
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['schedule-plans', 'detail', 'schedule-guid-001'],
    });
  });
});

// --------------------------------------------------------------------------
// 11. useDeleteSchedulePlan — Schedule plan deletion
//     Replaces: SheduleManager.Delete(id) + JobDataService.DeleteSchedulePlan()
//     Sends DELETE /workflows/schedules/{id}, invalidates ['schedule-plans'].
// --------------------------------------------------------------------------

describe('useDeleteSchedulePlan', () => {
  it('should delete schedule plan', async () => {
    mockDel.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useDeleteSchedulePlan(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('schedule-guid-001');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(mockDel).toHaveBeenCalledWith('/workflows/schedules/schedule-guid-001');
    expect(mockDel).toHaveBeenCalledTimes(1);
  });

  it('should invalidate schedule-plans query', async () => {
    mockDel.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteSchedulePlan(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('schedule-guid-001');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['schedule-plans'],
    });
  });
});

// --------------------------------------------------------------------------
// 12. useTriggerSchedulePlan — Manual schedule trigger
//     Replaces: SheduleManager.TriggerPlan(id) which creates a new job
//     immediately and resets the schedule's nextTriggerTime.
//     Sends POST /workflows/schedules/{id}/trigger.
//     Invalidates BOTH ['workflows'] (new job created) AND ['schedule-plans']
//     (nextTriggerTime updated).
// --------------------------------------------------------------------------

describe('useTriggerSchedulePlan', () => {
  it('should trigger schedule plan', async () => {
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const { result } = renderHook(() => useTriggerSchedulePlan(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('schedule-guid-001');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Takes id: string, calls post('/workflows/schedules/{id}/trigger')
    expect(mockPost).toHaveBeenCalledWith(
      '/workflows/schedules/schedule-guid-001/trigger',
    );
    expect(mockPost).toHaveBeenCalledTimes(1);
  });

  it('should invalidate workflows query (trigger creates a new job)', async () => {
    mockPost.mockResolvedValue(mockApiResponse({ ...mockBaseEnvelope }));

    const queryClient = createTestQueryClient();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useTriggerSchedulePlan(), {
      wrapper: createWrapper(queryClient),
    });

    await act(async () => {
      result.current.mutate('schedule-guid-001');
    });

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true);
    });

    // Triggering creates a new job — must invalidate workflow list
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['workflows'],
    });
    // Also invalidates schedule plans (nextTriggerTime updated)
    expect(invalidateSpy).toHaveBeenCalledWith({
      queryKey: ['schedule-plans'],
    });
  });

  it('should handle trigger of disabled schedule', async () => {
    // A disabled schedule plan cannot be triggered — server returns 400.
    // The API client rejects on failure (interceptor throws for success:false).
    mockPost.mockRejectedValue(
      mockErrorResponse('Cannot trigger a disabled schedule plan'),
    );

    const { result } = renderHook(() => useTriggerSchedulePlan(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate('disabled-schedule-guid');
    });

    await waitFor(() => {
      expect(result.current.isError).toBe(true);
    });

    expect(mockPost).toHaveBeenCalledWith(
      '/workflows/schedules/disabled-schedule-guid/trigger',
    );
  });
});
