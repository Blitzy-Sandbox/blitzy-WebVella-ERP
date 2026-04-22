/**
 * DateField — Date picker field component
 *
 * React replacement for the monolith's PcFieldDate ViewComponent.
 * Uses native `<input type="date">` for edit mode and
 * `Intl.DateTimeFormat(locale)` for localized display mode rendering.
 *
 * Value format: ISO 8601 date string "YYYY-MM-DD" or null.
 *
 * Source: WebVella.Erp.Web/Components/PcFieldDate/PcFieldDate.cs
 *         WebVella.Erp.Web/Components/PcFieldBase/PcFieldBase.cs
 */

import { useState, useCallback, useMemo } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the DateField component.
 *
 * Extends BaseFieldProps (from FieldRenderer) with date-specific value and
 * onChange types, plus an optional `useCurrentTimeAsDefault` flag that
 * pre-fills today's date when no value is provided on first render.
 */
export interface DateFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current date value as an ISO 8601 date string ("YYYY-MM-DD") or null. */
  value: string | null;

  /**
   * Callback invoked when the date value changes.
   * Receives the new ISO date string or null when the field is cleared.
   */
  onChange?: (value: string | null) => void;

  /**
   * When true and `value` is null/undefined on first render,
   * the field defaults to today's date. Once the user interacts
   * with the field (including clearing it), the default is no
   * longer re-applied. Defaults to false.
   */
  useCurrentTimeAsDefault?: boolean;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Returns today's date as an ISO 8601 date string (YYYY-MM-DD).
 * Uses the local timezone so the date matches the user's wall clock.
 */
function getTodayISODate(): string {
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
}

/**
 * Validates whether a string is a well-formed ISO date (YYYY-MM-DD)
 * and represents a real calendar date.
 */
function isValidISODate(dateStr: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(dateStr)) {
    return false;
  }
  /* Parse as local midnight to avoid timezone offset issues */
  const parsed = new Date(`${dateStr}T00:00:00`);
  if (Number.isNaN(parsed.getTime())) {
    return false;
  }
  /*
   * Verify round-trip: the Date constructor may silently overflow
   * invalid days (e.g. Feb 30 → Mar 2). Comparing back ensures the
   * original string represents a real calendar date.
   */
  const y = parsed.getFullYear();
  const m = String(parsed.getMonth() + 1).padStart(2, '0');
  const d = String(parsed.getDate()).padStart(2, '0');
  return dateStr === `${y}-${m}-${d}`;
}

/**
 * Converts a locale identifier from the underscore format used by the
 * monolith (e.g. "en_US") to the BCP 47 format required by
 * `Intl.DateTimeFormat` (e.g. "en-US").
 */
function normalizeToBcp47(locale: string): string {
  return locale.replace(/_/g, '-');
}

// ---------------------------------------------------------------------------
// DateField Component
// ---------------------------------------------------------------------------

/**
 * DateField renders a date value in two modes:
 *
 * - **Display mode** — Formats the ISO date string into a human-readable,
 *   locale-aware string via `Intl.DateTimeFormat`. Shows a configurable
 *   empty-value message when no date is set.
 *
 * - **Edit mode** — Renders a native `<input type="date">` with Tailwind
 *   styling. Accepts and emits ISO date strings ("YYYY-MM-DD").
 *
 * The component self-manages visibility (isVisible), access control
 * (full / readonly / forbidden), and error styling. The FieldRenderer
 * parent handles label rendering, description text, and field type
 * dispatch.
 */
function DateField(props: DateFieldProps): React.JSX.Element | null {
  const {
    name,
    fieldId,
    value,
    onChange,
    mode = 'edit',
    access = 'full',
    required = false,
    disabled = false,
    error,
    className,
    placeholder,
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    isVisible = true,
    locale,
    useCurrentTimeAsDefault = false,
  } = props;

  /* ── Visibility guard ── render nothing when the field is hidden */
  if (!isVisible) {
    return null;
  }

  /* ── Access guard ── forbidden access renders a deny message instead of the field */
  if (access === 'forbidden') {
    return (
      <span role="alert" className="text-sm text-red-600 italic">
        {accessDeniedMessage}
      </span>
    );
  }

  /* Derive effective disabled state: readonly access forces the input disabled */
  const isReadonly = access === 'readonly';
  const effectiveDisabled = disabled || isReadonly;

  /*
   * Track whether the default-today behaviour has been consumed.
   * Once the user interacts with the field (any onChange), we set this
   * to `true` so that clearing the field does not re-apply today's date.
   */
  const [defaultApplied, setDefaultApplied] = useState<boolean>(false);

  /*
   * Compute the effective value displayed / bound to the input.
   *
   * Priority:
   *   1. An explicit non-empty `value` prop always wins.
   *   2. When `useCurrentTimeAsDefault` is true and the user has not yet
   *      interacted, fall back to today's date.
   *   3. Otherwise null (empty).
   */
  const effectiveValue = useMemo<string | null>(() => {
    if (value !== null && value !== undefined && value !== '') {
      return value;
    }
    if (useCurrentTimeAsDefault && !defaultApplied) {
      return getTodayISODate();
    }
    return null;
  }, [value, useCurrentTimeAsDefault, defaultApplied]);

  /*
   * Localized display string for display mode.
   *
   * Uses `Intl.DateTimeFormat` with the provided locale (converted from
   * underscore to BCP 47 format). Falls back to the browser default locale
   * when none is provided. Returns `null` when there is no effective value.
   */
  const localizedDisplayValue = useMemo<string | null>(() => {
    if (!effectiveValue) {
      return null;
    }

    /* Guard against malformed date strings */
    if (!isValidISODate(effectiveValue)) {
      return effectiveValue;
    }

    try {
      /* Parse as local midnight to avoid off-by-one timezone shifts */
      const date = new Date(`${effectiveValue}T00:00:00`);
      const bcp47Locale = locale ? normalizeToBcp47(locale) : undefined;
      const formatter = new Intl.DateTimeFormat(bcp47Locale, {
        year: 'numeric',
        month: 'long',
        day: 'numeric',
      });
      return formatter.format(date);
    } catch {
      /* If Intl formatting fails (invalid locale, etc.), return the raw ISO string */
      return effectiveValue;
    }
  }, [effectiveValue, locale]);

  /*
   * Memoized change handler.
   *
   * Converts the native date input's value (YYYY-MM-DD string or empty
   * string when cleared) into the ISO string / null contract expected by
   * the parent, and marks the default as consumed so it is not re-applied.
   */
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>): void => {
      const raw = event.target.value;

      /* Mark the default as consumed after the first user interaction */
      if (!defaultApplied) {
        setDefaultApplied(true);
      }

      if (raw === '') {
        onChange?.(null);
      } else {
        onChange?.(raw);
      }
    },
    [defaultApplied, onChange],
  );

  /* Stable element id for linking aria attributes */
  const inputId = fieldId ?? `field-${name}`;

  // -----------------------------------------------------------------------
  // Display Mode
  // -----------------------------------------------------------------------

  if (mode === 'display') {
    return (
      <span
        data-field-name={name}
        className={
          `text-sm leading-relaxed ${
            localizedDisplayValue
              ? 'text-gray-900'
              : 'text-gray-400 italic'
          }${className ? ` ${className}` : ''}`
        }
      >
        {localizedDisplayValue ?? emptyValueMessage}
      </span>
    );
  }

  // -----------------------------------------------------------------------
  // Edit Mode
  // -----------------------------------------------------------------------

  /*
   * Build CSS class list for the date input.
   * Merges base Tailwind styling, error-state ring colour override,
   * disabled styling, and any caller-provided className.
   */
  const inputClasses = [
    'block',
    'w-full',
    'rounded-md',
    'border',
    error ? 'border-red-500' : 'border-gray-300',
    'px-3',
    'py-2',
    'text-sm',
    'shadow-sm',
    error
      ? 'focus-visible:border-red-500 focus-visible:ring-red-500'
      : 'focus-visible:border-blue-500 focus-visible:ring-blue-500',
    'focus-visible:outline-none',
    'focus-visible:ring-1',
    'disabled:bg-gray-100',
    'disabled:text-gray-500',
    'disabled:cursor-not-allowed',
    className ?? '',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <input
      type="date"
      id={inputId}
      name={name}
      value={effectiveValue ?? ''}
      onChange={handleChange}
      placeholder={placeholder}
      disabled={effectiveDisabled}
      required={required}
      className={inputClasses}
      aria-invalid={error ? true : undefined}
      aria-describedby={error ? `${name}-error` : undefined}
    />
  );
}

export default DateField;
