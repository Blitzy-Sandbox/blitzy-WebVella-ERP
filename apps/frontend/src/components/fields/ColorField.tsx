import React, { useState, useCallback } from 'react';
import { ClipboardIcon, CheckIcon } from '../common/ClipboardIcons';
import type { BaseFieldProps } from './FieldRenderer';

/**
 * Props for the ColorField component.
 * Extends BaseFieldProps with color-specific value/onChange types.
 */
export interface ColorFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** CSS color string in hex format (e.g., "#ff0000"), or null for empty */
  value: string | null;
  /** Callback fired when the color value changes; receives a valid hex color string */
  onChange?: (value: string) => void;
}

/** Default fallback color for the native color picker when no value is set */
const DEFAULT_COLOR = '#000000';

/** Regex matching a valid 6-digit hex color with leading # */
const HEX_COLOR_REGEX = /^#[0-9A-Fa-f]{6}$/;

/**
 * Normalizes a color value string into a valid lowercase 6-digit hex color.
 * Returns DEFAULT_COLOR when the input is null or not a valid hex color.
 */
function normalizeColorValue(val: string | null): string {
  if (!val) return DEFAULT_COLOR;
  if (HEX_COLOR_REGEX.test(val)) return val.toLowerCase();
  return DEFAULT_COLOR;
}

/**
 * Clipboard copy SVG icon — small inline icon used for the copy-to-clipboard button.
 * Uses fill="currentColor" so it inherits text color from parent.
 */


/**
 * ColorField — React color picker field component.
 *
 * Replaces the monolith's PcFieldColor ViewComponent. Provides a native
 * browser color picker input alongside a hex text input in edit mode, and
 * a color swatch with hex value text in display mode.
 *
 * The FieldRenderer parent handles label rendering, visibility gating,
 * forbidden-access messaging, and description text. This component focuses
 * on the inner field content for both edit and display modes.
 */
const ColorField: React.FC<ColorFieldProps> = ({
  /* ── Identity ──────────────────────────────────────────── */
  name,

  /* ── Label (used for aria attributes) ──────────────────── */
  label,
  labelMode,

  /* ── Mode and access ───────────────────────────────────── */
  mode = 'edit',
  access = 'full',

  /* ── Validation ────────────────────────────────────────── */
  required = false,
  disabled = false,
  error,

  /* ── Appearance ────────────────────────────────────────── */
  className,
  placeholder,
  description,
  isVisible = true,

  /* ── Messages ──────────────────────────────────────────── */
  emptyValueMessage = 'no data',
  accessDeniedMessage = 'access denied',

  /* ── Locale ────────────────────────────────────────────── */
  locale,

  /* ── Color-specific ────────────────────────────────────── */
  value,
  onChange,
}) => {
  /*
   * Internal state: keeps the text input in sync with the color picker.
   * We store the raw text the user types; only valid 6-digit hex values
   * propagate to the parent via onChange.
   */
  const [textInputValue, setTextInputValue] = useState<string>(
    value ? normalizeColorValue(value) : ''
  );

  /*
   * Track the previous external value to detect prop changes without
   * useEffect. When the parent pushes a new value, we reset the local
   * text input state to match. This is the recommended React pattern
   * for syncing derived state from props.
   */
  const [prevValue, setPrevValue] = useState<string | null>(value);
  if (value !== prevValue) {
    setPrevValue(value);
    setTextInputValue(value ? normalizeColorValue(value) : '');
  }

  /** Feedback state for the copy-to-clipboard action */
  const [copied, setCopied] = useState(false);

  /* ── Derived values ──────────────────────────────────────── */
  const isReadonly = access === 'readonly' || disabled;
  const effectiveColorValue = normalizeColorValue(value);
  const ariaLabel = label || name;
  const hasError = !!error;

  /* ── Event handlers (memoized) ───────────────────────────── */

  /** Handles change on the native <input type="color"> picker */
  const handleColorPickerChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const newColor = e.target.value.toLowerCase();
      setTextInputValue(newColor);
      onChange?.(newColor);
    },
    [onChange]
  );

  /** Handles change on the hex text <input type="text"> */
  const handleTextInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      let raw = e.target.value.trim();
      /* Auto-prepend # when the user omits it */
      if (raw.length > 0 && !raw.startsWith('#')) {
        raw = `#${raw}`;
      }
      setTextInputValue(raw);
      /* Propagate only valid 6-digit hex colors to the parent */
      if (HEX_COLOR_REGEX.test(raw)) {
        onChange?.(raw.toLowerCase());
      }
    },
    [onChange]
  );

  /** Copies the current hex value to the clipboard with visual feedback */
  const handleCopyToClipboard = useCallback(() => {
    const colorToCopy = value || effectiveColorValue;
    if (colorToCopy && typeof navigator !== 'undefined' && navigator.clipboard) {
      navigator.clipboard.writeText(colorToCopy).then(() => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      });
    }
  }, [value, effectiveColorValue]);

  /* ── Visibility guard (defensive — FieldRenderer also handles this) ── */
  if (!isVisible) {
    return null;
  }

  /* ── Access-denied guard (defensive — FieldRenderer also handles this) */
  if (access === 'forbidden') {
    return (
      <span className="text-sm italic text-gray-500">{accessDeniedMessage}</span>
    );
  }

  /* ═══════════════════════════════════════════════════════════
     DISPLAY MODE — color swatch + hex text + optional copy
     ═══════════════════════════════════════════════════════════ */
  if (mode === 'display') {
    if (!value) {
      return (
        <span className="text-sm italic text-gray-500">{emptyValueMessage}</span>
      );
    }

    return (
      <div
        className={`flex items-center gap-2${className ? ` ${className}` : ''}`}
        data-field-name={name}
        data-label-mode={labelMode}
        data-locale={locale}
      >
        {/* Color swatch */}
        <span
          className="inline-block h-6 w-6 shrink-0 rounded border border-gray-300"
          style={{ backgroundColor: value }}
          role="img"
          aria-label={`Color swatch: ${value}`}
        />

        {/* Hex value text */}
        <span className="text-sm font-mono text-gray-900">{value}</span>

        {/* Copy-to-clipboard button */}
        <button
          type="button"
          onClick={handleCopyToClipboard}
          className="inline-flex items-center justify-center rounded p-1 text-gray-400 hover:text-gray-600 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          aria-label={`Copy color value ${value}`}
          title={copied ? 'Copied!' : 'Copy to clipboard'}
        >
          {copied ? <CheckIcon /> : <ClipboardIcon />}
        </button>

        {/* aria-live region for copy feedback (screen readers) */}
        {copied && (
          <span className="sr-only" role="status" aria-live="polite">
            Copied to clipboard
          </span>
        )}
      </div>
    );
  }

  /* ═══════════════════════════════════════════════════════════
     EDIT MODE — native color picker + hex text input
     ═══════════════════════════════════════════════════════════ */

  /* Build conditional Tailwind classes for the text input */
  const textInputClasses = [
    'flex-1 rounded-md border px-3 py-2 text-sm font-mono shadow-sm',
    'focus:outline-none focus:ring-1',
    hasError
      ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
      : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500',
    isReadonly ? 'bg-gray-50 opacity-60 cursor-not-allowed' : 'bg-white',
  ].join(' ');

  /* Build conditional Tailwind classes for the color picker */
  const colorPickerClasses = [
    'h-10 w-10 shrink-0 cursor-pointer rounded border border-gray-300 p-0.5',
    isReadonly ? 'opacity-50 cursor-not-allowed' : '',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <div
      className={`flex items-center gap-2${className ? ` ${className}` : ''}`}
      data-field-name={name}
      data-label-mode={labelMode}
      data-locale={locale}
    >
      {/* Native color picker */}
      <input
        type="color"
        id={`${name}-color-picker`}
        name={name}
        value={effectiveColorValue}
        onChange={handleColorPickerChange}
        disabled={isReadonly}
        required={required}
        aria-label={ariaLabel}
        className={colorPickerClasses}
      />

      {/* Hex text input */}
      <input
        type="text"
        id={`${name}-hex-input`}
        value={textInputValue}
        onChange={handleTextInputChange}
        disabled={isReadonly}
        required={required}
        placeholder={placeholder || '#000000'}
        maxLength={7}
        aria-label={`${ariaLabel} hex value`}
        aria-describedby={description ? `${name}-description` : undefined}
        aria-invalid={hasError ? true : undefined}
        className={textInputClasses}
      />
    </div>
  );
};

export default ColorField;
