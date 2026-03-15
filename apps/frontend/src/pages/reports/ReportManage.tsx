/**
 * ReportManage.tsx — Edit Report Configuration Page
 *
 * Replaces the monolith's manage.cshtml / manage.cshtml.cs data source
 * management page (route /sdk/objects/data_source/m/{RecordId}/manage).
 * Provides a complete edit form for existing report/dashboard
 * configurations with EQL query editing, parameter management, test
 * execution, dirty-state tracking, and comprehensive validation.
 *
 * Route: /reports/manage/:id
 * Sources:
 *   - WebVella.Erp.Plugins.SDK/Pages/data_source/manage.cshtml.cs
 *   - WebVella.Erp.Plugins.SDK/Pages/data_source/manage.cshtml
 *   - WebVella.Erp/Api/DataSourceManager.cs (Update, lines 191-265)
 */

import { useState, useEffect, useCallback, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useParams, useNavigate, Link } from 'react-router-dom';

import { get, put, post } from '../../api/client';
import type { ApiResponse, ApiError } from '../../api/client';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';
import type {
  DatabaseDataSource,
  DataSourceParameter,
  DataSourceBase,
} from '../../types/datasource';

/* ================================================================
 * LOCAL TYPES
 * ================================================================ */

/** Shape of the edit form local state. */
interface ReportFormState {
  name: string;
  description: string;
  weight: number;
  returnTotal: boolean;
  queryConfiguration: string;
  parameterDefaults: string;
  resultModel: string;
}

/** Payload sent to the PUT endpoint (mirrors monolith OnPost body). */
interface UpdateDashboardPayload {
  name: string;
  description: string;
  weight: number;
  queryConfiguration: string;
  parameterDefaults: string;
  returnTotal: boolean;
}

/** Payload for the test execution endpoint (mirrors monolith /api/v3.0/datasource/test). */
interface TestExecutionPayload {
  action: 'sql' | 'data';
  eql: string;
  parameters: string;
  returnTotal: boolean;
}

/* ================================================================
 * QUERY KEY CONSTANTS
 * ================================================================ */

const QUERY_KEY_PREFIX = 'reporting';
const DASHBOARDS_KEY = 'dashboards';

/* ================================================================
 * HELPER FUNCTIONS
 * ================================================================ */

/**
 * Serializes a DataSourceParameter array into the monolith's
 * `{Name},{Type},{Value}` per-line text format.
 *
 * Source: manage.cshtml.cs OnGet —
 *   `paramLine += $"{par.Name},{par.Type},{par.Value}";`
 */
function serializeParameters(params: DataSourceParameter[] | undefined): string {
  if (!params || params.length === 0) {
    return '';
  }
  return params
    .map((p) => {
      const parts = [p.name ?? '', p.type ?? '', p.value ?? ''];
      if (p.ignoreParseErrors) {
        parts.push('true');
      }
      return parts.join(',');
    })
    .join('\n');
}

/**
 * Validates the parameter defaults text format.
 * Each non-empty line must have at least `name,type,value`.
 * Returns an error message string or null if valid.
 *
 * Source: DataSourceManager.cs lines 138-152 — splits by \n, each
 * line split by comma expecting >= 3 parts.
 */
function validateParameterFormat(text: string): string | null {
  if (!text.trim()) {
    return null;
  }
  const lines = text.split('\n').filter((line) => line.trim().length > 0);
  for (let i = 0; i < lines.length; i++) {
    const parts = lines[i].split(',');
    if (parts.length < 3) {
      return `Line ${i + 1}: Each parameter must have at least name,type,value (found ${parts.length} part${parts.length === 1 ? '' : 's'}).`;
    }
    if (!parts[0].trim()) {
      return `Line ${i + 1}: Parameter name cannot be empty.`;
    }
    if (!parts[1].trim()) {
      return `Line ${i + 1}: Parameter type cannot be empty.`;
    }
  }
  return null;
}

/** Builds the initial empty form state before data arrives. */
function createEmptyFormState(): ReportFormState {
  return {
    name: '',
    description: '',
    weight: 0,
    returnTotal: true,
    queryConfiguration: '',
    parameterDefaults: '',
    resultModel: 'EntityRecordList',
  };
}

/**
 * Extracts the display name from a DataSourceBase-shaped object.
 * Used in the page header to show the dashboard name dynamically.
 */
function extractDisplayName(ds: DataSourceBase): string {
  return ds.name || 'Unnamed Dashboard';
}

/**
 * Populates form state from a fetched DatabaseDataSource.
 * Mirrors the monolith's OnGet pre-population logic.
 *
 * Source: manage.cshtml.cs OnGet — reads Name, Description,
 * Weight, ReturnTotal, EqlText, ResultModel, and serializes
 * Parameters into ParamDefaults.
 */
function populateFormState(ds: DatabaseDataSource): ReportFormState {
  return {
    name: ds.name ?? '',
    description: ds.description ?? '',
    weight: ds.weight ?? 0,
    returnTotal: ds.returnTotal ?? true,
    queryConfiguration: ds.eqlText ?? '',
    parameterDefaults: serializeParameters(ds.parameters),
    resultModel: ds.resultModel ?? 'EntityRecordList',
  };
}

/* ================================================================
 * COMPONENT
 * ================================================================ */

/**
 * ReportManage — Edit Report Configuration Page
 *
 * Full behavioral parity with the monolith's data source manage page:
 * - Pre-populates form from existing dashboard data (OnGet parity)
 * - Full form validation including EQL parsing error display
 * - PUT mutation with cache invalidation and redirect
 * - Dirty state tracking with unsaved changes confirmation
 * - Test execution panel (Preview Query, Sample Data)
 * - 404 handling when dashboard not found
 * - Loading and generic error states
 */
function ReportManage(): React.ReactNode {
  /* ── Route params & navigation ────────────────────────────── */
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ── Form state ───────────────────────────────────────────── */
  const [formState, setFormState] = useState<ReportFormState>(createEmptyFormState);
  const [originalState, setOriginalState] = useState<ReportFormState | null>(null);
  const [validation, setValidation] = useState<FormValidation | null>(null);
  const [isPopulated, setIsPopulated] = useState(false);

  /* ── Test execution state ─────────────────────────────────── */
  const [testResult, setTestResult] = useState<string>('');
  const [testAction, setTestAction] = useState<'sql' | 'data' | null>(null);
  const [isTestLoading, setIsTestLoading] = useState(false);
  const [showTestPanel, setShowTestPanel] = useState(false);

  /* ── Data loading — replaces OnGet / dsMan.Get(RecordId) ─── */
  const {
    data: dashboardResponse,
    isLoading,
    isError,
    error: fetchError,
  } = useQuery<ApiResponse<DatabaseDataSource>>({
    queryKey: [QUERY_KEY_PREFIX, DASHBOARDS_KEY, id],
    queryFn: () => get<DatabaseDataSource>(`/v1/reporting/dashboards/${id}`),
    enabled: !!id,
    retry: (failureCount, error) => {
      /* Do not retry 404s — the dashboard simply does not exist */
      const apiErr = error as unknown as ApiError;
      if (apiErr?.status === 404) {
        return false;
      }
      return failureCount < 3;
    },
  });

  /* ── Pre-populate form from fetched data ──────────────────── */
  useEffect(() => {
    if (dashboardResponse?.success && dashboardResponse.object && !isPopulated) {
      const populated = populateFormState(dashboardResponse.object);
      setFormState(populated);
      setOriginalState(populated);
      setIsPopulated(true);
    }
  }, [dashboardResponse, isPopulated]);

  /* ── Dirty state tracking via useMemo ─────────────────────── */
  const isDirty = useMemo<boolean>(() => {
    if (!originalState) {
      return false;
    }
    return (
      formState.name !== originalState.name ||
      formState.description !== originalState.description ||
      formState.weight !== originalState.weight ||
      formState.returnTotal !== originalState.returnTotal ||
      formState.queryConfiguration !== originalState.queryConfiguration ||
      formState.parameterDefaults !== originalState.parameterDefaults
    );
  }, [formState, originalState]);

  /* ── Browser-level unsaved changes guard (beforeunload) ──── */
  useEffect(() => {
    if (!isDirty) {
      return undefined;
    }
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      /* Modern browsers ignore the custom string but still show a
         native confirmation dialog. */
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [isDirty]);

  /* ── Client-side validation ───────────────────────────────── */
  const validate = useCallback((): FormValidation | null => {
    const errors: ValidationError[] = [];

    if (!formState.name.trim()) {
      errors.push({ propertyName: 'name', message: 'Name is required.' });
    }

    if (!formState.queryConfiguration.trim()) {
      errors.push({
        propertyName: 'queryConfiguration',
        message: 'Query configuration is required.',
      });
    }

    const paramError = validateParameterFormat(formState.parameterDefaults);
    if (paramError) {
      errors.push({ propertyName: 'parameterDefaults', message: paramError });
    }

    if (errors.length > 0) {
      return { message: 'Please correct the errors below.', errors };
    }
    return null;
  }, [formState.name, formState.queryConfiguration, formState.parameterDefaults]);

  /* ── Update mutation — replaces OnPost / dsMan.Update() ──── */
  const updateMutation = useMutation<
    ApiResponse<DatabaseDataSource>,
    ApiError,
    UpdateDashboardPayload
  >({
    mutationFn: (payload) =>
      put<DatabaseDataSource>(`/v1/reporting/dashboards/${id}`, payload),
    onSuccess: (response) => {
      if (response.success) {
        /* Invalidate dashboard queries so list and detail views refresh */
        queryClient.invalidateQueries({
          queryKey: [QUERY_KEY_PREFIX, DASHBOARDS_KEY],
        });
        navigate(`/reports/view/${id}`);
      } else {
        /* Server returned success:false with validation details */
        const mappedErrors: ValidationError[] = (response.errors ?? []).map(
          (err) => ({
            propertyName: err.key ?? '',
            message: err.message ?? err.value ?? 'Validation error',
          }),
        );
        setValidation({
          message:
            response.message ?? 'Update failed. Please correct the errors below.',
          errors: mappedErrors,
        });
      }
    },
    onError: (error: ApiError) => {
      /*
       * Map server-side validation errors (422) including EQL parsing
       * errors with line/column information from DataSourceManager.Update
       * validation (EqlException → ValidationException path).
       */
      if (error.errors && error.errors.length > 0) {
        const mappedErrors: ValidationError[] = error.errors.map((err) => ({
          propertyName: err.key ?? '',
          message: err.message ?? err.value ?? 'Validation error',
        }));
        setValidation({
          message:
            error.message ?? 'Validation failed. Please correct the errors below.',
          errors: mappedErrors,
        });
      } else {
        setValidation({
          message:
            error.message ??
            'An unexpected error occurred while updating the dashboard.',
          errors: [],
        });
      }
    },
  });

  /* ── Form submission handler ──────────────────────────────── */
  const handleSubmit = useCallback(
    (e?: React.FormEvent<HTMLFormElement>) => {
      if (e) {
        e.preventDefault();
      }

      /* Client-side validation first */
      const clientErrors = validate();
      if (clientErrors) {
        setValidation(clientErrors);
        return;
      }

      /* Clear previous validation state */
      setValidation(null);

      const payload: UpdateDashboardPayload = {
        name: formState.name.trim(),
        description: formState.description.trim(),
        weight: formState.weight,
        queryConfiguration: formState.queryConfiguration,
        parameterDefaults: formState.parameterDefaults,
        returnTotal: formState.returnTotal,
      };

      updateMutation.mutate(payload);
    },
    [formState, validate, updateMutation],
  );

  /* ── Navigation with unsaved-changes guard ────────────────── */
  const handleCancel = useCallback(() => {
    if (isDirty) {
      const confirmed = window.confirm(
        'You have unsaved changes. Are you sure you want to leave?',
      );
      if (!confirmed) {
        return;
      }
    }
    navigate(`/reports/view/${id}`);
  }, [isDirty, navigate, id]);

  /* ── Test execution handlers ──────────────────────────────── */
  const handleTestExecution = useCallback(
    async (action: 'sql' | 'data') => {
      setIsTestLoading(true);
      setTestAction(action);
      setShowTestPanel(true);
      setTestResult('');

      try {
        const payload: TestExecutionPayload = {
          action,
          eql: formState.queryConfiguration,
          parameters: formState.parameterDefaults,
          returnTotal: formState.returnTotal,
        };

        const response = await post<{ result: string }>(
          '/v1/reporting/dashboards/test',
          payload,
        );

        if (response.success && response.object) {
          setTestResult(
            typeof response.object.result === 'string'
              ? response.object.result
              : JSON.stringify(response.object, null, 2),
          );
        } else {
          setTestResult(
            response.message ?? 'Test execution returned no results.',
          );
        }
      } catch (err: unknown) {
        const apiErr = err as ApiError;
        setTestResult(apiErr.message ?? 'Test execution failed.');
      } finally {
        setIsTestLoading(false);
      }
    },
    [
      formState.queryConfiguration,
      formState.parameterDefaults,
      formState.returnTotal,
    ],
  );

  /* ── Field-level error lookup helper ──────────────────────── */
  const getFieldError = useCallback(
    (fieldName: string): string | undefined => {
      if (!validation?.errors) {
        return undefined;
      }
      const match = validation.errors.find(
        (e) => e.propertyName.toLowerCase() === fieldName.toLowerCase(),
      );
      return match?.message;
    },
    [validation],
  );

  /* ── Generic field change handler ─────────────────────────── */
  const updateField = useCallback(
    <K extends keyof ReportFormState>(field: K, value: ReportFormState[K]) => {
      setFormState((prev) => ({ ...prev, [field]: value }));
    },
    [],
  );

  /* ── Derived state for rendering ──────────────────────────── */
  const dashboardName = dashboardResponse?.object
    ? extractDisplayName(dashboardResponse.object)
    : 'Dashboard';
  const isNotFound = isError && (fetchError as unknown as ApiError)?.status === 404;
  const isSaving = updateMutation.isPending;

  /* ================================================================
   * RENDER — Loading State
   * ================================================================ */
  if (isLoading) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <div className="text-center">
          <div
            className="mx-auto mb-4 h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-t-transparent"
            role="status"
            aria-label="Loading dashboard configuration"
          />
          <p className="text-sm text-gray-500">
            Loading dashboard configuration…
          </p>
        </div>
      </div>
    );
  }

  /* ================================================================
   * RENDER — Not Found State (404)
   * ================================================================ */
  if (isNotFound) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-12 text-center">
        <div className="mb-4 text-6xl" aria-hidden="true">
          🔍
        </div>
        <h1 className="mb-2 text-2xl font-semibold text-gray-900">
          Dashboard Not Found
        </h1>
        <p className="mb-6 text-gray-500">
          The dashboard you are trying to edit does not exist or has been
          removed.
        </p>
        <Link
          to="/reports"
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to Dashboards
        </Link>
      </div>
    );
  }

  /* ================================================================
   * RENDER — Generic Error State (non-404)
   * ================================================================ */
  if (isError) {
    return (
      <div className="mx-auto max-w-3xl px-4 py-12 text-center">
        <div className="mb-4 text-6xl" aria-hidden="true">
          ⚠️
        </div>
        <h1 className="mb-2 text-2xl font-semibold text-gray-900">
          Error Loading Dashboard
        </h1>
        <p className="mb-6 text-gray-500">
          {(fetchError as unknown as ApiError)?.message ??
            'An unexpected error occurred while loading the dashboard.'}
        </p>
        <Link
          to="/reports"
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          ← Back to Dashboards
        </Link>
      </div>
    );
  }

  /* ================================================================
   * RENDER — Main Edit Form
   * ================================================================ */
  return (
    <div className="mx-auto max-w-screen-xl px-4 py-6">
      {/* ── Page Header ──────────────────────────────────────── */}
      <div className="mb-6">
        <div className="mb-1">
          <Link
            to={`/reports/view/${id}`}
            className="text-sm text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
          >
            ← Back to Dashboard Details
          </Link>
        </div>
        <div className="flex flex-wrap items-center justify-between gap-4">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">
              {dashboardName}
            </h1>
            <p className="mt-1 text-sm text-gray-500">Manage</p>
          </div>

          {/* Header actions: Save + Cancel */}
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={handleCancel}
              disabled={isSaving}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => handleSubmit()}
              disabled={isSaving}
              className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isSaving ? (
                <>
                  <span
                    className="mr-2 inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                    aria-hidden="true"
                  />
                  Saving…
                </>
              ) : (
                'Save'
              )}
            </button>
          </div>
        </div>
      </div>

      {/* ── DynamicForm wrapper ───────────────────────────────── */}
      <DynamicForm
        id="update-dashboard"
        name="UpdateRecord"
        labelMode="stacked"
        fieldMode="form"
        validation={validation ?? undefined}
        showValidation={!!validation}
        onSubmit={handleSubmit}
      >
        {/* Row 1: Name + Description (half-width each) */}
        <div className="grid grid-cols-1 gap-6 md:grid-cols-2">
          {/* Name — required */}
          <div>
            <label
              htmlFor="field-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name <span className="text-red-500">*</span>
            </label>
            <input
              id="field-name"
              type="text"
              required
              value={formState.name}
              onChange={(e) => updateField('name', e.target.value)}
              aria-invalid={!!getFieldError('name')}
              aria-describedby={
                getFieldError('name') ? 'name-error' : undefined
              }
              className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600 ${
                getFieldError('name')
                  ? 'border-red-500 text-red-900'
                  : 'border-gray-300 text-gray-900'
              }`}
              placeholder="Dashboard name"
            />
            {getFieldError('name') && (
              <p
                id="name-error"
                className="mt-1 text-xs text-red-600"
                role="alert"
              >
                {getFieldError('name')}
              </p>
            )}
          </div>

          {/* Description */}
          <div>
            <label
              htmlFor="field-description"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <input
              id="field-description"
              type="text"
              value={formState.description}
              onChange={(e) => updateField('description', e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600"
              placeholder="Optional description"
            />
          </div>
        </div>

        {/* Row 2: Model (read-only) + Weight */}
        <div className="mt-6 grid grid-cols-1 gap-6 md:grid-cols-2">
          {/* Model — read-only */}
          <div>
            <label
              htmlFor="field-model"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Model
            </label>
            <input
              id="field-model"
              type="text"
              readOnly
              tabIndex={-1}
              value={formState.resultModel}
              className="block w-full rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm"
            />
          </div>

          {/* Weight — required */}
          <div>
            <label
              htmlFor="field-weight"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Weight <span className="text-red-500">*</span>
            </label>
            <input
              id="field-weight"
              type="number"
              required
              value={formState.weight}
              onChange={(e) =>
                updateField('weight', parseInt(e.target.value, 10) || 0)
              }
              aria-invalid={!!getFieldError('weight')}
              aria-describedby={
                getFieldError('weight') ? 'weight-error' : undefined
              }
              className={`block w-full rounded-md border px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600 ${
                getFieldError('weight')
                  ? 'border-red-500 text-red-900'
                  : 'border-gray-300 text-gray-900'
              }`}
              placeholder="0"
            />
            {getFieldError('weight') && (
              <p
                id="weight-error"
                className="mt-1 text-xs text-red-600"
                role="alert"
              >
                {getFieldError('weight')}
              </p>
            )}
          </div>
        </div>

        {/* Row 3: Return Total (checkbox) */}
        <div className="mt-6">
          <div className="flex items-center gap-3">
            <input
              id="field-return-total"
              type="checkbox"
              checked={formState.returnTotal}
              onChange={(e) => updateField('returnTotal', e.target.checked)}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            />
            <label
              htmlFor="field-return-total"
              className="text-sm font-medium text-gray-700"
            >
              Return Total
            </label>
          </div>
          <p className="mt-1 text-xs text-gray-400">
            Whether to include the total record count in query results.
          </p>
        </div>

        {/* Row 4: Query Configuration — full-width code area */}
        <div className="mt-6">
          <label
            htmlFor="field-query"
            className="mb-1 block text-sm font-medium text-gray-700"
          >
            Query Configuration <span className="text-red-500">*</span>
          </label>
          <textarea
            id="field-query"
            required
            rows={12}
            value={formState.queryConfiguration}
            onChange={(e) => updateField('queryConfiguration', e.target.value)}
            aria-invalid={!!getFieldError('queryConfiguration')}
            aria-describedby={
              getFieldError('queryConfiguration')
                ? 'query-error'
                : 'query-help'
            }
            className={`block w-full rounded-md border px-3 py-2 font-mono text-sm shadow-sm placeholder:text-gray-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600 ${
              getFieldError('queryConfiguration')
                ? 'border-red-500 text-red-900'
                : 'border-gray-300 text-gray-900'
            }`}
            placeholder="SELECT * FROM entity WHERE id = @recordId"
            spellCheck={false}
          />
          {getFieldError('queryConfiguration') && (
            <p
              id="query-error"
              className="mt-1 text-xs text-red-600"
              role="alert"
            >
              {getFieldError('queryConfiguration')}
            </p>
          )}
          <p id="query-help" className="mt-1 text-xs text-gray-400">
            Enter your EQL query. Use @paramName syntax for parameter
            placeholders.
          </p>
        </div>

        {/* Row 5: Parameter Defaults */}
        <div className="mt-6">
          <label
            htmlFor="field-params"
            className="mb-1 block text-sm font-medium text-gray-700"
          >
            Parameter Defaults
          </label>
          <textarea
            id="field-params"
            rows={5}
            value={formState.parameterDefaults}
            onChange={(e) =>
              updateField('parameterDefaults', e.target.value)
            }
            aria-invalid={!!getFieldError('parameterDefaults')}
            aria-describedby="params-help"
            className={`block w-full rounded-md border px-3 py-2 font-mono text-sm shadow-sm placeholder:text-gray-400 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-blue-600 ${
              getFieldError('parameterDefaults')
                ? 'border-red-500 text-red-900'
                : 'border-gray-300 text-gray-900'
            }`}
            placeholder={'recordId,guid,\npage,int,1\npageSize,int,10'}
            spellCheck={false}
          />
          {getFieldError('parameterDefaults') && (
            <p className="mt-1 text-xs text-red-600" role="alert">
              {getFieldError('parameterDefaults')}
            </p>
          )}
          <p id="params-help" className="mt-1 text-xs text-gray-400">
            One parameter per line in the format:{' '}
            <code className="rounded bg-gray-100 px-1 py-0.5 font-mono text-xs">
              name,type,value
            </code>
            . Optionally append{' '}
            <code className="rounded bg-gray-100 px-1 py-0.5 font-mono text-xs">
              ,true
            </code>{' '}
            to ignore parse errors.
          </p>
        </div>

        {/* ── Test Execution Panel ───────────────────────────── */}
        <div className="mt-6 rounded-md border border-gray-200 bg-gray-50 p-4">
          <h3 className="mb-3 text-sm font-semibold text-gray-700">
            Test Execution
          </h3>
          <div className="flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={() => handleTestExecution('sql')}
              disabled={
                isTestLoading || !formState.queryConfiguration.trim()
              }
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Preview Query
            </button>
            <button
              type="button"
              onClick={() => handleTestExecution('data')}
              disabled={
                isTestLoading || !formState.queryConfiguration.trim()
              }
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-50"
            >
              Sample Data
            </button>
          </div>

          {/* Test result display area */}
          {showTestPanel && (
            <div className="mt-4">
              <div className="flex items-center justify-between">
                <h4 className="text-xs font-medium text-gray-600">
                  {testAction === 'sql'
                    ? 'Generated SQL'
                    : 'Sample JSON Data'}
                </h4>
                <button
                  type="button"
                  onClick={() => {
                    setShowTestPanel(false);
                    setTestResult('');
                    setTestAction(null);
                  }}
                  className="text-xs text-gray-400 hover:text-gray-600 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
                  aria-label="Close test results"
                >
                  ✕ Close
                </button>
              </div>
              {isTestLoading ? (
                <div className="mt-2 flex items-center gap-2 text-sm text-gray-500">
                  <span
                    className="inline-block h-4 w-4 animate-spin rounded-full border-2 border-blue-600 border-t-transparent"
                    aria-hidden="true"
                  />
                  Executing…
                </div>
              ) : (
                <pre className="mt-2 max-h-80 overflow-auto rounded-md border border-gray-200 bg-white p-3 font-mono text-xs text-gray-800">
                  {testResult || 'No results returned.'}
                </pre>
              )}
            </div>
          )}
        </div>
      </DynamicForm>

      {/* ── Dirty state indicator bar ────────────────────────── */}
      {isDirty && (
        <div
          className="fixed inset-x-0 bottom-0 z-40 border-t border-amber-300 bg-amber-50 px-4 py-2 text-center text-sm text-amber-800"
          role="status"
          aria-live="polite"
        >
          You have unsaved changes.
        </div>
      )}
    </div>
  );
}

export default ReportManage;
