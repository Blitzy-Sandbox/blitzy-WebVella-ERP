/**
 * @file libs/shared-ui/src/components/Form.tsx
 *
 * Dynamic Form Builder Component — replaces the monolith's PcForm ViewComponent
 * (`WebVella.Erp.Web/Components/PcForm/`).
 *
 * Provides:
 * - `DynamicForm`   — configurable <form> wrapper with validation, visibility,
 *                      label-mode / field-render-mode propagation via React Context
 * - `FormContext`    — React Context that child field components consume
 * - `useFormContext` — convenience hook for reading FormContext
 *
 * Behavioural parity with the source PcForm component is maintained:
 *   • Configurable HTTP method (PcForm.cs line 38)
 *   • Label mode propagation (PcForm.cs line 44, context.Items)
 *   • Field render mode propagation (PcForm.cs line 47, context.Items)
 *   • Validation display with toggleable visibility (PcForm.cs line 53)
 *   • hookKey-based action URL construction (PcForm.cs lines 145-158)
 *   • Visibility control (PcForm.cs lines 124-137)
 *   • Auto-generated form ID (PcForm.cs lines 92-95)
 */

import {
  createContext,
  useContext,
  useId,
  useMemo,
  useCallback,
  type ReactNode,
  type ReactElement,
  type FormEvent,
} from 'react';

/* ------------------------------------------------------------------ */
/*  Type Definitions                                                   */
/* ------------------------------------------------------------------ */

/**
 * Individual validation error — mirrors the monolith's
 * `ValidationException.ToErrorList()` shape.
 */
export interface ValidationError {
  /** Field name or key that has the error */
  key: string;
  /** Human-readable error message */
  message: string;
}

/**
 * Props for the `DynamicForm` component.
 * Maps 1-to-1 with PcFormOptions (PcForm.cs lines 26-55).
 */
export interface DynamicFormProps {
  /**
   * Form HTML id.
   * Maps to PcForm's `id` option.  When omitted the component auto-generates
   * a stable identifier via React `useId()` (mirrors PcForm.cs lines 92-95
   * where `"wv-" + context.Node.Id` is used when the option is blank).
   */
  id?: string;

  /** Form `name` attribute — default `"form"` (PcForm.cs line 35) */
  name?: string;

  /** HTTP method — default `"post"` (PcForm.cs line 38) */
  method?: 'get' | 'post' | 'put' | 'patch' | 'delete';

  /**
   * Form action URL.
   * If `hookKey` is also provided, a `hookKey=…` query parameter is appended
   * (mirrors PcForm.cs lines 145-158).
   */
  action?: string;

  /**
   * Hook key appended as a query parameter to the action URL.
   * Maps to PcForm's `hook_key` option (PcForm.cs line 41).
   */
  hookKey?: string;

  /**
   * Label rendering mode propagated to child field components.
   * Maps to PcForm's `label_mode` / `WvLabelRenderMode` (PcForm.cs line 44).
   * Default: `"stacked"`.
   */
  labelMode?: 'stacked' | 'horizontal' | 'hidden';

  /**
   * Field rendering mode propagated to child field components.
   * Maps to PcForm's `mode` / `WvFieldRenderMode` (PcForm.cs line 47).
   * Default: `"form"`.
   */
  fieldRenderMode?: 'form' | 'display' | 'inline-edit' | 'simple';

  /** Additional CSS class(es) — maps to PcForm's `class` option (PcForm.cs line 50) */
  className?: string;

  /** Show the validation summary above the form — default `true` (PcForm.cs line 53) */
  showValidation?: boolean;

  /** Visibility flag — when `false` the entire form is not rendered (PcForm.cs lines 124-137) */
  isVisible?: boolean;

  /** Validation errors rendered inside the summary and propagated via context */
  validationErrors?: ValidationError[];

  /** Optional headline message shown inside the validation summary */
  validationMessage?: string;

  /** Child components — field components, buttons, layout containers, etc. */
  children: ReactNode;

  /**
   * Client-side form submit handler.
   * When provided, `event.preventDefault()` is called automatically (SPA mode).
   * When omitted, native form submission proceeds for progressive enhancement.
   */
  onSubmit?: (event: FormEvent<HTMLFormElement>) => void;

  /** Whether the form is currently being submitted (disables re-submission, propagated via context) */
  isSubmitting?: boolean;
}

/* ------------------------------------------------------------------ */
/*  Form Context                                                       */
/* ------------------------------------------------------------------ */

/**
 * Shape of the context value propagated to child field components.
 * Mirrors the data that PcForm.cs pushes into `context.Items` (lines 114-115)
 * plus the validation state from `context.Items[typeof(ValidationException)]` (line 141).
 */
export interface FormContextValue {
  /** Label rendering mode propagated to child fields */
  labelMode: 'stacked' | 'horizontal' | 'hidden';
  /** Field render mode propagated to child fields */
  fieldRenderMode: 'form' | 'display' | 'inline-edit' | 'simple';
  /** Validation errors accessible by child fields for per-field highlighting */
  validationErrors: ValidationError[];
  /** Whether the form is currently submitting */
  isSubmitting: boolean;
}

/**
 * React Context used to propagate form-level settings to child field
 * components without prop-drilling.
 */
export const FormContext = createContext<FormContextValue>({
  labelMode: 'stacked',
  fieldRenderMode: 'form',
  validationErrors: [],
  isSubmitting: false,
});

/**
 * Convenience hook for child field components to read the nearest
 * `FormContext` value.
 *
 * @example
 * ```tsx
 * const { labelMode, fieldRenderMode, validationErrors, isSubmitting } = useFormContext();
 * ```
 */
export function useFormContext(): FormContextValue {
  return useContext(FormContext);
}

/* ------------------------------------------------------------------ */
/*  Helpers                                                            */
/* ------------------------------------------------------------------ */

/**
 * Minimal class-name joiner — concatenates truthy string fragments,
 * avoiding a runtime dependency on `clsx` / `classnames`.
 */
function joinClassNames(...classes: (string | undefined | null | false)[]): string {
  return classes.filter(Boolean).join(' ');
}

/**
 * Build the form action URL, optionally appending a `hookKey` query parameter.
 *
 * Mirrors PcForm.cs lines 145-158:
 *  - If hookKey is provided, existing query params are preserved (except any
 *    prior `hookKey` value which is overridden), and `hookKey=<value>` is appended.
 *  - If the base URL already contains a query string, `&hookKey=<value>` is used;
 *    otherwise `?hookKey=<value>` is used.
 */
function buildActionUrl(action: string | undefined, hookKey: string | undefined): string | undefined {
  if (!action) {
    /* When there is no action, a hookKey alone cannot form a URL. */
    if (hookKey) {
      return `?hookKey=${encodeURIComponent(hookKey)}`;
    }
    return undefined;
  }

  if (!hookKey) {
    return action;
  }

  /* Strip any existing hookKey param from the URL before re-appending */
  const url = new URL(action, 'http://localhost'); /* base is required by URL but stripped below */
  url.searchParams.delete('hookKey');
  url.searchParams.set('hookKey', hookKey);

  /* Reconstruct a root-relative URL (pathname + search) */
  const isAbsolute = /^https?:\/\//.test(action);
  if (isAbsolute) {
    return url.toString();
  }

  return `${url.pathname}${url.search}`;
}

/* ------------------------------------------------------------------ */
/*  Label-mode CSS helpers                                             */
/* ------------------------------------------------------------------ */

/**
 * Returns a data attribute value so child components can adapt their
 * layout via Tailwind CSS selectors or attribute selectors.
 *
 * The data attribute is placed on the `<form>` element:
 * - `data-label-mode="stacked"`      → labels stacked above fields (default)
 * - `data-label-mode="horizontal"`   → labels inline with fields
 * - `data-label-mode="hidden"`       → labels visually hidden (sr-only)
 */
function labelModeAttr(mode: 'stacked' | 'horizontal' | 'hidden'): string {
  return mode;
}

/* ------------------------------------------------------------------ */
/*  DynamicForm Component                                              */
/* ------------------------------------------------------------------ */

/**
 * Dynamic form builder component — drop-in replacement for the monolith's
 * `PcForm` ViewComponent.
 *
 * @example
 * ```tsx
 * <DynamicForm
 *   name="create-record"
 *   method="post"
 *   labelMode="horizontal"
 *   fieldRenderMode="form"
 *   validationErrors={errors}
 *   onSubmit={handleSubmit}
 * >
 *   <TextField name="title" label="Title" />
 *   <button type="submit">Save</button>
 * </DynamicForm>
 * ```
 */
export function DynamicForm({
  id: idProp,
  name = 'form',
  method = 'post',
  action,
  hookKey,
  labelMode = 'stacked',
  fieldRenderMode = 'form',
  className,
  showValidation = true,
  isVisible = true,
  validationErrors = [],
  validationMessage,
  children,
  onSubmit,
  isSubmitting = false,
}: DynamicFormProps): ReactElement | null {
  /*
   * All hooks MUST be called unconditionally (React Rules of Hooks).
   * The visibility gate is applied AFTER hooks are invoked.
   */

  /* ---- Auto-generate ID when not provided (PcForm.cs lines 92-95) ---- */
  const autoId = useId();
  const formId = idProp || `wv-${autoId}`;

  /* ---- Compute action URL with hookKey (PcForm.cs lines 145-158) ---- */
  const computedAction = useMemo(
    () => buildActionUrl(action, hookKey),
    [action, hookKey],
  );

  /* ---- Memoized submit handler ---- */
  const handleSubmit = useCallback(
    (event: FormEvent<HTMLFormElement>) => {
      if (onSubmit) {
        event.preventDefault();
        if (!isSubmitting) {
          onSubmit(event);
        }
      }
      /* When onSubmit is not provided the native form submission proceeds. */
    },
    [onSubmit, isSubmitting],
  );

  /* ---- Context value for child field components ---- */
  const contextValue = useMemo<FormContextValue>(
    () => ({
      labelMode,
      fieldRenderMode,
      validationErrors,
      isSubmitting,
    }),
    [labelMode, fieldRenderMode, validationErrors, isSubmitting],
  );

  /* ---- Visibility gate (PcForm.cs lines 124-137) ---- */
  if (isVisible === false) {
    return null;
  }

  /* ---- Determine if the validation summary should render ---- */
  const hasErrors = validationErrors.length > 0;
  const showSummary = showValidation && (hasErrors || validationMessage);

  return (
    <FormContext.Provider value={contextValue}>
      {/* Validation summary (mirrors Display.cshtml lines 17-20: <wv-validation>) */}
      {showSummary && (
        <div
          role="alert"
          aria-live="assertive"
          className="mb-4 rounded-md border border-red-300 bg-red-50 p-4"
        >
          {validationMessage && (
            <p className="font-medium text-red-800">{validationMessage}</p>
          )}
          {hasErrors && (
            <ul className={joinClassNames(
              'list-disc text-sm text-red-700',
              validationMessage ? 'mt-2 ps-5' : 'ps-5',
            )}>
              {validationErrors.map((err) => (
                <li key={`${err.key}-${err.message}`}>{err.message}</li>
              ))}
            </ul>
          )}
        </div>
      )}

      {/* Form element (mirrors Display.cshtml lines 21-36: <wv-form>) */}
      <form
        id={formId}
        name={name}
        method={method}
        action={computedAction}
        className={joinClassNames('space-y-4', className)}
        onSubmit={handleSubmit}
        noValidate
        aria-busy={isSubmitting || undefined}
        data-label-mode={labelModeAttr(labelMode)}
        data-field-render-mode={fieldRenderMode}
      >
        {children}
      </form>
    </FormContext.Provider>
  );
}
