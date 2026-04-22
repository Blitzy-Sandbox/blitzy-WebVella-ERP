/**
 * CurrencyField — React replacement for the monolith's PcFieldCurrency ViewComponent.
 *
 * Renders a numeric input with a locale-derived currency symbol prefix (edit
 * mode) or a fully formatted currency string (display / read-only mode).
 *
 * Key behaviours:
 *   - **Edit mode** (mode='edit', access='full') — number input with a
 *     currency-symbol prefix derived from `currencyCode` via
 *     `Intl.NumberFormat.formatToParts()`. No hard-coded symbol map.
 *   - **Display mode** (mode='display' or access='readonly') — formatted
 *     currency string produced by
 *     `new Intl.NumberFormat(locale, { style: 'currency', currency })`.
 *     Falls back to `toFixed(decimalDigits)` when the formatter cannot run.
 *   - **Forbidden** (access='forbidden') — access-denied message.
 *
 * The component stores and exposes the raw numeric value — no unit conversion
 * is performed (unlike PercentField which converts between decimal and
 * percentage). `value` is always the actual currency amount.
 *
 * Default configuration mirrors the source monolith's `PcFieldBaseOptions`:
 *   - currencyCode = "USD"
 *   - decimalDigits = 2
 *   - min, max, step = null
 *
 * @module CurrencyField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the CurrencyField component.
 *
 * Extends all shared field properties from `BaseFieldProps` while overriding
 * `value` and `onChange` with currency-specific types. The `value` prop stores
 * the raw monetary amount (e.g. 99.95 represents $99.95 when currencyCode is
 * "USD").
 */
export interface CurrencyFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Raw monetary amount. `null` represents an empty / unset field. */
  value: number | null;
  /** Called with the parsed numeric value, or `null` when the input is cleared. */
  onChange?: (value: number | null) => void;
  /** ISO 4217 currency code used for symbol derivation and formatting (default: "USD"). */
  currencyCode?: string;
  /** Number of decimal digits for display formatting (default: 2). */
  decimalDigits?: number;
  /** Minimum allowed value (applied to the underlying `<input>` element). */
  min?: number | null;
  /** Maximum allowed value (applied to the underlying `<input>` element). */
  max?: number | null;
  /** Step increment (applied to the underlying `<input>` element). */
  step?: number | null;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Converts a numeric value to a string suitable for the `<input>` element's
 * value attribute. Rounds to `precision + 4` extra digits to avoid IEEE-754
 * floating-point artefacts while preserving user intent during active editing.
 */
function valueToInputString(val: number | null | undefined, precision: number): string {
  if (val === null || val === undefined) {
    return '';
  }
  const factor = Math.pow(10, precision + 4);
  const rounded = Math.round(val * factor) / factor;
  return String(rounded);
}

/**
 * Extracts the currency symbol from an ISO 4217 currency code using
 * `Intl.NumberFormat.formatToParts()`. Falls back to the raw code string when
 * the Intl API is unavailable or the code is unrecognised.
 *
 * Uses `currencyDisplay: 'narrowSymbol'` for compact rendering (e.g. "$"
 * instead of "US$" for USD).
 */
function deriveCurrencySymbol(currencyCode: string, locale: string): string {
  try {
    const parts = new Intl.NumberFormat(locale, {
      style: 'currency',
      currency: currencyCode,
      currencyDisplay: 'narrowSymbol',
    }).formatToParts(0);
    const symbolPart = parts.find((p) => p.type === 'currency');
    return symbolPart?.value ?? currencyCode;
  } catch {
    // Invalid currency code or missing Intl support — return the raw code
    return currencyCode;
  }
}

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * CurrencyField renders a currency input (edit mode) or a formatted currency
 * string (display / read-only mode).
 *
 * All values flow through the parent via the controlled `value` / `onChange`
 * contract. A local string state is maintained for the `<input>` during
 * editing to preserve intermediate keystrokes (e.g. trailing decimals).
 * The local state is synchronised back to the canonical value on blur and
 * whenever the `value` prop changes from an external source.
 */
function CurrencyField(props: CurrencyFieldProps): React.JSX.Element {
  const {
    // Currency-specific props
    value,
    onChange,
    currencyCode = 'USD',
    decimalDigits = 2,
    min = null,
    max = null,
    step = null,

    // BaseFieldProps members accessed (label and labelMode are inherited via
    // the Omit<BaseFieldProps, …> extension and consumed by FieldRenderer —
    // they are intentionally not destructured here to avoid unused-variable
    // warnings).
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
  // Memoised currency symbol
  // -----------------------------------------------------------------------

  /**
   * Currency symbol derived from `currencyCode` using Intl.NumberFormat.
   * Recomputed only when `currencyCode` or `locale` changes.
   */
  const currencySymbol = useMemo(
    () => deriveCurrencySymbol(currencyCode, locale ?? 'en-US'),
    [currencyCode, locale],
  );

  // -----------------------------------------------------------------------
  // Local input state — kept as a string to preserve intermediate typing
  // -----------------------------------------------------------------------

  const [inputValue, setInputValue] = useState<string>(
    () => valueToInputString(value, decimalDigits),
  );

  // React-recommended pattern for synchronising derived state with a prop
  // without useEffect (avoids an extra render cycle).
  // See: https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes
  const [prevPropValue, setPrevPropValue] = useState<number | null>(value);
  if (!Object.is(value, prevPropValue)) {
    setPrevPropValue(value);
    setInputValue(valueToInputString(value, decimalDigits));
  }

  // -----------------------------------------------------------------------
  // Callbacks
  // -----------------------------------------------------------------------

  /**
   * Handles every keystroke inside the number input. Updates local string
   * state immediately so the user sees what they type, then parses the
   * numeric value and propagates it to the parent via `onChange`.
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
        onChange?.(parsed);
      }
    },
    [onChange],
  );

  /**
   * On blur the input is reformatted to align with the canonical stored
   * value, cleaning up any intermediate or trailing-character artefacts.
   */
  const handleBlur = useCallback(() => {
    setInputValue(valueToInputString(value, decimalDigits));
  }, [value, decimalDigits]);

  // -----------------------------------------------------------------------
  // Memoised display value for read-only / display mode
  // -----------------------------------------------------------------------

  /**
   * Formatted currency string for display mode (e.g. "$1,234.56").
   * Uses `Intl.NumberFormat` with `style: 'currency'` for full locale-aware
   * formatting. Falls back to manual `toFixed` formatting when the Intl API
   * encounters an error.
   */
  const formattedValue = useMemo((): string => {
    if (value === null || value === undefined) {
      return '';
    }

    // Attempt locale-aware currency formatting
    const resolvedLocale = locale ?? 'en-US';
    try {
      return new Intl.NumberFormat(resolvedLocale, {
        style: 'currency',
        currency: currencyCode,
        minimumFractionDigits: decimalDigits,
        maximumFractionDigits: decimalDigits,
      }).format(value);
    } catch {
      // Invalid currency code or missing Intl support — fall through to manual
    }

    // Fallback: symbol + fixed-decimal number
    return `${currencyCode} ${value.toFixed(decimalDigits)}`;
  }, [value, locale, currencyCode, decimalDigits]);

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
  // Render: edit mode — number input with currency symbol prefix
  // -----------------------------------------------------------------------

  const ariaDescribedByParts: string[] = [];
  if (hasError) ariaDescribedByParts.push(errorId);
  if (descriptionId) ariaDescribedByParts.push(descriptionId);

  return (
    <div className={className}>
      <div className="relative">
        {/* Currency symbol prefix indicator */}
        <span
          className="pointer-events-none absolute inset-y-0 start-0 flex items-center ps-3 text-sm text-gray-500"
          aria-hidden="true"
        >
          {currencySymbol}
        </span>
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
            'block w-full rounded-md border ps-8 pe-3 py-2 text-sm shadow-sm',
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

export default CurrencyField;
