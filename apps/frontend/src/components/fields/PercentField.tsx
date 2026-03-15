/**
 * PercentField — React replacement for the monolith's PcFieldPercent ViewComponent.
 *
 * Handles bidirectional decimal-to-percentage conversion:
 *   - **Storage format**: decimal (0.5 represents 50%)
 *   - **Display format**: percentage (50 shown in the input, "50.00%" in read-only)
 *
 * Conversion rules:
 *   Store  → Display : value × 100   (0.5 → 50)
 *   Input  → Store   : input ÷ 100   (50  → 0.5)
 *
 * The component supports three rendering modes controlled by the `mode` and
 * `access` props inherited from BaseFieldProps:
 *   - **Edit**      (mode='edit', access='full')  — number input with "%" suffix
 *   - **Display**   (mode='display' or access='readonly') — formatted "XX.XX%" text
 *   - **Forbidden** (access='forbidden') — access-denied message
 *
 * Locale-aware formatting is applied in display mode when a `locale` prop is
 * provided, falling back to `toFixed(decimalDigits)` when unavailable.
 *
 * @module PercentField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the PercentField component.
 *
 * Extends all shared field properties from `BaseFieldProps` while overriding
 * `value` and `onChange` with percent-specific types.  The `value` prop stores
 * the percentage as a decimal (e.g. 0.5 = 50 %).
 *
 * `min`, `max`, and `step` are expressed in **display percentage units** and
 * are applied directly to the underlying `<input type="number">` element.
 */
export interface PercentFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Stored as decimal — 0.5 represents 50 %. */
  value: number | null;
  /** Called with the stored decimal value (input ÷ 100). */
  onChange?: (value: number | null) => void;
  /** Decimal digits for formatted display (default: 2). */
  decimalDigits?: number;
  /** Minimum allowed display percentage (applied to input element). */
  min?: number | null;
  /** Maximum allowed display percentage (applied to input element). */
  max?: number | null;
  /** Step increment in display percentage units (applied to input element). */
  step?: number | null;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Safely converts a stored decimal to a display-percentage string suitable
 * for the number input's value attribute.  Rounds to `precision + 4` extra
 * digits to avoid IEEE-754 artifacts while preserving user intent during
 * active editing.
 */
function decimalToInputString(decimal: number | null | undefined, precision: number): string {
  if (decimal === null || decimal === undefined) {
    return '';
  }
  const percentage = decimal * 100;
  // Extra precision avoids premature rounding during editing
  const factor = Math.pow(10, precision + 4);
  const rounded = Math.round(percentage * factor) / factor;
  return String(rounded);
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * PercentField renders a percentage input (edit mode) or a formatted
 * percentage string (display / read-only mode).
 *
 * All values flow through the parent via the controlled `value` / `onChange`
 * contract.  A local string state is maintained for the `<input>` during
 * editing to preserve intermediate keystrokes (e.g. trailing decimals).
 * The local state is synchronised back to the canonical value on blur and
 * whenever the `value` prop changes from an external source.
 */
function PercentField(props: PercentFieldProps): React.JSX.Element {
  const {
    // Percent-specific props
    value,
    onChange,
    decimalDigits = 2,
    min = null,
    max = null,
    step = null,

    // BaseFieldProps members accessed (label and labelMode are inherited via the
    // Omit<BaseFieldProps, …> extension and consumed by FieldRenderer — they are
    // intentionally not destructured here to avoid unused-variable warnings).
    name,
    fieldId: fieldIdProp,
    mode = 'edit',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder,
    description,
    isVisible = true,
    accessDeniedMessage = 'access denied',
    emptyValueMessage = 'no data',
    locale,
  } = props;

  // -----------------------------------------------------------------------
  // Local input state — kept as a string to preserve intermediate typing
  // -----------------------------------------------------------------------

  const [inputValue, setInputValue] = useState<string>(
    () => decimalToInputString(value, decimalDigits),
  );

  // React-recommended pattern for synchronising derived state with a prop
  // without useEffect (avoids an extra render cycle).
  // See: https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes
  const [prevPropValue, setPrevPropValue] = useState<number | null>(value);
  if (!Object.is(value, prevPropValue)) {
    setPrevPropValue(value);
    setInputValue(decimalToInputString(value, decimalDigits));
  }

  // -----------------------------------------------------------------------
  // Callbacks
  // -----------------------------------------------------------------------

  /**
   * Handles every keystroke inside the number input.
   * Updates local string state immediately so the user sees what they type,
   * then converts the parsed number to a stored decimal and propagates it
   * to the parent via `onChange`.
   */
  const handleChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const raw = e.target.value;
      setInputValue(raw);

      if (raw.trim() === '') {
        onChange?.(null);
        return;
      }

      const parsed = parseFloat(raw);
      if (!Number.isNaN(parsed)) {
        // Convert display percentage → stored decimal
        onChange?.(parsed / 100);
      }
    },
    [onChange],
  );

  /**
   * On blur the input is reformatted to align with the canonical stored
   * value, cleaning up any intermediate or trailing-character artefacts.
   */
  const handleBlur = useCallback(() => {
    setInputValue(decimalToInputString(value, decimalDigits));
  }, [value, decimalDigits]);

  // -----------------------------------------------------------------------
  // Memoised display value for read-only / display mode
  // -----------------------------------------------------------------------

  /**
   * Formatted percentage string for display mode (e.g. "50.00 %").
   * Uses `Intl.NumberFormat` with `style: 'percent'` when a locale is
   * available — that formatter automatically multiplies decimals by 100.
   * Falls back to manual `toFixed` formatting otherwise.
   */
  const formattedValue = useMemo((): string => {
    if (value === null || value === undefined) {
      return '';
    }

    // Attempt locale-aware formatting when a locale is provided
    if (locale) {
      try {
        return new Intl.NumberFormat(locale, {
          style: 'percent',
          minimumFractionDigits: decimalDigits,
          maximumFractionDigits: decimalDigits,
        }).format(value); // Intl percent style multiplies by 100 internally
      } catch {
        // Invalid locale — fall through to manual formatting
      }
    }

    const percentage = value * 100;
    return `${percentage.toFixed(decimalDigits)}%`;
  }, [value, decimalDigits, locale]);

  // -----------------------------------------------------------------------
  // Derived identifiers
  // -----------------------------------------------------------------------

  const fieldId = fieldIdProp ?? `field-${name}`;
  const errorId = `${name}-error`;
  const descriptionId = description ? `${name}-description` : undefined;
  const hasError = Boolean(error);

  // -----------------------------------------------------------------------
  // Render: invisible
  // -----------------------------------------------------------------------

  if (!isVisible) {
    return <React.Fragment />;
  }

  // -----------------------------------------------------------------------
  // Render: access denied
  // -----------------------------------------------------------------------

  if (access === 'forbidden') {
    return (
      <div className={className}>
        <span className="text-sm text-gray-500 italic">
          {accessDeniedMessage}
        </span>
      </div>
    );
  }

  // -----------------------------------------------------------------------
  // Render: display / read-only mode
  // -----------------------------------------------------------------------

  if (mode === 'display' || access === 'readonly') {
    return (
      <div className={className}>
        {value !== null && value !== undefined ? (
          <span className="text-sm text-gray-900">{formattedValue}</span>
        ) : (
          <span className="text-sm text-gray-400 italic">
            {emptyValueMessage}
          </span>
        )}
      </div>
    );
  }

  // -----------------------------------------------------------------------
  // Render: edit mode — number input with "%" suffix
  // -----------------------------------------------------------------------

  const ariaDescribedByParts: string[] = [];
  if (hasError) ariaDescribedByParts.push(errorId);
  if (descriptionId) ariaDescribedByParts.push(descriptionId);

  return (
    <div className={className}>
      <div className="relative">
        <input
          type="number"
          id={fieldId}
          name={name}
          value={inputValue}
          onChange={handleChange}
          onBlur={handleBlur}
          placeholder={placeholder}
          disabled={disabled}
          required={required}
          min={min ?? undefined}
          max={max ?? undefined}
          step={step ?? undefined}
          aria-invalid={hasError || undefined}
          aria-describedby={
            ariaDescribedByParts.length > 0
              ? ariaDescribedByParts.join(' ')
              : undefined
          }
          className={[
            'block w-full rounded-md border px-3 py-2 pe-8 text-sm shadow-sm',
            'focus:outline-none focus:ring-1',
            hasError
              ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
              : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500',
            'disabled:bg-gray-100 disabled:text-gray-500 disabled:cursor-not-allowed',
            /* Hide native number-input spinner buttons for consistent UX */
            '[appearance:textfield]',
            '[&::-webkit-outer-spin-button]:appearance-none',
            '[&::-webkit-inner-spin-button]:appearance-none',
          ].join(' ')}
        />
        {/* Percent suffix indicator */}
        <span
          className="pointer-events-none absolute inset-y-0 end-0 flex items-center pe-3 text-sm text-gray-500"
          aria-hidden="true"
        >
          %
        </span>
      </div>

      {/* Error message */}
      {hasError && (
        <p id={errorId} className="mt-1 text-xs text-red-600" role="alert">
          {error}
        </p>
      )}

      {/* Help / description text */}
      {description && (
        <p id={descriptionId} className="mt-1 text-xs text-gray-500">
          {description}
        </p>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

export default PercentField;
