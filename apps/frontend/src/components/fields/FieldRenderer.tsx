/**
 * FieldRenderer — Dynamic Field Type Dispatch Component
 *
 * Central field rendering component that replaces the monolith's
 * reflection-based PcFieldBase field resolution pipeline.
 * Accepts a `fieldType` prop and renders the correct field component
 * via React.lazy() code-splitting.
 *
 * Centralizes shared field logic:
 *   - Mode switching (display / edit)
 *   - Label rendering (stacked / horizontal / inline / hidden)
 *   - Access control (full / readonly / forbidden)
 *   - Required validation indicator
 *   - Help text, warning text, error display
 *   - Validation error list rendering
 *
 * Source: WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import React, { lazy, Suspense, useMemo } from 'react';
import { FieldType } from '../../types/entity';

// ---------------------------------------------------------------------------
// Type Aliases — maps from C# enums
// ---------------------------------------------------------------------------

/**
 * Field access level — maps to monolith's WvFieldAccess enum.
 *
 *   - `'full'`      → WvFieldAccess.Full      — Full read/write access
 *   - `'readonly'`  → WvFieldAccess.ReadOnly   — Read-only (disabled input)
 *   - `'forbidden'` → WvFieldAccess.Forbidden  — No access (access-denied message)
 */
export type WvFieldAccess = 'full' | 'readonly' | 'forbidden';

/**
 * Label render mode — maps to monolith's WvLabelRenderMode enum.
 *
 *   - `'stacked'`    → Label above field, both full-width (default)
 *   - `'horizontal'` → Label and field side-by-side (3:9 grid)
 *   - `'inline'`     → Label inline before field (flex row)
 *   - `'hidden'`     → No label rendered
 */
export type WvLabelRenderMode = 'stacked' | 'horizontal' | 'inline' | 'hidden';

/**
 * Field component mode — maps to monolith's ComponentMode (Display / Form).
 *
 *   - `'display'` → Read-only presentation of the value
 *   - `'edit'`    → Editable input control
 */
export type FieldMode = 'display' | 'edit';

// ---------------------------------------------------------------------------
// Shared Field Props Interface
// ---------------------------------------------------------------------------

/**
 * Base props that ALL field components accept.
 *
 * Consolidated from PcFieldBaseOptions and PcFieldBaseModel in the monolith.
 * Every concrete field component (TextField, NumberField, etc.) extends or
 * uses `Omit<BaseFieldProps, 'value' | 'onChange'>` to override value/onChange
 * with field-specific types.
 */
export interface BaseFieldProps {
  /** Machine-readable field name (e.g. "first_name"). */
  name: string;

  /** Unique field identifier (GUID string). */
  fieldId?: string;

  /** Current field value — type varies by field type. */
  value: unknown;

  /** Callback invoked when the field value changes (edit mode). */
  onChange?: (value: unknown) => void;

  /** Default value used when `value` is null/undefined. */
  defaultValue?: unknown;

  /** Human-readable label displayed next to the field. */
  label?: string;

  /** Controls label positioning relative to the field. Defaults to 'stacked'. */
  labelMode?: WvLabelRenderMode;

  /** Help text shown as a tooltip or hint near the label. */
  labelHelpText?: string;

  /** Warning text displayed in amber/yellow near the label. */
  labelWarningText?: string;

  /** Error text displayed in red near the label. */
  labelErrorText?: string;

  /** Render mode: 'display' for read-only, 'edit' for input. Defaults to 'edit'. */
  mode?: FieldMode;

  /** Access level controlling interaction. Defaults to 'full'. */
  access?: WvFieldAccess;

  /** Whether a non-empty value is required. */
  required?: boolean;

  /** Whether the field is disabled (grayed out, no interaction). */
  disabled?: boolean;

  /** Single inline error message displayed below the field. */
  error?: string;

  /**
   * Array of validation errors from the backend.
   * Each item contains key, value, and message properties.
   */
  validationErrors?: Array<{ key: string; value: string; message: string }>;

  /** Initialization errors from the component bootstrap phase. */
  initErrors?: string[];

  /** Additional CSS class name(s) applied to the field wrapper. */
  className?: string;

  /** Placeholder text for empty input controls. */
  placeholder?: string;

  /** Long-form description displayed below the field. */
  description?: string;

  /** Controls visibility. When false, the field renders nothing. */
  isVisible?: boolean;

  /** Name of the entity this field belongs to. */
  entityName?: string;

  /** Record identifier for API integration (GUID string). */
  recordId?: string;

  /** Base API URL for AJAX operations (e.g. "/api/v3/en_US/record/{entity}/{id}/"). */
  apiUrl?: string;

  /** Message displayed when access is 'forbidden'. Defaults to "access denied". */
  accessDeniedMessage?: string;

  /** Message displayed when value is empty in display mode. Defaults to "no data". */
  emptyValueMessage?: string;

  /** Locale identifier for formatting (e.g. "en_US"). */
  locale?: string;

  /** Connected entity GUID for relation fields. */
  connectedEntityId?: string;

  /** Datasource expression for the connected record ID. */
  connectedRecordIdDs?: string;
}

// ---------------------------------------------------------------------------
// FieldRenderer-Specific Props
// ---------------------------------------------------------------------------

/**
 * Props for the FieldRenderer component.
 * Extends BaseFieldProps with a `fieldType` discriminator that determines
 * which concrete field component to render.
 */
export interface FieldRendererProps extends BaseFieldProps {
  /** FieldType enum value (1–21) that selects the concrete field component. */
  fieldType: FieldType;
}

// ---------------------------------------------------------------------------
// Lazy-loaded Field Component Map
// ---------------------------------------------------------------------------

/**
 * Maps each FieldType enum value to a lazy-loaded React component.
 *
 * Code-splitting ensures only the field components actually used on a page
 * are downloaded. Each import path resolves to a sibling file under
 * `apps/frontend/src/components/fields/`.
 *
 * GeographyField (21) renders as TextField since geography data is displayed
 * as text in the UI layer.
 */
const FIELD_COMPONENT_MAP: Record<
  number,
  React.LazyExoticComponent<React.ComponentType<BaseFieldProps>>
> = {
  [FieldType.AutoNumberField as number]: lazy(
    () => import('./AutonumberField')
  ),
  [FieldType.CheckboxField as number]: lazy(
    () => import('./CheckboxField')
  ),
  [FieldType.CurrencyField as number]: lazy(
    () => import('./CurrencyField')
  ),
  [FieldType.DateField as number]: lazy(
    () => import('./DateField')
  ),
  [FieldType.DateTimeField as number]: lazy(
    () => import('./DateTimeField')
  ),
  [FieldType.EmailField as number]: lazy(
    () => import('./EmailField')
  ),
  [FieldType.FileField as number]: lazy(
    () => import('./FileField')
  ),
  [FieldType.HtmlField as number]: lazy(
    () => import('./HtmlField')
  ),
  [FieldType.ImageField as number]: lazy(
    () => import('./ImageField')
  ),
  [FieldType.MultiLineTextField as number]: lazy(
    () => import('./TextareaField')
  ),
  [FieldType.MultiSelectField as number]: lazy(
    () => import('./MultiSelectField')
  ),
  [FieldType.NumberField as number]: lazy(
    () => import('./NumberField')
  ),
  [FieldType.PasswordField as number]: lazy(
    () => import('./PasswordField')
  ),
  [FieldType.PercentField as number]: lazy(
    () => import('./PercentField')
  ),
  [FieldType.PhoneField as number]: lazy(
    () => import('./PhoneField')
  ),
  [FieldType.GuidField as number]: lazy(
    () => import('./GuidField')
  ),
  [FieldType.SelectField as number]: lazy(
    () => import('./SelectField')
  ),
  [FieldType.TextField as number]: lazy(
    () => import('./TextField')
  ),
  [FieldType.UrlField as number]: lazy(
    () => import('./UrlField')
  ),
  /* GeographyField renders as TextField — geography data is displayed as text */
  [FieldType.GeographyField as number]: lazy(
    () => import('./TextField')
  ),
};

// ---------------------------------------------------------------------------
// Loading Fallback
// ---------------------------------------------------------------------------

/**
 * Minimal loading indicator shown while a lazy-loaded field component chunk
 * is being fetched. Renders as a pulsing placeholder bar matching typical
 * field height.
 */
function FieldLoadingFallback(): React.JSX.Element {
  return (
    <div
      className="animate-pulse rounded bg-gray-200 h-10 w-full"
      role="status"
      aria-label="Loading field"
    >
      <span className="sr-only">Loading field…</span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// FieldLabel Helper
// ---------------------------------------------------------------------------

/** Props for the internal FieldLabel helper component. */
interface FieldLabelProps {
  /** Label text content. */
  text?: string;
  /** Whether the field is required — renders a red asterisk. */
  required?: boolean;
  /** Help text rendered as a tooltip-style hint. */
  helpText?: string;
  /** Warning text rendered in amber below the label. */
  warningText?: string;
  /** Error text rendered in red below the label. */
  errorText?: string;
  /** HTML `for` attribute linking the label to a form control. */
  htmlFor?: string;
}

/**
 * Renders a field label with optional required indicator, help text,
 * warning text, and error text.
 *
 * Mirrors the label rendering logic from PcFieldBase's Razor view templates
 * which compose labels from LabelText, LabelHelpText, LabelWarningText,
 * and LabelErrorText properties.
 */
function FieldLabel({
  text,
  required,
  helpText,
  warningText,
  errorText,
  htmlFor,
}: FieldLabelProps): React.JSX.Element | null {
  if (!text) {
    return null;
  }

  return (
    <div className="flex flex-col gap-0.5">
      <label
        htmlFor={htmlFor}
        className="text-sm font-medium text-gray-700 flex items-center gap-1"
      >
        <span>{text}</span>
        {required && (
          <span className="text-red-500" aria-hidden="true">
            *
          </span>
        )}
        {helpText && (
          <span
            className="text-gray-400 cursor-help"
            title={helpText}
            aria-label={helpText}
          >
            <svg
              className="inline-block h-4 w-4"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-7-4a1 1 0 11-2 0 1 1 0 012 0zM9 9a.75.75 0 000 1.5h.253a.25.25 0 01.244.304l-.459 2.066A1.75 1.75 0 0010.747 15H11a.75.75 0 000-1.5h-.253a.25.25 0 01-.244-.304l.459-2.066A1.75 1.75 0 009.253 9H9z"
                clipRule="evenodd"
              />
            </svg>
          </span>
        )}
      </label>
      {warningText && (
        <span className="text-xs text-amber-600" role="alert">
          {warningText}
        </span>
      )}
      {errorText && (
        <span className="text-xs text-red-600" role="alert">
          {errorText}
        </span>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Access Denied Fallback
// ---------------------------------------------------------------------------

/**
 * Renders the access-denied state when WvFieldAccess is 'forbidden'.
 * Displays a lock icon and the configurable access-denied message.
 */
function AccessDeniedMessage({
  message,
}: {
  message: string;
}): React.JSX.Element {
  return (
    <div
      className="flex items-center gap-2 rounded border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-400"
      role="status"
      aria-label="Access denied"
    >
      <svg
        className="h-4 w-4 shrink-0"
        viewBox="0 0 20 20"
        fill="currentColor"
        aria-hidden="true"
      >
        <path
          fillRule="evenodd"
          d="M10 1a4.5 4.5 0 00-4.5 4.5V9H5a2 2 0 00-2 2v6a2 2 0 002 2h10a2 2 0 002-2v-6a2 2 0 00-2-2h-.5V5.5A4.5 4.5 0 0010 1zm3 8V5.5a3 3 0 10-6 0V9h6z"
          clipRule="evenodd"
        />
      </svg>
      <span>{message}</span>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Unknown Field Type Fallback
// ---------------------------------------------------------------------------

/**
 * Renders a warning when the provided fieldType has no matching component
 * in FIELD_COMPONENT_MAP.
 */
function UnknownFieldType({
  fieldType,
  name,
}: {
  fieldType: number;
  name: string;
}): React.JSX.Element {
  return (
    <div
      className="rounded border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-700"
      role="alert"
    >
      <span className="font-medium">Unknown field type</span>
      {': '}
      field &quot;{name}&quot; has unsupported type ({fieldType}).
    </div>
  );
}

// ---------------------------------------------------------------------------
// Error Display Helpers
// ---------------------------------------------------------------------------

/**
 * Renders the combined error and validation feedback section below a field.
 * Handles single error string, validationErrors array, and initErrors array.
 */
function FieldErrors({
  error,
  validationErrors,
  initErrors,
  fieldName,
}: {
  error?: string;
  validationErrors?: Array<{ key: string; value: string; message: string }>;
  initErrors?: string[];
  fieldName: string;
}): React.JSX.Element | null {
  const hasError = Boolean(error);
  const hasValidation = validationErrors && validationErrors.length > 0;
  const hasInit = initErrors && initErrors.length > 0;

  if (!hasError && !hasValidation && !hasInit) {
    return null;
  }

  return (
    <div className="flex flex-col gap-0.5 mt-1" role="alert" aria-live="polite">
      {hasError && (
        <p className="text-sm text-red-600" id={`${fieldName}-error`}>
          {error}
        </p>
      )}
      {hasValidation &&
        validationErrors!.map((ve, idx) => (
          <p
            key={`${ve.key}-${idx}`}
            className="text-sm text-red-600"
          >
            {ve.message}
          </p>
        ))}
      {hasInit &&
        initErrors!.map((initErr, idx) => (
          <p
            key={`init-${idx}`}
            className="text-sm text-amber-600"
          >
            {initErr}
          </p>
        ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// FieldRenderer Component
// ---------------------------------------------------------------------------

/**
 * Dynamic field type dispatch component.
 *
 * Replaces the monolith's PcFieldBase reflection-based resolution pipeline:
 *   1. Checks visibility — returns null when `isVisible === false`
 *   2. Checks access — renders access-denied message for 'forbidden'
 *   3. Resolves the concrete field component via FIELD_COMPONENT_MAP
 *   4. Wraps in Suspense for lazy-loading
 *   5. Renders label (stacked / horizontal / inline / hidden)
 *   6. Renders error / validation messages
 *   7. Renders description text
 *
 * @param props — FieldRendererProps (BaseFieldProps + fieldType)
 * @returns The rendered field with label, errors, and description
 */
function FieldRenderer(props: FieldRendererProps): React.JSX.Element | null {
  const {
    fieldType,
    name,
    fieldId,
    value,
    onChange,
    defaultValue,
    label,
    labelMode = 'stacked',
    labelHelpText,
    labelWarningText,
    labelErrorText,
    mode = 'edit',
    access = 'full',
    required = false,
    disabled = false,
    error,
    validationErrors,
    initErrors,
    className,
    placeholder,
    description,
    isVisible = true,
    entityName,
    recordId,
    apiUrl,
    accessDeniedMessage = 'access denied',
    emptyValueMessage = 'no data',
    locale,
    connectedEntityId,
    connectedRecordIdDs,
  } = props;

  // Phase 1: Visibility check — if invisible, render nothing
  if (!isVisible) {
    return null;
  }

  // Phase 2: Resolve the concrete field component using memoization.
  // Because FieldType is a const enum, values are inlined as numbers at
  // compile time, so using numeric keys in the map is correct.
  const FieldComponent = useMemo(
    () => FIELD_COMPONENT_MAP[fieldType as number] ?? null,
    [fieldType]
  );

  // Phase 3: Access control — forbidden renders access-denied message
  if (access === 'forbidden') {
    return (
      <div className={className}>
        <AccessDeniedMessage message={accessDeniedMessage} />
      </div>
    );
  }

  // Compute effective disabled state: readonly access forces disabled
  const effectiveDisabled = disabled || access === 'readonly';

  // Compute effective mode: readonly access forces display mode
  const effectiveMode: FieldMode =
    access === 'readonly' ? 'display' : mode;

  // Assemble props to pass through to the concrete field component
  const fieldProps: BaseFieldProps = {
    name,
    fieldId,
    value,
    onChange,
    defaultValue,
    label,
    labelMode,
    labelHelpText,
    labelWarningText,
    labelErrorText,
    mode: effectiveMode,
    access,
    required,
    disabled: effectiveDisabled,
    error,
    validationErrors,
    initErrors,
    className: undefined, // className applied to wrapper, not inner field
    placeholder,
    description,
    isVisible,
    entityName,
    recordId,
    apiUrl,
    accessDeniedMessage,
    emptyValueMessage,
    locale,
    connectedEntityId,
    connectedRecordIdDs,
  };

  // Build a unique field ID for label-input association
  const controlId = fieldId ?? `field-${name}`;

  // Phase 4: Render unknown field type fallback
  if (!FieldComponent) {
    return (
      <div className={className}>
        <UnknownFieldType fieldType={fieldType as number} name={name} />
      </div>
    );
  }

  // Phase 5: Build the rendered field element (wrapped in Suspense)
  const renderedField = (
    <Suspense fallback={<FieldLoadingFallback />}>
      <FieldComponent {...fieldProps} />
    </Suspense>
  );

  // Phase 6: Render error/validation messages
  const renderedErrors = (
    <FieldErrors
      error={error}
      validationErrors={validationErrors}
      initErrors={initErrors}
      fieldName={name}
    />
  );

  // Phase 7: Render description text
  const renderedDescription = description ? (
    <p className="text-sm text-gray-500 mt-1">{description}</p>
  ) : null;

  // Phase 8: Label rendering based on labelMode

  // Hidden label mode — field only, no label
  if (labelMode === 'hidden') {
    return (
      <div className={className}>
        {renderedField}
        {renderedErrors}
        {renderedDescription}
      </div>
    );
  }

  // Build the label element
  const renderedLabel = (
    <FieldLabel
      text={label}
      required={required}
      helpText={labelHelpText}
      warningText={labelWarningText}
      errorText={labelErrorText}
      htmlFor={controlId}
    />
  );

  // Stacked layout (default): label above field, both full-width
  if (labelMode === 'stacked') {
    return (
      <div className={`flex flex-col gap-1 ${className ?? ''}`}>
        {renderedLabel}
        {renderedField}
        {renderedErrors}
        {renderedDescription}
      </div>
    );
  }

  // Horizontal layout: label and field side-by-side (3:9 grid)
  if (labelMode === 'horizontal') {
    return (
      <div className={`grid grid-cols-12 gap-2 items-start ${className ?? ''}`}>
        <div className="col-span-3">
          {renderedLabel}
        </div>
        <div className="col-span-9">
          {renderedField}
          {renderedErrors}
          {renderedDescription}
        </div>
      </div>
    );
  }

  // Inline layout: label inline before field
  if (labelMode === 'inline') {
    return (
      <div className={`flex items-center gap-2 ${className ?? ''}`}>
        {renderedLabel}
        <div className="flex-1 min-w-0">
          {renderedField}
          {renderedErrors}
          {renderedDescription}
        </div>
      </div>
    );
  }

  // Fallback: stacked (should never reach here due to type constraints)
  return (
    <div className={`flex flex-col gap-1 ${className ?? ''}`}>
      {renderedLabel}
      {renderedField}
      {renderedErrors}
      {renderedDescription}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default FieldRenderer;
