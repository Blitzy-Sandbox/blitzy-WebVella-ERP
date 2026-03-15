/**
 * RecordCreate Page Component
 *
 * React page component replacing `WebVella.Erp.Web/Pages/RecordCreate.cshtml[.cs]`
 * (`RecordCreatePageModel`). Handles creating new entity records with dynamic form
 * rendering based on entity field definitions.
 *
 * Route: /:appName/:areaName/:nodeName/c/:pageName?
 *
 * Lifecycle (mirrors monolith OnGet + OnPost):
 *  1. Resolve page context from URL params via usePageByUrl (replaces Init())
 *  2. Canonical redirect if pageName param doesn't match resolved page name
 *  3. Fetch entity metadata for field definitions via useEntity
 *  4. Sync navigation context to global store for layout chrome
 *  5. Render dynamic page body with DynamicForm for record creation
 *  6. On submit: generate UUID, call createRecord mutation, handle success/errors
 *  7. On success: navigate to record details or ReturnUrl
 *  8. On error: display validation message and field-specific errors
 *
 * Source files:
 *   - WebVella.Erp.Web/Pages/RecordCreate.cshtml (38 lines — Razor view)
 *   - WebVella.Erp.Web/Pages/RecordCreate.cshtml.cs (151 lines — PageModel)
 */

import { useState, useCallback, useEffect } from 'react';
import { useParams, useNavigate, useSearchParams, useLocation } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';

import { useCreateRecord } from '../../hooks/useRecords';
import { useEntity } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';
import type { ApiError } from '../../api/client';
import type { Entity } from '../../types/entity';
import type { EntityRecord } from '../../types/record';
import type { ErpPage, PageBodyNode } from '../../types/page';
import { PageType } from '../../types/page';
import type { ErrorModel } from '../../types/common';
import type { UrlInfo } from '../../types/common';
import { useAppStore } from '../../stores/appStore';
import DynamicForm from '../../components/forms/DynamicForm';
import type { FormValidation, ValidationError } from '../../components/forms/DynamicForm';

// ---------------------------------------------------------------------------
// Route Parameter Type
// ---------------------------------------------------------------------------

/**
 * Route parameters extracted by React Router from
 * /:appName/:areaName/:nodeName/c/:pageName?
 */
interface RecordCreateRouteParams {
  appName: string;
  areaName: string;
  nodeName: string;
  pageName?: string;
}

// ---------------------------------------------------------------------------
// RecordCreate Component
// ---------------------------------------------------------------------------

/**
 * Record creation page component.
 *
 * Replaces RecordCreatePageModel from the ASP.NET monolith. Resolves page
 * context from URL parameters, fetches entity metadata, renders a dynamic
 * form based on the page body node tree, and handles the record creation
 * mutation with full validation error display.
 *
 * Default exported for React.lazy() code-splitting.
 */
export default function RecordCreate(): React.JSX.Element {
  // -- Route parameters & navigation ------------------------------------------

  const params = useParams<Record<string, string>>();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const location = useLocation();

  /*
   * Entity metadata passed via React Router route state from the previous
   * page (e.g., RecordList passes entity in `<Link state={{ entity }}>`).
   * This makes entity data available on the **first synchronous render**
   * without waiting for TanStack Query to resolve from cache, eliminating
   * the ~50 ms gap where entity-dependent form inputs are absent from the DOM.
   */
  const entityFromRouteState = (location.state as { entity?: Entity } | null)?.entity ?? undefined;

  // Safely extract typed route params (React Router 7 returns string | undefined)
  const appName = params.appName ?? '';
  const areaName = params.areaName ?? '';
  const nodeName = params.nodeName ?? '';
  const pageName = params.pageName ?? '';
  /** Standalone entity name for /records/:entityName/create route. */
  const standaloneEntityName = params.entityName ?? '';
  /** Whether the component is rendered from a standalone route */
  const isStandalone = !!(standaloneEntityName && !appName && !areaName && !nodeName);

  // -- Local validation state -------------------------------------------------
  // Mirrors monolith's Validation.Message + Validation.Errors pattern
  // from RecordCreate.cshtml.cs lines 104-114

  const [validationMessage, setValidationMessage] = useState<string>('');
  const [validationErrors, setValidationErrors] = useState<ValidationError[]>(
    [],
  );

  // -- Global navigation store ------------------------------------------------

  const updateNavigationContext = useAppStore(
    (state) => state.updateNavigationContext,
  );

  // -- 1. Resolve page context (replaces Init() → ErpRequestContext) ----------
  // Builds UrlInfo matching the common.ts UrlInfo interface for the
  // usePageByUrl hook which calls POST /pages/resolve

  const urlInfo: UrlInfo | undefined =
    appName && areaName && nodeName
      ? {
          hasRelation: false,
          pageType: PageType.RecordCreate as number,
          appName,
          areaName,
          nodeName,
          pageName,
          recordId: null,
          relationId: null,
          parentRecordId: null,
        }
      : undefined;

  const {
    data: pageResponse,
    isLoading: isPageLoading,
  } = usePageByUrl(urlInfo, { enabled: !!urlInfo });

  // Extract the ErpPage from the API response envelope
  const page: ErpPage | undefined = pageResponse?.object ?? undefined;

  // -- 2. Canonical redirect --------------------------------------------------
  // Mirrors RecordCreate.cshtml.cs lines 29-33:
  // if (PageName != ErpRequestContext.Page.Name)
  //   return Redirect($"/{App}/{Area}/{Node}/c/{Page.Name}{queryString}");

  useEffect(() => {
    if (page && pageName && pageName !== page.name) {
      const queryString = searchParams.toString();
      const canonicalPath = `/${appName}/${areaName}/${nodeName}/c/${page.name}`;
      navigate(
        queryString ? `${canonicalPath}?${queryString}` : canonicalPath,
        { replace: true },
      );
    }
  }, [page, pageName, appName, areaName, nodeName, searchParams, navigate]);

  // -- 3. Fetch entity metadata -----------------------------------------------
  // Uses the entityId from the resolved page to determine entity name for
  // field definitions. The useEntity hook accepts idOrName.
  // Replaces ErpRequestContext.Entity metadata loading from BeforeRender().

  const entityIdOrName = page?.entityId ?? standaloneEntityName ?? '';
  const queryClient = useQueryClient();

  const {
    data: entity,
    isLoading: isEntityLoading,
  } = useEntity(entityIdOrName);

  /*
   * Synchronous TanStack Query cache read — resolves the entity data on the
   * **very first** synchronous render cycle if it was already fetched by a
   * prior page (e.g., RecordList). Without this, `useEntity` returns
   * `undefined` for at least one render tick while TanStack Query resolves
   * from its internal cache. The ~100 ms gap causes a race condition where
   * Playwright's `textInputs.count()` returns 0 because entity-dependent
   * form inputs have not rendered yet.
   */
  const cachedEntity = entityIdOrName
    ? queryClient.getQueryData<Entity>(['entities', entityIdOrName])
    : undefined;
  /** Effective entity — prefers the reactive `useEntity` result, falls back
   *  to route state (from <Link state={{ entity }}>) then synchronous cache
   *  read for instant first-render availability. */
  const effectiveEntity = entity ?? entityFromRouteState ?? cachedEntity ?? undefined;

  // -- 4. Sync navigation context ---------------------------------------------
  // Replaces BaseErpPageModel.Init() which populated ErpRequestContext
  // used by _AppMaster.cshtml layout components (Sidebar, TopNav, Breadcrumb)

  useEffect(() => {
    if (page) {
      updateNavigationContext({
        page,
      });
    }
  }, [page, updateNavigationContext]);

  // -- 5. Permission check ------------------------------------------------------
  // Verifies that the entity allows record creation based on recordPermissions.
  // In the monolith this was handled by SecurityContext checks in RecordManager.
  // Here we check Entity.recordPermissions.canCreate for informational display.

  const canCreate =
    !effectiveEntity ||
    !effectiveEntity.recordPermissions ||
    effectiveEntity.recordPermissions.canCreate.length > 0;

  // -- 6. Create record mutation ----------------------------------------------
  // Replaces RecordManager().CreateRecord(Entity.MapTo<Entity>(), PostObject)
  // from RecordCreate.cshtml.cs OnPost line 102.
  // useCreateRecord wraps POST /v1/entities/{entityName}/records

  const {
    mutate: createRecord,
    isPending: isCreating,
    isError: isCreateError,
    error: createError,
  } = useCreateRecord();

  // -- 7. Form submission handler ---------------------------------------------
  // Replaces the OnPost pipeline:
  //   a. ConvertFormPostToEntityRecord (line 64)
  //   b. ID generation via Guid.NewGuid() (lines 75-76) → crypto.randomUUID()
  //   c. Pre-create hooks (lines 78-92) → server-side API validation
  //   d. ValidateRecordSubmission (lines 94-100) → server-side + client-side
  //   e. RecordManager().CreateRecord (line 102)
  //   f. Error handling (lines 104-114)
  //   g. Post-create hooks (lines 115-119) → server-side SNS events
  //   h. Success redirect (lines 121-124)

  const handleSubmit = useCallback(
    (formData: EntityRecord) => {
      // Clear previous validation state
      setValidationMessage('');
      setValidationErrors([]);

      // Guard: entity must be resolved before submission
      if (!effectiveEntity) {
        setValidationMessage(
          'Entity metadata is not yet loaded. Please wait and try again.',
        );
        return;
      }

      // Client-side required field validation
      // Replaces monolith's ValidateRecordSubmission() from RecordCreate.cshtml.cs
      // Only validate when the form actually contains field inputs (check that at
      // least one entity field key is present in formData). This avoids a race
      // condition where the form submits before async entity data finishes loading
      // and the field inputs have not yet been rendered into the DOM.
      const formHasFieldInputs = effectiveEntity.fields
        ? effectiveEntity.fields.some(
            (f) =>
              f.name !== 'id' &&
              f.name !== 'created_on' &&
              f.name !== 'created_by' &&
              formData[f.name] !== undefined,
          )
        : false;

      if (formHasFieldInputs && effectiveEntity.fields) {
        const requiredErrors: ValidationError[] = [];
        for (const field of effectiveEntity.fields) {
          if (field.required && field.name !== 'id' && field.name !== 'created_on' && field.name !== 'created_by' && field.name !== 'last_modified_by' && field.name !== 'last_modified_on') {
            const value = formData[field.name];
            if (value === undefined || value === null || value === '') {
              requiredErrors.push({
                propertyName: field.name,
                message: `${field.label || field.name} is required`,
              });
            }
          }
        }
        if (requiredErrors.length > 0) {
          setValidationMessage('Please fill in all required fields.');
          setValidationErrors(requiredErrors);
          return;
        }
      }

      // Generate record ID if not present in form data
      // Matches RecordCreate.cshtml.cs lines 75-76:
      // if (!PostObject.Properties.ContainsKey("id"))
      //   PostObject["id"] = Guid.NewGuid();
      const recordData: EntityRecord = { ...formData };
      if (!recordData.id) {
        recordData.id = crypto.randomUUID();
      }

      const newRecordId = recordData.id as string;

      createRecord(
        {
          entityName: effectiveEntity.id ?? effectiveEntity.name,
          data: recordData,
        },
        {
          onSuccess: () => {
            // Matches RecordCreate.cshtml.cs lines 121-124:
            // if (ReturnUrl != null) return Redirect(ReturnUrl);
            // else return Redirect($"/{App}/{Area}/{Node}/r/{recordId}");
            const returnUrl = searchParams.get('ReturnUrl');
            if (returnUrl) {
              navigate(returnUrl);
            } else if (isStandalone) {
              navigate(`/records/${standaloneEntityName}/${newRecordId}`);
            } else {
              navigate(
                `/${appName}/${areaName}/${nodeName}/r/${newRecordId}`,
              );
            }
          },
          onError: (error: Error) => {
            // Extract structured error info from ApiError
            // Matches RecordCreate.cshtml.cs lines 104-114:
            // Validation.Message = createRecordResponse.Message;
            // Validation.Errors.AddRange(createRecordResponse.Errors.Select(...))
            const apiError = error as unknown as ApiError;

            setValidationMessage(
              apiError.message || 'An error occurred while creating the record.',
            );

            if (apiError.errors && Array.isArray(apiError.errors)) {
              setValidationErrors(
                apiError.errors.map(
                  (err: ErrorModel): ValidationError => ({
                    propertyName: err.key,
                    message: err.message,
                  }),
                ),
              );
            }
          },
        },
      );
    },
    [
      effectiveEntity,
      createRecord,
      searchParams,
      navigate,
      appName,
      areaName,
      nodeName,
    ],
  );

  // -- 8. Form event handler --------------------------------------------------
  // Bridges DynamicForm's native form event to our data-driven handleSubmit.
  // In the SPA, form data is managed by controlled field components, so we
  // collect values from the entity fields and pass them to handleSubmit.

  const handleFormSubmit = useCallback(
    (event: React.FormEvent<HTMLFormElement>) => {
      event.preventDefault();

      const formElement = event.currentTarget;

      // Collect form data from the native FormData API
      // This works because DynamicForm renders a <form> element and
      // field components use standard name attributes
      const nativeFormData = new FormData(formElement);
      const record: EntityRecord = {};

      nativeFormData.forEach((value, key) => {
        // Handle multi-value fields (e.g. multiselect checkboxes)
        const existing = record[key];
        if (existing !== undefined) {
          if (Array.isArray(existing)) {
            (existing as unknown[]).push(value);
          } else {
            record[key] = [existing, value];
          }
        } else {
          record[key] = value;
        }
      });

      handleSubmit(record);
    },
    [handleSubmit],
  );

  // -- 9. Build form validation object for DynamicForm ------------------------
  // Combines local validation state with reactive mutation error state.
  // Uses isCreateError and createError from the mutation for fallback display.

  const effectiveMessage =
    validationMessage ||
    (isCreateError && createError
      ? (createError as unknown as ApiError).message ||
        'An error occurred while creating the record.'
      : '');

  const formValidation: FormValidation | undefined =
    effectiveMessage || validationErrors.length > 0
      ? {
          message: effectiveMessage || undefined,
          errors: validationErrors,
        }
      : undefined;

  // -- 10. Render states ------------------------------------------------------

  // Loading state: waiting for page context or entity metadata
  if (isPageLoading && !isStandalone) {
    return (
      <div
        className="flex min-h-[200px] items-center justify-center"
        role="status"
        aria-label="Loading page"
      >
        <div className="flex flex-col items-center gap-3">
          <div
            className="inline-block size-8 animate-spin rounded-full border-4 border-current border-e-transparent text-blue-600"
            aria-hidden="true"
          />
          <span className="text-sm text-gray-500">Loading page…</span>
        </div>
      </div>
    );
  }

  // Not found state: page context could not be resolved
  // Matches RecordCreate.cshtml.cs: if (ErpRequestContext.Page == null) return NotFound();
  // For standalone /records/:entityName/create routes, page is null — that's expected.
  if (!page && !isStandalone) {
    return (
      <div
        className="flex min-h-[200px] flex-col items-center justify-center gap-2 text-gray-500"
        role="alert"
      >
        <svg
          className="size-12 text-gray-300"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          aria-hidden="true"
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            strokeWidth={1.5}
            d="M9.75 9.75l4.5 4.5m0-4.5l-4.5 4.5M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
          />
        </svg>
        <h2 className="text-lg font-semibold text-gray-700">Page Not Found</h2>
        <p className="text-sm">
          The requested record creation page could not be found.
        </p>
      </div>
    );
  }

  // Entity loading state — show skeleton only when entity is truly unavailable
  // (neither from the async hook nor from the synchronous cache read)
  const isLoading = isEntityLoading && !effectiveEntity;

  // -- 11. Page title ---------------------------------------------------------
  // Matches RecordCreate.cshtml: ViewData["Title"] = Model.ErpRequestContext.Page.Label
  const pageTitle = page?.label || effectiveEntity?.label || 'Create Record';

  // -- 12. Render page --------------------------------------------------------

  return (
    <div className="record-create-page">
      {/* Page title (replaces ViewData["Title"]) */}
      <h1 className="mb-4 text-2xl font-semibold text-gray-900">
        {pageTitle}
      </h1>

      {/* Permission warning — shows when entity has no create permissions */}
      {!canCreate && (
        <div
          className="mb-4 rounded-md border border-yellow-200 bg-yellow-50 p-4 text-sm text-yellow-800"
          role="alert"
        >
          <strong>Warning:</strong> No roles have permission to create records
          for this entity. The form may not submit successfully.
        </div>
      )}

      {/* Entity loading indicator */}
      {isLoading && (
        <div
          className="mb-4 flex items-center gap-2 text-sm text-gray-500"
          role="status"
          aria-label="Loading entity metadata"
        >
          <div
            className="inline-block size-4 animate-spin rounded-full border-2 border-current border-e-transparent"
            aria-hidden="true"
          />
          <span>Loading form fields…</span>
        </div>
      )}

      {/* Dynamic page body rendering — replaces Razor ViewComponent loop */}
      {/* RecordCreate.cshtml iterates currentPage.Body and invokes each
          root-level ViewComponent with ComponentMode.Display. */}
      {!isLoading && ((page?.body && page.body.length > 0) || isStandalone) ? (
        <DynamicForm
          name="RecordCreateForm"
          method="post"
          labelMode="stacked"
          fieldMode="form"
          showValidation={true}
          validation={formValidation}
          onSubmit={handleFormSubmit}
          disableNativeValidation={!isStandalone}
        >
          {/* Render page body nodes */}
          {page?.body && page.body.length > 0 ? (
            <PageBodyRenderer
              nodes={page.body}
              entity={effectiveEntity}
              mode="create"
              isSubmitting={isCreating}
            />
          ) : effectiveEntity?.fields ? (
            /* Standalone mode: render entity fields directly as form inputs */
            <div className="space-y-4" data-testid="record-form">
              {effectiveEntity.fields
                .filter((f) => f.name !== 'id' && f.name !== 'created_on' && f.name !== 'created_by' && f.name !== 'last_modified_by' && f.name !== 'last_modified_on')
                .map((field) => (
                  <div key={field.id ?? field.name} className="field-group">
                    <label
                      htmlFor={`field-${field.name}`}
                      className="block text-sm font-medium text-gray-700 mb-1"
                    >
                      {field.label || field.name}
                      {field.required && <span className="text-red-500 ml-1">*</span>}
                    </label>
                    <input
                      id={`field-${field.name}`}
                      name={field.name}
                      type="text"
                      required={field.required}
                      placeholder={field.placeholderText || ''}
                      className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      disabled={isCreating}
                    />
                  </div>
                ))}
            </div>
          ) : null}

          {/* Submit button area */}
          <div className="mt-6 flex items-center gap-3">
            <button
              type="submit"
              disabled={isCreating}
              className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors duration-150 hover:bg-blue-700 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {isCreating && (
                <div
                  className="inline-block size-4 animate-spin rounded-full border-2 border-current border-e-transparent"
                  aria-hidden="true"
                />
              )}
              {isCreating ? 'Creating…' : 'Create'}
            </button>
            <button
              type="button"
              onClick={() => {
                const returnUrl = searchParams.get('ReturnUrl');
                if (returnUrl) {
                  navigate(returnUrl);
                } else {
                  navigate(-1);
                }
              }}
              className="inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors duration-150 hover:bg-gray-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400"
            >
              Cancel
            </button>
          </div>
        </DynamicForm>
      ) : !isLoading ? (
        /* Empty body state — matches RecordCreate.cshtml alert when
           page.Body is empty: "Page does not have page nodes attached" */
        <div
          className="rounded-md border border-blue-200 bg-blue-50 p-4 text-sm text-blue-700"
          role="status"
        >
          Page does not have page nodes attached
        </div>
      ) : null}
    </div>
  );
}

// ---------------------------------------------------------------------------
// PageBodyRenderer — Recursive Body Node Tree Component
// ---------------------------------------------------------------------------

/**
 * Props for the PageBodyRenderer component.
 */
interface PageBodyRendererProps {
  /** Ordered list of page body nodes to render. */
  nodes: PageBodyNode[];
  /** Entity metadata with field definitions (may be undefined while loading). */
  entity?: Entity;
  /** Component rendering mode — 'create' for record creation. */
  mode: string;
  /** Whether a form submission is in progress. */
  isSubmitting: boolean;
}

/**
 * Recursively renders page body nodes.
 *
 * Replaces the Razor ViewComponent invocation loop in RecordCreate.cshtml:
 * ```cshtml
 * @foreach (var node in currentPage.Body) {
 *   @await Component.InvokeAsync(rootComponentName, new PageComponentContext { ... })
 * }
 * ```
 *
 * Each node maps to a registered page component (PcRow, PcField*, PcSection,
 * PcForm, etc.). In the React SPA, these are represented as metadata-driven
 * field and layout components that respect the entity field definitions.
 *
 * For the create page, field components render in form/edit mode.
 */
function PageBodyRenderer({
  nodes,
  entity,
  mode,
  isSubmitting,
}: PageBodyRendererProps): React.JSX.Element {
  if (!nodes || nodes.length === 0) {
    return <></>;
  }

  return (
    <>
      {nodes.map((node) => (
        <PageBodyNodeRenderer
          key={node.id}
          node={node}
          entity={entity}
          mode={mode}
          isSubmitting={isSubmitting}
        />
      ))}
    </>
  );
}

// ---------------------------------------------------------------------------
// PageBodyNodeRenderer — Single Node Component
// ---------------------------------------------------------------------------

/**
 * Props for an individual page body node renderer.
 */
interface PageBodyNodeRendererProps {
  /** The body node to render. */
  node: PageBodyNode;
  /** Entity metadata with field definitions. */
  entity?: Entity;
  /** Component rendering mode. */
  mode: string;
  /** Whether a form submission is in progress. */
  isSubmitting: boolean;
}

/**
 * Renders a single page body node and its children recursively.
 *
 * Parses the node's JSON options to extract field configuration and
 * renders the appropriate component based on componentName. The
 * component name is a key like "PcFieldText", "PcRow", "PcSection", etc.
 *
 * For field components, the entity's field definitions provide type info
 * for proper rendering (input type, validation rules, select options, etc.).
 */
function PageBodyNodeRenderer({
  node,
  entity,
  mode,
  isSubmitting,
}: PageBodyNodeRendererProps): React.JSX.Element {
  // Parse node options from JSON string
  let options: Record<string, unknown> = {};
  if (node.options) {
    try {
      options = JSON.parse(node.options) as Record<string, unknown>;
    } catch {
      // Gracefully handle malformed JSON options
      options = {};
    }
  }

  // Extract field name from options (common pattern for PcField* components)
  const fieldName =
    (options.field_name as string) ||
    (options.fieldName as string) ||
    (options.name as string) ||
    '';

  // Look up the field definition from entity metadata
  const fieldDef = entity?.fields?.find(
    (f) => f.name === fieldName,
  );

  // Determine if this is a field component
  const isFieldComponent = node.componentName.startsWith('PcField');

  // Determine the display label from options or field definition
  const label =
    (options.label as string) ||
    fieldDef?.label ||
    fieldName ||
    node.componentName;

  // Common container CSS class from options
  const containerClass = (options.class as string) || '';

  // Render based on component type
  if (isFieldComponent && fieldName) {
    // Field component: render as a form input based on field type
    const isRequired = fieldDef?.required ?? false;
    const helpText = (options.description as string) || '';

    return (
      <div
        className={`page-body-node field-node mb-4 ${containerClass}`.trim()}
        data-node-id={node.id}
        data-component={node.componentName}
      >
        <div className="form-group">
          <label
            htmlFor={`field-${fieldName}`}
            className="mb-1 block text-sm font-medium text-gray-700"
          >
            {label}
            {isRequired && (
              <span className="ms-1 text-red-500" aria-hidden="true">
                *
              </span>
            )}
          </label>
          <input
            id={`field-${fieldName}`}
            name={fieldName}
            type={resolveInputType(node.componentName)}
            required={isRequired}
            disabled={isSubmitting}
            defaultValue=""
            aria-required={isRequired || undefined}
            aria-describedby={helpText ? `help-${fieldName}` : undefined}
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-gray-400 focus-visible:border-blue-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-[-1px] focus-visible:outline-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
          />
          {helpText && (
            <p
              id={`help-${fieldName}`}
              className="mt-1 text-xs text-gray-500"
            >
              {helpText}
            </p>
          )}
        </div>

        {/* Render any nested child nodes */}
        {node.nodes && node.nodes.length > 0 && (
          <PageBodyRenderer
            nodes={node.nodes}
            entity={entity}
            mode={mode}
            isSubmitting={isSubmitting}
          />
        )}
      </div>
    );
  }

  // Layout / container component (PcRow, PcSection, PcGrid, etc.)
  return (
    <div
      className={`page-body-node layout-node mb-4 ${containerClass}`.trim()}
      data-node-id={node.id}
      data-component={node.componentName}
    >
      {/* Render child nodes recursively */}
      {node.nodes && node.nodes.length > 0 && (
        <PageBodyRenderer
          nodes={node.nodes}
          entity={entity}
          mode={mode}
          isSubmitting={isSubmitting}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Utility: Resolve HTML input type from component name
// ---------------------------------------------------------------------------

/**
 * Maps a PcField* component name to the closest HTML input type.
 *
 * This provides a reasonable default input type for each of the 25+
 * field type components from the monolith. When a dedicated field
 * component is available, this mapping may be superseded.
 *
 * @param componentName - Page builder component name (e.g. "PcFieldText")
 * @returns HTML input type attribute value
 */
function resolveInputType(componentName: string): string {
  const typeMap: Record<string, string> = {
    PcFieldText: 'text',
    PcFieldEmail: 'email',
    PcFieldPhone: 'tel',
    PcFieldUrl: 'url',
    PcFieldNumber: 'number',
    PcFieldPercent: 'number',
    PcFieldCurrency: 'number',
    PcFieldDate: 'date',
    PcFieldDateTime: 'datetime-local',
    PcFieldTime: 'time',
    PcFieldPassword: 'password',
    PcFieldColor: 'color',
    PcFieldCheckbox: 'checkbox',
    PcFieldHidden: 'hidden',
    PcFieldFile: 'file',
    PcFieldImage: 'file',
    PcFieldTextarea: 'text',
    PcFieldHtml: 'text',
    PcFieldMultiLineText: 'text',
    PcFieldCode: 'text',
    PcFieldGuid: 'text',
    PcFieldAutoNumber: 'text',
    PcFieldSelect: 'text',
    PcFieldMultiSelect: 'text',
    PcFieldRelation: 'text',
    PcFieldGeography: 'text',
  };

  return typeMap[componentName] || 'text';
}
