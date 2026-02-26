/**
 * EmailCompose.tsx — Email Composition Form
 *
 * React page component for composing and sending emails. Replaces the
 * monolith's Mail plugin email entity create workflow defined in
 * MailPlugin.20190215.cs (entity/page creation) and the send/queue
 * logic from SmtpService.cs / SmtpInternalService.cs.
 *
 * Supported email entity fields (from MailPlugin patches):
 *   - service_id   — SMTP service selector (required)
 *   - sender       — JSON {name, address}, auto-populated from SMTP defaults
 *   - recipients   — JSON [{name, address}], tag-style multi-value input
 *   - cc/bcc       — optional secondary recipient lists
 *   - subject      — text, max 1000 chars
 *   - content_html — rich text email body (primary)
 *   - content_text — plain text fallback
 *   - reply_to     — Reply-To header email (optional)
 *   - priority     — low / normal (default) / high
 *   - scheduled_on — optional datetime for future delivery
 *   - attachments  — JSON array of file IDs (default [])
 *
 * @module pages/notifications/EmailCompose
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { FormEvent, ChangeEvent, KeyboardEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import { get, post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import {
  useSmtpConfigs,
  useQueueEmail,
} from '../../hooks/useNotifications';
import type {
  SmtpConfig,
  SendEmailRequest,
  QueueEmailRequest,
  NotificationPriority,
} from '../../hooks/useNotifications';
import { useFileUpload } from '../../hooks/useFiles';
import type { FileMetadata } from '../../hooks/useFiles';

/* ------------------------------------------------------------------ */
/*  Local Types                                                        */
/* ------------------------------------------------------------------ */

/** Metadata kept per attached file for display */
interface AttachmentInfo {
  id: string;
  filename: string;
  size: number;
  contentType: string;
}

/** Per-field validation error map */
interface FieldErrors {
  [fieldName: string]: string;
}

/** API response shape for email send/queue endpoints */
interface EmailActionResponse {
  id: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** Priority dropdown options — mirrors EmailPriority enum (Low=0, Normal=1, High=2) */
const PRIORITY_OPTIONS: ReadonlyArray<{ value: NotificationPriority; label: string }> = [
  { value: 'low', label: 'Low' },
  { value: 'normal', label: 'Normal' },
  { value: 'high', label: 'High' },
];

/** Maximum subject length per MailPlugin.20190215.cs field definition */
const MAX_SUBJECT_LENGTH = 1000;

/* ------------------------------------------------------------------ */
/*  Tailwind CSS class constants (matching sibling page patterns)      */
/* ------------------------------------------------------------------ */

const labelCls =
  'block text-sm font-medium text-gray-700 mb-1';
const inputCls =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500';
const inputErrCls =
  'block w-full rounded-md border border-red-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus:border-red-500 focus:outline-none focus:ring-1 focus:ring-red-500';
const selectCls =
  'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50';
const selectErrCls =
  'block w-full rounded-md border border-red-300 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm focus:border-red-500 focus:outline-none focus:ring-1 focus:ring-red-500';
const helpCls = 'mt-1 text-xs text-gray-500';
const errCls = 'mt-1 text-xs text-red-600';
const sectionCls =
  'rounded-lg border border-gray-200 bg-white p-6 shadow-sm';
const sectionTitleCls =
  'mb-4 text-base font-semibold text-gray-900';

/* ------------------------------------------------------------------ */
/*  Helper Functions                                                   */
/* ------------------------------------------------------------------ */

/** Basic email format validation — matches monolith email pattern */
function isValidEmail(email: string): boolean {
  if (!email || !email.trim()) return false;
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim());
}

/**
 * Parse email string in "Display Name <address>" or plain "address" format.
 * Returns the bare email address portion.
 */
function parseEmailAddress(input: string): string {
  const trimmed = input.trim();
  const angleMatch = trimmed.match(/<([^>]+)>/);
  if (angleMatch) {
    return angleMatch[1].trim();
  }
  return trimmed;
}

/**
 * Validate the email composition form.
 *
 * Rules align with SmtpInternalService.ValidatePreCreateRecord():
 *  - SMTP service selection required (service_id is required)
 *  - Sender email required and must be valid
 *  - At least one recipient required; each must be valid email
 *  - CC/BCC recipients (if any) must be valid emails
 *  - Subject required (MaxLength 1000)
 *  - Reply-to (if provided) must be valid email
 */
function validateEmailForm(
  recipients: string[],
  ccRecipients: string[],
  bccRecipients: string[],
  smtpConfigId: string,
  subject: string,
  senderEmail: string,
  replyToEmail: string,
): FieldErrors {
  const errors: FieldErrors = {};

  if (!smtpConfigId) {
    errors.smtpConfigId = 'SMTP service selection is required.';
  }

  if (!senderEmail?.trim()) {
    errors.senderEmail = 'Sender email address is required.';
  } else if (!isValidEmail(senderEmail)) {
    errors.senderEmail = 'Sender email must be a valid email address.';
  }

  if (recipients.length === 0) {
    errors.recipients = 'At least one recipient is required.';
  } else {
    const invalid = recipients.find((r) => !isValidEmail(r));
    if (invalid) {
      errors.recipients = `Invalid recipient email address: ${invalid}`;
    }
  }

  if (ccRecipients.length > 0) {
    const invalid = ccRecipients.find((r) => !isValidEmail(r));
    if (invalid) {
      errors.ccRecipients = `Invalid CC email address: ${invalid}`;
    }
  }

  if (bccRecipients.length > 0) {
    const invalid = bccRecipients.find((r) => !isValidEmail(r));
    if (invalid) {
      errors.bccRecipients = `Invalid BCC email address: ${invalid}`;
    }
  }

  if (!subject?.trim()) {
    errors.subject = 'Subject is required.';
  } else if (subject.length > MAX_SUBJECT_LENGTH) {
    errors.subject = `Subject must not exceed ${MAX_SUBJECT_LENGTH} characters.`;
  }

  if (replyToEmail && replyToEmail.trim() !== '' && !isValidEmail(replyToEmail)) {
    errors.replyToEmail = 'Reply-to must be a valid email address.';
  }

  return errors;
}

/**
 * Convert field-level validation errors into FormValidation shape
 * expected by the DynamicForm validation / showValidation props.
 */
function toFormValidation(
  errors: FieldErrors,
  serverMessage?: string,
): FormValidation {
  const validationErrors: ValidationError[] = Object.entries(errors).map(
    ([propertyName, message]) => ({ propertyName, message }),
  );
  return {
    message:
      serverMessage ??
      (validationErrors.length > 0
        ? 'Please correct the errors below.'
        : undefined),
    errors: validationErrors,
  };
}

/** Format bytes into a human-readable file size string */
function formatFileSize(bytes: number): string {
  if (bytes === 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  const k = 1024;
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  const size = parseFloat((bytes / Math.pow(k, i)).toFixed(1));
  return `${size} ${units[i] ?? 'B'}`;
}

/* ================================================================== */
/*  EmailCompose Component                                             */
/* ================================================================== */

/**
 * Email Composition page component.
 *
 * Renders a multi-section form for composing and sending emails:
 *  1. SMTP Service selection (auto-populates sender)
 *  2. Sender Name and Email
 *  3. Recipients (To, CC, BCC — tag-style inputs)
 *  4. Subject, Reply-To, Priority
 *  5. Email Body (HTML and Plain Text tabs)
 *  6. Attachments (file upload with progress)
 *  7. Schedule (optional datetime picker)
 *  8. Actions (Send Now, Schedule, Cancel)
 */
export default function EmailCompose(): React.JSX.Element {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ---- Query Hooks ---- */

  const {
    data: smtpConfigsData,
    isLoading: isSmtpLoading,
    isError: isSmtpError,
  } = useSmtpConfigs();

  /* ---- Mutation Hooks ---- */

  const {
    mutateAsync: queueEmail,
    isPending: isQueuePending,
    isError: isQueueError,
    error: queueError,
  } = useQueueEmail();

  /* File upload hook for email attachments */
  const {
    upload: uploadFile,
    progress: uploadProgress,
    isUploading,
    isError: isUploadError,
    error: uploadError,
    reset: resetUpload,
  } = useFileUpload();

  /* Inline mutation for "Send Now" — uses post() from client.ts */
  const {
    mutateAsync: sendNow,
    isPending: isSendPending,
  } = useMutation<ApiResponse<EmailActionResponse>, Error, SendEmailRequest>({
    mutationFn: (payload: SendEmailRequest) =>
      post<EmailActionResponse>('/notifications/email/send', payload),
  });

  /* ---- Form State ---- */

  const [smtpConfigId, setSmtpConfigId] = useState<string>('');
  const [senderName, setSenderName] = useState<string>('');
  const [senderEmail, setSenderEmail] = useState<string>('');
  const [recipients, setRecipients] = useState<string[]>([]);
  const [recipientInput, setRecipientInput] = useState<string>('');
  const [ccRecipients, setCcRecipients] = useState<string[]>([]);
  const [ccInput, setCcInput] = useState<string>('');
  const [bccRecipients, setBccRecipients] = useState<string[]>([]);
  const [bccInput, setBccInput] = useState<string>('');
  const [showCc, setShowCc] = useState<boolean>(false);
  const [showBcc, setShowBcc] = useState<boolean>(false);
  const [subject, setSubject] = useState<string>('');
  const [contentHtml, setContentHtml] = useState<string>('');
  const [contentText, setContentText] = useState<string>('');
  const [replyToEmail, setReplyToEmail] = useState<string>('');
  const [priority, setPriority] = useState<NotificationPriority>('normal');
  const [scheduledOn, setScheduledOn] = useState<string>('');
  const [attachments, setAttachments] = useState<AttachmentInfo[]>([]);
  const [bodyTab, setBodyTab] = useState<'html' | 'text'>('html');

  /* ---- Validation State ---- */

  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [formValidation, setFormValidation] = useState<FormValidation>({
    errors: [],
  });

  /* ---- Computed Values ---- */

  /** Enabled SMTP configurations available for the dropdown */
  const enabledSmtpConfigs = useMemo<SmtpConfig[]>(() => {
    if (!smtpConfigsData?.object?.configs) return [];
    return smtpConfigsData.object.configs.filter(
      (cfg: SmtpConfig) => cfg.isEnabled,
    );
  }, [smtpConfigsData]);

  /** Whether any async operation is in progress */
  const isBusy = useMemo<boolean>(
    () => isSendPending || isQueuePending || isUploading,
    [isSendPending, isQueuePending, isUploading],
  );

  /** Whether the form has any validation errors displayed */
  const hasErrors = useMemo<boolean>(
    () => formValidation.errors.length > 0 || isQueueError || isSmtpError,
    [formValidation.errors.length, isQueueError, isSmtpError],
  );

  /* ---- Error Handlers ---- */

  /** Map API validation errors from response body to field errors */
  const handleApiValidationErrors = useCallback(
    (response: ApiResponse<unknown>) => {
      const serverErrors: FieldErrors = {};
      if (response.errors && response.errors.length > 0) {
        response.errors.forEach((item) => {
          if (item.key) {
            serverErrors[item.key] = item.message;
          }
        });
      }
      setFieldErrors(serverErrors);
      setFormValidation(
        toFormValidation(serverErrors, response.message || 'Failed to process email.'),
      );
    },
    [],
  );

  /** Handle thrown mutation errors (network / server errors) */
  const handleMutationError = useCallback((err: Error) => {
    let errorMessage = 'An unexpected error occurred while processing your email.';
    const serverErrors: FieldErrors = {};

    /* Extract structured data from ApiError if present */
    const apiError = err as unknown as ApiError;
    if (apiError.message) {
      errorMessage = apiError.message;
    }
    if (apiError.errors && Array.isArray(apiError.errors)) {
      apiError.errors.forEach((item) => {
        if (item.key) {
          serverErrors[item.key] = item.message;
        }
      });
    }

    setFieldErrors(serverErrors);
    setFormValidation(toFormValidation(serverErrors, errorMessage));
  }, []);

  /* ---- SMTP Config Change Handler ---- */

  /**
   * When user selects an SMTP config, auto-populate sender fields.
   * Uses get() to fetch fresh config details from the API, falling
   * back to the locally cached config list data.
   */
  const handleSmtpConfigChange = useCallback(
    async (configId: string) => {
      setSmtpConfigId(configId);
      /* Clear field error on change */
      setFieldErrors((prev) => {
        const next = { ...prev };
        delete next.smtpConfigId;
        return next;
      });

      if (!configId) {
        setSenderName('');
        setSenderEmail('');
        setReplyToEmail('');
        return;
      }

      /* Attempt fresh fetch via get() from client.ts */
      try {
        const response = await get<SmtpConfig>(
          `/notifications/smtp-configs/${configId}`,
        );
        if (response.success && response.object) {
          setSenderName(response.object.defaultFromName ?? '');
          setSenderEmail(response.object.defaultFromEmail ?? '');
          if (response.object.defaultReplyToEmail) {
            setReplyToEmail(response.object.defaultReplyToEmail);
          }
          return;
        }
      } catch {
        /* Fallback to locally cached data below */
      }

      /* Fallback: populate from the cached SMTP configs list */
      const cached = enabledSmtpConfigs.find((c) => c.id === configId);
      if (cached) {
        setSenderName(cached.defaultFromName ?? '');
        setSenderEmail(cached.defaultFromEmail ?? '');
        if (cached.defaultReplyToEmail) {
          setReplyToEmail(cached.defaultReplyToEmail);
        }
      }
    },
    [enabledSmtpConfigs],
  );

  /* ---- Recipient Tag Management ---- */

  /** Add a validated email address as a tag to the given recipient list */
  const addRecipient = useCallback(
    (
      currentList: string[],
      setList: React.Dispatch<React.SetStateAction<string[]>>,
      setInput: React.Dispatch<React.SetStateAction<string>>,
      rawInput: string,
      errorField: string,
    ) => {
      const email = parseEmailAddress(rawInput);
      if (!email) return;

      if (!isValidEmail(email)) {
        setFieldErrors((prev) => ({
          ...prev,
          [errorField]: `Invalid email address: ${email}`,
        }));
        return;
      }

      if (currentList.includes(email)) {
        setInput('');
        return;
      }

      setList((prev) => [...prev, email]);
      setInput('');
      /* Clear any existing error for this field */
      setFieldErrors((prev) => {
        const next = { ...prev };
        delete next[errorField];
        return next;
      });
    },
    [],
  );

  /** Handle keydown on recipient inputs — Enter/Comma adds the tag */
  const handleRecipientKeyDown = useCallback(
    (
      e: KeyboardEvent<HTMLInputElement>,
      currentList: string[],
      setList: React.Dispatch<React.SetStateAction<string[]>>,
      setInput: React.Dispatch<React.SetStateAction<string>>,
      rawInput: string,
      errorField: string,
    ) => {
      if (e.key === 'Enter' || e.key === ',') {
        e.preventDefault();
        addRecipient(currentList, setList, setInput, rawInput, errorField);
      }
    },
    [addRecipient],
  );

  /** Handle blur on recipient inputs — adds any pending text */
  const handleRecipientBlur = useCallback(
    (
      currentList: string[],
      setList: React.Dispatch<React.SetStateAction<string[]>>,
      setInput: React.Dispatch<React.SetStateAction<string>>,
      rawInput: string,
      errorField: string,
    ) => {
      if (rawInput.trim()) {
        addRecipient(currentList, setList, setInput, rawInput, errorField);
      }
    },
    [addRecipient],
  );

  /** Remove a recipient tag by index */
  const removeRecipient = useCallback(
    (
      setList: React.Dispatch<React.SetStateAction<string[]>>,
      index: number,
    ) => {
      setList((prev) => prev.filter((_, i) => i !== index));
    },
    [],
  );

  /* ---- Attachment Handlers ---- */

  /** Upload selected files as email attachments */
  const handleFileSelect = useCallback(
    async (e: ChangeEvent<HTMLInputElement>) => {
      const files = e.target.files;
      if (!files || files.length === 0) return;

      for (let i = 0; i < files.length; i++) {
        const file = files[i];
        try {
          const response = await uploadFile({ file });
          if (response.success && response.object) {
            const meta: FileMetadata = response.object;
            setAttachments((prev) => [
              ...prev,
              {
                id: meta.id,
                filename: meta.filename,
                size: meta.size,
                contentType: meta.contentType,
              },
            ]);
          }
        } catch {
          /* Upload errors are surfaced via useFileUpload().isError */
        }
      }

      /* Reset the file input so the same file can be re-selected */
      e.target.value = '';
    },
    [uploadFile],
  );

  /** Remove an attachment by its index */
  const removeAttachment = useCallback(
    (index: number) => {
      setAttachments((prev) => prev.filter((_, i) => i !== index));
      resetUpload();
    },
    [resetUpload],
  );

  /* ---- Generic Field Change Handler ---- */

  /** Clear field error when a text/select input changes */
  const handleFieldChange = useCallback(
    (
      fieldName: string,
      setter: React.Dispatch<React.SetStateAction<string>>,
    ) =>
      (e: ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) => {
        setter(e.target.value);
        setFieldErrors((prev) => {
          if (!prev[fieldName]) return prev;
          const next = { ...prev };
          delete next[fieldName];
          return next;
        });
      },
    [],
  );

  /* ---- Build Request Payload ---- */

  /** Construct the SendEmailRequest payload from current form state */
  const buildPayload = useCallback((): SendEmailRequest => ({
    recipients,
    ccRecipients: ccRecipients.length > 0 ? ccRecipients : undefined,
    bccRecipients: bccRecipients.length > 0 ? bccRecipients : undefined,
    subject: subject.trim(),
    htmlContent: contentHtml,
    textContent: contentText || undefined,
    replyTo: replyToEmail.trim() || undefined,
    smtpConfigId: smtpConfigId || undefined,
    attachmentIds:
      attachments.length > 0 ? attachments.map((a) => a.id) : undefined,
    priority,
  }), [
    recipients, ccRecipients, bccRecipients, subject,
    contentHtml, contentText, replyToEmail, smtpConfigId,
    attachments, priority,
  ]);

  /* ---- Form Submission Handlers ---- */

  /**
   * Handle "Send Now" action — validates then sends email immediately
   * via post() (inline useMutation). On success navigates to email
   * details page.
   */
  const handleSendNow = useCallback(
    async (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      const errors = validateEmailForm(
        recipients, ccRecipients, bccRecipients,
        smtpConfigId, subject, senderEmail, replyToEmail,
      );

      if (Object.keys(errors).length > 0) {
        setFieldErrors(errors);
        setFormValidation(toFormValidation(errors));
        return;
      }

      setFieldErrors({});
      setFormValidation({ errors: [] });

      try {
        const response = await sendNow(buildPayload());
        if (response.success) {
          queryClient.invalidateQueries({ queryKey: ['notifications'] });
          queryClient.invalidateQueries({ queryKey: ['smtp-configs'] });
          const emailId = response.object?.id;
          navigate(
            emailId
              ? `/notifications/emails/${emailId}`
              : '/notifications/emails',
          );
        } else {
          handleApiValidationErrors(response);
        }
      } catch (err) {
        handleMutationError(err as Error);
      }
    },
    [
      recipients, ccRecipients, bccRecipients, smtpConfigId,
      subject, senderEmail, replyToEmail, sendNow, buildPayload,
      queryClient, navigate, handleApiValidationErrors, handleMutationError,
    ],
  );

  /**
   * Handle "Schedule" action — validates (including scheduled datetime)
   * then queues email for future delivery via useQueueEmail().mutateAsync.
   * On success navigates to email list.
   */
  const handleSchedule = useCallback(async () => {
    const errors = validateEmailForm(
      recipients, ccRecipients, bccRecipients,
      smtpConfigId, subject, senderEmail, replyToEmail,
    );

    if (!scheduledOn) {
      errors.scheduledOn = 'Scheduled date and time is required when scheduling.';
    }

    if (Object.keys(errors).length > 0) {
      setFieldErrors(errors);
      setFormValidation(toFormValidation(errors));
      return;
    }

    setFieldErrors({});
    setFormValidation({ errors: [] });

    try {
      const payload: QueueEmailRequest = {
        ...buildPayload(),
        scheduledAt: new Date(scheduledOn).toISOString(),
      };
      const response = await queueEmail(payload);
      if (response.success) {
        queryClient.invalidateQueries({ queryKey: ['notifications'] });
        queryClient.invalidateQueries({ queryKey: ['smtp-configs'] });
        navigate('/notifications/emails');
      } else {
        handleApiValidationErrors(response);
      }
    } catch (err) {
      handleMutationError(err as Error);
    }
  }, [
    recipients, ccRecipients, bccRecipients, smtpConfigId,
    subject, senderEmail, replyToEmail, scheduledOn,
    queueEmail, buildPayload, queryClient, navigate,
    handleApiValidationErrors, handleMutationError,
  ]);

  /** Handle Cancel — navigate back to email list */
  const handleCancel = useCallback(() => {
    navigate('/notifications/emails');
  }, [navigate]);

  /* ================================================================ */
  /*  Render                                                           */
  /* ================================================================ */

  return (
    <div className="mx-auto max-w-3xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ---- Page Header ---- */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">Compose Email</h1>
        <p className="mt-1 text-sm text-gray-600">
          Fill in the fields below to compose and send an email.
        </p>
      </div>

      {/* ---- Error Banner ---- */}
      {hasErrors && (
        <div
          role="alert"
          className="mb-6 rounded-md border border-red-200 bg-red-50 p-4"
        >
          <div className="flex">
            <svg
              className="h-5 w-5 shrink-0 text-red-400"
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
            <div className="ml-3">
              <h3 className="text-sm font-medium text-red-800">
                {formValidation.message || 'Please fix the errors below.'}
              </h3>
              {formValidation.errors.length > 0 && (
                <ul className="mt-2 list-disc space-y-1 pl-5 text-sm text-red-700">
                  {formValidation.errors.map((err, idx) => (
                    <li key={`${err.propertyName}-${idx}`}>{err.message}</li>
                  ))}
                </ul>
              )}
              {isQueueError && queueError && !formValidation.message && (
                <p className="mt-1 text-sm text-red-700">
                  {(queueError as Error).message}
                </p>
              )}
            </div>
          </div>
        </div>
      )}

      {/* ---- Main Form ---- */}
      <DynamicForm
        name="email-compose"
        showValidation={formValidation.errors.length > 0}
        validation={formValidation}
        onSubmit={handleSendNow}
      >
        {/* ======================================================== */}
        {/* Section 1 — SMTP Service Selector                         */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>SMTP Service</h2>
          <div>
            <label htmlFor="smtpConfigId" className={labelCls}>
              SMTP Service <span className="text-red-500">*</span>
            </label>
            {isSmtpLoading ? (
              <p className="text-sm text-gray-500">Loading SMTP services…</p>
            ) : isSmtpError ? (
              <p className="text-sm text-red-600">
                Failed to load SMTP services. Please refresh.
              </p>
            ) : enabledSmtpConfigs.length === 0 ? (
              <p className="text-sm text-amber-600">
                No enabled SMTP services found.{' '}
                <button
                  type="button"
                  className="text-blue-600 underline hover:text-blue-800"
                  onClick={() => navigate('/notifications/smtp-services/create')}
                >
                  Create one
                </button>
              </p>
            ) : (
              <select
                id="smtpConfigId"
                name="smtpConfigId"
                value={smtpConfigId}
                onChange={(e) => handleSmtpConfigChange(e.target.value)}
                className={fieldErrors.smtpConfigId ? selectErrCls : selectCls}
                aria-invalid={!!fieldErrors.smtpConfigId}
                aria-describedby={
                  fieldErrors.smtpConfigId ? 'smtpConfigId-error' : undefined
                }
                disabled={isBusy}
              >
                <option value="">— Select SMTP Service —</option>
                {enabledSmtpConfigs.map((cfg) => (
                  <option key={cfg.id} value={cfg.id}>
                    {cfg.name} ({cfg.server}:{cfg.port})
                  </option>
                ))}
              </select>
            )}
            {fieldErrors.smtpConfigId && (
              <p id="smtpConfigId-error" className={errCls}>
                {fieldErrors.smtpConfigId}
              </p>
            )}
            <p className={helpCls}>
              Selecting a service auto-fills the sender and reply-to fields.
            </p>
          </div>
        </div>

        {/* ======================================================== */}
        {/* Section 2 — Sender                                        */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>Sender</h2>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Sender Name */}
            <div>
              <label htmlFor="senderName" className={labelCls}>
                Sender Name
              </label>
              <input
                id="senderName"
                name="senderName"
                type="text"
                value={senderName}
                onChange={handleFieldChange('senderName', setSenderName)}
                className={fieldErrors.senderName ? inputErrCls : inputCls}
                placeholder="e.g. John Doe"
                aria-invalid={!!fieldErrors.senderName}
                aria-describedby={
                  fieldErrors.senderName ? 'senderName-error' : undefined
                }
                disabled={isBusy}
              />
              {fieldErrors.senderName && (
                <p id="senderName-error" className={errCls}>
                  {fieldErrors.senderName}
                </p>
              )}
            </div>

            {/* Sender Email */}
            <div>
              <label htmlFor="senderEmail" className={labelCls}>
                Sender Email <span className="text-red-500">*</span>
              </label>
              <input
                id="senderEmail"
                name="senderEmail"
                type="email"
                value={senderEmail}
                onChange={handleFieldChange('senderEmail', setSenderEmail)}
                className={fieldErrors.senderEmail ? inputErrCls : inputCls}
                placeholder="sender@example.com"
                aria-invalid={!!fieldErrors.senderEmail}
                aria-describedby={
                  fieldErrors.senderEmail ? 'senderEmail-error' : undefined
                }
                disabled={isBusy}
              />
              {fieldErrors.senderEmail && (
                <p id="senderEmail-error" className={errCls}>
                  {fieldErrors.senderEmail}
                </p>
              )}
            </div>
          </div>
        </div>

        {/* ======================================================== */}
        {/* Section 3 — Recipients (To / CC / BCC)                    */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>Recipients</h2>

          {/* To (primary recipients) */}
          <div className="mb-4">
            <div className="flex items-center justify-between">
              <label htmlFor="recipients" className={labelCls}>
                To <span className="text-red-500">*</span>
              </label>
              <div className="flex gap-2 text-sm">
                {!showCc && (
                  <button
                    type="button"
                    className="text-blue-600 hover:text-blue-800"
                    onClick={() => setShowCc(true)}
                  >
                    CC
                  </button>
                )}
                {!showBcc && (
                  <button
                    type="button"
                    className="text-blue-600 hover:text-blue-800"
                    onClick={() => setShowBcc(true)}
                  >
                    BCC
                  </button>
                )}
              </div>
            </div>
            {/* Recipient tags */}
            <div
              className={`flex flex-wrap items-center gap-1 rounded-md border px-2 py-1.5 ${
                fieldErrors.recipients
                  ? 'border-red-300 ring-1 ring-red-300'
                  : 'border-gray-300 focus-within:border-blue-500 focus-within:ring-1 focus-within:ring-blue-500'
              } bg-white`}
            >
              {recipients.map((email, idx) => (
                <span
                  key={`to-${idx}`}
                  className="inline-flex items-center gap-1 rounded bg-blue-100 px-2 py-0.5 text-sm text-blue-800"
                >
                  {email}
                  <button
                    type="button"
                    className="ml-0.5 text-blue-500 hover:text-blue-700"
                    onClick={() => removeRecipient(setRecipients, idx)}
                    aria-label={`Remove ${email}`}
                    disabled={isBusy}
                  >
                    ×
                  </button>
                </span>
              ))}
              <input
                id="recipients"
                type="email"
                value={recipientInput}
                onChange={(e) => setRecipientInput(e.target.value)}
                onKeyDown={(e) =>
                  handleRecipientKeyDown(
                    e, recipients, setRecipients,
                    setRecipientInput, recipientInput, 'recipients',
                  )
                }
                onBlur={() =>
                  handleRecipientBlur(
                    recipients, setRecipients,
                    setRecipientInput, recipientInput, 'recipients',
                  )
                }
                className="flex-1 border-none bg-transparent py-0.5 text-sm outline-none placeholder:text-gray-400"
                placeholder={
                  recipients.length === 0
                    ? 'Type email and press Enter'
                    : ''
                }
                aria-invalid={!!fieldErrors.recipients}
                aria-describedby={
                  fieldErrors.recipients ? 'recipients-error' : undefined
                }
                disabled={isBusy}
              />
            </div>
            {fieldErrors.recipients && (
              <p id="recipients-error" className={errCls}>
                {fieldErrors.recipients}
              </p>
            )}
          </div>

          {/* CC Recipients (toggled) */}
          {showCc && (
            <div className="mb-4">
              <div className="flex items-center justify-between">
                <label htmlFor="ccRecipients" className={labelCls}>
                  CC
                </label>
                <button
                  type="button"
                  className="text-xs text-gray-400 hover:text-gray-600"
                  onClick={() => {
                    setShowCc(false);
                    setCcRecipients([]);
                    setCcInput('');
                  }}
                >
                  Remove CC
                </button>
              </div>
              <div className="flex flex-wrap items-center gap-1 rounded-md border border-gray-300 bg-white px-2 py-1.5 focus-within:border-blue-500 focus-within:ring-1 focus-within:ring-blue-500">
                {ccRecipients.map((email, idx) => (
                  <span
                    key={`cc-${idx}`}
                    className="inline-flex items-center gap-1 rounded bg-green-100 px-2 py-0.5 text-sm text-green-800"
                  >
                    {email}
                    <button
                      type="button"
                      className="ml-0.5 text-green-500 hover:text-green-700"
                      onClick={() => removeRecipient(setCcRecipients, idx)}
                      aria-label={`Remove CC ${email}`}
                      disabled={isBusy}
                    >
                      ×
                    </button>
                  </span>
                ))}
                <input
                  id="ccRecipients"
                  type="email"
                  value={ccInput}
                  onChange={(e) => setCcInput(e.target.value)}
                  onKeyDown={(e) =>
                    handleRecipientKeyDown(
                      e, ccRecipients, setCcRecipients,
                      setCcInput, ccInput, 'ccRecipients',
                    )
                  }
                  onBlur={() =>
                    handleRecipientBlur(
                      ccRecipients, setCcRecipients,
                      setCcInput, ccInput, 'ccRecipients',
                    )
                  }
                  className="flex-1 border-none bg-transparent py-0.5 text-sm outline-none placeholder:text-gray-400"
                  placeholder={
                    ccRecipients.length === 0
                      ? 'Type CC email and press Enter'
                      : ''
                  }
                  disabled={isBusy}
                />
              </div>
              {fieldErrors.ccRecipients && (
                <p className={errCls}>{fieldErrors.ccRecipients}</p>
              )}
            </div>
          )}

          {/* BCC Recipients (toggled) */}
          {showBcc && (
            <div>
              <div className="flex items-center justify-between">
                <label htmlFor="bccRecipients" className={labelCls}>
                  BCC
                </label>
                <button
                  type="button"
                  className="text-xs text-gray-400 hover:text-gray-600"
                  onClick={() => {
                    setShowBcc(false);
                    setBccRecipients([]);
                    setBccInput('');
                  }}
                >
                  Remove BCC
                </button>
              </div>
              <div className="flex flex-wrap items-center gap-1 rounded-md border border-gray-300 bg-white px-2 py-1.5 focus-within:border-blue-500 focus-within:ring-1 focus-within:ring-blue-500">
                {bccRecipients.map((email, idx) => (
                  <span
                    key={`bcc-${idx}`}
                    className="inline-flex items-center gap-1 rounded bg-yellow-100 px-2 py-0.5 text-sm text-yellow-800"
                  >
                    {email}
                    <button
                      type="button"
                      className="ml-0.5 text-yellow-600 hover:text-yellow-800"
                      onClick={() => removeRecipient(setBccRecipients, idx)}
                      aria-label={`Remove BCC ${email}`}
                      disabled={isBusy}
                    >
                      ×
                    </button>
                  </span>
                ))}
                <input
                  id="bccRecipients"
                  type="email"
                  value={bccInput}
                  onChange={(e) => setBccInput(e.target.value)}
                  onKeyDown={(e) =>
                    handleRecipientKeyDown(
                      e, bccRecipients, setBccRecipients,
                      setBccInput, bccInput, 'bccRecipients',
                    )
                  }
                  onBlur={() =>
                    handleRecipientBlur(
                      bccRecipients, setBccRecipients,
                      setBccInput, bccInput, 'bccRecipients',
                    )
                  }
                  className="flex-1 border-none bg-transparent py-0.5 text-sm outline-none placeholder:text-gray-400"
                  placeholder={
                    bccRecipients.length === 0
                      ? 'Type BCC email and press Enter'
                      : ''
                  }
                  disabled={isBusy}
                />
              </div>
              {fieldErrors.bccRecipients && (
                <p className={errCls}>{fieldErrors.bccRecipients}</p>
              )}
            </div>
          )}
        </div>

        {/* ======================================================== */}
        {/* Section 4 — Subject, Reply-To, Priority                   */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>Message Details</h2>

          {/* Subject */}
          <div className="mb-4">
            <label htmlFor="subject" className={labelCls}>
              Subject <span className="text-red-500">*</span>
            </label>
            <input
              id="subject"
              name="subject"
              type="text"
              value={subject}
              onChange={handleFieldChange('subject', setSubject)}
              className={fieldErrors.subject ? inputErrCls : inputCls}
              maxLength={MAX_SUBJECT_LENGTH}
              placeholder="Email subject"
              aria-invalid={!!fieldErrors.subject}
              aria-describedby={
                fieldErrors.subject
                  ? 'subject-error'
                  : 'subject-help'
              }
              disabled={isBusy}
            />
            {fieldErrors.subject ? (
              <p id="subject-error" className={errCls}>
                {fieldErrors.subject}
              </p>
            ) : (
              <p id="subject-help" className={helpCls}>
                {subject.length}/{MAX_SUBJECT_LENGTH} characters
              </p>
            )}
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Reply-To */}
            <div>
              <label htmlFor="replyToEmail" className={labelCls}>
                Reply-To Email
              </label>
              <input
                id="replyToEmail"
                name="replyToEmail"
                type="email"
                value={replyToEmail}
                onChange={handleFieldChange('replyToEmail', setReplyToEmail)}
                className={fieldErrors.replyToEmail ? inputErrCls : inputCls}
                placeholder="reply-to@example.com"
                aria-invalid={!!fieldErrors.replyToEmail}
                aria-describedby={
                  fieldErrors.replyToEmail
                    ? 'replyToEmail-error'
                    : 'replyToEmail-help'
                }
                disabled={isBusy}
              />
              {fieldErrors.replyToEmail ? (
                <p id="replyToEmail-error" className={errCls}>
                  {fieldErrors.replyToEmail}
                </p>
              ) : (
                <p id="replyToEmail-help" className={helpCls}>
                  Optional. Replies will go to this address.
                </p>
              )}
            </div>

            {/* Priority */}
            <div>
              <label htmlFor="priority" className={labelCls}>
                Priority
              </label>
              <select
                id="priority"
                name="priority"
                value={priority}
                onChange={(e) =>
                  setPriority(e.target.value as NotificationPriority)
                }
                className={selectCls}
                disabled={isBusy}
              >
                {PRIORITY_OPTIONS.map((opt) => (
                  <option key={opt.value} value={opt.value}>
                    {opt.label}
                  </option>
                ))}
              </select>
              <p className={helpCls}>Email delivery priority level.</p>
            </div>
          </div>
        </div>

        {/* ======================================================== */}
        {/* Section 5 — Email Body (HTML / Plain Text Tabs)           */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>Email Body</h2>

          {/* Tab switcher */}
          <div className="mb-3 flex border-b border-gray-200" role="tablist">
            <button
              type="button"
              role="tab"
              aria-selected={bodyTab === 'html'}
              aria-controls="body-html-panel"
              id="body-html-tab"
              className={`px-4 py-2 text-sm font-medium ${
                bodyTab === 'html'
                  ? 'border-b-2 border-blue-600 text-blue-600'
                  : 'text-gray-500 hover:border-b-2 hover:border-gray-300 hover:text-gray-700'
              }`}
              onClick={() => setBodyTab('html')}
            >
              HTML
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={bodyTab === 'text'}
              aria-controls="body-text-panel"
              id="body-text-tab"
              className={`px-4 py-2 text-sm font-medium ${
                bodyTab === 'text'
                  ? 'border-b-2 border-blue-600 text-blue-600'
                  : 'text-gray-500 hover:border-b-2 hover:border-gray-300 hover:text-gray-700'
              }`}
              onClick={() => setBodyTab('text')}
            >
              Plain Text
            </button>
          </div>

          {/* HTML tab panel */}
          <div
            id="body-html-panel"
            role="tabpanel"
            aria-labelledby="body-html-tab"
            hidden={bodyTab !== 'html'}
          >
            <label htmlFor="contentHtml" className="sr-only">
              HTML Content
            </label>
            <textarea
              id="contentHtml"
              name="contentHtml"
              value={contentHtml}
              onChange={(e) => setContentHtml(e.target.value)}
              rows={12}
              className={`${inputCls} font-mono text-sm`}
              placeholder="<html><body>Your email content here...</body></html>"
              disabled={isBusy}
            />
            {fieldErrors.contentHtml && (
              <p className={errCls}>{fieldErrors.contentHtml}</p>
            )}
            <p className={helpCls}>
              Enter the email body as HTML. A rich text editor integration
              can be added as a future enhancement.
            </p>
          </div>

          {/* Plain Text tab panel */}
          <div
            id="body-text-panel"
            role="tabpanel"
            aria-labelledby="body-text-tab"
            hidden={bodyTab !== 'text'}
          >
            <label htmlFor="contentText" className="sr-only">
              Plain Text Content
            </label>
            <textarea
              id="contentText"
              name="contentText"
              value={contentText}
              onChange={(e) => setContentText(e.target.value)}
              rows={12}
              className={inputCls}
              placeholder="Plain text version of the email body…"
              disabled={isBusy}
            />
            <p className={helpCls}>
              Optional plain-text fallback for email clients that do not
              support HTML.
            </p>
          </div>
        </div>

        {/* ======================================================== */}
        {/* Section 6 — Attachments                                   */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>Attachments</h2>

          {/* File upload input */}
          <div className="mb-3">
            <label
              htmlFor="file-upload"
              className="inline-flex cursor-pointer items-center gap-2 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-within:outline-none focus-within:ring-2 focus-within:ring-blue-500 focus-within:ring-offset-2"
            >
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
                  d="M18.375 12.739l-7.693 7.693a4.5 4.5 0 01-6.364-6.364l10.94-10.94A3 3 0 1119.5 7.372L8.552 18.32m.009-.01l-.01.01m5.699-9.941l-7.81 7.81a1.5 1.5 0 002.112 2.13"
                />
              </svg>
              {isUploading ? 'Uploading…' : 'Attach Files'}
              <input
                id="file-upload"
                type="file"
                multiple
                className="sr-only"
                onChange={handleFileSelect}
                disabled={isBusy}
              />
            </label>
          </div>

          {/* Upload progress indicator */}
          {isUploading && (
            <div className="mb-3">
              <div className="flex items-center justify-between text-sm text-gray-600">
                <span>Uploading…</span>
                <span>{uploadProgress.percentage}%</span>
              </div>
              <div className="mt-1 h-2 w-full overflow-hidden rounded-full bg-gray-200">
                <div
                  className="h-full rounded-full bg-blue-600 transition-all duration-300"
                  style={{ width: `${uploadProgress.percentage}%` }}
                  role="progressbar"
                  aria-valuenow={uploadProgress.percentage}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-label="File upload progress"
                />
              </div>
            </div>
          )}

          {/* Upload error */}
          {isUploadError && uploadError && (
            <p className="mb-3 text-sm text-red-600">
              Upload failed: {(uploadError as Error).message}
            </p>
          )}

          {/* Attached files list */}
          {attachments.length > 0 && (
            <ul className="divide-y divide-gray-200 rounded-md border border-gray-200">
              {attachments.map((att, idx) => (
                <li
                  key={att.id}
                  className="flex items-center justify-between px-4 py-2 text-sm"
                >
                  <div className="flex min-w-0 items-center gap-2">
                    <svg
                      className="h-4 w-4 shrink-0 text-gray-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      strokeWidth={1.5}
                      stroke="currentColor"
                      aria-hidden="true"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        d="M19.5 14.25v-2.625a3.375 3.375 0 00-3.375-3.375h-1.5A1.125 1.125 0 0113.5 7.125v-1.5a3.375 3.375 0 00-3.375-3.375H8.25m2.25 0H5.625c-.621 0-1.125.504-1.125 1.125v17.25c0 .621.504 1.125 1.125 1.125h12.75c.621 0 1.125-.504 1.125-1.125V11.25a9 9 0 00-9-9z"
                      />
                    </svg>
                    <span className="truncate font-medium text-gray-900">
                      {att.filename}
                    </span>
                    <span className="shrink-0 text-gray-500">
                      ({formatFileSize(att.size)})
                    </span>
                  </div>
                  <button
                    type="button"
                    className="ml-2 text-red-500 hover:text-red-700"
                    onClick={() => removeAttachment(idx)}
                    aria-label={`Remove attachment ${att.filename}`}
                    disabled={isBusy}
                  >
                    Remove
                  </button>
                </li>
              ))}
            </ul>
          )}

          {attachments.length === 0 && !isUploading && (
            <p className="text-sm text-gray-500">No attachments added.</p>
          )}
        </div>

        {/* ======================================================== */}
        {/* Section 7 — Schedule                                      */}
        {/* ======================================================== */}
        <div className={sectionCls}>
          <h2 className={sectionTitleCls}>Schedule</h2>
          <div>
            <label htmlFor="scheduledOn" className={labelCls}>
              Schedule Date &amp; Time
            </label>
            <input
              id="scheduledOn"
              name="scheduledOn"
              type="datetime-local"
              value={scheduledOn}
              onChange={handleFieldChange('scheduledOn', setScheduledOn)}
              className={fieldErrors.scheduledOn ? inputErrCls : inputCls}
              aria-invalid={!!fieldErrors.scheduledOn}
              aria-describedby={
                fieldErrors.scheduledOn
                  ? 'scheduledOn-error'
                  : 'scheduledOn-help'
              }
              disabled={isBusy}
            />
            {fieldErrors.scheduledOn ? (
              <p id="scheduledOn-error" className={errCls}>
                {fieldErrors.scheduledOn}
              </p>
            ) : (
              <p id="scheduledOn-help" className={helpCls}>
                Optional. Leave empty and click &quot;Send Now&quot; for
                immediate delivery, or pick a future date/time and click
                &quot;Schedule&quot;.
              </p>
            )}
          </div>
        </div>

        {/* ======================================================== */}
        {/* Section 8 — Actions                                       */}
        {/* ======================================================== */}
        <div className="flex flex-wrap items-center justify-end gap-3 pt-2">
          {/* Cancel */}
          <button
            type="button"
            onClick={handleCancel}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2"
            disabled={isBusy}
          >
            Cancel
          </button>

          {/* Schedule (only when datetime is set) */}
          {scheduledOn && (
            <button
              type="button"
              onClick={handleSchedule}
              className="rounded-md border border-transparent bg-green-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-green-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-green-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60"
              disabled={isBusy}
            >
              {isQueuePending ? 'Scheduling…' : 'Schedule'}
            </button>
          )}

          {/* Send Now (form submit button) */}
          <button
            type="submit"
            className="rounded-md border border-transparent bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60"
            disabled={isBusy}
          >
            {isSendPending ? 'Sending…' : 'Send Now'}
          </button>
        </div>
      </DynamicForm>
    </div>
  );
}
