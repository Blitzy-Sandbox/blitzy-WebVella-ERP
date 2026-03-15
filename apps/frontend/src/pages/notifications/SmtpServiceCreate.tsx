/**
 * SmtpServiceCreate.tsx
 *
 * React page component for creating a new SMTP service configuration.
 * Provides a form with host, port, credentials, TLS configuration,
 * and default sender settings.
 *
 * Replaces the monolith's SMTP service record creation page.
 * Validation rules mirror SmtpInternalService.ValidatePreCreateRecord()
 * from WebVella.Erp.Plugins.Mail.
 */

import React, { useState, useCallback } from 'react';
import type { FormEvent, ChangeEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { post } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation } from '../../components/forms/DynamicForm';

/* ------------------------------------------------------------------ */
/*  Types                                                              */
/* ------------------------------------------------------------------ */

/** SMTP service form data — field names match entity definition */
interface SmtpServiceFormData {
  name: string;
  server: string;
  port: number;
  username: string;
  password: string;
  connection_security: string;
  default_sender_name: string;
  default_sender_email: string;
  default_reply_to_email: string;
  max_retries_count: number;
  retry_wait_minutes: number;
  is_default: boolean;
  is_enabled: boolean;
}

/** API response shape for a created SMTP service */
interface SmtpServiceResponse {
  id: string;
  name: string;
}

/** Record of field-level validation error messages */
interface ValidationErrors {
  [fieldName: string]: string;
}

/* ------------------------------------------------------------------ */
/*  Constants                                                          */
/* ------------------------------------------------------------------ */

/** API base path for SMTP service operations */
const SMTP_BASE = '/notifications/smtp-configs';

/** Connection security options matching SecureSocketOptions enum */
const CONNECTION_SECURITY_OPTIONS = [
  { value: '0', label: 'None' },
  { value: '1', label: 'Auto' },
  { value: '2', label: 'SSL on Connect' },
  { value: '3', label: 'StartTLS' },
  { value: '4', label: 'StartTLS When Available' },
] as const;

/** Default form values matching entity field defaults (MailPlugin.20190215.cs) */
const DEFAULT_FORM_DATA: SmtpServiceFormData = {
  name: 'smtp service',
  server: 'smtp.domain.com',
  port: 25,
  username: '',
  password: '',
  connection_security: '1',
  default_sender_name: '',
  default_sender_email: '',
  default_reply_to_email: '',
  max_retries_count: 3,
  retry_wait_minutes: 60,
  is_default: false,
  is_enabled: true,
};

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Basic email format validation.
 * Mirrors the monolith's IsEmail() utility used in SmtpInternalService.
 */
function isValidEmail(email: string): boolean {
  if (!email || !email.trim()) return false;
  const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
  return emailRegex.test(email.trim());
}

/**
 * Client-side validation matching SmtpInternalService.ValidatePreCreateRecord().
 *
 * Rules applied:
 *  - name: required (unique check is server-side only)
 *  - server: required
 *  - port: integer between 1 and 65025 (source uses 65025, NOT 65535)
 *  - default_sender_email: required, valid email format
 *  - default_reply_to_email: if provided, must be valid email
 *  - max_retries_count: integer between 1 and 10
 *  - retry_wait_minutes: integer between 1 and 1440
 *  - connection_security: must be a valid enum value (0-4)
 */
const validateSmtpService = (data: SmtpServiceFormData): ValidationErrors => {
  const errors: ValidationErrors = {};

  /* Name — required */
  if (!data.name?.trim()) {
    errors.name = 'Name is required';
  }

  /* Server — required */
  if (!data.server?.trim()) {
    errors.server = 'Server is required';
  }

  /* Port — must be between 1 and 65025 (per SmtpInternalService source) */
  const portNum = Number(data.port);
  if (!Number.isInteger(portNum) || portNum < 1 || portNum > 65025) {
    errors.port = 'Port must be between 1 and 65025';
  }

  /* Default sender email — required and must be valid email */
  if (!data.default_sender_email?.trim()) {
    errors.default_sender_email = 'Default sender email is required';
  } else if (!isValidEmail(data.default_sender_email)) {
    errors.default_sender_email = 'Must be a valid email address';
  }

  /* Default reply-to email — if provided, must be valid email */
  if (
    data.default_reply_to_email &&
    data.default_reply_to_email.trim() !== '' &&
    !isValidEmail(data.default_reply_to_email)
  ) {
    errors.default_reply_to_email = 'Must be a valid email address';
  }

  /* Max retries count — must be between 1 and 10 */
  const retries = Number(data.max_retries_count);
  if (!Number.isInteger(retries) || retries < 1 || retries > 10) {
    errors.max_retries_count = 'Must be between 1 and 10';
  }

  /* Retry wait minutes — must be between 1 and 1440 */
  const retryWait = Number(data.retry_wait_minutes);
  if (!Number.isInteger(retryWait) || retryWait < 1 || retryWait > 1440) {
    errors.retry_wait_minutes = 'Must be between 1 and 1440 minutes';
  }

  /* Connection security — must be a valid enum value */
  const securityVal = Number(data.connection_security);
  if (!Number.isInteger(securityVal) || securityVal < 0 || securityVal > 4) {
    errors.connection_security = 'Must be a valid connection security option';
  }

  return errors;
};

/**
 * Convert field-level ValidationErrors into DynamicForm's FormValidation format.
 */
function toFormValidation(
  errors: ValidationErrors,
  serverMessage?: string,
): FormValidation {
  const validationErrors = Object.entries(errors).map(
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

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * SMTP Service creation page component.
 *
 * Renders a multi-section form for configuring a new SMTP service:
 *  1. Basic Configuration (name, server, port)
 *  2. Authentication (username, password)
 *  3. Connection Security (enum select)
 *  4. Default Sender (name, email, reply-to)
 *  5. Retry Configuration (max retries, wait minutes)
 *  6. Service Status (is_enabled, is_default)
 *
 * On successful creation, navigates to the manage page for the new service.
 * On cancel, navigates back to the SMTP service list.
 */
export default function SmtpServiceCreate(): React.JSX.Element {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ---------- Form state ---------- */
  const [formData, setFormData] = useState<SmtpServiceFormData>({
    ...DEFAULT_FORM_DATA,
  });
  const [fieldErrors, setFieldErrors] = useState<ValidationErrors>({});
  const [formValidation, setFormValidation] = useState<FormValidation>({
    errors: [],
  });

  /* ---------- Mutation ---------- */
  const { mutate, isPending, isError, error, isSuccess } = useMutation<
    ApiResponse<SmtpServiceResponse>,
    Error,
    SmtpServiceFormData
  >({
    mutationFn: (data: SmtpServiceFormData) =>
      post<SmtpServiceResponse>(SMTP_BASE, data),

    onSuccess: (response: ApiResponse<SmtpServiceResponse>) => {
      if (response.success) {
        /* Invalidate SMTP services list so list page auto-refetches */
        queryClient.invalidateQueries({ queryKey: ['smtp-services'] });

        /* Navigate to the newly created service's manage page */
        const createdId = response.object?.id;
        if (createdId) {
          navigate(`/notifications/smtp/${createdId}/manage`);
        } else {
          navigate('/notifications/smtp');
        }
      } else {
        /* API returned success: false with validation errors */
        const serverErrors: ValidationErrors = {};
        if (response.errors && response.errors.length > 0) {
          response.errors.forEach((err) => {
            if (err.key) {
              serverErrors[err.key] = err.message;
            }
          });
        }
        setFieldErrors(serverErrors);
        setFormValidation(
          toFormValidation(serverErrors, response.message),
        );
      }
    },

    onError: (err: Error) => {
      /* Try to extract structured error information from the response */
      let errorMessage =
        'An error occurred while creating the SMTP service.';
      const serverErrors: ValidationErrors = {};

      const apiError = err as Error & {
        response?: { data?: ApiResponse<unknown> };
      };
      if (apiError.response?.data) {
        const responseData = apiError.response.data;
        if (responseData.message) {
          errorMessage = responseData.message;
        }
        if (responseData.errors && responseData.errors.length > 0) {
          responseData.errors.forEach((e) => {
            if (e.key) {
              serverErrors[e.key] = e.message;
            }
          });
        }
      } else if (err.message) {
        errorMessage = err.message;
      }

      setFieldErrors(serverErrors);
      setFormValidation(toFormValidation(serverErrors, errorMessage));
    },
  });

  /* ---------- Field change handlers ---------- */

  const handleTextChange = useCallback(
    (e: ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
      const { name, value } = e.target;
      setFormData((prev) => ({ ...prev, [name]: value }));
      if (fieldErrors[name]) {
        setFieldErrors((prev) => {
          const next = { ...prev };
          delete next[name];
          return next;
        });
      }
    },
    [fieldErrors],
  );

  const handleNumberChange = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const { name, value } = e.target;
      const numValue = value === '' ? 0 : parseInt(value, 10);
      setFormData((prev) => ({
        ...prev,
        [name]: isNaN(numValue) ? 0 : numValue,
      }));
      if (fieldErrors[name]) {
        setFieldErrors((prev) => {
          const next = { ...prev };
          delete next[name];
          return next;
        });
      }
    },
    [fieldErrors],
  );

  const handleCheckboxChange = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const { name, checked } = e.target;
      setFormData((prev) => ({ ...prev, [name]: checked }));
    },
    [],
  );

  /* ---------- Form submission ---------- */

  const handleSubmit = useCallback(
    (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();

      /* Client-side validation matching SmtpInternalService rules */
      const errors = validateSmtpService(formData);

      if (Object.keys(errors).length > 0) {
        setFieldErrors(errors);
        setFormValidation(toFormValidation(errors));
        return;
      }

      /* Clear previous errors and submit */
      setFieldErrors({});
      setFormValidation({ errors: [] });
      mutate(formData);
    },
    [formData, mutate],
  );

  /* ---------- Cancel navigation ---------- */

  const handleCancel = useCallback(() => {
    navigate('/notifications/smtp');
  }, [navigate]);

  /* ---------- Shared Tailwind class sets ---------- */

  const labelCls =
    'block text-sm font-medium text-gray-700 mb-1';
  const inputCls =
    'block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500';
  const inputErrCls =
    'block w-full rounded-md border border-red-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 shadow-sm focus:border-red-500 focus:outline-none focus:ring-1 focus:ring-red-500';
  const helpCls = 'mt-1 text-xs text-gray-500';
  const errCls = 'mt-1 text-xs text-red-600';
  const sectionCls =
    'rounded-lg border border-gray-200 bg-white p-6 shadow-sm';
  const sectionTitleCls = 'mb-4 text-base font-semibold text-gray-900';

  /* ---------- Render ---------- */

  return (
    <div className="mx-auto max-w-3xl px-4 py-6 sm:px-6 lg:px-8">
      {/* Page header */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">
          Create SMTP Service
        </h1>
        <p className="mt-1 text-sm text-gray-600">
          Configure a new SMTP service for sending email notifications.
        </p>
      </div>

      {/* Server-side error banner */}
      {isError && error && (
        <div
          className="mb-6 rounded-md border border-red-200 bg-red-50 p-4"
          role="alert"
        >
          <p className="text-sm text-red-700">{error.message}</p>
        </div>
      )}

      {/* Brief success indicator (shown before navigation completes) */}
      {isSuccess && (
        <div
          className="mb-6 rounded-md border border-green-200 bg-green-50 p-4"
          role="status"
        >
          <p className="text-sm text-green-700">
            SMTP service created successfully. Redirecting&hellip;
          </p>
        </div>
      )}

      <DynamicForm
        id="smtp-service-create-form"
        name="smtpServiceCreateForm"
        labelMode="stacked"
        fieldMode="form"
        showValidation={
          Object.keys(fieldErrors).length > 0 || isError
        }
        validation={formValidation}
        className="space-y-6"
        onSubmit={handleSubmit}
      >
        {/* ========== Section 1: Basic Configuration ========== */}
        <section className={sectionCls} aria-labelledby="section-basic">
          <h2 id="section-basic" className={sectionTitleCls}>
            Basic Configuration
          </h2>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Name */}
            <div className="sm:col-span-2">
              <label htmlFor="smtp-name" className={labelCls}>
                Name{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                type="text"
                id="smtp-name"
                name="name"
                value={formData.name}
                onChange={handleTextChange}
                className={fieldErrors.name ? inputErrCls : inputCls}
                placeholder="smtp service"
                maxLength={100}
                required
                disabled={isPending}
                aria-required="true"
                aria-invalid={!!fieldErrors.name}
                aria-describedby={
                  fieldErrors.name ? 'smtp-name-error' : undefined
                }
              />
              {fieldErrors.name && (
                <p
                  id="smtp-name-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.name}
                </p>
              )}
            </div>

            {/* Server */}
            <div>
              <label htmlFor="smtp-server" className={labelCls}>
                Server{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                type="text"
                id="smtp-server"
                name="server"
                value={formData.server}
                onChange={handleTextChange}
                className={fieldErrors.server ? inputErrCls : inputCls}
                placeholder="smtp.domain.com"
                required
                disabled={isPending}
                aria-required="true"
                aria-invalid={!!fieldErrors.server}
                aria-describedby={`smtp-server-help${
                  fieldErrors.server ? ' smtp-server-error' : ''
                }`}
              />
              <p id="smtp-server-help" className={helpCls}>
                Domain name or IP address of the SMTP server
              </p>
              {fieldErrors.server && (
                <p
                  id="smtp-server-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.server}
                </p>
              )}
            </div>

            {/* Port */}
            <div>
              <label htmlFor="smtp-port" className={labelCls}>
                Port{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                type="number"
                id="smtp-port"
                name="port"
                value={formData.port}
                onChange={handleNumberChange}
                className={fieldErrors.port ? inputErrCls : inputCls}
                min={1}
                max={65025}
                required
                disabled={isPending}
                aria-required="true"
                aria-invalid={!!fieldErrors.port}
                aria-describedby={`smtp-port-help${
                  fieldErrors.port ? ' smtp-port-error' : ''
                }`}
              />
              <p id="smtp-port-help" className={helpCls}>
                Common ports: 25 (SMTP), 465 (SMTPS), 587 (Submission)
              </p>
              {fieldErrors.port && (
                <p
                  id="smtp-port-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.port}
                </p>
              )}
            </div>
          </div>
        </section>

        {/* ========== Section 2: Authentication ========== */}
        <section className={sectionCls} aria-labelledby="section-auth">
          <h2 id="section-auth" className={sectionTitleCls}>
            Authentication
          </h2>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Username */}
            <div>
              <label htmlFor="smtp-username" className={labelCls}>
                Username
              </label>
              <input
                type="text"
                id="smtp-username"
                name="username"
                value={formData.username}
                onChange={handleTextChange}
                className={inputCls}
                disabled={isPending}
                aria-describedby="smtp-username-help"
              />
              <p id="smtp-username-help" className={helpCls}>
                Only if SMTP server requires authentication
              </p>
            </div>

            {/* Password */}
            <div>
              <label htmlFor="smtp-password" className={labelCls}>
                Password
              </label>
              <input
                type="password"
                id="smtp-password"
                name="password"
                value={formData.password}
                onChange={handleTextChange}
                className={inputCls}
                disabled={isPending}
                autoComplete="new-password"
                aria-describedby="smtp-password-help"
              />
              <p id="smtp-password-help" className={helpCls}>
                Only if SMTP server requires authentication
              </p>
            </div>
          </div>
        </section>

        {/* ========== Section 3: Connection Security ========== */}
        <section
          className={sectionCls}
          aria-labelledby="section-security"
        >
          <h2 id="section-security" className={sectionTitleCls}>
            Connection Security
          </h2>
          <div>
            <label
              htmlFor="smtp-connection-security"
              className={labelCls}
            >
              Connection Security{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <select
              id="smtp-connection-security"
              name="connection_security"
              value={formData.connection_security}
              onChange={handleTextChange}
              className={
                fieldErrors.connection_security
                  ? inputErrCls
                  : inputCls
              }
              required
              disabled={isPending}
              aria-required="true"
              aria-invalid={!!fieldErrors.connection_security}
              aria-describedby={
                fieldErrors.connection_security
                  ? 'smtp-security-error'
                  : undefined
              }
            >
              {CONNECTION_SECURITY_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
            {fieldErrors.connection_security && (
              <p
                id="smtp-security-error"
                className={errCls}
                role="alert"
              >
                {fieldErrors.connection_security}
              </p>
            )}
          </div>
        </section>

        {/* ========== Section 4: Default Sender ========== */}
        <section
          className={sectionCls}
          aria-labelledby="section-sender"
        >
          <h2 id="section-sender" className={sectionTitleCls}>
            Default Sender
          </h2>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Default Sender Name */}
            <div className="sm:col-span-2">
              <label
                htmlFor="smtp-default-sender-name"
                className={labelCls}
              >
                Default Sender Name
              </label>
              <input
                type="text"
                id="smtp-default-sender-name"
                name="default_sender_name"
                value={formData.default_sender_name}
                onChange={handleTextChange}
                className={inputCls}
                disabled={isPending}
              />
            </div>

            {/* Default Sender Email */}
            <div>
              <label
                htmlFor="smtp-default-sender-email"
                className={labelCls}
              >
                Default Sender Email{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                type="email"
                id="smtp-default-sender-email"
                name="default_sender_email"
                value={formData.default_sender_email}
                onChange={handleTextChange}
                className={
                  fieldErrors.default_sender_email
                    ? inputErrCls
                    : inputCls
                }
                placeholder="sender@example.com"
                required
                disabled={isPending}
                aria-required="true"
                aria-invalid={!!fieldErrors.default_sender_email}
                aria-describedby={
                  fieldErrors.default_sender_email
                    ? 'smtp-sender-email-error'
                    : undefined
                }
              />
              {fieldErrors.default_sender_email && (
                <p
                  id="smtp-sender-email-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.default_sender_email}
                </p>
              )}
            </div>

            {/* Default Reply-To Email */}
            <div>
              <label
                htmlFor="smtp-default-reply-to"
                className={labelCls}
              >
                Default Reply-To Email
              </label>
              <input
                type="email"
                id="smtp-default-reply-to"
                name="default_reply_to_email"
                value={formData.default_reply_to_email}
                onChange={handleTextChange}
                className={
                  fieldErrors.default_reply_to_email
                    ? inputErrCls
                    : inputCls
                }
                disabled={isPending}
                aria-invalid={!!fieldErrors.default_reply_to_email}
                aria-describedby={
                  fieldErrors.default_reply_to_email
                    ? 'smtp-reply-to-error'
                    : undefined
                }
              />
              {fieldErrors.default_reply_to_email && (
                <p
                  id="smtp-reply-to-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.default_reply_to_email}
                </p>
              )}
            </div>
          </div>
        </section>

        {/* ========== Section 5: Retry Configuration ========== */}
        <section
          className={sectionCls}
          aria-labelledby="section-retry"
        >
          <h2 id="section-retry" className={sectionTitleCls}>
            Retry Configuration
          </h2>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            {/* Max Retries Count */}
            <div>
              <label htmlFor="smtp-max-retries" className={labelCls}>
                Max Retries Count{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                type="number"
                id="smtp-max-retries"
                name="max_retries_count"
                value={formData.max_retries_count}
                onChange={handleNumberChange}
                className={
                  fieldErrors.max_retries_count
                    ? inputErrCls
                    : inputCls
                }
                min={1}
                max={10}
                required
                disabled={isPending}
                aria-required="true"
                aria-invalid={!!fieldErrors.max_retries_count}
                aria-describedby={`smtp-retries-help${
                  fieldErrors.max_retries_count
                    ? ' smtp-retries-error'
                    : ''
                }`}
              />
              <p id="smtp-retries-help" className={helpCls}>
                Number of retry attempts (1–10)
              </p>
              {fieldErrors.max_retries_count && (
                <p
                  id="smtp-retries-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.max_retries_count}
                </p>
              )}
            </div>

            {/* Retry Wait Minutes */}
            <div>
              <label htmlFor="smtp-retry-wait" className={labelCls}>
                Retry Wait Minutes{' '}
                <span className="text-red-500" aria-hidden="true">
                  *
                </span>
              </label>
              <input
                type="number"
                id="smtp-retry-wait"
                name="retry_wait_minutes"
                value={formData.retry_wait_minutes}
                onChange={handleNumberChange}
                className={
                  fieldErrors.retry_wait_minutes
                    ? inputErrCls
                    : inputCls
                }
                min={1}
                max={1440}
                required
                disabled={isPending}
                aria-required="true"
                aria-invalid={!!fieldErrors.retry_wait_minutes}
                aria-describedby={`smtp-wait-help${
                  fieldErrors.retry_wait_minutes
                    ? ' smtp-wait-error'
                    : ''
                }`}
              />
              <p id="smtp-wait-help" className={helpCls}>
                Minutes to wait between retries (1–1440)
              </p>
              {fieldErrors.retry_wait_minutes && (
                <p
                  id="smtp-wait-error"
                  className={errCls}
                  role="alert"
                >
                  {fieldErrors.retry_wait_minutes}
                </p>
              )}
            </div>
          </div>
        </section>

        {/* ========== Section 6: Service Status ========== */}
        <section
          className={sectionCls}
          aria-labelledby="section-status"
        >
          <h2 id="section-status" className={sectionTitleCls}>
            Service Status
          </h2>
          <div className="space-y-4">
            {/* Is Enabled */}
            <div className="flex items-start gap-3">
              <input
                type="checkbox"
                id="smtp-is-enabled"
                name="is_enabled"
                checked={formData.is_enabled}
                onChange={handleCheckboxChange}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                disabled={isPending}
              />
              <div>
                <label
                  htmlFor="smtp-is-enabled"
                  className="text-sm font-medium text-gray-700"
                >
                  Enabled
                </label>
                <p className={helpCls}>
                  When enabled, this service can be used to send emails
                </p>
              </div>
            </div>

            {/* Is Default */}
            <div className="flex items-start gap-3">
              <input
                type="checkbox"
                id="smtp-is-default"
                name="is_default"
                checked={formData.is_default}
                onChange={handleCheckboxChange}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                disabled={isPending}
              />
              <div>
                <label
                  htmlFor="smtp-is-default"
                  className="text-sm font-medium text-gray-700"
                >
                  Default Service
                </label>
                <p className={helpCls}>
                  Setting this as default will remove the default flag
                  from any other SMTP service. There should always be
                  one default service active.
                </p>
              </div>
            </div>
          </div>
        </section>

        {/* ========== Action Buttons ========== */}
        <div className="flex items-center justify-end gap-3 pt-2">
          <button
            type="button"
            onClick={handleCancel}
            className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            disabled={isPending}
          >
            Cancel
          </button>
          <button
            type="submit"
            className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
            disabled={isPending}
          >
            {isPending ? 'Creating\u2026' : 'Create SMTP Service'}
          </button>
        </div>
      </DynamicForm>
    </div>
  );
}
