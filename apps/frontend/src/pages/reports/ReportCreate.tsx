// =============================================================================
// ReportCreate.tsx — Create Custom Report/Dashboard Configuration Page
//
// Replaces the monolith's SDK data source creation page:
//   - Source: WebVella.Erp.Plugins.SDK/Pages/data_source/create.cshtml[.cs]
//   - Route:  /sdk/objects/data_source/c/create  →  /reports/create
//
// Provides a complete form for creating new report/dashboard configurations
// against the Reporting service (POST /v1/reporting/dashboards). Preserves
// full behavioral parity with the monolith's DataSourceManager.Create() flow
// including client-side validation, server-side EQL parsing error mapping,
// parameter format validation, and test execution capabilities.
// =============================================================================

import React, { useState, useCallback } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router-dom';
import { post } from '../../api/client';
import type { ApiError } from '../../api/client';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import type {
  DatabaseDataSource,
  DataSourceParameter,
  DataSourceBase,
} from '../../types/datasource';

// ─── Types ───────────────────────────────────────────────────────────────────

/**
 * Payload shape for creating a new report dashboard via POST /v1/reporting/dashboards.
 * Maps directly to the monolith's DataSourceManager.Create() parameters:
 *   name, description, weight, eqlText, parameters (parsed), returnTotal, resultModel.
 */
interface CreateDashboardPayload {
  name: DataSourceBase['name'];
  description: DataSourceBase['description'];
  weight: DataSourceBase['weight'];
  eqlText: DatabaseDataSource['eqlText'];
  parameters: DataSourceBase['parameters'];
  returnTotal: DataSourceBase['returnTotal'];
  resultModel: DataSourceBase['resultModel'];
}

// ─── Constants ───────────────────────────────────────────────────────────────

/**
 * Default result model matching the monolith's read-only "EntityRecordList" value.
 * This is a fixed value — users cannot change the result model for database data sources.
 */
const DEFAULT_RESULT_MODEL: DataSourceBase['resultModel'] = 'EntityRecordList';

/**
 * Sample EQL snippets matching the monolith's inline helper scripts injected
 * in create.cshtml via the wv-datasource-manage StencilJS component.
 */
const SAMPLE_SNIPPETS: ReadonlyArray<{
  label: string;
  eql: string;
  params: string;
}> = [
  {
    label: 'Record Details',
    eql: 'SELECT * FROM entity WHERE id = @recordId',
    params: 'recordId,guid,,false',
  },
  {
    label: 'Record List',
    eql: 'SELECT * FROM entity ORDER BY sort_order ASC PAGE @page PAGESIZE @pageSize',
    params: 'page,int,1,false\npageSize,int,10,false',
  },
];

// ─── Helper Functions ────────────────────────────────────────────────────────

/**
 * Parses newline-separated parameter defaults text into typed DataSourceParameter
 * objects. Each non-empty line must follow: name,type,value[,ignoreParseErrors].
 *
 * Mirrors DataSourceManager.ProcessParametersText() from the monolith, which
 * splits the raw param text by newline and parses comma-separated segments.
 */
function parseParameterDefaults(text: string): DataSourceParameter[] {
  if (!text || !text.trim()) {
    return [];
  }

  const lines = text.split('\n').filter((line) => line.trim().length > 0);
  const parameters: DataSourceParameter[] = [];

  for (const line of lines) {
    const parts = line.split(',').map((p) => p.trim());
    if (parts.length >= 3) {
      const param: DataSourceParameter = {
        name: parts[0],
        type: parts[1],
        value: parts[2],
        ignoreParseErrors:
          parts.length >= 4 && parts[3].toLowerCase() === 'true',
      };
      parameters.push(param);
    }
  }

  return parameters;
}

/**
 * Validates the format of parameter defaults text. Returns an error message
 * string if invalid, or null if the text is valid (including empty).
 *
 * Validation rules match the monolith's DataSourceManager expectations:
 *   - Each non-empty line must have at least 3 comma-separated values
 *   - Parameter name cannot be empty
 *   - Parameter type cannot be empty
 *   - Optional 4th value (ignoreParseErrors) must be "true" or "false"
 */
function validateParameterFormat(text: string): string | null {
  if (!text || !text.trim()) {
    return null;
  }

  const lines = text.split('\n').filter((line) => line.trim().length > 0);

  for (let i = 0; i < lines.length; i++) {
    const parts = lines[i].split(',').map((p) => p.trim());

    if (parts.length < 3) {
      return `Line ${i + 1}: Expected at least 3 comma-separated values (name,type,value), found ${parts.length}.`;
    }

    if (!parts[0]) {
      return `Line ${i + 1}: Parameter name cannot be empty.`;
    }

    if (!parts[1]) {
      return `Line ${i + 1}: Parameter type cannot be empty.`;
    }

    if (
      parts.length >= 4 &&
      parts[3] !== '' &&
      parts[3].toLowerCase() !== 'true' &&
      parts[3].toLowerCase() !== 'false'
    ) {
      return `Line ${i + 1}: ignoreParseErrors must be "true" or "false", got "${parts[3]}".`;
    }
  }

  return null;
}

// ─── Component ───────────────────────────────────────────────────────────────

/**
 * ReportCreate — Page component for creating a new report/dashboard configuration.
 *
 * Replaces the monolith's SDK data source creation page. Form fields include:
 *   - Name (required text input)
 *   - Description (optional text input)
 *   - Result Model (read-only, always "EntityRecordList")
 *   - Weight (required number input, ordering priority)
 *   - Return Total (boolean checkbox toggle)
 *   - Query Configuration / EQL (required, full-width code textarea)
 *   - Parameter Defaults (newline-separated name,type,value[,ignoreParseErrors])
 *
 * Submits via TanStack Query useMutation to POST /v1/reporting/dashboards.
 * On success, navigates to /reports/view/:id. On error, maps server-side
 * validation errors (including EQL parsing errors with line/column info)
 * to per-field error display via DynamicForm's FormValidation.
 */
function ReportCreate(): React.JSX.Element {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  // ── Form Field State ──────────────────────────────────────────────
  const [name, setName] = useState<DataSourceBase['name']>('');
  const [description, setDescription] = useState<DataSourceBase['description']>('');
  const [weight, setWeight] = useState<DataSourceBase['weight']>(0);
  const [returnTotal, setReturnTotal] = useState<DataSourceBase['returnTotal']>(true);
  const [queryConfiguration, setQueryConfiguration] = useState<DatabaseDataSource['eqlText']>('');
  const [parameterDefaults, setParameterDefaults] = useState<string>('');

  // ── Validation State ──────────────────────────────────────────────
  const [validation, setValidation] = useState<FormValidation>({ errors: [] });

  // Clear a specific field-level error when the user starts editing that field.
  // If no errors remain after removal, also clear the summary message.
  const clearFieldError = useCallback((propertyName: string) => {
    setValidation((prev) => {
      if (!prev.errors.some((e) => e.propertyName === propertyName)) return prev;
      const filtered = prev.errors.filter((e) => e.propertyName !== propertyName);
      return {
        message: filtered.length > 0 ? prev.message : undefined,
        errors: filtered,
      };
    });
  }, []);

  // ── Test Execution State ──────────────────────────────────────────
  const [testSqlResult, setTestSqlResult] = useState<DatabaseDataSource['sqlText']>('');
  const [testDataResult, setTestDataResult] = useState<string>('');
  const [isTestPanelOpen, setIsTestPanelOpen] = useState<boolean>(false);

  // ── Create Dashboard Mutation ─────────────────────────────────────
  const createMutation = useMutation({
    mutationFn: async (payload: CreateDashboardPayload) => {
      return post<DatabaseDataSource>('/reporting/dashboards', payload);
    },
    onSuccess: (data) => {
      // Invalidate dashboard list cache so the list page reflects the new entry
      queryClient.invalidateQueries({ queryKey: ['reporting', 'dashboards'] });

      // Navigate to the newly created dashboard view page
      const createdId = data.object?.id;
      if (createdId) {
        navigate(`/reports/view/${createdId}`);
      } else {
        // Fallback to reports list if ID is not returned
        navigate('/reports');
      }
    },
    onError: (error: Error) => {
      // Map API errors to FormValidation for DynamicForm's ValidationSummary display.
      // The API client interceptor wraps all errors as ApiError with { message, errors[], status }.
      const apiErr = error as unknown as ApiError;
      const formErrors: ValidationError[] = [];

      if (apiErr.errors && Array.isArray(apiErr.errors)) {
        for (const errItem of apiErr.errors) {
          formErrors.push({
            propertyName: errItem.key || '',
            message: errItem.message,
          });
        }
      }

      setValidation({
        message: apiErr.message || 'Failed to create report dashboard.',
        errors: formErrors,
      });
    },
  });

  // ── Test Query Mutation ───────────────────────────────────────────
  // Mirrors the monolith's "Get SQL" and "Sample JSON" modals that POST
  // to /api/v3.0/datasource/test with {action, eql, parameters}.
  const testQueryMutation = useMutation({
    mutationFn: async (action: 'sql' | 'data') => {
      const parsedParams: DataSourceParameter[] = parseParameterDefaults(parameterDefaults);
      return post<{ result: string }>('/reporting/dashboards/test', {
        action,
        eql: queryConfiguration,
        parameters: parameterDefaults,
        paramList: parsedParams,
        returnTotal,
      });
    },
    onSuccess: (data, action) => {
      const result =
        data.object?.result || JSON.stringify(data.object, null, 2) || '';
      if (action === 'sql') {
        setTestSqlResult(result);
      } else {
        setTestDataResult(result);
      }
    },
    onError: (error: Error) => {
      const apiErr = error as unknown as ApiError;
      const errorMsg = apiErr.message || 'Test execution failed.';
      if (testSqlResult !== undefined) {
        setTestSqlResult(errorMsg);
      }
      setTestDataResult(errorMsg);
    },
  });

  // ── Client-Side Validation ────────────────────────────────────────
  // Mirrors DataSourceManager.Create() server-side checks:
  //   - Name required + non-empty
  //   - EQL/query required + non-empty
  //   - Parameter format validation (name,type,value per line)
  //   - Weight must be a valid number
  const validateForm = useCallback((): ValidationError[] => {
    const errors: ValidationError[] = [];

    if (!name.trim()) {
      errors.push({ propertyName: 'name', message: 'Name is required.' });
    }

    if (!queryConfiguration.trim()) {
      errors.push({
        propertyName: 'eqlText',
        message: 'Query configuration is required.',
      });
    }

    const paramError = validateParameterFormat(parameterDefaults);
    if (paramError) {
      errors.push({ propertyName: 'parameterDefaults', message: paramError });
    }

    if (Number.isNaN(weight)) {
      errors.push({
        propertyName: 'weight',
        message: 'Weight must be a valid number.',
      });
    }

    return errors;
  }, [name, queryConfiguration, parameterDefaults, weight]);

  // ── Form Submit Handler ───────────────────────────────────────────
  const handleSubmit = useCallback(
    (_e: React.FormEvent<HTMLFormElement>) => {
      // Clear previous validation state
      setValidation({ errors: [] });

      // Run client-side validation before submitting to server
      const errors = validateForm();
      if (errors.length > 0) {
        setValidation({
          message: 'Please fix the following errors before submitting.',
          errors,
        });
        return;
      }

      // Parse parameter text into structured DataSourceParameter array
      const parameters: DataSourceParameter[] =
        parseParameterDefaults(parameterDefaults);

      // Build payload matching DataSourceManager.Create() parameters
      const payload: CreateDashboardPayload = {
        name: name.trim(),
        description: description.trim(),
        weight,
        eqlText: queryConfiguration.trim(),
        parameters,
        returnTotal,
        resultModel: DEFAULT_RESULT_MODEL,
      };

      createMutation.mutate(payload);
    },
    [
      name,
      description,
      weight,
      queryConfiguration,
      parameterDefaults,
      returnTotal,
      validateForm,
      createMutation,
    ],
  );

  // ── Snippet Insertion Handler ─────────────────────────────────────
  const handleInsertSnippet = useCallback(
    (eql: string, params: string) => {
      setQueryConfiguration((prev) => (prev ? `${prev}\n${eql}` : eql));
      setParameterDefaults((prev) => (prev ? `${prev}\n${params}` : params));
      clearFieldError('eqlText');
      clearFieldError('parameterDefaults');
    },
    [clearFieldError],
  );

  // ── Test Execution Handlers ───────────────────────────────────────
  const handlePreviewQuery = useCallback(() => {
    setIsTestPanelOpen(true);
    setTestDataResult('');
    testQueryMutation.mutate('sql');
  }, [testQueryMutation]);

  const handleSampleData = useCallback(() => {
    setIsTestPanelOpen(true);
    setTestSqlResult('');
    testQueryMutation.mutate('data');
  }, [testQueryMutation]);

  const handleCloseTestPanel = useCallback(() => {
    setIsTestPanelOpen(false);
    setTestSqlResult('');
    setTestDataResult('');
  }, []);

  // ── Derived State ─────────────────────────────────────────────────
  const isSubmitting = createMutation.isPending;
  const isTesting = testQueryMutation.isPending;

  // ── Render ────────────────────────────────────────────────────────
  return (
    <main className="mx-auto max-w-screen-xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ─── Page Header ─────────────────────────────────────────── */}
      <header className="mb-6">
        <div className="flex flex-wrap items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            <Link
              to="/reports"
              className="inline-flex items-center gap-1 text-sm font-medium text-gray-500 transition-colors duration-200 hover:text-gray-700"
              aria-label="Back to reports list"
            >
              <svg
                className="h-5 w-5"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M12.707 5.293a1 1 0 010 1.414L9.414 10l3.293 3.293a1 1 0 01-1.414 1.414l-4-4a1 1 0 010-1.414l4-4a1 1 0 011.414 0z"
                  clipRule="evenodd"
                />
              </svg>
              Reports
            </Link>
            <span className="text-gray-300" aria-hidden="true">
              /
            </span>
            <h1 className="text-2xl font-bold tracking-tight text-gray-900">
              Create Report Dashboard
            </h1>
          </div>

          <div className="flex items-center gap-3">
            <Link
              to="/reports"
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-200 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              Cancel
            </Link>
            <button
              type="submit"
              form="CreateRecord"
              disabled={isSubmitting}
              className="inline-flex items-center gap-2 rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-200 hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
            >
              {isSubmitting ? (
                <>
                  <svg
                    className="h-4 w-4 animate-spin"
                    viewBox="0 0 24 24"
                    fill="none"
                    aria-hidden="true"
                  >
                    <circle
                      className="opacity-25"
                      cx="12"
                      cy="12"
                      r="10"
                      stroke="currentColor"
                      strokeWidth="4"
                    />
                    <path
                      className="opacity-75"
                      fill="currentColor"
                      d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                    />
                  </svg>
                  Creating&hellip;
                </>
              ) : (
                <>
                  <svg
                    className="h-4 w-4"
                    viewBox="0 0 20 20"
                    fill="currentColor"
                    aria-hidden="true"
                  >
                    <path d="M10 3a1 1 0 011 1v5h5a1 1 0 110 2h-5v5a1 1 0 11-2 0v-5H4a1 1 0 110-2h5V4a1 1 0 011-1z" />
                  </svg>
                  Create Dashboard
                </>
              )}
            </button>
          </div>
        </div>
      </header>

      {/* ─── Type Indicator Card ──────────────────────────────────── */}
      <section
        className="mb-6 rounded-lg border border-gray-200 bg-white p-4 shadow-sm"
        aria-label="Data source type"
      >
        <div className="flex items-center gap-4">
          <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-lg bg-purple-100">
            <svg
              className="h-6 w-6 text-purple-600"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <ellipse cx="12" cy="5" rx="9" ry="3" />
              <path d="M21 12c0 1.66-4 3-9 3s-9-1.34-9-3" />
              <path d="M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5" />
            </svg>
          </div>
          <div>
            <h2 className="text-base font-semibold text-gray-900">Database</h2>
            <p className="text-sm text-gray-500">
              SQL Select via EQL syntax
            </p>
          </div>
        </div>
      </section>

      {/* ─── Form Section ─────────────────────────────────────────── */}
      <section className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
        <DynamicForm
          id="CreateRecord"
          name="CreateRecord"
          labelMode="stacked"
          fieldMode="form"
          validation={validation}
          onSubmit={handleSubmit}
          className="space-y-6"
        >
          {/* ── Row 1: Name + Description ────────────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            <div>
              <label
                htmlFor="ds-name"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Name{' '}
                <span className="text-red-500" aria-label="required">
                  *
                </span>
              </label>
              <input
                id="ds-name"
                type="text"
                value={name}
                onChange={(e) => {
                  setName(e.target.value);
                  clearFieldError('name');
                }}
                required
                autoComplete="off"
                placeholder="Enter data source name"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                aria-describedby={
                  validation.errors.some((err) => err.propertyName === 'name')
                    ? 'ds-name-error'
                    : undefined
                }
                aria-invalid={
                  validation.errors.some((err) => err.propertyName === 'name')
                    ? true
                    : undefined
                }
              />
              {validation.errors.find((err) => err.propertyName === 'name') && (
                <p
                  id="ds-name-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {
                    validation.errors.find(
                      (err) => err.propertyName === 'name',
                    )?.message
                  }
                </p>
              )}
            </div>

            <div>
              <label
                htmlFor="ds-description"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Description
              </label>
              <input
                id="ds-description"
                type="text"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                autoComplete="off"
                placeholder="Enter description"
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              />
            </div>
          </div>

          {/* ── Row 2: Model (read-only) + Weight ────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            <div>
              <label
                htmlFor="ds-model"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Result Model
              </label>
              <input
                id="ds-model"
                type="text"
                value={DEFAULT_RESULT_MODEL}
                readOnly
                tabIndex={-1}
                className="block w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm"
                aria-readonly="true"
              />
              <p className="mt-1 text-xs text-gray-400">
                Read-only — determines the shape of returned data.
              </p>
            </div>

            <div>
              <label
                htmlFor="ds-weight"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Weight{' '}
                <span className="text-red-500" aria-label="required">
                  *
                </span>
              </label>
              <input
                id="ds-weight"
                type="number"
                value={weight}
                onChange={(e) => {
                  setWeight(parseInt(e.target.value, 10) || 0);
                  clearFieldError('weight');
                }}
                required
                min={0}
                className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                aria-describedby={
                  validation.errors.some(
                    (err) => err.propertyName === 'weight',
                  )
                    ? 'ds-weight-error'
                    : undefined
                }
                aria-invalid={
                  validation.errors.some(
                    (err) => err.propertyName === 'weight',
                  )
                    ? true
                    : undefined
                }
              />
              {validation.errors.find(
                (err) => err.propertyName === 'weight',
              ) && (
                <p
                  id="ds-weight-error"
                  className="mt-1 text-sm text-red-600"
                  role="alert"
                >
                  {
                    validation.errors.find(
                      (err) => err.propertyName === 'weight',
                    )?.message
                  }
                </p>
              )}
            </div>
          </div>

          {/* ── Row 3: Return Total ──────────────────────────────── */}
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            <div className="flex items-start gap-3 pt-1">
              <input
                id="ds-returnTotal"
                type="checkbox"
                checked={returnTotal}
                onChange={(e) => setReturnTotal(e.target.checked)}
                className="mt-0.5 h-4 w-4 rounded border-gray-300 text-indigo-600 focus:ring-indigo-500"
              />
              <div>
                <label
                  htmlFor="ds-returnTotal"
                  className="text-sm font-medium text-gray-700"
                >
                  Return Total Count
                </label>
                <p className="text-xs text-gray-500">
                  When enabled, includes the total record count in query
                  results for pagination support.
                </p>
              </div>
            </div>
          </div>

          {/* ── Row 4: Query Configuration (EQL Editor) ──────────── */}
          <div>
            <label
              htmlFor="ds-eql"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Query Configuration (EQL){' '}
              <span className="text-red-500" aria-label="required">
                *
              </span>
            </label>
            <textarea
              id="ds-eql"
              value={queryConfiguration}
              onChange={(e) => {
                setQueryConfiguration(e.target.value);
                clearFieldError('eqlText');
              }}
              required
              rows={10}
              spellCheck={false}
              placeholder={'SELECT * FROM entity WHERE id = @recordId'}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm shadow-sm transition-colors placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              aria-describedby="ds-eql-help"
              aria-invalid={
                validation.errors.some(
                  (err) => err.propertyName === 'eqlText',
                )
                  ? true
                  : undefined
              }
            />
            {validation.errors.find(
              (err) => err.propertyName === 'eqlText',
            ) && (
              <p
                id="ds-eql-error"
                className="mt-1 text-sm text-red-600"
                role="alert"
              >
                {
                  validation.errors.find(
                    (err) => err.propertyName === 'eqlText',
                  )?.message
                }
              </p>
            )}
            <p id="ds-eql-help" className="mt-1 text-xs text-gray-500">
              Enter an EQL (Entity Query Language) SELECT statement. Use
              @paramName for parameterized values.
            </p>

            {/* Sample Snippet Buttons */}
            <div className="mt-2 flex flex-wrap gap-2">
              {SAMPLE_SNIPPETS.map((snippet) => (
                <button
                  key={snippet.label}
                  type="button"
                  onClick={() =>
                    handleInsertSnippet(snippet.eql, snippet.params)
                  }
                  className="inline-flex items-center rounded border border-gray-200 bg-gray-50 px-2.5 py-1 text-xs font-medium text-gray-600 transition-colors hover:bg-gray-100 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-indigo-500"
                >
                  <svg
                    className="mr-1 h-3 w-3"
                    viewBox="0 0 20 20"
                    fill="currentColor"
                    aria-hidden="true"
                  >
                    <path d="M10 3a1 1 0 011 1v5h5a1 1 0 110 2h-5v5a1 1 0 11-2 0v-5H4a1 1 0 110-2h5V4a1 1 0 011-1z" />
                  </svg>
                  {snippet.label}
                </button>
              ))}
            </div>
          </div>

          {/* ── Row 5: Parameter Defaults ─────────────────────────── */}
          <div>
            <label
              htmlFor="ds-params"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Parameter Defaults
            </label>
            <textarea
              id="ds-params"
              value={parameterDefaults}
              onChange={(e) => {
                setParameterDefaults(e.target.value);
                clearFieldError('parameterDefaults');
              }}
              rows={5}
              spellCheck={false}
              placeholder={'recordId,guid,,false\npage,int,1,false'}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm shadow-sm transition-colors placeholder:text-gray-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              aria-describedby="ds-params-help"
              aria-invalid={
                validation.errors.some(
                  (err) => err.propertyName === 'parameterDefaults',
                )
                  ? true
                  : undefined
              }
            />
            {validation.errors.find(
              (err) => err.propertyName === 'parameterDefaults',
            ) && (
              <p
                id="ds-params-error"
                className="mt-1 text-sm text-red-600"
                role="alert"
              >
                {
                  validation.errors.find(
                    (err) => err.propertyName === 'parameterDefaults',
                  )?.message
                }
              </p>
            )}
            <p id="ds-params-help" className="mt-1 text-xs text-gray-500">
              One parameter per line in the format:{' '}
              <code className="rounded bg-gray-100 px-1 py-0.5 text-xs">
                name,type,value[,ignoreParseErrors]
              </code>
              . Example:{' '}
              <code className="rounded bg-gray-100 px-1 py-0.5 text-xs">
                recordId,guid,,false
              </code>
            </p>
          </div>

          {/* ── Test Execution Panel ──────────────────────────────── */}
          <div className="border-t border-gray-200 pt-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h3 className="text-sm font-medium text-gray-700">
                Test Execution
              </h3>
              <div className="flex gap-2">
                <button
                  type="button"
                  onClick={handlePreviewQuery}
                  disabled={!queryConfiguration.trim() || isTesting}
                  className="inline-flex items-center rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {isTesting ? 'Running\u2026' : 'Preview Query'}
                </button>
                <button
                  type="button"
                  onClick={handleSampleData}
                  disabled={!queryConfiguration.trim() || isTesting}
                  className="inline-flex items-center rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  {isTesting ? 'Running\u2026' : 'Sample Data'}
                </button>
              </div>
            </div>

            {isTestPanelOpen && (testSqlResult || testDataResult) && (
              <div className="mt-3 space-y-3">
                {testSqlResult && (
                  <div>
                    <h4 className="mb-1 text-xs font-medium text-gray-500">
                      Generated SQL
                    </h4>
                    <pre className="max-h-48 overflow-auto rounded-md border border-gray-200 bg-gray-50 p-3 font-mono text-xs text-gray-800">
                      {testSqlResult}
                    </pre>
                  </div>
                )}
                {testDataResult && (
                  <div>
                    <h4 className="mb-1 text-xs font-medium text-gray-500">
                      Sample JSON
                    </h4>
                    <pre className="max-h-64 overflow-auto rounded-md border border-gray-200 bg-gray-50 p-3 font-mono text-xs text-gray-800">
                      {testDataResult}
                    </pre>
                  </div>
                )}
                <button
                  type="button"
                  onClick={handleCloseTestPanel}
                  className="text-xs text-gray-500 underline transition-colors hover:text-gray-700 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-indigo-500"
                >
                  Close test results
                </button>
              </div>
            )}
          </div>
        </DynamicForm>
      </section>
    </main>
  );
}

export default ReportCreate;
