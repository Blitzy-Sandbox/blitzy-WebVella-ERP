/**
 * PhoneField — Phone number input component
 *
 * React replacement for the monolith's PcFieldPhone ViewComponent.
 * Provides a phone input with tel: clickable link display and optional
 * phone number formatting on blur.
 *
 * Edit Mode: <input type="tel"> with Tailwind styling, optional formatting on blur
 * Display Mode: <a href="tel:value"> clickable link with phone icon, or empty message
 *
 * The parent FieldRenderer component handles label rendering, access control
 * (forbidden state), error/validation display, and description text.
 * PhoneField focuses solely on the input control and display rendering.
 */

import React, { useState, useCallback } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

/* ────────────────────────────────────────────────────────────────────────── */
/*  Props Interface                                                          */
/* ────────────────────────────────────────────────────────────────────────── */

/**
 * Props for the PhoneField component.
 *
 * Extends BaseFieldProps (minus value/onChange) with phone-specific types:
 * - `value` is `string | null` (phone number string or empty)
 * - `onChange` emits a `string` (the raw or formatted phone value)
 * - `maxLength` constrains input character count
 */
export interface PhoneFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Phone number string value, or null when empty */
  value: string | null;
  /** Callback invoked when the phone value changes */
  onChange?: (value: string) => void;
  /** Maximum character length for the phone input */
  maxLength?: number | null;
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  Utility Functions                                                        */
/* ────────────────────────────────────────────────────────────────────────── */

/**
 * Strips non-digit characters from a phone string for use in a `tel:` href.
 * Preserves the leading '+' for international dialing codes.
 *
 * @example
 *   sanitizePhoneForHref('+1 (555) 123-4567') // → '+15551234567'
 *   sanitizePhoneForHref('(555) 123-4567')     // → '5551234567'
 */
function sanitizePhoneForHref(phone: string): string {
  const trimmed = phone.trim();
  if (trimmed.startsWith('+')) {
    return '+' + trimmed.slice(1).replace(/[^\d]/g, '');
  }
  return trimmed.replace(/[^\d]/g, '');
}

/**
 * Formats a raw phone number string for user-friendly display.
 *
 * Applies common formatting patterns:
 * - 10-digit numbers → (XXX) XXX-XXXX
 * - 11-digit numbers with leading 1 → +1 (XXX) XXX-XXXX
 * - All other formats → returned unchanged
 *
 * @example
 *   formatPhoneForDisplay('5551234567')  // → '(555) 123-4567'
 *   formatPhoneForDisplay('15551234567') // → '+1 (555) 123-4567'
 *   formatPhoneForDisplay('+44 20 7946') // → '+44 20 7946'
 */
function formatPhoneForDisplay(phone: string): string {
  const digits = phone.replace(/[^\d]/g, '');

  // US 10-digit: (XXX) XXX-XXXX
  if (digits.length === 10) {
    return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
  }

  // US 11-digit with leading 1: +1 (XXX) XXX-XXXX
  if (digits.length === 11 && digits.startsWith('1')) {
    return `+1 (${digits.slice(1, 4)}) ${digits.slice(4, 7)}-${digits.slice(7)}`;
  }

  // Return the original string for international or non-standard formats
  return phone;
}

/* ────────────────────────────────────────────────────────────────────────── */
/*  PhoneField Component                                                     */
/* ────────────────────────────────────────────────────────────────────────── */

/**
 * PhoneField renders a phone number input in edit mode and a clickable
 * `tel:` link in display mode.
 *
 * Features:
 * - Edit mode: `<input type="tel">` with native phone keyboard on mobile
 * - Display mode: Clickable phone link with phone icon
 * - Optional phone formatting applied on blur
 * - Full accessibility support (aria-invalid, aria-describedby, aria-label)
 * - Tailwind CSS styling with error, disabled, and readonly state variants
 */
function PhoneField(props: PhoneFieldProps): React.JSX.Element {
  const {
    // Identity
    name,
    // Value & callbacks
    value,
    onChange,
    maxLength,
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

  /**
   * Track whether the input is currently focused.
   * Used to decide when to apply phone formatting (only on blur, never while typing).
   */
  const [isFocused, setIsFocused] = useState<boolean>(false);

  // Compute a stable field ID for accessibility linking
  const fieldId = props.fieldId ?? `field-${name}`;

  // Determine interaction constraints from access level
  const isReadOnly = access === 'readonly';
  const effectiveDisabled = disabled || isReadOnly;

  /**
   * Memoized change handler — propagates the raw phone string to the parent.
   * The parent is responsible for storing the value and passing it back as a prop.
   */
  const handleChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      onChange?.(e.target.value);
    },
    [onChange],
  );

  /**
   * Memoized focus handler — marks the field as actively being edited.
   */
  const handleFocus = useCallback(() => {
    setIsFocused(true);
  }, []);

  /**
   * Memoized blur handler — applies optional phone formatting when the user
   * leaves the field. Only formats if the value has meaningful content and
   * the formatted result differs from the current value.
   */
  const handleBlur = useCallback(() => {
    setIsFocused(false);
    const currentValue = (value ?? '').trim();
    if (currentValue.length > 0) {
      const formatted = formatPhoneForDisplay(currentValue);
      if (formatted !== value) {
        onChange?.(formatted);
      }
    }
  }, [value, onChange]);

  // Derived display values
  const displayValue = value != null && value.trim().length > 0 ? value : '';
  const hasValue = displayValue.length > 0;

  /* ──────────────────────────────────────────────────────────────────────── */
  /*  Display Mode                                                           */
  /* ──────────────────────────────────────────────────────────────────────── */

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

    // Non-empty: render a clickable tel: link with a phone icon
    const phoneHref = `tel:${sanitizePhoneForHref(displayValue)}`;
    const formattedDisplay = formatPhoneForDisplay(displayValue);

    return (
      <span
        className={`inline-flex items-center gap-1.5${className ? ` ${className}` : ''}`}
        data-field-name={name}
      >
        {/* Phone icon — inline SVG with currentColor for theme compatibility */}
        <svg
          className="h-4 w-4 shrink-0 text-gray-400"
          viewBox="0 0 20 20"
          fill="currentColor"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            d="M2 3.5A1.5 1.5 0 013.5 2h1.148a1.5 1.5 0 011.465 1.175l.716 3.223a1.5 1.5 0 01-1.052 1.767l-.933.267c-.41.117-.643.555-.48.95a11.542 11.542 0 006.254 6.254c.395.163.833-.07.95-.48l.267-.933a1.5 1.5 0 011.767-1.052l3.223.716A1.5 1.5 0 0118 15.352V16.5a1.5 1.5 0 01-1.5 1.5H15c-1.149 0-2.263-.15-3.326-.43A13.022 13.022 0 012.43 8.326 13.019 13.019 0 012 5V3.5z"
            clipRule="evenodd"
          />
        </svg>
        <a
          href={phoneHref}
          className="text-sm text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          aria-label={`Call ${formattedDisplay}`}
        >
          {formattedDisplay}
        </a>
      </span>
    );
  }

  /* ──────────────────────────────────────────────────────────────────────── */
  /*  Edit Mode                                                              */
  /* ──────────────────────────────────────────────────────────────────────── */

  // Assemble dynamic CSS classes for the tel input element
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

  return (
    <input
      type="tel"
      id={fieldId}
      name={name}
      value={value ?? ''}
      onChange={handleChange}
      onFocus={handleFocus}
      onBlur={handleBlur}
      placeholder={placeholder}
      disabled={effectiveDisabled}
      readOnly={isReadOnly}
      required={required}
      maxLength={maxLength != null ? maxLength : undefined}
      className={resolvedClassName}
      aria-invalid={Boolean(error)}
      aria-describedby={error ? `${name}-error` : undefined}
      autoComplete="tel"
    />
  );
}

export default PhoneField;
