/**
 * @fileoverview TanStack Query 5 hooks for email and notification management.
 *
 * Replaces the monolith's MailService.cs (web layer SMTP log-email helper with
 * {{tag}} template rendering gated by ErpSettings.EmailEnabled),
 * SmtpInternalService.cs (Mail plugin SMTP engine with send/queue/validation,
 * retry scheduling, MIME composition via MailKit, and outbound queue
 * processing), MailPlugin.cs (email/smtp_service entity CRUD with 10-minute
 * interval scheduled queue processing), and the PostgreSQL LISTEN/NOTIFY
 * notification subsystem (NotificationContext.cs — singleton pub/sub
 * dispatcher, base64-encoded JSON over NOTIFY ERP_NOTIFICATIONS_CHANNNEL)
 * with API calls to the Notifications microservice Lambda handlers via
 * HTTP API Gateway.
 *
 * Notification types supported: email, in-app, webhook (AAP §0.4.1).
 * Email sending is async (SQS-triggered Lambda) — mutations return
 * immediately with queued status.
 *
 * @module useNotifications
 */

import {
  useQuery,
  useMutation,
  useQueryClient,
} from '@tanstack/react-query';
import { get, post, put, patch, del } from '../api/client';
import type { ApiResponse } from '../api/client';
import type { BaseResponseModel } from '../types/common';

// ---------------------------------------------------------------------------
// Query Key Factory
// ---------------------------------------------------------------------------

/**
 * Centralized query key factory for notification-related queries.
 * Ensures consistent key structure across all notification hooks
 * for cache management and invalidation.
 */
const notificationKeys = {
  /** Root key for all notification queries. */
  all: ['notifications'] as const,
  /** Key for notification list queries (with varying filter params). */
  lists: () => [...notificationKeys.all, 'list'] as const,
  /** Key for a specific notification list query filtered by params. */
  list: (params: NotificationListParams | undefined) =>
    [...notificationKeys.lists(), params] as const,
  /** Key prefix for single-notification detail queries. */
  details: () => [...notificationKeys.all, 'detail'] as const,
  /** Key for a specific notification detail by ID. */
  detail: (id: string) => [...notificationKeys.details(), id] as const,
};

/**
 * Centralized query key factory for email template queries.
 */
const emailTemplateKeys = {
  /** Root key for all email template queries. */
  all: ['email-templates'] as const,
  /** Key for email template list queries. */
  lists: () => [...emailTemplateKeys.all, 'list'] as const,
};

/**
 * Centralized query key factory for SMTP configuration queries.
 */
const smtpConfigKeys = {
  /** Root key for all SMTP configuration queries. */
  all: ['smtp-configs'] as const,
  /** Key for SMTP config list queries. */
  lists: () => [...smtpConfigKeys.all, 'list'] as const,
};

// ---------------------------------------------------------------------------
// Local Type Definitions
// ---------------------------------------------------------------------------

/**
 * Notification type classification.
 * Matches the target architecture notification types (AAP §0.4.1):
 * - email: SMTP-based email notifications (replaces MailPlugin email entity)
 * - in_app: In-application notifications (replaces PostgreSQL LISTEN/NOTIFY)
 * - webhook: External webhook dispatch (replaces NotificationContext handlers)
 */
export type NotificationType = 'email' | 'in_app' | 'webhook';

/**
 * Notification delivery status.
 * Maps from the monolith's email entity status field and
 * SmtpInternalService queue processing states:
 * - pending: Awaiting processing
 * - queued: Placed in SQS queue (replaces Mail plugin's scheduled job queue)
 * - sent: Successfully delivered
 * - failed: Delivery failed after retries (replaces SmtpInternalService retry logic)
 * - read: Recipient has read/acknowledged the notification
 */
export type NotificationStatus =
  | 'pending'
  | 'queued'
  | 'sent'
  | 'failed'
  | 'read';

/**
 * Notification priority levels.
 * Mirrors the SmtpInternalService queue ordering (priority + schedule).
 */
export type NotificationPriority = 'low' | 'normal' | 'high' | 'urgent';

/**
 * Notification model returned by the Notifications service.
 * Replaces the monolith's email entity record (subject, content, sender,
 * recipients, status, sent_on) and the Notification.cs DTO
 * (Channel + Message envelope) with a unified notification model
 * covering email, in-app, and webhook types.
 */
export interface Notification {
  /** Unique notification identifier (GUID as string). */
  id: string;
  /** Notification type: email, in-app, or webhook. */
  type: NotificationType;
  /** Current delivery status. */
  status: NotificationStatus;
  /** Notification priority for queue ordering. */
  priority: NotificationPriority;
  /** Subject line (for email) or title (for in-app/webhook). */
  subject: string;
  /** Notification body content (HTML for email, text for in-app). */
  content: string;
  /** Sender address (email) or system identifier. */
  sender: string;
  /** Primary recipient addresses (email) or user IDs (in-app). */
  recipients: string[];
  /** Carbon copy recipients (email only). Replaces SmtpInternalService cc: prefix handling. */
  ccRecipients?: string[];
  /** Blind carbon copy recipients (email only). Replaces SmtpInternalService bcc: prefix handling. */
  bccRecipients?: string[];
  /** Reply-to address (email only). */
  replyTo?: string;
  /** Attachment file IDs (email only). Replaces SmtpInternalService attachment resolution. */
  attachmentIds?: string[];
  /** Email template ID used for rendering (replaces MailService {{tag}} template system). */
  templateId?: string;
  /** Template variable substitutions (replaces MailService ReplaceTagsInHtml). */
  templateVariables?: Record<string, string>;
  /** Webhook destination URL (webhook type only). */
  webhookUrl?: string;
  /** SMTP configuration ID used for sending. Replaces smtp_service entity reference. */
  smtpConfigId?: string;
  /** Number of delivery attempts made. Replaces SmtpInternalService retry tracking. */
  retryCount: number;
  /** Maximum retry attempts allowed. Replaces SmtpInternalService max_retries_count (1–10). */
  maxRetries: number;
  /** Error message from last failed delivery attempt. */
  lastError?: string;
  /** ISO 8601 timestamp when the notification was scheduled for delivery. */
  scheduledAt?: string;
  /** ISO 8601 timestamp when the notification was successfully sent. */
  sentAt?: string;
  /** ISO 8601 timestamp when the notification was read/acknowledged. */
  readAt?: string;
  /** User ID of the creator. */
  createdBy: string;
  /** ISO 8601 creation timestamp. */
  createdOn: string;
  /** ISO 8601 last modification timestamp. */
  lastModifiedOn?: string;
}

/**
 * Paginated notification list response from GET /v1/notifications.
 */
export interface NotificationListResponse {
  /** Array of notification records. */
  notifications: Notification[];
  /** Total number of notifications matching the filters. */
  totalCount: number;
  /** Current page number. */
  page: number;
  /** Page size used for this request. */
  pageSize: number;
}

/**
 * Parameters for the notification listing query.
 * Supports filtering by status, type, date range, and pagination.
 */
export interface NotificationListParams {
  /** Filter by notification delivery status. */
  status?: NotificationStatus;
  /** Filter by notification type (email, in_app, webhook). */
  type?: NotificationType;
  /** Filter by date range start (ISO 8601). */
  dateFrom?: string;
  /** Filter by date range end (ISO 8601). */
  dateTo?: string;
  /** Search text applied over subject and content fields. */
  search?: string;
  /** Page number (1-based, default: 1). */
  page?: number;
  /** Number of items per page (default: 20). */
  pageSize?: number;
}

/**
 * Email template model.
 * Replaces the monolith's hardcoded HTML templates (LogTemplate,
 * InvoiceTemplate constants in MailService.cs) with dynamic,
 * server-managed templates supporting {{tag}} variable substitution.
 */
export interface EmailTemplate {
  /** Unique template identifier (GUID as string). */
  id: string;
  /** Template name for identification. */
  name: string;
  /** Template subject line (may contain {{tag}} placeholders). */
  subject: string;
  /** Template HTML body content (may contain {{tag}} placeholders). */
  htmlContent: string;
  /** Template plain-text body content (optional alternative). */
  textContent?: string;
  /** List of variable names used in the template for validation. */
  variables: string[];
  /** Whether this template is active and available for use. */
  isActive: boolean;
  /** User ID of the creator. */
  createdBy: string;
  /** ISO 8601 creation timestamp. */
  createdOn: string;
  /** ISO 8601 last modification timestamp. */
  lastModifiedOn?: string;
}

/**
 * Email template list response from GET /v1/notifications/templates.
 */
export interface EmailTemplateListResponse {
  /** Array of email template records. */
  templates: EmailTemplate[];
  /** Total number of templates. */
  totalCount: number;
}

/**
 * SMTP configuration model.
 * Replaces the monolith's smtp_service entity record with fields:
 * name (unique), server, port (1–65025), connection_security (SecureSocketOptions),
 * default_from_email, default_reply_to_email, max_retries_count (1–10),
 * retry_wait_minutes (1–1440), is_default, username, password.
 * Server-side validation is handled by SmtpInternalService.ValidatePreCreateRecord
 * / ValidatePreUpdateRecord.
 *
 * Note: Actual SMTP credentials (username, password) are stored in SSM
 * Parameter Store SecureString per AAP §0.8.3 — never in client state.
 * The smtpUsername/smtpPassword fields here are write-only for config
 * creation/update and are never returned by the API.
 */
export interface SmtpConfig {
  /** Unique SMTP configuration identifier (GUID as string). */
  id: string;
  /** Unique configuration name. Enforced unique by server validation. */
  name: string;
  /** SMTP server hostname. */
  server: string;
  /** SMTP server port (1–65025). Validated server-side. */
  port: number;
  /** Connection security mode (replaces MailKit SecureSocketOptions enum). */
  connectionSecurity: 'none' | 'ssl' | 'starttls' | 'auto';
  /** Default sender email address. Required, validated as email server-side. */
  defaultFromEmail: string;
  /** Default display name for the sender. */
  defaultFromName?: string;
  /** Default reply-to email address. Optional, validated as email if provided. */
  defaultReplyToEmail?: string;
  /** Maximum retry count for failed deliveries (1–10). */
  maxRetriesCount: number;
  /** Wait time between retries in minutes (1–1440). */
  retryWaitMinutes: number;
  /** Whether this is the default SMTP configuration. Only one can be default. */
  isDefault: boolean;
  /** Whether this SMTP configuration is enabled. */
  isEnabled: boolean;
  /** ISO 8601 creation timestamp. */
  createdOn: string;
  /** ISO 8601 last modification timestamp. */
  lastModifiedOn?: string;
}

/**
 * SMTP configuration list response from GET /v1/notifications/smtp-configs.
 */
export interface SmtpConfigListResponse {
  /** Array of SMTP configuration records. */
  configs: SmtpConfig[];
  /** Total number of SMTP configurations. */
  totalCount: number;
}

/**
 * Request body for POST /v1/notifications/email/send.
 * Triggers immediate email dispatch via the Notifications service Lambda.
 * Replaces SmtpInternalService.SendEmail (MimeMessage composition + SmtpClient).
 */
export interface SendEmailRequest {
  /** Sender email address. Defaults to the logged-in user's email. */
  senderEmail?: string;
  /** Sender display name. */
  senderName?: string;
  /** Recipient email addresses. */
  recipients: string[];
  /** CC recipients (replaces SmtpInternalService cc: prefix handling). */
  ccRecipients?: string[];
  /** BCC recipients (replaces SmtpInternalService bcc: prefix handling). */
  bccRecipients?: string[];
  /** Email subject line. */
  subject: string;
  /** HTML email body content. */
  htmlContent: string;
  /** Plain-text email body content (optional alternative). */
  textContent?: string;
  /** Reply-to email address. */
  replyTo?: string;
  /** SMTP configuration ID to use. If omitted, uses the default. */
  smtpConfigId?: string;
  /** File IDs to attach. Replaces SmtpInternalService attachment resolution. */
  attachmentIds?: string[];
  /** Email template ID (if using template-based rendering). */
  templateId?: string;
  /** Template variable substitutions for {{tag}} replacement. */
  templateVariables?: Record<string, string>;
  /** Notification priority for queue ordering. */
  priority?: NotificationPriority;
}

/**
 * Request body for POST /v1/notifications/email/queue.
 * Queues an email for async delivery via SQS-triggered Lambda.
 * Replaces Mail plugin's scheduled job-based SMTP queue processing
 * (ProcessSmtpQueue with 10-minute interval schedule plan).
 */
export interface QueueEmailRequest {
  /** Recipient email addresses. */
  recipients: string[];
  /** CC recipients. */
  ccRecipients?: string[];
  /** BCC recipients. */
  bccRecipients?: string[];
  /** Email subject line. */
  subject: string;
  /** HTML email body content. */
  htmlContent: string;
  /** Plain-text email body content (optional alternative). */
  textContent?: string;
  /** Reply-to email address. */
  replyTo?: string;
  /** SMTP configuration ID to use. If omitted, uses the default. */
  smtpConfigId?: string;
  /** File IDs to attach. */
  attachmentIds?: string[];
  /** Email template ID (if using template-based rendering). */
  templateId?: string;
  /** Template variable substitutions for {{tag}} replacement. */
  templateVariables?: Record<string, string>;
  /** Notification priority for queue ordering. */
  priority?: NotificationPriority;
  /** ISO 8601 scheduled delivery time. If omitted, sends immediately. */
  scheduledAt?: string;
}

/**
 * Request body for POST /v1/notifications/templates.
 * Creates a new email template replacing the monolith's hardcoded
 * HTML template constants (LogTemplate, InvoiceTemplate).
 */
export interface CreateEmailTemplateRequest {
  /** Template name for identification. Must be unique. */
  name: string;
  /** Template subject line (may contain {{tag}} placeholders). */
  subject: string;
  /** Template HTML body content (may contain {{tag}} placeholders). */
  htmlContent: string;
  /** Template plain-text body content (optional alternative). */
  textContent?: string;
  /** List of variable names used in the template. */
  variables?: string[];
  /** Whether this template is active. Defaults to true. */
  isActive?: boolean;
}

/**
 * Request body for PUT /v1/notifications/templates/{id}.
 * Updates an existing email template.
 */
export interface UpdateEmailTemplateRequest {
  /** Updated template name. */
  name?: string;
  /** Updated subject line. */
  subject?: string;
  /** Updated HTML body content. */
  htmlContent?: string;
  /** Updated plain-text body content. */
  textContent?: string;
  /** Updated list of variable names. */
  variables?: string[];
  /** Updated active status. */
  isActive?: boolean;
}

/**
 * Request body for PUT /v1/notifications/smtp-configs/{id}.
 * Updates SMTP configuration. Replaces smtp_service entity record updates.
 * Server-side validation enforces: unique name, port range (1–65025),
 * max_retries_count (1–10), retry_wait_minutes (1–1440), valid email formats.
 */
export interface UpdateSmtpConfigRequest {
  /** Updated configuration name. Must be unique. */
  name?: string;
  /** Updated SMTP server hostname. */
  server?: string;
  /** Updated SMTP server port (1–65025). */
  port?: number;
  /** Updated connection security mode. */
  connectionSecurity?: 'none' | 'ssl' | 'starttls' | 'auto';
  /** Updated default sender email address. */
  defaultFromEmail?: string;
  /** Updated default sender display name. */
  defaultFromName?: string;
  /** Updated default reply-to email address. */
  defaultReplyToEmail?: string;
  /** Updated maximum retry count (1–10). */
  maxRetriesCount?: number;
  /** Updated retry wait time in minutes (1–1440). */
  retryWaitMinutes?: number;
  /** Updated default flag. Setting true clears default on all other configs. */
  isDefault?: boolean;
  /** Updated enabled status. */
  isEnabled?: boolean;
  /** SMTP username (write-only, stored in SSM). */
  smtpUsername?: string;
  /** SMTP password (write-only, stored in SSM). */
  smtpPassword?: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Stale time for email template queries: 10 minutes (600,000ms).
 * Templates change infrequently, so a longer stale time reduces
 * unnecessary refetches while still providing reasonable freshness.
 */
const EMAIL_TEMPLATE_STALE_TIME_MS = 10 * 60 * 1000;

// ---------------------------------------------------------------------------
// Query Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches a paginated list of notifications with optional filters.
 *
 * Replaces querying the monolith's `email` entity records via EQL
 * and the PostgreSQL LISTEN/NOTIFY notification channel. Supports
 * filtering by status (pending/queued/sent/failed/read), type
 * (email/in_app/webhook), and date range for the notification listing.
 *
 * @param params - Optional filter, sort, and pagination parameters.
 * @returns TanStack Query result with notification list data, loading
 *          state, fetch state, error info, and refetch function.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isFetching, isError, error, isSuccess, refetch } =
 *   useNotifications({ status: 'sent', type: 'email', page: 1, pageSize: 20 });
 * const notifications = data?.object?.notifications ?? [];
 * const totalCount = data?.object?.totalCount ?? 0;
 * ```
 */
export function useNotifications(params?: NotificationListParams) {
  return useQuery({
    queryKey: notificationKeys.list(params),
    queryFn: async (): Promise<ApiResponse<NotificationListResponse>> => {
      const response = await get<NotificationListResponse>(
        '/notifications',
        params as Record<string, unknown> | undefined,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to fetch notifications';
        throw new Error(errorMessage);
      }
      return response;
    },
  });
}

/**
 * Fetches a single notification by ID.
 *
 * Returns full notification details including delivery status, content,
 * recipients, retry count, and timestamps. The query is enabled only
 * when a valid ID is provided to prevent unnecessary API calls.
 *
 * @param id - The notification GUID identifier (undefined to disable the query).
 * @returns TanStack Query result with single notification data.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isError, error, isSuccess, refetch } =
 *   useNotification(notificationId);
 * const notification = data?.object;
 * ```
 */
export function useNotification(id: string | undefined) {
  return useQuery({
    queryKey: notificationKeys.detail(id ?? ''),
    queryFn: async (): Promise<ApiResponse<Notification>> => {
      const response = await get<Notification>(`/notifications/${id}`);
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to fetch notification';
        throw new Error(errorMessage);
      }
      return response;
    },
    enabled: Boolean(id),
  });
}

/**
 * Fetches the list of email templates.
 *
 * Replaces the monolith's hardcoded HTML template constants
 * (LogTemplate, InvoiceTemplate in MailService.cs) with dynamic
 * server-managed templates that support {{tag}} variable substitution.
 * Uses a 10-minute staleTime since templates change infrequently.
 *
 * @returns TanStack Query result with email template list data.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isFetching, isError, error, isSuccess, refetch } =
 *   useEmailTemplates();
 * const templates = data?.object?.templates ?? [];
 * ```
 */
export function useEmailTemplates() {
  return useQuery({
    queryKey: emailTemplateKeys.lists(),
    queryFn: async (): Promise<ApiResponse<EmailTemplateListResponse>> => {
      const response = await get<EmailTemplateListResponse>(
        '/notifications/templates',
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to fetch email templates';
        throw new Error(errorMessage);
      }
      return response;
    },
    staleTime: EMAIL_TEMPLATE_STALE_TIME_MS,
  });
}

/**
 * Fetches the list of SMTP server configurations.
 *
 * Replaces querying the monolith's `smtp_service` entity records.
 * Each configuration represents an SMTP server with connection details,
 * retry settings, and default sender information. Server-side validation
 * enforces unique names, port ranges (1–65025), and valid email formats
 * per SmtpInternalService.ValidatePreCreateRecord/ValidatePreUpdateRecord.
 *
 * Note: SMTP credentials (username/password) are stored in SSM Parameter
 * Store and are never returned by this endpoint (AAP §0.8.3).
 *
 * @returns TanStack Query result with SMTP configuration list data.
 *
 * @example
 * ```tsx
 * const { data, isLoading, isFetching, isError, error, isSuccess, refetch } =
 *   useSmtpConfigs();
 * const configs = data?.object?.configs ?? [];
 * const defaultConfig = configs.find(c => c.isDefault);
 * ```
 */
export function useSmtpConfigs() {
  return useQuery({
    queryKey: smtpConfigKeys.lists(),
    queryFn: async (): Promise<ApiResponse<SmtpConfigListResponse>> => {
      const response = await get<SmtpConfigListResponse>(
        '/notifications/smtp-configs',
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to fetch SMTP configurations';
        throw new Error(errorMessage);
      }
      return response;
    },
  });
}

// ---------------------------------------------------------------------------
// Mutation Hooks
// ---------------------------------------------------------------------------

/**
 * Sends an email immediately via the Notifications service.
 *
 * Replaces SmtpInternalService.SendEmail which composed a MimeMessage
 * (From/To/CC/BCC/Reply-To/Subject/HTML body) with MailKit, handled
 * inline image CID embedding via ProcessHtmlContent, and transmitted
 * via SmtpClient. In the target architecture, the Lambda handler
 * processes the request and publishes to an SQS queue for async delivery.
 *
 * On success, invalidates the notifications list cache to reflect the
 * newly created notification record.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useSendEmail();
 * await mutateAsync({
 *   recipients: ['user@example.com'],
 *   subject: 'Hello',
 *   htmlContent: '<p>Hi there!</p>',
 *   smtpConfigId: 'config-uuid',
 * });
 * ```
 */
/**
 * Transforms the frontend SendEmailRequest / QueueEmailRequest into the
 * backend-compatible JSON payload.  The backend EmailHandler expects:
 *   recipients: [{name, address}], sender: {name, address},
 *   subject, text_body, html_body, attachment_keys, reply_to, priority
 * while the frontend uses flat string fields (senderEmail, htmlContent, etc.).
 */
function transformEmailPayload(
  request: SendEmailRequest | QueueEmailRequest,
): Record<string, unknown> {
  const toAddr = (email: string) => ({ name: '', address: email });
  const allRecipients = [
    ...request.recipients.map(toAddr),
    ...(request.ccRecipients ?? []).map((e) => ({ name: `cc:${e}`, address: e })),
    ...(request.bccRecipients ?? []).map((e) => ({ name: `bcc:${e}`, address: e })),
  ];

  const payload: Record<string, unknown> = {
    recipients: allRecipients,
    subject: request.subject,
    text_body: request.textContent ?? '',
    html_body: request.htmlContent ?? '',
    attachment_keys: request.attachmentIds ?? [],
  };

  // Build sender object when provided
  if ('senderEmail' in request && (request as SendEmailRequest).senderEmail) {
    payload.sender = {
      name: (request as SendEmailRequest).senderName ?? '',
      address: (request as SendEmailRequest).senderEmail,
    };
  }

  if (request.replyTo) {
    payload.reply_to = request.replyTo;
  }

  if (request.priority !== undefined) {
    payload.priority = request.priority;
  }

  return payload;
}

export function useSendEmail() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      request: SendEmailRequest,
    ): Promise<ApiResponse<BaseResponseModel>> => {
      const response = await post<BaseResponseModel>(
        '/notifications/emails/send',
        transformEmailPayload(request),
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to send email';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}

/**
 * Queues an email for asynchronous delivery via SQS.
 *
 * Replaces the Mail plugin's scheduled job-based SMTP queue processing
 * (ProcessSmtpQueue with 10-minute interval SchedulePlan, static lock
 * object, and queueProcessingInProgress guard). In the target architecture,
 * the email is placed on an SQS queue and processed by a dedicated
 * SQS-triggered Lambda worker.
 *
 * Supports optional scheduled delivery time for future-dated emails.
 *
 * On success, invalidates the notifications list cache to reflect the
 * newly queued notification record.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useQueueEmail();
 * await mutateAsync({
 *   recipients: ['user@example.com'],
 *   subject: 'Scheduled Report',
 *   htmlContent: '<p>Your weekly report</p>',
 *   scheduledAt: '2026-03-01T09:00:00Z',
 *   priority: 'normal',
 * });
 * ```
 */
export function useQueueEmail() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      request: QueueEmailRequest,
    ): Promise<ApiResponse<BaseResponseModel>> => {
      const response = await post<BaseResponseModel>(
        '/notifications/emails/queue',
        transformEmailPayload(request),
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to queue email';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}

/**
 * Creates a new email template.
 *
 * Replaces the monolith's hardcoded template approach (LogTemplate,
 * InvoiceTemplate constants in MailService.cs) with dynamic template
 * management. Templates support {{tag}} variable placeholders for
 * subject and body content.
 *
 * On success, invalidates the email templates list cache.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useCreateEmailTemplate();
 * await mutateAsync({
 *   name: 'Log Notification',
 *   subject: 'Log: {{type}} from {{source}}',
 *   htmlContent: '<p>Host: {{host}}</p><p>Message: {{message}}</p>',
 *   variables: ['type', 'source', 'host', 'message', 'details'],
 * });
 * ```
 */
export function useCreateEmailTemplate() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      request: CreateEmailTemplateRequest,
    ): Promise<ApiResponse<EmailTemplate>> => {
      const response = await post<EmailTemplate>(
        '/notifications/templates',
        request,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to create email template';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: emailTemplateKeys.all });
    },
  });
}

/**
 * Updates an existing email template.
 *
 * Supports partial updates — only the provided fields are modified.
 * On success, invalidates the email templates list cache.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useUpdateEmailTemplate();
 * await mutateAsync({
 *   id: 'template-uuid',
 *   data: {
 *     subject: 'Updated Subject: {{type}}',
 *     htmlContent: '<p>Updated content: {{message}}</p>',
 *   },
 * });
 * ```
 */
export function useUpdateEmailTemplate() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (params: {
      /** The email template GUID identifier. */
      id: string;
      /** The template fields to update. */
      data: UpdateEmailTemplateRequest;
    }): Promise<ApiResponse<EmailTemplate>> => {
      const response = await put<EmailTemplate>(
        `/notifications/templates/${params.id}`,
        params.data,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to update email template';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: emailTemplateKeys.all });
    },
  });
}

/**
 * Deletes an email template by ID.
 *
 * Permanently removes the template from the Notifications service.
 * On success, invalidates the email templates list cache so the
 * deleted template no longer appears.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, reset } =
 *   useDeleteEmailTemplate();
 * await mutateAsync('template-uuid');
 * ```
 */
export function useDeleteEmailTemplate() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      id: string,
    ): Promise<ApiResponse<BaseResponseModel>> => {
      const response = await del<BaseResponseModel>(
        `/notifications/templates/${id}`,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to delete email template';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: emailTemplateKeys.all });
    },
  });
}

/**
 * Updates an SMTP server configuration.
 *
 * Replaces the monolith's smtp_service entity record updates. Server-side
 * validation (from SmtpInternalService.ValidatePreUpdateRecord) enforces:
 * - Unique configuration names
 * - Port range: 1–65025
 * - max_retries_count: 1–10
 * - retry_wait_minutes: 1–1440
 * - Valid email format for defaultFromEmail and defaultReplyToEmail
 * - Default service invariant: setting isDefault=true clears it on others
 *   (HandleDefaultServiceSetup logic)
 *
 * Supports partial updates — only the provided fields are modified.
 * On success, invalidates the SMTP configs list cache.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useUpdateSmtpConfig();
 * await mutateAsync({
 *   id: 'config-uuid',
 *   data: {
 *     server: 'smtp.example.com',
 *     port: 587,
 *     connectionSecurity: 'starttls',
 *     isDefault: true,
 *   },
 * });
 * ```
 */
export function useUpdateSmtpConfig() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (params: {
      /** The SMTP configuration GUID identifier. */
      id: string;
      /** The configuration fields to update. */
      data: UpdateSmtpConfigRequest;
    }): Promise<ApiResponse<SmtpConfig>> => {
      const response = await put<SmtpConfig>(
        `/notifications/smtp-configs/${params.id}`,
        params.data,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to update SMTP configuration';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: smtpConfigKeys.all });
    },
  });
}

/**
 * Marks a notification as read.
 *
 * Sets the notification status to 'read' and records the readAt timestamp.
 * Replaces the monolith's in-app notification acknowledgement pattern
 * (PostgreSQL record update + NOTIFY channel broadcast).
 *
 * On success, invalidates the notifications list cache to update
 * unread counts and status indicators across the UI.
 *
 * @returns TanStack Query mutation with mutate, mutateAsync, isPending,
 *          isError, error, isSuccess, data, and reset.
 *
 * @example
 * ```tsx
 * const { mutateAsync, isPending, isError, error, isSuccess, data, reset } =
 *   useMarkNotificationRead();
 * await mutateAsync('notification-uuid');
 * ```
 */
export function useMarkNotificationRead() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (
      id: string,
    ): Promise<ApiResponse<BaseResponseModel>> => {
      const response = await patch<BaseResponseModel>(
        `/notifications/${id}/read`,
      );
      if (!response.success) {
        const errorMessage =
          response.message ||
          (response.errors?.length > 0 ? response.errors[0].message : '') ||
          'Failed to mark notification as read';
        throw new Error(errorMessage);
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}
