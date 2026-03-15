import { useState, useEffect, useCallback } from 'react';
import type { FormEvent } from 'react';
import { useParams, useNavigate, Link } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { put } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import Modal, { ModalSize } from '../../components/common/Modal';
import DynamicForm from '../../components/forms/DynamicForm';
import type {
  FormValidation,
  ValidationError,
} from '../../components/forms/DynamicForm';
import type {
  DatabaseDataSource,
  DataSourceParameter,
  EqlParameter,
} from '../../types/datasource';
import {
  useDataSource,
  useGenerateSql,
  useExecuteAdHocQuery,
} from '../../hooks/useReports';

/**
 * Parses the newline-separated "name,type,value" text into an array
 * of EqlParameter objects suitable for the generate-sql and ad-hoc
 * query mutations. Matches the monolith's ConvertDataSourceParameterToEqlParameter().
 */
function parseParamDefaultsToEqlParams(text: string): EqlParameter[] {
  if (!text.trim()) return [];

  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => {
      const parts = line.split(',');
      const paramName = (parts[0] ?? '').trim();
      /* parts[1] is the type (text/number/etc), parts[2+] is the value */
      const paramValue = parts.length > 2 ? parts.slice(2).join(',').trim() : '';
      return { name: paramName, value: paramValue };
    })
    .filter((p) => p.name.length > 0);
}

/**
 * DataSourceManage — Edit Data Source Page
 *
 * Route: /admin/data-sources/:dataSourceId/manage
 *
 * Replaces the monolith's
 * WebVella.Erp.Plugins.SDK/Pages/data_source/manage.cshtml[.cs].
 *
 * Loads an existing DatabaseDataSource by ID, pre-populates the
 * edit form with current values, provides interactive SQL and
 * Sample Data test modals, and persists updates via PUT mutation.
 *
 * Source behaviour preserved:
 *  - manage.cshtml.cs InitPage/OnGet → useDataSource(id) with
 *    form pre-population in useEffect
 *  - DataSourceManager.Update() → inline useMutation with
 *    client.put(/v1/datasources/{id})
 *  - EqlException → FormValidation error display
 *  - jQuery POST /api/v3.0/datasource/test action:'sql' →
 *    useGenerateSql() mutation
 *  - jQuery POST /api/v3.0/datasource/test action:'data' →
 *    useExecuteAdHocQuery() mutation
 *  - Redirect on success → navigate to detail page
 */
function DataSourceManage(): React.JSX.Element {
  const { dataSourceId } = useParams<{ dataSourceId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ═══════════════════════════════════════════════════════════════
   * Server State — Fetch existing data source
   * ═══════════════════════════════════════════════════════════════ */
  const {
    data: dsData,
    isLoading: isDsLoading,
    isError: isDsFetchError,
    error: dsFetchError,
  } = useDataSource(dataSourceId);

  /* ═══════════════════════════════════════════════════════════════
   * Inline Update Mutation
   *
   * useReports.ts has no useUpdateDataSource hook, so we define
   * the mutation inline using client.put().
   * ═══════════════════════════════════════════════════════════════ */
  const updateMutation = useMutation<
    ApiResponse<DatabaseDataSource>,
    unknown,
    {
      id: string;
      name: string;
      description: string;
      weight: number;
      eqlText: string;
      paramDefaults: string;
      returnTotal: boolean;
    }
  >({
    mutationFn: async (payload) => {
      const response = await put<DatabaseDataSource>(
        `/v1/datasources/${payload.id}`,
        {
          name: payload.name,
          description: payload.description,
          weight: payload.weight,
          eqlText: payload.eqlText,
          paramDefaults: payload.paramDefaults,
          returnTotal: payload.returnTotal,
        },
      );
      return response;
    },
    onSuccess: () => {
      /* Invalidate cached data source lists and this specific detail */
      queryClient.invalidateQueries({ queryKey: ['datasources'] });
      if (dataSourceId) {
        queryClient.invalidateQueries({
          queryKey: ['datasources', dataSourceId],
        });
      }
    },
  });

  /* ═══════════════════════════════════════════════════════════════
   * Test Mutations — Generate SQL + Execute Ad-Hoc Query
   * ═══════════════════════════════════════════════════════════════ */
  const generateSqlMutation = useGenerateSql();
  const executeQueryMutation = useExecuteAdHocQuery();

  /* ═══════════════════════════════════════════════════════════════
   * Local Form State
   *
   * Maps to the [BindProperty] fields in manage.cshtml.cs:
   * Name, Description, Weight, ReturnTotal, EqlInput, ParamDefaults
   * ═══════════════════════════════════════════════════════════════ */
  const [name, setName] = useState<string>('');
  const [description, setDescription] = useState<string>('');
  const [weight, setWeight] = useState<number>(10);
  const [returnTotal, setReturnTotal] = useState<boolean>(false);
  const [eqlInput, setEqlInput] = useState<string>('');
  const [paramDefaults, setParamDefaults] = useState<string>('');

  /* ── Validation State ─────────────────────────────────────────── */
  const [validation, setValidation] = useState<FormValidation | undefined>(
    undefined,
  );

  /* ── Modal Visibility State ───────────────────────────────────── */
  const [sqlModalVisible, setSqlModalVisible] = useState<boolean>(false);
  const [dataModalVisible, setDataModalVisible] = useState<boolean>(false);

  /* ── Test Result State ────────────────────────────────────────── */
  const [sqlResult, setSqlResult] = useState<string>('');
  const [dataResult, setDataResult] = useState<string>('');

  /* ═══════════════════════════════════════════════════════════════
   * Pre-populate form when data source arrives
   *
   * Replaces manage.cshtml.cs OnGet() logic that loads
   * DataSourceObject and populates BindProperties + serializes
   * Parameters to newline-separated name,type,value format.
   * ═══════════════════════════════════════════════════════════════ */
  useEffect(() => {
    if (!dsData) return;

    /*
     * useDataSource returns DataSourceBase | null. Cast to
     * DatabaseDataSource to access eqlText / sqlText.
     */
    const ds = dsData as DatabaseDataSource;

    setName(ds.name ?? '');
    setDescription(ds.description ?? '');
    setWeight(ds.weight ?? 10);
    setReturnTotal(ds.returnTotal ?? false);
    setEqlInput(ds.eqlText ?? '');

    /* Serialize parameters to newline-separated name,type,value
     * to populate the ParamDefaults textarea for editing.
     * This matches manage.cshtml.cs lines that iterate Parameters
     * and build "name,type,value\n" strings. */
    if (ds.parameters && Array.isArray(ds.parameters) && ds.parameters.length > 0) {
      const lines = ds.parameters.map((p: DataSourceParameter) => {
        const paramName = p.name ?? '';
        const paramType = p.type ?? 'text';
        const paramValue = p.value ?? '';
        return `${paramName},${paramType},${paramValue}`;
      });
      setParamDefaults(lines.join('\n'));
    } else {
      setParamDefaults('');
    }
  }, [dsData]);

  /* ═══════════════════════════════════════════════════════════════
   * Form Submission Handler
   *
   * Replaces manage.cshtml.cs OnPost(). Validates required fields
   * then calls updateMutation. EQL errors map to FormValidation.
   * On success, navigates to the data source details page.
   * ═══════════════════════════════════════════════════════════════ */
  const handleSubmit = useCallback(
    async (e: FormEvent<HTMLFormElement>) => {
      e.preventDefault();
      setValidation(undefined);

      /* Client-side required-field validation */
      const errors: ValidationError[] = [];

      if (!name.trim()) {
        errors.push({ propertyName: 'name', message: 'Name is required.' });
      }

      if (!eqlInput.trim()) {
        errors.push({
          propertyName: 'eqlInput',
          message: 'EQL text is required.',
        });
      }

      if (errors.length > 0) {
        setValidation({
          message: 'Please correct the errors below.',
          errors,
        });
        return;
      }

      if (!dataSourceId) return;

      try {
        await updateMutation.mutateAsync({
          id: dataSourceId,
          name: name.trim(),
          description: description.trim(),
          weight,
          eqlText: eqlInput.trim(),
          paramDefaults: paramDefaults.trim(),
          returnTotal,
        });

        /* Navigate to data source details on success */
        navigate(`/admin/data-sources/${dataSourceId}`, { replace: true });
      } catch (mutationError: unknown) {
        /* Map API/EQL error envelope to FormValidation so
         * DynamicForm can render the validation summary
         * and per-field messages — replaces the monolith's
         * EqlException → ValidationException conversion. */
        const apiErr = mutationError as {
          message?: string;
          errors?: Array<{
            key?: string;
            message?: string;
            propertyName?: string;
          }>;
        };

        const mappedErrors: ValidationError[] = [];

        if (apiErr?.errors && Array.isArray(apiErr.errors)) {
          for (const item of apiErr.errors) {
            mappedErrors.push({
              propertyName: item.propertyName ?? item.key ?? 'eqlInput',
              message: item.message ?? 'Validation error.',
            });
          }
        }

        setValidation({
          message:
            apiErr?.message ??
            'An error occurred while saving the data source.',
          errors: mappedErrors,
        });
      }
    },
    [
      dataSourceId,
      name,
      description,
      weight,
      eqlInput,
      paramDefaults,
      returnTotal,
      updateMutation,
      navigate,
    ],
  );

  /* ═══════════════════════════════════════════════════════════════
   * Test Handlers — SQL Preview + Sample Data
   *
   * Replaces the jQuery POST to /api/v3.0/datasource/test in
   * manage.cshtml. Two buttons test the current EQL input:
   *  - "Test SQL" → useGenerateSql → displays generated SQL
   *  - "Test Data" → useExecuteAdHocQuery → displays result JSON
   * ═══════════════════════════════════════════════════════════════ */
  const handleTestSql = useCallback(async () => {
    setSqlResult('');
    setValidation(undefined);

    if (!eqlInput.trim()) {
      setValidation({
        message: 'EQL text is required to generate SQL.',
        errors: [
          { propertyName: 'eqlInput', message: 'EQL text is required.' },
        ],
      });
      return;
    }

    try {
      const eqlParams = parseParamDefaultsToEqlParams(paramDefaults);
      const result = await generateSqlMutation.mutateAsync({
        eqlText: eqlInput.trim(),
        parameters: eqlParams.length > 0 ? eqlParams : undefined,
      });

      /* result is GenerateSqlResult with { sql, parameters?, fields? } */
      if (result && result.sql) {
        setSqlResult(result.sql);
      } else {
        setSqlResult(JSON.stringify(result, null, 2));
      }

      setSqlModalVisible(true);
    } catch (testError: unknown) {
      const errMsg =
        testError instanceof Error
          ? testError.message
          : 'An error occurred while generating SQL.';
      setSqlResult(`Error: ${errMsg}`);
      setSqlModalVisible(true);
    }
  }, [eqlInput, paramDefaults, generateSqlMutation]);

  const handleTestData = useCallback(async () => {
    setDataResult('');
    setValidation(undefined);

    if (!eqlInput.trim()) {
      setValidation({
        message: 'EQL text is required to execute query.',
        errors: [
          { propertyName: 'eqlInput', message: 'EQL text is required.' },
        ],
      });
      return;
    }

    try {
      const eqlParams = parseParamDefaultsToEqlParams(paramDefaults);
      const result = await executeQueryMutation.mutateAsync({
        eqlText: eqlInput.trim(),
        parameters: eqlParams.length > 0 ? eqlParams : undefined,
        returnTotal,
      });

      /* result is ReportExecutionResult with { records, totalCount, fields? } */
      setDataResult(
        result ? JSON.stringify(result, null, 2) : 'No data returned.',
      );
      setDataModalVisible(true);
    } catch (testError: unknown) {
      const errMsg =
        testError instanceof Error
          ? testError.message
          : 'An error occurred while executing the query.';
      setDataResult(`Error: ${errMsg}`);
      setDataModalVisible(true);
    }
  }, [eqlInput, paramDefaults, returnTotal, executeQueryMutation]);

  /* ── Modal Close Handlers ─────────────────────────────────────── */
  const handleCloseSqlModal = useCallback(() => {
    setSqlModalVisible(false);
  }, []);

  const handleCloseDataModal = useCallback(() => {
    setDataModalVisible(false);
  }, []);

  /* ═══════════════════════════════════════════════════════════════
   * Render — Loading State
   * ═══════════════════════════════════════════════════════════════ */
  if (isDsLoading) {
    return (
      <div
        className="flex min-h-[400px] items-center justify-center"
        role="status"
        aria-label="Loading data source"
      >
        <div className="flex items-center gap-2 text-gray-500">
          <svg
            className="h-5 w-5 animate-spin"
            xmlns="http://www.w3.org/2000/svg"
            fill="none"
            viewBox="0 0 24 24"
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
              d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
            />
          </svg>
          <span>Loading data source…</span>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
   * Render — Not Found / Fetch Error
   * ═══════════════════════════════════════════════════════════════ */
  const resolvedDs = dsData as DatabaseDataSource | undefined;

  if (isDsFetchError || !resolvedDs) {
    const errorMessage =
      dsFetchError != null &&
      typeof dsFetchError === 'object' &&
      'message' in dsFetchError
        ? String((dsFetchError as { message: unknown }).message)
        : 'The requested data source could not be found or an error occurred.';

    return (
      <div className="p-6">
        <div
          className="rounded-md border border-red-200 bg-red-50 p-4 text-red-700"
          role="alert"
        >
          <p className="font-semibold">Data source not found</p>
          <p className="mt-1 text-sm">{errorMessage}</p>
          <Link
            to="/admin/data-sources"
            className="mt-3 inline-block text-sm font-medium text-red-700 underline hover:text-red-900"
          >
            ← Back to Data Sources
          </Link>
        </div>
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════════
   * Render — Main Content
   * ═══════════════════════════════════════════════════════════════ */
  return (
    <div className="p-6">
      {/* ── Page Header ──────────────────────────────────────────── */}
      <div className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          {/* Teal-themed icon badge matching source data-source pages */}
          <div
            className="flex h-10 w-10 shrink-0 items-center justify-center rounded"
            style={{ backgroundColor: '#0ca678' }}
            aria-hidden="true"
          >
            <svg
              className="h-5 w-5 text-white"
              fill="currentColor"
              viewBox="0 0 16 16"
              xmlns="http://www.w3.org/2000/svg"
            >
              <path d="M12.5 16a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Zm.354-5.854 1.5 1.5a.5.5 0 0 1-.708.708L13 11.707V14.5a.5.5 0 0 1-1 0v-2.793l-.646.647a.5.5 0 0 1-.708-.708l1.5-1.5a.5.5 0 0 1 .708 0ZM1 3.5A1.5 1.5 0 0 1 2.5 2h11A1.5 1.5 0 0 1 15 3.5v4.09a4.518 4.518 0 0 0-3.5-1.09h-8A1.5 1.5 0 0 1 2 8v3.5A1.5 1.5 0 0 0 3.5 13H8a4.48 4.48 0 0 0 .5 1H3.5A2.5 2.5 0 0 1 1 11.5v-8Z" />
            </svg>
          </div>
          <div>
            <h1 className="text-xl font-semibold text-gray-900">
              Manage Data Source
            </h1>
            <p className="text-sm text-gray-500">
              {resolvedDs.name ?? 'Unnamed'}
            </p>
          </div>
        </div>

        {/* Header action buttons */}
        <div className="flex items-center gap-2">
          <button
            type="submit"
            form="ManageDataSource"
            disabled={updateMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {updateMutation.isPending && (
              <svg
                className="h-4 w-4 animate-spin"
                xmlns="http://www.w3.org/2000/svg"
                fill="none"
                viewBox="0 0 24 24"
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
                  d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                />
              </svg>
            )}
            Save Data Source
          </button>

          {/* Test SQL Button */}
          <button
            type="button"
            onClick={handleTestSql}
            disabled={generateSqlMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {generateSqlMutation.isPending && (
              <svg
                className="h-4 w-4 animate-spin"
                xmlns="http://www.w3.org/2000/svg"
                fill="none"
                viewBox="0 0 24 24"
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
                  d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                />
              </svg>
            )}
            Test SQL
          </button>

          {/* Test Data Button */}
          <button
            type="button"
            onClick={handleTestData}
            disabled={executeQueryMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {executeQueryMutation.isPending && (
              <svg
                className="h-4 w-4 animate-spin"
                xmlns="http://www.w3.org/2000/svg"
                fill="none"
                viewBox="0 0 24 24"
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
                  d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
                />
              </svg>
            )}
            Test Data
          </button>

          <Link
            to={`/admin/data-sources/${dataSourceId}`}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
          >
            Cancel
          </Link>
        </div>
      </div>

      {/* ── Breadcrumb navigation ────────────────────────────────── */}
      <nav aria-label="Breadcrumb" className="mb-4">
        <ol className="flex items-center gap-1 text-sm text-gray-500">
          <li>
            <Link
              to="/admin"
              className="hover:text-gray-700 hover:underline"
            >
              Admin
            </Link>
          </li>
          <li aria-hidden="true">
            <span className="mx-1">/</span>
          </li>
          <li>
            <Link
              to="/admin/data-sources"
              className="hover:text-gray-700 hover:underline"
            >
              Data Sources
            </Link>
          </li>
          <li aria-hidden="true">
            <span className="mx-1">/</span>
          </li>
          <li>
            <Link
              to={`/admin/data-sources/${dataSourceId}`}
              className="hover:text-gray-700 hover:underline"
            >
              {resolvedDs.name ?? 'Details'}
            </Link>
          </li>
          <li aria-hidden="true">
            <span className="mx-1">/</span>
          </li>
          <li aria-current="page" className="font-medium text-gray-900">
            Manage
          </li>
        </ol>
      </nav>

      {/* ── Data Source Edit Form ─────────────────────────────────── */}
      <DynamicForm
        id="ManageDataSource"
        name="ManageDataSource"
        validation={validation}
        onSubmit={handleSubmit}
      >
        <div className="grid grid-cols-12 gap-4">
          {/* Name — required, full-width (span-12) */}
          <div className="col-span-12">
            <label
              htmlFor="ds-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <input
              id="ds-name"
              name="name"
              type="text"
              required
              autoComplete="off"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              aria-required="true"
            />
          </div>

          {/* Description — optional, full-width textarea */}
          <div className="col-span-12">
            <label
              htmlFor="ds-description"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <textarea
              id="ds-description"
              name="description"
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Model — read-only display (matches source manage.cshtml disabled input) */}
          <div className="col-span-12">
            <label
              htmlFor="ds-model"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Model
            </label>
            <input
              id="ds-model"
              name="model"
              type="text"
              disabled
              value="DatabaseDataSource"
              className="block w-full cursor-not-allowed rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-500 shadow-sm"
              aria-disabled="true"
            />
            <p className="mt-1 text-xs text-gray-400">
              The data source model type cannot be changed.
            </p>
          </div>

          {/* Weight — numeric, 4-column span */}
          <div className="col-span-12 sm:col-span-4">
            <label
              htmlFor="ds-weight"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Weight
            </label>
            <input
              id="ds-weight"
              name="weight"
              type="number"
              min={0}
              step={1}
              value={weight}
              onChange={(e) => setWeight(Number(e.target.value) || 0)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* Return Total — checkbox, 8-column span */}
          <div className="col-span-12 sm:col-span-8">
            <div className="flex h-full items-end pb-1">
              <label className="inline-flex cursor-pointer items-center gap-2 text-sm text-gray-700">
                <input
                  type="checkbox"
                  name="returnTotal"
                  checked={returnTotal}
                  onChange={(e) => setReturnTotal(e.target.checked)}
                  className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                />
                Return Total Count
              </label>
            </div>
          </div>

          {/* EQL Input — code editor area (span-12)
           *
           * In the monolith this used Ace Editor in "pgsql" mode.
           * In the React SPA we use a monospace textarea. A
           * full code editor component (Monaco/CodeMirror) can be
           * integrated later without changing the data flow. */}
          <div className="col-span-12">
            <label
              htmlFor="ds-eql-input"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              EQL Input{' '}
              <span className="text-red-500" aria-hidden="true">
                *
              </span>
            </label>
            <textarea
              id="ds-eql-input"
              name="eqlInput"
              required
              rows={10}
              value={eqlInput}
              onChange={(e) => setEqlInput(e.target.value)}
              className="block w-full rounded-md border border-gray-300 bg-gray-900 px-3 py-2 font-mono text-sm text-green-400 shadow-sm placeholder:text-gray-600 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              aria-required="true"
              placeholder="SELECT * FROM entity WHERE id = @id"
              spellCheck={false}
              autoCorrect="off"
              autoCapitalize="off"
            />
            <p className="mt-1 text-xs text-gray-400">
              Enter your Entity Query Language (EQL) expression. Use the
              Test SQL and Test Data buttons to validate.
            </p>
          </div>

          {/* Param Defaults — textarea for name,type,value lines (span-12)
           *
           * Maps to manage.cshtml.cs OnGet() where Parameters are
           * serialized as newline-separated "name,type,value" strings.
           * On submit, we send the raw text; the API parses it. */}
          <div className="col-span-12">
            <label
              htmlFor="ds-param-defaults"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Parameter Defaults
            </label>
            <textarea
              id="ds-param-defaults"
              name="paramDefaults"
              rows={4}
              value={paramDefaults}
              onChange={(e) => setParamDefaults(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm shadow-sm placeholder:text-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              placeholder="name,text,default_value&#10;status,text,active"
              spellCheck={false}
            />
            <p className="mt-1 text-xs text-gray-400">
              One parameter per line in the format:{' '}
              <code className="rounded bg-gray-100 px-1 py-0.5 text-xs">
                name,type,value
              </code>
            </p>
          </div>
        </div>
      </DynamicForm>

      {/* ═══════════════════════════════════════════════════════════
       * SQL Result Modal
       *
       * Replaces the Bootstrap "modal-lg" #test_sql_result_modal
       * from manage.cshtml with an Ace editor showing generated SQL.
       * ═══════════════════════════════════════════════════════════ */}
      <Modal
        isVisible={sqlModalVisible}
        title="SQL Result"
        size={ModalSize.Large}
        onClose={handleCloseSqlModal}
        footer={
          <div className="flex justify-end">
            <button
              type="button"
              onClick={handleCloseSqlModal}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
            >
              Close
            </button>
          </div>
        }
      >
        <div className="max-h-[60vh] overflow-auto">
          <pre className="whitespace-pre-wrap rounded-md bg-gray-900 p-4 font-mono text-sm text-green-400">
            {sqlResult || 'No SQL result available.'}
          </pre>
        </div>
        {generateSqlMutation.isError && (
          <p className="mt-2 text-sm text-red-600" role="alert">
            An error occurred while generating SQL. Please check your EQL
            and try again.
          </p>
        )}
      </Modal>

      {/* ═══════════════════════════════════════════════════════════
       * Sample Data Result Modal
       *
       * Replaces the Bootstrap "modal-lg" #test_data_result_modal
       * from manage.cshtml with an Ace editor showing query results.
       * ═══════════════════════════════════════════════════════════ */}
      <Modal
        isVisible={dataModalVisible}
        title="Sample Data Result"
        size={ModalSize.Large}
        onClose={handleCloseDataModal}
        footer={
          <div className="flex justify-end">
            <button
              type="button"
              onClick={handleCloseDataModal}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
            >
              Close
            </button>
          </div>
        }
      >
        <div className="max-h-[60vh] overflow-auto">
          <pre className="whitespace-pre-wrap rounded-md bg-gray-900 p-4 font-mono text-sm text-green-400">
            {dataResult || 'No data result available.'}
          </pre>
        </div>
        {executeQueryMutation.isError && (
          <p className="mt-2 text-sm text-red-600" role="alert">
            An error occurred while executing the query. Please check your
            EQL and try again.
          </p>
        )}
      </Modal>
    </div>
  );
}

export default DataSourceManage;
