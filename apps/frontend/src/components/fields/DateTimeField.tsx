/**
 * DateTimeField — Date-Time Picker Component
 *
 * React replacement for the monolith's PcFieldDateTime ViewComponent.
 * Provides a date-time picker with:
 * - Edit mode: native <input type="datetime-local"> with UTC ↔ local conversion
 * - Display mode: localized Intl.DateTimeFormat output
 * - useCurrentTimeAsDefault: auto-populate with current time when value is null
 * - Full accessibility support with aria attributes
 */

import React, { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

/* ────────────────────────────────────────────────────────────────
   Props Interface
   ──────────────────────────────────────────────────────────────── */

/**
 * Props for the DateTimeField component.
 * Extends BaseFieldProps but overrides value/onChange with datetime-specific types.
 */
export interface DateTimeFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** ISO 8601 datetime string (e.g. "2024-03-15T14:30:00Z") or null */
  value: string | null;
  /** Callback invoked with an ISO 8601 string or null when the user changes the value */
  onChange?: (value: string | null) => void;
  /** When true and value is null, auto-populate with the current date-time on first render */
  useCurrentTimeAsDefault?: boolean;
}

/* ────────────────────────────────────────────────────────────────
   Utility Helpers
   ──────────────────────────────────────────────────────────────── */

/**
 * Convert an ISO 8601 UTC datetime string to the "YYYY-MM-DDTHH:mm" format
 * expected by <input type="datetime-local">. The native input operates in the
 * user's local timezone, so we convert from UTC to local.
 *
 * @param isoString - ISO 8601 datetime string (e.g. "2024-03-15T14:30:00Z")
 * @returns Local datetime string in "YYYY-MM-DDTHH:mm" format, or empty string
 */
function isoToDatetimeLocal(isoString: string | null): string {
  if (!isoString) {
    return '';
  }

  try {
    const date = new Date(isoString);
    if (Number.isNaN(date.getTime())) {
      return '';
    }

    // Build local YYYY-MM-DDTHH:mm by extracting local parts
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    const hours = String(date.getHours()).padStart(2, '0');
    const minutes = String(date.getMinutes()).padStart(2, '0');

    return `${year}-${month}-${day}T${hours}:${minutes}`;
  } catch {
    return '';
  }
}

/**
 * Convert a "YYYY-MM-DDTHH:mm" datetime-local string (local timezone)
 * to an ISO 8601 UTC datetime string for storage.
 *
 * @param localString - Local datetime string from <input type="datetime-local">
 * @returns ISO 8601 UTC string (e.g. "2024-03-15T14:30:00.000Z"), or null
 */
function datetimeLocalToIso(localString: string): string | null {
  if (!localString) {
    return null;
  }

  try {
    // The Date constructor interprets "YYYY-MM-DDTHH:mm" as local time
    const date = new Date(localString);
    if (Number.isNaN(date.getTime())) {
      return null;
    }
    return date.toISOString();
  } catch {
    return null;
  }
}

/**
 * Generate the current local datetime as an ISO 8601 UTC string.
 */
function currentIsoDatetime(): string {
  return new Date().toISOString();
}

/* ────────────────────────────────────────────────────────────────
   Component
   ──────────────────────────────────────────────────────────────── */

/**
 * DateTimeField renders a date-time picker in edit mode and a formatted
 * localized datetime string in display mode.
 */
function DateTimeField(props: DateTimeFieldProps): React.JSX.Element | null {
  const {
    name,
    fieldId,
    value,
    onChange,
    mode = 'display',
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
    useCurrentTimeAsDefault = false,
    label,
    labelMode,
    labelHelpText,
    labelWarningText,
    labelErrorText,
    entityName,
    recordId,
    apiUrl,
  } = props;

  /* --- Visibility guard --- */
  if (!isVisible) {
    return null;
  }

  /* --- Access-denied guard (forbidden access) --- */
  if (access === 'forbidden') {
    return (
      <span
        role="alert"
        data-field-name={name}
        data-field-mode={mode}
        className="text-sm text-red-600"
      >
        {accessDeniedMessage}
      </span>
    );
  }

  /* --- useCurrentTimeAsDefault initialisation --- */
  const [defaultApplied, setDefaultApplied] = useState(false);

  // Apply default current time once when value is null and useCurrentTimeAsDefault is true
  if (useCurrentTimeAsDefault && value === null && !defaultApplied && onChange) {
    // Use a microtask to avoid calling onChange during render
    // We set defaultApplied immediately to prevent re-entrancy
    setDefaultApplied(true);
    // Schedule the onChange call after the current render cycle
    Promise.resolve().then(() => {
      onChange(currentIsoDatetime());
    });
  }

  /* --- Effective states --- */
  const isReadonly = access === 'readonly';
  const effectiveDisabled = disabled || isReadonly;
  const fieldElementId = fieldId ?? `field-${name}`;
  const errorId = `${name}-error`;
  const descriptionId = description ? `${name}-description` : undefined;

  /* --- Memoised: datetime-local input value (edit mode) --- */
  const inputValue = useMemo(() => isoToDatetimeLocal(value), [value]);

  /* --- Memoised: localised display string (display mode) --- */
  const displayString = useMemo((): string => {
    if (!value) {
      return '';
    }

    try {
      const date = new Date(value);
      if (Number.isNaN(date.getTime())) {
        return value; // fallback: show raw string if unparseable
      }

      const resolvedLocale = locale || undefined; // undefined = browser default
      const formatter = new Intl.DateTimeFormat(resolvedLocale, {
        dateStyle: 'medium',
        timeStyle: 'short',
      });
      return formatter.format(date);
    } catch {
      return value; // fallback on Intl errors
    }
  }, [value, locale]);

  /* --- onChange handler (edit mode) --- */
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      if (!onChange) {
        return;
      }
      const raw = event.target.value;
      if (!raw) {
        onChange(null);
        return;
      }
      const iso = datetimeLocalToIso(raw);
      onChange(iso);
    },
    [onChange],
  );

  /* --- Aria described-by assembly --- */
  const ariaDescribedBy = useMemo(() => {
    const ids: string[] = [];
    if (error) ids.push(errorId);
    if (descriptionId) ids.push(descriptionId);
    return ids.length > 0 ? ids.join(' ') : undefined;
  }, [error, errorId, descriptionId]);

  /* ─────────────── DISPLAY MODE ─────────────── */
  if (mode === 'display') {
    if (!value) {
      return (
        <span
          className="text-sm text-gray-500 italic"
          data-field-name={name}
          data-field-mode="display"
        >
          {emptyValueMessage}
        </span>
      );
    }

    return (
      <time
        dateTime={value}
        className="text-sm text-gray-900"
        data-field-name={name}
        data-field-mode="display"
      >
        {displayString}
      </time>
    );
  }

  /* ─────────────── EDIT MODE ─────────────── */
  return (
    <input
      type="datetime-local"
      id={fieldElementId}
      name={name}
      value={inputValue}
      onChange={handleChange}
      placeholder={placeholder}
      disabled={effectiveDisabled}
      required={required}
      className={[
        'block w-full rounded-md border px-3 py-2 text-sm shadow-sm',
        'focus:outline-none focus:ring-1',
        error
          ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
          : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500',
        effectiveDisabled ? 'bg-gray-50 text-gray-500 cursor-not-allowed' : 'bg-white',
        className ?? '',
      ]
        .filter(Boolean)
        .join(' ')
        .trim()}
      aria-invalid={error ? true : undefined}
      aria-describedby={ariaDescribedBy}
      aria-required={required || undefined}
      data-field-name={name}
      data-field-mode="edit"
    />
  );
}

export default DateTimeField;
