/**
 * NumberField — Numeric input field component.
 *
 * React replacement for the monolith's PcFieldNumber ViewComponent.
 * Supports edit mode with native HTML number input (min/max/step constraints)
 * and display mode with locale-aware decimal formatting via toLocaleString().
 *
 * This component renders ONLY the input/display content. Label rendering,
 * error display, description, access control, and visibility are handled
 * by the parent FieldRenderer component.
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props interface — exported as named export per schema
// ---------------------------------------------------------------------------

/**
 * Props for the NumberField component.
 *
 * Extends BaseFieldProps (excluding value/onChange which are overridden
 * with number-specific types) and adds numeric-specific configuration:
 * min/max range, step increment, and decimal display precision.
 */
export interface NumberFieldProps
  extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current numeric value, or null when empty/unset. */
  value: number | null;

  /** Callback invoked when the user changes the value. Receives the parsed
   *  number or null when the input is cleared. */
  onChange?: (value: number | null) => void;

  /** Minimum allowed value for the number input (maps to HTML min attribute).
   *  Null means no minimum constraint. */
  min?: number | null;

  /** Maximum allowed value for the number input (maps to HTML max attribute).
   *  Null means no maximum constraint. */
  max?: number | null;

  /** Step increment for the number input (maps to HTML step attribute).
   *  Null uses the browser default (typically 1). */
  step?: number | null;

  /** Number of decimal places for display formatting.
   *  Default is 2, matching the monolith's PcFieldBaseOptions.DecimalDigits. */
  decimalDigits?: number;
}

// ---------------------------------------------------------------------------
// Shared Tailwind class constants
// ---------------------------------------------------------------------------

/** Base input styling — matches the TextField input pattern for visual consistency. */
const INPUT_BASE_CLASSES =
  'block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm ' +
  'focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 ' +
  'disabled:bg-gray-100 disabled:text-gray-500';

/** Error-state input border override. */
const INPUT_ERROR_CLASSES =
  'border-red-500 focus:border-red-500 focus:ring-red-500';

/** Display mode text styling. */
const DISPLAY_TEXT_CLASSES = 'text-sm text-gray-900';

/** Empty-value message styling. */
const DISPLAY_EMPTY_CLASSES = 'text-sm italic text-gray-400';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * NumberField renders a numeric input in edit mode and a locale-formatted
 * number string in display mode.
 *
 * Edit mode uses a native `<input type="number">` with optional min/max/step
 * attributes. The onChange handler parses the string value from the native
 * input event into a `number | null` before propagating to the parent.
 *
 * Display mode formats the value using `Number.toLocaleString()` with the
 * configured `decimalDigits` (defaulting to 2) for consistent decimal
 * precision. When value is null, it shows the `emptyValueMessage`.
 */
function NumberField(props: NumberFieldProps): React.JSX.Element {
  const {
    // Identity
    name,
    // Value
    value,
    onChange,
    // Numeric constraints
    min = null,
    max = null,
    step = null,
    decimalDigits = 2,
    // Field chrome (used for aria/id generation)
    label,
    labelMode,
    mode = 'edit',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder,
    description,
    isVisible = true,
    // Messages
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    // Locale
    locale,
  } = props;

  // ── Internal state ──────────────────────────────────────────────────────
  // Track the raw string representation of the input so that the user can
  // type freely (e.g. clear the field entirely) without the controlled
  // component snapping back to "0".
  const [inputValue, setInputValue] = useState<string>(
    value !== null && value !== undefined ? String(value) : '',
  );

  // Keep internal state synchronised when the external value prop changes
  // (e.g. form reset, server response).
  const derivedInputValue = useMemo(() => {
    if (value === null || value === undefined) return '';
    return String(value);
  }, [value]);

  // Use derived value when it diverges from the controlled state (parent
  // updated externally), otherwise keep local edits.
  const effectiveInputValue = useMemo(() => {
    // If the parsed local input matches the prop value, keep local string
    // to avoid reformatting while the user is typing.
    if (inputValue === '' && value === null) return '';
    const parsed = inputValue === '' ? null : Number(inputValue);
    if (parsed === value) return inputValue;
    return derivedInputValue;
  }, [inputValue, value, derivedInputValue]);

  // ── Handlers ────────────────────────────────────────────────────────────

  /**
   * Handle native input change events. Parses the string to a number,
   * emitting null for empty strings and NaN results.
   */
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      const raw = event.target.value;
      setInputValue(raw);

      if (raw === '' || raw === '-') {
        onChange?.(null);
        return;
      }

      const parsed = Number(raw);
      if (Number.isNaN(parsed)) {
        onChange?.(null);
        return;
      }

      onChange?.(parsed);
    },
    [onChange],
  );

  // ── Display formatting ──────────────────────────────────────────────────

  /**
   * Formatted display string using toLocaleString with the configured
   * decimal digit precision. Falls back to emptyValueMessage for null.
   */
  const formattedDisplayValue = useMemo(() => {
    if (value === null || value === undefined) {
      return null; // signal to render empty message
    }

    try {
      return value.toLocaleString(locale || undefined, {
        minimumFractionDigits: decimalDigits,
        maximumFractionDigits: decimalDigits,
      });
    } catch {
      // Gracefully handle invalid locale strings by falling back to
      // default locale formatting.
      return value.toLocaleString(undefined, {
        minimumFractionDigits: decimalDigits,
        maximumFractionDigits: decimalDigits,
      });
    }
  }, [value, locale, decimalDigits]);

  // ── Derived aria/id values ──────────────────────────────────────────────

  const controlId = `field-${name}`;

  const hasError = Boolean(error);
  const errorId = hasError ? `${name}-error` : undefined;

  // ── Render: Display mode ────────────────────────────────────────────────

  if (mode === 'display') {
    if (formattedDisplayValue === null) {
      return (
        <span className={`${DISPLAY_EMPTY_CLASSES}${className ? ` ${className}` : ''}`}>
          {emptyValueMessage}
        </span>
      );
    }

    return (
      <span className={`${DISPLAY_TEXT_CLASSES}${className ? ` ${className}` : ''}`}>
        {formattedDisplayValue}
      </span>
    );
  }

  // ── Render: Edit mode ───────────────────────────────────────────────────

  const inputClasses = [
    INPUT_BASE_CLASSES,
    hasError ? INPUT_ERROR_CLASSES : '',
    className ?? '',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <input
      type="number"
      id={controlId}
      name={name}
      value={effectiveInputValue}
      onChange={handleChange}
      placeholder={placeholder}
      disabled={disabled}
      required={required}
      aria-invalid={hasError || undefined}
      aria-describedby={errorId}
      {...(min !== null && min !== undefined ? { min } : {})}
      {...(max !== null && max !== undefined ? { max } : {})}
      {...(step !== null && step !== undefined ? { step } : {})}
      className={inputClasses}
    />
  );
}

export default NumberField;
