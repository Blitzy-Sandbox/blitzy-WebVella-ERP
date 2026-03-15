/**
 * RecordRelatedRecordCreate — Related record creation page component.
 *
 * Replaces the monolith's `RecordRelatedRecordCreate.cshtml[.cs]`
 * (`RecordRelatedRecordCreatePageModel`). Handles creating a new record
 * that is related to a parent record via a many-to-many relation,
 * performing a transactional create followed by a M2M relation link.
 *
 * Route: /:appName/:areaName/:nodeName/r/:recordId/rl/:relationId/c/:pageName?
 *
 * Workflow:
 *  1. Resolve page context via usePageByUrl (replaces Init → ErpRequestContext)
 *  2. Canonical redirect when pageName ≠ resolved page.name
 *  3. Fetch relation metadata (for M2M link direction)
 *  4. Fetch entity metadata (for dynamic form field rendering)
 *  5. Fetch parent record (for context display)
 *  6. On submit:
 *     a. Generate UUID if missing (replaces Guid.NewGuid())
 *     b. Create record via useCreateRecord.mutateAsync
 *     c. Determine relation direction (origin vs target) from relation.originEntityId
 *     d. Create M2M link via useCreateManyToManyRelation.mutateAsync
 *  7. On success: redirect to related record details or ReturnUrl
 *  8. On error: display validation errors inline
 */
import React, { useState, useCallback, useEffect, useMemo } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';

import {
  useCreateRecord,
  useRecord,
  useCreateManyToManyRelation,
} from '../../hooks/useRecords';
import { useEntity, useRelation } from '../../hooks/useEntities';
import { usePageByUrl } from '../../hooks/usePages';
import type { ApiError } from '../../api/client';
import type { Entity, EntityRelation } from '../../types/entity';
import type { EntityRecord } from '../../types/record';
import type { ErpPage, PageBodyNode } from '../../types/page';
import { PageType } from '../../types/page';
import type { ErrorModel, UrlInfo } from '../../types/common';
import { useAppStore } from '../../stores/appStore';
import DynamicForm from '../../components/forms/DynamicForm';
import type { ValidationError, FormValidation } from '../../components/forms/DynamicForm';

// ---------------------------------------------------------------------------
// Route Parameter Types
// ---------------------------------------------------------------------------

/** Typed route parameters extracted from the URL pattern. */
interface RouteParams {
  appName: string;
  areaName: string;
  nodeName: string;
  /** Parent record GUID */
  recordId: string;
  /** M2M relation GUID */
  relationId: string;
  /** Optional canonical page name slug */
  pageName?: string;
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Empty validation state for initial render / reset. */
const EMPTY_VALIDATION: FormValidation = { errors: [] };

/** System-managed field names excluded from manual entry. */
const SYSTEM_FIELD_NAMES = new Set<string>([
  'id',
  'created_on',
  'created_by',
  'last_modified_on',
  'last_modified_by',
]);

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Safely parses a JSON-serialized options string from a PageBodyNode.
 * Returns an empty object on parse failure.
 */
function parseNodeOptions(options: string): Record<string, unknown> {
  if (!options) return {};
  try {
    const parsed: unknown = JSON.parse(options);
    if (parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
    return {};
  } catch {
    return {};
  }
}

/**
 * Resolves a human-readable label for a body node.
 * Field-type nodes use the "label" option; others fall back to componentName.
 */
function getNodeLabel(opts: Record<string, unknown>, componentName: string): string {
  if (typeof opts.label === 'string' && opts.label.length > 0) {
    return opts.label;
  }
  if (typeof opts.field_label === 'string' && opts.field_label.length > 0) {
    return opts.field_label;
  }
  return componentName.replace(/^Pc/, '');
}

/**
 * Resolves the field name for input binding from body-node options.
 */
function getNodeFieldName(opts: Record<string, unknown>): string {
  if (typeof opts.field_name === 'string') return opts.field_name;
  if (typeof opts.name === 'string') return opts.name;
  return '';
}

/**
 * Determines the appropriate HTML input type from a PageBodyNode componentName.
 */
function resolveInputType(componentName: string): string {
  const lower = componentName.toLowerCase();
  if (lower.includes('number') || lower.includes('percent') || lower.includes('currency')) return 'number';
  if (lower.includes('date') && lower.includes('time')) return 'datetime-local';
  if (lower.includes('date')) return 'date';
  if (lower.includes('email')) return 'email';
  if (lower.includes('phone')) return 'tel';
  if (lower.includes('url')) return 'url';
  if (lower.includes('password')) return 'password';
  if (lower.includes('checkbox')) return 'checkbox';
  return 'text';
}

/**
 * Returns true when the component name represents a field input node
 * (e.g. PcFieldText, PcFieldDate).
 */
function isFieldNode(componentName: string): boolean {
  return componentName.startsWith('PcField');
}

/**
 * Returns true when the component name represents a multi-line text input
 * (e.g. PcFieldHtml, PcFieldMultiLineText).
 */
function isMultiLineNode(componentName: string): boolean {
  const lower = componentName.toLowerCase();
  return lower.includes('multiline') || lower.includes('html') || lower.includes('textarea');
}

/**
 * Recursively renders a page body node tree as structural layout containers
 * with field inputs for field-type nodes.
 */
function renderBodyNode(
  node: PageBodyNode,
  validationLookup: Map<string, string>,
): React.ReactNode {
  const opts = parseNodeOptions(node.options);
  const childNodes = node.nodes?.length
    ? node.nodes.map((child) => renderBodyNode(child, validationLookup))
    : null;

  /* ---- Field node → render labelled input ---- */
  if (isFieldNode(node.componentName)) {
    const fieldName = getNodeFieldName(opts);
    const label = getNodeLabel(opts, node.componentName);
    const fieldError = fieldName ? validationLookup.get(fieldName) : undefined;
    const isRequired = opts.required === true;

    if (isMultiLineNode(node.componentName)) {
      return (
        <div key={node.id} className="mb-4">
          <label
            htmlFor={`field-${node.id}`}
            className="block text-sm font-medium text-gray-700 mb-1"
          >
            {label}
            {isRequired && <span className="text-red-500 ms-0.5" aria-hidden="true">*</span>}
          </label>
          <textarea
            id={`field-${node.id}`}
            name={fieldName}
            required={isRequired}
            rows={4}
            className={
              'block w-full rounded-md border px-3 py-2 text-sm shadow-sm ' +
              'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ' +
              (fieldError
                ? 'border-red-500 text-red-900'
                : 'border-gray-300 text-gray-900')
            }
          />
          {fieldError && (
            <p className="mt-1 text-sm text-red-600" role="alert">
              {fieldError}
            </p>
          )}
        </div>
      );
    }

    return (
      <div key={node.id} className="mb-4">
        <label
          htmlFor={`field-${node.id}`}
          className="block text-sm font-medium text-gray-700 mb-1"
        >
          {label}
          {isRequired && <span className="text-red-500 ms-0.5" aria-hidden="true">*</span>}
        </label>
        <input
          id={`field-${node.id}`}
          type={resolveInputType(node.componentName)}
          name={fieldName}
          required={isRequired}
          className={
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm ' +
            'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ' +
            (fieldError
              ? 'border-red-500 text-red-900'
              : 'border-gray-300 text-gray-900')
          }
        />
        {fieldError && (
          <p className="mt-1 text-sm text-red-600" role="alert">
            {fieldError}
          </p>
        )}
      </div>
    );
  }

  /* ---- Container / layout node ---- */
  /* PcRow uses a CSS grid; PcSection wraps in a bordered card */
  const isRow = node.componentName === 'PcRow';
  const isSection = node.componentName === 'PcSection';
  const sectionLabel = isSection ? getNodeLabel(opts, node.componentName) : undefined;

  if (isSection) {
    return (
      <section
        key={node.id}
        className="mb-6 rounded-lg border border-gray-200 bg-white"
        aria-label={sectionLabel}
      >
        {sectionLabel && (
          <div className="border-b border-gray-200 px-4 py-3">
            <h2 className="text-base font-semibold text-gray-800">{sectionLabel}</h2>
          </div>
        )}
        <div className="p-4">{childNodes}</div>
      </section>
    );
  }

  if (isRow) {
    return (
      <div key={node.id} className="grid grid-cols-12 gap-4 mb-4">
        {childNodes}
      </div>
    );
  }

  /* Generic wrapper */
  return (
    <div key={node.id} data-component={node.componentName}>
      {childNodes}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main Component
// ---------------------------------------------------------------------------

/**
 * RecordRelatedRecordCreate — page component for creating a record that is
 * linked to a parent record via a many-to-many entity relation.
 *
 * Replicates the transactional OnPost flow from
 * `RecordRelatedRecordCreatePageModel`:
 *   CreateRecord → CreateRelationManyToManyRecord → Redirect
 */
export default function RecordRelatedRecordCreate(): React.JSX.Element {
  const params = useParams<keyof RouteParams>() as Partial<RouteParams>;
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  // ---- Local UI State ----
  const [validation, setValidation] = useState<FormValidation>(EMPTY_VALIDATION);

  // ---- App Store (navigation context) ----
  // Destructure required navigation context members from the Zustand store.
  // These replace the monolith's ErpRequestContext properties set by Init().
  const { currentApp, currentArea, currentNode, currentPage: storePage } =
    useAppStore.getState();

  // Read reactive state via the hook for render-triggering reactivity.
  const appStore = useAppStore();
  const activeApp = appStore.currentApp ?? currentApp;
  const activeArea = appStore.currentArea ?? currentArea;
  const activeNode = appStore.currentNode ?? currentNode;
  const activeStorePage = appStore.currentPage ?? storePage;

  // ---- Build UrlInfo for page context resolution ----
  const urlInfo: UrlInfo | undefined = useMemo(() => {
    if (!params.appName || !params.areaName || !params.nodeName) {
      return undefined;
    }
    return {
      hasRelation: true,
      pageType: PageType.RecordCreate as number,
      appName: params.appName,
      areaName: params.areaName,
      nodeName: params.nodeName,
      pageName: params.pageName ?? '',
      recordId: params.recordId,
      relationId: params.relationId,
      parentRecordId: params.recordId,
    };
  }, [
    params.appName,
    params.areaName,
    params.nodeName,
    params.pageName,
    params.recordId,
    params.relationId,
  ]);

  // ---- 1. Resolve page context (replaces Init → ErpRequestContext) ----
  const { data: pageResponse, isLoading: pageLoading } = usePageByUrl(urlInfo);
  const page: ErpPage | undefined = pageResponse?.object;

  // ---- 2. Canonical redirect when pageName differs from resolved page.name ----
  useEffect(() => {
    if (!page || !params.pageName) return;
    if (page.name && params.pageName !== page.name) {
      const returnUrl = searchParams.get('ReturnUrl');
      const basePath = `/${params.appName}/${params.areaName}/${params.nodeName}/r/${params.recordId}/rl/${params.relationId}/c/${page.name}`;
      const redirectPath = returnUrl
        ? `${basePath}?ReturnUrl=${encodeURIComponent(returnUrl)}`
        : basePath;
      navigate(redirectPath, { replace: true });
    }
  }, [page, params.appName, params.areaName, params.nodeName, params.recordId, params.relationId, params.pageName, navigate, searchParams]);

  // ---- 2b. Synchronise app store with resolved page and route context ----
  // Replaces the monolith's Init() which populated ErpRequestContext per-request.
  useEffect(() => {
    if (!page) return;
    // Update current page in store so other layout components (sidebar, breadcrumbs) react.
    useAppStore.setState({
      currentPage: page,
      pageName: page.name ?? '',
      recordId: params.recordId ?? null,
      relationId: params.relationId ?? null,
      parentRecordId: params.recordId ?? null,
    });
  }, [page, params.recordId, params.relationId]);

  // ---- 3. Fetch relation metadata (replaces EntityRelationManager().Read()) ----
  const { data: relation, isLoading: relationLoading } = useRelation(
    params.relationId ?? '',
  );

  // ---- 4. Fetch entity metadata for the NEW record's entity ----
  const { data: entity, isLoading: entityLoading } = useEntity(
    page?.entityId ?? '',
  );

  // ---- 5. Derive entity display metadata ----
  // Entity.label provides the user-facing name for the heading.
  // Entity.fields provides field definitions for validation context.
  const entityLabel: string = entity?.label ?? '';
  const entityFields = entity?.fields ?? [];
  const editableFieldCount = entityFields.filter(
    (f) => !f.system && !SYSTEM_FIELD_NAMES.has(f.name),
  ).length;

  // ---- 5b. Derive parent entity name from relation direction ----
  // Accesses relation.originEntityId and relation.targetEntityId to determine
  // the correct direction for the CreateRelationManyToManyRecord call.
  const parentEntityName: string = useMemo(() => {
    if (!entity || !relation) return '';
    // Explicitly read both IDs for direction determination
    const originEntityId = relation.originEntityId;
    const targetEntityId = relation.targetEntityId;
    // If new record's entity is on the origin side, parent is on the target side
    if (originEntityId === entity.id) {
      return relation.targetEntityName ?? '';
    }
    // If new record's entity is on the target side, parent is on the origin side
    if (targetEntityId === entity.id) {
      return relation.originEntityName ?? '';
    }
    // Fallback — shouldn't reach here in valid relation configurations
    return relation.originEntityName ?? '';
  }, [entity, relation]);

  // ---- 6. Fetch parent record for context display ----
  const { data: parentRecord, isLoading: parentRecordLoading } = useRecord(
    parentEntityName,
    params.recordId ?? '',
  );

  // ---- 7. Mutation hooks ----
  const createRecordMutation = useCreateRecord();
  const createM2MMutation = useCreateManyToManyRelation();

  // ---- 8. Validation error lookup map for per-field display ----
  const validationLookup: Map<string, string> = useMemo(() => {
    const map = new Map<string, string>();
    for (const err of validation.errors) {
      if (err.propertyName) {
        map.set(err.propertyName, err.message);
      }
    }
    return map;
  }, [validation.errors]);

  // ---- 9. Form submission handler ----
  const handleSubmit = useCallback(
    async (event: React.FormEvent<HTMLFormElement>) => {
      event.preventDefault();
      setValidation(EMPTY_VALIDATION);

      if (!entity || !relation || !params.recordId || !params.relationId) {
        return;
      }

      // Extract form field values from the native <form>
      const formEl = event.currentTarget;
      const formData = new FormData(formEl);
      const recordData: EntityRecord = {};

      formData.forEach((value, key) => {
        if (key && typeof value === 'string') {
          recordData[key] = value;
        }
      });

      // Generate UUID if not present (replaces Guid.NewGuid() on line 67-68)
      if (!recordData.id) {
        recordData.id = crypto.randomUUID();
      }

      const newRecordId = recordData.id as string;

      try {
        // Step A: Create the record in the entity
        await createRecordMutation.mutateAsync({
          entityName: entity.name,
          data: recordData,
        });

        // Step B: Determine M2M link direction
        // If new record's entity is the origin entity in the relation,
        // the new record is the origin and the parent is the target.
        // Otherwise, the parent is the origin and the new record is the target.
        const isNewRecordOrigin = relation.originEntityId === entity.id;
        const originId = isNewRecordOrigin ? newRecordId : params.recordId;
        const targetId = isNewRecordOrigin ? params.recordId : newRecordId;

        await createM2MMutation.mutateAsync({
          relationId: relation.id,
          originId,
          targetId,
        });

        // Step C: Redirect on success
        const returnUrl = searchParams.get('ReturnUrl');
        if (returnUrl) {
          navigate(returnUrl);
        } else {
          // Navigate to the newly created related record details page
          navigate(
            `/${params.appName}/${params.areaName}/${params.nodeName}` +
              `/r/${params.recordId}/rl/${params.relationId}/r/${newRecordId}`,
          );
        }
      } catch (error: unknown) {
        const apiError = error as ApiError;
        const errors: ValidationError[] = [];

        if (apiError.errors && Array.isArray(apiError.errors)) {
          for (const item of apiError.errors) {
            errors.push({
              propertyName: (item as ErrorModel).key ?? '',
              message: (item as ErrorModel).message ?? '',
            });
          }
        }

        setValidation({
          message:
            apiError.message ||
            'An error occurred while creating the related record.',
          errors,
        });

        // Scroll to the top so the user sees the validation summary
        window.scrollTo({ top: 0, behavior: 'smooth' });
      }
    },
    [
      entity,
      relation,
      params.recordId,
      params.relationId,
      params.appName,
      params.areaName,
      params.nodeName,
      navigate,
      searchParams,
      createRecordMutation,
      createM2MMutation,
    ],
  );

  // ---- 10. Derived loading / submitting / error flags ----
  const isLoading =
    pageLoading || entityLoading || relationLoading || parentRecordLoading;
  const isSubmitting =
    createRecordMutation.isPending || createM2MMutation.isPending;

  // Surface mutation-level error state (useCreateRecord.isError / .error).
  // These complement the manually-set validation state from the catch block
  // and ensure the component reflects mutation status from TanStack Query.
  const hasMutationError: boolean =
    createRecordMutation.isError || createM2MMutation.isError;
  const mutationErrorMessage: string =
    (createRecordMutation.error as Error | undefined)?.message ??
    (createM2MMutation.error as Error | undefined)?.message ??
    '';

  // ---- 11. Loading spinner ----
  if (isLoading) {
    return (
      <div
        className="flex items-center justify-center min-h-[12rem]"
        role="status"
        aria-label="Loading page"
      >
        <div
          className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"
          aria-hidden="true"
        />
        <span className="sr-only">Loading page…</span>
      </div>
    );
  }

  // ---- 12. Not found ----
  if (!page) {
    return (
      <div className="p-6" role="alert">
        <h1 className="text-xl font-semibold text-gray-800">Page Not Found</h1>
        <p className="mt-2 text-gray-600">
          The requested related record creation page could not be found.
        </p>
        <button
          type="button"
          className={
            'mt-4 inline-flex items-center rounded-md bg-blue-600 px-4 py-2 ' +
            'text-sm font-medium text-white hover:bg-blue-700 ' +
            'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600'
          }
          onClick={() => navigate(-1)}
        >
          Go Back
        </button>
      </div>
    );
  }

  // ---- 13. Determine body node availability ----
  const hasBodyNodes = Array.isArray(page.body) && page.body.length > 0;

  // ---- 14. Effective validation state — merge manual + mutation-level errors ----
  const effectiveValidation: FormValidation =
    validation.message || validation.errors.length > 0
      ? validation
      : hasMutationError
        ? { message: mutationErrorMessage, errors: [] }
        : EMPTY_VALIDATION;

  const showValidation =
    !!effectiveValidation.message || effectiveValidation.errors.length > 0;

  // ---- 15. Render ----
  return (
    <div className="record-related-create-page">
      {/* Page heading — uses entity.label for context alongside page.label */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900">
          {page.label || 'Create Related Record'}
        </h1>
        {/* Entity and parent context breadcrumb (uses Entity.label, parentRecord.id) */}
        {(entityLabel || parentRecord) && (
          <p className="mt-1 text-sm text-gray-500">
            {entityLabel && (
              <span>
                Entity: <span className="font-medium text-gray-700">{entityLabel}</span>
                {editableFieldCount > 0 && (
                  <span className="ms-1 text-gray-400">
                    ({editableFieldCount} field{editableFieldCount !== 1 ? 's' : ''})
                  </span>
                )}
              </span>
            )}
            {entityLabel && parentRecord && <span className="mx-2">·</span>}
            {parentRecord && (
              <span>
                Parent Record: <span className="font-medium text-gray-700">{parentRecord.id ?? params.recordId}</span>
              </span>
            )}
          </p>
        )}
        {/* Show current navigation context from appStore for contextual awareness */}
        {activeApp && activeArea && activeNode && (
          <nav className="mt-2 text-xs text-gray-400" aria-label="Current navigation context">
            {activeApp.name}
            {activeArea.name ? ` / ${activeArea.name}` : ''}
            {activeNode.name ? ` / ${activeNode.name}` : ''}
            {activeStorePage?.name ? ` / ${activeStorePage.name}` : ''}
          </nav>
        )}
      </div>

      {hasBodyNodes ? (
        <DynamicForm
          name="RecordRelatedRecordCreate"
          validation={effectiveValidation}
          showValidation={showValidation}
          onSubmit={handleSubmit}
          className="space-y-4"
        >
          {/* Body node tree — structural layout with field inputs */}
          {page.body.map((node) => renderBodyNode(node, validationLookup))}

          {/* Action buttons */}
          <div className="flex items-center gap-3 pt-4 border-t border-gray-200">
            <button
              type="submit"
              disabled={isSubmitting}
              className={
                'inline-flex items-center rounded-md px-4 py-2 text-sm font-semibold text-white shadow-sm ' +
                'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-600 ' +
                (isSubmitting
                  ? 'bg-blue-400 cursor-not-allowed'
                  : 'bg-blue-600 hover:bg-blue-700')
              }
              aria-busy={isSubmitting}
            >
              {isSubmitting ? 'Creating…' : 'Create'}
            </button>

            <button
              type="button"
              disabled={isSubmitting}
              className={
                'inline-flex items-center rounded-md border border-gray-300 bg-white px-4 py-2 ' +
                'text-sm font-semibold text-gray-700 shadow-sm hover:bg-gray-50 ' +
                'focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-gray-400 ' +
                (isSubmitting ? 'cursor-not-allowed opacity-50' : '')
              }
              onClick={() => {
                const returnUrl = searchParams.get('ReturnUrl');
                if (returnUrl) {
                  navigate(returnUrl);
                } else {
                  navigate(-1);
                }
              }}
            >
              Cancel
            </button>
          </div>
        </DynamicForm>
      ) : (
        /* Empty body state — matches monolith info message */
        <div
          className="rounded-md bg-blue-50 p-4 text-sm text-blue-700"
          role="alert"
        >
          <p>Page does not have page body nodes attached.</p>
        </div>
      )}
    </div>
  );
}
