/**
 * SmtpServiceManage.tsx — SMTP Service Edit Form Page
 *
 * React page component for editing an existing SMTP service configuration.
 * Pre-populates the form with fetched service data and provides Save, Test,
 * and Delete actions. Replaces the monolith's SmtpInternalService update/
 * test/delete flows with React + TanStack Query mutations against the
 * Notifications service Lambda handlers via API Gateway.
 *
 * Route: /notifications/smtp-services/:serviceId
 */

import React, { useState, useEffect, useCallback, type FormEvent } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate } from 'react-router';
import apiClient, {
  type ApiResponse,
  type ApiError,
  get,
  put,
  post,
  del,
} from '../../api/client';

// apiClient default import retained per schema contract — convenience
// functions (get, put, post, del) delegate to it internally.
import Modal, { ModalSize } from '../../components/common/Modal';
import DynamicForm, {
  type FormValidation,
  type ValidationError,
} from '../../components/forms/DynamicForm';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** SMTP service data returned by the API (matches SmtpService.cs properties). */
interface SmtpServiceData {
  id: string;
  name: string;
  server: string;
  port: number;
  username: string | null;
  password: string | null;
  default_sender_name: string;
  default_sender_email: string;
  default_reply_to_email: string | null;
  max_retries_count: number;
  retry_wait_minutes: number;
  is_default: boolean;
  is_enabled: boolean;
  connection_security: string;
}

/** Form state for editing an SMTP service. */
interface SmtpServiceForm {
  name: string;
  server: string;
  port: number;
  username: string;
  password: string;
  default_sender_name: string;
  default_sender_email: string;
  default_reply_to_email: string;
  max_retries_count: number;
  retry_wait_minutes: number;
  is_default: boolean;
  is_enabled: boolean;
  connection_security: string;
}

/** Form fields for the test email modal. */
interface TestEmailForm {
  recipient_email: string;
  subject: string;
  content: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const SMTP_BASE = '/v1/notifications/smtp-services';

/** Connection security dropdown options (from MailKit SecureSocketOptions enum). */
const CONNECTION_SECURITY_OPTIONS: { value: string; label: string }[] = [
  { value: '0', label: 'None' },
  { value: '1', label: 'Auto' },
  { value: '2', label: 'SSL on Connect' },
  { value: '3', label: 'StartTLS' },
  { value: '4', label: 'StartTLS When Available' },
];

/** Default values for a new/reset form. */
const INITIAL_FORM: SmtpServiceForm = {
  name: '',
  server: '',
  port: 25,
  username: '',
  password: '',
  default_sender_name: '',
  default_sender_email: '',
  default_reply_to_email: '',
  max_retries_count: 3,
  retry_wait_minutes: 60,
  is_default: false,
  is_enabled: true,
  connection_security: '1',
};

/** Default test email modal form state. */
const INITIAL_TEST_FORM: TestEmailForm = {
  recipient_email: '',
  subject: '',
  content: '',
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Basic RFC-5322-compliant email regex for client-side validation. */
const EMAIL_REGEX = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * Validates an email address string.
 * Mirrors the monolith's IsEmail() utility used in
 * SmtpInternalService.ValidatePreUpdateRecord().
 */
function isValidEmail(email: string): boolean {
  if (!email || !email.trim()) return false;
  return EMAIL_REGEX.test(email.trim());
}

/**
 * Client-side validation replicating SmtpInternalService.ValidatePreUpdateRecord().
 *
 * Rules:
 *  1. Name is required (uniqueness validated server-side, excluding current record)
 *  2. Server is required
 *  3. Port must be 1–65025 (matching source, not 65535)
 *  4. Max retries count: 1–10
 *  5. Retry wait minutes: 1–1440
 *  6. Default sender email: must be a valid email
 *  7. Default reply-to email: must be a valid email if provided
 *  8. Connection security: must be a valid enum value (0–4)
 */
function validateSmtpServiceForm(data: SmtpServiceForm): ValidationError[] {
  const errors: ValidationError[] = [];

  if (!data.name || !data.name.trim()) {
    errors.push({ propertyName: 'name', message: 'Name is required.' });
  }

  if (!data.server || !data.server.trim()) {
    errors.push({ propertyName: 'server', message: 'Server is required.' });
  }

  const port = Number(data.port);
  if (Number.isNaN(port) || port < 1 || port > 65025) {
    errors.push({
      propertyName: 'port',
      message: 'Port must be between 1 and 65025.',
    });
  }

  const retries = Number(data.max_retries_count);
  if (Number.isNaN(retries) || retries < 1 || retries > 10) {
    errors.push({
      propertyName: 'max_retries_count',
      message: 'Max retries count must be between 1 and 10.',
    });
  }

  const waitMinutes = Number(data.retry_wait_minutes);
  if (Number.isNaN(waitMinutes) || waitMinutes < 1 || waitMinutes > 1440) {
    errors.push({
      propertyName: 'retry_wait_minutes',
      message: 'Retry wait minutes must be between 1 and 1440.',
    });
  }

  if (!isValidEmail(data.default_sender_email)) {
    errors.push({
      propertyName: 'default_sender_email',
      message: 'A valid default sender email is required.',
    });
  }

  if (
    data.default_reply_to_email &&
    data.default_reply_to_email.trim() !== '' &&
    !isValidEmail(data.default_reply_to_email)
  ) {
    errors.push({
      propertyName: 'default_reply_to_email',
      message: 'Default reply-to email must be a valid email address.',
    });
  }

  const validSecurityValues = ['0', '1', '2', '3', '4'];
  if (!validSecurityValues.includes(String(data.connection_security))) {
    errors.push({
      propertyName: 'connection_security',
      message: 'Invalid connection security option.',
    });
  }

  return errors;
}

/**
 * Validates the test email modal fields.
 * Mirrors SmtpInternalService.TestSmtpServiceOnPost() validation.
 */
function validateTestEmailForm(data: TestEmailForm): ValidationError[] {
  const errors: ValidationError[] = [];

  if (!data.recipient_email || !data.recipient_email.trim()) {
    errors.push({
      propertyName: 'recipient_email',
      message: 'Recipient email is required.',
    });
  } else if (!isValidEmail(data.recipient_email)) {
    errors.push({
      propertyName: 'recipient_email',
      message: 'Recipient email must be a valid email address.',
    });
  }

  if (!data.subject || !data.subject.trim()) {
    errors.push({
      propertyName: 'subject',
      message: 'Subject is required.',
    });
  }

  if (!data.content || !data.content.trim()) {
    errors.push({
      propertyName: 'content',
      message: 'Content is required.',
    });
  }

  return errors;
}

/**
 * Returns the field-level error message for a given property, or an empty
 * string if no error exists.
 */
function getFieldError(
  errors: ValidationError[],
  propertyName: string,
): string {
  const match = errors.find(
    (e) => e.propertyName.toLowerCase() === propertyName.toLowerCase(),
  );
  return match ? match.message : '';
}

// ---------------------------------------------------------------------------
// API Hooks
// ---------------------------------------------------------------------------

/**
 * Fetches an existing SMTP service by ID.
 * GET /v1/notifications/smtp-services/:serviceId
 */
function useSmtpService(serviceId: string | undefined) {
  return useQuery<ApiResponse<SmtpServiceData>>({
    queryKey: ['smtp-service', serviceId],
    queryFn: () => get<SmtpServiceData>(`${SMTP_BASE}/${serviceId}`),
    enabled: !!serviceId,
  });
}

/**
 * Mutation for updating an existing SMTP service.
 * PUT /v1/notifications/smtp-services/:serviceId
 */
function useUpdateSmtpService(serviceId: string | undefined) {
  const queryClient = useQueryClient();
  return useMutation<ApiResponse<SmtpServiceData>, ApiError, SmtpServiceForm>({
    mutationFn: (data: SmtpServiceForm) =>
      put<SmtpServiceData>(`${SMTP_BASE}/${serviceId}`, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['smtp-services'] });
      queryClient.invalidateQueries({
        queryKey: ['smtp-service', serviceId],
      });
    },
  });
}

/**
 * Mutation for sending a test email through the SMTP configuration.
 * POST /v1/notifications/smtp-services/:serviceId/test
 */
function useTestSmtpService(serviceId: string | undefined) {
  return useMutation<ApiResponse<unknown>, ApiError, TestEmailForm>({
    mutationFn: (data: TestEmailForm) =>
      post<unknown>(`${SMTP_BASE}/${serviceId}/test`, data),
  });
}

/**
 * Mutation for deleting an SMTP service.
 * DELETE /v1/notifications/smtp-services/:serviceId
 */
function useDeleteSmtpService() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  return useMutation<ApiResponse<unknown>, ApiError, string>({
    mutationFn: (id: string) => del<unknown>(`${SMTP_BASE}/${id}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['smtp-services'] });
      navigate('/notifications/smtp-services');
    },
  });
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * SmtpServiceManage — Page component for editing an existing SMTP service.
 *
 * Route: /notifications/smtp-services/:serviceId
 *
 * Provides the same six-section form as SmtpServiceCreate, pre-populated with
 * the fetched service data. Additionally offers "Test" and "Delete" actions.
 */
function SmtpServiceManage(): React.JSX.Element {
  // ---- Routing ----
  const { serviceId } = useParams<{ serviceId: string }>();
  const navigate = useNavigate();

  // ---- API hooks ----
  const serviceQuery = useSmtpService(serviceId);
  const updateMutation = useUpdateSmtpService(serviceId);
  const testMutation = useTestSmtpService(serviceId);
  const deleteMutation = useDeleteSmtpService();

  // ---- Form state ----
  const [form, setForm] = useState<SmtpServiceForm>({ ...INITIAL_FORM });
  const [validationErrors, setValidationErrors] = useState<ValidationError[]>(
    [],
  );
  const [serverValidation, setServerValidation] =
    useState<FormValidation | null>(null);
  const [saveSuccess, setSaveSuccess] = useState(false);

  /** Tracks whether the loaded service is currently the default (server state). */
  const [isOriginallyDefault, setIsOriginallyDefault] = useState(false);

  // ---- Modal state ----
  const [isTestModalOpen, setIsTestModalOpen] = useState(false);
  const [testForm, setTestForm] = useState<TestEmailForm>({
    ...INITIAL_TEST_FORM,
  });
  const [testErrors, setTestErrors] = useState<ValidationError[]>([]);
  const [testResult, setTestResult] = useState<{
    success: boolean;
    message: string;
  } | null>(null);

  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);

  // ---- Pre-populate form when service data arrives ----
  useEffect(() => {
    if (serviceQuery.data?.object) {
      const svc = serviceQuery.data.object;
      setForm({
        name: svc.name ?? '',
        server: svc.server ?? '',
        port: svc.port ?? 25,
        username: svc.username ?? '',
        password: svc.password ?? '',
        default_sender_name: svc.default_sender_name ?? '',
        default_sender_email: svc.default_sender_email ?? '',
        default_reply_to_email: svc.default_reply_to_email ?? '',
        max_retries_count: svc.max_retries_count ?? 3,
        retry_wait_minutes: svc.retry_wait_minutes ?? 60,
        is_default: svc.is_default ?? false,
        is_enabled: svc.is_enabled ?? true,
        connection_security: String(svc.connection_security ?? '1'),
      });
      setIsOriginallyDefault(svc.is_default ?? false);
    }
  }, [serviceQuery.data]);

  // ---- Reset success banner after 4 seconds ----
  useEffect(() => {
    if (!saveSuccess) return;
    const timer = setTimeout(() => setSaveSuccess(false), 4000);
    return () => clearTimeout(timer);
  }, [saveSuccess]);

  // ---- Handlers ----

  /** Generic field change handler. */
  const handleFieldChange = useCallback(
    (
      field: keyof SmtpServiceForm,
      value: string | number | boolean,
    ) => {
      setForm((prev) => ({ ...prev, [field]: value }));
      // Clear field-level validation on change
      setValidationErrors((prev) =>
        prev.filter(
          (e) => e.propertyName.toLowerCase() !== field.toLowerCase(),
        ),
      );
      setSaveSuccess(false);
    },
    [],
  );

  /**
   * Handle default-service toggle.
   * Mirrors SmtpInternalService.HandleDefaultServiceSetup():
   *  - Toggling OFF while currently default is BLOCKED.
   *  - Toggling ON shows an informational note.
   */
  const handleDefaultToggle = useCallback(
    (checked: boolean) => {
      if (!checked && isOriginallyDefault) {
        /* Cannot unset is_default directly — user must first designate
           another service as default. Show a validation error instead. */
        setValidationErrors((prev) => {
          const filtered = prev.filter(
            (e) => e.propertyName.toLowerCase() !== 'is_default',
          );
          return [
            ...filtered,
            {
              propertyName: 'is_default',
              message:
                'This is the current default service. You must set another service as default before unsetting this one.',
            },
          ];
        });
        return; // Do NOT change the form value
      }
      handleFieldChange('is_default', checked);
    },
    [isOriginallyDefault, handleFieldChange],
  );

  /** Submit the edit form. */
  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setServerValidation(null);
      setSaveSuccess(false);

      const errors = validateSmtpServiceForm(form);
      setValidationErrors(errors);
      if (errors.length > 0) return;

      updateMutation.mutate(form, {
        onSuccess: () => {
          setSaveSuccess(true);
          setServerValidation(null);
        },
        onError: (err: ApiError) => {
          const sv: FormValidation = {
            message: err.message || 'Failed to update SMTP service.',
            errors:
              err.errors?.map((item) => ({
                propertyName: item.key || 'general',
                message: item.message,
              })) ?? [],
          };
          setServerValidation(sv);
        },
      });
    },
    [form, updateMutation],
  );

  /** Open the test email modal and reset its state. */
  const openTestModal = useCallback(() => {
    setTestForm({ ...INITIAL_TEST_FORM });
    setTestErrors([]);
    setTestResult(null);
    setIsTestModalOpen(true);
  }, []);

  /** Submit the test email form. */
  const handleTestSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setTestResult(null);

      const errors = validateTestEmailForm(testForm);
      setTestErrors(errors);
      if (errors.length > 0) return;

      testMutation.mutate(testForm, {
        onSuccess: () => {
          setTestResult({
            success: true,
            message: 'Test email sent successfully.',
          });
        },
        onError: (err: ApiError) => {
          setTestResult({
            success: false,
            message: err.message || 'Failed to send test email.',
          });
        },
      });
    },
    [testForm, testMutation],
  );

  /** Open the delete confirmation modal. */
  const openDeleteModal = useCallback(() => {
    setIsDeleteModalOpen(true);
  }, []);

  /** Confirm deletion. */
  const handleConfirmDelete = useCallback(() => {
    if (!serviceId) return;
    deleteMutation.mutate(serviceId);
  }, [serviceId, deleteMutation]);

  // ---- Derived state ----
  const isLoading = serviceQuery.isLoading;
  const isError = serviceQuery.isError;
  const isSaving = updateMutation.isPending;
  const isTesting = testMutation.isPending;
  const isDeleting = deleteMutation.isPending;

  const combinedValidation: FormValidation | null =
    validationErrors.length > 0
      ? { message: 'Please fix the errors below.', errors: validationErrors }
      : serverValidation;

  // ---- Loading state ----
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[24rem]">
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-solid border-blue-600 border-e-transparent"
            role="status"
            aria-label="Loading SMTP service"
          />
          <p className="text-sm text-gray-500">Loading SMTP service…</p>
        </div>
      </div>
    );
  }

  // ---- Error state ----
  if (isError) {
    return (
      <div className="mx-auto max-w-4xl px-4 py-8">
        <div
          className="rounded-md border border-red-300 bg-red-50 p-4 text-sm text-red-800"
          role="alert"
        >
          <p className="font-semibold">Error loading SMTP service</p>
          <p className="mt-1">
            {serviceQuery.error instanceof Error
              ? serviceQuery.error.message
              : 'An unexpected error occurred.'}
          </p>
          <button
            type="button"
            className="mt-3 inline-flex items-center rounded-md bg-red-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
            onClick={() => navigate('/notifications/smtp-services')}
          >
            Back to list
          </button>
        </div>
      </div>
    );
  }

  // ---- Main render ----
  return (
    <div className="mx-auto max-w-4xl px-4 py-6">
      {/* Page header */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">
            Edit SMTP Service
          </h1>
          <p className="mt-1 text-sm text-gray-500">
            {form.name || 'Untitled service'}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            onClick={() => navigate('/notifications/smtp-services')}
          >
            Cancel
          </button>
          <button
            type="button"
            className="inline-flex items-center rounded-md bg-teal-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-teal-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-teal-600 disabled:opacity-50 disabled:cursor-not-allowed"
            onClick={openTestModal}
            disabled={isTesting}
          >
            Test
          </button>
          <button
            type="button"
            className="inline-flex items-center rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
            onClick={openDeleteModal}
            disabled={isDeleting}
          >
            Delete
          </button>
        </div>
      </div>

      {/* Success banner */}
      {saveSuccess && (
        <div
          className="mb-4 rounded-md border border-green-300 bg-green-50 px-4 py-3 text-sm text-green-800"
          role="status"
        >
          SMTP service updated successfully.
        </div>
      )}

      {/* Edit form */}
      <DynamicForm
        id="smtp-service-manage-form"
        name="smtp-service-manage"
        method="post"
        labelMode="stacked"
        fieldMode="form"
        showValidation={
          validationErrors.length > 0 || serverValidation !== null
        }
        validation={combinedValidation ?? undefined}
        onSubmit={handleSubmit}
        className="space-y-8"
      >
        {/* ---- Section 1: Basic Configuration ---- */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Basic Configuration
          </legend>
          <div className="mt-2 grid gap-4 sm:grid-cols-2">
            {/* Name */}
            <div className="sm:col-span-2">
              <label
                htmlFor="smtp-name"
                className="block text-sm font-medium text-gray-700"
              >
                Service Name <span className="text-red-500">*</span>
              </label>
              <input
                id="smtp-name"
                type="text"
                required
                value={form.name}
                onChange={(e) => handleFieldChange('name', e.target.value)}
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'name')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="e.g. Production SMTP"
              />
              {getFieldError(validationErrors, 'name') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'name')}
                </p>
              )}
            </div>

            {/* Server */}
            <div>
              <label
                htmlFor="smtp-server"
                className="block text-sm font-medium text-gray-700"
              >
                Server <span className="text-red-500">*</span>
              </label>
              <input
                id="smtp-server"
                type="text"
                required
                value={form.server}
                onChange={(e) => handleFieldChange('server', e.target.value)}
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'server')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="smtp.example.com"
              />
              {getFieldError(validationErrors, 'server') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'server')}
                </p>
              )}
            </div>

            {/* Port */}
            <div>
              <label
                htmlFor="smtp-port"
                className="block text-sm font-medium text-gray-700"
              >
                Port <span className="text-red-500">*</span>
              </label>
              <input
                id="smtp-port"
                type="number"
                required
                min={1}
                max={65025}
                value={form.port}
                onChange={(e) =>
                  handleFieldChange('port', Number(e.target.value))
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'port')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="25"
              />
              {getFieldError(validationErrors, 'port') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'port')}
                </p>
              )}
            </div>
          </div>
        </fieldset>

        {/* ---- Section 2: Authentication ---- */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Authentication
          </legend>
          <div className="mt-2 grid gap-4 sm:grid-cols-2">
            {/* Username */}
            <div>
              <label
                htmlFor="smtp-username"
                className="block text-sm font-medium text-gray-700"
              >
                Username
              </label>
              <input
                id="smtp-username"
                type="text"
                value={form.username}
                onChange={(e) => handleFieldChange('username', e.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="user@example.com"
                autoComplete="username"
              />
            </div>

            {/* Password */}
            <div>
              <label
                htmlFor="smtp-password"
                className="block text-sm font-medium text-gray-700"
              >
                Password
              </label>
              <input
                id="smtp-password"
                type="password"
                value={form.password}
                onChange={(e) => handleFieldChange('password', e.target.value)}
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="••••••••"
                autoComplete="current-password"
              />
            </div>
          </div>
        </fieldset>

        {/* ---- Section 3: Connection Security ---- */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Connection Security
          </legend>
          <div className="mt-2 max-w-xs">
            <label
              htmlFor="smtp-security"
              className="block text-sm font-medium text-gray-700"
            >
              Security Protocol
            </label>
            <select
              id="smtp-security"
              value={form.connection_security}
              onChange={(e) =>
                handleFieldChange('connection_security', e.target.value)
              }
              className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                getFieldError(validationErrors, 'connection_security')
                  ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                  : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
              }`}
            >
              {CONNECTION_SECURITY_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
            {getFieldError(validationErrors, 'connection_security') && (
              <p className="mt-1 text-xs text-red-600">
                {getFieldError(validationErrors, 'connection_security')}
              </p>
            )}
          </div>
        </fieldset>

        {/* ---- Section 4: Default Sender ---- */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Default Sender
          </legend>
          <div className="mt-2 grid gap-4 sm:grid-cols-2">
            {/* Sender Name */}
            <div className="sm:col-span-2">
              <label
                htmlFor="smtp-sender-name"
                className="block text-sm font-medium text-gray-700"
              >
                Sender Name
              </label>
              <input
                id="smtp-sender-name"
                type="text"
                value={form.default_sender_name}
                onChange={(e) =>
                  handleFieldChange('default_sender_name', e.target.value)
                }
                className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                placeholder="WebVella ERP"
              />
            </div>

            {/* Sender Email */}
            <div>
              <label
                htmlFor="smtp-sender-email"
                className="block text-sm font-medium text-gray-700"
              >
                Sender Email <span className="text-red-500">*</span>
              </label>
              <input
                id="smtp-sender-email"
                type="email"
                required
                value={form.default_sender_email}
                onChange={(e) =>
                  handleFieldChange('default_sender_email', e.target.value)
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'default_sender_email')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="noreply@example.com"
              />
              {getFieldError(validationErrors, 'default_sender_email') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'default_sender_email')}
                </p>
              )}
            </div>

            {/* Reply-To Email */}
            <div>
              <label
                htmlFor="smtp-reply-to"
                className="block text-sm font-medium text-gray-700"
              >
                Reply-To Email
              </label>
              <input
                id="smtp-reply-to"
                type="email"
                value={form.default_reply_to_email}
                onChange={(e) =>
                  handleFieldChange('default_reply_to_email', e.target.value)
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'default_reply_to_email')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="support@example.com"
              />
              {getFieldError(validationErrors, 'default_reply_to_email') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'default_reply_to_email')}
                </p>
              )}
            </div>
          </div>
        </fieldset>

        {/* ---- Section 5: Retry Configuration ---- */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Retry Configuration
          </legend>
          <div className="mt-2 grid gap-4 sm:grid-cols-2">
            {/* Max Retries */}
            <div>
              <label
                htmlFor="smtp-max-retries"
                className="block text-sm font-medium text-gray-700"
              >
                Max Retries <span className="text-red-500">*</span>
              </label>
              <input
                id="smtp-max-retries"
                type="number"
                required
                min={1}
                max={10}
                value={form.max_retries_count}
                onChange={(e) =>
                  handleFieldChange(
                    'max_retries_count',
                    Number(e.target.value),
                  )
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'max_retries_count')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
              />
              {getFieldError(validationErrors, 'max_retries_count') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'max_retries_count')}
                </p>
              )}
              <p className="mt-1 text-xs text-gray-400">Range: 1–10</p>
            </div>

            {/* Retry Wait Minutes */}
            <div>
              <label
                htmlFor="smtp-retry-wait"
                className="block text-sm font-medium text-gray-700"
              >
                Retry Wait (minutes) <span className="text-red-500">*</span>
              </label>
              <input
                id="smtp-retry-wait"
                type="number"
                required
                min={1}
                max={1440}
                value={form.retry_wait_minutes}
                onChange={(e) =>
                  handleFieldChange(
                    'retry_wait_minutes',
                    Number(e.target.value),
                  )
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(validationErrors, 'retry_wait_minutes')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
              />
              {getFieldError(validationErrors, 'retry_wait_minutes') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'retry_wait_minutes')}
                </p>
              )}
              <p className="mt-1 text-xs text-gray-400">Range: 1–1440</p>
            </div>
          </div>
        </fieldset>

        {/* ---- Section 6: Service Status ---- */}
        <fieldset className="rounded-lg border border-gray-200 p-4">
          <legend className="px-2 text-sm font-semibold text-gray-700">
            Service Status
          </legend>
          <div className="mt-2 space-y-4">
            {/* Enabled */}
            <div className="flex items-center gap-3">
              <input
                id="smtp-enabled"
                type="checkbox"
                checked={form.is_enabled}
                onChange={(e) =>
                  handleFieldChange('is_enabled', e.target.checked)
                }
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              <label
                htmlFor="smtp-enabled"
                className="text-sm font-medium text-gray-700"
              >
                Service Enabled
              </label>
            </div>

            {/* Default Service */}
            <div>
              <div className="flex items-center gap-3">
                <input
                  id="smtp-default"
                  type="checkbox"
                  checked={form.is_default}
                  onChange={(e) => handleDefaultToggle(e.target.checked)}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                />
                <label
                  htmlFor="smtp-default"
                  className="text-sm font-medium text-gray-700"
                >
                  Default SMTP Service
                </label>
              </div>
              {getFieldError(validationErrors, 'is_default') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(validationErrors, 'is_default')}
                </p>
              )}
              {form.is_default && !isOriginallyDefault && (
                <p className="mt-1 text-xs text-amber-600">
                  Setting this as default will remove the default flag from all
                  other SMTP services.
                </p>
              )}
            </div>
          </div>
        </fieldset>

        {/* ---- Submit Button ---- */}
        <div className="flex justify-end">
          <button
            type="submit"
            disabled={isSaving}
            className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isSaving ? 'Saving…' : 'Save Changes'}
          </button>
        </div>
      </DynamicForm>

      {/* ================================================================ */}
      {/* Test Email Modal                                                  */}
      {/* ================================================================ */}
      <Modal
        isVisible={isTestModalOpen}
        id="test-email-modal"
        title="Send Test Email"
        size={ModalSize.Normal}
        onClose={() => setIsTestModalOpen(false)}
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              onClick={() => setIsTestModalOpen(false)}
            >
              Close
            </button>
            <button
              type="submit"
              form="test-email-form"
              disabled={isTesting}
              className="inline-flex items-center rounded-md bg-teal-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-teal-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-teal-600 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {isTesting ? 'Sending…' : 'Send Test'}
            </button>
          </div>
        }
      >
        {/* Test result banner */}
        {testResult && (
          <div
            className={`mb-4 rounded-md border px-4 py-3 text-sm ${
              testResult.success
                ? 'border-green-300 bg-green-50 text-green-800'
                : 'border-red-300 bg-red-50 text-red-800'
            }`}
            role="status"
          >
            {testResult.message}
          </div>
        )}

        <form id="test-email-form" onSubmit={handleTestSubmit}>
          <div className="space-y-4">
            {/* Recipient Email */}
            <div>
              <label
                htmlFor="test-recipient"
                className="block text-sm font-medium text-gray-700"
              >
                Recipient Email <span className="text-red-500">*</span>
              </label>
              <input
                id="test-recipient"
                type="email"
                required
                value={testForm.recipient_email}
                onChange={(e) =>
                  setTestForm((prev) => ({
                    ...prev,
                    recipient_email: e.target.value,
                  }))
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(testErrors, 'recipient_email')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="recipient@example.com"
              />
              {getFieldError(testErrors, 'recipient_email') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(testErrors, 'recipient_email')}
                </p>
              )}
            </div>

            {/* Subject */}
            <div>
              <label
                htmlFor="test-subject"
                className="block text-sm font-medium text-gray-700"
              >
                Subject <span className="text-red-500">*</span>
              </label>
              <input
                id="test-subject"
                type="text"
                required
                value={testForm.subject}
                onChange={(e) =>
                  setTestForm((prev) => ({
                    ...prev,
                    subject: e.target.value,
                  }))
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(testErrors, 'subject')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="Test email from WebVella ERP"
              />
              {getFieldError(testErrors, 'subject') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(testErrors, 'subject')}
                </p>
              )}
            </div>

            {/* Content */}
            <div>
              <label
                htmlFor="test-content"
                className="block text-sm font-medium text-gray-700"
              >
                Content <span className="text-red-500">*</span>
              </label>
              <textarea
                id="test-content"
                required
                rows={4}
                value={testForm.content}
                onChange={(e) =>
                  setTestForm((prev) => ({
                    ...prev,
                    content: e.target.value,
                  }))
                }
                className={`mt-1 block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-1 ${
                  getFieldError(testErrors, 'content')
                    ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
                    : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
                }`}
                placeholder="This is a test email sent from the SMTP service configuration."
              />
              {getFieldError(testErrors, 'content') && (
                <p className="mt-1 text-xs text-red-600">
                  {getFieldError(testErrors, 'content')}
                </p>
              )}
            </div>
          </div>
        </form>
      </Modal>

      {/* ================================================================ */}
      {/* Delete Confirmation Modal                                         */}
      {/* ================================================================ */}
      <Modal
        isVisible={isDeleteModalOpen}
        id="delete-smtp-modal"
        title="Delete SMTP Service"
        size={ModalSize.Small}
        onClose={() => setIsDeleteModalOpen(false)}
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              className="inline-flex items-center rounded-md bg-white px-3 py-2 text-sm font-medium text-gray-700 shadow-sm ring-1 ring-inset ring-gray-300 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
              onClick={() => setIsDeleteModalOpen(false)}
            >
              Cancel
            </button>
            {!isOriginallyDefault && (
              <button
                type="button"
                disabled={isDeleting}
                className="inline-flex items-center rounded-md bg-red-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-red-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50 disabled:cursor-not-allowed"
                onClick={handleConfirmDelete}
              >
                {isDeleting ? 'Deleting…' : 'Confirm Delete'}
              </button>
            )}
          </div>
        }
      >
        {isOriginallyDefault ? (
          <div className="space-y-3">
            <div
              className="rounded-md border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800"
              role="alert"
            >
              <p className="font-semibold">Cannot delete default service</p>
              <p className="mt-1">
                This SMTP service is currently set as the default. You must
                designate another service as default before deleting this one.
              </p>
            </div>
          </div>
        ) : (
          <div className="space-y-3">
            <p className="text-sm text-gray-600">
              Are you sure you want to delete the SMTP service{' '}
              <span className="font-semibold text-gray-900">
                &quot;{form.name}&quot;
              </span>
              ? This action cannot be undone.
            </p>
            {deleteMutation.isError && (
              <div
                className="rounded-md border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800"
                role="alert"
              >
                {deleteMutation.error instanceof Error
                  ? deleteMutation.error.message
                  : 'Failed to delete SMTP service.'}
              </div>
            )}
          </div>
        )}
      </Modal>
    </div>
  );
}

export default SmtpServiceManage;
