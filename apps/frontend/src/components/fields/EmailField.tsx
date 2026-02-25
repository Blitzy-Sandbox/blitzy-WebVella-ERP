/**
 * EmailField — Email Input Field Component
 *
 * React replacement for the monolith's PcFieldEmail ViewComponent
 * (WebVella.Erp.Web/Components/PcFieldEmail/PcFieldEmail.cs).
 *
 * Provides an email input in edit mode leveraging native `type="email"`
 * browser validation, and a clickable `mailto:` link in display mode.
 *
 * Edit Mode:
 *   - `<input type="email">` with native browser email validation
 *   - `maxLength` attribute support for character limit enforcement
 *   - Tailwind CSS styling with error, disabled, and readonly state variants
 *   - Full accessibility support (aria-invalid, aria-describedby, aria-label)
 *
 * Display Mode:
 *   - Renders email as `<a href="mailto:value">` clickable link
 *   - Inline envelope icon for visual affordance
 *   - Shows configurable empty message when value is null or blank
 *
 * The parent FieldRenderer component handles label rendering, access control
 * (forbidden state), error/validation display, and description text.
 * EmailField focuses solely on the input control and display rendering.
 *
 * @module components/fields/EmailField
 */

import React, { useState, useCallback } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the EmailField component.
 *
 * Extends BaseFieldProps (minus value/onChange) with email-specific types:
 * - `value` is `string | null` (email address string or empty)
 * - `onChange` emits a `string` (the raw email value)
 * - `maxLength` constrains input character count
 */
export interface EmailFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Email address string value, or null when empty */
  value: string | null;
  /** Callback invoked when the email value changes */
  onChange?: (value: string) => void;
  /** Maximum character length for the email input. Null means no limit. */
  maxLength?: number | null;
}

// ---------------------------------------------------------------------------
// EmailField Component
// ---------------------------------------------------------------------------

/**
 * EmailField renders an email input in edit mode and a clickable
 * `mailto:` link in display mode.
 *
 * Features:
 * - Edit mode: `<input type="email">` with native browser email validation
 * - Display mode: Clickable mailto link with envelope icon
 * - maxLength enforcement via the native HTML attribute
 * - Custom validation feedback through native validity API
 * - Full keyboard accessibility and ARIA attributes
 * - Tailwind CSS styling with error, disabled, and readonly state variants
 *
 * @param props — EmailFieldProps
 * @returns The rendered email field element
 */
function EmailField(props: EmailFieldProps): React.JSX.Element {
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
   * Track native browser email validation feedback.
   * Updated on blur via the native ValidityState API to show custom
   * validation messages alongside any parent-provided error state.
   */
  const [validationMessage, setValidationMessage] = useState<string>('');

  // Compute a stable field ID for accessibility linking
  const fieldId = props.fieldId ?? `field-${name}`;

  // Determine interaction constraints from access level
  const isReadOnly = access === 'readonly';
  const effectiveDisabled = disabled || isReadOnly;

  /**
   * Memoized change handler — propagates the raw email string to the parent.
   * Clears any previous validation message on change so stale feedback
   * disappears as the user types corrections.
   */
  const handleChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      if (validationMessage) {
        setValidationMessage('');
      }
      onChange?.(e.target.value);
    },
    [onChange, validationMessage],
  );

  /**
   * Memoized blur handler — checks native email validity via the browser's
   * ValidityState API and stores any validation message in local state.
   * This provides "type-to-fix" UX where errors only appear on blur, not
   * while the user is actively typing.
   */
  const handleBlur = useCallback(
    (e: React.FocusEvent<HTMLInputElement>) => {
      const input = e.target;
      const currentValue = input.value.trim();

      // If the field is empty and not required, no validation message
      if (currentValue.length === 0 && !required) {
        setValidationMessage('');
        return;
      }

      // Leverage native browser email validation via ValidityState
      if (!input.validity.valid) {
        setValidationMessage(input.validationMessage || 'Please enter a valid email address');
      } else {
        setValidationMessage('');
      }
    },
    [required],
  );

  // Derived display values
  const displayValue = value != null && value.trim().length > 0 ? value.trim() : '';
  const hasValue = displayValue.length > 0;

  // Combine parent error with native validation message
  const effectiveError = error || validationMessage;

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

    // Non-empty: render a clickable mailto: link with an envelope icon
    return (
      <span
        className={`inline-flex items-center gap-1.5${className ? ` ${className}` : ''}`}
        data-field-name={name}
      >
        {/* Envelope icon — inline SVG with currentColor for theme compatibility */}
        <svg
          className="h-4 w-4 shrink-0 text-gray-400"
          viewBox="0 0 20 20"
          fill="currentColor"
          aria-hidden="true"
        >
          <path
            d="M3 4a2 2 0 00-2 2v1.161l8.441 4.221a1.25 1.25 0 001.118 0L19 7.161V6a2 2 0 00-2-2H3z"
          />
          <path
            d="M19 8.839l-7.77 3.885a2.75 2.75 0 01-2.46 0L1 8.839V14a2 2 0 002 2h14a2 2 0 002-2V8.839z"
          />
        </svg>
        <a
          href={`mailto:${encodeURI(displayValue)}`}
          className="text-sm text-blue-600 hover:text-blue-800 hover:underline focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-blue-500"
          aria-label={`Send email to ${displayValue}`}
        >
          {displayValue}
        </a>
      </span>
    );
  }

  // --------------------------------------------------------------------------
  //  Edit Mode
  // --------------------------------------------------------------------------

  // Assemble dynamic CSS classes for the email input element
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
  if (effectiveError) {
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
    <>
      <input
        type="email"
        id={fieldId}
        name={name}
        value={value ?? ''}
        onChange={handleChange}
        onBlur={handleBlur}
        placeholder={placeholder}
        disabled={effectiveDisabled}
        readOnly={isReadOnly}
        required={required}
        maxLength={maxLength != null ? maxLength : undefined}
        className={resolvedClassName}
        aria-invalid={Boolean(effectiveError)}
        aria-describedby={effectiveError ? `${name}-error` : description ? `${name}-description` : undefined}
        autoComplete="email"
      />
      {validationMessage && !error && (
        <p
          className="text-sm text-red-600 mt-1"
          id={`${name}-validation`}
          role="alert"
          aria-live="polite"
        >
          {validationMessage}
        </p>
      )}
    </>
  );
}

export default EmailField;
