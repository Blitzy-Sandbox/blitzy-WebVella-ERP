/**
 * TextareaField — Multi-Line Text Input Component
 *
 * React replacement for the monolith's PcFieldTextarea ViewComponent.
 * Renders a multi-line text input (<textarea>) in edit mode with configurable
 * rows, maxLength, and resize behavior. In display mode, preserves whitespace
 * formatting with pre-wrap rendering.
 *
 * Source: WebVella.Erp.Web/Components/PcFieldTextarea/PcFieldTextarea.cs
 * Source: WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import React, { useState, useCallback } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the TextareaField component.
 *
 * Extends BaseFieldProps (overriding value/onChange with string-specific types)
 * and adds textarea-specific configuration: maxLength and visible rows.
 *
 * Maps from monolith's PcFieldTextarea options:
 *   - PcFieldBaseOptions.MaxLength → maxLength
 *   - PcFieldTextarea.Height (visibleLineNumber) → rows
 */
export interface TextareaFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current textarea value. Null represents an empty/unset field. */
  value: string | null;

  /** Callback invoked when textarea content changes (edit mode only). */
  onChange?: (value: string) => void;

  /** Maximum character count. Null or undefined means unlimited. */
  maxLength?: number | null;

  /**
   * Number of visible text rows. Controls the initial height of the textarea.
   * Maps from monolith's MultiLineTextField.visibleLineNumber.
   * @default 4
   */
  rows?: number;
}

// ---------------------------------------------------------------------------
// TextareaField Component
// ---------------------------------------------------------------------------

/**
 * Multi-line text input field component.
 *
 * Renders either an editable <textarea> (edit mode) or a whitespace-preserving
 * display view (display mode). Supports controlled input via value/onChange,
 * character limits via maxLength, and configurable visible rows.
 *
 * When used within FieldRenderer, the wrapper handles:
 *   - Visibility (isVisible)
 *   - Access forbidden state (accessDeniedMessage)
 *   - Label rendering (labelMode)
 *   - Error/validation display
 *   - Description text
 *
 * When used standalone, the component handles all concerns defensively.
 *
 * @param props - TextareaFieldProps
 * @returns Rendered textarea element or display text, or null if invisible
 */
function TextareaField(props: TextareaFieldProps): React.JSX.Element | null {
  const {
    // BaseFieldProps members — all 15 members_accessed per schema
    name,
    label,
    labelMode = 'stacked',
    mode = 'edit',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder,
    description,
    isVisible = true,
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    locale,

    // TextareaField-specific props (members_exposed)
    value,
    onChange,
    maxLength,
    rows = 4,

    // Additional BaseFieldProps used internally
    fieldId,
  } = props;

  // -------------------------------------------------------------------------
  // Internal State — local value buffer for controlled textarea behavior
  // -------------------------------------------------------------------------

  /**
   * Local value state maintains the textarea content independently of the
   * parent's value prop. This enables uncontrolled-fallback behavior when
   * the parent does not respond to onChange by updating value.
   */
  const [localValue, setLocalValue] = useState<string>(value ?? '');

  // -------------------------------------------------------------------------
  // Event Handler — memoized onChange that propagates string value to parent
  // -------------------------------------------------------------------------

  /**
   * Handles textarea input changes:
   *   1. Updates local state for immediate UI feedback
   *   2. Propagates the raw string value to parent via onChange callback
   */
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLTextAreaElement>): void => {
      const newValue = event.target.value;
      setLocalValue(newValue);
      onChange?.(newValue);
    },
    [onChange],
  );

  // -------------------------------------------------------------------------
  // Visibility Guard
  // -------------------------------------------------------------------------

  // When isVisible is false, render nothing. FieldRenderer also checks this,
  // but we handle it defensively for standalone usage.
  if (!isVisible) {
    return null;
  }

  // -------------------------------------------------------------------------
  // Access Control — Forbidden State
  // -------------------------------------------------------------------------

  // When access is forbidden, show the access denied message.
  // FieldRenderer handles this at the wrapper level, but we handle it
  // defensively for standalone usage.
  if (access === 'forbidden') {
    return (
      <div
        className={`text-sm text-gray-400 italic${className ? ` ${className}` : ''}`}
        role="status"
        aria-label={accessDeniedMessage}
        data-field-name={name}
        lang={locale}
      >
        {accessDeniedMessage}
      </div>
    );
  }

  // -------------------------------------------------------------------------
  // Computed Values
  // -------------------------------------------------------------------------

  // Unique control ID for label-input association
  const controlId = fieldId ?? `field-${name}`;

  // Determine if the textarea should be rendered as disabled.
  // FieldRenderer already sets disabled=true for readonly access, but we
  // handle it defensively for standalone usage.
  const isEffectivelyDisabled = disabled || access === 'readonly';

  // Effective textarea value: prefer prop value (controlled mode),
  // fall back to local state (uncontrolled mode)
  const effectiveValue = value !== null && value !== undefined ? value : localValue;

  // Compute maxLength as a positive number or undefined (strip null/zero/negative)
  const effectiveMaxLength =
    maxLength !== null && maxLength !== undefined && maxLength > 0
      ? maxLength
      : undefined;

  // -------------------------------------------------------------------------
  // Display Mode Rendering
  // -------------------------------------------------------------------------

  if (mode === 'display') {
    const hasValue = value !== null && value !== undefined && value !== '';
    const displayText = hasValue ? value : emptyValueMessage;

    return (
      <div
        className={[
          'text-sm',
          hasValue ? 'text-gray-900 whitespace-pre-wrap' : 'text-gray-500 italic',
          className ?? '',
        ]
          .filter(Boolean)
          .join(' ')}
        data-field-name={name}
        role="textbox"
        aria-readonly="true"
        aria-label={labelMode === 'hidden' && label ? label : undefined}
        aria-describedby={description ? `${controlId}-description` : undefined}
        lang={locale}
      >
        {displayText}
      </div>
    );
  }

  // -------------------------------------------------------------------------
  // Edit Mode Rendering
  // -------------------------------------------------------------------------

  // Build aria-describedby from available description sources
  const describedByIds = [
    error ? `${name}-error` : null,
    description ? `${controlId}-description` : null,
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <textarea
      id={controlId}
      name={name}
      value={effectiveValue}
      onChange={handleChange}
      placeholder={placeholder}
      disabled={isEffectivelyDisabled}
      required={required}
      rows={rows}
      maxLength={effectiveMaxLength}
      className={[
        'block w-full rounded-md border px-3 py-2 text-sm shadow-sm resize-y',
        'focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500',
        'disabled:bg-gray-100 disabled:text-gray-500 disabled:cursor-not-allowed',
        error ? 'border-red-500' : 'border-gray-300',
        className ?? '',
      ]
        .filter(Boolean)
        .join(' ')}
      aria-invalid={error ? true : undefined}
      aria-describedby={describedByIds || undefined}
      aria-required={required || undefined}
      aria-label={labelMode === 'hidden' && label ? label : undefined}
      lang={locale}
    />
  );
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default TextareaField;
