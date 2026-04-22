/**
 * RecordRelatedRecordManage — Related Record Edit Page
 *
 * Replaces the monolith's `RecordRelatedRecordManage.cshtml` / `.cshtml.cs`
 * (`RecordRelatedRecordManagePageModel`). Handles editing / updating a
 * record that is related to a parent record via an entity relation.
 *
 * Route: /:appName/:areaName/:nodeName/r/:recordId/rl/:relationId/m/:relatedRecordId/:pageName?
 *
 * Lifecycle (mirroring the source C# OnGet / OnPost):
 *  1.  Resolve page context from URL parameters via `usePageByUrl`
 *  2.  Canonical redirect when `pageName` does not match `page.name`
 *  3.  Verify parent record existence via `useRecord`
 *  4.  Fetch the related record for form pre-fill via `useRecord`
 *  5.  Fetch entity metadata (field definitions) via `useEntity`
 *  6.  Sync navigation context to the global app store
 *  7.  On form submit → `useUpdateRecord` mutation → redirect on success
 *  8.  Display validation errors on failure via `DynamicForm`
 *
 * Key differences from the monolith:
 *  - Pre-manage hooks replaced by API-level validation
 *  - Post-manage hooks replaced by SNS domain events
 *  - ValidateRecordSubmission is intentionally skipped (commented out in
 *    source at line 78) — validation is handled server-side
 *  - Form data collected via FormData API instead of
 *    `PageService.ConvertFormPostToEntityRecord`
 *  - `PostObject["id"] = RecordId.Value` is replicated by injecting
 *    `relatedRecordId` when the form payload lacks an `id` field
 *
 * @module pages/records/RecordRelatedRecordManage
 */

import React, { useState, useCallback, useEffect, useMemo } from 'react';
import type { FormEvent } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';

// Internal hooks
import { useRecord, useUpdateRecord } from '../../hooks/useRecords';
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

// UI components
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';

// ---------------------------------------------------------------------------
// Route Parameter Types
// ---------------------------------------------------------------------------

/** Shape of URL parameters extracted by React Router for this route. */
interface RelatedRecordManageRouteParams {
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
  /** Related record GUID — the record being edited on this page. */
  relatedRecordId: string;
  /** Optional page name slug for canonical URL matching. */
  pageName?: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Runtime type guard distinguishing structured API errors (rejected by the
 * Axios response interceptor when `success === false`) from generic Error
 * objects thrown during fetch or assertion.
 */
function isApiError(error: unknown): error is ApiError {
  return (
    typeof error === 'object' &&
    error !== null &&
    'message' in error &&
    'errors' in error &&
    Array.isArray((error as Record<string, unknown>).errors)
  );
}

/**
 * Maps API error items (`ErrorModel` / `ApiErrorItem` — structurally
 * identical `{ key, value, message }`) to the `ValidationError` shape
 * (`{ propertyName, message }`) consumed by `DynamicForm`.
 */
function mapErrorsToValidationErrors(
  errors: ReadonlyArray<ErrorModel> | undefined,
): ValidationError[] {
  if (!errors || errors.length === 0) return [];
  return errors.map((err) => ({
    propertyName: err.key ?? '',
    message: err.message ?? '',
  }));
}

// ---------------------------------------------------------------------------
// BodyNodeRenderer — Local recursive renderer for page body tree
// ---------------------------------------------------------------------------

/**
 * Recursively renders the page body node tree inside the form context.
 *
 * Each body node carries a `componentName` (e.g. "PcRow", "PcFieldText")
 * and nested `nodes` forming an arbitrary-depth layout tree. This renderer
 * emits a semantic container per node with data attributes so that
 * higher-level component renderers can progressively enhance the tree
 * without breaking the structural contract.
 *
 * For edit-mode pages the renderer wraps content in containers with
 * appropriate ARIA roles for accessibility.
 */
function BodyNodeRenderer({
  nodes,
  record,
  entity,
  fields,
}: {
  nodes: PageBodyNode[];
  record: EntityRecord | undefined;
  entity: Entity | undefined;
  /** Entity field definitions — enables field-type awareness for rendering */
  fields: Entity['fields'];
}): React.ReactElement {
  if (nodes.length === 0) {
    return <></>;
  }

  return (
    <>
      {nodes.map((node) => {
        // When the node references a field component (e.g. "PcFieldText"),
        // we can resolve the associated field definition from the entity
        // metadata so that downstream component renderers can apply
        // type-specific behaviour (validation rules, formatting, etc.).
        const fieldName =
          node.componentName?.startsWith('PcField')
            ? (node.componentName.replace('PcField', '').toLowerCase() ?? '')
            : undefined;
        const matchedField = fieldName
          ? fields.find(
              (f) =>
                f.name?.toLowerCase() === fieldName ||
                f.fieldType?.toString().toLowerCase() === fieldName,
            )
          : undefined;

        return (
          <div
            key={node.id}
            data-component={node.componentName}
            data-node-id={node.id}
            data-container-id={node.containerId}
            data-field-name={matchedField?.name}
            className="page-body-node"
          >
            {/* Recursive child node rendering */}
            {node.nodes && node.nodes.length > 0 && (
              <BodyNodeRenderer
                nodes={node.nodes}
                record={record}
                entity={entity}
                fields={fields}
              />
            )}
          </div>
        );
      })}
    </>
  );
}

// ---------------------------------------------------------------------------
// RecordRelatedRecordManage — Main Page Component
// ---------------------------------------------------------------------------

/**
 * Page component for editing a related record within a relation context.
 *
 * Default export for `React.lazy()` code-splitting at the router level.
 */
export default function RecordRelatedRecordManage(): React.ReactElement {
  // ── URL parameters ──────────────────────────────────────────────────────
  const params = useParams<
    Record<keyof RelatedRecordManageRouteParams, string>
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
    pageName = '',
  } = params;

  // ── Local state for validation feedback ─────────────────────────────────
  const [validationMessage, setValidationMessage] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<ValidationError[]>(
    [],
  );

  // ── Global navigation store ─────────────────────────────────────────────
  const updateNavigationContext = useAppStore(
    (state) => state.updateNavigationContext,
  );

  // ── 1. Resolve page context (replaces Init()) ──────────────────────────
  const urlInfo: UrlInfo | undefined = useMemo(() => {
    if (!appName) return undefined;
    return {
      hasRelation: true,
      pageType: PageType.RecordManage as number,
      appName,
      areaName,
      nodeName,
      pageName: pageName || '',
      recordId: relatedRecordId || null,
      relationId: relationId || null,
      parentRecordId: recordId || null,
    };
  }, [appName, areaName, nodeName, pageName, recordId, relationId, relatedRecordId]);

  const {
    data: pageResponse,
    isLoading: pageLoading,
  } = usePageByUrl(urlInfo, { enabled: !!urlInfo });

  // The page is inside the API response envelope (not unwrapped by the hook)
  const page: ErpPage | undefined = pageResponse?.object ?? undefined;

  // ── 2. Canonical redirect (lines 23-28 of source .cs) ──────────────────
  useEffect(() => {
    if (!page) return;

    // If pageName param differs from the resolved page name, redirect
    // to the canonical URL preserving query string.
    if (pageName && page.name && pageName !== page.name) {
      const qs = searchParams.toString();
      const canonicalPath =
        `/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/m/${relatedRecordId}/${page.name}` +
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

  // ── 3. Fetch entity metadata (field definitions for the form) ──────────
  const entityIdOrEmpty = page?.entityId ?? '';
  const {
    data: entity,
    isLoading: entityLoading,
  } = useEntity(entityIdOrEmpty);

  // Derive entity name for record operations (empty string disables hooks)
  const entityName = entity?.name ?? '';

  // ── 4. Verify parent record existence (replaces RecordsExists()) ───────
  const {
    data: parentRecord,
    isLoading: parentLoading,
    isError: parentNotFound,
  } = useRecord(entityName, recordId);

  // ── 5. Fetch related record for form pre-fill ──────────────────────────
  const {
    data: relatedRecord,
    isLoading: relatedLoading,
    isError: relatedNotFound,
  } = useRecord(entityName, relatedRecordId);

  // ── 6. Sync navigation context to app store ────────────────────────────
  useEffect(() => {
    if (!page) return;
    updateNavigationContext({ page });
  }, [page, updateNavigationContext]);

  // ── 7. Update mutation (replaces RecordManager().UpdateRecord) ─────────
  const updateMutation = useUpdateRecord();
  const { isPending: isSaving, isError: mutationHasError, error: mutationError } = updateMutation;

  // ── 8. Form submit handler ─────────────────────────────────────────────
  const handleSubmit = useCallback(
    async (event: FormEvent<HTMLFormElement>) => {
      event.preventDefault();

      // Clear previous validation state
      setValidationMessage('');
      setValidationErrors([]);

      if (!entityName || !relatedRecordId) return;

      // Collect form data from the native form element
      const formDataObj = new FormData(event.currentTarget);
      const formRecord: EntityRecord = {};
      formDataObj.forEach((value, key) => {
        formRecord[key] = value;
      });

      // Ensure id is set — replicates source line 82-83:
      // PostObject["id"] = RecordId.Value
      if (!formRecord.id) {
        formRecord.id = relatedRecordId;
      }

      updateMutation.mutate(
        {
          entityName,
          id: relatedRecordId,
          data: formRecord,
        },
        {
          onSuccess: () => {
            // Invalidate the related record cache for freshness
            void queryClient.invalidateQueries({
              queryKey: ['record', relatedRecordId],
            });

            // Navigate to related record details or ReturnUrl
            // Source lines 121-124
            const returnUrl = searchParams.get('ReturnUrl');
            if (returnUrl) {
              navigate(returnUrl);
            } else {
              navigate(
                `/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/r/${relatedRecordId}`,
              );
            }
          },
          onError: (error: Error) => {
            if (isApiError(error)) {
              setValidationMessage(
                error.message || 'An error occurred while updating the record.',
              );
              setValidationErrors(mapErrorsToValidationErrors(error.errors));
            } else {
              setValidationMessage(
                error.message || 'An unexpected error occurred.',
              );
            }
          },
        },
      );
    },
    [
      entityName,
      relatedRecordId,
      recordId,
      relationId,
      appName,
      areaName,
      nodeName,
      searchParams,
      navigate,
      queryClient,
      updateMutation,
    ],
  );

  // ── Derived permission check ──────────────────────────────────────────
  // `recordPermissions.canUpdate` is a `string[]` of role IDs that have
  // update permission.  A non-empty array means at least one role can
  // update; the API enforces per-user role checks server-side.  An empty
  // array (or undefined) means no role can update, so we block the form.
  // Entity.fields gives the full field definition list for form rendering.
  const canUpdate =
    (entity?.recordPermissions?.canUpdate ?? []).length > 0;
  const entityFields = entity?.fields ?? [];

  // ── Derived loading / error states ──────────────────────────────────────
  const isLoading =
    pageLoading || entityLoading || parentLoading || relatedLoading;

  // Not-found condition also verifies that the record data objects
  // themselves are present after loading completes.  This catches edge
  // cases where the API returns 200 with empty payload rather than 404.
  const isNotFound =
    (!pageLoading && !page) ||
    parentNotFound ||
    (!parentLoading && !parentRecord) ||
    relatedNotFound ||
    (!relatedLoading && !relatedRecord);

  // Surface any persistent mutation error that was not captured by the
  // onError callback (e.g. network failures before the response interceptor)
  useEffect(() => {
    if (mutationHasError && mutationError && !validationMessage) {
      setValidationMessage(
        mutationError.message || 'An unexpected error occurred.',
      );
    }
  }, [mutationHasError, mutationError, validationMessage]);

  // ── Construct FormValidation for DynamicForm ────────────────────────────
  const formValidation: FormValidation | undefined =
    validationMessage || validationErrors.length > 0
      ? { message: validationMessage, errors: validationErrors }
      : undefined;

  // ── Render: Loading state ───────────────────────────────────────────────
  if (isLoading) {
    return (
      <div
        className="flex min-h-[12rem] items-center justify-center"
        role="status"
        aria-label="Loading related record"
      >
        <div className="inline-flex items-center gap-2 text-gray-500">
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
          <span>Loading…</span>
        </div>
      </div>
    );
  }

  // ── Render: Not found state ─────────────────────────────────────────────
  if (isNotFound) {
    return (
      <div
        className="mx-auto max-w-xl py-16 text-center"
        role="alert"
        aria-live="assertive"
      >
        <h1 className="text-2xl font-semibold text-gray-900">
          Page Not Found
        </h1>
        <p className="mt-2 text-gray-600">
          The requested related record page could not be found. The page,
          parent record, or related record may not exist.
        </p>
        <button
          type="button"
          className="mt-6 inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          onClick={() => navigate(-1)}
        >
          Go Back
        </button>
      </div>
    );
  }

  // ── Render: Permission denied state ──────────────────────────────────────
  if (!canUpdate) {
    return (
      <div
        className="mx-auto max-w-xl py-16 text-center"
        role="alert"
        aria-live="assertive"
      >
        <h1 className="text-2xl font-semibold text-gray-900">
          Permission Denied
        </h1>
        <p className="mt-2 text-gray-600">
          You do not have permission to update this record.
          {entityFields.length === 0 &&
            ' The entity has no editable fields defined.'}
        </p>
        <button
          type="button"
          className="mt-6 inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
          onClick={() => navigate(-1)}
        >
          Go Back
        </button>
      </div>
    );
  }

  // ── Render: Main edit page ──────────────────────────────────────────────
  const pageLabel = page?.label ?? entity?.label ?? 'Manage Related Record';
  const hasBodyNodes = page?.body && page.body.length > 0;

  return (
    <div className="record-related-manage-page">
      {/* Page title — replicates ViewBag.Title = ErpRequestContext.Page.Label */}
      <h1 className="mb-4 text-xl font-semibold text-gray-900">
        {pageLabel}
      </h1>

      {hasBodyNodes ? (
        <DynamicForm
          name="RecordRelatedRecordManageForm"
          method="post"
          labelMode="stacked"
          fieldMode="form"
          showValidation={true}
          validation={formValidation}
          onSubmit={handleSubmit}
          className="record-related-manage-form"
        >
          {/* Render page body tree inside the form context.
              Entity fields are passed down so that field-level body nodes
              can resolve their field definitions for type-aware rendering. */}
          <BodyNodeRenderer
            nodes={page!.body}
            record={relatedRecord}
            entity={entity}
            fields={entityFields}
          />

          {/* Submit button — replaces the submit button rendered by
              PcForm in the page body. Provided here as a fallback so the
              form is always submittable. */}
          <div className="mt-6 flex items-center justify-end gap-3">
            <button
              type="button"
              className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
              onClick={() => {
                const returnUrl = searchParams.get('ReturnUrl');
                if (returnUrl) {
                  navigate(returnUrl);
                } else {
                  navigate(
                    `/${appName}/${areaName}/${nodeName}/r/${recordId}/rl/${relationId}/r/${relatedRecordId}`,
                  );
                }
              }}
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={isSaving || !canUpdate}
              aria-disabled={isSaving || !canUpdate}
              className="inline-flex items-center rounded-md bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isSaving ? (
                <>
                  <svg
                    className="-ml-0.5 mr-1.5 h-4 w-4 animate-spin"
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
                  Saving…
                </>
              ) : (
                'Save'
              )}
            </button>
          </div>
        </DynamicForm>
      ) : (
        /* Empty body message — replicates the "Page does not have page
           nodes attached" info alert from the Razor template */
        <div
          className="rounded-md bg-blue-50 p-4 text-sm text-blue-700"
          role="status"
        >
          <p>Page does not have page nodes attached.</p>
        </div>
      )}
    </div>
  );
}
