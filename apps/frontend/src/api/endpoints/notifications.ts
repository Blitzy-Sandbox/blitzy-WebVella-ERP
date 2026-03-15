/**
 * Email / Notification Operations API Module
 *
 * Provides typed API functions for the Notifications bounded-context service.
 * Replaces the monolith's Mail plugin services (SmtpInternalService, EmailServiceManager,
 * ProcessSmtpQueueJob) and the PostgreSQL LISTEN/NOTIFY notification subsystem with
 * HTTP API calls through API Gateway to the Notifications Lambda handlers.
 *
 * The target architecture uses:
 * - SES (stubbed for third-party) for outbound email delivery
 * - SQS for email queue processing (replaces ProcessSmtpQueueJob's 10-minute poll cycle)
 * - SNS for event-driven in-app notifications (replaces PostgreSQL LISTEN/NOTIFY)
 * - DynamoDB for notification and email metadata persistence
 *
 * Server response envelopes follow these typed shapes:
 * - {@link ApiResponse}<{@link EntityRecord}[]> — Email/notification list operations
 * - {@link ApiResponse}<{@link EntityRecord}> — Single email/SMTP/notification record
 * - {@link ApiResponse}<null> — Mutation acknowledgements (delete, mark-read)
 *
 * All notification endpoints require authenticated access.
 * JWT authorizer validates role claims at the API Gateway level.
 * Error handling is delegated to the centralized client.ts interceptor.
 *
 * @module api/endpoints/notifications
 */

import { get, post, put, del } from '../client';
import type { ApiResponse } from '../client';
import type { EntityRecord } from '../../types/record';

// ─── Base Path Constants ───────────────────────────────────────────────────────

/**
 * Base path for email management endpoints.
 * Maps from monolith's Mail plugin routes (email entity CRUD + queue operations).
 */
const EMAIL_BASE = '/notifications/emails';

/**
 * Base path for SMTP service configuration endpoints.
 * Maps from monolith's smtp_service entity CRUD in SmtpInternalService.
 */
const SMTP_SERVICE_BASE = '/notifications/smtp-services';

/**
 * Base path for in-app notification endpoints.
 * Replaces the monolith's PostgreSQL LISTEN/NOTIFY subsystem
 * (NotificationContext, ErpRecordChangeNotification) with SNS/SQS-backed notifications.
 */
const IN_APP_BASE = '/notifications/in-app';

// ─── Parameter Interfaces ──────────────────────────────────────────────────────

/**
 * Query parameters for filtering the email list.
 *
 * Maps to the monolith's EQL query in ProcessSmtpQueue which filters by status
 * and scheduled_on. Extends filtering to support date ranges and search.
 *
 * The `status` field corresponds to the monolith's EmailStatus enum:
 * - `queued` — Email awaiting queue processing (maps to EmailStatus.Pending)
 * - `sent` — Successfully delivered (maps to EmailStatus.Sent)
 * - `failed` — Delivery failed and retries exhausted (maps to EmailStatus.Aborted)
 * - `draft` — Created but not yet scheduled for sending
 */
export interface EmailListParams {
  /** Filter by email delivery status. */
  status?: 'queued' | 'sent' | 'failed' | 'draft';
  /** Page number for pagination (1-based). */
  page?: number;
  /** Number of records per page. */
  pageSize?: number;
  /** ISO 8601 date string for filtering emails created on or after this date. */
  fromDate?: string;
  /** ISO 8601 date string for filtering emails created on or before this date. */
  toDate?: string;
  /** Free-text search term matching subject, recipients, or body content. */
  search?: string;
}

/**
 * Parameters for composing and sending a new email.
 *
 * Maps to the monolith's SmtpInternalService.TestSmtpServiceOnPost flow:
 * - `serviceId` identifies the SMTP service configuration to use
 * - `to`, `cc`, `bcc` map to the Email.Recipients list with address prefix conventions
 *   (monolith uses "cc:" and "bcc:" prefixes in address field)
 * - `subject` and `body` are required (validated in monolith)
 * - `isHtml` controls whether body is sent as HTML (with ProcessHtmlContent processing)
 *   or plain text
 *
 * The email is queued via SQS for async delivery by the QueueProcessor Lambda,
 * replacing the monolith's in-process SmtpClient send.
 */
export interface SendEmailParams {
  /** GUID identifying the SMTP service configuration to route through. */
  serviceId: string;
  /** Comma-separated recipient email addresses. */
  to: string;
  /** Optional comma-separated CC recipient email addresses. */
  cc?: string;
  /** Optional comma-separated BCC recipient email addresses. */
  bcc?: string;
  /** Email subject line (required, validated server-side). */
  subject: string;
  /** Email body content (plain text or HTML based on isHtml flag). */
  body: string;
  /** When true, body is treated as HTML and processed for inline images. Defaults to false. */
  isHtml?: boolean;
}

/**
 * Parameters for creating or updating an SMTP service configuration.
 *
 * Maps to the monolith's smtp_service entity fields created in MailPlugin.20190215.cs.
 * Server-side validation mirrors SmtpInternalService.ValidatePreCreateRecord:
 * - `name` must be unique across all SMTP services
 * - `port` must be between 1 and 65025
 * - `defaultFromEmail` must be a valid email address
 * - `maxRetriesCount` must be between 1 and 10
 * - `retryWaitMinutes` must be between 1 and 1440
 */
export interface SmtpServiceParams {
  /** Display name for the SMTP service (must be unique). */
  name: string;
  /** SMTP server hostname or IP address. */
  host: string;
  /** SMTP server port (1–65025). */
  port: number;
  /** Optional SMTP authentication username. */
  username?: string;
  /** Optional SMTP authentication password. */
  password?: string;
  /** Whether to use SSL/TLS for the SMTP connection. */
  useSsl: boolean;
  /** Default sender email address (must be a valid email). */
  defaultFromEmail: string;
  /** Optional default sender display name. */
  defaultFromName?: string;
  /** Maximum number of send retries on failure (1–10). */
  maxRetriesCount?: number;
  /** Minutes to wait between retry attempts (1–1440). */
  retryWaitMinutes?: number;
}

/**
 * Query parameters for filtering the in-app notification list.
 *
 * In-app notifications replace the monolith's PostgreSQL LISTEN/NOTIFY
 * (NotificationContext, ErpRecordChangeNotification) with SNS/SQS-backed
 * notification records stored in DynamoDB.
 */
export interface NotificationListParams {
  /** Page number for pagination (1-based). */
  page?: number;
  /** Number of records per page. */
  pageSize?: number;
  /** Filter by read status: true for read only, false for unread only. */
  isRead?: boolean;
}

// ─── Email Operations ──────────────────────────────────────────────────────────

/**
 * Retrieves a paginated, filterable list of email records.
 *
 * Maps to the monolith's EQL queries used by the Mail plugin to list emails:
 * `SELECT * FROM email WHERE status = @status AND scheduled_on ...`.
 * Supports filtering by delivery status, date range, and free-text search
 * across the x_search composite field (subject, recipients, body).
 *
 * Each record in the response uses the {@link EntityRecord} shape,
 * preserving the dynamic key-value pattern from the monolith's email entity
 * (fields: id, subject, status, sent_on, service_id, recipients, sender, etc.).
 *
 * The caller inspects {@link ApiResponse.success}, {@link ApiResponse.object},
 * {@link ApiResponse.errors}, and {@link ApiResponse.message} on the envelope.
 *
 * @param params - Optional filters and pagination parameters.
 * @returns A promise resolving to the email record list.
 */
export function listEmails(
  params?: EmailListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  const query: Record<string, string> = {};

  if (params) {
    if (params.status !== undefined) {
      query.status = params.status;
    }
    if (params.page !== undefined) {
      query.page = String(params.page);
    }
    if (params.pageSize !== undefined) {
      query.pageSize = String(params.pageSize);
    }
    if (params.fromDate !== undefined) {
      query.fromDate = params.fromDate;
    }
    if (params.toDate !== undefined) {
      query.toDate = params.toDate;
    }
    if (params.search !== undefined) {
      query.search = params.search;
    }
  }

  return get<EntityRecord[]>(
    EMAIL_BASE,
    Object.keys(query).length > 0 ? query : undefined,
  );
}

/**
 * Retrieves a single email record by its unique identifier.
 *
 * Maps to the monolith's SmtpInternalService.GetEmail(Guid id) which executes:
 * `SELECT * FROM email WHERE id = @id`.
 *
 * The returned {@link EntityRecord} includes all email entity fields:
 * id, subject, body, status, sent_on, service_id, sender, recipients,
 * reply_to_email, attachments, retries_count, server_error, scheduled_on, etc.
 *
 * @param emailId - GUID string identifying the email record.
 * @returns A promise resolving to the matched email record.
 */
export function getEmail(
  emailId: string,
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(
    `${EMAIL_BASE}/${encodeURIComponent(emailId)}`,
  );
}

/**
 * Queues a new email for delivery through the specified SMTP service.
 *
 * Replaces the monolith's SmtpInternalService.TestSmtpServiceOnPost and
 * direct SmtpClient send flow. In the target architecture, the email is
 * persisted with status "queued" and an SQS message triggers the
 * QueueProcessor Lambda for async delivery.
 *
 * Server-side validation (mirroring the monolith):
 * - `serviceId` must reference an existing, enabled SMTP service
 * - `to` must contain at least one valid email address
 * - `subject` is required and non-empty
 * - `body` is required and non-empty
 *
 * @param params - Email composition parameters.
 * @returns A promise resolving to the created email record.
 */
export function sendEmail(
  params: SendEmailParams,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(
    `${EMAIL_BASE}/send`,
    params,
  );
}

/**
 * Retries delivery of a previously failed email.
 *
 * Maps to the monolith's SmtpInternalService.EmailSendNowOnPost which
 * retrieves the email by ID, looks up the associated SMTP service, and
 * invokes SendEmail. In the target architecture, a new SQS message is
 * enqueued for the QueueProcessor Lambda to re-attempt delivery.
 *
 * The monolith's retry logic tracks retries_count against max_retries_count
 * and sets status to "aborted" when retries are exhausted.
 *
 * @param emailId - GUID string identifying the email to retry.
 * @returns A promise resolving to the updated email record with new status.
 */
export function retryEmail(
  emailId: string,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(
    `${EMAIL_BASE}/${encodeURIComponent(emailId)}/retry`,
    {},
  );
}

/**
 * Deletes an email record permanently.
 *
 * Removes the email metadata from DynamoDB. This is irreversible.
 * Corresponds to a standard RecordManager.DeleteRecord("email", ...) in the monolith.
 *
 * @param emailId - GUID string identifying the email to delete.
 * @returns A promise resolving to a success/error acknowledgement.
 */
export function deleteEmail(
  emailId: string,
): Promise<ApiResponse<null>> {
  return del<null>(
    `${EMAIL_BASE}/${encodeURIComponent(emailId)}`,
  );
}

// ─── SMTP Service Configuration ────────────────────────────────────────────────

/**
 * Retrieves all SMTP service configurations.
 *
 * Maps to the monolith's EQL query: `SELECT * FROM smtp_service`.
 * Each record follows the smtp_service entity shape created in MailPlugin.20190215.cs:
 * id, name, host, port, username, is_enabled, is_default, default_from_email,
 * default_from_name, max_retries_count, retry_wait_minutes, connection_security, etc.
 *
 * @returns A promise resolving to the list of SMTP service records.
 */
export function listSmtpServices(): Promise<ApiResponse<EntityRecord[]>> {
  return get<EntityRecord[]>(SMTP_SERVICE_BASE);
}

/**
 * Retrieves a single SMTP service configuration by its unique identifier.
 *
 * Maps to the monolith's EmailServiceManager.GetSmtpService(Guid id) which
 * loads the smtp_service entity record by ID.
 *
 * @param serviceId - GUID string identifying the SMTP service.
 * @returns A promise resolving to the matched SMTP service record.
 */
export function getSmtpService(
  serviceId: string,
): Promise<ApiResponse<EntityRecord>> {
  return get<EntityRecord>(
    `${SMTP_SERVICE_BASE}/${encodeURIComponent(serviceId)}`,
  );
}

/**
 * Creates a new SMTP service configuration.
 *
 * Server-side validation mirrors SmtpInternalService.ValidatePreCreateRecord:
 * - `name` uniqueness check (EQL: SELECT * FROM smtp_service WHERE name = @name)
 * - `port` range validation (1–65025)
 * - `defaultFromEmail` email format validation
 * - `maxRetriesCount` range validation (1–10)
 * - `retryWaitMinutes` range validation (1–1440)
 * - Default service flag management (HandleDefaultServiceSetup)
 *
 * @param service - SMTP service configuration parameters.
 * @returns A promise resolving to the created SMTP service record.
 */
export function createSmtpService(
  service: SmtpServiceParams,
): Promise<ApiResponse<EntityRecord>> {
  return post<EntityRecord>(SMTP_SERVICE_BASE, service);
}

/**
 * Updates an existing SMTP service configuration.
 *
 * Server-side validation mirrors SmtpInternalService.ValidatePreUpdateRecord:
 * - `name` uniqueness check (excluding the current record by ID)
 * - `port` range validation (1–65025)
 * - `defaultFromEmail` email format validation
 * - `maxRetriesCount` range validation (1–10)
 * - `retryWaitMinutes` range validation (1–1440)
 * - Default service flag management (cannot unset is_default if it's the only default)
 *
 * Accepts a partial subset of {@link SmtpServiceParams} — only submitted fields
 * are updated.
 *
 * @param serviceId - GUID string identifying the SMTP service to update.
 * @param service   - Partial SMTP service configuration fields to update.
 * @returns A promise resolving to the updated SMTP service record.
 */
export function updateSmtpService(
  serviceId: string,
  service: Partial<SmtpServiceParams>,
): Promise<ApiResponse<EntityRecord>> {
  return put<EntityRecord>(
    `${SMTP_SERVICE_BASE}/${encodeURIComponent(serviceId)}`,
    service,
  );
}

/**
 * Deletes an SMTP service configuration.
 *
 * Removes the smtp_service record from DynamoDB. This is irreversible.
 * The server validates that deleting this service does not orphan queued emails.
 *
 * @param serviceId - GUID string identifying the SMTP service to delete.
 * @returns A promise resolving to a success/error acknowledgement.
 */
export function deleteSmtpService(
  serviceId: string,
): Promise<ApiResponse<null>> {
  return del<null>(
    `${SMTP_SERVICE_BASE}/${encodeURIComponent(serviceId)}`,
  );
}

// ─── In-App Notifications ──────────────────────────────────────────────────────

/**
 * Retrieves a paginated list of in-app notifications for the current user.
 *
 * Replaces the monolith's PostgreSQL LISTEN/NOTIFY subsystem:
 * - NotificationContext registered listeners via reflection discovery
 * - ErpRecordChangeNotification carried change payloads over SQL channels
 *
 * In the target architecture, domain events published to SNS create
 * notification records in DynamoDB, and this endpoint queries them.
 *
 * Each {@link EntityRecord} in the response contains notification fields:
 * id, channel, message, is_read, created_on, user_id, etc.
 *
 * @param params - Optional filters and pagination parameters.
 * @returns A promise resolving to the notification record list.
 */
export function listNotifications(
  params?: NotificationListParams,
): Promise<ApiResponse<EntityRecord[]>> {
  const query: Record<string, string> = {};

  if (params) {
    if (params.page !== undefined) {
      query.page = String(params.page);
    }
    if (params.pageSize !== undefined) {
      query.pageSize = String(params.pageSize);
    }
    if (params.isRead !== undefined) {
      query.isRead = String(params.isRead);
    }
  }

  return get<EntityRecord[]>(
    IN_APP_BASE,
    Object.keys(query).length > 0 ? query : undefined,
  );
}

/**
 * Marks a single in-app notification as read.
 *
 * Updates the notification record's `is_read` flag to `true` in DynamoDB.
 * This is an idempotent operation — marking an already-read notification
 * as read again is a no-op that returns success.
 *
 * @param notificationId - GUID string identifying the notification to mark read.
 * @returns A promise resolving to a success/error acknowledgement.
 */
export function markNotificationRead(
  notificationId: string,
): Promise<ApiResponse<null>> {
  return post<null>(
    `${IN_APP_BASE}/${encodeURIComponent(notificationId)}/read`,
    {},
  );
}

/**
 * Marks all in-app notifications as read for the current user.
 *
 * Performs a bulk update on all unread notification records belonging to
 * the authenticated user. This is an idempotent operation.
 *
 * @returns A promise resolving to a success/error acknowledgement.
 */
export function markAllNotificationsRead(): Promise<ApiResponse<null>> {
  return post<null>(
    `${IN_APP_BASE}/read-all`,
    {},
  );
}
