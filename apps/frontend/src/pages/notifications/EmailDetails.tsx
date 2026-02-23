/**
 * EmailDetails.tsx — Email Detail View Page
 *
 * React page component for viewing full email details including content,
 * sender/recipients, attachments, and SMTP service information.
 * Replaces the monolith's email details page defined in MailPlugin.20190422.cs.
 *
 * Route: /notifications/emails/:emailId
 */
import React, { useState, useMemo, useCallback } from 'react';
import {
  useQuery,
  useMutation,
  useQueryClient,
} from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';
import DOMPurify from 'dompurify';

import apiClient, { get, post } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import Modal from '../../components/common/Modal';
import TabNav from '../../components/common/TabNav';
import type { TabConfig } from '../../components/common/TabNav';

/* ─────────────────────────────────────────────
 * Type Definitions
 * (Derived from Email.cs, EmailAddress.cs, SmtpService.cs)
 * ───────────────────────────────────────────── */

/** Mirrors MailPlugin Api/EmailAddress.cs JSON shape */
interface EmailAddress {
  name: string;
  address: string;
}

/** Email status values from MailPlugin.20190215.cs (select field) */
type EmailStatus = 'pending' | 'sent' | 'aborted';

/** Email priority values from MailPlugin.20190215.cs (select field) */
type EmailPriority = 'low' | 'normal' | 'high';

/** Mirrors MailPlugin Api/Email.cs with JSON field shapes */
interface EmailRecord {
  id: string;
  service_id: string;
  sender: string;
  recipients: string;
  reply_to_email: string;
  subject: string;
  content_text: string;
  content_html: string;
  created_on: string;
  sent_on: string | null;
  status: EmailStatus;
  priority: EmailPriority;
  server_error: string | null;
  scheduled_on: string | null;
  retries_count: number;
  x_search: string;
  sender_name: string;
  recipient_name: string;
  attachments: string | null;
}

/** SMTP service name resolution result (only name needed) */
interface SmtpServiceRecord {
  id: string;
  name: string;
}

/* ─────────────────────────────────────────────
 * Constants
 * ───────────────────────────────────────────── */

const EMAILS_BASE = '/v1/notifications/emails';
const SMTP_BASE = '/v1/notifications/smtp-services';

const STATUS_LABELS: Record<EmailStatus, string> = {
  pending: 'Pending',
  sent: 'Sent',
  aborted: 'Aborted',
};

const STATUS_COLORS: Record<EmailStatus, string> = {
  pending:
    'bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-200',
  sent: 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200',
  aborted: 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200',
};

const PRIORITY_LABELS: Record<EmailPriority, string> = {
  low: 'Low',
  normal: 'Normal',
  high: 'High',
};

const PRIORITY_COLORS: Record<EmailPriority, string> = {
  low: 'text-slate-500',
  normal: 'text-blue-600',
  high: 'text-red-600 font-semibold',
};

/* ─────────────────────────────────────────────
 * JSON Field Parsing Utilities
 *
 * Replicates the ICodeVariable logic from MailPlugin.20190422.cs:
 * - Sender: JsonConvert.DeserializeObject<EmailAddress>(record["sender"]).Address
 * - Recipients: JsonConvert.DeserializeObject<List<EmailAddress>>(record["recipients"])
 *              .Select(x => x.Address).Join(";")
 * - Attachments: JSON.parse of string array
 * ───────────────────────────────────────────── */

/**
 * Parses the JSON sender field and extracts the email address.
 * Matches: JsonConvert.DeserializeObject<EmailAddress>(record["sender"]).Address
 */
function parseSender(senderJson: string | null | undefined): string {
  if (!senderJson) return 'Unknown sender';
  try {
    const sender: EmailAddress = JSON.parse(senderJson);
    return sender.address || 'Unknown sender';
  } catch {
    return 'Unknown sender';
  }
}

/**
 * Parses the JSON sender field and extracts the display name.
 */
function parseSenderName(senderJson: string | null | undefined): string {
  if (!senderJson) return '';
  try {
    const sender: EmailAddress = JSON.parse(senderJson);
    return sender.name || '';
  } catch {
    return '';
  }
}

/**
 * Parses the JSON recipients field and joins addresses with semicolons.
 * Matches: recipients.Select(x => x.Address).Join(";")
 */
function parseRecipients(
  recipientsJson: string | null | undefined
): string {
  if (!recipientsJson) return 'Unknown recipients';
  try {
    const recipients: EmailAddress[] = JSON.parse(recipientsJson);
    if (!Array.isArray(recipients) || recipients.length === 0) {
      return 'Unknown recipients';
    }
    return recipients.map((r) => r.address).join('; ');
  } catch {
    return 'Unknown recipients';
  }
}

/**
 * Parses the JSON attachments field into an array of attachment path strings.
 */
function parseAttachments(
  attachmentsJson: string | null | undefined
): string[] {
  if (!attachmentsJson) return [];
  try {
    const attachments = JSON.parse(attachmentsJson);
    if (!Array.isArray(attachments)) return [];
    return attachments.filter(
      (a): a is string => typeof a === 'string' && a.length > 0
    );
  } catch {
    return [];
  }
}

/**
 * Formats a date string for display using the monolith's yyyy-MMM-dd HH:mm pattern.
 * Returns an em dash for null/undefined/empty values.
 */
function formatDateTime(dateStr: string | null | undefined): string {
  if (!dateStr) return '—';
  try {
    const date = new Date(dateStr);
    if (isNaN(date.getTime())) return '—';

    const months = [
      'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
      'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
    ];
    const year = date.getFullYear();
    const month = months[date.getMonth()];
    const day = String(date.getDate()).padStart(2, '0');
    const hours = String(date.getHours()).padStart(2, '0');
    const minutes = String(date.getMinutes()).padStart(2, '0');

    return `${year}-${month}-${day} ${hours}:${minutes}`;
  } catch {
    return '—';
  }
}

/**
 * Extracts the filename from a full path string for display.
 */
function getFilename(path: string): string {
  const parts = path.split(/[/\\]/);
  return parts[parts.length - 1] || path;
}

/* ─────────────────────────────────────────────
 * Data-Fetching Hooks
 * ───────────────────────────────────────────── */

/**
 * Fetches a single email by ID from GET /v1/notifications/emails/:emailId.
 * Uses the default apiClient instance directly for this primary data fetch.
 */
function useEmailDetail(emailId: string | undefined) {
  return useQuery<ApiResponse<EmailRecord>>({
    queryKey: ['email', emailId],
    queryFn: async () => {
      const response = await apiClient.get<ApiResponse<EmailRecord>>(
        `${EMAILS_BASE}/${emailId}`
      );
      return response.data;
    },
    enabled: !!emailId,
  });
}

/**
 * Fetches the SMTP service by ID and selects only the name property.
 * Replicates EmailServiceManager.GetSmtpService(serviceId).Name
 * with "SMTP service not found" fallback.
 */
function useSmtpServiceName(serviceId: string | undefined) {
  return useQuery<ApiResponse<SmtpServiceRecord>, Error, string>({
    queryKey: ['smtp-service', serviceId],
    queryFn: () =>
      get<SmtpServiceRecord>(`${SMTP_BASE}/${serviceId}`),
    enabled: !!serviceId,
    select: (data) => {
      if (data.success && data.object) {
        return data.object.name || 'SMTP service not found';
      }
      return 'SMTP service not found';
    },
  });
}

/* ─────────────────────────────────────────────
 * Sub-Components
 * ───────────────────────────────────────────── */

/** Display-only field row matching PcFieldText mode=2 (display) */
function DetailField({
  label,
  value,
  className = '',
  isError = false,
}: {
  label: string;
  value: React.ReactNode;
  className?: string;
  isError?: boolean;
}) {
  return (
    <div className={`py-3 ${className}`}>
      <dt className="text-sm font-medium text-slate-500 dark:text-slate-400">
        {label}
      </dt>
      <dd
        className={`mt-1 text-sm ${
          isError
            ? 'text-red-600 dark:text-red-400 font-mono'
            : 'text-slate-900 dark:text-slate-100'
        }`}
      >
        {value || '—'}
      </dd>
    </div>
  );
}

/** Status badge component for email status display */
function StatusBadge({ status }: { status: EmailStatus }) {
  const label = STATUS_LABELS[status] || status;
  const colorClass = STATUS_COLORS[status] || STATUS_COLORS.pending;

  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${colorClass}`}
    >
      {label}
    </span>
  );
}

/** Priority indicator matching the monolith's priority display */
function PriorityIndicator({ priority }: { priority: EmailPriority }) {
  const label = PRIORITY_LABELS[priority] || priority;
  const colorClass = PRIORITY_COLORS[priority] || PRIORITY_COLORS.normal;

  return <span className={`text-sm ${colorClass}`}>{label}</span>;
}

/* ─────────────────────────────────────────────
 * Main Component
 * ───────────────────────────────────────────── */

/**
 * EmailDetails — Email Detail View Page
 *
 * Route: /notifications/emails/:emailId
 * Fetches email by ID from Notifications service API.
 * Fetches SMTP service name by service_id (replicating ICodeVariable from Patch20190422).
 * Provides "Send Now" action (replacing SmtpInternalService.EmailSendNowOnPost()).
 */
function EmailDetails(): React.JSX.Element {
  const { emailId } = useParams<{ emailId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ── Local state ── */
  const [activeTabId, setActiveTabId] = useState<string>('html');
  const [isSendConfirmOpen, setIsSendConfirmOpen] = useState(false);

  /* ── Data queries ── */
  const {
    data: emailResponse,
    isLoading: isEmailLoading,
    isError: isEmailError,
    error: emailError,
  } = useEmailDetail(emailId);

  const email = emailResponse?.success ? emailResponse.object ?? null : null;

  /* Extract API-level errors and message for non-success responses */
  const apiErrors = emailResponse && !emailResponse.success
    ? emailResponse.errors
    : undefined;
  const apiMessage = emailResponse && !emailResponse.success
    ? emailResponse.message
    : undefined;

  const {
    data: smtpServiceName,
    isLoading: isServiceLoading,
  } = useSmtpServiceName(email?.service_id);

  /* ── Send Now mutation ── */
  const sendNowMutation = useMutation<ApiResponse<void>, Error, string>({
    mutationFn: (id: string) =>
      post<void>(`${EMAILS_BASE}/${id}/send-now`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['email', emailId] });
      setIsSendConfirmOpen(false);
      // Post-redirect pattern: navigate to the same details page to refresh
      // (replaces the Razor Page PRG pattern from MailPlugin)
      navigate(`/notifications/emails/${emailId}`, { replace: true });
    },
  });

  /* ── Memoized parsed values (replicating ICodeVariable logic) ── */
  const senderAddress = useMemo(
    () => parseSender(email?.sender),
    [email?.sender]
  );

  const senderName = useMemo(
    () => parseSenderName(email?.sender),
    [email?.sender]
  );

  const recipientAddresses = useMemo(
    () => parseRecipients(email?.recipients),
    [email?.recipients]
  );

  const attachments = useMemo(
    () => parseAttachments(email?.attachments),
    [email?.attachments]
  );

  const sanitizedHtml = useMemo(() => {
    if (!email?.content_html) return '';
    return DOMPurify.sanitize(email.content_html, {
      ALLOWED_TAGS: [
        'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
        'p', 'br', 'hr', 'ul', 'ol', 'li',
        'strong', 'em', 'b', 'i', 'u', 'a',
        'img', 'table', 'thead', 'tbody', 'tr', 'td', 'th',
        'div', 'span', 'blockquote', 'pre', 'code',
        'style', 'center', 'font', 'sub', 'sup',
      ],
      ALLOWED_ATTR: [
        'href', 'src', 'alt', 'title', 'style', 'class',
        'width', 'height', 'border', 'cellpadding', 'cellspacing',
        'align', 'valign', 'bgcolor', 'color', 'face', 'size',
        'target', 'rel',
      ],
      ALLOW_DATA_ATTR: false,
    });
  }, [email?.content_html]);

  /* ── Callbacks ── */
  const handleOpenSendConfirm = useCallback(() => {
    setIsSendConfirmOpen(true);
  }, []);

  const handleCloseSendConfirm = useCallback(() => {
    setIsSendConfirmOpen(false);
  }, []);

  const handleConfirmSend = useCallback(async () => {
    if (!emailId) return;
    try {
      await sendNowMutation.mutateAsync(emailId);
    } catch {
      // Error is captured by mutation state — no additional handling needed
    }
  }, [emailId, sendNowMutation]);

  const handleTabChange = useCallback((tabId: string) => {
    setActiveTabId(tabId);
  }, []);

  /* ── Tab configuration for content section ── */
  const contentTabs: TabConfig[] = useMemo(
    () => [
      {
        id: 'html',
        label: 'HTML View',
        content: sanitizedHtml ? (
          <div
            className="prose prose-sm max-w-none dark:prose-invert overflow-auto rounded border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900"
            dangerouslySetInnerHTML={{ __html: sanitizedHtml }}
          />
        ) : (
          <p className="py-4 text-sm italic text-slate-400">
            No HTML content available.
          </p>
        ),
      },
      {
        id: 'plaintext',
        label: 'Plain Text',
        content: email?.content_text ? (
          <pre className="overflow-auto whitespace-pre-wrap break-words rounded border border-slate-200 bg-slate-50 p-4 text-sm text-slate-800 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200">
            {email.content_text}
          </pre>
        ) : (
          <p className="py-4 text-sm italic text-slate-400">
            No plain text content available.
          </p>
        ),
      },
    ],
    [sanitizedHtml, email?.content_text]
  );

  /* ── Loading state ── */
  if (isEmailLoading) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-slate-200 border-t-blue-600"
            role="status"
            aria-label="Loading email details"
          />
          <p className="text-sm text-slate-500">Loading email details…</p>
        </div>
      </div>
    );
  }

  /* ── Error state ── */
  if (isEmailError) {
    const errorMessage =
      emailError instanceof Error
        ? emailError.message
        : 'An unexpected error occurred while fetching the email.';

    return (
      <div className="mx-auto max-w-3xl px-4 py-8">
        <div className="rounded-lg border border-red-200 bg-red-50 p-6 dark:border-red-800 dark:bg-red-950">
          <h2 className="text-lg font-semibold text-red-800 dark:text-red-200">
            Error Loading Email
          </h2>
          <p className="mt-2 text-sm text-red-700 dark:text-red-300">
            {errorMessage}
          </p>
          <Link
            to="/notifications/emails"
            className="mt-4 inline-flex items-center text-sm font-medium text-red-600 hover:text-red-500 dark:text-red-400 dark:hover:text-red-300"
          >
            ← Back to email list
          </Link>
        </div>
      </div>
    );
  }

  /* ── Not-found state (also handles API non-success responses) ── */
  if (!email) {
    const notFoundMessage = apiMessage
      || 'The requested email could not be found. It may have been deleted or the ID is invalid.';

    return (
      <div className="mx-auto max-w-3xl px-4 py-8">
        <div className="rounded-lg border border-slate-200 bg-slate-50 p-6 dark:border-slate-700 dark:bg-slate-800">
          <h2 className="text-lg font-semibold text-slate-800 dark:text-slate-200">
            Email Not Found
          </h2>
          <p className="mt-2 text-sm text-slate-600 dark:text-slate-400">
            {notFoundMessage}
          </p>
          {apiErrors && apiErrors.length > 0 && (
            <ul className="mt-2 list-inside list-disc text-sm text-red-600 dark:text-red-400">
              {apiErrors.map((err, idx) => (
                <li key={`${err.key}-${idx}`}>{err.message}</li>
              ))}
            </ul>
          )}
          <Link
            to="/notifications/emails"
            className="mt-4 inline-flex items-center text-sm font-medium text-blue-600 hover:text-blue-500 dark:text-blue-400 dark:hover:text-blue-300"
          >
            ← Back to email list
          </Link>
        </div>
      </div>
    );
  }

  /* ── Derived display values ── */
  const canSendNow = email.status === 'pending';
  const serviceDisplayName =
    isServiceLoading
      ? 'Loading…'
      : smtpServiceName || 'SMTP service not found';

  /* ── Main render ── */
  return (
    <div className="mx-auto max-w-5xl px-4 py-6">
      {/* ── Header Section ── */}
      <header className="mb-6">
        {/* Back button */}
        <Link
          to="/notifications/emails"
          className="mb-4 inline-flex items-center gap-1 text-sm font-medium text-slate-600 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-200"
        >
          <svg
            className="h-4 w-4"
            viewBox="0 0 20 20"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              fillRule="evenodd"
              d="M17 10a.75.75 0 01-.75.75H5.612l4.158 3.96a.75.75 0 11-1.04 1.08l-5.5-5.25a.75.75 0 010-1.08l5.5-5.25a.75.75 0 111.04 1.08L5.612 9.25H16.25A.75.75 0 0117 10z"
              clipRule="evenodd"
            />
          </svg>
          Back to email list
        </Link>

        {/* Title row with status, priority, and action */}
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex-1">
            <h1 className="text-2xl font-bold text-slate-900 dark:text-slate-100">
              {email.subject || '(No subject)'}
            </h1>
            <div className="mt-2 flex flex-wrap items-center gap-3">
              <StatusBadge status={email.status} />
              <span className="text-sm text-slate-400">•</span>
              <PriorityIndicator priority={email.priority} />
            </div>
          </div>

          {/* Send Now action button (only for pending emails) */}
          {canSendNow && (
            <button
              type="button"
              onClick={handleOpenSendConfirm}
              disabled={sendNowMutation.isPending}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {sendNowMutation.isPending ? (
                <>
                  <div
                    className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                    role="status"
                    aria-label="Sending"
                  />
                  Sending…
                </>
              ) : (
                <>
                  <svg
                    className="h-4 w-4"
                    viewBox="0 0 20 20"
                    fill="currentColor"
                    aria-hidden="true"
                  >
                    <path d="M3.105 2.289a.75.75 0 00-.826.95l1.414 4.925A1.5 1.5 0 005.135 9.25h6.115a.75.75 0 010 1.5H5.135a1.5 1.5 0 00-1.442 1.086l-1.414 4.926a.75.75 0 00.826.95 28.896 28.896 0 0015.293-7.154.75.75 0 000-1.115A28.897 28.897 0 003.105 2.289z" />
                  </svg>
                  Send Now
                </>
              )}
            </button>
          )}
        </div>
      </header>

      {/* ── Send Now Mutation Error ── */}
      {sendNowMutation.isError && (
        <div
          className="mb-6 rounded-lg border border-red-200 bg-red-50 p-4 dark:border-red-800 dark:bg-red-950"
          role="alert"
        >
          <p className="text-sm font-medium text-red-800 dark:text-red-200">
            Failed to send email
          </p>
          <p className="mt-1 text-sm text-red-700 dark:text-red-300">
            {sendNowMutation.error instanceof Error
              ? sendNowMutation.error.message
              : 'An unexpected error occurred while sending the email.'}
          </p>
        </div>
      )}

      {/* ── Metadata Row (matching Patch20190422 column layout) ── */}
      <section
        className="mb-6 grid grid-cols-1 gap-6 rounded-lg border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-700 dark:bg-slate-800 md:grid-cols-2"
        aria-label="Email metadata"
      >
        {/* Column 1 — Service + Sender (matching Patch20190422 column1) */}
        <div className="space-y-4">
          <DetailField
            label="Service"
            value={serviceDisplayName}
          />
          <DetailField
            label="Sender"
            value={
              senderName ? (
                <span>
                  {senderName}{' '}
                  <span className="text-slate-500 dark:text-slate-400">
                    &lt;{senderAddress}&gt;
                  </span>
                </span>
              ) : (
                senderAddress
              )
            }
          />
        </div>

        {/* Column 2 — Recipients (matching Patch20190422 column2) */}
        <div className="space-y-4">
          <DetailField
            label="Recipient(s)"
            value={recipientAddresses}
          />
          <DetailField
            label="Reply-To Email"
            value={email.reply_to_email}
          />
        </div>
      </section>

      {/* ── Detail Fields ── */}
      <section
        className="mb-6 rounded-lg border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-700 dark:bg-slate-800"
        aria-label="Email details"
      >
        <h2 className="mb-4 text-lg font-semibold text-slate-900 dark:text-slate-100">
          Details
        </h2>
        <dl className="grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-2 lg:grid-cols-3">
          <DetailField
            label="Created On"
            value={formatDateTime(email.created_on)}
          />
          <DetailField
            label="Sent On"
            value={formatDateTime(email.sent_on)}
          />
          <DetailField
            label="Scheduled On"
            value={formatDateTime(email.scheduled_on)}
          />
          <DetailField
            label="Retries Count"
            value={String(email.retries_count ?? 0)}
          />
          <DetailField
            label="Priority"
            value={<PriorityIndicator priority={email.priority} />}
          />
          <DetailField
            label="Status"
            value={<StatusBadge status={email.status} />}
          />
        </dl>

        {/* Server Error (conditionally shown with error styling) */}
        {email.server_error && (
          <div className="mt-4 border-t border-slate-200 pt-4 dark:border-slate-700">
            <DetailField
              label="Server Error"
              value={email.server_error}
              isError
            />
          </div>
        )}
      </section>

      {/* ── Attachments Section ── */}
      {attachments.length > 0 && (
        <section
          className="mb-6 rounded-lg border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-700 dark:bg-slate-800"
          aria-label="Email attachments"
        >
          <h2 className="mb-4 text-lg font-semibold text-slate-900 dark:text-slate-100">
            Attachments
            <span className="ml-2 text-sm font-normal text-slate-500">
              ({attachments.length})
            </span>
          </h2>
          <ul className="divide-y divide-slate-200 dark:divide-slate-700">
            {attachments.map((attachment, index) => (
              <li
                key={`${attachment}-${index}`}
                className="flex items-center gap-3 py-3"
              >
                <svg
                  className="h-5 w-5 shrink-0 text-slate-400"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path
                    fillRule="evenodd"
                    d="M15.621 4.379a3 3 0 00-4.242 0l-7 7a3 3 0 004.241 4.243h.001l.497-.5a.75.75 0 011.064 1.057l-.498.501-.002.002a4.5 4.5 0 01-6.364-6.364l7-7a4.5 4.5 0 016.368 6.36l-3.455 3.553A2.625 2.625 0 119.52 9.52l3.45-3.451a.75.75 0 111.061 1.06l-3.45 3.451a1.125 1.125 0 001.587 1.595l3.454-3.553a3 3 0 000-4.242z"
                    clipRule="evenodd"
                  />
                </svg>
                <span className="truncate text-sm text-slate-700 dark:text-slate-300">
                  {getFilename(attachment)}
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {/* ── Content Section (HTML / Plain Text toggle) ── */}
      <section
        className="rounded-lg border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-700 dark:bg-slate-800"
        aria-label="Email content"
      >
        <h2 className="mb-4 text-lg font-semibold text-slate-900 dark:text-slate-100">
          Content
        </h2>
        <TabNav
          tabs={contentTabs}
          activeTabId={activeTabId}
          onTabChange={handleTabChange}
        />
      </section>

      {/* ── Send Now Confirmation Modal ── */}
      <Modal
        isVisible={isSendConfirmOpen}
        title="Confirm Send Now"
        onClose={handleCloseSendConfirm}
        footer={
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={handleCloseSendConfirm}
              disabled={sendNowMutation.isPending}
              className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-slate-400 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-600 dark:bg-slate-700 dark:text-slate-200 dark:hover:bg-slate-600"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmSend}
              disabled={sendNowMutation.isPending}
              className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {sendNowMutation.isPending ? (
                <>
                  <div
                    className="h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                    role="status"
                    aria-label="Sending"
                  />
                  Sending…
                </>
              ) : (
                'Confirm Send'
              )}
            </button>
          </div>
        }
      >
        <p className="text-sm text-slate-600 dark:text-slate-300">
          Are you sure you want to send this email now?
        </p>
        <div className="mt-3 rounded-md bg-slate-50 p-3 dark:bg-slate-900">
          <p className="text-sm font-medium text-slate-800 dark:text-slate-200">
            {email.subject || '(No subject)'}
          </p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
            To: {recipientAddresses}
          </p>
        </div>
        <p className="mt-3 text-xs text-slate-500 dark:text-slate-400">
          This will immediately attempt to deliver the email via the configured
          SMTP service. This action cannot be undone.
        </p>
      </Modal>
    </div>
  );
}

export default EmailDetails;
