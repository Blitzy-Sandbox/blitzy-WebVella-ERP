/**
 * @file useNotifications.test.ts
 * @description Comprehensive Vitest test suite for 11 TanStack Query hooks covering
 * notification and email management. These hooks replace the monolith's:
 * - MailService.cs (web layer SMTP helper)
 * - SmtpService.cs (Mail plugin SMTP engine with send/queue/validation)
 * - MailPlugin email/smtp_service entity CRUD
 * - PostgreSQL LISTEN/NOTIFY notification subsystem
 *
 * Tests verify correct API call behavior, response handling, caching (staleTime),
 * cache invalidation on mutations, and enabled/disabled state management.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, waitFor, act } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React, { type ReactNode } from 'react';

import {
  useNotifications,
  useNotification,
  useEmailTemplates,
  useSmtpConfigs,
  useSendEmail,
  useQueueEmail,
  useCreateEmailTemplate,
  useUpdateEmailTemplate,
  useDeleteEmailTemplate,
  useUpdateSmtpConfig,
  useMarkNotificationRead,
} from '../../../src/hooks/useNotifications';

import type {
  Notification,
  NotificationListResponse,
  EmailTemplate,
  EmailTemplateListResponse,
  SmtpConfig,
  SmtpConfigListResponse,
  SendEmailRequest,
  QueueEmailRequest,
  CreateEmailTemplateRequest,
  UpdateEmailTemplateRequest,
  UpdateSmtpConfigRequest,
} from '../../../src/hooks/useNotifications';

import type { BaseResponseModel } from '../../../src/types/common';
import type { ApiResponse } from '../../../src/api/client';

// ---------------------------------------------------------------------------
// Mock the API client module — intercepts all HTTP calls made by the hooks.
// Includes 'patch' for useMarkNotificationRead (PATCH /v1/notifications/{id}/read)
// in addition to the standard get/post/put/del used by other hook test suites.
// ---------------------------------------------------------------------------
vi.mock('../../../src/api/client', () => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
}));

import { get, post, put, patch, del } from '../../../src/api/client';

const mockedGet = vi.mocked(get);
const mockedPost = vi.mocked(post);
const mockedPut = vi.mocked(put);
const mockedPatch = vi.mocked(patch);
const mockedDel = vi.mocked(del);

// ---------------------------------------------------------------------------
// Test Fixtures — derived from MailPlugin entity definitions
// Notification fixtures map to the 'email' entity from MailPlugin.20190215.cs
// SMTP config fixtures map to the 'smtp_service' entity
// ---------------------------------------------------------------------------

const mockNotification: Notification = {
  id: 'notif-00000000-0000-0000-0000-000000000001',
  type: 'email',
  status: 'sent',
  priority: 'normal',
  subject: 'New task assigned',
  content: '<p>You have a new task</p>',
  sender: 'system@webvella.com',
  recipients: ['user@webvella.com'],
  ccRecipients: [],
  bccRecipients: [],
  retryCount: 0,
  maxRetries: 3,
  sentAt: '2024-01-20T10:00:00Z',
  createdBy: 'system-user-guid',
  createdOn: '2024-01-20T09:55:00Z',
};

const mockNotificationTwo: Notification = {
  id: 'notif-00000000-0000-0000-0000-000000000002',
  type: 'in_app',
  status: 'pending',
  priority: 'high',
  subject: 'Approval required',
  content: '<p>Invoice #1234 needs your approval</p>',
  sender: 'system@webvella.com',
  recipients: ['manager@webvella.com'],
  retryCount: 0,
  maxRetries: 3,
  createdBy: 'system-user-guid',
  createdOn: '2024-01-21T08:00:00Z',
};

const mockEmailTemplate: EmailTemplate = {
  id: 'template-00000000-0000-0000-0000-000000000001',
  name: 'task-assigned',
  subject: 'New Task: {{taskName}}',
  htmlContent: '<p>Hi {{userName}}, you have been assigned {{taskName}}</p>',
  variables: ['taskName', 'userName'],
  isActive: true,
  createdBy: 'admin-user-guid',
  createdOn: '2024-01-01T00:00:00Z',
  lastModifiedOn: '2024-01-15T12:00:00Z',
};

const mockSmtpConfig: SmtpConfig = {
  id: 'smtp-00000000-0000-0000-0000-000000000001',
  name: 'primary',
  server: 'smtp.example.com',
  port: 587,
  connectionSecurity: 'starttls',
  defaultFromEmail: 'noreply@webvella.com',
  maxRetriesCount: 3,
  retryWaitMinutes: 5,
  isDefault: true,
  isEnabled: true,
  createdOn: '2024-01-01T00:00:00Z',
  lastModifiedOn: '2024-01-10T00:00:00Z',
};

// ---------------------------------------------------------------------------
// Response helpers — match ApiResponse<T> shape from client.ts
// ---------------------------------------------------------------------------

function createSuccessResponse<T>(object: T): ApiResponse<T> {
  return {
    success: true,
    object,
    errors: [],
    statusCode: 200,
    timestamp: new Date().toISOString(),
    message: '',
    hash: 'response-hash',
  };
}

function createErrorResponse(
  statusCode: number,
  errors: Array<{ key: string; value: string; message: string }>,
  message?: string,
): ApiResponse<undefined> {
  return {
    success: false,
    object: undefined,
    errors,
    statusCode,
    timestamp: new Date().toISOString(),
    message: message ?? errors.map((e) => e.message).join(', '),
    hash: '',
  };
}

// ---------------------------------------------------------------------------
// QueryClient wrapper — uses React.createElement (this is a .ts file, not .tsx)
// ---------------------------------------------------------------------------

let queryClient: QueryClient;

function createWrapper() {
  return function Wrapper({ children }: { children: ReactNode }) {
    return React.createElement(QueryClientProvider, { client: queryClient }, children);
  };
}

// ---------------------------------------------------------------------------
// Test lifecycle — fresh QueryClient per test with retry:false for immediate
// propagation of failures; mocks cleared to avoid cross-test pollution.
// ---------------------------------------------------------------------------

beforeEach(() => {
  queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });
  vi.clearAllMocks();
});

afterEach(() => {
  queryClient.clear();
});

// ================================================================
//  1. useNotifications — List notifications with filtering
//     Replaces querying email entity records from MailPlugin
// ================================================================

describe('useNotifications', () => {
  const mockListResponse: NotificationListResponse = {
    notifications: [mockNotification, mockNotificationTwo],
    totalCount: 2,
    page: 1,
    pageSize: 10,
  };

  it('should fetch notifications', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockListResponse));

    const { result } = renderHook(() => useNotifications(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications', undefined);
    expect(result.current.data?.object?.notifications).toHaveLength(2);
    expect(result.current.data?.object?.totalCount).toBe(2);
  });

  it('should filter by status', async () => {
    const sentOnlyResponse: NotificationListResponse = {
      notifications: [mockNotification],
      totalCount: 1,
      page: 1,
      pageSize: 10,
    };
    mockedGet.mockResolvedValueOnce(createSuccessResponse(sentOnlyResponse));

    const { result } = renderHook(
      () => useNotifications({ status: 'sent' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications', { status: 'sent' });
    expect(result.current.data?.object?.notifications).toHaveLength(1);
    expect(result.current.data?.object?.notifications[0].status).toBe('sent');
  });

  it('should filter by type — email/in_app/webhook (AAP §0.4.1)', async () => {
    const emailOnlyResponse: NotificationListResponse = {
      notifications: [mockNotification],
      totalCount: 1,
      page: 1,
      pageSize: 10,
    };
    mockedGet.mockResolvedValueOnce(createSuccessResponse(emailOnlyResponse));

    const { result } = renderHook(
      () => useNotifications({ type: 'email' }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications', { type: 'email' });
    expect(result.current.data?.object?.notifications[0].type).toBe('email');
  });

  it('should filter by date range', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockListResponse));

    const dateFrom = '2024-01-01T00:00:00Z';
    const dateTo = '2024-01-31T23:59:59Z';

    const { result } = renderHook(
      () => useNotifications({ dateFrom, dateTo }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications', { dateFrom, dateTo });
  });

  it('should handle pagination', async () => {
    const page2Response: NotificationListResponse = {
      notifications: [mockNotificationTwo],
      totalCount: 11,
      page: 2,
      pageSize: 10,
    };
    mockedGet.mockResolvedValueOnce(createSuccessResponse(page2Response));

    const { result } = renderHook(
      () => useNotifications({ page: 2, pageSize: 10 }),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications', { page: 2, pageSize: 10 });
    expect(result.current.data?.object?.page).toBe(2);
    expect(result.current.data?.object?.totalCount).toBe(11);
  });
});

// ================================================================
//  2. useNotification — Single notification fetch
//     Replaces querying a single email entity record by ID
// ================================================================

describe('useNotification', () => {
  it('should fetch notification by ID', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockNotification));

    const { result } = renderHook(
      () => useNotification(mockNotification.id),
      { wrapper: createWrapper() },
    );

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith(`/notifications/${mockNotification.id}`);
    expect(result.current.data?.object?.id).toBe(mockNotification.id);
    expect(result.current.data?.object?.subject).toBe('New task assigned');
  });

  it('should not fetch when id is falsy — hook disabled via enabled: Boolean(id)', async () => {
    const { result } = renderHook(
      () => useNotification(''),
      { wrapper: createWrapper() },
    );

    // Query should remain idle; no API call should be made
    expect(result.current.isFetching).toBe(false);
    expect(mockedGet).not.toHaveBeenCalled();
  });
});

// ================================================================
//  3. useEmailTemplates — Email template listing
//     Replaces email template entity records from MailPlugin entities.
//     Templates change infrequently so staleTime is 10 minutes (600000ms).
// ================================================================

describe('useEmailTemplates', () => {
  const mockTemplateListResponse: EmailTemplateListResponse = {
    templates: [mockEmailTemplate],
    totalCount: 1,
  };

  it('should fetch email templates', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockTemplateListResponse));

    const { result } = renderHook(() => useEmailTemplates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications/templates');
    expect(result.current.data?.object?.templates).toHaveLength(1);
    expect(result.current.data?.object?.templates[0].name).toBe('task-assigned');
  });

  it('should use staleTime of 10 minutes — templates change infrequently', async () => {
    mockedGet.mockResolvedValue(createSuccessResponse(mockTemplateListResponse));

    // First render triggers API call
    const { result, rerender } = renderHook(() => useEmailTemplates(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockedGet).toHaveBeenCalledTimes(1);

    // Re-render should NOT trigger a new API call because data is still fresh
    // (staleTime: 600000ms = 10 minutes prevents immediate refetch)
    rerender();

    expect(mockedGet).toHaveBeenCalledTimes(1);
    expect(result.current.data?.object?.templates).toHaveLength(1);
  });
});

// ================================================================
//  4. useSmtpConfigs — SMTP configuration listing
//     Replaces smtp_service entity record queries from MailPlugin
// ================================================================

describe('useSmtpConfigs', () => {
  const mockSmtpConfigListResponse: SmtpConfigListResponse = {
    configs: [mockSmtpConfig],
    totalCount: 1,
  };

  it('should fetch SMTP configs', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockSmtpConfigListResponse));

    const { result } = renderHook(() => useSmtpConfigs(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedGet).toHaveBeenCalledWith('/notifications/smtp-configs');
    expect(result.current.data?.object?.configs).toHaveLength(1);
  });

  it('should include default config flag in response', async () => {
    mockedGet.mockResolvedValueOnce(createSuccessResponse(mockSmtpConfigListResponse));

    const { result } = renderHook(() => useSmtpConfigs(), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const defaultConfig = result.current.data?.object?.configs.find((c) => c.isDefault);
    expect(defaultConfig).toBeDefined();
    expect(defaultConfig?.name).toBe('primary');
    expect(defaultConfig?.isDefault).toBe(true);
  });
});

// ================================================================
//  5. useSendEmail — Send email mutation
//     Replaces SmtpService.SendEmail (synchronous SMTP send in monolith).
//     In the serverless architecture, email is dispatched via SQS-triggered
//     Lambda (replacing SmtpInboundProcessorJob from Mail plugin).
// ================================================================

describe('useSendEmail', () => {
  const mockSendRequest: SendEmailRequest = {
    recipients: ['user@webvella.com'],
    subject: 'Task Update',
    htmlContent: '<p>Your task has been updated</p>',
    templateId: mockEmailTemplate.id,
  };

  const mockSendResponse: BaseResponseModel = {
    success: true,
    message: 'Email sent successfully',
    timestamp: '2024-01-20T10:01:00Z',
    hash: 'send-hash',
    errors: [],
    accessWarnings: [],
  };

  it('should send email', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockSendResponse));

    const { result } = renderHook(() => useSendEmail(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockSendRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/notifications/emails/send',
      expect.objectContaining({
        recipients: expect.arrayContaining([
          expect.objectContaining({ address: 'user@webvella.com' }),
        ]),
        subject: 'Task Update',
        html_body: '<p>Your task has been updated</p>',
      }),
    );
  });

  it('should invalidate notifications query on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockSendResponse));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useSendEmail(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockSendRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['notifications'] }),
    );
  });

  it('should handle send failure — SMTP error (500)', async () => {
    mockedPost.mockResolvedValueOnce(
      createErrorResponse(500, [
        { key: 'smtp', value: '', message: 'SMTP connection refused' },
      ]),
    );

    const { result } = renderHook(() => useSendEmail(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockSendRequest);
    });

    await waitFor(() => expect(result.current.isError).toBe(true));
    expect(result.current.error?.message).toContain('SMTP connection refused');
  });
});

// ================================================================
//  6. useQueueEmail — Queue email for async delivery
//     Replaces Mail plugin job-based queue (SmtpInboundProcessorJob).
//     Email sending is async via SQS-triggered Lambda (AAP §0.5.1).
//     The mutation resolves immediately with queued status — actual
//     email delivery happens asynchronously.
// ================================================================

describe('useQueueEmail', () => {
  const mockQueueRequest: QueueEmailRequest = {
    recipients: ['team@webvella.com'],
    subject: 'Weekly Report',
    htmlContent: '<p>Attached is the weekly report</p>',
    scheduledAt: '2024-01-25T09:00:00Z',
  };

  const mockQueueResponse: BaseResponseModel = {
    success: true,
    message: 'Email queued for delivery',
    timestamp: '2024-01-20T10:02:00Z',
    hash: 'queue-hash',
    errors: [],
    accessWarnings: [],
  };

  it('should queue email for later delivery', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockQueueResponse));

    const { result } = renderHook(() => useQueueEmail(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockQueueRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/notifications/emails/queue',
      expect.objectContaining({
        recipients: expect.arrayContaining([
          expect.objectContaining({ address: 'team@webvella.com' }),
        ]),
        subject: 'Weekly Report',
        html_body: '<p>Attached is the weekly report</p>',
      }),
    );
  });

  it('should return queued status immediately — async SQS-triggered Lambda', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockQueueResponse));

    const { result } = renderHook(() => useQueueEmail(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockQueueRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    // The mutation resolves immediately — actual email sending happens
    // asynchronously via SQS-triggered Lambda (replacing SmtpInboundProcessorJob)
    expect(result.current.data?.success).toBe(true);
    expect(result.current.data?.object?.message).toBe('Email queued for delivery');
  });

  it('should invalidate notifications query on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockQueueResponse));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useQueueEmail(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockQueueRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['notifications'] }),
    );
  });
});

// ================================================================
//  7. useCreateEmailTemplate — Create email template
//     Replaces creating email template entity records in MailPlugin.
//     Supports {{tag}} token replacement (RenderService.cs pattern).
// ================================================================

describe('useCreateEmailTemplate', () => {
  const mockCreateRequest: CreateEmailTemplateRequest = {
    name: 'invoice-sent',
    subject: 'Invoice #{{invoiceNumber}} sent',
    htmlContent: '<p>Dear {{customerName}}, your invoice #{{invoiceNumber}} has been sent.</p>',
    variables: ['invoiceNumber', 'customerName'],
  };

  it('should create email template', async () => {
    const createdTemplate: EmailTemplate = {
      ...mockEmailTemplate,
      id: 'template-new-guid',
      name: 'invoice-sent',
      subject: mockCreateRequest.subject,
      htmlContent: mockCreateRequest.htmlContent,
      variables: mockCreateRequest.variables!,
    };
    mockedPost.mockResolvedValueOnce(createSuccessResponse(createdTemplate));

    const { result } = renderHook(() => useCreateEmailTemplate(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockCreateRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPost).toHaveBeenCalledWith(
      '/notifications/templates',
      mockCreateRequest,
    );
  });

  it('should invalidate email-templates query on success', async () => {
    mockedPost.mockResolvedValueOnce(createSuccessResponse(mockEmailTemplate));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCreateEmailTemplate(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockCreateRequest);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['email-templates'] }),
    );
  });
});

// ================================================================
//  8. useUpdateEmailTemplate — Update existing email template
//     Replaces updating email template entity records in MailPlugin.
//     Accepts { id, data } param — PUT /v1/notifications/templates/{id}
// ================================================================

describe('useUpdateEmailTemplate', () => {
  const mockUpdateRequest: UpdateEmailTemplateRequest = {
    name: 'task-assigned-v2',
    subject: 'Task Assigned: {{taskName}} [Updated]',
    htmlContent: '<p>Hi {{userName}}, task "{{taskName}}" has been assigned to you. Due: {{dueDate}}</p>',
    variables: ['taskName', 'userName', 'dueDate'],
  };

  it('should update template', async () => {
    const updatedTemplate: EmailTemplate = {
      ...mockEmailTemplate,
      name: mockUpdateRequest.name!,
      subject: mockUpdateRequest.subject!,
      htmlContent: mockUpdateRequest.htmlContent!,
      variables: mockUpdateRequest.variables!,
      lastModifiedOn: '2024-02-01T00:00:00Z',
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedTemplate));

    const { result } = renderHook(() => useUpdateEmailTemplate(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockEmailTemplate.id, data: mockUpdateRequest });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/notifications/templates/${mockEmailTemplate.id}`,
      mockUpdateRequest,
    );
  });

  it('should invalidate email-templates query on success', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockEmailTemplate));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateEmailTemplate(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockEmailTemplate.id, data: mockUpdateRequest });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['email-templates'] }),
    );
  });
});

// ================================================================
//  9. useDeleteEmailTemplate — Delete email template
//     Replaces deleting email template entity records in MailPlugin.
//     Accepts string id — DELETE /v1/notifications/templates/{id}
// ================================================================

describe('useDeleteEmailTemplate', () => {
  it('should delete template', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));

    const { result } = renderHook(() => useDeleteEmailTemplate(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockEmailTemplate.id);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedDel).toHaveBeenCalledWith(
      `/notifications/templates/${mockEmailTemplate.id}`,
    );
  });

  it('should invalidate email-templates query on success', async () => {
    mockedDel.mockResolvedValueOnce(createSuccessResponse(undefined));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useDeleteEmailTemplate(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockEmailTemplate.id);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['email-templates'] }),
    );
  });
});

// ================================================================
//  10. useUpdateSmtpConfig — Update SMTP configuration
//      Replaces SMTP service record updates in the monolith.
//      SMTP credentials (password) are stored in SSM Parameter Store
//      SecureString — never exposed to the frontend (AAP §0.8.3).
// ================================================================

describe('useUpdateSmtpConfig', () => {
  const mockSmtpUpdateRequest: UpdateSmtpConfigRequest = {
    name: 'primary-updated',
    server: 'smtp.newprovider.com',
    port: 465,
    connectionSecurity: 'ssl',
    defaultFromEmail: 'noreply@newprovider.com',
    isDefault: true,
  };

  it('should update SMTP config', async () => {
    const updatedConfig: SmtpConfig = {
      ...mockSmtpConfig,
      name: 'primary-updated',
      server: 'smtp.newprovider.com',
      port: 465,
      connectionSecurity: 'ssl' as const,
      defaultFromEmail: 'noreply@newprovider.com',
      lastModifiedOn: '2024-02-01T00:00:00Z',
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(updatedConfig));

    const { result } = renderHook(() => useUpdateSmtpConfig(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockSmtpConfig.id, data: mockSmtpUpdateRequest });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPut).toHaveBeenCalledWith(
      `/notifications/smtp-configs/${mockSmtpConfig.id}`,
      mockSmtpUpdateRequest,
    );
  });

  it('should invalidate smtp-configs query on success', async () => {
    mockedPut.mockResolvedValueOnce(createSuccessResponse(mockSmtpConfig));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useUpdateSmtpConfig(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockSmtpConfig.id, data: mockSmtpUpdateRequest });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['smtp-configs'] }),
    );
  });

  it('should NOT expose sensitive SMTP credentials — stored in SSM per AAP §0.8.3', async () => {
    // SMTP credentials (password) must NEVER appear in API responses.
    // Per AAP security requirements, secrets are stored in SSM Parameter Store SecureString.
    const configResponse: SmtpConfig = {
      ...mockSmtpConfig,
      name: 'primary-updated',
    };
    mockedPut.mockResolvedValueOnce(createSuccessResponse(configResponse));

    const { result } = renderHook(() => useUpdateSmtpConfig(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate({ id: mockSmtpConfig.id, data: mockSmtpUpdateRequest });
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    const responseConfig = result.current.data?.object;
    // SmtpConfig type does NOT include a 'password' field by design
    // Credentials are managed via SSM SecureString, never exposed to the frontend
    expect(responseConfig).toBeDefined();
    expect(responseConfig).not.toHaveProperty('password');
    expect(responseConfig).not.toHaveProperty('secret');
    expect(responseConfig).not.toHaveProperty('credentials');
  });
});

// ================================================================
//  11. useMarkNotificationRead — Mark notification as read
//      Replaces updating email entity record status field.
//      Uses PATCH method — PATCH /v1/notifications/{id}/read
// ================================================================

describe('useMarkNotificationRead', () => {
  const mockReadResponse: BaseResponseModel = {
    success: true,
    message: 'Notification marked as read',
    timestamp: '2024-01-20T11:00:00Z',
    hash: 'read-hash',
    errors: [],
    accessWarnings: [],
  };

  it('should mark notification as read', async () => {
    mockedPatch.mockResolvedValueOnce(createSuccessResponse(mockReadResponse));

    const { result } = renderHook(() => useMarkNotificationRead(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockNotification.id);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(mockedPatch).toHaveBeenCalledWith(
      `/notifications/${mockNotification.id}/read`,
    );
  });

  it('should invalidate notifications query on success', async () => {
    mockedPatch.mockResolvedValueOnce(createSuccessResponse(mockReadResponse));
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useMarkNotificationRead(), {
      wrapper: createWrapper(),
    });

    await act(async () => {
      result.current.mutate(mockNotification.id);
    });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: ['notifications'] }),
    );
  });
});
