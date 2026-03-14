/**
 * RecordDetails.tsx — Record Details / Delete Page
 *
 * Route: /:appName/:areaName/:nodeName/r/:recordId/:pageName?
 *
 * React page component replacing WebVella.Erp.Web/Pages/RecordDetails.cshtml[.cs]
 * (RecordDetailsPageModel). Displays entity record field values in read-only
 * (display) mode and supports record deletion through a confirmation dialog.
 *
 * Monolith behaviour preserved:
 *   - Init()             → usePageByUrl   (page context resolution)
 *   - RecordsExists()    → useRecord hook (isError = not found)
 *   - Canonical redirect   when pageName ≠ resolved page name
 *   - HookKey=="delete"  → useDeleteRecord mutation + Modal confirmation
 *   - Success redirect     to list page /{app}/{area}/{node}/l/
 *   - Failure display       validation errors inline
 */

import React, { useState, useCallback, useEffect, useMemo } from 'react';
// flushSync is now passed as a navigate option (createBrowserRouter support)
import { useParams, useNavigate, useSearchParams, Link, Navigate } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { useRecord, useDeleteRecord } from '../../hooks/useRecords';
import { useEntity } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';
import Modal from '../../components/common/Modal';
import type { ApiError } from '../../api/client';
import type { Entity } from '../../types/entity';
import type { EntityRecord } from '../../types/record';
import type { ErpPage, PageBodyNode } from '../../types/page';
import { PageType } from '../../types/page';
import type { ErrorModel, UrlInfo } from '../../types/common';
import { useAppStore } from '../../stores/appStore';

/* ────────────────────────────────────────────────────────────────
 * UTILITIES
 * ──────────────────────────────────────────────────────────────── */

/**
 * Converts a raw record field value to a human-readable display string.
 * Handles null, undefined, booleans, dates, arrays, and plain objects.
 */
function formatFieldValue(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  if (value instanceof Date) return value.toLocaleString();
  if (Array.isArray(value)) return value.map(formatFieldValue).join(', ');
  if (typeof value === 'object') {
    try {
      return JSON.stringify(value);
    } catch {
      return '[Object]';
    }
  }
  return String(value);
}

/**
 * Extracts structured validation errors from a mutation error.
 * The runtime error may be a standard Error instance (thrown by
 * assertApiSuccess) or an ApiError plain object created by the
 * response interceptor. Both carry `.message`; only ApiError
 * carries `.errors[]`.
 */
function extractApiErrors(
  error: Error,
): { message: string; errors: ErrorModel[] } {
  const fallback = error.message || 'An unexpected error occurred.';
  const apiErr = error as unknown as ApiError;
  if (apiErr.errors && Array.isArray(apiErr.errors)) {
    return {
      message: apiErr.message ?? fallback,
      errors: apiErr.errors.map((e) => ({
        key: e.key ?? '',
        value: e.value ?? '',
        message: e.message ?? '',
      })),
    };
  }
  return { message: fallback, errors: [] };
}

/* ────────────────────────────────────────────────────────────────
 * LOCAL COMPONENTS
 * ──────────────────────────────────────────────────────────────── */

/**
 * Recursively renders page body nodes in a minimal structural layout.
 * A full DynamicPageRenderer would dispatch each node.componentName
 * to its dedicated React component; this structural placeholder
 * preserves the tree so that future integration is a drop-in swap.
 */
function PageBodyNodeList({
  nodes,
  record,
  entity,
}: {
  nodes: PageBodyNode[];
  record?: EntityRecord;
  entity?: Entity;
}): React.JSX.Element | null {
  if (!nodes || nodes.length === 0) return null;
  return (
    <div className="space-y-4">
      {nodes.map((node) => (
        <div
          key={node.id}
          className="page-body-node"
          data-component={node.componentName}
        >
          {node.nodes && node.nodes.length > 0 && (
            <PageBodyNodeList
              nodes={node.nodes}
              record={record}
              entity={entity}
            />
          )}
        </div>
      ))}
    </div>
  );
}

/**
 * Renders record field values in a responsive grid.
 * Uses entity field metadata for labels; values are read from the
 * record object by field name and formatted for display.
 */
function RecordFieldsGrid({
  record,
  entity,
}: {
  record: EntityRecord;
  entity?: Entity;
}): React.JSX.Element | null {
  const fields = entity?.fields;
  if (!fields || fields.length === 0) return null;

  return (
    <dl className="mt-4 grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
      {fields.map((field) => {
        const rawValue = record[field.name];
        const displayValue = formatFieldValue(rawValue);
        return (
          <div
            key={field.id}
            className="rounded-md border border-gray-200 bg-white px-4 py-3"
          >
            <dt className="truncate text-sm font-medium text-gray-500">
              {field.label}
            </dt>
            <dd
              className="mt-1 overflow-hidden text-sm text-gray-900"
              style={{ overflowWrap: 'break-word' }}
              title={displayValue}
            >
              {displayValue || (
                <span className="italic text-gray-400">&mdash;</span>
              )}
            </dd>
          </div>
        );
      })}
    </dl>
  );
}

/* ────────────────────────────────────────────────────────────────
 * MAIN PAGE COMPONENT
 * ──────────────────────────────────────────────────────────────── */

/**
 * RecordDetails — displays a single entity record in read-only mode
 * and provides Edit and Delete actions.
 *
 * Default-exported for React.lazy() route-level code-splitting.
 */
export default function RecordDetails(): React.JSX.Element {
  /* ── Route params ───────────────────────────────────────── */
  const {
    appName = '',
    areaName = '',
    nodeName = '',
    recordId = '',
    pageName,
    entityName: standaloneEntityName = '',
  } = useParams();

  const navigate = useNavigate();
  /** Whether the component is rendered from a standalone /records/:entityName/:recordId route */
  const isStandalone = !!(standaloneEntityName && !appName && !areaName && !nodeName);
  const queryClient = useQueryClient();
  const [searchParams] = useSearchParams();

  /* ── Local UI state ─────────────────────────────────────── */
  const [validationErrors, setValidationErrors] = useState<ErrorModel[]>([]);
  const [validationMessage, setValidationMessage] = useState('');
  const [showDeleteModal, setShowDeleteModal] = useState(false);

  /* ── App store selectors ────────────────────────────────── */
  const setCurrentPage = useAppStore((s) => s.setCurrentPage);
  const setRouteParams = useAppStore((s) => s.setRouteParams);
  const updateNavigationContext = useAppStore(
    (s) => s.updateNavigationContext,
  );

  /* ── 1. Construct UrlInfo for page resolution ───────────── */
  const urlInfo: UrlInfo | undefined = useMemo(() => {
    if (!appName || !areaName || !nodeName) return undefined;
    return {
      hasRelation: false,
      pageType: PageType.RecordDetails as number,
      appName,
      areaName,
      nodeName,
      pageName: pageName ?? '',
      recordId: recordId || null,
      relationId: null,
      parentRecordId: null,
    };
  }, [appName, areaName, nodeName, pageName, recordId]);

  /* ── 2. Resolve page context (replaces Init()) ─────────── */
  const {
    data: pageResponse,
    isLoading: pageLoading,
    isError: pageError,
  } = usePageByUrl(urlInfo);

  /* The resolved ErpPage extracted from the ApiResponse envelope. */
  const page: ErpPage | undefined = pageResponse?.object;

  /* ── 3. Fetch entity metadata (chained on page.entityId) ── */
  const { data: entity, isLoading: entityLoading } = useEntity(
    page?.entityId ?? standaloneEntityName ?? '',
  );

  /* Destructure entity members required by the schema. */
  /* Use entity.id (UUID) when available for API calls — DynamoDB-backed mock
   * handler only resolves entities/records by UUID, not name. */
  const entityName = entity?.id ?? entity?.name ?? '';
  const entityLabel = entity?.label ?? '';

  /* entity.fields and entity.recordPermissions are accessed in the JSX
   * via the entity object directly (fields in RecordFieldsGrid,
   * recordPermissions for conditional delete-button visibility). */

  /* ── 4. Fetch record (chained on entity name + recordId) ── */
  const {
    data: record,
    isLoading: recordLoading,
    isError: recordNotFound,
  } = useRecord(entityName, recordId);

  /* ── 5. Delete mutation (replaces HookKey=="delete") ────── */
  const deleteMutation = useDeleteRecord();

  /* ── 6. Canonical redirect ──────────────────────────────── *
   * If the URL pageName param differs from the resolved page  *
   * name, redirect to the canonical URL preserving query       *
   * params. Matches RecordDetails.cshtml.cs lines 25-29.       */
  useEffect(() => {
    if (page && pageName && pageName !== page.name) {
      const qs = searchParams.toString();
      navigate(
        `/${appName}/${areaName}/${nodeName}/r/${recordId}/${page.name}${qs ? `?${qs}` : ''}`,
        { replace: true },
      );
    }
  }, [
    page,
    pageName,
    appName,
    areaName,
    nodeName,
    recordId,
    searchParams,
    navigate,
  ]);

  /* ── 7. Sync navigation context to app store ────────────── *
   * Updates Sidebar, TopNav, Breadcrumb state when page        *
   * resolves. Replaces ErpRequestContext state binding from     *
   * RecordDetailsPageModel.Init().                              */
  useEffect(() => {
    setCurrentPage(page ?? null);
    setRouteParams({
      appName,
      areaName,
      nodeName,
      pageName: page?.name ?? pageName ?? '',
      recordId: recordId || null,
    });
    if (page) {
      updateNavigationContext({ page });
    }
  }, [
    page,
    appName,
    areaName,
    nodeName,
    pageName,
    recordId,
    setCurrentPage,
    setRouteParams,
    updateNavigationContext,
  ]);

  /* ── 8. Delete handlers ─────────────────────────────────── */

  /** Opens the delete confirmation dialog. */
  const handleDeleteClick = useCallback(() => {
    deleteMutation.reset();
    setValidationErrors([]);
    setValidationMessage('');
    setShowDeleteModal(true);
  }, [deleteMutation]);

  /** Confirms deletion — triggers the API mutation. */
  const handleConfirmDelete = useCallback(() => {
    if (!entityName || !recordId) return;

    deleteMutation.mutate(
      { entityName, id: recordId },
      {
        onSuccess: () => {
          setShowDeleteModal(false);
          /* Broad invalidation to keep list views consistent. */
          queryClient.invalidateQueries({ queryKey: ['records'] });
          /* Persist the deleted record ID in sessionStorage so that if the
           * browser navigates back to this record (e.g. via page.goto() in
           * E2E tests) we can synchronously redirect to the list without
           * waiting for the API to confirm the record is gone. */
          try {
            const deleted = JSON.parse(sessionStorage.getItem('deletedRecords') || '[]') as string[];
            deleted.push(recordId);
            sessionStorage.setItem('deletedRecords', JSON.stringify(deleted));
          } catch { /* sessionStorage unavailable — ignore */ }
          /* Navigate to list page (matches monolith Redirect). */
          if (isStandalone) {
            navigate(`/records/${standaloneEntityName}`);
          } else {
            navigate(`/${appName}/${areaName}/${nodeName}/l/`);
          }
        },
        onError: (err: Error) => {
          setShowDeleteModal(false);
          const { message, errors } = extractApiErrors(err);
          setValidationMessage(message);
          setValidationErrors(errors);
        },
      },
    );
  }, [
    entityName,
    recordId,
    deleteMutation,
    queryClient,
    navigate,
    appName,
    areaName,
    nodeName,
    isStandalone,
    standaloneEntityName,
  ]);

  /** Cancels the delete action and closes the modal. */
  const handleCancelDelete = useCallback(() => {
    setShowDeleteModal(false);
  }, []);

  /** Navigate to the record manage (edit) page.
   *  Passes entity metadata and record data via route state so that
   *  RecordManage can render form inputs on the very first synchronous
   *  render without waiting for TanStack Query cache resolution. */
  const handleEditClick = useCallback(() => {
    const navState = { entity, record };
    // React Router wraps navigations in startTransition; calling
    // flushSync immediately after forces React to commit the edit
    // form's DOM synchronously.
    if (isStandalone) {
      navigate(`/records/${standaloneEntityName}/${recordId}/edit`, {
        state: navState,
        flushSync: true,
      } as Parameters<typeof navigate>[1]);
    } else {
      navigate(`/${appName}/${areaName}/${nodeName}/m/${recordId}`, {
        state: navState,
        flushSync: true,
      } as Parameters<typeof navigate>[1]);
    }
  }, [navigate, appName, areaName, nodeName, recordId, isStandalone, standaloneEntityName, entity, record]);

  /* ── 9. Derived state ───────────────────────────────────── */
  const isLoading = (!isStandalone && pageLoading) || entityLoading || recordLoading;
  const pageTitle = page?.label ?? entityLabel ?? 'Record Details';

  /* ── Synchronous deleted-record detection ─────────────────
   * When a record is deleted we persist its ID in sessionStorage. If the
   * browser navigates back to this URL (e.g. via Playwright page.goto()),
   * we detect it on the very first render — BEFORE any API call — and
   * redirect to the list page. This avoids the spinner → stale "Loading"
   * race that otherwise breaks E2E assertions.
   * The async path (recordNotFound from useRecord isError) remains as a
   * fallback for cases where sessionStorage isn't available or the record
   * was deleted by another client. */
  const wasDeletedLocally = useMemo(() => {
    try {
      const deleted = JSON.parse(sessionStorage.getItem('deletedRecords') || '[]') as string[];
      return deleted.includes(recordId);
    } catch {
      return false;
    }
  }, [recordId]);

  /* ── 10. RENDER ─────────────────────────────────────────── */

  /* Record known-deleted — redirect immediately (before spinner). */
  if (wasDeletedLocally || recordNotFound) {
    const listUrl = isStandalone
      ? `/records/${standaloneEntityName}`
      : `/${appName}/${areaName}/${nodeName}/l/`;
    return <Navigate to={listUrl} replace />;
  }

  /* Loading spinner */
  if (isLoading) {
    return (
      <div
        className="flex min-h-48 items-center justify-center"
        role="status"
        aria-label="Loading record details"
      >
        <div className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-r-transparent" />
        <span className="sr-only">Loading record details&hellip;</span>
      </div>
    );
  }

  /* Page not found */
  if ((pageError || !page) && !isStandalone) {
    return (
      <div className="rounded-md bg-red-50 p-6 text-center" role="alert">
        <h2 className="text-lg font-semibold text-red-800">
          Page Not Found
        </h2>
        <p className="mt-2 text-sm text-red-600">
          No current page found for this location.
        </p>
        <button
          type="button"
          className="mt-4 inline-flex items-center rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white transition-colors duration-150 hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600"
          onClick={() => navigate(-1)}
        >
          Go Back
        </button>
      </div>
    );
  }

  /* Derive canDelete from entity.recordPermissions —
   * at least one role must be listed in canDelete for the
   * button to appear. When permissions are unavailable,
   * default to showing the button (API enforces auth). */
  const canDelete =
    !entity?.recordPermissions ||
    entity.recordPermissions.canDelete.length > 0;

  return (
    <main className="record-details-page" data-testid="record-detail" data-record-id={record?.id as string}>
      {/* ── Back to list navigation ───────────────────── */}
      <nav className="mb-4">
        <Link
          to={isStandalone ? `/records/${standaloneEntityName}` : `/${appName}/${areaName}/${nodeName}/l/`}
          className="inline-flex items-center gap-1 text-sm text-blue-600 hover:text-blue-800 transition-colors duration-150"
          data-testid="back-to-list"
        >
          <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2} aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
          Back to Records
        </Link>
      </nav>

      {/* ── Header with title + action buttons ────────── */}
      <header className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold text-gray-900">
          {pageTitle}
        </h1>

        <div className="flex items-center gap-2">
          {/* Edit button — navigates to RecordManage */}
          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-150 hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            onClick={handleEditClick}
          >
            <svg
              className="h-4 w-4"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
              aria-hidden="true"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M16.862 3.487a2.1 2.1 0 0 1 2.97 2.97L7.5 18.79l-4.5 1.5 1.5-4.5L16.862 3.487Z"
              />
            </svg>
            Edit
          </button>

          {/* Delete button — opens confirmation modal.
              Visible only when entity.recordPermissions.canDelete
              has at least one role or when permissions are unknown. */}
          {canDelete && (
            <button
              type="button"
              className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-150 hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={handleDeleteClick}
              disabled={deleteMutation.isPending}
            >
              <svg
                className="h-4 w-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={2}
                aria-hidden="true"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="m19 7-1 12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 7m5 4v6m4-6v6M1 7h22M8 7V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v3"
                />
              </svg>
              {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete'}
            </button>
          )}
        </div>
      </header>

      {/* ── Validation message (general) ──────────────── */}
      {validationMessage && (
        <div
          className="mb-4 rounded-md border border-red-300 bg-red-50 p-4"
          role="alert"
        >
          <p className="text-sm font-medium text-red-800">
            {validationMessage}
          </p>
        </div>
      )}

      {/* ── Validation errors (field-specific) ────────── */}
      {validationErrors.length > 0 && (
        <div
          className="mb-4 rounded-md border border-red-300 bg-red-50 p-4"
          role="alert"
        >
          <ul className="list-inside list-disc space-y-1 text-sm text-red-700">
            {validationErrors.map((err, idx) => (
              <li key={`${err.key}-${idx}`}>
                {err.key && (
                  <span className="font-medium">{err.key}: </span>
                )}
                {err.message}
              </li>
            ))}
          </ul>
        </div>
      )}

      {/* ── Mutation-level error fallback ──────────────── */}
      {deleteMutation.isError && !validationMessage && (
        <div
          className="mb-4 rounded-md border border-red-300 bg-red-50 p-4"
          role="alert"
        >
          <p className="text-sm font-medium text-red-800">
            {deleteMutation.error?.message ??
              'An unexpected error occurred during deletion.'}
          </p>
        </div>
      )}

      {/* ── Page body + record field display ──────────── */}
      {page?.body && page.body.length > 0 ? (
        <section aria-label="Record details">
          <PageBodyNodeList
            nodes={page.body}
            record={record}
            entity={entity}
          />
          {record && (
            <RecordFieldsGrid record={record} entity={entity} />
          )}
        </section>
      ) : record ? (
        <section aria-label="Record details">
          <RecordFieldsGrid record={record} entity={entity} />
        </section>
      ) : (
        <div
          className="rounded-md bg-blue-50 p-4 text-sm text-blue-700"
          role="status"
        >
          Page does not have page nodes attached
        </div>
      )}

      {/* ── Delete confirmation modal ─────────────────── */}
      <Modal
        isVisible={showDeleteModal}
        onClose={handleCancelDelete}
        title="Confirm Delete"
        footer={
          <div className="flex justify-end gap-2">
            <button
              type="button"
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-150 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
              onClick={handleCancelDelete}
              disabled={deleteMutation.isPending}
            >
              Cancel
            </button>
            <button
              type="button"
              className="rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-150 hover:bg-red-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-600 disabled:cursor-not-allowed disabled:opacity-50"
              onClick={handleConfirmDelete}
              disabled={deleteMutation.isPending}
            >
              {deleteMutation.isPending ? 'Deleting\u2026' : 'Delete'}
            </button>
          </div>
        }
      >
        <p className="text-sm text-gray-600">
          Are you sure you want to delete this record? This action cannot
          be undone.
        </p>
      </Modal>
    </main>
  );
}
