import { useState, useEffect, useCallback } from 'react';
import { useParams, useNavigate, Link } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { del, get } from '../../api/client';
import type { ApiResponse } from '../../api/client';
import Modal, { ModalSize } from '../../components/common/Modal';
import type {
  DataSourceBase,
  DatabaseDataSource,
  DataSourceParameter,
} from '../../types/datasource';
import { DataSourceType } from '../../types/datasource';
import {
  useDataSource,
  useGenerateSql,
  useExecuteAdHocQuery,
} from '../../hooks/useReports';

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Type-guard narrowing DataSourceBase to DatabaseDataSource.
 */
function isDatabaseDataSource(
  ds: DataSourceBase,
): ds is DatabaseDataSource {
  return ds.type === DataSourceType.Database;
}

/* ------------------------------------------------------------------ */
/*  Component                                                          */
/* ------------------------------------------------------------------ */

/**
 * DataSourceDetails — read-only data source details page.
 *
 * Route: `/admin/data-sources/:dataSourceId`
 *
 * Replaces the monolith's `details.cshtml[.cs]` page:
 * - Loads data source via useDataSource(id)
 * - Conditional rendering: database (EQL) vs code (class name)
 * - Displays name, description, model, weight, entity, returnTotal
 * - Parameter badges
 * - Test / preview modals for SQL and sample JSON data
 * - Delete with reference-lock check
 * - Manage link (database type only)
 */
export default function DataSourceDetails(): React.JSX.Element {
  const { dataSourceId } = useParams<{ dataSourceId: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /* ----- data source query ---------------------------------------- */
  const {
    data: dataSource,
    isLoading,
    isError,
    error,
  } = useDataSource(dataSourceId);

  /* ----- reference check for delete-lock -------------------------- */
  const [isReferenceLocked, setIsReferenceLocked] = useState<boolean>(true);
  const [referencesLoading, setReferencesLoading] = useState<boolean>(true);

  useEffect(() => {
    if (!dataSourceId) return;
    let cancelled = false;

    (async () => {
      try {
        setReferencesLoading(true);
        const res = await get<{ hasReferences: boolean }>(
          `/v1/datasources/${dataSourceId}/references`,
        );
        if (!cancelled) {
          setIsReferenceLocked(res?.object?.hasReferences ?? false);
        }
      } catch {
        /* On error, default to locked to avoid accidental deletion */
        if (!cancelled) {
          setIsReferenceLocked(true);
        }
      } finally {
        if (!cancelled) {
          setReferencesLoading(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [dataSourceId]);

  /* ----- delete mutation ------------------------------------------ */
  const deleteMutation = useMutation<ApiResponse<unknown>, Error>({
    mutationFn: () => del<unknown>(`/v1/datasources/${dataSourceId}`),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['datasources'] });
      navigate('/admin/data-sources');
    },
  });

  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);

  const handleDeleteClick = useCallback(() => {
    setShowDeleteConfirm(true);
  }, []);

  const handleConfirmDelete = useCallback(() => {
    deleteMutation.mutate();
    setShowDeleteConfirm(false);
  }, [deleteMutation]);

  /* ----- SQL preview modal ---------------------------------------- */
  const [showSqlModal, setShowSqlModal] = useState(false);
  const [sqlResult, setSqlResult] = useState<string>('');

  const generateSqlMutation = useGenerateSql();

  const handleShowSql = useCallback(() => {
    if (!dataSource || !isDatabaseDataSource(dataSource)) return;

    const params = dataSource.parameters ?? [];
    generateSqlMutation.mutate(
      {
        eqlText: dataSource.eqlText ?? '',
        parameters: params.map((p) => ({
          name: p.name.startsWith('@') ? p.name.slice(1) : p.name,
          value: p.value ?? '',
        })),
        entityName: dataSource.entityName ?? undefined,
      },
      {
        onSuccess: (result) => {
          setSqlResult(result?.sql ?? '');
          setShowSqlModal(true);
        },
      },
    );
  }, [dataSource, generateSqlMutation]);

  /* ----- Sample data preview modal -------------------------------- */
  const [showDataModal, setShowDataModal] = useState(false);
  const [dataResult, setDataResult] = useState<string>('');

  const executeQueryMutation = useExecuteAdHocQuery();

  const handleShowData = useCallback(() => {
    if (!dataSource) return;

    const isDb = isDatabaseDataSource(dataSource);
    const eqlText = isDb ? (dataSource as DatabaseDataSource).eqlText ?? '' : '';
    const params = dataSource.parameters ?? [];

    executeQueryMutation.mutate(
      {
        eqlText,
        parameters: params.map((p) => ({
          name: p.name.startsWith('@') ? p.name.slice(1) : p.name,
          value: p.value ?? '',
        })),
        returnTotal: dataSource.returnTotal ?? false,
      },
      {
        onSuccess: (result) => {
          setDataResult(JSON.stringify(result, null, 2));
          setShowDataModal(true);
        },
      },
    );
  }, [dataSource, executeQueryMutation]);

  /* ----- derived state -------------------------------------------- */
  const isDatabase =
    dataSource != null && dataSource.type === DataSourceType.Database;
  const isCode =
    dataSource != null && dataSource.type === DataSourceType.Code;
  const parameters: DataSourceParameter[] = dataSource?.parameters ?? [];

  // For code type, both manage and delete are locked
  const isDeleteDisabled = isCode || isReferenceLocked || referencesLoading;
  const isManageLocked = isCode;

  /* ================================================================ */
  /*  RENDER                                                           */
  /* ================================================================ */

  /* Loading state */
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[20rem]">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600" />
      </div>
    );
  }

  /* Error state */
  if (isError || !dataSource) {
    return (
      <div className="rounded-md bg-red-50 p-4">
        <p className="text-sm text-red-700">
          {(error as Error)?.message ??
            'Failed to load data source. It may have been deleted.'}
        </p>
        <Link
          to="/admin/data-sources"
          className="mt-2 inline-block text-sm font-medium text-red-700 underline"
        >
          Back to Data Sources
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* ---------------------------------------------------------- */}
      {/*  PAGE HEADER                                                */}
      {/* ---------------------------------------------------------- */}
      <header className="flex flex-wrap items-center justify-between gap-4">
        <div className="flex items-center gap-3">
          <Link
            to="/admin/data-sources"
            className="text-sm font-medium text-slate-500 hover:text-slate-700"
          >
            ← Data Sources
          </Link>
          <span className="text-slate-300">/</span>
          <h1 className="text-xl font-semibold text-slate-900">
            {dataSource.name}
          </h1>
        </div>

        {/* Header actions */}
        <div className="flex items-center gap-2">
          {/* Manage (database only) */}
          {isDatabase && !isManageLocked && (
            <Link
              to={`/admin/data-sources/${dataSourceId}/manage`}
              className="inline-flex items-center gap-1.5 rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-4 w-4"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path d="M17.414 2.586a2 2 0 00-2.828 0L7 10.172V13h2.828l7.586-7.586a2 2 0 000-2.828z" />
                <path
                  fillRule="evenodd"
                  d="M2 6a2 2 0 012-2h4a1 1 0 010 2H4v10h10v-4a1 1 0 112 0v4a2 2 0 01-2 2H4a2 2 0 01-2-2V6z"
                  clipRule="evenodd"
                />
              </svg>
              Manage
            </Link>
          )}

          {isManageLocked && (
            <span
              className="inline-flex items-center gap-1.5 rounded-md border border-slate-200 bg-slate-100 px-3 py-1.5 text-sm font-medium text-slate-400 cursor-not-allowed"
              title="Code data sources cannot be managed from the UI"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-4 w-4"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M5 9V7a5 5 0 0110 0v2a2 2 0 012 2v5a2 2 0 01-2 2H5a2 2 0 01-2-2v-5a2 2 0 012-2zm8-2v2H7V7a3 3 0 016 0z"
                  clipRule="evenodd"
                />
              </svg>
              Manage Locked
            </span>
          )}

          {/* Delete */}
          {!isDeleteDisabled ? (
            <button
              type="button"
              onClick={handleDeleteClick}
              disabled={deleteMutation.isPending}
              className="inline-flex items-center gap-1.5 rounded-md border border-red-300 bg-white px-3 py-1.5 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-4 w-4"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z"
                  clipRule="evenodd"
                />
              </svg>
              {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
            </button>
          ) : (
            <span
              className="inline-flex items-center gap-1.5 rounded-md border border-slate-200 bg-slate-100 px-3 py-1.5 text-sm font-medium text-slate-400 cursor-not-allowed"
              title={
                isCode
                  ? 'Code data sources cannot be deleted from the UI'
                  : 'This data source is referenced by one or more pages'
              }
            >
              <svg
                xmlns="http://www.w3.org/2000/svg"
                className="h-4 w-4"
                viewBox="0 0 20 20"
                fill="currentColor"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  d="M5 9V7a5 5 0 0110 0v2a2 2 0 012 2v5a2 2 0 01-2 2H5a2 2 0 01-2-2v-5a2 2 0 012-2zm8-2v2H7V7a3 3 0 016 0z"
                  clipRule="evenodd"
                />
              </svg>
              Delete Locked
            </span>
          )}
        </div>
      </header>

      {/* ---------------------------------------------------------- */}
      {/*  TYPE CARD                                                   */}
      {/* ---------------------------------------------------------- */}
      <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="p-5">
          {isDatabase ? (
            <div className="flex items-center gap-4">
              <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-lg bg-purple-100 text-purple-700">
                {/* Database icon */}
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  className="h-6 w-6"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path d="M3 12v3c0 1.657 3.134 3 7 3s7-1.343 7-3v-3c0 1.657-3.134 3-7 3s-7-1.343-7-3z" />
                  <path d="M3 7v3c0 1.657 3.134 3 7 3s7-1.343 7-3V7c0 1.657-3.134 3-7 3S3 8.657 3 7z" />
                  <path d="M17 5c0 1.657-3.134 3-7 3S3 6.657 3 5s3.134-3 7-3 7 1.343 7 3z" />
                </svg>
              </div>
              <div>
                <h2 className="text-base font-semibold text-slate-900">
                  Database
                </h2>
                <p className="text-sm text-slate-500">
                  SQL Select from the database via the EQL syntax
                </p>
              </div>
            </div>
          ) : (
            <div className="flex items-center gap-4">
              <div className="flex h-12 w-12 shrink-0 items-center justify-center rounded-lg bg-pink-100 text-pink-700">
                {/* Code icon */}
                <svg
                  xmlns="http://www.w3.org/2000/svg"
                  className="h-6 w-6"
                  viewBox="0 0 20 20"
                  fill="currentColor"
                  aria-hidden="true"
                >
                  <path
                    fillRule="evenodd"
                    d="M12.316 3.051a1 1 0 01.633 1.265l-4 12a1 1 0 11-1.898-.632l4-12a1 1 0 011.265-.633zM5.707 6.293a1 1 0 010 1.414L3.414 10l2.293 2.293a1 1 0 11-1.414 1.414l-3-3a1 1 0 010-1.414l3-3a1 1 0 011.414 0zm8.586 0a1 1 0 011.414 0l3 3a1 1 0 010 1.414l-3 3a1 1 0 11-1.414-1.414L16.586 10l-2.293-2.293a1 1 0 010-1.414z"
                    clipRule="evenodd"
                  />
                </svg>
              </div>
              <div>
                <h2 className="text-base font-semibold text-slate-900">
                  Code
                </h2>
                <p className="text-sm text-slate-500">
                  Data source generated by a method with a specific attribute
                </p>
              </div>
            </div>
          )}
        </div>
      </section>

      {/* ---------------------------------------------------------- */}
      {/*  METADATA FIELDS (read-only)                                 */}
      {/* ---------------------------------------------------------- */}
      <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
        <div className="divide-y divide-slate-100">
          {/* Name */}
          <ReadOnlyRow label="Name" value={dataSource.name} />

          {/* Description */}
          <ReadOnlyRow
            label="Description"
            value={dataSource.description ?? '—'}
          />

          {/* Result Model */}
          <ReadOnlyRow
            label="Result Model"
            value={dataSource.resultModel ?? '—'}
          />

          {/* Weight */}
          <ReadOnlyRow
            label="Weight"
            value={String(dataSource.weight ?? 0)}
          />

          {/* Entity Name */}
          <ReadOnlyRow
            label="Entity"
            value={dataSource.entityName ?? '—'}
          />

          {/* Return Total */}
          <div className="flex items-center gap-8 px-5 py-3">
            <span className="w-40 shrink-0 text-sm font-medium text-slate-500">
              Return Total
            </span>
            <span className="text-sm text-slate-900">
              {dataSource.returnTotal ? (
                <span className="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800">
                  Yes
                </span>
              ) : (
                <span className="inline-flex items-center rounded-full bg-slate-100 px-2.5 py-0.5 text-xs font-medium text-slate-600">
                  No
                </span>
              )}
            </span>
          </div>
        </div>
      </section>

      {/* ---------------------------------------------------------- */}
      {/*  PARAMETERS                                                  */}
      {/* ---------------------------------------------------------- */}
      {parameters.length > 0 && (
        <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-100 px-5 py-3">
            <h3 className="text-sm font-semibold text-slate-900">
              Parameters
            </h3>
          </div>
          <div className="flex flex-wrap gap-2 px-5 py-4">
            {parameters.map((param, idx) => (
              <span
                key={`${param.name}-${idx}`}
                className="inline-flex items-center gap-1 rounded-md bg-indigo-50 px-2.5 py-1 text-xs font-medium text-indigo-700 ring-1 ring-inset ring-indigo-200"
              >
                <span className="font-semibold">{param.name}</span>
                <span className="text-indigo-400">??</span>
                <span className="text-cyan-700">{param.value ?? ''}</span>
                <span className="text-yellow-700">({param.type ?? 'text'})</span>
              </span>
            ))}
          </div>
        </section>
      )}

      {/* ---------------------------------------------------------- */}
      {/*  EQL / CODE DISPLAY                                          */}
      {/* ---------------------------------------------------------- */}
      {isDatabase && isDatabaseDataSource(dataSource) && (
        <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-100 px-5 py-3">
            <h3 className="text-sm font-semibold text-slate-900">
              EQL Input
            </h3>
          </div>
          <div className="p-5">
            <pre className="overflow-x-auto rounded-md bg-slate-900 p-4 text-sm text-slate-100 font-mono leading-relaxed">
              {dataSource.eqlText || '(empty)'}
            </pre>
          </div>
        </section>
      )}

      {isCode && (
        <section className="rounded-lg border border-slate-200 bg-white shadow-sm">
          <div className="border-b border-slate-100 px-5 py-3">
            <h3 className="text-sm font-semibold text-slate-900">
              Full Class
            </h3>
          </div>
          <div className="p-5">
            <pre className="overflow-x-auto rounded-md bg-slate-900 p-4 text-sm text-slate-100 font-mono leading-relaxed">
              {(dataSource as unknown as { fullClassName?: string })
                .fullClassName || '(empty)'}
            </pre>
          </div>
        </section>
      )}

      {/* ---------------------------------------------------------- */}
      {/*  TEST / PREVIEW ACTIONS                                      */}
      {/* ---------------------------------------------------------- */}
      <section className="flex flex-wrap gap-3">
        {/* Show SQL — database type only */}
        {isDatabase && (
          <button
            type="button"
            onClick={handleShowSql}
            disabled={generateSqlMutation.isPending}
            className="inline-flex items-center gap-1.5 rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:opacity-50"
          >
            {generateSqlMutation.isPending ? 'Generating…' : 'Show SQL'}
          </button>
        )}

        {/* Show Sample Data — both types */}
        <button
          type="button"
          onClick={handleShowData}
          disabled={executeQueryMutation.isPending}
          className="inline-flex items-center gap-1.5 rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-500 disabled:opacity-50"
        >
          {executeQueryMutation.isPending
            ? 'Executing…'
            : 'Sample Data as JSON'}
        </button>
      </section>

      {/* ---------------------------------------------------------- */}
      {/*  MODALS                                                      */}
      {/* ---------------------------------------------------------- */}

      {/* SQL Result Modal */}
      <Modal
        isVisible={showSqlModal}
        onClose={() => setShowSqlModal(false)}
        title="SQL Result"
        size={ModalSize.Large}
        id="modal-sql-result"
      >
        <pre className="overflow-x-auto rounded-md bg-slate-900 p-4 text-sm text-slate-100 font-mono leading-relaxed max-h-[60vh] overflow-y-auto">
          {sqlResult || '(no SQL generated)'}
        </pre>
      </Modal>

      {/* Sample Data Result Modal */}
      <Modal
        isVisible={showDataModal}
        onClose={() => setShowDataModal(false)}
        title="Sample Data Result"
        size={ModalSize.Large}
        id="modal-data-result"
      >
        <pre className="overflow-x-auto rounded-md bg-slate-900 p-4 text-sm text-slate-100 font-mono leading-relaxed max-h-[60vh] overflow-y-auto">
          {dataResult || '(no data)'}
        </pre>
      </Modal>

      {/* Delete Confirmation Modal */}
      <Modal
        isVisible={showDeleteConfirm}
        onClose={() => setShowDeleteConfirm(false)}
        title="Confirm Delete"
        size={ModalSize.Normal}
        id="modal-delete-confirm"
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setShowDeleteConfirm(false)}
              className="rounded-md border border-slate-300 bg-white px-4 py-2 text-sm font-medium text-slate-700 shadow-sm hover:bg-slate-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-slate-500"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={handleConfirmDelete}
              disabled={deleteMutation.isPending}
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:opacity-50"
            >
              {deleteMutation.isPending ? 'Deleting…' : 'Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-slate-600">
          Are you sure you want to delete the data source{' '}
          <strong className="font-semibold text-slate-900">
            {dataSource.name}
          </strong>
          ? This action cannot be undone.
        </p>
      </Modal>

      {/* Delete mutation error */}
      {deleteMutation.isError && (
        <div className="rounded-md bg-red-50 p-4">
          <p className="text-sm text-red-700">
            {(deleteMutation.error as Error)?.message ??
              'Failed to delete data source.'}
          </p>
        </div>
      )}
    </div>
  );
}

/* ------------------------------------------------------------------ */
/*  ReadOnlyRow — simple label + value display                         */
/* ------------------------------------------------------------------ */

interface ReadOnlyRowProps {
  label: string;
  value: string;
}

function ReadOnlyRow({ label, value }: ReadOnlyRowProps): React.JSX.Element {
  return (
    <div className="flex items-center gap-8 px-5 py-3">
      <span className="w-40 shrink-0 text-sm font-medium text-slate-500">
        {label}
      </span>
      <span className="text-sm text-slate-900 break-words">{value}</span>
    </div>
  );
}
