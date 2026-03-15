/**
 * DynamicForm — Dynamic Form Builder Component
 *
 * Replaces the monolith's PcForm ViewComponent (WebVella.Erp.Web/Components/PcForm/).
 * Top-level form wrapper providing a <form> element with configurable method, name, ID,
 * validation display, and — most critically — propagation of label render mode and field
 * render mode to all descendant field components via React Context.
 *
 * Composition hierarchy: DynamicForm > FormSection > FormRow > FieldComponents
 *
 * Source mapping:
 *  - PcFormOptions model  → DynamicFormProps interface
 *  - WvLabelRenderMode    → LabelRenderMode type
 *  - WvFieldRenderMode    → FieldRenderMode type
 *  - context.Items[…]     → FormContext (React Context)
 *  - <wv-validation>      → ValidationSummary inline component
 *  - <wv-form>            → native <form> element
 */

import {
  createContext,
  useContext,
  useMemo,
  useCallback,
  useId,
  type ReactNode,
  type FormEvent,
} from 'react';

/* ────────────────────────────────────────────────────────────────
 * Type Exports
 *
 * Maps to WvLabelRenderMode / WvFieldRenderMode C# enums.
 * The "Undefined" sentinel (value 0) is handled at the component
 * level — callers that omit the prop get the documented default.
 * ──────────────────────────────────────────────────────────────── */

/**
 * Label positioning relative to its field.
 *
 * | Value        | Source Enum              | Behaviour                        |
 * |------------- |--------------------------|----------------------------------|
 * | `stacked`    | WvLabelRenderMode.Stacked    | Label above field (default)  |
 * | `horizontal` | WvLabelRenderMode.Horizontal | Label beside field           |
 * | `inline`     | WvLabelRenderMode.Inline     | Label inline with field      |
 */
export type LabelRenderMode = 'stacked' | 'horizontal' | 'inline';

/**
 * Field interaction mode.
 *
 * | Value          | Source Enum                | Behaviour                          |
 * |--------------- |----------------------------|------------------------------------|
 * | `form`         | WvFieldRenderMode.Form       | Full form editing (default)      |
 * | `inlineEdit`   | WvFieldRenderMode.InlineEdit | Click-to-edit inline             |
 * | `simple`       | WvFieldRenderMode.Simple     | Simplified edit capability       |
 * | `display`      | WvFieldRenderMode.Display    | Read-only display                |
 */
export type FieldRenderMode = 'form' | 'inlineEdit' | 'simple' | 'display';

/* ────────────────────────────────────────────────────────────────
 * Validation Interfaces
 *
 * Maps to ValidationException.Message + .ToErrorList() from
 * PcForm.cs line 139 and Display.cshtml line 19.
 * ──────────────────────────────────────────────────────────────── */

/** Single field-level validation error. */
export interface ValidationError {
  /** Property or field name that failed validation. */
  propertyName: string;
  /** Human-readable error message for this field. */
  message: string;
}

/** Aggregated form validation state. */
export interface FormValidation {
  /** Optional top-level summary message (maps to ValidationException.Message). */
  message?: string;
  /** Individual field-level errors (maps to ValidationException.ToErrorList()). */
  errors: ValidationError[];
}

/* ────────────────────────────────────────────────────────────────
 * FormContext — Render-Mode Propagation
 *
 * Replaces the monolith's `context.Items[typeof(WvLabelRenderMode)]`
 * and `context.Items[typeof(WvFieldRenderMode)]` propagation
 * (PcForm.cs lines 114-115). All descendant components consume
 * this context to determine their rendering style.
 * ──────────────────────────────────────────────────────────────── */

/** Context value available to all descendants within a DynamicForm. */
export interface FormContextValue {
  /** Active label positioning mode for descendant fields. */
  labelMode: LabelRenderMode;
  /** Active field interaction mode for descendant fields. */
  fieldMode: FieldRenderMode;
  /** DOM id of the enclosing form element. */
  formId: string;
  /** name attribute of the enclosing form element. */
  formName: string;
}

/**
 * React Context carrying form render-mode configuration.
 *
 * Defaults match the PcFormOptions defaults:
 *  - labelMode  → 'stacked'  (WvLabelRenderMode.Stacked)
 *  - fieldMode  → 'form'     (WvFieldRenderMode.Form)
 *  - formId     → ''
 *  - formName   → 'form'
 */
export const FormContext = createContext<FormContextValue>({
  labelMode: 'stacked',
  fieldMode: 'form',
  formId: '',
  formName: 'form',
});

/**
 * Convenience hook for consuming the nearest FormContext.
 *
 * Usage within any descendant of DynamicForm:
 * ```tsx
 * const { labelMode, fieldMode, formId, formName } = useFormContext();
 * ```
 */
export const useFormContext = (): FormContextValue => useContext(FormContext);

/* ────────────────────────────────────────────────────────────────
 * DynamicFormProps
 *
 * Maps 1:1 to PcFormOptions properties (PcForm.cs lines 26-55).
 * ──────────────────────────────────────────────────────────────── */

interface DynamicFormProps {
  /* ── Core form attributes ─────────────────────────────────── */

  /** Form DOM id. Auto-generated as "wv-{useId()}" when omitted (source: line 94). */
  id?: string;
  /** Form name attribute. Default: "form" (source: PcFormOptions.Name). */
  name?: string;
  /** HTTP method. Default: "post" (source: PcFormOptions.Method). */
  method?: 'get' | 'post';

  /* ── Render mode configuration (propagated via FormContext) ─ */

  /** Label positioning. Default: 'stacked' (source: WvLabelRenderMode.Stacked). */
  labelMode?: LabelRenderMode;
  /** Field interaction mode. Default: 'form' (source: WvFieldRenderMode.Form). */
  fieldMode?: FieldRenderMode;

  /* ── Form behaviour ─────────────────────────────────────── */

  /** Hook key appended as query parameter to computed action URL (source: lines 144-158). */
  hookKey?: string;
  /** Explicit action URL — overrides hookKey-computed URL. */
  actionUrl?: string;
  /** Display validation summary above form. Default: true (source: PcFormOptions.ShowValidation). */
  showValidation?: boolean;
  /** Validation errors to render in the summary. */
  validation?: FormValidation;

  /* ── Visibility & styling ──────────────────────────────── */

  /** When false the form renders nothing (source: lines 124-137). Default: true. */
  isVisible?: boolean;
  /** Custom CSS classes applied to the <form> element (source: PcFormOptions.Class). */
  className?: string;

  /* ── Events ────────────────────────────────────────────── */

  /** Custom submit handler. Receives the native form event. */
  onSubmit?: (event: FormEvent<HTMLFormElement>) => void | Promise<void>;

  /**
   * When true (default), HTML5 constraint validation is suppressed via
   * the `noValidate` form attribute.  Set to false to re-enable native
   * browser validation (`:invalid` pseudo-class, tooltips, submission
   * prevention) for forms that rely on `required` / `pattern` attributes.
   */
  disableNativeValidation?: boolean;

  /* ── Composition ───────────────────────────────────────── */

  /** Child elements (FormSection, FormRow, FieldComponents). */
  children?: ReactNode;
}

/* ────────────────────────────────────────────────────────────────
 * ValidationSummary — inline sub-component
 *
 * Replaces the <wv-validation> TagHelper rendered in
 * Display.cshtml lines 17-20. Renders BEFORE the <form> element
 * to match the source layout.
 * ──────────────────────────────────────────────────────────────── */

function ValidationSummary({
  validation,
}: {
  validation?: FormValidation;
}): ReactNode {
  if (
    !validation ||
    (!validation.message && validation.errors.length === 0)
  ) {
    return null;
  }

  return (
    <div
      role="alert"
      aria-live="assertive"
      className="mb-4 rounded-lg border border-red-200 bg-red-50 p-4"
    >
      {validation.message && (
        <p className="mb-2 font-medium text-red-800">{validation.message}</p>
      )}
      {validation.errors.length > 0 && (
        <ul className="list-disc ps-5 text-sm text-red-700">
          {validation.errors.map((err, idx) => (
            <li key={`${err.propertyName}-${idx}`}>
              <span className="font-medium">{err.propertyName}</span>
              {err.propertyName && err.message ? ': ' : ''}
              {err.message}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/* ────────────────────────────────────────────────────────────────
 * buildActionUrl — Hook-Key Action URL Computation
 *
 * Mirrors PcForm.cs lines 144-158 where, when a hookKey is provided,
 * the current request path and existing query params are recomposed
 * with `hookKey={value}` appended (replacing any existing hookKey).
 *
 * In the SPA context we read from window.location instead of
 * HttpContext.Request.
 * ──────────────────────────────────────────────────────────────── */

function buildActionUrl(hookKey: string): string {
  if (typeof window === 'undefined') {
    return '';
  }
  const url = new URL(window.location.href);
  url.searchParams.set('hookKey', hookKey);
  return `${url.pathname}?${url.searchParams.toString()}`;
}

/* ────────────────────────────────────────────────────────────────
 * DynamicForm Component
 *
 * Functional component — default export.
 * ──────────────────────────────────────────────────────────────── */

function DynamicForm({
  id,
  name,
  method,
  labelMode,
  fieldMode,
  hookKey,
  actionUrl,
  showValidation = true,
  validation,
  isVisible = true,
  className,
  onSubmit,
  disableNativeValidation = true,
  children,
}: DynamicFormProps): ReactNode {
  /* ── 1. Auto-generate form ID (source: lines 92-95) ────── */
  const autoId = useId();
  const formId = id || `wv-${autoId}`;

  /* ── 2. Resolve render modes (source: lines 86-89) ─────── */
  const resolvedLabelMode: LabelRenderMode = labelMode ?? 'stacked';
  const resolvedFieldMode: FieldRenderMode = fieldMode ?? 'form';

  /* ── 3. Resolve form name/method (source defaults) ─────── */
  const formName = name || 'form';
  const formMethod = method || 'post';

  /* ── 4. Memoize context value (source: lines 114-115) ──── */
  const contextValue = useMemo<FormContextValue>(
    () => ({
      labelMode: resolvedLabelMode,
      fieldMode: resolvedFieldMode,
      formId,
      formName,
    }),
    [resolvedLabelMode, resolvedFieldMode, formId, formName],
  );

  /* ── 5. Compute action URL (source: lines 144-158) ─────── */
  const computedAction = useMemo<string | undefined>(() => {
    if (actionUrl) {
      return actionUrl;
    }
    if (hookKey) {
      return buildActionUrl(hookKey);
    }
    return undefined;
  }, [actionUrl, hookKey]);

  /* ── 6. Submit handler (SPA: preventDefault by default) ─── */
  const handleSubmit = useCallback(
    (event: FormEvent<HTMLFormElement>) => {
      if (onSubmit) {
        event.preventDefault();
        onSubmit(event);
        return;
      }

      /*
       * When no explicit onSubmit is provided but a hookKey/actionUrl
       * exists, allow the browser's native form submission to the
       * computed action URL (non-SPA fallback matching source behaviour).
       * The action attribute is already set on the form element.
       *
       * For pure SPA mode (no hookKey, no actionUrl, no onSubmit)
       * prevent the default browser submission.
       */
      if (!computedAction) {
        event.preventDefault();
      }
    },
    [onSubmit, computedAction],
  );

  /* ── 7. Visibility gate (source: lines 124-137) ────────── */
  if (isVisible === false) {
    return null;
  }

  /* ── 8. Render ─────────────────────────────────────────── */
  return (
    <>
      {/* Validation summary rendered BEFORE the form (Display.cshtml lines 17-20) */}
      {showValidation && <ValidationSummary validation={validation} />}

      <FormContext.Provider value={contextValue}>
        <form
          id={formId}
          name={formName}
          method={formMethod}
          action={computedAction}
          className={className}
          onSubmit={handleSubmit}
          noValidate={disableNativeValidation}
        >
          {children}
        </form>
      </FormContext.Provider>
    </>
  );
}

export default DynamicForm;
