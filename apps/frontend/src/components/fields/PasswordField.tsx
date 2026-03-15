/**
 * PasswordField.tsx — Password input with visibility toggle.
 *
 * React replacement for the monolith's PcFieldPassword ViewComponent.
 * Renders a password input in edit mode with an eye/eye-off toggle button,
 * and masked dots in display mode (never reveals the actual password value).
 *
 * @module apps/frontend/src/components/fields/PasswordField
 */

import React, { useState, useCallback } from 'react';
import type { BaseFieldProps } from './FieldRenderer';

// ---------------------------------------------------------------------------
// Props Interface
// ---------------------------------------------------------------------------

/**
 * Props for the PasswordField component.
 *
 * Extends shared BaseFieldProps (with password-specific overrides for `value`
 * and `onChange`) and adds `minLength` / `maxLength` validation constraints
 * derived from the monolith's PcFieldBaseOptions.Min / Max.
 */
export interface PasswordFieldProps extends Omit<BaseFieldProps, 'value' | 'onChange'> {
  /** Current password string value. Null when no password is set. */
  value: string | null;

  /** Callback fired when the password value changes in edit mode. */
  onChange?: (value: string) => void;

  /** Minimum required character count for the password. */
  minLength?: number | null;

  /** Maximum allowed character count for the password. */
  maxLength?: number | null;
}

// ---------------------------------------------------------------------------
// SVG Icon Sub-Components
// ---------------------------------------------------------------------------

/**
 * Eye icon — rendered when the password text is masked (click to reveal).
 * Uses Heroicons v2 outline "eye" paths with stroke="currentColor" so
 * the icon inherits the parent text colour.
 */
function EyeIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      fill="none"
      viewBox="0 0 24 24"
      strokeWidth={1.5}
      stroke="currentColor"
      className="h-5 w-5"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M2.036 12.322a1.012 1.012 0 0 1 0-.639C3.423 7.51 7.36 4.5 12 4.5c4.64 0 8.577 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.64 0-8.577-3.007-9.963-7.178Z"
      />
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"
      />
    </svg>
  );
}

/**
 * Eye-slash icon — rendered when the password text is visible (click to mask).
 * Uses Heroicons v2 outline "eye-slash" paths.
 */
function EyeSlashIcon(): React.JSX.Element {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      fill="none"
      viewBox="0 0 24 24"
      strokeWidth={1.5}
      stroke="currentColor"
      className="h-5 w-5"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        d="M3.98 8.223A10.477 10.477 0 0 0 1.934 12C3.226 16.338 7.244 19.5 12 19.5c.993 0 1.953-.138 2.863-.395M6.228 6.228A10.451 10.451 0 0 1 12 4.5c4.756 0 8.773 3.162 10.065 7.498a10.522 10.522 0 0 1-4.293 5.774M6.228 6.228 3 3m3.228 3.228 3.65 3.65m7.894 7.894L21 21m-3.228-3.228-3.65-3.65m0 0a3 3 0 1 0-4.243-4.243m4.242 4.242L9.88 9.88"
      />
    </svg>
  );
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Masked dots displayed instead of the actual password in display mode. */
const MASKED_PASSWORD_DISPLAY = '\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022';

// ---------------------------------------------------------------------------
// Component
// ---------------------------------------------------------------------------

/**
 * PasswordField — password input with visibility toggle.
 *
 * **Edit mode** renders an `<input>` whose type toggles between `"password"`
 * and `"text"` via an inline eye-icon button.  Supports `minLength` /
 * `maxLength` HTML validation attributes.
 *
 * **Display mode** renders masked dots (`••••••••`).  The actual password
 * value is **never** rendered to the DOM in display mode.
 *
 * Defensive visibility (`isVisible`) and access-level (`access`) checks are
 * included even though FieldRenderer also performs them, as a safety net.
 */
function PasswordField(props: PasswordFieldProps): React.JSX.Element | null {
  // Destructure all BaseFieldProps members_accessed plus password-specific props
  const {
    name,
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
    emptyValueMessage = 'no data',
    accessDeniedMessage = 'access denied',
    locale,
    fieldId,
    value,
    onChange,
    minLength,
    maxLength,
  } = props;

  // ---- State: password visibility toggle ----
  const [showPassword, setShowPassword] = useState<boolean>(false);

  // ---- Memoised change handler ----
  const handleChange = useCallback(
    (event: React.ChangeEvent<HTMLInputElement>) => {
      onChange?.(event.target.value);
    },
    [onChange],
  );

  // ---- Memoised toggle handler ----
  const handleToggleVisibility = useCallback(() => {
    setShowPassword((prev) => !prev);
  }, []);

  // ---- Defensive visibility check (FieldRenderer also checks) ----
  if (!isVisible) {
    return null;
  }

  // ---- Defensive access check (FieldRenderer also checks) ----
  if (access === 'forbidden') {
    return (
      <span className="text-sm italic text-gray-500">
        {accessDeniedMessage}
      </span>
    );
  }

  // ---- Derived state ----
  const isReadOnly = access === 'readonly';
  const effectiveDisabled = disabled || isReadOnly;
  const controlId = fieldId ?? `field-${name}`;
  const hasError = Boolean(error);

  // ---- Display mode (or readonly): masked dots — never expose password ----
  if (mode === 'display' || isReadOnly) {
    const hasValue = value !== null && value !== undefined && value.length > 0;

    return (
      <span
        className={['text-sm text-gray-900', className]
          .filter(Boolean)
          .join(' ')}
        data-field-name={name}
        data-label-mode={labelMode}
        {...(locale ? { lang: locale } : {})}
      >
        {hasValue ? MASKED_PASSWORD_DISPLAY : emptyValueMessage}
      </span>
    );
  }

  // ---- Edit mode: password input + inline visibility toggle ----

  // Resolve nullable length constraints to actual HTML attribute values
  const effectiveMinLength = minLength != null ? minLength : undefined;
  const effectiveMaxLength = maxLength != null ? maxLength : undefined;

  // Build aria-describedby from FieldRenderer-rendered companion elements
  const ariaDescribedByParts: string[] = [];
  if (hasError) {
    ariaDescribedByParts.push(`${controlId}-error`);
  }
  if (description) {
    ariaDescribedByParts.push(`${controlId}-description`);
  }
  const ariaDescribedBy =
    ariaDescribedByParts.length > 0
      ? ariaDescribedByParts.join(' ')
      : undefined;

  return (
    <div
      className={['relative', className].filter(Boolean).join(' ')}
      data-field-name={name}
      data-label-mode={labelMode}
      {...(locale ? { lang: locale } : {})}
    >
      <input
        type={showPassword ? 'text' : 'password'}
        id={controlId}
        name={name}
        value={value ?? ''}
        onChange={handleChange}
        placeholder={placeholder}
        disabled={effectiveDisabled}
        required={required}
        minLength={effectiveMinLength}
        maxLength={effectiveMaxLength}
        autoComplete="current-password"
        aria-invalid={hasError}
        aria-describedby={ariaDescribedBy}
        aria-label={label ?? name}
        className={[
          'block w-full rounded-md border px-3 py-2 pe-10 text-sm shadow-sm',
          'focus:outline-none focus:ring-1',
          hasError
            ? 'border-red-500 focus:border-red-500 focus:ring-red-500'
            : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500',
          effectiveDisabled
            ? 'bg-gray-100 text-gray-500 cursor-not-allowed'
            : '',
        ]
          .filter(Boolean)
          .join(' ')}
      />

      {/* Visibility toggle button — absolute-positioned inside the input */}
      <button
        type="button"
        onClick={handleToggleVisibility}
        disabled={effectiveDisabled}
        className={[
          'absolute inset-y-0 end-0 flex items-center pe-3',
          'text-gray-400',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:rounded-sm',
          effectiveDisabled
            ? 'cursor-not-allowed opacity-50'
            : 'cursor-pointer hover:text-gray-600',
        ].join(' ')}
        aria-label={showPassword ? 'Hide password' : 'Show password'}
        aria-pressed={showPassword}
        tabIndex={effectiveDisabled ? -1 : 0}
      >
        {showPassword ? <EyeSlashIcon /> : <EyeIcon />}
      </button>
    </div>
  );
}

export default PasswordField;
