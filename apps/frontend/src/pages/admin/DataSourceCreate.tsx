/**
 * DataSourceCreate — Data Source Creation Page
 *
 * Replaces WebVella.Erp.Plugins.SDK/Pages/data_source/create.cshtml[.cs].
 * Provides a form for creating EQL/database data source definitions with
 * interactive SQL preview and sample data testing capabilities.
 *
 * Route: /admin/data-sources/create
 *
 * Source mapping:
 *  - create.cshtml.cs OnPost()        → inline useMutation (POST /v1/datasources)
 *  - create.cshtml Ace editor         → styled monospace textarea for EQL input
 *  - create.cshtml testDataSource()   → handleTestSql / handleTestData callbacks
 *  - create.cshtml #modal-sql-result  → SQL Result Modal (Modal component)
 *  - create.cshtml #modal-data-result → Data Result Modal (Modal component)
 *  - create.cshtml EQL snippets       → PRESET_RECORD_DETAILS / PRESET_RECORD_LIST
 *  - DataSourceManager.Create()       → POST /v1/datasources via client.post()
 *  - EqlException → ValidationException → DynamicForm validation display
 *
 * @module pages/admin/DataSourceCreate
 */

import { useState, useCallback, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { post, type ApiResponse } from '../../api/client';
import Modal, { ModalSize } from '../../components/common/Modal';
import DynamicForm, {
  type FormValidation,
  type ValidationError,
} from '../../components/forms/DynamicForm';
import type {
  DataSourceTestModel,
  DataSourceParameter,
} from '../../types/datasource';
import {
  useGenerateSql,
  useExecuteAdHocQuery,
} from '../../hooks/useReports';

/* ────────────────────────────────────────────────────────────────
 * Local Types
 * ──────────────────────────────────────────────────────────────── */

/**
 * Payload shape for the data source creation API endpoint.
 * Maps to the Entity Management service's POST /v1/datasources contract,
 * replacing the monolith's DataSourceManager.Create() parameter set.
 */
interface CreateDataSourcePayload {
  /** Display name (validated for uniqueness on the server). */
  name: string;
  /** Optional human-readable description. */
  description: string;
  /** Sorting weight — lower values appear first (default: 10). */
  weight: number;
  /** EQL query text to compile and store. */
  eqlText: string;
  /**
   * Newline-separated parameter definitions in
   * "name,type,value[,ignoreParseErrors]" format.
   * Parsed server-side by ProcessParametersText().
   */
  parametersText: string;
  /** Whether to compute total record count alongside results. */
  returnTotal: boolean;
}

/** Minimal response shape for extracting the new data source ID. */
interface CreateDataSourceResult {
  id: string;
  name: string;
}

/* ────────────────────────────────────────────────────────────────
 * Helper: Parse parameter defaults to EqlParameter-compatible pairs
 *
 * Splits newline-separated "name,type,value[,ignoreParseErrors]" text
 * into name/value pairs for the test mutation hooks. Strips the type
 * metadata — only name and raw value are extracted.
 *
 * Matches the sibling DataSourceManage page's parsing logic and maps
 * to the monolith's ConvertDataSourceParameterToEqlParameter().
 * ──────────────────────────────────────────────────────────────── */

function parseParamDefaultsToEqlParams(
  text: string,
): Array<{ name: string; value: unknown }> {
  if (!text.trim()) {
    return [];
  }
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => {
      const parts = line.split(',').map((p) => p.trim());
      const name = parts[0] || '';
      /* Value starts at index 2; join remaining parts to handle commas */
      const value = parts.length > 2 ? parts.slice(2).join(',').trim() : '';
      return { name, value };
    })
    .filter((param) => param.name.length > 0);
}

/* ────────────────────────────────────────────────────────────────
 * Helper: Build DataSourceTestModel
 *
 * Constructs a typed DataSourceTestModel representing the current
 * test configuration. Maps to the monolith's jQuery POST payload
 * in create.cshtml (lines 166-170):
 *   { action: resultType, eql: eql, parameters: paramsString }
 * ──────────────────────────────────────────────────────────────── */

function buildTestPayload(
  action: string,
  eql: string,
  paramDefaultsText: string,
  returnTotal: boolean,
): DataSourceTestModel {
  /* Parse parameter defaults into structured DataSourceParameter[] */
  const paramList: DataSourceParameter[] = paramDefaultsText
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => {
      const parts = line.split(',').map((p) => p.trim());
      return {
        name: parts[0] || '',
        type: parts[1] || 'text',
        value: parts[2] || '',
        ignoreParseErrors:
          parts.length >= 4 && parts[3].toLowerCase() === 'true',
      };
    })
    .filter((p) => p.name.length > 0);

  return { action, eql, parameters: paramDefaultsText, paramList, returnTotal };
}

/* ────────────────────────────────────────────────────────────────
 * EQL Template Presets
 *
 * Pre-built EQL snippets from create.cshtml that populate the code
 * editor and parameter defaults fields with common query patterns.
 * Each preset includes typed DataSourceParameter definitions for the
 * paramList entries.
 * ──────────────────────────────────────────────────────────────── */

/**
 * Record-details preset: fetches a single record by ID.
 * Source: create.cshtml lines 71-76 (eql_snippet_record_details).
 */
const PRESET_RECORD_DETAILS: {
  eql: string;
  paramDefaults: string;
  parameters: DataSourceParameter[];
} = {
  eql: 'SELECT *\nFROM user\nWHERE id = @@recordId\n',
  paramDefaults:
    'recordId,guid,00000000-0000-0000-0000-000000000000\n',
  parameters: [
    {
      name: 'recordId',
      type: 'guid',
      value: '00000000-0000-0000-0000-000000000000',
      ignoreParseErrors: false,
    },
  ],
};

/**
 * Record-list preset: fetches a paginated, sorted list of records.
 * Source: create.cshtml lines 78-90 (eql_snippet_record_list).
 */
const PRESET_RECORD_LIST: {
  eql: string;
  paramDefaults: string;
  parameters: DataSourceParameter[];
} = {
  eql: 'SELECT *\nFROM user\nORDER BY @@sortBy @@sortOrder\nPAGE @@page\nPAGESIZE @@pageSize\n',
  paramDefaults:
    'sortBy,text,id\nsortOrder,text,asc\npage,int,1\npageSize,int,10\n',
  parameters: [
    { name: 'sortBy', type: 'text', value: 'id', ignoreParseErrors: false },
    {
      name: 'sortOrder',
      type: 'text',
      value: 'asc',
      ignoreParseErrors: false,
    },
    { name: 'page', type: 'int', value: '1', ignoreParseErrors: false },
    {
      name: 'pageSize',
      type: 'int',
      value: '10',
      ignoreParseErrors: false,
    },
  ],
};

/** Descriptive guidance text for the parameter defaults textarea. */
const PARAM_DEFAULTS_HELP =
  'New line separated list: name,type,value[,ignoreParseErrors]. ' +
  "Types: 'guid', 'int', 'decimal', 'date', 'text', 'bool'.";

/* ────────────────────────────────────────────────────────────────
 * DataSourceCreate Component
 * ──────────────────────────────────────────────────────────────── */

function DataSourceCreate() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ── Form field state ────────────────────────────────────── */
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [weight, setWeight] = useState(10);
  const [returnTotal, setReturnTotal] = useState(true);
  const [eqlInput, setEqlInput] = useState('');
  const [paramDefaults, setParamDefaults] = useState('');

  /* ── Validation state ───────────────────────────────────── */
  const [validation, setValidation] = useState<FormValidation | null>(null);

  /* ── Modal visibility and content state ─────────────────── */
  const [sqlModalVisible, setSqlModalVisible] = useState(false);
  const [dataModalVisible, setDataModalVisible] = useState(false);
  const [sqlResult, setSqlResult] = useState('');
  const [dataResult, setDataResult] = useState('');
  const [testError, setTestError] = useState('');

  /* ── Create mutation (inline — POST /v1/datasources) ────── */
  const createMutation = useMutation({
    mutationFn: async (
      payload: CreateDataSourcePayload,
    ): Promise<ApiResponse<CreateDataSourceResult>> => {
      const response = await post<CreateDataSourceResult>(
        '/v1/datasources',
        payload,
      );
      if (!response.success) {
        throw response;
      }
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['datasources'] });
    },
  });

  /* ── Test mutations from useReports hook module ─────────── */
  const generateSqlMutation = useGenerateSql();
  const executeQueryMutation = useExecuteAdHocQuery();

  /* ── Callback: form submission ──────────────────────────── */
  const handleSubmit = useCallback(
    async (event: FormEvent<HTMLFormElement>) => {
      event.preventDefault();
      setValidation(null);
      setTestError('');

      /* Client-side required-field check */
      const errors: ValidationError[] = [];
      if (!name.trim()) {
        errors.push({ propertyName: 'name', message: 'Name is required.' });
      }
      if (!eqlInput.trim()) {
        errors.push({
          propertyName: 'eqlInput',
          message: 'EQL input is required.',
        });
      }
      if (errors.length > 0) {
        setValidation({ message: 'Please correct the errors below.', errors });
        return;
      }

      const payload: CreateDataSourcePayload = {
        name: name.trim(),
        description: description.trim(),
        weight,
        eqlText: eqlInput,
        parametersText: paramDefaults,
        returnTotal,
      };

      try {
        const result = await createMutation.mutateAsync(payload);
        if (result.success && result.object?.id) {
          navigate(`/admin/data-sources/${result.object.id}`);
        } else if (result.success) {
          navigate('/admin/data-sources');
        }
      } catch (err: unknown) {
        /* Map API errors → FormValidation for the DynamicForm banner */
        const apiErr = err as ApiResponse<unknown> | undefined;
        if (apiErr && Array.isArray(apiErr.errors) && apiErr.errors.length > 0) {
          setValidation({
            message: apiErr.message ?? 'Data source creation failed.',
            errors: apiErr.errors.map((e) => ({
              propertyName: e.key || 'general',
              message: e.message,
            })),
          });
        } else {
          setValidation({
            message: 'An unexpected error occurred while creating the data source.',
            errors: [],
          });
        }
      }
    },
    [
      name,
      description,
      weight,
      eqlInput,
      paramDefaults,
      returnTotal,
      createMutation,
      navigate,
    ],
  );

  /* ── Callback: test EQL → show generated SQL ───────────── */
  const handleTestSql = useCallback(async () => {
    setTestError('');
    setSqlResult('');
    const parsedParams = parseParamDefaultsToEqlParams(paramDefaults);
    try {
      const result = await generateSqlMutation.mutateAsync({
        eqlText: eqlInput,
        parameters: parsedParams,
      });
      setSqlResult(result.sql ?? '-- No SQL generated --');
      setSqlModalVisible(true);
    } catch (err: unknown) {
      const apiErr = err as ApiResponse<unknown> | undefined;
      const message =
        apiErr?.message ??
        (err instanceof Error ? err.message : 'Failed to generate SQL.');
      setTestError(message);
    }
  }, [eqlInput, paramDefaults, generateSqlMutation]);

  /* ── Callback: test EQL → show sample data ─────────────── */
  const handleTestData = useCallback(async () => {
    setTestError('');
    setDataResult('');
    const parsedParams = parseParamDefaultsToEqlParams(paramDefaults);
    try {
      const result = await executeQueryMutation.mutateAsync({
        eqlText: eqlInput,
        parameters: parsedParams,
        returnTotal,
      });
      setDataResult(JSON.stringify(result, null, 2));
      setDataModalVisible(true);
    } catch (err: unknown) {
      const apiErr = err as ApiResponse<unknown> | undefined;
      const message =
        apiErr?.message ??
        (err instanceof Error ? err.message : 'Failed to execute query.');
      setTestError(message);
    }
  }, [eqlInput, paramDefaults, returnTotal, executeQueryMutation]);

  /* ── Callback: apply EQL template presets ───────────────── */
  const handleApplyRecordDetails = useCallback(() => {
    setEqlInput(PRESET_RECORD_DETAILS.eql);
    setParamDefaults(PRESET_RECORD_DETAILS.paramDefaults);
  }, []);

  const handleApplyRecordList = useCallback(() => {
    setEqlInput(PRESET_RECORD_LIST.eql);
    setParamDefaults(PRESET_RECORD_LIST.paramDefaults);
  }, []);

  /* ── Callback: close modals ────────────────────────────── */
  const handleCloseSqlModal = useCallback(() => {
    setSqlModalVisible(false);
  }, []);

  const handleCloseDataModal = useCallback(() => {
    setDataModalVisible(false);
  }, []);

  /* Derived: whether any mutation is in flight */
  const isBusy =
    createMutation.isPending ||
    generateSqlMutation.isPending ||
    executeQueryMutation.isPending;

  /* ================================================================
   * JSX Render
   * ================================================================ */
  return (
    <div className="mx-auto w-full max-w-7xl px-4 py-6 sm:px-6 lg:px-8">
      {/* ── Page header with breadcrumb + actions ──────────── */}
      <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <nav className="mb-1 text-sm text-gray-500" aria-label="Breadcrumb">
            <ol className="flex items-center gap-1">
              <li>
                <Link
                  to="/admin"
                  className="hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:rounded"
                >
                  Admin
                </Link>
              </li>
              <li aria-hidden="true">/</li>
              <li>
                <Link
                  to="/admin/data-sources"
                  className="hover:text-gray-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:rounded"
                >
                  Data Sources
                </Link>
              </li>
              <li aria-hidden="true">/</li>
              <li className="font-medium text-gray-900" aria-current="page">
                Create
              </li>
            </ol>
          </nav>
          <h1 className="text-2xl font-bold text-gray-900">
            Create Data Source
          </h1>
        </div>
        <div className="flex items-center gap-2">
          <Link
            to="/admin/data-sources"
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            Cancel
          </Link>
          <button
            type="submit"
            form="ds-create-form"
            disabled={isBusy}
            className="inline-flex items-center rounded-md border border-transparent bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {createMutation.isPending ? 'Creating…' : 'Create Data Source'}
          </button>
        </div>
      </div>

      {/* ── Test error banner (non-form validation) ──────── */}
      {testError && (
        <div
          role="alert"
          className="mb-4 rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-700"
        >
          <strong className="font-medium">Test Error: </strong>
          {testError}
        </div>
      )}

      {/* ── Form ─────────────────────────────────────────── */}
      <DynamicForm
        id="ds-create-form"
        name="ds-create-form"
        method="post"
        showValidation={validation !== null}
        validation={validation ?? undefined}
        onSubmit={handleSubmit}
      >
        <div className="grid grid-cols-12 gap-6">
          {/* ── Name (required) ──────────────────────────── */}
          <div className="col-span-12 sm:col-span-6">
            <label
              htmlFor="ds-name"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Name <span className="text-red-500">*</span>
            </label>
            <input
              id="ds-name"
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
              autoFocus
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
              placeholder="e.g. my_datasource"
            />
          </div>

          {/* ── Weight ───────────────────────────────────── */}
          <div className="col-span-12 sm:col-span-3">
            <label
              htmlFor="ds-weight"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Weight
            </label>
            <input
              id="ds-weight"
              type="number"
              value={weight}
              onChange={(e) => setWeight(parseInt(e.target.value, 10) || 0)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
            />
          </div>

          {/* ── Return Total ─────────────────────────────── */}
          <div className="col-span-12 sm:col-span-3 flex items-end">
            <label className="inline-flex items-center gap-2 text-sm text-gray-700">
              <input
                type="checkbox"
                checked={returnTotal}
                onChange={(e) => setReturnTotal(e.target.checked)}
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
              />
              Return Total
            </label>
          </div>

          {/* ── Description ──────────────────────────────── */}
          <div className="col-span-12">
            <label
              htmlFor="ds-description"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Description
            </label>
            <textarea
              id="ds-description"
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
              placeholder="Optional description of this data source"
            />
          </div>

          {/* ── EQL Template Presets ─────────────────────── */}
          <div className="col-span-12">
            <div className="mb-2 flex items-center gap-2">
              <span className="text-sm font-medium text-gray-700">
                EQL Templates:
              </span>
              <button
                type="button"
                onClick={handleApplyRecordDetails}
                className="rounded-md border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                Record Details
              </button>
              <button
                type="button"
                onClick={handleApplyRecordList}
                className="rounded-md border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
              >
                Record List
              </button>
            </div>
          </div>

          {/* ── EQL Input (code editor) ─────────────────── */}
          <div className="col-span-12">
            <label
              htmlFor="ds-eql-input"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              EQL Input <span className="text-red-500">*</span>
            </label>
            <textarea
              id="ds-eql-input"
              rows={10}
              value={eqlInput}
              onChange={(e) => setEqlInput(e.target.value)}
              required
              spellCheck={false}
              className="block w-full rounded-md border border-gray-700 bg-gray-900 px-3 py-2 font-mono text-sm text-green-400 shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
              placeholder="SELECT *&#10;FROM entity_name&#10;WHERE id = @@recordId"
            />
          </div>

          {/* ── Param Defaults ───────────────────────────── */}
          <div className="col-span-12">
            <label
              htmlFor="ds-param-defaults"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Parameter Defaults
            </label>
            <textarea
              id="ds-param-defaults"
              rows={5}
              value={paramDefaults}
              onChange={(e) => setParamDefaults(e.target.value)}
              spellCheck={false}
              className="block w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm shadow-sm focus:border-blue-500 focus:ring-1 focus:ring-blue-500"
              placeholder="recordId,guid,00000000-0000-0000-0000-000000000000"
            />
            <p className="mt-1 text-xs text-gray-500">{PARAM_DEFAULTS_HELP}</p>
          </div>

          {/* ── Test buttons ─────────────────────────────── */}
          <div className="col-span-12 flex items-center gap-3">
            <button
              type="button"
              onClick={handleTestSql}
              disabled={!eqlInput.trim() || generateSqlMutation.isPending}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {generateSqlMutation.isPending ? 'Generating…' : 'Show SQL'}
            </button>
            <button
              type="button"
              onClick={handleTestData}
              disabled={!eqlInput.trim() || executeQueryMutation.isPending}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {executeQueryMutation.isPending
                ? 'Executing…'
                : 'Show Sample Data'}
            </button>
          </div>
        </div>
      </DynamicForm>

      {/* ── SQL Result Modal ────────────────────────────── */}
      <Modal
        isVisible={sqlModalVisible}
        title="Generated SQL"
        size={ModalSize.Large}
        onClose={handleCloseSqlModal}
        footer={
          <button
            type="button"
            onClick={handleCloseSqlModal}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            Close
          </button>
        }
      >
        <pre className="max-h-[60vh] overflow-auto rounded-md bg-gray-900 p-4 font-mono text-sm text-green-400">
          {sqlResult || '-- No SQL generated --'}
        </pre>
      </Modal>

      {/* ── Sample Data Result Modal ────────────────────── */}
      <Modal
        isVisible={dataModalVisible}
        title="Sample Data (JSON)"
        size={ModalSize.Large}
        onClose={handleCloseDataModal}
        footer={
          <button
            type="button"
            onClick={handleCloseDataModal}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            Close
          </button>
        }
      >
        <pre className="max-h-[60vh] overflow-auto rounded-md bg-gray-900 p-4 font-mono text-sm text-green-400">
          {dataResult || '{ }'}
        </pre>
      </Modal>
    </div>
  );
}

/* ────────────────────────────────────────────────────────────────
 * Default export for lazy-loading via React.lazy() in router.tsx
 * ──────────────────────────────────────────────────────────────── */
export default DataSourceCreate;
