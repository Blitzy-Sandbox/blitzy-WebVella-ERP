/**
 * RecordManage Page Component
 *
 * React page component replacing WebVella.Erp.Web/Pages/RecordManage.cshtml(.cs)
 * (RecordManagePageModel). Handles editing/updating existing entity records with
 * a dynamic form pre-filled with current record data.
 *
 * Route: /:appName/:areaName/:nodeName/m/:recordId/:pageName?
 *
 * Lifecycle:
 *  1. Resolve page context from URL parameters (usePageByUrl)
 *  2. Canonical redirect if pageName differs from resolved page name
 *  3. Fetch entity metadata (useEntity) for field definitions
 *  4. Fetch existing record (useRecord) for form pre-fill — also serves
 *     as the RecordsExists() check from the monolith
 *  5. Render DynamicForm with record data and page body structure
 *  6. On submit → validate → update record → redirect to details view
 *
 * @module pages/records/RecordManage
 */

import React, { useState, useCallback, useEffect } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';

import { useRecord, useUpdateRecord } from '../../hooks/useRecords';
import { useEntity } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';
import type { ApiError } from '../../api/client';
import type { Entity } from '../../types/entity';
import type { EntityRecord } from '../../types/record';
import { PageType } from '../../types/page';
import type { ErpPage, PageBodyNode } from '../../types/page';
import type { ErrorModel, UrlInfo } from '../../types/common';
import { useAppStore } from '../../stores/appStore';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';

/* -------------------------------------------------------------------------- */
/* Type Guard Utilities                                                       */
/* -------------------------------------------------------------------------- */

/**
 * Type guard for ApiError — the Axios response interceptor in client.ts
 * rejects with plain objects matching the ApiError interface shape, so
 * `instanceof` cannot be used (ApiError is an interface, not a class).
 * This performs structural duck-type checking for the discriminating
 * properties that distinguish ApiError from a generic Error.
 */
function isApiError(err: unknown): err is ApiError {
  return (
    typeof err === 'object' &&
    err !== null &&
    'status' in err &&
    'errors' in err &&
    'timestamp' in err
  );
}

/* -------------------------------------------------------------------------- */
/* Route Parameter Types                                                      */
/* -------------------------------------------------------------------------- */

/**
 * Route parameter shape for the RecordManage page.
 *
 * Matches: /:appName/:areaName/:nodeName/m/:recordId/:pageName?
 */
interface RecordManageRouteParams {
  appName: string;
  areaName: string;
  nodeName: string;
  recordId: string;
  pageName?: string;
}

/* -------------------------------------------------------------------------- */
/* Helper: Recursive Page Body Node Renderer                                  */
/* -------------------------------------------------------------------------- */

/**
 * Renders a page body node and its children recursively.
 *
 * This structural renderer creates semantic container elements for each node
 * in the page body tree. Each node renders with `data-component-name` and
 * `data-node-id` attributes identifying the component type and unique ID,
 * enabling CSS targeting and component-specific rendering logic.
 */
function BodyNodeRenderer({
  node,
  record,
  fields,
}: {
  node: PageBodyNode;
  record: EntityRecord;
  fields: Entity['fields'];
}): React.JSX.Element {
  const childNodes = node.nodes ?? [];

  return (
    <div
      data-component-name={node.componentName}
      data-node-id={node.id}
      className="page-body-node mb-2"
    >
      {childNodes.length > 0 &&
        childNodes.map((child) => (
          <BodyNodeRenderer
            key={child.id}
            node={child}
            record={record}
            fields={fields}
          />
        ))}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Main Component: RecordManage                                               */
/* -------------------------------------------------------------------------- */

/**
 * Record manage/edit page component.
 *
 * Replaces the monolith's RecordManagePageModel which:
 *  - OnGet: resolves page context, checks record existence, applies
 *    canonical redirect, runs hooks, and pre-fills the form with record data
 *  - OnPost: converts form data, injects record ID, runs pre-manage hooks,
 *    validates, calls RecordManager.UpdateRecord(), runs post-manage hooks,
 *    and redirects to the record details view
 *
 * This component reproduces all of the above using:
 *  - usePageByUrl for page context resolution
 *  - useRecord for fetching the existing record + existence check
 *  - useEntity for entity metadata (field definitions)
 *  - useUpdateRecord for the update mutation
 *  - useAppStore for navigation context synchronisation
 *  - DynamicForm for form rendering and validation display
 */
export default function RecordManage(): React.JSX.Element {
  /* ---------------------------------------------------------------------- */
  /* 1. Route parameters & navigation                                       */
  /* ---------------------------------------------------------------------- */
  const params = useParams() as Partial<RecordManageRouteParams>;
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryClient = useQueryClient();

  /* ---------------------------------------------------------------------- */
  /* 2. Local state — form validation                                       */
  /* ---------------------------------------------------------------------- */
  const [validation, setValidation] = useState<FormValidation>({
    message: '',
    errors: [],
  });

  /* ---------------------------------------------------------------------- */
  /* 3. App store — navigation context synchronisation                      */
  /* ---------------------------------------------------------------------- */
  const updateNavigationContext = useAppStore(
    (state) => state.updateNavigationContext,
  );

  /* ---------------------------------------------------------------------- */
  /* 4. Construct UrlInfo for page context resolution                       */
  /* ---------------------------------------------------------------------- */
  const urlInfo: UrlInfo | undefined = params.appName
    ? {
        hasRelation: false,
        pageType: PageType.RecordManage,
        appName: params.appName,
        areaName: params.areaName ?? '',
        nodeName: params.nodeName ?? '',
        pageName: params.pageName ?? '',
        recordId: params.recordId ?? null,
        relationId: null,
        parentRecordId: null,
      }
    : undefined;

  /* ---------------------------------------------------------------------- */
  /* 5. Page context query — resolves ErpPage from URL parameters           */
  /*    usePageByUrl returns ApiResponse<ErpPage> (the full envelope).      */
  /*    Unwrap via .object to get the actual ErpPage.                       */
  /* ---------------------------------------------------------------------- */
  const { data: pageResponse, isLoading: pageLoading } = usePageByUrl(urlInfo);

  /**
   * Unwrapped ErpPage from the API response envelope.
   * `pageResponse` is `ApiResponse<ErpPage> | undefined`; the actual ErpPage
   * lives at `pageResponse.object`. All downstream logic operates on `erpPage`.
   */
  const erpPage: ErpPage | undefined = pageResponse?.object;

  /* ---------------------------------------------------------------------- */
  /* 6. Canonical redirect                                                  */
  /*    Matches monolith RecordManage.cshtml.cs lines 30-34:                */
  /*    if PageName != ErpRequestContext.Page.Name → redirect               */
  /*    Also covers the case where pageName is absent from URL.             */
  /* ---------------------------------------------------------------------- */
  useEffect(() => {
    if (erpPage && params.pageName !== erpPage.name) {
      const qs = searchParams.toString();
      const canonicalUrl = [
        '',
        params.appName,
        params.areaName,
        params.nodeName,
        'm',
        params.recordId,
        erpPage.name,
      ].join('/');
      navigate(qs ? `${canonicalUrl}?${qs}` : canonicalUrl, { replace: true });
    }
  }, [
    erpPage,
    params.appName,
    params.areaName,
    params.nodeName,
    params.recordId,
    params.pageName,
    searchParams,
    navigate,
  ]);

  /* ---------------------------------------------------------------------- */
  /* 7. Entity metadata query                                               */
  /*    Gated by erpPage.entityId availability (disabled while page loads)  */
  /* ---------------------------------------------------------------------- */
  const entityIdOrName = erpPage?.entityId ?? '';
  const { data: entity, isLoading: entityLoading } = useEntity(entityIdOrName);

  /* ---------------------------------------------------------------------- */
  /* 8. Record fetch — pre-fills form with current values                   */
  /*    Also serves as RecordsExists() check (isError → record not found)  */
  /*    Gated by entity.name availability (disabled while entity loads)    */
  /* ---------------------------------------------------------------------- */
  const entityName = entity?.name ?? '';
  const recordId = params.recordId ?? '';
  const {
    data: record,
    isLoading: recordLoading,
    isError: recordNotFound,
  } = useRecord(entityName, recordId);

  /* ---------------------------------------------------------------------- */
  /* 9. Sync navigation context when page resolves                          */
  /*    Replaces ErpRequestContext population in RecordManagePageModel.      */
  /*    updateNavigationContext expects typed App/SitemapArea/SitemapNode    */
  /*    objects — which we don't have from the page resolve endpoint.       */
  /*    We pass only the resolved ErpPage; app/area/node context is         */
  /*    handled by navigation components that resolve these from the URL.   */
  /* ---------------------------------------------------------------------- */
  useEffect(() => {
    if (erpPage) {
      updateNavigationContext({
        page: erpPage,
      });
    }
  }, [erpPage, updateNavigationContext]);

  /* ---------------------------------------------------------------------- */
  /* 10. Update mutation                                                    */
  /*     Replaces RecordManager().UpdateRecord() — includes optimistic      */
  /*     updates, cache snapshot/rollback, and automatic invalidation.      */
  /* ---------------------------------------------------------------------- */
  const updateMutation = useUpdateRecord();

  /* ---------------------------------------------------------------------- */
  /* 11. Form submission handler                                            */
  /*     Replaces RecordManage.cshtml.cs OnPost:                            */
  /*       - ConvertFormPostToEntityRecord (FormData extraction)            */
  /*       - ID injection (lines 79-80)                                    */
  /*       - ValidateRecordSubmission (server-side via API)                 */
  /*       - UpdateRecord (mutation)                                        */
  /*       - Success redirect to details view or ReturnUrl                 */
  /* ---------------------------------------------------------------------- */
  const handleSubmit = useCallback(
    (event: React.FormEvent<HTMLFormElement>) => {
      event.preventDefault();

      /* Clear previous validation state */
      setValidation({ message: '', errors: [] });

      /* Extract form data — replaces ConvertFormPostToEntityRecord */
      const formData = new FormData(event.currentTarget);
      const recordData: EntityRecord = {};

      formData.forEach((value, key) => {
        recordData[key] = typeof value === 'string' ? value : value;
      });

      /*
       * Ensure record ID is set — matches monolith lines 79-80:
       *   if (!PostObject.Properties.ContainsKey("id"))
       *     PostObject["id"] = RecordId.Value;
       */
      if (!recordData.id && recordId) {
        recordData.id = recordId;
      }

      updateMutation.mutate(
        {
          entityName,
          id: recordId,
          data: recordData,
        },
        {
          onSuccess: (updatedRecord) => {
            /* Invalidate page-related caches for freshness */
            void queryClient.invalidateQueries({ queryKey: ['pages'] });

            /* Determine redirect target */
            const returnUrl = searchParams.get('ReturnUrl');
            if (returnUrl) {
              /* ReturnUrl present — navigate there (monolith line 127) */
              navigate(returnUrl);
            } else {
              /*
               * Navigate to record details view — monolith lines 124-125:
               *   Redirect($"/{App}/{Area}/{Node}/r/{updatedRecordId}")
               */
              const updatedId = updatedRecord?.id ?? recordId;
              navigate(
                `/${params.appName}/${params.areaName}/${params.nodeName}/r/${updatedId}`,
              );
            }
          },
          onError: (error: Error) => {
            /*
             * Map API errors to validation state — matches monolith
             * lines 107-116 where updateRecordResponse.Errors are
             * iterated and displayed with error.Key / error.Message.
             *
             * The Axios interceptor rejects with a plain object matching
             * the ApiError interface (not a class instance), so we use
             * the isApiError structural type guard instead of instanceof.
             */
            if (isApiError(error)) {
              const apiErrors: ValidationError[] = (error.errors ?? []).map(
                (item: { key?: string; value?: string; message?: string }) => {
                  const errorModel = item as unknown as ErrorModel;
                  return {
                    propertyName: errorModel.key ?? '',
                    message: errorModel.message ?? String(item),
                  };
                },
              );
              setValidation({
                message: error.message,
                errors: apiErrors,
              });
            } else {
              setValidation({
                message:
                  error.message ||
                  'An unexpected error occurred while updating the record.',
                errors: [],
              });
            }
          },
        },
      );
    },
    [
      entityName,
      recordId,
      params.appName,
      params.areaName,
      params.nodeName,
      searchParams,
      navigate,
      updateMutation,
      queryClient,
    ],
  );

  /* ---------------------------------------------------------------------- */
  /* 12. Cancel handler — navigates back to record details or ReturnUrl     */
  /* ---------------------------------------------------------------------- */
  const handleCancel = useCallback(() => {
    const returnUrl = searchParams.get('ReturnUrl');
    if (returnUrl) {
      navigate(returnUrl);
    } else if (params.appName && params.areaName && params.nodeName && recordId) {
      navigate(
        `/${params.appName}/${params.areaName}/${params.nodeName}/r/${recordId}`,
      );
    } else {
      navigate(-1);
    }
  }, [
    params.appName,
    params.areaName,
    params.nodeName,
    recordId,
    searchParams,
    navigate,
  ]);

  /* ---------------------------------------------------------------------- */
  /* 13. Loading states                                                     */
  /* ---------------------------------------------------------------------- */

  /* Page context still loading */
  if (pageLoading) {
    return (
      <div
        className="flex items-center justify-center min-h-64"
        role="status"
        aria-live="polite"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading page…</span>
        </div>
      </div>
    );
  }

  /* Page not found — matches NotFound from monolith when page is null */
  if (!erpPage) {
    return (
      <div
        className="flex flex-col items-center justify-center min-h-64 text-center"
        role="alert"
      >
        <h1 className="text-2xl font-semibold text-gray-900 mb-2">
          Page Not Found
        </h1>
        <p className="text-gray-500 mb-4">
          The requested page could not be found.
        </p>
        <button
          type="button"
          onClick={() => navigate(-1)}
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          Go Back
        </button>
      </div>
    );
  }

  /* Entity or record still loading */
  if (entityLoading || (!entity && entityIdOrName) || recordLoading) {
    return (
      <div
        className="flex items-center justify-center min-h-64"
        role="status"
        aria-live="polite"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="h-8 w-8 animate-spin rounded-full border-4 border-gray-200 border-t-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading record…</span>
        </div>
      </div>
    );
  }

  /* Record not found — replaces RecordsExists() returning NotFound */
  if (recordNotFound) {
    return (
      <div
        className="flex flex-col items-center justify-center min-h-64 text-center"
        role="alert"
      >
        <h1 className="text-2xl font-semibold text-gray-900 mb-2">
          Record Not Found
        </h1>
        <p className="text-gray-500 mb-4">
          The record you are trying to edit does not exist or has been deleted.
        </p>
        <button
          type="button"
          onClick={() => navigate(-1)}
          className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600"
        >
          Go Back
        </button>
      </div>
    );
  }

  /* ---------------------------------------------------------------------- */
  /* 14. Derived values                                                     */
  /* ---------------------------------------------------------------------- */
  const pageTitle = erpPage.label || entity?.label || 'Manage Record';
  const hasBodyNodes = erpPage.body && erpPage.body.length > 0;
  const entityFields = entity?.fields ?? [];
  /*
   * entity.recordPermissions is available for client-side permission gating.
   * The API enforces record-level permissions server-side with 403 responses;
   * a client-side check here provides an early UX signal but is not the
   * authoritative gate.
   */
  const _recordPermissions = entity?.recordPermissions;

  /*
   * Merge local validation state with mutation error state.
   * The onError callback already syncs mutation errors into `validation`, but
   * `updateMutation.isError` / `updateMutation.error` provide the canonical
   * mutation error signal — used here as a fallback if local state was cleared
   * between renders.
   */
  const effectiveValidation: FormValidation =
    validation.message || validation.errors.length > 0
      ? validation
      : updateMutation.isError && updateMutation.error
        ? {
            message:
              updateMutation.error.message ||
              'An unexpected error occurred while updating the record.',
            errors: isApiError(updateMutation.error)
              ? (updateMutation.error.errors ?? []).map(
                  (item: { key?: string; value?: string; message?: string }) => {
                    const errorModel = item as unknown as ErrorModel;
                    return {
                      propertyName: errorModel.key ?? '',
                      message: errorModel.message ?? String(item),
                    };
                  },
                )
              : [],
          }
        : { message: '', errors: [] };

  /* ---------------------------------------------------------------------- */
  /* 15. Render                                                             */
  /* ---------------------------------------------------------------------- */
  return (
    <div className="record-manage-page">
      {/* Page heading */}
      <header className="mb-6">
        <h1 className="text-2xl font-semibold text-gray-900">{pageTitle}</h1>
      </header>

      {/* DynamicForm wraps the <form> element, provides FormContext and
          renders ValidationSummary above children */}
      <DynamicForm
        validation={effectiveValidation}
        onSubmit={handleSubmit}
      >
        {/* Hidden record ID — ensures ID is always submitted with form data */}
        <input
          type="hidden"
          name="id"
          value={record?.id ?? recordId}
        />

        {/* Page body content */}
        {hasBodyNodes ? (
          <div className="page-body">
            {erpPage.body.map((node: PageBodyNode) => (
              <BodyNodeRenderer
                key={node.id}
                node={node}
                record={record ?? ({} as EntityRecord)}
                fields={entityFields}
              />
            ))}
          </div>
        ) : (
          <div
            className="rounded-md bg-blue-50 p-4 text-sm text-blue-700"
            role="status"
          >
            <p>This page does not have page body nodes defined.</p>
          </div>
        )}

        {/* Form action buttons */}
        <div className="mt-6 flex items-center justify-end gap-3 border-t border-gray-200 pt-4">
          <button
            type="button"
            onClick={handleCancel}
            className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-500"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={updateMutation.isPending}
            className="inline-flex items-center rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:bg-blue-400"
          >
            {updateMutation.isPending ? (
              <>
                <span
                  className="me-2 inline-block h-4 w-4 animate-spin rounded-full border-2 border-white border-t-transparent"
                  aria-hidden="true"
                />
                Saving…
              </>
            ) : (
              'Save'
            )}
          </button>
        </div>
      </DynamicForm>
    </div>
  );
}
