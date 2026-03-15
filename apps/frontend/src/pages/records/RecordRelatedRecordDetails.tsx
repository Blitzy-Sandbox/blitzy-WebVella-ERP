/**
 * RecordRelatedRecordDetails — Related Record Details Page
 *
 * Route: /:appName/:areaName/:nodeName/r/:recordId/rl/:relationId/r/:relatedRecordId/:pageName?
 *
 * React page component replacing
 * `WebVella.Erp.Web/Pages/RecordRelatedRecordDetails.cshtml` /
 * `RecordRelatedRecordDetails.cshtml.cs`
 * (`RecordRelatedRecordDetailsPageModel`).
 *
 * Displays the details of a record that is related to a parent record
 * via an entity relation. The page renders field values in read-only
 * (display) mode using the resolved page body tree and entity metadata.
 *
 * Monolith behaviour preserved:
 *   - Init()             → usePageByUrl   (page context resolution)
 *   - RecordsExists()    → two useRecord calls (parent + related)
 *   - Canonical redirect   when pageName ≠ resolved page.name
 *   - IPageHook global     → no client equivalent (server-side only)
 *   - BeforeRender()       → entity metadata + record data fetching
 *   - Page body rendering  → PageBodyNodeList + RecordFieldsGrid
 *
 * Key differences from the monolith:
 *   - No antiforgery tokens — JWT Bearer auth
 *   - No standard delete behaviour (unlike RecordDetails) — monolith's
 *     IRecordRelatedRecordDetailsPageHook.OnPost() is replaced by
 *     specific API mutation calls triggered by custom UI actions
 *   - Pre/post hooks replaced by API-level validation + SNS events
 *   - Page body ViewComponent loop replaced by PageBodyNodeList
 *   - Display mode ComponentMode.Display replaced by read-only field grid
 *
 * @module pages/records/RecordRelatedRecordDetails
 */

import React, { useState, useEffect, useMemo } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';

// Internal hooks
import { useRecord } from '../../hooks/useRecords';
import { useEntity } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';

// API types
import type { ApiError } from '../../api/client';

// Domain type imports
import type { Entity } from '../../types/entity';
import type { EntityRecord } from '../../types/record';
import type { ErpPage, PageBodyNode } from '../../types/page';
import { PageType } from '../../types/page';
import type { ErrorModel, UrlInfo } from '../../types/common';

// State store
import { useAppStore } from '../../stores/appStore';

// ---------------------------------------------------------------------------
// Route Parameter Types
// ---------------------------------------------------------------------------

/** Shape of URL parameters extracted by React Router for this route. */
interface RelatedRecordDetailsRouteParams {
  /** Application slug (e.g. "crm"). */
  appName: string;
  /** Sitemap area slug. */
  areaName: string;
  /** Sitemap node slug. */
  nodeName: string;
  /** Parent record GUID — the owning side of the relation. */
  recordId: string;
  /** Relation GUID linking parent and related records. */
  relationId: string;
  /** Related record GUID — the record whose details are displayed. */
  relatedRecordId: string;
  /** Optional page name slug for canonical URL matching. */
  pageName?: string;
}

// ---------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------

/**
 * Converts a raw record field value to a human-readable display string.
 * Handles null, undefined, booleans, dates, arrays, and plain objects
 * gracefully — never renders "null" or "undefined" as visible text.
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
 * Extracts structured validation errors from a mutation/query error.
 * The runtime error may be a standard Error instance or an ApiError
 * plain object created by the response interceptor. Both carry
 * `.message`; only ApiError carries `.errors[]`.
 */
function extractApiErrors(
  error: unknown,
): { message: string; errors: ErrorModel[] } {
  if (!error) return { message: 'An unexpected error occurred.', errors: [] };
  const errObj = error as Record<string, unknown>;
  const fallback =
    typeof errObj.message === 'string'
      ? errObj.message
      : 'An unexpected error occurred.';
  const apiErr = error as ApiError;
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

// ---------------------------------------------------------------------------
// Local Components
// ---------------------------------------------------------------------------

/**
 * Recursively renders page body nodes in a minimal structural layout.
 *
 * Each body node carries a `componentName` (e.g. "PcRow", "PcSection")
 * and nested `nodes` forming an arbitrary-depth layout tree. This renderer
 * preserves the tree structure so that a full DynamicPageRenderer can
 * progressively enhance individual component types without breaking
 * the structural contract.
 *
 * For detail-mode pages the renderer emits semantic containers with
 * data attributes for component identification and potential
 * enhancement by higher-level renderers.
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
          data-node-id={node.id}
          data-container-id={node.containerId}
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
 * Renders record field values in a responsive grid using entity field
 * metadata for labels. Values are read from the record object by
 * field name and formatted for display via `formatFieldValue`.
 *
 * Accessibility: uses a description list (`<dl>`) with term/detail
 * pairs for semantic field name–value presentation.
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

// ---------------------------------------------------------------------------
// Main Page Component
// ---------------------------------------------------------------------------

/**
 * RecordRelatedRecordDetails — displays the details of a related record
 * in read-only mode within a relation context.
 *
 * Default-exported for React.lazy() route-level code-splitting.
 */
export default function RecordRelatedRecordDetails(): React.JSX.Element {
  // ── URL parameters ──────────────────────────────────────────────────────
  const params = useParams<
    Record<keyof RelatedRecordDetailsRouteParams, string>
  >();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryClient = useQueryClient();

  const {
    appName = '',
    areaName = '',
    nodeName = '',
    recordId = '',
    relationId = '',
    relatedRecordId = '',
    pageName,
  } = params;

  // ── Local state for validation feedback ─────────────────────────────────
  // Validation state captures errors from any custom action API calls
  // that may be triggered by hook-driven UI actions on this page.
  const [validationErrors, setValidationErrors] = useState<ErrorModel[]>([]);
  const [validationMessage, setValidationMessage] = useState<string>('');

  // ── Global navigation store ─────────────────────────────────────────────
  const setCurrentPage = useAppStore((s) => s.setCurrentPage);
  const setRouteParams = useAppStore((s) => s.setRouteParams);
  const updateNavigationContext = useAppStore(
    (s) => s.updateNavigationContext,
  );

  // ── 1. Construct UrlInfo for page resolution ────────────────────────────
  // hasRelation: true differentiates this from a standard RecordDetails
  // page. parentRecordId and relationId establish the relation context.
  // recordId in UrlInfo maps to the related record ID (the record being
  // displayed), matching the monolith's routing where RecordId was the
  // related record within the relation context.
  const urlInfo: UrlInfo | undefined = useMemo(() => {
    if (!appName || !areaName || !nodeName) return undefined;
    return {
      hasRelation: true,
      pageType: PageType.RecordDetails as number,
      appName,
      areaName,
      nodeName,
      pageName: pageName ?? '',
      recordId: relatedRecordId || null,
      relationId: relationId || null,
      parentRecordId: recordId || null,
    };
  }, [appName, areaName, nodeName, pageName, recordId, relationId, relatedRecordId]);

  // ── 2. Resolve page context (replaces Init()) ──────────────────────────
  const {
    data: pageResponse,
    isLoading: pageLoading,
    isError: pageError,
  } = usePageByUrl(urlInfo, { enabled: !!urlInfo });

  // The resolved ErpPage extracted from the API response envelope.
  const page: ErpPage | undefined = pageResponse?.object ?? undefined;

  // ── 3. Canonical redirect (cshtml.cs lines 20-24) ──────────────────────
  // If the URL pageName param differs from the resolved page name,
  // redirect to the canonical URL preserving the query string and all
  // relation-aware path segments.
  useEffect(() => {
    if (!page) return;
    if (pageName && page.name && pageName !== page.name) {
      const qs = searchParams.toString();
      const canonicalPath =
        `/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/r/${relatedRecordId}/${page.name}` +
        (qs ? `?${qs}` : '');
      navigate(canonicalPath, { replace: true });
    }
  }, [
    page,
    pageName,
    appName,
    areaName,
    nodeName,
    recordId,
    relationId,
    relatedRecordId,
    searchParams,
    navigate,
  ]);

  // ── 4. Fetch entity metadata (chained on page.entityId) ────────────────
  const entityIdOrEmpty = page?.entityId ?? '';
  const {
    data: entity,
    isLoading: entityLoading,
  } = useEntity(entityIdOrEmpty);

  // Derive entity name for record operations (empty string disables hooks)
  const entityName = entity?.name ?? '';
  const entityLabel = entity?.label ?? '';

  // ── 5. Verify parent record existence (first half of RecordsExists()) ──
  const {
    data: parentRecord,
    isLoading: parentLoading,
    isError: parentNotFound,
  } = useRecord(entityName, recordId);

  // ── 6. Fetch related record data (second half of RecordsExists()) ──────
  const {
    data: relatedRecord,
    isLoading: relatedLoading,
    isError: relatedNotFound,
  } = useRecord(entityName, relatedRecordId);

  // ── 7. Sync navigation context to app store ────────────────────────────
  // Updates Sidebar, TopNav, Breadcrumb state when page resolves.
  // Replaces ErpRequestContext state binding from
  // RecordRelatedRecordDetailsPageModel.Init().
  useEffect(() => {
    setCurrentPage(page ?? null);
    setRouteParams({
      appName,
      areaName,
      nodeName,
      pageName: page?.name ?? pageName ?? '',
      recordId: recordId || null,
      relationId: relationId || null,
      parentRecordId: recordId || null,
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
    relationId,
    setCurrentPage,
    setRouteParams,
    updateNavigationContext,
  ]);

  // ── 8. Expose queryClient for custom action cache invalidation ─────────
  // Custom actions (hook-driven in monolith, now API mutation calls) may
  // use queryClient.invalidateQueries to refresh data after mutations.
  // This is kept available for custom action handlers passed to child
  // components that may trigger API mutations and need cache consistency.
  const handleCustomActionError = (error: unknown): void => {
    const { message, errors } = extractApiErrors(error);
    setValidationMessage(message);
    setValidationErrors(errors);
    // Invalidate both record queries so UI stays consistent after errors
    void queryClient.invalidateQueries({
      queryKey: ['record', relatedRecordId],
    });
  };

  // Reset validation state utility for custom action triggers
  const clearValidation = (): void => {
    setValidationMessage('');
    setValidationErrors([]);
  };

  // ── 9. Derived state ───────────────────────────────────────────────────
  const isLoading =
    pageLoading || entityLoading || parentLoading || relatedLoading;

  const pageTitle = page?.label ?? entityLabel ?? 'Related Record Details';

  // Not-found condition: page missing, either record missing, or API
  // returned 200 with empty payload rather than 404.
  const isNotFound =
    (!pageLoading && !page) ||
    parentNotFound ||
    (!parentLoading && !parentRecord) ||
    relatedNotFound ||
    (!relatedLoading && !relatedRecord);

  // ── 10. RENDER ─────────────────────────────────────────────────────────

  /* Loading spinner */
  if (isLoading) {
    return (
      <div
        className="flex min-h-48 items-center justify-center"
        role="status"
        aria-label="Loading related record details"
      >
        <div className="inline-block h-8 w-8 animate-spin rounded-full border-4 border-blue-600 border-r-transparent" />
        <span className="sr-only">Loading related record details&hellip;</span>
      </div>
    );
  }

  /* Page not found — ErpRequestContext.Page == null equivalent */
  if (pageError || !page) {
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

  /* Parent record not found — RecordsExists() first check failed */
  if (parentNotFound || (!parentLoading && !parentRecord)) {
    return (
      <div
        className="rounded-md bg-yellow-50 p-6 text-center"
        role="alert"
      >
        <h2 className="text-lg font-semibold text-yellow-800">
          Parent Record Not Found
        </h2>
        <p className="mt-2 text-sm text-yellow-700">
          The parent record for this relation does not exist or has been
          deleted.
        </p>
        <button
          type="button"
          className="mt-4 inline-flex items-center rounded-md bg-yellow-600 px-4 py-2 text-sm font-medium text-white transition-colors duration-150 hover:bg-yellow-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-yellow-600"
          onClick={() =>
            navigate(`/${appName}/${areaName}/${nodeName}/l/`)
          }
        >
          Back to List
        </button>
      </div>
    );
  }

  /* Related record not found — RecordsExists() second check failed */
  if (relatedNotFound || (!relatedLoading && !relatedRecord)) {
    return (
      <div
        className="rounded-md bg-yellow-50 p-6 text-center"
        role="alert"
      >
        <h2 className="text-lg font-semibold text-yellow-800">
          Related Record Not Found
        </h2>
        <p className="mt-2 text-sm text-yellow-700">
          The requested related record does not exist or has been deleted.
        </p>
        <button
          type="button"
          className="mt-4 inline-flex items-center rounded-md bg-yellow-600 px-4 py-2 text-sm font-medium text-white transition-colors duration-150 hover:bg-yellow-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-yellow-600"
          onClick={() =>
            navigate(
              `/${appName}/${areaName}/${nodeName}/r/${recordId}`,
            )
          }
        >
          Back to Parent Record
        </button>
      </div>
    );
  }

  return (
    <div
      className="record-related-details-page"
      data-record-id={relatedRecord?.id as string}
      data-parent-record-id={recordId}
      data-relation-id={relationId}
    >
      {/* ── Header with title + navigation action ────────── */}
      <header className="mb-6 flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold text-gray-900">
          {pageTitle}
        </h1>

        <div className="flex items-center gap-2">
          {/* Edit button — navigates to RecordRelatedRecordManage */}
          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-150 hover:bg-blue-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
            onClick={() => {
              clearValidation();
              navigate(
                `/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/m/${relatedRecordId}`,
              );
            }}
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

          {/* Back to parent — navigates to parent RecordDetails */}
          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-150 hover:bg-gray-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
            onClick={() =>
              navigate(
                `/${appName}/${areaName}/${nodeName}/r/${recordId}`,
              )
            }
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
                d="M10.5 19.5 3 12m0 0 7.5-7.5M3 12h18"
              />
            </svg>
            Back to Parent
          </button>
        </div>
      </header>

      {/* ── Validation message (general) ──────────────────── */}
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

      {/* ── Validation errors (field-specific) ────────────── */}
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

      {/* ── Page body + record field display ──────────────── */}
      {page.body && page.body.length > 0 ? (
        <section aria-label="Related record details">
          <PageBodyNodeList
            nodes={page.body}
            record={relatedRecord}
            entity={entity}
          />
          {relatedRecord && (
            <RecordFieldsGrid record={relatedRecord} entity={entity} />
          )}
        </section>
      ) : relatedRecord ? (
        <section aria-label="Related record details">
          <RecordFieldsGrid record={relatedRecord} entity={entity} />
        </section>
      ) : (
        <div
          className="rounded-md bg-blue-50 p-4 text-sm text-blue-700"
          role="status"
        >
          Page does not have page nodes attached
        </div>
      )}
    </div>
  );
}
