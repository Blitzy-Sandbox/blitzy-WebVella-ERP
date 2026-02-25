/**
 * TimeField — Time-Only Picker Field Component
 *
 * React replacement for the monolith's PcFieldTime ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldTime/PcFieldTime.cs).
 *
 * Provides a native time input in edit mode leveraging `<input type="time">`
 * for cross-browser time selection, and a localized time display in display
 * mode using `Intl.DateTimeFormat`.
 *
 * Edit Mode:
 *   - `<input type="time">` with native browser time picker
 *   - Supports both "HH:mm" (default) and "HH:mm:ss" (step=1) formats
 *   - Tailwind CSS styling with error, disabled, and readonly state variants
 *   - Full accessibility support (aria-invalid, aria-describedby, aria-required)
 *
 * Display Mode:
 *   - Formats time for localized display via `Intl.DateTimeFormat`
 *   - Respects the locale prop for culture-appropriate rendering
 *   - Shows configurable empty message when value is null or blank
 *
 * Value Format:
 *   - Accepts and emits "HH:mm" or "HH:mm:ss" time strings
 *   - Automatically detects seconds presence and adjusts the input step
 *   - Emits null when the input is cleared
 *
 * The parent FieldRenderer component handles label rendering, access control
 * (forbidden state), error/validation display, and description text.
 * TimeField focuses solely on the time input control and display rendering.
 *
 * @module components/fields/TimeField
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the TimeField component.
 *
 * Extends BaseFieldProps (minus value/onChange) with time-specific types:
 * - `value` is `string | null` in "HH:mm" or "HH:mm:ss" format
 * - `onChange` emits `string | null` — null when the field is cleared
 */
export interface TimeFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Time string value in "HH:mm" or "HH:mm:ss" format, or null when empty */
  value: string | null;
  /** Callback invoked when the time value changes. Receives null on clear. */
  onChange?: (value: string | null) => void;
}

// ---------------------------------------------------------------------------
// Internal Helpers
// ---------------------------------------------------------------------------

/**
 * Regex pattern matching "HH:mm:ss" format — used to detect whether the
 * value includes a seconds component.
 */
const HH_MM_SS_PATTERN = /^\d{2}:\d{2}:\d{2}$/;

/**
 * Regex pattern matching "HH:mm" format — used to validate basic time strings.
 */
const HH_MM_PATTERN = /^\d{2}:\d{2}$/;

/**
 * Parses a time string ("HH:mm" or "HH:mm:ss") into numeric parts.
 * Returns null if the string is not a valid time format.
 */
function parseTimeParts(
  timeStr: string,
): { hours: number; minutes: number; seconds: number } | null {
  if (!timeStr) {
    return null;
  }

  const trimmed = timeStr.trim();

  if (HH_MM_SS_PATTERN.test(trimmed)) {
    const [h, m, s] = trimmed.split(':').map(Number);
    if (h >= 0 && h <= 23 && m >= 0 && m <= 59 && s >= 0 && s <= 59) {
      return { hours: h, minutes: m, seconds: s };
    }
    return null;
  }

  if (HH_MM_PATTERN.test(trimmed)) {
    const [h, m] = trimmed.split(':').map(Number);
    if (h >= 0 && h <= 23 && m >= 0 && m <= 59) {
      return { hours: h, minutes: m, seconds: 0 };
    }
    return null;
  }

  return null;
}

/**
 * Determines whether the value uses HH:mm:ss format (includes seconds).
 */
function hasSeconds(value: string | null): boolean {
  if (!value) {
    return false;
  }
  return HH_MM_SS_PATTERN.test(value.trim());
}

// ---------------------------------------------------------------------------
// TimeField Component
// ---------------------------------------------------------------------------

/**
 * TimeField renders a native time picker in edit mode and a localized
 * time string in display mode.
 *
 * Features:
 * - Edit mode: `<input type="time">` with native browser time picker
 * - Display mode: Localized time display via `Intl.DateTimeFormat`
 * - Automatic seconds detection: "HH:mm" vs "HH:mm:ss" format support
 * - Emits null when the field is cleared
 * - Full keyboard accessibility and ARIA attributes
 * - Tailwind CSS styling with error, disabled, and readonly state variants
 *
 * @param props — TimeFieldProps
 * @returns The rendered time field element
 */
function TimeField(props: TimeFieldProps): React.JSX.Element {
  const {
    // Identity
    name,
    // Value & callbacks
    value,
    onChange,
    // Mode & access (defaults aligned with FieldRenderer)
    mode = 'edit',
    access = 'full',
    // Validation
    required = false,
    disabled = false,
    error,
    // Appearance
    className,
    placeholder,
    description,
    isVisible = true,
    // Messages
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    // Locale
    locale,
    // Label props (received from FieldRenderer, destructured for schema compliance)
    label,
    labelMode,
  } = props;

  // Compute a stable field ID for accessibility linking
  const fieldId = props.fieldId ?? `field-${name}`;

  // Track whether the original value used HH:mm:ss format so we preserve it
  const [includesSeconds] = useState<boolean>(() => hasSeconds(value));

  // Determine interaction constraints from access level
  const isReadOnly = access === 'readonly';
  const effectiveDisabled = disabled || isReadOnly;

  /**
   * Memoized change handler — propagates the time string to the parent.
   *
   * When the input is cleared (empty string), emits null to signal absence.
   * If the original value included seconds, the emitted value also includes
   * seconds (":00" appended) to maintain format consistency.
   */
  const handleChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const raw = e.target.value;

      if (raw === '') {
        onChange?.(null);
        return;
      }

      // The native time input emits "HH:mm" or "HH:mm:ss" depending on step
      // If the original value had seconds and the input only emits HH:mm,
      // we append ":00" to maintain format consistency.
      if (includesSeconds && HH_MM_PATTERN.test(raw)) {
        onChange?.(`${raw}:00`);
      } else {
        onChange?.(raw);
      }
    },
    [onChange, includesSeconds],
  );

  /**
   * Memoized localized display string for the current time value.
   *
   * Uses `Intl.DateTimeFormat` to produce a locale-aware time representation.
   * Falls back to the raw time string if formatting fails (e.g., invalid locale).
   */
  const formattedDisplayTime = useMemo(() => {
    if (value == null || value.trim().length === 0) {
      return '';
    }

    const parts = parseTimeParts(value);
    if (!parts) {
      // If parsing fails, return the raw value as a fallback
      return value;
    }

    try {
      // Create a reference Date — only the time portion matters
      const refDate = new Date(2000, 0, 1, parts.hours, parts.minutes, parts.seconds);

      const formatOptions: Intl.DateTimeFormatOptions = {
        hour: '2-digit',
        minute: '2-digit',
        ...(parts.seconds > 0 || hasSeconds(value) ? { second: '2-digit' } : {}),
      };

      const resolvedLocale = locale && locale.trim().length > 0 ? locale : undefined;
      const formatter = new Intl.DateTimeFormat(resolvedLocale, formatOptions);
      return formatter.format(refDate);
    } catch {
      // If Intl.DateTimeFormat throws (e.g., invalid locale), fall back to raw value
      return value;
    }
  }, [value, locale]);

  // Derived display values
  const displayValue = formattedDisplayTime;
  const hasValue = value != null && value.trim().length > 0;

  // --------------------------------------------------------------------------
  //  Display Mode
  // --------------------------------------------------------------------------

  if (mode === 'display') {
    // Empty state: render the empty value message in muted italic text
    if (!hasValue) {
      return (
        <span
          className={`text-sm italic text-gray-500${className ? ` ${className}` : ''}`}
          data-field-name={name}
        >
          {emptyValueMessage}
        </span>
      );
    }

    // Non-empty: render the formatted time with a clock icon
    return (
      <span
        className={`inline-flex items-center gap-1.5${className ? ` ${className}` : ''}`}
        data-field-name={name}
      >
        {/* Clock icon — inline SVG with currentColor for theme compatibility */}
        <svg
          className="h-4 w-4 shrink-0 text-gray-400"
          viewBox="0 0 20 20"
          fill="currentColor"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M10 18a8 8 0 100-16 8 8 0 000 16zm.75-13a.75.75 0 00-1.5 0v5c0 .414.336.75.75.75h4a.75.75 0 000-1.5h-3.25V5z"
            clipRule="evenodd"
          />
        </svg>
        <span className="text-sm text-gray-900">
          {displayValue}
        </span>
      </span>
    );
  }

  // --------------------------------------------------------------------------
  //  Edit Mode
  // --------------------------------------------------------------------------

  // Assemble dynamic CSS classes for the time input element
  const inputClasses: string[] = [
    'block',
    'w-full',
    'rounded-md',
    'border',
    'px-3',
    'py-2',
    'text-sm',
    'shadow-sm',
    'focus:outline-none',
    'focus:ring-1',
  ];

  // Error state → red border and ring
  if (error) {
    inputClasses.push(
      'border-red-500',
      'focus:border-red-500',
      'focus:ring-red-500',
    );
  } else {
    inputClasses.push(
      'border-gray-300',
      'focus:border-blue-500',
      'focus:ring-blue-500',
    );
  }

  // Disabled/readonly state → muted background and cursor
  if (effectiveDisabled) {
    inputClasses.push('bg-gray-50', 'cursor-not-allowed', 'text-gray-500');
  } else {
    inputClasses.push('bg-white', 'text-gray-900');
  }

  const resolvedClassName = `${inputClasses.join(' ')}${className ? ` ${className}` : ''}`;

  // Determine the step attribute: use 1 (seconds granularity) if the value
  // originally included seconds, otherwise use the default (60 = minutes only).
  const stepValue = includesSeconds ? 1 : 60;

  // Prepare the value for the native time input. The input expects "HH:mm"
  // or "HH:mm:ss". If the value is null, use empty string.
  const inputValue = value ?? '';

  return (
    <input
      type="time"
      id={fieldId}
      name={name}
      value={inputValue}
      onChange={handleChange}
      step={stepValue}
      placeholder={placeholder}
      disabled={effectiveDisabled}
      readOnly={isReadOnly}
      required={required}
      className={resolvedClassName}
      aria-invalid={Boolean(error)}
      aria-required={required}
      aria-describedby={
        error
          ? `${name}-error`
          : description
            ? `${name}-description`
            : undefined
      }
    />
  );
}

export default TimeField;
